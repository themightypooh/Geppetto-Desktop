// MapEntities — entity place/edit/validate API + tie/untie brushes to brush entities.
//
// Hammer Desktop P1: hammer-entity-place-edit-validate (worker muse-3-desktop-hammer-14).
// Backend / data-model / API only — no Desktop UI, no Entity Properties dialog. This is
// the API + validation a future caller (viewport, VMF I/O, MapDocument) uses.
//
// Dependencies, as landed on 2026-11-13 (read, not assumed):
//   * EntityRegistry (hammer-entity-registry-fgd-json, DONE): kernel/Effigy/Map/EntityRegistry.cs
//     + kernel/Effigy/Map/registry/core-source1.json (14 Source-1 classes). MapEntities takes
//     a real EntityRegistry and delegates every class/keyvalue decision to it: class lookup
//     (Find/Get), kind (EntityKind.Point vs Brush), and keyvalue type checks (CheckEntity /
//     CheckKeyValue / ValidateEntity). Registry rejections propagate untouched as
//     EntityRegistryException with the registry's named codes (UnknownEntityClass,
//     UnknownKeyValue, InvalidKeyValueType). A registry JSON that adds a class makes it
//     placeable here with no code change.
//   * MapBrush (hammer-brush-core-plane-based-brushes, NOT landed yet — no MapBrush.cs on
//     disk at write time): tie/untie take the brush payload as generic TBrush, so the
//     spec-shaped call compiles and runs today (PlaceBrushEntity("func_door", brushes)
//     infers TBrush from the argument). Brushes are stored by reference and returned
//     untouched by Untie, so brush data is preserved exactly. When MapBrush lands, callers
//     pass IReadOnlyList<MapBrush> with no change here; the integrator may add a
//     non-generic PlaceBrushEntity(string, IReadOnlyList<MapBrush>) convenience overload
//     that defaults origin to the brushes' centroid (today the origin defaults to
//     Vec3.Zero unless the caller passes one).
//
// Origin / angles model (follows the registry's actual contract, not Source folklore):
//   * "origin" is NOT an implicit registry keyvalue (implicit keys are classname,
//     targetname, parentname, angles, spawnflags), so it is never stored in Keyvalues —
//     it lives on MapEntity.Origin. SetKeyvalue(entity, "origin", "x y z") is a
//     convenience helper that sets the property; GetKeyvalue("origin") formats it.
//     Placement keyvalues containing "origin" are rejected by the registry as
//     UnknownKeyValue — use the position argument instead.
//   * "angles" IS an implicit registry keyvalue (Angle type), so it is mirrored into
//     Keyvalues["angles"] and validated by the registry like any other keyvalue.
//     SetAngles / SetKeyvalue("angles", ...) keep the property and the keyvalue in sync.
//   * Scale (hammer-prop-lighting-negative-scale, 2026-11-13) is Geppetto-native:
//     the registry has no scale key for the prop classes (prop_static /
//     prop_dynamic / prop_physics expose model/skin/... only), so a prop's scale
//     lives on MapEntity.Scale (default (1, 1, 1)) and is NOT a keyvalue —
//     SetKeyvalue("scales", ...) is rejected by the registry as UnknownKeyValue.
//     SetScale accepts a zero component (a zero-scale prop is a named validation
//     issue, ZeroScale, not a placement rejection); NaN/Infinity is rejected with
//     InvalidScale. A negative-determinant (mirrored) scale is legal: the lighting
//     path corrects it (see PropLighting in LightmapBake.cs), it is never silently
//     lit wrong.
//   * "classname" is fixed by placement and read-only afterwards (SetKeyvalue rejects it
//     with ReadOnlyKeyvalue); GetKeyvalue("classname") returns the canonical class name.
//
// Target names: matching is case-insensitive (Source targetnames are; EntityIo's
// ResolveTargets contract says the same). Validate() reports a missing target as an
// Error (MissingTarget) and a duplicate targetname as a Warning (DuplicateTargetname),
// never as an error — a map with a duplicate name still opens. Values starting with
// '!' are engine special targets (!self, !activator, !player, ...) and are never
// reported missing, mirroring EntityIo.
//
// Errors are named, never silent: registry problems raise EntityRegistryException
// (registry codes); this layer's problems raise MapEntityException (codes below).
// Placement never invents a default or repairs a bad value.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// Named error from the entity place/edit layer. <see cref="Code"/> is the stable
/// machine-readable name; registry-driven rejections instead raise
/// <see cref="EntityRegistryException"/> with the registry's own codes.
/// </summary>
public sealed class MapEntityException : Exception
{
	public const string CodeNotPointEntity = "NotPointEntity";
	public const string CodeNotBrushEntity = "NotBrushEntity";
	public const string CodeNoBrushes = "NoBrushes";
	public const string CodeNotTied = "NotTied";
	public const string CodeUnknownEntity = "UnknownEntity";
	public const string CodeBrushTypeMismatch = "BrushTypeMismatch";
	public const string CodeInvalidOrigin = "InvalidOrigin";
	public const string CodeInvalidAngles = "InvalidAngles";
	public const string CodeInvalidScale = "InvalidScale";
	public const string CodeReadOnlyKeyvalue = "ReadOnlyKeyvalue";

