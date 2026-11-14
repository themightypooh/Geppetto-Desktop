using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;

namespace Effigy.Map;

/// <summary>
/// Hammer Desktop P3: soundscapes + env_soundscape zones + particle preview
/// data (hammer-soundscapes-particle-preview-backend). Backend / data-model /
/// API only — no Desktop UI, no preview UI: this file is the native map data
/// (soundscape zones, ambient_generic wiring, info_particle_system
/// references), registry validation against the sound/particle registries,
/// and the preview DATA a future viewport/UI caller consumes (which
/// soundscape is active at a point, with what attenuation; what a particle
/// system evaluates to at time t).
///
/// DEPENDENCY STATUS (2026-11-13, verified against the tree):
///   * EntityRegistry (hammer-entity-registry-fgd-json, DONE):
///     kernel/Effigy/Map/EntityRegistry.cs + registry/core-source1.json (14
///     classes). This file does NOT edit them. The soundscape classes
///     (env_soundscape, trigger_soundscape, ambient_generic,
///     info_particle_system) are NOT in the core set, so they ship as an
///     additional registry JSON in this ticket's fixtures
///     (tests/fixtures/hammer/hammer-soundscapes-particle-preview-backend/
///     hammer-soundscapes.json), loaded with EntityRegistry.LoadAdditional —
///     the P1 design's "a new registry JSON adds entities without code
///     changes". Placement/validation of those entities goes through
///     MapEntities with that extended registry; see the tests.
///   * AssetRegistries (hammer-model-particle-sound-registries, worker 61,
///     IN PROGRESS — kernel/Effigy/Map/AssetRegistries.cs does NOT exist
///     yet): its filed spec names ModelRegistry / ParticleRegistry /
///     SoundRegistry classes with search + favourites/recent tracking.
///     It files NO member names, so this file does NOT reference those
///     types. The seam is the two small interfaces below,
///     <see cref="ISoundscapeSoundResolver"/> and
///     <see cref="ISoundscapeParticleResolver"/> (one existence query
///     each), plus the <see cref="SoundscapeResolvers"/> HashSet bundle
///     for tests. When AssetRegistries lands, the integrator adds a
///     ~10-line adapter class implementing those two interfaces over
///     SoundRegistry / ParticleRegistry (their search API answers
///     "does this name exist"), and no type in this file changes.
///     This file defines NO ModelRegistry / ParticleRegistry /
///     SoundRegistry (BrushToModel.cs already defines a ModelRegistry in
///     Effigy.Map, and worker 61 owns the canonical ones) and NO second
///     ParticleSystemDef (see below).
///   * PreviewEffects (hammer-preview-wysiwyg-effects, DONE):
///     kernel/Effigy/Map/PreviewEffects.cs, namespace Effigy. Its
///     PreviewParticleSystem is deliberately NOT the kernel's existing
///     ParticleSystemDef (the Desktop particle-authoring definition); it
///     is the small Source-1 info_particle_system parameter set. This
///     file does NOT introduce a second ParticleSystemDef either.
///     <see cref="ParticleSystemRef.ToPreviewParticleSystem"/> is the
///     thin adapter PreviewEffects' header asks for: it maps a map
///     particle reference onto PreviewParticleSystem, and
///     <see cref="ParticlePreview.Preview"/> evaluates it with
///     PreviewEffects.EvaluateParticles. No second evaluator exists.
///   * IssueEngine (hammer-validation-issue-engine, DONE): its IssueKind
///     enum has no soundscape/particle kind and the file is read-only
///     for this ticket, so this file does NOT register an IIssueRule.
///     Validation findings are <see cref="SoundscapeIssue"/> (named
///     codes, same shape as MapEntityIssue). The final integrator adds
///     IssueKind entries + a rule adapter that projects these issues
///     onto Issue — named as a follow-up in the handoff, not done here.
///   * Export: VmfIo / EntityIo are the exporters (read-only here).
///     This ticket proves export inclusion at the layer it owns: every
///     type round-trips through MapEntity keyvalues
///     (<see cref="SoundscapeZone.ToKeyvalues"/>, etc.), which is the
///     data those exporters write. The tests place entities via
///     MapEntities and read them back with FromEntity.
///
/// Geometry is the kernel's inches and Z-up. Soundscape names are
/// matched case-insensitively (Source behaviour). Sound paths are
/// matched case-insensitively by the default resolvers (same choice as
/// the entity registry); a case-sensitive resolver can be supplied.
/// Errors are named, never silent: registry problems raise
/// <see cref="SoundscapeException"/>; validation problems are returned
/// as <see cref="SoundscapeIssue"/> values, never thrown.
/// </summary>
public sealed class SoundscapeException : Exception
{
	public const string CodeDuplicateSoundscape = "DuplicateSoundscape";
	public const string CodeInvalidSoundscape = "InvalidSoundscape";

	public string Code = "";
	public string SoundscapeName = "";

	public SoundscapeException( string code, string message, string soundscapeName = "" )
		: base( message ) { Code = code; SoundscapeName = soundscapeName; }
}

/// <summary>
/// One sound entry inside a soundscape definition. Volume is 0..1,
/// Pitch is Source pitch 0..255 (100 = normal). Loop plays continuously;
/// a non-looping sound replays after a random DelayMin..DelayMax seconds
/// (0/0 = play once at soundscape start). DelayMax must be &gt;= DelayMin.
/// </summary>
public sealed class SoundscapeSound
{
	public string Path = "";
	/// <summary>0..1.</summary>
	public float Volume = 1f;
	/// <summary>Source pitch 0..255, 100 = normal.</summary>
	public float Pitch = 100f;
	public bool Loop;
	/// <summary>Seconds before a non-looping sound replays (min).</summary>
	public float DelayMin;
	/// <summary>Seconds before a non-looping sound replays (max).</summary>
	public float DelayMax;

	public SoundscapeSound() { }
	public SoundscapeSound( string path, float volume = 1f, float pitch = 100f, bool loop = false, float delayMin = 0f, float delayMax = 0f )
	{
		Path = path ?? "";
		Volume = volume;
		Pitch = pitch;
		Loop = loop;
		DelayMin = delayMin;
		DelayMax = delayMax;
	}
}

/// <summary>
/// One soundscape definition: a name and the sounds that make it up.
/// This is the Geppetto-native equivalent of one entry in a Source
/// scripts/soundscapes_*.txt file (which is Valve KeyValues; here it is
/// JSON, the same format the entity registry uses).
/// </summary>
public sealed class SoundscapeDef
{
	public string Name = "";
	public string Description = "";
	public IReadOnlyList<SoundscapeSound> Sounds => _sounds;
	readonly List<SoundscapeSound> _sounds = new();

	public SoundscapeDef() { }
	public SoundscapeDef( string name, IEnumerable<SoundscapeSound> sounds = null, string description = "" )
	{
		Name = name ?? "";
		Description = description ?? "";
		if ( sounds is not null ) _sounds.AddRange( sounds );
	}

