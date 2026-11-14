// Entity I/O logic list model (Hammer "When -> Target -> Input + delay").
//
// Hammer Desktop P1: hammer-entity-io-logic-list-model (worker muse-3-desktop-hammer-15).
// Backend / data-model / API only — no Desktop UI, no node graph (the node graph is
// explicitly Phase 3, hammer-io-node-graph-scripting-hooks). This is the LIST form:
// an entity owns output rows; each row says "when this event fires, send this input
// to that target, after this delay, this many times".
//
// Source: Report §1.4 — func_button OnPressed -> door_01 Open; logic_relay groups
// outputs; math_counter is an AND-gate; logic_auto fires on map load; Outputs tab
// Mark selects a target, a red target is missing, bold means several share the name.
//
// Naming: the row types are EntityIoOutput / EntityIoInput, NOT EntityOutput /
// EntityInput — those names already belong to the landed EntityRegistry
// (kernel/Effigy/Map/EntityRegistry.cs), where they are FGD input/output
// declarations. Do not rename either side.
//
// Seams (sibling workers own the other side):
//   * EntityRegistry (hammer-entity-registry-fgd-json) HAS LANDED:
//     kernel/Effigy/Map/EntityRegistry.cs + kernel/Effigy/Map/registry/
//     core-source1.json (schemaVersion 1, "classes" array). The logic-entity
//     definitions in this file use that schema exactly (see LogicEntityCatalog):
//     keyvalue types are string / int / float / colour / angle / target /
//     model / sound — there is NO "boolean" type (StartDisabled etc. are int
//     0/1); targetname is implicit via baseClasses ["Targetname"], never
//     redeclared; inputs/outputs are {name, argumentType, description}
//     objects. core-source1.json already holds logic_relay, logic_timer and
//     logic_auto; the other six (logic_branch, logic_case, math_counter,
//     logic_compare, env_global, point_template) are still missing —
//     ToRegistryJson() emits the exact entries to add. This file never writes
//     a registry JSON file itself.
//   * MapEntities (hammer-entity-place-edit-validate, worker 14) has NOT
//     landed yet (2026-11-13): no MapEntities.cs on disk. This file never
//     references its types. It can back ResolveTargets with the
//     Func<string,int> overload (count = FindByTargetName(name).Count) and
//     supplies SourceEntity keys. SourceEntity here is the source entity's
//     targetname, or its stable id ("#" + MapEntity.Id) when unnamed — never an
//     empty string.
//   * MapDocument (hammer-map-document-model, worker 12): embeds ToJson()
//     verbatim as the "entity_io" section of a map document; FromJson() reads
//     it back.
//   * VmfIo (hammer-vmf-map-import-export, worker 16): writes the lines from
//     ToVmfConnectionsBlock(sourceEntity) into an entity's "connections" block
//     and parses them back with FromVmfConnectionLines().
//
// VMF connection format (one line per output inside an entity's "connections"
// block): "Event" "TargetName,Input,Parameter,Delay,TimesToFire" — TimesToFire
// -1 means infinite, 1 means fire once. Delay is seconds, invariant culture.
// Parameter is the override parameter passed to the input (empty when none).
// Names must not contain ',' or '"' — a comma would break the VMF value format.
//
// Target resolution is case-insensitive (Source targetnames are) and treats
// engine special targets ("!self", "!activator", "!player", "!pvsplayer", ... —
// any name starting with '!') as always resolved: they are never a real
// targetname in the map, so they are never missing or ambiguous.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Effigy;

/// <summary>
/// One output row: "when <see cref="Event"/> fires on <see cref="SourceEntity"/>,
/// send <see cref="Input"/> to <see cref="TargetName"/> after
/// <see cref="DelaySeconds"/> seconds, at most <see cref="MaxFires"/> times
/// (-1 = forever). Mirrors one row of Hammer's Outputs tab.
/// </summary>
public sealed class EntityIoOutput : IEquatable<EntityIoOutput>
{
	public string SourceEntity { get; set; }
	public string Event { get; set; }
	public string TargetName { get; set; }
	public string Input { get; set; }
	public string Parameter { get; set; }
	public double DelaySeconds { get; set; }
	public int MaxFires { get; set; }
	public bool Enabled { get; set; }

	public EntityIoOutput( string sourceEntity, string @event, string targetName, string input,
		double delaySeconds = 0, int maxFires = -1, string parameter = "", bool enabled = true )
	{
		SourceEntity = sourceEntity;
		Event = @event;
		TargetName = targetName;
		Input = input;
		Parameter = parameter ?? "";
		DelaySeconds = delaySeconds;
		MaxFires = maxFires;
		Enabled = enabled;
		string error = Validate();
		if ( error is not null ) throw new ArgumentException( error );
	}

	/// <summary>Null when this row is valid, otherwise a named error.</summary>
	public string Validate()
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

	/// <summary>True for the events a logic_auto fires when a map loads (OnMapSpawn / OnNewGame / OnLoadGame / OnBackgroundMap, per the registry's logic_auto).</summary>
	public bool IsLoadEvent => Event is "OnMapSpawn" or "OnNewGame" or "OnLoadGame" or "OnBackgroundMap";

	/// <summary>True when this row fires exactly once (MaxFires == 1).</summary>
	public bool IsFireOnce => MaxFires == 1;

	public EntityIoOutput Copy() => new( SourceEntity, Event, TargetName, Input, DelaySeconds, MaxFires, Parameter, Enabled );

	/// <summary>One VMF connections-block line: "Event" "Target,Input,Parameter,Delay,Times".</summary>
	public string ToVmfConnection() =>
		$"\"{Event}\" \"{TargetName},{Input},{Parameter},{DelaySeconds.ToString( "G", CultureInfo.InvariantCulture )},{MaxFires}\"";

	public bool Equals( EntityIoOutput other ) => other is not null
		&& SourceEntity == other.SourceEntity && Event == other.Event && TargetName == other.TargetName
		&& Input == other.Input && Parameter == other.Parameter
		&& DelaySeconds.Equals( other.DelaySeconds ) && MaxFires == other.MaxFires && Enabled == other.Enabled;

	public override bool Equals( object obj ) => Equals( obj as EntityIoOutput );

	public override int GetHashCode() => HashCode.Combine( SourceEntity, Event, TargetName, Input, Parameter, DelaySeconds, MaxFires, Enabled );
}

