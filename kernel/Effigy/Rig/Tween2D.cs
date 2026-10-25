using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// 2D tweening for the 2D animation suite (lane 5/13). Parameter-level keyframes in the
/// Synfig waypoint spirit: every key carries its own In (how the value ARRIVES at the key)
/// and Out (how it LEAVES the key), so a single track can hold, ease, and snap without a
/// second track per behaviour.
///
/// Easing reuses the existing RigEase machinery and NEVER edits RigAnimFile.cs:
/// Linear / Smooth / EaseIn / EaseOut / Stepped go through RigKey.Apply, exactly as a
/// RigTrack does. The modes RigEase cannot express get their own handling here and only
/// here: Bezier (cubic handles), Tcb (Kochanek-Bartels tangents), Clamped (TCB tangents
/// clamped so a segment can never overshoot its own endpoints), and Constant (a hold —
/// the same visible result as Stepped, kept as its own name because Synfig authors say
/// "hold", not "step", and a later UI should not have to translate).
///
/// Deterministic, engine-free, no AI: shape in-betweening is point-by-point lerp of
/// same-point-count strokes, Grease Pencil style. A stroke pair with different point
/// counts is REJECTED loudly instead of resampled silently — resampling would invent
/// geometry the artist never drew, and the count mismatch is the signal that a
/// correspondence pass (a later lane) is needed, not a guess.
/// </summary>
public enum TweenEase
{
	/// <summary>Hold the left key's value until the next key. Same picture as Stepped.</summary>
	Constant,
	Linear,
	EaseIn,
	EaseOut,
	Smooth,
	/// <summary>Jump at the segment start: the value IS the left key for the whole segment.</summary>
	Stepped,
	/// <summary>Cubic Bezier through Out/In handles (0..1 in both axes, see TweenKey).</summary>
	Bezier,
	/// <summary>Kochanek-Bartels (Tension/Continuity/Bias) Hermite through the neighbours.</summary>
	Tcb,
	/// <summary>TCB tangents clamped to the segment's own direction and size: no overshoot.</summary>
	Clamped
}

/// <summary>One parameter keyframe. Frame is an integer frame number; Value is the parameter
/// (pixels, degrees, scale factor, opacity — the track, not the key, knows the unit).
/// In/Out default to Linear so a key typed with no easing still moves predictably.</summary>
public sealed class TweenKey
{
	public int Frame;
	public float Value;
	public TweenEase In = TweenEase.Linear;
	public TweenEase Out = TweenEase.Linear;

	/// <summary>Bezier handles in unit segment space: Out leaves this key, In arrives at the
	/// NEXT key. Ignored unless the segment's easing is Bezier. Null handles fall back to
	/// Smooth rather than crashing a track an author only half-filled.</summary>
	public float OutX = 0.33f, OutY = 0f, InX = 0.67f, InY = 1f;

	/// <summary>TCB parameters, each -1..1. Ignored unless the segment's easing is Tcb/Clamped.</summary>
	public float Tension, Continuity, Bias;

	public TweenKey() { }
	public TweenKey( int frame, float value ) { Frame = frame; Value = value; }
}

/// <summary>
/// One scalar 2D parameter over time: position X, rotation, opacity, anything one number
/// describes. Keys are kept sorted; inserting a key on an existing frame REPLACES it
/// (a track holds one value per frame, never a stack of competing keys).
/// Held before the first key and after the last, exactly like RigTrack.Evaluate.
/// </summary>
public sealed class TweenTrack
{
	public string Name = "";
	readonly List<TweenKey> _keys = new();
	public IReadOnlyList<TweenKey> Keys => _keys;

	public void Add( TweenKey key )
	{
		if ( key is null ) throw new ArgumentNullException( nameof( key ) );
		var i = _keys.FindIndex( k => k.Frame == key.Frame );
		if ( i >= 0 ) { _keys[i] = key; return; }
		var at = _keys.FindIndex( k => k.Frame > key.Frame );
		if ( at < 0 ) _keys.Add( key ); else _keys.Insert( at, key );
	}

