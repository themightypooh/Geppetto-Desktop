using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// A heat-map atlas of one bone's influence, rasterised through the mesh's UVs.
///
/// NOT VERTEX COLOURS. Paint taught this the expensive way: resolution equal to mesh density is
/// unusable on a CAD cage (a box paints six faces), and the preview material that read a COLOR
/// stream was a workaround the atlas path existed to delete. Weight painting's *data* is still per
/// vertex — that is what the compiler skins — but the *display* is a texture, the same answer
/// colour painting landed on. Rasterising barycentric-interpolated vertex weights onto the atlas
/// is what the GPU will interpolate at deform time, so the ramp is the bind, drawn at texel
/// resolution rather than as eight coloured corners.
///
/// Nothing here writes <see cref="PolyMesh.VertexColors"/>.
/// </summary>
public static class WeightRamp
{
	/// <summary>
	/// Rasterise <paramref name="influence"/> (one value per vertex, 0..1) onto a fresh canvas.
	/// Faces without UVs are skipped; overlapping islands overwrite, which is why a box wants an
	/// unwrap before this is useful — the same door colour painting already has.
	/// </summary>
	public static PaintCanvas Bake( PolyMesh mesh, float[] influence, int resolution )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( influence is null )
			throw new ArgumentNullException( nameof( influence ) );

		if ( influence.Length != mesh.VertexCount )
			throw new ArgumentException( $"influence ({influence.Length}) and mesh ({mesh.VertexCount}) disagree" );

		if ( resolution < 1 )
			throw new ArgumentOutOfRangeException( nameof( resolution ) );

		var canvas = new PaintCanvas( resolution, resolution );

		foreach ( var face in mesh.Faces )
		{
			if ( face.Count < 3 || face.UVs is null || face.UVs.Length != face.Count )
				continue;

			var corners = new List<Vec3>( face.Count );

			for ( var c = 0; c < face.Count; c++ )
				corners.Add( mesh.Positions[face.Indices[c]] );

			foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
			{
				var u0 = face.UVs[ia];
				var u1 = face.UVs[ib];
				var u2 = face.UVs[ic];
				var w0 = influence[face.Indices[ia]];
				var w1 = influence[face.Indices[ib]];
				var w2 = influence[face.Indices[ic]];

				NormalBake.Rasterise( u0, u1, u2, resolution, resolution, ( x, y, wa, wb, wc ) =>
				{
					var t = Math.Clamp( w0 * wa + w1 * wb + w2 * wc, 0f, 1f );
					Color( t, out var r, out var g, out var b );
					canvas.Blend( x, y, r, g, b, 1f );
				} );
			}
		}

