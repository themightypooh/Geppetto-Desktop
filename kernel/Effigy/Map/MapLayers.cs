// Hammer Desktop P1: selection / grouping / layers / visgroups data model.
// Board task hammer-selection-grouping-layers-visgroups-model.
// Report §1.2 #22/#23/#24.
//
// This file is the model a future caller (Outliner, viewport tools, AI tools)
// uses. It is DATA MODEL ONLY: no UI, no viewport, no GL, no shelling out to
// Hammer / VBSP / VVIS / VRAD.
//
// What lives where (do not duplicate it here):
//   * Layer / VisGroup storage, eye toggles and compile-visible flags, and the
//     document-level CompileVisibleBrushes()/CompileVisibleEntities() filter
//     already live in MapDocument.cs (hammer-map-document-model). A MapLayer /
//     MapVisGroup is a named coloured set { Id, Name, Colour, Visible,
//     CompileVisible }; MapCompileSettings.CompileVisibleOnly is the
//     "compile visible objects only" switch. This file builds ON TOP of them.
//   * Linked copies (edit one copy, all update) already live in LinkedGroups.cs.
//     A MapGroup here is DIFFERENT: it binds live document objects that move
//     and edit as one, with IgnoreGroups editing a single member. It is not a
//     linked-copy definition and must not be confused with one.
//   * The compile runner itself lives in CompileRunner.cs. The seam this file
//     provides is ToCompileInput(): a CompileInput built from the
//     compile-visible brushes, which a caller hands to CompileRunner.Run.
//     (CompileRunner's header names the future adapter
//     CompileInput.FromDocument(MapDocument); when the integrator lands it,
//     it should delegate to MapLayers.ToCompileInput rather than re-deriving
//     the filter. This file does NOT edit CompileRunner.cs.)
//
// Persistence seam (known limitation, not a bug to work around silently):
// MapDocument has no history entries for group membership, selection, quick-hide
// or visgroup membership, so MapLayers state is SESSION state: it is not saved
// by MapDocumentIo and does not survive a reload. Group / selection / hidden
// edits go through the document's history API (MoveBrush / MoveEntity) where
// one exists, so the GEOMETRY moves are undoable; the grouping itself is not
// yet. Auto-visgroup membership writes VisGroupIds directly (the only route
// the document offers); same caveat.
//
// Visibility semantics (the two toggles are NOT the same):
//   * Eye (Visible): editor visibility in 2D/3D. VisibleBrushes() /
//     VisibleEntities() honour it (via the document) plus quick-hide.
//   * CompileVisible: compile visibility. CompileVisibleBrushes() /
//     CompileVisibleEntities() / ToCompileInput() honour it (via the
//     document) plus quick-hide, and only when CompileVisibleOnly is on.
//     When CompileVisibleOnly is off, a compile includes everything the
//     document holds — eye-hide and quick-hide included — exactly like
//     MapDocument.CompileVisibleBrushes() defines it.
//   * Quick-hide (HideSelected / HideUnselected / QuickHide) is per-object
//     editor hiding held here. It never touches the document's flags.

using System;
using System.Collections.Generic;
using System.Linq;
using Effigy.Map; // FuncDetail (func_detail classification) lives in Effigy.Map.

namespace Effigy;

/// <summary>Which kind of document object a <see cref="MapObjectRef"/> names.</summary>
public enum MapObjectKind
{
	Brush,
	Entity,
}

/// <summary>
/// One document object by kind + id. Brush and entity ids live in separate
/// lists in <see cref="MapDocument"/>, so the kind is part of the identity:
/// a brush "a1" and an entity "a1" are different objects.
/// </summary>
public readonly struct MapObjectRef : IEquatable<MapObjectRef>
{
	public MapObjectKind Kind { get; }
	public string Id { get; }

	public MapObjectRef( MapObjectKind kind, string id )
	{
		Kind = kind;
		Id = id ?? "";
	}

	public static MapObjectRef Brush( string id ) => new( MapObjectKind.Brush, id );
	public static MapObjectRef Entity( string id ) => new( MapObjectKind.Entity, id );

	public bool Equals( MapObjectRef other ) => Kind == other.Kind && Id == other.Id;
	public override bool Equals( object obj ) => obj is MapObjectRef other && Equals( other );
	public override int GetHashCode() => HashCode.Combine( Kind, Id );
	public override string ToString() => $"{Kind}:{Id}";
}

