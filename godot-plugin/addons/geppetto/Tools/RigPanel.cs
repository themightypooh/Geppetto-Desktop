#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Godot;

namespace Geppetto;

/// <summary>
/// The Rig workspace's side panel, ported from s&amp;box's EffigyRigPanel: Add Bone + placement row on
/// top, the bone tree (double-click renames, right-click for Rename / Parent to / Delete), and the
/// inspector for the picked bone: Assign Body and Mirror, head/tail, the parts pinned to it, softness,
/// and the rig problems. Every change goes through <see cref="GeppettoRig"/>, so it is one undo step on
/// the live document and lands in the .effigy on save.
/// Not ported: s&amp;box's Pose / Reset Pose (the Animate workspace poses the Skeleton3D here) and the
/// corrective-shape row.
/// </summary>
[Tool]
public partial class RigPanel : EditorDock
{
	static RigPanel? _instance;
	static bool _docked;

	GeppettoPlugin? _plugin;
	GeppettoCharacter? _character;
	GeppettoDocument? Doc => _character?.Doc;

	Button _addBone = null!, _assign = null!, _mirror = null!;
	OptionButton _placement = null!;
	CheckBox _snap = null!;
	Tree _tree = null!;
	PopupMenu _menu = null!, _parentMenu = null!;
	VBoxContainer _inspector = null!, _bodies = null!, _softFields = null!, _problems = null!;
	Label _name = null!, _bodiesHeader = null!, _problemsHeader = null!, _empty = null!;
	SpinBox[] _head = null!, _tail = null!, _soft = null!;
	CheckBox _softToggle = null!;
	bool _syncing;
	int _menuBone = -1;
	List<int> _menuParents = new();

	/// <summary>Shows the panel for <paramref name="c"/>, docking it on first use.</summary>
	public static void Open( GeppettoPlugin plugin, GeppettoCharacter c )
	{
		if ( _instance == null || !IsInstanceValid( _instance ) )
		{
			_instance = new RigPanel { _plugin = plugin };
			_docked = false;
			plugin.TreeExiting += () =>
			{
				if ( _instance != null && IsInstanceValid( _instance ) )
				{
					if ( _docked ) plugin.RemoveDock( _instance );
					_instance.QueueFree();
				}
				_instance = null;
				_docked = false;
			};
		}
		if ( !_docked ) { plugin.AddDock( _instance ); _docked = true; }
		_instance._character = c;
		_instance.Rebuild();
		_instance.MakeVisible();
	}

	/// <summary>The open panel's character, if any (the Add Bone tool's button text follows it).</summary>
	internal static RigPanel? Shown => _instance != null && IsInstanceValid( _instance ) ? _instance : null;
	public static void RefreshOpen() { if ( _instance != null && IsInstanceValid( _instance ) ) _instance.Rebuild(); }

	public RigPanel()
	{
		Title = "Rig";
		LayoutKey = "geppetto_rig";
		DefaultSlot = DockSlot.RightUl;
		IconName = "Skeleton3D";
		Closable = true;
		AvailableLayouts = DockLayout.Vertical | DockLayout.Floating;

		var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		AddChild( scroll );
		var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		col.AddThemeConstantOverride( "separation", 6 );
		scroll.AddChild( col );

		// Header: Add Bone, then the placement row (s&box BuildPlacementRow).
		_addBone = Wrap( new Button
		{
			Text = "Add Bone", ToggleMode = true, SizeFlagsHorizontal = SizeFlags.ExpandFill,
			TooltipText = "Click the model to place a bone. Click again to extend a chain from it. "
				+ "Select a bone first to branch a new chain from ITS tail instead of starting a new root.",
		} );
		_addBone.Pressed += ToggleAddBone;
		col.AddChild( _addBone );

		var row = new HFlowContainer();
		col.AddChild( row );
		_placement = new OptionButton
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			TooltipText = "Middle: the joint lands halfway through the material under the cursor, which "
				+ "is where a joint belongs. Surface: on the skin, for an attachment point.",
		};
		_placement.AddItem( "Middle" );
		_placement.AddItem( "Surface" );
		_placement.Selected = RigBoneTool.PlaceMiddle ? 0 : 1;
		_placement.ItemSelected += i => { RigBoneTool.PlaceMiddle = i == 0; GeppettoTools.Redraw(); };
		row.AddChild( _placement );
		_snap = new CheckBox
		{
			Text = "Y=0", ButtonPressed = RigBoneTool.SnapCentre,
			TooltipText = "Put every placed joint exactly on y = 0 — the plane Mirror reflects across, "
				+ "so a spine placed with this on mirrors cleanly.",
		};
		_snap.Toggled += on => { RigBoneTool.SnapCentre = on; GeppettoTools.Redraw(); };
		row.AddChild( _snap );

