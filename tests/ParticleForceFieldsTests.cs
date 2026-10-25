using System.Numerics;
using Marionette.Desktop;

// Lane P22 (particles session, 2026-10-24): tests for ParticleForceFields.cs.
// Same harness shape as Particle2DTests: registry.Add("particle-force-fields", name, body).
// Sweep line (NOT added to the canonical tree — coordinator sweep only):
//   ParticleForceFieldsTests.Register( registry, settings );
// placed after the GpuParticleSystemTests.Register line in TestRegistry.cs.
// Units throughout: inches, Z-up, seconds, accelerations in/s^2.
internal static class ParticleForceFieldsTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void NearVec( Vector3 got, Vector3 want, float tol, string what )
		{
			if ( (got - want).Length() > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" );
		}
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; }
			catch ( InvalidOperationException ) { return; }
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}
		Vector3 Acc( IParticleForceField f, Vector3 p ) => f.Acceleration( p, Vector3.Zero, 0f );
		Vector3 Origin() => Vector3.Zero;

		registry.Add( "particle-force-fields", "attractor constant: full strength inside, zero beyond radius", () =>
		{
			var f = new ParticleAttractorField { Center = Origin(), Strength = 100, Radius = 10, Falloff = ParticleForceFalloff.Constant };
			f.Validate();
			NearVec( Acc( f, new Vector3( 5, 0, 0 ) ), new Vector3( -100, 0, 0 ), 1e-4f, "constant at d=5" );
			NearVec( Acc( f, new Vector3( 0, 0, 9.9f ) ), new Vector3( 0, 0, -100 ), 1e-4f, "constant at d=9.9 on Z" );
			NearVec( Acc( f, new Vector3( 10.1f, 0, 0 ) ), Vector3.Zero, 1e-4f, "constant beyond radius" );
		} );

		registry.Add( "particle-force-fields", "attractor linear: strength at centre, half at half radius, zero at radius", () =>
		{
			var f = new ParticleAttractorField { Center = Origin(), Strength = 100, Radius = 10, Falloff = ParticleForceFalloff.Linear };
			NearVec( Acc( f, new Vector3( 1, 0, 0 ) ), new Vector3( -100, 0, 0 ) * 0.9f, 1e-4f, "linear at d=1 (0.9 x strength)" );
			NearVec( Acc( f, new Vector3( 5, 0, 0 ) ), new Vector3( -50, 0, 0 ), 1e-4f, "linear at d=5" );
			NearVec( Acc( f, new Vector3( 2.5f, 0, 0 ) ), new Vector3( -75, 0, 0 ), 1e-4f, "linear at d=2.5" );
			NearVec( Acc( f, new Vector3( 10, 0, 0 ) ), Vector3.Zero, 1e-4f, "linear at radius" );
			NearVec( Acc( f, new Vector3( 15, 0, 0 ) ), Vector3.Zero, 1e-4f, "linear beyond radius" );
		} );

		registry.Add( "particle-force-fields", "attractor inverse-square: strength at 1 inch, quarter at 2, hundredth at 10", () =>
		{
			var f = new ParticleAttractorField { Center = Origin(), Strength = 100, Radius = 10, Falloff = ParticleForceFalloff.InverseSquare, MinDistance = 1 };
			NearVec( Acc( f, new Vector3( 1, 0, 0 ) ), new Vector3( -100, 0, 0 ), 1e-4f, "inverse-square at d=1" );
			NearVec( Acc( f, new Vector3( 2, 0, 0 ) ), new Vector3( -25, 0, 0 ), 1e-4f, "inverse-square at d=2" );
			NearVec( Acc( f, new Vector3( 10, 0, 0 ) ), new Vector3( -1, 0, 0 ), 1e-4f, "inverse-square at d=10" );
			NearVec( Acc( f, new Vector3( 20, 0, 0 ) ), Vector3.Zero, 1e-4f, "inverse-square beyond radius is zero, not 0.25" );
		} );

		registry.Add( "particle-force-fields", "attractor pulls toward the point on every axis", () =>
		{
			var f = new ParticleAttractorField { Center = new Vector3( 10, 20, 30 ), Strength = 60, Radius = 100, Falloff = ParticleForceFalloff.Constant };
			Vector3 a = Acc( f, new Vector3( 10, 20, 0 ) );
			NearVec( a, new Vector3( 0, 0, 60 ), 1e-4f, "attractor below centre pulls +Z" );
			Vector3 b = Acc( f, new Vector3( 40, 20, 30 ) );
			NearVec( b, new Vector3( -60, 0, 0 ), 1e-4f, "attractor pulls -X" );
		} );

		registry.Add( "particle-force-fields", "repeller: same magnitudes as attractor, opposite sign", () =>
		{
			var att = new ParticleAttractorField { Center = Origin(), Strength = 100, Radius = 10, Falloff = ParticleForceFalloff.Linear };
			var rep = new ParticleRepellerField { Center = Origin(), Strength = 100, Radius = 10, Falloff = ParticleForceFalloff.Linear };
			foreach ( Vector3 p in new[] { new Vector3( 5, 0, 0 ), new Vector3( 0, 2.5f, 0 ), new Vector3( 0, 0, 9 ) } )
				NearVec( Acc( rep, p ), -Acc( att, p ), 1e-4f, $"repeller is -attractor at {p}" );
			NearVec( Acc( rep, new Vector3( 5, 0, 0 ) ), new Vector3( 50, 0, 0 ), 1e-4f, "repeller linear at d=5 pushes +X" );
			NearVec( Acc( rep, new Vector3( 15, 0, 0 ) ), Vector3.Zero, 1e-4f, "repeller beyond radius" );
		} );

		registry.Add( "particle-force-fields", "repeller inverse-square matches attractor magnitude, flipped", () =>
		{
			var rep = new ParticleRepellerField { Center = Origin(), Strength = 100, Radius = 10, Falloff = ParticleForceFalloff.InverseSquare, MinDistance = 1 };
			NearVec( Acc( rep, new Vector3( 2, 0, 0 ) ), new Vector3( 25, 0, 0 ), 1e-4f, "repeller inverse-square at d=2" );
			NearVec( Acc( rep, new Vector3( 10, 0, 0 ) ), new Vector3( 1, 0, 0 ), 1e-4f, "repeller inverse-square at d=10" );
		} );

		registry.Add( "particle-force-fields", "on-point clamp: exactly on the point is finite zero, just inside is capped", () =>
		{
			var att = new ParticleAttractorField { Center = Origin(), Strength = 100, Radius = 10, Falloff = ParticleForceFalloff.InverseSquare, MinDistance = 1 };
			Vector3 on = Acc( att, Origin() );
			Check( on == Vector3.Zero, $"on-point attractor must be exactly zero, got {on}" );
			Check( float.IsFinite( on.X ) && float.IsFinite( on.Y ) && float.IsFinite( on.Z ), "on-point attractor must be finite" );
			NearVec( Acc( att, new Vector3( 0.001f, 0, 0 ) ), new Vector3( -100, 0, 0 ), 1e-3f, "0.001 inch away is clamped to strength at MinDistance, not 100M" );
			var rep = new ParticleRepellerField { Center = Origin(), Strength = 100, Radius = 10, Falloff = ParticleForceFalloff.InverseSquare, MinDistance = 1 };
			Check( Acc( rep, Origin() ) == Vector3.Zero, "on-point repeller must be exactly zero" );
			var well = new ParticleGravityWellField { Center = Origin(), Mu = 1000, MinDistance = 1 };
			Check( Acc( well, Origin() ) == Vector3.Zero, "on-point gravity well must be exactly zero" );
			NearVec( Acc( well, new Vector3( 0.001f, 0, 0 ) ), new Vector3( -1000, 0, 0 ), 1e-3f, "well 0.001 inch away is clamped to Mu/MinDistance^2" );
		} );

		registry.Add( "particle-force-fields", "wind zone hard edge: full inside including faces, zero outside", () =>
		{
			var z = new ParticleWindZoneField { Min = new Vector3( -10, -10, 0 ), Max = new Vector3( 10, 10, 20 ), WindAcceleration = new Vector3( 20, 0, 0 ), Feather = 0 };
			z.Validate();
			NearVec( Acc( z, new Vector3( 0, 0, 10 ) ), new Vector3( 20, 0, 0 ), 1e-4f, "hard edge deep inside" );
			NearVec( Acc( z, new Vector3( 10, 0, 10 ) ), new Vector3( 20, 0, 0 ), 1e-4f, "hard edge on the face is still full" );
			NearVec( Acc( z, new Vector3( 10.01f, 0, 10 ) ), Vector3.Zero, 1e-4f, "hard edge just outside" );
			NearVec( Acc( z, new Vector3( 0, 0, 25 ) ), Vector3.Zero, 1e-4f, "hard edge above the box" );
		} );

		registry.Add( "particle-force-fields", "wind zone feathered edge: zero on the face, half at half feather, full deep inside", () =>
		{
			var z = new ParticleWindZoneField { Min = new Vector3( -10, -10, 0 ), Max = new Vector3( 10, 10, 20 ), WindAcceleration = new Vector3( 20, 0, 0 ), Feather = 2 };
			z.Validate();
			NearVec( Acc( z, new Vector3( 0, 0, 10 ) ), new Vector3( 20, 0, 0 ), 1e-4f, "feathered deep inside" );
			NearVec( Acc( z, new Vector3( 10, 0, 10 ) ), Vector3.Zero, 1e-4f, "feathered exactly on the face is zero" );
			NearVec( Acc( z, new Vector3( 9, 0, 10 ) ), new Vector3( 10, 0, 0 ), 1e-4f, "feathered 1 inch inside a 2-inch feather is half" );
			NearVec( Acc( z, new Vector3( 8, 0, 10 ) ), new Vector3( 20, 0, 0 ), 1e-4f, "feathered at the full feather depth is full" );
			NearVec( Acc( z, new Vector3( 11, 0, 10 ) ), Vector3.Zero, 1e-4f, "feathered outside has no halo" );
		} );

		registry.Add( "particle-force-fields", "wind zone feather clamps to half extent on a thin box", () =>
		{
			var z = new ParticleWindZoneField { Min = new Vector3( 0, 0, 0 ), Max = new Vector3( 1, 10, 10 ), WindAcceleration = new Vector3( 8, 0, 0 ), Feather = 5 };
			z.Validate();
			NearVec( Acc( z, new Vector3( 0.5f, 5, 5 ) ), new Vector3( 8, 0, 0 ), 1e-4f, "thin box centre is full despite feather > half extent" );
			NearVec( Acc( z, new Vector3( 0, 5, 5 ) ), Vector3.Zero, 1e-4f, "thin box face is still zero" );
		} );

		registry.Add( "particle-force-fields", "gravity well: inverse-square falloff toward the centre", () =>
		{
			var w = new ParticleGravityWellField { Center = Origin(), Mu = 1000, MinDistance = 1 };
			w.Validate();
			NearVec( Acc( w, new Vector3( 10, 0, 0 ) ), new Vector3( -10, 0, 0 ), 1e-4f, "well at d=10: Mu/d^2 = 10" );
			NearVec( Acc( w, new Vector3( 20, 0, 0 ) ), new Vector3( -2.5f, 0, 0 ), 1e-4f, "well at d=20: 2.5, quarter of d=10" );
			NearVec( Acc( w, new Vector3( 0, 0, 5 ) ), new Vector3( 0, 0, -40 ), 1e-4f, "well above centre pulls -Z" );
			NearVec( Acc( w, new Vector3( 1, 0, 0 ) ), new Vector3( -1000, 0, 0 ), 1e-4f, "well at d=1: Mu" );
		} );

		registry.Add( "particle-force-fields", "combine sums fields for one particle", () =>
		{
			var att = new ParticleAttractorField { Center = Origin(), Strength = 100, Radius = 10, Falloff = ParticleForceFalloff.Constant };
			var zone = new ParticleWindZoneField { Min = new Vector3( -10, -10, -10 ), Max = new Vector3( 10, 10, 10 ), WindAcceleration = new Vector3( 0, 20, 0 ), Feather = 0 };
			var well = new ParticleGravityWellField { Center = Origin(), Mu = 1000, MinDistance = 1 };
			var combo = ParticleForceFields.Combine( new IParticleForceField[] { att, zone, well } );
			combo.Validate();
			Vector3 p = new Vector3( 5, 0, 0 );
			Vector3 expected = Acc( att, p ) + Acc( zone, p ) + Acc( well, p );
			NearVec( Acc( combo, p ), expected, 1e-4f, "combined equals component sum" );
			NearVec( Acc( combo, p ), new Vector3( -140, 20, 0 ), 1e-4f, "combined at (5,0,0): -100 attractor, -40 well, +20 wind Y" );
		} );

		registry.Add( "particle-force-fields", "combine: empty sums to zero, attractor plus equal repeller cancels", () =>
		{
			var empty = ParticleForceFields.Combine( Array.Empty<IParticleForceField>() );
			empty.Validate();
			NearVec( Acc( empty, new Vector3( 3, 4, 5 ) ), Vector3.Zero, 1e-4f, "empty combine is zero" );
			var cancel = ParticleForceFields.Combine( new IParticleForceField[]
			{
				new ParticleAttractorField { Center = Origin(), Strength = 50, Radius = 10, Falloff = ParticleForceFalloff.Constant },
				new ParticleRepellerField { Center = Origin(), Strength = 50, Radius = 10, Falloff = ParticleForceFalloff.Constant },
			} );
			NearVec( Acc( cancel, new Vector3( 5, 0, 0 ) ), Vector3.Zero, 1e-4f, "equal attractor + repeller cancel" );
		} );

		registry.Add( "particle-force-fields", "combine ignores velocity and time for these fields, matches direct sum", () =>
		{
			var f = ParticleForceFields.Combine( new IParticleForceField[]
			{
				new ParticleAttractorField { Center = Origin(), Strength = 100, Radius = 10, Falloff = ParticleForceFalloff.Constant },
			} );
			Vector3 p = new Vector3( 5, 0, 0 );
			NearVec( f.Acceleration( p, new Vector3( 999, -999, 123 ), 42f ), new Vector3( -100, 0, 0 ), 1e-4f, "velocity/time do not change these fields" );
		} );

		registry.Add( "particle-force-fields", "validate rejects negative radius and negative strength", () =>
		{
			Throws( () => new ParticleAttractorField { Center = Origin(), Strength = 100, Radius = -5 }.Validate(), "attractor negative radius" );
			Throws( () => new ParticleAttractorField { Center = Origin(), Strength = 100, Radius = 0 }.Validate(), "attractor zero radius" );
			Throws( () => new ParticleAttractorField { Center = Origin(), Strength = -1, Radius = 10 }.Validate(), "attractor negative strength" );
			Throws( () => new ParticleRepellerField { Center = Origin(), Strength = -1, Radius = 10 }.Validate(), "repeller negative strength" );
			Throws( () => new ParticleAttractorField { Center = Origin(), Strength = float.NaN, Radius = 10 }.Validate(), "attractor NaN strength" );
			Throws( () => new ParticleAttractorField { Center = Origin(), Strength = 100, Radius = 10, MinDistance = 0 }.Validate(), "attractor zero min distance" );
			Throws( () => new ParticleGravityWellField { Center = Origin(), Mu = -10 }.Validate(), "gravity well negative Mu" );
			Throws( () => new ParticleGravityWellField { Center = Origin(), Mu = 100, MinDistance = -1 }.Validate(), "gravity well negative min distance" );
		} );

		registry.Add( "particle-force-fields", "validate rejects inverted wind zone boxes and negative feather", () =>
		{
			Throws( () => new ParticleWindZoneField { Min = new Vector3( 10, 0, 0 ), Max = new Vector3( -10, 10, 10 ), WindAcceleration = new Vector3( 1, 0, 0 ) }.Validate(), "inverted X box" );
			Throws( () => new ParticleWindZoneField { Min = new Vector3( 0, 5, 0 ), Max = new Vector3( 10, 4, 10 ), WindAcceleration = new Vector3( 1, 0, 0 ) }.Validate(), "inverted Y box" );
			Throws( () => new ParticleWindZoneField { Min = new Vector3( 0, 0, 9 ), Max = new Vector3( 10, 10, 1 ), WindAcceleration = new Vector3( 1, 0, 0 ) }.Validate(), "inverted Z box" );
			Throws( () => new ParticleWindZoneField { Min = Vector3.Zero, Max = new Vector3( 10, 10, 10 ), WindAcceleration = new Vector3( 1, 0, 0 ), Feather = -1 }.Validate(), "negative feather" );
		} );

		registry.Add( "particle-force-fields", "validate rejects non-finite centres and propagates through combine", () =>
		{
			Throws( () => new ParticleAttractorField { Center = new Vector3( float.NaN, 0, 0 ), Strength = 10, Radius = 10 }.Validate(), "NaN centre" );
			Throws( () => new ParticleWindZoneField { Min = new Vector3( float.PositiveInfinity, 0, 0 ), Max = new Vector3( 10, 10, 10 ) }.Validate(), "infinite zone min" );
			Throws( () => ParticleForceFields.Combine( new IParticleForceField[]
			{
				new ParticleAttractorField { Center = Origin(), Strength = 10, Radius = 10 },
				new ParticleAttractorField { Center = Origin(), Strength = -5, Radius = 10 },
			} ).Validate(), "combine propagates a bad child" );
			Throws( () => ParticleForceFields.Combine( new IParticleForceField[] { null! } ).Validate(), "combine rejects a null entry" );
		} );

		registry.Add( "particle-force-fields", "validate passes for well-formed fields", () =>
		{
			new ParticleAttractorField { Center = Origin(), Strength = 0, Radius = 10, Falloff = ParticleForceFalloff.Constant }.Validate();
			new ParticleRepellerField { Center = Origin(), Strength = 10, Radius = 10, Falloff = ParticleForceFalloff.InverseSquare }.Validate();
			new ParticleWindZoneField { Min = Vector3.Zero, Max = new Vector3( 10, 10, 10 ), WindAcceleration = new Vector3( 1, 2, 3 ), Feather = 1 }.Validate();
			new ParticleGravityWellField { Center = Origin(), Mu = 0 }.Validate();
			ParticleForceFields.Combine( new IParticleForceField[]
			{
				new ParticleAttractorField { Center = Origin(), Strength = 10, Radius = 10 },
			} ).Validate();
		} );

		registry.Add( "particle-force-fields", "zero strength fields are inert everywhere", () =>
		{
			var att = new ParticleAttractorField { Center = Origin(), Strength = 0, Radius = 10, Falloff = ParticleForceFalloff.Constant };
			var well = new ParticleGravityWellField { Center = Origin(), Mu = 0 };
			var zone = new ParticleWindZoneField { Min = new Vector3( -10, -10, -10 ), Max = new Vector3( 10, 10, 10 ), WindAcceleration = Vector3.Zero };
			NearVec( Acc( att, new Vector3( 5, 0, 0 ) ), Vector3.Zero, 1e-4f, "zero-strength attractor" );
			NearVec( Acc( well, new Vector3( 5, 0, 0 ) ), Vector3.Zero, 1e-4f, "zero-Mu well" );
			NearVec( Acc( zone, new Vector3( 0, 0, 0 ) ), Vector3.Zero, 1e-4f, "zero wind zone" );
		} );
	}
}
