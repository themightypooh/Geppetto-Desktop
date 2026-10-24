using System;
using System.Collections.Generic;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Cross-field (4-RoSy) foundation for future field-aligned quad remeshing.
/// Provides per-vertex 4-RoSy tangent frames initialized along principal curvature /
/// creases, a 4-angle smoother, and an alignment metric. ExtractQuads is still a
/// placeholder: tracing streamlines into quad patches is not implemented here — the
/// kernel's QuadRemesh does the actual extraction.
/// </summary>
public static class CrossFieldService
{
	public sealed class Field
	{
		public Vec3[] Tangent { get; }
		public Vec3[] Bitangent { get; }
		public Vec3[] Normal { get; }
		public int[] Rotation { get; } // 0..3 (quarter-turn index)
		public Field( int count )
		{
			Tangent = new Vec3[ count ];
			Bitangent = new Vec3[ count ];
			Normal = new Vec3[ count ];
			Rotation = new int[ count ];
		}
	}

	/// <summary>
	/// Build the field: per-vertex normals, then a tangent per vertex from the 4-angle
	/// average of its incident edge directions, weighted toward high-dihedral edges
	/// (creases) and seam edges. That makes the field follow principal curvature
	/// instead of an arbitrary up vector: on a cylinder the directions run round it
	/// and along it, not diagonally across it. Directions differing by a quarter turn
	/// reinforce in the average instead of cancelling, which is the 4-RoSy rule.
	/// </summary>
	public static Field Compute( PolyMesh mesh )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		int n = mesh.Positions.Count;
		var field = new Field( n );

