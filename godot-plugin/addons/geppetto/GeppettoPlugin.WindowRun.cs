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

// The real-window check, kept out of GeppettoPlugin.cs. Only runs when GEPPETTO_WINDOW_RUN names an
// output folder: it opens GEPPETTO_WINDOW_SCENE, selects the Geppetto character, picks every dial tool the
// way a dial click does (OK-ing feature boxes, dragging viewport tools across the model), saves, reopens,
// exports a GLB, screenshots each step and checks Geppetto's labels for clipped text. Then it quits.
public partial class GeppettoPlugin
{
	string _runOut = "";
	readonly List<string> _runLog = new();
	int _runShot;
	int _runFails;

	void MaybeStartWindowRun()
	{
		_runOut = System.Environment.GetEnvironmentVariable( "GEPPETTO_WINDOW_RUN" ) ?? "";
		if ( _runOut.Length == 0 ) return;
		Directory.CreateDirectory( _runOut );
		_ = System.Environment.GetEnvironmentVariable( "GEPPETTO_WINDOW_WS" ) switch { "SketchEdit" => SketchEditRun(), "Live" => LiveRun(), "Blank" => BlankRun(), "CadAudit" => CadAuditRun(), "Deep" => DeepRun(), "SketchTools" => SketchToolsRun(), "RigAudit" => RigAuditRun(), "ClothingAudit" => ClothingAuditRun(), "AnimateAudit" => AnimateAuditRun(), "NoModelAudit" => NoModelAuditRun(), _ => WindowRun() };
	}

	async Task Wait( double seconds ) => await ToSignal( GetTree().CreateTimer( seconds ), SceneTreeTimer.SignalName.Timeout );

	void Note( string line, bool fail = false )
	{
		if ( fail ) _runFails++;
		var l = ( fail ? "FAIL " : "ok   " ) + line;
		_runLog.Add( l );
		GD.Print( "[WindowRun] " + l );
	}

	/// <summary>Saves the editor window and every other visible window (dialogs, popups) as PNGs.</summary>
	void Shot( string name )
	{
		var safe = new string( name.Select( ch => char.IsLetterOrDigit( ch ) ? ch : '-' ).ToArray() );
		var main = EditorInterface.Singleton.GetBaseControl().GetWindow();
		var file = Path.Combine( _runOut, $"{++_runShot:D3}-{safe}.png" );
		main.GetTexture().GetImage().SavePng( file );
		foreach ( var w in OpenWindows() )
			w.GetTexture()?.GetImage()?.SavePng( Path.Combine( _runOut, $"{_runShot:D3}-{safe}-popup-{w.Title.Replace( '/', '-' )}.png" ) );
	}

	static IEnumerable<Window> OpenWindows()
	{
		var stack = new Stack<Node>();
		stack.Push( EditorInterface.Singleton.GetBaseControl() );
		while ( stack.Count > 0 )
		{
			var n = stack.Pop();
			foreach ( var c in n.GetChildren() )
			{
				if ( c is Window w && w.Visible && w is not PopupMenu ) yield return w;
				stack.Push( c );
			}
		}
	}

	/// <summary>Flags Geppetto labels and buttons whose text is wider than the room they get.</summary>
	void CheckClipping( string step )
	{
		var roots = new List<Control>();
		roots.AddRange( _dials.Where( IsInstanceValid ) );
		if ( _featureTree != null ) roots.Add( _featureTree );
		if ( _timeline != null && _timelineDocked ) roots.Add( _timeline );
		foreach ( var w in OpenWindows() ) roots.AddRange( w.GetChildren().OfType<Control>() );
		int bad = 0;
		foreach ( var root in roots )
			foreach ( var c in Walk( root ) )
			{
				if ( !c.IsVisibleInTree() || c.Size.X < 2 ) continue;
				string text; Font? font; int size; bool safe;
				switch ( c )
				{
					case Label l:
						text = l.Text; font = l.GetThemeFont( "font" ); size = l.GetThemeFontSize( "font_size" );
						safe = l.AutowrapMode != TextServer.AutowrapMode.Off || l.TextOverrunBehavior != TextServer.OverrunBehavior.NoTrimming;
						break;
					case Button b when b is not OptionButton:
						text = b.Text; font = b.GetThemeFont( "font" ); size = b.GetThemeFontSize( "font_size" );
						safe = b.AutowrapMode != TextServer.AutowrapMode.Off || b.TextOverrunBehavior != TextServer.OverrunBehavior.NoTrimming || b.ClipText;
						if ( b.Icon != null ) text += "    ";
						break;
					default: continue;
				}
				if ( safe || string.IsNullOrEmpty( text ) || font == null ) continue;
				float need = text.Split( '\n' ).Max( t => font.GetStringSize( t, HorizontalAlignment.Left, -1, size ).X );
				if ( need > c.Size.X + 1 )
				{
					bad++;
					Note( $"{step}: text spills: \"{text.Trim()}\" needs {need:F0}px, has {c.Size.X:F0}px ({c.GetPath()})", fail: true );
				}
			}
		if ( bad == 0 ) Note( $"{step}: no clipped text" );
	}

