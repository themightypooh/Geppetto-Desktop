// Instances as collaboration — shared instance libraries, name fix-up,
// IO proxy, per-map compile settings in version control.
// Board: hammer-instances-collaboration-libraries (Geppetto Desktop, Hammer P3).
// Report §6 P3-5: instances as a collaboration feature. NATIVE Geppetto:
// instance libraries are versioned map documents; no Hammer app, no
// Hammer++/VBSP/VVIS/VRAD, no shelling out to any external compiler.
//
// SCOPE (this file only — the linked-groups mechanics live in
// LinkedGroups.cs, worker 34, and are reused, not reimplemented):
//   * Shared instance libraries: a library is a versioned set of sub-map
//     documents (InstanceLibraryManifest) plus a registry
//     (InstanceLibraryRegistry) that versions them and refuses downgrades.
//   * Name fix-up across library updates: LibraryNameFixup prefixes every
//     targetname in a resolved sub-map (InstanceAuto-style: "<prefix>_<name>")
//     and rewrites every intra-library target reference to match. The host
//     map's own names are NEVER renamed — a collision on the incoming side
//     gets a numeric suffix. A resolve records its original->resolved name
//     map on the LibraryInstance, and UpdateInstance reuses it, so updating
//     a library re-resolves with prefixed names intact. When the author
//     RENAMES a target between versions, UpdateInstance takes a rename map
//     (old -> new): the renamed target keeps its old resolved name, stale
//     references inside the new sub-map are rewritten, and proxies follow
//     via LibraryIoProxy.RemapRenames with the same map.
//   * IO proxy for cross-map I/O: LibraryIoProxy is the host-side entity
//     that forwards events into a library instance (forward routes) and
//     out of it (backward routes). Resolved rows are LibraryIoOutput, the
//     same shape as EntityIoOutput, and forward rows serialise to VMF
//     connections lines ("Event" "Target,Input,Parameter,Delay,Times").
//   * Per-map compile settings stored in the map document
//     (MapCompileSettings, "compile_settings" section, version 1):
//     preset + visibility / lightmap overrides + compiler limits, as plain
//     diffable JSON. Output paths are deliberately NOT stored here — they
//     are machine-local, not version-controlled.
//
// DEPENDENCY STATUS (2026-11-13, verified against the live tree, updated
// by the carry-on worker daxter-hammer-instances). MapInstances
// (hammer-sprinkles-prefabs-instances, worker 59, done) and MapDocument
// (hammer-map-document-model, worker 12, in-progress) HAVE landed since
// this file was written, so the "do not exist yet" note below is stale
// history. This file stays SELF-CONTAINED (plain data, following the
// LinkedGroups precedent) and references no type from either ticket; the
// parallel types are the final integrator's reconciliation, NOT resolved
// here — LibrarySubMap vs MapInstances' SubMapContent, LibraryNameFixup
// vs InstanceNameFixUp, and Effigy.Map.MapCompileSettings vs
// Effigy.MapCompileSettings (MapDocument's, persisted as "op set-compile"
// in the .gmap file). Two real collisions found and fixed in this file:
// (1) InstanceIoProxy duplicated MapInstances' type in the same namespace
// and is now LibraryIoProxy (and InstanceIoProxySet is LibraryIoProxySet);
// the "instance_io_proxies" section name is unchanged. (2) The tests use
// Effigy.Map.MapCompileSettings fully qualified, because the bare name is
// ambiguous with Effigy.MapCompileSettings in the tests project.
// The seams are explicit and narrow:
//   * MapInstances: its instance reference gains a LibraryInstance twin —
//     LibraryInstance.LibraryName + EntryName + Prefix is the
//     library-backed form of MapInstances' sub-map path + name-prefix
//     fix-up, and LibraryNameFixup is the shared fix-up both call.
//     LibrarySubMap remains the sub-map content model: the
//     instance-relevant subset (entities + brushes + IO outputs) of a map
//     document, with an adapter to the full MapDocument when it lands.
//   * MapDocument: embeds the JSON sections verbatim —
//     InstanceLibraryRegistry.ToJson as "instance_libraries",
//     LibraryInstanceSet.ToJson as "library_instances",
//     LibraryIoProxySet.ToJson as "instance_io_proxies", and
//     MapCompileSettings.ToJson as "compile_settings" (the compile-settings
//     slot the map-document ticket already names in its scope).
//   * CompileRunner (LANDED): MapCompileSettings.Preset is the real
//     Effigy.CompilePreset and VisibilityOverride the real
//     VisibilityQuality; Stages()/EffectiveVisibility()/
//     EffectiveLightmapResolution() mirror CompilePresetDef.For() exactly
//     (Quick Play = BSP-only; Preview = BSP + fast visibility + 4-texel
//     lightmap; Final = BSP + full visibility + 16-texel lightmap), and
//     the limits mirror CompileOptions.Validate. A runner call takes the
//     preset + overrides from here; this file never runs a compile.
//   * EntityIo (LANDED): LibraryIoOutput mirrors EntityIoOutput field for
//     field (SourceEntity is a targetname, or "#<id>" when unnamed); a
//     resolved proxy row converts 1:1, and ToVmfLine uses EntityIo's
//     connections-line format. Target resolution is case-insensitive and
//     '!' specials are never rewritten, mirroring EntityIo/MapEntities.
//   * LinkedGroups (LANDED): a linked group is a different concept —
//     copies of one definition inside one map. A library instance may
//     PLACE linked groups (a sub-map whose members include a linked-group
//     definition id is carried as an entity keyvalue, not expanded here);
//     this file never touches LinkedGroupSet.
//
// RESULTS ARE DATA, NOT EXCEPTIONS. A refused operation returns a result
// with Ok false and a machine-readable Reason slug plus a human Message.
// FromJson throws ArgumentException naming the bad field (the EntityIo /
// LinkedGroups pattern) because a corrupt document is a load error, not a
// refusal. Resolve/Update never mutate the input sub-map (deep copies) and
// never rename a host entity.
//
// NO DESKTOP UI. Backend / data-model / API / headless only, per Pooh
// 2026-11-13.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Effigy.Map;

/// <summary>Library / sub-map version: 1-3 dot-separated non-negative integers ("1.2.3"). No pre-release or build suffixes.</summary>
public static class InstanceLibraryVersion
{
	/// <summary>True when <paramref name="version"/> parses as major[.minor[.patch]].</summary>
	public static bool IsValid( string? version ) => TryParse( version, out _, out _, out _ );

	/// <summary>Parses "1", "1.2" or "1.2.3" (missing parts are 0). False for blank, >3 parts, non-digits, or a negative part.</summary>
	public static bool TryParse( string? version, out int major, out int minor, out int patch )
	{
		major = minor = patch = 0;
		if ( string.IsNullOrWhiteSpace( version ) ) return false;
		string[] parts = version.Trim().Split( '.' );
		if ( parts.Length < 1 || parts.Length > 3 ) return false;
		int[] values = new int[3];
		for ( int i = 0; i < parts.Length; i++ )
		{
			string part = parts[i];
			if ( part.Length == 0 ) return false;
			foreach ( char c in part ) if ( c < '0' || c > '9' ) return false;
			if ( !int.TryParse( part, NumberStyles.None, CultureInfo.InvariantCulture, out values[i] ) ) return false;
		}
		major = values[0]; minor = values[1]; patch = values[2];
		return true;
	}

	/// <summary>Orders versions numerically: 1.10.0 &gt; 1.9.9, 2.0.0 &gt; 1.99.99. Returns 0 when equal (including "1.2" vs "1.2.0"). Throws ArgumentException for an invalid version.</summary>
	public static int Compare( string a, string b )
	{
		if ( !TryParse( a, out int aMajor, out int aMinor, out int aPatch ) )
			throw new ArgumentException( $"Invalid library version '{a}': expected major[.minor[.patch]] numbers.", nameof( a ) );
		if ( !TryParse( b, out int bMajor, out int bMinor, out int bPatch ) )
			throw new ArgumentException( $"Invalid library version '{b}': expected major[.minor[.patch]] numbers.", nameof( b ) );
		if ( aMajor != bMajor ) return aMajor.CompareTo( bMajor );
		if ( aMinor != bMinor ) return aMinor.CompareTo( bMinor );
		return aPatch.CompareTo( bPatch );
	}

	/// <summary>Normalised "major.minor.patch" form ("1.2" -&gt; "1.2.0"). Throws ArgumentException for an invalid version.</summary>
	public static string Normalise( string version )
	{
		if ( !TryParse( version, out int major, out int minor, out int patch ) )
			throw new ArgumentException( $"Invalid library version '{version}': expected major[.minor[.patch]] numbers.", nameof( version ) );
		return $"{major}.{minor}.{patch}";
	}
}

/// <summary>Base result for instances-collaboration operations: data, not exceptions.</summary>
public class InstancesCollabResult
{
	public bool Ok { get; protected set; }
	public string Reason { get; protected set; } = "";
	public string Message { get; protected set; } = "";

	protected InstancesCollabResult() { }

	public static InstancesCollabResult Success() => new() { Ok = true };
	public static InstancesCollabResult Failure( string reason, string message ) => new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>One sub-map document inside a library: its name, project-relative path and version.</summary>
public sealed class LibraryEntry
{
	/// <summary>Entry name, unique within its manifest (e.g. "room_small").</summary>
	public string Name { get; set; } = "";

	/// <summary>Project-relative path to the sub-map document, under the library path (forward slashes, e.g. "libraries/room_kit/room_small.gmap").</summary>
	public string Path { get; set; } = "";

	/// <summary>Version of this sub-map document ("1.0.0").</summary>
	public string Version { get; set; } = "";

	public LibraryEntry Clone() => new() { Name = Name, Path = Path, Version = Version };
}

/// <summary>
/// The manifest of one shared instance library: name, version, path, and
/// the versioned sub-map documents it contains. The manifest file is the
/// version-controlled record of what a library version holds; the registry
/// decides which version wins.
/// </summary>
public sealed class InstanceLibraryManifest
{
	/// <summary>Library name (e.g. "room_kit"). Unique across the registry.</summary>
	public string Name { get; set; } = "";

	/// <summary>Library version ("1.1.0").</summary>
	public string Version { get; set; } = "";

	/// <summary>Project-relative directory holding the library's sub-map documents (forward slashes, e.g. "libraries/room_kit").</summary>
	public string Path { get; set; } = "";

	/// <summary>The sub-map documents in this library version, in manifest order.</summary>
	public List<LibraryEntry> Entries { get; } = new();

	/// <summary>Entry by name (case-sensitive — entry names are file identities), or null.</summary>
	public LibraryEntry? FindEntry( string name ) =>
		Entries.FirstOrDefault( e => string.Equals( e.Name, name, StringComparison.Ordinal ) );

	public InstanceLibraryManifest Clone()
	{
		var copy = new InstanceLibraryManifest { Name = Name, Version = Version, Path = Path };
		foreach ( var e in Entries ) copy.Entries.Add( e.Clone() );
		return copy;
	}

	/// <summary>
	/// Null when this manifest is valid, otherwise a named error. Rules: a
	/// non-blank name with no whitespace, ',' , '"' , '/' or '\'; a valid
	/// version; a project-relative path (forward slashes, no ".." segment);
	/// entries with unique non-blank names, valid versions, project-relative
	/// paths contained under this library's path.
	/// </summary>
	public string? Validate()
	{
		if ( string.IsNullOrWhiteSpace( Name ) ) return "library name is empty";
		if ( Name.Any( c => char.IsWhiteSpace( c ) || c is ',' or '"' or '/' or '\\' ) )
			return $"library name '{Name}' must not contain whitespace, ',', '\"', '/' or '\\'";
		if ( !InstanceLibraryVersion.IsValid( Version ) )
			return $"library '{Name}' has invalid version '{Version}': expected major[.minor[.patch]] numbers";
		string? pathError = ValidateRelativePath( Path, $"library '{Name}'" );
		if ( pathError is not null ) return pathError;
		var seen = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var entry in Entries )
		{
			if ( entry is null ) return $"library '{Name}' has a null entry";
			if ( string.IsNullOrWhiteSpace( entry.Name ) ) return $"library '{Name}' has an entry with an empty name";
			if ( !seen.Add( entry.Name ) ) return $"library '{Name}' has duplicate entry name '{entry.Name}'";
			if ( !InstanceLibraryVersion.IsValid( entry.Version ) )
				return $"library '{Name}' entry '{entry.Name}' has invalid version '{entry.Version}'";
			string? entryPathError = ValidateRelativePath( entry.Path, $"library '{Name}' entry '{entry.Name}'" );
			if ( entryPathError is not null ) return entryPathError;
			if ( !entry.Path.StartsWith( Path + "/", StringComparison.Ordinal ) )
				return $"library '{Name}' entry '{entry.Name}' path '{entry.Path}' is not under the library path '{Path}'";
		}
		return null;
	}

