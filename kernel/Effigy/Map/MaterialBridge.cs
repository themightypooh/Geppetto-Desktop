using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Effigy;

/// <summary>
/// Material bridge: Geppetto PBR &lt;-&gt; Source VMT &lt;-&gt; glTF PBR
/// (hammer-material-bridge-pbr-vmt-gltf).
///
/// The PBR side is the existing shared record <see cref="PbrMaterial"/> (MaterialX.cs:
/// linear base colour, metallic, roughness, maps, opacity, emissive, clearcoat, IOR).
/// This file adds no second PBR type; it only maps that record to and from the other
/// two formats.
///
/// VMT (Source 1) mapping, canonical keys this bridge writes and reads:
///   $basetexture &lt;- BaseColorMap (extension stripped; Source names no extension)
///   $color       &lt;- BaseColor, as "[r g b]". Written when there is no colour map
///                  (a flat-colour material has nothing else to carry the colour), or
///                  when a map is present and the colour is not white (it is a tint).
///                  Omitted for a white base colour over a map (Source default).
///   $bumpmap     &lt;- NormalMap ($normalmap is also accepted on read)
///   $roughness   &lt;- Roughness. On read, a missing $roughness falls back to
///                  $phongexponent via roughness = sqrt(2 / (exponent + 2)).
///   $metalness   &lt;- Metallic (Source 2-style key; classic Source has no metalness
///                  scalar, so this is the bridge's canonical key, not a claim that
///                  every Source branch honours it)
///   $metalnessmap / $roughnessmap &lt;- the matching map slots
///   $translucent "1" + $alpha &lt;- Opacity &lt; 1
///   $selfillum "1" + $selfillumtint &lt;- non-black Emissive
///   $surfaceprop &lt;- the surfaceProp argument (PbrMaterial carries no surfaceprop
///                  slot, so it lives on the VMT side; see VmtMaterial.SurfaceProp)
/// Unknown VMT keys are never dropped: they stay in VmtMaterial.Params and are
/// re-emitted verbatim by ToText, including nested blocks (kept in VmtMaterial.Blocks).
/// A VMT -&gt; PBR -&gt; VMT round-trip keeps them as long as the VmtMaterial is the
/// carrier (PbrMaterial has no slot for them, by design).
///
/// Tool textures (nodraw, skybox, hint, skip, trigger, clip, areaportal, occluder,
/// blocklight, blocklos, origin, plus playerclip, npcclip, ladder, fog) are VMTs
/// whose identity is a shader + compile flags, not a PBR look. The table is DERIVED
/// from the canonical registry in ToolTexture.cs (hammer-face-material-uv-model):
/// a tool's %compile* VMT flags are its registry flags rendered as strings
/// (DropsFromBakedMesh -&gt; "%compileNoDraw", not solid -&gt; "%compileNonSolid"),
/// so this bridge cannot disagree with FaceMaterial about what a tool does.
/// %compileDetail is brush-level, never on a tool VMT (same rule as ToolTexture).
/// The BSP-compile lane owns interpreting those flags; this bridge only carries
/// them losslessly. glTF has no tool-texture concept, so a tool material crosses
/// to glTF as a marker: name "tools/tools&lt;name&gt;" with the tool name, kind,
/// shader and compile flags in extras, so a downstream exporter can exclude it
/// from the baked mesh (the same rule as FaceMaterial's "nodraw excluded from the
/// baked mesh, kept in the brush").
///
/// glTF 2.0 mapping: baseColorFactor [r g b a] (linear; PbrMaterial.BaseColor is
/// already linear), metallicFactor, roughnessFactor, baseColorTexture /
/// metallicRoughnessTexture / normalTexture by texture index when the caller supplies
/// an index map, otherwise the source paths are preserved under extras.geppetto so
/// nothing is lost. alphaMode BLEND when Opacity &lt; 1, else OPAQUE. Clearcoat and
/// IOR cross via KHR_materials_clearcoat / KHR_materials_ior when set (IOR only
/// when it differs from the 1.5 default). KHR_materials_transmission is NOT emitted:
/// translucency travels via baseColorFactor alpha + BLEND. A combined
/// metallic-roughness texture is emitted only when MetallicMap and RoughnessMap name
/// the same file; differing maps are preserved under extras.geppetto instead.
///
/// DEPENDENCY NOTE (hammer-face-material-uv-model). The dependency LANDED while
/// this ticket was being written (FaceMaterial.cs + ToolTexture.cs, 2026-11-13
/// ~15:57 EST) and this bridge was reconciled against it: FaceMaterial carries a
/// material NAME string + FaceUv (not a PbrMaterial), so the seam is by name —
/// MaterialBridge.ToolTexture(FaceMaterial.MaterialName) — and the tool table is
/// derived from the canonical ToolTexture registry (see above), not a parallel
/// copy. PbrMaterial is still the look record; a FaceMaterial's look is resolved
/// by name through this bridge. Canonical name spellings follow ToolTexture:
/// "tools/toolsblock_los" (with underscore); the "blocklos" spelling is accepted
/// as an alias on lookup.
/// </summary>
public static class MaterialBridge
{
	public const string DefaultShader = "LightmappedGeneric";
	public const string WaterShader = "Water";
	public const string SkyShader = "Sky";

