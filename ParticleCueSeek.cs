#nullable enable
using System;
using System.Collections.Generic;

namespace Marionette.Desktop;

/// <summary>
/// P52 — deterministic cue seek (particle wave 6).
///
/// A pure, deterministic cue-seek evaluator. It does NOT simulate particles:
/// it answers, for a cue timeline and a target time <c>t</c>, which cues have
/// fired by <c>t</c>, which are still pending, which were skipped for want of
/// a resolvable origin, and — for the ones that fired — what burst step they
/// carry, so that seeking to <c>t</c> and then stepping forward produces the
/// same subsequent behaviour as playing from 0 to <c>t</c>.
///
/// Units: seconds for all times, fixed-step indices for all steps, particle
/// counts for bursts. This is the 3D cue model only (kernel inches, Z-up, as
/// in <see cref="ParticleSystem"/>): the evaluator deals in times, steps and
/// counts, never in positions, so no position unit can leak in. A 2D cue
/// timeline (pixels, Y-down) is NOT supported here — it needs its own
/// labelled path in a separate lane, never a silent unit mix.
///
/// Seek definition (shared with P36's deterministic seek/prewarm, whose
/// <c>ParticleSeek.cs</c> was NOT in the canonical tree at this lane's build
/// time, so this lane defines cue-seek in exactly those terms rather than
/// inventing a second definition): seek(t) = reset + fixed-step replay to t.
/// The evaluator produces the replay plan; the caller resets the system and
/// steps from 0 applying that plan. It never tries to "un-fire" a cue:
/// seeking backwards (t2 &lt; t1 after a seek to t1) re-derives from scratch
/// — evaluate at t2 on a fresh result — because evaluation is a pure
/// function of (timeline, t, dt, resolver) and holds no state.
///
/// Cue model, mirroring <see cref="ParticleCueBridge"/> and
/// <see cref="ParticleSystem.TriggerCue"/>:
/// - A burst cue fires once: the bridge's <c>TriggerCue</c> retimes the
///   preset's step-0 burst to the system's current step. The evaluator
///   reproduces that retiming as <see cref="ParticleCueFired.BurstStep"/> =
///   trigger step (+ <see cref="ParticleCueEvent.BurstStepOffset"/>).
/// - A rate cue starts looping at its trigger step and keeps running; the
///   bridge stops it at its RigEvent end. The evaluator reports it as
///   "running since trigger time" (see below) — it does NOT simulate its
///   emission history.
/// - A cue with no resolvable origin is SKIPPED SILENTLY, exactly as the
///   bridge skips it at play: never an error, never a fabricated origin.
///   The caller supplies <c>originKnown</c>; a null resolver means no origin
///   ever resolves (the resolver-less bridge fires nothing).
///
/// Classification rules (all tested):
/// - Step conversion snaps to the fixed-step grid: a time within ~1e-6
///   relative (≈1 µs at step 60, 60 Hz) of a step boundary counts as that
///   boundary. A fixed step-epsilon cannot absorb float 1/60 s rounding —
///   the deficit grows with step count (3.1e-6 steps at step 60, 0.011 at
///   step 216000) — so the snap is relative: floor(ratio * (1 + 1e-6)).
///   1.0 s at 60 Hz is step 60, not 59, under float and double dt alike.
/// - Classification is step-quantized: a cue is fired iff its trigger step
///   is &lt;= the seek step count. Sub-step time differences inside one step
///   are indistinguishable to the fixed-step sim (a cue at 1.005 s and a cue
///   at 1.0 s both schedule at step 60 and both fire on the next step).
/// - Boundary: a cue exactly AT t counts as fired (for t &gt; 0). Its burst
///   is scheduled at the current step and fires on the NEXT step, exactly as
///   a live <c>TriggerCue</c> at time t behaves — see
///   <see cref="ParticleCueFired.BurstEmittedBySeekTime"/>.
/// - Seek to 0 is the exception that keeps "seeking to 0 fires nothing"
///   true: at t = 0 nothing is fired, everything resolvable is pending,
///   even a cue at time 0 (it fires on the first step, it has not fired yet).
/// - Unsorted timelines are SORTED deterministically (stable: ties keep
///   timeline order), not rejected — a scrubbed timeline may arrive
///   unsorted, and seek must not depend on arrival order. Duplicate cue
///   times are allowed; their order is the stable timeline order.
/// - The input timeline is never mutated; sorting works on a copy.
///
/// What seek returns for rate emitters vs burst cues (stated plainly):
/// - Burst cue: <see cref="ParticleCueFired.BurstStep"/> + BurstCount is
///   everything the caller needs — add the emitter up front with that burst
///   step and it fires at exactly the right step during replay (a burst
///   emitter present before its burst step is inert: Rate = 0, and
///   <c>BeginStep</c> only fires bursts whose step matches the current step).
/// - Rate cue: <see cref="ParticleCueFired.RunningSinceSeconds"/> (the
///   trigger time) + <see cref="ParticleCueFired.StepsRunning"/> (whole
///   steps completed since the trigger step). That is a REPORT, not a
///   catch-up: the caller must add the rate emitter at its trigger step
///   during replay so its per-step accumulation (including the fractional
///   remainder) matches play. Adding a rate emitter fresh at t instead
///   UNDER-emits — its pre-t history is lost — and is NOT a seek.
/// </summary>
public static class ParticleCueSeek
{
	/// <summary>Relative step-boundary snap (see the header). Not a step count.</summary>
	public const double StepSnapRelative = 1e-6;

