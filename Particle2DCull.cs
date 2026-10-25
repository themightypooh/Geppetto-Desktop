#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

// Wave-6 lane P18 (particles session, 2026-10-24): self-contained 2D kill/cull
// rules for the flat 2D sim. Flat XY, Y-down canvas pixels, seconds — the same
// units as Particle2D.cs. These are PURE predicates: given a particle's
// position, velocity, and age they return kill yes/no with a documented
// reason. They own no sim state, draw nothing, and do NOT hook into
// Particle2DSystem themselves — the exact hookup lines for the 2D sim's death
// step are reported to the coordinator sweep (see the note at the bottom of
// this file), because Particle2D.cs is another lane's file and is not edited
// here.
//
// Boundary conventions, stated once so every rule agrees:
//   - "Exceeds" a limit means strictly greater, EXCEPT the age rule (below).
//   - A particle exactly ON a limit (bounds edge, distance circle, speed
//     threshold) is ALIVE. Only strictly beyond/ below the limit kills.
//   - The age rule is the exception, deliberately: see Particle2DAgeCullRule.

/// <summary>Which single cull rule a decision is about. None = no rule fired.</summary>
public enum Particle2DCullRuleKind { Age, Bounds, Distance, Speed }

/// <summary>How <see cref="Particle2DCull.Evaluate"/> combines several rules.</summary>
public enum Particle2DCullMode
{
	/// <summary>Kill if any rule fires.</summary>
	Any,
	/// <summary>Kill only if every rule fires.</summary>
	All,
}

/// <summary>One particle's state as the cull rules see it. Build it from a
/// <see cref="Particle2DState"/> by copying Position, Velocity, Age, Lifetime.
/// Speed is derived from Velocity, never stored separately, so the two can
/// never disagree.</summary>
public struct Particle2DCullSample
{
	public Vector2 Position;
	public Vector2 Velocity;
	public float Age;
	public float Lifetime;
	public Particle2DCullSample( Vector2 position, Vector2 velocity, float age, float lifetime )
	{ Position = position; Velocity = velocity; Age = age; Lifetime = lifetime; }
	public float Speed => Velocity.Length();
}

/// <summary>Outcome of one cull evaluation. Fired lists every rule that fired,
/// whether or not the mode turned that into a kill — so an All-mode
/// near-miss still diagnoses which rules fired and which did not. Reason is
/// a human-readable sentence, never empty.</summary>
public readonly record struct Particle2DCullDecision( bool Kill, Particle2DCullRuleKind[] Fired, string Reason )
{
	public bool FiredRule( Particle2DCullRuleKind kind ) => Array.IndexOf( Fired, kind ) >= 0;
}

/// <summary>One pure kill predicate. Constructors validate their own
/// parameters loudly (negative limits, non-finite values); ShouldKill
/// additionally rejects a non-finite sample or, for the age rule, a
/// non-positive lifetime, rather than silently keeping the particle.</summary>
public abstract class Particle2DCullRule
{
	public abstract Particle2DCullRuleKind Kind { get; }
	public string Name => Kind.ToString();
	public abstract bool ShouldKill( in Particle2DCullSample sample, out string reason );
	protected static void CheckSample( in Particle2DCullSample sample )
	{
		if ( !Particle2DMath.Finite( sample.Position ) || !Particle2DMath.Finite( sample.Velocity )
			|| !float.IsFinite( sample.Age ) || !float.IsFinite( sample.Lifetime ) )
			throw new ArgumentException( "Cull sample position, velocity, age, and lifetime must all be finite." );
	}
	protected static void CheckNonNegativeFinite( float value, string name )
	{
		if ( !float.IsFinite( value ) || value < 0 )
			throw new ArgumentOutOfRangeException( name, $"{name} must be finite and >= 0." );
	}
}