	/// <summary>Fails when the shown dial's ring is not inside the editor window, where Poo can see it.</summary>
	/// <summary>A dock with a tall minimum (an unsized wrapped label) makes the whole editor taller than
	/// the window and pushes the bottom panel off screen; every Geppetto dock must leave it fitting.</summary>
	void CheckEditorFits( string step )
	{
		var win = EditorInterface.Singleton.GetBaseControl().GetWindow().Size;
		var min = EditorInterface.Singleton.GetBaseControl().GetWindow().GetContentsMinimumSize();
		var docks = new List<string>();
		foreach ( var d in new Control?[] { _featureTree, _timeline, RigPanel.Shown } )
			if ( d != null && IsInstanceValid( d ) ) docks.Add( $"{d.Name} {d.GetCombinedMinimumSize().Y:0}" );
		Note( $"{step}: editor minimum {min.X:0}x{min.Y:0} in window {win.X}x{win.Y} (dock minimums: {string.Join( ", ", docks )})", fail: min.Y > win.Y || min.X > win.X );
	}

	void CheckDialOnScreen( string step )
	{
		var win = EditorInterface.Singleton.GetBaseControl().GetWindow();
		var screen = new Rect2( Vector2.Zero, win.Size );
		foreach ( var d in _dials.Where( d => IsInstanceValid( d ) && d.IsVisibleInTree() ) )
		{
			var ring = d.RingRectGlobal();
			Note( $"{step}: dial ring {ring} in window {screen} (dial rect {d.GetGlobalRect()}, base {EditorInterface.Singleton.GetBaseControl().Size})", fail: !screen.Encloses( ring ) );
			return;
		}
		Note( $"{step}: no dial shown", fail: true );
	}

	static IEnumerable<Control> Walk( Node n )
	{
		if ( n is Control c ) yield return c;
		foreach ( var ch in n.GetChildren() )
			foreach ( var d in Walk( ch ) ) yield return d;
	}

