#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Effigy;
using Godot;

namespace Geppetto;

// GEPPETTO_WINDOW_WS=CadAudit: every CAD tool outside the Sketch stage, driven the way Poo drives it
// (dial tool, viewport clicks through the tool's Input, the settings box's own buttons and fields, the
// Features dock's rows and menu). Each tool must build without an error, Cancel must leave nothing
// behind, and Ctrl+Z / Ctrl+Shift+Z (the editor history) must take it out and put it back.
public partial class GeppettoPlugin
{
	async Task CadAuditRun()
	{
		var said = new List<string>();
		void Heard( string m, bool warn ) => said.Add( ( warn ? "warn: " : "" ) + m );
		GeppettoToolContext.Spoken += Heard;
		try { await CadAudit( said ); }
		catch ( Exception e ) { Note( "cad audit: threw " + e, fail: true ); }
		GeppettoToolContext.Spoken -= Heard;
		Finish();
	}

	async Task CadAudit( List<string> said )
	{
		await Wait( 4 );
		string scene = System.Environment.GetEnvironmentVariable( "GEPPETTO_WINDOW_SCENE" ) ?? "res://blank.tscn";
		EditorInterface.Singleton.OpenSceneFromPath( scene );
		await Wait( 2 );
		EditorInterface.Singleton.GetSelection().Clear();
		_character = null;
		GeppettoDialState.OpenWorkspace( Array.FindIndex( GeppettoDialData.Workspaces, w => w.Name == "CAD" ) );
		RefreshDial();

		var vp = EditorInterface.Singleton.GetEditorViewport3D( 0 );
		var cam = vp.GetCamera3D();
		var mid = vp.GetVisibleRect().Size / 2;
		InputEvent Press( bool down, Vector2 at ) => new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = down, Position = at, ButtonMask = down ? MouseButtonMask.Left : 0 };
		InputEvent Move( Vector2 at ) => new InputEventMouseMotion { Position = at };
		InputEvent KeyPress( Key k ) => new InputEventKey { Keycode = k, Pressed = true };
		async Task Click( Vector2 at, bool shift = false )
		{
			var t = GeppettoTools.Active!;
			t.Input( cam, Move( at ) );
			var down = (InputEventMouseButton)Press( true, at ); down.ShiftPressed = shift;
			t.Input( cam, down ); t.Input( cam, Press( false, at ) );
			await Wait( 0.15 );
		}
		async Task Key( Key k ) { GeppettoTools.Active?.Input( cam, KeyPress( k ) ); await Wait( 0.4 ); }
		string LastSaid() => said.Count == 0 ? "nothing" : said[^1];

		GeppettoDocument Doc() => _character!.Doc!;
		Vector2 Scr( Vec3 e ) => cam.UnprojectPosition( GeppettoPicker.BodyTransformOf( _character!.Root ) * EffigyMeshBridge.TransformPosition( e.x, e.y, e.z ) );
		string State()
		{
			var s = Doc().Studio;
			return $"{s.Features.Count}f/{s.Bodies.Count}b/{s.Bodies.Sum( b => b.Mesh?.FaceCount ?? 0 )}faces/" +
				string.Join( "", s.Features.Select( f => ( f.Suppressed ? "s" : "" ) + ( f.Visible ? "" : "h" ) + ( f.Name?.Length ?? 0 ) ) );
		}
		string Errors() => string.Join( "; ", Doc().Studio.Features.Where( f => f.Error != null ).Select( f => $"{f.Name}: {f.Error}" ) );

		EditorUndoRedoManager ur = GetUndoRedo();
		UndoRedo History() => ur.GetHistoryUndoRedo( ur.GetObjectHistoryId( EditorInterface.Singleton.GetEditedSceneRoot() ) );

		GeppettoFeatureDialog? Box() => OpenWindows().OfType<GeppettoFeatureDialog>().LastOrDefault();

		// The settings box: shot, no clipped or blank rows, then Add. Returns the feature once it is in the tree.
		async Task<Feature?> Accept( string step, Func<GeppettoFeatureDialog, Task>? fill = null )
		{
			await Wait( 0.4 );
			var box = Box();
			if ( box == null ) { Note( $"{step}: no settings box (said: {LastSaid()})", fail: true ); return null; }
			CheckRows( box, step );
			if ( fill != null ) await fill( box );
			await Wait( 0.3 );
			Shot( step + "-box" );
			CheckClipping( step + " box" );
			var f = box.Feature;
			box.GetOkButton().EmitSignal( BaseButton.SignalName.Pressed );
			await Wait( 0.6 );
			bool inTree = Doc().Studio.Features.Contains( f );
			Note( $"{step}: added={inTree} error={f.Error ?? "none"} state {State()}", fail: !inTree || f.Error != null );
			return inTree ? f : null;
		}

