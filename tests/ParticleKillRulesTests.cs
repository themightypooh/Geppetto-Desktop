using System;
using System.Collections.Generic;
using System.Numerics;
using Marionette.Desktop;

// Lane P33 (3D kill rules). Register line for the coordinator sweep — do NOT add it here:
//   ParticleKillRulesTests.Register( registry, settings );  // after GpuParticleSystemTests.Register in TestRegistry.cs
internal static class ParticleKillRulesTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		static void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		static void Throws<T>( Action action, string reason ) where T : Exception
		{
			try { action(); } catch ( T ) { return; }
			throw new Exception( reason );
		}
		static Vector3 P( float x, float y, float z ) => new( x, y, z );
		static Vector3 V( float x, float y, float z ) => new( x, y, z );

		registry.Add( "particle-kill-rules", "age kills at the lifetime boundary, not past it", () =>
		{
			var rule = new ParticleAgeKillRule( 2f );
			// Sim agreement: ParticleSystem kills at the first step where step >= deathStep,
			// i.e. once age has REACHED lifetime — so age == lifetime already kills.
			Check( !rule.Kills( P( 0, 0, 0 ), V( 0, 0, 0 ), 1.999f ), "age 1.999 killed before lifetime 2" );
			Check( rule.Kills( P( 0, 0, 0 ), V( 0, 0, 0 ), 2f ), "age exactly lifetime 2 did not kill" );
			Check( rule.Kills( P( 0, 0, 0 ), V( 0, 0, 0 ), 2.001f ), "age past lifetime did not kill" );
			Check( !rule.Kills( P( 0, 0, 0 ), V( 0, 0, 0 ), 0f ), "fresh particle killed by age rule" );
		} );

		registry.Add( "particle-kill-rules", "speed kill spares slow particles inside the grace period", () =>
		{
			var rule = new ParticleSpeedKillRule( 10f, gracePeriod: 1f );
			var slow = V( 3, 4, 0 ); // speed exactly 5
			var fast = V( 30, 0, 0 );
			Check( !rule.Kills( P( 0, 0, 0 ), slow, 0.5f ), "slow particle killed inside grace period" );
			Check( !rule.Kills( P( 0, 0, 0 ), slow, 0.999f ), "slow particle killed just before grace expired" );
			Check( rule.Kills( P( 0, 0, 0 ), slow, 1f ), "slow particle survived at grace expiry" );
			Check( rule.Kills( P( 0, 0, 0 ), slow, 5f ), "slow particle survived after grace" );
			Check( !rule.Kills( P( 0, 0, 0 ), fast, 5f ), "fast particle killed by speed rule" );
			Check( !rule.Kills( P( 0, 0, 0 ), V( 10, 0, 0 ), 5f ), "particle at exactly MinSpeed killed (rule is strict <)" );
			Check( rule.Kills( P( 0, 0, 0 ), V( 9.999f, 0, 0 ), 5f ), "particle just under MinSpeed survived" );
			var noGrace = new ParticleSpeedKillRule( 10f );
			Check( noGrace.Kills( P( 0, 0, 0 ), slow, 0f ), "zero-grace rule spared a slow newborn" );
		} );

		registry.Add( "particle-kill-rules", "height kills outside the band, band edges survive", () =>
		{
			var rule = new ParticleHeightKillRule( 0f, 100f );
			Check( rule.Kills( P( 0, 0, -0.001f ), V( 0, 0, 0 ), 0f ), "Z below floor survived" );
			Check( !rule.Kills( P( 0, 0, 0 ), V( 0, 0, 0 ), 0f ), "Z exactly on floor killed" );
			Check( !rule.Kills( P( 0, 0, 50 ), V( 0, 0, 0 ), 0f ), "Z inside band killed" );
			Check( !rule.Kills( P( 0, 0, 100 ), V( 0, 0, 0 ), 0f ), "Z exactly on ceiling killed" );
			Check( rule.Kills( P( 0, 0, 100.001f ), V( 0, 0, 0 ), 0f ), "Z above ceiling survived" );
			// Height kill is removal, not the ground-plane bounce: a particle BELOW the floor
			// is killed here even though ParticleSystem's ground collision would bounce it.
			var belowWorld = new ParticleHeightKillRule( -50f, 50f );
			Check( !belowWorld.Kills( P( 0, 0, -50 ), V( 0, 0, 0 ), 0f ), "negative-floor edge killed" );
			Check( belowWorld.Kills( P( 0, 0, -50.001f ), V( 0, 0, 0 ), 0f ), "below negative floor survived" );
		} );

		registry.Add( "particle-kill-rules", "distance kills strictly beyond the radius from the origin", () =>
		{
			var rule = new ParticleDistanceKillRule( P( 10, 0, 0 ), 5f );
			Check( !rule.Kills( P( 10, 0, 0 ), V( 0, 0, 0 ), 0f ), "particle at origin killed" );
			Check( !rule.Kills( P( 15, 0, 0 ), V( 0, 0, 0 ), 0f ), "particle exactly at MaxDistance killed" );
			Check( rule.Kills( P( 15.001f, 0, 0 ), V( 0, 0, 0 ), 0f ), "particle just beyond MaxDistance survived" );
			Check( !rule.Kills( P( 5, 0, 0 ), V( 0, 0, 0 ), 0f ), "particle at opposite shell point killed" );
			Check( rule.Kills( P( 10, 4, 3.001f ), V( 0, 0, 0 ), 0f ), "3-4-5 diagonal just beyond radius survived" );
			Check( !rule.Kills( P( 10, 4, 3 ), V( 0, 0, 0 ), 0f ), "3-4-5 diagonal exactly on radius killed" );
		} );

		registry.Add( "particle-kill-rules", "bounds kills outside any axis, faces survive", () =>
		{
			var rule = new ParticleBoundsKillRule( P( -10, -10, 0 ), P( 10, 10, 100 ) );
			Check( !rule.Kills( P( 0, 0, 50 ), V( 0, 0, 0 ), 0f ), "particle inside box killed" );
			Check( !rule.Kills( P( 10, 10, 100 ), V( 0, 0, 0 ), 0f ), "particle on box corner killed" );
			Check( !rule.Kills( P( -10, 0, 0 ), V( 0, 0, 0 ), 0f ), "particle on box face killed" );
			Check( rule.Kills( P( 10.001f, 0, 50 ), V( 0, 0, 0 ), 0f ), "particle past +X face survived" );
			Check( rule.Kills( P( 0, -10.001f, 50 ), V( 0, 0, 0 ), 0f ), "particle past -Y face survived" );
			Check( rule.Kills( P( 0, 0, -0.001f ), V( 0, 0, 0 ), 0f ), "particle below box survived" );
			Check( rule.Kills( P( 0, 0, 100.001f ), V( 0, 0, 0 ), 0f ), "particle above box survived" );
		} );

		registry.Add( "particle-kill-rules", "combine Any kills on any firing rule and names the first", () =>
		{
			var age = new ParticleAgeKillRule( 1f );
			var speed = new ParticleSpeedKillRule( 10f );
			var rules = new List<ParticleKillRule> { age, speed };
			var oneFires = ParticleKillRules.Combine( rules, ParticleKillCombineMode.Any, P( 0, 0, 0 ), V( 30, 0, 0 ), 2f );
			Check( oneFires.Kill && oneFires.FiredRule == ParticleKillRuleKind.Age, $"Any with only age firing gave kill={oneFires.Kill} rule={oneFires.FiredRule}" );
			Check( oneFires.FiredRules.Length == 1 && oneFires.FiredRules[0] == ParticleKillRuleKind.Age, "Any diagnostic did not list exactly the firing rule" );
			var noneFire = ParticleKillRules.Combine( rules, ParticleKillCombineMode.Any, P( 0, 0, 0 ), V( 30, 0, 0 ), 0.5f );
			Check( !noneFire.Kill && noneFire.FiredRule == ParticleKillRuleKind.None && noneFire.FiredRules.Length == 0, "Any with nothing firing killed or named a rule" );
			var reversed = ParticleKillRules.Combine( new List<ParticleKillRule> { speed, age }, ParticleKillCombineMode.Any, P( 0, 0, 0 ), V( 1, 0, 0 ), 2f );
			Check( reversed.Kill && reversed.FiredRule == ParticleKillRuleKind.Speed && reversed.FiredRules.Length == 2, $"Any first-fired rule is not list order: {reversed.FiredRule}, count {reversed.FiredRules.Length}" );
		} );

		registry.Add( "particle-kill-rules", "combine All kills only when every rule fires", () =>
		{
			var age = new ParticleAgeKillRule( 1f );
			var speed = new ParticleSpeedKillRule( 10f );
			var rules = new List<ParticleKillRule> { age, speed };
			var both = ParticleKillRules.Combine( rules, ParticleKillCombineMode.All, P( 0, 0, 0 ), V( 1, 0, 0 ), 2f );
			Check( both.Kill && both.FiredRules.Length == 2, $"All with both firing gave kill={both.Kill}, fired count {both.FiredRules.Length}" );
			Check( both.FiredRule == ParticleKillRuleKind.Age, $"All kill named {both.FiredRule} instead of the first firing rule" );
			var oneOnly = ParticleKillRules.Combine( rules, ParticleKillCombineMode.All, P( 0, 0, 0 ), V( 30, 0, 0 ), 2f );
			Check( !oneOnly.Kill && oneOnly.FiredRule == ParticleKillRuleKind.None, "All killed when only one rule fired" );
			var none = ParticleKillRules.Combine( rules, ParticleKillCombineMode.All, P( 0, 0, 0 ), V( 30, 0, 0 ), 0.5f );
			Check( !none.Kill, "All killed when no rule fired" );
		} );

		registry.Add( "particle-kill-rules", "combine with no rules never kills, and bad mode throws", () =>
		{
			var empty = new List<ParticleKillRule>();
			Check( !ParticleKillRules.Combine( empty, ParticleKillCombineMode.Any, P( 0, 0, 0 ), V( 0, 0, 0 ), 99f ).Kill, "empty Any killed" );
			Check( !ParticleKillRules.Combine( empty, ParticleKillCombineMode.All, P( 0, 0, 0 ), V( 0, 0, 0 ), 99f ).Kill, "empty All killed (vacuous-true rejected by design)" );
			Throws<ArgumentOutOfRangeException>( () => ParticleKillRules.Combine( empty, (ParticleKillCombineMode)99, P( 0, 0, 0 ), V( 0, 0, 0 ), 0f ), "unknown combine mode did not throw" );
		} );

		registry.Add( "particle-kill-rules", "combine accepts a live ParticleState directly", () =>
		{
			var state = new ParticleState { Position = P( 0, 0, -5 ), Velocity = V( 0, 0, 0 ), Age = 0.5f, Alive = 1 };
			var rules = new List<ParticleKillRule> { new ParticleHeightKillRule( 0f, 100f ) };
			var result = ParticleKillRules.Combine( rules, ParticleKillCombineMode.Any, state );
			Check( result.Kill && result.FiredRule == ParticleKillRuleKind.Height, $"state-based combine gave kill={result.Kill} rule={result.FiredRule}" );
		} );

		registry.Add( "particle-kill-rules", "validate loudly rejects every bad configuration", () =>
		{
			Throws<ArgumentOutOfRangeException>( () => new ParticleAgeKillRule( 0f ).Validate(), "zero lifetime accepted" );
			Throws<ArgumentOutOfRangeException>( () => new ParticleAgeKillRule( -1f ).Validate(), "negative lifetime accepted" );
			Throws<ArgumentOutOfRangeException>( () => new ParticleAgeKillRule( float.NaN ).Validate(), "NaN lifetime accepted" );
			Throws<ArgumentOutOfRangeException>( () => new ParticleSpeedKillRule( -0.1f ).Validate(), "negative speed threshold accepted" );
			Throws<ArgumentOutOfRangeException>( () => new ParticleSpeedKillRule( 10f, -1f ).Validate(), "negative grace period accepted" );
			Throws<ArgumentException>( () => new ParticleHeightKillRule( 100f, 0f ).Validate(), "inverted height band accepted" );
			Throws<ArgumentOutOfRangeException>( () => new ParticleDistanceKillRule( P( 0, 0, 0 ), -5f ).Validate(), "negative max distance accepted" );
			Throws<ArgumentException>( () => new ParticleBoundsKillRule( P( 10, 0, 0 ), P( -10, 10, 10 ) ).Validate(), "inverted X box accepted" );
			Throws<ArgumentException>( () => new ParticleBoundsKillRule( P( 0, 10, 0 ), P( 10, -10, 10 ) ).Validate(), "inverted Y box accepted" );
			Throws<ArgumentException>( () => new ParticleBoundsKillRule( P( 0, 0, 10 ), P( 10, 10, -10 ) ).Validate(), "inverted Z box accepted" );
			Throws<ArgumentException>( () => new ParticleBoundsKillRule( P( float.NaN, 0, 0 ), P( 10, 10, 10 ) ).Validate(), "NaN box corner accepted" );
			Throws<ArgumentException>( () => ParticleKillRules.ValidateAll( new List<ParticleKillRule> { new ParticleAgeKillRule( 1f ), new ParticleDistanceKillRule( P( 0, 0, 0 ), -1f ) } ), "ValidateAll missed a bad second rule" );

			// Valid edge configurations must NOT throw: zero speed floor (never fires, explicit
			// off), zero grace, flat height band, zero distance radius, degenerate point box.
			ParticleKillRules.ValidateAll( new List<ParticleKillRule>
			{
				new ParticleAgeKillRule( 1f ),
				new ParticleSpeedKillRule( 0f, 0f ),
				new ParticleHeightKillRule( 50f, 50f ),
				new ParticleDistanceKillRule( P( 0, 0, 0 ), 0f ),
				new ParticleBoundsKillRule( P( 1, 2, 3 ), P( 1, 2, 3 ) ),
			} );
		} );

		registry.Add( "particle-kill-rules", "non-finite particle state never kills via predicate", () =>
		{
			var age = new ParticleAgeKillRule( 1f );
			Check( !age.Kills( P( 0, 0, 0 ), V( 0, 0, 0 ), float.NaN ), "NaN age killed (NaN comparisons must be false)" );
			var speed = new ParticleSpeedKillRule( 10f );
			Check( !speed.Kills( P( 0, 0, 0 ), new Vector3( float.NaN, 0, 0 ), 5f ), "NaN velocity killed" );
			var height = new ParticleHeightKillRule( 0f, 100f );
			Check( !height.Kills( new Vector3( 0, 0, float.NaN ), V( 0, 0, 0 ), 0f ), "NaN height killed" );
		} );
	}
}
