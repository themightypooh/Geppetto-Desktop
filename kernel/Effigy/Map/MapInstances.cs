// MapInstances — sprinkles/presets + prefabs + func_instance (native).
// Board: hammer-sprinkles-prefabs-instances (Geppetto Desktop, Hammer P2).
// NATIVE Geppetto, no Hammer dependency: instances reference Geppetto-native
// map documents (VMF sub-maps are also readable via the VMF importer); the
// store below binds a sub-map by STRING map id + source path, never by a
// MapDocument reference, because MapDocument does not exist on disk yet.
//
// MODEL. Three things that look alike and are deliberately kept apart:
//
//   * Sprinkle preset — a named set of prop/detail placements (model path +
//     local offset/angles/scale + keyvalues). Placing a preset resolves each
//     item to a world origin/angles pair; the caller (a MapEntities adapter)
//     seats real prop_static/prop_dynamic/prop_physics entities from the
//     placed data. Presets never carry brushes and never own target names
//     beyond the keyvalues they were given.
//   * Prefab — the older copy-paste equivalent: a saved selection of brushes
//     + entities. Instantiating a prefab returns FRESH COPIES translated by
//     the placement offset/yaw; later edits to one instantiation never touch
//     another, and never touch the definition. That is the whole difference
//     from a linked group (LinkedGroups.cs): prefabs copy, linked groups
//     share. Instantiation applies name fix-up (below) to the COPIES.
//   * func_instance — a reference to a reusable sub-map: a MapInstance holds
//     the sub-map's string id + source path, a name prefix, a placement
//     origin/yaw, and a fix-up switch. Resolve() rebuilds the sub-map's
//     members at the placement with target-name fix-up applied, so the main
//     map re-resolves whenever the sub-map is edited in place (edit the
//     registered sub-map content; instances read it live).
//
// NAME FIX-UP (InstanceAuto-style prefixing). On resolve, every targetname
// DEFINED inside the sub-map/prefab gets the instance prefix, and every
// keyvalue that REFERENCES one of those names gets the same prefix — exact
// match, case-insensitive (Source targetnames are; MapEntities and EntityIo
// resolve the same way), plus the comma-first-segment form EntityIo outputs
// use ("Target,Input,Parameter,Delay,Fires"). References to names defined
// OUTSIDE the sub-map are left alone, and '!' engine specials (!self,
// !activator, ...) are never prefixed. The original sub-map/prefab content
// is NEVER renamed: fix-up applies only to resolved copies. A collision is
// fixed by prefixing; a collision that survives prefixing (the prefixed
// name is already taken in the main map) is a named failure, never a silent
// rename of the original. Empty prefix + fix-up on auto-prefixes with the
// instance id ("mi-3_"); fix-up off passes names through and Validate
// reports the collisions instead.
//
// DEPENDENCY STATUS (2026-11-13). MapDocument (hammer-map-document-model,
// worker 12) has NOT landed: no MapDocument.cs on disk. So this file is
// SELF-CONTAINED like LinkedGroups.cs: members are plain data (brush
// vertices + material, entity origin/angles + keyvalues), sub-maps are
// registered in-memory content (RegisterSubMap), and the store is
// MapInstanceStore, which MapDocument will own the way it owns
// EntityIoList/LinkedGroupSet (ToJson embedded verbatim). The seams for
// the siblings are explicit and narrow:
//   * MapDocument: owns one MapInstanceStore; when it lands, the integrator
//     replaces RegisterSubMap with a load from the document / VMF importer
//     (sub-map lookup stays by string id + source path, so no call shape
//     changes). MapInstance.SourcePath is the path the document/VMF loader
//     resolves; SubMapId is the document's id.
//   * MapEntities / PropPlacement: PlacePreset returns PlacedSprinkle data;
//     the adapter seats prop entities through MapEntities.PlacePointEntity
//     + PropPlacement (model-path-exists validation belongs to the
//     AssetRegistries ticket, per PropPlacement.cs). InstanceMember converts
//     to/from MapEntity via classname/targetname keyvalues when that adapter
//     is written; until then this file compiles and tests stand alone.
//   * VmfIo: ToFuncInstanceRecords() emits one record per instance; VmfIo
//     writes one func_instance block per record (file + fixup_name +
//     origin). FromFuncInstanceRecords() rebuilds instances against
//     sub-maps already registered.
//   * Phase 3 (hammer-instances-collaboration-libraries, worker 81) builds
//     on this file: InstanceNameFixUp is the public fix-up engine it reuses
//     for library updates, and MapInstanceStore is the store it versions.
//
// RESULTS ARE DATA, NOT EXCEPTIONS. A refused operation returns a result
// with Ok false and a machine-readable Reason slug plus a human Message.
// FromJson throws ArgumentException naming the bad field (the EntityIo /
// LinkedGroups pattern) because a corrupt document is a load error, not a
// refusal.
//
// NO DESKTOP UI. Backend / data-model / API / headless only, per Pooh
// 2026-11-13. No Hammer, no shelling out to Hammer/Hammer++/VBSP/VVIS/VRAD.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Effigy.Map;

/// <summary>What an instance/prefab member is: a brush solid or a point entity.</summary>
public enum InstanceMemberKind
{
	/// <summary>A brush: vertices + a material.</summary>
	Brush,

	/// <summary>A point entity: origin + angles + keyvalues.</summary>
	Entity,
}

/// <summary>
/// One member of a prefab definition or a sub-map, in definition-local
/// coordinates (kernel inches, Z-up). Brushes carry <see cref="Vertices"/> +
/// <see cref="Material"/>; entities carry <see cref="Origin"/> +
/// <see cref="Angles"/> + <see cref="KeyValues"/>.
/// </summary>
public sealed class InstanceMember
{
	/// <summary>Member id, unique within its definition / sub-map.</summary>
	public string Id { get; set; } = "";

	/// <summary>Brush or entity.</summary>
	public InstanceMemberKind Kind { get; set; } = InstanceMemberKind.Brush;

	/// <summary>Display name. Need not be unique; <see cref="Id"/> is the identity.</summary>
	public string Name { get; set; } = "";

	/// <summary>Brush material (e.g. "dev/dev_measuregeneric01"). Empty for entities.</summary>
	public string Material { get; set; } = "";

	/// <summary>Brush vertices in definition-local space. Empty for entities.</summary>
	public List<Vec3> Vertices { get; } = new();

	/// <summary>Entity origin in definition-local space. Ignored for brushes.</summary>
	public Vec3 Origin { get; set; } = Vec3.Zero;

	/// <summary>Entity angles, pitch/yaw/roll in degrees. Ignored for brushes.</summary>
	public Vec3 Angles { get; set; } = Vec3.Zero;

	/// <summary>Entity keyvalues (classname, targetname, ...). Brushes leave this empty.</summary>
	public Dictionary<string, string> KeyValues { get; } = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>This entity's targetname (keyvalue "targetname"), or "" when unnamed / a brush.</summary>
	public string TargetName => KeyValues.TryGetValue( "targetname", out string? name ) ? name ?? "" : "";

	public InstanceMember Clone()
	{
		var copy = new InstanceMember
		{
			Id = Id,
			Kind = Kind,
			Name = Name,
			Material = Material,
			Origin = Origin,
			Angles = Angles,
		};
		copy.Vertices.AddRange( Vertices );
		foreach ( var kv in KeyValues ) copy.KeyValues[kv.Key] = kv.Value;
		return copy;
	}

	/// <summary>Copy moved by <paramref name="delta"/>: brush vertices and entity origin shift.</summary>
	public InstanceMember Translated( Vec3 delta )
	{
		var copy = Clone();
		for ( int i = 0; i < copy.Vertices.Count; i++ ) copy.Vertices[i] = copy.Vertices[i] + delta;
		copy.Origin = copy.Origin + delta;
		return copy;
	}

	/// <summary>
	/// Copy rotated <paramref name="yawDegrees"/> about the Z axis (definition
	/// origin) and then moved by <paramref name="origin"/>. Entity angles gain
	/// the yaw. This is the instance placement transform; resolve applies it.
	/// </summary>
	public InstanceMember Transformed( Vec3 origin, float yawDegrees )
	{
		var copy = Clone();
		for ( int i = 0; i < copy.Vertices.Count; i++ )
			copy.Vertices[i] = MapInstancesMath.RotateYaw( copy.Vertices[i], yawDegrees ) + origin;
		copy.Origin = MapInstancesMath.RotateYaw( copy.Origin, yawDegrees ) + origin;
		if ( copy.Kind == InstanceMemberKind.Entity )
			copy.Angles = new Vec3( copy.Angles.x, copy.Angles.y + yawDegrees, copy.Angles.z );
		return copy;
	}

	/// <summary>
	/// Inverse of <see cref="Transformed"/>: re-expresses a member that lives
	/// in main-map coordinates as sub-map-local, relative to
	/// <paramref name="origin"/> / <paramref name="yawDegrees"/>. Used by
	/// <see cref="MapInstanceStore.CreateInstanceFromSelection"/> so an
	/// instance placed back at the same origin resolves to the original
	/// selection exactly.
	/// </summary>
	public InstanceMember Rebased( Vec3 origin, float yawDegrees )
	{
		var copy = Clone();
		for ( int i = 0; i < copy.Vertices.Count; i++ )
			copy.Vertices[i] = MapInstancesMath.RotateYaw( copy.Vertices[i] - origin, -yawDegrees );
		copy.Origin = MapInstancesMath.RotateYaw( copy.Origin - origin, -yawDegrees );
		if ( copy.Kind == InstanceMemberKind.Entity )
			copy.Angles = new Vec3( copy.Angles.x, copy.Angles.y - yawDegrees, copy.Angles.z );
		return copy;
	}
}

