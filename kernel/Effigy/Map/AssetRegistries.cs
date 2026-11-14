using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Effigy;

namespace Effigy.Map;

/// <summary>
/// Hammer Desktop P2: model / particle / sound registries + lookup API
/// (hammer-model-particle-sound-registries). Backend only — no browser UI,
/// no Hammer / VBSP shell-out, engine-free kernel code.
///
/// Three registries, one file, one namespace (Effigy.Map):
///   <see cref="ModelAssetRegistry"/> — models (path, attachments, scale,
///     prop type static / dynamic / physics);
///   <see cref="ParticleAssetRegistry"/> — particle systems (system name,
///     params, optional definition file);
///   <see cref="SoundAssetRegistry"/> — sounds (path, volume).
/// <see cref="AssetRegistries"/> is the facade that owns one of each plus
/// the project root, and places registry entries through the entity API
/// (<see cref="MapEntities"/> + <see cref="EntityRegistry"/>).
///
/// Lookup / search: substring (case-insensitive) + wildcard (`*` any run,
/// `?` one character) over each entry's name / path / system name, via
/// <see cref="AssetSearch"/>. Used / Favourites / Recent are data on the
/// registries: per-entry <c>UsedCount</c> + <c>LastUsedAtUtc</c>, a
/// favourites set, and a most-recent-first list capped at
/// <see cref="RegistryUsage.MaxRecent"/> (20).
///
/// Naming — read before renaming anything:
///   * <c>ModelRegistry</c> already exists in this namespace
///     (BrushToModel.cs, interim registry keyed by BrushModelAsset for
///     brush-to-model conversions, documented there as "the canonical
///     registry belongs to hammer-model-particle-sound-registries ... until
///     the integrator re-points it"). Defining a second ModelRegistry here
///     would be a duplicate-type compile break, so the canonical model
///     registry is <see cref="ModelAssetRegistry"/>. The integrator
///     re-points BrushToModel at it (one file, BrushToModel.cs — NOT this
///     ticket's file to edit); the Register / TryGet / Contains / Remove /
///     Clear / Count shape here is deliberately compatible.
///   * No second <c>ParticleSystemDef</c>: that name is the Desktop
///     particle-authoring definition (ParticleAuthoring.cs). A registry
///     particle entry is <see cref="ParticleEntry"/> (system name +
///     params), never a definition.
///   * <see cref="AssetKind"/> (Model / Material / Sound / Particle) and
///     <see cref="AssetHotload.NormalizePath"/> are reused from
///     AssetHotload.cs, not redefined. The missing-asset issue codes
///     MISSING_MODEL / MISSING_SOUND / MISSING_PARTICLE are the same
///     strings hotload's RevalidateReferences emits, so the auto-fix
///     ticket can switch on one set.
///   * Placement validates the model / sound / particle file exists under
///     the project root first: a missing file is a named issue
///     (MISSING_MODEL / MISSING_SOUND / MISSING_PARTICLE) and NO entity
///     is placed. A prop_static has no scale keyvalue in
///     core-source1.json, so the entry's (possibly non-uniform) scale is
///     placement data carried on <see cref="AssetPlacementResult.Scale"/>,
///     not smuggled into a keyvalue.
///   * Particle / sound placement needs entity classes the core registry
///     does not ship yet (info_particle_system, ambient_generic — owned
///     by the P3 soundscapes ticket). Placement checks the supplied
///     registry and returns a named issue (UnknownEntityClass /
///     UnknownKeyValue) instead of inventing a class.
///
/// EXTENSION POINT — hammer-browsers-used-count-replace-texture (LANDED):
/// <see cref="AssetRegistries.UseCount(MapDocument, AssetKind, string)"/> counts
/// references to one asset in a map document (faces for materials, entities for
/// models / sounds / particles); each registry's <c>UsedOnly(document)</c>
/// lists the registered entries referenced at least once. Use-counts here are
/// DOCUMENT references — distinct from the registries' placement tally
/// (<c>UsedCount</c> / <c>NoteUsed</c>): a model placed ten times then deleted
/// from the map has UsedCount 10 and UseCount 0. ReplaceTexture /
/// ReplaceModel are undoable commands via <see cref="MapUndo"/> (see
/// <see cref="ReplaceTextureCommand"/> for the per-face mechanics and its
/// honest limits); per-texture alpha data lives on the facade
/// (<see cref="TextureAlphaInfo"/>) for the future browser. Keep the three
/// registries as separate public classes so that addition touches one
/// registry at a time, and keep entry identity (model = Path, particle =
/// SystemName, sound = Path) stable.
/// <para/>
/// Hotload seam: <see cref="AssetRegistries.WatchWith"/> is the one thin
/// adapter AssetHotload.cs anticipated (registry entry -> Watch call);
/// nothing in AssetHotload.cs is redefined or edited here.
/// </summary>
public enum PropType
{
	/// <summary>Placed as prop_static (baked, no physics, no animation).</summary>
	Static,
	/// <summary>Placed as prop_dynamic (animated, no physics).</summary>
	Dynamic,
	/// <summary>Placed as prop_physics (VPhysics simulated).</summary>
	Physics,
}

/// <summary>One registered model: path, attachments, scale, prop type.
/// Identity is <see cref="Path"/> (project-relative, normalized on
/// register); <see cref="Name"/> is the display / search name and must be
/// unique within the registry. <see cref="UsedCount"/> /
/// <see cref="LastUsedAtUtc"/> are owned by the registry (see
/// <see cref="ModelAssetRegistry.NoteUsed"/>); do not bump them by hand.
/// </summary>
public sealed class ModelEntry
{
	public string Name = "";
	/// <summary>Project-relative path, forward slashes, normalized on
	/// register. Must end in a known model extension (.mdl / .vmdl / .glb
	/// / .obj / .fbx / .effigy) — the same rule the entity API enforces
	/// on the prop's `model` keyvalue — and contain no whitespace.</summary>
	public string Path = "";
	/// <summary>Attachment point names on the model (e.g. "muzzle",
	/// "handle"). Empty when the model exposes none.</summary>
	public List<string> Attachments = new();
	/// <summary>Placement scale; non-uniform allowed. Must be finite
	/// with every component &gt; 0. A prop entity has no scale keyvalue
	/// in the core registry, so this is placement data carried on the
	/// placement result, not a keyvalue.</summary>
	public Vec3 Scale = Vec3.One;
	public PropType PropType = PropType.Static;
	public int UsedCount;
	public DateTime LastUsedAtUtc;

	public override string ToString() =>
		$"{PropType} '{Name}' {Path} scale {Scale}";
}

/// <summary>One registered particle system: system name + params.
/// Identity is <see cref="SystemName"/>. <see cref="DefinitionPath"/> is
/// the project-relative definition file (.json / .pcf) when the system
/// comes from a file, "" for a built-in system with no file. Params are
/// flat string pairs (e.g. "count" = "64"); they are search text, not a
/// second definition — the authoring definition stays
/// <c>ParticleSystemDef</c> and is deliberately NOT redefined here.
/// </summary>
public sealed class ParticleEntry
{
	public string SystemName = "";
	public string DefinitionPath = "";
	public Dictionary<string, string> Params = new( StringComparer.OrdinalIgnoreCase );
	public int UsedCount;
	public DateTime LastUsedAtUtc;

	public override string ToString() =>
		string.IsNullOrEmpty( DefinitionPath )
			? $"particle '{SystemName}' (built-in)"
			: $"particle '{SystemName}' {DefinitionPath}";
}

/// <summary>One registered sound: path + volume. Identity is
/// <see cref="Path"/> (project-relative, normalized on register);
/// <see cref="Name"/> is the display / search name and must be unique
/// within the registry. <see cref="Volume"/> is 0..1 inclusive.
/// </summary>
public sealed class SoundEntry
{
	public string Name = "";
	/// <summary>Project-relative path, forward slashes, normalized on
	/// register. Must end in a known sound extension (.wav / .mp3 / .ogg
	/// / .flac) — the same rule the entity API enforces on a `sound`
	/// keyvalue — and contain no whitespace.</summary>
	public string Path = "";
	public float Volume = 1f;
	public int UsedCount;
	public DateTime LastUsedAtUtc;

	public override string ToString() =>
		$"sound '{Name}' {Path} vol {Volume.ToString( CultureInfo.InvariantCulture )}";
}

