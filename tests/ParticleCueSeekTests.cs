using System.Numerics;
using Marionette.Desktop;

/// <summary>
/// P52 tests (group particle-cue-seek). The seek/play equivalence tests use
/// a test-local play driver and a test-local seek driver that share ONE
/// documented convention — the interval convention in
/// <see cref="ParticleCueSeek"/>: a cue belongs to the fixed step whose
/// interval [step*dt, (step+1)*dt) contains its trigger time, with a time
/// exactly on a boundary belonging to the step starting there. The play
/// driver implements that convention with plain double arithmetic (it never
/// calls the evaluator); the seek driver applies the evaluator's plan.
/// Agreement between them is therefore a real check of the evaluator's step
/// math, not a tautology: a one-step error in TriggerStep changes burst
/// timing and alive counts.
///
/// Lifetime trick, stated so the counts read honestly: preset lifetimes are
/// short (sparks 0.2–0.6 s), so both drivers override emitter lifetime to
/// 10 s. That removes deaths from the comparison window (all windows here
/// are &lt;= 2.5 s), so alive counts equal emitted counts exactly and any
/// timing error shows up as a count error. RNG streams differ between the
/// drivers (seek adds burst emitters up front, play adds them as they fire,
/// and AddEmitter seeds by add-order index), so these tests assert counts
/// and step indices — the quantities seek promises — never positions.
/// </summary>
internal static class ParticleCueSeekTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		static void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		static void Throws<T>( Action body, string what ) where T : Exception
		{
			try { body(); }
			catch ( Exception ex ) { Check( ex is T, $"{what}: threw {ex.GetType().Name}, expected {typeof( T ).Name}" ); return; }
			throw new Exception( $"{what}: did not throw" );
		}

		static ParticleSystemSettings Settings() => new();
		static double Dt( ParticleSystemSettings s ) => s.FixedDeltaTime;
		static Vector3 Origin() => new( 0, 0, 20 );

		static ParticleCueSeek.ParticleCueEvent Burst( string name, double time, int count )
			=> new( name, time, name, ParticleCueSeekKind.Burst, count, 0 );
		static ParticleCueSeek.ParticleCueEvent Rate( string name, double time, double rate )
			=> new( name, time, name, ParticleCueSeekKind.Rate, 0, rate );
		static ParticleCueSeek.ParticleCueEvent PresetBurst( string name, double time )
			=> Burst( name, time, ParticleEmitterPresets.Create( name, Origin() ).Bursts[0].Count );
		static ParticleCueSeek.ParticleCueEvent PresetRate( string name, double time )
			=> Rate( name, time, ParticleEmitterPresets.Create( name, Origin() ).Rate );

		static ParticleEmitter LongLife( ParticleEmitter em )
		{
			em.LifetimeMin = 10f; em.LifetimeMax = 10f;
			return em;
		}

		// Play driver: time-based, evaluator-free. Before the particle Step()
		// at StepIndex k, fire every not-yet-fired cue whose trigger time lies
		// in [k*dt, (k+1)*dt) under plain double dt — boundary times belong
		// to the step starting there. Burst cues are fired TriggerCue-style
		// (preset Create + burst retimed to the current step).
		static (ParticleSystem system, Dictionary<string, long> fireSteps, Dictionary<int, long> aliveAt)
			RunPlay( ParticleCueSeek.ParticleCueEvent[] events, int totalSteps, int[] snapshotSteps )
		{
			var s = Settings();
			var system = new ParticleSystem( s );
			double dt = 1.0 / 60.0;
			var fired = new HashSet<int>();
			var fireSteps = new Dictionary<string, long>();
			var aliveAt = new Dictionary<int, long>();
			var want = new HashSet<int>( snapshotSteps );
			for ( int step = 0; step < totalSteps; step++ )
			{
				for ( int i = 0; i < events.Length; i++ )
				{
					if ( fired.Contains( i ) ) continue;
					var ev = events[i];
					if ( ev.TriggerTimeSeconds >= step * dt && ev.TriggerTimeSeconds < ( step + 1 ) * dt )
					{
						var em = LongLife( ParticleEmitterPresets.Create( ev.Name, Origin() ) );
						if ( ev.Kind == ParticleCueSeekKind.Burst )
							em.Bursts = new List<ParticleBurst> { new( (int)system.StepIndex, ev.BurstCount ) };
						system.AddEmitter( em );
						fired.Add( i );
						fireSteps[ev.Name] = system.StepIndex;
					}
				}
				system.Step();
				if ( want.Contains( step + 1 ) ) aliveAt[step + 1] = system.AliveCount;
			}
			return (system, fireSteps, aliveAt);
		}

		// Seek driver: apply the evaluator's plan. Burst emitters go in up
		// front with their planned burst step (inert until that step); rate
		// emitters go in at their trigger step during replay, exactly as play
		// would add them. Pending cues are never added by seek.
		static (ParticleSystem system, Dictionary<int, long> aliveAt)
			RunSeek( ParticleCueSeekResult plan, ParticleCueSeek.ParticleCueEvent[] events, int totalSteps, int[] snapshotSteps )
		{
			var system = new ParticleSystem( Settings() );
			var byName = new Dictionary<string, ParticleCueSeek.ParticleCueEvent>();
			foreach ( var ev in events ) byName[ev.Name] = ev;
			var rateAt = new Dictionary<long, ParticleCueSeek.ParticleCueEvent>();
			foreach ( var f in plan.Fired )
			{
				var ev = byName[f.Name];
				if ( f.Kind == ParticleCueSeekKind.Burst )
				{
					var em = LongLife( ParticleEmitterPresets.Create( f.Name, Origin() ) );
					em.Bursts = new List<ParticleBurst> { new( (int)f.BurstStep, f.BurstCount ) };
					system.AddEmitter( em );
				}
				else rateAt[f.TriggerStep] = ev;
			}
			var aliveAt = new Dictionary<int, long>();
			var want = new HashSet<int>( snapshotSteps );
			for ( int step = 0; step < totalSteps; step++ )
			{
				if ( rateAt.TryGetValue( step, out var rev ) )
					system.AddEmitter( LongLife( ParticleEmitterPresets.Create( rev.Name, Origin() ) ) );
				system.Step();
				if ( want.Contains( step + 1 ) ) aliveAt[step + 1] = system.AliveCount;
			}
			return (system, aliveAt);
		}

		registry.Add( "particle-cue-seek", "step math: 1.0 s is step 60 at 60 Hz, 0.5 s is 30, 0 s is 0", () =>
		{
			var s = Settings();
			Check( ParticleCueSeek.StepAt( 0, Dt( s ) ) == 0, $"StepAt(0) = {ParticleCueSeek.StepAt( 0, Dt( s ) )}" );
			Check( ParticleCueSeek.StepAt( 0.5, Dt( s ) ) == 30, $"StepAt(0.5) = {ParticleCueSeek.StepAt( 0.5, Dt( s ) )}" );
			Check( ParticleCueSeek.StepAt( 1.0, Dt( s ) ) == 60, $"StepAt(1.0) = {ParticleCueSeek.StepAt( 1.0, Dt( s ) )} (float-dt rounding must not give 59)" );
			Check( ParticleCueSeek.StepAt( 2.0, Dt( s ) ) == 120, $"StepAt(2.0) = {ParticleCueSeek.StepAt( 2.0, Dt( s ) )}" );
			Check( ParticleCueSeek.StepAt( 3600.0, Dt( s ) ) == 216000, $"StepAt(3600) = {ParticleCueSeek.StepAt( 3600.0, Dt( s ) )}" );
		} );

		registry.Add( "particle-cue-seek", "single burst cue: seek(t) then step N == play from 0 for t+N steps", () =>
		{
			var events = new[] { PresetBurst( "sparks", 1.0 ) };
			var plan = ParticleCueSeek.Evaluate( events, 1.0, Dt( Settings() ), static _ => true );
			Check( plan.SeekSteps == 60, $"seek steps {plan.SeekSteps}, expected 60" );
			Check( plan.Fired.Count == 1 && plan.Fired[0].TriggerStep == 60 && plan.Fired[0].BurstStep == 60 && plan.Fired[0].BurstCount == 70,
				$"plan fired: count {plan.Fired.Count}, step {(plan.Fired.Count > 0 ? plan.Fired[0].TriggerStep : -1)}, burst {(plan.Fired.Count > 0 ? plan.Fired[0].BurstCount : -1)}" );
			var (play, fireSteps, playAlive) = RunPlay( events, 90, new[] { 60, 90 } );
			var (seek, seekAlive) = RunSeek( plan, events, 90, new[] { 60, 90 } );
			Check( fireSteps["sparks"] == 60, $"play fired sparks at step {fireSteps["sparks"]}, expected 60" );
			Check( playAlive[60] == 0 && seekAlive[60] == 0, $"alive at 60 steps: play {playAlive[60]}, seek {seekAlive[60]} (burst at step 60 has not fired yet)" );
			Check( playAlive[90] == 70 && seekAlive[90] == 70, $"alive at 90 steps: play {playAlive[90]}, seek {seekAlive[90]}, expected 70" );
			Check( play.TotalEmitted == 70 && seek.TotalEmitted == 70, $"total emitted: play {play.TotalEmitted}, seek {seek.TotalEmitted}" );
		} );

		registry.Add( "particle-cue-seek", "two cues plus one rate cue: seek/play equivalence on burst and alive counts", () =>
		{
			var events = new[] { PresetBurst( "sparks", 0.5 ), PresetRate( "bubbles", 1.0 ), PresetBurst( "fire", 2.0 ) };
			var plan = ParticleCueSeek.Evaluate( events, 2.0, Dt( Settings() ), static _ => true );
			Check( plan.SeekSteps == 120, $"seek steps {plan.SeekSteps}, expected 120" );
			Check( plan.Fired.Count == 3, $"fired {plan.Fired.Count}, expected 3" );
			Check( plan.Fired[0].Name == "sparks" && plan.Fired[0].TriggerStep == 30, $"first fired {plan.Fired[0].Name}@{plan.Fired[0].TriggerStep}, expected sparks@30" );
			Check( plan.Fired[1].Name == "bubbles" && plan.Fired[1].Kind == ParticleCueSeekKind.Rate && plan.Fired[1].RunningSinceSeconds == 1.0 && plan.Fired[1].StepsRunning == 60,
				$"rate report: {plan.Fired[1].Name} since {plan.Fired[1].RunningSinceSeconds}s running {plan.Fired[1].StepsRunning} steps, expected since 1.0 running 60" );
			Check( plan.Fired[2].Name == "fire" && plan.Fired[2].TriggerStep == 120 && !plan.Fired[2].BurstEmittedBySeekTime,
				$"fire at seek time: step {plan.Fired[2].TriggerStep}, emitted {plan.Fired[2].BurstEmittedBySeekTime} (at-t burst must be scheduled, not emitted)" );
			var (play, fireSteps, playAlive) = RunPlay( events, 150, new[] { 120, 150 } );
			var (seek, seekAlive) = RunSeek( plan, events, 150, new[] { 120, 150 } );
			Check( fireSteps["sparks"] == 30 && fireSteps["bubbles"] == 60 && fireSteps["fire"] == 120,
				$"play fire steps: sparks {fireSteps["sparks"]}, bubbles {fireSteps["bubbles"]}, fire {fireSteps["fire"]}" );
			// At 120 steps: sparks 70 + bubbles 60 steps x 40/s = 40; fire (step 120) not yet emitted.
			Check( playAlive[120] == 110 && seekAlive[120] == 110, $"alive at 120 steps: play {playAlive[120]}, seek {seekAlive[120]}, expected 110" );
			// At 150 steps: sparks 70 + bubbles 90 steps x 40/s = 60 + fire 120 = 250.
			Check( playAlive[150] == 250 && seekAlive[150] == 250, $"alive at 150 steps: play {playAlive[150]}, seek {seekAlive[150]}, expected 250" );
			Check( play.TotalEmitted == 250 && seek.TotalEmitted == 250, $"total emitted: play {play.TotalEmitted}, seek {seek.TotalEmitted}" );
		} );

		registry.Add( "particle-cue-seek", "real TriggerCue retimes a burst to the current step", () =>
		{
			var system = new ParticleSystem( Settings() );
			system.Step( 5 );
			var em = system.TriggerCue( "sparks", Origin() );
			Check( em is not null, "TriggerCue returned null for sparks" );
			Check( em!.Bursts.Count == 1 && em.Bursts[0].Step == 5 && em.Bursts[0].Count == 70,
				$"TriggerCue burst: step {(em.Bursts.Count > 0 ? em.Bursts[0].Step : -1)}, count {(em.Bursts.Count > 0 ? em.Bursts[0].Count : -1)}, expected step 5 count 70" );
		} );

		registry.Add( "particle-cue-seek", "boundary: cue exactly at t counts as fired, burst fires on the next step", () =>
		{
			var events = new[] { Burst( "a", 0.5, 10 ), Burst( "b", 1.0, 20 ) };
			var plan = ParticleCueSeek.Evaluate( events, 1.0, Dt( Settings() ), static _ => true );
			Check( plan.Fired.Count == 2, $"fired {plan.Fired.Count}, expected 2 (at-t cue counts as fired)" );
			Check( plan.Fired[0].BurstEmittedBySeekTime, "earlier cue must report its burst emitted by seek time" );
			Check( !plan.Fired[1].BurstEmittedBySeekTime && plan.Fired[1].BurstStep == 60,
				$"at-t cue: emitted {plan.Fired[1].BurstEmittedBySeekTime}, burst step {plan.Fired[1].BurstStep} (scheduled at 60, fires on the next step)" );
			var simEvents = new[] { PresetBurst( "sparks", 1.0 ) };
			var simPlan = ParticleCueSeek.Evaluate( simEvents, 1.0, Dt( Settings() ), static _ => true );
			var (_, aliveAt) = RunSeek( simPlan, simEvents, 61, new[] { 60, 61 } );
			Check( aliveAt[60] == 0 && aliveAt[61] == 70, $"at-t burst in sim: alive at 60 steps {aliveAt[60]}, at 61 steps {aliveAt[61]}, expected 0 then 70" );
		} );

		registry.Add( "particle-cue-seek", "seek to 0 fires nothing, even a cue at time 0", () =>
		{
			var events = new[] { Burst( "at_zero", 0.0, 10 ), Burst( "later", 1.0, 20 ), Rate( "loop", 0.5, 40 ) };
			var plan = ParticleCueSeek.Evaluate( events, 0.0, Dt( Settings() ), static _ => true );
			Check( plan.SeekSteps == 0 && plan.Fired.Count == 0, $"seek(0): steps {plan.SeekSteps}, fired {plan.Fired.Count}" );
			Check( plan.Pending.Count == 3, $"seek(0): pending {plan.Pending.Count}, expected 3" );
			Check( plan.SkippedNoOrigin.Count == 0, $"seek(0): skipped {plan.SkippedNoOrigin.Count}" );
		} );

		registry.Add( "particle-cue-seek", "backwards seek re-derives from scratch", () =>
		{
			var events = new[] { Burst( "a", 0.5, 10 ), Burst( "b", 2.0, 20 ), Burst( "c", 4.0, 30 ) };
			var dt = Dt( Settings() );
			var first = ParticleCueSeek.Evaluate( events, 5.0, dt, static _ => true );
			Check( first.Fired.Count == 3, $"seek(5): fired {first.Fired.Count}, expected 3" );
			var back = ParticleCueSeek.Evaluate( events, 2.0, dt, static _ => true );
			var fresh = ParticleCueSeek.Evaluate( events, 2.0, dt, static _ => true );
			Check( back.ToCanonicalString() == fresh.ToCanonicalString(), $"backwards seek differs from fresh seek: {back.ToCanonicalString()} vs {fresh.ToCanonicalString()}" );
			Check( back.Fired.Count == 2 && back.Pending.Count == 1 && back.Pending[0].Name == "c",
				$"seek(2) after seek(5): fired {back.Fired.Count}, pending {back.Pending.Count} (must re-derive, never un-fire)" );
		} );

		registry.Add( "particle-cue-seek", "determinism: same timeline and t give bit-identical results", () =>
		{
			var events = new[] { Burst( "a", 1.0, 70 ), Rate( "b", 0.5, 40 ), Burst( "c", 1.0, 20 ) };
			var dt = Dt( Settings() );
			var one = ParticleCueSeek.Evaluate( events, 2.0, dt, static _ => true );
			var two = ParticleCueSeek.Evaluate( events, 2.0, dt, static _ => true );
			Check( one.ToCanonicalString() == two.ToCanonicalString(), $"two evaluations differ: {one.ToCanonicalString()} vs {two.ToCanonicalString()}" );
			Check( one.Fired.Count == 3 && one.Fired[0].BurstStep == one.Fired[0].TriggerStep,
				"determinism check needs fired cues with burst steps" );
		} );

		registry.Add( "particle-cue-seek", "origin-less cue skipped silently, never errors", () =>
		{
			var events = new[] { Burst( "foot", 0.5, 10 ), Burst( "hand", 1.0, 20 ) };
			var dt = Dt( Settings() );
			var none = ParticleCueSeek.Evaluate( events, 2.0, dt, null );
			Check( none.Fired.Count == 0 && none.SkippedNoOrigin.Count == 2,
				$"null resolver: fired {none.Fired.Count}, skipped {none.SkippedNoOrigin.Count} (resolver-less bridge fires nothing)" );
			var selective = ParticleCueSeek.Evaluate( events, 2.0, dt, key => key == "hand" );
			Check( selective.Fired.Count == 1 && selective.Fired[0].Name == "hand",
				$"selective resolver: fired [{string.Join( ",", selective.Fired.Select( f => f.Name ) )}], expected only hand" );
			Check( selective.SkippedNoOrigin.Count == 1 && selective.SkippedNoOrigin[0].Name == "foot",
				$"selective resolver: skipped [{string.Join( ",", selective.SkippedNoOrigin.Select( s => s.Name ) )}], expected foot" );
			var nullKey = ParticleCueSeek.Evaluate( new[] { new ParticleCueSeek.ParticleCueEvent( "nokey", 0.5, null, ParticleCueSeekKind.Burst, 10, 0 ) }, 2.0, dt, static _ => true );
			Check( nullKey.Fired.Count == 0 && nullKey.SkippedNoOrigin.Count == 1,
				"null origin key must skip, never fire at a fabricated origin" );
		} );

		registry.Add( "particle-cue-seek", "rate cue reported running-since-trigger, and a fresh add at t is not a seek", () =>
		{
			var events = new[] { PresetRate( "bubbles", 1.0 ) };
			var plan = ParticleCueSeek.Evaluate( events, 2.0, Dt( Settings() ), static _ => true );
			Check( plan.Fired.Count == 1 && plan.Fired[0].RunningSinceSeconds == 1.0 && plan.Fired[0].StepsRunning == 60,
				$"rate report: since {plan.Fired[0].RunningSinceSeconds}s, running {plan.Fired[0].StepsRunning} steps, expected since 1.0 running 60" );
			var (play, _, _) = RunPlay( events, 150, Array.Empty<int>() );
			Check( play.TotalEmitted == 60, $"replay total emitted {play.TotalEmitted}, expected 90 steps x 40/s = 60" );
			var fresh = new ParticleSystem( Settings() );
			fresh.Step( 120 );
			fresh.AddEmitter( LongLife( ParticleEmitterPresets.Create( "bubbles", Origin() ) ) );
			fresh.Step( 30 );
			Check( fresh.TotalEmitted == 20, $"fresh-at-t total emitted {fresh.TotalEmitted}, expected 30 steps x 40/s = 20 (jump-add loses history, so it is not a seek)" );
			Check( play.TotalEmitted != fresh.TotalEmitted, "replay and jump-add must differ, or the rate test proves nothing" );
		} );

		registry.Add( "particle-cue-seek", "seek past the last cue: all fired, pending empty", () =>
		{
			var events = new[] { Burst( "a", 0.5, 10 ), Burst( "b", 1.0, 20 ), Rate( "c", 2.0, 40 ) };
			var plan = ParticleCueSeek.Evaluate( events, 10.0, Dt( Settings() ), static _ => true );
			Check( plan.Fired.Count == 3 && plan.Pending.Count == 0, $"past-last: fired {plan.Fired.Count}, pending {plan.Pending.Count}" );
		} );

		registry.Add( "particle-cue-seek", "unsorted timeline sorts deterministically, ties keep timeline order, input untouched", () =>
		{
			var events = new[]
			{
				Burst( "late", 2.0, 30 ), Burst( "early", 0.5, 10 ), Burst( "mid", 1.0, 20 ),
				Burst( "tie_b", 1.0, 22 ), Burst( "tie_a", 1.0, 21 ),
			};
			var dt = Dt( Settings() );
			var plan = ParticleCueSeek.Evaluate( events, 3.0, dt, static _ => true );
			var names = string.Join( ",", plan.Fired.Select( f => f.Name ) );
			Check( names == "early,mid,tie_b,tie_a,late", $"sorted fired order: {names} (ties keep timeline order: tie_b was listed before tie_a)" );
			Check( events[0].Name == "late" && events[1].Name == "early", "Evaluate mutated the caller's timeline order" );
			var sortedInput = new[] { Burst( "early", 0.5, 10 ), Burst( "mid", 1.0, 20 ), Burst( "tie_b", 1.0, 22 ), Burst( "tie_a", 1.0, 21 ), Burst( "late", 2.0, 30 ) };
			Check( plan.ToCanonicalString() == ParticleCueSeek.Evaluate( sortedInput, 3.0, dt, static _ => true ).ToCanonicalString(),
				"unsorted and pre-sorted inputs must give identical results" );
		} );

		registry.Add( "particle-cue-seek", "loud failures: bad target, bad step, bad timeline", () =>
		{
			var events = new[] { Burst( "a", 0.5, 10 ) };
			var dt = Dt( Settings() );
			Throws<ArgumentOutOfRangeException>( () => ParticleCueSeek.Evaluate( events, -1.0, dt, static _ => true ), "negative t" );
			Throws<ArgumentException>( () => ParticleCueSeek.Evaluate( events, double.NaN, dt, static _ => true ), "NaN t" );
			Throws<ArgumentException>( () => ParticleCueSeek.Evaluate( events, double.PositiveInfinity, dt, static _ => true ), "infinite t" );
			Throws<ArgumentNullException>( () => ParticleCueSeek.Evaluate( null!, 1.0, dt, static _ => true ), "null timeline" );
			Throws<ArgumentOutOfRangeException>( () => ParticleCueSeek.Evaluate( events, 1.0, 0, static _ => true ), "zero step" );
			Throws<ArgumentOutOfRangeException>( () => ParticleCueSeek.Evaluate( events, 1.0, -0.01, static _ => true ), "negative step" );
			Throws<ArgumentException>( () => ParticleCueSeek.Evaluate( events, 1.0, double.NaN, static _ => true ), "NaN step" );
			Throws<ArgumentOutOfRangeException>( () => ParticleCueSeek.Evaluate( events, 1.0, 2.0, static _ => true ), "step above 1 s" );
		} );

		registry.Add( "particle-cue-seek", "loud failures: bad cue events", () =>
		{
			var dt = Dt( Settings() );
			Throws<ArgumentException>( () => ParticleCueSeek.Evaluate( new[] { new ParticleCueSeek.ParticleCueEvent( "", 0.5, "k", ParticleCueSeekKind.Burst, 10, 0 ) }, 1.0, dt, static _ => true ), "empty cue name" );
			Throws<ArgumentOutOfRangeException>( () => ParticleCueSeek.Evaluate( new[] { Burst( "a", -0.5, 10 ) }, 1.0, dt, static _ => true ), "negative trigger time" );
			Throws<ArgumentException>( () => ParticleCueSeek.Evaluate( new[] { Burst( "a", double.NaN, 10 ) }, 1.0, dt, static _ => true ), "NaN trigger time" );
			Throws<ArgumentOutOfRangeException>( () => ParticleCueSeek.Evaluate( new[] { Burst( "a", 0.5, -1 ) }, 1.0, dt, static _ => true ), "negative burst count" );
			Throws<ArgumentOutOfRangeException>( () => ParticleCueSeek.Evaluate( new[] { Rate( "a", 0.5, -1 ) }, 1.0, dt, static _ => true ), "negative rate" );
			Throws<ArgumentException>( () => ParticleCueSeek.Evaluate( new[] { Rate( "a", 0.5, double.NaN ) }, 1.0, dt, static _ => true ), "NaN rate" );
			Throws<ArgumentOutOfRangeException>( () => ParticleCueSeek.Evaluate( new[] { new ParticleCueSeek.ParticleCueEvent( "a", 0.5, "k", ParticleCueSeekKind.Burst, 10, 0, -1 ) }, 1.0, dt, static _ => true ), "negative burst step offset" );
		} );

		registry.Add( "particle-cue-seek", "step quantization: cue inside the seek step schedules with it", () =>
		{
			var events = new[] { Burst( "inside", 1.005, 10 ) };
			var plan = ParticleCueSeek.Evaluate( events, 1.0, Dt( Settings() ), static _ => true );
			Check( plan.Fired.Count == 1 && plan.Fired[0].TriggerStep == 60,
				$"cue at 1.005 s with t=1.0 s: fired {plan.Fired.Count}, step {(plan.Fired.Count > 0 ? plan.Fired[0].TriggerStep : -1)} (step-quantized: same step as the at-t cue)" );
		} );

		registry.Add( "particle-cue-seek", "pending cues are never added by seek itself", () =>
		{
			var events = new[] { PresetBurst( "sparks", 1.0 ), PresetBurst( "fire", 3.0 ) };
			var plan = ParticleCueSeek.Evaluate( events, 1.0, Dt( Settings() ), static _ => true );
			Check( plan.Fired.Count == 1 && plan.Pending.Count == 1 && plan.Pending[0].Name == "fire" && plan.Pending[0].TriggerStep == 180,
				$"pending: fired {plan.Fired.Count}, pending [{string.Join( ",", plan.Pending.Select( p => p.Name ) )}]" );
			var (seek, _) = RunSeek( plan, events, 90, Array.Empty<int>() );
			Check( seek.Emitters.Count == 1, $"seek system emitters {seek.Emitters.Count}, expected 1 (pending fire must not be added by seek)" );
			Check( seek.AliveCount == 70, $"seek alive {seek.AliveCount}, expected sparks 70 only" );
		} );
	}
}
