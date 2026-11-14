// LinkedGroups — linked groups for Hammer maps: edit one copy, all update.
// Board: hammer-instances-linked-groups (Geppetto Desktop, Hammer P2).
// TrenchBroom-style, NATIVE Geppetto: a linked group is a document concept
// distinct from both dumb groups (layers ticket) and file instances
// (sprinkles/prefabs ticket).
//
// MODEL. One LinkedGroupDefinition holds the shared members (brushes and
// entities, in definition-local inches/Z-up coordinates). Every
// LinkedGroupInstance is a placement of that definition: an id, the shared
// definition id, and an offset. Instances store NO member copies —
// Resolve() rebuilds the members from the definition plus the offset, so
// EditMember on the definition propagates to every linked copy by
// construction. Unlink() snapshots one instance's resolved members into an
// IndependentGroup and drops the link; later definition edits no longer
// touch it, and the other copies stay linked.
//
// API (exactly as filed in the ticket desc):
//   DefineLinkedGroup / LinkCopy / EditMember (propagates) / Unlink.
// EditMemberViaInstance is the same propagation reached through a copy:
// edit a member in ONE linked copy and every copy updates.
//
// DEPENDENCY STATUS (2026-11-13, reported to the coordinator). Both
// dependencies are still `todo` with no files on disk: MapLayers
// (hammer-selection-grouping-layers-visgroups-model) and MapInstances
// (hammer-sprinkles-prefabs-instances) do not exist, and neither does the
// MapDocument they would sit on (hammer-map-document-model, in-progress
// with another worker). Referencing types that do not exist would break the
// shared tree's compile for every worker, so this file is SELF-CONTAINED:
// members are plain data (brush vertices + material, entity origin +
// keyvalues) and the store is LinkedGroupSet, which MapDocument will own
// the way it owns EntityIoList (ToJson embedded verbatim). The seams for
// the siblings are explicit and narrow:
//   * Layers/visgroups: definitions and instances each carry Layer and
//     VisGroup names; a copy may sit in a different layer/visgroup than
//     its definition. VisibleInstances(hiddenVisGroups) is the filter the
//     layers ticket's CompileVisibleOnly runner consumes.
//   * Instances/VMF: ToInstanceRecords() emits one record per linked copy
//     carrying the SHARED definition id; VmfIo (sibling-owned) writes one
//     func_instance-style block per record. FromInstanceRecords() rebuilds
//     instances against definitions already in the set.
//   * MapBrush/MapEntities adapters (when those types land): a brush member
//     converts to/from MapBrush via its vertices + material; an entity
//     member via its classname/targetname keyvalues. Until then this file
//     compiles and tests stand alone, following the MapCsg precedent.
//
// RESULTS ARE DATA, NOT EXCEPTIONS. A refused operation returns a result
// with Ok false and a machine-readable Reason slug plus a human Message.
// FromJson throws ArgumentException naming the bad field (the EntityIo
// pattern) because a corrupt document is a load error, not a refusal.
//
// NO DESKTOP UI. Backend / data-model / API / headless only, per Pooh
// 2026-11-13.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Effigy.Map;

/// <summary>What a linked-group member is: a brush solid or a point entity.</summary>
public enum LinkedGroupMemberKind
{
	/// <summary>A brush: vertices + a material.</summary>
	Brush,

	/// <summary>A point entity: origin + keyvalues.</summary>
	Entity,
}

/// <summary>
/// One member of a linked-group definition, in definition-local coordinates
/// (kernel inches, Z-up). Brushes carry <see cref="Vertices"/> + <see cref="Material"/>;
/// entities carry <see cref="Origin"/> + <see cref="KeyValues"/>.
/// </summary>
public sealed class LinkedGroupMember
{
	/// <summary>Member id, unique within its definition.</summary>
	public string Id { get; set; } = "";

	/// <summary>Brush or entity.</summary>
	public LinkedGroupMemberKind Kind { get; set; } = LinkedGroupMemberKind.Brush;

	/// <summary>Display name. Need not be unique; <see cref="Id"/> is the identity.</summary>
	public string Name { get; set; } = "";

	/// <summary>Brush material (e.g. "dev/dev_measuregeneric01"). Empty for entities.</summary>
	public string Material { get; set; } = "";

	/// <summary>Brush vertices in definition-local space. Empty for entities.</summary>
	public List<Vec3> Vertices { get; } = new();

	/// <summary>Entity origin in definition-local space. Ignored for brushes.</summary>
	public Vec3 Origin { get; set; } = Vec3.Zero;

	/// <summary>Entity keyvalues (classname, targetname, ...). Brushes leave this empty.</summary>
	public Dictionary<string, string> KeyValues { get; } = new( StringComparer.Ordinal );

	public LinkedGroupMember Clone()
	{
		var copy = new LinkedGroupMember
		{
			Id = Id,
			Kind = Kind,
			Name = Name,
			Material = Material,
			Origin = Origin,
		};
		copy.Vertices.AddRange( Vertices );
		foreach ( var kv in KeyValues ) copy.KeyValues[kv.Key] = kv.Value;
		return copy;
	}

