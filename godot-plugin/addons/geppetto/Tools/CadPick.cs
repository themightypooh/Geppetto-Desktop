#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

/// <summary>
/// The CAD tools that start from a pick in the viewport (Detail stage, Extrude, Plane, Sketch,
/// Face Material). Like s&amp;box: pick the geometry, the feature is built and given the selection
/// (<see cref="Feature.ApplyGeometrySelection"/>), then its settings box opens with a live preview.
/// </summary>
public static partial class GeppettoTools
{
	/// <summary>Tool → what its picker takes, whether it may confirm with nothing picked, and the prompt.</summary>
	static readonly Dictionary<string, (PickKinds Kinds, bool AllowEmpty, string Prompt)> CadPickTools = new()
	{
		["Fillet"] = ( PickKinds.Faces | PickKinds.Edges, false, "click the edges to round (a face takes its whole rim)" ),
		["Chamfer"] = ( PickKinds.Faces | PickKinds.Edges, false, "click the edges to bevel (a face takes its whole rim)" ),
		["Shell"] = ( PickKinds.Faces, false, "click the faces to open; the rest becomes a wall" ),
		["Move Face"] = ( PickKinds.Faces, false, "click the faces to move" ),
		["Delete Face"] = ( PickKinds.Faces, false, "click the faces to delete" ),
		["Draft"] = ( PickKinds.Faces, false, "click the faces to taper" ),
		["Hole"] = ( PickKinds.Faces, false, "click the face to drill into" ),
		["Extrude"] = ( PickKinds.Faces | PickKinds.Regions, false, "click a sketch region or a face to pull" ),
		["Plane"] = ( PickKinds.Faces, true, "click a face to put the plane on (Enter with nothing picked: the Top plane)" ),
	};

	static partial void RegisterCadPick()
	{
		foreach ( var (tool, spec) in CadPickTools )
		{
			var name = tool;
			Register( "CAD", name, ctx =>
			{
				if ( ctx.Doc is not { } doc || ctx.Character is not { } character ) return ctx.Need();
				var picker = new GeppettoPicker( name, doc, spec.Kinds, spec.Prompt );
				picker.Confirmed = p => ConfirmCadPick( p, name, spec.AllowEmpty );
				picker.Start( character );
				return true;
			} );
		}

		Register( "CAD", "Face Material", ctx =>
		{
			if ( ctx.Doc is not { } doc || ctx.Character is not { } character ) return ctx.Need();
			StartFaceMaterial( doc, character );
			return true;
		} );

		Register( "CAD", "Sketch", ctx =>
		{
			if ( ctx.Doc is not { } doc || ctx.Character is not { } character ) return ctx.Need();
			new CadSketch( doc ).Start( character );
			return true;
		} );

		// CAD > Sketch > Draw / Shapes / Modify tiles: the sketch tools (the same as their hotkeys).
		foreach ( var (tile, tool) in SketchTiles )
		{
			var t = tool;
			Register( "CAD", tile, ctx => RunSketchTool( ctx, t ) );
		}
		Register( "CAD", "Construction", ctx =>
		{
			if ( Active is CadSketch { Editing: not null } s ) { s.ToggleConstruction(); return true; }
			return RunSketchTool( ctx, CadSketch.EditTool.Line, construction: true );
		} );
		Register( "CAD", "Project", ctx =>
		{
			if ( Active is CadSketch { Editing: not null } s )
			{
				s.SetTool( CadSketch.EditTool.Use );
				GeppettoToolContext.Say( "Project: click a green face edge to copy it in; Shift+U takes them all." );
				return true;
			}
			return RunSketchTool( ctx, CadSketch.EditTool.Use );
		} );
		Register( "CAD", "Finish sketch", ctx =>
		{
			if ( Active is CadSketch { Editing: not null } s ) { s.Finish(); return true; }
			if ( Active is CadSketch q && q.Points.Count >= 3 ) return q.Commit();
			GeppettoToolContext.Say( "Finish sketch: no sketch is open." );
			return true;
		} );
	}

