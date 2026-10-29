#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

/// <summary>
/// Feature-tree edits, each one undo step through <see cref="GeppettoDocument.Edit"/>. Kept apart from the
/// dock so the headless test drives exactly what the right-click menu does.
/// </summary>
public static class GeppettoFeatureOps
{
	public static bool Rename( GeppettoDocument doc, Feature f, string name )
	{
		name = name.Trim();
		if ( name.Length == 0 ) { GeppettoToolContext.Say( "A feature needs a name - kept the old one." ); return false; }
		return doc.Edit( $"Geppetto: rename {f.TypeName}", s => ParametricEditService.RenameFeature( s, f.Id, name ) );
	}

	public static bool ToggleSuppressed( GeppettoDocument doc, Feature f ) =>
		doc.Edit( $"Geppetto: {( f.Suppressed ? "unsuppress" : "suppress" )} '{f.Name}'",
			s => ParametricEditService.SetFeatureSuppressed( s, f.Id, !f.Suppressed ) );

	public static bool ToggleVisible( GeppettoDocument doc, Feature f ) =>
		doc.Edit( $"Geppetto: {( f.Visible ? "hide" : "show" )} '{f.Name}'",
			s => ParametricEditService.SetFeatureVisible( s, f.Id, !f.Visible ) );

	public static bool Delete( GeppettoDocument doc, Feature f )
	{
		// A settings box open on this feature would write into a feature that is gone.
		foreach ( var d in OpenDialogs().Where( d => d.Feature == f ) ) d.Hide();
		return doc.Edit( $"Geppetto: delete '{f.Name}'", s => ParametricEditService.RemoveFeature( s, f.Id ) );
	}

	/// <summary>Moves a feature one place (-1 up, +1 down). Refuses at the ends, like s&amp;box.</summary>
	public static bool Move( GeppettoDocument doc, Feature f, int delta )
	{
		var list = doc.Studio.Features;
		int from = list.IndexOf( f ), to = from + delta;
		if ( from < 0 ) return false;
		if ( to < 0 ) { GeppettoToolContext.Say( $"'{f.Name}' is already first." ); return false; }
		if ( to >= list.Count ) { GeppettoToolContext.Say( $"'{f.Name}' is already last." ); return false; }
		return doc.Edit( $"Geppetto: move '{f.Name}' {( delta < 0 ? "up" : "down" )}", s => ParametricEditService.MoveFeature( s, from, to ) );
	}

	static IEnumerable<GeppettoFeatureDialog> OpenDialogs()
	{
		if ( !Engine.IsEditorHint() || EditorInterface.Singleton == null ) yield break;
		foreach ( var n in EditorInterface.Singleton.GetBaseControl().GetChildren() )
			if ( n is GeppettoFeatureDialog d && GodotObject.IsInstanceValid( d ) ) yield return d;
	}
}

/// <summary>
/// The Features dock: s&amp;box's feature tree (rows in build order, failing rows red, right-click Edit /
/// Rename / Suppress / Hide / Move / Delete, double-click edits) plus its Parts list, which is how a body
/// is picked for the CAD tools since Poohdot's viewport selects nodes, not Effigy bodies.
/// </summary>
[Tool]
public partial class GeppettoFeatureTree : EditorDock
{
	static GeppettoFeatureTree? _instance;

	/// <summary>Made by the plugin (<c>AddDock(GeppettoFeatureTree.Create())</c>).</summary>
	public static GeppettoFeatureTree Create() => _instance = new GeppettoFeatureTree();

	Tree _features = null!, _parts = null!;
	Label _header = null!;
	PopupMenu _menu = null!;
	GeppettoCharacter? _character;
	GeppettoDocument? Doc => _character?.Doc;
	bool _filling;

	// The window run drives the dock through these, the way clicks would.
	internal Tree FeatureRows => _features;
	internal Tree PartRows => _parts;
	internal PopupMenu RowMenu => _menu;

	enum Item { Edit, EditSketch, Rename, Suppress, Hide, MoveUp, MoveDown, Delete }

	public GeppettoFeatureTree()
	{
		Title = "Geppetto Features";
		LayoutKey = "GeppettoFeatures";
		DefaultSlot = DockSlot.RightUl;
		Name = "GeppettoFeatures";

		// A wrapped label with no width asks for one word per line, which made this dock ~1800 px tall and
		// pushed the whole editor off screen: labels get a width and two lines, and the dock scrolls.
		var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
		AddChild( scroll );
		var box = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
		scroll.AddChild( box );

		_header = Wrapped( "Select a Geppetto character in the scene." );
		box.AddChild( _header );

		box.AddChild( new Label { Text = "Features", ThemeTypeVariation = "HeaderSmall" } );
		_features = NewTree( Tree.SelectModeEnum.Single );
		_features.SizeFlagsStretchRatio = 2f;
		_features.ItemActivated += () => { if ( Selected() is { } f ) EditFeature( f ); };
		_features.ItemSelected += () => GeppettoCad.SelectedFeatureId = Selected()?.Id;
		_features.ItemMouseSelected += ( pos, button ) => { if ( button == (long)MouseButton.Right ) ShowMenu( pos ); };
		_features.ItemEdited += OnRenamed;
		box.AddChild( _features );

		var partsLabel = Wrapped( "Parts (pick the bodies a CAD tool works on; none = all)" );
		partsLabel.ThemeTypeVariation = "HeaderSmall";
		box.AddChild( partsLabel );
		_parts = NewTree( Tree.SelectModeEnum.Multi );
		_parts.MultiSelected += ( item, col, selected ) => { if ( !_filling ) CollectParts(); };
		box.AddChild( _parts );

		_menu = new PopupMenu();
		_menu.IdPressed += id => OnMenu( (Item)(int)id );
		AddChild( _menu );
	}

