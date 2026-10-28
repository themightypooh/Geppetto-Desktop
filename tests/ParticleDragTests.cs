using System.Numerics;
using Marionette.Desktop;

// Lane P67 (particles session, 2026-10-24): tests for ParticleDrag.cs.
// Same harness shape as ParticleForceFieldsTests: registry.Add("particle-drag", name, body).
// Sweep line (NOT added to the canonical tree — coordinator sweep only):
//   ParticleDragTests.Register( registry, settings );
// placed after the GpuParticleSystemTests.Register line in TestRegistry.cs.
// Units throughout: inches, Z-up, seconds, velocities in/s, accelerations in/s^2.
// These are pure evaluators: no sim stepping, no wiring into Integrate (that is
// sweep work, not tested here), no RNG anywhere.
internal static class ParticleDragTests
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
			throw new Exception( $"{what}: expected an ArgumentException, none thrown" );
		}

		registry.Add( "particle-drag", "linear known value: 2/s at 10 in/s gives -20 in/s^2", () =>
		{
			NearVec( ParticleDrag.Linear( new Vector3( 10, 0, 0 ), Vector3.Zero, 2 ), new Vector3( -20, 0, 0 ), 1e-4f, "linear 2/s, rel 10" );
		} );

		registry.Add( "particle-drag", "linear acts on relative velocity, not absolute", () =>
		{
			// Velocity 15, wind 5: relative 10, drag 2/s -> -20, not -30.
			NearVec( ParticleDrag.Linear( new Vector3( 15, 0, 0 ), new Vector3( 5, 0, 0 ), 2 ), new Vector3( -20, 0, 0 ), 1e-4f, "linear with wind" );
			// Particle slower than the wind is sped UP toward the wind.
			NearVec( ParticleDrag.Linear( Vector3.Zero, new Vector3( 10, 0, 0 ), 2 ), new Vector3( 20, 0, 0 ), 1e-4f, "linear below wind" );
		} );

		registry.Add( "particle-drag", "linear zero relative velocity is exactly zero", () =>
		{
			Check( ParticleDrag.Linear( new Vector3( 7, -3, 2 ), new Vector3( 7, -3, 2 ), 2 ) == Vector3.Zero, "linear at wind speed must be exactly zero" );
			Check( ParticleDrag.Linear( Vector3.Zero, Vector3.Zero, 5 ) == Vector3.Zero, "linear at rest in still air must be exactly zero" );
		} );

		registry.Add( "particle-drag", "linear zero coefficient is drag off", () =>
		{
			Check( ParticleDrag.Linear( new Vector3( 100, -50, 25 ), new Vector3( 10, 10, 10 ), 0 ) == Vector3.Zero, "linear coeff 0 must be zero" );
			ParticleDrag.Validate( 0, 0 );
		} );

		registry.Add( "particle-drag", "quadratic known value: 0.1/in at 10 in/s gives -10 in/s^2", () =>
		{
			NearVec( ParticleDrag.Quadratic( new Vector3( 10, 0, 0 ), Vector3.Zero, 0.1f ), new Vector3( -10, 0, 0 ), 1e-4f, "quadratic 0.1/in, rel 10" );
		} );

		registry.Add( "particle-drag", "quadratic doubles speed quadruples magnitude", () =>
		{
			Vector3 at10 = ParticleDrag.Quadratic( new Vector3( 10, 0, 0 ), Vector3.Zero, 0.1f );
			Vector3 at20 = ParticleDrag.Quadratic( new Vector3( 20, 0, 0 ), Vector3.Zero, 0.1f );
			NearVec( at20, new Vector3( -40, 0, 0 ), 1e-4f, "quadratic at rel 20" );
			Near( at20.Length() / at10.Length(), 4, 1e-4f, "quadratic magnitude ratio 20/10" );
			// Same check off-axis with wind: rel (0, 6, 8) has speed 10, doubled rel speed 20 quadruples.
			Vector3 w = ParticleDrag.Quadratic( new Vector3( 3, 6, 8 ), new Vector3( 3, 0, 0 ), 0.1f );
			Near( w.Length(), 10, 1e-4f, "quadratic diagonal speed-10 magnitude" );
			Vector3 w2 = ParticleDrag.Quadratic( new Vector3( 3, 12, 16 ), new Vector3( 3, 0, 0 ), 0.1f );
			Near( w2.Length() / w.Length(), 4, 1e-4f, "quadratic diagonal magnitude ratio" );
		} );

		registry.Add( "particle-drag", "quadratic zero relative velocity is exactly zero", () =>
		{
			Check( ParticleDrag.Quadratic( new Vector3( 7, -3, 2 ), new Vector3( 7, -3, 2 ), 0.5f ) == Vector3.Zero, "quadratic at wind speed must be exactly zero" );
			Check( ParticleDrag.Quadratic( new Vector3( 10, 0, 0 ), Vector3.Zero, 0 ) == Vector3.Zero, "quadratic coeff 0 must be zero" );
		} );

		registry.Add( "particle-drag", "stokes known value: coefficient 6, mass 2, rel 10 gives -30 in/s^2", () =>
		{
			NearVec( ParticleDrag.Stokes( new Vector3( 10, 0, 0 ), Vector3.Zero, 6, 2 ), new Vector3( -30, 0, 0 ), 1e-4f, "stokes 6/2, rel 10" );
		} );

		registry.Add( "particle-drag", "stokes depends on mass where linear does not", () =>
		{
			// Same coefficient value, same relative velocity: mass 3 halves the
			// mass-2 result, while Linear(2) with coeff 2 gives -20 regardless.
			NearVec( ParticleDrag.Stokes( new Vector3( 10, 0, 0 ), Vector3.Zero, 6, 3 ), new Vector3( -20, 0, 0 ), 1e-4f, "stokes 6/3, rel 10" );
			NearVec( ParticleDrag.Linear( new Vector3( 10, 0, 0 ), Vector3.Zero, 2 ), new Vector3( -20, 0, 0 ), 1e-4f, "linear 2, rel 10" );
			// ...but Stokes with mass 1 and coefficient 2 equals Linear with coeff 2:
			// the two models agree only at mass 1, they are not interchangeable.
			NearVec( ParticleDrag.Stokes( new Vector3( 10, 0, 0 ), Vector3.Zero, 2, 1 ), ParticleDrag.Linear( new Vector3( 10, 0, 0 ), Vector3.Zero, 2 ), 1e-4f, "stokes mass 1 == linear" );
			NearVec( ParticleDrag.Stokes( new Vector3( 10, 0, 0 ), Vector3.Zero, 2, 4 ), new Vector3( -5, 0, 0 ), 1e-4f, "stokes mass 4 quarters it" );
		} );

		registry.Add( "particle-drag", "stokes zero relative velocity is exactly zero", () =>
		{
			Check( ParticleDrag.Stokes( new Vector3( 7, -3, 2 ), new Vector3( 7, -3, 2 ), 6, 2 ) == Vector3.Zero, "stokes at wind speed must be exactly zero" );
			Check( ParticleDrag.Stokes( new Vector3( 10, 0, 0 ), Vector3.Zero, 0, 2 ) == Vector3.Zero, "stokes coeff 0 must be zero" );
			ParticleDrag.ValidateStokes( 0, 2 );
		} );

		registry.Add( "particle-drag", "sign correctness on all axes: drag never pushes away from wind", () =>
		{
			var cases = new[]
			{
				new Vector3( 10, 0, 0 ), new Vector3( -10, 0, 0 ),
				new Vector3( 0, 10, 0 ), new Vector3( 0, -10, 0 ),
				new Vector3( 0, 0, 10 ), new Vector3( 0, 0, -10 ),
				new Vector3( 4, -6, 8 ), new Vector3( -3, 5, -7 ),
			};
			foreach ( Vector3 v in cases )
			{
				Vector3 wind = new Vector3( 1, 2, -1 );
				Vector3 rel = v - wind;
				Check( Vector3.Dot( ParticleDrag.Linear( v, wind, 2 ), rel ) < 0, $"linear must oppose rel at {v}" );
				Check( Vector3.Dot( ParticleDrag.Quadratic( v, wind, 0.1f ), rel ) < 0, $"quadratic must oppose rel at {v}" );
				Check( Vector3.Dot( ParticleDrag.Stokes( v, wind, 6, 2 ), rel ) < 0, $"stokes must oppose rel at {v}" );
				Check( Vector3.Dot( ParticleDrag.Combined( v, wind, 2, 0.1f ), rel ) < 0, $"combined must oppose rel at {v}" );
				// One small step along the drag must land closer to the wind, not farther.
				Vector3 stepped = v + ParticleDrag.Linear( v, wind, 2 ) * 0.01f;
				Check( (stepped - wind).Length() < rel.Length(), $"linear step must approach wind at {v}" );
			}
		} );

		registry.Add( "particle-drag", "combined is exactly the sum of linear and quadratic", () =>
		{
			var samples = new[]
			{
				(new Vector3( 12, -4, 3 ), new Vector3( 2, 1, -1 ), 2f, 0.1f),
				(new Vector3( 0, 0, 0 ), new Vector3( 5, -5, 5 ), 0.5f, 0.02f),
				(new Vector3( -20, 10, 0 ), Vector3.Zero, 1f, 0.25f),
				(new Vector3( 3, 3, 3 ), new Vector3( 3, 3, 3 ), 2f, 0.1f),
			};
			foreach ( var (v, w, lin, quad) in samples )
			{
				Vector3 want = ParticleDrag.Linear( v, w, lin ) + ParticleDrag.Quadratic( v, w, quad );
				Check( ParticleDrag.Combined( v, w, lin, quad ) == want, $"combined must equal linear + quadratic at {v}" );
			}
			// Spot value: rel (10, -5, 4), |rel| = sqrt(141) ~ 11.874; linear -2*rel
			// = (-20, 10, -8); quadratic -0.1*11.874*rel ~ (-11.874, 5.937, -4.750).
			NearVec( ParticleDrag.Combined( new Vector3( 12, -4, 3 ), new Vector3( 2, 1, -1 ), 2, 0.1f ), new Vector3( -31.874f, 15.937f, -12.750f ), 1e-3f, "combined spot value" );
		} );

		registry.Add( "particle-drag", "combined zero relative velocity is exactly zero", () =>
		{
			Check( ParticleDrag.Combined( new Vector3( 7, -3, 2 ), new Vector3( 7, -3, 2 ), 2, 0.1f ) == Vector3.Zero, "combined at wind speed must be exactly zero" );
		} );

		registry.Add( "particle-drag", "validate rejects negative coefficients loudly", () =>
		{
			Throws( () => ParticleDrag.Validate( -0.1f, 0 ), "negative dragLinear" );
			Throws( () => ParticleDrag.Validate( 0, -0.1f ), "negative dragQuadratic" );
			Throws( () => ParticleDrag.ValidateStokes( -1, 2 ), "negative stokes coefficient" );
			Throws( () => ParticleDrag.Linear( Vector3.Zero, Vector3.Zero, -1 ), "linear evaluator negative coeff" );
			Throws( () => ParticleDrag.Quadratic( Vector3.Zero, Vector3.Zero, -1 ), "quadratic evaluator negative coeff" );
			Throws( () => ParticleDrag.Combined( Vector3.Zero, Vector3.Zero, -1, 0 ), "combined negative linear" );
			Throws( () => ParticleDrag.Combined( Vector3.Zero, Vector3.Zero, 0, -1 ), "combined negative quadratic" );
		} );

		registry.Add( "particle-drag", "validate rejects bad mass loudly, accepts positive mass", () =>
		{
			Throws( () => ParticleDrag.ValidateStokes( 6, 0 ), "stokes mass 0" );
			Throws( () => ParticleDrag.ValidateStokes( 6, -2 ), "stokes negative mass" );
			Throws( () => ParticleDrag.Stokes( Vector3.Zero, Vector3.Zero, 6, 0 ), "stokes evaluator mass 0" );
			ParticleDrag.ValidateStokes( 6, 2 );
		} );

		registry.Add( "particle-drag", "validate rejects non-finite inputs loudly, never clamps", () =>
		{
			Throws( () => ParticleDrag.Validate( float.NaN, 0 ), "NaN dragLinear" );
			Throws( () => ParticleDrag.Validate( 0, float.PositiveInfinity ), "infinite dragQuadratic" );
			Throws( () => ParticleDrag.ValidateStokes( float.NaN, 2 ), "NaN stokes coefficient" );
			Throws( () => ParticleDrag.ValidateStokes( 6, float.NaN ), "NaN mass" );
			Throws( () => ParticleDrag.Linear( new Vector3( float.NaN, 0, 0 ), Vector3.Zero, 2 ), "NaN velocity" );
			Throws( () => ParticleDrag.Linear( Vector3.Zero, new Vector3( 0, float.PositiveInfinity, 0 ), 2 ), "infinite wind" );
			Throws( () => ParticleDrag.Quadratic( new Vector3( 0, 0, float.NegativeInfinity ), Vector3.Zero, 0.1f ), "infinite velocity quadratic" );
			Throws( () => ParticleDrag.Stokes( Vector3.Zero, new Vector3( float.NaN, 0, 0 ), 6, 2 ), "NaN wind stokes" );
		} );
	}
}
