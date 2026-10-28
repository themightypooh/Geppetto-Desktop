using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>
/// GPU strand/trail buffer: a companion compute pass to <see cref="GpuParticleSystem"/>.
///
/// This module is deliberately NOT wired into <see cref="GpuParticleSystem.Dispatch"/>.
/// The coordinator sweep owns that hookup (see the integration lines in the lane report):
/// the intended call is one <see cref="Dispatch"/> of this module per substep, AFTER the
/// parity <c>Dispatch</c>, on the SAME particle SSBO, so the recorded position is the
/// integrated position for that substep. Strand rendering (a later lane) reads the
/// trail buffer; nothing here draws anything.
///
/// Layout. One trail buffer holds, per particle slot, a ring of past positions:
/// <c>particleCount * trailLength</c> vec4s (xyz = position, w unused), stored in ring
/// order. Element <c>slot * trailLength + k</c> is ring slot k. The newest position is
/// at ring slot <c>step % trailLength</c> after the record for that step, so a
/// newest-first read walks backwards from that head, wrapping (see
/// <see cref="ReadTrailFromFloats"/> and <see cref="CpuTrails.ReadTrail"/>). This is
/// the same newest-first order <see cref="ParticleSystem.WriteStrand"/> writes:
/// element 0 is the head (current position), the last element is the oldest tail.
///
/// Trail length cap. <see cref="MaxTrailLength"/> is 32. A larger request throws
/// <see cref="ArgumentOutOfRangeException"/> naming the cap; it is NEVER silently
/// shrunk, because a silently shorter trail would change the strand length a caller
/// asked for. Trail length must be &gt;= 1; particle count must be &gt;= 0 (count 0
/// dispatches nothing, by the same uCount-guard shape as the parity shader).
///
/// Spawn convention (matches WriteStrand's pre-birth rule). A newly spawned slot's
/// ring starts filled with its spawn position, so points before birth repeat the
/// spawn position instead of streaking to the origin. Emission is CPU-only by
/// design (lane 3), so the fill is a host-side upload: <see cref="FillSpawn"/>
/// (single slot) or <see cref="CreateFromStates"/> (whole buffer at setup).
/// A slot that is never fill-spawned reads as zeros until its first record.
///
/// Dead slots. A record dispatch skips slots whose alive flag is 0, exactly as the
/// parity shader does, and never writes their rings. Consequence the caller must
/// respect: a dead slot's ring keeps its previous particle's trail until the slot
/// is reused, so the spawn path MUST call <see cref="FillSpawn"/> before the slot
/// goes live again, or the new particle inherits the old trail. This mirrors the
/// CPU <c>TrailBuffer.Clear</c> rule in P24's <c>ParticleTrails.cs</c>.
///
/// Step counter. The caller owns a monotonically increasing step index (one
/// record per substep) and passes it as <paramref name="stepIndex"/>; the shader
/// takes it modulo the trail length, so the counter itself never wraps in
/// practice. Records for a slot that missed steps (e.g. it was dead) simply leave
/// older ring entries in place; the read order is always defined by the latest
/// recorded step, which is why <see cref="ReadTrail"/> takes the last step.
///
/// Resampling / smoothing. The GPU buffer is RAW history only. Spacing-based
/// decimation on read-back is provided as a CPU helper
/// (<see cref="DecimateBySpacing"/>, same keep-head / minimum-distance / keep-tail
/// semantics as P24's <c>ParticleTrails.ResampleBySpacing</c>); smoothing and
/// width curves stay CPU-side in P24's <c>ParticleTrails</c>. No resampling or
/// smoothing shader is built here, deliberately: the lane is buffer + record +
/// read-back + oracle.
///
/// Agreement policy. GPU-vs-CPU agreement is EPSILON agreement, never
/// bit-identical (same policy as the collider / turbulence companions): tests
/// compare with a 1e-4 tolerance and report the measured maximum deviation.
///
/// Validation, stated plainly. What <see cref="Dispatch"/> can check without a
/// readback it checks loudly before touching GL: negative count, trail length
/// outside 1..32, negative step, zero program / particle / trail handles.
/// Non-finite positions cannot be checked by a compute dispatch without reading
/// the SSBO back (which would stall the pipeline the GPU path exists to avoid),
/// so finiteness is enforced where positions ENTER this module on the host:
/// <see cref="FillSpawn"/>, <see cref="CreateFromStates"/> and
/// <see cref="CpuTrails.Record"/> all reject non-finite positions, and the CPU
/// oracle is the test reference for that rule. A dead slot's stored position is
/// never validated by <see cref="CpuTrails.Record"/> because a dead slot is never
/// read or written.
///
/// Out-of-bounds guard. The shader returns immediately when
/// <c>gl_GlobalInvocationID.x &gt;= uCount</c>, so dispatching a count that is
/// not a multiple of the workgroup size never reads or writes past the first
/// <c>count</c> slots, even though the group count rounds up. The trail buffer
/// itself is sized by the caller for the full allocation, not just the count,
/// which is what makes the sentinel test (slots past count byte-identical)
/// meaningful.
///
/// Out of scope, stated plainly:
/// - GPU spawn/kill (a separate lane); this pass never changes the alive flag.
/// - Mesh collision in GLSL, GPU LOD, CPU trail code changes (P24 untouched).
/// - Any rendering / strand-layer drawing (that reads this buffer later).
/// - Save / serialization of trails.
/// Units: kernel inches, Z-up, seconds, like <see cref="ParticleSystem"/>.
/// </summary>
public static class GpuParticleTrails
{
	/// <summary>Same 64-byte ParticleState layout as the parity shader.</summary>
	public const int StrideFloats = GpuParticleSystem.StrideFloats;
	/// <summary>Workgroup size; must match local_size_x in the shader.</summary>
	public const int LocalSize = 256;
	/// <summary>Maximum trail length (points per particle). Larger requests throw, never shrink.</summary>
	public const int MaxTrailLength = 32;
	/// <summary>Floats per trail point (vec4: xyz position, w unused).</summary>
	public const int TrailPointFloats = 4;

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
layout(std430, binding = 1) buffer Trails {
    vec4 trails[];
};
uniform uint uCount;
uniform int uTrailLength;
uniform int uStep;
void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i >= uCount) return;
    Particle pt = particles[i];
    if (pt.velAlive.w == 0.0) return;
    int head = uStep % uTrailLength;
    trails[int(i) * uTrailLength + head] = vec4(pt.posAge.xyz, 0.0);
}
";

	public static uint CreateProgram( GL gl )
	{
		var cs = gl.CreateShader( ShaderType.ComputeShader );
		gl.ShaderSource( cs, ComputeSrc );
		gl.CompileShader( cs );
		gl.GetShader( cs, ShaderParameterName.CompileStatus, out int ok );
		if ( ok == 0 ) { var log = gl.GetShaderInfoLog( cs ); gl.DeleteShader( cs ); throw new InvalidOperationException( "gpu trail compute compile failed:\n" + log ); }
		var prog = gl.CreateProgram();
		gl.AttachShader( prog, cs );
		gl.LinkProgram( prog );
		gl.GetProgram( prog, ProgramPropertyARB.LinkStatus, out int linked );
		gl.DetachShader( prog, cs );
		gl.DeleteShader( cs );
		if ( linked == 0 ) { var log = gl.GetProgramInfoLog( prog ); gl.DeleteProgram( prog ); throw new InvalidOperationException( "gpu trail compute link failed:\n" + log ); }
		return prog;
	}

	/// <summary>Trail length must be 1..<see cref="MaxTrailLength"/>; larger values throw, never shrink.</summary>
	public static void ValidateTrailLength( int trailLength )
	{
		if ( trailLength < 1 || trailLength > MaxTrailLength )
			throw new ArgumentOutOfRangeException( nameof( trailLength ), $"Trail length must be 1..{MaxTrailLength}; {trailLength} was requested and will not be silently shrunk." );
	}

	/// <summary>Particle count must be &gt;= 0; 0 is a legal no-op dispatch, negative is not.</summary>
	public static void ValidateParticleCount( int particleCount )
	{
		if ( particleCount < 0 ) throw new ArgumentOutOfRangeException( nameof( particleCount ), "Particle count must be >= 0." );
	}

	/// <summary>Step index must be &gt;= 0 (the caller-owned monotonic record counter).</summary>
	public static void ValidateStep( int stepIndex )
	{
		if ( stepIndex < 0 ) throw new ArgumentOutOfRangeException( nameof( stepIndex ), "Step index must be >= 0." );
	}

	/// <summary>A position with any non-finite component is rejected loudly.</summary>
	public static void ValidatePosition( Vector3 position )
	{
		if ( !float.IsFinite( position.X ) || !float.IsFinite( position.Y ) || !float.IsFinite( position.Z ) )
			throw new ArgumentException( "Trail position has a non-finite component." );
	}

	static void ValidateSlot( int slot, int particleCount )
	{
		if ( slot < 0 || slot >= particleCount ) throw new ArgumentOutOfRangeException( nameof( slot ), $"Slot {slot} is outside 0..{particleCount - 1}." );
	}

	static void ValidateHandle( uint handle, string name )
	{
		if ( handle == 0 ) throw new ArgumentException( $"{name} handle is 0 (null buffer / program).", name );
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
	/// Allocate a zero-filled trail buffer for <paramref name="particleCount"/> slots.
	/// Every ring reads as zeros until its slot is fill-spawned or recorded.
	/// </summary>
	public static uint CreateTrailBuffer( GL gl, int particleCount, int trailLength )
	{
		ValidateParticleCount( particleCount );
		ValidateTrailLength( trailLength );
		return UploadFloats( gl, new float[particleCount * trailLength * TrailPointFloats] );
	}

	/// <summary>
	/// Allocate a trail buffer pre-filled from CPU states: each live slot's ring is
	/// filled with that slot's current position (the spawn convention), each dead
	/// slot's ring is zero. Live positions must be finite; dead slots are not read.
	/// </summary>
	public static uint CreateFromStates( GL gl, ReadOnlySpan<ParticleState> states, int trailLength )
	{
		ValidateTrailLength( trailLength );
		var data = new float[states.Length * trailLength * TrailPointFloats];
		for ( int i = 0; i < states.Length; i++ )
		{
			if ( states[i].Alive == 0 ) continue;
			ValidatePosition( states[i].Position );
			for ( int k = 0; k < trailLength; k++ )
			{
				int b = (i * trailLength + k) * TrailPointFloats;
				data[b + 0] = states[i].Position.X; data[b + 1] = states[i].Position.Y; data[b + 2] = states[i].Position.Z; data[b + 3] = 0;
			}
		}
		return UploadFloats( gl, data );
	}

	/// <summary>
	/// Fill one slot's whole ring with its spawn position (the pre-birth convention):
	/// after this, points before the slot's first record repeat the spawn position
	/// instead of streaking to the origin. Call this on (re)spawn, before the slot
	/// goes live, so a reused slot never inherits its previous particle's trail.
	/// Host-side upload: emission is CPU-only by design, so the spawn path is too.
	/// </summary>
	public static void FillSpawn( GL gl, uint trailBuffer, int slot, int particleCount, int trailLength, Vector3 spawnPosition )
	{
		ValidateHandle( trailBuffer, nameof( trailBuffer ) );
		ValidateParticleCount( particleCount );
		ValidateTrailLength( trailLength );
		ValidateSlot( slot, particleCount );
		ValidatePosition( spawnPosition );
		var data = new float[trailLength * TrailPointFloats];
		for ( int k = 0; k < trailLength; k++ )
		{
			data[k * TrailPointFloats + 0] = spawnPosition.X;
			data[k * TrailPointFloats + 1] = spawnPosition.Y;
			data[k * TrailPointFloats + 2] = spawnPosition.Z;
			data[k * TrailPointFloats + 3] = 0;
		}
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, trailBuffer );
		unsafe
		{
			fixed ( float* ptr = data )
			{
				gl.BufferSubData( BufferTargetARB.ShaderStorageBuffer, (nint)(slot * trailLength * TrailPointFloats * sizeof( float )), (nuint)(data.Length * sizeof( float )), ptr );
			}
		}
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, 0 );
	}

	/// <summary>
	/// Record one step: for every live slot in the first <paramref name="particleCount"/>
	/// slots, write its current SSBO position into its ring at
	/// <c>stepIndex % trailLength</c>. Dead slots and slots past the count are
	/// untouched. Intended to run AFTER <c>GpuParticleSystem.Dispatch</c> on the
	/// same particle buffer, once per substep (integration lines reported for the
	/// sweep, not edited here). All arguments are validated before any GL call.
	/// </summary>
	public static void Dispatch( GL gl, uint program, uint particleBuffer, uint trailBuffer, int particleCount, int trailLength, int stepIndex )
	{
		ValidateHandle( program, nameof( program ) );
		ValidateHandle( particleBuffer, nameof( particleBuffer ) );
		ValidateHandle( trailBuffer, nameof( trailBuffer ) );
		ValidateParticleCount( particleCount );
		ValidateTrailLength( trailLength );
		ValidateStep( stepIndex );
		gl.UseProgram( program );
		gl.Uniform1( gl.GetUniformLocation( program, "uCount" ), (uint)particleCount );
		gl.Uniform1( gl.GetUniformLocation( program, "uTrailLength" ), trailLength );
		gl.Uniform1( gl.GetUniformLocation( program, "uStep" ), stepIndex );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 0, particleBuffer );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 1, trailBuffer );
		uint groups = (uint)((particleCount + LocalSize - 1) / LocalSize);
		if ( groups == 0 ) groups = 1;
		gl.DispatchCompute( groups, 1, 1 );
		gl.MemoryBarrier( MemoryBarrierMask.ShaderStorageBarrierBit );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 0, 0 );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 1, 0 );
	}

	/// <summary>Read back raw trail floats (<paramref name="particleCount"/> x trailLength x 4).</summary>
	public static float[] DownloadTrails( GL gl, uint trailBuffer, int particleCount, int trailLength )
	{
		ValidateHandle( trailBuffer, nameof( trailBuffer ) );
		ValidateParticleCount( particleCount );
		ValidateTrailLength( trailLength );
		var data = new float[particleCount * trailLength * TrailPointFloats];
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, trailBuffer );
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

	/// <summary>
	/// Newest-first read of one slot from raw trail floats (ring order).
	/// <paramref name="lastStep"/> is the step index of the latest record for the
	/// buffer (-1 = never recorded: the slot's ring is returned in storage order,
	/// which is correct for a fill-spawned or zero ring since all entries agree
	/// or are zero). Element 0 of the result is the head.
	/// </summary>
	public static Vector3[] ReadTrailFromFloats( float[] data, int slot, int particleCount, int trailLength, int lastStep )
	{
		if ( data is null ) throw new ArgumentNullException( nameof( data ) );
		ValidateParticleCount( particleCount );
		ValidateTrailLength( trailLength );
		ValidateSlot( slot, particleCount );
		if ( data.Length < particleCount * trailLength * TrailPointFloats )
			throw new ArgumentException( $"Trail data has {data.Length} floats, need {particleCount * trailLength * TrailPointFloats}.", nameof( data ) );
		var result = new Vector3[trailLength];
		if ( lastStep < 0 )
		{
			for ( int k = 0; k < trailLength; k++ )
			{
				int b = (slot * trailLength + k) * TrailPointFloats;
				result[k] = new Vector3( data[b + 0], data[b + 1], data[b + 2] );
			}
			return result;
		}
		int head = ((lastStep % trailLength) + trailLength) % trailLength;
		for ( int i = 0; i < trailLength; i++ )
		{
			int ring = ((head - i) % trailLength + trailLength) % trailLength;
			int b = (slot * trailLength + ring) * TrailPointFloats;
			result[i] = new Vector3( data[b + 0], data[b + 1], data[b + 2] );
		}
		return result;
	}

	/// <summary>Download then newest-first read one slot (see <see cref="ReadTrailFromFloats"/>).</summary>
	public static Vector3[] ReadTrail( GL gl, uint trailBuffer, int slot, int particleCount, int trailLength, int lastStep ) =>
		ReadTrailFromFloats( DownloadTrails( gl, trailBuffer, particleCount, trailLength ), slot, particleCount, trailLength, lastStep );

	/// <summary>
	/// Spacing-based decimation of a newest-first trail on read-back (CPU only;
	/// the GPU buffer stays raw history). Keeps the head, then a point only once
	/// it is at least <paramref name="spacing"/> from the last kept point; the
	/// final raw point (tail) is always kept unless it duplicates the last kept
	/// point. Same semantics as P24's ResampleBySpacing, applied to the
	/// newest-first order this buffer reads in. Never shrinks the count silently
	/// by any other rule: spacing &gt; 0 is required, loudly.
	/// </summary>
	public static Vector3[] DecimateBySpacing( Vector3[] newestFirst, float spacing )
	{
		if ( newestFirst is null ) throw new ArgumentNullException( nameof( newestFirst ) );
		if ( newestFirst.Length == 0 ) throw new ArgumentException( "A trail point list must not be empty.", nameof( newestFirst ) );
		if ( !float.IsFinite( spacing ) || spacing <= 0f ) throw new ArgumentOutOfRangeException( nameof( spacing ), "Decimation spacing must be finite and > 0." );
		foreach ( var p in newestFirst ) ValidatePosition( p );
		if ( newestFirst.Length == 1 ) return new[] { newestFirst[0] };
		var kept = new System.Collections.Generic.List<Vector3> { newestFirst[0] };
		for ( int i = 1; i < newestFirst.Length; i++ )
			if ( Vector3.Distance( newestFirst[i], kept[kept.Count - 1] ) >= spacing )
				kept.Add( newestFirst[i] );
		var last = newestFirst[newestFirst.Length - 1];
		if ( kept[kept.Count - 1] != last ) kept.Add( last );
		return kept.ToArray();
	}

	public static void Dispose( GL gl, uint trailBuffer, uint program )
	{
		if ( trailBuffer != 0 ) gl.DeleteBuffer( trailBuffer );
		if ( program != 0 ) gl.DeleteProgram( program );
	}
}

