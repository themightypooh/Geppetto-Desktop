using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>
/// GPU particle colliders: a companion compute pass to <see cref="GpuParticleSystem"/>.
///
/// This module is deliberately NOT wired into <see cref="GpuParticleSystem.Dispatch"/>.
/// The coordinator sweep owns that hookup (see the integration lines in the lane report):
/// the intended call is one <see cref="Dispatch"/> of this module per substep, AFTER the
/// parity <c>Dispatch</c>, on the SAME SSBO, so colliders see the integrated positions.
///
/// SSBO layout is identical to <see cref="GpuParticleSystem"/>: 16 floats per particle
/// (<see cref="GpuParticleSystem.StrideFloats"/>), position at offset 0, velocity at
/// offset 4, alive flag at offset 7. Only position and velocity are written; age,
/// rotation, angular velocity and size are left byte-identical, and dead slots
/// (<c>Alive == 0</c>) are left untouched.
///
/// Colliders, applied in this order per particle (sphere first, then box):
/// - Sphere: a solid sphere. A particle is a sphere of radius
///   <c>uParticleRadius</c> (the CPU <c>CollisionRadius</c>); it collides when the
///   centre distance is strictly less than sphere radius + particle radius, and is
///   pushed out along the centre-to-particle direction to exactly that distance.
///   A particle centred exactly on the sphere centre has no direction, so both the
///   GLSL and the CPU reference use the deterministic fallback normal (0, 0, 1).
/// - Box: an axis-aligned solid box. Outside, the particle collides when its distance
///   to the closest point on the box is strictly less than the particle radius, and
///   is pushed out along (position - closest point) to exactly the particle radius
///   from that point (a face hit gives the face normal; a corner/edge hit gives the
///   diagonal closest-point direction). Inside (or exactly on a face), it is pushed
///   out through the NEAREST face to exactly the particle radius beyond that face.
///   Ties between faces resolve to the first face in Min.X, Max.X, Min.Y, Max.Y,
///   Min.Z, Max.Z order, identically in GLSL and C#.
/// 
/// Bounce response is exactly the CPU response (<c>ParticleSystem.Respond</c> and the
/// parity shader's <c>respond</c>): if the velocity component along the contact
/// normal is non-negative the velocity is unchanged (only the position is pushed
/// out); otherwise the tangential part is scaled by (1 - friction) and the normal
/// part is reflected and scaled by restitution:
/// <c>vel = vt * (1 - friction) - n * (vn * restitution)</c>.
///
/// Out of scope, stated plainly:
/// - Mesh colliders in GLSL (lane 3's stated limit; a future lane scopes them alone).
/// - GPU spawn/kill (a separate lane); this pass never changes the alive flag.
/// - Per-particle collision events/callbacks: a GPU-driven system cannot raise them,
///   exactly as the parity shader cannot raise mesh/ground events.
/// - CPU colliders themselves (lane P23's file, not landed at the time of this lane;
///   this file's CPU code is only a test reference, not that lane).
///
/// Agreement policy (deep-research correction, section 18): GPU-vs-CPU agreement is
/// EPSILON agreement, never bit-identical. GLSL and C# float arithmetic differ in
/// rounding, so the tests compare with a 1e-4 tolerance and report the measured
/// maximum deviations.
///
/// Out-of-bounds guard: the shader returns immediately when
/// <c>gl_GlobalInvocationID.x &gt;= uCount</c>, the same class of guard lane 3 uses
/// (its <c>uCount</c> check). Dispatching a count that is not a multiple of the
/// workgroup size therefore never reads or writes past the first <c>count</c>
/// slots, even though the dispatch rounds the group count up.
/// </summary>
public static class GpuParticleColliders
{
	/// <summary>Same 64-byte ParticleState layout as the parity shader.</summary>
	public const int StrideFloats = GpuParticleSystem.StrideFloats;
	/// <summary>Workgroup size; must match local_size_x in the shader.</summary>
	public const int LocalSize = 256;

	/// <summary>A solid sphere collider. Restitution and Friction must be 0..1; Radius must be &gt;= 0.</summary>
	public struct SphereCollider
	{
		public Vector3 Center;
		public float Radius;
		public float Restitution;
		public float Friction;
	}