/// <summary>One named registry problem. Codes are stable machine strings:
/// MISSING_MODEL / MISSING_SOUND / MISSING_PARTICLE match the codes
/// AssetHotload's RevalidateReferences emits; UnknownModel /
/// UnknownParticle / UnknownSound mean the entry is not in the registry;
/// UnknownEntityClass / UnknownKeyValue reuse
/// <see cref="EntityRegistry.CodeUnknownEntityClass"/> /
/// <see cref="EntityRegistry.CodeUnknownKeyValue"/> verbatim.</summary>
public sealed class AssetRegistryIssue
{
	public const string CodeMissingModel = "MISSING_MODEL";
	public const string CodeMissingSound = "MISSING_SOUND";
	public const string CodeMissingParticle = "MISSING_PARTICLE";
	public const string CodeUnknownModel = "UnknownModel";
	public const string CodeUnknownParticle = "UnknownParticle";
	public const string CodeUnknownSound = "UnknownSound";

	public string Code = "";
	public IssueSeverity Severity = IssueSeverity.Error;
	public string Message = "";
	public string AssetPath = "";
	public AssetKind Kind;
	/// <summary>Referencing entity, or -1 (registry validation has no entity yet).</summary>
	public int EntityId = -1;

	public AssetRegistryIssue() { }

	public AssetRegistryIssue( string code, string message, AssetKind kind, string assetPath = "" )
	{
		Code = code;
		Message = message;
		Kind = kind;
		AssetPath = assetPath;
	}

	public override string ToString() =>
		$"[{Severity} {Code}] {Message}";
}

/// <summary>Outcome of one registry placement through the entity API.
/// Ok false always carries a machine-readable <see cref="Code"/> and a
/// human <see cref="Message"/>, and <see cref="Entity"/> null — a
/// refusal is never a half-placed entity. On success <see cref="Scale"/>
/// carries the entry's placement scale (see the file header: props have
/// no scale keyvalue).</summary>
public sealed class AssetPlacementResult
{
	public bool Ok;
	public string Code = "";
	public string Message = "";
	public MapEntity? Entity;
	public string ClassName = "";
	public Vec3 Scale = Vec3.One;

	public static AssetPlacementResult Succeeded( MapEntity entity, string className, Vec3 scale ) =>
		new() { Ok = true, Entity = entity, ClassName = className, Scale = scale };

	public static AssetPlacementResult Failed( string code, string message ) =>
		new() { Ok = false, Code = code, Message = message };

	public override string ToString() =>
		Ok ? $"placed {ClassName} #{Entity?.Id} scale {Scale}" : $"[{Code}] {Message}";
}

/// <summary>Substring + wildcard matching shared by the three registries.
/// Blank query matches everything. Otherwise the query is a
/// case-insensitive substring, unless it contains `*` (any run, including
/// empty) or `?` (exactly one character), in which case the glob must
/// match a SUBSTRING of the text — wildcards extend substring search,
/// they do not replace it with a full-path glob, so `props/crate*`
/// finds `models/props/crate.mdl` exactly as the substring `props`
/// does.</summary>
public static class AssetSearch
{
	public static bool Matches( string? text, string? query )
	{
		if ( string.IsNullOrWhiteSpace( query ) ) return true;
		text ??= "";
		query = query.Trim();
		if ( query.IndexOf( '*' ) >= 0 || query.IndexOf( '?' ) >= 0 )
		{
			var pattern = Regex.Escape( query ).Replace( "\\*", ".*" ).Replace( "\\?", "." );
			return Regex.IsMatch( text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );
		}
		return text.IndexOf( query, StringComparison.OrdinalIgnoreCase ) >= 0;
	}

	public static bool MatchesAny( IEnumerable<string?> texts, string? query )
	{
		foreach ( var text in texts )
			if ( Matches( text, query ) ) return true;
		return false;
	}
}

/// <summary>Per-registry Used / Favourites / Recent bookkeeping, shared
/// so the three registries behave identically. Keys are the registry's
/// entry identity (model / sound = normalized path, particle = system
/// name). Recent is most-recent-first, capped at <see cref="MaxRecent"/>.
/// </summary>
internal sealed class RegistryUsage
{
	public const int MaxRecent = 20;

	readonly List<string> _recent = new();
	readonly HashSet<string> _favourites = new( StringComparer.OrdinalIgnoreCase );

	public void NoteUsed( string key )
	{
		if ( string.IsNullOrWhiteSpace( key ) ) return;
		_recent.RemoveAll( k => string.Equals( k, key, StringComparison.OrdinalIgnoreCase ) );
		_recent.Insert( 0, key );
		if ( _recent.Count > MaxRecent ) _recent.RemoveRange( MaxRecent, _recent.Count - MaxRecent );
	}

	public IReadOnlyList<string> Recent => _recent.ToArray();

	public bool MarkFavourite( string key )
	{
		if ( string.IsNullOrWhiteSpace( key ) ) return false;
		return _favourites.Add( key );
	}

	public bool UnmarkFavourite( string key ) =>
		!string.IsNullOrWhiteSpace( key ) && _favourites.Remove( key );

	public bool IsFavourite( string key ) =>
		!string.IsNullOrWhiteSpace( key ) && _favourites.Contains( key );

	public IReadOnlyList<string> Favourites =>
		_favourites.OrderBy( k => k, StringComparer.OrdinalIgnoreCase ).ToArray();

	public void Forget( string key )
	{
		if ( string.IsNullOrWhiteSpace( key ) ) return;
		_recent.RemoveAll( k => string.Equals( k, key, StringComparison.OrdinalIgnoreCase ) );
		_favourites.Remove( key );
	}

	public void Clear()
	{
		_recent.Clear();
		_favourites.Clear();
	}
}

/// <summary>Canonical model registry (see the file header for why this is
/// <c>ModelAssetRegistry</c> and not <c>ModelRegistry</c>). Models by
/// path + display name, with substring / wildcard search, prop-type
/// filter, favourites / recent / used tracking, on-disk validation, and
/// placement through the entity API (via <see cref="AssetRegistries"/>).
/// Registration is refused — never a silent overwrite — for a null /
/// blank entry, a blank name, an un-normalizable or extension-invalid
/// path, a non-finite or non-positive scale component, or a duplicate
/// name or path (the existing entry is kept).</summary>
public sealed class ModelAssetRegistry
{
	readonly List<ModelEntry> _order = new();
	readonly Dictionary<string, ModelEntry> _byPath = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, ModelEntry> _byName = new( StringComparer.OrdinalIgnoreCase );
	readonly RegistryUsage _usage = new();

	public int Count => _order.Count;
	/// <summary>Entries in registration order.</summary>
	public IReadOnlyList<ModelEntry> All => _order.ToArray();

	public bool Register( ModelEntry entry )
	{
		if ( entry is null || string.IsNullOrWhiteSpace( entry.Name ) ) return false;
		string path;
		try { path = AssetHotload.NormalizePath( entry.Path ); }
		catch ( ArgumentException ) { return false; }
		if ( !EntityRegistry.TryCheckValue( KeyValueType.Model, path, out _ ) ) return false;
		if ( !ValidScale( entry.Scale ) ) return false;
		if ( _byPath.ContainsKey( path ) || _byName.ContainsKey( entry.Name.Trim() ) ) return false;
		entry.Path = path;
		entry.Name = entry.Name.Trim();
		_order.Add( entry );
		_byPath[path] = entry;
		_byName[entry.Name] = entry;
		return true;
	}

	public static bool ValidScale( Vec3 scale ) =>
		float.IsFinite( scale.x ) && float.IsFinite( scale.y ) && float.IsFinite( scale.z )
		&& scale.x > 0f && scale.y > 0f && scale.z > 0f;

	public bool TryGetByName( string name, out ModelEntry entry )
	{
		entry = null!;
		if ( string.IsNullOrWhiteSpace( name ) ) return false;
		return _byName.TryGetValue( name.Trim(), out entry );
	}

	public bool TryGetByPath( string path, out ModelEntry entry )
	{
		entry = null!;
		if ( string.IsNullOrWhiteSpace( path ) ) return false;
		string normalized;
		try { normalized = AssetHotload.NormalizePath( path ); }
		catch ( ArgumentException ) { return false; }
		return _byPath.TryGetValue( normalized, out entry );
	}

	public bool TryGet( string nameOrPath, out ModelEntry entry ) =>
		TryGetByName( nameOrPath, out entry ) || TryGetByPath( nameOrPath, out entry );

	public bool Contains( string nameOrPath ) => TryGet( nameOrPath, out _ );

	public bool Remove( string nameOrPath )
	{
		if ( !TryGet( nameOrPath, out var entry ) ) return false;
		_order.Remove( entry );
		_byPath.Remove( entry.Path );
		_byName.Remove( entry.Name );
		_usage.Forget( entry.Path );
		return true;
	}

	public void Clear()
	{
		_order.Clear();
		_byPath.Clear();
		_byName.Clear();
		_usage.Clear();
	}

	/// <summary>Substring / wildcard search over Name and Path.</summary>
	public IReadOnlyList<ModelEntry> Search( string query ) =>
		_order.Where( e => AssetSearch.MatchesAny( new[] { e.Name, e.Path }, query ) ).ToArray();

