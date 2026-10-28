#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Geppetto;

/// <summary>
/// The Animate workspace's "Timeline" tab in Poohdot's bottom panel: one lane per bone with the clip's
/// keyframe diamonds, a frame ruler and an orange playhead. Clicking or dragging on the ruler or the lanes
/// moves the playhead and poses the skeleton from the clip. The clip switcher lists the model's takes
/// (<c>&lt;base&gt;_*.riganim</c>) and makes new ones; an Events row shows Spawn model / Morph events; the
/// Shape Keys button opens the shape-key sliders beside the lanes. Not here yet: key dragging, curve view.
/// </summary>
[Tool]
public partial class GeppettoTimeline : EditorDock
{
	public GeppettoCharacter? Character;
	public int Frame;
	public event Action<int>? FrameChanged;

	/// <summary>The playhead of the open Timeline (the frame the Animate tools key and spawn at).</summary>
	public static int CurrentFrame { get; private set; }

	const string NewTakeItem = "New take…";
	Label _clip = null!, _frameLabel = null!;
	OptionButton _takes = null!;
	Button _play = null!, _shapeKeysButton = null!;
	AnimateShapeKeys _shapeKeys = null!;
	Lanes _lanes = null!;
	bool _playing, _filling;
	double _playAcc;

	public GeppettoTimeline()
	{
		Title = "Timeline";
		LayoutKey = "geppetto_timeline";
		DefaultSlot = DockSlot.Bottom;
		IconName = "Animation";
		Closable = false;
		AvailableLayouts = DockLayout.Horizontal | DockLayout.Floating;

		var col = new VBoxContainer();
		AddChild( col );
		var bar = new HBoxContainer();
		col.AddChild( bar );
		_play = new Button { Text = "Play", ToggleMode = true, TooltipText = "Play the clip from the playhead" };
		_play.Toggled += on => { _playing = on; _play.Text = on ? "Stop" : "Play"; };
		bar.AddChild( _play );
		_frameLabel = new Label { VerticalAlignment = VerticalAlignment.Center };
		bar.AddChild( _frameLabel );
		_clip = new Label { VerticalAlignment = VerticalAlignment.Center, SizeFlagsHorizontal = SizeFlags.ExpandFill,
			HorizontalAlignment = HorizontalAlignment.Right, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
		bar.AddChild( _clip );
		_takes = new OptionButton { FitToLongestItem = true, TooltipText = "Take: the .riganim being animated" };
		_takes.ItemSelected += OnTakePicked;
		bar.AddChild( _takes );
		_shapeKeysButton = new Button { Text = "Shape Keys", ToggleMode = true, TooltipText = "Show the shape-key sliders" };
		_shapeKeysButton.Toggled += on => { _shapeKeys.Visible = on; if ( on ) _shapeKeys.Show( Character, Frame ); };
		bar.AddChild( _shapeKeysButton );

		var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		col.AddChild( split );
		var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2( 0, 120 ) };
		split.AddChild( scroll );
		_lanes = new Lanes { Owner_ = this, SizeFlagsHorizontal = SizeFlags.ExpandFill };
		scroll.AddChild( _lanes );
		_shapeKeys = new AnimateShapeKeys { Visible = false, Frame = () => Frame };
		split.AddChild( _shapeKeys );

		GeppettoCharacter.ClipChanged += OnClipChanged;
	}

	public override void _ExitTree() => GeppettoCharacter.ClipChanged -= OnClipChanged;

	void OnClipChanged( GeppettoCharacter c )
	{
		if ( c != Character || !IsInstanceValid( this ) ) return;
		Refresh();
		Character?.ScrubTo( Frame );
	}

	void OnTakePicked( long index )
	{
		if ( _filling || Character == null ) return;
		if ( _takes.GetItemText( (int)index ) == NewTakeItem ) Character.NewTake();
		else if ( _takes.GetItemMetadata( (int)index ).AsString() is { Length: > 0 } path ) Character.SwitchClip( path );
		Refresh();
		Character.ScrubTo( Frame );
	}

