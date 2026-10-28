using System;
using Marionette.Desktop;

// Wave-6 lane P74 (particles session, 2026-10-24): tests for Particle2DWind.cs.
// Same harness shape as Particle2DTests: registry.Add("particle-2d-wind", name, body).
// Ramps are LINEAR (see the production header): mid-attack and mid-decay are
// exactly 0.5, boundaries are exact at clean points. Periodicity uses a 1e-6
// tolerance because float modulo rounding can shift At(t + P) by ~1e-7 even
// when the shape repeats exactly; boundary values are asserted exactly.

internal static class Particle2DWindTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; }
			catch ( InvalidOperationException ) { return; }
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}

		GustFrontProfile Front() => new() { AttackSeconds = 1, SustainSeconds = 1, DecaySeconds = 1 };
		GustTrainProfile Train() => new() { AttackSeconds = 1, SustainSeconds = 1, DecaySeconds = 1, LullSeconds = 1 };

		registry.Add( "particle-2d-wind", "front: t=0 is 0, mid-attack is exactly 0.5", () =>
		{
			var f = Front();
			Near( f.At( 0 ), 0, 0, "front at t=0" );
			Near( f.At( 0.5f ), 0.5f, 0, "front mid-attack" );
		} );

		registry.Add( "particle-2d-wind", "front: peak is 1 at attack end and mid-sustain", () =>
		{
			var f = Front();
			Near( f.At( 1 ), 1, 0, "front at attack end" );
			Near( f.At( 1.5f ), 1, 0, "front mid-sustain" );
		} );

		registry.Add( "particle-2d-wind", "front: mid-decay is exactly 0.5", () =>
		{
			var f = Front();
			Near( f.At( 2.5f ), 0.5f, 0, "front mid-decay" );
		} );

		registry.Add( "particle-2d-wind", "front: boundaries and post-decay are exact", () =>
		{
			var f = Front();
			Near( f.At( 2 ), 1, 0, "front at decay start" );
			Near( f.At( 3 ), 0, 0, "front at total end" );
			Near( f.At( 4 ), 0, 0, "front after decay" );
			Near( f.At( 100 ), 0, 0, "front long after decay" );
		} );

		registry.Add( "particle-2d-wind", "front: negative time reads as pre-gust 0", () =>
		{
			var f = Front();
			Near( f.At( -1 ), 0, 0, "front at negative time" );
		} );

		registry.Add( "particle-2d-wind", "front: zero attack snaps on, zero decay snaps off", () =>
		{
			var f = new GustFrontProfile { AttackSeconds = 0, SustainSeconds = 1, DecaySeconds = 0 };
			f.Validate();
			Near( f.At( 0 ), 1, 0, "zero-attack front at t=0" );
			Near( f.At( 0.5f ), 1, 0, "zero-attack front mid-sustain" );
			Near( f.At( 1 ), 0, 0, "zero-decay front at sustain end" );
			Near( f.At( 1.5f ), 0, 0, "zero-decay front after sustain" );
		} );

		registry.Add( "particle-2d-wind", "train: repeats the front shape each period", () =>
		{
			var tr = Train();
			Near( tr.Period, 4, 0, "train period" );
			Near( tr.At( 0.5f ), 0.5f, 0, "train first mid-attack" );
			Near( tr.At( 4.5f ), 0.5f, 0, "train second mid-attack" );
			Near( tr.At( 8.5f ), 0.5f, 0, "train third mid-attack" );
			Near( tr.At( 5.5f ), 1, 0, "train second sustain" );
		} );

		registry.Add( "particle-2d-wind", "train: periodic at several probes", () =>
		{
			var tr = Train();
			float[] probes = { 0.25f, 0.9f, 1.7f, 2.6f, 3.4f };
			foreach ( float t in probes )
				Near( tr.At( t ), tr.At( t + tr.Period ), 1e-6f, $"train periodicity at t={t}" );
			Near( tr.At( 100.25f ), tr.At( 104.25f ), 1e-6f, "train periodicity at large t" );
		} );

		registry.Add( "particle-2d-wind", "train: lull is exactly 0, including mid-lull", () =>
		{
			var tr = Train();
			Near( tr.At( 3 ), 0, 0, "train at lull start" );
			Near( tr.At( 3.5f ), 0, 0, "train mid-lull" );
			Near( tr.At( 3.99f ), 0, 0, "train late lull" );
		} );

		registry.Add( "particle-2d-wind", "train: period boundary equals period start, negatives wrap", () =>
		{
			var tr = Train();
			Near( tr.At( 4 ), tr.At( 0 ), 0, "train period boundary vs start" );
			Near( tr.At( -3.5f ), 0.5f, 0, "train negative time wraps to mid-attack" );
			Near( tr.At( -0.5f ), 0, 0, "train negative time wraps into lull" );
		} );

		registry.Add( "particle-2d-wind", "noise: deterministic for a fixed seed", () =>
		{
			var a = new NoiseGustProfile { Seed = 7 };
			var b = new NoiseGustProfile { Seed = 7 };
			for ( int i = 0; i <= 100; i++ )
			{
				float t = i * 0.1f;
				Near( a.At( t ), b.At( t ), 0, $"noise determinism at t={t}" );
			}
		} );

		registry.Add( "particle-2d-wind", "noise: different seeds differ", () =>
		{
			var a = new NoiseGustProfile { Seed = 7 };
			var b = new NoiseGustProfile { Seed = 8 };
			float maxDiff = 0;
			for ( int i = 0; i <= 400; i++ )
			{
				float t = i * 0.05f;
				maxDiff = MathF.Max( maxDiff, MathF.Abs( a.At( t ) - b.At( t ) ) );
			}
			Check( maxDiff > 0.05f, $"different seeds barely differ: max diff {maxDiff} over 0..20 s" );
		} );

		registry.Add( "particle-2d-wind", "noise: continuous, no popping between samples", () =>
		{
			var n = new NoiseGustProfile { Seed = 7 };
			float maxDelta = 0;
			float prev = n.At( 0 );
			for ( int i = 1; i <= 2000; i++ )
			{
				float t = i * 0.01f;
				float v = n.At( t );
				maxDelta = MathF.Max( maxDelta, MathF.Abs( v - prev ) );
				prev = v;
			}
			Check( maxDelta < 0.05f, $"noise jumps between 0.01 s samples: max delta {maxDelta}" );
			foreach ( float cell in new float[] { 2f, 4f, 6f } )
			{
				float before = n.At( cell - 0.005f );
				float after = n.At( cell + 0.005f );
				Check( MathF.Abs( after - before ) < 0.05f, $"noise jumps across time cell at t={cell}: {before} -> {after}" );
			}
		} );

		registry.Add( "particle-2d-wind", "noise: range sweep stays in [0,1] and spans most of it", () =>
		{
			var n = new NoiseGustProfile { Seed = 7 };
			float min = float.MaxValue, max = float.MinValue;
			for ( int i = 0; i <= 2000; i++ )
			{
				float v = n.At( i * 0.01f );
				if ( v < min ) min = v;
				if ( v > max ) max = v;
			}
			Check( min >= 0, $"noise sweep below 0: min {min}" );
			Check( max <= 1, $"noise sweep above 1: max {max}" );
			Check( max - min >= 0.5f, $"noise sweep barely varies: min {min}, max {max} (degenerate curve?)" );
		} );

		registry.Add( "particle-2d-wind", "noise: frozen TimeScale 0 is constant", () =>
		{
			var n = new NoiseGustProfile { Seed = 7, TimeScale = 0 };
			n.Validate();
			Near( n.At( 0 ), n.At( 5 ), 0, "frozen noise at t=5" );
			Near( n.At( 0 ), n.At( 13.7f ), 0, "frozen noise at t=13.7" );
		} );

		registry.Add( "particle-2d-wind", "combiner: sum clamps at 1", () =>
		{
			var a = Front();
			var b = Front();
			var sum = CombinedGustProfile.Combine( GustCombineMode.Sum, a, b );
			Near( sum.At( 1.5f ), 1, 0, "sum of two peaks clamps to 1, not 2" );
		} );

		registry.Add( "particle-2d-wind", "combiner: sum below the clamp adds plainly", () =>
		{
			var a = new GustFrontProfile { AttackSeconds = 1, SustainSeconds = 1, DecaySeconds = 1 };
			var b = new GustFrontProfile { AttackSeconds = 2, SustainSeconds = 1, DecaySeconds = 1 };
			var sum = CombinedGustProfile.Combine( GustCombineMode.Sum, a, b );
			Near( sum.At( 0.25f ), 0.375f, 0, "sum below clamp: 0.25 + 0.125" );
		} );

		registry.Add( "particle-2d-wind", "combiner: product gates a gust by a lull envelope", () =>
		{
			var gust = new GustFrontProfile { AttackSeconds = 0.5f, SustainSeconds = 10, DecaySeconds = 0.5f };
			var envelope = Train();
			var gated = CombinedGustProfile.Combine( GustCombineMode.Product, gust, envelope );
			Near( gated.At( 1.5f ), 1, 0, "gated gust where envelope is 1" );
			Near( gated.At( 3.5f ), 0, 0, "gated gust silenced where envelope is 0 (mid-lull)" );
			var partial = CombinedGustProfile.Combine( GustCombineMode.Product,
				new GustFrontProfile { AttackSeconds = 0.5f, SustainSeconds = 1, DecaySeconds = 1 }, Train() );
			Near( partial.At( 0.25f ), 0.125f, 0, "product partial: 0.5 x 0.25" );
		} );

		registry.Add( "particle-2d-wind", "combiner: product of in-range profiles stays in range", () =>
		{
			var gated = CombinedGustProfile.Combine( GustCombineMode.Product, Front(), new NoiseGustProfile { Seed = 7 } );
			for ( int i = 0; i <= 240; i++ )
			{
				float v = gated.At( i * 0.05f );
				Check( v >= 0 && v <= 1, $"product out of range at t={i * 0.05f}: {v}" );
			}
		} );

		registry.Add( "particle-2d-wind", "validate: front rejects negative, non-finite, and all-zero", () =>
		{
			Throws( () => new GustFrontProfile { AttackSeconds = -1, SustainSeconds = 1, DecaySeconds = 1 }.Validate(), "front negative attack" );
			Throws( () => new GustFrontProfile { AttackSeconds = 1, SustainSeconds = -0.5f, DecaySeconds = 1 }.Validate(), "front negative sustain" );
			Throws( () => new GustFrontProfile { AttackSeconds = 1, SustainSeconds = 1, DecaySeconds = float.NaN }.Validate(), "front NaN decay" );
			Throws( () => new GustFrontProfile { AttackSeconds = float.PositiveInfinity, SustainSeconds = 1, DecaySeconds = 1 }.Validate(), "front infinite attack" );
			Throws( () => new GustFrontProfile { AttackSeconds = 0, SustainSeconds = 0, DecaySeconds = 0 }.Validate(), "front all-zero" );
		} );

		registry.Add( "particle-2d-wind", "validate: train rejects negative lull and no-lull-no-decay", () =>
		{
			Throws( () => new GustTrainProfile { AttackSeconds = 1, SustainSeconds = 1, DecaySeconds = 1, LullSeconds = -1 }.Validate(), "train negative lull" );
			Throws( () => new GustTrainProfile { AttackSeconds = 1, SustainSeconds = 1, DecaySeconds = 1, LullSeconds = float.NaN }.Validate(), "train NaN lull" );
			Throws( () => new GustTrainProfile { AttackSeconds = 1, SustainSeconds = 1, DecaySeconds = 0, LullSeconds = 0 }.Validate(), "train no lull and no decay" );
			Throws( () => new GustTrainProfile { AttackSeconds = -1, SustainSeconds = 1, DecaySeconds = 1, LullSeconds = 1 }.Validate(), "train negative attack" );
		} );

		registry.Add( "particle-2d-wind", "validate: noise rejects bad timescale, octaves, lacunarity, gain", () =>
		{
			Throws( () => new NoiseGustProfile { TimeScale = -1 }.Validate(), "noise negative timescale" );
			Throws( () => new NoiseGustProfile { TimeScale = float.NaN }.Validate(), "noise NaN timescale" );
			Throws( () => new NoiseGustProfile { Octaves = 0 }.Validate(), "noise zero octaves" );
			Throws( () => new NoiseGustProfile { Lacunarity = 1 }.Validate(), "noise lacunarity 1" );
			Throws( () => new NoiseGustProfile { Gain = 0 }.Validate(), "noise gain 0" );
			Throws( () => new NoiseGustProfile { Gain = 1 }.Validate(), "noise gain 1" );
			Throws( () => new NoiseGustProfile { Gain = float.NaN }.Validate(), "noise NaN gain" );
		} );

		registry.Add( "particle-2d-wind", "validate: combined rejects empty, null, and invalid child", () =>
		{
			Throws( () => new CombinedGustProfile( Array.Empty<IGustProfile>() ).Validate(), "combined empty" );
			var withNull = new List<IGustProfile> { Front(), null! };
			Throws( () => new CombinedGustProfile( withNull ).Validate(), "combined null entry" );
			Throws( () => new CombinedGustProfile( new IGustProfile[] { new GustFrontProfile { AttackSeconds = -1, SustainSeconds = 1, DecaySeconds = 1 } } ).Validate(), "combined invalid child" );
		} );

		registry.Add( "particle-2d-wind", "validate: At rejects non-finite time", () =>
		{
			Throws( () => Front().At( float.NaN ), "front NaN time" );
			Throws( () => Train().At( float.PositiveInfinity ), "train infinite time" );
			Throws( () => new NoiseGustProfile { Seed = 7 }.At( float.NaN ), "noise NaN time" );
		} );

		registry.Add( "particle-2d-wind", "validate: zero individual phases are legal", () =>
		{
			new GustFrontProfile { AttackSeconds = 0, SustainSeconds = 1, DecaySeconds = 1 }.Validate();
			new GustTrainProfile { AttackSeconds = 1, SustainSeconds = 1, DecaySeconds = 0.5f, LullSeconds = 0 }.Validate();
			new GustTrainProfile { AttackSeconds = 1, SustainSeconds = 1, DecaySeconds = 0, LullSeconds = 1 }.Validate();
			new NoiseGustProfile { Seed = 7, TimeScale = 0 }.Validate();
		} );
	}
}
