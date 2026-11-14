using System;

namespace Effigy;

/// <summary>
/// Distribute a joint roll along a limb, so a wrist or ankle turn unwinds
/// gradually instead of pinching shut on one ring of vertices.
///
/// WHY THIS IS NOT <see cref="TwistWeights"/>. That class redistributes SKIN
/// WEIGHTS: a vertex's weight is split across the twist bones that share its
/// parent, by where the vertex sits along the bone. This class distributes
/// the ROLL ANGLE itself: a 90 degree wrist turn becomes 30 + 30 + 30 across
/// three rings instead of 90 on one ring. The two are companions, not rivals.
/// <see cref="TwistWeights"/> answers "which bone owns this vertex";
/// this answers "how much of the turn does each ring take". Neither touches
/// the other, and neither touches <see cref="SkeletonRetarget"/>, whose roll
/// conventions (Lane 7) this preserves by working in plain degrees on top of
/// whatever the retarget produced.
///
/// THE CANDY-WRAPPER FAILURE. A forearm or calf is a tube. Twist it 90
/// degrees at one cross-section and the tube collapses to a point there:
/// every vertex on that ring rotates together, the ring's area goes to
/// nothing, and the limb reads as pinched. Spread the same 90 degrees over
/// three rings at 30 each and no ring collapses, because 30 degrees of
/// twist across a short segment keeps the cross-section open. That is the
/// whole test: the largest single segment must stay small, and the segments
/// must sum back to exactly the turn that was asked for.
///
/// PROFILES, DOCUMENTED SHARES. All shares sum to 1, and the segment rolls
/// sum to the total roll (the last segment absorbs floating-point residue,
/// so the sum holds to within one ulp).
/// <list type="bullet">
/// <item><description>Uniform: every segment takes 1/n. Three segments of a
/// 90 degree turn take 30 each; the end segments carry 1/n each.</description></item>
/// <item><description>ByLength: a segment's share is its share of the limb's
/// length, from the station positions passed in. With equally spaced
/// stations this is identical to Uniform; it only differs on uneven
/// stations, where a longer segment rightly takes more of the turn.</description></item>
/// <item><description>Smooth: the cumulative twist follows a smoothstep
/// ramp, so the turn eases in and out. Four equal segments of a 90 degree
/// turn take 14.0625, 30.9375, 30.9375, 14.0625 degrees: the end segments
/// carry 15.625% each, the middle two 34.375% each.</description></item>
/// <item><description>DistalRamp: a segment's share grows toward the joint,
/// proportional to its index (1, 2, 3, ...). Three segments of a 90 degree
/// turn take 15, 30, 45 degrees: the distal (wrist/ankle-side) end segment
/// carries 50%.</description></item>
/// </list>
///
/// UNITS AND LIMITS. Degrees, not radians. This is angle arithmetic only:
/// it deforms no mesh, reweights no skin, and wires nothing into the anim
/// graph. The caller applies the returned segment rolls to its twist bones
/// or rings, in order from the proximal (elbow/knee-side) end to the distal
/// (wrist/ankle-side) end. A negative total is a turn the other way, and
/// its segments are negative by the same shares.
/// </summary>
public static class TwistDistribution
{
	/// <summary>How the total roll is shared between segments.</summary>
	public enum TwistProfile
	{
		/// <summary>Equal share per segment, regardless of segment length.</summary>
		Uniform,

		/// <summary>Share proportional to segment length (station spacing).</summary>
		ByLength,

		/// <summary>Smoothstep cumulative ramp: ends ease, middle carries most.</summary>
		Smooth,

		/// <summary>Share proportional to segment index (1, 2, 3, ...): distal-heavy.</summary>
		DistalRamp,
	}

	/// <summary>
	/// Split <paramref name="totalRollDegrees"/> across
	/// <paramref name="segmentCount"/> equally spaced segments.
	/// </summary>
	public static double[] Distribute( double totalRollDegrees, int segmentCount, TwistProfile profile = TwistProfile.Uniform )
	{
		if ( segmentCount < 1 )
			throw new ArgumentOutOfRangeException( nameof( segmentCount ), "At least one segment is required." );

		var stations = new double[segmentCount + 1];

		for ( var i = 0; i <= segmentCount; i++ )
			stations[i] = (double)i / segmentCount;

		return DistributeAlong( totalRollDegrees, stations, profile );
	}

