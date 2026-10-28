#nullable enable
using System;

namespace Marionette.Desktop;

// Cue loop scheduler for the particles session (wave-6 lane P53).
//
// WHAT THIS IS: a pure schedule calculator, not a player. Given a looping
// cue definition it answers, for any time t, whether the loop is active,
// which iteration it is in, and where inside that iteration the playback
// position sits — and when the schedule next changes, so a caller can arm
// a wake-up instead of polling every frame. There is no wall-clock read,
// no RNG, no hidden state, and no simulation here: calling ScheduleAt
// twice with the same arguments always returns the same answer, and
// nothing in this file emits a particle, touches the bridge, or moves
// an emitter. Wiring a schedule into live emitters is the coordinator
// sweep's job (see the integration note at the bottom); the mark-loop
// start/stop events themselves are lane P34's bridge extension, not
// this file.
//
// CLOCK: every time in this file is loop-local seconds since the loop
// was armed (the cue fired). StartDelaySeconds is the delay after
// arming; EndTimeSeconds and the explicit-stop argument are on that
// same clock. The bridge maps timeline time to loop-local time by
// subtracting the trigger time before calling in — this file never
// guesses at that mapping.
//
// TIMELINE SHAPE (half-open intervals, start inclusive, end exclusive):
//   iteration i starts at StartDelaySeconds + i * (DurationSeconds +
//   GapSeconds) and runs for DurationSeconds; then GapSeconds of
//   silence follow before the next iteration (the gap after the final
//   iteration is irrelevant — the loop has stopped by then). A time
//   exactly on an iteration start is active at phase 0 (forward) or
//   phase 1 (reversed); a time exactly on an iteration end is inactive,
//   in the gap. A time before StartDelaySeconds is inactive (pending).
//
// LOOP MODES:
//   Count    — fire the loop exactly LoopCount times, then stop.
//              LoopCount 0 is the documented degenerate: the loop
//              never fires (ScheduleAt is inactive everywhere, and
//              NextTransition returns null). It is not an error,
//              because "fire zero times" is a meaningful schedule.
//   Infinite — loop until explicitly stopped: until EndTimeSeconds
//              (time-based end) or the explicit-stop argument to
//              ScheduleAt/NextTransition (explicit stop signal).
//              LoopCount is ignored in this mode, documented here
//              so nobody reads a leftover count as a bound.
//   PingPong — LoopCount iterations that alternate direction: even
//              iterations (0, 2, 4, ...) play forward, odd iterations
//              (1, 3, 5, ...) play in reverse. See REVERSE below.
//
// STOP CONDITIONS (all three, and how they compose):
//   count-based — the mode's iteration cap (Count, PingPong). The
//     loop stops when the cap's last iteration ends.
//   time-based  — EndTimeSeconds, an absolute loop-local cutoff.
//     The loop is inactive at and after it, even mid-iteration.
//   explicit    — the stoppedAt argument to ScheduleAt/NextTransition
//     (loop-local time of an external stop, e.g. a RigEvent end or a
//     P34 mark-loop stop). The loop is inactive at and after it,
//     even mid-iteration. When both are set, the earlier one wins.
//   A stop never extends a loop and never restarts it: after any stop
//   the schedule is inactive forever, and NextTransition returns null
//   once no future active period remains.
//
// REVERSE (what "reverse" means for a particle loop — read this before
// using PingPong): the emission PATTERN plays backward, not time
// travel. On a reversed iteration the pattern position runs from 1
// down to 0, so a ramp-up emission curve plays as a ramp-down, a
// pattern sequence plays in reverse order. The simulation itself is
// NOT reversed: particles already emitted keep aging forward, nothing
// is un-emitted, and no particle state is rewound. Phase in the
// schedule result is the PATTERN position (forward iterations 0 -> 1,
// reversed iterations 1 -> 0); Direction reports which way it moves
// (+1 forward, -1 reverse, 0 when inactive).
//
// REVERSIBLE PHASE (which loop kinds may use PingPong):
//   ReversiblePhase must be true for PingPong, and Validate() throws
//   otherwise. A loop HAS a reversible phase when its internal phase
//   is a pure function of the pattern position that can be read
//   backward: emission-pattern loops (a rate curve or pattern
//   sequence defined over [0, 1)) and keyframed pattern loops.
//   A loop does NOT have one when its phase depends on accumulated
//   sim state: stateful rate emitters whose emitted particles persist
//   (playing the pattern backward does not un-emit them), one-shot
//   bursts with no periodic phase, and loops driven by live sim
//   state. The caller declares this — this file cannot inspect a
//   pattern it was never given, so it trusts the flag and fails
//   loudly when PingPong is asked of a non-reversible loop, rather
//   than silently playing it forward and calling it ping-pong.
//
// OUT OF SCOPE (other lanes, named so they do not creep in): cue
// seeking (P52), sim seek/prewarm (P36), mark-loop start/stop events
// themselves (P34's bridge extension), bone-origin resolution
// (caller-supplied, never invented here), save/serialization
// (lane 5), and actually wiring scheduling into the bridge (sweep
// integration lines only, below).