	/// <summary>Copy moved by <paramref name="delta"/>: brush vertices and entity origin shift.</summary>
	public LinkedGroupMember Translated( Vec3 delta )
	{
		var copy = Clone();
		for ( int i = 0; i < copy.Vertices.Count; i++ ) copy.Vertices[i] = copy.Vertices[i] + delta;
		copy.Origin = copy.Origin + delta;
		return copy;
	}
}

/// <summary>One edit to a definition member. Built with the static factories; never by hand.</summary>
public sealed class LinkedMemberEdit
{
	/// <summary>What the edit does.</summary>
	public enum EditKind
	{
		/// <summary>Move the member: brush vertices and entity origin shift by Delta.</summary>
		Move,

		/// <summary>Change a brush's material to Value. Brushes only.</summary>
		SetMaterial,

		/// <summary>Set one entity keyvalue: Key = Value. Entities only.</summary>
		SetKeyValue,

		/// <summary>Rename the member to Value. Any kind.</summary>
		Rename,
	}

	public EditKind Kind { get; private set; }
	public Vec3 Delta { get; private set; } = Vec3.Zero;
	public string Key { get; private set; } = "";
	public string Value { get; private set; } = "";

	/// <summary>Move the member by <paramref name="delta"/> (inches, Z-up).</summary>
	public static LinkedMemberEdit Move( Vec3 delta ) => new() { Kind = EditKind.Move, Delta = delta };

	/// <summary>Change a brush's material to <paramref name="material"/>.</summary>
	public static LinkedMemberEdit SetMaterial( string material ) => new() { Kind = EditKind.SetMaterial, Value = material ?? "" };

	/// <summary>Set entity key <paramref name="key"/> to <paramref name="value"/>.</summary>
	public static LinkedMemberEdit SetKeyValue( string key, string value ) => new() { Kind = EditKind.SetKeyValue, Key = key ?? "", Value = value ?? "" };

	/// <summary>Rename the member to <paramref name="name"/>.</summary>
	public static LinkedMemberEdit Rename( string name ) => new() { Kind = EditKind.Rename, Value = name ?? "" };

	/// <summary>
	/// Applies this edit to <paramref name="member"/> in place.
	/// Returns (true, "") on success, or (false, reason-slug) when the edit
	/// does not apply to this member kind. Never throws for a bad edit.
	/// </summary>
	public (bool Ok, string Reason) ApplyTo( LinkedGroupMember member )
	{
		if ( member is null ) return (false, "linkedgroups-member-null");
		switch ( Kind )
		{
			case EditKind.Move:
				for ( int i = 0; i < member.Vertices.Count; i++ ) member.Vertices[i] = member.Vertices[i] + Delta;
				member.Origin = member.Origin + Delta;
				return (true, "");
			case EditKind.SetMaterial:
				if ( member.Kind != LinkedGroupMemberKind.Brush ) return (false, "linkedgroups-edit-not-applicable");
				member.Material = Value;
				return (true, "");
			case EditKind.SetKeyValue:
				if ( member.Kind != LinkedGroupMemberKind.Entity ) return (false, "linkedgroups-edit-not-applicable");
				if ( string.IsNullOrWhiteSpace( Key ) ) return (false, "linkedgroups-invalid-key");
				member.KeyValues[Key] = Value;
				return (true, "");
			case EditKind.Rename:
				if ( string.IsNullOrWhiteSpace( Value ) ) return (false, "linkedgroups-invalid-name");
				member.Name = Value;
				return (true, "");
			default:
				return (false, "linkedgroups-unknown-edit");
		}
	}
}

/// <summary>The shared definition: the single source of truth for every linked copy.</summary>
public sealed class LinkedGroupDefinition
{
	/// <summary>Definition id (e.g. "lg-1"). Shared by every copy; this is the id a VMF round-trip preserves.</summary>
	public string Id { get; set; } = "";

	/// <summary>Display name.</summary>
	public string Name { get; set; } = "";

	/// <summary>Home layer ("" = default/world layer). Copies may sit in other layers.</summary>
	public string Layer { get; set; } = "";

	/// <summary>Home visgroup ("" = none).</summary>
	public string VisGroup { get; set; } = "";

	/// <summary>Members, in definition order.</summary>
	public List<LinkedGroupMember> Members { get; } = new();

	/// <summary>Finds a member by id, or null.</summary>
	public LinkedGroupMember? FindMember( string memberId ) =>
		Members.FirstOrDefault( m => string.Equals( m.Id, memberId, StringComparison.Ordinal ) );

	public LinkedGroupDefinition Clone()
	{
		var copy = new LinkedGroupDefinition { Id = Id, Name = Name, Layer = Layer, VisGroup = VisGroup };
		foreach ( var m in Members ) copy.Members.Add( m.Clone() );
		return copy;
	}
}

