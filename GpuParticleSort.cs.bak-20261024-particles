using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>
/// GPU back-to-front sorting for transparent particles (particles lane 8).
///
/// Why this exists: alpha-blended billboards must draw back-to-front, and the
/// CPU <c>ParticleSystem.SortBackToFront</c> order does not transfer to the
/// GPU path — the GPU never sees the CPU order. This sorts particle slots on
/// the GPU by camera depth so a future draw path can consume the index buffer
/// directly instead of reading positions back to the CPU.
///
/// Algorithm — bitonic sort, picked over odd-even merge sort because it is
/// the GPU Gems 2 ch. 46 reference, its pass count is data-independent, and
/// every pass is a set of disjoint compare-exchanges that map cleanly to one
/// compute dispatch per pass. Sort key per slot is a (depth, slot-index) pair:
/// depth is transformed with the same float-bit trick the CPU sort uses
/// (monotonic map of the IEEE bits, then inverted so ascending key order is
/// descending depth, i.e. back-to-front), and ties break by lower slot index
/// first — which matches the CPU radix sort's stable order exactly, since the
/// CPU fills its index array in slot order.
///
/// Packing note (honest deviation from GPU Gems 2 ch. 46): that chapter packs
/// TWO key/index pairs per fragment because a fragment shader outputs a
/// vec4. A compute shader has no such constraint, so this uses one key SSBO
/// (uint) and one index SSBO (int) instead. Each compare-exchange touches
/// both buffers from the single invocation that owns the pair, so there is
/// no race; passes are separated by dispatches + storage barriers.
///
/// Layout: consumes the 64-byte <see cref="ParticleState"/> SSBO lane 3 built
/// (<see cref="GpuParticleSystem.StrideFloats"/> = 16, position at offset 0,
/// alive at offset 7). This file NEVER changes that layout.
///
/// Dead slots and padding: dead slots (<c>Alive == 0</c>) get key
/// <c>uint.MaxValue</c> and sort last, in slot order; the count is padded up
/// to a power of two with sentinel entries (key <c>uint.MaxValue</c>, index
/// -1) which <see cref="DownloadIndices"/> strips. The caller draws the
/// live prefix of the returned order; the live count itself is the caller's
/// business (the CPU sim knows it, or it comes from a count buffer).
///
/// Blend-mode rule (documented, not enforced — no blend modes are changed
/// here): additive and multiplicative blending are order-independent and
/// need no sort. This sort exists for alpha blending only.
///
/// Agreement rule: never promise bit-identical CPU/GPU output. Sort ORDER
/// is exact (indices are integers, ties are resolved identically), but
/// depths are float32 on both sides and may differ in the last bits, so two
/// particles at nearly identical depths could swap between CPU and GPU
/// order. Tests use well-separated depths except for exact ties, where the
/// order is robust by construction.
///
/// NOT built here: draw-path wiring (a later integration step consumes the
/// index buffer), distributed sort passes over multiple frames (the
/// single-pass-per-(stage,substage) primitive <see cref="SortPass"/> is
/// public so a future caller CAN distribute passes, but v1 sorts fully in
/// one call), mesh-collision sorting, and any his-eye check (headless only).
/// </summary>
public static class GpuParticleSort
{
	/// <summary>Compute workgroup width; one invocation per slot per pass.</summary>
	public const int WorkgroupSize = 256;

	/// <summary>uMode values: build keys from states, or run one sort pass.</summary>
	public const uint ModeKeys = 0, ModeSort = 1;

	/// <summary>Sort key assigned to dead slots and padding: sorts last.</summary>
	public const uint DeadKey = 0xFFFFFFFFu;

	/// <summary>Index-buffer sentinel for padding slots; stripped on download.</summary>
	public const int PaddingIndex = -1;