	/// <summary>All models of one prop type, in registration order.</summary>
	public IReadOnlyList<ModelEntry> ByPropType( PropType type ) =>
		_order.Where( e => e.PropType == type ).ToArray();

	/// <summary>Record one use: bumps UsedCount + LastUsedAtUtc and
	/// moves the entry to the front of Recent. No-op for a null entry
	/// or one that is not registered. This is the usage seam for the
	/// sequenced hammer-browsers-used-count-replace-texture ticket.</summary>
	public void NoteUsed( ModelEntry entry )
	{
		if ( entry is null ) return;
		if ( !_byPath.TryGetValue( entry.Path, out var owned ) || !ReferenceEquals( owned, entry ) ) return;
		entry.UsedCount++;
		entry.LastUsedAtUtc = DateTime.UtcNow;
		_usage.NoteUsed( entry.Path );
	}

	public bool MarkFavourite( string nameOrPath )
	{
		if ( !TryGet( nameOrPath, out var entry ) ) return false;
		return _usage.MarkFavourite( entry.Path );
	}

	public bool UnmarkFavourite( string nameOrPath )
	{
		if ( !TryGet( nameOrPath, out var entry ) ) return false;
		return _usage.UnmarkFavourite( entry.Path );
	}

	public bool IsFavourite( string nameOrPath ) =>
		TryGet( nameOrPath, out var entry ) && _usage.IsFavourite( entry.Path );

	/// <summary>Favourited entries, in registration order.</summary>
	public IReadOnlyList<ModelEntry> Favourites =>
		_order.Where( e => _usage.IsFavourite( e.Path ) ).ToArray();

	/// <summary>Most-recently-used entries, most recent first.</summary>
	public IReadOnlyList<ModelEntry> Recent =>
		_usage.Recent.Select( k => _byPath.TryGetValue( k, out var e ) ? e : null )
			.Where( e => e is not null ).Select( e => e! ).ToArray();

	/// <summary>Used entries, most-used first (ties: most recently used).</summary>
	public IReadOnlyList<ModelEntry> Used =>
		_order.Where( e => e.UsedCount > 0 )
			.OrderByDescending( e => e.UsedCount )
			.ThenByDescending( e => e.LastUsedAtUtc )
			.ThenBy( e => e.Name, StringComparer.OrdinalIgnoreCase )
			.ToArray();

	/// <summary>Registered models referenced at least once in
	/// <paramref name="document"/> (an entity's `model` keyvalue), in
	/// registration order. Unreferenced models are excluded — this is the
	/// "show used only" list. Counts document references, not the
	/// placement tally (<see cref="ModelEntry.UsedCount"/>).</summary>
	public IReadOnlyList<ModelEntry> UsedOnly( MapDocument document )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		var counts = MapAssetUsage.CountAllUses( document, AssetKind.Model );
		return _order.Where( e => counts.TryGetValue( e.Path, out var c ) && c > 0 ).ToArray();
	}

	/// <summary>One MISSING_MODEL issue per registered model whose file
	/// does not exist under <paramref name="projectRoot"/>.</summary>
	public IReadOnlyList<AssetRegistryIssue> ValidateFiles( string projectRoot )
	{
		if ( string.IsNullOrWhiteSpace( projectRoot ) )
			throw new ArgumentException( "A project root is required.", nameof( projectRoot ) );
		var issues = new List<AssetRegistryIssue>();
		foreach ( var entry in _order )
		{
			if ( !File.Exists( AssetRegistries.ResolvePath( projectRoot, entry.Path ) ) )
				issues.Add( new AssetRegistryIssue( AssetRegistryIssue.CodeMissingModel,
					$"Registered model '{entry.Name}' is missing on disk: '{entry.Path}'.",
					AssetKind.Model, entry.Path ) );
		}
		return issues;
	}
}

/// <summary>Particle-system registry. Systems by system name, with
/// substring / wildcard search (over system name, definition path and
/// params), favourites / recent / used tracking, and on-disk validation
/// of definition files. Registration is refused for a null entry, a
/// blank system name, or a duplicate system name.</summary>
public sealed class ParticleAssetRegistry
{
	readonly List<ParticleEntry> _order = new();
	readonly Dictionary<string, ParticleEntry> _byName = new( StringComparer.OrdinalIgnoreCase );
	readonly RegistryUsage _usage = new();

	public int Count => _order.Count;
	/// <summary>Entries in registration order.</summary>
	public IReadOnlyList<ParticleEntry> All => _order.ToArray();

	public bool Register( ParticleEntry entry )
	{
		if ( entry is null || string.IsNullOrWhiteSpace( entry.SystemName ) ) return false;
		if ( _byName.ContainsKey( entry.SystemName.Trim() ) ) return false;
		if ( !string.IsNullOrWhiteSpace( entry.DefinitionPath ) )
		{
			try { entry.DefinitionPath = AssetHotload.NormalizePath( entry.DefinitionPath ); }
			catch ( ArgumentException ) { return false; }
		}
		entry.SystemName = entry.SystemName.Trim();
		_order.Add( entry );
		_byName[entry.SystemName] = entry;
		return true;
	}

	public bool TryGet( string systemName, out ParticleEntry entry )
	{
		entry = null!;
		if ( string.IsNullOrWhiteSpace( systemName ) ) return false;
		return _byName.TryGetValue( systemName.Trim(), out entry );
	}

	public bool Contains( string systemName ) => TryGet( systemName, out _ );

	public bool Remove( string systemName )
	{
		if ( !TryGet( systemName, out var entry ) ) return false;
		_order.Remove( entry );
		_byName.Remove( entry.SystemName );
		_usage.Forget( entry.SystemName );
		return true;
	}

	public void Clear()
	{
		_order.Clear();
		_byName.Clear();
		_usage.Clear();
	}

	/// <summary>Substring / wildcard search over SystemName,
	/// DefinitionPath and each "key=value" param.</summary>
	public IReadOnlyList<ParticleEntry> Search( string query )
	{
		return _order.Where( e =>
		{
			var fields = new List<string?> { e.SystemName, e.DefinitionPath };
			foreach ( var pair in e.Params ) fields.Add( $"{pair.Key}={pair.Value}" );
			return AssetSearch.MatchesAny( fields, query );
		} ).ToArray();
	}

	/// <summary>Record one use: bumps UsedCount + LastUsedAtUtc and
	/// moves the entry to the front of Recent. No-op for a null entry
	/// or one that is not registered.</summary>
	public void NoteUsed( ParticleEntry entry )
	{
		if ( entry is null ) return;
		if ( !_byName.TryGetValue( entry.SystemName, out var owned ) || !ReferenceEquals( owned, entry ) ) return;
		entry.UsedCount++;
		entry.LastUsedAtUtc = DateTime.UtcNow;
		_usage.NoteUsed( entry.SystemName );
	}

	public bool MarkFavourite( string systemName )
	{
		if ( !TryGet( systemName, out var entry ) ) return false;
		return _usage.MarkFavourite( entry.SystemName );
	}

	public bool UnmarkFavourite( string systemName )
	{
		if ( !TryGet( systemName, out var entry ) ) return false;
		return _usage.UnmarkFavourite( entry.SystemName );
	}

	public bool IsFavourite( string systemName ) =>
		TryGet( systemName, out var entry ) && _usage.IsFavourite( entry.SystemName );

	/// <summary>Favourited entries, in registration order.</summary>
	public IReadOnlyList<ParticleEntry> Favourites =>
		_order.Where( e => _usage.IsFavourite( e.SystemName ) ).ToArray();

	/// <summary>Most-recently-used entries, most recent first.</summary>
	public IReadOnlyList<ParticleEntry> Recent =>
		_usage.Recent.Select( k => _byName.TryGetValue( k, out var e ) ? e : null )
			.Where( e => e is not null ).Select( e => e! ).ToArray();

	/// <summary>Used entries, most-used first (ties: most recently used).</summary>
	public IReadOnlyList<ParticleEntry> Used =>
		_order.Where( e => e.UsedCount > 0 )
			.OrderByDescending( e => e.UsedCount )
			.ThenByDescending( e => e.LastUsedAtUtc )
			.ThenBy( e => e.SystemName, StringComparer.OrdinalIgnoreCase )
			.ToArray();

