using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>
/// GPU-side particle LOD and culling for the landed GPU parity shader
/// (wave-6 lane P45, particles session, 2026-10-24). Companion module only:
/// it NEVER edits <see cref="GpuParticleSystem"/> and NEVER changes the
/// 64-byte <see cref="ParticleState"/> SSBO layout
/// (<see cref="GpuParticleSystem.StrideFloats"/> = 16, position at offset 0,
/// alive at offset 7). It reads that SSBO and writes three small side
/// buffers a later integrate / spawn step can consume:
///
///   1. Band flags (one uint per particle): Full / Reduced / Collapsed,
///      from camera distance — see <see cref="ClassifyBand"/>.
///   2. Skip masks (one uint per particle): 1 = integrate this dispatch,
///      0 = skip it — Collapsed particles integrate on exactly 1 of every
///      N dispatches, deterministically — see <see cref="ShouldIntegrate"/>.
///   3. Budget counters (two uints): [live count, spawn-allowed count] —
///      counting only, it never kills a live particle — see
///      <see cref="CountBudget"/>.
///
/// BAND SEMANTICS — READ THIS BEFORE CHANGING A THRESHOLD. Lane P35's CPU
/// LOD file (ParticleLod.cs) was NOT present in the canonical tree when
/// this lane ran (checked 2026-10-24: no ParticleLod.cs, no band constants
/// anywhere in the tree), so no landed CPU band values existed to copy.
/// The semantics below are therefore stated as the contract P35 must match
/// when it lands, and every threshold is a caller-supplied parameter, not
/// a baked-in assumption — reconciling with P35 is passing different
/// numbers to the same functions, not rewriting the logic:
///   - Distance is Euclidean camera distance in ParticleState world units
///     (kernel inches, Z-up — the same units the CPU sim integrates in).
///   - The compare is on SQUARED distance on both sides
///     (d2 &lt;= fullMax*fullMax), so the CPU reference and the GLSL agree
///     EXACTLY, bit for bit, with no sqrt-rounding boundary disagreement.
///   - A particle exactly ON a threshold takes the NEARER (higher-detail)
///     band: d &lt;= fullMax is Full, d &lt;= reducedMax is Reduced, else
///     Collapsed. Boundary ties are therefore deterministic, never a
///     coin flip between bands.
///   - Dead slots (Alive == 0) are still band-classified by distance.
///     Liveness is a separate field; the band pass does not conflate
///     "far" with "dead", and the budget pass counts liveness separately.
///   - Defaults (<see cref="DefaultFullMax"/>, <see cref="DefaultReducedMax"/>)
///     are P45's starting proposal only: Full inside 1500, Reduced to 6000,
///     Collapsed beyond. They are defaults, not measurements — a real
///     scene should tune them against the overdraw budget (lane-list §16:
///     "LOD: reduce particle count when distant", §18 J13).
///   - Reduced does NOT throttle integration: only Collapsed particles
///     skip dispatches. Reduced is a render-detail hint (fewer verts,
///     cheaper sprite); the sim still steps Reduced particles every
///     dispatch so they never visibly lag. Collapsed particles are the
///     only ones whose motion is allowed to go stale.
///
/// SKIP SEMANTICS AND POSITION LAG BOUND. A Collapsed particle integrates
/// iff (slotIndex + dispatchCounter) % skipN == 0 — deterministic per
/// particle, never random, and spread evenly: over any N consecutive
/// dispatches each Collapsed particle integrates exactly once, and on any
/// single dispatch exactly ~1/N of them integrate (the residue classes
/// partition the slots). A Collapsed particle is therefore skipped for at
/// most N-1 consecutive dispatches. If its velocity held constant over
/// that window, its position lags a fully-integrated particle by at most
/// (N-1) * dt * |v| — see <see cref="LagBound"/>. With acceleration a
/// the lag grows by roughly 0.5 * |a| * ((N-1)*dt)^2 — see
/// <see cref="LagBoundWithAccel"/> — which is why skipN belongs small
/// (2–4) for fast or accelerating effects and is only safe large for
/// slow, distant ambient clouds. The mask this module writes is a
/// PROPOSAL to the integrate pass: actually skipping requires the sweep
/// integration lines at the bottom of this file (the landed integrate
/// shader does not consult any mask today, and this lane does not edit it).
///
/// BUDGET SEMANTICS. The budget pass counts slots with Alive != 0 via an
/// atomic counter, then a single-thread finalize pass writes
/// spawnAllowed = max(0, budget - live). It does not kill, expire, or
/// rewrite any particle: a system over budget simply spawns nothing new
/// until slots free. Lane P44's spawn pass is the consumer of
/// spawnAllowed; this lane only produces the number.
///
/// uCount GUARD. Every pass checks `i &gt;= uCount` before any read or
/// write, so counts that are not multiples of the workgroup size (256)
/// never touch out-of-range slots — the tail workgroup's extra threads
/// return immediately. The flag/mask buffers are sized exactly `count`
/// entries; no padding is required and none is assumed.
///
/// NOT built here: wiring the skip mask into
/// <see cref="GpuParticleSystem.Dispatch"/> (sweep lines below, coordinator
/// only), GPU spawn/kill (lane P44), the CPU LOD itself (lane P35),
/// save/serialization (lane 5), any change to the landed parity shader,
/// and any his-eye check (headless EGL only).
/// </summary>
public static class GpuParticleLod
{
	/// <summary>Compute workgroup width; matches the landed parity shader.</summary>
	public const int WorkgroupSize = 256;