		// Ctrl+Z takes the step out, Ctrl+Shift+Z puts it back.
		async Task UndoRedo( string step )
		{
			string before = State();
			History().Undo(); await Wait( 0.4 );
			string undone = State();
			History().Redo(); await Wait( 0.4 );
			string redone = State();
			Note( $"{step}: undo {before} -> {undone}, redo -> {redone}", fail: undone == before || redone != before );
		}

		// Takes a step back out so the next tool starts from the same model.
		async Task Drop( string step, string to )
		{
			for ( int i = 0; i < 6 && State() != to; i++ ) { History().Undo(); await Wait( 0.3 ); }
			Note( $"{step}: back to {State()}", fail: State() != to );
		}

		PopupMenu? Menu() => EditorInterface.Singleton.GetBaseControl().GetChildren().OfType<PopupMenu>().LastOrDefault( m => m.Visible );
		async Task<bool> Choose( string step, string option )
		{
			await Wait( 0.3 );
			var menu = Menu();
			if ( menu == null ) { Note( $"{step}: no choice menu", fail: true ); return false; }
			int idx = Enumerable.Range( 0, menu.ItemCount ).FirstOrDefault( i => menu.GetItemText( i ) == option, -1 );
			if ( idx < 0 ) { Note( $"{step}: menu has no {option}", fail: true ); return false; }
			Shot( step + "-menu" );
			menu.EmitSignal( PopupMenu.SignalName.IdPressed, (long)menu.GetItemId( idx ) );
			menu.Hide();
			return true;
		}

		// ---------------------------------------------------------------------------------
		// 1. Sketch → Revolve, Spline → Sweep, Bend Along Curve, Plane → second sketch → Loft
		// ---------------------------------------------------------------------------------
		bool ran = RunTool( "CAD", "Sketch" );
		for ( int i = 0; i < 40 && GeppettoTools.Active is not CadSketch; i++ ) await Wait( 0.25 );
		if ( _character?.Doc == null || GeppettoTools.Active is not CadSketch draw ) { Note( $"sketch: ran={ran}, no model or tool", fail: true ); return; }
		await Click( mid );
		// Phase 1 is laid out in units for a framed model; a new model isn't framed, so scale it all by k until
		// 4 units is ~60px and no click lands in another's point snap (12px).
		float k = 1;
		while ( Scr( new Vec3( 4 * k, 0, 0 ) ).DistanceTo( Scr( new Vec3( 0, 0, 0 ) ) ) < 60 && k < 64 ) k *= 2;
		Vec3 K( float x, float y, float z ) => new( x * k, y * k, z * k );
		foreach ( var c in new[] { K( 4, 4, 0 ), K( 8, 4, 0 ), K( 8, 8, 0 ), K( 4, 8, 0 ) } ) await Click( Scr( c ) );
		await Key( Godot.Key.Enter );
		var sk1 = Doc().Studio.Features.OfType<SketchFeature>().LastOrDefault();
		Note( $"sketch 1: {sk1?.Sketch.Curves.Count ?? 0} curves on {( draw.PlaneIndex >= 0 ? "plane " + draw.PlaneIndex : "?" )}, points " +
			string.Join( " ", sk1?.Sketch.Points.Select( p => $"({p.x:0.#},{p.y:0.#})" ) ?? Enumerable.Empty<string>() ), fail: sk1 == null || sk1.Sketch.Curves.Count != 4 );
		if ( sk1 == null ) return;

		// Revolve: Cancel first, then for real.
		string s0 = State();
		RunTool( "CAD", "Revolve" );
		await Wait( 0.4 );
		var rbox = Box();
		Note( $"revolve cancel: box={rbox != null}", fail: rbox == null );
		rbox?.GetCancelButton().EmitSignal( BaseButton.SignalName.Pressed );
		await Wait( 0.5 );
		Note( $"revolve cancel: {s0} -> {State()}", fail: State() != s0 );
		RunTool( "CAD", "Revolve" );
		var revolve = await Accept( "revolve" );
		if ( revolve != null ) await UndoRedo( "revolve" );

