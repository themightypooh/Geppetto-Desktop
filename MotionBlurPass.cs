using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>Per-pixel motion blur (McGuire et al. 2012 reconstruction filter) on the linear HDR
/// scene, before bloom and depth of field.
///
/// Velocity: a shared geometry pass writes pixel displacement, previous depth and validity into an RGBA16f
/// target, from this frame's and last frame's unjittered view-projection and both frames' bone
/// palettes (current and previous palettes in one SSBO, so two 220-bone palettes do not blow the
/// vertex uniform limit). This captures camera, rigid and skinned deformation motion.
/// The cheap honest version: the previous frame is whatever was drawn last, not a sub-frame
/// shutter interval; TAA consumes the same field before blur. Morph-target motion is not tracked (morphs are applied to the VBO in
/// place, so they contribute no velocity); the model matrix is identity in this renderer, so
/// there is no per-object previous transform to keep.
///
/// Temporal stability: <see cref="Track"/> rejects history (previous = current, zero velocity)
/// on the first frame, a resize, a mesh/player change (source reload), an explicit
/// <see cref="Reset"/>, or a detected camera cut, and reports it so other history users reset.</summary>
public sealed class MotionBlurPass : IDisposable
{
	const int Bones = MarionettePlayer.MaxGpuBones;
	const int TileSize = 16;
	const uint SsboBinding = 7;
	readonly GL _gl;
	readonly RenderTarget _velocity, _tiles, _output;
	readonly GlShader _velocityShader, _tileShader, _blurShader;
	readonly uint _vao, _ssbo;
	readonly int _uViewProj, _uPrevViewProj, _uSkinned, _uScreen, _uLivePrev, _uRasterViewProj, _uHistoryValid;
	readonly UniformCache _uniforms; // R10: no per-frame string marshalling
	Matrix4x4[] _prevBones = new Matrix4x4[Bones], _curBones = new Matrix4x4[Bones];
	readonly Matrix4x4[] _upload = new Matrix4x4[Bones * 2];
	Matrix4x4 _prevViewProj, _curViewProj;
	Vector3 _lastEye, _lastForward;
	object _lastMesh, _lastPlayer;
	int _lastW, _lastH, _boneCount;
	bool _valid;
	bool _velocityReady;

	/// <summary>Cut threshold: an eye jump larger than this fraction of the orbit distance.</summary>
	public float CutDistanceFraction { get; set; } = 0.35f;
	/// <summary>Cut threshold: a view rotation larger than this many degrees in one frame.</summary>
	public float CutAngleDegrees { get; set; } = 30f;
	/// <summary>Largest blur half-length in pixels.</summary>
	public float MaxBlurPixels { get; set; } = TileSize * 2;
	public uint VelocityTexture => _velocity.ColorTexture;
	public uint VelocityDepthTexture => _velocity.DepthTexture;
	/// <summary>True when the last <see cref="Track"/> rejected history.</summary>
	public bool HistoryRejected { get; private set; }

