using System.Numerics;
using Marionette.Desktop;
using Silk.NET.OpenGL;

internal static class GpuParticleCollidersTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Close( float a, float b, float eps, string what ) { if ( MathF.Abs( a - b ) > eps ) throw new Exception( $"{what}: got {a}, want {b} (eps {eps})" ); }
		void CloseVec( Vector3 a, Vector3 b, float eps, string what )
		{
			Close( a.X, b.X, eps, what + ".X" ); Close( a.Y, b.Y, eps, what + ".Y" ); Close( a.Z, b.Z, eps, what + ".Z" );
		}
		static ParticleState P( Vector3 pos, Vector3 vel, float alive = 1 ) => new() { Position = pos, Velocity = vel, Alive = alive, Rotation = Quaternion.Identity, Size = 1 };
		static void MaxDevs( ParticleState[] gpu, ParticleState[] cpu, out float pos, out float vel )
		{
			pos = vel = 0;
			for ( int i = 0; i < gpu.Length; i++ )
			{
				pos = MathF.Max( pos, MathF.Max( MathF.Abs( gpu[i].Position.X - cpu[i].Position.X ), MathF.Max( MathF.Abs( gpu[i].Position.Y - cpu[i].Position.Y ), MathF.Abs( gpu[i].Position.Z - cpu[i].Position.Z ) ) ) );
				vel = MathF.Max( vel, MathF.Max( MathF.Abs( gpu[i].Velocity.X - cpu[i].Velocity.X ), MathF.Max( MathF.Abs( gpu[i].Velocity.Y - cpu[i].Velocity.Y ), MathF.Abs( gpu[i].Velocity.Z - cpu[i].Velocity.Z ) ) ) );
			}
		}
		// CPU oracle for a whole array (sphere first, then box — the shader's order).
		static ParticleState[] Oracle( ParticleState[] src, GpuParticleColliders.SphereCollider? sphere, GpuParticleColliders.BoxCollider? box, float radius )
		{
			var dst = (ParticleState[])src.Clone();
			for ( int i = 0; i < dst.Length; i++ ) GpuParticleColliders.ApplyCpu( ref dst[i], sphere, box, radius );
			return dst;
		}
		const float R = 0.1f; // particle radius, as in the CPU CollisionRadius default scale
		var sphere = new GpuParticleColliders.SphereCollider { Center = Vector3.Zero, Radius = 1, Restitution = 0.5f, Friction = 0.2f };
		var box = new GpuParticleColliders.BoxCollider { Min = new Vector3( -1, -1, -1 ), Max = new Vector3( 1, 1, 1 ), Restitution = 0.5f, Friction = 0.2f };

		registry.Add( "gpu-particle-collider", "sphere cpu reference: bounce, friction, extremes, miss, centre, separating", () =>
		{
			// Head-on with tangential velocity: vt*(1-f) + reflected normal*restitution.
			var pos = new Vector3( 0, 0, 1.05f ); var vel = new Vector3( 4, 0, -10 );
			Check( GpuParticleColliders.CollideSphereCpu( ref pos, ref vel, sphere, R ), "head-on sphere hit missed" );
			CloseVec( pos, new Vector3( 0, 0, 1.1f ), 1e-6f, "sphere head-on pos" );
			CloseVec( vel, new Vector3( 3.2f, 0, 5 ), 1e-6f, "sphere head-on vel" );
			// Restitution 0 + friction 1 kills everything.
			var dead = new GpuParticleColliders.SphereCollider { Center = Vector3.Zero, Radius = 1, Restitution = 0, Friction = 1 };
			pos = new Vector3( 0, 0, 1.05f ); vel = new Vector3( 4, 3, -10 );
			Check( GpuParticleColliders.CollideSphereCpu( ref pos, ref vel, dead, R ), "dead sphere hit missed" );
			CloseVec( vel, Vector3.Zero, 1e-6f, "rest 0 / fric 1 vel" );
			// Miss: far particle untouched, returns false.
			pos = new Vector3( 0, 0, 3 ); vel = new Vector3( 1, 2, 3 );
			Check( !GpuParticleColliders.CollideSphereCpu( ref pos, ref vel, sphere, R ), "far sphere reported a hit" );
			CloseVec( pos, new Vector3( 0, 0, 3 ), 0, "sphere miss pos" );
			// Centre-coincident: fallback normal (0,0,1).
			pos = Vector3.Zero; vel = new Vector3( 1, 0, -1 );
			Check( GpuParticleColliders.CollideSphereCpu( ref pos, ref vel, sphere, R ), "centre sphere hit missed" );
			CloseVec( pos, new Vector3( 0, 0, 1.1f ), 1e-6f, "centre sphere pos" );
			CloseVec( vel, new Vector3( 0.8f, 0, 0.5f ), 1e-6f, "centre sphere vel" );
			// Separating velocity: position still pushed out, velocity unchanged (CPU Respond semantics).
			pos = new Vector3( 0, 0, 1.05f ); vel = new Vector3( 0, 0, 10 );
			Check( GpuParticleColliders.CollideSphereCpu( ref pos, ref vel, sphere, R ), "separating sphere hit missed" );
			CloseVec( pos, new Vector3( 0, 0, 1.1f ), 1e-6f, "separating sphere pos" );
			CloseVec( vel, new Vector3( 0, 0, 10 ), 0, "separating sphere vel" );
		} );

		registry.Add( "gpu-particle-collider", "box cpu reference: inside nearest face, outside face, corner, miss", () =>
		{
			var pos = new Vector3( 0.9f, 0.2f, 0.1f ); var vel = new Vector3( -5, 2, 0 );
			Check( GpuParticleColliders.CollideBoxCpu( ref pos, ref vel, box, R ), "inside box hit missed" );
			CloseVec( pos, new Vector3( 1.1f, 0.2f, 0.1f ), 1e-6f, "box inside pos" );
			CloseVec( vel, new Vector3( 2.5f, 1.6f, 0 ), 1e-6f, "box inside vel" );
			pos = new Vector3( 0, 0, 1.05f ); vel = new Vector3( 1, 0, -4 );
			Check( GpuParticleColliders.CollideBoxCpu( ref pos, ref vel, box, R ), "outside face hit missed" );
			CloseVec( pos, new Vector3( 0, 0, 1.1f ), 1e-6f, "box face pos" );
			CloseVec( vel, new Vector3( 0.8f, 0, 2 ), 1e-6f, "box face vel" );
			// Corner: closest point (1,1,0), diagonal normal (1,1,0)/sqrt(2).
			pos = new Vector3( 1.07f, 1.07f, 0 ); vel = new Vector3( -2, -2, 3 );
			Check( GpuParticleColliders.CollideBoxCpu( ref pos, ref vel, box, R ), "corner hit missed" );
			CloseVec( pos, new Vector3( 1.0707107f, 1.0707107f, 0 ), 1e-4f, "box corner pos" );
			// Tangential part is (0,0,3) (normal is the xy diagonal), scaled by (1 - 0.2) = 2.4; normal part reflects to (1,1,0).
			CloseVec( vel, new Vector3( 1, 1, 2.4f ), 1e-4f, "box corner vel" );
			pos = new Vector3( 0, 0, 3 ); vel = new Vector3( 1, 2, 3 );
			Check( !GpuParticleColliders.CollideBoxCpu( ref pos, ref vel, box, R ), "far box reported a hit" );
			CloseVec( pos, new Vector3( 0, 0, 3 ), 0, "box miss pos" );
		} );

		registry.Add( "gpu-particle-collider", "gpu sphere matches cpu reference within epsilon", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleColliders.CreateProgram( gl );
			var states = new[]
			{
				P( new Vector3( 0, 0, 1.05f ), new Vector3( 4, 0, -10 ) ),
				P( new Vector3( 0, 0, 3 ), new Vector3( 1, 2, 3 ) ),
				P( new Vector3( -0.5f, 0.5f, 0.5f ), new Vector3( -3, 1, -2 ) ),
				P( Vector3.Zero, new Vector3( 1, 0, -1 ) ),
				P( new Vector3( 0, 0, 1.05f ), new Vector3( 0, 0, 10 ) ),
				P( new Vector3( 0.3f, -0.9f, 0.2f ), new Vector3( 2, -4, 6 ) ),
				P( new Vector3( 0, 0, 0.5f ), new Vector3( 0, 0, -7 ), alive: 0 ), // dead: untouched
				P( new Vector3( 2, 0, 0 ), new Vector3( -8, 3, 0 ) ),
			};
			var want = Oracle( states, sphere, null, R );
			uint buf = GpuParticleSystem.UploadStates( gl, states );
			GpuParticleColliders.Dispatch( gl, prog, buf, states.Length, sphere, null, R );
			var got = GpuParticleSystem.DownloadStates( gl, buf, states.Length );
			MaxDevs( got, want, out float maxPos, out float maxVel );
			Console.WriteLine( $"gpu-collider sphere: maxPosDev={maxPos:G9} maxVelDev={maxVel:G9}" );
			Check( maxPos <= 1e-4f && maxVel <= 1e-4f, $"sphere gpu/cpu deviation too large: pos {maxPos}, vel {maxVel}" );
			Check( got[6].Position == states[6].Position && got[6].Velocity == states[6].Velocity, "dead slot changed by gpu sphere pass" );
			GpuParticleColliders.Dispose( gl, buf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after gpu sphere test" );
		} );

		registry.Add( "gpu-particle-collider", "gpu box matches cpu reference within epsilon", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleColliders.CreateProgram( gl );
			var states = new[]
			{
				P( new Vector3( 0.9f, 0.2f, 0.1f ), new Vector3( -5, 2, 0 ) ),
				P( new Vector3( 0, 0, 1.05f ), new Vector3( 1, 0, -4 ) ),
				P( new Vector3( 1.07f, 1.07f, 0 ), new Vector3( -2, -2, 3 ) ),
				P( new Vector3( 0, 0, 3 ), new Vector3( 1, 2, 3 ) ),
				P( new Vector3( -0.95f, -0.95f, -0.95f ), new Vector3( -1, -2, -3 ) ),
				P( new Vector3( 0, 0, -1.05f ), new Vector3( 0, 5, 4 ) ),
				P( new Vector3( 0.5f, 0.5f, 0.5f ), new Vector3( 0, 0, -7 ), alive: 0 ), // dead: untouched
			};
			var want = Oracle( states, null, box, R );
			uint buf = GpuParticleSystem.UploadStates( gl, states );
			GpuParticleColliders.Dispatch( gl, prog, buf, states.Length, null, box, R );
			var got = GpuParticleSystem.DownloadStates( gl, buf, states.Length );
			MaxDevs( got, want, out float maxPos, out float maxVel );
			Console.WriteLine( $"gpu-collider box: maxPosDev={maxPos:G9} maxVelDev={maxVel:G9}" );
			Check( maxPos <= 1e-4f && maxVel <= 1e-4f, $"box gpu/cpu deviation too large: pos {maxPos}, vel {maxVel}" );
			Check( got[6].Position == states[6].Position && got[6].Velocity == states[6].Velocity, "dead slot changed by gpu box pass" );
			GpuParticleColliders.Dispose( gl, buf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after gpu box test" );
		} );

		registry.Add( "gpu-particle-collider", "gpu sphere then box matches cpu sequential order", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleColliders.CreateProgram( gl );
			// Sphere (r=1 at origin) and box ([-1,1]^3) overlap, so order matters:
			// a particle pushed out of the sphere may then hit the box, and vice versa.
			var states = new[]
			{
				P( new Vector3( 0, 0, 1.05f ), new Vector3( 4, 0, -10 ) ),
				P( new Vector3( 0.9f, 0.9f, 0 ), new Vector3( -3, -3, 2 ) ),
				P( new Vector3( 0, 0, 0.5f ), new Vector3( 1, 2, -3 ) ),
				P( new Vector3( 2, 2, 2 ), new Vector3( -1, -1, -1 ) ),
			};
			var want = Oracle( states, sphere, box, R );
			uint buf = GpuParticleSystem.UploadStates( gl, states );
			GpuParticleColliders.Dispatch( gl, prog, buf, states.Length, sphere, box, R );
			var got = GpuParticleSystem.DownloadStates( gl, buf, states.Length );
			MaxDevs( got, want, out float maxPos, out float maxVel );
			Console.WriteLine( $"gpu-collider combined: maxPosDev={maxPos:G9} maxVelDev={maxVel:G9}" );
			Check( maxPos <= 1e-4f && maxVel <= 1e-4f, $"combined gpu/cpu deviation too large: pos {maxPos}, vel {maxVel}" );
			GpuParticleColliders.Dispose( gl, buf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after gpu combined test" );
		} );

		registry.Add( "gpu-particle-collider", "gpu restitution 0 friction 1 kills velocity exactly", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleColliders.CreateProgram( gl );
			var dead = new GpuParticleColliders.SphereCollider { Center = Vector3.Zero, Radius = 1, Restitution = 0, Friction = 1 };
			var states = new[] { P( new Vector3( 0, 0, 1.05f ), new Vector3( 4, 3, -10 ) ) };
			uint buf = GpuParticleSystem.UploadStates( gl, states );
			GpuParticleColliders.Dispatch( gl, prog, buf, states.Length, dead, null, R );
			var got = GpuParticleSystem.DownloadStates( gl, buf, states.Length );
			Console.WriteLine( $"gpu-collider dead-bounce: pos={got[0].Position} vel={got[0].Velocity}" );
			CloseVec( got[0].Position, new Vector3( 0, 0, 1.1f ), 1e-4f, "dead-bounce pos" );
			CloseVec( got[0].Velocity, Vector3.Zero, 1e-4f, "dead-bounce vel" );
			GpuParticleColliders.Dispose( gl, buf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after gpu dead-bounce test" );
		} );

		registry.Add( "gpu-particle-collider", "non-multiple workgroup count: guard leaves beyond-count slots untouched", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleColliders.CreateProgram( gl );
			const int count = 257; // 256 + 1: second workgroup is 255/256 guard-only
			const int total = 512;
			var states = new ParticleState[total];
			for ( int i = 0; i < count; i++ )
				states[i] = P( new Vector3( (i % 7) - 3, (i % 5) - 2, 1.05f - (i % 3) * 0.4f ), new Vector3( i - 20, 3, -5 ) );
			// Sentinels beyond count: alive AND inside the sphere, so any shader
			// invocation past uCount would visibly push them out and bounce them.
			for ( int i = count; i < total; i++ )
				states[i] = P( Vector3.Zero, new Vector3( 0, 0, -10 ) );
			var want = Oracle( states, sphere, null, R );
			uint buf = GpuParticleSystem.UploadStates( gl, states );
			GpuParticleColliders.Dispatch( gl, prog, buf, count, sphere, null, R );
			var got = GpuParticleSystem.DownloadStates( gl, buf, total );
			float maxPos = 0, maxVel = 0;
			for ( int i = 0; i < count; i++ )
			{
				maxPos = MathF.Max( maxPos, (got[i].Position - want[i].Position).Length() );
				maxVel = MathF.Max( maxVel, (got[i].Velocity - want[i].Velocity).Length() );
			}
			int sentinelChanged = 0;
			for ( int i = count; i < total; i++ )
				if ( got[i].Position != states[i].Position || got[i].Velocity != states[i].Velocity ) sentinelChanged++;
			Console.WriteLine( $"gpu-collider guard: count={count} maxPosDev={maxPos:G9} maxVelDev={maxVel:G9} sentinelChanged={sentinelChanged}/{total - count}" );
			Check( maxPos <= 1e-4f && maxVel <= 1e-4f, $"guard run deviation too large: pos {maxPos}, vel {maxVel}" );
			Check( sentinelChanged == 0, $"{sentinelChanged} beyond-count slots changed — uCount guard failed" );
			GpuParticleColliders.Dispose( gl, buf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after guard test" );
		} );

		registry.Add( "gpu-particle-collider", "count zero dispatches nothing and leaves the buffer untouched", () =>
		{
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			uint prog = GpuParticleColliders.CreateProgram( gl );
			var states = new[] { P( Vector3.Zero, new Vector3( 0, 0, -10 ) ) };
			uint buf = GpuParticleSystem.UploadStates( gl, states );
			GpuParticleColliders.Dispatch( gl, prog, buf, 0, sphere, box, R );
			var got = GpuParticleSystem.DownloadStates( gl, buf, states.Length );
			Check( got[0].Position == states[0].Position && got[0].Velocity == states[0].Velocity, "count-0 dispatch changed a slot" );
			GpuParticleColliders.Dispose( gl, buf, prog );
			Check( gl.GetError() == GLEnum.NoError, "GL error after count-0 test" );
		} );

		registry.Add( "gpu-particle-collider", "cpu apply leaves dead slots and non-position fields untouched", () =>
		{
			var ps = new ParticleState { Position = Vector3.Zero, Velocity = new Vector3( 0, 0, -10 ), Alive = 0, Age = 2.5f, Rotation = new Quaternion( 0.1f, 0.2f, 0.3f, 0.9f ), AngularVelocity = new Vector3( 1, 2, 3 ), Size = 4 };
			var before = ps;
			GpuParticleColliders.ApplyCpu( ref ps, sphere, box, R );
			Check( ps.Position == before.Position && ps.Velocity == before.Velocity && ps.Age == before.Age && ps.Rotation == before.Rotation && ps.AngularVelocity == before.AngularVelocity && ps.Size == before.Size, "dead cpu apply changed a field" );
			var live = new ParticleState { Position = new Vector3( 0, 0, 1.05f ), Velocity = new Vector3( 4, 0, -10 ), Alive = 1, Age = 2.5f, Rotation = new Quaternion( 0.1f, 0.2f, 0.3f, 0.9f ), AngularVelocity = new Vector3( 1, 2, 3 ), Size = 4 };
			GpuParticleColliders.ApplyCpu( ref live, sphere, null, R );
			Check( live.Age == 2.5f && live.Rotation == new Quaternion( 0.1f, 0.2f, 0.3f, 0.9f ) && live.AngularVelocity == new Vector3( 1, 2, 3 ) && live.Size == 4, "live cpu apply changed a non-position field" );
			CloseVec( live.Position, new Vector3( 0, 0, 1.1f ), 1e-6f, "live cpu apply pos" );
		} );

		registry.Add( "gpu-particle-collider", "invalid collider parameters fail loudly", () =>
		{
			void Throws( Action f, string what )
			{
				try { f(); } catch ( ArgumentException ) { return; }
				throw new Exception( $"expected ArgumentException for {what}" );
			}
			var pos = new Vector3( 0, 0, 1.05f ); var vel = new Vector3( 0, 0, -1 );
			Throws( () => GpuParticleColliders.CollideSphereCpu( ref pos, ref vel, new GpuParticleColliders.SphereCollider { Center = Vector3.Zero, Radius = 1, Restitution = 1.5f, Friction = 0.2f }, R ), "restitution > 1" );
			Throws( () => GpuParticleColliders.CollideSphereCpu( ref pos, ref vel, sphere, 0 ), "particle radius 0" );
			Throws( () => GpuParticleColliders.CollideSphereCpu( ref pos, ref vel, sphere, -1 ), "particle radius negative" );
			Throws( () => GpuParticleColliders.CollideBoxCpu( ref pos, ref vel, new GpuParticleColliders.BoxCollider { Min = new Vector3( 1, -1, -1 ), Max = new Vector3( 1, 1, 1 ), Restitution = 0.5f, Friction = 0.2f }, R ), "box min == max on x" );
			Throws( () => GpuParticleColliders.CollideBoxCpu( ref pos, ref vel, new GpuParticleColliders.BoxCollider { Min = new Vector3( -1, -1, -1 ), Max = new Vector3( 1, 1, 1 ), Restitution = 0.5f, Friction = -0.1f }, R ), "friction < 0" );
			var ps = P( new Vector3( 0, 0, 1.05f ), new Vector3( 0, 0, -1 ) );
			Throws( () => GpuParticleColliders.ApplyCpu( ref ps, sphere, box, 0 ), "apply with radius 0" );
		} );
	}
}