	/// <summary>Registered particle systems referenced at least once in
	/// <paramref name="document"/> (an entity's `effect_name` keyvalue),
	/// in registration order. Unreferenced systems are excluded — this is
	/// the "show used only" list.</summary>
	public IReadOnlyList<ParticleEntry> UsedOnly( MapDocument document )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		var counts = MapAssetUsage.CountAllUses( document, AssetKind.Particle );
		return _order.Where( e => counts.TryGetValue( e.SystemName, out var c ) && c > 0 ).ToArray();
	}

	/// <summary>One MISSING_PARTICLE issue per registered system whose
	/// definition file does not exist under
	/// <paramref name="projectRoot"/>. Built-in systems (empty
	/// DefinitionPath) are never missing.</summary>
	public IReadOnlyList<AssetRegistryIssue> ValidateFiles( string projectRoot )
	{
		if ( string.IsNullOrWhiteSpace( projectRoot ) )
			throw new ArgumentException( "A project root is required.", nameof( projectRoot ) );
		var issues = new List<AssetRegistryIssue>();
		foreach ( var entry in _order )
		{
			if ( string.IsNullOrWhiteSpace( entry.DefinitionPath ) ) continue;
			if ( !File.Exists( AssetRegistries.ResolvePath( projectRoot, entry.DefinitionPath ) ) )
				issues.Add( new AssetRegistryIssue( AssetRegistryIssue.CodeMissingParticle,
					$"Registered particle system '{entry.SystemName}' is missing on disk: '{entry.DefinitionPath}'.",
					AssetKind.Particle, entry.DefinitionPath ) );
		}
		return issues;
	}
}

/// <summary>Sound registry. Sounds by path + display name, with
/// substring / wildcard search, favourites / recent / used tracking,
/// and on-disk validation. Registration is refused for a null entry,
/// a blank name, an un-normalizable or extension-invalid path, a
/// volume outside 0..1 (or NaN), or a duplicate name or path.</summary>
public sealed class SoundAssetRegistry
{
	readonly List<SoundEntry> _order = new();
	readonly Dictionary<string, SoundEntry> _byPath = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, SoundEntry> _byName = new( StringComparer.OrdinalIgnoreCase );
	readonly RegistryUsage _usage = new();

	public int Count => _order.Count;
	/// <summary>Entries in registration order.</summary>
	public IReadOnlyList<SoundEntry> All => _order.ToArray();

	public bool Register( SoundEntry entry )
	{
		if ( entry is null || string.IsNullOrWhiteSpace( entry.Name ) ) return false;
		string path;
		try { path = AssetHotload.NormalizePath( entry.Path ); }
		catch ( ArgumentException ) { return false; }
		if ( !EntityRegistry.TryCheckValue( KeyValueType.Sound, path, out _ ) ) return false;
		if ( !float.IsFinite( entry.Volume ) || entry.Volume < 0f || entry.Volume > 1f ) return false;
		if ( _byPath.ContainsKey( path ) || _byName.ContainsKey( entry.Name.Trim() ) ) return false;
		entry.Path = path;
		entry.Name = entry.Name.Trim();
		_order.Add( entry );
		_byPath[path] = entry;
		_byName[entry.Name] = entry;
		return true;
	}

	public bool TryGetByName( string name, out SoundEntry entry )
	{
		entry = null!;
		if ( string.IsNullOrWhiteSpace( name ) ) return false;
		return _byName.TryGetValue( name.Trim(), out entry );
	}

	public bool TryGetByPath( string path, out SoundEntry entry )
	{
		entry = null!;
		if ( string.IsNullOrWhiteSpace( path ) ) return false;
		string normalized;
		try { normalized = AssetHotload.NormalizePath( path ); }
		catch ( ArgumentException ) { return false; }
		return _byPath.TryGetValue( normalized, out entry );
	}

	public bool TryGet( string nameOrPath, out SoundEntry entry ) =>
		TryGetByName( nameOrPath, out entry ) || TryGetByPath( nameOrPath, out entry );

	public bool Contains( string nameOrPath ) => TryGet( nameOrPath, out _ );

	public bool Remove( string nameOrPath )
	{
		if ( !TryGet( nameOrPath, out var entry ) ) return false;
		_order.Remove( entry );
		_byPath.Remove( entry.Path );
		_byName.Remove( entry.Name );
		_usage.Forget( entry.Path );
		return true;
	}

	public void Clear()
	{
		_order.Clear();
		_byPath.Clear();
		_byName.Clear();
		_usage.Clear();
	}

	/// <summary>Substring / wildcard search over Name and Path.</summary>
	public IReadOnlyList<SoundEntry> Search( string query ) =>
		_order.Where( e => AssetSearch.MatchesAny( new[] { e.Name, e.Path }, query ) ).ToArray();

	/// <summary>Record one use: bumps UsedCount + LastUsedAtUtc and
	/// moves the entry to the front of Recent. No-op for a null entry
	/// or one that is not registered.</summary>
	public void NoteUsed( SoundEntry entry )
	{
		if ( entry is null ) return;
		if ( !_byPath.TryGetValue( entry.Path, out var owned ) || !ReferenceEquals( owned, entry ) ) return;
		entry.UsedCount++;
		entry.LastUsedAtUtc = DateTime.UtcNow;
		_usage.NoteUsed( entry.Path );
	}

	public bool MarkFavourite( string nameOrPath )
	{
		if ( !TryGet( nameOrPath, out var entry ) ) return false;
		return _usage.MarkFavourite( entry.Path );
	}

	public bool UnmarkFavourite( string nameOrPath )
	{
		if ( !TryGet( nameOrPath, out var entry ) ) return false;
		return _usage.UnmarkFavourite( entry.Path );
	}

	public bool IsFavourite( string nameOrPath ) =>
		TryGet( nameOrPath, out var entry ) && _usage.IsFavourite( entry.Path );

	/// <summary>Favourited entries, in registration order.</summary>
	public IReadOnlyList<SoundEntry> Favourites =>
		_order.Where( e => _usage.IsFavourite( e.Path ) ).ToArray();

	/// <summary>Most-recently-used entries, most recent first.</summary>
	public IReadOnlyList<SoundEntry> Recent =>
		_usage.Recent.Select( k => _byPath.TryGetValue( k, out var e ) ? e : null )
			.Where( e => e is not null ).Select( e => e! ).ToArray();

	/// <summary>Used entries, most-used first (ties: most recently used).</summary>
	public IReadOnlyList<SoundEntry> Used =>
		_order.Where( e => e.UsedCount > 0 )
			.OrderByDescending( e => e.UsedCount )
			.ThenByDescending( e => e.LastUsedAtUtc )
			.ThenBy( e => e.Name, StringComparer.OrdinalIgnoreCase )
			.ToArray();

	/// <summary>Registered sounds referenced at least once in
	/// <paramref name="document"/> (an entity's `message` / `sound`
	/// keyvalue), in registration order. Unreferenced sounds are
	/// excluded — this is the "show used only" list.</summary>
	public IReadOnlyList<SoundEntry> UsedOnly( MapDocument document )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		var counts = MapAssetUsage.CountAllUses( document, AssetKind.Sound );
		return _order.Where( e => counts.TryGetValue( e.Path, out var c ) && c > 0 ).ToArray();
	}

	/// <summary>One MISSING_SOUND issue per registered sound whose file
	/// does not exist under <paramref name="projectRoot"/>.</summary>
	public IReadOnlyList<AssetRegistryIssue> ValidateFiles( string projectRoot )
	{
		if ( string.IsNullOrWhiteSpace( projectRoot ) )
			throw new ArgumentException( "A project root is required.", nameof( projectRoot ) );
		var issues = new List<AssetRegistryIssue>();
		foreach ( var entry in _order )
		{
			if ( !File.Exists( AssetRegistries.ResolvePath( projectRoot, entry.Path ) ) )
				issues.Add( new AssetRegistryIssue( AssetRegistryIssue.CodeMissingSound,
					$"Registered sound '{entry.Name}' is missing on disk: '{entry.Path}'.",
					AssetKind.Sound, entry.Path ) );
		}
		return issues;
	}
}

/// <summary>Facade: one model + one particle + one sound registry bound
/// to a project root, with cross-registry validation, the hotload watch
/// adapter, and placement through the entity API. No browser UI: the
/// browser (deferred) reads these registries; it does not live here.
/// </summary>
public sealed class AssetRegistries
{
	public AssetRegistries( string projectRoot )
	{
		if ( string.IsNullOrWhiteSpace( projectRoot ) )
			throw new ArgumentException( "A project root is required.", nameof( projectRoot ) );
		ProjectRoot = projectRoot;
	}

	/// <summary>Directory asset paths resolve under.</summary>
	public string ProjectRoot { get; }
	public ModelAssetRegistry Models { get; } = new();
	public ParticleAssetRegistry Particles { get; } = new();
	public SoundAssetRegistry Sounds { get; } = new();

	/// <summary>Entity class a prop type places as.</summary>
	public static string ClassNameFor( PropType type ) => type switch
	{
		PropType.Static => "prop_static",
		PropType.Dynamic => "prop_dynamic",
		PropType.Physics => "prop_physics",
		_ => throw new ArgumentOutOfRangeException( nameof( type ), type, "Unknown prop type." ),
	};

