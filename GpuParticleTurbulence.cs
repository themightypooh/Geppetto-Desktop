using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>
/// GPU compute-shader turbulence VARIANTS, companion to the landed
/// <see cref="GpuParticleSystem"/> parity shader — read that file, do not
/// edit it. This module never touches <c>GpuParticleSystem.cs</c>.
///
/// What this is: a velocity-only force pass over the SAME 64-byte
/// <see cref="ParticleState"/> SSBO layout (<see cref="GpuParticleSystem.StrideFloats"/>
/// = 16, position at offset 0). One <see cref="Dispatch"/> call adds
/// <c>turbulenceAcceleration * dt</c> to every live particle's velocity
/// and NOTHING else: positions, ages, rotations, sizes and dead slots
/// are byte-identical afterwards. That is deliberate composition, not
/// an omission — the parity shader already owns position/age/rotation
/// integration, so a second integrator here would double-step particles.
/// The coordinator sweep wires this pass into a substep (see the
/// integration lines in the lane report); on its own it is a force,
/// not a full step.
///
/// Variants (uniform <c>uVariant</c>):
/// - <see cref="TurbulenceVariant.FbmCurl"/> (default): multi-octave
///   curl noise. Octave <c>o</c> samples the lane-3 curl field at
///   frequency <c>Lacunarity^o</c> with amplitude <c>Gain^o</c>, and
///   the sum is divided by the amplitude sum so <see cref="TurbulenceParams.Strength"/>
///   keeps its lane-3 meaning at octave 1. Each octave is itself a
///   curl, so the fBm sum is still divergence-free. Note the curl
///   magnitude itself scales with sample frequency (the analytic
///   gradient is taken at the scaled point), so with the defaults
///   (gain 0.5, lacunarity 2) successive octaves contribute roughly
///   equal magnitude, not decaying magnitude — amplitude grows
///   slowly with octave count; it does not explode (bounded in tests).
/// - <see cref="TurbulenceVariant.DomainWarp"/>: the sample position
///   is first warped by a first noise field — three scalar value-noise
///   samples at independent seeds, scaled by
///   <see cref="TurbulenceParams.WarpAmplitude"/> — and the lane-3 curl
///   is then evaluated at the warped position. Warp amplitude 0
///   reproduces the single-octave curl exactly. Unlike the fBm sum,
///   the warped field is NOT claimed divergence-free: it is a curl
///   field sampled at warped coordinates, and divergence in the
///   original coordinates is not zero in general.
///
/// Time-varying mode (both variants): the noise-space point is
/// <c>pos * Frequency + (0, 0, time * Speed)</c>, exactly the lane-3
/// time shift, so time enters only through the smooth value-noise
/// field. The field is continuous in <c>time</c> (smoothstep fade has
/// zero derivative at lattice boundaries, so both value and gradient
/// cross cell boundaries continuously) — adjacent-frame outputs
/// differ by a small amount and there is no popping. This is proven
/// by test, not asserted: the time test compares adjacent-t outputs
/// against outputs seconds apart.
///
/// CPU reference: <see cref="CpuAcceleration"/> (and the
/// <c>CpuFbmCurl</c> / <see cref="CpuWarpCurl"/> pieces) re-implements
/// the same math on top of the public <see cref="ParticleMath"/>
/// primitives, sharing no GLSL text. GPU-vs-CPU agreement is EPSILON,
/// never bit-identical (the deep-research correction in
/// particles-lane-list.md §18: GPU indices/FP rounding differ) —
/// tests use absolute velocity tolerances and report measured
/// deviations, they do not compare floats for equality.
///
/// Defaults (documented, also the <see cref="TurbulenceParams.Default"/>
/// value): octaves 4, lacunarity 2.0, gain 0.5, warp amplitude 0.5,
/// strength 10, frequency 0.2, speed 0.7, seed 7. Octaves are capped
/// at 8 (the GLSL loop bound); <see cref="TurbulenceParams.Validate"/>
/// throws loudly outside the documented ranges instead of clamping
/// silently.
///
/// NOT built here (other lanes / sweep): wiring into
/// <c>GpuParticleSystem.Dispatch</c>, CPU noise variants themselves
/// (lane P29 owns <c>ParticleNoise.cs</c>), GPU colliders/spawn/LOD/
/// over-life, save/serialization (lane 5). ParticleNoise.cs had NOT
/// landed when this lane was written, so the GLSL below ports the
/// lane-3 noise (hash/lattice/noiseGrad/curl) verbatim rather than
/// depending on P29's design.
/// </summary>
public static class GpuParticleTurbulence
{
	/// <summary>Which turbulence field the shader evaluates.</summary>
	public enum TurbulenceVariant
	{
		FbmCurl = 0,
		DomainWarp = 1,
	}