	/// <summary>
	/// One cue on a timeline. <c>Kind</c> selects which shape field matters:
	/// burst cues use <c>BurstCount</c> (+ optional <c>BurstStepOffset</c>),
	/// rate cues use <c>RatePerSecond</c>. The evaluator never resolves
	/// preset names itself — the caller (bridge/sweep) supplies the shape the
	/// bridge models, so no preset table is duplicated here.
	/// </summary>
	public readonly record struct ParticleCueEvent(
		string Name,
		double TriggerTimeSeconds,
		string? OriginKey,
		ParticleCueSeekKind Kind,
		int BurstCount,
		double RatePerSecond,
		int BurstStepOffset = 0 );

	/// <summary>A cue that fired by the seek time.</summary>
	public readonly record struct ParticleCueFired(
		string Name,
		double TriggerTimeSeconds,
		long TriggerStep,
		ParticleCueSeekKind Kind,
		int BurstCount,
		long BurstStep,
		bool BurstEmittedBySeekTime,
		double RatePerSecond,
		double RunningSinceSeconds,
		long StepsRunning );

	/// <summary>A cue still to come after the seek time.</summary>
	public readonly record struct ParticleCuePending(
		string Name,
		double TriggerTimeSeconds,
		long TriggerStep,
		ParticleCueSeekKind Kind,
		int BurstCount,
		double RatePerSecond );

	/// <summary>A cue skipped because no origin resolved for its key. Never an error.</summary>
	public readonly record struct ParticleCueSkipped(
		string Name,
		double TriggerTimeSeconds,
		long TriggerStep,
		ParticleCueSeekKind Kind );