	internal static string ResolvePath( string projectRoot, string normalizedPath ) =>
		Path.GetFullPath( Path.Combine( projectRoot, normalizedPath.Replace( '/', Path.DirectorySeparatorChar ) ) );

	/// <summary>Missing-file issues across all three registries.</summary>
	public IReadOnlyList<AssetRegistryIssue> ValidateAll()
	{
		var issues = new List<AssetRegistryIssue>();
		issues.AddRange( Models.ValidateFiles( ProjectRoot ) );
		issues.AddRange( Particles.ValidateFiles( ProjectRoot ) );
		issues.AddRange( Sounds.ValidateFiles( ProjectRoot ) );
		return issues;
	}

	/// <summary>The one thin hotload adapter AssetHotload.cs anticipated:
	/// watch every registered asset file (models + sounds + particle
	/// definition files; built-in particle systems have no file).
	/// Returns how many files were watched. The hotload service then
	/// owns change detection + reload; the registries refresh entries
	/// from reload results, they do not re-implement polling here.</summary>
	public int WatchWith( AssetHotload hotload )
	{
		if ( hotload is null ) throw new ArgumentNullException( nameof( hotload ) );
		int watched = 0;
		foreach ( var model in Models.All ) { hotload.Watch( AssetKind.Model, model.Path ); watched++; }
		foreach ( var sound in Sounds.All ) { hotload.Watch( AssetKind.Sound, sound.Path ); watched++; }
		foreach ( var particle in Particles.All )
		{
			if ( string.IsNullOrWhiteSpace( particle.DefinitionPath ) ) continue;
			hotload.Watch( AssetKind.Particle, particle.DefinitionPath );
			watched++;
		}
		return watched;
	}

	/// <summary>Place a registered model as a prop entity
	/// (prop_static / prop_dynamic / prop_physics by
	/// <see cref="ModelEntry.PropType"/>) at <paramref name="position"/>
	/// with the model's path as the `model` keyvalue. The file must
	/// exist under the project root and the entry must be registered;
	/// either failure is a named issue and NO entity is placed. A
	/// successful placement records one use and returns the entity plus
	/// the entry's scale on the result.</summary>
	public AssetPlacementResult PlaceModel( ModelEntry entry, MapEntities entities, Vec3 position, Vec3? angles = null )
	{
		if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
		if ( entry is null || !Models.TryGetByPath( entry.Path, out var owned ) || !ReferenceEquals( owned, entry ) )
			return AssetPlacementResult.Failed( AssetRegistryIssue.CodeUnknownModel,
				$"Model '{entry?.Path}' is not in the model registry; register it before placing." );
		if ( !File.Exists( ResolvePath( ProjectRoot, entry.Path ) ) )
			return AssetPlacementResult.Failed( AssetRegistryIssue.CodeMissingModel,
				$"Cannot place model '{entry.Name}': file is missing on disk: '{entry.Path}'." );
		var className = ClassNameFor( entry.PropType );
		if ( entities.Registry.Find( className ) is null )
			return AssetPlacementResult.Failed( EntityRegistry.CodeUnknownEntityClass,
				$"Cannot place model '{entry.Name}': entity class '{className}' is not in the entity registry." );
		var keyvalues = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
		{
			{ "model", entry.Path },
		};
		var placed = entities.PlacePointEntity( className, position, keyvalues, angles );
		Models.NoteUsed( entry );
		return AssetPlacementResult.Succeeded( placed, className, entry.Scale );
	}

	/// <summary>Place a registered particle system as an
	/// info_particle_system entity at <paramref name="position"/> with
	/// `effect_name` = the system name. The entry must be registered
	/// and its definition file (when it has one) must exist. The core
	/// entity registry does not ship info_particle_system yet (owned by
	/// the P3 soundscapes ticket): when the supplied registry lacks the
	/// class — or the class lacks the `effect_name` keyvalue — this
	/// returns that named issue and places nothing, instead of
	/// inventing a class.</summary>
	public AssetPlacementResult PlaceParticle( ParticleEntry entry, MapEntities entities, Vec3 position, Vec3? angles = null )
	{
		if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
		if ( entry is null || !Particles.TryGet( entry.SystemName, out var owned ) || !ReferenceEquals( owned, entry ) )
			return AssetPlacementResult.Failed( AssetRegistryIssue.CodeUnknownParticle,
				$"Particle system '{entry?.SystemName}' is not in the particle registry; register it before placing." );
		if ( !string.IsNullOrWhiteSpace( entry.DefinitionPath )
			&& !File.Exists( ResolvePath( ProjectRoot, entry.DefinitionPath ) ) )
			return AssetPlacementResult.Failed( AssetRegistryIssue.CodeMissingParticle,
				$"Cannot place particle system '{entry.SystemName}': definition file is missing on disk: '{entry.DefinitionPath}'." );
		var cls = entities.Registry.Find( "info_particle_system" );
		if ( cls is null )
			return AssetPlacementResult.Failed( EntityRegistry.CodeUnknownEntityClass,
				"Cannot place particle system: entity class 'info_particle_system' is not in the entity registry (the P3 soundscapes ticket owns it)." );
		if ( cls.FindKeyValue( "effect_name" ) is null )
			return AssetPlacementResult.Failed( EntityRegistry.CodeUnknownKeyValue,
				"Cannot place particle system: entity class 'info_particle_system' has no 'effect_name' keyvalue." );
		var keyvalues = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
		{
			{ "effect_name", entry.SystemName },
		};
		var placed = entities.PlacePointEntity( "info_particle_system", position, keyvalues, angles );
		Particles.NoteUsed( entry );
		return AssetPlacementResult.Succeeded( placed, "info_particle_system", Vec3.One );
	}

	/// <summary>Place a registered sound as an ambient_generic entity
	/// at <paramref name="position"/> with `message` = the sound path
	/// and `volume` = the entry volume. The entry must be registered
	/// and its file must exist. The core entity registry does not ship
	/// ambient_generic yet (owned by the P3 soundscapes ticket): when
	/// the supplied registry lacks the class — or the class lacks the
	/// `message` / `volume` keyvalues — this returns that named issue
	/// and places nothing, instead of inventing a class.</summary>
	public AssetPlacementResult PlaceSound( SoundEntry entry, MapEntities entities, Vec3 position, Vec3? angles = null )
	{
		if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
		if ( entry is null || !Sounds.TryGetByPath( entry.Path, out var owned ) || !ReferenceEquals( owned, entry ) )
			return AssetPlacementResult.Failed( AssetRegistryIssue.CodeUnknownSound,
				$"Sound '{entry?.Path}' is not in the sound registry; register it before placing." );
		if ( !File.Exists( ResolvePath( ProjectRoot, entry.Path ) ) )
			return AssetPlacementResult.Failed( AssetRegistryIssue.CodeMissingSound,
				$"Cannot place sound '{entry.Name}': file is missing on disk: '{entry.Path}'." );
		var cls = entities.Registry.Find( "ambient_generic" );
		if ( cls is null )
			return AssetPlacementResult.Failed( EntityRegistry.CodeUnknownEntityClass,
				"Cannot place sound: entity class 'ambient_generic' is not in the entity registry (the P3 soundscapes ticket owns it)." );
		if ( cls.FindKeyValue( "message" ) is null || cls.FindKeyValue( "volume" ) is null )
			return AssetPlacementResult.Failed( EntityRegistry.CodeUnknownKeyValue,
				"Cannot place sound: entity class 'ambient_generic' needs 'message' and 'volume' keyvalues." );
		var keyvalues = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
		{
			{ "message", entry.Path },
			{ "volume", entry.Volume.ToString( CultureInfo.InvariantCulture ) },
		};
		var placed = entities.PlacePointEntity( "ambient_generic", position, keyvalues, angles );
		Sounds.NoteUsed( entry );
		return AssetPlacementResult.Succeeded( placed, "ambient_generic", Vec3.One );
	}

	// --- Map usage accounting (hammer-browsers-used-count-replace-texture) -------------

	/// <summary>Per-texture alpha-channel data recorded on this facade
	/// (see <see cref="TextureAlphaInfo"/>). Material names are compared
	/// case-insensitively, like the registries.</summary>
	readonly Dictionary<string, TextureAlphaInfo> _textureAlpha = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>All recorded per-texture alpha data, in record order.
	/// Textures with no record are unknown (treated as opaque by the
	/// browser); use <see cref="TextureAlpha(string)"/> for one texture.</summary>
	public IReadOnlyList<TextureAlphaInfo> TextureAlphas =>
		_textureAlpha.Values.Select( a => a.Clone() ).ToArray();