	/// <summary>Detail flag values written to the flag buffer.</summary>
	public const uint FlagFull = 0, FlagReduced = 1, FlagCollapsed = 2;

	/// <summary>uMode values for the single compute program.</summary>
	public const uint ModeBand = 0, ModeSkip = 1, ModeCount = 2, ModeFinalize = 3;

	/// <summary>
	/// Default band thresholds in ParticleState world units (kernel inches).
	/// Proposal only — see the class doc; pass explicit values to override.
	/// </summary>
	public const float DefaultFullMax = 1500f, DefaultReducedMax = 6000f;

	static readonly string ComputeSrc = @"#version 430 core
layout(local_size_x = 256) in;
layout(std430, binding = 0) readonly buffer States {
    float stateFloats[];
};
layout(std430, binding = 1) buffer Flags {
    uint flags[];
};
layout(std430, binding = 2) buffer Masks {
    uint masks[];
};
layout(std430, binding = 3) buffer Counters {
    uint counters[];
};
uniform uint uMode;
uniform uint uCount;
uniform vec3 uCameraPos;
uniform float uFullMax2;
uniform float uReducedMax2;
uniform uint uDispatch;
uniform uint uSkipN;
uniform uint uBudget;

void main() {
    uint i = gl_GlobalInvocationID.x;
    if (uMode == 3u) {
        // Finalize: single thread turns the atomic live count into a
        // spawn-allowed count. Runs as its own dispatch after the count
        // dispatch + barrier, so no thread races the counter.
        if (i == 0u) {
            counters[1] = counters[0] >= uBudget ? 0u : uBudget - counters[0];
        }
        return;
    }
    // uCount guard: the tail workgroup's extra threads touch nothing,
    // so non-multiples of 256 are safe on every pass below.
    if (i >= uCount) return;
    if (uMode == 0u) {
        uint b = i * 16u;
        vec3 pos = vec3(stateFloats[b + 0u], stateFloats[b + 1u], stateFloats[b + 2u]);
        vec3 d = pos - uCameraPos;
        float d2 = dot(d, d);
        // Squared-distance compare, boundary takes the nearer band.
        flags[i] = d2 <= uFullMax2 ? 0u : (d2 <= uReducedMax2 ? 1u : 2u);
        return;
    }
    if (uMode == 1u) {
        // Collapsed integrates iff (slot + dispatch) % N == 0; every
        // other band integrates every dispatch.
        masks[i] = (flags[i] != 2u || (i + uDispatch) % uSkipN == 0u) ? 1u : 0u;
        return;
    }
    // uMode == 2u: atomic live count. Alive != 0 counts; dead slots and
    // the tail guard above contribute nothing.
    uint b2 = i * 16u;
    if (stateFloats[b2 + 7u] != 0.0) {
        atomicAdd(counters[0], 1u);
    }
}
";

