// Entity I/O node-graph data model + entity scripting hooks (Hammer P3).
//
// Hammer Desktop P3: hammer-io-node-graph-scripting-hooks (worker muse-3-desktop-hammer-78).
// Backend / data-model / API only — no Desktop UI, no node editor. The graph UI is
// deferred; this file is the data model the future graph view edits, plus the
// script-hook registry that the future Lua/VScript view edits.
//
// Source: Report §1.4 + §6 P3-2 — Source 2 joins the I/O list with Lua/VScript
// entity scripts; Geppetto's equivalent is a node/graph view PLUS the simple list.
// Both views edit the same data: this graph converts losslessly to and from the
// list model (kernel/Effigy/Map/EntityIo.cs, worker 15).
//
// Mapping (list <-> graph):
//   * list row (EntityIoOutput)      -> one graph EDGE, output -> input with delay.
//   * entity (source or target name) -> one ENTITY node.
//   * event (e.g. OnPressed)         -> one EVENT node owned by its entity.
//   * input (e.g. Open)             -> one INPUT node owned by its entity.
//   * an "output" in the list sense is the edge itself (event node -> input node,
//     carrying delay / max fires / parameter / enabled), not a fourth node kind.
//     Node ownership (Node.Entity) is what ties ports to their entity; there are
//     no containment edges. Edge endpoints must be Event -> Input, enforced by
//     Validate() and ToList().
//   * logic_relay equivalents        -> one SUB-GRAPH per relay entity, grouping
//     the relay's entity node + its event/input nodes, with the Trigger input node
//     as entry and the OnTrigger event node as exit. Sub-graphs are groupings, not
//     containers: edges crossing the boundary stay ordinary edges, so collapsing a
//     relay never loses a connection.
//   * layout (Node.X/Y) is graph-side metadata only. It is preserved by the graph
//     JSON round-trip but is NOT part of the list model: list -> graph -> list is
//     lossless for I/O rows, while list -> graph -> JSON -> graph preserves layout
//     too. FromList() assigns a deterministic layered layout (depth * 260,
//     order * 110) so a graph built from a list is stable.
//
// Script hooks (EntityScriptHookRegistry):
//   * A hook is a named script entry point on one entity (e.g. button_01 OnPressed
//     runs Lua). Language is Lua only; "vscript" is accepted as Source's name for
//     the same Lua runtime and normalised to "lua". Anything else is rejected:
//     entity scripts are never a shell.
//   * Safety rules mirror McpSafety.SafetyGates (Marionette.Desktop, the landed
//     mcp-cli-security-safety-gates layer) — the kernel cannot reference that
//     app-level class, so the same ruleset is implemented here, not a second
//     safety model. hammer-ai-safety-gates-map (worker 68, in progress) extends
//     McpSafety.cs / GateCheck.cs for map AI tools; this registry applies the
//     same gates to stored entity scripts:
//       - no shell/exec: os.execute / os.remove / os.rename / os.exit / io.popen /
//         loadfile / dofile / loadstring / require("os"|"io") / shell_exec /
//         run_command / run_shell are rejected (the McpSafety ForbiddenTools
//         names, in their Lua host-call forms).
//       - destructive operations (kill / remove / delete / destroy / explode /
//         overwrite, mirroring McpSafety's destructive-tool gate) require the
//         explicit allow_destructive flag on the hook; without it the hook is
//         rejected, with it the acceptance is written to the audit log.
//       - secrets are never stored: a source containing a secret assignment or a
//         known secret shape (sk- keys, ghp_/github_pat_ tokens, AKIA keys,
//         Bearer tokens — the McpSafety shapes) is rejected, and the rejection
//         names the shape only, never the value.
//       - every Add (accept, replace, or reject) and Remove is appended to the
//         in-memory AuditLog with a UTC timestamp; the log never contains a
//         secret value. Source size is capped at MaxSourceChars (262144, mirroring
//         SafetyPolicy.MaxArgsBytes).
//   * Hooks are NOT executed by this file: there is no Lua runtime in the kernel
//     and no script ever runs here. Registration + validation + serialisation only.
//     Execution is a future ticket, behind the same gates.
//   * Unlike EntityIoOutput (whose ctor throws), EntityScriptHook's ctor does NOT
//     throw: the safety gate must report a named reason, so validation lives in
//     ValidateHook()/Add(), which return the reason string (null = accepted).
//
// Serialisation: graph JSON version 1 ("entity_io_graph" shape, nodes / edges /
// subgraphs / scripts). MapDocument embeds it verbatim alongside the list
// model's "entity_io" section; both describe the same connections.
//
// Seams (sibling workers own the other side):
//   * EntityIoList (hammer-entity-io-logic-list-model, worker 15) — DONE, landed.
//     Row types are EntityIoOutput / EntityIoInput (NOT EntityOutput /
//     EntityInput, which belong to EntityRegistry's FGD declarations).
//   * MapEntities (hammer-entity-place-edit-validate) — MapEntity.TargetName /
//     FindByTargetName can back FromList's classNameByEntity resolver.
//   * MapDocument (hammer-map-document-model) — embeds ToJson() verbatim.
//   * VmfIo (hammer-vmf-map-import-export) — the list model owns VMF lines; the
//     graph converts through the list, never directly to VMF.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Effigy;

namespace Effigy.Map;

/// <summary>What kind of port a graph node is. An output row of the list model is an edge, not a node (see the file header).</summary>
public enum EntityIoNodeKind
{
	Entity,
	Event,
	Input,
}