	/// <summary>How many times one asset is referenced in
	/// <paramref name="document"/>: faces for a material, entities for a
	/// model / sound / particle (see <see cref="MapAssetUsage"/> for the
	/// exact reference rules). A registered model or sound may be named by
	/// display name or path; anything else is used as given. 0 for a blank
	/// key or an unreferenced asset. This counts DOCUMENT references, not
	/// the registries' placement tally (<c>UsedCount</c>).</summary>
	public int UseCount( MapDocument document, AssetKind kind, string nameOrPath )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		if ( string.IsNullOrWhiteSpace( nameOrPath ) ) return 0;
		return MapAssetUsage.CountUses( document, kind, ResolveAssetKey( kind, nameOrPath ) );
	}

	/// <summary>Reference counts for every asset of one kind referenced in
	/// <paramref name="document"/>: key (material name / model path /
	/// sound path / particle system name) to reference count. Only
	/// referenced assets appear; unreferenced assets are absent, not zero.</summary>
	public IReadOnlyDictionary<string, int> UseCounts( MapDocument document, AssetKind kind )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		return MapAssetUsage.CountAllUses( document, kind );
	}

	/// <summary>Replace one texture with another on every face that uses
	/// it (or only the selected faces when <paramref name="selectedFaces"/>
	/// is given), as a single undo step on <paramref name="undo"/>. UVs
	/// are preserved; only the material name changes. Faces on
	/// entity-bound brushes (func_detail, ...) are SKIPPED, not replaced —
	/// see <see cref="ReplaceTextureCommand"/> — and listed on the result.
	/// A replace with no matching faces is a no-op success with no undo
	/// step. <paramref name="undo"/> must target
	/// <paramref name="document"/>.</summary>
	public ReplaceTextureResult ReplaceTexture( MapDocument document, MapUndo undo,
		string oldMaterial, string newMaterial,
		IReadOnlyList<(string BrushId, int FaceIndex)>? selectedFaces = null )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		if ( undo is null ) throw new ArgumentNullException( nameof( undo ) );
		if ( !ReferenceEquals( undo.Target, document ) )
			throw new ArgumentException( "The MapUndo stack must target the same document.", nameof( undo ) );
		if ( string.IsNullOrWhiteSpace( oldMaterial ) )
			return ReplaceTextureResult.Failed( "InvalidMaterial", "Replace Texture needs a non-blank old material name." );
		if ( string.IsNullOrWhiteSpace( newMaterial ) )
			return ReplaceTextureResult.Failed( "InvalidMaterial", "Replace Texture needs a non-blank new material name." );
		oldMaterial = oldMaterial.Trim();
		newMaterial = newMaterial.Trim();
		if ( string.Equals( oldMaterial, newMaterial, StringComparison.OrdinalIgnoreCase ) )
			return ReplaceTextureResult.Succeeded( 0, 0, 0, Array.Empty<string>(), null );
		if ( selectedFaces is not null )
			foreach ( var (brushId, faceIndex) in selectedFaces )
			{
				var brush = document.FindBrush( brushId );
				if ( brush is null )
					return ReplaceTextureResult.Failed( "UnknownFace", $"Selected face names unknown brush '{brushId}'." );
				if ( faceIndex < 0 || faceIndex >= brush.Faces.Count )
					return ReplaceTextureResult.Failed( "UnknownFace",
						$"Brush '{brushId}' has {brush.Faces.Count} faces; face {faceIndex} is out of range." );
			}
		var command = ReplaceTextureCommand.TryCreate( document, oldMaterial, newMaterial, selectedFaces,
			out int facesSkipped, out var skippedBrushIds );
		if ( command is null )
			return ReplaceTextureResult.Succeeded( 0, 0, facesSkipped, skippedBrushIds, null );
		undo.Execute( command );
		return ReplaceTextureResult.Succeeded( command.FacesChanged, command.BrushesChanged,
			facesSkipped, skippedBrushIds, command );
	}

	/// <summary>Replace one model with another on every entity whose
	/// `model` keyvalue names it (or only the selected entities when
	/// <paramref name="selectedEntityIds"/> is given), as a single undo
	/// step on <paramref name="undo"/> (one
	/// <see cref="SetEntityPropertyCommand"/> per entity, composed).
	/// <paramref name="newModel"/> must be a registered model (name or
	/// path); <paramref name="oldModel"/> may be any model path referenced
	/// in the document, registered or not. A replace with no matching
	/// entities is a no-op success with no undo step.
	/// <paramref name="undo"/> must target <paramref name="document"/>.</summary>
	public ReplaceModelResult ReplaceModel( MapDocument document, MapUndo undo,
		string oldModel, string newModel, IReadOnlyList<string>? selectedEntityIds = null )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		if ( undo is null ) throw new ArgumentNullException( nameof( undo ) );
		if ( !ReferenceEquals( undo.Target, document ) )
			throw new ArgumentException( "The MapUndo stack must target the same document.", nameof( undo ) );
		if ( string.IsNullOrWhiteSpace( oldModel ) )
			return ReplaceModelResult.Failed( "InvalidModel", "Replace Model needs a non-blank old model path." );
		if ( string.IsNullOrWhiteSpace( newModel ) )
			return ReplaceModelResult.Failed( "InvalidModel", "Replace Model needs a non-blank new model path." );
		string newPath;
		try { newPath = AssetHotload.NormalizePath( newModel ); }
		catch ( ArgumentException )
		{
			return ReplaceModelResult.Failed( "InvalidModel", $"New model path '{newModel}' is not a usable project-relative path." );
		}
		var oldPath = ResolveAssetKey( AssetKind.Model, oldModel );
		if ( string.Equals( oldPath, newPath, StringComparison.OrdinalIgnoreCase ) )
			return ReplaceModelResult.Succeeded( 0, null );
		if ( !Models.TryGetByPath( newPath, out _ ) )
			return ReplaceModelResult.Failed( AssetRegistryIssue.CodeUnknownModel,
				$"Model '{newModel}' is not in the model registry; register it before replacing to it." );
		HashSet<string>? selected = selectedEntityIds is null
			? null
			: new HashSet<string>( selectedEntityIds, StringComparer.Ordinal );
		if ( selected is not null )
			foreach ( var id in selected )
				if ( document.FindEntity( id ) is null )
					return ReplaceModelResult.Failed( "UnknownEntity", $"Selected entity '{id}' does not exist." );
		var children = new List<MapCommand>();
		foreach ( var entity in document.Entities )
		{
			if ( selected is not null && !selected.Contains( entity.Id ) ) continue;
			if ( !entity.KeyValues.TryGetValue( "model", out var current ) ) continue;
			if ( !MapAssetUsage.PathsEqual( current, oldPath ) ) continue;
			children.Add( new SetEntityPropertyCommand( entity.Id, "model", current, newPath, true ) );
		}
		if ( children.Count == 0 )
			return ReplaceModelResult.Succeeded( 0, null );
		MapCommand command = children.Count == 1
			? children[0]
			: new CompositeMapCommand( $"Replace model '{oldPath}' with '{newPath}' ({children.Count} entities)", children );
		undo.Execute( command );
		return ReplaceModelResult.Succeeded( children.Count, command );
	}

	/// <summary>Record per-texture alpha data by hand (or from an image
	/// header the caller decoded): whether the texture carries an alpha
	/// channel, its opacity 0..1 when known, and where the data came from.
	/// Overwrites any earlier record for the material. The browser reads
	/// this back through <see cref="TextureAlpha(string)"/> /
	/// <see cref="HasAlpha(string)"/>.</summary>
	public void NoteTextureAlpha( string materialName, bool hasAlpha, float opacity = 1f, string source = "manual" )
	{
		if ( string.IsNullOrWhiteSpace( materialName ) )
			throw new ArgumentException( "A texture needs a non-blank material name.", nameof( materialName ) );
		if ( !float.IsFinite( opacity ) || opacity < 0f || opacity > 1f )
			throw new ArgumentException( "Opacity must be 0..1.", nameof( opacity ) );
		var name = materialName.Trim();
		_textureAlpha[name] = new TextureAlphaInfo
		{
			MaterialName = name,
			HasAlpha = hasAlpha,
			Opacity = opacity,
			Source = string.IsNullOrWhiteSpace( source ) ? "manual" : source.Trim(),
		};
	}

	/// <summary>The recorded alpha data for one texture, or null when
	/// nothing has been recorded for it (unknown — the browser treats
	/// unknown as opaque). The returned record is a copy.</summary>
	public TextureAlphaInfo? TextureAlpha( string materialName )
	{
		if ( string.IsNullOrWhiteSpace( materialName ) ) return null;
		return _textureAlpha.TryGetValue( materialName.Trim(), out var info ) ? info.Clone() : null;
	}

	/// <summary>True only when alpha data has been recorded for the
	/// texture AND says it carries an alpha channel. Unknown textures
	/// read false — use <see cref="TextureAlpha(string)"/> to tell
	/// unknown apart from known-opaque.</summary>
	public bool HasAlpha( string materialName ) =>
		!string.IsNullOrWhiteSpace( materialName )
		&& _textureAlpha.TryGetValue( materialName.Trim(), out var info ) && info.HasAlpha;

	/// <summary>Record alpha data for a material from its VMT: HasAlpha
	/// when $translucent is "1", $alpha is present, or $alphatest is "1";
	/// Opacity from $alpha (clamped 0..1, 1 when absent). The material
	/// name defaults to the VMT's $basetexture. Returns the recorded
	/// info. This reads the VMT the caller parsed — the kernel never
	/// decodes image files for alpha data.</summary>
	public TextureAlphaInfo NoteTextureAlphaFromVmt( VmtMaterial vmt, string materialName = null )
	{
		if ( vmt is null ) throw new ArgumentNullException( nameof( vmt ) );
		var name = string.IsNullOrWhiteSpace( materialName ) ? vmt.BaseTexture : materialName;
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "No material name: pass one, or a VMT with $basetexture.", nameof( materialName ) );
		bool translucent = string.Equals( vmt.Get( "$translucent" ), "1", StringComparison.Ordinal );
		string alphaText = vmt.Get( "$alpha" );
		bool alphaTest = string.Equals( vmt.Get( "$alphatest" ), "1", StringComparison.Ordinal );
		float opacity = 1f;
		if ( !string.IsNullOrWhiteSpace( alphaText )
			&& float.TryParse( alphaText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed )
			&& float.IsFinite( parsed ) )
			opacity = Math.Clamp( parsed, 0f, 1f );
		bool hasAlpha = translucent || alphaTest || !string.IsNullOrWhiteSpace( alphaText );
		string source = translucent ? "vmt:$translucent"
			: !string.IsNullOrWhiteSpace( alphaText ) ? "vmt:$alpha"
			: alphaTest ? "vmt:$alphatest" : "vmt:opaque";
		NoteTextureAlpha( name, hasAlpha, opacity, source );
		return TextureAlpha( name )!;
	}

	/// <summary>Resolve a UseCount / replace key to the canonical form
	/// the document counts against: a registered model / sound resolves
	/// by display name or path to its registered path; anything else is
	/// normalised as a project-relative path when possible, else used
	/// trimmed as given (materials and particle system names are never
	/// path-normalised).</summary>
	string ResolveAssetKey( AssetKind kind, string key )
	{
		switch ( kind )
		{
			case AssetKind.Model:
				if ( Models.TryGet( key, out var model ) ) return model.Path;
				break;
			case AssetKind.Sound:
				if ( Sounds.TryGet( key, out var sound ) ) return sound.Path;
				break;
			default:
				return key.Trim();
		}
		return MapAssetUsage.NormalizeKey( key );
	}
}

