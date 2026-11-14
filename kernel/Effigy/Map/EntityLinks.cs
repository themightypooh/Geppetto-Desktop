// Entity link lines data (Hammer "Show Connections").
//
// Hammer Desktop P2: hammer-entity-show-connections (worker muse-3-desktop-hammer-31).
// Backend / data-model / API only — no Desktop UI, no viewport rendering. This is the
// DATA a future viewer/AI caller draws: for every I/O output + targetname reference in
// the map, link segments (source entity origin -> target entity origin) with a kind
// (output, targetname, path) and a validity (missing/ambiguous targets flagged).
//
// Source: Round-2 report §7 #12 (links MISSED) — see target/targetname links as lines
// in 3D (TrenchBroom does this better than Hammer). Pass 1 (EntityIo) has the I/O list
// model with target resolution; nothing exposed the links as geometry until now.
//
// DEPENDENCY STATUS, 2026-11-13 ~16:05 EST: EntityIo (hammer-entity-io-logic-list-model,
// worker 15) HAS landed — kernel/Effigy/Map/EntityIo.cs is on disk (rewritten 16:05 EST:
// the row types are EntityIoOutput / EntityIoInput, NOT EntityOutput / EntityInput —
// those names already belong to EntityRegistry's FGD declarations. This file builds
// directly on it (EntityIoList / EntityIoOutput / IsSpecialTarget, same case-insensitive
// targetname rules, same "!..." special-target rule). MapEntities
// (hammer-entity-place-edit-validate, worker 14) and MapDocument
// (hammer-map-document-model, worker 12) have NOT landed — neither file exists on disk
// yet — so this file does NOT reference their types and does not guess at them. Instead,
// following the MapUndo precedent (IMapUndoTarget + InMemoryMapUndoTarget), the contract
// is EntityLinkDocument: the minimal snapshot BuildLinks needs (entities with id /
// classname / origin / targetname / keyvalues, plus the EntityIoList). When MapEntities
// and MapDocument land, a thin adapter converts their entity model into an
// EntityLinkDocument (FromStates already does this for the landed MapEntityState);
// the segments, kinds, validity rules and tests do not move.
//
// Geometry convention: kernel inches, Z-up (CONTRACTS.md). Origins are Vec3, the
// kernel's own vector type — no engine type, no UI type.
//
// Link rules (deterministic order: outputs first in list order, then targetname links
// in entity order, then path links in chain order):
//   * Output: one segment per enabled EntityIoOutput row. The source is the entity whose
//     targetname matches the row's SourceEntity (EntityIo: the source's targetname, or
//     "#" + id when unnamed), falling back to a bare entity id match. A row whose
//     source entity is not in the map has no origin to draw from, so it yields NO
//     segment; it is reported instead in EntityLinkSet.UnresolvedSources. Disabled
//     rows never fire, so they yield no segment either.
//   * TargetName: one segment per entity "target" keyvalue (the general targetname
//     reference: trigger targets, path targets, ...). The source is that entity.
//   * Path: a path_track / path_corner entity's "target" is a Path link, not a
//     TargetName link — the chain edges, in order, with PathOrder 0, 1, 2, ... from the
//     chain head and ChainHead naming the head. Loops terminate: each edge is emitted
//     once, the walk stops when it would revisit a node.
//   * Validity: exactly one matching entity -> Valid. No match -> Missing, and the
//     segment is degenerate (End == Start, at the source origin): a missing target has
//     no endpoint to draw, but the segment still exists so the caller can flag it
//     (Hammer's red target). Several matches -> Ambiguous, one segment per matching
//     entity (the engine fires all of them; Hammer's bold case). Engine special
//     targets ("!self", "!activator", "!player", ... — any name starting with '!') are
//     never missing or ambiguous (EntityIo.IsSpecialTarget): they yield one degenerate
//     Valid segment at the source origin ("!self" targets the source itself).
//   * No deduping across kinds: an output to X and a "target" keyvalue to X are two
//     different links. Within one kind, rows/keyvalues are preserved as written.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Effigy;

