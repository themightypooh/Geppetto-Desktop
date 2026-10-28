#if TOOLS
#nullable enable
using System;
using System.IO;
using Godot;

namespace Geppetto;

/// <summary>
/// New / edit material box, opened from the ring's "+" ball or a ball's right-click menu: name, colour,
/// roughness, metal, an optional colour texture, and where to save it (the shared library every project
/// sees, or this project's materials folder). Saves through <see cref="GeppettoMaterialLibrary"/>.
/// </summary>
public partial class GeppettoMaterialDialog : ConfirmationDialog
{
	GeppettoDialMaterial? _editing;
	LineEdit _name = null!;
	ColorPickerButton _color = null!;
	HSlider _rough = null!, _metal = null!;
	SpinBox _roughBox = null!, _metalBox = null!;
	Label _image = null!, _error = null!;
	OptionButton _where = null!;
	string? _albedo;
	bool _clearAlbedo;
	Action<GeppettoDialMaterial>? _saved;

	/// <summary>Opens the box. <paramref name="edit"/> a saved material to change it in place;
	/// <paramref name="start"/> any material to copy its values into a new one.</summary>
	public static GeppettoMaterialDialog Open( GeppettoDialMaterial? edit, GeppettoDialMaterial? start, Action<GeppettoDialMaterial>? saved )
	{
		var d = new GeppettoMaterialDialog { _editing = edit, _saved = saved, Exclusive = false };
		d.Title = edit != null ? $"Edit material" : "New material";
		d.OkButtonText = "Save";
		d.Build( edit ?? start );
		EditorInterface.Singleton.GetBaseControl().AddChild( d );
		d.Confirmed += d.Commit;
		d.Canceled += d.QueueFree;
		d.CloseRequested += d.QueueFree;
		d.PopupCentered( new Vector2I( 440, 0 ) );
		d._name.GrabFocus();
		d._name.SelectAll();
		return d;
	}

	void Build( GeppettoDialMaterial? from )
	{
		var grid = new GridContainer { Columns = 2 };
		grid.AddThemeConstantOverride( "h_separation", 10 );
		grid.AddThemeConstantOverride( "v_separation", 8 );
		Label Caption( string t ) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center };

		_name = new LineEdit { Text = _editing?.Name ?? ( from != null && !from.IsNew ? from.Name + " copy" : "" ), PlaceholderText = "e.g. Worn leather", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		_name.TextSubmitted += _ => { if ( Validate() ) { Commit(); } };
		grid.AddChild( Caption( "Name" ) ); grid.AddChild( _name );

		_color = new ColorPickerButton { Color = from?.Color ?? new Color( 0.62f, 0.6f, 0.57f ), EditAlpha = false, CustomMinimumSize = new Vector2( 0, 28 ), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		grid.AddChild( Caption( "Colour" ) ); grid.AddChild( _color );

		( _rough, _roughBox ) = Slider( grid, Caption( "Roughness" ), from?.Rough ?? 0.5f );
		( _metal, _metalBox ) = Slider( grid, Caption( "Metal" ), from?.Metal ?? 0f );

		var imgRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		_image = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, ClipText = true, VerticalAlignment = VerticalAlignment.Center };
		var choose = new Button { Text = "Choose…", TooltipText = "A colour texture (PNG, JPG or WebP). It's copied into the material's folder." };
		var clear = new Button { Text = "Clear", TooltipText = "Use the colour only" };
		choose.Pressed += ChooseImage;
		clear.Pressed += () => { _albedo = null; _clearAlbedo = true; ShowImage( null ); };
		imgRow.AddChild( _image ); imgRow.AddChild( choose ); imgRow.AddChild( clear );
		grid.AddChild( Caption( "Texture" ) ); grid.AddChild( imgRow );
		ShowImage( from?.Look?.AlbedoMap );

		_where = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		_where.AddItem( "My library (every project)", 0 );
		_where.AddItem( "This project only", 1 );
		_where.Selected = _editing?.Source == "project" ? 1 : 0;
		_where.Disabled = _editing != null;
		_where.TooltipText = _editing != null ? "Saved where it already lives" : "My library is ~/.local/share/poohdot/materials; this project is res://materials";
		grid.AddChild( Caption( "Save in" ) ); grid.AddChild( _where );

		var box = new VBoxContainer();
		box.AddChild( grid );
		_error = new Label { Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2( 400, 0 ) };
		_error.AddThemeColorOverride( "font_color", new Color( "ff6b6b" ) );
		box.AddChild( _error );
		AddChild( box );
		GetOkButton().Disabled = false;
	}

	static (HSlider, SpinBox) Slider( GridContainer grid, Label caption, float value )
	{
		var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		var s = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.01, Value = value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
		var b = new SpinBox { MinValue = 0, MaxValue = 1, Step = 0.01, Value = value, CustomMinimumSize = new Vector2( 84, 0 ) };
		s.ValueChanged += v => b.SetValueNoSignal( v );
		b.ValueChanged += v => s.SetValueNoSignal( v );
		row.AddChild( s ); row.AddChild( b );
		grid.AddChild( caption ); grid.AddChild( row );
		return (s, b);
	}

	void ShowImage( string? path )
	{
		_image.Text = string.IsNullOrEmpty( path ) ? "None (colour only)" : Path.GetFileName( path );
		_image.TooltipText = path ?? "";
	}

	void ChooseImage()
	{
		var fd = new EditorFileDialog
		{
			FileMode = EditorFileDialog.FileModeEnum.OpenFile,
			Access = EditorFileDialog.AccessEnum.Filesystem,
			Title = "Choose a colour texture",
			Filters = new[] { "*.png, *.jpg, *.jpeg, *.webp ; Images" },
		};
		fd.FileSelected += p => { _albedo = p; _clearAlbedo = false; ShowImage( p ); fd.QueueFree(); };
		fd.Canceled += fd.QueueFree;
		AddChild( fd );
		fd.PopupCentered( new Vector2I( 900, 600 ) );
	}

	string Source => _editing?.Source ?? ( _where.Selected == 1 ? "project" : "user" );

	bool Validate()
	{
		string name = _name.Text.Trim();
		string? why = null;
		if ( name.Length == 0 ) why = "Give the material a name.";
		else if ( name.StartsWith( '#' ) ) why = "Names can't start with #, that's how picked colours are named.";
		else if ( GeppettoDialData.PresetMaterials().Exists( p => string.Equals( p.Name, name, StringComparison.OrdinalIgnoreCase ) ) ) why = $"{name} is a built-in material; pick another name.";
		else if ( GeppettoMaterialLibrary.Exists( name, Source ) && ( _editing?.Dir == null || GeppettoMaterialLibrary.Safe( name ) != Path.GetFileName( _editing.Dir ) ) )
			why = $"There's already a material called {name}.";
		_error.Text = why ?? "";
		_error.Visible = why != null;
		return why == null;
	}

	void Commit()
	{
		if ( !Validate() ) { CallDeferred( Window.MethodName.Popup ); return; } // keep the box open to fix it
		try
		{
			var m = GeppettoMaterialLibrary.Save( _name.Text, _color.Color, (float)_rough.Value, (float)_metal.Value, _albedo, _clearAlbedo, Source, _editing?.Dir );
			GeppettoToolContext.Say( $"Material {m.Name} saved{( m.Source == "project" ? " in this project" : " in your library" )}." );
			_saved?.Invoke( m );
			QueueFree();
		}
		catch ( Exception e )
		{
			_error.Text = e.Message; _error.Visible = true;
			CallDeferred( Window.MethodName.Popup );
		}
	}
}
#endif
