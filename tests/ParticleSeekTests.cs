using System.Numerics;
using Marionette.Desktop;

internal static class ParticleSeekTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		static void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		static void Throws<T>( Action body, string reason ) where T : Exception
		{
			try { body(); } catch ( T ) { return; }
			throw new Exception( reason );
		}

		// One burst + one rate emitter, seeded, no forces beyond default gravity:
		// burst 50 at step 0, rate 60/s, lifetime exactly 2 s (120 steps).
		static ParticleSystem BuildSystem( uint seed = 7 )
		{
			var system = new ParticleSystem( new ParticleSystemSettings { Capacity = 1000, Seed = seed } );
			var em = new ParticleEmitter
			{
				Shape = ParticleEmitterShape.Point, Direction = new Vector3( 0, 0, 1 ),
				Speed = 100f, SpeedJitter = 0.5f, SpreadDegrees = 30f,
				LifetimeMin = 2f, LifetimeMax = 2f, SizeMin = 1f, SizeMax = 2f,
				Rate = 60f, Bursts = new List<ParticleBurst> { new( 0, 50 ) },
			};
			system.AddEmitter( em );
			return system;
		}

		// Rate-only fountain, lifetime exactly 2 s: exact alive counts, no burst.
		static ParticleSystem BuildFountain( uint seed = 7 )
		{
			var system = new ParticleSystem( new ParticleSystemSettings { Capacity = 1000, Seed = seed } );
			system.AddEmitter( new ParticleEmitter
			{
				Shape = ParticleEmitterShape.Point, Direction = new Vector3( 0, 0, 1 ),
				Speed = 30f, SpeedJitter = 0.2f, SpreadDegrees = 10f,
				LifetimeMin = 2f, LifetimeMax = 2f, SizeMin = 1f, SizeMax = 1f, Rate = 120f,
			} );
			return system;
		}

		registry.Add( "particle-seek", "Seek(t) twice gives bit-identical states", () =>
		{
			var seeker = new ParticleSeek( BuildSystem() );
			var a = seeker.Seek( 1.0 );
			var b = seeker.Seek( 1.0 );
			Check( a.StepIndex == 60 && b.StepIndex == 60, $"expected StepIndex 60, got {a.StepIndex}/{b.StepIndex}" );
			// Time is StepIndex * (double)(float)(1/60), so 60 steps is 1.00000005 s, not exactly 1.0.
			Check( Math.Abs( a.Time - 1.0 ) < 1e-6, $"seeked Time {a.Time} is not ~1.0" );
			Check( ParticleSeek.StatesEqual( a, b, out var diff ), $"Seek(1.0) twice differs: {diff}" );
			Check( a.AliveCount > 0, "seeked system is empty" );
		} );

		registry.Add( "particle-seek", "Seek(t) equals stepping live to t from the start", () =>
		{
			var seeker = new ParticleSeek( BuildSystem() );
			var seeked = seeker.Seek( 1.0 );
			var live = BuildSystem();
			live.Step( 60 );
			Check( ParticleSeek.StatesEqual( seeked, live, out var diff ), $"Seek(1.0) != live 60 steps: {diff}" );
			var seeked2 = seeker.Seek( 2.5 );
			var live2 = BuildSystem();
			live2.Step( 150 );
			Check( ParticleSeek.StatesEqual( seeked2, live2, out var diff2 ), $"Seek(2.5) != live 150 steps: {diff2}" );
		} );

		registry.Add( "particle-seek", "Seek(0) is the fresh initial state", () =>
		{
			var seeker = new ParticleSeek( BuildSystem() );
			var at0 = seeker.Seek( 0 );
			var fresh = BuildSystem();
			Check( at0.StepIndex == 0 && at0.AliveCount == 0, $"Seek(0) not fresh: step {at0.StepIndex}, alive {at0.AliveCount}" );
			Check( ParticleSeek.StatesEqual( at0, fresh, out var diff ), $"Seek(0) != fresh system: {diff}" );
		} );

		registry.Add( "particle-seek", "Seek snaps to the nearest fixed step", () =>
		{
			var seeker = new ParticleSeek( BuildSystem() );
			var near = seeker.Seek( 0.51 ); // 30.6 steps -> 31
			Check( near.StepIndex == 31, $"Seek(0.51) gave StepIndex {near.StepIndex}, expected 31" );
			Check( Math.Abs( near.Time - 0.51 ) < 1.0 / 120.0, $"snapped Time {near.Time} is more than half a step from 0.51" );
			Check( seeker.StepsFor( 1.0 ) == 60 && Math.Abs( seeker.TimeFor( 60 ) - 1.0 ) < 1e-6, "StepsFor/TimeFor round-trip failed" );
		} );

		registry.Add( "particle-seek", "Prewarm populates a looping fountain; 0 steps is empty", () =>
		{
			var seeker = new ParticleSeek( BuildFountain() );
			var cold = seeker.Prewarm( 0 );
			var warm60 = seeker.Prewarm( 60 );
			var warm180 = seeker.Prewarm( 180 );
			Check( cold.AliveCount == 0, $"Prewarm(0) has {cold.AliveCount} particles, expected 0" );
			// 120/s = 2/step, nothing dies before 120 steps: 60 steps -> exactly 120 alive.
			Check( warm60.AliveCount == 120, $"Prewarm(60) has {warm60.AliveCount} particles, expected 120" );
			// Lifetime exactly 2 s = 120 steps: at 180 steps only the last 120 steps' worth survives.
			Check( warm180.AliveCount == 240, $"Prewarm(180) has {warm180.AliveCount} particles, expected 240" );
			Check( warm180.AliveCount > warm60.AliveCount, "prewarm did not grow the population" );
		} );

		registry.Add( "particle-seek", "Prewarm is real steps, not a shortcut", () =>
		{
			var seeker = new ParticleSeek( BuildFountain() );
			var prewarmed = seeker.Prewarm( 60 );
			var seeked = seeker.Seek( 1.0 );
			Check( ParticleSeek.StatesEqual( prewarmed, seeked, out var diff ), $"Prewarm(60) != Seek(1.0): {diff}" );
		} );

		registry.Add( "particle-seek", "SeekToCue positions one step before a retimed burst, which then fires on its step", () =>
		{
			// Lane 4 shape: per-preset system (sparks medium), burst retimed by
			// the bridge to the cue step (120 = 2.0 s), exactly as TriggerCue does.
			var baseSettings = new ParticleSystemSettings { Capacity = 1000, Seed = 7 };
			var system = new ParticleSystem( ParticleEmitterPresets.SettingsFor( "sparks", baseSettings ) );
			var em = ParticleEmitterPresets.Create( "sparks", Vector3.Zero );
			em.Bursts = new List<ParticleBurst> { new( 120, 70 ) };
			system.AddEmitter( em );
			var seeker = new ParticleSeek( system );
			Check( system.Settings.Drag == 0.05f, $"sparks medium drag {system.Settings.Drag} is not 0.05" );
			var positioned = seeker.SeekToCue( 2.0 );
			Check( positioned.StepIndex == 119, $"SeekToCue(2.0) gave StepIndex {positioned.StepIndex}, expected 119 (one step before cue step 120)" );
			Check( Math.Abs( positioned.Time - (2.0 - 1.0 / 60.0) ) < 1e-6, $"SeekToCue Time {positioned.Time} is not one step before 2.0" );
			Check( positioned.AliveCount == 0, $"cue burst fired early: {positioned.AliveCount} alive before the cue step" );
			positioned.Step(); // advances clock to the cue step; burst not yet fired
			Check( positioned.StepIndex == 120 && positioned.AliveCount == 0, $"after one step: index {positioned.StepIndex}, alive {positioned.AliveCount} — burst fired a step early" );
			positioned.Step(); // Step() starting at step 120 fires the burst
			Check( positioned.AliveCount == 70, $"cue burst did not fire on its step: {positioned.AliveCount} alive, expected 70" );
		} );

		registry.Add( "particle-seek", "SeekToCue(0) returns the fresh system; ForPreset builds a per-preset seeker", () =>
		{
			var seeker = ParticleSeek.ForPreset( "sparks", Vector3.Zero, new ParticleSystemSettings { Capacity = 1000, Seed = 7 } );
			var at0 = seeker.SeekToCue( 0 );
			Check( at0.StepIndex == 0 && at0.AliveCount == 0, $"SeekToCue(0) not fresh: step {at0.StepIndex}, alive {at0.AliveCount}" );
			Check( at0.Settings.Drag == 0.05f, "ForPreset sparks system does not carry the sparks medium" );
			var fired = seeker.Seek( 0.5 );
			Check( fired.AliveCount > 0, "ForPreset sparks burst (step 0, 70) produced nothing by 0.5 s" );
		} );

		registry.Add( "particle-seek", "Validate loudly rejects negative times, negative steps, and past-maximum seeks", () =>
		{
			var seeker = new ParticleSeek( BuildSystem() );
			seeker.Validate();
			Throws<ArgumentOutOfRangeException>( () => seeker.Seek( -1.0 ), "negative seek time did not throw" );
			Throws<ArgumentOutOfRangeException>( () => seeker.SeekSteps( -1 ), "negative step count did not throw" );
			Throws<ArgumentOutOfRangeException>( () => seeker.Prewarm( -5 ), "negative prewarm did not throw" );
			Throws<ArgumentException>( () => seeker.Seek( double.NaN ), "NaN seek time did not throw" );
			Throws<ArgumentOutOfRangeException>( () => seeker.Seek( 601.0 ), "seek past default 600 s maximum did not throw" );
			Throws<ArgumentOutOfRangeException>( () => seeker.SeekSteps( 36001 ), "seek past default 36000-step maximum did not throw" );
			var tight = new ParticleSeek( BuildSystem() ) { MaxSeekSeconds = 1.0 };
			Throws<ArgumentOutOfRangeException>( () => tight.Seek( 2.0 ), "custom MaxSeekSeconds bound was not honoured" );
			Throws<ArgumentOutOfRangeException>( () => { var bad = new ParticleSeek( BuildSystem() ) { MaxSeekSeconds = 0 }; bad.Validate(); }, "MaxSeekSeconds 0 passed Validate" );
		} );

		registry.Add( "particle-seek", "Blueprint is a snapshot: later prototype mutation does not move seeks", () =>
		{
			var prototype = BuildFountain();
			var seeker = new ParticleSeek( prototype );
			prototype.Emitters[0].Rate = 6000f; // mutate the prototype after wrapping
			var seeked = seeker.Seek( 1.0 );
			Check( seeked.AliveCount == 120, $"snapshot seek gave {seeked.AliveCount} alive, expected 120 from the original 120/s rate" );
			Check( seeked.TotalEmitted == 120, $"snapshot seek emitted {seeked.TotalEmitted}, expected 120" );
		} );

		registry.Add( "particle-seek", "Factory seeker and same-seed seekers agree; a different seed differs", () =>
		{
			var factory = new ParticleSeek( () => BuildSystem() );
			var a = factory.Seek( 1.0 );
			var b = factory.Seek( 1.0 );
			Check( ParticleSeek.StatesEqual( a, b, out var diff ), $"factory Seek twice differs: {diff}" );
			var other = new ParticleSeek( BuildSystem( seed: 8 ) ).Seek( 1.0 );
			Check( !ParticleSeek.StatesEqual( a, other, out _ ), "different seed produced an identical state (seed ignored?)" );
			var same = new ParticleSeek( BuildSystem( seed: 7 ) ).Seek( 1.0 );
			Check( ParticleSeek.StatesEqual( a, same, out var diff2 ), $"same-seed different seeker differs: {diff2}" );
		} );

		registry.Add( "particle-seek", "Seek(1.0) from different histories is bit-identical", () =>
		{
			// History 1: a seeker built from a prototype already stepped to 3.0 s.
			// AddEmitter re-seeds every emitter stream from (seed, emitter index),
			// and the blueprint holds settings + emitter configuration only — no
			// stepped state, no RNG position, no rate remainder — so the seek
			// still starts from the initial state.
			var stepped = BuildSystem();
			stepped.Step( 180 );
			Check( stepped.StepIndex == 180, $"stepped prototype is at StepIndex {stepped.StepIndex}, expected 180" );
			var fromStepped = new ParticleSeek( stepped ).Seek( 1.0 );
			// History 2: one seeker that sought to 3.0 first, then seeks back.
			var seeker = new ParticleSeek( BuildSystem() );
			var wentFar = seeker.Seek( 3.0 );
			Check( wentFar.StepIndex == 180, $"Seek(3.0) gave StepIndex {wentFar.StepIndex}, expected 180" );
			var back = seeker.Seek( 1.0 );
			var fresh = new ParticleSeek( BuildSystem() ).Seek( 1.0 );
			Check( fromStepped.StepIndex == 60 && back.StepIndex == 60 && fresh.StepIndex == 60,
				$"histories disagree on StepIndex: stepped {fromStepped.StepIndex}, back {back.StepIndex}, fresh {fresh.StepIndex}" );
			Check( ParticleSeek.StatesEqual( fromStepped, fresh, out var diff1 ), $"seek from stepped prototype differs from fresh seek: {diff1}" );
			Check( ParticleSeek.StatesEqual( back, fresh, out var diff2 ), $"seek back from 3.0 differs from fresh seek: {diff2}" );
			// Spot value: burst 50 at step 0 + rate 60/s = 1/step over 60 steps,
			// nothing dies before step 120 (lifetime exactly 2 s).
			Check( fresh.AliveCount == 110, $"Seek(1.0) has {fresh.AliveCount} alive, expected 110" );
			Check( fresh.TotalEmitted == 110, $"Seek(1.0) emitted {fresh.TotalEmitted}, expected 110" );
		} );

		registry.Add( "particle-seek", "Seek(a) then stepping b equals Seek(a+b)", () =>
		{
			var seeker = new ParticleSeek( BuildSystem() );
			var composed = seeker.Seek( 1.0 );
			composed.Step( 60 );
			var direct = seeker.Seek( 2.0 );
			Check( composed.StepIndex == 120 && direct.StepIndex == 120,
				$"composition StepIndex mismatch: composed {composed.StepIndex}, direct {direct.StepIndex}" );
			Check( ParticleSeek.StatesEqual( composed, direct, out var diff ), $"Seek(1.0) + Step(60) != Seek(2.0): {diff}" );
			// Single steps compose exactly the same way as one Step(60) call.
			var single = seeker.Seek( 1.0 );
			for ( int i = 0; i < 60; i++ ) single.Step();
			Check( ParticleSeek.StatesEqual( single, direct, out var diff2 ), $"Seek(1.0) + 60x Step() != Seek(2.0): {diff2}" );
		} );

		registry.Add( "particle-seek", "Quantization boundary: exact half-step rounds away from zero", () =>
		{
			var seeker = new ParticleSeek( BuildSystem() );
			double dt = seeker.FixedDeltaTime; // float 1/60 as double
			double half = dt / 2; // division by 2 is exact: half/dt is exactly 0.5
			Check( seeker.StepsFor( half ) == 1, $"StepsFor(half step {half}) gave {seeker.StepsFor( half )}, expected 1 (ties away from zero)" );
			var atHalf = seeker.Seek( half );
			Check( atHalf.StepIndex == 1, $"Seek(half step) gave StepIndex {atHalf.StepIndex}, expected 1" );
			Check( seeker.StepsFor( half - 1e-6 ) == 0, $"just below half a step gave {seeker.StepsFor( half - 1e-6 )}, expected 0" );
			Check( seeker.Seek( half - 1e-6 ).StepIndex == 0, "just below half a step did not seek to step 0" );
			Check( seeker.StepsFor( dt * 2.25 ) == 2, $"StepsFor(2.25 steps) gave {seeker.StepsFor( dt * 2.25 )}, expected 2" );
		} );

		registry.Add( "particle-seek", "ParticleSeekParams validates and applies exactly like Seek/Prewarm", () =>
		{
			var seeker = new ParticleSeek( BuildSystem() );
			var p = new ParticleSeekParams( TargetTime: 1.0, PrewarmSteps: 60 );
			p.Validate();
			var viaSeek = p.SeekWith( seeker );
			Check( ParticleSeek.StatesEqual( viaSeek, seeker.Seek( 1.0 ), out var diff1 ), $"SeekWith != Seek: {diff1}" );
			var viaPrewarm = p.PrewarmWith( seeker );
			Check( ParticleSeek.StatesEqual( viaPrewarm, seeker.Prewarm( 60 ), out var diff2 ), $"PrewarmWith != Prewarm: {diff2}" );
			var zero = new ParticleSeekParams();
			zero.Validate();
			Check( zero.PrewarmWith( seeker ).AliveCount == 0, "default params prewarm is not the documented no-op" );
			Check( zero.SeekWith( seeker ).StepIndex == 0, "default params seek is not the initial state" );
			Throws<ArgumentException>( () => new ParticleSeekParams( TargetTime: double.NaN ).Validate(), "NaN TargetTime passed Validate" );
			Throws<ArgumentException>( () => new ParticleSeekParams( TargetTime: double.PositiveInfinity ).Validate(), "infinite TargetTime passed Validate" );
			Throws<ArgumentOutOfRangeException>( () => new ParticleSeekParams( TargetTime: -1.0 ).Validate(), "negative TargetTime passed Validate" );
			Throws<ArgumentOutOfRangeException>( () => new ParticleSeekParams( TargetTime: 601.0 ).Validate(), "TargetTime past 600 s maximum passed Validate" );
			Throws<ArgumentOutOfRangeException>( () => new ParticleSeekParams( PrewarmSteps: -5 ).Validate(), "negative PrewarmSteps passed Validate" );
		} );
	}
}
