#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Effigy;
using Godot;

namespace Geppetto;

/// <summary>
/// The tool dial in Poohdot: a <see cref="GeppettoDial"/> over each 3D editor viewport, a "Geppetto"
/// toggle in the 3D toolbar, and the Animate workspace's Timeline tab in the bottom panel. The dial
/// shows while a Geppetto character (an imported .effigy scene) is selected or edited, or while the
/// toggle is on. Picked tools run the real bridges where they exist and log <c>TODO tool: …</c> otherwise.
/// </summary>
public partial class GeppettoPlugin
{
	const int AnimateWs = 5;
	readonly List<GeppettoDial> _dials = new();
	Button? _dialToggle;
	GeppettoTimeline? _timeline;
	bool _timelineDocked;
	GeppettoCharacter? _character;

	// Animate state: auto-key polls the pose; two-bone IK follows a Marker3D target.
	bool _autoKey;
	Xform[]? _autoKeyBaseline;
	double _autoKeyClock;
	Marker3D? _ikTarget;
	string? _ikEnd;
	Vector3 _ikLast;
	readonly Dictionary<string, BaseButton> _toolbarButtons = new();

	void SetupDial()
	{
		GeppettoDialState.NoSave = !string.IsNullOrEmpty( System.Environment.GetEnvironmentVariable( "GEPPETTO_DIAL_SHOTS" ) );
		GeppettoDialState.Load();

		for ( int i = 0; i < 4; i++ )
		{
			var surface = ViewportSurface( i );
			if ( surface == null ) continue;
			var dial = new GeppettoDial { Name = "GeppettoDial", Viewport3D = EditorInterface.Singleton.GetEditorViewport3D( i ), Visible = false };
			surface.AddChild( dial );
			_dials.Add( dial );
		}

		_dialToggle = new Button { Text = "Geppetto", ToggleMode = true, Flat = true, ButtonPressed = GeppettoDialState.ForceShow,
			TooltipText = "Show the Geppetto tool dial even when no Geppetto character is selected" };
		_dialToggle.Toggled += on => { GeppettoDialState.ForceShow = on; GeppettoDialState.Save(); RefreshDial(); };
		AddControlToContainer( CustomControlContainer.SpatialEditorMenu, _dialToggle );

		_timeline = new GeppettoTimeline();
		_timeline.FrameChanged += _ => _autoKeyBaseline = ReadPose();

		EditorInterface.Singleton.GetSelection().SelectionChanged += RefreshDial;
		SceneChanged += OnSceneChanged;
		GeppettoDialState.Changed += SyncTimeline;
		GeppettoDialState.ToolPicked += OnToolPicked;
		GeppettoDialState.MaterialPicked += OnMaterialPicked;
		SetProcess( true );
		RefreshDial();

		if ( GeppettoDialState.NoSave ) Callable.From( () => { _ = TakeShots(); } ).CallDeferred();
	}

	void TeardownDial()
	{
		EditorInterface.Singleton.GetSelection().SelectionChanged -= RefreshDial;
		SceneChanged -= OnSceneChanged;
		GeppettoDialState.Changed -= SyncTimeline;
		GeppettoDialState.ToolPicked -= OnToolPicked;
		GeppettoDialState.MaterialPicked -= OnMaterialPicked;
		foreach ( var d in _dials ) if ( IsInstanceValid( d ) ) d.QueueFree();
		_dials.Clear();
		if ( _dialToggle != null ) { RemoveControlFromContainer( CustomControlContainer.SpatialEditorMenu, _dialToggle ); _dialToggle.QueueFree(); _dialToggle = null; }
		if ( _timeline != null ) { if ( _timelineDocked ) RemoveDock( _timeline ); _timeline.QueueFree(); _timeline = null; _timelineDocked = false; }
	}

	/// <summary>The input surface of 3D viewport <paramref name="i"/>: the Node3DEditorViewport's full-rect
	/// Control that sits over its SubViewportContainer and feeds the camera.</summary>
	static Control? ViewportSurface( int i )
	{
		var vp = EditorInterface.Singleton.GetEditorViewport3D( i );
		var editorViewport = vp?.GetParent()?.GetParent();
		return editorViewport?.GetChildren().OfType<Control>().FirstOrDefault( c => c is not SubViewportContainer );
	}

	void OnSceneChanged( Node root ) => RefreshDial();