	/// <summary>Null when <paramref name="path"/> is a usable project-relative path, otherwise a named error.</summary>
	public static string? ValidateRelativePath( string? path, string what )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return $"{what} path is empty";
		if ( path!.Contains( '\\' ) ) return $"{what} path '{path}' must use forward slashes";
		if ( System.IO.Path.IsPathRooted( path ) ) return $"{what} path '{path}' must be project-relative, not absolute";
		string[] segments = path.Split( '/' );
		if ( segments.Any( s => s is "" or "." or ".." ) )
			return $"{what} path '{path}' must not contain empty, '.' or '..' segments";
		return null;
	}

	// ---- JSON (the manifest file format, version 1) ----

	sealed class EntryJson
	{
		public string name { get; set; } = "";
		public string path { get; set; } = "";
		public string version { get; set; } = "";
	}

	sealed class ManifestJson
	{
		public int version { get; set; } = 1;
		public string name { get; set; } = "";
		public string library_version { get; set; } = "";
		public string path { get; set; } = "";
		public List<EntryJson> entries { get; set; } = new();
	}

	/// <summary>Serialises this manifest as its file format (version 1). Deterministic: entries in manifest order, no timestamps.</summary>
	public string ToJson()
	{
		var doc = new ManifestJson
		{
			name = Name,
			library_version = Version,
			path = Path,
			entries = Entries.Select( e => new EntryJson { name = e.Name, path = e.Path, version = e.Version } ).ToList(),
		};
		return JsonSerializer.Serialize( doc, new JsonSerializerOptions { WriteIndented = true } );
	}

	/// <summary>Reads back <see cref="ToJson"/> output. Throws <see cref="ArgumentException"/> naming the bad field.</summary>
	public static InstanceLibraryManifest FromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) throw new ArgumentException( "instance library manifest JSON is empty.", nameof( json ) );
		ManifestJson? doc;
		try { doc = JsonSerializer.Deserialize<ManifestJson>( json ); }
		catch ( JsonException ex ) { throw new ArgumentException( $"instance library manifest JSON is not valid JSON: {ex.Message}", nameof( json ), ex ); }
		if ( doc is null ) throw new ArgumentException( "instance library manifest JSON is empty.", nameof( json ) );
		if ( doc.version != 1 ) throw new ArgumentException( $"unsupported instance library manifest version {doc.version}; expected 1.", nameof( json ) );
		var manifest = new InstanceLibraryManifest
		{
			Name = doc.name ?? "",
			Version = doc.library_version ?? "",
			Path = doc.path ?? "",
		};
		foreach ( var ej in doc.entries ?? new List<EntryJson>() )
		{
			if ( ej is null ) throw new ArgumentException( "instance library manifest entry row is null.", nameof( json ) );
			manifest.Entries.Add( new LibraryEntry { Name = ej.name ?? "", Path = ej.path ?? "", Version = ej.version ?? "" } );
		}
		string? error = manifest.Validate();
		if ( error is not null ) throw new ArgumentException( $"instance library manifest is invalid: {error}.", nameof( json ) );
		return manifest;
	}
}

/// <summary>Result of <see cref="InstanceLibraryRegistry.Register"/> / Update.</summary>
public sealed class RegisterLibraryResult : InstancesCollabResult
{
	public InstanceLibraryManifest? Manifest { get; private set; }

	/// <summary>The version this library had before the update ("" when newly registered).</summary>
	public string PreviousVersion { get; private set; } = "";

	public static RegisterLibraryResult Success( InstanceLibraryManifest manifest, string previousVersion = "" ) =>
		new() { Ok = true, Manifest = manifest, PreviousVersion = previousVersion };

	public static new RegisterLibraryResult Failure( string reason, string message ) =>
		new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>
/// The shared-libraries store: one current manifest per library name.
/// Register adds a new library; Update moves a library to a newer version
/// and refuses a downgrade or a same-version path change (a version must
/// mean one document). MapDocument embeds <see cref="ToJson"/> verbatim as
/// its "instance_libraries" section.
/// </summary>
public sealed class InstanceLibraryRegistry
{
	/// <summary>Registry schema version. Bump only with a migration, never silently.</summary>
	public const int SchemaVersion = 1;

	readonly List<InstanceLibraryManifest> _libraries = new();

	public IReadOnlyList<InstanceLibraryManifest> Libraries => _libraries;

	/// <summary>Current manifest for a library name, or null.</summary>
	public InstanceLibraryManifest? Find( string name ) =>
		_libraries.FirstOrDefault( l => string.Equals( l.Name, name, StringComparison.Ordinal ) );

	/// <summary>Registers a new library. Refuses an invalid manifest or a name that is already registered (use Update for a new version).</summary>
	public RegisterLibraryResult Register( InstanceLibraryManifest manifest )
	{
		if ( manifest is null )
			return RegisterLibraryResult.Failure( "instancescollab-manifest-null", "No library manifest was supplied." );
		string? error = manifest.Validate();
		if ( error is not null )
			return RegisterLibraryResult.Failure( "instancescollab-invalid-manifest", $"Library manifest is invalid: {error}." );
		if ( Find( manifest.Name ) is not null )
			return RegisterLibraryResult.Failure( "instancescollab-duplicate-library",
				$"Library '{manifest.Name}' is already registered; use Update for a new version." );
		var stored = manifest.Clone();
		_libraries.Add( stored );
		return RegisterLibraryResult.Success( stored );
	}

	/// <summary>
	/// Moves a library to a newer version. Refuses an unknown library (use
	/// Register), a downgrade, and a same-version manifest pointing at a
	/// different path. A same-version manifest at the same path replaces the
	/// entries (re-registration of identical content) and reports the same
	/// version as previous.
	///
	/// The version/path conflict is checked BEFORE full manifest
	/// validation: a same-version manifest moved to a new path fails entry
	/// validation (its entries still point under the old path), and that
	/// must not mask the conflict this check exists to report. A manifest
	/// whose version does not parse at all still fails validation.
	/// </summary>
	public RegisterLibraryResult Update( InstanceLibraryManifest manifest )
	{
		if ( manifest is null )
			return RegisterLibraryResult.Failure( "instancescollab-manifest-null", "No library manifest was supplied." );
		var existing = Find( manifest.Name );
		if ( existing is null )
		{
			string? notFoundError = manifest.Validate();
			if ( notFoundError is not null )
				return RegisterLibraryResult.Failure( "instancescollab-invalid-manifest", $"Library manifest is invalid: {notFoundError}." );
			return RegisterLibraryResult.Failure( "instancescollab-library-not-found",
				$"Library '{manifest.Name}' is not registered; use Register first." );
		}
		if ( InstanceLibraryVersion.IsValid( manifest.Version ) )
		{
			int order = InstanceLibraryVersion.Compare( manifest.Version, existing.Version );
			if ( order < 0 )
				return RegisterLibraryResult.Failure( "instancescollab-version-downgrade",
					$"Library '{manifest.Name}' version {manifest.Version} is older than the registered {existing.Version}; a downgrade is refused." );
			if ( order == 0 && !string.Equals( manifest.Path, existing.Path, StringComparison.Ordinal ) )
				return RegisterLibraryResult.Failure( "instancescollab-version-conflict",
					$"Library '{manifest.Name}' version {manifest.Version} is already registered at '{existing.Path}', not '{manifest.Path}'; one version means one document." );
		}
		string? error = manifest.Validate();
		if ( error is not null )
			return RegisterLibraryResult.Failure( "instancescollab-invalid-manifest", $"Library manifest is invalid: {error}." );
		string previous = existing.Version;
		_libraries.Remove( existing );
		var stored = manifest.Clone();
		_libraries.Add( stored );
		return RegisterLibraryResult.Success( stored, previous );
	}

	/// <summary>Removes a library. False + a named reason when it was never registered.</summary>
	public InstancesCollabResult Remove( string name )
	{
		var existing = Find( name );
		if ( existing is null )
			return InstancesCollabResult.Failure( "instancescollab-library-not-found", $"Library '{name}' is not registered." );
		_libraries.Remove( existing );
		return InstancesCollabResult.Success();
	}

	// ---- JSON (registry section, version 1) ----

	sealed class RegistryJson
	{
		public int version { get; set; } = 1;
		public List<JsonElement> libraries { get; set; } = new();
	}

	/// <summary>Serialises this registry as the "instance_libraries" section of a map document (version 1). Deterministic: libraries in registration order, no timestamps.</summary>
	public string ToJson()
	{
		var doc = new RegistryJson
		{
			libraries = _libraries.Select( l => JsonSerializer.Deserialize<JsonElement>( l.ToJson() ) ).ToList(),
		};
		return JsonSerializer.Serialize( doc, new JsonSerializerOptions { WriteIndented = true } );
	}

	/// <summary>Reads back <see cref="ToJson"/> output. Throws <see cref="ArgumentException"/> naming the bad field.</summary>
	public static InstanceLibraryRegistry FromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) throw new ArgumentException( "instance_libraries JSON is empty.", nameof( json ) );
		RegistryJson? doc;
		try { doc = JsonSerializer.Deserialize<RegistryJson>( json ); }
		catch ( JsonException ex ) { throw new ArgumentException( $"instance_libraries JSON is not valid JSON: {ex.Message}", nameof( json ), ex ); }
		if ( doc is null ) throw new ArgumentException( "instance_libraries JSON is empty.", nameof( json ) );
		if ( doc.version != 1 ) throw new ArgumentException( $"unsupported instance_libraries version {doc.version}; expected 1.", nameof( json ) );
		var registry = new InstanceLibraryRegistry();
		foreach ( var element in doc.libraries ?? new List<JsonElement>() )
		{
			var manifest = InstanceLibraryManifest.FromJson( element.GetRawText() );
			var registered = registry.Register( manifest );
			if ( !registered.Ok )
				throw new ArgumentException( $"instance_libraries JSON is invalid: {registered.Message}", nameof( json ) );
		}
		return registry;
	}
}

// ---- Library sub-map content (the versioned sub-map document, instance-relevant subset) ----

/// <summary>One entity inside a library sub-map: classname, targetname, origin and keyvalues, in sub-map-local inches/Z-up.</summary>
public sealed class LibraryEntity
{
	/// <summary>Entity id, unique within its sub-map. Stable across versions: fix-up keys off targetnames, never off this id.</summary>
	public string Id { get; set; } = "";

	/// <summary>Class name (e.g. "light", "func_door").</summary>
	public string ClassName { get; set; } = "";

	/// <summary>Targetname as authored in the sub-map ("" = unnamed). Fix-up prefixes this; the original is never renamed in place.</summary>
	public string TargetName { get; set; } = "";

	/// <summary>Origin in sub-map-local space. The instance offset is added on resolve.</summary>
	public Vec3 Origin { get; set; } = Vec3.Zero;

	/// <summary>Keyvalues (classname is NOT duplicated here — it lives on <see cref="ClassName"/>).</summary>
	public Dictionary<string, string> KeyValues { get; } = new( StringComparer.OrdinalIgnoreCase );

	public LibraryEntity Clone()
	{
		var copy = new LibraryEntity { Id = Id, ClassName = ClassName, TargetName = TargetName, Origin = Origin };
		foreach ( var kv in KeyValues ) copy.KeyValues[kv.Key] = kv.Value;
		return copy;
	}
}

/// <summary>One brush inside a library sub-map: vertices + material, in sub-map-local inches/Z-up.</summary>
public sealed class LibraryBrush
{
	/// <summary>Brush id, unique within its sub-map.</summary>
	public string Id { get; set; } = "";

	/// <summary>Display name.</summary>
	public string Name { get; set; } = "";

	/// <summary>Material (e.g. "dev/dev_measuregeneric01").</summary>
	public string Material { get; set; } = "";

	/// <summary>Brush vertices in sub-map-local space. The instance offset is added on resolve.</summary>
	public List<Vec3> Vertices { get; } = new();

	public LibraryBrush Clone()
	{
		var copy = new LibraryBrush { Id = Id, Name = Name, Material = Material };
		copy.Vertices.AddRange( Vertices );
		return copy;
	}
}