	/// <summary>Canonical compile-flag keys. Values are always "1" when present.</summary>
	public const string CompileNoDraw = "%compileNoDraw";
	public const string CompileNonSolid = "%compileNonSolid";
	public const string CompileDetail = "%compileDetail";
	public const string ToolTextureMarker = "%tooltexture";

	/// <summary>Source surfaceprop for a named material kind, or null when the kind has none.</summary>
	public static string SurfacePropForKind( string kind )
	{
		if ( string.IsNullOrWhiteSpace( kind ) ) return null;
		switch ( kind.Trim().ToLowerInvariant() )
		{
			case "concrete": return "concrete";
			case "metal": return "metal";
			case "glass": return "glass";
			case "water": return "water";
			case "wood": return "wood";
			case "dirt": return "dirt";
			case "flesh": return "flesh";
			case "plastic": return "plastic";
			case "rock": return "rock";
			default: return null;
		}
	}

	/// <summary>
	/// PBR -&gt; VMT. <paramref name="shader"/> defaults to LightmappedGeneric;
	/// pass "Water" for water, "Sky" for a skybox. <paramref name="surfaceProp"/>
	/// defaults to null (no $surfaceprop line). <paramref name="extra"/> key-values
	/// are written as-is and win over mapped keys on collision, so a caller can
	/// force any VMT key, including unknown ones.
	/// </summary>
	public static VmtMaterial ToVmt( PbrMaterial material, string shader = null, string surfaceProp = null,
		IEnumerable<KeyValuePair<string, string>> extra = null )
	{
		if ( material is null ) throw new ArgumentNullException( nameof( material ) );
		var vmt = new VmtMaterial { Shader = string.IsNullOrWhiteSpace( shader ) ? DefaultShader : shader.Trim() };
		if ( !string.IsNullOrWhiteSpace( material.BaseColorMap ) )
			vmt.Set( "$basetexture", StripExtension( material.BaseColorMap ) );
		bool hasMap = !string.IsNullOrWhiteSpace( material.BaseColorMap );
		if ( !hasMap || !IsWhite( material.BaseColor ) )
			vmt.Set( "$color", ColorText( material.BaseColor ) );
		if ( !string.IsNullOrWhiteSpace( material.NormalMap ) )
			vmt.Set( "$bumpmap", StripExtension( material.NormalMap ) );
		vmt.Set( "$roughness", F( material.Roughness ) );
		if ( material.Metallic > 0f )
			vmt.Set( "$metalness", F( material.Metallic ) );
		if ( !string.IsNullOrWhiteSpace( material.MetallicMap ) )
			vmt.Set( "$metalnessmap", StripExtension( material.MetallicMap ) );
		if ( !string.IsNullOrWhiteSpace( material.RoughnessMap ) )
			vmt.Set( "$roughnessmap", StripExtension( material.RoughnessMap ) );
		if ( material.Opacity < 0.999f )
		{
			vmt.Set( "$translucent", "1" );
			vmt.Set( "$alpha", F( material.Opacity ) );
		}
		if ( !IsBlack( material.Emissive ) )
		{
			vmt.Set( "$selfillum", "1" );
			vmt.Set( "$selfillumtint", ColorText( material.Emissive ) );
		}
		if ( !string.IsNullOrWhiteSpace( surfaceProp ) )
			vmt.Set( "$surfaceprop", surfaceProp.Trim() );
		if ( extra != null )
			foreach ( var pair in extra )
				vmt.Set( pair.Key, pair.Value );
		return vmt;
	}

