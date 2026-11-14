using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>How a silhouette inflates. Slab keeps full-size flat caps and bulges the rim
/// outward at mid-thickness; Pillow tapers the ring to a point at both poles, so the
/// solid is a lens with no flat cap at all.</summary>
public enum LineworkInflateMode
{
	Slab,
	Pillow,
}

/// <summary>Options for <see cref="LineworkInflate.TryInflate"/>.</summary>
public sealed class LineworkInflateOptions
{
	/// <summary>Total thickness, from -Thickness/2 to +Thickness/2. Must be &gt; 0.</summary>
	public float Thickness = 2f;

	/// <summary>Slab only: extra rim scale at mid-thickness, 0 (straight walls) to 0.5
	/// (mid-ring 1.5x the silhouette). Ignored by Pillow.</summary>
	public float Bulge = 0.15f;

	/// <summary>Number of rim bands along the thickness (2 to 256). Slab builds
	/// RimSteps+1 rings; Pillow builds RimSteps-1 intermediate rings plus two apices.</summary>
	public int RimSteps = 8;

	public LineworkInflateMode Mode = LineworkInflateMode.Slab;

	public int Material = 0;
}

/// <summary>2D silhouette to 3D mesh inflation, s&amp;box Linework Lane F.
///
/// This is the gap Inflate2DFeature leaves open, not a second copy of it. Inflate2D
/// extrudes with stepped bevel / cushion / ridge sections and flat caps at a tapered
/// scale; it takes its rings on trust. This builder starts one step earlier and ends
/// one step stricter:
///
///   - the silhouette is CLEANED and VALIDATED first (duplicate and collinear points
///     dropped, degenerate / self-intersecting rings rejected, holes must sit strictly
///     inside the outer ring and clear of each other), so a messy hand-drawn outline
///     either becomes a buildable ring or fails with a reason, never a corrupt mesh;
///   - the rim is SMOOTH, not stepped: Slab scales the ring by 1 + Bulge*sin(pi*t)
///     along the thickness, so the caps stay full-size and clean while the middle
///     puffs out; Pillow scales by sin(pi*t) to apices, a true lens;
///   - every ring in every section is scaled about the OUTER centroid by the SAME
///     factor, which is what keeps the result free of self-intersection on simple
///     shapes: a positive scale of a simple ring stays simple, and applying one
///     affine map to outer and holes together preserves hole containment, which
///     scaling holes about their own centroids (Inflate2D's choice) does not
///     guarantee. A constant-distance offset would be the wrong tool here — it
///     self-intersects at concave corners; centroid scaling cannot.
///   - the built mesh is PROVED closed before it is returned: every directed edge
///     must appear exactly once, with its reverse present exactly once.
///
/// What this deliberately does NOT do: a screened-Poisson elevation field over the
/// interior (the roadmap's full Monster-Mash height field), per-point depth control,
/// or UV projection beyond planar bounds UVs. Caps are ear-clipped with holes by
/// Triangulate.WithHoles, the same triangulator Inflate2D uses.</summary>
public static class LineworkInflate
{
	/// <summary>Inflates one silhouette profile into a closed mesh. False + error when
	/// the silhouette or the options are not buildable; never a partial mesh.</summary>
	public static bool TryInflate( LineworkProfile profile, LineworkInflateOptions options,
		out PolyMesh mesh, out string error )
	{
		mesh = null;
		error = null;
		options ??= new LineworkInflateOptions();
		if ( !float.IsFinite( options.Thickness ) || options.Thickness <= 0f )
		{
			error = $"thickness must be > 0 (got {options.Thickness}).";
			return false;
		}
		if ( !float.IsFinite( options.Bulge ) || options.Bulge < 0f || options.Bulge > 0.5f )
		{
			error = $"bulge must be 0 to 0.5 (got {options.Bulge}).";
			return false;
		}
		if ( options.RimSteps < 2 || options.RimSteps > 256 )
		{
			error = $"rim steps must be 2 to 256 (got {options.RimSteps}).";
			return false;
		}
		if ( options.Material < 0 )
		{
			error = $"material must be >= 0 (got {options.Material}).";
			return false;
		}
		if ( !TryClean( profile, out var cleaned, out error ) )
			return false;

		var outer = Normalized( cleaned.Outer, wantCCW: true );
		var holes = cleaned.Holes.Select( h => Normalized( h, wantCCW: false ) ).ToList();
		var centroid = Centroid( outer );

		var minX = outer.Min( q => q.x );
		var maxX = outer.Max( q => q.x );
		var minY = outer.Min( q => q.y );
		var maxY = outer.Max( q => q.y );
		var spanX = MathF.Max( 1e-6f, maxX - minX );
		var spanY = MathF.Max( 1e-6f, maxY - minY );
		Vec2 Uv( Vec2 q ) => new( (q.x - minX) / spanX, (q.y - minY) / spanY );

		var positions = new List<Vec3>();
		var faces = new List<Face>();
		var uvs = new List<Vec2>();
		var material = options.Material;

		void AddTri( int a, int b, int c, bool flip )
		{
			if ( flip ) (a, c) = (c, a);
			faces.Add( new Face( new[] { a, b, c },
				new[] { uvs[a], uvs[b], uvs[c] }, material ) );
		}

		void AddWalls( List<int> lo, List<int> hi, List<List<Vec2>> rings )
		{
			var offset = 0;
			foreach ( var ring in rings )
			{
				var n = ring.Count;
				for ( var i = 0; i < n; i++ )
				{
					var a = offset + i;
					var b = offset + (i + 1) % n;
					AddTri( lo[a], lo[b], hi[b], flip: false );
					AddTri( lo[a], hi[b], hi[a], flip: false );
				}
				offset += n;
			}
		}

		List<int> AddRingBlock( List<Vec2> ring, float z )
		{
			var block = new List<int>();
			foreach ( var q in ring )
			{
				positions.Add( new Vec3( q.x, q.y, z ) );
				uvs.Add( Uv( q ) );
				block.Add( positions.Count - 1 );
			}
			return block;
		}

		// One section block per ring set, in the concatenated layout
		// Triangulate.WithHoles indexes into: outer first, then each hole in order.
		List<int> AddSection( List<List<Vec2>> rings, float z )
		{
			var block = new List<int>();
			foreach ( var ring in rings )
				block.AddRange( AddRingBlock( ring, z ) );
			return block;
		}

		if ( options.Mode == LineworkInflateMode.Slab )
		{
			var t = options.Thickness;
			var blocks = new List<List<int>>();
			var blockRings = new List<List<List<Vec2>>>();
			for ( var i = 0; i <= options.RimSteps; i++ )
			{
				var f = (float)i / options.RimSteps;
				var z = -t / 2f + t * f;
				var scale = i == 0 || i == options.RimSteps
					? 1f
					: 1f + options.Bulge * MathF.Sin( MathF.PI * f );
				var rings = new List<List<Vec2>> { ScaledRing( outer, centroid, scale ) };
				rings.AddRange( holes.Select( h => ScaledRing( h, centroid, scale ) ) );
				blocks.Add( AddSection( rings, z ) );
				blockRings.Add( rings );
			}

			// Clean caps: WithHoles triangles are wound for the +Z face; the back cap flips.
			for ( var cap = 0; cap < 2; cap++ )
			{
				var si = cap == 0 ? 0 : options.RimSteps;
				var rings = blockRings[si];
				var tris = Triangulate.WithHoles( rings[0], rings.Skip( 1 ).ToList() );
				if ( tris is null || tris.Count == 0 )
				{
					error = "silhouette cap could not be triangulated (a hole could not be bridged into its outer ring).";
					return false;
				}
				var block = blocks[si];
				foreach ( var (a, b, c) in tris )
					AddTri( block[a], block[b], block[c], flip: cap == 0 );
			}

			for ( var si = 0; si < options.RimSteps; si++ )
				AddWalls( blocks[si], blocks[si + 1], blockRings[si] );
		}
		else if ( options.Mode == LineworkInflateMode.Pillow )
		{
			if ( holes.Count > 0 )
			{
				error = "Pillow cannot inflate a holed silhouette: apex fans cannot cap a through-hole. Use Slab for holed profiles.";
				return false;
			}
			var t = options.Thickness;
			positions.Add( new Vec3( centroid.x, centroid.y, -t / 2f ) );
			uvs.Add( Uv( centroid ) );
			var bottomApex = 0;
			var blocks = new List<List<int>>();
			var blockRings = new List<List<Vec2>>();
			for ( var i = 1; i < options.RimSteps; i++ )
			{
				var f = (float)i / options.RimSteps;
				var z = -t / 2f + t * f;
				var scale = MathF.Sin( MathF.PI * f );
				var ring = ScaledRing( outer, centroid, scale );
				blocks.Add( AddRingBlock( ring, z ) );
				blockRings.Add( ring );
			}
			positions.Add( new Vec3( centroid.x, centroid.y, t / 2f ) );
			uvs.Add( Uv( centroid ) );
			var topApex = positions.Count - 1;

			var first = blocks[0];
			for ( var i = 0; i < first.Count; i++ )
			{
				var a = first[i];
				var b = first[(i + 1) % first.Count];
				AddTri( bottomApex, b, a, flip: false );
			}
			for ( var si = 0; si + 1 < blocks.Count; si++ )
				AddWalls( blocks[si], blocks[si + 1], new List<List<Vec2>> { blockRings[si] } );
			var last = blocks[^1];
			for ( var i = 0; i < last.Count; i++ )
			{
				var a = last[i];
				var b = last[(i + 1) % last.Count];
				AddTri( topApex, a, b, flip: false );
			}
		}
		else
		{
			error = $"unknown inflate mode {options.Mode}.";
			return false;
		}

		mesh = new PolyMesh( positions, faces );
		if ( !IsClosed( mesh, out var closedError ) )
		{
			error = $"internal: built mesh is not watertight ({closedError}).";
			mesh = null;
			return false;
		}
		return true;
	}

