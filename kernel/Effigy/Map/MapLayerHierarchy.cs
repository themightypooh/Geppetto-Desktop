// MapLayerHierarchy — Hammer Desktop P2: hierarchical layers + auto-visgroups +
// Mark / select-all-in-group (board hammer-visgroups-hierarchical-layers).
//
// WHAT THIS FILE IS. The P2 extension of the flat layer/visgroup foundation in
// MapDocument.cs (MapLayer / MapVisGroup: id, name, colour, eye toggle,
// compile-visible flag; brushes/entities carry LayerId + VisGroupIds). It builds
// on the REAL types only — no stubs, no invented MapLayers API:
//
//   * HIERARCHY. MapLayer is flat and MapDocument.cs is owned by another worker
//     (hammer-map-document-model, file locked), so the parent/child tree lives
//     HERE as an overlay: MapLayerNode (ParentId + nullable visibility /
//     compile-visible overrides) keyed by layer id. Effective visibility walks
//     up the tree: a hidden parent hides its children, and a child override
//     changes only its own flag — it cannot re-show a child of a hidden parent
//     (TrenchBroom layers behave the same way).
//   * AUTO-VISGROUPS. Five stable groups, assigned by object kind on creation:
//     World Geometry, Tool Brushes, World Detail, Entities, Instance. A
//     func_detail brush (bound to a func_detail-class entity — FuncDetail.
//     IsFuncDetailClass, the real API) lands in World Detail; an all-tool-
//     texture brush (ToolTexture.IsTool, the real API) lands in Tool Brushes.
//   * MARK / SELECT-ALL. MarkGroup(id) highlights one visgroup; MarkedMembers
//     and SelectAllInGroup(id) return exactly that group's members (brushes
//     then entities, in document order) for query results / selection.
//
// DEPENDENCY NOTE. The P1 ticket (hammer-selection-grouping-layers-visgroups-
// model) that this ticket depends on had NOT landed MapLayers.cs when this was
// written — there is no MapLayers.cs on disk and no Group / Selection /
// HideSelected / QuickHide API to build on. This file therefore does NOT
// create MapLayers.cs (that filename is the P1 worker's) and does NOT invent
// the P1 Group type. The final integrator merges this overlay into MapLayers /
// the document history when P1 lands.
//
// PERSISTENCE CAVEAT. Auto-visgroup assignment mutates the in-memory
// VisGroupIds list of the brush/entity (there is no add-brush-visgroup history
// op in MapDocument.cs, and that file is locked). A later document save
// persists the membership because the saver writes VisGroupIds, but undo does
// not record the assignment. The integrator wires a history op for it.
//
// DESKTOP UI: none. Backend / data-model / API only, per the ticket.
// No UI code, no viewer, no shelling out to Hammer.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Map;

/// <summary>Which auto-visgroup an object belongs to, by kind.</summary>
public enum AutoVisGroupKind
{
	WorldGeometry,
	ToolBrushes,
	WorldDetail,
	Entities,
	Instances,
}

/// <summary>Whether a group member is a brush or an entity.</summary>
public enum MapGroupMemberKind
{
	Brush,
	Entity,
}

/// <summary>One member of a visgroup / layer query result: kind + stable id.</summary>
public sealed class MapGroupMember : IEquatable<MapGroupMember>
{
	public MapGroupMemberKind Kind;
	public string Id = "";

	public MapGroupMember() { }

	public MapGroupMember( MapGroupMemberKind kind, string id )
	{
		Kind = kind;
		Id = id ?? "";
	}

	public bool Equals( MapGroupMember other ) =>
		other is not null && Kind == other.Kind && Id == other.Id;

	public override bool Equals( object obj ) => Equals( obj as MapGroupMember );

	public override int GetHashCode() => HashCode.Combine( Kind, Id );

	public override string ToString() => $"{( Kind == MapGroupMemberKind.Brush ? "brush" : "entity" )}:{Id}";
}