	/// <summary>
	/// VMT -&gt; PBR. Unknown keys are ignored here (they stay on the VmtMaterial);
	/// only the mapped keys above are read. Missing keys fall back to the
	/// PbrMaterial defaults (roughness 0.6, metallic 0, opacity 1); an unparseable
	/// number is ignored, never thrown. A $color-less material with a map reads as
	/// white; without a map it keeps the PbrMaterial default grey.
	/// </summary>
	public static PbrMaterial FromVmt( VmtMaterial vmt )
	{
		if ( vmt is null ) throw new ArgumentNullException( nameof( vmt ) );
		var pbr = new PbrMaterial();
		string map = vmt.BaseTexture;
		if ( !string.IsNullOrWhiteSpace( map ) )
			pbr.BaseColorMap = map;
		Vec3? color = ParseColor( vmt.Get( "$color" ) );
		if ( color.HasValue )
			pbr.BaseColor = color.Value;
		else if ( !string.IsNullOrWhiteSpace( map ) )
			pbr.BaseColor = new Vec3( 1f, 1f, 1f );
		string normal = vmt.Get( "$bumpmap" ) ?? vmt.Get( "$normalmap" );
		if ( !string.IsNullOrWhiteSpace( normal ) )
			pbr.NormalMap = normal;
		float? roughness = ParseFloat( vmt.Get( "$roughness" ) );
		if ( !roughness.HasValue )
		{
			float? exponent = ParseFloat( vmt.Get( "$phongexponent" ) );
			if ( exponent.HasValue )
				roughness = MathF.Sqrt( 2f / ( exponent.Value + 2f ) );
		}
		if ( roughness.HasValue )
			pbr.Roughness = Math.Clamp( roughness.Value, 0f, 1f );
		float? metallic = ParseFloat( vmt.Get( "$metalness" ) );
		if ( metallic.HasValue )
			pbr.Metallic = Math.Clamp( metallic.Value, 0f, 1f );
		string metalMap = vmt.Get( "$metalnessmap" );
		if ( !string.IsNullOrWhiteSpace( metalMap ) )
			pbr.MetallicMap = metalMap;
		string roughMap = vmt.Get( "$roughnessmap" );
		if ( !string.IsNullOrWhiteSpace( roughMap ) )
			pbr.RoughnessMap = roughMap;
		float? alpha = ParseFloat( vmt.Get( "$alpha" ) );
		if ( alpha.HasValue )
			pbr.Opacity = Math.Clamp( alpha.Value, 0f, 1f );
		if ( vmt.Get( "$selfillum" ) == "1" )
		{
			Vec3? tint = ParseColor( vmt.Get( "$selfillumtint" ) );
			pbr.Emissive = tint ?? new Vec3( 1f, 1f, 1f );
		}
		return pbr;
	}

	/// <summary>
	/// PBR -&gt; glTF material record. When <paramref name="tool"/> is given the
	/// result is a tool marker (see the class comment), not a renderable look.
	/// </summary>
	public static GltfMaterial ToGltf( PbrMaterial material, ToolTextureInfo tool = null )
	{
		if ( material is null ) throw new ArgumentNullException( nameof( material ) );
		var gltf = new GltfMaterial
		{
			Name = tool != null ? tool.MaterialName : material.Name,
			BaseColor = material.BaseColor,
			Opacity = material.Opacity,
			Metallic = material.Metallic,
			Roughness = material.Roughness,
			BaseColorTexture = material.BaseColorMap,
			NormalTexture = material.NormalMap,
			Emissive = material.Emissive,
			Clearcoat = material.Clearcoat,
			ClearcoatRoughness = material.ClearcoatRoughness,
			Ior = material.Ior,
			HasIor = Math.Abs( material.Ior - 1.5f ) > 1e-4f,
			AlphaMode = material.Opacity < 0.999f ? "BLEND" : "OPAQUE",
		};
		if ( !string.IsNullOrWhiteSpace( material.MetallicMap ) && material.MetallicMap == material.RoughnessMap )
			gltf.MetallicRoughnessTexture = material.MetallicMap;
		else
		{
			if ( !string.IsNullOrWhiteSpace( material.MetallicMap ) )
				gltf.Extras["metallicMap"] = material.MetallicMap;
			if ( !string.IsNullOrWhiteSpace( material.RoughnessMap ) )
				gltf.Extras["roughnessMap"] = material.RoughnessMap;
		}
		if ( tool != null )
		{
			gltf.Extras["tool"] = tool.MaterialName;
			gltf.Extras["toolKind"] = tool.Kind.ToString();
			gltf.Extras["shader"] = tool.Shader;
			gltf.Extras["compileFlags"] = string.Join( ",", tool.CompileFlags );
		}
		return gltf;
	}

	/// <summary>glTF material record -&gt; PBR. A tool marker's extras are not PBR data and are dropped here.</summary>
	public static PbrMaterial FromGltf( GltfMaterial gltf )
	{
		if ( gltf is null ) throw new ArgumentNullException( nameof( gltf ) );
		var pbr = new PbrMaterial
		{
			Name = gltf.Name,
			BaseColor = gltf.BaseColor,
			Opacity = gltf.Opacity,
			Metallic = gltf.Metallic,
			Roughness = gltf.Roughness,
			BaseColorMap = gltf.BaseColorTexture,
			NormalMap = gltf.NormalTexture,
			Emissive = gltf.Emissive,
			Clearcoat = gltf.Clearcoat,
			ClearcoatRoughness = gltf.ClearcoatRoughness,
			Ior = gltf.HasIor ? gltf.Ior : 1.5f,
		};
		if ( !string.IsNullOrWhiteSpace( gltf.MetallicRoughnessTexture ) )
		{
			pbr.MetallicMap = gltf.MetallicRoughnessTexture;
			pbr.RoughnessMap = gltf.MetallicRoughnessTexture;
		}
		else
		{
			if ( gltf.Extras.TryGetValue( "metallicMap", out string metalMap ) )
				pbr.MetallicMap = metalMap;
			if ( gltf.Extras.TryGetValue( "roughnessMap", out string roughMap ) )
				pbr.RoughnessMap = roughMap;
		}
		return pbr;
	}

