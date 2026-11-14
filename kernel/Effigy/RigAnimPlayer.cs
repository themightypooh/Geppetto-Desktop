using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// One point on a time-remap curve: at <see cref="Output"/> seconds of played (output) time,
/// the clip is at <see cref="Source"/> seconds of source-clip time. Output times must increase
/// strictly along the curve; source times may rise (normal/slow/fast), stay flat (hold/freeze)
/// or fall (reverse).
/// </summary>
public readonly record struct TimeRemapPoint( float Output, float Source );

/// <summary>
/// Time remap for an animation clip: a piecewise-linear map from output (played) time to
/// source-clip time, plus the inverse used when baking a remapped clip.
///
/// A 2-second clip remapped to 4 seconds plays at half speed: the curve runs (0, 0) to
/// (4, 2), so output second 2 reads source second 1. A hold is a flat run of source time
/// over output time; reverse runs source time backwards. The source clip's keys are never
/// altered by a remap — playback reads through <see cref="MapOutputToSource"/> and
/// The bake (ClipEditService.BakeRemappedClip) writes a new clip with retimed keys.
/// </summary>
public sealed class ClipTimeRemap
{
	public List<TimeRemapPoint> Points { get; } = new();

	/// <summary>Source-clip length the curve covers, in seconds.</summary>
	public float SourceDuration { get; }

	/// <summary>Played length of the remapped clip, in seconds (the last point's output time).</summary>
	public float OutputDuration => Points.Count == 0 ? 0f : Points[^1].Output;

	ClipTimeRemap( List<TimeRemapPoint> points, float sourceDuration )
	{
		Points = points;
		SourceDuration = sourceDuration;
	}

	static float RequireFinitePositive( float value, string name )
	{
		if ( !float.IsFinite( value ) || value <= 0f )
			throw new ArgumentException( $"{name} must be a finite number above zero.", name );
		return value;
	}

	static List<TimeRemapPoint> ValidatePoints( IEnumerable<TimeRemapPoint> points, float sourceDuration )
	{
		RequireFinitePositive( sourceDuration, nameof( sourceDuration ) );
		var list = new List<TimeRemapPoint>( points ?? throw new ArgumentNullException( nameof( points ) ) );
		if ( list.Count < 2 )
			throw new ArgumentException( "A time remap needs at least two points.", nameof( points ) );
		if ( list[0].Output != 0f )
			throw new ArgumentException( "A time remap must start at output time 0.", nameof( points ) );
		for ( var i = 0; i < list.Count; i++ )
		{
			if ( !float.IsFinite( list[i].Output ) || !float.IsFinite( list[i].Source ) )
				throw new ArgumentException( "Remap points must be finite numbers.", nameof( points ) );
			if ( list[i].Output < 0f || list[i].Source < 0f )
				throw new ArgumentException( "Remap times cannot be negative.", nameof( points ) );
			if ( i > 0 && list[i].Output <= list[i - 1].Output )
				throw new ArgumentException( "Remap output times must increase strictly.", nameof( points ) );
		}
		return list;
	}

	/// <summary>Constant-speed remap: <paramref name="speed"/> is source seconds per output
	/// second, so 0.5 plays a 2-second clip over 4 output seconds and 2.0 plays it over 1.</summary>
	public static ClipTimeRemap ConstantSpeed( float speed, float sourceDuration )
	{
		RequireFinitePositive( speed, nameof( speed ) );
		RequireFinitePositive( sourceDuration, nameof( sourceDuration ) );
		return new ClipTimeRemap( new List<TimeRemapPoint>
		{
			new( 0f, 0f ),
			new( sourceDuration / speed, sourceDuration ),
		}, sourceDuration );
	}

	/// <summary>Ease in and out: the clip starts and ends slowly and runs fastest in the middle
	/// (smoothstep over 16 linear segments; the endpoints are exact).</summary>
	public static ClipTimeRemap EaseInOut( float sourceDuration, float outputDuration )
	{
		RequireFinitePositive( sourceDuration, nameof( sourceDuration ) );
		RequireFinitePositive( outputDuration, nameof( outputDuration ) );
		const int segments = 16;
		var points = new List<TimeRemapPoint>( segments + 1 );
		for ( var i = 0; i <= segments; i++ )
		{
			var t = (float)i / segments;
			var eased = t * t * ( 3f - 2f * t );
			points.Add( new TimeRemapPoint( outputDuration * t, sourceDuration * eased ) );
		}
		return new ClipTimeRemap( points, sourceDuration );
	}

