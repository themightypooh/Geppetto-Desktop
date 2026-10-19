using System;
using System.Collections.Generic;
using System.Numerics;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Click-time triangle picking in the same four-weight skinning space as the GPU.
/// AABBs are a broad phase, not a substitute for the visible mesh. Buffers are allocated once.
/// </summary>
public sealed class MeshPicker
{
	sealed class Part
	{
		public Vector3[] Bind, Posed;
		public BoneWeight[][] Skin;
		public int[] Triangles;
	}
	readonly List<Part> _parts = new();

	public void Add( PolyMesh mesh )
	{
		var bind = new Vector3[mesh.VertexCount];
		var skin = mesh.Skin is null ? null : new BoneWeight[mesh.VertexCount][];
		for ( int v = 0; v < bind.Length; v++ )
		{
			var p = mesh.Positions[v];
			bind[v] = new Vector3( p.x, p.y, p.z );
			if ( skin is not null )
			{
				var weights = mesh.Skin[v];
				skin[v] = weights.Length > 4 ? SkinWeights.Prune( weights, 4 ) : (BoneWeight[])weights.Clone();
			}
		}
		var triangles = new List<int>();
		foreach ( var face in mesh.Faces )
		{
			if ( face.Count == 3 ) triangles.AddRange( face.Indices );
			else if ( face.Count > 3 )
			{
				var polygon = new List<Vec3>( face.Count );
				foreach ( int v in face.Indices ) polygon.Add( mesh.Positions[v] );
				foreach ( var (a, b, c) in Triangulate.Face( polygon ) )
				{
					triangles.Add( face.Indices[a] ); triangles.Add( face.Indices[b] ); triangles.Add( face.Indices[c] );
				}
			}
		}
		_parts.Add( new Part { Bind = bind, Posed = new Vector3[bind.Length], Skin = skin, Triangles = triangles.ToArray() } );
	}

	public (Vector3 Min, Vector3 Max) Bounds( int index, ReadOnlySpan<Matrix4x4> palette )
	{
		var part = _parts[index];
		var min = new Vector3( float.MaxValue );
		var max = new Vector3( float.MinValue );
		for ( int v = 0; v < part.Bind.Length; v++ )
		{
			var p = part.Bind[v];
			if ( part.Skin is not null && !palette.IsEmpty )
			{
				p = Vector3.Zero;
				float sum = 0;
				foreach ( var w in part.Skin[v] )
				{
					p += Vector3.Transform( part.Bind[v], palette[w.Bone] ) * w.Weight;
					sum += w.Weight;
				}
				p /= sum;
			}
			part.Posed[v] = p;
			min = Vector3.Min( min, p ); max = Vector3.Max( max, p );
		}
		return (min, max);
	}

	public int Pick( Vector3 origin, Vector3 direction, ReadOnlySpan<Matrix4x4> palette )
	{
		float nearest = float.MaxValue;
		int hit = -1;
		for ( int i = 0; i < _parts.Count; i++ )
		{
			var (min, max) = Bounds( i, palette );
			if ( !SkeletonOverlay.RayHitsAabb( origin, direction, min, max, out float enter ) || enter > nearest ) continue;
			var part = _parts[i];
			for ( int t = 0; t < part.Triangles.Length; t += 3 )
			{
				var a = part.Posed[part.Triangles[t]];
				var b = part.Posed[part.Triangles[t + 1]];
				var c = part.Posed[part.Triangles[t + 2]];
				var edge1 = b - a; var edge2 = c - a;
				var cross = Vector3.Cross( direction, edge2 );
				float det = Vector3.Dot( edge1, cross );
				if ( MathF.Abs( det ) < 1e-7f ) continue;
				float inverse = 1f / det;
				var delta = origin - a;
				float u = Vector3.Dot( delta, cross ) * inverse;
				if ( u < 0 || u > 1 ) continue;
				var q = Vector3.Cross( delta, edge1 );
				float v = Vector3.Dot( direction, q ) * inverse;
				if ( v < 0 || u + v > 1 ) continue;
				float distance = Vector3.Dot( edge2, q ) * inverse;
				if ( distance >= 0 && distance < nearest ) { nearest = distance; hit = i; }
			}
		}
		return hit;
	}
}
