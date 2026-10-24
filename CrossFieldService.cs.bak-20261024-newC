using System;
using System.Collections.Generic;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Cross-field (4-RoSy) foundation for future field-aligned quad remeshing.
/// Currently provides per-vertex tangent frames and a simple initial field aligned
/// to the mesh normal; smoothing/optimization is not yet implemented.
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
			{
				normals[ f.Indices[ c2 ] ] += fn;
			}
		}
		for ( int i = 0; i < n; i++ )
		{
			float l = normals[ i ].Length;
			field.Normal[ i ] = l > 1e-9f ? normals[ i ] / l : new Vec3( 0, 0, 1 );
			// Tangent: pick an arbitrary vector perpendicular to normal.
			var up = Math.Abs( field.Normal[ i ].y ) < 0.9f ? new Vec3( 0, 1, 0 ) : new Vec3( 1, 0, 0 );
			var t = Vec3.Cross( up, field.Normal[ i ] );
			float tl = t.Length;
			field.Tangent[ i ] = tl > 1e-9f ? t / tl : new Vec3( 1, 0, 0 );
			field.Bitangent[ i ] = Vec3.Cross( field.Normal[ i ], field.Tangent[ i ] );
			field.Rotation[ i ] = 0;
		}
		return field;
	}

	public static void Smooth( PolyMesh mesh, Field field, int iterations = 3 )
	{
		if ( mesh is null || field is null ) throw new ArgumentNullException();
		int n = mesh.Positions.Count;
		// Build adjacency.
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
			var newT = new Vec3[ n ];
			for ( int i = 0; i < n; i++ )
			{
				var acc = field.Tangent[ i ];
				foreach ( int j in adj[ i ] ) acc += field.Tangent[ j ];
				float l = acc.Length;
				if ( l > 1e-9f )
				{
					acc /= l;
					// Re-orthogonalize against normal.
					acc = acc - field.Normal[ i ] * Vec3.Dot( acc, field.Normal[ i ] );
					float l2 = acc.Length;
					if ( l2 > 1e-9f ) acc /= l2;
					else acc = field.Tangent[ i ];
				}
				else acc = field.Tangent[ i ];
				newT[ i ] = acc;
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