	public void AddSound( SoundscapeSound sound )
	{
		if ( sound is null ) throw new ArgumentNullException( nameof( sound ) );
		_sounds.Add( sound );
	}
}

/// <summary>
/// Seam to the sound registry (hammer-model-particle-sound-registries,
/// worker 61): does this sound path exist? When AssetRegistries lands,
/// the integrator implements this over SoundRegistry. A null resolver
/// passed to a validator means "skip the existence check", never "pass".
/// </summary>
public interface ISoundscapeSoundResolver
{
	bool SoundExists( string path );
}

/// <summary>
/// Seam to the particle registry (hammer-model-particle-sound-registries,
/// worker 61): does this particle-system definition exist? When
/// AssetRegistries lands, the integrator implements this over
/// ParticleRegistry. A null resolver passed to a validator means "skip
/// the existence check", never "pass".
/// </summary>
public interface ISoundscapeParticleResolver
{
	bool ParticleExists( string name );
}

/// <summary>
/// Seam to a soundscape registry: does this soundscape name exist, and
/// what is its definition? <see cref="SoundscapeRegistry"/> implements
/// it; validators take the interface so a future registry source can
/// substitute without changing validation.
/// </summary>
public interface ISoundscapeResolver
{
	bool SoundscapeExists( string name );
	SoundscapeDef FindSoundscape( string name );
}

/// <summary>
/// Test / adapter bundle: HashSet-backed sound + particle resolvers.
/// Case-insensitive, matching the entity registry. The integrator's
/// AssetRegistries adapter replaces this bundle with one over the real
/// registries; the validators never see the difference.
/// </summary>
public sealed class SoundscapeResolvers : ISoundscapeSoundResolver, ISoundscapeParticleResolver
{
	readonly HashSet<string> _sounds;
	readonly HashSet<string> _particles;

	public static SoundscapeResolvers Empty => FromSets( null, null );

	public static SoundscapeResolvers FromSets( IEnumerable<string> sounds, IEnumerable<string> particles )
	{
		var soundSet = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		if ( sounds is not null ) foreach ( string s in sounds ) if ( !string.IsNullOrWhiteSpace( s ) ) soundSet.Add( s.Trim() );
		var particleSet = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		if ( particles is not null ) foreach ( string p in particles ) if ( !string.IsNullOrWhiteSpace( p ) ) particleSet.Add( p.Trim() );
		return new SoundscapeResolvers( soundSet, particleSet );
	}

	SoundscapeResolvers( HashSet<string> sounds, HashSet<string> particles )
	{
		_sounds = sounds;
		_particles = particles;
	}

	public bool SoundExists( string path ) =>
		!string.IsNullOrWhiteSpace( path ) && _sounds.Contains( path.Trim() );

	public bool ParticleExists( string name ) =>
		!string.IsNullOrWhiteSpace( name ) && _particles.Contains( name.Trim() );
}

/// <summary>
/// In-memory soundscape registry: soundscape definitions by name
/// (case-insensitive). Load from the JSON format in the file header
/// (schemaVersion 1). A duplicate name is a named refusal
/// (DuplicateSoundscape), never a silent overwrite — same contract as
/// EntityRegistry.Add.
/// </summary>
public sealed class SoundscapeRegistry : ISoundscapeResolver
{
	public const int CurrentSchemaVersion = 1;

	readonly Dictionary<string, SoundscapeDef> _defs = new( StringComparer.OrdinalIgnoreCase );

	public string Name = "";
	public int SchemaVersion = CurrentSchemaVersion;
	public string SourcePath = "";

	public IReadOnlyList<SoundscapeDef> Definitions => _defs.Values.OrderBy( d => d.Name, StringComparer.OrdinalIgnoreCase ).ToArray();
	public IReadOnlyCollection<string> Names => _defs.Keys;
	public int Count => _defs.Count;