/// <summary>
/// One layer's place in the hierarchy: its parent ("" = root) plus optional
/// overrides. A null override means "inherit my own MapLayer flag"; a set
/// override replaces that flag for this layer only — it never overrides an
/// ancestor, so a hidden parent still hides the child.
/// </summary>
public sealed class MapLayerNode
{
	public string ParentId = "";
	public bool? VisibleOverride;
	public bool? CompileVisibleOverride;

	public MapLayerNode Clone() => new()
	{
		ParentId = ParentId,
		VisibleOverride = VisibleOverride,
		CompileVisibleOverride = CompileVisibleOverride,
	};
}

/// <summary>
/// Hierarchical layers + auto-visgroups + Mark/select-all-in-group over a
/// <see cref="MapDocument"/>. The document owns the layers/visgroups/brushes/
/// entities; this class owns only the tree overlay, the auto-visgroup rules
/// and the marked group. One hierarchy per document.
/// </summary>
public sealed class MapLayerHierarchy
{
	public MapDocument Document { get; }

	readonly Dictionary<string, MapLayerNode> _nodes = new( StringComparer.Ordinal );

	string _markedVisGroupId = "";

	public MapLayerHierarchy( MapDocument document )
	{
		Document = document ?? throw new ArgumentNullException( nameof( document ) );
	}

	/// <summary>Hierarchy nodes, keyed by layer id. Layers with no parent and no override have no node.</summary>
	public IReadOnlyDictionary<string, MapLayerNode> Nodes => _nodes;

	/// <summary>The currently marked visgroup id, or "" when nothing is marked.</summary>
	public string MarkedVisGroupId => _markedVisGroupId;

	// --- auto-visgroup identity ------------------------------------------------------

	public const string AutoWorldGeometryId = "auto-world-geometry";
	public const string AutoToolBrushesId = "auto-tool-brushes";
	public const string AutoWorldDetailId = "auto-world-detail";
	public const string AutoEntitiesId = "auto-entities";
	public const string AutoInstancesId = "auto-instances";

	public static string AutoVisGroupId( AutoVisGroupKind kind ) => kind switch
	{
		AutoVisGroupKind.WorldGeometry => AutoWorldGeometryId,
		AutoVisGroupKind.ToolBrushes => AutoToolBrushesId,
		AutoVisGroupKind.WorldDetail => AutoWorldDetailId,
		AutoVisGroupKind.Entities => AutoEntitiesId,
		AutoVisGroupKind.Instances => AutoInstancesId,
		_ => throw new ArgumentOutOfRangeException( nameof( kind ), kind, "Unknown auto-visgroup kind." ),
	};

	public static string AutoVisGroupName( AutoVisGroupKind kind ) => kind switch
	{
		AutoVisGroupKind.WorldGeometry => "World Geometry",
		AutoVisGroupKind.ToolBrushes => "Tool Brushes",
		AutoVisGroupKind.WorldDetail => "World Detail",
		AutoVisGroupKind.Entities => "Entities",
		AutoVisGroupKind.Instances => "Instance",
		_ => throw new ArgumentOutOfRangeException( nameof( kind ), kind, "Unknown auto-visgroup kind." ),
	};

	public static string AutoVisGroupColour( AutoVisGroupKind kind ) => kind switch
	{
		AutoVisGroupKind.WorldGeometry => "#808080",
		AutoVisGroupKind.ToolBrushes => "#E0A030",
		AutoVisGroupKind.WorldDetail => "#40A0E0",
		AutoVisGroupKind.Entities => "#40C040",
		AutoVisGroupKind.Instances => "#C040C0",
		_ => throw new ArgumentOutOfRangeException( nameof( kind ), kind, "Unknown auto-visgroup kind." ),
	};

	// --- hierarchy -------------------------------------------------------------------

