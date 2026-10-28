using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>
/// GPU particle spawn/kill path (wave-6 lane P44, particles session, 2026-10-24).
/// Companion compute module to <see cref="GpuParticleSystem"/>: it closes the GPU
/// loop lane 3 left open (lane 3's doc states emission/expiry are CPU-only "by
/// design" — this file is the GPU side of that same contract, it does NOT edit
/// <see cref="GpuParticleSystem"/> and NEVER changes the 64-byte
/// <see cref="ParticleState"/> SSBO layout (<see cref="GpuParticleSystem.StrideFloats"/>
/// = 16, position at offset 0, age at offset 3, velocity at offset 4, alive at
/// offset 7, size at offset 15).
///
/// TWO PASSES, ONE PROGRAM (uMode selects):
///   - Spawn (mode 0): fills dead slots with fresh live particles from a caller
///     supplied request list, capped by a caller-supplied budget. The budget is
///     normally P45's spawn-allowed count (<see cref="GpuParticleLod.CountBudget"/>
///     writes it to its counter buffer's counters[1]); any caller count works.
///   - Kill (mode 1): marks slots dead whose age has reached their lifetime.
///     Lifetime is NOT in ParticleState (age only, no lifetime — P46 hit the same
///     fact), so lifetimes arrive as a caller-supplied per-slot float buffer,
///     one entry per slot, in slot order. There is no per-dispatch uniform
///     lifetime overload: a single lifetime cannot express the CPU's per-slot
///     lifetime ranges, and inventing one would silently expire mixed-lifetime
///     systems wrong. The caller owns the lifetime buffer; it is usually built
///     from the CPU system's per-slot lifetimes at upload time.
///
/// SLOT-CLAIMING RULE (spawn) — LOWEST FREE SLOT FIRST, PROVEN, NOT ATOMIC.
/// Spawn is a single-thread scan (thread 0 of one workgroup) over slots in
/// ascending order: the k-th free slot found (0-based) receives request k
/// (0-based, request order preserved), stopping when spawned == budget, or
/// requests run out, or free slots run out. No atomics, no races, no
/// thread-scheduling dependence: the same inputs always produce the same
/// slot assignment on every GPU. Cost is one sequential scan per dispatch;
/// that is the honest price of exact lowest-first semantics — a parallel
/// atomic freelist would be faster at huge counts but could NOT promise
/// which request lands in which slot, and this lane promises exactly that.
/// Spawn therefore runs once per step (not per substep) in the intended
/// wiring below, where one scan is negligible next to the integrate passes.
///
/// CAP RULE (spawn) — CAPPED, REPORTED, NEVER A SILENT MYSTERY, NEVER AN ERROR.
/// Spawned = min(budget, requestCount, freeSlotCount). Requesting more than
/// the budget allows, or more than capacity holds, is the NORMAL case (that
/// is what a budget is for, and it mirrors the CPU's DroppedForCapacity drop
/// on a full pool) — it is not an error, and the exact spawned count is
/// written to the spawned-counter buffer and returned by
/// <see cref="DispatchSpawn"/>, so no caller ever has to guess how many
/// landed. What DOES fail loudly (ArgumentException family, before any
/// dispatch) is malformed input: negative counts, negative budget, null/zero
/// buffers where a buffer is required, non-finite request fields, negative
/// sizes, and non-finite / non-positive lifetimes at upload time. See
/// <see cref="ValidateRequest"/> and <see cref="UploadLifetimes"/>.
///
/// KILL RULE — AGE &gt;= LIFETIME, CPU KillSlot SHAPE, ONE DOCUMENTED DELTA.
/// A live slot dies iff Age &gt;= its per-slot lifetime. On death the shader
/// writes Alive = 0 and Velocity = (0, 0, 0) and leaves every other field
/// byte-identical — exactly what the CPU's KillSlot does to the state
/// (Alive = 0, Velocity = Zero; position, age, rotation, angular velocity,
/// size untouched). Dead slots are never touched by either pass. DELTA VS
/// CPU BeginStep, STATED PLAINLY: the CPU expires by step index
/// (deathStep = ceil(lifetime / dt - 1e-6)), so for a lifetime that is an
/// exact multiple of the fixed step the CPU kills one substep earlier than
/// the age &gt;= lifetime rule would. The GPU rule here is the age rule the
/// lane brief specifies, and the CPU reference in this file implements the
/// same age rule — GPU-vs-CPU agreement in the tests is against THIS
/// file's oracle, not against BeginStep's step-quantized expiry. Do not
/// compare GPU kill timing against BeginStep and expect bit equality in
/// that epsilon edge case.
///
/// AGREEMENT POLICY. GPU-vs-CPU agreement for both passes is EXACT (flags
/// and floats), not epsilon-based: spawn writes are verbatim copies of
/// request fields plus the constants age 0 / alive 1, and kill writes only
/// the constants alive 0 / velocity 0 after a comparison. No arithmetic is
/// performed on particle data, so there is nothing for GLSL-vs-C# rounding
/// to disagree about. (Force passes such as colliders/turbulence need
/// epsilon; this one does not, and claiming epsilon here would hide bugs.)
///
/// uCount GUARD. Kill checks `i &gt;= uCount` before any read or write, so
/// counts that are not multiples of the workgroup size (256) never touch
/// out-of-range slots. Spawn's single thread loops `s &lt; uCount` only.
/// Slots past the count are never touched by either pass — proven by the
/// sentinel test at count 257, following P42's guard test.
///
/// BUFFER BINDINGS (one program, modes share bindings by dispatch, never
/// simultaneously): binding 0 = state SSBO (16 floats/slot, parity layout);
/// binding 1 = aux float buffer — spawn mode reads it as request records
/// (16 floats/request, same field order as the state: position xyz, ignored
/// slot, velocity xyz, ignored slot, rotation xyzw, angular velocity xyz,
/// size); kill mode reads it as lifetimes (1 float/slot). Binding 2 (spawn
/// only) = spawned-counter buffer (1 uint, written by thread 0).
///
/// INTENDED WIRING (sweep lines only — this lane edits nothing existing).
/// Per substep, in order, on the SAME state SSBO:
///   1. GpuParticleSpawn.DispatchSpawn (this file) — BEFORE parity Dispatch,
///      fed by P45's budget: GpuParticleLod.CountBudget(..., budget) then
///      read counters[1] as the spawn budget. Fresh particles then integrate
///      in the same substep, matching CPU BeginStep-then-SimulateCpu order.
///   2. GpuParticleSystem.Dispatch (parity integrate).
///   3. GpuParticleSpawn.DispatchKill (this file) — AFTER parity Dispatch.
///      Slots freed here are claimed by the NEXT substep's spawn pass, which
///      is the same slot-reuse timing as CPU BeginStep kill-then-spawn.
/// Kill may also run standalone (no spawn) whenever expiry alone is needed.
///
/// NOT built here: per-particle collision/death events or callbacks (a GPU
/// system cannot raise them, same as the parity shader); death sub-emitters
/// (CPU FlushPendingChildren has no GPU form); strand-history writes;
/// mesh colliders in GLSL (P42 owns sphere/box; mesh explicitly out);
/// GPU strand/trail buffer (P43); GPU LOD itself (P45 — this file only
/// CONSUMES its budget count); CPU spawn/step changes; save/serialization;
/// any live-cabinet check (headless EGL only).
/// </summary>
public static class GpuParticleSpawn
{
	/// <summary>Same 64-byte ParticleState layout as the parity shader.</summary>
	public const int StrideFloats = GpuParticleSystem.StrideFloats;
	/// <summary>Workgroup size; must match local_size_x in the shader.</summary>
	public const int WorkgroupSize = 256;
	/// <summary>uMode values for the single compute program.</summary>
	public const uint ModeSpawn = 0, ModeKill = 1;