	public string Code = "";
	public string ClassName = "";
	public string KeyName = "";

	public MapEntityException( string code, string message, string className = "", string keyName = "" )
		: base( message ) { Code = code; ClassName = className; KeyName = keyName; }
}

/// <summary>Severity of one <see cref="MapEntityIssue"/> from <see cref="MapEntities.Validate"/>.</summary>
public enum MapEntityIssueSeverity
{
	Error,
	Warning
}

/// <summary>
/// One validation finding from <see cref="MapEntities.Validate"/>: a missing target
/// (error), a duplicate targetname (warning), a zero-scale prop (error), or a
/// registry keyvalue problem (error, carrying the registry's code).
/// <see cref="EntityId"/> is the placed entity's stable id, or 0 when the finding
/// is not about one entity.
/// </summary>
public sealed class MapEntityIssue
{
	public const string CodeMissingTarget = "MissingTarget";
	public const string CodeDuplicateTargetname = "DuplicateTargetname";
	public const string CodeZeroScale = "ZeroScale";

	public MapEntityIssueSeverity Severity;
	public string Code = "";
	public string Message = "";
	public int EntityId;
	public string Key = "";

	public MapEntityIssue() { }
	public MapEntityIssue( MapEntityIssueSeverity severity, string code, string message, int entityId = 0, string key = "" )
	{
		Severity = severity; Code = code; Message = message; EntityId = entityId; Key = key;
	}

	public override string ToString() => string.IsNullOrEmpty( Key )
		? $"[{Severity} {Code}] {Message}"
		: $"[{Severity} {Code}] {Message} (entity #{EntityId}, key '{Key}')";
}

/// <summary>
/// One placed entity: a point entity (origin + angles + scale, no brushes) or a
/// brush entity (tied brushes). Keyvalues are the registry-validated strings;
/// "origin" is not a keyvalue (see the file header) and lives on
/// <see cref="Origin"/>; "scales" is likewise not a keyvalue and lives on
/// <see cref="Scale"/>.
/// </summary>
public sealed class MapEntity
{
	readonly Dictionary<string, string> _keyvalues = new( StringComparer.OrdinalIgnoreCase );
	readonly List<object> _brushes = new();

	internal MapEntity( int id, EntityClass cls )
	{
		Id = id;
		ClassName = cls.Name;
		Kind = cls.Kind;
	}

	/// <summary>Stable id, assigned in placement order starting at 1, never reused.</summary>
	public int Id { get; internal set; }
	/// <summary>Canonical class name from the registry (placement normalises case).</summary>
	public string ClassName { get; internal set; }
	public EntityKind Kind { get; internal set; }
	/// <summary>True for brush-entity classes (func_door, trigger_*, ...).</summary>
	public bool IsBrushEntity => Kind == EntityKind.Brush;
	/// <summary>Placement origin (inches, Z-up). Not a keyvalue — see the file header.</summary>
	public Vec3 Origin { get; internal set; }
	/// <summary>Pitch/yaw/roll in degrees; mirrored into keyvalues["angles"].</summary>
	public Vec3 Angles { get; internal set; }
	/// <summary>Per-axis prop scale; (1, 1, 1) when unscaled. Geppetto-native, NOT a
	/// keyvalue (the registry has no scale key for prop classes). A negative
	/// determinant (an odd number of negative components) means the prop is mirrored;
	/// a zero component means the prop cannot be lit (see Validate / ZeroScale).</summary>
	public Vec3 Scale { get; internal set; } = Vec3.One;
	/// <summary>Registry-validated keyvalues (case-insensitive keys). Never contains "origin".</summary>
	public IReadOnlyDictionary<string, string> Keyvalues => _keyvalues;
	/// <summary>Tied brushes, by reference, in tie order. Empty for point entities.</summary>
	public IReadOnlyList<object> Brushes => _brushes;
	/// <summary>The entity's own targetname (keyvalue "targetname"), or "" when unnamed.</summary>
	public string TargetName => _keyvalues.TryGetValue( "targetname", out string name ) ? name : "";

