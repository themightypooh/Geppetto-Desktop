using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Effigy.Map;

/// <summary>
/// Shared material library (hammer-interop-shared-material-library).
///
/// Map materials live in the same library as every other Desktop material: a
/// material created for a map is usable on a sculpt/CAD body and vice versa, and
/// the VMT/glTF export presets apply to map materials through the bridge.
///
/// ONE LOOK RECORD. A library entry wraps one <see cref="PbrMaterial"/> — the
/// shared record MaterialBridge maps to and from — plus library metadata (name,
/// tags, origin, the Hammer map path, the VMT shader/surfaceprop, extra VMT
/// keys). No second PBR type is defined here, and none is invented for faces:
/// a <see cref="FaceMaterial"/> carries a material NAME string + FaceUv, so the
/// map seam is by name — <see cref="SharedMaterialLibrary.ResolveFace"/> looks
/// the name up in this library. The FaceMaterial -&gt; PbrMaterial adapter itself
/// is STILL UNBUILT and unowned (open integrator decision): a face whose material
/// is not in the library has no PBR look here, and ResolveFace says so with null
/// instead of inventing one.
///
/// IDENTITY. <see cref="SharedMaterialEntry.Name"/> is the library identity and
/// <see cref="SharedMaterialEntry.MapPath"/> is the face/studio identity (the
/// Hammer path a brush face or a <c>PartStudio.MaterialNames</c> slot carries).
/// Both are unique in a library (case-insensitive, slash-normalised), so a name
/// lookup can never be ambiguous between two entries.
///
/// CAD BODIES. Bodies wear a material through a slot name, not through the
/// library: <see cref="SharedMaterialLibrary.BindToStudio"/> hands the entry's
/// map path to <see cref="MaterialDrop.SlotFor"/> (one slot per material, reused)
/// and writes <c>PartStudio.MaterialNames[slot]</c>. Face assignment stays with
/// <see cref="FaceMaterialEdit"/> / <see cref="MaterialDrop"/> — this library
/// binds the name, it does not paint faces.
///
/// EXPORT PRESETS. <see cref="MaterialExportPreset"/> only selects the target
/// (VMT or glTF), the VMT shader/surfaceprop and extra keys; every value mapping
/// lives in <see cref="MaterialBridge"/>, so a preset cannot disagree with the
/// bridge. A tool entry (nodraw, skybox, ...) exports as a tool marker, not a
/// renderable look: VMT via <see cref="ToolTextureInfo.ToVmt"/>, glTF via the
/// bridge's tool marker. The per-engine texture-packing presets in core
/// (TextureSetExport.ExportPreset, EngineExportPresets) operate on baked texture
/// sets, not on this record — composing them with this library is a future
/// integrator step, not duplicated here.
///
/// Kernel-side, engine-free, no UI. JSON import/export mirrors
/// SmartMaterialLibrary: a bad entry is skipped with a named warning, never a
/// silent drop and never a failed import.
/// </summary>
public enum MaterialOrigin
{
	/// <summary>Created for a map (a brush face material).</summary>
	Map,
	/// <summary>Created for a sculpt (paint / smart material side).</summary>
	Sculpt,
	/// <summary>Created for a CAD body (a PartStudio slot material).</summary>
	Cad,
}

/// <summary>VMT text or glTF material JSON — the two things a material export preset can produce.</summary>
public enum MaterialExportTarget
{
	Vmt,
	Gltf,
}

/// <summary>
/// One material in the shared library: one <see cref="PbrMaterial"/> look plus
/// the metadata the library, the map and the exporters need. The entry keeps
/// <see cref="Pbr"/>.Name equal to <see cref="Name"/> so the bridge's exports
/// (which name the glTF material from PbrMaterial.Name) agree with the library.
/// </summary>
public sealed class SharedMaterialEntry
{
	public string Name;
	public PbrMaterial Pbr;
	public string[] Tags = Array.Empty<string>();
	public MaterialOrigin Origin = MaterialOrigin.Map;

	/// <summary>The Hammer-style material path this entry is known by on map faces and studio slots (e.g. "brick/brickwall001"). Defaults to <see cref="Name"/>.</summary>
	public string MapPath;

	/// <summary>VMT shader for VMT export. Defaults to LightmappedGeneric; a preset may override it.</summary>
	public string Shader = MaterialBridge.DefaultShader;