/// <summary>
/// CPU reference implementation of the GPU trail ring, used as the test oracle.
/// Semantics are identical to the shader by construction: one record per step
/// writes each live slot's position at ring slot <c>step % trailLength</c>;
/// dead slots are never read or written; <see cref="ReadTrail"/> returns
/// newest-first (element 0 = head). Fill a slot with <see cref="FillSpawn"/>
/// at spawn so pre-birth points repeat the spawn position.
/// </summary>
public sealed class CpuTrails
{
	public int Count { get; }
	public int TrailLength { get; }
	/// <summary>Step index of the latest <see cref="Record"/> call, or -1 if none.</summary>
	public int LastStep { get; private set; } = -1;
	readonly Vector3[] _ring;

	public CpuTrails( int particleCount, int trailLength )
	{
		GpuParticleTrails.ValidateParticleCount( particleCount );
		GpuParticleTrails.ValidateTrailLength( trailLength );
		Count = particleCount;
		TrailLength = trailLength;
		_ring = new Vector3[particleCount * trailLength];
	}

	/// <summary>Fill one slot's whole ring with its spawn position (pre-birth convention).</summary>
	public void FillSpawn( int slot, Vector3 spawnPosition )
	{
		if ( slot < 0 || slot >= Count ) throw new ArgumentOutOfRangeException( nameof( slot ), $"Slot {slot} is outside 0..{Count - 1}." );
		GpuParticleTrails.ValidatePosition( spawnPosition );
		for ( int k = 0; k < TrailLength; k++ ) _ring[slot * TrailLength + k] = spawnPosition;
	}

