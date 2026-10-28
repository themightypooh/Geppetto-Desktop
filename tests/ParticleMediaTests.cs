using System.Numerics;
using Marionette.Desktop;

// Lane P38 (particles session, 2026-10-24): medium bundles.
// Same harness shape as Particle2DTests: registry.Add("particle-media", name, body).
// Sweep line (NOT added to the canonical tree — coordinator sweep only):
//   ParticleMediaTests.Register( registry, settings );
// placed after the GpuParticleSystemTests.Register line in TestRegistry.cs.
// SettingsFor hookup (coordinator sweep only, NOT made here): if presets are
// to resolve through bundles, SettingsFor's "bubbles" case becomes
//   var m = MediumBundle.ApplyTo( s, MediumBundle.Water ); s = m; // then turbulence
// i.e. ApplyTo first, then the preset's turbulence/wind tweaks — never an edit
// to ParticleSystem.cs by this lane.
internal static class ParticleMediaTests
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

		registry.Add( "particle-media", "air bundle field values", () =>
		{
			var b = MediumBundle.Air;
			NearVec( b.Gravity, new Vector3( 0, 0, -386.0886f ), 1e-3f, "air gravity" );
			Near( b.DragLinear, 0.05f, 1e-6f, "air dragLinear" );
			Near( b.DragQuadratic, 0.0005f, 1e-6f, "air dragQuadratic" );
			Near( b.Buoyancy, 0f, 0f, "air buoyancy" );
			Near( b.Density, 1.2f, 1e-6f, "air density" );
		} );

		registry.Add( "particle-media", "water bundle field values", () =>
		{
			var b = MediumBundle.Water;
			NearVec( b.Gravity, new Vector3( 0, 0, -386.0886f ), 1e-3f, "water gravity" );
			Near( b.DragLinear, 2.0f, 1e-6f, "water dragLinear" );
			Near( b.DragQuadratic, 0.05f, 1e-6f, "water dragQuadratic" );
			Near( b.Buoyancy, 450f, 1e-6f, "water buoyancy" );
			Near( b.Density, 1000f, 1e-6f, "water density" );
		} );

		registry.Add( "particle-media", "space bundle is near-zero, not zero", () =>
		{
			var b = MediumBundle.Space;
			Check( b.Gravity.Length() <= 0.1f, $"space gravity magnitude {b.Gravity.Length()} exceeds near-zero bound 0.1" );
			Check( b.DragLinear <= 0.01f, $"space dragLinear {b.DragLinear} exceeds near-zero bound 0.01" );
			Check( b.Density <= 1e-3f, $"space density {b.Density} exceeds near-vacuum bound 1e-3" );
			Check( b.Gravity.Length() > 0f || b.DragLinear > 0f, "space bundle must not be exactly nothing" );
		} );

		registry.Add( "particle-media", "zero-G is exactly zero gravity with air-like drag", () =>
		{
			var b = MediumBundle.ZeroG;
			NearVec( b.Gravity, Vector3.Zero, 0f, "zero-G gravity must be exactly zero" );
			Check( b.DragLinear == MediumBundle.Air.DragLinear, $"zero-G dragLinear {b.DragLinear} != air {MediumBundle.Air.DragLinear}" );
			Check( b.DragQuadratic == MediumBundle.Air.DragQuadratic, "zero-G dragQuadratic != air" );
			Check( b.Density == MediumBundle.Air.Density, "zero-G density != air" );
			Check( b.Buoyancy == 0f, "zero-G buoyancy must be 0" );
		} );

		registry.Add( "particle-media", "net gravity folds buoyancy, per bundle", () =>
		{
			NearVec( MediumBundle.Water.NetGravity(), new Vector3( 0, 0, 63.9114f ), 1e-3f, "water net gravity" );
			NearVec( MediumBundle.Air.NetGravity(), new Vector3( 0, 0, -386.0886f ), 1e-3f, "air net gravity" );
			NearVec( MediumBundle.Space.NetGravity(), new Vector3( 0, 0, -0.01f ), 1e-6f, "space net gravity" );
			NearVec( MediumBundle.ZeroG.NetGravity(), Vector3.Zero, 0f, "zero-G net gravity" );
		} );

		registry.Add( "particle-media", "ApplyTo overrides scene gravity, never adds to it", () =>
		{
			// Scene gravity deliberately non-default: if ApplyTo added, the
			// result would be -1000 + 63.9114 = -936.0886, not +63.9114.
			var scene = new ParticleSystemSettings { Gravity = new Vector3( 0, 0, -1000f ), Drag = 9f, Wind = new Vector3( 1, 2, 3 ) };
			var got = MediumBundle.ApplyTo( scene, MediumBundle.Water );
			NearVec( got.Gravity, new Vector3( 0, 0, 63.9114f ), 1e-3f, "water ApplyTo gravity must be bundle net, not scene + bundle" );
			Near( got.Drag, 2.0f, 1e-6f, "water ApplyTo drag" );
			NearVec( got.Wind, new Vector3( 1, 2, 3 ), 0f, "wind must survive ApplyTo" );
		} );

		registry.Add( "particle-media", "ApplyTo preserves every other field", () =>
		{
			var scene = new ParticleSystemSettings
			{
				Capacity = 500, Seed = 42, FixedDeltaTime = 1f / 30f, Substeps = 2,
				Gravity = new Vector3( 0, 0, -100f ), Wind = new Vector3( 3, 4, 5 ), Drag = 7f,
				TurbulenceStrength = 7f, TurbulenceFrequency = 0.2f, TurbulenceSpeed = 1.5f,
				VortexStrength = 2f, VortexCenter = new Vector3( 1, 2, 3 ), VortexAxis = new Vector3( 0, 1, 0 ), VortexRadius = 2.5f,
				GroundEnabled = true, GroundNormal = new Vector3( 0, 0, 1 ), GroundOffset = 1f,
				CollisionRadius = 0.05f, Restitution = 0.5f, Friction = 0.1f,
				RenderType = ParticleRenderType.MeshInstance, StrandPoints = 4,
			};
			var got = MediumBundle.ApplyTo( scene, MediumBundle.Air );
			Check( got.Capacity == 500 && got.Seed == 42, "capacity/seed must survive" );
			Near( got.FixedDeltaTime, 1f / 30f, 1e-6f, "fixed step must survive" );
			Check( got.Substeps == 2, "substeps must survive" );
			NearVec( got.Wind, new Vector3( 3, 4, 5 ), 0f, "wind must survive" );
			Near( got.TurbulenceStrength, 7f, 1e-6f, "turbulence strength must survive" );
			Near( got.TurbulenceFrequency, 0.2f, 1e-6f, "turbulence frequency must survive" );
			Near( got.TurbulenceSpeed, 1.5f, 1e-6f, "turbulence speed must survive" );
			Near( got.VortexStrength, 2f, 1e-6f, "vortex strength must survive" );
			NearVec( got.VortexCenter, new Vector3( 1, 2, 3 ), 0f, "vortex centre must survive" );
			NearVec( got.VortexAxis, new Vector3( 0, 1, 0 ), 0f, "vortex axis must survive" );
			Near( got.VortexRadius, 2.5f, 1e-6f, "vortex radius must survive" );
			Check( got.GroundEnabled && got.GroundOffset == 1f, "ground settings must survive" );
			Near( got.CollisionRadius, 0.05f, 1e-6f, "collision radius must survive" );
			Near( got.Restitution, 0.5f, 1e-6f, "restitution must survive" );
			Near( got.Friction, 0.1f, 1e-6f, "friction must survive" );
			Check( got.RenderType == ParticleRenderType.MeshInstance && got.StrandPoints == 4, "render settings must survive" );
			NearVec( got.Gravity, new Vector3( 0, 0, -386.0886f ), 1e-3f, "air gravity must replace scene gravity" );
			Near( got.Drag, 0.05f, 1e-6f, "air drag must replace scene drag" );
		} );

		registry.Add( "particle-media", "ApplyTo is pure, input untouched", () =>
		{
			var scene = new ParticleSystemSettings { Gravity = new Vector3( 0, 0, -1000f ), Drag = 9f };
			var got = MediumBundle.ApplyTo( scene, MediumBundle.Water );
			NearVec( scene.Gravity, new Vector3( 0, 0, -1000f ), 0f, "input gravity must not move" );
			Near( scene.Drag, 9f, 0f, "input drag must not move" );
			Check( !ReferenceEquals( got, scene ), "ApplyTo must return a new object" );
		} );

		registry.Add( "particle-media", "validation rejects bad density", () =>
		{
			Throws( () => new MediumBundle( new Vector3( 0, 0, -386f ), 0.05f, 0.0005f, 0f, 0f ), "density 0" );
			Throws( () => new MediumBundle( new Vector3( 0, 0, -386f ), 0.05f, 0.0005f, 0f, -5f ), "density negative" );
			Throws( () => new MediumBundle( new Vector3( 0, 0, -386f ), 0.05f, 0.0005f, 0f, float.NaN ), "density NaN" );
		} );

		registry.Add( "particle-media", "validation rejects bad drag coefficients", () =>
		{
			Throws( () => new MediumBundle( new Vector3( 0, 0, -386f ), -0.1f, 0.0005f, 0f, 1.2f ), "dragLinear negative" );
			Throws( () => new MediumBundle( new Vector3( 0, 0, -386f ), 0.05f, -0.5f, 0f, 1.2f ), "dragQuadratic negative" );
			Throws( () => new MediumBundle( new Vector3( 0, 0, -386f ), float.NaN, 0.0005f, 0f, 1.2f ), "dragLinear NaN" );
		} );

		registry.Add( "particle-media", "validation rejects bad buoyancy and non-finite gravity", () =>
		{
			Throws( () => new MediumBundle( new Vector3( 0, 0, -386f ), 0.05f, 0.0005f, -1f, 1.2f ), "buoyancy negative" );
			Throws( () => new MediumBundle( new Vector3( 0, 0, -386f ), 0.05f, 0.0005f, 2001f, 1.2f ), "buoyancy above 2000" );
			Throws( () => new MediumBundle( new Vector3( 0, 0, float.NaN ), 0.05f, 0.0005f, 0f, 1.2f ), "gravity NaN" );
		} );

		registry.Add( "particle-media", "validation boundaries pass and canonical bundles validate", () =>
		{
			_ = new MediumBundle( new Vector3( 0, 0, -386f ), 0f, 0f, 0f, 1e-9f );
			_ = new MediumBundle( new Vector3( 0, 0, -386f ), 0f, 0f, 2000f, 1.2f );
			MediumBundle.Air.Validate();
			MediumBundle.Water.Validate();
			MediumBundle.Space.Validate();
			MediumBundle.ZeroG.Validate();
		} );

		registry.Add( "particle-media", "water bubble rises through the real sim", () =>
		{
			var settings = MediumBundle.ApplyTo( new ParticleSystemSettings { Capacity = 100, Seed = 7 }, MediumBundle.Water );
			var sys = new ParticleSystem( settings );
			sys.AddEmitter( new ParticleEmitter
			{
				Position = new Vector3( 0, 0, 0 ), Direction = new Vector3( 0, 0, 1 ), Speed = 0,
				LifetimeMin = 5f, LifetimeMax = 5f, SizeMin = 1f, SizeMax = 1f,
				Bursts = new System.Collections.Generic.List<ParticleBurst> { new( 0, 1 ) },
			} );
			sys.Step( 60 ); // 1 s at 60 Hz
			Check( sys.AliveCount == 1, $"expected 1 bubble alive, got {sys.AliveCount}" );
			var st = sys.States[0];
			Console.WriteLine( $"bubble after 1s: z={st.Position.Z} vz={st.Velocity.Z}" );
			Check( st.Position.Z > 10f && st.Position.Z < 30f, $"bubble must rise 10..30 in in 1 s, got z={st.Position.Z}" );
			Check( st.Velocity.Z > 0f, $"bubble velocity must point up, got vz={st.Velocity.Z}" );
		} );

		registry.Add( "particle-media", "space drift is near-straight through the real sim", () =>
		{
			var settings = MediumBundle.ApplyTo( new ParticleSystemSettings { Capacity = 100, Seed = 7 }, MediumBundle.Space );
			var sys = new ParticleSystem( settings );
			sys.AddEmitter( new ParticleEmitter
			{
				Position = new Vector3( 0, 0, 0 ), Direction = new Vector3( 1, 0, 0 ), Speed = 10f, SpreadDegrees = 0,
				LifetimeMin = 5f, LifetimeMax = 5f, SizeMin = 1f, SizeMax = 1f,
				Bursts = new System.Collections.Generic.List<ParticleBurst> { new( 0, 1 ) },
			} );
			sys.Step( 60 ); // 1 s at 60 Hz
			Check( sys.AliveCount == 1, $"expected 1 drifting particle alive, got {sys.AliveCount}" );
			var st = sys.States[0];
			Console.WriteLine( $"space drift after 1s: x={st.Position.X} z={st.Position.Z}" );
			Check( st.Position.X > 9f && st.Position.X < 11f, $"10 in/s for 1 s must land x in 9..11, got {st.Position.X}" );
			Check( MathF.Abs( st.Position.Z ) < 0.5f, $"near-zero gravity must keep |z| < 0.5, got {st.Position.Z}" );
		} );

		registry.Add( "particle-media", "dust sinks slowly in water, fast in air", () =>
		{
			// Heavy-grain case: per-particle density does not exist in
			// ParticleState yet, so this steps the bundle's raw gravity +
			// drags (linear + quadratic) for a grain starting at rest.
			// The comparison is the claim: same grain, only the medium changes.
			static float Drop( MediumBundle b )
			{
				float z = 0f, v = 0f, h = 1f / 60f;
				for ( int i = 0; i < 60; i++ )
				{
					v += ( b.Gravity.Z - b.DragLinear * v - b.DragQuadratic * MathF.Abs( v ) * v ) * h;
					z += v * h;
				}
				return z; // negative = fell
			}
			float waterDrop = Drop( MediumBundle.Water );
			float airDrop = Drop( MediumBundle.Air );
			Console.WriteLine( $"dust after 1s: water z={waterDrop} air z={airDrop}" );
			Check( waterDrop < 0f, $"dust in water must still sink, got z={waterDrop}" );
			Check( airDrop < 0f, $"dust in air must sink, got z={airDrop}" );
			Check( MathF.Abs( waterDrop ) < MathF.Abs( airDrop ) * 0.6f,
				$"water drop |{waterDrop}| must be < 60% of air drop |{airDrop}|" );
		} );
	}
}
