#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>
/// THE RULE (wave-6 lane P37, scaling mode): a scaled character must NOT silently
/// scale particle size/speed. When a particle system is parented to (or authored for)
/// a character scaled by factor S, the author chooses ONE scaling mode, and the mode
/// decides what happens. There is no silent default that scales everything: the mode
/// is an explicit field on the system, and its default value is documented below.
/// Units: kernel inches, Z-up, seconds (ParticleSystem.cs). Times never change under
/// any mode, so a scaled effect runs for the same seconds as the unscaled one.
/// </summary>
/// <remarks>
/// TWO PIPELINES, NEVER BOTH. This file offers two entry points for two different
/// future parenting implementations, and applying both double-scales:
/// <list type="bullet">
/// <item><see cref="ParticleScaling.ScaleSettings"/> returns WORLD-SPACE settings: hand
/// them to a world-space ParticleSystem. The sweep must then place the system at the
/// bone's world position WITHOUT additionally scaling sim quantities (no scaled
/// parent transform over the sim). This is the entry point the sweep should call
/// in the normal case, and the one whose numbers the tests pin directly
/// (S = 2, WorldScale: size 2 -&gt; 4, speed 10 -&gt; 20, gravity -386 -&gt; -772).</item>
/// <item><see cref="ParticleScaling.CompensateForParent"/> returns PRE-PARENT (local)
/// settings for a sim whose outputs a scaled parent transform WILL multiply by S.
/// It divides (compensates) so the parent's scale lands where the mode says it
/// should. Use it ONLY if the parenting implementation really scales sim outputs;
/// the tests pin it via compensated-then-parent-scaled == expected world result.</item>
/// </list>
/// The two agree by construction: ScaleSettings(x, S, mode) equals
/// ScaleSettings(CompensateForParent(x, S, mode), S, WorldScale) for every mode.
/// PIVOT: all position/length scalings are about the character origin (the scale
/// pivot at the coordinate origin). The sweep must express emitter positions,
/// vortex centres, and ground/kill-plane geometry relative to that pivot before
/// calling, or use <see cref="ParticleScaling.ScalePositionAboutPivot"/> and
/// <see cref="ParticleScaling.ScalePlaneOffset"/> for an off-origin pivot.
/// MESH LIMIT: a MeshSurface emitter's <see cref="ParticleEmitter.Mesh"/> is shared
/// by reference, NOT rescaled — mesh vertices live outside this file. The sweep
/// must supply a pre-scaled mesh (or parent the mesh) or MeshSurface spawn
/// positions will not follow the mode. Everything else is a fresh copy; inputs
/// are never mutated, and a scaled emitter is a fresh authoring object (its
/// runtime Rng/RateRemainder are reset; AddEmitter assigns them).
/// QUADRATIC DRAG: ParticleSystemSettings carries only linear Drag (1/s). The
/// quadratic coefficient (1/in) lives in <see cref="ParticleDragCoefficients"/>
/// and is scaled by its own function, because no existing field holds it.
/// </remarks>
public enum ParticleScalingMode
{
	/// <summary>
	/// DEFAULT. Particle sizes, speeds, and gravity all scale by S, so the effect
	/// grows with the character and looks identical relative to it. "Looks
	/// identical" numerically: positions relative to the character scale by S,
	/// velocities scale by S, accelerations scale by S, and times are unchanged —
	/// so the effect traces the same shape, S times larger, in the same seconds.
	/// Default because character-attached effects (hand sparks, breath frost) are
	/// the common case, and an effect that stays tiny on a doubled character is
	/// the surprising outcome. The default is explicit in code (first enum value),
	/// not an implicit transform: nothing scales unless this mode field says so.
	/// </summary>
	WorldScale,
	/// <summary>
	/// Particle sizes, speeds, and gravity do NOT scale: the effect stays the same
	/// absolute size/speed in the world even though the character grew. World-space
	/// settings are the authored values unchanged; the pre-parent compensation
	/// divides authored values by S before the sim runs, so the parent's xS
	/// restores exactly the authored world result. For world-anchored effects
	/// (rain, ground fog) that must not grow with the character.
	/// </summary>
	PreserveWorld,
	/// <summary>
	/// Sizes scale but speeds/gravity do not, so a big character gets big,
	/// slow-looking particles: world sizes are S times authored, world speeds and
	/// gravity are the authored values, times unchanged. Geometry quantities
	/// (positions, sizes, distances) follow WorldScale; dynamics quantities
	/// (velocities, accelerations, wind) follow PreserveWorld. The two coupled
	/// coefficients follow their derivations, not either group alone: turbulence
	/// frequency scales as 1/S (it is tied to scaled positions), vortex strength
	/// scales as S (acceleration x length, with acceleration held), and quadratic
	/// drag is unchanged (velocity held, so k = accel / v^2 is held).
	/// </summary>
	ScaleSizeOnly,
}

