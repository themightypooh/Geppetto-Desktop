using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Effigy;

/// <summary>
/// S&amp;box Lane 5a: Source 2 Smart PBR .vmat compiler.
///
/// WHAT THIS FIXES. <see cref="VmdlMaterials"/> lines 12-29 document the blank-grey bug:
/// a .vmdl with no MaterialGroupList is filled in by ModelDoc with
/// <c>use_global_default = true</c> and <c>materials/default.vmat</c>, so a prop that
/// looked right in the viewport compiled to blank grey; an unbound slot resolved to no
/// asset at all and rendered in the red missing-material shader. This compiler attacks
/// both halves from the material side: every bake emits a <c>complex.shader</c> .vmat
/// with all four texture slots filled, and a complete MaterialGroupList remap (see
/// <see cref="GroupList"/>) that names the compiled .vmat explicitly with
/// <c>use_global_default = false</c>, so no slot is ever left for ModelDoc to replace.
///
/// WHAT IT IS NOT. This lane emits .vmat TEXT and bake scalars. It does not bake
/// per-texel curvature, cavity or AO maps from a mesh — there is no mesh input in the
/// verb, and no texture file is written here. Curvature wear, crevice AO and SSS arrive
/// as numbers on the .vmat (<c>g_flEdgeWearAmount</c>, <c>g_flCreviceAoAmount</c>,
/// <c>g_flSssAmount</c> plus the <c>F_DIFFUSE_WRAP</c> SSS flag); the texture slots
/// point at the derived paths the texture baker is expected to fill
/// (<c>materials/&lt;slug&gt;/&lt;slug&gt;_color.tga</c> and siblings). A bake whose
/// texture files were never produced will still compile — it will just show the
/// missing-texture checker, not blank grey — and that gap is stated, not hidden.
/// The .vmat body is generic KV3 (<c>{ shader = "complex.shader" ... }</c>); no
/// kv3 encoding header GUID is emitted, and no ModelDoc compile was run headless,
/// so "valid KV3" here means: balanced braces, the required keys present exactly
/// once, and the remap node naming every supplied slot. A ModelDoc probe is still
/// owed before anyone calls this engine-proven.
///
/// SELF-CONTAINED ON PURPOSE. Only System.* types. No reference to
/// <see cref="VmdlMaterials"/>, no mesh type, no engine type — so the file compiles
/// in an isolated runner with just itself and its test file, and so it can be copied
/// into an s&amp;box project as a loose .cs file like the rest of the kernel.
///
/// VERB SHAPE (modelled here, NOT wired into BuildScript.cs — that wiring is owed):
/// <c>bake vmat name=AlienSkin preset=Flesh sss=0.4 edge_wear=0.5</c>
/// Accepted keys: name, preset, sss, edge_wear, crevice_ao, roughness, metalness.
/// Unknown keys, a missing name, an unknown preset, or an unparseable number throw
/// <see cref="ArgumentException"/> — a bad bake fails loudly instead of emitting a
/// grey default. Sliders (sss, edge_wear, crevice_ao, roughness, metalness) clamp
/// to 0..1; clamping is documented behaviour, tested, not a silent fix-up of a test.
/// </summary>
public static class SmartPbrCompiler
{
	/// <summary>One bake's inputs. Defaults reproduce the spec's example verb.</summary>
	public sealed class Options
	{
		public string Name = "AlienSkin";
		public string Preset = "Flesh";
		public double Sss = 0.4;
		public double EdgeWear = 0.5;
		public double CreviceAo = 0.5;
		public double? Roughness;
		public double? Metalness;

		/// <summary>Explicit texture paths. Null means "derive from the name" (see <see cref="SmartPbrCompiler.TexturePaths"/>).</summary>
		public string TextureColor;
		public string TextureNormal;
		public string TextureRoughness;
		public string TextureAmbientOcclusion;
		public string TextureHeight;
	}