		// Per-vertex normal via area-weighted face normals.
		var normals = new Vec3[ n ];
		for ( int fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			var f = mesh.Faces[ fi ];
			if ( f.Count < 3 ) continue;
			var a = mesh.Positions[ f.Indices[ 0 ] ];
			var b = mesh.Positions[ f.Indices[ 1 ] ];
			var c = mesh.Positions[ f.Indices[ 2 ] ];
			var fn = Vec3.Cross( b - a, c - a );
			for ( int c2 = 0; c2 < f.Count; c2++ )
				normals[ f.Indices[ c2 ] ] += fn;
		}
		var adj = Adjacency( mesh, n );
		for ( int i = 0; i < n; i++ )
		{
			float l = normals[ i ].Length;
			field.Normal[ i ] = l > 1e-9f ? normals[ i ] / l : new Vec3( 0, 0, 1 );
			var normal = field.Normal[ i ];
			var up = Math.Abs( normal.y ) < 0.9f ? new Vec3( 0, 1, 0 ) : new Vec3( 1, 0, 0 );
			var t0 = Vec3.Cross( up, normal );
			float tl = t0.Length;
			t0 = tl > 1e-9f ? t0 / tl : new Vec3( 1, 0, 0 );
			var b0 = Vec3.Cross( normal, t0 );
			double cx = 0, cy = 0;
			foreach ( int j in adj[ i ] )
			{
				var d = mesh.Positions[ j ] - mesh.Positions[ i ];
				d -= normal * Vec3.Dot( d, normal );
				if ( d.LengthSquared < 1e-18f ) continue;
				d = d / d.Length;
				var theta = (float)Math.Atan2( Vec3.Dot( d, b0 ), Vec3.Dot( d, t0 ) );
				float w = 1f;
				var e = new EdgeKey( i, j );
				if ( mesh.Seams is not null && mesh.Seams.Contains( e ) ) w = 8f;
				else
				{
					float nl = normals[ j ].Length;
					var nj = nl > 1e-9f ? normals[ j ] / nl : normal;
					w = 1f + 3f * (1f - Math.Max( -1f, Math.Min( 1f, Vec3.Dot( normal, nj ) ) ));
				}
				cx += w * Math.Cos( 4 * theta );
				cy += w * Math.Sin( 4 * theta );
			}
			float phi = (cx == 0 && cy == 0) ? 0f : (float)(0.25 * Math.Atan2( cy, cx ));
			var t = t0 * (float)Math.Cos( phi ) + b0 * (float)Math.Sin( phi );
			field.Tangent[ i ] = t.LengthSquared > 1e-9f ? t / t.Length : t0;
			field.Bitangent[ i ] = Vec3.Cross( normal, field.Tangent[ i ] );
			field.Rotation[ i ] = 0;
		}
		return field;
	}

	static List<int>[] Adjacency( PolyMesh mesh, int n )
	{
		var adj = new List<int>[ n ];
		for ( int i = 0; i < n; i++ ) adj[ i ] = new List<int>();
		foreach ( var f in mesh.Faces )
			for ( int e = 0; e < f.Count; e++ )
			{
				int a = f.Indices[ e ], b = f.Indices[ ( e + 1 ) % f.Count ];
				if ( a < 0 || a >= n || b < 0 || b >= n || a == b ) continue;
				if ( !adj[ a ].Contains( b ) ) adj[ a ].Add( b );
				if ( !adj[ b ].Contains( a ) ) adj[ b ].Add( a );
			}
		return adj;
	}

	/// <summary>Mean 4-RoSy neighbour agreement of a field, 0..1: 1 when every neighbouring
	/// pair of directions is parallel or a quarter turn apart, 0 when they sit at 45
	/// degrees. The same metric the kernel's QuadRemesh cross-field reports.</summary>
	public static float Alignment( PolyMesh mesh, Field field )
	{
		if ( mesh is null || field is null ) throw new ArgumentNullException();
		var adj = Adjacency( mesh, mesh.Positions.Count );
		double sum = 0; int count = 0;
		for ( int i = 0; i < adj.Length; i++ )
			foreach ( int j in adj[ i ] )
			{
				if ( j <= i ) continue;
				float d = Math.Abs( Vec3.Dot( field.Tangent[ i ], field.Tangent[ j ] ) );
				sum += Math.Abs( 2 * d * d - 1 );
				count++;
			}
		return count == 0 ? 1f : (float)(sum / count);
	}

	/// <summary>
	/// Smooth the field 4-RoSy style: each vertex's new direction is the 4-angle average
	/// of its neighbours' directions, projected into its own tangent plane. Averaging
	/// raw tangents (the old version) cancels directions a quarter turn apart, which is
	/// exactly the disagreement a cross-field is allowed to have; the 4-angle average
	/// keeps them. Rotation indices are left at 0 — the direction itself carries the
	/// quarter-turn information.
	/// </summary>
	public static void Smooth( PolyMesh mesh, Field field, int iterations = 3 )
	{
		if ( mesh is null || field is null ) throw new ArgumentNullException();
		int n = mesh.Positions.Count;
		var adj = Adjacency( mesh, n );
		for ( int it = 0; it < iterations; it++ )
		{
			var newT = new Vec3[ n ];
			for ( int i = 0; i < n; i++ )
			{
				var normal = field.Normal[ i ];
				var up = Math.Abs( normal.y ) < 0.9f ? new Vec3( 0, 1, 0 ) : new Vec3( 1, 0, 0 );
				var t0 = Vec3.Cross( up, normal );
				t0 = t0.LengthSquared > 1e-9f ? t0 / t0.Length : new Vec3( 1, 0, 0 );
				var b0 = Vec3.Cross( normal, t0 );
				double cx = 0, cy = 0;
				// Own direction counts once, so an isolated vertex keeps its direction.
				{
					var th = (float)Math.Atan2( Vec3.Dot( field.Tangent[ i ], b0 ), Vec3.Dot( field.Tangent[ i ], t0 ) );
					cx += Math.Cos( 4 * th ); cy += Math.Sin( 4 * th );
				}
				foreach ( int j in adj[ i ] )
				{
					var d = field.Tangent[ j ] - normal * Vec3.Dot( field.Tangent[ j ], normal );
					if ( d.LengthSquared < 1e-18f ) continue;
					d = d / d.Length;
					var th = (float)Math.Atan2( Vec3.Dot( d, b0 ), Vec3.Dot( d, t0 ) );
					cx += Math.Cos( 4 * th ); cy += Math.Sin( 4 * th );
				}
				float phi = (cx == 0 && cy == 0) ? 0f : (float)(0.25 * Math.Atan2( cy, cx ));
				var t = t0 * (float)Math.Cos( phi ) + b0 * (float)Math.Sin( phi );
				newT[ i ] = t.LengthSquared > 1e-9f ? t / t.Length : field.Tangent[ i ];
			}
			for ( int i = 0; i < n; i++ )
			{
				field.Tangent[ i ] = newT[ i ];
				field.Bitangent[ i ] = Vec3.Cross( field.Normal[ i ], field.Tangent[ i ] );
			}
		}
	}

	public static List<int[]> ExtractQuads( PolyMesh mesh, Field field )
	{
		// Placeholder: real extraction traces streamlines and builds quad patches.
		return new List<int[]>();
	}

	public static float[] VertexCurvature( PolyMesh mesh )
	{
		// Returns discrete Gaussian curvature (angle defect) per vertex, divided by a
		// simple one-third area estimate. Positive on convex regions, negative on saddles.
		int n = mesh.Positions.Count;
		var curv = new float[ n ];
		var angleSum = new float[ n ];
		var area = new float[ n ];
		if ( mesh is null ) return curv;
		for ( int fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			var f = mesh.Faces[ fi ];
			if ( f.Count < 3 ) continue;
			// triangulate fan from corner 0
			for ( int ti = 1; ti + 1 < f.Count; ti++ )
			{
				var ia = f.Indices[ 0 ]; var ib = f.Indices[ ti ]; var ic = f.Indices[ ti + 1 ];
				if ( ia >= n || ib >= n || ic >= n ) continue;
				var a = mesh.Positions[ ia ]; var b = mesh.Positions[ ib ]; var c = mesh.Positions[ ic ];
				var ab = b - a; var ac = c - a; var bc = c - b;
				float lab = ab.Length, lac = ac.Length, lbc = bc.Length;
				if ( lab < 1e-12f || lac < 1e-12f || lbc < 1e-12f ) continue;
				float triArea = Vec3.Cross( ab, ac ).Length * 0.5f;
				// angles
				float angA = (float)Math.Acos( Math.Clamp( Vec3.Dot( ab, ac ) / ( lab * lac ), -1f, 1f ) );
				float angB = (float)Math.Acos( Math.Clamp( Vec3.Dot( ( a - b ), ( c - b ) ) / ( lab * lbc ), -1f, 1f ) );
				float angC = (float)Math.PI - angA - angB;
				angleSum[ ia ] += angA;
				angleSum[ ib ] += angB;
				angleSum[ ic ] += angC;
				area[ ia ] += triArea / 3f;
				area[ ib ] += triArea / 3f;
				area[ ic ] += triArea / 3f;
			}
		}
		for ( int i = 0; i < n; i++ )
		{
			float defect = (float)( 2 * Math.PI ) - angleSum[ i ];
			float a = area[ i ] > 1e-12f ? area[ i ] : 1f;
			curv[ i ] = defect / a;
		}
		return curv;
	}

	public static void OptimizeMIQ( PolyMesh mesh, Field field, int iterations = 5 )
	{
		if ( mesh is null || field is null ) throw new ArgumentNullException();
		int n = mesh.Positions.Count;
		var adj = new List<int>[ n ];
		for ( int i = 0; i < n; i++ ) adj[ i ] = new List<int>();
		foreach ( var f in mesh.Faces )
		{
			for ( int e = 0; e < f.Count; e++ )
			{
				int a = f.Indices[ e ], b = f.Indices[ ( e + 1 ) % f.Count ];
				if ( !adj[ a ].Contains( b ) ) adj[ a ].Add( b );
				if ( !adj[ b ].Contains( a ) ) adj[ b ].Add( a );
			}
		}
		for ( int it = 0; it < iterations; it++ )
		{
			for ( int i = 0; i < n; i++ )
			{
				// Try 4 rotations, pick best alignment with neighbors.
				float bestScore = float.MinValue; int bestR = field.Rotation[ i ];
				for ( int r = 0; r < 4; r++ )
				{
					// rotate tangent by r*90
					var t = field.Tangent[ i ];
					// apply rotation around normal
					float ang = r * (float)( Math.PI / 2 );
					var cos = (float)Math.Cos( ang ); var sin = (float)Math.Sin( ang );
					var rt = field.Tangent[ i ] * cos + field.Bitangent[ i ] * sin;
					float score = 0;
					foreach ( int j in adj[ i ] )
					{
						score += (float)Vec3.Dot( rt, field.Tangent[ j ] );
					}
					if ( score > bestScore ) { bestScore = score; bestR = r; }
				}
				field.Rotation[ i ] = bestR;
				float a2 = bestR * (float)( Math.PI / 2 );
				var c2 = (float)Math.Cos( a2 ); var s2 = (float)Math.Sin( a2 );
				field.Tangent[ i ] = field.Tangent[ i ] * c2 + field.Bitangent[ i ] * s2; // approximate
			}
		}
	}

	public static List<List<Vec3>> TraceStreamlines( PolyMesh mesh, Field field, int maxSteps = 200, float stepSize = 0.02f )
	{
		var paths = new List<List<Vec3>>();
		if ( mesh is null || field is null ) return paths;
		int n = mesh.Positions.Count;
		var visited = new HashSet<int>();
		// Seed from every vertex, but skip already-covered ones.
		for ( int s = 0; s < n; s++ )
		{
			if ( visited.Contains( s ) ) continue;
			var path = new List<Vec3>();
			var pos = mesh.Positions[ s ];
			for ( int step = 0; step < maxSteps; step++ )
			{
				path.Add( pos );
				// nearest vertex
				int nearest = 0; float best = float.MaxValue;
				for ( int i = 0; i < n; i++ )
				{
					float d = ( mesh.Positions[ i ] - pos ).Length;
					if ( d < best ) { best = d; nearest = i; }
				}
				if ( visited.Contains( nearest ) && step > 0 ) break;
				visited.Add( nearest );
				var dir = field.Tangent[ nearest ];
				var next = pos + dir * stepSize;
				// Stop if we leave the mesh bounding box by too far.
				if ( ( next - pos ).Length < 1e-9f ) break;
				pos = next;
			}
			if ( path.Count > 2 ) paths.Add( path );
		}
		return paths;
	}
}

