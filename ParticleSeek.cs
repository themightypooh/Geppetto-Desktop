#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>
/// Deterministic seek and prewarm for the 3D CPU particle sim (lane P36).
/// Units are kernel inches, Z-up, seconds, like <see cref="ParticleSystem"/>.
/// Fixed step is <see cref="ParticleSystemSettings.FixedDeltaTime"/> (60 Hz by default).
///
/// WHY A BLUEPRINT, NOT A RESET: <see cref="ParticleSystem"/> exposes no Reset.
/// Its emitter RNG streams (<c>ParticleEmitter.Rng</c>) and rate remainders are
/// internal, and <c>StepIndex</c> has no public setter, so no caller can rewind
/// a live system in place without editing ParticleSystem.cs (this lane must not).
/// A <see cref="ParticleSeek"/> therefore snapshots the system's configuration
/// at construction — settings plus every emitter, deep-cloned — and every seek
/// builds a FRESH system from that snapshot and fixed-steps it forward. That is
/// what makes seeking deterministic: the rebuild re-seeds every emitter stream
/// from the same seed in the same order, so Seek(t) twice, and Seek(t) versus
/// stepping a fresh system live to t, run the identical RNG sequence.
///
/// SNAPSHOT RULE: construct the seeker AFTER the system is fully configured
/// (all emitters added) and, normally, before it is stepped. Later mutation of
/// the prototype system does NOT change the seeker: the blueprint is a clone,
/// not a reference. Emitters added to the prototype after construction are
/// not seen by the seeker either — build a new seeker for a new configuration.
///
/// SEEK SEMANTICS: Seek(t) snaps t to the nearest fixed step
/// (<see cref="StepsFor"/>); the returned system's <c>Time</c> can differ from
/// the requested t by less than half a step. There is no fractional-step seek:
/// the sim only exists at fixed steps, and inventing one would break the
/// Seek(t) == live-stepped equality this file proves.
///
/// SEEK-TO-CUE SEMANTICS: <see cref="SeekToCue"/> positions the sim ONE fixed
/// step before the cue step. It assumes the cue is already scheduled at the cue
/// step — lane 4's bridge retimes a burst to the current step exactly the way
/// <see cref="ParticleSystem.TriggerCue"/> does, and this helper never retimes
/// anything itself (cue firing stays the bridge's job). A burst scheduled at
/// step k fires during the Step() that starts at StepIndex k, so from the
/// SeekToCue position (k − 1) the first Step() merely advances the clock to
/// the cue step and the second Step() fires the burst — on its scheduled step,
/// never early. A burst still sitting at step 0 is NOT moved by SeekToCue:
/// seeking past step 0 has already fired it, so the caller must retime it to
/// the cue step first. SeekToCue(0) returns the fresh system (StepIndex 0).
///
/// COST RULE (prewarm is not free): prewarm steps are real sim steps. The cost
/// of Seek(t) / Prewarm(n) is n × (per-step emission + alive particles ×
/// Substeps integrations + collision checks). Prewarming a looping ember
/// fountain for 10 s at 60 Hz costs 600 full steps before the first rendered
/// frame; nothing is cached and nothing is skipped. <see cref="MaxSeekSeconds"/>
/// (default 600 s) bounds runaway seeks; see <see cref="ValidateSteps"/>.
///
/// SEEK PARAMS: <see cref="ParticleSeekParams"/> bundles a TargetTime /
/// PrewarmSteps pair with Validate(); SeekWith/PrewarmWith apply it through
/// a seeker (which enforces its own dt-dependent step ceiling). The record
/// never combines the two operations: Seek resets to the initial state, so
/// "prewarm then seek" would just be the seek.
///
/// WHAT IS NOT HERE, ON PURPOSE:
/// - 2D seek/prewarm: a separate lane, if needed. This file is 3D CPU only.
/// - GPU seek: not attempted. GPU state lives in SSBOs on the GPU, the CPU
///   path throws once <c>SimulatedOnGpu</c> is set (ParticleSystem.Step
///   refuses a GPU-driven system), and there is no CPU-side snapshot to
///   rebuild from. Resetting a GPU sim would need a GPU dispatch plus an
///   entropy policy for its streams (resetting by assignment collapses
///   distinct particles into identical paths); that is a GPU-lane job.
/// - Timeline-mark cue seeking, save/serialization, and viewport wiring:
///   other lanes / the coordinator sweep. This file only positions a sim.
/// </summary>
public sealed class ParticleSeek
{
	/// <summary>Default seek/prewarm ceiling, seconds of sim time (10 minutes).</summary>
	public const double DefaultMaxSeekSeconds = 600.0;