		// The bone tree.
		_tree = new Tree
		{
			HideRoot = true, SelectMode = Tree.SelectModeEnum.Row, AllowRmbSelect = true,
			CustomMinimumSize = new Vector2( 0, 180 ), SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		_tree.ItemSelected += OnTreeSelected;
		_tree.ItemActivated += () => { if ( _tree.GetSelected() is { } it ) BeginRename( it ); };
		_tree.ItemEdited += OnTreeEdited;
		_tree.ItemMouseSelected += ( pos, button ) => { if ( button == (long)MouseButton.Right ) OpenMenu( pos ); };
		col.AddChild( _tree );

		_empty = Wrapped( "No bones yet. Add Bone, then click the model to place the first joint." );
		col.AddChild( _empty );

		_menu = new PopupMenu();
		_menu.AddItem( "Rename", 0 );
		_parentMenu = new PopupMenu { Name = "ParentTo" };
		_menu.AddChild( _parentMenu );
		_menu.AddSubmenuNodeItem( "Parent to", _parentMenu, 1 );
		_menu.AddSeparator();
		_menu.AddItem( "Delete", 2 );
		_menu.IdPressed += OnMenu;
		_parentMenu.IndexPressed += OnParentPicked;
		AddChild( _menu );

		// Inspector for the picked bone.
		_inspector = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		col.AddChild( _inspector );
		_name = Wrapped( "" );
		_inspector.AddChild( _name );

		var acts = new HFlowContainer();
		_inspector.AddChild( acts );
		_assign = Wrap( new Button
		{
			Text = "Assign Body",
			TooltipText = "Pin parts to this bone: click bodies in the viewport. Clicking a pinned part again unpins it. "
				+ "Optional — unassigned bodies still skin to the nearest bone.",
		} );
		_assign.Pressed += () => RunTool( "Assign Body" );
		acts.AddChild( _assign );
		_mirror = Wrap( new Button
		{
			Text = "Mirror bones",
			TooltipText = "Mirror this bone (and everything under it) across Y=0 — this project's "
				+ "left/right axis — onto the same parent it already hangs from.",
		} );
		_mirror.Pressed += () => RunTool( "Mirror bones" );
		acts.AddChild( _mirror );

		_head = AddVecRow( "Head" );
		_tail = AddVecRow( "Tail" );

		_bodiesHeader = Wrapped( "" );
		_bodiesHeader.Modulate = new Color( 1, 1, 1, 0.6f );
		_inspector.AddChild( _bodiesHeader );
		_bodies = new VBoxContainer();
		_inspector.AddChild( _bodies );

		_softToggle = new CheckBox
		{
			Text = "Soft",
			TooltipText = "Let this bone lag and swing behind the pose instead of following it rigidly. "
				+ "The tail is simulated; the head always stays where its parent put it.",
		};
		_softToggle.Toggled += on => { if ( !_syncing && Doc is { } d ) GeppettoRig.SetSoft( d, GeppettoRig.SelectedIndex( d ), on ); };
		_inspector.AddChild( _softToggle );
		_softFields = new VBoxContainer();
		_inspector.AddChild( _softFields );
		_soft = new[]
		{
			AddSoftField( "Stiffness", 0, 0, 2000, 1,
				"How hard the bone snaps back to the pose, as an acceleration per unit of offset. Its "
				+ "frequency is sqrt(stiffness) radians a second - 40 is about one swing a second, 400 "
				+ "is a stiff twitch. Zero is a dead limb that only gravity moves." ),
			AddSoftField( "Damping", 1, 0, 1, 0.01,
				"How much of its speed the bone keeps after ONE SECOND, 0 to 1. At 1 a disturbed bone "
				+ "rings forever; at 0 it has no momentum at all. Limbs live low - a few percent - "
				+ "because a second is a long time for a swinging arm." ),
			AddSoftField( "Weight", 2, 0, 10, 0.05,
				"How hard gravity pulls this bone's tail, as a multiple of the solve's gravity. Per bone, "
				+ "because a forearm and a coat tail hang differently." ),
			AddSoftField( "Cone", 3, 0, 180, 1,
				"The furthest the bone may stray from its animated direction, in degrees. This is the "
				+ "difference between soft and broken: without a limit a fast enough swing puts a "
				+ "forearm through the shoulder it hangs off, and stiffness cannot prevent it - "
				+ "stiffness is a rate, and a rate can be outrun. 180 means unlimited." ),
		};

		_problemsHeader = Wrapped( "" );
		_problemsHeader.Modulate = new Color( 1, 1, 1, 0.6f );
		col.AddChild( _problemsHeader );
		_problems = new VBoxContainer();
		col.AddChild( _problems );
	}