	/// <summary>
	/// Split <paramref name="totalRollDegrees"/> across the intervals between
	/// <paramref name="stations"/>: normalised positions along the limb, head
	/// 0 to tail 1, strictly increasing. The returned rolls run proximal to
	/// distal and sum to the total.
	/// </summary>
	public static double[] DistributeAlong( double totalRollDegrees, double[] stations, TwistProfile profile = TwistProfile.ByLength )
	{
		if ( !double.IsFinite( totalRollDegrees ) )
			throw new ArgumentException( "Total roll must be finite.", nameof( totalRollDegrees ) );
		if ( stations is null )
			throw new ArgumentNullException( nameof( stations ) );
		if ( stations.Length < 2 )
			throw new ArgumentException( "At least two stations (head and tail) are required.", nameof( stations ) );
		if ( !Enum.IsDefined( profile ) )
			throw new ArgumentOutOfRangeException( nameof( profile ) );

		foreach ( var t in stations )
		{
			if ( !double.IsFinite( t ) )
				throw new ArgumentException( "Station positions must be finite.", nameof( stations ) );
		}

		// Endpoints are snapped, not demanded bit-exact: a caller that
		// computed its stations will be an ulp or two off 0 or 1, and
		// rejecting that would reject every real caller.
		if ( Math.Abs( stations[0] ) > 1e-6 || Math.Abs( stations[^1] - 1.0 ) > 1e-6 )
			throw new ArgumentException( "Stations must run from 0 (head) to 1 (tail).", nameof( stations ) );

		for ( var i = 1; i < stations.Length; i++ )
		{
			if ( stations[i] <= stations[i - 1] )
				throw new ArgumentException( "Stations must be strictly increasing.", nameof( stations ) );
		}

		var count = stations.Length - 1;
		var shares = new double[count];

		switch ( profile )
		{
			case TwistProfile.Uniform:
				for ( var i = 0; i < count; i++ )
					shares[i] = 1.0 / count;
				break;

			case TwistProfile.ByLength:
				for ( var i = 0; i < count; i++ )
					shares[i] = stations[i + 1] - stations[i];
				break;

			case TwistProfile.Smooth:
				for ( var i = 0; i < count; i++ )
					shares[i] = Smoothstep( stations[i + 1] ) - Smoothstep( stations[i] );
				break;

			case TwistProfile.DistalRamp:
				for ( var i = 0; i < count; i++ )
					shares[i] = i + 1;
				break;
		}

		// Normalise so the shares sum to exactly 1 in intent; the segment
		// rolls below then sum to the total by construction.
		var shareSum = 0.0;

		foreach ( var s in shares )
			shareSum += s;

		if ( shareSum <= 0.0 )
			throw new ArgumentException( "Stations produce no positive segment.", nameof( stations ) );

		var rolls = new double[count];

		for ( var i = 0; i < count; i++ )
			rolls[i] = totalRollDegrees * (shares[i] / shareSum);

		// Last segment absorbs residue, so the sum is the total, not the
		// total plus accumulated rounding.
		if ( count > 1 )
		{
			var rest = 0.0;

			for ( var i = 0; i < count - 1; i++ )
				rest += rolls[i];

			rolls[count - 1] = totalRollDegrees - rest;
		}

		return rolls;
	}

	/// <summary>
	/// Cumulative twist at each station boundary: element 0 is 0, the last
	/// is the total roll. Length is one more than the segment count.
	/// </summary>
	public static double[] Cumulative( double[] segmentRolls )
	{
		if ( segmentRolls is null )
			throw new ArgumentNullException( nameof( segmentRolls ) );

		foreach ( var r in segmentRolls )
		{
			if ( !double.IsFinite( r ) )
				throw new ArgumentException( "Segment rolls must be finite.", nameof( segmentRolls ) );
		}

		var result = new double[segmentRolls.Length + 1];

		for ( var i = 0; i < segmentRolls.Length; i++ )
			result[i + 1] = result[i] + segmentRolls[i];

		return result;
	}