	void RefreshDial()
	{
		var was = _character;
		_character = GeppettoCharacter.Find( _character );
		if ( _character != was ) { _autoKeyBaseline = null; ClearIk(); }
		bool show = GeppettoDialState.ForceShow || _character != null;
		foreach ( var d in _dials ) if ( IsInstanceValid( d ) ) d.Visible = show;
		SyncTimeline();
	}

	/// <summary>The Timeline tab is docked while Animate is the dial's workspace and gone otherwise.</summary>
	void SyncTimeline()
	{
		if ( _timeline == null ) return;
		bool want = GeppettoDialState.Ws == AnimateWs && ( GeppettoDialState.ForceShow || _character != null );
		if ( want && !_timelineDocked )
		{
			AddDock( _timeline );
			_timelineDocked = true;
			_timeline.MakeVisible();
		}
		else if ( !want && _timelineDocked )
		{
			RemoveDock( _timeline );
			_timelineDocked = false;
		}
		if ( !want ) return;
		if ( _timeline.Character != _character )
		{
			_character?.EnsureClip();
			_timeline.Character = _character;
			_timeline.Refresh();
		}
	}

	// =========================================================================================
	// Tools
	// =========================================================================================

	void OnToolPicked( string ws, string tool )
	{
		string name = GeppettoDialData.Clean( tool );
		try
		{
			if ( !RunTool( ws, name ) ) GD.Print( $"[Geppetto] TODO tool: {ws} > {name}" );
		}
		catch ( Exception e ) { GD.PushError( $"[Geppetto] {ws} > {name} failed: {e.Message}" ); }
	}

	/// <summary>Runs a wired tool. False when the tool has no backend yet.</summary>
	bool RunTool( string ws, string tool )
	{
		var c = _character;
		switch ( ws, tool )
		{
			case ("Animate", "Move" or "Rotate" or "Scale"):
				if ( c?.Skeleton != null ) Select( c.Skeleton );
				return PressToolbar( "spatial_editor/tool_" + tool.ToLowerInvariant() );

			case ("Animate", "Key"):
				return KeyPose( all: false );

			case ("Animate", "Auto-Key"):
				_autoKey = !_autoKey;
				_autoKeyBaseline = ReadPose();
				GD.Print( $"[Geppetto] Auto-Key {( _autoKey ? "on" : "off" )}" );
				return true;

			case ("Animate", "Two-bone IK"):
				return AddIkTarget();

			case ("Animate", "Prop"): return AddSceneNode( new Node3D { Name = "Prop" } );
			case ("Animate", "Light"): return AddSceneNode( new OmniLight3D { Name = "Light", Position = new Vector3( 1, 2, 1 ) } );
			case ("Animate", "Camera"): return AddSceneNode( new Camera3D { Name = "Camera", Position = new Vector3( 0, 1.5f, 3 ) } );

			case ("Animate", "Export Animation…"):
				if ( c == null ) return Need();
				string glb = c.EffigyPath[..^".effigy".Length] + ".glb";
				var err = EffigyGlbExporter.ExportEffigy( ProjectSettings.GlobalizePath( c.EffigyPath ), ProjectSettings.GlobalizePath( glb ) );
				GD.Print( $"[Geppetto] Export Animation → {glb}: {err}" );
				return true;

			case ("Rig", "Rest"):
				if ( c?.Skeleton == null || c.Rig == null ) return Need();
				EffigyPoseBridge.ApplyRestPose( c.Rig, c.Skeleton );
				_autoKeyBaseline = ReadPose();
				GD.Print( "[Geppetto] Rest pose applied" );
				return true;

		}
		// Everything else lives in the per-workspace tool files (Tools/*.cs).
		return GeppettoTools.TryRun( new GeppettoToolContext( this, c, ws, tool ) );
	}

	bool Need()
	{
		GD.PushWarning( "[Geppetto] Select a Geppetto character (an imported .effigy scene) first." );
		return true;
	}

	static void Select( Node n )
	{
		var sel = EditorInterface.Singleton.GetSelection();
		sel.Clear();
		sel.AddNode( n );
	}

	/// <summary>Presses one of the 3D toolbar's mode buttons, found by its shortcut's name.</summary>
	bool PressToolbar( string shortcut )
	{
		if ( !_toolbarButtons.TryGetValue( shortcut, out var b ) || !IsInstanceValid( b ) )
		{
			b = FindButton( EditorInterface.Singleton.GetBaseControl(), shortcut )!;
			if ( b == null ) { GD.PushWarning( $"[Geppetto] No 3D toolbar button for {shortcut}" ); return true; }
			_toolbarButtons[shortcut] = b;
		}
		b.EmitSignal( BaseButton.SignalName.Pressed );
		return true;
	}

