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

// GEPPETTO_WINDOW_WS=RigAudit / ClothingAudit / AnimateAudit / NoModelAudit: the Rig, Clothing and Animate
// dial tools run the way Poo runs them. Nothing is selected before each tool (the tool must find the
// character itself), viewport clicks are real mouse events sent into the window (Input.ParseInputEvent at the
// viewport's place on screen, so they go through Godot's GUI and the plugin's viewport forwarding), and each
// step checks what the tool really did to the document, the scene or the files on disk.
public partial class GeppettoPlugin
{
	readonly List<(string Text, bool Warn)> _heard = new();
	bool _hearing;

	void Listen()
	{
		if ( !_hearing ) { GeppettoToolContext.Spoken += ( m, w ) => _heard.Add( (m, w) ); _hearing = true; }
		_heard.Clear();
	}

	string Heard => _heard.Count == 0 ? "(nothing)" : string.Join( " | ", _heard.Select( h => ( h.Warn ? "WARN " : "" ) + h.Text ) );
	bool HeardWarn( string part ) => _heard.Any( h => h.Warn && h.Text.Contains( part ) );
	bool HeardSay( string part ) => _heard.Any( h => !h.Warn && h.Text.Contains( part ) );

	static SubViewport Vp => EditorInterface.Singleton.GetEditorViewport3D( 0 );

	/// <summary>A point in the 3D viewport's own pixels, in the editor window's pixels.</summary>
	static Vector2 ToWindow( Vector2 local )
	{
		var vp = Vp;
		var box = (Control)vp.GetParent();
		var r = box.GetGlobalRect();
		return r.Position + local * ( r.Size / (Vector2)vp.Size );
	}

	async Task Frames( int n = 2 )
	{
		for ( int i = 0; i < n; i++ ) await ToSignal( GetTree(), SceneTree.SignalName.ProcessFrame );
	}

	static InputEventMouseMotion Motion( Vector2 w, MouseButtonMask mask = 0, Vector2 rel = default ) =>
		new() { Position = w, GlobalPosition = w, ButtonMask = mask, Relative = rel };

	static InputEventMouseButton Button( Vector2 w, bool down, MouseButton b = MouseButton.Left ) =>
		new()
		{
			Position = w, GlobalPosition = w, ButtonIndex = b, Pressed = down,
			ButtonMask = down ? ( b == MouseButton.Right ? MouseButtonMask.Right : MouseButtonMask.Left ) : 0,
		};

	/// <summary>A real click at a window point: move there, press, release.</summary>
	async Task ClickAt( Vector2 w, MouseButton b = MouseButton.Left )
	{
		Input.ParseInputEvent( Motion( w ) );
		await Frames();
		Input.ParseInputEvent( Button( w, true, b ) );
		await Frames();
		Input.ParseInputEvent( Button( w, false, b ) );
		await Frames( 3 );
	}

	Task ClickVp( Vector2 local ) => ClickAt( ToWindow( local ) );

	async Task DragVp( Vector2 from, Vector2 to, int steps = 10 )
	{
		var a = ToWindow( from );
		var b = ToWindow( to );
		Input.ParseInputEvent( Motion( a ) );
		await Frames();
		Input.ParseInputEvent( Button( a, true ) );
		await Frames();
		var last = a;
		for ( int i = 1; i <= steps; i++ )
		{
			var p = a.Lerp( b, i / (float)steps );
			Input.ParseInputEvent( Motion( p, MouseButtonMask.Left, p - last ) );
			last = p;
			await Frames();
		}
		Input.ParseInputEvent( Button( b, false ) );
		await Frames( 3 );
	}

	/// <summary>A real key press and release (with Ctrl/Shift), e.g. Esc or Ctrl+Z.</summary>
	async Task PressKey( Key k, bool ctrl = false, bool shift = false )
	{
		foreach ( bool down in new[] { true, false } )
		{
			Input.ParseInputEvent( new InputEventKey { Keycode = k, PhysicalKeycode = k, KeyLabel = k, Pressed = down, CtrlPressed = ctrl, ShiftPressed = shift } );
			await Frames();
		}
		await Frames( 2 );
	}

	static IEnumerable<Node> WalkAll( Node n )
	{
		yield return n;
		foreach ( var c in n.GetChildren( true ) )
			foreach ( var d in WalkAll( c ) ) yield return d;
	}

	/// <summary>Picks an item of the 3D viewport's View menu (Front View, Focus Selection), as a click would.</summary>
	static bool ViewMenu( string text )
	{
		foreach ( var root in new Node?[] { Vp.GetParent()?.GetParent(), EditorInterface.Singleton.GetBaseControl() } )
		{
			if ( root == null ) continue;
			foreach ( var pm in WalkAll( root ).OfType<PopupMenu>() )
				for ( int i = 0; i < pm.ItemCount; i++ )
					if ( pm.GetItemText( i ) == text )
					{
						pm.EmitSignal( PopupMenu.SignalName.IdPressed, pm.GetItemId( i ) );
						return true;
					}
		}
		return false;
	}

	/// <summary>Front view, focused on <paramref name="node"/>, then nothing selected again.</summary>
	async Task FrameOn( Node node )
	{
		var sel = EditorInterface.Singleton.GetSelection();
		sel.Clear();
		sel.AddNode( node );
		await Wait( 0.3 );
		bool front = ViewMenu( "Front View" );
		await Wait( 0.3 );
		bool focus = ViewMenu( "Focus Selection" );
		await Wait( 1.2 );
		sel.Clear();
		await Wait( 0.2 );
		Note( $"camera: front view {front}, focus {focus} on {node.Name}", fail: !front || !focus );
	}