	/// <summary>Optional $surfaceprop for VMT export (e.g. "concrete"). A preset may override it.</summary>
	public string SurfaceProp;

	/// <summary>Extra VMT key-values written on VMT export, after the preset's. Entry keys win over preset keys on collision; both win over the bridge's mapped keys (the bridge's rule).</summary>
	public Dictionary<string, string> VmtExtra = new( StringComparer.OrdinalIgnoreCase );

	public SharedMaterialEntry( string name, PbrMaterial pbr, MaterialOrigin origin = MaterialOrigin.Map )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) throw new ArgumentException( "A shared material needs a name.", nameof( name ) );
		if ( pbr is null ) throw new ArgumentNullException( nameof( pbr ) );
		Name = name.Trim();
		Pbr = pbr;
		Pbr.Name = Name;
		Origin = origin;
		MapPath = Name;
	}

	/// <summary>The tool texture this entry is, or null for an ordinary material. Derived from <see cref="MapPath"/> through the bridge, so it cannot disagree with the FaceMaterial tool registry.</summary>
	public ToolTextureInfo Tool => MaterialBridge.ToolTexture( MapPath );

	/// <summary>True when this entry is a tool texture (nodraw, skybox, trigger, ...).</summary>
	public bool IsTool => Tool is not null;

	/// <summary>This entry as a brush-face assignment: the map path plus <paramref name="uv"/>. The face's look is this entry's PBR, resolved by name through the library.</summary>
	public FaceMaterial ToFaceMaterial( FaceUv uv ) => new( MapPath, uv );

	/// <summary>This entry as a brush-face assignment with a default UV.</summary>
	public FaceMaterial ToFaceMaterial() => ToFaceMaterial( FaceUv.Default );

	/// <summary>VMT export through the default Source VMT preset (LightmappedGeneric, this entry's shader/surfaceprop when the preset leaves them unset).</summary>
	public VmtMaterial ToVmt() => MaterialExportPreset.SourceVmt.ToVmt( this );

	/// <summary>glTF export through the default glTF preset.</summary>
	public GltfMaterial ToGltf() => MaterialExportPreset.GltfPbr.ToGltf( this );

	/// <summary>JSON form: {"name","tags","origin","mapPath","shader","surfaceProp","vmtExtra","pbr":{...}}.</summary>
	public JsonObject ToJson()
	{
		var json = new JsonObject
		{
			["name"] = Name,
			["origin"] = Origin.ToString(),
			["mapPath"] = MapPath,
			["shader"] = Shader,
		};
		if ( Tags.Length > 0 )
		{
			// (JsonNode) cast, not Add(string): JsonArray.Add(string) stores the
			// value as a JsonValueCustomized, whose WriteTo with custom
			// JsonSerializerOptions throws "must specify a TypeInfoResolver"
			// (.NET 8); the cast uses the string -> JsonNode conversion, which
			// writes fine under the same options.
			var tags = new JsonArray();
			foreach ( var tag in Tags ) tags.Add( (JsonNode)tag );
			json["tags"] = tags;
		}
		if ( !string.IsNullOrWhiteSpace( SurfaceProp ) ) json["surfaceProp"] = SurfaceProp;
		if ( VmtExtra.Count > 0 )
		{
			var extra = new JsonObject();
			foreach ( var pair in VmtExtra ) extra[pair.Key] = pair.Value;
			json["vmtExtra"] = extra;
		}
		json["pbr"] = PbrToJson();
		return json;
	}

	JsonObject PbrToJson()
	{
		var pbr = new JsonObject
		{
			["name"] = Pbr.Name,
			["baseColor"] = new JsonArray( (double)Pbr.BaseColor.x, (double)Pbr.BaseColor.y, (double)Pbr.BaseColor.z ),
			["metallic"] = (double)Pbr.Metallic,
			["roughness"] = (double)Pbr.Roughness,
			["opacity"] = (double)Pbr.Opacity,
			["emissive"] = new JsonArray( (double)Pbr.Emissive.x, (double)Pbr.Emissive.y, (double)Pbr.Emissive.z ),
			["clearcoat"] = (double)Pbr.Clearcoat,
			["clearcoatRoughness"] = (double)Pbr.ClearcoatRoughness,
			["ior"] = (double)Pbr.Ior,
		};
		if ( !string.IsNullOrWhiteSpace( Pbr.BaseColorMap ) ) pbr["baseColorMap"] = Pbr.BaseColorMap;
		if ( !string.IsNullOrWhiteSpace( Pbr.MetallicMap ) ) pbr["metallicMap"] = Pbr.MetallicMap;
		if ( !string.IsNullOrWhiteSpace( Pbr.RoughnessMap ) ) pbr["roughnessMap"] = Pbr.RoughnessMap;
		if ( !string.IsNullOrWhiteSpace( Pbr.NormalMap ) ) pbr["normalMap"] = Pbr.NormalMap;
		if ( !string.IsNullOrWhiteSpace( Pbr.OpacityMap ) ) pbr["opacityMap"] = Pbr.OpacityMap;
		if ( !string.IsNullOrWhiteSpace( Pbr.EmissiveMap ) ) pbr["emissiveMap"] = Pbr.EmissiveMap;
		return pbr;
	}

	public string ToJsonString() => ToJson().ToJsonString();

	/// <summary>Parse <see cref="ToJson"/> output. A missing "pbr" keeps the PbrMaterial defaults; a missing or blank "name", or an unknown "origin", throws naming the problem.</summary>
	public static SharedMaterialEntry FromJson( JsonObject json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		string name = Str( json, "name" );
		if ( string.IsNullOrWhiteSpace( name ) ) throw new ArgumentException( "Shared material JSON is missing 'name'." );
		var origin = MaterialOrigin.Map;
		string originText = Str( json, "origin" );
		if ( !string.IsNullOrWhiteSpace( originText ) && !Enum.TryParse<MaterialOrigin>( originText, true, out origin ) )
			throw new ArgumentException( $"Shared material '{name}' has unknown origin '{originText}'." );
		var pbr = PbrFromJson( json["pbr"] as JsonObject, name );
		var entry = new SharedMaterialEntry( name, pbr, origin );
		string mapPath = Str( json, "mapPath" );
		if ( !string.IsNullOrWhiteSpace( mapPath ) ) entry.MapPath = mapPath.Trim();
		string shader = Str( json, "shader" );
		if ( !string.IsNullOrWhiteSpace( shader ) ) entry.Shader = shader.Trim();
		string surfaceProp = Str( json, "surfaceProp" );
		if ( !string.IsNullOrWhiteSpace( surfaceProp ) ) entry.SurfaceProp = surfaceProp.Trim();
		if ( json["tags"] is JsonArray tags )
		{
			var list = new List<string>();
			foreach ( var tag in tags )
			{
				string text;
				try { text = tag?.GetValue<string>(); }
				catch ( Exception ex ) { throw new ArgumentException( $"Shared material '{name}': a tag is not a string.", ex ); }
				if ( !string.IsNullOrWhiteSpace( text ) ) list.Add( text );
			}
			entry.Tags = list.ToArray();
		}
		if ( json["vmtExtra"] is JsonObject extra )
		{
			foreach ( var pair in extra )
			{
				string value;
				try { value = pair.Value?.GetValue<string>(); }
				catch ( Exception ex ) { throw new ArgumentException( $"Shared material '{name}': vmtExtra '{pair.Key}' is not a string.", ex ); }
				if ( value != null ) entry.VmtExtra[pair.Key] = value;
			}
		}
		return entry;
	}

	public static SharedMaterialEntry FromJsonString( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) throw new ArgumentException( "Shared material JSON text is blank." );
		if ( JsonNode.Parse( json ) is not JsonObject obj ) throw new ArgumentException( "Shared material JSON text is not an object." );
		return FromJson( obj );
	}

	static PbrMaterial PbrFromJson( JsonObject json, string name )
	{
		var pbr = new PbrMaterial { Name = name };
		if ( json is null ) return pbr;
		pbr.BaseColor = Vec( json, "baseColor", pbr.BaseColor );
		pbr.BaseColorMap = Str( json, "baseColorMap" );
		pbr.Metallic = Num( json, "metallic", pbr.Metallic );
		pbr.MetallicMap = Str( json, "metallicMap" );
		pbr.Roughness = Num( json, "roughness", pbr.Roughness );
		pbr.RoughnessMap = Str( json, "roughnessMap" );
		pbr.NormalMap = Str( json, "normalMap" );
		pbr.Opacity = Num( json, "opacity", pbr.Opacity );
		pbr.OpacityMap = Str( json, "opacityMap" );
		pbr.Emissive = Vec( json, "emissive", pbr.Emissive );
		pbr.EmissiveMap = Str( json, "emissiveMap" );
		pbr.Clearcoat = Num( json, "clearcoat", pbr.Clearcoat );
		pbr.ClearcoatRoughness = Num( json, "clearcoatRoughness", pbr.ClearcoatRoughness );
		pbr.Ior = Num( json, "ior", pbr.Ior );
		return pbr;
	}

	static float Num( JsonObject json, string key, float fallback )
	{
		if ( json[key] is null ) return fallback;
		try { return (float)json[key]!.GetValue<double>(); }
		catch ( Exception ex ) { throw new ArgumentException( $"Shared material JSON: '{key}' is not a number.", ex ); }
	}

	static Vec3 Vec( JsonObject json, string key, Vec3 fallback )
	{
		if ( json[key] is not JsonArray pair || pair.Count != 3 ) return fallback;
		try { return new Vec3( (float)pair[0]!.GetValue<double>(), (float)pair[1]!.GetValue<double>(), (float)pair[2]!.GetValue<double>() ); }
		catch ( Exception ex ) { throw new ArgumentException( $"Shared material JSON: '{key}' is not a [r, g, b] triple.", ex ); }
	}

	static string Str( JsonObject json, string key )
	{
		if ( json[key] is null ) return null;
		try { return json[key]!.GetValue<string>(); }
		catch ( Exception ex ) { throw new ArgumentException( $"Shared material JSON: '{key}' is not a string.", ex ); }
	}
}

