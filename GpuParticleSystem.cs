using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>
/// GPU particle update via compute shader (GL 4.3+).
///
/// One <see cref="Dispatch"/> call mirrors exactly ONE <c>ParticleSystem.Integrate</c>
/// substep for every live particle: gravity, curl-noise turbulence, vortex,
/// semi-implicit drag toward the wind, ground-plane collision (restitution /
/// friction), age advance, and quaternion rotation from angular velocity.
/// Callers that want a full CPU <c>Step</c> loop <see cref="Dispatch"/> once
/// per substep, advancing <c>time</c> by the substep each time — the same loop
/// <c>ParticleSystem.SimulateCpu</c> runs.
///
/// BREAKING CHANGE (2026-10-24, particles lane 3): the previous shader was
/// only <c>p += v*dt</c> plus a box bounce, which matched no CPU behaviour
/// despite the old comments claiming an exact mirror. That shader and its
/// <c>Dispatch(gl, program, buffer, count, dt, bounds)</c> form are REMOVED;
/// the box bounce is gone, and the SSBO now holds the full 64-byte
/// <see cref="ParticleState"/> (16 floats) instead of pos+vel (6 floats).
/// <see cref="StrideFloats"/> changed 6 to 16; position is still at offset 0,
/// so callers that indexed with <see cref="StrideFloats"/> keep working.
/// The old <c>Upload</c>/<c>Download</c> names are kept with the new layout.
///
/// Deliberately NOT mirrored here, and stated plainly:
/// - Emission and expiry stay on the CPU (<c>ParticleSystem.BeginStep</c> /
///   <c>EndStep</c>). The GPU never spawns or kills; dead slots
///   (<c>Alive == 0</c>) are left byte-identical.
/// - Mesh collision is not in the shader. A CPU system with a
///   <c>CollisionMesh</c> will diverge from the GPU after the first mesh hit;
///   ground-plane collision IS mirrored.
/// - Strand history is not written by the shader.
/// After a GPU-driven run the CPU copy of a system is stale, exactly as the
/// <c>SimulatedOnGpu</c> flag on <see cref="ParticleSystem"/> says; use
/// <see cref="DownloadStates"/> to bring states back for verification.
/// </summary>
public static class GpuParticleSystem
{
	/// <summary>ParticleState as floats: four std430 vec4s (pos+age, vel+alive, rotation, angVel+size).</summary>
	public const int StrideFloats = 16;
	public const int StrideBytes = StrideFloats * sizeof( float );

	/// <summary>Float offsets within one particle's 16 floats.</summary>
	public const int PosOffset = 0, AgeOffset = 3, VelOffset = 4, AliveOffset = 7;
	public const int RotOffset = 8, AngVelOffset = 12, SizeOffset = 15;

	/// <summary>
	/// Parameters for one GPU integrate step. Build with <see cref="FromSettings"/>
	/// so axis / ground-normal normalization matches the CPU SimParams exactly.
	/// </summary>
	public struct GpuSimParams
	{
		public Vector3 Gravity, Wind, VortexCenter, VortexAxis, GroundNormal;
		public float Drag, TurbStrength, TurbFreq, TurbSpeed;
		public float VortexStrength, VortexRadius2;
		public bool GroundEnabled;
		public float GroundOffset, Radius, Restitution, Friction;
		public uint Seed;