/// <summary>
/// What kind of quantity a sim value is. The factor for each kind is DERIVED from
/// one (length, rate, acceleration) triple per mode — never ad-hoc per field.
/// World-space triples: WorldScale (S, S, S); PreserveWorld (1, 1, 1);
/// ScaleSizeOnly (S, 1, 1). Pre-parent triples are those triples divided by S.
/// </summary>
public enum ParticleScaleQuantity
{
	/// <summary>Length, inches: size, position, distance, radius, offset. Factor: length.</summary>
	Length,
	/// <summary>Rate, inches/second: speed, velocity, wind. Factor: rate.</summary>
	Rate,
	/// <summary>Acceleration, inches/second^2: gravity, turbulence strength. Factor: acceleration.</summary>
	Acceleration,
	/// <summary>Time, seconds: lifetime, fixed step, rate is 1/time (see RatePerSecond). Never scales.</summary>
	Time,
	/// <summary>Count or ratio: capacity, seed, burst count, restitution, friction, angles, jitter. Never scales.</summary>
	Dimensionless,
	/// <summary>Particles per second, 1/second. Never scales: the same count per second, for the same seconds.</summary>
	RatePerSecond,
	/// <summary>Inverse length, 1/inch: turbulence frequency (noise lattice). Factor: 1/length.</summary>
	InverseLength,
	/// <summary>Vortex strength, inches^2/second^2 (acceleration x length, from
	/// accel = cross(axis, r) * Strength / (|r|^2 + Radius^2)). Factor: acceleration x length.</summary>
	VortexStrength,
	/// <summary>Quadratic drag coefficient, 1/inch (accel = -k |v| v). Factor: acceleration / rate^2.
	/// WorldScale gives S / S^2 = 1/S: velocities grow as S, so v^2 grows as S^2,
	/// and only k = 1/S keeps acceleration growing as S.</summary>
	QuadraticDrag,
	/// <summary>Linear drag coefficient, 1/second (drag pulls velocity toward wind at
	/// this rate). Time-like: NEVER scales under any mode.</summary>
	LinearDrag,
}

/// <summary>
/// Drag coefficients as a pair, because ParticleSystemSettings holds only the
/// linear one. Linear is 1/s (time-like, never scales); quadratic is 1/in
/// (scales as 1/S under WorldScale, unchanged otherwise — see
/// <see cref="ParticleScaleQuantity.QuadraticDrag"/>).
/// </summary>
public readonly record struct ParticleDragCoefficients( float Linear, float Quadratic );

/// <summary>Pure scaling/compensation functions for particle systems (wave-6 lane P37).
/// Every function returns fresh objects and never mutates its input.</summary>
public static class ParticleScaling
{
	readonly record struct Triple( float Length, float Rate, float Acceleration );

	static Triple WorldTriple( ParticleScalingMode mode, float s ) => mode switch
	{
		ParticleScalingMode.WorldScale => new Triple( s, s, s ),
		ParticleScalingMode.PreserveWorld => new Triple( 1f, 1f, 1f ),
		ParticleScalingMode.ScaleSizeOnly => new Triple( s, 1f, 1f ),
		_ => throw new ArgumentException( $"Unknown particle scaling mode {mode}." ),
	};