/// <summary>
/// One named export preset for shared materials: VMT or glTF, honouring the
/// bridge mappings. The preset only selects the target, the VMT shader /
/// surfaceprop and extra VMT keys — every value mapping lives in
/// <see cref="MaterialBridge"/>, so a preset cannot disagree with the bridge.
/// A null Shader/SurfaceProp means "this entry's own", so one preset serves
/// entries with different shaders; the builtins pin the shader for the cases
/// where the target fixes it (water, sky).
/// </summary>
public sealed class MaterialExportPreset
{
	public string Id;
	public string DisplayName;
	public MaterialExportTarget Target;

	/// <summary>VMT shader; null means the entry's own <see cref="SharedMaterialEntry.Shader"/>.</summary>
	public string Shader;

	/// <summary>VMT $surfaceprop; null means the entry's own <see cref="SharedMaterialEntry.SurfaceProp"/>.</summary>
	public string SurfaceProp;

	/// <summary>Extra VMT key-values for this preset. An entry's own <see cref="SharedMaterialEntry.VmtExtra"/> wins on collision.</summary>
	public Dictionary<string, string> Extra = new( StringComparer.OrdinalIgnoreCase );

	public MaterialExportPreset( string id, string displayName, MaterialExportTarget target, string shader = null, string surfaceProp = null )
	{
		if ( string.IsNullOrWhiteSpace( id ) ) throw new ArgumentException( "An export preset needs an id.", nameof( id ) );
		if ( string.IsNullOrWhiteSpace( displayName ) ) throw new ArgumentException( "An export preset needs a display name.", nameof( displayName ) );
		Id = id.Trim();
		DisplayName = displayName.Trim();
		Target = target;
		Shader = string.IsNullOrWhiteSpace( shader ) ? null : shader.Trim();
		SurfaceProp = string.IsNullOrWhiteSpace( surfaceProp ) ? null : surfaceProp.Trim();
	}

