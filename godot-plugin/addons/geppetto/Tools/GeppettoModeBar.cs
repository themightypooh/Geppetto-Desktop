#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Geppetto;

/// <summary>A tool that drives the mode bar itself: its own title, buttons, step line and help, and
/// real Done/Cancel. Other active tools get a plain bar: their name with Done (Enter) and Cancel (Esc).</summary>
public interface IGeppettoModeTool
{
	/// <summary>False while the tool is only waiting (Sketch choosing a plane): no frame, no bar.</summary>
	bool InMode { get; }
	string ModeTag { get; }
	string ModeTitle { get; }
	string ModeIcon { get; }
	Color ModeColour { get; }
	IReadOnlyList<GeppettoModeBar.ModeButton> ModeButtons { get; }
	/// <summary>One short line under the bar: what the next click does.</summary>
	string ModeStep { get; }
	/// <summary>The full hotkey list, shown on the "?" hover.</summary>
	string ModeHelp { get; }
	string DoneLabel => "Finish";
	void ModeDone();
	void ModeCancel();
}

/// <summary>
/// What makes a running tool unmistakable: a 4px frame round the 3D viewport in the workspace's colour,
/// a small tag top-left, and a rounded mode bar at top centre (icon + title, the tool's buttons, then
/// Done and Cancel, and a "?" that shows the hotkeys on hover). Real Controls, so clicks work like any
/// button; one sits over each 3D viewport like the dial and passes everything else through.
/// </summary>
public sealed partial class GeppettoModeBar : Control
{
	public readonly record struct ModeButton( string Label, string Icon, bool Active, Action Run, string Tip = "" );

	public static readonly Color Amber = new( "e8a03a" );

	/// <summary>The frame colour per dial workspace: CAD, Model, Paint, Rig, Clothing, Animate.</summary>
	public static readonly Color[] WorkspaceColours =
	{
		Amber, new( "4a9ff0" ), new( "ef6fb4" ), new( "56c26a" ), new( "a57cf0" ), new( "3cc4b8" ),
	};

	/// <summary>Where a tool's own DrawHint goes: under the bar while one shows, else its old place.</summary>
	public static float HintTop { get; private set; } = 44f;

	const float FrameWidth = 4f;

	PanelContainer _pill = null!;
	HBoxContainer _row = null!;
	Label _step = null!;
	PanelContainer _stepBox = null!;
	string _sig = "";
	Color _colour = Amber;
	string _tag = "";
	bool _iconsOnly;

	/// <summary>The bar's buttons by label, for the window runs (they click them like a user would).</summary>
	public Dictionary<string, Button> Buttons { get; } = new();
	public bool Showing => Visible && _sig.Length > 0;
	public Control Pill => _pill;
	public string Title { get; private set; } = "";
	public Color FrameColour => _colour;
	public string Tag => _tag;

	public override void _Ready()
	{
		SetAnchorsAndOffsetsPreset( LayoutPreset.FullRect );
		MouseFilter = MouseFilterEnum.Ignore;
		Visible = false;

		_pill = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
		_pill.AddThemeStyleboxOverride( "panel", Box( new Color( 0.09f, 0.1f, 0.12f, 0.94f ), 16, new Color( 1, 1, 1, 0.08f ), 8, 4 ) );
		_row = new HBoxContainer();
		_row.AddThemeConstantOverride( "separation", 4 );
		_pill.AddChild( _row );
		AddChild( _pill );

		_stepBox = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
		_stepBox.AddThemeStyleboxOverride( "panel", Box( new Color( 0.08f, 0.09f, 0.11f, 0.85f ), 6, new Color( 0, 0, 0, 0 ), 8, 3 ) );
		_step = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
		_step.AddThemeColorOverride( "font_color", new Color( 0.9f, 0.91f, 0.93f ) );
		_stepBox.AddChild( _step );
		AddChild( _stepBox );

		Resized += Place;
	}

	static StyleBoxFlat Box( Color bg, int radius, Color border, int padX, int padY ) => new()
	{
		BgColor = bg, BorderColor = border,
		BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
		CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
		ContentMarginLeft = padX, ContentMarginRight = padX, ContentMarginTop = padY, ContentMarginBottom = padY,
	};