/// <summary>
/// One node of the I/O graph: an entity, one of its events (output port), or one
/// of its inputs (input port). <see cref="Id"/> is deterministic:
/// "entity:&lt;name&gt;", "event:&lt;entity&gt;:&lt;event&gt;",
/// "input:&lt;entity&gt;:&lt;input&gt;". <see cref="X"/>/<see cref="Y"/> are layout
/// metadata for the future graph view and are not part of the list model.
/// </summary>
public sealed class EntityIoNode : IEquatable<EntityIoNode>
{
	public string Id { get; set; }
	public EntityIoNodeKind Kind { get; set; }
	public string Entity { get; set; }
	public string Port { get; set; }
	public string Label { get; set; }
	public double X { get; set; }
	public double Y { get; set; }

	public EntityIoNode( string id, EntityIoNodeKind kind, string entity, string port = "", string label = "", double x = 0, double y = 0 )
	{
		Id = id ?? "";
		Kind = kind;
		Entity = entity ?? "";
		Port = port ?? "";
		Label = label ?? "";
		X = x;
		Y = y;
	}

	/// <summary>Null when this node is valid, otherwise a named error.</summary>
	public string Validate()
	{
		if ( string.IsNullOrWhiteSpace( Id ) ) return "node id is empty";
		if ( string.IsNullOrWhiteSpace( Entity ) ) return $"node '{Id}': entity is empty";
		if ( Kind == EntityIoNodeKind.Entity && !string.IsNullOrEmpty( Port ) ) return $"node '{Id}': an entity node must not have a port";
		if ( Kind != EntityIoNodeKind.Entity && string.IsNullOrWhiteSpace( Port ) ) return $"node '{Id}': a port node needs a port name";
		if ( !double.IsFinite( X ) || !double.IsFinite( Y ) ) return $"node '{Id}': layout position must be finite";
		return null;
	}

	public EntityIoNode Copy() => new( Id, Kind, Entity, Port, Label, X, Y );

	public bool Equals( EntityIoNode other ) => other is not null
		&& Id == other.Id && Kind == other.Kind && Entity == other.Entity && Port == other.Port
		&& Label == other.Label && X.Equals( other.X ) && Y.Equals( other.Y );

	public override bool Equals( object obj ) => Equals( obj as EntityIoNode );

	public override int GetHashCode() => HashCode.Combine( Id, Kind, Entity, Port, Label, X, Y );
}

/// <summary>
/// One edge of the I/O graph: one output row of the list model — when the
/// <see cref="FromNodeId"/> event fires, send the <see cref="ToNodeId"/> input
/// after <see cref="DelaySeconds"/> seconds, at most <see cref="MaxFires"/> times
/// (-1 = forever). From must be an Event node, To must be an Input node.
/// </summary>
public sealed class EntityIoEdge : IEquatable<EntityIoEdge>
{
	public string Id { get; set; }
	public string FromNodeId { get; set; }
	public string ToNodeId { get; set; }
	public double DelaySeconds { get; set; }
	public int MaxFires { get; set; }
	public string Parameter { get; set; }
	public bool Enabled { get; set; }

	public EntityIoEdge( string id, string fromNodeId, string toNodeId,
		double delaySeconds = 0, int maxFires = -1, string parameter = "", bool enabled = true )
	{
		Id = id ?? "";
		FromNodeId = fromNodeId ?? "";
		ToNodeId = toNodeId ?? "";
		Parameter = parameter ?? "";
		DelaySeconds = delaySeconds;
		MaxFires = maxFires;
		Enabled = enabled;
	}

	/// <summary>Null when this edge is valid, otherwise a named error.</summary>
	public string Validate()
	{
		if ( string.IsNullOrWhiteSpace( Id ) ) return "edge id is empty";
		if ( string.IsNullOrWhiteSpace( FromNodeId ) ) return $"edge '{Id}': source node is empty";
		if ( string.IsNullOrWhiteSpace( ToNodeId ) ) return $"edge '{Id}': target node is empty";
		if ( Parameter.Contains( ',' ) || Parameter.Contains( '"' ) ) return $"edge '{Id}': parameter must not contain ',' or '\"'";
		if ( !double.IsFinite( DelaySeconds ) || DelaySeconds < 0 ) return $"edge '{Id}': delay must be a finite number >= 0";
		if ( MaxFires < -1 ) return $"edge '{Id}': max fires must be -1 (forever) or >= 0";
		return null;
	}

	public EntityIoEdge Copy() => new( Id, FromNodeId, ToNodeId, DelaySeconds, MaxFires, Parameter, Enabled );

	public bool Equals( EntityIoEdge other ) => other is not null
		&& Id == other.Id && FromNodeId == other.FromNodeId && ToNodeId == other.ToNodeId
		&& DelaySeconds.Equals( other.DelaySeconds ) && MaxFires == other.MaxFires
		&& Parameter == other.Parameter && Enabled == other.Enabled;

	public override bool Equals( object obj ) => Equals( obj as EntityIoEdge );

	public override int GetHashCode() => HashCode.Combine( Id, FromNodeId, ToNodeId, DelaySeconds, MaxFires, Parameter, Enabled );
}

/// <summary>
/// A sub-graph: the logic_relay equivalent — a named grouping of one relay
/// entity's nodes (its entity node plus its event/input nodes). The relay's
/// Trigger input node is the entry, its OnTrigger event node is the exit.
/// A grouping only: edges into/out of member nodes stay ordinary edges.
/// </summary>
public sealed class EntityIoSubGraph
{
	public string Id { get; set; }
	public string Name { get; set; }
	public string RelayEntity { get; set; }
	public string EntryNodeId { get; set; }
	public string OutputNodeId { get; set; }
	public List<string> MemberNodeIds { get; set; } = new List<string>();