	/// <summary>A compiled bake: the .vmat text, its asset path, and its remap node.</summary>
	public sealed class Result
	{
		public string VmatPath = "";
		public string VmatText = "";
		public string GroupListText = "";
		public int RemapCount;
	}

	/// <summary>Preset defaults: base roughness and metalness before any verb override.</summary>
	public static (double Roughness, double Metalness) PresetDefaults( string preset )
	{
		switch ( (preset ?? "").Trim().ToLowerInvariant() )
		{
			case "flesh": return (0.650, 0.0);
			case "bone": return (0.550, 0.0);
			case "cloth": return (0.900, 0.0);
			case "stone": return (0.850, 0.0);
			case "metal": return (0.350, 1.0);
			default: throw new ArgumentException( $"Unknown SmartPBR preset '{preset}'. Known presets: Flesh, Bone, Cloth, Stone, Metal.", nameof( preset ) );
		}
	}

	/// <summary>Lowercase alphanumeric slug: "Alien Skin" -&gt; "alienskin". Anything else is stripped, never kept as a path separator.</summary>
	public static string Slug( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "SmartPBR bake needs a name (bake vmat name=...).", nameof( name ) );

		var sb = new StringBuilder();
		foreach ( var ch in name )
			if ( (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') )
				sb.Append( char.ToLowerInvariant( ch ) );

		if ( sb.Length == 0 )
			throw new ArgumentException( $"SmartPBR name '{name}' has no letters or digits to build a path from.", nameof( name ) );

		return sb.ToString();
	}

	/// <summary>Asset path for the compiled .vmat: materials/&lt;slug&gt;/&lt;slug&gt;.vmat.</summary>
	public static string VmatPathFor( string name ) => $"materials/{Slug( name )}/{Slug( name )}.vmat";

	/// <summary>Derived texture paths for a name, in TextureColor/Normal/Roughness/AmbientOcclusion order.</summary>
	public static (string Color, string Normal, string Roughness, string AmbientOcclusion, string Height) TexturePaths( string name )
	{
		var slug = Slug( name );
		var dir = $"materials/{slug}";
		return ($"{dir}/{slug}_color.tga", $"{dir}/{slug}_normal.tga", $"{dir}/{slug}_roughness.tga", $"{dir}/{slug}_ao.tga", $"{dir}/{slug}_height.tga");
	}

	static double Clamp01( double v ) => v < 0 ? 0 : v > 1 ? 1 : v;

	static string F( double v ) => v.ToString( "0.000", CultureInfo.InvariantCulture );

	/// <summary>
	/// Parse the Lane 5 verb. Must start with "bake vmat" (case-insensitive, extra
	/// whitespace tolerated); the rest is key=value pairs. name is required.
	/// </summary>
	public static Options ParseVerb( string verb )
	{
		if ( string.IsNullOrWhiteSpace( verb ) )
			throw new ArgumentException( "SmartPBR verb is empty. Expected: bake vmat name=AlienSkin preset=Flesh sss=0.4 edge_wear=0.5", nameof( verb ) );

		var parts = verb.Trim().Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length < 2 || !parts[0].Equals( "bake", StringComparison.OrdinalIgnoreCase ) || !parts[1].Equals( "vmat", StringComparison.OrdinalIgnoreCase ) )
			throw new ArgumentException( $"SmartPBR verb must start with 'bake vmat', got '{verb}'.", nameof( verb ) );

		var options = new Options { Name = null };
		var seenName = false;
		for ( var i = 2; i < parts.Length; i++ )
		{
			var eq = parts[i].IndexOf( '=' );
			if ( eq <= 0 )
				throw new ArgumentException( $"SmartPBR verb token '{parts[i]}' is not key=value.", nameof( verb ) );

			var key = parts[i].Substring( 0, eq ).ToLowerInvariant();
			var value = parts[i].Substring( eq + 1 );
			switch ( key )
			{
				case "name": options.Name = value; seenName = true; break;
				case "preset": options.Preset = value; break;
				case "sss": options.Sss = ParseNumber( key, value ); break;
				case "edge_wear": options.EdgeWear = ParseNumber( key, value ); break;
				case "crevice_ao": options.CreviceAo = ParseNumber( key, value ); break;
				case "roughness": options.Roughness = ParseNumber( key, value ); break;
				case "metalness": options.Metalness = ParseNumber( key, value ); break;
				default: throw new ArgumentException( $"Unknown SmartPBR verb key '{key}'. Accepted: name, preset, sss, edge_wear, crevice_ao, roughness, metalness.", nameof( verb ) );
			}
		}

		if ( !seenName || string.IsNullOrWhiteSpace( options.Name ) )
			throw new ArgumentException( "SmartPBR verb needs name=... (bake vmat name=AlienSkin preset=Flesh sss=0.4 edge_wear=0.5).", nameof( verb ) );

		// Validate the preset now, not at compile time, so a typo fails at parse.
		_ = PresetDefaults( options.Preset );
		_ = Slug( options.Name );
		return options;
	}