	/// <summary>Tied brushes as the caller's brush type. Throws BrushTypeMismatch when the stored brushes are not all of that type.</summary>
	public IReadOnlyList<TBrush> GetBrushes<TBrush>()
	{
		var typed = new List<TBrush>( _brushes.Count );
		foreach ( object brush in _brushes )
		{
			if ( brush is TBrush match ) typed.Add( match );
			else throw new MapEntityException( MapEntityException.CodeBrushTypeMismatch,
				$"Entity #{Id} ('{ClassName}') holds a brush that is not a '{typeof( TBrush ).Name}'.", ClassName );
		}
		return typed;
	}

	internal void StoreKeyvalue( string key, string value ) => _keyvalues[key] = value;
	internal void DropKeyvalue( string key ) => _keyvalues.Remove( key );
	internal void AttachBrushes( IReadOnlyList<object> brushes )
	{
		_brushes.Clear();
		_brushes.AddRange( brushes );
	}

	public override string ToString() => $"#{Id} {ClassName} @ {MapEntities.FormatOrigin( Origin )}";
}

/// <summary>
/// Owns placed entities for one map: place point/brush entities, tie/untie brushes,
/// edit keyvalues with registry type checks, search by class/targetname, and validate
/// target references. All class/keyvalue knowledge comes from the injected
/// <see cref="EntityRegistry"/>; this class holds no class table of its own, so a
/// registry JSON addition is placeable with no code change.
/// </summary>
public sealed class MapEntities
{
	readonly EntityRegistry _registry;
	readonly List<MapEntity> _entities = new();
	int _nextId = 1;

	public MapEntities( EntityRegistry registry )
	{
		_registry = registry ?? throw new ArgumentNullException( nameof( registry ) );
	}

	public EntityRegistry Registry => _registry;
	public IReadOnlyList<MapEntity> Entities => _entities;
	public int Count => _entities.Count;

	// ---- Placement ----

	/// <summary>
	/// Place a point entity (info_player_start, light, prop_static, ...): the class must
	/// exist and be a point class; keyvalues are registry type-checked (unknown class/key
	/// or mistyped value raises EntityRegistryException with its named code). The origin
	/// is the position argument; angles default to (0, 0, 0) and are mirrored into
	/// keyvalues["angles"].
	/// </summary>
	public MapEntity PlacePointEntity( string className, Vec3 position, IReadOnlyDictionary<string, string> keyvalues = null, Vec3? angles = null )
	{
		var cls = _registry.Get( className );
		if ( cls.Kind != EntityKind.Point )
			throw new MapEntityException( MapEntityException.CodeNotPointEntity,
				$"Cannot place '{cls.Name}' as a point entity: it is a brush-entity class; use PlaceBrushEntity or Tie.", cls.Name );
		if ( keyvalues is not null ) _registry.CheckEntity( cls.Name, keyvalues );
		RequireFinite( position, MapEntityException.CodeInvalidOrigin, cls.Name );
		var entity = NewEntity( cls, keyvalues, position, angles );
		_entities.Add( entity );
		return entity;
	}