	public override void _EnterTree()
	{
		GeppettoDocument.AnyChanged += OnDocChanged;
		GeppettoRig.SelectionChanged += OnSelectionChanged;
		GeppettoTools.ActiveChanged += SyncAddBone;
	}

	public override void _ExitTree()
	{
		GeppettoDocument.AnyChanged -= OnDocChanged;
		GeppettoRig.SelectionChanged -= OnSelectionChanged;
		GeppettoTools.ActiveChanged -= SyncAddBone;
	}

	void OnDocChanged( GeppettoDocument d ) { if ( d == Doc ) Rebuild(); }
	void OnSelectionChanged( GeppettoDocument d ) { if ( d == Doc ) Rebuild(); }

	// ---- widgets -------------------------------------------------------------------------------

	/// <summary>Buttons never clip: the text wraps inside the button and the button grows to fit.</summary>
	static Button Wrap( Button b )
	{
		b.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		b.CustomMinimumSize = new Vector2( 90, 0 );
		return b;
	}

	static Label Wrapped( string text ) => new()
	{
		Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill,
		CustomMinimumSize = new Vector2( 60, 0 ),
	};

	SpinBox[] AddVecRow( string label )
	{
		var row = new HFlowContainer();
		row.AddChild( new Label { Text = label, CustomMinimumSize = new Vector2( 40, 0 ) } );
		var boxes = new SpinBox[3];
		for ( int i = 0; i < 3; i++ )
		{
			var sb = new SpinBox
			{
				MinValue = -10000, MaxValue = 10000, Step = 0.01, AllowGreater = true, AllowLesser = true,
				Prefix = "XYZ"[i].ToString(), CustomMinimumSize = new Vector2( 80, 0 ),
				TooltipText = $"{label} {"XYZ"[i]} in inches (Effigy model space, Z up)",
			};
			sb.ValueChanged += _ => OnHeadTailEdited();
			row.AddChild( sb );
			boxes[i] = sb;
		}
		_inspector.AddChild( row );
		return boxes;
	}

	SpinBox AddSoftField( string label, int which, double min, double max, double step, string tip )
	{
		var row = new HFlowContainer();
		row.AddChild( new Label { Text = label, TooltipText = tip, MouseFilter = MouseFilterEnum.Stop, CustomMinimumSize = new Vector2( 70, 0 ) } );
		var sb = new SpinBox { MinValue = min, MaxValue = max, Step = step, TooltipText = tip, CustomMinimumSize = new Vector2( 90, 0 ) };
		sb.ValueChanged += v => { if ( !_syncing && Doc is { } d ) GeppettoRig.SetSoftValue( d, GeppettoRig.SelectedIndex( d ), which, (float)v ); };
		row.AddChild( sb );
		_softFields.AddChild( row );
		return sb;
	}

	// ---- refresh -------------------------------------------------------------------------------

	void Rebuild()
	{
		if ( !IsInstanceValid( this ) ) return;
		_syncing = true;
		try { RebuildInner(); }
		finally { _syncing = false; }
	}