/// <summary>Reference counting against a map document, shared by the
/// three registries' <c>UsedOnly</c> and the facade's
/// <see cref="AssetRegistries.UseCount(MapDocument, AssetKind, string)"/>.
/// One reference = one face (material) or one entity (model / sound /
/// particle):
///   model:    an entity's `model` keyvalue;
///   material: a brush face's <see cref="FaceMaterial.MaterialName"/>;
///   sound:    an entity's `message` (ambient_generic) or `sound` keyvalue;
///   particle: an entity's `effect_name` (info_particle_system) keyvalue.
/// Comparisons are OrdinalIgnoreCase (Source names are case-insensitive in
/// practice, and the registries look up the same way). This counts
/// REFERENCES IN THE DOCUMENT, not the registries' placement tally
/// (<c>UsedCount</c> / <c>NoteUsed</c>): a model placed ten times then
/// deleted from the map has UsedCount 10 and UseCount 0.</summary>
internal static class MapAssetUsage
{
	public static int CountUses( MapDocument document, AssetKind kind, string key )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		if ( string.IsNullOrWhiteSpace( key ) ) return 0;
		return CountAllUses( document, kind ).TryGetValue( key.Trim(), out var count ) ? count : 0;
	}

	public static IReadOnlyDictionary<string, int> CountAllUses( MapDocument document, AssetKind kind )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		var counts = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		void Add( string key )
		{
			if ( string.IsNullOrWhiteSpace( key ) ) return;
			key = kind is AssetKind.Model or AssetKind.Sound ? NormalizeKey( key ) : key.Trim();
			counts[key] = counts.TryGetValue( key, out var c ) ? c + 1 : 1;
		}
		switch ( kind )
		{
			case AssetKind.Material:
				foreach ( var brush in document.Brushes )
					foreach ( var face in brush.Faces )
						Add( face.Material?.MaterialName ?? "" );
				break;
			case AssetKind.Model:
				foreach ( var entity in document.Entities )
					if ( entity.KeyValues.TryGetValue( "model", out var model ) )
						Add( model );
				break;
			case AssetKind.Sound:
				foreach ( var entity in document.Entities )
				{
					if ( entity.KeyValues.TryGetValue( "message", out var message ) ) Add( message );
					if ( entity.KeyValues.TryGetValue( "sound", out var sound ) ) Add( sound );
				}
				break;
			case AssetKind.Particle:
				foreach ( var entity in document.Entities )
					if ( entity.KeyValues.TryGetValue( "effect_name", out var effect ) )
						Add( effect );
				break;
			default:
				throw new ArgumentOutOfRangeException( nameof( kind ), kind, "Unknown asset kind." );
		}
		return counts;
	}

	/// <summary>Best-effort project-relative normalisation: a bad path
	/// comes back trimmed as given instead of throwing, so counting a
	/// stale reference never fails the count.</summary>
	internal static string NormalizeKey( string key )
	{
		try { return AssetHotload.NormalizePath( key ); }
		catch ( ArgumentException ) { return key.Trim(); }
	}

	/// <summary>Two model / sound paths name the same asset when their
	/// normalised forms match case-insensitively.</summary>
	internal static bool PathsEqual( string a, string b ) =>
		string.Equals( NormalizeKey( a ), NormalizeKey( b ), StringComparison.OrdinalIgnoreCase );
}

/// <summary>Per-texture alpha-channel data for the future asset browser:
/// whether the texture carries an alpha channel (translucent or
/// alpha-tested), the opacity when known, and where the data came from.
/// The kernel never decodes image files for this: data arrives from a VMT
/// (<see cref="AssetRegistries.NoteTextureAlphaFromVmt"/>) or is noted by
/// hand (<see cref="AssetRegistries.NoteTextureAlpha(string, bool, float, string)"/>).
/// A texture with no record is unknown — the browser treats unknown as
/// opaque; <see cref="AssetRegistries.TextureAlpha(string)"/> returns null
/// for it, which is how the browser tells unknown apart from
/// known-opaque.</summary>
public sealed class TextureAlphaInfo
{
	public string MaterialName = "";
	public bool HasAlpha;
	/// <summary>Opacity 0..1 when known (VMT $alpha); 1 otherwise.</summary>
	public float Opacity = 1f;
	/// <summary>Where the data came from, e.g. "vmt:$translucent", "vmt:$alpha", "manual".</summary>
	public string Source = "";

	public TextureAlphaInfo Clone() => new()
	{
		MaterialName = MaterialName,
		HasAlpha = HasAlpha,
		Opacity = Opacity,
		Source = Source,
	};

	public override string ToString() => HasAlpha
		? $"{MaterialName} alpha (opacity {Opacity}, {Source})"
		: $"{MaterialName} opaque ({Source})";
}

/// <summary>Outcome of one <see cref="AssetRegistries.ReplaceTexture"/>
/// call. <see cref="Ok"/> false always carries a machine-readable
/// <see cref="Code"/> and a human <see cref="Message"/>, and nothing was
/// changed. A no-op success (nothing used the old material, or old == new)
/// has <see cref="FacesChanged"/> 0 and no command. Faces on entity-bound
/// brushes are never silently dropped: they are counted in
/// <see cref="FacesSkipped"/> and their brush ids listed in
/// <see cref="SkippedBrushIds"/> (see <see cref="ReplaceTextureCommand"/>).</summary>
public sealed class ReplaceTextureResult
{
	public bool Ok;
	public string Code = "";
	public string Message = "";
	public int FacesChanged;
	public int BrushesChanged;
	public int FacesSkipped;
	public IReadOnlyList<string> SkippedBrushIds = Array.Empty<string>();
	/// <summary>The executed undoable command, or null when nothing was executed (no-op or failure).</summary>
	public ReplaceTextureCommand? Command;
	public bool IsNoOp => FacesChanged == 0;

	public static ReplaceTextureResult Succeeded( int facesChanged, int brushesChanged,
		int facesSkipped, IReadOnlyList<string> skippedBrushIds, ReplaceTextureCommand? command ) =>
		new()
		{
			Ok = true,
			FacesChanged = facesChanged,
			BrushesChanged = brushesChanged,
			FacesSkipped = facesSkipped,
			SkippedBrushIds = skippedBrushIds ?? Array.Empty<string>(),
			Command = command,
		};