/// <summary>
/// One IO output row inside a library sub-map: "when <see cref="Event"/>
/// fires on <see cref="SourceEntity"/>, send <see cref="Input"/> to
/// <see cref="TargetName"/>". Mirrors <see cref="EntityIoOutput"/> field
/// for field (SourceEntity is a targetname, or "#&lt;id&gt;" when the source
/// is unnamed); a resolved row converts 1:1 and <see cref="ToVmfLine"/>
/// uses EntityIo's connections-line format.
/// </summary>
public sealed class LibraryIoOutput
{
	public string SourceEntity { get; set; } = "";
	public string Event { get; set; } = "";
	public string TargetName { get; set; } = "";
	public string Input { get; set; } = "";
	public string Parameter { get; set; } = "";
	public double DelaySeconds { get; set; }
	public int MaxFires { get; set; } = -1;
	public bool Enabled { get; set; } = true;

	public LibraryIoOutput Clone() => new()
	{
		SourceEntity = SourceEntity,
		Event = Event,
		TargetName = TargetName,
		Input = Input,
		Parameter = Parameter ?? "",
		DelaySeconds = DelaySeconds,
		MaxFires = MaxFires,
		Enabled = Enabled,
	};

	/// <summary>Null when this row is valid, otherwise a named error (same rules as EntityIoOutput: no ',' or '"' in event/target/input/parameter, delay &gt;= 0 and finite, MaxFires -1 or &gt;= 0).</summary>
	public string? Validate()
	{
		if ( string.IsNullOrWhiteSpace( SourceEntity ) ) return "source entity is empty";
		if ( string.IsNullOrWhiteSpace( Event ) ) return "event is empty";
		if ( string.IsNullOrWhiteSpace( TargetName ) ) return "target name is empty";
		if ( string.IsNullOrWhiteSpace( Input ) ) return "input is empty";
		if ( Event.Contains( ',' ) || Event.Contains( '"' ) ) return "event must not contain ',' or '\"'";
		if ( TargetName.Contains( ',' ) || TargetName.Contains( '"' ) ) return "target name must not contain ',' or '\"'";
		if ( Input.Contains( ',' ) || Input.Contains( '"' ) ) return "input must not contain ',' or '\"'";
		if ( Parameter.Contains( ',' ) || Parameter.Contains( '"' ) ) return "parameter must not contain ',' or '\"'";
		if ( !double.IsFinite( DelaySeconds ) || DelaySeconds < 0 ) return "delay must be a finite number >= 0";
		if ( MaxFires < -1 ) return "max fires must be -1 (forever) or >= 0";
		return null;
	}

	/// <summary>One VMF connections-block line: "Event" "Target,Input,Parameter,Delay,Times".</summary>
	public string ToVmfLine() =>
		$"\"{Event}\" \"{TargetName},{Input},{Parameter},{DelaySeconds.ToString( "G", CultureInfo.InvariantCulture )},{MaxFires}\"";
}

/// <summary>
/// One versioned sub-map document as a library entry holds it: entities,
/// brushes and IO outputs. This is the instance-relevant subset of a map
/// document — the full document (layers, displacements, undo history) is
/// MapDocument's, and the adapter from a full MapDocument to this subset
/// lands with MapDocument. A sub-map never references the host map: every
/// target it names is either its own entity or an engine special ('!...').
/// </summary>
public sealed class LibrarySubMap
{
	/// <summary>Sub-map name (matches its <see cref="LibraryEntry"/> name).</summary>
	public string Name { get; set; } = "";

	/// <summary>Sub-map version ("1.0.0").</summary>
	public string Version { get; set; } = "";

	/// <summary>Project-relative path to this sub-map document.</summary>
	public string Path { get; set; } = "";

	public List<LibraryEntity> Entities { get; } = new();
	public List<LibraryBrush> Brushes { get; } = new();
	public List<LibraryIoOutput> IoOutputs { get; } = new();

	/// <summary>Entity by targetname (case-insensitive, mirroring MapEntities/EntityIo), or null. Unnamed entities are never found by name.</summary>
	public LibraryEntity? FindByTargetName( string targetName )
	{
		if ( string.IsNullOrWhiteSpace( targetName ) ) return null;
		return Entities.FirstOrDefault( e => string.Equals( e.TargetName, targetName, StringComparison.OrdinalIgnoreCase ) );
	}

	/// <summary>Entities sharing a targetname (case-insensitive). More than one means an ambiguous target.</summary>
	public IReadOnlyList<LibraryEntity> FindAllByTargetName( string targetName )
	{
		if ( string.IsNullOrWhiteSpace( targetName ) ) return Array.Empty<LibraryEntity>();
		return Entities.Where( e => string.Equals( e.TargetName, targetName, StringComparison.OrdinalIgnoreCase ) ).ToList();
	}

	public LibrarySubMap Clone()
	{
		var copy = new LibrarySubMap { Name = Name, Version = Version, Path = Path };
		foreach ( var e in Entities ) copy.Entities.Add( e.Clone() );
		foreach ( var b in Brushes ) copy.Brushes.Add( b.Clone() );
		foreach ( var o in IoOutputs ) copy.IoOutputs.Add( o.Clone() );
		return copy;
	}

	/// <summary>Null when this sub-map is valid, otherwise a named error: valid name/version/path, unique entity and brush ids.</summary>
	public string? Validate()
	{
		if ( string.IsNullOrWhiteSpace( Name ) ) return "sub-map name is empty";
		if ( !InstanceLibraryVersion.IsValid( Version ) ) return $"sub-map '{Name}' has invalid version '{Version}'";
		string? pathError = InstanceLibraryManifest.ValidateRelativePath( Path, $"sub-map '{Name}'" );
		if ( pathError is not null ) return pathError;
		var entityIds = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var e in Entities )
		{
			if ( e is null ) return $"sub-map '{Name}' has a null entity";
			if ( string.IsNullOrWhiteSpace( e.Id ) ) return $"sub-map '{Name}' has an entity with an empty id";
			if ( !entityIds.Add( e.Id ) ) return $"sub-map '{Name}' has duplicate entity id '{e.Id}'";
			if ( string.IsNullOrWhiteSpace( e.ClassName ) ) return $"sub-map '{Name}' entity '{e.Id}' has an empty classname";
		}
		var brushIds = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var b in Brushes )
		{
			if ( b is null ) return $"sub-map '{Name}' has a null brush";
			if ( string.IsNullOrWhiteSpace( b.Id ) ) return $"sub-map '{Name}' has a brush with an empty id";
			if ( !brushIds.Add( b.Id ) ) return $"sub-map '{Name}' has duplicate brush id '{b.Id}'";
		}
		foreach ( var o in IoOutputs )
		{
			if ( o is null ) return $"sub-map '{Name}' has a null IO output";
			string? ioError = o.Validate();
			if ( ioError is not null ) return $"sub-map '{Name}' IO output is invalid: {ioError}";
		}
		return null;
	}

	// ---- JSON (the versioned sub-map document, version 1) ----

	sealed class Vec3Json
	{
		public float x { get; set; }
		public float y { get; set; }
		public float z { get; set; }
	}

	sealed class EntityJson
	{
		public string id { get; set; } = "";
		public string classname { get; set; } = "";
		public string targetname { get; set; } = "";
		public Vec3Json origin { get; set; } = new();
		public Dictionary<string, string> key_values { get; set; } = new();
	}

	sealed class BrushJson
	{
		public string id { get; set; } = "";
		public string name { get; set; } = "";
		public string material { get; set; } = "";
		public List<Vec3Json> vertices { get; set; } = new();
	}

	sealed class IoOutputJson
	{
		public string source_entity { get; set; } = "";
		public string @event { get; set; } = "";
		public string target { get; set; } = "";
		public string input { get; set; } = "";
		public string parameter { get; set; } = "";
		public double delay_seconds { get; set; }
		public int max_fires { get; set; } = -1;
		public bool enabled { get; set; } = true;
	}

	sealed class SubMapJson
	{
		public int version { get; set; } = 1;
		public string name { get; set; } = "";
		public string submap_version { get; set; } = "";
		public string path { get; set; } = "";
		public List<EntityJson> entities { get; set; } = new();
		public List<BrushJson> brushes { get; set; } = new();
		public List<IoOutputJson> io_outputs { get; set; } = new();
	}

	static Vec3Json ToJson( Vec3 v ) => new() { x = v.x, y = v.y, z = v.z };
	static Vec3 FromJson( Vec3Json v ) => v is null ? Vec3.Zero : new Vec3( v.x, v.y, v.z );

	/// <summary>Serialises this sub-map as its document format (version 1). Deterministic: entities/brushes/outputs in stored order, no timestamps.</summary>
	public string ToJson()
	{
		var doc = new SubMapJson
		{
			name = Name,
			submap_version = Version,
			path = Path,
			entities = Entities.Select( e => new EntityJson
			{
				id = e.Id,
				classname = e.ClassName,
				targetname = e.TargetName,
				origin = ToJson( e.Origin ),
				key_values = new Dictionary<string, string>( e.KeyValues ),
			} ).ToList(),
			brushes = Brushes.Select( b => new BrushJson
			{
				id = b.Id,
				name = b.Name,
				material = b.Material,
				vertices = b.Vertices.Select( ToJson ).ToList(),
			} ).ToList(),
			io_outputs = IoOutputs.Select( o => new IoOutputJson
			{
				source_entity = o.SourceEntity,
				@event = o.Event,
				target = o.TargetName,
				input = o.Input,
				parameter = o.Parameter,
				delay_seconds = o.DelaySeconds,
				max_fires = o.MaxFires,
				enabled = o.Enabled,
			} ).ToList(),
		};
		return JsonSerializer.Serialize( doc, new JsonSerializerOptions { WriteIndented = true } );
	}

	/// <summary>Reads back <see cref="ToJson"/> output. Throws <see cref="ArgumentException"/> naming the bad field.</summary>
	public static LibrarySubMap FromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) throw new ArgumentException( "library sub-map JSON is empty.", nameof( json ) );
		SubMapJson? doc;
		try { doc = JsonSerializer.Deserialize<SubMapJson>( json ); }
		catch ( JsonException ex ) { throw new ArgumentException( $"library sub-map JSON is not valid JSON: {ex.Message}", nameof( json ), ex ); }
		if ( doc is null ) throw new ArgumentException( "library sub-map JSON is empty.", nameof( json ) );
		if ( doc.version != 1 ) throw new ArgumentException( $"unsupported library sub-map version {doc.version}; expected 1.", nameof( json ) );
		var subMap = new LibrarySubMap
		{
			Name = doc.name ?? "",
			Version = doc.submap_version ?? "",
			Path = doc.path ?? "",
		};
		foreach ( var ej in doc.entities ?? new List<EntityJson>() )
		{
			if ( ej is null ) throw new ArgumentException( "library sub-map entity row is null.", nameof( json ) );
			var entity = new LibraryEntity
			{
				Id = ej.id ?? "",
				ClassName = ej.classname ?? "",
				TargetName = ej.targetname ?? "",
				Origin = FromJson( ej.origin ),
			};
			foreach ( var kv in ej.key_values ?? new Dictionary<string, string>() ) entity.KeyValues[kv.Key] = kv.Value;
			subMap.Entities.Add( entity );
		}
		foreach ( var bj in doc.brushes ?? new List<BrushJson>() )
		{
			if ( bj is null ) throw new ArgumentException( "library sub-map brush row is null.", nameof( json ) );
			var brush = new LibraryBrush { Id = bj.id ?? "", Name = bj.name ?? "", Material = bj.material ?? "" };
			foreach ( var v in bj.vertices ?? new List<Vec3Json>() ) brush.Vertices.Add( FromJson( v ) );
			subMap.Brushes.Add( brush );
		}
		foreach ( var oj in doc.io_outputs ?? new List<IoOutputJson>() )
		{
			if ( oj is null ) throw new ArgumentException( "library sub-map IO output row is null.", nameof( json ) );
			subMap.IoOutputs.Add( new LibraryIoOutput
			{
				SourceEntity = oj.source_entity ?? "",
				Event = oj.@event ?? "",
				TargetName = oj.target ?? "",
				Input = oj.input ?? "",
				Parameter = oj.parameter ?? "",
				DelaySeconds = oj.delay_seconds,
				MaxFires = oj.max_fires,
				Enabled = oj.enabled,
			} );
		}
		string? error = subMap.Validate();
		if ( error is not null ) throw new ArgumentException( $"library sub-map is invalid: {error}.", nameof( json ) );
		return subMap;
	}
}

