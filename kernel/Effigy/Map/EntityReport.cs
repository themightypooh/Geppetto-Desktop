// Entity Report API (hammer-entity-report-find-replace).
//
// Hammer's Entity Report lists all entities in the map, lets the user select
// and edit every entity of a class at once, and provides the "find the
// compiler error" tools: Find/Replace entities and Go to Brush Number.
// Native Geppetto equivalent: this file is data + API only, no report dialog
// UI (UI deferred per the ticket).
//
// DEPENDENCY STATUS (2026-11-13): MapEntities (hammer-entity-place-edit-
// validate, worker 14) HAS landed since this file was first written, and it
// matches its filed spec exactly — PlacePointEntity / PlaceBrushEntity /
// Tie / Untie / SetKeyvalue with registry type checks / FindByClass /
// FindByTargetName are all present with the spec'd semantics, so there is
// no spec mismatch to report. The report builds on it through
// ReportEntity.FromMapEntity / EntityReport.FromMapEntities (see below);
// the query / bulk-edit / find-replace / brush-lookup semantics in this
// file are the contract sibling workers build against and must not be
// silently changed.
//
// I/O MODEL SEAM: EntityIo's rows are now EntityIoOutput / EntityIoInput
// (worker 15 renamed them, resolving the EntityOutput / EntityInput
// CS0101 collision with EntityRegistry). This file still carries its own
// row, ReportOutput, which mirrors one EntityIoOutput / VMF
// connections-block row (event, target, input, parameter, delay,
// times-to-fire) 1:1. Find/Replace target renames rewrite the ReportOutput
// rows in this report; they do NOT yet rewrite EntityIoList rows — that
// write-back belongs to the integrator (the per-entity change list records
// every output rewrite so it can be audited).
//
// CLASSNAME RULE: MapEntities fixes the class at placement — its
// SetKeyvalue rejects "classname" with ReadOnlyKeyvalue. Report-level class
// replacement (FindReplace ReplaceClass, BulkSetKeyvalue "classname")
// operates on the report view only; applying it to a MapEntities
// collection must be a remove + re-place, never a SetKeyvalue call.
//
// BRUSH NUMBERS: MapBrush carries no numeric id — compiler brush numbers
// are a VMF-import concept (VmfIo owns the numbering). FromMapEntities
// therefore takes an optional brushNumberOf function; without one the
// report's brush numbers are empty and BrushNumberLookup cannot resolve.
//
// Brush numbers are the compiler brush ids (VMF brush "id"): a brush
// entity owns its brushes' numbers; worldspawn brushes are registered with
// RegisterWorldBrush. BrushNumberLookup resolves a number to its owning
// entity (or to a world brush), which is Hammer's "Go to Brush Number".
//
// No Desktop UI, no Hammer/VBSP/VVIS/VRAD, no engine types. Kernel only.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>How a query pattern matches a value.</summary>
public enum EntityReportMatchMode
{
	/// <summary>Value contains the pattern.</summary>
	Substring,
	/// <summary>'*' matches any run of characters, '?' matches one character.</summary>
	Wildcard,
	/// <summary>Value equals the pattern.</summary>
	Exact
}

/// <summary>
/// One output row as the report sees it: when <see cref="Event"/> fires,
/// send <see cref="Input"/> to <see cref="TargetName"/> after
/// <see cref="DelaySeconds"/> seconds, at most <see cref="MaxFires"/> times
/// (-1 = forever). Mirrors one EntityIoOutput row / VMF connections-block
/// row and one row of Hammer's Outputs tab (see the file header for why
/// this is a separate type rather than EntityIoOutput itself).
/// </summary>
public sealed class ReportOutput
{
	public string Event = "";
	public string TargetName = "";
	public string Input = "";
	public string Parameter = "";
	public double DelaySeconds;
	public int MaxFires = -1;

	public ReportOutput() { }

	public ReportOutput( string @event, string targetName, string input,
		double delaySeconds = 0, int maxFires = -1, string parameter = "" )
	{
		Event = @event ?? "";
		TargetName = targetName ?? "";
		Input = input ?? "";
		Parameter = parameter ?? "";
		DelaySeconds = delaySeconds;
		MaxFires = maxFires;
	}

	public ReportOutput Copy() => new( Event, TargetName, Input, DelaySeconds, MaxFires, Parameter );
}

