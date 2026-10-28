using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Surface data transfer, barycentric version (v4 Lane F).
///
/// The old code was an O(V_target x V_source) nearest-vertex lookup, which staircase-aliases
/// UVs and weights whenever a low-poly rig feeds a high-poly garment or a remeshed body: a
/// target point halfway between two source vertices got one of them wholesale. This version
/// builds a <see cref="MeshBVH"/> over the source once and, per target vertex, interpolates
/// over the hit triangle with barycentric weights, so midway means 50/50.
///
/// Roadmap-name note: the roadmap says "MeshBVH.ClosestPoint". No such method exists on
/// MeshBVH; its closest-point API is <see cref="MeshBVH.NearestSurface"/>, which returns the
/// closest point, face, and normal, and the barycentric weights are computed here from the
/// hit point and that face's triangle. A face, not a vertex, decides each UV, so a target
/// point on one side of a UV seam gets that side's UVs, not a blend across the seam.
///
/// Faces are cloned into the output (the old code shared Face objects with the target, so
/// its UV post-pass silently rewrote the target's own UVs).
/// </summary>
public static class DataTransferService
{
	public enum Channel { SkinWeights, UVs, VertexColors, MorphDeltas, Morphs = MorphDeltas, UV2 }

	public sealed class Options
	{
		public Channel[] Channels { get; set; } = new[] { Channel.SkinWeights, Channel.UVs };
		public float MaxDistance { get; set; } = float.MaxValue;
	}

	public sealed class Result
	{
		public PolyMesh Mesh { get; }
		public int TransferredVerts { get; }
		public int SkippedVerts { get; }
		public Result( PolyMesh m, int t, int s ) { Mesh = m; TransferredVerts = t; SkippedVerts = s; }
	}

	public static Result Transfer( PolyMesh source, PolyMesh target, Options options = null )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		if ( target is null ) throw new ArgumentNullException( nameof( target ) );
		options ??= new Options();
		var wantSkin = Array.IndexOf( options.Channels, Channel.SkinWeights ) >= 0;
		var wantUV = Array.IndexOf( options.Channels, Channel.UVs ) >= 0;
		var wantColors = Array.IndexOf( options.Channels, Channel.VertexColors ) >= 0;
		var wantMorphs = Array.IndexOf( options.Channels, Channel.MorphDeltas ) >= 0;
		var wantUV2 = Array.IndexOf( options.Channels, Channel.UV2 ) >= 0;

		var outMesh = new PolyMesh();
		outMesh.Positions.AddRange( target.Positions );
		foreach ( var f in target.Faces )
			outMesh.Faces.Add( new Face( (int[])f.Indices.Clone(), (Vec2[])f.UVs.Clone(), f.Material ) );
		// UV2 rides its own channel (wave3detail): the kernel mirror now carries UV2/FaceUV2
		// (Lane P sync landed it), so the stale "mirror has no UV2" note is gone. UV2 is
		// per-vertex on PolyMesh, so the transfer interpolates it per target vertex like
		// the other vertex channels, using the hit triangle's corner UV2s (GetUV2, which
		// is seam-correct per hit face). A target vertex shared across a source seam
		// keeps one value, the same collapse the UV channel's per-vertex intermediate has.

		if ( wantSkin && source.Skin is not null )
			outMesh.Skin = new SkinWeights( target.Positions.Count );
		else if ( target.Skin is not null )
		{
			var w = new SkinWeights( target.Skin.Count );
			for ( int i = 0; i < target.Skin.Count; i++ ) w[i] = target.Skin[i];
			outMesh.Skin = w;
		}

		Vec4[] colors = null;
		if ( wantColors && source.HasVertexColors )
			colors = new Vec4[target.Positions.Count];
		else if ( target.VertexColors is not null )
			colors = (Vec4[])target.VertexColors.Clone();

		var srcMorphs = wantMorphs ? source.ValidMorphs.ToList() : new List<MeshMorph>();
		var outMorphs = new List<MeshMorph>();
		foreach ( var m in srcMorphs )
			outMorphs.Add( new MeshMorph { Name = m.Name, Deltas = new Vec3[target.Positions.Count] } );

		var perVertUV = wantUV ? new Vec2[target.Positions.Count] : null;
		var hasUV = wantUV ? new bool[target.Positions.Count] : null;
		var perVertUV2 = wantUV2 ? new Vec2[target.Positions.Count] : null;
		var hasUV2 = wantUV2 ? new bool[target.Positions.Count] : null;