	/// <summary>Freeze the clip at <paramref name="sourceTime"/> for
	/// <paramref name="holdDuration"/> output seconds, then resume. The total output length
	/// grows by the hold; source time after the freeze is unchanged.</summary>
	public static ClipTimeRemap Hold( float sourceTime, float holdDuration, float sourceDuration )
	{
		RequireFinitePositive( sourceDuration, nameof( sourceDuration ) );
		RequireFinitePositive( holdDuration, nameof( holdDuration ) );
		if ( !float.IsFinite( sourceTime ) || sourceTime < 0f || sourceTime > sourceDuration )
			throw new ArgumentException( "Hold time must lie inside the source clip.", nameof( sourceTime ) );
		return new ClipTimeRemap( new List<TimeRemapPoint>
		{
			new( 0f, 0f ),
			new( sourceTime, sourceTime ),
			new( sourceTime + holdDuration, sourceTime ),
			new( sourceDuration + holdDuration, sourceDuration ),
		}, sourceDuration );
	}

	/// <summary>Play the clip backwards over its own length.</summary>
	public static ClipTimeRemap Reverse( float duration )
	{
		RequireFinitePositive( duration, nameof( duration ) );
		return new ClipTimeRemap( new List<TimeRemapPoint>
		{
			new( 0f, duration ),
			new( duration, 0f ),
		}, duration );
	}

	/// <summary>An explicit curve. Points must start at output 0 with strictly increasing
	/// output times; <paramref name="sourceDuration"/> is the source length the curve covers.</summary>
	public static ClipTimeRemap FromPoints( IEnumerable<TimeRemapPoint> points, float sourceDuration ) =>
		new( ValidatePoints( points, sourceDuration ), sourceDuration );

	/// <summary>Source-clip time playing at <paramref name="outputSeconds"/> of output time.
	/// Clamped to the curve's ends outside its range.</summary>
	public float MapOutputToSource( float outputSeconds )
	{
		if ( Points.Count == 0 )
			return outputSeconds;
		if ( outputSeconds <= Points[0].Output )
			return Points[0].Source;
		if ( outputSeconds >= Points[^1].Output )
			return Points[^1].Source;
		for ( var i = 0; i < Points.Count - 1; i++ )
		{
			var a = Points[i];
			var b = Points[i + 1];
			if ( outputSeconds < a.Output || outputSeconds > b.Output )
				continue;
			var span = b.Output - a.Output;
			var t = span > 0f ? ( outputSeconds - a.Output ) / span : 0f;
			return a.Source + ( b.Source - a.Source ) * t;
		}
		return Points[^1].Source;
	}

	/// <summary>Output time at which <paramref name="sourceSeconds"/> of source time plays.
	/// A source time inside a hold maps to the hold's start (the first output moment that
	/// shows it); outside the covered source range the nearest end is returned.</summary>
	public float MapSourceToOutput( float sourceSeconds )
	{
		if ( Points.Count == 0 )
			return sourceSeconds;
		for ( var i = 0; i < Points.Count - 1; i++ )
		{
			var a = Points[i];
			var b = Points[i + 1];
			var lo = MathF.Min( a.Source, b.Source );
			var hi = MathF.Max( a.Source, b.Source );
			if ( sourceSeconds < lo || sourceSeconds > hi )
				continue;
			var span = b.Source - a.Source;
			if ( MathF.Abs( span ) < 1e-9f )
				return a.Output;
			var t = ( sourceSeconds - a.Source ) / span;
			return a.Output + ( b.Output - a.Output ) * t;
		}
		var first = Points[0];
		var last = Points[^1];
		return MathF.Abs( sourceSeconds - first.Source ) <= MathF.Abs( sourceSeconds - last.Source )
			? first.Output
			: last.Output;
	}

	/// <summary>Flat runs of the curve — output spans over which source time does not advance
	/// (a freeze). Each is (source time held, output start, output end), in seconds.</summary>
	public IReadOnlyList<(float Source, float OutputStart, float OutputEnd)> HoldSegments()
	{
		var holds = new List<(float, float, float)>();
		for ( var i = 0; i < Points.Count - 1; i++ )
		{
			var a = Points[i];
			var b = Points[i + 1];
			if ( b.Source == a.Source && b.Output > a.Output )
				holds.Add( (a.Source, a.Output, b.Output) );
		}
		return holds;
	}

	/// <summary>Local output-seconds per source-second at <paramref name="sourceSeconds"/>,
	/// for scaling frame spans (morph-event fades, channel-curve modifier periods) when baking.
	/// 1 is unscaled; 0 inside a hold.</summary>
	public float SlopeAtSource( float sourceSeconds )
	{
		const float e = 0.001f;
		var before = MapSourceToOutput( MathF.Max( 0f, sourceSeconds - e ) );
		var after = MapSourceToOutput( sourceSeconds + e );
		var span = MathF.Min( sourceSeconds + e, SourceDuration ) - MathF.Max( 0f, sourceSeconds - e );
		if ( span <= 0f )
			return 1f;
		// Magnitude: reverse runs output backwards as source advances, but a fade span
		// still scales by how fast output moves, so the sign is not the fade's business.
		return Math.Clamp( MathF.Abs( ( after - before ) / span ), 0f, float.MaxValue );
	}

