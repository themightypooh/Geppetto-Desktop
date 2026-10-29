#if TOOLS
#nullable enable
using System;
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
			await Click( mid );
			Note( $"plane: {( draw.Plane == null ? "none" : $"normal {draw.Plane.Normal}" )}, picked {( draw.PlaneIndex >= 0 ? "origin plane " + draw.PlaneIndex : draw.Face != null ? "face" : draw.PlaneFeatureId )}", fail: draw.Plane == null );
			Note( $"planes hidden once one is picked: wanted={GeppettoDatumPlanes.Wanted( GeppettoTools.Active )}", fail: GeppettoDatumPlanes.Wanted( GeppettoTools.Active ) );
			foreach ( var q in new[] { new Vector2( -1, -1 ), new Vector2( 1, -1 ), new Vector2( 1, 1 ), new Vector2( -1, 1 ) } )
				await Click( mid + q * r );
			await Wait( 0.2 );
			Shot( "blank-2-square" );
			GeppettoTools.Active?.Input( cam, KeyPress( Key.Enter ) );
			await Wait( 0.5 );
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
}
#endif