	void RebuildInner()
	{
		var doc = Doc;
		var rig = doc?.Studio.Rig;
		int sel = doc == null ? -1 : GeppettoRig.SelectedIndex( doc );
		SyncAddBone();

		_tree.Clear();
		var root = _tree.CreateItem();
		var items = new TreeItem?[rig?.Count ?? 0];
		if ( rig != null )
			for ( int i = 0; i < rig.Count; i++ )   // parents always come before children in the rig
			{
				var bone = rig.Bones[i];
				var parent = bone.Parent >= 0 && bone.Parent < i ? items[bone.Parent] ?? root : root;
				var it = _tree.CreateItem( parent );
				it.SetText( 0, bone.Name );
				it.SetTooltipText( 0, bone.Soft != null ? $"{bone.Name} (soft)" : bone.Name );
				it.SetTextOverrunBehavior( 0, TextServer.OverrunBehavior.TrimEllipsis );
				it.SetMetadata( 0, i );
				if ( bone.Soft != null ) it.SetCustomColor( 0, RigView.SoftColor );
				items[i] = it;
				if ( i == sel ) it.Select( 0 );
			}
		_empty.Visible = rig == null || rig.Count == 0;
		if ( rig == null ) _empty.Text = "Select a Geppetto character to edit its rig.";
		else _empty.Text = "No bones yet. Add Bone, then click the model to place the first joint.";

		bool has = rig != null && sel >= 0 && sel < rig.Count;
		_inspector.Visible = has;
		if ( has )
		{
			var bone = rig!.Bones[sel];
			_name.Text = bone.Parent >= 0 ? $"{bone.Name}  (child of {rig.Bones[bone.Parent].Name})" : $"{bone.Name}  (root)";
			var head = rig.HeadWorld( sel );
			var tail = rig.TailWorld( sel );
			for ( int k = 0; k < 3; k++ ) { _head[k].Value = Axis( head, k ); _tail[k].Value = Axis( tail, k ); }

			foreach ( var c in _bodies.GetChildren() ) c.QueueFree();
			var pinned = GeppettoRig.BodiesOnBone( doc!, bone.Name );
			_bodiesHeader.Text = pinned.Count == 0 ? "No parts pinned (they skin to the nearest bone)." : $"Parts pinned to {bone.Name}:";
			foreach ( var body in pinned )
			{
				var r = new HBoxContainer();
				r.AddChild( new Label
				{
					Text = body.Name, TooltipText = body.Name, SizeFlagsHorizontal = SizeFlags.ExpandFill,
					TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, MouseFilter = MouseFilterEnum.Stop,
					CustomMinimumSize = new Vector2( 40, 0 ),
				} );
				var un = new Button { Text = "Unassign", TooltipText = $"Unassign from '{bone.Name}'" };
				string id = body.Id;
				un.Pressed += () => { if ( Doc is { } d ) GeppettoRig.UnassignBody( d, id ); };
				r.AddChild( un );
				_bodies.AddChild( r );
			}

			_softToggle.ButtonPressed = bone.Soft != null;
			_softFields.Visible = bone.Soft != null;
			if ( bone.Soft is { } s )
			{
				_soft[0].Value = s.Stiffness; _soft[1].Value = s.Damping; _soft[2].Value = s.Weight; _soft[3].Value = s.MaxAngle;
			}
		}

		foreach ( var c in _problems.GetChildren() ) c.QueueFree();
		var problems = rig == null || rig.Count == 0 ? new List<RigProblem>()
			: RigDiagnostics.Check( rig, null, doc!.Studio.BodyBoneMap, doc.Studio.Bodies.Select( b => b.Id ).ToList() );
		_problemsHeader.Visible = problems.Count > 0;
		_problemsHeader.Text = problems.Count == 1 ? "1 rig problem" : $"{problems.Count} rig problems";
		foreach ( var p in problems )
		{
			var box = new VBoxContainer();
			var text = Wrapped( $"{( p.Severity == RigSeverity.Error ? "Error" : "Warning" )}: {p.Problem}" );
			text.TooltipText = $"{p.Cause}\n{p.Remedy}";
			text.MouseFilter = MouseFilterEnum.Stop;
			text.Modulate = p.Severity == RigSeverity.Error ? new Color( 1f, 0.55f, 0.5f ) : new Color( 1f, 0.85f, 0.45f );
			box.AddChild( text );
			var fix = Wrapped( p.Remedy );
			fix.Modulate = new Color( 1, 1, 1, 0.7f );
			box.AddChild( fix );
			if ( p.Bone >= 0 && p.Bone < rig!.Count )
			{
				int b = p.Bone;
				var go = Wrap( new Button { Text = $"Select {rig.Bones[b].Name}", TooltipText = $"Select '{rig.Bones[b].Name}'" } );
				go.Pressed += () => { if ( Doc is { } d ) GeppettoRig.Select( d, b ); };
				box.AddChild( go );
			}
			_problems.AddChild( box );
		}
	}

	static float Axis( Vec3 v, int k ) => k == 0 ? v.x : k == 1 ? v.y : v.z;