	static BaseButton? FindButton( Node n, string shortcut )
	{
		if ( n is BaseButton bb && bb.Shortcut?.ResourceName == shortcut ) return bb;
		foreach ( var ch in n.GetChildren() )
			if ( FindButton( ch, shortcut ) is { } hit ) return hit;
		return null;
	}

	Xform[]? ReadPose() =>
		_character is { Rig: { } rig, Skeleton: { } skel } ? EffigyPoseBridge.ReadLocalPose( rig, skel ) : null;

	int CurrentFrame => _timeline?.Frame ?? 0;

	/// <summary>Key K: keys the bones that differ from the clip at the playhead (all bones when
	/// <paramref name="all"/>), as one undo step.</summary>
	bool KeyPose( bool all )
	{
		var c = _character;
		if ( c?.Skeleton == null || c.Rig == null || !c.EnsureClip() || c.AutoKey == null ) return Need();
		int frame = CurrentFrame;
		int keyed;
		if ( all || c.Clip!.BoneTracks.All( t => t.Keys.Count == 0 ) )
		{
			c.AutoKey.KeyBones( c.Skeleton, c.BoneNames, frame );
			keyed = c.BoneNames.Count;
		}
		else
		{
			var clipPose = EffigyPoseBridge.SampleClip( c.Clip, c.Rig, frame / (double)c.Fps );
			keyed = c.AutoKey.KeyChangedBones( c.Skeleton, clipPose, frame ).Count;
		}
		GD.Print( $"[Geppetto] Keyed {keyed} bone(s) at frame {frame}" );
		_autoKeyBaseline = ReadPose();
		_timeline?.Refresh();
		return true;
	}

	bool AddIkTarget()
	{
		var c = _character;
		if ( c?.Skeleton == null || c.Rig == null ) return Need();
		_ikEnd = c.IkEndBone();
		if ( _ikEnd == null ) { GD.PushWarning( "[Geppetto] Two-bone IK needs a bone with a parent and a grandparent." ); return true; }
		ClearIk();
		var skel = c.Skeleton;
		var at = skel.GlobalTransform * skel.GetBoneGlobalPose( skel.FindBone( _ikEnd ) ).Origin;
		var target = new Marker3D { Name = $"IK {_ikEnd}" };
		AddSceneNode( target );
		target.GlobalPosition = at;
		_ikTarget = target;
		_ikLast = at;
		Select( target );
		GD.Print( $"[Geppetto] Two-bone IK on {_ikEnd}: move the '{target.Name}' marker" );
		return true;
	}

	void ClearIk() { _ikTarget = null; _ikEnd = null; }

	/// <summary>Adds a node under the edited scene's root as one undo step.</summary>
	bool AddSceneNode( Node node )
	{
		var root = EditorInterface.Singleton.GetEditedSceneRoot();
		if ( root == null ) { node.Free(); return Need(); }
		var ur = GetUndoRedo();
		ur.CreateAction( $"Geppetto: add {node.Name}", UndoRedo.MergeMode.Disable, root );
		ur.AddDoMethod( root, Node.MethodName.AddChild, node, true );
		ur.AddDoProperty( node, Node.PropertyName.Owner, root );
		ur.AddDoReference( node );
		ur.AddUndoMethod( root, Node.MethodName.RemoveChild, node );
		ur.CommitAction();
		return true;
	}

	void OnMaterialPicked( GeppettoDialMaterial m )
	{
		// Only Face Material applies a material; elsewhere the pick just becomes the current material.
		if ( GeppettoDialState.Workspace.Name != "CAD" || GeppettoDialData.Clean( GeppettoDialState.CurrentTool ) != "Face Material" ) return;
		// Colours only the faces picked with the Face Material tool (Tools/CadPick.cs).
		GeppettoTools.FaceMaterialPicked( m );
	}