/// <summary>How a looping cue repeats. See the file header for the timeline shape.</summary>
public enum ParticleCueLoopMode
{
	/// <summary>Fire exactly <see cref="ParticleCueLoopDefinition.LoopCount"/> times, then stop.</summary>
	Count,
	/// <summary>Loop until EndTimeSeconds or an explicit stop. LoopCount is ignored.</summary>
	Infinite,
	/// <summary>LoopCount iterations alternating forward/reverse. Requires ReversiblePhase.</summary>
	PingPong,
}

/// <summary>
/// Pure schedule answer for one time: whether the loop is active, which
/// iteration it is in (0-based, -1 when inactive), the pattern position
/// within the iteration (0..1; forward iterations rise 0 -> 1, reversed
/// iterations fall 1 -> 0; 0 when inactive), and the direction that
/// position moves (+1 forward, -1 reverse, 0 when inactive).
/// </summary>
public readonly struct ParticleCueLoopSchedule
{
	public ParticleCueLoopSchedule( bool active, int iteration, double phase, int direction )
	{
		Active = active;
		Iteration = iteration;
		Phase = phase;
		Direction = direction;
	}

	public bool Active { get; }
	public int Iteration { get; }
	public double Phase { get; }
	public int Direction { get; }

	/// <summary>True on odd PingPong iterations, when Phase falls instead of rising.</summary>
	public bool Reversed => Direction < 0;

	/// <summary>The inactive answer: not active, no iteration, phase 0, no direction.</summary>
	public static ParticleCueLoopSchedule Inactive => new( false, -1, 0.0, 0 );
}

/// <summary>
/// A looping cue definition: loop duration, start delay, gap between
/// loops, mode, iteration count, optional time-based end, and whether
/// the loop's phase is reversible (required for PingPong). All times
/// are loop-local seconds since arming; all values are validated by
/// <see cref="Validate"/> and re-validated on every ScheduleAt /
/// NextTransition call, so a bad definition fails loudly at the call
/// site instead of producing a silent never-firing loop.
/// </summary>
public sealed class ParticleCueLoopDefinition
{
	/// <summary>How the loop repeats. Default: Count.</summary>
	public ParticleCueLoopMode Mode { get; set; } = ParticleCueLoopMode.Count;

	/// <summary>Length of one iteration, seconds. Must be positive.</summary>
	public double DurationSeconds { get; set; } = 1.0;

	/// <summary>Delay after arming before the first iteration starts, seconds. Must be >= 0.</summary>
	public double StartDelaySeconds { get; set; }

	/// <summary>Silence between iterations, seconds. Must be >= 0.</summary>
	public double GapSeconds { get; set; }

	/// <summary>
	/// Total iterations in Count and PingPong modes. Ignored in
	/// Infinite mode. 0 means "never fires" (Count/PingPong only);
	/// negative counts are rejected by Validate().
	/// </summary>
	public int LoopCount { get; set; } = 1;

	/// <summary>
	/// Optional time-based end, loop-local seconds. The loop is
	/// inactive at and after this time, even mid-iteration. Null
	/// means no time-based end. Composes with the explicit stop
	/// argument: the earlier cutoff wins.
	/// </summary>
	public double? EndTimeSeconds { get; set; }