	/// <summary>
	/// One spawn request: the fresh particle's initial fields. Age is forced
	/// to 0 and Alive to 1 by the spawn pass; the request carries no age or
	/// alive value, so a caller cannot spawn a pre-aged or dead particle.
	/// Rotation is copied verbatim (not normalized) — the CPU spawn path
	/// writes unit quaternions by construction, and verbatim copy keeps
	/// GPU == CPU exact.
	/// </summary>
	public struct SpawnRequest
	{
		public Vector3 Position;
		public Vector3 Velocity;
		public Quaternion Rotation;
		public Vector3 AngularVelocity;
		public float Size;
	}

	/// <summary>Build a spawn request from a CPU state, copying its live fields and ignoring Age/Alive.</summary>
	public static SpawnRequest FromState( ParticleState s ) => new()
	{
		Position = s.Position, Velocity = s.Velocity, Rotation = s.Rotation,
		AngularVelocity = s.AngularVelocity, Size = s.Size,
	};

	static readonly string ComputeSrc = @"
#version 430 core
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
layout(std430, binding = 1) buffer Aux {
    float auxFloats[];
};
layout(std430, binding = 2) buffer Spawned {
    uint spawnedOut[];
};
uniform uint uMode;
uniform uint uCount;
uniform uint uBudget;
uniform uint uRequestCount;

void main() {
    uint i = gl_GlobalInvocationID.x;
    if (uMode == 1u) {
        // Kill: one thread per slot, uCount guard first.
        if (i >= uCount) return;
        Particle pt = particles[i];
        if (pt.velAlive.w == 0.0) return;
        if (pt.posAge.w >= auxFloats[i]) {
            pt.velAlive.w = 0.0;
            pt.velAlive.xyz = vec3(0.0);
            particles[i] = pt;
        }
        return;
    }
    // Spawn: thread 0 only, ascending slot scan = lowest free slot first,
    // request order preserved, capped by budget / request count / free slots.
    if (i != 0u) return;
    uint spawned = 0u;
    for (uint s = 0u; s < uCount; s++) {
        if (spawned >= uBudget || spawned >= uRequestCount) break;
        Particle pt = particles[s];
        if (pt.velAlive.w != 0.0) continue;
        uint b = spawned * 16u;
        pt.posAge.xyz = vec3(auxFloats[b], auxFloats[b + 1u], auxFloats[b + 2u]);
        pt.posAge.w = 0.0;
        pt.velAlive.xyz = vec3(auxFloats[b + 4u], auxFloats[b + 5u], auxFloats[b + 6u]);
        pt.velAlive.w = 1.0;
        pt.rot = vec4(auxFloats[b + 8u], auxFloats[b + 9u], auxFloats[b + 10u], auxFloats[b + 11u]);
        pt.angSize.xyz = vec3(auxFloats[b + 12u], auxFloats[b + 13u], auxFloats[b + 14u]);
        pt.angSize.w = auxFloats[b + 15u];
        particles[s] = pt;
        spawned++;
    }
    spawnedOut[0] = spawned;
}
";

