using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>GPU-only directional key-light depth pass, shared by EGL and the viewport.</summary>
public sealed class KeyShadowMap : IDisposable
{
	public const int Resolution = 2048;
	public static readonly Vector3 Direction = Vector3.Normalize( new Vector3( 0.6f, -0.7f, 0.9f ) );
	readonly GL _gl;
	readonly uint _framebuffer;
	readonly GlShader _shader;
	readonly int _mvp, _skinned, _bones;
	public uint Texture { get; }
	public Matrix4x4 ViewProjection { get; private set; }

	public KeyShadowMap( GL gl )
	{
		_gl = gl;
		_shader = new GlShader( gl, Program.VertexSrc, "#version 460 core\nvoid main() {}" );
		_mvp = _shader.Uniform( "uMvp" ); _skinned = _shader.Uniform( "uSkinned" ); _bones = _shader.Uniform( "uBones[0]" );
		Texture = gl.GenTexture();
		gl.BindTexture( TextureTarget.Texture2D, Texture );
		unsafe { gl.TexImage2D( TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, Resolution, Resolution, 0, PixelFormat.DepthComponent, PixelType.Float, null ); }
		gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest );
		gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest );
		gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge );
		gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge );
		_framebuffer = gl.GenFramebuffer();
		gl.BindFramebuffer( FramebufferTarget.Framebuffer, _framebuffer );
		gl.FramebufferTexture2D( FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, Texture, 0 );
		gl.DrawBuffer( DrawBufferMode.None ); gl.ReadBuffer( ReadBufferMode.None );
		if ( gl.CheckFramebufferStatus( FramebufferTarget.Framebuffer ) != GLEnum.FramebufferComplete )
			throw new InvalidOperationException( "Key-light shadow framebuffer is incomplete." );
		gl.BindFramebuffer( FramebufferTarget.Framebuffer, 0 );
	}

	public void Draw( ViewportMesh mesh, MarionettePlayer player )
	{
		float radius = Math.Max( mesh.Radius * 1.15f, 1 );
		float near = 0.1f, far = radius * 8;
		var view = Matrix4x4.CreateLookAt( mesh.Centre + Direction * radius * 3, mesh.Centre, Vector3.UnitZ );
		var projection = Matrix4x4.CreateOrthographic( radius * 2, radius * 2, near, far );
		projection.M33 = -2f / (far - near);
		projection.M43 = -(far + near) / (far - near);
		ViewProjection = view * projection;
		_gl.BindTexture( TextureTarget.Texture2D, 0 );
		_gl.BindFramebuffer( FramebufferTarget.Framebuffer, _framebuffer );
		_gl.Viewport( 0, 0, Resolution, Resolution );
		_gl.Enable( EnableCap.DepthTest ); _gl.DepthMask( true );
		_gl.Enable( EnableCap.PolygonOffsetFill ); _gl.PolygonOffset( 2f, 4f );
		_gl.Clear( ClearBufferMask.DepthBufferBit );
		_shader.Use(); _shader.SetMatrix( _mvp, ViewProjection );
		bool skinned = mesh.HasSkin && player is not null;
		_shader.SetInt( _skinned, skinned ? 1 : 0 );
		if ( skinned ) _shader.SetMatrixArray( _bones, player.GpuBones );
		mesh.Draw( _gl );
		_gl.Disable( EnableCap.PolygonOffsetFill );
	}

	public void Dispose()
	{
		_gl.DeleteFramebuffer( _framebuffer ); _gl.DeleteTexture( Texture ); _shader.Dispose();
	}
}