// ---- Library instances (placements in the host map) ----

/// <summary>
/// One placement of a library sub-map in the host map: which library and
/// entry, the name prefix, the placement offset, and the record of the last
/// resolve — the pinned version ("" = track the library) and the
/// original-&gt;resolved name map that makes re-resolves stable across
/// library updates.
/// </summary>
public sealed class LibraryInstance
{
	/// <summary>Instance id (e.g. "libinst-1").</summary>
	public string Id { get; set; } = "";

	/// <summary>Library name (must be registered).</summary>
	public string LibraryName { get; set; } = "";

	/// <summary>Entry name inside the library.</summary>
	public string EntryName { get; set; } = "";

	/// <summary>Name prefix (InstanceAuto-style): resolved names are "<see cref="Prefix"/>_&lt;targetname&gt;".</summary>
	public string Prefix { get; set; } = "";

	/// <summary>Placement offset (inches, Z-up) added to brush vertices and entity origins on resolve.</summary>
	public Vec3 Offset { get; set; } = Vec3.Zero;

	/// <summary>Version this instance is pinned to ("" = track the library's current version). A pinned instance refuses to resolve a different version.</summary>
	public string PinnedVersion { get; set; } = "";

	/// <summary>Sub-map version the last resolve used ("" = never resolved).</summary>
	public string ResolvedVersion { get; set; } = "";

	/// <summary>
	/// Original targetname -&gt; resolved (prefixed) name from the last
	/// resolve, keyed by the ORIGINAL name. This map is the stability
	/// record: UpdateInstance reuses it for names that still exist, so a
	/// library update never renames a target the host already references.
	/// </summary>
	public Dictionary<string, string> NameMap { get; } = new( StringComparer.OrdinalIgnoreCase );

	public LibraryInstance Clone()
	{
		var copy = new LibraryInstance
		{
			Id = Id,
			LibraryName = LibraryName,
			EntryName = EntryName,
			Prefix = Prefix,
			Offset = Offset,
			PinnedVersion = PinnedVersion,
			ResolvedVersion = ResolvedVersion,
		};
		foreach ( var kv in NameMap ) copy.NameMap[kv.Key] = kv.Value;
		return copy;
	}

	/// <summary>Adopts a resolve result: records its version and name map. Callers use <see cref="InstanceLibraryService.UpdateInstance"/> instead of adopting by hand.</summary>
	public void ApplyResolution( InstanceResolveResult result )
	{
		if ( result is null ) throw new ArgumentNullException( nameof( result ) );
		if ( !result.Ok ) throw new ArgumentException( $"Cannot adopt a failed resolve: {result.Reason}.", nameof( result ) );
		ResolvedVersion = result.SubMapVersion;
		NameMap.Clear();
		foreach ( var kv in result.NameMap ) NameMap[kv.Key] = kv.Value;
	}
}

/// <summary>The map-document store for library instances: placements plus their resolve records. MapDocument embeds <see cref="ToJson"/> verbatim as its "library_instances" section.</summary>
public sealed class LibraryInstanceSet
{
	/// <summary>Section schema version. Bump only with a migration, never silently.</summary>
	public const int SchemaVersion = 1;

	readonly List<LibraryInstance> _instances = new();
	int _nextInstance = 1;

	public IReadOnlyList<LibraryInstance> Instances => _instances;

	public LibraryInstance? Find( string instanceId ) =>
		_instances.FirstOrDefault( i => string.Equals( i.Id, instanceId, StringComparison.Ordinal ) );

	/// <summary>Places a library entry in the host map. Refuses a blank library/entry name or an invalid prefix, with a named reason.</summary>
	public InstancesCollabResult Add( string libraryName, string entryName, string prefix, Vec3 offset = default, string pinnedVersion = "" )
	{
		if ( string.IsNullOrWhiteSpace( libraryName ) )
			return InstancesCollabResult.Failure( "instancescollab-invalid-library", "A library instance needs a library name." );
		if ( string.IsNullOrWhiteSpace( entryName ) )
			return InstancesCollabResult.Failure( "instancescollab-invalid-entry", "A library instance needs an entry name." );
		string? prefixError = LibraryNameFixup.ValidatePrefix( prefix );
		if ( prefixError is not null )
			return InstancesCollabResult.Failure( "instancescollab-invalid-prefix", prefixError );
		if ( !string.IsNullOrEmpty( pinnedVersion ) && !InstanceLibraryVersion.IsValid( pinnedVersion ) )
			return InstancesCollabResult.Failure( "instancescollab-invalid-version",
				$"Pinned version '{pinnedVersion}' is invalid: expected major[.minor[.patch]] numbers." );
		var instance = new LibraryInstance
		{
			Id = $"libinst-{_nextInstance++}",
			LibraryName = libraryName,
			EntryName = entryName,
			Prefix = prefix,
			Offset = offset,
			PinnedVersion = pinnedVersion,
		};
		_instances.Add( instance );
		return InstancesCollabResult.Success();
	}

	/// <summary>The instance just added by the last <see cref="Add"/> call, or null. Tests use this to fetch the generated id.</summary>
	public LibraryInstance? LastAdded => _instances.Count == 0 ? null : _instances[_instances.Count - 1];

	/// <summary>Removes a placement. False + a named reason when it does not exist.</summary>
	public InstancesCollabResult Remove( string instanceId )
	{
		var instance = Find( instanceId );
		if ( instance is null )
			return InstancesCollabResult.Failure( "instancescollab-instance-not-found", $"No library instance '{instanceId}'." );
		_instances.Remove( instance );
		return InstancesCollabResult.Success();
	}

	// ---- JSON (map-document section, version 1) ----

	sealed class Vec3Json
	{
		public float x { get; set; }
		public float y { get; set; }
		public float z { get; set; }
	}

	sealed class InstanceJson
	{
		public string id { get; set; } = "";
		public string library_name { get; set; } = "";
		public string entry_name { get; set; } = "";
		public string prefix { get; set; } = "";
		public Vec3Json offset { get; set; } = new();
		public string pinned_version { get; set; } = "";
		public string resolved_version { get; set; } = "";
		public Dictionary<string, string> name_map { get; set; } = new();
	}

	sealed class SetJson
	{
		public int version { get; set; } = 1;
		public int next_instance { get; set; }
		public List<InstanceJson> instances { get; set; } = new();
	}

	/// <summary>Serialises this set as the "library_instances" section of a map document (version 1). Deterministic: name maps serialised sorted by original name.</summary>
	public string ToJson()
	{
		var doc = new SetJson
		{
			next_instance = _nextInstance,
			instances = _instances.Select( i => new InstanceJson
			{
				id = i.Id,
				library_name = i.LibraryName,
				entry_name = i.EntryName,
				prefix = i.Prefix,
				offset = new Vec3Json { x = i.Offset.x, y = i.Offset.y, z = i.Offset.z },
				pinned_version = i.PinnedVersion,
				resolved_version = i.ResolvedVersion,
				name_map = i.NameMap.OrderBy( kv => kv.Key, StringComparer.Ordinal )
					.ToDictionary( kv => kv.Key, kv => kv.Value, StringComparer.Ordinal ),
			} ).ToList(),
		};
		return JsonSerializer.Serialize( doc, new JsonSerializerOptions { WriteIndented = true } );
	}

	/// <summary>Reads back <see cref="ToJson"/> output. Throws <see cref="ArgumentException"/> naming the bad field.</summary>
	public static LibraryInstanceSet FromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) throw new ArgumentException( "library_instances JSON is empty.", nameof( json ) );
		SetJson? doc;
		try { doc = JsonSerializer.Deserialize<SetJson>( json ); }
		catch ( JsonException ex ) { throw new ArgumentException( $"library_instances JSON is not valid JSON: {ex.Message}", nameof( json ), ex ); }
		if ( doc is null ) throw new ArgumentException( "library_instances JSON is empty.", nameof( json ) );
		if ( doc.version != 1 ) throw new ArgumentException( $"unsupported library_instances version {doc.version}; expected 1.", nameof( json ) );
		var set = new LibraryInstanceSet { _nextInstance = Math.Max( 1, doc.next_instance ) };
		var ids = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var ij in doc.instances ?? new List<InstanceJson>() )
		{
			if ( ij is null ) throw new ArgumentException( "library_instances instance row is null.", nameof( json ) );
			if ( string.IsNullOrWhiteSpace( ij.id ) ) throw new ArgumentException( "library_instances instance is missing 'id'.", nameof( json ) );
			if ( !ids.Add( ij.id ) ) throw new ArgumentException( $"library_instances instance id '{ij.id}' appears twice.", nameof( json ) );
			string? prefixError = LibraryNameFixup.ValidatePrefix( ij.prefix ?? "" );
			if ( prefixError is not null ) throw new ArgumentException( $"library_instances instance '{ij.id}' is invalid: {prefixError}.", nameof( json ) );
			var instance = new LibraryInstance
			{
				Id = ij.id,
				LibraryName = ij.library_name ?? "",
				EntryName = ij.entry_name ?? "",
				Prefix = ij.prefix ?? "",
				Offset = ij.offset is null ? Vec3.Zero : new Vec3( ij.offset.x, ij.offset.y, ij.offset.z ),
				PinnedVersion = ij.pinned_version ?? "",
				ResolvedVersion = ij.resolved_version ?? "",
			};
			foreach ( var kv in ij.name_map ?? new Dictionary<string, string>() ) instance.NameMap[kv.Key] = kv.Value;
			set._instances.Add( instance );
			if ( ij.id.StartsWith( "libinst-", StringComparison.Ordinal )
				&& int.TryParse( ij.id.AsSpan( 8 ), out int n ) && n >= set._nextInstance )
				set._nextInstance = n + 1;
		}
		return set;
	}
}

// ---- Name fix-up ----

/// <summary>One name a fix-up had to disambiguate: the prefixed name was already taken in the host, so a numeric suffix was added. The host name is never the one renamed.</summary>
public sealed class NameFixupCollision
{
	/// <summary>Targetname as authored in the sub-map.</summary>
	public string Original { get; set; } = "";

	/// <summary>The plain prefixed name ("<prefix>_<original>") that collided.</summary>
	public string Prefixed { get; set; } = "";

	/// <summary>The name actually used ("<prefixed>_2", ...).</summary>
	public string Resolved { get; set; } = "";
}

/// <summary>Result of <see cref="LibraryNameFixup.FixUp"/>: fixed-up copies of the sub-map content plus the name map and the collisions that were disambiguated.</summary>
public sealed class FixUpResult : InstancesCollabResult
{
	public IReadOnlyList<LibraryEntity> Entities { get; private set; } = Array.Empty<LibraryEntity>();
	public IReadOnlyList<LibraryBrush> Brushes { get; private set; } = Array.Empty<LibraryBrush>();
	public IReadOnlyList<LibraryIoOutput> IoOutputs { get; private set; } = Array.Empty<LibraryIoOutput>();

	/// <summary>Original targetname -&gt; resolved name, for every named entity in the sub-map.</summary>
	public IReadOnlyDictionary<string, string> NameMap { get; private set; } = new Dictionary<string, string>();

	/// <summary>Collisions disambiguated with a numeric suffix (empty when every prefixed name was free).</summary>
	public IReadOnlyList<NameFixupCollision> Collisions { get; private set; } = Array.Empty<NameFixupCollision>();

	/// <summary>Non-fatal notes (a targetname shared by several entities in the sub-map; a stable name that now collides with a host name and was kept anyway).</summary>
	public IReadOnlyList<string> Warnings { get; private set; } = Array.Empty<string>();

	public static FixUpResult Success(
		IReadOnlyList<LibraryEntity> entities,
		IReadOnlyList<LibraryBrush> brushes,
		IReadOnlyList<LibraryIoOutput> ioOutputs,
		IReadOnlyDictionary<string, string> nameMap,
		IReadOnlyList<NameFixupCollision> collisions,
		IReadOnlyList<string> warnings ) =>
		new()
		{
			Ok = true,
			Entities = entities,
			Brushes = brushes,
			IoOutputs = ioOutputs,
			NameMap = nameMap,
			Collisions = collisions,
			Warnings = warnings,
		};