	public static uint CreateProgram( GL gl )
	{
		var cs = gl.CreateShader( ShaderType.ComputeShader );
		gl.ShaderSource( cs, ComputeSrc );
		gl.CompileShader( cs );
		gl.GetShader( cs, ShaderParameterName.CompileStatus, out int ok );
		if ( ok == 0 ) { var log = gl.GetShaderInfoLog( cs ); gl.DeleteShader( cs ); throw new InvalidOperationException( "gpu spawn compute compile failed:\n" + log ); }
		var prog = gl.CreateProgram();
		gl.AttachShader( prog, cs );
		gl.LinkProgram( prog );
		gl.GetProgram( prog, ProgramPropertyARB.LinkStatus, out int linked );
		gl.DetachShader( prog, cs );
		gl.DeleteShader( cs );
		if ( linked == 0 ) { var log = gl.GetProgramInfoLog( prog ); gl.DeleteProgram( prog ); throw new InvalidOperationException( "gpu spawn compute link failed:\n" + log ); }
		return prog;
	}

	// ---- Validation (loud, before any dispatch) ----

	static bool Finite( Vector3 v ) => float.IsFinite( v.X ) && float.IsFinite( v.Y ) && float.IsFinite( v.Z );
	static bool Finite( Quaternion q ) => float.IsFinite( q.X ) && float.IsFinite( q.Y ) && float.IsFinite( q.Z ) && float.IsFinite( q.W );

