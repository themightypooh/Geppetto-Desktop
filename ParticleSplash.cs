#nullable enable
using System;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>
/// Splash droplets (lane P63): the SECONDARY spawn that happens on a GROUND HIT,
/// not on death. This is deliberately distinct from lane P09's sub-emitters:
/// P09's DeathSubEmitter fires when a particle's lifetime runs out (At End),
/// and its CollisionSubEmitter replaces a parent with children on contact.
/// A splash is neither: the parent KEEPS living (it bounces per
/// <see cref="ParticleSystemSettings.Restitution"/> / Friction, or keeps
/// falling if restitution is 0) and the droplets are an extra, one-shot
/// burst computed from the impact — a pure function over the hit, so the
/// coordinator sweep can call it from the collision step without touching
/// the simulation loop's state. It is NOT wired into the collision step
/// by this lane: see the integration lines in the lane report.
/// Units: kernel inches, Z-up, seconds — the same as
/// <see cref="ParticleSystem"/>. Never uses System.Random: all randomness
/// comes from the caller's <see cref="ParticleRng"/> (SplitMix64/xorshift),
/// so a fixed seed reproduces a fixed splash.
/// </summary>

/// <summary>One droplet produced by <see cref="ParticleSplash.SpawnSplash"/>.
/// Position is the hit point lifted off the ground (see
/// <see cref="SplashSpec.SpawnHeightOffset"/>); Velocity points into the
/// upward cone around +Z (or the supplied ground normal); Size is the
/// droplet's diameter in inches; Lifetime is seconds from spawn.</summary>
public readonly record struct SplashDroplet( Vector3 Position, Vector3 Velocity, float Size, float Lifetime );

/// <summary>Authoring parameters for a ground-hit splash. All ranges are
/// inclusive [min, max] and are sampled uniformly with the caller's RNG.
/// Defaults describe a water splash off a ~0.5 in parent (the
/// water_splash preset's size): droplet sizes 0.06–0.20 in are
/// 0.12–0.40× that parent, so droplets are always strictly smaller than
/// their parent at the documented ratio 0.12–0.40. For a parent of a
/// different size, scale SizeMin/SizeMax by parentSize / 0.5, or use
/// <see cref="ParticleSplash.SpawnSplashForParentSize"/> which does it.
/// </summary>
public sealed class SplashSpec
{
	/// <summary>Droplets requested per hit, before the per-step cap. Must be &gt;= 0.</summary>
	public int DropletCount = 8;
	/// <summary>Droplet launch speed range, inches/second. Must be &gt;= 0, min &lt;= max.</summary>
	public float DropletSpeedMin = 12f;
	public float DropletSpeedMax = 36f;
	/// <summary>Droplet size (diameter) range, inches. Must be &gt;= 0, min &lt;= max,
	/// and smaller than the parent — see the class doc for the 0.12–0.40 ratio.</summary>
	public float DropletSizeMin = 0.06f;
	public float DropletSizeMax = 0.20f;
	/// <summary>Droplet lifetime range, seconds. Must be &gt;= 0, min &lt;= max.</summary>
	public float DropletLifetimeMin = 0.25f;
	public float DropletLifetimeMax = 0.60f;
	/// <summary>Impact speed (magnitude of the hit velocity, inches/second)
	/// at or above which a splash happens. Below it, a gentle landing makes
	/// NO droplets at all. Must be &gt;= 0; 0 means every contact splashes.</summary>
	public float ImpactSpeedThreshold = 24f;
	/// <summary>Half-angle of the upward launch cone, degrees. Must be in
	/// the open interval (0, 90): 0 would be a single vertical line and
	/// 90 would be a flat disc, neither is a splash cone.</summary>
	public float ConeHalfAngleDegrees = 35f;
	/// <summary>Upward lift applied to every droplet's spawn position, inches,
	/// so droplets do not start inside the ground plane. Must be &gt;= 0.
	/// Applied along +Z by <see cref="ParticleSplash.SpawnSplash"/> and along
	/// the supplied normal by <see cref="ParticleSplash.SpawnSplashForNormal"/>.</summary>
	public float SpawnHeightOffset = 0.02f;
	/// <summary>Maximum droplets this spec may emit from ONE hit. A heavy
	/// rain impact that requests more is truncated to this cap; the excess
	/// is DROPPED, never queued, never carried to the next hit or step.
	/// This cap alone does not bound a whole step with many hits — for
	/// that, use <see cref="SplashRateLimiter"/> (it drops across hits
	/// within a step and counts what it dropped). Must be &gt;= 0.</summary>
	public int MaxDropletsPerStep = 256;