/// <summary>One edit to an instance/prefab/sub-map member. Built with the static factories; never by hand.</summary>
public sealed class InstanceMemberEdit
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
	public static InstanceMemberEdit Move( Vec3 delta ) => new() { Kind = EditKind.Move, Delta = delta };

	/// <summary>Change a brush's material to <paramref name="material"/>.</summary>
	public static InstanceMemberEdit SetMaterial( string material ) => new() { Kind = EditKind.SetMaterial, Value = material ?? "" };

	/// <summary>Set entity key <paramref name="key"/> to <paramref name="value"/>.</summary>
	public static InstanceMemberEdit SetKeyValue( string key, string value ) => new() { Kind = EditKind.SetKeyValue, Key = key ?? "", Value = value ?? "" };

	/// <summary>Rename the member to <paramref name="name"/>.</summary>
	public static InstanceMemberEdit Rename( string name ) => new() { Kind = EditKind.Rename, Value = name ?? "" };

	/// <summary>
	/// Applies this edit to <paramref name="member"/> in place.
	/// Returns (true, "") on success, or (false, reason-slug) when the edit
	/// does not apply to this member kind. Never throws for a bad edit.
	/// </summary>
	public (bool Ok, string Reason) ApplyTo( InstanceMember member )
	{
		if ( member is null ) return (false, "mapinstances-member-null");
		switch ( Kind )
		{
			case EditKind.Move:
				for ( int i = 0; i < member.Vertices.Count; i++ ) member.Vertices[i] = member.Vertices[i] + Delta;
				member.Origin = member.Origin + Delta;
				return (true, "");
			case EditKind.SetMaterial:
				if ( member.Kind != InstanceMemberKind.Brush ) return (false, "mapinstances-edit-not-applicable");
				member.Material = Value;
				return (true, "");
			case EditKind.SetKeyValue:
				if ( member.Kind != InstanceMemberKind.Entity ) return (false, "mapinstances-edit-not-applicable");
				if ( string.IsNullOrWhiteSpace( Key ) ) return (false, "mapinstances-invalid-key");
				member.KeyValues[Key] = Value;
				return (true, "");
			case EditKind.Rename:
				if ( string.IsNullOrWhiteSpace( Value ) ) return (false, "mapinstances-invalid-name");
				member.Name = Value;
				return (true, "");
			default:
				return (false, "mapinstances-unknown-edit");
		}
	}
}

/// <summary>Yaw-rotation helper shared by resolve and selection rebasing. Z-up: yaw turns +X toward +Y.</summary>
public static class MapInstancesMath
{
	/// <summary>Rotates <paramref name="v"/> about the Z axis by <paramref name="yawDegrees"/>.</summary>
	public static Vec3 RotateYaw( Vec3 v, float yawDegrees )
	{
		if ( yawDegrees == 0f ) return v;
		float radians = yawDegrees * MathF.PI / 180f;
		float c = MathF.Cos( radians ), s = MathF.Sin( radians );
		return new Vec3( v.x * c - v.y * s, v.x * s + v.y * c, v.z );
	}

	/// <summary>True when every component of <paramref name="v"/> is finite.</summary>
	public static bool IsFinite( Vec3 v ) => float.IsFinite( v.x ) && float.IsFinite( v.y ) && float.IsFinite( v.z );
}

/// <summary>
/// The name fix-up engine (InstanceAuto-style prefixing). Public so the
/// Phase 3 libraries ticket reuses exactly this logic for library updates
/// instead of inventing a second prefixer. Pure functions: nothing here
/// mutates its inputs.
/// </summary>
public static class InstanceNameFixUp
{
	/// <summary>
	/// Targetnames defined by <paramref name="members"/> (entity members'
	/// "targetname" keyvalues, blanks excluded), matched case-insensitively.
	/// This is the namespace a resolve may prefix.
	/// </summary>
	public static HashSet<string> DefinedTargetNames( IEnumerable<InstanceMember> members )
	{
		var names = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		if ( members is null ) return names;
		foreach ( var m in members )
		{
			if ( m is null || m.Kind != InstanceMemberKind.Entity ) continue;
			string target = m.TargetName;
			if ( target.Length > 0 && !target.StartsWith( "!", StringComparison.Ordinal ) ) names.Add( target );
		}
		return names;
	}

	/// <summary>
	/// Prefixes one keyvalue <paramref name="value"/> when it references a
	/// name in <paramref name="definedTargets"/>: an exact match, or the
	/// comma-first-segment form EntityIo outputs use
	/// ("Target,Input,Parameter,Delay,Fires"). Values starting with '!'
	/// (engine specials) and values naming nothing defined are returned
	/// untouched. Empty <paramref name="prefix"/> returns the value as-is.
	/// </summary>
	public static string FixUpValue( string value, IReadOnlyCollection<string>? definedTargets, string prefix )
	{
		if ( string.IsNullOrEmpty( prefix ) || string.IsNullOrEmpty( value ) ) return value ?? "";
		if ( value.StartsWith( "!", StringComparison.Ordinal ) ) return value;
		if ( definedTargets is null ) return value;
		int comma = value.IndexOf( ',' );
		if ( comma > 0 )
		{
			// Comma-form IO output: only the head names a target.
			string head = value.Substring( 0, comma );
			return definedTargets.Contains( head ) ? prefix + value : value;
		}
		return definedTargets.Contains( value ) ? prefix + value : value;
	}

	/// <summary>
	/// Returns a fixed-up clone of <paramref name="member"/>: an entity's own
	/// targetname is prefixed, and every other keyvalue is run through
	/// <see cref="FixUpValue"/> (references to names defined in the same
	/// sub-map/prefab move with it; references outward do not). Brushes are
	/// returned as an unmodified clone — they carry no names.
	/// </summary>
	public static InstanceMember FixUpMember( InstanceMember member, IReadOnlyCollection<string> definedTargets, string prefix )
	{
		var copy = member.Clone();
		if ( copy.Kind != InstanceMemberKind.Entity || string.IsNullOrEmpty( prefix ) ) return copy;
		if ( copy.KeyValues.TryGetValue( "targetname", out string? own ) && own.Length > 0
			&& !own.StartsWith( "!", StringComparison.Ordinal ) )
			copy.KeyValues["targetname"] = prefix + own;
		var keys = copy.KeyValues.Keys.ToList();
		foreach ( string key in keys )
		{
			if ( string.Equals( key, "targetname", StringComparison.OrdinalIgnoreCase ) ) continue;
			if ( string.Equals( key, "classname", StringComparison.OrdinalIgnoreCase ) ) continue;
			copy.KeyValues[key] = FixUpValue( copy.KeyValues[key], definedTargets, prefix );
		}
		return copy;
	}

	/// <summary>
	/// Resolved targetnames in <paramref name="members"/> that also appear in
	/// <paramref name="mainMapTargetNames"/> (case-insensitive). Empty when
	/// there is nothing to collide with, or when fix-up already moved every
	/// name out of the way.
	/// </summary>
	public static IReadOnlyList<string> FindCollisions(
		IEnumerable<InstanceMember> members,
		IReadOnlyCollection<string>? mainMapTargetNames )
	{
		var collisions = new List<string>();
		if ( members is null || mainMapTargetNames is null ) return collisions;
		var main = new HashSet<string>( mainMapTargetNames, StringComparer.OrdinalIgnoreCase );
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var m in members )
		{
			if ( m is null || m.Kind != InstanceMemberKind.Entity ) continue;
			string target = m.TargetName;
			if ( target.Length == 0 ) continue;
			if ( main.Contains( target ) && seen.Add( target ) ) collisions.Add( target );
		}
		return collisions;
	}
}

/// <summary>One prop/detail placement inside a sprinkle preset, in preset-local coordinates.</summary>
public sealed class SprinkleItem
{
	/// <summary>Model path, e.g. "models/props_c17/oildrum001.mdl". Required.</summary>
	public string Model { get; set; } = "";

	/// <summary>Entity class to seat as; blank defaults to "prop_static" at define time.</summary>
	public string ClassName { get; set; } = "prop_static";

	/// <summary>Offset from the preset placement origin (inches, Z-up), before yaw.</summary>
	public Vec3 Offset { get; set; } = Vec3.Zero;

	/// <summary>Angles, pitch/yaw/roll in degrees; yaw gains the placement yaw.</summary>
	public Vec3 Angles { get; set; } = Vec3.Zero;

	/// <summary>Uniform scale. Must be finite and &gt; 0.</summary>
	public float Scale { get; set; } = 1f;

	/// <summary>Extra keyvalues for the seated entity (e.g. skin, solid). Never contains "origin".</summary>
	public Dictionary<string, string> KeyValues { get; } = new( StringComparer.OrdinalIgnoreCase );

	public SprinkleItem Clone()
	{
		var copy = new SprinkleItem
		{
			Model = Model,
			ClassName = ClassName,
			Offset = Offset,
			Angles = Angles,
			Scale = Scale,
		};
		foreach ( var kv in KeyValues ) copy.KeyValues[kv.Key] = kv.Value;
		return copy;
	}
}

/// <summary>One seated sprinkle: a preset item resolved to world coordinates by PlacePreset.</summary>
public sealed class PlacedSprinkle
{
	/// <summary>Model path from the preset item.</summary>
	public string Model { get; set; } = "";

	/// <summary>Entity class to seat as ("prop_static" by default).</summary>
	public string ClassName { get; set; } = "prop_static";

	/// <summary>World origin: placement origin + yaw-rotated item offset.</summary>
	public Vec3 Origin { get; set; } = Vec3.Zero;

	/// <summary>World angles: item angles with the placement yaw added.</summary>
	public Vec3 Angles { get; set; } = Vec3.Zero;

	/// <summary>Uniform scale from the preset item.</summary>
	public float Scale { get; set; } = 1f;

	/// <summary>Keyvalues copied from the preset item.</summary>
	public Dictionary<string, string> KeyValues { get; } = new( StringComparer.OrdinalIgnoreCase );
}

/// <summary>A sprinkle preset: a named, reusable set of prop/detail placements.</summary>
public sealed class SprinklePreset
{
	/// <summary>Preset id (e.g. "sp-1").</summary>
	public string Id { get; set; } = "";

	/// <summary>Display name.</summary>
	public string Name { get; set; } = "";

	/// <summary>Items, in preset order.</summary>
	public List<SprinkleItem> Items { get; } = new();

	public SprinklePreset Clone()
	{
		var copy = new SprinklePreset { Id = Id, Name = Name };
		foreach ( var item in Items ) copy.Items.Add( item.Clone() );
		return copy;
	}
}

/// <summary>
/// A prefab: a saved selection of brushes + entities, in prefab-local
/// coordinates. Instantiation copies (never links): see
/// <see cref="MapInstanceStore.InstantiatePrefab"/>.
/// </summary>
public sealed class PrefabDefinition
{
	/// <summary>Prefab id (e.g. "pf-1").</summary>
	public string Id { get; set; } = "";

	/// <summary>Display name.</summary>
	public string Name { get; set; } = "";

	/// <summary>Home layer ("" = default/world layer).</summary>
	public string Layer { get; set; } = "";

	/// <summary>Home visgroup ("" = none).</summary>
	public string VisGroup { get; set; } = "";

	/// <summary>Members, in definition order.</summary>
	public List<InstanceMember> Members { get; } = new();

	/// <summary>Finds a member by id, or null.</summary>
	public InstanceMember? FindMember( string memberId ) =>
		Members.FirstOrDefault( m => string.Equals( m.Id, memberId, StringComparison.Ordinal ) );

	public PrefabDefinition Clone()
	{
		var copy = new PrefabDefinition { Id = Id, Name = Name, Layer = Layer, VisGroup = VisGroup };
		foreach ( var m in Members ) copy.Members.Add( m.Clone() );
		return copy;
	}
}