	public MotionBlurPass( GL gl )
	{
		_gl = gl;
		_velocity = new RenderTarget( gl, InternalFormat.Rgba16f, depth: true, TextureMinFilter.Nearest );
		_tiles = new RenderTarget( gl, InternalFormat.RG16f, depth: false, TextureMinFilter.Nearest );
		_output = new RenderTarget( gl, InternalFormat.Rgba16f, depth: false );
		_velocityShader = new GlShader( gl, VelocityVs, VelocityFs );
		_tileShader = new GlShader( gl, PostChainShaders.FullscreenVs, TileMaxFs );
		_blurShader = new GlShader( gl, PostChainShaders.FullscreenVs, ReconstructFs );
		_uViewProj = _velocityShader.Uniform( "uViewProj" ); _uPrevViewProj = _velocityShader.Uniform( "uPrevViewProj" );
		_uSkinned = _velocityShader.Uniform( "uSkinned" ); _uScreen = _velocityShader.Uniform( "uScreen" );
		_uLivePrev = _velocityShader.Uniform( "uLivePrev" );
		_uRasterViewProj = _velocityShader.Uniform("uRasterViewProj");
		_uHistoryValid = _velocityShader.Uniform("uHistoryValid");
		_uniforms = new UniformCache( gl );
		_vao = gl.GenVertexArray();
		_ssbo = gl.GenBuffer();
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, _ssbo );
		unsafe { gl.BufferData( BufferTargetARB.ShaderStorageBuffer, (nuint)( Bones * 2 * sizeof( Matrix4x4 ) ), null, BufferUsageARB.DynamicDraw ); }
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, 0 );
	}

	public void Reset() { _valid = false; _velocityReady = false; }

	/// <summary>Called once per frame before any pass: records this frame's camera and palette and
	/// shifts last frame's into "previous". Allocation-free. Returns true when history was rejected.</summary>
	public bool Track( OrbitCamera camera, ViewportMesh mesh, MarionettePlayer player, int width, int height )
	{
		_velocityReady = false;
		var viewProj = camera.ViewMatrix() * camera.ProjectionMatrix( width / (float)Math.Max( 1, height ) );
		var eye = camera.Eye;
		var forward = Vector3.Normalize( camera.Target - eye );
		if ( camera.Flying ) forward = camera.Forward;
		bool skinned = mesh.HasSkin && player is not null;
		int count = skinned ? player.GpuBones.Length : 0;
		bool reject = !_valid || width != _lastW || height != _lastH
			|| !ReferenceEquals( mesh, _lastMesh ) || !ReferenceEquals( player, _lastPlayer ) || count != _boneCount
			|| Vector3.Distance( eye, _lastEye ) > CutDistanceFraction * MathF.Max( 1e-3f, camera.Distance )
			|| Vector3.Dot( forward, _lastForward ) < MathF.Cos( CutAngleDegrees * MathF.PI / 180f );
		if ( !reject ) { _prevViewProj = _curViewProj; ( _prevBones, _curBones ) = ( _curBones, _prevBones ); }
		_curViewProj = viewProj;
		if ( skinned ) player.GpuBones.CopyTo( _curBones );
		if ( reject )
		{
			_prevViewProj = viewProj;
			if ( skinned ) player.GpuBones.CopyTo( _prevBones );
		}
		_lastEye = eye; _lastForward = forward; _lastMesh = mesh; _lastPlayer = player;
		_lastW = width; _lastH = height; _boneCount = count; _valid = true;
		HistoryRejected = reject;
		return reject;
	}

	/// <summary>Writes velocity, then blurs <paramref name="colorTex"/> and copies the result back
	/// into <paramref name="colorFbo"/>. <paramref name="shutter"/> is the fraction of the frame
	/// interval the shutter is open (Blender's Motion Blur "Shutter", default 0.5).</summary>
	public unsafe void Apply( ViewportMesh mesh, float shutter, uint colorFbo, uint colorTex, int width, int height, float near, float far, LiveGeometry live = null )
	{
		if (!_velocityReady) RenderVelocity(mesh, width, height, live);
		var previousUnit = TextureUnit.Texture0;
		_gl.ActiveTexture( TextureUnit.Texture15 );
		_velocity.Ensure( width, height );
		_tiles.Ensure( ( width + TileSize - 1 ) / TileSize, ( height + TileSize - 1 ) / TileSize );
		_output.Ensure( width, height );
		_gl.ActiveTexture( previousUnit );

		// 1. Velocity was generated above or shared with TAA earlier in this frame.
		// 2. Tile max velocity.
		_gl.Disable(EnableCap.DepthTest);
		_gl.Disable(EnableCap.Blend);
		_gl.BindVertexArray( _vao );
		_tileShader.Use();
		_tileShader.SetInt( _uniforms[_tileShader.Handle, "uVelocity"], 0 );
		_tileShader.SetInt( _uniforms[_tileShader.Handle, "uTile"], TileSize );
		_tileShader.SetFloat( _uniforms[_tileShader.Handle, "uScale"], shutter * 0.5f );
		_tileShader.SetFloat( _uniforms[_tileShader.Handle, "uMaxBlur"], MaxBlurPixels );
		_gl.BindFramebuffer( FramebufferTarget.Framebuffer, _tiles.Framebuffer );
		_gl.Viewport( 0, 0, (uint)_tiles.Width, (uint)_tiles.Height );
		_gl.ActiveTexture( TextureUnit.Texture0 ); _gl.BindTexture( TextureTarget.Texture2D, _velocity.ColorTexture );
		_gl.DrawArrays( PrimitiveType.Triangles, 0, 3 );

		// 3. Reconstruction.
		_blurShader.Use();
		_blurShader.SetInt( _uniforms[_blurShader.Handle, "uColor"], 0 );
		_blurShader.SetInt( _uniforms[_blurShader.Handle, "uVelocity"], 1 );
		_blurShader.SetInt( _uniforms[_blurShader.Handle, "uTiles"], 2 );
		_blurShader.SetInt( _uniforms[_blurShader.Handle, "uDepth"], 3 );
		_blurShader.SetFloat( _uniforms[_blurShader.Handle, "uScale"], shutter * 0.5f );
		_blurShader.SetFloat( _uniforms[_blurShader.Handle, "uMaxBlur"], MaxBlurPixels );
		_blurShader.SetFloat( _uniforms[_blurShader.Handle, "uNear"], near );
		_blurShader.SetFloat( _uniforms[_blurShader.Handle, "uFar"], far );
		_gl.ActiveTexture( TextureUnit.Texture0 ); _gl.BindTexture( TextureTarget.Texture2D, colorTex );
		_gl.ActiveTexture( TextureUnit.Texture1 ); _gl.BindTexture( TextureTarget.Texture2D, _velocity.ColorTexture );
		_gl.ActiveTexture( TextureUnit.Texture2 ); _gl.BindTexture( TextureTarget.Texture2D, _tiles.ColorTexture );
		_gl.ActiveTexture( TextureUnit.Texture3 ); _gl.BindTexture( TextureTarget.Texture2D, _velocity.DepthTexture );
		_gl.BindFramebuffer( FramebufferTarget.Framebuffer, _output.Framebuffer );
		_gl.Viewport( 0, 0, (uint)width, (uint)height );
		_gl.DrawArrays( PrimitiveType.Triangles, 0, 3 );
		_gl.BindFramebuffer( FramebufferTarget.ReadFramebuffer, _output.Framebuffer );
		_gl.BindFramebuffer( FramebufferTarget.DrawFramebuffer, colorFbo );
		_gl.BlitFramebuffer( 0, 0, width, height, 0, 0, width, height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest );
		_gl.ActiveTexture( TextureUnit.Texture0 );
		_gl.Enable( EnableCap.DepthTest );
	}

	/// <summary>Shared full-frame displacement (pixels), previous depth, and validity.
	/// Optional raster projection aligns the field with TAA jitter, while the stored
	/// displacement remains unjittered for motion blur. No second palette upload per frame.</summary>
	public unsafe void RenderVelocity(ViewportMesh mesh, int width, int height, LiveGeometry live = null,
		Matrix4x4? rasterViewProjection = null)
	{
		_gl.ActiveTexture(TextureUnit.Texture15);
		_velocity.Ensure(width, height);
		for ( int i = 0; i < _boneCount; i++ ) { _upload[i] = _curBones[i]; _upload[Bones + i] = _prevBones[i]; }
		_gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, _ssbo );
		fixed ( Matrix4x4* p = _upload )
			_gl.BufferSubData( BufferTargetARB.ShaderStorageBuffer, 0, (nuint)( _upload.Length * sizeof( Matrix4x4 ) ), p );
		_gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, SsboBinding, _ssbo );
		bool cull = _gl.IsEnabled( EnableCap.CullFace );
		_gl.Disable( EnableCap.CullFace ); _gl.Disable( EnableCap.Blend );
		_gl.Enable( EnableCap.DepthTest ); _gl.DepthMask( true );
		_gl.BindFramebuffer( FramebufferTarget.Framebuffer, _velocity.Framebuffer );
		_gl.Viewport( 0, 0, (uint)width, (uint)height );
		_gl.ClearColor( 0, 0, 0, 0 );
		_gl.Clear( ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit );
		_velocityShader.Use();
		_velocityShader.SetMatrix( _uViewProj, _curViewProj ); _velocityShader.SetMatrix( _uPrevViewProj, _prevViewProj );
		_velocityShader.SetMatrix(_uRasterViewProj, rasterViewProjection ?? _curViewProj);
		_velocityShader.SetInt(_uHistoryValid, HistoryRejected ? 0 : 1);
		_velocityShader.SetInt( _uSkinned, _boneCount > 0 ? 1 : 0 );
		_velocityShader.SetVec2( _uScreen, width, height );
		_velocityShader.SetInt( _uLivePrev, 0 );
		mesh.Draw( _gl );
		if ( live is { HasCasters: true } ) // R9: opaque live layers with their own previous positions
		{
			_velocityShader.SetInt( _uSkinned, 0 ); _velocityShader.SetInt( _uLivePrev, 1 );
			live.DrawDepth( _gl );
			_velocityShader.SetInt( _uLivePrev, 0 );
		}
		if ( cull ) _gl.Enable( EnableCap.CullFace );
		_gl.Disable( EnableCap.DepthTest );
		_velocityReady = true;
	}

	public void Dispose()
	{
		_velocity.Dispose(); _tiles.Dispose(); _output.Dispose();
		_velocityShader.Dispose(); _tileShader.Dispose(); _blurShader.Dispose();
		_gl.DeleteVertexArray( _vao ); _gl.DeleteBuffer( _ssbo );
	}

	const string VelocityVs = @"#version 460 core
