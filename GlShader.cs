using System;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

public sealed class GlShader : IDisposable
{
	readonly GL _gl;
	public uint Handle { get; }

	public GlShader( GL gl, string vertexSource, string fragmentSource )
	{
		_gl = gl;
		var vs = Compile( ShaderType.VertexShader, vertexSource );
		var fs = Compile( ShaderType.FragmentShader, fragmentSource );
		Handle = _gl.CreateProgram();
		_gl.AttachShader( Handle, vs );
		_gl.AttachShader( Handle, fs );
		_gl.LinkProgram( Handle );
		_gl.GetProgram( Handle, ProgramPropertyARB.LinkStatus, out int linked );
		if ( linked == 0 )
		{
			var log = _gl.GetProgramInfoLog( Handle );
			_gl.DeleteShader( vs );
			_gl.DeleteShader( fs );
			_gl.DeleteProgram( Handle );
			throw new InvalidOperationException( "shader link failed:\n" + log );
		}
		_gl.DetachShader( Handle, vs );
		_gl.DetachShader( Handle, fs );
		_gl.DeleteShader( vs );
		_gl.DeleteShader( fs );
	}

	uint Compile( ShaderType type, string source )
	{
		var shader = _gl.CreateShader( type );
		_gl.ShaderSource( shader, source );
		_gl.CompileShader( shader );
		_gl.GetShader( shader, ShaderParameterName.CompileStatus, out int ok );
		if ( ok == 0 )
		{
			var log = _gl.GetShaderInfoLog( shader );
			_gl.DeleteShader( shader );
			throw new InvalidOperationException( $"{type} compile failed:\n" + log );
		}
		return shader;
	}

	public void Use() => _gl.UseProgram( Handle );

	public int Uniform( string name ) => _gl.GetUniformLocation( Handle, name );

	public unsafe void SetMatrix( int location, System.Numerics.Matrix4x4 m )
	{
		if ( location < 0 ) return;
		_gl.UniformMatrix4( location, 1, false, (float*)&m );
	}

	public unsafe void SetMatrixArray( int location, ReadOnlySpan<System.Numerics.Matrix4x4> matrices )
	{
		if ( location < 0 || matrices.IsEmpty ) return;
		fixed ( System.Numerics.Matrix4x4* ptr = matrices )
		{
			_gl.UniformMatrix4( location, (uint)matrices.Length, false, (float*)ptr );
		}
	}

	public void SetVec3( int location, float x, float y, float z )
	{
		if ( location < 0 ) return;
		_gl.Uniform3( location, x, y, z );
	}

	public void SetFloat( int location, float v )
	{
		if ( location < 0 ) return;
		_gl.Uniform1( location, v );
	}

	public void SetInt( int location, int v )
	{
		if ( location < 0 ) return;
		_gl.Uniform1( location, v );
	}

	public void Dispose() => _gl.DeleteProgram( Handle );
}