	public EntityIoSubGraph( string id, string name, string relayEntity,
		string entryNodeId = "", string outputNodeId = "", List<string> memberNodeIds = null )
	{
		Id = id ?? "";
		Name = name ?? "";
		RelayEntity = relayEntity ?? "";
		EntryNodeId = entryNodeId ?? "";
		OutputNodeId = outputNodeId ?? "";
		MemberNodeIds = memberNodeIds is null ? new List<string>() : new List<string>( memberNodeIds );
	}

	/// <summary>Null when this sub-graph is valid against <paramref name="graph"/>, otherwise a named error.</summary>
	public string Validate( EntityIoGraph graph )
	{
		if ( string.IsNullOrWhiteSpace( Id ) ) return "sub-graph id is empty";
		if ( string.IsNullOrWhiteSpace( Name ) ) return $"sub-graph '{Id}': name is empty";
		if ( string.IsNullOrWhiteSpace( RelayEntity ) ) return $"sub-graph '{Id}': relay entity is empty";
		if ( graph is null ) return $"sub-graph '{Id}': no graph to validate against";
		var members = new HashSet<string>( MemberNodeIds, StringComparer.Ordinal );
		foreach ( string member in MemberNodeIds )
			if ( graph.FindNode( member ) is null ) return $"sub-graph '{Id}': member node '{member}' does not exist";
		string relayNodeId = "entity:" + RelayEntity;
		if ( !members.Contains( relayNodeId ) ) return $"sub-graph '{Id}': relay entity node '{relayNodeId}' is not a member";
		if ( !string.IsNullOrEmpty( EntryNodeId ) && !members.Contains( EntryNodeId ) ) return $"sub-graph '{Id}': entry node '{EntryNodeId}' is not a member";
		if ( !string.IsNullOrEmpty( OutputNodeId ) && !members.Contains( OutputNodeId ) ) return $"sub-graph '{Id}': output node '{OutputNodeId}' is not a member";
		return null;
	}

	public EntityIoSubGraph Copy() => new( Id, Name, RelayEntity, EntryNodeId, OutputNodeId, MemberNodeIds );
}

/// <summary>
/// One script entry point on one entity: when <see cref="EntryPoint"/> fires on
/// <see cref="Entity"/>, the Lua script in <see cref="Source"/> runs. The ctor
/// does NOT throw — the safety gate must report a named reason, so validation
/// lives in <see cref="EntityScriptHookRegistry.ValidateHook"/>; see the file
/// header for why this differs from EntityIoOutput (whose ctor throws).
/// <see cref="Language"/> is normalised: "vscript" becomes "lua".
/// </summary>
public sealed class EntityScriptHook : IEquatable<EntityScriptHook>
{
	public string Entity { get; set; }
	public string EntryPoint { get; set; }
	public string Language { get; set; }
	public string Source { get; set; }
	public bool AllowDestructive { get; set; }
	public List<string> Operations { get; set; } = new List<string>();

	public EntityScriptHook( string entity, string entryPoint, string language, string source,
		bool allowDestructive = false, List<string> operations = null )
	{
		Entity = entity ?? "";
		EntryPoint = entryPoint ?? "";
		Language = EntityScriptHookRegistry.NormaliseLanguage( language );
		Source = source ?? "";
		AllowDestructive = allowDestructive;
		Operations = operations is null ? new List<string>() : new List<string>( operations );
	}

	/// <summary>Null when this hook passes the safety gate, otherwise the named rejection reason.</summary>
	public string Validate() => EntityScriptHookRegistry.ValidateHook( this );

	public EntityScriptHook Copy() => new( Entity, EntryPoint, Language, Source, AllowDestructive, Operations );

	public bool Equals( EntityScriptHook other ) => other is not null
		&& Entity == other.Entity && EntryPoint == other.EntryPoint && Language == other.Language
		&& Source == other.Source && AllowDestructive == other.AllowDestructive
		&& Operations.SequenceEqual( other.Operations );

	public override bool Equals( object obj ) => Equals( obj as EntityScriptHook );

	public override int GetHashCode() => HashCode.Combine( Entity, EntryPoint, Language, Source, AllowDestructive );
}

/// <summary>
/// The script-hook registry: named Lua entry points per entity, guarded by the
/// safety rules mirrored from McpSafety.SafetyGates (no shell/exec, destructive
/// operations need an explicit flag, secrets are never stored). Registration,
/// validation and serialisation only — nothing here executes a script.
/// </summary>
public sealed class EntityScriptHookRegistry
{
	/// <summary>Maximum script source length, in characters — mirrors SafetyPolicy.MaxArgsBytes (256 KiB).</summary>
	public const int MaxSourceChars = 262144;

	/// <summary>Lua host-call forms of the McpSafety ForbiddenTools names, plus the Lua file/shell escapes. Checked case-insensitively, on identifier boundaries.</summary>
	static readonly string[] ForbiddenCalls =
	{
		"os.execute", "os.remove", "os.rename", "os.exit", "io.popen",
		"loadfile", "dofile", "loadstring", "shell_exec", "run_command", "run_shell",
	};

	/// <summary>Engine operations that destroy user work unless explicitly confirmed — mirrors McpSafety's destructive-tool gate (there: remove_feature).</summary>
	static readonly string[] DestructiveOperations = { "kill", "remove", "delete", "destroy", "explode", "overwrite" };