	/// <summary>Throws unless the camera position and thresholds are usable.</summary>
	public static void ValidateBands( Vector3 cameraPosition, float fullMax, float reducedMax )
	{
		if ( !float.IsFinite( cameraPosition.X ) || !float.IsFinite( cameraPosition.Y ) || !float.IsFinite( cameraPosition.Z ) )
			throw new ArgumentException( "Camera position must be finite." );
		if ( !float.IsFinite( fullMax ) || !float.IsFinite( reducedMax ) || fullMax < 0 || reducedMax < fullMax )
			throw new ArgumentOutOfRangeException( nameof( fullMax ), "Band thresholds must be finite, fullMax >= 0, and reducedMax >= fullMax." );
	}

	/// <summary>Throws unless skipN is a usable skip period (1 = no skipping).</summary>
	public static void ValidateSkipN( uint skipN )
	{
		if ( skipN < 1 ) throw new ArgumentOutOfRangeException( nameof( skipN ), "skipN must be >= 1 (1 = integrate every dispatch)." );
	}

	/// <summary>
	/// CPU reference for the band pass. Squared-distance compare, exactly
	/// as the GLSL: d &lt;= fullMax is Full, d &lt;= reducedMax is Reduced,
	/// else Collapsed. Dead slots are classified by distance like any
	/// other slot. Agreement with the GPU is EXACT (integer flags from
	/// identical float32 arithmetic), not epsilon-based.
	/// </summary>
	public static uint ClassifyBand( Vector3 position, Vector3 cameraPosition, float fullMax, float reducedMax )
	{
		ValidateBands( cameraPosition, fullMax, reducedMax );
		Vector3 d = position - cameraPosition;
		float d2 = Vector3.Dot( d, d );
		float f2 = fullMax * fullMax, r2 = reducedMax * reducedMax;
		return d2 <= f2 ? FlagFull : d2 <= r2 ? FlagReduced : FlagCollapsed;
	}

	/// <summary>CPU reference for the band pass over a whole state array.</summary>
	public static uint[] ClassifyAll( ReadOnlySpan<ParticleState> states, Vector3 cameraPosition, float fullMax = DefaultFullMax, float reducedMax = DefaultReducedMax )
	{
		var flags = new uint[states.Length];
		for ( int i = 0; i < states.Length; i++ ) flags[i] = ClassifyBand( states[i].Position, cameraPosition, fullMax, reducedMax );
		return flags;
	}

	/// <summary>
	/// CPU reference for the skip mask: true = integrate this dispatch.
	/// Non-Collapsed particles always integrate; a Collapsed particle
	/// integrates iff (slotIndex + dispatchCounter) % skipN == 0.
	/// Unsigned wraparound matches the GLSL uint arithmetic exactly.
	/// </summary>
	public static bool ShouldIntegrate( uint flag, uint slotIndex, uint dispatchCounter, uint skipN )
	{
		ValidateSkipN( skipN );
		if ( flag != FlagCollapsed ) return true;
		return unchecked( slotIndex + dispatchCounter ) % skipN == 0;
	}

	/// <summary>CPU reference for the skip mask over a whole flag array.</summary>
	public static uint[] SkipMaskAll( ReadOnlySpan<uint> flags, uint dispatchCounter, uint skipN )
	{
		var masks = new uint[flags.Length];
		for ( int i = 0; i < flags.Length; i++ ) masks[i] = ShouldIntegrate( flags[i], (uint)i, dispatchCounter, skipN ) ? 1u : 0u;
		return masks;
	}

	/// <summary>CPU reference for the budget count: slots with Alive != 0.</summary>
	public static int CountLive( ReadOnlySpan<ParticleState> states )
	{
		int live = 0;
		for ( int i = 0; i < states.Length; i++ ) if ( states[i].Alive != 0 ) live++;
		return live;
	}