		// Spline: drawn in the viewport while its box is open.
		RunTool( "CAD", "Spline" );
		await Wait( 0.4 );
		Note( $"spline: draw tool={GeppettoTools.Active?.Name}, box={Box() != null}", fail: GeppettoTools.Active is not GeppettoCadSplineDraw || Box() == null );
		SplineFeature? spline = null;
		if ( GeppettoTools.Active is GeppettoCadSplineDraw )
		{
			foreach ( var c in new[] { K( -30, -24, 0 ), K( -32, -8, 0 ), K( -32, 8, 0 ), K( -30, 24, 0 ) } ) await Click( Scr( c ) );
			await Wait( 0.3 );
			Shot( "spline-drawn" );
			var sbox = Box();
			spline = sbox?.Feature as SplineFeature;
			Note( $"spline: {spline?.Points.Count ?? 0} points at {string.Join( " ", spline?.Points.Select( q => $"({q.x:F0},{q.y:F0},{q.z:F0})" ) ?? Array.Empty<string>() )}", fail: ( spline?.Points.Count ?? 0 ) < 4 );
			await Key( Godot.Key.Backspace );
			Note( $"spline backspace: {spline?.Points.Count ?? 0} points", fail: ( spline?.Points.Count ?? 0 ) != 3 );
			await Click( Scr( K( -30, 24, 0 ) ) );
			await Key( Godot.Key.Enter );
			bool inTree = spline != null && Doc().Studio.Features.Contains( spline );
			Note( $"spline enter: added={inTree} error={spline?.Error ?? "none"} box open={Box() != null} tool={GeppettoTools.Active?.Name ?? "none"}",
				fail: !inTree || spline!.Error != null || Box() != null );
			if ( inTree ) await UndoRedo( "spline" );
		}

		GeppettoCad.SelectedFeatureId = null;
		RunTool( "CAD", "Sweep" );
		var sweep = await Accept( "sweep" );
		if ( sweep != null ) await UndoRedo( "sweep" );

		RunTool( "CAD", "Bend Along Curve" );
		var bend = await Accept( "bend along curve" );
		if ( bend != null ) await UndoRedo( "bend along curve" );

		RunTool( "CAD", "Plane" );
		await Wait( 0.3 );
		await Key( Godot.Key.Enter );
		var plane = await Accept( "plane", box => TypeInto( box, "Offset", $"{10 * k}", "plane" ) );
		Note( $"plane offset typed: {( plane as PlaneFeature )?.Offset.Value}", fail: ( plane as PlaneFeature )?.Offset.Value != 10 * k );

		RunTool( "CAD", "Sketch" );
		await Wait( 0.3 );
		if ( GeppettoTools.Active is CadSketch draw2 )
		{
			var ce = EffigyPoseBridge.ToEffigyPosition( GeppettoPicker.BodyTransformOf( _character!.Root ).AffineInverse() * cam.GlobalPosition );
			float qx = ce.x >= 0 ? 6 : -6, qy = ce.y >= 0 ? 6 : -6;
			await Click( Scr( K( qx, qy, 10 ) ) );
			Note( $"sketch 2: on {( draw2.PlaneFeatureId.Length > 0 ? "datum " + draw2.PlaneFeatureId : draw2.Face != null ? "a face" : "plane " + draw2.PlaneIndex )}", fail: plane == null || draw2.PlaneFeatureId != plane.Id );
			foreach ( var c in new[] { K( qx - 3, qy - 3, 10 ), K( qx + 3, qy - 3, 10 ), K( qx + 3, qy + 3, 10 ), K( qx - 3, qy + 3, 10 ) } ) await Click( Scr( c ) );
			await Key( Godot.Key.Enter );
		}
		var sk2 = Doc().Studio.Features.OfType<SketchFeature>().LastOrDefault();
		Note( $"sketch 2: {sk2?.Sketch.Curves.Count ?? 0} curves, error={sk2?.Error ?? "none"}", fail: sk2 == null || sk2 == sk1 || sk2.Error != null );

		RunTool( "CAD", "Loft" );
		var loft = await Accept( "loft" );
		if ( loft != null ) await UndoRedo( "loft" );
		GeppettoTools.Activate( null );
		await Wait( 0.3 );
		Shot( "phase1-all" );
		Note( $"phase 1 errors: {( Errors() is { Length: > 0 } e1 ? e1 : "none" )}", fail: Errors().Length > 0 );

		// ---------------------------------------------------------------------------------
		// 2. The Features dock on those rows
		// ---------------------------------------------------------------------------------
		await DockAudit( revolve, sk1, State, UndoRedo, Box, Accept );