	/// <summary>
	/// Whether the loop's internal phase can be read backward (an
	/// emission pattern / keyframed pattern over [0, 1)). PingPong
	/// requires this to be true; see REVERSIBLE PHASE in the header.
	/// </summary>
	public bool ReversiblePhase { get; set; }

	/// <summary>
	/// Loudly rejects non-positive durations, negative delays/gaps/
	/// counts, non-finite times, and PingPong on a loop whose
	/// definition has no reversible phase. Throws
	/// ArgumentOutOfRangeException / ArgumentException with the
	/// offending field named — never a silent clamp.
	/// </summary>
	public void Validate()
	{
		if ( double.IsNaN( DurationSeconds ) || double.IsInfinity( DurationSeconds ) )
			throw new ArgumentException( "Loop duration must be a finite number of seconds.", nameof( DurationSeconds ) );
		if ( DurationSeconds <= 0 )
			throw new ArgumentOutOfRangeException( nameof( DurationSeconds ), DurationSeconds, "Loop duration must be positive; a zero or negative loop has no phase to schedule." );
		if ( double.IsNaN( StartDelaySeconds ) || double.IsInfinity( StartDelaySeconds ) )
			throw new ArgumentException( "Start delay must be a finite number of seconds.", nameof( StartDelaySeconds ) );
		if ( StartDelaySeconds < 0 )
			throw new ArgumentOutOfRangeException( nameof( StartDelaySeconds ), StartDelaySeconds, "Start delay must not be negative; a loop cannot start before it is armed." );
		if ( double.IsNaN( GapSeconds ) || double.IsInfinity( GapSeconds ) )
			throw new ArgumentException( "Gap must be a finite number of seconds.", nameof( GapSeconds ) );
		if ( GapSeconds < 0 )
			throw new ArgumentOutOfRangeException( nameof( GapSeconds ), GapSeconds, "Gap between loops must not be negative." );
		if ( LoopCount < 0 )
			throw new ArgumentOutOfRangeException( nameof( LoopCount ), LoopCount, "Loop count must not be negative; use 0 for a loop that never fires." );
		if ( EndTimeSeconds.HasValue && ( double.IsNaN( EndTimeSeconds.Value ) || double.IsInfinity( EndTimeSeconds.Value ) ) )
			throw new ArgumentException( "End time must be a finite number of seconds.", nameof( EndTimeSeconds ) );
		if ( Mode == ParticleCueLoopMode.PingPong && !ReversiblePhase )
			throw new ArgumentException( "PingPong requires ReversiblePhase: this loop's definition has no reversible phase, so its pattern cannot be played backward (see the file header for which loop kinds are reversible).", nameof( ReversiblePhase ) );
	}

	/// <summary>
	/// Pure schedule query: given loop-local time <paramref name="t"/>,
	/// return whether the loop is active at t, which iteration it is
	/// in, and its pattern position within that iteration (0..1).
	/// <paramref name="stoppedAt"/> is an optional explicit-stop time
	/// on the same clock (null = not stopped); the loop is inactive
	/// at and after it. No hidden state: same arguments, same answer.
	/// NaN times throw; +infinity time is simply inactive (no
	/// iteration contains it).
	/// </summary>
	public ParticleCueLoopSchedule ScheduleAt( double t, double? stoppedAt = null )
	{
		Validate();
		if ( double.IsNaN( t ) )
			throw new ArgumentException( "Schedule time must not be NaN.", nameof( t ) );
		if ( stoppedAt.HasValue && double.IsNaN( stoppedAt.Value ) )
			throw new ArgumentException( "Explicit stop time must not be NaN.", nameof( stoppedAt ) );
		double? cutoff = Cutoff( stoppedAt );
		if ( t < StartDelaySeconds || ( cutoff.HasValue && t >= cutoff.Value ) )
			return ParticleCueLoopSchedule.Inactive;
		if ( Mode != ParticleCueLoopMode.Infinite && LoopCount == 0 )
			return ParticleCueLoopSchedule.Inactive;
		double cycle = DurationSeconds + GapSeconds;
		int iteration = (int)Math.Floor( ( t - StartDelaySeconds ) / cycle );
		if ( Mode != ParticleCueLoopMode.Infinite && iteration >= LoopCount )
			return ParticleCueLoopSchedule.Inactive;
		double iterationStart = StartDelaySeconds + iteration * cycle;
		if ( t >= iterationStart + DurationSeconds )
			return ParticleCueLoopSchedule.Inactive; // In the gap between iterations.
		double progress = ( t - iterationStart ) / DurationSeconds; // 0..1 through the iteration.
		bool reversed = Mode == ParticleCueLoopMode.PingPong && iteration % 2 == 1;
		// Pattern position: forward iterations rise 0 -> 1, reversed
		// iterations fall 1 -> 0. The sim itself is never reversed —
		// only this position runs backward.
		double phase = reversed ? 1.0 - progress : progress;
		return new ParticleCueLoopSchedule( true, iteration, phase, reversed ? -1 : 1 );
	}