	/// <summary>Loudly rejects an invalid spec. Called by every SpawnSplash
	/// entry point, so a bad spec fails at the hit instead of silently
	/// emitting a degenerate splash. Throws ArgumentOutOfRangeException
	/// for negative / out-of-range values and ArgumentException for a
	/// min &gt; max pair or a non-finite value.</summary>
	public void Validate()
	{
		static void Finite( float v, string name )
		{
			if ( !float.IsFinite( v ) ) throw new ArgumentException( $"SplashSpec: {name} must be finite." );
		}
		static void Range( float min, float max, string name )
		{
			Finite( min, name + "Min" ); Finite( max, name + "Max" );
			if ( min < 0 ) throw new ArgumentOutOfRangeException( name + "Min", $"{name}Min must be >= 0." );
			if ( max < min ) throw new ArgumentOutOfRangeException( name + "Max", $"{name}Max must be >= {name}Min." );
		}
		if ( DropletCount < 0 ) throw new ArgumentOutOfRangeException( nameof( DropletCount ), "DropletCount must be >= 0." );
		if ( MaxDropletsPerStep < 0 ) throw new ArgumentOutOfRangeException( nameof( MaxDropletsPerStep ), "MaxDropletsPerStep must be >= 0." );
		Range( DropletSpeedMin, DropletSpeedMax, "DropletSpeed" );
		Range( DropletSizeMin, DropletSizeMax, "DropletSize" );
		Range( DropletLifetimeMin, DropletLifetimeMax, "DropletLifetime" );
		Finite( ImpactSpeedThreshold, nameof( ImpactSpeedThreshold ) );
		if ( ImpactSpeedThreshold < 0 ) throw new ArgumentOutOfRangeException( nameof( ImpactSpeedThreshold ), "ImpactSpeedThreshold must be >= 0." );
		Finite( ConeHalfAngleDegrees, nameof( ConeHalfAngleDegrees ) );
		if ( ConeHalfAngleDegrees <= 0 || ConeHalfAngleDegrees >= 90 ) throw new ArgumentOutOfRangeException( nameof( ConeHalfAngleDegrees ), "ConeHalfAngleDegrees must be in (0, 90)." );
		Finite( SpawnHeightOffset, nameof( SpawnHeightOffset ) );
		if ( SpawnHeightOffset < 0 ) throw new ArgumentOutOfRangeException( nameof( SpawnHeightOffset ), "SpawnHeightOffset must be >= 0." );
	}
}

/// <summary>Per-step droplet budget across MANY hits in one step. A single
/// spec's MaxDropletsPerStep caps one hit; this caps the step: once the
/// budget is spent, further hits in the same step emit nothing and the
/// excess is counted in <see cref="DroppedCount"/> — dropped, never
/// queued for a later step. Call <see cref="ResetStep"/> at the start of
/// each simulation step (the sweep integration does this once per step,
/// not once per hit).</summary>
public sealed class SplashRateLimiter
{
	/// <summary>Total droplets this limiter has allowed through, all steps.</summary>
	public int AllowedCount { get; private set; }
	/// <summary>Total droplets dropped for exceeding the budget, all steps.
	/// This is the only record of dropped droplets: there is no queue.</summary>
	public int DroppedCount { get; private set; }
	int _remaining;