/// <summary>
/// One linked copy: a placement of a definition. Stores NO member copies —
/// <see cref="LinkedGroupSet.Resolve"/> rebuilds them from the definition
/// plus <see cref="Offset"/>, which is what makes edits propagate.
/// </summary>
public sealed class LinkedGroupInstance
{
	/// <summary>Instance id (e.g. "lgi-1").</summary>
	public string Id { get; set; } = "";

	/// <summary>The definition this copy is linked to. Shared across copies of the same definition.</summary>
	public string DefinitionId { get; set; } = "";

	/// <summary>Placement offset (inches, Z-up) applied to every resolved member.</summary>
	public Vec3 Offset { get; set; } = Vec3.Zero;

	/// <summary>Layer this copy sits in ("" = the definition's layer).</summary>
	public string Layer { get; set; } = "";

	/// <summary>Visgroup this copy sits in ("" = the definition's visgroup).</summary>
	public string VisGroup { get; set; } = "";

	/// <summary>Effective layer: the copy's own layer, or the definition's when the copy sets none.</summary>
	public string EffectiveLayer( LinkedGroupDefinition definition ) =>
		string.IsNullOrEmpty( Layer ) ? definition.Layer : Layer;

	/// <summary>Effective visgroup: the copy's own visgroup, or the definition's when the copy sets none.</summary>
	public string EffectiveVisGroup( LinkedGroupDefinition definition ) =>
		string.IsNullOrEmpty( VisGroup ) ? definition.VisGroup : VisGroup;
}

/// <summary>
/// A former linked copy, converted by <see cref="LinkedGroupSet.Unlink"/>.
/// Holds its own snapshot of the resolved members (offset already applied),
/// so later definition edits never touch it. Provenance is
/// <see cref="SourceDefinitionId"/>.
/// </summary>
public sealed class IndependentGroup
{
	/// <summary>Group id: the unlinked instance's id, kept so references survive the unlink.</summary>
	public string Id { get; set; } = "";

	/// <summary>Display name: the definition's name at unlink time.</summary>
	public string Name { get; set; } = "";

	/// <summary>The definition this copy was unlinked from.</summary>
	public string SourceDefinitionId { get; set; } = "";

	/// <summary>Layer the copy sat in.</summary>
	public string Layer { get; set; } = "";

	/// <summary>Visgroup the copy sat in.</summary>
	public string VisGroup { get; set; } = "";

	/// <summary>Snapshot members, with the instance offset already applied.</summary>
	public List<LinkedGroupMember> Members { get; } = new();

	public IndependentGroup Clone()
	{
		var copy = new IndependentGroup
		{
			Id = Id,
			Name = Name,
			SourceDefinitionId = SourceDefinitionId,
			Layer = Layer,
			VisGroup = VisGroup,
		};
		foreach ( var m in Members ) copy.Members.Add( m.Clone() );
		return copy;
	}
}

/// <summary>One VMF-bound instance record: a linked copy as the VMF writer sees it.</summary>
public sealed class LinkedGroupInstanceRecord
{
	public string InstanceId { get; set; } = "";
	public string DefinitionId { get; set; } = "";
	public float OffsetX { get; set; }
	public float OffsetY { get; set; }
	public float OffsetZ { get; set; }
	public string Layer { get; set; } = "";
	public string VisGroup { get; set; } = "";

	/// <summary>Placement offset as a vector.</summary>
	public Vec3 Offset => new( OffsetX, OffsetY, OffsetZ );
}

/// <summary>Base result for linked-group operations: data, not exceptions.</summary>
public class LinkedGroupResult
{
	public bool Ok { get; protected set; }
	public string Reason { get; protected set; } = "";
	public string Message { get; protected set; } = "";

	protected LinkedGroupResult() { }

	public static LinkedGroupResult Success() => new() { Ok = true };
	public static LinkedGroupResult Failure( string reason, string message ) => new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>Result of <see cref="LinkedGroupSet.DefineLinkedGroup"/>.</summary>
public sealed class DefineLinkedGroupResult : LinkedGroupResult
{
	public LinkedGroupDefinition? Definition { get; private set; }

	public static DefineLinkedGroupResult Success( LinkedGroupDefinition definition ) =>
		new() { Ok = true, Definition = definition };

	public static new DefineLinkedGroupResult Failure( string reason, string message ) =>
		new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>Result of <see cref="LinkedGroupSet.LinkCopy"/>.</summary>
public sealed class LinkCopyResult : LinkedGroupResult
{
	public LinkedGroupInstance? Instance { get; private set; }

	public static LinkCopyResult Success( LinkedGroupInstance instance ) =>
		new() { Ok = true, Instance = instance };

	public static new LinkCopyResult Failure( string reason, string message ) =>
		new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>Result of <see cref="LinkedGroupSet.EditMember"/>: propagation is by construction, so this reports how many linked copies the edit reached.</summary>
public sealed class EditMemberResult : LinkedGroupResult
{
	/// <summary>Linked copies of the edited definition at edit time (all of them now resolve the edit).</summary>
	public int AffectedInstances { get; private set; }