/// <summary>What kind of reference a link segment draws.</summary>
public enum EntityLinkKind
{
	/// <summary>An EntityIo output row: source event -> target input.</summary>
	Output,
	/// <summary>An entity "target" keyvalue pointing at a targetname.</summary>
	TargetName,
	/// <summary>A path_track / path_corner "target": one edge of a path chain, in order.</summary>
	Path,
}

/// <summary>Whether a link's target resolved. Missing is Hammer's red target; Ambiguous is the bold case.</summary>
public enum EntityLinkValidity
{
	/// <summary>Exactly one entity has the target name (or the target is a special "!..." target).</summary>
	Valid,
	/// <summary>No entity has the target name.</summary>
	Missing,
	/// <summary>Several entities share the target name; the engine fires all of them.</summary>
	Ambiguous,
}

/// <summary>
/// One entity as BuildLinks needs to see it: a stable id, a classname, an origin
/// (kernel inches, Z-up) and its keyvalues. TargetName is the "targetname" keyvalue
/// ("" when unnamed); <see cref="Target"/> is the "target" keyvalue ("" when none).
/// This is deliberately MapEntities-agnostic: MapEntities, MapDocument and the landed
/// MapEntityState (see <see cref="FromState"/>) all adapt into this shape.
/// </summary>
public sealed class EntityLinkEntity
{
	public string Id { get; }
	public string ClassName { get; }
	public Vec3 Origin { get; }
	public string TargetName { get; }
	public IReadOnlyDictionary<string, string> Properties { get; }

	public EntityLinkEntity( string id, string className, Vec3 origin, string targetName = "",
		IReadOnlyDictionary<string, string>? properties = null )
	{
		if ( string.IsNullOrWhiteSpace( id ) ) throw new ArgumentException( "entity id is empty.", nameof( id ) );
		if ( string.IsNullOrWhiteSpace( className ) ) throw new ArgumentException( "entity classname is empty.", nameof( className ) );
		if ( !float.IsFinite( origin.x ) || !float.IsFinite( origin.y ) || !float.IsFinite( origin.z ) )
			throw new ArgumentException( $"entity '{id}' origin is not finite: {origin}.", nameof( origin ) );
		Id = id;
		ClassName = className;
		Origin = origin;
		TargetName = targetName ?? "";
		var props = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		if ( properties is not null )
			foreach ( var pair in properties )
				props[pair.Key] = pair.Value ?? "";
		if ( TargetName.Length > 0 ) props["targetname"] = TargetName;
		else if ( props.TryGetValue( "targetname", out string? named ) ) TargetName = named ?? "";
		Properties = props;
	}

	/// <summary>The "target" keyvalue (the targetname this entity points at), or "" when it has none.</summary>
	public string Target => TryGetProperty( "target", out string? target ) ? target ?? "" : "";

	/// <summary>True for path_track / path_corner: their "target" is a Path link, not a TargetName link.</summary>
	public bool IsPathNode => EntityLinks.IsPathNodeClass( ClassName );

	public bool TryGetProperty( string name, out string? value ) => Properties.TryGetValue( name, out value );

	/// <summary>Adapts the landed MapEntityState (MapUndo.cs) into this shape.</summary>
	public static EntityLinkEntity FromState( MapEntityState state )
	{
		if ( state is null ) throw new ArgumentNullException( nameof( state ) );
		string targetName = "";
		if ( state.Properties is not null && state.Properties.TryGetValue( "targetname", out string? named ) )
			targetName = named ?? "";
		return new EntityLinkEntity( state.Id, state.ClassName, state.Origin, targetName, state.Properties );
	}
}

/// <summary>
/// One link line: source entity origin -> target entity origin, with kind + validity.
/// A future viewer draws Start -> End; a missing/special target is degenerate
/// (End == Start) because it has no endpoint, and <see cref="IsDegenerate"/> says so.
/// </summary>
public sealed class EntityLinkSegment
{
	public string SourceId { get; }
	public string SourceName { get; }
	public string SourceClass { get; }
	public string TargetId { get; }
	public string TargetName { get; }
	public Vec3 Start { get; }
	public Vec3 End { get; }
	public EntityLinkKind Kind { get; }
	public EntityLinkValidity Validity { get; }
	public string Event { get; }
	public string Input { get; }
	public string Parameter { get; }
	public double DelaySeconds { get; }
	public int MaxFires { get; }
	/// <summary>0-based edge index within a path chain; -1 for non-path links.</summary>
	public int PathOrder { get; }
	/// <summary>Chain head (targetname, else id) for path links; "" for non-path links.</summary>
	public string ChainHead { get; }