	public static new FixUpResult Failure( string reason, string message ) =>
		new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>
/// Name fix-up across library updates (InstanceAuto-style prefixing).
/// Pure functions: they deep-copy the sub-map, prefix every targetname as
/// "<prefix>_<name>", and rewrite every intra-library target reference —
/// keyvalues named "target"/"parentname", any keyvalue whose value equals a
/// library targetname, and IO output sources/targets — to the prefixed
/// name. Engine specials ('!self', '!activator', ...) and names that belong
/// to the host map are never rewritten. When a prefixed name is already
/// taken in the host, the INCOMING name gets a "_2"/"_3" suffix; the host
/// entity is never renamed, and the original sub-map is never modified.
/// </summary>
public static class LibraryNameFixup
{
	/// <summary>Keyvalue keys that are always target references, whatever their value (mirrors MapEntities' Target-typed keys + implicit "parentname").</summary>
	public static readonly IReadOnlyList<string> TargetReferenceKeys = new[] { "target", "parentname" };

	/// <summary>Null when <paramref name="prefix"/> is a usable name prefix, otherwise a named error.</summary>
	public static string? ValidatePrefix( string? prefix )
	{
		if ( string.IsNullOrWhiteSpace( prefix ) ) return "instance prefix is empty";
		if ( prefix!.Any( c => char.IsWhiteSpace( c ) || c is ',' or '"' or '/' or '\\' ) )
			return $"instance prefix '{prefix}' must not contain whitespace, ',', '\"', '/' or '\\'";
		if ( prefix.StartsWith( "!", StringComparison.Ordinal ) )
			return $"instance prefix '{prefix}' must not start with '!' (engine special targets start with '!')";
		return null;
	}

	/// <summary>True for engine special targets ('!self', '!activator', ...): never a real targetname, never rewritten (mirrors EntityIo/MapEntities).</summary>
	public static bool IsSpecialTarget( string? name ) =>
		!string.IsNullOrEmpty( name ) && name!.StartsWith( "!", StringComparison.Ordinal );

	/// <summary>The plain prefixed form of a name ("<prefix>_<name>"). Unnamed and special targets pass through unchanged.</summary>
	public static string PrefixName( string prefix, string name )
	{
		if ( string.IsNullOrEmpty( name ) || IsSpecialTarget( name ) ) return name;
		return $"{prefix}_{name}";
	}

	/// <summary>
	/// Fixes up a sub-map for placement: prefixes targetnames, rewrites
	/// intra-library references, and reports the name map + collisions.
	/// <paramref name="hostTargetNames"/> is the multiset of targetnames
	/// already in the host map (case-insensitive); <paramref name="stableMap"/>
	/// is a previous resolve's name map — names in it are REUSED verbatim
	/// for stability even if they now collide with a host name (reported as
	/// a warning, never silently renamed). The input sub-map is not modified.
	/// </summary>
	public static FixUpResult FixUp(
		LibrarySubMap subMap,
		string prefix,
		IReadOnlyCollection<string>? hostTargetNames = null,
		IReadOnlyDictionary<string, string>? stableMap = null )
	{
		if ( subMap is null )
			return FixUpResult.Failure( "instancescollab-submap-null", "No library sub-map was supplied." );
		string? prefixError = ValidatePrefix( prefix );
		if ( prefixError is not null )
			return FixUpResult.Failure( "instancescollab-invalid-prefix", prefixError );

		var host = new HashSet<string>( hostTargetNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase );
		var warnings = new List<string>();
		var collisions = new List<NameFixupCollision>();
		var nameMap = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		var claimed = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		// First pass: decide every named entity's resolved name.
		foreach ( var entity in subMap.Entities )
		{
			if ( string.IsNullOrEmpty( entity.TargetName ) || IsSpecialTarget( entity.TargetName ) ) continue;
			if ( nameMap.ContainsKey( entity.TargetName ) )
			{
				// Two entities share one targetname in the sub-map (Source
				// allows it, as a warning): both resolve to the same name.
				warnings.Add( $"sub-map '{subMap.Name}' targetname '{entity.TargetName}' is used by more than one entity; both resolve to the same prefixed name" );
				continue;
			}
			string resolved;
			if ( stableMap is not null && stableMap.TryGetValue( entity.TargetName, out string? stable ) && !string.IsNullOrEmpty( stable ) )
			{
				resolved = stable;
				if ( host.Contains( resolved ) )
					warnings.Add( $"stable name '{resolved}' for '{entity.TargetName}' now collides with a host targetname and was kept anyway; host references to it win" );
			}
			else
			{
				string prefixed = PrefixName( prefix, entity.TargetName );
				resolved = prefixed;
				if ( host.Contains( resolved ) || claimed.Contains( resolved ) )
				{
					int n = 2;
					while ( host.Contains( $"{prefixed}_{n}" ) || claimed.Contains( $"{prefixed}_{n}" ) ) n++;
					resolved = $"{prefixed}_{n}";
					collisions.Add( new NameFixupCollision { Original = entity.TargetName, Prefixed = prefixed, Resolved = resolved } );
				}
			}
			nameMap[entity.TargetName] = resolved;
			claimed.Add( resolved );
		}

		string MapReference( string value )
		{
			if ( string.IsNullOrEmpty( value ) || IsSpecialTarget( value ) ) return value;
			return nameMap.TryGetValue( value, out string? mapped ) ? mapped : value;
		}

		// Second pass: deep-copy everything with references rewritten.
		var entities = new List<LibraryEntity>();
		foreach ( var entity in subMap.Entities )
		{
			var copy = entity.Clone();
			if ( !string.IsNullOrEmpty( copy.TargetName ) && !IsSpecialTarget( copy.TargetName ) )
				copy.TargetName = nameMap.TryGetValue( copy.TargetName, out string? mapped ) ? mapped : copy.TargetName;
			foreach ( string key in copy.KeyValues.Keys.ToList() )
			{
				string value = copy.KeyValues[key];
				bool isTargetKey = TargetReferenceKeys.Any( k => string.Equals( k, key, StringComparison.OrdinalIgnoreCase ) );
				if ( isTargetKey || nameMap.ContainsKey( value ) )
					copy.KeyValues[key] = MapReference( value );
			}
			entities.Add( copy );
		}
		var brushes = subMap.Brushes.Select( b => b.Clone() ).ToList();
		var ioOutputs = new List<LibraryIoOutput>();
		foreach ( var output in subMap.IoOutputs )
		{
			var copy = output.Clone();
			// "#<id>" sources are id-based and stable; targetname sources follow the name map.
			if ( !copy.SourceEntity.StartsWith( "#", StringComparison.Ordinal ) )
				copy.SourceEntity = MapReference( copy.SourceEntity );
			copy.TargetName = MapReference( copy.TargetName );
			ioOutputs.Add( copy );
		}
		return FixUpResult.Success( entities, brushes, ioOutputs, nameMap, collisions, warnings );
	}
}

/// <summary>Result of <see cref="InstanceLibraryService.ResolveInstance"/>.</summary>
public sealed class InstanceResolveResult : InstancesCollabResult
{
	/// <summary>Sub-map version that was resolved.</summary>
	public string SubMapVersion { get; private set; } = "";

	/// <summary>Resolved entities: prefixed targetnames, references rewritten, instance offset applied to origins.</summary>
	public IReadOnlyList<LibraryEntity> Entities { get; private set; } = Array.Empty<LibraryEntity>();

	/// <summary>Resolved brushes: instance offset applied to vertices.</summary>
	public IReadOnlyList<LibraryBrush> Brushes { get; private set; } = Array.Empty<LibraryBrush>();

	/// <summary>Resolved IO outputs: sources/targets rewritten to prefixed names.</summary>
	public IReadOnlyList<LibraryIoOutput> IoOutputs { get; private set; } = Array.Empty<LibraryIoOutput>();

	/// <summary>Original targetname -&gt; resolved name for this resolve.</summary>
	public IReadOnlyDictionary<string, string> NameMap { get; private set; } = new Dictionary<string, string>();

	public IReadOnlyList<NameFixupCollision> Collisions { get; private set; } = Array.Empty<NameFixupCollision>();
	public IReadOnlyList<string> Warnings { get; private set; } = Array.Empty<string>();

	public static InstanceResolveResult Success(
		string subMapVersion,
		IReadOnlyList<LibraryEntity> entities,
		IReadOnlyList<LibraryBrush> brushes,
		IReadOnlyList<LibraryIoOutput> ioOutputs,
		IReadOnlyDictionary<string, string> nameMap,
		IReadOnlyList<NameFixupCollision> collisions,
		IReadOnlyList<string> warnings ) =>
		new()
		{
			Ok = true,
			SubMapVersion = subMapVersion,
			Entities = entities,
			Brushes = brushes,
			IoOutputs = ioOutputs,
			NameMap = nameMap,
			Collisions = collisions,
			Warnings = warnings,
		};

	public static new InstanceResolveResult Failure( string reason, string message ) =>
		new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>Result of <see cref="InstanceLibraryService.UpdateInstance"/>: the re-resolve, plus which names were kept, added and removed by the update.</summary>
public sealed class UpdateInstanceResult : InstancesCollabResult
{
	public InstanceResolveResult? Resolve { get; private set; }

	/// <summary>Original targetnames whose resolved name is unchanged by the update (the stability guarantee).</summary>
	public IReadOnlyList<string> KeptNames { get; private set; } = Array.Empty<string>();

	/// <summary>Original targetnames new in this sub-map version.</summary>
	public IReadOnlyList<string> AddedNames { get; private set; } = Array.Empty<string>();

	/// <summary>Original targetnames gone from this sub-map version (host references to their old resolved names now dangle).</summary>
	public IReadOnlyList<string> RemovedNames { get; private set; } = Array.Empty<string>();

	/// <summary>Renames applied by this update, as "old -&gt; new" (unprefixed). A renamed target keeps its resolved name, so it also appears in <see cref="KeptNames"/> under its new name.</summary>
	public IReadOnlyList<string> RenamedNames { get; private set; } = Array.Empty<string>();

	public static UpdateInstanceResult Success(
		InstanceResolveResult resolve,
		IReadOnlyList<string> kept,
		IReadOnlyList<string> added,
		IReadOnlyList<string> removed,
		IReadOnlyList<string>? renamed = null ) =>
		new() { Ok = true, Resolve = resolve, KeptNames = kept, AddedNames = added, RemovedNames = removed, RenamedNames = renamed ?? Array.Empty<string>() };

	public static new UpdateInstanceResult Failure( string reason, string message ) =>
		new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>
/// Resolves and updates library instances against sub-map documents.
/// Resolve is a pure preview (it does not touch the instance); Update
/// re-resolves reusing the instance's recorded name map and adopts the
/// result, so a library update keeps every prefixed name the host already
/// references. Neither method mutates the input sub-map.
/// </summary>
public sealed class InstanceLibraryService
{
	/// <summary>
	/// Resolves one instance against a sub-map: fix-up with the instance's
	/// prefix (reusing its recorded name map for stability), then the
	/// placement offset. Refuses a null sub-map, a pinned instance asked to
	/// resolve a different version, or an invalid prefix. Does not adopt
	/// the result into the instance — use <see cref="UpdateInstance"/> for
	/// that, or <see cref="LibraryInstance.ApplyResolution"/>.
	/// </summary>
	public InstanceResolveResult ResolveInstance(
		LibraryInstance instance,
		LibrarySubMap subMap,
		IReadOnlyCollection<string>? hostTargetNames = null )
	{
		if ( instance is null )
			return InstanceResolveResult.Failure( "instancescollab-instance-null", "No library instance was supplied." );
		if ( subMap is null )
			return InstanceResolveResult.Failure( "instancescollab-submap-null", "No library sub-map was supplied." );
		if ( !string.IsNullOrEmpty( instance.PinnedVersion )
			&& InstanceLibraryVersion.IsValid( subMap.Version )
			&& InstanceLibraryVersion.Compare( instance.PinnedVersion, subMap.Version ) != 0 )
			return InstanceResolveResult.Failure( "instancescollab-version-pinned",
				$"Instance '{instance.Id}' is pinned to version {instance.PinnedVersion} and refuses sub-map version {subMap.Version}." );
		return ResolveWithStableMap( instance, subMap, hostTargetNames, instance.NameMap );
	}

