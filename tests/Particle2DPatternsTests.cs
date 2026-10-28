using System.Numerics;
using Marionette.Desktop;

// Wave-6 lane P16 tests. Group: particle-2d-patterns.
// Sweep line (scratch only — do NOT add to the canonical TestRegistry.cs):
//   Particle2DPatternsTests.Register( registry, settings );
// placed immediately after the Particle2DTests.Register line (which the
// sweep adds after GpuParticleSystemTests.Register).

internal static class Particle2DPatternsTests
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
		float AngleDeg( Vector2 v ) => MathF.Atan2( v.Y, v.X ) * 180f / MathF.PI;

		registry.Add( "particle-2d-patterns", "burst ring positions lie on the circle with outward velocities", () =>
		{
			var rng = new ParticleRng( 42 );
			var spawns = Particle2DPatterns.BurstRing( new Vector2( 100, 200 ), 50, 80, 12, ref rng );
			Check( spawns.Count == 12, $"ring count: got {spawns.Count}, want 12" );
			for ( int i = 0; i < 12; i++ )
			{
				var s = spawns[i];
				Near( (s.Position - new Vector2( 100, 200 )).Length(), 50, 1e-3f, $"ring particle {i} distance from centre" );
				Near( s.Velocity.Length(), 80, 1e-3f, $"ring particle {i} speed" );
				var radial = (s.Position - new Vector2( 100, 200 )) / 50;
				float cross = radial.X * s.Velocity.Y - radial.Y * s.Velocity.X;
				Near( cross, 0, 1e-3f, $"ring particle {i} velocity must be parallel to the radius" );
				Check( Vector2.Dot( radial, s.Velocity ) > 0, $"ring particle {i} velocity must point outward, not inward" );
				float wantAngle = i * 30; if ( wantAngle > 180 ) wantAngle -= 360; // Atan2 returns -180..180, so 210 deg reads as -150
				Near( AngleDeg( radial ), wantAngle, 1e-3f, $"ring particle {i} angle (exactly even, no jitter)" );
			}
			Check( spawns[0].Position == new Vector2( 150, 200 ), $"ring particle 0 should be at (150,200), got {spawns[0].Position}" );
			Check( spawns[3].Position == new Vector2( 100, 250 ), $"ring particle 3 (90 deg, Y-down) should be at (100,250), got {spawns[3].Position}" );
		} );

		registry.Add( "particle-2d-patterns", "burst ring jitter stays on the circle within the jitter bound", () =>
		{
			var rng = new ParticleRng( 42 );
			var spawns = Particle2DPatterns.BurstRing( new Vector2( 0, 0 ), 30, 10, 200, ref rng, angularJitterDegrees: 10 );
			float maxDev = 0;
			for ( int i = 0; i < 200; i++ )
			{
				var s = spawns[i];
				Near( s.Position.Length(), 30, 1e-3f, $"jittered ring particle {i} must stay on the circle" );
				float dev = MathF.Abs( AngleDeg( s.Position ) - i * 1.8f );
				if ( dev > 180 ) dev = 360 - dev;
				Check( dev <= 10.001f, $"jittered ring particle {i} deviated {dev} deg, over the 10 deg jitter bound" );
				maxDev = MathF.Max( maxDev, dev );
			}
			Check( maxDev > 1, $"jitter should actually move angles, but max deviation was only {maxDev} deg" );
		} );

		registry.Add( "particle-2d-patterns", "burst ring count 1 sits at angle 0", () =>
		{
			var rng = new ParticleRng( 1 );
			var spawns = Particle2DPatterns.BurstRing( new Vector2( 5, 5 ), 20, 7, 1, ref rng );
			Check( spawns.Count == 1, $"count-1 ring: got {spawns.Count}" );
			Check( spawns[0].Position == new Vector2( 25, 5 ), $"count-1 ring position: got {spawns[0].Position}, want (25,5)" );
			Check( spawns[0].Velocity == new Vector2( 7, 0 ), $"count-1 ring velocity: got {spawns[0].Velocity}, want (7,0)" );
		} );

		registry.Add( "particle-2d-patterns", "spiral radius grows by exactly the step with tangential velocities", () =>
		{
			var rng = new ParticleRng( 42 );
			var spawns = Particle2DPatterns.Spiral( new Vector2( 0, 0 ), 10, 5, 0, 30, 60, 20, ref rng );
			Check( spawns.Count == 20, $"spiral count: got {spawns.Count}, want 20" );
			for ( int i = 0; i < 20; i++ )
			{
				var s = spawns[i];
				Near( s.Position.Length(), 10 + 5 * i, 1e-3f, $"spiral particle {i} radius" );
				Near( s.Velocity.Length(), 60, 1e-3f, $"spiral particle {i} speed" );
				var radial = s.Position / s.Position.Length();
				Near( Vector2.Dot( radial, s.Velocity ), 0, 1e-3f, $"spiral particle {i} velocity must be tangential (perpendicular to radius)" );
			}
			for ( int i = 1; i < 20; i++ )
				Near( spawns[i].Position.Length() - spawns[i - 1].Position.Length(), 5, 1e-3f, $"spiral radius growth step {i}" );
			// Particle 1: angle 30 deg, radial (cos30, sin30), tangent (-sin30, cos30) — clockwise on screen in Y-down.
			var t1 = new Vector2( -0.5f, 0.8660254f );
			Near( (spawns[1].Velocity / 60 - t1).Length(), 0, 1e-3f, "spiral particle 1 tangent direction" );
		} );

		registry.Add( "particle-2d-patterns", "spiral negative angle step follows index order", () =>
		{
			var rng = new ParticleRng( 42 );
			var spawns = Particle2DPatterns.Spiral( new Vector2( 0, 0 ), 10, 5, 0, -30, 60, 2, ref rng );
			// Particle 1: angle -30 deg, radial (cos-30, sin-30); index-order tangent is the flipped increasing-angle tangent.
			var want = 60 * new Vector2( -0.5f, -0.8660254f );
			Near( (spawns[1].Velocity - want).Length(), 0, 1e-3f, "negative-step spiral particle 1 tangent" );
		} );

		registry.Add( "particle-2d-patterns", "fountain velocities stay inside the upward cone", () =>
		{
			var rng = new ParticleRng( 42 );
			var spawns = Particle2DPatterns.Fountain( new Vector2( 0, 0 ), 100, 200, 60, 500, ref rng );
			Check( spawns.Count == 500, $"fountain count: got {spawns.Count}, want 500" );
			float minSigned = float.MaxValue, maxSigned = float.MinValue;
			foreach ( var s in spawns )
			{
				Check( s.Position == new Vector2( 0, 0 ), $"fountain particle must start at the origin, got {s.Position}" );
				float speed = s.Velocity.Length();
				Check( speed >= 100 && speed <= 200, $"fountain speed {speed} outside [100, 200]" );
				Check( s.Velocity.Y < 0, $"fountain velocity must point up (-Y), got {s.Velocity}" );
				float angleFromUp = MathF.Acos( -s.Velocity.Y / speed ) * 180f / MathF.PI;
				Check( angleFromUp <= 30.01f, $"fountain angle from up {angleFromUp} deg exceeds the 30 deg half-angle" );
				float signed = MathF.Atan2( s.Velocity.X, -s.Velocity.Y ) * 180f / MathF.PI;
				minSigned = MathF.Min( minSigned, signed ); maxSigned = MathF.Max( maxSigned, signed );
			}
			Check( minSigned < -20 && maxSigned > 20, $"fountain cone should reach both sides, got signed angles [{minSigned}, {maxSigned}]" );
		} );

		registry.Add( "particle-2d-patterns", "fountain equal min/max speed gives one speed", () =>
		{
			var rng = new ParticleRng( 42 );
			var spawns = Particle2DPatterns.Fountain( new Vector2( 10, 10 ), 150, 150, 90, 50, ref rng );
			foreach ( var s in spawns ) Near( s.Velocity.Length(), 150, 1e-3f, "fountain equal-speed particle speed" );
		} );

		registry.Add( "particle-2d-patterns", "spray velocities are exactly perpendicular with no spread", () =>
		{
			var rng = new ParticleRng( 42 );
			var spawns = Particle2DPatterns.Spray( new Vector2( 0, 0 ), new Vector2( 100, 0 ), 50, 200, ref rng );
			float minX = float.MaxValue, maxX = float.MinValue;
			foreach ( var s in spawns )
			{
				Near( s.Position.Y, 0, 1e-4f, "spray particle must lie on the segment line" );
				Check( s.Position.X >= 0 && s.Position.X <= 100, $"spray particle X {s.Position.X} outside the segment" );
				Near( (s.Velocity - new Vector2( 0, 50 )).Length(), 0, 1e-3f, "east segment, no spread: velocity must be the clockwise normal (0,50)" );
				minX = MathF.Min( minX, s.Position.X ); maxX = MathF.Max( maxX, s.Position.X );
			}
			Check( minX < 20 && maxX > 80, $"spray should cover the segment, got X range [{minX}, {maxX}]" );
		} );

		registry.Add( "particle-2d-patterns", "spray spread stays on the perpendicular side", () =>
		{
			var rng = new ParticleRng( 42 );
			var spawns = Particle2DPatterns.Spray( new Vector2( 0, 0 ), new Vector2( 100, 0 ), 50, 500, ref rng, spreadDegrees: 60 );
			var perp = new Vector2( 0, 1 );
			foreach ( var s in spawns )
			{
				Near( s.Position.Y, 0, 1e-4f, "spread spray particle must lie on the segment line" );
				Near( s.Velocity.Length(), 50, 1e-3f, "spread spray particle speed" );
				float cosAng = Vector2.Dot( s.Velocity / 50, perp );
				Check( cosAng >= MathF.Cos( 30 * MathF.PI / 180f ) - 1e-3f, $"spread spray angle from perpendicular exceeds 30 deg half-spread (cos {cosAng})" );
				Check( s.Velocity.Y > 0, $"spread spray velocity must stay on the perpendicular side, got {s.Velocity}" );
			}
		} );

		registry.Add( "particle-2d-patterns", "every generator returns exactly N particles", () =>
		{
			var rng = new ParticleRng( 9 );
			Check( Particle2DPatterns.BurstRing( new Vector2( 0, 0 ), 5, 5, 37, ref rng ).Count == 37, "ring count 37" );
			Check( Particle2DPatterns.Spiral( new Vector2( 0, 0 ), 5, 1, 0, 10, 5, 37, ref rng ).Count == 37, "spiral count 37" );
			Check( Particle2DPatterns.Fountain( new Vector2( 0, 0 ), 5, 6, 45, 37, ref rng ).Count == 37, "fountain count 37" );
			Check( Particle2DPatterns.Spray( new Vector2( 0, 0 ), new Vector2( 10, 0 ), 5, 37, ref rng ).Count == 37, "spray count 37" );
		} );

		registry.Add( "particle-2d-patterns", "same seed gives identical spawn lists", () =>
		{
			List<Particle2DSpawn> Run( uint seed )
			{
				var rng = new ParticleRng( seed );
				var all = new List<Particle2DSpawn>();
				all.AddRange( Particle2DPatterns.BurstRing( new Vector2( 10, 20 ), 40, 30, 50, ref rng, angularJitterDegrees: 15 ) );
				all.AddRange( Particle2DPatterns.Spiral( new Vector2( 0, 0 ), 5, 2, 0, 20, 40, 50, ref rng, speedJitter: 0.5f ) );
				all.AddRange( Particle2DPatterns.Fountain( new Vector2( 0, 0 ), 50, 150, 60, 50, ref rng ) );
				all.AddRange( Particle2DPatterns.Spray( new Vector2( 0, 0 ), new Vector2( 50, 50 ), 30, 50, ref rng, spreadDegrees: 40, speedJitter: 0.5f ) );
				return all;
			}
			var a = Run( 42 ); var b = Run( 42 );
			Check( a.Count == b.Count && a.Count == 200, $"determinism needs 200 identical spawns, got {a.Count} vs {b.Count}" );
			for ( int i = 0; i < a.Count; i++ )
				Check( a[i].Position == b[i].Position && a[i].Velocity == b[i].Velocity, $"spawn {i} differs between same-seed runs" );
			var c = Run( 43 );
			bool anyDiff = false;
			for ( int i = 0; i < a.Count; i++ )
				if ( a[i].Position != c[i].Position || a[i].Velocity != c[i].Velocity ) { anyDiff = true; break; }
			Check( anyDiff, "different seeds produced identical spawn lists — seed is ignored" );
		} );

		registry.Add( "particle-2d-patterns", "Validate rejects bad counts radii speeds and cone angles loudly", () =>
		{
			Throws( () => Particle2DPatterns.Validate( 0, 1, 1, 90 ), "count 0" );
			Throws( () => Particle2DPatterns.Validate( -3, 1, 1, 90 ), "count -3" );
			Throws( () => Particle2DPatterns.Validate( 10, -1, 1, 90 ), "negative radius" );
			Throws( () => Particle2DPatterns.Validate( 10, float.NaN, 1, 90 ), "NaN radius" );
			Throws( () => Particle2DPatterns.Validate( 10, 1, -1, 90 ), "negative speed" );
			Throws( () => Particle2DPatterns.Validate( 10, 1, float.PositiveInfinity, 90 ), "infinite speed" );
			Throws( () => Particle2DPatterns.Validate( 10, 1, 1, 0 ), "cone angle 0" );
			Throws( () => Particle2DPatterns.Validate( 10, 1, 1, 180 ), "cone angle 180" );
			Throws( () => Particle2DPatterns.Validate( 10, 1, 1, 200 ), "cone angle 200" );
			Throws( () => Particle2DPatterns.Validate( 10, 1, 1, float.NaN ), "NaN cone angle" );
			Particle2DPatterns.Validate( 10, 5, 5, 90 );
			Particle2DPatterns.Validate( 1, 0, 0, 179.9f );
		} );

		registry.Add( "particle-2d-patterns", "generators reject their own bad inputs loudly", () =>
		{
			var rng = new ParticleRng( 42 );
			Throws( () => Particle2DPatterns.BurstRing( new Vector2( 0, 0 ), 5, 5, 0, ref rng ), "ring count 0" );
			Throws( () => Particle2DPatterns.BurstRing( new Vector2( 0, 0 ), -5, 5, 5, ref rng ), "ring negative radius" );
			Throws( () => Particle2DPatterns.BurstRing( new Vector2( 0, 0 ), 5, -5, 5, ref rng ), "ring negative speed" );
			Throws( () => Particle2DPatterns.BurstRing( new Vector2( 0, 0 ), 5, 5, 5, ref rng, angularJitterDegrees: -1 ), "ring negative jitter" );
			Throws( () => Particle2DPatterns.BurstRing( new Vector2( 0, 0 ), 5, 5, 5, ref rng, angularJitterDegrees: 181 ), "ring jitter over 180" );
			Throws( () => Particle2DPatterns.Spiral( new Vector2( 0, 0 ), -5, 1, 0, 10, 5, 5, ref rng ), "spiral negative start radius" );
			Throws( () => Particle2DPatterns.Spiral( new Vector2( 0, 0 ), 5, -1, 0, 10, 5, 5, ref rng ), "spiral negative radius step" );
			Throws( () => Particle2DPatterns.Spiral( new Vector2( 0, 0 ), 5, 1, 0, 10, 5, 5, ref rng, speedJitter: 1.5f ), "spiral speed jitter over 1" );
			Throws( () => Particle2DPatterns.Fountain( new Vector2( 0, 0 ), 200, 100, 60, 5, ref rng ), "fountain min speed over max" );
			Throws( () => Particle2DPatterns.Fountain( new Vector2( 0, 0 ), -1, 100, 60, 5, ref rng ), "fountain negative min speed" );
			Throws( () => Particle2DPatterns.Fountain( new Vector2( 0, 0 ), 10, 100, 0, 5, ref rng ), "fountain cone 0" );
			Throws( () => Particle2DPatterns.Fountain( new Vector2( 0, 0 ), 10, 100, 180, 5, ref rng ), "fountain cone 180" );
			Throws( () => Particle2DPatterns.Spray( new Vector2( 5, 5 ), new Vector2( 5, 5 ), 10, 5, ref rng ), "spray degenerate segment" );
			Throws( () => Particle2DPatterns.Spray( new Vector2( 0, 0 ), new Vector2( 10, 0 ), -10, 5, ref rng ), "spray negative speed" );
			Throws( () => Particle2DPatterns.Spray( new Vector2( 0, 0 ), new Vector2( 10, 0 ), 10, 5, ref rng, spreadDegrees: 181 ), "spray spread over 180" );
		} );
	}
}