	/// <summary>CPU reference for the finalize pass: spawns allowed under a budget, never negative, never kills.</summary>
	public static int SpawnAllowed( int live, int budget )
	{
		if ( budget < 0 ) throw new ArgumentOutOfRangeException( nameof( budget ), "Budget must be >= 0." );
		if ( live < 0 ) throw new ArgumentOutOfRangeException( nameof( live ), "Live count must be >= 0." );
		return live >= budget ? 0 : budget - live;
	}

	/// <summary>
	/// Position lag bound for a Collapsed particle skipped at most skipN-1
	/// consecutive dispatches, in world units: (skipN-1) * dt * maxSpeed.
	/// Exact under constant velocity over the window; with acceleration
	/// use <see cref="LagBoundWithAccel"/>.
	/// </summary>
	public static float LagBound( uint skipN, float dt, float maxSpeed )
	{
		ValidateSkipN( skipN );
		if ( !float.IsFinite( dt ) || dt < 0 || !float.IsFinite( maxSpeed ) || maxSpeed < 0 )
			throw new ArgumentOutOfRangeException( nameof( dt ), "dt and maxSpeed must be finite and >= 0." );
		return ( skipN - 1 ) * dt * maxSpeed;
	}

	/// <summary>
	/// Lag bound with acceleration: (skipN-1)*dt*maxSpeed +
	/// 0.5*maxAccel*((skipN-1)*dt)^2. The second term is the extra drift
	/// from velocity changing while the particle is skipped.
	/// </summary>
	public static float LagBoundWithAccel( uint skipN, float dt, float maxSpeed, float maxAccel )
	{
		float window = ( skipN - 1 ) * dt;
		if ( !float.IsFinite( maxAccel ) || maxAccel < 0 )
			throw new ArgumentOutOfRangeException( nameof( maxAccel ), "maxAccel must be finite and >= 0." );
		return LagBound( skipN, dt, maxSpeed ) + 0.5f * maxAccel * window * window;
	}

	public static uint CreateProgram( GL gl )
	{
		var cs = gl.CreateShader( ShaderType.ComputeShader );
		gl.ShaderSource( cs, ComputeSrc );
		gl.CompileShader( cs );
		gl.GetShader( cs, ShaderParameterName.CompileStatus, out int ok );
		if ( ok == 0 ) { var log = gl.GetShaderInfoLog( cs ); gl.DeleteShader( cs ); throw new InvalidOperationException( "LOD compute compile failed:\n" + log ); }
		var prog = gl.CreateProgram();
		gl.AttachShader( prog, cs );
		gl.LinkProgram( prog );
		gl.GetProgram( prog, ProgramPropertyARB.LinkStatus, out int linked );
		gl.DetachShader( prog, cs );
		gl.DeleteShader( cs );
		if ( linked == 0 ) { var log = gl.GetProgramInfoLog( prog ); gl.DeleteProgram( prog ); throw new InvalidOperationException( "LOD compute link failed:\n" + log ); }
		return prog;
	}