	/// <summary>Source VMT: the entry's own shader (LightmappedGeneric unless the entry says otherwise) and surfaceprop.</summary>
	public static readonly MaterialExportPreset SourceVmt = new( "source-vmt", "Source VMT", MaterialExportTarget.Vmt );

	/// <summary>Source VMT water: Water shader, $surfaceprop water.</summary>
	public static readonly MaterialExportPreset SourceVmtWater = new( "source-vmt-water", "Source VMT water", MaterialExportTarget.Vmt, MaterialBridge.WaterShader, "water" );

	/// <summary>Source VMT skybox: Sky shader.</summary>
	public static readonly MaterialExportPreset SourceVmtSky = new( "source-vmt-sky", "Source VMT skybox", MaterialExportTarget.Vmt, MaterialBridge.SkyShader );

	/// <summary>glTF 2.0 PBR.</summary>
	public static readonly MaterialExportPreset GltfPbr = new( "gltf-pbr", "glTF 2.0 PBR", MaterialExportTarget.Gltf );

	public static IReadOnlyList<MaterialExportPreset> All => new[] { SourceVmt, SourceVmtWater, SourceVmtSky, GltfPbr };

	/// <summary>The preset with this id (case-insensitive), or null when there is none.</summary>
	public static MaterialExportPreset ById( string id )
	{
		if ( string.IsNullOrWhiteSpace( id ) ) return null;
		foreach ( var preset in All )
			if ( string.Equals( preset.Id, id.Trim(), StringComparison.OrdinalIgnoreCase ) ) return preset;
		return null;
	}