	/// <summary>Throws unless a spawn request is usable: all fields finite, size &gt;= 0.</summary>
	public static void ValidateRequest( SpawnRequest r )
	{
		if ( !Finite( r.Position ) || !Finite( r.Velocity ) || !Finite( r.Rotation ) || !Finite( r.AngularVelocity ) )
			throw new ArgumentException( "Spawn request position, velocity, rotation and angular velocity must all be finite." );
		if ( !float.IsFinite( r.Size ) || r.Size < 0 )
			throw new ArgumentOutOfRangeException( nameof( r ), "Spawn request size must be finite and >= 0." );
	}

	/// <summary>Throws unless a lifetime is usable: finite and &gt; 0 (the CPU emitter invariant: LifetimeMin &gt; 0).</summary>
	public static void ValidateLifetime( float lifetime )
	{
		if ( !float.IsFinite( lifetime ) || lifetime <= 0 )
			throw new ArgumentOutOfRangeException( nameof( lifetime ), "Lifetime must be finite and > 0." );
	}

	static void ValidateCounts( int count, int budget, int requestCount )
	{
		if ( count < 0 ) throw new ArgumentOutOfRangeException( nameof( count ), "Particle count must be >= 0." );
		if ( budget < 0 ) throw new ArgumentOutOfRangeException( nameof( budget ), "Spawn budget must be >= 0." );
		if ( requestCount < 0 ) throw new ArgumentOutOfRangeException( nameof( requestCount ), "Request count must be >= 0." );
	}

	// ---- CPU reference implementation (test oracle: same rules as the GLSL) ----

	/// <summary>
	/// CPU spawn: lowest free slot first, request order preserved, spawned =
	/// min(budget, requests.Length, free slots). Fresh slots get Age 0, Alive 1
	/// and the request's fields verbatim. Live slots and dead slots beyond the
	/// spawned set are untouched. Returns the spawned count.
	/// </summary>
	public static int SpawnCpu( Span<ParticleState> states, ReadOnlySpan<SpawnRequest> requests, int budget )
	{
		if ( budget < 0 ) throw new ArgumentOutOfRangeException( nameof( budget ), "Spawn budget must be >= 0." );
		for ( int i = 0; i < requests.Length; i++ ) ValidateRequest( requests[i] );
		int spawned = 0;
		for ( int s = 0; s < states.Length; s++ )
		{
			if ( spawned >= budget || spawned >= requests.Length ) break;
			if ( states[s].Alive != 0 ) continue;
			var r = requests[spawned];
			states[s].Position = r.Position;
			states[s].Velocity = r.Velocity;
			states[s].Rotation = r.Rotation;
			states[s].AngularVelocity = r.AngularVelocity;
			states[s].Size = r.Size;
			states[s].Age = 0;
			states[s].Alive = 1;
			spawned++;
		}
		return spawned;
	}

	/// <summary>
	/// CPU kill for one slot: live and Age &gt;= lifetime dies (Alive = 0,
	/// Velocity = Zero, everything else untouched); dead slots and unexpired
	/// slots are untouched. Returns true iff this slot died here.
	/// </summary>
	public static bool KillSlotCpu( ref ParticleState ps, float lifetime )
	{
		if ( ps.Alive == 0 ) return false;
		ValidateLifetime( lifetime );
		if ( ps.Age < lifetime ) return false;
		ps.Alive = 0;
		ps.Velocity = Vector3.Zero;
		return true;
	}