		// Everything above back out: the box tests start from an empty model.
		for ( int i = 0; i < 60 && Doc().Studio.Features.Count > 0; i++ ) { if ( !History().Undo() ) break; await Wait( 0.15 ); }
		Note( $"undo all: {State()}", fail: Doc().Studio.Features.Count != 0 );

		// ---------------------------------------------------------------------------------
		// 3. Primitive box; Extrude from its face; every Detail tool on it
		// ---------------------------------------------------------------------------------
		s0 = State();
		RunTool( "CAD", "Primitive" );
		if ( await Choose( "primitive", "Box" ) )
		{
			await Wait( 0.4 );
			Box()?.GetCancelButton().EmitSignal( BaseButton.SignalName.Pressed );
			await Wait( 0.4 );
			Note( $"primitive cancel: {s0} -> {State()}", fail: State() != s0 );
		}
		RunTool( "CAD", "Primitive" );
		if ( !await Choose( "primitive", "Box" ) ) return;
		var boxFeature = await Accept( "primitive box" );
		if ( boxFeature == null ) return;
		await UndoRedo( "primitive box" );
		string baseState = State();

		(Body B, Face F) Top()
		{
			var b = Doc().Studio.Bodies[0];
			var f = b.Mesh.Faces.Where( f => b.Mesh.FaceNormal( f ).z > 0.9f ).OrderByDescending( f => b.Mesh.FaceCentroid( f ).z ).First();
			return ( b, f );
		}
		Vec3 TopCenter() { var (b, f) = Top(); return b.Mesh.FaceCentroid( f ); }
		Vec3 TopEdge()
		{
			var (b, f) = Top();
			var a = b.Mesh.Positions[f.Indices[0]]; var c = b.Mesh.Positions[f.Indices[1]];
			var m = ( a + c ) * 0.5f;
			return m + ( b.Mesh.FaceCentroid( f ) - m ) * 0.02f;
		}
		Vec3 SideCenter()
		{
			var b = Doc().Studio.Bodies[0];
			var ce = EffigyPoseBridge.ToEffigyPosition( GeppettoPicker.BodyTransformOf( _character!.Root ).AffineInverse() * cam.GlobalPosition );
			var f = b.Mesh.Faces.Where( f => MathF.Abs( b.Mesh.FaceNormal( f ).z ) < 0.1f )
				.OrderByDescending( f => { var n = b.Mesh.FaceNormal( f ); var c = b.Mesh.FaceCentroid( f ); var d = ce - c; return ( n.x * d.x + n.y * d.y + n.z * d.z ) / MathF.Sqrt( d.x * d.x + d.y * d.y + d.z * d.z ); } ).First();
			return b.Mesh.FaceCentroid( f );
		}
		float TopZ() => Doc().Studio.Bodies.SelectMany( b => b.Mesh.Positions ).Max( p => p.z );

		// Extrude from the top face; the typed Distance must move the preview.
		RunTool( "CAD", "Extrude" );
		await Wait( 0.3 );
		if ( GeppettoTools.Active is GeppettoPicker ep )
		{
			await Click( Scr( TopCenter() ) );
			Note( $"extrude pick: {ep.Faces.Count} faces", fail: ep.Faces.Count == 0 );
			await Key( Godot.Key.Enter );
			float z0 = TopZ();
			var ex = await Accept( "extrude face", async box =>
			{
				float before = TopZ();
				await TypeInto( box, "Distance", "20", "extrude" );
				await Wait( 0.3 );
				Note( $"extrude typed distance 20: preview top {before:F1} -> {TopZ():F1} (base top {z0:F1})", fail: MathF.Abs( TopZ() - before ) < 1f );
			} );
			if ( ex != null ) { await UndoRedo( "extrude face" ); await Drop( "extrude face", baseState ); }
		}
		else Note( "extrude: no picker", fail: true );

