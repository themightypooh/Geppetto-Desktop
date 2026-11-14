using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Effigy.Render;

namespace Effigy;

/// <summary>
/// What a material slot looks like: a colour, roughness, metalness and glow, set by script
/// (`material 2 preset=pcb_green`, `material 2 color=#1f5c32 rough=0.4`) and saved with the document.
///
/// WHY THIS EXISTS. A slot used to be a number and a .vmat path. Nothing in Effigy could read what
/// the path meant, so a turnaround with shade=material showed every hard-surface part as clay and
/// the desktop viewer guessed colours from words in the name. A look is the slot's own fact: the
/// renderers shade with it, the viewer draws with it, and export writes it as the slot's .vmat.
///
/// Colours are sRGB 0..1, as typed. <see cref="Shade"/> converts them to linear for the renderer.
/// </summary>
public sealed class SlotLook
{
	public Vec3 Color = new( 0.62f, 0.6f, 0.57f );
	public float Rough = 0.5f;
	public float Metal;
	/// <summary>Glow colour, sRGB. Black is none.</summary>
	public Vec3 Emit;
	/// <summary>The preset it started from, if any. A name only: the values above are what count.</summary>
	public string Preset;

	// --- Lane C: cartoony-to-realistic look spectrum. Every master defaults to 0 (Opacity to 1),
	// so a look that never touches them shades exactly as before, and Describe() only writes the
	// ones that moved, so an old .effigy file does not change bytes on save. ---
	/// <summary>0 = standard PBR, 1 = fully stepped cel shading.</summary>
	public float Toon;
	/// <summary>Cel lighting bands when <see cref="Toon"/> &gt; 0 (2, 3 or 4 in practice).</summary>
	public int ToonBands = 3;
	/// <summary>0..1 crisp view-angle rim highlight, stepped with the cel bands.</summary>
	public float ToonRim;
	/// <summary>0..1 curvature-ridge highlight + warm AO-cavity tint (stylized PBR).</summary>
	public float Stylized;
	/// <summary>Ink outline width in inches; 0 draws no outline.</summary>
	public float OutlineWidth;
	/// <summary>Ink outline colour, sRGB.</summary>
	public Vec3 OutlineColor = new( 0.05f, 0.05f, 0.07f );
	/// <summary>0..1 secondary dielectric lobe: wet slime, cornea, lacquer.</summary>
	public float Clearcoat;
	/// <summary>Clearcoat lobe roughness.</summary>
	public float ClearcoatRoughness = 0.05f;
	/// <summary>0..1 wrap diffuse + back-lit translucency: flesh, fins, leaves.</summary>
	public float Subsurface;
	/// <summary>Colour light picks up inside the material, sRGB.</summary>
	public Vec3 SubsurfaceColor = new( 0.85f, 0.22f, 0.15f );
	/// <summary>Thin-membrane alpha: 1 is opaque, below 1 is fin webbing, wing, glass.</summary>
	public float Opacity = 1f;
	/// <summary>Tint of shadowed / ambient light on this slot, sRGB. White is no tint:
	/// a fresh look shades exactly as before, and Describe() only writes it when moved.
	/// Renderer-side (ShadedRender ambient, desktop GLSL ambient); complex.vfx has no
	/// single shadow-tint parameter, so the exported .vmat does not carry it.</summary>
	public Vec3 ShadowTint = Vec3.One;
	/// <summary>Oil-film hue shift on grazing reflections, 0..1 (blue -> purple -> green -> gold
	/// with angle, the same film <see cref="Effigy.Render.ShadeMaterial.Iridescence"/> uses).
	/// Renderer-side (ShadedRender, desktop GLSL); no invented .vmat parameter.</summary>
	public float Iridescence;
    /// <summary>Slot-level texture maps, 2026-10-25: paths as typed in `material` (relative
    /// paths resolve against the script/document directory at shade/export time, else the
    /// working directory). Null = no map, so old files shade exactly as before. The
    /// headless renderer already consumed these fields on ShadeMaterial; SlotLook simply
    /// never carried them. A missing/unreadable map at shade time warns and falls back
    /// to the scalar, it never fails the build.</summary>
    public string AlbedoMap, RoughnessMap, MetalnessMap, NormalMap, AoMap;
    public float NormalStrength = 1f;
    public Vec2 MapScale = new Vec2(1f, 1f);
    // Wave-3 PBR (2026-10-25): generic anisotropy, sheen, live layer, SSS, specular/IOR,
    // emission/height/detail maps, per-map offset/clamp. All default to the old behaviour,
    // so a look that never touches them shades exactly as before.
    public float Aniso;
    /// <summary>Anisotropy direction rotation, degrees, around the shading normal.</summary>
    public float AnisoRotation;
    public string AnisoMap;
    public float Sheen;
    public Vec3? SheenTint;
    public float SheenRoughness = 0.5f;
    public string SheenMap;
    /// <summary>Live layer (Stage A): a second look blended over the base by LayerAmount
    /// and LayerMask. Null LayerColor = no layer. LayerRough/LayerMetal &lt; 0 = keep base.</summary>
    public Vec3? LayerColor;
    public float LayerRough = -1f, LayerMetal = -1f, LayerAmount = 0.5f;
    public bool LayerMaskAo;
    public float LayerMaskMin, LayerMaskMax = 1f;
    public string LayerBlend = "normal";
    public string TransmissionMap;
    /// <summary>Per-channel scatter distances in model units (r, g, b). Default 0.5 all channels.</summary>
    public Vec3 ScatterDist = new( 0.5f, 0.5f, 0.5f );
    public bool ScatterDistSet;
    /// <summary>Dielectric F0 directly, 0..1. Ior, when set (&gt; 0), overrides it via ((ior-1)/(ior+1))^2.</summary>
    public float Specular = 0.04f;
    public float SpecularTint;
    public float Ior;
    public string EmitMap;
    public float EmitStrength = 1f;
    public string HeightMap;
    public float HeightStrength = 1f;
    public string DetailNormalMap;
    public float DetailScale = 0.5f;
    public Vec2 MapOffset;
    public bool ClampMaps;
    /// <summary>When true (default), a smooth dielectric (rough &lt; 0.25, metal &lt; 0.5) gets an
    /// implicit 0.4 clearcoat in Shade(). Set implicitcoat=off to opt out and see only the coat you asked for.</summary>
    public bool ImplicitCoat = true;
    public bool BurleyDiffuse;

	public bool Glows => Emit.x > 0f || Emit.y > 0f || Emit.z > 0f;

	public SlotLook Clone() => new() { Color = Color, Rough = Rough, Metal = Metal, Emit = Emit, Preset = Preset, Toon = Toon, ToonBands = ToonBands, ToonRim = ToonRim, Stylized = Stylized, OutlineWidth = OutlineWidth, OutlineColor = OutlineColor, Clearcoat = Clearcoat, ClearcoatRoughness = ClearcoatRoughness, Subsurface = Subsurface, SubsurfaceColor = SubsurfaceColor, Opacity = Opacity, ShadowTint = ShadowTint, Iridescence = Iridescence , AlbedoMap = AlbedoMap, RoughnessMap = RoughnessMap, MetalnessMap = MetalnessMap, NormalMap = NormalMap, AoMap = AoMap, NormalStrength = NormalStrength, MapScale = MapScale, Aniso = Aniso, AnisoRotation = AnisoRotation, AnisoMap = AnisoMap, Sheen = Sheen, SheenTint = SheenTint, SheenRoughness = SheenRoughness, SheenMap = SheenMap, LayerColor = LayerColor, LayerRough = LayerRough, LayerMetal = LayerMetal, LayerAmount = LayerAmount, LayerMaskAo = LayerMaskAo, LayerMaskMin = LayerMaskMin, LayerMaskMax = LayerMaskMax, LayerBlend = LayerBlend, TransmissionMap = TransmissionMap, ScatterDist = ScatterDist, ScatterDistSet = ScatterDistSet, Specular = Specular, SpecularTint = SpecularTint, Ior = Ior, EmitMap = EmitMap, EmitStrength = EmitStrength, HeightMap = HeightMap, HeightStrength = HeightStrength, DetailNormalMap = DetailNormalMap, DetailScale = DetailScale, MapOffset = MapOffset, ClampMaps = ClampMaps, ImplicitCoat = ImplicitCoat, BurleyDiffuse = BurleyDiffuse };