/// <summary>Age kill. Kills when age has REACHED the particle's lifetime
/// (age &gt;= lifetime), not strictly exceeded it — and that is not a second
/// lifetime, it is Particle2DSystem's own semantics restated: the sim
/// quantizes lifetime to whole fixed steps (death step = spawn step +
/// ceil(L / FixedDeltaTime - 1e-6), at least 1) and expires the particle at
/// the START of that step, when its accumulated age has reached L. A strict
/// age &gt; lifetime predicate would keep a 1 s particle alive through one
/// extra 60 Hz step (age 1.0 at step 61's start is dead in the sim, alive
/// under strict &gt;). Caveat: the sim's authority is its integer step
/// comparison, not accumulated float age, so a caller that accumulates age
/// in floats may see the last-step age land a float epsilon either side of
/// L; the sim's step expiry, not this predicate, wins that tie.</summary>
public sealed class Particle2DAgeCullRule : Particle2DCullRule
{
	public override Particle2DCullRuleKind Kind => Particle2DCullRuleKind.Age;
	public override bool ShouldKill( in Particle2DCullSample sample, out string reason )
	{
		CheckSample( sample );
		if ( sample.Lifetime <= 0 )
			throw new ArgumentException( "Age cull needs a positive lifetime (Particle2D emitters guarantee LifetimeMin > 0)." );
		if ( sample.Age >= sample.Lifetime )
		{
			reason = $"age {sample.Age} reached lifetime {sample.Lifetime}";
			return true;
		}
		reason = $"age {sample.Age} below lifetime {sample.Lifetime}";
		return false;
	}
}

/// <summary>Bounds kill. Kills when the particle is strictly outside the
/// world rectangle [Min, Max] expanded outward by Margin on every side.
/// Margin is why the brief says "slightly outside still die on time":
/// with Margin = 0 a particle dies the moment it crosses the edge; with
/// Margin &gt; 0 it gets that many extra pixels of slack and dies only past
/// the expanded edge. A particle exactly ON the expanded edge is alive.
/// Y-down pixels: Min.Y is the top edge, Max.Y the bottom edge.</summary>
public sealed class Particle2DBoundsCullRule : Particle2DCullRule
{
	public Vector2 Min { get; }
	public Vector2 Max { get; }
	public float Margin { get; }
	public Particle2DBoundsCullRule( Vector2 min, Vector2 max, float margin = 0 )
	{
		if ( !Particle2DMath.Finite( min ) || !Particle2DMath.Finite( max ) )
			throw new ArgumentException( "Bounds min/max must be finite." );
		if ( max.X <= min.X || max.Y <= min.Y )
			throw new ArgumentException( $"Bounds rectangle is inverted or degenerate: min {min} must be strictly below max {max} on both axes (matches Particle2DSettings wrap validation, which also requires Max > Min)." );
		CheckNonNegativeFinite( margin, nameof( margin ) );
		Min = min; Max = max; Margin = margin;
	}
	public override Particle2DCullRuleKind Kind => Particle2DCullRuleKind.Bounds;
	public override bool ShouldKill( in Particle2DCullSample sample, out string reason )
	{
		CheckSample( sample );
		var p = sample.Position;
		if ( p.X < Min.X - Margin || p.X > Max.X + Margin || p.Y < Min.Y - Margin || p.Y > Max.Y + Margin )
		{
			reason = $"position {p} outside bounds [{Min}, {Max}] expanded by margin {Margin}";
			return true;
		}
		reason = $"position {p} inside bounds [{Min}, {Max}] expanded by margin {Margin}";
		return false;
	}
}

/// <summary>Distance kill. Kills when the particle is strictly farther than
/// MaxDistance from Origin (Euclidean distance, canvas pixels). Exactly at
/// MaxDistance is alive. Origin is configurable and need not be (0, 0) —
/// it is typically the emitter position or the effect centre.</summary>
public sealed class Particle2DDistanceCullRule : Particle2DCullRule
{
	public Vector2 Origin { get; }
	public float MaxDistance { get; }
	public Particle2DDistanceCullRule( Vector2 origin, float maxDistance )
	{
		if ( !Particle2DMath.Finite( origin ) )
			throw new ArgumentException( "Distance origin must be finite." );
		CheckNonNegativeFinite( maxDistance, nameof( maxDistance ) );
		Origin = origin; MaxDistance = maxDistance;
	}
	public override Particle2DCullRuleKind Kind => Particle2DCullRuleKind.Distance;
	public override bool ShouldKill( in Particle2DCullSample sample, out string reason )
	{
		CheckSample( sample );
		float d = (sample.Position - Origin).Length();
		if ( d > MaxDistance )
		{
			reason = $"distance {d} from origin {Origin} exceeds {MaxDistance}";
			return true;
		}
		reason = $"distance {d} from origin {Origin} within {MaxDistance}";
		return false;
	}
}

