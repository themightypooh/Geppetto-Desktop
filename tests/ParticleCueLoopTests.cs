using Marionette.Desktop;

internal static class ParticleCueLoopTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		static void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		static void Near( double actual, double expected, string what )
		{
			Check( Math.Abs( actual - expected ) < 1e-9, $"{what}: expected {expected}, got {actual}" );
		}
		static void Active( ParticleCueLoopSchedule s, int iteration, double phase, int direction, string what )
		{
			Check( s.Active, $"{what}: expected active, got inactive" );
			Check( s.Iteration == iteration, $"{what}: expected iteration {iteration}, got {s.Iteration}" );
			Near( s.Phase, phase, $"{what} phase" );
			Check( s.Direction == direction, $"{what}: expected direction {direction}, got {s.Direction}" );
		}
		static void Inactive( ParticleCueLoopSchedule s, string what )
		{
			Check( !s.Active, $"{what}: expected inactive, got active iteration {s.Iteration} phase {s.Phase}" );
			Check( s.Iteration == -1 && s.Phase == 0.0 && s.Direction == 0, $"{what}: inactive answer was not (iteration -1, phase 0, direction 0): got ({s.Iteration}, {s.Phase}, {s.Direction})" );
		}
		static void Throws( Action action, string what )
		{
			bool threw = false;
			try { action(); } catch ( ArgumentException ) { threw = true; }
			Check( threw, $"{what}: expected a loud ArgumentException, got silent success" );
		}

		registry.Add( "particle-cue-loop", "count mode: exact schedule at known times", () =>
		{
			// Duration 2, delay 0.5, gap 0.5, 2 iterations:
			// iter 0 [0.5, 2.5), gap [2.5, 3.0), iter 1 [3.0, 5.0).
			var def = new ParticleCueLoopDefinition { Mode = ParticleCueLoopMode.Count, DurationSeconds = 2.0, StartDelaySeconds = 0.5, GapSeconds = 0.5, LoopCount = 2 };
			Inactive( def.ScheduleAt( 0.25 ), "t=0.25 before delay" );
			Active( def.ScheduleAt( 0.5 ), 0, 0.0, 1, "t=0.5 iter 0 start" );
			Active( def.ScheduleAt( 1.5 ), 0, 0.5, 1, "t=1.5 mid iter 0" );
			Inactive( def.ScheduleAt( 2.5 ), "t=2.5 iter 0 end = gap start" );
			Inactive( def.ScheduleAt( 2.75 ), "t=2.75 in gap" );
			Active( def.ScheduleAt( 3.0 ), 1, 0.0, 1, "t=3.0 iter 1 start" );
			Active( def.ScheduleAt( 4.0 ), 1, 0.5, 1, "t=4.0 mid iter 1" );
			Inactive( def.ScheduleAt( 5.0 ), "t=5.0 iter 1 end, count exhausted" );
			Inactive( def.ScheduleAt( 100.0 ), "t=100 long after exhaustion" );
		} );

		registry.Add( "particle-cue-loop", "count mode stops after exactly N iterations", () =>
		{
			var def = new ParticleCueLoopDefinition { Mode = ParticleCueLoopMode.Count, DurationSeconds = 1.0, LoopCount = 3 };
			Active( def.ScheduleAt( 2.999 ), 2, 0.999, 1, "t=2.999 last iteration" );
			Inactive( def.ScheduleAt( 3.0 ), "t=3.0 exactly N iterations done" );
			// Sweep: the only iterations ever active are 0, 1, 2.
			var seen = new HashSet<int>();
			for ( double t = 0; t < 5.0; t += 0.05 )
			{
				var s = def.ScheduleAt( t );
				if ( s.Active ) seen.Add( s.Iteration );
			}
			Check( seen.SetEquals( new[] { 0, 1, 2 } ), $"sweep saw iterations {string.Join( ",", seen )} instead of exactly 0,1,2" );
		} );

		registry.Add( "particle-cue-loop", "infinite mode never exhausts", () =>
		{
			// Duration 1, gap 1: iter i runs [2i, 2i+1).
			var def = new ParticleCueLoopDefinition { Mode = ParticleCueLoopMode.Infinite, DurationSeconds = 1.0, GapSeconds = 1.0 };
			Active( def.ScheduleAt( 100.5 ), 50, 0.5, 1, "t=100.5 iter 50" );
			Inactive( def.ScheduleAt( 101.5 ), "t=101.5 in gap after iter 50" );
			Active( def.ScheduleAt( 102.0 ), 51, 0.0, 1, "t=102 iter 51 start" );
			Active( def.ScheduleAt( 0.0 ), 0, 0.0, 1, "t=0 iter 0 start" );
		} );

		registry.Add( "particle-cue-loop", "ping-pong phase direction alternates per iteration", () =>
		{
			// Duration 2, no delay/gap, 4 iterations, reversible pattern.
			// Even iterations rise 0 -> 1, odd iterations fall 1 -> 0.
			var def = new ParticleCueLoopDefinition { Mode = ParticleCueLoopMode.PingPong, DurationSeconds = 2.0, LoopCount = 4, ReversiblePhase = true };
			Active( def.ScheduleAt( 0.5 ), 0, 0.25, 1, "t=0.5 forward iter 0" );
			Active( def.ScheduleAt( 2.0 ), 1, 1.0, -1, "t=2.0 reversed iter 1 starts at phase 1" );
			Active( def.ScheduleAt( 2.5 ), 1, 0.75, -1, "t=2.5 reversed iter 1 falling" );
			Active( def.ScheduleAt( 3.0 ), 1, 0.5, -1, "t=3.0 reversed iter 1 midpoint" );
			Active( def.ScheduleAt( 4.0 ), 2, 0.0, 1, "t=4.0 forward iter 2 restarts at 0" );
			Active( def.ScheduleAt( 6.5 ), 3, 0.75, -1, "t=6.5 reversed iter 3 falling" );
			Inactive( def.ScheduleAt( 8.0 ), "t=8.0 all 4 iterations done" );
			Check( def.ScheduleAt( 2.5 ).Reversed && !def.ScheduleAt( 0.5 ).Reversed, "Reversed flag did not follow odd/even iterations" );
		} );

		registry.Add( "particle-cue-loop", "time-based end and explicit stop both cut mid-iteration", () =>
		{
			var timed = new ParticleCueLoopDefinition { Mode = ParticleCueLoopMode.Count, DurationSeconds = 2.0, LoopCount = 10, EndTimeSeconds = 3.5 };
			Active( timed.ScheduleAt( 3.0 ), 1, 0.5, 1, "t=3.0 before time-based end" );
			Inactive( timed.ScheduleAt( 3.5 ), "t=3.5 exactly at time-based end" );
			Inactive( timed.ScheduleAt( 5.0 ), "t=5.0 after time-based end" );
			var stopped = new ParticleCueLoopDefinition { Mode = ParticleCueLoopMode.Infinite, DurationSeconds = 2.0 };
			Active( stopped.ScheduleAt( 1.0, stoppedAt: 1.5 ), 0, 0.5, 1, "t=1.0 before explicit stop" );
			Inactive( stopped.ScheduleAt( 1.5, stoppedAt: 1.5 ), "t=1.5 exactly at explicit stop" );
			Inactive( stopped.ScheduleAt( 2.5, stoppedAt: 1.5 ), "t=2.5 after explicit stop" );
			// Earlier cutoff wins when both are set.
			var both = new ParticleCueLoopDefinition { Mode = ParticleCueLoopMode.Infinite, DurationSeconds = 2.0, EndTimeSeconds = 9.0 };
			Inactive( both.ScheduleAt( 2.0, stoppedAt: 1.5 ), "explicit stop 1.5 beats end time 9.0" );
			var both2 = new ParticleCueLoopDefinition { Mode = ParticleCueLoopMode.Infinite, DurationSeconds = 2.0, EndTimeSeconds = 1.25 };
			Inactive( both2.ScheduleAt( 2.0, stoppedAt: 9.0 ), "end time 1.25 beats explicit stop 9.0" );
		} );

		registry.Add( "particle-cue-loop", "NextTransition values at known times", () =>
		{
			// Duration 2, delay 0.5, gap 0.5, 2 iterations:
			// starts 0.5 / 3.0, ends 2.5 / 5.0.
			var def = new ParticleCueLoopDefinition { Mode = ParticleCueLoopMode.Count, DurationSeconds = 2.0, StartDelaySeconds = 0.5, GapSeconds = 0.5, LoopCount = 2 };
			Near( def.NextTransition( 0.0 ) ?? -1, 0.5, "NextTransition(0) start" );
			Near( def.NextTransition( 0.5 ) ?? -1, 2.5, "NextTransition(0.5) iter 0 end" );
			Near( def.NextTransition( 1.0 ) ?? -1, 2.5, "NextTransition(1.0) iter 0 end" );
			Near( def.NextTransition( 2.5 ) ?? -1, 3.0, "NextTransition(2.5) next iter start" );
			Near( def.NextTransition( 3.5 ) ?? -1, 5.0, "NextTransition(3.5) iter 1 end" );
			Check( def.NextTransition( 5.0 ) is null, "NextTransition(5.0) should be null after exhaustion" );
			Check( def.NextTransition( 100.0 ) is null, "NextTransition(100) should be null" );
			// Infinite: transitions keep coming.
			var inf = new ParticleCueLoopDefinition { Mode = ParticleCueLoopMode.Infinite, DurationSeconds = 1.0, GapSeconds = 1.0 };
			Near( inf.NextTransition( 0.75 ) ?? -1, 1.0, "infinite NextTransition(0.75) iter end" );
			Near( inf.NextTransition( 1.0 ) ?? -1, 2.0, "infinite NextTransition(1.0) next start" );
			// Mid-iteration cutoff is itself a transition.
			var timed = new ParticleCueLoopDefinition { Mode = ParticleCueLoopMode.Infinite, DurationSeconds = 10.0, EndTimeSeconds = 3.5 };
			Near( timed.NextTransition( 1.0 ) ?? -1, 3.5, "timed NextTransition(1.0) cutoff" );
			Check( timed.NextTransition( 3.5 ) is null, "timed NextTransition(3.5) null at cutoff" );
			// Cutoff landing in a gap is silent: no wake-up for a
			// state that does not change (inactive stays inactive).
			var gapStop = new ParticleCueLoopDefinition { Mode = ParticleCueLoopMode.Infinite, DurationSeconds = 1.0, GapSeconds = 1.0 };
			Near( gapStop.NextTransition( 0.5, stoppedAt: 1.5 ) ?? -1, 1.0, "gap-stop NextTransition(0.5) iter end, not the stop" );
			Check( gapStop.NextTransition( 1.0, stoppedAt: 1.5 ) is null, "gap-stop NextTransition(1.0) null: next start 2.0 is past the stop" );
			// Every non-null transition is strictly after t.
			foreach ( double t in new[] { 0.0, 0.5, 1.0, 2.5, 3.5 } )
			{
				var n = def.NextTransition( t );
				Check( n is null || n.Value > t, $"NextTransition({t}) was not strictly after t: {n}" );
			}
		} );

		registry.Add( "particle-cue-loop", "Validate loudly rejects bad definitions", () =>
		{
			Throws( () => new ParticleCueLoopDefinition { DurationSeconds = 0 }.Validate(), "zero duration" );
			Throws( () => new ParticleCueLoopDefinition { DurationSeconds = -1 }.Validate(), "negative duration" );
			Throws( () => new ParticleCueLoopDefinition { DurationSeconds = double.NaN }.Validate(), "NaN duration" );
			Throws( () => new ParticleCueLoopDefinition { StartDelaySeconds = -0.5 }.Validate(), "negative delay" );
			Throws( () => new ParticleCueLoopDefinition { GapSeconds = -1 }.Validate(), "negative gap" );
			Throws( () => new ParticleCueLoopDefinition { LoopCount = -2 }.Validate(), "negative count" );
			Throws( () => new ParticleCueLoopDefinition { Mode = ParticleCueLoopMode.PingPong, ReversiblePhase = false }.Validate(), "ping-pong without reversible phase" );
			Throws( () => new ParticleCueLoopDefinition { EndTimeSeconds = double.NaN }.Validate(), "NaN end time" );
			Throws( () => new ParticleCueLoopDefinition { DurationSeconds = 1 }.ScheduleAt( double.NaN ), "NaN schedule time" );
			// Valid definitions do not throw, including the documented
			// degenerate (Count 0 = never fires) and Infinite ignoring
			// a leftover LoopCount.
			new ParticleCueLoopDefinition { DurationSeconds = 1, LoopCount = 0 }.Validate();
			new ParticleCueLoopDefinition { Mode = ParticleCueLoopMode.Infinite, DurationSeconds = 1, LoopCount = 99 }.Validate();
			new ParticleCueLoopDefinition { Mode = ParticleCueLoopMode.PingPong, DurationSeconds = 1, ReversiblePhase = true }.Validate();
			Inactive( new ParticleCueLoopDefinition { DurationSeconds = 1, LoopCount = 0 }.ScheduleAt( 0.5 ), "Count 0 never fires" );
			Check( new ParticleCueLoopDefinition { DurationSeconds = 1, LoopCount = 0 }.NextTransition( 0.0 ) is null, "Count 0 has no transition" );
		} );

		registry.Add( "particle-cue-loop", "pure function: repeated calls agree, no hidden state", () =>
		{
			var def = new ParticleCueLoopDefinition { Mode = ParticleCueLoopMode.PingPong, DurationSeconds = 2.0, LoopCount = 4, ReversiblePhase = true, StartDelaySeconds = 0.25, GapSeconds = 0.25 };
			var first = def.ScheduleAt( 2.75 );
			_ = def.ScheduleAt( 0.0 );
			_ = def.ScheduleAt( 99.0 );
			_ = def.NextTransition( 2.75 );
			var second = def.ScheduleAt( 2.75 );
			Check( first.Active == second.Active && first.Iteration == second.Iteration && first.Phase == second.Phase && first.Direction == second.Direction,
				$"interleaved calls changed the answer: first ({first.Active},{first.Iteration},{first.Phase},{first.Direction}) second ({second.Active},{second.Iteration},{second.Phase},{second.Direction})" );
			Check( def.DurationSeconds == 2.0 && def.LoopCount == 4, "querying mutated the definition" );
		} );

		registry.Add( "particle-cue-loop", "boundaries: start inclusive, end exclusive", () =>
		{
			var def = new ParticleCueLoopDefinition { DurationSeconds = 2.0, StartDelaySeconds = 1.0, LoopCount = 1 };
			Inactive( def.ScheduleAt( 0.999 ), "just before start" );
			Active( def.ScheduleAt( 1.0 ), 0, 0.0, 1, "exactly at start" );
			Active( def.ScheduleAt( 2.999 ), 0, 0.9995, 1, "just before end" );
			Inactive( def.ScheduleAt( 3.0 ), "exactly at end" );
			var timed = new ParticleCueLoopDefinition { Mode = ParticleCueLoopMode.Infinite, DurationSeconds = 5.0, EndTimeSeconds = 2.0 };
			Active( timed.ScheduleAt( 1.999 ), 0, 0.3998, 1, "just before end time" );
			Inactive( timed.ScheduleAt( 2.0 ), "exactly at end time" );
		} );
		registry.Add( "particle-cue-loop", "firing: forward times exact, count 3 period 2 start 1", () =>
		{
			var spec = new ParticleCueLoopSpec { LoopCount = 3, LoopPeriodSeconds = 2.0 };
			var at5 = spec.Evaluate( 1.0, 5.0 );
			Check( at5.Count == 3, $"expected 3 firings, got {at5.Count}" );
			Near( at5.Firings[0].Time, 1.0, "firing 0 time" );
			Near( at5.Firings[1].Time, 3.0, "firing 1 time" );
			Near( at5.Firings[2].Time, 5.0, "firing 2 time" );
			Check( at5.Firings[0].PassIndex == 0 && at5.Firings[1].PassIndex == 1 && at5.Firings[2].PassIndex == 2, "pass indices were not 0,1,2" );
			Check( at5.Firings[0].Direction == 1 && at5.Firings[1].Direction == 1 && at5.Firings[2].Direction == 1, "forward passes did not all report direction +1" );
			Check( at5.NextFiring is null, $"exhausted loop reported a next firing {at5.NextFiring} instead of null" );
			var before = spec.Evaluate( 1.0, 4.999 );
			Check( before.Count == 2 && before.NextFiring == 5.0, $"before last firing: expected 2 firings and next 5.0, got {before.Count} and {before.NextFiring}" );
		} );

		registry.Add( "particle-cue-loop", "firing: forward times exact, second spec", () =>
		{
			var spec = new ParticleCueLoopSpec { LoopCount = 4, LoopPeriodSeconds = 0.5 };
			var r = spec.Evaluate( 0.0, 1.5 );
			Check( r.Count == 4, $"expected 4 firings, got {r.Count}" );
			Near( r.Firings[0].Time, 0.0, "firing 0 time" );
			Near( r.Firings[1].Time, 0.5, "firing 1 time" );
			Near( r.Firings[2].Time, 1.0, "firing 2 time" );
			Near( r.Firings[3].Time, 1.5, "firing 3 time" );
			Check( r.NextFiring is null, "count-4 loop at its last firing reported a next firing" );
			var mid = spec.Evaluate( 0.0, 1.25 );
			Check( mid.Count == 3 && mid.NextFiring == 1.5, $"mid-loop: expected 3 firings and next 1.5, got {mid.Count} and {mid.NextFiring}" );
		} );

		registry.Add( "particle-cue-loop", "firing: ping-pong directions alternate F,R,F,R with mirrored vectors", () =>
		{
			var spec = new ParticleCueLoopSpec { LoopCount = 4, LoopPeriodSeconds = 2.0, PingPong = true };
			var dir = new System.Numerics.Vector3( 1, 0, 0 );
			var r = spec.Evaluate( 0.0, 6.0, dir );
			Check( r.Count == 4, $"expected 4 firings, got {r.Count}" );
			int[] dirs = { 1, -1, 1, -1 };
			bool[] rev = { false, true, false, true };
			for ( int i = 0; i < 4; i++ )
			{
				Near( r.Firings[i].Time, i * 2.0, $"pass {i} time" );
				Check( r.Firings[i].PassIndex == i, $"pass {i} index wrong" );
				Check( r.Firings[i].Direction == dirs[i], $"pass {i} direction: expected {dirs[i]}, got {r.Firings[i].Direction}" );
				Check( r.Firings[i].Reversed == rev[i], $"pass {i} reversed flag wrong" );
				Check( r.Firings[i].DirectionVector == new System.Numerics.Vector3( dirs[i], 0, 0 ), $"pass {i} vector: expected ({dirs[i]},0,0), got {r.Firings[i].DirectionVector}" );
			}
		} );

		registry.Add( "particle-cue-loop", "firing: ping-pong falls back to forward for directionless cues", () =>
		{
			var spec = new ParticleCueLoopSpec { LoopCount = 3, LoopPeriodSeconds = 1.0, PingPong = true };
			var noDir = spec.Evaluate( 0.0, 2.0, null );
			foreach ( var f in noDir.Firings )
				Check( f.Direction == 1 && !f.Reversed && f.DirectionVector is null, $"null-direction pass {f.PassIndex} did not fall back to forward: dir {f.Direction} rev {f.Reversed} vec {f.DirectionVector}" );
			var zero = spec.Evaluate( 0.0, 2.0, System.Numerics.Vector3.Zero );
			foreach ( var f in zero.Firings )
				Check( f.Direction == 1 && !f.Reversed && f.DirectionVector == System.Numerics.Vector3.Zero, $"zero-direction pass {f.PassIndex} did not fall back to forward: dir {f.Direction} rev {f.Reversed} vec {f.DirectionVector}" );
		} );

		registry.Add( "particle-cue-loop", "firing: exhausted loop reports no next firing", () =>
		{
			var spec = new ParticleCueLoopSpec { LoopCount = 2, LoopPeriodSeconds = 1.0 };
			var r = spec.Evaluate( 0.0, 10.0 );
			Check( r.Count == 2, $"expected 2 firings, got {r.Count}" );
			Check( r.NextFiring is null, $"exhausted loop at t=10 reported next {r.NextFiring} instead of null" );
			var atLast = spec.Evaluate( 0.0, 1.0 );
			Check( atLast.Count == 2 && atLast.NextFiring is null, "loop at its last firing did not report exhausted" );
		} );

		registry.Add( "particle-cue-loop", "firing: infinite loop arithmetic correct at large t", () =>
		{
			var spec = new ParticleCueLoopSpec { Infinite = true, LoopPeriodSeconds = 2.0 };
			// Brief arithmetic, stated exactly: firings at 0,2,...,98 are 50;
			// at t=100 the firing AT 100 also counts (firing-at-t rule), so
			// the total is 51 and the next firing is 102. The brief's "50"
			// is the count through t=98, not through t=100 inclusive.
			Check( spec.CountFirings( 0.0, 98.0 ) == 50, $"infinite count at t=98: expected 50, got {spec.CountFirings( 0.0, 98.0 )}" );
			Check( spec.CountFirings( 0.0, 99.0 ) == 50, $"infinite count at t=99: expected 50, got {spec.CountFirings( 0.0, 99.0 )}" );
			Check( spec.CountFirings( 0.0, 100.0 ) == 51, $"infinite count at t=100: expected 51 (0..100 step 2), got {spec.CountFirings( 0.0, 100.0 )}" );
			Check( spec.NextFiringTime( 0.0, 98.0 ) == 100.0, $"infinite next after t=98: expected 100.0, got {spec.NextFiringTime( 0.0, 98.0 )}" );
			Check( spec.NextFiringTime( 0.0, 100.0 ) == 102.0, $"infinite next after t=100: expected 102.0, got {spec.NextFiringTime( 0.0, 100.0 )}" );
			var r = spec.Evaluate( 0.0, 100.0 );
			Check( r.Count == 51, $"infinite evaluate at t=100: expected 51 firings, got {r.Count}" );
			Near( r.Firings[50].Time, 100.0, "infinite last firing at t=100" );
			Check( r.NextFiring == 102.0, $"infinite next firing at t=100: expected 102.0, got {r.NextFiring}" );
			// Arithmetic-only path at a huge t: no list is materialized.
			Check( spec.CountFirings( 0.0, 100000.0 ) == 50001, $"infinite count at t=100000: expected 50001, got {spec.CountFirings( 0.0, 100000.0 )}" );
			Check( spec.NextFiringTime( 0.0, 100000.0 ) == 100002.0, $"infinite next after t=100000: expected 100002.0, got {spec.NextFiringTime( 0.0, 100000.0 )}" );
		} );

		registry.Add( "particle-cue-loop", "firing: boundary firing exactly at t counts, pre-start query", () =>
		{
			var spec = new ParticleCueLoopSpec { LoopCount = 3, LoopPeriodSeconds = 2.0 };
			var atExact = spec.Evaluate( 1.0, 3.0 );
			Check( atExact.Count == 2 && atExact.Firings[1].Time == 3.0, $"firing exactly at t=3 did not count: got {atExact.Count} firings" );
			var atStart = spec.Evaluate( 1.0, 1.0 );
			Check( atStart.Count == 1 && atStart.Firings[0].Time == 1.0 && atStart.NextFiring == 3.0, "firing exactly at start did not count with next 3.0" );
			var pre = spec.Evaluate( 1.0, 0.999 );
			Check( pre.Count == 0 && pre.NextFiring == 1.0, $"pre-start query: expected 0 firings and next 1.0, got {pre.Count} and {pre.NextFiring}" );
		} );

		registry.Add( "particle-cue-loop", "firing: count 1 is a single firing", () =>
		{
			var spec = new ParticleCueLoopSpec { LoopCount = 1, LoopPeriodSeconds = 2.0 };
			var r = spec.Evaluate( 5.0, 5.0 );
			Check( r.Count == 1 && r.Firings[0].Time == 5.0 && r.Firings[0].PassIndex == 0 && r.NextFiring is null, "count-1 loop did not behave as a single firing" );
			var later = spec.Evaluate( 5.0, 99.0 );
			Check( later.Count == 1 && later.NextFiring is null, "count-1 loop grew firings after its single firing" );
		} );

		registry.Add( "particle-cue-loop", "firing: backwards query re-derives, pure function", () =>
		{
			var spec = new ParticleCueLoopSpec { LoopCount = 5, LoopPeriodSeconds = 2.0, PingPong = true };
			var dir = new System.Numerics.Vector3( 0, 0, 1 );
			var later = spec.Evaluate( 1.0, 9.0, dir );
			Check( later.Count == 5, $"expected 5 firings by t=9, got {later.Count}" );
			var earlier = spec.Evaluate( 1.0, 5.0, dir );
			var freshSpec = new ParticleCueLoopSpec { LoopCount = 5, LoopPeriodSeconds = 2.0, PingPong = true };
			var fresh = freshSpec.Evaluate( 1.0, 5.0, dir );
			Check( earlier.Count == fresh.Count, "backwards query count differed from a fresh query" );
			for ( int i = 0; i < earlier.Count; i++ )
			{
				Check( earlier.Firings[i].Time == fresh.Firings[i].Time && earlier.Firings[i].PassIndex == fresh.Firings[i].PassIndex && earlier.Firings[i].Direction == fresh.Firings[i].Direction && earlier.Firings[i].DirectionVector == fresh.Firings[i].DirectionVector,
					$"backwards query firing {i} differed from fresh query" );
				Check( earlier.Firings[i].Time == later.Firings[i].Time && earlier.Firings[i].Direction == later.Firings[i].Direction,
					$"backwards query firing {i} differed from the earlier query's prefix" );
			}
			Check( earlier.NextFiring == fresh.NextFiring, "backwards query next firing differed from a fresh query" );
		} );

		registry.Add( "particle-cue-loop", "firing: loud failures", () =>
		{
			Throws( () => new ParticleCueLoopSpec { LoopPeriodSeconds = 0 }.Validate(), "zero period" );
			Throws( () => new ParticleCueLoopSpec { LoopPeriodSeconds = -2 }.Validate(), "negative period" );
			Throws( () => new ParticleCueLoopSpec { LoopPeriodSeconds = double.NaN }.Validate(), "NaN period" );
			Throws( () => new ParticleCueLoopSpec { LoopPeriodSeconds = double.PositiveInfinity }.Validate(), "infinite period" );
			Throws( () => new ParticleCueLoopSpec { LoopCount = 0 }.Validate(), "count 0 finite" );
			Throws( () => new ParticleCueLoopSpec { LoopCount = -3 }.Validate(), "negative count finite" );
			Throws( () => new ParticleCueLoopSpec { LoopCount = 3, LoopPeriodSeconds = 1 }.Evaluate( double.NaN, 1.0 ), "NaN start" );
			Throws( () => new ParticleCueLoopSpec { LoopCount = 3, LoopPeriodSeconds = 1 }.Evaluate( 0.0, double.NaN ), "NaN query time" );
			Throws( () => new ParticleCueLoopSpec { Infinite = true, LoopPeriodSeconds = 1 }.CountFirings( 0.0, double.PositiveInfinity ), "infinite loop counted at +infinity" );
			var inf = new ParticleCueLoopSpec { Infinite = true, LoopPeriodSeconds = 1.0, LoopCount = 0 };
			inf.Validate();
			Check( inf.CountFirings( 0.0, 2.0 ) == 3, "infinite loop with a leftover LoopCount 0 did not ignore the count" );
		} );

		registry.Add( "particle-cue-loop", "firing: scheduled firing through TriggerCue matches manual trigger", () =>
		{
			var origin = new System.Numerics.Vector3( 10, 20, 30 );
			var spec = new ParticleCueLoopSpec { LoopCount = 3, LoopPeriodSeconds = 2.0, PingPong = true };
			var dir = new System.Numerics.Vector3( 1, 0, 0 );
			var firings = spec.Evaluate( 1.0, 5.0, dir );
			Check( firings.Count == 3 && firings.Firings[1].Time == 3.0 && firings.Firings[1].DirectionVector == new System.Numerics.Vector3( -1, 0, 0 ),
				"reverse scheduled firing was not t=3 with direction (-1,0,0)" );
			var sysA = new ParticleSystem( new ParticleSystemSettings() );
			var sysB = new ParticleSystem( new ParticleSystemSettings() );
			sysA.Step( 30 );
			sysB.Step( 30 );
			var viaSchedule = sysA.TriggerCue( "sparks", origin, firings.Firings[1].DirectionVector );
			var manual = sysB.TriggerCue( "sparks", origin, new System.Numerics.Vector3( -1, 0, 0 ) );
			Check( viaSchedule is not null && manual is not null, "TriggerCue returned null for sparks" );
			Check( viaSchedule!.Bursts.Count == 1 && manual!.Bursts.Count == 1, "expected exactly one retimed burst per trigger" );
			Check( viaSchedule.Bursts[0].Step == 30 && manual.Bursts[0].Step == 30, $"burst retiming differed from current step 30: schedule {viaSchedule.Bursts[0].Step}, manual {manual.Bursts[0].Step}" );
			Check( viaSchedule.Bursts[0].Count == manual.Bursts[0].Count, $"burst count differed: schedule {viaSchedule.Bursts[0].Count}, manual {manual.Bursts[0].Count}" );
			Check( viaSchedule.Direction == manual.Direction && viaSchedule.Direction == new System.Numerics.Vector3( -1, 0, 0 ), $"emitter direction differed: schedule {viaSchedule.Direction}, manual {manual.Direction}" );
			Check( viaSchedule.Position == origin && manual.Position == origin, "emitter origin differed from the bridge-supplied origin" );
		} );

	}
}