	public void Add( SoundscapeDef def )
	{
		if ( def is null ) throw new ArgumentNullException( nameof( def ) );
		if ( string.IsNullOrWhiteSpace( def.Name ) )
			throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape, "Soundscape definition has no name." );
		string name = def.Name.Trim();
		if ( _defs.ContainsKey( name ) )
			throw new SoundscapeException( SoundscapeException.CodeDuplicateSoundscape, $"Duplicate soundscape '{name}'.", name );
		def.Name = name;
		_defs.Add( name, def );
	}

	public SoundscapeDef Find( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return null;
		return _defs.TryGetValue( name.Trim(), out var def ) ? def : null;
	}

	public bool TryGet( string name, out SoundscapeDef def ) => ( def = Find( name ) ) is not null;

	public bool Contains( string name ) => Find( name ) is not null;

	bool ISoundscapeResolver.SoundscapeExists( string name ) => Contains( name );
	SoundscapeDef ISoundscapeResolver.FindSoundscape( string name ) => Find( name );

	/// <summary>Validate every definition's sounds against a sound resolver. Null resolver = skip existence checks (range checks still run).</summary>
	public IReadOnlyList<SoundscapeIssue> ValidateAll( ISoundscapeSoundResolver sounds )
	{
		var issues = new List<SoundscapeIssue>();
		foreach ( var def in _defs.Values )
			issues.AddRange( SoundscapeValidator.ValidateSoundscapeDef( def, sounds ) );
		return issues;
	}

	public static SoundscapeRegistry Load( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape, "Soundscape file path is empty." );
		string json;
		try { json = File.ReadAllText( path ); }
		catch ( Exception ex ) { throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape, $"Cannot read soundscape file '{path}': {ex.Message}" ); }
		var registry = LoadFromJson( json, path );
		registry.SourcePath = path;
		return registry;
	}

	public static SoundscapeRegistry LoadFromJson( string json, string sourceName = "" )
	{
		var registry = new SoundscapeRegistry();
		registry.LoadJsonInto( json, sourceName );
		return registry;
	}

	void LoadJsonInto( string json, string sourceName )
	{
		JsonDocument doc;
		try { doc = JsonDocument.Parse( json ); }
		catch ( JsonException ex ) { throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape, $"Soundscape file '{sourceName}' is malformed JSON: {ex.Message}" ); }
		using ( doc )
		{
			var root = doc.RootElement;
			if ( root.ValueKind != JsonValueKind.Object )
				throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape, $"Soundscape file '{sourceName}' must be a JSON object." );
			if ( root.TryGetProperty( "schemaVersion", out var versionEl ) )
			{
				if ( versionEl.ValueKind != JsonValueKind.Number || !versionEl.TryGetInt32( out int version ) || version != CurrentSchemaVersion )
					throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape, $"Soundscape file '{sourceName}' has unsupported schemaVersion '{versionEl.GetRawText()}'; expected {CurrentSchemaVersion}." );
				SchemaVersion = version;
			}
			if ( root.TryGetProperty( "name", out var nameEl ) && nameEl.ValueKind == JsonValueKind.String )
				Name = nameEl.GetString() ?? "";
			if ( !root.TryGetProperty( "soundscapes", out var listEl ) || listEl.ValueKind != JsonValueKind.Array )
				throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape, $"Soundscape file '{sourceName}' has no 'soundscapes' array." );
			foreach ( var defEl in listEl.EnumerateArray() )
				Add( ParseDef( defEl, sourceName ) );
		}
	}

	static SoundscapeDef ParseDef( JsonElement el, string sourceName )
	{
		if ( el.ValueKind != JsonValueKind.Object )
			throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape, $"Soundscape file '{sourceName}': each soundscape must be an object." );
		string name = RequiredString( el, "name", sourceName );
		var def = new SoundscapeDef( name.Trim(), null, OptionalString( el, "description" ) );
		if ( el.TryGetProperty( "sounds", out var soundsEl ) )
		{
			if ( soundsEl.ValueKind != JsonValueKind.Array )
				throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape, $"Soundscape '{name}': 'sounds' must be an array.", name );
			foreach ( var soundEl in soundsEl.EnumerateArray() )
				def.AddSound( ParseSound( soundEl, name, sourceName ) );
		}
		return def;
	}

	static SoundscapeSound ParseSound( JsonElement el, string soundscapeName, string sourceName )
	{
		if ( el.ValueKind != JsonValueKind.Object )
			throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape, $"Soundscape '{soundscapeName}': each sound must be an object.", soundscapeName );
		string path = RequiredString( el, "path", sourceName, soundscapeName );
		float volume = OptionalFloat( el, "volume", 1f );
		float pitch = OptionalFloat( el, "pitch", 100f );
		bool loop = el.TryGetProperty( "loop", out var loopEl ) && loopEl.ValueKind == JsonValueKind.True;
		float delayMin = OptionalFloat( el, "delayMin", 0f );
		float delayMax = OptionalFloat( el, "delayMax", 0f );
		if ( volume < 0f || volume > 1f )
			throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape, $"Soundscape '{soundscapeName}': sound '{path}' has volume {volume}, expected 0..1.", soundscapeName );
		if ( pitch < 0f || pitch > 255f )
			throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape, $"Soundscape '{soundscapeName}': sound '{path}' has pitch {pitch}, expected 0..255.", soundscapeName );
		if ( delayMin < 0f || delayMax < delayMin )
			throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape, $"Soundscape '{soundscapeName}': sound '{path}' has delay range {delayMin}..{delayMax}, expected 0 <= min <= max.", soundscapeName );
		return new SoundscapeSound( path.Trim(), volume, pitch, loop, delayMin, delayMax );
	}

	static string RequiredString( JsonElement el, string property, string sourceName, string soundscapeName = "" )
	{
		if ( !el.TryGetProperty( property, out var valueEl ) || valueEl.ValueKind != JsonValueKind.String
			|| string.IsNullOrWhiteSpace( valueEl.GetString() ) )
			throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape,
				string.IsNullOrEmpty( soundscapeName )
					? $"Soundscape file '{sourceName}': missing or empty string property '{property}'."
					: $"Soundscape '{soundscapeName}': missing or empty string property '{property}'.", soundscapeName );
		return valueEl.GetString();
	}

	static string OptionalString( JsonElement el, string property, string fallback = "" )
	{
		if ( el.TryGetProperty( property, out var valueEl ) && valueEl.ValueKind == JsonValueKind.String )
			return valueEl.GetString() ?? fallback;
		return fallback;
	}

	static float OptionalFloat( JsonElement el, string property, float fallback )
	{
		if ( !el.TryGetProperty( property, out var valueEl ) ) return fallback;
		if ( valueEl.ValueKind == JsonValueKind.Number && valueEl.TryGetSingle( out float f ) && float.IsFinite( f ) ) return f;
		throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape, $"Soundscape property '{property}' must be a finite number." );
	}
}

/// <summary>
/// A soundscape zone: native map data for env_soundscape (point entity
/// with a radius) or trigger_soundscape (brush trigger volume naming a
/// soundscape). Origin is the MapEntity origin (inches, Z-up, not a
/// keyvalue — same rule as MapEntities). Radius &lt;= 0 means global: the
/// zone is active at any distance. A bounds (AABB) form covers a trigger
/// volume whose extent comes from its tied brushes: set Min/Max from the
/// brush bounds when placing (MapBrush has no AABB accessor this ticket
/// can call, so the caller supplies the bounds; FromEntity on a
/// trigger_soundscape leaves HasBounds false and documents it).
/// Contains() prefers bounds when present, else the radius rule.
/// </summary>
public sealed class SoundscapeZone
{
	public const string ClassName = "env_soundscape";
	public const string TriggerClassName = "trigger_soundscape";

	public string TargetName = "";
	public string SoundscapeName = "";
	public Vec3 Origin = Vec3.Zero;
	/// <summary>Active radius in inches. 0 = global (active everywhere). Negative is invalid (see validation).</summary>
	public float Radius;
	public Vec3 Min = Vec3.Zero;
	public Vec3 Max = Vec3.Zero;
	/// <summary>True when Min/Max describe a trigger volume.</summary>
	public bool HasBounds;

	public SoundscapeZone() { }
	public SoundscapeZone( string soundscapeName, Vec3 origin, float radius = 0f, string targetName = "" )
	{
		SoundscapeName = soundscapeName ?? "";
		Origin = origin;
		Radius = radius;
		TargetName = targetName ?? "";
	}

	/// <summary>Is the point inside this zone? Bounds form: inclusive AABB. Radius form: distance &lt;= Radius. Global (radius 0, no bounds): always true.</summary>
	public bool Contains( Vec3 point )
	{
		if ( HasBounds )
			return point.x >= Min.x && point.x <= Max.x
				&& point.y >= Min.y && point.y <= Max.y
				&& point.z >= Min.z && point.z <= Max.z;
		if ( Radius > 0f ) return ( point - Origin ).Length <= Radius;
		return true;
	}

	/// <summary>Distance from a point to the zone origin (nearest-zone ordering in preview). For a bounds zone this is the distance to its origin, not to the box face.</summary>
	public float DistanceTo( Vec3 point ) => ( point - Origin ).Length;

	/// <summary>Keyvalues for an env_soundscape point entity (what MapEntities.PlacePointEntity takes). "origin" is not a keyvalue — it lives on the MapEntity.</summary>
	public Dictionary<string, string> ToKeyvalues()
	{
		var kv = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		if ( !string.IsNullOrWhiteSpace( TargetName ) ) kv["targetname"] = TargetName;
		kv["soundscape"] = SoundscapeName;
		kv["radius"] = Radius.ToString( "R", CultureInfo.InvariantCulture );
		return kv;
	}