	/// <summary>Wraps to at most two lines, then trims with the full text as a tooltip.</summary>
	static Label Wrapped( string text ) => new()
	{
		Text = text, TooltipText = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, MaxLinesVisible = 2,
		TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, CustomMinimumSize = new Vector2( 140, 0 ),
		SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Pass,
	};

	static Tree NewTree( Tree.SelectModeEnum mode ) => new()
	{
		HideRoot = true, SelectMode = mode, SizeFlagsVertical = SizeFlags.ExpandFill,
		CustomMinimumSize = new Vector2( 0, 120 ), AllowRmbSelect = true,
	};

	public override void _EnterTree()
	{
		GeppettoDocument.AnyChanged += OnDocChanged;
		EditorInterface.Singleton.GetSelection().SelectionChanged += Reselect;
		Reselect();
	}

	public override void _ExitTree()
	{
		GeppettoDocument.AnyChanged -= OnDocChanged;
		if ( EditorInterface.Singleton?.GetSelection() is { } sel ) sel.SelectionChanged -= Reselect;
		if ( _instance == this ) _instance = null;
	}

	void OnDocChanged( GeppettoDocument doc )
	{
		if ( doc == Doc ) Fill();
	}

	void Reselect()
	{
		var found = GeppettoCharacter.Find( _character );
		if ( found?.Doc != Doc || found?.Root != _character?.Root )
		{
			_character = found;
			GeppettoCad.SelectedBodyIds.Clear();
			GeppettoCad.SelectedFeatureId = null;
		}
		Fill();
	}

	// =========================================================================================
	// Rows
	// =========================================================================================

	const int MaxName = 60;

	void Fill()
	{
		_filling = true;
		_features.Clear();
		_parts.Clear();
		var doc = Doc;
		if ( doc == null )
		{
			_header.Text = "Select a Geppetto character in the scene to see its features.";
			_header.TooltipText = _header.Text;
			_filling = false;
			return;
		}
		_header.Text = System.IO.Path.GetFileName( _character!.EffigyPath );
		_header.TooltipText = _character.EffigyPath;

		var errors = doc.LastReport?.Errors.ToLookup( e => e.FeatureId, e => e.Message );
		var warnings = doc.LastReport?.Warnings.ToLookup( e => e.FeatureId, e => e.Message );
		var root = _features.CreateItem();
		int index = 0;
		foreach ( var f in doc.Studio.Features )
		{
			var item = _features.CreateItem( root );
			string name = f.Name ?? f.TypeName;
			string label = $"{++index}. {name}";
			if ( !string.Equals( name, f.TypeName, StringComparison.Ordinal ) ) label += $"  ({f.TypeName})";
			if ( f.Suppressed ) label += "  - suppressed";
			if ( !f.Visible ) label += "  - hidden";
			var tip = label;
			if ( label.Length > MaxName ) label = label[..( MaxName - 1 )] + "…";
			item.SetText( 0, label );
			item.SetAutowrapMode( 0, TextServer.AutowrapMode.WordSmart );
			item.SetTextOverrunBehavior( 0, TextServer.OverrunBehavior.TrimEllipsis );
			item.SetMetadata( 0, f.Id );

			if ( errors?[f.Id].ToList() is { Count: > 0 } errs )
			{
				item.SetCustomColor( 0, new Color( 0.95f, 0.35f, 0.32f ) );
				tip += "\n\nError: " + string.Join( "\n", errs );
			}
			else if ( warnings?[f.Id].ToList() is { Count: > 0 } warns )
			{
				item.SetCustomColor( 0, new Color( 0.95f, 0.72f, 0.3f ) );
				tip += "\n\nWarning: " + string.Join( "\n", warns );
			}
			else if ( f.Suppressed || !f.Visible ) item.SetCustomColor( 0, new Color( 0.6f, 0.6f, 0.62f ) );
			item.SetTooltipText( 0, tip );
			if ( f.Id == GeppettoCad.SelectedFeatureId ) item.Select( 0 );
		}

		var proot = _parts.CreateItem();
		GeppettoCad.SelectedBodyIds.RemoveAll( id => doc.Studio.Bodies.All( b => b.Id != id ) );
		foreach ( var b in doc.Studio.Bodies )
		{
			var item = _parts.CreateItem( proot );
			string name = string.IsNullOrEmpty( b.Name ) ? b.Id : b.Name;
			var shown = name.Length > MaxName ? name[..( MaxName - 1 )] + "…" : name;
			item.SetText( 0, b.Visible ? shown : shown + "  - hidden" );
			item.SetAutowrapMode( 0, TextServer.AutowrapMode.WordSmart );
			item.SetTextOverrunBehavior( 0, TextServer.OverrunBehavior.TrimEllipsis );
			item.SetTooltipText( 0, $"{name}\nid: {b.Id}" );
			item.SetMetadata( 0, b.Id );
			if ( GeppettoCad.SelectedBodyIds.Contains( b.Id ) ) item.Select( 0 );
		}
		_filling = false;
	}