/// <summary>
/// One input row: the receiving side of an output — what <see cref="TargetEntity"/>
/// is told to do (<see cref="Input"/>) when <see cref="SourceEntity"/>'s
/// <see cref="Event"/> fires. <see cref="EntityIoList.InputsFor"/> derives these
/// from outputs; a row can also stand alone to document an input an entity accepts.
/// </summary>
public sealed class EntityIoInput : IEquatable<EntityIoInput>
{
	public string TargetEntity { get; set; }
	public string Input { get; set; }
	public string Parameter { get; set; }
	public string SourceEntity { get; set; }
	public string Event { get; set; }
	public double DelaySeconds { get; set; }
	public int MaxFires { get; set; }
	public bool Enabled { get; set; }

	public EntityIoInput( string targetEntity, string input, string parameter = "",
		string sourceEntity = "", string @event = "", double delaySeconds = 0, int maxFires = -1, bool enabled = true )
	{
		TargetEntity = targetEntity;
		Input = input;
		Parameter = parameter ?? "";
		SourceEntity = sourceEntity ?? "";
		Event = @event ?? "";
		DelaySeconds = delaySeconds;
		MaxFires = maxFires;
		Enabled = enabled;
		string error = Validate();
		if ( error is not null ) throw new ArgumentException( error );
	}

	/// <summary>Null when this row is valid, otherwise a named error.</summary>
	public string Validate()
	{
		if ( string.IsNullOrWhiteSpace( TargetEntity ) ) return "target entity is empty";
		if ( string.IsNullOrWhiteSpace( Input ) ) return "input is empty";
		if ( Input.Contains( ',' ) || Input.Contains( '"' ) ) return "input must not contain ',' or '\"'";
		if ( Parameter.Contains( ',' ) || Parameter.Contains( '"' ) ) return "parameter must not contain ',' or '\"'";
		if ( !double.IsFinite( DelaySeconds ) || DelaySeconds < 0 ) return "delay must be a finite number >= 0";
		if ( MaxFires < -1 ) return "max fires must be -1 (forever) or >= 0";
		return null;
	}

	/// <summary>The input row a target sees for one output row.</summary>
	public static EntityIoInput FromOutput( EntityIoOutput output ) => new(
		output.TargetName, output.Input, output.Parameter,
		output.SourceEntity, output.Event, output.DelaySeconds, output.MaxFires, output.Enabled );

	public EntityIoInput Copy() => new( TargetEntity, Input, Parameter, SourceEntity, Event, DelaySeconds, MaxFires, Enabled );

	public bool Equals( EntityIoInput other ) => other is not null
		&& TargetEntity == other.TargetEntity && Input == other.Input && Parameter == other.Parameter
		&& SourceEntity == other.SourceEntity && Event == other.Event
		&& DelaySeconds.Equals( other.DelaySeconds ) && MaxFires == other.MaxFires && Enabled == other.Enabled;

	public override bool Equals( object obj ) => Equals( obj as EntityIoInput );

	public override int GetHashCode() => HashCode.Combine( TargetEntity, Input, Parameter, SourceEntity, Event, DelaySeconds, MaxFires, Enabled );
}

/// <summary>
/// What <see cref="EntityIoList.ResolveTargets"/> found. <see cref="Missing"/> is
/// the red-target case (a target name no entity has); <see cref="Ambiguous"/> is
/// the bold case (several entities share the name, so the engine fires all of
/// them). Both lists hold distinct target names, sorted case-insensitively.
/// </summary>
public sealed class EntityIoResolution
{
	public IReadOnlyList<string> Missing { get; }
	public IReadOnlyList<string> Ambiguous { get; }
	public IReadOnlyList<EntityIoOutput> ResolvedOutputs { get; }
	public IReadOnlyList<EntityIoOutput> MissingOutputs { get; }
	public IReadOnlyList<EntityIoOutput> AmbiguousOutputs { get; }

	public EntityIoResolution( IReadOnlyList<string> missing, IReadOnlyList<string> ambiguous,
		IReadOnlyList<EntityIoOutput> resolvedOutputs, IReadOnlyList<EntityIoOutput> missingOutputs,
		IReadOnlyList<EntityIoOutput> ambiguousOutputs )
	{
		Missing = missing;
		Ambiguous = ambiguous;
		ResolvedOutputs = resolvedOutputs;
		MissingOutputs = missingOutputs;
		AmbiguousOutputs = ambiguousOutputs;
	}

	public bool HasProblems => Missing.Count > 0 || Ambiguous.Count > 0;
	public bool IsClean => !HasProblems;
}

/// <summary>
/// The I/O list model for one map: every entity's output rows, with Add / Remove /
/// Copy, target resolution, Mark-target lookup, and serialisation into the map
/// document (JSON) and VMF connections blocks.
/// </summary>
public sealed class EntityIoList
{
	readonly List<EntityIoOutput> _outputs = new();

	public IReadOnlyList<EntityIoOutput> Outputs => _outputs;
	public int Count => _outputs.Count;

	/// <summary>Adds a row. Returns null on success, otherwise a named error; the list is unchanged on error.</summary>
	public string Add( EntityIoOutput output )
	{
		if ( output is null ) return "output is null";
		string error = output.Validate();
		if ( error is not null ) return error;
		_outputs.Add( output );
		return null;
	}

	/// <summary>Removes the first row equal to <paramref name="output"/>. False when no such row exists.</summary>
	public bool Remove( EntityIoOutput output )
	{
		if ( output is null ) return false;
		int index = _outputs.FindIndex( o => o.Equals( output ) );
		if ( index < 0 ) return false;
		_outputs.RemoveAt( index );
		return true;
	}

	/// <summary>Duplicates a row (Hammer's Copy on the Outputs tab) and adds the duplicate. Returns the new row.</summary>
	public EntityIoOutput Copy( EntityIoOutput output )
	{
		if ( output is null ) throw new ArgumentNullException( nameof( output ) );
		var copy = output.Copy();
		_outputs.Add( copy );
		return copy;
	}

	public IReadOnlyList<EntityIoOutput> OutputsFrom( string sourceEntity ) =>
		_outputs.Where( o => o.SourceEntity == sourceEntity ).ToList();

	public IReadOnlyList<EntityIoOutput> OutputsTo( string targetName ) =>
		_outputs.Where( o => string.Equals( o.TargetName, targetName, StringComparison.OrdinalIgnoreCase ) ).ToList();

	/// <summary>The input rows a target receives from this list's outputs.</summary>
	public IReadOnlyList<EntityIoInput> InputsFor( string targetName ) =>
		OutputsTo( targetName ).Select( EntityIoInput.FromOutput ).ToList();

