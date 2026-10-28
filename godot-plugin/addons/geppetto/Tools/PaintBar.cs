#if TOOLS
#nullable enable
using System;
using Effigy;
using Godot;

namespace Geppetto;

/// <summary>
/// Port of s&amp;box's EffigyPaintBar: one row of brush settings for the open Paint layer — channel, colour
/// swatch (Base Color) or Value box (every other channel), Radius, Strength (0.01..1), Falloff and the
/// Erase toggle (shows "Erasing" while on; Ctrl flips it for one stroke). s&amp;box floated it over the
/// viewport's top-left; here it sits in the 3D view's toolbar while the brush is up and leaves with it.
/// Every label sizes to its text and every number box widens to its value, so nothing clips.
/// </summary>
public partial class PaintBar : HBoxContainer
{
	const float MinStrength = 0.01f;

	readonly PaintBrushTool _tool;
	readonly OptionButton _channel = new();
	readonly ColorPickerButton _swatch = new();
	readonly Label _valueLabel = Dim( "Value" );
	readonly SpinBox _value = Number( 0f, 1f, 0.01f, "" );
	readonly SpinBox _radius = Number( 1e-4f, 1e6f, 0.001f, "u" );
	readonly SpinBox _strength = Number( MinStrength, 1f, 0.01f, "" );
	readonly OptionButton _falloff = new();
	readonly Button _erase = new() { Text = "Erase", ToggleMode = true };
	bool _refreshing;

	public PaintBar( PaintBrushTool tool )
	{
		_tool = tool;
		Name = "GeppettoPaintBar";
		AddThemeConstantOverride( "separation", 8 );

		_channel.TooltipText = "Channel — what the brush paints: the surface color, how rough or metallic it is, bumps (Height), or where this layer shows (Layer Mask).";
		foreach ( var ch in Enum.GetValues<PaintChannel>() ) _channel.AddItem( PaintChannels.Label( ch ), (int)ch );
		_channel.ItemSelected += i => { if ( !_refreshing ) _tool.SetChannel( (PaintChannel)_channel.GetItemId( (int)i ) ); };
		AddChild( _channel );

		_swatch.TooltipText = "Brush color. Click to pick.";
		_swatch.EditAlpha = true;
		_swatch.CustomMinimumSize = new Vector2( 28, 0 );
		_swatch.ColorChanged += c => { if ( !_refreshing ) _tool.SetColour( c ); };
		AddChild( _swatch );

		AddChild( _valueLabel );
		_value.TooltipText = "The value the brush paints on this channel, 0 to 1. Roughness: 0 shiny, 1 matte. Metallic: 1 is metal. Height: 0.5 level, higher raises. Layer Mask: 1 shows, 0 hides.";
		_value.ValueChanged += v => { if ( !_refreshing ) _tool.SetValue( (float)v ); };
		AddChild( _value );

		AddChild( Dim( "Radius" ) );
		_radius.TooltipText = "Brush radius, in model units. [ and ] shrink and grow it.";
		_radius.ValueChanged += v => { if ( !_refreshing ) _tool.SetRadius( (float)v ); };
		AddChild( _radius );

		AddChild( Dim( "Strength" ) );
		_strength.TooltipText = "Paint strength, 0.01 to 1.";
		_strength.ValueChanged += v => { if ( !_refreshing ) _tool.SetStrength( (float)v ); };
		AddChild( _strength );

		AddChild( Dim( "Falloff" ) );
		_falloff.TooltipText = "How the brush fades from its centre to its edge. Smooth is a soft dab; the others are harder or sharper.";
		foreach ( var f in Enum.GetValues<BrushFalloff>() ) _falloff.AddItem( f.ToString(), (int)f );
		_falloff.ItemSelected += i => { if ( !_refreshing ) _tool.SetFalloff( (BrushFalloff)_falloff.GetItemId( (int)i ) ); };
		AddChild( _falloff );

		_erase.TooltipText = "Erase (or hold Ctrl). A modifier nobody is told about is not a tool you can find.";
		_erase.Toggled += on => { if ( !_refreshing ) _tool.SetErasing( on ); };
		AddChild( _erase );
	}

	/// <summary>Pulls every value from the tool's open session (or its remembered settings).</summary>
	public void Refresh()
	{
		_refreshing = true;
		try
		{
			var s = _tool.Settings;
			_channel.Select( _channel.GetItemIndex( (int)s.Channel ) );
			_swatch.Color = new Color( s.R, s.G, s.B, s.A );
			bool colour = s.Channel == PaintChannel.Color;
			_swatch.Visible = colour;
			_valueLabel.Visible = !colour;
			_value.Visible = !colour;
			_value.SetValueNoSignal( s.Value );
			_radius.SetValueNoSignal( _tool.Session?.Radius ?? s.Radius ?? 0.25f );
			_strength.SetValueNoSignal( s.Strength );
			_falloff.Select( _falloff.GetItemIndex( (int)s.Falloff ) );
			_erase.SetPressedNoSignal( s.Erasing );
			_erase.Text = s.Erasing ? "Erasing" : "Erase";
		}
		finally { _refreshing = false; }
	}