	/// <summary>
	/// Each segment's share of the total roll. All zeros when the total is
	/// zero (a share of nothing is nothing, not NaN).
	/// </summary>
	public static double[] Shares( double[] segmentRolls )
	{
		if ( segmentRolls is null )
			throw new ArgumentNullException( nameof( segmentRolls ) );

		var total = 0.0;

		foreach ( var r in segmentRolls )
		{
			if ( !double.IsFinite( r ) )
				throw new ArgumentException( "Segment rolls must be finite.", nameof( segmentRolls ) );
			total += r;
		}

		var result = new double[segmentRolls.Length];

		if ( total == 0.0 )
			return result;

		for ( var i = 0; i < segmentRolls.Length; i++ )
			result[i] = segmentRolls[i] / total;

		return result;
	}

	/// <summary>Largest single segment roll, in degrees, ignoring sign.</summary>
	public static double MaxSegmentRoll( double[] segmentRolls )
	{
		if ( segmentRolls is null )
			throw new ArgumentNullException( nameof( segmentRolls ) );
		if ( segmentRolls.Length == 0 )
			throw new ArgumentException( "At least one segment is required.", nameof( segmentRolls ) );

		var max = 0.0;

		foreach ( var r in segmentRolls )
		{
			if ( !double.IsFinite( r ) )
				throw new ArgumentException( "Segment rolls must be finite.", nameof( segmentRolls ) );
			max = Math.Max( max, Math.Abs( r ) );
		}

		return max;
	}

	/// <summary>
	/// Concentration of the turn: the largest segment's share of the total
	/// absolute roll. 1.0 means one ring takes everything (pinched); 1/n
	/// means perfectly spread over n segments; 0 means no roll at all.
	/// </summary>
	public static double PinchFactor( double[] segmentRolls )
	{
		if ( segmentRolls is null )
			throw new ArgumentNullException( nameof( segmentRolls ) );

		var absolute = 0.0;

		foreach ( var r in segmentRolls )
		{
			if ( !double.IsFinite( r ) )
				throw new ArgumentException( "Segment rolls must be finite.", nameof( segmentRolls ) );
			absolute += Math.Abs( r );
		}

		if ( absolute == 0.0 )
			return 0.0;

		return MaxSegmentRoll( segmentRolls ) / absolute;
	}

	/// <summary>
	/// True when any single segment exceeds
	/// <paramref name="maxPerSegmentDegrees"/>: that ring is carrying a
	/// pinch-sized share of the turn. Equal to the threshold is not pinched.
	/// </summary>
	public static bool IsPinched( double[] segmentRolls, double maxPerSegmentDegrees )
	{
		if ( !double.IsFinite( maxPerSegmentDegrees ) || maxPerSegmentDegrees <= 0.0 )
			throw new ArgumentOutOfRangeException( nameof( maxPerSegmentDegrees ), "The pinch threshold must be positive and finite." );

		return MaxSegmentRoll( segmentRolls ) > maxPerSegmentDegrees;
	}

	/// <summary>
	/// Forearm default: three rings, uniform spread. A 90 degree wrist turn
	/// becomes 30 + 30 + 30. Same math as <see cref="Calf"/>; only the limb
	/// differs, and nothing in the Lane 7 spec distinguishes their profiles.
	/// </summary>
	public static double[] Forearm( double totalRollDegrees ) =>
		Distribute( totalRollDegrees, 3, TwistProfile.Uniform );

	/// <summary>
	/// Calf default: three rings, uniform spread. A 60 degree ankle turn
	/// becomes 20 + 20 + 20. See <see cref="Forearm"/>.
	/// </summary>
	public static double[] Calf( double totalRollDegrees ) =>
		Distribute( totalRollDegrees, 3, TwistProfile.Uniform );

	static double Smoothstep( double t ) => t * t * (3.0 - 2.0 * t);
}
