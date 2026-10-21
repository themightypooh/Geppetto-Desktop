using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>R5 screen-space ambient occlusion: view-space positions reconstructed from the
/// pre-pass depth, normals reconstructed from depth, a 16-sample hemisphere kernel rotated
/// by a 4x4 noise texture with range checks, then a separable depth-aware bilateral blur.
/// The lit shader multiplies its ambient occlusion by <see cref="Texture"/>.</summary>
public sealed class SsaoPass : IDisposable
{
	const int KernelSize = 16;
	GL _gl;
	UniformCache _uniforms;
	RenderTarget _raw, _blurH, _blurV;
	uint _vao, _noise, _aoProg, _blurProg;
	readonly Vector3[] _kernel = BuildKernel();

	public bool Enabled { get; set; } = true;
	/// <summary>Sample radius in world units; 0 = 3% of the camera's orbit distance.</summary>
	public float Radius { get; set; } = 0f;
	public float Intensity { get; set; } = 1.0f;
	/// <summary>Depth bias in world units as a fraction of the radius (avoids self-occlusion acne).</summary>
	public float Bias { get; set; } = 0.025f;
	/// <summary>Blurred AO (R8, 1 = unoccluded), or 0 before the first <see cref="Render"/>.</summary>
	public uint Texture => _blurV?.ColorTexture ?? 0;
	/// <summary>A 1x1 white texture to bind when AO is disabled.</summary>
	public uint White { get; private set; }

