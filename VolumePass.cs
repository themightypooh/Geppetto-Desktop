using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>R8: participating media composited into the linear HDR scene before the post chain.
/// Full-resolution ray march per pixel (front-to-back, energy-conserving integration) with the
/// key light's shadow map for volumetric occlusion (light shafts). Output rgb = in-scatter,
/// a = transmittance, blended as dst = src.rgb + dst * src.a. Hosts that cannot create the
/// march shader or an R16F 3D texture fall back to analytic height fog (no shadows).
/// An empty or disabled grid issues no GL calls at all.</summary>
public sealed class VolumePass : IDisposable
{
	/// <summary>Texture unit of the optional 3D density field (unit 13 is R8's).</summary>
	public const int DensityUnit = 13;
	public int Steps { get; set; } = 72;

	readonly GL _gl;
	readonly uint _vao;
	GlShader _march, _analytic;
	uint _densityTex;
	DensityTexture _uploaded;
	bool _textureFailed;
	public bool UsingFallback { get; private set; }
	/// <summary>True when the last <see cref="Apply"/> drew anything.</summary>
	public bool LastApplied { get; private set; }

	public VolumePass( GL gl )
	{
		_gl = gl;
		_vao = gl.GenVertexArray();
		try { _march = new GlShader( gl, FullscreenVs, Header + MarchFs ); }
		catch ( Exception e ) { Console.Error.WriteLine( "VolumePass: ray-march shader unavailable, using height fog: " + e.Message ); }
		_analytic = new GlShader( gl, FullscreenVs, Header + AnalyticFs );
	}