	/// <summary>
	/// Place a brush entity (func_door, trigger_multiple, ...): the class must exist and
	/// be a brush class, and at least one brush must be supplied. Brushes are stored by
	/// reference and never modified. Origin defaults to (0, 0, 0) — pass one explicitly,
	/// or compute it from the brushes once MapBrush lands (see the file header).
	/// </summary>
	public MapEntity PlaceBrushEntity<TBrush>( string className, IReadOnlyList<TBrush> brushes, IReadOnlyDictionary<string, string> keyvalues = null, Vec3? origin = null )
	{
		var cls = _registry.Get( className );
		if ( cls.Kind != EntityKind.Brush )
			throw new MapEntityException( MapEntityException.CodeNotBrushEntity,
				$"Cannot place '{cls.Name}' as a brush entity: it is a point-entity class; use PlacePointEntity.", cls.Name );
		if ( brushes is null || brushes.Count == 0 )
			throw new MapEntityException( MapEntityException.CodeNoBrushes,
				$"Cannot place '{cls.Name}' with no brushes: a brush entity needs at least one tied brush.", cls.Name );
		if ( keyvalues is not null ) _registry.CheckEntity( cls.Name, keyvalues );
		var at = origin ?? Vec3.Zero;
		RequireFinite( at, MapEntityException.CodeInvalidOrigin, cls.Name );
		var entity = NewEntity( cls, keyvalues, at, null );
		entity.AttachBrushes( brushes.Cast<object>().ToArray() );
		_entities.Add( entity );
		return entity;
	}

	/// <summary>
	/// Hammer's Tie to Entity (Ctrl+T): turn brushes into a brush entity of the given
	/// class. Same creation path as <see cref="PlaceBrushEntity{TBrush}"/>; the two names
	/// exist because the ticket's API names both.
	/// </summary>
	public MapEntity Tie<TBrush>( string className, IReadOnlyList<TBrush> brushes, IReadOnlyDictionary<string, string> keyvalues = null, Vec3? origin = null )
		=> PlaceBrushEntity( className, brushes, keyvalues, origin );

	/// <summary>
	/// Hammer's Untie to World: detach the entity's brushes and remove the entity, so
	/// the brushes return to the world. Returns the same brush references that were
	/// tied, in tie order — brush data is preserved exactly. A point entity (never
	/// tied) raises NotTied; an entity this collection does not own raises UnknownEntity.
	/// </summary>
	public IReadOnlyList<TBrush> Untie<TBrush>( MapEntity entity )
	{
		var owned = RequireOwned( entity );
		if ( owned.Brushes.Count == 0 )
			throw new MapEntityException( MapEntityException.CodeNotTied,
				$"Cannot untie entity #{owned.Id} ('{owned.ClassName}'): it has no tied brushes.", owned.ClassName );
		var brushes = owned.GetBrushes<TBrush>();
		_entities.Remove( owned );
		return brushes;
	}

	// ---- Editing ----

	/// <summary>
	/// Set one keyvalue, type-checked against the registry: an unknown key or a
	/// mistyped value raises EntityRegistryException with its named code and the stored
	/// value is left unchanged. "origin" sets <see cref="MapEntity.Origin"/> (it is not
	/// a registry keyvalue); "angles" also updates <see cref="MapEntity.Angles"/>;
	/// "classname" is fixed by placement and raises ReadOnlyKeyvalue.
	/// </summary>
	public void SetKeyvalue( MapEntity entity, string key, string value )
	{
		var owned = RequireOwned( entity );
		if ( string.Equals( key, "origin", StringComparison.OrdinalIgnoreCase ) )
		{
			SetOrigin( owned, ParseOrigin( value, owned.ClassName ) );
			return;
		}
		if ( string.Equals( key, "classname", StringComparison.OrdinalIgnoreCase ) )
			throw new MapEntityException( MapEntityException.CodeReadOnlyKeyvalue,
				$"Cannot set 'classname' on entity #{owned.Id}: the class is fixed by placement ('{owned.ClassName}'). Place a new entity instead.", owned.ClassName, "classname" );
		_registry.CheckKeyValue( owned.ClassName, key, value );
		owned.StoreKeyvalue( key, value );
		if ( string.Equals( key, "angles", StringComparison.OrdinalIgnoreCase ) )
			owned.Angles = ParseAngles( value, owned.ClassName );
	}

	/// <summary>
	/// Get one keyvalue: "origin" formats <see cref="MapEntity.Origin"/>, "classname"
	/// is the class name, a known-but-unset key returns null, and an unknown key raises
	/// EntityRegistryException with code UnknownKeyValue.
	/// </summary>
	public string GetKeyvalue( MapEntity entity, string key )
	{
		var owned = RequireOwned( entity );
		if ( string.Equals( key, "origin", StringComparison.OrdinalIgnoreCase ) ) return FormatOrigin( owned.Origin );
		if ( string.Equals( key, "classname", StringComparison.OrdinalIgnoreCase ) ) return owned.ClassName;
		if ( string.IsNullOrWhiteSpace( key ) )
			throw new EntityRegistryException( EntityRegistry.CodeUnknownKeyValue, "Keyvalue has no name.", owned.ClassName, "" );
		var cls = _registry.Get( owned.ClassName );
		bool known = cls.FindKeyValue( key ) is not null || EntityRegistry.ImplicitKeyValues.ContainsKey( key );
		if ( !known )
			throw new EntityRegistryException( EntityRegistry.CodeUnknownKeyValue,
				$"Unknown keyvalue '{key}' for entity class '{owned.ClassName}'.", owned.ClassName, key );
		return owned.Keyvalues.TryGetValue( key, out string value ) ? value : null;
	}

