#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>
/// Which kill rule fired. None means no rule fired (the particle survives this check).
/// Kept separate from <see cref="ParticleDeathReason"/>: that enum is what ParticleSystem
/// reports on OnDeath (Expired / Killed / Collision); this enum is which predicate fired,
/// so a Killed death can be diagnosed back to one rule.
/// </summary>
public enum ParticleKillRuleKind { None, Age, Speed, Height, Distance, Bounds }

/// <summary>How <see cref="ParticleKillRules.Combine"/> merges several rules.</summary>
public enum ParticleKillCombineMode
{
	/// <summary>Kill if ANY rule fires (the usual culling behaviour).</summary>
	Any,
	/// <summary>Kill only if EVERY rule fires (e.g. "slow AND far away").</summary>
	All,
}

/// <summary>
/// Result of one kill-rule evaluation. FiredRule is the first firing rule in list order
/// (for mode All, when killed, every rule fired and FiredRule is simply the first of them);
/// FiredRules lists every rule that fired. When Kill is false, FiredRule is None and
/// FiredRules is empty, even if some rules fired under mode All without killing.
/// </summary>
public readonly record struct ParticleKillEvaluation( bool Kill, ParticleKillRuleKind FiredRule, ParticleKillRuleKind[] FiredRules );

/// <summary>
/// One pure kill predicate for 3D particles: given a particle's position, velocity and age,
/// answer kill yes/no. Units are ParticleSystem's units: kernel inches, Z-up, seconds.
/// Rules are deliberately stateless and allocation-free on the evaluate path, so a sim step
/// can call them per particle per substep without garbage.
///
/// This file is rules ONLY. It does not touch the sim: it never frees a slot, never fires
/// OnDeath, never spawns sub-emitters (P09 owns death sub-emitters), and it is NOT wired
/// into ParticleSystem.SimulateCpu — the coordinator sweep owns that hookup (see the
/// integration lines in the lane report). Kill planes/spheres are P09's ParticleKillVolume,
/// not these rules; 2D kill rules are P18's lane.
/// </summary>
public abstract class ParticleKillRule
{
	/// <summary>Which kind this rule is, for diagnostics.</summary>
	public abstract ParticleKillRuleKind Kind { get; }

	/// <summary>Human-readable name, used in Validate() errors.</summary>
	public virtual string Name => Kind.ToString();

	/// <summary>Pure predicate: true = kill this particle now.</summary>
	public abstract bool Kills( Vector3 position, Vector3 velocity, float age );

	/// <summary>
	/// Loudly reject a bad configuration. Throws ArgumentException /
	/// ArgumentOutOfRangeException; never silently clamps. This is authoring-time validation
	/// (same pattern as ParticleSystemSettings.Validate, called once per step there) — it is
	/// NOT called by Kills or Combine, so the hot path stays allocation-free.
	/// </summary>
	public abstract void Validate();
}

/// <summary>
/// Age kill. Kills when the particle's age has reached its lifetime.
///
/// Agreement with ParticleSystem.cs (read, not re-invented): at spawn, a particle gets
/// lifeSeconds = Range(LifetimeMin, LifetimeMax) and deathStep = StepIndex +
/// max(1, ceil(life / FixedDeltaTime - 1e-6)); BeginStep kills the slot at the first step
/// where step &gt;= deathStep with reason Expired. So a particle is dead once its age has
/// REACHED its lifetime — the boundary step itself is already dead. This rule therefore
/// uses age &gt;= lifetime, not age &gt; lifetime: a strict-&gt; test would let a particle
/// live one step past the sim's own expiry. Pass the slot's LifetimeOf(slot) as
/// <see cref="Lifetime"/> to match the sim exactly.
/// </summary>
public sealed class ParticleAgeKillRule : ParticleKillRule
{
	public override ParticleKillRuleKind Kind => ParticleKillRuleKind.Age;

	/// <summary>Lifetime in seconds. Must be finite and &gt; 0 (a zero lifetime is rejected: ParticleSystem itself requires 0 &lt; LifetimeMin).</summary>
	public float Lifetime;

	public ParticleAgeKillRule( float lifetime ) { Lifetime = lifetime; }

	public override bool Kills( Vector3 position, Vector3 velocity, float age ) => age >= Lifetime;

	public override void Validate()
	{
		if ( !float.IsFinite( Lifetime ) || Lifetime <= 0 )
			throw new ArgumentOutOfRangeException( nameof( Lifetime ), Lifetime, "Age kill rule: lifetime must be finite and > 0 seconds." );
	}
}

