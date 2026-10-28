using System;
using System.Numerics;
using Marionette.Desktop;
using Silk.NET.OpenGL;

/// <summary>
/// GPU particle LOD / culling tests (particles wave-6 lane P45, group
/// "gpu-particle-lod"). Every GPU result is compared against the CPU
/// reference in GpuParticleLod EXACTLY — band flags and skip masks are
/// integers from identical float32 arithmetic, so there is no epsilon
/// anywhere in these tests. Headless EGL only; nothing here claims
/// his-eye success, and nothing here wires LOD into the integrate pass
/// (that wiring is sweep-only — see GpuParticleLodSweepNote).
/// </summary>
internal static class GpuParticleLodTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool cond, string msg ) { if ( !cond ) throw new Exception( "gpu-particle-lod: " + msg ); }

		static ParticleState Make( Vector3 pos, float alive = 1f, Vector3 vel = default ) => new()
		{
			Position = pos, Age = 0, Velocity = vel, Alive = alive,
			Rotation = Quaternion.Identity, AngularVelocity = Vector3.Zero, Size = 1
		};

		static void CheckFlags( uint[] got, uint[] want, string msg )
		{
			if ( got.Length != want.Length ) throw new Exception( $"gpu-particle-lod: {msg}: length {got.Length} != {want.Length}" );
			for ( int i = 0; i < want.Length; i++ )
				if ( got[i] != want[i ] ) throw new Exception( $"gpu-particle-lod: {msg}: slot {i} got {got[i]} want {want[i]}" );
		}

		static void CheckNoGlError( GL gl, string msg )
		{
			if ( gl.GetError() != GLEnum.NoError ) throw new Exception( "gpu-particle-lod: GL error after " + msg );
		}

		registry.Add( "gpu-particle-lod", "band flags at known distances match cpu exactly", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleLod.CreateProgram( gl );
			var cam = Vector3.Zero;
			// Distances: 0, 1000, 1500 (boundary -> Full), 1501, 6000
			// (boundary -> Reduced), 6001, 10000. The 900/1200/0 slot is
			// exactly 1500 by the 3-4-5 triangle, via squared distance.
			var states = new[]
			{
				Make( new Vector3( 0, 0, 0 ) ),
				Make( new Vector3( 1000, 0, 0 ) ),
				Make( new Vector3( 1500, 0, 0 ) ),
				Make( new Vector3( 900, 1200, 0 ) ),
				Make( new Vector3( 1501, 0, 0 ) ),
				Make( new Vector3( 6000, 0, 0 ) ),
				Make( new Vector3( 6001, 0, 0 ) ),
				Make( new Vector3( 10000, 0, 0 ) ),
				Make( new Vector3( 20000, 0, 0 ), alive: 0 ), // dead, still banded by distance
			};
			uint stateBuf = GpuParticleSystem.UploadStates( gl, states );
			uint flagBuf = GpuParticleLod.CreateFlagBuffer( gl, states.Length );
			try
			{
				GpuParticleLod.ClassifyBands( gl, prog, stateBuf, flagBuf, states.Length, cam );
				var got = GpuParticleLod.DownloadFlags( gl, flagBuf, states.Length );
				var want = GpuParticleLod.ClassifyAll( states, cam );
				CheckFlags( got, want, "band flags" );
				uint[] expected = { 0, 0, 0, 0, 1, 1, 2, 2, 2 };
				CheckFlags( got, expected, "band flags known distances" );
				CheckNoGlError( gl, "band pass" );
			}
			finally { GpuParticleLod.Dispose( gl, prog, stateBuf, flagBuf ); }
		} );

		registry.Add( "gpu-particle-lod", "custom thresholds re-band the same particles", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleLod.CreateProgram( gl );
			var cam = new Vector3( 10, 0, 0 );
			var states = new[]
			{
				Make( new Vector3( 110, 0, 0 ) ),  // distance 100 -> Full at boundary
				Make( new Vector3( 210, 0, 0 ) ),  // distance 200 -> Reduced at boundary
				Make( new Vector3( 211, 0, 0 ) ),  // distance 201 -> Collapsed
				Make( new Vector3( 10, 50, 0 ) ),  // distance 50 -> Full
			};
			uint stateBuf = GpuParticleSystem.UploadStates( gl, states );
			uint flagBuf = GpuParticleLod.CreateFlagBuffer( gl, states.Length );
			try
			{
				GpuParticleLod.ClassifyBands( gl, prog, stateBuf, flagBuf, states.Length, cam, 100f, 200f );
				var got = GpuParticleLod.DownloadFlags( gl, flagBuf, states.Length );
				CheckFlags( got, GpuParticleLod.ClassifyAll( states, cam, 100f, 200f ), "custom-threshold bands" );
				CheckFlags( got, new uint[] { 0, 1, 2, 0 }, "custom-threshold known bands" );
				CheckNoGlError( gl, "custom-threshold band pass" );
			}
			finally { GpuParticleLod.Dispose( gl, prog, stateBuf, flagBuf ); }
		} );

		registry.Add( "gpu-particle-lod", "skip pattern over N dispatches matches cpu exactly", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleLod.CreateProgram( gl );
			// Slots 0-1 Full, 2-3 Reduced, 4-11 Collapsed (camera at origin,
			// defaults 1500 / 6000). Skip N = 4.
			var states = new ParticleState[12];
			states[0] = Make( new Vector3( 100, 0, 0 ) );
			states[1] = Make( new Vector3( 0, 100, 0 ) );
			states[2] = Make( new Vector3( 3000, 0, 0 ) );
			states[3] = Make( new Vector3( 0, 3000, 0 ) );
			for ( int i = 4; i < 12; i++ ) states[i] = Make( new Vector3( 10000 + i * 100, 0, 0 ) );
			uint stateBuf = GpuParticleSystem.UploadStates( gl, states );
			uint flagBuf = GpuParticleLod.CreateFlagBuffer( gl, states.Length );
			uint maskBuf = GpuParticleLod.CreateMaskBuffer( gl, states.Length );
			try
			{
				GpuParticleLod.ClassifyBands( gl, prog, stateBuf, flagBuf, states.Length, Vector3.Zero );
				var flags = GpuParticleLod.DownloadFlags( gl, flagBuf, states.Length );
				CheckFlags( flags, new uint[] { 0, 0, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2 }, "skip-test bands" );
				int[] integrated = new int[states.Length];
				for ( uint d = 0; d < 8; d++ )
				{
					GpuParticleLod.ComputeSkipMask( gl, prog, flagBuf, maskBuf, states.Length, d, 4 );
					var got = GpuParticleLod.DownloadMasks( gl, maskBuf, states.Length );
					CheckFlags( got, GpuParticleLod.SkipMaskAll( flags, d, 4 ), $"skip mask dispatch {d}" );
					for ( int i = 0; i < states.Length; i++ ) integrated[i] += (int)got[i];
				}
				// Full + Reduced integrate all 8 dispatches; each Collapsed
				// integrates exactly 8/4 = 2 times over 8 dispatches.
				for ( int i = 0; i < 4; i++ ) Check( integrated[i] == 8, $"slot {i} integrated {integrated[i]} times, want 8" );
				for ( int i = 4; i < 12; i++ ) Check( integrated[i] == 2, $"collapsed slot {i} integrated {integrated[i]} times, want 2" );
				// N = 1 integrates everything, including Collapsed.
				GpuParticleLod.ComputeSkipMask( gl, prog, flagBuf, maskBuf, states.Length, 3, 1 );
				CheckFlags( GpuParticleLod.DownloadMasks( gl, maskBuf, states.Length ),
					new uint[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, "skipN=1 mask" );
				CheckNoGlError( gl, "skip passes" );
			}
			finally { GpuParticleLod.Dispose( gl, prog, stateBuf, flagBuf, maskBuf ); }
		} );

		registry.Add( "gpu-particle-lod", "budget counts live and writes spawn-allowed below at above", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleLod.CreateProgram( gl );
			var states = new ParticleState[10];
			for ( int i = 0; i < 10; i++ ) states[i] = Make( new Vector3( i * 10, 0, 0 ), alive: i < 7 ? 1f : 0f );
			uint stateBuf = GpuParticleSystem.UploadStates( gl, states );
			uint counterBuf = GpuParticleLod.CreateCounterBuffer( gl );
			try
			{
				GpuParticleLod.CountBudget( gl, prog, stateBuf, counterBuf, states.Length, 10 );
				var c = GpuParticleLod.DownloadCounters( gl, counterBuf );
				Check( c.live == 7 && c.spawnAllowed == 3, $"below budget: live {c.live} allowed {c.spawnAllowed}, want 7/3" );
				GpuParticleLod.CountBudget( gl, prog, stateBuf, counterBuf, states.Length, 7 );
				c = GpuParticleLod.DownloadCounters( gl, counterBuf );
				Check( c.live == 7 && c.spawnAllowed == 0, $"at budget: live {c.live} allowed {c.spawnAllowed}, want 7/0" );
				GpuParticleLod.CountBudget( gl, prog, stateBuf, counterBuf, states.Length, 3 );
				c = GpuParticleLod.DownloadCounters( gl, counterBuf );
				Check( c.live == 7 && c.spawnAllowed == 0, $"above budget: live {c.live} allowed {c.spawnAllowed}, want 7/0" );
				// Repeat call must not accumulate: the counter is re-zeroed.
				GpuParticleLod.CountBudget( gl, prog, stateBuf, counterBuf, states.Length, 10 );
				c = GpuParticleLod.DownloadCounters( gl, counterBuf );
				Check( c.live == 7 && c.spawnAllowed == 3, $"repeat count: live {c.live} allowed {c.spawnAllowed}, want 7/3" );
				// Budget pass kills nothing: states are byte-identical after.
				var back = GpuParticleSystem.DownloadStates( gl, stateBuf, states.Length );
				for ( int i = 0; i < states.Length; i++ )
					Check( back[i].Position == states[i].Position && back[i].Alive == states[i].Alive,
						$"budget pass rewrote particle {i}" );
				CheckNoGlError( gl, "budget passes" );
			}
			finally { GpuParticleLod.Dispose( gl, prog, stateBuf, counterBuf ); }
		} );

		registry.Add( "gpu-particle-lod", "non-multiple-of-workgroup-size counts are guarded", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleLod.CreateProgram( gl );
			foreach ( int count in new[] { 1, 257, 1000 } )
			{
				var states = new ParticleState[count];
				for ( int i = 0; i < count; i++ )
					states[i] = Make( new Vector3( ( i % 3 ) * 3000, 0, 0 ), alive: i % 5 == 0 ? 0f : 1f );
				uint stateBuf = GpuParticleSystem.UploadStates( gl, states );
				uint flagBuf = GpuParticleLod.CreateFlagBuffer( gl, count );
				uint maskBuf = GpuParticleLod.CreateMaskBuffer( gl, count );
				uint counterBuf = GpuParticleLod.CreateCounterBuffer( gl );
				try
				{
					GpuParticleLod.ClassifyBands( gl, prog, stateBuf, flagBuf, count, Vector3.Zero );
					var flags = GpuParticleLod.DownloadFlags( gl, flagBuf, count );
					CheckFlags( flags, GpuParticleLod.ClassifyAll( states, Vector3.Zero ), $"bands count {count}" );
					// Tail slots specifically: the last slot is the one an
					// out-of-bounds write would corrupt or a bad guard drop.
					Check( flags[count - 1] == GpuParticleLod.ClassifyBand( states[count - 1].Position, Vector3.Zero,
						GpuParticleLod.DefaultFullMax, GpuParticleLod.DefaultReducedMax ), $"tail band count {count}" );
					GpuParticleLod.ComputeSkipMask( gl, prog, flagBuf, maskBuf, count, 5, 3 );
					CheckFlags( GpuParticleLod.DownloadMasks( gl, maskBuf, count ),
						GpuParticleLod.SkipMaskAll( flags, 5, 3 ), $"skip mask count {count}" );
					GpuParticleLod.CountBudget( gl, prog, stateBuf, counterBuf, count, 100000 );
					var c = GpuParticleLod.DownloadCounters( gl, counterBuf );
					int wantLive = GpuParticleLod.CountLive( states );
					Check( (int)c.live == wantLive && (int)c.spawnAllowed == 100000 - wantLive,
						$"budget count {count}: live {c.live} allowed {c.spawnAllowed}, want {wantLive}/{100000 - wantLive}" );
					CheckNoGlError( gl, $"guarded passes count {count}" );
				}
				finally { GpuParticleLod.Dispose( gl, 0, stateBuf, flagBuf, maskBuf, counterBuf ); }
			}
			// Zero particles: all three passes run, budget reports 0 live.
			{
				uint stateBuf = GpuParticleSystem.UploadStates( gl, Array.Empty<ParticleState>() );
				uint flagBuf = GpuParticleLod.CreateFlagBuffer( gl, 0 );
				uint maskBuf = GpuParticleLod.CreateMaskBuffer( gl, 0 );
				uint counterBuf = GpuParticleLod.CreateCounterBuffer( gl );
				try
				{
					GpuParticleLod.ClassifyBands( gl, prog, stateBuf, flagBuf, 0, Vector3.Zero );
					GpuParticleLod.ComputeSkipMask( gl, prog, flagBuf, maskBuf, 0, 0, 4 );
					GpuParticleLod.CountBudget( gl, prog, stateBuf, counterBuf, 0, 50 );
					var c = GpuParticleLod.DownloadCounters( gl, counterBuf );
					Check( c.live == 0 && c.spawnAllowed == 50, $"zero-count budget: live {c.live} allowed {c.spawnAllowed}, want 0/50" );
					CheckNoGlError( gl, "zero-count passes" );
				}
				finally { GpuParticleLod.Dispose( gl, prog, stateBuf, flagBuf, maskBuf, counterBuf ); }
			}
		} );

		registry.Add( "gpu-particle-lod", "cpu lag bounds and argument validation", () =>
		{
			// N=4, dt=1/60, speed 120: skipped at most 3 steps -> 3*120/60 = 6.
			float lag = GpuParticleLod.LagBound( 4, 1f / 60f, 120f );
			Check( Math.Abs( lag - 6f ) < 1e-5f, $"lag bound {lag}, want 6" );
			Check( GpuParticleLod.LagBound( 1, 1f / 60f, 120f ) == 0, "skipN=1 must have zero lag" );
			float lagA = GpuParticleLod.LagBoundWithAccel( 4, 1f / 60f, 120f, 40f );
			float wantA = 6f + 0.5f * 40f * 0.05f * 0.05f; // window = 3/60 = 0.05
			Check( Math.Abs( lagA - wantA ) < 1e-5f, $"accel lag bound {lagA}, want {wantA}" );
			Check( GpuParticleLod.SpawnAllowed( 0, 5 ) == 5 && GpuParticleLod.SpawnAllowed( 5, 5 ) == 0
				&& GpuParticleLod.SpawnAllowed( 9, 5 ) == 0, "spawn-allowed arithmetic" );
			static bool Throws( Action a ) { try { a(); return false; } catch ( ArgumentOutOfRangeException ) { return true; } catch ( ArgumentException ) { return true; } }
			Check( Throws( () => GpuParticleLod.ClassifyBand( Vector3.Zero, Vector3.Zero, 200f, 100f ) ), "reducedMax < fullMax must throw" );
			Check( Throws( () => GpuParticleLod.ShouldIntegrate( 2, 0, 0, 0 ) ), "skipN=0 must throw" );
			Check( Throws( () => GpuParticleLod.SpawnAllowed( 3, -1 ) ), "negative budget must throw" );
			Check( Throws( () => GpuParticleLod.LagBound( 2, -1f, 10f ) ), "negative dt must throw" );
		} );
	}
}