/// <summary>
/// One placed entity as the report sees it: a stable id, a class name, its
/// keyvalues (including "targetname"), the compiler brush numbers it owns
/// (empty for point entities), and its output rows. Build one from a
/// MapEntities entity with <see cref="FromMapEntity"/>, or directly from a
/// fixture / parsed map data. Keyvalue names are case-insensitive, matching
/// Source behaviour. "origin" is not a keyvalue (as in MapEntity) — it lives
/// on <see cref="Origin"/>.
/// </summary>
public sealed class ReportEntity
{
	/// <summary>Stable id within the map (VMF entity id). 0 = unassigned.</summary>
	public int Id;
	/// <summary>Entity class, e.g. "npc_citizen" or "func_door".</summary>
	public string ClassName = "";
	/// <summary>Keyvalues by name (case-insensitive). "targetname" lives here.</summary>
	public Dictionary<string, string> KeyValues = new( StringComparer.OrdinalIgnoreCase );
	/// <summary>Compiler brush numbers owned by this entity (brush entities only).</summary>
	public List<int> BrushNumbers = new();
	/// <summary>Output rows owned by this entity (Hammer Outputs tab).</summary>
	public List<ReportOutput> Outputs = new();

	public ReportEntity() { }

	public ReportEntity( int id, string className )
	{
		Id = id;
		ClassName = className ?? "";
	}

	public ReportEntity( int id, string className, IDictionary<string, string> keyValues,
		IEnumerable<int> brushNumbers ) : this( id, className )
	{
		if ( keyValues != null )
			foreach ( var pair in keyValues )
				SetKeyValue( pair.Key, pair.Value ?? "" );
		if ( brushNumbers != null )
			BrushNumbers.AddRange( brushNumbers );
	}

	/// <summary>Placement origin (inches, Z-up). Not a keyvalue — mirrors <see cref="MapEntity.Origin"/>.</summary>
	public Vec3 Origin;

	/// <summary>
	/// Builds a report view of a <see cref="MapEntity"/>: id, canonical class,
	/// keyvalues (which never contain "origin" — that lives on
	/// <see cref="Origin"/>), and the compiler brush numbers supplied by
	/// <paramref name="brushNumbers"/> (MapBrush carries no numeric id; the
	/// VMF layer supplies the numbering). Output rows, when the caller has
	/// them from an EntityIoList, come via <paramref name="outputs"/>.
	/// </summary>
	public static ReportEntity FromMapEntity( MapEntity entity, IEnumerable<int> brushNumbers = null,
		IEnumerable<ReportOutput> outputs = null )
	{
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		var view = new ReportEntity( entity.Id, entity.ClassName );
		view.Origin = entity.Origin;
		foreach ( var pair in entity.Keyvalues )
			view.KeyValues[pair.Key] = pair.Value;
		if ( brushNumbers != null )
			view.BrushNumbers.AddRange( brushNumbers );
		if ( outputs != null )
			view.Outputs.AddRange( outputs );
		return view;
	}

	/// <summary>Target name ("targetname" keyvalue), or "" when unnamed.</summary>
	public string TargetName => GetKeyValue( "targetname" ) ?? "";

	/// <summary>Keyvalue by name (case-insensitive); "classname" returns <see cref="ClassName"/>. Null when absent.</summary>
	public string GetKeyValue( string key )
	{
		if ( string.IsNullOrWhiteSpace( key ) ) return null;
		if ( string.Equals( key, "classname", StringComparison.OrdinalIgnoreCase ) ) return ClassName;
		return KeyValues.TryGetValue( key, out var value ) ? value : null;
	}

	/// <summary>True when the entity has a keyvalue of this name (case-insensitive).</summary>
	public bool HasKeyValue( string key )
	{
		if ( string.IsNullOrWhiteSpace( key ) ) return false;
		if ( string.Equals( key, "classname", StringComparison.OrdinalIgnoreCase ) ) return !string.IsNullOrEmpty( ClassName );
		return KeyValues.ContainsKey( key );
	}

	/// <summary>
	/// Sets a keyvalue. "classname" sets <see cref="ClassName"/> instead of
	/// going into <see cref="KeyValues"/>; every other key is stored as-is.
	/// </summary>
	public void SetKeyValue( string key, string value )
	{
		if ( string.IsNullOrWhiteSpace( key ) ) throw new ArgumentException( "Keyvalue has no name.", nameof( key ) );
		if ( value is null ) throw new ArgumentNullException( nameof( value ) );
		if ( string.Equals( key, "classname", StringComparison.OrdinalIgnoreCase ) ) { ClassName = value; return; }
		KeyValues[key] = value;
	}

	/// <summary>Removes a keyvalue. Returns true when one was present. "classname" cannot be removed.</summary>
	public bool RemoveKeyValue( string key )
	{
		if ( string.IsNullOrWhiteSpace( key ) ) return false;
		if ( string.Equals( key, "classname", StringComparison.OrdinalIgnoreCase ) ) return false;
		return KeyValues.Remove( key );
	}
}