/// <summary>
/// One prefab instantiation: the fresh, fixed-up copies produced by
/// <see cref="MapInstanceStore.InstantiatePrefab"/>. The copies are owned
/// by the caller (they become ordinary map members once pasted); the store
/// keeps only the definition, so instantiations are not persisted in the
/// store JSON.
/// </summary>
public sealed class PrefabInstance
{
	/// <summary>Instantiation id (e.g. "pfi-1").</summary>
	public string Id { get; set; } = "";

	/// <summary>The prefab this was instantiated from.</summary>
	public string PrefabId { get; set; } = "";

	/// <summary>Prefix applied to the copies' targetnames (auto-generated when the caller passed none).</summary>
	public string Prefix { get; set; } = "";

	/// <summary>Placement origin the copies were translated by.</summary>
	public Vec3 Offset { get; set; } = Vec3.Zero;

	/// <summary>Placement yaw the copies were rotated by.</summary>
	public float YawDegrees { get; set; }

	/// <summary>Resolved copies: translated, yaw-rotated, name-fixed-up.</summary>
	public List<InstanceMember> Members { get; } = new();
}

/// <summary>
/// A reusable sub-map, registered in the store by string id + source path.
/// Until MapDocument lands this is the whole sub-map: members in sub-map-
/// local coordinates. When MapDocument lands, the integrator replaces
/// RegisterSubMap with a load from the document / VMF importer; the id +
/// path binding (and every call shape on the store) stays the same.
/// </summary>
public sealed class SubMapContent
{
	/// <summary>Sub-map id (e.g. "sm-1"): the string id MapInstances bind by.</summary>
	public string Id { get; set; } = "";

	/// <summary>Path to the sub-map file, e.g. "maps/rooms/room_a.vmf" (VMF) or a native map path.</summary>
	public string SourcePath { get; set; } = "";

	/// <summary>Display name.</summary>
	public string Name { get; set; } = "";

	/// <summary>Members, in sub-map-local coordinates, in definition order.</summary>
	public List<InstanceMember> Members { get; } = new();

	/// <summary>Finds a member by id, or null.</summary>
	public InstanceMember? FindMember( string memberId ) =>
		Members.FirstOrDefault( m => string.Equals( m.Id, memberId, StringComparison.Ordinal ) );

	/// <summary>Targetnames defined by this sub-map's entities.</summary>
	public HashSet<string> DefinedTargetNames() => InstanceNameFixUp.DefinedTargetNames( Members );

	public SubMapContent Clone()
	{
		var copy = new SubMapContent { Id = Id, SourcePath = SourcePath, Name = Name };
		foreach ( var m in Members ) copy.Members.Add( m.Clone() );
		return copy;
	}
}

/// <summary>
/// One func_instance: a reference to a reusable sub-map. Stores NO member
/// copies — <see cref="MapInstanceStore.ResolveInstance"/> rebuilds them
/// from the registered sub-map plus <see cref="Origin"/> /
/// <see cref="YawDegrees"/> and the name fix-up, so editing the sub-map
/// in place re-resolves every instance by construction.
/// </summary>
public sealed class MapInstance
{
	/// <summary>Instance id (e.g. "mi-1").</summary>
	public string Id { get; set; } = "";

	/// <summary>Display name; defaults to the sub-map's name at placement.</summary>
	public string Name { get; set; } = "";

	/// <summary>String id of the sub-map this references (the MapDocument seam).</summary>
	public string SubMapId { get; set; } = "";

	/// <summary>Path to the sub-map file, copied from the sub-map at placement.</summary>
	public string SourcePath { get; set; } = "";

	/// <summary>
	/// Name prefix for fix-up. "" + <see cref="FixUp"/> means auto-prefix
	/// (<see cref="Id"/> + "_"). Ignored when <see cref="FixUp"/> is false.
	/// </summary>
	public string Prefix { get; set; } = "";

	/// <summary>Whether target-name fix-up applies on resolve (default true).</summary>
	public bool FixUp { get; set; } = true;

	/// <summary>Placement origin (inches, Z-up) in the main map.</summary>
	public Vec3 Origin { get; set; } = Vec3.Zero;

	/// <summary>Placement yaw in degrees about the Z axis (default 0).</summary>
	public float YawDegrees { get; set; }

	/// <summary>Layer this instance sits in ("" = default/world layer).</summary>
	public string Layer { get; set; } = "";

	/// <summary>Visgroup this instance sits in ("" = none).</summary>
	public string VisGroup { get; set; } = "";

	/// <summary>The prefix resolve actually applies: "" when fix-up is off, else the set prefix or Id + "_".</summary>
	public string EffectivePrefix => !FixUp ? "" : ( string.IsNullOrEmpty( Prefix ) ? Id + "_" : Prefix );
}

/// <summary>One leg of an instance IO proxy: a port on the proxy ↔ a port on an entity inside the sub-map.</summary>
public sealed class InstanceIoRoute
{
	/// <summary>Port name exposed on the proxy in the main map (an input the main map fires, or an output the proxy fires outward).</summary>
	public string ProxyPort { get; set; } = "";

	/// <summary>Targetname of the entity inside the sub-map (unprefixed here; resolve prefixes it).</summary>
	public string InnerTarget { get; set; } = "";

	/// <summary>Input/output name on the inner entity (e.g. "Toggle", "OnTrigger").</summary>
	public string InnerPort { get; set; } = "";

	/// <summary>Parameter passed with the IO ("" = none).</summary>
	public string Parameter { get; set; } = "";

	public InstanceIoRoute Clone() => new()
	{
		ProxyPort = ProxyPort,
		InnerTarget = InnerTarget,
		InnerPort = InnerPort,
		Parameter = Parameter,
	};
}

/// <summary>
/// The IO proxy for one instance: the entity the main map talks to instead
/// of reaching inside the sub-map. Inputs are main-map → sub-map (a main-map
/// output fires ProxyPort, the proxy fires InnerPort at InnerTarget);
/// outputs are sub-map → main-map (InnerTarget firing InnerPort makes the
/// proxy fire ProxyPort outward). InnerTarget is stored UNPREFIXED and
/// prefixed on resolve, so a re-prefix never double-prefixes; ProxyPort is
/// a main-map name and is never prefixed.
/// </summary>
public sealed class InstanceIoProxy
{
	/// <summary>Proxy id (e.g. "iop-1").</summary>
	public string Id { get; set; } = "";

	/// <summary>The instance this proxies for.</summary>
	public string InstanceId { get; set; } = "";

	/// <summary>Main-map → sub-map legs.</summary>
	public List<InstanceIoRoute> Inputs { get; } = new();

	/// <summary>Sub-map → main-map legs.</summary>
	public List<InstanceIoRoute> Outputs { get; } = new();

	public InstanceIoProxy Clone()
	{
		var copy = new InstanceIoProxy { Id = Id, InstanceId = InstanceId };
		foreach ( var r in Inputs ) copy.Inputs.Add( r.Clone() );
		foreach ( var r in Outputs ) copy.Outputs.Add( r.Clone() );
		return copy;
	}
}

/// <summary>A resolved IO proxy: the proxy's legs with inner targets prefixed for its instance.</summary>
public sealed class ResolvedIoProxy
{
	/// <summary>Proxy id.</summary>
	public string ProxyId { get; set; } = "";

	/// <summary>Instance id.</summary>
	public string InstanceId { get; set; } = "";

	/// <summary>Prefix that was applied to the inner targets.</summary>
	public string Prefix { get; set; } = "";

	/// <summary>Resolved input legs (ProxyPort unchanged, InnerTarget prefixed).</summary>
	public List<InstanceIoRoute> Inputs { get; } = new();

	/// <summary>Resolved output legs (ProxyPort unchanged, InnerTarget prefixed).</summary>
	public List<InstanceIoRoute> Outputs { get; } = new();
}

/// <summary>One VMF-bound func_instance record: an instance as the VMF writer sees it.</summary>
public sealed class FuncInstanceRecord
{
	public string InstanceId { get; set; } = "";
	public string SubMapId { get; set; } = "";
	public string SubMapPath { get; set; } = "";
	public string Prefix { get; set; } = "";
	public bool FixUp { get; set; } = true;
	public float OriginX { get; set; }
	public float OriginY { get; set; }
	public float OriginZ { get; set; }
	public float YawDegrees { get; set; }
	public string Layer { get; set; } = "";
	public string VisGroup { get; set; } = "";

	/// <summary>Placement origin as a vector.</summary>
	public Vec3 Origin => new( OriginX, OriginY, OriginZ );
}

/// <summary>Severity of one <see cref="MapInstanceIssue"/> from <see cref="MapInstanceStore.ValidateInstance"/>.</summary>
public enum MapInstanceIssueSeverity
{
	Error,
	Warning,
}

/// <summary>One validation finding from <see cref="MapInstanceStore.ValidateInstance"/>.</summary>
public sealed class MapInstanceIssue
{
	public const string CodeSubMapNotFound = "mapinstances-submap-not-found";
	public const string CodeInstanceNotFound = "mapinstances-instance-not-found";
	public const string CodeTargetCollision = "mapinstances-target-collision";
	public const string CodeProxyTargetNotFound = "mapinstances-proxy-target-not-found";

	public MapInstanceIssueSeverity Severity;
	public string Code = "";
	public string Message = "";

	public MapInstanceIssue() { }
	public MapInstanceIssue( MapInstanceIssueSeverity severity, string code, string message )
	{
		Severity = severity; Code = code; Message = message;
	}

	public override string ToString() => $"[{Severity} {Code}] {Message}";
}

/// <summary>Base result for map-instances operations: data, not exceptions.</summary>
public class MapInstancesResult
{
	public bool Ok { get; protected set; }
	public string Reason { get; protected set; } = "";
	public string Message { get; protected set; } = "";

	protected MapInstancesResult() { }

	public static MapInstancesResult Success() => new() { Ok = true };
	public static MapInstancesResult Failure( string reason, string message ) => new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>Result of <see cref="MapInstanceStore.DefinePreset"/>.</summary>
public sealed class DefinePresetResult : MapInstancesResult
{
	public SprinklePreset? Preset { get; private set; }

	public static DefinePresetResult Success( SprinklePreset preset ) =>
		new() { Ok = true, Preset = preset };

	public static new DefinePresetResult Failure( string reason, string message ) =>
		new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>Result of <see cref="MapInstanceStore.PlacePreset"/>.</summary>
public sealed class PlacePresetResult : MapInstancesResult
{
	public IReadOnlyList<PlacedSprinkle> Placed { get; private set; } = Array.Empty<PlacedSprinkle>();

	public static PlacePresetResult Success( IReadOnlyList<PlacedSprinkle> placed ) =>
		new() { Ok = true, Placed = placed };

	public static new PlacePresetResult Failure( string reason, string message ) =>
		new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>Result of <see cref="MapInstanceStore.SavePrefab"/>.</summary>
public sealed class SavePrefabResult : MapInstancesResult
{
	public PrefabDefinition? Definition { get; private set; }

