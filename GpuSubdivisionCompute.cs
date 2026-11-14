// The GPU subdivision backend: Catmull-Clark positions by compute shader (godlike Platform P1).
//
// HOW IT WORKS. The CPU builds the stencil table once per topology
// (GpuSubdivision.BuildTable — one weighted sum per output vertex, flattened
// to source vertices). The GPU evaluates it: one thread per output vertex,
// sum(w * pos[src]). Topology (faces, UVs, skin, colours) still comes from
// the CPU reference (CatmullClark.SubdivideWithMap) on every call; only the
// positions come from the GPU.
//
// WHAT IT IS NOT. A first GPU step, not the fast path:
//   - It does not save time on the first call for a new topology — the CPU
//     still builds the topology and the table. The win it sets up is the
//     follow-on: cache the topology per TopologyKey and re-evaluate positions
//     on the GPU for every cage edit (a sculpt stroke) without rebuilding
//     anything. The stencil-table cache in this file is already built for
//     that; the topology cache is the follow-on, not here.
//   - Smooth subdivision only. The interface carries no crease input, so a
//     creased cage declines and the CPU reference (which handles creases)
//     runs instead.
//   - Ceilings: 65,536 cage vertices, 1M output vertices, 64 stencil entries
//     per output vertex. Above any of them the backend declines and the job
//     falls back to the CPU subdivision. A BVH/large-cage path is follow-on.
// Every GL failure — no EGL context, shader compile/link failure, buffer
// failure — declines with a reason instead of throwing, so a broken GPU path
// costs a CPU subdivision, never a crash or a wrong mesh. GL thread rules are
// the caller's: one lock serialises all backend calls.
//
// HOST WIRING (final integrator, shared entry point — not this file): call
// <see cref="Install"/> once at Desktop startup, next to
// ManifoldBoolean.Install(). Until then the CPU subdivision runs and nothing
// changes.
#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Effigy;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

public sealed class GpuSubdivisionComputeBackend : IGpuSubdivisionBackend, IDisposable
{
	public const int MaxCageVertices = GpuSubdivision.MaxCageVertices;
	public const int MaxOutputVertices = GpuSubdivision.MaxOutputVertices;
	public const int MaxStencilEntries = GpuSubdivision.MaxStencilEntries;

	public static void Install() => GpuSubdivision.Provider ??= new GpuSubdivisionComputeBackend();

	readonly object _lock = new();
	HeadlessGlContext? _context;
	uint _program;
	bool _disposed;

	/// <summary>Uploaded stencil buffers per topology, so a sculpted cage
	/// re-evaluates without re-uploading the table.</summary>
	sealed class TableGpu
	{
		public uint IndexBuffer;
		public uint WeightBuffer;
		public uint OffsetBuffer;
		public int OutputVertices;
	}
	readonly Dictionary<long, TableGpu> _tables = new();

	public void Dispose()
	{
		lock ( _lock )
		{
			_disposed = true;
			if ( _context is not null )
			{
				var gl = _context.Gl;
				foreach ( var t in _tables.Values )
				{
					gl.DeleteBuffer( t.IndexBuffer );
					gl.DeleteBuffer( t.WeightBuffer );
					gl.DeleteBuffer( t.OffsetBuffer );
				}
				_tables.Clear();
				if ( _program != 0 ) { gl.DeleteProgram( _program ); _program = 0; }
				_context.Dispose();
				_context = null;
			}
		}
	}

	public bool TrySubdivide( PolyMesh mesh, out PolyMesh? subdivided, out string? why )
	{
		subdivided = null;
		why = null;
		if ( mesh is null ) { why = "No mesh to subdivide."; return false; }
		if ( mesh.VertexCount > MaxCageVertices ) { why = $"GpuSubdivision ceiling is {MaxCageVertices:N0} cage vertices (this cage has {mesh.VertexCount:N0})."; return false; }
		if ( mesh.FaceCount == 0 ) { why = "Cannot subdivide an empty mesh."; return false; }

		lock ( _lock )
		{
			if ( _disposed ) { why = "GpuSubdivision backend is disposed."; return false; }
			try
			{
				var gl = GetGl( out why );
				if ( gl is null ) return false;
				EnsureProgram( gl );
				subdivided = SubdivideOnGpu( gl, mesh, out why );
				return subdivided is not null;
			}
			catch ( Exception ex )
			{
				subdivided = null;
				why = $"GPU subdivision failed: {ex.Message}";
				return false;
			}
		}
	}