	/// <summary>PBR -&gt; glTF material JSON (a single material object, glTF 2.0).</summary>
	public static string ToGltfJson( PbrMaterial material, IReadOnlyDictionary<string, int> textureIndices = null,
		ToolTextureInfo tool = null )
		=> ToGltf( material, tool ).ToJson( textureIndices );

	/// <summary>glTF material JSON -&gt; PBR, via the record. Throws FormatException on malformed JSON.</summary>
	public static PbrMaterial FromGltfJson( string json ) => GltfMaterial.ParseJson( json ).ToPbr();

	// ---- tool textures ----

	/// <summary>
	/// The tool table, derived from the canonical <see cref="ToolTexture.All"/>
	/// registry (hammer-face-material-uv-model): one ToolTextureInfo per registry
	/// entry, same order. %compile* strings are the registry flags rendered for a
	/// VMT (NoDraw -&gt; %compileNoDraw, non-solid -&gt; %compileNonSolid); the
	/// kind-specific meaning (Sky, Hint, Clip, ...) has no %compile* string in the
	/// filed three-key set and is preserved in ToolTextureInfo.Kind instead.
	/// </summary>
	static readonly ToolTextureInfo[] Tools = BuildTools();

	static ToolTextureInfo[] BuildTools()
	{
		// global:: — the bare name would bind to MaterialBridge.ToolTexture(string),
		// the lookup method on this class, not the registry type.
		var tools = new ToolTextureInfo[global::Effigy.ToolTexture.All.Count];
		for ( int i = 0; i < tools.Length; i++ )
		{
			global::Effigy.ToolTexture entry = global::Effigy.ToolTexture.All[i];
			var flags = new List<string>();
			if ( entry.DropsFromBakedMesh ) flags.Add( CompileNoDraw );
			if ( !entry.IsSolid ) flags.Add( CompileNonSolid );
			tools[i] = new ToolTextureInfo(
				entry.Name.Substring( "tools/".Length ),
				entry.Name,
				entry.Kind == ToolTextureKind.Skybox ? SkyShader : DefaultShader,
				entry.Kind,
				flags.ToArray() );
		}
		return tools;
	}

	/// <summary>All tool textures this bridge knows, in table order.</summary>
	public static IReadOnlyList<ToolTextureInfo> ToolTextures => Tools;

	/// <summary>
	/// Tool info for a tool name, accepting "nodraw", "toolsnodraw" and
	/// "tools/toolsnodraw" spellings, case-insensitively, plus the filed-spec
	/// alias "blocklos" for the canonical "toolsblock_los". Null when not a tool.
	/// </summary>
	public static ToolTextureInfo ToolTexture( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return null;
		string bare = name.Trim().Replace( '\\', '/' ).ToLowerInvariant();
		if ( bare.StartsWith( "materials/", StringComparison.Ordinal ) ) bare = bare.Substring( "materials/".Length );
		if ( bare.StartsWith( "tools/", StringComparison.Ordinal ) ) bare = bare.Substring( "tools/".Length );
		if ( !bare.StartsWith( "tools", StringComparison.Ordinal ) ) bare = "tools" + bare;
		if ( bare == "toolsblocklos" ) bare = "toolsblock_los";
		foreach ( var tool in Tools )
			if ( tool.Name == bare ) return tool;
		return null;
	}

	/// <summary>True when <paramref name="name"/> is one of the known tool textures (same spellings as ToolTexture).</summary>
	public static bool IsToolTexture( string name ) => ToolTexture( name ) != null;

	internal static string F( float value ) => value.ToString( "0.000", CultureInfo.InvariantCulture );

	internal static string ColorText( Vec3 color ) =>
		"[" + F( color.x ) + " " + F( color.y ) + " " + F( color.z ) + "]";

	internal static bool IsWhite( Vec3 color ) => color.x >= 0.999f && color.y >= 0.999f && color.z >= 0.999f;

	internal static bool IsBlack( Vec3 color ) => color.x <= 0.001f && color.y <= 0.001f && color.z <= 0.001f;

