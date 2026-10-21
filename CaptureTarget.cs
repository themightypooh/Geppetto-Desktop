using System;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>A one-shot capture is sized independently of window-manager limits.</summary>
public sealed class CaptureTarget : IDisposable
{
	readonly GL _gl;
	readonly uint _framebuffer, _color, _depth;
	readonly int _width, _height;
	public uint Handle => _framebuffer;

	public CaptureTarget( GL gl, int width, int height )
	{
		_gl = gl; _width = width; _height = height;
		gl.GetInteger( GetPName.MaxRenderbufferSize, out int max );
		if ( width > max || height > max ) throw new ArgumentOutOfRangeException( nameof( width ), "Capture exceeds GPU renderbuffer limits." );
		_framebuffer = gl.GenFramebuffer(); _color = gl.GenRenderbuffer(); _depth = gl.GenRenderbuffer();
		gl.BindFramebuffer( FramebufferTarget.Framebuffer, _framebuffer );
		gl.BindRenderbuffer( RenderbufferTarget.Renderbuffer, _color );
		gl.RenderbufferStorage( RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, (uint)width, (uint)height );
		gl.FramebufferRenderbuffer( FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _color );
		gl.BindRenderbuffer( RenderbufferTarget.Renderbuffer, _depth );
		gl.RenderbufferStorage( RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)width, (uint)height );
		gl.FramebufferRenderbuffer( FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _depth );
		if ( gl.CheckFramebufferStatus( FramebufferTarget.Framebuffer ) != GLEnum.FramebufferComplete )
		{
			Dispose();
			throw new InvalidOperationException( "Capture framebuffer is incomplete." );
		}
		gl.Viewport( 0, 0, (uint)width, (uint)height );
	}

	public void Present( int width, int height )
	{
		_gl.BindFramebuffer( FramebufferTarget.ReadFramebuffer, _framebuffer );
		_gl.BindFramebuffer( FramebufferTarget.DrawFramebuffer, 0 );
		_gl.BlitFramebuffer( 0, 0, _width, _height, 0, 0, width, height,
			ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear );
		_gl.BindFramebuffer( FramebufferTarget.Framebuffer, 0 );
		_gl.Viewport( 0, 0, (uint)width, (uint)height );
	}

	public void Dispose()
	{
		_gl.BindFramebuffer( FramebufferTarget.Framebuffer, 0 );
		_gl.DeleteRenderbuffer( _depth ); _gl.DeleteRenderbuffer( _color ); _gl.DeleteFramebuffer( _framebuffer );
	}
}