	/// <summary>Nothing selected and no remembered character: the tool has to find the character itself.</summary>
	void Forget()
	{
		EditorInterface.Singleton.GetSelection().Clear();
		_character = null;
	}

	/// <summary>Runs a dial tool with nothing selected, as clicking it on the ring does.</summary>
	async Task<bool> Dial( string ws, string tool, double settle = 0.4 )
	{
		Forget();
		_heard.Clear();
		bool ran;
		try { ran = RunTool( ws, tool ); }
		catch ( Exception e ) { Note( $"{ws} > {tool}: threw {e.GetType().Name}: {e.Message}", fail: true ); return false; }
		await Wait( settle );
		if ( !ran ) Note( $"{ws} > {tool}: not wired", fail: true );
		CheckToasts( $"{ws} > {tool}" );
		return ran;
	}

	readonly HashSet<string> _toastsSeen = new();

	/// <summary>The editor's toasts cut a message longer than half the window with "…" (the full text only in
	/// a tooltip); every toast a tool raises must fit whole.</summary>
	void CheckToasts( string step )
	{
		var toaster = EditorInterface.Singleton.GetEditorToaster();
		if ( toaster == null ) return;
		foreach ( var l in WalkAll( toaster ).OfType<Label>() )
			if ( l.IsVisibleInTree() && l.TextOverrunBehavior == TextServer.OverrunBehavior.TrimEllipsis && _toastsSeen.Add( l.Text ) )
				Note( $"{step}: toast cut short: {l.Text}", fail: true );
	}

	static Vec3 Centroid( PolyMesh m )
	{
		double x = 0, y = 0, z = 0;
		foreach ( var p in m.Positions ) { x += p.x; y += p.y; z += p.z; }
		int n = Math.Max( 1, m.Positions.Count );
		return new Vec3( (float)( x / n ), (float)( y / n ), (float)( z / n ) );
	}

	/// <summary>A viewport pixel that lands on a body of <paramref name="c"/> (bodies tried in
	/// <paramref name="order"/>), and the body really under it.</summary>
	static (Vector2 Pos, Body Body)? BodyPixel( GeppettoCharacter c, Func<Body, bool>? want = null, Func<Body, float>? order = null )
	{
		var cam = Vp.GetCamera3D();
		var frame = RigView.Frame( c );
		var visible = c.Doc!.Studio.Bodies.Where( b => b.Visible && b.Mesh.Positions.Count > 0 ).ToList();
		var rect = new Rect2( Vector2.Zero, Vp.Size );
		foreach ( var b in order == null ? visible.OrderByDescending( b => b.Mesh.Positions.Count ) : visible.OrderBy( order ) )
		{
			if ( RigView.Screen( cam, frame, Centroid( b.Mesh ) ) is not { } s || !rect.HasPoint( s ) ) continue;
			var (o, d) = RigView.Ray( cam, s, frame );
			if ( MeshRaycast.Raycast( visible, o, d )?.Body is { } hit && ( want == null || want( hit ) ) ) return (s, hit);
		}
		return null;
	}

	/// <summary>A viewport pixel on bone <paramref name="i"/>'s drawn line that picks that bone.</summary>
	static Vector2? BonePixel( GeppettoCharacter c, int i )
	{
		var cam = Vp.GetCamera3D();
		var rig = c.Doc!.Studio.Rig;
		var world = SoftSolver.BindPose( rig );
		var frame = RigView.Frame( c );
		var head = world[i].Origin;
		var tail = head + world[i].Y.Normal * rig.Bones[i].Length;
		if ( RigView.Screen( cam, frame, head ) is not { } a || RigView.Screen( cam, frame, tail ) is not { } b ) return null;
		foreach ( float t in new[] { 0.5f, 0.35f, 0.65f, 0.2f, 0.8f } )
		{
			var p = a.Lerp( b, t );
			if ( RigRunNearest( cam, c, p ) == i ) return p;
		}
		return null;
	}

	static int RigRunNearest( Camera3D cam, GeppettoCharacter c, Vector2 p ) => RigBonePickTool.Nearest( cam, c, p );

	async Task OpenRunScene()
	{
		await Wait( 4 );
		string scene = System.Environment.GetEnvironmentVariable( "GEPPETTO_WINDOW_SCENE" ) ?? "res://test.tscn";
		EditorInterface.Singleton.OpenSceneFromPath( scene );
		await Wait( 3 );
		Listen();
		Forget();
		RefreshDial();
	}

	void OpenWs( string name )
	{
		GeppettoDialState.OpenWorkspace( Array.FindIndex( GeppettoDialData.Workspaces, w => w.Name == name ) );
		RefreshDial();
	}

	int SceneVersion()
	{
		var ur = GetUndoRedo();
		var root = EditorInterface.Singleton.GetEditedSceneRoot();
		return (int)ur.GetHistoryUndoRedo( ur.GetObjectHistoryId( root ) ).GetVersion();
	}

	/// <summary>The window's open feature box (Subdivide, Garment ...), OK-ed; false when none opened.</summary>
	async Task<GeppettoFeatureDialog?> OkFeatureBox( string step )
	{
		for ( int i = 0; i < 20; i++ )
		{
			if ( OpenWindows().OfType<GeppettoFeatureDialog>().LastOrDefault() is { } fd )
			{
				await Wait( 0.3 );
				Shot( $"{step}-box" );
				CheckClipping( $"{step} box" );
				fd.GetOkButton().EmitSignal( BaseButton.SignalName.Pressed );
				await Wait( 0.5 );
				return fd;
			}
			await Wait( 0.15 );
		}
		return null;
	}

