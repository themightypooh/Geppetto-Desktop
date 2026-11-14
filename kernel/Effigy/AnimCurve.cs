using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>How one segment of an <see cref="AnimCurve"/> interpolates to the next key.</summary>
public enum AnimTangent
{
	/// <summary>Cubic Bezier through the keys, with weighted handles (the Graph editor's
	/// default: TangentOut/TangentIn are slopes in value per frame, OutWeight/InWeight
	/// are handle lengths as fractions of the segment span).</summary>
	Bezier,

	/// <summary>Straight line between the keys; slopes and weights are ignored.</summary>
	Linear,

	/// <summary>Hold the left key's value until the next key. Stepped is the same hold
	/// (Blender's Constant and Maya's stepped tangents agree here); both are kept so a
	/// curve authored in either convention round-trips without silent retiming.</summary>
	Constant,

	/// <summary>Same hold as Constant: the value jumps at the key, never between keys.</summary>
	Stepped
}

/// <summary>One key on an <see cref="AnimCurve"/>: a frame, a scalar value, and the Bezier
/// handle that leaves it (TangentOut/OutWeight) and arrives at it (TangentIn/InWeight).
/// Slopes are value per frame; weights are fractions of the neighbouring segment span,
/// clamped to 0..1 when the curve evaluates, so a handle can never cross the next key.</summary>
public sealed class AnimCurveKey
{
	public int Frame;
	public float Value;
	public AnimTangent Tangent = AnimTangent.Bezier;
	public float TangentOut;
	public float TangentIn;
	public float OutWeight = 1f / 3f;
	public float InWeight = 1f / 3f;

	public AnimCurveKey Clone() => (AnimCurveKey)MemberwiseClone();
}

/// <summary>
/// The Graph editor's curve: one scalar channel (a <see cref="RigChannel"/> of one track)
/// as editable keys with Bezier/Linear/Constant/Stepped tangents, weighted handles, ease
/// presets, F-curve modifiers and mocap filtering.
///
/// This is the piece <see cref="RigTrack.ChannelCurves"/> does not have: channel curves
/// are linear-only scalar keys, and pose keys carry their tangents through
/// <see cref="RigHermite"/> instead. An AnimCurve is the editable master; it converts to
/// and from <see cref="ScalarKey"/> lists (<see cref="ToScalarKeys"/> /
/// <see cref="FromScalarKeys"/>) so a graphed channel feeds the real evaluation path
/// (<see cref="RigTrack.EvaluateWithChannels"/>) instead of living in a parallel universe.
/// The conversion is lossy by design — channel curves hold no tangents — and says so.
///
/// Evaluation is a pure function of the frame: binary search for the segment, then the
/// segment's tangent rule. Moving many keys in one op is one pass plus a sort, which is
/// what the "300+ keys moved in one op completes instantly" bar measures.
/// </summary>
public sealed class AnimCurve
{
	public string Name = "";
	public List<AnimCurveKey> Keys = new();

	/// <summary>Non-destructive modifiers, applied in list order: Cycle and SteppedQuantize
	/// warp the frame before sampling (as <see cref="RigTrack"/> does), SinWave, PerlinNoise
	/// and SpringOvershoot adjust the sampled value after.</summary>
	public List<FCurveModifier> Modifiers = new();

	/// <summary>The curve's value at a frame. Empty curves read 0; outside the keys the
	/// first/last value holds (a curve never extrapolates). Modifiers apply to held values
	/// too, exactly as RigTrack's channel sampling does.</summary>
	public float Evaluate( float frame )
	{
		if ( Keys.Count == 0 )
			return 0f;

		var t = WarpFrame( frame );
		float value;

		if ( Keys.Count == 1 || t <= Keys[0].Frame )
			value = Keys[0].Value;
		else if ( t >= Keys[^1].Frame )
			value = Keys[^1].Value;
		else
		{
			var i = SegmentIndex( t );
			value = SampleSegment( Keys[i], Keys[i + 1], t );
		}

		return ApplyValueModifiers( value, t );
	}

