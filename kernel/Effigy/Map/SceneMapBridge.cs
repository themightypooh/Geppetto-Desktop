// SceneMapBridge — Hammer Desktop interop: shared scene / layer model.
// Board: hammer-interop-shared-scene-layer-model (Geppetto Desktop).
//
// WHAT THIS IS. One scene model, not two: map layers / visgroups convert onto the
// Desktop scene-layer model (UnifiedLayerStack / UnifiedLayerEntry), assembly
// occurrences convert onto map instances and back, and a .gtscn text scene can
// reference a map document. Backend / data-model only; no UI.
//
// SEAMS — READ BEFORE WIRING (both dependencies were unlanded when this was written):
//   1. MAP SIDE IS SPEC-SHAPED, NOT THE CANONICAL TYPE. The layers-model worker
//      (hammer-selection-grouping-layers-visgroups-model, HELD) owns the canonical
//      MapLayers.cs / MapDocument.cs types and the instances worker (worker 59,
//      hammer-sprinkles-prefabs-instances) owns MapInstances.cs. Neither file existed
//      on disk when this bridge was written, so this file defines small adapter
//      records (MapLayerData, MapVisGroupData, MapInstanceData) that mirror the
//      FILED SPECS exactly — see each record's doc comment for the spec line it
//      mirrors. The bridge never invents a MapLayers/MapDocument/MapInstances type:
//      the integrator replaces each adapter with a one-line mapping from the real
//      type (same field names, same string ids) without touching bridge logic.
//      All cross-document binding is by STRING ID (layer name, occurrence id,
//      sub-map path); the bridge never holds a MapDocument reference.
//   2. SCENE SIDE IS REAL. UnifiedLayerStack / UnifiedLayerEntry (kernel/Effigy)
//      and AssemblyOccurrence / AssemblyTransform (kernel/Effigy/Assembly) are
//      used directly, not stubbed. A map layer becomes a stack entry with no paint
//      or sculpt content of its own (it groups/owns map objects, not pixels or
//      deltas); Kind is Paint as the neutral default. The stack entry carries
//      Name + Group + Enabled only, so Colour and CompileVisible live in this
//      bridge's sidecar (keyed by entry name) and round-trip losslessly through
//      the bridge; a bare scene entry with no bridge history converts with the
//      documented defaults (white, CompileVisible == Enabled). Visgroup member
//      lists are map-side only: a bare scene entry yields an empty member list.
//   3. .GTSCN IS TEXT-LEVEL, NOT A TextSceneDocument REFERENCE. TextSceneDocument
//      lives in core/ (Geppetto.Core), which references the kernel — the engine-free
//      kernel cannot reference it back (RULES.md #8). So this bridge speaks the
//      .gtscn line format itself: a map reference is an `asset` record with
//      Kind "map" (`asset\t<id>\tmap\t<path>`), optionally placed by a `node`
//      whose Name equals the asset id and an `xform` for that node. That is already
//      valid .gtscn: TextSceneDocument.Parse accepts Kind "map" (kind is a free
//      string) and the node pairing is this bridge's documented convention. The
//      core-side adapter is two lines in the integrator's hands; the tests below
//      prove the round-trip through the REAL TextSceneDocument.
//   4. NO HAMMER. Nothing here shells out to Hammer / Hammer++ / VBSP / VVIS /
//      VRAD, and nothing reads a .vmf/.bsp. Native Geppetto data only.
//
// INVALID IS A NAMED REASON, NEVER A SILENT FIX (MapBrush convention).
// Converters throw ArgumentException naming the defect; the Validate* methods
// return the same codes without throwing. Fix-up never renames an original:
// ApplyNamePrefix / FixUpTargetNames return new strings and are idempotent
// (a name that already carries the prefix is left alone).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Effigy.Assembly;

