using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>Shadow catcher for the viewport floor (the grid's z = 0 plane). The floor is not a
/// visible surface: it writes pre-pass depth so SSAO produces contact shadows under the model,
/// then multiplies the lit HDR target by key-light visibility and that AO. Unshadowed, unoccluded
/// floor multiplies by exactly 1, so the background is unchanged away from the model.</summary>
public sealed class GroundShadowPass : IDisposable
{
	const string VertexSrc = @"#version 460 core
layout(location = 0) in vec2 aPos;
uniform mat4 uViewProj;
uniform vec3 uCenter;
uniform float uExtent;
out vec3 vWorld;
void main() {
	vWorld = vec3(uCenter.xy + aPos * uExtent, uCenter.z);
	gl_Position = uViewProj * vec4(vWorld, 1.0);
}
";

	const string FragmentSrc = @"#version 460 core
in vec3 vWorld;
out vec4 FragColor;
uniform mat4 uLightViewProjection;
uniform sampler2D uShadowMap;
uniform sampler2D uAoMap;
uniform vec2 uScreenSize;
uniform float uShadowRadius;
uniform float uDepthRange;
uniform float uWorldTexel;
uniform float uShadowStrength;
uniform float uAoStrength;
void main() {
	vec4 clip = uLightViewProjection * vec4(vWorld, 1.0);
	vec3 p = clip.xyz / clip.w * 0.5 + 0.5;
	float visible = 1.0;
	if (all(greaterThanEqual(p.xy, vec2(0.0))) && all(lessThanEqual(p.xy, vec2(1.0)))) {
		// The map is fitted to the model, so the floor usually lies beyond its far plane:
		// any stored depth (< 1) at this texel is then an occluder between floor and light.
		float z = min(p.z, 1.0) - 1e-4;
		vec2 texel = 1.0 / vec2(textureSize(uShadowMap, 0));
		float r = max(uShadowRadius, 1.5);
		// Blocker search, then a penumbra that widens with floor-to-occluder distance
		// (contact stays crisp, an arm's shadow softens). Depth is linear: orthographic light.
		float blocker = 0.0, blockers = 0.0;
		for (int y = -2; y <= 2; y++)
			for (int x = -2; x <= 2; x++) {
				float d = texture(uShadowMap, p.xy + vec2(x, y) * texel * r * 2.0).r;
				if (d < 1.0 && d < z) { blocker += d; blockers += 1.0; }
			}
		if (blockers > 0.0) {
			float distance = (p.z - blocker / blockers) * 0.5 * uDepthRange;
			float spread = r * 0.5 + distance * 0.025 / max(uWorldTexel, 1e-6);
			spread = min(spread, 24.0);
			float lit = 0.0;
			for (int y = -3; y <= 3; y++)
				for (int x = -3; x <= 3; x++) {
					float d = texture(uShadowMap, p.xy + vec2(x, y) * texel * spread / 3.0).r;
					lit += (d >= 1.0 || z <= d) ? 1.0 : 0.0;
				}
			visible = lit / 49.0;
		}
	}
	float ao = texture(uAoMap, gl_FragCoord.xy / uScreenSize).r;
	float k = mix(1.0, visible, uShadowStrength) * mix(1.0, ao, uAoStrength);
	FragColor = vec4(k, k, k, 1.0);
}
";

	readonly GL _gl;
	readonly GlShader _shader;
	readonly uint _vao, _vbo;
	readonly int _viewProj, _center, _extent, _lightViewProjection, _shadowMap, _aoMap, _screenSize, _shadowRadius, _shadowStrength, _aoStrength, _depthRange, _worldTexel;

	/// <summary>Floor height in world units (the grid plane).</summary>
	public float Height { get; set; }
	/// <summary>How far key-light shadow darkens the floor, 0..1.</summary>
	public float ShadowStrength { get; set; } = 0.6f;
	/// <summary>How far SSAO contact occlusion darkens the floor, 0..1.</summary>
	public float AoStrength { get; set; } = 0.85f;

	public unsafe GroundShadowPass( GL gl )
	{
		_gl = gl;
		_shader = new GlShader( gl, VertexSrc, FragmentSrc );
		_viewProj = _shader.Uniform( "uViewProj" ); _center = _shader.Uniform( "uCenter" ); _extent = _shader.Uniform( "uExtent" );
		_lightViewProjection = _shader.Uniform( "uLightViewProjection" ); _shadowMap = _shader.Uniform( "uShadowMap" ); _aoMap = _shader.Uniform( "uAoMap" );
		_screenSize = _shader.Uniform( "uScreenSize" ); _shadowRadius = _shader.Uniform( "uShadowRadius" );
		_shadowStrength = _shader.Uniform( "uShadowStrength" ); _aoStrength = _shader.Uniform( "uAoStrength" );
		_depthRange = _shader.Uniform( "uDepthRange" ); _worldTexel = _shader.Uniform( "uWorldTexel" );
		float[] quad = { -1, -1, 1, -1, -1, 1, 1, 1 };
		_vao = gl.GenVertexArray(); _vbo = gl.GenBuffer();
		gl.BindVertexArray( _vao );
		gl.BindBuffer( BufferTargetARB.ArrayBuffer, _vbo );
		fixed ( float* data = quad ) gl.BufferData( BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof( float )), data, BufferUsageARB.StaticDraw );
		gl.EnableVertexAttribArray( 0 );
		gl.VertexAttribPointer( 0, 2, VertexAttribPointerType.Float, false, 2 * sizeof( float ), (void*)0 );
		gl.BindVertexArray( 0 );
	}

	void Quad( Matrix4x4 viewProj, OrbitCamera camera )
	{
		_shader.Use();
		_shader.SetMatrix( _viewProj, viewProj );
		// Centred under the camera target and reaching the far plane, so its edge is never seen.
		_shader.SetVec3( _center, camera.Target.X, camera.Target.Y, Height );
		_shader.SetFloat( _extent, camera.Far );
		_gl.BindVertexArray( _vao );
		_gl.DrawArrays( PrimitiveType.TriangleStrip, 0, 4 );
		_gl.BindVertexArray( 0 );
	}

	/// <summary>Pre-pass: floor depth only, pushed behind coplanar model geometry.</summary>
	public void DrawDepth( Matrix4x4 viewProj, OrbitCamera camera )
	{
		_gl.Enable( EnableCap.PolygonOffsetFill ); _gl.PolygonOffset( 1f, 4f );
		Quad( viewProj, camera );
		_gl.Disable( EnableCap.PolygonOffsetFill );
	}

	/// <summary>Lit pass: multiply the HDR target by floor visibility. Expects the key shadow map
	/// on <paramref name="shadowUnit"/> and the AO texture (or white) on <paramref name="aoUnit"/>.</summary>
	public void Shade( Matrix4x4 viewProj, OrbitCamera camera, Matrix4x4 lightViewProjection, float depthRange, float worldTexel, float shadowRadius, int shadowUnit, int aoUnit, int width, int height )
	{
		bool blend = _gl.IsEnabled( EnableCap.Blend );
		_gl.GetBoolean( GetPName.DepthWritemask, out bool depthWrite );
		_shader.Use();
		_shader.SetMatrix( _lightViewProjection, lightViewProjection );
		_shader.SetInt( _shadowMap, shadowUnit ); _shader.SetInt( _aoMap, aoUnit );
		_shader.SetVec2( _screenSize, width, height );
		_shader.SetFloat( _shadowRadius, shadowRadius );
		_shader.SetFloat( _depthRange, depthRange ); _shader.SetFloat( _worldTexel, worldTexel );
		_shader.SetFloat( _shadowStrength, Math.Clamp( ShadowStrength, 0f, 1f ) );
		_shader.SetFloat( _aoStrength, Math.Clamp( AoStrength, 0f, 1f ) );
		_gl.Enable( EnableCap.Blend ); _gl.BlendFunc( BlendingFactor.DstColor, BlendingFactor.Zero );
		_gl.DepthMask( false ); _gl.DepthFunc( DepthFunction.Lequal );
		_gl.Enable( EnableCap.PolygonOffsetFill ); _gl.PolygonOffset( 1f, 4f );
		Quad( viewProj, camera );
		_gl.Disable( EnableCap.PolygonOffsetFill );
		_gl.DepthFunc( DepthFunction.Less ); _gl.DepthMask( depthWrite );
		if ( !blend ) _gl.Disable( EnableCap.Blend );
		_gl.BlendFunc( BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha );
	}

	public void Dispose()
	{
		_gl.DeleteBuffer( _vbo ); _gl.DeleteVertexArray( _vao );
		_shader.Dispose();
	}
}