	/// <summary>
	/// Record one step over <paramref name="states"/> (must hold at least
	/// <see cref="Count"/> entries; only the first Count are read). Live slots
	/// must have finite positions; dead slots are skipped before any validation.
	/// Count 0 records nothing but still validates the step index.
	/// </summary>
	public void Record( ReadOnlySpan<ParticleState> states, int stepIndex )
	{
		GpuParticleTrails.ValidateStep( stepIndex );
		if ( Count == 0 ) return;
		if ( states.Length < Count ) throw new ArgumentException( $"Need {Count} states, got {states.Length}.", nameof( states ) );
		int head = stepIndex % TrailLength;
		for ( int i = 0; i < Count; i++ )
		{
			if ( states[i].Alive == 0 ) continue;
			GpuParticleTrails.ValidatePosition( states[i].Position );
			_ring[i * TrailLength + head] = states[i].Position;
		}
		LastStep = stepIndex;
	}

	/// <summary>Newest-first read of one slot (element 0 = head); see <see cref="GpuParticleTrails.ReadTrailFromFloats"/> for the never-recorded case.</summary>
	public Vector3[] ReadTrail( int slot )
	{
		if ( slot < 0 || slot >= Count ) throw new ArgumentOutOfRangeException( nameof( slot ), $"Slot {slot} is outside 0..{Count - 1}." );
		var result = new Vector3[TrailLength];
		if ( LastStep < 0 )
		{
			Array.Copy( _ring, slot * TrailLength, result, 0, TrailLength );
			return result;
		}
		int head = ((LastStep % TrailLength) + TrailLength) % TrailLength;
		for ( int i = 0; i < TrailLength; i++ )
		{
			int ring = ((head - i) % TrailLength + TrailLength) % TrailLength;
			result[i] = _ring[slot * TrailLength + ring];
		}
		return result;
	}
}