	public static SavePrefabResult Success( PrefabDefinition definition ) =>
		new() { Ok = true, Definition = definition };

	public static new SavePrefabResult Failure( string reason, string message ) =>
		new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>Result of <see cref="MapInstanceStore.InstantiatePrefab"/>.</summary>
public sealed class InstantiatePrefabResult : MapInstancesResult
{
	public PrefabInstance? Instance { get; private set; }

	public static InstantiatePrefabResult Success( PrefabInstance instance ) =>
		new() { Ok = true, Instance = instance };

	public static new InstantiatePrefabResult Failure( string reason, string message ) =>
		new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>Result of <see cref="MapInstanceStore.RegisterSubMap"/>.</summary>
public sealed class RegisterSubMapResult : MapInstancesResult
{
	public SubMapContent? SubMap { get; private set; }

	public static RegisterSubMapResult Success( SubMapContent subMap ) =>
		new() { Ok = true, SubMap = subMap };

	public static new RegisterSubMapResult Failure( string reason, string message ) =>
		new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>Result of <see cref="MapInstanceStore.PlaceInstance"/>.</summary>
public sealed class PlaceInstanceResult : MapInstancesResult
{
	public MapInstance? Instance { get; private set; }

	public static PlaceInstanceResult Success( MapInstance instance ) =>
		new() { Ok = true, Instance = instance };

	public static new PlaceInstanceResult Failure( string reason, string message ) =>
		new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>Result of <see cref="MapInstanceStore.CreateInstanceFromSelection"/>.</summary>
public sealed class CreateInstanceResult : MapInstancesResult
{
	public MapInstance? Instance { get; private set; }
	public SubMapContent? SubMap { get; private set; }

	public static CreateInstanceResult Success( MapInstance instance, SubMapContent subMap ) =>
		new() { Ok = true, Instance = instance, SubMap = subMap };

	public static new CreateInstanceResult Failure( string reason, string message ) =>
		new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>Result of <see cref="MapInstanceStore.ResolveInstance"/>.</summary>
public sealed class ResolveInstanceResult : MapInstancesResult
{
	/// <summary>Resolved members: fresh clones, transformed + name-fixed-up. Empty on failure.</summary>
	public IReadOnlyList<InstanceMember> Members { get; private set; } = Array.Empty<InstanceMember>();

	/// <summary>Resolved targetnames that still collide with the supplied main-map names (empty when fix-up cleared them).</summary>
	public IReadOnlyList<string> Collisions { get; private set; } = Array.Empty<string>();

	/// <summary>Prefix that was applied ("" when fix-up is off).</summary>
	public string Prefix { get; private set; } = "";

	public static ResolveInstanceResult Success( IReadOnlyList<InstanceMember> members, IReadOnlyList<string> collisions, string prefix ) =>
		new() { Ok = true, Members = members, Collisions = collisions, Prefix = prefix };

	public static new ResolveInstanceResult Failure( string reason, string message ) =>
		new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>Result of <see cref="MapInstanceStore.EditSubMapMember"/>: in-place edits re-resolve by construction, so this reports how many instances the edit reached.</summary>
public sealed class EditSubMapResult : MapInstancesResult
{
	/// <summary>Instances of the edited sub-map at edit time (all of them now resolve the edit).</summary>
	public int AffectedInstances { get; private set; }

	/// <summary>Ids of the affected instances.</summary>
	public IReadOnlyList<string> AffectedInstanceIds { get; private set; } = Array.Empty<string>();

	public static EditSubMapResult Success( IReadOnlyList<string> affectedIds ) =>
		new() { Ok = true, AffectedInstances = affectedIds.Count, AffectedInstanceIds = affectedIds };

	public static new EditSubMapResult Failure( string reason, string message ) =>
		new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>Result of <see cref="MapInstanceStore.AddIoProxy"/>.</summary>
public sealed class AddIoProxyResult : MapInstancesResult
{
	public InstanceIoProxy? Proxy { get; private set; }

	public static AddIoProxyResult Success( InstanceIoProxy proxy ) =>
		new() { Ok = true, Proxy = proxy };

	public static new AddIoProxyResult Failure( string reason, string message ) =>
		new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>Result of <see cref="MapInstanceStore.ResolveProxy"/>.</summary>
public sealed class ResolveProxyResult : MapInstancesResult
{
	public ResolvedIoProxy? Proxy { get; private set; }

	public static ResolveProxyResult Success( ResolvedIoProxy proxy ) =>
		new() { Ok = true, Proxy = proxy };

	public static new ResolveProxyResult Failure( string reason, string message ) =>
		new() { Ok = false, Reason = reason, Message = message };
}

/// <summary>
/// The document-level store for sprinkles, prefabs and func_instances:
/// sprinkle presets, prefab definitions, registered sub-maps, placed
/// instances and their IO proxies. MapDocument owns one of these
/// (hammer-map-document-model) and embeds <see cref="ToJson"/> verbatim,
/// the way it embeds EntityIoList / LinkedGroupSet.
/// </summary>
public sealed class MapInstanceStore
{
	/// <summary>Document section schema version. Bump only with a migration, never silently.</summary>
	public const int SchemaVersion = 1;

	readonly List<SprinklePreset> _presets = new();
	readonly List<PrefabDefinition> _prefabs = new();
	readonly List<SubMapContent> _subMaps = new();
	readonly List<MapInstance> _instances = new();
	readonly List<InstanceIoProxy> _proxies = new();
	int _nextPreset = 1;
	int _nextPrefab = 1;
	int _nextPrefabInstance = 1;
	int _nextSubMap = 1;
	int _nextInstance = 1;
	int _nextProxy = 1;

	public IReadOnlyList<SprinklePreset> Presets => _presets;
	public IReadOnlyList<PrefabDefinition> Prefabs => _prefabs;
	public IReadOnlyList<SubMapContent> SubMaps => _subMaps;
	public IReadOnlyList<MapInstance> Instances => _instances;
	public IReadOnlyList<InstanceIoProxy> Proxies => _proxies;

	public SprinklePreset? FindPreset( string presetId ) =>
		_presets.FirstOrDefault( p => string.Equals( p.Id, presetId, StringComparison.Ordinal ) );

	public PrefabDefinition? FindPrefab( string prefabId ) =>
		_prefabs.FirstOrDefault( p => string.Equals( p.Id, prefabId, StringComparison.Ordinal ) );

	public SubMapContent? FindSubMap( string subMapId ) =>
		_subMaps.FirstOrDefault( s => string.Equals( s.Id, subMapId, StringComparison.Ordinal ) );

	public SubMapContent? FindSubMapByPath( string sourcePath ) =>
		_subMaps.FirstOrDefault( s => string.Equals( s.SourcePath, sourcePath, StringComparison.Ordinal ) );

	public MapInstance? FindInstance( string instanceId ) =>
		_instances.FirstOrDefault( i => string.Equals( i.Id, instanceId, StringComparison.Ordinal ) );

	public InstanceIoProxy? FindProxy( string proxyId ) =>
		_proxies.FirstOrDefault( p => string.Equals( p.Id, proxyId, StringComparison.Ordinal ) );

	/// <summary>Instances of a sub-map, in placement order.</summary>
	public IReadOnlyList<MapInstance> InstancesOf( string subMapId ) =>
		_instances.Where( i => string.Equals( i.SubMapId, subMapId, StringComparison.Ordinal ) ).ToList();

	/// <summary>IO proxies of an instance, in add order.</summary>
	public IReadOnlyList<InstanceIoProxy> ProxiesFor( string instanceId ) =>
		_proxies.Where( p => string.Equals( p.InstanceId, instanceId, StringComparison.Ordinal ) ).ToList();

	// ---- Sprinkle presets ----

	/// <summary>
	/// Defines a sprinkle preset from <paramref name="items"/> (deep-copied).
	/// Refuses a blank name, an empty item list, a blank model path, a
	/// non-positive/non-finite scale, or a non-finite offset/angles, with a
	/// named reason. A blank item classname defaults to "prop_static".
	/// </summary>
	public DefinePresetResult DefinePreset( string name, IEnumerable<SprinkleItem> items )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return DefinePresetResult.Failure( "mapinstances-invalid-name", "A sprinkle preset needs a non-blank name." );
		if ( items is null )
			return DefinePresetResult.Failure( "mapinstances-empty-preset", "A sprinkle preset needs at least one item." );
		var copied = items.Select( i => i?.Clone() ).ToList();
		if ( copied.Count == 0 || copied.Any( i => i is null ) )
			return DefinePresetResult.Failure( "mapinstances-empty-preset", "A sprinkle preset needs at least one item." );
		foreach ( var item in copied )
		{
			if ( string.IsNullOrWhiteSpace( item!.Model ) )
				return DefinePresetResult.Failure( "mapinstances-invalid-item", "A sprinkle item needs a non-blank model path." );
			if ( !float.IsFinite( item.Scale ) || item.Scale <= 0f )
				return DefinePresetResult.Failure( "mapinstances-invalid-item",
					$"Sprinkle item '{item.Model}' needs a finite scale greater than 0 (got {item.Scale})." );
			if ( !MapInstancesMath.IsFinite( item.Offset ) || !MapInstancesMath.IsFinite( item.Angles ) )
				return DefinePresetResult.Failure( "mapinstances-invalid-item",
					$"Sprinkle item '{item.Model}' needs finite offset and angles." );
			if ( string.IsNullOrWhiteSpace( item.ClassName ) ) item.ClassName = "prop_static";
		}
		var preset = new SprinklePreset { Id = $"sp-{_nextPreset++}", Name = name };
		foreach ( var item in copied ) preset.Items.Add( item! );
		_presets.Add( preset );
		return DefinePresetResult.Success( preset );
	}

	/// <summary>
	/// Places a sprinkle preset at <paramref name="origin"/> with
	/// <paramref name="yawDegrees"/> of yaw: each item resolves to a world
	/// origin (placement origin + yaw-rotated offset) and world angles (item
	/// angles + placement yaw). Returns PlacedSprinkle data — seating real
	/// prop entities from it is the MapEntities/PropPlacement adapter's job.
	/// </summary>
	public PlacePresetResult PlacePreset( string presetId, Vec3 origin, float yawDegrees = 0f )
	{
		var preset = FindPreset( presetId );
		if ( preset is null )
			return PlacePresetResult.Failure( "mapinstances-preset-not-found", $"No sprinkle preset '{presetId}'." );
		if ( !MapInstancesMath.IsFinite( origin ) || !float.IsFinite( yawDegrees ) )
			return PlacePresetResult.Failure( "mapinstances-invalid-origin", "A sprinkle placement needs a finite origin and yaw." );
		var placed = preset.Items.Select( item =>
		{
			var seated = new PlacedSprinkle
			{
				Model = item.Model,
				ClassName = item.ClassName,
				Origin = MapInstancesMath.RotateYaw( item.Offset, yawDegrees ) + origin,
				Angles = new Vec3( item.Angles.x, item.Angles.y + yawDegrees, item.Angles.z ),
				Scale = item.Scale,
			};
			foreach ( var kv in item.KeyValues ) seated.KeyValues[kv.Key] = kv.Value;
			return seated;
		} ).ToList();
		return PlacePresetResult.Success( placed );
	}