/// <summary>
/// One report query: list/search all entities by class, keyvalue and/or
/// target. Every set field must match (AND); an unset field matches
/// everything. Class is always an exact case-insensitive match; keyvalue
/// and target values use <see cref="MatchMode"/>.
/// </summary>
public sealed class EntityReportQuery
{
	/// <summary>Exact class name (case-insensitive). Null/empty = any class.</summary>
	public string ClassName;
	/// <summary>Keyvalue key to filter on. Null/empty = no keyvalue filter.</summary>
	public string KeyName;
	/// <summary>Pattern the key's value must match. Null = the key must merely exist.</summary>
	public string KeyPattern;
	/// <summary>Pattern the targetname must match. Null/empty = no target filter.</summary>
	public string TargetPattern;
	/// <summary>How KeyPattern / TargetPattern match. Default Substring.</summary>
	public EntityReportMatchMode MatchMode = EntityReportMatchMode.Substring;
	/// <summary>False (default) = case-insensitive matching, matching Source targetnames.</summary>
	public bool CaseSensitive;

	public bool Matches( ReportEntity entity )
	{
		if ( entity is null ) return false;
		if ( !string.IsNullOrEmpty( ClassName )
			&& !string.Equals( entity.ClassName, ClassName, StringComparison.OrdinalIgnoreCase ) ) return false;
		if ( !string.IsNullOrEmpty( KeyName ) )
		{
			string value = entity.GetKeyValue( KeyName );
			if ( value is null ) return false;
			if ( KeyPattern != null && !EntityReport.MatchesPattern( value, KeyPattern, MatchMode, CaseSensitive ) ) return false;
		}
		if ( !string.IsNullOrEmpty( TargetPattern )
			&& !EntityReport.MatchesPattern( entity.TargetName, TargetPattern, MatchMode, CaseSensitive ) ) return false;
		return true;
	}
}

/// <summary>One entity's outcome from <see cref="EntityReport.BulkSetKeyvalue"/>.</summary>
public sealed class BulkSetKeyvalueEntry
{
	public ReportEntity Entity;
	public bool Changed;
	/// <summary>Named error when the set was refused for this entity ("" when applied or unchanged). Registry codes: UnknownEntityClass / UnknownKeyValue / InvalidKeyValueType.</summary>
	public string Error = "";
}

/// <summary>Outcome of <see cref="EntityReport.BulkSetKeyvalue"/>.</summary>
public sealed class BulkSetKeyvalueResult
{
	public List<BulkSetKeyvalueEntry> Entries = new();
	/// <summary>Entities whose value actually changed.</summary>
	public int Changed;
	/// <summary>
	/// Target names shared by more than one entity after the edit, when the
	/// edited key was "targetname" (Hammer warns on duplicate targetnames —
	/// the engine fires every entity sharing the name). Empty otherwise.
	/// </summary>
	public IReadOnlyList<string> DuplicateTargetNames = Array.Empty<string>();
}

/// <summary>One Find/Replace request: match by class, target name and/or keyvalue; replace class, target name and/or keyvalue.</summary>
public sealed class FindReplaceRequest
{
	/// <summary>Exact class to match (case-insensitive). Null/empty = any class.</summary>
	public string FindClass;
	/// <summary>Pattern the targetname must match. Null/empty = no target filter.</summary>
	public string FindTargetName;
	/// <summary>Keyvalue key to match. Null/empty = no keyvalue filter.</summary>
	public string FindKey;
	/// <summary>Pattern the matched key's value must match. Null = the key must merely exist.</summary>
	public string FindValuePattern;
	/// <summary>New class for matched entities. Null = keep the class.</summary>
	public string ReplaceClass;
	/// <summary>New targetname for matched entities. Null = keep the targetname.</summary>
	public string ReplaceTargetName;
	/// <summary>Rename the matched key to this name. Null = keep the key name.</summary>
	public string ReplaceKey;
	/// <summary>New value for the matched key. Null = keep the value.</summary>
	public string ReplaceValue;
	/// <summary>How FindTargetName / FindValuePattern match. Default Substring.</summary>
	public EntityReportMatchMode MatchMode = EntityReportMatchMode.Substring;
	/// <summary>False (default) = case-insensitive matching.</summary>
	public bool CaseSensitive;
	/// <summary>
	/// True (default): renaming a targetname also rewrites every output row
	/// targeting the old name and, when a registry is bound, every
	/// target-typed keyvalue (e.g. "target", "parentname", "filtername")
	/// holding the old name. Without a registry only outputs are rewritten.
	/// </summary>
	public bool RenameTargets = true;
}

/// <summary>One entity's outcome from <see cref="EntityReport.FindReplace"/>.</summary>
public sealed class FindReplaceEntityResult
{
	public ReportEntity Entity;
	/// <summary>True when the entity matched the find criteria.</summary>
	public bool Matched;
	/// <summary>True when the entity was actually modified (including output/keyvalue rewrites on an unmatched entity).</summary>
	public bool Changed;
	/// <summary>Human-readable change list, e.g. "targetname 'door_01' -&gt; 'door_02'", "output 'OnPressed' target 'door_01' -&gt; 'door_02'".</summary>
	public List<string> Changes = new();
	/// <summary>Named error when a replacement was refused for this entity ("" when none).</summary>
	public string Error = "";
}