	readonly ParticleSystemSettings? _settings;
	readonly List<ParticleEmitter>? _emitters;
	readonly Func<ParticleSystem>? _factory;

	/// <summary>Seek ceiling for this seeker, seconds. Lower it for interactive scrubbing.</summary>
	public double MaxSeekSeconds { get; set; } = DefaultMaxSeekSeconds;

	/// <summary>Fixed step of the blueprinted system, seconds.</summary>
	public float FixedDeltaTime => _settings?.FixedDeltaTime ?? BuildFresh().Settings.FixedDeltaTime;

	/// <summary>Maximum seekable step count under the current ceiling and fixed step.</summary>
	public long MaxSteps => (long)Math.Floor( MaxSeekSeconds / (double)FixedDeltaTime );

	/// <summary>Snapshot a fully-configured system. The blueprint is cloned; later
	/// mutation of <paramref name="prototype"/> does not affect this seeker.</summary>
	public ParticleSeek( ParticleSystem prototype )
	{
		ArgumentNullException.ThrowIfNull( prototype );
		_settings = CloneSettings( prototype.Settings );
		_emitters = new List<ParticleEmitter>();
		foreach ( var em in prototype.Emitters ) _emitters.Add( CloneEmitter( em, 0 ) );
	}

	/// <summary>Build every seeked system from <paramref name="factory"/> instead
	/// of a snapshot. The factory must return a fresh, identically-configured
	/// system on every call, or seeks stop being deterministic.</summary>
	public ParticleSeek( Func<ParticleSystem> factory )
	{
		ArgumentNullException.ThrowIfNull( factory );
		_factory = factory;
	}

	/// <summary>Per-preset seeker in lane 4's shape: one system for one preset,
	/// built with <see cref="ParticleEmitterPresets.SettingsFor"/> so the preset's
	/// medium (e.g. bubbles rise, sparks fall) is the system's medium, never a
	/// shared default. The preset's burst stays at step 0 unless the caller
	/// retimes it (see <see cref="SeekToCue"/>).</summary>
	public static ParticleSeek ForPreset( string presetName, Vector3 origin, ParticleSystemSettings baseSettings, Vector3? direction = null )
	{
		ArgumentNullException.ThrowIfNull( baseSettings );
		var settings = ParticleEmitterPresets.SettingsFor( presetName, baseSettings );
		var system = new ParticleSystem( settings );
		system.AddEmitter( ParticleEmitterPresets.Create( presetName, origin, direction ) );
		return new ParticleSeek( system );
	}

	/// <summary>Fresh system at the blueprinted initial state (StepIndex 0, nothing emitted).</summary>
	public ParticleSystem BuildFresh()
	{
		if ( _factory is not null ) return _factory();
		var system = new ParticleSystem( CloneSettings( _settings! ) );
		foreach ( var em in _emitters! ) system.AddEmitter( CloneEmitter( em, 0 ) );
		return system;
	}

	/// <summary>Fixed steps for a time, rounded to the nearest step (ties away from zero).</summary>
	public long StepsFor( double seconds )
	{
		ValidateTime( seconds );
		return (long)Math.Round( seconds / (double)FixedDeltaTime, MidpointRounding.AwayFromZero );
	}

	/// <summary>Sim time of a step count, seconds.</summary>
	public double TimeFor( long steps )
	{
		ValidateSteps( steps );
		return steps * (double)FixedDeltaTime;
	}

	/// <summary>Reset to the initial state and fixed-step forward to time
	/// <paramref name="seconds"/> (snapped to the nearest step). Deterministic:
	/// calling this twice returns bit-identical systems (see
	/// <see cref="StatesEqual"/>), and the result equals a fresh system stepped
	/// live to the same step count.</summary>
	public ParticleSystem Seek( double seconds ) => SeekSteps( StepsFor( seconds ) );