namespace Effigy.Map
{
	/// <summary>
	/// Map layer, filed-spec shape (hammer-selection-grouping-layers-visgroups-model:
	/// "Layer/VisGroup (name, colour, eye toggle, compile-visible flag)").
	/// ADAPTER, not the canonical type — the layers-model worker owns MapLayers.cs;
	/// the integrator maps the real type onto this record field for field.
	/// </summary>
	public sealed class MapLayerData
	{
		/// <summary>Stable string id. Empty means "the Name is the id" (see the bridge header).</summary>
		public string Id { get; set; } = "";
		public string Name { get; set; } = "";
		/// <summary>Colour as "#RRGGBB". Validated, never silently repaired.</summary>
		public string ColorHex { get; set; } = "#ffffff";
		/// <summary>Eye toggle: visible in 2D/3D. Maps to UnifiedLayerEntry.Enabled.</summary>
		public bool Visible { get; set; } = true;
		/// <summary>Compile-visible flag: included when compiling visible objects only.</summary>
		public bool CompileVisible { get; set; } = true;
	}

	/// <summary>
	/// Map visgroup, filed-spec shape (same ticket: "visgroups are named coloured
	/// sets with visibility toggles in 2D/3D and at compile time"). ADAPTER — see
	/// <see cref="MapLayerData"/>. MemberObjectIds are string ids of the brushes /
	/// entities in the set; they are map-side only and do not survive onto a scene
	/// entry except through this bridge's sidecar.
	/// </summary>
	public sealed class MapVisGroupData
	{
		public string Id { get; set; } = "";
		public string Name { get; set; } = "";
		public string ColorHex { get; set; } = "#ffffff";
		public bool Visible { get; set; } = true;
		public bool CompileVisible { get; set; } = true;
		public List<string> MemberObjectIds { get; set; } = new List<string>();
	}

	/// <summary>
	/// Map instance, filed-spec shape (hammer-sprinkles-prefabs-instances:
	/// "func_instance (path to a sub-map + name-prefix fix-up for targets)").
	/// ADAPTER, not the canonical type — worker 59 owns MapInstances.cs; the
	/// integrator maps the real type onto this record field for field. The
	/// transform is carried as the assembly basis + origin (three float[3] each),
	/// the same numbers AssemblyTransform stores, so occurrence &lt;-&gt; instance
	/// is exact with no Euler decomposition anywhere. Visible is a bridge
	/// extension (occurrences carry it; the filed instance spec does not list
	/// it) and defaults to true.
	/// </summary>
	public sealed class MapInstanceData
	{
		public string Id { get; set; } = "";
		public string Name { get; set; } = "";
		/// <summary>Path to the referenced sub-map document (Geppetto-native; a VMF sub-map is readable via the VMF importer, not here).</summary>
		public string SubMapPath { get; set; } = "";
		/// <summary>Name prefix applied to the instance's copies of target names (InstanceAuto-style). Empty = no fix-up.</summary>
		public string NamePrefix { get; set; } = "";
		public bool Visible { get; set; } = true;
		public float[] BasisX { get; set; } = new[] { 1f, 0f, 0f };
		public float[] BasisY { get; set; } = new[] { 0f, 1f, 0f };
		public float[] BasisZ { get; set; } = new[] { 0f, 0f, 1f };
		public float[] Origin { get; set; } = new[] { 0f, 0f, 0f };
	}

	/// <summary>
	/// A .gtscn map reference: one map document referenced from a text scene.
	/// On the wire it is an `asset` record with Kind "map" (see the file header);
	/// NodeId / Position / RotationDegrees / Scale are present only when the
	/// reference is placed by a node in the scene.
	/// </summary>
	public sealed class MapReference
	{
		public string AssetId { get; set; } = "";
		public string Name { get; set; } = "";
		public string Path { get; set; } = "";
		/// <summary>Scene layer the placing node sits on, or "" for none. Must be a declared layer when a node is emitted.</summary>
		public string LayerName { get; set; } = "";
		/// <summary>Id of the placing node, or "" for an unplaced (asset-only) reference. A placed reference needs one: an xform has nothing to attach to without it.</summary>
		public string NodeId { get; set; } = "";
		public float[] Position { get; set; }
		public float[] RotationDegrees { get; set; }
		public float[] Scale { get; set; }
	}