	/// <summary>Move an entity: updates <see cref="MapEntity.Origin"/> (not a keyvalue).</summary>
	public void SetOrigin( MapEntity entity, Vec3 origin )
	{
		var owned = RequireOwned( entity );
		RequireFinite( origin, MapEntityException.CodeInvalidOrigin, owned.ClassName );
		owned.Origin = origin;
	}

	/// <summary>Rotate an entity: updates <see cref="MapEntity.Angles"/> and keyvalues["angles"].</summary>
	public void SetAngles( MapEntity entity, Vec3 angles )
	{
		var owned = RequireOwned( entity );
		RequireFinite( angles, MapEntityException.CodeInvalidAngles, owned.ClassName );
		owned.Angles = angles;
		owned.StoreKeyvalue( "angles", FormatAngles( angles ) );
	}

	/// <summary>Scale an entity: updates <see cref="MapEntity.Scale"/> (not a keyvalue —
	/// the registry has no scale key for prop classes). A zero component is accepted
	/// here and reported by <see cref="Validate"/> as a ZeroScale error; NaN or
	/// Infinity is rejected with <see cref="MapEntityException.CodeInvalidScale"/>.</summary>
	public void SetScale( MapEntity entity, Vec3 scale )
	{
		var owned = RequireOwned( entity );
		RequireFinite( scale, MapEntityException.CodeInvalidScale, owned.ClassName );
		owned.Scale = scale;
	}

	// ---- Search / list (Entity Report) ----

	/// <summary>All entities of a class (case-insensitive). An unknown class returns an empty list.</summary>
	public IReadOnlyList<MapEntity> FindByClass( string className )
	{
		if ( string.IsNullOrWhiteSpace( className ) ) return Array.Empty<MapEntity>();
		string wanted = className.Trim();
		return _entities.Where( e => string.Equals( e.ClassName, wanted, StringComparison.OrdinalIgnoreCase ) ).ToArray();
	}

	/// <summary>All entities with this targetname (case-insensitive, matching EntityIo's target resolution). Empty/blank returns an empty list.</summary>
	public IReadOnlyList<MapEntity> FindByTargetName( string targetName )
	{
		if ( string.IsNullOrWhiteSpace( targetName ) ) return Array.Empty<MapEntity>();
		string wanted = targetName.Trim();
		return _entities.Where( e => string.Equals( e.TargetName, wanted, StringComparison.OrdinalIgnoreCase ) ).ToArray();
	}

	/// <summary>Entity by stable id, or null.</summary>
	public MapEntity FindById( int id ) => _entities.FirstOrDefault( e => e.Id == id );

	/// <summary>
	/// Remove an entity from the collection. Returns false when the entity is null or
	/// not owned. Removing a brush entity this way drops its brushes — use
	/// <see cref="Untie{TBrush}"/> to recover them.
	/// </summary>
	public bool Remove( MapEntity entity )
	{
		if ( entity is null ) return false;
		var owned = _entities.FirstOrDefault( e => e.Id == entity.Id );
		if ( owned is null ) return false;
		_entities.Remove( owned );
		return true;
	}

	// ---- Validation ----