	/// <summary>The Animate box (a ConfirmationDialog) with this title, if it is showing.</summary>
	static ConfirmationDialog? AnimateBox( string title ) =>
		OpenWindows().OfType<ConfirmationDialog>().LastOrDefault( d => d.Title == title );

	// =============================================================================================
	// Rig
	// =============================================================================================

	async Task RigAuditRun()
	{
		try
		{
			await OpenRunScene();
			OpenWs( "Rig" );
			var c0 = GeppettoCharacter.Find( null );
			Note( $"rig: character found with nothing selected = {c0?.EffigyPath ?? "none"}", fail: c0 == null );
			if ( c0?.Doc == null ) { Finish(); return; }
			await FrameOn( c0.Root );
			Shot( "rig-0-framed" );
			var doc = c0.Doc;
			var rig = doc.Studio.Rig;
			GeppettoRig.Select( doc, -1 );

			// ---- Add Bone: two real clicks place a bone; Esc stops; Ctrl+Z / Ctrl+Shift+Z undo and redo it.
			int n0 = rig.Count;
			await Dial( "Rig", "Add Bone" );
			var arm = BodyPixel( c0, order: b => -MathF.Abs( Centroid( b.Mesh ).y ) );   // the part furthest off centre
			Note( $"Add Bone: tool {GeppettoTools.Active?.Name}, aim at {arm?.Body.Name}", fail: GeppettoTools.Active is not RigBoneTool || arm == null );
			if ( arm == null ) { Finish(); return; }
			GeppettoRig.Select( doc, -1 );
			await ClickVp( arm.Value.Pos );
			await ClickVp( arm.Value.Pos + new Vector2( 0, 60 ) );
			await Wait( 0.4 );
			rig = doc.Studio.Rig;
			Shot( "rig-1-add-bone" );
			CheckClipping( "Add Bone" );
			Note( $"Add Bone: {n0} -> {rig.Count} bones ({Heard})", fail: rig.Count != n0 + 1 );
			await PressKey( Key.Escape );
			Note( $"Add Bone: Esc stops the tool (active {GeppettoTools.Active?.Name ?? "none"})", fail: GeppettoTools.Active != null );
			int added = rig.Count - 1;
			string addedName = rig.Count > n0 ? rig.Bones[added].Name : "";
			if ( GeppettoTools.Active != null ) GeppettoTools.Activate( null );

			await PressKey( Key.Z, ctrl: true );
			await Wait( 0.4 );
			Note( $"Ctrl+Z: {doc.Studio.Rig.Count} bones (want {n0})", fail: doc.Studio.Rig.Count != n0 );
			await PressKey( Key.Z, ctrl: true, shift: true );
			await Wait( 0.4 );
			Note( $"Ctrl+Shift+Z: {doc.Studio.Rig.Count} bones (want {n0 + 1})", fail: doc.Studio.Rig.Count != n0 + 1 );
			rig = doc.Studio.Rig;

			// ---- Mirror bones with nothing picked: click the new bone, its mirror appears.
			GeppettoRig.Select( doc, -1 );
			int before = rig.Count;
			await Dial( "Rig", "Mirror bones" );
			var bp = BonePixel( c0, added );
			Note( $"Mirror bones: tool {GeppettoTools.Active?.Name}, bone {addedName} on screen at {bp}", fail: GeppettoTools.Active is not RigBonePickTool || bp == null );
			Shot( "rig-2-mirror-pick" );
			if ( bp != null ) await ClickVp( bp.Value );
			await Wait( 0.4 );
			rig = doc.Studio.Rig;
			Note( $"Mirror bones: {before} -> {rig.Count} bones ({Heard})", fail: rig.Count != before + 1 );
			int mirrored = rig.Count - 1;

			// ---- Make Soft with nothing picked: click the bone, it turns soft.
			GeppettoRig.Select( doc, -1 );
			await Dial( "Rig", "Make Soft" );
			bp = BonePixel( c0, added );
			if ( bp != null ) await ClickVp( bp.Value );
			await Wait( 0.4 );
			rig = doc.Studio.Rig;
			Note( $"Make Soft: {addedName} soft = {rig.Bones[added].Soft != null}", fail: rig.Bones[added].Soft == null );

			// ---- Assign Body with nothing picked: click the bone, then a part.
			GeppettoRig.Select( doc, -1 );
			await Dial( "Rig", "Assign Body" );
			bp = BonePixel( c0, added );
			if ( bp != null ) await ClickVp( bp.Value );
			await Wait( 0.3 );
			Note( $"Assign Body: after the bone click the tool is {GeppettoTools.Active?.Name} ({GeppettoTools.Active?.GetType().Name})", fail: GeppettoTools.Active is not RigBodyPickTool );
			var part = BodyPixel( c0 );
			if ( part != null ) await ClickVp( part.Value.Pos );
			await Wait( 0.4 );
			var on = GeppettoRig.BodiesOnBone( doc, addedName ).Select( b => b.Id ).ToList();
			Shot( "rig-3-assign" );
			Note( $"Assign Body: {part?.Body.Name} on {addedName} = {part != null && on.Contains( part.Value.Body.Id )}", fail: part == null || !on.Contains( part.Value.Body.Id ) );
			await PressKey( Key.Escape );
			if ( GeppettoTools.Active != null ) GeppettoTools.Activate( null );

			// ---- Preview and Rest.
			await Dial( "Rig", "Preview", 1.0 );
			Note( $"Preview: tool {GeppettoTools.Active?.GetType().Name} ({Heard})", fail: GeppettoTools.Active is not RigSoftPreview || !HeardSay( "Soft preview on" ) );
			Shot( "rig-4-preview" );
			await Dial( "Rig", "Rest" );
			Note( $"Rest during preview: {Heard}", fail: !HeardSay( "back on their pose" ) );
			await Dial( "Rig", "Preview" );
			Note( $"Preview again turns it off: {Heard}", fail: GeppettoTools.Active != null || !HeardSay( "off" ) );
			if ( c0.Skeleton is { } skel && skel.GetBoneCount() > 0 )
			{
				skel.SetBonePoseRotation( 0, new Quaternion( Vector3.Up, 0.6f ) * skel.GetBoneRest( 0 ).Basis.GetRotationQuaternion() );
				await Dial( "Rig", "Rest" );
				var rot = skel.GetBonePoseRotation( 0 );
				var rest = skel.GetBoneRest( 0 ).Basis.GetRotationQuaternion();
				Note( $"Rest without preview puts the posed skeleton back ({rot.AngleTo( rest ):0.000} rad off, {Heard})", fail: rot.AngleTo( rest ) > 1e-3f );
			}

			// ---- Paint Weights: a real drag on a part paints into the weight layer.
			GeppettoRig.Select( doc, added );
			await Dial( "Rig", "Paint Weights", 0.6 );
			int painted0 = doc.Studio.WeightPaint?.Count ?? 0;
			var paintAt = BodyPixel( c0 );
			Note( $"Paint Weights: tool {GeppettoTools.Active?.Name} ({Heard})", fail: GeppettoTools.Active is not PaintWeightsTool || paintAt == null );
			if ( paintAt != null && GeppettoTools.Active is PaintWeightsTool )
			{
				await DragVp( paintAt.Value.Pos - new Vector2( 30, 0 ), paintAt.Value.Pos + new Vector2( 30, 0 ) );
				await Wait( 0.5 );
				Shot( "rig-5-paint-weights" );
				CheckClipping( "Paint Weights" );
				int painted1 = doc.Studio.WeightPaint?.Count ?? 0;
				Note( $"Paint Weights: painted vertices {painted0} -> {painted1}", fail: painted1 <= painted0 );
			}
			await PressKey( Key.Escape );
			if ( GeppettoTools.Active != null ) GeppettoTools.Activate( null );

			// ---- Subdivide / Remesh: click a part, OK the box, the feature builds; Ctrl+Z takes it away.
			foreach ( var tool in new[] { "Subdivide", "Remesh" } )
			{
				int f0 = doc.Studio.Features.Count;
				await Dial( "Rig", tool );
				var small = BodyPixel( c0, order: b => b.Mesh.Positions.Count );
				Note( $"{tool}: tool {GeppettoTools.Active?.Name}, part {small?.Body.Name}", fail: GeppettoTools.Active?.Name != tool || small == null );
				if ( small != null ) await ClickVp( small.Value.Pos );
				var box = await OkFeatureBox( $"rig-6-{tool}" );
				var last = doc.Studio.Features.LastOrDefault();
				Note( $"{tool}: box {box != null}, features {f0} -> {doc.Studio.Features.Count}, last {last?.TypeName} error={last?.Error ?? "none"}",
					fail: box == null || doc.Studio.Features.Count != f0 + 1 || last?.Error != null );
				if ( GeppettoTools.Active != null ) GeppettoTools.Activate( null );
				await PressKey( Key.Z, ctrl: true );
				await Wait( 0.6 );
				Note( $"{tool}: Ctrl+Z leaves {doc.Studio.Features.Count} features (want {f0})", fail: doc.Studio.Features.Count != f0 );
			}

			// ---- Check model / Wrinkle Maps say something Poo can read.
			await Dial( "Rig", "Check model" );
			Note( $"Check model: {Heard}", fail: _heard.Count == 0 );
			await Dial( "Rig", "Wrinkle Maps" );
			Note( $"Wrinkle Maps: {Heard}", fail: !HeardWarn( "Wrinkle Maps" ) );

			// ---- Rig panel: click a bone in the tree, right-click > Parent to, then move its head.
			if ( RigPanel.Shown is { } panel )
			{
				var tree = panel.RunTree;
				TreeItem? item = null;
				foreach ( var n in WalkTree( tree.GetRoot() ) ) if ( n.GetText( 0 ) == addedName ) item = n;
				if ( item != null )
				{
					tree.ScrollToItem( item );
					await Frames( 2 );
					var r = tree.GetItemAreaRect( item );
					var at = tree.GetGlobalRect().Position + r.Position + new Vector2( 30, r.Size.Y / 2 );
					await ClickAt( at );
					await Wait( 0.3 );
					Note( $"Rig panel: clicking {addedName} in the tree picks it (picked {GeppettoRig.SelectedIndex( doc )})", fail: GeppettoRig.SelectedIndex( doc ) != added );
					// the tree rebuilt on the pick; find the row again and right-click it
					foreach ( var n in WalkTree( tree.GetRoot() ) ) if ( n.GetText( 0 ) == addedName ) item = n;
					r = tree.GetItemAreaRect( item );
					at = tree.GetGlobalRect().Position + r.Position + new Vector2( 30, r.Size.Y / 2 );
					await ClickAt( at, MouseButton.Right );
					await Wait( 0.3 );
					var pm = panel.RunParentMenu;
					Note( $"Rig panel: right-click opens the bone menu = {panel.RunMenu.Visible}, Parent to has {pm.ItemCount} items", fail: !panel.RunMenu.Visible || pm.ItemCount < 2 );
					Shot( "rig-7-parent-menu" );
					int oldParent = doc.Studio.Rig.Bones[added].Parent;
					int idx = -1;
					for ( int i = 0; i < pm.ItemCount; i++ )
						if ( !pm.IsItemDisabled( i ) && !pm.IsItemSeparator( i ) && pm.GetItemText( i ) != addedName ) { idx = i; if ( i > 0 ) break; }
					string target = idx >= 0 ? pm.GetItemText( idx ) : "";
					if ( idx >= 0 ) pm.EmitSignal( PopupMenu.SignalName.IndexPressed, (long)idx );
					panel.RunMenu.Hide();
					await Wait( 0.4 );
					int np = doc.Studio.Rig.Bones[added].Parent;
					string nowParent = np < 0 ? "None (root)" : doc.Studio.Rig.Bones[np].Name;
					Note( $"Reparent: {addedName} parent {oldParent} -> {nowParent} (picked '{target}')", fail: idx < 0 || nowParent != target );

					// moving the bone: type a new head X in the panel
					GeppettoRig.Select( doc, added );
					await Wait( 0.3 );
					float hx = doc.Studio.Rig.HeadWorld( added ).x;
					panel.RunHead[0].Value = hx + 2;
					await Wait( 0.4 );
					float hx2 = doc.Studio.Rig.HeadWorld( added ).x;
					Shot( "rig-8-moved-head" );
					CheckClipping( "Rig panel" );
					Note( $"Move bone: head X {hx:0.##} -> {hx2:0.##}", fail: MathF.Abs( hx2 - ( hx + 2 ) ) > 0.01f );
				}
				else Note( $"Rig panel: no tree row for {addedName}", fail: true );
			}
			else Note( "Rig panel: not open", fail: true );

			// ---- Delete with nothing picked: click the mirrored bone, it goes.
			GeppettoRig.Select( doc, -1 );
			rig = doc.Studio.Rig;
			string mName = mirrored < rig.Count ? rig.Bones[mirrored].Name : "";
			await Dial( "Rig", "Delete" );
			bp = BonePixel( c0, mirrored );
			if ( bp != null ) await ClickVp( bp.Value );
			await Wait( 0.4 );
			rig = doc.Studio.Rig;
			Note( $"Delete: {mName} gone = {rig.Bones.All( b => b.Name != mName )}, {rig.Count} bones ({Heard})", fail: bp == null || rig.Bones.Any( b => b.Name == mName ) );

			// ---- Bone from Part: click a part, a bone is laid down it.
			GeppettoRig.Select( doc, -1 );
			before = doc.Studio.Rig.Count;
			await Dial( "Rig", "Bone from Part" );
			var bpart = BodyPixel( c0 );
			if ( bpart != null ) await ClickVp( bpart.Value.Pos );
			await Wait( 0.4 );
			Note( $"Bone from Part: {before} -> {doc.Studio.Rig.Count} bones from {bpart?.Body.Name} ({Heard})", fail: doc.Studio.Rig.Count != before + 1 );
			Shot( "rig-9-bone-from-part" );
			await PressKey( Key.Escape );
			if ( GeppettoTools.Active != null ) GeppettoTools.Activate( null );

			Note( $"rig: rebuild errors {doc.LastReport?.Errors.Count ?? 0}", fail: ( doc.LastReport?.Errors.Count ?? 0 ) > 0 );
			CheckEditorFits( "rig end" );
		}
		catch ( Exception e ) { Note( "rig run threw: " + e, fail: true ); }
		Finish();
	}