	/// <summary>
	/// The resolve core: fix-up with <paramref name="stableMap"/> (the
	/// instance's recorded map for a plain resolve, or the rename-carried
	/// map for an update with renames), then the placement offset. Assumes
	/// the pin check already ran.
	/// </summary>
	static InstanceResolveResult ResolveWithStableMap(
		LibraryInstance instance,
		LibrarySubMap subMap,
		IReadOnlyCollection<string>? hostTargetNames,
		IReadOnlyDictionary<string, string>? stableMap )
	{
		var fixedUp = LibraryNameFixup.FixUp( subMap, instance.Prefix, hostTargetNames, stableMap );
		if ( !fixedUp.Ok )
			return InstanceResolveResult.Failure( fixedUp.Reason, fixedUp.Message );
		var entities = fixedUp.Entities.Select( e =>
		{
			var copy = e.Clone();
			copy.Origin = copy.Origin + instance.Offset;
			return copy;
		} ).ToList();
		var brushes = fixedUp.Brushes.Select( b =>
		{
			var copy = b.Clone();
			for ( int i = 0; i < copy.Vertices.Count; i++ ) copy.Vertices[i] = copy.Vertices[i] + instance.Offset;
			return copy;
		} ).ToList();
		return InstanceResolveResult.Success(
			subMap.Version, entities, brushes, fixedUp.IoOutputs,
			fixedUp.NameMap, fixedUp.Collisions, fixedUp.Warnings );
	}

	/// <summary>
	/// Rewrites one reference through <paramref name="renames"/> (old -&gt;
	/// new, case-insensitive): an exact match, or the comma-first-segment
	/// form EntityIo outputs use. '!' specials and names that were not
	/// renamed pass through.
	/// </summary>
	static string RenameReference( string value, IReadOnlyDictionary<string, string> renames )
	{
		if ( string.IsNullOrEmpty( value ) || renames is null || renames.Count == 0 ) return value ?? "";
		if ( LibraryNameFixup.IsSpecialTarget( value ) ) return value;
		int comma = value.IndexOf( ',' );
		if ( comma > 0 )
		{
			string head = value.Substring( 0, comma );
			return renames.TryGetValue( head, out string? mapped ) ? mapped + value.Substring( comma ) : value;
		}
		return renames.TryGetValue( value, out string? exact ) ? exact : value;
	}

	/// <summary>
	/// Updates one instance to a new sub-map version: re-resolves reusing
	/// the recorded name map, adopts the result into the instance, and
	/// reports which names were kept (resolved name unchanged), added,
	/// removed and renamed. A pinned instance refuses a version other than
	/// its pin; a failed update adopts nothing.
	///
	/// RENAMES. <paramref name="renames"/> maps an old targetname to the
	/// new targetname the library author renamed it to. The renamed target
	/// keeps its old RESOLVED (prefixed) name — the host never sees the
	/// rename — and stale references to the old name inside the new
	/// sub-map (keyvalues, IO rows) are rewritten to the new name before
	/// fix-up. A rename is refused when the old or new name is blank, an
	/// engine special, or the same name; when the new name is not defined
	/// in the new sub-map version; when the old name is still defined
	/// there (that is not a rename); or, for an instance that already
	/// resolved, when the old name is not one it resolved before. Proxies
	/// are NOT remapped here — run
	/// <see cref="LibraryIoProxy.RemapRenames"/> with the same map on each
	/// proxy of the instance, then re-validate it.
	/// </summary>
	public UpdateInstanceResult UpdateInstance(
		LibraryInstance instance,
		LibrarySubMap subMap,
		IReadOnlyCollection<string>? hostTargetNames = null,
		IReadOnlyDictionary<string, string>? renames = null )
	{
		if ( instance is null )
			return UpdateInstanceResult.Failure( "instancescollab-instance-null", "No library instance was supplied." );
		if ( subMap is null )
			return UpdateInstanceResult.Failure( "instancescollab-submap-null", "No library sub-map was supplied." );
		if ( !string.IsNullOrEmpty( instance.PinnedVersion )
			&& InstanceLibraryVersion.IsValid( subMap.Version )
			&& InstanceLibraryVersion.Compare( instance.PinnedVersion, subMap.Version ) != 0 )
			return UpdateInstanceResult.Failure( "instancescollab-version-pinned",
				$"Instance '{instance.Id}' is pinned to version {instance.PinnedVersion} and refuses sub-map version {subMap.Version}." );
		// ---- Validate the rename map before resolving anything: a refused
		// update adopts nothing. ----
		var renameMap = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		if ( renames is not null )
		{
			foreach ( var kv in renames )
			{
				string oldName = kv.Key ?? "";
				string newName = kv.Value ?? "";
				if ( string.IsNullOrWhiteSpace( oldName ) || string.IsNullOrWhiteSpace( newName ) )
					return UpdateInstanceResult.Failure( "instancescollab-invalid-rename", "A rename needs a non-blank old and new targetname." );
				if ( LibraryNameFixup.IsSpecialTarget( oldName ) || LibraryNameFixup.IsSpecialTarget( newName ) )
					return UpdateInstanceResult.Failure( "instancescollab-invalid-rename",
						$"Rename '{oldName}' -> '{newName}' names an engine special ('!' names are never renamed)." );
				if ( string.Equals( oldName, newName, StringComparison.OrdinalIgnoreCase ) )
					return UpdateInstanceResult.Failure( "instancescollab-invalid-rename",
						$"Rename '{oldName}' -> '{newName}' changes nothing." );
				if ( !renameMap.TryAdd( oldName, newName ) )
					return UpdateInstanceResult.Failure( "instancescollab-invalid-rename",
						$"Rename for '{oldName}' appears twice." );
			}
			foreach ( var kv in renameMap )
			{
				if ( subMap.FindByTargetName( kv.Value ) is null )
					return UpdateInstanceResult.Failure( "instancescollab-rename-unknown-target",
						$"Rename '{kv.Key}' -> '{kv.Value}' names no target in the new sub-map version." );
				if ( subMap.FindByTargetName( kv.Key ) is not null )
					return UpdateInstanceResult.Failure( "instancescollab-rename-collision",
						$"Rename '{kv.Key}' -> '{kv.Value}' is not a rename: '{kv.Key}' is still defined in the new sub-map version." );
				if ( instance.ResolvedVersion.Length > 0 && !instance.NameMap.ContainsKey( kv.Key ) )
					return UpdateInstanceResult.Failure( "instancescollab-rename-unknown-target",
						$"Rename '{kv.Key}' names no target the instance resolved before (last resolved version {instance.ResolvedVersion})." );
			}
		}

		// The effective stable map: the recorded map, with each renamed
		// target carried over under its NEW name at the SAME resolved name,
		// so the host keeps referencing one stable name across the rename.
		var stable = new Dictionary<string, string>( instance.NameMap, StringComparer.OrdinalIgnoreCase );
		foreach ( var kv in renameMap )
		{
			if ( stable.TryGetValue( kv.Key, out string? resolvedBefore ) )
			{
				stable.Remove( kv.Key );
				stable[kv.Value] = resolvedBefore;
			}
		}

		// Rewrite stale references to renamed targets inside a clone of the
		// new sub-map (the author renamed the targetname; references in
		// keyvalues and IO rows may still name the old one). The input
		// sub-map is never modified.
		var rewritten = subMap.Clone();
		if ( renameMap.Count > 0 )
		{
			foreach ( var entity in rewritten.Entities )
			{
				foreach ( string key in entity.KeyValues.Keys.ToList() )
					entity.KeyValues[key] = RenameReference( entity.KeyValues[key], renameMap );
			}
			foreach ( var output in rewritten.IoOutputs )
			{
				if ( !output.SourceEntity.StartsWith( "#", StringComparison.Ordinal ) )
					output.SourceEntity = RenameReference( output.SourceEntity, renameMap );
				output.TargetName = RenameReference( output.TargetName, renameMap );
			}
		}

		var before = instance.NameMap.Keys.ToList();
		var resolved = ResolveWithStableMap( instance, rewritten, hostTargetNames, stable );
		if ( !resolved.Ok )
			return UpdateInstanceResult.Failure( resolved.Reason, resolved.Message );
		var after = resolved.NameMap.Keys.ToList();
		var renamedNames = renameMap.Select( kv => $"{kv.Key} -> {kv.Value}" ).ToList();
		var kept = after.Where( name =>
			( before.Contains( name, StringComparer.OrdinalIgnoreCase )
				&& string.Equals( resolved.NameMap[name], instance.NameMap[name], StringComparison.Ordinal ) )
			|| renameMap.Any( kv => string.Equals( kv.Value, name, StringComparison.OrdinalIgnoreCase )
				&& before.Contains( kv.Key, StringComparer.OrdinalIgnoreCase )
				&& string.Equals( resolved.NameMap[name], instance.NameMap[kv.Key], StringComparison.Ordinal ) ) ).ToList();
		var added = after.Where( name => !before.Contains( name, StringComparer.OrdinalIgnoreCase )
			&& !renameMap.Values.Any( v => string.Equals( v, name, StringComparison.OrdinalIgnoreCase ) ) ).ToList();
		var removed = before.Where( name => !after.Contains( name, StringComparer.OrdinalIgnoreCase )
			&& !renameMap.ContainsKey( name ) ).ToList();
		instance.ApplyResolution( resolved );
		return UpdateInstanceResult.Success( resolved, kept, added, removed, renamedNames );
	}
}

// ---- IO proxy for cross-map I/O ----

/// <summary>
/// One proxy route: when <see cref="Event"/> fires, send <see cref="Input"/>
/// to <see cref="TargetName"/>. Forward routes fire on the proxy itself
/// (source = the proxy's targetname); backward routes fire on an instance
/// entity (<see cref="SourceEntity"/>, as authored) and send into the host.
/// </summary>
public sealed class IoProxyRoute
{
	/// <summary>
	/// Entity that fires the event: "" = the proxy itself (forward routes).
	/// Backward routes name the instance entity as authored (resolved to
	/// its prefixed name); "#&lt;id&gt;" and '!' specials pass through.
	/// </summary>
	public string SourceEntity { get; set; } = "";

	/// <summary>Event that triggers this route.</summary>
	public string Event { get; set; } = "";

	/// <summary>Target to send the input to: an instance targetname as authored (forward) or a host targetname (backward).</summary>
	public string TargetName { get; set; } = "";

	/// <summary>Input to send.</summary>
	public string Input { get; set; } = "";

	/// <summary>Override parameter ("" = none).</summary>
	public string Parameter { get; set; } = "";

	/// <summary>Delay in seconds before the input fires.</summary>
	public double DelaySeconds { get; set; }

	/// <summary>Times to fire (-1 = forever).</summary>
	public int MaxFires { get; set; } = -1;

	/// <summary>Disabled routes resolve to nothing and emit no VMF line.</summary>
	public bool Enabled { get; set; } = true;

	public IoProxyRoute Clone() => new()
	{
		SourceEntity = SourceEntity,
		Event = Event,
		TargetName = TargetName,
		Input = Input,
		Parameter = Parameter ?? "",
		DelaySeconds = DelaySeconds,
		MaxFires = MaxFires,
		Enabled = Enabled,
	};

	/// <summary>Null when this route is valid, otherwise a named error (same rules as LibraryIoOutput).</summary>
	public string? Validate()
	{
		if ( string.IsNullOrWhiteSpace( Event ) ) return "event is empty";
		if ( string.IsNullOrWhiteSpace( TargetName ) ) return "target name is empty";
		if ( string.IsNullOrWhiteSpace( Input ) ) return "input is empty";
		if ( Event.Contains( ',' ) || Event.Contains( '"' ) ) return "event must not contain ',' or '\"'";
		if ( TargetName.Contains( ',' ) || TargetName.Contains( '"' ) ) return "target name must not contain ',' or '\"'";
		if ( Input.Contains( ',' ) || Input.Contains( '"' ) ) return "input must not contain ',' or '\"'";
		if ( Parameter.Contains( ',' ) || Parameter.Contains( '"' ) ) return "parameter must not contain ',' or '\"'";
		if ( !double.IsFinite( DelaySeconds ) || DelaySeconds < 0 ) return "delay must be a finite number >= 0";
		if ( MaxFires < -1 ) return "max fires must be -1 (forever) or >= 0";
		return null;
	}
}

/// <summary>One proxy validation finding.</summary>
public sealed class IoProxyIssue
{
	public const string CodeTargetMissing = "instancescollab-proxy-target-missing";
	public const string CodeTargetAmbiguous = "instancescollab-proxy-target-ambiguous";
	public const string CodeRouteInvalid = "instancescollab-proxy-route-invalid";

	/// <summary>Error (blocks the proxy) or Warning.</summary>
	public string Severity { get; set; } = "Error";

