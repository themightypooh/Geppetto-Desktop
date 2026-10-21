using System;
using System.Threading;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>An FBO with an optional colour attachment of a requested internal format and an
/// optional depth texture. <see cref="Ensure"/> (re)creates the attachments when the size
/// changes and deletes the old ones; <see cref="Dispose"/> deletes everything. GL thread only.
/// Texture creation binds onto whichever texture unit is active, so callers that hold
/// sampler bindings select a scratch unit first.</summary>
public sealed class RenderTarget : IDisposable
{
	static int _live;
	/// <summary>GL objects (framebuffers + textures) currently owned by all render targets.</summary>
	public static int LiveObjects => Volatile.Read( ref _live );

	readonly GL _gl;
	public InternalFormat? ColorFormat { get; }
	public bool HasDepth { get; }
	public TextureMinFilter Filter { get; }
	public uint Framebuffer { get; private set; }
	public uint ColorTexture { get; private set; }
	public uint DepthTexture { get; private set; }
	public int Width { get; private set; }
	public int Height { get; private set; }

	public RenderTarget( GL gl, InternalFormat? colorFormat, bool depth, TextureMinFilter filter = TextureMinFilter.Linear )
	{
		if ( colorFormat is null && !depth ) throw new ArgumentException( "A render target needs a colour or a depth attachment." );
		_gl = gl; ColorFormat = colorFormat; HasDepth = depth; Filter = filter;
	}

	/// <summary>Creates or resizes the attachments. Returns true when GL objects were (re)created;
	/// the target's framebuffer is left bound in that case.</summary>
	public unsafe bool Ensure( int width, int height )
	{
		width = Math.Max( 1, width ); height = Math.Max( 1, height );
		if ( Framebuffer != 0 && Width == width && Height == height ) return false;
		Release();
		Width = width; Height = height;
		try
		{
			if ( ColorFormat is { } format )
			{
				ColorTexture = _gl.GenTexture(); Interlocked.Increment( ref _live );
				_gl.BindTexture( TextureTarget.Texture2D, ColorTexture );
				var (pixelFormat, pixelType) = UploadFormat( format );
				_gl.TexImage2D( TextureTarget.Texture2D, 0, format, (uint)width, (uint)height, 0, pixelFormat, pixelType, null );
				_gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)Filter );
				_gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)( Filter == TextureMinFilter.Nearest ? GLEnum.Nearest : GLEnum.Linear ) );
				_gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge );
				_gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge );
			}
			if ( HasDepth )
			{
				DepthTexture = _gl.GenTexture(); Interlocked.Increment( ref _live );
				_gl.BindTexture( TextureTarget.Texture2D, DepthTexture );
				_gl.TexImage2D( TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, (uint)width, (uint)height, 0, PixelFormat.DepthComponent, PixelType.Float, null );
				_gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest );
				_gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest );
				_gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge );
				_gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge );
			}
			Framebuffer = _gl.GenFramebuffer(); Interlocked.Increment( ref _live );
			_gl.BindFramebuffer( FramebufferTarget.Framebuffer, Framebuffer );
			if ( ColorTexture != 0 )
				_gl.FramebufferTexture2D( FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, ColorTexture, 0 );
			if ( DepthTexture != 0 )
				_gl.FramebufferTexture2D( FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, DepthTexture, 0 );
			if ( ColorTexture == 0 ) { _gl.DrawBuffer( DrawBufferMode.None ); _gl.ReadBuffer( ReadBufferMode.None ); }
			var status = _gl.CheckFramebufferStatus( FramebufferTarget.Framebuffer );
			if ( status != GLEnum.FramebufferComplete ) throw new InvalidOperationException( $"Render target {width}x{height} {ColorFormat} incomplete: {status}" );
		}
		catch
		{
			Release();
			throw;
		}
		return true;
	}

	static (PixelFormat, PixelType) UploadFormat( InternalFormat format ) => format switch
	{
		InternalFormat.Rgba16f or InternalFormat.Rgb16f or InternalFormat.R11fG11fB10f => (PixelFormat.Rgba, PixelType.HalfFloat),
		InternalFormat.Rgba32f => (PixelFormat.Rgba, PixelType.Float),
		_ => (PixelFormat.Rgba, PixelType.UnsignedByte),
	};

	void Release()
	{
		if ( Framebuffer != 0 ) { _gl.DeleteFramebuffer( Framebuffer ); Framebuffer = 0; Interlocked.Decrement( ref _live ); }
		if ( ColorTexture != 0 ) { _gl.DeleteTexture( ColorTexture ); ColorTexture = 0; Interlocked.Decrement( ref _live ); }
		if ( DepthTexture != 0 ) { _gl.DeleteTexture( DepthTexture ); DepthTexture = 0; Interlocked.Decrement( ref _live ); }
		Width = Height = 0;
	}

	public void Dispose() => Release();
}