	/// <summary>
	/// Set a layer's parent ("" or null = root). Throws naming the problem when
	/// the layer or the parent is unknown, the layer would parent itself, or the
	/// new parent would create a cycle.
	/// </summary>
	public void SetParent( string layerId, string parentId )
	{
		if ( string.IsNullOrWhiteSpace( layerId ) )
			throw new ArgumentException( "A layer needs a non-blank id.", nameof( layerId ) );
		if ( Document.FindLayer( layerId ) is null )
			throw new InvalidOperationException( $"No layer '{layerId}'." );
		parentId ??= "";
		if ( parentId.Length > 0 )
		{
			if ( Document.FindLayer( parentId ) is null )
				throw new InvalidOperationException( $"No layer '{parentId}'." );
			if ( parentId == layerId )
				throw new InvalidOperationException( $"Layer '{layerId}' cannot be its own parent." );
			var walk = parentId;
			var seen = new HashSet<string>( StringComparer.Ordinal );
			while ( walk.Length > 0 )
			{
				if ( !seen.Add( walk ) || walk == layerId )
					throw new InvalidOperationException(
						$"Setting '{layerId}' under '{parentId}' would create a cycle in the layer tree." );
				walk = NodeOrNull( walk )?.ParentId ?? "";
			}
		}
		NodeFor( layerId ).ParentId = parentId;
	}

	/// <summary>Parent layer id, or "" for a root layer (or a layer with no node).</summary>
	public string ParentOf( string layerId ) => NodeOrNull( layerId )?.ParentId ?? "";

	/// <summary>Direct children of a layer, in document layer order.</summary>
	public IReadOnlyList<string> ChildrenOf( string layerId ) =>
		Document.Layers
			.Where( l => ParentOf( l.Id ) == layerId )
			.Select( l => l.Id )
			.ToList();

	/// <summary>All descendants of a layer (children, grandchildren, ...), in document layer order.</summary>
	public IReadOnlyList<string> Descendants( string layerId )
	{
		var result = new List<string>();
		var queue = new Queue<string>( ChildrenOf( layerId ) );
		while ( queue.Count > 0 )
		{
			var next = queue.Dequeue();
			result.Add( next );
			foreach ( var child in ChildrenOf( next ) )
				queue.Enqueue( child );
		}
		return result;
	}

	/// <summary>Root layers (no parent), in document layer order.</summary>
	public IReadOnlyList<string> RootLayers() =>
		Document.Layers.Where( l => ParentOf( l.Id ).Length == 0 ).Select( l => l.Id ).ToList();

	/// <summary>Override a layer's own eye toggle (null clears the override back to the MapLayer flag).</summary>
	public void SetVisibleOverride( string layerId, bool? visible )
	{
		RequireLayer( layerId );
		NodeFor( layerId ).VisibleOverride = visible;
	}

	/// <summary>Override a layer's own compile-visible flag (null clears the override).</summary>
	public void SetCompileVisibleOverride( string layerId, bool? compileVisible )
	{
		RequireLayer( layerId );
		NodeFor( layerId ).CompileVisibleOverride = compileVisible;
	}

	/// <summary>
	/// Effective eye visibility: the layer's own flag (or its override) AND every
	/// ancestor's effective visibility. Hiding a parent hides the children even
	/// when a child overrides itself visible.
	/// </summary>
	public bool EffectiveVisible( string layerId )
	{
		var layer = RequireLayer( layerId );
		var node = NodeOrNull( layerId );
		var own = node?.VisibleOverride ?? layer.Visible;
		if ( !own )
			return false;
		var parent = node?.ParentId ?? "";
		return parent.Length == 0 || EffectiveVisible( parent );
	}

	/// <summary>Effective compile visibility: same inheritance as <see cref="EffectiveVisible"/>, on the compile-visible flag.</summary>
	public bool EffectiveCompileVisible( string layerId )
	{
		var layer = RequireLayer( layerId );
		var node = NodeOrNull( layerId );
		var own = node?.CompileVisibleOverride ?? layer.CompileVisible;
		if ( !own )
			return false;
		var parent = node?.ParentId ?? "";
		return parent.Length == 0 || EffectiveCompileVisible( parent );
	}