	/// <summary>Result of resolving a map instance against a sub-map lookup.</summary>
	public sealed class ResolvedMapInstance
	{
		public MapInstanceData Instance { get; set; }
		/// <summary>True when the sub-map path resolved through the supplied lookup.</summary>
		public bool Exists { get; set; }
		/// <summary>Empty when <see cref="Exists"/>; otherwise the named reason ("missing-sub-map: ...", "missing-path: ...", "unresolved: ...").</summary>
		public string Reason { get; set; } = "";
		/// <summary>The sub-map's target names with the instance prefix applied. Empty when the instance does not resolve. The originals are never renamed.</summary>
		public List<string> ResolvedTargetNames { get; set; } = new List<string>();
	}

	/// <summary>
	/// The shared scene / layer model bridge. Instance state is only the layer
	/// sidecar (colour / compile flag / kind / visgroup members, keyed by scene
	/// entry name); every other method is a pure conversion. One bridge instance
	/// per document being converted: sidecars are per-document, never global.
	/// </summary>
	public sealed class SceneMapBridge
	{
		/// <summary>Stack group map layers land in.</summary>
		public const string MapGroupName = "Map";
		/// <summary>Stack group visgroups land in.</summary>
		public const string VisGroupName = "VisGroup";
		/// <summary>.gtscn asset Kind for a map reference.</summary>
		public const string MapAssetKind = "map";
		/// <summary>Colour a scene entry with no bridge history converts back with.</summary>
		public const string DefaultColorHex = "#ffffff";

		sealed class LayerSidecar
		{
			public string ColorHex = DefaultColorHex;
			public bool CompileVisible = true;
			public string Kind = "layer";
			public List<string> MemberObjectIds = new List<string>();
		}

		readonly Dictionary<string, LayerSidecar> _sidecar = new Dictionary<string, LayerSidecar>( StringComparer.Ordinal );

		// ---------------------------------------------------------------- layers

		/// <summary>Map layer -&gt; scene-layer entry. Records colour / compile flag / kind in the sidecar so the way back is lossless. Throws on a missing name or a bad colour.</summary>
		public UnifiedLayerEntry ToSceneEntry( MapLayerData layer )
		{
			if ( layer is null ) throw new ArgumentNullException( nameof( layer ) );
			RequireName( layer.Name );
			RequireColor( layer.ColorHex );
			_sidecar[layer.Name] = new LayerSidecar
			{
				ColorHex = layer.ColorHex, CompileVisible = layer.CompileVisible, Kind = "layer",
			};
			return new UnifiedLayerEntry
			{
				Kind = UnifiedLayerKind.Paint,
				Name = layer.Name,
				Group = MapGroupName,
				Enabled = layer.Visible,
			};
		}

		/// <summary>Map visgroup -&gt; scene-layer entry. Records colour / compile flag / kind / members in the sidecar so the way back is lossless. Throws on a missing name or a bad colour.</summary>
		public UnifiedLayerEntry ToSceneEntry( MapVisGroupData visgroup )
		{
			if ( visgroup is null ) throw new ArgumentNullException( nameof( visgroup ) );
			RequireName( visgroup.Name );
			RequireColor( visgroup.ColorHex );
			_sidecar[visgroup.Name] = new LayerSidecar
			{
				ColorHex = visgroup.ColorHex, CompileVisible = visgroup.CompileVisible, Kind = "visgroup",
				MemberObjectIds = new List<string>( visgroup.MemberObjectIds ?? new List<string>() ),
			};
			return new UnifiedLayerEntry
			{
				Kind = UnifiedLayerKind.Paint,
				Name = visgroup.Name,
				Group = VisGroupName,
				Enabled = visgroup.Visible,
			};
		}

		/// <summary>Add a converted entry to a stack: registers its group and appends it. Throws when the stack already has an entry of that name (the stack's own uniqueness rule).</summary>
		public static void AddToStack( UnifiedLayerStack stack, UnifiedLayerEntry entry )
		{
			if ( stack is null ) throw new ArgumentNullException( nameof( stack ) );
			if ( entry is null ) throw new ArgumentNullException( nameof( entry ) );
			if ( string.IsNullOrWhiteSpace( entry.Name ) )
				throw new ArgumentException( "missing-name: a layer entry needs a name.", nameof( entry ) );
			if ( stack.Find( entry.Name ) is not null )
				throw new ArgumentException( $"duplicate-name: the stack already has an entry named '{entry.Name}'.", nameof( entry ) );
			stack.AddGroup( entry.Group );
			stack.Entries.Add( entry );
		}