	/// <summary>
	/// Resolves every referenced target name against the targetnames present in the
	/// map. <paramref name="knownTargetNames"/> is the multiset of targetnames in the
	/// map (a name appearing twice means two entities share it, i.e. ambiguous).
	/// </summary>
	public EntityIoResolution ResolveTargets( IReadOnlyCollection<string> knownTargetNames )
	{
		if ( knownTargetNames is null ) throw new ArgumentNullException( nameof( knownTargetNames ) );
		var counts = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		foreach ( string name in knownTargetNames )
		{
			if ( string.IsNullOrWhiteSpace( name ) ) continue;
			counts.TryGetValue( name, out int count );
			counts[name] = count + 1;
		}
		return ResolveTargets( name => counts.TryGetValue( name, out int count ) ? count : 0 );
	}

	/// <summary>
	/// Resolves every referenced target name through a counter (MapEntities can back
	/// this with FindByTargetName(name).Count, so no name list is materialised).
	/// </summary>
	public EntityIoResolution ResolveTargets( Func<string, int> countByTargetName )
	{
		if ( countByTargetName is null ) throw new ArgumentNullException( nameof( countByTargetName ) );
		var missing = new SortedSet<string>( StringComparer.OrdinalIgnoreCase );
		var ambiguous = new SortedSet<string>( StringComparer.OrdinalIgnoreCase );
		var resolved = new List<EntityIoOutput>();
		var missingOutputs = new List<EntityIoOutput>();
		var ambiguousOutputs = new List<EntityIoOutput>();
		foreach ( EntityIoOutput output in _outputs )
		{
			if ( IsSpecialTarget( output.TargetName ) ) { resolved.Add( output ); continue; }
			int count = countByTargetName( output.TargetName );
			if ( count == 0 ) { missing.Add( output.TargetName ); missingOutputs.Add( output ); }
			else if ( count == 1 ) resolved.Add( output );
			else { ambiguous.Add( output.TargetName ); ambiguousOutputs.Add( output ); }
		}
		return new EntityIoResolution( missing.ToList(), ambiguous.ToList(), resolved, missingOutputs, ambiguousOutputs );
	}

	/// <summary>Engine special targets ("!self", "!activator", "!player", ...) always resolve — they are never a real targetname.</summary>
	public static bool IsSpecialTarget( string targetName ) =>
		!string.IsNullOrEmpty( targetName ) && targetName[0] == '!';

	/// <summary>
	/// Mark-target lookup: the names the Mark picker (Hammer's Outputs-tab "Mark"
	/// button) offers for a typed query — known targetnames containing the query,
	/// case-insensitive, distinct, sorted. An empty query returns every known name.
	/// </summary>
	public static IReadOnlyList<string> MarkCandidates( IReadOnlyCollection<string> knownTargetNames, string query )
	{
		if ( knownTargetNames is null ) throw new ArgumentNullException( nameof( knownTargetNames ) );
		query ??= "";
		var found = new SortedSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( string name in knownTargetNames )
		{
			if ( string.IsNullOrWhiteSpace( name ) ) continue;
			if ( name.Contains( query, StringComparison.OrdinalIgnoreCase ) ) found.Add( name );
		}
		return found.ToList();
	}

	// ---- Map-document serialisation (JSON, the "entity_io" section) ----

	sealed class OutputJson
	{
		public string source_entity { get; set; } = "";
		public string @event { get; set; } = "";
		public string target { get; set; } = "";
		public string input { get; set; } = "";
		public string parameter { get; set; } = "";
		public double delay { get; set; }
		public int max_fires { get; set; } = -1;
		public bool enabled { get; set; } = true;
	}

	sealed class ListJson
	{
		public int version { get; set; } = 1;
		public List<OutputJson> outputs { get; set; } = new();
	}

	/// <summary>Serialises this list as the "entity_io" section of a map document (version 1). MapDocument embeds this verbatim.</summary>
	public string ToJson()
	{
		var doc = new ListJson
		{
			outputs = _outputs.Select( o => new OutputJson
			{
				source_entity = o.SourceEntity, @event = o.Event, target = o.TargetName,
				input = o.Input, parameter = o.Parameter, delay = o.DelaySeconds,
				max_fires = o.MaxFires, enabled = o.Enabled,
			} ).ToList(),
		};
		return JsonSerializer.Serialize( doc, new JsonSerializerOptions { WriteIndented = true } );
	}