/// <summary>
/// Speed kill. Kills when the particle's speed drops below <see cref="MinSpeed"/>.
/// A freshly spawned particle is often slow (zero launch speed, or a burst that has not
/// accelerated yet), so <see cref="GracePeriod"/> exempts young particles: the rule only
/// fires once age &gt;= GracePeriod. With GracePeriod = 0 the grace is off and a slow
/// particle dies on its first check. Speed is |velocity| in inches per second.
/// </summary>
public sealed class ParticleSpeedKillRule : ParticleKillRule
{
	public override ParticleKillRuleKind Kind => ParticleKillRuleKind.Speed;

	/// <summary>Speed floor in inches/second. Must be finite and &gt;= 0. A floor of 0 never fires (no speed is &lt; 0) and is allowed as an explicit "off, but present" setting.</summary>
	public float MinSpeed;
	/// <summary>Seconds after spawn during which a slow particle is spared. Must be finite and &gt;= 0.</summary>
	public float GracePeriod;

	public ParticleSpeedKillRule( float minSpeed, float gracePeriod = 0f ) { MinSpeed = minSpeed; GracePeriod = gracePeriod; }

	public override bool Kills( Vector3 position, Vector3 velocity, float age ) =>
		age >= GracePeriod && velocity.Length() < MinSpeed;

	public override void Validate()
	{
		if ( !float.IsFinite( MinSpeed ) || MinSpeed < 0 )
			throw new ArgumentOutOfRangeException( nameof( MinSpeed ), MinSpeed, "Speed kill rule: minimum speed must be finite and >= 0." );
		if ( !float.IsFinite( GracePeriod ) || GracePeriod < 0 )
			throw new ArgumentOutOfRangeException( nameof( GracePeriod ), GracePeriod, "Speed kill rule: grace period must be finite and >= 0 seconds." );
	}
}

/// <summary>
/// Height kill. Kills when Z drops below <see cref="Floor"/> or rises above
/// <see cref="Ceiling"/>. This is NOT the ground-plane collision: ParticleSystem's ground
/// plane (GroundEnabled + Restitution + Friction) BOUNCES a particle back up; this rule
/// REMOVES a particle that escapes the vertical band entirely (fell through the world, or
/// flew off the top). The band edges themselves are survivable: exactly Floor or exactly
/// Ceiling does not kill (strict inequalities), matching the ground test's d &lt; 0 shape.
/// </summary>
public sealed class ParticleHeightKillRule : ParticleKillRule
{
	public override ParticleKillRuleKind Kind => ParticleKillRuleKind.Height;

	/// <summary>Lowest survivable Z, kernel inches. Must be finite and &lt;= Ceiling.</summary>
	public float Floor;
	/// <summary>Highest survivable Z, kernel inches. Must be finite and &gt;= Floor.</summary>
	public float Ceiling;

	public ParticleHeightKillRule( float floor, float ceiling ) { Floor = floor; Ceiling = ceiling; }

	public override bool Kills( Vector3 position, Vector3 velocity, float age ) =>
		position.Z < Floor || position.Z > Ceiling;

	public override void Validate()
	{
		if ( !float.IsFinite( Floor ) || !float.IsFinite( Ceiling ) )
			throw new ArgumentException( "Height kill rule: floor and ceiling must be finite." );
		if ( Floor > Ceiling )
			throw new ArgumentException( $"Height kill rule: inverted band — floor {Floor} is above ceiling {Ceiling}." );
	}
}

/// <summary>
/// Distance kill. Kills when the particle is farther than <see cref="MaxDistance"/> from
/// <see cref="Origin"/> (Euclidean distance, kernel inches). Exactly MaxDistance away
/// survives (strict &gt;); this is a culling radius, not a shell — a particle sitting on
/// the shell is still in the scene.
/// </summary>
public sealed class ParticleDistanceKillRule : ParticleKillRule
{
	public override ParticleKillRuleKind Kind => ParticleKillRuleKind.Distance;

	/// <summary>Centre of the culling sphere. Must be finite.</summary>
	public Vector3 Origin;
	/// <summary>Maximum survivable distance, kernel inches. Must be finite and &gt;= 0.</summary>
	public float MaxDistance;

	public ParticleDistanceKillRule( Vector3 origin, float maxDistance ) { Origin = origin; MaxDistance = maxDistance; }

	public override bool Kills( Vector3 position, Vector3 velocity, float age ) =>
		( position - Origin ).Length() > MaxDistance;

	public override void Validate()
	{
		if ( !ParticleMath.Finite( Origin ) )
			throw new ArgumentException( "Distance kill rule: origin must be finite." );
		if ( !float.IsFinite( MaxDistance ) || MaxDistance < 0 )
			throw new ArgumentOutOfRangeException( nameof( MaxDistance ), MaxDistance, "Distance kill rule: maximum distance must be finite and >= 0." );
	}
}