	public EntityLinkSegment( string sourceId, string sourceName, string sourceClass,
		string targetId, string targetName, Vec3 start, Vec3 end, EntityLinkKind kind,
		EntityLinkValidity validity, string @event = "", string input = "", string parameter = "",
		double delaySeconds = 0, int maxFires = -1, int pathOrder = -1, string chainHead = "" )
	{
		if ( string.IsNullOrWhiteSpace( sourceId ) ) throw new ArgumentException( "link source id is empty.", nameof( sourceId ) );
		if ( !float.IsFinite( start.x ) || !float.IsFinite( start.y ) || !float.IsFinite( start.z ) )
			throw new ArgumentException( $"link start is not finite: {start}.", nameof( start ) );
		if ( !float.IsFinite( end.x ) || !float.IsFinite( end.y ) || !float.IsFinite( end.z ) )
			throw new ArgumentException( $"link end is not finite: {end}.", nameof( end ) );
		SourceId = sourceId;
		SourceName = sourceName ?? "";
		SourceClass = sourceClass ?? "";
		TargetId = targetId ?? "";
		TargetName = targetName ?? "";
		Start = start;
		End = end;
		Kind = kind;
		Validity = validity;
		Event = @event ?? "";
		Input = input ?? "";
		Parameter = parameter ?? "";
		DelaySeconds = delaySeconds;
		MaxFires = maxFires;
		PathOrder = pathOrder;
		ChainHead = chainHead ?? "";
	}

	/// <summary>Segment length in kernel inches (0 for a degenerate missing/special segment).</summary>
	public float Length => ( End - Start ).Length;

	/// <summary>True when the segment has no drawable extent (missing or special target).</summary>
	public bool IsDegenerate => Length <= 1e-6f;

	public bool IsValid => Validity == EntityLinkValidity.Valid;

	public override string ToString() =>
		$"{Kind} {SourceName} --{Event}/{Input}--> {TargetName} [{Validity}] {Start}->{End}";
}

/// <summary>
/// Everything <see cref="EntityLinks.BuildLinks"/> found: all segments plus the stable
/// queries (links for one entity, all links) and the validity tallies.
/// </summary>
public sealed class EntityLinkSet
{
	public IReadOnlyList<EntityLinkSegment> Links { get; }
	/// <summary>Output rows whose source entity is not in the map: no origin, so no segment. Not a target problem.</summary>
	public IReadOnlyList<EntityIoOutput> UnresolvedSources { get; }

	public EntityLinkSet( IReadOnlyList<EntityLinkSegment> links, IReadOnlyList<EntityIoOutput>? unresolvedSources = null )
	{
		Links = links is null ? throw new ArgumentNullException( nameof( links ) ) : links.ToArray();
		UnresolvedSources = unresolvedSources is null ? Array.Empty<EntityIoOutput>() : unresolvedSources.ToArray();
	}

	public int Count => Links.Count;
	public int ValidCount => Links.Count( l => l.Validity == EntityLinkValidity.Valid );
	public int MissingCount => Links.Count( l => l.Validity == EntityLinkValidity.Missing );
	public int AmbiguousCount => Links.Count( l => l.Validity == EntityLinkValidity.Ambiguous );
	public bool HasProblems => MissingCount > 0 || AmbiguousCount > 0;
	public bool IsClean => !HasProblems;
	public bool HasUnresolvedSources => UnresolvedSources.Count > 0;

	/// <summary>Distinct target names flagged missing, sorted case-insensitively.</summary>
	public IReadOnlyList<string> MissingTargets => Links
		.Where( l => l.Validity == EntityLinkValidity.Missing )
		.Select( l => l.TargetName ).Distinct( StringComparer.OrdinalIgnoreCase )
		.OrderBy( n => n, StringComparer.OrdinalIgnoreCase ).ToList();