	// ---- Prefabs ----

	/// <summary>
	/// Saves a selection as a prefab definition (deep-copied, so later caller
	/// mutation cannot corrupt the definition). Refuses a blank name, an
	/// empty member list, or duplicate member ids, with a named reason.
	/// Blank member ids are auto-assigned ("m-1", ...).
	/// </summary>
	public SavePrefabResult SavePrefab(
		string name,
		IEnumerable<InstanceMember> members,
		string layer = "",
		string visGroup = "" )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return SavePrefabResult.Failure( "mapinstances-invalid-name", "A prefab needs a non-blank name." );
		if ( members is null )
			return SavePrefabResult.Failure( "mapinstances-empty-prefab", "A prefab needs at least one member." );
		var copied = members.Select( m => m?.Clone() ).ToList();
		if ( copied.Count == 0 || copied.Any( m => m is null ) )
			return SavePrefabResult.Failure( "mapinstances-empty-prefab", "A prefab needs at least one member." );
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
				return SavePrefabResult.Failure( "mapinstances-duplicate-member-id", $"Member id '{m.Id}' appears twice in the prefab." );
		}
		var definition = new PrefabDefinition
		{
			Id = $"pf-{_nextPrefab++}",
			Name = name,
			Layer = layer ?? "",
			VisGroup = visGroup ?? "",
		};
		foreach ( var m in copied ) definition.Members.Add( m! );
		_prefabs.Add( definition );
		return SavePrefabResult.Success( definition );
	}

	/// <summary>
	/// Instantiates a prefab: fresh copies of the definition's members,
	/// translated by <paramref name="offset"/>, rotated by
	/// <paramref name="yawDegrees"/>, and name-fixed-up with
	/// <paramref name="prefix"/> (auto-generated "pfi-N-" when null/blank).
	/// The definition is never renamed and never mutated; two instantiations
	/// get distinct prefixes, so their targetnames never collide with each
	/// other. A resolved targetname that still collides with a name in
	/// <paramref name="mainMapTargetNames"/> fails with
	/// mapinstances-target-collision and instantiates nothing.
	/// </summary>
	public InstantiatePrefabResult InstantiatePrefab(
		string prefabId,
		Vec3 offset,
		float yawDegrees = 0f,
		string? prefix = null,
		IReadOnlyCollection<string>? mainMapTargetNames = null )
	{
		var definition = FindPrefab( prefabId );
		if ( definition is null )
			return InstantiatePrefabResult.Failure( "mapinstances-prefab-not-found", $"No prefab '{prefabId}'." );
		if ( !MapInstancesMath.IsFinite( offset ) || !float.IsFinite( yawDegrees ) )
			return InstantiatePrefabResult.Failure( "mapinstances-invalid-origin", "A prefab instantiation needs a finite offset and yaw." );
		int n = _nextPrefabInstance;
		string instanceId = $"pfi-{n}";
		string effectivePrefix = string.IsNullOrEmpty( prefix ) ? instanceId + "-" : prefix;
		var defined = InstanceNameFixUp.DefinedTargetNames( definition.Members );
		var resolved = definition.Members
			.Select( m => InstanceNameFixUp.FixUpMember( m.Transformed( offset, yawDegrees ), defined, effectivePrefix ) )
			.ToList();
		var collisions = InstanceNameFixUp.FindCollisions( resolved, mainMapTargetNames );
		if ( collisions.Count > 0 )
			return InstantiatePrefabResult.Failure( "mapinstances-target-collision",
				$"Prefab '{prefabId}' still collides with the main map after prefixing '{effectivePrefix}': {string.Join( ", ", collisions )}." );
		_nextPrefabInstance = n + 1;
		var instance = new PrefabInstance
		{
			Id = instanceId,
			PrefabId = definition.Id,
			Prefix = effectivePrefix,
			Offset = offset,
			YawDegrees = yawDegrees,
		};
		foreach ( var m in resolved ) instance.Members.Add( m );
		return InstantiatePrefabResult.Success( instance );
	}

	// ---- Sub-maps ----

	/// <summary>
	/// Registers sub-map content (deep-copied) under its string id. A blank
	/// id is auto-assigned ("sm-1", ...). Refuses an empty member list, a
	/// blank name, a duplicate sub-map id, or duplicate member ids, with a
	/// named reason. This is the seam MapDocument's loader replaces: the
	/// store keeps binding sub-maps by id + source path either way.
	/// </summary>
	public RegisterSubMapResult RegisterSubMap( SubMapContent content )
	{
		if ( content is null )
			return RegisterSubMapResult.Failure( "mapinstances-empty-submap", "A sub-map needs at least one member." );
		if ( string.IsNullOrWhiteSpace( content.Name ) )
			return RegisterSubMapResult.Failure( "mapinstances-invalid-name", "A sub-map needs a non-blank name." );
		if ( content.Members.Count == 0 )
			return RegisterSubMapResult.Failure( "mapinstances-empty-submap", $"Sub-map '{content.Name}' needs at least one member." );
		var copied = content.Clone();
		if ( string.IsNullOrWhiteSpace( copied.Id ) ) copied.Id = $"sm-{_nextSubMap++}";
		if ( FindSubMap( copied.Id ) is not null )
			return RegisterSubMapResult.Failure( "mapinstances-submap-duplicate-id", $"Sub-map id '{copied.Id}' is already registered." );
		var seen = new HashSet<string>( StringComparer.Ordinal );
		for ( int i = 0; i < copied.Members.Count; i++ )
		{
			var m = copied.Members[i];
			if ( string.IsNullOrWhiteSpace( m.Id ) )
			{
				int n = 1;
				while ( seen.Contains( $"m-{n}" ) ) n++;
				m.Id = $"m-{n}";
			}
			if ( !seen.Add( m.Id ) )
				return RegisterSubMapResult.Failure( "mapinstances-duplicate-member-id", $"Member id '{m.Id}' appears twice in sub-map '{copied.Id}'." );
		}
		_subMaps.Add( copied );
		return RegisterSubMapResult.Success( copied );
	}

	/// <summary>
	/// Removes a registered sub-map. Refuses while any instance still
	/// references it (mapinstances-submap-in-use); remove the instances
	/// first. Unknown ids fail with mapinstances-submap-not-found.
	/// </summary>
	public MapInstancesResult RemoveSubMap( string subMapId )
	{
		var subMap = FindSubMap( subMapId );
		if ( subMap is null )
			return MapInstancesResult.Failure( "mapinstances-submap-not-found", $"No sub-map '{subMapId}'." );
		if ( InstancesOf( subMapId ).Count > 0 )
			return MapInstancesResult.Failure( "mapinstances-submap-in-use",
				$"Sub-map '{subMapId}' is still referenced by {InstancesOf( subMapId ).Count} instance(s); remove them first." );
		_subMaps.Remove( subMap );
		return MapInstancesResult.Success();
	}

	/// <summary>
	/// Edits one member of a registered sub-map in place; EVERY instance of
	/// that sub-map resolves the edit, because instances read the sub-map.
	/// Returns the affected instance ids. Refuses an unknown sub-map/member
	/// or an edit that does not apply to the member kind, and in that case
	/// changes nothing.
	/// </summary>
	public EditSubMapResult EditSubMapMember( string subMapId, string memberId, InstanceMemberEdit edit )
	{
		var subMap = FindSubMap( subMapId );
		if ( subMap is null )
			return EditSubMapResult.Failure( "mapinstances-submap-not-found", $"No sub-map '{subMapId}'." );
		var member = subMap.FindMember( memberId );
		if ( member is null )
			return EditSubMapResult.Failure( "mapinstances-member-not-found", $"Sub-map '{subMapId}' has no member '{memberId}'." );
		if ( edit is null )
			return EditSubMapResult.Failure( "mapinstances-unknown-edit", "No edit was supplied." );
		var (ok, reason) = edit.ApplyTo( member );
		if ( !ok )
			return EditSubMapResult.Failure( reason, $"Edit {edit.Kind} does not apply to member '{memberId}' ({member.Kind})." );
		var affected = InstancesOf( subMapId ).Select( i => i.Id ).ToList();
		return EditSubMapResult.Success( affected );
	}

	/// <summary>
	/// Edits a sub-map member as seen through ONE instance
	/// (<paramref name="instanceId"/>): the edit lands on that instance's
	/// sub-map, so every instance updates — the data-level "in-place edit
	/// with the main map as context". Refuses an unknown instance.
	/// </summary>
	public EditSubMapResult EditSubMapMemberViaInstance( string instanceId, string memberId, InstanceMemberEdit edit )
	{
		var instance = FindInstance( instanceId );
		if ( instance is null )
			return EditSubMapResult.Failure( "mapinstances-instance-not-found", $"No instance '{instanceId}'." );
		return EditSubMapMember( instance.SubMapId, memberId, edit );
	}

	// ---- Instances ----

	/// <summary>
	/// Places a func_instance referencing <paramref name="subMapId"/> at
	/// <paramref name="origin"/> with <paramref name="yawDegrees"/> of yaw.
	/// <paramref name="prefix"/> is the fix-up prefix ("" = auto-prefix
	/// with the instance id); it is ignored when <paramref name="fixUp"/>
	/// is false. The display name defaults to the sub-map's name.
	/// </summary>
	public PlaceInstanceResult PlaceInstance(
		string subMapId,
		Vec3 origin,
		string name = "",
		string? prefix = null,
		bool fixUp = true,
		float yawDegrees = 0f,
		string layer = "",
		string visGroup = "" )
	{
		var subMap = FindSubMap( subMapId );
		if ( subMap is null )
			return PlaceInstanceResult.Failure( "mapinstances-submap-not-found", $"No sub-map '{subMapId}'." );
		if ( !MapInstancesMath.IsFinite( origin ) || !float.IsFinite( yawDegrees ) )
			return PlaceInstanceResult.Failure( "mapinstances-invalid-origin", "An instance needs a finite origin and yaw." );
		var instance = new MapInstance
		{
			Id = $"mi-{_nextInstance++}",
			Name = string.IsNullOrWhiteSpace( name ) ? subMap.Name : name,
			SubMapId = subMap.Id,
			SourcePath = subMap.SourcePath,
			Prefix = prefix ?? "",
			FixUp = fixUp,
			Origin = origin,
			YawDegrees = yawDegrees,
			Layer = layer ?? "",
			VisGroup = visGroup ?? "",
		};
		_instances.Add( instance );
		return PlaceInstanceResult.Success( instance );
	}