		/// <summary>Scene-layer entry -&gt; map layer. Colour / compile flag come from the sidecar when this bridge converted the entry; otherwise the documented defaults (white, CompileVisible == Enabled). The id is the entry name: map layers are string-bound to scene entry names.</summary>
		public MapLayerData ToMapLayer( UnifiedLayerEntry entry )
		{
			if ( entry is null ) throw new ArgumentNullException( nameof( entry ) );
			var sidecar = SidecarFor( entry.Name );
			return new MapLayerData
			{
				Id = entry.Name,
				Name = entry.Name,
				ColorHex = sidecar is null ? DefaultColorHex : sidecar.ColorHex,
				Visible = entry.Enabled,
				CompileVisible = sidecar is null ? entry.Enabled : sidecar.CompileVisible,
			};
		}

		/// <summary>Scene-layer entry -&gt; map visgroup. Members come from the sidecar when this bridge converted the entry; otherwise empty (member lists are map-side only).</summary>
		public MapVisGroupData ToMapVisGroup( UnifiedLayerEntry entry )
		{
			if ( entry is null ) throw new ArgumentNullException( nameof( entry ) );
			var sidecar = SidecarFor( entry.Name );
			return new MapVisGroupData
			{
				Id = entry.Name,
				Name = entry.Name,
				ColorHex = sidecar is null ? DefaultColorHex : sidecar.ColorHex,
				Visible = entry.Enabled,
				CompileVisible = sidecar is null ? entry.Enabled : sidecar.CompileVisible,
				MemberObjectIds = sidecar is null ? new List<string>() : new List<string>( sidecar.MemberObjectIds ),
			};
		}

		/// <summary>What this bridge recorded for a scene entry name ("layer" / "visgroup"), or false when the entry has no bridge history. Lets the integrator tell a layer entry from a visgroup entry without guessing from the group name.</summary>
		public bool TryGetRecordedKind( string entryName, out string kind, out string colorHex, out bool compileVisible )
		{
			kind = ""; colorHex = DefaultColorHex; compileVisible = true;
			var sidecar = SidecarFor( entryName );
			if ( sidecar is null ) return false;
			kind = sidecar.Kind; colorHex = sidecar.ColorHex; compileVisible = sidecar.CompileVisible;
			return true;
		}

		LayerSidecar SidecarFor( string entryName )
			=> string.IsNullOrEmpty( entryName ) ? null
				: _sidecar.TryGetValue( entryName, out var sidecar ) ? sidecar : null;

		// ------------------------------------------------------- occurrences/instances

		/// <summary>Assembly occurrence -&gt; map instance: the occurrence is placed as an instance of the sub-map at <paramref name="subMapPath"/>. The instance prefix is the occurrence name plus "_". Throws on a missing occurrence name or an empty sub-map path.</summary>
		public static MapInstanceData OccurrenceToInstance( AssemblyOccurrence occurrence, string subMapPath )
		{
			if ( occurrence is null ) throw new ArgumentNullException( nameof( occurrence ) );
			RequireName( occurrence.Name );
			if ( string.IsNullOrWhiteSpace( subMapPath ) )
				throw new ArgumentException( "missing-path: placing an occurrence as a map instance needs a sub-map path.", nameof( subMapPath ) );
			var transform = occurrence.Transform ?? new AssemblyTransform();
			RequireVector3( transform.X ); RequireVector3( transform.Y );
			RequireVector3( transform.Z ); RequireVector3( transform.Origin );
			return new MapInstanceData
			{
				Id = occurrence.Id ?? "",
				Name = occurrence.Name,
				SubMapPath = subMapPath,
				NamePrefix = occurrence.Name + "_",
				Visible = occurrence.Visible,
				BasisX = Clone3( transform.X ),
				BasisY = Clone3( transform.Y ),
				BasisZ = Clone3( transform.Z ),
				Origin = Clone3( transform.Origin ),
			};
		}

