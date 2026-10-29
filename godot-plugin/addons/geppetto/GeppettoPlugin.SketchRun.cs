#if TOOLS
#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Effigy;
using Godot;

namespace Geppetto;

// GEPPETTO_WINDOW_WS=SketchEdit: the window run for editing a sketch that is already in the tree. Draws a
// square with the Sketch tool, re-opens it, chains a triangle with Line, box-selects it, drags it,
// deletes it, adds an open line, finishes, then undoes and redoes through the editor's own history.
// Every step is driven through the tool's Input the way the viewport sends it, and screenshotted.
public partial class GeppettoPlugin
{
	async Task SketchEditRun()
	{
		try
		{
			await Wait( 4 );
			string scene = System.Environment.GetEnvironmentVariable( "GEPPETTO_WINDOW_SCENE" ) ?? "res://e2e/room.tscn";
			EditorInterface.Singleton.OpenSceneFromPath( scene );
			await Wait( 2 );
			SelectCharacter();
			await Wait( 1 );
			if ( _character?.Doc is not { } doc ) { Note( "open: no Geppetto character in " + scene, fail: true ); Finish(); return; }
			GeppettoDialState.Ws = Array.FindIndex( GeppettoDialData.Workspaces, w => w.Name == "CAD" );
			RefreshDial();
			var vp = EditorInterface.Singleton.GetEditorViewport3D( 0 );
			var cam = vp.GetCamera3D();
			var mid = vp.GetVisibleRect().Size / 2;
			InputEvent Press( bool down, Vector2 at, MouseButton b = MouseButton.Left ) => new InputEventMouseButton { ButtonIndex = b, Pressed = down, Position = at, ButtonMask = down ? MouseButtonMask.Left : 0 };
			InputEvent Move( Vector2 at, bool held = false ) => new InputEventMouseMotion { Position = at, ButtonMask = held ? MouseButtonMask.Left : 0 };
			InputEvent KeyPress( Key k ) => new InputEventKey { Keycode = k, Pressed = true };
			IGeppettoViewportTool T() => GeppettoTools.Active!;
			async Task Click( Vector2 at ) { T().Input( cam, Move( at ) ); T().Input( cam, Press( true, at ) ); T().Input( cam, Press( false, at ) ); await Wait( 0.1 ); }
			async Task Step( string name ) { await Wait( 0.3 ); Shot( name ); CheckClipping( name ); }

			// --- a new square, the existing tool ---
			int f0 = doc.Studio.Features.Count;
			if ( !RunTool( "CAD", "Sketch" ) || GeppettoTools.Active is not CadSketch draw ) { Note( "Sketch: not wired", fail: true ); Finish(); return; }
			float r = vp.GetVisibleRect().Size.Y * 0.12f;
			await Click( mid );
			foreach ( var q in new[] { new Vector2( -1, -1 ), new Vector2( 1, -1 ), new Vector2( 1, 1 ), new Vector2( -1, 1 ) } )
				await Click( mid + q * r );
			await Step( "sketch-new-square" );
			T().Input( cam, KeyPress( Key.Enter ) );
			await Wait( 0.3 );
			var sk = doc.Studio.Features.LastOrDefault() as SketchFeature;
			Note( $"new square: +{doc.Studio.Features.Count - f0} feature, {sk?.Sketch.Curves.Count} curves, plane={( draw.Face != null ? "face" : "global" )}", fail: sk == null || sk.Sketch.Curves.Count != 4 );
			if ( sk == null ) { Finish(); return; }

			// --- re-open it (what a double-click in Features does) ---
			var root = EditorInterface.Singleton.GetEditedSceneRoot();
			var ur = GetUndoRedo();
			var history = ur.GetHistoryUndoRedo( ur.GetObjectHistoryId( root ) );
			ulong v0 = history.GetVersion();
			var ed = new CadSketch( doc );
			Note( $"edit: opened={ed.StartEdit( _character!, sk ) && GeppettoTools.Active == ed}", fail: GeppettoTools.Active != ed );
			ed.Input( cam, Move( mid ) );
			await Step( "sketch-edit-open" );

			// Line: a triangle to the right of the square, closed on its first point.
			ed.Input( cam, KeyPress( Key.L ) );
			var t0 = mid + new Vector2( 2.2f, -0.6f ) * r;
			await Click( t0 );
			await Click( mid + new Vector2( 3.6f, -0.6f ) * r );
			ed.Input( cam, Move( mid + new Vector2( 2.9f, 0.7f ) * r ) );
			await Step( "sketch-edit-line-chaining" );
			await Click( mid + new Vector2( 2.9f, 0.7f ) * r );
			await Click( t0 );
			int regions = ProfileFinder.Find( sk.Sketch ).Profiles.Count;
			Note( $"line: {sk.Sketch.Curves.Count} curves, {regions} closed regions, chain open={ed.ChainLast >= 0}", fail: sk.Sketch.Curves.Count != 7 || regions != 2 || ed.ChainLast >= 0 );
			await Step( "sketch-edit-line-closed" );

			// Select: box left to right around the triangle (shot mid-drag), then drag it down by a grabbed point.
			ed.Input( cam, KeyPress( Key.S ) );
			var b0 = mid + new Vector2( 1.9f, -1.0f ) * r; var b1 = mid + new Vector2( 4.0f, 1.1f ) * r;
			ed.Input( cam, Move( b0 ) ); ed.Input( cam, Press( true, b0 ) );
			for ( int i = 1; i <= 5; i++ ) ed.Input( cam, Move( b0 + ( b1 - b0 ) * i / 5f, held: true ) );
			await Step( "sketch-edit-box" );
			ed.Input( cam, Press( false, b1 ) );
			Note( $"box select: {ed.SelectedCurves.Count} curves, {ed.SelectedPoints.Count} points", fail: ed.SelectedCurves.Count != 3 || ed.SelectedPoints.Count != 3 );
			await Step( "sketch-edit-selected" );
			ed.Input( cam, Move( t0 ) ); ed.Input( cam, Press( true, t0 ) );
			for ( int i = 1; i <= 6; i++ ) ed.Input( cam, Move( t0 + new Vector2( 0, i * r * 0.15f ), held: true ) );
			ed.Input( cam, Press( false, t0 + new Vector2( 0, r * 0.9f ) ) );
			Note( $"drag: still {ProfileFinder.Find( sk.Sketch ).Profiles.Count} regions, rebuild errors {doc.LastReport?.Errors.Count}", fail: doc.LastReport?.Errors.Count > 0 );
			await Step( "sketch-edit-dragged" );

			// Delete it, then an open two-line chain off the square's corner, ended with Enter.
			ed.Input( cam, KeyPress( Key.Delete ) );
			Note( $"delete: {sk.Sketch.Curves.Count} curves left", fail: sk.Sketch.Curves.Count != 4 );
			ed.Input( cam, KeyPress( Key.L ) );
			await Click( mid + new Vector2( 1, 1 ) * r );
			await Click( mid + new Vector2( 2.5f, 1.6f ) * r );
			await Click( mid + new Vector2( 2.5f, -1 ) * r );
			ed.Input( cam, KeyPress( Key.Enter ) );
			Note( $"open chain: {sk.Sketch.Curves.Count} curves, chain open={ed.ChainLast >= 0}", fail: sk.Sketch.Curves.Count != 6 || ed.ChainLast >= 0 );
			ed.Input( cam, KeyPress( Key.S ) );
			ed.Input( cam, Move( mid + new Vector2( 3, 0 ) * r ) );
			await Step( "sketch-edit-open-lines" );

			// Finish: one step on the scene's history; undo / redo through it.
			ed.Input( cam, KeyPress( Key.Enter ) );
			await Wait( 0.3 );
			ulong v1 = history.GetVersion();
			Note( $"finish: tool closed={GeppettoTools.Active == null}, history steps {v1 - v0}", fail: GeppettoTools.Active != null || v1 - v0 != 1 );
			await Step( "sketch-edit-finished" );
			history.Undo();
			await Wait( 0.3 );
			var back = doc.FeatureById( sk.Id ) as SketchFeature;
			Note( $"undo: {back?.Sketch.Curves.Count} curves (square = 4)", fail: back?.Sketch.Curves.Count != 4 );
			await Step( "sketch-edit-undone" );
			history.Redo();
			await Wait( 0.3 );
			back = doc.FeatureById( sk.Id ) as SketchFeature;
			Note( $"redo: {back?.Sketch.Curves.Count} curves", fail: back?.Sketch.Curves.Count != 6 );
		}
		catch ( Exception e ) { Note( "sketch edit run threw: " + e, fail: true ); }
		Finish();
	}
}
#endif