	/// <summary>
	/// Removes an instance. Its IO proxies go with it (a proxy without an
	/// instance has nothing to proxy for). Unknown ids fail with
	/// mapinstances-instance-not-found.
	/// </summary>
	public MapInstancesResult RemoveInstance( string instanceId )
	{
		var instance = FindInstance( instanceId );
		if ( instance is null )
			return MapInstancesResult.Failure( "mapinstances-instance-not-found", $"No instance '{instanceId}'." );
		_proxies.RemoveAll( p => string.Equals( p.InstanceId, instanceId, StringComparison.Ordinal ) );
		_instances.Remove( instance );
		return MapInstancesResult.Success();
	}

	/// <summary>
	/// Resolves an instance to its members: the registered sub-map's members,
	/// transformed by the placement (yaw, then origin) and name-fixed-up
	/// with the instance's effective prefix. Returns fresh clones — mutating
	/// the result never corrupts the sub-map, and re-resolving after an
	/// in-place sub-map edit shows the edit. Collisions are the resolved
	/// targetnames that still appear in
	/// <paramref name="mainMapTargetNames"/> (empty when fix-up cleared
	/// them, or when no main-map names were supplied).
	/// </summary>
	public ResolveInstanceResult ResolveInstance( string instanceId, IReadOnlyCollection<string>? mainMapTargetNames = null )
	{
		var instance = FindInstance( instanceId );
		if ( instance is null )
			return ResolveInstanceResult.Failure( "mapinstances-instance-not-found", $"No instance '{instanceId}'." );
		var subMap = FindSubMap( instance.SubMapId );
		if ( subMap is null )
			return ResolveInstanceResult.Failure( "mapinstances-submap-not-found",
				$"Instance '{instanceId}' points at missing sub-map '{instance.SubMapId}'." );
		string prefix = instance.EffectivePrefix;
		var defined = subMap.DefinedTargetNames();
		var resolved = subMap.Members
			.Select( m => InstanceNameFixUp.FixUpMember( m.Transformed( instance.Origin, instance.YawDegrees ), defined, prefix ) )
			.ToList();
		var collisions = InstanceNameFixUp.FindCollisions( resolved, mainMapTargetNames );
		return ResolveInstanceResult.Success( resolved, collisions, prefix );
	}

	/// <summary>Resolves an instance; false + a named reason when it cannot be resolved.</summary>
	public bool TryResolveInstance( string instanceId, out IReadOnlyList<InstanceMember> members, out string reason )
	{
		var result = ResolveInstance( instanceId );
		members = result.Members;
		reason = result.Ok ? "" : result.Reason;
		return result.Ok;
	}

	/// <summary>
	/// Validates one instance against the main map's targetnames: a missing
	/// sub-map is an Error, a resolved targetname colliding with a main-map
	/// name is an Error (fix-up should have moved it; fix-up off leaves it
	/// in place), and an IO proxy leg pointing at a target the sub-map does
	/// not define is an Error. A clean instance reports no issues.
	/// </summary>
	public IReadOnlyList<MapInstanceIssue> ValidateInstance( string instanceId, IReadOnlyCollection<string>? mainMapTargetNames = null )
	{
		var issues = new List<MapInstanceIssue>();
		var instance = FindInstance( instanceId );
		if ( instance is null )
		{
			issues.Add( new MapInstanceIssue( MapInstanceIssueSeverity.Error,
				MapInstanceIssue.CodeInstanceNotFound, $"No instance '{instanceId}'." ) );
			return issues;
		}
		var subMap = FindSubMap( instance.SubMapId );
		if ( subMap is null )
		{
			issues.Add( new MapInstanceIssue( MapInstanceIssueSeverity.Error,
				MapInstanceIssue.CodeSubMapNotFound, $"Instance '{instanceId}' points at missing sub-map '{instance.SubMapId}'." ) );
			return issues;
		}
		var resolved = ResolveInstance( instanceId, mainMapTargetNames );
		foreach ( string collision in resolved.Collisions )
			issues.Add( new MapInstanceIssue( MapInstanceIssueSeverity.Error,
				MapInstanceIssue.CodeTargetCollision,
				$"Instance '{instanceId}' resolves targetname '{collision}' that already exists in the main map (fix-up {( instance.FixUp ? "on" : "off" )}, prefix '{resolved.Prefix}')." ) );
		var defined = subMap.DefinedTargetNames();
		foreach ( var proxy in ProxiesFor( instanceId ) )
		{
			foreach ( var route in proxy.Inputs.Concat( proxy.Outputs ) )
			{
				if ( route.InnerTarget.Length == 0 || !defined.Contains( route.InnerTarget ) )
					issues.Add( new MapInstanceIssue( MapInstanceIssueSeverity.Error,
						MapInstanceIssue.CodeProxyTargetNotFound,
						$"Proxy '{proxy.Id}' of instance '{instanceId}' points at inner target '{route.InnerTarget}', which sub-map '{subMap.Id}' does not define." ) );
			}
		}
		return issues;
	}

	/// <summary>
	/// Creates a func_instance from a selection: the selected members are
	/// saved as a new registered sub-map (rebased to the placement origin, so
	/// the sub-map is local and the instance placed at
	/// <paramref name="origin"/> resolves back to the original selection
	/// exactly) and an instance referencing it is placed. The caller's
	/// selection is deep-copied, never renamed and never mutated — fix-up
	/// applies only to resolved copies. Refuses an empty selection, a blank
	/// name, or duplicate member ids.
	/// </summary>
	public CreateInstanceResult CreateInstanceFromSelection(
		string name,
		IEnumerable<InstanceMember> members,
		Vec3 origin,
		string sourcePath = "",
		string? prefix = null,
		bool fixUp = true,
		float yawDegrees = 0f )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return CreateInstanceResult.Failure( "mapinstances-invalid-name", "An instance from a selection needs a non-blank name." );
		if ( members is null )
			return CreateInstanceResult.Failure( "mapinstances-empty-submap", "An instance from a selection needs at least one member." );
		var copied = members.Select( m => m?.Clone() ).ToList();
		if ( copied.Count == 0 || copied.Any( m => m is null ) )
			return CreateInstanceResult.Failure( "mapinstances-empty-submap", "An instance from a selection needs at least one member." );
		if ( !MapInstancesMath.IsFinite( origin ) || !float.IsFinite( yawDegrees ) )
			return CreateInstanceResult.Failure( "mapinstances-invalid-origin", "An instance from a selection needs a finite origin and yaw." );
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
				return CreateInstanceResult.Failure( "mapinstances-duplicate-member-id", $"Member id '{m.Id}' appears twice in the selection." );
		}
		var subMap = new SubMapContent
		{
			Id = $"sm-{_nextSubMap++}",
			SourcePath = sourcePath ?? "",
			Name = name,
		};
		foreach ( var m in copied ) subMap.Members.Add( m!.Rebased( origin, yawDegrees ) );
		_subMaps.Add( subMap );
		var instance = new MapInstance
		{
			Id = $"mi-{_nextInstance++}",
			Name = name,
			SubMapId = subMap.Id,
			SourcePath = subMap.SourcePath,
			Prefix = prefix ?? "",
			FixUp = fixUp,
			Origin = origin,
			YawDegrees = yawDegrees,
		};
		_instances.Add( instance );
		return CreateInstanceResult.Success( instance, subMap );
	}

	// ---- IO proxy ----

	/// <summary>
	/// Adds an IO proxy for an instance. Every leg's inner target must name
	/// an entity the instance's sub-map defines (else
	/// mapinstances-proxy-target-not-found), and every leg needs a non-blank
	/// proxy port and inner port. Inner targets are stored unprefixed and
	/// prefixed on resolve.
	/// </summary>
	public AddIoProxyResult AddIoProxy(
		string instanceId,
		IEnumerable<InstanceIoRoute> inputs,
		IEnumerable<InstanceIoRoute> outputs )
	{
		var instance = FindInstance( instanceId );
		if ( instance is null )
			return AddIoProxyResult.Failure( "mapinstances-instance-not-found", $"No instance '{instanceId}'." );
		var subMap = FindSubMap( instance.SubMapId );
		if ( subMap is null )
			return AddIoProxyResult.Failure( "mapinstances-submap-not-found",
				$"Instance '{instanceId}' points at missing sub-map '{instance.SubMapId}'." );
		var defined = subMap.DefinedTargetNames();
		var inCopied = ( inputs ?? Enumerable.Empty<InstanceIoRoute>() ).Select( r => r?.Clone() ).ToList();
		var outCopied = ( outputs ?? Enumerable.Empty<InstanceIoRoute>() ).Select( r => r?.Clone() ).ToList();
		foreach ( var route in inCopied.Concat( outCopied ) )
		{
			if ( route is null )
				return AddIoProxyResult.Failure( "mapinstances-invalid-proxy-port", "An IO proxy leg was null." );
			if ( string.IsNullOrWhiteSpace( route.ProxyPort ) || string.IsNullOrWhiteSpace( route.InnerPort ) )
				return AddIoProxyResult.Failure( "mapinstances-invalid-proxy-port", "An IO proxy leg needs a non-blank proxy port and inner port." );
			if ( route.InnerTarget.Length == 0 || !defined.Contains( route.InnerTarget ) )
				return AddIoProxyResult.Failure( "mapinstances-proxy-target-not-found",
					$"IO proxy leg '{route.ProxyPort}' points at inner target '{route.InnerTarget}', which sub-map '{subMap.Id}' does not define." );
		}
		var proxy = new InstanceIoProxy { Id = $"iop-{_nextProxy++}", InstanceId = instance.Id };
		foreach ( var r in inCopied ) proxy.Inputs.Add( r! );
		foreach ( var r in outCopied ) proxy.Outputs.Add( r! );
		_proxies.Add( proxy );
		return AddIoProxyResult.Success( proxy );
	}

	/// <summary>Removes an IO proxy. Unknown ids fail with mapinstances-proxy-not-found.</summary>
	public MapInstancesResult RemoveProxy( string proxyId )
	{
		var proxy = FindProxy( proxyId );
		if ( proxy is null )
			return MapInstancesResult.Failure( "mapinstances-proxy-not-found", $"No IO proxy '{proxyId}'." );
		_proxies.Remove( proxy );
		return MapInstancesResult.Success();
	}