		/// <summary>Map instance -&gt; assembly occurrence in the assembly whose definition id is <paramref name="definitionId"/> (string-bound: the sub-map path is not an assembly definition id, the caller supplies the mapping). Throws on a missing instance name or definition id.</summary>
		public static AssemblyOccurrence InstanceToOccurrence( MapInstanceData instance, string definitionId )
		{
			if ( instance is null ) throw new ArgumentNullException( nameof( instance ) );
			RequireName( instance.Name );
			if ( string.IsNullOrWhiteSpace( definitionId ) )
				throw new ArgumentException( "missing-definition-id: an instance needs an assembly definition id to become an occurrence.", nameof( definitionId ) );
			RequireVector3( instance.BasisX ); RequireVector3( instance.BasisY );
			RequireVector3( instance.BasisZ ); RequireVector3( instance.Origin );
			return new AssemblyOccurrence
			{
				Id = instance.Id ?? "",
				DefinitionId = definitionId,
				Name = instance.Name,
				Visible = instance.Visible,
				Transform = new AssemblyTransform
				{
					X = Clone3( instance.BasisX ),
					Y = Clone3( instance.BasisY ),
					Z = Clone3( instance.BasisZ ),
					Origin = Clone3( instance.Origin ),
				},
			};
		}

		/// <summary>InstanceAuto-style prefixing: the prefixed copy of one target name. A name that already carries the prefix is returned unchanged (never double-prefixed); an empty prefix is a no-op.</summary>
		public static string ApplyNamePrefix( string targetName, string prefix )
		{
			if ( string.IsNullOrEmpty( targetName ) ) return targetName ?? "";
			if ( string.IsNullOrEmpty( prefix ) ) return targetName;
			return targetName.StartsWith( prefix, StringComparison.Ordinal ) ? targetName : prefix + targetName;
		}

		/// <summary>The instance's copies of a sub-map's target names, each prefixed. Returns new strings; the supplied names are never renamed.</summary>
		public static List<string> FixUpTargetNames( IEnumerable<string> targetNames, string prefix )
		{
			var fixedUp = new List<string>();
			if ( targetNames is null ) return fixedUp;
			foreach ( var name in targetNames )
				fixedUp.Add( ApplyNamePrefix( name, prefix ) );
			return fixedUp;
		}

		/// <summary>Resolve a map instance: the sub-map must exist through <paramref name="subMapExists"/>, and its target names come back prefixed. Never throws for a missing sub-map — that is an Exists=false result naming the path.</summary>
		public static ResolvedMapInstance ResolveInstance( MapInstanceData instance, Func<string, bool> subMapExists, IEnumerable<string> subMapTargetNames )
		{
			if ( instance is null ) throw new ArgumentNullException( nameof( instance ) );
			if ( string.IsNullOrWhiteSpace( instance.SubMapPath ) )
				return new ResolvedMapInstance { Instance = instance, Exists = false, Reason = "missing-path: the instance has no sub-map path." };
			if ( subMapExists is null )
				return new ResolvedMapInstance { Instance = instance, Exists = false, Reason = "unresolved: no sub-map lookup was supplied." };
			if ( !subMapExists( instance.SubMapPath ) )
				return new ResolvedMapInstance { Instance = instance, Exists = false, Reason = $"missing-sub-map: '{instance.SubMapPath}' was not found." };
			return new ResolvedMapInstance
			{
				Instance = instance,
				Exists = true,
				ResolvedTargetNames = FixUpTargetNames( subMapTargetNames, instance.NamePrefix ),
			};
		}

		/// <summary>One call for the acceptance case: an assembly occurrence placed as a map instance of <paramref name="subMapPath"/>, resolved through <paramref name="subMapExists"/>.</summary>
		public static ResolvedMapInstance ResolveOccurrence( AssemblyOccurrence occurrence, string subMapPath, Func<string, bool> subMapExists, IEnumerable<string> subMapTargetNames )
			=> ResolveInstance( OccurrenceToInstance( occurrence, subMapPath ), subMapExists, subMapTargetNames );

		// ------------------------------------------------------------------- .gtscn

