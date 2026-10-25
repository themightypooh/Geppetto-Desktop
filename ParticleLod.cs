#nullable enable
using System;

namespace Marionette.Desktop;

// P35 — 3D LOD / budget rules for the CPU particle system (lane P35, wave 6).
//
// Self-contained on purpose: it takes distances and counts, not a live
// ParticleSystem, so it can be tested without a sim and so the sweep can
// wire it into the sim step / render layer with a few lines (see the
// integration lines in the lane report). Units follow ParticleSystem.cs:
// kernel inches, Z-up, seconds. The sim's fixed step is 60 Hz
// (ParticleSystemSettings.FixedDeltaTime = 1/60 s); ParticleLodSettings
// defaults FixedDeltaTime to the same value so the far-field lag bound
// below is in the same seconds.
//
// What this file is NOT: 2D LOD (lane P20), GPU LOD (separate lane),
// overdraw estimation (separate lane), and any wiring into the sim step
// itself (sweep only). It never kills a live particle on its own: the
// budget enforcer only caps NEW spawns, unless CullOldest is explicitly
// enabled, in which case it reports how many oldest particles the caller
// must cull first — the caller does the culling, this file only counts.

/// <summary>One LOD band. A particle belongs to the first band whose
/// UpperDistance it is strictly below; the last band's UpperDistance is
/// conventionally PositiveInfinity so every distance lands somewhere.
/// RateMultiplier scales the emitter's spawn rate (0 = emit nothing,
/// 1 = full rate; never above 1 — LOD only ever reduces work).
/// SizeMultiplier scales the particle's drawn size (0 = invisible,
/// 1 = authored size; up to 2 is allowed so a distant effect may be
/// kept legible by drawing slightly larger, never more).</summary>
public readonly record struct ParticleLodBand( float UpperDistance, float RateMultiplier, float SizeMultiplier );

/// <summary>LOD + budget configuration. Validate() must be called (or the
/// constructor-equivalent defaults used) before any query; every query
/// also re-checks the fields it depends on, so a mutated settings object
/// fails loudly instead of silently applying a nonsense table.</summary>
public sealed class ParticleLodSettings
{
	/// <summary>Distance bands, strictly increasing UpperDistance, at
	/// least one band. Default: near &lt; 600 in (full), mid &lt; 2400 in
	/// (half rate, 0.85 size), far (quarter rate, 0.7 size).</summary>
	public ParticleLodBand[] Bands =
	{
		new( 600f, 1f, 1f ),
		new( 2400f, 0.5f, 0.85f ),
		new( float.PositiveInfinity, 0.25f, 0.7f ),
	};

	/// <summary>Hard cap on live particles for one system, mirroring
	/// ParticleSystemSettings.Capacity (1..4,000,000 there). 0 is legal
	/// here and means "spawn nothing"; negative is rejected.</summary>
	public int MaxLiveParticles = 10_000;

	/// <summary>Default false: at budget, new spawns are dropped and live
	/// particles are never touched. True: the caller may cull the oldest
	/// live particles to make room; see CullCount().</summary>
	public bool CullOldest;

	/// <summary>Distance beyond which a particle collapses to the cheap
	/// representation (updated only every UpdateIntervalN steps).</summary>
	public float FarDistance = 2400f;

	/// <summary>Far-field update stride N: a far particle is integrated
	/// once every N fixed steps. Must be &gt;= 1; 1 = no skipping.</summary>
	public int UpdateIntervalN = 4;

	/// <summary>Fixed step, seconds. Defaults to the sim's 1/60 s.</summary>
	public float FixedDeltaTime = 1f / 60f;

	public static ParticleLodSettings Default() => new();