layout(location = 0) in vec3 aPos;
layout(location = 2) in vec4 aBoneIndices;
layout(location = 3) in vec4 aBoneWeights;
layout(location = 6) in vec3 aPrevPos; // R9 live geometry only (uLivePrev)
uniform int uLivePrev;
layout(std430, binding = 7) readonly buffer Palettes { mat4 uPalette[]; }; // [0,220) current, [220,440) previous
uniform mat4 uViewProj;
uniform mat4 uPrevViewProj;
uniform mat4 uRasterViewProj;
uniform int uSkinned;
out vec4 vCur;
out vec4 vPrev;
vec3 skin(int base) {
	ivec4 b = clamp(ivec4(aBoneIndices + 0.5), 0, 219) + base;
	mat4 m = uPalette[b.x] * aBoneWeights.x + uPalette[b.y] * aBoneWeights.y
	       + uPalette[b.z] * aBoneWeights.z + uPalette[b.w] * aBoneWeights.w;
	return (m * vec4(aPos, 1.0)).xyz;
}
void main() {
	vec3 cur = uSkinned != 0 ? skin(0) : aPos;
	vec3 prev = uSkinned != 0 ? skin(220) : uLivePrev != 0 ? aPrevPos : aPos;
	vCur = uViewProj * vec4(cur, 1.0);
	vPrev = uPrevViewProj * vec4(prev, 1.0);
	gl_Position = uRasterViewProj * vec4(cur,1.0);
}
";

	/// <summary>Velocity in pixels over one frame interval (current minus previous).</summary>
	const string VelocityFs = @"#version 460 core