	/// <summary>Keyvalues for a trigger_soundscape brush entity: soundscape name only (a trigger volume has no radius key).</summary>
	public Dictionary<string, string> ToTriggerKeyvalues()
	{
		var kv = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		if ( !string.IsNullOrWhiteSpace( TargetName ) ) kv["targetname"] = TargetName;
		kv["soundscape"] = SoundscapeName;
		return kv;
	}

	/// <summary>Read a zone back from a placed MapEntity (env_soundscape or trigger_soundscape). Throws SoundscapeException (InvalidSoundscape) for any other class or an unparsable radius.</summary>
	public static SoundscapeZone FromEntity( MapEntity entity )
	{
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		return FromKeyvalues( entity.ClassName, entity.Origin, entity.Keyvalues, entity.TargetName );
	}

	/// <summary>Shared FromEntity core, callable without a MapEntity (tests, importers).</summary>
	public static SoundscapeZone FromKeyvalues( string className, Vec3 origin, IReadOnlyDictionary<string, string> keyvalues, string targetName = "" )
	{
		bool isEnv = string.Equals( className, ClassName, StringComparison.OrdinalIgnoreCase );
		bool isTrigger = string.Equals( className, TriggerClassName, StringComparison.OrdinalIgnoreCase );
		if ( !isEnv && !isTrigger )
			throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape,
				$"Cannot read a soundscape zone from entity class '{className}': expected '{ClassName}' or '{TriggerClassName}'." );
		var zone = new SoundscapeZone { Origin = origin, TargetName = targetName ?? "" };
		if ( keyvalues is not null && keyvalues.TryGetValue( "soundscape", out string name ) ) zone.SoundscapeName = name ?? "";
		if ( isEnv )
		{
			if ( keyvalues is not null && keyvalues.TryGetValue( "radius", out string radiusText ) )
			{
				if ( !float.TryParse( radiusText, NumberStyles.Float, CultureInfo.InvariantCulture, out float radius ) || !float.IsFinite( radius ) )
					throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape,
						$"Soundscape zone radius '{radiusText}' is not a finite number." );
				zone.Radius = radius;
			}
		}
		// Trigger volumes: bounds live on the tied brushes, not in keyvalues,
		// so a zone read back from a trigger_soundscape has HasBounds false.
		// The caller re-applies Min/Max from the brush bounds when known.
		return zone;
	}
}

/// <summary>
/// ambient_generic wiring: one-off / looping sound emitter. Source keys,
/// exactly as the Source FGD names them: "message" is the sound path,
/// "health" is volume 0..10 (this type stores Volume 0..1 and maps it to
/// health = round(volume * 10) — Source's own granularity, so a round
/// trip quantises volume to 0.1 steps; documented, not hidden), "pitch"
/// is Source pitch 0..255 (100 = normal), "radius" is the audible radius
/// in inches (0 = infinite). "startsilent" / "loop" are Geppetto
/// extension keys (int 0/1): Source ignores unknown keys, Geppetto
/// preserves them through the round trip. Spawnflags are NOT used for
/// these — the exact Source flag bits were not verified against a Source
/// FGD on this ticket, and inventing them would be worse than a named
/// extension key.
/// </summary>
public sealed class AmbientGenericRef
{
	public const string ClassName = "ambient_generic";

	public string TargetName = "";
	public string SoundPath = "";
	/// <summary>0..1. Stored as Source "health" 0..10 (see the class doc).</summary>
	public float Volume = 1f;
	/// <summary>Source pitch 0..255, 100 = normal.</summary>
	public float Pitch = 100f;
	/// <summary>Audible radius in inches. 0 = infinite. Negative is invalid.</summary>
	public float Radius;
	public bool StartSilent;
	public bool Loop;

	public AmbientGenericRef() { }
	public AmbientGenericRef( string soundPath, float volume = 1f, float pitch = 100f, float radius = 0f, string targetName = "" )
	{
		SoundPath = soundPath ?? "";
		Volume = volume;
		Pitch = pitch;
		Radius = radius;
		TargetName = targetName ?? "";
	}

	/// <summary>Volume 0..1 mapped to Source health 0..10 (round half away from zero).</summary>
	public static int VolumeToHealth( float volume )
	{
		float clamped = volume < 0f ? 0f : volume > 1f ? 1f : volume;
		return (int)Math.Round( clamped * 10f, MidpointRounding.AwayFromZero );
	}

	public static float HealthToVolume( int health )
	{
		int clamped = health < 0 ? 0 : health > 10 ? 10 : health;
		return clamped / 10f;
	}

	/// <summary>Keyvalues for an ambient_generic point entity (what MapEntities.PlacePointEntity takes).</summary>
	public Dictionary<string, string> ToKeyvalues()
	{
		var kv = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		if ( !string.IsNullOrWhiteSpace( TargetName ) ) kv["targetname"] = TargetName;
		kv["message"] = SoundPath;
		kv["health"] = VolumeToHealth( Volume ).ToString( CultureInfo.InvariantCulture );
		kv["pitch"] = ( (int)Math.Round( Pitch ) ).ToString( CultureInfo.InvariantCulture );
		kv["radius"] = Radius.ToString( "R", CultureInfo.InvariantCulture );
		kv["startsilent"] = StartSilent ? "1" : "0";
		kv["loop"] = Loop ? "1" : "0";
		return kv;
	}

	/// <summary>Read an ambient_generic back from a placed MapEntity. Throws SoundscapeException (InvalidSoundscape) for any other class or an unparsable numeric key.</summary>
	public static AmbientGenericRef FromEntity( MapEntity entity )
	{
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		return FromKeyvalues( entity.ClassName, entity.Keyvalues, entity.TargetName );
	}

	public static AmbientGenericRef FromKeyvalues( string className, IReadOnlyDictionary<string, string> keyvalues, string targetName = "" )
	{
		if ( !string.Equals( className, ClassName, StringComparison.OrdinalIgnoreCase ) )
			throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape,
				$"Cannot read an ambient_generic from entity class '{className}'." );
		var ambient = new AmbientGenericRef { TargetName = targetName ?? "" };
		if ( keyvalues is null ) return ambient;
		if ( keyvalues.TryGetValue( "message", out string path ) ) ambient.SoundPath = path ?? "";
		if ( keyvalues.TryGetValue( "health", out string healthText ) )
		{
			if ( !int.TryParse( healthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int health ) )
				throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape, $"ambient_generic health '{healthText}' is not an integer." );
			ambient.Volume = HealthToVolume( health );
		}
		if ( keyvalues.TryGetValue( "pitch", out string pitchText ) )
		{
			if ( !float.TryParse( pitchText, NumberStyles.Float, CultureInfo.InvariantCulture, out float pitch ) || !float.IsFinite( pitch ) )
				throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape, $"ambient_generic pitch '{pitchText}' is not a finite number." );
			ambient.Pitch = pitch;
		}
		if ( keyvalues.TryGetValue( "radius", out string radiusText ) )
		{
			if ( !float.TryParse( radiusText, NumberStyles.Float, CultureInfo.InvariantCulture, out float radius ) || !float.IsFinite( radius ) )
				throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape, $"ambient_generic radius '{radiusText}' is not a finite number." );
			ambient.Radius = radius;
		}
		if ( keyvalues.TryGetValue( "startsilent", out string silentText ) ) ambient.StartSilent = silentText == "1";
		if ( keyvalues.TryGetValue( "loop", out string loopText ) ) ambient.Loop = loopText == "1";
		return ambient;
	}
}