	float WarpFrame( float frame )
	{
		var t = frame;

		foreach ( var m in Modifiers )
		{
			if ( m is null )
				continue;

			if ( m.Kind == FCurveModifierKind.Cycle && m.Period > 0f )
				t = ((t % m.Period) + m.Period) % m.Period;
			else if ( m.Kind == FCurveModifierKind.SteppedQuantize && m.Steps >= 1 )
				t = MathF.Floor( t / m.Steps ) * m.Steps;
		}

		return t;
	}

	float ApplyValueModifiers( float value, float frame )
	{
		foreach ( var m in Modifiers )
		{
			if ( m is null )
				continue;

			switch ( m.Kind )
			{
				case FCurveModifierKind.SinWave:
					value += m.Amplitude * MathF.Sin( 2f * MathF.PI * frame / MathF.Max( m.Period, 1e-6f ) + m.Phase );
					break;

				case FCurveModifierKind.PerlinNoise:
					// Same deterministic hash-lattice smooth value noise as RigTrack: the name
					// is the roadmap's, the function is documented there, NOT gradient Perlin.
					value += m.Amplitude * (2f * ValueNoise( frame / MathF.Max( m.Period, 1e-6f ) + m.Phase ) - 1f);
					break;

				case FCurveModifierKind.SpringOvershoot:
					value += m.Amplitude * MathF.Exp( -frame / MathF.Max( m.Period, 1e-6f ) )
						* MathF.Sin( 2f * MathF.PI * frame / MathF.Max( m.Period, 1e-6f ) );
					break;
			}
		}

		return value;
	}

	static float ValueNoise( float x )
	{
		var i = MathF.Floor( x );
		var f = x - i;
		var u = f * f * (3f - 2f * f);
		return Hash( (int)i ) + (Hash( (int)i + 1 ) - Hash( (int)i )) * u;
	}

	static float Hash( int n )
	{
		var v = MathF.Sin( n * 127.1f ) * 43758.5453f;
		return v - MathF.Floor( v );
	}

	/// <summary>Index of the left key of the segment <paramref name="frame"/> falls in.
	/// Callers must have established the frame lies strictly inside the key range.</summary>
	int SegmentIndex( float frame )
	{
		var lo = 0;
		var hi = Keys.Count - 1;

		while ( lo < hi )
		{
			var mid = (lo + hi + 1) / 2;

			if ( Keys[mid].Frame <= frame )
				lo = mid;
			else
				hi = mid - 1;
		}

		return Math.Min( lo, Keys.Count - 2 );
	}

	float SampleSegment( AnimCurveKey a, AnimCurveKey b, float frame )
	{
		switch ( a.Tangent )
		{
			case AnimTangent.Linear:
			{
				var span = b.Frame - a.Frame;
				if ( span <= 0 )
					return a.Value;
				var u = (frame - a.Frame) / span;
				return a.Value + (b.Value - a.Value) * u;
			}

			case AnimTangent.Constant:
			case AnimTangent.Stepped:
				return a.Value;

			default:
				return BezierSegment( a, b, frame );
		}
	}

	float BezierSegment( AnimCurveKey a, AnimCurveKey b, float frame )
	{
		var span = b.Frame - a.Frame;

		if ( span <= 0 )
			return a.Value;

		var wa = Math.Clamp( a.OutWeight, 0f, 1f );
		var wb = Math.Clamp( b.InWeight, 0f, 1f );
		var p1x = a.Frame + wa * span;
		var p1y = a.Value + a.TangentOut * wa * span;
		var p2x = b.Frame - wb * span;
		var p2y = b.Value - b.TangentIn * wb * span;

		// The frame coordinate is non-decreasing in t while the handles stay inside the
		// segment (weights clamped to 0..1), so bisection on t finds the frame exactly.
		// 32 rounds land far below float noise; no Newton step to diverge on flat runs.
		float lo = 0f, hi = 1f;

		for ( var i = 0; i < 32; i++ )
		{
			var mid = (lo + hi) * 0.5f;

			if ( Cubic( a.Frame, p1x, p2x, b.Frame, mid ) < frame )
				lo = mid;
			else
				hi = mid;
		}

		return Cubic( a.Value, p1y, p2y, b.Value, (lo + hi) * 0.5f );
	}

	static float Cubic( float p0, float p1, float p2, float p3, float t )
	{
		var u = 1f - t;
		return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
	}

