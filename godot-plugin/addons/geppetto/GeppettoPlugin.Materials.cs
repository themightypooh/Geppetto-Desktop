#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

// The material ring's actions beyond picking: the "+" ball (new material), the right-click menu on a
// ball or recent (use on faces / a whole slot, edit, copy, rename, delete), and applying a pick to the
// Model workspace's selected faces.
public partial class GeppettoPlugin
{
	PopupMenu? _matMenu;
	GeppettoDialMaterial? _matMenuFor;
	readonly List<int> _matMenuSlots = new();

	const int MenuFaces = 1, MenuEdit = 2, MenuCopy = 3, MenuRename = 4, MenuDelete = 5, MenuSlot0 = 100;

	void SetupMaterials()
	{
		GeppettoDialState.NewMaterialAsked += OnNewMaterial;
		GeppettoDialState.MaterialMenuAsked += OnMaterialMenu;
	}

	void TeardownMaterials()
	{
		GeppettoDialState.NewMaterialAsked -= OnNewMaterial;
		GeppettoDialState.MaterialMenuAsked -= OnMaterialMenu;
		if ( _matMenu != null && IsInstanceValid( _matMenu ) ) _matMenu.QueueFree();
		_matMenu = null;
	}

	void OnNewMaterial() => GeppettoMaterialDialog.Open( null, GeppettoDialState.Current, Saved );

	/// <summary>A saved or edited material goes into the ring and becomes current (without applying it).</summary>
	static void Saved( GeppettoDialMaterial m )
	{
		GeppettoDialState.ReloadMaterials();
		GeppettoDialState.PickMaterial( GeppettoDialState.Materials.FirstOrDefault( x => x.Name == m.Name ) ?? m, remember: false );
	}

	void OnMaterialMenu( GeppettoDialMaterial m, Vector2 screen )
	{
		_matMenuFor = m;
		if ( _matMenu == null || !IsInstanceValid( _matMenu ) )
		{
			_matMenu = new PopupMenu();
			_matMenu.IdPressed += OnMaterialMenuId;
			EditorInterface.Singleton.GetBaseControl().AddChild( _matMenu );
		}
		var menu = _matMenu;
		menu.Clear( true );
		menu.AddSeparator( m.Name );

		var doc = _character?.Doc;
		bool faces = doc != null && PickedFaceCount( doc ) > 0;
		menu.AddItem( "Use on selected faces", MenuFaces );
		menu.SetItemDisabled( menu.GetItemIndex( MenuFaces ), !faces );
		if ( !faces ) menu.SetItemTooltip( menu.GetItemIndex( MenuFaces ), doc == null ? "Select a Geppetto character first" : "Select faces in Model, or click them with CAD › Face Material" );

		// Use on a whole slot: every face on that slot changes at once.
		var slots = new PopupMenu { Name = "slots" };
		slots.IdPressed += OnMaterialMenuId;
		_matMenuSlots.Clear();
		if ( doc != null )
			foreach ( int s in UsedSlots( doc.Studio ) )
			{
				_matMenuSlots.Add( s );
				slots.AddItem( $"{s}  {SlotLabel( doc.Studio, s )}", MenuSlot0 + s );
			}
		menu.AddChild( slots );
		menu.AddSubmenuNodeItem( "Use on whole slot", slots );
		if ( _matMenuSlots.Count == 0 ) menu.SetItemDisabled( menu.ItemCount - 1, true );

		menu.AddSeparator();
		if ( m.Custom ) menu.AddItem( "Edit…", MenuEdit );
		menu.AddItem( m.Custom ? "Copy as new…" : "Make a custom copy…", MenuCopy );
		if ( m.Custom )
		{
			menu.AddItem( "Rename…", MenuRename );
			menu.AddItem( "Delete", MenuDelete );
		}
		menu.ResetSize();
		menu.Position = (Vector2I)screen;
		menu.Popup();
	}