	/// <summary>Cleans and validates one silhouette without building anything. The
	/// cleaned profile drops duplicate / collinear points and closing-point repeats,
	/// and keeps the input winding; the builder normalizes winding itself.</summary>
	public static bool TryClean( LineworkProfile profile, out LineworkProfile cleaned, out string error )
	{
		cleaned = null;
		error = null;
		if ( profile is null )
		{
			error = "no silhouette profile was given.";
			return false;
		}
		if ( !CleanRingStrict( profile.Outer, "outer", out var outer, out error ) )
			return false;
		var holes = new List<List<Vec2>>();
		for ( var i = 0; i < profile.Holes.Count; i++ )
		{
			if ( !CleanRingStrict( profile.Holes[i], $"hole {i}", out var hole, out error ) )
				return false;
			holes.Add( hole );
		}
		for ( var i = 0; i < holes.Count; i++ )
		{
			foreach ( var p in holes[i] )
			{
				if ( !PointStrictlyInside( p, outer ) )
				{
					error = $"hole {i} is not fully inside the outer ring: a vertex touches or leaves it.";
					return false;
				}
			}
			for ( var j = i + 1; j < holes.Count; j++ )
			{
				if ( RingsCross( holes[i], holes[j] ) || holes[j].Any( p => PointStrictlyInside( p, holes[i] ) )
					|| holes[i].Any( p => PointStrictlyInside( p, holes[j] ) ) )
				{
					error = $"holes {i} and {j} cross, touch, or nest inside each other.";
					return false;
				}
			}
		}
		cleaned = new LineworkProfile
		{
			Layer = profile.Layer,
			StrokeId = profile.StrokeId,
			Outer = outer,
			Holes = holes,
		};
		return true;
	}