	/// <summary>Named defects, never a silent fix: a blank id or display name, or a target that is not VMT or glTF. Empty means the preset is usable.</summary>
	public IReadOnlyList<string> Validate()
	{
		var defects = new List<string>();
		if ( string.IsNullOrWhiteSpace( Id ) ) defects.Add( "Export preset id is blank." );
		if ( string.IsNullOrWhiteSpace( DisplayName ) ) defects.Add( $"Export preset '{Id}' has a blank display name." );
		if ( Target != MaterialExportTarget.Vmt && Target != MaterialExportTarget.Gltf )
			defects.Add( $"Export preset '{Id}' has unknown target '{Target}'." );
		return defects;
	}

	/// <summary>
	/// VMT export through this preset. Throws <see cref="InvalidOperationException"/> when this preset is a glTF preset — a VMT export from a glTF preset has no answer, and a null would hide it.
	/// A tool entry exports as its tool VMT (%tooltexture + compile flags); for a skybox the entry's BaseColorMap is the sky name when set, else the bridge default.
	/// </summary>
	public VmtMaterial ToVmt( SharedMaterialEntry entry )
	{
		if ( entry is null ) throw new ArgumentNullException( nameof( entry ) );
		if ( Target != MaterialExportTarget.Vmt )
			throw new InvalidOperationException( $"Preset '{Id}' exports glTF, not VMT." );
		if ( entry.Tool is ToolTextureInfo tool )
		{
			string skyName = tool.Kind == ToolTextureKind.Skybox && !string.IsNullOrWhiteSpace( entry.Pbr.BaseColorMap )
				? entry.Pbr.BaseColorMap : null;
			return tool.ToVmt( skyName );
		}
		var extra = new List<KeyValuePair<string, string>>();
		foreach ( var pair in Extra ) extra.Add( pair );
		foreach ( var pair in entry.VmtExtra ) extra.Add( pair );
		return MaterialBridge.ToVmt( entry.Pbr, Shader ?? entry.Shader, SurfaceProp ?? entry.SurfaceProp, extra );
	}

	/// <summary>
	/// glTF export through this preset. Throws <see cref="InvalidOperationException"/> when this preset is a VMT preset. A tool entry exports as the bridge's tool marker, not a renderable look.
	/// </summary>
	public GltfMaterial ToGltf( SharedMaterialEntry entry )
	{
		if ( entry is null ) throw new ArgumentNullException( nameof( entry ) );
		if ( Target != MaterialExportTarget.Gltf )
			throw new InvalidOperationException( $"Preset '{Id}' exports VMT, not glTF." );
		return MaterialBridge.ToGltf( entry.Pbr, entry.Tool );
	}

	/// <summary>The export as text: canonical VMT text for a VMT preset, glTF material JSON for a glTF preset.</summary>
	public string ExportText( SharedMaterialEntry entry )
	{
		if ( entry is null ) throw new ArgumentNullException( nameof( entry ) );
		return Target == MaterialExportTarget.Vmt
			? ToVmt( entry ).ToText()
			: MaterialBridge.ToGltfJson( entry.Pbr, null, entry.Tool );
	}

	/// <summary>The file name this export would be written as: the entry's map path plus ".vmt" or ".gltf-material.json". Subdirectories are kept — they are meaningful in Source paths.</summary>
	public string ExportFileName( SharedMaterialEntry entry )
	{
		if ( entry is null ) throw new ArgumentNullException( nameof( entry ) );
		string path = ( entry.MapPath ?? entry.Name ).Trim().Replace( '\\', '/' ).TrimStart( '/' );
		return Target == MaterialExportTarget.Vmt ? path + ".vmt" : path + ".gltf-material.json";
	}
}

