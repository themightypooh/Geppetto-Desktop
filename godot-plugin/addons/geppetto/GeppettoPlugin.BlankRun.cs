#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Effigy;
using Godot;

namespace Geppetto;

// GEPPETTO_WINDOW_WS=Blank: what Poo hit first. An empty 3D scene, nothing selected, CAD > Sketch. The
// tool must make a model, show the Top/Front/Right planes, take a square on the plane under the cursor,
// and Extrude must turn that square into a body. Driven through the tools' Input like the viewport does.
public partial class GeppettoPlugin
{
	async Task BlankRun()
	{
		try
		{
			await Wait( 4 );
			string scene = System.Environment.GetEnvironmentVariable( "GEPPETTO_WINDOW_SCENE" ) ?? "res://blank.tscn";
			EditorInterface.Singleton.OpenSceneFromPath( scene );
			await Wait( 2 );
			EditorInterface.Singleton.GetSelection().Clear();
			_character = null;
			RefreshDial();
			Note( $"open: {scene}, character before = {( _character == null ? "none" : _character.EffigyPath )}", fail: _character != null );
			GeppettoDialState.OpenWorkspace( Array.FindIndex( GeppettoDialData.Workspaces, w => w.Name == "CAD" ) );
			RefreshDial();
			Shot( "blank-0-empty" );

			var vp = EditorInterface.Singleton.GetEditorViewport3D( 0 );
			var cam = vp.GetCamera3D();
			var mid = vp.GetVisibleRect().Size / 2;
			InputEvent Press( bool down, Vector2 at ) => new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = down, Position = at, ButtonMask = down ? MouseButtonMask.Left : 0 };
			InputEvent Move( Vector2 at ) => new InputEventMouseMotion { Position = at };
			InputEvent KeyPress( Key k ) => new InputEventKey { Keycode = k, Pressed = true };
			async Task Click( Vector2 at ) { var t = GeppettoTools.Active!; t.Input( cam, Move( at ) ); t.Input( cam, Press( true, at ) ); t.Input( cam, Press( false, at ) ); await Wait( 0.15 ); }

			bool ran = RunTool( "CAD", "Sketch" );
			for ( int i = 0; i < 40 && GeppettoTools.Active is not CadSketch; i++ ) await Wait( 0.25 );
			var doc = _character?.Doc;
			Note( $"sketch: ran={ran}, model={( _character?.EffigyPath ?? "none" )}, tool={GeppettoTools.Active?.Name}",
				fail: !ran || doc == null || GeppettoTools.Active is not CadSketch );
			if ( doc == null || GeppettoTools.Active is not CadSketch draw ) { Finish(); return; }
			// A calm start: the new model isn't selected (no move/rotate gizmo), the planes show because
			// Sketch is waiting for one, and the hovered one is named.
			int picked = EditorInterface.Singleton.GetSelection().GetSelectedNodes().Count;
			Note( $"calm: {picked} node(s) selected after the new model, planes wanted={GeppettoDatumPlanes.Wanted( GeppettoTools.Active )}",
				fail: picked != 0 || !GeppettoDatumPlanes.Wanted( GeppettoTools.Active ) );
			GeppettoTools.Active.Input( cam, Move( mid ) );
			await Wait( 0.3 );
			Note( $"hover: {GeppettoDatumPlanes.Hovered ?? "none"} under the middle", fail: GeppettoDatumPlanes.Hovered == null );
			Shot( "blank-1-planes" );
			CheckClipping( "planes" );

			float r = vp.GetVisibleRect().Size.Y * 0.1f;
			var bar = _modeBars.FirstOrDefault( b => IsInstanceValid( b ) && b.GetParent() == ViewportSurface( 0 ) );
			Note( $"mode bar before a plane: showing={bar?.Showing}", fail: bar == null || bar.Showing );
			await Click( mid );
			Note( $"plane: {( draw.Plane == null ? "none" : $"normal {draw.Plane.Normal}" )}, picked {( draw.PlaneIndex >= 0 ? "origin plane " + draw.PlaneIndex : draw.Face != null ? "face" : draw.PlaneFeatureId )}", fail: draw.Plane == null );
			Note( $"planes hidden once one is picked: wanted={GeppettoDatumPlanes.Wanted( GeppettoTools.Active )}", fail: GeppettoDatumPlanes.Wanted( GeppettoTools.Active ) );

			// Sketch mode: amber frame + SKETCH tag + the bar naming the plane, inside the viewport, model dimmed.
			await Wait( 0.3 );
			if ( !await CheckSketchBar( bar, draw, "sketch mode" ) ) { Finish(); return; }
			Shot( "blank-1b-sketch-mode" );
			CheckClipping( "sketch mode bar" );
			// Rectangle on the bar starts a full sketch with that tool armed.
			await RealClick( bar!.Buttons.GetValueOrDefault( "Rectangle" ), "Rectangle" );
			Note( $"bar Rectangle: editing={draw.Editing != null}, tool={draw.Tool}", fail: draw.Editing == null || draw.Tool != CadSketch.EditTool.Rectangle );
			int before = doc.Studio.Features.Count;
			await Click( mid - new Vector2( r, r ) );
			await Click( mid + new Vector2( r, r ) );
			Note( $"bar rectangle drawn: {draw.Editing?.Sketch.Curves.Count ?? 0} curves", fail: ( draw.Editing?.Sketch.Curves.Count ?? 0 ) != 4 );
			// Cancel throws it away: no feature, tool off, bar gone, dim off.
			await RealClick( bar.Buttons.GetValueOrDefault( "Cancel" ), "Cancel" );
			await Wait( 0.3 );
			Note( $"bar Cancel: tool={GeppettoTools.Active?.Name ?? "none"}, features {before} -> {doc.Studio.Features.Count}, bar showing={bar.Showing}, dimmed={ModelDimmed}",
				fail: GeppettoTools.Active != null || doc.Studio.Features.Count != before || bar.Showing || ModelDimmed );
			Shot( "blank-1c-cancelled" );

			// Again, and this time the quick outline is finished with the bar's Finish.
			RunTool( "CAD", "Sketch" );
			for ( int i = 0; i < 20 && GeppettoTools.Active is not CadSketch; i++ ) await Wait( 0.25 );
			if ( GeppettoTools.Active is not CadSketch again ) { Note( "sketch again: no tool", fail: true ); Finish(); return; }
			draw = again;
			GeppettoTools.Active.Input( cam, Move( mid ) );
			await Click( mid );
			await Wait( 0.3 );
			if ( !await CheckSketchBar( bar, draw, "sketch mode again" ) ) { Finish(); return; }
			foreach ( var q in new[] { new Vector2( -1, -1 ), new Vector2( 1, -1 ), new Vector2( 1, 1 ), new Vector2( -1, 1 ) } )
				await Click( mid + q * r );
			await Wait( 0.2 );
			Shot( "blank-2-square" );
			await RealClick( bar.Buttons.GetValueOrDefault( "Finish" ), "Finish" );
			await Wait( 0.5 );
			Note( $"bar Finish: tool={GeppettoTools.Active?.Name ?? "none"}, bar showing={bar.Showing}, dimmed={ModelDimmed}", fail: GeppettoTools.Active != null || bar.Showing || ModelDimmed );
			var sk = doc.Studio.Features.OfType<SketchFeature>().LastOrDefault();
			Note( $"square: {sk?.Sketch.Curves.Count ?? 0} curves, error={sk?.Error}", fail: sk == null || sk.Sketch.Curves.Count != 4 || sk.Error != null );
			Shot( "blank-3-sketch" );
			if ( sk == null ) { Finish(); return; }

			int bodies = doc.Studio.Bodies.Count;
			ran = RunTool( "CAD", "Extrude" );
			await Wait( 0.3 );
			if ( GeppettoTools.Active is not GeppettoPicker pick ) { Note( $"extrude: ran={ran}, no picker", fail: true ); Finish(); return; }
			await Click( mid );
			Note( $"extrude pick: region sketch={pick.RegionSketchId}, seeds={pick.RegionSeeds.Count()}, faces={pick.Faces.Count()}", fail: pick.RegionSketchId != sk.Id );
			GeppettoTools.Active?.Input( cam, KeyPress( Key.Enter ) );
			await Wait( 0.5 );
			var box = OpenWindows().OfType<GeppettoFeatureDialog>().LastOrDefault();
			if ( box != null )
			{
				Shot( "blank-4-extrude-box" );
				CheckClipping( "extrude box" );
				box.GetOkButton().EmitSignal( BaseButton.SignalName.Pressed );
				await Wait( 0.6 );
			}
			var ex = doc.Studio.Features.LastOrDefault();
			Note( $"extrude: box={box != null}, last={ex?.TypeName} error={ex?.Error}, bodies {bodies} -> {doc.Studio.Bodies.Count}, faces={doc.Studio.Bodies.Sum( b => b.Mesh?.FaceCount ?? 0 )}",
				fail: ex is not ExtrudeFeature || ex.Error != null || doc.Studio.Bodies.Sum( b => b.Mesh?.FaceCount ?? 0 ) < 6 );
			GeppettoTools.Activate( null );
			await Wait( 0.4 );
			Shot( "blank-5-solid" );
		}
		catch ( Exception e ) { Note( "blank: threw " + e, fail: true ); }
		Finish();
	}

	async Task<bool> CheckSketchBar( GeppettoModeBar? bar, CadSketch draw, string step )
	{
		if ( bar == null ) { Note( $"{step}: no mode bar on the viewport", fail: true ); return false; }
		bar.Sync();
		await Wait( 0.1 );
		var pill = bar.Pill.GetGlobalRect();
		var area = bar.GetGlobalRect();
		var tagRect = new Rect2( bar.GetGlobalTransformWithCanvas() * bar.TagRect().Position, bar.TagRect().Size );
		bool inside = area.Encloses( pill ) && !pill.Intersects( tagRect );
		bool amber = bar.FrameColour.IsEqualApprox( GeppettoModeBar.Amber );
		Note( $"{step}: bar showing={bar.Showing}, tag={bar.Tag}, amber={amber}, title=\"{bar.Title}\", pill {pill.Position.Round()}+{pill.Size.Round()} inside {area.Size.Round()}={inside}, buttons=[{string.Join( ",", bar.Buttons.Keys )}], dimmed={ModelDimmed}",
			fail: !bar.Showing || bar.Tag != "SKETCH" || !amber || !bar.Title.StartsWith( "Sketching on " ) || bar.Title.EndsWith( " a plane" ) || !inside
				|| !new[] { "Line", "Rectangle", "Circle", "Arc", "Trim", "Dimension", "Finish", "Cancel" }.All( bar.Buttons.ContainsKey ) || !ModelDimmed );
		return bar.Showing;
	}

	/// <summary>A real mouse click on a control, through the editor window's input like a person's.</summary>
	async Task RealClick( Control? c, string what )
	{
		if ( c == null || !c.IsVisibleInTree() ) { Note( $"click {what}: not on screen", fail: true ); return; }
		var w = c.GetViewport();
		var at = c.GetGlobalTransformWithCanvas() * ( c.Size / 2 );
		w.PushInput( new InputEventMouseMotion { Position = at, GlobalPosition = at }, true );
		await Wait( 0.05 );
		foreach ( bool down in new[] { true, false } )
			w.PushInput( new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = down, Position = at, GlobalPosition = at, ButtonMask = down ? MouseButtonMask.Left : 0 }, true );
		await Wait( 0.25 );
	}
}
#endif