/// <summary>Outcome of <see cref="EntityReport.FindReplace"/>.</summary>
public sealed class FindReplaceResult
{
	/// <summary>Request-level error ("" when the request ran). Nothing changes when this is set.</summary>
	public string Error = "";
	/// <summary>Warnings that do not block the replace, e.g. a targetname that is now shared by several entities.</summary>
	public List<string> Warnings = new();
	/// <summary>Per-entity outcomes: every matched entity, plus any unmatched entity whose outputs/keyvalues were rewritten by a target rename.</summary>
	public List<FindReplaceEntityResult> PerEntity = new();
	/// <summary>Entities matching the find criteria.</summary>
	public int Matched;
	/// <summary>Entities actually modified.</summary>
	public int Changed;
}

/// <summary>Outcome of <see cref="EntityReport.BrushNumberLookup"/>.</summary>
public sealed class BrushNumberLookupResult
{
	/// <summary>The brush number that was looked up.</summary>
	public int BrushNumber;
	/// <summary>True when the number resolves to a brush in this map.</summary>
	public bool Found;
	/// <summary>The entity owning the brush, or null for a world brush / not found.</summary>
	public ReportEntity Entity;
	/// <summary>True when the brush belongs to worldspawn (no owning entity).</summary>
	public bool IsWorldBrush;
	/// <summary>Why the lookup failed ("" when found).</summary>
	public string Reason = "";
}

/// <summary>Counts for <see cref="EntityReport.Summarize"/>.</summary>
public sealed class EntityReportSummary
{
	public int Total;
	/// <summary>Entity count per class (case-insensitive keys).</summary>
	public Dictionary<string, int> ByClass = new( StringComparer.OrdinalIgnoreCase );
	/// <summary>Target names shared by more than one entity (first-seen spelling).</summary>
	public List<string> DuplicateTargetNames = new();
	/// <summary>Entities with no targetname at all.</summary>
	public int EntitiesWithoutTargetName;
}

/// <summary>
/// Entity Report: list/search all entities by class/keyvalue/target
/// (substring + wildcard), SelectAllByClass, bulk keyvalue edit on a
/// selection, Find/Replace entities (by class, target name, or keyvalue
/// match) with a per-entity result list, and Go to Brush Number.
///
/// The report holds live references to the <see cref="ReportEntity"/>
/// objects it was built from (the list itself is copied), so edits made
/// through this API are visible to the caller and vice versa. An optional
/// <see cref="EntityRegistry"/> turns bulk edits and replacements into
/// type-checked ones (unknown key / mistyped value / unknown replacement
/// class are per-entity named errors and change nothing for that entity);
/// without a registry any key/value is accepted.
/// </summary>
public sealed class EntityReport
{
	readonly List<ReportEntity> _entities;
	readonly EntityRegistry _registry;
	readonly HashSet<int> _worldBrushes = new();

	/// <summary>Builds a report over <paramref name="entities"/> (live entity references; the list is copied). <paramref name="registry"/> may be null (no type checking).</summary>
	public EntityReport( IEnumerable<ReportEntity> entities, EntityRegistry registry = null )
	{
		_entities = entities is null ? new List<ReportEntity>() : new List<ReportEntity>( entities );
		_registry = registry;
	}

	/// <summary>
	/// Builds a report over a <see cref="MapEntities"/> collection: one
	/// <see cref="ReportEntity"/> per <see cref="MapEntity"/>, via
	/// <see cref="ReportEntity.FromMapEntity"/>. <paramref name="brushNumberOf"/>
	/// maps an entity to its compiler brush numbers (VMF numbering — see the
	/// file header); without it brush entities carry no numbers and
	/// <see cref="BrushNumberLookup"/> cannot resolve them. The report holds
	/// views, not the MapEntity objects: edits through this API do not write
	/// back into the MapEntities collection (the integrator owns write-back,
	/// including the classname remove + re-place rule).
	/// </summary>
	public static EntityReport FromMapEntities( MapEntities map, EntityRegistry registry = null,
		Func<MapEntity, IEnumerable<int>> brushNumberOf = null )
	{
		if ( map is null ) throw new ArgumentNullException( nameof( map ) );
		var views = new List<ReportEntity>();
		foreach ( var entity in map.Entities )
			views.Add( ReportEntity.FromMapEntity( entity,
				brushNumberOf is null ? null : brushNumberOf( entity ) ) );
		return new EntityReport( views, registry ?? map.Registry );
	}