	// --- per-object visibility -------------------------------------------------------

	/// <summary>
	/// A brush is visible when its layer (if any) is effectively visible and
	/// every visgroup it belongs to is visible. A dangling layer/visgroup id is
	/// treated as visible here (never silently hide geometry); see
	/// <see cref="Validate"/> — dangling references are defects, not silent clears.
	/// </summary>
	public bool IsVisible( MapBrushData brush )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		return LayerVisible( brush.LayerId, EffectiveVisible ) && VisGroupsVisible( brush.VisGroupIds, v => v.Visible );
	}

	/// <summary>Same as <see cref="IsVisible(MapBrushData)"/>, for entities.</summary>
	public bool IsVisible( MapEntityData entity )
	{
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		return LayerVisible( entity.LayerId, EffectiveVisible ) && VisGroupsVisible( entity.VisGroupIds, v => v.Visible );
	}

	/// <summary>
	/// A brush is compile-visible when its layer (if any) is effectively
	/// compile-visible and every visgroup it belongs to is compile-visible.
	/// This is the per-object half of the compile-visible filter; the document
	/// level CompileSettings.CompileVisibleOnly switch is the caller's.
	/// </summary>
	public bool IsCompileVisible( MapBrushData brush )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		return LayerVisible( brush.LayerId, EffectiveCompileVisible ) && VisGroupsVisible( brush.VisGroupIds, v => v.CompileVisible );
	}

	/// <summary>Same as <see cref="IsCompileVisible(MapBrushData)"/>, for entities.</summary>
	public bool IsCompileVisible( MapEntityData entity )
	{
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		return LayerVisible( entity.LayerId, EffectiveCompileVisible ) && VisGroupsVisible( entity.VisGroupIds, v => v.CompileVisible );
	}

	bool LayerVisible( string layerId, Func<string, bool> effective )
	{
		if ( string.IsNullOrWhiteSpace( layerId ) )
			return true;
		if ( Document.FindLayer( layerId ) is null )
			return true;
		return effective( layerId );
	}

	bool VisGroupsVisible( List<string> visGroupIds, Func<MapVisGroup, bool> flag )
	{
		foreach ( var id in visGroupIds )
		{
			var group = Document.FindVisGroup( id );
			if ( group is not null && !flag( group ) )
				return false;
		}
		return true;
	}

	// --- auto-visgroups --------------------------------------------------------------

	/// <summary>Create the five auto-visgroups in the document if missing. Returns the ids created by this call (empty when all five already existed).</summary>
	public IReadOnlyList<string> EnsureAutoVisGroups()
	{
		var created = new List<string>();
		foreach ( var kind in Enum.GetValues<AutoVisGroupKind>() )
		{
			var id = AutoVisGroupId( kind );
			if ( Document.FindVisGroup( id ) is not null )
				continue;
			Document.AddVisGroup( new MapVisGroup
			{
				Id = id,
				Name = AutoVisGroupName( kind ),
				Colour = AutoVisGroupColour( kind ),
				Visible = true,
				CompileVisible = true,
			} );
			created.Add( id );
		}
		return created;
	}

	/// <summary>
	/// Classify a brush by kind: bound to a func_detail-class entity
	/// (FuncDetail.IsFuncDetailClass) → World Detail; every face a tool texture
	/// (ToolTexture.IsTool) → Tool Brushes; anything else → World Geometry.
	/// A func_detail brush wins over the tool test: func_detail is the more
	/// specific classification. A faceless brush is World Geometry.
	/// </summary>
	public AutoVisGroupKind ClassifyBrush( MapBrushData brush )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		foreach ( var entity in Document.Entities )
		{
			if ( entity.BrushIds.Contains( brush.Id ) && FuncDetail.IsFuncDetailClass( entity.ClassName ) )
				return AutoVisGroupKind.WorldDetail;
		}
		if ( brush.Faces.Count > 0 && brush.Faces.All( f => ToolTexture.IsTool( f.Material.MaterialName ) ) )
			return AutoVisGroupKind.ToolBrushes;
		return AutoVisGroupKind.WorldGeometry;
	}

	/// <summary>
	/// Classify an entity by kind: func_instance → Instance; a func_detail-class
	/// brush entity → World Detail (it travels with its brushes); anything else
	/// (point entities, lights, triggers, ...) → Entities.
	/// </summary>
	public AutoVisGroupKind ClassifyEntity( MapEntityData entity )
	{
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		if ( string.Equals( entity.ClassName, "func_instance", StringComparison.OrdinalIgnoreCase ) )
			return AutoVisGroupKind.Instances;
		if ( FuncDetail.IsFuncDetailClass( entity.ClassName ) )
			return AutoVisGroupKind.WorldDetail;
		return AutoVisGroupKind.Entities;
	}

	/// <summary>Assign a brush to its auto-visgroup (creating the group first when missing). Returns the kind assigned. Other memberships are kept.</summary>
	public AutoVisGroupKind AssignAutoVisGroup( MapBrushData brush )
	{
		var kind = ClassifyBrush( brush );
		EnsureVisGroup( kind );
		var id = AutoVisGroupId( kind );
		if ( !brush.VisGroupIds.Contains( id ) )
			brush.VisGroupIds.Add( id );
		return kind;
	}

	/// <summary>Assign an entity to its auto-visgroup (creating the group first when missing). Returns the kind assigned. Other memberships are kept.</summary>
	public AutoVisGroupKind AssignAutoVisGroup( MapEntityData entity )
	{
		var kind = ClassifyEntity( entity );
		EnsureVisGroup( kind );
		var id = AutoVisGroupId( kind );
		if ( !entity.VisGroupIds.Contains( id ) )
			entity.VisGroupIds.Add( id );
		return kind;
	}

	/// <summary>Assign every brush and entity in the document to its auto-visgroup. Returns how many objects landed in each kind.</summary>
	public IReadOnlyDictionary<AutoVisGroupKind, int> AssignAll()
	{
		EnsureAutoVisGroups();
		var counts = Enum.GetValues<AutoVisGroupKind>().ToDictionary( k => k, _ => 0 );
		foreach ( var brush in Document.Brushes )
			counts[AssignAutoVisGroup( brush )]++;
		foreach ( var entity in Document.Entities )
			counts[AssignAutoVisGroup( entity )]++;
		return counts;
	}

	void EnsureVisGroup( AutoVisGroupKind kind )
	{
		var id = AutoVisGroupId( kind );
		if ( Document.FindVisGroup( id ) is null )
			EnsureAutoVisGroups();
	}

	// --- Mark / select-all-in-group --------------------------------------------------

	/// <summary>Mark a visgroup: its members are the highlighted set (see <see cref="MarkedMembers"/>). Throws when the visgroup is unknown.</summary>
	public void MarkGroup( string visGroupId )
	{
		if ( string.IsNullOrWhiteSpace( visGroupId ) )
			throw new ArgumentException( "A visgroup needs a non-blank id.", nameof( visGroupId ) );
		if ( Document.FindVisGroup( visGroupId ) is null )
			throw new InvalidOperationException( $"No visgroup '{visGroupId}'." );
		_markedVisGroupId = visGroupId;
	}

	/// <summary>Clear the mark.</summary>
	public void Unmark() => _markedVisGroupId = "";

	/// <summary>Members of the marked visgroup, or empty when nothing is marked.</summary>
	public IReadOnlyList<MapGroupMember> MarkedMembers() =>
		_markedVisGroupId.Length == 0
			? Array.Empty<MapGroupMember>()
			: SelectAllInGroup( _markedVisGroupId );

	/// <summary>
	/// Exactly the members of a visgroup: brushes then entities, each in document
	/// order. Throws when the visgroup is unknown.
	/// </summary>
	public IReadOnlyList<MapGroupMember> SelectAllInGroup( string visGroupId )
	{
		if ( Document.FindVisGroup( visGroupId ) is null )
			throw new InvalidOperationException( $"No visgroup '{visGroupId}'." );
		var members = new List<MapGroupMember>();
		foreach ( var brush in Document.Brushes )
			if ( brush.VisGroupIds.Contains( visGroupId ) )
				members.Add( new MapGroupMember( MapGroupMemberKind.Brush, brush.Id ) );
		foreach ( var entity in Document.Entities )
			if ( entity.VisGroupIds.Contains( visGroupId ) )
				members.Add( new MapGroupMember( MapGroupMemberKind.Entity, entity.Id ) );
		return members;
	}

	// --- validation ------------------------------------------------------------------

	/// <summary>
	/// Hierarchy defects: nodes pointing at layers that no longer exist in the
	/// document, parents that no longer exist, cycles (defensive — SetParent
	/// refuses them), and brushes/entities referencing missing layers or
	/// visgroups. Empty means clean. Removing a layer never rewrites the
	/// objects that pointed at it — that is the document's own rule, and this
	/// reports it instead of silently clearing it.
	/// </summary>
	public List<string> Validate()
	{
		var defects = new List<string>();
		foreach ( var (layerId, node) in _nodes )
		{
			if ( Document.FindLayer( layerId ) is null )
				defects.Add( $"hierarchy node for missing layer '{layerId}'." );
			if ( node.ParentId.Length > 0 && Document.FindLayer( node.ParentId ) is null )
				defects.Add( $"layer '{layerId}' has a missing parent '{node.ParentId}'." );
		}
		foreach ( var layer in Document.Layers )
		{
			var seen = new HashSet<string>( StringComparer.Ordinal );
			var walk = layer.Id;
			while ( walk.Length > 0 )
			{
				if ( !seen.Add( walk ) )
				{
					defects.Add( $"layer hierarchy has a cycle at '{walk}'." );
					break;
				}
				walk = NodeOrNull( walk )?.ParentId ?? "";
			}
		}
		foreach ( var brush in Document.Brushes )
		{
			if ( brush.LayerId.Length > 0 && Document.FindLayer( brush.LayerId ) is null )
				defects.Add( $"brush '{brush.Id}' references missing layer '{brush.LayerId}'." );
			foreach ( var id in brush.VisGroupIds )
				if ( Document.FindVisGroup( id ) is null )
					defects.Add( $"brush '{brush.Id}' references missing visgroup '{id}'." );
		}
		foreach ( var entity in Document.Entities )
		{
			if ( entity.LayerId.Length > 0 && Document.FindLayer( entity.LayerId ) is null )
				defects.Add( $"entity '{entity.Id}' references missing layer '{entity.LayerId}'." );
			foreach ( var id in entity.VisGroupIds )
				if ( Document.FindVisGroup( id ) is null )
					defects.Add( $"entity '{entity.Id}' references missing visgroup '{id}'." );
		}
		return defects;
	}

	// --- helpers ---------------------------------------------------------------------

	MapLayerNode NodeOrNull( string layerId ) =>
		_nodes.TryGetValue( layerId, out var node ) ? node : null;

	MapLayerNode NodeFor( string layerId )
	{
		if ( !_nodes.TryGetValue( layerId, out var node ) )
		{
			node = new MapLayerNode();
			_nodes[layerId] = node;
		}
		return node;
	}

	MapLayer RequireLayer( string layerId )
	{
		if ( string.IsNullOrWhiteSpace( layerId ) )
			throw new ArgumentException( "A layer needs a non-blank id.", nameof( layerId ) );
		return Document.FindLayer( layerId )
			?? throw new InvalidOperationException( $"No layer '{layerId}'." );
	}
}