	/// <summary>
	/// Next time after <paramref name="t"/> at which the schedule
	/// changes state — the loop starts, an iteration ends, or a new
	/// iteration begins — so the caller can arm a wake-up instead of
	/// polling every frame. Returns null when no future change
	/// exists (loop exhausted, already past every cutoff, or never
	/// fires). Strictly greater than t when non-null.
	/// <para/>
	/// Only changes to the (Active, Iteration, Direction) answer
	/// count as transitions: a cutoff that lands in a gap, or before
	/// the first iteration starts, is silent (the loop was already
	/// inactive and stays inactive) and produces no wake-up — the
	/// caller needs no wake-up for a state that does not change.
	/// <paramref name="stoppedAt"/> is the same explicit-stop time as
	/// in ScheduleAt, on the same loop-local clock.
	/// </summary>
	public double? NextTransition( double t, double? stoppedAt = null )
	{
		Validate();
		if ( double.IsNaN( t ) )
			throw new ArgumentException( "Current time must not be NaN.", nameof( t ) );
		if ( stoppedAt.HasValue && double.IsNaN( stoppedAt.Value ) )
			throw new ArgumentException( "Explicit stop time must not be NaN.", nameof( stoppedAt ) );
		double? cutoff = Cutoff( stoppedAt );
		if ( cutoff.HasValue && t >= cutoff.Value )
			return null; // Already stopped; no future change.
		if ( Mode != ParticleCueLoopMode.Infinite && LoopCount == 0 )
			return null; // Never fires; no future change.
		if ( t < StartDelaySeconds )
			return cutoff.HasValue && cutoff.Value <= StartDelaySeconds ? null : StartDelaySeconds;
		double cycle = DurationSeconds + GapSeconds;
		int iteration = (int)Math.Floor( ( t - StartDelaySeconds ) / cycle );
		if ( Mode != ParticleCueLoopMode.Infinite && iteration >= LoopCount )
			return null; // Past the last iteration; inactive forever.
		double iterationStart = StartDelaySeconds + iteration * cycle;
		double iterationEnd = iterationStart + DurationSeconds;
		if ( t < iterationEnd )
		{
			// Inside an iteration: the next change is its end, unless
			// a cutoff lands inside it first (a mid-iteration stop).
			if ( cutoff.HasValue && cutoff.Value <= iterationEnd )
				return cutoff.Value;
			return iterationEnd;
		}
		// In the gap after iteration `iteration`: the next change is
		// the next iteration's start, unless there is none or a cutoff
		// lands before it (a silent cutoff in the gap: no wake-up).
		int next = iteration + 1;
		if ( Mode != ParticleCueLoopMode.Infinite && next >= LoopCount )
			return null;
		double nextStart = StartDelaySeconds + next * cycle;
		if ( cutoff.HasValue && cutoff.Value <= nextStart )
			return null;
		return nextStart;
	}

	double? Cutoff( double? stoppedAt )
	{
		if ( EndTimeSeconds.HasValue && stoppedAt.HasValue )
			return Math.Min( EndTimeSeconds.Value, stoppedAt.Value );
		return EndTimeSeconds ?? stoppedAt;
	}
}

