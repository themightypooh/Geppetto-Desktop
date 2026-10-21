namespace Marionette.Desktop;

/// <summary>R5 screen-space shaders: hemisphere SSAO, its depth-aware bilateral blur, and the
/// lit shader's view-space SSR march (spliced into <see cref="RendererShaders"/>).</summary>
public static class ScreenSpaceShaders
{
	const string ViewFromDepth = @"
vec3 viewPos(vec2 uv, float d) {
	vec4 p = uInvProjection * vec4(uv * 2.0 - 1.0, d * 2.0 - 1.0, 1.0);
	return p.xyz / p.w;
}
";

	public const string SsaoFs = @"#version 460 core
in vec2 vUv;
out vec4 FragColor;
uniform sampler2D uDepth;
uniform sampler2D uNoise;
uniform mat4 uProjection;
uniform mat4 uInvProjection;
uniform vec3 uKernel[16];
uniform float uRadius;
uniform float uBias;
uniform float uIntensity;
uniform vec2 uNoiseScale;
" + ViewFromDepth + @"
vec3 posAt(ivec2 p) {
	ivec2 s = textureSize(uDepth, 0);
	p = clamp(p, ivec2(0), s - 1);
	return viewPos((vec2(p) + 0.5) / vec2(s), texelFetch(uDepth, p, 0).r);
}
void main() {
	ivec2 px = ivec2(gl_FragCoord.xy);
	float d = texelFetch(uDepth, px, 0).r;
	if (d >= 1.0) { FragColor = vec4(1.0); return; }
	vec3 P = posAt(px);
	// Normal from depth: pick the smaller-difference neighbour per axis so silhouettes
	// do not produce normals bent across the depth discontinuity.
	vec3 l = posAt(px - ivec2(1, 0)), r = posAt(px + ivec2(1, 0));
	vec3 b = posAt(px - ivec2(0, 1)), t = posAt(px + ivec2(0, 1));
	vec3 dx = abs(r.z - P.z) < abs(P.z - l.z) ? r - P : P - l;
	vec3 dy = abs(t.z - P.z) < abs(P.z - b.z) ? t - P : P - b;
	vec3 N = normalize(cross(dx, dy));
	if (dot(N, P) > 0.0) N = -N;
	vec3 rnd = vec3(texture(uNoise, vUv * uNoiseScale).xy, 0.0);
	vec3 T = normalize(rnd - N * dot(rnd, N));
	mat3 TBN = mat3(T, cross(N, T), N);
	float occ = 0.0;
	for (int i = 0; i < 16; i++) {
		vec3 s = P + TBN * uKernel[i] * uRadius;
		vec4 c = uProjection * vec4(s, 1.0);
		vec2 uv = c.xy / c.w * 0.5 + 0.5;
		if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) continue;
		// Compare against the reconstructed surface at that texel, not the sample's own depth:
		// at grazing angles one depth texel spans more depth than any usable bias, so a
		// depth-versus-depth test makes flat ground occlude itself. A point on P's own plane
		// has dot(v, N) == 0 whatever texel it lands in.
		ivec2 sp = ivec2(uv * vec2(textureSize(uDepth, 0)));
		if (texelFetch(uDepth, clamp(sp, ivec2(0), textureSize(uDepth, 0) - 1), 0).r >= 1.0) continue;
		vec3 v = posAt(sp) - P;
		float dist = length(v);
		float range = 1.0 - smoothstep(uRadius, uRadius * 2.0, dist);
		occ += (dot(v, N) > uBias + 0.1 * dist ? 1.0 : 0.0) * range;
	}
	float ao = clamp(1.0 - occ / 16.0 * uIntensity, 0.0, 1.0);
	FragColor = vec4(ao * ao);
}
";

	public const string BilateralBlurFs = @"#version 460 core