/// <summary>Speed kill. Kills when speed has dropped strictly BELOW
/// MinSpeed — but only once the grace period has elapsed: while
/// Age &lt; GraceSeconds the particle is never killed by this rule, so a
/// freshly spawned slow (or stationary) particle is not killed instantly.
/// At Age == GraceSeconds the grace has elapsed and a slow particle dies.
/// Speed exactly equal to MinSpeed is alive (it has not dropped below).
/// GraceSeconds = 0 means no grace: a slow particle dies immediately.</summary>
public sealed class Particle2DSpeedCullRule : Particle2DCullRule
{
	public float MinSpeed { get; }
	public float GraceSeconds { get; }
	public Particle2DSpeedCullRule( float minSpeed, float graceSeconds = 0 )
	{
		CheckNonNegativeFinite( minSpeed, nameof( minSpeed ) );
		CheckNonNegativeFinite( graceSeconds, nameof( graceSeconds ) );
		MinSpeed = minSpeed; GraceSeconds = graceSeconds;
	}
	public override Particle2DCullRuleKind Kind => Particle2DCullRuleKind.Speed;
	public override bool ShouldKill( in Particle2DCullSample sample, out string reason )
	{
		CheckSample( sample );
		if ( sample.Age < GraceSeconds )
		{
			reason = $"speed {sample.Speed} below {MinSpeed} but age {sample.Age} is inside grace {GraceSeconds}";
			return false;
		}
		if ( sample.Speed < MinSpeed )
		{
			reason = $"speed {sample.Speed} below {MinSpeed} after grace {GraceSeconds} (age {sample.Age})";
			return true;
		}
		reason = $"speed {sample.Speed} at or above {MinSpeed}";
		return false;
	}
}

/// <summary>Combines several cull rules, and validates rule sets.
/// Any: kill if any rule fires. All: kill only if every rule fires.
/// An empty rule list is rejected loudly in both modes (a cull with no
/// rules is a configuration error, not a vacuous kill / no-kill), as is
/// an unknown mode value or a null rule entry.</summary>
public static class Particle2DCull
{
	public static Particle2DCullDecision Evaluate( IReadOnlyList<Particle2DCullRule> rules, Particle2DCullMode mode, in Particle2DCullSample sample )
	{
		ValidateRules( rules );
		if ( mode != Particle2DCullMode.Any && mode != Particle2DCullMode.All )
			throw new ArgumentException( $"Unknown cull mode {mode}." );
		var fired = new List<Particle2DCullRuleKind>();
		var reasons = new List<string>();
		for ( int i = 0; i < rules.Count; i++ )
		{
			if ( rules[i].ShouldKill( sample, out string reason ) )
			{
				fired.Add( rules[i].Kind );
				reasons.Add( $"{rules[i].Kind}: {reason}" );
			}
		}
		bool kill = mode == Particle2DCullMode.Any ? fired.Count > 0 : fired.Count == rules.Count;
		string reasonText = fired.Count == 0
			? "no cull rule fired"
			: kill
				? $"killed by {string.Join( "; ", reasons )}"
				: $"not killed under {mode} (only {fired.Count} of {rules.Count} rules fired): {string.Join( "; ", reasons )}";
		return new Particle2DCullDecision( kill, fired.ToArray(), reasonText );
	}

	public static void ValidateRules( IReadOnlyList<Particle2DCullRule>? rules )
	{
		if ( rules is null ) throw new ArgumentException( "Cull rule list must not be null." );
		if ( rules.Count == 0 ) throw new ArgumentException( "Cull rule list must not be empty." );
		for ( int i = 0; i < rules.Count; i++ )
			if ( rules[i] is null ) throw new ArgumentException( $"Cull rule at index {i} must not be null." );
	}
}