// COORDINATOR SWEEP — bridge integration (PROPOSED, not applied by
// this lane; ParticleCueBridge.cs was not edited):
//
// The bridge today tracks looping rate emitters under their RigEvent
// object and disables them at EventEnd, with no schedule input. To
// drive a looping cue from this scheduler the sweep needs, per looping
// cue, (a) a ParticleCueLoopDefinition, (b) the loop-local clock
// (timeline time minus trigger time), and (c) the explicit stop
// (RigEvent end / P34 mark-loop stop, as a loop-local time). With
// those three, the per-frame bridge decision is exactly:
//   var at = definition.ScheduleAt( loopLocalTime, stoppedAt );
//   emitter.Enabled = at.Active;
// and the wake-up arm is:
//   double? wake = definition.NextTransition( loopLocalTime, stoppedAt );
// No other bridge behaviour changes: the no-origin-skips rule, the
// burst-presets-are-never-tracked rule, and the EventEnd disable all
// still hold — the scheduler only decides WHEN a tracked looping
// emitter is on, never whether it fires or where it fires from.

// =====================================================================
// P53 BRIEF FIRING API (added in close-out, 2026-10-24; the phase
// scheduler above is the first agent's verified work and is untouched).
//
// This section answers the brief's question directly: given a cue that
// fires once per loop pass, WHEN does it fire? A firing is a discrete
// event at start + k * LoopPeriodSeconds, not a pattern position over
// a duration. Burst cues (sparks, splash, fire) use THIS API; looping
// rate emitters (bubbles, dust) use the phase scheduler above, whose
// Duration/Gap cycle decides when the emitter is on. Do not conflate
// the two: a burst has no pattern phase to reverse, only a launch
// direction, and a rate emitter has no discrete firing list.
//
// PINGPONG REVERSAL, DEFINED EXACTLY (burst cues): on a reverse pass
// the cue's launch direction vector is NEGATED (-d), kernel inches,
// Z-up. That is the whole reversal: same origin, same preset, same
// burst count, opposite launch direction. No plane is chosen because
// none is needed — negation is the only origin-independent mirror.
// FALLBACK, PER THE BRIEF: when the cue has no meaningful reverse —
// no direction (null) or a near-zero direction (LengthSquared <=
// 1e-12, the same threshold ParticleEmitterPresets.Create uses to
// decide a direction re-aims anything) — PingPong falls back to
// Forward for that cue: Direction stays +1, Reversed is false, and
// DirectionVector is returned unchanged. A reverse is never invented
// for a directionless cue.
//
// SEEK COMPATIBILITY: every method here is a pure function of
// (spec, start, t, direction). There is no stored pass index, no
// "last fired" memory, no wall clock, no RNG. Querying t2 < t1 after
// t1 returns exactly what a fresh query at t2 returns, so a seek to
// any time re-derives the same schedule — this is what makes the
// scheduler consistent with P52 cue seek by construction. (P52 had
// NOT landed in canonical at close-out check time: no
// ParticleCueSeek.cs exists there, so no seek file was read or
// touched; this section was written to the pure-function rule in
// the brief instead.)
//
// COUNT DIVERGENCE, STATED NOT HIDDEN: the phase scheduler above
// documents LoopCount 0 as "never fires" and its tests pin that.
// The firing spec below follows the P53 brief strictly instead:
// a finite loop with LoopCount < 1 throws loudly. The two APIs
// keep their own validation; neither is silently unified.
//
// ARITHMETIC, NOT ITERATION: firing COUNTS and the NEXT firing time
// are computed with Math.Floor division, never by walking passes
// one by one. Evaluate() still materializes the firing list itself
// (a list of N firings is inherently O(N) to return), so callers
// that only need a count or a next time at a huge t must use
// CountFirings / NextFiringTime, which never allocate the list.
// Floating-point caveat: firing times are start + k * period in
// double arithmetic; a query time that is mathematically exact but
// not exactly representable can land one ulp either side of a
// firing boundary. Tests use exactly representable values.
// =====================================================================