	/// <summary>Composite <paramref name="grid"/> into the bound HDR framebuffer
	/// <paramref name="hdrFbo"/>. <paramref name="viewProjection"/> must match the depth texture.</summary>
	public void Apply( VolumeGrid grid, uint hdrFbo, uint depthTexture, uint shadowTexture, Matrix4x4 lightViewProjection,
		Matrix4x4 viewProjection, Vector3 eye, Vector3 keyDirection, Vector3 keyRadiance, float exposure, int width, int height )
	{
		LastApplied = false;
		if ( grid is null || grid.IsEmpty ) return;
		Matrix4x4.Invert( viewProjection, out var invViewProjection );
		var key = keyDirection.LengthSquared() > 1e-6f ? Vector3.Normalize( keyDirection ) : KeyShadowMap.Direction;
		float maxDistance = grid.MaxDistance > 0 ? grid.MaxDistance : grid.ReferenceRadius * 6f;

		bool fallback = grid.ForceFallback || _march is null;
		FogVolume textured = null;
		if ( !fallback )
			foreach ( var v in grid.Volumes ) if ( v.Texture is not null ) { textured = v; break; }
		if ( textured is not null && !EnsureDensityTexture( textured.Texture ) ) fallback = true;
		UsingFallback = fallback;
		var shader = fallback ? _analytic : _march;

		_gl.BindFramebuffer( FramebufferTarget.Framebuffer, hdrFbo );
		_gl.Viewport( 0, 0, (uint)width, (uint)height );
		_gl.Disable( EnableCap.DepthTest );
		_gl.DepthMask( false );
		_gl.Enable( EnableCap.Blend );
		_gl.BlendFunc( BlendingFactor.One, BlendingFactor.SrcAlpha );
		shader.Use();
		_gl.ActiveTexture( TextureUnit.Texture0 ); _gl.BindTexture( TextureTarget.Texture2D, shadowTexture );
		_gl.ActiveTexture( TextureUnit.Texture7 ); _gl.BindTexture( TextureTarget.Texture2D, depthTexture );
		shader.SetInt( shader.Uniform( "uShadowMap" ), 0 );
		shader.SetInt( shader.Uniform( "uDepth" ), 7 );
		if ( !fallback )
		{
			_gl.ActiveTexture( TextureUnit.Texture0 + DensityUnit );
			_gl.BindTexture( TextureTarget.Texture3D, textured is null ? 0 : _densityTex );
			shader.SetInt( shader.Uniform( "uDensityTex" ), DensityUnit );
		}
		shader.SetMatrix( shader.Uniform( "uInvViewProjection" ), invViewProjection );
		shader.SetMatrix( shader.Uniform( "uLightViewProjection" ), lightViewProjection );
		shader.SetVec3( shader.Uniform( "uEye" ), eye.X, eye.Y, eye.Z );
		shader.SetVec3( shader.Uniform( "uKey" ), key.X, key.Y, key.Z );
		var kr = keyRadiance * (grid.KeyIntensity * exposure); var amb = grid.Ambient * exposure;
		shader.SetVec3( shader.Uniform( "uKeyRadiance" ), kr.X, kr.Y, kr.Z );
		shader.SetVec3( shader.Uniform( "uAmbient" ), amb.X, amb.Y, amb.Z );
		shader.SetFloat( shader.Uniform( "uExposure" ), exposure );
		shader.SetFloat( shader.Uniform( "uMaxDistance" ), maxDistance );
		shader.SetInt( shader.Uniform( "uSteps" ), Math.Clamp( Steps, 4, 256 ) );
		int count = 0;
		foreach ( var v in grid.Volumes )
		{
			if ( count == VolumeGrid.MaxVolumes ) break;
			if ( fallback && v.Shape != VolumeShape.World ) continue;
			string i = "[" + count + "]";
			shader.SetInt( shader.Uniform( "uShape" + i ), (int)v.Shape );
			shader.SetInt( shader.Uniform( "uBlend" + i ), (int)v.Blend );
			shader.SetInt( shader.Uniform( "uTextured" + i ), ReferenceEquals( v, textured ) ? 1 : 0 );
			shader.SetVec3( shader.Uniform( "uCentre" + i ), v.Centre.X, v.Centre.Y, v.Centre.Z );
			shader.SetVec3( shader.Uniform( "uSize" + i ), v.Size.X, v.Size.Y, v.Size.Z );
			shader.SetVec3( shader.Uniform( "uScatter" + i ), v.Scattering.X, v.Scattering.Y, v.Scattering.Z );
			shader.SetVec3( shader.Uniform( "uEmission" + i ), v.Emission.X, v.Emission.Y, v.Emission.Z );
			// params: density, absorption, anisotropy g, falloff; height falloff separately.
			shader.SetFloat( shader.Uniform( "uDensity" + i ), v.Density );
			shader.SetFloat( shader.Uniform( "uAbsorb" + i ), v.Absorption );
			shader.SetFloat( shader.Uniform( "uG" + i ), Math.Clamp( v.Anisotropy, -0.95f, 0.95f ) );
			shader.SetFloat( shader.Uniform( "uFalloff" + i ), v.Falloff );
			shader.SetFloat( shader.Uniform( "uHeight" + i ), v.HeightFalloff );
			count++;
		}
		shader.SetInt( shader.Uniform( "uCount" ), count );
		_gl.BindVertexArray( _vao );
		_gl.DrawArrays( PrimitiveType.Triangles, 0, 3 );
		_gl.BindVertexArray( 0 );
		_gl.Disable( EnableCap.Blend );
		_gl.DepthMask( true );
		_gl.Enable( EnableCap.DepthTest );
		_gl.ActiveTexture( TextureUnit.Texture0 );
		LastApplied = true;
	}

	unsafe bool EnsureDensityTexture( DensityTexture texture )
	{
		if ( _textureFailed ) return false;
		if ( ReferenceEquals( texture, _uploaded ) ) return true;
		while ( _gl.GetError() != GLEnum.NoError ) { }
		if ( _densityTex == 0 ) _densityTex = _gl.GenTexture();
		_gl.ActiveTexture( TextureUnit.Texture0 + DensityUnit );
		_gl.BindTexture( TextureTarget.Texture3D, _densityTex );
		fixed ( float* data = texture.Values )
			_gl.TexImage3D( TextureTarget.Texture3D, 0, InternalFormat.R16f, (uint)texture.Width, (uint)texture.Height,
				(uint)texture.Depth, 0, PixelFormat.Red, PixelType.Float, data );
		_gl.TexParameter( TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear );
		_gl.TexParameter( TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear );
		_gl.TexParameter( TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge );
		_gl.TexParameter( TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge );
		_gl.TexParameter( TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)GLEnum.ClampToEdge );
		if ( _gl.GetError() != GLEnum.NoError )
		{
			Console.Error.WriteLine( "VolumePass: R16F 3D textures unavailable, using height fog." );
			_textureFailed = true;
			return false;
		}
		_uploaded = texture;
		return true;
	}

	public void Dispose()
	{
		_march?.Dispose(); _analytic?.Dispose();
		if ( _densityTex != 0 ) _gl.DeleteTexture( _densityTex );
		_gl.DeleteVertexArray( _vao );
	}

	const string FullscreenVs = @"#version 460 core
