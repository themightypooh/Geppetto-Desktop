using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>The same mesh/lighting pipeline for the interactive app and windowless EGL renders.</summary>
public sealed class SceneRenderer : IDisposable
{
	readonly GL _gl;
	readonly GlShader _shader;
	readonly KeyShadowMap _shadow;
	readonly int _mvp, _model, _eye, _key, _skinned, _bones, _exposure, _lightViewProjection, _shadowTexture;
	readonly int _albedo, _roughness, _metallic, _emission;
	readonly int _albedoMap;
	readonly int _normalMap, _roughnessMap, _metallicMap, _emissionMap, _occlusionMap, _normalY;

	public SceneRenderer( GL gl )
	{
		_gl = gl;
		_shadow = new KeyShadowMap( gl );
		_shader = new GlShader( gl, Program.VertexSrc, Program.FragmentSrc );
		_mvp = _shader.Uniform( "uMvp" ); _model = _shader.Uniform( "uModel" );
		_eye = _shader.Uniform( "uEye" ); _key = _shader.Uniform( "uKeyDirection" );
		_lightViewProjection = _shader.Uniform( "uLightViewProjection" ); _shadowTexture = _shader.Uniform( "uShadowMap" );
		_skinned = _shader.Uniform( "uSkinned" ); _bones = _shader.Uniform( "uBones[0]" );
		_albedo = _shader.Uniform( "uAlbedo" ); _roughness = _shader.Uniform( "uRoughness" );
		_albedoMap = _shader.Uniform( "uAlbedoMap" );
		_normalMap = _shader.Uniform( "uNormalMap" ); _roughnessMap = _shader.Uniform( "uRoughnessMap" );
		_metallicMap = _shader.Uniform( "uMetallicMap" ); _emissionMap = _shader.Uniform( "uEmissionMap" );
		_normalY = _shader.Uniform( "uNormalY" );
		_occlusionMap = _shader.Uniform( "uOcclusionMap" );
		_metallic = _shader.Uniform( "uMetallic" ); _emission = _shader.Uniform( "uEmission" );
		_exposure = _shader.Uniform( "uExposure" );
	}

	public void Draw( ViewportMesh mesh, MarionettePlayer player, OrbitCamera camera, int width, int height, float exposure, uint target = 0 )
	{
		_shadow.Draw( mesh, player );
		_gl.BindFramebuffer( FramebufferTarget.Framebuffer, target );
		_gl.Viewport( 0, 0, (uint)width, (uint)height );
		_gl.Enable( EnableCap.DepthTest );
		_gl.ClearColor( 0.09f, 0.09f, 0.11f, 1 );
		_gl.Clear( ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit );
		var viewProj = camera.ViewMatrix() * camera.ProjectionMatrix( width / (float)Math.Max( 1, height ) );
		var eye = camera.Eye;
		var key = KeyShadowMap.Direction;
		_shader.Use();
		_shader.SetMatrix( _mvp, viewProj ); _shader.SetMatrix( _model, Matrix4x4.Identity );
		_shader.SetVec3( _eye, eye.X, eye.Y, eye.Z );
		_shader.SetVec3( _key, key.X, key.Y, key.Z );
		_shader.SetMatrix( _lightViewProjection, _shadow.ViewProjection );
		_gl.ActiveTexture( TextureUnit.Texture0 ); _gl.BindTexture( TextureTarget.Texture2D, _shadow.Texture );
		_shader.SetInt( _shadowTexture, 0 );
		_shader.SetInt( _albedoMap, 1 );
		_shader.SetInt( _normalMap, 2 ); _shader.SetInt( _roughnessMap, 3 );
		_shader.SetInt( _metallicMap, 4 ); _shader.SetInt( _emissionMap, 5 );
		_shader.SetInt( _occlusionMap, 6 );
		_shader.SetFloat( _exposure, exposure );
		bool skinned = mesh.HasSkin && player is not null;
		_shader.SetInt( _skinned, skinned ? 1 : 0 );
		if ( skinned ) _shader.SetMatrixArray( _bones, player.GpuBones );
		mesh.DrawBatched( _gl, _shader, _albedo, _roughness, _metallic, _emission, _normalY );
	}

	public void Dispose() { _shadow.Dispose(); _shader.Dispose(); }
}