	static double ParseNumber( string key, string value )
	{
		if ( !double.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v ) )
			throw new ArgumentException( $"SmartPBR verb key '{key}' needs a number, got '{value}'.", nameof( value ) );
		return v;
	}

	/// <summary>Compile one bake. No slot is left unbound: see <see cref="GroupList"/>.</summary>
	public static Result Compile( Options options, IEnumerable<string> slotNames = null )
	{
		if ( options is null ) throw new ArgumentNullException( nameof( options ) );
		if ( string.IsNullOrWhiteSpace( options.Name ) ) throw new ArgumentException( "SmartPBR bake needs a name.", nameof( options ) );

		var (roughness, metalness) = PresetDefaults( options.Preset );
		roughness = Clamp01( options.Roughness ?? roughness );
		metalness = Clamp01( options.Metalness ?? metalness );
		var sss = Clamp01( options.Sss );
		var edgeWear = Clamp01( options.EdgeWear );
		var creviceAo = Clamp01( options.CreviceAo );

		var vmatPath = VmatPathFor( options.Name );
		var (dColor, dNormal, dRough, dAo, dHeight) = TexturePaths( options.Name );
		var color = string.IsNullOrWhiteSpace( options.TextureColor ) ? dColor : options.TextureColor;
		var normal = string.IsNullOrWhiteSpace( options.TextureNormal ) ? dNormal : options.TextureNormal;
		var rough = string.IsNullOrWhiteSpace( options.TextureRoughness ) ? dRough : options.TextureRoughness;
		var ao = string.IsNullOrWhiteSpace( options.TextureAmbientOcclusion ) ? dAo : options.TextureAmbientOcclusion;
		var height = string.IsNullOrWhiteSpace( options.TextureHeight ) ? dHeight : options.TextureHeight;

		var sb = new StringBuilder();
		sb.Append( "{\n" );
		sb.Append( "\tshader = \"complex.shader\"\n" );
		sb.Append( "\tF_SPECULAR = 1\n" );
		sb.Append( "\tF_ROUGHNESS = 1\n" );
		sb.Append( "\tF_TRANSLUCENT = 1\n" );
		sb.Append( "\tF_DIFFUSE_WRAP = 1\n" );
		sb.Append( "\tF_PARALLAX_OCCLUSION = 1\n" );
		sb.Append( $"\tTextureColor = \"{color}\"\n" );
		sb.Append( $"\tTextureNormal = \"{normal}\"\n" );
		sb.Append( $"\tTextureRoughness = \"{rough}\"\n" );
		sb.Append( $"\tTextureAmbientOcclusion = \"{ao}\"\n" );
		sb.Append( $"\tTextureHeight = \"{height}\"\n" );
		sb.Append( $"\tg_flMetalness = {F( metalness )}\n" );
		sb.Append( $"\tg_flRoughnessScaleFactor = {F( roughness )}\n" );
		// Bake scalars, carried on the .vmat. F_DIFFUSE_WRAP is the Source 2 SSS
		// switch; the amount itself, the edge wear and the crevice AO have no single
		// canonical complex.shader scalar, so they are emitted under these explicit
		// names rather than smuggled into an unrelated parameter. A ModelDoc probe
		// is owed to confirm they are carried, not silently dropped.
		sb.Append( $"\tg_flSssAmount = {F( sss )}\n" );
		sb.Append( $"\tg_flEdgeWearAmount = {F( edgeWear )}\n" );
		sb.Append( $"\tg_flCreviceAoAmount = {F( creviceAo )}\n" );
		sb.Append( "\tg_flParallaxScale = 0.04\n" );
		sb.Append( "\tg_vParallaxSteps = [8, 32]\n" );
		sb.Append( "}\n" );

		var remaps = Remaps( options, slotNames );
		return new Result
		{
			VmatPath = vmatPath,
			VmatText = sb.ToString(),
			GroupListText = GroupList( remaps ),
			RemapCount = remaps.Count,
		};
	}

	/// <summary>
	/// Every from → to pair for a bake. The compiled .vmat path plus every spelling
	/// the mesh writers / ModelDoc importer might keep (full path, filename, stripped
	/// name, and the name as given), plus one remap per supplied mesh slot — all to
	/// the same compiled .vmat. Deduped exactly (case-sensitive, in first-seen order):
