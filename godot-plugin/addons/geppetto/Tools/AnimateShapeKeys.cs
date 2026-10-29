#if TOOLS
#nullable enable
using System;
using System.Linq;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

/// <summary>
/// The Shape Keys panel beside the Timeline, ported from s&amp;box's EffigyShapeKeysPanel: a "Shape Keys ·
/// body" header, a wrapped status line, then one row per shape key with its name, a 0–1 slider and a
/// percentage readout. Dragging previews the weight on the mesh; letting go keys it on the take's Morph
/// track at the playhead (one undo step), the way Marionette keys morphs.
/// </summary>
[Tool]
public partial class AnimateShapeKeys : VBoxContainer
{
	public Func<int>? Frame;
	GeppettoCharacter? _c;
	readonly Label _title, _status;
	readonly VBoxContainer _list;

	public AnimateShapeKeys()
	{
		CustomMinimumSize = new Vector2( 240, 0 );
		_title = new Label { Text = "Shape Keys", AutowrapMode = TextServer.AutowrapMode.WordSmart };
		AddChild( _title );
		_status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color( 1, 1, 1, 0.7f ) };
		AddChild( _status );
		var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		AddChild( scroll );
		_list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		scroll.AddChild( _list );
	}

	/// <summary>Keys <paramref name="morph"/>'s weight at <paramref name="frame"/> on the current take
	/// (a Morph track key; its weight is the key's Scale.x). One undo step.</summary>
	public static bool KeyWeight( GeppettoCharacter c, string morph, int frame, float weight )
	{
		if ( !c.EnsureClip() || c.ClipSession == null ) { GeppettoToolContext.Warn( "Shape key: this character has no take (it needs a rig)." ); return false; }
		weight = Math.Clamp( weight, 0f, 1f );
		var pose = new RigPose( Vec3.Zero, Quat.Identity, new Vec3( weight, weight, weight ) );
		var cmd = ClipEditService.BuildInsertOrReplaceKey( c.ClipSession, RigTrackKind.Morph, morph, frame, pose );
		return c.ClipEdit( cmd, $"Geppetto: key {morph} {weight:P0}" );
	}

	/// <summary>Rebuilds the rows for <paramref name="c"/> at <paramref name="frame"/>.</summary>
	public void Show( GeppettoCharacter? c, int frame )
	{
		_c = c;
		foreach ( var ch in _list.GetChildren() ) ch.QueueFree();
		_title.Text = c == null ? "Shape Keys" : $"Shape Keys · {System.IO.Path.GetFileNameWithoutExtension( c.EffigyPath )}";
		_title.TooltipText = _title.Text;
		if ( c == null ) { _status.Text = "Select a Geppetto character."; return; }
		var morphs = c.MorphNames();
		if ( morphs.Count == 0 ) { _status.Text = "This model has no shape keys. Add one in the Model workspace (Shape Key)."; return; }
		_status.Text = c.Clip == null ? "No take open." : $"Drag a slider to key it at frame {frame}.";
		var weights = c.Clip?.MorphWeightsAt( frame );
		var keyed = c.Clip?.Tracks.Where( t => t.Kind == RigTrackKind.Morph ).Select( t => t.Name ).ToHashSet() ?? new();

		foreach ( var name in morphs )
		{
			float w = weights != null && weights.TryGetValue( name, out var v ) ? v : 0f;
			var row = new HBoxContainer();
			var label = new Label
			{
				Text = keyed.Contains( name ) ? name + " ◆" : name, TooltipText = name, CustomMinimumSize = new Vector2( 90, 0 ),
				TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 0.8f,
				MouseFilter = MouseFilterEnum.Pass,
			};
			var slider = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.01, Value = w, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
			var readout = new Label { Text = $"{w:P0}", HorizontalAlignment = HorizontalAlignment.Right };
			// The readout is as wide as "100 %" in the current font, so it never jitters or cuts.
			readout.CustomMinimumSize = new Vector2( readout.GetThemeFont( "font" ).GetStringSize( $"{1f:P0}", HorizontalAlignment.Left, -1, readout.GetThemeFontSize( "font_size" ) ).X + 4, 0 );
			slider.ValueChanged += val => { readout.Text = $"{val:P0}"; Preview( name, (float)val ); };
			slider.DragEnded += changed => { if ( changed && _c != null ) KeyWeight( _c, name, Frame?.Invoke() ?? frame, (float)slider.Value ); };
			row.AddChild( label );
			row.AddChild( slider );
			row.AddChild( readout );
			_list.AddChild( row );
		}
	}

	void Preview( string morph, float w )
	{
		if ( _c?.Body is not { Mesh: ArrayMesh m } body ) return;
		for ( int i = 0; i < m.GetBlendShapeCount(); i++ )
			if ( m.GetBlendShapeName( i ) == morph ) body.SetBlendShapeValue( i, w );
	}
}
#endif