	internal static float? ParseFloat( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) ) return null;
		return float.TryParse( text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value )
			? value : (float?)null;
	}

	internal static Vec3? ParseColor( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) ) return null;
		string body = text.Trim().TrimStart( '[' ).TrimEnd( ']' ).Trim();
		string[] parts = body.Split( new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length < 3 ) return null;
		float? r = ParseFloat( parts[0] ), g = ParseFloat( parts[1] ), b = ParseFloat( parts[2] );
		if ( !r.HasValue || !g.HasValue || !b.HasValue ) return null;
		return new Vec3( r.Value, g.Value, b.Value );
	}

	/// <summary>Source texture names carry no extension; strip .vtf/.png/.tga/.jpg, normalise slashes.</summary>
	internal static string StripExtension( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return path;
		string clean = path.Trim().Replace( '\\', '/' );
		int dot = clean.LastIndexOf( '.' );
		int slash = clean.LastIndexOf( '/' );
		if ( dot > slash )
		{
			string ext = clean.Substring( dot ).ToLowerInvariant();
			if ( ext == ".vtf" || ext == ".png" || ext == ".tga" || ext == ".jpg" || ext == ".jpeg" || ext == ".vmt" )
				clean = clean.Substring( 0, dot );
		}
		return clean;
	}
}

/// <summary>
/// A parsed Source VMT: shader name + flat key/value params + nested blocks.
/// Keys are case-insensitive (Source is); first-seen order is kept so ToText
/// re-emits a stable document. Unknown keys live here alongside known ones and
/// are never dropped by Parse or ToText.
/// </summary>
public sealed class VmtMaterial
{
	public string Shader = MaterialBridge.DefaultShader;

	/// <summary>Flat params, insertion order in <see cref="KeyOrder"/>.</summary>
	public Dictionary<string, string> Params = new( StringComparer.OrdinalIgnoreCase );

	public List<string> KeyOrder = new();

	/// <summary>Nested blocks (e.g. Proxies): name -&gt; canonical re-render of the inner tokens.</summary>
	public Dictionary<string, string> Blocks = new( StringComparer.OrdinalIgnoreCase );

	public string BaseTexture
	{
		get => Get( "$basetexture" );
		set => Set( "$basetexture", value );
	}

	public string SurfaceProp
	{
		get => Get( "$surfaceprop" );
		set => Set( "$surfaceprop", value );
	}

	/// <summary>Tool name when $basetexture is tools/tools&lt;name&gt;, else null.</summary>
	public string ToolName
	{
		get
		{
			string tex = BaseTexture;
			if ( string.IsNullOrWhiteSpace( tex ) ) return null;
			if ( !tex.StartsWith( "tools/", StringComparison.OrdinalIgnoreCase ) ) return null;
			return tex.Substring( "tools/".Length );
		}
	}

	public bool IsToolTexture => ToolName != null && MaterialBridge.IsToolTexture( ToolName );

	public string Get( string key )
		=> key != null && Params.TryGetValue( key, out string value ) ? value : null;

	/// <summary>Set a param; a null value removes it.</summary>
	public void Set( string key, string value )
	{
		if ( string.IsNullOrWhiteSpace( key ) ) throw new ArgumentException( "VMT key must not be empty.", nameof( key ) );
		if ( value is null )
		{
			Params.Remove( key );
			KeyOrder.RemoveAll( k => string.Equals( k, key, StringComparison.OrdinalIgnoreCase ) );
			return;
		}
		if ( !Params.ContainsKey( key ) ) KeyOrder.Add( key );
		Params[key] = value;
	}

	public PbrMaterial ToPbr() => MaterialBridge.FromVmt( this );

	/// <summary>Canonical VMT text: quoted shader, braces, tab-indented "key" "value" lines.</summary>
	public string ToText()
	{
		var sb = new StringBuilder();
		sb.Append( '"' ).Append( Escape( Shader ) ).Append( "\"\n{\n" );
		foreach ( string key in KeyOrder )
		{
			if ( !Params.TryGetValue( key, out string value ) ) continue;
			sb.Append( "\t\"" ).Append( Escape( key ) ).Append( "\" \"" ).Append( Escape( value ) ).Append( "\"\n" );
		}
		foreach ( var pair in Blocks )
		{
			sb.Append( "\t\"" ).Append( Escape( pair.Key ) ).Append( "\"\n\t{\n" );
			foreach ( string line in pair.Value.Split( '\n' ) )
				if ( line.Length > 0 ) sb.Append( "\t\t" ).Append( line ).Append( "\n" );
			sb.Append( "\t}\n" );
		}
		sb.Append( "}\n" );
		return sb.ToString();
	}