	/// <summary>
	/// Resolves an IO proxy: the proxy's legs with inner targets prefixed
	/// for its instance. Proxy ports are main-map names and are never
	/// prefixed; inner targets are prefixed exactly once, at resolve time.
	/// </summary>
	public ResolveProxyResult ResolveProxy( string proxyId )
	{
		var proxy = FindProxy( proxyId );
		if ( proxy is null )
			return ResolveProxyResult.Failure( "mapinstances-proxy-not-found", $"No IO proxy '{proxyId}'." );
		var instance = FindInstance( proxy.InstanceId );
		if ( instance is null )
			return ResolveProxyResult.Failure( "mapinstances-instance-not-found", $"Proxy '{proxyId}' points at missing instance '{proxy.InstanceId}'." );
		string prefix = instance.EffectivePrefix;
		var resolved = new ResolvedIoProxy { ProxyId = proxy.Id, InstanceId = proxy.InstanceId, Prefix = prefix };
		foreach ( var route in proxy.Inputs )
		{
			var copy = route.Clone();
			if ( prefix.Length > 0 && !copy.InnerTarget.StartsWith( "!", StringComparison.Ordinal ) )
				copy.InnerTarget = prefix + copy.InnerTarget;
			resolved.Inputs.Add( copy );
		}
		foreach ( var route in proxy.Outputs )
		{
			var copy = route.Clone();
			if ( prefix.Length > 0 && !copy.InnerTarget.StartsWith( "!", StringComparison.Ordinal ) )
				copy.InnerTarget = prefix + copy.InnerTarget;
			resolved.Outputs.Add( copy );
		}
		return ResolveProxyResult.Success( resolved );
	}

	// ---- VMF seam ----

	/// <summary>
	/// One record per instance, each carrying its sub-map id + path and its
	/// fix-up prefix. VmfIo writes one func_instance block per record.
	/// </summary>
	public IReadOnlyList<FuncInstanceRecord> ToFuncInstanceRecords() =>
		_instances.Select( i => new FuncInstanceRecord
		{
			InstanceId = i.Id,
			SubMapId = i.SubMapId,
			SubMapPath = i.SourcePath,
			Prefix = i.Prefix,
			FixUp = i.FixUp,
			OriginX = i.Origin.x,
			OriginY = i.Origin.y,
			OriginZ = i.Origin.z,
			YawDegrees = i.YawDegrees,
			Layer = i.Layer,
			VisGroup = i.VisGroup,
		} ).ToList();

	/// <summary>
	/// Rebuilds instances from func_instance records against sub-maps already
	/// registered in this store. A record pointing at a missing sub-map, or
	/// a duplicate instance id, fails the whole rebuild with a named reason
	/// and changes nothing.
	/// </summary>
	public MapInstancesResult FromFuncInstanceRecords( IEnumerable<FuncInstanceRecord> records )
	{
		if ( records is null ) return MapInstancesResult.Failure( "mapinstances-records-null", "No func_instance records were supplied." );
		var rows = records.ToList();
		foreach ( var r in rows )
		{
			if ( r is null ) return MapInstancesResult.Failure( "mapinstances-record-null", "A func_instance record was null." );
			if ( FindSubMap( r.SubMapId ) is null )
				return MapInstancesResult.Failure( "mapinstances-submap-not-found", $"Func_instance record '{r.InstanceId}' points at missing sub-map '{r.SubMapId}'." );
			if ( FindInstance( r.InstanceId ) is not null )
				return MapInstancesResult.Failure( "mapinstances-duplicate-instance-id", $"Instance id '{r.InstanceId}' is already in use." );
			if ( !MapInstancesMath.IsFinite( r.Origin ) || !float.IsFinite( r.YawDegrees ) )
				return MapInstancesResult.Failure( "mapinstances-invalid-origin", $"Func_instance record '{r.InstanceId}' needs a finite origin and yaw." );
		}
		foreach ( var r in rows )
		{
			var subMap = FindSubMap( r.SubMapId )!;
			_instances.Add( new MapInstance
			{
				Id = r.InstanceId,
				Name = subMap.Name,
				SubMapId = subMap.Id,
				SourcePath = string.IsNullOrEmpty( r.SubMapPath ) ? subMap.SourcePath : r.SubMapPath,
				Prefix = r.Prefix ?? "",
				FixUp = r.FixUp,
				Origin = r.Origin,
				YawDegrees = r.YawDegrees,
				Layer = r.Layer ?? "",
				VisGroup = r.VisGroup ?? "",
			} );
			// Keep generated ids unique past any restored id of the form mi-N.
			if ( r.InstanceId.StartsWith( "mi-", StringComparison.Ordinal )
				&& int.TryParse( r.InstanceId.AsSpan( 3 ), out int n ) && n >= _nextInstance )
				_nextInstance = n + 1;
		}
		return MapInstancesResult.Success();
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
		public Vec3Json angles { get; set; } = new();
		public List<Vec3Json> vertices { get; set; } = new();
		public Dictionary<string, string> key_values { get; set; } = new();
	}

	sealed class SprinkleItemJson
	{
		public string model { get; set; } = "";
		public string classname { get; set; } = "";
		public Vec3Json offset { get; set; } = new();
		public Vec3Json angles { get; set; } = new();
		public float scale { get; set; }
		public Dictionary<string, string> key_values { get; set; } = new();
	}

	sealed class PresetJson
	{
		public string id { get; set; } = "";
		public string name { get; set; } = "";
		public List<SprinkleItemJson> items { get; set; } = new();
	}

	sealed class PrefabJson
	{
		public string id { get; set; } = "";
		public string name { get; set; } = "";
		public string layer { get; set; } = "";
		public string visgroup { get; set; } = "";
		public List<MemberJson> members { get; set; } = new();
	}

	sealed class SubMapJson
	{
		public string id { get; set; } = "";
		public string source_path { get; set; } = "";
		public string name { get; set; } = "";
		public List<MemberJson> members { get; set; } = new();
	}

	sealed class InstanceJson
	{
		public string id { get; set; } = "";
		public string name { get; set; } = "";
		public string submap_id { get; set; } = "";
		public string source_path { get; set; } = "";
		public string prefix { get; set; } = "";
		public bool fixup { get; set; } = true;
		public Vec3Json origin { get; set; } = new();
		public float yaw_degrees { get; set; }
		public string layer { get; set; } = "";
		public string visgroup { get; set; } = "";
	}

	sealed class RouteJson
	{
		public string proxy_port { get; set; } = "";
		public string inner_target { get; set; } = "";
		public string inner_port { get; set; } = "";
		public string parameter { get; set; } = "";
	}

	sealed class ProxyJson
	{
		public string id { get; set; } = "";
		public string instance_id { get; set; } = "";
		public List<RouteJson> inputs { get; set; } = new();
		public List<RouteJson> outputs { get; set; } = new();
	}

	sealed class StoreJson
	{
		public int version { get; set; } = 1;
		public int next_preset { get; set; }
		public int next_prefab { get; set; }
		public int next_prefab_instance { get; set; }
		public int next_submap { get; set; }
		public int next_instance { get; set; }
		public int next_proxy { get; set; }
		public List<PresetJson> presets { get; set; } = new();
		public List<PrefabJson> prefabs { get; set; } = new();
		public List<SubMapJson> submaps { get; set; } = new();
		public List<InstanceJson> instances { get; set; } = new();
		public List<ProxyJson> proxies { get; set; } = new();
	}

	static Vec3Json ToJson( Vec3 v ) => new() { x = v.x, y = v.y, z = v.z };
	static Vec3 FromJson( Vec3Json v ) => v is null ? Vec3.Zero : new Vec3( v.x, v.y, v.z );

	static MemberJson ToJson( InstanceMember m ) => new()
	{
		id = m.Id,
		kind = m.Kind == InstanceMemberKind.Entity ? "entity" : "brush",
		name = m.Name,
		material = m.Material,
		origin = ToJson( m.Origin ),
		angles = ToJson( m.Angles ),
		vertices = m.Vertices.Select( ToJson ).ToList(),
		key_values = new Dictionary<string, string>( m.KeyValues ),
	};

	static InstanceMember FromJson( MemberJson j )
	{
		if ( j is null ) throw new ArgumentException( "map_instances member row is null." );
		if ( string.IsNullOrWhiteSpace( j.id ) ) throw new ArgumentException( "map_instances member is missing 'id'." );
		if ( j.kind != "brush" && j.kind != "entity" ) throw new ArgumentException( $"map_instances member '{j.id}' has unknown kind '{j.kind}'." );
		var m = new InstanceMember
		{
			Id = j.id,
			Kind = j.kind == "entity" ? InstanceMemberKind.Entity : InstanceMemberKind.Brush,
			Name = j.name ?? "",
			Material = j.material ?? "",
			Origin = FromJson( j.origin ),
			Angles = FromJson( j.angles ),
		};
		foreach ( var v in j.vertices ?? new List<Vec3Json>() ) m.Vertices.Add( FromJson( v ) );
		foreach ( var kv in j.key_values ?? new Dictionary<string, string>() ) m.KeyValues[kv.Key] = kv.Value;
		return m;
	}

	static List<InstanceMember> MembersFromJson( List<MemberJson> rows, string owner )
	{
		var members = new List<InstanceMember>();
		var memberIds = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var mj in rows ?? new List<MemberJson>() )
		{
			var member = FromJson( mj );
			if ( !memberIds.Add( member.Id ) )
				throw new ArgumentException( $"map_instances {owner} has duplicate member id '{member.Id}'." );
			members.Add( member );
		}
		return members;
	}

	/// <summary>Serialises this store as the "map_instances" section of a map document (version 1). MapDocument embeds this verbatim.</summary>
	public string ToJson()
	{
		var doc = new StoreJson
		{
			next_preset = _nextPreset,
			next_prefab = _nextPrefab,
			next_prefab_instance = _nextPrefabInstance,
			next_submap = _nextSubMap,
			next_instance = _nextInstance,
			next_proxy = _nextProxy,
			presets = _presets.Select( p => new PresetJson
			{
				id = p.Id,
				name = p.Name,
				items = p.Items.Select( item => new SprinkleItemJson
				{
					model = item.Model,
					classname = item.ClassName,
					offset = ToJson( item.Offset ),
					angles = ToJson( item.Angles ),
					scale = item.Scale,
					key_values = new Dictionary<string, string>( item.KeyValues ),
				} ).ToList(),
			} ).ToList(),
			prefabs = _prefabs.Select( p => new PrefabJson
			{
				id = p.Id,
				name = p.Name,
				layer = p.Layer,
				visgroup = p.VisGroup,
				members = p.Members.Select( ToJson ).ToList(),
			} ).ToList(),
			submaps = _subMaps.Select( s => new SubMapJson
			{
				id = s.Id,
				source_path = s.SourcePath,
				name = s.Name,
				members = s.Members.Select( ToJson ).ToList(),
			} ).ToList(),
			instances = _instances.Select( i => new InstanceJson
			{
				id = i.Id,
				name = i.Name,
				submap_id = i.SubMapId,
				source_path = i.SourcePath,
				prefix = i.Prefix,
				fixup = i.FixUp,
				origin = ToJson( i.Origin ),
				yaw_degrees = i.YawDegrees,
				layer = i.Layer,
				visgroup = i.VisGroup,
			} ).ToList(),
			proxies = _proxies.Select( p => new ProxyJson
			{
				id = p.Id,
				instance_id = p.InstanceId,
				inputs = p.Inputs.Select( r => new RouteJson
				{
					proxy_port = r.ProxyPort,
					inner_target = r.InnerTarget,
					inner_port = r.InnerPort,
					parameter = r.Parameter,
				} ).ToList(),
				outputs = p.Outputs.Select( r => new RouteJson
				{
					proxy_port = r.ProxyPort,
					inner_target = r.InnerTarget,
					inner_port = r.InnerPort,
					parameter = r.Parameter,
				} ).ToList(),
			} ).ToList(),
		};
		return JsonSerializer.Serialize( doc, new JsonSerializerOptions { WriteIndented = true } );
	}

