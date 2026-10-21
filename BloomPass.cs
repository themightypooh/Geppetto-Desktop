using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

public enum ViewTransform { AgX, Standard }
public enum ViewLook { None, Punchy }

/// <summary>Display settings for the final composite, mirroring Blender's Color Management panel.
/// The defaults match Blender's: AgX, look None, exposure 0, gamma 1, white balance 6500 K
/// (D65, neutral) with no tint.</summary>
public sealed record ViewSettings
{
	public ViewTransform Transform { get; init; } = ViewTransform.AgX;
	public ViewLook Look { get; init; } = ViewLook.None;
	/// <summary>Stops, applied to scene-linear HDR before the view transform.</summary>
	public float Exposure { get; init; } = 0f;
	public float Gamma { get; init; } = 1f;
	/// <summary>The illuminant the scene is assumed to be lit by, in kelvin. D65 (6500 K) is
	/// neutral; the correction removes that illuminant's tint from the linear image, the way
	/// a camera white balance does. See <see cref="WhiteBalance"/>.</summary>
	public float WhiteBalanceTemperature { get; init; } = WhiteBalance.ReferenceKelvin;
	/// <summary>Green (&lt; 0) to magenta (&gt; 0) tint, -1 to 1. Zero is neutral.</summary>
	public float WhiteBalanceTint { get; init; } = 0f;
}

/// <summary>The HDR post chain: owns the linear Rgba16f scene target, a progressive bloom mip
/// chain, the SSR history copy, the half-resolution depth-of-field layers, and the composite
/// that applies depth of field, bloom, the view transform and the single sRGB encode.</summary>
public sealed class BloomPass : IDisposable
{
	const int MaxLevels = 6;
	readonly GL _gl;
	readonly UniformCache _uniforms;
	readonly RenderTarget _scene;
	readonly RenderTarget _history;
	readonly List<RenderTarget> _mips = new();
	readonly RenderTarget _dofPrep, _dofFar, _dofNear;
	readonly uint _vao;
	uint _downProg, _upProg, _compositeProg, _dofPrepProg, _dofGatherProg;
	int _w, _h;

	public BloomPass( GL gl )
	{
		_gl = gl;
		_uniforms = new UniformCache( gl );
		// Fullscreen passes use gl_VertexID only, but core profile still requires a VAO.
		_vao = _gl.GenVertexArray();
		_scene = new RenderTarget( gl, InternalFormat.Rgba16f, depth: true );
		_history = new RenderTarget( gl, InternalFormat.Rgba16f, depth: false );
		_dofPrep = new RenderTarget( gl, InternalFormat.Rgba16f, depth: false );
		_dofFar = new RenderTarget( gl, InternalFormat.Rgba16f, depth: false );
		_dofNear = new RenderTarget( gl, InternalFormat.Rgba16f, depth: false );
	}

	/// <summary>Lens focal length in millimetres (Blender camera "Focal Length"). With the
	/// viewport's fixed vertical FOV the implied sensor height is 2 f tan(fov/2).</summary>
	public float FocalLengthMm { get; set; } = 50f;
	/// <summary>Aperture blade count for the bokeh shape; below 3 is a circular aperture.</summary>
	public int ApertureBlades { get; set; } = 0;
	/// <summary>Blade rotation in radians.</summary>
	public float ApertureRotation { get; set; } = 0f;
	/// <summary>Vertical field of view in degrees of the camera the scene was rendered with.</summary>
	public float FovDegrees { get; set; } = 45f;
	/// <summary>Largest CoC radius in full-resolution pixels (the near-field search radius).</summary>
	public float MaxCocRadius { get; set; } = 32f;
	/// <summary>Scene units per millimetre; the kernel works in inches.</summary>
	public const float SceneUnitsPerMm = 1f / 25.4f;

	public float Threshold { get; set; } = 1.0f;
	/// <summary>Soft-knee width as a fraction of <see cref="Threshold"/>.</summary>
	public float Knee { get; set; } = 0.5f;
	public float Intensity { get; set; } = 0.6f;
	/// <summary>Upsample tent radius in texels of each level.</summary>
	public float Radius { get; set; } = 1.0f;

