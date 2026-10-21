using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>World volumes cover the whole view (global fog); Box/Sphere are local media.</summary>
public enum VolumeShape { World = 0, Box = 1, Sphere = 2 }

/// <summary>Additive media add their density; multiplicative media scale the density
/// accumulated from the volumes before them (e.g. 0 clears fog inside a room).</summary>
public enum VolumeBlend { Additive = 0, Multiplicative = 1 }

/// <summary>One participating medium. Distances are world units (s&amp;box inches);
/// <see cref="Density"/> is extinction per world unit at full weight.</summary>
public sealed class FogVolume
{
	public VolumeShape Shape = VolumeShape.World;
	public VolumeBlend Blend = VolumeBlend.Additive;
	public Vector3 Centre;
	/// <summary>Box half extents; a sphere uses X as its radius. Ignored by World.</summary>
	public Vector3 Size = new( 100, 100, 100 );
	/// <summary>Fraction of the half extent (0..1) over which density fades to zero at the edge.</summary>
	public float Falloff = 0.5f;
	public float Density = 0.01f;
	/// <summary>Scattering albedo (per channel); extinction = Density * (Scattering + Absorption).</summary>
	public Vector3 Scattering = new( 1, 1, 1 );
	public float Absorption = 0.05f;
	/// <summary>Henyey-Greenstein g in (-1,1); positive scatters forward (towards the key light).</summary>
	public float Anisotropy = 0.5f;
	/// <summary>Linear HDR radiance emitted per unit density.</summary>
	public Vector3 Emission;
	/// <summary>World volumes only: density *= exp(-max(0, z - Centre.Z) * HeightFalloff).</summary>
	public float HeightFalloff;
	/// <summary>Optional density multiplier sampled over the volume's box bounds (X fastest).</summary>
	public DensityTexture Texture;

	/// <summary>CPU reference of the shader's density weight at a world point (for tests/tools).</summary>
	public float Weight( Vector3 p )
	{
		float edge;
		switch ( Shape )
		{
			case VolumeShape.World:
				return HeightFalloff > 0 ? MathF.Exp( -MathF.Max( 0, p.Z - Centre.Z ) * HeightFalloff ) : 1f;
			case VolumeShape.Sphere:
				edge = 1f - (p - Centre).Length() / MathF.Max( Size.X, 1e-4f );
				break;
			default:
				var d = Vector3.Abs( p - Centre ) / Vector3.Max( Size, new Vector3( 1e-4f ) );
				edge = 1f - MathF.Max( d.X, MathF.Max( d.Y, d.Z ) );
				break;
		}
		if ( edge <= 0 ) return 0;
		return Falloff <= 1e-4f ? 1f : Math.Clamp( edge / Falloff, 0f, 1f );
	}
}

/// <summary>A 3D density field (row-major X, then Y, then Z), uploaded as R16F.</summary>
public sealed class DensityTexture
{
	public readonly int Width, Height, Depth;
	public readonly float[] Values;
	public DensityTexture( int width, int height, int depth, float[] values )
	{
		if ( width <= 0 || height <= 0 || depth <= 0 ) throw new ArgumentOutOfRangeException( nameof( width ) );
		ArgumentNullException.ThrowIfNull( values );
		if ( values.Length != width * height * depth ) throw new ArgumentException( "Density texture size mismatch." );
		Width = width; Height = height; Depth = depth; Values = values;
	}
}

/// <summary>The set of media rendered by <see cref="VolumePass"/>. Empty or disabled grids are free.</summary>
public sealed class VolumeGrid
{
	public const int MaxVolumes = 8;
	public readonly List<FogVolume> Volumes = new();
	public bool Enabled = true;
	/// <summary>Ray length cap for sky pixels / unbounded world fog; 0 = 6x the reference radius.</summary>
	public float MaxDistance;
	/// <summary>Scene size the CLI density is relative to; set by <see cref="GlobalFog"/>.</summary>
	public float ReferenceRadius = 100f;
	/// <summary>Uniform ambient in-scatter (linear HDR) added to the key light.</summary>
	public Vector3 Ambient = new( 0.02f, 0.022f, 0.028f );
	/// <summary>Artistic multiplier on the key light's in-scatter (volumetric light intensity).</summary>
	public float KeyIntensity = 5f;
	/// <summary>Force the analytic height-fog fallback (no shadows, no local volumes).</summary>
	public bool ForceFallback;

	public bool IsEmpty
	{
		get
		{
			if ( !Enabled ) return true;
			foreach ( var v in Volumes )
				if ( v.Blend == VolumeBlend.Additive && (v.Density > 0 || v.Emission != Vector3.Zero) ) return false;
			return true;
		}
	}

	/// <summary>Global fog scaled to the scene: <paramref name="opticalDepth"/> is the extinction
	/// across one scene radius, so the look is independent of the scene's unit scale.</summary>
	public static VolumeGrid GlobalFog( float opticalDepth, Vector3 centre, float sceneRadius )
	{
		float radius = MathF.Max( sceneRadius, 1e-3f );
		var grid = new VolumeGrid { ReferenceRadius = radius };
		grid.Volumes.Add( new FogVolume
		{
			Shape = VolumeShape.World,
			Centre = centre - new Vector3( 0, 0, radius ),
			Density = MathF.Max( 0, opticalDepth ) / radius,
			Anisotropy = 0.3f,
			HeightFalloff = 0.35f / radius,
		} );
		return grid;
	}

	/// <summary>CPU reference of the combined extinction at a point (matches the shader).</summary>
	public float DensityAt( Vector3 p )
	{
		float density = 0;
		foreach ( var v in Volumes )
		{
			float w = v.Weight( p );
			if ( v.Blend == VolumeBlend.Additive ) density += v.Density * w;
			else density *= 1f + (v.Density - 1f) * w;
		}
		return density;
	}
}