	/// <summary>An axis-aligned solid box collider. Every Min component must be &lt; the matching Max component.</summary>
	public struct BoxCollider
	{
		public Vector3 Min;
		public Vector3 Max;
		public float Restitution;
		public float Friction;
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
uniform float uParticleRadius;
uniform int uSphereEnabled;
uniform vec3 uSphereCenter;
uniform float uSphereRadius;
uniform float uSphereRestitution;
uniform float uSphereFriction;
uniform int uBoxEnabled;
uniform vec3 uBoxMin;
uniform vec3 uBoxMax;
uniform float uBoxRestitution;
uniform float uBoxFriction;

vec3 respond(vec3 vel, vec3 n, float restitution, float friction) {
    float vn = dot(vel, n);
    if (vn >= 0.0) return vel;
    vec3 vt = vel - n * vn;
    return vt * (1.0 - friction) - n * (vn * restitution);
}
void sphereHit(inout vec3 pos, inout vec3 vel) {
    vec3 d = pos - uSphereCenter;
    float dist = length(d);
    float eff = uSphereRadius + uParticleRadius;
    if (dist >= eff) return;
    vec3 n = dist > 0.000001 ? d / dist : vec3(0.0, 0.0, 1.0);
    pos = uSphereCenter + n * eff;
    vel = respond(vel, n, uSphereRestitution, uSphereFriction);
}
void boxHit(inout vec3 pos, inout vec3 vel) {
    vec3 clamped = clamp(pos, uBoxMin, uBoxMax);
    vec3 delta = pos - clamped;
    if (delta == vec3(0.0)) {
        vec3 n = vec3(-1.0, 0.0, 0.0);
        float best = pos.x - uBoxMin.x;
        if (uBoxMax.x - pos.x < best) { best = uBoxMax.x - pos.x; n = vec3(1.0, 0.0, 0.0); }
        if (pos.y - uBoxMin.y < best) { best = pos.y - uBoxMin.y; n = vec3(0.0, -1.0, 0.0); }
        if (uBoxMax.y - pos.y < best) { best = uBoxMax.y - pos.y; n = vec3(0.0, 1.0, 0.0); }
        if (pos.z - uBoxMin.z < best) { best = pos.z - uBoxMin.z; n = vec3(0.0, 0.0, -1.0); }
        if (uBoxMax.z - pos.z < best) { best = uBoxMax.z - pos.z; n = vec3(0.0, 0.0, 1.0); }
        if (n.x < 0.0) pos.x = uBoxMin.x - uParticleRadius;
        else if (n.x > 0.0) pos.x = uBoxMax.x + uParticleRadius;
        else if (n.y < 0.0) pos.y = uBoxMin.y - uParticleRadius;
        else if (n.y > 0.0) pos.y = uBoxMax.y + uParticleRadius;
        else if (n.z < 0.0) pos.z = uBoxMin.z - uParticleRadius;
        else pos.z = uBoxMax.z + uParticleRadius;
        vel = respond(vel, n, uBoxRestitution, uBoxFriction);
        return;
    }
    float dist = length(delta);
    if (dist >= uParticleRadius) return;
    vec3 n = delta / dist;
    pos = clamped + n * uParticleRadius;
    vel = respond(vel, n, uBoxRestitution, uBoxFriction);
}
void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i >= uCount) return;
    Particle pt = particles[i];
    if (pt.velAlive.w == 0.0) return;
    vec3 pos = pt.posAge.xyz;
    vec3 vel = pt.velAlive.xyz;
    if (uSphereEnabled != 0) sphereHit(pos, vel);
    if (uBoxEnabled != 0) boxHit(pos, vel);
    pt.posAge.xyz = pos;
    pt.velAlive.xyz = vel;
    particles[i] = pt;
}
";

	public static uint CreateProgram( GL gl )
	{
		var cs = gl.CreateShader( ShaderType.ComputeShader );
		gl.ShaderSource( cs, ComputeSrc );
		gl.CompileShader( cs );
		gl.GetShader( cs, ShaderParameterName.CompileStatus, out int ok );
		if ( ok == 0 ) { var log = gl.GetShaderInfoLog( cs ); gl.DeleteShader( cs ); throw new InvalidOperationException( "gpu collider compute compile failed:\n" + log ); }
		var prog = gl.CreateProgram();
		gl.AttachShader( prog, cs );
		gl.LinkProgram( prog );
		gl.GetProgram( prog, ProgramPropertyARB.LinkStatus, out int linked );
		gl.DetachShader( prog, cs );
		gl.DeleteShader( cs );
		if ( linked == 0 ) { var log = gl.GetProgramInfoLog( prog ); gl.DeleteProgram( prog ); throw new InvalidOperationException( "gpu collider compute link failed:\n" + log ); }
		return prog;
	}

	// ---- CPU reference implementation (same branches as the GLSL, for tests) ----

	/// <summary>Exact CPU copy of the shader's bounce response.</summary>
	public static Vector3 RespondCpu( Vector3 vel, Vector3 n, float restitution, float friction )
	{
		float vn = Vector3.Dot( vel, n );
		if ( vn >= 0 ) return vel;
		var vt = vel - n * vn;
		return vt * (1f - friction) - n * (vn * restitution);
	}

	static void ValidateResponse( float restitution, float friction )
	{
		if ( !float.IsFinite( restitution ) || restitution < 0 || restitution > 1 ) throw new ArgumentOutOfRangeException( nameof( restitution ), "Restitution must be 0..1." );
		if ( !float.IsFinite( friction ) || friction < 0 || friction > 1 ) throw new ArgumentOutOfRangeException( nameof( friction ), "Friction must be 0..1." );
	}

	static void ValidateRadius( float particleRadius )
	{
		if ( !float.IsFinite( particleRadius ) || particleRadius <= 0 ) throw new ArgumentOutOfRangeException( nameof( particleRadius ), "Particle radius must be > 0." );
	}

	static void ValidateSphere( SphereCollider sphere, float particleRadius )
	{
		ValidateRadius( particleRadius );
		if ( !float.IsFinite( sphere.Radius ) || sphere.Radius < 0 ) throw new ArgumentOutOfRangeException( nameof( sphere ), "Sphere radius must be >= 0." );
		ValidateResponse( sphere.Restitution, sphere.Friction );
	}

	static void ValidateBox( BoxCollider box, float particleRadius )
	{
		ValidateRadius( particleRadius );
		if ( box.Min.X >= box.Max.X || box.Min.Y >= box.Max.Y || box.Min.Z >= box.Max.Z ) throw new ArgumentException( "Box Min must be < Max on every axis." );
		ValidateResponse( box.Restitution, box.Friction );
	}

	static void Validate( SphereCollider? sphere, BoxCollider? box, float particleRadius )
	{
		if ( sphere.HasValue ) ValidateSphere( sphere.Value, particleRadius );
		if ( box.HasValue ) ValidateBox( box.Value, particleRadius );
		if ( !sphere.HasValue && !box.HasValue ) ValidateRadius( particleRadius );
	}

	/// <summary>CPU sphere collision; returns true on a hit. Mirrors sphereHit in the shader.</summary>
	public static bool CollideSphereCpu( ref Vector3 pos, ref Vector3 vel, SphereCollider sphere, float particleRadius )
	{
		ValidateSphere( sphere, particleRadius );
		Vector3 d = pos - sphere.Center;
		float dist = d.Length();
		float eff = sphere.Radius + particleRadius;
		if ( dist >= eff ) return false;
		Vector3 n = dist > 1e-6f ? d / dist : new Vector3( 0, 0, 1 );
		pos = sphere.Center + n * eff;
		vel = RespondCpu( vel, n, sphere.Restitution, sphere.Friction );
		return true;
	}

	/// <summary>CPU box collision; returns true on a hit. Mirrors boxHit in the shader.</summary>
	public static bool CollideBoxCpu( ref Vector3 pos, ref Vector3 vel, BoxCollider box, float particleRadius )
	{
		ValidateBox( box, particleRadius );
		Vector3 clamped = new(
			MathF.Min( MathF.Max( pos.X, box.Min.X ), box.Max.X ),
			MathF.Min( MathF.Max( pos.Y, box.Min.Y ), box.Max.Y ),
			MathF.Min( MathF.Max( pos.Z, box.Min.Z ), box.Max.Z ) );
		Vector3 delta = pos - clamped;
		if ( delta == Vector3.Zero )
		{
			// Inside (or exactly on a face): nearest face wins, first on ties.
			Vector3 n = new( -1, 0, 0 );
			float best = pos.X - box.Min.X;
			if ( box.Max.X - pos.X < best ) { best = box.Max.X - pos.X; n = new Vector3( 1, 0, 0 ); }
			if ( pos.Y - box.Min.Y < best ) { best = pos.Y - box.Min.Y; n = new Vector3( 0, -1, 0 ); }
			if ( box.Max.Y - pos.Y < best ) { best = box.Max.Y - pos.Y; n = new Vector3( 0, 1, 0 ); }
			if ( pos.Z - box.Min.Z < best ) { best = pos.Z - box.Min.Z; n = new Vector3( 0, 0, -1 ); }
			if ( box.Max.Z - pos.Z < best ) { best = box.Max.Z - pos.Z; n = new Vector3( 0, 0, 1 ); }
			if ( n.X < 0 ) pos.X = box.Min.X - particleRadius;
			else if ( n.X > 0 ) pos.X = box.Max.X + particleRadius;
			else if ( n.Y < 0 ) pos.Y = box.Min.Y - particleRadius;
			else if ( n.Y > 0 ) pos.Y = box.Max.Y + particleRadius;
			else if ( n.Z < 0 ) pos.Z = box.Min.Z - particleRadius;
			else pos.Z = box.Max.Z + particleRadius;
			vel = RespondCpu( vel, n, box.Restitution, box.Friction );
			return true;
		}
		float dist = delta.Length();
		if ( dist >= particleRadius ) return false;
		Vector3 n2 = delta / dist;
		pos = clamped + n2 * particleRadius;
		vel = RespondCpu( vel, n2, box.Restitution, box.Friction );
		return true;
	}

	/// <summary>
	/// CPU reference for one collider pass over one particle: sphere first, then box,
	/// dead slots untouched, only position/velocity written. This is the oracle the
	/// GPU tests compare against (with epsilon, never bit-identical).
	/// </summary>
	public static void ApplyCpu( ref ParticleState ps, SphereCollider? sphere, BoxCollider? box, float particleRadius )
	{
		if ( ps.Alive == 0 ) return;
		Validate( sphere, box, particleRadius );
		Vector3 pos = ps.Position, vel = ps.Velocity;
		if ( sphere.HasValue ) CollideSphereCpu( ref pos, ref vel, sphere.Value, particleRadius );
		if ( box.HasValue ) CollideBoxCpu( ref pos, ref vel, box.Value, particleRadius );
		ps.Position = pos;
		ps.Velocity = vel;
	}

	/// <summary>
	/// Run one collider pass over <paramref name="particleCount"/> SSBO slots.
	/// Pass null for a collider to disable it; at least the particle radius is
	/// always validated. Intended to run AFTER <c>GpuParticleSystem.Dispatch</c>
	/// on the same buffer (integration lines reported for the sweep, not edited here).
	/// </summary>
	public static void Dispatch( GL gl, uint program, uint buffer, int particleCount, SphereCollider? sphere, BoxCollider? box, float particleRadius )
	{
		if ( particleCount < 0 ) throw new ArgumentOutOfRangeException( nameof( particleCount ) );
		Validate( sphere, box, particleRadius );
		gl.UseProgram( program );
		void F( string name, float v ) => gl.Uniform1( gl.GetUniformLocation( program, name ), v );
		void V( string name, Vector3 v ) => gl.Uniform3( gl.GetUniformLocation( program, name ), v.X, v.Y, v.Z );
		gl.Uniform1( gl.GetUniformLocation( program, "uCount" ), (uint)particleCount );
		F( "uParticleRadius", particleRadius );
		gl.Uniform1( gl.GetUniformLocation( program, "uSphereEnabled" ), sphere.HasValue ? 1 : 0 );
		V( "uSphereCenter", sphere.HasValue ? sphere.Value.Center : Vector3.Zero );
		F( "uSphereRadius", sphere.HasValue ? sphere.Value.Radius : 0 );
		F( "uSphereRestitution", sphere.HasValue ? sphere.Value.Restitution : 0 );
		F( "uSphereFriction", sphere.HasValue ? sphere.Value.Friction : 0 );
		gl.Uniform1( gl.GetUniformLocation( program, "uBoxEnabled" ), box.HasValue ? 1 : 0 );
		V( "uBoxMin", box.HasValue ? box.Value.Min : Vector3.Zero );
		V( "uBoxMax", box.HasValue ? box.Value.Max : Vector3.Zero );
		F( "uBoxRestitution", box.HasValue ? box.Value.Restitution : 0 );
		F( "uBoxFriction", box.HasValue ? box.Value.Friction : 0 );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 0, buffer );
		uint groups = (uint)((particleCount + LocalSize - 1) / LocalSize);
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
}