	/// <summary>Linear HDR scene target the lit pass renders into.</summary>
	public uint ColorFbo => _scene.Framebuffer;
	public uint ColorTex => _scene.ColorTexture;
	/// <summary>Safe SSR source while rendering into <see cref="ColorFbo"/>: the previous frame's
	/// linear HDR colour. Sampling ColorTex during the main pass would be a feedback loop.</summary>
	public uint ScreenColorTex => _history.ColorTexture;
	/// <summary>Accumulated bloom at half resolution (sum of all levels).</summary>
	public uint BloomTex => _mips.Count > 0 ? _mips[0].ColorTexture : 0;
	public int Levels => _mips.Count;
	public int Width => _w;
	public int Height => _h;

	uint Compile( ShaderType type, string src )
	{
		uint sh = _gl.CreateShader( type );
		_gl.ShaderSource( sh, src );
		_gl.CompileShader( sh );
		_gl.GetShader( sh, ShaderParameterName.CompileStatus, out int ok );
		if ( ok == 0 ) { var log = _gl.GetShaderInfoLog( sh ); _gl.DeleteShader( sh ); throw new InvalidOperationException( log ); }
		return sh;
	}

	uint Link( string vs, string fs )
	{
		uint v = Compile( ShaderType.VertexShader, vs );
		uint f = Compile( ShaderType.FragmentShader, fs );
		uint p = _gl.CreateProgram();
		_gl.AttachShader( p, v ); _gl.AttachShader( p, f );
		_gl.LinkProgram( p );
		_gl.GetProgram( p, ProgramPropertyARB.LinkStatus, out int linked );
		_gl.DetachShader( p, v ); _gl.DetachShader( p, f );
		_gl.DeleteShader( v ); _gl.DeleteShader( f );
		if ( linked == 0 ) { var log = _gl.GetProgramInfoLog( p ); _gl.DeleteProgram( p ); throw new InvalidOperationException( log ); }
		return p;
	}

	public void Ensure( int w, int h )
	{
		if ( _w == w && _h == h && _scene.Framebuffer != 0 ) return;
		_w = w; _h = h;
		_scene.Ensure( w, h );
		if ( _history.Ensure( w, h ) )
		{
			// New history starts black, not undefined; HistoryValid keeps SSR off until stored.
			HistoryValid = false;
			_gl.ClearColor( 0, 0, 0, 1 );
			_gl.Clear( ClearBufferMask.ColorBufferBit );
		}
		foreach ( var mip in _mips ) mip.Dispose();
		_mips.Clear();
		int mw = w / 2, mh = h / 2;
		while ( _mips.Count < MaxLevels && mw >= 4 && mh >= 4 )
		{
			var mip = new RenderTarget( _gl, InternalFormat.Rgba16f, depth: false );
			mip.Ensure( mw, mh );
			_mips.Add( mip );
			mw /= 2; mh /= 2;
		}
		if ( _downProg == 0 )
		{
			_downProg = Link( PostChainShaders.FullscreenVs, PostChainShaders.DownsampleFs );
			_upProg = Link( PostChainShaders.FullscreenVs, PostChainShaders.UpsampleFs );
			_compositeProg = Link( PostChainShaders.FullscreenVs, PostChainShaders.Composite );
			_dofPrepProg = Link( PostChainShaders.FullscreenVs, PostChainShaders.DofPrepFs );
			_dofGatherProg = Link( PostChainShaders.FullscreenVs, PostChainShaders.DofGatherFs );
		}
		int hw = Math.Max( 1, ( w + 1 ) / 2 ), hh = Math.Max( 1, ( h + 1 ) / 2 );
		_dofPrep.Ensure( hw, hh ); _dofFar.Ensure( hw, hh ); _dofNear.Ensure( hw, hh );
	}

	void Fullscreen( uint fbo, int w, int h, uint src )
	{
		_gl.BindFramebuffer( FramebufferTarget.Framebuffer, fbo );
		_gl.Viewport( 0, 0, (uint)w, (uint)h );
		_gl.ActiveTexture( TextureUnit.Texture0 );
		_gl.BindTexture( TextureTarget.Texture2D, src );
		_gl.DrawArrays( PrimitiveType.Triangles, 0, 3 );
	}