	/// <summary>Dial tile → sketch tool.</summary>
	public static readonly (string Tile, CadSketch.EditTool Tool)[] SketchTiles =
	{
		( "Select", CadSketch.EditTool.Select ), ( "Line", CadSketch.EditTool.Line ), ( "Midpoint line", CadSketch.EditTool.LineMidpoint ),
		( "Point", CadSketch.EditTool.Point ), ( "Sketch spline", CadSketch.EditTool.Spline ), ( "Arc", CadSketch.EditTool.Arc ),
		( "3 point arc", CadSketch.EditTool.ArcThreePoint ), ( "Rectangle", CadSketch.EditTool.Rectangle ),
		( "Centre rectangle", CadSketch.EditTool.RectangleCentre ), ( "Circle", CadSketch.EditTool.Circle ),
		( "3 point circle", CadSketch.EditTool.CircleThreePoint ), ( "Polygon", CadSketch.EditTool.Polygon ),
		( "Outer polygon", CadSketch.EditTool.PolygonCircumscribed ), ( "Slot", CadSketch.EditTool.Slot ), ( "Ellipse", CadSketch.EditTool.Ellipse ),
		( "Trim", CadSketch.EditTool.Trim ), ( "Extend", CadSketch.EditTool.Extend ), ( "Sketch fillet", CadSketch.EditTool.Fillet ),
		( "Offset", CadSketch.EditTool.Offset ), ( "Sketch mirror", CadSketch.EditTool.Mirror ), ( "Cut", CadSketch.EditTool.Cut ),
	};

	/// <summary>A sketch tool picked on the dial: switch the open sketch to it, or start a sketch with it armed
	/// (the plane pick then opens the full sketch session with that tool).</summary>
	static bool RunSketchTool( GeppettoToolContext ctx, CadSketch.EditTool tool, bool construction = false )
	{
		if ( Active is CadSketch s )
		{
			if ( s.Editing != null ) s.SetTool( tool );
			else if ( s.Plane != null ) s.BeginNew( tool );
			else { s.Armed = tool; GeppettoToolContext.Say( $"Sketch ({tool}): click the Top, Front or Right plane, a Plane, or a face." ); }
			if ( construction ) s.ConstructionMode = true;
			Redraw();
			return true;
		}
		if ( ctx.Doc is not { } doc || ctx.Character is not { } character ) return ctx.Need();
		var sketch = new CadSketch( doc ) { Armed = tool, ConstructionMode = construction };
		sketch.Start( character );
		GeppettoToolContext.Say( $"Sketch ({tool}): click the Top, Front or Right plane, a Plane, or a face." );
		return true;
	}

	static bool ConfirmCadPick( GeppettoPicker p, string tool, bool allowEmpty )
	{
		if ( p.IsEmpty && !allowEmpty )
		{
			GeppettoToolContext.Say( $"{tool}: pick {( p.Kinds.HasFlag( PickKinds.Edges ) ? "an edge or a face" : p.Kinds.HasFlag( PickKinds.Regions ) ? "a sketch region or a face" : "a face" )} first, then press Enter." );
			return false;
		}
		var feature = BuildCadFeature( tool, p.Doc.Studio, p.Faces.ToList(), p.Edges.ToList(), p.RegionSketchId, p.RegionSeeds.ToList() );
		if ( feature == null ) { GeppettoToolContext.Say( $"{tool}: not a picking tool." ); return true; }
		// Stop picking before the settings box takes over the viewport.
		Activate( null );
		GeppettoFeatureDialog.ShowNew( p.Doc, feature );
		return true;
	}