	/// <summary>Secret shapes, mirrored from McpSafety.SafetyGates. Rejection messages name the shape only, never the matched value.</summary>
	static readonly Regex SecretAssignment = new( @"\b[A-Za-z0-9_]*?(?:api_key|token|secret|password|passwd|auth|authorization)\s*[:=]\s*(""[^""]*""|'[^']*'|[^\s,;""']+)", RegexOptions.Compiled | RegexOptions.IgnoreCase );
	static readonly Regex SkKey = new( @"sk-[A-Za-z0-9\-_]{16,}", RegexOptions.Compiled );
	static readonly Regex GithubToken = new( @"(?:ghp_|gho_|ghu_|ghs_|ghr_|github_pat_)[A-Za-z0-9_]{10,}", RegexOptions.Compiled );
	static readonly Regex AwsAccessKey = new( @"AKIA[0-9A-Z]{16}", RegexOptions.Compiled );
	static readonly Regex BearerToken = new( @"Bearer\s+[A-Za-z0-9\-._~+/=]{8,}", RegexOptions.Compiled );
	static readonly Regex RequireOsIo = new( @"require\s*(?:\(\s*)?[""'](?:os|io)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase );
	static readonly Regex EntryPointName = new( @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled );

	readonly List<EntityScriptHook> _hooks = new List<EntityScriptHook>();
	readonly List<string> _audit = new List<string>();

	public IReadOnlyList<EntityScriptHook> Hooks => _hooks;
	public int Count => _hooks.Count;

	/// <summary>Append-only audit log (in memory): every accept / replace / reject / remove, UTC timestamped, never containing a secret value.</summary>
	public IReadOnlyList<string> AuditLog => _audit;

	/// <summary>"vscript" is Source's name for the same Lua runtime — normalise it to "lua". Anything else is returned lower-cased for ValidateHook to reject by name.</summary>
	public static string NormaliseLanguage( string language )
	{
		if ( string.IsNullOrWhiteSpace( language ) ) return "";
		string normalised = language.Trim().ToLowerInvariant();
		return normalised == "vscript" ? "lua" : normalised;
	}

	/// <summary>
	/// The safety gate. Null when the hook is accepted, otherwise the named
	/// rejection reason (the reason never contains a secret value).
	/// </summary>
	public static string ValidateHook( EntityScriptHook hook )
	{
		if ( hook is null ) return "script hook is null";
		if ( string.IsNullOrWhiteSpace( hook.Entity ) ) return "script hook entity is empty";
		if ( string.IsNullOrWhiteSpace( hook.EntryPoint ) ) return "script hook entry point is empty";
		if ( !EntryPointName.IsMatch( hook.EntryPoint ) ) return $"script hook entry point '{hook.EntryPoint}' is not a valid name (letters, digits and underscore; must not start with a digit)";
		if ( hook.Language != "lua" ) return $"script language '{hook.Language}' is not allowed: entity scripts are Lua/VScript only (no arbitrary shell)";
		if ( string.IsNullOrWhiteSpace( hook.Source ) ) return $"script hook '{hook.Entity}:{hook.EntryPoint}' has an empty source";
		if ( hook.Source.Length > MaxSourceChars ) return $"script hook '{hook.Entity}:{hook.EntryPoint}' source exceeds {MaxSourceChars} characters";
		if ( ContainsForbiddenCall( hook.Source, out string call ) ) return $"script hook '{hook.Entity}:{hook.EntryPoint}' uses forbidden operation '{call}': entity scripts may not execute shell commands or load external files";
		if ( RequireOsIo.IsMatch( hook.Source ) ) return $"script hook '{hook.Entity}:{hook.EntryPoint}' uses forbidden operation 'require(os/io)': entity scripts may not load the os/io libraries";
		foreach ( string operation in hook.Operations ?? new List<string>() )
		{
			if ( string.IsNullOrWhiteSpace( operation ) ) return $"script hook '{hook.Entity}:{hook.EntryPoint}' has an empty operation name";
			if ( operation.Contains( ',' ) || operation.Contains( '"' ) ) return $"script hook '{hook.Entity}:{hook.EntryPoint}' operation '{operation}' must not contain ',' or '\"'";
			if ( !hook.AllowDestructive && DestructiveOperations.Contains( operation, StringComparer.OrdinalIgnoreCase ) )
				return $"script hook '{hook.Entity}:{hook.EntryPoint}' operation '{operation}' is destructive and requires allow_destructive";
		}
		if ( FindSecretShape( hook.Source, out string shape ) ) return $"script hook '{hook.Entity}:{hook.EntryPoint}' source contains a secret value ({shape}); secrets are never stored in script hooks";
		return null;
	}

	static bool ContainsForbiddenCall( string source, out string found )
	{
		found = "";
		foreach ( string call in ForbiddenCalls )
		{
			int at = 0;
			while ( true )
			{
				int index = source.IndexOf( call, at, StringComparison.OrdinalIgnoreCase );
				if ( index < 0 ) break;
				bool beforeOk = index == 0 || !IsIdentifierChar( source[index - 1] );
				int after = index + call.Length;
				bool afterOk = after >= source.Length || !IsIdentifierChar( source[after] );
				if ( beforeOk && afterOk ) { found = call; return true; }
				at = index + 1;
			}
		}
		return false;
	}

	static bool IsIdentifierChar( char c ) => char.IsLetterOrDigit( c ) || c == '_' || c == '.';

	static bool FindSecretShape( string source, out string shape )
	{
		shape = "";
		Match assignment = SecretAssignment.Match( source );
		if ( assignment.Success )
		{
			string value = assignment.Groups[1].Value.Trim( '"', '\'' );
			if ( value.Length >= 4 && value != "[redacted]" ) { shape = "secret assignment"; return true; }
		}
		if ( SkKey.IsMatch( source ) ) { shape = "sk- key shape"; return true; }
		if ( GithubToken.IsMatch( source ) ) { shape = "GitHub token shape"; return true; }
		if ( AwsAccessKey.IsMatch( source ) ) { shape = "AWS access key shape"; return true; }
		if ( BearerToken.IsMatch( source ) ) { shape = "Bearer token shape"; return true; }
		return false;
	}

	/// <summary>Adds a hook, or replaces the hook already registered for the same entity + entry point. Returns null on success, otherwise the gate's rejection reason; the registry is unchanged on rejection. Every outcome is audit-logged.</summary>
	public string Add( EntityScriptHook hook )
	{
		string error = ValidateHook( hook );
		string key = hook is null ? "?" : hook.Entity + ":" + hook.EntryPoint;
		if ( error is not null ) { Audit( $"rejected {key}: {error}" ); return error; }
		int existing = _hooks.FindIndex( h => h.Entity == hook.Entity && h.EntryPoint == hook.EntryPoint );
		if ( existing >= 0 ) { _hooks[existing] = hook.Copy(); Audit( $"replaced {key} allow_destructive={hook.AllowDestructive}" ); }
		else { _hooks.Add( hook.Copy() ); Audit( $"accepted {key} allow_destructive={hook.AllowDestructive}" ); }
		return null;
	}

	/// <summary>Removes the hook for an entity + entry point. False when no such hook exists.</summary>
	public bool Remove( string entity, string entryPoint )
	{
		int index = _hooks.FindIndex( h => h.Entity == entity && h.EntryPoint == entryPoint );
		if ( index < 0 ) return false;
		_hooks.RemoveAt( index );
		Audit( $"removed {entity}:{entryPoint}" );
		return true;
	}

	public EntityScriptHook Find( string entity, string entryPoint ) =>
		_hooks.FirstOrDefault( h => h.Entity == entity && h.EntryPoint == entryPoint );

	public IReadOnlyList<EntityScriptHook> HooksFor( string entity ) =>
		_hooks.Where( h => h.Entity == entity ).ToList();

	/// <summary>Every hook's gate result; empty when the registry is fully valid.</summary>
	public IReadOnlyList<string> ValidateAll()
	{
		var errors = new List<string>();
		foreach ( EntityScriptHook hook in _hooks )
		{
			string error = ValidateHook( hook );
			if ( error is not null ) errors.Add( error );
		}
		return errors;
	}

	void Audit( string message ) => _audit.Add( DateTimeOffset.UtcNow.ToString( "o", CultureInfo.InvariantCulture ) + " " + message );

	// ---- JSON serialisation (version 1) ----

	sealed class HookJson
	{
		public string entity { get; set; } = "";
		public string entry_point { get; set; } = "";
		public string language { get; set; } = "";
		public string source { get; set; } = "";
		public bool allow_destructive { get; set; }
		public List<string> operations { get; set; } = new List<string>();
	}

	sealed class RegistryJson
	{
		public int version { get; set; } = 1;
		public List<HookJson> hooks { get; set; } = new List<HookJson>();
	}

	public string ToJson()
	{
		var doc = new RegistryJson
		{
			hooks = _hooks.Select( h => new HookJson
			{
				entity = h.Entity, entry_point = h.EntryPoint, language = h.Language,
				source = h.Source, allow_destructive = h.AllowDestructive,
				operations = new List<string>( h.Operations ),
			} ).ToList(),
		};
		return JsonSerializer.Serialize( doc, new JsonSerializerOptions { WriteIndented = true } );
	}

	/// <summary>Reads back <see cref="ToJson"/> output. Throws <see cref="ArgumentException"/> naming the bad hook.</summary>
	public static EntityScriptHookRegistry FromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) throw new ArgumentException( "script-hook JSON is empty.", nameof( json ) );
		RegistryJson doc;
		try { doc = JsonSerializer.Deserialize<RegistryJson>( json ); }
		catch ( JsonException ex ) { throw new ArgumentException( $"script-hook JSON is not valid JSON: {ex.Message}", nameof( json ), ex ); }
		if ( doc is null ) throw new ArgumentException( "script-hook JSON is empty.", nameof( json ) );
		if ( doc.version != 1 ) throw new ArgumentException( $"unsupported script-hook version {doc.version}; expected 1.", nameof( json ) );
		var registry = new EntityScriptHookRegistry();
		foreach ( HookJson row in doc.hooks ?? new List<HookJson>() )
		{
			var hook = new EntityScriptHook( row.entity, row.entry_point, row.language, row.source, row.allow_destructive, row.operations );
			string error = registry.Add( hook );
			if ( error is not null ) throw new ArgumentException( $"invalid script hook (entity '{row.entity}', entry point '{row.entry_point}'): {error}", nameof( json ) );
		}
		return registry;
	}
}