	/// <summary>Entities in this report, in build order.</summary>
	public IReadOnlyList<ReportEntity> Entities => _entities;
	public int Count => _entities.Count;
	/// <summary>The registry bound for type checking, or null.</summary>
	public EntityRegistry Registry => _registry;

	/// <summary>Registers a compiler brush number owned by worldspawn (no entity). Brush numbers are positive VMF ids.</summary>
	public void RegisterWorldBrush( int brushNumber )
	{
		if ( brushNumber <= 0 ) throw new ArgumentException( "Brush numbers are positive VMF ids.", nameof( brushNumber ) );
		_worldBrushes.Add( brushNumber );
	}

	/// <summary>All entities matching <paramref name="query"/> (AND of its set fields), in build order.</summary>
	public IReadOnlyList<ReportEntity> Query( EntityReportQuery query )
	{
		if ( query is null ) throw new ArgumentNullException( nameof( query ) );
		var found = new List<ReportEntity>();
		foreach ( var entity in _entities )
			if ( entity != null && query.Matches( entity ) ) found.Add( entity );
		return found;
	}

	/// <summary>All entities of a class (exact, case-insensitive). Null/empty class returns none.</summary>
	public IReadOnlyList<ReportEntity> QueryByClass( string className )
	{
		if ( string.IsNullOrWhiteSpace( className ) ) return Array.Empty<ReportEntity>();
		return Query( new EntityReportQuery { ClassName = className.Trim() } );
	}

	/// <summary>Hammer's "select all entities of this class": every entity of <paramref name="className"/> (exact, case-insensitive). Null/empty class selects none.</summary>
	public IReadOnlyList<ReportEntity> SelectAllByClass( string className ) => QueryByClass( className );

	/// <summary>Entities with a targetname matching <paramref name="pattern"/>. Null/empty pattern returns none.</summary>
	public IReadOnlyList<ReportEntity> QueryByTarget( string pattern, EntityReportMatchMode mode = EntityReportMatchMode.Substring, bool caseSensitive = false )
	{
		if ( string.IsNullOrEmpty( pattern ) ) return Array.Empty<ReportEntity>();
		return Query( new EntityReportQuery { TargetPattern = pattern, MatchMode = mode, CaseSensitive = caseSensitive } );
	}

	/// <summary>Entities whose targetname is exactly <paramref name="targetName"/> (case-insensitive, Source targetnames). Null/empty returns none.</summary>
	public IReadOnlyList<ReportEntity> FindByTargetName( string targetName )
	{
		if ( string.IsNullOrWhiteSpace( targetName ) ) return Array.Empty<ReportEntity>();
		return Query( new EntityReportQuery { TargetPattern = targetName.Trim(), MatchMode = EntityReportMatchMode.Exact } );
	}

	/// <summary>
	/// Entities having keyvalue <paramref name="key"/> whose value matches
	/// <paramref name="pattern"/> (null pattern = the key must merely exist).
	/// Null/empty key returns none.
	/// </summary>
	public IReadOnlyList<ReportEntity> QueryByKeyValue( string key, string pattern, EntityReportMatchMode mode = EntityReportMatchMode.Substring, bool caseSensitive = false )
	{
		if ( string.IsNullOrWhiteSpace( key ) ) return Array.Empty<ReportEntity>();
		return Query( new EntityReportQuery { KeyName = key, KeyPattern = pattern, MatchMode = mode, CaseSensitive = caseSensitive } );
	}

	/// <summary>
	/// Bulk keyvalue edit on a selection (e.g. everything
	/// <see cref="SelectAllByClass"/> returned): sets <paramref name="key"/>
	/// to <paramref name="value"/> on each selected entity. With a registry
	/// bound, each entity is type-checked against its class first — a
	/// mistyped value or unknown key is a per-entity named error and leaves
	/// that entity unchanged. Setting "classname" renames the entity's
	/// class (unknown replacement class is refused when a registry is
	/// bound). Setting "targetname" to a name another entity already has
	/// is applied but reported in
	/// <see cref="BulkSetKeyvalueResult.DuplicateTargetNames"/>.
	/// </summary>
	public BulkSetKeyvalueResult BulkSetKeyvalue( IEnumerable<ReportEntity> selection, string key, string value )
	{
		if ( selection is null ) throw new ArgumentNullException( nameof( selection ) );
		if ( string.IsNullOrWhiteSpace( key ) ) throw new ArgumentException( "Keyvalue has no name.", nameof( key ) );
		if ( value is null ) throw new ArgumentNullException( nameof( value ) );
		var result = new BulkSetKeyvalueResult();
		foreach ( var entity in selection )
		{
			var entry = new BulkSetKeyvalueEntry { Entity = entity };
			result.Entries.Add( entry );
			if ( entity is null ) { entry.Error = "entity is null"; continue; }
			if ( string.Equals( key, "classname", StringComparison.OrdinalIgnoreCase ) )
			{
				if ( _registry != null && _registry.Find( value ) is null )
				{
					entry.Error = $"{EntityRegistry.CodeUnknownEntityClass}: Unknown entity class '{value}'.";
					continue;
				}
			}
			else if ( _registry != null )
			{
				var error = _registry.ValidateKeyValue( entity.ClassName, key, value );
				if ( error != null ) { entry.Error = $"[{error.Code}] {error.Message}"; continue; }
			}
			string old = entity.GetKeyValue( key );
			if ( string.Equals( old, value, StringComparison.Ordinal ) ) continue;
			entity.SetKeyValue( key, value );
			entry.Changed = true;
			result.Changed++;
		}
		if ( string.Equals( key, "targetname", StringComparison.OrdinalIgnoreCase ) )
			result.DuplicateTargetNames = FindDuplicateTargetNames();
		return result;
	}