	// Pre-parent compensation: the parent will multiply lengths, rates, and
	// accelerations by S, so the local values must be the world values divided
	// by S. WorldScale compensates to identity (the parent does the growing);
	// PreserveWorld divides everything by S; ScaleSizeOnly holds geometry and
	// divides dynamics by S.
	static Triple CompensateTriple( ParticleScalingMode mode, float s ) => mode switch
	{
		ParticleScalingMode.WorldScale => new Triple( 1f, 1f, 1f ),
		ParticleScalingMode.PreserveWorld => new Triple( 1f / s, 1f / s, 1f / s ),
		ParticleScalingMode.ScaleSizeOnly => new Triple( 1f, 1f / s, 1f / s ),
		_ => throw new ArgumentException( $"Unknown particle scaling mode {mode}." ),
	};

	static float FactorFrom( Triple t, ParticleScaleQuantity quantity ) => quantity switch
	{
		ParticleScaleQuantity.Length => t.Length,
		ParticleScaleQuantity.Rate => t.Rate,
		ParticleScaleQuantity.Acceleration => t.Acceleration,
		ParticleScaleQuantity.Time => 1f,
		ParticleScaleQuantity.Dimensionless => 1f,
		ParticleScaleQuantity.RatePerSecond => 1f,
		ParticleScaleQuantity.InverseLength => 1f / t.Length,
		ParticleScaleQuantity.VortexStrength => t.Acceleration * t.Length,
		ParticleScaleQuantity.QuadraticDrag => t.Acceleration / ( t.Rate * t.Rate ),
		ParticleScaleQuantity.LinearDrag => 1f,
		_ => throw new ArgumentException( $"Unknown particle scale quantity {quantity}." ),
	};

	/// <summary>World-space factor for one quantity kind under a mode. S = 1 is
	/// exactly 1 for every kind and mode, so S = 1 is the exact identity.</summary>
	public static float Factor( ParticleScalingMode mode, ParticleScaleQuantity quantity, float scale )
	{
		CheckScale( scale );
		CheckMode( mode );
		return FactorFrom( WorldTriple( mode, scale ), quantity );
	}

	/// <summary>
	/// World-space settings: the values a world-space ParticleSystem should run
	/// with. Positions/lengths, velocities, and accelerations follow the mode's
	/// triple; times, counts, ratios, and linear drag never change. The sweep
	/// calls this at system creation and must NOT additionally scale sim
	/// quantities with a parent transform.
	/// </summary>
	public static ParticleSystemSettings ScaleSettings( ParticleSystemSettings settings, float scale, ParticleScalingMode mode )
	{
		ArgumentNullException.ThrowIfNull( settings );
		CheckScale( scale );
		CheckMode( mode );
		ValidateSettings( settings );
		var t = WorldTriple( mode, scale );
		var output = ScaleSettingsWith( settings, t );
		ValidateSettings( output );
		return output;
	}

	/// <summary>
	/// Pre-parent compensation: the settings a local-space sim should run with
	/// when a scaled parent transform WILL multiply its outputs by S. WorldScale
	/// passes through (the parent grows the effect); PreserveWorld divides
	/// lengths, rates, and accelerations by S; ScaleSizeOnly holds geometry and
	/// divides dynamics by S. Never apply this AND ScaleSettings to one system.
	/// </summary>
	public static ParticleSystemSettings CompensateForParent( ParticleSystemSettings settings, float scale, ParticleScalingMode mode )
	{
		ArgumentNullException.ThrowIfNull( settings );
		CheckScale( scale );
		CheckMode( mode );
		ValidateSettings( settings );
		var t = CompensateTriple( mode, scale );
		var output = ScaleSettingsWith( settings, t );
		ValidateSettings( output );
		return output;
	}

