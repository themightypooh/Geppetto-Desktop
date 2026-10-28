using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>De-interleaved, batch-local copy of one material batch: what an engine adapter (Godot ArrayMesh, glTF, ...)
/// consumes without knowing the 24-float GL layout. Indices are rebased to this surface's own vertices; winding is as stored.</summary>
public sealed class CpuSurface
{
	public int BatchIndex;
	public string MaterialName;
	public int MaterialSlot;
	public Vector3[] Positions, Normals;
	public Vector2[] Uvs;
	/// <summary>xyz tangent, w = bitangent sign.</summary>
	public Vector4[] Tangents;
	/// <summary>Per-corner vertex colour (white when the mesh carries none).</summary>
	public Vector4[] Colors;
	/// <summary>4 bones per vertex (row-major, length 4*N), null when the mesh is unskinned. Bone numbers index <see cref="CpuMesh.Rig"/>.</summary>
	public int[] BoneIndices;
	/// <summary>4 weights per vertex, normalised to sum 1; null when unskinned.</summary>
	public float[] BoneWeights;
	public int[] Indices;
}

public static class VertexLayout
{
	public const int Stride = CpuMesh.FloatsPerVertex;       // 24 floats
	public const int Position = 0, Normal = 3, BoneIndices = 6, BoneWeights = 10, Uv = 14, Tangent = 16, Color = 20;
}


public static class CpuMeshSurfaces
{
	/// <summary>Split <paramref name="mesh"/> into one <see cref="CpuSurface"/> per draw batch. Reads Vertices (or BaseVertices
	/// after a GPU upload released them) and Indices; throws if both are gone.</summary>
	public static CpuSurface[] ToSurfaces( this CpuMesh mesh, bool useBaseVertices = false )
	{
		var v = (useBaseVertices ? mesh.BaseVertices : mesh.Vertices ?? mesh.BaseVertices)
			?? throw new InvalidOperationException( "CpuMesh has no vertex data (released after GPU upload?)." );
		var idx = mesh.Indices ?? throw new InvalidOperationException( "CpuMesh has no index data." );
		var result = new CpuSurface[mesh.Batches.Length];
		var remap = new int[v.Length / VertexLayout.Stride];
		for ( int b = 0; b < result.Length; b++ )
		{
			var batch = mesh.Batches[b];
			Array.Fill( remap, -1 );
			var used = new List<int>();
			var local = new int[batch.IndexCount];
			int first = batch.IndexOffset / sizeof( uint );       // IndexOffset is BYTES into the element buffer
			for ( int k = 0; k < local.Length; k++ )
			{
				int src = (int)idx[first + k];
				if ( remap[src] < 0 ) { remap[src] = used.Count; used.Add( src ); }
				local[k] = remap[src];
			}
			int n = used.Count;
			var s = new CpuSurface
			{
				BatchIndex = b, MaterialName = batch.MaterialName, MaterialSlot = batch.MaterialSlot, Indices = local,
				Positions = new Vector3[n], Normals = new Vector3[n], Uvs = new Vector2[n], Tangents = new Vector4[n], Colors = new Vector4[n],
				BoneIndices = mesh.HasSkin ? new int[n * 4] : null, BoneWeights = mesh.HasSkin ? new float[n * 4] : null,
			};
			for ( int i = 0; i < n; i++ )
			{
				int o = used[i] * VertexLayout.Stride;
				s.Positions[i] = new Vector3( v[o], v[o + 1], v[o + 2] );
				s.Normals[i] = new Vector3( v[o + 3], v[o + 4], v[o + 5] );
				s.Uvs[i] = new Vector2( v[o + 14], v[o + 15] );
				s.Tangents[i] = new Vector4( v[o + 16], v[o + 17], v[o + 18], v[o + 19] );
				s.Colors[i] = new Vector4( v[o + 20], v[o + 21], v[o + 22], v[o + 23] );
				if ( s.BoneIndices is not null )
					for ( int j = 0; j < 4; j++ ) { s.BoneIndices[i * 4 + j] = (int)v[o + 6 + j]; s.BoneWeights[i * 4 + j] = v[o + 10 + j]; }
			}
			result[b] = s;
		}
		return result;
	}
}