	void FillTakes()
	{
		_filling = true;
		_takes.Clear();
		var c = Character;
		_takes.Disabled = c?.Doc == null || c.Rig == null;
		if ( !_takes.Disabled )
		{
			string? cur = c!.ClipDiskPath is { } p ? System.IO.Path.GetFullPath( p ) : null;
			foreach ( var t in c.Takes() )
			{
				_takes.AddItem( c.TakeName( t ) );
				int i = _takes.ItemCount - 1;
				_takes.SetItemMetadata( i, t );
				_takes.SetItemTooltip( i, t );
				if ( t == cur ) _takes.Select( i );
			}
			_takes.AddSeparator();
			_takes.AddItem( NewTakeItem );
		}
		_takes.TooltipText = c?.ClipDiskPath ?? "Take: the .riganim being animated";
		_filling = false;
	}

	/// <summary>Re-reads the character's clip (after keys were added, or the character changed).</summary>
	public void Refresh()
	{
		var c = Character;
		_frameLabel.Text = $"Frame {Frame}";
		_clip.Text = c == null ? "No Geppetto character selected"
			: c.Clip == null ? "No clip"
			: $"{System.IO.Path.GetFileNameWithoutExtension( c.EffigyPath )} · {c.ClipName}{( c.ClipSession!.IsDirty ? " *" : "" )} · {c.Fps} fps";
		_clip.TooltipText = _clip.Text;
		FillTakes();
		if ( _shapeKeys.Visible ) _shapeKeys.Show( c, Frame );
		_lanes.Relayout();
	}

	public void SetFrame( int f, bool scrub = true )
	{
		int max = ( Character?.FrameCount ?? 60 ) - 1;
		f = Math.Clamp( f, 0, max );
		if ( f == Frame && !scrub ) return;
		Frame = f;
		CurrentFrame = f;
		if ( scrub ) Character?.ScrubTo( f );
		_frameLabel.Text = $"Frame {Frame}";
		_lanes.QueueRedraw();
		if ( _shapeKeys.Visible && !_playing ) _shapeKeys.Show( Character, f );
		FrameChanged?.Invoke( f );
	}

	public override void _Process( double delta )
	{
		if ( !_playing || Character?.Clip == null ) return;
		_playAcc += delta * Character.Fps;
		if ( _playAcc < 1 ) return;
		int step = (int)_playAcc; _playAcc -= step;
		SetFrame( ( Frame + step ) % Character.FrameCount );
	}

	/// <summary>The lanes: a name column, a frame ruler, the events row, rows of key diamonds and the playhead.</summary>
	[Tool]
	public partial class Lanes : Control
	{
		public GeppettoTimeline Owner_ = null!;
		const float RowH = 20, RulerH = 22, FramePx = 12, Pad = 10;
		static readonly Color Gep = new( "e8a03a" );
		float _nameW = 120;
		bool _drag;
		List<(string Label, int Start, int End, bool Morph)> _events = new();
		float EventsH => _events.Count == 0 ? 0 : RowH;

		Font F => GetThemeFont( "font", "Label" );
		int FS => GetThemeFontSize( "font_size", "Label" );

		public void Relayout()
		{
			var c = Owner_.Character;
			var names = c?.BoneNames ?? Array.Empty<string>();
			_events = AnimateTools.Events( c );
			// The name column grows to the longest bone name, so no name is ever cut.
			_nameW = Mathf.Max( 120, names.Count == 0 ? 0 : names.Max( n => F.GetStringSize( n, HorizontalAlignment.Left, -1, FS ).X ) + 2 * Pad );
			_nameW = Mathf.Max( _nameW, F.GetStringSize( "Events", HorizontalAlignment.Left, -1, FS ).X + 2 * Pad );
			int frames = c?.FrameCount ?? 60;
			// An event bar is at least as long as its label, so the lanes grow to fit the last one.
			float evRight = _events.Count == 0 ? 0 : _events.Max( e => X( e.Start ) + BarW( e ) );
			CustomMinimumSize = new Vector2( Mathf.Max( _nameW + frames * FramePx + Pad, evRight + Pad ), RulerH + EventsH + Mathf.Max( 1, names.Count ) * RowH + 4 );
			QueueRedraw();
		}

		float X( int frame ) => _nameW + frame * FramePx + FramePx / 2;

		float BarW( (string Label, int Start, int End, bool Morph) e ) =>
			Mathf.Max( ( e.End - e.Start + 1 ) * FramePx, F.GetStringSize( e.Label, HorizontalAlignment.Left, -1, FS - 2 ).X + 8 );