	/// <summary>The shipped looks, by name: the roadmap Lane H set plus wave-4 skin_caucasian_sss.</summary>
	public static readonly IReadOnlyDictionary<string, SlotLook> Presets = new Dictionary<string, SlotLook>( StringComparer.OrdinalIgnoreCase )
	{
		["abs_matte"] = Make( "abs_matte", "#2b2d31", 0.55f, 0f ),
		["soft_rubber"] = Make( "soft_rubber", "#1b1b1e", 0.88f, 0f ),
		["glossy_keycap"] = Make( "glossy_keycap", "#e6e3dc", 0.16f, 0f ),
		["anodised_metal"] = Make( "anodised_metal", "#3c4d6b", 0.32f, 1f ),
		["pcb_green"] = Make( "pcb_green", "#1f5c32", 0.4f, 0f ),
		["nylon_webbing"] = Make( "nylon_webbing", "#2a2a2e", 0.92f, 0f ),
		["led_emissive"] = Make( "led_emissive", "#ff3a24", 0.2f, 0f, "#ff3a24" ),
		// Lane C: cartoony-to-realistic spectrum. Colours are starting points, not claims:
		// the look fields are what a script overrides (coat=, sss=, stylized=, toon=).
		["toon_flat"] = Make( "toon_flat", "#d8d2c8", 0.9f, 0f, toon: 1f, toonBands: 2 ),
		["toon_cel"] = Make( "toon_cel", "#5a7fa6", 0.6f, 0f, toon: 1f, toonBands: 3, toonRim: 0.35f ),
		["stylized_vinyl"] = Make( "stylized_vinyl", "#e8e0d0", 0.35f, 0f, stylized: 0.6f, clearcoat: 0.4f ),
		["stylized_handpainted"] = Make( "stylized_handpainted", "#8a9a6a", 0.85f, 0f, stylized: 1f ),
		["wet_scales"] = Make( "wet_scales", "#3f6a72", 0.22f, 0f, stylized: 0.25f, clearcoat: 0.85f, subsurface: 0.35f ),
		["fish_fin_membrane"] = Make( "fish_fin_membrane", "#c8d8d0", 0.4f, 0f, subsurface: 0.6f, opacity: 0.82f ),
		["eye_cornea"] = Make( "eye_cornea", "#20242a", 0.08f, 0f, clearcoat: 1f, clearcoatRough: 0.03f, subsurface: 0.2f ),
		["wet_mucus"] = Make( "wet_mucus", "#6a8a7a", 0.15f, 0f, clearcoat: 0.9f, subsurface: 0.3f ),
		["flesh_sss"] = Make( "flesh_sss", "#c98a7a", 0.55f, 0f, subsurface: 0.7f, subsurfaceColor: "#d94a2a" ),
		// Wave-4 SSS: a named caucasian skin look, distinct from generic flesh_sss
		// (lighter base, stronger scatter, warm red scatter colour, light oil sheen).
		["skin_caucasian_sss"] = Make( "skin_caucasian_sss", "#d9a184", 0.55f, 0f, subsurface: 0.85f, subsurfaceColor: "#e05a33", clearcoat: 0.25f, clearcoatRough: 0.35f ),
		["chitin_iridescent"] = Make( "chitin_iridescent", "#2a3a4a", 0.18f, 0.3f, stylized: 0.4f, clearcoat: 0.7f, iridescence: 0.7f ),
		// Wave-4 SSS: a named caucasian skin look, distinct from generic flesh_sss
		// (lighter base, stronger scatter, warm red scatter colour, light oil sheen).
		["skin_caucasian_sss"] = Make( "skin_caucasian_sss", "#d9a184", 0.55f, 0f, subsurface: 0.85f, subsurfaceColor: "#e05a33", clearcoat: 0.25f, clearcoatRough: 0.35f ),
		// Wave-5 SlotLook32: the roadmap Lane H named presets. Each entry is a distinct
		// parameter tuple, not a rename of an existing preset. Honest renderer notes:
		// halftone shading, true anisotropic hair lobes and glass refraction are roadmap
		// H1 renderer fields for later; these presets drive the fields that exist today.
		// Stylized and NPR (roadmap H1):
		["toon_inked"] = Make( "toon_inked", "#e8e2d6", 0.9f, 0f, toon: 1f, toonBands: 3, toonRim: 0.15f, outline: 0.06f ),
		["toon_halftone"] = Make( "toon_halftone", "#b8b0a4", 0.85f, 0f, toon: 1f, toonBands: 4, stylized: 0.3f ),
		["linework_sketch"] = Make( "linework_sketch", "#f0ece2", 0.95f, 0f, toon: 1f, toonBands: 2, outline: 0.03f ),
		["retro_ps1"] = Make( "retro_ps1", "#8a8a9a", 0.7f, 0f, stylized: 0.5f, shadowTint: "#8a90a0" ),
		// Organics, skin, eyes and creature (roadmap H1):
		["bone_ivory"] = Make( "bone_ivory", "#e8ddc4", 0.6f, 0f, subsurface: 0.25f, subsurfaceColor: "#f0e2c0", clearcoat: 0.05f ),
		["tooth_enamel"] = Make( "tooth_enamel", "#f5f2e8", 0.18f, 0f, clearcoat: 0.8f, clearcoatRough: 0.1f, subsurface: 0.15f ),
		// Hair, cloth and leather (roadmap H1):
		["hair_aniso"] = Make( "hair_aniso", "#3a2a20", 0.45f, 0f, stylized: 0.7f, clearcoat: 0.3f, clearcoatRough: 0.25f ),
		["velvet_cloth"] = Make( "velvet_cloth", "#5a2a3a", 0.95f, 0f, subsurface: 0.3f, subsurfaceColor: "#7a3a50", shadowTint: "#e0d0e0" ),
		["denim_twill"] = Make( "denim_twill", "#3a5a8a", 0.9f, 0f, stylized: 0.35f ),
		["worn_leather"] = Make( "worn_leather", "#6a4a2a", 0.65f, 0f, stylized: 0.3f, clearcoat: 0.15f, subsurface: 0.1f ),
		["satin_silk"] = Make( "satin_silk", "#d8c8e0", 0.3f, 0f, stylized: 0.4f, clearcoat: 0.6f, clearcoatRough: 0.2f, subsurface: 0.2f ),
		// Metals, glass and hard-surface (roadmap H1):
		["brushed_steel"] = Make( "brushed_steel", "#9aa0a8", 0.35f, 1f, stylized: 0.5f ),
		["gunmetal_blued"] = Make( "gunmetal_blued", "#2a3238", 0.28f, 1f, clearcoat: 0.3f ),
		["aged_brass"] = Make( "aged_brass", "#8a7a3a", 0.45f, 1f, stylized: 0.2f ),
		["rusted_iron"] = Make( "rusted_iron", "#7a3a22", 0.85f, 0.6f, stylized: 0.4f ),
		["car_lacquer"] = Make( "car_lacquer", "#14264e", 0.08f, 0f, clearcoat: 1f, clearcoatRough: 0.05f ),
		["clear_glass"] = Make( "clear_glass", "#dfe8ea", 0.05f, 0f, opacity: 0.25f, clearcoat: 0.5f ),
		// Wave-3 PBR presets: each drives a parameter that did not exist before.
		["brushed_metal"] = Make( "brushed_metal", "#9aa0a8", 0.35f, 1f, aniso: 0.8f, anisoRot: 0f ),
		["brushed_wood"] = Make( "brushed_wood", "#7a5a34", 0.5f, 0f, aniso: 0.6f ),
		["fabric_wool"] = Make( "fabric_wool", "#8a7a6a", 0.95f, 0f, sheen: 0.7f, sheenColor: "#d8c8b0", sheenRough: 0.5f ),
		["fabric_velvet"] = Make( "fabric_velvet", "#5a2a3a", 0.95f, 0f, sheen: 0.9f, sheenColor: "#a06a7a", sheenRough: 0.4f ),
		["worn_leather2"] = Make( "worn_leather2", "#6a4a2a", 0.65f, 0f, sheen: 0.25f, sheenColor: "#c8a880", sheenRough: 0.6f ),
		["skin_burley"] = Make( "skin_burley", "#d9a184", 0.55f, 0f, subsurface: 0.85f, subsurfaceColor: "#e05a33", scatterDist: "0.5,0.2,0.1" ),
		["ceramic_glaze"] = Make( "ceramic_glaze", "#e8e8f0", 0.12f, 0f, clearcoat: 1f, clearcoatRough: 0.03f, specular: 0.05f ),
		["water_fake"] = Make( "water_fake", "#2a5a7a", 0.05f, 0f, specular: 0.02f, clearcoat: 0.6f ),
		["diamond_fake"] = Make( "diamond_fake", "#f0f4f8", 0.02f, 0f, specular: 0.17f, clearcoat: 0.4f ),
	};