		var bvh = MeshBVH.Build( source );
		int transferred = 0, skipped = 0;
		for ( int ti = 0; ti < target.Positions.Count; ti++ )
		{
			var hit = bvh.NearestSurface( source, target.Positions[ti], options.MaxDistance );
			if ( hit is null ) { skipped++; continue; }
			if ( !TryBarycentric( source, hit.Value, out var tri, out var w ) ) { skipped++; continue; }
			var srcFace = source.Faces[tri.face];
			int v0 = srcFace.Indices[tri.c0], v1 = srcFace.Indices[tri.c1], v2 = srcFace.Indices[tri.c2];

			if ( wantSkin && outMesh.Skin is not null && source.Skin is not null )
			{
				var acc = new Dictionary<int, float>();
				Accumulate( acc, source.Skin, v0, w.w0 );
				Accumulate( acc, source.Skin, v1, w.w1 );
				Accumulate( acc, source.Skin, v2, w.w2 );
				float sum = 0f;
				foreach ( var kv in acc ) sum += kv.Value;
				var merged = new List<BoneWeight>();
				if ( sum > 1e-9f )
					foreach ( var kv in acc )
						if ( kv.Value > 1e-4f ) merged.Add( new BoneWeight( kv.Key, kv.Value / sum ) );
				merged.Sort( ( a, b ) => a.Bone.CompareTo( b.Bone ) );
				outMesh.Skin[ti] = merged.ToArray();
			}

			if ( wantUV && tri.c0 < srcFace.UVs.Length && tri.c1 < srcFace.UVs.Length && tri.c2 < srcFace.UVs.Length )
			{
				perVertUV[ti] = srcFace.UVs[tri.c0] * w.w0 + srcFace.UVs[tri.c1] * w.w1 + srcFace.UVs[tri.c2] * w.w2;
				hasUV[ti] = true;
			}

			if ( wantUV2 && source.HasUV2 )
			{
				perVertUV2[ti] = source.GetUV2( tri.face, tri.c0 ) * w.w0 + source.GetUV2( tri.face, tri.c1 ) * w.w1 + source.GetUV2( tri.face, tri.c2 ) * w.w2;
				hasUV2[ti] = true;
			}

			if ( wantColors && colors is not null && source.VertexColors is not null
				&& v0 < source.VertexColors.Length && v1 < source.VertexColors.Length && v2 < source.VertexColors.Length )
				colors[ti] = source.VertexColors[v0] * w.w0 + source.VertexColors[v1] * w.w1 + source.VertexColors[v2] * w.w2;

			if ( wantMorphs )
				for ( int mi = 0; mi < srcMorphs.Count; mi++ )
				{
					var d = srcMorphs[mi].Deltas;
					if ( v0 < d.Length && v1 < d.Length && v2 < d.Length )
						outMorphs[mi].Deltas[ti] = d[v0] * w.w0 + d[v1] * w.w1 + d[v2] * w.w2;
				}

			transferred++;
		}

		if ( wantUV && hasUV is not null )
			foreach ( var f in outMesh.Faces )
				for ( int c = 0; c < f.Count; c++ )
				{
					int v = f.Indices[c];
					if ( v < hasUV.Length && hasUV[v] && c < f.UVs.Length ) f.UVs[c] = perVertUV[v];
				}
		if ( wantUV2 && hasUV2 is not null )
		{
			var uv2 = new List<Vec2>( target.Positions.Count );
			for ( int vi = 0; vi < target.Positions.Count; vi++ )
				uv2.Add( hasUV2[vi] ? perVertUV2[vi] : ( target.HasUV2 && vi < target.UV2.Count ? target.UV2[vi] : Vec2.Zero ) );
			outMesh.UV2 = uv2;
			outMesh.FaceUV2 = null;
		}
		if ( colors is not null ) outMesh.VertexColors = colors;
		if ( outMorphs.Count > 0 ) outMesh.Morphs.AddRange( outMorphs );

		return new Result( outMesh, transferred, skipped );
	}

	static void Accumulate( Dictionary<int, float> acc, SkinWeights skin, int vertex, float weight )
	{
		if ( weight <= 0f || vertex < 0 || vertex >= skin.Count ) return;
		foreach ( var bw in skin[vertex] )
		{
			if ( bw.Weight <= 0f ) continue;
			acc[bw.Bone] = acc.TryGetValue( bw.Bone, out float cur ) ? cur + bw.Weight * weight : bw.Weight * weight;
		}
	}

	/// <summary>The hit face's fan triangle containing the hit point, and its barycentric
	/// weights (clamped to the triangle, renormalised). False when the face is degenerate
	/// or the hit names no face.</summary>
	static bool TryBarycentric( PolyMesh source, MeshHit hit, out (int face, int c0, int c1, int c2) tri, out (float w0, float w1, float w2) w )
	{
		tri = default; w = default;
		if ( hit.FaceIndex < 0 || hit.FaceIndex >= source.Faces.Count ) return false;
		var face = source.Faces[hit.FaceIndex];
		if ( face.Count < 3 ) return false;
		float bestNeg = float.MaxValue;
		for ( int i = 1; i + 1 < face.Count; i++ )
		{
			var bw = Barycentric( hit.Point,
				source.Positions[face.Indices[0]], source.Positions[face.Indices[i]], source.Positions[face.Indices[i + 1]] );
			if ( bw is null ) continue;
			var (w0, w1, w2) = bw.Value;
			float neg = -Math.Min( 0f, Math.Min( w0, Math.Min( w1, w2 ) ) );
			if ( neg < bestNeg )
			{
				bestNeg = neg;
				tri = (hit.FaceIndex, 0, i, i + 1);
				w = (w0, w1, w2);
				if ( bestNeg == 0f ) break;
			}
		}
		if ( bestNeg == float.MaxValue ) return false;
		float x = Math.Max( 0f, w.w0 ), y = Math.Max( 0f, w.w1 ), z = Math.Max( 0f, w.w2 );
		float sum = x + y + z;
		w = sum < 1e-9f ? (1f, 0f, 0f) : (x / sum, y / sum, z / sum);
		return true;
	}

	static (float, float, float)? Barycentric( Vec3 p, Vec3 a, Vec3 b, Vec3 c )
	{
		var v0 = b - a; var v1 = c - a; var v2 = p - a;
		float d00 = Vec3.Dot( v0, v0 ), d01 = Vec3.Dot( v0, v1 ), d11 = Vec3.Dot( v1, v1 );
		float d20 = Vec3.Dot( v2, v0 ), d21 = Vec3.Dot( v2, v1 );
		float denom = d00 * d11 - d01 * d01;
		if ( MathF.Abs( denom ) < 1e-12f ) return null;
		float v = ( d11 * d20 - d01 * d21 ) / denom;
		float w = ( d00 * d21 - d01 * d20 ) / denom;
		return ( 1f - v - w, v, w );
	}
}