	/// <summary>Builds the bloom chain from the linear HDR scene colour: thresholded Karis
	/// downsample to half resolution, 13-tap downsamples, then tent upsamples added back up.</summary>
	public void Process()
	{
		if ( _mips.Count == 0 ) return;
		_gl.BindVertexArray( _vao );
		_gl.Disable( EnableCap.DepthTest );
		_gl.Disable( EnableCap.Blend );
		_gl.UseProgram( _downProg );
		_gl.Uniform1( _uniforms[_downProg, "uSrc"], 0 );
		_gl.Uniform1( _uniforms[_downProg, "uThreshold"], Threshold );
		_gl.Uniform1( _uniforms[_downProg, "uKnee"], MathF.Max( 1e-4f, Threshold * Knee ) );
		int first = _uniforms[_downProg, "uFirst"];
		uint src = _scene.ColorTexture;
		for ( int i = 0; i < _mips.Count; i++ )
		{
			_gl.Uniform1( first, i == 0 ? 1 : 0 );
			Fullscreen( _mips[i].Framebuffer, _mips[i].Width, _mips[i].Height, src );
			src = _mips[i].ColorTexture;
		}
		_gl.UseProgram( _upProg );
		_gl.Uniform1( _uniforms[_upProg, "uSrc"], 0 );
		_gl.Uniform1( _uniforms[_upProg, "uRadius"], Radius );
		_gl.Enable( EnableCap.Blend );
		_gl.BlendFunc( BlendingFactor.One, BlendingFactor.One );
		for ( int i = _mips.Count - 1; i > 0; i-- )
			Fullscreen( _mips[i - 1].Framebuffer, _mips[i - 1].Width, _mips[i - 1].Height, _mips[i].ColorTexture );
		_gl.Disable( EnableCap.Blend );
		_gl.Enable( EnableCap.DepthTest );
	}

	void SetCoc( uint prog, float focus, float fStop, float near, float far )
	{
		void Set( string name, float value ) => _gl.Uniform1( _uniforms[prog, name], value );
		Set( "uFocus", MathF.Max( 1e-3f, focus ) ); Set( "uFStop", MathF.Max( 0f, fStop ) );
		Set( "uFocal", MathF.Max( 1f, FocalLengthMm ) * SceneUnitsPerMm );
		Set( "uTanHalfFov", MathF.Tan( Math.Clamp( FovDegrees, 1f, 179f ) * MathF.PI / 360f ) );
		Set( "uMaxCoc", MathF.Max( 1f, MaxCocRadius ) );
		Set( "uNear", near ); Set( "uFar", far );
	}

	/// <summary>Half-resolution DoF: colour + signed CoC prep, then far and near gathers.</summary>
	void DepthOfField( uint depthTex, float focus, float fStop, float near, float far )
	{
		_gl.BindVertexArray( _vao );
		_gl.Disable( EnableCap.Blend ); _gl.Disable( EnableCap.DepthTest );
		_gl.UseProgram( _dofPrepProg );
		SetCoc( _dofPrepProg, focus, fStop, near, far );
		_gl.Uniform1( _uniforms[_dofPrepProg, "uColor"], 0 );
		_gl.Uniform1( _uniforms[_dofPrepProg, "uDepth"], 1 );
		_gl.ActiveTexture( TextureUnit.Texture1 ); _gl.BindTexture( TextureTarget.Texture2D, depthTex );
		Fullscreen( _dofPrep.Framebuffer, _dofPrep.Width, _dofPrep.Height, _scene.ColorTexture );
		_gl.UseProgram( _dofGatherProg );
		void Set( string name, float value ) => _gl.Uniform1( _uniforms[_dofGatherProg, name], value );
		_gl.Uniform1( _uniforms[_dofGatherProg, "uPrep"], 0 );
		Set( "uMaxCoc", MathF.Max( 1f, MaxCocRadius ) ); Set( "uBladeRotation", ApertureRotation );
		_gl.Uniform1( _uniforms[_dofGatherProg, "uBlades"], ApertureBlades );
		int layer = _uniforms[_dofGatherProg, "uLayer"];
		_gl.Uniform1( layer, 0 ); Fullscreen( _dofFar.Framebuffer, _dofFar.Width, _dofFar.Height, _dofPrep.ColorTexture );
		_gl.Uniform1( layer, 1 ); Fullscreen( _dofNear.Framebuffer, _dofNear.Width, _dofNear.Height, _dofPrep.ColorTexture );
		_gl.Enable( EnableCap.DepthTest );
	}

