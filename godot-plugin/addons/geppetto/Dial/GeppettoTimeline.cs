#if TOOLS
#nullable enable
using System;
using System.Linq;
using Godot;

namespace Geppetto;

/// <summary>
/// The Animate workspace's "Timeline" tab in Poohdot's bottom panel: one lane per bone with the clip's
/// keyframe diamonds, a frame ruler and an orange playhead. Clicking or dragging on the ruler or the lanes
/// moves the playhead and poses the skeleton from the clip. Kept simple for this pass: no key dragging,
/// no curve view, no range markers (TODO).
/// </summary>
[Tool]
public partial class GeppettoTimeline : EditorDock
{
	public GeppettoCharacter? Character;
	public int Frame;
	public event Action<int>? FrameChanged;

	Label _clip = null!, _frameLabel = null!;
	Button _play = null!;
	Lanes _lanes = null!;
	bool _playing;
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

		var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2( 0, 120 ) };
		col.AddChild( scroll );
		_lanes = new Lanes { Owner_ = this, SizeFlagsHorizontal = SizeFlags.ExpandFill };
		scroll.AddChild( _lanes );
	}

	/// <summary>Re-reads the character's clip (after keys were added, or the character changed).</summary>
	public void Refresh()
	{
		var c = Character;
		_frameLabel.Text = $"Frame {Frame}";
		_clip.Text = c == null ? "No Geppetto character selected"
			: c.Clip == null ? "No clip"
			: $"{System.IO.Path.GetFileNameWithoutExtension( c.EffigyPath )} · {c.ClipName} · {c.Fps} fps";
		_clip.TooltipText = _clip.Text;
		_lanes.Relayout();
	}

	public void SetFrame( int f, bool scrub = true )
	{
		int max = ( Character?.FrameCount ?? 60 ) - 1;
		f = Math.Clamp( f, 0, max );
		if ( f == Frame && !scrub ) return;
		Frame = f;
		if ( scrub ) Character?.ScrubTo( f );
		_frameLabel.Text = $"Frame {Frame}";
		_lanes.QueueRedraw();
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

	/// <summary>The lanes: a name column, a frame ruler, rows of key diamonds and the playhead.</summary>
	[Tool]
	public partial class Lanes : Control
	{
		public GeppettoTimeline Owner_ = null!;
		const float RowH = 20, RulerH = 22, FramePx = 12, Pad = 10;
		static readonly Color Gep = new( "e8a03a" );
		float _nameW = 120;
		bool _drag;

		Font F => GetThemeFont( "font", "Label" );
		int FS => GetThemeFontSize( "font_size", "Label" );

		public void Relayout()
		{
			var c = Owner_.Character;
			var names = c?.BoneNames ?? Array.Empty<string>();
			// The name column grows to the longest bone name, so no name is ever cut.
			_nameW = Mathf.Max( 120, names.Count == 0 ? 0 : names.Max( n => F.GetStringSize( n, HorizontalAlignment.Left, -1, FS ).X ) + 2 * Pad );
			int frames = c?.FrameCount ?? 60;
			CustomMinimumSize = new Vector2( _nameW + frames * FramePx + Pad, RulerH + Mathf.Max( 1, names.Count ) * RowH + 4 );
			QueueRedraw();
		}

		float X( int frame ) => _nameW + frame * FramePx + FramePx / 2;

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

			if ( names.Count == 0 )
				DrawString( font, new Vector2( Pad, RulerH + RowH - 6 ), c == null ? "Select a Geppetto character to see its bones." : "This character has no bones.",
					HorizontalAlignment.Left, -1, fs, dim );

			// lanes
			for ( int i = 0; i < names.Count; i++ )
			{
				float y = RulerH + i * RowH;
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