/// <summary>
/// The I/O node graph for one map: entity/event/input nodes, output edges
/// (event -> input with delay), logic_relay sub-graphs, and the entity script
/// hooks attached to its entities. Converts losslessly to and from
/// <see cref="EntityIoList"/> — both views edit the same data.
/// </summary>
public sealed class EntityIoGraph
{
	readonly List<EntityIoNode> _nodes = new List<EntityIoNode>();
	readonly List<EntityIoEdge> _edges = new List<EntityIoEdge>();
	readonly List<EntityIoSubGraph> _subGraphs = new List<EntityIoSubGraph>();

	public IReadOnlyList<EntityIoNode> Nodes => _nodes;
	public IReadOnlyList<EntityIoEdge> Edges => _edges;
	public IReadOnlyList<EntityIoSubGraph> SubGraphs => _subGraphs;
	public int NodeCount => _nodes.Count;
	public int EdgeCount => _edges.Count;
	public int SubGraphCount => _subGraphs.Count;

	/// <summary>Script hooks attached to this graph's entities (Lua entry points per entity).</summary>
	public EntityScriptHookRegistry ScriptHooks { get; } = new EntityScriptHookRegistry();

	public EntityIoNode FindNode( string id ) => _nodes.FirstOrDefault( n => n.Id == id );

	public EntityIoNode FindEntityNode( string entity ) => FindNode( "entity:" + entity );

	public EntityIoSubGraph FindSubGraph( string id ) => _subGraphs.FirstOrDefault( s => s.Id == id );