	public float Evaluate( float frame )
	{
		if ( _keys.Count == 0 ) return 0f;
		if ( frame <= _keys[0].Frame ) return _keys[0].Value;
		if ( frame >= _keys[^1].Frame ) return _keys[^1].Value;
		for ( var i = 0; i < _keys.Count - 1; i++ )
		{
			var a = _keys[i]; var b = _keys[i + 1];
			if ( frame < a.Frame || frame > b.Frame ) continue;
			var span = b.Frame - a.Frame;
			var t = span > 0 ? (frame - a.Frame) / span : 0f;
			var prev = i > 0 ? _keys[i - 1] : a;
			var next = i + 2 < _keys.Count ? _keys[i + 2] : b;
			return a.Value + (b.Value - a.Value) * SegmentProgress( a, prev, b, next, t );
		}
		return _keys[^1].Value;
	}

	/// <summary>Sample every integer frame from first key to last key, inclusive.</summary>
	public List<float> Bake() =>
		_keys.Count == 0 ? new List<float>()
		: Enumerable.Range( _keys[0].Frame, _keys[^1].Frame - _keys[0].Frame + 1 )
			.Select( f => Evaluate( f ) ).ToList();

	/// <summary>Progress (0..1, or outside it for unclamped TCB) across one segment.
	/// Shared by scalar tracks and by rotation, so a rotation segment and a scalar
	/// segment with the same keys ease identically; only the delta differs (wrapped
	/// for rotation, straight for scalars).</summary>
	internal static float SegmentProgress( TweenKey a, TweenKey prev, TweenKey b, TweenKey next, float t )
	{
		switch ( a.Out )
		{
			case TweenEase.Constant:
			case TweenEase.Stepped: return 0f;
			case TweenEase.Linear: return RigKey.Apply( RigEase.Linear, t );
			case TweenEase.Smooth: return RigKey.Apply( RigEase.Smooth, t );
			case TweenEase.EaseIn: return RigKey.Apply( RigEase.EaseIn, t );
			case TweenEase.EaseOut: return RigKey.Apply( RigEase.EaseOut, t );
			case TweenEase.Bezier: return BezierY( t, a.OutX, a.OutY, b.InX, b.InY );
			case TweenEase.Tcb:
			case TweenEase.Clamped: return HermiteFactor( a, prev, b, next, t, a.Out == TweenEase.Clamped );
			default: return t;
		}
	}

	// Kochanek-Bartels: a key's OUTGOING tangent leaves it, its INCOMING tangent arrives
	// at it; the segment uses a's outgoing and b's incoming. Tangents start as per-frame
	// slopes (neighbour deltas over neighbour frame spans, so uneven key spacing does not
	// bend the curve) and are scaled by this segment's frame span into per-unit-t
	// derivatives for the Hermite basis. At the ends there is no neighbour, so the segment
	// is EXTRAPOLATED (same delta, one more span) instead of reusing the key itself —
	// reusing the key halves the end tangent and bends even a collinear line, which is
	// exactly the bug a "through collinear keys is linear" test caught.
	static float HermiteFactor( TweenKey a, TweenKey prev, TweenKey b, TweenKey next, float t, bool clamped )
	{
		var span0 = Math.Max( 1, b.Frame - a.Frame );
		if ( prev.Frame == a.Frame )
			prev = new TweenKey( a.Frame - span0, a.Value - (b.Value - a.Value) );
		if ( next.Frame == b.Frame )
			next = new TweenKey( b.Frame + span0, b.Value + (b.Value - a.Value) );
		float SlopeIn( TweenKey key, TweenKey p, TweenKey n )
		{
			var sPrev = p.Frame == key.Frame ? 0f : (key.Value - p.Value) / Math.Max( 1, key.Frame - p.Frame );
			var sNext = n.Frame == key.Frame ? 0f : (n.Value - key.Value) / Math.Max( 1, n.Frame - key.Frame );
			return (1f - key.Tension) * 0.5f * ((1f + key.Continuity) * (1f + key.Bias) * sPrev
				+ (1f - key.Continuity) * (1f - key.Bias) * sNext);
		}
		float SlopeOut( TweenKey key, TweenKey p, TweenKey n )
		{
			var sPrev = p.Frame == key.Frame ? 0f : (key.Value - p.Value) / Math.Max( 1, key.Frame - p.Frame );
			var sNext = n.Frame == key.Frame ? 0f : (n.Value - key.Value) / Math.Max( 1, n.Frame - key.Frame );
			return (1f - key.Tension) * 0.5f * ((1f - key.Continuity) * (1f + key.Bias) * sPrev
				+ (1f + key.Continuity) * (1f - key.Bias) * sNext);
		}
		var span = Math.Max( 1, b.Frame - a.Frame );
		var mA = SlopeOut( a, prev, b ) * span;
		var mB = SlopeIn( b, a, next ) * span;
		if ( clamped )
		{
			var delta = b.Value - a.Value;
			mA = ClampTangent( mA, delta );
			mB = ClampTangent( mB, delta );
		}
		var t2 = t * t; var t3 = t2 * t;
		var h00 = 2f * t3 - 3f * t2 + 1f;
		var h10 = t3 - 2f * t2 + t;
		var h01 = -2f * t3 + 3f * t2;
		var h11 = t3 - t2;
		var delta2 = b.Value - a.Value;
		if ( MathF.Abs( delta2 ) < 1e-9f ) return t;
		return (h00 * a.Value + h10 * mA + h01 * b.Value + h11 * mB - a.Value) / delta2;
	}