	public SplashRateLimiter( int maxDropletsPerStep )
	{
		if ( maxDropletsPerStep < 0 ) throw new ArgumentOutOfRangeException( nameof( maxDropletsPerStep ), "SplashRateLimiter budget must be >= 0." );
		_remaining = maxDropletsPerStep;
		MaxDropletsPerStep = maxDropletsPerStep;
	}

	public int MaxDropletsPerStep { get; }

	/// <summary>How many of a requested count may still be emitted this step.</summary>
	public int Remaining => _remaining;

	/// <summary>Resets the per-step budget. Dropped/allowed totals are kept.</summary>
	public void ResetStep() => _remaining = MaxDropletsPerStep;

	/// <summary>Claims up to <paramref name="requested"/> droplets from this
	/// step's budget and returns how many were granted; the rest are
	/// counted as dropped. Never returns more than requested.</summary>
	public int Take( int requested )
	{
		if ( requested < 0 ) throw new ArgumentOutOfRangeException( nameof( requested ), "Take count must be >= 0." );
		int granted = Math.Min( requested, _remaining );
		_remaining -= granted;
		AllowedCount += granted;
		DroppedCount += requested - granted;
		return granted;
	}
}

/// <summary>Pure splash computation. No system state is read or written;
/// the caller (sweep integration) owns spawning the returned droplets
/// into the particle system and owns the rate limiter's step reset.</summary>
public static class ParticleSplash
{
	/// <summary>Ground-hit splash around the default +Z ground normal.
	/// Impact speed is |hitVelocity|; at or above
	/// <see cref="SplashSpec.ImpactSpeedThreshold"/> the full (capped)
	/// count is emitted, below it the result is empty — there is no
	/// partial splash for a gentle landing. Droplet count is
	/// min(spec.DropletCount, spec.MaxDropletsPerStep); excess droplets
	/// are dropped, never queued. Deterministic for a fixed RNG state:
	/// the same seed and hit reproduce the same droplets, in order.</summary>
	public static SplashDroplet[] SpawnSplash( Vector3 hitPosition, Vector3 hitVelocity, SplashSpec spec, ref ParticleRng rng )
		=> SpawnSplashForNormal( hitPosition, hitVelocity, spec, new Vector3( 0, 0, 1 ), ref rng );

	/// <summary>Same as <see cref="SpawnSplash"/> but the launch cone opens
	/// around <paramref name="groundNormal"/> and the spawn offset is
	/// applied along it. Use this for a tilted ground plane; the +Z
	/// overload is the flat-ground case (the system's default
	/// GroundNormal). The normal is normalized internally; a zero normal
	/// throws.</summary>
	public static SplashDroplet[] SpawnSplashForNormal( Vector3 hitPosition, Vector3 hitVelocity, SplashSpec spec, Vector3 groundNormal, ref ParticleRng rng )
	{
		if ( spec is null ) throw new ArgumentNullException( nameof( spec ) );
		spec.Validate();
		if ( !ParticleMath.Finite( hitPosition ) || !ParticleMath.Finite( hitVelocity ) )
			throw new ArgumentException( "Splash: hit position/velocity must be finite." );
		if ( groundNormal.LengthSquared() < 1e-12f ) throw new ArgumentException( "Splash: ground normal must be non-zero." );
		var axis = Vector3.Normalize( groundNormal );
		if ( hitVelocity.Length() < spec.ImpactSpeedThreshold ) return Array.Empty<SplashDroplet>();
		int count = Math.Min( spec.DropletCount, spec.MaxDropletsPerStep );
		return Build( hitPosition, spec, axis, count, ref rng );
	}