	/// <summary>
	/// CPU kill over a whole state array against a per-slot lifetime array.
	/// Lengths must match exactly (a missing lifetime is a caller bug, not a
	/// default) — mismatches throw. Returns the killed count.
	/// </summary>
	public static int KillCpu( Span<ParticleState> states, ReadOnlySpan<float> lifetimes )
	{
		if ( lifetimes.Length != states.Length )
			throw new ArgumentException( $"Lifetime count {lifetimes.Length} must match state count {states.Length}." );
		for ( int i = 0; i < lifetimes.Length; i++ ) ValidateLifetime( lifetimes[i] );
		int killed = 0;
		for ( int i = 0; i < states.Length; i++ ) if ( KillSlotCpu( ref states[i], lifetimes[i] ) ) killed++;
		return killed;
	}

	// ---- Host buffer helpers ----

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
	/// Pack spawn requests into the aux buffer layout (16 floats/request, same
	/// field order as the state; the two ignored slots are zeroed). Validates
	/// every request — a bad request fails here, never mid-dispatch.
	/// </summary>
	public static uint UploadRequests( GL gl, ReadOnlySpan<SpawnRequest> requests )
	{
		var data = new float[requests.Length * StrideFloats];
		for ( int i = 0; i < requests.Length; i++ )
		{
			ValidateRequest( requests[i] );
			int b = i * StrideFloats;
			var r = requests[i];
			data[b + 0] = r.Position.X; data[b + 1] = r.Position.Y; data[b + 2] = r.Position.Z; data[b + 3] = 0;
			data[b + 4] = r.Velocity.X; data[b + 5] = r.Velocity.Y; data[b + 6] = r.Velocity.Z; data[b + 7] = 0;
			data[b + 8] = r.Rotation.X; data[b + 9] = r.Rotation.Y; data[b + 10] = r.Rotation.Z; data[b + 11] = r.Rotation.W;
			data[b + 12] = r.AngularVelocity.X; data[b + 13] = r.AngularVelocity.Y; data[b + 14] = r.AngularVelocity.Z; data[b + 15] = r.Size;
		}
		return UploadFloats( gl, data );
	}

	/// <summary>
	/// Pack per-slot lifetimes into the aux buffer layout (1 float/slot, slot
	/// order). Validates every lifetime — finite and &gt; 0 — because the kill
	/// dispatch itself cannot validate buffer contents.
	/// </summary>
	public static uint UploadLifetimes( GL gl, ReadOnlySpan<float> lifetimes )
	{
		for ( int i = 0; i < lifetimes.Length; i++ ) ValidateLifetime( lifetimes[i] );
		return UploadFloats( gl, lifetimes.ToArray() );
	}