	/// <summary>
	/// Validate the whole collection, in placement order: per entity, the registry's
	/// keyvalue problems (Error, registry code); a prop entity with a zero scale
	/// component is a ZeroScale Error naming the axis; every target-typed keyvalue
	/// (declared type Target, plus implicit "parentname") whose value names no entity
	/// is a MissingTarget Error; every targetname used by more than one entity is a
	/// DuplicateTargetname Warning on each entity beyond the first. Values starting
	/// with '!' are engine special targets and are never reported missing.
	/// </summary>
	public IReadOnlyList<MapEntityIssue> Validate()
	{
		var issues = new List<MapEntityIssue>();
		var byTargetName = new Dictionary<string, List<MapEntity>>( StringComparer.OrdinalIgnoreCase );
		var firstSeen = new List<string>();
		foreach ( var entity in _entities )
		{
			if ( entity.TargetName.Length == 0 ) continue;
			if ( !byTargetName.TryGetValue( entity.TargetName, out var group ) )
			{
				group = new List<MapEntity>();
				byTargetName[entity.TargetName] = group;
				firstSeen.Add( entity.TargetName );
			}
			group.Add( entity );
		}
		foreach ( var entity in _entities )
		{
			foreach ( var error in _registry.ValidateEntity( entity.ClassName, entity.Keyvalues ) )
				issues.Add( new MapEntityIssue( MapEntityIssueSeverity.Error, error.Code, error.Message, entity.Id, error.KeyName ) );
			if ( IsPropClass( entity.ClassName ) && HasZeroScale( entity.Scale ) )
				issues.Add( new MapEntityIssue( MapEntityIssueSeverity.Error, MapEntityIssue.CodeZeroScale,
					$"Entity #{entity.Id} ('{entity.ClassName}') has zero scale on axis {string.Join( ", ", ZeroScaleAxes( entity.Scale ) )} (scale {FormatTriple( entity.Scale )}): a zero-scale prop cannot be lit or placed — set a non-zero scale.",
					entity.Id, "scales" ) );
			var cls = _registry.Find( entity.ClassName );
			if ( cls is null ) continue;
			foreach ( var pair in entity.Keyvalues )
			{
				KeyValueType? type = cls.FindKeyValue( pair.Key )?.Type;
				if ( type is null && EntityRegistry.ImplicitKeyValues.TryGetValue( pair.Key, out var implicitType ) ) type = implicitType;
				if ( type != KeyValueType.Target ) continue;
				string target = pair.Value;
				if ( target.Length == 0 || target.StartsWith( "!", StringComparison.Ordinal ) ) continue;
				if ( !byTargetName.ContainsKey( target ) )
					issues.Add( new MapEntityIssue( MapEntityIssueSeverity.Error, MapEntityIssue.CodeMissingTarget,
						$"Entity #{entity.Id} ('{entity.ClassName}') references missing target '{target}' in key '{pair.Key}'.", entity.Id, pair.Key ) );
			}
		}
		foreach ( string name in firstSeen )
		{
			var group = byTargetName[name];
			if ( group.Count < 2 ) continue;
			for ( int i = 1; i < group.Count; i++ )
				issues.Add( new MapEntityIssue( MapEntityIssueSeverity.Warning, MapEntityIssue.CodeDuplicateTargetname,
					$"Targetname '{name}' is used by {group.Count} entities (first is #{group[0].Id}).", group[i].Id, "targetname" ) );
		}
		return issues;
	}

	// ---- Origin / angles helpers ("x y z", invariant culture, round-trippable) ----

	public static string FormatOrigin( Vec3 origin ) => FormatTriple( origin );
	public static string FormatAngles( Vec3 angles ) => FormatTriple( angles );

	static string FormatTriple( Vec3 value ) => string.Join( " ",
		value.x.ToString( "R", CultureInfo.InvariantCulture ),
		value.y.ToString( "R", CultureInfo.InvariantCulture ),
		value.z.ToString( "R", CultureInfo.InvariantCulture ) );

	public static bool TryParseOrigin( string text, out Vec3 origin ) => TryParseTriple( text, out origin );
	public static bool TryParseAngles( string text, out Vec3 angles ) => TryParseTriple( text, out angles );

	public static Vec3 ParseOrigin( string text, string className = "" )
	{
		if ( !TryParseTriple( text, out var origin ) )
			throw new MapEntityException( MapEntityException.CodeInvalidOrigin,
				$"Origin '{text}' must be three finite numbers ('x y z').", className, "origin" );
		return origin;
	}

	public static Vec3 ParseAngles( string text, string className = "" )
	{
		if ( !TryParseTriple( text, out var angles ) )
			throw new MapEntityException( MapEntityException.CodeInvalidAngles,
				$"Angles '{text}' must be three finite numbers ('pitch yaw roll').", className, "angles" );
		return angles;
	}