	/// <summary>Reset to the initial state and fixed-step forward
	/// <paramref name="steps"/> steps.</summary>
	public ParticleSystem SeekSteps( long steps )
	{
		ValidateSteps( steps );
		var system = BuildFresh();
		system.Step( checked( (int)steps ) );
		return system;
	}

	/// <summary>Step a system <paramref name="steps"/> fixed steps before its
	/// first rendered frame, so a looping effect starts already populated
	/// instead of empty. This is SeekSteps under its honest name: the steps
	/// are real sim steps, with real cost (see the class cost rule), and the
	/// returned system is the one to render from — it is NOT the prototype
	/// advanced in place, because a live system cannot be rewound.</summary>
	public ParticleSystem Prewarm( int steps ) => SeekSteps( steps );

	/// <summary>Position a per-preset system one fixed step before a cue's time,
	/// so the cue — already scheduled at the cue step by the bridge — fires on
	/// its scheduled step when playback continues (see the class header for the
	/// exact two-step chain). Cue firing itself is the bridge's job.</summary>
	public ParticleSystem SeekToCue( double cueTime )
	{
		long cueSteps = StepsFor( cueTime );
		return SeekSteps( cueSteps == 0 ? 0 : cueSteps - 1 );
	}

	/// <summary>Validate this seeker's configuration: the ceiling must be finite
	/// and positive, and the blueprinted settings must pass their own
	/// Validate(). For a factory seeker this builds (and discards) one fresh
	/// system to validate it.</summary>
	public void Validate()
	{
		if ( !double.IsFinite( MaxSeekSeconds ) || MaxSeekSeconds <= 0 )
			throw new ArgumentOutOfRangeException( nameof( MaxSeekSeconds ), "MaxSeekSeconds must be finite and > 0." );
		if ( _factory is not null ) { BuildFresh().Settings.Validate(); return; }
		_settings!.Validate();
	}

	/// <summary>Loudly reject a seek time: non-finite, negative, or past
	/// <see cref="MaxSeekSeconds"/> (which bounds runaway prewarm cost).</summary>
	public void ValidateTime( double seconds )
	{
		if ( !double.IsFinite( seconds ) )
			throw new ArgumentException( "Seek time must be finite.", nameof( seconds ) );
		if ( seconds < 0 )
			throw new ArgumentOutOfRangeException( nameof( seconds ), "Seek time must be >= 0; seeking before the start is not defined." );
		if ( seconds > MaxSeekSeconds )
			throw new ArgumentOutOfRangeException( nameof( seconds ), $"Seek time {seconds}s exceeds MaxSeekSeconds {MaxSeekSeconds}s (runaway prewarm bound)." );
		if ( StepsCountFor( seconds ) > int.MaxValue )
			throw new ArgumentOutOfRangeException( nameof( seconds ), "Seek time needs more steps than a single Step(int) call can take; raise FixedDeltaTime or lower MaxSeekSeconds." );
	}

	/// <summary>Loudly reject a seek/prewarm step count: negative, or past
	/// <see cref="MaxSteps"/> (which bounds runaway prewarm cost).</summary>
	public void ValidateSteps( long steps )
	{
		if ( steps < 0 )
			throw new ArgumentOutOfRangeException( nameof( steps ), "Seek/prewarm step count must be >= 0." );
		if ( steps > MaxSteps )
			throw new ArgumentOutOfRangeException( nameof( steps ), $"Step count {steps} exceeds MaxSteps {MaxSteps} (MaxSeekSeconds {MaxSeekSeconds}s at {FixedDeltaTime}s/step)." );
		if ( steps > int.MaxValue )
			throw new ArgumentOutOfRangeException( nameof( steps ), "Step count exceeds what a single Step(int) call can take." );
	}

	long StepsCountFor( double seconds ) => (long)Math.Round( seconds / (double)FixedDeltaTime, MidpointRounding.AwayFromZero );

