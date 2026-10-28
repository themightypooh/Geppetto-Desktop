using System.Numerics;
using Marionette.Desktop;
using Silk.NET.OpenGL;

// Sweep line (coordinator sweep only, never added by this lane):
//   GpuParticleTrailsTests.Register( registry, settings );  // after GpuParticleSystemTests.Register in tests/TestRegistry.cs
// Integration lines for the sweep (reported, not applied): call
//   GpuParticleTrails.Dispatch( gl, trailProgram, particleBuffer, trailBuffer, count, trailLength, stepIndex );
// once per substep, AFTER GpuParticleSystem.Dispatch, on the same particle SSBO;
// call GpuParticleTrails.FillSpawn(...) for a slot when it is (re)spawned, before it goes live.
internal static class GpuParticleTrailsTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Close( float a, float b, float eps, string what ) { if ( MathF.Abs( a - b ) > eps ) throw new Exception( $"{what}: got {a}, want {b} (eps {eps})" ); }
		void CloseVec( Vector3 a, Vector3 b, float eps, string what )
		{
			Close( a.X, b.X, eps, what + ".X" ); Close( a.Y, b.Y, eps, what + ".Y" ); Close( a.Z, b.Z, eps, what + ".Z" );
		}
		void CloseTrail( Vector3[] got, Vector3[] want, float eps, string what )
		{
			Check( got.Length == want.Length, $"{what}: length {got.Length} != {want.Length}" );
			for ( int i = 0; i < want.Length; i++ ) CloseVec( got[i], want[i], eps, $"{what}[{i}]" );
		}
		static ParticleState P( Vector3 pos, float alive = 1 ) => new() { Position = pos, Velocity = Vector3.Zero, Alive = alive, Rotation = Quaternion.Identity, Size = 1 };
		static float MaxTrailDev( Vector3[] a, Vector3[] b )
		{
			float m = 0;
			for ( int i = 0; i < a.Length; i++ ) m = MathF.Max( m, (a[i] - b[i]).Length() );
			return m;
		}

		registry.Add( "gpu-particle-trails", "cpu ring wraps correctly after more steps than the trail length", () =>
		{
			var trails = new CpuTrails( 1, 4 );
			for ( int step = 0; step <= 9; step++ )
				trails.Record( new[] { P( new Vector3( step, step * 2, step * 3 ) ) }, step );
			// After step 9 with length 4, newest-first is steps 9, 8, 7, 6.
			CloseTrail( trails.ReadTrail( 0 ), new[]
			{
				new Vector3( 9, 18, 27 ), new Vector3( 8, 16, 24 ), new Vector3( 7, 14, 21 ), new Vector3( 6, 12, 18 ),
			}, 0, "wrapped trail" );
			// Mid-wrap check on a fresh ring: after step 4 (5 records, length 4) head is 4, tail is 1, step 0 is gone.
			var mid = new CpuTrails( 1, 4 );
			for ( int step = 0; step <= 4; step++ ) mid.Record( new[] { P( new Vector3( step, 0, 0 ) ) }, step );
			CloseTrail( mid.ReadTrail( 0 ), new[] { new Vector3( 4, 0, 0 ), new Vector3( 3, 0, 0 ), new Vector3( 2, 0, 0 ), new Vector3( 1, 0, 0 ) }, 0, "mid-wrap trail" );
		} );

		registry.Add( "gpu-particle-trails", "cpu spawn fill repeats spawn position, no streak to origin", () =>
		{
			var trails = new CpuTrails( 1, 4 );
			trails.FillSpawn( 0, new Vector3( 5, 5, 5 ) );
			CloseTrail( trails.ReadTrail( 0 ), new[] { new Vector3( 5, 5, 5 ), new Vector3( 5, 5, 5 ), new Vector3( 5, 5, 5 ), new Vector3( 5, 5, 5 ) }, 0, "spawn-only trail" );
			trails.Record( new[] { P( new Vector3( 6, 5, 5 ) ) }, 0 );
			trails.Record( new[] { P( new Vector3( 7, 5, 5 ) ) }, 1 );
			// Newest-first: 7, 6, then spawn repeats — never Vector3.Zero.
			CloseTrail( trails.ReadTrail( 0 ), new[] { new Vector3( 7, 5, 5 ), new Vector3( 6, 5, 5 ), new Vector3( 5, 5, 5 ), new Vector3( 5, 5, 5 ) }, 0, "spawn + records trail" );
			Check( trails.ReadTrail( 0 )[3] != Vector3.Zero, "tail streaked to the origin" );
		} );

		registry.Add( "gpu-particle-trails", "cpu dead slots never written, even with a non-finite stored position", () =>
		{
			var trails = new CpuTrails( 2, 3 );
			trails.FillSpawn( 0, new Vector3( 1, 1, 1 ) );
			trails.FillSpawn( 1, new Vector3( 9, 9, 9 ) );
			// Slot 1 is dead and carries NaN: Record must skip it before any validation, not throw.
			var states = new[] { P( new Vector3( 2, 1, 1 ) ), P( new Vector3( float.NaN, 0, 0 ), alive: 0 ) };
			trails.Record( states, 0 );
			trails.Record( new[] { P( new Vector3( 3, 1, 1 ) ), P( new Vector3( float.NaN, 0, 0 ), alive: 0 ) }, 1 );
			CloseTrail( trails.ReadTrail( 1 ), new[] { new Vector3( 9, 9, 9 ), new Vector3( 9, 9, 9 ), new Vector3( 9, 9, 9 ) }, 0, "dead slot trail" );
			CloseTrail( trails.ReadTrail( 0 ), new[] { new Vector3( 3, 1, 1 ), new Vector3( 2, 1, 1 ), new Vector3( 1, 1, 1 ) }, 0, "live slot trail" );
		} );

		registry.Add( "gpu-particle-trails", "gpu head is newest and ring wraps after more steps than the trail length", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleTrails.CreateProgram( gl );
			const int trailLength = 4;
			uint trailBuf = GpuParticleTrails.CreateTrailBuffer( gl, 1, trailLength );
			var oracle = new CpuTrails( 1, trailLength );
			oracle.FillSpawn( 0, new Vector3( 0, 0, 0 ) );
			GpuParticleTrails.FillSpawn( gl, trailBuf, 0, 1, trailLength, new Vector3( 0, 0, 0 ) );
			for ( int step = 0; step <= 5; step++ )
			{
				var states = new[] { P( new Vector3( step * 1.5f, step, -step * 0.5f ) ) };
				uint buf = GpuParticleSystem.UploadStates( gl, states );
				GpuParticleTrails.Dispatch( gl, prog, buf, trailBuf, 1, trailLength, step );
				oracle.Record( states, step );
				GpuParticleSystem.Dispose( gl, buf, 0 );
				// Keep the trail buffer alive across steps; only the particle buffer is re-uploaded.
				if ( step == 5 )
				{
					var got = GpuParticleTrails.ReadTrail( gl, trailBuf, 0, 1, trailLength, step );
					var want = oracle.ReadTrail( 0 );
					float dev = MaxTrailDev( got, want );
					Console.WriteLine( $"gpu-trails wrap: maxDev={dev:G9} head={got[0]}" );
					CloseTrail( got, want, 1e-4f, "gpu wrapped trail" );
					Check( got[0] == states[0].Position, "gpu head is not the newest position" );
				}
			}
			GpuParticleTrails.Dispose( gl, trailBuf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after gpu wrap test" );
		} );

		registry.Add( "gpu-particle-trails", "gpu ring matches cpu oracle within epsilon over several particles and steps", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleTrails.CreateProgram( gl );
			const int count = 5, trailLength = 6;
			uint trailBuf = GpuParticleTrails.CreateTrailBuffer( gl, count, trailLength );
			var oracle = new CpuTrails( count, trailLength );
			for ( int i = 0; i < count; i++ )
			{
				var spawn = new Vector3( i * 0.37f, -i * 0.91f, i * 1.13f );
				oracle.FillSpawn( i, spawn );
				GpuParticleTrails.FillSpawn( gl, trailBuf, i, count, trailLength, spawn );
			}
			float maxDev = 0;
			for ( int step = 0; step < 7; step++ )
			{
				var states = new ParticleState[count];
				for ( int i = 0; i < count; i++ )
					states[i] = P( new Vector3( i * 0.37f + step * 0.73f, -i * 0.91f + step * 0.19f, i * 1.13f - step * 0.41f ) );
				uint buf = GpuParticleSystem.UploadStates( gl, states );
				GpuParticleTrails.Dispatch( gl, prog, buf, trailBuf, count, trailLength, step );
				oracle.Record( states, step );
				GpuParticleSystem.Dispose( gl, buf, 0 );
				if ( step == 6 )
				{
					var raw = GpuParticleTrails.DownloadTrails( gl, trailBuf, count, trailLength );
					for ( int i = 0; i < count; i++ )
						maxDev = MathF.Max( maxDev, MaxTrailDev( GpuParticleTrails.ReadTrailFromFloats( raw, i, count, trailLength, step ), oracle.ReadTrail( i ) ) );
				}
			}
			Console.WriteLine( $"gpu-trails oracle: maxDev={maxDev:G9}" );
			Check( maxDev <= 1e-4f, $"gpu/cpu trail deviation too large: {maxDev}" );
			GpuParticleTrails.Dispose( gl, trailBuf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after gpu oracle test" );
		} );

		registry.Add( "gpu-particle-trails", "gpu dead slots never written", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleTrails.CreateProgram( gl );
			const int trailLength = 4;
			uint trailBuf = GpuParticleTrails.CreateTrailBuffer( gl, 2, trailLength );
			GpuParticleTrails.FillSpawn( gl, trailBuf, 0, 2, trailLength, new Vector3( 1, 1, 1 ) );
			GpuParticleTrails.FillSpawn( gl, trailBuf, 1, 2, trailLength, new Vector3( 9, 9, 9 ) );
			for ( int step = 0; step < 3; step++ )
			{
				var states = new[] { P( new Vector3( 2 + step, 1, 1 ) ), P( new Vector3( 0, 0, 0 ), alive: 0 ) };
				uint buf = GpuParticleSystem.UploadStates( gl, states );
				GpuParticleTrails.Dispatch( gl, prog, buf, trailBuf, 2, trailLength, step );
				GpuParticleSystem.Dispose( gl, buf, 0 );
			}
			var raw = GpuParticleTrails.DownloadTrails( gl, trailBuf, 2, trailLength );
			var dead = GpuParticleTrails.ReadTrailFromFloats( raw, 1, 2, trailLength, 2 );
			CloseTrail( dead, new[] { new Vector3( 9, 9, 9 ), new Vector3( 9, 9, 9 ), new Vector3( 9, 9, 9 ), new Vector3( 9, 9, 9 ) }, 0, "gpu dead slot trail" );
			var live = GpuParticleTrails.ReadTrailFromFloats( raw, 0, 2, trailLength, 2 );
			CloseTrail( live, new[] { new Vector3( 4, 1, 1 ), new Vector3( 3, 1, 1 ), new Vector3( 2, 1, 1 ), new Vector3( 1, 1, 1 ) }, 1e-4f, "gpu live slot trail" );
			GpuParticleTrails.Dispose( gl, trailBuf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after gpu dead-slot test" );
		} );

		registry.Add( "gpu-particle-trails", "sentinel slots past count byte-identical at count 257", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleTrails.CreateProgram( gl );
			const int count = 257, total = 512, trailLength = 4;
			uint trailBuf = GpuParticleTrails.CreateTrailBuffer( gl, total, trailLength );
			var sentinel = new Vector3( 7, 7, 7 );
			for ( int i = count; i < total; i++ ) GpuParticleTrails.FillSpawn( gl, trailBuf, i, total, trailLength, sentinel );
			var oracle = new CpuTrails( count, trailLength );
			for ( int step = 0; step < 3; step++ )
			{
				var states = new ParticleState[total];
				for ( int i = 0; i < count; i++ ) states[i] = P( new Vector3( (i % 7) - 3 + step * 0.3f, (i % 5) - 2, step ) );
				for ( int i = count; i < total; i++ ) states[i] = P( Vector3.Zero );
				uint buf = GpuParticleSystem.UploadStates( gl, states );
				GpuParticleTrails.Dispatch( gl, prog, buf, trailBuf, count, trailLength, step );
				var head = new ParticleState[count];
				Array.Copy( states, head, count );
				oracle.Record( head, step );
				GpuParticleSystem.Dispose( gl, buf, 0 );
			}
			var raw = GpuParticleTrails.DownloadTrails( gl, trailBuf, total, trailLength );
			int sentinelChanged = 0;
			for ( int i = count; i < total; i++ )
				for ( int k = 0; k < trailLength; k++ )
				{
					int b = (i * trailLength + k) * 4;
					if ( raw[b + 0] != 7f || raw[b + 1] != 7f || raw[b + 2] != 7f ) sentinelChanged++;
				}
			float maxDev = 0;
			for ( int i = 0; i < count; i++ )
				maxDev = MathF.Max( maxDev, MaxTrailDev( GpuParticleTrails.ReadTrailFromFloats( raw, i, total, trailLength, 2 ), oracle.ReadTrail( i ) ) );
			Console.WriteLine( $"gpu-trails guard: count={count} maxDev={maxDev:G9} sentinelChangedPoints={sentinelChanged}/{(total - count) * trailLength}" );
			Check( maxDev <= 1e-4f, $"guard run deviation too large: {maxDev}" );
			Check( sentinelChanged == 0, $"{sentinelChanged} beyond-count trail points changed — uCount guard failed" );
			GpuParticleTrails.Dispose( gl, trailBuf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after guard test" );
		} );

		registry.Add( "gpu-particle-trails", "count zero dispatches nothing and leaves the buffer untouched", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleTrails.CreateProgram( gl );
			const int trailLength = 4;
			uint trailBuf = GpuParticleTrails.CreateTrailBuffer( gl, 1, trailLength );
			GpuParticleTrails.FillSpawn( gl, trailBuf, 0, 1, trailLength, new Vector3( 3, 3, 3 ) );
			var before = GpuParticleTrails.DownloadTrails( gl, trailBuf, 1, trailLength );
			uint buf = GpuParticleSystem.UploadStates( gl, new[] { P( Vector3.Zero ) } );
			GpuParticleTrails.Dispatch( gl, prog, buf, trailBuf, 0, trailLength, 0 );
			var after = GpuParticleTrails.DownloadTrails( gl, trailBuf, 1, trailLength );
			Check( before.Length == after.Length, "count-0 download length changed" );
			for ( int i = 0; i < before.Length; i++ ) Check( before[i] == after[i], $"count-0 dispatch changed trail float {i}" );
			GpuParticleSystem.Dispose( gl, buf, 0 );
			GpuParticleTrails.Dispose( gl, trailBuf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after count-0 test" );
		} );

		registry.Add( "gpu-particle-trails", "validation fails loudly: length, count, step, handles, non-finite positions", () =>
		{
			void Throws( Action f, string what )
			{
				try { f(); } catch ( ArgumentException ) { return; }
				throw new Exception( $"expected ArgumentException for {what}" );
			}
			Throws( () => new CpuTrails( 1, 0 ), "trail length 0" );
			Throws( () => new CpuTrails( 1, 33 ), "trail length 33 over cap 32 (must not shrink)" );
			Throws( () => new CpuTrails( 1, -1 ), "trail length negative" );
			Throws( () => new CpuTrails( -1, 4 ), "particle count negative" );
			Throws( () => GpuParticleTrails.ValidateTrailLength( 100 ), "trail length 100" );
			var trails = new CpuTrails( 1, 4 );
			Throws( () => trails.Record( new[] { P( new Vector3( 1, 0, 0 ) ) }, -1 ), "negative step" );
			Throws( () => trails.Record( new[] { P( new Vector3( float.NaN, 0, 0 ) ) }, 0 ), "live NaN position in record" );
			Throws( () => trails.FillSpawn( 0, new Vector3( float.PositiveInfinity, 0, 0 ) ), "non-finite spawn position" );
			Throws( () => trails.ReadTrail( 1 ), "slot out of range" );
			Throws( () => GpuParticleTrails.ReadTrailFromFloats( null!, 0, 1, 4, 0 ), "null trail data" );
			Throws( () => GpuParticleTrails.ReadTrailFromFloats( new float[8], 0, 1, 4, 0 ), "short trail data" );
			Throws( () => GpuParticleTrails.DecimateBySpacing( new[] { new Vector3( 0, 0, 0 ) }, 0 ), "decimation spacing 0" );
			Throws( () => GpuParticleTrails.DecimateBySpacing( new[] { new Vector3( 0, 0, 0 ) }, float.NaN ), "decimation spacing NaN" );
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleTrails.CreateProgram( gl );
			uint trailBuf = GpuParticleTrails.CreateTrailBuffer( gl, 1, 4 );
			uint buf = GpuParticleSystem.UploadStates( gl, new[] { P( Vector3.Zero ) } );
			Throws( () => GpuParticleTrails.Dispatch( gl, prog, buf, 0, 1, 4, 0 ), "zero trail handle" );
			Throws( () => GpuParticleTrails.Dispatch( gl, prog, 0, trailBuf, 1, 4, 0 ), "zero particle handle" );
			Throws( () => GpuParticleTrails.Dispatch( gl, prog, buf, trailBuf, -1, 4, 0 ), "negative count dispatch" );
			Throws( () => GpuParticleTrails.Dispatch( gl, prog, buf, trailBuf, 1, 33, 0 ), "over-cap trail length dispatch" );
			Throws( () => GpuParticleTrails.Dispatch( gl, prog, buf, trailBuf, 1, 4, -1 ), "negative step dispatch" );
			Throws( () => GpuParticleTrails.FillSpawn( gl, trailBuf, 1, 1, 4, new Vector3( 1, 1, 1 ) ), "spawn slot out of range" );
			GpuParticleSystem.Dispose( gl, buf, 0 );
			GpuParticleTrails.Dispose( gl, trailBuf, prog );
		} );

		registry.Add( "gpu-particle-trails", "spacing decimation read keeps head, drops close points, always keeps tail", () =>
		{
			var trail = new[] { new Vector3( 0, 0, 0 ), new Vector3( 0.5f, 0, 0 ), new Vector3( 1, 0, 0 ), new Vector3( 2, 0, 0 ), new Vector3( 3, 0, 0 ) };
			CloseTrail( GpuParticleTrails.DecimateBySpacing( trail, 1 ), new[] { new Vector3( 0, 0, 0 ), new Vector3( 1, 0, 0 ), new Vector3( 2, 0, 0 ), new Vector3( 3, 0, 0 ) }, 0, "decimated trail" );
			var shortTrail = new[] { new Vector3( 0, 0, 0 ), new Vector3( 0.9f, 0, 0 ), new Vector3( 0.95f, 0, 0 ) };
			CloseTrail( GpuParticleTrails.DecimateBySpacing( shortTrail, 1 ), new[] { new Vector3( 0, 0, 0 ), new Vector3( 0.95f, 0, 0 ) }, 0, "tail-kept trail" );
		} );
	}
}