	/// <summary>Evaluate a cue timeline at target time <paramref name="targetTimeSeconds"/>.</summary>
	/// <param name="timeline">Cue events in any order; sorted deterministically (stable) internally.</param>
	/// <param name="targetTimeSeconds">Seek target, seconds, &gt;= 0, finite.</param>
	/// <param name="fixedDeltaTimeSeconds">Fixed step, seconds, in (0, 1] — must match the sim's step.</param>
	/// <param name="originKnown">Maps an event's origin key to whether an origin resolves for it.
	/// Null = no resolver = every cue skipped (the resolver-less bridge fires nothing).
	/// Pass <c>static _ =&gt; true</c> when origins are pre-resolved or irrelevant.</param>
	public static ParticleCueSeekResult Evaluate(
		IReadOnlyList<ParticleCueEvent> timeline,
		double targetTimeSeconds,
		double fixedDeltaTimeSeconds,
		Func<string, bool>? originKnown )
	{
		ArgumentNullException.ThrowIfNull( timeline );
		if ( double.IsNaN( targetTimeSeconds ) || double.IsInfinity( targetTimeSeconds ) )
			throw new ArgumentException( "Seek target time must be finite.", nameof( targetTimeSeconds ) );
		if ( targetTimeSeconds < 0 )
			throw new ArgumentOutOfRangeException( nameof( targetTimeSeconds ), "Seek target time must be >= 0." );
		if ( double.IsNaN( fixedDeltaTimeSeconds ) || double.IsInfinity( fixedDeltaTimeSeconds ) )
			throw new ArgumentException( "Fixed step must be finite.", nameof( fixedDeltaTimeSeconds ) );
		if ( fixedDeltaTimeSeconds <= 0 || fixedDeltaTimeSeconds > 1 )
			throw new ArgumentOutOfRangeException( nameof( fixedDeltaTimeSeconds ), "Fixed step must be in (0, 1] s." );

		long seekSteps = StepAt( targetTimeSeconds, fixedDeltaTimeSeconds );
		if ( seekSteps > int.MaxValue )
			throw new ArgumentOutOfRangeException( nameof( targetTimeSeconds ), "Seek target is beyond the int step range the sim's bursts can address." );
		// (StepAt already guards the double->long conversion range; this keeps the
		// horizon error specific to the burst-addressable range.)

		// Validate every event first, so a bad timeline fails before any classification.
		for ( int i = 0; i < timeline.Count; i++ ) Validate( timeline[i] );

		// Stable deterministic sort: trigger time, ties keep timeline order. Input untouched.
		var order = new int[timeline.Count];
		for ( int i = 0; i < order.Length; i++ ) order[i] = i;
		Array.Sort( order, ( a, b ) =>
		{
			int byTime = timeline[a].TriggerTimeSeconds.CompareTo( timeline[b].TriggerTimeSeconds );
			return byTime != 0 ? byTime : a.CompareTo( b );
		} );

		var fired = new List<ParticleCueFired>();
		var pending = new List<ParticleCuePending>();
		var skipped = new List<ParticleCueSkipped>();
		foreach ( int i in order )
		{
			var ev = timeline[i];
			long triggerStep = StepAt( ev.TriggerTimeSeconds, fixedDeltaTimeSeconds );
			if ( triggerStep > int.MaxValue )
				throw new ArgumentOutOfRangeException( nameof( timeline ), $"Cue '{ev.Name}' at {ev.TriggerTimeSeconds}s is beyond the int step range the sim's bursts can address." );
			long burstStep = triggerStep + ev.BurstStepOffset;
			if ( burstStep > int.MaxValue )
				throw new ArgumentOutOfRangeException( nameof( timeline ), $"Cue '{ev.Name}' burst step {burstStep} is beyond the int step range the sim's bursts can address." );

			// Bridge rule: no key, no resolver, or resolver says no => silent skip, never wrong.
			if ( ev.OriginKey is null || originKnown is null || !originKnown( ev.OriginKey ) )
			{
				skipped.Add( new ParticleCueSkipped( ev.Name, ev.TriggerTimeSeconds, triggerStep, ev.Kind ) );
				continue;
			}
			// Seek to 0 fires nothing, even a cue at time 0.
			if ( targetTimeSeconds == 0 )
			{
				pending.Add( new ParticleCuePending( ev.Name, ev.TriggerTimeSeconds, triggerStep, ev.Kind, ev.BurstCount, ev.RatePerSecond ) );
				continue;
			}
			if ( triggerStep <= seekSteps )
			{
				bool burstEmitted = ev.Kind == ParticleCueSeekKind.Burst && burstStep < seekSteps;
				long stepsRunning = ev.Kind == ParticleCueSeekKind.Rate ? seekSteps - triggerStep : 0;
				fired.Add( new ParticleCueFired(
					ev.Name, ev.TriggerTimeSeconds, triggerStep, ev.Kind,
					ev.Kind == ParticleCueSeekKind.Burst ? ev.BurstCount : 0,
					burstStep, burstEmitted,
					ev.Kind == ParticleCueSeekKind.Rate ? ev.RatePerSecond : 0,
					ev.TriggerTimeSeconds, stepsRunning ) );
			}
			else
			{
				pending.Add( new ParticleCuePending( ev.Name, ev.TriggerTimeSeconds, triggerStep, ev.Kind, ev.BurstCount, ev.RatePerSecond ) );
			}
		}
		return new ParticleCueSeekResult( targetTimeSeconds, fixedDeltaTimeSeconds, seekSteps, fired, pending, skipped );
	}

	/// <summary>Whole fixed steps from 0 to <paramref name="timeSeconds"/>, boundary-snapped.</summary>
	public static long StepAt( double timeSeconds, double fixedDeltaTimeSeconds )
	{
		if ( double.IsNaN( timeSeconds ) || double.IsInfinity( timeSeconds ) )
			throw new ArgumentException( "Time must be finite.", nameof( timeSeconds ) );
		if ( timeSeconds < 0 )
			throw new ArgumentOutOfRangeException( nameof( timeSeconds ), "Time must be >= 0." );
		if ( double.IsNaN( fixedDeltaTimeSeconds ) || double.IsInfinity( fixedDeltaTimeSeconds ) || fixedDeltaTimeSeconds <= 0 || fixedDeltaTimeSeconds > 1 )
			throw new ArgumentOutOfRangeException( nameof( fixedDeltaTimeSeconds ), "Fixed step must be in (0, 1] s." );
		if ( timeSeconds / fixedDeltaTimeSeconds > 9.0e18 )
			throw new ArgumentOutOfRangeException( nameof( timeSeconds ), "Time is beyond the step range this evaluator can address." );
		double ratio = timeSeconds / fixedDeltaTimeSeconds;
		return (long)Math.Floor( ratio * (1.0 + StepSnapRelative) + 1e-9 );
	}