/// <summary>
/// The shared Desktop material library: map materials registered with the same
/// entries as sculpt/CAD materials, searchable by name/tags, exportable through
/// the bridge presets. One entry per name and one per map path (case-insensitive,
/// slash-normalised) so a lookup by either can never be ambiguous.
///
/// Search mirrors SmartMaterialLibrary: a blank query returns everything, otherwise
/// a case-insensitive substring match on name, map path or any tag.
/// </summary>
public sealed class SharedMaterialLibrary
{
	public List<SharedMaterialEntry> Entries { get; } = new();

	public int Count => Entries.Count;

	static string Norm( string value ) =>
		string.IsNullOrWhiteSpace( value ) ? null : value.Trim().Replace( '\\', '/' ).ToLowerInvariant();

	/// <summary>Add an entry. A duplicate name or a duplicate map path throws, naming the collision — the library cannot resolve a face to two entries.</summary>
	public SharedMaterialEntry Add( SharedMaterialEntry entry )
	{
		if ( entry is null ) throw new ArgumentNullException( nameof( entry ) );
		if ( string.IsNullOrWhiteSpace( entry.Name ) ) throw new ArgumentException( "A shared material needs a name.", nameof( entry ) );
		if ( string.IsNullOrWhiteSpace( entry.MapPath ) ) throw new ArgumentException( $"Shared material '{entry.Name}' needs a map path.", nameof( entry ) );
		if ( entry.Pbr is null ) throw new ArgumentNullException( nameof( entry ) );
		entry.Pbr.Name = entry.Name;
		string nameKey = Norm( entry.Name );
		string pathKey = Norm( entry.MapPath );
		foreach ( var existing in Entries )
		{
			if ( Norm( existing.Name ) == nameKey || Norm( existing.MapPath ) == nameKey )
				throw new ArgumentException( $"Shared material '{entry.Name}' is already in this library as '{existing.Name}'." );
			if ( Norm( existing.Name ) == pathKey || Norm( existing.MapPath ) == pathKey )
				throw new ArgumentException( $"Shared material '{entry.Name}' uses map path '{entry.MapPath}', which '{existing.Name}' already uses." );
		}
		Entries.Add( entry );
		return entry;
	}

	/// <summary>Create an entry from a PBR look and add it. <paramref name="mapPath"/> defaults to the PBR name; <paramref name="shader"/>/<paramref name="surfaceProp"/> default to the entry defaults.</summary>
	public SharedMaterialEntry Add( PbrMaterial pbr, MaterialOrigin origin, string[] tags = null, string mapPath = null, string shader = null, string surfaceProp = null )
	{
		if ( pbr is null ) throw new ArgumentNullException( nameof( pbr ) );
		if ( string.IsNullOrWhiteSpace( pbr.Name ) ) throw new ArgumentException( "A shared material needs a name; set PbrMaterial.Name.", nameof( pbr ) );
		var entry = new SharedMaterialEntry( pbr.Name, pbr, origin )
		{
			Tags = tags ?? Array.Empty<string>(),
			Shader = string.IsNullOrWhiteSpace( shader ) ? MaterialBridge.DefaultShader : shader.Trim(),
			SurfaceProp = string.IsNullOrWhiteSpace( surfaceProp ) ? null : surfaceProp.Trim(),
		};
		if ( !string.IsNullOrWhiteSpace( mapPath ) ) entry.MapPath = mapPath.Trim();
		return Add( entry );
	}

	/// <summary>The entry with this name or map path (case-insensitive, slashes normalised), or null when there is none.</summary>
	public SharedMaterialEntry Get( string name )
	{
		string key = Norm( name );
		if ( key is null ) return null;
		foreach ( var entry in Entries )
			if ( Norm( entry.Name ) == key || Norm( entry.MapPath ) == key ) return entry;
		return null;
	}

	/// <summary>Entries whose name, map path or any tag contains <paramref name="query"/> (case-insensitive). A blank query returns everything, in insertion order.</summary>
	public List<SharedMaterialEntry> Search( string query )
	{
		if ( string.IsNullOrWhiteSpace( query ) ) return Entries.ToList();
		string q = query.Trim().ToLowerInvariant();
		return Entries.Where( e =>
			( e.Name ?? "" ).ToLowerInvariant().Contains( q, StringComparison.Ordinal ) ||
			( e.MapPath ?? "" ).ToLowerInvariant().Contains( q, StringComparison.Ordinal ) ||
			e.Tags.Any( t => ( t ?? "" ).ToLowerInvariant().Contains( q, StringComparison.Ordinal ) ) ).ToList();
	}