	/// <summary>Distinct target names flagged ambiguous, sorted case-insensitively.</summary>
	public IReadOnlyList<string> AmbiguousTargets => Links
		.Where( l => l.Validity == EntityLinkValidity.Ambiguous )
		.Select( l => l.TargetName ).Distinct( StringComparer.OrdinalIgnoreCase )
		.OrderBy( n => n, StringComparer.OrdinalIgnoreCase ).ToList();

	static bool Matches( EntityLinkSegment link, string name ) =>
		string.Equals( link.SourceId, name, StringComparison.OrdinalIgnoreCase )
		|| string.Equals( link.TargetId, name, StringComparison.OrdinalIgnoreCase )
		|| ( link.SourceName.Length > 0 && string.Equals( link.SourceName, name, StringComparison.OrdinalIgnoreCase ) )
		|| ( link.TargetName.Length > 0 && string.Equals( link.TargetName, name, StringComparison.OrdinalIgnoreCase ) );

	/// <summary>Every segment touching one entity, by id or by targetname (either end). Empty when the name matches nothing.</summary>
	public IReadOnlyList<EntityLinkSegment> LinksFor( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return Array.Empty<EntityLinkSegment>();
		return Links.Where( l => Matches( l, name ) ).ToList();
	}

	/// <summary>Every segment starting at one entity, by id or by targetname.</summary>
	public IReadOnlyList<EntityLinkSegment> LinksFrom( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return Array.Empty<EntityLinkSegment>();
		return Links.Where( l =>
			string.Equals( l.SourceId, name, StringComparison.OrdinalIgnoreCase )
			|| ( l.SourceName.Length > 0 && string.Equals( l.SourceName, name, StringComparison.OrdinalIgnoreCase ) ) ).ToList();
	}

	/// <summary>Every segment ending at one entity, by id or by targetname.</summary>
	public IReadOnlyList<EntityLinkSegment> LinksTo( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return Array.Empty<EntityLinkSegment>();
		return Links.Where( l =>
			string.Equals( l.TargetId, name, StringComparison.OrdinalIgnoreCase )
			|| ( l.TargetName.Length > 0 && string.Equals( l.TargetName, name, StringComparison.OrdinalIgnoreCase ) ) ).ToList();
	}

	/// <summary>Every segment of one kind, in build order.</summary>
	public IReadOnlyList<EntityLinkSegment> LinksOfKind( EntityLinkKind kind ) => Links.Where( l => l.Kind == kind ).ToList();
}

/// <summary>
/// The input BuildLinks needs: the map's entities plus its I/O list. Versioned JSON
/// (version 1) so fixtures can carry a whole link scenario in one file:
/// { "version": 1, "entities": [{ "id", "classname", "origin": [x,y,z],
/// "targetname", "properties": {"target": "..."} }], "entity_io": { ...EntityIoList JSON... } }.
/// The "entity_io" section is exactly EntityIoList.ToJson() / FromJson().
/// </summary>
public sealed class EntityLinkDocument
{
	public IReadOnlyList<EntityLinkEntity> Entities { get; }
	public EntityIoList Io { get; }

	public EntityLinkDocument( IReadOnlyList<EntityLinkEntity> entities, EntityIoList? io = null )
	{
		if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
		Entities = entities.ToArray();
		Io = io ?? new EntityIoList();
	}

	/// <summary>Entities carrying this targetname, in map order (more than one = ambiguous).</summary>
	public IReadOnlyList<EntityLinkEntity> FindByTargetName( string targetName )
	{
		if ( string.IsNullOrWhiteSpace( targetName ) ) return Array.Empty<EntityLinkEntity>();
		return Entities.Where( e => string.Equals( e.TargetName, targetName, StringComparison.OrdinalIgnoreCase ) ).ToList();
	}

	/// <summary>The entity with this stable id, or null.</summary>
	public EntityLinkEntity? FindById( string id )
	{
		if ( string.IsNullOrWhiteSpace( id ) ) return null;
		return Entities.FirstOrDefault( e => string.Equals( e.Id, id, StringComparison.OrdinalIgnoreCase ) );
	}

