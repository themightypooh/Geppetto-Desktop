using System.Numerics;
using Marionette.Desktop;

// Wave-6 lane P18 (particles session, 2026-10-24): tests for Particle2DCull.
// Same harness shape as Particle2DTests: registry.Add("particle-2d-cull", name, body).
// Sweep line (NOT added to the canonical tree — coordinator sweep only):
//   Particle2DCullTests.Register( registry, settings );
// placed after the Particle2DTests.Register line (which is itself still a
// held sweep item, after GpuParticleSystemTests.Register).
internal static class Particle2DCullTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; }
			catch ( InvalidOperationException ) { return; }
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}
		Particle2DCullSample S( float x, float y, float vx, float vy, float age, float lifetime )
			=> new( new Vector2( x, y ), new Vector2( vx, vy ), age, lifetime );
		bool Kill( Particle2DCullRule rule, Particle2DCullSample s, out string reason ) => rule.ShouldKill( s, out reason );

		// ---- Age (matches Particle2DSystem: expiry when age REACHES lifetime) ----
		registry.Add( "particle-2d-cull", "age below lifetime stays alive", () =>
		{
			Check( !Kill( new Particle2DAgeCullRule(), S( 0, 0, 0, 0, 0.99f, 1f ), out _ ), "age 0.99 of lifetime 1.0 must not kill" );
		} );
		registry.Add( "particle-2d-cull", "age exactly at lifetime kills (sim expiry point)", () =>
		{
			Check( Kill( new Particle2DAgeCullRule(), S( 0, 0, 0, 0, 1f, 1f ), out var r ), $"age == lifetime must kill: {r}" );
		} );
		registry.Add( "particle-2d-cull", "age above lifetime kills", () =>
		{
			Check( Kill( new Particle2DAgeCullRule(), S( 0, 0, 0, 0, 1.5f, 1f ), out _ ), "age 1.5 of lifetime 1.0 must kill" );
		} );
		registry.Add( "particle-2d-cull", "age rule rejects non-positive lifetime loudly", () =>
		{
			Throws( () => Kill( new Particle2DAgeCullRule(), S( 0, 0, 0, 0, 0.5f, 0f ), out _ ), "zero lifetime" );
			Throws( () => Kill( new Particle2DAgeCullRule(), S( 0, 0, 0, 0, 0.5f, -1f ), out _ ), "negative lifetime" );
		} );

		// ---- Bounds ----
		registry.Add( "particle-2d-cull", "bounds inside stays alive, on edge stays alive", () =>
		{
			var rule = new Particle2DBoundsCullRule( new Vector2( 0, 0 ), new Vector2( 800, 600 ) );
			Check( !Kill( rule, S( 400, 300, 0, 0, 0, 10 ), out _ ), "centre must stay alive" );
			Check( !Kill( rule, S( 800, 600, 0, 0, 0, 10 ), out _ ), "exactly on max edge must stay alive" );
			Check( !Kill( rule, S( 0, 0, 0, 0, 0, 10 ), out _ ), "exactly on min edge must stay alive" );
		} );
		registry.Add( "particle-2d-cull", "bounds just outside dies on every side", () =>
		{
			var rule = new Particle2DBoundsCullRule( new Vector2( 0, 0 ), new Vector2( 800, 600 ) );
			Check( Kill( rule, S( 800.01f, 300, 0, 0, 0, 10 ), out _ ), "past right edge must kill" );
			Check( Kill( rule, S( -0.01f, 300, 0, 0, 0, 10 ), out _ ), "past left edge must kill" );
			Check( Kill( rule, S( 400, 600.01f, 0, 0, 0, 10 ), out _ ), "past bottom edge (Y-down) must kill" );
			Check( Kill( rule, S( 400, -0.01f, 0, 0, 0, 10 ), out _ ), "past top edge must kill" );
		} );
		registry.Add( "particle-2d-cull", "bounds margin gives slack then kills", () =>
		{
			var rule = new Particle2DBoundsCullRule( new Vector2( 0, 0 ), new Vector2( 800, 600 ), margin: 10 );
			Check( !Kill( rule, S( 805, 300, 0, 0, 0, 10 ), out _ ), "5px past edge inside 10px margin must stay alive" );
			Check( !Kill( rule, S( 810, 300, 0, 0, 0, 10 ), out _ ), "exactly on margin-expanded edge must stay alive" );
			Check( Kill( rule, S( 810.01f, 300, 0, 0, 0, 10 ), out _ ), "past margin-expanded edge must kill" );
		} );
		registry.Add( "particle-2d-cull", "bounds rejects inverted, degenerate, and negative-margin rectangles", () =>
		{
			Throws( () => new Particle2DBoundsCullRule( new Vector2( 800, 0 ), new Vector2( 0, 600 ) ), "inverted X" );
			Throws( () => new Particle2DBoundsCullRule( new Vector2( 0, 600 ), new Vector2( 800, 0 ) ), "inverted Y" );
			Throws( () => new Particle2DBoundsCullRule( new Vector2( 100, 100 ), new Vector2( 100, 600 ) ), "degenerate zero-width" );
			Throws( () => new Particle2DBoundsCullRule( new Vector2( 0, 0 ), new Vector2( 800, 600 ), margin: -1 ), "negative margin" );
		} );

		// ---- Distance ----
		registry.Add( "particle-2d-cull", "distance inside stays alive, exactly at limit stays alive", () =>
		{
			var rule = new Particle2DDistanceCullRule( new Vector2( 100, 100 ), 50 );
			Check( !Kill( rule, S( 130, 100, 0, 0, 0, 10 ), out _ ), "30px from origin inside 50 must stay alive" );
			Check( !Kill( rule, S( 150, 100, 0, 0, 0, 10 ), out _ ), "exactly 50px from origin must stay alive" );
		} );
		registry.Add( "particle-2d-cull", "distance beyond limit kills, measured from origin not world zero", () =>
		{
			var rule = new Particle2DDistanceCullRule( new Vector2( 100, 100 ), 50 );
			Check( Kill( rule, S( 151, 100, 0, 0, 0, 10 ), out _ ), "51px from origin must kill" );
			// 60px from world origin but only 40px from the (100,100) origin: must NOT kill.
			Check( !Kill( rule, S( 100, 60, 0, 0, 0, 10 ), out _ ), "distance must be measured from the configured origin" );
			// Classic 3-4-5 triangle: (103,104) is exactly 5 from (100,100); (106,108) is 10.
			var five = new Particle2DDistanceCullRule( new Vector2( 100, 100 ), 5 );
			Check( !Kill( five, S( 103, 104, 0, 0, 0, 10 ), out _ ), "3-4-5 distance 5 at limit 5 must stay alive" );
			Check( Kill( five, S( 106, 108, 0, 0, 0, 10 ), out _ ), "3-4-5 distance 10 past limit 5 must kill" );
		} );
		registry.Add( "particle-2d-cull", "distance rejects negative limit", () =>
		{
			Throws( () => new Particle2DDistanceCullRule( new Vector2( 0, 0 ), -1 ), "negative max distance" );
		} );

		// ---- Speed + grace ----
		registry.Add( "particle-2d-cull", "speed fast particle stays alive after grace", () =>
		{
			var rule = new Particle2DSpeedCullRule( 10, graceSeconds: 0.5f );
			Check( !Kill( rule, S( 0, 0, 30, 40, 5, 10 ), out _ ), "speed 50 above threshold 10 must stay alive" );
		} );
		registry.Add( "particle-2d-cull", "speed slow particle dies after grace", () =>
		{
			var rule = new Particle2DSpeedCullRule( 10, graceSeconds: 0.5f );
			Check( Kill( rule, S( 0, 0, 3, 4, 5, 10 ), out _ ), "speed 5 below threshold 10 after grace must kill" );
			Check( Kill( rule, S( 0, 0, 0, 0, 5, 10 ), out _ ), "stationary particle after grace must kill" );
		} );
		registry.Add( "particle-2d-cull", "speed grace protects freshly spawned slow particles", () =>
		{
			var rule = new Particle2DSpeedCullRule( 10, graceSeconds: 0.5f );
			Check( !Kill( rule, S( 0, 0, 0, 0, 0, 10 ), out _ ), "stationary at age 0 inside grace must stay alive" );
			Check( !Kill( rule, S( 0, 0, 0, 0, 0.49f, 10 ), out _ ), "stationary at age 0.49 inside grace 0.5 must stay alive" );
		} );
		registry.Add( "particle-2d-cull", "speed at exactly grace elapsed kills, at exactly threshold stays alive", () =>
		{
			var rule = new Particle2DSpeedCullRule( 10, graceSeconds: 0.5f );
			Check( Kill( rule, S( 0, 0, 0, 0, 0.5f, 10 ), out _ ), "slow at age == grace must kill (grace elapsed)" );
			Check( !Kill( rule, S( 0, 0, 6, 8, 5, 10 ), out _ ), "speed exactly 10 at threshold must stay alive" );
		} );
		registry.Add( "particle-2d-cull", "speed rejects negative threshold and negative grace", () =>
		{
			Throws( () => new Particle2DSpeedCullRule( -1 ), "negative speed threshold" );
			Throws( () => new Particle2DSpeedCullRule( 10, graceSeconds: -0.5f ), "negative grace" );
		} );

		// ---- Combine ----
		registry.Add( "particle-2d-cull", "combine Any kills when one rule fires and names it", () =>
		{
			var rules = new Particle2DCullRule[]
			{
				new Particle2DAgeCullRule(),
				new Particle2DBoundsCullRule( new Vector2( 0, 0 ), new Vector2( 800, 600 ) ),
			};
			// Young but far outside: only Bounds fires.
			var d = Particle2DCull.Evaluate( rules, Particle2DCullMode.Any, S( 900, 300, 0, 0, 0.1f, 10 ) );
			Check( d.Kill, "Any must kill when Bounds fires" );
			Check( d.FiredRule( Particle2DCullRuleKind.Bounds ), "diagnostic must name Bounds" );
			Check( !d.FiredRule( Particle2DCullRuleKind.Age ), "diagnostic must not name Age, which did not fire" );
			Check( d.Reason.Contains( "Bounds" ), $"reason must name the fired rule, got: {d.Reason}" );
		} );
		registry.Add( "particle-2d-cull", "combine Any stays alive when nothing fires", () =>
		{
			var rules = new Particle2DCullRule[] { new Particle2DAgeCullRule(), new Particle2DDistanceCullRule( new Vector2( 0, 0 ), 50 ) };
			var d = Particle2DCull.Evaluate( rules, Particle2DCullMode.Any, S( 10, 0, 0, 0, 0.1f, 10 ) );
			Check( !d.Kill && d.Fired.Length == 0, $"Any with no firing rule must not kill, got Kill={d.Kill} Fired={d.Fired.Length}" );
		} );
		registry.Add( "particle-2d-cull", "combine All kills only when every rule fires", () =>
		{
			var rules = new Particle2DCullRule[]
			{
				new Particle2DAgeCullRule(),
				new Particle2DBoundsCullRule( new Vector2( 0, 0 ), new Vector2( 800, 600 ) ),
			};
			var both = Particle2DCull.Evaluate( rules, Particle2DCullMode.All, S( 900, 300, 0, 0, 5, 1 ) );
			Check( both.Kill && both.Fired.Length == 2, "All must kill when both rules fire" );
			var one = Particle2DCull.Evaluate( rules, Particle2DCullMode.All, S( 900, 300, 0, 0, 0.1f, 10 ) );
			Check( !one.Kill, "All must not kill when only Bounds fires" );
			Check( one.Fired.Length == 1 && one.FiredRule( Particle2DCullRuleKind.Bounds ),
				"All near-miss diagnostic must still list the rule that did fire" );
		} );
		registry.Add( "particle-2d-cull", "combine rejects empty list, null entry, and unknown mode", () =>
		{
			var sample = S( 0, 0, 0, 0, 0, 10 );
			Throws( () => Particle2DCull.Evaluate( Array.Empty<Particle2DCullRule>(), Particle2DCullMode.Any, sample ), "empty Any" );
			Throws( () => Particle2DCull.Evaluate( Array.Empty<Particle2DCullRule>(), Particle2DCullMode.All, sample ), "empty All" );
			Throws( () => Particle2DCull.Evaluate( new Particle2DCullRule[] { null! }, Particle2DCullMode.Any, sample ), "null rule" );
			Throws( () => Particle2DCull.Evaluate( new Particle2DCullRule[] { new Particle2DAgeCullRule() }, (Particle2DCullMode)99, sample ), "unknown mode" );
		} );

		// ---- Settings Validate / BuildRules ----
		registry.Add( "particle-2d-cull", "settings validate rejects inverted bounds and negative limits", () =>
		{
			Throws( () => new Particle2DCullSettings { UseAge = false, UseBounds = true, BoundsMin = new Vector2( 800, 0 ), BoundsMax = new Vector2( 0, 600 ) }.Validate(), "settings inverted bounds" );
			Throws( () => new Particle2DCullSettings { UseAge = false, UseDistance = true, DistanceOrigin = new Vector2( 0, 0 ), DistanceMax = -5 }.Validate(), "settings negative distance" );
			Throws( () => new Particle2DCullSettings { UseAge = false, UseSpeed = true, SpeedMin = -1 }.Validate(), "settings negative speed" );
			Throws( () => new Particle2DCullSettings { UseAge = false, UseBounds = true, BoundsMin = new Vector2( 0, 0 ), BoundsMax = new Vector2( 800, 600 ), BoundsMargin = -2 }.Validate(), "settings negative margin" );
			Throws( () => new Particle2DCullSettings { UseAge = false }.Validate(), "settings with no rule enabled" );
		} );
		registry.Add( "particle-2d-cull", "settings build rules in Age, Bounds, Distance, Speed order and they evaluate", () =>
		{
			var settings = new Particle2DCullSettings
			{
				UseBounds = true, BoundsMin = new Vector2( 0, 0 ), BoundsMax = new Vector2( 800, 600 ), BoundsMargin = 0,
				UseDistance = true, DistanceOrigin = new Vector2( 0, 0 ), DistanceMax = 100,
				UseSpeed = true, SpeedMin = 10, SpeedGraceSeconds = 0.5f,
			};
			var rules = settings.BuildRules();
			Check( rules.Length == 4, $"expected 4 rules, got {rules.Length}" );
			Check( rules[0].Kind == Particle2DCullRuleKind.Age && rules[1].Kind == Particle2DCullRuleKind.Bounds
				&& rules[2].Kind == Particle2DCullRuleKind.Distance && rules[3].Kind == Particle2DCullRuleKind.Speed,
				"built rules must be in Age, Bounds, Distance, Speed order" );
			var d = Particle2DCull.Evaluate( rules, Particle2DCullMode.Any, S( 900, 300, 0, 0, 5, 10 ) );
			Check( d.Kill, "built rules must kill an old, out-of-bounds, stationary particle" );
		} );

		// ---- Mapping from a real Particle2DState (the shape the sweep hookup will use) ----
		registry.Add( "particle-2d-cull", "sample built from Particle2DState fields evaluates", () =>
		{
			var state = new Particle2DState { Position = new Vector2( 900, 300 ), Velocity = new Vector2( 0, 0 ), Age = 0.1f, Lifetime = 10f };
			var sample = new Particle2DCullSample( state.Position, state.Velocity, state.Age, state.Lifetime );
			var d = Particle2DCull.Evaluate(
				new Particle2DCullRule[] { new Particle2DBoundsCullRule( new Vector2( 0, 0 ), new Vector2( 800, 600 ) ) },
				Particle2DCullMode.Any, sample );
			Check( d.Kill && d.FiredRule( Particle2DCullRuleKind.Bounds ), "state-mapped sample outside bounds must kill via Bounds" );
		} );
	}
}
