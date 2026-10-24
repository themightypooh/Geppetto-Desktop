namespace Marionette.Desktop;

public static class RendererShaders
{
	public const string VertexSrc = @"#version 460 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNrm;
layout(location = 2) in vec4 aBoneIndices;
layout(location = 3) in vec4 aBoneWeights;
layout(location = 4) in vec2 aUv;
layout(location = 5) in vec4 aTangent;
layout(location = 7) in vec4 aColor;

uniform mat4 uMvp;
uniform mat4 uModel;
uniform int uSkinned;
uniform mat4 uBones[220];

out vec3 vNrm;
out vec3 vWorld;
out vec2 vUv;
out vec3 vTangent;
out float vHandedness;
out float vMirror;
out vec3 vObject;
out vec3 vColor;

void main() {
	vec3 pos = aPos;
	vec3 nrm = aNrm;
	vec3 tangent = aTangent.xyz;
	mat3 deformation = mat3(1.0);

	if (uSkinned != 0) {
		int b0 = clamp(int(aBoneIndices.x + 0.5), 0, 219);
		int b1 = clamp(int(aBoneIndices.y + 0.5), 0, 219);
		int b2 = clamp(int(aBoneIndices.z + 0.5), 0, 219);
		int b3 = clamp(int(aBoneIndices.w + 0.5), 0, 219);

		mat4 skin = uBones[b0] * aBoneWeights.x
		          + uBones[b1] * aBoneWeights.y
		          + uBones[b2] * aBoneWeights.z
		          + uBones[b3] * aBoneWeights.w;

		pos = (skin * vec4(aPos, 1.0)).xyz;
		deformation = mat3(skin);
	}

	vec4 world = uModel * vec4(pos, 1.0);
	vWorld = world.xyz;
	mat3 transform = mat3(uModel) * deformation;
	// The normal uses inverse transpose; the tangent is a direction under the same skin/model.
	vNrm = transpose(inverse(transform)) * nrm;
	vTangent = transform * tangent;
	float mirror = determinant(transform) < 0.0 ? -1.0 : 1.0;
	vHandedness = aTangent.w * mirror;
	// A mirroring transform reverses screen winding, so gl_FrontFacing alone no longer names
	// the authored outward side.
	vMirror = mirror;
	vUv = aUv;
	vColor = aColor.rgb;
	// Bind-pose object position: procedural material masks stay stuck to the surface as it moves.
	vObject = aPos;
	gl_Position = uMvp * world;
}
";

