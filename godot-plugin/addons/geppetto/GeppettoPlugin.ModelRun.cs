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

// GEPPETTO_WINDOW_WS=Deep: the Model, material ring and Paint tools the way Poo uses them, in an EMPTY
// scene. Dial picks go through GeppettoDialState.PickTool (the ring's own click path), viewport clicks
// through the active tool's Input, dialogs through their own buttons. Every op is checked for a real
// change, one undo step that puts the mesh back, a redo, and the Body keeping its colours. Materials are
// saved in this project's res://materials only.
public partial class GeppettoPlugin
{
	async Task DeepRun()
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
			Note( $"open: {scene}, model before = {( _character == null ? "none" : _character.EffigyPath )}", fail: _character != null );

			var doc = await DeepEmptyStart();
			if ( doc == null ) { Finish(); return; }
			await DeepMaterials( doc );
			await DeepModelOps( doc );
			await DeepPaint( doc, scene );
		}
		catch ( Exception e ) { Note( "deep: threw " + e, fail: true ); }
		Finish();
	}

	// =========================================================================================
	// Driving the editor like a user
	// =========================================================================================

	/// <summary>Opens the workspace and stage on the ring and clicks the tool, as the dial does.</summary>
	static bool DialPick( string ws, string tool )
	{
		int w = Array.FindIndex( GeppettoDialData.Workspaces, x => x.Name == ws );
		if ( w < 0 ) return false;
		var stages = GeppettoDialData.Workspaces[w].Stages;
		int st = Array.FindIndex( stages, s => s.Tools.Any( t => GeppettoDialData.Clean( t ) == tool ) );
		if ( st < 0 ) return false;
		GeppettoDialState.OpenWorkspace( w );
		GeppettoDialState.OpenStageTools( st );
		GeppettoDialState.PickTool( stages[st].Tools.First( t => GeppettoDialData.Clean( t ) == tool ) );
		return true;
	}

	static Camera3D DeepCam => EditorInterface.Singleton.GetEditorViewport3D( 0 ).GetCamera3D();

	static InputEventMouseButton Mouse( bool down, Vector2 at, bool shift = false, bool ctrl = false, bool alt = false ) => new()
	{
		ButtonIndex = MouseButton.Left, Pressed = down, Position = at, ButtonMask = down ? MouseButtonMask.Left : 0,
		ShiftPressed = shift, CtrlPressed = ctrl, AltPressed = alt,
	};

	async Task ViewClick( Vector2 at, bool shift = false, bool ctrl = false, bool alt = false )
	{
		var t = GeppettoTools.Active;
		if ( t == null ) return;
		var cam = DeepCam;
		t.Input( cam, new InputEventMouseMotion { Position = at } );
		t.Input( cam, Mouse( true, at, shift, ctrl, alt ) );
		t.Input( cam, Mouse( false, at, shift, ctrl, alt ) );
		await Wait( 0.12 );
	}

	/// <summary>The viewport's "Frame selection" (F) on the Body, through the 3D view's own input.</summary>
	async Task FrameBody()
	{
		var vp = EditorInterface.Singleton.GetEditorViewport3D( 0 );
		var view = vp.GetParent()?.GetParent();
		var surface = view?.GetChildren().OfType<Control>().FirstOrDefault( c => c is not SubViewportContainer && c.GetClass() == "Control" );
		var body = ( _character?.Root as Node )?.FindChild( "Body", true, false ) as Node;
		if ( surface == null || body == null ) { Note( "frame: no 3D view surface or Body", fail: true ); return; }
		var sel = EditorInterface.Singleton.GetSelection();
		var keep = sel.GetSelectedNodes().ToList();
		sel.Clear(); sel.AddNode( body );
		await Wait( 0.1 );
		var before = DeepCam.GlobalPosition;
		surface.GrabFocus();
		surface.EmitSignal( Control.SignalName.GuiInput, new InputEventKey { Keycode = Key.F, PhysicalKeycode = Key.F, Pressed = true } );
		await Wait( 0.6 );
		sel.Clear();
		foreach ( var n in keep ) sel.AddNode( n );
		RefreshDial();
		await Wait( 0.1 );
		Note( $"frame: camera {before} -> {DeepCam.GlobalPosition}" );
	}

	Transform3D DeepBodyXform =>
		( _character?.Root as Node )?.FindChild( "Body", true, false ) is Node3D n ? n.GlobalTransform
		: _character?.Root is Node3D r ? r.GlobalTransform : Transform3D.Identity;

	Vector3 DeepWorld( Vec3 p ) => DeepBodyXform * EffigyMeshBridge.TransformPosition( p.x, p.y, p.z );
	Vector2 DeepScreen( Vec3 p ) => DeepCam.UnprojectPosition( DeepWorld( p ) );

	bool Facing( PolyMesh m, int f )
	{
		var c = DeepWorld( m.FaceCentroid( m.Faces[f] ) );
		var tip = DeepWorld( m.FaceCentroid( m.Faces[f] ) + m.FaceNormal( m.Faces[f] ) );
		return ( tip - c ).Normalized().Dot( ( DeepCam.GlobalPosition - c ).Normalized() ) > 0.15f;
	}

	/// <summary>Front faces, most squarely facing the camera first.</summary>
	List<int> FrontFaces( PolyMesh m ) => Enumerable.Range( 0, m.FaceCount ).Where( f => Facing( m, f ) )
		.OrderByDescending( f =>
		{
			var c = DeepWorld( m.FaceCentroid( m.Faces[f] ) );
			var tip = DeepWorld( m.FaceCentroid( m.Faces[f] ) + m.FaceNormal( m.Faces[f] ) );
			return ( tip - c ).Normalized().Dot( ( DeepCam.GlobalPosition - c ).Normalized() );
		} ).ToList();

	MeshEditSession? Ses( GeppettoDocument doc ) => GeppettoModelEdit.For( doc ).Session;

	string SelText( GeppettoDocument doc ) => Ses( doc ) is { } s ? $"{s.SelectedVertices.Count}v {s.SelectedEdges.Count}e {s.SelectedFaces.Count}f" : "no session";

	/// <summary>Clicks a face on the screen; true when the click selected exactly that face.</summary>
	async Task<bool> ClickFace( GeppettoDocument doc, int f, bool shift = false )
	{
		var m = Ses( doc )!.Mesh;
		await ViewClick( DeepScreen( m.FaceCentroid( m.Faces[f] ) ), shift );
		return Ses( doc ) is { } s && s.SelectedFaces.Contains( f ) && ( shift || s.SelectedFaces.Count == 1 );
	}

	async Task<int> PickFrontFace( GeppettoDocument doc, int skip = 0, bool shift = false )
	{
		if ( Ses( doc ) == null ) GeppettoModelEdit.For( doc ).EnsureSessionQuiet();
		foreach ( int f in FrontFaces( Ses( doc )!.Mesh ).Skip( skip ) )
			if ( await ClickFace( doc, f, shift ) ) return f;
		return -1;
	}

	/// <summary>Clicks an edge of a front face (just inside the face, the way the select tool reads it).</summary>
	async Task<EdgeKey?> PickFrontEdge( GeppettoDocument doc, bool alt = false, bool shift = false, Func<EdgeKey, bool>? want = null )
	{
		if ( Ses( doc ) == null ) GeppettoModelEdit.For( doc ).EnsureSessionQuiet();
		var m = Ses( doc )!.Mesh;
		foreach ( int f in FrontFaces( m ) )
		{
			var idx = m.Faces[f].Indices;
			var c = m.FaceCentroid( m.Faces[f] );
			for ( int i = 0; i < idx.Length; i++ )
			{
				var e = new EdgeKey( idx[i], idx[( i + 1 ) % idx.Length] );
				if ( want != null && !want( e ) ) continue;
				var mid = ( m.Positions[e.A] + m.Positions[e.B] ) * 0.5f;
				await ViewClick( DeepScreen( mid + ( c - mid ) * 0.08f ), shift, alt: alt );
				if ( Ses( doc ) is { } s && s.SelectedEdges.Contains( e ) ) return e;
			}
		}
		return null;
	}

	async Task<int> PickFrontVertex( GeppettoDocument doc, bool shift = false, Func<int, bool>? want = null )
	{
		if ( Ses( doc ) == null ) GeppettoModelEdit.For( doc ).EnsureSessionQuiet();
		var m = Ses( doc )!.Mesh;
		foreach ( int f in FrontFaces( m ) )
		{
			var c = m.FaceCentroid( m.Faces[f] );
			foreach ( int v in m.Faces[f].Indices )
			{
				if ( want != null && !want( v ) ) continue;
				await ViewClick( DeepScreen( m.Positions[v] + ( c - m.Positions[v] ) * 0.05f ), shift );
				if ( Ses( doc ) is { } s && s.SelectedVertices.Contains( v ) ) return v;
			}
		}
		return -1;
	}

	static string MeshSig( GeppettoDocument doc )
	{
		var e = GeppettoModelEdit.For( doc );
		if ( e.Feature == null || !doc.Studio.Features.Contains( e.Feature ) ) return "none";
		var blob = e.Feature.SaveMesh();
		if ( blob == null ) return "none";
		var h = new HashCode();
		foreach ( var b in blob ) h.Add( b );
		return $"{blob.Length}:{h.ToHashCode():x8}";
	}

	UndoRedo DeepHistory()
	{
		var ur = GetUndoRedo();
		return ur.GetHistoryUndoRedo( ur.GetObjectHistoryId( EditorInterface.Singleton.GetEditedSceneRoot() ) );
	}

	// =========================================================================================
	// 1. An empty scene: Model > Face must offer a shape, then pick on it
	// =========================================================================================

	async Task<GeppettoDocument?> DeepEmptyStart()
	{
		Shot( "deep-00-empty-scene" );
		bool ran = DialPick( "Model", "Face" );
		PopupMenu? menu = null;
		for ( int i = 0; i < 40 && menu == null; i++ )
		{
			await Wait( 0.25 );
			menu = EditorInterface.Singleton.GetBaseControl().GetChildren().OfType<PopupMenu>().LastOrDefault( m => m.Visible );
		}
		Note( $"empty scene > Model > Face: ran={ran}, model={_character?.EffigyPath ?? "none"}, shape menu={( menu != null )}",
			fail: !ran || _character?.Doc == null || menu == null );
		var doc = _character?.Doc;
		if ( doc == null || menu == null ) return null;
		menu.GetTexture()?.GetImage()?.SavePng( Path.Combine( _runOut, $"{++_runShot:D3}-deep-shape-menu.png" ) );
		Shot( "deep-01-shape-menu" );
		var shapes = GeppettoCad.Choices( "Primitive" )!;
		menu.EmitSignal( PopupMenu.SignalName.IdPressed, (long)Array.IndexOf( shapes, "Box" ) );
		menu.Hide();
		await Wait( 0.5 );
		var box = OpenWindows().OfType<GeppettoFeatureDialog>().LastOrDefault();
		if ( box == null ) { Note( "empty: no Box settings", fail: true ); return null; }
		Shot( "deep-02-box-settings" );
		CheckClipping( "box settings" );
		box.GetOkButton().EmitSignal( BaseButton.SignalName.Pressed );
		await Wait( 0.6 );
		int faces = doc.Studio.Bodies.Sum( b => b.Mesh?.FaceCount ?? 0 );
		Note( $"empty: Box added, {doc.Studio.Bodies.Count} body / {faces} faces, tool now {GeppettoTools.Active?.Name ?? "none"}",
			fail: faces < 6 || GeppettoTools.Active is not GeppettoModelSelectTool );
		await FrameBody();
		if ( GeppettoTools.Active is not GeppettoModelSelectTool ) DialPick( "Model", "Face" );
		int f = await PickFrontFace( doc );
		Note( $"empty: clicked face {f} on the new box ({SelText( doc )})", fail: f < 0 );
		Shot( "deep-03-box-face-picked" );
		return doc;
	}

	// =========================================================================================
	// 2. The material ring: +, right-click menu, swap
	// =========================================================================================

	async Task DeepMaterials( GeppettoDocument doc )
	{
		try
		{
			string png = Path.Combine( _runOut, "checker.png" );
			var img = Image.CreateEmpty( 64, 64, false, Image.Format.Rgb8 );
			for ( int y = 0; y < 64; y++ ) for ( int x = 0; x < 64; x++ ) img.SetPixel( x, y, ( ( x / 8 + y / 8 ) & 1 ) == 0 ? new Color( "c8462e" ) : new Color( "f0d9a0" ) );
			img.SavePng( png );

			if ( !GeppettoDialState.MatOpen ) GeppettoDialState.ToggleMaterials();
			await Wait( 0.5 );
			Shot( "deep-10-ring" );
			CheckClipping( "material ring" );
			CheckDialOnScreen( "material ring" );

			// "+" ball: the New material box
			var brick = await NewMaterialThroughBox( "Deep Brick", new Color( "b04a2a" ), png );
			var gold = await NewMaterialThroughBox( "Deep Gold", new Color( "d9a93a" ), null );
			if ( brick == null || gold == null ) return;

			// Use on whole slot: every face of the box (slot 0)
			int slot = Ses( doc )?.Mesh.Faces[0].Material ?? 0;
			int col0 = ColouredSurfaces();
			await MenuPick( brick, MenuSlot0 + slot, "Use on whole slot" );
			bool slotBrick = doc.Studio.MaterialNames.TryGetValue( slot, out var sn ) && sn == brick.Name;
			Note( $"materials: whole slot {slot} -> {sn}, coloured surfaces {col0} -> {ColouredSurfaces()}", fail: !slotBrick || ColouredSurfaces() < 1 );
			Shot( "deep-11-slot-brick" );

			// Use on selected faces: click a face, right-click gold
			DialPick( "Model", "Face" );
			int f = await PickFrontFace( doc );
			await MenuPick( gold, MenuFaces, "Use on selected faces" );
			int fslot = Ses( doc )?.Mesh.Faces[Math.Max( 0, f )].Material ?? -1;
			bool faceGold = f >= 0 && doc.Studio.MaterialNames.TryGetValue( fslot, out var fn ) && fn == gold.Name;
			Note( $"materials: face {f} -> slot {fslot} ({( fslot >= 0 && doc.Studio.MaterialNames.TryGetValue( fslot, out var xn ) ? xn : "?" )}), other faces still brick={doc.Studio.MaterialNames.GetValueOrDefault( slot ) == brick.Name}, coloured {ColouredSurfaces()}",
				fail: !faceGold || ColouredSurfaces() < 2 );
			Shot( "deep-12-face-gold" );

			// Swap: the same face to brick again by picking it on the ring (Model + faces selected)
			GeppettoDialState.PickMaterial( brick );
			await Wait( 0.4 );
			int swapped = Ses( doc )?.Mesh.Faces[Math.Max( 0, f )].Material ?? -1;
			Note( $"materials: swap face {f} back to brick from the ring: slot {swapped} ({doc.Studio.MaterialNames.GetValueOrDefault( swapped )})", fail: doc.Studio.MaterialNames.GetValueOrDefault( swapped ) != brick.Name );
			GeppettoDialState.PickMaterial( gold );
			await Wait( 0.4 );
			Shot( "deep-13-swapped" );

			// Edit: change the gold's colour; the slot showing it must change too
			var goldSlot = doc.Studio.MaterialNames.FirstOrDefault( kv => kv.Value == gold.Name ).Key;
			var before = doc.Studio.MaterialLooks.GetValueOrDefault( goldSlot )?.Color;
			await MenuPick( gold, MenuEdit, "Edit…" );
			var edit = OpenWindows().OfType<GeppettoMaterialDialog>().LastOrDefault();
			if ( edit == null ) Note( "materials: Edit opened no box", fail: true );
			else
			{
				Shot( "deep-14-edit-box" );
				CheckClipping( "material edit box" );
				var picker = edit.FindChildren( "*", "ColorPickerButton", true, false ).OfType<ColorPickerButton>().First();
				picker.Color = new Color( "3a7bd9" );
				edit.GetOkButton().EmitSignal( BaseButton.SignalName.Pressed );
				await Wait( 0.5 );
				var after = doc.Studio.MaterialLooks.GetValueOrDefault( goldSlot )?.Color;
				Note( $"materials: Edit gold to blue, slot {goldSlot} look {before} -> {after}", fail: after == null || Equals( before, after ) );
				Shot( "deep-15-edited" );
			}

			// Copy as new
			int count0 = GeppettoDialState.Materials.Count;
			await MenuPick( brick, MenuCopy, "Copy as new…" );
			var copy = OpenWindows().OfType<GeppettoMaterialDialog>().LastOrDefault();
			if ( copy == null ) Note( "materials: Copy opened no box", fail: true );
			else
			{
				SetWhereProject( copy );
				copy.GetOkButton().EmitSignal( BaseButton.SignalName.Pressed );
				await Wait( 0.5 );
				bool has = GeppettoDialState.Materials.Any( m => m.Name == "Deep Brick copy" );
				Note( $"materials: Copy -> \"Deep Brick copy\" on the ring={has} ({count0} -> {GeppettoDialState.Materials.Count} balls)", fail: !has );
			}

			// Rename brick: the slots showing it follow the new name and keep the texture
			await MenuPick( brick, MenuRename, "Rename…" );
			var ren = OpenWindows().OfType<ConfirmationDialog>().LastOrDefault( w => w.Title == "Rename material" );
			if ( ren == null ) Note( "materials: Rename opened no box", fail: true );
			else
			{
				Shot( "deep-16-rename-box" );
				CheckClipping( "rename box" );
				ren.FindChildren( "*", "LineEdit", true, false ).OfType<LineEdit>().First().Text = "Deep Brick Two";
				ren.GetOkButton().EmitSignal( BaseButton.SignalName.Pressed );
				await Wait( 0.5 );
				bool follows = doc.Studio.MaterialNames.ContainsValue( "Deep Brick Two" ) && !doc.Studio.MaterialNames.ContainsValue( "Deep Brick" );
				bool tex = doc.Studio.MaterialLooks.Values.All( l => l.AlbedoMap == null || File.Exists( ProjectSettings.GlobalizePath( l.AlbedoMap ) ) || File.Exists( l.AlbedoMap ) );
				Note( $"materials: Rename -> slots now say Deep Brick Two={follows}, every slot texture still on disk={tex}, coloured {ColouredSurfaces()}", fail: !follows || !tex || ColouredSurfaces() < 1 );
				brick = GeppettoDialState.Materials.FirstOrDefault( m => m.Name == "Deep Brick Two" ) ?? brick;
			}

			// Delete the renamed brick: off the ring, the model keeps its look
			await MenuPick( brick, MenuDelete, "Delete" );
			var del = OpenWindows().OfType<ConfirmationDialog>().LastOrDefault( w => w.Title == "Delete material" );
			if ( del == null ) Note( "materials: Delete opened no box", fail: true );
			else
			{
				Shot( "deep-17-delete-box" );
				CheckClipping( "delete box" );
				del.GetOkButton().EmitSignal( BaseButton.SignalName.Pressed );
				await Wait( 0.5 );
				bool gone = !GeppettoDialState.Materials.Any( m => m.Name == brick.Name );
				bool tex = doc.Studio.MaterialLooks.Values.All( l => l.AlbedoMap == null || File.Exists( ProjectSettings.GlobalizePath( l.AlbedoMap ) ) || File.Exists( l.AlbedoMap ) );
				doc.Studio.MarkAllDirty(); doc.Refresh();
				await Wait( 0.3 );
				Note( $"materials: Delete -> off the ring={gone}, model's textures still found={tex}, coloured {ColouredSurfaces()}", fail: !gone || !tex || ColouredSurfaces() < 1 );
				Shot( "deep-18-deleted-model-keeps-look" );
			}
			GeppettoDialState.MatOpen = false;
			GeppettoDialState.Touch();
		}
		catch ( Exception e ) { Note( "materials: threw " + e, fail: true ); }
	}

	static void SetWhereProject( GeppettoMaterialDialog d )
	{
		// "Save in": this project, never ~/.local/share/poohdot/materials from a test
		foreach ( var o in d.FindChildren( "*", "OptionButton", true, false ).OfType<OptionButton>() )
			if ( o.ItemCount == 2 && !o.Disabled ) { o.Select( 1 ); o.EmitSignal( OptionButton.SignalName.ItemSelected, 1L ); }
	}

	async Task<GeppettoDialMaterial?> NewMaterialThroughBox( string name, Color colour, string? albedo )
	{
		var plus = GeppettoDialState.Materials.FirstOrDefault( m => m.IsNew );
		if ( plus == null ) { Note( "materials: no \"+\" ball on the ring", fail: true ); return null; }
		GeppettoDialState.AskNewMaterial();   // the "+" ball's click
		await Wait( 0.4 );
		var d = OpenWindows().OfType<GeppettoMaterialDialog>().LastOrDefault();
		if ( d == null ) { Note( "materials: \"+\" opened no box", fail: true ); return null; }
		d.FindChildren( "*", "LineEdit", true, false ).OfType<LineEdit>().First().Text = name;
		d.FindChildren( "*", "ColorPickerButton", true, false ).OfType<ColorPickerButton>().First().Color = colour;
		SetWhereProject( d );
		if ( albedo != null )   // what the file box's FileSelected hands the dialog
			typeof( GeppettoMaterialDialog ).GetField( "_albedo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance )!.SetValue( d, albedo );
		Shot( $"deep-new-material-{name}" );
		CheckClipping( "new material box" );
		d.GetOkButton().EmitSignal( BaseButton.SignalName.Pressed );
		await Wait( 0.5 );
		var m = GeppettoDialState.Materials.FirstOrDefault( x => x.Name == name );
		bool inProject = m?.Dir != null && m.Dir.StartsWith( ProjectSettings.GlobalizePath( "res://" ) );
		Note( $"materials: \"+\" saved {name} on the ring={m != null}, in this project={inProject}, texture={m?.Look.AlbedoMap != null}",
			fail: m == null || !inProject || ( albedo != null && m.Look.AlbedoMap == null ) );
		return m;
	}

	async Task MenuPick( GeppettoDialMaterial m, int id, string label )
	{
		GeppettoDialState.AskMaterialMenu( m, new Vector2( 900, 400 ) );   // the ball's right-click
		await Wait( 0.3 );
		if ( _matMenu == null ) { Note( $"materials: no right-click menu for {label}", fail: true ); return; }
		var slots = _matMenu.GetChildren().OfType<PopupMenu>().FirstOrDefault();
		bool inMenu = Enumerable.Range( 0, _matMenu.ItemCount ).Any( i => _matMenu.GetItemId( i ) == id && !_matMenu.IsItemDisabled( i ) )
			|| ( id >= MenuSlot0 && slots != null && Enumerable.Range( 0, slots.ItemCount ).Any( i => slots.GetItemId( i ) == id ) );
		if ( !inMenu ) Note( $"materials: \"{label}\" missing or greyed in {m.Name}'s menu", fail: true );
		if ( _runShot < 400 ) _matMenu.GetTexture()?.GetImage()?.SavePng( Path.Combine( _runOut, $"{++_runShot:D3}-deep-menu-{label.Trim( '…' ).Replace( ' ', '-' )}.png" ) );
		_matMenu.EmitSignal( PopupMenu.SignalName.IdPressed, (long)id );
		_matMenu.Hide();
		await Wait( 0.4 );
	}

	// =========================================================================================
	// 3. Every Model op, picked with real clicks
	// =========================================================================================

	async Task DeepModelOps( GeppettoDocument doc )
	{
		try
		{
			var history = DeepHistory();
			DialPick( "Model", "Face" );
			await Wait( 0.2 );
			int coloured = ColouredSurfaces();

			async Task Faces( int n = 1 )
			{
				DialPick( "Model", "Face" );
				await PickFrontFace( doc );
				for ( int i = 1; i < n; i++ ) await PickFrontFace( doc, i, shift: true );
			}
			async Task Edge() { DialPick( "Model", "Edge" ); await PickFrontEdge( doc ); }
			async Task Verts( int n )
			{
				DialPick( "Model", "Vertex" );
				int a = await PickFrontVertex( doc );
				for ( int i = 1; i < n; i++ ) await PickFrontVertex( doc, shift: true, want: v => Ses( doc )?.SelectedVertices.Contains( v ) == false );
			}

			// Select ops: change the selection, no mesh step
			async Task Sel( string tool, Func<Task> pick, Func<int, int, bool> ok )
			{
				await pick();
				int before = Ses( doc )?.SelectedFaces.Count ?? -1;
				string t0 = SelText( doc );
				DialPick( "Model", tool );
				await Wait( 0.2 );
				int after = Ses( doc )?.SelectedFaces.Count ?? -1;
				Note( $"Model > {tool}: {t0} -> {SelText( doc )}", fail: !ok( before, after ) );
				Shot( $"deep-op-{tool}" );
			}
			await Sel( "Grow", () => Faces(), ( a, b ) => b > a );
			await Sel( "Shrink", () => Faces( 2 ), ( a, b ) => b < a );
			await Sel( "Linked", () => Faces(), ( a, b ) => b == Ses( doc )!.Mesh.FaceCount );
			await Sel( "Invert", () => Faces(), ( a, b ) => b == Ses( doc )!.Mesh.FaceCount - 1 );

			// Mesh ops: the change, its undo, its redo, the colours
			async Task Op( string tool, Func<Task> pick, bool keep = false, bool undoAfter = true )
			{
				await pick();
				string sel = SelText( doc );
				string sig0 = MeshSig( doc );
				ulong v0 = history.GetVersion();
				DialPick( "Model", tool );
				await Wait( 0.35 );
				var pop = OpenWindows().OfType<GeppettoModelOpPopup>().LastOrDefault();
				bool typed = GeppettoModelEdit.Ops[tool].TakesNumber;
				if ( typed && pop == null ) { Note( $"Model > {tool}: no number box", fail: true ); return; }
				if ( pop != null )
				{
					Shot( $"deep-op-{tool}-box" );
					CheckClipping( $"{tool} box" );
					if ( pop.Size.Y > 400 ) Note( $"Model > {tool}: number box is {pop.Size.Y}px tall", fail: true );
					pop.GetOkButton().EmitSignal( BaseButton.SignalName.Pressed );
					await Wait( 0.4 );
				}
				var edit = GeppettoModelEdit.For( doc );
				string sig1 = MeshSig( doc );
				ulong v1 = history.GetVersion();
				int col = ColouredSurfaces();
				int want = Math.Min( coloured, Ses( doc )?.Mesh.Faces.Select( f => f.Material ).Distinct().Count() ?? coloured );
				bool changed = sig1 != sig0 && v1 > v0 && !edit.LastRefused;
				Note( $"Model > {tool} on {sel}: {( changed ? "changed" : "NO CHANGE" )} ({edit.LastMessage}), {Ses( doc )?.Mesh.FaceCount} faces, coloured {col} (want {want})",
					fail: !changed || col < want );
				Shot( $"deep-op-{tool}" );
				if ( !changed ) return;
				history.Undo(); await Wait( 0.25 );
				string sigU = MeshSig( doc );
				history.Redo(); await Wait( 0.25 );
				string sigR = MeshSig( doc );
				Note( $"Model > {tool}: undo puts it back={sigU == sig0}, redo={sigR == sig1}", fail: sigU != sig0 || sigR != sig1 );
				if ( !keep && undoAfter ) { history.Undo(); await Wait( 0.25 ); }
			}

			foreach ( var t in new[] { "Move", "Rotate", "Scale", "Shrink/Fatten" } ) await Op( t, () => Faces() );
			await Op( "Bend", () => Faces( 2 ) );
			await Op( "Edge slide", Edge );
			await Op( "Bevel", Edge );
			await Op( "Spin", Edge );
			await Op( "Pipe", Edge );
			await Op( "Crease", Edge, keep: true );
			await Op( "Clear crease", Edge );
			history.Undo(); await Wait( 0.25 );   // the crease
			await Op( "Mark seam", Edge );
			await Op( "Duplicate", () => Faces() );
			await Op( "Merge", () => Verts( 2 ) );
			await Op( "Dissolve", async () =>
			{
				DialPick( "Model", "Edge" );
				var m = Ses( doc )!.Mesh;
				var ef = m.BuildEdgeFaces();
				await PickFrontEdge( doc, want: k => ef.TryGetValue( k, out var fs ) && fs.Count == 2 && m.Faces[fs[0]].Material == m.Faces[fs[1]].Material );
			} );
			foreach ( var t in new[] { "Smooth" } ) await Op( t, async () => { await Faces(); DialPick( "Model", "Linked" ); await Wait( 0.2 ); } );
			foreach ( var t in new[] { "Flip", "Triangulate", "Project UVs", "Cylinder projection", "Sphere projection" } ) await Op( t, () => Faces() );

			// Delete a face, then Fill its hole from the rim (Alt-click = the rim's loop)
			await Op( "Delete", () => Faces(), keep: true );
			{
				var m = Ses( doc )!.Mesh;
				var open = OpenEdges( m );
				DialPick( "Model", "Edge" );
				var e = await PickFrontEdge( doc, alt: true, want: k => open.Contains( k ) );
				Note( $"Model > Fill: Alt-click on the hole's rim picked {( e == null ? "nothing" : SelText( doc ) )} ({open.Count} open edges)", fail: e == null );
				await Op( "Fill", () => Task.CompletedTask );
				history.Undo(); await Wait( 0.25 );   // the delete
			}

			// Delete two faces apart, then Bridge the two holes
			{
				await Faces();
				var m = Ses( doc )!.Mesh;
				int a = Ses( doc )!.SelectedFaces.First();
				var na = new HashSet<int>( m.Faces[a].Indices );
				int b = Enumerable.Range( 0, m.FaceCount ).FirstOrDefault( f => f != a && !m.Faces[f].Indices.Any( na.Contains ) );
				Ses( doc )!.SelectFace( b, MeshEditSession.Combine.Add );   // the far face is out of sight: picked like the Faces list would
				GeppettoModelEdit.For( doc ).SelectionChanged();
				await Op( "Delete", () => Task.CompletedTask, keep: true );
				m = Ses( doc )!.Mesh;
				var open = OpenEdges( m );
				DialPick( "Model", "Edge" );
				var e = await PickFrontEdge( doc, alt: true, want: k => open.Contains( k ) );
				var s = Ses( doc )!;
				// the second rim faces away from the camera: Shift+Alt on it through the session, as a click behind would
				int front = s.SelectedEdges.Count;
				foreach ( var k in open.Where( k => !s.SelectedEdges.Contains( k ) ).ToList() ) s.SelectEdge( k, MeshEditSession.Combine.Add );
				GeppettoModelEdit.For( doc ).SelectionChanged();
				Note( $"Model > Bridge: front rim by Alt-click = {front} edges; back rim (out of sight) added through the session" );
				Note( $"Model > Bridge: rims picked {SelText( doc )} of {open.Count} open edges", fail: e == null || s.SelectedEdges.Count < open.Count );
				await Op( "Bridge", () => Task.CompletedTask );
				history.Undo(); await Wait( 0.25 );   // the delete
			}

			// Cancel on a number box: nothing changes
			{
				await Faces();
				string sig0 = MeshSig( doc );
				ulong v0 = history.GetVersion();
				DialPick( "Model", "Extrude" );
				await Wait( 0.35 );
				var pop = OpenWindows().OfType<GeppettoModelOpPopup>().LastOrDefault();
				if ( pop == null ) Note( "Model > Extrude: no number box for Cancel", fail: true );
				else
				{
					pop.GetCancelButton().EmitSignal( BaseButton.SignalName.Pressed );
					await Wait( 0.35 );
					Note( $"Model > Extrude, Cancel: mesh unchanged={MeshSig( doc ) == sig0}, no undo step={history.GetVersion() == v0}", fail: MeshSig( doc ) != sig0 || history.GetVersion() != v0 );
				}
			}

			// Kept: Extrude, Inset and Bevel stay on the box for the paint and the save
			await Op( "Extrude", () => Faces(), keep: true );
			await Op( "Inset", () => Faces(), keep: true );
			await Op( "Bevel", Edge, keep: true );
			await Op( "Relax", async () => { await Faces(); DialPick( "Model", "Linked" ); await Wait( 0.2 ); }, keep: true );
			GeppettoTools.Activate( null );
			await Wait( 0.3 );
			Note( $"Model: after the kept edits {Ses( doc )?.Mesh.FaceCount} faces, coloured surfaces {ColouredSurfaces()} (was {coloured})", fail: ColouredSurfaces() < coloured );
			Shot( "deep-20-model-kept" );
		}
		catch ( Exception e ) { Note( "model ops: threw " + e, fail: true ); }
	}

	static HashSet<EdgeKey> OpenEdges( PolyMesh m )
	{
		var count = new Dictionary<EdgeKey, int>();
		foreach ( var f in m.Faces )
			for ( int i = 0; i < f.Indices.Length; i++ )
			{
				var k = new EdgeKey( f.Indices[i], f.Indices[( i + 1 ) % f.Indices.Length] );
				count[k] = count.GetValueOrDefault( k ) + 1;
			}
		return count.Where( kv => kv.Value == 1 ).Select( kv => kv.Key ).ToHashSet();
	}

	// =========================================================================================
	// 4. Paint: every brush, still showing after the brush is put down, saved
	// =========================================================================================

	async Task DeepPaint( GeppettoDocument doc, string scene )
	{
		try
		{
			var cam = DeepCam;
			var mesh = doc.Studio.Bodies.First( b => b.Visible && b.Mesh is { FaceCount: > 0 } ).Mesh!;
			int face = Enumerable.Range( 0, mesh.FaceCount ).Where( f => Facing( mesh, f ) ).OrderByDescending( f => mesh.FaceArea( mesh.Faces[f] ) ).First();
			var at = DeepScreen( mesh.FaceCentroid( mesh.Faces[face] ) );

			async Task Stroke( Vector2 from, float len )
			{
				var t = GeppettoTools.Active!;
				t.Input( cam, new InputEventMouseMotion { Position = from } );
				t.Input( cam, Mouse( true, from ) );
				for ( int i = 1; i <= 8; i++ )
					t.Input( cam, new InputEventMouseMotion { Position = from + new Vector2( i * len / 8, i * len / 16 ), Relative = new Vector2( len / 8, len / 16 ), ButtonMask = MouseButtonMask.Left } );
				t.Input( cam, Mouse( false, from + new Vector2( len, len / 2 ) ) );
				await Wait( 0.3 );
			}

			PaintBar? Bar() => EditorInterface.Singleton.GetBaseControl().FindChild( "GeppettoPaintBar", true, false ) as PaintBar;
			int strokes0 = 0;
			foreach ( var ch in new[] { "Colour", "Roughness", "Metal", "Normal" } )
			{
				DialPick( "Paint", ch );
				await Wait( 0.3 );
				if ( GeppettoTools.Active is not PaintBrushTool brush ) { Note( $"Paint > {ch}: no brush", fail: true ); continue; }
				if ( brush.Session == null ) { await ViewClick( at ); await Wait( 0.3 ); }
				if ( brush.Session == null ) { Note( $"Paint > {ch}: clicking the body opened no layer", fail: true ); continue; }
				if ( Bar() is { } bar )
				{
					var boxes = bar.GetChildren().OfType<SpinBox>().ToList();   // value, radius, strength
					if ( ch == "Colour" ) bar.GetChildren().OfType<ColorPickerButton>().First().Color = new Color( "2a9d4a" );
					else boxes[0].Value = ch == "Normal" ? 0.9 : 0.15;
				}
				int n0 = brush.Feature?.Strokes?.Count ?? 0;
				await Stroke( at - new Vector2( 40, 20 ), 80 );
				int n1 = brush.Feature?.Strokes?.Count ?? 0;
				Note( $"Paint > {ch}: stroke {n0} -> {n1} on layer {brush.Feature?.Name}", fail: n1 != n0 + 1 );
				Shot( $"deep-paint-{ch}" );
				CheckClipping( $"paint {ch}" );
				strokes0 = n1;
			}

			// Brush radius / strength / value: the dial focuses the bar; the typed number reaches the brush
			foreach ( var (tool, idx, v) in new[] { ("Brush radius", 1, 0.9), ("Paint strength", 2, 0.5), ("Channel value", 0, 0.4) } )
			{
				DialPick( "Paint", tool );
				await Wait( 0.2 );
				var brush = GeppettoTools.Active as PaintBrushTool;
				var boxes = Bar()?.GetChildren().OfType<SpinBox>().ToList();
				if ( brush == null || boxes == null ) { Note( $"Paint > {tool}: no brush or bar", fail: true ); continue; }
				bool focused = boxes[idx].GetLineEdit().HasFocus() || !boxes[idx].Visible;
				boxes[idx].Value = v;
				float got = idx switch { 1 => brush.Session?.Radius ?? -1, 2 => brush.Settings.Strength, _ => brush.Settings.Value };
				Note( $"Paint > {tool}: bar box focused={focused}, set {v} -> brush {got}", fail: !focused || MathF.Abs( got - (float)v ) > 1e-3f );
			}
			Shot( "deep-paint-bar" );

			// New Layer, Fill Layer
			foreach ( var (tool, fill) in new[] { ("New Layer", false), ("Fill Layer", true) } )
			{
				int layers = doc.Studio.Features.OfType<PaintFeature>().Count();
				DialPick( "Paint", tool );
				await Wait( 0.4 );
				if ( GeppettoTools.Active is PaintBrushTool b && b.PendingNewLayer != null ) { await ViewClick( at ); await Wait( 0.3 ); }
				var top = doc.Studio.Features.OfType<PaintFeature>().LastOrDefault();
				int now = doc.Studio.Features.OfType<PaintFeature>().Count();
				Note( $"Paint > {tool}: layers {layers} -> {now}, top is fill={top?.Fill.Value}", fail: now != layers + 1 || top?.Fill.Value != fill );
				if ( !fill && GeppettoTools.Active is PaintBrushTool nb && nb.Session != null )
				{
					await Stroke( at + new Vector2( -30, 10 ), 60 );
					Note( $"Paint > {tool}: stroke on the new layer ({nb.Feature?.Strokes?.Count ?? 0})", fail: ( nb.Feature?.Strokes?.Count ?? 0 ) < 1 );
				}
				Shot( $"deep-paint-{tool}" );
			}
			// The fill layer covers everything: take it out again so the strokes show in the shots
			var fillLayer = doc.Studio.Features.OfType<PaintFeature>().LastOrDefault( p => p.Fill.Value );
			if ( fillLayer != null ) DeepHistory().Undo();
			await Wait( 0.3 );

			// Put the brush down: the paint must stay on the Body
			GeppettoTools.Activate( null );
			await Wait( 0.5 );
			int painted = PaintedSurfaces();
			Note( $"Paint: brush put down, Body surfaces still showing the paint texture: {painted}", fail: painted < 1 );
			Shot( "deep-30-paint-after-brush" );

			// Save, close, reopen
			int layersSaved = doc.Studio.Features.OfType<PaintFeature>().Count();
			int strokesSaved = doc.Studio.Features.OfType<PaintFeature>().Sum( p => p.Strokes?.Count ?? 0 );
			int facesSaved = doc.Studio.Bodies.Sum( b => b.Mesh?.FaceCount ?? 0 );
			var slotsSaved = string.Join( ",", doc.Studio.MaterialNames.OrderBy( kv => kv.Key ).Select( kv => $"{kv.Key}={kv.Value}" ) );
			_SaveExternalData();
			EditorInterface.Singleton.SaveScene();
			await Wait( 1 );
			Note( $"save: dirty after save={doc.Session.IsDirty}", fail: doc.Session.IsDirty );
			GeppettoDocument.CloseAll();
			EditorInterface.Singleton.ReloadSceneFromPath( scene );
			await Wait( 2 );
			SelectCharacter();
			await Wait( 1 );
			var back = _character?.Doc;
			int layersBack = back?.Studio.Features.OfType<PaintFeature>().Count() ?? -1;
			int strokesBack = back?.Studio.Features.OfType<PaintFeature>().Sum( p => p.Strokes?.Count ?? 0 ) ?? -1;
			int facesBack = back?.Studio.Bodies.Sum( b => b.Mesh?.FaceCount ?? 0 ) ?? -1;
			var slotsBack = back == null ? "" : string.Join( ",", back.Studio.MaterialNames.OrderBy( kv => kv.Key ).Select( kv => $"{kv.Key}={kv.Value}" ) );
			Note( $"reopen: paint layers {layersBack}/{layersSaved}, strokes {strokesBack}/{strokesSaved}, faces {facesBack}/{facesSaved}, slots [{slotsBack}]",
				fail: layersBack != layersSaved || strokesBack != strokesSaved || facesBack != facesSaved || slotsBack != slotsSaved );
			int paintedBack = PaintedSurfaces();
			Note( $"reopen: Body surfaces showing the paint: {paintedBack}, coloured {ColouredSurfaces()}", fail: paintedBack < 1 );
			Shot( "deep-31-reopened" );
		}
		catch ( Exception e ) { Note( "paint: threw " + e, fail: true ); }
	}

	int PaintedSurfaces()
	{
		var root = EditorInterface.Singleton.GetEditedSceneRoot();
		var node = ( _character?.Root as Node ) ?? root;
		if ( node?.FindChild( "Body", true, false ) is not MeshInstance3D { Mesh: { } mesh } body ) return 0;
		int n = 0;
		for ( int i = 0; i < mesh.GetSurfaceCount(); i++ )
			if ( ( body.GetSurfaceOverrideMaterial( i ) ?? mesh.SurfaceGetMaterial( i ) ) is BaseMaterial3D { AlbedoTexture: { ResourceName: "paint_color" } } ) n++;
		return n;
	}
}
#endif
