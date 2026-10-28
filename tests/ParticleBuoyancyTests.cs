using System.Numerics;
using Marionette.Desktop;

// Lane P66 (particles session, 2026-10-24): 3D buoyancy evaluators.
// Same harness shape as ParticleMediaTests: registry.Add("particle-buoyancy", name, body).
// Sweep line (NOT added to the canonical tree — coordinator sweep only):
//   ParticleBuoyancyTests.Register( registry, settings );
// placed immediately after the ParticleMediaTests.Register line (itself still
// sweep-pending, after GpuParticleSystemTests.Register per P38's report). If
// the sweep lands only this lane, place it after GpuParticleSystemTests.Register.
// Units: inches, Z-up, seconds; densities kg/m^3 (air 1.2, water 1000).
// Earth gravity Z = -386.0886 in/s^2. All hand-computed expectations below
// use net = g * (1 - rho_m / rho_p) and buoyant = -g * (rho_m / rho_p).
internal static class ParticleBuoyancyTests
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
		void Throws<T>( Action body, string what ) where T : Exception
		{
			try { body(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: expected {typeof( T ).Name}, got {ex.GetType().Name}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, none thrown" );
		}
		const float G = -386.0886f;
		var gVec = new Vector3( 0, 0, G );

		registry.Add( "particle-buoyancy", "neutral buoyancy is exactly zero", () =>
		{
			var net = ParticleBuoyancy.NetAcceleration( gVec, 1000f, 1000f );
			Check( net == Vector3.Zero, $"neutral net must be exactly zero, got {net}" );
			Check( ParticleBuoyancy.NetAccelerationZ( G, 1000f, 1000f ) == 0f, "neutral scalar net must be exactly 0" );
			// And via the bundle path, at the bundle's own density:
			Check( ParticleBuoyancy.NetAcceleration( MediumBundle.Water, 1000f ) == Vector3.Zero, "water bundle at density 1000 must be exactly zero" );
			Check( ParticleBuoyancy.NetAcceleration( MediumBundle.Air, 1.2f ) == Vector3.Zero, "air bundle at density 1.2 must be exactly zero" );
			// The buoyant term itself is exactly the counter-gravity at neutral:
			NearVec( ParticleBuoyancy.BuoyantAcceleration( gVec, 1000f, 1000f ), new Vector3( 0, 0, 386.0886f ), 0f, "neutral buoyant lift" );
			Console.WriteLine( "neutral: net=0 exactly, buoyant=+386.0886 exactly" );
		} );

		registry.Add( "particle-buoyancy", "air bubble in water rises, hand-computed", () =>
		{
			// rho_m=1000, rho_p=1.2: ratio 833.3333; buoyant 386.0886*833.3333=321740.5; net 321354.4114.
			var b = ParticleBuoyancy.BuoyantAcceleration( gVec, 1000f, 1.2f );
			var net = ParticleBuoyancy.NetAcceleration( gVec, 1000f, 1.2f );
			Near( b.Z, 321740.5f, 0.5f, "air-in-water buoyant" );
			Near( net.Z, 321354.4114f, 0.5f, "air-in-water net" );
			Check( net.Z > 0f, "lighter particle must rise (+Z)" );
			Console.WriteLine( $"air bubble in water: buoyant={b.Z} net={net.Z}" );
		} );

		registry.Add( "particle-buoyancy", "stone in water sinks, hand-computed", () =>
		{
			// rho_m=1000, rho_p=2500: ratio 0.4; buoyant 154.43544; net -231.65316.
			var b = ParticleBuoyancy.BuoyantAcceleration( gVec, 1000f, 2500f );
			var net = ParticleBuoyancy.NetAcceleration( gVec, 1000f, 2500f );
			Near( b.Z, 154.43544f, 1e-3f, "stone-in-water buoyant" );
			Near( net.Z, -231.65316f, 1e-3f, "stone-in-water net" );
			Check( net.Z < 0f, "heavier particle must sink (-Z)" );
			Console.WriteLine( $"stone in water: buoyant={b.Z} net={net.Z}" );
		} );

		registry.Add( "particle-buoyancy", "water drop and stone in air, hand-computed", () =>
		{
			// Drop: rho_m=1.2, rho_p=1000: ratio 0.0012; buoyant 0.46330632; net -385.625294.
			Near( ParticleBuoyancy.NetAccelerationZ( G, 1.2f, 1000f ), -385.625294f, 1e-3f, "drop-in-air net" );
			Near( ParticleBuoyancy.BuoyantAccelerationZ( G, 1.2f, 1000f ), 0.46330632f, 1e-5f, "drop-in-air buoyant" );
			// Stone: rho_m=1.2, rho_p=2500: ratio 0.00048; buoyant 0.18532253; net -385.903277.
			Near( ParticleBuoyancy.NetAccelerationZ( G, 1.2f, 2500f ), -385.903277f, 1e-3f, "stone-in-air net" );
			Near( ParticleBuoyancy.BuoyantAccelerationZ( G, 1.2f, 2500f ), 0.18532253f, 1e-5f, "stone-in-air buoyant" );
			Console.WriteLine( "drop in air net=-385.625294, stone in air net=-385.903277" );
		} );

		registry.Add( "particle-buoyancy", "half-density particle in water rises at exactly one g", () =>
		{
			// rho_m=1000, rho_p=500: ratio 2; buoyant 772.1772; net +386.0886 = -G.
			Near( ParticleBuoyancy.NetAccelerationZ( G, 1000f, 500f ), 386.0886f, 1e-3f, "half-density net" );
			Near( ParticleBuoyancy.BuoyantAccelerationZ( G, 1000f, 500f ), 772.1772f, 1e-3f, "half-density buoyant" );
			Console.WriteLine( "half-density in water: net=+386.0886" );
		} );

		registry.Add( "particle-buoyancy", "vacuum gives zero buoyancy exactly", () =>
		{
			foreach ( float rhoP in new[] { 1.2f, 1000f, 2500f } )
			{
				var b = ParticleBuoyancy.BuoyantAcceleration( gVec, 0f, rhoP );
				Check( b == Vector3.Zero, $"vacuum buoyant must be exactly zero for rho_p={rhoP}, got {b}" );
				var net = ParticleBuoyancy.NetAcceleration( gVec, 0f, rhoP );
				Check( net == gVec, $"vacuum net must equal gravity exactly for rho_p={rhoP}, got {net}" );
			}
			Console.WriteLine( "vacuum: buoyant=0 exactly, net=gravity exactly, all densities" );
		} );

		registry.Add( "particle-buoyancy", "rise-sink sign rule across density pairs", () =>
		{
			(float rhoM, float rhoP, float sign)[] cases =
			{
				( 1000f, 999f, 1f ), ( 1000f, 1001f, -1f ), ( 1.2f, 1.1f, 1f ),
				( 1.2f, 1.3f, -1f ), ( 1000f, 0.5f, 1f ), ( 1000f, 8000f, -1f ),
			};
			foreach ( var (rhoM, rhoP, sign) in cases )
			{
				float net = ParticleBuoyancy.NetAccelerationZ( G, rhoM, rhoP );
				Check( MathF.Sign( net ) == MathF.Sign( sign ), $"rho_m={rhoM} rho_p={rhoP}: net {net} must have sign {sign}" );
			}
			Console.WriteLine( "sign rule holds on 6 pairs either side of neutral" );
		} );

		registry.Add( "particle-buoyancy", "P38 bundle reconciliation: reference density and the neutral disagreement", () =>
		{
			// P38 water: Buoyancy 450, net +63.9114. Archimedes equals 450 only at 857.9747.
			float rhoStar = ParticleBuoyancy.ReferenceDensity( MediumBundle.Water );
			Near( rhoStar, 857.9747f, 1e-2f, "water reference density" );
			Near( ParticleBuoyancy.NetAccelerationZ( MediumBundle.Water, rhoStar ), 63.9114f, 1e-2f, "net at reference density" );
			// The disagreement, stated not hidden: neutral under P66 is 0, P38's bundle net is +63.9114.
			Check( ParticleBuoyancy.NetAccelerationZ( MediumBundle.Water, 1000f ) == 0f, "P66 neutral must be 0" );
			NearVec( MediumBundle.Water.NetGravity(), new Vector3( 0, 0, 63.9114f ), 1e-3f, "P38 bundle net stays +63.9114" );
			Check( ParticleBuoyancy.NetAccelerationZ( MediumBundle.Water, 1000f ) != MediumBundle.Water.NetGravity().Z,
				"P66 neutral and P38 bundle net must differ — that is the reconciliation, not a bug to hide" );
			// Air/space/zero-G bundles have no finite reference density (Buoyancy 0).
			Throws<InvalidOperationException>( () => ParticleBuoyancy.ReferenceDensity( MediumBundle.Air ), "air reference density" );
			Throws<InvalidOperationException>( () => ParticleBuoyancy.ReferenceDensity( MediumBundle.ZeroG ), "zero-G reference density" );
			Console.WriteLine( $"water reference density={rhoStar}, P66-neutral=0 vs P38-net=+63.9114" );
		} );

		registry.Add( "particle-buoyancy", "bundle Buoyancy field is ignored by the density path", () =>
		{
			// Two bundles identical except Buoyancy must give identical P66 nets: never add both lifts.
			var noLift = new MediumBundle( new Vector3( 0, 0, G ), 2.0f, 0.05f, 0f, 1000f );
			var lift = new MediumBundle( new Vector3( 0, 0, G ), 2.0f, 0.05f, 450f, 1000f );
			foreach ( float rhoP in new[] { 500f, 1000f, 2500f } )
			{
				var a = ParticleBuoyancy.NetAcceleration( noLift, rhoP );
				var b = ParticleBuoyancy.NetAcceleration( lift, rhoP );
				Check( a == b, $"bundle Buoyancy must not leak into P66 net at rho_p={rhoP}: {a} vs {b}" );
			}
			Near( ParticleBuoyancy.NetAccelerationZ( MediumBundle.Water, 2500f ), -231.65316f, 1e-3f, "water bundle stone net" );
			Console.WriteLine( "bundle Buoyancy ignored on the density path (0 vs 450 give identical nets)" );
		} );

		registry.Add( "particle-buoyancy", "zero and negative and non-finite densities are rejected loudly", () =>
		{
			// Zero particle density: REJECTED by decision (not maximally buoyant).
			Throws<ArgumentOutOfRangeException>( () => ParticleBuoyancy.NetAcceleration( gVec, 1000f, 0f ), "particle density 0" );
			Throws<ArgumentOutOfRangeException>( () => ParticleBuoyancy.NetAcceleration( gVec, 1000f, -5f ), "particle density negative" );
			Throws<ArgumentOutOfRangeException>( () => ParticleBuoyancy.NetAcceleration( gVec, -1f, 1000f ), "medium density negative" );
			Throws<ArgumentOutOfRangeException>( () => ParticleBuoyancy.NetAcceleration( gVec, float.NaN, 1000f ), "medium density NaN" );
			Throws<ArgumentOutOfRangeException>( () => ParticleBuoyancy.NetAcceleration( gVec, 1000f, float.PositiveInfinity ), "particle density +Inf" );
			Throws<ArgumentOutOfRangeException>( () => ParticleBuoyancy.NetAcceleration( gVec, 1000f, float.NaN ), "particle density NaN" );
			Throws<ArgumentException>( () => ParticleBuoyancy.NetAcceleration( new Vector3( 0, 0, float.NaN ), 1000f, 1000f ), "gravity NaN" );
			Throws<ArgumentOutOfRangeException>( () => ParticleBuoyancy.NetAcceleration( MediumBundle.Water, 0f ), "bundle-path particle density 0" );
			Console.WriteLine( "all loud-failure density classes throw" );
		} );

		registry.Add( "particle-buoyancy", "terminal velocity is exact on hand-computed cases", () =>
		{
			// v_t = a_net / k. Stone in water: -231.65316 / 2.0 = -115.82658.
			Near( ParticleBuoyancy.TerminalVelocity( -231.65316f, 2.0f ), -115.82658f, 1e-3f, "stone-water terminal" );
			// P38-net rising case: +63.9114 / 2.0 = +31.9557.
			Near( ParticleBuoyancy.TerminalVelocity( 63.9114f, 2.0f ), 31.9557f, 1e-3f, "P38-net terminal" );
			// Neutral: exactly 0 at any drag.
			Check( ParticleBuoyancy.TerminalVelocity( 0f, 2.0f ) == 0f, "neutral terminal must be exactly 0" );
			// Half-density in water with air-like drag: 386.0886 / 0.05 = 7721.772.
			Near( ParticleBuoyancy.TerminalVelocity( 386.0886f, 0.05f ), 7721.772f, 1e-2f, "half-density terminal" );
			// Bundle path: stone in water bundle (dragLinear 2.0) = -115.82658.
			Near( ParticleBuoyancy.TerminalVelocity( MediumBundle.Water, 2500f ), -115.82658f, 1e-3f, "bundle stone terminal" );
			Check( ParticleBuoyancy.TerminalVelocity( MediumBundle.Water, 1000f ) == 0f, "bundle neutral terminal must be exactly 0" );
			Console.WriteLine( "terminals: stone-water -115.82658, P38-net +31.9557, neutral 0, half-density 7721.772" );
		} );

		registry.Add( "particle-buoyancy", "terminal velocity loud failures", () =>
		{
			Throws<InvalidOperationException>( () => ParticleBuoyancy.TerminalVelocity( -100f, 0f ), "zero drag has no terminal velocity" );
			Throws<ArgumentOutOfRangeException>( () => ParticleBuoyancy.TerminalVelocity( -100f, -0.5f ), "negative drag" );
			Throws<ArgumentOutOfRangeException>( () => ParticleBuoyancy.TerminalVelocity( float.NaN, 2.0f ), "NaN net acceleration" );
			Throws<ArgumentOutOfRangeException>( () => ParticleBuoyancy.TerminalVelocity( -100f, float.PositiveInfinity ), "infinite drag" );
			Console.WriteLine( "terminal-velocity loud failures all throw" );
		} );

		registry.Add( "particle-buoyancy", "evaluators are deterministic", () =>
		{
			var first = ParticleBuoyancy.NetAcceleration( gVec, 1000f, 1.2f );
			var firstT = ParticleBuoyancy.TerminalVelocity( -231.65316f, 2.0f );
			for ( int i = 0; i < 1000; i++ )
			{
				Check( ParticleBuoyancy.NetAcceleration( gVec, 1000f, 1.2f ) == first, $"repeat {i} changed the net" );
				Check( ParticleBuoyancy.TerminalVelocity( -231.65316f, 2.0f ) == firstT, $"repeat {i} changed the terminal velocity" );
			}
			Console.WriteLine( "1000 repeats bitwise identical" );
		} );

		registry.Add( "particle-buoyancy", "light particle rises through the real sim at the predicted band", () =>
		{
			// Sim accepts a caller gravity override: feed the P66 net for a
			// half-density particle in water (+386.0886) as settings gravity,
			// drag 0, particle at rest. Semi-implicit Euler over 60 steps at
			// 60 Hz gives z = a * h^2 * (1+...+60) = 386.0886 * 0.508333 = 196.26;
			// explicit Euler would give 193.04 — the band covers both.
			var settings = new ParticleSystemSettings { Capacity = 100, Seed = 7, Gravity = new Vector3( 0, 0, 386.0886f ), Drag = 0f };
			var sys = new ParticleSystem( settings );
			sys.AddEmitter( new ParticleEmitter
			{
				Position = new Vector3( 0, 0, 0 ), Direction = new Vector3( 0, 0, 1 ), Speed = 0, SpreadDegrees = 0,
				LifetimeMin = 5f, LifetimeMax = 5f, SizeMin = 1f, SizeMax = 1f,
				Bursts = new System.Collections.Generic.List<ParticleBurst> { new( 0, 1 ) },
			} );
			sys.Step( 60 );
			Check( sys.AliveCount == 1, $"expected 1 particle alive, got {sys.AliveCount}" );
			var st = sys.States[0];
			Console.WriteLine( $"rising sim after 1s: z={st.Position.Z} vz={st.Velocity.Z}" );
			Check( st.Position.Z > 185f && st.Position.Z < 210f, $"rising particle must land z in 185..210, got {st.Position.Z}" );
			Check( st.Velocity.Z > 0f, $"rising velocity must point up, got vz={st.Velocity.Z}" );
		} );

		registry.Add( "particle-buoyancy", "heavy particle sinks through the real sim at the predicted band", () =>
		{
			// P66 net for stone in water (-231.65316) as settings gravity, drag 0.
			// Semi-implicit: -231.65316 * 0.508333 = -117.76; explicit: -115.83 — band covers both.
			var settings = new ParticleSystemSettings { Capacity = 100, Seed = 7, Gravity = new Vector3( 0, 0, -231.65316f ), Drag = 0f };
			var sys = new ParticleSystem( settings );
			sys.AddEmitter( new ParticleEmitter
			{
				Position = new Vector3( 0, 0, 0 ), Direction = new Vector3( 0, 0, 1 ), Speed = 0, SpreadDegrees = 0,
				LifetimeMin = 5f, LifetimeMax = 5f, SizeMin = 1f, SizeMax = 1f,
				Bursts = new System.Collections.Generic.List<ParticleBurst> { new( 0, 1 ) },
			} );
			sys.Step( 60 );
			Check( sys.AliveCount == 1, $"expected 1 particle alive, got {sys.AliveCount}" );
			var st = sys.States[0];
			Console.WriteLine( $"sinking sim after 1s: z={st.Position.Z} vz={st.Velocity.Z}" );
			Check( st.Position.Z > -125f && st.Position.Z < -110f, $"sinking particle must land z in -125..-110, got {st.Position.Z}" );
			Check( st.Velocity.Z < 0f, $"sinking velocity must point down, got vz={st.Velocity.Z}" );
		} );

		registry.Add( "particle-buoyancy", "neutral particle hangs through the real sim", () =>
		{
			// P66 neutral net (exactly 0) as settings gravity, drag 0, at rest: must not move.
			var settings = new ParticleSystemSettings { Capacity = 100, Seed = 7, Gravity = Vector3.Zero, Drag = 0f };
			var sys = new ParticleSystem( settings );
			sys.AddEmitter( new ParticleEmitter
			{
				Position = new Vector3( 0, 0, 10 ), Direction = new Vector3( 0, 0, 1 ), Speed = 0, SpreadDegrees = 0,
				LifetimeMin = 5f, LifetimeMax = 5f, SizeMin = 1f, SizeMax = 1f,
				Bursts = new System.Collections.Generic.List<ParticleBurst> { new( 0, 1 ) },
			} );
			sys.Step( 60 );
			Check( sys.AliveCount == 1, $"expected 1 particle alive, got {sys.AliveCount}" );
			var st = sys.States[0];
			Console.WriteLine( $"neutral sim after 1s: z={st.Position.Z} vz={st.Velocity.Z}" );
			Check( st.Position.Z == 10f, $"neutral particle must hang at z=10, got {st.Position.Z}" );
			Check( st.Velocity.Z == 0f, $"neutral velocity must stay 0, got vz={st.Velocity.Z}" );
		} );
	}
}
