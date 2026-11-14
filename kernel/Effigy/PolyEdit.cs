using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// The poly-edit tools that have no home yet: the ones the edit session
/// (MeshEditSession, MeshAdjacency.cs) does not already provide. Extrude,
/// inset, loop cut, bridge, slide, knife, bevel, merge by distance and
/// loop/ring selection all live there already; this file only fills the
/// gaps, so there is exactly one implementation of each tool.
/// </summary>
public static class PolyEdit
{
	/// <summary>Collapse each listed edge to its midpoint, Blender's Edge
	/// Collapse: the two endpoints become one vertex, faces that end up with
	/// fewer than three distinct vertices are dropped, and unreferenced
	/// vertices are compacted away. Edges that share a vertex collapse
	/// together — the merged vertex is the average of the whole group — so
	/// collapsing a run of edges pinches it to one point. Returns a new mesh;
	/// the input is untouched. An edge that is not in the mesh is ignored; an
	/// empty list returns a clone.
	///
	/// Materials and UVs survive (a surviving face keeps the first corner's
	/// UV for each distinct vertex). Per-vertex side data does not: morphs,
	/// skin weights and vertex colours are sized to the old vertex count, so
	/// the result carries none of them — the same rule the modifier stack
	/// applies when it changes topology.</summary>
	public static PolyMesh CollapseEdges( PolyMesh mesh, IEnumerable<EdgeKey> edges )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( edges is null ) throw new ArgumentNullException( nameof( edges ) );

		var list = edges.Distinct().ToList();
		if ( list.Count == 0 ) return mesh.Clone();

		// Union-find over vertices: each collapsed edge merges its endpoints.
		var parent = new int[mesh.VertexCount];
		for ( var i = 0; i < parent.Length; i++ ) parent[i] = i;
		int Find( int v )
		{
			while ( parent[v] != v ) { parent[v] = parent[parent[v]]; v = parent[v]; }
			return v;
		}
		foreach ( var e in list )
		{
			if ( e.A < 0 || e.B < 0 || e.A >= mesh.VertexCount || e.B >= mesh.VertexCount ) continue;
			var ra = Find( e.A );
			var rb = Find( e.B );
			if ( ra != rb ) parent[ra] = rb;
		}

		// Average position per merged group.
		var sum = new Vec3[mesh.VertexCount];
		var count = new int[mesh.VertexCount];
		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			var r = Find( v );
			sum[r] += mesh.Positions[v];
			count[r]++;
		}

		// New index per representative, assigned only to groups a surviving face uses.
		var remap = new int[mesh.VertexCount];
		for ( var i = 0; i < remap.Length; i++ ) remap[i] = -1;
		var result = new PolyMesh();
		foreach ( var face in mesh.Faces )
		{
			var seen = new List<int>();
			var idx = new List<int>();
			var uvs = new List<Vec2>();
			for ( var k = 0; k < face.Indices.Length; k++ )
			{
				var r = Find( face.Indices[k] );
				if ( seen.Contains( r ) ) continue;
				seen.Add( r );
				if ( remap[r] < 0 )
				{
					remap[r] = result.Positions.Count;
					result.Positions.Add( sum[r] * (1f / count[r]) );
				}
				idx.Add( remap[r] );
				uvs.Add( face.UVs is not null && k < face.UVs.Length ? face.UVs[k] : default );
			}
			if ( idx.Count < 3 ) continue;
			result.Faces.Add( new Face( idx.ToArray(), uvs.ToArray(), face.Material ) );
		}
		result.InvalidateVertexCache();
		return result;
	}
}