	static SlotLook Make( string name, string color, float rough, float metal, string emit = null,
		float toon = 0f, int toonBands = 3, float toonRim = 0f, float stylized = 0f,
		float outline = 0f, string outlineColor = null,
		float clearcoat = 0f, float clearcoatRough = 0.05f,
		float subsurface = 0f, string subsurfaceColor = null, float opacity = 1f,
		string shadowTint = null, float iridescence = 0f,
		float aniso = 0f, float anisoRot = 0f, float sheen = 0f, string sheenColor = null, float sheenRough = 0.5f,
		float specular = 0.04f, string scatterDist = null ) => new()
	{
		Preset = name,
		Color = FurMaterial.ParseHex( color, Vec3.One ),
		Rough = rough,
		Metal = metal,
		Emit = emit is null ? default : FurMaterial.ParseHex( emit, default ),
		Toon = toon, ToonBands = toonBands, ToonRim = toonRim, Stylized = stylized,
		OutlineWidth = outline,
		OutlineColor = outlineColor is null ? new Vec3( 0.05f, 0.05f, 0.07f ) : FurMaterial.ParseHex( outlineColor, new Vec3( 0.05f, 0.05f, 0.07f ) ),
		Clearcoat = clearcoat, ClearcoatRoughness = clearcoatRough,
		Subsurface = subsurface,
		SubsurfaceColor = subsurfaceColor is null ? new Vec3( 0.85f, 0.22f, 0.15f ) : FurMaterial.ParseHex( subsurfaceColor, new Vec3( 0.85f, 0.22f, 0.15f ) ),
		Opacity = opacity,
		ShadowTint = shadowTint is null ? Vec3.One : FurMaterial.ParseHex( shadowTint, Vec3.One ),
		Iridescence = iridescence,
		Aniso = aniso, AnisoRotation = anisoRot,
		Sheen = sheen, SheenTint = sheenColor is null ? null : FurMaterial.ParseHex( sheenColor, Vec3.One ),
		SheenRoughness = sheenRough,
		Specular = specular,
		ScatterDist = scatterDist is null ? new Vec3( 0.5f, 0.5f, 0.5f ) : ParseVec3( scatterDist ) ?? new Vec3( 0.5f, 0.5f, 0.5f ),
		ScatterDistSet = scatterDist is not null,
	};

	/// <summary>Named look by name, cloned so the caller owns it. The roadmap's TryPreset: the
	/// shipped mechanism was the <see cref="Presets"/> dictionary plus `preset=` in <see cref="Parse"/>.</summary>
	public static bool TryPreset( string name, out SlotLook look )
	{
		if ( !string.IsNullOrWhiteSpace( name ) && Presets.TryGetValue( name, out var p ) ) { look = p.Clone(); return true; }
		look = null;
		return false;
	}

	public static string PresetList => string.Join( ", ", Presets.Keys );

	/// <summary>The keys `material` and the document's `materiallook` line take.</summary>
	public static bool IsKey( string key ) => key?.ToLowerInvariant() is "preset" or "color" or "colour" or "rough" or "metal" or "emit" or "toon" or "toonbands" or "toon_bands" or "toonrim" or "toon_rim" or "stylized" or "outline" or "outlinecolor" or "outlinecolour" or "clearcoat" or "coat" or "clearcoatrough" or "clearcoatroughness" or "coatrough" or "subsurface" or "sss" or "subsurfacecolor" or "subsurfacecolour" or "opacity" or "shadowtint" or "shadow_tint" or "iridescence" or "albedo" or "albedomap" or "roughmap" or "roughnessmap" or "metalmap" or "metalnessmap" or "normal" or "normalmap" or "ao" or "aomap" or "normalstrength" or "mapscale" or "aniso" or "anisotropy" or "anisorot" or "anisorotation" or "anisomap" or "sheen" or "sheencolor" or "sheencolour" or "sheenrough" or "sheenroughness" or "sheenmap" or "layercolor" or "layercolour" or "layerrough" or "layermetal" or "layermask" or "layeramount" or "layerblend" or "transmissionmap" or "thicknessmap" or "scatterdist" or "scatterdistance" or "specular" or "speculartint" or "ior" or "emitmap" or "emitstrength" or "heightmap" or "heightstrength" or "detailnormal" or "detailnormalmap" or "detailscale" or "mapoffset" or "clampmaps" or "implicitcoat" or "burleydiffuse";

	/// <summary>
	/// Apply key=value pairs over <paramref name="start"/> (or a fresh look). preset= goes first,
	/// whatever order it was typed in, so `color=` after it tints the preset rather than being lost.
	/// Returns null and sets <paramref name="error"/> on a bad value.
	/// </summary>
	static string BlankToNull(string v) => string.IsNullOrWhiteSpace(v) || v is "0" or "none" or "off" ? null : v.Trim();

	static Vec3? ParseVec3( string v )
	{
		var p = (v ?? "").Split( ',', StringSplitOptions.RemoveEmptyEntries );
		if ( p.Length != 3 ) return null;
		if ( !float.TryParse( p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a ) ||
			 !float.TryParse( p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b ) ||
			 !float.TryParse( p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var c ) ) return null;
		return new Vec3( a, b, c );
	}

	static bool ParseBool( string v, out bool b )
	{
		if ( v is "1" or "on" or "true" or "yes" ) { b = true; return true; }
		if ( v is "0" or "off" or "false" or "no" ) { b = false; return true; }
		b = false; return false;
	}