	/// <summary>
	/// Find/Replace entities: match by class, target name and/or keyvalue,
	/// then replace the class, the targetname and/or the keyvalue, with a
	/// per-entity result list. Renaming a targetname also rewrites every
	/// output row targeting the old name and (registry bound) every
	/// target-typed keyvalue holding it — that is the "renames a target
	/// across outputs and updates the I/O model" case. A request with no
	/// find criterion, or with nothing to replace, is refused with
	/// <see cref="FindReplaceResult.Error"/> set and nothing changed.
	/// </summary>
	public FindReplaceResult FindReplace( FindReplaceRequest request )
	{
		var result = new FindReplaceResult();
		if ( request is null ) throw new ArgumentNullException( nameof( request ) );
		bool hasFind = !string.IsNullOrEmpty( request.FindClass )
			|| !string.IsNullOrEmpty( request.FindTargetName )
			|| !string.IsNullOrEmpty( request.FindKey );
		if ( !hasFind )
		{
			result.Error = "Find/Replace needs at least one find criterion (FindClass, FindTargetName or FindKey).";
			return result;
		}
		bool hasReplace = request.ReplaceClass != null || request.ReplaceTargetName != null
			|| request.ReplaceKey != null || request.ReplaceValue != null;
		if ( !hasReplace )
		{
			result.Error = "Find/Replace needs something to replace (ReplaceClass, ReplaceTargetName, ReplaceKey or ReplaceValue).";
			return result;
		}
		var perEntity = new Dictionary<ReportEntity, FindReplaceEntityResult>();
		FindReplaceEntityResult For( ReportEntity entity )
		{
			if ( !perEntity.TryGetValue( entity, out var r ) )
			{
				r = new FindReplaceEntityResult { Entity = entity };
				perEntity[entity] = r;
				result.PerEntity.Add( r );
			}
			return r;
		}
		void NoteChange( ReportEntity entity, string change )
		{
			var r = For( entity );
			r.Changes.Add( change );
			if ( !r.Changed ) { r.Changed = true; result.Changed++; }
		}

		var matched = new List<ReportEntity>();
		foreach ( var entity in _entities )
		{
			if ( entity is null ) continue;
			if ( !MatchesFind( entity, request ) ) continue;
			matched.Add( entity );
			For( entity ).Matched = true;
		}
		result.Matched = matched.Count;

		var renames = new List<(string Old, string New)>();
		foreach ( var entity in matched )
		{
			var r = For( entity );
			// Class first: later key validation uses the final class.
			if ( request.ReplaceClass != null
				&& !string.Equals( entity.ClassName, request.ReplaceClass, StringComparison.OrdinalIgnoreCase ) )
			{
				if ( _registry != null && _registry.Find( request.ReplaceClass ) is null )
				{
					r.Error = $"{EntityRegistry.CodeUnknownEntityClass}: Unknown entity class '{request.ReplaceClass}'.";
				}
				else
				{
					string oldClass = entity.ClassName;
					entity.ClassName = request.ReplaceClass;
					NoteChange( entity, $"class '{oldClass}' -> '{request.ReplaceClass}'" );
				}
			}
			// Targetname.
			if ( request.ReplaceTargetName != null )
			{
				string oldName = entity.TargetName;
				if ( !string.Equals( oldName, request.ReplaceTargetName, StringComparison.OrdinalIgnoreCase ) )
				{
					entity.SetKeyValue( "targetname", request.ReplaceTargetName );
					NoteChange( entity, $"targetname '{oldName}' -> '{request.ReplaceTargetName}'" );
					if ( request.RenameTargets && !string.IsNullOrEmpty( oldName ) )
						renames.Add( (oldName, request.ReplaceTargetName) );
				}
			}
			// Keyvalue: rename the key, set the value, or both.
			if ( !string.IsNullOrEmpty( request.FindKey ) && entity.HasKeyValue( request.FindKey ) )
			{
				string current = entity.GetKeyValue( request.FindKey );
				bool valueMatches = request.FindValuePattern is null
					|| MatchesPattern( current, request.FindValuePattern, request.MatchMode, request.CaseSensitive );
				if ( valueMatches )
				{
					string newKey = request.ReplaceKey ?? request.FindKey;
					string newValue = request.ReplaceValue ?? current;
					bool keyRenamed = request.ReplaceKey != null
						&& !string.Equals( request.ReplaceKey, request.FindKey, StringComparison.OrdinalIgnoreCase );
					bool valueSet = request.ReplaceValue != null
						&& !string.Equals( current, request.ReplaceValue, StringComparison.Ordinal );
					if ( keyRenamed || valueSet )
					{
						string error = ValidateReplacementKeyValue( entity, newKey, newValue );
						if ( error != null )
						{
							r.Error = error;
						}
						else
						{
							if ( keyRenamed ) entity.RemoveKeyValue( request.FindKey );
							entity.SetKeyValue( newKey, newValue );
							NoteChange( entity, keyRenamed
								? $"key '{request.FindKey}' ('{current}') -> '{newKey}' ('{newValue}')"
								: $"key '{request.FindKey}' '{current}' -> '{newValue}'" );
						}
					}
				}
			}
		}

		// Target renames rewrite references everywhere, not just on matches.
		foreach ( var (oldName, newName) in renames )
		{
			foreach ( var entity in _entities )
			{
				if ( entity is null ) continue;
				foreach ( var output in entity.Outputs )
				{
					if ( output != null && string.Equals( output.TargetName, oldName, StringComparison.OrdinalIgnoreCase ) )
					{
						output.TargetName = newName;
						NoteChange( entity, $"output '{output.Event}' target '{oldName}' -> '{newName}'" );
					}
				}
				if ( _registry != null )
				{
					foreach ( string key in entity.KeyValues.Keys.ToArray() )
					{
						if ( string.Equals( key, "targetname", StringComparison.OrdinalIgnoreCase ) ) continue;
						if ( !IsTargetKey( entity, key ) ) continue;
						if ( string.Equals( entity.KeyValues[key], oldName, StringComparison.OrdinalIgnoreCase ) )
						{
							entity.KeyValues[key] = newName;
							NoteChange( entity, $"key '{key}' '{oldName}' -> '{newName}'" );
						}
					}
				}
			}
		}

		foreach ( string duplicate in FindDuplicateTargetNames() )
			result.Warnings.Add( $"targetname '{duplicate}' is now shared by more than one entity" );
		return result;
	}