		public static GpuSimParams FromSettings( ParticleSystemSettings s )
		{
			ArgumentNullException.ThrowIfNull( s );
			return new GpuSimParams
			{
				Gravity = s.Gravity, Wind = s.Wind, Drag = s.Drag,
				TurbStrength = s.TurbulenceStrength, TurbFreq = s.TurbulenceFrequency, TurbSpeed = s.TurbulenceSpeed,
				VortexStrength = s.VortexStrength, VortexCenter = s.VortexCenter,
				VortexAxis = s.VortexAxis.LengthSquared() > 0 ? Vector3.Normalize( s.VortexAxis ) : new Vector3( 0, 0, 1 ),
				VortexRadius2 = s.VortexRadius * s.VortexRadius,
				GroundEnabled = s.GroundEnabled,
				GroundNormal = s.GroundNormal.LengthSquared() > 0 ? Vector3.Normalize( s.GroundNormal ) : new Vector3( 0, 0, 1 ),
				GroundOffset = s.GroundOffset, Radius = s.CollisionRadius,
				Restitution = s.Restitution, Friction = s.Friction,
				Seed = s.Seed,
			};
		}
	}

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
uniform vec3 uGravity;
uniform vec3 uWind;
uniform float uDrag;
uniform float uTurbStrength;
uniform float uTurbFreq;
uniform float uTurbSpeed;
uniform uint uSeed;
uniform float uVortexStrength;
uniform vec3 uVortexCenter;
uniform vec3 uVortexAxis;
uniform float uVortexRadius2;
uniform int uGroundEnabled;
uniform vec3 uGroundNormal;
uniform float uGroundOffset;
uniform float uRadius;
uniform float uRestitution;
uniform float uFriction;

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
vec3 respond(vec3 vel, vec3 n, float restitution, float friction) {
    float vn = dot(vel, n);
    if (vn >= 0.0) return vel;
    vec3 vt = vel - n * vn;
    return vt * (1.0 - friction) - n * (vn * restitution);
}
void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i >= uCount) return;
    Particle pt = particles[i];
    if (pt.velAlive.w == 0.0) return;
    vec3 pos = pt.posAge.xyz;
    vec3 vel = pt.velAlive.xyz;
    vec3 acc = uGravity;
    if (uTurbStrength > 0.0) {
        acc += curlNoise(pos * uTurbFreq + vec3(0.0, 0.0, uTime * uTurbSpeed), uSeed) * uTurbStrength;
    }
    if (uVortexStrength != 0.0) {
        vec3 r = pos - uVortexCenter;
        vec3 rp = r - uVortexAxis * dot(r, uVortexAxis);
        float d2 = dot(rp, rp);
        acc += cross(uVortexAxis, rp) * (uVortexStrength / (d2 + uVortexRadius2));
    }
    vel = (vel + (acc + uWind * uDrag) * uDt) / (1.0 + uDrag * uDt);
    vec3 next = pos + vel * uDt;
    if (uGroundEnabled != 0) {
        float d = dot(uGroundNormal, next) - uGroundOffset - uRadius;
        if (d < 0.0) {
            next -= uGroundNormal * d;
            vel = respond(vel, uGroundNormal, uRestitution, uFriction);
        }
    }
    pt.posAge.xyz = next;
    pt.posAge.w += uDt;
    pt.velAlive.xyz = vel;
    vec3 w = pt.angSize.xyz;
    if (w.x != 0.0 || w.y != 0.0 || w.z != 0.0) {
        vec4 q = pt.rot;
        vec4 dq = vec4(
            w.x * q.w + w.y * q.z - w.z * q.y,
            -w.x * q.z + w.y * q.w + w.z * q.x,
            w.x * q.y - w.y * q.x + w.z * q.w,
            -w.x * q.x - w.y * q.y - w.z * q.z);
        q += dq * (0.5 * uDt);
        pt.rot = q / length(q);
    }
    particles[i] = pt;
}
";

	public static uint CreateProgram( GL gl )
	{
		var cs = gl.CreateShader( ShaderType.ComputeShader );
		gl.ShaderSource( cs, ComputeSrc );
		gl.CompileShader( cs );
		gl.GetShader( cs, ShaderParameterName.CompileStatus, out int ok );
		if ( ok == 0 ) { var log = gl.GetShaderInfoLog( cs ); gl.DeleteShader( cs ); throw new InvalidOperationException( "compute compile failed:\n" + log ); }
		var prog = gl.CreateProgram();
		gl.AttachShader( prog, cs );
		gl.LinkProgram( prog );
		gl.GetProgram( prog, ProgramPropertyARB.LinkStatus, out int linked );
		gl.DetachShader( prog, cs );
		gl.DeleteShader( cs );
		if ( linked == 0 ) { var log = gl.GetProgramInfoLog( prog ); gl.DeleteProgram( prog ); throw new InvalidOperationException( "compute link failed:\n" + log ); }
		return prog;
	}

	static uint UploadFloats( GL gl, float[] data )
	{
		uint buf = gl.GenBuffer();
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, buf );
		unsafe
		{
			fixed ( float* ptr = data )
			{
				gl.BufferData( BufferTargetARB.ShaderStorageBuffer, (nuint)(data.Length * sizeof( float )), ptr, BufferUsageARB.DynamicDraw );
			}
		}
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, 0 );
		return buf;
	}

	/// <summary>
	/// Allocate and initialize an SSBO for <paramref name="particleCount"/> particles.
	/// Positions random-ish in [-1,1], velocities small; every particle is alive,
	/// unrotated, unspinning, age 0, size 1. Layout is the full 16-float state.
	/// Returns buffer handle.
	/// </summary>
	public static uint Upload( GL gl, int particleCount, uint seed = 12345u )
	{
		if ( particleCount < 0 ) throw new ArgumentOutOfRangeException( nameof( particleCount ) );
		var data = new float[particleCount * StrideFloats];
		var rng = new Random( (int)seed );
		for ( int i = 0; i < particleCount; i++ )
		{
			int b = i * StrideFloats;
			data[b + 0] = (float)(rng.NextDouble() * 2 - 1);
			data[b + 1] = (float)(rng.NextDouble() * 2 - 1);
			data[b + 2] = (float)(rng.NextDouble() * 2 - 1);
			data[b + 3] = 0; // Age
			data[b + 4] = (float)(rng.NextDouble() * 0.2 - 0.1);
			data[b + 5] = (float)(rng.NextDouble() * 0.2 - 0.1);
			data[b + 6] = (float)(rng.NextDouble() * 0.2 - 0.1);
			data[b + 7] = 1; // Alive
			data[b + 8] = 0; data[b + 9] = 0; data[b + 10] = 0; data[b + 11] = 1; // Identity rotation
			data[b + 12] = 0; data[b + 13] = 0; data[b + 14] = 0; // Angular velocity
			data[b + 15] = 1; // Size
		}
		return UploadFloats( gl, data );
	}

	/// <summary>Upload CPU particle states verbatim (field order = SSBO layout). Returns buffer handle.</summary>
	public static uint UploadStates( GL gl, ReadOnlySpan<ParticleState> states ) => UploadFloats( gl, StatesToFloats( states ) );

	/// <summary>
	/// Run one integrate substep (see class doc) for <paramref name="particleCount"/>
	/// particles with substep <paramref name="dt"/> at simulation time
	/// <paramref name="time"/> (the time the CPU would pass to Integrate).
	/// </summary>
	public static void Dispatch( GL gl, uint program, uint buffer, int particleCount, GpuSimParams sim, float dt, float time )
	{
		if ( particleCount < 0 ) throw new ArgumentOutOfRangeException( nameof( particleCount ) );
		gl.UseProgram( program );
		void F( string name, float v ) => gl.Uniform1( gl.GetUniformLocation( program, name ), v );
		void V( string name, Vector3 v ) => gl.Uniform3( gl.GetUniformLocation( program, name ), v.X, v.Y, v.Z );
		gl.Uniform1( gl.GetUniformLocation( program, "uCount" ), (uint)particleCount );
		F( "uDt", dt ); F( "uTime", time );
		V( "uGravity", sim.Gravity ); V( "uWind", sim.Wind ); F( "uDrag", sim.Drag );
		F( "uTurbStrength", sim.TurbStrength ); F( "uTurbFreq", sim.TurbFreq ); F( "uTurbSpeed", sim.TurbSpeed );
		gl.Uniform1( gl.GetUniformLocation( program, "uSeed" ), sim.Seed );
		F( "uVortexStrength", sim.VortexStrength ); V( "uVortexCenter", sim.VortexCenter ); V( "uVortexAxis", sim.VortexAxis );
		F( "uVortexRadius2", sim.VortexRadius2 );
		gl.Uniform1( gl.GetUniformLocation( program, "uGroundEnabled" ), sim.GroundEnabled ? 1 : 0 );
		V( "uGroundNormal", sim.GroundNormal );
		F( "uGroundOffset", sim.GroundOffset ); F( "uRadius", sim.Radius );
		F( "uRestitution", sim.Restitution ); F( "uFriction", sim.Friction );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 0, buffer );
		uint groups = (uint)((particleCount + 255) / 256);
		if ( groups == 0 ) groups = 1;
		gl.DispatchCompute( groups, 1, 1 );
		gl.MemoryBarrier( MemoryBarrierMask.ShaderStorageBarrierBit );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 0, 0 );
	}

	/// <summary>Read back raw SSBO floats (<paramref name="particleCount"/> × 16) for verification.</summary>
	public static float[] Download( GL gl, uint buffer, int particleCount )
	{
		var data = new float[particleCount * StrideFloats];
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, buffer );
		unsafe
		{
			fixed ( float* ptr = data )
			{
				gl.GetBufferSubData( BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(data.Length * sizeof( float )), ptr );
			}
		}
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, 0 );
		return data;
	}

	/// <summary>Read back particle states from an SSBO written by <see cref="UploadStates"/> / <see cref="Dispatch"/>.</summary>
	public static ParticleState[] DownloadStates( GL gl, uint buffer, int particleCount ) => FloatsToStates( Download( gl, buffer, particleCount ), particleCount );

	public static void Dispose( GL gl, uint buffer, uint program )
	{
		if ( buffer != 0 ) gl.DeleteBuffer( buffer );
		if ( program != 0 ) gl.DeleteProgram( program );
	}

	static float[] StatesToFloats( ReadOnlySpan<ParticleState> states )
	{
		var data = new float[states.Length * StrideFloats];
		for ( int i = 0; i < states.Length; i++ )
		{
			int b = i * StrideFloats;
			var s = states[i];
			data[b + 0] = s.Position.X; data[b + 1] = s.Position.Y; data[b + 2] = s.Position.Z; data[b + 3] = s.Age;
			data[b + 4] = s.Velocity.X; data[b + 5] = s.Velocity.Y; data[b + 6] = s.Velocity.Z; data[b + 7] = s.Alive;
			data[b + 8] = s.Rotation.X; data[b + 9] = s.Rotation.Y; data[b + 10] = s.Rotation.Z; data[b + 11] = s.Rotation.W;
			data[b + 12] = s.AngularVelocity.X; data[b + 13] = s.AngularVelocity.Y; data[b + 14] = s.AngularVelocity.Z; data[b + 15] = s.Size;
		}
		return data;
	}

	static ParticleState[] FloatsToStates( float[] data, int count )
	{
		var states = new ParticleState[count];
		for ( int i = 0; i < count; i++ )
		{
			int b = i * StrideFloats;
			states[i] = new ParticleState
			{
				Position = new Vector3( data[b + 0], data[b + 1], data[b + 2] ), Age = data[b + 3],
				Velocity = new Vector3( data[b + 4], data[b + 5], data[b + 6] ), Alive = data[b + 7],
				Rotation = new Quaternion( data[b + 8], data[b + 9], data[b + 10], data[b + 11] ),
				AngularVelocity = new Vector3( data[b + 12], data[b + 13], data[b + 14] ), Size = data[b + 15],
			};
		}
		return states;
	}
}