	static IEnumerable<TreeItem> WalkTree( TreeItem? it )
	{
		if ( it == null ) yield break;
		yield return it;
		for ( var ch = it.GetFirstChild(); ch != null; ch = ch.GetNext() )
			foreach ( var d in WalkTree( ch ) ) yield return d;
	}

	// =============================================================================================
	// Clothing
	// =============================================================================================

	async Task ClothingAuditRun()
	{
		try
		{
			await OpenRunScene();
			OpenWs( "Clothing" );
			var c0 = GeppettoCharacter.Find( null );
			if ( c0?.Doc == null ) { Note( "clothing: no character found", fail: true ); Finish(); return; }
			await FrameOn( c0.Root );
			var doc = c0.Doc;

			// Detail tools before there is a garment: refused, nothing added.
			foreach ( var tool in new[] { "Trim", "Fur", "Fabric", "Pocket", "Hardware", "Wear", "Glow" } )
			{
				int f0 = doc.Studio.Features.Count;
				await Dial( "Clothing", tool, 0.3 );
				bool box = OpenWindows().OfType<GeppettoFeatureDialog>().Any();
				foreach ( var w in OpenWindows().OfType<GeppettoFeatureDialog>().ToList() ) w.GetCancelButton().EmitSignal( BaseButton.SignalName.Pressed );
				Note( $"{tool} with no garment: refused ({Heard})", fail: box || doc.Studio.Features.Count != f0 || !_heard.Any( h => h.Warn ) );
			}
			await Dial( "Clothing", "Shape garment" );
			Note( $"Shape garment with no garment: {Heard}", fail: !HeardWarn( "Garment" ) );

			// Wearer: the file box opens; picking a file in it loads the wearer.
			int fw = doc.Studio.Features.Count;
			await Dial( "Clothing", "Wearer", 0.6 );
			var fd = OpenWindows().OfType<EditorFileDialog>().LastOrDefault();
			Shot( "cloth-1-wearer-box" );
			Note( $"Wearer: file box open = {fd != null}", fail: fd == null );
			if ( fd != null )
			{
				fd.EmitSignal( FileDialog.SignalName.FileSelected, "res://characters/mannequin.effigy" );
				await Wait( 1.5 );
				var wf = doc.Studio.Features.OfType<WearerFeature>().LastOrDefault();
				Note( $"Wearer: features {fw} -> {doc.Studio.Features.Count}, wearer error={wf?.Error ?? "none"} ({Heard})", fail: wf == null || wf.Error != null );
				// A wearer is a reference body for garments; drop it again so the garment goes on the character's own torso.
				if ( wf != null ) { await PressKey( Key.Z, ctrl: true ); await Wait( 0.8 ); }
				Note( $"Wearer: Ctrl+Z removes it ({doc.Studio.Features.Count} features, want {fw})", fail: doc.Studio.Features.Count != fw );
			}

			// Garment, then each detail tool: the box opens, OK builds a clean feature.
			foreach ( var tool in new[] { "Garment", "Trim", "Fur", "Fabric", "Pocket", "Hardware", "Wear", "Glow" } )
			{
				int f0 = doc.Studio.Features.Count;
				var t = DateTime.Now;
				await Dial( "Clothing", tool, 0.3 );
				var box = await OkFeatureBox( $"cloth-2-{tool}" );
				var last = doc.Studio.Features.LastOrDefault();
				Note( $"{tool}: {( DateTime.Now - t ).TotalSeconds:0.0}s, box {box != null}, features {f0} -> {doc.Studio.Features.Count}, {last?.TypeName} error={last?.Error ?? "none"} ({Heard})",
					fail: box == null || doc.Studio.Features.Count != f0 + 1 || last?.Error != null );
			}
			Shot( "cloth-3-dressed" );

			// Check answers fast.
			var t0 = DateTime.Now;
			await Dial( "Clothing", "Check", 0.05 );
			double secs = ( DateTime.Now - t0 ).TotalSeconds - 0.05;
			Note( $"Check: {secs:0.00}s, {ClothingCheck.Marks.Count} marks ({Heard})", fail: secs > 2.0 || _heard.Count == 0 );
			Shot( "cloth-4-check" );
			await Dial( "Clothing", "Push out", 0.5 );
			Note( $"Push out: {Heard}", fail: _heard.Count == 0 );
			await Dial( "Clothing", "Check", 0.2 );
			await Dial( "Clothing", "Clear marks" );
			Note( $"Clear marks: {ClothingCheck.Marks.Count} marks left ({Heard})", fail: ClothingCheck.Marks.Count != 0 || _heard.Count == 0 );

			// Shape garment: one garment, a Mesh edit goes on it.
			int fs = doc.Studio.Features.Count;
			await Dial( "Clothing", "Shape garment", 0.6 );
			Note( $"Shape garment: features {fs} -> {doc.Studio.Features.Count} ({Heard})", fail: doc.Studio.Features.Count != fs + 1 || doc.Studio.Features.Last().Error != null );

			// Make clothing / live cloth write their files.
			string dir = Path.Combine( Path.GetDirectoryName( doc.DiskPath )!, ClothingPublish.Folder );
			await Dial( "Clothing", "Make clothing", 1.0 );
			var glbs = Directory.Exists( dir ) ? Directory.GetFiles( dir, "*.glb" ) : Array.Empty<string>();
			var clothing = Directory.Exists( dir ) ? Directory.GetFiles( dir, "*.clothing" ) : Array.Empty<string>();
			Note( $"Make clothing: {glbs.Length} glb, {clothing.Length} .clothing in {dir} ({Heard})", fail: glbs.Length == 0 || clothing.Length == 0 || _heard.Any( h => h.Warn ) );
			await Dial( "Clothing", "Make live cloth", 1.0 );
			var cloth = Directory.Exists( dir ) ? Directory.GetFiles( dir, "*.cloth.json" ) : Array.Empty<string>();
			Note( $"Make live cloth: {cloth.Length} .cloth.json ({Heard})", fail: cloth.Length == 0 || _heard.Any( h => h.Warn ) );
			Shot( "cloth-5-done" );
			CheckClipping( "clothing end" );
			CheckEditorFits( "clothing end" );
		}
		catch ( Exception e ) { Note( "clothing run threw: " + e, fail: true ); }
		Finish();
	}

