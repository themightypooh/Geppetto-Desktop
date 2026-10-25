using System.Numerics;
using Marionette.Desktop;

// Wave-6 lane P12 (particles session, 2026-10-24): tests for Particle2DFields.cs.
// Same harness shape as Particle2DTests: registry.Add("particle-2d-fields", name, body).
// Sweep line (NOT added to the canonical tree — coordinator sweep only):
//   Particle2DFieldsTests.Register( registry, settings );
// placed after the Particle2DTests.Register line.
internal static class Particle2DFieldsTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void NearVec( Vector2 got, Vector2 want, float tol, string what )
		{
			Near( got.X, want.X, tol, what + " (X)" );
			Near( got.Y, want.Y, tol, what + " (Y)" );
		}
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; }
			catch ( InvalidOperationException ) { return; }
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}
		void Finite( Vector2 v, string what ) { Check( float.IsFinite( v.X ) && float.IsFinite( v.Y ), $"{what}: non-finite vector ({v.X}, {v.Y})" ); }

		var centre = new Vector2( 100, 100 );
		Vector2 At( Vector2 c, float dx, float dy ) => new( c.X + dx, c.Y + dy );

		registry.Add( "particle-2d-fields", "attractor constant: full strength inside, zero outside, toward centre", () =>
		{
			var f = new Particle2DAttractorField { Center = centre, Strength = 100, Radius = 200, Falloff = Particle2DFieldFalloff.Constant };
			f.Validate();
			NearVec( f.Evaluate( At( centre, 50, 0 ), Vector2.Zero, 0 ), new Vector2( -100, 0 ), 1e-4f, "constant at d=50, +X side" );
			NearVec( f.Evaluate( At( centre, 150, 0 ), Vector2.Zero, 0 ), new Vector2( -100, 0 ), 1e-4f, "constant at d=150" );
			NearVec( f.Evaluate( At( centre, 200, 0 ), Vector2.Zero, 0 ), new Vector2( -100, 0 ), 1e-4f, "constant at the radius boundary is inclusive" );
			NearVec( f.Evaluate( At( centre, 250, 0 ), Vector2.Zero, 0 ), Vector2.Zero, 1e-4f, "constant outside the radius is zero" );
			NearVec( f.Evaluate( At( centre, 0, 50 ), Vector2.Zero, 0 ), new Vector2( 0, -100 ), 1e-4f, "constant below centre pulls up (-Y, Y-down)" );
		} );

		registry.Add( "particle-2d-fields", "attractor linear: strength falls to zero at the radius", () =>
		{
			var f = new Particle2DAttractorField { Center = centre, Strength = 200, Radius = 100, Falloff = Particle2DFieldFalloff.Linear };
			f.Validate();
			NearVec( f.Evaluate( At( centre, 25, 0 ), Vector2.Zero, 0 ), new Vector2( -150, 0 ), 1e-4f, "linear at d=25 of R=100, S=200" );
			NearVec( f.Evaluate( At( centre, 50, 0 ), Vector2.Zero, 0 ), new Vector2( -100, 0 ), 1e-4f, "linear at d=50" );
			NearVec( f.Evaluate( At( centre, 75, 0 ), Vector2.Zero, 0 ), new Vector2( -50, 0 ), 1e-4f, "linear at d=75" );
			NearVec( f.Evaluate( At( centre, 100, 0 ), Vector2.Zero, 0 ), Vector2.Zero, 1e-4f, "linear at the radius is zero" );
			NearVec( f.Evaluate( At( centre, 150, 0 ), Vector2.Zero, 0 ), Vector2.Zero, 1e-4f, "linear outside the radius is zero" );
		} );

		registry.Add( "particle-2d-fields", "attractor inverse-square: strength over distance squared", () =>
		{
			var f = new Particle2DAttractorField { Center = centre, Strength = 10000, Radius = 500, Falloff = Particle2DFieldFalloff.InverseSquare, MinDistance = 10 };
			f.Validate();
			NearVec( f.Evaluate( At( centre, 100, 0 ), Vector2.Zero, 0 ), new Vector2( -1, 0 ), 1e-4f, "inverse-square at d=100, S=10000" );
			NearVec( f.Evaluate( At( centre, 50, 0 ), Vector2.Zero, 0 ), new Vector2( -4, 0 ), 1e-4f, "inverse-square at d=50" );
			NearVec( f.Evaluate( At( centre, 200, 0 ), Vector2.Zero, 0 ), new Vector2( -0.25f, 0 ), 1e-4f, "inverse-square at d=200" );
			NearVec( f.Evaluate( At( centre, 600, 0 ), Vector2.Zero, 0 ), Vector2.Zero, 1e-4f, "inverse-square outside the radius is zero" );
		} );

		registry.Add( "particle-2d-fields", "attractor inverse-square clamp: near centre capped, on-point finite zero", () =>
		{
			var f = new Particle2DAttractorField { Center = centre, Strength = 10000, Radius = 500, Falloff = Particle2DFieldFalloff.InverseSquare, MinDistance = 10 };
			NearVec( f.Evaluate( At( centre, 5, 0 ), Vector2.Zero, 0 ), new Vector2( -100, 0 ), 1e-4f, "clamped at d=5 < MinDistance 10: 10000/10^2 toward centre" );
			NearVec( f.Evaluate( At( centre, 1, 0 ), Vector2.Zero, 0 ), new Vector2( -100, 0 ), 1e-4f, "clamped at d=1: same capped magnitude" );
			var onPoint = f.Evaluate( centre, Vector2.Zero, 0 );
			Finite( onPoint, "on-point attractor" );
			NearVec( onPoint, Vector2.Zero, 1e-4f, "exactly on the point has no direction, so zero — not infinity" );
			var near = f.Evaluate( At( centre, 0.001f, 0 ), Vector2.Zero, 0 );
			Finite( near, "near-point attractor" );
			Check( MathF.Abs( near.X ) <= 100.0001f, $"near-point magnitude must stay capped at 100, got {near.X}" );
		} );

		registry.Add( "particle-2d-fields", "attractor ignores velocity and time", () =>
		{
			var f = new Particle2DAttractorField { Center = centre, Strength = 100, Radius = 200, Falloff = Particle2DFieldFalloff.Constant };
			var a = f.Evaluate( At( centre, 50, 0 ), Vector2.Zero, 0 );
			var b = f.Evaluate( At( centre, 50, 0 ), new Vector2( 999, -999 ), 123.456f );
			NearVec( b, a, 1e-6f, "attractor must not depend on velocity or time" );
		} );

		registry.Add( "particle-2d-fields", "repeller constant: same shape as attractor, opposite sign", () =>
		{
			var f = new Particle2DRepellerField { Center = centre, Strength = 100, Radius = 200, Falloff = Particle2DFieldFalloff.Constant };
			f.Validate();
			NearVec( f.Evaluate( At( centre, 50, 0 ), Vector2.Zero, 0 ), new Vector2( 100, 0 ), 1e-4f, "repeller at d=50 pushes +X" );
			NearVec( f.Evaluate( At( centre, 0, 50 ), Vector2.Zero, 0 ), new Vector2( 0, 100 ), 1e-4f, "repeller below centre pushes down (+Y)" );
			NearVec( f.Evaluate( At( centre, 250, 0 ), Vector2.Zero, 0 ), Vector2.Zero, 1e-4f, "repeller outside the radius is zero" );
			var a = new Particle2DAttractorField { Center = centre, Strength = 100, Radius = 200, Falloff = Particle2DFieldFalloff.Constant };
			var p = At( centre, 80, 60 );
			NearVec( f.Evaluate( p, Vector2.Zero, 0 ), -a.Evaluate( p, Vector2.Zero, 0 ), 1e-4f, "repeller is exactly the attractor negated at the same point" );
		} );

		registry.Add( "particle-2d-fields", "repeller linear and inverse-square clamp mirror the attractor", () =>
		{
			var lin = new Particle2DRepellerField { Center = centre, Strength = 200, Radius = 100, Falloff = Particle2DFieldFalloff.Linear };
			NearVec( lin.Evaluate( At( centre, 50, 0 ), Vector2.Zero, 0 ), new Vector2( 100, 0 ), 1e-4f, "repeller linear at d=50" );
			var inv = new Particle2DRepellerField { Center = centre, Strength = 10000, Radius = 500, Falloff = Particle2DFieldFalloff.InverseSquare, MinDistance = 10 };
			NearVec( inv.Evaluate( At( centre, 5, 0 ), Vector2.Zero, 0 ), new Vector2( 100, 0 ), 1e-4f, "repeller clamped at d=5" );
			var onPoint = inv.Evaluate( centre, Vector2.Zero, 0 );
			Finite( onPoint, "on-point repeller" );
			NearVec( onPoint, Vector2.Zero, 1e-4f, "repeller exactly on the point is zero, not infinite" );
		} );

		registry.Add( "particle-2d-fields", "gust profile: base plus sine at specific times, seed 0 phase 0", () =>
		{
			var f = new Particle2DGustField { Min = new Vector2( 0, 0 ), Max = new Vector2( 200, 200 ), Direction = new Vector2( 1, 0 ), BaseStrength = 50, GustAmplitude = 20, GustFrequency = 1, Seed = 0 };
			f.Validate();
			var inside = new Vector2( 100, 100 );
			NearVec( f.Evaluate( inside, Vector2.Zero, 0 ), new Vector2( 50, 0 ), 1e-3f, "gust at t=0: base only" );
			NearVec( f.Evaluate( inside, Vector2.Zero, 0.25f ), new Vector2( 70, 0 ), 1e-3f, "gust at t=0.25 (quarter period): base + amplitude" );
			NearVec( f.Evaluate( inside, Vector2.Zero, 0.5f ), new Vector2( 50, 0 ), 1e-3f, "gust at t=0.5 (half period): base again" );
			NearVec( f.Evaluate( inside, Vector2.Zero, 0.75f ), new Vector2( 30, 0 ), 1e-3f, "gust at t=0.75: base - amplitude" );
			NearVec( f.Evaluate( inside, Vector2.Zero, 1.0f ), new Vector2( 50, 0 ), 1e-3f, "gust at t=1.0: one full period, back to base" );
		} );

		registry.Add( "particle-2d-fields", "gust zone: zero outside, inclusive on the boundary", () =>
		{
			var f = new Particle2DGustField { Min = new Vector2( 0, 0 ), Max = new Vector2( 200, 200 ), Direction = new Vector2( 1, 0 ), BaseStrength = 50, GustAmplitude = 20, GustFrequency = 1, Seed = 0 };
			NearVec( f.Evaluate( new Vector2( 250, 100 ), Vector2.Zero, 0.25f ), Vector2.Zero, 1e-4f, "gust outside the zone is zero even at peak gust" );
			NearVec( f.Evaluate( new Vector2( 100, 250 ), Vector2.Zero, 0.25f ), Vector2.Zero, 1e-4f, "gust outside below the zone is zero" );
			NearVec( f.Evaluate( new Vector2( 200, 100 ), Vector2.Zero, 0 ), new Vector2( 50, 0 ), 1e-3f, "gust on the max edge is inside" );
			NearVec( f.Evaluate( new Vector2( 0, 0 ), Vector2.Zero, 0 ), new Vector2( 50, 0 ), 1e-3f, "gust on the min corner is inside" );
		} );

		registry.Add( "particle-2d-fields", "gust direction is normalized and velocity is ignored", () =>
		{
			var f = new Particle2DGustField { Min = new Vector2( 0, 0 ), Max = new Vector2( 200, 200 ), Direction = new Vector2( 3, 4 ), BaseStrength = 50, GustAmplitude = 0, GustFrequency = 0, Seed = 0 };
			NearVec( f.Evaluate( new Vector2( 100, 100 ), Vector2.Zero, 0 ), new Vector2( 30, 40 ), 1e-4f, "direction (3,4) normalized to (0.6,0.8) times base 50" );
			NearVec( f.Evaluate( new Vector2( 100, 100 ), new Vector2( 999, -999 ), 5 ), new Vector2( 30, 40 ), 1e-4f, "gust must not depend on particle velocity" );
		} );

		registry.Add( "particle-2d-fields", "gust seed: same seed and time is identical, different seed differs", () =>
		{
			var a = new Particle2DGustField { Min = new Vector2( 0, 0 ), Max = new Vector2( 200, 200 ), Direction = new Vector2( 1, 0 ), BaseStrength = 50, GustAmplitude = 20, GustFrequency = 1, Seed = 7 };
			var p = new Vector2( 100, 100 );
			NearVec( a.Evaluate( p, Vector2.Zero, 1.234f ), a.Evaluate( p, Vector2.Zero, 1.234f ), 1e-6f, "same seed + time must be bit-identical" );
			var seed0 = new Particle2DGustField { Min = new Vector2( 0, 0 ), Max = new Vector2( 200, 200 ), Direction = new Vector2( 1, 0 ), BaseStrength = 50, GustAmplitude = 20, GustFrequency = 1, Seed = 0 };
			var seed1 = new Particle2DGustField { Min = new Vector2( 0, 0 ), Max = new Vector2( 200, 200 ), Direction = new Vector2( 1, 0 ), BaseStrength = 50, GustAmplitude = 20, GustFrequency = 1, Seed = 1 };
			var v0 = seed0.Evaluate( p, Vector2.Zero, 0 );
			var v1 = seed1.Evaluate( p, Vector2.Zero, 0 );
			Check( v1 != v0, $"seed 1 gust at t=0 ({v1}) must differ from seed 0 ({v0})" );
			float expected1 = 50 + 20 * MathF.Sin( Particle2DGustField.PhaseForSeed( 1 ) );
			Near( v1.X, expected1, 1e-3f, "seed 1 gust follows the documented seed-phase formula" );
		} );

		registry.Add( "particle-2d-fields", "combined field sums its fields for one particle", () =>
		{
			var att = new Particle2DAttractorField { Center = centre, Strength = 100, Radius = 200, Falloff = Particle2DFieldFalloff.Constant };
			var rep = new Particle2DRepellerField { Center = centre, Strength = 40, Radius = 200, Falloff = Particle2DFieldFalloff.Constant };
			var gust = new Particle2DGustField { Min = new Vector2( 0, 0 ), Max = new Vector2( 200, 200 ), Direction = new Vector2( 1, 0 ), BaseStrength = 50, GustAmplitude = 0, GustFrequency = 0, Seed = 0 };
			var combined = Particle2DCombinedField.Combine( att, rep, gust );
			combined.Validate();
			var p = At( centre, 50, 0 );
			var want = att.Evaluate( p, Vector2.Zero, 0 ) + rep.Evaluate( p, Vector2.Zero, 0 ) + gust.Evaluate( p, Vector2.Zero, 0 );
			NearVec( combined.Evaluate( p, Vector2.Zero, 0 ), want, 1e-4f, "combined equals the sum of its parts" );
			NearVec( combined.Evaluate( p, Vector2.Zero, 0 ), new Vector2( -10, 0 ), 1e-4f, "attractor -100 + repeller +40 + gust +50 = -10" );
			var outside = new Vector2( 500, 500 );
			NearVec( combined.Evaluate( outside, Vector2.Zero, 0 ), Vector2.Zero, 1e-4f, "all three fields are zero far away, so the sum is zero" );
		} );

		registry.Add( "particle-2d-fields", "combined field nests: a combined field inside a combined field sums", () =>
		{
			var inner = Particle2DCombinedField.Combine(
				new Particle2DAttractorField { Center = centre, Strength = 100, Radius = 200, Falloff = Particle2DFieldFalloff.Constant } );
			var outer = Particle2DCombinedField.Combine( inner,
				new Particle2DGustField { Min = new Vector2( 0, 0 ), Max = new Vector2( 200, 200 ), Direction = new Vector2( 0, 1 ), BaseStrength = 25, GustAmplitude = 0, GustFrequency = 0, Seed = 0 } );
			outer.Validate();
			NearVec( outer.Evaluate( At( centre, 50, 0 ), Vector2.Zero, 0 ), new Vector2( -100, 25 ), 1e-4f, "nested sum: attractor (-100,0) + gust (0,25)" );
		} );

		registry.Add( "particle-2d-fields", "combined empty list: Validate throws loudly, Evaluate is zero", () =>
		{
			var empty = Particle2DCombinedField.Combine( new List<IParticle2DField>() );
			Throws( () => empty.Validate(), "empty combined field list must fail Validate" );
			NearVec( empty.Evaluate( new Vector2( 100, 100 ), Vector2.Zero, 0 ), Vector2.Zero, 1e-6f, "empty combined Evaluate is documented as zero; Validate is the loud gate" );
		} );

		registry.Add( "particle-2d-fields", "combined rejects null entries and invalid children in Validate", () =>
		{
			var withNull = Particle2DCombinedField.Combine( new Particle2DAttractorField { Center = centre, Strength = 10, Radius = 100 }, null! );
			Throws( () => withNull.Validate(), "null field entry must fail Validate" );
			Throws( () => withNull.Evaluate( centre, Vector2.Zero, 0 ), "null field entry must fail Evaluate too, not NullReference silently" );
			var badChild = Particle2DCombinedField.Combine(
				new Particle2DAttractorField { Center = centre, Strength = -5, Radius = 100 } );
			Throws( () => badChild.Validate(), "an invalid child field must fail the combined Validate" );
		} );

		registry.Add( "particle-2d-fields", "Validate rejects bad point-field configs", () =>
		{
			Throws( () => new Particle2DAttractorField { Center = centre, Strength = 100, Radius = -10 }.Validate(), "negative radius" );
			Throws( () => new Particle2DAttractorField { Center = centre, Strength = 100, Radius = 0 }.Validate(), "zero radius" );
			Throws( () => new Particle2DAttractorField { Center = centre, Strength = -1, Radius = 100 }.Validate(), "negative attractor strength" );
			Throws( () => new Particle2DRepellerField { Center = centre, Strength = -5, Radius = 100 }.Validate(), "negative repeller strength" );
			Throws( () => new Particle2DAttractorField { Center = centre, Strength = 100, Radius = 100, MinDistance = 0 }.Validate(), "zero MinDistance" );
			Throws( () => new Particle2DAttractorField { Center = centre, Strength = 100, Radius = 100, MinDistance = 150 }.Validate(), "MinDistance above Radius" );
			Throws( () => new Particle2DAttractorField { Center = new Vector2( float.NaN, 0 ), Strength = 100, Radius = 100 }.Validate(), "NaN centre" );
			Throws( () => new Particle2DAttractorField { Center = centre, Strength = float.PositiveInfinity, Radius = 100 }.Validate(), "infinite strength" );
		} );

		registry.Add( "particle-2d-fields", "Validate rejects bad gust configs", () =>
		{
			var ok = new Particle2DGustField { Min = new Vector2( 0, 0 ), Max = new Vector2( 200, 200 ), Direction = new Vector2( 1, 0 ), BaseStrength = 50, GustAmplitude = 20, GustFrequency = 1, Seed = 0 };
			Throws( () => new Particle2DGustField { Min = ok.Min, Max = ok.Max, Direction = ok.Direction, BaseStrength = -1, GustAmplitude = 20, GustFrequency = 1 }.Validate(), "negative base strength" );
			Throws( () => new Particle2DGustField { Min = ok.Min, Max = ok.Max, Direction = ok.Direction, BaseStrength = 50, GustAmplitude = -1, GustFrequency = 1 }.Validate(), "negative gust amplitude" );
			Throws( () => new Particle2DGustField { Min = ok.Min, Max = ok.Max, Direction = ok.Direction, BaseStrength = 50, GustAmplitude = 20, GustFrequency = -1 }.Validate(), "negative gust frequency" );
			Throws( () => new Particle2DGustField { Min = ok.Min, Max = ok.Max, Direction = Vector2.Zero, BaseStrength = 50, GustAmplitude = 20, GustFrequency = 1 }.Validate(), "zero gust direction" );
			Throws( () => new Particle2DGustField { Min = new Vector2( 200, 0 ), Max = new Vector2( 0, 200 ), Direction = ok.Direction, BaseStrength = 50 }.Validate(), "inverted gust rectangle" );
			Throws( () => new Particle2DGustField { Min = ok.Min, Max = ok.Max, Direction = ok.Direction, BaseStrength = float.NaN }.Validate(), "NaN base strength" );
		} );

		registry.Add( "particle-2d-fields", "Validate passes clean configs", () =>
		{
			new Particle2DAttractorField { Center = centre, Strength = 100, Radius = 200, Falloff = Particle2DFieldFalloff.InverseSquare, MinDistance = 2 }.Validate();
			new Particle2DRepellerField { Center = centre, Strength = 0, Radius = 50 }.Validate();
			new Particle2DGustField { Min = new Vector2( 0, 0 ), Max = new Vector2( 10, 10 ), Direction = new Vector2( 0, -1 ), BaseStrength = 0, GustAmplitude = 0, GustFrequency = 0, Seed = 42 }.Validate();
			Particle2DCombinedField.Combine(
				new Particle2DAttractorField { Center = centre, Strength = 10, Radius = 100 } ).Validate();
		} );

		registry.Add( "particle-2d-fields", "finiteness sweep: no field returns NaN or infinity anywhere", () =>
		{
			var fields = new IParticle2DField[]
			{
				new Particle2DAttractorField { Center = centre, Strength = 10000, Radius = 500, Falloff = Particle2DFieldFalloff.InverseSquare, MinDistance = 0.5f },
				new Particle2DRepellerField { Center = centre, Strength = 10000, Radius = 500, Falloff = Particle2DFieldFalloff.InverseSquare, MinDistance = 0.5f },
				new Particle2DGustField { Min = new Vector2( 0, 0 ), Max = new Vector2( 200, 200 ), Direction = new Vector2( 1, 1 ), BaseStrength = 50, GustAmplitude = 100, GustFrequency = 3, Seed = 9 },
			};
			var probes = new Vector2[] { centre, At( centre, 0.0001f, 0 ), At( centre, 500, 0 ), At( centre, 501, 0 ), new Vector2( 0, 0 ), new Vector2( 200, 200 ), new Vector2( -1000, 5000 ) };
			foreach ( var f in fields )
				foreach ( var p in probes )
					foreach ( float t in new float[] { 0, 0.333f, 10 } )
						Finite( f.Evaluate( p, new Vector2( 50, -80 ), t ), $"field {f.GetType().Name} at ({p.X}, {p.Y}) t={t}" );
		} );
	}
}