		public override void _GuiInput( InputEvent e )
		{
			if ( e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb )
			{
				_drag = mb.Pressed && mb.Position.X >= _nameW;
				if ( _drag ) { Seek( mb.Position.X ); AcceptEvent(); }
			}
			else if ( e is InputEventMouseMotion mm && _drag ) { Seek( mm.Position.X ); AcceptEvent(); }
		}

		void Seek( float x ) => Owner_.SetFrame( Mathf.RoundToInt( ( x - _nameW - FramePx / 2 ) / FramePx ) );

		public override void _Draw()
		{
			var c = Owner_.Character;
			var names = c?.BoneNames ?? Array.Empty<string>();
			int frames = c?.FrameCount ?? 60, fps = c?.Fps ?? 30;
			var font = F; int fs = FS;
			var text = GetThemeColor( "font_color", "Label" );
			var dim = text; dim.A = 0.55f;
			float w = Size.X;

			// ruler
			DrawRect( new Rect2( 0, 0, w, RulerH ), new Color( 0, 0, 0, 0.25f ) );
			int major = fps >= 20 ? 10 : 5;
			for ( int f = 0; f < frames; f++ )
			{
				float x = X( f );
				bool big = f % major == 0;
				DrawLine( new Vector2( x, RulerH - ( big ? 8 : 4 ) ), new Vector2( x, RulerH ), dim, 1 );
				if ( big ) DrawString( font, new Vector2( x + 2, RulerH - 9 ), f.ToString(), HorizontalAlignment.Left, -1, fs - 3, dim );
			}

			// events: Spawn model (blue) and Morph (purple) bars with the label inside
			if ( _events.Count > 0 )
			{
				DrawRect( new Rect2( 0, RulerH, w, RowH ), new Color( 0, 0, 0, 0.12f ) );
				DrawString( font, new Vector2( Pad, RulerH + RowH / 2 + font.GetAscent( fs ) / 2 - 2 ), "Events", HorizontalAlignment.Left, -1, fs, dim );
				foreach ( var e in _events )
				{
					float x0 = X( e.Start ) - FramePx / 2;
					DrawRect( new Rect2( x0, RulerH + 3, BarW( e ), RowH - 6 ), e.Morph ? new Color( "8a6fd1", 0.75f ) : new Color( "4f8fd8", 0.75f ) );
					DrawString( font, new Vector2( x0 + 4, RulerH + RowH / 2 + font.GetAscent( fs - 2 ) / 2 - 2 ), e.Label, HorizontalAlignment.Left, -1, fs - 2, text );
				}
			}

			if ( names.Count == 0 )
				DrawString( font, new Vector2( Pad, RulerH + EventsH + RowH - 6 ), c == null ? "Select a Geppetto character to see its bones." : "This character has no bones.",
					HorizontalAlignment.Left, -1, fs, dim );

			// lanes
			for ( int i = 0; i < names.Count; i++ )
			{
				float y = RulerH + EventsH + i * RowH;
				if ( i % 2 == 1 ) DrawRect( new Rect2( 0, y, w, RowH ), new Color( 1, 1, 1, 0.03f ) );
				DrawString( font, new Vector2( Pad, y + RowH / 2 + font.GetAscent( fs ) / 2 - 2 ), names[i], HorizontalAlignment.Left, -1, fs, text );
				foreach ( var f in c!.KeyFrames( names[i] ) )
				{
					var p = new Vector2( X( f ), y + RowH / 2 );
					const float d = 5;
					DrawColoredPolygon( new[] { p + new Vector2( 0, -d ), p + new Vector2( d, 0 ), p + new Vector2( 0, d ), p + new Vector2( -d, 0 ) }, new Color( "d9dbe0" ) );
					DrawPolyline( new[] { p + new Vector2( 0, -d ), p + new Vector2( d, 0 ), p + new Vector2( 0, d ), p + new Vector2( -d, 0 ), p + new Vector2( 0, -d ) }, new Color( 0, 0, 0, 0.6f ), 1, true );
				}
			}
			DrawLine( new Vector2( _nameW, 0 ), new Vector2( _nameW, Size.Y ), new Color( 1, 1, 1, 0.08f ), 1 );

			// playhead
			float px = X( Owner_.Frame );
			DrawLine( new Vector2( px, 0 ), new Vector2( px, Size.Y ), Gep, 2 );
			DrawColoredPolygon( new[] { new Vector2( px - 5, 0 ), new Vector2( px + 5, 0 ), new Vector2( px, 7 ) }, Gep );
		}
	}
}
#endif