	/// <summary>s&amp;box's NewFeature for the picking tools, then the selection applied to it.</summary>
	public static Feature? BuildCadFeature( string tool, PartStudio studio, IReadOnlyList<FaceRef> faces, IReadOnlyList<EdgeRef> edges,
		string? sketchId = null, IReadOnlyList<Vec2>? seeds = null )
	{
		Feature? f = tool switch
		{
			"Fillet" => new FilletFeature(),
			"Chamfer" => new ChamferFeature(),
			"Shell" => new ShellFeature(),
			"Move Face" => new MoveFaceFeature(),
			"Delete Face" => new DeleteFaceFeature(),
			"Draft" => new DraftFeature(),
			"Hole" => new HoleFeature(),
			"Plane" => new PlaneFeature(),
			"Face Material" => new FaceMaterialFeature(),
			// s&box's AwaitingPick: an Extrude does nothing until it is given a region or a face.
			"Extrude" => new ExtrudeFeature { SketchFeatureId = SketchConsumingFeature.AwaitingPick },
			_ => null,
		};
		if ( f == null ) return null;
		var bodyIds = faces.Select( x => x.BodyId ).Concat( edges.Select( e => e.BodyId ) ).Distinct().ToList();
		f.ApplyGeometrySelection( faces, bodyIds, studio.Bodies, edges, sketchId, seeds );
		return f;
	}

	// =========================================================================================
	// Face Material: click faces, pick a material on the ring (or Enter for the current one)
	// =========================================================================================

	static GeppettoPicker? _faceMaterial;

	/// <summary>Faces picked with a running Face Material tool (0 when it isn't running).</summary>
	public static int FaceMaterialPickCount => _faceMaterial is { } p && Active == p ? p.Faces.Count : 0;

	static void StartFaceMaterial( GeppettoDocument doc, GeppettoCharacter character )
	{
		var picker = new GeppettoPicker( "Face Material", doc, PickKinds.Faces,
			"click faces, then pick a material on the ring (Enter uses the current one)" );
		picker.Confirmed = p =>
		{
			if ( GeppettoDialState.Current is not { } m ) { GeppettoToolContext.Say( "Face Material: pick a material on the ring first." ); return false; }
			ApplyFaceMaterialPick( p, m );
			return false; // keep painting; Esc stops
		};
		picker.Stopped = () => { if ( _faceMaterial == picker ) _faceMaterial = null; };
		_faceMaterial = picker;
		picker.Start( character );
	}

	/// <summary>The dial's material ring was clicked while Face Material is the current tool.</summary>
	public static void FaceMaterialPicked( GeppettoDialMaterial m )
	{
		if ( _faceMaterial is not { } p || Active != p )
		{
			GeppettoToolContext.Say( $"Face Material: {m.Name} is now the current material. Run Face Material and click faces to paint it." );
			return;
		}
		ApplyFaceMaterialPick( p, m );
	}

	static void ApplyFaceMaterialPick( GeppettoPicker p, GeppettoDialMaterial m )
	{
		if ( p.Faces.Count == 0 ) { GeppettoToolContext.Say( "Face Material: click the faces to paint first." ); return; }
		int slot = ApplyFaceMaterial( p.Doc, p.Faces.ToList(), m.Name, m.Look );
		if ( slot < 0 ) return;
		GeppettoToolContext.Say( $"Face Material: {m.Name} on {p.Faces.Count} face{( p.Faces.Count == 1 ? "" : "s" )} (slot {slot})" );
		p.Clear();
		Redraw();
	}