out vec2 vUv;
void main() {
	vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
	vUv = p;
	gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}";

	const string Header = @"#version 460 core
in vec2 vUv;
out vec4 FragColor;
const int MAXV = 8;
uniform sampler2D uDepth;
uniform sampler2D uShadowMap;
uniform mat4 uInvViewProjection;
uniform mat4 uLightViewProjection;
uniform vec3 uEye, uKey, uKeyRadiance, uAmbient;
uniform float uExposure, uMaxDistance;
uniform int uSteps, uCount;
uniform int uShape[MAXV], uBlend[MAXV], uTextured[MAXV];
uniform vec3 uCentre[MAXV], uSize[MAXV], uScatter[MAXV], uEmission[MAXV];
uniform float uDensity[MAXV], uAbsorb[MAXV], uG[MAXV], uFalloff[MAXV], uHeight[MAXV];
const float PI = 3.14159265;

float hg(float c, float g) {
	float d = 1.0 + g * g - 2.0 * g * c;
	return (1.0 - g * g) / (4.0 * PI * d * sqrt(d));
}

// World-space view ray for this pixel and the distance to the opaque surface (or the cap).
void viewRay(out vec3 dir, out float sceneDist) {
	float z = texture(uDepth, vUv).r;
	vec4 far = uInvViewProjection * vec4(vUv * 2.0 - 1.0, (z >= 1.0 ? 1.0 : z) * 2.0 - 1.0, 1.0);
	vec3 p = far.xyz / far.w;
	dir = normalize(p - uEye);
	sceneDist = z >= 1.0 ? uMaxDistance : min(length(p - uEye), uMaxDistance);
}
";

	const string MarchFs = @"
uniform sampler3D uDensityTex;

float weightOf(int i, vec3 p) {
	if (uShape[i] == 0)
		return uHeight[i] > 0.0 ? exp(-max(0.0, p.z - uCentre[i].z) * uHeight[i]) : 1.0;
	float edge;
	if (uShape[i] == 2) edge = 1.0 - length(p - uCentre[i]) / max(uSize[i].x, 1e-4);
	else {
		vec3 d = abs(p - uCentre[i]) / max(uSize[i], vec3(1e-4));
		edge = 1.0 - max(d.x, max(d.y, d.z));
	}
	if (edge <= 0.0) return 0.0;
	float w = uFalloff[i] <= 1e-4 ? 1.0 : clamp(edge / uFalloff[i], 0.0, 1.0);
	if (uTextured[i] != 0) {
		vec3 ext = uShape[i] == 2 ? vec3(uSize[i].x) : uSize[i];
		w *= texture(uDensityTex, (p - uCentre[i]) / max(ext, vec3(1e-4)) * 0.5 + 0.5).r;
	}
	return w;
}

// Ray interval [t0,t1] covered by volume i.
vec2 rangeOf(int i, vec3 o, vec3 d, float tMax) {
	if (uShape[i] == 0) return vec2(0.0, tMax);
	vec3 ext = uShape[i] == 2 ? vec3(uSize[i].x) : uSize[i];
	vec3 inv = 1.0 / mix(d, vec3(1e-6), lessThan(abs(d), vec3(1e-6)));
	vec3 a = (uCentre[i] - ext - o) * inv, b = (uCentre[i] + ext - o) * inv;
	vec3 lo = min(a, b), hi = max(a, b);
	float t0 = max(max(lo.x, lo.y), max(lo.z, 0.0));
	float t1 = min(min(hi.x, hi.y), min(hi.z, tMax));
	return vec2(t0, t1);
}

