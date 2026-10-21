using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

public enum AntiAliasingMode { None, Temporal, Msaa4 }

/// <summary>Motion-reprojected HDR TAA, with camera-only fallback when no velocity
/// field is supplied. Geometry without reliable motion vectors rejects history.
/// Opt-in only: deterministic single-frame captures default to no jitter/history.</summary>
public sealed class TaaPass : IDisposable
{
    bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; Reset(); } }
    }
    GL _gl;
    GlShader _shader;
    RenderTarget _history, _resolve;
    uint _vao;
    bool _valid;
    int _frame;
    Matrix4x4 _previous;
    int _width, _height;
    Vector2 _currentJitter, _previousJitter;
    /// <summary>Neighbourhood sigma clipping; lower values reject more history.</summary>
    public float VarianceGamma { get; set; } = 1.25f;
    /// <summary>Maximum static history blend, capped below one to keep converging.</summary>
    public float HistoryWeight { get; set; } = 0.9f;
    readonly int[] _uniforms = new int[15];
    public int FrameIndex => _frame;

    public void Reset() { _valid = false; _frame = 0; }

    public Vector2 Jitter(int width, int height)
    {
        if (!Enabled) return Vector2.Zero;
        if (_width != width || _height != height)
        {
            Reset(); _width = width; _height = height;
        }
        int index = _frame % 8 + 1;
        _currentJitter = new Vector2((Halton(index, 2) - 0.5f) * 2 / Math.Max(1, width),
            (Halton(index, 3) - 0.5f) * 2 / Math.Max(1, height));
        return _currentJitter;
    }

    static float Halton(int index, int radix)
    {
        float value = 0, fraction = 1;
        while (index > 0) { fraction /= radix; value += fraction * (index % radix); index /= radix; }
        return value;
    }

    void Initialize(GL gl)
    {
        if (_gl is not null)
        {
            if (!ReferenceEquals(_gl, gl)) throw new InvalidOperationException("TAA cannot be shared between GL contexts.");
            return;
        }
        var shader = new GlShader(gl, TaaShaders.Vertex, TaaShaders.Fragment);
        try
        {
            _history = new RenderTarget(gl, InternalFormat.Rgba16f, true);
            _resolve = new RenderTarget(gl, InternalFormat.Rgba16f, false);
            _vao = gl.GenVertexArray();
            _shader = shader; _gl = gl;
        }
        catch { shader.Dispose(); throw; }
        string[] names = { "uCurrent", "uDepth", "uHistory", "uHistoryDepth", "uInverseCurrent", "uPrevious", "uValid", "uTexel", "uVarianceGamma", "uHistoryWeight", "uVelocity", "uVelocityDepth", "uHasVelocity", "uCurrentJitter", "uPreviousJitter" };
        for (int i = 0; i < names.Length; i++) _uniforms[i] = _shader.Uniform(names[i]);
    }

    public void Resolve(GL gl, uint color, uint depth, uint destination, int width, int height,
        Matrix4x4 viewProjection, bool rejectHistory, uint velocityTexture = 0, uint velocityDepthTexture = 0)
    {
        if (!Enabled) { Reset(); return; }
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (color == 0 || depth == 0 || destination == 0)
            throw new ArgumentException("TAA requires offscreen colour and depth targets.");
        Initialize(gl);
        gl.ActiveTexture(TextureUnit.Texture11);
        if (_history.Ensure(width, height)) _valid = false;
        _resolve.Ensure(width, height);
        if (!Matrix4x4.Invert(viewProjection, out var inverse)) { Reset(); return; }
        // Large projection/view changes are camera cuts, not motion to accumulate.
        bool history = _valid && !rejectHistory && !IsCameraCut(inverse, _previous);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _resolve.Framebuffer);
        gl.Viewport(0, 0, (uint)width, (uint)height);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.Blend);
        _shader.Use();
        Span<uint> textures = stackalloc uint[] { color, depth, _history.ColorTexture, _history.DepthTexture };
        for (int i = 0; i < 4; i++)
        {
            gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture11 + i));
            gl.BindTexture(TextureTarget.Texture2D, textures[i]);
            _shader.SetInt(_uniforms[i], 11 + i);
        }
        _shader.SetMatrix(_uniforms[4], inverse);
        _shader.SetMatrix(_uniforms[5], _previous);
        _shader.SetInt(_uniforms[6], history ? 1 : 0);
        _shader.SetVec2(_uniforms[7], 1f / width, 1f / height);
        _shader.SetFloat(_uniforms[8], float.IsFinite(VarianceGamma) ? Math.Clamp(VarianceGamma, 0.5f, 3f) : 1.25f);
        _shader.SetFloat(_uniforms[9], float.IsFinite(HistoryWeight) ? Math.Clamp(HistoryWeight, 0, 0.97f) : 0.9f);
        gl.ActiveTexture(TextureUnit.Texture15);
        gl.BindTexture(TextureTarget.Texture2D, velocityTexture != 0 ? velocityTexture : depth);
        _shader.SetInt(_uniforms[10], 15);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, velocityDepthTexture != 0 ? velocityDepthTexture : depth);
        _shader.SetInt(_uniforms[11], 0);
        _shader.SetInt(_uniforms[12], velocityTexture != 0 && velocityDepthTexture != 0 ? 1 : 0);
        _shader.SetVec2(_uniforms[13], _currentJitter.X * 0.5f, _currentJitter.Y * 0.5f);
        _shader.SetVec2(_uniforms[14], _previousJitter.X * 0.5f, _previousJitter.Y * 0.5f);
        gl.BindVertexArray(_vao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _resolve.Framebuffer);
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, destination);
        gl.BlitFramebuffer(0, 0, width, height, 0, 0, width, height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        // Destination carries the lit scene depth, untouched by the colour-only blit.
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, destination);
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _history.Framebuffer);
        gl.BlitFramebuffer(0, 0, width, height, 0, 0, width, height,
            ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, destination);
        _previous = viewProjection;
        _previousJitter = _currentJitter;
        _valid = !rejectHistory;
        _frame = (_frame + 1) & 0x7fffffff;
    }

    static bool IsCameraCut(Matrix4x4 inverseCurrent, Matrix4x4 previous)
    {
        // Compare frustum points after homogeneous reprojection. Unlike a raw
        // matrix-element threshold this is independent of scene-unit scale.
        for (int i = 0; i < 5; i++)
        {
            var clip = new Vector4(i == 0 ? 0 : (i & 1) == 0 ? -0.5f : 0.5f,
                i == 0 ? 0 : (i & 2) == 0 ? -0.5f : 0.5f, 0, 1);
            var world = Vector4.Transform(clip, inverseCurrent);
            var old = Vector4.Transform(world, previous);
            if (!float.IsFinite(old.W) || old.W <= 1e-8f) return true;
            float x = old.X / old.W, y = old.Y / old.W;
            if (!float.IsFinite(x + y) || MathF.Abs(x - clip.X) > 0.5f || MathF.Abs(y - clip.Y) > 0.5f)
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_gl is null) return;
        _history.Dispose(); _resolve.Dispose(); _shader.Dispose(); _gl.DeleteVertexArray(_vao);
        _gl = null; Reset();
    }
}
