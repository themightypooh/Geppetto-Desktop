using System.Numerics;
using Marionette.Desktop;

internal static class Particle2DSubTests
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
		Particle2DChildSpec Spec() => new()
		{
			ChildCount = 5, Shape = Particle2DSubShape.Point,
			SpeedMin = 10, SpeedMax = 20, LifetimeMin = 0.5f, LifetimeMax = 1.5f,
			VelocityInheritance = 0.5f,
		};
		Vector2 ParentPos() => new( 100, 200 );
		Vector2 ParentVel() => new( 40, -30 );

		registry.Add( "particle-2d-sub", "point shape children sit exactly at parent position", () =>
		{
			var rng = ParticleRng.For( 42, 0 );
			var kids = Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), Spec(), ref rng );
			Check( kids.Count == 5, $"expected 5 children, got {kids.Count}" );
			foreach ( var k in kids ) Check( k.Position == ParentPos(), $"point child at {k.Position}, want {ParentPos()}" );
		} );

		registry.Add( "particle-2d-sub", "ring shape children sit exactly on the burst circle", () =>
		{
			var spec = Spec();
			spec.Shape = Particle2DSubShape.Ring;
			spec.RingRadius = 25;
			var rng = ParticleRng.For( 42, 0 );
			var kids = Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), spec, ref rng );
			Check( kids.Count == 5, $"expected 5 children, got {kids.Count}" );
			foreach ( var k in kids ) Near( (k.Position - ParentPos()).Length(), 25, 1e-4f, "ring child distance from parent" );
			bool anyDiff = false;
			for ( int i = 1; i < kids.Count; i++ ) if ( kids[i].Position != kids[0].Position ) { anyDiff = true; break; }
			Check( anyDiff, "ring children all landed on the same spot — burst has no spread" );
		} );

		registry.Add( "particle-2d-sub", "ring radius 0 degenerates to a point", () =>
		{
			var spec = Spec();
			spec.Shape = Particle2DSubShape.Ring;
			spec.RingRadius = 0;
			var rng = ParticleRng.For( 7, 0 );
			var kids = Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), spec, ref rng );
			foreach ( var k in kids ) Check( k.Position == ParentPos(), $"radius-0 ring child at {k.Position}, want {ParentPos()}" );
		} );

		registry.Add( "particle-2d-sub", "inheritance 0 with zero own speed starts children still", () =>
		{
			var spec = Spec();
			spec.VelocityInheritance = 0;
			spec.SpeedMin = 0; spec.SpeedMax = 0;
			var rng = ParticleRng.For( 42, 0 );
			var kids = Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), spec, ref rng );
			foreach ( var k in kids ) Check( k.Velocity == Vector2.Zero, $"inheritance-0 child velocity {k.Velocity}, want (0, 0)" );
		} );

		registry.Add( "particle-2d-sub", "inheritance 0.5 with zero own speed keeps half the parent velocity", () =>
		{
			var spec = Spec();
			spec.VelocityInheritance = 0.5f;
			spec.SpeedMin = 0; spec.SpeedMax = 0;
			var rng = ParticleRng.For( 42, 0 );
			var kids = Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), spec, ref rng );
			foreach ( var k in kids ) Check( k.Velocity == ParentVel() * 0.5f, $"inheritance-0.5 child velocity {k.Velocity}, want {ParentVel() * 0.5f}" );
		} );

		registry.Add( "particle-2d-sub", "inheritance 1 with zero own speed keeps the full parent velocity", () =>
		{
			var spec = Spec();
			spec.VelocityInheritance = 1;
			spec.SpeedMin = 0; spec.SpeedMax = 0;
			var rng = ParticleRng.For( 42, 0 );
			var kids = Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), spec, ref rng );
			foreach ( var k in kids ) Check( k.Velocity == ParentVel(), $"inheritance-1 child velocity {k.Velocity}, want {ParentVel()}" );
		} );

		registry.Add( "particle-2d-sub", "inheritance plus own speed: residual speed stays in range", () =>
		{
			var spec = Spec();
			spec.VelocityInheritance = 0.5f;
			spec.SpeedMin = 10; spec.SpeedMax = 20;
			var rng = ParticleRng.For( 42, 0 );
			var kids = Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), spec, ref rng );
			foreach ( var k in kids )
			{
				float residual = (k.Velocity - ParentVel() * 0.5f).Length();
				Check( residual >= 10 - 1e-4f && residual <= 20 + 1e-4f, $"residual speed {residual} outside [10, 20]" );
			}
		} );

		registry.Add( "particle-2d-sub", "lifetimes stay in range, fixed range is exact", () =>
		{
			var rng = ParticleRng.For( 42, 0 );
			var kids = Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), Spec(), ref rng );
			foreach ( var k in kids ) Check( k.Lifetime >= 0.5f && k.Lifetime <= 1.5f, $"lifetime {k.Lifetime} outside [0.5, 1.5]" );
			var fixedSpec = Spec();
			fixedSpec.LifetimeMin = 2; fixedSpec.LifetimeMax = 2;
			var rng2 = ParticleRng.For( 42, 0 );
			foreach ( var k in Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), fixedSpec, ref rng2 ) )
				Check( k.Lifetime == 2, $"fixed lifetime {k.Lifetime}, want 2" );
		} );

		registry.Add( "particle-2d-sub", "child count exact, zero count spawns an empty list", () =>
		{
			var rng = ParticleRng.For( 42, 0 );
			Check( Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), Spec(), ref rng ).Count == 5, "count 5 not honoured" );
			var zero = Spec();
			zero.ChildCount = 0;
			var rng2 = ParticleRng.For( 42, 0 );
			Check( Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), zero, ref rng2 ).Count == 0, "count 0 did not give an empty list" );
		} );

		registry.Add( "particle-2d-sub", "same seed gives bit-identical children", () =>
		{
			var spec = Spec();
			spec.Shape = Particle2DSubShape.Ring;
			spec.RingRadius = 25;
			var rngA = ParticleRng.For( 1234, 0 );
			var rngB = ParticleRng.For( 1234, 0 );
			var a = Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), spec, ref rngA );
			var b = Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), spec, ref rngB );
			Check( a.Count == b.Count, "determinism run lengths differ" );
			for ( int i = 0; i < a.Count; i++ )
			{
				Check( a[i].Position == b[i].Position, $"child {i} position differs across same-seed runs" );
				Check( a[i].Velocity == b[i].Velocity, $"child {i} velocity differs across same-seed runs" );
				Check( a[i].Lifetime == b[i].Lifetime, $"child {i} lifetime differs across same-seed runs" );
				Check( a[i].Depth == b[i].Depth, $"child {i} depth differs across same-seed runs" );
			}
		} );

		registry.Add( "particle-2d-sub", "different seed gives different children", () =>
		{
			var spec = Spec();
			spec.Shape = Particle2DSubShape.Ring;
			spec.RingRadius = 25;
			var rngA = ParticleRng.For( 1, 0 );
			var rngB = ParticleRng.For( 2, 0 );
			var a = Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), spec, ref rngA );
			var b = Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), spec, ref rngB );
			bool anyDiff = false;
			for ( int i = 0; i < a.Count; i++ ) if ( a[i].Position != b[i].Position || a[i].Velocity != b[i].Velocity ) { anyDiff = true; break; }
			Check( anyDiff, "different seeds produced identical children — seed is ignored" );
		} );

		registry.Add( "particle-2d-sub", "default max depth 1: depth-0 spawns depth-1, depth-1 throws", () =>
		{
			var spec = Spec();
			Check( spec.MaxDepth == 1, $"default MaxDepth is {spec.MaxDepth}, want 1" );
			var rng = ParticleRng.For( 42, 0 );
			var kids = Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), spec, ref rng, 0 );
			foreach ( var k in kids ) Check( k.Depth == 1, $"child depth {k.Depth}, want 1" );
			Check( !spec.ChildMaySpawn( 1 ), "ChildMaySpawn(1) must be false at MaxDepth 1" );
			var rng2 = ParticleRng.For( 42, 0 );
			Throws( () => Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), spec, ref rng2, 1 ),
				"depth-1 parent at MaxDepth 1 spawning grandchildren" );
			var rng3 = ParticleRng.For( 42, 0 );
			Throws( () => Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), spec, ref rng3, -1 ),
				"negative parent depth" );
		} );

		registry.Add( "particle-2d-sub", "deeper spec allows grandchildren, then stops", () =>
		{
			var spec = Spec();
			spec.MaxDepth = 2;
			var rng = ParticleRng.For( 42, 0 );
			var kids = Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), spec, ref rng, 1 );
			foreach ( var k in kids ) Check( k.Depth == 2, $"grandchild depth {k.Depth}, want 2" );
			var rng2 = ParticleRng.For( 42, 0 );
			Throws( () => Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), spec, ref rng2, 2 ),
				"depth-2 parent at MaxDepth 2 spawning great-grandchildren" );
		} );

		registry.Add( "particle-2d-sub", "Validate rejects every bad spec loudly", () =>
		{
			// Each case mutates a fresh local (structs copy on pass, so a
			// mutate-by-lambda helper would silently test the valid spec).
			Particle2DChildSpec bad;
			bad = Spec(); bad.ChildCount = -1; Throws( () => bad.Validate(), "negative child count" );
			bad = Spec(); bad.SpeedMin = -5; Throws( () => bad.Validate(), "negative speed min" );
			bad = Spec(); bad.SpeedMax = 5; Throws( () => bad.Validate(), "inverted speed range (max below min)" );
			bad = Spec(); bad.LifetimeMin = -1; Throws( () => bad.Validate(), "negative lifetime min" );
			bad = Spec(); bad.LifetimeMin = 0; Throws( () => bad.Validate(), "zero lifetime min" );
			bad = Spec(); bad.LifetimeMax = 0.1f; Throws( () => bad.Validate(), "inverted lifetime range (max below min)" );
			bad = Spec(); bad.VelocityInheritance = -0.1f; Throws( () => bad.Validate(), "inheritance below 0" );
			bad = Spec(); bad.VelocityInheritance = 1.1f; Throws( () => bad.Validate(), "inheritance above 1" );
			bad = Spec(); bad.RingRadius = -3; Throws( () => bad.Validate(), "negative ring radius" );
			bad = Spec(); bad.MaxDepth = 0; Throws( () => bad.Validate(), "MaxDepth below 1" );
			bad = Spec(); bad.Shape = (Particle2DSubShape)99; Throws( () => bad.Validate(), "unknown (empty) emitter shape" );
			bad = Spec(); bad.SpeedMin = float.NaN; Throws( () => bad.Validate(), "NaN speed" );
		} );

		registry.Add( "particle-2d-sub", "SpawnChildren itself throws on an invalid spec", () =>
		{
			var spec = Spec();
			spec.ChildCount = -3;
			var rng = ParticleRng.For( 42, 0 );
			Throws( () => Particle2DSubSpawner.SpawnChildren( ParentPos(), ParentVel(), spec, ref rng ),
				"spawn with negative child count" );
		} );

		registry.Add( "particle-2d-sub", "non-finite parent position or velocity throws", () =>
		{
			var rng = ParticleRng.For( 42, 0 );
			Throws( () => Particle2DSubSpawner.SpawnChildren( new Vector2( float.NaN, 0 ), ParentVel(), Spec(), ref rng ),
				"NaN parent position" );
			var rng2 = ParticleRng.For( 42, 0 );
			Throws( () => Particle2DSubSpawner.SpawnChildren( ParentPos(), new Vector2( 0, float.PositiveInfinity ), Spec(), ref rng2 ),
				"infinite parent velocity" );
		} );
	}
}