/// <summary>One scheduled cue firing: its time, its 0-based pass index, and its direction.</summary>
public readonly struct ParticleCueLoopFiring
{
	public ParticleCueLoopFiring( double time, int passIndex, int direction, bool reversed, System.Numerics.Vector3? directionVector )
	{
		Time = time;
		PassIndex = passIndex;
		Direction = direction;
		Reversed = reversed;
		DirectionVector = directionVector;
	}

	/// <summary>Seconds, on the same clock as the start time passed to Evaluate.</summary>
	public double Time { get; }
	/// <summary>0-based pass: firing k happens at start + k * period.</summary>
	public int PassIndex { get; }
	/// <summary>+1 forward, -1 reverse. Never 0: a firing always has a direction.</summary>
	public int Direction { get; }
	/// <summary>True exactly on reverse PingPong passes with a real direction.</summary>
	public bool Reversed { get; }
	/// <summary>Launch direction for this firing: the cue direction on forward passes, its negation on reverse passes, null when the cue has no direction (PingPong fallback: forward).</summary>
	public System.Numerics.Vector3? DirectionVector { get; }
}

/// <summary>Result of <see cref="ParticleCueLoopSpec.Evaluate"/>: every firing at or before t, plus the next firing after t (null when the loop is exhausted).</summary>
public sealed class ParticleCueLoopFiringSet
{
	public ParticleCueLoopFiringSet( System.Collections.Generic.IReadOnlyList<ParticleCueLoopFiring> firings, double? nextFiring )
	{
		Firings = firings;
		NextFiring = nextFiring;
	}

	public System.Collections.Generic.IReadOnlyList<ParticleCueLoopFiring> Firings { get; }
	public int Count => Firings.Count;
	/// <summary>Next firing time strictly after the query time, or null when no firing remains.</summary>
	public double? NextFiring { get; }
}

/// <summary>
/// A discrete cue-loop spec: fire every <see cref="LoopPeriodSeconds"/>
/// seconds, <see cref="LoopCount"/> times, or forever when
/// <see cref="Infinite"/> is set. Infinite is an explicit named bool —
/// never encoded as a magic count like -1. When Infinite is set,
/// LoopCount is ignored (documented here so a leftover count is
/// never read as a bound). PingPong alternates forward/reverse
/// passes; see the section header for the exact reversal rule and
/// its directionless fallback. All times are seconds.
/// </summary>
public sealed class ParticleCueLoopSpec
{
	/// <summary>Total firings in a finite loop. Must be >= 1. Ignored when <see cref="Infinite"/> is set.</summary>
	public int LoopCount { get; set; } = 1;

	/// <summary>Explicit infinite marker: loop forever (until the caller stops asking). Never encode as -1.</summary>
	public bool Infinite { get; set; }

	/// <summary>Seconds between successive firings. Must be finite and > 0.</summary>
	public double LoopPeriodSeconds { get; set; } = 1.0;

	/// <summary>When true, odd passes fire reversed (direction negated); directionless cues fall back to forward.</summary>
	public bool PingPong { get; set; }

	/// <summary>Loudly rejects non-finite or non-positive periods and, for finite loops, counts below 1. Never silently clamps.</summary>
	public void Validate()
	{
		if ( double.IsNaN( LoopPeriodSeconds ) || double.IsInfinity( LoopPeriodSeconds ) )
			throw new ArgumentException( "Loop period must be a finite number of seconds.", nameof( LoopPeriodSeconds ) );
		if ( LoopPeriodSeconds <= 0 )
			throw new ArgumentOutOfRangeException( nameof( LoopPeriodSeconds ), LoopPeriodSeconds, "Loop period must be positive; a zero or negative period would stack infinite firings on one instant." );
		if ( !Infinite && LoopCount < 1 )
			throw new ArgumentOutOfRangeException( nameof( LoopCount ), LoopCount, "Loop count must be at least 1 for a finite loop; use Infinite = true for an unbounded loop." );
	}

	/// <summary>Launch direction for one pass: the cue direction on forward passes and on directionless fallback, its negation on reverse PingPong passes. Pure function of its arguments.</summary>
	public static System.Numerics.Vector3? DirectionForPass( System.Numerics.Vector3? cueDirection, int passIndex, bool pingPong )
	{
		if ( cueDirection is not { } d ) return null;
		if ( d.LengthSquared() <= 1e-12f ) return cueDirection;
		bool reverse = pingPong && passIndex % 2 == 1;
		return reverse ? -d : d;
	}