/// <summary>
/// info_particle_system reference: a particle system placed in the map
/// by definition name. Source keys: "effect_name" (the particle-system
/// definition name, resolved against the particle registry),
/// "start_active" (1/0), "cpoint1".."cpoint7" (control-point target
/// entities). Extra keyvalues (the preview parameters the fixture
/// registry declares — emissionrate, lifetime, speed, spread,
/// startsize, endsize, gravity) ride in <see cref="Params"/> and round
/// trip through the entity; keys the registry does not declare are
/// rejected by MapEntities at placement, exactly like any other
/// keyvalue, so Params can only ever hold declared keys.
/// </summary>
public sealed class ParticleSystemRef
{
	public const string ClassName = "info_particle_system";
	public const int MaxControlPoints = 7;

	public string TargetName = "";
	public string EffectName = "";
	public Vec3 Origin = Vec3.Zero;
	public Vec3 Angles = Vec3.Zero;
	public bool StartActive = true;
	public IReadOnlyList<string> ControlPoints => _controlPoints;
	readonly List<string> _controlPoints = new();
	/// <summary>Extra declared keyvalues (preview parameters), case-insensitive keys.</summary>
	public IReadOnlyDictionary<string, string> Params => _params;
	readonly Dictionary<string, string> _params = new( StringComparer.OrdinalIgnoreCase );

	public ParticleSystemRef() { }
	public ParticleSystemRef( string effectName, Vec3 origin, bool startActive = true, string targetName = "" )
	{
		EffectName = effectName ?? "";
		Origin = origin;
		StartActive = startActive;
		TargetName = targetName ?? "";
	}

	public void AddControlPoint( string targetName )
	{
		if ( string.IsNullOrWhiteSpace( targetName ) ) throw new ArgumentException( "Control point target name is empty.", nameof( targetName ) );
		if ( _controlPoints.Count >= MaxControlPoints )
			throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape,
				$"info_particle_system supports at most {MaxControlPoints} control points (cpoint1..cpoint7)." );
		_controlPoints.Add( targetName.Trim() );
	}

	public void SetParam( string key, string value )
	{
		if ( string.IsNullOrWhiteSpace( key ) ) throw new ArgumentException( "Parameter key is empty.", nameof( key ) );
		_params[key.Trim()] = value ?? "";
	}

	/// <summary>Keyvalues for an info_particle_system point entity (what MapEntities.PlacePointEntity takes). "origin"/"angles" live on the MapEntity, not here — angles is mirrored into keyvalues by MapEntities itself.</summary>
	public Dictionary<string, string> ToKeyvalues()
	{
		var kv = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		if ( !string.IsNullOrWhiteSpace( TargetName ) ) kv["targetname"] = TargetName;
		kv["effect_name"] = EffectName;
		kv["start_active"] = StartActive ? "1" : "0";
		for ( int i = 0; i < _controlPoints.Count; i++ ) kv["cpoint" + ( i + 1 )] = _controlPoints[i];
		foreach ( var pair in _params ) kv[pair.Key] = pair.Value;
		return kv;
	}

	/// <summary>Read a particle reference back from a placed MapEntity. Throws SoundscapeException (InvalidSoundscape) for any other class.</summary>
	public static ParticleSystemRef FromEntity( MapEntity entity )
	{
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		return FromKeyvalues( entity.ClassName, entity.Origin, entity.Angles, entity.Keyvalues, entity.TargetName );
	}

	public static ParticleSystemRef FromKeyvalues( string className, Vec3 origin, Vec3 angles, IReadOnlyDictionary<string, string> keyvalues, string targetName = "" )
	{
		if ( !string.Equals( className, ClassName, StringComparison.OrdinalIgnoreCase ) )
			throw new SoundscapeException( SoundscapeException.CodeInvalidSoundscape,
				$"Cannot read an info_particle_system from entity class '{className}'." );
		var particle = new ParticleSystemRef { Origin = origin, Angles = angles, TargetName = targetName ?? "" };
		if ( keyvalues is null ) return particle;
		foreach ( var pair in keyvalues )
		{
			string key = pair.Key;
			string value = pair.Value ?? "";
			if ( string.Equals( key, "effect_name", StringComparison.OrdinalIgnoreCase ) ) particle.EffectName = value;
			else if ( string.Equals( key, "start_active", StringComparison.OrdinalIgnoreCase ) ) particle.StartActive = value != "0";
			else if ( key.Length == 7 && key.StartsWith( "cpoint", StringComparison.OrdinalIgnoreCase )
				&& key[6] >= '1' && key[6] <= '7' && !string.IsNullOrWhiteSpace( value ) )
			{
				int index = key[6] - '1';
				while ( particle._controlPoints.Count <= index ) particle._controlPoints.Add( "" );
				particle._controlPoints[index] = value;
			}
			else if ( string.Equals( key, "targetname", StringComparison.OrdinalIgnoreCase )
				|| string.Equals( key, "angles", StringComparison.OrdinalIgnoreCase )
				|| string.Equals( key, "parentname", StringComparison.OrdinalIgnoreCase )
				|| string.Equals( key, "spawnflags", StringComparison.OrdinalIgnoreCase )
				|| string.Equals( key, "classname", StringComparison.OrdinalIgnoreCase ) )
			{
				// Implicit / identity keys: already represented on the ref or the entity. Skip.
			}
			else particle._params[key] = value;
		}
		// Drop trailing empty control-point slots left by gaps (cpoint2 without cpoint1 is malformed input, not data).
		while ( particle._controlPoints.Count > 0 && string.IsNullOrEmpty( particle._controlPoints[particle._controlPoints.Count - 1] ) )
			particle._controlPoints.RemoveAt( particle._controlPoints.Count - 1 );
		return particle;
	}

	/// <summary>
	/// Direction the emitter points, from Source angles (pitch = x, yaw = y,
	/// degrees; pitch positive looks down, Z-up). (0,0,0) -&gt; +X.
	/// </summary>
	public static Vec3 DirectionFromAngles( Vec3 angles )
	{
		float pitch = angles.x * MathF.PI / 180f;
		float yaw = angles.y * MathF.PI / 180f;
		float cosP = MathF.Cos( pitch );
		return new Vec3( cosP * MathF.Cos( yaw ), cosP * MathF.Sin( yaw ), -MathF.Sin( pitch ) );
	}

	/// <summary>
	/// The thin adapter PreviewEffects' header asks for: map this reference
	/// onto its PreviewParticleSystem DTO. Emission parameters come from
	/// Params when present (keys "emissionrate", "lifetime", "speed",
	/// "spread", "startsize", "endsize", "gravity" as "x y z", invariant
	/// culture); unparsable or absent values fall back to the DTO
	/// defaults — except EmissionRate, which defaults to 0, so a system
	/// with no known emission parameters honestly evaluates to zero
	/// particles instead of inventing some. A system with StartActive
	/// false also maps to EmissionRate 0: it emits nothing until
	/// triggered, and the preview shows that.
	/// </summary>
	public PreviewEffects.PreviewParticleSystem ToPreviewParticleSystem()
	{
		var system = new PreviewEffects.PreviewParticleSystem
		{
			Name = EffectName,
			Origin = Origin,
			Direction = DirectionFromAngles( Angles ),
			EmissionRate = StartActive ? ParamFloat( "emissionrate", 0f ) : 0f,
			Lifetime = ParamFloat( "lifetime", 1f ),
			Speed = ParamFloat( "speed", 0f ),
			Spread = ParamFloat( "spread", 0f ),
			Gravity = ParamVec3( "gravity", Vec3.Zero ),
			StartSize = ParamFloat( "startsize", 1f ),
			EndSize = ParamFloat( "endsize", 1f ),
		};
		return system;
	}

	float ParamFloat( string key, float fallback )
	{
		if ( _params.TryGetValue( key, out string text )
			&& float.TryParse( text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value )
			&& float.IsFinite( value ) ) return value;
		return fallback;
	}

	Vec3 ParamVec3( string key, Vec3 fallback )
	{
		if ( !_params.TryGetValue( key, out string text ) || string.IsNullOrWhiteSpace( text ) ) return fallback;
		string[] parts = text.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != 3 ) return fallback;
		if ( !float.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x )
			|| !float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y )
			|| !float.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z ) ) return fallback;
		if ( !float.IsFinite( x ) || !float.IsFinite( y ) || !float.IsFinite( z ) ) return fallback;
		return new Vec3( x, y, z );
	}
}