	/// <summary>Bit-identity check for the determinism proofs: step index, alive
	/// count, high-water mark, total emitted, dropped count, per-slot ids,
	/// lifetimes, and every ParticleState field compared with exact (==)
	/// equality — no epsilon, because the same code path with the same seed
	/// must produce the same floats bit for bit.</summary>
	public static bool StatesEqual( ParticleSystem a, ParticleSystem b, out string difference )
	{
		ArgumentNullException.ThrowIfNull( a );
		ArgumentNullException.ThrowIfNull( b );
		if ( a.StepIndex != b.StepIndex ) { difference = $"StepIndex {a.StepIndex} != {b.StepIndex}"; return false; }
		if ( a.AliveCount != b.AliveCount ) { difference = $"AliveCount {a.AliveCount} != {b.AliveCount}"; return false; }
		if ( a.HighWater != b.HighWater ) { difference = $"HighWater {a.HighWater} != {b.HighWater}"; return false; }
		if ( a.TotalEmitted != b.TotalEmitted ) { difference = $"TotalEmitted {a.TotalEmitted} != {b.TotalEmitted}"; return false; }
		if ( a.DroppedForCapacity != b.DroppedForCapacity ) { difference = $"DroppedForCapacity {a.DroppedForCapacity} != {b.DroppedForCapacity}"; return false; }
		for ( int s = 0; s < a.HighWater; s++ )
		{
			var x = a.States[s]; var y = b.States[s];
			if ( x.Position != y.Position || x.Velocity != y.Velocity || x.Age != y.Age || x.Alive != y.Alive
				|| x.Rotation != y.Rotation || x.AngularVelocity != y.AngularVelocity || x.Size != y.Size )
			{ difference = $"slot {s} state differs: {x} vs {y}"; return false; }
			if ( a.Ids[s] != b.Ids[s] ) { difference = $"slot {s} id {a.Ids[s]} != {b.Ids[s]}"; return false; }
			if ( a.LifetimeOf( s ) != b.LifetimeOf( s ) ) { difference = $"slot {s} lifetime {a.LifetimeOf( s )} != {b.LifetimeOf( s )}"; return false; }
		}
		difference = "";
		return true;
	}

	static ParticleSystemSettings CloneSettings( ParticleSystemSettings s )
	{
		var c = new ParticleSystemSettings
		{
			Capacity = s.Capacity, Seed = s.Seed, FixedDeltaTime = s.FixedDeltaTime, Substeps = s.Substeps,
			Gravity = s.Gravity, Wind = s.Wind, Drag = s.Drag,
			TurbulenceStrength = s.TurbulenceStrength, TurbulenceFrequency = s.TurbulenceFrequency, TurbulenceSpeed = s.TurbulenceSpeed,
			VortexStrength = s.VortexStrength, VortexCenter = s.VortexCenter, VortexAxis = s.VortexAxis, VortexRadius = s.VortexRadius,
			GroundEnabled = s.GroundEnabled, GroundNormal = s.GroundNormal, GroundOffset = s.GroundOffset,
			CollisionMesh = s.CollisionMesh, CollisionRadius = s.CollisionRadius, Restitution = s.Restitution, Friction = s.Friction,
			RenderType = s.RenderType, StrandPoints = s.StrandPoints,
		};
		// ParticleEmitterPresets.Clone drops KillVolumes; a seek blueprint must not.
		foreach ( var v in s.KillVolumes )
			c.KillVolumes.Add( new ParticleKillVolume { Kind = v.Kind, Normal = v.Normal, Offset = v.Offset, Center = v.Center, Radius = v.Radius, KillInside = v.KillInside } );
		return c;
	}