	void OnMaterialMenuId( long id )
	{
		if ( _matMenuFor is not { } m ) return;
		switch ( (int)id )
		{
			case MenuFaces: ApplyToPickedFaces( m ); break;
			case MenuEdit: GeppettoMaterialDialog.Open( m, null, Saved ); break;
			case MenuCopy: GeppettoMaterialDialog.Open( null, m, Saved ); break;
			case MenuRename: AskRename( m ); break;
			case MenuDelete: AskDelete( m ); break;
			default:
				if ( id >= MenuSlot0 && _character?.Doc is { } doc ) SetSlotLook( doc, (int)id - MenuSlot0, m );
				break;
		}
	}

	void AskRename( GeppettoDialMaterial m )
	{
		var d = new ConfirmationDialog { Title = "Rename material", OkButtonText = "Rename", Exclusive = false };
		var box = new VBoxContainer();
		var name = new LineEdit { Text = m.Name, CustomMinimumSize = new Vector2( 320, 0 ) };
		var err = new Label { Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2( 320, 0 ) };
		err.AddThemeColorOverride( "font_color", new Color( "ff6b6b" ) );
		box.AddChild( name ); box.AddChild( err );
		d.AddChild( box );
		d.RegisterTextEnter( name );
		d.Confirmed += () =>
		{
			try
			{
				var n = GeppettoMaterialLibrary.Rename( m, name.Text );
				Saved( n );
				GeppettoToolContext.Say( $"Renamed {m.Name} to {n.Name}." );
				d.QueueFree();
			}
			catch ( Exception e ) { err.Text = e.Message; err.Visible = true; d.CallDeferred( Window.MethodName.Popup ); }
		};
		d.Canceled += d.QueueFree;
		EditorInterface.Singleton.GetBaseControl().AddChild( d );
		d.PopupCentered();
		name.GrabFocus(); name.SelectAll();
	}

	void AskDelete( GeppettoDialMaterial m )
	{
		var d = new ConfirmationDialog { Title = "Delete material", OkButtonText = "Delete", Exclusive = false,
			DialogText = $"Delete {m.Name}?\nIt moves to the .trash folder beside it, so it can be brought back.\nCharacters already using it keep their look.", DialogAutowrap = true };
		d.Confirmed += () =>
		{
			try
			{
				var to = GeppettoMaterialLibrary.Delete( m );
				GeppettoDialState.ReloadMaterials();
				GeppettoToolContext.Say( $"Deleted {m.Name} (moved to {to})." );
			}
			catch ( Exception e ) { GD.PushWarning( $"[Geppetto] Couldn't delete {m.Name}: {e.Message}" ); }
			d.QueueFree();
		};
		d.Canceled += d.QueueFree;
		EditorInterface.Singleton.GetBaseControl().AddChild( d );
		d.PopupCentered( new Vector2I( 380, 0 ) );
	}

	// =========================================================================================
	// Applying a material
	// =========================================================================================

	void OnMaterialPicked( GeppettoDialMaterial m )
	{
		// Face Material in CAD: colours the faces picked with that tool (Tools/CadPick.cs).
		if ( GeppettoDialState.Workspace.Name == "CAD" && GeppettoDialData.Clean( GeppettoDialState.CurrentTool ) == "Face Material" )
		{
			GeppettoTools.FaceMaterialPicked( m );
			return;
		}
		// Model with faces selected: s&box's "material for these faces". Elsewhere the pick just becomes current.
		if ( GeppettoDialState.Workspace.Name == "Model" && _character?.Doc is { } doc && ModelFaces( doc ) > 0 )
			ApplyModelFaces( doc, m );
	}

	static int ModelFaces( GeppettoDocument doc )
	{
		var s = GeppettoModelEdit.For( doc ).Session;
		return s != null && s.Mode == EditElement.Face ? s.SelectedFaces.Count : 0;
	}