	public override void _Process( double delta )
	{
		var c = _character;
		if ( c?.Skeleton == null || c.Rig == null || !IsInstanceValid( c.Skeleton ) ) return;

		// Two-bone IK follows its marker.
		if ( _ikTarget != null && IsInstanceValid( _ikTarget ) && _ikEnd != null && _ikTarget.IsInsideTree()
			&& _ikTarget.GlobalPosition.DistanceSquaredTo( _ikLast ) > 1e-8f )
		{
			_ikLast = _ikTarget.GlobalPosition;
			var target = EffigyIkBridge.SkeletonSpaceFromGlobal( c.Skeleton, _ikLast );
			var r = EffigyIkBridge.SolveTwoBone( c.Rig, c.Skeleton, _ikEnd, target, new Vector3( 0, 0, 1 ) );
			if ( r.Success && _autoKey && c.EnsureClip() )
			{
				c.AutoKey!.KeyIkChain( c.Skeleton, _ikEnd, 2, CurrentFrame );
				_autoKeyBaseline = ReadPose();
				_timeline?.Refresh();
			}
		}

		// Auto-key: key whatever bones changed since the last look.
		if ( !_autoKey || ( _autoKeyClock += delta ) < 0.25 ) return;
		_autoKeyClock = 0;
		if ( _autoKeyBaseline == null ) { _autoKeyBaseline = ReadPose(); return; }
		if ( !c.EnsureClip() ) return;
		var changed = c.AutoKey!.KeyChangedBones( c.Skeleton, _autoKeyBaseline, CurrentFrame );
		if ( changed.Count > 0 )
		{
			_autoKeyBaseline = ReadPose();
			_timeline?.Refresh();
		}
	}

	// =========================================================================================
	// Screenshots (GEPPETTO_DIAL_SHOTS=<dir>): opens the spike character, walks the dial through its
	// states, saves the editor window as PNGs and quits. Nothing is saved to the project.
	// =========================================================================================

	async Task TakeShots()
	{
		string dir = System.Environment.GetEnvironmentVariable( "GEPPETTO_DIAL_SHOTS" )!;
		System.IO.Directory.CreateDirectory( dir );
		var tree = GetTree();
		async Task Wait( double s ) => await ToSignal( tree.CreateTimer( s ), SceneTreeTimer.SignalName.Timeout );
		async Task Shot( string name )
		{
			await Wait( 0.9 );
			await ToSignal( RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw );
			var img = EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture().GetImage();
			string path = System.IO.Path.Combine( dir, name + ".png" );
			img.SavePng( path );
			GD.Print( $"[Geppetto] shot {path}" );
		}

		await Wait( 2 );
		// Reimport so the scene carries the importer's geppetto_effigy meta.
		EditorInterface.Singleton.GetResourceFilesystem().ReimportFiles( new[] { "res://spike/g1-spike.effigy" } );
		await Wait( 1 );
		EditorInterface.Singleton.OpenSceneFromPath( "res://spike/g1-spike.effigy", true );
		EditorInterface.Singleton.SetMainScreenEditor( "3D" );
		await Wait( 2 );
		RefreshDial();
		GD.Print( $"[Geppetto] shots: character={_character?.EffigyPath ?? "none"} bones={_character?.BoneNames.Count ?? 0}" );

		foreach ( var (ws, stage, label) in new[] { ( 0, 1, "cad" ), ( 3, 2, "rig" ) } )
		{
			GeppettoDialState.MatOpen = false;
			GeppettoDialState.Ws = ws; GeppettoDialState.Level = 0;
			GeppettoDialState.FanStart = Time.GetTicksMsec(); GeppettoDialState.Touch();
			await Shot( $"{label}-1-closed" );
			GeppettoDialState.OpenWorkspace( ws );
			GeppettoDialState.OpenStageTools( stage );
			await Shot( $"{label}-2-stage-open" );
			GeppettoDialState.ToggleMaterials();
			await Shot( $"{label}-3-material-ring" );
		}

		// Animate: the Timeline tab, with a few real keys made through the Key tool (in memory only).
		GeppettoDialState.MatOpen = false;
		GeppettoDialState.OpenWorkspace( AnimateWs );
		GeppettoDialState.OpenStageTools( 0 );
		await Wait( 0.5 );
		if ( _character?.Skeleton is { } skel && _timeline != null )
		{
			KeyPose( all: true );
			int bone = skel.GetBoneCount() > 1 ? 1 : 0;
			foreach ( var f in new[] { 15, 30, 45 } )
			{
				_timeline.SetFrame( f, scrub: false );
				skel.SetBonePoseRotation( bone, skel.GetBonePoseRotation( bone ) * new Quaternion( Vector3.Up, 0.4f ) );
				KeyPose( all: false );
			}
			_timeline.SetFrame( 30 );
		}
		await Shot( "animate-timeline" );
		GeppettoDialState.ToggleMaterials();
		await Shot( "animate-material-ring" );

		GD.Print( "[Geppetto] shots done" );
		tree.Quit();
	}
}
#endif