	static uint UploadUints( GL gl, uint[] data )
	{
		uint buf = gl.GenBuffer();
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, buf );
		unsafe
		{
			fixed ( uint* ptr = data )
			{
				gl.BufferData( BufferTargetARB.ShaderStorageBuffer, (nuint)(data.Length * sizeof( uint )), ptr, BufferUsageARB.DynamicDraw );
			}
		}
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, 0 );
		return buf;
	}

	static uint[] DownloadUints( GL gl, uint buffer, int count )
	{
		var data = new uint[count];
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, buffer );
		unsafe
		{
			fixed ( uint* ptr = data )
			{
				gl.GetBufferSubData( BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(data.Length * sizeof( uint )), ptr );
			}
		}
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, 0 );
		return data;
	}

	/// <summary>Allocate a zeroed flag buffer of <paramref name="count"/> entries (binding 1).</summary>
	public static uint CreateFlagBuffer( GL gl, int count )
	{
		if ( count < 0 ) throw new ArgumentOutOfRangeException( nameof( count ) );
		return UploadUints( gl, new uint[count] );
	}

	/// <summary>Allocate a zeroed skip-mask buffer of <paramref name="count"/> entries (binding 2).</summary>
	public static uint CreateMaskBuffer( GL gl, int count )
	{
		if ( count < 0 ) throw new ArgumentOutOfRangeException( nameof( count ) );
		return UploadUints( gl, new uint[count] );
	}

	/// <summary>Allocate the 2-uint budget counter buffer [live, spawnAllowed] (binding 3), zeroed.</summary>
	public static uint CreateCounterBuffer( GL gl ) => UploadUints( gl, new uint[2] );

	/// <summary>Read back flag values for <paramref name="count"/> particles.</summary>
	public static uint[] DownloadFlags( GL gl, uint flagBuffer, int count ) => DownloadUints( gl, flagBuffer, count );

	/// <summary>Read back skip-mask values for <paramref name="count"/> particles.</summary>
	public static uint[] DownloadMasks( GL gl, uint maskBuffer, int count ) => DownloadUints( gl, maskBuffer, count );

	/// <summary>Read back [live, spawnAllowed] from a budget counter buffer.</summary>
	public static (uint live, uint spawnAllowed) DownloadCounters( GL gl, uint counterBuffer )
	{
		var c = DownloadUints( gl, counterBuffer, 2 );
		return ( c[0], c[1] );
	}

	static void SetCommon( GL gl, uint program, uint mode, uint count )
	{
		gl.UseProgram( program );
		gl.Uniform1( gl.GetUniformLocation( program, "uMode" ), mode );
		gl.Uniform1( gl.GetUniformLocation( program, "uCount" ), count );
	}

	static void DispatchGroups( GL gl, uint count )
	{
		uint groups = ( count + (uint)WorkgroupSize - 1u ) / (uint)WorkgroupSize;
		if ( groups == 0 ) groups = 1;
		gl.DispatchCompute( groups, 1, 1 );
		gl.MemoryBarrier( MemoryBarrierMask.ShaderStorageBarrierBit );
	}

	static void UnbindAll( GL gl )
	{
		for ( uint b = 0; b < 4; b++ ) gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, b, 0 );
	}

	/// <summary>
	/// Band pass: writes one detail flag per particle from camera distance.
	/// Binding 0 = state SSBO (from <see cref="GpuParticleSystem.UploadStates"/>),
	/// binding 1 = flag buffer. Thresholds are squared on the host in float32,
	/// exactly as <see cref="ClassifyBand"/> squares them.
	/// </summary>
	public static void ClassifyBands( GL gl, uint program, uint stateBuffer, uint flagBuffer, int count, Vector3 cameraPosition, float fullMax = DefaultFullMax, float reducedMax = DefaultReducedMax )
	{
		if ( count < 0 ) throw new ArgumentOutOfRangeException( nameof( count ) );
		ValidateBands( cameraPosition, fullMax, reducedMax );
		SetCommon( gl, program, ModeBand, (uint)count );
		gl.Uniform3( gl.GetUniformLocation( program, "uCameraPos" ), cameraPosition.X, cameraPosition.Y, cameraPosition.Z );
		gl.Uniform1( gl.GetUniformLocation( program, "uFullMax2" ), fullMax * fullMax );
		gl.Uniform1( gl.GetUniformLocation( program, "uReducedMax2" ), reducedMax * reducedMax );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 0, stateBuffer );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 1, flagBuffer );
		DispatchGroups( gl, (uint)count );
		UnbindAll( gl );
	}

	/// <summary>
	/// Skip pass: writes one mask value per particle (1 = integrate this
	/// dispatch, 0 = skip). Binding 1 = flag buffer, binding 2 = mask buffer.
	/// This pass only PRODUCES the mask; the integrate shader consults it
	/// only after the sweep integration lines below are applied.
	/// </summary>
	public static void ComputeSkipMask( GL gl, uint program, uint flagBuffer, uint maskBuffer, int count, uint dispatchCounter, uint skipN )
	{
		if ( count < 0 ) throw new ArgumentOutOfRangeException( nameof( count ) );
		ValidateSkipN( skipN );
		SetCommon( gl, program, ModeSkip, (uint)count );
		gl.Uniform1( gl.GetUniformLocation( program, "uDispatch" ), dispatchCounter );
		gl.Uniform1( gl.GetUniformLocation( program, "uSkipN" ), skipN );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 1, flagBuffer );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 2, maskBuffer );
		DispatchGroups( gl, (uint)count );
		UnbindAll( gl );
	}

	/// <summary>
	/// Budget pass: counts live slots (atomic counter) and writes
	/// [live, spawnAllowed] into <paramref name="counterBuffer"/>.
	/// The counter buffer is re-zeroed on the host before the count
	/// dispatch, so repeated calls do not accumulate. Binding 0 = state
	/// SSBO, binding 3 = counter buffer. Counting only — no particle is
	/// killed, expired, or rewritten by this pass.
	/// </summary>
	public static void CountBudget( GL gl, uint program, uint stateBuffer, uint counterBuffer, int count, uint budget )
	{
		if ( count < 0 ) throw new ArgumentOutOfRangeException( nameof( count ) );
		unsafe
		{
			uint zero0 = 0, zero1 = 0;
			gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, counterBuffer );
			gl.BufferSubData( BufferTargetARB.ShaderStorageBuffer, 0, (nuint)sizeof( uint ), &zero0 );
			gl.BufferSubData( BufferTargetARB.ShaderStorageBuffer, (nint)sizeof( uint ), (nuint)sizeof( uint ), &zero1 );
			gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, 0 );
		}
		SetCommon( gl, program, ModeCount, (uint)count );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 0, stateBuffer );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 3, counterBuffer );
		DispatchGroups( gl, (uint)count );
		// Finalize runs as its own dispatch after the barrier above, so
		// the single thread reads the finished atomic count.
		SetCommon( gl, program, ModeFinalize, 0 );
		gl.Uniform1( gl.GetUniformLocation( program, "uBudget" ), budget );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 3, counterBuffer );
		gl.DispatchCompute( 1, 1, 1 );
		gl.MemoryBarrier( MemoryBarrierMask.ShaderStorageBarrierBit );
		UnbindAll( gl );
	}

	public static void Dispose( GL gl, uint program, params uint[] buffers )
	{
		foreach ( uint b in buffers ) if ( b != 0 ) gl.DeleteBuffer( b );
		if ( program != 0 ) gl.DeleteProgram( program );
	}
}

