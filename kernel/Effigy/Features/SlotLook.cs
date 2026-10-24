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

	public bool Glows => Emit.x > 0f || Emit.y > 0f || Emit.z > 0f;

	public SlotLook Clone() => new() { Color = Color, Rough = Rough, Metal = Metal, Emit = Emit, Preset = Preset, Toon = Toon, ToonBands = ToonBands, ToonRim = ToonRim, Stylized = Stylized, OutlineWidth = OutlineWidth, OutlineColor = OutlineColor, Clearcoat = Clearcoat, ClearcoatRoughness = ClearcoatRoughness, Subsurface = Subsurface, SubsurfaceColor = SubsurfaceColor, Opacity = Opacity };

	/// <summary>The shipped hard-surface looks, by name.</summary>
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
		["chitin_iridescent"] = Make( "chitin_iridescent", "#2a3a4a", 0.18f, 0.3f, stylized: 0.4f, clearcoat: 0.7f ),
	};

	static SlotLook Make( string name, string color, float rough, float metal, string emit = null,
		float toon = 0f, int toonBands = 3, float toonRim = 0f, float stylized = 0f,
		float outline = 0f, string outlineColor = null,
		float clearcoat = 0f, float clearcoatRough = 0.05f,
		float subsurface = 0f, string subsurfaceColor = null, float opacity = 1f ) => new()
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
	public static bool IsKey( string key ) => key?.ToLowerInvariant() is "preset" or "color" or "colour" or "rough" or "metal" or "emit" or "toon" or "toonbands" or "toon_bands" or "toonrim" or "toon_rim" or "stylized" or "outline" or "outlinecolor" or "outlinecolour" or "clearcoat" or "coat" or "clearcoatrough" or "clearcoatroughness" or "coatrough" or "subsurface" or "sss" or "subsurfacecolor" or "subsurfacecolour" or "opacity";

	/// <summary>
	/// Apply key=value pairs over <paramref name="start"/> (or a fresh look). preset= goes first,
	/// whatever order it was typed in, so `color=` after it tints the preset rather than being lost.
	/// Returns null and sets <paramref name="error"/> on a bad value.
	/// </summary>
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
					if ( !float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f ) || f < 0f || f > 1f )
					{
						error = $"{key}={value}: give a number from 0 to 1";
						return null;
					}

					if ( key == "rough" ) look.Rough = f; else look.Metal = f;
					break;
				case "toon" or "toonrim" or "toon_rim" or "stylized" or "clearcoat" or "coat" or "clearcoatrough" or "clearcoatroughness" or "coatrough" or "subsurface" or "sss" or "opacity":
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
					}
					break;
				case "toonbands" or "toon_bands":
					if ( !int.TryParse( value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b ) || b < 2 || b > 8 )
					{
						error = $"{key}={value}: give a whole number of cel bands from 2 to 8";
						return null;
					}
					look.ToonBands = b;
					break;
				case "outline":
					if ( !float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out var w ) || w < 0f )
					{
						error = $"{key}={value}: give an outline width in inches, 0 or more";
						return null;
					}
					look.OutlineWidth = w;
					break;
				case "outlinecolor" or "outlinecolour" or "subsurfacecolor" or "subsurfacecolour":
					var hc = FurMaterial.ParseHex( value, new Vec3( -1, -1, -1 ) );
					if ( hc.x < 0f ) { error = $"{key}={value}: give a #rrggbb colour"; return null; }
					if ( key.StartsWith( "outline" ) ) look.OutlineColor = hc; else look.SubsurfaceColor = hc;
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
		if ( !string.IsNullOrEmpty( Preset ) ) s += $" preset={Preset}";
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
	public ShadeMaterial Shade() => new()
	{
		Albedo = Linear( Color ),
		UseVertexColours = false,
		Roughness = Math.Clamp( Rough, 0.03f, 1f ),
		Metallic = Metal,
		Emission = Linear( Emit ) * 2f,
		// A gloss this low on a non-metal reads as a lacquered keycap: give it a clear coat.
		// An explicit clearcoat wins over that heuristic.
		Coat = MathF.Max( Clearcoat, Rough < 0.25f && Metal < 0.5f ? 0.4f : 0f ),
		CoatRoughness = Clearcoat > 0f ? Math.Clamp( ClearcoatRoughness, 0.01f, 1f ) : 0.04f,
		Subsurface = Subsurface,
		SubsurfaceTint = Subsurface > 0f ? Linear( SubsurfaceColor ) : null,
	};

	/// <summary>The slot as an s&amp;box complex.vfx material: tint, metalness, roughness, glow.</summary>
	public string Vmat()
	{
		var s = "// THIS FILE IS AUTO-GENERATED from an Effigy slot look. Edit the look (`material`), not this file.\n" +
			"Layer0\n{\n" +
			"\tshader \"complex.vfx\"\n" +
			$"\tg_vColorTint \"[{N6( Color.x )} {N6( Color.y )} {N6( Color.z )} 0.000000]\"\n" +
			"\tg_flModelTintAmount \"1.000\"\n" +
			$"\tg_flMetalness \"{N( Metal )}\"\n" +
			$"\tg_flRoughnessScaleFactor \"{N( Rough )}\"\n";

		if ( Glows )
		{
			var peak = MathF.Max( Emit.x, MathF.Max( Emit.y, Emit.z ) );
			s += "\tF_SELF_ILLUM 1\n" +
				"\tg_flSelfIllumAlbedoFactor \"1.000\"\n" +
				$"\tg_flSelfIllumBrightness \"{N( 2f * peak )}\"\n" +
				$"\tg_vSelfIllumTint \"[{N6( Emit.x / peak )} {N6( Emit.y / peak )} {N6( Emit.z / peak )} 0.000000]\"\n";
		}

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
				log?.Add( $"export: slot {slot} has a look but no .vmat path, so it exports as {name}; give it one: material {slot} materials/<name>.vmat" );
				continue;
			}

			var path = System.IO.Path.Combine( assetsRoot ?? ".", name );

			if ( System.IO.File.Exists( path ) && !IsOurs( System.IO.File.ReadAllText( path ) ) )
			{
				log?.Add( $"export: kept {name}: it was not written from a slot look" );
				continue;
			}

			System.IO.Directory.CreateDirectory( System.IO.Path.GetDirectoryName( System.IO.Path.GetFullPath( path ) ) ?? "." );
			System.IO.File.WriteAllText( path, look.Vmat() );
			written?.Add( path );
			log?.Add( $"export: slot {slot} look written to {name}" );
		}
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