/// a mesh may carry the name as given ("AlienSkin"), and collapsing it into the
/// lowercase slug would drop exactly the spelling that mesh uses.
	/// Nothing is left to fall through to the global default.
	/// </summary>
	public static List<(string From, string To)> Remaps( Options options, IEnumerable<string> slotNames = null )
	{
		if ( options is null ) throw new ArgumentNullException( nameof( options ) );
		var target = VmatPathFor( options.Name );
		var slug = Slug( options.Name );
		var remaps = new List<(string, string)>();
		var seen = new HashSet<string>( StringComparer.Ordinal );

		void Add( string from )
		{
			if ( string.IsNullOrWhiteSpace( from ) ) return;
			if ( seen.Add( from ) ) remaps.Add( (from, target) );
		}

		Add( target );
		Add( $"{slug}.vmat" );
		Add( slug );
		Add( options.Name );
		if ( slotNames is not null )
			foreach ( var slot in slotNames )
				Add( slot );

		return remaps;
	}

	/// <summary>
	/// The MaterialGroupList KV3 node for a bake's remaps. <c>use_global_default</c>
	/// is false and <c>global_default_material</c> is empty — the exact two settings
	/// whose defaults produced the blank-grey prop. This node belongs in the .vmdl,
	/// pasted beside the mesh; it is emitted here so the .vmat and its remap can
	/// never drift apart. It is NOT written into the .vmat itself.
	/// </summary>
	public static string GroupList( List<(string From, string To)> remaps )
	{
		if ( remaps is null ) throw new ArgumentNullException( nameof( remaps ) );
		var sb = new StringBuilder();
		sb.Append( "{\n" );
		sb.Append( "\t_class = \"MaterialGroupList\"\n" );
		sb.Append( "\tchildren =\n" );
		sb.Append( "\t[\n" );
		sb.Append( "\t\t{\n" );
		sb.Append( "\t\t\t_class = \"DefaultMaterialGroup\"\n" );
		sb.Append( "\t\t\tremaps =\n" );
		sb.Append( "\t\t\t[\n" );
		foreach ( var (from, to) in remaps )
		{
			sb.Append( "\t\t\t\t{\n" );
			sb.Append( $"\t\t\t\t\tfrom = \"{from}\"\n" );
			sb.Append( $"\t\t\t\t\tto = \"{to}\"\n" );
			sb.Append( "\t\t\t\t},\n" );
		}
		sb.Append( "\t\t\t]\n" );
		sb.Append( "\t\t\tuse_global_default = false\n" );
		sb.Append( "\t\t\tglobal_default_material = \"\"\n" );
		sb.Append( "\t\t},\n" );
		sb.Append( "\t]\n" );
		sb.Append( "}\n" );
		return sb.ToString();
	}
}