	bool MatchesFind( ReportEntity entity, FindReplaceRequest request )
	{
		if ( !string.IsNullOrEmpty( request.FindClass )
			&& !string.Equals( entity.ClassName, request.FindClass, StringComparison.OrdinalIgnoreCase ) ) return false;
		if ( !string.IsNullOrEmpty( request.FindTargetName )
			&& !MatchesPattern( entity.TargetName, request.FindTargetName, request.MatchMode, request.CaseSensitive ) ) return false;
		if ( !string.IsNullOrEmpty( request.FindKey ) )
		{
			string value = entity.GetKeyValue( request.FindKey );
			if ( value is null ) return false;
			if ( request.FindValuePattern != null
				&& !MatchesPattern( value, request.FindValuePattern, request.MatchMode, request.CaseSensitive ) ) return false;
		}
		return true;
	}

	/// <summary>Null when the replacement key/value is valid for the entity's (possibly just-renamed) class; otherwise a named error.</summary>
	string ValidateReplacementKeyValue( ReportEntity entity, string key, string value )
	{
		if ( _registry is null ) return null;
		if ( string.Equals( key, "classname", StringComparison.OrdinalIgnoreCase ) )
			return _registry.Find( value ) is null
				? $"{EntityRegistry.CodeUnknownEntityClass}: Unknown entity class '{value}'."
				: null;
		var error = _registry.ValidateKeyValue( entity.ClassName, key, value );
		return error is null ? null : $"[{error.Code}] {error.Message}";
	}

	/// <summary>True when <paramref name="key"/> is a target-typed keyvalue on the entity's class (declared or implicit base keyvalue). Needs a registry; false without one.</summary>
	bool IsTargetKey( ReportEntity entity, string key )
	{
		if ( _registry is null ) return false;
		var cls = _registry.Find( entity.ClassName );
		if ( cls is null ) return false;
		var declared = cls.FindKeyValue( key );
		if ( declared != null ) return declared.Type == KeyValueType.Target;
		return EntityRegistry.ImplicitKeyValues.TryGetValue( key, out var type ) && type == KeyValueType.Target;
	}