	void SyncAddBone()
	{
		if ( !IsInstanceValid( _addBone ) ) return;
		bool on = GeppettoTools.Active is RigBoneTool;
		_addBone.SetPressedNoSignal( on );
		var doc = Doc;
		int sel = doc == null ? -1 : GeppettoRig.SelectedIndex( doc );
		// s&box: the text teaches branching ("Branch from 'upper_arm'") instead of hiding it in a tooltip.
		_addBone.Text = on ? "Stop placing bones"
			: sel >= 0 && sel < doc!.Studio.Rig.Count ? $"Branch from '{doc.Studio.Rig.Bones[sel].Name}'" : "Add Bone";
	}

	// ---- actions -------------------------------------------------------------------------------

	void ToggleAddBone()
	{
		if ( _character == null || Doc == null ) { GeppettoToolContext.Warn( "Select a Geppetto character (an imported .effigy scene) first." ); SyncAddBone(); return; }
		if ( GeppettoTools.Active is RigBoneTool ) GeppettoTools.Activate( null );
		else GeppettoTools.Activate( new RigBoneTool( _character ) );
		SyncAddBone();
	}

	void RunTool( string tool )
	{
		if ( _plugin == null ) return;
		GeppettoTools.TryRun( new GeppettoToolContext( _plugin, _character, "Rig", tool ) );
	}

	void OnTreeSelected()
	{
		if ( _syncing || Doc is not { } d || _tree.GetSelected() is not { } it ) return;
		int i = it.GetMetadata( 0 ).AsInt32();
		if ( i != GeppettoRig.SelectedIndex( d ) ) Callable.From( () => GeppettoRig.Select( d, i ) ).CallDeferred();
	}

	void BeginRename( TreeItem it )
	{
		it.SetEditable( 0, true );
		_tree.EditSelected( true );
	}

	void OnTreeEdited()
	{
		if ( _syncing || Doc is not { } d || _tree.GetEdited() is not { } it ) return;
		it.SetEditable( 0, false );
		int i = it.GetMetadata( 0 ).AsInt32();
		string name = it.GetText( 0 ).Trim();
		Callable.From( () =>
		{
			if ( i < d.Studio.Rig.Count && name != d.Studio.Rig.Bones[i].Name ) GeppettoRig.RenameBone( d, i, name );
			Rebuild();   // puts the old name back when the rename was refused
		} ).CallDeferred();
	}

	void OpenMenu( Vector2 pos )
	{
		if ( Doc is not { } d || _tree.GetSelected() is not { } it ) return;
		_menuBone = it.GetMetadata( 0 ).AsInt32();
		var rig = d.Studio.Rig;
		if ( _menuBone < 0 || _menuBone >= rig.Count ) return;
		int current = rig.Bones[_menuBone].Parent;

		_parentMenu.Clear();
		_menuParents = new List<int> { -1 };
		_parentMenu.AddItem( "None (root)" );
		_parentMenu.SetItemDisabled( 0, current < 0 );
		var choices = GeppettoRig.ParentChoices( rig, _menuBone );
		if ( choices.Count > 0 ) { _parentMenu.AddSeparator(); _menuParents.Add( int.MinValue ); }
		foreach ( int p in choices )
		{
			_parentMenu.AddItem( rig.Bones[p].Name );
			_parentMenu.SetItemDisabled( _parentMenu.ItemCount - 1, p == current );
			_menuParents.Add( p );
		}
		_menu.ResetSize();
		_menu.Position = (Vector2I)( _tree.GetScreenPosition() + pos );
		_menu.Popup();
	}

	void OnMenu( long id )
	{
		if ( Doc is not { } d || _menuBone < 0 ) return;
		if ( id == 0 && _tree.GetSelected() is { } it ) BeginRename( it );
		else if ( id == 2 ) GeppettoRig.DeleteBone( d, _menuBone );
	}

	void OnParentPicked( long index )
	{
		if ( Doc is not { } d || _menuBone < 0 || index < 0 || index >= _menuParents.Count ) return;
		int p = _menuParents[(int)index];
		if ( p == int.MinValue ) return;
		GeppettoRig.Reparent( d, _menuBone, p );
	}

	void OnHeadTailEdited()
	{
		if ( _syncing || Doc is not { } d ) return;
		int sel = GeppettoRig.SelectedIndex( d );
		if ( sel < 0 ) return;
		var head = new Vec3( (float)_head[0].Value, (float)_head[1].Value, (float)_head[2].Value );
		var tail = new Vec3( (float)_tail[0].Value, (float)_tail[1].Value, (float)_tail[2].Value );
		GeppettoRig.SetHeadTail( d, sel, head, tail );
	}
}
#endif