		return canvas;
	}

	/// <summary>
	/// Classic weight ramp: blue at 0, through cyan and green and yellow, to red at 1. Zero is
	/// "this bone does not own you" and has to read as cold, not as missing.
	/// </summary>
	public static void Color( float t, out byte r, out byte g, out byte b )
	{
		t = Math.Clamp( t, 0f, 1f );

		float rf, gf, bf;

		if ( t < 0.25f )
		{
			var u = t / 0.25f;
			rf = 0f;
			gf = u;
			bf = 1f;
		}
		else if ( t < 0.5f )
		{
			var u = ( t - 0.25f ) / 0.25f;
			rf = 0f;
			gf = 1f;
			bf = 1f - u;
		}
		else if ( t < 0.75f )
		{
			var u = ( t - 0.5f ) / 0.25f;
			rf = u;
			gf = 1f;
			bf = 0f;
		}
		else
		{
			var u = ( t - 0.75f ) / 0.25f;
			rf = 1f;
			gf = 1f - u;
			bf = 0f;
		}

		r = ToByte( rf );
		g = ToByte( gf );
		b = ToByte( bf );
	}

	static byte ToByte( float v ) => (byte)Math.Clamp( MathF.Round( v * 255f ), 0f, 255f );

	/// <summary>
	/// Gradient ramp (roadmap J2): set <paramref name="bone"/>'s influence by projection onto
	/// <paramref name="direction"/> from <paramref name="origin"/>, where <paramref name="from"/>
	/// and <paramref name="to"/> are distances along that direction (not 0..1). A vertex at
	/// <paramref name="from"/> gets 0, at <paramref name="to"/> gets 1, linearly between, clamped
	/// outside. The other influences rescale proportionally, so the partition of unity survives;
	/// a vertex with no other influence becomes wholly this bone when the ramp is above 0.
	/// Returns a new <see cref="SkinWeights"/>; the input is not mutated.
	/// </summary>
	public static SkinWeights ApplyLinear( PolyMesh mesh, SkinWeights weights, int bone,
		Vec3 origin, Vec3 direction, float from = 0f, float to = 1f )
	{
		RequireWeights( mesh, weights, bone );

		if ( direction.Length < 1e-6f )
			throw new ArgumentException( "A linear ramp needs a non-zero direction.", nameof( direction ) );

		if ( from == to )
			throw new ArgumentException( "A linear ramp needs from != to.", nameof( from ) );

		var dir = direction.Normal;
		var values = new float[mesh.VertexCount];

		for ( var v = 0; v < mesh.VertexCount; v++ )
			values[v] = (Vec3.Dot( mesh.Positions[v] - origin, dir ) - from) / (to - from);

		return SetInfluence( mesh, weights, bone, values );
	}

	/// <summary>
	/// Radial ramp (roadmap J2): set <paramref name="bone"/>'s influence by distance from
	/// <paramref name="center"/>. With <paramref name="feather"/> 0 the edge is hard (1 inside
	/// <paramref name="radius"/>, 0 outside); with a feather the ramp is 1 within
	/// radius - feather and falls linearly to 0 at the radius. Same rescaling and
	/// non-mutating contract as <see cref="ApplyLinear"/>.
	/// </summary>
	public static SkinWeights ApplyRadial( PolyMesh mesh, SkinWeights weights, int bone,
		Vec3 center, float radius, float feather = 0f )
	{
		RequireWeights( mesh, weights, bone );

		if ( radius <= 0f )
			throw new ArgumentOutOfRangeException( nameof( radius ), "A radial ramp needs a positive radius." );

		feather = Math.Clamp( feather, 0f, radius );

		var values = new float[mesh.VertexCount];

		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			var d = (mesh.Positions[v] - center).Length;

			if ( feather <= 0f )
				values[v] = d < radius ? 1f : 0f;
			else if ( d <= radius - feather )
				values[v] = 1f;
			else if ( d >= radius )
				values[v] = 0f;
			else
				values[v] = 1f - (d - (radius - feather)) / feather;
		}

		return SetInfluence( mesh, weights, bone, values );
	}

	static void RequireWeights( PolyMesh mesh, SkinWeights weights, int bone )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( weights is null )
			throw new ArgumentNullException( nameof( weights ) );

		if ( weights.Count != mesh.VertexCount )
			throw new ArgumentException( $"weights ({weights.Count}) and mesh ({mesh.VertexCount}) disagree" );

		if ( bone < 0 )
			throw new ArgumentOutOfRangeException( nameof( bone ), "A ramp paints a bone; -1 is nothing." );
	}

	static SkinWeights SetInfluence( PolyMesh mesh, SkinWeights weights, int bone, float[] values )
	{
		var result = new SkinWeights( mesh.VertexCount );

		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			var t = Math.Clamp( values[v], 0f, 1f );
			var others = new Dictionary<int, float>();
			var sum = 0f;

			foreach ( var w in weights[v] )
			{
				if ( w.Bone == bone )
					continue;

				others[w.Bone] = w.Weight;
				sum += w.Weight;
			}

			if ( sum <= 0f )
			{
				result[v] = t > 0f ? new[] { new BoneWeight( bone, 1f ) } : Array.Empty<BoneWeight>();
				continue;
			}

			var acc = new Dictionary<int, float>();

			if ( t > 0f )
				acc[bone] = t;

			var scale = (1f - t) / sum;

			foreach ( var (b, w) in others )
				acc[b] = w * scale;

			result[v] = SkinWeights.Finish( acc );
		}

		return result;
	}
}
