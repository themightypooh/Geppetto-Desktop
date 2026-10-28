using System.Numerics;
using Marionette.Desktop;

// Lane P64 (particles session, 2026-10-24): tests for ParticleVortex.cs.
// Same harness shape as Particle2DTests: registry.Add("particle-vortex", name, body).
// Sweep line (NOT added to the canonical tree — coordinator sweep only):
//   ParticleVortexTests.Register( registry, settings );
// placed after the GpuParticleSystemTests.Register line in tests/TestRegistry.cs.
// Units throughout: inches, Z-up, seconds, accelerations in/s^2.
internal static class ParticleVortexTests
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
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}
		void Finite( Vector3 v, string what )
		{
			Check( float.IsFinite( v.X ) && float.IsFinite( v.Y ) && float.IsFinite( v.Z ), $"{what}: not finite, got {v}" );
		}
		ParticleTornadoVortex Tornado() => new() { Center = Vector3.Zero, Axis = new Vector3( 0, 0, 1 ), Strength = 100, CoreRadius = 2, UpdraftStrength = 50 };
		ParticleWhirlpoolVortex Whirlpool() => new() { Center = Vector3.Zero, Axis = new Vector3( 0, 0, 1 ), Strength = 80, InwardStrength = 60, DrainStrength = 40, CoreRadius = 2 };
		ParticleVortexRing Ring() => new() { Center = Vector3.Zero, Axis = new Vector3( 0, 0, 1 ), RingRadius = 10, TubeRadius = 2, Strength = 100 };

		registry.Add( "particle-vortex", "tornado: core-edge rotation + half updraft at the core radius", () =>
		{
			var t = Tornado(); t.Validate();
			// d = 2 = CoreRadius: tangential cross(Z,(2,0,0)) = +Y at full Strength; updraft = 50 * 2/(2+2) = 25.
			NearVec( t.Acceleration( new Vector3( 2, 0, 0 ) ), new Vector3( 0, 100, 25 ), 1e-4f, "tornado at core edge on +X" );
			// On +Y the tangent flips: cross(Z,(0,2,0)) = -X.
			NearVec( t.Acceleration( new Vector3( 0, 2, 0 ) ), new Vector3( -100, 0, 25 ), 1e-4f, "tornado at core edge on +Y" );
		} );

		registry.Add( "particle-vortex", "tornado: inside-core rotation ramps linearly, updraft grows toward the core", () =>
		{
			var t = Tornado();
			// d = 1 (half the core): rotation 100 * 1/2 = 50; updraft 50 * 2/(1+2) = 33.333.
			NearVec( t.Acceleration( new Vector3( 1, 0, 0 ) ), new Vector3( 0, 50, 100f / 3f ), 1e-4f, "tornado at half core" );
			// d = 4 (twice the core): rotation 100 * 2/4 = 50; updraft 50 * 2/(4+2) = 16.667.
			NearVec( t.Acceleration( new Vector3( 4, 0, 0 ) ), new Vector3( 0, 50, 50f / 3f ), 1e-4f, "tornado at twice core" );
			// Updraft profile is strictly decreasing with distance: axis > half-core > core > twice-core.
			float upAxis = t.Acceleration( Vector3.Zero ).Z;
			float upHalf = t.Acceleration( new Vector3( 1, 0, 0 ) ).Z;
			float upCore = t.Acceleration( new Vector3( 2, 0, 0 ) ).Z;
			float upTwice = t.Acceleration( new Vector3( 4, 0, 0 ) ).Z;
			Near( upAxis, 50, 1e-4f, "tornado updraft at axis" );
			Check( upAxis > upHalf && upHalf > upCore && upCore > upTwice, $"tornado updraft must fall with distance: {upAxis} > {upHalf} > {upCore} > {upTwice}" );
		} );

		registry.Add( "particle-vortex", "tornado: axis is pure updraft, column is height-independent, core never exceeds strength", () =>
		{
			var t = Tornado();
			NearVec( t.Acceleration( Vector3.Zero ), new Vector3( 0, 0, 50 ), 1e-4f, "tornado exactly on the axis" );
			NearVec( t.Acceleration( new Vector3( 2, 0, 5 ) ), new Vector3( 0, 100, 25 ), 1e-4f, "tornado 5 inches up the column" );
			NearVec( t.Acceleration( new Vector3( 2, 0, -7 ) ), new Vector3( 0, 100, 25 ), 1e-4f, "tornado below the origin" );
			for ( float d = 0.001f; d <= 20; d *= 2 )
			{
				Vector3 a = t.Acceleration( new Vector3( d, 0, 0 ) );
				Finite( a, $"tornado at d={d}" );
				float tangential = MathF.Sqrt( a.X * a.X + a.Y * a.Y );
				Check( tangential <= 100 + 1e-4f, $"tornado tangential {tangential} at d={d} exceeds Strength 100" );
			}
			// A hair off the axis is a hair of rotation, not an explosion: 100 * 0.001/2 = 0.05.
			Near( t.Acceleration( new Vector3( 0.001f, 0, 0 ) ).Y, 0.05f, 1e-4f, "tornado 0.001 inch off the axis" );
		} );

		registry.Add( "particle-vortex", "whirlpool: core edge spirals inward and down", () =>
		{
			var w = Whirlpool(); w.Validate();
			// d = 2 = CoreRadius: tangential +Y 80, inward -X 60, drain -Z 40 * 2/(2+2) = 20.
			NearVec( w.Acceleration( new Vector3( 2, 0, 0 ) ), new Vector3( -60, 80, -20 ), 1e-4f, "whirlpool at core edge" );
			// d = 1: tangential 40, inward 30, drain 40 * 2/3 = 26.667 down.
			NearVec( w.Acceleration( new Vector3( 1, 0, 0 ) ), new Vector3( -30, 40, -80f / 3f ), 1e-4f, "whirlpool at half core" );
			// d = 4: tangential 40, inward 30, drain 40 * 2/6 = 13.333 down.
			NearVec( w.Acceleration( new Vector3( 4, 0, 0 ) ), new Vector3( -30, 40, -40f / 3f ), 1e-4f, "whirlpool at twice core" );
			// Every point has an inward (toward-axis) and a downward component.
			foreach ( Vector3 p in new[] { new Vector3( 2, 0, 0 ), new Vector3( 0, 3, 0 ), new Vector3( -1, -1, 4 ) } )
			{
				Vector3 a = w.Acceleration( p );
				Check( Vector3.Dot( a, -new Vector3( p.X, p.Y, 0 ) ) > 0, $"whirlpool at {p} must pull inward, got {a}" );
				Check( a.Z < 0, $"whirlpool at {p} must drain down, got {a}" );
			}
		} );

		registry.Add( "particle-vortex", "whirlpool: axis is pure drain, axis is configurable, non-unit axis behaves like unit", () =>
		{
			var w = Whirlpool();
			NearVec( w.Acceleration( Vector3.Zero ), new Vector3( 0, 0, -40 ), 1e-4f, "whirlpool exactly on the axis is pure drain" );
			// Configurable axis: Axis = +X, particle at (0,2,0). Tangential cross(X,(0,2,0)) = +Z 80;
			// inward -Y 60; drain along -X: 40 * 2/(2+2) = 20.
			var wx = new ParticleWhirlpoolVortex { Center = Vector3.Zero, Axis = new Vector3( 1, 0, 0 ), Strength = 80, InwardStrength = 60, DrainStrength = 40, CoreRadius = 2 };
			wx.Validate();
			NearVec( wx.Acceleration( new Vector3( 0, 2, 0 ) ), new Vector3( -20, -60, 80 ), 1e-4f, "whirlpool with +X axis" );
			// Default drain direction is documented Z-down: with Axis = +Z the drain is -Z (covered above),
			// and flipping the axis flips the drain.
			var flipped = new ParticleWhirlpoolVortex { Center = Vector3.Zero, Axis = new Vector3( 0, 0, -1 ), Strength = 80, InwardStrength = 60, DrainStrength = 40, CoreRadius = 2 };
			NearVec( flipped.Acceleration( Vector3.Zero ), new Vector3( 0, 0, 40 ), 1e-4f, "whirlpool with -Z axis drains +Z" );
			// Non-unit axis: Axis = (0,0,5) must equal Axis = (0,0,1) everywhere.
			var scaled = new ParticleWhirlpoolVortex { Center = Vector3.Zero, Axis = new Vector3( 0, 0, 5 ), Strength = 80, InwardStrength = 60, DrainStrength = 40, CoreRadius = 2 };
			NearVec( scaled.Acceleration( new Vector3( 2, 0, 0 ) ), w.Acceleration( new Vector3( 2, 0, 0 ) ), 1e-4f, "whirlpool non-unit axis" );
		} );

		registry.Add( "particle-vortex", "vortex ring: tube-edge circulation follows the local tangent", () =>
		{
			var r = Ring(); r.Validate();
			// Radially outside the ring (a = +2, b = 0, s = TubeRadius): circulation is +Z at full Strength.
			NearVec( r.Acceleration( new Vector3( 12, 0, 0 ) ), new Vector3( 0, 0, 100 ), 1e-4f, "ring radially outside at tube edge" );
			// Above the ring plane (a = 0, b = +2, s = TubeRadius): circulation is radially inward (-X).
			NearVec( r.Acceleration( new Vector3( 10, 0, 2 ) ), new Vector3( -100, 0, 0 ), 1e-4f, "ring above plane at tube edge" );
			// Radially inside (a = -2, b = 0): circulation is -Z.
			NearVec( r.Acceleration( new Vector3( 8, 0, 0 ) ), new Vector3( 0, 0, -100 ), 1e-4f, "ring radially inside at tube edge" );
			// Below the plane (a = 0, b = -2): circulation is radially outward (+X).
			NearVec( r.Acceleration( new Vector3( 10, 0, -2 ) ), new Vector3( 100, 0, 0 ), 1e-4f, "ring below plane at tube edge" );
			// Tilted axis: Axis = +Y, ring in the XZ plane; radially outside at (12,0,0) circulates +Y.
			var ry = new ParticleVortexRing { Center = Vector3.Zero, Axis = new Vector3( 0, 1, 0 ), RingRadius = 10, TubeRadius = 2, Strength = 100 };
			NearVec( ry.Acceleration( new Vector3( 12, 0, 0 ) ), new Vector3( 0, 100, 0 ), 1e-4f, "ring with +Y axis" );
		} );

		registry.Add( "particle-vortex", "vortex ring: linear ramp inside the tube, cubic falloff outside, far is ~zero", () =>
		{
			var r = Ring();
			// Inside the tube, s = 1 (half the tube): half strength, same tangent as the tube edge.
			NearVec( r.Acceleration( new Vector3( 11, 0, 0 ) ), new Vector3( 0, 0, 50 ), 1e-4f, "ring at half tube radius" );
			NearVec( r.Acceleration( new Vector3( 10, 0, 1 ) ), new Vector3( -50, 0, 0 ), 1e-4f, "ring above plane at half tube radius" );
			// Outside the tube, s = 4: 100 * (2/4)^3 = 12.5.
			NearVec( r.Acceleration( new Vector3( 14, 0, 0 ) ), new Vector3( 0, 0, 12.5f ), 1e-4f, "ring at twice tube radius" );
			// s = 10: 100 * (2/10)^3 = 0.8 — already under 1% of Strength.
			NearVec( r.Acceleration( new Vector3( 20, 0, 0 ) ), new Vector3( 0, 0, 0.8f ), 1e-4f, "ring at 5 tube radii" );
			// Far: s = 50: 100 * (2/50)^3 = 0.0064 — ~nothing.
			Vector3 far = r.Acceleration( new Vector3( 60, 0, 0 ) );
			NearVec( far, new Vector3( 0, 0, 0.0064f ), 1e-4f, "ring far away" );
			Check( far.Length() < 0.01f, $"ring far field must be < 0.01 in/s^2, got {far.Length()}" );
			// Far in the other direction (above the plane) is ~zero too.
			Check( r.Acceleration( new Vector3( 10, 0, 60 ) ).Length() < 0.01f, "ring far above plane must be ~zero" );
		} );

		registry.Add( "particle-vortex", "vortex ring: filament and axis are finite zero, never NaN", () =>
		{
			var r = Ring();
			NearVec( r.Acceleration( new Vector3( 10, 0, 0 ) ), Vector3.Zero, 1e-6f, "ring exactly on the filament" );
			NearVec( r.Acceleration( Vector3.Zero ), Vector3.Zero, 1e-6f, "ring exactly on the axis" );
			Finite( r.Acceleration( new Vector3( 10, 0, 0 ) ), "ring on filament" );
			Finite( r.Acceleration( Vector3.Zero ), "ring on axis" );
			// A hair off the filament is a hair of circulation: s = 0.001 -> 100 * 0.001/2 = 0.05 along +Z.
			NearVec( r.Acceleration( new Vector3( 10.001f, 0, 0 ) ), new Vector3( 0, 0, 0.05f ), 1e-4f, "ring 0.001 inch off the filament" );
		} );

		registry.Add( "particle-vortex", "core clamp shared: no rotational component ever exceeds its strength", () =>
		{
			var t = Tornado();
			var w = Whirlpool();
			var r = Ring();
			for ( float d = 0.001f; d <= 50; d *= 1.5f )
			{
				Vector3 ta = t.Acceleration( new Vector3( d, 0, 0 ) );
				Check( MathF.Sqrt( ta.X * ta.X + ta.Y * ta.Y ) <= 100 + 1e-4f, $"tornado tangential exceeds cap at d={d}" );
				Vector3 wa = w.Acceleration( new Vector3( d, 0, 0 ) );
				float wTangential = MathF.Abs( wa.Y ); // At (d,0,0) the tangent is +Y, inward is -X.
				Check( wTangential <= 80 + 1e-4f && MathF.Abs( wa.X ) <= 60 + 1e-4f, $"whirlpool components exceed caps at d={d}: {wa}" );
				Check( MathF.Abs( wa.Z ) <= 40 + 1e-4f, $"whirlpool drain exceeds cap at d={d}" );
			}
			for ( float s = 0.001f; s <= 50; s += 0.37f )
			{
				Vector3 ra = r.Acceleration( new Vector3( 10 + s, 0, 0 ) );
				Check( ra.Length() <= 100 + 1e-4f, $"ring circulation exceeds cap at s={s}: {ra}" );
			}
			// Peak is attained exactly at each core edge, not just approached.
			Near( t.Acceleration( new Vector3( 2, 0, 0 ) ).Y, 100, 1e-4f, "tornado peak at core edge" );
			Near( w.Acceleration( new Vector3( 2, 0, 0 ) ).Y, 80, 1e-4f, "whirlpool tangential peak at core edge" );
			Near( r.Acceleration( new Vector3( 12, 0, 0 ) ).Z, 100, 1e-4f, "ring peak at tube edge" );
		} );

		registry.Add( "particle-vortex", "validate rejects negative radii and strengths, zero axes, and degenerate rings loudly", () =>
		{
			Throws( () => new ParticleTornadoVortex { Strength = -1, CoreRadius = 2 }.Validate(), "tornado negative strength" );
			Throws( () => new ParticleTornadoVortex { Strength = 100, CoreRadius = -2 }.Validate(), "tornado negative core radius" );
			Throws( () => new ParticleTornadoVortex { Strength = 100, CoreRadius = 0 }.Validate(), "tornado zero core radius" );
			Throws( () => new ParticleTornadoVortex { Strength = 100, CoreRadius = 2, UpdraftStrength = -5 }.Validate(), "tornado negative updraft" );
			Throws( () => new ParticleTornadoVortex { Axis = Vector3.Zero, Strength = 100, CoreRadius = 2 }.Validate(), "tornado zero axis" );
			Throws( () => new ParticleWhirlpoolVortex { Strength = 80, InwardStrength = -1, CoreRadius = 2 }.Validate(), "whirlpool negative inward" );
			Throws( () => new ParticleWhirlpoolVortex { Strength = 80, DrainStrength = -1, CoreRadius = 2 }.Validate(), "whirlpool negative drain" );
			Throws( () => new ParticleWhirlpoolVortex { Axis = Vector3.Zero, Strength = 80, CoreRadius = 2 }.Validate(), "whirlpool zero axis" );
			Throws( () => new ParticleWhirlpoolVortex { Strength = 80, CoreRadius = -1 }.Validate(), "whirlpool negative core radius" );
			Throws( () => new ParticleVortexRing { RingRadius = 10, TubeRadius = -1, Strength = 100 }.Validate(), "ring negative tube radius" );
			Throws( () => new ParticleVortexRing { RingRadius = -10, TubeRadius = 2, Strength = 100 }.Validate(), "ring negative ring radius" );
			Throws( () => new ParticleVortexRing { RingRadius = 1, TubeRadius = 2, Strength = 100 }.Validate(), "ring radius smaller than tube radius" );
			Throws( () => new ParticleVortexRing { RingRadius = 2, TubeRadius = 2, Strength = 100 }.Validate(), "ring radius equal to tube radius" );
			Throws( () => new ParticleVortexRing { RingRadius = 10, TubeRadius = 2, Strength = -100 }.Validate(), "ring negative strength" );
			Throws( () => new ParticleVortexRing { Axis = Vector3.Zero, RingRadius = 10, TubeRadius = 2, Strength = 100 }.Validate(), "ring zero axis" );
			// Valid configurations pass Validate() without throwing.
			Tornado().Validate();
			Whirlpool().Validate();
			Ring().Validate();
		} );

		registry.Add( "particle-vortex", "evaluators are pure: same position, same result, across instances", () =>
		{
			var t1 = Tornado(); var t2 = Tornado();
			var w1 = Whirlpool(); var w2 = Whirlpool();
			var r1 = Ring(); var r2 = Ring();
			foreach ( Vector3 p in new[] { Vector3.Zero, new Vector3( 1, 2, 3 ), new Vector3( 12, 0, 2 ), new Vector3( -5, 4, -1 ) } )
			{
				Check( t1.Acceleration( p ) == t2.Acceleration( p ), $"tornado not pure at {p}" );
				Check( w1.Acceleration( p ) == w2.Acceleration( p ), $"whirlpool not pure at {p}" );
				Check( r1.Acceleration( p ) == r2.Acceleration( p ), $"ring not pure at {p}" );
			}
		} );
	}
}