	/// <summary>Allocate the 1-uint spawned-counter buffer (binding 2), zeroed.</summary>
	public static uint CreateSpawnedBuffer( GL gl )
	{
		uint buf = gl.GenBuffer();
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, buf );
		unsafe
		{
			uint zero = 0;
			gl.BufferData( BufferTargetARB.ShaderStorageBuffer, (nuint)sizeof( uint ), &zero, BufferUsageARB.DynamicDraw );
		}
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, 0 );
		return buf;
	}

	/// <summary>Read back the spawned count written by the last spawn dispatch.</summary>
	public static uint DownloadSpawned( GL gl, uint spawnedBuffer )
	{
		if ( spawnedBuffer == 0 ) throw new ArgumentException( "Spawned buffer handle must be non-zero." );
		var data = new uint[1];
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, spawnedBuffer );
		unsafe
		{
			fixed ( uint* ptr = data )
			{
				gl.GetBufferSubData( BufferTargetARB.ShaderStorageBuffer, 0, (nuint)sizeof( uint ), ptr );
			}
		}
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, 0 );
		return data[0];
	}

	static void ZeroCounter( GL gl, uint spawnedBuffer )
	{
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, spawnedBuffer );
		unsafe
		{
			uint zero = 0;
			gl.BufferSubData( BufferTargetARB.ShaderStorageBuffer, 0, (nuint)sizeof( uint ), &zero );
		}
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, 0 );
	}

	static void UnbindAll( GL gl )
	{
		for ( uint b = 0; b < 3; b++ ) gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, b, 0 );
	}

	/// <summary>
	/// Spawn dispatch: claims the lowest free slots for the first
	/// min(budget, requestCount, free) requests, in request order. Returns the
	/// spawned count (also written to <paramref name="spawnedBuffer"/>).
	/// Zero-count, zero-budget, or zero-request calls dispatch NOTHING: the
	/// counter is zeroed and 0 returned, the state buffer untouched.
	/// </summary>
	public static int DispatchSpawn( GL gl, uint program, uint stateBuffer, uint requestBuffer, uint spawnedBuffer, int count, int budget, int requestCount )
	{
		ValidateCounts( count, budget, requestCount );
		if ( spawnedBuffer == 0 ) throw new ArgumentException( "Spawned buffer handle must be non-zero." );
		if ( count == 0 || budget == 0 || requestCount == 0 )
		{
			ZeroCounter( gl, spawnedBuffer );
			return 0;
		}
		if ( stateBuffer == 0 ) throw new ArgumentException( "State buffer handle must be non-zero." );
		if ( requestBuffer == 0 ) throw new ArgumentException( "Request buffer handle must be non-zero." );
		gl.UseProgram( program );
		gl.Uniform1( gl.GetUniformLocation( program, "uMode" ), ModeSpawn );
		gl.Uniform1( gl.GetUniformLocation( program, "uCount" ), (uint)count );
		gl.Uniform1( gl.GetUniformLocation( program, "uBudget" ), (uint)budget );
		gl.Uniform1( gl.GetUniformLocation( program, "uRequestCount" ), (uint)requestCount );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 0, stateBuffer );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 1, requestBuffer );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 2, spawnedBuffer );
		gl.DispatchCompute( 1, 1, 1 );
		gl.MemoryBarrier( MemoryBarrierMask.ShaderStorageBarrierBit );
		UnbindAll( gl );
		return (int)DownloadSpawned( gl, spawnedBuffer );
	}

	/// <summary>
	/// Kill dispatch: marks every live slot with Age &gt;= its per-slot lifetime
	/// dead (Alive = 0, Velocity = Zero, all other fields untouched). Dead
	/// slots and unexpired slots are untouched. A zero count dispatches
	/// nothing and leaves the buffer untouched.
	/// </summary>
	public static void DispatchKill( GL gl, uint program, uint stateBuffer, uint lifetimeBuffer, int count )
	{
		if ( count < 0 ) throw new ArgumentOutOfRangeException( nameof( count ), "Particle count must be >= 0." );
		if ( count == 0 ) return;
		if ( stateBuffer == 0 ) throw new ArgumentException( "State buffer handle must be non-zero." );
		if ( lifetimeBuffer == 0 ) throw new ArgumentException( "Lifetime buffer handle must be non-zero." );
		gl.UseProgram( program );
		gl.Uniform1( gl.GetUniformLocation( program, "uMode" ), ModeKill );
		gl.Uniform1( gl.GetUniformLocation( program, "uCount" ), (uint)count );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 0, stateBuffer );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 1, lifetimeBuffer );
		uint groups = (uint)((count + WorkgroupSize - 1) / WorkgroupSize);
		gl.DispatchCompute( groups, 1, 1 );
		gl.MemoryBarrier( MemoryBarrierMask.ShaderStorageBarrierBit );
		UnbindAll( gl );
	}

	public static void Dispose( GL gl, uint program, params uint[] buffers )
	{
		foreach ( uint b in buffers ) if ( b != 0 ) gl.DeleteBuffer( b );
		if ( program != 0 ) gl.DeleteProgram( program );
	}
}
