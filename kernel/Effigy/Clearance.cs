using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// How two bodies sit against each other: the smallest gap between them, or, where they run into
/// each other, how much volume they share and how deep the worst crossing goes.
///
/// WHY VOLUME AS WELL AS CROSSINGS. A button sunk wholly inside its shell crosses nothing, so a
/// crossing count calls it clear; the shared volume does not. Volume is measured by casting lines
/// along x through a grid over the two bodies' common box: each line is exactly inside a closed
/// mesh between alternate crossings, and the shared length times the cell area is the overlap.
/// Exact along x, gridded across it, so it is good to the grid's cell, not to the last decimal.
/// An open mesh has no inside; its overlap is reported as a guess.
/// </summary>
public static class Clearance
{
	public sealed class Reading
	{
		public string NameA, NameB;
		/// <summary>The closest the two skins come; zero when they cross.</summary>
		public float Gap;
		public Vec3 PointA, PointB;
		public int Crossings;
		public float Depth;
		public Vec3 Deepest;
		/// <summary>Volume inside both, cubic units.</summary>
		public float Overlap;
		public bool OpenA, OpenB;

		/// <summary>Closed bodies overlap when they share volume. Crossings alone, with none shared,
		/// are faces lying on each other (a switch flat on a board): that is touching. An open body
		/// has no volume, so its crossings are all there is to go on.</summary>
		public bool Overlaps => Overlap > 0f || (Crossings > 0 && (OpenA || OpenB));

		public string Describe( float min = 0f )
		{
			var open = OpenA || OpenB
				? $" ({string.Join( " and ", new[] { OpenA ? NameA : null, OpenB ? NameB : null }.Where( n => n is not null ) )} open: no inside, so the volume is a guess)"
				: "";

			if ( Overlaps )
			{
				var inside = Crossings == 0 ? ", one wholly inside the other" : $", {Crossings} crossing(s)";
				var deep = Depth > 0f ? $", deepest {N( Depth )} at {V( Deepest )}" : "";
				return $"{NameA} x {NameB}! overlap {N( Overlap )} u³{inside}{deep}{open}";
			}

			if ( Crossings > 0 )
				return $"{NameA} x {NameB}: touching, faces meeting ({Crossings} crossing(s) on the contact, no volume shared to the grid's cell)";

			if ( Gap < 1e-4f )
				return $"{NameA} x {NameB}: touching at {V( PointA )}{open}";

			var mark = Gap < min ? "!" : ":";
			var under = Gap < min ? $", under min={N( min )}" : "";
			return $"{NameA} x {NameB}{mark} gap {N( Gap )}{under} ({V( PointA )} to {V( PointB )}){open}";
		}

		static string N( float v ) => v.ToString( v != 0f && MathF.Abs( v ) < 0.001f ? "0.######" : "0.####", System.Globalization.CultureInfo.InvariantCulture );
		static string V( Vec3 p ) => $"{N( p.x )},{N( p.y )},{N( p.z )}";
	}

	/// <summary>One body made ready to be measured against others: its triangle tree and whether it is
	/// closed. Every pair of twenty bodies is 190 readings, and each needs these from both sides.</summary>
	public sealed class Prepared
	{
		public readonly string Name;
		public readonly PolyMesh Mesh;
		public readonly TriangleSet Triangles;
		public readonly bool Open;

		public Prepared( string name, PolyMesh mesh )
		{
			Name = name;
			Mesh = mesh;
			Triangles = TriangleSet.From( mesh );
			Open = !MeshValidator.Validate( mesh ).IsClosed;
		}
	}

	public static Reading Measure( Body a, Body b, int columns = 160 ) =>
		Measure( new Prepared( a.Name ?? a.Id, a.Mesh ), new Prepared( b.Name ?? b.Id, b.Mesh ), columns );

	public static Reading Measure( Prepared a, Prepared b, int columns = 160 )
	{
		var r = new Reading { NameA = a.Name, NameB = b.Name, OpenA = a.Open, OpenB = b.Open };
		var sa = a.Triangles;
		var sb = b.Triangles;
		var clash = MeshClash.Between( sa, sb, 0 );
		r.Crossings = clash.Crossings;
		r.Depth = clash.Depth;
		r.Deepest = clash.Deepest;
		r.Overlap = OverlapVolume( a.Mesh, b.Mesh, columns );

		var gap = MeshClash.Nearest( sa, sb );
		r.Gap = r.Overlaps ? 0f : gap.Distance;
		r.PointA = gap.PointA;
		r.PointB = gap.PointB;
		return r;
	}

