using System;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>
/// GPU particle update via compute shader (GL 4.3+). Positions/velocities live in an SSBO;
/// <see cref="Dispatch"/> runs the compute kernel and <see cref="Download"/> reads results back.
/// No CPU fallback simulation is performed here.
/// </summary>
public static class GpuParticleSystem
{
	public const int StrideFloats = 6; // pos(3) + vel(3)
	public const int StrideBytes = StrideFloats * sizeof(float);

	static readonly string ComputeSrc = @"#version 430 core
layout(local_size_x = 256) in;
layout(std430, binding = 0) buffer Particles {
    float data[];
};
uniform float uDt;
uniform vec3 uBounds;
void main() {
    uint i = gl_GlobalInvocationID.x;
    uint base = i * 6;
    vec3 p = vec3(data[base], data[base+1], data[base+2]);
    vec3 v = vec3(data[base+3], data[base+4], data[base+5]);
    p += v * uDt;
    // Bounce inside [-bounds, bounds].
    for (int k = 0; k < 3; k++) {
        if (p[k] > uBounds[k]) { p[k] = uBounds[k]; v[k] = -v[k]; }
        if (p[k] < -uBounds[k]) { p[k] = -uBounds[k]; v[k] = -v[k]; }
    }
    data[base] = p.x; data[base+1] = p.y; data[base+2] = p.z;
    data[base+3] = v.x; data[base+4] = v.y; data[base+5] = v.z;
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

	/// <summary>Allocate and initialize an SSBO for <paramref name="particleCount"/> particles.
	/// Initial positions random-ish in [-1,1], velocities small. Returns buffer handle.</summary>
	public static uint Upload( GL gl, int particleCount, uint seed = 12345u )
	{
		if ( particleCount < 0 ) throw new ArgumentOutOfRangeException( nameof( particleCount ) );
		uint buf = gl.GenBuffer();
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, buf );
		var data = new float[particleCount * StrideFloats];
		var rng = new Random( (int)seed );
		for ( int i = 0; i < particleCount; i++ )
		{
			int b = i * StrideFloats;
			data[b] = (float)(rng.NextDouble() * 2 - 1);
			data[b+1] = (float)(rng.NextDouble() * 2 - 1);
			data[b+2] = (float)(rng.NextDouble() * 2 - 1);
			data[b+3] = (float)(rng.NextDouble() * 0.2 - 0.1);
			data[b+4] = (float)(rng.NextDouble() * 0.2 - 0.1);
			data[b+5] = (float)(rng.NextDouble() * 0.2 - 0.1);
		}
		unsafe
		{
			fixed ( float* ptr = data )
			{
				gl.BufferData( BufferTargetARB.ShaderStorageBuffer, (nuint)(data.Length * sizeof(float)), ptr, BufferUsageARB.DynamicDraw );
			}
		}
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, 0 );
		return buf;
	}

	/// <summary>Run the compute kernel for <paramref name="particleCount"/> particles with the given timestep.</summary>
	public static void Dispatch( GL gl, uint program, uint buffer, int particleCount, float dt, System.Numerics.Vector3 bounds )
	{
		if ( particleCount < 0 ) throw new ArgumentOutOfRangeException( nameof( particleCount ) );
		gl.UseProgram( program );
		int dtLoc = gl.GetUniformLocation( program, "uDt" );
		int boundsLoc = gl.GetUniformLocation( program, "uBounds" );
		gl.Uniform1( dtLoc, dt );
		gl.Uniform3( boundsLoc, bounds.X, bounds.Y, bounds.Z );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 0, buffer );
		uint groups = (uint)((particleCount + 255) / 256);
		if ( groups == 0 ) groups = 1;
		gl.DispatchCompute( groups, 1, 1 );
		gl.MemoryBarrier( MemoryBarrierMask.ShaderStorageBarrierBit );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 0, 0 );
	}

	/// <summary>Read back particle data (pos+vel) for <paramref name="particleCount"/> particles.</summary>
	public static float[] Download( GL gl, uint buffer, int particleCount )
	{
		var data = new float[particleCount * StrideFloats];
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, buffer );
		unsafe
		{
			fixed ( float* ptr = data )
			{
				gl.GetBufferSubData( BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(data.Length * sizeof(float)), ptr );
			}
		}
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, 0 );
		return data;
	}

	public static void Dispose( GL gl, uint buffer, uint program )
	{
		if ( buffer != 0 ) gl.DeleteBuffer( buffer );
		if ( program != 0 ) gl.DeleteProgram( program );
	}
}