/// <summary>
/// Bounds kill. Kills when the particle leaves the axis-aligned box [Min, Max] on ANY
/// axis — a particle outside in X alone is already out of the box. The box faces
/// themselves are survivable (inclusive): a particle exactly on a face is still inside.
/// Distinct from the distance rule (sphere) and the height rule (Z band only).
/// </summary>
public sealed class ParticleBoundsKillRule : ParticleKillRule
{
	public override ParticleKillRuleKind Kind => ParticleKillRuleKind.Bounds;

	/// <summary>Box corner with the smallest X, Y and Z. Must be finite and &lt;= Max per axis.</summary>
	public Vector3 Min;
	/// <summary>Box corner with the largest X, Y and Z. Must be finite and &gt;= Min per axis.</summary>
	public Vector3 Max;

	public ParticleBoundsKillRule( Vector3 min, Vector3 max ) { Min = min; Max = max; }

	public override bool Kills( Vector3 position, Vector3 velocity, float age ) =>
		position.X < Min.X || position.X > Max.X ||
		position.Y < Min.Y || position.Y > Max.Y ||
		position.Z < Min.Z || position.Z > Max.Z;

	public override void Validate()
	{
		if ( !ParticleMath.Finite( Min ) || !ParticleMath.Finite( Max ) )
			throw new ArgumentException( "Bounds kill rule: min and max must be finite." );
		if ( Min.X > Max.X ) throw new ArgumentException( $"Bounds kill rule: inverted box on X — min {Min.X} is above max {Max.X}." );
		if ( Min.Y > Max.Y ) throw new ArgumentException( $"Bounds kill rule: inverted box on Y — min {Min.Y} is above max {Max.Y}." );
		if ( Min.Z > Max.Z ) throw new ArgumentException( $"Bounds kill rule: inverted box on Z — min {Min.Z} is above max {Max.Z}." );
	}
}

/// <summary>Evaluator for a set of kill rules, plus authoring-time validation.</summary>
public static class ParticleKillRules
{
	/// <summary>
	/// Evaluate a list of rules against one particle.
	/// Mode Any: kill if any rule fires; FiredRule is the first firing rule in list order.
	/// Mode All: kill only if every rule fires; when killed, FiredRule is the first rule
	/// (all fired, so "which fired" is all of them — see FiredRules).
	/// An empty rule list never kills in either mode: All is deliberately NOT vacuous-true,
	/// because "no rules configured" must mean "nothing is culled", not "kill everything".
	/// Unknown mode throws ArgumentOutOfRangeException. Combine does NOT call Validate()
	/// (hot path); call <see cref="ValidateAll"/> at authoring time.
	/// Non-finite position/velocity/age never kill here: every comparison above is false
	/// for NaN, so a corrupted particle survives this check rather than being silently
	/// culled — bad config is caught by Validate(), bad state is not hidden by culling.
	/// </summary>
	public static ParticleKillEvaluation Combine( IReadOnlyList<ParticleKillRule> rules, ParticleKillCombineMode mode, Vector3 position, Vector3 velocity, float age )
	{
		ArgumentNullException.ThrowIfNull( rules );
		if ( mode != ParticleKillCombineMode.Any && mode != ParticleKillCombineMode.All )
			throw new ArgumentOutOfRangeException( nameof( mode ), mode, "Unknown kill combine mode." );
		if ( rules.Count == 0 )
			return new ParticleKillEvaluation( false, ParticleKillRuleKind.None, Array.Empty<ParticleKillRuleKind>() );

		var fired = new List<ParticleKillRuleKind>();
		foreach ( var rule in rules )
		{
			ArgumentNullException.ThrowIfNull( rule );
			if ( rule.Kills( position, velocity, age ) ) fired.Add( rule.Kind );
		}
		bool kill = mode == ParticleKillCombineMode.Any ? fired.Count > 0 : fired.Count == rules.Count;
		return new ParticleKillEvaluation( kill, kill ? fired[0] : ParticleKillRuleKind.None, kill ? fired.ToArray() : Array.Empty<ParticleKillRuleKind>() );
	}

	/// <summary>Convenience: evaluate rules against a live ParticleState (its Position, Velocity, Age).</summary>
	public static ParticleKillEvaluation Combine( IReadOnlyList<ParticleKillRule> rules, ParticleKillCombineMode mode, ParticleState state ) =>
		Combine( rules, mode, state.Position, state.Velocity, state.Age );

	/// <summary>
	/// Loudly validate a whole rule list: every rule's Validate(), in order. Throws on the
	/// first bad rule (ArgumentException / ArgumentOutOfRangeException from that rule).
	/// Null list or null entries throw ArgumentNullException.
	/// </summary>
	public static void ValidateAll( IReadOnlyList<ParticleKillRule> rules )
	{
		ArgumentNullException.ThrowIfNull( rules );
		for ( int i = 0; i < rules.Count; i++ )
		{
			var rule = rules[i] ?? throw new ArgumentNullException( nameof( rules ), $"Kill rule at index {i} is null." );
			rule.Validate();
		}
	}
}