	/// <summary>Loudly rejects: an empty band table; unsorted (non
	/// strictly increasing) band thresholds; a non-finite threshold
	/// other than PositiveInfinity on the last band; rate multipliers
	/// outside [0, 1]; size multipliers outside [0, 2]; a negative
	/// budget; a negative or non-finite far distance; a non-positive
	/// update interval; a non-finite or non-positive fixed step.</summary>
	public void Validate()
	{
		if ( Bands is null || Bands.Length == 0 )
			throw new ArgumentException( "LOD band table must contain at least one band." );
		float prev = float.NegativeInfinity;
		for ( int i = 0; i < Bands.Length; i++ )
		{
			var b = Bands[i];
			bool last = i == Bands.Length - 1;
			if ( !last && !float.IsFinite( b.UpperDistance ) )
				throw new ArgumentException( $"LOD band {i}: only the last band may use an infinite upper distance." );
			if ( !( b.UpperDistance > prev ) )
				throw new ArgumentException( $"LOD band {i}: upper distances must be strictly increasing (got {b.UpperDistance} after {prev}); unsorted band thresholds are rejected." );
			if ( float.IsNaN( b.RateMultiplier ) || b.RateMultiplier < 0f || b.RateMultiplier > 1f )
				throw new ArgumentOutOfRangeException( nameof( Bands ), $"LOD band {i}: rate multiplier must be in [0, 1] (got {b.RateMultiplier})." );
			if ( float.IsNaN( b.SizeMultiplier ) || b.SizeMultiplier < 0f || b.SizeMultiplier > 2f )
				throw new ArgumentOutOfRangeException( nameof( Bands ), $"LOD band {i}: size multiplier must be in [0, 2] (got {b.SizeMultiplier})." );
			prev = b.UpperDistance;
		}
		if ( MaxLiveParticles < 0 )
			throw new ArgumentOutOfRangeException( nameof( MaxLiveParticles ), "Live-particle budget must not be negative." );
		if ( !float.IsFinite( FarDistance ) || FarDistance < 0f )
			throw new ArgumentOutOfRangeException( nameof( FarDistance ), "Far distance must be finite and >= 0." );
		if ( UpdateIntervalN < 1 )
			throw new ArgumentOutOfRangeException( nameof( UpdateIntervalN ), "Far-field update interval N must be >= 1." );
		if ( !float.IsFinite( FixedDeltaTime ) || FixedDeltaTime <= 0f )
			throw new ArgumentOutOfRangeException( nameof( FixedDeltaTime ), "Fixed step must be finite and > 0." );
	}

	/// <summary>Index of the band for a camera distance in inches.
	/// Negative or NaN distances are rejected (a distance is >= 0).</summary>
	public int BandIndexFor( float distance )
	{
		if ( float.IsNaN( distance ) || distance < 0f )
			throw new ArgumentOutOfRangeException( nameof( distance ), "Camera distance must be >= 0." );
		Validate();
		for ( int i = 0; i < Bands.Length; i++ )
			if ( distance < Bands[i].UpperDistance ) return i;
		return Bands.Length - 1;
	}

	/// <summary>Emission-rate multiplier for a camera distance.</summary>
	public float RateMultiplierFor( float distance ) => Bands[BandIndexFor( distance )].RateMultiplier;

	/// <summary>Drawn-size multiplier for a camera distance.</summary>
	public float SizeMultiplierFor( float distance ) => Bands[BandIndexFor( distance )].SizeMultiplier;

	/// <summary>How many of <paramref name="requested"/> new spawns are
	/// allowed this step, given <paramref name="currentLive"/> live
	/// particles. Deterministic and priority-to-live: with CullOldest
	/// off it is max(0, MaxLiveParticles - currentLive) capped at
	/// requested, so it is 0 at budget and never negative, and no live
	/// particle is ever displaced. With CullOldest on it returns
	/// requested (the caller must first cull CullCount() oldest).
	/// Negative counts are rejected.</summary>
	public int AllowedSpawns( int currentLive, int requested )
	{
		if ( currentLive < 0 ) throw new ArgumentOutOfRangeException( nameof( currentLive ), "Live count must be >= 0." );
		if ( requested < 0 ) throw new ArgumentOutOfRangeException( nameof( requested ), "Requested spawn count must be >= 0." );
		Validate();
		if ( CullOldest ) return requested;
		int room = MaxLiveParticles - currentLive;
		if ( room <= 0 ) return 0;
		return Math.Min( requested, room );
	}

