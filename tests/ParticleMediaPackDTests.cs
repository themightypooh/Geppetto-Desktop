using System.Numerics;
using Marionette.Desktop;

// Lane P54 (particles session, 2026-10-24): medium preset pack D.
// Same harness shape as ParticleMediaTests: registry.Add("particle-media-pack-d", name, body).
// Sweep line (NOT added to the canonical tree — coordinator sweep only):
//   ParticleMediaPackDTests.Register( registry, settings );
// placed immediately after the ParticleMediaTests.Register line in TestRegistry.cs.
// (Note: canonical TestRegistry.cs has no ParticleMediaTests line yet — P38's
// own sweep line goes in first, after GpuParticleSystemTests.Register, then
// this line after it.)
// Hookup (coordinator sweep only, NOT made here): callers use
// ParticleMediaPackD.Vacuum / .Helium / .Honey / .SmokeFilledRoom directly
// with the SAME MediumBundle.ApplyTo P38 uses —
//   var s = MediumBundle.ApplyTo( baseSettings, ParticleMediaPackD.Honey );
// No edit to ParticleMedia.cs is needed for that path.
internal static class ParticleMediaPackDTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void NearVec( Vector3 got, Vector3 want, float tol, string what )
		{
			if ( MathF.Abs( got.X - want.X ) > tol || MathF.Abs( got.Y - want.Y ) > tol || MathF.Abs( got.Z - want.Z ) > tol )
				throw new Exception( $"{what}: got {got}, want {want} ± {tol}" );
		}
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; }
			catch ( InvalidOperationException ) { return; }
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}

		// One burst particle through the REAL ParticleSystem, no ground, no wind.
		static ParticleSystem OneParticle( MediumBundle bundle, Vector3 direction, float speed, uint seed = 7 )
		{
			var applied = MediumBundle.ApplyTo( new ParticleSystemSettings { Capacity = 100, Seed = seed }, bundle );
			var sys = new ParticleSystem( applied );
			sys.AddEmitter( new ParticleEmitter
			{
				Position = new Vector3( 0, 0, 0 ), Direction = direction, Speed = speed, SpreadDegrees = 0,
				LifetimeMin = 5f, LifetimeMax = 5f, SizeMin = 1f, SizeMax = 1f,
				Bursts = new System.Collections.Generic.List<ParticleBurst> { new( 0, 1 ) },
			} );
			return sys;
		}

		registry.Add( "particle-media-pack-d", "vacuum bundle field values, distinct from space", () =>
		{
			var b = ParticleMediaPackD.Vacuum;
			NearVec( b.Gravity, Vector3.Zero, 0f, "vacuum gravity must be exactly zero" );
			Near( b.DragLinear, 0f, 0f, "vacuum dragLinear must be exactly zero" );
			Near( b.DragQuadratic, 0f, 0f, "vacuum dragQuadratic must be exactly zero" );
			Near( b.Buoyancy, 0f, 0f, "vacuum buoyancy" );
			Near( b.Density, 1e-9f, 0f, "vacuum density" );
			NearVec( b.NetGravity(), Vector3.Zero, 0f, "vacuum net gravity" );
			// Vacuum is the idealized limit; space is a medium with residual stuff.
			var s = MediumBundle.Space;
			Check( b.Gravity != s.Gravity, "vacuum gravity must differ from space micro-gravity" );
			Check( b.DragLinear < s.DragLinear, $"vacuum dragLinear {b.DragLinear} must be below space {s.DragLinear}" );
			Check( b.Density < s.Density, $"vacuum density {b.Density} must be below space {s.Density}" );
			Check( b.Gravity != s.Gravity || b.DragLinear != s.DragLinear || b.Density != s.Density,
				"vacuum and space must not be field-identical" );
		} );

		registry.Add( "particle-media-pack-d", "helium bundle field values and net lift", () =>
		{
			var b = ParticleMediaPackD.Helium;
			NearVec( b.Gravity, new Vector3( 0, 0, -386.0886f ), 1e-3f, "helium gravity" );
			Near( b.DragLinear, 0.02f, 1e-6f, "helium dragLinear" );
			Near( b.DragQuadratic, 0.0002f, 1e-6f, "helium dragQuadratic" );
			Near( b.Buoyancy, 410f, 1e-6f, "helium buoyancy" );
			Near( b.Density, 0.1664f, 1e-6f, "helium density" );
			NearVec( b.NetGravity(), new Vector3( 0, 0, 23.9114f ), 1e-3f, "helium net gravity" );
			Check( b.NetGravity().Z > 0f, "helium net gravity must be positive (lifting)" );
			Check( b.NetGravity().Z < MediumBundle.Water.NetGravity().Z,
				$"helium net {b.NetGravity().Z} must be weaker than water net {MediumBundle.Water.NetGravity().Z}" );
			Check( b.DragLinear < MediumBundle.Air.DragLinear, "helium dragLinear must be below air's" );
			Check( b.DragQuadratic < MediumBundle.Air.DragQuadratic, "helium dragQuadratic must be below air's" );
		} );

		registry.Add( "particle-media-pack-d", "honey bundle field values and terminal velocity", () =>
		{
			var b = ParticleMediaPackD.Honey;
			NearVec( b.Gravity, new Vector3( 0, 0, -386.0886f ), 1e-3f, "honey gravity" );
			Near( b.DragLinear, 200f, 1e-6f, "honey dragLinear" );
			Near( b.DragQuadratic, 5.0f, 1e-6f, "honey dragQuadratic" );
			Near( b.Buoyancy, 0f, 0f, "honey buoyancy is the low end: drag, not lift" );
			Near( b.Density, 1420f, 1e-6f, "honey density" );
			Near( b.DragLinear, MediumBundle.Water.DragLinear * 100f, 1e-6f, "honey dragLinear must be 100x water's" );
			Near( b.DragQuadratic, MediumBundle.Water.DragQuadratic * 100f, 1e-6f, "honey dragQuadratic must be 100x water's" );
			// Terminal speed from the bundle's own fields: 5v^2 + 200v - 386.0886 = 0.
			float term = ( -b.DragLinear + MathF.Sqrt( b.DragLinear * b.DragLinear + 4f * b.DragQuadratic * 386.0886f ) ) / ( 2f * b.DragQuadratic );
			float termLinearOnly = 386.0886f / b.DragLinear;
			Console.WriteLine( $"honey terminal: linear+quadratic={term} in/s, linear-only={termLinearOnly} in/s" );
			Check( term > 1.80f && term < 1.90f, $"honey linear+quadratic terminal must be ~1.845 in/s, got {term}" );
			Check( termLinearOnly > 1.92f && termLinearOnly < 1.94f, $"honey linear-only terminal must be ~1.9304 in/s, got {termLinearOnly}" );
		} );

		registry.Add( "particle-media-pack-d", "smoke-filled room bundle field values, drags only", () =>
		{
			var b = ParticleMediaPackD.SmokeFilledRoom;
			NearVec( b.Gravity, MediumBundle.Air.Gravity, 0f, "smoke-room gravity must equal air's" );
			Near( b.DragLinear, 0.5f, 1e-6f, "smoke-room dragLinear" );
			Near( b.DragQuadratic, 0.005f, 1e-6f, "smoke-room dragQuadratic" );
			Near( b.Buoyancy, 0f, 0f, "smoke-room buoyancy" );
			Near( b.Density, 1.25f, 1e-6f, "smoke-room density" );
			Near( b.DragLinear, MediumBundle.Air.DragLinear * 10f, 1e-6f, "smoke-room dragLinear must be 10x air's" );
			Near( b.DragQuadratic, MediumBundle.Air.DragQuadratic * 10f, 1e-6f, "smoke-room dragQuadratic must be 10x air's" );
			Check( b.Density > 1.0f && b.Density < 1.5f, $"smoke-room density {b.Density} must stay air-like" );
			// No turbulence field exists on the bundle by construction: the
			// bundle shape is P38's five fields, and turbulence lives on settings.
			Check( b.NetGravity().Z == MediumBundle.Air.NetGravity().Z, "smoke-room net must equal air's (no lift smuggled in)" );
		} );

		registry.Add( "particle-media-pack-d", "names are lowercase-underscore and collision-free", () =>
		{
			var names = ParticleMediaPackD.Names;
			Check( names.Length == 4, $"expected exactly 4 bundle names, got {names.Length}" );
			foreach ( var n in new[] { "vacuum", "helium", "honey", "smoke_filled_room" } )
				Check( System.Array.IndexOf( names, n ) >= 0, $"bundle name '{n}' missing" );
			foreach ( var n in names )
			{
				Check( n == n.ToLowerInvariant() && !n.Contains( ' ' ) && !n.Contains( '-' ),
					$"bundle name '{n}' must be lowercase underscore" );
			}
			foreach ( var old in new[] { "air", "water", "space", "zero_g" } )
				Check( System.Array.IndexOf( names, old ) < 0, $"bundle name collides with P38 bundle '{old}'" );
			foreach ( var p in ParticleEmitterPresets.Names )
				Check( System.Array.IndexOf( names, p ) < 0, $"bundle name collides with preset '{p}'" );
		} );

		registry.Add( "particle-media-pack-d", "ByName round-trips and rejects unknowns", () =>
		{
			foreach ( var n in ParticleMediaPackD.Names )
			{
				var byName = ParticleMediaPackD.ByName( n );
				var direct = n switch
				{
					"vacuum" => ParticleMediaPackD.Vacuum,
					"helium" => ParticleMediaPackD.Helium,
					"honey" => ParticleMediaPackD.Honey,
					_ => ParticleMediaPackD.SmokeFilledRoom,
				};
				NearVec( byName.Gravity, direct.Gravity, 0f, $"ByName({n}) gravity" );
				Near( byName.DragLinear, direct.DragLinear, 0f, $"ByName({n}) dragLinear" );
				Near( byName.DragQuadratic, direct.DragQuadratic, 0f, $"ByName({n}) dragQuadratic" );
				Near( byName.Buoyancy, direct.Buoyancy, 0f, $"ByName({n}) buoyancy" );
				Near( byName.Density, direct.Density, 0f, $"ByName({n}) density" );
			}
			Throws( () => ParticleMediaPackD.ByName( "air" ), "ByName must reject P38 names" );
			Throws( () => ParticleMediaPackD.ByName( "smoke" ), "ByName must reject preset names" );
			Throws( () => ParticleMediaPackD.ByName( "VACUUM" ), "ByName is exact-match, not case-insensitive" );
			Throws( () => ParticleMediaPackD.ByName( "" ), "ByName must reject empty names" );
		} );

		registry.Add( "particle-media-pack-d", "hostile-base override for all four bundles", () =>
		{
			// Scene gravity deliberately hostile: if ApplyTo added, vacuum
			// would give -1000, helium -976.0886, honey/smoke -1386.0886.
			var scene = new ParticleSystemSettings { Gravity = new Vector3( 0, 0, -1000f ), Drag = 9f, Wind = new Vector3( 1, 2, 3 ) };
			foreach ( var bundle in new[] { ParticleMediaPackD.Vacuum, ParticleMediaPackD.Helium, ParticleMediaPackD.Honey, ParticleMediaPackD.SmokeFilledRoom } )
			{
				var got = MediumBundle.ApplyTo( scene, bundle );
				NearVec( got.Gravity, bundle.NetGravity(), 1e-3f, "ApplyTo gravity must be bundle net, never scene + bundle" );
				Near( got.Drag, bundle.DragLinear, 1e-6f, "ApplyTo drag must be bundle DragLinear" );
				NearVec( got.Wind, new Vector3( 1, 2, 3 ), 0f, "wind must survive ApplyTo" );
			}
			NearVec( MediumBundle.ApplyTo( scene, ParticleMediaPackD.Vacuum ).Gravity, Vector3.Zero, 0f, "vacuum hostile-base net" );
			NearVec( MediumBundle.ApplyTo( scene, ParticleMediaPackD.Helium ).Gravity, new Vector3( 0, 0, 23.9114f ), 1e-3f, "helium hostile-base net" );
		} );

		registry.Add( "particle-media-pack-d", "ApplyTo is pure for pack bundles", () =>
		{
			var scene = new ParticleSystemSettings { Gravity = new Vector3( 0, 0, -1000f ), Drag = 9f };
			var got = MediumBundle.ApplyTo( scene, ParticleMediaPackD.Honey );
			NearVec( scene.Gravity, new Vector3( 0, 0, -1000f ), 0f, "input gravity must not move" );
			Near( scene.Drag, 9f, 0f, "input drag must not move" );
			Check( !ReferenceEquals( got, scene ), "ApplyTo must return a new object" );
		} );

		registry.Add( "particle-media-pack-d", "all four bundles pass P38 validation", () =>
		{
			ParticleMediaPackD.Vacuum.Validate();
			ParticleMediaPackD.Helium.Validate();
			ParticleMediaPackD.Honey.Validate();
			ParticleMediaPackD.SmokeFilledRoom.Validate();
		} );

		registry.Add( "particle-media-pack-d", "validation rejects bad bundle variants", () =>
		{
			var g = new Vector3( 0, 0, -386f );
			Throws( () => new MediumBundle( g, -0.1f, 0.0005f, 0f, 1.2f ), "dragLinear negative" );
			Throws( () => new MediumBundle( g, 0.05f, -0.5f, 0f, 1.2f ), "dragQuadratic negative" );
			Throws( () => new MediumBundle( g, 0.05f, 0.0005f, -1f, 1.2f ), "buoyancy negative" );
			Throws( () => new MediumBundle( g, 0.05f, 0.0005f, 2001f, 1.2f ), "buoyancy above 2000" );
			Throws( () => new MediumBundle( g, 0.05f, 0.0005f, 0f, 0f ), "density zero" );
			Throws( () => new MediumBundle( new Vector3( 0, 0, float.NaN ), 0.05f, 0.0005f, 0f, 1.2f ), "gravity NaN" );
			// ApplyTo re-validates: a default bundle (density 0) must fail there too.
			Throws( () => MediumBundle.ApplyTo( new ParticleSystemSettings(), default ), "ApplyTo must reject a default bundle" );
		} );

		registry.Add( "particle-media-pack-d", "honey stops fast, air keeps going, real sim", () =>
		{
			var honey = OneParticle( ParticleMediaPackD.Honey, new Vector3( 1, 0, 0 ), 100f );
			honey.Step( 60 ); // 1 s at 60 Hz
			Check( honey.AliveCount == 1, $"expected 1 honey particle alive, got {honey.AliveCount}" );
			var hs = honey.States[0];
			var air = OneParticle( MediumBundle.Air, new Vector3( 1, 0, 0 ), 100f );
			air.Step( 60 );
			var as_ = air.States[0];
			Console.WriteLine( $"honey vs air after 1s at 100 in/s: honey x={hs.Position.X} vx={hs.Velocity.X}, air x={as_.Position.X}" );
			Check( hs.Position.X < 2f, $"honey particle must stop within 2 in, travelled {hs.Position.X}" );
			Check( MathF.Abs( hs.Velocity.X ) < 0.01f, $"honey horizontal speed must be ~0, got vx={hs.Velocity.X}" );
			Check( as_.Position.X > 50f, $"air particle must travel > 50 in, travelled {as_.Position.X}" );
			Check( hs.Position.X < as_.Position.X, "honey must travel less than air" );
		} );

		registry.Add( "particle-media-pack-d", "helium rises through the real sim", () =>
		{
			var sys = OneParticle( ParticleMediaPackD.Helium, new Vector3( 0, 0, 1 ), 0f );
			sys.Step( 60 ); // 1 s at 60 Hz
			Check( sys.AliveCount == 1, $"expected 1 helium particle alive, got {sys.AliveCount}" );
			var st = sys.States[0];
			Console.WriteLine( $"helium rise after 1s: z={st.Position.Z} vz={st.Velocity.Z}" );
			Check( st.Position.Z > 8f && st.Position.Z < 16f, $"helium particle must rise 8..16 in in 1 s, got z={st.Position.Z}" );
			Check( st.Velocity.Z > 10f, $"helium velocity must point up strongly, got vz={st.Velocity.Z}" );
		} );

		registry.Add( "particle-media-pack-d", "vacuum flight is exactly straight, real sim", () =>
		{
			var sys = OneParticle( ParticleMediaPackD.Vacuum, new Vector3( 1, 0, 0 ), 10f );
			sys.Step( 60 ); // 1 s at 60 Hz
			Check( sys.AliveCount == 1, $"expected 1 vacuum particle alive, got {sys.AliveCount}" );
			var st = sys.States[0];
			Console.WriteLine( $"vacuum drift after 1s: x={st.Position.X} z={st.Position.Z}" );
			Check( st.Position.X > 9f && st.Position.X < 11f, $"10 in/s for 1 s must land x in 9..11, got {st.Position.X}" );
			Check( MathF.Abs( st.Position.Z ) < 0.01f, $"exactly zero gravity must keep |z| < 0.01, got {st.Position.Z}" );
		} );

		registry.Add( "particle-media-pack-d", "smoke-filled room drags more than air, real sim", () =>
		{
			var room = OneParticle( ParticleMediaPackD.SmokeFilledRoom, new Vector3( 1, 0, 0 ), 100f );
			room.Step( 60 );
			var air = OneParticle( MediumBundle.Air, new Vector3( 1, 0, 0 ), 100f );
			air.Step( 60 );
			var rs = room.States[0];
			var as_ = air.States[0];
			Console.WriteLine( $"smoke-room vs air after 1s at 100 in/s: room x={rs.Position.X}, air x={as_.Position.X}" );
			Check( rs.Position.X > 60f && rs.Position.X < 95f, $"smoke-room particle must travel 60..95 in, travelled {rs.Position.X}" );
			Check( rs.Position.X < as_.Position.X, "smoke-room must travel less than air" );
		} );

		registry.Add( "particle-media-pack-d", "honey drop crawls, air drop falls, real sim", () =>
		{
			var honey = OneParticle( ParticleMediaPackD.Honey, new Vector3( 0, 0, 1 ), 0f );
			honey.Step( 60 );
			var air = OneParticle( MediumBundle.Air, new Vector3( 0, 0, 1 ), 0f );
			air.Step( 60 );
			var hz = honey.States[0].Position.Z;
			var az = air.States[0].Position.Z;
			Console.WriteLine( $"drop after 1s from rest: honey z={hz} air z={az}" );
			Check( hz < 0f && hz > -2.5f, $"honey drop must crawl to z in -2.5..0, got {hz}" );
			Check( az < -100f, $"air drop must fall past -100 in, got {az}" );
			Check( MathF.Abs( hz ) < MathF.Abs( az ) * 0.05f, $"honey drop |{hz}| must be < 5% of air drop |{az}|" );
		} );

		registry.Add( "particle-media-pack-d", "same-seed runs are bit-identical", () =>
		{
			foreach ( var bundle in new[] { ParticleMediaPackD.Helium, ParticleMediaPackD.Honey } )
			{
				var a = OneParticle( bundle, new Vector3( 1, 0, 0 ), 50f, 7 );
				var b = OneParticle( bundle, new Vector3( 1, 0, 0 ), 50f, 7 );
				a.Step( 60 );
				b.Step( 60 );
				Check( a.AliveCount == b.AliveCount, "same-seed alive counts must match" );
				for ( int i = 0; i < a.AliveCount; i++ )
				{
					Check( a.States[i].Position == b.States[i].Position,
						$"same-seed positions must be bit-identical, got {a.States[i].Position} vs {b.States[i].Position}" );
					Check( a.States[i].Velocity == b.States[i].Velocity,
						$"same-seed velocities must be bit-identical, got {a.States[i].Velocity} vs {b.States[i].Velocity}" );
				}
			}
		} );
	}
}