	/// <summary>
	/// Parse VMT text. Throws FormatException on empty input, a missing shader, or
	/// unbalanced braces. // comments are skipped. Duplicate keys: last wins, order
	/// kept at first sight. Nested blocks are preserved in <see cref="Blocks"/>.
	/// </summary>
	public static VmtMaterial Parse( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) ) throw new FormatException( "VMT text is empty." );
		var tokens = Tokenize( text );
		int at = 0;
		string Next()
		{
			if ( at >= tokens.Count ) throw new FormatException( "Unexpected end of VMT text." );
			return tokens[at++];
		}
		string shader = Next();
		if ( shader == "{" || shader == "}" ) throw new FormatException( "VMT must start with a shader name." );
		if ( Next() != "{" ) throw new FormatException( "VMT shader must be followed by '{'." );
		var vmt = new VmtMaterial { Shader = shader };
		while ( true )
		{
			if ( at >= tokens.Count ) throw new FormatException( "Unbalanced braces in VMT text." );
			string key = Next();
			if ( key == "}" ) break;
			if ( key == "{" ) throw new FormatException( "Unexpected '{' in VMT text." );
			string value = Next();
			if ( value == "}" ) throw new FormatException( "VMT key '" + key + "' has no value." );
			if ( value == "{" )
			{
				var inner = new List<string>();
				int depth = 1;
				while ( depth > 0 )
				{
					if ( at >= tokens.Count ) throw new FormatException( "Unbalanced braces in VMT block '" + key + "'." );
					string token = Next();
					if ( token == "{" ) depth++;
					else if ( token == "}" ) depth--;
					if ( depth > 0 ) inner.Add( token );
				}
				vmt.Blocks[key] = RenderInner( inner );
			}
			else
			{
				vmt.Set( key, value );
			}
		}
		if ( at != tokens.Count ) throw new FormatException( "Trailing content after the VMT closing brace." );
		return vmt;
	}

	static string RenderInner( List<string> tokens )
	{
		// Tokens re-joined one per line; braces stay as tokens so a nested block's
		// keys still read back, even though the layout is canonical, not original.
		var lines = new List<string>();
		foreach ( string token in tokens )
			lines.Add( token == "{" || token == "}" ? token : "\"" + Escape( token ) + "\"" );
		// Pair consecutive quoted lines back into key/value lines for readability.
		var joined = new StringBuilder();
		for ( int i = 0; i < lines.Count; i++ )
		{
			if ( i + 1 < lines.Count && lines[i].StartsWith( "\"", StringComparison.Ordinal )
				&& lines[i + 1].StartsWith( "\"", StringComparison.Ordinal ) )
			{
				joined.Append( lines[i] ).Append( ' ' ).Append( lines[i + 1] ).Append( '\n' );
				i++;
			}
			else joined.Append( lines[i] ).Append( '\n' );
		}
		return joined.ToString().TrimEnd( '\n' );
	}

	static string Escape( string text ) => text.Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" );

	static List<string> Tokenize( string text )
	{
		var tokens = new List<string>();
		int i = 0;
		while ( i < text.Length )
		{
			char c = text[i];
			if ( char.IsWhiteSpace( c ) ) { i++; continue; }
			if ( c == '/' && i + 1 < text.Length && text[i + 1] == '/' )
			{
				while ( i < text.Length && text[i] != '\n' ) i++;
				continue;
			}
			if ( c == '{' || c == '}' ) { tokens.Add( c.ToString() ); i++; continue; }
			if ( c == '"' )
			{
				var sb = new StringBuilder();
				i++;
				while ( i < text.Length && text[i] != '"' )
				{
					if ( text[i] == '\\' && i + 1 < text.Length ) { sb.Append( text[i + 1] ); i += 2; }
					else { sb.Append( text[i] ); i++; }
				}
				if ( i >= text.Length ) throw new FormatException( "Unterminated string in VMT text." );
				i++;
				tokens.Add( sb.ToString() );
				continue;
			}
			int start = i;
			while ( i < text.Length && !char.IsWhiteSpace( text[i] ) && text[i] != '{' && text[i] != '}' ) i++;
			tokens.Add( text.Substring( start, i - start ) );
		}
		return tokens;
	}
}

/// <summary>
/// One Source tool texture: its VMT shader and compile flags. The VMT always
/// carries "%tooltexture" "1" plus the flags below (each "1"). See MaterialBridge
/// for what the flags mean and who interprets them.
/// </summary>
public sealed class ToolTextureInfo
{
	public ToolTextureInfo( string name, string materialName, string shader, ToolTextureKind kind, string[] compileFlags )
	{
		Name = name;
		MaterialName = materialName;
		Shader = shader;
		Kind = kind;
		CompileFlags = compileFlags ?? Array.Empty<string>();
	}

	/// <summary>Bare name, e.g. "toolsnodraw".</summary>
	public string Name { get; }

	/// <summary>Canonical material name, e.g. "tools/toolsnodraw".</summary>
	public string MaterialName { get; }

	public string Shader { get; }

	/// <summary>The canonical registry kind (ToolTextureKind).</summary>
	public ToolTextureKind Kind { get; }

	public string[] CompileFlags { get; }