	/// <summary>
	/// Go to Brush Number: resolves a compiler brush number to the brush.
	/// Returns the owning entity for entity brushes, a world-brush result
	/// for registered worldspawn brushes, and Found=false (with a reason)
	/// for unknown or non-positive numbers.
	/// </summary>
	public BrushNumberLookupResult BrushNumberLookup( int brushNumber )
	{
		var result = new BrushNumberLookupResult { BrushNumber = brushNumber };
		if ( brushNumber <= 0 )
		{
			result.Reason = "Brush numbers are positive VMF ids.";
			return result;
		}
		foreach ( var entity in _entities )
		{
			if ( entity != null && entity.BrushNumbers.Contains( brushNumber ) )
			{
				result.Found = true;
				result.Entity = entity;
				return result;
			}
		}
		if ( _worldBrushes.Contains( brushNumber ) )
		{
			result.Found = true;
			result.IsWorldBrush = true;
			return result;
		}
		result.Reason = $"No brush with number {brushNumber} in this map.";
		return result;
	}

	/// <summary>Counts for the report: total, per class, duplicate targetnames, entities without a targetname.</summary>
	public EntityReportSummary Summarize()
	{
		var summary = new EntityReportSummary { Total = _entities.Count };
		foreach ( var entity in _entities )
		{
			if ( entity is null ) continue;
			string cls = string.IsNullOrEmpty( entity.ClassName ) ? "(no class)" : entity.ClassName;
			summary.ByClass[cls] = summary.ByClass.TryGetValue( cls, out int n ) ? n + 1 : 1;
			if ( string.IsNullOrEmpty( entity.TargetName ) ) summary.EntitiesWithoutTargetName++;
		}
		summary.DuplicateTargetNames = FindDuplicateTargetNames();
		return summary;
	}

	/// <summary>One-line-per-fact text rendering of <see cref="Summarize"/> (the headless form of the report list).</summary>
	public string ToText()
	{
		var summary = Summarize();
		var lines = new List<string> { $"Entity Report: {summary.Total} entities" };
		foreach ( var pair in summary.ByClass.OrderBy( p => p.Key, StringComparer.OrdinalIgnoreCase ) )
			lines.Add( $"  {pair.Key}: {pair.Value}" );
		lines.Add( summary.DuplicateTargetNames.Count == 0
			? "Duplicate targetnames: none"
			: "Duplicate targetnames: " + string.Join( ", ", summary.DuplicateTargetNames ) );
		lines.Add( $"Entities without a targetname: {summary.EntitiesWithoutTargetName}" );
		return string.Join( "\n", lines );
	}

	List<string> FindDuplicateTargetNames()
	{
		var seen = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		var duplicates = new List<string>();
		foreach ( var entity in _entities )
		{
			if ( entity is null ) continue;
			string name = entity.TargetName;
			if ( string.IsNullOrEmpty( name ) ) continue;
			if ( seen.TryGetValue( name, out string first ) )
			{
				if ( !duplicates.Contains( first, StringComparer.OrdinalIgnoreCase ) ) duplicates.Add( first );
			}
			else seen[name] = name;
		}
		return duplicates;
	}

	/// <summary>Matches <paramref name="value"/> against <paramref name="pattern"/> under <paramref name="mode"/>. A null pattern matches everything.</summary>
	public static bool MatchesPattern( string value, string pattern, EntityReportMatchMode mode, bool caseSensitive )
	{
		value ??= "";
		if ( pattern is null ) return true;
		switch ( mode )
		{
			case EntityReportMatchMode.Exact:
				return string.Equals( value, pattern, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase );
			case EntityReportMatchMode.Wildcard:
				return MatchesWildcard( value, pattern, caseSensitive );
			default:
				if ( pattern.Length == 0 ) return true;
				return value.IndexOf( pattern, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase ) >= 0;
		}
	}

	/// <summary>Wildcard match: '*' matches any run of characters, '?' matches exactly one character.</summary>
	public static bool MatchesWildcard( string value, string pattern, bool caseSensitive )
	{
		value ??= "";
		pattern ??= "";
		bool Same( char a, char b ) => caseSensitive ? a == b : char.ToUpperInvariant( a ) == char.ToUpperInvariant( b );
		int v = 0, p = 0, star = -1, mark = 0;
		while ( v < value.Length )
		{
			if ( p < pattern.Length && ( pattern[p] == '?' || Same( pattern[p], value[v] ) ) ) { v++; p++; }
			else if ( p < pattern.Length && pattern[p] == '*' ) { star = p++; mark = v; }
			else if ( star != -1 ) { p = star + 1; v = ++mark; }
			else return false;
		}
		while ( p < pattern.Length && pattern[p] == '*' ) p++;
		return p == pattern.Length;
	}
}