	static ParticleEmitter CloneEmitter( ParticleEmitter em, int depth )
	{
		if ( depth > 32 ) throw new ArgumentException( "Emitter sub-emitter chain too deep to clone." );
		var c = new ParticleEmitter
		{
			Shape = em.Shape, Position = em.Position, AreaU = em.AreaU, AreaV = em.AreaV,
			BoxHalfExtents = em.BoxHalfExtents, SphereRadius = em.SphereRadius, Mesh = em.Mesh,
			Rate = em.Rate, Bursts = new List<ParticleBurst>( em.Bursts ),
			LifetimeMin = em.LifetimeMin, LifetimeMax = em.LifetimeMax, SizeMin = em.SizeMin, SizeMax = em.SizeMax,
			Direction = em.Direction, UseSurfaceNormal = em.UseSurfaceNormal, Speed = em.Speed, SpeedJitter = em.SpeedJitter,
			SpreadDegrees = em.SpreadDegrees, BaseVelocity = em.BaseVelocity, AngularSpeedMax = em.AngularSpeedMax,
			Enabled = em.Enabled, DieOnCollision = em.DieOnCollision,
			DeathSubEmitter = CloneSub( em.DeathSubEmitter, depth ), CollisionSubEmitter = CloneSub( em.CollisionSubEmitter, depth ),
		};
		return c;
	}

	static ParticleSubEmitter? CloneSub( ParticleSubEmitter? sub, int depth )
	{
		if ( sub is null ) return null;
		return new ParticleSubEmitter
		{
			ChildPreset = sub.ChildPreset,
			ChildTemplate = sub.ChildTemplate is null ? null : CloneEmitter( sub.ChildTemplate, depth + 1 ),
			Count = sub.Count, VelocityInherit = sub.VelocityInherit, MaxDepth = sub.MaxDepth,
			ThrowOnDepthExceeded = sub.ThrowOnDepthExceeded,
		};
	}
}

/// <summary>
/// Validated seek/prewarm parameters (lane P36 close-out addition).
/// One bundle, two operations — never a combined operation, because Seek
/// resets to the initial state and Prewarm IS SeekSteps under its honest
/// name, so "prewarm then seek" would just be the seek:
/// <see cref="SeekWith"/> seeks to <see cref="TargetTime"/>;
/// <see cref="PrewarmWith"/> prewarms <see cref="PrewarmSteps"/> steps.
/// <see cref="Validate"/> loudly rejects non-finite or negative times,
/// negative steps, and times past the ceiling (default
/// <see cref="ParticleSeek.DefaultMaxSeekSeconds"/> = 600 s — the runaway
/// prewarm bound: 600 s at 60 Hz is 36,000 real sim steps before the first
/// rendered frame, already far past any scrub/prewarm use). The step-count
/// ceiling for a non-default fixed step is enforced by the seeker itself
/// (<see cref="ParticleSeek.ValidateSteps"/> / MaxSteps) at apply time,
/// because this record does not know the system's fixed step.
/// </summary>
public sealed record ParticleSeekParams( double TargetTime = 0, int PrewarmSteps = 0 )
{
	public void Validate( double maxSeekSeconds = ParticleSeek.DefaultMaxSeekSeconds )
	{
		if ( !double.IsFinite( TargetTime ) )
			throw new ArgumentException( "TargetTime must be finite.", nameof( TargetTime ) );
		if ( TargetTime < 0 )
			throw new ArgumentOutOfRangeException( nameof( TargetTime ), "TargetTime must be >= 0; seeking before the start is not defined." );
		if ( !double.IsFinite( maxSeekSeconds ) || maxSeekSeconds <= 0 )
			throw new ArgumentOutOfRangeException( nameof( maxSeekSeconds ), "maxSeekSeconds must be finite and > 0." );
		if ( TargetTime > maxSeekSeconds )
			throw new ArgumentOutOfRangeException( nameof( TargetTime ), $"TargetTime {TargetTime}s exceeds maxSeekSeconds {maxSeekSeconds}s (runaway prewarm bound)." );
		if ( PrewarmSteps < 0 )
			throw new ArgumentOutOfRangeException( nameof( PrewarmSteps ), "PrewarmSteps must be >= 0; Prewarm(0) is the documented no-op." );
	}

	public ParticleSystem SeekWith( ParticleSeek seeker )
	{
		ArgumentNullException.ThrowIfNull( seeker );
		Validate( seeker.MaxSeekSeconds );
		return seeker.Seek( TargetTime );
	}

	public ParticleSystem PrewarmWith( ParticleSeek seeker )
	{
		ArgumentNullException.ThrowIfNull( seeker );
		Validate( seeker.MaxSeekSeconds );
		return seeker.Prewarm( PrewarmSteps );
	}
}