	/// <summary>Watertight proof for a built mesh: every face index in range, no
	/// repeated corner in a face, all positions finite, and every directed edge used
	/// exactly once with its reverse used exactly once. False + reason otherwise.</summary>
	public static bool IsClosed( PolyMesh mesh, out string error )
	{
		error = null;
		if ( mesh is null )
		{
			error = "mesh is null.";
			return false;
		}
		foreach ( var p in mesh.Positions )
		{
			if ( !float.IsFinite( p.x ) || !float.IsFinite( p.y ) || !float.IsFinite( p.z ) )
			{
				error = "mesh has a non-finite position.";
				return false;
			}
		}
		var directed = new Dictionary<long, int>();
		foreach ( var f in mesh.Faces )
		{
			if ( f is null || f.Indices is null || f.Indices.Length < 3 )
			{
				error = "mesh has a face with fewer than 3 corners.";
				return false;
			}
			var seen = new HashSet<int>();
			foreach ( var idx in f.Indices )
			{
				if ( idx < 0 || idx >= mesh.Positions.Count )
				{
					error = $"face references vertex {idx} outside 0..{mesh.Positions.Count - 1}.";
					return false;
				}
				if ( !seen.Add( idx ) )
				{
					error = $"face repeats vertex {idx}.";
					return false;
				}
			}
			for ( var k = 0; k < f.Indices.Length; k++ )
			{
				var a = f.Indices[k];
				var b = f.Indices[(k + 1) % f.Indices.Length];
				var key = ((long)a << 32) | (uint)b;
				directed.TryGetValue( key, out var n );
				directed[key] = n + 1;
			}
		}
		foreach ( var (key, n) in directed )
		{
			if ( n != 1 )
			{
				error = $"edge {(int)(key >> 32)}->{(int)key} is used {n} times, not once.";
				return false;
			}
			var reverse = ((long)(int)key << 32) | (uint)(int)(key >> 32);
			if ( !directed.ContainsKey( reverse ) )
			{
				error = $"edge {(int)(key >> 32)}->{(int)key} has no opposite face.";
				return false;
			}
		}
		return true;
	}

