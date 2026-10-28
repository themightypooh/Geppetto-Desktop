using System;
using System.Numerics;
using Marionette.Desktop;

internal static class ParticleFlipbookTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Eq( int got, int want, string what ) { if ( got != want ) throw new Exception( $"{what}: got {got}, want {want}" ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void Throws( Action body, string what )
		{
			try { body(); }
			// ArgumentOutOfRangeException derives from ArgumentException, so one catch covers both.
			catch ( ArgumentException ) { return; }
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}
		ParticleFlipbook Book( int cols = 4, int rows = 4, int frames = 16, float fps = 10 ) => new()
		{
			Columns = cols, Rows = rows, FrameCount = frames, FramesPerSecond = fps,
		};

		registry.Add( "particle-flipbook", "atlas UV rects are row-major, row 0 on top", () =>
		{
			var b = Book();
			var uv0 = b.UvFor( 0 );
			Near( uv0.U0, 0, 1e-6f, "frame 0 U0" ); Near( uv0.V0, 0, 1e-6f, "frame 0 V0" );
			Near( uv0.U1, 0.25f, 1e-6f, "frame 0 U1" ); Near( uv0.V1, 0.25f, 1e-6f, "frame 0 V1" );
			var uv5 = b.UvFor( 5 );
			Near( uv5.U0, 0.25f, 1e-6f, "frame 5 U0" ); Near( uv5.V0, 0.25f, 1e-6f, "frame 5 V0" );
			Near( uv5.U1, 0.5f, 1e-6f, "frame 5 U1" ); Near( uv5.V1, 0.5f, 1e-6f, "frame 5 V1" );
			var uv15 = b.UvFor( 15 );
			Near( uv15.U0, 0.75f, 1e-6f, "frame 15 U0" ); Near( uv15.V0, 0.75f, 1e-6f, "frame 15 V0" );
			Near( uv15.U1, 1, 1e-6f, "frame 15 U1" ); Near( uv15.V1, 1, 1e-6f, "frame 15 V1" );
		} );

		registry.Add( "particle-flipbook", "partial atlas: only the first FrameCount cells play", () =>
		{
			var b = Book( frames: 10 );
			var uv9 = b.UvFor( 9 );
			Near( uv9.U0, 0.25f, 1e-6f, "frame 9 U0" ); Near( uv9.V0, 0.5f, 1e-6f, "frame 9 V0" );
			Throws( () => b.UvFor( 10 ), "frame 10 past FrameCount 10" );
			Throws( () => b.UvFor( -1 ), "negative frame" );
		} );

		registry.Add( "particle-flipbook", "FlipU and FlipV swap the rect edges", () =>
		{
			var b = Book(); b.FlipU = true; b.FlipV = true;
			var uv = b.UvFor( 5 );
			Near( uv.U0, 0.5f, 1e-6f, "flipped U0" ); Near( uv.U1, 0.25f, 1e-6f, "flipped U1" );
			Near( uv.V0, 0.5f, 1e-6f, "flipped V0" ); Near( uv.V1, 0.25f, 1e-6f, "flipped V1" );
		} );

		registry.Add( "particle-flipbook", "bad configuration fails loudly", () =>
		{
			Throws( () => Book( cols: 0 ).Validate(), "Columns 0" );
			Throws( () => Book( rows: 0 ).Validate(), "Rows 0" );
			Throws( () => Book( frames: 0 ).Validate(), "FrameCount 0" );
			Throws( () => Book( frames: 17 ).Validate(), "FrameCount past Columns*Rows" );
			Throws( () => new ParticleFlipbook { Columns = 4, Rows = 4, FrameCount = 16, FirstFrame = -1 }.Validate(), "FirstFrame negative" );
			Throws( () => new ParticleFlipbook { Columns = 4, Rows = 4, FrameCount = 16, FramesPerSecond = -1 }.Validate(), "negative fps" );
			Throws( () => new ParticleFlipbook { Columns = 4, Rows = 4, FrameCount = 16, SpeedScale = -1 }.Validate(), "negative speed scale" );
			Throws( () => new ParticleFlipbook { Columns = 4, Rows = 4, FrameCount = 16, Mode = (ParticleFlipbookMode)99 }.Validate(), "unknown mode" );
		} );

		registry.Add( "particle-flipbook", "Loop advances and wraps", () =>
		{
			var b = Book( frames: 4 );
			Eq( b.FrameAt( 0, 0, 0, 0 ), 0, "age 0" );
			Eq( b.FrameAt( 0.05f, 0, 0, 0 ), 0, "age 0.05" );
			Eq( b.FrameAt( 0.1f, 0, 0, 0 ), 1, "age 0.1" );
			Eq( b.FrameAt( 0.35f, 0, 0, 0 ), 3, "age 0.35" );
			Eq( b.FrameAt( 0.4f, 0, 0, 0 ), 0, "age 0.4 wraps" );
			Eq( b.FrameAt( 0.2f, 0, 2, 0 ), 0, "base 2 + 2 wraps to 0" );
			Eq( b.FrameAt( 0.1f, 0, 2, 0 ), 3, "base 2 + 1" );
		} );

		registry.Add( "particle-flipbook", "Once holds the last frame", () =>
		{
			var b = Book( frames: 4 ); b.Mode = ParticleFlipbookMode.Once;
			Eq( b.FrameAt( 0.1f, 0, 0, 0 ), 1, "age 0.1" );
			Eq( b.FrameAt( 1.0f, 0, 0, 0 ), 3, "age 1.0 holds last" );
			Eq( b.FrameAt( 5.0f, 0, 0, 0 ), 3, "age 5.0 still holds last" );
			Eq( b.FrameAt( 0.3f, 0, 2, 0 ), 3, "base 2 + 3 clamps to last" );
		} );

		registry.Add( "particle-flipbook", "PingPong reverses at both ends", () =>
		{
			var b = Book( frames: 4 ); b.Mode = ParticleFlipbookMode.PingPong;
			Eq( b.FrameAt( 0.0f, 0, 0, 0 ), 0, "age 0.0" );
			Eq( b.FrameAt( 0.3f, 0, 0, 0 ), 3, "age 0.3 hits far end" );
			Eq( b.FrameAt( 0.4f, 0, 0, 0 ), 2, "age 0.4 turns back" );
			Eq( b.FrameAt( 0.5f, 0, 0, 0 ), 1, "age 0.5" );
			Eq( b.FrameAt( 0.6f, 0, 0, 0 ), 0, "age 0.6 hits near end" );
			Eq( b.FrameAt( 0.7f, 0, 0, 0 ), 1, "age 0.7 turns forward again" );
		} );

		registry.Add( "particle-flipbook", "PingPong degenerates cleanly for 1 and 2 frames", () =>
		{
			var one = Book( frames: 1 ); one.Mode = ParticleFlipbookMode.PingPong;
			Eq( one.FrameAt( 5.0f, 0, 0, 0 ), 0, "single frame never moves" );
			var two = Book( frames: 2 ); two.Mode = ParticleFlipbookMode.PingPong;
			Eq( two.FrameAt( 0.0f, 0, 0, 0 ), 0, "two-frame age 0" );
			Eq( two.FrameAt( 0.1f, 0, 0, 0 ), 1, "two-frame age 0.1" );
			Eq( two.FrameAt( 0.2f, 0, 0, 0 ), 0, "two-frame age 0.2" );
			Eq( two.FrameAt( 0.3f, 0, 0, 0 ), 1, "two-frame age 0.3" );
		} );

		registry.Add( "particle-flipbook", "speed scaling adds fps per inch per s, zero speed is exact base fps", () =>
		{
			var b = Book( frames: 16 ); b.SpeedScale = 0.1f;
			Eq( b.FrameAt( 0.1f, 0, 0, 0 ), 1, "speed 0, age 0.1 -> 1 frame at 10 fps" );
			Eq( b.FrameAt( 0.1f, 100, 0, 0 ), 2, "speed 100 in/s adds 10 fps -> 2 frames" );
			Eq( b.FrameAt( 0.2f, 50, 0, 0 ), 3, "speed 50 in/s adds 5 fps -> 3 frames at 15 fps" );
		} );

		registry.Add( "particle-flipbook", "random start is deterministic per seed and in range", () =>
		{
			var b = Book( frames: 16 );
			for ( uint s = 0; s < 100; s++ )
			{
				int off = b.StartOffsetFor( s );
				Check( off >= 0 && off < 16, $"seed {s} offset {off} out of range" );
			}
			Check( b.StartOffsetFor( 7 ) == b.StartOffsetFor( 7 ), "same seed must give same offset" );
			int distinct = 0;
			var seen = new System.Collections.Generic.HashSet<int>();
			for ( uint s = 0; s < 100; s++ ) seen.Add( b.StartOffsetFor( s ) );
			distinct = seen.Count;
			Check( distinct >= 2, $"100 seeds gave only {distinct} distinct offset(s); RNG is not spreading" );
		} );

		registry.Add( "particle-flipbook", "random start offsets are exact for known seeds", () =>
		{
			var b = Book( frames: 16 );
			Eq( b.StartOffsetFor( 42 ), 6, "seed 42 offset in 16 frames" );
			Eq( b.StartOffsetFor( 7 ), 14, "seed 7 offset in 16 frames" );
			Eq( b.StartOffsetFor( 5 ), 12, "seed 5 offset in 16 frames" );
			var b4 = Book( frames: 4 );
			Eq( b4.StartOffsetFor( 42 ), 1, "seed 42 offset in 4 frames" );
		} );

		registry.Add( "particle-flipbook", "random start shifts playback by the seeded offset", () =>
		{
			var b = Book( frames: 4 ); b.RandomStart = true;
			int off = b.StartOffsetFor( 42 );
			Eq( b.FrameAt( 0, 0, 0, 42 ), off % 4, "age 0 frame is the offset" );
			Eq( b.FrameAt( 0.1f, 0, 0, 42 ), (off + 1) % 4, "age 0.1 advances one past the offset" );
			Eq( b.FrameAt( 0, 0, 0, 42 ), b.FrameAt( 0, 0, 0, 42 ), "same seed twice is identical" );
		} );

		registry.Add( "particle-flipbook", "zero fps is static: base frame, or base plus offset", () =>
		{
			var b = Book( frames: 4 ); b.FramesPerSecond = 0;
			Eq( b.FrameAt( 0, 0, 2, 0 ), 2, "static, age 0" );
			Eq( b.FrameAt( 9.0f, 0, 2, 0 ), 2, "static, age 9 still base" );
			b.RandomStart = true;
			int off = b.StartOffsetFor( 5 );
			Eq( b.FrameAt( 0, 0, 2, 5 ), (2 + off) % 4, "static random start, age 0" );
			Eq( b.FrameAt( 9.0f, 0, 2, 5 ), (2 + off) % 4, "static random start never advances" );
		} );

		registry.Add( "particle-flipbook", "lifetime overload clamps age to the particle's life", () =>
		{
			var b = Book( frames: 4 );
			Eq( b.FrameAt( 5.0f, 1.0f, 0, 0, 0 ), b.FrameAt( 1.0f, 0, 0, 0 ), "age past lifetime equals age at lifetime" );
			Eq( b.FrameAt( 0.2f, 1.0f, 0, 0, 0 ), b.FrameAt( 0.2f, 0, 0, 0 ), "age inside lifetime is unchanged" );
			Throws( () => b.FrameAt( 0.2f, 0, 0, 0, 0 ), "lifetime 0" );
			Throws( () => b.FrameAt( 0.2f, -1, 0, 0, 0 ), "negative lifetime" );
		} );

		registry.Add( "particle-flipbook", "invalid evaluation inputs fail loudly", () =>
		{
			var b = Book( frames: 4 );
			Throws( () => b.FrameAt( -0.1f, 0, 0, 0 ), "negative age" );
			Throws( () => b.FrameAt( float.NaN, 0, 0, 0 ), "NaN age" );
			Throws( () => b.FrameAt( 0.1f, -5, 0, 0 ), "negative speed" );
			Throws( () => b.FrameAt( 0.1f, float.PositiveInfinity, 0, 0 ), "infinite speed" );
		} );

		registry.Add( "particle-flipbook", "out-of-range base frames clamp instead of wrapping", () =>
		{
			var b = Book( frames: 4 );
			Eq( b.FrameAt( 0, 0, 99, 0 ), 3, "base 99 clamps to last" );
			Eq( b.FrameAt( 0, 0, -5, 0 ), 0, "base -5 clamps to first" );
		} );

		registry.Add( "particle-flipbook", "Sample returns frame and UV rect together", () =>
		{
			var b = Book();
			var s = b.Sample( 0.25f, 10f, 0, 0, 0 );
			Eq( s.Frame, 2, "age 0.25 at 10 fps is frame 2" );
			Near( s.Uv.U0, 0.5f, 1e-6f, "sample frame 2 U0" ); Near( s.Uv.V0, 0, 1e-6f, "sample frame 2 V0" );
			Near( s.Uv.U1, 0.75f, 1e-6f, "sample frame 2 U1" ); Near( s.Uv.V1, 0.25f, 1e-6f, "sample frame 2 V1" );
			var uv = b.UvFor( s.Frame );
			Check( uv.U0 == s.Uv.U0 && uv.V0 == s.Uv.V0 && uv.U1 == s.Uv.U1 && uv.V1 == s.Uv.V1, "Sample UV must equal UvFor(frame)" );
			var held = Book( frames: 4 ); held.Mode = ParticleFlipbookMode.Once;
			Eq( held.Sample( 9f, 1f, 0, 0, 0 ).Frame, 3, "Sample past lifetime in Once holds last frame" );
		} );

		registry.Add( "particle-flipbook", "SampleState uses state age and velocity magnitude", () =>
		{
			var b = Book( frames: 16 ); b.SpeedScale = 0.1f;
			var state = new ParticleState { Age = 0.2f, Velocity = new Vector3( 30, 40, 0 ), Size = 2 };
			// Speed = |(30,40,0)| = 50 in/s, so effective fps = 10 + 0.1*50 = 15; floor(0.2*15) = 3.
			var s = b.SampleState( state, 1f, 0 );
			Eq( s.Frame, 3, "SampleState frame from age + velocity magnitude" );
			var uv = b.UvFor( 3 );
			Check( uv.U0 == s.Uv.U0 && uv.V0 == s.Uv.V0 && uv.U1 == s.Uv.U1 && uv.V1 == s.Uv.V1, "SampleState UV must equal UvFor(frame)" );
			Near( s.Uv.U0, 0.75f, 1e-6f, "frame 3 U0" ); Near( s.Uv.V0, 0, 1e-6f, "frame 3 V0" );
		} );

		registry.Add( "particle-flipbook", "UV rect corners match LiveBuilder.Quad assignment", () =>
		{
			// Convention lock for the sweep: Quad's bottom corners (a, b) get V = 1 and its
			// top corners (d, c) get V = 0, so the rect maps a -> (U0, V1), b -> (U1, V1),
			// c -> (U1, V0), d -> (U0, V0). If this mapping ever flips, the atlas renders upside-down.
			var b = Book();
			var uv = b.UvFor( 5 );
			Near( uv.U0, 0.25f, 1e-6f, "frame 5 U0" ); Near( uv.V1, 0.5f, 1e-6f, "frame 5 V1 (bottom corner V)" );
			Near( uv.U1, 0.5f, 1e-6f, "frame 5 U1" ); Near( uv.V0, 0.25f, 1e-6f, "frame 5 V0 (top corner V)" );
			Check( uv.V0 < uv.V1, "V0 (top edge) must be below V1 (bottom edge) in rect coordinates" );
		} );

		registry.Add( "particle-flipbook", "evaluator is pure: two instances agree on every input", () =>
		{
			var a = Book( frames: 8 ); a.Mode = ParticleFlipbookMode.PingPong; a.SpeedScale = 0.05f; a.RandomStart = true;
			var c = Book( frames: 8 ); c.Mode = ParticleFlipbookMode.PingPong; c.SpeedScale = 0.05f; c.RandomStart = true;
			for ( int i = 0; i < 50; i++ )
			{
				float age = i * 0.07f;
				for ( uint s = 0; s < 3; s++ )
					Eq( a.FrameAt( age, 30 + i, 3, s ), c.FrameAt( age, 30 + i, 3, s ), $"instance mismatch at age {age} seed {s}" );
			}
		} );
	}
}