	/// <summary>Reads back <see cref="ToJson"/> output. Throws <see cref="ArgumentException"/> naming the bad field.</summary>
	public static EntityIoList FromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) throw new ArgumentException( "entity_io JSON is empty.", nameof( json ) );
		ListJson doc;
		try { doc = JsonSerializer.Deserialize<ListJson>( json ); }
		catch ( JsonException ex ) { throw new ArgumentException( $"entity_io JSON is not valid JSON: {ex.Message}", nameof( json ), ex ); }
		if ( doc is null ) throw new ArgumentException( "entity_io JSON is empty.", nameof( json ) );
		if ( doc.version != 1 ) throw new ArgumentException( $"unsupported entity_io version {doc.version}; expected 1.", nameof( json ) );
		var list = new EntityIoList();
		foreach ( OutputJson row in doc.outputs ?? new List<OutputJson>() )
		{
			EntityIoOutput output;
			try { output = new EntityIoOutput( row.source_entity, row.@event, row.target, row.input, row.delay, row.max_fires, row.parameter, row.enabled ); }
			catch ( ArgumentException ex ) { throw new ArgumentException( $"invalid entity_io output row (source '{row.source_entity}', event '{row.@event}'): {ex.Message}", nameof( json ), ex ); }
			list._outputs.Add( output );
		}
		return list;
	}

	// ---- VMF serialisation (connections blocks) ----

	/// <summary>One VMF connections-block line per output owned by <paramref name="sourceEntity"/>.</summary>
	public IReadOnlyList<string> ToVmfConnectionLines( string sourceEntity ) =>
		OutputsFrom( sourceEntity ).Select( o => o.ToVmfConnection() ).ToList();

	/// <summary>The full VMF "connections" sub-block for one entity, as lines (for VmfIo to write into the entity block).</summary>
	public IReadOnlyList<string> ToVmfConnectionsBlock( string sourceEntity )
	{
		var lines = new List<string> { "connections", "{" };
		lines.AddRange( ToVmfConnectionLines( sourceEntity ) );
		lines.Add( "}" );
		return lines;
	}

	/// <summary>
	/// Parses one VMF connections line ('"Event" "Target,Input,Parameter,Delay,Times"')
	/// into the output row it describes for <paramref name="sourceEntity"/>.
	/// Returns false with a named <paramref name="error"/> instead of throwing.
	/// </summary>
	public static bool TryParseVmfConnectionLine( string line, string sourceEntity, out EntityIoOutput output, out string error )
	{
		output = null;
		error = null;
		if ( string.IsNullOrWhiteSpace( line ) ) { error = "connection line is empty"; return false; }
		if ( !TryReadQuoted( line, 0, out string @event, out int next ) || !TryReadQuoted( line, next, out string value, out _ ) )
		{
			error = $"connection line is not two quoted strings: {line}";
			return false;
		}
		string[] parts = value.Split( ',' );
		if ( parts.Length != 5 ) { error = $"connection value must have 5 comma-separated parts (target,input,parameter,delay,times): {value}"; return false; }
		if ( !double.TryParse( parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double delay ) )
		{
			error = $"connection delay is not a number: {parts[3]}";
			return false;
		}
		if ( !int.TryParse( parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int times ) )
		{
			error = $"connection fire count is not an integer: {parts[4]}";
			return false;
		}
		try { output = new EntityIoOutput( sourceEntity, @event, parts[0], parts[1], delay, times, parts[2] ); }
		catch ( ArgumentException ex ) { error = ex.Message; return false; }
		return true;
	}

	/// <summary>
	/// Parses VMF connections lines back into a list (blank lines and the
	/// "connections" / "{" / "}" wrapper lines are skipped, so a whole block can be
	/// passed). Throws <see cref="ArgumentException"/> naming the bad line.
	/// </summary>
	public static EntityIoList FromVmfConnectionLines( string sourceEntity, IEnumerable<string> lines )
	{
		if ( string.IsNullOrWhiteSpace( sourceEntity ) ) throw new ArgumentException( "source entity is empty.", nameof( sourceEntity ) );
		if ( lines is null ) throw new ArgumentNullException( nameof( lines ) );
		var list = new EntityIoList();
		foreach ( string raw in lines )
		{
			string line = raw?.Trim() ?? "";
			if ( line.Length == 0 || line is "connections" or "{" or "}" ) continue;
			if ( !TryParseVmfConnectionLine( line, sourceEntity, out EntityIoOutput output, out string error ) )
				throw new ArgumentException( $"bad VMF connection line '{raw}': {error}", nameof( lines ) );
			list._outputs.Add( output! );
		}
		return list;
	}

	static bool TryReadQuoted( string text, int start, out string value, out int next )
	{
		value = "";
		next = start;
		int open = text.IndexOf( '"', start );
		if ( open < 0 ) return false;
		int close = text.IndexOf( '"', open + 1 );
		if ( close < 0 ) return false;
		value = text.Substring( open + 1, close - open - 1 );
		next = close + 1;
		return true;
	}
}

/// <summary>One input or output port on a logic entity (name, argument type, what it does). Argument types use the registry's vocabulary ("void", "float", "int", "string").</summary>
public sealed class LogicEntityPort
{
	public string Name { get; }
	public string ArgumentType { get; }
	public string Description { get; }

	public LogicEntityPort( string name, string argumentType, string description )
	{
		Name = name;
		ArgumentType = string.IsNullOrWhiteSpace( argumentType ) ? "void" : argumentType;
		Description = description ?? "";
	}
}

/// <summary>One keyvalue a logic entity accepts (name, registry type spelling, default, what it does). Types are the registry's: "string" / "int" / "float" — there is no "boolean" type; on/off keyvalues are int 0/1.</summary>
public sealed class LogicEntityKey
{
	public string Name { get; }
	public string Type { get; }
	public string DefaultValue { get; }
	public string Description { get; }

	public LogicEntityKey( string name, string type, string defaultValue, string description )
	{
		Name = name;
		Type = type;
		DefaultValue = defaultValue ?? "";
		Description = description ?? "";
	}
}

/// <summary>One logic entity's definition: what it is, which inputs it accepts, which outputs it fires, which keyvalues it takes. Kind is "point" for all nine; targetname comes from baseClasses ["Targetname"] (implicit, never redeclared).</summary>
public sealed class LogicEntityDefinition
{
	public string ClassName { get; }
	public string Description { get; }
	public string Kind { get; }
	public IReadOnlyList<string> BaseClasses { get; }
	public IReadOnlyList<LogicEntityPort> Inputs { get; }
	public IReadOnlyList<LogicEntityPort> Outputs { get; }
	public IReadOnlyList<LogicEntityKey> KeyValues { get; }
	public IReadOnlyList<string> Helpers { get; }

	public LogicEntityDefinition( string className, string description, string kind,
		IReadOnlyList<string> baseClasses, IReadOnlyList<LogicEntityPort> inputs,
		IReadOnlyList<LogicEntityPort> outputs, IReadOnlyList<LogicEntityKey> keyValues,
		IReadOnlyList<string> helpers )
	{
		ClassName = className;
		Description = description ?? "";
		Kind = string.IsNullOrWhiteSpace( kind ) ? "point" : kind;
		BaseClasses = baseClasses ?? Array.Empty<string>();
		Inputs = inputs ?? Array.Empty<LogicEntityPort>();
		Outputs = outputs ?? Array.Empty<LogicEntityPort>();
		KeyValues = keyValues ?? Array.Empty<LogicEntityKey>();
		Helpers = helpers ?? Array.Empty<string>();
	}

	public IReadOnlyList<string> InputNames => Inputs.Select( p => p.Name ).ToList();
	public IReadOnlyList<string> OutputNames => Outputs.Select( p => p.Name ).ToList();
}

/// <summary>
/// The nine logic entities (logic_auto, logic_relay, logic_timer, logic_branch,
/// logic_case, math_counter, logic_compare, env_global, point_template) as DATA in
/// the LANDED registry schema (EntityRegistry: schemaVersion 1, "classes" array,
/// per-class name / kind / description / baseClasses / keyvalues / inputs /
/// outputs / helpers; keyvalue types string / int / float; inputs/outputs are
/// {name, argumentType, description} objects).
///
/// <see cref="LogicEntitiesJson"/> is the canonical definition document, loaded by
/// <see cref="FromJson"/>; <see cref="ToRegistryJson"/> emits the exact entries
/// the registry worker merges into the registry JSON (this file never writes a
/// registry JSON file itself). The three classes already in
/// registry/core-source1.json (logic_auto, logic_relay, logic_timer) are copied
/// from that file verbatim, so either copy loads identically; the other six are
/// the entries still missing there. Keyvalue types are validated on load through
/// <see cref="EntityRegistry.ParseKeyValueType"/>, so a "boolean" type (which the
/// registry does not have) is rejected instead of silently landing.
/// </summary>
public static class LogicEntityCatalog
{
	/// <summary>The nine logic-entity definitions, loaded from <see cref="LogicEntitiesJson"/>.</summary>
	public static IReadOnlyList<LogicEntityDefinition> Default { get; } = FromJson( LogicEntitiesJson );

