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

	public bool Glows => Emit.x > 0f || Emit.y > 0f || Emit.z > 0f;

	public SlotLook Clone() => new() { Color = Color, Rough = Rough, Metal = Metal, Emit = Emit, Preset = Preset };

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
	};

	static SlotLook Make( string name, string color, float rough, float metal, string emit = null ) => new()
	{
		Preset = name,
		Color = FurMaterial.ParseHex( color, Vec3.One ),
		Rough = rough,
		Metal = metal,
		Emit = emit is null ? default : FurMaterial.ParseHex( emit, default ),
	};

	public static string PresetList => string.Join( ", ", Presets.Keys );

	/// <summary>The keys `material` and the document's `materiallook` line take.</summary>
	public static bool IsKey( string key ) => key?.ToLowerInvariant() is "preset" or "color" or "colour" or "rough" or "metal" or "emit";

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
			}
		}

		return look;
	}

	/// <summary>The document form: `color=#rrggbb rough=0.5 metal=0 [emit=#rrggbb] [preset=name]`.</summary>
	public string Describe()
	{
		var s = $"color={Hex( Color )} rough={N( Rough )} metal={N( Metal )}";
		if ( Glows ) s += $" emit={Hex( Emit )}";
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
		Coat = Rough < 0.25f && Metal < 0.5f ? 0.4f : 0f,
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