/// <summary>
/// EXACT SWEEP INTEGRATION LINES (coordinator only — this lane does NOT
/// edit GpuParticleSystem.cs). To make the integrate pass actually skip
/// Collapsed particles, apply these three changes together; any one alone
/// is a no-op or a break:
///
/// 1. In GpuParticleSystem's ComputeSrc, after the `Particles` SSBO block,
///    add:
///        layout(std430, binding = 1) buffer SkipMasks {
///            uint skipMasks[];
///        };
///    and in main(), immediately after `if (i &gt;= uCount) return;`, add:
///        if (skipMasks[i] == 0u) return;
///    (Binding 1 is free: the landed shader binds only binding 0.)
/// 2. In GpuParticleSystem.Dispatch, after the existing
///    `gl.BindBufferBase(..., 0, buffer);` line, add a `uint maskBuffer`
///    parameter to the method and bind it:
///        gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 1, maskBuffer );
///    and unbind binding 1 alongside binding 0 at the end. Callers that
///    have no LOD mask pass a buffer filled with 1s (integrate everything)
///    so existing behaviour is unchanged by default.
/// 3. Per frame, before Dispatch, run in order:
///        GpuParticleLod.ClassifyBands( gl, lodProg, stateBuf, flagBuf, count, cameraPos );
///        GpuParticleLod.ComputeSkipMask( gl, lodProg, flagBuf, maskBuf, count, dispatchCounter, skipN );
///    with dispatchCounter incremented once per Dispatch call, and pass
///    maskBuf into Dispatch. The budget pass is independent and runs
///    whenever the spawn pass needs a fresh spawn-allowed count:
///        GpuParticleLod.CountBudget( gl, lodProg, stateBuf, counterBuf, count, budget );
///    then lane P44's spawn pass reads counters[1].
/// </summary>
internal static class GpuParticleLodSweepNote { }