	/// <summary>
	/// Time remap, baked: a NEW clip whose keys play <paramref name="source"/> through this
	/// remap, without altering the source clip's keys.
	///
	/// Every time-keyed value is retimed through the remap — track keys, channel-curve keys,
	/// morph events (start/end retimed, fades scaled by the local rate at each end) and marks —
	/// and the clip length becomes the remap's output length. The result is written out with
	/// <c>RigAnimWriter</c>; the source clip is never touched.
	///
	/// Two source keys that land on one output frame keep the LATER source key, as a key Move
	/// does on a collision. A hold is different: retiming alone would let the baked clip
	/// interpolate through the frozen span, so the bake materialises each hold as a frozen key
	/// at both ends of the span (the source pose at the held time) and drops keys strictly
	/// inside it, which are never visible while time is frozen. Morph-event weights inside a
	/// hold are retimed, not frozen — an approximation, documented here rather than hidden.
	///
	/// Time-independent rig data (IK, limits, collision, drivers, constraints, goal spaces,
	/// objects, props, root motion) is shared with the source, not copied: sampling never
	/// mutates it, and a baked clip is the same rig playing retimed keys. Channel-curve
	/// modifier periods are scaled by the AVERAGE rate only — a period is a rate, and the local
	/// rate varies under ease/hold, so a modifier on a remapped clip is approximate. Cubic /
	/// Monotone key tangents are carried as authored, not re-scaled, exactly as a key Move
	/// carries them.
	/// </summary>
	public RigAnimFile Bake( RigAnimFile source )
	{
		ArgumentNullException.ThrowIfNull( source );

		var fps = Math.Max( 1, source.FrameRate );
		int MapFrame( int frame ) => Math.Max( 0, (int)Math.Round(
			MapSourceToOutput( frame / (float)fps ) * fps, MidpointRounding.AwayFromZero ) );
		var averageRate = SourceDuration > 0f ? OutputDuration / SourceDuration : 1f;

		var baked = new RigAnimFile
		{
			SourceModel = source.SourceModel,
			RigAsset = source.RigAsset,
			FrameRate = source.FrameRate,
			FrameCount = Math.Max( 1, (int)Math.Round( OutputDuration * fps, MidpointRounding.AwayFromZero ) ),
			PlayInPlace = source.PlayInPlace,
			RootMotion = source.RootMotion,
			Ik = source.Ik,
			Limits = source.Limits,
			Collision = source.Collision,
			Drivers = source.Drivers,
			Constraints = source.Constraints,
			GoalSpaces = source.GoalSpaces,
			Objects = source.Objects,
			Props = source.Props,
		};

		var holds = HoldSegments();
		int HoldFrame( float outputSeconds ) => Math.Max( 0, (int)Math.Round(
			outputSeconds * fps, MidpointRounding.AwayFromZero ) );
		bool InsideHold( int outputFrame )
		{
			foreach ( var (_, start, end) in holds )
				if ( outputFrame > HoldFrame( start ) && outputFrame < HoldFrame( end ) )
					return true;
			return false;
		}

		foreach ( var track in source.Tracks )
		{
			var retimed = new RigTrack { Name = track.Name, Kind = track.Kind };

			var orderedKeys = track.Keys
				.Select( ( key, index ) => (Key: CloneKey( key ), Index: index, Frame: MapFrame( key.Frame ) ) )
				.Where( x => !InsideHold( x.Frame ) )
				.OrderBy( x => x.Frame )
				.ThenBy( x => x.Index )
				.ToList();
			var keysByFrame = new Dictionary<int, RigKey>();
			foreach ( var (key, _, frame) in orderedKeys )
			{
				key.Frame = frame;
				keysByFrame[frame] = key;
			}

			// A freeze holds one pose: materialise it as keys at both ends of the span, or the
			// baked clip would interpolate through the frozen span instead of holding. The
			// frozen key wins a collision at either end — the span shows the held pose.
			foreach ( var (holdSource, outputStart, outputEnd) in holds )
			{
				var frozen = track.Evaluate( holdSource * fps );
				foreach ( var outputFrame in new[] { HoldFrame( outputStart ), HoldFrame( outputEnd ) } )
					keysByFrame[outputFrame] = new RigKey { Frame = outputFrame, Local = frozen, Ease = RigEase.Linear };
			}
			retimed.Keys.AddRange( keysByFrame.OrderBy( kv => kv.Key ).Select( kv => kv.Value ) );

			foreach ( var (channel, curve) in track.ChannelCurves )
			{
				var orderedCurve = curve
					.Select( ( key, index ) => (Key: key, Index: index, Frame: MapFrame( key.Frame ) ) )
					.Where( x => !InsideHold( x.Frame ) )
					.OrderBy( x => x.Frame )
					.ThenBy( x => x.Index )
					.ToList();
				var curveByFrame = new Dictionary<int, ScalarKey>();
				foreach ( var (key, _, frame) in orderedCurve )
					curveByFrame[frame] = new ScalarKey( frame, key.Value );
				foreach ( var (holdSource, outputStart, outputEnd) in holds )
				{
					var frozenValue = ScalarAt( curve, holdSource * fps );
					foreach ( var outputFrame in new[] { HoldFrame( outputStart ), HoldFrame( outputEnd ) } )
						curveByFrame[outputFrame] = new ScalarKey( outputFrame, frozenValue );
				}
				retimed.ChannelCurves[channel] = curveByFrame
					.OrderBy( kv => kv.Key ).Select( kv => kv.Value ).ToList();
			}

			foreach ( var modifier in track.Modifiers )
				retimed.Modifiers.Add( new FCurveModifier( modifier.Kind, modifier.Amplitude,
					Math.Max( 0.001f, modifier.Period * averageRate ), modifier.Phase, modifier.Steps ) );

			baked.Tracks.Add( retimed );
		}

		foreach ( var e in source.MorphEvents )
		{
			var start = MapFrame( e.Start );
			baked.MorphEvents.Add( new RigMorphEvent
			{
				Morph = e.Morph,
				Start = start,
				End = Math.Max( start, MapFrame( e.End ) ),
				FadeIn = Math.Max( 0, (int)Math.Round( e.FadeIn * SlopeAtSource( e.Start / (float)fps ), MidpointRounding.AwayFromZero ) ),
				FadeOut = Math.Max( 0, (int)Math.Round( e.FadeOut * SlopeAtSource( e.End / (float)fps ), MidpointRounding.AwayFromZero ) ),
				Value = e.Value,
			} );
		}

		if ( source.Marks is not null )
			baked.Marks = source.Marks
				.Where( m => m is not null )
				.Select( m =>
				{
					var copy = m.Clone();
					copy.Frame = MapFrame( m.Frame );
					return copy;
				} )
				.ToList();

		return baked;
	}