	/// <summary>Same as <see cref="SpawnSplash"/> but droplet sizes are
	/// scaled by parentSize / 0.5, preserving the documented 0.12–0.40
	/// size ratio for a parent of any size. parentSize must be &gt; 0.</summary>
	public static SplashDroplet[] SpawnSplashForParentSize( Vector3 hitPosition, Vector3 hitVelocity, float parentSize, SplashSpec spec, ref ParticleRng rng )
	{
		if ( spec is null ) throw new ArgumentNullException( nameof( spec ) );
		if ( !float.IsFinite( parentSize ) || parentSize <= 0 ) throw new ArgumentOutOfRangeException( nameof( parentSize ), "Parent size must be finite and > 0." );
		var scaled = new SplashSpec
		{
			DropletCount = spec.DropletCount,
			DropletSpeedMin = spec.DropletSpeedMin, DropletSpeedMax = spec.DropletSpeedMax,
			DropletSizeMin = spec.DropletSizeMin * parentSize / 0.5f,
			DropletSizeMax = spec.DropletSizeMax * parentSize / 0.5f,
			DropletLifetimeMin = spec.DropletLifetimeMin, DropletLifetimeMax = spec.DropletLifetimeMax,
			ImpactSpeedThreshold = spec.ImpactSpeedThreshold,
			ConeHalfAngleDegrees = spec.ConeHalfAngleDegrees,
			SpawnHeightOffset = spec.SpawnHeightOffset,
			MaxDropletsPerStep = spec.MaxDropletsPerStep,
		};
		return SpawnSplash( hitPosition, hitVelocity, scaled, ref rng );
	}

	/// <summary>Splash for one hit, additionally limited by a per-step
	/// <paramref name="limiter"/> shared across hits in the step. The
	/// returned count is min(spec count, spec cap, limiter budget);
	/// everything beyond it is counted in limiter.DroppedCount and
	/// dropped, never queued. The limiter is NOT reset here — the caller
	/// resets it once per step.</summary>
	public static SplashDroplet[] SpawnSplashLimited( Vector3 hitPosition, Vector3 hitVelocity, SplashSpec spec, ref ParticleRng rng, SplashRateLimiter limiter )
	{
		if ( limiter is null ) throw new ArgumentNullException( nameof( limiter ) );
		// Compute the full (per-hit capped) splash first so the RNG stream
		// advances identically whether or not the limiter truncates — a
		// later hit's droplets must not depend on how many were dropped.
		var full = SpawnSplash( hitPosition, hitVelocity, spec, ref rng );
		int granted = limiter.Take( full.Length );
		if ( granted == full.Length ) return full;
		var kept = new SplashDroplet[granted];
		Array.Copy( full, kept, granted );
		return kept;
	}

	static SplashDroplet[] Build( Vector3 hitPosition, SplashSpec spec, Vector3 axis, int count, ref ParticleRng rng )
	{
		var droplets = new SplashDroplet[count];
		var origin = hitPosition + axis * spec.SpawnHeightOffset;
		float halfAngle = spec.ConeHalfAngleDegrees * (MathF.PI / 180f);
		for ( int i = 0; i < count; i++ )
		{
			var dir = RandomInCone( ref rng, axis, halfAngle );
			float speed = rng.Range( spec.DropletSpeedMin, spec.DropletSpeedMax );
			float size = rng.Range( spec.DropletSizeMin, spec.DropletSizeMax );
			float life = rng.Range( spec.DropletLifetimeMin, spec.DropletLifetimeMax );
			droplets[i] = new SplashDroplet( origin, dir * speed, size, life );
		}
		return droplets;
	}

	// Uniform direction inside a cone around axis; same construction as
	// ParticleSystem.RandomInCone (which is private to that class):
	// cos sampled uniformly in [cos(halfAngle), 1], azimuth uniform.
	static Vector3 RandomInCone( ref ParticleRng rng, Vector3 axis, float halfAngle )
	{
		float cosMax = MathF.Cos( halfAngle );
		float z = 1 - rng.NextFloat() * (1 - cosMax);
		float a = rng.NextFloat() * MathF.PI * 2;
		float r = MathF.Sqrt( MathF.Max( 0, 1 - z * z ) );
		var helper = MathF.Abs( axis.Z ) < 0.9f ? new Vector3( 0, 0, 1 ) : new Vector3( 1, 0, 0 );
		var t = Vector3.Normalize( Vector3.Cross( helper, axis ) );
		var b = Vector3.Cross( axis, t );
		return Vector3.Normalize( t * (r * MathF.Cos( a )) + b * (r * MathF.Sin( a )) + axis * z );
	}
}
