using System;
using System.Numerics;
using Marionette.Desktop;

// Lane P29 (particles session, 2026-10-24): 3D noise variants.
// Group: particle-noise.
// Sweep line (scratch only — do NOT add to the canonical TestRegistry.cs):
//   ParticleNoiseTests.Register( registry, settings );
// placed after the GpuParticleSystemTests.Register line.
// Integration (coordinator sweep only, NOT applied here): the turbulence
// step in ParticleSystem.Integrate (ParticleSystem.cs, the line that today
// reads `acc += ParticleMath.Curl( pos * p.TurbFreq + new Vector3( 0, 0,
// time * p.TurbSpeed ), p.Seed ) * p.TurbStrength;`) gains a variant call
// once a ParticleNoiseConfig field exists on the settings — e.g.
//   acc += ParticleNoise.WarpedCurl( pos, noiseConfig ) * p.TurbStrength;
// No such settings field exists yet, so no drop-in one-liner is claimed.

internal static class ParticleNoiseTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void NearVec( Vector3 got, Vector3 want, float tol, string what ) { if ( (got - want).Length() > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; }
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}
		ParticleNoiseConfig Cfg() => new() { Seed = 4242u, Scale = 0.1f, Octaves = 4, Lacunarity = 2f, Gain = 0.5f, WarpAmplitude = 2f, WarpFrequency = 0.05f, TimeScale = 1f };
		Vector3[] Grid()
		{
			var pts = new Vector3[125];
			int n = 0;
			for ( int x = 0; x < 5; x++ ) for ( int y = 0; y < 5; y++ ) for ( int z = 0; z < 5; z++ )
				pts[n++] = new Vector3( x * 7.3f - 11f, y * 5.1f + 3f, z * 9.7f - 4f );
			return pts;
		}
		float MeanLen( Func<Vector3, Vector3> f, Vector3[] pts )
		{
			float s = 0;
			foreach ( var p in pts ) s += f( p ).Length();
			return s / pts.Length;
		}

		registry.Add( "particle-noise", "probe field values at known points", () =>
		{
			var cfg = Cfg();
			foreach ( var p in new[] { new Vector3( 0, 0, 0 ), new Vector3( 10, 0, 0 ), new Vector3( 3, 7, 11 ), new Vector3( -5, 20, 2 ) } )
			{
				var a = ParticleNoise.CurlA( p, cfg );
				var b = ParticleNoise.CurlFbm( p, cfg );
				var w = ParticleNoise.WarpedCurl( p, cfg );
				var t = ParticleNoise.CurlTime( p, 1.5f, cfg );
				var port = ParticleMath.Curl( p * cfg.Scale, cfg.Seed );
				Console.WriteLine( $"PROBE p={p} CurlA={a} Fbm={b} Warped={w} Time1.5={t} Port={port}" );
				Check( ParticleMath.Finite( a ) && ParticleMath.Finite( b ) && ParticleMath.Finite( w ) && ParticleMath.Finite( t ), $"non-finite field at {p}" );
			}
		} );

		registry.Add( "particle-noise", "golden field values for a fixed seed", () =>
		{
			// Values measured from the scratch run (seed 4242, Scale 0.1);
			// pinning them turns any future kernel drift into a failure.
			var cfg = Cfg();
			NearVec( ParticleNoise.CurlA( new Vector3( 3, 7, 11 ), cfg ), new Vector3( 0.6542625f, -0.625853f, -0.447196f ), 2e-4f, "CurlA(3,7,11)" );
			NearVec( ParticleNoise.CurlA( new Vector3( 0, 0, 0 ), cfg ), new Vector3( 1.1430267f, 0.06302768f, 0.4288967f ), 2e-4f, "CurlA(0,0,0)" );
			NearVec( ParticleNoise.WarpedCurl( new Vector3( 3, 7, 11 ), cfg ), new Vector3( 0.012876719f, 0.56101173f, -0.12709185f ), 2e-4f, "WarpedCurl(3,7,11)" );
			NearVec( ParticleNoise.CurlTime( new Vector3( 3, 7, 11 ), 1.5f, cfg ), new Vector3( -0.18702507f, 0.2785563f, 0.79082465f ), 2e-4f, "CurlTime(3,7,11,1.5)" );
		} );

		registry.Add( "particle-noise", "variant A is deterministic and non-degenerate", () =>
		{
			var cfg = Cfg();
			foreach ( var p in Grid() )
			{
				var a = ParticleNoise.CurlA( p, cfg );
				var b = ParticleNoise.CurlA( p, cfg );
				Check( a == b, $"CurlA not bit-identical on repeat at {p}: {a} vs {b}" );
				Check( ParticleMath.Finite( a ), $"CurlA non-finite at {p}" );
			}
			Check( MeanLen( p => ParticleNoise.CurlA( p, cfg ), Grid() ) > 0.05f, "CurlA mean magnitude suspiciously near zero" );
			var otherSeed = ParticleNoise.CurlA( new Vector3( 3, 7, 11 ), new ParticleNoiseConfig { Seed = 999u, Scale = 0.1f } );
			var sameSeed = ParticleNoise.CurlA( new Vector3( 3, 7, 11 ), cfg );
			Check( otherSeed != sameSeed, "different seed gave an identical field value" );
		} );

		registry.Add( "particle-noise", "variant A differs from the existing port field", () =>
		{
			var cfg = Cfg();
			float sum = 0, max = 0;
			int same = 0;
			foreach ( var p in Grid() )
			{
				var a = ParticleNoise.CurlA( p, cfg );
				var port = ParticleMath.Curl( p * cfg.Scale, cfg.Seed );
				float d = (a - port).Length();
				sum += d; if ( d > max ) max = d;
				if ( a == port ) same++;
			}
			float mean = sum / 125f;
			Console.WriteLine( $"PROBE variantA-vs-port meanDiff={mean} maxDiff={max} identical={same}/125" );
			Check( same == 0, "variant A matched the port exactly at some grid point" );
			Check( mean > 0.05f, $"variant A barely differs from the port: mean diff {mean}" );
			Check( max > 0.2f, $"variant A barely differs from the port: max diff {max}" );
		} );

		registry.Add( "particle-noise", "gradient noise gradient matches finite differences", () =>
		{
			// Analytic gradient sanity: central differences of the scalar
			// field must agree with GradientNoiseGrad's gradient.
			uint seed = 4242u;
			foreach ( var p in new[] { new Vector3( 1.3f, -2.7f, 4.1f ), new Vector3( 0.2f, 0.9f, -1.4f ), new Vector3( -6.6f, 3.3f, 0.7f ) } )
			{
				float h = 1e-3f;
				ParticleNoise.GradientNoiseGrad( p, seed, out var g );
				float vx1 = ParticleNoise.GradientNoiseGrad( p + new Vector3( h, 0, 0 ), seed, out _ );
				float vx0 = ParticleNoise.GradientNoiseGrad( p - new Vector3( h, 0, 0 ), seed, out _ );
				float vy1 = ParticleNoise.GradientNoiseGrad( p + new Vector3( 0, h, 0 ), seed, out _ );
				float vy0 = ParticleNoise.GradientNoiseGrad( p - new Vector3( 0, h, 0 ), seed, out _ );
				float vz1 = ParticleNoise.GradientNoiseGrad( p + new Vector3( 0, 0, h ), seed, out _ );
				float vz0 = ParticleNoise.GradientNoiseGrad( p - new Vector3( 0, 0, h ), seed, out _ );
				var fd = new Vector3( (vx1 - vx0) / (2 * h), (vy1 - vy0) / (2 * h), (vz1 - vz0) / (2 * h) );
				Check( (g - fd).Length() < 0.05f, $"analytic gradient {g} disagrees with finite differences {fd} at {p}" );
			}
		} );

		registry.Add( "particle-noise", "fbm one octave equals variant A", () =>
		{
			var cfg = Cfg();
			cfg.Octaves = 1;
			foreach ( var p in Grid() )
				Check( ParticleNoise.CurlFbm( p, cfg ) == ParticleNoise.CurlA( p, cfg ), $"fbm(1 octave) != CurlA at {p}" );
		} );

		registry.Add( "particle-noise", "fbm more octaves changes the field but stays finite and deterministic", () =>
		{
			var cfg = Cfg();
			var one = Cfg(); one.Octaves = 1;
			float sum = 0;
			foreach ( var p in Grid() )
			{
				var f = ParticleNoise.CurlFbm( p, cfg );
				Check( ParticleMath.Finite( f ), $"fbm non-finite at {p}" );
				Check( f == ParticleNoise.CurlFbm( p, cfg ), $"fbm not deterministic at {p}" );
				sum += (f - ParticleNoise.CurlFbm( p, one )).Length();
			}
			float mean = sum / 125f;
			Console.WriteLine( $"PROBE fbm 4-octaves vs 1-octave meanDiff={mean}" );
			Check( mean > 0.02f, $"extra octaves barely changed the field: mean diff {mean}" );
		} );

		registry.Add( "particle-noise", "fbm gain and lacunarity change the field", () =>
		{
			var cfg = Cfg();
			var alt = Cfg(); alt.Gain = 0.25f; alt.Lacunarity = 3f;
			float sum = 0;
			foreach ( var p in Grid() ) sum += (ParticleNoise.CurlFbm( p, cfg ) - ParticleNoise.CurlFbm( p, alt )).Length();
			Check( sum / 125f > 0.01f, "gain/lacunarity change had no measurable effect" );
		} );

		registry.Add( "particle-noise", "zero warp amplitude equals unwarped fbm exactly", () =>
		{
			var cfg = Cfg(); cfg.WarpAmplitude = 0f;
			foreach ( var p in Grid() )
				Check( ParticleNoise.WarpedCurl( p, cfg ) == ParticleNoise.CurlFbm( p, cfg ), $"warped(amp 0) != fbm at {p}" );
		} );

		registry.Add( "particle-noise", "warp effect magnitude grows with amplitude", () =>
		{
			var small = Cfg(); small.WarpAmplitude = 0.5f;
			var big = Cfg(); big.WarpAmplitude = 8f;
			float meanSmall = 0, meanBig = 0;
			foreach ( var p in Grid() )
			{
				var f = ParticleNoise.CurlFbm( p, Cfg() );
				meanSmall += (ParticleNoise.WarpedCurl( p, small ) - f).Length();
				meanBig += (ParticleNoise.WarpedCurl( p, big ) - f).Length();
			}
			meanSmall /= 125f; meanBig /= 125f;
			Console.WriteLine( $"PROBE warp meanDiff amp0.5={meanSmall} amp8={meanBig}" );
			Check( meanSmall > 0.01f, $"small warp had no effect: {meanSmall}" );
			Check( meanBig > meanSmall, $"larger warp did not move the field more: {meanBig} vs {meanSmall}" );
			foreach ( var p in Grid() )
				Check( ParticleNoise.WarpedCurl( p, big ) == ParticleNoise.WarpedCurl( p, big ), "warped field not deterministic" );
		} );

		registry.Add( "particle-noise", "time mode is continuous in t and varies over time", () =>
		{
			var cfg = Cfg();
			float stepSum = 0, jumpSum = 0;
			foreach ( var p in Grid() )
			{
				var t0 = ParticleNoise.CurlTime( p, 2f, cfg );
				var tStep = ParticleNoise.CurlTime( p, 2.01f, cfg );
				var tJump = ParticleNoise.CurlTime( p, 7f, cfg );
				Check( ParticleMath.Finite( t0 ) && ParticleMath.Finite( tStep ) && ParticleMath.Finite( tJump ), $"time field non-finite at {p}" );
				Check( t0 == ParticleNoise.CurlTime( p, 2f, cfg ), $"time field not deterministic at {p}" );
				stepSum += (tStep - t0).Length();
				jumpSum += (tJump - t0).Length();
			}
			float stepMean = stepSum / 125f, jumpMean = jumpSum / 125f;
			Console.WriteLine( $"PROBE time continuity dt0.01 meanDiff={stepMean} dt5 meanDiff={jumpMean}" );
			Check( stepMean < 0.5f, $"adjacent-t step too large for a continuous field: {stepMean}" );
			Check( stepMean < jumpMean, $"tiny t step moved the field as much as a 5 s jump: {stepMean} vs {jumpMean}" );
			Check( jumpMean > 0.05f, $"field barely varies over 5 s: {jumpMean}" );
		} );

		registry.Add( "particle-noise", "time scale zero freezes the field", () =>
		{
			var cfg = Cfg(); cfg.TimeScale = 0f;
			var p = new Vector3( 3, 7, 11 );
			Check( ParticleNoise.CurlTime( p, 0f, cfg ) == ParticleNoise.CurlTime( p, 99f, cfg ), "TimeScale 0 did not freeze the field" );
		} );

		registry.Add( "particle-noise", "spatial field is smooth at small steps", () =>
		{
			var cfg = Cfg();
			float nearSum = 0, farSum = 0;
			foreach ( var p in Grid() )
			{
				nearSum += (ParticleNoise.CurlA( p + new Vector3( 0.01f, 0, 0 ), cfg ) - ParticleNoise.CurlA( p, cfg )).Length();
				farSum += (ParticleNoise.CurlA( p + new Vector3( 5f, 0, 0 ), cfg ) - ParticleNoise.CurlA( p, cfg )).Length();
			}
			float nearMean = nearSum / 125f, farMean = farSum / 125f;
			Console.WriteLine( $"PROBE spatial smoothness dx0.01 meanDiff={nearMean} dx5 meanDiff={farMean}" );
			Check( nearMean < farMean, $"0.01-inch step moved the field as much as a 5-inch step: {nearMean} vs {farMean}" );
		} );

		registry.Add( "particle-noise", "Validate rejects non-positive octaves", () =>
		{
			var c0 = Cfg(); c0.Octaves = 0;
			Throws( () => c0.Validate(), "octaves 0" );
			var cn = Cfg(); cn.Octaves = -3;
			Throws( () => cn.Validate(), "octaves -3" );
			Throws( () => ParticleNoise.CurlFbm( new Vector3( 1, 2, 3 ), c0 ), "evaluator with octaves 0" );
		} );

		registry.Add( "particle-noise", "Validate rejects lacunarity at or below 1", () =>
		{
			var c1 = Cfg(); c1.Lacunarity = 1f;
			Throws( () => c1.Validate(), "lacunarity 1" );
			var c05 = Cfg(); c05.Lacunarity = 0.5f;
			Throws( () => c05.Validate(), "lacunarity 0.5" );
			var ok = Cfg(); ok.Lacunarity = 1.01f;
			ok.Validate();
		} );

		registry.Add( "particle-noise", "Validate rejects gain outside (0,1)", () =>
		{
			foreach ( float g in new[] { 0f, 1f, 1.5f, -0.25f } )
			{
				var c = Cfg(); c.Gain = g;
				Throws( () => c.Validate(), $"gain {g}" );
			}
			var ok = Cfg(); ok.Gain = 0.99f;
			ok.Validate();
		} );

		registry.Add( "particle-noise", "Validate rejects negative scales", () =>
		{
			var s = Cfg(); s.Scale = -0.1f;
			Throws( () => s.Validate(), "negative Scale" );
			var wf = Cfg(); wf.WarpFrequency = -1f;
			Throws( () => wf.Validate(), "negative WarpFrequency" );
			var wa = Cfg(); wa.WarpAmplitude = -1f;
			Throws( () => wa.Validate(), "negative WarpAmplitude" );
			var ts = Cfg(); ts.TimeScale = -1f;
			Throws( () => ts.Validate(), "negative TimeScale" );
			var zero = Cfg(); zero.Scale = 0f; zero.WarpAmplitude = 0f; zero.TimeScale = 0f;
			zero.Validate();
		} );

		registry.Add( "particle-noise", "default config validates", () =>
		{
			new ParticleNoiseConfig().Validate();
		} );
	}
}