	/// <summary>Writes the display-encoded image into <paramref name="target"/>. Depth of field
	/// is off when <paramref name="fStop"/> is 0; <paramref name="focus"/> is a view distance in
	/// scene units. The lens is <see cref="FocalLengthMm"/>, <see cref="ApertureBlades"/> etc.</summary>
	public void Composite( uint target, ViewSettings view, uint depthTex, float focus, float fStop, float near, float far )
	{
		bool dof = fStop > 0 && depthTex != 0;
		if ( dof ) DepthOfField( depthTex, focus, fStop, near, far );
		_gl.BindVertexArray( _vao );
		_gl.Disable( EnableCap.Blend );
		_gl.BindFramebuffer( FramebufferTarget.Framebuffer, target );
		_gl.Viewport( 0, 0, (uint)_w, (uint)_h );
		_gl.UseProgram( _compositeProg );
		void Set( string name, float value ) => _gl.Uniform1( _uniforms[_compositeProg, name], value );
		void SetInt( string name, int value ) => _gl.Uniform1( _uniforms[_compositeProg, name], value );
		_gl.ActiveTexture( TextureUnit.Texture0 ); _gl.BindTexture( TextureTarget.Texture2D, _scene.ColorTexture );
		_gl.ActiveTexture( TextureUnit.Texture1 ); _gl.BindTexture( TextureTarget.Texture2D, BloomTex );
		_gl.ActiveTexture( TextureUnit.Texture2 ); _gl.BindTexture( TextureTarget.Texture2D, depthTex );
		_gl.ActiveTexture( TextureUnit.Texture3 ); _gl.BindTexture( TextureTarget.Texture2D, _dofFar.ColorTexture );
		_gl.ActiveTexture( TextureUnit.Texture4 ); _gl.BindTexture( TextureTarget.Texture2D, _dofNear.ColorTexture );
		SetInt( "uColor", 0 ); SetInt( "uBloom", 1 ); SetInt( "uDepth", 2 ); SetInt( "uDofFar", 3 ); SetInt( "uDofNear", 4 );
		Set( "uBloomIntensity", _mips.Count > 0 ? Intensity : 0f );
		Set( "uBloomLevels", Math.Max( 1, _mips.Count ) );
		SetCoc( _compositeProg, focus, dof ? fStop : 0f, near, far );
		Set( "uViewExposure", view.Exposure ); Set( "uGamma", view.Gamma <= 0 ? 1f : view.Gamma );
		SetInt( "uViewTransform", (int)view.Transform ); SetInt( "uLook", (int)view.Look );
		var balance = WhiteBalance.Gain( view.WhiteBalanceTemperature, view.WhiteBalanceTint );
		_gl.Uniform3( _uniforms[_compositeProg, "uWhiteBalance"], balance.X, balance.Y, balance.Z );
		_gl.DrawArrays( PrimitiveType.Triangles, 0, 3 );
	}

	/// <summary>False until a frame has been stored since creation, resize or reset; SSR then
	/// falls back to the environment instead of reflecting black.</summary>
	public bool HistoryValid { get; private set; }

	/// <summary>Rejects the SSR history (camera cut, source reload) rather than ghosting the
	/// previous view.</summary>
	public void ResetHistory()
	{
		HistoryValid = false;
		if ( _history.Framebuffer == 0 ) return;
		_gl.BindFramebuffer( FramebufferTarget.Framebuffer, _history.Framebuffer );
		_gl.ClearColor( 0, 0, 0, 1 );
		_gl.Clear( ClearBufferMask.ColorBufferBit );
	}

	/// <summary>Keeps this frame's linear HDR colour as next frame's SSR source.</summary>
	public void StoreHistory()
	{
		_gl.BindFramebuffer( FramebufferTarget.ReadFramebuffer, _scene.Framebuffer );
		_gl.BindFramebuffer( FramebufferTarget.DrawFramebuffer, _history.Framebuffer );
		_gl.BlitFramebuffer( 0, 0, _w, _h, 0, 0, _w, _h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest );
		HistoryValid = true;
	}

	public void Dispose()
	{
		if ( _vao != 0 ) _gl.DeleteVertexArray( _vao );
		_scene.Dispose(); _history.Dispose();
		_dofPrep.Dispose(); _dofFar.Dispose(); _dofNear.Dispose();
		if ( _dofPrepProg != 0 ) _gl.DeleteProgram( _dofPrepProg );
		if ( _dofGatherProg != 0 ) _gl.DeleteProgram( _dofGatherProg );
		foreach ( var mip in _mips ) mip.Dispose();
		_mips.Clear();
		if ( _downProg != 0 ) _gl.DeleteProgram( _downProg );
		if ( _upProg != 0 ) _gl.DeleteProgram( _upProg );
		if ( _compositeProg != 0 ) _gl.DeleteProgram( _compositeProg );
	}
}