	public const string FragmentSrc = @"#version 460 core
in vec3 vNrm;
in vec3 vWorld;
in vec2 vUv;
in vec3 vTangent;
in float vHandedness;
in float vMirror;
in vec3 vObject;
in vec3 vColor;

uniform vec3 uAlbedo;
uniform sampler2D uAlbedoMap;
uniform sampler2D uNormalMap;
uniform sampler2D uRoughnessMap;
uniform sampler2D uMetallicMap;
uniform sampler2D uEmissionMap;
uniform sampler2D uOcclusionMap;
uniform float uNormalY;
uniform float uNormalStrength;
uniform float uPs1Occlusion;
uniform float uPs1Wear;
uniform vec3 uPs1WearColor;
uniform float uPs1Grime;
uniform float uPs1GrimeFilm;
uniform float uPs1Glow;
uniform float uPs1BootDirt;
uniform float uPs1BootToe;
uniform float uPs1BootCuff;
uniform int uAlphaMode;
uniform int uDoubleSided;
uniform float uAlphaCutoff;
uniform float uBaseAlpha;
uniform vec2 uUvScale[6];
uniform vec2 uUvOffset[6];
uniform float uUvRotation[6];
uniform float uRoughness;
uniform float uMetallic;
uniform vec3 uEmission;
// Wave-2 look wiring: pasted from GlShader.LookSpectrumGlsl's helpers (the wiring
// Lane C's comment asked for), driven per batch by ViewportMesh.DrawBatched via
// GlShader.SetLook, and per live layer by LiveGeometry.Draw. Defaults are no-ops.
uniform float uLookToon;
uniform int uLookToonBands;
uniform float uLookToonRim;
uniform float uLookStylized;
uniform float uLookOutlineWidth;
uniform vec3 uLookOutlineColor;
uniform float uLookClearcoat;
uniform float uLookClearcoatRough;
uniform float uLookSubsurface;
uniform vec3 uLookSubsurfaceColor;
uniform float uLookOpacity;
uniform vec3 uLookShadowTint;
uniform float uLookIridescence;
uniform vec3 uEye;
uniform vec3 uKeyDirection;
uniform mat4 uLightViewProjection;
uniform sampler2D uShadowMap;
uniform float uShadowRadius;
uniform float uShadowNormalOffset;
uniform float uShadowRotation;
uniform float uShadowWorldTexel;
uniform float uShadowDepthRange;
uniform float uShadowSlopeBias;
uniform float uShadowConstantBias;
uniform float uExposure;
uniform float uEnvIntensity;
uniform vec3 uEnvColor;
uniform vec3 uPointLightPos;
uniform vec3 uPointLightColor;
uniform float uPointLightIntensity;
uniform sampler2D uDepthMap;
uniform vec2 uScreenSize;
uniform float uSsaoRadius;
uniform float uSsaoStrength;
uniform samplerCube uEnvMap;
uniform sampler2D uBrdfLut;
uniform float uEnvironmentMaxLod;
uniform vec3 uIrradiance[9];
uniform float uEnvMapIntensity;
uniform sampler2D uScreenColor;
uniform mat4 uView;
uniform mat4 uProjection;
uniform mat4 uInvProjection;
uniform float uSsrStrength;

out vec4 FragColor;

const float PI = 3.14159265359;

float ggx(float nh, float nl, float nv, float roughness) {
	float a = max(roughness * roughness, 0.0016);
	float a2 = a * a;
	float d = nh * nh * (a2 - 1.0) + 1.0;
	float D = a2 / (PI * d * d);
	float k = a * 0.5;
	float G = (nl / (nl * (1.0 - k) + k)) * (nv / (nv * (1.0 - k) + k));
	return (D * G) / max(4.0 * nl * nv, 1e-4);
}

vec3 fresnel(vec3 f0, float c) {
	float k = pow(1.0 - clamp(c, 0.0, 1.0), 5.0);
	return f0 + (vec3(1.0) - f0) * k;
}

float lookQuantize(float nl, int bands, float edge) {
	if (bands <= 1) return clamp(nl, 0.0, 1.0);
	float scaled = clamp(nl, 0.0, 1.0) * float(bands);
	float level = floor(scaled);
	float frac = scaled - level;
	float lo = level / float(bands);
	if (frac < edge) return mix(max(level - 1.0, 0.0) / float(bands), lo, smoothstep(0.0, edge, frac));
	if (frac > 1.0 - edge) return mix(lo, min(level + 1.0, float(bands)) / float(bands), smoothstep(1.0 - edge, 1.0, frac));
	return lo;
}
float lookDitheredAlpha(float opacity, vec2 fragCoord) {
	if (opacity >= 1.0) return 1.0;
	if (opacity <= 0.0) return 0.0;
	float t = fract(sin(dot(fragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453);
	return opacity > t ? 1.0 : 0.0;
}
vec3 lookOilFilm(float t) {
	t = clamp(t, 0.0, 1.0) * 3.0;
	vec3 blue = vec3(0.25, 0.4, 1.0), purple = vec3(0.7, 0.25, 0.95), green = vec3(0.25, 0.95, 0.55), gold = vec3(1.0, 0.8, 0.3);
	if (t < 1.0) return mix(blue, purple, t);
	if (t < 2.0) return mix(purple, green, t - 1.0);
	return mix(green, gold, t - 2.0);
}

" + MaterialFeatureShaders.Lobes + ScreenSpaceShaders.Lit + @"

vec3 shadeLight(vec3 n, vec3 v, float nv, vec3 lDir, vec3 radiance, float lightSize, vec3 diffuseCol, vec3 f0, float roughness, float ao) {
	vec3 l = normalize(lDir);
	if (r6Active()) return r6ShadeLight(n, v, nv, l, radiance, lightSize, diffuseCol, f0, roughness, ao);
	float rawNl = dot(n, l);
	float diff = max(rawNl, 0.0);
	float nl = max(rawNl, 0.0);
	if (diff <= 0.0 && nl <= 0.0) return vec3(0.0);
	// Wave-2 look wiring: cel bands step the diffuse term, mirroring LookSpectrum.
	if (uLookToon > 0.0) diff = mix(diff, lookQuantize(diff, clamp(uLookToonBands, 2, 8), 0.08), uLookToon);
	vec3 h = normalize(l + v);
	float nh = max(dot(n, h), 0.0);
	float vh = max(dot(v, h), 0.0);
	float r = max(roughness, lightSize * 0.6);
	float spec = ggx(nh, nl, nv, r);
	if (uLookToon > 0.0 && roughness < 0.4) spec = mix(spec, lookQuantize(clamp(spec, 0.0, 1.0), clamp(uLookToonBands, 2, 8), 0.08), uLookToon);
	vec3 F = fresnel(f0, vh);
	return ((vec3(1.0) - F) * diffuseCol * diff + F * spec * nl * ao) * radiance;
}

float keyVisibility(vec3 n) {
	float nl = max(dot(n, uKeyDirection), 0.0);
	vec4 clip = uLightViewProjection * vec4(vWorld + n * uShadowNormalOffset * (1.0 - nl), 1.0);
	vec3 p = clip.xyz / clip.w * 0.5 + 0.5;
	// Project the receiver plane into shadow UV/depth. Every PCF tap must
	// compare against that plane at its own UV, not the centre's depth;
	// otherwise increasing the radius self-shadows every sloped surface.
	vec3 dpdx = dFdx(p), dpdy = dFdy(p);
	float determinant = dpdx.x * dpdy.y - dpdx.y * dpdy.x;
	vec2 receiverGradient = abs(determinant) > 1e-12
		? vec2(dpdy.y * dpdx.z - dpdx.y * dpdy.z, dpdx.x * dpdy.z - dpdy.x * dpdx.z) / determinant
		: vec2(0);
	if (p.x < 0.0 || p.x > 1.0 || p.y < 0.0 || p.y > 1.0 || p.z < 0.0 || p.z > 1.0) return 1.0;
	// The bias is measured in world units (texels of the shadow map and the slope of
	// the surface) and converted with the light's depth range, so a tight fit keeps
	// the same bias instead of shrinking it into the surface.
	float slope = sqrt(max(0.0, 1.0 - nl * nl)) / max(nl, 0.05);
	float biasWorld = uShadowWorldTexel * (uShadowConstantBias + uShadowSlopeBias * slope);
	float bias = biasWorld * 2.0 / max(uShadowDepthRange, 1e-4);
	vec2 texel = 1.0 / vec2(textureSize(uShadowMap, 0));
	mat2 rotation = mat2(cos(uShadowRotation), sin(uShadowRotation), -sin(uShadowRotation), cos(uShadowRotation));
	if (uShadowRadius <= 0.0) return p.z - bias <= texture(uShadowMap,p.xy).r ? 1.0 : 0.0;
	vec2 offsets[16] = vec2[16](
		vec2(-0.94201624, -0.39906216), vec2(0.94558609, -0.76890725),
		vec2(-0.094184101, -0.92938870), vec2(0.34495938, 0.29387760),
		vec2(-0.91588581, 0.45771432), vec2(-0.81544232, -0.87912464),
		vec2(-0.38277543, 0.27676845), vec2(0.97484398, 0.75648379),
		vec2(0.44323325, -0.97511554), vec2(0.53742981, -0.47373420),
		vec2(-0.26496911, -0.41893023), vec2(0.79197514, 0.19090188),
		vec2(-0.24188840, 0.99706507), vec2(-0.81409955, 0.91437590),
		vec2(0.19984126, 0.78641367), vec2(0.14383161, -0.14100790)
	);
	// Blocker search (PCSS).
	float blockerSum = 0.0;
	int blockerCount = 0;
	for (int i = 0; i < 16; ++i) {
		vec2 o = rotation * offsets[i] * texel * max(1.5, uShadowRadius);
		vec2 sampleUv = p.xy + o;
		if (any(lessThan(sampleUv,vec2(0))) || any(greaterThan(sampleUv,vec2(1)))) continue;
		float d = texture(uShadowMap, sampleUv).r;
		float receiverDepth = p.z + dot(receiverGradient, o);
		if (d < receiverDepth - bias) { blockerSum += d - dot(receiverGradient, o); blockerCount++; }
	}
	if (blockerCount == 0) return 1.0;
	float avgBlocker = blockerSum / float(blockerCount);
	float penumbra = clamp((p.z - avgBlocker) / max(avgBlocker, 1e-4) * 0.22, 0.0, 0.08);
	// PCF with penumbra-scaled kernel.
	float visible = 0.0;
	for (int i = 0; i < 16; ++i) {
		vec2 o = rotation * offsets[i] * texel * uShadowRadius * (1.0 + penumbra * 20.0);
		vec2 sampleUv = p.xy + o;
		visible += any(lessThan(sampleUv,vec2(0))) || any(greaterThan(sampleUv,vec2(1)))
			? 1.0 : (p.z + dot(receiverGradient, o) - bias <= texture(uShadowMap,sampleUv).r ? 1.0 : 0.0);
	}
	return visible / 16.0;
}

vec2 transformUv(vec2 uv, int idx) {
	float r = uUvRotation[idx];
	vec2 p = uv - vec2(0.5);
	float c = cos(r), s = sin(r);
	p = vec2(p.x * c - p.y * s, p.x * s + p.y * c) + vec2(0.5);
	return p * uUvScale[idx] + uUvOffset[idx];
}

// ps1_lit's hash and eased value noise, so procedural masks match the game's.
float ps1Hash(vec3 p) {
	p = fract(p * vec3(0.1031, 0.1030, 0.0973));
	p += dot(p, p.yzx + 33.33);
	return fract((p.x + p.y) * p.z);
}
float ps1Noise(vec3 p) {
	vec3 c = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = mix(mix(ps1Hash(c), ps1Hash(c + vec3(1, 0, 0)), f.x), mix(ps1Hash(c + vec3(0, 1, 0)), ps1Hash(c + vec3(1, 1, 0)), f.x), f.y);
	float b = mix(mix(ps1Hash(c + vec3(0, 0, 1)), ps1Hash(c + vec3(1, 0, 1)), f.x), mix(ps1Hash(c + vec3(0, 1, 1)), ps1Hash(c + vec3(1, 1, 1)), f.x), f.y);
	return mix(a, b, f.z);
}

void main() {
	vec3 n = normalize(vNrm);
	vec3 tProj = vTangent - n * dot(n, vTangent);
	if (dot(tProj, tProj) > 1e-6) {
		vec3 t = normalize(tProj);
		vec3 b = cross(n, t) * (vHandedness < 0.0 ? -1.0 : 1.0);
		vec3 mapNormal = texture(uNormalMap, transformUv(vUv, 1)).xyz * 2.0 - 1.0;
		mapNormal.y *= uNormalY;
		mapNormal.xy *= uNormalStrength;
		if (dot(mapNormal, mapNormal) > 1e-4) {
			n = normalize(mat3(t, b, n) * normalize(mapNormal));
		}
	}
	// R4: facing comes from rasterised winding (corrected for mirroring), never from dot(n, v).
	// The normal map is applied in the authored front-side frame first; a backface then
	// negates the whole perturbed normal, which is the same bumpy sheet seen from behind.
	bool backface = gl_FrontFacing != (vMirror >= 0.0);
	if (backface) n = -n;
	r6Frame(n, backface ? -vNrm : vNrm, vTangent);
	// Single-sided materials have no lit back side: their backfaces (a closed mesh seen
	// from inside) get no studio/direct light, only dim ambient.
	bool unlitBack = backface && uDoubleSided == 0;
	float directMask = unlitBack ? 0.0 : 1.0;
	float ambientMask = unlitBack ? 0.35 : 1.0;
	vec3 v = normalize(uEye - vWorld);
	float nv = clamp(dot(n, v), 1e-3, 1.0);

	// Separate scalar maps use red (R), not implicit ORM packing. Factors are linear multipliers.
	float rough = clamp(uRoughness * texture(uRoughnessMap, transformUv(vUv, 2)).r, 0.04, 1.0);
	// Toksvig specular anti-aliasing: widen roughness where normals vary across the pixel.
	vec3 dNdx = dFdx(n);
	vec3 dNdy = dFdy(n);
	float normalVariance = max(dot(dNdx, dNdx), dot(dNdy, dNdy));
	rough = clamp(sqrt(rough * rough + normalVariance * 0.5), 0.04, 1.0);
	float metal = clamp(uMetallic * texture(uMetallicMap, transformUv(vUv, 3)).r, 0.0, 1.0);
	vec4 albedoTex = texture(uAlbedoMap, transformUv(vUv, 0));
	vec3 albedo = uAlbedo * albedoTex.rgb * vColor;
	float matAlpha = uBaseAlpha * albedoTex.a;
	if (uAlphaMode == 1 && matAlpha < uAlphaCutoff) discard;
	if (uAlphaMode == 3 && matAlpha <= r6Hash()) discard;
	// Wave-2 look wiring: opaque batches with a look opacity below 1 dither away
		// (stipple, not a wash). Blend-mode batches keep their own alpha path.
	if (uAlphaMode == 0 && uLookOpacity < 1.0 && lookDitheredAlpha(uLookOpacity, gl_FragCoord.xy) <= 0.0) discard;
	float ao = texture(uOcclusionMap, transformUv(vUv, 5)).r;
	ao = clamp(ao * uPs1Occlusion, 0.0, 1.0);
	// Cheap screen-space-ish ambient occlusion from normal variance (cavity darkening).
	ao = clamp(ao * (1.0 - clamp(normalVariance * 1.5, 0.0, 0.45)), 0.0, 1.0);
	// R5: hemisphere SSAO with bilateral blur, computed after the depth pre-pass (white during it).
	if (uScreenSize.x > 0.0) ao *= texture(uAoMap, gl_FragCoord.xy / uScreenSize).r;
	// Wear: edge wear blending wear color by fresnel.
	float wearMask = uPs1Wear * pow(1.0 - nv, 2.0);
	albedo = mix(albedo, uPs1WearColor, clamp(wearMask, 0.0, 1.0));
	// Grime: dark overlay modulated by film.
	float grimeMask = uPs1Grime * (0.5 + 0.5 * (1.0 - n.z));
	albedo = mix(albedo, albedo * 0.35, clamp(grimeMask * uPs1GrimeFilm, 0.0, 1.0));
	// Boot mud: ps1_lit's g_flPs1BootDirt block, in object-space inches.
	if (uPs1BootDirt > 0.01) {
		vec3 p = vObject;
		float patchN = ps1Noise(p * 1.2 + 17.3);
		float grit = ps1Noise(p * 7.0 + 43.1);
		float toe = smoothstep(uPs1BootToe, uPs1BootToe + 3.0, p.x);
		float low = 1.0 - smoothstep(0.8, 2.8, p.z + (patchN - 0.5) * 0.8);
		float cuff = smoothstep(uPs1BootCuff - 0.65, uPs1BootCuff, p.z);
		vec3 bootSurface = normalize(cross(dFdx(p), dFdy(p)));
		float top = smoothstep(0.45, 0.85, abs(bootSurface.z)) * smoothstep(0.65, 1.2, p.z) * (1.0 - smoothstep(3.8, 5.0, p.z));
		float topPatch = ps1Noise(p * 0.65 + 7.8);
		float topMud = top * smoothstep(0.28, 0.53, topPatch + (grit - 0.5) * 0.22);
		float edgeMud = low * (0.40 + 0.60 * smoothstep(0.30, 0.66, patchN));
		float splash = smoothstep(0.56, 0.72, patchN) * (0.20 + toe * 0.30 + cuff * 0.15);
		float mud = clamp(max(topMud, max(edgeMud, splash)) * uPs1BootDirt, 0.0, 1.0);
		vec3 dirt = mix(vec3(0.020, 0.014, 0.008), vec3(0.055, 0.040, 0.025), grit);
		albedo = mix(albedo, dirt, mud);
		rough = max(rough, mix(0.85, 1.0, mud));
		metal = 0.0;
	}
	// Glow: add to emission.
	vec3 glowAdd = uPs1Glow * albedo;
	vec3 f0 = mix(vec3(0.04), albedo, metal);
	// R6: transmission replaces the dielectric diffuse lobe.
	vec3 diffuseCol = albedo * (1.0 - metal) * (1.0 - uTransmission);

	// Studio lighting:
	// 1. Key light: directional with soft cast shadows
	vec3 keyDir = normalize(uKeyDirection);
	// 2. Fill light: cool fill from opposite flank
	vec3 fillDir = vec3(-0.7, -0.5, 0.4);
	// 3. Rim light: top-side highlight catching silhouettes and hair/shoulders
	vec3 rimDir = vec3(0.2, 0.8, 0.6);
	// 4. Back / Butt light: warm bounce from behind so back/butt is never pitch black
	vec3 backDir = vec3(-0.6, 0.7, 0.2);
	vec3 bounceDir = vec3(0.0, 0.0, -1.0);

	vec3 hdr = vec3(0.0);
	vec3 direct = vec3(0.0);
	direct += shadeLight(n, v, nv, keyDir, vec3(1.0, 0.95, 0.88) * 3.0, 0.22, diffuseCol, f0, rough, ao) * keyVisibility(n);
	direct += shadeLight(n, v, nv, fillDir, vec3(0.65, 0.80, 1.0) * 0.8, 0.40, diffuseCol, f0, rough, ao);
	direct += shadeLight(n, v, nv, rimDir, vec3(1.6), 0.22, diffuseCol, f0, rough, ao);
	direct += shadeLight(n, v, nv, backDir, vec3(0.95, 0.90, 0.85) * 0.9, 0.32, diffuseCol, f0, rough, ao);
	direct += shadeLight(n, v, nv, bounceDir, vec3(0.4, 0.38, 0.35) * 0.3, 0.6, diffuseCol, f0, rough, ao);
	// Optional point light.
	vec3 plDir = uPointLightPos - vWorld;
	float plDist = length(plDir);
	if (plDist > 1e-4) {
		vec3 pl = plDir / plDist;
		float plAtt = 1.0 / (1.0 + plDist * plDist * 0.02);
		direct += shadeLight(n, v, nv, pl, uPointLightColor * uPointLightIntensity * plAtt, 0.25, diffuseCol, f0, rough, ao);
	}
	hdr += direct * directMask;
		// Wave-4 SSS: wrapped diffuse + backlight translucency, mirroring kernel
		// ShadePixel and GlShader.lookSpectrum. uLookSubsurface is 0 for non-SSS
		// looks, so this is a no-op there. shadeLight returns 0 on the shadow side,
		// so this term is what warms the terminator and backlit rims red. The 3.0
		// factor is the key light's radiance, matching the key shadeLight call above.
		if (uLookSubsurface > 0.0) {
			float sssWrap = 0.5 * uLookSubsurface;
			float nlKey = dot(n, keyDir);
			float wrapDiff = max(0.0, (nlKey + sssWrap) / (1.0 + sssWrap));
			float backKey = pow(max(0.0, dot(v, -keyDir)), 3.0);
			float rimBack = pow(1.0 - nv, 3.0) * max(0.0, dot(v, -keyDir));
			hdr += uLookSubsurfaceColor * albedo * uLookSubsurface * (wrapDiff * 0.5 + backKey * 0.75 + rimBack * 0.5) * 3.0 * directMask;
		}
	// Environment lighting from cubemap.
	float sh[9] = float[9](0.2820947918, 0.4886025119*n.y, 0.4886025119*n.z, 0.4886025119*n.x,
		1.0925484306*n.x*n.y, 1.0925484306*n.y*n.z, 0.3153915653*(3.0*n.z*n.z-1.0),
		1.0925484306*n.x*n.z, 0.5462742153*(n.x*n.x-n.y*n.y));
	vec3 irradiance = vec3(0);
	for (int i=0; i<9; i++) irradiance += uIrradiance[i]*sh[i];
	vec3 envDiffuse = max(irradiance, vec3(0)) / 3.14159265359 * uEnvMapIntensity;
	hdr += diffuseCol * envDiffuse * ao * uEnvIntensity * uEnvColor * ambientMask * uLookShadowTint;
	vec3 skyCol    = vec3(0.24, 0.27, 0.34);
	vec3 horizonCol = vec3(0.18, 0.19, 0.22);
	vec3 groundCol = vec3(0.10, 0.09, 0.10);
	float hemiMix = n.z * 0.5 + 0.5;
	vec3 hemi = mix(groundCol, mix(horizonCol, skyCol, smoothstep(0.0, 1.0, hemiMix)), hemiMix);
	hdr += diffuseCol * hemi * ao * (1.0 - uEnvMapIntensity) * uEnvIntensity * uEnvColor * ambientMask * uLookShadowTint;

	// Ground bounce / ambient reflection with fresnel and roughness preservation for metals
	vec3 refl = r6BendReflection(reflect(-v, n), n, v, rough);
	vec3 envSpec = textureLod(uEnvMap, refl, rough * uEnvironmentMaxLod).rgb * uEnvMapIntensity * uEnvIntensity * uEnvColor;
	vec2 brdf = texture(uBrdfLut, vec2(nv, rough)).rg;
	vec3 envF = f0 * brdf.x + brdf.y;
	// Wave-2 look wiring: oil-film hue shift on grazing reflections, mirroring
		// ShadeMaterial.Iridescence (same blue->purple->green->gold phase walk).
	if (uLookIridescence > 0.0) {
		float filmPhase = 1.0 - nv + abs(mod((vWorld.x + vWorld.y * 0.7 + vWorld.z * 0.4) * 0.05, 0.35));
		envF = mix(envF, envF * lookOilFilm(filmPhase) * 1.5, uLookIridescence);
	}
	hdr += envF * envSpec * ao * ao * directMask;
	// Wave-2 look wiring: stepped rim highlight at the silhouette, mirroring LookSpectrum.
	if (uLookToon > 0.0 && uLookToonRim > 0.0) {
		float fres = pow(1.0 - nv, 4.0);
		hdr += vec3(1.0) * (smoothstep(0.62, 0.68, fres) * uLookToonRim * uLookToon * 0.5) * directMask;
	}
	// Wave-2 look wiring, stylized half: no cavity channel reaches this shader, so
		// (1 - ao) is the crease proxy — open areas saturate, shut areas warm/darken.
	if (uLookStylized > 0.0) {
		float lum = (hdr.r + hdr.g + hdr.b) / 3.0;
		hdr = mix(vec3(lum), hdr, 1.0 + 0.2 * uLookStylized);
		hdr = mix(hdr, vec3(hdr.r, hdr.g * 0.85, hdr.b * 0.65), uLookStylized * (1.0 - ao) * 0.5);
		hdr *= 1.0 - 0.125 * uLookStylized * ao;
	}
	// R5: view-space SSR with binary refinement; replaces the environment where it hits.
	if (uSsrStrength > 0.0 && uScreenSize.x > 0.0 && !unlitBack) {
		vec4 hit = r5Ssr(n, rough);
		hdr += envF * (hit.rgb - envSpec * ao * ao * directMask) * hit.a * uSsrStrength;
	}
	r6Ambient(hdr, n, v, nv, albedo, metal, rough, f0, ao * ambientMask, uEnvMapIntensity * uEnvIntensity * uEnvColor, uEnvironmentMaxLod);
	hdr += (uEmission + glowAdd) * texture(uEmissionMap, transformUv(vUv, 4)).rgb;

	// Subtle distance fog for depth perception (linear, toward the dark studio backdrop).
	float fogDist = length(uEye - vWorld);
	float fog = clamp((fogDist - 8.0) * 0.04, 0.0, 0.35);
	vec3 fogColor = vec3(0.12, 0.125, 0.14);
	hdr = mix(hdr, fogColor, fog);
	// R1: no in-shader bloom approximation; bloom runs on the HDR target (BloomPass).
	// R1: write linear HDR; tone-map + sRGB encode happen once in the composite.
	float outAlpha = (uAlphaMode == 2) ? matAlpha : 1.0;
	FragColor = vec4(hdr * uExposure, outAlpha);
}
";
}