	/// <summary>
	/// Puts the picked faces (their whole coplanar surfaces) on a material slot showing
	/// <paramref name="look"/>, as ONE undo step. The bookkeeping is the kernel's
	/// <see cref="FaceMaterialEdit.Assign"/> (s&amp;box's right-click "material for this face"): a face
	/// leaves any assignment holding it, one assignment feature per slot. Returns the slot, or -1.
	/// </summary>
	public static int ApplyFaceMaterial( GeppettoDocument doc, IReadOnlyList<FaceRef> faces, string name, SlotLook? look )
	{
		var studio = doc.Studio;
		var resolved = new List<(string BodyId, int Face, FaceRef Ref)>();
		foreach ( var f in faces )
			if ( FacePlane.TryResolveFace( studio.Bodies, f, out var body, out int index ) )
				resolved.Add( ( body.Id, index, f ) );
		if ( resolved.Count == 0 ) { GeppettoToolContext.Say( "Face Material: the picked faces are gone (the part changed); pick them again." ); return -1; }

		int slot = SlotForLook( studio, name, look );
		if ( slot < 0 ) { GeppettoToolContext.Say( "Face Material: all 63 material slots are in use." ); return -1; }

		var before = StudioMaterialState.Take( studio, slot );
		StudioMaterialState? after = null;
		var cmd = new DelegateEditCommand( $"Face material {name}",
			execute: () =>
			{
				if ( after != null ) { after.Restore( studio ); return; }
				if ( look != null ) studio.MaterialLooks[slot] = look.Clone();
				foreach ( var r in resolved ) FaceMaterialEdit.Assign( studio, r.BodyId, r.Face, r.Ref, slot );
				after = StudioMaterialState.Take( studio, slot );
			},
			undo: () => before.Restore( studio ) );
		return doc.Execute( cmd, $"Geppetto: face material {name}" ) ? slot : -1;
	}

	/// <summary>The slot already showing this look (same preset, or same colour/roughness/metal), else
	/// the lowest slot from 1 that nothing uses, names or shades.</summary>
	internal static int SlotForLook( PartStudio studio, string name, SlotLook? look )
	{
		if ( look != null )
			foreach ( var (slot, l) in studio.MaterialLooks.OrderBy( kv => kv.Key ) )
			{
				if ( slot == 0 || l == null ) continue;
				bool same = !string.IsNullOrEmpty( look.Preset )
					? string.Equals( l.Preset, look.Preset, StringComparison.OrdinalIgnoreCase )
					: l.Color.x == look.Color.x && l.Color.y == look.Color.y && l.Color.z == look.Color.z && l.Rough == look.Rough && l.Metal == look.Metal;
				if ( same ) return slot;
			}
		var taken = new HashSet<int>( FaceMaterialEdit.UsedSlots( studio ) );
		taken.UnionWith( studio.MaterialLooks.Keys );
		foreach ( var b in studio.Bodies )
			if ( b?.Mesh != null )
				foreach ( var f in b.Mesh.Faces ) taken.Add( f.Material );
		for ( int s = 1; s <= MaterialDrop.HighestSlot; s++ )
			if ( !taken.Contains( s ) ) return s;
		return -1;
	}

	/// <summary>What a face-material edit can change: the feature order, every assignment's face
	/// list, the rollback bar and one slot's look.</summary>
	sealed class StudioMaterialState
	{
		List<Feature> _features = new();
		Dictionary<FaceMaterialFeature, List<FaceRef>> _faces = new();
		int _rollback;
		int _slot;
		SlotLook? _look;

		public static StudioMaterialState Take( PartStudio s, int slot ) => new()
		{
			_features = s.Features.ToList(),
			_faces = s.Features.OfType<FaceMaterialFeature>().ToDictionary( f => f, f => f.Faces.ToList() ),
			_rollback = s.RollbackIndex,
			_slot = slot,
			_look = s.MaterialLooks.TryGetValue( slot, out var l ) ? l : null,
		};

		public void Restore( PartStudio s )
		{
			s.Features.Clear();
			s.Features.AddRange( _features );
			foreach ( var (f, faces) in _faces ) { f.Faces.Clear(); f.Faces.AddRange( faces ); }
			s.RollbackIndex = _rollback;
			if ( _look != null ) s.MaterialLooks[_slot] = _look; else s.MaterialLooks.Remove( _slot );
			s.MarkAllDirty();
		}
	}
}
#endif