	// =============================================================================================
	// Animate
	// =============================================================================================

	async Task AnimateAuditRun()
	{
		try
		{
			await OpenRunScene();
			OpenWs( "Animate" );
			var c0 = GeppettoCharacter.Find( null );
			if ( c0?.Doc == null || c0.Skeleton == null ) { Note( "animate: no rigged character found", fail: true ); Finish(); return; }
			await FrameOn( c0.Root );
			var root = EditorInterface.Singleton.GetEditedSceneRoot();

			// Move / Rotate / Scale press the 3D toolbar's mode buttons with the skeleton picked.
			foreach ( var (tool, sc) in new[] { ("Move", "spatial_editor/tool_move"), ("Rotate", "spatial_editor/tool_rotate"), ("Scale", "spatial_editor/tool_scale") } )
			{
				await Dial( "Animate", tool, 0.3 );
				var b = FindButton( EditorInterface.Singleton.GetBaseControl(), sc );
				bool skelPicked = EditorInterface.Singleton.GetSelection().GetSelectedNodes().OfType<Skeleton3D>().Any();
				Note( $"{tool}: toolbar button pressed = {b?.ButtonPressed}, skeleton picked = {skelPicked} ({Heard})", fail: b?.ButtonPressed != true || !skelPicked );
			}
			Shot( "anim-1-rotate" );
			var c = _character ?? c0;
			var skel = c.Skeleton!;

			// Timeline: real clicks on the lanes move the playhead.
			var lanes = _timeline == null ? null : WalkAll( _timeline ).OfType<GeppettoTimeline.Lanes>().FirstOrDefault();
			Note( $"Timeline docked = {_timelineDocked}, lanes visible = {lanes?.IsVisibleInTree()}", fail: lanes == null || !lanes.IsVisibleInTree() );
			if ( lanes != null )
			{
				var lr = lanes.GetGlobalRect();
				await ClickAt( lr.Position + new Vector2( 300, 8 ) );
				int fa = GeppettoTimeline.CurrentFrame;
				await ClickAt( lr.Position + new Vector2( 420, 8 ) );
				int fb = GeppettoTimeline.CurrentFrame;
				Note( $"Timeline click: frames {fa} then {fb} (10 frames apart = {fb - fa == 10})", fail: fb - fa != 10 );
			}

			// Key: pose a bone (what the Rotate gizmo does), K keys it at the playhead.
			_timeline?.SetFrame( 0 );
			string bone = c.IkEndBone() ?? c.BoneNames.Last();
			int bi = skel.FindBone( bone );
			int pi = skel.GetBoneParent( bi );
			string parentBone = pi >= 0 ? skel.GetBoneName( pi ) : bone;
			int ki = pi >= 0 ? pi : bi;
			skel.SetBonePoseRotation( ki, new Quaternion( Vector3.Forward, 0.4f ) * skel.GetBonePoseRotation( ki ) );
			await Dial( "Animate", "Key", 0.3 );
			Note( $"Key: {parentBone} keyed at 0 = {c.KeyFrames( parentBone ).Contains( 0 )} ({Heard})", fail: !c.KeyFrames( parentBone ).Contains( 0 ) );

			// Auto-Key: on, move the playhead, pose, and the bone is keyed there by itself.
			await Dial( "Animate", "Auto-Key", 0.3 );
			Note( $"Auto-Key on = {_autoKey} ({Heard})", fail: !_autoKey );
			_timeline?.SetFrame( 20 );
			await Wait( 0.4 );
			skel.SetBonePoseRotation( ki, new Quaternion( Vector3.Forward, -0.5f ) * skel.GetBonePoseRotation( ki ) );
			await Wait( 0.8 );
			Note( $"Auto-Key: {parentBone} keyed at 20 = {c.KeyFrames( parentBone ).Contains( 20 )}", fail: !c.KeyFrames( parentBone ).Contains( 20 ) );
			await Dial( "Animate", "Auto-Key", 0.2 );
			Note( $"Auto-Key off = {!_autoKey}", fail: _autoKey );
			Shot( "anim-2-keys" );

			// Two-bone IK: a marker appears; moving it bends the chain.
			await Dial( "Animate", "Two-bone IK", 0.4 );
			var marker = root.GetChildren().OfType<Marker3D>().LastOrDefault( m => m.Name.ToString().StartsWith( "IK " ) );
			Note( $"Two-bone IK: marker {marker?.Name} ({Heard})", fail: marker == null );
			if ( marker != null )
			{
				var pose0 = skel.GetBonePoseRotation( ki );
				marker.GlobalPosition += new Vector3( 0.15f, 0.1f, 0.1f );
				await Wait( 0.5 );
				float moved = skel.GetBonePoseRotation( ki ).AngleTo( pose0 );
				Note( $"Two-bone IK: moving the marker turns {parentBone} by {moved:0.000} rad", fail: moved < 1e-3f );
				Shot( "anim-3-ik" );
			}

			// Joint limit: the box, then Set limit writes it into the take's .ctrlrig.
			await Dial( "Animate", "Joint limit", 0.4 );
			var jl = AnimateBox( "Joint limit" );
			Note( $"Joint limit: box open = {jl != null}", fail: jl == null );
			if ( jl != null )
			{
				Shot( "anim-4-joint-limit" );
				CheckClipping( "Joint limit box" );
				jl.GetOkButton().EmitSignal( BaseButton.SignalName.Pressed );
				await Wait( 0.4 );
				Note( $"Joint limit: limits in the take = {c.Clip?.Limits?.Count ?? 0} ({Heard})", fail: ( c.Clip?.Limits?.Count ?? 0 ) == 0 || !HeardSay( "Joint limit on" ) );
			}

			// Prop / Light / Camera add a node to the scene.
			foreach ( var (tool, type) in new[] { ("Prop", typeof( Node3D )), ("Light", typeof( OmniLight3D )), ("Camera", typeof( Camera3D )) } )
			{
				int n0 = root.GetChildCount();
				await Dial( "Animate", tool, 0.3 );
				var added = root.GetChildCount() > n0 ? root.GetChild( root.GetChildCount() - 1 ) : null;
				Note( $"{tool}: added {added?.Name} ({added?.GetType().Name}) ({Heard})", fail: added == null || added.GetType() != type );
			}

			// First Person View: a camera on the head bone, and the viewport looks through it.
			await Dial( "Animate", "First Person View", 0.8 );
			var fpv = skel.GetNodeOrNull<BoneAttachment3D>( AnimateTools.FirstPersonName );
			Shot( "anim-5-first-person" );
			Note( $"First Person View: attachment on {fpv?.BoneName} ({Heard})", fail: fpv == null );
			await Dial( "Animate", "First Person View", 0.4 );
			Note( $"First Person View again removes it = {skel.GetNodeOrNull( AnimateTools.FirstPersonName ) == null}", fail: skel.GetNodeOrNull( AnimateTools.FirstPersonName ) != null );

			// Spawn model: the box, a model path, Add event.
			int ev0 = AnimateTools.Events( c ).Count;
			await Dial( "Animate", "Spawn model", 0.4 );
			var sm = AnimateBox( "Spawn model" );
			Note( $"Spawn model: box open = {sm != null}", fail: sm == null );
			if ( sm != null )
			{
				if ( WalkAll( sm ).OfType<LineEdit>().FirstOrDefault( l => l.PlaceholderText.StartsWith( "res://" ) ) is { } path ) path.Text = "res://characters/brute.effigy";
				Shot( "anim-6-spawn" );
				CheckClipping( "Spawn model box" );
				sm.GetOkButton().EmitSignal( BaseButton.SignalName.Pressed );
				await Wait( 0.4 );
				Note( $"Spawn model: events {ev0} -> {AnimateTools.Events( c ).Count} ({Heard})", fail: AnimateTools.Events( c ).Count != ev0 + 1 );
			}

			// Morph event: give the model a shape key (as the Model workspace's Shape Key does), then the box.
			var doc = c.Doc!;
			var sk = new ShapeKeyFeature();
			sk.Key.Value = "smile";
			doc.AddFeature( sk );
			doc.AddFeature( new SimpleDeformFeature() );
			doc.AddFeature( new ShapeKeyEndFeature() );
			await Wait( 0.6 );
			ev0 = AnimateTools.Events( c ).Count;
			await Dial( "Animate", "Morph event", 0.4 );
			var me = AnimateBox( "Morph event" );
			Note( $"Morph event: shape keys {string.Join( ",", c.MorphNames() )}, box open = {me != null} ({Heard})", fail: me == null );
			if ( me != null )
			{
				Shot( "anim-7-morph" );
				me.GetOkButton().EmitSignal( BaseButton.SignalName.Pressed );
				await Wait( 0.4 );
				Note( $"Morph event: events {ev0} -> {AnimateTools.Events( c ).Count} ({Heard})", fail: AnimateTools.Events( c ).Count != ev0 + 1 );
			}

			// Shape keys: the timeline's Shape Keys button shows a slider per key.
			if ( _timeline != null && WalkAll( _timeline ).OfType<Button>().FirstOrDefault( b => b.Text == "Shape Keys" ) is { } skb )
			{
				await ClickAt( skb.GetGlobalRect().GetCenter() );
				await Wait( 0.4 );
				int sliders = WalkAll( _timeline ).OfType<HSlider>().Count( s => s.IsVisibleInTree() );
				Shot( "anim-8-shape-keys" );
				CheckClipping( "Shape Keys" );
				Note( $"Shape Keys button: pressed {skb.ButtonPressed}, {sliders} sliders for {c.MorphNames().Count} keys", fail: !skb.ButtonPressed || sliders != c.MorphNames().Count );
				await ClickAt( skb.GetGlobalRect().GetCenter() );
			}
			else Note( "Shape Keys button: not found", fail: true );

			// Export: the box, Export, the file is on disk.
			await Dial( "Animate", "Export Animation…", 0.4 );
			var ex = AnimateBox( "Export Animation" );
			Note( $"Export: box open = {ex != null}", fail: ex == null );
			if ( ex != null )
			{
				var file = WalkAll( ex ).OfType<LineEdit>().First();
				string glb = ProjectSettings.GlobalizePath( file.Text );
				if ( File.Exists( glb ) ) File.Delete( glb );
				Shot( "anim-9-export" );
				CheckClipping( "Export box" );
				ex.GetOkButton().EmitSignal( BaseButton.SignalName.Pressed );
				await Wait( 1.5 );
				long bytes = File.Exists( glb ) ? new FileInfo( glb ).Length : 0;
				Note( $"Export: {file.Text} {bytes} bytes ({Heard})", fail: bytes == 0 || !HeardSay( "Exported" ) );
			}

			// Ctrl+S saves the take: the .riganim on disk has the keys.
			string? clipPath = c.ClipDiskPath;
			var t0 = clipPath != null && File.Exists( clipPath ) ? File.GetLastWriteTimeUtc( clipPath ) : DateTime.MinValue;
			Forget();
			await PressKey( Key.S, ctrl: true );
			await Wait( 1.5 );
			bool saved = clipPath != null && File.Exists( clipPath ) && File.GetLastWriteTimeUtc( clipPath ) > t0;
			string text = saved ? File.ReadAllText( clipPath! ) : "";
			Note( $"Ctrl+S: {clipPath} saved = {saved}, has {parentBone} = {text.Contains( parentBone )}, has the morph event = {text.Contains( "smile" )}",
				fail: !saved || !text.Contains( parentBone ) || !text.Contains( "smile" ) );
			Shot( "anim-10-saved" );
			CheckEditorFits( "animate end" );
		}
		catch ( Exception e ) { Note( "animate run threw: " + e, fail: true ); }
		Finish();
	}