	GL? GetGl( out string? why )
	{
		why = null;
		if ( _context is null )
		{
			try { _context = new HeadlessGlContext(); }
			catch ( Exception ex ) { why = $"No headless GL context: {ex.Message}"; return null; }
		}
		return _context.Gl;
	}

	PolyMesh? SubdivideOnGpu( GL gl, PolyMesh mesh, out string? why )
	{
		why = null;
		var key = GpuSubdivision.TopologyKey( mesh );
		var isNewTable = false;
		if ( !_tables.TryGetValue( key, out var tableGpu ) )
		{
			var table = GpuSubdivision.BuildTable( mesh );
			if ( table.OutputVertexCount > MaxOutputVertices )
			{
				why = $"GpuSubdivision ceiling is {MaxOutputVertices:N0} output vertices (this subdivision makes {table.OutputVertexCount:N0}).";
				return null;
			}
			var maxStencil = 0;
			for ( var i = 0; i < table.OutputVertexCount; i++ )
				maxStencil = Math.Max( maxStencil, table.Offsets[i + 1] - table.Offsets[i] );
			if ( maxStencil > MaxStencilEntries )
			{
				why = $"GpuSubdivision ceiling is {MaxStencilEntries} stencil entries per vertex (this cage needs {maxStencil}).";
				return null;
			}
			tableGpu = new TableGpu
			{
				IndexBuffer = UploadBuffer( gl, table.Indices ),
				WeightBuffer = UploadBuffer( gl, table.Weights ),
				OffsetBuffer = UploadBuffer( gl, table.Offsets ),
				OutputVertices = table.OutputVertexCount,
			};
			_tables[key] = tableGpu;
			isNewTable = true;
		}

		// Cage positions in, subdivided positions out.
		var positions = new float[mesh.VertexCount * 3];
		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			positions[i * 3] = mesh.Positions[i].x;
			positions[i * 3 + 1] = mesh.Positions[i].y;
			positions[i * 3 + 2] = mesh.Positions[i].z;
		}
		var posIn = UploadBuffer( gl, positions );
		var posOut = NewBuffer( gl, tableGpu.OutputVertices * 3 * sizeof( float ) );
		try
		{
			gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 0, posIn );
			gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 1, tableGpu.IndexBuffer );
			gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 2, tableGpu.WeightBuffer );
			gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 3, tableGpu.OffsetBuffer );
			gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 4, posOut );
			gl.UseProgram( _program );
			SetUniform( gl, _program, "uOutVerts", tableGpu.OutputVertices );
			gl.DispatchCompute( (uint)((tableGpu.OutputVertices + 63) / 64), 1, 1 );
			gl.MemoryBarrier( MemoryBarrierMask.ShaderStorageBarrierBit );
			var gpuPositions = new float[tableGpu.OutputVertices * 3];
			ReadBuffer( gl, posOut, gpuPositions );
			if ( gl.GetError() != GLEnum.NoError ) { why = "GL error during GPU subdivision."; return null; }

			// Topology from the CPU reference; positions from the GPU.
			var (cpu, _) = CatmullClark.SubdivideWithMap( mesh );
			if ( cpu.VertexCount != tableGpu.OutputVertices ) { why = "CPU reference and GPU table disagree on the output vertex count."; return null; }
			var result = cpu;
			var newPositions = new List<Vec3>( tableGpu.OutputVertices );
			for ( var i = 0; i < tableGpu.OutputVertices; i++ )
				newPositions.Add( new Vec3( gpuPositions[i * 3], gpuPositions[i * 3 + 1], gpuPositions[i * 3 + 2] ) );
			result.Positions = newPositions;

			if ( isNewTable )
			{
				// First use of this table: cross-check the GPU positions against
				// the CPU reference. On mismatch the table is evicted and the job
				// declines — the caller falls back to the CPU subdivision.
				for ( var i = 0; i < cpu.VertexCount; i++ )
				{
					if ( (cpu.Positions[i] - result.Positions[i]).Length > 1e-4f )
					{
						_tables.Remove( key );
						gl.DeleteBuffer( tableGpu.IndexBuffer );
						gl.DeleteBuffer( tableGpu.WeightBuffer );
						gl.DeleteBuffer( tableGpu.OffsetBuffer );
						why = $"GPU subdivision cross-check failed at vertex {i} (GPU and CPU positions differ by more than 1e-4).";
						return null;
					}
				}
			}
			return result;
		}
		finally
		{
			gl.DeleteBuffer( posIn );
			gl.DeleteBuffer( posOut );
		}
	}

	static uint UploadBuffer<T>( GL gl, T[] data ) where T : unmanaged
	{
		var id = gl.GenBuffer();
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, id );
		var bytes = (nuint)(data.Length * Marshal.SizeOf<T>());
		unsafe { gl.BufferData( BufferTargetARB.ShaderStorageBuffer, bytes, null, BufferUsageARB.StaticDraw ); }
		unsafe
		{
			fixed ( T* p = data ) gl.BufferSubData( BufferTargetARB.ShaderStorageBuffer, 0, bytes, p );
		}
		return id;
	}

	static uint NewBuffer( GL gl, int bytes )
	{
		var id = gl.GenBuffer();
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, id );
		unsafe { gl.BufferData( BufferTargetARB.ShaderStorageBuffer, (nuint)bytes, null, BufferUsageARB.StaticDraw ); }
		return id;
	}

	static void ReadBuffer<T>( GL gl, uint id, T[] data ) where T : unmanaged
	{
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, id );
		var bytes = (nuint)(data.Length * Marshal.SizeOf<T>());
		unsafe
		{
			fixed ( T* p = data ) gl.GetBufferSubData( BufferTargetARB.ShaderStorageBuffer, 0, bytes, p );
		}
	}

	static void SetUniform( GL gl, uint program, string name, int value )
	{
		var loc = gl.GetUniformLocation( program, name );
		if ( loc >= 0 ) gl.Uniform1( loc, value );
	}

	void EnsureProgram( GL gl )
	{
		if ( _program != 0 ) return;
		var shader = gl.CreateShader( ShaderType.ComputeShader );
		gl.ShaderSource( shader, SubdivideSrc );
		gl.CompileShader( shader );
		gl.GetShader( shader, ShaderParameterName.CompileStatus, out int compiled );
		if ( compiled == 0 )
		{
			var log = gl.GetShaderInfoLog( shader );
			gl.DeleteShader( shader );
			throw new InvalidOperationException( $"GPU subdivision shader failed to compile: {log}" );
		}
		_program = gl.CreateProgram();
		gl.AttachShader( _program, shader );
		gl.LinkProgram( _program );
		gl.GetProgram( _program, ProgramPropertyARB.LinkStatus, out int linked );
		if ( linked == 0 )
		{
			var log = gl.GetProgramInfoLog( _program );
			gl.DeleteProgram( _program );
			_program = 0;
			throw new InvalidOperationException( $"GPU subdivision program failed to link: {log}" );
		}
		gl.DeleteShader( shader );
	}

	const string SubdivideSrc = @"#version 430 core
layout(local_size_x = 64) in;
layout(std430, binding = 0) readonly buffer PosIn { float posIn[]; };
layout(std430, binding = 1) readonly buffer StencilIdx { int stencilIdx[]; };
layout(std430, binding = 2) readonly buffer StencilW { float stencilW[]; };
layout(std430, binding = 3) readonly buffer StencilOff { int stencilOff[]; };
layout(std430, binding = 4) buffer PosOut { float posOut[]; };
uniform int uOutVerts;
void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i >= uint(uOutVerts)) return;
    vec3 sum = vec3(0.0);
    for (int k = stencilOff[i]; k < stencilOff[i + 1]; k++) {
        int src = stencilIdx[k];
        sum += stencilW[k] * vec3(posIn[src * 3], posIn[src * 3 + 1], posIn[src * 3 + 2]);
    }
    posOut[i * 3] = sum.x;
    posOut[i * 3 + 1] = sum.y;
    posOut[i * 3 + 2] = sum.z;
}
";
}
