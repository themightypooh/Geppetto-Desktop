namespace Marionette.Desktop;

/// <summary>Post-chain shader sources (bloom, depth of field, view transform, display encode).
///
/// Colour convention: albedo/base-colour and emission images are sRGB-encoded inputs and are
/// decoded to linear when sampled; roughness, metallic, normal, occlusion and mask images are
/// linear data. Lighting runs in linear Rec.709. The lit pass writes linear HDR into an
/// Rgba16f target. Bloom and depth of field operate on that linear HDR. The view transform
/// (AgX by default, matching Blender's default view with look "None") and the sRGB display
/// encode happen exactly once, in <see cref="Composite"/>.</summary>
public static class PostChainShaders
{
	public const string FullscreenVs = @"#version 460 core
out vec2 vUv;
void main() {
	vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
	vUv = p;
	gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
";

	/// <summary>13-tap downsample (Jimenez 2014). The first level applies the soft-knee
	/// threshold in linear units and Karis-averages the sample groups so single hot pixels
	/// cannot flicker the whole halo.</summary>
	public const string DownsampleFs = @"#version 460 core
in vec2 vUv;
out vec4 FragColor;
uniform sampler2D uSrc;
uniform int uFirst;
uniform float uThreshold;
uniform float uKnee;
float luma(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }
vec3 karis(vec3 a, vec3 b, vec3 c, vec3 d) {
	float wa = 1.0 / (1.0 + luma(a)), wb = 1.0 / (1.0 + luma(b)), wc = 1.0 / (1.0 + luma(c)), wd = 1.0 / (1.0 + luma(d));
	return (a * wa + b * wb + c * wc + d * wd) / (wa + wb + wc + wd);
}
vec3 tap(vec2 uv) { return min(texture(uSrc, uv).rgb, vec3(65000.0)); }
void main() {
	vec2 t = 1.0 / vec2(textureSize(uSrc, 0));
	vec3 a = tap(vUv + t * vec2(-2, 2)), b = tap(vUv + t * vec2(0, 2)), c = tap(vUv + t * vec2(2, 2));
	vec3 d = tap(vUv + t * vec2(-2, 0)), e = tap(vUv), f = tap(vUv + t * vec2(2, 0));
	vec3 g = tap(vUv + t * vec2(-2, -2)), h = tap(vUv + t * vec2(0, -2)), i = tap(vUv + t * vec2(2, -2));
	vec3 j = tap(vUv + t * vec2(-1, 1)), k = tap(vUv + t * vec2(1, 1)), l = tap(vUv + t * vec2(-1, -1)), m = tap(vUv + t * vec2(1, -1));
	vec3 col;
	if (uFirst == 1) {
		col = karis(j, k, l, m) * 0.5
			+ karis(a, b, d, e) * 0.125 + karis(b, c, e, f) * 0.125
			+ karis(d, e, g, h) * 0.125 + karis(e, f, h, i) * 0.125;
		float br = max(col.r, max(col.g, col.b));
		float soft = clamp(br - uThreshold + uKnee, 0.0, 2.0 * uKnee);
		soft = soft * soft / (4.0 * uKnee + 1e-5);
		col *= max(soft, br - uThreshold) / max(br, 1e-5);
	} else {
		col = (j + k + l + m) * 0.125
			+ (a + c + g + i) * 0.03125
			+ (b + d + f + h) * 0.0625
			+ e * 0.125;
	}
	FragColor = vec4(max(col, vec3(0.0)), 1.0);
}
";

	/// <summary>3x3 tent upsample, additively blended onto the next larger level.</summary>
	public const string UpsampleFs = @"#version 460 core
in vec2 vUv;
out vec4 FragColor;
uniform sampler2D uSrc;
uniform float uRadius;
void main() {
	vec2 t = uRadius / vec2(textureSize(uSrc, 0));
	vec3 s = texture(uSrc, vUv).rgb * 4.0;
	s += (texture(uSrc, vUv + vec2(-t.x, 0)).rgb + texture(uSrc, vUv + vec2(t.x, 0)).rgb
		+ texture(uSrc, vUv + vec2(0, -t.y)).rgb + texture(uSrc, vUv + vec2(0, t.y)).rgb) * 2.0;
	s += texture(uSrc, vUv + vec2(-t.x, -t.y)).rgb + texture(uSrc, vUv + vec2(t.x, -t.y)).rgb
		+ texture(uSrc, vUv + vec2(-t.x, t.y)).rgb + texture(uSrc, vUv + vec2(t.x, t.y)).rgb;
	FragColor = vec4(s / 16.0, 1.0);
}
";

	/// <summary>Shared signed circle of confusion (Blender's thin-lens camera): radius in
	/// full-resolution pixels, negative in front of the focus plane, positive behind it.
	/// c = f/N * f * |S - D| / (D * (S - f)) on a sensor of height 2 f tan(fovY/2), so with the
	/// viewport's vertical FOV fixed the focal length and f-stop set the blur.</summary>
	const string CocGlsl = @"
uniform float uNear;
uniform float uFar;
uniform float uFocus;      // S, scene units
uniform float uFocal;      // f, scene units
uniform float uFStop;      // N; 0 disables
uniform float uTanHalfFov;
uniform float uMaxCoc;     // radius clamp, full-res pixels
float cocAt(float D, float imageHeight) {
	float S = max(uFocus, uFocal * 1.001);
	float diameter = uFocal * uFocal * (D - S) / (uFStop * D * (S - uFocal));
	float px = diameter / (2.0 * uFocal * uTanHalfFov) * imageHeight * 0.5;
	return clamp(px, -uMaxCoc, uMaxCoc);
}
float linearDepth(float d) {
	float z = d * 2.0 - 1.0;
	return 2.0 * uNear * uFar / (uFar + uNear - z * (uFar - uNear));
}
float signedCoc(float d, float imageHeight) {
	if (uFStop <= 0.0 || d >= 1.0) return d >= 1.0 && uFStop > 0.0 ? cocAt(uFar, imageHeight) : 0.0;
	return cocAt(linearDepth(d), imageHeight);
}
";


	/// <summary>Full to half resolution: averaged linear HDR colour, signed CoC in alpha. The
	/// nearest of the four depths wins when it is in the near field, so foreground edges spread.</summary>
	public const string DofPrepFs = @"#version 460 core
in vec2 vUv;
out vec4 FragColor;
uniform sampler2D uColor;
uniform sampler2D uDepth;
" + CocGlsl + @"
void main() {
	vec2 full = vec2(textureSize(uColor, 0));
	ivec2 base = ivec2(gl_FragCoord.xy) * 2;
	ivec2 hi = ivec2(full) - 1;
	vec3 c = vec3(0.0); float nearC = 0.0, sumC = 0.0;
	for (int i = 0; i < 4; i++) {
		ivec2 p = min(base + ivec2(i & 1, i >> 1), hi);
		c += min(texelFetch(uColor, p, 0).rgb, vec3(65000.0));
		float k = signedCoc(texelFetch(uDepth, p, 0).r, full.y);
		nearC = min(nearC, k); sumC += k;
	}
	FragColor = vec4(c * 0.25, nearC < -0.5 ? nearC : sumC * 0.25);
}
";

	/// <summary>Half-resolution bokeh gather. Layer 0 (far/background) gathers within the pixel's
	/// own CoC from samples whose disc reaches it and that are behind focus, so in-focus surfaces
	/// never smear outwards. Layer 1 (near field) searches the maximum radius and scatters-as-gather
	/// foreground samples over everything, including in-focus pixels, with coverage alpha from each
	/// sample's disc area. Taps lie on a polygon of uBlades sides rotated by uBladeRotation
	/// (fewer than 3 blades = circular aperture).</summary>
	public const string DofGatherFs = @"#version 460 core
in vec2 vUv;
out vec4 FragColor;
uniform sampler2D uPrep;
uniform int uLayer;
uniform float uMaxCoc;
uniform int uBlades;
uniform float uBladeRotation;
const int TAPS = 72;
float polygon(float theta) {
	if (uBlades < 3) return 1.0;
	float seg = 6.28318530 / float(uBlades);
	float a = mod(theta - uBladeRotation, seg) - seg * 0.5;
	return cos(seg * 0.5) / cos(a);
}
void main() {
	vec2 halfTexel = 1.0 / vec2(textureSize(uPrep, 0));
	vec4 centre = texture(uPrep, vUv);
	// Interleaved gradient noise rotates the spiral per pixel, trading banding for fine grain.
	float noise = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715)))) * 6.28318530;
	const float golden = 2.39996323;
	if (uLayer == 0) {
		float R = centre.a;
		if (R < 0.5) { FragColor = vec4(centre.rgb, 1.0); return; }
		vec3 sum = centre.rgb; float total = 1.0;
		for (int i = 0; i < TAPS; i++) {
			float rho = sqrt((float(i) + 0.5) / float(TAPS));
			float theta = float(i) * golden + noise;
			vec2 offset = vec2(cos(theta), sin(theta)) * rho * polygon(theta) * R; // full-res px
			vec4 s = texture(uPrep, vUv + offset * 0.5 * halfTexel);
			float w = s.a > 0.0 ? clamp(s.a - rho * R + 1.0, 0.0, 1.0) : 0.0;
			sum += s.rgb * w; total += w;
		}
		FragColor = vec4(sum / total, 1.0);
	} else {
		float R = uMaxCoc;
		vec3 sum = vec3(0.0); float alpha = 0.0, total = 0.0;
		for (int i = 0; i < TAPS; i++) {
			float rho = sqrt((float(i) + 0.5) / float(TAPS));
			float theta = float(i) * golden + noise;
			vec2 offset = vec2(cos(theta), sin(theta)) * rho * polygon(theta) * R;
			vec4 s = texture(uPrep, vUv + offset * 0.5 * halfTexel);
			float r = -s.a;
			if (r <= 0.5) continue;
			float cover = clamp(r - rho * R + 1.0, 0.0, 1.0) * smoothstep(0.5, 1.5, r);
			// Each tap stands for 1/TAPS of the search disc; its light spreads over pi r^2.
			float w = cover * (R * R) / (float(TAPS) * max(r * r, 1.0));
			sum += s.rgb * w; total += w; alpha += w;
		}
		FragColor = total > 0.0 ? vec4(sum / total, clamp(alpha, 0.0, 1.0)) : vec4(0.0);
	}
}
";

	/// <summary>Physical depth of field (half-res far/near layers over the sharp image), bloom,
	/// exposure, view transform + look, gamma, then the single sRGB encode.</summary>
	public const string Composite = @"#version 460 core