in vec4 vCur;
in vec4 vPrev;
uniform vec2 uScreen;
uniform int uHistoryValid;
out vec4 Velocity;
void main() {
	vec2 a = vCur.xy / vCur.w, b = vPrev.xy / max(vPrev.w, 1e-5);
	Velocity = vec4((a - b) * 0.5 * uScreen, vPrev.z / max(vPrev.w,1e-5)*0.5+0.5,
		uHistoryValid != 0 && vPrev.w > 1e-5 ? 1.0 : 0.0);
}
";

	/// <summary>Per tile, the longest shutter-scaled half-velocity.</summary>
	const string TileMaxFs = @"#version 460 core
out vec2 FragColor;
uniform sampler2D uVelocity;
uniform int uTile;
uniform float uScale;
uniform float uMaxBlur;
void main() {
	ivec2 size = textureSize(uVelocity, 0);
	ivec2 base = ivec2(gl_FragCoord.xy) * uTile;
	vec2 best = vec2(0.0); float len = 0.0;
	for (int y = 0; y < uTile; y++)
		for (int x = 0; x < uTile; x++) {
			ivec2 p = base + ivec2(x, y);
			if (p.x >= size.x || p.y >= size.y) continue;
			vec2 v = texelFetch(uVelocity, p, 0).xy * uScale;
			float l = dot(v, v);
			if (l > len) { len = l; best = v; }
		}
	float l = length(best);
	FragColor = l > uMaxBlur ? best * (uMaxBlur / l) : best;
}
";

	/// <summary>McGuire 2012: gather along the 3x3 neighbourhood's dominant velocity, weighting
	/// each sample by soft depth ordering and whether its own (or the centre's) blur reaches it.</summary>
	const string ReconstructFs = @"#version 460 core