	/// <summary>Parameters for one turbulence force pass. Build from <see cref="Default"/> and tweak fields, then call <see cref="TurbulenceParams.Validate"/> (Dispatch does it for you).</summary>
	public struct TurbulenceParams
	{
		public TurbulenceVariant Variant;
		public int Octaves;
		public float Lacunarity;
		public float Gain;
		public float WarpAmplitude;
		public float Strength;
		public float Frequency;
		public float Speed;
		public uint Seed;

		/// <summary>Documented defaults: fBm curl, 4 octaves, lacunarity 2, gain 0.5, warp 0.5, strength 10, frequency 0.2, speed 0.7, seed 7.</summary>
		public static TurbulenceParams Default => new TurbulenceParams
		{
			Variant = TurbulenceVariant.FbmCurl,
			Octaves = 4,
			Lacunarity = 2.0f,
			Gain = 0.5f,
			WarpAmplitude = 0.5f,
			Strength = 10f,
			Frequency = 0.2f,
			Speed = 0.7f,
			Seed = 7u,
		};

		public void Validate()
		{
			if ( Variant != TurbulenceVariant.FbmCurl && Variant != TurbulenceVariant.DomainWarp )
				throw new ArgumentException( $"Unknown turbulence variant {(int)Variant}." );
			if ( Octaves < 1 || Octaves > 8 )
				throw new ArgumentException( $"Octaves must be 1..8, got {Octaves}." );
			if ( !float.IsFinite( Lacunarity ) || Lacunarity <= 0 )
				throw new ArgumentException( $"Lacunarity must be finite and > 0, got {Lacunarity}." );
			if ( !float.IsFinite( Gain ) || Gain < 0 || Gain > 1 )
				throw new ArgumentException( $"Gain must be finite and in [0,1], got {Gain}." );
			if ( !float.IsFinite( WarpAmplitude ) || WarpAmplitude < 0 )
				throw new ArgumentException( $"WarpAmplitude must be finite and >= 0, got {WarpAmplitude}." );
			if ( !float.IsFinite( Strength ) || Strength < 0 )
				throw new ArgumentException( $"Strength must be finite and >= 0, got {Strength}." );
			if ( !float.IsFinite( Frequency ) || Frequency <= 0 )
				throw new ArgumentException( $"Frequency must be finite and > 0, got {Frequency}." );
			if ( !float.IsFinite( Speed ) )
				throw new ArgumentException( $"Speed must be finite, got {Speed}." );
		}
	}