	/// <summary>Ids of the affected copies.</summary>
	public IReadOnlyList<string> AffectedInstanceIds { get; private set; } = Array.Empty<string>();

	public static EditMemberResult Success( IReadOnlyList<string> affectedIds ) =>
		new() { Ok = true, AffectedInstances = affectedIds.Count, AffectedInstanceIds = affectedIds };

	public static new EditMemberResult Failure( string reason, string message ) =>
		new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>Result of <see cref="LinkedGroupSet.Unlink"/>.</summary>
public sealed class UnlinkResult : LinkedGroupResult
{
	public IndependentGroup? Group { get; private set; }

	public static UnlinkResult Success( IndependentGroup group ) =>
		new() { Ok = true, Group = group };

	public static new UnlinkResult Failure( string reason, string message ) =>
		new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>
/// The document-level store for linked groups: definitions, their linked
/// copies, and groups unlinked into independence. MapDocument owns one of
/// these (hammer-map-document-model) and embeds <see cref="ToJson"/>
/// verbatim, the way it embeds EntityIoList.
/// </summary>
public sealed class LinkedGroupSet
{
	/// <summary>Document section schema version. Bump only with a migration, never silently.</summary>
	public const int SchemaVersion = 1;

	readonly List<LinkedGroupDefinition> _definitions = new();
	readonly List<LinkedGroupInstance> _instances = new();
	readonly List<IndependentGroup> _independent = new();
	int _nextDefinition = 1;
	int _nextInstance = 1;

	public IReadOnlyList<LinkedGroupDefinition> Definitions => _definitions;
	public IReadOnlyList<LinkedGroupInstance> Instances => _instances;
	public IReadOnlyList<IndependentGroup> IndependentGroups => _independent;

	public LinkedGroupDefinition? FindDefinition( string definitionId ) =>
		_definitions.FirstOrDefault( d => string.Equals( d.Id, definitionId, StringComparison.Ordinal ) );

	public LinkedGroupInstance? FindInstance( string instanceId ) =>
		_instances.FirstOrDefault( i => string.Equals( i.Id, instanceId, StringComparison.Ordinal ) );

	public IndependentGroup? FindIndependent( string groupId ) =>
		_independent.FirstOrDefault( g => string.Equals( g.Id, groupId, StringComparison.Ordinal ) );

	/// <summary>Linked copies of a definition, in link order.</summary>
	public IReadOnlyList<LinkedGroupInstance> InstancesOf( string definitionId ) =>
		_instances.Where( i => string.Equals( i.DefinitionId, definitionId, StringComparison.Ordinal ) ).ToList();

	/// <summary>
	/// Defines a linked group from <paramref name="members"/> (deep-copied, so
	/// later caller mutation cannot corrupt the definition). Refuses an empty
	/// member list, a blank name, or duplicate member ids, with a named reason.
	/// </summary>
	public DefineLinkedGroupResult DefineLinkedGroup(
		string name,
		IEnumerable<LinkedGroupMember> members,
		string layer = "",
		string visGroup = "" )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return DefineLinkedGroupResult.Failure( "linkedgroups-invalid-name", "A linked group needs a non-blank name." );
		if ( members is null )
			return DefineLinkedGroupResult.Failure( "linkedgroups-empty-definition", "A linked group needs at least one member." );
		var copied = members.Select( m => m?.Clone() ).ToList();
		if ( copied.Count == 0 || copied.Any( m => m is null ) )
			return DefineLinkedGroupResult.Failure( "linkedgroups-empty-definition", "A linked group needs at least one member." );
		var seen = new HashSet<string>( StringComparer.Ordinal );
		for ( int i = 0; i < copied.Count; i++ )
		{
			var m = copied[i]!;
			if ( string.IsNullOrWhiteSpace( m.Id ) )
			{
				int n = 1;
				while ( seen.Contains( $"m-{n}" ) ) n++;
				m.Id = $"m-{n}";
			}
			if ( !seen.Add( m.Id ) )
				return DefineLinkedGroupResult.Failure( "linkedgroups-duplicate-member-id", $"Member id '{m.Id}' appears twice in the definition." );
		}
		var definition = new LinkedGroupDefinition
		{
			Id = $"lg-{_nextDefinition++}",
			Name = name,
			Layer = layer ?? "",
			VisGroup = visGroup ?? "",
		};
		foreach ( var m in copied ) definition.Members.Add( m! );
		_definitions.Add( definition );
		return DefineLinkedGroupResult.Success( definition );
	}

	/// <summary>
	/// Places a new linked copy of <paramref name="definitionId"/> at
	/// <paramref name="offset"/>. The copy shares the definition id with
	/// every other copy of the same definition; it stores no members of
	/// its own. <paramref name="layer"/> / <paramref name="visGroup"/>
	/// override the definition's placement ("" = inherit).
	/// </summary>
	public LinkCopyResult LinkCopy( string definitionId, Vec3 offset, string layer = "", string visGroup = "" )
	{
		var definition = FindDefinition( definitionId );
		if ( definition is null )
			return LinkCopyResult.Failure( "linkedgroups-definition-not-found", $"No linked-group definition '{definitionId}'." );
		var instance = new LinkedGroupInstance
		{
			Id = $"lgi-{_nextInstance++}",
			DefinitionId = definition.Id,
			Offset = offset,
			Layer = layer ?? "",
			VisGroup = visGroup ?? "",
		};
		_instances.Add( instance );
		return LinkCopyResult.Success( instance );
	}

