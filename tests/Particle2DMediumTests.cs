using System.Numerics;
using Marionette.Desktop;

// Lane P75 (particles session, 2026-10-24): 2D medium bundles.
// Same harness shape as Particle2DTests: registry.Add("particle-2d-medium", name, body).
// Sweep line (NOT added to the canonical tree — coordinator sweep only):
//   Particle2DMediumTests.Register( registry, settings );
// placed after the GpuParticleSystemTests.Register line in TestRegistry.cs
// (and after the Particle2DTests.Register line once lane 1's held sweep line
// lands, so the 2D groups stay together in registration order).
// Bundle application hookup (coordinator sweep only, NOT made here): if 2D
// presets are to resolve through bundles, a preset's SettingsFor case becomes
//   s = Particle2DMedium.Apply( Particle2DMedium.Water, s ); // then turbulence
// i.e. Apply first, then the preset's turbulence/wind tweaks — never an edit
// to Particle2D.cs by this lane. The settings fields a bundle cannot carry
// yet (DragQuadratic, Buoyancy, Density) need the sweep lines in the lane
// report, not a smuggle into Drag or Gravity here.
internal static class Particle2DMediumTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void NearVec( Vector2 got, Vector2 want, float tol, string what )
		{
			if ( MathF.Abs( got.X - want.X ) > tol || MathF.Abs( got.Y - want.Y ) > tol )
				throw new Exception( $"{what}: got {got}, want {want} ± {tol}" );
		}
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; }
			catch ( InvalidOperationException ) { return; }
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}

		registry.Add( "particle-2d-medium", "air bundle field values", () =>
		{
			var b = Particle2DMedium.Air;
			Check( b.Gravity == new Vector2( 0, 980f ), $"air gravity: got {b.Gravity}, want (0, 980)" );
			Check( b.DragLinear == 0.05f, $"air dragLinear: got {b.DragLinear}, want 0.05" );
			Check( b.DragQuadratic == 0.0005f, $"air dragQuadratic: got {b.DragQuadratic}, want 0.0005" );
			Check( b.Buoyancy == 0f, $"air buoyancy: got {b.Buoyancy}, want 0" );
			Check( b.Density == 1f, $"air density: got {b.Density}, want 1 (air reference)" );
		} );

		registry.Add( "particle-2d-medium", "water bundle field values", () =>
		{
			var b = Particle2DMedium.Water;
			Check( b.Gravity == new Vector2( 0, 980f ), $"water gravity: got {b.Gravity}, want (0, 980)" );
			Check( b.DragLinear == 2.5f, $"water dragLinear: got {b.DragLinear}, want 2.5" );
			Check( b.DragQuadratic == 0.02f, $"water dragQuadratic: got {b.DragQuadratic}, want 0.02" );
			Check( b.Buoyancy == 0.95f, $"water buoyancy: got {b.Buoyancy}, want 0.95" );
			Check( b.Density == 800f, $"water density: got {b.Density}, want 800 (air = 1 reference)" );
		} );

		registry.Add( "particle-2d-medium", "thick smoke bundle field values", () =>
		{
			var b = Particle2DMedium.ThickSmoke;
			Check( b.Gravity == new Vector2( 0, 980f ), $"smoke gravity: got {b.Gravity}, want (0, 980)" );
			Check( b.DragLinear == 1.8f, $"smoke dragLinear: got {b.DragLinear}, want 1.8" );
			Check( b.DragQuadratic == 0.01f, $"smoke dragQuadratic: got {b.DragQuadratic}, want 0.01" );
			Check( b.Buoyancy == 0.85f, $"smoke buoyancy: got {b.Buoyancy}, want 0.85" );
			Check( b.Density == 1.2f, $"smoke density: got {b.Density}, want 1.2" );
		} );

		registry.Add( "particle-2d-medium", "bundle net gravity: air full, water weak sink, smoke slow settle", () =>
		{
			NearVec( Particle2DMedium.Air.NetGravity(), new Vector2( 0, 980f ), 0.01f, "air net gravity" );
			NearVec( Particle2DMedium.Water.NetGravity(), new Vector2( 0, 49f ), 0.01f, "water net gravity" );
			NearVec( Particle2DMedium.ThickSmoke.NetGravity(), new Vector2( 0, 147f ), 0.01f, "smoke net gravity" );
			Check( Particle2DMedium.Water.NetGravity().Y > 0, "water net gravity must stay positive (sink, not rise) for the reference mote" );
		} );

		registry.Add( "particle-2d-medium", "bubble in water rises: net Y negative in Y-down space", () =>
		{
			// Bubble = air-density particle (p = 1) in water (Density 800, B 0.95):
			// 980 * (1 - 760) / (1 + 380) = -1952.28 px/s^2. Negative Y = up.
			var net = Particle2DMedium.Water.NetAcceleration( 1f );
			Check( net.Y < 0, $"bubble in water must rise (net Y negative), got {net}" );
			Near( net.Y, -1952.28f, 1f, "bubble net Y" );
			Check( net.X == 0, $"bubble net X must be 0, got {net.X}" );
		} );

		registry.Add( "particle-2d-medium", "stone in water sinks slowly: positive but under air gravity", () =>
		{
			// Stone (p = 2500) in water: 980 * (2500 - 760) / (2500 + 380) = 592.08 px/s^2.
			var net = Particle2DMedium.Water.NetAcceleration( 2500f );
			Check( net.Y > 0, $"stone in water must sink (net Y positive), got {net}" );
			Near( net.Y, 592.08f, 1f, "stone net Y" );
			Check( net.Y < 980f, $"stone in water must sink slower than in air, got {net.Y} vs 980" );
		} );

		registry.Add( "particle-2d-medium", "near-neutral mote in water nearly hovers", () =>
		{
			// p = 800 (the medium's own density): 980 * 40 / 1180 = 33.22 px/s^2.
			var net = Particle2DMedium.Water.NetAcceleration( 800f );
			Check( net.Y > 0, $"near-neutral mote must sink very slowly, got {net}" );
			Near( net.Y, 33.22f, 1f, "near-neutral mote net Y" );
			Check( net.Y < 49f, $"near-neutral mote must sink slower than the bundle net 49, got {net.Y}" );
		} );

		registry.Add( "particle-2d-medium", "air net is pure gravity for any particle density", () =>
		{
			// Buoyancy 0: the density term vanishes, so light and heavy agree exactly.
			NearVec( Particle2DMedium.Air.NetAcceleration( 1f ), new Vector2( 0, 980f ), 0.01f, "air net, light particle" );
			NearVec( Particle2DMedium.Air.NetAcceleration( 2500f ), new Vector2( 0, 980f ), 0.01f, "air net, heavy particle" );
		} );

		registry.Add( "particle-2d-medium", "light smoke mote rises while smoke reference settles", () =>
		{
			// p = 0.8 in thick smoke (Density 1.2, B 0.85): 980 * (0.8 - 1.02) / (0.8 + 0.51) = -164.58.
			var net = Particle2DMedium.ThickSmoke.NetAcceleration( 0.8f );
			Check( net.Y < 0, $"light smoke mote must rise (net Y negative), got {net}" );
			Near( net.Y, -164.58f, 1f, "light smoke mote net Y" );
		} );

		registry.Add( "particle-2d-medium", "override not add: hostile base yields exactly the bundle gravity", () =>
		{
			var hostile = new Particle2DSettings
			{
				Gravity = new Vector2( 0, 9999f ),
				GravityAngleDegrees = 45f,
				Drag = 99f,
			};
			var applied = Particle2DMedium.Apply( Particle2DMedium.Water, hostile );
			NearVec( applied.Gravity, new Vector2( 0, 49f ), 0.01f, "applied water gravity under hostile base" );
			Check( applied.Gravity != new Vector2( 0, 9999f + 49f ), "applied gravity must not be base + bundle" );
			Check( applied.Gravity != new Vector2( 0, 9999f ), "applied gravity must not be the base gravity" );
			Check( applied.Drag == 2.5f, $"applied drag must be the bundle linear drag, got {applied.Drag}" );
			Check( applied.GravityAngleDegrees is null, "Apply must clear a stale gravity angle so it cannot re-aim bundle gravity" );
			// Pure function: the hostile input is untouched.
			Check( hostile.Gravity == new Vector2( 0, 9999f ), "Apply must not mutate its input settings" );
			Check( hostile.Drag == 99f, "Apply must not mutate its input drag" );
			Check( !ReferenceEquals( applied, hostile ), "Apply must return a new settings object" );
		} );

		registry.Add( "particle-2d-medium", "apply leaves non-medium fields untouched", () =>
		{
			var baseSettings = new Particle2DSettings
			{
				Capacity = 1234,
				Seed = 77,
				FixedDeltaTime = 1f / 30f,
				Substeps = 2,
				Gravity = new Vector2( 10, 20 ),
				Wind = new Vector2( 500, -500 ),
				Drag = 3f,
				TurbulenceStrength = 77f,
				TurbulenceFrequency = 0.03f,
				TurbulenceSpeed = 1.5f,
				VortexStrength = 9f,
				VortexCenter = new Vector2( 40, 50 ),
				VortexRadius = 7f,
				FloorEnabled = true,
				FloorY = 123f,
				WrapEnabled = true,
				WrapMin = new Vector2( 0, 0 ),
				WrapMax = new Vector2( 800, 600 ),
				Restitution = 0.7f,
				Friction = 0.4f,
			};
			var applied = Particle2DMedium.Apply( Particle2DMedium.Air, baseSettings );
			Check( applied.Capacity == 1234, "Capacity must survive Apply" );
			Check( applied.Seed == 77, "Seed must survive Apply" );
			Check( applied.FixedDeltaTime == 1f / 30f, "FixedDeltaTime must survive Apply" );
			Check( applied.Substeps == 2, "Substeps must survive Apply" );
			Check( applied.Wind == new Vector2( 500, -500 ), $"Wind must survive Apply, got {applied.Wind}" );
			Check( applied.TurbulenceStrength == 77f, "TurbulenceStrength must survive Apply" );
			Check( applied.TurbulenceFrequency == 0.03f, "TurbulenceFrequency must survive Apply" );
			Check( applied.TurbulenceSpeed == 1.5f, "TurbulenceSpeed must survive Apply" );
			Check( applied.VortexStrength == 9f, "VortexStrength must survive Apply" );
			Check( applied.VortexCenter == new Vector2( 40, 50 ), "VortexCenter must survive Apply" );
			Check( applied.VortexRadius == 7f, "VortexRadius must survive Apply" );
			Check( applied.FloorEnabled && applied.FloorY == 123f, "Floor must survive Apply" );
			Check( applied.WrapEnabled && applied.WrapMin == new Vector2( 0, 0 ) && applied.WrapMax == new Vector2( 800, 600 ), "Wrap must survive Apply" );
			Check( applied.Restitution == 0.7f, "Restitution must survive Apply" );
			Check( applied.Friction == 0.4f, "Friction must survive Apply" );
		} );

		registry.Add( "particle-2d-medium", "apply does not smuggle quadratic, buoyancy, or density into drag or gravity", () =>
		{
			var applied = Particle2DMedium.Apply( Particle2DMedium.Water, new Particle2DSettings() );
			Check( applied.Drag == 2.5f, $"applied drag must equal DragLinear exactly, got {applied.Drag}" );
			Check( applied.Drag != 2.5f + 0.02f, "quadratic drag must not be folded into linear Drag" );
			NearVec( applied.Gravity, Particle2DMedium.Water.NetGravity(), 0.01f, "applied gravity must equal bundle NetGravity" );
		} );

		registry.Add( "particle-2d-medium", "resolve name: prefixes, case, whole words", () =>
		{
			Check( Particle2DMedium.ResolveName( "air" ) == "air", "plain air" );
			Check( Particle2DMedium.ResolveName( "AIR" ) == "air", "case-insensitive air" );
			Check( Particle2DMedium.ResolveName( "fx:water" ) == "water", "fx: prefix" );
			Check( Particle2DMedium.ResolveName( "vfx:Water" ) == "water", "vfx: prefix, mixed case" );
			Check( Particle2DMedium.ResolveName( "particle:thick_smoke" ) == "thick_smoke", "particle: prefix" );
			Check( Particle2DMedium.ResolveName( "fx_thick-smoke" ) == "thick_smoke", "fx_ prefix with dash form" );
			Check( Particle2DMedium.ResolveName( "morning_water_splash" ) == "water", "whole-word middle match" );
			Check( Particle2DMedium.ResolveName( "underwater" ) is null, "substring underwater must NOT resolve to water" );
			Check( Particle2DMedium.ResolveName( "smoke" ) is null, "bare smoke must NOT resolve to thick_smoke" );
			Check( Particle2DMedium.ResolveName( "helium" ) is null, "unknown name must return null" );
			Check( Particle2DMedium.ResolveName( "" ) is null, "empty name must return null" );
			Check( Particle2DMedium.ResolveName( "   " ) is null, "whitespace name must return null" );
			Check( Particle2DMedium.ResolveName( null ) is null, "null name must return null" );
		} );

		registry.Add( "particle-2d-medium", "bundle for resolves prefixed names and throws on unknown", () =>
		{
			Check( Particle2DMedium.BundleFor( "fx:water" ).Density == 800f, "BundleFor must resolve prefixed water" );
			Check( Particle2DMedium.BundleFor( "THICK_SMOKE" ).Buoyancy == 0.85f, "BundleFor must resolve upper-case thick_smoke" );
			Throws( () => Particle2DMedium.BundleFor( "helium" ), "BundleFor unknown name" );
			Throws( () => Particle2DMedium.BundleFor( "underwater" ), "BundleFor substring name" );
		} );

		registry.Add( "particle-2d-medium", "validate rejects bad bundles loudly", () =>
		{
			Throws( () => { _ = new Particle2DMedium( new Vector2( float.NaN, 980f ), 0.05f, 0.0005f, 0f, 1f ); }, "non-finite gravity" );
			Throws( () => { _ = new Particle2DMedium( new Vector2( 0, 980f ), -0.1f, 0.0005f, 0f, 1f ); }, "negative dragLinear" );
			Throws( () => { _ = new Particle2DMedium( new Vector2( 0, 980f ), 0.05f, -0.01f, 0f, 1f ); }, "negative dragQuadratic" );
			Throws( () => { _ = new Particle2DMedium( new Vector2( 0, 980f ), 0.05f, 0.0005f, -0.1f, 1f ); }, "buoyancy below 0" );
			Throws( () => { _ = new Particle2DMedium( new Vector2( 0, 980f ), 0.05f, 0.0005f, 1.1f, 1f ); }, "buoyancy above 1" );
			Throws( () => { _ = new Particle2DMedium( new Vector2( 0, 980f ), 0.05f, 0.0005f, float.NaN, 1f ); }, "non-finite buoyancy" );
			Throws( () => { _ = new Particle2DMedium( new Vector2( 0, 980f ), 0.05f, 0.0005f, 0f, 0f ); }, "zero density" );
			Throws( () => { _ = new Particle2DMedium( new Vector2( 0, 980f ), 0.05f, 0.0005f, 0f, -5f ); }, "negative density" );
			Throws( () => { _ = new Particle2DMedium( new Vector2( 0, 980f ), 0.05f, 0.0005f, 0f, float.PositiveInfinity ); }, "non-finite density" );
			Throws( () => { default( Particle2DMedium ).Validate(); }, "default bundle (density 0) must fail validation" );
			Throws( () => { _ = Particle2DMedium.Apply( default, new Particle2DSettings() ); }, "Apply of a default bundle must fail" );
			Throws( () => { _ = Particle2DMedium.Apply( Particle2DMedium.Air, null! ); }, "Apply of null settings must fail" );
		} );

		registry.Add( "particle-2d-medium", "net acceleration rejects bad particle densities", () =>
		{
			Throws( () => { _ = Particle2DMedium.Water.NetAcceleration( 0f ); }, "zero particle density" );
			Throws( () => { _ = Particle2DMedium.Water.NetAcceleration( -2f ); }, "negative particle density" );
			Throws( () => { _ = Particle2DMedium.Water.NetAcceleration( float.NaN ); }, "non-finite particle density" );
		} );

		registry.Add( "particle-2d-medium", "applied settings feed a live 2d system unchanged", () =>
		{
			var applied = Particle2DMedium.Apply( Particle2DMedium.Water, new Particle2DSettings() );
			applied.Validate();
			var sys = new Particle2DSystem( applied );
			NearVec( sys.Settings.Gravity, new Vector2( 0, 49f ), 0.01f, "live system gravity after water Apply" );
			Check( sys.Settings.Drag == 2.5f, $"live system drag after water Apply, got {sys.Settings.Drag}" );
			sys.Step( 1 );
			Check( sys.AliveCount == 0, "empty system must stay empty after a step" );
		} );
	}
}