	void CollectParts()
	{
		GeppettoCad.SelectedBodyIds.Clear();
		for ( var item = _parts.GetNextSelected( null ); item != null; item = _parts.GetNextSelected( item ) )
			GeppettoCad.SelectedBodyIds.Add( item.GetMetadata( 0 ).AsString() );
	}

	Feature? Selected() =>
		_features.GetSelected() is { } item && Doc is { } doc ? doc.FeatureById( item.GetMetadata( 0 ).AsString() ) : null;

	// =========================================================================================
	// Actions
	// =========================================================================================

	void EditFeature( Feature f )
	{
		if ( Doc is not { } doc ) return;
		// A drawn sketch opens in the viewport, where its curves are; a script sketch (Curves text) and
		// its plane / offset still edit in the settings box (Edit... in the menu).
		if ( f is SketchFeature sk && CadSketch.CanEdit( sk ) && _character != null ) { EditSketch( sk ); return; }
		GeppettoFeatureDialog.ShowEdit( doc, f );
	}

	void EditSketch( SketchFeature f )
	{
		if ( Doc is { } doc && _character != null ) new CadSketch( doc ).StartEdit( _character, f );
	}

	void ShowMenu( Vector2 pos )
	{
		if ( Selected() is not { } f || Doc is not { } doc ) return;
		int at = doc.Studio.Features.IndexOf( f );
		_menu.Clear();
		_menu.AddItem( "Edit...", (int)Item.Edit );
		if ( f is SketchFeature sk && CadSketch.CanEdit( sk ) ) _menu.AddItem( "Edit sketch", (int)Item.EditSketch );
		_menu.AddItem( "Rename", (int)Item.Rename );
		_menu.AddItem( f.Suppressed ? "Unsuppress" : "Suppress", (int)Item.Suppress );
		_menu.AddItem( f.Visible ? "Hide" : "Show", (int)Item.Hide );
		_menu.AddSeparator();
		_menu.AddItem( "Move up", (int)Item.MoveUp );
		_menu.SetItemDisabled( _menu.GetItemIndex( (int)Item.MoveUp ), at <= 0 );
		_menu.AddItem( "Move down", (int)Item.MoveDown );
		_menu.SetItemDisabled( _menu.GetItemIndex( (int)Item.MoveDown ), at >= doc.Studio.Features.Count - 1 );
		_menu.AddSeparator();
		_menu.AddItem( "Delete", (int)Item.Delete );
		_menu.Position = (Vector2I)( _features.GetScreenPosition() + pos );
		_menu.ResetSize();
		_menu.Popup();
	}

	void OnMenu( Item item )
	{
		if ( Selected() is not { } f || Doc is not { } doc ) return;
		switch ( item )
		{
			case Item.Edit: GeppettoFeatureDialog.ShowEdit( doc, f ); break;
			case Item.EditSketch: if ( f is SketchFeature sk ) EditSketch( sk ); break;
			case Item.Rename:
				var row = _features.GetSelected();
				row.SetText( 0, f.Name ?? f.TypeName );
				row.SetEditable( 0, true );
				// The row menu is still closing; editing now loses the field to the menu's focus hand-back.
				Callable.From( () => _features.EditSelected( true ) ).CallDeferred();
				break;
			case Item.Suppress: GeppettoFeatureOps.ToggleSuppressed( doc, f ); break;
			case Item.Hide: GeppettoFeatureOps.ToggleVisible( doc, f ); break;
			case Item.MoveUp: GeppettoFeatureOps.Move( doc, f, -1 ); break;
			case Item.MoveDown: GeppettoFeatureOps.Move( doc, f, +1 ); break;
			case Item.Delete: GeppettoFeatureOps.Delete( doc, f ); break;
		}
	}

	void OnRenamed()
	{
		var row = _features.GetEdited();
		if ( row == null || Doc is not { } doc ) return;
		row.SetEditable( 0, false );
		if ( doc.FeatureById( row.GetMetadata( 0 ).AsString() ) is { } f && row.GetText( 0 ) != f.Name )
		{
			if ( !GeppettoFeatureOps.Rename( doc, f, row.GetText( 0 ) ) ) Fill();
		}
		else Fill();
	}
}
#endif