	/// <summary>
	/// Edits one member of a definition; EVERY linked copy resolves the
	/// edit, because copies read the definition. Returns the affected
	/// copy ids. Refuses an unknown definition/member or an edit that does
	/// not apply to the member kind (e.g. SetMaterial on an entity), and
	/// in that case changes nothing.
	/// </summary>
	public EditMemberResult EditMember( string definitionId, string memberId, LinkedMemberEdit edit )
	{
		var definition = FindDefinition( definitionId );
		if ( definition is null )
			return EditMemberResult.Failure( "linkedgroups-definition-not-found", $"No linked-group definition '{definitionId}'." );
		var member = definition.FindMember( memberId );
		if ( member is null )
			return EditMemberResult.Failure( "linkedgroups-member-not-found", $"Definition '{definitionId}' has no member '{memberId}'." );
		if ( edit is null )
			return EditMemberResult.Failure( "linkedgroups-unknown-edit", "No edit was supplied." );
		var (ok, reason) = edit.ApplyTo( member );
		if ( !ok )
			return EditMemberResult.Failure( reason, $"Edit {edit.Kind} does not apply to member '{memberId}' ({member.Kind})." );
		var affected = InstancesOf( definitionId ).Select( i => i.Id ).ToList();
		return EditMemberResult.Success( affected );
	}

	/// <summary>
	/// Edits a member as seen through ONE linked copy (<paramref name="instanceId"/>):
	/// the edit lands on that copy's definition, so every copy updates —
	/// this is the TrenchBroom "edit one instance, all update" path.
	/// Refuses an unknown or already-unlinked instance.
	/// </summary>
	public EditMemberResult EditMemberViaInstance( string instanceId, string memberId, LinkedMemberEdit edit )
	{
		var instance = FindInstance( instanceId );
		if ( instance is null )
			return EditMemberResult.Failure( "linkedgroups-instance-not-found", $"No linked copy '{instanceId}' (it may have been unlinked)." );
		return EditMember( instance.DefinitionId, memberId, edit );
	}

	/// <summary>
	/// Unlinks one copy: it becomes an <see cref="IndependentGroup"/> holding
	/// a snapshot of its resolved members (offset already applied) and the
	/// link is dropped. Later definition edits never touch it; the other
	/// copies stay linked. The group keeps the instance id, so references
	/// to the copy survive the unlink.
	/// </summary>
	public UnlinkResult Unlink( string instanceId )
	{
		var instance = FindInstance( instanceId );
		if ( instance is null )
			return UnlinkResult.Failure( "linkedgroups-instance-not-found", $"No linked copy '{instanceId}' to unlink." );
		var definition = FindDefinition( instance.DefinitionId );
		if ( definition is null )
			return UnlinkResult.Failure( "linkedgroups-definition-not-found", $"Copy '{instanceId}' points at missing definition '{instance.DefinitionId}'." );
		var resolved = Resolve( instance );
		var group = new IndependentGroup
		{
			Id = instance.Id,
			Name = definition.Name,
			SourceDefinitionId = definition.Id,
			Layer = instance.EffectiveLayer( definition ),
			VisGroup = instance.EffectiveVisGroup( definition ),
		};
		foreach ( var m in resolved ) group.Members.Add( m );
		_instances.Remove( instance );
		_independent.Add( group );
		return UnlinkResult.Success( group );
	}

	/// <summary>
	/// Resolves a linked copy to its members: definition members translated
	/// by the instance offset. Returns fresh clones — mutating the result
	/// never corrupts the definition. Empty when the instance is unknown
	/// (use <see cref="TryResolve"/> for the reason).
	/// </summary>
	public IReadOnlyList<LinkedGroupMember> Resolve( string instanceId )
	{
		var instance = FindInstance( instanceId );
		return instance is null ? Array.Empty<LinkedGroupMember>() : Resolve( instance );
	}

	/// <summary>Resolves a linked copy; false + a named reason when it cannot be resolved.</summary>
	public bool TryResolve( string instanceId, out IReadOnlyList<LinkedGroupMember> members, out string reason )
	{
		var instance = FindInstance( instanceId );
		if ( instance is null )
		{
			members = Array.Empty<LinkedGroupMember>();
			reason = "linkedgroups-instance-not-found";
			return false;
		}
		if ( FindDefinition( instance.DefinitionId ) is null )
		{
			members = Array.Empty<LinkedGroupMember>();
			reason = "linkedgroups-definition-not-found";
			return false;
		}
		members = Resolve( instance );
		reason = "";
		return true;
	}