	static Vec2 Centroid( List<Vec2> ring )
	{
		var c = Vec2.Zero;
		foreach ( var p in ring ) c += p;
		return c / MathF.Max( 1, ring.Count );
	}

	static List<Vec2> ScaledRing( List<Vec2> ring, Vec2 centroid, float scale )
	{
		if ( MathF.Abs( scale - 1f ) < 1e-6f ) return new List<Vec2>( ring );
		return ring.Select( p => centroid + (p - centroid) * scale ).ToList();
	}

	static List<Vec2> Normalized( List<Vec2> ring, bool wantCCW )
	{
		var r = new List<Vec2>( ring );
		if ( (LineworkReader.SignedArea( r ) > 0f) != wantCCW ) r.Reverse();
		return r;
	}

	static bool CleanRingStrict( List<Vec2> ring, string name, out List<Vec2> cleaned, out string error )
	{
		cleaned = new List<Vec2>();
		error = null;
		if ( ring is null || !LineworkReader.CleanRing( ring, out var work ) )
		{
			error = $"{name} ring has fewer than 3 distinct points, so it encloses nothing.";
			return false;
		}
		// Drop points that lie exactly on the straight line between their neighbours
		// (same direction only — a point that doubles back is a defect, not a no-op).
		var changed = true;
		while ( changed && work.Count > 3 )
		{
			changed = false;
			for ( var i = 0; i < work.Count; i++ )
			{
				var a = work[(i - 1 + work.Count) % work.Count];
				var b = work[i];
				var c = work[(i + 1) % work.Count];
				var ab = b - a;
				var bc = c - b;
				if ( ab.LengthSquared < 1e-12f || bc.LengthSquared < 1e-12f )
				{
					error = $"{name} ring has a zero-length edge.";
					return false;
				}
				var cross = Vec2.Cross( ab, bc );
				if ( MathF.Abs( cross ) > 1e-6f * ab.Length * bc.Length )
					continue;
				if ( Vec2.Dot( ab, bc ) <= 0f )
				{
					error = $"{name} ring doubles back on itself.";
					return false;
				}
				work.RemoveAt( i );
				changed = true;
				break;
			}
		}
		if ( work.Count < 3 )
		{
			error = $"{name} ring has fewer than 3 points after cleaning.";
			return false;
		}
		if ( MathF.Abs( LineworkReader.SignedArea( work ) ) <= 1e-12f )
		{
			error = $"{name} ring encloses no area.";
			return false;
		}
		if ( RingSelfIntersects( work ) )
		{
			error = $"{name} ring self-intersects (a bowtie or a ring that touches itself).";
			return false;
		}
		cleaned = work;
		return true;
	}