	static readonly string ComputeSrc = @"#version 430 core
layout(local_size_x = 256) in;
layout(std430, binding = 0) readonly buffer States {
    float stateFloats[];
};
layout(std430, binding = 1) buffer Keys {
    uint keys[];
};
layout(std430, binding = 2) buffer Indices {
    int indices[];
};
uniform uint uMode;
uniform uint uCount;
uniform uint uPadded;
uniform uint uStage;
uniform uint uSubstage;
uniform vec3 uCameraPos;
uniform vec3 uCameraFwd;

void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i >= uPadded) return;
    if (uMode == 0u) {
        if (i >= uCount) {
            keys[i] = 0xFFFFFFFFu;
            indices[i] = -1;
            return;
        }
        uint b = i * 16u;
        vec3 pos = vec3(stateFloats[b + 0u], stateFloats[b + 1u], stateFloats[b + 2u]);
        float alive = stateFloats[b + 7u];
        if (alive == 0.0) {
            keys[i] = 0xFFFFFFFFu;
            indices[i] = int(i);
            return;
        }
        float depth = dot(pos - uCameraPos, uCameraFwd);
        uint bits = floatBitsToUint(depth);
        uint ascending = (bits & 0x80000000u) != 0u ? ~bits : bits | 0x80000000u;
        keys[i] = ~ascending;
        indices[i] = int(i);
        return;
    }
    uint l = i ^ uSubstage;
    if (l <= i) return;
    bool dirAsc = (i & uStage) == 0u;
    uint ka = keys[i], kb = keys[l];
    int ia = indices[i], ib = indices[l];
    bool aAfterB = (ka > kb) || (ka == kb && ia > ib);
    bool aBeforeB = (ka < kb) || (ka == kb && ia < ib);
    if ((dirAsc && aAfterB) || (!dirAsc && aBeforeB)) {
        keys[i] = kb; keys[l] = ka;
        indices[i] = ib; indices[l] = ia;
    }
}
";

	/// <summary>Next power of two >= <paramref name="count"/> (0 for count &lt;= 0).</summary>
	public static int PaddedCount( int count )
	{
		if ( count <= 0 ) return 0;
		if ( count > ( 1 << 30 ) ) throw new ArgumentOutOfRangeException( nameof( count ), "Count too large to pad to a power of two." );
		int p = 1;
		while ( p < count ) p <<= 1;
		return p;
	}

	/// <summary>Number of bitonic passes for a padded size: log2(P) * (log2(P) + 1) / 2.</summary>
	public static int PassCount( int paddedCount )
	{
		if ( paddedCount <= 1 ) return 0;
		int log = 0;
		while ( ( 1 << log ) < paddedCount ) log++;
		return log * ( log + 1 ) / 2;
	}

	/// <summary>Throws unless camera position/forward are finite and forward is non-zero (mirrors the CPU sort's guard).</summary>
	public static void ValidateCamera( Vector3 cameraPosition, Vector3 cameraForward )
	{
		static bool Finite( Vector3 v ) => float.IsFinite( v.X ) && float.IsFinite( v.Y ) && float.IsFinite( v.Z );
		if ( !Finite( cameraPosition ) || !Finite( cameraForward ) || cameraForward.LengthSquared() < 1e-20f )
			throw new ArgumentException( "Camera position/forward must be finite and forward non-zero." );
	}

	/// <summary>CPU mirror of the GLSL depth-key math, for tests and reference orders. Forward is normalized here, exactly as the shader receives it.</summary>
	public static uint DepthKey( Vector3 position, Vector3 cameraPosition, Vector3 cameraForward )
	{
		Vector3 fwd = Vector3.Normalize( cameraForward );
		float depth = Vector3.Dot( position - cameraPosition, fwd );
		uint bits = BitConverter.SingleToUInt32Bits( depth );
		uint ascending = ( bits & 0x80000000u ) != 0 ? ~bits : bits | 0x80000000u;
		return ~ascending;
	}

	public static uint CreateProgram( GL gl )
	{
		var cs = gl.CreateShader( ShaderType.ComputeShader );
		gl.ShaderSource( cs, ComputeSrc );
		gl.CompileShader( cs );
		gl.GetShader( cs, ShaderParameterName.CompileStatus, out int ok );
		if ( ok == 0 ) { var log = gl.GetShaderInfoLog( cs ); gl.DeleteShader( cs ); throw new InvalidOperationException( "sort compute compile failed:\n" + log ); }
		var prog = gl.CreateProgram();
		gl.AttachShader( prog, cs );
		gl.LinkProgram( prog );
		gl.GetProgram( prog, ProgramPropertyARB.LinkStatus, out int linked );
		gl.DetachShader( prog, cs );
		gl.DeleteShader( cs );
		if ( linked == 0 ) { var log = gl.GetProgramInfoLog( prog ); gl.DeleteProgram( prog ); throw new InvalidOperationException( "sort compute link failed:\n" + log ); }
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

	static uint UploadInts( GL gl, int[] data )
	{
		uint buf = gl.GenBuffer();
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, buf );
		unsafe
		{
			fixed ( int* ptr = data )
			{
				gl.BufferData( BufferTargetARB.ShaderStorageBuffer, (nuint)(data.Length * sizeof( int )), ptr, BufferUsageARB.DynamicDraw );
			}
		}
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, 0 );
		return buf;
	}

	/// <summary>Upload a key array as an SSBO (for direct SortBuffers use).</summary>
	public static uint UploadKeys( GL gl, uint[] keys ) => UploadUints( gl, keys );

	/// <summary>Upload an index array as an SSBO (for direct SortBuffers use).</summary>
	public static uint UploadIndices( GL gl, int[] indices ) => UploadInts( gl, indices );

	/// <summary>Allocate zeroed key + identity index buffers of <paramref name="paddedCount"/> entries. ComputeKeys overwrites both.</summary>
	public static (uint keys, uint indices) CreateBuffers( GL gl, int paddedCount )
	{
		if ( paddedCount < 0 ) throw new ArgumentOutOfRangeException( nameof( paddedCount ) );
		var keys = new uint[paddedCount];
		var indices = new int[paddedCount];
		for ( int i = 0; i < paddedCount; i++ ) indices[i] = i;
		return ( UploadUints( gl, keys ), UploadInts( gl, indices ) );
	}

	static void SetUniforms( GL gl, uint program, uint mode, uint count, uint padded, uint stage, uint substage, Vector3 cameraPos, Vector3 cameraFwd )
	{
		gl.UseProgram( program );
		gl.Uniform1( gl.GetUniformLocation( program, "uMode" ), mode );
		gl.Uniform1( gl.GetUniformLocation( program, "uCount" ), count );
		gl.Uniform1( gl.GetUniformLocation( program, "uPadded" ), padded );
		gl.Uniform1( gl.GetUniformLocation( program, "uStage" ), stage );
		gl.Uniform1( gl.GetUniformLocation( program, "uSubstage" ), substage );
		gl.Uniform3( gl.GetUniformLocation( program, "uCameraPos" ), cameraPos.X, cameraPos.Y, cameraPos.Z );
		gl.Uniform3( gl.GetUniformLocation( program, "uCameraFwd" ), cameraFwd.X, cameraFwd.Y, cameraFwd.Z );
	}

	static void DispatchGroups( GL gl, uint padded )
	{
		uint groups = ( padded + (uint)WorkgroupSize - 1u ) / (uint)WorkgroupSize;
		if ( groups == 0 ) groups = 1;
		gl.DispatchCompute( groups, 1, 1 );
		gl.MemoryBarrier( MemoryBarrierMask.ShaderStorageBarrierBit );
	}

	/// <summary>Fill key/index buffers from a state SSBO (binding 0 = states, 1 = keys, 2 = indices). Dead slots get DeadKey, padding gets PaddingIndex.</summary>
	public static void ComputeKeys( GL gl, uint program, uint stateBuffer, uint keyBuffer, uint indexBuffer, int count, Vector3 cameraPosition, Vector3 cameraForward )
	{
		if ( count < 0 ) throw new ArgumentOutOfRangeException( nameof( count ) );
		ValidateCamera( cameraPosition, cameraForward );
		int padded = PaddedCount( count );
		if ( padded == 0 ) return;
		Vector3 fwd = Vector3.Normalize( cameraForward );
		SetUniforms( gl, program, ModeKeys, (uint)count, (uint)padded, 0, 0, cameraPosition, fwd );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 0, stateBuffer );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 1, keyBuffer );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 2, indexBuffer );
		DispatchGroups( gl, (uint)padded );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 0, 0 );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 1, 0 );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 2, 0 );
	}

	/// <summary>Run ONE bitonic compare-exchange pass (stage <paramref name="stage"/>, substage <paramref name="substage"/>) over key/index buffers. This is the primitive a future caller can distribute across frames.</summary>
	public static void SortPass( GL gl, uint program, uint keyBuffer, uint indexBuffer, int paddedCount, uint stage, uint substage )
	{
		if ( paddedCount <= 1 ) return;
		SetUniforms( gl, program, ModeSort, 0, (uint)paddedCount, stage, substage, Vector3.Zero, Vector3.UnitZ );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 1, keyBuffer );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 2, indexBuffer );
		DispatchGroups( gl, (uint)paddedCount );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 1, 0 );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 2, 0 );
	}

	/// <summary>Run the full bitonic sort in place over key/index buffers of <paramref name="paddedCount"/> (power-of-two) entries.</summary>
	public static void SortBuffers( GL gl, uint program, uint keyBuffer, uint indexBuffer, int paddedCount )
	{
		if ( paddedCount <= 1 ) return;
		for ( uint k = 2; k <= (uint)paddedCount; k <<= 1 )
			for ( uint j = k / 2; j > 0; j >>= 1 )
				SortPass( gl, program, keyBuffer, indexBuffer, paddedCount, k, j );
	}

	/// <summary>Read back the index buffer, stripping padding sentinels (-1). Dead slots remain, at the end, in slot order.</summary>
	public static int[] DownloadIndices( GL gl, uint indexBuffer, int paddedCount )
	{
		var data = new int[paddedCount];
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, indexBuffer );
		unsafe
		{
			fixed ( int* ptr = data )
			{
				gl.GetBufferSubData( BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(data.Length * sizeof( int )), ptr );
			}
		}
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, 0 );
		int n = 0;
		for ( int i = 0; i < data.Length; i++ ) if ( data[i] >= 0 ) n++;
		var stripped = new int[n];
		int w = 0;
		for ( int i = 0; i < data.Length; i++ ) if ( data[i] >= 0 ) stripped[w++] = data[i];
		return stripped;
	}

	/// <summary>Read back the key buffer verbatim (padded length, sentinels included) for verification.</summary>
	public static uint[] DownloadKeys( GL gl, uint keyBuffer, int paddedCount )
	{
		var data = new uint[paddedCount];
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, keyBuffer );
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

	/// <summary>Convenience: allocate temp buffers, compute keys, sort fully, download the stripped order, and free the temp buffers. The caller owns <paramref name="program"/> and <paramref name="stateBuffer"/>.</summary>
	public static int[] SortIndices( GL gl, uint program, uint stateBuffer, int count, Vector3 cameraPosition, Vector3 cameraForward )
	{
		if ( count < 0 ) throw new ArgumentOutOfRangeException( nameof( count ) );
		ValidateCamera( cameraPosition, cameraForward );
		if ( count == 0 ) return Array.Empty<int>();
		int padded = PaddedCount( count );
		var (keys, indices) = CreateBuffers( gl, padded );
		try
		{
			ComputeKeys( gl, program, stateBuffer, keys, indices, count, cameraPosition, cameraForward );
			SortBuffers( gl, program, keys, indices, padded );
			return DownloadIndices( gl, indices, padded );
		}
		finally
		{
			if ( keys != 0 ) gl.DeleteBuffer( keys );
			if ( indices != 0 ) gl.DeleteBuffer( indices );
		}
	}

	/// <summary>Free sort buffers and, when <paramref name="program"/> is non-zero, the program. Pass 0 for buffers/programs the caller still owns.</summary>
	public static void Dispose( GL gl, uint keyBuffer, uint indexBuffer, uint program )
	{
		if ( keyBuffer != 0 ) gl.DeleteBuffer( keyBuffer );
		if ( indexBuffer != 0 ) gl.DeleteBuffer( indexBuffer );
		if ( program != 0 ) gl.DeleteProgram( program );
	}
}
