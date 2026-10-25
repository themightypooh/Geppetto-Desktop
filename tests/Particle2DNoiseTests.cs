using System.Numerics;
using Marionette.Desktop;

// Lane P17 (particles session, 2026-10-24): 2D noise evaluators.
// Same harness shape as Particle2DTests: registry.Add("particle-2d-noise", name, body).
// Sweep line (NOT added to the canonical tree — coordinator sweep only):
//   Particle2DNoiseTests.Register( registry, settings );
// placed after the Particle2DTests.Register line.
internal static class Particle2DNoiseTests
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
		Particle2DNoiseSettings S( uint seed = 7 ) => new() { Seed = seed };
		float DotNorm( Vector2 a, Vector2 b )
		{
			float la = a.Length(), lb = b.Length();
			if ( la < 1e-6f || lb < 1e-6f ) return 0;
			return Vector2.Dot( a / la, b / lb );
		}

		registry.Add( "particle-2d-noise", "value noise stays in [-1,1] and reaches both extremes", () =>
		{
			float min = float.MaxValue, max = float.MinValue;
			for ( int i = 0; i < 96; i++ )
				for ( int j = 0; j < 96; j++ )
				{
					float v = Particle2DNoise.Value( new Vector2( i * 0.25f, j * 0.25f ), 42 );
					Check( v >= -1 && v <= 1, $"value out of [-1,1]: {v} at ({i}, {j})" );
					if ( v < min ) min = v;
					if ( v > max ) max = v;
				}
			Check( min < -0.7f && max > 0.7f, $"field does not reach both extremes: min {min}, max {max}" );
		} );

		registry.Add( "particle-2d-noise", "value noise matches Particle2DMath potential", () =>
		{
			Vector2[] pts = { new( 0.37f, 1.73f ), new( 2.5f, 3.25f ), new( 13.7f, 42.3f ), new( -4.2f, 7.9f ), new( 0.5f, 0.5f ) };
			foreach ( uint seed in new uint[] { 7, 123 } )
				foreach ( var p in pts )
				{
					float mine = Particle2DNoise.Value( p, seed );
					float theirs = Particle2DMath.NoiseGrad( p, seed, out _ );
					Near( mine, theirs, 1e-6f, $"Value vs NoiseGrad at {p} seed {seed}" );
				}
		} );

		registry.Add( "particle-2d-noise", "value noise is deterministic and seed-sensitive", () =>
		{
			var p = new Vector2( 13.7f, 42.3f );
			Check( Particle2DNoise.Value( p, 7 ) == Particle2DNoise.Value( p, 7 ), "same seed/point gave different values" );
			int diff = 0;
			for ( int i = 0; i < 20; i++ )
			{
				var q = new Vector2( i * 1.37f + 0.31f, i * 0.91f + 2.7f );
				if ( Particle2DNoise.Value( q, 7 ) != Particle2DNoise.Value( q, 8 ) ) diff++;
			}
			Check( diff > 10, $"seed 7 vs 8 differ at only {diff}/20 points — seed looks ignored" );
		} );

		registry.Add( "particle-2d-noise", "value noise is continuous across lattice lines", () =>
		{
			// Straddle integer lattice lines in both axes: smoothstep fade has zero
			// derivative at boundaries, so a 0.002 step across a line must barely move.
			foreach ( float y in new float[] { 3.31f, 7.77f } )
				Check( MathF.Abs( Particle2DNoise.Value( new Vector2( 2.999f, y ), 7 ) - Particle2DNoise.Value( new Vector2( 3.001f, y ), 7 ) ) < 0.05f,
					$"jump across x lattice line at y={y}" );
			foreach ( float x in new float[] { 2.71f, 9.13f } )
				Check( MathF.Abs( Particle2DNoise.Value( new Vector2( x, 4.999f ), 7 ) - Particle2DNoise.Value( new Vector2( x, 5.001f ), 7 ) ) < 0.05f,
					$"jump across y lattice line at x={x}" );
			// Small steps anywhere move only a little (gradient is bounded).
			Check( MathF.Abs( Particle2DNoise.Value( new Vector2( 13.7f, 42.3f ), 7 ) - Particle2DNoise.Value( new Vector2( 13.71f, 42.3f ), 7 ) ) < 0.05f,
				"0.01 step moved value by >= 0.05" );
		} );

		registry.Add( "particle-2d-noise", "fbm stays in [-1,1]", () =>
		{
			var s = S();
			for ( int i = 0; i < 64; i++ )
				for ( int j = 0; j < 64; j++ )
				{
					float v = Particle2DNoise.FbmAt( new Vector2( i * 7.3f, j * 7.3f ), s );
					Check( v >= -1 && v <= 1, $"fbm out of [-1,1]: {v} at ({i}, {j})" );
				}
		} );

		registry.Add( "particle-2d-noise", "fbm with one octave equals value noise", () =>
		{
			Vector2[] pts = { new( 0.37f, 1.73f ), new( 13.7f, 42.3f ), new( -4.2f, 7.9f ) };
			foreach ( var p in pts )
				Check( Particle2DNoise.Fbm( p, 7, 1, 2f, 0.5f ) == Particle2DNoise.Value( p, 7 ),
					$"fbm(1 octave) != value at {p}" );
		} );

		registry.Add( "particle-2d-noise", "fbm gains detail with octaves", () =>
		{
			// Fine-step mean absolute difference: extra octaves add high-frequency
			// energy, so the 4-octave field must wiggle more per 0.05 step than the
			// 1-octave field over the same grid.
			float MeanStep( int octaves )
			{
				float sum = 0;
				int n = 0;
				for ( int i = 0; i < 40; i++ )
					for ( int j = 0; j < 40; j++ )
					{
						var p = new Vector2( i * 0.3f + 0.13f, j * 0.3f + 0.29f );
						sum += MathF.Abs( Particle2DNoise.Fbm( p + new Vector2( 0.05f, 0 ), 7, octaves, 2f, 0.5f ) - Particle2DNoise.Fbm( p, 7, octaves, 2f, 0.5f ) );
						n++;
					}
				return sum / n;
			}
			float one = MeanStep( 1 ), four = MeanStep( 4 );
			Check( four > one * 1.1f, $"4-octave mean step {four} not above 1-octave {one} — octaves add no detail" );
		} );

		registry.Add( "particle-2d-noise", "fbm is deterministic and seed-sensitive", () =>
		{
			var s7 = S( 7 ); var s8 = S( 8 );
			var p = new Vector2( 137, 251 );
			Check( Particle2DNoise.FbmAt( p, s7 ) == Particle2DNoise.FbmAt( p, s7 ), "same-seed fbm differs" );
			int diff = 0;
			for ( int i = 0; i < 20; i++ )
			{
				var q = new Vector2( i * 13.7f + 11, i * 9.1f + 23 );
				if ( Particle2DNoise.FbmAt( q, s7 ) != Particle2DNoise.FbmAt( q, s8 ) ) diff++;
			}
			Check( diff > 10, $"fbm seed 7 vs 8 differ at only {diff}/20 points" );
		} );

		registry.Add( "particle-2d-noise", "curl-value direction matches sim curl", () =>
		{
			Vector2[] pts = { new( 2.74f, 5.02f ), new( 13.7f, 42.3f ), new( 0.37f, 1.73f ), new( 7.31f, 3.19f ), new( 21.5f, 11.1f ) };
			foreach ( var p in pts )
			{
				Vector2 mine = Particle2DNoise.CurlValue( p, 7 );
				Vector2 sim = Particle2DMath.Curl( p, 7 );
				if ( sim.Length() < 0.2f ) continue; // near a lattice zero the direction is meaningless
				float dot = DotNorm( mine, sim );
				Check( dot > 0.99f, $"curl-value direction off at {p}: dot {dot}, mine {mine}, sim {sim}" );
			}
		} );

		registry.Add( "particle-2d-noise", "curl magnitudes are finite and bounded in practice", () =>
		{
			var s = S();
			float maxV = 0, maxF = 0;
			for ( int i = 0; i < 48; i++ )
				for ( int j = 0; j < 48; j++ )
				{
					var p = new Vector2( i * 11.3f + 3.1f, j * 11.7f + 5.9f );
					float mv = Particle2DNoise.CurlValueAt( p, s ).Length();
					float mf = Particle2DNoise.CurlAt( p, s ).Length();
					Check( float.IsFinite( mv ) && float.IsFinite( mf ), $"non-finite curl at {p}" );
					if ( mv > maxV ) maxV = mv;
					if ( mf > maxF ) maxF = mf;
				}
			Check( maxV > 0.5f && maxF > 0.5f, $"curl fields look dead: max value-curl {maxV}, max fbm-curl {maxF}" );
			Check( maxV < 5 && maxF < 5, $"curl magnitude implausibly large: value {maxV}, fbm {maxF}" );
		} );

		registry.Add( "particle-2d-noise", "curl field is near divergence-free", () =>
		{
			// Discrete divergence of the curl field via central differences. The
			// analytic curl of a gradient is exactly divergence-free; the
			// finite-difference version must stay small relative to the field.
			float h = 0.05f;
			Vector2[] pts = { new( 2.74f, 5.02f ), new( 13.7f, 42.3f ), new( 7.31f, 3.19f ), new( 21.5f, 11.1f ) };
			foreach ( var p in pts )
			{
				float dCxdx = (Particle2DNoise.CurlValue( p + new Vector2( h, 0 ), 7 ).X - Particle2DNoise.CurlValue( p - new Vector2( h, 0 ), 7 ).X) / (2f * h);
				float dCydy = (Particle2DNoise.CurlValue( p + new Vector2( 0, h ), 7 ).Y - Particle2DNoise.CurlValue( p - new Vector2( 0, h ), 7 ).Y) / (2f * h);
				Check( MathF.Abs( dCxdx + dCydy ) < 0.01f, $"divergence {dCxdx + dCydy} at {p} — curl is not divergence-free-ish" );
			}
		} );

		registry.Add( "particle-2d-noise", "fbm curl differs from single-octave curl", () =>
		{
			int diff = 0;
			for ( int i = 0; i < 20; i++ )
			{
				var p = new Vector2( i * 1.31f + 0.37f, i * 0.97f + 2.11f );
				Vector2 a = Particle2DNoise.CurlValue( p, 7 ), b = Particle2DNoise.Curl( p, 7, 4, 2f, 0.5f );
				if ( (a - b).Length() > 0.1f ) diff++;
			}
			Check( diff > 10, $"fbm curl matches value curl at {20 - diff}/20 points — octaves change nothing" );
		} );

		registry.Add( "particle-2d-noise", "time noise is continuous across time-cell boundaries", () =>
		{
			// TimeScale 0.5: t = 2.0 lands exactly on a time-cell boundary (z = 1).
			// A 0.01 s step across it must not jump.
			var s = S();
			foreach ( var pos in new Vector2[] { new( 137, 251 ), new( 40, 90 ), new( 300, 15 ) } )
				Check( MathF.Abs( Particle2DNoise.ValueAtTime( pos, 1.99f, s ) - Particle2DNoise.ValueAtTime( pos, 2.01f, s ) ) < 0.05f,
					$"time jump across cell boundary at {pos}" );
			Check( MathF.Abs( Particle2DNoise.ValueAtTime( new Vector2( 137, 251 ), 1.0f, s ) - Particle2DNoise.ValueAtTime( new Vector2( 137, 251 ), 1.01f, s ) ) < 0.05f,
				"0.01 s time step moved value by >= 0.05" );
		} );

		registry.Add( "particle-2d-noise", "time noise evolves without freezing", () =>
		{
			var s = S();
			int evolving = 0;
			foreach ( var pos in new Vector2[] { new( 137, 251 ), new( 40, 90 ), new( 300, 15 ), new( 640, 360 ) } )
			{
				float min = float.MaxValue, max = float.MinValue;
				for ( int k = 0; k <= 16; k++ )
				{
					float v = Particle2DNoise.ValueAtTime( pos, k * 0.25f, s );
					Check( v >= -1 && v <= 1, $"time noise out of [-1,1]: {v}" );
					if ( v < min ) min = v;
					if ( v > max ) max = v;
				}
				if ( max - min > 0.5f ) evolving++;
			}
			Check( evolving >= 2, $"only {evolving}/4 positions evolve over 0..4 s — time field looks frozen" );
		} );

		registry.Add( "particle-2d-noise", "time curl is deterministic and continuous", () =>
		{
			var s = S();
			var pos = new Vector2( 137, 251 );
			Check( Particle2DNoise.CurlAtTime( pos, 1.5f, s ) == Particle2DNoise.CurlAtTime( pos, 1.5f, s ), "same-time curl differs" );
			Vector2 a = Particle2DNoise.CurlAtTime( pos, 1.5f, s ), b = Particle2DNoise.CurlAtTime( pos, 1.51f, s );
			if ( a.Length() > 0.2f && b.Length() > 0.2f )
				Check( DotNorm( a, b ) > 0.9f, $"time curl direction jumps in 0.01 s: dot {DotNorm( a, b )}" );
			Check( Particle2DNoise.CurlValueAtTime( pos, 1.5f, s ) == Particle2DNoise.CurlValueAtTime( pos, 1.5f, s ), "same-time value curl differs" );
		} );

		registry.Add( "particle-2d-noise", "defaults are the documented values", () =>
		{
			var s = new Particle2DNoiseSettings();
			Check( s.Seed == 1, $"default Seed {s.Seed} != 1" );
			Near( s.Scale, 0.02f, 1e-6f, "default Scale" );
			Check( s.Octaves == 4, $"default Octaves {s.Octaves} != 4" );
			Near( s.Lacunarity, 2f, 1e-6f, "default Lacunarity" );
			Near( s.Gain, 0.5f, 1e-6f, "default Gain" );
			Near( s.TimeScale, 0.5f, 1e-6f, "default TimeScale" );
			s.Validate(); // defaults must pass their own validation
		} );

		registry.Add( "particle-2d-noise", "golden values for a fixed seed", () =>
		{
			// Measured on GOO (dotnet 10.0.402) 2026-10-24; float math is
			// deterministic, so these pin the field, not just its shape.
			var s = S( 7 );
			Near( Particle2DNoise.Value( new Vector2( 2.74f, 5.02f ), 7 ), -0.7486171f, 1e-5f, "golden value" );
			Near( Particle2DNoise.ValueAt( new Vector2( 137, 251 ), s ), -0.7486171f, 1e-5f, "golden valueAt (same noise coord as above)" );
			Near( Particle2DNoise.FbmAt( new Vector2( 137, 251 ), s ), -0.394214f, 1e-5f, "golden fbmAt" );
			Vector2 cv = Particle2DNoise.CurlValue( new Vector2( 2.74f, 5.02f ), 7 );
			Near( cv.X, -0.1497364f, 1e-5f, "golden curlValue X" );
			Near( cv.Y, -0.77384174f, 1e-5f, "golden curlValue Y" );
			Vector2 cf = Particle2DNoise.CurlAt( new Vector2( 137, 251 ), s );
			Near( cf.X, -0.0038024783f, 1e-5f, "golden curlAt X" );
			Near( cf.Y, -0.7138851f, 1e-5f, "golden curlAt Y" );
			Near( Particle2DNoise.ValueAtTime( new Vector2( 137, 251 ), 1.5f, s ), -0.10680145f, 1e-5f, "golden valueAtTime" );
			Near( Particle2DNoise.FbmAtTime( new Vector2( 137, 251 ), 1.5f, s ), -0.2702705f, 1e-5f, "golden fbmAtTime" );
			Vector2 ct = Particle2DNoise.CurlAtTime( new Vector2( 137, 251 ), 1.5f, s );
			Near( ct.X, -0.27084768f, 1e-5f, "golden curlAtTime X" );
			Near( ct.Y, -0.7792048f, 1e-5f, "golden curlAtTime Y" );
			Vector2 cvt = Particle2DNoise.CurlValueAtTime( new Vector2( 137, 251 ), 1.5f, s );
			Near( cvt.X, -0.032075644f, 1e-5f, "golden curlValueAtTime X" );
			Near( cvt.Y, -1.1699611f, 1e-5f, "golden curlValueAtTime Y" );
		} );

		registry.Add( "particle-2d-noise", "Validate rejects bad settings loudly", () =>
		{
			Particle2DNoiseSettings Bad( Action<Particle2DNoiseSettings> edit ) { var s = new Particle2DNoiseSettings(); edit( s ); return s; }
			Throws( () => Bad( s => s.Octaves = 0 ).Validate(), "octaves 0" );
			Throws( () => Bad( s => s.Octaves = -3 ).Validate(), "octaves -3" );
			Throws( () => Bad( s => s.Lacunarity = 1 ).Validate(), "lacunarity 1" );
			Throws( () => Bad( s => s.Lacunarity = 0.5f ).Validate(), "lacunarity 0.5" );
			Throws( () => Bad( s => s.Lacunarity = float.NaN ).Validate(), "lacunarity NaN" );
			Throws( () => Bad( s => s.Gain = 0 ).Validate(), "gain 0" );
			Throws( () => Bad( s => s.Gain = 1 ).Validate(), "gain 1" );
			Throws( () => Bad( s => s.Gain = 1.5f ).Validate(), "gain 1.5" );
			Throws( () => Bad( s => s.Gain = -0.1f ).Validate(), "gain -0.1" );
			Throws( () => Bad( s => s.Scale = 0 ).Validate(), "scale 0" );
			Throws( () => Bad( s => s.Scale = -0.01f ).Validate(), "scale negative" );
			Throws( () => Bad( s => s.Scale = float.NaN ).Validate(), "scale NaN" );
			Throws( () => Bad( s => s.TimeScale = -0.1f ).Validate(), "time scale negative" );
			// TimeScale 0 is a legal still mode, not an error.
			Bad( s => s.TimeScale = 0 ).Validate();
			// Raw overloads validate too.
			Throws( () => Particle2DNoise.Fbm( new Vector2( 1, 2 ), 7, 0, 2f, 0.5f ), "raw fbm octaves 0" );
			Throws( () => Particle2DNoise.Fbm( new Vector2( 1, 2 ), 7, 4, 1f, 0.5f ), "raw fbm lacunarity 1" );
			Throws( () => Particle2DNoise.Curl( new Vector2( 1, 2 ), 7, 4, 2f, 1f ), "raw curl gain 1" );
		} );
	}
}