	// Noise core below is the lane-3 port, verbatim in structure and
	// constants (hashI / lattice / noiseGrad / curlNoise), so the
	// octave-1 / warp-0 cases reproduce the parity shader exactly.
	static readonly string ComputeSrc = @"#version 430 core
layout(local_size_x = 256) in;
struct Particle {
    vec4 posAge;
    vec4 velAlive;
    vec4 rot;
    vec4 angSize;
};
layout(std430, binding = 0) buffer Particles {
    Particle particles[];
};
uniform uint uCount;
uniform float uDt;
uniform float uTime;
uniform float uFreq;
uniform float uStrength;
uniform float uSpeed;
uniform uint uSeed;
uniform int uVariant;
uniform int uOctaves;
uniform float uLacunarity;
uniform float uGain;
uniform float uWarpAmplitude;

uint hashI(ivec3 p, uint seed) {
    uint h = uint(p.x) * 0x8da6b343u ^ uint(p.y) * 0xd8163841u ^ uint(p.z) * 0xcb1ab31fu ^ seed;
    h ^= h >> 16u;
    h *= 0x7feb352du;
    h ^= h >> 15u;
    h *= 0x846ca68bu;
    h ^= h >> 16u;
    return h;
}
float lattice(ivec3 p, uint seed) {
    return float(hashI(p, seed) & 0xFFFFFFu) * (2.0 / 16777215.0) - 1.0;
}
float noiseGrad(vec3 p, uint seed, out vec3 grad) {
    vec3 fl = floor(p);
    ivec3 ip = ivec3(fl);
    vec3 t = p - fl;
    vec3 u = t * t * (3.0 - 2.0 * t);
    vec3 d = 6.0 * t * (1.0 - t);
    float c000 = lattice(ip, seed);
    float c100 = lattice(ip + ivec3(1, 0, 0), seed);
    float c010 = lattice(ip + ivec3(0, 1, 0), seed);
    float c110 = lattice(ip + ivec3(1, 1, 0), seed);
    float c001 = lattice(ip + ivec3(0, 0, 1), seed);
    float c101 = lattice(ip + ivec3(1, 0, 1), seed);
    float c011 = lattice(ip + ivec3(0, 1, 1), seed);
    float c111 = lattice(ip + ivec3(1, 1, 1), seed);
    float x00 = c000 + (c100 - c000) * u.x;
    float x10 = c010 + (c110 - c010) * u.x;
    float x01 = c001 + (c101 - c001) * u.x;
    float x11 = c011 + (c111 - c011) * u.x;
    float y0 = x00 + (x10 - x00) * u.y;
    float y1 = x01 + (x11 - x01) * u.y;
    float v = y0 + (y1 - y0) * u.z;
    float dx00 = (c100 - c000) * d.x;
    float dx10 = (c110 - c010) * d.x;
    float dx01 = (c101 - c001) * d.x;
    float dx11 = (c111 - c011) * d.x;
    float dy0x = dx00 + (dx10 - dx00) * u.y;
    float dy1x = dx01 + (dx11 - dx01) * u.y;
    float gx = dy0x + (dy1x - dy0x) * u.z;
    float gy0 = (x10 - x00) * d.y;
    float gy1 = (x11 - x01) * d.y;
    float gy = gy0 + (gy1 - gy0) * u.z;
    float gz = (y1 - y0) * d.z;
    grad = vec3(gx, gy, gz);
    return v;
}
vec3 curlNoise(vec3 p, uint seed) {
    vec3 g1, g2, g3;
    noiseGrad(p, seed, g1);
    noiseGrad(p, seed ^ 0x68bc21ebu, g2);
    noiseGrad(p, seed ^ 0x02e5be93u, g3);
    return vec3(g3.y - g2.z, g1.z - g3.x, g2.x - g1.y);
}
vec3 fbmCurl(vec3 p, uint seed) {
    vec3 sum = vec3(0.0);
    float amp = 1.0;
    float f = 1.0;
    float ampSum = 0.0;
    for (int o = 0; o < 8; o++) {
        if (o >= uOctaves) break;
        sum += amp * curlNoise(p * f, seed);
        ampSum += amp;
        amp *= uGain;
        f *= uLacunarity;
    }
    return sum / ampSum;
}
vec3 warpCurl(vec3 p, uint seed) {
    vec3 g;
    float nx = noiseGrad(p, seed ^ 0x9e3779b9u, g);
    float ny = noiseGrad(p, seed ^ 0x85ebca6bu, g);
    float nz = noiseGrad(p, seed ^ 0xc2b2ae35u, g);
    vec3 warped = p + vec3(nx, ny, nz) * uWarpAmplitude;
    return curlNoise(warped, seed);
}
void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i >= uCount) return;
    Particle pt = particles[i];
    if (pt.velAlive.w == 0.0) return;
    vec3 baseP = pt.posAge.xyz * uFreq + vec3(0.0, 0.0, uTime * uSpeed);
    vec3 field = (uVariant == 1) ? warpCurl(baseP, uSeed) : fbmCurl(baseP, uSeed);
    pt.velAlive.xyz += field * uStrength * uDt;
    particles[i] = pt;
}
";

	public static uint CreateProgram( GL gl )
	{
		var cs = gl.CreateShader( ShaderType.ComputeShader );
		gl.ShaderSource( cs, ComputeSrc );
		gl.CompileShader( cs );
		gl.GetShader( cs, ShaderParameterName.CompileStatus, out int ok );
		if ( ok == 0 ) { var log = gl.GetShaderInfoLog( cs ); gl.DeleteShader( cs ); throw new InvalidOperationException( "turbulence compute compile failed:\n" + log ); }
		var prog = gl.CreateProgram();
		gl.AttachShader( prog, cs );
		gl.LinkProgram( prog );
		gl.GetProgram( prog, ProgramPropertyARB.LinkStatus, out int linked );
		gl.DetachShader( prog, cs );
		gl.DeleteShader( cs );
		if ( linked == 0 ) { var log = gl.GetProgramInfoLog( prog ); gl.DeleteProgram( prog ); throw new InvalidOperationException( "turbulence compute link failed:\n" + log ); }
		return prog;
	}

	/// <summary>
	/// Velocity-only force pass: <c>vel += CpuAcceleration(pos, p, time) * dt</c>
	/// for live particles. Positions, ages, rotations, sizes and dead slots
	/// are untouched. Buffers are the parity shader's: create them with
	/// <see cref="GpuParticleSystem.UploadStates"/> and read them back with
	/// <see cref="GpuParticleSystem.DownloadStates"/>.
	/// </summary>
	public static void Dispatch( GL gl, uint program, uint buffer, int particleCount, TurbulenceParams p, float dt, float time )
	{
		if ( particleCount < 0 ) throw new ArgumentOutOfRangeException( nameof( particleCount ) );
		p.Validate();
		gl.UseProgram( program );
		void F( string name, float v ) => gl.Uniform1( gl.GetUniformLocation( program, name ), v );
		gl.Uniform1( gl.GetUniformLocation( program, "uCount" ), (uint)particleCount );
		F( "uDt", dt ); F( "uTime", time );
		F( "uFreq", p.Frequency ); F( "uStrength", p.Strength ); F( "uSpeed", p.Speed );
		gl.Uniform1( gl.GetUniformLocation( program, "uSeed" ), p.Seed );
		gl.Uniform1( gl.GetUniformLocation( program, "uVariant" ), (int)p.Variant );
		gl.Uniform1( gl.GetUniformLocation( program, "uOctaves" ), p.Octaves );
		F( "uLacunarity", p.Lacunarity ); F( "uGain", p.Gain ); F( "uWarpAmplitude", p.WarpAmplitude );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 0, buffer );
		uint groups = (uint)((particleCount + 255) / 256);
		if ( groups == 0 ) groups = 1;
		gl.DispatchCompute( groups, 1, 1 );
		gl.MemoryBarrier( MemoryBarrierMask.ShaderStorageBarrierBit );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 0, 0 );
	}

	public static void Dispose( GL gl, uint buffer, uint program )
	{
		if ( buffer != 0 ) gl.DeleteBuffer( buffer );
		if ( program != 0 ) gl.DeleteProgram( program );
	}

	// ---- CPU reference (shares no GLSL text; built on ParticleMath) ----

	/// <summary>Multi-octave curl at noise-space point p, amplitude-normalised by the gain sum.</summary>
	public static Vector3 CpuFbmCurl( Vector3 p, uint seed, int octaves, float lacunarity, float gain )
	{
		Vector3 sum = Vector3.Zero;
		float amp = 1f, f = 1f, ampSum = 0f;
		for ( int o = 0; o < octaves; o++ )
		{
			sum += amp * ParticleMath.Curl( p * f, seed );
			ampSum += amp;
			amp *= gain;
			f *= lacunarity;
		}
		return sum / ampSum;
	}

	/// <summary>Curl evaluated at a value-noise-warped position (three independent scalar seeds, one per axis).</summary>
	public static Vector3 CpuWarpCurl( Vector3 p, uint seed, float warpAmplitude )
	{
		float nx = ParticleMath.NoiseGrad( p, seed ^ 0x9e3779b9u, out _ );
		float ny = ParticleMath.NoiseGrad( p, seed ^ 0x85ebca6bu, out _ );
		float nz = ParticleMath.NoiseGrad( p, seed ^ 0xc2b2ae35u, out _ );
		return ParticleMath.Curl( p + new Vector3( nx, ny, nz ) * warpAmplitude, seed );
	}

	/// <summary>Full turbulence acceleration for one particle: variant field at the time-shifted noise point, times strength.</summary>
	public static Vector3 CpuAcceleration( Vector3 position, TurbulenceParams p, float time )
	{
		p.Validate();
		Vector3 baseP = position * p.Frequency + new Vector3( 0, 0, time * p.Speed );
		Vector3 field = p.Variant == TurbulenceVariant.DomainWarp
			? CpuWarpCurl( baseP, p.Seed, p.WarpAmplitude )
			: CpuFbmCurl( baseP, p.Seed, p.Octaves, p.Lacunarity, p.Gain );
		return field * p.Strength;
	}
}