	/// <summary>Alias for <see cref="Default"/>.</summary>
	public static IReadOnlyList<LogicEntityDefinition> All => Default;

	public static bool TryGet( string className, out LogicEntityDefinition definition )
	{
		definition = Default.FirstOrDefault( d => string.Equals( d.ClassName, className, StringComparison.OrdinalIgnoreCase ) );
		return definition is not null;
	}

	public static bool IsLogicEntity( string className ) => TryGet( className, out _ );

	/// <summary>Loads logic-entity definitions from a registry-JSON document (schemaVersion 1, "classes" array). Throws <see cref="ArgumentException"/> naming the bad entry.</summary>
	public static IReadOnlyList<LogicEntityDefinition> FromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) throw new ArgumentException( "logic-entity JSON is empty.", nameof( json ) );
		JsonDocument doc;
		try { doc = JsonDocument.Parse( json ); }
		catch ( JsonException ex ) { throw new ArgumentException( $"logic-entity JSON is not valid JSON: {ex.Message}", nameof( json ), ex ); }
		using ( doc )
		{
			JsonElement root = doc.RootElement;
			if ( root.ValueKind != JsonValueKind.Object ) throw new ArgumentException( "logic-entity JSON root must be an object.", nameof( json ) );
			if ( root.TryGetProperty( "schemaVersion", out JsonElement version ) )
			{
				if ( version.ValueKind != JsonValueKind.Number || !version.TryGetInt32( out int v ) || v != 1 )
					throw new ArgumentException( "unsupported logic-entity JSON schemaVersion; expected 1.", nameof( json ) );
			}
			if ( !root.TryGetProperty( "classes", out JsonElement classes ) || classes.ValueKind != JsonValueKind.Array )
				throw new ArgumentException( "logic-entity JSON must have a \"classes\" array.", nameof( json ) );
			var definitions = new List<LogicEntityDefinition>();
			var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
			foreach ( JsonElement entity in classes.EnumerateArray() )
			{
				string className = RequiredString( entity, "name", json );
				if ( !seen.Add( className ) ) throw new ArgumentException( $"duplicate logic-entity definition: {className}", nameof( json ) );
				string description = OptionalString( entity, "description" );
				string kind = OptionalString( entity, "kind", "point" );
				if ( kind is not "point" and not "brush" )
					throw new ArgumentException( $"logic entity {className}: \"kind\" must be \"point\" or \"brush\".", nameof( json ) );
				var baseClasses = StringArray( entity, "baseClasses", json, className );
				var helpers = StringArray( entity, "helpers", json, className, objectForm: true, kindProperty: "kind" );
				var inputs = PortArray( entity, "inputs", json, className );
				var outputs = PortArray( entity, "outputs", json, className );
				var keys = new List<LogicEntityKey>();
				if ( entity.TryGetProperty( "keyvalues", out JsonElement keyvalues ) )
				{
					if ( keyvalues.ValueKind != JsonValueKind.Array )
						throw new ArgumentException( $"logic entity {className}: \"keyvalues\" must be an array.", nameof( json ) );
					foreach ( JsonElement key in keyvalues.EnumerateArray() )
					{
						string keyName = RequiredString( key, "name", json );
						string keyType = RequiredString( key, "type", json );
						try { EntityRegistry.ParseKeyValueType( keyType ); }
						catch ( EntityRegistryException ex ) { throw new ArgumentException( $"logic entity {className}: keyvalue '{keyName}' has unknown type '{keyType}' ({ex.Code}).", nameof( json ), ex ); }
						keys.Add( new LogicEntityKey( keyName, keyType, OptionalString( key, "default" ), OptionalString( key, "description" ) ) );
					}
				}
				definitions.Add( new LogicEntityDefinition( className, description, kind, baseClasses, inputs, outputs, keys, helpers ) );
			}
			return definitions;
		}

		static string RequiredString( JsonElement element, string property, string json )
		{
			if ( !element.TryGetProperty( property, out JsonElement value ) || value.ValueKind != JsonValueKind.String
				|| string.IsNullOrWhiteSpace( value.GetString() ) )
				throw new ArgumentException( $"logic-entity JSON entry is missing a non-empty \"{property}\".", nameof( json ) );
			return value.GetString()!;
		}

		static string OptionalString( JsonElement element, string property, string fallback = "" ) =>
			element.TryGetProperty( property, out JsonElement value ) && value.ValueKind == JsonValueKind.String
				? value.GetString() ?? fallback
				: fallback;

		static List<string> StringArray( JsonElement element, string property, string json, string className, bool objectForm = false, string kindProperty = "" )
		{
			var list = new List<string>();
			if ( !element.TryGetProperty( property, out JsonElement array ) ) return list;
			if ( array.ValueKind != JsonValueKind.Array )
				throw new ArgumentException( $"logic entity {className}: \"{property}\" must be an array.", nameof( json ) );
			foreach ( JsonElement item in array.EnumerateArray() )
			{
				if ( objectForm )
				{
					if ( item.ValueKind != JsonValueKind.Object )
						throw new ArgumentException( $"logic entity {className}: \"{property}\" must contain only objects.", nameof( json ) );
					list.Add( RequiredString( item, kindProperty, json ) );
				}
				else
				{
					if ( item.ValueKind != JsonValueKind.String )
						throw new ArgumentException( $"logic entity {className}: \"{property}\" must contain only strings.", nameof( json ) );
					list.Add( item.GetString()! );
				}
			}
			return list;
		}

		static List<LogicEntityPort> PortArray( JsonElement element, string property, string json, string className )
		{
			var list = new List<LogicEntityPort>();
			if ( !element.TryGetProperty( property, out JsonElement array ) ) return list;
			if ( array.ValueKind != JsonValueKind.Array )
				throw new ArgumentException( $"logic entity {className}: \"{property}\" must be an array.", nameof( json ) );
			foreach ( JsonElement item in array.EnumerateArray() )
			{
				if ( item.ValueKind != JsonValueKind.Object )
					throw new ArgumentException( $"logic entity {className}: \"{property}\" must contain only objects.", nameof( json ) );
				list.Add( new LogicEntityPort(
					RequiredString( item, "name", json ),
					OptionalString( item, "argumentType", "void" ),
					OptionalString( item, "description" ) ) );
			}
			return list;
		}
	}

	/// <summary>Emits the canonical definitions as registry JSON (schemaVersion 1, name "source1-logic") — the exact entries for the registry JSON.</summary>
	public static string ToRegistryJson()
	{
		using var stream = new MemoryStream();
		using ( var writer = new Utf8JsonWriter( stream, new JsonWriterOptions { Indented = true } ) )
		{
			writer.WriteStartObject();
			writer.WriteNumber( "schemaVersion", 1 );
			writer.WriteString( "name", "source1-logic" );
			writer.WriteStartArray( "classes" );
			foreach ( LogicEntityDefinition definition in Default )
			{
				writer.WriteStartObject();
				writer.WriteString( "name", definition.ClassName );
				writer.WriteString( "kind", definition.Kind );
				writer.WriteString( "description", definition.Description );
				writer.WriteStartArray( "baseClasses" );
				foreach ( string baseClass in definition.BaseClasses ) writer.WriteStringValue( baseClass );
				writer.WriteEndArray();
				writer.WriteStartArray( "keyvalues" );
				foreach ( LogicEntityKey key in definition.KeyValues )
				{
					writer.WriteStartObject();
					writer.WriteString( "name", key.Name );
					writer.WriteString( "type", key.Type );
					writer.WriteString( "default", key.DefaultValue );
					writer.WriteString( "description", key.Description );
					writer.WriteEndObject();
				}
				writer.WriteEndArray();
				writer.WriteStartArray( "inputs" );
				foreach ( LogicEntityPort port in definition.Inputs ) WritePort( writer, port );
				writer.WriteEndArray();
				writer.WriteStartArray( "outputs" );
				foreach ( LogicEntityPort port in definition.Outputs ) WritePort( writer, port );
				writer.WriteEndArray();
				writer.WriteStartArray( "helpers" );
				foreach ( string helper in definition.Helpers )
				{
					writer.WriteStartObject();
					writer.WriteString( "kind", helper );
					writer.WriteEndObject();
				}
				writer.WriteEndArray();
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
			writer.WriteEndObject();
		}
		return Encoding.UTF8.GetString( stream.ToArray() );

		static void WritePort( Utf8JsonWriter writer, LogicEntityPort port )
		{
			writer.WriteStartObject();
			writer.WriteString( "name", port.Name );
			writer.WriteString( "argumentType", port.ArgumentType );
			writer.WriteString( "description", port.Description );
			writer.WriteEndObject();
		}
	}

	/// <summary>Canonical logic-entity definitions (registry-JSON shape, schemaVersion 1). Also shipped as the test fixture logic-entities.json. logic_auto / logic_relay / logic_timer are copied verbatim from registry/core-source1.json; the other six are the entries still missing there.</summary>
	public const string LogicEntitiesJson = """
{
  "schemaVersion": 1,
  "name": "source1-logic",
  "classes": [
    {
      "name": "logic_auto",
      "kind": "point",
      "description": "Logic auto: fires outputs on map spawn, new game, load game, or background map load.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [
        {"name": "OnMapSpawn", "argumentType": "void", "description": "Fired when the map spawns."},
        {"name": "OnNewGame", "argumentType": "void", "description": "Fired on a new game."},
        {"name": "OnLoadGame", "argumentType": "void", "description": "Fired when a save is loaded."},
        {"name": "OnBackgroundMap", "argumentType": "void", "description": "Fired when the map loads as a background map."},
        {"name": "OnMultiNewMap", "argumentType": "void", "description": "Fired on a multiplayer new map."},
        {"name": "OnMultiNewRound", "argumentType": "void", "description": "Fired on a multiplayer new round."}
      ],
      "helpers": [{"kind": "origin"}]
    },
    {
      "name": "logic_relay",
      "kind": "point",
      "description": "Logic relay: fires its OnTrigger output when triggered. Used to chain logic together.",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "StartDisabled", "type": "int", "default": "0", "description": "Start disabled. 0 = off, 1 = on."}
      ],
      "inputs": [
        {"name": "Trigger", "argumentType": "void", "description": "Fire the relay's outputs."},
        {"name": "Enable", "argumentType": "void", "description": "Enable the relay."},
        {"name": "Disable", "argumentType": "void", "description": "Disable the relay."},
        {"name": "Toggle", "argumentType": "void", "description": "Toggle the relay."},
        {"name": "CancelPending", "argumentType": "void", "description": "Cancel any pending Trigger."}
      ],
      "outputs": [
        {"name": "OnTrigger", "argumentType": "void", "description": "Fired when the relay is triggered."},
        {"name": "OnSpawn", "argumentType": "void", "description": "Fired when the relay spawns."}
      ],
      "helpers": [{"kind": "origin"}]
    },
    {
      "name": "logic_timer",
      "kind": "point",
      "description": "Logic timer: fires OnTimer repeatedly at a set interval.",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "RefireTime", "type": "float", "default": "1", "description": "Seconds between timer fires."},
        {"name": "StartDisabled", "type": "int", "default": "0", "description": "Start disabled. 0 = off, 1 = on."},
        {"name": "UseRandomTime", "type": "int", "default": "0", "description": "Use a random interval between LowerRandomBound and UpperRandomBound. 0 = off, 1 = on."},
        {"name": "LowerRandomBound", "type": "float", "default": "0", "description": "Lower bound for random intervals."},
        {"name": "UpperRandomBound", "type": "float", "default": "0", "description": "Upper bound for random intervals."}
      ],
      "inputs": [
        {"name": "Enable", "argumentType": "void", "description": "Enable the timer."},
        {"name": "Disable", "argumentType": "void", "description": "Disable the timer."},
        {"name": "Toggle", "argumentType": "void", "description": "Toggle the timer."},
        {"name": "FireTimer", "argumentType": "void", "description": "Fire the timer immediately."},
        {"name": "RefireTime", "argumentType": "float", "description": "Set the refire interval."}
      ],
      "outputs": [
        {"name": "OnTimer", "argumentType": "void", "description": "Fired every interval."},
        {"name": "OnTimerHigh", "argumentType": "void", "description": "Fired when the timer goes high."},
        {"name": "OnTimerLow", "argumentType": "void", "description": "Fired when the timer goes low."}
      ],
      "helpers": [{"kind": "origin"}]
    },
    {
      "name": "logic_branch",
      "kind": "point",
      "description": "Logic branch: stores a boolean; fires OnTrue or OnFalse when tested.",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "InitialValue", "type": "int", "default": "0", "description": "Initial boolean value (0 or 1)."}
      ],
      "inputs": [
        {"name": "SetValue", "argumentType": "int", "description": "Set the value."},
        {"name": "SetValueTest", "argumentType": "int", "description": "Set the value and test it."},
        {"name": "Toggle", "argumentType": "void", "description": "Toggle the value."},
        {"name": "ToggleTest", "argumentType": "void", "description": "Toggle the value and test it."}
      ],
      "outputs": [
        {"name": "OnTrue", "argumentType": "void", "description": "Fired when the value tests true."},
        {"name": "OnFalse", "argumentType": "void", "description": "Fired when the value tests false."}
      ],
      "helpers": [{"kind": "origin"}]
    },
    {
      "name": "logic_case",
      "kind": "point",
      "description": "Logic case: compares an input value against up to 16 cases and fires the matching OnCase output, or OnDefault.",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "Case01", "type": "string", "default": "", "description": "Value that fires OnCase01."},
        {"name": "Case02", "type": "string", "default": "", "description": "Value that fires OnCase02."},
        {"name": "Case03", "type": "string", "default": "", "description": "Value that fires OnCase03."},
        {"name": "Case04", "type": "string", "default": "", "description": "Value that fires OnCase04."},
        {"name": "Case05", "type": "string", "default": "", "description": "Value that fires OnCase05."},
        {"name": "Case06", "type": "string", "default": "", "description": "Value that fires OnCase06."},
        {"name": "Case07", "type": "string", "default": "", "description": "Value that fires OnCase07."},
        {"name": "Case08", "type": "string", "default": "", "description": "Value that fires OnCase08."},
        {"name": "Case09", "type": "string", "default": "", "description": "Value that fires OnCase09."},
        {"name": "Case10", "type": "string", "default": "", "description": "Value that fires OnCase10."},
        {"name": "Case11", "type": "string", "default": "", "description": "Value that fires OnCase11."},
        {"name": "Case12", "type": "string", "default": "", "description": "Value that fires OnCase12."},
        {"name": "Case13", "type": "string", "default": "", "description": "Value that fires OnCase13."},
        {"name": "Case14", "type": "string", "default": "", "description": "Value that fires OnCase14."},
        {"name": "Case15", "type": "string", "default": "", "description": "Value that fires OnCase15."},
        {"name": "Case16", "type": "string", "default": "", "description": "Value that fires OnCase16."}
      ],
      "inputs": [
        {"name": "InValue", "argumentType": "string", "description": "Value to compare against the cases."},
        {"name": "PickRandom", "argumentType": "void", "description": "Fire a random OnCase output."},
        {"name": "PickRandomShuffle", "argumentType": "void", "description": "Fire a random OnCase output, shuffled."}
      ],
      "outputs": [
        {"name": "OnCase01", "argumentType": "void", "description": "Fired when the input value matches Case01."},
        {"name": "OnCase02", "argumentType": "void", "description": "Fired when the input value matches Case02."},
        {"name": "OnCase03", "argumentType": "void", "description": "Fired when the input value matches Case03."},
        {"name": "OnCase04", "argumentType": "void", "description": "Fired when the input value matches Case04."},
        {"name": "OnCase05", "argumentType": "void", "description": "Fired when the input value matches Case05."},
        {"name": "OnCase06", "argumentType": "void", "description": "Fired when the input value matches Case06."},
        {"name": "OnCase07", "argumentType": "void", "description": "Fired when the input value matches Case07."},
        {"name": "OnCase08", "argumentType": "void", "description": "Fired when the input value matches Case08."},
        {"name": "OnCase09", "argumentType": "void", "description": "Fired when the input value matches Case09."},
        {"name": "OnCase10", "argumentType": "void", "description": "Fired when the input value matches Case10."},
        {"name": "OnCase11", "argumentType": "void", "description": "Fired when the input value matches Case11."},
        {"name": "OnCase12", "argumentType": "void", "description": "Fired when the input value matches Case12."},
        {"name": "OnCase13", "argumentType": "void", "description": "Fired when the input value matches Case13."},
        {"name": "OnCase14", "argumentType": "void", "description": "Fired when the input value matches Case14."},
        {"name": "OnCase15", "argumentType": "void", "description": "Fired when the input value matches Case15."},
        {"name": "OnCase16", "argumentType": "void", "description": "Fired when the input value matches Case16."},
        {"name": "OnDefault", "argumentType": "void", "description": "Fired when the input value matches no case."}
      ],
      "helpers": [{"kind": "origin"}]
    },
    {
      "name": "math_counter",
      "kind": "point",
      "description": "Math counter: stores a number; fires OnHitMax and OnHitMin at its limits. Two Add inputs with max 2 model an AND-gate: it fires OnHitMax only after both inputs arrive.",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "StartValue", "type": "float", "default": "0", "description": "Value the counter starts at."},
        {"name": "min", "type": "float", "default": "0", "description": "Minimum value; reaching it fires OnHitMin."},
        {"name": "max", "type": "float", "default": "0", "description": "Maximum value; reaching it fires OnHitMax."},
        {"name": "StartDisabled", "type": "int", "default": "0", "description": "Start disabled. 0 = off, 1 = on."}
      ],
      "inputs": [
        {"name": "Add", "argumentType": "float", "description": "Add a value to the counter."},
        {"name": "Subtract", "argumentType": "float", "description": "Subtract a value from the counter."},
        {"name": "Divide", "argumentType": "float", "description": "Divide the counter by a value."},
        {"name": "Multiply", "argumentType": "float", "description": "Multiply the counter by a value."},
        {"name": "SetValue", "argumentType": "float", "description": "Set the counter value."},
        {"name": "SetHitMax", "argumentType": "float", "description": "Set the maximum value."},
        {"name": "SetHitMin", "argumentType": "float", "description": "Set the minimum value."},
        {"name": "SetValueNoFire", "argumentType": "float", "description": "Set the counter value without firing outputs."},
        {"name": "GetValue", "argumentType": "void", "description": "Fire OutValue with the current value."}
      ],
      "outputs": [
        {"name": "OnHitMax", "argumentType": "void", "description": "Fired when the counter reaches its maximum."},
        {"name": "OnHitMin", "argumentType": "void", "description": "Fired when the counter reaches its minimum."},
        {"name": "OnChangedFromMin", "argumentType": "void", "description": "Fired when the counter leaves its minimum."},
        {"name": "OnChangedFromMax", "argumentType": "void", "description": "Fired when the counter leaves its maximum."},
        {"name": "OutValue", "argumentType": "float", "description": "Fired with the current value."}
      ],
      "helpers": [{"kind": "origin"}]
    },
    {
      "name": "logic_compare",
      "kind": "point",
      "description": "Logic compare: compares a value against CompareValue and fires the matching output.",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "InitialValue", "type": "float", "default": "0", "description": "Value to compare."},
        {"name": "CompareValue", "type": "float", "default": "0", "description": "Value to compare against."}
      ],
      "inputs": [
        {"name": "SetValue", "argumentType": "float", "description": "Set the value to compare."},
        {"name": "SetCompareValue", "argumentType": "float", "description": "Set the value to compare against."},
        {"name": "Compare", "argumentType": "void", "description": "Compare the values and fire the matching output."},
        {"name": "SetValueNoFire", "argumentType": "float", "description": "Set the value without firing outputs."}
      ],
      "outputs": [
        {"name": "OnEqualTo", "argumentType": "void", "description": "Fired when the value equals CompareValue."},
        {"name": "OnNotEqualTo", "argumentType": "void", "description": "Fired when the value does not equal CompareValue."},
        {"name": "OnGreaterThan", "argumentType": "void", "description": "Fired when the value is greater than CompareValue."},
        {"name": "OnLessThan", "argumentType": "void", "description": "Fired when the value is less than CompareValue."}
      ],
      "helpers": [{"kind": "origin"}]
    },
    {
      "name": "env_global",
      "kind": "point",
      "description": "Env global: controls a global state flag shared across maps.",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "GlobalState", "type": "string", "default": "", "description": "Name of the global state flag."},
        {"name": "InitialState", "type": "int", "default": "0", "description": "Initial state: 0 = off, 1 = on, 2 = dead."}
      ],
      "inputs": [
        {"name": "TurnOn", "argumentType": "void", "description": "Turn the global state on."},
        {"name": "TurnOff", "argumentType": "void", "description": "Turn the global state off."},
        {"name": "Toggle", "argumentType": "void", "description": "Toggle the global state."},
        {"name": "Remove", "argumentType": "void", "description": "Remove the global state."}
      ],
      "outputs": [],
      "helpers": [{"kind": "origin"}]
    },
    {
      "name": "point_template",
      "kind": "point",
      "description": "Point template: spawns copies of up to 16 template entities.",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "Template01", "type": "string", "default": "", "description": "First template entity to spawn."},
        {"name": "Template02", "type": "string", "default": "", "description": "Second template entity to spawn."},
        {"name": "Template03", "type": "string", "default": "", "description": "Third template entity to spawn."},
        {"name": "Template04", "type": "string", "default": "", "description": "Fourth template entity to spawn."},
        {"name": "Template05", "type": "string", "default": "", "description": "Fifth template entity to spawn."},
        {"name": "Template06", "type": "string", "default": "", "description": "Sixth template entity to spawn."},
        {"name": "Template07", "type": "string", "default": "", "description": "Seventh template entity to spawn."},
        {"name": "Template08", "type": "string", "default": "", "description": "Eighth template entity to spawn."},
        {"name": "Template09", "type": "string", "default": "", "description": "Ninth template entity to spawn."},
        {"name": "Template10", "type": "string", "default": "", "description": "Tenth template entity to spawn."},
        {"name": "Template11", "type": "string", "default": "", "description": "Eleventh template entity to spawn."},
        {"name": "Template12", "type": "string", "default": "", "description": "Twelfth template entity to spawn."},
        {"name": "Template13", "type": "string", "default": "", "description": "Thirteenth template entity to spawn."},
        {"name": "Template14", "type": "string", "default": "", "description": "Fourteenth template entity to spawn."},
        {"name": "Template15", "type": "string", "default": "", "description": "Fifteenth template entity to spawn."},
        {"name": "Template16", "type": "string", "default": "", "description": "Sixteenth template entity to spawn."}
      ],
      "inputs": [
        {"name": "ForceSpawn", "argumentType": "void", "description": "Spawn the template entities."},
        {"name": "ForceSpawnAtEntityOrigin", "argumentType": "void", "description": "Spawn the template entities at this entity's origin."}
      ],
      "outputs": [],
      "helpers": [{"kind": "origin"}]
    }
  ]
}
""";
}

