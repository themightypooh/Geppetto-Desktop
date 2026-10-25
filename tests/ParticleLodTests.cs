using System;
using Marionette.Desktop;

internal static class ParticleLodTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; } // also covers ArgumentOutOfRangeException, its subclass
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}
		ParticleLodSettings Lod() => ParticleLodSettings.Default();

		registry.Add( "particle-lod", "default bands at known distances", () =>
		{
			var s = Lod();
			// Near band: < 600 in.
			Near( s.RateMultiplierFor( 0f ), 1f, 0f, "rate at 0" );
			Near( s.SizeMultiplierFor( 0f ), 1f, 0f, "size at 0" );
			Near( s.RateMultiplierFor( 599f ), 1f, 0f, "rate at 599" );
			// Mid band: 600 <= d < 2400.
			Near( s.RateMultiplierFor( 600f ), 0.5f, 0f, "rate at 600" );
			Near( s.SizeMultiplierFor( 600f ), 0.85f, 0f, "size at 600" );
			Near( s.RateMultiplierFor( 2399f ), 0.5f, 0f, "rate at 2399" );
			// Far band: d >= 2400.
			Near( s.RateMultiplierFor( 2400f ), 0.25f, 0f, "rate at 2400" );
			Near( s.SizeMultiplierFor( 2400f ), 0.7f, 0f, "size at 2400" );
			Near( s.RateMultiplierFor( 100000f ), 0.25f, 0f, "rate at 100000" );
			Check( s.BandIndexFor( 100f ) == 0 && s.BandIndexFor( 1000f ) == 1 && s.BandIndexFor( 5000f ) == 2, "band indices 0/1/2 expected" );
		} );

		registry.Add( "particle-lod", "custom band table follows its own thresholds", () =>
		{
			var s = Lod();
			s.Bands = new[] { new ParticleLodBand( 100f, 1f, 1f ), new ParticleLodBand( float.PositiveInfinity, 0.1f, 0.5f ) };
			Near( s.RateMultiplierFor( 99f ), 1f, 0f, "custom rate near" );
			Near( s.RateMultiplierFor( 100f ), 0.1f, 0f, "custom rate far" );
			Near( s.SizeMultiplierFor( 500f ), 0.5f, 0f, "custom size far" );
		} );

		registry.Add( "particle-lod", "budget below budget allows full request", () =>
		{
			var s = Lod(); // MaxLiveParticles 10000.
			Check( s.AllowedSpawns( 0, 500 ) == 500, "empty system allows 500" );
			Check( s.AllowedSpawns( 9900, 500 ) == 100, "9900 live allows only 100 of 500" );
			Check( s.AllowedSpawns( 9999, 1 ) == 1, "one slot left allows one" );
		} );

		registry.Add( "particle-lod", "budget at and above budget allows zero, never negative", () =>
		{
			var s = Lod();
			Check( s.AllowedSpawns( 10000, 500 ) == 0, "at budget allows 0" );
			Check( s.AllowedSpawns( 12000, 500 ) == 0, "over budget allows 0, not negative" );
			Check( s.AllowedSpawns( 10000, 0 ) == 0, "zero request at budget is 0" );
			Check( s.CullCount( 12000, 500 ) == 0, "cull count is 0 when CullOldest is off, even over budget" );
		} );

		registry.Add( "particle-lod", "budget zero cap spawns nothing", () =>
		{
			var s = Lod(); s.MaxLiveParticles = 0;
			Check( s.AllowedSpawns( 0, 10 ) == 0, "zero budget allows nothing" );
		} );

		registry.Add( "particle-lod", "cull-oldest option reports exact cull count", () =>
		{
			var s = Lod(); s.CullOldest = true;
			Check( s.AllowedSpawns( 10000, 500 ) == 500, "cull-oldest allows the full request" );
			Check( s.CullCount( 10000, 500 ) == 500, "at budget, cull 500 to spawn 500" );
			Check( s.CullCount( 9900, 500 ) == 400, "9900 + 500 - 10000 = 400 to cull" );
			Check( s.CullCount( 5000, 500 ) == 0, "room available, cull nothing" );
			Check( s.CullCount( 12000, 500 ) == 2500, "already over budget, cull the 2000 excess plus 500" );
		} );

		registry.Add( "particle-lod", "far-field collapse boundary", () =>
		{
			var s = Lod(); // FarDistance 2400.
			Check( !s.IsFar( 2400f ), "exactly at far distance is not far (strictly beyond)" );
			Check( s.IsFar( 2401f ), "just beyond far distance is far" );
			Check( !s.IsFar( 0f ), "camera distance 0 is not far" );
		} );

		registry.Add( "particle-lod", "far-field update skipping every Nth step", () =>
		{
			var s = Lod(); // N = 4.
			int updated = 0;
			for ( long step = 0; step < 12; step++ ) if ( s.ShouldUpdate( step ) ) updated++;
			Check( updated == 3, $"N=4 over steps 0..11 should update 3 times, got {updated}" );
			Check( s.ShouldUpdate( 0 ) && s.ShouldUpdate( 4 ) && s.ShouldUpdate( 8 ), "updates on 0, 4, 8" );
			Check( !s.ShouldUpdate( 1 ) && !s.ShouldUpdate( 2 ) && !s.ShouldUpdate( 3 ) && !s.ShouldUpdate( 5 ), "no updates on 1, 2, 3, 5" );
		} );

		registry.Add( "particle-lod", "far-field staggered slots each update once per N steps", () =>
		{
			var s = Lod(); // N = 4.
			for ( int slot = 0; slot < 4; slot++ )
			{
				int updated = 0;
				for ( long step = 0; step < 12; step++ ) if ( s.ShouldUpdate( step, slot ) ) updated++;
				Check( updated == 3, $"slot {slot} should update 3 times in 12 steps, got {updated}" );
			}
			// Staggering: the four slots do not all fire on the same step.
			int step0 = 0;
			for ( int slot = 0; slot < 4; slot++ ) if ( s.ShouldUpdate( 0, slot ) ) step0++;
			Check( step0 == 1, $"exactly one slot should update on step 0, got {step0}" );
		} );

		registry.Add( "particle-lod", "far-field lag bound is N steps times velocity", () =>
		{
			var s = Lod(); // N = 4, dt = 1/60.
			Near( s.MaxLagSeconds(), 4f / 60f, 1e-6f, "max lag seconds" );
			Near( s.MaxPositionLag( 120f ), 8f, 1e-4f, "120 in/s over 4 steps at 60 Hz lags at most 8 in" );
			Near( s.MaxPositionLag( 0f ), 0f, 0f, "still particle lags 0" );
			var fast = Lod(); fast.UpdateIntervalN = 1;
			Near( fast.MaxPositionLag( 120f ), 2f, 1e-4f, "N=1 lags at most one step (2 in at 120 in/s)" );
		} );

		registry.Add( "particle-lod", "validate rejects unsorted band thresholds", () =>
		{
			var s = Lod();
			s.Bands = new[] { new ParticleLodBand( 2400f, 0.5f, 0.85f ), new ParticleLodBand( 600f, 1f, 1f ) };
			Throws( () => s.Validate(), "descending thresholds" );
			s.Bands = new[] { new ParticleLodBand( 600f, 1f, 1f ), new ParticleLodBand( 600f, 0.5f, 0.85f ) };
			Throws( () => s.Validate(), "equal thresholds are not strictly increasing" );
			s.Bands = new ParticleLodBand[0];
			Throws( () => s.Validate(), "empty band table" );
			s.Bands = new[] { new ParticleLodBand( float.PositiveInfinity, 1f, 1f ), new ParticleLodBand( float.PositiveInfinity, 0.5f, 0.85f ) };
			Throws( () => s.Validate(), "infinite threshold on a non-last band" );
		} );

		registry.Add( "particle-lod", "validate rejects negative budgets", () =>
		{
			var s = Lod(); s.MaxLiveParticles = -1;
			Throws( () => s.Validate(), "negative budget" );
			Throws( () => s.AllowedSpawns( 0, 10 ), "queries re-check a bad budget" );
		} );

		registry.Add( "particle-lod", "validate rejects non-positive N values", () =>
		{
			var s = Lod(); s.UpdateIntervalN = 0;
			Throws( () => s.Validate(), "N = 0" );
			s.UpdateIntervalN = -3;
			Throws( () => s.Validate(), "N negative" );
			Throws( () => s.ShouldUpdate( 0 ), "queries re-check a bad N" );
		} );

		registry.Add( "particle-lod", "validate rejects multipliers outside documented ranges", () =>
		{
			var overRate = Lod();
			overRate.Bands = new[] { new ParticleLodBand( float.PositiveInfinity, 1.5f, 1f ) };
			Throws( () => overRate.Validate(), "rate above 1" );
			var negRate = Lod();
			negRate.Bands = new[] { new ParticleLodBand( float.PositiveInfinity, -0.1f, 1f ) };
			Throws( () => negRate.Validate(), "rate below 0" );
			var overSize = Lod();
			overSize.Bands = new[] { new ParticleLodBand( float.PositiveInfinity, 1f, 2.5f ) };
			Throws( () => overSize.Validate(), "size above 2" );
			var negSize = Lod();
			negSize.Bands = new[] { new ParticleLodBand( float.PositiveInfinity, 1f, -0.1f ) };
			Throws( () => negSize.Validate(), "size below 0" );
			// Boundary values themselves are legal.
			var edge = Lod();
			edge.Bands = new[] { new ParticleLodBand( 100f, 0f, 0f ), new ParticleLodBand( float.PositiveInfinity, 1f, 2f ) };
			edge.Validate();
		} );

		registry.Add( "particle-lod", "validate rejects bad distances and counts", () =>
		{
			var s = Lod();
			Throws( () => s.RateMultiplierFor( -1f ), "negative distance" );
			Throws( () => s.RateMultiplierFor( float.NaN ), "NaN distance" );
			Throws( () => s.AllowedSpawns( -1, 10 ), "negative live count" );
			Throws( () => s.AllowedSpawns( 0, -10 ), "negative request" );
			Throws( () => s.ShouldUpdate( -1 ), "negative step" );
			Throws( () => s.ShouldUpdate( 0, -1 ), "negative slot" );
			s.FarDistance = -5f;
			Throws( () => s.Validate(), "negative far distance" );
		} );

		registry.Add( "particle-lod", "defaults validate and rate never increases emission", () =>
		{
			var s = Lod();
			s.Validate();
			for ( float d = 0f; d <= 10000f; d += 250f )
				Check( s.RateMultiplierFor( d ) <= 1f && s.RateMultiplierFor( d ) >= 0f, $"rate at {d} stays in [0, 1]" );
		} );
	}
}