in vec2 vUv;
out vec4 FragColor;
uniform sampler2D uAo;
uniform sampler2D uDepth;
uniform mat4 uInvProjection;
uniform vec2 uStep;
uniform float uDepthSigma;
" + ViewFromDepth + @"
void main() {
	float d0 = texture(uDepth, vUv).r;
	if (d0 >= 1.0) { FragColor = vec4(1.0); return; }
	float z0 = viewPos(vUv, d0).z;
	float sum = 0.0, wsum = 0.0;
	for (int i = -4; i <= 4; i++) {
		vec2 uv = vUv + uStep * float(i);
		float d = texture(uDepth, uv).r;
		float dz = (d >= 1.0) ? 1e9 : viewPos(uv, d).z - z0;
		float w = exp(-float(i * i) / 8.0) * exp(-dz * dz / (2.0 * uDepthSigma * uDepthSigma));
		sum += texture(uAo, uv).r * w; wsum += w;
	}
	FragColor = vec4(sum / max(wsum, 1e-5));
}
";

	/// <summary>Lit-shader functions (needs uDepthMap, uScreenColor, uView, uProjection,
	/// uInvProjection, uScreenSize, vWorld, r6Hash). <c>r5Ssr</c> returns hit colour and
	/// confidence; confidence 0 means fall back to the environment.</summary>
	public const string Lit = @"
uniform sampler2D uAoMap;
" + ViewFromDepth + @"
vec4 r5Ssr(vec3 n, float rough) {
	float roughFade = 1.0 - smoothstep(0.35, 0.65, rough);
	if (roughFade <= 0.0) return vec4(0.0);
	vec3 vpos = (uView * vec4(vWorld, 1.0)).xyz;
	vec3 vn = normalize(mat3(uView) * n);
	vec3 dir = reflect(normalize(vpos), vn);
	// Rays heading back at the camera leave the screen behind the near plane: fade them.
	float facing = 1.0 - smoothstep(0.0, 0.6, dir.z);
	if (facing <= 0.0) return vec4(0.0);
	float maxDist = length(vpos) * 0.75;
	const int STEPS = 48;
	float stepLen = maxDist / float(STEPS);
	float thickness = stepLen * 2.5;
	vec3 ray = vpos + vn * stepLen * 0.1 + dir * stepLen * r6Hash();
	vec3 prev = ray;
	for (int i = 0; i < STEPS; i++) {
		prev = ray;
		ray += dir * stepLen;
		vec4 c = uProjection * vec4(ray, 1.0);
		if (c.w <= 0.0) break;
		vec2 uv = c.xy / c.w * 0.5 + 0.5;
		if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) break;
		float sd = texture(uDepthMap, uv).r;
		if (sd >= 1.0) continue;
		float sceneZ = viewPos(uv, sd).z;
		float dz = sceneZ - ray.z;
		if (dz > 0.0 && dz < thickness) {
			// Binary refinement between the last miss and this hit.
			vec3 a = prev, b = ray;
			for (int j = 0; j < 6; j++) {
				vec3 m = (a + b) * 0.5;
				vec4 mc = uProjection * vec4(m, 1.0);
				vec2 muv = mc.xy / mc.w * 0.5 + 0.5;
				float mz = viewPos(muv, texture(uDepthMap, muv).r).z;
				if (mz > m.z) b = m; else a = m;
			}
			vec4 hc = uProjection * vec4(b, 1.0);
			vec2 huv = hc.xy / hc.w * 0.5 + 0.5;
			vec2 edge = smoothstep(vec2(0.0), vec2(0.08), huv) * (1.0 - smoothstep(vec2(0.92), vec2(1.0), huv));
			float distFade = 1.0 - smoothstep(0.6, 1.0, float(i) / float(STEPS));
			float conf = edge.x * edge.y * roughFade * facing * distFade;
			// Blur rougher hits by sampling the history at a coarser mip-equivalent footprint.
			vec2 px = rough * 6.0 / uScreenSize;
			vec3 col = (texture(uScreenColor, huv).rgb * 2.0
				+ texture(uScreenColor, huv + vec2(px.x, 0)).rgb + texture(uScreenColor, huv - vec2(px.x, 0)).rgb
				+ texture(uScreenColor, huv + vec2(0, px.y)).rgb + texture(uScreenColor, huv - vec2(0, px.y)).rgb) / 6.0;
			return vec4(col, conf);
		}
	}
	return vec4(0.0);
}
";
}