	IReadOnlyList<LinkedGroupMember> Resolve( LinkedGroupInstance instance )
	{
		var definition = FindDefinition( instance.DefinitionId );
		if ( definition is null ) return Array.Empty<LinkedGroupMember>();
		return definition.Members.Select( m => m.Translated( instance.Offset ) ).ToList();
	}

	/// <summary>Moves a linked copy to another layer. "" clears the override (inherits the definition's layer).</summary>
	public LinkedGroupResult SetInstanceLayer( string instanceId, string layer )
	{
		var instance = FindInstance( instanceId );
		if ( instance is null ) return LinkedGroupResult.Failure( "linkedgroups-instance-not-found", $"No linked copy '{instanceId}'." );
		instance.Layer = layer ?? "";
		return LinkedGroupResult.Success();
	}

	/// <summary>Moves a linked copy to another visgroup. "" clears the override (inherits the definition's visgroup).</summary>
	public LinkedGroupResult SetInstanceVisGroup( string instanceId, string visGroup )
	{
		var instance = FindInstance( instanceId );
		if ( instance is null ) return LinkedGroupResult.Failure( "linkedgroups-instance-not-found", $"No linked copy '{instanceId}'." );
		instance.VisGroup = visGroup ?? "";
		return LinkedGroupResult.Success();
	}

	/// <summary>Linked copies in a visgroup (effective visgroup: the copy's own, else its definition's).</summary>
	public IReadOnlyList<LinkedGroupInstance> InstancesInVisGroup( string visGroup ) =>
		_instances.Where( i =>
		{
			var d = FindDefinition( i.DefinitionId );
			return d is not null && string.Equals( i.EffectiveVisGroup( d ), visGroup, StringComparison.Ordinal );
		} ).ToList();

	/// <summary>Linked copies in a layer (effective layer: the copy's own, else its definition's).</summary>
	public IReadOnlyList<LinkedGroupInstance> InstancesInLayer( string layer ) =>
		_instances.Where( i =>
		{
			var d = FindDefinition( i.DefinitionId );
			return d is not null && string.Equals( i.EffectiveLayer( d ), layer, StringComparison.Ordinal );
		} ).ToList();

	/// <summary>
	/// The compile-visible filter seam for the layers ticket: copies whose
	/// effective visgroup is hidden are excluded. Copies with no visgroup
	/// are always visible.
	/// </summary>
	public IReadOnlyList<LinkedGroupInstance> VisibleInstances( IReadOnlyCollection<string> hiddenVisGroups )
	{
		var hidden = hiddenVisGroups is null
			? new HashSet<string>( StringComparer.Ordinal )
			: new HashSet<string>( hiddenVisGroups, StringComparer.Ordinal );
		return _instances.Where( i =>
		{
			var d = FindDefinition( i.DefinitionId );
			if ( d is null ) return false;
			var vg = i.EffectiveVisGroup( d );
			return string.IsNullOrEmpty( vg ) || !hidden.Contains( vg );
		} ).ToList();
	}

	// ---- VMF seam ----

	/// <summary>
	/// One record per linked copy, each carrying the SHARED definition id.
	/// VmfIo writes one func_instance-style block per record; the shared
	/// definition id is what makes the copies linked after a VMF round-trip
	/// instead of dumb duplicates.
	/// </summary>
	public IReadOnlyList<LinkedGroupInstanceRecord> ToInstanceRecords() =>
		_instances.Select( i => new LinkedGroupInstanceRecord
		{
			InstanceId = i.Id,
			DefinitionId = i.DefinitionId,
			OffsetX = i.Offset.x,
			OffsetY = i.Offset.y,
			OffsetZ = i.Offset.z,
			Layer = i.Layer,
			VisGroup = i.VisGroup,
		} ).ToList();

	/// <summary>
	/// Rebuilds linked copies from VMF instance records against definitions
	/// already in this set. A record pointing at a missing definition, or a
	/// duplicate instance id, fails the whole rebuild with a named reason
	/// and changes nothing.
	/// </summary>
	public LinkedGroupResult FromInstanceRecords( IEnumerable<LinkedGroupInstanceRecord> records )
	{
		if ( records is null ) return LinkedGroupResult.Failure( "linkedgroups-records-null", "No instance records were supplied." );
		var rows = records.ToList();
		foreach ( var r in rows )
		{
			if ( r is null ) return LinkedGroupResult.Failure( "linkedgroups-record-null", "An instance record was null." );
			if ( FindDefinition( r.DefinitionId ) is null )
				return LinkedGroupResult.Failure( "linkedgroups-definition-not-found", $"Instance record '{r.InstanceId}' points at missing definition '{r.DefinitionId}'." );
			if ( FindInstance( r.InstanceId ) is not null || FindIndependent( r.InstanceId ) is not null )
				return LinkedGroupResult.Failure( "linkedgroups-duplicate-instance-id", $"Instance id '{r.InstanceId}' is already in use." );
		}
		foreach ( var r in rows )
		{
			_instances.Add( new LinkedGroupInstance
			{
				Id = r.InstanceId,
				DefinitionId = r.DefinitionId,
				Offset = r.Offset,
				Layer = r.Layer ?? "",
				VisGroup = r.VisGroup ?? "",
			} );
			// Keep generated ids unique past any restored id of the form lgi-N.
			if ( r.InstanceId.StartsWith( "lgi-", StringComparison.Ordinal )
				&& int.TryParse( r.InstanceId.AsSpan( 4 ), out int n ) && n >= _nextInstance )
				_nextInstance = n + 1;
		}
		return LinkedGroupResult.Success();
	}