/// <summary>
/// Filter categories for the editor view (Hammer's filter bar). A brush or
/// entity belongs to exactly one category; see <see cref="MapLayers.ClassifyBrush"/>
/// and <see cref="MapLayers.ClassifyEntity"/> for the precedence.
/// </summary>
public enum MapFilterCategory
{
	/// <summary>Worldspawn brush: bound to no entity, no displacement, no tool-only faces.</summary>
	World,
	/// <summary>Brush bound to an entity, or a plain (non-model) entity.</summary>
	Entities,
	/// <summary>Brush carrying a displacement.</summary>
	Displacements,
	/// <summary>Brush whose faces are all tool textures (other than nodraw).</summary>
	ToolTextures,
	/// <summary>Brush whose faces are all nodraw.</summary>
	NoDraw,
	/// <summary>Entity with a model: classname prop_* or a "model" keyvalue.</summary>
	Models,
}

/// <summary>
/// Multi-selection: an ordered set of document objects. Order is insertion
/// order; adding an already-selected object is a no-op. This class knows no
/// document; <see cref="MapLayers"/> validates refs against its document.
/// </summary>
public sealed class MapSelection
{
	readonly List<MapObjectRef> _items = new();

	public IReadOnlyList<MapObjectRef> Items => _items;
	public int Count => _items.Count;
	public bool IsEmpty => _items.Count == 0;

	public bool Contains( MapObjectRef target ) => _items.Contains( target );

	public void Add( MapObjectRef target )
	{
		if ( string.IsNullOrWhiteSpace( target.Id ) )
			throw new ArgumentException( "Cannot select an object with a blank id.", nameof( target ) );
		if ( !_items.Contains( target ) )
			_items.Add( target );
	}

	public void AddRange( IEnumerable<MapObjectRef> targets )
	{
		if ( targets is null ) throw new ArgumentNullException( nameof( targets ) );
		foreach ( var t in targets )
			Add( t );
	}

	public bool Remove( MapObjectRef target ) => _items.Remove( target );

	public void Toggle( MapObjectRef target )
	{
		if ( !_items.Remove( target ) )
			Add( target );
	}

	public void Clear() => _items.Clear();

	public IReadOnlyList<MapObjectRef> Brushes => _items.Where( r => r.Kind == MapObjectKind.Brush ).ToList();
	public IReadOnlyList<MapObjectRef> Entities => _items.Where( r => r.Kind == MapObjectKind.Entity ).ToList();
}

/// <summary>
/// One group: live document objects bound so they move/edit as one. Members
/// are <see cref="MapObjectRef"/>s into the owning document, NOT copies: a
/// group move moves the document's own brushes/entities through the
/// document's history API, so it is undoable like any other edit.
/// </summary>
public sealed class MapGroup
{
	public string Id = "";
	public string Name = "";
	public List<MapObjectRef> Members = new();

	public MapGroup Clone() => new()
	{
		Id = Id,
		Name = Name,
		Members = new List<MapObjectRef>( Members ),
	};

	public bool SameAs( MapGroup other ) =>
		other is not null && Id == other.Id && Name == other.Name
		&& Members.SequenceEqual( other.Members );

	public bool Contains( MapObjectRef target ) => Members.Contains( target );

	public List<string> Validate()
	{
		var defects = new List<string>();
		if ( string.IsNullOrWhiteSpace( Id ) )
			defects.Add( "group has a blank id." );
		if ( string.IsNullOrWhiteSpace( Name ) )
			defects.Add( $"group '{Id}' has a blank name." );
		var seen = new HashSet<MapObjectRef>();
		foreach ( var m in Members )
		{
			if ( string.IsNullOrWhiteSpace( m.Id ) )
				defects.Add( $"group '{Id}' has a member with a blank id." );
			else if ( !seen.Add( m ) )
				defects.Add( $"group '{Id}' lists '{m}' twice." );
		}
		return defects;
	}
}

/// <summary>
/// Selection / grouping / layers / visgroups session state for one
/// <see cref="MapDocument"/>. Layers and visgroups themselves are stored on
/// the document (see the file header); this class owns groups, the selection,
/// quick-hide, filter categories and the compile-visible projection the
/// compile runner consumes.
/// </summary>
public sealed class MapLayers
{
	readonly MapDocument _doc;
	readonly List<MapGroup> _groups = new();
	readonly HashSet<MapObjectRef> _hidden = new();
	readonly HashSet<MapFilterCategory> _enabledCategories = new()
	{
		MapFilterCategory.World,
		MapFilterCategory.Entities,
		MapFilterCategory.Displacements,
		MapFilterCategory.ToolTextures,
		MapFilterCategory.NoDraw,
		MapFilterCategory.Models,
	};