	static void Validate( ParticleCueEvent ev )
	{
		if ( string.IsNullOrWhiteSpace( ev.Name ) )
			throw new ArgumentException( "Cue events need a non-empty name.", nameof( ev ) );
		if ( double.IsNaN( ev.TriggerTimeSeconds ) || double.IsInfinity( ev.TriggerTimeSeconds ) )
			throw new ArgumentException( $"Cue '{ev.Name}' trigger time must be finite.", nameof( ev ) );
		if ( ev.TriggerTimeSeconds < 0 )
			throw new ArgumentOutOfRangeException( nameof( ev ), $"Cue '{ev.Name}' trigger time must be >= 0." );
		if ( ev.BurstStepOffset < 0 )
			throw new ArgumentOutOfRangeException( nameof( ev ), $"Cue '{ev.Name}' burst step offset must be >= 0." );
		switch ( ev.Kind )
		{
			case ParticleCueSeekKind.Burst:
				if ( ev.BurstCount < 0 )
					throw new ArgumentOutOfRangeException( nameof( ev ), $"Cue '{ev.Name}' burst count must be >= 0." );
				break;
			case ParticleCueSeekKind.Rate:
				if ( double.IsNaN( ev.RatePerSecond ) || double.IsInfinity( ev.RatePerSecond ) )
					throw new ArgumentException( $"Cue '{ev.Name}' rate must be finite.", nameof( ev ) );
				if ( ev.RatePerSecond < 0 )
					throw new ArgumentOutOfRangeException( nameof( ev ), $"Cue '{ev.Name}' rate must be >= 0." );
				break;
			default:
				throw new ArgumentException( $"Cue '{ev.Name}' has an unknown kind {(int)ev.Kind}.", nameof( ev ) );
		}
	}
}

/// <summary>Kind of cue shape, as the bridge models it: one-shot burst or looping rate.</summary>
public enum ParticleCueSeekKind
{
	Burst,
	Rate,
}

/// <summary>Immutable result of <see cref="ParticleCueSeek.Evaluate"/>: fired, pending and skipped cues at a seek time.</summary>
public sealed class ParticleCueSeekResult
{
	public ParticleCueSeekResult(
		double targetTimeSeconds,
		double fixedDeltaTimeSeconds,
		long seekSteps,
		IReadOnlyList<ParticleCueSeek.ParticleCueFired> fired,
		IReadOnlyList<ParticleCueSeek.ParticleCuePending> pending,
		IReadOnlyList<ParticleCueSeek.ParticleCueSkipped> skipped )
	{
		TargetTimeSeconds = targetTimeSeconds;
		FixedDeltaTimeSeconds = fixedDeltaTimeSeconds;
		SeekSteps = seekSteps;
		Fired = fired;
		Pending = pending;
		SkippedNoOrigin = skipped;
	}

	/// <summary>Seek target, seconds.</summary>
	public double TargetTimeSeconds { get; }

	/// <summary>Fixed step used, seconds.</summary>
	public double FixedDeltaTimeSeconds { get; }

	/// <summary>Whole fixed steps from 0 to the target (the replay length).</summary>
	public long SeekSteps { get; }

	/// <summary>Cues fired by the seek time, in stable trigger order.</summary>
	public IReadOnlyList<ParticleCueSeek.ParticleCueFired> Fired { get; }

	/// <summary>Cues still to come, in stable trigger order.</summary>
	public IReadOnlyList<ParticleCueSeek.ParticleCuePending> Pending { get; }

	/// <summary>Cues skipped for want of a resolvable origin, in stable trigger order.</summary>
	public IReadOnlyList<ParticleCueSeek.ParticleCueSkipped> SkippedNoOrigin { get; }

	/// <summary>Deterministic rendering of the whole result, for equality checks in tests.</summary>
	public string ToCanonicalString()
	{
		var parts = new List<string>
		{
			$"t={TargetTimeSeconds:R} steps={SeekSteps}",
		};
		foreach ( var f in Fired )
			parts.Add( $"F:{f.Name}@{f.TriggerTimeSeconds:R}/s{f.TriggerStep}/{f.Kind}/b{f.BurstCount}/bs{f.BurstStep}/em{(f.BurstEmittedBySeekTime ? 1 : 0)}/r{f.RatePerSecond:R}/run{f.StepsRunning}" );
		foreach ( var p in Pending )
			parts.Add( $"P:{p.Name}@{p.TriggerTimeSeconds:R}/s{p.TriggerStep}/{p.Kind}" );
		foreach ( var s in SkippedNoOrigin )
			parts.Add( $"S:{s.Name}@{s.TriggerTimeSeconds:R}/s{s.TriggerStep}/{s.Kind}" );
		return string.Join( "|", parts );
	}
}
