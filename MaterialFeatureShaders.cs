namespace Marionette.Desktop;

/// <summary>R6 material lobes for the lit shader: subsurface (wrapped per-channel diffuse plus
/// back-lit translucency), sheen (Charlie distribution, Neubelt visibility), clearcoat (second
/// GGX layer on the geometric normal), anisotropic GGX, screen-space transmission with
/// refraction and Beer-Lambert volume attenuation, and hashed alpha. Spliced into
/// <see cref="RendererShaders.FragmentSrc"/> after ggx()/fresnel(); every entry point is a
/// no-op for materials with all R6 weights at zero, so those render exactly as before.</summary>
public static class MaterialFeatureShaders
{
	public const string Lobes = @"
uniform float uTransmission;
uniform float uThickness;
uniform float uIor;
uniform vec3 uAttenuationColor;
uniform float uAttenuationDistance;
uniform float uSubsurface;
uniform vec3 uSubsurfaceRadius;
uniform float uSubsurfaceScale;
uniform float uSheen;
uniform vec3 uSheenTint;
uniform float uSheenRoughness;
uniform float uClearcoat;
uniform float uClearcoatRoughness;
uniform float uAnisotropy;
uniform float uAnisotropyRotation;
uniform sampler2D uOpaqueColor;

vec3 r6GeomN = vec3(0.0, 0.0, 1.0);
vec3 r6T = vec3(1.0, 0.0, 0.0);
vec3 r6B = vec3(0.0, 1.0, 0.0);

bool r6Active() {
	return uTransmission > 0.0 || uSubsurface > 0.0 || uSheen > 0.0 || uClearcoat > 0.0 || abs(uAnisotropy) > 1e-4;
}

// Shading frame for anisotropy and the clearcoat's (unperturbed) normal. n is the final
// shading normal, geomN the interpolated normal, both already flipped for backfaces.
void r6Frame(vec3 n, vec3 geomN, vec3 tangent) {
	r6GeomN = normalize(geomN);
	vec3 t = tangent - n * dot(n, tangent);
	if (dot(t, t) < 1e-6) t = abs(n.z) < 0.9 ? cross(vec3(0.0, 0.0, 1.0), n) : cross(vec3(1.0, 0.0, 0.0), n);
	t = normalize(t);
	vec3 b = cross(n, t);
	float c = cos(uAnisotropyRotation), s = sin(uAnisotropyRotation);
	r6T = t * c + b * s;
	r6B = cross(n, r6T);
}

// Interleaved gradient noise: a stable per-pixel threshold for hashed alpha.
float r6Hash() {
	return fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
}

float r6AnisoGgx(vec3 n, vec3 h, float nl, float nv, float roughness) {
	float a = max(roughness * roughness, 0.0016);
	float at = max(a * (1.0 + uAnisotropy), 0.001);
	float ab = max(a * (1.0 - uAnisotropy), 0.001);
	float th = dot(r6T, h), bh = dot(r6B, h), nh = dot(n, h);
	float d = th * th / (at * at) + bh * bh / (ab * ab) + nh * nh;
	float D = 1.0 / (PI * at * ab * d * d);
	float k = a * 0.5;
	float G = (nl / (nl * (1.0 - k) + k)) * (nv / (nv * (1.0 - k) + k));
	return (D * G) / max(4.0 * nl * nv, 1e-4);
}

float r6SheenD(float nh, float r) {
	float inv = 1.0 / r;
	float sin2 = max(1.0 - nh * nh, 0.0078125);
	return (2.0 + inv) * pow(sin2, inv * 0.5) / (2.0 * PI);
}

float r6SheenV(float nl, float nv) {
	return 1.0 / max(4.0 * (nl + nv - nl * nv), 1e-4);
}

// Full direct-light response for materials using any R6 lobe; mirrors shadeLight's
// conventions (unnormalised diffuse, specular masked by ao).
vec3 r6ShadeLight(vec3 n, vec3 v, float nv, vec3 l, vec3 radiance, float lightSize, vec3 diffuseCol, vec3 f0, float roughness, float ao) {
	float rawNl = dot(n, l);
	float nl = max(rawNl, 0.0);
	// Subsurface: per-channel wrap; longer mean free paths (red in skin) bleed further.
	vec3 wrap = uSubsurface * clamp(uSubsurfaceRadius * uSubsurfaceScale * 20.0, 0.0, 1.0);
	vec3 diff = clamp((vec3(rawNl) + wrap) / (1.0 + wrap), 0.0, 1.0) / (1.0 + wrap);
	// Back-lit translucency through thin parts (ears, fingers, leaves).
	float back = pow(clamp(dot(v, -l), 0.0, 1.0), 4.0) * clamp(-rawNl + 0.3, 0.0, 1.0);
	vec3 translucency = diffuseCol * wrap * back * 0.5;
	vec3 h = normalize(l + v);
	float nh = max(dot(n, h), 0.0);
	float vh = clamp(dot(v, h), 0.0, 1.0);
	float r = max(roughness, lightSize * 0.6);
	vec3 F = fresnel(f0, vh);
	float spec = nl > 0.0 ? (abs(uAnisotropy) > 1e-4 ? r6AnisoGgx(n, h, nl, nv, r) : ggx(nh, nl, nv, r)) : 0.0;
	vec3 base = (vec3(1.0) - F) * diffuseCol * diff + F * spec * nl * ao + translucency;
	if (uSheen > 0.0) {
		vec3 sheenCol = uSheen * uSheenTint;
		base *= 1.0 - 0.157 * max(sheenCol.r, max(sheenCol.g, sheenCol.b));
		base += sheenCol * r6SheenD(nh, uSheenRoughness) * r6SheenV(nl, nv) * nl;
	}
	if (uClearcoat > 0.0) {
		vec3 nc = r6GeomN;
		float ncl = max(dot(nc, l), 0.0);
		float ncv = clamp(dot(nc, v), 1e-3, 1.0);
		float nch = max(dot(nc, h), 0.0);
		float Fc = uClearcoat * (0.04 + 0.96 * pow(1.0 - vh, 5.0));
		float cr = max(uClearcoatRoughness, lightSize * 0.6);
		base = base * (1.0 - Fc) + Fc * ggx(nch, ncl, ncv, cr) * ncl * ao;
	}
	return base * radiance;
}

// Anisotropic bent reflection vector for environment lookups (Filament's approximation).
vec3 r6BendReflection(vec3 refl, vec3 n, vec3 v, float roughness) {
	if (abs(uAnisotropy) < 1e-4) return refl;
	vec3 dir = uAnisotropy >= 0.0 ? r6B : r6T;
	vec3 anisoT = cross(dir, v);
	vec3 anisoN = cross(anisoT, dir);
	vec3 bent = normalize(mix(n, anisoN, abs(uAnisotropy) * clamp(roughness * 4.0, 0.0, 1.0)));
	return reflect(-v, bent);
}

// Ambient lobes and transmission, added after the base environment terms. envScale is the
// environment radiance scale (intensity * colour), ambientMask the unlit-back dimming.
void r6Ambient(inout vec3 hdr, vec3 n, vec3 v, float nv, vec3 albedo, float metal, float roughness, vec3 f0, float ao, vec3 envScale, float maxLod) {
	if (!r6Active()) return;
	if (uTransmission > 0.0) {
		// Refract the view ray through the surface and read the opaque scene behind it.
		vec3 rd = refract(-v, n, 1.0 / max(uIor, 1.0));
		if (dot(rd, rd) < 1e-6) rd = -v;
		float travel = max(uThickness, 0.02);
		vec4 clip = uProjection * uView * vec4(vWorld + rd * travel, 1.0);
		vec2 uv = clamp(clip.xy / clip.w * 0.5 + 0.5, vec2(0.001), vec2(0.999));
		// Rough glass: a small disk blur that widens with roughness.
		vec2 px = 1.0 / max(uScreenSize, vec2(1.0));
		float radius = roughness * roughness * 24.0;
		vec3 behind = texture(uOpaqueColor, uv).rgb;
		if (radius > 0.5) {
			for (int i = 0; i < 8; i++) {
				float a = float(i) * 2.39996323;
				float rr = sqrt((float(i) + 0.5) / 8.0) * radius;
				behind += texture(uOpaqueColor, uv + vec2(cos(a), sin(a)) * rr * px).rgb;
			}
			behind /= 9.0;
		}
		// The opaque capture is already exposed linear HDR; return to scene
		// radiance before the lit shader applies this surface's exposure once.
		behind /= max(uExposure, 1e-6);
		vec3 absorb = uAttenuationDistance > 0.0 ? pow(max(uAttenuationColor, vec3(1e-4)), vec3(uThickness / uAttenuationDistance)) : vec3(1.0);
		vec3 Ft = fresnel(f0, nv);
		hdr += behind * albedo * absorb * (vec3(1.0) - Ft) * uTransmission * (1.0 - metal);
	}
	if (uSheen > 0.0) {
		vec3 ambient = textureLod(uEnvMap, n, maxLod).rgb * envScale;
		hdr += uSheen * uSheenTint * ambient * ao * 0.2 * pow(max(1.0 - nv, 0.0), 3.0);
	}
	if (uClearcoat > 0.0) {
		vec3 nc = r6GeomN;
		float ncv = clamp(dot(nc, v), 1e-3, 1.0);
		float Fc = uClearcoat * (0.04 + 0.96 * pow(1.0 - ncv, 5.0));
		vec3 env = textureLod(uEnvMap, reflect(-v, nc), uClearcoatRoughness * maxLod).rgb * envScale;
		hdr = hdr * (1.0 - Fc) + env * Fc * ao;
	}
}
";
}