/// <summary>Severity of one <see cref="SoundscapeIssue"/>.</summary>
public enum SoundscapeIssueSeverity
{
	Error,
	Warning,
}

/// <summary>
/// One validation finding: a missing soundscape / sound / particle, or
/// an out-of-range value. Same shape as MapEntityIssue (severity, stable
/// code, message, entity id, key). EntityId is the placed entity's
/// stable id, or 0 when the finding is not about one entity (e.g. a
/// soundscape definition's missing sound).
/// </summary>
public sealed class SoundscapeIssue
{
	public const string CodeMissingSoundscapeName = "MissingSoundscapeName";
	public const string CodeMissingSoundscape = "MissingSoundscape";
	public const string CodeMissingSound = "MissingSound";
	public const string CodeMissingSoundPath = "MissingSoundPath";
	public const string CodeMissingParticle = "MissingParticle";
	public const string CodeMissingParticleName = "MissingParticleName";
	public const string CodeInvalidRadius = "InvalidRadius";
	public const string CodeInvalidVolume = "InvalidVolume";
	public const string CodeInvalidPitch = "InvalidPitch";
	public const string CodeInvalidDelay = "InvalidDelay";
	public const string CodeInvalidBounds = "InvalidBounds";
	public const string CodeInvalidSoundscapeValue = "InvalidSoundscapeValue";

	public SoundscapeIssueSeverity Severity;
	public string Code = "";
	public string Message = "";
	public int EntityId;
	public string Key = "";
	public string ClassName = "";

	public SoundscapeIssue() { }
	public SoundscapeIssue( SoundscapeIssueSeverity severity, string code, string message, int entityId = 0, string key = "", string className = "" )
	{
		Severity = severity; Code = code; Message = message; EntityId = entityId; Key = key; ClassName = className;
	}

	public override string ToString() => string.IsNullOrEmpty( Key )
		? $"[{Severity} {Code}] {Message}"
		: $"[{Severity} {Code}] {Message} (entity #{EntityId}, key '{Key}')";
}