		/// <summary>Canonical .gtscn lines for one map reference: the `asset` record (Kind "map"), plus the placing `node` and its `xform` when the reference is placed. The placing node's Name is the asset id — that is the pairing rule ParseGtscnMapReferences uses. A placed reference needs a node id; an unplaced reference is the asset line alone. Throws FormatException on a tab / line break inside any field (the .gtscn rule).</summary>
		public static string[] ToGtscnLines( MapReference reference )
		{
			if ( reference is null ) throw new ArgumentNullException( nameof( reference ) );
			if ( string.IsNullOrWhiteSpace( reference.AssetId ) )
				throw new ArgumentException( "missing-id: a map reference needs an asset id.", nameof( reference ) );
			if ( string.IsNullOrWhiteSpace( reference.Name ) )
				throw new ArgumentException( "missing-name: a map reference needs a name.", nameof( reference ) );
			if ( string.IsNullOrWhiteSpace( reference.Path ) )
				throw new ArgumentException( "missing-path: a map reference needs a map document path.", nameof( reference ) );
			var hasTransform = reference.Position is not null || reference.RotationDegrees is not null || reference.Scale is not null;
			if ( hasTransform && string.IsNullOrWhiteSpace( reference.NodeId ) )
				throw new ArgumentException( "missing-node-id: a placed map reference needs a node id for its xform.", nameof( reference ) );
			var lines = new List<string>
			{
				"asset\t" + Field( reference.AssetId ) + "\t" + MapAssetKind + "\t" + Field( reference.Path ),
			};
			if ( !string.IsNullOrWhiteSpace( reference.NodeId ) )
			{
				// The placing node's Name IS the asset id: that is the pairing rule
				// ParseGtscnMapReferences uses to find the node for an asset.
				lines.Add( "node\t" + Field( reference.NodeId ) + "\t-\t" + Field( reference.AssetId ) + "\t"
					+ Field( string.IsNullOrEmpty( reference.LayerName ) ? "-" : reference.LayerName ) );
				if ( hasTransform )
					lines.Add( "xform\t" + Field( reference.NodeId ) + "\t"
						+ Vector( reference.Position ) + "\t" + Vector( reference.RotationDegrees ) + "\t" + Vector( reference.Scale ) );
			}
			return lines.ToArray();
		}

		/// <summary>Find every map reference in .gtscn text (a whole file or a pasted block: a "gtscn 1" header is accepted when present, not required). An asset of Kind "map" is a reference; the node whose Name equals the asset id places it, and that node's xform (when present) is the placement. Malformed map assets and duplicate asset ids throw FormatException with a line number, mirroring TextSceneDocument.Parse; every other line is ignored.</summary>
		public static List<MapReference> ParseGtscnMapReferences( string text )
		{
			if ( text is null ) throw new ArgumentNullException( nameof( text ) );
			var assets = new Dictionary<string, MapReference>( StringComparer.Ordinal );
			var nodesByName = new Dictionary<string, (string NodeId, string LayerName)>( StringComparer.Ordinal );
			var xformsByNode = new Dictionary<string, (float[] Position, float[] RotationDegrees, float[] Scale)>( StringComparer.Ordinal );
			var lines = text.Replace( "\r\n", "\n" ).Split( '\n' );
			for ( var i = 0; i < lines.Length; i++ )
			{
				var line = lines[i];
				if ( line.Length == 0 ) continue;
				var lineNo = i + 1;
				var tab = line.IndexOf( '\t' );
				var head = tab < 0 ? line : line[..tab];
				var rest = tab < 0 ? "" : line[( tab + 1 )..];
				if ( head == "gtscn" )
				{
					if ( rest != "1" ) throw new FormatException( $"Line {lineNo}: unsupported text scene version '{rest}'." );
					continue;
				}
				string[] Fields() => rest.Split( '\t' );
				switch ( head )
				{
					case "asset":
					{
						var f = Fields();
						if ( f.Length != 3 ) throw new FormatException( $"Line {lineNo}: asset needs id, kind, path." );
						if ( f[1] != MapAssetKind ) break;
						if ( assets.ContainsKey( f[0] ) ) throw new FormatException( $"Line {lineNo}: duplicate asset id '{f[0]}'." );
						assets[f[0]] = new MapReference { AssetId = f[0], Path = f[2] };
						break;
					}
					case "node":
					{
						var f = Fields();
						if ( f.Length != 4 ) throw new FormatException( $"Line {lineNo}: node needs id, parent, name, layer." );
						// Only the node that places a map asset matters here; keep the first
						// node per name so a stray same-named node cannot steal the placement.
						if ( !nodesByName.ContainsKey( f[2] ) )
							nodesByName[f[2]] = ( f[0], f[3] == "-" ? "" : f[3] );
						break;
					}
					case "xform":
					{
						var f = Fields();
						if ( f.Length != 4 ) throw new FormatException( $"Line {lineNo}: xform needs node id, position, rotation, scale." );
						xformsByNode[f[0]] = ( ParseVector( f[1], lineNo ), ParseVector( f[2], lineNo ), ParseVector( f[3], lineNo ) );
						break;
					}
				}
			}
			var found = new List<MapReference>();
			foreach ( var reference in assets.Values.OrderBy( r => r.AssetId, StringComparer.Ordinal ) )
			{
				if ( nodesByName.TryGetValue( reference.AssetId, out var node ) )
				{
					reference.Name = reference.AssetId;
					reference.NodeId = node.NodeId;
					reference.LayerName = node.LayerName;
					if ( xformsByNode.TryGetValue( node.NodeId, out var xform ) )
					{
						reference.Position = xform.Position;
						reference.RotationDegrees = xform.RotationDegrees;
						reference.Scale = xform.Scale;
					}
				}
				else
				{
					reference.Name = reference.AssetId;
				}
				found.Add( reference );
			}
			return found;
		}

