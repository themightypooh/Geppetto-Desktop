using System.Numerics;
using Marionette.Desktop;
using Silk.NET.OpenGL;

internal static class GpuParticleSystemTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }

		// CPU oracle for ONE Integrate substep, written from the documented
		// algorithm (gravity, curl turbulence, vortex, semi-implicit drag
		// toward wind, ground collision, age, quaternion spin) using the
		// public ParticleMath primitives. It shares no code with the GLSL,
		// so a GLSL transcription slip (wrong sign, wrong uniform, wrong
		// noise port) shows up as a mismatch here. Mesh collision is not
		// oracled: the shader deliberately does not mirror it.
		static ParticleState OracleStep( ParticleState ps, GpuParticleSystem.GpuSimParams sim, float h, float time )
		{
			Vector3 pos = ps.Position, vel = ps.Velocity;
			Vector3 acc = sim.Gravity;
			if ( sim.TurbStrength > 0 )
				acc += ParticleMath.Curl( pos * sim.TurbFreq + new Vector3( 0, 0, time * sim.TurbSpeed ), sim.Seed ) * sim.TurbStrength;
			if ( sim.VortexStrength != 0 )
			{
				var r = pos - sim.VortexCenter;
				var rp = r - sim.VortexAxis * Vector3.Dot( r, sim.VortexAxis );
				float d2 = Vector3.Dot( rp, rp );
				acc += Vector3.Cross( sim.VortexAxis, rp ) * (sim.VortexStrength / (d2 + sim.VortexRadius2));
			}
			vel = (vel + (acc + sim.Wind * sim.Drag) * h) / (1f + sim.Drag * h);
			Vector3 next = pos + vel * h;
			if ( sim.GroundEnabled )
			{
				float d = Vector3.Dot( sim.GroundNormal, next ) - sim.GroundOffset - sim.Radius;
				if ( d < 0 )
				{
					next -= sim.GroundNormal * d;
					vel = OracleRespond( vel, sim.GroundNormal, sim.Restitution, sim.Friction );
				}
			}
			ps.Position = next;
			ps.Velocity = vel;
			ps.Age += h;
			var w = ps.AngularVelocity;
			if ( w.X != 0 || w.Y != 0 || w.Z != 0 )
			{
				var q = ps.Rotation;
				var dq = ParticleMath.Mul( new Quaternion( w.X, w.Y, w.Z, 0 ), q );
				q = new Quaternion( q.X + dq.X * (0.5f * h), q.Y + dq.Y * (0.5f * h), q.Z + dq.Z * (0.5f * h), q.W + dq.W * (0.5f * h) );
				float len = MathF.Sqrt( q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W );
				ps.Rotation = new Quaternion( q.X / len, q.Y / len, q.Z / len, q.W / len );
			}
			return ps;
		}

		static Vector3 OracleRespond( Vector3 vel, Vector3 n, float restitution, float friction )
		{
			float vn = Vector3.Dot( vel, n );
			if ( vn >= 0 ) return vel;
			var vt = vel - n * vn;
			return vt * (1f - friction) - n * (vn * restitution);
		}

		// Max component deviation between two state arrays, split by field.
		static void MaxDevs( ParticleState[] a, ParticleState[] b, out float pos, out float vel, out float age, out float rot )
		{
			pos = vel = age = rot = 0;
			for ( int i = 0; i < a.Length; i++ )
			{
				pos = MathF.Max( pos, MathF.Max( MathF.Abs( a[i].Position.X - b[i].Position.X ), MathF.Max( MathF.Abs( a[i].Position.Y - b[i].Position.Y ), MathF.Abs( a[i].Position.Z - b[i].Position.Z ) ) ) );
				vel = MathF.Max( vel, MathF.Max( MathF.Abs( a[i].Velocity.X - b[i].Velocity.X ), MathF.Max( MathF.Abs( a[i].Velocity.Y - b[i].Velocity.Y ), MathF.Abs( a[i].Velocity.Z - b[i].Velocity.Z ) ) ) );
				age = MathF.Max( age, MathF.Abs( a[i].Age - b[i].Age ) );
				rot = MathF.Max( rot, MathF.Max( MathF.Abs( a[i].Rotation.X - b[i].Rotation.X ), MathF.Max( MathF.Abs( a[i].Rotation.Y - b[i].Rotation.Y ), MathF.Max( MathF.Abs( a[i].Rotation.Z - b[i].Rotation.Z ), MathF.Abs( a[i].Rotation.W - b[i].Rotation.W ) ) ) ) );
			}
		}

		registry.Add( "gpu-particle", "upload/download round-trip preserves full particle state", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleSystem.CreateProgram( gl );
			var states = new[]
			{
				new ParticleState { Position = new Vector3( 1, 2, 3 ), Age = 0.5f, Velocity = new Vector3( -4, 5, -6 ), Alive = 1, Rotation = Quaternion.Identity, AngularVelocity = new Vector3( 1, 2, 3 ), Size = 2.5f },
				new ParticleState { Position = new Vector3( -7, 8, -9 ), Age = 0, Velocity = Vector3.Zero, Alive = 0, Rotation = new Quaternion( 0.3f, 0.4f, 0.5f, 0.6f ), AngularVelocity = Vector3.Zero, Size = 0.25f },
				new ParticleState { Position = new Vector3( 100, -200, 300 ), Age = 12.25f, Velocity = new Vector3( 0.001f, -0.002f, 0.003f ), Alive = 1, Rotation = Quaternion.CreateFromAxisAngle( Vector3.UnitZ, 1.1f ), AngularVelocity = new Vector3( -2, 0, 4 ), Size = 7 },
			};
			uint buf = GpuParticleSystem.UploadStates( gl, states );
			var raw = GpuParticleSystem.Download( gl, buf, states.Length );
			Check( raw.Length == states.Length * GpuParticleSystem.StrideFloats, $"raw download length {raw.Length} != {states.Length * GpuParticleSystem.StrideFloats}" );
			Check( raw[GpuParticleSystem.AgeOffset] == 0.5f && raw[GpuParticleSystem.AliveOffset] == 1 && raw[GpuParticleSystem.SizeOffset + GpuParticleSystem.StrideFloats] == 0.25f,
				"SSBO field offsets wrong (age/alive/size landed in the wrong slots)" );
			var back = GpuParticleSystem.DownloadStates( gl, buf, states.Length );
			for ( int i = 0; i < states.Length; i++ )
			{
				Check( back[i].Position == states[i].Position && back[i].Age == states[i].Age && back[i].Velocity == states[i].Velocity && back[i].Alive == states[i].Alive,
					$"state {i} pos/age/vel/alive changed by round-trip" );
				Check( back[i].Rotation == states[i].Rotation && back[i].AngularVelocity == states[i].AngularVelocity && back[i].Size == states[i].Size,
					$"state {i} rotation/angvel/size changed by round-trip" );
			}
			GpuParticleSystem.Dispose( gl, buf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after state round-trip" );
		} );

		registry.Add( "gpu-particle", "gpu integrate matches cpu oracle: gravity, drag, wind", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleSystem.CreateProgram( gl );
			var settings = new ParticleSystemSettings { Gravity = new Vector3( 0, 0, -386.0886f ), Wind = new Vector3( 10, -5, 2 ), Drag = 0.4f, Seed = 7 };
			var sim = GpuParticleSystem.GpuSimParams.FromSettings( settings );
			var states = new ParticleState[8];
			for ( int i = 0; i < states.Length; i++ )
				states[i] = new ParticleState { Position = new Vector3( i * 3 - 10, i - 4, 100 + i * 7 ), Velocity = new Vector3( 10 - i, i * 2, 50 - i * 5 ), Alive = 1, Rotation = Quaternion.Identity, Size = 1 };
			var oracle = (ParticleState[])states.Clone();
			uint buf = GpuParticleSystem.UploadStates( gl, states );
			float dt = 1f / 60f, maxPos = 0, maxVel = 0, maxAge = 0;
			for ( int step = 0; step < 60; step++ )
			{
				GpuParticleSystem.Dispatch( gl, prog, buf, states.Length, sim, dt, step * dt );
				var gpu = GpuParticleSystem.DownloadStates( gl, buf, states.Length );
				for ( int i = 0; i < oracle.Length; i++ ) oracle[i] = OracleStep( oracle[i], sim, dt, step * dt );
				MaxDevs( gpu, oracle, out float p, out float v, out float a, out _ );
				maxPos = MathF.Max( maxPos, p ); maxVel = MathF.Max( maxVel, v ); maxAge = MathF.Max( maxAge, a );
			}
			Console.WriteLine( $"gpu-vs-oracle gravity/drag/wind over 60 steps: maxPosDev={maxPos:G6} maxVelDev={maxVel:G6} maxAgeDev={maxAge:G6}" );
			// Absolute tolerances: velocities here are O(400), so 1e-3 is ~2.5e-6 relative. Measured on RTX 5060 Ti: pos 6.9e-05, vel 1.8e-04 (float32 step-to-step rounding, not a logic error).
			Check( maxPos < 1e-3f, $"position deviation too large: {maxPos:G6}" );
			Check( maxVel < 1e-3f, $"velocity deviation too large: {maxVel:G6}" );
			Check( maxAge < 1e-6f, $"age deviation too large: {maxAge:G6}" );
			GpuParticleSystem.Dispose( gl, buf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after gravity parity run" );
		} );

		registry.Add( "gpu-particle", "gpu integrate matches cpu oracle: turbulence, vortex, ground, spin, substeps", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleSystem.CreateProgram( gl );
			var settings = new ParticleSystemSettings
			{
				Gravity = new Vector3( 0, 0, -386.0886f ), Drag = 0.2f, Seed = 7,
				TurbulenceStrength = 30f, TurbulenceFrequency = 0.2f, TurbulenceSpeed = 0.7f,
				VortexStrength = 500f, VortexCenter = new Vector3( 0, 0, 50 ), VortexAxis = new Vector3( 0, 0, 1 ), VortexRadius = 5f,
				GroundEnabled = true, GroundNormal = new Vector3( 0, 0, 1 ), GroundOffset = 0, CollisionRadius = 0.5f, Restitution = 0.35f, Friction = 0.2f,
				Substeps = 2,
			};
			var sim = GpuParticleSystem.GpuSimParams.FromSettings( settings );
			var states = new ParticleState[16];
			for ( int i = 0; i < states.Length; i++ )
				states[i] = new ParticleState
				{
					Position = new Vector3( (i % 4) * 10 - 15, (i / 4) * 8 - 12, 20 + i ),
					Velocity = new Vector3( i - 8, 5 - i, -20 - i * 3 ),
					Alive = 1,
					Rotation = i % 3 == 0 ? Quaternion.Identity : Quaternion.CreateFromAxisAngle( Vector3.Normalize( new Vector3( 1, i + 1, 2 ) ), 0.3f * i ),
					AngularVelocity = i % 2 == 0 ? Vector3.Zero : new Vector3( 1, 2, 3 ),
					Size = 1 + i * 0.1f,
				};
			var oracle = (ParticleState[])states.Clone();
			uint buf = GpuParticleSystem.UploadStates( gl, states );
			float dt = 1f / 60f, h = dt / 2f, maxPos = 0, maxVel = 0, maxRot = 0;
			bool anyBounced = false;
			for ( int step = 0; step < 60; step++ )
			{
				for ( int k = 0; k < 2; k++ )
				{
					float t = step * dt + k * h;
					GpuParticleSystem.Dispatch( gl, prog, buf, states.Length, sim, h, t );
					var gpu = GpuParticleSystem.DownloadStates( gl, buf, states.Length );
					for ( int i = 0; i < oracle.Length; i++ ) oracle[i] = OracleStep( oracle[i], sim, h, t );
					MaxDevs( gpu, oracle, out float p, out float v, out _, out float r );
					maxPos = MathF.Max( maxPos, p ); maxVel = MathF.Max( maxVel, v ); maxRot = MathF.Max( maxRot, r );
					for ( int i = 0; i < gpu.Length; i++ )
					{
						Check( gpu[i].Position.Z >= 0.5f - 1e-4f, $"particle {i} below ground after bounce: z={gpu[i].Position.Z}" );
						if ( gpu[i].Velocity.Z > 0 && oracle[i].Position.Z <= 0.5f + 1e-4f ) anyBounced = true;
					}
				}
			}
			Console.WriteLine( $"gpu-vs-oracle turbulence/vortex/ground/spin over 60 steps x2 substeps: maxPosDev={maxPos:G6} maxVelDev={maxVel:G6} maxRotDev={maxRot:G6} anyBounced={anyBounced}" );
			Check( maxPos < 5e-3f, $"position deviation too large: {maxPos:G6}" );
			Check( maxVel < 5e-3f, $"velocity deviation too large: {maxVel:G6}" );
			Check( maxRot < 5e-3f, $"rotation deviation too large: {maxRot:G6}" );
			Check( anyBounced, "no particle bounced off the ground in 60 steps; ground path untested" );
			GpuParticleSystem.Dispose( gl, buf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after turbulence parity run" );
		} );

		registry.Add( "gpu-particle", "dead slots are untouched by dispatch", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleSystem.CreateProgram( gl );
			var sim = GpuParticleSystem.GpuSimParams.FromSettings( new ParticleSystemSettings { Gravity = new Vector3( 0, 0, -386.0886f ) } );
			var dead = new ParticleState { Position = new Vector3( 1e6f, -1e6f, 3e5f ), Age = 5, Velocity = new Vector3( 999, -999, 999 ), Alive = 0, Rotation = new Quaternion( 0.3f, 0.4f, 0.5f, 0.6f ), AngularVelocity = new Vector3( 7, 8, 9 ), Size = 42 };
			var live = new ParticleState { Position = new Vector3( 0, 0, 100 ), Velocity = Vector3.Zero, Alive = 1, Rotation = Quaternion.Identity, Size = 1 };
			uint buf = GpuParticleSystem.UploadStates( gl, new[] { dead, live } );
			GpuParticleSystem.Dispatch( gl, prog, buf, 2, sim, 1f / 60f, 0 );
			var back = GpuParticleSystem.DownloadStates( gl, buf, 2 );
			Check( back[0].Position == dead.Position && back[0].Velocity == dead.Velocity && back[0].Age == dead.Age && back[0].Rotation == dead.Rotation && back[0].AngularVelocity == dead.AngularVelocity && back[0].Size == dead.Size,
				"dead slot changed by dispatch" );
			Check( back[1].Position != live.Position, "live particle did not move under gravity" );
			GpuParticleSystem.Dispose( gl, buf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after dead-slot run" );
		} );

		registry.Add( "gpu-particle", "upload fills a valid full state", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleSystem.CreateProgram( gl );
			uint buf = GpuParticleSystem.Upload( gl, 64, 99 );
			var states = GpuParticleSystem.DownloadStates( gl, buf, 64 );
			for ( int i = 0; i < states.Length; i++ )
			{
				Check( states[i].Alive == 1, $"particle {i} not alive after upload" );
				Check( states[i].Rotation == Quaternion.Identity, $"particle {i} rotation not identity after upload" );
				Check( states[i].Size == 1 && states[i].Age == 0, $"particle {i} size/age wrong after upload" );
				Check( MathF.Abs( states[i].Position.X ) <= 1 && MathF.Abs( states[i].Position.Y ) <= 1 && MathF.Abs( states[i].Position.Z ) <= 1, $"particle {i} position outside [-1,1] after upload" );
			}
			GpuParticleSystem.Dispose( gl, buf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after upload check" );
		} );

		registry.Add( "gpu-particle", "zero particles does not crash", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleSystem.CreateProgram( gl );
			uint buf = GpuParticleSystem.Upload( gl, 0, 7 );
			var sim = GpuParticleSystem.GpuSimParams.FromSettings( new ParticleSystemSettings() );
			GpuParticleSystem.Dispatch( gl, prog, buf, 0, sim, 0.1f, 0 );
			var data = GpuParticleSystem.Download( gl, buf, 0 );
			Check( data.Length == 0, "zero-particle download should be empty" );
			var states = GpuParticleSystem.DownloadStates( gl, buf, 0 );
			Check( states.Length == 0, "zero-particle state download should be empty" );
			GpuParticleSystem.Dispose( gl, buf, prog );
		} );
	}
}