/// <summary>
/// Validation for soundscape zones, ambient_generic wiring and particle
/// references, resolved against the sound/particle registries through
/// the seam interfaces. A null resolver means "skip that existence
/// check" (range checks still run); it never means "pass". All findings
/// are returned, never thrown — except a null zone/ambient/particle
/// argument, which is a caller bug (ArgumentNullException).
/// </summary>
public static class SoundscapeValidator
{
	public static IReadOnlyList<SoundscapeIssue> ValidateZone( SoundscapeZone zone, ISoundscapeResolver soundscapes, ISoundscapeSoundResolver sounds, int entityId = 0 )
	{
		if ( zone is null ) throw new ArgumentNullException( nameof( zone ) );
		var issues = new List<SoundscapeIssue>();
		if ( zone.Radius < 0f )
			issues.Add( new SoundscapeIssue( SoundscapeIssueSeverity.Error, SoundscapeIssue.CodeInvalidRadius,
				$"Soundscape zone radius {zone.Radius} is negative; 0 means global.", entityId, "radius", SoundscapeZone.ClassName ) );
		if ( zone.HasBounds && ( zone.Min.x > zone.Max.x || zone.Min.y > zone.Max.y || zone.Min.z > zone.Max.z ) )
			issues.Add( new SoundscapeIssue( SoundscapeIssueSeverity.Error, SoundscapeIssue.CodeInvalidBounds,
				$"Soundscape zone bounds min {zone.Min} exceed max {zone.Max}.", entityId, "", SoundscapeZone.ClassName ) );
		if ( string.IsNullOrWhiteSpace( zone.SoundscapeName ) )
		{
			issues.Add( new SoundscapeIssue( SoundscapeIssueSeverity.Error, SoundscapeIssue.CodeMissingSoundscapeName,
				$"Soundscape zone{( entityId > 0 ? $" #{entityId}" : "" )} names no soundscape.", entityId, "soundscape", SoundscapeZone.ClassName ) );
			return issues;
		}
		if ( soundscapes is null ) return issues;
		var def = soundscapes.FindSoundscape( zone.SoundscapeName );
		if ( def is null )
		{
			issues.Add( new SoundscapeIssue( SoundscapeIssueSeverity.Error, SoundscapeIssue.CodeMissingSoundscape,
				$"Soundscape zone{( entityId > 0 ? $" #{entityId}" : "" )} names unknown soundscape '{zone.SoundscapeName}'.", entityId, "soundscape", SoundscapeZone.ClassName ) );
			return issues;
		}
		issues.AddRange( ValidateSoundscapeDef( def, sounds, entityId ) );
		return issues;
	}

	/// <summary>Validate one soundscape definition's sounds: range checks always run; existence needs a resolver (null = skip).</summary>
	public static IReadOnlyList<SoundscapeIssue> ValidateSoundscapeDef( SoundscapeDef def, ISoundscapeSoundResolver sounds, int entityId = 0 )
	{
		if ( def is null ) throw new ArgumentNullException( nameof( def ) );
		var issues = new List<SoundscapeIssue>();
		foreach ( var sound in def.Sounds )
		{
			if ( string.IsNullOrWhiteSpace( sound.Path ) )
			{
				issues.Add( new SoundscapeIssue( SoundscapeIssueSeverity.Error, SoundscapeIssue.CodeMissingSoundPath,
					$"Soundscape '{def.Name}' has a sound with no path.", entityId, "", "" ) );
				continue;
			}
			if ( sound.Volume < 0f || sound.Volume > 1f )
				issues.Add( new SoundscapeIssue( SoundscapeIssueSeverity.Error, SoundscapeIssue.CodeInvalidVolume,
					$"Soundscape '{def.Name}': sound '{sound.Path}' has volume {sound.Volume}, expected 0..1.", entityId, sound.Path ) );
			if ( sound.Pitch < 0f || sound.Pitch > 255f )
				issues.Add( new SoundscapeIssue( SoundscapeIssueSeverity.Error, SoundscapeIssue.CodeInvalidPitch,
					$"Soundscape '{def.Name}': sound '{sound.Path}' has pitch {sound.Pitch}, expected 0..255.", entityId, sound.Path ) );
			if ( sound.DelayMin < 0f || sound.DelayMax < sound.DelayMin )
				issues.Add( new SoundscapeIssue( SoundscapeIssueSeverity.Error, SoundscapeIssue.CodeInvalidDelay,
					$"Soundscape '{def.Name}': sound '{sound.Path}' has delay range {sound.DelayMin}..{sound.DelayMax}, expected 0 <= min <= max.", entityId, sound.Path ) );
			if ( sounds is not null && !sounds.SoundExists( sound.Path ) )
				issues.Add( new SoundscapeIssue( SoundscapeIssueSeverity.Error, SoundscapeIssue.CodeMissingSound,
					$"Soundscape '{def.Name}' references missing sound '{sound.Path}'.", entityId, sound.Path ) );
		}
		return issues;
	}

	public static IReadOnlyList<SoundscapeIssue> ValidateAmbient( AmbientGenericRef ambient, ISoundscapeSoundResolver sounds, int entityId = 0 )
	{
		if ( ambient is null ) throw new ArgumentNullException( nameof( ambient ) );
		var issues = new List<SoundscapeIssue>();
		if ( string.IsNullOrWhiteSpace( ambient.SoundPath ) )
		{
			issues.Add( new SoundscapeIssue( SoundscapeIssueSeverity.Error, SoundscapeIssue.CodeMissingSoundPath,
				$"ambient_generic{( entityId > 0 ? $" #{entityId}" : "" )} has no sound path ('message').", entityId, "message", AmbientGenericRef.ClassName ) );
			return issues;
		}
		if ( ambient.Volume < 0f || ambient.Volume > 1f )
			issues.Add( new SoundscapeIssue( SoundscapeIssueSeverity.Error, SoundscapeIssue.CodeInvalidVolume,
				$"ambient_generic '{ambient.SoundPath}' has volume {ambient.Volume}, expected 0..1.", entityId, "health", AmbientGenericRef.ClassName ) );
		if ( ambient.Pitch < 0f || ambient.Pitch > 255f )
			issues.Add( new SoundscapeIssue( SoundscapeIssueSeverity.Error, SoundscapeIssue.CodeInvalidPitch,
				$"ambient_generic '{ambient.SoundPath}' has pitch {ambient.Pitch}, expected 0..255.", entityId, "pitch", AmbientGenericRef.ClassName ) );
		if ( ambient.Radius < 0f )
			issues.Add( new SoundscapeIssue( SoundscapeIssueSeverity.Error, SoundscapeIssue.CodeInvalidRadius,
				$"ambient_generic '{ambient.SoundPath}' has radius {ambient.Radius}, expected >= 0.", entityId, "radius", AmbientGenericRef.ClassName ) );
		if ( sounds is not null && !sounds.SoundExists( ambient.SoundPath ) )
			issues.Add( new SoundscapeIssue( SoundscapeIssueSeverity.Error, SoundscapeIssue.CodeMissingSound,
				$"ambient_generic{( entityId > 0 ? $" #{entityId}" : "" )} references missing sound '{ambient.SoundPath}'.", entityId, "message", AmbientGenericRef.ClassName ) );
		return issues;
	}

	public static IReadOnlyList<SoundscapeIssue> ValidateParticle( ParticleSystemRef particle, ISoundscapeParticleResolver particles, int entityId = 0 )
	{
		if ( particle is null ) throw new ArgumentNullException( nameof( particle ) );
		var issues = new List<SoundscapeIssue>();
		if ( string.IsNullOrWhiteSpace( particle.EffectName ) )
		{
			issues.Add( new SoundscapeIssue( SoundscapeIssueSeverity.Error, SoundscapeIssue.CodeMissingParticleName,
				$"info_particle_system{( entityId > 0 ? $" #{entityId}" : "" )} names no particle system ('effect_name').", entityId, "effect_name", ParticleSystemRef.ClassName ) );
			return issues;
		}
		if ( particles is not null && !particles.ParticleExists( particle.EffectName ) )
			issues.Add( new SoundscapeIssue( SoundscapeIssueSeverity.Error, SoundscapeIssue.CodeMissingParticle,
				$"info_particle_system{( entityId > 0 ? $" #{entityId}" : "" )} references missing particle system '{particle.EffectName}'.", entityId, "effect_name", ParticleSystemRef.ClassName ) );
		return issues;
	}

	/// <summary>
	/// Validate every soundscape / ambient / particle entity in a placed
	/// collection, in placement order. Entities of other classes are
	/// ignored. A zone/ambient/particle whose keyvalues cannot be parsed
	/// (e.g. a non-numeric radius that slipped past the registry) is one
	/// InvalidSoundscapeValue error, not an exception. Target existence
	/// (cpoint targets, parentname) is NOT checked here — MapEntities.
	/// Validate owns it, and duplicating it would double-report.
	/// </summary>
	public static IReadOnlyList<SoundscapeIssue> ValidateMap( MapEntities entities, ISoundscapeResolver soundscapes, ISoundscapeSoundResolver sounds, ISoundscapeParticleResolver particles )
	{
		if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
		var issues = new List<SoundscapeIssue>();
		foreach ( var entity in entities.Entities )
		{
			bool isZone = string.Equals( entity.ClassName, SoundscapeZone.ClassName, StringComparison.OrdinalIgnoreCase )
				|| string.Equals( entity.ClassName, SoundscapeZone.TriggerClassName, StringComparison.OrdinalIgnoreCase );
			bool isAmbient = string.Equals( entity.ClassName, AmbientGenericRef.ClassName, StringComparison.OrdinalIgnoreCase );
			bool isParticle = string.Equals( entity.ClassName, ParticleSystemRef.ClassName, StringComparison.OrdinalIgnoreCase );
			if ( !isZone && !isAmbient && !isParticle ) continue;
			try
			{
				if ( isZone ) issues.AddRange( ValidateZone( SoundscapeZone.FromEntity( entity ), soundscapes, sounds, entity.Id ) );
				else if ( isAmbient ) issues.AddRange( ValidateAmbient( AmbientGenericRef.FromEntity( entity ), sounds, entity.Id ) );
				else issues.AddRange( ValidateParticle( ParticleSystemRef.FromEntity( entity ), particles, entity.Id ) );
			}
			catch ( SoundscapeException ex )
			{
				issues.Add( new SoundscapeIssue( SoundscapeIssueSeverity.Error, SoundscapeIssue.CodeInvalidSoundscapeValue,
					$"Entity #{entity.Id} ('{entity.ClassName}') has an unparsable soundscape value: {ex.Message}", entity.Id, "", entity.ClassName ) );
			}
		}
		return issues;
	}
}

/// <summary>Result of <see cref="SoundscapePreview.ResolveActive"/>: which soundscape is audible at a point, and how loud. Preview DATA only — no audio is played here (no UI, no audio device in the kernel).</summary>
public sealed class SoundscapePreviewResult
{
	/// <summary>True when a zone contains the point and names a registered soundscape.</summary>
	public bool Active;
	/// <summary>The containing zone, or null when no zone contains the point.</summary>
	public SoundscapeZone Zone;
	/// <summary>The resolved soundscape definition, or null when the zone names none / an unknown one.</summary>
	public SoundscapeDef Soundscape;
	/// <summary>The definition's sounds (same list as Soundscape.Sounds). Effective volume of each = Volume * Attenuation.</summary>
	public IReadOnlyList<SoundscapeSound> Sounds => Soundscape is null ? Array.Empty<SoundscapeSound>() : Soundscape.Sounds;
	/// <summary>Distance falloff 0..1: 1 inside a bounds zone or a global zone, 1 - dist/radius inside a radius zone.</summary>
	public float Attenuation = 1f;
	/// <summary>Distance from the point to the zone origin, inches.</summary>
	public float Distance;
	/// <summary>Why this result: "inside zone ...", "no zone contains ...", "zone names missing soundscape ...", ...</summary>
	public string Reason = "";

	public SoundscapePreviewResult() { }
}

/// <summary>
/// Soundscape preview data: which soundscape is active at a point.
/// Among containing zones the nearest (by distance to zone origin)
/// wins; ties go to the first zone in list order. A zone naming an
/// unknown soundscape is not active — the result says so instead of
/// guessing a fallback soundscape.
/// </summary>
public static class SoundscapePreview
{
	public static SoundscapePreviewResult ResolveActive( Vec3 point, IReadOnlyList<SoundscapeZone> zones, ISoundscapeResolver soundscapes )
	{
		if ( soundscapes is null ) throw new ArgumentNullException( nameof( soundscapes ) );
		var result = new SoundscapePreviewResult { Reason = $"No soundscape zone contains {point}." };
		if ( zones is null || zones.Count == 0 ) return result;
		SoundscapeZone best = null;
		float bestDistance = float.MaxValue;
		foreach ( var zone in zones )
		{
			if ( zone is null || !zone.Contains( point ) ) continue;
			float distance = zone.DistanceTo( point );
			if ( best is null || distance < bestDistance ) { best = zone; bestDistance = distance; }
		}
		if ( best is null ) return result;
		result.Zone = best;
		result.Distance = bestDistance;
		if ( string.IsNullOrWhiteSpace( best.SoundscapeName ) )
		{
			result.Reason = $"Zone '{best.TargetName}' contains {point} but names no soundscape.";
			return result;
		}
		var def = soundscapes.FindSoundscape( best.SoundscapeName );
		if ( def is null )
		{
			result.Reason = $"Zone '{best.TargetName}' names unknown soundscape '{best.SoundscapeName}'.";
			return result;
		}
		result.Soundscape = def;
		result.Active = true;
		if ( best.HasBounds || best.Radius <= 0f ) result.Attenuation = 1f;
		else result.Attenuation = 1f - bestDistance / best.Radius;
		if ( result.Attenuation < 0f ) result.Attenuation = 0f;
		result.Reason = $"Inside zone '{best.TargetName}' ({best.SoundscapeName}).";
		return result;
	}
}

/// <summary>Result of <see cref="ParticlePreview.Preview"/>: whether the particle definition exists, and what it evaluates to at time t (null when missing).</summary>
public sealed class ParticlePreviewResult
{
	public ParticleSystemRef Ref;
	/// <summary>True when the particle definition exists in the registry.</summary>
	public bool Found;
	/// <summary>PreviewEffects evaluation at the requested time, or null when the definition is missing.</summary>
	public PreviewEffects.ParticleEvaluation Evaluation;
	/// <summary>MissingParticle issue when not found, else null.</summary>
	public SoundscapeIssue Issue;

	public ParticlePreviewResult() { }
}

/// <summary>
/// Particle preview data: resolve a map particle reference against the
/// particle registry and evaluate it with PreviewEffects (the one
/// evaluator — this class adds resolution, not a second simulation).
/// A missing definition is a named MissingParticle issue in the result,
/// never an exception. A null resolver is a caller bug: existence is
/// the point of this call (pass SoundscapeResolvers.Empty to mean
/// "nothing exists").
/// </summary>
public static class ParticlePreview
{
	public static ParticlePreviewResult Preview( ParticleSystemRef particle, ISoundscapeParticleResolver particles, float time )
	{
		if ( particle is null ) throw new ArgumentNullException( nameof( particle ) );
		if ( particles is null ) throw new ArgumentNullException( nameof( particles ) );
		var result = new ParticlePreviewResult { Ref = particle };
		if ( string.IsNullOrWhiteSpace( particle.EffectName ) )
		{
			result.Issue = new SoundscapeIssue( SoundscapeIssueSeverity.Error, SoundscapeIssue.CodeMissingParticleName,
				"info_particle_system names no particle system ('effect_name').", 0, "effect_name", ParticleSystemRef.ClassName );
			return result;
		}
		if ( !particles.ParticleExists( particle.EffectName ) )
		{
			result.Issue = new SoundscapeIssue( SoundscapeIssueSeverity.Error, SoundscapeIssue.CodeMissingParticle,
				$"info_particle_system references missing particle system '{particle.EffectName}'.", 0, "effect_name", ParticleSystemRef.ClassName );
			return result;
		}
		result.Found = true;
		result.Evaluation = PreviewEffects.EvaluateParticles( particle.ToPreviewParticleSystem(), time );
		return result;
	}
}