	/// <summary>Reads back <see cref="ToJson"/> output. Throws <see cref="ArgumentException"/> naming the bad field.</summary>
	public static MapInstanceStore FromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) throw new ArgumentException( "map_instances JSON is empty.", nameof( json ) );
		StoreJson? doc;
		try { doc = JsonSerializer.Deserialize<StoreJson>( json ); }
		catch ( JsonException ex ) { throw new ArgumentException( $"map_instances JSON is not valid JSON: {ex.Message}", nameof( json ), ex ); }
		if ( doc is null ) throw new ArgumentException( "map_instances JSON is empty.", nameof( json ) );
		if ( doc.version != 1 ) throw new ArgumentException( $"unsupported map_instances version {doc.version}; expected 1.", nameof( json ) );
		var store = new MapInstanceStore
		{
			_nextPreset = Math.Max( 1, doc.next_preset ),
			_nextPrefab = Math.Max( 1, doc.next_prefab ),
			_nextPrefabInstance = Math.Max( 1, doc.next_prefab_instance ),
			_nextSubMap = Math.Max( 1, doc.next_submap ),
			_nextInstance = Math.Max( 1, doc.next_instance ),
			_nextProxy = Math.Max( 1, doc.next_proxy ),
		};
		var presetIds = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var pj in doc.presets ?? new List<PresetJson>() )
		{
			if ( pj is null ) throw new ArgumentException( "map_instances preset row is null.", nameof( json ) );
			if ( string.IsNullOrWhiteSpace( pj.id ) ) throw new ArgumentException( "map_instances preset is missing 'id'.", nameof( json ) );
			if ( !presetIds.Add( pj.id ) ) throw new ArgumentException( $"map_instances preset id '{pj.id}' appears twice.", nameof( json ) );
			if ( string.IsNullOrWhiteSpace( pj.name ) ) throw new ArgumentException( $"map_instances preset '{pj.id}' is missing 'name'.", nameof( json ) );
			var preset = new SprinklePreset { Id = pj.id, Name = pj.name };
			foreach ( var ij in pj.items ?? new List<SprinkleItemJson>() )
			{
				if ( ij is null ) throw new ArgumentException( $"map_instances preset '{pj.id}' has a null item.", nameof( json ) );
				if ( string.IsNullOrWhiteSpace( ij.model ) ) throw new ArgumentException( $"map_instances preset '{pj.id}' has an item with a blank model path.", nameof( json ) );
				if ( !float.IsFinite( ij.scale ) || ij.scale <= 0f )
					throw new ArgumentException( $"map_instances preset '{pj.id}' item '{ij.model}' has an invalid scale {ij.scale}; expected finite and greater than 0.", nameof( json ) );
				var item = new SprinkleItem
				{
					Model = ij.model,
					ClassName = string.IsNullOrWhiteSpace( ij.classname ) ? "prop_static" : ij.classname,
					Offset = FromJson( ij.offset ),
					Angles = FromJson( ij.angles ),
					Scale = ij.scale,
				};
				foreach ( var kv in ij.key_values ?? new Dictionary<string, string>() ) item.KeyValues[kv.Key] = kv.Value;
				preset.Items.Add( item );
			}
			if ( preset.Items.Count == 0 )
				throw new ArgumentException( $"map_instances preset '{pj.id}' has no items.", nameof( json ) );
			store._presets.Add( preset );
			if ( pj.id.StartsWith( "sp-", StringComparison.Ordinal )
				&& int.TryParse( pj.id.AsSpan( 3 ), out int n ) && n >= store._nextPreset )
				store._nextPreset = n + 1;
		}
		var prefabIds = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var fj in doc.prefabs ?? new List<PrefabJson>() )
		{
			if ( fj is null ) throw new ArgumentException( "map_instances prefab row is null.", nameof( json ) );
			if ( string.IsNullOrWhiteSpace( fj.id ) ) throw new ArgumentException( "map_instances prefab is missing 'id'.", nameof( json ) );
			if ( !prefabIds.Add( fj.id ) ) throw new ArgumentException( $"map_instances prefab id '{fj.id}' appears twice.", nameof( json ) );
			if ( string.IsNullOrWhiteSpace( fj.name ) ) throw new ArgumentException( $"map_instances prefab '{fj.id}' is missing 'name'.", nameof( json ) );
			var prefab = new PrefabDefinition
			{
				Id = fj.id,
				Name = fj.name,
				Layer = fj.layer ?? "",
				VisGroup = fj.visgroup ?? "",
			};
			foreach ( var m in MembersFromJson( fj.members, $"prefab '{fj.id}'" ) ) prefab.Members.Add( m );
			if ( prefab.Members.Count == 0 )
				throw new ArgumentException( $"map_instances prefab '{fj.id}' has no members.", nameof( json ) );
			store._prefabs.Add( prefab );
			if ( fj.id.StartsWith( "pf-", StringComparison.Ordinal )
				&& int.TryParse( fj.id.AsSpan( 3 ), out int n ) && n >= store._nextPrefab )
				store._nextPrefab = n + 1;
		}
		var subMapIds = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var sj in doc.submaps ?? new List<SubMapJson>() )
		{
			if ( sj is null ) throw new ArgumentException( "map_instances sub-map row is null.", nameof( json ) );
			if ( string.IsNullOrWhiteSpace( sj.id ) ) throw new ArgumentException( "map_instances sub-map is missing 'id'.", nameof( json ) );
			if ( !subMapIds.Add( sj.id ) ) throw new ArgumentException( $"map_instances sub-map id '{sj.id}' appears twice.", nameof( json ) );
			if ( string.IsNullOrWhiteSpace( sj.name ) ) throw new ArgumentException( $"map_instances sub-map '{sj.id}' is missing 'name'.", nameof( json ) );
			var subMap = new SubMapContent
			{
				Id = sj.id,
				SourcePath = sj.source_path ?? "",
				Name = sj.name,
			};
			foreach ( var m in MembersFromJson( sj.members, $"sub-map '{sj.id}'" ) ) subMap.Members.Add( m );
			if ( subMap.Members.Count == 0 )
				throw new ArgumentException( $"map_instances sub-map '{sj.id}' has no members.", nameof( json ) );
			store._subMaps.Add( subMap );
			if ( sj.id.StartsWith( "sm-", StringComparison.Ordinal )
				&& int.TryParse( sj.id.AsSpan( 3 ), out int n ) && n >= store._nextSubMap )
				store._nextSubMap = n + 1;
		}
		var instanceIds = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var ij in doc.instances ?? new List<InstanceJson>() )
		{
			if ( ij is null ) throw new ArgumentException( "map_instances instance row is null.", nameof( json ) );
			if ( string.IsNullOrWhiteSpace( ij.id ) ) throw new ArgumentException( "map_instances instance is missing 'id'.", nameof( json ) );
			if ( !instanceIds.Add( ij.id ) ) throw new ArgumentException( $"map_instances instance id '{ij.id}' appears twice.", nameof( json ) );
			if ( store.FindSubMap( ij.submap_id ) is null )
				throw new ArgumentException( $"map_instances instance '{ij.id}' points at missing sub-map '{ij.submap_id}'.", nameof( json ) );
			if ( !MapInstancesMath.IsFinite( FromJson( ij.origin ) ) || !float.IsFinite( ij.yaw_degrees ) )
				throw new ArgumentException( $"map_instances instance '{ij.id}' has a non-finite origin or yaw.", nameof( json ) );
			store._instances.Add( new MapInstance
			{
				Id = ij.id,
				Name = ij.name ?? "",
				SubMapId = ij.submap_id,
				SourcePath = ij.source_path ?? "",
				Prefix = ij.prefix ?? "",
				FixUp = ij.fixup,
				Origin = FromJson( ij.origin ),
				YawDegrees = ij.yaw_degrees,
				Layer = ij.layer ?? "",
				VisGroup = ij.visgroup ?? "",
			} );
			if ( ij.id.StartsWith( "mi-", StringComparison.Ordinal )
				&& int.TryParse( ij.id.AsSpan( 3 ), out int n ) && n >= store._nextInstance )
				store._nextInstance = n + 1;
		}
		foreach ( var pj in doc.proxies ?? new List<ProxyJson>() )
		{
			if ( pj is null ) throw new ArgumentException( "map_instances IO proxy row is null.", nameof( json ) );
			if ( string.IsNullOrWhiteSpace( pj.id ) ) throw new ArgumentException( "map_instances IO proxy is missing 'id'.", nameof( json ) );
			if ( !instanceIds.Add( pj.id ) ) throw new ArgumentException( $"map_instances IO proxy id '{pj.id}' collides with an instance id.", nameof( json ) );
			if ( store.FindInstance( pj.instance_id ) is null )
				throw new ArgumentException( $"map_instances IO proxy '{pj.id}' points at missing instance '{pj.instance_id}'.", nameof( json ) );
			var proxy = new InstanceIoProxy { Id = pj.id, InstanceId = pj.instance_id };
			foreach ( var rj in pj.inputs ?? new List<RouteJson>() )
			{
				if ( rj is null ) throw new ArgumentException( $"map_instances IO proxy '{pj.id}' has a null input leg.", nameof( json ) );
				proxy.Inputs.Add( new InstanceIoRoute
				{
					ProxyPort = rj.proxy_port ?? "",
					InnerTarget = rj.inner_target ?? "",
					InnerPort = rj.inner_port ?? "",
					Parameter = rj.parameter ?? "",
				} );
			}
			foreach ( var rj in pj.outputs ?? new List<RouteJson>() )
			{
				if ( rj is null ) throw new ArgumentException( $"map_instances IO proxy '{pj.id}' has a null output leg.", nameof( json ) );
				proxy.Outputs.Add( new InstanceIoRoute
				{
					ProxyPort = rj.proxy_port ?? "",
					InnerTarget = rj.inner_target ?? "",
					InnerPort = rj.inner_port ?? "",
					Parameter = rj.parameter ?? "",
				} );
			}
			store._proxies.Add( proxy );
			if ( pj.id.StartsWith( "iop-", StringComparison.Ordinal )
				&& int.TryParse( pj.id.AsSpan( 4 ), out int n ) && n >= store._nextProxy )
				store._nextProxy = n + 1;
		}
		return store;
	}
}
