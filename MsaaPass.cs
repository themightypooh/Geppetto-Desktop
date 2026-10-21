using System;
using System.Threading;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>Explicit four-sample linear HDR alternative to temporal AA.
/// Colour is resolved at the exact requested extent. Renderbuffers are GL-thread owned.</summary>
public sealed class MsaaPass : IDisposable
{
    readonly GL _gl;
    uint _fbo, _color, _depth;
    int _width, _height;
    static int _live;
    public static int LiveObjects => Volatile.Read(ref _live);
    public uint Framebuffer => _fbo;

    public MsaaPass(GL gl) { _gl = gl; }

    public void Ensure(int width, int height)
    {
        if (_fbo != 0 && width == _width && height == _height) return;
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        // GL_MAX_SAMPLES is absent from this Silk.NET version's GetPName enum.
        _gl.GetInteger((GetPName)0x8D57, out int maxSamples);
        if (maxSamples < 4) throw new NotSupportedException("Four-sample HDR MSAA is unavailable on this host.");
        Dispose();
        _width = width; _height = height;
        try
        {
            _fbo = _gl.GenFramebuffer(); Interlocked.Increment(ref _live);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            _color = _gl.GenRenderbuffer(); Interlocked.Increment(ref _live);
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _color);
            _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, 4, InternalFormat.Rgba16f, (uint)width, (uint)height);
            _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _color);
            _depth = _gl.GenRenderbuffer(); Interlocked.Increment(ref _live);
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
            _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, 4, InternalFormat.DepthComponent24, (uint)width, (uint)height);
            _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _depth);
            if (_gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
                throw new NotSupportedException("Four-sample HDR MSAA framebuffer is incomplete.");
        }
        catch { Dispose(); throw; }
    }

    public void Resolve(uint destination)
    {
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, destination);
        _gl.BlitFramebuffer(0, 0, _width, _height, 0, 0, _width, _height,
            ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, destination);
    }

    public void Dispose()
    {
        if (_fbo != 0) { _gl.DeleteFramebuffer(_fbo); _fbo = 0; Interlocked.Decrement(ref _live); }
        if (_color != 0) { _gl.DeleteRenderbuffer(_color); _color = 0; Interlocked.Decrement(ref _live); }
        if (_depth != 0) { _gl.DeleteRenderbuffer(_depth); _depth = 0; Interlocked.Decrement(ref _live); }
        _width = _height = 0;
    }
}