	/// <summary>
	/// The tool's VMT. For "skybox" the basetexture is the sky name
	/// (<paramref name="skyName"/>, default "sky_day01_01"); for every other tool
	/// it is the tools/ material itself.
	/// </summary>
	public VmtMaterial ToVmt( string skyName = null )
	{
		var vmt = new VmtMaterial { Shader = Shader };
		vmt.Set( "$basetexture", Kind == ToolTextureKind.Skybox ? ( string.IsNullOrWhiteSpace( skyName ) ? "sky_day01_01" : skyName.Trim() ) : MaterialName );
		vmt.Set( MaterialBridge.ToolTextureMarker, "1" );
		foreach ( string flag in CompileFlags )
			vmt.Set( flag, "1" );
		return vmt;
	}
}

/// <summary>
/// A glTF 2.0 material (the material object only, not a whole asset). Texture
/// slots hold source paths until the caller supplies a texture-index map to
/// <see cref="ToJson(IReadOnlyDictionary{string, int})"/>; without one the paths
/// are preserved under extras.geppetto so the round-trip loses nothing. Extras
/// keys "tool" / "shader" / "compileFlags" mark a tool-texture marker (see
/// MaterialBridge); "metallicMap" / "roughnessMap" hold split ORM maps that share
/// no single metallicRoughnessTexture.
/// </summary>
public sealed class GltfMaterial
{
	public string Name = "Material";
	public Vec3 BaseColor = new( 0.18f, 0.18f, 0.18f );
	public float Opacity = 1f;
	public float Metallic;
	public float Roughness = 0.6f;
	public string BaseColorTexture;
	public string MetallicRoughnessTexture;
	public string NormalTexture;
	public Vec3 Emissive = new( 0f, 0f, 0f );
	public string AlphaMode = "OPAQUE";
	public bool DoubleSided;
	public float Clearcoat;
	public float ClearcoatRoughness = 0.1f;
	public float Ior = 1.5f;
	public bool HasIor;
	public Dictionary<string, string> Extras = new( StringComparer.Ordinal );

	public PbrMaterial ToPbr() => MaterialBridge.FromGltf( this );

	/// <summary>glTF material JSON. <paramref name="textureIndices"/> maps a texture source path to its glTF texture index.</summary>
	public string ToJson( IReadOnlyDictionary<string, int> textureIndices = null )
	{
		var root = new JsonObject { ["name"] = Name };
		var pbr = new JsonObject
		{
			["baseColorFactor"] = new JsonArray( BaseColor.x, BaseColor.y, BaseColor.z, Opacity ),
			["metallicFactor"] = Metallic,
			["roughnessFactor"] = Roughness,
		};
		var geppetto = new JsonObject();
		void Texture( JsonObject parent, string key, string path )
		{
			if ( string.IsNullOrWhiteSpace( path ) ) return;
			if ( textureIndices != null && textureIndices.TryGetValue( path, out int index ) )
				parent[key] = new JsonObject { ["index"] = index };
			else
				geppetto[key == "baseColorTexture" ? "baseColorTexture" : key == "metallicRoughnessTexture" ? "metallicRoughnessTexture" : "normalTexture"] = path;
		}
		Texture( pbr, "baseColorTexture", BaseColorTexture );
		Texture( pbr, "metallicRoughnessTexture", MetallicRoughnessTexture );
		root["pbrMetallicRoughness"] = pbr;
		if ( !string.IsNullOrWhiteSpace( NormalTexture ) )
		{
			if ( textureIndices != null && textureIndices.TryGetValue( NormalTexture, out int normalIndex ) )
				root["normalTexture"] = new JsonObject { ["index"] = normalIndex };
			else
				geppetto["normalTexture"] = NormalTexture;
		}
		if ( !MaterialBridge.IsBlack( Emissive ) )
			root["emissiveFactor"] = new JsonArray( Emissive.x, Emissive.y, Emissive.z );
		if ( AlphaMode != "OPAQUE" )
			root["alphaMode"] = AlphaMode;
		if ( DoubleSided )
			root["doubleSided"] = true;
		var extensions = new JsonObject();
		if ( Clearcoat > 0f )
			extensions["KHR_materials_clearcoat"] = new JsonObject
			{
				["clearcoatFactor"] = Clearcoat,
				["clearcoatRoughnessFactor"] = ClearcoatRoughness,
			};
		if ( HasIor )
			extensions["KHR_materials_ior"] = new JsonObject { ["ior"] = Ior };
		if ( extensions.Count > 0 )
			root["extensions"] = extensions;
		foreach ( var pair in Extras )
			geppetto[pair.Key] = pair.Value;
		if ( geppetto.Count > 0 )
			root["extras"] = new JsonObject { ["geppetto"] = geppetto };
		return root.ToJsonString( new JsonSerializerOptions { WriteIndented = true } );
	}