in vec2 vUv;
out vec4 FragColor;
uniform sampler2D uColor;
uniform sampler2D uVelocity;
uniform sampler2D uTiles;
uniform sampler2D uDepth;
uniform float uScale;
uniform float uMaxBlur;
uniform float uNear;
uniform float uFar;
const int SAMPLES = 20;
float linearDepth(float d) {
	float z = d * 2.0 - 1.0;
	return 2.0 * uNear * uFar / (uFar + uNear - z * (uFar - uNear));
}
vec2 vel(vec2 uv) {
	vec2 v = texture(uVelocity, uv).xy * uScale;
	float l = length(v);
	return l > uMaxBlur ? v * (uMaxBlur / l) : v;
}
float cone(float dist, float v) { return clamp(1.0 - dist / max(v, 1e-4), 0.0, 1.0); }
float cylinder(float dist, float v) { return 1.0 - smoothstep(0.95 * v, 1.05 * v + 1e-4, dist); }
float softDepth(float a, float b, float extent) { return clamp(1.0 - (a - b) / extent, 0.0, 1.0); }
void main() {
	vec2 size = vec2(textureSize(uColor, 0));
	ivec2 tile = ivec2(gl_FragCoord.xy) / 16;
	ivec2 tiles = textureSize(uTiles, 0);
	vec2 vmax = vec2(0.0); float lmax = 0.0;
	for (int y = -1; y <= 1; y++)
		for (int x = -1; x <= 1; x++) {
			vec2 v = texelFetch(uTiles, clamp(tile + ivec2(x, y), ivec2(0), tiles - 1), 0).xy;
			float l = dot(v, v);
			if (l > lmax) { lmax = l; vmax = v; }
		}
	vec3 centre = texture(uColor, vUv).rgb;
	if (sqrt(lmax) < 0.5) { FragColor = vec4(centre, 1.0); return; }
	vec2 vc = vel(vUv);
	float lc = max(length(vc), 0.5);
	float zc = linearDepth(texture(uDepth, vUv).r);
	float extent = max(zc * 0.01, 1e-3);
	float jitter = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715)))) - 0.5;
	float total = 1.0 / lc;
	vec3 sum = centre * total;
	for (int i = 0; i < SAMPLES; i++) {
		float t = mix(-1.0, 1.0, (float(i) + jitter + 1.0) / float(SAMPLES + 1));
		vec2 offset = vmax * t;
		vec2 uv = vUv + offset / size;
		if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) continue;
		float dist = length(offset);
		float zy = linearDepth(texture(uDepth, uv).r);
		vec2 vy = vel(uv);
		float ly = max(length(vy), 0.5);
		// A foreground moving sample may cover the background centre. Test
		// sample depth against centre depth in that order; the reversed test
		// suppresses its velocity support and prevents silhouettes from smearing.
		float f = softDepth(zy, zc, extent); // sample in front of centre
		float b = softDepth(zc, zy, extent); // sample behind centre
		float w = f * cone(dist, ly) + b * cone(dist, lc) + cylinder(dist, ly) * cylinder(dist, lc) * 2.0;
		sum += texture(uColor, uv).rgb * w; total += w;
	}
	FragColor = vec4(sum / total, 1.0);
}
";
}