	static ParticleSystemSettings ScaleSettingsWith( ParticleSystemSettings settings, Triple t )
	{
		float L( float v ) => v * t.Length;
		float A( float v ) => v * t.Acceleration;
		Vector3 LV( Vector3 v ) => v * t.Length;
		Vector3 RV( Vector3 v ) => v * t.Rate;
		Vector3 AV( Vector3 v ) => v * t.Acceleration;
		var volumes = new List<ParticleKillVolume>( settings.KillVolumes.Count );
		foreach ( var kv in settings.KillVolumes )
			volumes.Add( new ParticleKillVolume
			{
				Kind = kv.Kind,
				Normal = kv.Normal,
				Offset = L( kv.Offset ),
				Center = LV( kv.Center ),
				Radius = L( kv.Radius ),
				KillInside = kv.KillInside,
			} );
		return new ParticleSystemSettings
		{
			Capacity = settings.Capacity,
			Seed = settings.Seed,
			FixedDeltaTime = settings.FixedDeltaTime,
			Substeps = settings.Substeps,
			Gravity = AV( settings.Gravity ),
			Wind = RV( settings.Wind ),
			Drag = settings.Drag,
			TurbulenceStrength = A( settings.TurbulenceStrength ),
			TurbulenceFrequency = settings.TurbulenceFrequency / t.Length,
			TurbulenceSpeed = settings.TurbulenceSpeed,
			VortexStrength = settings.VortexStrength * t.Acceleration * t.Length,
			VortexCenter = LV( settings.VortexCenter ),
			VortexAxis = settings.VortexAxis,
			VortexRadius = L( settings.VortexRadius ),
			GroundEnabled = settings.GroundEnabled,
			GroundNormal = settings.GroundNormal,
			GroundOffset = L( settings.GroundOffset ),
			CollisionMesh = settings.CollisionMesh,
			CollisionRadius = L( settings.CollisionRadius ),
			Restitution = settings.Restitution,
			Friction = settings.Friction,
			RenderType = settings.RenderType,
			StrandPoints = settings.StrandPoints,
			KillVolumes = volumes,
		};
	}

	/// <summary>World-space emitter: spawn geometry follows the length factor,
	/// speeds follow the rate factor, times/counts/angles never change. The mesh
	/// reference is shared (see the file header's mesh limit); sub-emitter child
	/// templates are scaled recursively, preset names are passed through.</summary>
	public static ParticleEmitter ScaleEmitter( ParticleEmitter emitter, float scale, ParticleScalingMode mode )
	{
		ArgumentNullException.ThrowIfNull( emitter );
		CheckScale( scale );
		CheckMode( mode );
		ValidateEmitter( emitter );
		var output = ScaleEmitterWith( emitter, WorldTriple( mode, scale ), 0 );
		ValidateEmitter( output );
		return output;
	}

	/// <summary>Pre-parent emitter compensation; see <see cref="CompensateForParent"/>.</summary>
	public static ParticleEmitter CompensateEmitterForParent( ParticleEmitter emitter, float scale, ParticleScalingMode mode )
	{
		ArgumentNullException.ThrowIfNull( emitter );
		CheckScale( scale );
		CheckMode( mode );
		ValidateEmitter( emitter );
		var output = ScaleEmitterWith( emitter, CompensateTriple( mode, scale ), 0 );
		ValidateEmitter( output );
		return output;
	}