	static RigKey CloneKey( RigKey key ) => new()
	{
		Frame = key.Frame,
		Local = key.Local,
		Ease = key.Ease,
		TangentIn = (float[])key.TangentIn?.Clone(),
		TangentOut = (float[])key.TangentOut?.Clone(),
	};

	/// <summary>Scalar channel-curve value at a fractional frame: held outside the keys,
	/// linearly interpolated between them — the scalar half of RigTrack.Evaluate.</summary>
	static float ScalarAt( List<ScalarKey> curve, float frame )
	{
		if ( curve.Count == 0 )
			return 0f;
		if ( frame <= curve[0].Frame )
			return curve[0].Value;
		if ( frame >= curve[^1].Frame )
			return curve[^1].Value;
		for ( var i = 0; i < curve.Count - 1; i++ )
		{
			var a = curve[i];
			var b = curve[i + 1];
			if ( frame < a.Frame || frame > b.Frame )
				continue;
			var span = b.Frame - a.Frame;
			var t = span > 0 ? ( frame - a.Frame ) / span : 0f;
			return a.Value + ( b.Value - a.Value ) * t;
		}
		return curve[0].Value;
	}
}

public class RigAnimPlayer
{
	public List<string> Bones { get; } = new List<string>();
	public List<object> Frames { get; } = new List<object>();

	/// <summary>Time remap applied by <see cref="MapTime"/> when playing. Null means the clip
	/// plays unmapped. The real playback path (<c>MarionettePlayer</c>) applies the same map
	/// to its sampled frame; see the integrator note in the task handoff.</summary>
	public ClipTimeRemap? Remap { get; set; }

	/// <summary>Source-clip time for <paramref name="outputSeconds"/> of played time.</summary>
	public float MapTime( float outputSeconds ) =>
		Remap is null ? outputSeconds : Remap.MapOutputToSource( outputSeconds );

	public void Play( string boneName )
	{
		if ( string.IsNullOrEmpty( boneName ) ) return;
		if ( !Bones.Contains( boneName ) ) return;
	}

	public void Update( float dt )
	{
		if ( Frames is null || Frames.Count == 0 ) return;
	}

	public void Stop()
	{
		Frames.Clear();
	}
}