	/// <summary>Puts the keyboard in one box (the dial's Brush radius / Paint strength / Channel value).</summary>
	public void Focus( string field )
	{
		var box = field switch { "radius" => _radius, "strength" => _strength, _ => _value };
		if ( !box.Visible ) { _swatch.GrabFocus(); return; }
		var line = box.GetLineEdit();
		line.GrabFocus();
		line.SelectAll();
	}

	internal static Label Dim( string text ) => new()
	{
		Text = text,
		Modulate = new Color( 1, 1, 1, 0.6f ),
		VerticalAlignment = VerticalAlignment.Center,
	};

	internal static SpinBox Number( float min, float max, float step, string suffix )
	{
		var box = new SpinBox
		{
			MinValue = min,
			MaxValue = max,
			Step = step,
			Suffix = suffix,
			AllowGreater = max >= 1e6f,
			CustomMinimumSize = new Vector2( 90, 0 ),
			SelectAllOnFocus = true,
		};
		// The box widens to its text instead of cutting it off (no clipped numbers).
		box.GetLineEdit().ExpandToTextLength = true;
		return box;
	}
}

/// <summary>
/// Port of s&amp;box's EffigyWeightBar: Radius, Strength, Falloff, Brush (Add / Subtract / Set / Smooth)
/// and the bone being painted. s&amp;box named the bone from the Rig tree's selection; Poohdot's Skeleton3D
/// has no selection a plugin can read, so the bone is a list here (Shift+click on the mesh also picks the
/// bone under the cursor).
/// </summary>
public partial class WeightBar : HBoxContainer
{
	readonly PaintWeightsTool _tool;
	readonly SpinBox _radius = PaintBar.Number( 1e-4f, 1e6f, 0.001f, "u" );
	readonly SpinBox _strength = PaintBar.Number( 0.01f, 1f, 0.01f, "" );
	readonly OptionButton _falloff = new();
	readonly OptionButton _kind = new();
	readonly OptionButton _bone = new();
	bool _refreshing;

	public WeightBar( PaintWeightsTool tool )
	{
		_tool = tool;
		Name = "GeppettoWeightBar";
		AddThemeConstantOverride( "separation", 8 );

		AddChild( PaintBar.Dim( "Radius" ) );
		_radius.TooltipText = "Brush radius, in model units. [ and ] shrink and grow it.";
		_radius.ValueChanged += v => { if ( !_refreshing ) _tool.SetRadius( (float)v ); };
		AddChild( _radius );

		AddChild( PaintBar.Dim( "Strength" ) );
		_strength.TooltipText = "Weight strength, 0.01 to 1.";
		_strength.ValueChanged += v => { if ( !_refreshing ) _tool.SetStrength( (float)v ); };
		AddChild( _strength );

		AddChild( PaintBar.Dim( "Falloff" ) );
		foreach ( var f in Enum.GetValues<BrushFalloff>() ) _falloff.AddItem( f.ToString(), (int)f );
		_falloff.ItemSelected += i => { if ( !_refreshing ) _tool.SetFalloff( (BrushFalloff)_falloff.GetItemId( (int)i ) ); };
		AddChild( _falloff );

		AddChild( PaintBar.Dim( "Brush" ) );
		foreach ( var k in Enum.GetValues<WeightBrushKind>() ) _kind.AddItem( k.ToString(), (int)k );
		_kind.TooltipText = "Add pulls weight onto the bone, Subtract takes it off, Set paints toward a fixed value, Smooth evens out neighbours.";
		_kind.ItemSelected += i => { if ( !_refreshing ) _tool.SetBrush( (WeightBrushKind)_kind.GetItemId( (int)i ) ); };
		AddChild( _kind );

		AddChild( PaintBar.Dim( "Bone" ) );
		_bone.TooltipText = "The bone the brush paints. Shift+click the mesh to pick the bone under the cursor. The heat map is a texture, not vertex colour.";
		_bone.ItemSelected += i => { if ( !_refreshing ) _tool.SetBone( _bone.GetItemId( (int)i ) ); };
		AddChild( _bone );
	}

	public void Refresh()
	{
		var s = _tool.Session;
		if ( s == null ) return;
		_refreshing = true;
		try
		{
			_radius.SetValueNoSignal( s.Radius );
			_strength.SetValueNoSignal( s.Strength );
			_falloff.Select( _falloff.GetItemIndex( (int)s.Falloff ) );
			_kind.Select( _kind.GetItemIndex( (int)s.Brush ) );
			if ( _bone.ItemCount != s.Skeleton.Count )
			{
				_bone.Clear();
				for ( int b = 0; b < s.Skeleton.Count; b++ ) _bone.AddItem( s.Skeleton.Bones[b].Name, b );
			}
			_bone.Select( s.Bone >= 0 ? _bone.GetItemIndex( s.Bone ) : -1 );
		}
		finally { _refreshing = false; }
	}
}
#endif