	/// <summary>Parse glTF material JSON (our own shape or a standard material object). Throws FormatException on malformed JSON.</summary>
	public static GltfMaterial ParseJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) throw new FormatException( "glTF material JSON is empty." );
		JsonDocument doc;
		try { doc = JsonDocument.Parse( json ); }
		catch ( JsonException ex ) { throw new FormatException( "Malformed glTF material JSON: " + ex.Message, ex ); }
		using ( doc )
		{
			JsonElement root = doc.RootElement;
			if ( root.ValueKind != JsonValueKind.Object ) throw new FormatException( "glTF material JSON must be an object." );
			var mat = new GltfMaterial();
			if ( root.TryGetProperty( "name", out JsonElement name ) && name.ValueKind == JsonValueKind.String )
				mat.Name = name.GetString();
			if ( root.TryGetProperty( "pbrMetallicRoughness", out JsonElement pbr ) && pbr.ValueKind == JsonValueKind.Object )
			{
				if ( pbr.TryGetProperty( "baseColorFactor", out JsonElement factor ) && factor.ValueKind == JsonValueKind.Array && factor.GetArrayLength() >= 3 )
				{
					mat.BaseColor = new Vec3( F( factor, 0 ), F( factor, 1 ), F( factor, 2 ) );
					if ( factor.GetArrayLength() >= 4 ) mat.Opacity = F( factor, 3 );
				}
				if ( pbr.TryGetProperty( "metallicFactor", out JsonElement metal ) && metal.ValueKind == JsonValueKind.Number )
					mat.Metallic = (float)metal.GetDouble();
				if ( pbr.TryGetProperty( "roughnessFactor", out JsonElement rough ) && rough.ValueKind == JsonValueKind.Number )
					mat.Roughness = (float)rough.GetDouble();
				mat.BaseColorTexture = TexturePath( pbr, "baseColorTexture" );
				mat.MetallicRoughnessTexture = TexturePath( pbr, "metallicRoughnessTexture" );
			}
			mat.NormalTexture = TexturePath( root, "normalTexture" );
			if ( root.TryGetProperty( "emissiveFactor", out JsonElement emissive ) && emissive.ValueKind == JsonValueKind.Array && emissive.GetArrayLength() >= 3 )
				mat.Emissive = new Vec3( F( emissive, 0 ), F( emissive, 1 ), F( emissive, 2 ) );
			if ( root.TryGetProperty( "alphaMode", out JsonElement alpha ) && alpha.ValueKind == JsonValueKind.String )
				mat.AlphaMode = alpha.GetString();
			if ( root.TryGetProperty( "doubleSided", out JsonElement sided ) && ( sided.ValueKind == JsonValueKind.True || sided.ValueKind == JsonValueKind.False ) )
				mat.DoubleSided = sided.GetBoolean();
			if ( root.TryGetProperty( "extensions", out JsonElement extensions ) && extensions.ValueKind == JsonValueKind.Object )
			{
				if ( extensions.TryGetProperty( "KHR_materials_clearcoat", out JsonElement coat ) && coat.ValueKind == JsonValueKind.Object )
				{
					if ( coat.TryGetProperty( "clearcoatFactor", out JsonElement coatFactor ) && coatFactor.ValueKind == JsonValueKind.Number )
						mat.Clearcoat = (float)coatFactor.GetDouble();
					if ( coat.TryGetProperty( "clearcoatRoughnessFactor", out JsonElement coatRough ) && coatRough.ValueKind == JsonValueKind.Number )
						mat.ClearcoatRoughness = (float)coatRough.GetDouble();
				}
				if ( extensions.TryGetProperty( "KHR_materials_ior", out JsonElement ior ) && ior.ValueKind == JsonValueKind.Object
					&& ior.TryGetProperty( "ior", out JsonElement iorValue ) && iorValue.ValueKind == JsonValueKind.Number )
				{
					mat.Ior = (float)iorValue.GetDouble();
					mat.HasIor = true;
				}
			}
			if ( root.TryGetProperty( "extras", out JsonElement extras ) && extras.ValueKind == JsonValueKind.Object
				&& extras.TryGetProperty( "geppetto", out JsonElement geppetto ) && geppetto.ValueKind == JsonValueKind.Object )
			{
				foreach ( JsonProperty property in geppetto.EnumerateObject() )
				{
					string value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
					switch ( property.Name )
					{
						case "baseColorTexture": mat.BaseColorTexture ??= value; break;
						case "metallicRoughnessTexture": mat.MetallicRoughnessTexture ??= value; break;
						case "normalTexture": mat.NormalTexture ??= value; break;
						default: mat.Extras[property.Name] = value; break;
					}
				}
			}
			return mat;
		}
	}

	static float F( JsonElement array, int index )
	{
		JsonElement item = array[index];
		return item.ValueKind == JsonValueKind.Number ? (float)item.GetDouble() : 0f;
	}

	/// <summary>Texture path for a slot: a standard {"index": n} carries no path (null), anything else is not a path either.</summary>
	static string TexturePath( JsonElement parent, string key ) => null;
}