		foreach ( var tool in new[] { "Fillet", "Chamfer", "Shell", "Move Face", "Delete Face", "Draft", "Hole" } )
		{
			bool edge = tool is "Fillet" or "Chamfer";
			RunTool( "CAD", tool );
			await Wait( 0.3 );
			if ( GeppettoTools.Active is not GeppettoPicker p ) { Note( $"{tool}: no picker (said: {LastSaid()})", fail: true ); continue; }
			// Enter with nothing picked must say why, where Poo looks.
			int n = said.Count;
			await Key( Godot.Key.Enter );
			Note( $"{tool} empty enter: {( said.Count > n ? said[^1] : "silent" )}", fail: said.Count == n || !said[^1].StartsWith( "warn:" ) );
			await Click( Scr( edge ? TopEdge() : tool == "Draft" ? SideCenter() : TopCenter() ) );
			Note( $"{tool} pick: {p.Edges.Count} edges, {p.Faces.Count} faces", fail: edge ? p.Edges.Count + p.Faces.Count == 0 : p.Faces.Count == 0 );
			Shot( tool + "-picked" );
			await Key( Godot.Key.Enter );
			var f = await Accept( tool.ToLowerInvariant() );
			if ( f != null ) { await UndoRedo( tool ); await Drop( tool, baseState ); }
			else { Box()?.GetCancelButton().EmitSignal( BaseButton.SignalName.Pressed ); await Wait( 0.3 ); await Drop( tool, baseState ); }
		}

		// ---------------------------------------------------------------------------------
		// 4. Repeat tools, Part, Profile, Import on the box
		// ---------------------------------------------------------------------------------
		foreach ( var tool in new[] { "Mirror", "Linear Pattern", "Circular Pattern", "Transform", "Simple Deform", "Lattice", "Part", "Profile" } )
		{
			RunTool( "CAD", tool );
			var f = await Accept( tool.ToLowerInvariant() );
			if ( f != null ) { await UndoRedo( tool ); await Drop( tool, baseState ); }
			else { Box()?.GetCancelButton().EmitSignal( BaseButton.SignalName.Pressed ); await Wait( 0.3 ); await Drop( tool, baseState ); }
		}

		// Outside the project: a new .obj under res:// gets auto-imported, and that import's progress
		// dialog parents itself to the file dialog below, which is freed under it (an editor crash).
		string obj = ProjectSettings.GlobalizePath( "user://audit_cube.obj" );
		File.WriteAllText( obj, "o cube\nv 0 0 0\nv 4 0 0\nv 4 4 0\nv 0 4 0\nv 0 0 4\nv 4 0 4\nv 4 4 4\nv 0 4 4\n" +
			"f 1 4 3 2\nf 5 6 7 8\nf 1 2 6 5\nf 2 3 7 6\nf 3 4 8 7\nf 4 1 5 8\n" );
		RunTool( "CAD", "Import" );
		await Wait( 0.5 );
		var fd = OpenWindows().OfType<EditorFileDialog>().LastOrDefault();
		Note( $"import: file dialog={fd != null}", fail: fd == null );
		if ( fd != null )
		{
			Shot( "import-file" );
			fd.EmitSignal( FileDialog.SignalName.FileSelected, obj );
			fd.Hide();
			var imp = await Accept( "import" );
			if ( imp != null ) { await UndoRedo( "import" ); await Drop( "import", baseState ); }
		}