	static int PickedFaceCount( GeppettoDocument doc ) =>
		Math.Max( ModelFaces( doc ), GeppettoTools.FaceMaterialPickCount );

	/// <summary>The right-click "Use on selected faces": the Model selection, else the Face Material picks.</summary>
	void ApplyToPickedFaces( GeppettoDialMaterial m )
	{
		if ( _character?.Doc is not { } doc ) return;
		GeppettoDialState.PickMaterial( m, remember: false );
		if ( ModelFaces( doc ) > 0 ) ApplyModelFaces( doc, m );
		else GeppettoTools.FaceMaterialPicked( m );
	}

	/// <summary>Puts the Model selection's faces on a slot showing <paramref name="m"/> (a slot already
	/// showing that look is reused) as one undo step on the Mesh edit feature.</summary>
	static bool ApplyModelFaces( GeppettoDocument doc, GeppettoDialMaterial m )
	{
		var studio = doc.Studio;
		int slot = GeppettoTools.SlotForLook( studio, m.Name, m.Look );
		if ( slot < 0 ) { GeppettoToolContext.Say( "Material: all 63 material slots are in use." ); return false; }
		// an unused slot gets the look first; with no faces on it yet it changes nothing visible
		if ( !studio.MaterialLooks.ContainsKey( slot ) ) studio.MaterialLooks[slot] = m.Look.Clone();
		if ( !studio.MaterialNames.ContainsKey( slot ) ) studio.MaterialNames[slot] = m.Name;
		return GeppettoModelEdit.For( doc ).Run( $"Material {m.Name}", s => s.SetFaceMaterial( slot ) );
	}

	/// <summary>Every face on <paramref name="slot"/> shows <paramref name="m"/>: one undo step.</summary>
	static void SetSlotLook( GeppettoDocument doc, int slot, GeppettoDialMaterial m )
	{
		var studio = doc.Studio;
		var had = studio.MaterialLooks.TryGetValue( slot, out var old ) ? old : null;
		var hadName = studio.MaterialNames.TryGetValue( slot, out var on ) ? on : null;
		var look = m.Look.Clone();
		var cmd = new DelegateEditCommand( $"Slot {slot} material {m.Name}",
			execute: () => { studio.MaterialLooks[slot] = look; studio.MaterialNames[slot] = m.Name; studio.MarkAllDirty(); },
			undo: () =>
			{
				if ( had != null ) studio.MaterialLooks[slot] = had; else studio.MaterialLooks.Remove( slot );
				if ( hadName != null ) studio.MaterialNames[slot] = hadName; else studio.MaterialNames.Remove( slot );
				studio.MarkAllDirty();
			} );
		if ( doc.Execute( cmd, $"Geppetto: slot {slot} material {m.Name}" ) )
		{
			GeppettoDialState.PickMaterial( m, remember: true );
			GeppettoToolContext.Say( $"Slot {slot} now shows {m.Name}." );
		}
	}

	/// <summary>Slots faces actually sit on, plus slots with a look, lowest first.</summary>
	static List<int> UsedSlots( PartStudio studio )
	{
		var set = new HashSet<int>( FaceMaterialEdit.UsedSlots( studio ) );
		foreach ( var b in studio.Bodies )
			if ( b?.Mesh != null )
				foreach ( var f in b.Mesh.Faces ) set.Add( f.Material );
		set.UnionWith( studio.MaterialLooks.Keys );
		return set.OrderBy( s => s ).ToList();
	}

	static string SlotLabel( PartStudio studio, int slot )
	{
		if ( studio.MaterialNames.TryGetValue( slot, out var n ) && !string.IsNullOrWhiteSpace( n ) ) return n;
		if ( studio.MaterialLooks.TryGetValue( slot, out var l ) && !string.IsNullOrEmpty( l?.Preset ) ) return l.Preset!;
		return studio.NameForSlot( slot );
	}
}
#endif