	static float ClampTangent( float m, float delta )
	{
		if ( delta == 0f ) return 0f;
		if ( MathF.Sign( m ) != MathF.Sign( delta ) ) return 0f;
		return Math.Clamp( m, -3f * MathF.Abs( delta ), 3f * MathF.Abs( delta ) )
			* MathF.Sign( delta );
	}

	// Cubic Bezier y at parameter x=t: solve x(u)=t by bisection (monotone in x for sane
	// handles; 24 iterations is far past float precision), then read y(u).
	static float BezierY( float t, float x1, float y1, float x2, float y2 )
	{
		if ( t <= 0f ) return 0f;
		if ( t >= 1f ) return 1f;
		float X( float u ) => 3f * (1f - u) * (1f - u) * u * x1 + 3f * (1f - u) * u * u * x2 + u * u * u;
		float Y( float u ) => 3f * (1f - u) * (1f - u) * u * y1 + 3f * (1f - u) * u * u * y2 + u * u * u;
		var lo = 0f; var hi = 1f;
		for ( var k = 0; k < 24; k++ )
		{
			var mid = (lo + hi) * 0.5f;
			if ( X( mid ) < t ) lo = mid; else hi = mid;
		}
		return Math.Clamp( Y( (lo + hi) * 0.5f ), 0f, 1f );
	}
}

/// <summary>A 2D part's whole transform at a frame: position, rotation in DEGREES (the 2D
/// canvas unit — a RigTrack's radians/quaternion never leaks into a 2D track), uniform
/// per-axis scale, opacity clamped to 0..1 on evaluation.</summary>
public readonly record struct TweenTransform(
	float X, float Y, float RotationDeg, float ScaleX, float ScaleY, float Opacity )
{
	public static readonly TweenTransform Identity = new( 0f, 0f, 0f, 1f, 1f, 1f );
}

/// <summary>
/// Six scalar tracks, one per transform channel, keyed together by frame. Rotation
/// interpolates by SHORTEST ARC (350° -> 10° passes through 0°, not backwards through
/// 180°); every other channel lerps straight. A missing channel track holds its
/// identity value (0 for position/rotation, 1 for scale/opacity).
/// </summary>
public sealed class TweenTransformTrack
{
	public string Name = "";
	public TweenTrack X = new(), Y = new(), Rotation = new(), ScaleX = new(), ScaleY = new(), Opacity = new();

	public void Key( int frame, TweenTransform value, TweenEase ease = TweenEase.Linear )
	{
		X.Add( new TweenKey( frame, value.X ) { Out = ease, In = ease } );
		Y.Add( new TweenKey( frame, value.Y ) { Out = ease, In = ease } );
		Rotation.Add( new TweenKey( frame, value.RotationDeg ) { Out = ease, In = ease } );
		ScaleX.Add( new TweenKey( frame, value.ScaleX ) { Out = ease, In = ease } );
		ScaleY.Add( new TweenKey( frame, value.ScaleY ) { Out = ease, In = ease } );
		Opacity.Add( new TweenKey( frame, value.Opacity ) { Out = ease, In = ease } );
	}

