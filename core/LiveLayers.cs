using System;
using System.Collections.Generic;
using System.Numerics;
using Effigy;

namespace Marionette.Desktop;

/// <summary>R9: surface parameters for live geometry, the same terms a mesh batch feeds the lit shader.</summary>
/// <summary>Wave-2 look wiring: the SlotLook spectrum for one live layer, in shader
/// uniform terms. Defaults are the old behaviour (all zero, opacity 1, shadow tint white).</summary>
public readonly record struct LiveLook( float Toon = 0f, int ToonBands = 3, float ToonRim = 0f, float Stylized = 0f,
	float OutlineWidth = 0f, Vector3 OutlineColor = default, float Clearcoat = 0f, float ClearcoatRoughness = 0.05f,
	float Subsurface = 0f, Vector3 SubsurfaceColor = default, float Opacity = 1f,
	Vector3 ShadowTint = default, float Iridescence = 0f );

public readonly record struct LiveMaterial( Vector3 Albedo, float Roughness = .6f, float Metallic = 0f,
	Vector3 Emission = default, float Alpha = 1f, bool Blend = false, bool DoubleSided = true )
{
	public MaterialLobes Lobes { get; init; } = MaterialLobes.None;
	public LiveLook Look { get; init; }
	/// <summary>Soft round sprite in albedo alpha and emission (billboards); quads otherwise draw square.</summary>
	public bool Sprite { get; init; }
}

/// <summary>Camera basis a layer builds view-facing geometry (billboards, ribbons) against.</summary>
public readonly record struct LiveView( Vector3 Eye, Vector3 Right, Vector3 Up, Vector3 Forward );

/// <summary>Growable vertex stream in <see cref="CpuMesh"/>'s 20-float layout, unskinned.
/// Buffers grow to the high-water mark and are reused, so steady frames do not allocate.</summary>
public sealed class LiveBuilder
{
	float[] _data = new float[CpuMesh.FloatsPerVertex * 1024];
	public int FloatCount { get; private set; }
	public int VertexCount => FloatCount / CpuMesh.FloatsPerVertex;
	internal float[] Data => _data;
	/// <summary>World AABB of everything emitted since the last reset (inverted when empty).</summary>
	public Vector3 Min { get; private set; } = new( float.PositiveInfinity );
	public Vector3 Max { get; private set; } = new( float.NegativeInfinity );
	public void Reset() { FloatCount = 0; ResetBounds(); }
	public void ResetBounds() { Min = new Vector3( float.PositiveInfinity ); Max = new Vector3( float.NegativeInfinity ); }

	public void Vertex( Vector3 p, Vector3 n, Vector2 uv, Vector3 tangent )
	{
		if ( FloatCount + CpuMesh.FloatsPerVertex > _data.Length ) Array.Resize( ref _data, _data.Length * 2 );
		var d = _data.AsSpan( FloatCount, CpuMesh.FloatsPerVertex );
		d[0] = p.X; d[1] = p.Y; d[2] = p.Z;
		d[3] = n.X; d[4] = n.Y; d[5] = n.Z;
		d[6] = d[7] = d[8] = d[9] = 0; // bone indices
		d[10] = 1; d[11] = d[12] = d[13] = 0; // bone weights
		d[14] = uv.X; d[15] = uv.Y;
		d[16] = tangent.X; d[17] = tangent.Y; d[18] = tangent.Z; d[19] = 1;
		d[20] = 1; d[21] = 1; d[22] = 1; d[23] = 1; // no per-vertex colour on live layers: white keeps albedo unchanged
		FloatCount += CpuMesh.FloatsPerVertex;
		Min = Vector3.Min( Min, p ); Max = Vector3.Max( Max, p );
	}

	public void Triangle( Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc )
	{
		var t = SafeNormalize( b - a, Vector3.UnitX );
		Vertex( a, na, new Vector2( 0, 0 ), t );
		Vertex( b, nb, new Vector2( 1, 0 ), t );
		Vertex( c, nc, new Vector2( 0, 1 ), t );
	}

	/// <summary>Quad a-b-c-d (counter-clockwise seen from <paramref name="n"/>).</summary>
	public void Quad( Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n )
	{
		var t = SafeNormalize( b - a, Vector3.UnitX );
		Vertex( a, n, new Vector2( 0, 1 ), t ); Vertex( b, n, new Vector2( 1, 1 ), t ); Vertex( c, n, new Vector2( 1, 0 ), t );
		Vertex( a, n, new Vector2( 0, 1 ), t ); Vertex( c, n, new Vector2( 1, 0 ), t ); Vertex( d, n, new Vector2( 0, 0 ), t );
	}

	public static Vector3 SafeNormalize( Vector3 v, Vector3 fallback )
	{
		float l = v.Length();
		return l > 1e-12f ? v / l : fallback;
	}
}

/// <summary>One source of live geometry. <see cref="Advance"/> steps its simulation, <see cref="Emit"/>
/// writes this frame's triangles. Emit must not allocate in steady state.</summary>
public abstract class LiveLayer
{
	public LiveMaterial Material { get; set; } = new( new Vector3( .8f ) );
	public bool Visible { get; set; } = true;
	/// <summary>True when the geometry changes between frames (TAA must not treat it as static).</summary>
	public virtual bool Dynamic => true;
	// R7: last build's positions; reused as previous positions when the vertex count is unchanged.
	public float[] PrevPositions = Array.Empty<float>();
	public int PrevCount = -1;
	public virtual void Advance( float dt ) { }
	public abstract void Emit( LiveBuilder b, in LiveView view );
}