/// <summary>One bundle of cull configuration, for callers that prefer a
/// settings object over hand-built rules. Validate() loudly rejects
/// negative margins / distances / thresholds / grace, inverted or
/// degenerate bound rectangles, and non-finite values; BuildRules()
/// validates first and then returns the enabled rules in a fixed order
/// (Age, Bounds, Distance, Speed). Age needs no parameter — it reads the
/// particle's own lifetime, so it is on by default; the other rules are
/// off until their Use flag is set. A bundle with no rule enabled at all
/// is rejected by Validate() as a configuration error.</summary>
public sealed class Particle2DCullSettings
{
	public bool UseAge = true;
	public bool UseBounds;
	public Vector2 BoundsMin;
	public Vector2 BoundsMax;
	public float BoundsMargin;
	public bool UseDistance;
	public Vector2 DistanceOrigin;
	public float DistanceMax;
	public bool UseSpeed;
	public float SpeedMin;
	public float SpeedGraceSeconds;

	public void Validate()
	{
		if ( !UseAge && !UseBounds && !UseDistance && !UseSpeed )
			throw new ArgumentException( "Cull settings enable no rule at all; enable at least one." );
		if ( UseBounds )
		{
			// Reuse the rule constructor's own loud checks (single source of truth).
			_ = new Particle2DBoundsCullRule( BoundsMin, BoundsMax, BoundsMargin );
		}
		if ( UseDistance )
			_ = new Particle2DDistanceCullRule( DistanceOrigin, DistanceMax );
		if ( UseSpeed )
			_ = new Particle2DSpeedCullRule( SpeedMin, SpeedGraceSeconds );
	}

	public Particle2DCullRule[] BuildRules()
	{
		Validate();
		var rules = new List<Particle2DCullRule>();
		if ( UseAge ) rules.Add( new Particle2DAgeCullRule() );
		if ( UseBounds ) rules.Add( new Particle2DBoundsCullRule( BoundsMin, BoundsMax, BoundsMargin ) );
		if ( UseDistance ) rules.Add( new Particle2DDistanceCullRule( DistanceOrigin, DistanceMax ) );
		if ( UseSpeed ) rules.Add( new Particle2DSpeedCullRule( SpeedMin, SpeedGraceSeconds ) );
		return rules.ToArray();
	}
}

// COORDINATOR SWEEP — exact hookup into the 2D sim's death step (NOT made
// by this lane; Particle2D.cs is not edited here). In Particle2DSystem,
// add one property:
//     public Particle2DCullRule[]? CullRules { get; set; }
// and in BeginStep(), immediately AFTER the existing deathStep expiry loop
// (after the `if (died) { span.Sort(); span.Reverse(); }` block, before
// `while ( _highWater > 0 && ... ) _highWater--;`), insert:
//     if ( CullRules is { Length: > 0 } )
//     {
//         for ( int s = 0; s < _highWater; s++ )
//         {
//             if ( !_state[s].Alive ) continue;
//             var sample = new Particle2DCullSample( _state[s].Position, _state[s].Velocity, _state[s].Age, _state[s].Lifetime );
//             if ( !Particle2DCull.Evaluate( CullRules, Particle2DCullMode.Any, sample ).Kill ) continue;
//             _state[s].Alive = false;
//             _state[s].Velocity = Vector2.Zero;
//             _freeStack[_freeCount++] = s;
//             AliveCount--;
//             died = true;
//         }
//         if ( died ) { var span2 = _freeStack.AsSpan( 0, _freeCount ); span2.Sort(); span2.Reverse(); }
//     }
// Notes for the sweep: the cull pass must run AFTER the deathStep loop so
// the sim's own lifetime expiry stays authoritative and cull rules never
// resurrect or pre-empt it; a null/empty CullRules means "no culling", the
// current behaviour, so existing particle groups are unaffected until a
// caller opts in. The `died` re-sort above reuses the existing free-stack
// compaction; if the sweep instead moves the cull loop BEFORE the existing
// `if (died)` block, the re-sort line must be dropped (the existing block
// then covers both). Do NOT run cull rules inside Simulate()'s substeps:
// rules are evaluated once per Step(), on post-integration state, matching
// the age/lifetime accounting in BeginStep().