	/// <summary>Adds a node. Returns null on success, otherwise a named error; the graph is unchanged on error.</summary>
	public string AddNode( EntityIoNode node )
	{
		if ( node is null ) return "node is null";
		string error = node.Validate();
		if ( error is not null ) return error;
		if ( FindNode( node.Id ) is not null ) return $"duplicate node id '{node.Id}'";
		_nodes.Add( node.Copy() );
		return null;
	}

	/// <summary>Adds an edge. Returns null on success, otherwise a named error; the graph is unchanged on error. Endpoints must already exist.</summary>
	public string AddEdge( EntityIoEdge edge )
	{
		if ( edge is null ) return "edge is null";
		string error = edge.Validate();
		if ( error is not null ) return error;
		if ( _edges.Any( e => e.Id == edge.Id ) ) return $"duplicate edge id '{edge.Id}'";
		if ( FindNode( edge.FromNodeId ) is null ) return $"edge '{edge.Id}': source node '{edge.FromNodeId}' does not exist";
		if ( FindNode( edge.ToNodeId ) is null ) return $"edge '{edge.Id}': target node '{edge.ToNodeId}' does not exist";
		var from = FindNode( edge.FromNodeId );
		var to = FindNode( edge.ToNodeId );
		if ( from.Kind != EntityIoNodeKind.Event ) return $"edge '{edge.Id}': source node '{edge.FromNodeId}' is a {from.Kind}, not an Event";
		if ( to.Kind != EntityIoNodeKind.Input ) return $"edge '{edge.Id}': target node '{edge.ToNodeId}' is a {to.Kind}, not an Input";
		_edges.Add( edge.Copy() );
		return null;
	}

	/// <summary>Adds a sub-graph. Returns null on success, otherwise a named error; the graph is unchanged on error.</summary>
	public string AddSubGraph( EntityIoSubGraph subGraph )
	{
		if ( subGraph is null ) return "sub-graph is null";
		string error = subGraph.Validate( this );
		if ( error is not null ) return error;
		if ( FindSubGraph( subGraph.Id ) is not null ) return $"duplicate sub-graph id '{subGraph.Id}'";
		_subGraphs.Add( subGraph.Copy() );
		return null;
	}

	/// <summary>Removes a node, its incident edges, and its sub-graph memberships (entry/output refs to it are cleared). False when no such node exists.</summary>
	public bool RemoveNode( string id )
	{
		int index = _nodes.FindIndex( n => n.Id == id );
		if ( index < 0 ) return false;
		_nodes.RemoveAt( index );
		_edges.RemoveAll( e => e.FromNodeId == id || e.ToNodeId == id );
		foreach ( EntityIoSubGraph subGraph in _subGraphs )
		{
			subGraph.MemberNodeIds.RemoveAll( m => m == id );
			if ( subGraph.EntryNodeId == id ) subGraph.EntryNodeId = "";
			if ( subGraph.OutputNodeId == id ) subGraph.OutputNodeId = "";
		}
		return true;
	}

	/// <summary>
	/// Builds the graph for a list. Node ids are deterministic (see
	/// <see cref="EntityIoNode"/>); edges keep list order as e1..eN. When
	/// <paramref name="classNameByEntity"/> is supplied, every entity it reports
	/// as "logic_relay" gets a sub-graph (see <see cref="CreateRelaySubGraph"/>);
	/// without a resolver no sub-graphs are created. Special targets ("!self",
	/// "!activator", ...) get entity nodes like any other name — they are never
	/// a real targetname, but the edge still needs an endpoint.
	/// </summary>
	public static EntityIoGraph FromList( EntityIoList list, Func<string, string> classNameByEntity = null )
	{
		if ( list is null ) throw new ArgumentNullException( nameof( list ) );
		var graph = new EntityIoGraph();
		var entityOrder = new List<string>();
		var seenEntities = new HashSet<string>( StringComparer.Ordinal );
		void EnsureEntity( string entity )
		{
			if ( !seenEntities.Add( entity ) ) return;
			entityOrder.Add( entity );
			graph._nodes.Add( new EntityIoNode( "entity:" + entity, EntityIoNodeKind.Entity, entity, "", entity, 0, ( entityOrder.Count - 1 ) * 110 ) );
		}
		foreach ( EntityIoOutput row in list.Outputs )
		{
			EnsureEntity( row.SourceEntity );
			EnsureEntity( row.TargetName );
			string eventId = "event:" + row.SourceEntity + ":" + row.Event;
			if ( graph.FindNode( eventId ) is null )
				graph._nodes.Add( new EntityIoNode( eventId, EntityIoNodeKind.Event, row.SourceEntity, row.Event, row.SourceEntity + " : " + row.Event ) );
			string inputId = "input:" + row.TargetName + ":" + row.Input;
			if ( graph.FindNode( inputId ) is null )
				graph._nodes.Add( new EntityIoNode( inputId, EntityIoNodeKind.Input, row.TargetName, row.Input, row.TargetName + " : " + row.Input ) );
			graph._edges.Add( new EntityIoEdge( "e" + ( graph._edges.Count + 1 ), eventId, inputId,
				row.DelaySeconds, row.MaxFires, row.Parameter, row.Enabled ) );
		}
		Layout( graph );
		if ( classNameByEntity is not null )
		{
			foreach ( string entity in entityOrder )
			{
				string className = classNameByEntity( entity ) ?? "";
				if ( string.Equals( className, "logic_relay", StringComparison.OrdinalIgnoreCase ) )
					graph.CreateRelaySubGraph( entity, out _ );
			}
		}
		return graph;
	}