	static bool TryParseTriple( string text, out Vec3 value )
	{
		value = Vec3.Zero;
		if ( string.IsNullOrWhiteSpace( text ) ) return false;
		string[] parts = text.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != 3 ) return false;
		if ( !float.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x )
			|| !float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y )
			|| !float.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z ) ) return false;
		if ( !float.IsFinite( x ) || !float.IsFinite( y ) || !float.IsFinite( z ) ) return false;
		value = new Vec3( x, y, z );
		return true;
	}

	// ---- Scale helpers (hammer-prop-lighting-negative-scale) ----
	// The determinant of a prop transform is the product of its scale components
	// (a rotation has determinant +1, so it never changes the sign). The sign is
	// what the lighting path needs: negative = mirrored, zero = un-lightable.
	// These are one-line predicates, duplicated in PropLighting (LightmapBake.cs)
	// because that file must compile without this one (worker 57's harness) and
	// vice versa; keep the two copies agreeing.

	/// <summary>Determinant of the prop transform's scale part: sx * sy * sz.</summary>
	public static float PropTransformDeterminant( Vec3 scale ) => scale.x * scale.y * scale.z;

	/// <summary>True when the scale mirrors the prop (negative determinant: an odd number of negative components).</summary>
	public static bool IsMirroredScale( Vec3 scale ) => PropTransformDeterminant( scale ) < 0f;

	/// <summary>True when any scale component is exactly zero.</summary>
	public static bool HasZeroScale( Vec3 scale ) => scale.x == 0f || scale.y == 0f || scale.z == 0f;

	/// <summary>True for the prop classes (prop_static / prop_dynamic / prop_physics, case-insensitive).</summary>
	public static bool IsPropClass( string className ) =>
		!string.IsNullOrWhiteSpace( className ) && className.StartsWith( "prop_", StringComparison.OrdinalIgnoreCase );

	/// <summary>Names ("x", "y", "z") of the zero scale components, in axis order.</summary>
	public static IReadOnlyList<string> ZeroScaleAxes( Vec3 scale )
	{
		var axes = new List<string>();
		if ( scale.x == 0f ) axes.Add( "x" );
		if ( scale.y == 0f ) axes.Add( "y" );
		if ( scale.z == 0f ) axes.Add( "z" );
		return axes;
	}

	// ---- Internals ----

	MapEntity NewEntity( EntityClass cls, IReadOnlyDictionary<string, string> keyvalues, Vec3 origin, Vec3? angles )
	{
		var entity = new MapEntity( _nextId++, cls ) { Origin = origin, Angles = Vec3.Zero };
		if ( keyvalues is not null )
		{
			foreach ( var pair in keyvalues )
			{
				if ( string.Equals( pair.Key, "classname", StringComparison.OrdinalIgnoreCase ) )
				{
					if ( !string.Equals( pair.Value, cls.Name, StringComparison.OrdinalIgnoreCase ) )
						throw new MapEntityException( MapEntityException.CodeReadOnlyKeyvalue,
							$"Cannot place '{cls.Name}' with classname '{pair.Value}': the class is fixed by placement.", cls.Name, "classname" );
					continue;
				}
				entity.StoreKeyvalue( pair.Key, pair.Value );
			}
		}
		if ( angles.HasValue )
		{
			RequireFinite( angles.Value, MapEntityException.CodeInvalidAngles, cls.Name );
			entity.Angles = angles.Value;
			entity.StoreKeyvalue( "angles", FormatAngles( angles.Value ) );
		}
		else if ( entity.Keyvalues.TryGetValue( "angles", out string storedAngles ) )
		{
			entity.Angles = ParseAngles( storedAngles, cls.Name );
		}
		return entity;
	}

	MapEntity RequireOwned( MapEntity entity )
	{
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		var owned = _entities.FirstOrDefault( e => e.Id == entity.Id );
		if ( owned is null )
			throw new MapEntityException( MapEntityException.CodeUnknownEntity,
				$"Entity #{entity.Id} ('{entity.ClassName}') is not owned by this collection.", entity.ClassName );
		return owned;
	}

	static void RequireFinite( Vec3 value, string code, string className )
	{
		if ( float.IsFinite( value.x ) && float.IsFinite( value.y ) && float.IsFinite( value.z ) ) return;
		string message = code == MapEntityException.CodeInvalidOrigin
			? "Origin must be three finite numbers."
			: code == MapEntityException.CodeInvalidScale
			? "Scale must be three finite numbers."
			: "Angles must be three finite numbers.";
		throw new MapEntityException( code, message, className );
	}
}