	/// <summary>Builds a document from landed MapEntityState snapshots plus an I/O list.</summary>
	public static EntityLinkDocument FromStates( IEnumerable<MapEntityState> states, EntityIoList? io = null )
	{
		if ( states is null ) throw new ArgumentNullException( nameof( states ) );
		return new EntityLinkDocument( states.Select( EntityLinkEntity.FromState ).ToList(), io );
	}

	// ---- JSON (version 1) ----

	sealed class EntityJson
	{
		public string id { get; set; } = "";
		public string classname { get; set; } = "";
		public float[] origin { get; set; } = Array.Empty<float>();
		public string targetname { get; set; } = "";
		public Dictionary<string, string> properties { get; set; } = new();
	}

	sealed class DocumentJson
	{
		public int version { get; set; } = 1;
		public List<EntityJson> entities { get; set; } = new();
		public JsonElement entity_io { get; set; }
	}

	/// <summary>Serialises this document (entities + the entity_io section) as version-1 JSON.</summary>
	public string ToJson()
	{
		var doc = new DocumentJson
		{
			entities = Entities.Select( e => new EntityJson
			{
				id = e.Id,
				classname = e.ClassName,
				origin = new[] { e.Origin.x, e.Origin.y, e.Origin.z },
				targetname = e.TargetName,
				properties = new Dictionary<string, string>( e.Properties, StringComparer.OrdinalIgnoreCase ),
			} ).ToList(),
			entity_io = JsonSerializer.Deserialize<JsonElement>( Io.ToJson() ),
		};
		return JsonSerializer.Serialize( doc, new JsonSerializerOptions { WriteIndented = true } );
	}

	/// <summary>Reads back <see cref="ToJson"/> output. Throws <see cref="ArgumentException"/> naming the bad field.</summary>
	public static EntityLinkDocument FromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) throw new ArgumentException( "entity-link document JSON is empty.", nameof( json ) );
		DocumentJson? doc;
		try { doc = JsonSerializer.Deserialize<DocumentJson>( json ); }
		catch ( JsonException ex ) { throw new ArgumentException( $"entity-link document JSON is not valid JSON: {ex.Message}", nameof( json ), ex ); }
		if ( doc is null ) throw new ArgumentException( "entity-link document JSON is empty.", nameof( json ) );
		if ( doc.version != 1 ) throw new ArgumentException( $"unsupported entity-link document version {doc.version}; expected 1.", nameof( json ) );
		var entities = new List<EntityLinkEntity>();
		foreach ( EntityJson row in doc.entities ?? new List<EntityJson>() )
		{
			if ( row.origin is null || row.origin.Length != 3 )
				throw new ArgumentException( $"entity-link entity '{row.id}' origin must have exactly 3 numbers.", nameof( json ) );
			try
			{
				entities.Add( new EntityLinkEntity( row.id, row.classname,
					new Vec3( row.origin[0], row.origin[1], row.origin[2] ), row.targetname ?? "", row.properties ) );
			}
			catch ( ArgumentException ex )
			{
				throw new ArgumentException( $"invalid entity-link entity '{row.id}': {ex.Message}", nameof( json ), ex );
			}
		}
		EntityIoList io = new EntityIoList();
		if ( doc.entity_io.ValueKind == JsonValueKind.Object )
			io = EntityIoList.FromJson( doc.entity_io.GetRawText() );
		return new EntityLinkDocument( entities, io );
	}
}

/// <summary>
/// Show Connections: link-line data for a map document. Pure data — no rendering,
/// no UI. See the file header for the link rules.
/// </summary>
public static class EntityLinks
{
	/// <summary>True for the path-node classes whose "target" is a Path link: path_track, path_corner.</summary>
	public static bool IsPathNodeClass( string className ) =>
		className is not null && ( string.Equals( className, "path_track", StringComparison.OrdinalIgnoreCase )
			|| string.Equals( className, "path_corner", StringComparison.OrdinalIgnoreCase ) );

