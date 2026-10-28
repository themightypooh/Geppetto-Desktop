using System.Numerics;
using Marionette.Desktop;

internal static class ParticleCollidersTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void NearV( Vector3 got, Vector3 want, float tol, string what )
		{
			if ( (got - want).Length() > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" );
		}
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; } // covers ArgumentOutOfRangeException too (it derives from it)
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}

		// A [-5, 5]^3 cube as six planes: inside = negative side of every plane.
		static ParticlePlaneSetCollider Cube( float half = 5f )
		{
			var c = new ParticlePlaneSetCollider();
			c.Planes.Add( new ColliderPlane( new Vector3( -1, 0, 0 ), half ) );
			c.Planes.Add( new ColliderPlane( new Vector3( 1, 0, 0 ), half ) );
			c.Planes.Add( new ColliderPlane( new Vector3( 0, -1, 0 ), half ) );
			c.Planes.Add( new ColliderPlane( new Vector3( 0, 1, 0 ), half ) );
			c.Planes.Add( new ColliderPlane( new Vector3( 0, 0, -1 ), half ) );
			c.Planes.Add( new ColliderPlane( new Vector3( 0, 0, 1 ), half ) );
			return c;
		}

		// ---- Sphere ----

		registry.Add( "particle-colliders", "sphere head-on swept hit point and normal", () =>
		{
			var s = new ParticleSphereCollider { Center = new Vector3( 0, 0, 0 ), Radius = 10 };
			var hit = s.Sweep( new Vector3( 0, 0, -20 ), new Vector3( 0, 0, 20 ), 1 );
			Check( hit.Hit, "expected a hit" );
			NearV( hit.Point, new Vector3( 0, 0, -11 ), 1e-4f, "hit point" );
			NearV( hit.Normal, new Vector3( 0, 0, -1 ), 1e-4f, "hit normal" );
			Near( hit.Time, 9f / 40, 1e-4f, "hit time" );
			Check( MathF.Abs( hit.Normal.Length() - 1 ) < 1e-4f, "normal must be unit length" );
		} );

		registry.Add( "particle-colliders", "sphere miss passes beside", () =>
		{
			var s = new ParticleSphereCollider { Center = new Vector3( 0, 0, 0 ), Radius = 10 };
			var hit = s.Sweep( new Vector3( 0, 20, -20 ), new Vector3( 0, 20, 20 ), 1 );
			Check( !hit.Hit, "segment 20in off-axis must miss a radius-11 sphere" );
		} );

		registry.Add( "particle-colliders", "sphere tunnelling: swept catches what a point test misses", () =>
		{
			var s = new ParticleSphereCollider { Center = new Vector3( 0, 0, 0 ), Radius = 1 };
			var prev = new Vector3( 0, 0, -10 );
			var curr = new Vector3( 0, 0, 10 );
			Check( !s.Contains( curr, 0 ), "point test at the end position must miss (particle jumped clean through)" );
			var hit = s.Sweep( prev, curr, 0 );
			Check( hit.Hit, "swept test must catch the tunnelled sphere" );
			NearV( hit.Point, new Vector3( 0, 0, -1 ), 1e-4f, "tunnel hit point" );
			NearV( hit.Normal, new Vector3( 0, 0, -1 ), 1e-4f, "tunnel hit normal" );
		} );

		registry.Add( "particle-colliders", "sphere start inside reports contact at t=0", () =>
		{
			var s = new ParticleSphereCollider { Center = new Vector3( 0, 0, 0 ), Radius = 10 };
			var hit = s.Sweep( new Vector3( 3, 4, 0 ), new Vector3( 3, 4, 20 ), 0 );
			Check( hit.Hit, "expected a contact hit" );
			Near( hit.Time, 0, 1e-6f, "inside-start time" );
			NearV( hit.Point, new Vector3( 3, 4, 0 ), 1e-4f, "inside-start point" );
			NearV( hit.Normal, new Vector3( 0.6f, 0.8f, 0 ), 1e-4f, "inside-start radial normal" );
		} );

		// ---- Box ----

		registry.Add( "particle-colliders", "box head-on swept hit with radius expansion", () =>
		{
			var b = new ParticleBoxCollider { Min = new Vector3( -5, -5, -5 ), Max = new Vector3( 5, 5, 5 ) };
			var hit = b.Sweep( new Vector3( 0, 0, -20 ), new Vector3( 0, 0, 20 ), 1 );
			Check( hit.Hit, "expected a hit" );
			NearV( hit.Point, new Vector3( 0, 0, -6 ), 1e-4f, "hit point (box face -5 minus radius 1)" );
			NearV( hit.Normal, new Vector3( 0, 0, -1 ), 1e-4f, "hit normal" );
			Near( hit.Time, 14f / 40, 1e-4f, "hit time" );
		} );

		registry.Add( "particle-colliders", "box normal is the closest face on each side", () =>
		{
			var b = new ParticleBoxCollider { Min = new Vector3( -5, -5, -5 ), Max = new Vector3( 5, 5, 5 ) };
			var hitX = b.Sweep( new Vector3( 20, 0, 0 ), new Vector3( -20, 0, 0 ), 0 );
			Check( hitX.Hit, "expected an X-face hit" );
			NearV( hitX.Point, new Vector3( 5, 0, 0 ), 1e-4f, "X-face hit point" );
			NearV( hitX.Normal, new Vector3( 1, 0, 0 ), 1e-4f, "X-face normal" );
			var hitY = b.Sweep( new Vector3( 0, -20, 0 ), new Vector3( 0, 20, 0 ), 0 );
			Check( hitY.Hit, "expected a Y-face hit" );
			NearV( hitY.Normal, new Vector3( 0, -1, 0 ), 1e-4f, "Y-face normal" );
		} );

		registry.Add( "particle-colliders", "box miss above and inside-start closest face", () =>
		{
			var b = new ParticleBoxCollider { Min = new Vector3( -5, -5, -5 ), Max = new Vector3( 5, 5, 5 ) };
			Check( !b.Sweep( new Vector3( -20, 0, 20 ), new Vector3( 20, 0, 20 ), 0 ).Hit, "segment above the box must miss" );
			var inside = b.Sweep( new Vector3( 0, 0, 4.5f ), new Vector3( 0, 0, 20 ), 0 );
			Check( inside.Hit, "expected an inside-start hit" );
			Near( inside.Time, 0, 1e-6f, "inside-start time" );
			NearV( inside.Normal, new Vector3( 0, 0, 1 ), 1e-4f, "closest face is +Z (0.5in away)" );
		} );

		// ---- Plane-set ----

		registry.Add( "particle-colliders", "plane-set cube head-on hit", () =>
		{
			var hit = Cube().Sweep( new Vector3( 0, 0, -20 ), new Vector3( 0, 0, 20 ), 0 );
			Check( hit.Hit, "expected a hit" );
			NearV( hit.Point, new Vector3( 0, 0, -5 ), 1e-4f, "hit point" );
			NearV( hit.Normal, new Vector3( 0, 0, -1 ), 1e-4f, "hit normal" );
			Near( hit.Time, 15f / 40, 1e-4f, "hit time" );
		} );

		registry.Add( "particle-colliders", "plane-set erodes by particle radius and misses beside", () =>
		{
			var hit = Cube().Sweep( new Vector3( 0, 0, -20 ), new Vector3( 0, 0, 20 ), 1 );
			Check( hit.Hit, "expected a hit" );
			NearV( hit.Point, new Vector3( 0, 0, -6 ), 1e-4f, "eroded hit point (face -5 minus radius 1)" );
			Check( !Cube().Sweep( new Vector3( 20, 0, -20 ), new Vector3( 20, 0, 20 ), 0 ).Hit, "segment beside the cube must miss" );
			Check( Cube().Contains( new Vector3( 0, 0, 0 ), 0 ), "cube centre must be inside" );
			Check( !Cube().Contains( new Vector3( 0, 0, 6 ), 0 ), "point past the face must be outside" );
		} );

		registry.Add( "particle-colliders", "plane-set flipped room: wall hit normal points back inside", () =>
		{
			var room = Cube().Flipped();
			var hit = room.Sweep( new Vector3( 0, 0, 0 ), new Vector3( 0, 0, 20 ), 0 );
			Check( hit.Hit, "expected a wall hit from inside the room" );
			NearV( hit.Point, new Vector3( 0, 0, 5 ), 1e-4f, "wall hit point" );
			NearV( hit.Normal, new Vector3( 0, 0, -1 ), 1e-4f, "wall normal points back into the room" );
			Near( hit.Time, 0.25f, 1e-4f, "hit time" );
			// Resolve must bounce the room-bound particle back inside.
			var (pos, vel) = ParticleColliderResponse.Resolve( new Vector3( 0, 0, 20 ), new Vector3( 0, 0, 10 ), hit, 0.5f, 0f );
			NearV( pos, new Vector3( 0, 0, 5 ), 1e-4f, "resolved position snaps to the wall contact" );
			NearV( vel, new Vector3( 0, 0, -5 ), 1e-4f, "resolved velocity reflects back into the room at restitution 0.5" );
			// With a radius, the centre stops one radius short of the wall.
			var fat = room.Sweep( new Vector3( 0, 0, 0 ), new Vector3( 0, 0, 20 ), 1 );
			Check( fat.Hit, "expected a radius-corrected wall hit" );
			NearV( fat.Point, new Vector3( 0, 0, 4 ), 1e-4f, "radius-corrected wall contact (wall 5 minus radius 1)" );
			NearV( fat.Normal, new Vector3( 0, 0, -1 ), 1e-4f, "radius-corrected wall normal" );
			Near( fat.Time, 0.2f, 1e-4f, "radius-corrected hit time" );
			Check( room.Contains( new Vector3( 0, 0, 0 ), 0 ), "room centre is inside the room" );
			Check( !room.Contains( new Vector3( 0, 0, 6 ), 0 ), "point past the wall is outside the room" );
		} );

		// ---- Capsule ----

		registry.Add( "particle-colliders", "capsule side hit is perpendicular to the axis", () =>
		{
			var c = new ParticleCapsuleCollider { A = new Vector3( 0, 0, -5 ), B = new Vector3( 0, 0, 5 ), Radius = 2 };
			var hit = c.Sweep( new Vector3( -20, 0, 0 ), new Vector3( 20, 0, 0 ), 1 );
			Check( hit.Hit, "expected a side hit" );
			NearV( hit.Point, new Vector3( -3, 0, 0 ), 1e-4f, "side hit point (radius 2 + particle 1)" );
			NearV( hit.Normal, new Vector3( -1, 0, 0 ), 1e-4f, "side hit normal" );
			Near( hit.Time, 17f / 40, 1e-4f, "hit time" );
		} );

		registry.Add( "particle-colliders", "capsule cap hit and diagonal-axis hit", () =>
		{
			var c = new ParticleCapsuleCollider { A = new Vector3( 0, 0, -5 ), B = new Vector3( 0, 0, 5 ), Radius = 2 };
			var cap = c.Sweep( new Vector3( 0, 0, -20 ), new Vector3( 0, 0, 20 ), 1 );
			Check( cap.Hit, "expected a cap hit" );
			NearV( cap.Point, new Vector3( 0, 0, -8 ), 1e-4f, "cap hit point (end -5 minus radius 3)" );
			NearV( cap.Normal, new Vector3( 0, 0, -1 ), 1e-4f, "cap hit normal" );
			Near( cap.Time, 12f / 40, 1e-4f, "cap hit time" );
			var alongX = new ParticleCapsuleCollider { A = new Vector3( -5, 0, 0 ), B = new Vector3( 5, 0, 0 ), Radius = 2 };
			var side = alongX.Sweep( new Vector3( 0, -20, 0 ), new Vector3( 0, 20, 0 ), 0 );
			Check( side.Hit, "expected an X-axis capsule side hit" );
			NearV( side.Point, new Vector3( 0, -2, 0 ), 1e-4f, "X-axis side hit point" );
			NearV( side.Normal, new Vector3( 0, -1, 0 ), 1e-4f, "X-axis side hit normal" );
		} );

		registry.Add( "particle-colliders", "capsule miss offset and start-inside contact", () =>
		{
			var c = new ParticleCapsuleCollider { A = new Vector3( 0, 0, -5 ), B = new Vector3( 0, 0, 5 ), Radius = 2 };
			Check( !c.Sweep( new Vector3( -20, 10, 0 ), new Vector3( 20, 10, 0 ), 0 ).Hit, "segment 10in off-axis must miss a radius-2 capsule" );
			var inside = c.Sweep( new Vector3( 1, 0, 0 ), new Vector3( 20, 0, 0 ), 0 );
			Check( inside.Hit, "expected an inside-start hit" );
			Near( inside.Time, 0, 1e-6f, "inside-start time" );
			NearV( inside.Normal, new Vector3( 1, 0, 0 ), 1e-4f, "inside-start normal points away from the axis" );
		} );

		// ---- Resolve (ParticleSystem Respond semantics) ----

		registry.Add( "particle-colliders", "resolve reflects normal at restitution and keeps tangential at 1-friction", () =>
		{
			var hit = new ColliderHit( true, new Vector3( 1, 2, 3 ), new Vector3( 0, 0, 1 ), 0.5f );
			var (pos, vel) = ParticleColliderResponse.Resolve( new Vector3( 9, 9, 9 ), new Vector3( 4, 0, -10 ), hit, 0.5f, 0.2f );
			NearV( pos, new Vector3( 1, 2, 3 ), 1e-4f, "position snaps to the hit point" );
			NearV( vel, new Vector3( 3.2f, 0, 5 ), 1e-4f, "velocity: tangential 4*0.8, normal 10*0.5" );
		} );

		registry.Add( "particle-colliders", "resolve separating velocity is unchanged but position still snaps", () =>
		{
			var hit = new ColliderHit( true, new Vector3( 1, 2, 3 ), new Vector3( 0, 0, 1 ), 0.5f );
			var (pos, vel) = ParticleColliderResponse.Resolve( new Vector3( 9, 9, 9 ), new Vector3( 0, 0, 10 ), hit, 0.5f, 0.2f );
			NearV( pos, new Vector3( 1, 2, 3 ), 1e-4f, "position still snaps to the hit point" );
			NearV( vel, new Vector3( 0, 0, 10 ), 1e-4f, "separating velocity (vn >= 0) is untouched, matching Respond" );
		} );

		registry.Add( "particle-colliders", "resolve miss passes through and zero restitution plus full friction stops dead", () =>
		{
			var (pos, vel) = ParticleColliderResponse.Resolve( new Vector3( 1, 1, 1 ), new Vector3( 2, 2, 2 ), ColliderHit.Miss, 0.5f, 0.2f );
			NearV( pos, new Vector3( 1, 1, 1 ), 1e-4f, "miss keeps position" );
			NearV( vel, new Vector3( 2, 2, 2 ), 1e-4f, "miss keeps velocity" );
			var hit = new ColliderHit( true, new Vector3( 0, 0, 0 ), new Vector3( 0, 0, 1 ), 0.5f );
			var (_, stopped) = ParticleColliderResponse.Resolve( new Vector3( 0, 0, 0 ), new Vector3( 4, 3, -10 ), hit, 0f, 1f );
			NearV( stopped, new Vector3( 0, 0, 0 ), 1e-4f, "restitution 0 + friction 1 kills all velocity" );
		} );

		registry.Add( "particle-colliders", "resolve rejects out-of-range restitution and friction", () =>
		{
			var hit = new ColliderHit( true, new Vector3( 0, 0, 0 ), new Vector3( 0, 0, 1 ), 0.5f );
			Throws( () => ParticleColliderResponse.Resolve( new Vector3( 0, 0, 0 ), new Vector3( 0, 0, -1 ), hit, -0.1f, 0.2f ), "negative restitution" );
			Throws( () => ParticleColliderResponse.Resolve( new Vector3( 0, 0, 0 ), new Vector3( 0, 0, -1 ), hit, 0.5f, 1.1f ), "friction above 1" );
			Throws( () => ParticleColliderResponse.Resolve( new Vector3( 0, 0, 0 ), new Vector3( float.NaN, 0, 0 ), hit, 0.5f, 0.2f ), "NaN velocity" );
		} );

		// ---- Validate ----

		registry.Add( "particle-colliders", "validate rejects negative radii", () =>
		{
			Throws( () => new ParticleSphereCollider { Center = new Vector3( 0, 0, 0 ), Radius = -1 }.Validate(), "negative sphere radius" );
			Throws( () => new ParticleCapsuleCollider { A = new Vector3( 0, 0, 0 ), B = new Vector3( 0, 0, 1 ), Radius = -0.5f }.Validate(), "negative capsule radius" );
			Throws( () => new ParticleSphereCollider { Center = new Vector3( 0, 0, 0 ), Radius = 0 }.Sweep( new Vector3( 0, 0, -1 ), new Vector3( 0, 0, 1 ), -1 ), "negative particle radius in sweep" );
			// Zero is not negative: a zero-radius sphere is a valid point collider.
			new ParticleSphereCollider { Center = new Vector3( 0, 0, 0 ), Radius = 0 }.Validate();
		} );

		registry.Add( "particle-colliders", "validate rejects inverted boxes", () =>
		{
			Throws( () => new ParticleBoxCollider { Min = new Vector3( 5, -5, -5 ), Max = new Vector3( -5, 5, 5 ) }.Validate(), "Min.X > Max.X" );
			Throws( () => new ParticleBoxCollider { Min = new Vector3( -5, 5, -5 ), Max = new Vector3( 5, -5, 5 ) }.Validate(), "Min.Y > Max.Y" );
			// Equal corners are a flat slab, not inverted: validation passes.
			new ParticleBoxCollider { Min = new Vector3( 0, 0, 0 ), Max = new Vector3( 0, 5, 5 ) }.Validate();
		} );

		registry.Add( "particle-colliders", "validate rejects empty plane lists and zero-normal planes", () =>
		{
			Throws( () => new ParticlePlaneSetCollider().Validate(), "empty plane list" );
			var bad = new ParticlePlaneSetCollider();
			bad.Planes.Add( new ColliderPlane( new Vector3( 0, 0, 0 ), 0 ) );
			Throws( () => bad.Validate(), "zero-length plane normal" );
			var ok = Cube();
			ok.Validate();
		} );

		registry.Add( "particle-colliders", "validate rejects zero-length capsule segments", () =>
		{
			Throws( () => new ParticleCapsuleCollider { A = new Vector3( 1, 2, 3 ), B = new Vector3( 1, 2, 3 ), Radius = 1 }.Validate(), "degenerate capsule segment" );
			var ok = new ParticleCapsuleCollider { A = new Vector3( 0, 0, 0 ), B = new Vector3( 0, 0, 1 ), Radius = 1 };
			ok.Validate();
		} );

		registry.Add( "particle-colliders", "all four shapes share the IParticleCollider surface", () =>
		{
			IParticleCollider[] shapes =
			{
				new ParticleSphereCollider { Center = new Vector3( 0, 0, 0 ), Radius = 5 },
				new ParticleBoxCollider { Min = new Vector3( -5, -5, -5 ), Max = new Vector3( 5, 5, 5 ) },
				Cube(),
				new ParticleCapsuleCollider { A = new Vector3( 0, 0, -5 ), B = new Vector3( 0, 0, 5 ), Radius = 2 },
			};
			foreach ( var shape in shapes )
			{
				shape.Validate();
				Check( shape.Contains( new Vector3( 0, 0, 0 ), 0 ), "every shape must contain the origin" );
				Check( !shape.Sweep( new Vector3( 50, 50, 50 ), new Vector3( 60, 60, 60 ), 0 ).Hit, "every shape must miss a far-away segment" );
			}
		} );
	}
}