in vec2 vUv;
out vec4 FragColor;
uniform sampler2D uColor;
uniform sampler2D uBloom;
uniform sampler2D uDepth;
uniform sampler2D uDofFar;
uniform sampler2D uDofNear;
uniform float uBloomIntensity;
uniform float uBloomLevels;
uniform float uViewExposure;
uniform vec3 uWhiteBalance;
uniform float uGamma;
uniform int uViewTransform; // 0 AgX, 1 Standard
uniform int uLook;          // 0 None, 1 Punchy
" + CocGlsl + @"
const float kAgxNone[65] = float[]( 0.000000, 0.002006, 0.003338, 0.005200, 0.007322, 0.009628, 0.012177, 0.014816, 0.017746, 0.020813, 0.024178, 0.027859, 0.031683, 0.035994, 0.040497, 0.045490, 0.050808, 0.056587, 0.062933, 0.069599, 0.077199, 0.085174, 0.094092, 0.103761, 0.114190, 0.125961, 0.138279, 0.152604, 0.167749, 0.184681, 0.203332, 0.223353, 0.246247, 0.270254, 0.297748, 0.326879, 0.358704, 0.393059, 0.428989, 0.466786, 0.504722, 0.541486, 0.577209, 0.611481, 0.643631, 0.674954, 0.703241, 0.730778, 0.756074, 0.779986, 0.802581, 0.823211, 0.843214, 0.861090, 0.878373, 0.894293, 0.909218, 0.923443, 0.936353, 0.948884, 0.960250, 0.971169, 0.981391, 0.990707, 0.999572 );
const float kAgxPunchy[65] = float[]( 0.015550, 0.015710, 0.015940, 0.016095, 0.016244, 0.016470, 0.016619, 0.016834, 0.016980, 0.017194, 0.017404, 0.017608, 0.017949, 0.018474, 0.019043, 0.019832, 0.020868, 0.022159, 0.023800, 0.025752, 0.028368, 0.031514, 0.035615, 0.040724, 0.047370, 0.055874, 0.066185, 0.078461, 0.092214, 0.107438, 0.124249, 0.142634, 0.162615, 0.185106, 0.209323, 0.234994, 0.262522, 0.292388, 0.322841, 0.355228, 0.389366, 0.425549, 0.463529, 0.502024, 0.539312, 0.575732, 0.610632, 0.643480, 0.675534, 0.704452, 0.732515, 0.758525, 0.782846, 0.806161, 0.827077, 0.847291, 0.865845, 0.883214, 0.899797, 0.914715, 0.929120, 0.942397, 0.954825, 0.966747, 0.977516 );
vec3 depthOfField() {
	vec3 sharp = texture(uColor, vUv).rgb;
	if (uFStop <= 0.0) return sharp;
	float c0 = signedCoc(texture(uDepth, vUv).r, float(textureSize(uColor, 0).y));
	vec3 col = mix(sharp, texture(uDofFar, vUv).rgb, smoothstep(0.5, 1.5, c0));
	vec4 nearLayer = texture(uDofNear, vUv);
	return mix(col, nearLayer.rgb, nearLayer.a);
}
float curve(float x, int look) {
	float f = clamp(x, 0.0, 1.0) * 64.0;
	int i = min(int(f), 63);
	return look == 1 ? mix(kAgxPunchy[i], kAgxPunchy[i + 1], f - float(i)) : mix(kAgxNone[i], kAgxNone[i + 1], f - float(i));
}
vec3 agx(vec3 c) {
	const mat3 inset = mat3(0.842479062253094, 0.0423282422610123, 0.0423756549057051,
		0.0784335999999992, 0.878468636469772, 0.0784336,
		0.0792237451477643, 0.0791661274605434, 0.879142973793104);
	const mat3 outset = mat3(1.19687900512017, -0.0528968517574562, -0.0529716355144438,
		-0.0980208811401368, 1.15190312990417, -0.0980434501171241,
		-0.0990297440797205, -0.0989611768448433, 1.15107367264116);
	const float minEv = -12.47393, maxEv = 4.026069;
	vec3 v = inset * max(c, vec3(0.0));
	v = (clamp(log2(max(v, vec3(1e-10))), minEv, maxEv) - minEv) / (maxEv - minEv);
	v = vec3(curve(v.r, uLook), curve(v.g, uLook), curve(v.b, uLook));
	if (uLook == 1) {
		float l = dot(v, vec3(0.2126, 0.7152, 0.0722));
		v = l + 1.4 * (v - l);
	}
	return pow(max(outset * v, vec3(0.0)), vec3(2.2));
}
vec3 srgbEncode(vec3 c) {
	c = clamp(c, 0.0, 1.0);
	return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c));
}
void main() {
	vec3 hdr = depthOfField();
	hdr += texture(uBloom, vUv).rgb * (uBloomIntensity / uBloomLevels);
	hdr *= exp2(uViewExposure);
	// R1: white balance removes the assumed illuminant's tint from the linear image, the
	// way a camera does, before the view transform. It only moves colour, never exposure.
	hdr *= uWhiteBalance;
	vec3 display = uViewTransform == 0 ? agx(hdr) : hdr;
	vec3 encoded = srgbEncode(display);
	if (uGamma != 1.0) encoded = pow(encoded, vec3(1.0 / uGamma));
	FragColor = vec4(encoded, 1.0);
}
";
}