	static ParticleEmitter ScaleEmitterWith( ParticleEmitter emitter, Triple t, int depth )
	{
		Vector3 LV( Vector3 v ) => v * t.Length;
		Vector3 RV( Vector3 v ) => v * t.Rate;
		var bursts = new List<ParticleBurst>( emitter.Bursts.Count );
		bursts.AddRange( emitter.Bursts );
		return new ParticleEmitter
		{
			Shape = emitter.Shape,
			Position = LV( emitter.Position ),
			AreaU = LV( emitter.AreaU ),
			AreaV = LV( emitter.AreaV ),
			BoxHalfExtents = LV( emitter.BoxHalfExtents ),
			SphereRadius = emitter.SphereRadius * t.Length,
			Mesh = emitter.Mesh,
			Rate = emitter.Rate,
			Bursts = bursts,
			LifetimeMin = emitter.LifetimeMin,
			LifetimeMax = emitter.LifetimeMax,
			SizeMin = emitter.SizeMin * t.Length,
			SizeMax = emitter.SizeMax * t.Length,
			Direction = emitter.Direction,
			UseSurfaceNormal = emitter.UseSurfaceNormal,
			Speed = emitter.Speed * t.Rate,
			SpeedJitter = emitter.SpeedJitter,
			SpreadDegrees = emitter.SpreadDegrees,
			BaseVelocity = RV( emitter.BaseVelocity ),
			AngularSpeedMax = emitter.AngularSpeedMax,
			Enabled = emitter.Enabled,
			DeathSubEmitter = ScaleSub( emitter.DeathSubEmitter, t, depth ),
			CollisionSubEmitter = ScaleSub( emitter.CollisionSubEmitter, t, depth ),
			DieOnCollision = emitter.DieOnCollision,
		};
	}

	static ParticleSubEmitter? ScaleSub( ParticleSubEmitter? sub, Triple t, int depth )
	{
		if ( sub is null ) return null;
		if ( depth >= 32 ) throw new ArgumentException( "Sub-emitter template chain too deep." );
		return new ParticleSubEmitter
		{
			ChildPreset = sub.ChildPreset,
			ChildTemplate = sub.ChildTemplate is null ? null : ScaleEmitterWith( sub.ChildTemplate, t, depth + 1 ),
			Count = sub.Count,
			VelocityInherit = sub.VelocityInherit,
			MaxDepth = sub.MaxDepth,
			ThrowOnDepthExceeded = sub.ThrowOnDepthExceeded,
		};
	}

	/// <summary>World-space drag coefficients: linear never changes, quadratic
	/// scales as acceleration / rate^2 (WorldScale 1/S, otherwise unchanged).</summary>
	public static ParticleDragCoefficients ScaleDragCoefficients( ParticleDragCoefficients drag, float scale, ParticleScalingMode mode )
	{
		CheckScale( scale );
		CheckMode( mode );
		CheckFinite( drag.Linear, nameof( drag.Linear ) );
		CheckFinite( drag.Quadratic, nameof( drag.Quadratic ) );
		var t = WorldTriple( mode, scale );
		var output = new ParticleDragCoefficients( drag.Linear, drag.Quadratic * t.Acceleration / ( t.Rate * t.Rate ) );
		CheckFinite( output.Quadratic, nameof( drag.Quadratic ) );
		return output;
	}

	/// <summary>Pre-parent drag compensation: linear never changes; quadratic
	/// scales as acceleration / rate^2 of the compensate triple (PreserveWorld
	/// and ScaleSizeOnly multiply by S, because the parent grows velocities by S
	/// so v^2 grows by S^2 and only k x S restores the authored world drag;
	/// WorldScale holds it).</summary>
	public static ParticleDragCoefficients CompensateDragCoefficients( ParticleDragCoefficients drag, float scale, ParticleScalingMode mode )
	{
		CheckScale( scale );
		CheckMode( mode );
		CheckFinite( drag.Linear, nameof( drag.Linear ) );
		CheckFinite( drag.Quadratic, nameof( drag.Quadratic ) );
		var t = CompensateTriple( mode, scale );
		var output = new ParticleDragCoefficients( drag.Linear, drag.Quadratic * t.Acceleration / ( t.Rate * t.Rate ) );
		CheckFinite( output.Quadratic, nameof( drag.Quadratic ) );
		return output;
	}