	public string Code { get; set; } = "";
	public string Message { get; set; } = "";
}

/// <summary>Result of <see cref="LibraryIoProxy.Validate"/>.</summary>
public sealed class IoProxyValidationResult : InstancesCollabResult
{
	public IReadOnlyList<IoProxyIssue> Issues { get; private set; } = Array.Empty<IoProxyIssue>();

	/// <summary>True when no Error-severity issue was found (warnings do not fail validation).</summary>
	public bool IsValid => Issues.All( i => i.Severity != "Error" );

	public static IoProxyValidationResult FromIssues( IReadOnlyList<IoProxyIssue> issues ) =>
		new()
		{
			Ok = issues.All( i => i.Severity != "Error" ),
			Reason = issues.FirstOrDefault( i => i.Severity == "Error" )?.Code ?? "",
			Message = issues.FirstOrDefault( i => i.Severity == "Error" )?.Message ?? "",
			Issues = issues,
		};
}

/// <summary>
/// The IO proxy for one library instance: a host-side entity (targetname
/// <see cref="Name"/>) that forwards I/O across the instance boundary.
/// Forward routes carry a host event into the instance (their target is an
/// instance targetname as authored; resolve rewrites it to the prefixed
/// name). Backward routes carry an instance event out to a host target
/// (their target is already a host-global name). The proxy never invents a
/// target: an unresolvable forward target is reported, not guessed.
/// </summary>
public sealed class LibraryIoProxy
{
	/// <summary>Proxy id (e.g. "ioproxy-1").</summary>
	public string Id { get; set; } = "";

	/// <summary>Targetname of the proxy entity in the host map.</summary>
	public string Name { get; set; } = "";

	/// <summary>Library instance this proxy forwards for.</summary>
	public string InstanceId { get; set; } = "";

	/// <summary>Host event -&gt; instance input.</summary>
	public List<IoProxyRoute> Forward { get; } = new();

	/// <summary>Instance event -&gt; host input.</summary>
	public List<IoProxyRoute> Backward { get; } = new();

	public LibraryIoProxy Clone()
	{
		var copy = new LibraryIoProxy { Id = Id, Name = Name, InstanceId = InstanceId };
		foreach ( var r in Forward ) copy.Forward.Add( r.Clone() );
		foreach ( var r in Backward ) copy.Backward.Add( r.Clone() );
		return copy;
	}

	/// <summary>
	/// Validates this proxy against a resolved instance: every enabled
	/// forward route's target must exist in the sub-map (by original
	/// targetname) and be unambiguous; every route's fields must be valid.
	/// Backward targets are host-global and are NOT checked here — the host
	/// map's own validation (MapEntities.Validate) owns them. Returns all
	/// findings; Ok is false only when an Error is present.
	/// </summary>
	public IoProxyValidationResult Validate( LibrarySubMap subMap, IReadOnlyDictionary<string, string>? nameMap = null )
	{
		var issues = new List<IoProxyIssue>();
		if ( subMap is null )
		{
			issues.Add( new IoProxyIssue { Code = "instancescollab-submap-null", Message = "No library sub-map was supplied." } );
			return IoProxyValidationResult.FromIssues( issues );
		}
		if ( string.IsNullOrWhiteSpace( Name ) )
			issues.Add( new IoProxyIssue { Code = "instancescollab-proxy-route-invalid", Message = "The IO proxy needs a targetname in the host map." } );
		foreach ( var route in Forward )
		{
			string? fieldError = route.Validate();
			if ( fieldError is not null )
			{
				issues.Add( new IoProxyIssue { Code = IoProxyIssue.CodeRouteInvalid, Message = $"Forward route '{route.Event}' -> '{route.TargetName}' is invalid: {fieldError}." } );
				continue;
			}
			if ( !route.Enabled ) continue;
			if ( LibraryNameFixup.IsSpecialTarget( route.TargetName ) ) continue;
			var matches = subMap.FindAllByTargetName( route.TargetName );
			if ( matches.Count == 0 )
				issues.Add( new IoProxyIssue
				{
					Code = IoProxyIssue.CodeTargetMissing,
					Message = $"Forward route '{route.Event}' targets '{route.TargetName}', which is not an entity in sub-map '{subMap.Name}'.",
				} );
			else if ( matches.Count > 1 )
				issues.Add( new IoProxyIssue
				{
					Severity = "Warning",
					Code = IoProxyIssue.CodeTargetAmbiguous,
					Message = $"Forward route '{route.Event}' targets '{route.TargetName}', which names {matches.Count} entities in sub-map '{subMap.Name}'.",
				} );
		}
		foreach ( var route in Backward )
		{
			string? fieldError = route.Validate();
			if ( fieldError is not null )
			{
				issues.Add( new IoProxyIssue { Code = IoProxyIssue.CodeRouteInvalid, Message = $"Backward route '{route.Event}' -> '{route.TargetName}' is invalid: {fieldError}." } );
				continue;
			}
			if ( !route.Enabled ) continue;
			// The backward SOURCE must be an instance entity; the TARGET is
			// host-global and is the host map's own validation to check.
			if ( string.IsNullOrWhiteSpace( route.SourceEntity )
				|| route.SourceEntity.StartsWith( "#", StringComparison.Ordinal )
				|| LibraryNameFixup.IsSpecialTarget( route.SourceEntity ) ) continue;
			var matches = subMap.FindAllByTargetName( route.SourceEntity );
			if ( matches.Count == 0 )
				issues.Add( new IoProxyIssue
				{
					Code = IoProxyIssue.CodeTargetMissing,
					Message = $"Backward route '{route.Event}' fires on '{route.SourceEntity}', which is not an entity in sub-map '{subMap.Name}'.",
				} );
			else if ( matches.Count > 1 )
				issues.Add( new IoProxyIssue
				{
					Severity = "Warning",
					Code = IoProxyIssue.CodeTargetAmbiguous,
					Message = $"Backward route '{route.Event}' fires on '{route.SourceEntity}', which names {matches.Count} entities in sub-map '{subMap.Name}'.",
				} );
		}
		return IoProxyValidationResult.FromIssues( issues );
	}

	/// <summary>
	/// Resolves the enabled forward routes against a resolve's name map:
	/// each becomes a <see cref="LibraryIoOutput"/> from this proxy
	/// (SourceEntity = <see cref="Name"/>) to the prefixed instance
	/// target. Routes whose target has no entry in the name map (unknown
	/// to the sub-map) are skipped and listed in
	/// <paramref name="unresolved"/>; they are never guessed.
	/// </summary>
	public IReadOnlyList<LibraryIoOutput> ResolveForward(
		IReadOnlyDictionary<string, string> nameMap,
		out IReadOnlyList<string> unresolved )
	{
		var resolved = new List<LibraryIoOutput>();
		var missing = new List<string>();
		foreach ( var route in Forward )
		{
			if ( !route.Enabled ) continue;
			if ( LibraryNameFixup.IsSpecialTarget( route.TargetName ) )
			{
				resolved.Add( ToOutput( route, route.TargetName ) );
				continue;
			}
			if ( nameMap is null || !nameMap.TryGetValue( route.TargetName, out string? mapped ) )
			{
				missing.Add( route.TargetName );
				continue;
			}
			resolved.Add( ToOutput( route, mapped ) );
		}
		unresolved = missing;
		return resolved;
	}

	/// <summary>
	/// Resolves the enabled backward routes: each becomes a
	/// <see cref="LibraryIoOutput"/> from the prefixed instance entity
	/// (<see cref="IoProxyRoute.SourceEntity"/> mapped through
	/// <paramref name="nameMap"/>; "#&lt;id&gt;" and '!' sources pass
	/// through unchanged) to the host target. The host target is already
	/// global and is never rewritten.
	/// </summary>
	public IReadOnlyList<LibraryIoOutput> ResolveBackward( IReadOnlyDictionary<string, string>? nameMap = null )
	{
		var resolved = new List<LibraryIoOutput>();
		foreach ( var route in Backward )
		{
			if ( !route.Enabled ) continue;
			string source = route.SourceEntity;
			if ( !string.IsNullOrEmpty( source )
				&& !source.StartsWith( "#", StringComparison.Ordinal )
				&& !LibraryNameFixup.IsSpecialTarget( source )
				&& nameMap is not null
				&& nameMap.TryGetValue( source, out string? mapped ) )
				source = mapped;
			resolved.Add( new LibraryIoOutput
			{
				SourceEntity = source,
				Event = route.Event,
				TargetName = route.TargetName,
				Input = route.Input,
				Parameter = route.Parameter,
				DelaySeconds = route.DelaySeconds,
				MaxFires = route.MaxFires,
				Enabled = route.Enabled,
			} );
		}
		return resolved;
	}

	/// <summary>
	/// Remaps this proxy's legs through a library-update rename map (old
	/// -&gt; new targetname, case-insensitive): forward targets and
	/// backward sources name instance entities as authored, so they follow
	/// the rename; backward targets are host-global and are never touched,
	/// and '#&lt;id&gt;' and '!' names pass through. Returns how many legs
	/// were remapped. Call this with the same rename map an
	/// <see cref="InstanceLibraryService.UpdateInstance"/> used, then
	/// re-validate the proxy against the new sub-map version.
	/// </summary>
	public int RemapRenames( IReadOnlyDictionary<string, string>? renames )
	{
		if ( renames is null || renames.Count == 0 ) return 0;
		int remapped = 0;
		foreach ( var route in Forward )
		{
			if ( string.IsNullOrEmpty( route.TargetName ) || LibraryNameFixup.IsSpecialTarget( route.TargetName ) ) continue;
			if ( renames.TryGetValue( route.TargetName, out string? mapped ) ) { route.TargetName = mapped; remapped++; }
		}
		foreach ( var route in Backward )
		{
			if ( string.IsNullOrEmpty( route.SourceEntity ) || route.SourceEntity.StartsWith( "#", StringComparison.Ordinal )
				|| LibraryNameFixup.IsSpecialTarget( route.SourceEntity ) ) continue;
			if ( renames.TryGetValue( route.SourceEntity, out string? mapped ) ) { route.SourceEntity = mapped; remapped++; }
		}
		return remapped;
	}

	LibraryIoOutput ToOutput( IoProxyRoute route, string target ) => new()
	{
		SourceEntity = Name,
		Event = route.Event,
		TargetName = target,
		Input = route.Input,
		Parameter = route.Parameter,
		DelaySeconds = route.DelaySeconds,
		MaxFires = route.MaxFires,
		Enabled = route.Enabled,
	};

	/// <summary>VMF connections-block lines for the resolved forward routes — the lines VmfIo writes into this proxy entity's "connections" block.</summary>
	public IReadOnlyList<string> ToVmfLines( IReadOnlyDictionary<string, string> nameMap )
	{
		var rows = ResolveForward( nameMap, out _ );
		return rows.Select( r => r.ToVmfLine() ).ToList();
	}
}

/// <summary>The map-document store for IO proxies. MapDocument embeds <see cref="ToJson"/> verbatim as its "instance_io_proxies" section.</summary>
public sealed class LibraryIoProxySet
{
	/// <summary>Section schema version. Bump only with a migration, never silently.</summary>
	public const int SchemaVersion = 1;

	readonly List<LibraryIoProxy> _proxies = new();
	int _nextProxy = 1;

	public IReadOnlyList<LibraryIoProxy> Proxies => _proxies;

	public LibraryIoProxy? Find( string proxyId ) =>
		_proxies.FirstOrDefault( p => string.Equals( p.Id, proxyId, StringComparison.Ordinal ) );

	/// <summary>Adds a proxy for an instance. Refuses a blank proxy name or instance id, with a named reason.</summary>
	public InstancesCollabResult Add( string name, string instanceId )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return InstancesCollabResult.Failure( "instancescollab-invalid-proxy", "An IO proxy needs a targetname in the host map." );
		if ( string.IsNullOrWhiteSpace( instanceId ) )
			return InstancesCollabResult.Failure( "instancescollab-instance-not-found", "An IO proxy needs a library instance id." );
		_proxies.Add( new LibraryIoProxy { Id = $"ioproxy-{_nextProxy++}", Name = name, InstanceId = instanceId } );
		return InstancesCollabResult.Success();
	}

	/// <summary>The proxy just added by the last <see cref="Add"/> call, or null.</summary>
	public LibraryIoProxy? LastAdded => _proxies.Count == 0 ? null : _proxies[_proxies.Count - 1];