	/// <summary>Volume inside both closed meshes, by lines along x through a grid over their common box.</summary>
	public static float OverlapVolume( PolyMesh a, PolyMesh b, int columns = 160 )
	{
		var (minA, maxA) = ModelMeasure.Bounds( a );
		var (minB, maxB) = ModelMeasure.Bounds( b );
		var min = new Vec3( MathF.Max( minA.x, minB.x ), MathF.Max( minA.y, minB.y ), MathF.Max( minA.z, minB.z ) );
		var max = new Vec3( MathF.Min( maxA.x, maxB.x ), MathF.Min( maxA.y, maxB.y ), MathF.Min( maxA.z, maxB.z ) );

		if ( max.x <= min.x || max.y <= min.y || max.z <= min.z )
			return 0f;

		var cell = MathF.Max( max.y - min.y, max.z - min.z ) / Math.Max( 8, columns );
		var ny = Math.Max( 1, (int)MathF.Ceiling( (max.y - min.y) / cell ) );
		var nz = Math.Max( 1, (int)MathF.Ceiling( (max.z - min.z) / cell ) );

		var hitsA = Hits( a, min, cell, ny, nz );
		var hitsB = Hits( b, min, cell, ny, nz );
		var total = 0.0;

		for ( var c = 0; c < hitsA.Length; c++ )
		{
			if ( hitsA[c] is not { Count: >= 2 } ha || hitsB[c] is not { Count: >= 2 } hb )
				continue;

			ha.Sort();
			hb.Sort();
			total += Shared( ha, hb );
		}

		return (float)(total * cell * cell);
	}

	/// <summary>Where each grid line along x crosses the mesh's skin, by column.</summary>
	static List<float>[] Hits( PolyMesh mesh, Vec3 min, float cell, int ny, int nz )
	{
		var hits = new List<float>[ny * nz];
		// Off the cell's exact centre, so a line does not run down a shared edge and count it twice.
		const float jitterY = 0.5137f, jitterZ = 0.4871f;

		foreach ( var face in mesh.Faces )
		{
			var idx = face.Indices;

			for ( var k = 1; k + 1 < idx.Length; k++ )
			{
				var p0 = mesh.Positions[idx[0]];
				var p1 = mesh.Positions[idx[k]];
				var p2 = mesh.Positions[idx[k + 1]];

				var y0 = (int)MathF.Floor( (MathF.Min( p0.y, MathF.Min( p1.y, p2.y ) ) - min.y) / cell - jitterY ) + 1;
				var y1 = (int)MathF.Floor( (MathF.Max( p0.y, MathF.Max( p1.y, p2.y ) ) - min.y) / cell - jitterY );
				var z0 = (int)MathF.Floor( (MathF.Min( p0.z, MathF.Min( p1.z, p2.z ) ) - min.z) / cell - jitterZ ) + 1;
				var z1 = (int)MathF.Floor( (MathF.Max( p0.z, MathF.Max( p1.z, p2.z ) ) - min.z) / cell - jitterZ );

				y0 = Math.Max( y0, 0 ); z0 = Math.Max( z0, 0 );
				y1 = Math.Min( y1, ny - 1 ); z1 = Math.Min( z1, nz - 1 );

				var det = (p1.y - p0.y) * (p2.z - p0.z) - (p2.y - p0.y) * (p1.z - p0.z);
				if ( MathF.Abs( det ) < 1e-12f ) continue;

				for ( var iy = y0; iy <= y1; iy++ )
				{
					var y = min.y + (iy + jitterY) * cell;

					for ( var iz = z0; iz <= z1; iz++ )
					{
						var z = min.z + (iz + jitterZ) * cell;
						var u = ((y - p0.y) * (p2.z - p0.z) - (p2.y - p0.y) * (z - p0.z)) / det;
						var v = ((p1.y - p0.y) * (z - p0.z) - (y - p0.y) * (p1.z - p0.z)) / det;

						if ( u < 0f || v < 0f || u + v > 1f )
							continue;

						var x = p0.x + u * (p1.x - p0.x) + v * (p2.x - p0.x);
						(hits[iy * nz + iz] ??= new List<float>()).Add( x );
					}
				}
			}
		}

		return hits;
	}

	/// <summary>Length inside both, where each sorted list is entry, exit, entry, exit….</summary>
	static double Shared( List<float> a, List<float> b )
	{
		double total = 0;
		int i = 0, j = 0;

		while ( i + 1 < a.Count && j + 1 < b.Count )
		{
			var lo = MathF.Max( a[i], b[j] );
			var hi = MathF.Min( a[i + 1], b[j + 1] );
			if ( hi > lo ) total += hi - lo;

			if ( a[i + 1] < b[j + 1] ) i += 2;
			else j += 2;
		}

		return total;
	}
}