	/// <summary>Remove the entry with this name or map path. False when there is none.</summary>
	public bool Remove( string name )
	{
		var entry = Get( name );
		return entry != null && Entries.Remove( entry );
	}

	/// <summary>
	/// The library entry a brush face's material resolves to, by name — the map
	/// seam. Null when the face is null or its material is not in the library:
	/// that face has no PBR look until it is registered (or the unbuilt
	/// FaceMaterial -&gt; PbrMaterial adapter lands), and null says so instead of
	/// inventing one.
	/// </summary>
	public SharedMaterialEntry ResolveFace( FaceMaterial face ) => face is null ? null : Get( face.MaterialName );

	/// <summary>The PBR look for a brush face, or false when the face's material is not in the library. <paramref name="pbr"/> is null on false.</summary>
	public bool TryResolvePbr( FaceMaterial face, out PbrMaterial pbr )
	{
		pbr = ResolveFace( face )?.Pbr;
		return pbr != null;
	}

	/// <summary>
	/// Use this entry on a CAD body: bind its map path to a studio slot. The slot
	/// comes from <see cref="MaterialDrop.SlotFor"/> (one slot per material, reused,
	/// lowest free from 1), and the name is written to
	/// <c>PartStudio.MaterialNames[slot]</c> so <c>NameForSlot</c> and the exporters
	/// see it. Returns the slot, or -1 when all 63 slots are spoken for. Face
	/// assignment itself stays with FaceMaterialEdit / MaterialDrop.
	/// </summary>
	public int BindToStudio( PartStudio studio, SharedMaterialEntry entry )
	{
		if ( studio is null ) throw new ArgumentNullException( nameof( studio ) );
		if ( entry is null ) throw new ArgumentNullException( nameof( entry ) );
		int slot = MaterialDrop.SlotFor( studio, entry.MapPath );
		if ( slot < 0 ) return -1;
		studio.MaterialNames[slot] = entry.MapPath;
		return slot;
	}

	/// <summary>
	/// Unbind an entry from a studio: retire its slot name, but only when no face
	/// still wears the slot — retiring a worn slot would leave faces pointing at a
	/// name that no longer exists. False when the entry is not bound, or when a
	/// face still wears it.
	/// </summary>
	public bool UnbindFromStudio( PartStudio studio, SharedMaterialEntry entry )
	{
		if ( studio is null ) throw new ArgumentNullException( nameof( studio ) );
		if ( entry is null ) throw new ArgumentNullException( nameof( entry ) );
		int slot = MaterialDrop.SlotCarrying( studio, entry.MapPath );
		if ( slot < 0 ) return false;
		bool worn = studio.Features.OfType<FaceMaterialFeature>().Any( f => !f.Suppressed && f.Material.Clamped == slot );
		if ( worn ) return false;
		return studio.MaterialNames.Remove( slot );
	}

	/// <summary>JSON form: a list of entry objects (see <see cref="SharedMaterialEntry.ToJson"/>).</summary>
	public string ExportJson()
	{
		var array = new JsonArray();
		foreach ( var entry in Entries ) array.Add( entry.ToJson() );
		return array.ToJsonString( new JsonSerializerOptions { WriteIndented = true } );
	}

	/// <summary>
	/// Import a library. A malformed document or a non-list throws; a single bad
	/// entry is skipped with one warning naming the problem, and the rest still
	/// import — the same rule as SmartMaterialLibrary.
	/// </summary>
	public static (SharedMaterialLibrary Library, List<string> Warnings) ImportJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) throw new ArgumentException( "Nothing to import.", nameof( json ) );
		JsonNode node;
		try { node = JsonNode.Parse( json ); }
		catch ( JsonException ex ) { throw new ArgumentException( "Shared material library JSON is malformed: " + ex.Message, ex ); }
		if ( node is not JsonArray array ) throw new ArgumentException( "Shared material library JSON did not contain a list." );
		var library = new SharedMaterialLibrary();
		var warnings = new List<string>();
		foreach ( var item in array )
		{
			try
			{
				if ( item is not JsonObject obj ) throw new ArgumentException( "A library entry is not an object." );
				library.Add( SharedMaterialEntry.FromJson( obj ) );
			}
			catch ( Exception ex ) { warnings.Add( "Skipped a library entry: " + ex.Message ); }
		}
		return (library, warnings);
	}
}
