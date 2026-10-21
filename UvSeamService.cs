using System;
using System.Collections.Generic;
using Effigy;

namespace Marionette.Desktop;

public static class UvSeamService
{
	public static PolyMesh MarkSeams( PolyMesh source, IEnumerable<(int a, int b)> seamEdges )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		if ( seamEdges is null ) throw new ArgumentNullException( nameof( seamEdges ) );

		// Build set of seam edge keys.
		var seams = new HashSet<(int,int)>();
		foreach ( var (a, b) in seamEdges )
		{
			var key = a < b ? ( a, b ) : ( b, a );
			seams.Add( key );
		}

		// Split vertices that are incident to seam edges: duplicate vertex index for faces on one side.
		// Simple approach: for each seam edge, for faces containing it, reindex one endpoint.
		var outMesh = new PolyMesh();
		outMesh.Positions.AddRange( source.Positions );
		outMesh.Faces.AddRange( source.Faces );
		if ( source.Skin is not null )
		{
			var w = new SkinWeights( source.Skin.Count );
			for ( int i = 0; i < source.Skin.Count; i++ ) w[ i ] = source.Skin[ i ];
			outMesh.Skin = w;
		}
		if ( source.VertexColors is not null ) outMesh.VertexColors = (Vec4[])source.VertexColors.Clone();

		// For each seam edge, duplicate the lower-index vertex for faces where edge appears reversed.
		var edgeFaceMap = new Dictionary<(int,int), List<int>>();
		for ( int fi = 0; fi < source.Faces.Count; fi++ )
		{
			var f = source.Faces[ fi ];
			for ( int e = 0; e < f.Count; e++ )
			{
				int a = f.Indices[ e ], b = f.Indices[ ( e + 1 ) % f.Count ];
				var key = a < b ? ( a, b ) : ( b, a );
				if ( !seams.Contains( key ) ) continue;
				if ( !edgeFaceMap.TryGetValue( key, out var list ) ) { list = new List<int>(); edgeFaceMap[ key ] = list; }
				list.Add( fi );
			}
		}

		// Duplicate vertex for second face sharing seam edge.
		foreach ( var kv in edgeFaceMap )
		{
			if ( kv.Value.Count < 2 ) continue;
			// duplicate vertex kv.Key.Item1 for the second face
			int orig = kv.Key.Item1;
			int dup = outMesh.Positions.Count;
			outMesh.Positions.Add( source.Positions[ orig ] );
			if ( outMesh.Skin is not null )
			{
				while ( outMesh.Skin.Count < outMesh.Positions.Count ) outMesh.Skin.Vertices.Add( Array.Empty<BoneWeight>() );
				outMesh.Skin[ dup ] = source.Skin is not null && orig < source.Skin.Count ? source.Skin[ orig ] : Array.Empty<BoneWeight>();
			}
			if ( outMesh.VertexColors is not null )
			{
				Array.Resize( ref outMesh.VertexColors, outMesh.Positions.Count );
				outMesh.VertexColors[ dup ] = source.VertexColors is not null && orig < source.VertexColors.Length ? source.VertexColors[ orig ] : new Vec4( 1, 1, 1, 1 );
			}
			// reindex second face's occurrence of orig -> dup
			var f = outMesh.Faces[ kv.Value[ 1 ] ];
			for ( int c = 0; c < f.Indices.Length; c++ )
				if ( f.Indices[ c ] == orig ) f.Indices[ c ] = dup;
		}
		return outMesh;
	}
}