		// ---------------------------------------------------------------- validation

		/// <summary>Named defects in a map layer ("missing-name: ...", "bad-colour: ...", "missing-id: ..."). Empty = valid. Never throws.</summary>
		public static List<string> ValidateLayer( MapLayerData layer )
		{
			var issues = new List<string>();
			if ( layer is null ) { issues.Add( "missing-layer: no layer was supplied." ); return issues; }
			if ( string.IsNullOrWhiteSpace( layer.Id ) ) issues.Add( "missing-id: the layer has no id (the name is used as its id)." );
			if ( string.IsNullOrWhiteSpace( layer.Name ) ) issues.Add( "missing-name: a layer needs a name." );
			if ( !IsColorHex( layer.ColorHex ) ) issues.Add( $"bad-colour: '{layer.ColorHex}' is not #RRGGBB." );
			return issues;
		}

		/// <summary>Named defects in a map visgroup. Empty = valid. Never throws.</summary>
		public static List<string> ValidateVisGroup( MapVisGroupData visgroup )
		{
			var issues = new List<string>();
			if ( visgroup is null ) { issues.Add( "missing-visgroup: no visgroup was supplied." ); return issues; }
			if ( string.IsNullOrWhiteSpace( visgroup.Id ) ) issues.Add( "missing-id: the visgroup has no id (the name is used as its id)." );
			if ( string.IsNullOrWhiteSpace( visgroup.Name ) ) issues.Add( "missing-name: a visgroup needs a name." );
			if ( !IsColorHex( visgroup.ColorHex ) ) issues.Add( $"bad-colour: '{visgroup.ColorHex}' is not #RRGGBB." );
			if ( visgroup.MemberObjectIds is not null )
				foreach ( var member in visgroup.MemberObjectIds )
					if ( string.IsNullOrWhiteSpace( member ) ) { issues.Add( "missing-member-id: a visgroup member id is empty." ); break; }
			return issues;
		}

		/// <summary>Named defects in a map instance. Empty = valid. Never throws.</summary>
		public static List<string> ValidateInstance( MapInstanceData instance )
		{
			var issues = new List<string>();
			if ( instance is null ) { issues.Add( "missing-instance: no instance was supplied." ); return issues; }
			if ( string.IsNullOrWhiteSpace( instance.Id ) ) issues.Add( "missing-id: the instance has no id." );
			if ( string.IsNullOrWhiteSpace( instance.Name ) ) issues.Add( "missing-name: an instance needs a name." );
			if ( string.IsNullOrWhiteSpace( instance.SubMapPath ) ) issues.Add( "missing-path: an instance needs a sub-map path." );
			if ( !string.IsNullOrEmpty( instance.NamePrefix ) && instance.NamePrefix.Any( char.IsWhiteSpace ) )
				issues.Add( $"bad-prefix: '{instance.NamePrefix}' contains whitespace, so it cannot prefix a target name." );
			if ( !IsVector3( instance.BasisX ) || !IsVector3( instance.BasisY ) || !IsVector3( instance.BasisZ ) || !IsVector3( instance.Origin ) )
				issues.Add( "bad-transform: basis and origin each need exactly three finite numbers." );
			return issues;
		}