	/// <summary>Whether a pass is a true reverse pass: PingPong, odd pass, and a real (non-null, non-near-zero) direction.</summary>
	public static bool IsReversePass( System.Numerics.Vector3? cueDirection, int passIndex, bool pingPong )
	{
		if ( !pingPong || passIndex % 2 != 1 ) return false;
		return cueDirection is { } d && d.LengthSquared() > 1e-12f;
	}

	/// <summary>
	/// Number of firings at or before <paramref name="t"/>, computed by
	/// division, never by iterating passes. A firing exactly at t
	/// counts as fired. A query before <paramref name="start"/> is 0.
	/// NaN times throw; -infinity is a pre-start query (0); +infinity
	/// is every firing of a finite loop, and throws for an infinite
	/// loop (its count is unbounded).
	/// </summary>
	public long CountFirings( double start, double t )
	{
		Validate();
		if ( double.IsNaN( start ) )
			throw new ArgumentException( "Loop start time must not be NaN.", nameof( start ) );
		if ( double.IsNaN( t ) )
			throw new ArgumentException( "Query time must not be NaN.", nameof( t ) );
		if ( double.IsInfinity( start ) )
			throw new ArgumentException( "Loop start time must be finite.", nameof( start ) );
		if ( double.IsPositiveInfinity( t ) )
		{
			if ( Infinite )
				throw new ArgumentException( "Cannot count firings of an infinite loop at +infinity.", nameof( t ) );
			return LoopCount;
		}
		if ( t < start ) return 0;
		long count = (long)Math.Floor( ( t - start ) / LoopPeriodSeconds ) + 1;
		return Infinite ? count : Math.Min( count, (long)LoopCount );
	}

	/// <summary>
	/// Next firing time strictly after <paramref name="t"/>, computed
	/// by division, never by iterating passes. Before start the
	/// answer is start itself; after exhaustion it is null, never a
	/// fabricated time. Same NaN/infinity rules as
	/// <see cref="CountFirings"/>.
	/// </summary>
	public double? NextFiringTime( double start, double t )
	{
		Validate();
		if ( double.IsNaN( start ) )
			throw new ArgumentException( "Loop start time must not be NaN.", nameof( start ) );
		if ( double.IsNaN( t ) )
			throw new ArgumentException( "Query time must not be NaN.", nameof( t ) );
		if ( double.IsInfinity( start ) )
			throw new ArgumentException( "Loop start time must be finite.", nameof( start ) );
		if ( double.IsPositiveInfinity( t ) ) return null;
		if ( t < start ) return start;
		long fired = CountFirings( start, t );
		if ( !Infinite && fired >= LoopCount ) return null;
		return start + fired * LoopPeriodSeconds;
	}

	/// <summary>
	/// Every firing at or before <paramref name="t"/> plus the next
	/// firing after it. Pure function of (spec, start, t, direction):
	/// no state is kept, so a backwards query re-derives exactly what
	/// a fresh query at that time returns. The firing list itself is
	/// O(N) to return by nature; use <see cref="CountFirings"/> /
	/// <see cref="NextFiringTime"/> when only a count or next time
	/// is needed at a huge t.
	/// </summary>
	public ParticleCueLoopFiringSet Evaluate( double start, double t, System.Numerics.Vector3? direction = null )
	{
		long count = CountFirings( start, t );
		double? next = NextFiringTime( start, t );
		var firings = new System.Collections.Generic.List<ParticleCueLoopFiring>();
		for ( long k = 0; k < count; k++ )
		{
			int pass = (int)k;
			bool reversed = IsReversePass( direction, pass, PingPong );
			firings.Add( new ParticleCueLoopFiring(
				start + k * LoopPeriodSeconds,
				pass,
				reversed ? -1 : 1,
				reversed,
				DirectionForPass( direction, pass, PingPong ) ) );
		}
		return new ParticleCueLoopFiringSet( firings, next );
	}
}