	/// <summary>How many oldest live particles the caller must cull
	/// before spawning <paramref name="requested"/> new ones. Always 0
	/// with CullOldest off (spawns are dropped instead). With CullOldest
	/// on it is max(0, currentLive + requested - MaxLiveParticles):
	/// exactly enough that currentLive - culled + requested == budget,
	/// including the case where the system is already over budget.</summary>
	public int CullCount( int currentLive, int requested )
	{
		if ( currentLive < 0 ) throw new ArgumentOutOfRangeException( nameof( currentLive ), "Live count must be >= 0." );
		if ( requested < 0 ) throw new ArgumentOutOfRangeException( nameof( requested ), "Requested spawn count must be >= 0." );
		Validate();
		if ( !CullOldest ) return 0;
		int over = currentLive + requested - MaxLiveParticles;
		return over > 0 ? over : 0;
	}

	/// <summary>True when the particle is in the far field (distance
	/// strictly beyond FarDistance) and therefore collapses to the
	/// cheap representation.</summary>
	public bool IsFar( float distance )
	{
		if ( float.IsNaN( distance ) || distance < 0f )
			throw new ArgumentOutOfRangeException( nameof( distance ), "Camera distance must be >= 0." );
		Validate();
		return distance > FarDistance;
	}

	/// <summary>True when a far-field particle is integrated on fixed
	/// step <paramref name="step"/> (phase 0: steps 0, N, 2N, ...).
	/// Near particles are always updated; callers gate on IsFar first.</summary>
	public bool ShouldUpdate( long step )
	{
		if ( step < 0 ) throw new ArgumentOutOfRangeException( nameof( step ), "Step index must be >= 0." );
		Validate();
		return step % UpdateIntervalN == 0;
	}

	/// <summary>Staggered form: particle <paramref name="slot"/> updates
	/// when (step + slot) % N == 0, so far particles do not all update
	/// on the same step (which would spike one frame in N). Each
	/// particle still updates exactly once every N steps.</summary>
	public bool ShouldUpdate( long step, int slot )
	{
		if ( step < 0 ) throw new ArgumentOutOfRangeException( nameof( step ), "Step index must be >= 0." );
		if ( slot < 0 ) throw new ArgumentOutOfRangeException( nameof( slot ), "Slot must be >= 0." );
		Validate();
		return ( step + slot ) % UpdateIntervalN == 0;
	}

	/// <summary>Worst-case stale time of a far particle, seconds:
	/// N fixed steps (the gap between two of its updates, inclusive of
	/// the step it missed). At the default N = 4 and 60 Hz this is
	/// 4/60 = 0.0667 s.</summary>
	public float MaxLagSeconds()
	{
		Validate();
		return UpdateIntervalN * FixedDeltaTime;
	}

	/// <summary>Bounded position error of the far-field approximation:
	/// a particle whose true position moves at speed
	/// <paramref name="speed"/> (inches/second) can lag its drawn
	/// position by at most speed * N * dt inches, because its position
	/// is at most N steps stale and nothing else is approximated (its
	/// velocity is NOT changed by collapsing — only the update rate).
	/// At N = 4, 60 Hz, 120 in/s: at most 8 inches. The error is a pure
	/// lag along the direction of motion (never a sideways jump), it
	/// vanishes the moment the particle comes back inside FarDistance,
	/// and it does not accumulate: each far update re-integrates from
	/// the true state, so the lag resets to zero on every update step.</summary>
	public float MaxPositionLag( float speed )
	{
		if ( float.IsNaN( speed ) || speed < 0f )
			throw new ArgumentOutOfRangeException( nameof( speed ), "Speed must be >= 0." );
		return speed * MaxLagSeconds();
	}
}