		// ---------------------------------------------------------------------------------
		// 5. Boolean: a sphere into the box's corner, both picked in Parts, Subtract
		// ---------------------------------------------------------------------------------
		RunTool( "CAD", "Primitive" );
		if ( await Choose( "boolean sphere", "Sphere" ) )
		{
			var top = TopCenter();
			var sph = await Accept( "boolean sphere", async box =>
			{
				var sl = Sliders( box, "Position" );
				if ( sl.Count == 3 ) { await TypeInto( sl[0], box, "6", "sphere x" ); await TypeInto( sl[1], box, "6", "sphere y" ); await TypeInto( sl[2], box, top.z.ToString( System.Globalization.CultureInfo.InvariantCulture ), "sphere z" ); }
				else Note( $"sphere position row: {sl.Count} fields", fail: true );
			} );
			string twoState = State();
			var tree = _featureTree;
			int picked = 0;
			if ( tree != null && sph != null )
			{
				tree.MakeVisible();
				await Wait( 0.3 );
				var pr = tree.PartRows;
				for ( var it = pr.GetRoot()?.GetFirstChild(); it != null; it = it.GetNext() )
				{
					var at = pr.GetGlobalTransformWithCanvas() * pr.GetItemAreaRect( it ).GetCenter();
					foreach ( bool down in new[] { true, false } )
						pr.GetViewport().PushInput( new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = down, Position = at, GlobalPosition = at, CtrlPressed = picked > 0, ButtonMask = down ? MouseButtonMask.Left : 0 }, true );
					await Wait( 0.2 );
					picked++;
				}
				Shot( "boolean-parts-picked" );
			}
			Note( $"boolean parts picked: rows {picked}, SelectedBodyIds {GeppettoCad.SelectedBodyIds.Count}", fail: GeppettoCad.SelectedBodyIds.Count != 2 );
			RunTool( "CAD", "Boolean" );
			if ( await Choose( "boolean", "Subtract" ) )
			{
				var bo = await Accept( "boolean subtract" );
				Note( $"boolean: bodies {Doc().Studio.Bodies.Count}", fail: Doc().Studio.Bodies.Count != 1 );
				if ( bo != null ) { await UndoRedo( "boolean" ); await Drop( "boolean", twoState ); }
			}
			GeppettoCad.SelectedBodyIds.Clear();
			await Drop( "boolean sphere", baseState );
		}

		// ---------------------------------------------------------------------------------
		// 6. Face Material on the box's top
		// ---------------------------------------------------------------------------------
		RunTool( "CAD", "Face Material" );
		await Wait( 0.3 );
		if ( GeppettoTools.Active is GeppettoPicker fm )
		{
			int before = Top().F.Material;
			await Click( Scr( TopCenter() ) );
			Note( $"face material pick: {fm.Faces.Count} faces, current material {GeppettoDialState.Current?.Name ?? "none"}", fail: fm.Faces.Count == 0 || GeppettoDialState.Current == null );
			await Key( Godot.Key.Enter );
			int after = Top().F.Material;
			Note( $"face material: top slot {before} -> {after}, {LastSaid()}", fail: after == before );
			GeppettoTools.Activate( null );
			Shot( "face-material" );
			await UndoRedo( "face material" );
			await Drop( "face material", baseState );
		}
		else Note( "face material: no picker", fail: true );

		GeppettoTools.Activate( null );
		await Wait( 0.3 );
		Shot( "end" );
		CheckClipping( "end" );
	}

	/// <summary>The Features dock: double-click a sketch, Edit..., Suppress, Hide, Rename, Delete, undo/redo after each.</summary>
	async Task DockAudit( Feature? target, SketchFeature sketch, Func<string> state, Func<string, Task> undoRedo,
		Func<GeppettoFeatureDialog?> box, Func<string, Func<GeppettoFeatureDialog, Task>?, Task<Feature?>> accept )
	{
		var tree = _featureTree;
		if ( tree == null || target == null ) { Note( "dock: no Features dock or no feature to edit", fail: true ); return; }
		var rows = tree.FeatureRows;
		tree.MakeVisible();
		await Wait( 0.3 );
		TreeItem? Row( Feature f )
		{
			for ( var it = rows.GetRoot()?.GetFirstChild(); it != null; it = it.GetNext() )
				if ( it.GetMetadata( 0 ).AsString() == f.Id ) return it;
			return null;
		}
		async Task<bool> Pick( Feature f )
		{
			var r = Row( f );
			if ( r == null ) { Note( $"dock: no row for {f.Name}", fail: true ); return false; }
			r.Select( 0 );
			await Wait( 0.1 );
			return true;
		}
		async Task<bool> MenuItem( Feature f, string text )
		{
			if ( !await Pick( f ) ) return false;
			rows.EmitSignal( Tree.SignalName.ItemMouseSelected, rows.GetItemAreaRect( Row( f )! ).GetCenter(), (long)MouseButton.Right );
			await Wait( 0.2 );
			var m = tree.RowMenu;
			int idx = Enumerable.Range( 0, m.ItemCount ).FirstOrDefault( i => m.GetItemText( i ) == text, -1 );
			if ( !m.Visible || idx < 0 ) { Note( $"dock menu: {text} missing (visible={m.Visible})", fail: true ); return false; }
			// The way PopupMenu.activate_item does it: hide, then say which item.
			m.Hide();
			m.EmitSignal( PopupMenu.SignalName.IdPressed, (long)m.GetItemId( idx ) );
			await Wait( 0.3 );
			return true;
		}

		Shot( "dock-rows" );
		CheckClipping( "dock rows" );

		// Double-click the sketch: it opens in the viewport for editing.
		if ( await Pick( sketch ) )
		{
			Note( $"dock select: SelectedFeatureId={GeppettoCad.SelectedFeatureId}", fail: GeppettoCad.SelectedFeatureId != sketch.Id );
			rows.EmitSignal( Tree.SignalName.ItemActivated );
			await Wait( 0.3 );
			Note( $"dock double-click sketch: tool={GeppettoTools.Active?.Name ?? "none"} editing={( GeppettoTools.Active as CadSketch )?.Editing?.Id}",
				fail: GeppettoTools.Active is not CadSketch { Editing: not null } );
			Shot( "dock-sketch-edit" );
			GeppettoTools.Activate( null );
			await Wait( 0.3 );
		}

		// Double-click a non-sketch feature: its settings box.
		if ( await Pick( target ) )
		{
			rows.EmitSignal( Tree.SignalName.ItemActivated );
			await Wait( 0.3 );
			var b = box();
			Note( $"dock double-click {target.Name}: box={b?.Title}", fail: b == null || b.Feature != target );
			b?.GetCancelButton().EmitSignal( BaseButton.SignalName.Pressed );
			await Wait( 0.3 );
		}

		// Edit...: change the revolve's angle, Apply, undo/redo.
		if ( await MenuItem( target, "Edit..." ) )
		{
			string before = state();
			int faces = Doc0().Studio.Bodies.Sum( x => x.Mesh?.FaceCount ?? 0 );
			var b = box();
			if ( b == null ) Note( "dock Edit...: no box", fail: true );
			else
			{
				Shot( "dock-edit-box" );
				CheckClipping( "dock edit box" );
				await TypeInto( b, "Angle", "180", "dock edit" );
				await Wait( 0.2 );
				b.GetOkButton().EmitSignal( BaseButton.SignalName.Pressed );
				await Wait( 0.5 );
				int faces2 = Doc0().Studio.Bodies.Sum( x => x.Mesh?.FaceCount ?? 0 );
				var ur = GetUndoRedo();
				var h = ur.GetHistoryUndoRedo( ur.GetObjectHistoryId( EditorInterface.Singleton.GetEditedSceneRoot() ) );
				string label = h.GetCurrentActionName();
				Note( $"dock Edit... apply: angle {( target as RevolveFeature )?.Angle.Value}, history '{label}', error={target.Error ?? "none"}",
					fail: target is RevolveFeature { Angle.Value: not 180f } || target.Error != null || !label.Contains( "Edit" ) );
				h.Undo(); await Wait( 0.3 );
				float undone = ( target as RevolveFeature )?.Angle.Value ?? 0;
				h.Redo(); await Wait( 0.3 );
				Note( $"dock Edit... undo angle {undone}, redo {( target as RevolveFeature )?.Angle.Value}", fail: undone == 180f || ( target as RevolveFeature )?.Angle.Value != 180f );
			}
		}

		foreach ( var (item, check) in new (string, Func<bool>)[]
		{
			( "Suppress", () => target.Suppressed ),
			( "Unsuppress", () => !target.Suppressed ),
			( "Hide", () => !target.Visible ),
			( "Show", () => target.Visible ),
		} )
		{
			if ( !await MenuItem( target, item ) ) continue;
			Note( $"dock {item}: {state()}", fail: !check() );
			await undoRedo( "dock " + item );
		}

		// Rename: the row turns into a text field; type and press Enter.
		if ( await MenuItem( target, "Rename" ) )
		{
			await Wait( 0.2 );
			var edit = Internal( rows ).OfType<LineEdit>().FirstOrDefault( l => l.IsVisibleInTree() );
			if ( edit == null ) Note( $"dock rename: no text field on the row ({Internal( rows ).OfType<LineEdit>().Count()} hidden)", fail: true );
			else
			{
				Shot( "dock-rename-field" );
				var w = edit.GetViewport();
				w.PushInput( new InputEventKey { Keycode = Godot.Key.A, CtrlPressed = true, Pressed = true }, true );
				foreach ( char ch in "Lathe Ring" ) w.PushInput( new InputEventKey { Unicode = ch, Keycode = ch == ' ' ? Godot.Key.Space : Godot.Key.A + ( char.ToUpperInvariant( ch ) - 'A' ), ShiftPressed = char.IsUpper( ch ), Pressed = true }, true );
				w.PushInput( new InputEventKey { Keycode = Godot.Key.Enter, Pressed = true }, true );
				await Wait( 0.3 );
				Shot( "dock-renamed" );
				Note( $"dock rename: {target.Name}, row '{Row( target )?.GetText( 0 )}'", fail: target.Name != "Lathe Ring" || Row( target )?.GetText( 0 )?.Contains( "Lathe Ring" ) != true );
				await undoRedo( "dock rename" );
			}
		}

		if ( await MenuItem( target, "Delete" ) )
		{
			bool gone = !Doc0().Studio.Features.Contains( target );
			Note( $"dock delete: gone={gone} {state()}", fail: !gone );
			if ( gone )
			{
				var ur = GetUndoRedo();
				var h = ur.GetHistoryUndoRedo( ur.GetObjectHistoryId( EditorInterface.Singleton.GetEditedSceneRoot() ) );
				h.Undo(); await Wait( 0.3 );
				bool back = Doc0().Studio.Features.Any( f => f.Id == target.Id );
				h.Redo(); await Wait( 0.3 );
				bool goneAgain = Doc0().Studio.Features.All( f => f.Id != target.Id );
				Note( $"dock delete undo: back={back}, redo gone={goneAgain}", fail: !back || !goneAgain );
				h.Undo(); await Wait( 0.3 );
			}
		}
		Shot( "dock-after" );
		CheckClipping( "dock after" );

		GeppettoDocument Doc0() => _character!.Doc!;
	}

	static IEnumerable<Node> Internal( Node n )
	{
		foreach ( var c in n.GetChildren( true ) )
		{
			yield return c;
			foreach ( var d in Internal( c ) ) yield return d;
		}
	}

	/// <summary>The value fields of the settings box row whose label starts with <paramref name="label"/>.</summary>
	static List<EditorSpinSlider> Sliders( GeppettoFeatureDialog box, string label )
	{
		var l = Walk( box ).OfType<Label>().FirstOrDefault( x => x.Text.StartsWith( label, StringComparison.Ordinal ) && x.IsVisibleInTree() );
		return l?.GetParent() is { } row ? Walk( row ).OfType<EditorSpinSlider>().ToList() : new();
	}

	/// <summary>Types into a number field the way a user does: click it, type, Enter.</summary>
	async Task TypeInto( GeppettoFeatureDialog box, string label, string text, string step )
	{
		var s = Sliders( box, label ).FirstOrDefault();
		if ( s == null ) { Note( $"{step}: no {label} field", fail: true ); return; }
		await TypeInto( s, box, text, step + " " + label );
	}

	async Task TypeInto( EditorSpinSlider s, GeppettoFeatureDialog box, string text, string step )
	{
		var w = s.GetViewport();
		var at = s.GetGlobalTransformWithCanvas() * ( s.Size / 2 );
		w.PushInput( new InputEventMouseMotion { Position = at, GlobalPosition = at }, true );
		foreach ( bool down in new[] { true, false } )
			w.PushInput( new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = down, Position = at, GlobalPosition = at, ButtonMask = down ? MouseButtonMask.Left : 0 }, true );
		await Wait( 0.2 );
		var edit = Internal( s ).OfType<LineEdit>().FirstOrDefault( l => l.IsVisibleInTree() );
		if ( edit == null ) { Note( $"{step}: clicking the field opened no text box", fail: true ); return; }
		edit.SelectAll();
		foreach ( char ch in text )
		{
			var k = ch == '.' ? Godot.Key.Period : ch == '-' ? Godot.Key.Minus : Godot.Key.Key0 + ( ch - '0' );
			w.PushInput( new InputEventKey { Unicode = ch, Keycode = k, Pressed = true }, true );
		}
		w.PushInput( new InputEventKey { Keycode = Godot.Key.Enter, Pressed = true }, true );
		await Wait( 0.2 );
		Note( $"{step}: typed {text} -> {s.Value}", fail: MathF.Abs( (float)s.Value - float.Parse( text, System.Globalization.CultureInfo.InvariantCulture ) ) > 0.01f );
	}

	/// <summary>A row with nothing in it (empty label, a dropdown with no choice) is a lie: fail it.</summary>
	void CheckRows( GeppettoFeatureDialog box, string step )
	{
		int bad = 0;
		foreach ( var c in Walk( box ) )
		{
			if ( !c.IsVisibleInTree() ) continue;
			if ( c is OptionButton { Selected: < 0 } o ) { bad++; Note( $"{step}: dropdown with nothing chosen ({o.GetPath()})", fail: true ); }
			if ( c is Label { Text: "" } l && l.GetParent() is HBoxContainer ) { bad++; Note( $"{step}: blank row ({l.GetPath()})", fail: true ); }
			if ( c is ScrollContainer sc && sc.GetVScrollBar().Visible && sc.Size.Y < 550 ) { bad++; Note( $"{step}: rows cut off, scroll bar on a {sc.Size.Y:F0}px box", fail: true ); }
			if ( c is Label l2 && System.Text.RegularExpressions.Regex.IsMatch( l2.Text, @"\b[0-9a-f]{8,}\b" ) ) { bad++; Note( $"{step}: raw id shown: \"{l2.Text}\"", fail: true ); }
		}
		if ( bad == 0 ) Note( $"{step}: rows filled" );
	}
}
#endif
