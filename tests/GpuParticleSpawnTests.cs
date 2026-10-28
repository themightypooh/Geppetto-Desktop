using System.Numerics;
using Marionette.Desktop;
using Silk.NET.OpenGL;

internal static class GpuParticleSpawnTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Close( float a, float b, float eps, string what ) { if ( MathF.Abs( a - b ) > eps ) throw new Exception( $"{what}: got {a}, want {b} (eps {eps})" ); }
		void CloseVec( Vector3 a, Vector3 b, float eps, string what )
		{
			Close( a.X, b.X, eps, what + ".X" ); Close( a.Y, b.Y, eps, what + ".Y" ); Close( a.Z, b.Z, eps, what + ".Z" );
		}
		// Exact whole-state equality (all 7 fields): spawn/kill do copies and
		// constant writes only, so GPU == CPU exactly, never epsilon.
		void SameState( ParticleState a, ParticleState b, string what )
		{
			Check( a.Position == b.Position && a.Velocity == b.Velocity && a.Rotation == b.Rotation
				&& a.AngularVelocity == b.AngularVelocity && a.Age == b.Age && a.Alive == b.Alive && a.Size == b.Size,
				$"{what}: states differ: got pos={a.Position} vel={a.Velocity} age={a.Age} alive={a.Alive} size={a.Size}, want pos={b.Position} vel={b.Velocity} age={b.Age} alive={b.Alive} size={b.Size}" );
		}
		static ParticleState P( Vector3 pos, Vector3 vel, float alive = 1, float age = 0, float size = 1 ) => new()
		{
			Position = pos, Velocity = vel, Alive = alive, Age = age,
			Rotation = Quaternion.Identity, AngularVelocity = Vector3.Zero, Size = size,
		};
		static ParticleState Dead( Vector3 pos, float age = 9, float size = 7 ) => new()
		{
			Position = pos, Velocity = new Vector3( 11, 22, 33 ), Alive = 0, Age = age,
			Rotation = new Quaternion( 0.1f, 0.2f, 0.3f, 0.9f ), AngularVelocity = new Vector3( 4, 5, 6 ), Size = size,
		};
		static GpuParticleSpawn.SpawnRequest Req( float x, float y, float z, float vx = 0, float vy = 0, float vz = 0, float size = 1 ) => new()
		{
			Position = new Vector3( x, y, z ), Velocity = new Vector3( vx, vy, vz ),
			Rotation = Quaternion.Identity, AngularVelocity = Vector3.Zero, Size = size,
		};
		void Throws( Action f, string what )
		{
			try { f(); } catch ( ArgumentException ) { return; }
			throw new Exception( $"expected ArgumentException for {what}" );
		}

		registry.Add( "gpu-particle-spawn", "cpu spawn lowest-first: empty, partial, budget cap, order preserved", () =>
		{
			// Empty system, 5 requests, budget 5: requests land in slots 0..4 in order.
			var states = new ParticleState[6];
			for ( int i = 0; i < states.Length; i++ ) states[i] = Dead( new Vector3( i, 0, 0 ) );
			var reqs = new[] { Req( 10, 0, 0, 1, 0, 0 ), Req( 20, 0, 0, 2, 0, 0 ), Req( 30, 0, 0, 3, 0, 0 ), Req( 40, 0, 0 ), Req( 50, 0, 0 ) };
			Check( GpuParticleSpawn.SpawnCpu( states, reqs, 5 ) == 5, "empty spawn count" );
			for ( int i = 0; i < 5; i++ )
			{
				Check( states[i].Alive == 1 && states[i].Age == 0, $"slot {i} not fresh-live" );
				CloseVec( states[i].Position, reqs[i].Position, 0, $"slot {i} pos" );
				CloseVec( states[i].Velocity, reqs[i].Velocity, 0, $"slot {i} vel" );
			}
			Check( states[5].Alive == 0, "slot 5 should stay dead" );
			// Partial system: live at 0 and 2, budget 3 of 10 requests -> requests
			// 0,1,2 land in the lowest dead slots 1,3,4, in order.
			var states2 = new ParticleState[8];
			for ( int i = 0; i < states2.Length; i++ ) states2[i] = Dead( new Vector3( i, 0, 0 ) );
			states2[0] = P( new Vector3( 100, 0, 0 ), new Vector3( 1, 1, 1 ) );
			states2[2] = P( new Vector3( 200, 0, 0 ), new Vector3( 2, 2, 2 ) );
			states2[5] = P( new Vector3( 500, 0, 0 ), new Vector3( 5, 5, 5 ) );
			var live0 = states2[0]; var live2 = states2[2]; var live5 = states2[5];
			var reqs2 = new GpuParticleSpawn.SpawnRequest[10];
			for ( int i = 0; i < 10; i++ ) reqs2[i] = Req( 1000 + i, i, 0, 0, i, 0, 1 + i );
			Check( GpuParticleSpawn.SpawnCpu( states2, reqs2, 3 ) == 3, "partial spawn count" );
			SameState( states2[0], live0, "live slot 0" ); SameState( states2[2], live2, "live slot 2" ); SameState( states2[5], live5, "live slot 5" );
			Check( states2[1].Position == reqs2[0].Position && states2[1].Alive == 1, "request 0 must land in lowest dead slot 1" );
			Check( states2[3].Position == reqs2[1].Position && states2[3].Alive == 1, "request 1 must land in slot 3" );
			Check( states2[4].Position == reqs2[2].Position && states2[4].Alive == 1, "request 2 must land in slot 4" );
			Check( states2[6].Alive == 0 && states2[7].Alive == 0, "slots past budget must stay dead" );
		} );

		registry.Add( "gpu-particle-spawn", "cpu spawn never overwrites live, full system and zero budget spawn nothing", () =>
		{
			var states = new ParticleState[3];
			for ( int i = 0; i < 3; i++ ) states[i] = P( new Vector3( i * 10, 0, 0 ), new Vector3( i, i, i ) );
			var before = (ParticleState[])states.Clone();
			var reqs = new[] { Req( 99, 99, 99 ) };
			Check( GpuParticleSpawn.SpawnCpu( states, reqs, 10 ) == 0, "full system must spawn 0" );
			for ( int i = 0; i < 3; i++ ) SameState( states[i], before[i], $"full slot {i}" );
			var states2 = new ParticleState[2];
			for ( int i = 0; i < 2; i++ ) states2[i] = Dead( new Vector3( i, 0, 0 ) );
			Check( GpuParticleSpawn.SpawnCpu( states2, reqs, 0 ) == 0, "zero budget must spawn 0" );
			Check( states2[0].Alive == 0 && states2[1].Alive == 0, "zero-budget slots stay dead" );
			Check( GpuParticleSpawn.SpawnCpu( states2, Array.Empty<GpuParticleSpawn.SpawnRequest>(), 5 ) == 0, "zero requests must spawn 0" );
		} );

		registry.Add( "gpu-particle-spawn", "cpu kill expires exactly aged-out slots, boundary inclusive, rest identical", () =>
		{
			var states = new[]
			{
				P( new Vector3( 1, 0, 0 ), new Vector3( 1, 2, 3 ), age: 5 ),   // age == lifetime: dies (>=)
				P( new Vector3( 2, 0, 0 ), new Vector3( 4, 5, 6 ), age: 4.9f ), // survives
				P( new Vector3( 3, 0, 0 ), new Vector3( 7, 8, 9 ), age: 99 ),  // dies
				Dead( new Vector3( 4, 0, 0 ) ),                                // already dead: untouched
				P( new Vector3( 5, 0, 0 ), new Vector3( 0, 0, 1 ), age: 0 ),   // survives
			};
			var lifetimes = new[] { 5f, 5f, 2f, 1f, 5f };
			var before = (ParticleState[])states.Clone();
			Check( GpuParticleSpawn.KillCpu( states, lifetimes ) == 2, "kill count" );
			Check( states[0].Alive == 0 && states[0].Velocity == Vector3.Zero, "boundary slot must die with zeroed velocity" );
			Check( states[0].Position == before[0].Position && states[0].Age == before[0].Age && states[0].Size == before[0].Size, "killed slot keeps pos/age/size" );
			Check( states[2].Alive == 0 && states[2].Velocity == Vector3.Zero, "aged-out slot must die" );
			SameState( states[1], before[1], "unexpired slot 1" );
			SameState( states[3], before[3], "already-dead slot 3" );
			SameState( states[4], before[4], "unexpired slot 4" );
		} );

		registry.Add( "gpu-particle-spawn", "cpu and dispatch validation fails loudly", () =>
		{
			var states = new ParticleState[1];
			states[0] = Dead( Vector3.Zero );
			Throws( () => GpuParticleSpawn.SpawnCpu( states, new[] { Req( 1, 0, 0 ) }, -1 ), "negative budget" );
			Throws( () => GpuParticleSpawn.SpawnCpu( states, new[] { new GpuParticleSpawn.SpawnRequest { Position = new Vector3( float.NaN, 0, 0 ), Rotation = Quaternion.Identity, Size = 1 } }, 1 ), "NaN position" );
			Throws( () => GpuParticleSpawn.SpawnCpu( states, new[] { Req( 1, 0, 0, size: -1 ) }, 1 ), "negative size" );
			Throws( () => GpuParticleSpawn.KillCpu( states, new[] { 1f, 2f } ), "lifetime count mismatch" );
			Throws( () => GpuParticleSpawn.KillCpu( states, new[] { 0f } ), "zero lifetime" );
			Throws( () => GpuParticleSpawn.KillCpu( states, new[] { float.NaN } ), "NaN lifetime" );
			Throws( () => GpuParticleSpawn.ValidateLifetime( -3 ), "negative lifetime" );
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleSpawn.CreateProgram( gl );
			uint spawned = GpuParticleSpawn.CreateSpawnedBuffer( gl );
			uint buf = GpuParticleSystem.UploadStates( gl, new[] { P( Vector3.Zero, Vector3.Zero ) } );
			uint reqBuf = GpuParticleSpawn.UploadRequests( gl, new[] { Req( 1, 0, 0 ) } );
			Throws( () => GpuParticleSpawn.DispatchSpawn( gl, prog, buf, reqBuf, spawned, -1, 1, 1 ), "dispatch negative count" );
			Throws( () => GpuParticleSpawn.DispatchSpawn( gl, prog, buf, reqBuf, spawned, 1, -1, 1 ), "dispatch negative budget" );
			Throws( () => GpuParticleSpawn.DispatchSpawn( gl, prog, buf, reqBuf, spawned, 1, 1, -1 ), "dispatch negative request count" );
			Throws( () => GpuParticleSpawn.DispatchSpawn( gl, prog, 0, reqBuf, spawned, 1, 1, 1 ), "dispatch zero state buffer" );
			Throws( () => GpuParticleSpawn.DispatchSpawn( gl, prog, buf, 0, spawned, 1, 1, 1 ), "dispatch zero request buffer" );
			Throws( () => GpuParticleSpawn.DispatchSpawn( gl, prog, buf, reqBuf, 0, 1, 1, 1 ), "dispatch zero spawned buffer" );
			Throws( () => GpuParticleSpawn.DispatchKill( gl, prog, buf, reqBuf, -1 ), "kill negative count" );
			Throws( () => GpuParticleSpawn.DispatchKill( gl, prog, 0, reqBuf, 1 ), "kill zero state buffer" );
			Throws( () => GpuParticleSpawn.UploadLifetimes( gl, new[] { 1f, 0f } ), "upload zero lifetime" );
			Throws( () => GpuParticleSpawn.UploadRequests( gl, new[] { Req( 1, 0, 0, size: float.PositiveInfinity ) } ), "upload infinite size" );
			GpuParticleSpawn.Dispose( gl, prog, buf, reqBuf, spawned );
			Check( gl.GetError() == GLEnum.NoError, "GL error after validation test" );
		} );

		registry.Add( "gpu-particle-spawn", "gpu spawn exactly N into an empty system", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleSpawn.CreateProgram( gl );
			const int n = 10;
			var states = new ParticleState[n];
			for ( int i = 0; i < n; i++ ) states[i] = Dead( new Vector3( i, 0, 0 ) );
			var reqs = new GpuParticleSpawn.SpawnRequest[n];
			for ( int i = 0; i < n; i++ ) reqs[i] = new GpuParticleSpawn.SpawnRequest
			{
				Position = new Vector3( i * 10, i, -i ), Velocity = new Vector3( i, -i, 2 * i ),
				Rotation = new Quaternion( 0, 0, 0, 1 ), AngularVelocity = new Vector3( 0.1f * i, 0, 0 ), Size = 1 + 0.5f * i,
			};
			uint buf = GpuParticleSystem.UploadStates( gl, states );
			uint reqBuf = GpuParticleSpawn.UploadRequests( gl, reqs );
			uint spawnedBuf = GpuParticleSpawn.CreateSpawnedBuffer( gl );
			int spawned = GpuParticleSpawn.DispatchSpawn( gl, prog, buf, reqBuf, spawnedBuf, n, n, n );
			var got = GpuParticleSystem.DownloadStates( gl, buf, n );
			Console.WriteLine( $"gpu-spawn empty: spawned={spawned} slot0pos={got[0].Position} slot9pos={got[9].Position} slot9size={got[9].Size}" );
			Check( spawned == n, $"spawned {spawned} != {n}" );
			Check( GpuParticleSpawn.DownloadSpawned( gl, spawnedBuf ) == n, "spawned counter mismatch" );
			for ( int i = 0; i < n; i++ )
			{
				Check( got[i].Alive == 1 && got[i].Age == 0, $"slot {i} not fresh-live" );
				Check( got[i].Position == reqs[i].Position && got[i].Velocity == reqs[i].Velocity
					&& got[i].Rotation == reqs[i].Rotation && got[i].AngularVelocity == reqs[i].AngularVelocity && got[i].Size == reqs[i].Size,
					$"slot {i} fields differ from request" );
			}
			GpuParticleSpawn.Dispose( gl, prog, buf, reqBuf, spawnedBuf );
			Check( gl.GetError() == GLEnum.NoError, "GL error after gpu spawn empty test" );
		} );

		registry.Add( "gpu-particle-spawn", "gpu spawn respects budget: 3 of 10 into lowest slots, partial system", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleSpawn.CreateProgram( gl );
			var states = new ParticleState[8];
			for ( int i = 0; i < 8; i++ ) states[i] = Dead( new Vector3( i, 0, 0 ) );
			states[0] = P( new Vector3( 100, 0, 0 ), new Vector3( 1, 1, 1 ) );
			states[2] = P( new Vector3( 200, 0, 0 ), new Vector3( 2, 2, 2 ) );
			states[5] = P( new Vector3( 500, 0, 0 ), new Vector3( 5, 5, 5 ) );
			var live0 = states[0]; var live2 = states[2]; var live5 = states[5];
			var reqs = new GpuParticleSpawn.SpawnRequest[10];
			for ( int i = 0; i < 10; i++ ) reqs[i] = Req( 1000 + i, i, 0, 0, i, 0, 1 + i );
			uint buf = GpuParticleSystem.UploadStates( gl, states );
			uint reqBuf = GpuParticleSpawn.UploadRequests( gl, reqs );
			uint spawnedBuf = GpuParticleSpawn.CreateSpawnedBuffer( gl );
			int spawned = GpuParticleSpawn.DispatchSpawn( gl, prog, buf, reqBuf, spawnedBuf, 8, 3, 10 );
			var got = GpuParticleSystem.DownloadStates( gl, buf, 8 );
			Console.WriteLine( $"gpu-spawn budget: spawned={spawned} slot1pos={got[1].Position} slot3pos={got[3].Position} slot4pos={got[4].Position}" );
			Check( spawned == 3, $"budget spawn must be exactly 3, got {spawned}" );
			SameState( got[0], live0, "live slot 0" ); SameState( got[2], live2, "live slot 2" ); SameState( got[5], live5, "live slot 5" );
			Check( got[1].Position == reqs[0].Position && got[1].Alive == 1, "request 0 must land in lowest dead slot 1" );
			Check( got[3].Position == reqs[1].Position && got[3].Alive == 1, "request 1 must land in slot 3" );
			Check( got[4].Position == reqs[2].Position && got[4].Alive == 1, "request 2 must land in slot 4" );
			Check( got[6].Alive == 0 && got[7].Alive == 0, "slots past budget must stay dead" );
			GpuParticleSpawn.Dispose( gl, prog, buf, reqBuf, spawnedBuf );
			Check( gl.GetError() == GLEnum.NoError, "GL error after gpu budget test" );
		} );

		registry.Add( "gpu-particle-spawn", "gpu spawn never overwrites live slots", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleSpawn.CreateProgram( gl );
			var states = new[]
			{
				P( new Vector3( 1, 2, 3 ), new Vector3( 4, 5, 6 ), age: 2.5f, size: 3 ),
				Dead( new Vector3( 9, 9, 9 ) ),
				P( new Vector3( -1, -2, -3 ), new Vector3( -4, -5, -6 ), age: 0.5f, size: 0.5f ),
			};
			var before0 = states[0]; var before2 = states[2];
			var reqs = new[] { Req( 77, 77, 77, 8, 8, 8, 9 ), Req( 88, 88, 88 ) };
			uint buf = GpuParticleSystem.UploadStates( gl, states );
			uint reqBuf = GpuParticleSpawn.UploadRequests( gl, reqs );
			uint spawnedBuf = GpuParticleSpawn.CreateSpawnedBuffer( gl );
			int spawned = GpuParticleSpawn.DispatchSpawn( gl, prog, buf, reqBuf, spawnedBuf, 3, 10, 2 );
			var got = GpuParticleSystem.DownloadStates( gl, buf, 3 );
			Check( spawned == 1, $"only the one dead slot may fill, got {spawned}" );
			SameState( got[0], before0, "live slot 0 overwritten" );
			SameState( got[2], before2, "live slot 2 overwritten" );
			Check( got[1].Position == reqs[0].Position && got[1].Alive == 1, "dead slot must take request 0" );
			GpuParticleSpawn.Dispose( gl, prog, buf, reqBuf, spawnedBuf );
			Check( gl.GetError() == GLEnum.NoError, "GL error after gpu no-overwrite test" );
		} );

		registry.Add( "gpu-particle-spawn", "gpu kill expires exactly aged-out slots, rest byte-identical", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleSpawn.CreateProgram( gl );
			var states = new[]
			{
				P( new Vector3( 1, 0, 0 ), new Vector3( 1, 2, 3 ), age: 5 ),
				P( new Vector3( 2, 0, 0 ), new Vector3( 4, 5, 6 ), age: 4.9f ),
				P( new Vector3( 3, 0, 0 ), new Vector3( 7, 8, 9 ), age: 99 ),
				Dead( new Vector3( 4, 0, 0 ) ),
				P( new Vector3( 5, 0, 0 ), new Vector3( 0, 0, 1 ), age: 0 ),
			};
			var lifetimes = new[] { 5f, 5f, 2f, 1f, 5f };
			var before = (ParticleState[])states.Clone();
			uint buf = GpuParticleSystem.UploadStates( gl, states );
			uint lifeBuf = GpuParticleSpawn.UploadLifetimes( gl, lifetimes );
			GpuParticleSpawn.DispatchKill( gl, prog, buf, lifeBuf, states.Length );
			var got = GpuParticleSystem.DownloadStates( gl, buf, states.Length );
			int dead = 0; foreach ( var s in got ) if ( s.Alive == 0 ) dead++;
			Console.WriteLine( $"gpu-kill: dead={dead} slot0alive={got[0].Alive} slot0vel={got[0].Velocity}" );
			Check( got[0].Alive == 0 && got[0].Velocity == Vector3.Zero, "boundary slot must die" );
			Check( got[0].Position == before[0].Position && got[0].Age == before[0].Age, "killed slot keeps pos/age" );
			Check( got[2].Alive == 0 && got[2].Velocity == Vector3.Zero, "aged-out slot must die" );
			SameState( got[1], before[1], "unexpired slot 1" );
			SameState( got[3], before[3], "already-dead slot 3" );
			SameState( got[4], before[4], "unexpired slot 4" );
			GpuParticleSpawn.Dispose( gl, prog, buf, lifeBuf );
			Check( gl.GetError() == GLEnum.NoError, "GL error after gpu kill test" );
		} );

		registry.Add( "gpu-particle-spawn", "gpu spawn then kill round trip", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleSpawn.CreateProgram( gl );
			const int n = 5;
			var states = new ParticleState[n];
			for ( int i = 0; i < n; i++ ) states[i] = Dead( new Vector3( i, 0, 0 ) );
			var reqs = new GpuParticleSpawn.SpawnRequest[n];
			for ( int i = 0; i < n; i++ ) reqs[i] = Req( i * 10, i, 0, i, 0, 0, 1 + i );
			uint buf = GpuParticleSystem.UploadStates( gl, states );
			uint reqBuf = GpuParticleSpawn.UploadRequests( gl, reqs );
			uint spawnedBuf = GpuParticleSpawn.CreateSpawnedBuffer( gl );
			int spawned = GpuParticleSpawn.DispatchSpawn( gl, prog, buf, reqBuf, spawnedBuf, n, n, n );
			Check( spawned == n, "round-trip spawn count" );
			// Simulate integration aging: slots 0 and 3 age past lifetime 5, the rest stay young.
			var aged = GpuParticleSystem.DownloadStates( gl, buf, n );
			aged[0].Age = 10; aged[3].Age = 6; aged[1].Age = 0.5f; aged[2].Age = 1; aged[4].Age = 4.9f;
			uint buf2 = GpuParticleSystem.UploadStates( gl, aged );
			var lifetimes = new[] { 5f, 5f, 5f, 5f, 5f };
			uint lifeBuf = GpuParticleSpawn.UploadLifetimes( gl, lifetimes );
			GpuParticleSpawn.DispatchKill( gl, prog, buf2, lifeBuf, n );
			var got = GpuParticleSystem.DownloadStates( gl, buf2, n );
			var want = (ParticleState[])aged.Clone();
			Check( GpuParticleSpawn.KillCpu( want, lifetimes ) == 2, "oracle kill count" );
			for ( int i = 0; i < n; i++ ) SameState( got[i], want[i], $"round-trip slot {i}" );
			Console.WriteLine( $"gpu-spawn-kill round trip: spawned={spawned} killed=2 aliveAfter={got.Count( s => s.Alive != 0 )}" );
			GpuParticleSpawn.Dispose( gl, prog, buf, reqBuf, spawnedBuf, buf2, lifeBuf );
			Check( gl.GetError() == GLEnum.NoError, "GL error after round-trip test" );
		} );

		registry.Add( "gpu-particle-spawn", "gpu count zero and budget zero dispatch nothing cleanly", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleSpawn.CreateProgram( gl );
			var states = new[] { Dead( new Vector3( 3, 4, 5 ) ), P( new Vector3( 1, 0, 0 ), new Vector3( 9, 9, 9 ), age: 99 ) };
			var before = (ParticleState[])states.Clone();
			uint buf = GpuParticleSystem.UploadStates( gl, states );
			uint reqBuf = GpuParticleSpawn.UploadRequests( gl, new[] { Req( 1, 1, 1 ) } );
			uint spawnedBuf = GpuParticleSpawn.CreateSpawnedBuffer( gl );
			uint lifeBuf = GpuParticleSpawn.UploadLifetimes( gl, new[] { 1f, 1f } );
			Check( GpuParticleSpawn.DispatchSpawn( gl, prog, buf, reqBuf, spawnedBuf, 0, 5, 1 ) == 0, "count-0 spawn must return 0" );
			Check( GpuParticleSpawn.DispatchSpawn( gl, prog, buf, reqBuf, spawnedBuf, 2, 0, 1 ) == 0, "budget-0 spawn must return 0" );
			Check( GpuParticleSpawn.DispatchSpawn( gl, prog, buf, reqBuf, spawnedBuf, 2, 5, 0 ) == 0, "request-0 spawn must return 0" );
			GpuParticleSpawn.DispatchKill( gl, prog, buf, lifeBuf, 0 );
			var got = GpuParticleSystem.DownloadStates( gl, buf, 2 );
			SameState( got[0], before[0], "count-0 slot 0" );
			SameState( got[1], before[1], "count-0 slot 1 (aged live slot must survive a no-op kill)" );
			GpuParticleSpawn.Dispose( gl, prog, buf, reqBuf, spawnedBuf, lifeBuf );
			Check( gl.GetError() == GLEnum.NoError, "GL error after count-0 test" );
		} );

		registry.Add( "gpu-particle-spawn", "gpu ucount guard at 257 leaves sentinels untouched for spawn and kill", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleSpawn.CreateProgram( gl );
			const int count = 257; // 256 + 1: second workgroup is 255/256 guard-only
			const int total = 512;
			var states = new ParticleState[total];
			states[0] = P( new Vector3( 0, 0, 0 ), Vector3.Zero );
			for ( int i = 1; i < count; i++ ) states[i] = Dead( new Vector3( i, 0, 0 ) );
			// Sentinels past count: DEAD with distinctive contents for spawn
			// (a guard failure would spawn into them), LIVE and over-age for
			// kill (a guard failure would kill them).
			for ( int i = count; i < total; i++ )
				states[i] = i % 2 == 0 ? Dead( new Vector3( 777, 888, 999 ) ) : P( new Vector3( 777, 888, 999 ), new Vector3( -1, -2, -3 ), age: 999 );
			var sentinelBefore = new ParticleState[total - count];
			Array.Copy( states, count, sentinelBefore, 0, total - count );
			var reqs = new GpuParticleSpawn.SpawnRequest[300];
			for ( int i = 0; i < 300; i++ ) reqs[i] = Req( 5000 + i, 0, 0 );
			var lifetimes = new float[total];
			for ( int i = 0; i < total; i++ ) lifetimes[i] = 5f;
			uint buf = GpuParticleSystem.UploadStates( gl, states );
			uint reqBuf = GpuParticleSpawn.UploadRequests( gl, reqs );
			uint spawnedBuf = GpuParticleSpawn.CreateSpawnedBuffer( gl );
			uint lifeBuf = GpuParticleSpawn.UploadLifetimes( gl, lifetimes );
			int spawned = GpuParticleSpawn.DispatchSpawn( gl, prog, buf, reqBuf, spawnedBuf, count, 1000, 300 );
			GpuParticleSpawn.DispatchKill( gl, prog, buf, lifeBuf, count );
			var got = GpuParticleSystem.DownloadStates( gl, buf, total );
			int sentinelChanged = 0;
			for ( int i = count; i < total; i++ ) if ( got[i].Position != sentinelBefore[i - count].Position || got[i].Velocity != sentinelBefore[i - count].Velocity || got[i].Alive != sentinelBefore[i - count].Alive ) sentinelChanged++;
			Console.WriteLine( $"gpu-spawn guard: count={count} spawned={spawned} sentinelChanged={sentinelChanged}/{total - count}" );
			Check( spawned == 256, $"in-range dead slots are 256, spawned {spawned}" );
			Check( sentinelChanged == 0, $"{sentinelChanged} beyond-count slots changed — uCount guard failed" );
			GpuParticleSpawn.Dispose( gl, prog, buf, reqBuf, spawnedBuf, lifeBuf );
			Check( gl.GetError() == GLEnum.NoError, "GL error after guard test" );
		} );

		registry.Add( "gpu-particle-spawn", "gpu spawn list longer than capacity caps at free slots with counter proof", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleSpawn.CreateProgram( gl );
			var states = new ParticleState[4];
			for ( int i = 0; i < 4; i++ ) states[i] = Dead( new Vector3( i, 0, 0 ) );
			var reqs = new GpuParticleSpawn.SpawnRequest[10];
			for ( int i = 0; i < 10; i++ ) reqs[i] = Req( 100 + i, 0, 0 );
			uint buf = GpuParticleSystem.UploadStates( gl, states );
			uint reqBuf = GpuParticleSpawn.UploadRequests( gl, reqs );
			uint spawnedBuf = GpuParticleSpawn.CreateSpawnedBuffer( gl );
			int spawned = GpuParticleSpawn.DispatchSpawn( gl, prog, buf, reqBuf, spawnedBuf, 4, 100, 10 );
			var got = GpuParticleSystem.DownloadStates( gl, buf, 4 );
			Console.WriteLine( $"gpu-spawn over-capacity: spawned={spawned} counter={GpuParticleSpawn.DownloadSpawned( gl, spawnedBuf )}" );
			Check( spawned == 4, $"over-capacity spawn must cap at 4, got {spawned}" );
			Check( GpuParticleSpawn.DownloadSpawned( gl, spawnedBuf ) == 4, "over-capacity counter must read 4" );
			for ( int i = 0; i < 4; i++ ) Check( got[i].Position == reqs[i].Position && got[i].Alive == 1, $"slot {i} must hold request {i}" );
			GpuParticleSpawn.Dispose( gl, prog, buf, reqBuf, spawnedBuf );
			Check( gl.GetError() == GLEnum.NoError, "GL error after over-capacity test" );
		} );

		registry.Add( "gpu-particle-spawn", "gpu spawn and kill match cpu oracle exactly on a mixed system", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleSpawn.CreateProgram( gl );
			var states = new ParticleState[12];
			for ( int i = 0; i < 12; i++ ) states[i] = Dead( new Vector3( i, i, i ) );
			states[1] = P( new Vector3( 10, 0, 0 ), new Vector3( 1, 0, 0 ), age: 1 );
			states[4] = P( new Vector3( 40, 0, 0 ), new Vector3( 0, 1, 0 ), age: 7, size: 2 );
			states[9] = P( new Vector3( 90, 0, 0 ), new Vector3( 0, 0, 1 ), age: 3, size: 0.5f );
			var reqs = new GpuParticleSpawn.SpawnRequest[6];
			for ( int i = 0; i < 6; i++ ) reqs[i] = new GpuParticleSpawn.SpawnRequest
			{
				Position = new Vector3( 200 + i, -i, i ), Velocity = new Vector3( i, i * 2, -i ),
				Rotation = new Quaternion( 0, 0, 0, 1 ), AngularVelocity = new Vector3( 0, 0, i ), Size = 1 + i,
			};
			var lifetimes = new[] { 5f, 5f, 5f, 5f, 5f, 5f, 5f, 5f, 5f, 5f, 5f, 5f };
			// Oracle: spawn budget 4, then age the spawned slots by hand (the
			// integrate step's job) and kill: slot 4 (age 7) and any spawned
			// slot aged past 5 must die.
			var want = (ParticleState[])states.Clone();
			Check( GpuParticleSpawn.SpawnCpu( want, reqs, 4 ) == 4, "oracle spawn count" );
			want[0].Age = 6; want[2].Age = 5; want[3].Age = 4.9f;
			Check( GpuParticleSpawn.KillCpu( want, lifetimes ) == 3, "oracle kill count (slots 0, 2, 4)" );
			uint buf = GpuParticleSystem.UploadStates( gl, states );
			uint reqBuf = GpuParticleSpawn.UploadRequests( gl, reqs );
			uint spawnedBuf = GpuParticleSpawn.CreateSpawnedBuffer( gl );
			uint lifeBuf = GpuParticleSpawn.UploadLifetimes( gl, lifetimes );
			int spawned = GpuParticleSpawn.DispatchSpawn( gl, prog, buf, reqBuf, spawnedBuf, 12, 4, 6 );
			var mid = GpuParticleSystem.DownloadStates( gl, buf, 12 );
			mid[0].Age = 6; mid[2].Age = 5; mid[3].Age = 4.9f;
			uint buf2 = GpuParticleSystem.UploadStates( gl, mid );
			GpuParticleSpawn.DispatchKill( gl, prog, buf2, lifeBuf, 12 );
			var got = GpuParticleSystem.DownloadStates( gl, buf2, 12 );
			Console.WriteLine( $"gpu-spawn oracle: spawned={spawned} aliveAfter={got.Count( s => s.Alive != 0 )}" );
			Check( spawned == 4, "gpu spawn count must match oracle" );
			for ( int i = 0; i < 12; i++ ) SameState( got[i], want[i], $"oracle slot {i}" );
			GpuParticleSpawn.Dispose( gl, prog, buf, reqBuf, spawnedBuf, lifeBuf, buf2 );
			Check( gl.GetError() == GLEnum.NoError, "GL error after oracle test" );
		} );
	}
}