	/// <summary>Move every key on one of <paramref name="frames"/> by <paramref name="delta"/>
	/// frames, in one pass. The delta is clamped so no key goes below frame 0; a moved key
	/// landing on an unmoved one replaces it (the same rule as ClipKeyEditor.Move and the
	/// remap bake), and moved keys keep their values, tangents and weights.</summary>
	public int MoveKeys( IReadOnlyList<int> frames, int delta )
	{
		var wanted = new HashSet<int>( frames ?? throw new ArgumentNullException( nameof( frames ) ) );
		var moving = Keys.Where( k => wanted.Contains( k.Frame ) ).ToList();

		if ( moving.Count == 0 || delta == 0 )
			return 0;

		delta = Math.Max( delta, -moving.Min( k => k.Frame ) );

		if ( delta == 0 )
			return 0;

		foreach ( var k in moving )
			Keys.Remove( k );

		var landed = moving.Select( k => k.Frame + delta ).ToHashSet();
		Keys.RemoveAll( k => landed.Contains( k.Frame ) );

		foreach ( var k in moving )
			k.Frame += delta;

		Keys.AddRange( moving );
		Keys.Sort( ( x, y ) => x.Frame.CompareTo( y.Frame ) );
		return delta;
	}

	/// <summary>Catmull-Rom slopes on every key (neighbour chord through the key). The curve
	/// passes through every key, but it can overshoot between them; the never-overshoots
	/// Monotone tangents live in <see cref="RigHermite"/> for pose keys, not here.</summary>
	public void AutoTangents()
	{
		for ( var i = 0; i < Keys.Count; i++ )
		{
			float slope;

			if ( Keys.Count == 1 )
				slope = 0f;
			else if ( i == 0 )
				slope = (Keys[1].Value - Keys[0].Value) / Math.Max( 1, Keys[1].Frame - Keys[0].Frame );
			else if ( i == Keys.Count - 1 )
				slope = (Keys[^1].Value - Keys[^2].Value) / Math.Max( 1, Keys[^1].Frame - Keys[^2].Frame );
			else
				slope = (Keys[i + 1].Value - Keys[i - 1].Value) / Math.Max( 1, Keys[i + 1].Frame - Keys[i - 1].Frame );

			Keys[i].TangentOut = slope;
			Keys[i].TangentIn = slope;
			Keys[i].Tangent = AnimTangent.Bezier;
		}
	}

	/// <summary>Mocap jitter reduction: a moving average over <paramref name="window"/>
	/// neighbouring values (odd, default 3). Endpoints are pinned — smoothing must never
	/// move the first or last key of a take.</summary>
	public void Smooth( int window = 3 )
	{
		if ( window < 1 || window % 2 == 0 )
			throw new ArgumentException( "Smooth window must be odd and at least 1.", nameof( window ) );

		if ( Keys.Count < 3 )
			return;

		var half = window / 2;
		var smoothed = Keys.Select( k => k.Value ).ToArray();

		for ( var i = 1; i < Keys.Count - 1; i++ )
		{
			var lo = Math.Max( 0, i - half );
			var hi = Math.Min( Keys.Count - 1, i + half );
			var sum = 0f;

			for ( var j = lo; j <= hi; j++ )
				sum += Keys[j].Value;

			smoothed[i] = sum / (hi - lo + 1);
		}

		for ( var i = 1; i < Keys.Count - 1; i++ )
			Keys[i].Value = smoothed[i];
	}

	/// <summary>Mocap spike removal: an interior key further than
	/// <paramref name="threshold"/> from the median of itself and its two neighbours is
	/// replaced by that median. Endpoints are never touched.</summary>
	public void Despike( float threshold )
	{
		if ( !float.IsFinite( threshold ) || threshold <= 0f )
			throw new ArgumentException( "Despike threshold must be a finite number above zero.", nameof( threshold ) );

		if ( Keys.Count < 3 )
			return;

		for ( var i = 1; i < Keys.Count - 1; i++ )
		{
			var values = new[] { Keys[i - 1].Value, Keys[i].Value, Keys[i + 1].Value };
			Array.Sort( values );
			var median = values[1];

			if ( MathF.Abs( Keys[i].Value - median ) > threshold )
				Keys[i].Value = median;
		}
	}