	/// <summary>
	/// Local sim state placed in the world by a scaled parent: positions,
	/// velocities, and sizes scale by S; age, rotation, angular velocity
	/// (radians/second — angle is dimensionless, time is unchanged), and
	/// alive-ness do not. Mode-independent: the mode already acted on the
	/// settings, so placement always multiplies by S.
	/// </summary>
	public static ParticleState ToWorldState( ParticleState state, float scale )
	{
		CheckScale( scale );
		CheckFinite( state.Position, nameof( state.Position ) );
		CheckFinite( state.Velocity, nameof( state.Velocity ) );
		CheckFinite( state.Size, nameof( state.Size ) );
		return new ParticleState
		{
			Position = state.Position * scale,
			Age = state.Age,
			Velocity = state.Velocity * scale,
			Alive = state.Alive,
			Rotation = state.Rotation,
			AngularVelocity = state.AngularVelocity,
			Size = state.Size * scale,
		};
	}

	/// <summary>Scale a position about an off-origin pivot: pivot + S * (position - pivot).
	/// ScaleSettings assumes the pivot is the coordinate origin; use this when it is not.</summary>
	public static Vector3 ScalePositionAboutPivot( Vector3 position, Vector3 pivot, float scale )
	{
		CheckScale( scale );
		CheckFinite( position, nameof( position ) );
		CheckFinite( pivot, nameof( pivot ) );
		return pivot + ( position - pivot ) * scale;
	}

	/// <summary>
	/// Scale a plane offset about an off-origin pivot. A plane Normal.x = Offset,
	/// scaled about pivot P, becomes Normal.x = S * Offset + (1 - S) * dot(Normal, P).
	/// Use for GroundOffset / kill-plane offsets when the character origin is not
	/// the coordinate origin; ScaleSettings' plain xS on offsets is exactly this
	/// formula with P = origin.
	/// </summary>
	public static float ScalePlaneOffset( float offset, Vector3 normal, Vector3 pivot, float scale )
	{
		CheckScale( scale );
		CheckFinite( offset, nameof( offset ) );
		CheckFinite( normal, nameof( normal ) );
		CheckFinite( pivot, nameof( pivot ) );
		float output = scale * offset + ( 1f - scale ) * Vector3.Dot( normal, pivot );
		CheckFinite( output, nameof( offset ) );
		return output;
	}

	/// <summary>
	/// Loud validation for the scaling inputs: non-positive or non-finite S,
	/// unknown mode enum values, and non-finite settings/emitter/drag inputs are
	/// all rejected. Range rules (e.g. Speed &gt;= 0, Capacity bounds) stay with
	/// ParticleSystemSettings.Validate / ParticleEmitter.Validate and are not
	/// duplicated here; scaled outputs are re-checked for finiteness so a huge
	/// S overflowing a finite input fails loudly instead of silently producing
	/// Infinity. S = 1 passes and is the exact identity for every mode.
	/// </summary>
	public static void Validate( float scale, ParticleScalingMode mode )
	{
		CheckScale( scale );
		CheckMode( mode );
	}

	static void CheckScale( float scale )
	{
		if ( !float.IsFinite( scale ) ) throw new ArgumentOutOfRangeException( nameof( scale ), "Character scale S must be finite." );
		if ( scale <= 0 ) throw new ArgumentOutOfRangeException( nameof( scale ), "Character scale S must be positive." );
	}

	static void CheckMode( ParticleScalingMode mode )
	{
		if ( mode is not ( ParticleScalingMode.WorldScale or ParticleScalingMode.PreserveWorld or ParticleScalingMode.ScaleSizeOnly ) )
			throw new ArgumentException( $"Unknown particle scaling mode {mode}." );
	}

	static void CheckFinite( float value, string name )
	{
		if ( !float.IsFinite( value ) ) throw new ArgumentException( $"{name} must be finite." );
	}

	static void CheckFinite( Vector3 value, string name )
	{
		if ( !ParticleMath.Finite( value ) ) throw new ArgumentException( $"{name} must be finite." );
	}