float keyShadow(vec3 p) {
	vec4 c = uLightViewProjection * vec4(p, 1.0);
	vec3 s = c.xyz / c.w * 0.5 + 0.5;
	if (s.x < 0.0 || s.x > 1.0 || s.y < 0.0 || s.y > 1.0 || s.z > 1.0) return 1.0;
	return s.z - 0.0015 <= texture(uShadowMap, s.xy).r ? 1.0 : 0.0;
}

void main() {
	vec3 dir; float sceneDist;
	viewRay(dir, sceneDist);
	float t0 = 1e30, t1 = 0.0;
	for (int i = 0; i < uCount; ++i) {
		if (uBlend[i] != 0) continue;
		vec2 r = rangeOf(i, uEye, dir, sceneDist);
		if (r.y > r.x) { t0 = min(t0, r.x); t1 = max(t1, r.y); }
	}
	if (t1 <= t0) { FragColor = vec4(0.0, 0.0, 0.0, 1.0); return; }
	float c = dot(dir, uKey);
	float dt = (t1 - t0) / float(uSteps);
	// Interleaved gradient noise jitter hides banding without temporal accumulation.
	float jitter = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
	vec3 L = vec3(0.0);
	vec3 T = vec3(1.0);
	for (int s = 0; s < uSteps; ++s) {
		vec3 p = uEye + dir * (t0 + (float(s) + jitter) * dt);
		vec3 sigmaS = vec3(0.0); vec3 emit = vec3(0.0);
		float sigmaA = 0.0, phase = 0.0, density = 0.0;
		for (int i = 0; i < uCount; ++i) {
			float w = weightOf(i, p);
			if (uBlend[i] != 0) {
				float m = mix(1.0, uDensity[i], w);
				sigmaS *= m; sigmaA *= m; emit *= m; phase *= m; density *= m;
				continue;
			}
			float d = uDensity[i] * w;
			if (d <= 0.0) continue;
			sigmaS += d * uScatter[i];
			sigmaA += d * uAbsorb[i];
			emit += d * uEmission[i];
			phase += d * hg(c, uG[i]);
			density += d;
		}
		vec3 sigmaT = sigmaS + vec3(sigmaA);
		if (max(sigmaT.r, max(sigmaT.g, sigmaT.b)) <= 1e-9 && emit == vec3(0.0)) continue;
		float ph = density > 0.0 ? phase / density : 0.0;
		vec3 S = sigmaS * (uKeyRadiance * keyShadow(p) * ph + uAmbient) + emit * uExposure;
		vec3 Tr = exp(-sigmaT * dt);
		vec3 safeT = max(sigmaT, vec3(1e-7));
		L += T * (S - S * Tr) / safeT;
		T *= Tr;
	}
	FragColor = vec4(L, dot(T, vec3(1.0 / 3.0)));
}";

	// Fallback: closed-form exponential height fog for World volumes only, no shadows.
	const string AnalyticFs = @"
void main() {
	vec3 dir; float dist;
	viewRay(dir, dist);
	float c = dot(dir, uKey);
	vec3 L = vec3(0.0); float T = 1.0;
	for (int i = 0; i < uCount; ++i) {
		if (uShape[i] != 0 || uBlend[i] != 0) continue;
		float k = uHeight[i];
		float h0 = max(0.0, uEye.z - uCentre[i].z);
		float dz = dir.z * dist;
		// integral of exp(-k * h(t)) along the ray, linearised for tiny k*dz.
		float od = k > 0.0 ? exp(-k * h0) * (abs(k * dz) > 1e-4 ? (1.0 - exp(-k * dz)) / (k * dir.z) : dist) : dist;
		float extinction = uDensity[i] * (dot(uScatter[i], vec3(1.0 / 3.0)) + uAbsorb[i]);
		float t = exp(-max(od, 0.0) * extinction);
		vec3 albedo = uScatter[i] / max(dot(uScatter[i], vec3(1.0 / 3.0)) + uAbsorb[i], 1e-5);
		vec3 S = albedo * (uKeyRadiance * hg(c, uG[i]) + uAmbient) + uEmission[i] * uExposure / max(extinction, 1e-6) * uDensity[i];
		L += T * S * (1.0 - t);
		T *= t;
	}
	FragColor = vec4(L, T);
}";
}