	// ---- JSON (map-document section, version 1) ----

	sealed class Vec3Json
	{
		public float x { get; set; }
		public float y { get; set; }
		public float z { get; set; }
	}

	sealed class MemberJson
	{
		public string id { get; set; } = "";
		public string kind { get; set; } = "";
		public string name { get; set; } = "";
		public string material { get; set; } = "";
		public Vec3Json origin { get; set; } = new();
		public List<Vec3Json> vertices { get; set; } = new();
		public Dictionary<string, string> key_values { get; set; } = new();
	}

	sealed class DefinitionJson
	{
		public string id { get; set; } = "";
		public string name { get; set; } = "";
		public string layer { get; set; } = "";
		public string visgroup { get; set; } = "";
		public List<MemberJson> members { get; set; } = new();
	}

	sealed class InstanceJson
	{
		public string id { get; set; } = "";
		public string definition_id { get; set; } = "";
		public Vec3Json offset { get; set; } = new();
		public string layer { get; set; } = "";
		public string visgroup { get; set; } = "";
	}

	sealed class IndependentJson
	{
		public string id { get; set; } = "";
		public string name { get; set; } = "";
		public string source_definition_id { get; set; } = "";
		public string layer { get; set; } = "";
		public string visgroup { get; set; } = "";
		public List<MemberJson> members { get; set; } = new();
	}

	sealed class SetJson
	{
		public int version { get; set; } = 1;
		public int next_definition { get; set; }
		public int next_instance { get; set; }
		public List<DefinitionJson> definitions { get; set; } = new();
		public List<InstanceJson> instances { get; set; } = new();
		public List<IndependentJson> independent { get; set; } = new();
	}

	static Vec3Json ToJson( Vec3 v ) => new() { x = v.x, y = v.y, z = v.z };
	static Vec3 FromJson( Vec3Json v ) => v is null ? Vec3.Zero : new Vec3( v.x, v.y, v.z );

	static MemberJson ToJson( LinkedGroupMember m ) => new()
	{
		id = m.Id,
		kind = m.Kind == LinkedGroupMemberKind.Entity ? "entity" : "brush",
		name = m.Name,
		material = m.Material,
		origin = ToJson( m.Origin ),
		vertices = m.Vertices.Select( ToJson ).ToList(),
		key_values = new Dictionary<string, string>( m.KeyValues ),
	};

	static LinkedGroupMember FromJson( MemberJson j )
	{
		if ( j is null ) throw new ArgumentException( "linked_groups member row is null." );
		if ( string.IsNullOrWhiteSpace( j.id ) ) throw new ArgumentException( "linked_groups member is missing 'id'." );
		if ( j.kind != "brush" && j.kind != "entity" ) throw new ArgumentException( $"linked_groups member '{j.id}' has unknown kind '{j.kind}'." );
		var m = new LinkedGroupMember
		{
			Id = j.id,
			Kind = j.kind == "entity" ? LinkedGroupMemberKind.Entity : LinkedGroupMemberKind.Brush,
			Name = j.name ?? "",
			Material = j.material ?? "",
			Origin = FromJson( j.origin ),
		};
		foreach ( var v in j.vertices ?? new List<Vec3Json>() ) m.Vertices.Add( FromJson( v ) );
		foreach ( var kv in j.key_values ?? new Dictionary<string, string>() ) m.KeyValues[kv.Key] = kv.Value;
		return m;
	}

	/// <summary>Serialises this set as the "linked_groups" section of a map document (version 1). MapDocument embeds this verbatim.</summary>
	public string ToJson()
	{
		var doc = new SetJson
		{
			next_definition = _nextDefinition,
			next_instance = _nextInstance,
			definitions = _definitions.Select( d => new DefinitionJson
			{
				id = d.Id,
				name = d.Name,
				layer = d.Layer,
				visgroup = d.VisGroup,
				members = d.Members.Select( ToJson ).ToList(),
			} ).ToList(),
			instances = _instances.Select( i => new InstanceJson
			{
				id = i.Id,
				definition_id = i.DefinitionId,
				offset = ToJson( i.Offset ),
				layer = i.Layer,
				visgroup = i.VisGroup,
			} ).ToList(),
			independent = _independent.Select( g => new IndependentJson
			{
				id = g.Id,
				name = g.Name,
				source_definition_id = g.SourceDefinitionId,
				layer = g.Layer,
				visgroup = g.VisGroup,
				members = g.Members.Select( ToJson ).ToList(),
			} ).ToList(),
		};
		return JsonSerializer.Serialize( doc, new JsonSerializerOptions { WriteIndented = true } );
	}