	static void ValidateSettings( ParticleSystemSettings settings )
	{
		CheckFinite( settings.Gravity, nameof( settings.Gravity ) );
		CheckFinite( settings.Wind, nameof( settings.Wind ) );
		CheckFinite( settings.Drag, nameof( settings.Drag ) );
		CheckFinite( settings.TurbulenceStrength, nameof( settings.TurbulenceStrength ) );
		CheckFinite( settings.TurbulenceFrequency, nameof( settings.TurbulenceFrequency ) );
		CheckFinite( settings.TurbulenceSpeed, nameof( settings.TurbulenceSpeed ) );
		CheckFinite( settings.VortexStrength, nameof( settings.VortexStrength ) );
		CheckFinite( settings.VortexCenter, nameof( settings.VortexCenter ) );
		CheckFinite( settings.VortexAxis, nameof( settings.VortexAxis ) );
		CheckFinite( settings.VortexRadius, nameof( settings.VortexRadius ) );
		CheckFinite( settings.GroundNormal, nameof( settings.GroundNormal ) );
		CheckFinite( settings.GroundOffset, nameof( settings.GroundOffset ) );
		CheckFinite( settings.CollisionRadius, nameof( settings.CollisionRadius ) );
		CheckFinite( settings.Restitution, nameof( settings.Restitution ) );
		CheckFinite( settings.Friction, nameof( settings.Friction ) );
		CheckFinite( settings.FixedDeltaTime, nameof( settings.FixedDeltaTime ) );
		foreach ( var kv in settings.KillVolumes )
		{
			CheckFinite( kv.Normal, "KillVolume.Normal" );
			CheckFinite( kv.Offset, "KillVolume.Offset" );
			CheckFinite( kv.Center, "KillVolume.Center" );
			CheckFinite( kv.Radius, "KillVolume.Radius" );
		}
	}

	static void ValidateEmitter( ParticleEmitter emitter ) => ValidateEmitterDepth( emitter, 0 );

	static void ValidateEmitterDepth( ParticleEmitter emitter, int depth )
	{
		CheckFinite( emitter.Position, nameof( emitter.Position ) );
		CheckFinite( emitter.AreaU, nameof( emitter.AreaU ) );
		CheckFinite( emitter.AreaV, nameof( emitter.AreaV ) );
		CheckFinite( emitter.BoxHalfExtents, nameof( emitter.BoxHalfExtents ) );
		CheckFinite( emitter.SphereRadius, nameof( emitter.SphereRadius ) );
		CheckFinite( emitter.Rate, nameof( emitter.Rate ) );
		CheckFinite( emitter.LifetimeMin, nameof( emitter.LifetimeMin ) );
		CheckFinite( emitter.LifetimeMax, nameof( emitter.LifetimeMax ) );
		CheckFinite( emitter.SizeMin, nameof( emitter.SizeMin ) );
		CheckFinite( emitter.SizeMax, nameof( emitter.SizeMax ) );
		CheckFinite( emitter.Direction, nameof( emitter.Direction ) );
		CheckFinite( emitter.Speed, nameof( emitter.Speed ) );
		CheckFinite( emitter.SpeedJitter, nameof( emitter.SpeedJitter ) );
		CheckFinite( emitter.SpreadDegrees, nameof( emitter.SpreadDegrees ) );
		CheckFinite( emitter.BaseVelocity, nameof( emitter.BaseVelocity ) );
		CheckFinite( emitter.AngularSpeedMax, nameof( emitter.AngularSpeedMax ) );
		if ( depth >= 32 ) throw new ArgumentException( "Sub-emitter template chain too deep." );
		foreach ( var sub in new[] { emitter.DeathSubEmitter, emitter.CollisionSubEmitter } )
		{
			if ( sub is null ) continue;
			CheckFinite( sub.VelocityInherit, "SubEmitter.VelocityInherit" );
			if ( sub.ChildTemplate is not null ) ValidateEmitterDepth( sub.ChildTemplate, depth + 1 );
		}
	}
}