	static void Layout( EntityIoGraph graph )
	{
		var depth = new Dictionary<string, int>( StringComparer.Ordinal );
		foreach ( EntityIoNode node in graph._nodes ) depth[node.Id] = 0;
		for ( int pass = 0; pass < graph._nodes.Count; pass++ )
		{
			bool changed = false;
			foreach ( EntityIoEdge edge in graph._edges )
			{
				int next = depth[edge.FromNodeId] + 1;
				if ( next > depth[edge.ToNodeId] && next <= graph._nodes.Count ) { depth[edge.ToNodeId] = next; changed = true; }
			}
			if ( !changed ) break;
		}
		var perDepth = new Dictionary<int, int>();
		foreach ( EntityIoNode node in graph._nodes )
		{
			if ( node.Kind == EntityIoNodeKind.Entity ) continue;
			int d = depth[node.Id];
			perDepth.TryGetValue( d, out int at );
			perDepth[d] = at + 1;
			node.X = ( d + 1 ) * 260;
			node.Y = at * 110;
		}
	}

	/// <summary>
	/// Groups a relay entity's nodes into a sub-graph: the relay entity node plus
	/// its event/input nodes; entry is its Trigger input node, exit its OnTrigger
	/// event node (either is "" when the relay has no such port). Returns null on
	/// success, otherwise a named error; the graph is unchanged on error.
	/// </summary>
	public string CreateRelaySubGraph( string relayEntity, out EntityIoSubGraph subGraph )
	{
		subGraph = null;
		if ( string.IsNullOrWhiteSpace( relayEntity ) ) return "relay entity is empty";
		if ( FindEntityNode( relayEntity ) is null ) return $"no entity node for relay '{relayEntity}'";
		string id = "subgraph:" + relayEntity;
		if ( FindSubGraph( id ) is not null ) return $"sub-graph for relay '{relayEntity}' already exists";
		var members = new List<string> { "entity:" + relayEntity };
		string entry = "", output = "";
		foreach ( EntityIoNode node in _nodes )
		{
			if ( node.Entity != relayEntity || node.Kind == EntityIoNodeKind.Entity ) continue;
			members.Add( node.Id );
			if ( node.Kind == EntityIoNodeKind.Input && string.Equals( node.Port, "Trigger", StringComparison.OrdinalIgnoreCase ) ) entry = node.Id;
			if ( node.Kind == EntityIoNodeKind.Event && string.Equals( node.Port, "OnTrigger", StringComparison.OrdinalIgnoreCase ) ) output = node.Id;
		}
		subGraph = new EntityIoSubGraph( id, relayEntity, relayEntity, entry, output, members );
		_subGraphs.Add( subGraph );
		return null;
	}

	/// <summary>Converts the graph back to the list model. Throws <see cref="ArgumentException"/> naming the first bad edge.</summary>
	public EntityIoList ToList()
	{
		var errors = Validate();
		if ( errors.Count > 0 ) throw new ArgumentException( "entity I/O graph is invalid: " + errors[0] );
		var list = new EntityIoList();
		foreach ( EntityIoEdge edge in _edges )
		{
			var from = FindNode( edge.FromNodeId );
			var to = FindNode( edge.ToNodeId );
			EntityIoOutput row;
			try { row = new EntityIoOutput( from.Entity, from.Port, to.Entity, to.Port, edge.DelaySeconds, edge.MaxFires, edge.Parameter, edge.Enabled ); }
			catch ( ArgumentException ex ) { throw new ArgumentException( $"edge '{edge.Id}' does not convert to a list row: {ex.Message}", ex ); }
			list.Add( row );
		}
		return list;
	}

	/// <summary>False with a named <paramref name="error"/> instead of throwing when the graph does not convert.</summary>
	public bool TryToList( out EntityIoList list, out string error )
	{
		list = null;
		error = null;
		try { list = ToList(); return true; }
		catch ( ArgumentException ex ) { error = ex.Message; return false; }
	}

	/// <summary>Every validation error in the graph (nodes, edges, sub-graphs, script hooks); empty when the graph is valid.</summary>
	public IReadOnlyList<string> Validate()
	{
		var errors = new List<string>();
		var nodeIds = new HashSet<string>( StringComparer.Ordinal );
		foreach ( EntityIoNode node in _nodes )
		{
			string error = node.Validate();
			if ( error is not null ) errors.Add( error );
			if ( !nodeIds.Add( node.Id ) ) errors.Add( $"duplicate node id '{node.Id}'" );
		}
		var edgeIds = new HashSet<string>( StringComparer.Ordinal );
		foreach ( EntityIoEdge edge in _edges )
		{
			string error = edge.Validate();
			if ( error is not null ) errors.Add( error );
			if ( !edgeIds.Add( edge.Id ) ) errors.Add( $"duplicate edge id '{edge.Id}'" );
			var from = FindNode( edge.FromNodeId );
			var to = FindNode( edge.ToNodeId );
			if ( from is null ) errors.Add( $"edge '{edge.Id}': source node '{edge.FromNodeId}' does not exist" );
			else if ( from.Kind != EntityIoNodeKind.Event ) errors.Add( $"edge '{edge.Id}': source node '{edge.FromNodeId}' is a {from.Kind}, not an Event" );
			if ( to is null ) errors.Add( $"edge '{edge.Id}': target node '{edge.ToNodeId}' does not exist" );
			else if ( to.Kind != EntityIoNodeKind.Input ) errors.Add( $"edge '{edge.Id}': target node '{edge.ToNodeId}' is a {to.Kind}, not an Input" );
		}
		var subGraphIds = new HashSet<string>( StringComparer.Ordinal );
		foreach ( EntityIoSubGraph subGraph in _subGraphs )
		{
			string error = subGraph.Validate( this );
			if ( error is not null ) errors.Add( error );
			if ( !subGraphIds.Add( subGraph.Id ) ) errors.Add( $"duplicate sub-graph id '{subGraph.Id}'" );
		}
		foreach ( string error in ScriptHooks.ValidateAll() ) errors.Add( "script: " + error );
		return errors;
	}