	/// <summary>Brings the bar up to date with the active tool. Cheap when nothing changed (it runs on
	/// every overlay redraw): the controls are only rebuilt when a title or button state changes.</summary>
	public void Sync()
	{
		var tool = GeppettoTools.Active;
		var mode = tool as IGeppettoModeTool;
		if ( tool == null || mode is { InMode: false } )
		{
			if ( _sig.Length > 0 ) { _sig = ""; Clear(); }
			Visible = false;
			HintTop = 44f;
			QueueRedraw();
			return;
		}
		string title, icon, step, help, tag, done;
		Color colour;
		IReadOnlyList<ModeButton> buttons;
		if ( mode != null )
		{
			title = mode.ModeTitle; icon = mode.ModeIcon; step = mode.ModeStep; help = mode.ModeHelp;
			tag = mode.ModeTag; colour = mode.ModeColour; buttons = mode.ModeButtons; done = mode.DoneLabel;
		}
		else
		{
			int ws = Math.Clamp( GeppettoDialState.Ws, 0, WorkspaceColours.Length - 1 );
			title = tool.Name; icon = tool.Name; step = ""; help = "";
			tag = GeppettoDialData.Workspaces[ws].Name.ToUpperInvariant();
			colour = WorkspaceColours[ws]; buttons = Array.Empty<ModeButton>(); done = "Done";
		}
		string sig = $"{title}|{icon}|{tag}|{colour.ToHtml()}|{done}|{help.GetHashCode()}|" + string.Join( ",", buttons.Select( b => b.Label + ( b.Active ? "*" : "" ) ) );
		Visible = true;
		_colour = colour; _tag = tag; Title = title;
		if ( sig != _sig )
		{
			_sig = sig;
			Build( tool, mode, title, icon, buttons, help, done );
		}
		_step.Text = step;
		_stepBox.Visible = step.Length > 0;
		Place();
		QueueRedraw();
	}

	void Clear()
	{
		foreach ( var c in _row.GetChildren() ) { _row.RemoveChild( c ); c.QueueFree(); }
		Buttons.Clear();
	}

	void Build( IGeppettoViewportTool tool, IGeppettoModeTool? mode, string title, string icon, IReadOnlyList<ModeButton> buttons, string help, string done )
	{
		Clear();
		var ink = new Color( 0.92f, 0.93f, 0.95f );
		int px = (int)( 18 * EditorInterface.Singleton.GetEditorScale() );
		_row.AddChild( new TextureRect { Texture = GeppettoDialData.Icon( GeppettoDialData.IconPath( icon, null ), _colour, px ), StretchMode = TextureRect.StretchModeEnum.KeepCentered, MouseFilter = MouseFilterEnum.Ignore } );
		var name = new Label { Text = title, TooltipText = title, VerticalAlignment = VerticalAlignment.Center, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, MouseFilter = MouseFilterEnum.Pass };
		name.CustomMinimumSize = new Vector2( Math.Min( 260f, name.GetThemeFont( "font" ).GetStringSize( title, HorizontalAlignment.Left, -1, name.GetThemeFontSize( "font_size" ) ).X + 2 ), 0 );
		name.AddThemeColorOverride( "font_color", ink );
		_row.AddChild( name );

		if ( buttons.Count > 0 ) _row.AddChild( new VSeparator() );
		foreach ( var b in buttons )
		{
			var run = b.Run;
			var btn = MakeButton( b.Label, b.Icon, b.Active ? _colour : null, b.Tip.Length > 0 ? b.Tip : b.Label, () => run() );
			_row.AddChild( btn );
		}
		_row.AddChild( new VSeparator() );
		_row.AddChild( MakeButton( done, "", _colour, done == "Finish" ? "Finish (Enter): keep what you drew" : "Done (Enter)", () =>
		{
			if ( mode != null ) mode.ModeDone();
			else Done( tool );
		}, filled: true ) );
		_row.AddChild( MakeButton( "Cancel", "", null, "Cancel: throw this away", () =>
		{
			if ( mode != null ) mode.ModeCancel();
			else GeppettoTools.Activate( null );
		} ) );
		if ( help.Length > 0 )
		{
			var q = new Label { Text = "?", TooltipText = help, MouseFilter = MouseFilterEnum.Stop, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, CustomMinimumSize = new Vector2( 22, 0 ) };
			q.AddThemeColorOverride( "font_color", new Color( 0.7f, 0.72f, 0.76f ) );
			_row.AddChild( q );
		}
		_iconsOnly = false;
	}

