using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Which frames an onion skin shows. Keys = only keyframes (the animator's poses, not
/// every in-between — the default, because ghosting every frame of a 24fps shot is
/// noise); Frames = every frame within range; Selected = only frames the user picked
/// (e.g. the two drawings being in-betweened).
/// </summary>
public enum OnionMode
{
	Keys,
	Frames,
	Selected
}

/// <summary>
/// One ghost frame in an onion skin: a frame number, which side of the current frame
/// it sits on, how far away it is, and how it should be drawn. Opacity already folds
/// in master, side, per-offset, and falloff — the renderer just multiplies it.
/// Tint is the side colour; TintStrength 1 means the ghost IS a flat silhouette in
/// that colour (Krita's 100% tint), 0 means the drawing's own colours show through
/// and the tint is only a label. Colour is a Vec4 (r,g,b,a) so the C# side and the
/// QML side speak the same numbers; the QML overlay converts to a Qt color.
/// </summary>
public sealed class OnionFrame
{
	public int Frame;
	public bool IsBefore;
	public int Distance;
	public float Opacity;
	public Vec4 Tint;
	public float TintStrength;
	public bool IsStartFrame;

	public OnionFrame( int frame, bool isBefore, int distance, float opacity, Vec4 tint, float tintStrength, bool isStartFrame = false )
	{
		Frame = frame; IsBefore = isBefore; Distance = distance;
		Opacity = opacity; Tint = tint; TintStrength = tintStrength; IsStartFrame = isStartFrame;
	}

	/// <summary>Silhouette colour: side colour at the ghost's opacity, alpha included.</summary>
	public Vec4 Silhouette() => new( Tint.x, Tint.y, Tint.z, Opacity );

	/// <summary>Drawing colour mixed toward the side colour by TintStrength, alpha scaled
	/// by the ghost's opacity. Strength 0 returns the drawing colour, ghosted only.</summary>
	public Vec4 Mix( Vec4 drawing ) => new(
		drawing.x + (Tint.x - drawing.x) * TintStrength,
		drawing.y + (Tint.y - drawing.y) * TintStrength,
		drawing.z + (Tint.z - drawing.z) * TintStrength,
		drawing.w * Opacity );
}

/// <summary>
/// Onion-skin settings for ONE layer (or the whole canvas when no layer overrides).
/// Colours are configurable ON PURPOSE and default to the Toon Boom Harmony convention
/// — red = previous, green = next — because the vendors DISAGREE (Animation Desk uses
/// green = previous, red = next) and a hardcoded convention would be wrong for half
/// the users who ever touched another app. Swap() flips them in one call; setting the
/// two colours directly allows any pair.
/// </summary>
public sealed class OnionSettings
{
	public bool Enabled = true;
	public OnionMode Mode = OnionMode.Keys;
	public int BeforeCount = 1;
	public int AfterCount = 1;
	public float MasterOpacity = 0.5f;
	public float BeforeOpacity = 1f;
	public float AfterOpacity = 1f;

	/// <summary>Per-offset opacity multipliers, index 0 = the NEAREST ghost on a side
	/// (rank, not frame distance — see Compute). When present and long enough, an entry
	/// REPLACES the linear falloff for that rank; beyond the list, falloff resumes.
	/// Null = pure falloff. Krita exposes exactly this as per-frame opacity sliders.</summary>
	public float[] PerOffsetOpacity;

	/// <summary>Linear distance falloff: distance 1 keeps full opacity, the farthest shown
	/// frame keeps 1/count. With count 3: 1.00, 0.67, 0.33. Set count 0 on a side to hide it.</summary>
	public bool UseFalloff = true;

	public Vec4 BeforeColor = new( 1f, 0f, 0f, 1f );
	public Vec4 AfterColor = new( 0f, 1f, 0f, 1f );
	public float TintStrength = 1f;

	/// <summary>Looping shots: also show frame 0 when the playhead is near the end, so the
	/// loop joint can be judged. Only applies when Loop is true at compute time.</summary>
	public bool ShowStartFrame;

	public void SwapColors()
	{
		var tmp = BeforeColor; BeforeColor = AfterColor; AfterColor = tmp;
		var tmpO = BeforeOpacity; BeforeOpacity = AfterOpacity; AfterOpacity = tmpO;
	}

	public void Validate()
	{
		if ( BeforeCount < 0 || AfterCount < 0 ) throw new ArgumentOutOfRangeException( "Onion counts cannot be negative." );
		if ( MasterOpacity < 0f || MasterOpacity > 1f || BeforeOpacity < 0f || BeforeOpacity > 1f
			|| AfterOpacity < 0f || AfterOpacity > 1f || TintStrength < 0f || TintStrength > 1f )
			throw new ArgumentOutOfRangeException( "Onion opacities and tint strength live in 0..1." );
		if ( PerOffsetOpacity is not null )
			foreach ( var v in PerOffsetOpacity )
				if ( v < 0f || v > 1f ) throw new ArgumentOutOfRangeException( "Per-offset opacities live in 0..1." );
	}
}

/// <summary>
/// The onion-skin plan for a playhead position: which ghost frames to draw, in draw
/// order (farthest first, so nearer ghosts paint over farther ones). Pure computation
/// — no drawing, no canvas, no particles, no brush: the QML overlay and any future
/// renderer consume OnionFrame lists and draw them however they draw drawings.
/// </summary>
public static class OnionSkin
{
	public static List<OnionFrame> Compute(
		OnionSettings settings, int currentFrame, int totalFrames,
		IReadOnlyList<int> keyFrames = null, IReadOnlyList<int> selectedFrames = null, bool loop = false )
	{
		if ( settings is null ) throw new ArgumentNullException( nameof( settings ) );
		settings.Validate();
		if ( !settings.Enabled || totalFrames <= 0 ) return new List<OnionFrame>();
		currentFrame = Math.Clamp( currentFrame, 0, totalFrames - 1 );

		var candidates = new List<int>();
		switch ( settings.Mode )
		{
			case OnionMode.Keys:
				if ( keyFrames is not null ) candidates.AddRange( keyFrames );
				break;
			case OnionMode.Frames:
				for ( var f = 0; f < totalFrames; f++ ) candidates.Add( f );
				break;
			case OnionMode.Selected:
				if ( selectedFrames is not null ) candidates.AddRange( selectedFrames );
				break;
		}

		var frames = new List<OnionFrame>();
		void AddSide( bool before, int count, float sideOpacity, Vec4 color )
		{
			if ( count <= 0 ) return;
			var side = candidates
				.Where( f => f >= 0 && f < totalFrames && f != currentFrame && (before ? f < currentFrame : f > currentFrame) )
				.Select( f => (frame: f, dist: Math.Abs( f - currentFrame )) )
				.OrderBy( x => x.dist ).Take( count ).ToList();
			// Opacity falls off by RANK (1st ghost, 2nd ghost, …), not by frame distance:
			// in Keys mode two keys can be 20 frames apart, and a falloff in raw frames
			// would fade the second key to nothing. Distance stays on the frame for
			// labels and draw order; only the rank drives opacity.
			for ( var rank = 0; rank < side.Count; rank++ )
			{
				var (frame, dist) = side[rank];
				frames.Add( new OnionFrame( frame, before, dist,
					OpacityFor( settings, before, rank + 1, count, sideOpacity ), color, settings.TintStrength ) );
			}
		}
		AddSide( true, settings.BeforeCount, settings.BeforeOpacity, settings.BeforeColor );
		AddSide( false, settings.AfterCount, settings.AfterOpacity, settings.AfterColor );

		// Loop joint: frame 0 as an "after" ghost when the playhead is near the end and
		// frame 0 is not already shown. It does NOT consume an AfterCount slot — it is
		// the loop's own frame, and hiding a real next frame for it would be wrong.
		if ( loop && settings.ShowStartFrame && currentFrame != 0
			&& !frames.Any( f => f.Frame == 0 ) )
		{
			var dist = totalFrames - currentFrame;
			// Rank 1 on purpose: the loop joint must stay visible enough to judge,
			// never faded out by being far away in frames from the playhead.
			frames.Add( new OnionFrame( 0, false, dist,
				OpacityFor( settings, false, 1, Math.Max( 1, settings.AfterCount ), settings.AfterOpacity ),
				settings.AfterColor, settings.TintStrength, isStartFrame: true ) );
		}

		// Farthest first so nearer ghosts win the paint order.
		frames.Sort( ( x, y ) => y.Distance.CompareTo( x.Distance ) );
		return frames;
	}

	static float OpacityFor( OnionSettings s, bool before, int rank, int count, float sideOpacity )
	{
		var factor = 1f;
		if ( s.PerOffsetOpacity is not null && rank - 1 < s.PerOffsetOpacity.Length )
			factor = s.PerOffsetOpacity[rank - 1];
		else if ( s.UseFalloff && count > 0 )
			factor = Math.Max( 0f, 1f - (rank - 1) / (float)count );
		return Math.Clamp( s.MasterOpacity * sideOpacity * factor, 0f, 1f );
	}
}

/// <summary>
/// Per-layer onion skins: each layer may carry its own settings; a layer without one
/// falls back to Default. A disabled layer contributes no ghosts, even if Default is
/// on — Krita's per-layer lightbulb, in model form. The plan is still just a frame list.
/// </summary>
public sealed class OnionSkinSet
{
	public OnionSettings Default = new();
	readonly Dictionary<string, OnionSettings> _layers = new();

	public void SetLayer( string layerId, OnionSettings settings )
	{
		if ( string.IsNullOrWhiteSpace( layerId ) ) throw new ArgumentException( "A layer needs an id." );
		_layers[layerId] = settings ?? throw new ArgumentNullException( nameof( settings ) );
	}

	public OnionSettings ForLayer( string layerId ) =>
		layerId is not null && _layers.TryGetValue( layerId, out var s ) ? s : Default;

	public List<OnionFrame> ComputeForLayer( string layerId, int currentFrame, int totalFrames,
		IReadOnlyList<int> keyFrames = null, IReadOnlyList<int> selectedFrames = null, bool loop = false ) =>
		OnionSkin.Compute( ForLayer( layerId ), currentFrame, totalFrames, keyFrames, selectedFrames, loop );
}