	/// <summary>Reads back <see cref="ToJson"/> output. Throws <see cref="ArgumentException"/> naming the bad field.</summary>
	public static LinkedGroupSet FromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) throw new ArgumentException( "linked_groups JSON is empty.", nameof( json ) );
		SetJson? doc;
		try { doc = JsonSerializer.Deserialize<SetJson>( json ); }
		catch ( JsonException ex ) { throw new ArgumentException( $"linked_groups JSON is not valid JSON: {ex.Message}", nameof( json ), ex ); }
		if ( doc is null ) throw new ArgumentException( "linked_groups JSON is empty.", nameof( json ) );
		if ( doc.version != 1 ) throw new ArgumentException( $"unsupported linked_groups version {doc.version}; expected 1.", nameof( json ) );
		var set = new LinkedGroupSet
		{
			_nextDefinition = Math.Max( 1, doc.next_definition ),
			_nextInstance = Math.Max( 1, doc.next_instance ),
		};
		var definitionIds = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var dj in doc.definitions ?? new List<DefinitionJson>() )
		{
			if ( dj is null ) throw new ArgumentException( "linked_groups definition row is null.", nameof( json ) );
			if ( string.IsNullOrWhiteSpace( dj.id ) ) throw new ArgumentException( "linked_groups definition is missing 'id'.", nameof( json ) );
			if ( !definitionIds.Add( dj.id ) ) throw new ArgumentException( $"linked_groups definition id '{dj.id}' appears twice.", nameof( json ) );
			if ( string.IsNullOrWhiteSpace( dj.name ) ) throw new ArgumentException( $"linked_groups definition '{dj.id}' is missing 'name'.", nameof( json ) );
			var definition = new LinkedGroupDefinition
			{
				Id = dj.id,
				Name = dj.name,
				Layer = dj.layer ?? "",
				VisGroup = dj.visgroup ?? "",
			};
			var memberIds = new HashSet<string>( StringComparer.Ordinal );
			foreach ( var mj in dj.members ?? new List<MemberJson>() )
			{
				var member = FromJson( mj );
				if ( !memberIds.Add( member.Id ) )
					throw new ArgumentException( $"linked_groups definition '{dj.id}' has duplicate member id '{member.Id}'.", nameof( json ) );
				definition.Members.Add( member );
			}
			if ( definition.Members.Count == 0 )
				throw new ArgumentException( $"linked_groups definition '{dj.id}' has no members.", nameof( json ) );
			set._definitions.Add( definition );
			if ( dj.id.StartsWith( "lg-", StringComparison.Ordinal )
				&& int.TryParse( dj.id.AsSpan( 3 ), out int n ) && n >= set._nextDefinition )
				set._nextDefinition = n + 1;
		}
		var instanceIds = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var ij in doc.instances ?? new List<InstanceJson>() )
		{
			if ( ij is null ) throw new ArgumentException( "linked_groups instance row is null.", nameof( json ) );
			if ( string.IsNullOrWhiteSpace( ij.id ) ) throw new ArgumentException( "linked_groups instance is missing 'id'.", nameof( json ) );
			if ( !instanceIds.Add( ij.id ) ) throw new ArgumentException( $"linked_groups instance id '{ij.id}' appears twice.", nameof( json ) );
			if ( set.FindDefinition( ij.definition_id ) is null )
				throw new ArgumentException( $"linked_groups instance '{ij.id}' points at missing definition '{ij.definition_id}'.", nameof( json ) );
			set._instances.Add( new LinkedGroupInstance
			{
				Id = ij.id,
				DefinitionId = ij.definition_id,
				Offset = FromJson( ij.offset ),
				Layer = ij.layer ?? "",
				VisGroup = ij.visgroup ?? "",
			} );
			if ( ij.id.StartsWith( "lgi-", StringComparison.Ordinal )
				&& int.TryParse( ij.id.AsSpan( 4 ), out int n ) && n >= set._nextInstance )
				set._nextInstance = n + 1;
		}
		foreach ( var gj in doc.independent ?? new List<IndependentJson>() )
		{
			if ( gj is null ) throw new ArgumentException( "linked_groups independent-group row is null.", nameof( json ) );
			if ( string.IsNullOrWhiteSpace( gj.id ) ) throw new ArgumentException( "linked_groups independent group is missing 'id'.", nameof( json ) );
			if ( !instanceIds.Add( gj.id ) ) throw new ArgumentException( $"linked_groups independent group id '{gj.id}' collides with an instance id.", nameof( json ) );
			var group = new IndependentGroup
			{
				Id = gj.id,
				Name = gj.name ?? "",
				SourceDefinitionId = gj.source_definition_id ?? "",
				Layer = gj.layer ?? "",
				VisGroup = gj.visgroup ?? "",
			};
			foreach ( var mj in gj.members ?? new List<MemberJson>() ) group.Members.Add( FromJson( mj ) );
			set._independent.Add( group );
		}
		return set;
	}
}