	static Vector3[] BuildKernel()
	{
		// Deterministic hemisphere kernel (+Z), denser near the origin.
		var rng = new Random( 1337 );
		var k = new Vector3[KernelSize];
		for ( int i = 0; i < KernelSize; i++ )
		{
			var v = Vector3.Normalize( new Vector3( (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 0.9f + 0.1f ) );
			float s = i / (float)KernelSize;
			s = 0.1f + 0.9f * s * s;
			k[i] = v * s * (float)( 0.5 + 0.5 * rng.NextDouble() );
		}
		return k;
	}

	uint Link( string vs, string fs )
	{
		uint Compile( ShaderType type, string src )
		{
			uint sh = _gl.CreateShader( type );
			_gl.ShaderSource( sh, src ); _gl.CompileShader( sh );
			_gl.GetShader( sh, ShaderParameterName.CompileStatus, out int ok );
			if ( ok == 0 ) { var log = _gl.GetShaderInfoLog( sh ); _gl.DeleteShader( sh ); throw new InvalidOperationException( log ); }
			return sh;
		}
		uint v = Compile( ShaderType.VertexShader, vs ), f = Compile( ShaderType.FragmentShader, fs );
		uint p = _gl.CreateProgram();
		_gl.AttachShader( p, v ); _gl.AttachShader( p, f ); _gl.LinkProgram( p );
		_gl.GetProgram( p, ProgramPropertyARB.LinkStatus, out int linked );
		_gl.DetachShader( p, v ); _gl.DetachShader( p, f ); _gl.DeleteShader( v ); _gl.DeleteShader( f );
		if ( linked == 0 ) { var log = _gl.GetProgramInfoLog( p ); _gl.DeleteProgram( p ); throw new InvalidOperationException( log ); }
		return p;
	}

	/// <summary>Creates or resizes the targets. Binds textures on the active unit; callers
	/// holding sampler bindings select a scratch unit first.</summary>
	public unsafe void Ensure( GL gl, int w, int h )
	{
		if ( _gl is null )
		{
			_gl = gl;
			_uniforms = new UniformCache( gl );
			_vao = gl.GenVertexArray();
			_raw = new RenderTarget( gl, InternalFormat.R8, depth: false );
			_blurH = new RenderTarget( gl, InternalFormat.R8, depth: false );
			_blurV = new RenderTarget( gl, InternalFormat.R8, depth: false );
			_aoProg = Link( PostChainShaders.FullscreenVs, ScreenSpaceShaders.SsaoFs );
			_blurProg = Link( PostChainShaders.FullscreenVs, ScreenSpaceShaders.BilateralBlurFs );
			// 4x4 rotation noise: random tangent-plane vectors, tiled across the screen.
			var rng = new Random( 7 );
			var noise = new float[16 * 4];
			for ( int i = 0; i < 16; i++ )
			{
				float a = (float)( rng.NextDouble() * Math.PI * 2 );
				noise[i * 4] = MathF.Cos( a ); noise[i * 4 + 1] = MathF.Sin( a ); noise[i * 4 + 3] = 1;
			}
			_noise = gl.GenTexture();
			gl.BindTexture( TextureTarget.Texture2D, _noise );
			fixed ( float* p = noise ) gl.TexImage2D( TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, 4, 4, 0, PixelFormat.Rgba, PixelType.Float, p );
			gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest );
			gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest );
			gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat );
			gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat );
			White = gl.GenTexture();
			gl.BindTexture( TextureTarget.Texture2D, White );
			byte* white = stackalloc byte[] { 255, 255, 255, 255 };
			gl.TexImage2D( TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, white );
		}
		_raw.Ensure( w, h ); _blurH.Ensure( w, h ); _blurV.Ensure( w, h );
	}

	/// <summary>Computes AO from <paramref name="depthTex"/>. Leaves texture units 0-1 and the
	/// framebuffer binding changed; restores nothing else.</summary>
	public unsafe void Render( uint depthTex, Matrix4x4 projection, float cameraDistance )
	{
		if ( _gl is null ) return;
		var gl = _gl;
		Matrix4x4.Invert( projection, out var invProj );
		gl.BindVertexArray( _vao );
		gl.Disable( EnableCap.DepthTest );
		gl.Disable( EnableCap.Blend );
		float radius = Radius > 0 ? Radius : MathF.Max( 1e-3f, cameraDistance * 0.03f );

		gl.UseProgram( _aoProg );
		int U( string n ) => _uniforms[_aoProg, n];
		gl.UniformMatrix4( U( "uProjection" ), 1, false, (float*)&projection );
		gl.UniformMatrix4( U( "uInvProjection" ), 1, false, (float*)&invProj );
		fixed ( Vector3* k = _kernel ) gl.Uniform3( U( "uKernel[0]" ), KernelSize, (float*)k );
		gl.Uniform1( U( "uRadius" ), radius );
		gl.Uniform1( U( "uBias" ), radius * Bias );
		gl.Uniform1( U( "uIntensity" ), Intensity );
		gl.Uniform2( U( "uNoiseScale" ), _raw.Width / 4f, _raw.Height / 4f );
		gl.Uniform1( U( "uDepth" ), 0 ); gl.Uniform1( U( "uNoise" ), 1 );
		gl.ActiveTexture( TextureUnit.Texture0 ); gl.BindTexture( TextureTarget.Texture2D, depthTex );
		gl.ActiveTexture( TextureUnit.Texture1 ); gl.BindTexture( TextureTarget.Texture2D, _noise );
		Draw( _raw );

		gl.UseProgram( _blurProg );
		int B( string n ) => _uniforms[_blurProg, n];
		gl.UniformMatrix4( B( "uInvProjection" ), 1, false, (float*)&invProj );
		gl.Uniform1( B( "uAo" ), 0 ); gl.Uniform1( B( "uDepth" ), 1 );
		gl.Uniform1( B( "uDepthSigma" ), radius * 0.5f );
		gl.ActiveTexture( TextureUnit.Texture1 ); gl.BindTexture( TextureTarget.Texture2D, depthTex );
		gl.ActiveTexture( TextureUnit.Texture0 ); gl.BindTexture( TextureTarget.Texture2D, _raw.ColorTexture );
		gl.Uniform2( B( "uStep" ), 1f / _raw.Width, 0f );
		Draw( _blurH );
		gl.BindTexture( TextureTarget.Texture2D, _blurH.ColorTexture );
		gl.Uniform2( B( "uStep" ), 0f, 1f / _raw.Height );
		Draw( _blurV );
		gl.Enable( EnableCap.DepthTest );
		gl.BindVertexArray( 0 );
	}

	void Draw( RenderTarget t )
	{
		_gl.BindFramebuffer( FramebufferTarget.Framebuffer, t.Framebuffer );
		_gl.Viewport( 0, 0, (uint)t.Width, (uint)t.Height );
		_gl.DrawArrays( PrimitiveType.Triangles, 0, 3 );
	}

	public void Dispose()
	{
		if ( _gl is null ) return;
		_raw.Dispose(); _blurH.Dispose(); _blurV.Dispose();
		_gl.DeleteTexture( _noise ); _gl.DeleteTexture( White );
		_gl.DeleteProgram( _aoProg ); _gl.DeleteProgram( _blurProg );
		_gl.DeleteVertexArray( _vao );
		_gl = null;
	}
}