	public MapLayers( MapDocument doc )
	{
		_doc = doc ?? throw new ArgumentNullException( nameof( doc ) );
	}

	public MapDocument Document => _doc;

	/// <summary>The live multi-selection.</summary>
	public MapSelection Selection { get; } = new();

	/// <summary>
	/// Hammer's "Ignore Groups": when true, an edit targets exactly the
	/// object picked, not its whole group. When false (the default), picking
	/// one member addresses the whole group. See <see cref="ExpandForEdit"/>.
	/// </summary>
	public bool IgnoreGroups { get; set; }

	public IReadOnlyList<MapGroup> Groups => _groups;

	// --- groups -----------------------------------------------------------------

	public MapGroup CreateGroup( string name, IEnumerable<MapObjectRef> members )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "A group needs a non-blank name.", nameof( name ) );
		if ( members is null ) throw new ArgumentNullException( nameof( members ) );
		var list = members.ToList();
		foreach ( var m in list )
			RequireExists( m );
		var group = new MapGroup { Id = MapDocument.NewId(), Name = name, Members = list };
		_groups.Add( group );
		return group;
	}

	public MapGroup FindGroup( string id ) =>
		string.IsNullOrWhiteSpace( id ) ? null : _groups.FirstOrDefault( g => g.Id == id );

	public MapGroup GroupOf( MapObjectRef target ) => _groups.FirstOrDefault( g => g.Contains( target ) );

	public bool DeleteGroup( string id )
	{
		var group = FindGroup( id );
		if ( group is null ) return false;
		_groups.Remove( group );
		return true;
	}

	public void AddToGroup( string groupId, MapObjectRef member )
	{
		var group = FindGroup( groupId ) ?? throw new InvalidOperationException( $"No group '{groupId}'." );
		RequireExists( member );
		if ( !group.Members.Contains( member ) )
			group.Members.Add( member );
	}

	public bool RemoveFromGroup( string groupId, MapObjectRef member )
	{
		var group = FindGroup( groupId ) ?? throw new InvalidOperationException( $"No group '{groupId}'." );
		return group.Members.Remove( member );
	}

	/// <summary>
	/// What an edit actually targets. With <see cref="IgnoreGroups"/> off, a
	/// ref that belongs to a group expands to the whole group (Hammer's
	/// default: groups move/edit as one). With it on, refs pass through
	/// unchanged, so one brush inside a group can be edited alone. Order is
	/// preserved and duplicates are removed.
	/// </summary>
	public IReadOnlyList<MapObjectRef> ExpandForEdit( IEnumerable<MapObjectRef> refs )
	{
		if ( refs is null ) throw new ArgumentNullException( nameof( refs ) );
		var outList = new List<MapObjectRef>();
		foreach ( var r in refs )
		{
			if ( IgnoreGroups )
			{
				if ( !outList.Contains( r ) ) outList.Add( r );
				continue;
			}
			var group = GroupOf( r );
			IEnumerable<MapObjectRef> expanded = group is null ? new[] { r } : group.Members;
			foreach ( var m in expanded )
				if ( !outList.Contains( m ) ) outList.Add( m );
		}
		return outList;
	}

	// --- moving (groups move as one; ignore-groups moves one member) ------------

	/// <summary>
	/// Move a whole group by <paramref name="delta"/>: every member brush
	/// shifts by the delta (via the document, so each move is a history
	/// entry), every member entity shifts its origin by the delta, and
	/// brushes bound to a member entity move with it. A brush listed both
	/// directly and through its entity moves once. All members are validated
	/// first, so a bad member fails before anything moves. Returns the
	/// objects that moved.
	/// </summary>
	public IReadOnlyList<MapObjectRef> MoveGroup( string groupId, Vec3 delta )
	{
		var group = FindGroup( groupId ) ?? throw new InvalidOperationException( $"No group '{groupId}'." );
		return MoveRefs( group.Members, delta );
	}

	/// <summary>
	/// Ignore-groups edit: move exactly one member of a group by
	/// <paramref name="delta"/>, leaving its group-mates where they are. The
	/// member must belong to the group. A brush entity member carries its
	/// bound brushes with it.
	/// </summary>
	public IReadOnlyList<MapObjectRef> MoveMember( string groupId, MapObjectRef member, Vec3 delta )
	{
		var group = FindGroup( groupId ) ?? throw new InvalidOperationException( $"No group '{groupId}'." );
		if ( !group.Contains( member ) )
			throw new InvalidOperationException( $"Object '{member}' is not a member of group '{groupId}'." );
		return MoveRefs( new[] { member }, delta );
	}

	/// <summary>Move the current selection by <paramref name="delta"/>, expanding through <see cref="ExpandForEdit"/>.</summary>
	public IReadOnlyList<MapObjectRef> MoveSelection( Vec3 delta ) =>
		MoveRefs( ExpandForEdit( Selection.Items ), delta );

	IReadOnlyList<MapObjectRef> MoveRefs( IReadOnlyList<MapObjectRef> refs, Vec3 delta )
	{
		if ( !float.IsFinite( delta.x ) || !float.IsFinite( delta.y ) || !float.IsFinite( delta.z ) )
			throw new ArgumentException( $"Move delta ({delta.x}, {delta.y}, {delta.z}) is not finite.", nameof( delta ) );
		// Collect + validate everything first: a bad member must fail before
		// any brush moves, or the group would be left half-moved.
		var brushMoves = new List<(string Id, Vec3 Min, Vec3 Max)>();
		var entityMoves = new List<(string Id, Vec3 Origin)>();
		var seenBrushes = new HashSet<string>( StringComparer.Ordinal );
		var seenEntities = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var r in refs )
		{
			if ( r.Kind == MapObjectKind.Brush )
			{
				CollectBrushMove( r.Id, delta, brushMoves, seenBrushes );
			}
			else
			{
				var entity = _doc.FindEntity( r.Id )
					?? throw new InvalidOperationException( $"No entity '{r.Id}'." );
				if ( seenEntities.Add( entity.Id ) )
					entityMoves.Add( ( entity.Id, entity.Origin + delta ) );
				foreach ( var brushId in entity.BrushIds )
					CollectBrushMove( brushId, delta, brushMoves, seenBrushes );
			}
		}
		// Apply through the document's history API (one entry per object).
		// A pure translation keeps each brush's size, and RemapBrush is a
		// pure translation at scale 1, so faces move exactly by the delta.
		foreach ( var (id, min, max) in brushMoves )
			_doc.MoveBrush( id, min, max );
		foreach ( var (id, origin) in entityMoves )
			_doc.MoveEntity( id, origin );
		var moved = new List<MapObjectRef>();
		foreach ( var (id, _, _) in brushMoves ) moved.Add( MapObjectRef.Brush( id ) );
		foreach ( var (id, _) in entityMoves ) moved.Add( MapObjectRef.Entity( id ) );
		return moved;
	}

	void CollectBrushMove( string brushId, Vec3 delta,
		List<(string Id, Vec3 Min, Vec3 Max)> moves, HashSet<string> seen )
	{
		if ( !seen.Add( brushId ) ) return;
		var brush = _doc.FindBrush( brushId )
			?? throw new InvalidOperationException( $"No brush '{brushId}'." );
		if ( !brush.TryBounds( out var min, out var max ) )
			throw new InvalidOperationException( $"Brush '{brushId}' has no solvable bounds, so it cannot be moved." );
		moves.Add( ( brushId, min + delta, max + delta ) );
	}

	// --- selection ---------------------------------------------------------------

	public void AddToSelection( MapObjectRef target )
	{
		RequireExists( target );
		Selection.Add( target );
	}

	public void SelectGroup( string groupId )
	{
		var group = FindGroup( groupId ) ?? throw new InvalidOperationException( $"No group '{groupId}'." );
		Selection.AddRange( group.Members );
	}

	public bool RemoveFromSelection( MapObjectRef target ) => Selection.Remove( target );
	public void ClearSelection() => Selection.Clear();
	public bool IsSelected( MapObjectRef target ) => Selection.Contains( target );

	// --- quick-hide ---------------------------------------------------------------

	/// <summary>Hide one object (editor-only; see the file header).</summary>
	public void Hide( MapObjectRef target )
	{
		RequireExists( target );
		_hidden.Add( target );
	}

	public bool Unhide( MapObjectRef target ) => _hidden.Remove( target );
	public bool IsHidden( MapObjectRef target ) => _hidden.Contains( target );
	public int HiddenCount => _hidden.Count;

	/// <summary>Hide the current selection.</summary>
	public void HideSelected()
	{
		foreach ( var r in Selection.Items )
			_hidden.Add( r );
	}

	/// <summary>
	/// Hide everything in the document that is NOT selected (raw selection,
	/// no group expansion: a group-mate that is not itself selected is hidden).
	/// </summary>
	public void HideUnselected()
	{
		foreach ( var b in _doc.Brushes )
			if ( !Selection.Contains( MapObjectRef.Brush( b.Id ) ) )
				_hidden.Add( MapObjectRef.Brush( b.Id ) );
		foreach ( var e in _doc.Entities )
			if ( !Selection.Contains( MapObjectRef.Entity( e.Id ) ) )
				_hidden.Add( MapObjectRef.Entity( e.Id ) );
	}

	public void UnhideAll() => _hidden.Clear();

	// --- editor + compile visibility ----------------------------------------------

	/// <summary>Editor visibility: the document's layer/visgroup eye state, minus quick-hide.</summary>
	public bool IsEditorVisible( MapBrushData brush ) =>
		brush is not null && _doc.VisibleBrushes().Any( b => b.Id == brush.Id ) && !IsHidden( MapObjectRef.Brush( brush.Id ) );

	/// <summary>Editor visibility: the document's layer/visgroup eye state, minus quick-hide.</summary>
	public bool IsEditorVisible( MapEntityData entity ) =>
		entity is not null && _doc.VisibleEntities().Any( e => e.Id == entity.Id ) && !IsHidden( MapObjectRef.Entity( entity.Id ) );

	public IReadOnlyList<MapBrushData> VisibleBrushes() =>
		_doc.VisibleBrushes().Where( b => !IsHidden( MapObjectRef.Brush( b.Id ) ) ).ToList();

	public IReadOnlyList<MapEntityData> VisibleEntities() =>
		_doc.VisibleEntities().Where( e => !IsHidden( MapObjectRef.Entity( e.Id ) ) ).ToList();

	/// <summary>
	/// Compile visibility: the document's compile-visible filter (layer /
	/// visgroup CompileVisible flags, gated by CompileVisibleOnly), minus
	/// quick-hide. When CompileVisibleOnly is off the document's filter
	/// returns everything, and so does this.
	/// </summary>
	public bool IsCompileVisible( MapBrushData brush ) =>
		brush is not null && _doc.CompileVisibleBrushes().Any( b => b.Id == brush.Id ) && !IsHidden( MapObjectRef.Brush( brush.Id ) );

	/// <summary>Compile visibility (see <see cref="IsCompileVisible(MapBrushData)"/>).</summary>
	public bool IsCompileVisible( MapEntityData entity ) =>
		entity is not null && _doc.CompileVisibleEntities().Any( e => e.Id == entity.Id ) && !IsHidden( MapObjectRef.Entity( entity.Id ) );

	public IReadOnlyList<MapBrushData> CompileVisibleBrushes() =>
		_doc.CompileVisibleBrushes().Where( b => !IsHidden( MapObjectRef.Brush( b.Id ) ) ).ToList();

	public IReadOnlyList<MapEntityData> CompileVisibleEntities() =>
		_doc.CompileVisibleEntities().Where( e => !IsHidden( MapObjectRef.Entity( e.Id ) ) ).ToList();

	// --- filters -------------------------------------------------------------------

	public IReadOnlyList<MapFilterCategory> EnabledCategories => _enabledCategories.ToList();

	public void SetCategoryEnabled( MapFilterCategory category, bool enabled )
	{
		if ( enabled ) _enabledCategories.Add( category );
		else _enabledCategories.Remove( category );
	}

	public bool IsCategoryEnabled( MapFilterCategory category ) => _enabledCategories.Contains( category );

	/// <summary>
	/// Which filter category a brush is. Precedence: a displacement brush is
	/// Displacements whatever its faces are; then all-nodraw faces are
	/// NoDraw; then all-tool faces are ToolTextures; then a brush bound to an
	/// entity is Entities; otherwise World.
	/// </summary>
	public MapFilterCategory ClassifyBrush( MapBrushData brush )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( _doc.Displacements.Any( d => d.BrushId == brush.Id ) )
			return MapFilterCategory.Displacements;
		if ( brush.Faces.Count > 0 && brush.Faces.All( f => ToolTexture.IsNoDraw( f.Material?.MaterialName ?? "" ) ) )
			return MapFilterCategory.NoDraw;
		if ( brush.Faces.Count > 0 && brush.Faces.All( f => ToolTexture.IsTool( f.Material?.MaterialName ?? "" ) ) )
			return MapFilterCategory.ToolTextures;
		if ( _doc.Entities.Any( e => e.BrushIds.Contains( brush.Id ) ) )
			return MapFilterCategory.Entities;
		return MapFilterCategory.World;
	}

	/// <summary>Which filter category an entity is: Models for prop_* classes or a "model" keyvalue, else Entities.</summary>
	public MapFilterCategory ClassifyEntity( MapEntityData entity )
	{
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		if ( ( entity.ClassName ?? "" ).StartsWith( "prop_", StringComparison.OrdinalIgnoreCase )
			|| entity.KeyValues.ContainsKey( "model" ) )
			return MapFilterCategory.Models;
		return MapFilterCategory.Entities;
	}

	/// <summary>Editor-visible brushes whose filter category is enabled.</summary>
	public IReadOnlyList<MapBrushData> FilteredBrushes() =>
		VisibleBrushes().Where( b => IsCategoryEnabled( ClassifyBrush( b ) ) ).ToList();

	/// <summary>Editor-visible entities whose filter category is enabled.</summary>
	public IReadOnlyList<MapEntityData> FilteredEntities() =>
		VisibleEntities().Where( e => IsCategoryEnabled( ClassifyEntity( e ) ) ).ToList();

	// --- compile input (the seam the compile runner consumes) ----------------------

	/// <summary>
	/// Build the <see cref="CompileInput"/> a compile runs on, from the
	/// compile-visible brushes: faces become plane faces (normal + distance,
	/// material name, lightmap scale); a brush bound to a func_detail entity
	/// is detail (real visible geometry, excluded from the BSP solid, per
	/// CompileBrush); lights come from compile-visible "light" entities and
	/// the player start from the compile-visible "info_player_start".
	/// Brush ids are document-order ints, like MapDocument.ToIssueDocument().
	/// </summary>
	public CompileInput ToCompileInput()
	{
		var input = new CompileInput( _doc.Name );
		var brushes = CompileVisibleBrushes();
		var detailBrushIds = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var entity in CompileVisibleEntities() )
			if ( FuncDetail.IsFuncDetailClass( entity.ClassName ) )
				foreach ( var id in entity.BrushIds )
					detailBrushIds.Add( id );
		var index = 0;
		foreach ( var brush in brushes )
		{
			var faces = new List<CompileFace>();
			foreach ( var face in brush.Faces )
			{
				faces.Add( new CompileFace(
					face.Normal,
					face.Distance,
					string.IsNullOrWhiteSpace( face.Material?.MaterialName ) ? "default" : face.Material.MaterialName )
				{
					LightmapScale = face.Material?.Uv.LightmapScale ?? FaceUv.DefaultLightmapScale,
				} );
			}
			input.Brushes.Add( new CompileBrush(
				string.IsNullOrWhiteSpace( brush.Name ) ? brush.Id : brush.Name,
				index++,
				faces,
				detailBrushIds.Contains( brush.Id ) ) );
		}
		foreach ( var entity in CompileVisibleEntities() )
		{
			if ( string.Equals( entity.ClassName, "light", StringComparison.OrdinalIgnoreCase ) )
				input.Lights.Add( new CompileLight( entity.Origin ) );
			else if ( string.Equals( entity.ClassName, "info_player_start", StringComparison.OrdinalIgnoreCase )
				&& input.PlayerStart is null )
				input.PlayerStart = entity.Origin;
		}
		return input;
	}

	// --- validation -----------------------------------------------------------------

	/// <summary>Defects in this session state: dangling group members, selection and hidden refs.</summary>
	public List<string> Validate()
	{
		var defects = new List<string>();
		foreach ( var group in _groups )
		{
			defects.AddRange( group.Validate() );
			foreach ( var m in group.Members )
				if ( !Exists( m ) )
					defects.Add( $"group '{group.Id}' references missing {m.Kind.ToString().ToLowerInvariant()} '{m.Id}'." );
		}
		foreach ( var r in Selection.Items )
			if ( !Exists( r ) )
				defects.Add( $"selection references missing {r.Kind.ToString().ToLowerInvariant()} '{r.Id}'." );
		foreach ( var r in _hidden )
			if ( !Exists( r ) )
				defects.Add( $"hidden set references missing {r.Kind.ToString().ToLowerInvariant()} '{r.Id}'." );
		return defects;
	}

	bool Exists( MapObjectRef target ) =>
		target.Kind == MapObjectKind.Brush ? _doc.FindBrush( target.Id ) is not null : _doc.FindEntity( target.Id ) is not null;

	void RequireExists( MapObjectRef target )
	{
		if ( string.IsNullOrWhiteSpace( target.Id ) )
			throw new ArgumentException( "An object reference needs a non-blank id.", nameof( target ) );
		if ( !Exists( target ) )
			throw new InvalidOperationException( $"No {target.Kind.ToString().ToLowerInvariant()} '{target.Id}'." );
	}

	// --- auto-visgroups ---------------------------------------------------------------

	/// <summary>Auto-visgroup for func_detail brushes.</summary>
	public const string AutoFuncDetailId = "auto-func-detail";
	/// <summary>Auto-visgroup for all entities.</summary>
	public const string AutoEntitiesId = "auto-entities";
	/// <summary>Auto-visgroup for displacement brushes.</summary>
	public const string AutoDisplacementsId = "auto-displacements";
	/// <summary>Auto-visgroup for tool-texture brushes.</summary>
	public const string AutoToolTexturesId = "auto-tool-textures";

	/// <summary>
	/// Ensure the four auto-visgroups exist and assign membership: func_detail
	/// brushes (bound to a func_detail-class entity) to Auto: func_detail,
	/// every entity to Auto: Entities, displacement brushes to Auto:
	/// Displacements, brushes with any tool-texture face to Auto: Tool
	/// Textures. Idempotent: a second call creates nothing and duplicates no
	/// membership. Returns the four visgroups (created or already present).
	///
	/// NOTE: membership is written to VisGroupIds directly — MapDocument has
	/// no history entry for it (see the file header). A brush or entity in
	/// two auto-visgroups is normal: VisGroupIds is a set of memberships.
	/// </summary>
	public static IReadOnlyList<MapVisGroup> EnsureAutoVisGroups( MapDocument doc )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		var groups = new List<MapVisGroup>
		{
			EnsureVisGroup( doc, AutoFuncDetailId, "Auto: func_detail", "#E67E22" ),
			EnsureVisGroup( doc, AutoEntitiesId, "Auto: Entities", "#3498DB" ),
			EnsureVisGroup( doc, AutoDisplacementsId, "Auto: Displacements", "#9B59B6" ),
			EnsureVisGroup( doc, AutoToolTexturesId, "Auto: Tool Textures", "#95A5A6" ),
		};
		var funcDetailBrushIds = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var entity in doc.Entities )
		{
			AddVisGroupId( entity.VisGroupIds, AutoEntitiesId );
			if ( FuncDetail.IsFuncDetailClass( entity.ClassName ) )
				foreach ( var id in entity.BrushIds )
					funcDetailBrushIds.Add( id );
		}
		foreach ( var brush in doc.Brushes )
		{
			if ( funcDetailBrushIds.Contains( brush.Id ) )
				AddVisGroupId( brush.VisGroupIds, AutoFuncDetailId );
			if ( doc.Displacements.Any( d => d.BrushId == brush.Id ) )
				AddVisGroupId( brush.VisGroupIds, AutoDisplacementsId );
			if ( brush.Faces.Any( f => ToolTexture.IsTool( f.Material?.MaterialName ?? "" ) ) )
				AddVisGroupId( brush.VisGroupIds, AutoToolTexturesId );
		}
		return groups;
	}

	static MapVisGroup EnsureVisGroup( MapDocument doc, string id, string name, string colour )
	{
		var existing = doc.FindVisGroup( id );
		if ( existing is not null ) return existing;
		var visGroup = new MapVisGroup { Id = id, Name = name, Colour = colour, Visible = true, CompileVisible = true };
		doc.AddVisGroup( visGroup );
		return doc.FindVisGroup( id );
	}

	static void AddVisGroupId( List<string> ids, string id )
	{
		if ( !ids.Contains( id ) )
			ids.Add( id );
	}
}