	/// <summary>Builds every link segment for a document: outputs, then targetname links, then path chains.</summary>
	public static EntityLinkSet BuildLinks( EntityLinkDocument document )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		return BuildLinks( document.Entities, document.Io );
	}

	/// <summary>Builds every link segment from an entity list plus an I/O list.</summary>
	public static EntityLinkSet BuildLinks( IReadOnlyList<EntityLinkEntity> entities, EntityIoList io )
	{
		if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
		if ( io is null ) throw new ArgumentNullException( nameof( io ) );

		var byTargetName = new Dictionary<string, List<EntityLinkEntity>>( StringComparer.OrdinalIgnoreCase );
		var byId = new Dictionary<string, EntityLinkEntity>( StringComparer.OrdinalIgnoreCase );
		foreach ( EntityLinkEntity entity in entities )
		{
			if ( entity.TargetName.Length > 0 )
			{
				if ( !byTargetName.TryGetValue( entity.TargetName, out List<EntityLinkEntity>? list ) )
					byTargetName[entity.TargetName] = list = new List<EntityLinkEntity>();
				list.Add( entity );
			}
			if ( !byId.ContainsKey( entity.Id ) ) byId[entity.Id] = entity;
		}

		IReadOnlyList<EntityLinkEntity> ResolveTargets( string targetName ) =>
			byTargetName.TryGetValue( targetName, out List<EntityLinkEntity>? list ) ? list : Array.Empty<EntityLinkEntity>();

		EntityLinkEntity? ResolveSource( string sourceEntity )
		{
			if ( byTargetName.TryGetValue( sourceEntity, out List<EntityLinkEntity>? list ) && list.Count > 0 ) return list[0];
			// Unnamed sources are keyed "#" + id (EntityIo convention); a bare id also resolves.
			string idKey = sourceEntity.StartsWith( "#", StringComparison.Ordinal ) ? sourceEntity.Substring( 1 ) : sourceEntity;
			return byId.TryGetValue( idKey, out EntityLinkEntity? byIdEntity ) ? byIdEntity : null;
		}

		var links = new List<EntityLinkSegment>();
		var unresolvedSources = new List<EntityIoOutput>();

		// ---- Outputs (EntityIo rows, in list order; disabled rows never fire) ----
		foreach ( EntityIoOutput output in io.Outputs )
		{
			if ( !output.Enabled ) continue;
			EntityLinkEntity? source = ResolveSource( output.SourceEntity );
			if ( source is null ) { unresolvedSources.Add( output ); continue; }
			links.AddRange( SegmentsForReference( source, output.TargetName, EntityLinkKind.Output,
				ResolveTargets, output.Event, output.Input, output.Parameter, output.DelaySeconds, output.MaxFires ) );
		}

		// ---- TargetName links (entity "target" keyvalues, except path nodes — see below) ----
		foreach ( EntityLinkEntity entity in entities )
		{
			if ( entity.IsPathNode ) continue;
			string target = entity.Target;
			if ( string.IsNullOrWhiteSpace( target ) ) continue;
			links.AddRange( SegmentsForReference( entity, target, EntityLinkKind.TargetName, ResolveTargets ) );
		}

		// ---- Path links (path_track / path_corner chains, in order; loops terminate) ----
		links.AddRange( BuildPathLinks( entities, ResolveTargets ) );

		return new EntityLinkSet( links, unresolvedSources );
	}

	/// <summary>
	/// Segments for one targetname reference from one source entity: one Valid segment
	/// per matching entity, one degenerate Missing segment when nothing matches, one
	/// degenerate Valid segment for a special "!..." target.
	/// </summary>
	static IReadOnlyList<EntityLinkSegment> SegmentsForReference( EntityLinkEntity source, string targetName,
		EntityLinkKind kind, Func<string, IReadOnlyList<EntityLinkEntity>> resolveTargets,
		string @event = "", string input = "", string parameter = "",
		double delaySeconds = 0, int maxFires = -1, int pathOrder = -1, string chainHead = "" )
	{
		string sourceName = source.TargetName.Length > 0 ? source.TargetName : source.Id;
		if ( EntityIoList.IsSpecialTarget( targetName ) )
		{
			// "!self" is the source itself; other "!..." targets have no fixed point.
			// Either way they resolve by definition: one degenerate Valid segment.
			string targetId = string.Equals( targetName, "!self", StringComparison.OrdinalIgnoreCase ) ? source.Id : "";
			return new[]
			{
				new EntityLinkSegment( source.Id, sourceName, source.ClassName, targetId, targetName,
					source.Origin, source.Origin, kind, EntityLinkValidity.Valid,
					@event, input, parameter, delaySeconds, maxFires, pathOrder, chainHead ),
			};
		}
		IReadOnlyList<EntityLinkEntity> matches = resolveTargets( targetName );
		if ( matches.Count == 0 )
			return new[]
			{
				new EntityLinkSegment( source.Id, sourceName, source.ClassName, "", targetName,
					source.Origin, source.Origin, kind, EntityLinkValidity.Missing,
					@event, input, parameter, delaySeconds, maxFires, pathOrder, chainHead ),
			};
		var segments = new List<EntityLinkSegment>();
		foreach ( EntityLinkEntity match in matches )
		{
			var validity = matches.Count == 1 ? EntityLinkValidity.Valid : EntityLinkValidity.Ambiguous;
			segments.Add( new EntityLinkSegment( source.Id, sourceName, source.ClassName, match.Id, targetName,
				source.Origin, match.Origin, kind, validity,
				@event, input, parameter, delaySeconds, maxFires, pathOrder, chainHead ) );
		}
		return segments;
	}

	static IReadOnlyList<EntityLinkSegment> BuildPathLinks( IReadOnlyList<EntityLinkEntity> entities,
		Func<string, IReadOnlyList<EntityLinkEntity>> resolveTargets )
	{
		var nodes = entities.Where( e => e.IsPathNode ).ToList();
		if ( nodes.Count == 0 ) return Array.Empty<EntityLinkSegment>();

		// A node is a chain head when no other path node targets it (by targetname or id).
		var targeted = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( EntityLinkEntity node in nodes )
			if ( !string.IsNullOrWhiteSpace( node.Target ) ) targeted.Add( node.Target );
		var heads = nodes.Where( n =>
			( n.TargetName.Length == 0 || !targeted.Contains( n.TargetName ) ) && !targeted.Contains( n.Id ) ).ToList();

		var segments = new List<EntityLinkSegment>();
		var covered = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		// Walk one chain from its head (or from an unheaded node: a pure loop has no head).
		void WalkChain( EntityLinkEntity head )
		{
			string headKey = head.TargetName.Length > 0 ? head.TargetName : head.Id;
			var visited = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
			EntityLinkEntity current = head;
			int order = 0;
			while ( true )
			{
				if ( !visited.Add( current.Id ) ) return;
				covered.Add( current.Id );
				string target = current.Target;
				if ( string.IsNullOrWhiteSpace( target ) ) return;
				if ( EntityIoList.IsSpecialTarget( target ) )
				{
					segments.AddRange( SegmentsForReference( current, target, EntityLinkKind.Path, resolveTargets,
						pathOrder: order, chainHead: headKey ) );
					return;
				}
				IReadOnlyList<EntityLinkEntity> matches = resolveTargets( target );
				if ( matches.Count == 0 )
				{
					segments.AddRange( SegmentsForReference( current, target, EntityLinkKind.Path, resolveTargets,
						pathOrder: order, chainHead: headKey ) );
					return;
				}
				segments.AddRange( SegmentsForReference( current, target, EntityLinkKind.Path, resolveTargets,
					pathOrder: order, chainHead: headKey ) );
				EntityLinkEntity next = matches[0];
				if ( !next.IsPathNode || visited.Contains( next.Id ) ) return;
				current = next;
				order++;
			}
		}

		foreach ( EntityLinkEntity head in heads ) WalkChain( head );
		// Pure loops (and chains whose head was already covered as a mid-chain node) have
		// no head to walk from: start a walk at each uncovered node that has a target.
		foreach ( EntityLinkEntity node in nodes )
			if ( !covered.Contains( node.Id ) && !string.IsNullOrWhiteSpace( node.Target ) ) WalkChain( node );

		return segments;
	}
}