	public static ReplaceTextureResult Failed( string code, string message ) =>
		new() { Ok = false, Code = code, Message = message };

	public override string ToString() => Ok
		? $"replaced {FacesChanged} faces on {BrushesChanged} brushes"
			+ ( FacesSkipped > 0 ? $", skipped {FacesSkipped} faces on {SkippedBrushIds.Count} entity-bound brushes" : "" )
		: $"[{Code}] {Message}";
}

/// <summary>Outcome of one <see cref="AssetRegistries.ReplaceModel"/>
/// call. <see cref="Ok"/> false always carries a machine-readable
/// <see cref="Code"/> and a human <see cref="Message"/>, and nothing was
/// changed. A no-op success has <see cref="EntitiesChanged"/> 0 and no
/// command.</summary>
public sealed class ReplaceModelResult
{
	public bool Ok;
	public string Code = "";
	public string Message = "";
	public int EntitiesChanged;
	/// <summary>The executed undoable command, or null when nothing was executed (no-op or failure).</summary>
	public MapCommand? Command;
	public bool IsNoOp => EntitiesChanged == 0;

	public static ReplaceModelResult Succeeded( int entitiesChanged, MapCommand? command ) =>
		new() { Ok = true, EntitiesChanged = entitiesChanged, Command = command };

	public static ReplaceModelResult Failed( string code, string message ) =>
		new() { Ok = false, Code = code, Message = message };

	public override string ToString() => Ok
		? $"replaced the model on {EntitiesChanged} entities"
		: $"[{Code}] {Message}";
}

/// <summary>One undoable Replace Texture over a <see cref="MapDocument"/>:
/// every face whose material is <c>_old</c> (within the construction-time
/// selection, or all faces when unscoped) gets <c>_new</c>, keeping its UV.
/// One command is one undo step for the whole replace.
///
/// HOW IT EDITS — read before re-pointing this at another target: the
/// frozen <see cref="IMapUndoTarget"/> surface cannot set one face's
/// material. Its brush state carries a single material per brush
/// (<see cref="MapBrushState"/>, lossy by design — see
/// <see cref="MapBrushData.ToBrushState"/>) and
/// <see cref="IMapUndoTarget.SetBrushMaterial(string, string)"/>
/// re-textures a WHOLE brush, which would clobber mixed-material brushes
/// (a nodraw face would become the new texture). So this command drives
/// the <see cref="MapDocument"/> public API directly: RemoveBrush +
/// AddBrush with full per-face clones. That keeps geometry, layers,
/// visgroups and per-face materials / UVs exact, and displacements on a
/// replaced brush are carried across (captured as clones, re-added after
/// the brush). Two honest limits, both reported by
/// <see cref="AssetRegistries.ReplaceTexture"/> and never hidden:
///  (1) a replaced brush moves to the end of the document's brush list
///      (brushes are a set; identity and content are exact, order is
///      not — the same set of brushes, in a different list order);
///  (2) a brush bound to an entity (func_detail, ...) is SKIPPED, not
///      replaced: RemoveBrush drops the entity's brush binding and no
///      history-safe API restores it, so replacing it would silently
///      unbind the brush. The proper fix is a per-face SetFaceMaterial
///      history entry in MapDocument.cs (document-owner work); until it
///      lands, skipped brushes are listed on the result.
/// The command throws <see cref="InvalidOperationException"/> on a
/// non-MapDocument target. Like every <see cref="MapCommand"/>, it
/// assumes no interleaved edit between construction and Do: a face or
/// displacement added to an affected brush after construction is not in
/// the captured before-state.</summary>
public sealed class ReplaceTextureCommand : MapCommand
{
	sealed class BrushReplacement
	{
		public string BrushId = "";
		public MapBrushData Before = null!;
		public MapBrushData After = null!;
		public List<MapDisplacementData> Displacements = new();
	}

	readonly string _old, _new;
	readonly List<BrushReplacement> _brushes;
	readonly IReadOnlyList<(string BrushId, int FaceIndex)>? _selection;
	readonly int _facesChanged;

	/// <summary>Faces whose material changed (before + after differ).</summary>
	public int FacesChanged => _facesChanged;
	/// <summary>Brushes that had at least one face replaced.</summary>
	public int BrushesChanged => _brushes.Count;
	public string OldMaterial => _old;
	public string NewMaterial => _new;

	private ReplaceTextureCommand( string oldMaterial, string newMaterial,
		List<BrushReplacement> brushes, IReadOnlyList<(string BrushId, int FaceIndex)>? selection )
	{
		_old = oldMaterial;
		_new = newMaterial;
		_brushes = brushes ?? throw new ArgumentNullException( nameof( brushes ) );
		_selection = selection;
		foreach ( var r in _brushes )
			for ( var i = 0; i < r.Before.Faces.Count; i++ )
				if ( !string.Equals( r.Before.Faces[i].Material?.MaterialName,
					r.After.Faces[i].Material?.MaterialName, StringComparison.Ordinal ) )
					_facesChanged++;
		Description = $"Replace texture '{_old}' with '{_new}' ({_facesChanged} faces, {_brushes.Count} brushes)";
		Kind = MapEditKind.Brush;
	}

	/// <summary>Plan a replace against the document's current state, or
	/// null when no face matches (nothing to do). Skipped faces on
	/// entity-bound brushes are counted in <paramref name="facesSkipped"/>
	/// and their brush ids in <paramref name="skippedBrushIds"/>; they are
	/// not in the command. Assumes the selection was already validated
	/// (every selected face exists).</summary>
	internal static ReplaceTextureCommand? TryCreate( MapDocument document,
		string oldMaterial, string newMaterial,
		IReadOnlyList<(string BrushId, int FaceIndex)>? selectedFaces,
		out int facesSkipped, out IReadOnlyList<string> skippedBrushIds )
	{
		facesSkipped = 0;
		var skipped = new List<string>();
		var replacements = new List<BrushReplacement>();
		HashSet<(string, int)>? selected = selectedFaces is null
			? null
			: new HashSet<(string, int)>( selectedFaces );
		var boundBrushIds = new HashSet<string>(
			document.Entities.SelectMany( e => e.BrushIds ), StringComparer.Ordinal );
		foreach ( var brush in document.Brushes )
		{
			var after = brush.Clone();
			int changed = 0, skippedHere = 0;
			for ( var i = 0; i < brush.Faces.Count; i++ )
			{
				var face = brush.Faces[i];
				if ( face.Material is null
					|| !string.Equals( face.Material.MaterialName, oldMaterial, StringComparison.OrdinalIgnoreCase ) )
					continue;
				if ( selected is not null && !selected.Contains( (brush.Id, i) ) ) continue;
				if ( boundBrushIds.Contains( brush.Id ) ) { skippedHere++; continue; }
				after.Faces[i] = new MapBrushFaceData( face.Normal, face.Distance,
					face.Material.WithMaterial( newMaterial ) );
				changed++;
			}
			if ( skippedHere > 0 )
			{
				facesSkipped += skippedHere;
				skipped.Add( brush.Id );
			}
			if ( changed == 0 ) continue;
			var displacements = document.Displacements
				.Where( d => d.BrushId == brush.Id )
				.Select( d => d.Clone() ).ToList();
			replacements.Add( new BrushReplacement
			{
				BrushId = brush.Id,
				Before = brush.Clone(),
				After = after,
				Displacements = displacements,
			} );
		}
		skippedBrushIds = skipped;
		if ( replacements.Count == 0 ) return null;
		return new ReplaceTextureCommand( oldMaterial, newMaterial, replacements, selectedFaces );
	}

	static MapDocument RequireDocument( IMapUndoTarget target ) =>
		target as MapDocument ?? throw new InvalidOperationException(
			"Replace Texture needs a MapDocument target: per-face materials are not representable "
			+ "on the lossy IMapUndoTarget brush state (MapBrushState carries one material per "
			+ "brush), and SetBrushMaterial would re-texture whole brushes. See ReplaceTextureCommand." );

	void Apply( MapDocument document, bool toAfter )
	{
		foreach ( var r in _brushes )
		{
			document.RemoveBrush( r.BrushId );
			document.AddBrush( ( toAfter ? r.After : r.Before ).Clone() );
			foreach ( var displacement in r.Displacements )
				document.AddDisplacement( displacement.Clone() );
		}
	}

	public override void Do( IMapUndoTarget target ) => Apply( RequireDocument( target ), true );
	public override void Undo( IMapUndoTarget target ) => Apply( RequireDocument( target ), false );
	public override bool IsNoOp => _brushes.Count == 0;

	public override MapCommand Repeat( IMapUndoTarget target )
	{
		var document = target as MapDocument;
		if ( document is null ) return null;
		return TryCreate( document, _old, _new, _selection, out _, out _ );
	}
}