    public static SlotLook Parse( IReadOnlyDictionary<string, string> pairs, SlotLook start, out string error )
	{
		error = null;
		var look = start?.Clone() ?? new SlotLook();

		var preset = pairs.FirstOrDefault( kv => kv.Key.Equals( "preset", StringComparison.OrdinalIgnoreCase ) ).Value;
		if ( preset is not null )
		{
			if ( !Presets.TryGetValue( preset, out var p ) )
			{
				error = $"preset={preset} is not a look. Use one of: {PresetList}";
				return null;
			}

			look = p.Clone();
		}

		foreach ( var (k, value) in pairs )
		{
			var key = k.ToLowerInvariant();
			switch ( key )
			{
				case "color" or "colour":
					var c = FurMaterial.ParseHex( value, new Vec3( -1, -1, -1 ) );
					if ( c.x < 0f ) { error = $"{key}={value}: give a #rrggbb colour"; return null; }
					look.Color = c;
					break;
				case "emit":
					if ( value is "0" or "none" or "off" ) { look.Emit = default; break; }
					var e = FurMaterial.ParseHex( value, new Vec3( -1, -1, -1 ) );
					if ( e.x < 0f ) { error = $"emit={value}: give a #rrggbb glow colour, or emit=none"; return null; }
					look.Emit = e;
					break;
				case "rough" or "metal":
					if ( !float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f ) || !float.IsFinite( f ) || f < 0f || f > 1f )
					{
						error = $"{key}={value}: give a number from 0 to 1";
						return null;
					}

					if ( key == "rough" ) look.Rough = f; else look.Metal = f;
					break;
				case "toon" or "toonrim" or "toon_rim" or "stylized" or "clearcoat" or "coat" or "clearcoatrough" or "clearcoatroughness" or "coatrough" or "subsurface" or "sss" or "opacity" or "iridescence" or "aniso" or "anisotropy" or "sheen" or "sheenrough" or "sheenroughness" or "specular" or "speculartint" or "layeramount":
					if ( !float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out var g ) || g < 0f || g > 1f )
					{
						error = $"{key}={value}: give a number from 0 to 1";
						return null;
					}
					switch ( key )
					{
						case "toon": look.Toon = g; break;
						case "toonrim" or "toon_rim": look.ToonRim = g; break;
						case "stylized": look.Stylized = g; break;
						case "clearcoat" or "coat": look.Clearcoat = g; break;
						case "clearcoatrough" or "clearcoatroughness" or "coatrough": look.ClearcoatRoughness = g; break;
						case "subsurface" or "sss": look.Subsurface = g; break;
						case "opacity": look.Opacity = g; break;
						case "iridescence": look.Iridescence = g; break;
						case "aniso" or "anisotropy": look.Aniso = g; break;
						case "sheen": look.Sheen = g; break;
						case "sheenrough" or "sheenroughness": look.SheenRoughness = g; break;
						case "specular": look.Specular = g; break;
						case "speculartint": look.SpecularTint = g; break;
						case "layeramount": look.LayerAmount = g; break;
					}
					break;
				case "albedo" or "albedomap": look.AlbedoMap = BlankToNull(value); break;
                case "roughmap" or "roughnessmap": look.RoughnessMap = BlankToNull(value); break;
                case "metalmap" or "metalnessmap": look.MetalnessMap = BlankToNull(value); break;
                case "normal" or "normalmap": look.NormalMap = BlankToNull(value); break;
                case "ao" or "aomap": look.AoMap = BlankToNull(value); break;
                case "normalstrength":
                    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ns) || ns < 0f || ns > 8f)
                    {
                        error = $"normalstrength={value}: give a number from 0 to 8 (0 is flat, 1 as painted, 2 twice as deep)";
                        return null;
                    }
                    look.NormalStrength = ns;
                    break;
                case "mapscale":
                    var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var mx) || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var my) || mx <= 0f || my <= 0f)
                    {
                        error = $"mapscale={value}: give two numbers above 0, e.g. mapscale=2,2";
                        return null;
                    }
                    look.MapScale = new Vec2(mx, my);
                    break;
				case "anisorot" or "anisorotation":
					if ( !float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ar ) || ar < -360f || ar > 360f ) { error = $"anisorot={value}: give degrees from -360 to 360"; return null; }
					look.AnisoRotation = ar;
					break;
				case "ior":
					if ( !float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ior ) || ior < 1f || ior > 3f ) { error = $"ior={value}: give a number from 1 to 3"; return null; }
					look.Ior = ior;
					break;
				case "emitstrength":
					if ( !float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out var es ) || es < 0f || es > 10f ) { error = $"emitstrength={value}: give a number from 0 to 10"; return null; }
					look.EmitStrength = es;
					break;
				case "heightstrength":
					if ( !float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hs ) || hs < 0f || hs > 8f ) { error = $"heightstrength={value}: give a number from 0 to 8"; return null; }
					look.HeightStrength = hs;
					break;
				case "detailscale":
					if ( !float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ds ) || ds < 0f || ds > 2f ) { error = $"detailscale={value}: give a number from 0 to 2"; return null; }
					look.DetailScale = ds;
					break;
				case "layerrough" or "layermetal":
					if ( value is "none" or "off" ) { if ( key == "layerrough" ) look.LayerRough = -1f; else look.LayerMetal = -1f; break; }
					if ( !float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lv ) || lv < 0f || lv > 1f ) { error = $"{key}={value}: give a number from 0 to 1, or none"; return null; }
					if ( key == "layerrough" ) look.LayerRough = lv; else look.LayerMetal = lv;
					break;
				case "layerblend":
					if ( value.ToLowerInvariant() is not ("normal" or "multiply" or "overlay") ) { error = $"layerblend={value}: use normal, multiply, or overlay"; return null; }
					look.LayerBlend = value.ToLowerInvariant();
					break;
				case "layermask":
					if ( value.Equals( "uniform", StringComparison.OrdinalIgnoreCase ) ) { look.LayerMaskAo = false; break; }
					if ( value.Equals( "ao", StringComparison.OrdinalIgnoreCase ) ) { look.LayerMaskAo = true; look.LayerMaskMin = 0f; look.LayerMaskMax = 1f; break; }
					if ( value.StartsWith( "ao:", StringComparison.OrdinalIgnoreCase ) )
					{
						var mp = value[3..].Split( ',', StringSplitOptions.RemoveEmptyEntries );
						if ( mp.Length != 2 || !float.TryParse( mp[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var mn ) || !float.TryParse( mp[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var mx2 ) || mn < 0f || mx2 > 1f || mn >= mx2 ) { error = $"layermask={value}: use uniform, ao, or ao:min,max (0..1, min < max)"; return null; }
						look.LayerMaskAo = true; look.LayerMaskMin = mn; look.LayerMaskMax = mx2;
						break;
					}
					error = $"layermask={value}: use uniform, ao, or ao:min,max"; return null;
				case "scatterdist" or "scatterdistance":
					var sv = ParseVec3( value );
					if ( sv is null || sv.Value.x <= 0f || sv.Value.y <= 0f || sv.Value.z <= 0f || sv.Value.x > 5f || sv.Value.y > 5f || sv.Value.z > 5f ) { error = $"scatterdist={value}: give three distances from 0 to 5, e.g. scatterdist=0.5,0.2,0.1"; return null; }
					look.ScatterDist = sv.Value; look.ScatterDistSet = true;
					break;
				case "mapoffset":
					var op = value.Split( ',', StringSplitOptions.RemoveEmptyEntries );
					if ( op.Length != 2 || !float.TryParse( op[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ox ) || !float.TryParse( op[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var oy ) ) { error = $"mapoffset={value}: give two numbers, e.g. mapoffset=0.25,0"; return null; }
					look.MapOffset = new Vec2( ox, oy );
					break;
				case "clampmaps" or "implicitcoat" or "burleydiffuse":
					if ( !ParseBool( value, out var bv ) ) { error = $"{key}={value}: use on or off"; return null; }
					if ( key == "clampmaps" ) look.ClampMaps = bv; else if ( key == "implicitcoat" ) look.ImplicitCoat = bv; else look.BurleyDiffuse = bv;
					break;
				case "anisomap": look.AnisoMap = BlankToNull( value ); break;
				case "sheenmap": look.SheenMap = BlankToNull( value ); break;
				case "transmissionmap" or "thicknessmap": look.TransmissionMap = BlankToNull( value ); break;
				case "emitmap": look.EmitMap = BlankToNull( value ); break;
				case "heightmap": look.HeightMap = BlankToNull( value ); break;
				case "detailnormal" or "detailnormalmap": look.DetailNormalMap = BlankToNull( value ); break;
				case "toonbands" or "toon_bands":
					if ( !int.TryParse( value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b ) || b < 2 || b > 8 )
					{
						error = $"{key}={value}: give a whole number of cel bands from 2 to 8";
						return null;
					}
					look.ToonBands = b;
					break;
				case "outline":
					if ( !float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out var w ) || !float.IsFinite( w ) || w < 0f )
					{
						error = $"{key}={value}: give an outline width in inches, 0 or more";
						return null;
					}
					look.OutlineWidth = w;
					break;
				case "outlinecolor" or "outlinecolour" or "subsurfacecolor" or "subsurfacecolour" or "shadowtint" or "shadow_tint" or "sheencolor" or "sheencolour" or "layercolor" or "layercolour":
					var hc = FurMaterial.ParseHex( value, new Vec3( -1, -1, -1 ) );
					if ( hc.x < 0f ) { error = $"{key}={value}: give a #rrggbb colour"; return null; }
					if ( key.StartsWith( "outline" ) ) look.OutlineColor = hc;
					else if ( key.StartsWith( "shadow" ) ) look.ShadowTint = hc;
					else if ( key.StartsWith( "sheen" ) ) look.SheenTint = hc;
					else if ( key.StartsWith( "layer" ) ) look.LayerColor = hc;
					else look.SubsurfaceColor = hc;
					break;
			}
		}

		return look;
	}

	/// <summary>The document form: `color=#rrggbb rough=0.5 metal=0 [emit=#rrggbb] [preset=name]`.</summary>
	public string Describe()
	{
		var s = $"color={Hex( Color )} rough={N( Rough )} metal={N( Metal )}";
		if ( Glows ) s += $" emit={Hex( Emit )}";
		if ( Toon > 0f )
		{
			s += $" toon={N( Toon )}";
			if ( ToonBands != 3 ) s += $" toonbands={ToonBands}";
			if ( ToonRim > 0f ) s += $" toonrim={N( ToonRim )}";
		}
		if ( Stylized > 0f ) s += $" stylized={N( Stylized )}";
		if ( OutlineWidth > 0f )
		{
			s += $" outline={N( OutlineWidth )}";
			if ( OutlineColor.x != 0.05f || OutlineColor.y != 0.05f || OutlineColor.z != 0.07f ) s += $" outlinecolor={Hex( OutlineColor )}";
		}
		if ( Clearcoat > 0f )
		{
			s += $" clearcoat={N( Clearcoat )}";
			if ( ClearcoatRoughness != 0.05f ) s += $" clearcoatrough={N( ClearcoatRoughness )}";
		}
		if ( Subsurface > 0f )
		{
			s += $" subsurface={N( Subsurface )}";
			if ( SubsurfaceColor.x != 0.85f || SubsurfaceColor.y != 0.22f || SubsurfaceColor.z != 0.15f ) s += $" subsurfacecolor={Hex( SubsurfaceColor )}";
		}
		if ( Opacity < 1f ) s += $" opacity={N( Opacity )}";
		if ( ShadowTint.x != 1f || ShadowTint.y != 1f || ShadowTint.z != 1f ) s += $" shadowtint={Hex( ShadowTint )}";
		if ( Iridescence > 0f ) s += $" iridescence={N( Iridescence )}";
		if ( Aniso > 0f ) { s += $" aniso={N( Aniso )}"; if ( AnisoRotation != 0f ) s += $" anisorot={N( AnisoRotation )}"; }
		if ( !string.IsNullOrEmpty( AnisoMap ) ) s += $" anisomap={AnisoMap}";
		if ( Sheen > 0f ) { s += $" sheen={N( Sheen )}"; if ( SheenTint.HasValue ) s += $" sheencolor={Hex( SheenTint.Value )}"; if ( SheenRoughness != 0.5f ) s += $" sheenrough={N( SheenRoughness )}"; }
		if ( !string.IsNullOrEmpty( SheenMap ) ) s += $" sheenmap={SheenMap}";
		if ( LayerColor.HasValue ) { s += $" layercolor={Hex( LayerColor.Value )}"; if ( LayerRough >= 0f ) s += $" layerrough={N( LayerRough )}"; if ( LayerMetal >= 0f ) s += $" layermetal={N( LayerMetal )}"; if ( LayerAmount != 0.5f ) s += $" layeramount={N( LayerAmount )}"; if ( LayerMaskAo ) s += $" layermask=ao:{N( LayerMaskMin )},{N( LayerMaskMax )}"; if ( LayerBlend != "normal" ) s += $" layerblend={LayerBlend}"; }
		if ( !string.IsNullOrEmpty( TransmissionMap ) ) s += $" transmissionmap={TransmissionMap}";
		if ( ScatterDistSet ) s += $" scatterdist={N( ScatterDist.x )},{N( ScatterDist.y )},{N( ScatterDist.z )}";
		if ( Ior > 0f ) s += $" ior={N( Ior )}"; else if ( Specular != 0.04f ) s += $" specular={N( Specular )}";
		if ( SpecularTint > 0f ) s += $" speculartint={N( SpecularTint )}";
		if ( !string.IsNullOrEmpty( EmitMap ) ) s += $" emitmap={EmitMap}";
		if ( EmitStrength != 1f ) s += $" emitstrength={N( EmitStrength )}";
		if ( !string.IsNullOrEmpty( HeightMap ) ) s += $" heightmap={HeightMap}";
		if ( HeightStrength != 1f ) s += $" heightstrength={N( HeightStrength )}";
		if ( !string.IsNullOrEmpty( DetailNormalMap ) ) s += $" detailnormal={DetailNormalMap}";
		if ( DetailScale != 0.5f ) s += $" detailscale={N( DetailScale )}";
		if ( MapOffset.x != 0f || MapOffset.y != 0f ) s += $" mapoffset={N( MapOffset.x )},{N( MapOffset.y )}";
		if ( ClampMaps ) s += " clampmaps=on";
		if ( !ImplicitCoat ) s += " implicitcoat=off";
		if ( BurleyDiffuse ) s += " burleydiffuse=on";
		if (!string.IsNullOrEmpty(AlbedoMap)) s += $" albedo={AlbedoMap}";
        if (!string.IsNullOrEmpty(RoughnessMap)) s += $" roughmap={RoughnessMap}";
        if (!string.IsNullOrEmpty(MetalnessMap)) s += $" metalmap={MetalnessMap}";
        if (!string.IsNullOrEmpty(NormalMap)) s += $" normal={NormalMap}";
        if (!string.IsNullOrEmpty(AoMap)) s += $" ao={AoMap}";
        if (NormalStrength != 1f) s += $" normalstrength={N(NormalStrength)}";
        if (MapScale.x != 1f || MapScale.y != 1f) s += $" mapscale={N(MapScale.x)},{N(MapScale.y)}";
        if (!string.IsNullOrEmpty( Preset ) ) s += $" preset={Preset}";
		return s;
	}

	/// <summary>Read <see cref="Describe"/>'s form back. Preset is kept as a name; the values win.</summary>
	public static SlotLook FromLine( string text )
	{
		var pairs = new Dictionary<string, string>();
		string preset = null;

		foreach ( var part in (text ?? "").Split( ' ', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var eq = part.IndexOf( '=' );
			if ( eq <= 0 ) continue;
			var key = part[..eq].ToLowerInvariant();
			if ( key == "preset" ) preset = part[(eq + 1)..];
			else if ( IsKey( key ) ) pairs[key] = part[(eq + 1)..];
		}

		var look = Parse( pairs, null, out _ ) ?? new SlotLook();
		look.Preset = preset;
		return look;
	}

	/// <summary>The renderer's material: linear colour, no vertex colours, glow as light.</summary>
	public ShadeMaterial Shade() => Shade(null, null);

    /// <summary>The renderer's material, with slot maps loaded when set. Map paths
    /// resolve against baseDir (the script/document directory) first, then the
    /// working directory. A map that cannot be loaded warns (via warn, when given)
    /// and falls back to the scalar, never failing the build. All maps are
    /// resampled to one square size, because ShadeMaterial.MapSize is a single
    /// square size shared by every map; PNG rows are used as-is (row 0 = map row 0).
    /// When an albedo map is set, MapTint stays white so the map's own colours
    /// show; the slot colour remains the scalar fallback when no map is set.</summary>
    public ShadeMaterial Shade(string baseDir, System.Action<string> warn)
    {
        var m = new ShadeMaterial
        {
            Albedo = Linear( Color ),
            UseVertexColours = false,
            Roughness = Math.Clamp( Rough, 0.03f, 1f ),
            Metallic = Metal,
            Emission = Linear( Emit ) * 2f,
            Coat = ImplicitCoat ? MathF.Max( Clearcoat, Rough < 0.25f && Metal < 0.5f ? 0.4f : 0f ) : Clearcoat,
            CoatRoughness = Clearcoat > 0f ? Math.Clamp( ClearcoatRoughness, 0.01f, 1f ) : 0.04f,
            Subsurface = Subsurface,
            SubsurfaceTint = Subsurface > 0f ? Linear( SubsurfaceColor ) : null,
            Iridescence = Iridescence,
            ShadowTint = ShadowTint,
            Toon = Toon,
            ToonBands = ToonBands,
            ToonRim = ToonRim,
            Stylized = Stylized,
            Opacity = Opacity,
            NormalStrength = NormalStrength,
            MapScale = MapScale,
            MapOffset = MapOffset,
            ClampMaps = ClampMaps,
            Specular = Ior > 0f ? (Ior - 1f) * (Ior - 1f) / ((Ior + 1f) * (Ior + 1f)) : Specular,
            SpecularTint = SpecularTint,
            Ior = Ior,
            Aniso = Aniso,
            AnisoRotation = AnisoRotation,
            Sheen = Sheen,
            SheenTint = SheenTint.HasValue ? Linear( SheenTint.Value ) : null,
            SheenRoughness = SheenRoughness,
            LayerColor = LayerColor.HasValue ? Linear( LayerColor.Value ) : null,
            LayerRough = LayerRough,
            LayerMetal = LayerMetal,
            LayerAmount = LayerAmount,
            LayerMaskAo = LayerMaskAo,
            LayerMaskMin = LayerMaskMin,
            LayerMaskMax = LayerMaskMax,
            LayerBlend = LayerBlend,
            ScatterDist = ScatterDist,
            ScatterDistance = ScatterDist.x,
            EmissionStrength = EmitStrength,
            HeightStrength = HeightStrength,
            DetailScale = DetailScale,
            BurleyDiffuse = BurleyDiffuse,
        };
        var loaded = new System.Collections.Generic.List<(string Kind, byte[] Pixels, int Size)>();
        // Bilinear sample of a packed RGB int array (row-major, w x h), coordinates in texels.
        static int Bilinear( int[] rgb, byte[] alpha, int w, int h, float fx, float fy )
        {
            fx = Math.Clamp( fx, 0f, w - 1.001f ); fy = Math.Clamp( fy, 0f, h - 1.001f );
            var x0 = (int)fx; var y0 = (int)fy; var tx = fx - x0; var ty = fy - y0;
            var x1 = Math.Min( x0 + 1, w - 1 ); var y1 = Math.Min( y0 + 1, h - 1 );
            int At( int x, int y ) => rgb[y * w + x];
            var c00 = At( x0, y0 ); var c10 = At( x1, y0 ); var c01 = At( x0, y1 ); var c11 = At( x1, y1 );
            float LerpC( int sh ) { var v00 = (c00 >> sh) & 255; var v10 = (c10 >> sh) & 255; var v01 = (c01 >> sh) & 255; var v11 = (c11 >> sh) & 255; return (v00 * (1 - tx) + v10 * tx) * (1 - ty) + (v01 * (1 - tx) + v11 * tx) * ty; }
            var a00 = alpha[y0 * w + x0]; var a10 = alpha[y0 * w + x1]; var a01 = alpha[y1 * w + x0]; var a11 = alpha[y1 * w + x1];
            var a = (a00 * (1 - tx) + a10 * tx) * (1 - ty) + (a01 * (1 - tx) + a11 * tx) * ty;
            return ((int)Math.Round( LerpC( 16 ) ) << 16) | ((int)Math.Round( LerpC( 8 ) ) << 8) | (int)Math.Round( LerpC( 0 ) ) | ((int)Math.Round( a ) << 24);
        }
        // Deterministic value-noise FBM, 256x256, for procedural map paths like noise:4,4,0
        // (scale, octaves, seed). Returns packed RGBA ints; grey, alpha 255.
        static int[] NoiseMap( float scale, int octaves, int seed )
        {
            const int S = 256;
            var px = new int[S * S];
            static float Hash( int x, int y, int seed ) { var h = x * 374761393 + y * 668265263 + seed * 974634211; h = (h ^ (h >> 13)) * 1274126177; return ((h ^ (h >> 16)) & 0xffffff) / 16777216f; }
            float Value( int x, int y, int f ) { var fx = x / (float)f; var fy = y / (float)f; var x0 = (int)MathF.Floor( fx ); var y0 = (int)MathF.Floor( fy ); var tx = fx - x0; var ty = fy - y0; var sx = tx * tx * (3 - 2 * tx); var sy = ty * ty * (3 - 2 * ty); var v00 = Hash( x0, y0, seed ); var v10 = Hash( x0 + 1, y0, seed ); var v01 = Hash( x0, y0 + 1, seed ); var v11 = Hash( x0 + 1, y0 + 1, seed ); return (v00 * (1 - sx) + v10 * sx) * (1 - sy) + (v01 * (1 - sx) + v11 * sx) * sy; }
            for ( var y = 0; y < S; y++ ) for ( var x = 0; x < S; x++ )
            {
                float amp = 1f, tot = 0f, sum = 0f; var freq = Math.Max( 1f, scale );
                for ( var o = 0; o < Math.Clamp( octaves, 1, 8 ); o++ ) { sum += Value( x, y, Math.Max( 1, (int)freq ) ) * amp; tot += amp; amp *= 0.5f; freq *= 2f; }
                var v = (int)Math.Clamp( sum / tot * 255f, 0f, 255f );
                px[y * S + x] = (v << 16) | (v << 8) | v | (255 << 24);
            }
            return px;
        }
        void TryLoad(string kind, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if ( path.StartsWith( "noise:", StringComparison.OrdinalIgnoreCase ) )
                {
                    var np = path[6..].Split( ',', StringSplitOptions.RemoveEmptyEntries );
                    float.TryParse( np.Length > 0 ? np[0] : "4", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var nscale ); if ( nscale <= 0f ) nscale = 4f;
                    int.TryParse( np.Length > 1 ? np[1] : "4", out var noct ); if ( noct <= 0 ) noct = 4;
                    int.TryParse( np.Length > 2 ? np[2] : "0", out var nseed );
                    var npx = NoiseMap( nscale, noct, nseed );
                    var outp = new byte[256 * 256 * 4];
                    for ( var i = 0; i < npx.Length; i++ ) { var c = npx[i]; outp[i * 4] = (byte)(c >> 16 & 255); outp[i * 4 + 1] = (byte)(c >> 8 & 255); outp[i * 4 + 2] = (byte)(c & 255); outp[i * 4 + 3] = 255; }
                    loaded.Add( (kind, outp, 256) );
                    return;
                }
                var full = System.IO.Path.IsPathRooted(path) ? path
                    : !string.IsNullOrWhiteSpace(baseDir) && System.IO.File.Exists(System.IO.Path.Combine(baseDir, path)) ? System.IO.Path.Combine(baseDir, path)
                    : path;
                var (w, h, rgb, alpha) = Raster.LoadPng(full);
                if (w <= 0 || h <= 0) throw new System.IO.InvalidDataException("empty image");
                var side = Math.Max(w, h);
                var px = new byte[side * side * 4];
                for (var y = 0; y < side; y++)
                    for (var x = 0; x < side; x++)
                    {
                        var c = Bilinear( rgb, alpha, w, h, (x + 0.5f) * w / side - 0.5f, (y + 0.5f) * h / side - 0.5f );
                        var o = (y * side + x) * 4;
                        px[o] = (byte)(c >> 16 & 255); px[o + 1] = (byte)(c >> 8 & 255); px[o + 2] = (byte)(c & 255); px[o + 3] = (byte)(c >> 24 & 255);
                    }
                loaded.Add((kind, px, side));
            }
            catch (System.Exception e) { warn?.Invoke($"slot map {kind}={path} could not be loaded ({e.Message}); shading with the scalar instead"); }
        }
        TryLoad("albedo", AlbedoMap);
        TryLoad("roughness", RoughnessMap);
        TryLoad("metalness", MetalnessMap);
        TryLoad("normal", NormalMap);
        TryLoad("ao", AoMap);
        TryLoad("aniso", AnisoMap);
        TryLoad("sheen", SheenMap);
        TryLoad("emission", EmitMap);
        TryLoad("transmission", TransmissionMap);
        TryLoad("height", HeightMap);
        TryLoad("detailnormal", DetailNormalMap);
        if (loaded.Count > 0)
        {
            // Largest map wins (was: first-loaded map's size), so a small map never downsamples a big one.
            var size = 0; foreach ( var (_, _, sz) in loaded ) if ( sz > size ) size = sz;
            byte[] Resize(byte[] px, int from)
            {
                if (from == size) return px;
                var outp = new byte[size * size * 4];
                for (var y = 0; y < size; y++)
                    for (var x = 0; x < size; x++)
                    {
                        var sx = (x + 0.5f) * from / size - 0.5f; var sy = (y + 0.5f) * from / size - 0.5f;
                        var x0 = Math.Clamp( (int)MathF.Floor( sx ), 0, from - 1 ); var y0 = Math.Clamp( (int)MathF.Floor( sy ), 0, from - 1 );
                        var x1 = Math.Min( x0 + 1, from - 1 ); var y1 = Math.Min( y0 + 1, from - 1 );
                        var tx = Math.Clamp( sx - x0, 0f, 1f ); var ty = Math.Clamp( sy - y0, 0f, 1f );
                        for ( var ch = 0; ch < 4; ch++ )
                        {
                            var v00 = px[(y0 * from + x0) * 4 + ch]; var v10 = px[(y0 * from + x1) * 4 + ch]; var v01 = px[(y1 * from + x0) * 4 + ch]; var v11 = px[(y1 * from + x1) * 4 + ch];
                            outp[(y * size + x) * 4 + ch] = (byte)Math.Round( (v00 * (1 - tx) + v10 * tx) * (1 - ty) + (v01 * (1 - tx) + v11 * tx) * ty );
                        }
                    }
                return outp;
            }
            foreach (var (kind, px, from) in loaded)
            {
                var map = Resize(px, from);
                switch (kind)
                {
                    case "albedo": m.AlbedoMap = map; m.MapTint = Vec3.One; break;
                    case "roughness": m.RoughnessMap = map; break;
                    case "metalness": m.MetallicMap = map; break;
                    case "normal": m.NormalMap = map; break;
                    case "ao": m.AoMap = map; break;
                    case "aniso": m.AnisoMap = map; break;
                    case "sheen": m.SheenMap = map; break;
                    case "emission": m.EmissionMap = map; break;
                    case "transmission": m.TransmissionMap = map; break;
                    case "height": m.HeightMap = map; break;
                    case "detailnormal": m.DetailNormalMap = map; break;
                }
            }
            m.MapSize = size;
        }
        return m;
    }

	/// <summary>The slot as an s&amp;box complex.vfx material: tint, metalness, roughness, glow.</summary>
	public string Vmat()
	{
		// Export parity: complex.vfx has no parameters for these, so they are headless-only
		// and are listed here rather than silently dropped. Everything else below maps to a real parameter.
		var headless = new System.Collections.Generic.List<string>();
		if ( Toon > 0f || Stylized > 0f || Iridescence > 0f || ShadowTint.x != 1f || ShadowTint.y != 1f || ShadowTint.z != 1f || OutlineWidth > 0f ) headless.Add( "toon/stylized/iridescence/shadowtint/outline" );
		if ( Aniso > 0f || !string.IsNullOrEmpty( AnisoMap ) ) headless.Add( "aniso" );
		if ( Sheen > 0f || !string.IsNullOrEmpty( SheenMap ) ) headless.Add( "sheen" );
		if ( LayerColor.HasValue ) headless.Add( "layer (live blend; bake it to maps for export)" );
		if ( !string.IsNullOrEmpty( TransmissionMap ) || ScatterDistSet ) headless.Add( "transmission/scatterdist (subsurface scale+colour below are the exported part)" );
		if ( SpecularTint > 0f || Ior > 0f ) headless.Add( "speculartint/ior (F0 itself is not a complex.vfx parameter)" );
		if ( !string.IsNullOrEmpty( HeightMap ) || !string.IsNullOrEmpty( DetailNormalMap ) ) headless.Add( "height/detailnormal (bake into the normal map for export)" );
		if ( BurleyDiffuse ) headless.Add( "burleydiffuse" );
		var s = "// THIS FILE IS AUTO-GENERATED from an Effigy slot look. Edit the look (`material`), not this file.\n" +
			(headless.Count > 0 ? $"// Headless-only look fields (no complex.vfx equivalent, see the look): {string.Join( ", ", headless )}\n" : "") +
			"Layer0\n{\n" +
			"\tshader \"complex.vfx\"\n" +
			"\tTextureColor \"[1.000000 1.000000 1.000000 1.000000]\"\n" +
			"\tTextureNormal \"materials/default/default_normal.tga\"\n" +
			"\tTextureRoughness \"[1.000000 1.000000 1.000000 1.000000]\"\n" +
			"\tTextureAmbientOcclusion \"materials/default/default_ao.tga\"\n" +
			$"\tg_vColorTint \"[{N6( Color.x )} {N6( Color.y )} {N6( Color.z )} 0.000000]\"\n" +
			"\tg_flModelTintAmount \"1.000\"\n" +
			$"\tg_flMetalness \"{N( Math.Clamp( Metal, 0f, 1f ) )}\"\n" +
			$"\tg_flRoughnessScaleFactor \"{N( Math.Clamp( Rough, 0.03f, 1f ) )}\"\n";

		if ( !string.IsNullOrEmpty( EmitMap ) && !Glows )
		{
			// Emission map has no per-texel complex.vfx parameter; the in-game approximation is
			// self-illumination at the map strength (white tint), while the headless renderer uses the map itself.
			s += "\tF_SELF_ILLUM 1\n" +
				"\tg_flSelfIllumAlbedoFactor \"1.000\"\n" +
				$"\tg_flSelfIllumBrightness \"{N( 2f * EmitStrength )}\"\n" +
				"\tg_vSelfIllumTint \"[1.000000 1.000000 1.000000 0.000000]\"\n";
		}
		if ( Glows )
		{
			var peak = MathF.Max( Emit.x, MathF.Max( Emit.y, Emit.z ) );
			s += "\tF_SELF_ILLUM 1\n" +
				"\tTextureSelfIllumMask \"[1.000000 1.000000 1.000000 1.000000]\"\n" +
				"\tg_flSelfIllumAlbedoFactor \"1.000\"\n" +
				$"\tg_flSelfIllumBrightness \"{N( 2f * peak )}\"\n" +
				$"\tg_vSelfIllumTint \"[{N6( Emit.x / peak )} {N6( Emit.y / peak )} {N6( Emit.z / peak )} 0.000000]\"\n";
		}

		if (!string.IsNullOrEmpty(AlbedoMap)) s += $"\tTextureColor \"{AlbedoMap}\"\n";
		if (!string.IsNullOrEmpty(NormalMap)) s += $"\tTextureNormal \"{NormalMap}\"\n";
		if (!string.IsNullOrEmpty(RoughnessMap)) s += $"\tTextureRoughness \"{RoughnessMap}\"\n";
		if (!string.IsNullOrEmpty(MetalnessMap)) s += $"\tTextureMetalness \"{MetalnessMap}\"\n";
		if (!string.IsNullOrEmpty(AoMap)) s += $"\tTextureAmbientOcclusion \"{AoMap}\"\n";
		if (!string.IsNullOrEmpty(EmitMap)) s += $"\t// Emission map (headless): \"{EmitMap}\"\n";
        s += VmdlMaterials.LookParameters( this );

		return s + "}\n";
	}

	/// <summary>True when the file at this path is one <see cref="Vmat"/> wrote, so export may replace it.</summary>
	public static bool IsOurs( string vmatText ) => vmatText?.StartsWith( "// THIS FILE IS AUTO-GENERATED from an Effigy slot look" ) == true;

	/// <summary>
	/// Write each looked slot's .vmat under the assets root, at the path the slot is named. A file
	/// there that this did not write is someone's hand-made material and is left alone; a slot with
	/// a look but no .vmat path is reported, since nothing would load what it wrote.
	/// </summary>
	public static void WriteVmats( PartStudio studio, string assetsRoot, List<string> log, List<string> written )
	{
		foreach ( var (slot, look) in studio.MaterialLooks.OrderBy( kv => kv.Key ) )
		{
			if ( look is null ) continue;
			var name = studio.NameForSlot( slot );

			if ( !name.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase ) )
			{
				log?.Add( $"export: slot {slot} has no explicit .vmat path; using a model-local look sidecar" );
				continue;
			}

			var root = System.IO.Path.GetFullPath( assetsRoot ?? "." );
			var path = System.IO.Path.GetFullPath( System.IO.Path.Combine( root, name.Replace( '\\', '/' ) ) );
			var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			if ( !path.StartsWith( System.IO.Path.TrimEndingDirectorySeparator( root ) + System.IO.Path.DirectorySeparatorChar, comparison ) )
			{
				log?.Add( $"export: refused slot {slot} material outside the assets root: {name}" );
				continue;
			}

			if ( System.IO.File.Exists( path ) && !IsOurs( System.IO.File.ReadAllText( path ) ) )
			{
				log?.Add( $"export: kept {name}: it was not written from a slot look" );
				continue;
			}

			// Wave 3: copy any map PNGs into the project beside this .vmat and rewrite
			// the .vmat's texture paths to the copies, so the material does not dangle
			// at a /tmp or absolute path the game will never see. A missing source is
			// logged here and fails exportvalidate; the .vmat keeps the original path.
			var lookToWrite = CopyMapsForExport( look, path, assetsRoot, log, written ) ?? look;

			System.IO.Directory.CreateDirectory( System.IO.Path.GetDirectoryName( System.IO.Path.GetFullPath( path ) ) ?? "." );
			System.IO.File.WriteAllText( path, lookToWrite.Vmat() );
			written?.Add( path );
			log?.Add( $"export: slot {slot} look written to {name}" );
		}
	}

	/// <summary>
	/// Wave 3: copy a slot look's map PNGs into the project beside its .vmat.
	/// Returns a clone whose map paths point at the copies (project-relative,
	/// forward slashes), or null when nothing was copied. A source that does not
	/// exist is logged and left alone here; exportvalidate fails on it.
	/// The copy name is the .vmat stem plus _color/_normal/_rough/_metal/_ao.
	/// Texture paths are slash-normalized only, not lowercased: lowercasing the
	/// reference without lowercasing the directory on a case-sensitive disk
	/// would break the very file just copied.
	/// </summary>
	public static SlotLook CopyMapsForExport( SlotLook look, string vmatPath, string assetsRoot, List<string> log, List<string> written )
	{
		if ( look is null || string.IsNullOrWhiteSpace( vmatPath ) )
			return null;

		var maps = new (string Kind, string Source, string Suffix)[]
		{
			("albedo", look.AlbedoMap, "color"),
			("normal", look.NormalMap, "normal"),
			("roughness", look.RoughnessMap, "rough"),
			("metalness", look.MetalnessMap, "metal"),
			("ao", look.AoMap, "ao"),
		};

		var clone = look.Clone();
		var changed = false;
		var vmatDir = System.IO.Path.GetDirectoryName( System.IO.Path.GetFullPath( vmatPath ) ) ?? ".";
		var stem = System.IO.Path.GetFileNameWithoutExtension( vmatPath );
		var rootFull = System.IO.Path.GetFullPath( assetsRoot ?? "." );

		foreach ( var (kind, source, suffix) in maps )
		{
			if ( string.IsNullOrWhiteSpace( source ) )
				continue;

			var srcPath = System.IO.Path.IsPathRooted( source ) ? source : System.IO.Path.Combine( assetsRoot ?? ".", source );

			if ( !System.IO.File.Exists( srcPath ) )
			{
				log?.Add( $"export: slot map {kind} missing: {source} (dangling reference left in .vmat)" );
				continue;
			}

			var ext = System.IO.Path.GetExtension( srcPath );
			if ( string.IsNullOrEmpty( ext ) )
				ext = ".png";

			var destFile = (stem + "_" + suffix + ext).ToLowerInvariant();
			var destPath = System.IO.Path.Combine( vmatDir, destFile );
			string rel;

			try { rel = System.IO.Path.GetRelativePath( rootFull, destPath ).Replace( (char)92, '/' ); }
			catch { rel = destFile; }

			if ( !string.Equals( System.IO.Path.GetFullPath( srcPath ), System.IO.Path.GetFullPath( destPath ), StringComparison.Ordinal ) )
			{
				System.IO.Directory.CreateDirectory( vmatDir );
				System.IO.File.Copy( srcPath, destPath, true );
				written?.Add( destPath );
				log?.Add( $"export: map {kind} copied to {rel}" );
			}

			switch ( kind )
			{
				case "albedo": clone.AlbedoMap = rel; break;
				case "normal": clone.NormalMap = rel; break;
				case "roughness": clone.RoughnessMap = rel; break;
				case "metalness": clone.MetalnessMap = rel; break;
				case "ao": clone.AoMap = rel; break;
			}

			changed = true;
		}

		return changed ? clone : null;
	}

	/// <summary>
	/// A look for a slot nobody gave one, from words in its name (`materials/pad/rubber_grip.vmat`
	/// reads as soft rubber). Null when no word matches: the slot stays clay.
	/// </summary>
	public static SlotLook Guess( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return null;
		var key = name.ToLowerInvariant();
		var file = System.IO.Path.GetFileNameWithoutExtension( key );
		if ( file.StartsWith( "material_" ) ) return null;

		string p =
			Has( key, "led", "glow", "emissive", "lamp", "light" ) ? "led_emissive" :
			Has( key, "pcb", "circuit", "board" ) ? "pcb_green" :
			Has( key, "rubber", "grip", "silicone", "tyre", "tire" ) ? "soft_rubber" :
			Has( key, "webbing", "nylon", "strap", "fabric", "cloth" ) ? "nylon_webbing" :
			Has( key, "keycap", "button", "gloss", "key_" ) ? "glossy_keycap" :
			Has( key, "metal", "steel", "alu", "anodi", "chrome", "iron", "brass" ) ? "anodised_metal" :
			Has( key, "abs", "plastic", "shell", "housing", "case" ) ? "abs_matte" :
			null;

		return p is null ? null : Presets[p].Clone();
	}

	static bool Has( string key, params string[] words ) => words.Any( key.Contains );

	static Vec3 Linear( Vec3 c ) => new( ShadedRender.SrgbToLinear( c.x ), ShadedRender.SrgbToLinear( c.y ), ShadedRender.SrgbToLinear( c.z ) );

	static string Hex( Vec3 c ) =>
		$"#{B( c.x ):x2}{B( c.y ):x2}{B( c.z ):x2}";

	static int B( float v ) => Math.Clamp( (int)MathF.Round( v * 255f ), 0, 255 );
	static string N( float v ) => v.ToString( "0.###", CultureInfo.InvariantCulture );
	static string N6( float v ) => v.ToString( "0.000000", CultureInfo.InvariantCulture );
}