	public TweenTransform Evaluate( float frame )
	{
		var rot = Rotation.Keys.Count == 0 ? 0f : ShortestArcEvaluate( frame );
		return new TweenTransform(
			X.Evaluate( frame ), Y.Evaluate( frame ), rot,
			ScaleX.Keys.Count == 0 ? 1f : ScaleX.Evaluate( frame ),
			ScaleY.Keys.Count == 0 ? 1f : ScaleY.Evaluate( frame ),
			Opacity.Keys.Count == 0 ? 1f : Math.Clamp( Opacity.Evaluate( frame ), 0f, 1f ) );
	}

	float ShortestArcEvaluate( float frame )
	{
		var keys = Rotation.Keys;
		if ( frame <= keys[0].Frame ) return keys[0].Value;
		if ( frame >= keys[^1].Frame ) return keys[^1].Value;
		for ( var i = 0; i < keys.Count - 1; i++ )
		{
			var a = keys[i]; var b = keys[i + 1];
			if ( frame < a.Frame || frame > b.Frame ) continue;
			var span = b.Frame - a.Frame;
			var t = span > 0 ? (frame - a.Frame) / span : 0f;
			// Same segment progress as a scalar track (including TCB neighbours and
			// Bezier handles), applied to the wrapped delta instead of the raw one.
			var prev = i > 0 ? keys[i - 1] : a;
			var next = i + 2 < keys.Count ? keys[i + 2] : b;
			var f = TweenTrack.SegmentProgress( a, prev, b, next, t );
			var delta = WrapDeg( b.Value - a.Value );
			return a.Value + delta * f;
		}
		return keys[^1].Value;
	}

	static float WrapDeg( float d )
	{
		d %= 360f;
		if ( d > 180f ) d -= 360f;
		if ( d < -180f ) d += 360f;
		return d;
	}
}

/// <summary>
/// Deterministic shape in-betweening: a stroke is an ordered point list (2D canvas points,
/// y-down like the rest of the 2D suite). Interpolating two strokes REQUIRES identical
/// point counts — Grease Pencil's interpolate has the same rule, because with different
/// counts there is no honest correspondence, only a guess. Widths, when present, must
/// match the point count and interpolate alongside.
/// </summary>
public static class TweenShape
{
	public static List<Vec2> Interpolate( IReadOnlyList<Vec2> a, IReadOnlyList<Vec2> b, float t, RigEase ease = RigEase.Linear )
	{
		if ( a is null || b is null ) throw new ArgumentNullException( a is null ? nameof( a ) : nameof( b ) );
		if ( a.Count != b.Count )
			throw new ArgumentException( $"Stroke in-betweening needs equal point counts, got {a.Count} and {b.Count} — resampling is a correspondence pass, not a silent guess." );
		var f = RigKey.Apply( ease, Math.Clamp( t, 0f, 1f ) );
		var outPts = new List<Vec2>( a.Count );
		for ( var i = 0; i < a.Count; i++ )
			outPts.Add( new Vec2( a[i].x + (b[i].x - a[i].x) * f, a[i].y + (b[i].y - a[i].y) * f ) );
		return outPts;
	}

	public static List<float> InterpolateWidths( IReadOnlyList<float> a, IReadOnlyList<float> b, float t, RigEase ease = RigEase.Linear )
	{
		if ( a is null || b is null ) throw new ArgumentNullException( a is null ? nameof( a ) : nameof( b ) );
		if ( a.Count != b.Count )
			throw new ArgumentException( $"Width in-betweening needs equal counts, got {a.Count} and {b.Count}." );
		var f = RigKey.Apply( ease, Math.Clamp( t, 0f, 1f ) );
		var result = new List<float>( a.Count );
		for ( var i = 0; i < a.Count; i++ ) result.Add( a[i] + (b[i] - a[i]) * f );
		return result;
	}
}