	public bool IsValid => Validate().Count == 0;

	// ---- JSON serialisation (version 1) ----

	sealed class NodeJson
	{
		public string id { get; set; } = "";
		public string kind { get; set; } = "";
		public string entity { get; set; } = "";
		public string port { get; set; } = "";
		public string label { get; set; } = "";
		public double x { get; set; }
		public double y { get; set; }
	}

	sealed class EdgeJson
	{
		public string id { get; set; } = "";
		public string from { get; set; } = "";
		public string to { get; set; } = "";
		public double delay { get; set; }
		public int max_fires { get; set; } = -1;
		public bool enabled { get; set; } = true;
		public string parameter { get; set; } = "";
	}

	sealed class SubGraphJson
	{
		public string id { get; set; } = "";
		public string name { get; set; } = "";
		public string relay_entity { get; set; } = "";
		public string entry_node { get; set; } = "";
		public string output_node { get; set; } = "";
		public List<string> members { get; set; } = new List<string>();
	}

	sealed class GraphJson
	{
		public int version { get; set; } = 1;
		public List<NodeJson> nodes { get; set; } = new List<NodeJson>();
		public List<EdgeJson> edges { get; set; } = new List<EdgeJson>();
		public List<SubGraphJson> subgraphs { get; set; } = new List<SubGraphJson>();
		public JsonElement scripts { get; set; }
	}

	/// <summary>Serialises this graph (nodes, edges, sub-graphs, script hooks) as version-1 JSON. MapDocument embeds this verbatim alongside the list model's "entity_io" section.</summary>
	public string ToJson()
	{
		var doc = new GraphJson
		{
			nodes = _nodes.Select( n => new NodeJson
			{
				id = n.Id, kind = n.Kind.ToString().ToLowerInvariant(), entity = n.Entity,
				port = n.Port, label = n.Label, x = n.X, y = n.Y,
			} ).ToList(),
			edges = _edges.Select( e => new EdgeJson
			{
				id = e.Id, from = e.FromNodeId, to = e.ToNodeId, delay = e.DelaySeconds,
				max_fires = e.MaxFires, enabled = e.Enabled, parameter = e.Parameter,
			} ).ToList(),
			subgraphs = _subGraphs.Select( s => new SubGraphJson
			{
				id = s.Id, name = s.Name, relay_entity = s.RelayEntity,
				entry_node = s.EntryNodeId, output_node = s.OutputNodeId,
				members = new List<string>( s.MemberNodeIds ),
			} ).ToList(),
			scripts = JsonDocument.Parse( ScriptHooks.ToJson() ).RootElement.Clone(),
		};
		return JsonSerializer.Serialize( doc, new JsonSerializerOptions { WriteIndented = true } );
	}

	/// <summary>Reads back <see cref="ToJson"/> output. Throws <see cref="ArgumentException"/> naming the bad entry; never returns an invalid graph.</summary>
	public static EntityIoGraph FromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) throw new ArgumentException( "entity I/O graph JSON is empty.", nameof( json ) );
		GraphJson doc;
		try { doc = JsonSerializer.Deserialize<GraphJson>( json ); }
		catch ( JsonException ex ) { throw new ArgumentException( $"entity I/O graph JSON is not valid JSON: {ex.Message}", nameof( json ), ex ); }
		if ( doc is null ) throw new ArgumentException( "entity I/O graph JSON is empty.", nameof( json ) );
		if ( doc.version != 1 ) throw new ArgumentException( $"unsupported entity I/O graph version {doc.version}; expected 1.", nameof( json ) );
		var graph = new EntityIoGraph();
		foreach ( NodeJson row in doc.nodes ?? new List<NodeJson>() )
		{
			if ( !Enum.TryParse<EntityIoNodeKind>( row.kind, ignoreCase: true, out EntityIoNodeKind kind ) )
				throw new ArgumentException( $"invalid graph node '{row.id}': unknown kind '{row.kind}'.", nameof( json ) );
			string error = graph.AddNode( new EntityIoNode( row.id, kind, row.entity, row.port, row.label, row.x, row.y ) );
			if ( error is not null ) throw new ArgumentException( $"invalid graph node '{row.id}': {error}", nameof( json ) );
		}
		foreach ( EdgeJson row in doc.edges ?? new List<EdgeJson>() )
		{
			string error = graph.AddEdge( new EntityIoEdge( row.id, row.from, row.to, row.delay, row.max_fires, row.parameter, row.enabled ) );
			if ( error is not null ) throw new ArgumentException( $"invalid graph edge '{row.id}': {error}", nameof( json ) );
		}
		foreach ( SubGraphJson row in doc.subgraphs ?? new List<SubGraphJson>() )
		{
			string error = graph.AddSubGraph( new EntityIoSubGraph( row.id, row.name, row.relay_entity, row.entry_node, row.output_node, row.members ) );
			if ( error is not null ) throw new ArgumentException( $"invalid graph sub-graph '{row.id}': {error}", nameof( json ) );
		}
		if ( doc.scripts.ValueKind == JsonValueKind.Object )
		{
			try
			{
				var scripts = EntityScriptHookRegistry.FromJson( doc.scripts.GetRawText() );
				foreach ( EntityScriptHook hook in scripts.Hooks ) graph.ScriptHooks.Add( hook );
			}
			catch ( ArgumentException ex ) { throw new ArgumentException( $"invalid graph scripts: {ex.Message}", nameof( json ), ex ); }
		}
		var errors = graph.Validate();
		if ( errors.Count > 0 ) throw new ArgumentException( "entity I/O graph JSON is invalid: " + errors[0], nameof( json ) );
		return graph;
	}
}