	/// <summary>The curve as plain scalar keys, for <see cref="RigTrack.ChannelCurves"/>.
	/// Tangents, weights and modifiers do not survive: channel curves are linear-only.</summary>
	public List<ScalarKey> ToScalarKeys() =>
		Keys.Select( k => new ScalarKey( k.Frame, k.Value ) ).ToList();

	/// <summary>Build a curve from plain scalar keys. Duplicate frames keep the LAST key
	/// (the same rule as a key Move landing on another key). New keys take
	/// <paramref name="tangent"/> with flat (zero-slope) Bezier handles.</summary>
	public static AnimCurve FromScalarKeys( IEnumerable<ScalarKey> keys, AnimTangent tangent = AnimTangent.Linear )
	{
		var curve = new AnimCurve();
		var byFrame = new Dictionary<int, ScalarKey>();

		foreach ( var key in keys ?? throw new ArgumentNullException( nameof( keys ) ) )
			byFrame[key.Frame] = key;

		foreach ( var key in byFrame.Values.OrderBy( k => k.Frame ) )
			curve.Keys.Add( new AnimCurveKey { Frame = key.Frame, Value = key.Value, Tangent = tangent } );

		return curve;
	}

	/// <summary>Two-key ease presets over [startFrame, endFrame]. Endpoints are exact by
	/// construction; EaseIn starts slow (midpoint below linear), EaseOut starts fast,
	/// EaseInOut is the smoothstep (midpoint exactly halfway).</summary>
	public static AnimCurve EaseInOut( int startFrame, int endFrame, float from, float to ) =>
		Eased( startFrame, endFrame, from, to, 0f, 0f );

	public static AnimCurve EaseIn( int startFrame, int endFrame, float from, float to )
	{
		var avg = (to - from) / Math.Max( 1, endFrame - startFrame );
		return Eased( startFrame, endFrame, from, to, 0f, 2f * avg );
	}

	public static AnimCurve EaseOut( int startFrame, int endFrame, float from, float to )
	{
		var avg = (to - from) / Math.Max( 1, endFrame - startFrame );
		return Eased( startFrame, endFrame, from, to, 2f * avg, 0f );
	}

	public static AnimCurve Linear( int startFrame, int endFrame, float from, float to ) =>
		new()
		{
			Keys =
			{
				new AnimCurveKey { Frame = startFrame, Value = from, Tangent = AnimTangent.Linear },
				new AnimCurveKey { Frame = endFrame, Value = to, Tangent = AnimTangent.Linear },
			},
		};

	static AnimCurve Eased( int startFrame, int endFrame, float from, float to, float outSlope, float inSlope ) =>
		new()
		{
			Keys =
			{
				new AnimCurveKey { Frame = startFrame, Value = from, Tangent = AnimTangent.Bezier, TangentOut = outSlope },
				new AnimCurveKey { Frame = endFrame, Value = to, Tangent = AnimTangent.Bezier, TangentIn = inSlope },
			},
		};

	/// <summary>Problems that make this curve untrustworthy, for a loud report instead of a
	/// silently wrong evaluation. Empty is a problem here (Evaluate reads it as 0).</summary>
	public List<string> Validate()
	{
		var notes = new List<string>();

		if ( Keys.Count == 0 )
			notes.Add( "no keys" );

		for ( var i = 0; i < Keys.Count; i++ )
		{
			var k = Keys[i];

			if ( k.Frame < 0 )
				notes.Add( $"key {i}: frame {k.Frame} is negative" );

			if ( !float.IsFinite( k.Value ) || !float.IsFinite( k.TangentOut ) || !float.IsFinite( k.TangentIn ) )
				notes.Add( $"key {i}: non-finite value or slope" );

			if ( k.OutWeight < 0f || k.OutWeight > 1f || k.InWeight < 0f || k.InWeight > 1f )
				notes.Add( $"key {i}: handle weight outside 0..1 (clamped when evaluating)" );

			if ( i > 0 && k.Frame <= Keys[i - 1].Frame )
				notes.Add( $"key {i}: frame {k.Frame} is not after {Keys[i - 1].Frame}" );
		}

		return notes;
	}
}