	/// <summary>Drags the active viewport tool across the middle of the first 3D viewport.</summary>
	void DragActiveTool()
	{
		if ( GeppettoTools.Active is not { } tool ) return;
		var vp = EditorInterface.Singleton.GetEditorViewport3D( 0 );
		var cam = vp.GetCamera3D();
		var mid = vp.GetVisibleRect().Size / 2;
		InputEvent Press( bool down, Vector2 at ) => new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = down, Position = at, ButtonMask = down ? MouseButtonMask.Left : 0 };
		tool.Input( cam, new InputEventMouseMotion { Position = mid } );
		tool.Input( cam, Press( true, mid ) );
		for ( int i = 1; i <= 6; i++ )
			tool.Input( cam, new InputEventMouseMotion { Position = mid + new Vector2( i * 6, i * 3 ), Relative = new Vector2( 6, 3 ), ButtonMask = MouseButtonMask.Left } );
		tool.Input( cam, Press( false, mid + new Vector2( 36, 18 ) ) );
	}

	void SelectCharacter()
	{
		var root = EditorInterface.Singleton.GetEditedSceneRoot();
		var sel = EditorInterface.Singleton.GetSelection();
		sel.Clear();
		// "Character", else the first imported .effigy in the scene (the test project names them LineCook etc.).
		var node = root?.FindChild( "Character", true, false )
			?? root?.FindChildren( "*", "", true, false ).FirstOrDefault( n => n.HasMeta( EffigySceneImporter.EffigyMeta ) )
			?? root;
		if ( node != null ) sel.AddNode( node );
		RefreshDial();
	}

	async Task WindowRun()
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
			int features0 = doc.Studio.Features.Count;
			Note( $"open: {_character.EffigyPath}, {features0} features, dial visible={_dials.Any( d => d.Visible )}" );
			Shot( "open" );
			CheckClipping( "open" );
			CheckDialOnScreen( "open" );
			CheckEditorFits( "open" );

			// GEPPETTO_WINDOW_WS=Animate runs just that workspace (Materials: just the material checks).
			string only = System.Environment.GetEnvironmentVariable( "GEPPETTO_WINDOW_WS" ) ?? "";
			var picked = only.Split( ',', StringSplitOptions.RemoveEmptyEntries );   // or a list: Rig,Animate
			bool Runs( string name ) => picked.Length == 0 || picked.Contains( name );
			// GEPPETTO_WINDOW_SAVE_EACH=1 saves the scene between steps, to find the one that leaves it unsaveable.
			bool saveEach = System.Environment.GetEnvironmentVariable( "GEPPETTO_WINDOW_SAVE_EACH" ) == "1";
			async Task SaveProbe( string after )
			{
				if ( !saveEach ) return;
				GD.Print( $"[WindowRun] save probe after {after}" );
				_SaveExternalData();
				EditorInterface.Singleton.SaveScene();
				await Wait( 0.5 );
				GD.Print( $"[WindowRun] save probe after {after}: ok" );
			}
			if ( Runs( "Materials" ) ) { await MaterialRun( doc ); await SaveProbe( "Materials" ); }
			if ( Runs( "Primitives" ) ) { await PrimitiveRun( doc ); await SaveProbe( "Primitives" ); }
			for ( int w = 0; w < GeppettoDialData.Workspaces.Length; w++ )
			{
				var ws = GeppettoDialData.Workspaces[w];
				if ( !Runs( ws.Name ) ) continue;
				if ( w > 0 ) await SaveProbe( $"before {ws.Name}" );
				Note( $"{ws.Name}: switching" );
				GeppettoDialState.Ws = w;
				GeppettoDialState.Touch();   // redraws the ring, as a click would
				RefreshDial();
				await Wait( 0.3 );
				Shot( $"{ws.Name}-workspace" );
				CheckClipping( ws.Name );
				// the stage ring and each stage's tool ring, for icon/label shots
				GeppettoDialState.OpenWorkspace( w );
				RefreshDial();
				await Wait( 0.8 );
				Shot( $"{ws.Name}-ring-stages" );
				for ( int s = 0; s < ws.Stages.Length; s++ )
				{
					GeppettoDialState.OpenStageTools( s );
					RefreshDial();
					await Wait( 0.8 );
					Shot( $"{ws.Name}-ring-{ws.Stages[s].Stage}" );
					foreach ( var tile in ws.Stages[s].Tools.Where( t => GeppettoDialData.Group( ws.Name, t ) != null ) )
					{
						GeppettoDialState.OpenGroupTools( tile );
						RefreshDial();
						await Wait( 0.8 );
						Shot( $"{ws.Name}-ring-{ws.Stages[s].Stage}-{tile}" );
						CheckClipping( $"{ws.Name} > {tile} ring" );
						GeppettoDialState.Back();
					}
				}
				GeppettoDialState.Level = 0;
				GeppettoDialState.Touch();
				RefreshDial();
				foreach ( var (stage, tools) in ws.Stages )
					foreach ( var raw in tools.SelectMany( t => GeppettoDialData.Group( ws.Name, t ) ?? new[] { t } ) )
					{
						string tool = GeppettoDialData.Clean( raw );
						if ( tool is "Delete" or "Remesh" or "Subdivide" ) continue; // would wreck the model for the later steps
						SelectCharacter();
						int errs = 0;
						try { if ( !RunTool( ws.Name, tool ) ) { Note( $"{ws.Name} > {tool}: not wired", fail: true ); continue; } }
						catch ( Exception e ) { errs++; Note( $"{ws.Name} > {tool}: threw {e.GetType().Name}: {e.Message}", fail: true ); }
						await Wait( 0.35 );
						DragActiveTool();
						await Wait( 0.25 );
						Shot( $"{ws.Name}-{tool}" );
						CheckClipping( $"{ws.Name} > {tool}" );
						foreach ( var win in OpenWindows().ToList() )
						{
							if ( win is GeppettoFeatureDialog fd ) fd.GetOkButton().EmitSignal( BaseButton.SignalName.Pressed );
							else win.Hide();
						}
						GeppettoTools.Activate( null );
						await Wait( 0.2 );
						var report = _character?.Doc?.LastReport;
						if ( errs == 0 ) Note( $"{ws.Name} > {tool}: ran{( report is { Errors.Count: > 0 } ? $" (rebuild errors: {report.Errors.Count})" : "" )}" );
					}
			}

			// Save, reopen and check the edits came back.
			doc = _character!.Doc!;
			int featuresEdited = doc.Studio.Features.Count;
			_SaveExternalData();
			EditorInterface.Singleton.SaveScene();
			await Wait( 1 );
			Note( $"save: features {features0} -> {featuresEdited}, dirty after save={doc.Session.IsDirty}", fail: doc.Session.IsDirty );
			Shot( "saved" );
			string effigy = ProjectSettings.GlobalizePath( _character.EffigyPath );
			GeppettoDocument.CloseAll();
			EditorInterface.Singleton.ReloadSceneFromPath( scene );
			await Wait( 2 );
			SelectCharacter();
			await Wait( 1 );
			int featuresBack = _character?.Doc?.Studio.Features.Count ?? -1;
			Note( $"reopen: {featuresBack} features (saved {featuresEdited})", fail: featuresBack != featuresEdited );
			Shot( "reopened" );

			string glb = Path.Combine( _runOut, "export.glb" );
			var err = EffigyGlbExporter.ExportEffigy( effigy, glb );
			long bytes = File.Exists( glb ) ? new FileInfo( glb ).Length : 0;
			Note( $"export GLB: {err}, {bytes} bytes", fail: err != Error.Ok || bytes == 0 );
		}
		catch ( Exception e ) { Note( "run threw: " + e, fail: true ); }
		Finish();
	}

	/// <summary>The material ring's manager: save a custom material (with a texture) into this project,
	/// see it on the ring, the new/edit box and right-click menu, put it on Model faces and on a whole
	/// slot, rename it, delete it to .trash. Writes only under res://materials (run on a copy).</summary>
	async Task MaterialRun( GeppettoDocument doc )
	{
		try
		{
			GeppettoDialState.Ws = Array.FindIndex( GeppettoDialData.Workspaces, w => w.Name == "Model" );
			GeppettoDialState.Touch();   // redraws the ring, as a click would
			GeppettoDialState.MatOpen = false;
			RefreshDial();
			// a 64px checker as the colour texture
			string png = Path.Combine( _runOut, "checker.png" );
			var img = Image.CreateEmpty( 64, 64, false, Image.Format.Rgb8 );
			for ( int y = 0; y < 64; y++ ) for ( int x = 0; x < 64; x++ ) img.SetPixel( x, y, ( ( x / 8 + y / 8 ) & 1 ) == 0 ? new Color( "c8462e" ) : new Color( "f0d9a0" ) );
			img.SavePng( png );
			var saved = GeppettoMaterialLibrary.Save( "Window Test Brick", new Color( "b04a2a" ), 0.8f, 0f, png, false, "project" );
			var gold = GeppettoMaterialLibrary.Save( "Window Test Gold", new Color( "d9a93a" ), 0.25f, 1f, null, false, "project" );
			Note( $"materials: saved {saved.Dir} (albedo={saved.Look.AlbedoMap != null}) and {gold.Name}", fail: saved.Look.AlbedoMap == null || !File.Exists( Path.Combine( saved.Dir!, "window_test_brick.tres" ) ) );
			GeppettoDialState.ToggleMaterials();
			await Wait( 0.6 );
			bool onRing = GeppettoDialState.Materials.Any( m => m.Name == "Window Test Brick" );
			int plusAt = GeppettoDialState.Materials.FindIndex( m => m.IsNew );
			Note( $"materials: ring has {GeppettoDialState.Materials.Count} balls, custom on it={onRing}, \"+\" at ball {plusAt} (must be 0, page 1)", fail: !onRing || plusAt != 0 );
			Shot( "materials-ring" );
			CheckClipping( "materials ring" );
			CheckDialOnScreen( "materials ring" );

			var box = GeppettoMaterialDialog.Open( null, saved, null );
			await Wait( 0.4 );
			Shot( "materials-new-box" );
			CheckClipping( "material box" );
			box.QueueFree();
			await Wait( 0.1 );

			OnMaterialMenu( saved, new Vector2( 900, 400 ) );
			await Wait( 0.4 );
			if ( _matMenu != null )
			{
				_matMenu.GetTexture()?.GetImage()?.SavePng( Path.Combine( _runOut, $"{++_runShot:D3}-materials-menu.png" ) );
				var items = Enumerable.Range( 0, _matMenu.ItemCount ).Select( i => _matMenu.GetItemText( i ) ).Where( t => t.Length > 0 ).ToList();
				Note( $"materials: menu = {string.Join( " | ", items )}", fail: !items.Contains( "Rename…" ) || !items.Contains( "Delete" ) );
				_matMenu.Hide();
			}

			// Model: select two faces, pick the brick, the faces move to its slot.
			int coloured0 = ColouredSurfaces();
			var edit = GeppettoModelEdit.For( doc );
			edit.SetMode( EditElement.Face );
			// the test character has several bodies: open the biggest, as clicking it would
			if ( edit.Session == null && doc.Studio.Bodies.Where( b => b.Visible && b.Mesh is { FaceCount: > 1 } ).OrderByDescending( b => b.Mesh!.FaceCount ).FirstOrDefault() is { } body )
				edit.Begin( body.Id );
			if ( edit.Session is not { } ses || ses.Mesh.FaceCount < 2 ) Note( "materials: no Model session to test faces", fail: true );
			else
			{
				ses.SelectFace( 0 ); ses.SelectFace( 1, MeshEditSession.Combine.Add ); edit.SelectionChanged();
				GeppettoDialState.PickMaterial( saved );
				await Wait( 0.5 );
				var s2 = edit.Session!;
				int slot0 = s2.Mesh.Faces[0].Material, slot1 = s2.Mesh.Faces[1].Material;
				bool lookSet = doc.Studio.MaterialLooks.TryGetValue( slot0, out var lk ) && lk.AlbedoMap == saved.Look.AlbedoMap;
				Note( $"materials: Model faces 0,1 on slot {slot0},{slot1}, look has the texture={lookSet}", fail: slot0 != slot1 || slot0 == 0 || !lookSet );
				int coloured1 = ColouredSurfaces();
				Note( $"materials: coloured Body surfaces {coloured0} before Model, {coloured1} after the face pick (grey view if it drops)", fail: coloured1 < coloured0 );
				Shot( "materials-model-faces" );
				// whole slot: the brick's slot turns gold, undoably
				SetSlotLook( doc, slot0, gold );
				await Wait( 0.4 );
				bool isGold = doc.Studio.MaterialLooks.TryGetValue( slot0, out var g ) && g.Metal == 1f;
				Note( $"materials: slot {slot0} now gold={isGold}", fail: !isGold );
				Shot( "materials-slot-gold" );
			}

			var renamed = GeppettoMaterialLibrary.Rename( saved, "Window Test Brick Two" );
			bool renOk = Directory.Exists( renamed.Dir! ) && !Directory.Exists( saved.Dir! ) && File.Exists( Path.Combine( renamed.Dir!, "window_test_brick_two_albedo.png" ) );
			bool repointed = doc.Studio.MaterialLooks.Values.All( l => l.AlbedoMap == null || File.Exists( l.AlbedoMap ) );
			Note( $"materials: renamed to {renamed.Dir}, files moved={renOk}, slot maps still found={repointed}", fail: !renOk || !repointed );
			string trash = GeppettoMaterialLibrary.Delete( renamed );
			GeppettoMaterialLibrary.Delete( gold );
			GeppettoDialState.ReloadMaterials();
			bool gone = !GeppettoDialState.Materials.Any( m => m.Name.StartsWith( "Window Test" ) ) && Directory.Exists( trash );
			Note( $"materials: deleted to {trash}, off the ring={gone}", fail: !gone );
			GeppettoDialState.MatOpen = false;
			GeppettoDialState.Touch();
		}
		catch ( Exception e ) { Note( "materials: threw " + e, fail: true ); }
	}

	/// <summary>Add Primitive from both fans, the way a click does: the shape menu, then the settings box
	/// (size and segments), OK. Each shape must add one feature and one body, beside the model (FitToModel), and undo and
	/// redo as one step. Undone again afterwards so the later steps see the model unchanged.</summary>
	async Task PrimitiveRun( GeppettoDocument doc )
	{
		try
		{
			var shapes = GeppettoCad.Choices( "Primitive" )!;
			var root = EditorInterface.Singleton.GetEditedSceneRoot();
			var ur = GetUndoRedo();
			var history = ur.GetHistoryUndoRedo( ur.GetObjectHistoryId( root ) );
			foreach ( var ws in new[] { "CAD", "Model" } )
			{
				GeppettoDialState.Ws = Array.FindIndex( GeppettoDialData.Workspaces, w => w.Name == ws );
				GeppettoDialState.Touch();   // redraws the ring, as a click would
				RefreshDial();
				for ( int i = 0; i < shapes.Length; i++ )
				{
					string step = $"{ws} > Primitive > {shapes[i]}";
					SelectCharacter();
					int features = doc.Studio.Features.Count, bodies = doc.Studio.Bodies.Count;
					// Where the tool should stand it: the origin on an empty model, else beside the model.
					var expect = new PrimitiveFeature();
					GeppettoCad.FitToModel( doc.Studio, expect );
					if ( !RunTool( ws, "Primitive" ) ) { Note( $"{step}: not wired", fail: true ); continue; }
					await Wait( 0.3 );
					var menu = EditorInterface.Singleton.GetBaseControl().GetChildren().OfType<PopupMenu>().LastOrDefault( m => m.Visible );
					if ( menu == null ) { Note( $"{step}: no shape menu", fail: true ); continue; }
					menu.EmitSignal( PopupMenu.SignalName.IdPressed, (long)i );
					menu.Hide();
					await Wait( 0.4 );
					var box = OpenWindows().OfType<GeppettoFeatureDialog>().LastOrDefault();
					if ( box?.Feature is not PrimitiveFeature prim ) { Note( $"{step}: no settings box", fail: true ); continue; }
					bool sized = prim.Parameters.Any( p => p == prim.SizeX || p == prim.Radius ) && ( prim.Parameters.Contains( prim.Segments ) || prim.Parameters.Contains( prim.Divisions ) || shapes[i] is "Box" or "Wedge" );
					Shot( $"{ws}-primitive-{shapes[i]}-box" );
					CheckClipping( $"{step} box" );
					box.GetOkButton().EmitSignal( BaseButton.SignalName.Pressed );
					await Wait( 0.4 );
					int addF = doc.Studio.Features.Count - features, addB = doc.Studio.Bodies.Count - bodies;
					var last = doc.Studio.Features.LastOrDefault() as PrimitiveFeature;
					bool placed = last != null && ( last.Position.Value - expect.Position.Value ).Length < 1e-3f;
					Note( $"{step}: +{addF} feature, +{addB} body, shape={last?.Shape.Value}, size/segments editable={sized}, placed beside the model={placed}",
						fail: addF != 1 || addB < 1 || last?.Shape.Value != shapes[i] || !sized || !placed );
					Shot( $"{ws}-primitive-{shapes[i]}" );
					history.Undo();
					await Wait( 0.2 );
					int undone = doc.Studio.Features.Count;
					history.Redo();
					await Wait( 0.2 );
					int redone = doc.Studio.Features.Count;
					history.Undo();
					await Wait( 0.2 );
					Note( $"{step}: undo {undone}, redo {redone}, undo again {doc.Studio.Features.Count} (from {features})",
						fail: undone != features || redone != features + 1 || doc.Studio.Features.Count != features );
				}
			}
		}
		catch ( Exception e ) { Note( "primitives: threw " + e, fail: true ); }
	}

	/// <summary>Surfaces of the selected character's Body whose material is not plain grey: a texture or
	/// a tinted albedo. Drops to 0 when a rebuild loses the slot looks and the view goes grey.</summary>
	int ColouredSurfaces()
	{
		var root = EditorInterface.Singleton.GetEditedSceneRoot();
		var node = EditorInterface.Singleton.GetSelection().GetSelectedNodes().FirstOrDefault() ?? root;
		if ( node?.FindChild( "Body", true, false ) is not MeshInstance3D { Mesh: { } mesh } body ) return 0;
		int n = 0;
		for ( int i = 0; i < mesh.GetSurfaceCount(); i++ )
			if ( ( body.GetSurfaceOverrideMaterial( i ) ?? mesh.SurfaceGetMaterial( i ) ) is BaseMaterial3D m
				&& ( m.AlbedoTexture != null || Mathf.Abs( m.AlbedoColor.R - m.AlbedoColor.G ) > 0.02f || Mathf.Abs( m.AlbedoColor.G - m.AlbedoColor.B ) > 0.02f ) )
				n++;
		return n;
	}

	void Finish()
	{
		_runLog.Add( $"RESULT fails={_runFails}" );
		File.WriteAllLines( Path.Combine( _runOut, "run.txt" ), _runLog );
		GD.Print( $"[WindowRun] done, {_runFails} failures, shots in {_runOut}" );
		if ( System.Environment.GetEnvironmentVariable( "GEPPETTO_WINDOW_KEEP" ) != "1" ) GetTree().Quit( _runFails > 0 ? 1 : 0 );
	}
}
#endif
