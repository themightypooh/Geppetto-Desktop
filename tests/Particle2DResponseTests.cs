using System.Numerics;
using Marionette.Desktop;

// P76 tests. Sweep-owned registration (NOT added by this lane):
//   Particle2DResponseTests.Register( registry, settings );
// placed after the GpuParticleSystemTests.Register line, and after the
// Particle2DTests.Register line once the sweep adds it.
//
// Y-down sign proofs live here, not in comments: a falling particle moves
// +Y, a flat floor's free-space normal is (0,-1), and a bounce must come
// back with -Y. Every expected number below is computed from the formula
// documented in Particle2DResponse.cs, not copied from a run.

internal static class Particle2DResponseTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void NearV( Vector2 got, Vector2 want, float tol, string what )
		{
			if ( (got - want).Length() > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" );
		}
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; } // covers ArgumentOutOfRangeException too (it derives from it)
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}
		static Particle2DResponseSettings S( float restitution = 0.3f, float friction = 0.2f, float stickiness = 1f, float threshold = 0f, float radius = 0f )
			=> new() { Restitution = restitution, Friction = friction, Stickiness = stickiness, StickyThreshold = threshold, ParticleRadius = radius };
		static Particle2DResponseHit Hit( Vector2 point, Vector2 normal ) => new( true, point, normal );
		Vector2 contact = new( 30, 200 );
		Vector2 floorNormal = new( 0, -1 ); // Y-down "up": free space above a flat floor.

		// ---- Bounce ----

		registry.Add( "particle-2d-response", "bounce head-on: (0,100) onto floor normal (0,-1), rest 0.5, fric 0.2 -> (0,-50) exactly", () =>
		{
			// vn = (0,100).(0,-1) = -100; vt = (0,0); out = (0,0) - (0,-1)*(-100*0.5) = (0,-50).
			var r = Particle2DResponse.Bounce( new Vector2( 30, 190 ), new Vector2( 0, 100 ), Hit( contact, floorNormal ), S( restitution: 0.5f ) );
			Check( r.Velocity == new Vector2( 0, -50 ), $"outgoing must be exactly (0,-50), got {r.Velocity}" );
			Check( r.Position == contact, $"position must snap to contact {contact}, got {r.Position}" );
			Check( !r.Stuck, "bounce must not report Stuck" );
		} );

		registry.Add( "particle-2d-response", "bounce damps tangential by (1-friction): (80,100) -> (64,-50)", () =>
		{
			// vt = (80,0); vt*(1-0.2) = (64,0); normal part (0,-50). Total (64,-50).
			var r = Particle2DResponse.Bounce( new Vector2( 30, 190 ), new Vector2( 80, 100 ), Hit( contact, floorNormal ), S( restitution: 0.5f, friction: 0.2f ) );
			NearV( r.Velocity, new Vector2( 64, -50 ), 1e-3f, "bounce with tangential" );
			Check( r.Position == contact, "position must snap to contact" );
		} );

		registry.Add( "particle-2d-response", "bounce separating velocity untouched, position still snaps", () =>
		{
			// vn = (80,-100).(0,-1) = 100 >= 0: already moving away (up, off the floor).
			var v = new Vector2( 80, -100 );
			var r = Particle2DResponse.Bounce( new Vector2( 30, 210 ), v, Hit( contact, floorNormal ), S( restitution: 0.5f ) );
			Check( r.Velocity == v, $"separating velocity must be untouched, got {r.Velocity}" );
			Check( r.Position == contact, "separating contact still snaps position" );
			Check( !r.Stuck, "separating bounce must not report Stuck" );
		} );

		registry.Add( "particle-2d-response", "bounce parallel velocity untouched (vn = 0 boundary)", () =>
		{
			var v = new Vector2( 80, 0 );
			var r = Particle2DResponse.Bounce( new Vector2( 30, 200 ), v, Hit( contact, floorNormal ), S() );
			Check( r.Velocity == v, "velocity along the surface (vn = 0) must be untouched" );
			Check( r.Position == contact, "position still snaps" );
		} );

		registry.Add( "particle-2d-response", "bounce restitution 0 + friction 1 -> (0,0), not stuck", () =>
		{
			// vt*(1-1) = 0; normal part * 0 = 0. Total (0,0) — dead stop, but Stuck stays false: bounce never sticks.
			var r = Particle2DResponse.Bounce( new Vector2( 30, 190 ), new Vector2( 80, 100 ), Hit( contact, floorNormal ), S( restitution: 0f, friction: 1f ) );
			Check( r.Velocity == Vector2.Zero, $"rest 0 + fric 1 must give (0,0), got {r.Velocity}" );
			Check( !r.Stuck, "bounce dead stop is not Stuck (only Sticky/threshold set Stuck)" );
		} );

		registry.Add( "particle-2d-response", "bounce restitution 1 + friction 0 mirrors fully: (80,100) -> (80,-100)", () =>
		{
			var r = Particle2DResponse.Bounce( new Vector2( 30, 190 ), new Vector2( 80, 100 ), Hit( contact, floorNormal ), S( restitution: 1f, friction: 0f ) );
			Check( r.Velocity == new Vector2( 80, -100 ), $"perfect bounce must mirror, got {r.Velocity}" );
		} );

		registry.Add( "particle-2d-response", "bounce ceiling sign: (0,-100) onto normal (0,1) -> (0,50)", () =>
		{
			// Y-down proof from the other side: rising particle (0,-100) hits a ceiling whose
			// free-space normal is (0,1); vn = -100; out = -(0,1)*(-100*0.5) = (0,50): falls back down.
			var r = Particle2DResponse.Bounce( new Vector2( 30, 10 ), new Vector2( 0, -100 ), Hit( new Vector2( 30, 20 ), new Vector2( 0, 1 ) ), S( restitution: 0.5f ) );
			Check( r.Velocity == new Vector2( 0, 50 ), $"ceiling bounce must be (0,50), got {r.Velocity}" );
		} );

		registry.Add( "particle-2d-response", "bounce miss passes through unchanged", () =>
		{
			var p = new Vector2( 30, 150 );
			var v = new Vector2( 80, 100 );
			var r = Particle2DResponse.Bounce( p, v, Particle2DResponseHit.Miss, S( restitution: 0.5f ) );
			Check( r.Position == p && r.Velocity == v && !r.Stuck, $"miss must pass through unchanged, got {r}" );
		} );

		registry.Add( "particle-2d-response", "bounce normal is normalised: (0,-2) behaves as (0,-1)", () =>
		{
			var r = Particle2DResponse.Bounce( new Vector2( 30, 190 ), new Vector2( 0, 100 ), Hit( contact, new Vector2( 0, -2 ) ), S( restitution: 0.5f ) );
			Check( r.Velocity == new Vector2( 0, -50 ), $"non-unit normal must give (0,-50), got {r.Velocity}" );
		} );

		// ---- Bounce + sticky threshold ----

		registry.Add( "particle-2d-response", "bounce threshold boundary: speed exactly at threshold sticks", () =>
		{
			// |(0,100)| = 100 <= threshold 100 -> degrade: exactly zero + Stuck.
			var r = Particle2DResponse.Bounce( new Vector2( 30, 190 ), new Vector2( 0, 100 ), Hit( contact, floorNormal ), S( restitution: 0.5f, threshold: 100f ) );
			Check( r.Velocity == Vector2.Zero, $"at-threshold bounce must stick to zero, got {r.Velocity}" );
			Check( r.Stuck, "at-threshold bounce must report Stuck" );
			Check( r.Position == contact, "stuck bounce still snaps position" );
		} );

		registry.Add( "particle-2d-response", "bounce threshold boundary: speed just above threshold bounces", () =>
		{
			// |(0,101)| = 101 > threshold 100 -> normal bounce: (0,-50.5).
			var r = Particle2DResponse.Bounce( new Vector2( 30, 190 ), new Vector2( 0, 101 ), Hit( contact, floorNormal ), S( restitution: 0.5f, threshold: 100f ) );
			NearV( r.Velocity, new Vector2( 0, -50.5f ), 1e-3f, "just-above-threshold bounce" );
			Check( !r.Stuck, "just-above-threshold bounce must not report Stuck" );
		} );

		registry.Add( "particle-2d-response", "bounce below threshold sticks, threshold 0 never sticks", () =>
		{
			var slow = Particle2DResponse.Bounce( new Vector2( 30, 190 ), new Vector2( 0, 10 ), Hit( contact, floorNormal ), S( restitution: 0.5f, threshold: 100f ) );
			Check( slow.Velocity == Vector2.Zero && slow.Stuck, "slow bounce below threshold must stick" );
			var disabled = Particle2DResponse.Bounce( new Vector2( 30, 190 ), new Vector2( 0, 10 ), Hit( contact, floorNormal ), S( restitution: 0.5f, threshold: 0f ) );
			Check( disabled.Velocity == new Vector2( 0, -5 ), $"threshold 0 disables the rule, got {disabled.Velocity}" );
			Check( !disabled.Stuck, "threshold-disabled bounce must not report Stuck" );
		} );

		registry.Add( "particle-2d-response", "bounce threshold never sticks a separating particle", () =>
		{
			// |(0,-1)| = 1 <= threshold 100, but vn = 1 >= 0: separating wins, velocity untouched.
			var v = new Vector2( 0, -1 );
			var r = Particle2DResponse.Bounce( new Vector2( 30, 210 ), v, Hit( contact, floorNormal ), S( restitution: 0.5f, threshold: 100f ) );
			Check( r.Velocity == v && !r.Stuck, "separating particle must not stick via threshold" );
			Check( r.Position == contact, "position still snaps" );
		} );

		// ---- Sticky ----

		registry.Add( "particle-2d-response", "sticky full: velocity exactly zero + Stuck true", () =>
		{
			var r = Particle2DResponse.Sticky( new Vector2( 30, 190 ), new Vector2( 80, 100 ), Hit( contact, floorNormal ), S( stickiness: 1f ) );
			Check( r.Velocity == Vector2.Zero, $"full sticky must give exactly zero, got {r.Velocity}" );
			Check( r.Stuck, "full sticky must report Stuck" );
			Check( r.Position == contact, "sticky snaps position" );
		} );

		registry.Add( "particle-2d-response", "sticky half damps both components: (80,100) -> (40,50), not stuck", () =>
		{
			var r = Particle2DResponse.Sticky( new Vector2( 30, 190 ), new Vector2( 80, 100 ), Hit( contact, floorNormal ), S( stickiness: 0.5f ) );
			Check( r.Velocity == new Vector2( 40, 50 ), $"half sticky must give (40,50), got {r.Velocity}" );
			Check( !r.Stuck, "partial sticky must not report Stuck" );
		} );

		registry.Add( "particle-2d-response", "sticky zero leaves velocity unchanged, not stuck", () =>
		{
			var v = new Vector2( 80, 100 );
			var r = Particle2DResponse.Sticky( new Vector2( 30, 190 ), v, Hit( contact, floorNormal ), S( stickiness: 0f ) );
			Check( r.Velocity == v && !r.Stuck, "zero stickiness must leave velocity and Stuck alone" );
			Check( r.Position == contact, "position still snaps" );
		} );

		registry.Add( "particle-2d-response", "sticky miss passes through unchanged", () =>
		{
			var p = new Vector2( 30, 150 );
			var v = new Vector2( 80, 100 );
			var r = Particle2DResponse.Sticky( p, v, Particle2DResponseHit.Miss, S( stickiness: 1f ) );
			Check( r.Position == p && r.Velocity == v && !r.Stuck, $"sticky miss must pass through, got {r}" );
		} );

		// ---- Slide ----

		registry.Add( "particle-2d-response", "slide flat floor: vertical removed, horizontal kept damped: (80,100) -> (64,0)", () =>
		{
			var r = Particle2DResponse.Slide( new Vector2( 30, 190 ), new Vector2( 80, 100 ), Hit( contact, floorNormal ), S( friction: 0.2f ) );
			NearV( r.Velocity, new Vector2( 64, 0 ), 1e-3f, "flat slide" );
			Check( r.Velocity.Y == 0, $"flat slide must remove vertical exactly, got Y = {r.Velocity.Y}" );
			Check( r.Position == contact, "slide snaps position" );
			Check( !r.Stuck, "slide never reports Stuck" );
		} );

		registry.Add( "particle-2d-response", "slide 45-degree slope: velocity runs ALONG the slope, normal component ~0", () =>
		{
			// Slope rising up-right in Y-down space; free-space normal (-√2/2,-√2/2) (up-left).
			// Falling (0,100): vn = -70.71; vt = (0,100) - n*vn = (-50,50): along the slope direction (-1,1).
			float s = 0.70710678f;
			var n = new Vector2( -s, -s );
			var r = Particle2DResponse.Slide( new Vector2( 30, 190 ), new Vector2( 0, 100 ), Hit( contact, n ), S( friction: 0f ) );
			NearV( r.Velocity, new Vector2( -50, 50 ), 1e-3f, "slope slide velocity" );
			Near( Vector2.Dot( r.Velocity, Vector2.Normalize( n ) ), 0f, 1e-3f, "slope slide normal component" );
			// Parallel check: cross product of velocity with the slope direction (-1,1)/√2 must be ~0.
			Near( r.Velocity.X * 1f - r.Velocity.Y * -1f, 0f, 1e-3f, "slope slide must be parallel to the slope" );
		} );

		registry.Add( "particle-2d-response", "slide 45-degree slope with friction: (-50,50) damped to (-40,40)", () =>
		{
			float s = 0.70710678f;
			var r = Particle2DResponse.Slide( new Vector2( 30, 190 ), new Vector2( 0, 100 ), Hit( contact, new Vector2( -s, -s ) ), S( friction: 0.2f ) );
			NearV( r.Velocity, new Vector2( -40, 40 ), 1e-3f, "slope slide with friction" );
			Near( Vector2.Dot( r.Velocity, new Vector2( -s, -s ) ), 0f, 1e-3f, "damped slope slide normal component" );
		} );

		registry.Add( "particle-2d-response", "slide separating velocity untouched, position still snaps", () =>
		{
			var v = new Vector2( 80, -100 );
			var r = Particle2DResponse.Slide( new Vector2( 30, 210 ), v, Hit( contact, floorNormal ), S() );
			Check( r.Velocity == v, "separating slide must leave velocity alone" );
			Check( r.Position == contact, "position still snaps" );
		} );

		registry.Add( "particle-2d-response", "slide miss passes through unchanged", () =>
		{
			var p = new Vector2( 30, 150 );
			var v = new Vector2( 80, 100 );
			var r = Particle2DResponse.Slide( p, v, Particle2DResponseHit.Miss, S() );
			Check( r.Position == p && r.Velocity == v && !r.Stuck, $"slide miss must pass through, got {r}" );
		} );

		// ---- Validate: loud failures ----

		registry.Add( "particle-2d-response", "validate rejects out-of-range and negative settings", () =>
		{
			var p = new Vector2( 30, 190 );
			var v = new Vector2( 80, 100 );
			var hit = Hit( contact, floorNormal );
			Throws( () => Particle2DResponse.Bounce( p, v, hit, S( restitution: -0.1f ) ), "negative restitution" );
			Throws( () => Particle2DResponse.Bounce( p, v, hit, S( restitution: 1.1f ) ), "restitution above 1" );
			Throws( () => Particle2DResponse.Bounce( p, v, hit, S( restitution: float.NaN ) ), "NaN restitution" );
			Throws( () => Particle2DResponse.Bounce( p, v, hit, S( friction: -0.1f ) ), "negative friction" );
			Throws( () => Particle2DResponse.Bounce( p, v, hit, S( friction: 1.1f ) ), "friction above 1" );
			Throws( () => Particle2DResponse.Sticky( p, v, hit, S( stickiness: -0.1f ) ), "negative stickiness" );
			Throws( () => Particle2DResponse.Sticky( p, v, hit, S( stickiness: 1.1f ) ), "stickiness above 1" );
			Throws( () => Particle2DResponse.Bounce( p, v, hit, S( threshold: -1f ) ), "negative sticky threshold" );
			Throws( () => Particle2DResponse.Bounce( p, v, hit, S( radius: -1f ) ), "negative particle radius" );
			Throws( () => Particle2DResponse.Bounce( p, v, hit, S( radius: float.NaN ) ), "NaN particle radius" );
			Throws( () => S( restitution: -1f ).Validate(), "settings Validate rejects negative restitution directly" );
			Throws( () => S( stickiness: 2f ).Validate(), "settings Validate rejects stickiness above 1 directly" );
		} );

		registry.Add( "particle-2d-response", "validate rejects non-finite state and bad normals", () =>
		{
			var p = new Vector2( 30, 190 );
			var v = new Vector2( 80, 100 );
			var hit = Hit( contact, floorNormal );
			Throws( () => Particle2DResponse.Bounce( new Vector2( float.NaN, 190 ), v, hit, S() ), "NaN position" );
			Throws( () => Particle2DResponse.Bounce( p, new Vector2( float.PositiveInfinity, 100 ), hit, S() ), "infinite velocity" );
			Throws( () => Particle2DResponse.Bounce( p, v, Hit( contact, Vector2.Zero ), S() ), "zero-length normal" );
			Throws( () => Particle2DResponse.Bounce( p, v, Hit( contact, new Vector2( float.NaN, -1 ) ), S() ), "NaN normal" );
			Throws( () => Particle2DResponse.Bounce( p, v, Hit( new Vector2( float.NaN, 200 ), floorNormal ), S() ), "NaN hit point" );
			Throws( () => Particle2DResponse.Slide( p, v, Hit( contact, Vector2.Zero ), S() ), "slide zero-length normal" );
			Throws( () => Particle2DResponse.Sticky( p, v, Hit( contact, Vector2.Zero ), S() ), "sticky zero-length normal" );
			Throws( () => Particle2DResponse.Bounce( p, v, hit, null! ), "null settings" );
		} );
	}
}