	// =============================================================================================
	// No model
	// =============================================================================================

	async Task NoModelAuditRun()
	{
		try
		{
			await OpenRunScene();
			Note( $"no model: character = {GeppettoCharacter.Find( null )?.EffigyPath ?? "none"}", fail: GeppettoCharacter.Find( null ) != null );
			foreach ( var wsName in new[] { "Rig", "Clothing", "Animate" } )
			{
				OpenWs( wsName );
				var ws = GeppettoDialData.Workspaces.First( w => w.Name == wsName );
				foreach ( var (_, tools) in ws.Stages )
					foreach ( var raw in tools )
					{
						string tool = GeppettoDialData.Clean( raw );
						await Dial( wsName, tool, 0.25 );
						bool box = OpenWindows().Any();
						foreach ( var w in OpenWindows().ToList() ) w.Hide();
						Note( $"{wsName} > {tool} with no model: {Heard}", fail: !HeardWarn( GeppettoToolContext.NoModel ) || box || GeppettoTools.Active != null || _character != null );
						if ( GeppettoTools.Active != null ) GeppettoTools.Activate( null );
					}
				Shot( $"nomodel-{wsName}" );
			}
			CheckClipping( "no model" );
		}
		catch ( Exception e ) { Note( "no-model run threw: " + e, fail: true ); }
		Finish();
	}
}
#endif