	Button MakeButton( string label, string icon, Color? lit, string tip, Action run, bool filled = false )
	{
		var b = new Button { Text = label, TooltipText = tip, FocusMode = FocusModeEnum.None, MouseFilter = MouseFilterEnum.Stop };
		if ( icon.Length > 0 )
			b.Icon = GeppettoDialData.Icon( GeppettoDialData.IconPath( icon, null ), lit ?? new Color( 0.85f, 0.86f, 0.9f ), (int)( 16 * EditorInterface.Singleton.GetEditorScale() ) );
		var bg = filled ? lit!.Value : lit is { } c ? c with { A = 0.22f } : new Color( 1, 1, 1, 0.06f );
		var fg = filled ? new Color( 0.08f, 0.07f, 0.05f ) : lit ?? new Color( 0.9f, 0.91f, 0.93f );
		foreach ( var state in new[] { "normal", "hover", "pressed", "focus" } )
		{
			var sb = Box( state == "hover" ? bg.Lightened( 0.15f ) with { A = Math.Max( bg.A, 0.18f ) } : bg, 10, lit is { } l && !filled ? l with { A = 0.7f } : new Color( 0, 0, 0, 0 ), 8, 2 );
			if ( state == "focus" ) sb.DrawCenter = false;
			b.AddThemeStyleboxOverride( state, sb );
		}
		foreach ( var k in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" } ) b.AddThemeColorOverride( k, fg );
		// Deferred: the run rebuilds the bar, which would free this button inside its own signal.
		b.Pressed += () => Callable.From( () => { run(); GeppettoTools.Redraw(); } ).CallDeferred();
		Buttons[label] = b;
		return b;
	}

	/// <summary>Done for a tool without its own: Enter, the way its hint says to confirm; if it is still
	/// running after that (a brush has nothing to confirm), it stops.</summary>
	static void Done( IGeppettoViewportTool tool )
	{
		var cam = EditorInterface.Singleton.GetEditorViewport3D( 0 )?.GetCamera3D();
		if ( cam != null ) tool.Input( cam, new InputEventKey { Keycode = Key.Enter, Pressed = true } );
		if ( GeppettoTools.Active == tool ) GeppettoTools.Activate( null );
	}

	/// <summary>Top centre, under the viewport's own menu row. When the bar is wider than the viewport the
	/// tool buttons drop their words (icons with tooltips), so nothing runs off the edge.</summary>
	void Place()
	{
		if ( _pill == null ) return;
		float room = Size.X - 24f;
		SetIconsOnly( false );
		if ( _pill.GetCombinedMinimumSize().X > room ) SetIconsOnly( true );
		var ps = _pill.GetCombinedMinimumSize();
		_pill.Size = ps;
		// Top centre, unless that would sit on the SKETCH tag or the view gizmo (top-right, ~90px):
		// then it drops below both, so nothing covers anything.
		float x = Mathf.Max( 12f, ( Size.X - ps.X ) * 0.5f );
		var tag = TagRect();
		bool crowded = x < tag.End.X + 8f || x + ps.X > Size.X - GizmoRoom;
		_pill.Position = new Vector2( x, crowded ? Mathf.Max( tag.End.Y, GizmoRoom ) + 6f : 40f );
		float stepW = Mathf.Min( Mathf.Max( ps.X, 200f ), Mathf.Max( 120f, Size.X - 24f ) );
		_step.CustomMinimumSize = new Vector2( stepW - 16f, 0 );
		var ss = _stepBox.GetCombinedMinimumSize();
		_stepBox.Size = ss;
		_stepBox.Position = new Vector2( Mathf.Max( 12f, ( Size.X - ss.X ) * 0.5f ), _pill.Position.Y + ps.Y + 6f );
		HintTop = _pill.Position.Y + ps.Y + ( _stepBox.Visible ? ss.Y + 12f : 0f ) + 14f;
	}

	/// <summary>Room the editor's view gizmo takes at the viewport's top-right.</summary>
	const float GizmoRoom = 92f;

	int TagFontSize => Math.Max( 10, GetThemeDefaultFontSize() - 2 );

	/// <summary>The tag, top-left under the editor's Perspective menu.</summary>
	public Rect2 TagRect()
	{
		if ( _tag.Length == 0 ) return new Rect2( FrameWidth + 6, 40f, 0, 0 );
		var ts = GetThemeDefaultFont().GetStringSize( _tag, HorizontalAlignment.Left, -1, TagFontSize );
		return new Rect2( new Vector2( FrameWidth + 6, 40f ), ts + new Vector2( 14, 6 ) );
	}

	void SetIconsOnly( bool on )
	{
		if ( _iconsOnly == on ) return;
		_iconsOnly = on;
		foreach ( var (label, b) in Buttons )
			if ( b.Icon != null ) b.Text = on ? "" : label;
	}

	public override void _Draw()
	{
		if ( !Showing ) return;
		// The frame, inset so all four sides show even where the viewport meets the editor's edge.
		var r = new Rect2( Vector2.Zero, Size ).Grow( -FrameWidth / 2 );
		DrawRect( r, _colour, false, FrameWidth );
		if ( _tag.Length == 0 ) return;
		var font = GetThemeDefaultFont();
		int fs = TagFontSize;
		var box = TagRect();
		DrawStyleBox( Box( _colour, 4, new Color( 0, 0, 0, 0 ), 0, 0 ), box );
		DrawString( font, box.Position + new Vector2( 7, 3 + font.GetAscent( fs ) ), _tag, HorizontalAlignment.Left, -1, fs, new Color( 0.08f, 0.07f, 0.05f ) );
	}
}
#endif
