using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Topological symmetry (Blender's Topology Mirror): which vertex is the mirror twin of which, worked
/// out from the mesh's CONNECTIVITY rather than from where the vertices currently sit.
///
/// Positional mirroring finds a vertex's twin by reflecting its position and looking there, which
/// breaks the moment the model has been posed or sculpted lopsided — the reflected point no longer
/// lands on the twin, so the mirrored stroke goes to the wrong place or nowhere. A topology map is
/// built once, and from then on a stroke on vertex i moves vertex Map[i] by the mirrored amount,
/// however asymmetric the shape has become.
///
/// How the map is built: vertices whose reflection lands on another vertex (within a tolerance) are
/// paired first — those are the anchors. Then pairing spreads along edges: if u pairs with u', each
/// unpaired neighbour of u is paired with the unpaired neighbour of u' that best matches it, judged
/// by reflected position. That fills in the parts the position test missed (a shifted region, an
/// asymmetric edit) as long as the edge structure is mirror-symmetric. A vertex that still has no
/// twin maps to -1 and simply gets no mirrored copy.
/// </summary>
public static class TopologySymmetry
{
	/// <summary>Twin per vertex, or -1. A vertex on the mirror plane is its own twin.</summary>
	public static int[] Build( PolyMesh mesh, MirrorAxis axis, float tolerance = 0f )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var n = mesh.VertexCount;
		var map = new int[n];
		Array.Fill( map, -1 );
		if ( n == 0 || axis == MirrorAxis.None )
			return map;

		var p = mesh.Positions;
		if ( tolerance <= 0f )
		{
			var min = p[0];
			var max = p[0];
			foreach ( var v in p )
			{
				min = new Vec3( MathF.Min( min.x, v.x ), MathF.Min( min.y, v.y ), MathF.Min( min.z, v.z ) );
				max = new Vec3( MathF.Max( max.x, v.x ), MathF.Max( max.y, v.y ), MathF.Max( max.z, v.z ) );
			}

			tolerance = MathF.Max( ( max - min ).Length * 1e-4f, 1e-6f );
		}

		// 1. Anchors by reflected position, through a hash grid.
		var cell = tolerance * 2f;
		var grid = new Dictionary<(int, int, int), List<int>>();
		(int, int, int) Key( Vec3 v ) => ((int)MathF.Floor( v.x / cell ), (int)MathF.Floor( v.y / cell ), (int)MathF.Floor( v.z / cell ));
		for ( var i = 0; i < n; i++ )
		{
			var k = Key( p[i] );
			if ( !grid.TryGetValue( k, out var list ) )
				grid[k] = list = new List<int>();
			list.Add( i );
		}

		var t2 = tolerance * tolerance;
		for ( var i = 0; i < n; i++ )
		{
			if ( map[i] >= 0 )
				continue;

			var r = Brush.Mirror( p[i], axis );
			var (kx, ky, kz) = Key( r );
			int best = -1;
			var bestD = t2;
			for ( var dx = -1; dx <= 1; dx++ )
			for ( var dy = -1; dy <= 1; dy++ )
			for ( var dz = -1; dz <= 1; dz++ )
			{
				if ( !grid.TryGetValue( (kx + dx, ky + dy, kz + dz), out var list ) )
					continue;

				foreach ( var j in list )
				{
					if ( map[j] >= 0 && j != i )
						continue;

					var d = ( p[j] - r ).LengthSquared;
					if ( d <= bestD )
					{
						bestD = d;
						best = j;
					}
				}
			}

			if ( best >= 0 )
			{
				map[i] = best;
				map[best] = i;
			}
		}

		// 2. Spread along edges from the anchors.
		var edges = mesh.BuildVertexEdges();
		int Other( EdgeKey e, int v ) => e.A == v ? e.B : e.A;
		var queue = new Queue<int>();
		for ( var i = 0; i < n; i++ )
			if ( map[i] >= 0 )
				queue.Enqueue( i );

		while ( queue.Count > 0 )
		{
			var u = queue.Dequeue();
			var twin = map[u];
			if ( twin < 0 || edges[u] is null || edges[twin] is null )
				continue;

			foreach ( var eu in edges[u] )
			{
				var a = Other( eu, u );
				if ( map[a] >= 0 )
					continue;

				var r = Brush.Mirror( p[a], axis );
				int best = -1;
				var bestD = float.MaxValue;
				foreach ( var et in edges[twin] )
				{
					var b = Other( et, twin );
					if ( map[b] >= 0 && b != a )
						continue;

					var d = ( p[b] - r ).LengthSquared;
					if ( d < bestD )
					{
						bestD = d;
						best = b;
					}
				}

				if ( best < 0 )
					continue;

				map[a] = best;
				map[best] = a;
				queue.Enqueue( a );
				if ( best != a )
					queue.Enqueue( best );
			}
		}

		return map;
	}

	/// <summary>How many vertices found a twin, 0 to 1.</summary>
	public static float Coverage( int[] map )
	{
		if ( map is null || map.Length == 0 )
			return 0f;

		var paired = 0;
		foreach ( var m in map )
			if ( m >= 0 )
				paired++;

		return paired / (float)map.Length;
	}
}