		/// <summary>Named defects in a .gtscn map reference. Empty = valid. Never throws.</summary>
		public static List<string> ValidateMapReference( MapReference reference )
		{
			var issues = new List<string>();
			if ( reference is null ) { issues.Add( "missing-reference: no map reference was supplied." ); return issues; }
			if ( string.IsNullOrWhiteSpace( reference.AssetId ) ) issues.Add( "missing-id: a map reference needs an asset id." );
			if ( string.IsNullOrWhiteSpace( reference.Name ) ) issues.Add( "missing-name: a map reference needs a name." );
			if ( string.IsNullOrWhiteSpace( reference.Path ) ) issues.Add( "missing-path: a map reference needs a map document path." );
			var hasTransform = reference.Position is not null || reference.RotationDegrees is not null || reference.Scale is not null;
			if ( hasTransform && string.IsNullOrWhiteSpace( reference.NodeId ) ) issues.Add( "missing-node-id: a placed map reference needs a node id for its xform." );
			if ( hasTransform && ( !IsVector3( reference.Position ) || !IsVector3( reference.RotationDegrees ) || !IsVector3( reference.Scale ) ) )
				issues.Add( "bad-transform: position, rotation and scale each need exactly three finite numbers." );
			foreach ( var field in new[] { reference.AssetId, reference.Name, reference.Path, reference.LayerName, reference.NodeId } )
				if ( field is not null && ( field.Contains( '\t' ) || field.Contains( '\n' ) || field.Contains( '\r' ) ) )
				{ issues.Add( "bad-field: a .gtscn field cannot contain a tab or a line break." ); break; }
			return issues;
		}

		// ------------------------------------------------------------------- helpers

		static void RequireName( string name )
		{
			if ( string.IsNullOrWhiteSpace( name ) )
				throw new ArgumentException( "missing-name: a name is required.", nameof( name ) );
		}

		static void RequireColor( string colorHex )
		{
			if ( !IsColorHex( colorHex ) )
				throw new ArgumentException( $"bad-colour: '{colorHex}' is not #RRGGBB.", nameof( colorHex ) );
		}

		static void RequireVector3( float[] values )
		{
			if ( !IsVector3( values ) )
				throw new ArgumentException( "bad-transform: basis and origin each need exactly three finite numbers.", nameof( values ) );
		}

		/// <summary>#RRGGBB, nothing else. No silent repair: "#f00", "red" and "#FF000080" are all defects.</summary>
		public static bool IsColorHex( string value )
		{
			if ( string.IsNullOrEmpty( value ) || value.Length != 7 || value[0] != '#' ) return false;
			for ( var i = 1; i < 7; i++ )
			{
				var c = value[i];
				if ( !( c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F' ) ) return false;
			}
			return true;
		}

		static float[] Clone3( float[] values )
			=> IsVector3( values ) ? new[] { values[0], values[1], values[2] } : new[] { 0f, 0f, 0f };

		static bool IsVector3( float[] values )
			=> values is not null && values.Length == 3 && values.All( float.IsFinite );

		static string Field( string value )
		{
			if ( value.Contains( '\t' ) || value.Contains( '\n' ) || value.Contains( '\r' ) )
				throw new FormatException( "Text scene fields cannot contain tabs or line breaks." );
			return value;
		}

		static string Vector( float[] values )
		{
			if ( !IsVector3( values ) )
				throw new FormatException( "Text scene vectors need exactly three numbers." );
			return string.Join( " ", values.Select( v => v.ToString( "R", CultureInfo.InvariantCulture ) ) );
		}

		static float[] ParseVector( string text, int lineNo )
		{
			var parts = text.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
			if ( parts.Length != 3 )
				throw new FormatException( $"Line {lineNo}: a vector needs exactly three numbers." );
			var values = new float[3];
			for ( var i = 0; i < 3; i++ )
				if ( !float.TryParse( parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i] ) )
					throw new FormatException( $"Line {lineNo}: '{parts[i]}' is not a number." );
			return values;
		}
	}
}