/// <summary>
/// Pure model of a math_counter's value: Add/Subtract/SetValue clamp to [Min, Max]
/// and report which limit outputs fired on each change (OnHitMax / OnHitMin /
/// OnChangedFromMax / OnChangedFromMin). This is what makes a math_counter an
/// AND-gate: with StartValue 0 and Max = N, OnHitMax fires only after N Adds.
/// No engine, no map — the counter state alone.
/// </summary>
public sealed class MathCounterState
{
	public double StartValue { get; }
	public double Min { get; }
	public double Max { get; }
	public double Value { get; private set; }
	public IReadOnlyList<string> LastOutputsFired { get; private set; } = Array.Empty<string>();

	public MathCounterState( double startValue = 0, double min = 0, double max = 0 )
	{
		if ( !double.IsFinite( startValue ) || !double.IsFinite( min ) || !double.IsFinite( max ) )
			throw new ArgumentException( "math_counter start, min and max must be finite numbers." );
		if ( max < min ) throw new ArgumentException( "math_counter max must be >= min." );
		StartValue = startValue;
		Min = min;
		Max = max;
		Value = Math.Clamp( startValue, min, max );
	}

	/// <summary>An AND-gate over <paramref name="requiredInputs"/> inputs: fires OnHitMax only after that many Adds.</summary>
	public static MathCounterState AndGate( int requiredInputs )
	{
		if ( requiredInputs < 1 ) throw new ArgumentException( "an AND-gate needs at least 1 input.", nameof( requiredInputs ) );
		return new MathCounterState( 0, 0, requiredInputs );
	}

	public bool AtMax => Value >= Max;
	public bool AtMin => Value <= Min;

	public void Add( double amount ) => Apply( Value + amount );
	public void Subtract( double amount ) => Apply( Value - amount );

	public void SetValue( double value ) => Apply( value );

	public void Reset()
	{
		Value = Math.Clamp( StartValue, Min, Max );
		LastOutputsFired = Array.Empty<string>();
	}

	void Apply( double next )
	{
		double previous = Value;
		Value = Math.Clamp( next, Min, Max );
		var fired = new List<string>();
		if ( Value >= Max && previous < Max ) fired.Add( "OnHitMax" );
		if ( Value <= Min && previous > Min ) fired.Add( "OnHitMin" );
		if ( previous <= Min && Value > Min ) fired.Add( "OnChangedFromMin" );
		if ( previous >= Max && Value < Max ) fired.Add( "OnChangedFromMax" );
		LastOutputsFired = fired;
	}
}