	static bool RingSelfIntersects( List<Vec2> ring )
	{
		var n = ring.Count;
		for ( var i = 0; i < n; i++ )
		{
			var a = ring[i];
			var b = ring[(i + 1) % n];
			for ( var j = i + 1; j < n; j++ )
			{
				// Adjacent edges share a vertex by construction; so do the first/last pair.
				if ( j == (i + 1) % n || (i == 0 && j == n - 1) )
					continue;
				if ( SegmentsCross( a, b, ring[j], ring[(j + 1) % n] ) )
					return true;
			}
		}
		return false;
	}

	static bool RingsCross( List<Vec2> a, List<Vec2> b )
	{
		for ( var i = 0; i < a.Count; i++ )
			for ( var j = 0; j < b.Count; j++ )
				if ( SegmentsCross( a[i], a[(i + 1) % a.Count], b[j], b[(j + 1) % b.Count] ) )
					return true;
		return false;
	}

	static float Orient( Vec2 a, Vec2 b, Vec2 c ) => Vec2.Cross( b - a, c - a );

	static bool OnSegment( Vec2 a, Vec2 b, Vec2 p )
	{
		var ab = b - a;
		var len = ab.Length;
		if ( len < 1e-12f ) return false;
		if ( MathF.Abs( Vec2.Cross( ab, p - a ) ) > 1e-6f * len )
			return false;
		return p.x >= MathF.Min( a.x, b.x ) - 1e-6f && p.x <= MathF.Max( a.x, b.x ) + 1e-6f
			&& p.y >= MathF.Min( a.y, b.y ) - 1e-6f && p.y <= MathF.Max( a.y, b.y ) + 1e-6f;
	}

	static bool SegmentsCross( Vec2 a, Vec2 b, Vec2 c, Vec2 d )
	{
		const float eps = 1e-9f;
		var o1 = Orient( a, b, c );
		var o2 = Orient( a, b, d );
		var o3 = Orient( c, d, a );
		var o4 = Orient( c, d, b );
		if ( ((o1 > eps && o2 < -eps) || (o1 < -eps && o2 > eps))
			&& ((o3 > eps && o4 < -eps) || (o3 < -eps && o4 > eps)) )
			return true;
		if ( MathF.Abs( o1 ) <= eps && OnSegment( a, b, c ) ) return true;
		if ( MathF.Abs( o2 ) <= eps && OnSegment( a, b, d ) ) return true;
		if ( MathF.Abs( o3 ) <= eps && OnSegment( c, d, a ) ) return true;
		if ( MathF.Abs( o4 ) <= eps && OnSegment( c, d, b ) ) return true;
		return false;
	}

	static bool PointStrictlyInside( Vec2 p, List<Vec2> ring )
	{
		var inside = false;
		for ( var i = 0; i < ring.Count; i++ )
		{
			var a = ring[i];
			var b = ring[(i + 1) % ring.Count];
			if ( OnSegment( a, b, p ) )
				return false;
			if ( (a.y > p.y) != (b.y > p.y) )
			{
				var xAtY = (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x;
				if ( p.x < xAtY )
					inside = !inside;
			}
		}
		return inside;
	}
}