	/// <summary>Removes a proxy. False + a named reason when it does not exist.</summary>
	public InstancesCollabResult Remove( string proxyId )
	{
		var proxy = Find( proxyId );
		if ( proxy is null )
			return InstancesCollabResult.Failure( "instancescollab-proxy-not-found", $"No IO proxy '{proxyId}'." );
		_proxies.Remove( proxy );
		return InstancesCollabResult.Success();
	}

	// ---- JSON (map-document section, version 1) ----

	sealed class RouteJson
	{
		public string source_entity { get; set; } = "";
		public string @event { get; set; } = "";
		public string target { get; set; } = "";
		public string input { get; set; } = "";
		public string parameter { get; set; } = "";
		public double delay_seconds { get; set; }
		public int max_fires { get; set; } = -1;
		public bool enabled { get; set; } = true;
	}

	sealed class ProxyJson
	{
		public string id { get; set; } = "";
		public string name { get; set; } = "";
		public string instance_id { get; set; } = "";
		public List<RouteJson> forward { get; set; } = new();
		public List<RouteJson> backward { get; set; } = new();
	}

	sealed class SetJson
	{
		public int version { get; set; } = 1;
		public int next_proxy { get; set; }
		public List<ProxyJson> proxies { get; set; } = new();
	}

	static RouteJson ToJson( IoProxyRoute r ) => new()
	{
		source_entity = r.SourceEntity,
		@event = r.Event,
		target = r.TargetName,
		input = r.Input,
		parameter = r.Parameter,
		delay_seconds = r.DelaySeconds,
		max_fires = r.MaxFires,
		enabled = r.Enabled,
	};

	static IoProxyRoute FromJson( RouteJson j ) => new()
	{
		SourceEntity = j.source_entity ?? "",
		Event = j.@event ?? "",
		TargetName = j.target ?? "",
		Input = j.input ?? "",
		Parameter = j.parameter ?? "",
		DelaySeconds = j.delay_seconds,
		MaxFires = j.max_fires,
		Enabled = j.enabled,
	};

	/// <summary>Serialises this set as the "instance_io_proxies" section of a map document (version 1). Deterministic: proxies in added order, no timestamps.</summary>
	public string ToJson()
	{
		var doc = new SetJson
		{
			next_proxy = _nextProxy,
			proxies = _proxies.Select( p => new ProxyJson
			{
				id = p.Id,
				name = p.Name,
				instance_id = p.InstanceId,
				forward = p.Forward.Select( ToJson ).ToList(),
				backward = p.Backward.Select( ToJson ).ToList(),
			} ).ToList(),
		};
		return JsonSerializer.Serialize( doc, new JsonSerializerOptions { WriteIndented = true } );
	}

	/// <summary>Reads back <see cref="ToJson"/> output. Throws <see cref="ArgumentException"/> naming the bad field.</summary>
	public static LibraryIoProxySet FromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) throw new ArgumentException( "instance_io_proxies JSON is empty.", nameof( json ) );
		SetJson? doc;
		try { doc = JsonSerializer.Deserialize<SetJson>( json ); }
		catch ( JsonException ex ) { throw new ArgumentException( $"instance_io_proxies JSON is not valid JSON: {ex.Message}", nameof( json ), ex ); }
		if ( doc is null ) throw new ArgumentException( "instance_io_proxies JSON is empty.", nameof( json ) );
		if ( doc.version != 1 ) throw new ArgumentException( $"unsupported instance_io_proxies version {doc.version}; expected 1.", nameof( json ) );
		var set = new LibraryIoProxySet { _nextProxy = Math.Max( 1, doc.next_proxy ) };
		var ids = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var pj in doc.proxies ?? new List<ProxyJson>() )
		{
			if ( pj is null ) throw new ArgumentException( "instance_io_proxies proxy row is null.", nameof( json ) );
			if ( string.IsNullOrWhiteSpace( pj.id ) ) throw new ArgumentException( "instance_io_proxies proxy is missing 'id'.", nameof( json ) );
			if ( !ids.Add( pj.id ) ) throw new ArgumentException( $"instance_io_proxies proxy id '{pj.id}' appears twice.", nameof( json ) );
			if ( string.IsNullOrWhiteSpace( pj.name ) ) throw new ArgumentException( $"instance_io_proxies proxy '{pj.id}' is missing 'name'.", nameof( json ) );
			var proxy = new LibraryIoProxy { Id = pj.id, Name = pj.name, InstanceId = pj.instance_id ?? "" };
			foreach ( var rj in pj.forward ?? new List<RouteJson>() ) proxy.Forward.Add( FromJson( rj ) );
			foreach ( var rj in pj.backward ?? new List<RouteJson>() ) proxy.Backward.Add( FromJson( rj ) );
			set._proxies.Add( proxy );
			if ( pj.id.StartsWith( "ioproxy-", StringComparison.Ordinal )
				&& int.TryParse( pj.id.AsSpan( 8 ), out int n ) && n >= set._nextProxy )
				set._nextProxy = n + 1;
		}
		return set;
	}
}

// ---- Per-map compile settings (version-controlled document section) ----

/// <summary>
/// Per-map compile settings, stored in the map document ("compile_settings"
/// section, version 1) so they are version-controlled with the map: two
/// collaborators compiling the same map get the same preset, overrides and
/// limits. Plain diffable JSON — no absolute paths, no timestamps, no
/// machine-local state (the output folder is a local run choice, not a
/// setting, and is deliberately absent). The preset + overrides mirror
/// CompilePresetDef / CompileOptions exactly; the runner consumes them by
/// preset name, and this file never runs a compile.
/// </summary>
public sealed class MapCompileSettings
{
	/// <summary>Section schema version. Bump only with a migration, never silently.</summary>
	public const int SchemaVersion = 1;

	/// <summary>Default preset for a new map: Preview — fast iteration (BSP + fast visibility + low-res lightmap). Final is the release choice.</summary>
	public CompilePreset Preset { get; set; } = CompilePreset.Preview;

	/// <summary>Override the preset's visibility quality (null = use the preset).</summary>
	public VisibilityQuality? VisibilityOverride { get; set; }

	/// <summary>Override the preset's lightmap resolution in texels per face axis (0 = use the preset). Must be 0 or 1..64.</summary>
	public int LightmapResolutionOverride { get; set; }

	/// <summary>Maximum brushes in one compile. Over it the BSP stage fails with a limit-exceeded issue.</summary>
	public int MaxBrushes { get; set; } = 16384;

	/// <summary>Maximum planes per brush. Over it the BSP stage fails with an invalid-brush issue.</summary>
	public int MaxPlanesPerBrush { get; set; } = 128;

	/// <summary>A settings object with the defaults (Preview preset, no overrides, 16384 brushes / 128 planes).</summary>
	public static MapCompileSettings Defaults() => new();

	public MapCompileSettings Clone() => new()
	{
		Preset = Preset,
		VisibilityOverride = VisibilityOverride,
		LightmapResolutionOverride = LightmapResolutionOverride,
		MaxBrushes = MaxBrushes,
		MaxPlanesPerBrush = MaxPlanesPerBrush,
	};

	/// <summary>Null when these settings are valid, otherwise a named error (same limits as CompileOptions.Validate).</summary>
	public string? Validate()
	{
		if ( !Enum.IsDefined( Preset ) ) return $"compile preset {(int)Preset} is not a known preset";
		if ( VisibilityOverride.HasValue && !Enum.IsDefined( VisibilityOverride.Value ) )
			return $"visibility override {(int)VisibilityOverride.Value} is not a known visibility quality";
		if ( MaxBrushes < 1 ) return $"MaxBrushes {MaxBrushes} must be at least 1";
		if ( MaxPlanesPerBrush < 4 ) return $"MaxPlanesPerBrush {MaxPlanesPerBrush} must be at least 4";
		if ( LightmapResolutionOverride < 0 || LightmapResolutionOverride > 64 )
			return $"LightmapResolutionOverride {LightmapResolutionOverride} must be 0 (preset) or 1..64";
		return null;
	}

	/// <summary>Stages this preset runs, in order (mirrors CompilePresetDef.Stages(): Quick Play runs BSP only).</summary>
	public IReadOnlyList<string> Stages() => Preset switch
	{
		CompilePreset.QuickPlay => new[] { "BspBuild" },
		CompilePreset.Preview => new[] { "BspBuild", "Visibility", "LightmapBake" },
		CompilePreset.Final => new[] { "BspBuild", "Visibility", "LightmapBake" },
		_ => throw new ArgumentException( $"Unknown compile preset {Preset}.", nameof( Preset ) ),
	};

	/// <summary>Effective visibility quality: the override, or the preset's (Quick Play None, Preview Fast, Final Full).</summary>
	public VisibilityQuality EffectiveVisibility() => VisibilityOverride ?? (Preset switch
	{
		CompilePreset.QuickPlay => VisibilityQuality.None,
		CompilePreset.Preview => VisibilityQuality.Fast,
		CompilePreset.Final => VisibilityQuality.Full,
		_ => throw new ArgumentException( $"Unknown compile preset {Preset}.", nameof( Preset ) ),
	});

	/// <summary>Effective lightmap resolution in texels per face axis: the override, or the preset's (Quick Play 0 = no bake, Preview 4, Final 16).</summary>
	public int EffectiveLightmapResolution()
	{
		if ( LightmapResolutionOverride != 0 ) return LightmapResolutionOverride;
		return Preset switch
		{
			CompilePreset.QuickPlay => 0,
			CompilePreset.Preview => 4,
			CompilePreset.Final => 16,
			_ => throw new ArgumentException( $"Unknown compile preset {Preset}.", nameof( Preset ) ),
		};
	}

	// ---- JSON (map-document section, version 1) ----

	sealed class SettingsJson
	{
		public int version { get; set; } = 1;
		public string preset { get; set; } = "";
		public string? visibility_override { get; set; }
		public int lightmap_resolution_override { get; set; }
		public int max_brushes { get; set; }
		public int max_planes_per_brush { get; set; }
	}

	/// <summary>Serialises these settings as the "compile_settings" section of a map document (version 1). Deterministic: no timestamps, no paths.</summary>
	public string ToJson()
	{
		var doc = new SettingsJson
		{
			preset = Preset.ToString(),
			visibility_override = VisibilityOverride?.ToString(),
			lightmap_resolution_override = LightmapResolutionOverride,
			max_brushes = MaxBrushes,
			max_planes_per_brush = MaxPlanesPerBrush,
		};
		return JsonSerializer.Serialize( doc, new JsonSerializerOptions { WriteIndented = true } );
	}

	/// <summary>Reads back <see cref="ToJson"/> output. Throws <see cref="ArgumentException"/> naming the bad field. Unknown JSON fields are ignored (forward compatibility).</summary>
	public static MapCompileSettings FromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) throw new ArgumentException( "compile_settings JSON is empty.", nameof( json ) );
		SettingsJson? doc;
		try { doc = JsonSerializer.Deserialize<SettingsJson>( json ); }
		catch ( JsonException ex ) { throw new ArgumentException( $"compile_settings JSON is not valid JSON: {ex.Message}", nameof( json ), ex ); }
		if ( doc is null ) throw new ArgumentException( "compile_settings JSON is empty.", nameof( json ) );
		if ( doc.version != 1 ) throw new ArgumentException( $"unsupported compile_settings version {doc.version}; expected 1.", nameof( json ) );
		if ( !Enum.TryParse<CompilePreset>( doc.preset, out var preset ) )
			throw new ArgumentException( $"compile_settings has unknown preset '{doc.preset}'; expected QuickPlay, Preview or Final.", nameof( json ) );
		VisibilityQuality? visibility = null;
		if ( doc.visibility_override is not null )
		{
			if ( !Enum.TryParse<VisibilityQuality>( doc.visibility_override, out var parsed ) )
				throw new ArgumentException( $"compile_settings has unknown visibility override '{doc.visibility_override}'; expected None, Fast or Full.", nameof( json ) );
			visibility = parsed;
		}
		var settings = new MapCompileSettings
		{
			Preset = preset,
			VisibilityOverride = visibility,
			LightmapResolutionOverride = doc.lightmap_resolution_override,
			MaxBrushes = doc.max_brushes,
			MaxPlanesPerBrush = doc.max_planes_per_brush,
		};
		string? error = settings.Validate();
		if ( error is not null ) throw new ArgumentException( $"compile_settings is invalid: {error}.", nameof( json ) );
		return settings;
	}
}
