#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

// 2D sub-emitter specifications (particles session, lane P19, 2026-10-24).
// Self-contained child-spawn specs for the 2D sim: flat XY, Y-down canvas
// pixels, seconds — the same units as Particle2D.cs. This file owns ONLY the
// spec and the pure spawn function. It does NOT wire children into the sim:
// there is no death-step hookup, no canvas/QML drawing, no 3D sub-emitter,
// no collision/death callback, and no save/serialization here (those belong
// to the 2D suite, lane P09 for 3D, and lane 5 respectively).
//
// How it is meant to be used (the "At End" / firework pattern): when a 2D
// particle dies, the sim calls Particle2DSubSpawner.SpawnChildren with the
// dying particle's position and velocity, a Particle2DChildSpec, and the
// sim's seeded ParticleRng. The returned Particle2DChildSpawn list is what
// the sim turns into new particles. VelocityInheritance is the fraction of
// the parent's velocity a child keeps: 0 = the child starts still (plus its
// own random launch), 1 = the child keeps the parent's full velocity (plus
// its own random launch on top).
//
// Determinism: SpawnChildren draws ONLY from the supplied ParticleRng —
// never System.Random — in a fixed order per child: (1) ring angle, Ring
// shape only; (2) launch-direction angle; (3) speed; (4) lifetime. A fixed
// seed therefore gives bit-identical child lists, and a Count of 0 consumes
// no RNG draws at all.
//
// Depth guard: MaxDepth defaults to 1. A parent at depth D may spawn only
// when D < MaxDepth, and its children carry Depth = D + 1. So with the
// default, a depth-0 parent spawns depth-1 children, and those children do
// NOT themselves spawn grandchildren — attempting to spawn from a depth-1
// parent with MaxDepth 1 throws InvalidOperationException. Grandchildren
// need the child's own spec to explicitly allow a deeper depth
// (a spec with MaxDepth >= 2 handed to the child at its death).
//
// SWEEP INTEGRATION (coordinator only — NOT applied by this lane):
// Particle2DState currently has no Depth field and no per-particle child
// spec, so wiring this in needs those two additions first. Once they exist,
// in Particle2DSystem.BeginStep's expiry loop (Particle2D.cs, the loop that
// sets _state[s].Alive = false and pushes s onto _freeStack), capture the
// dying particle's Position/Velocity/Depth BEFORE it is freed, and after
// the loop spawn its children into free slots with the sim's RNG, e.g.:
//   var dead = _state[s]; // before Alive = false
//   ... existing free-slot code ...
//   if (dead.SubSpec is { } sub && sub.ChildMaySpawn(dead.Depth))
//       foreach (var child in Particle2DSubSpawner.SpawnChildren(
//           dead.Position, dead.Velocity, sub, ref <sim rng>, dead.Depth))
//           <spawn one Particle2DState from child, with Depth = child.Depth>;
// The sim RNG to pass is whichever ParticleRng the sweep designates for
// death spawns (a dedicated stream keeps death spawns from shifting the
// emitters' streams). This file deliberately does not pick that stream.

/// <summary>Child emitter shape: Point = every child starts exactly at the
/// parent's death position; Ring = children start on a circle of radius
/// RingRadius around it (a burst). A Ring with radius 0 degenerates to a
/// point and is valid, not empty.</summary>
public enum Particle2DSubShape { Point, Ring }

/// <summary>One spawned child: where it starts, how fast it moves, how long
/// it lives, and its recursion depth (parent depth + 1).</summary>
public struct Particle2DChildSpawn
{
	public Vector2 Position;
	public Vector2 Velocity;
	public float Lifetime;
	public int Depth;
	public Particle2DChildSpawn( Vector2 position, Vector2 velocity, float lifetime, int depth )
	{ Position = position; Velocity = velocity; Lifetime = lifetime; Depth = depth; }
}

/// <summary>Child-spawn specification for one dying 2D particle.</summary>
public struct Particle2DChildSpec
{
	/// <summary>How many children one death spawns. 0 = none (valid, spawns an empty list).</summary>
	public int ChildCount;
	/// <summary>Point = at the death position; Ring = burst on a circle around it.</summary>
	public Particle2DSubShape Shape;
	/// <summary>Ring shape only: burst radius in canvas pixels (>= 0; 0 = point).</summary>
	public float RingRadius;
	/// <summary>Own launch speed range, px/s: each child draws uniformly in [SpeedMin, SpeedMax].</summary>
	public float SpeedMin;
	public float SpeedMax;
	/// <summary>Child lifetime range, seconds: each child draws uniformly in [LifetimeMin, LifetimeMax].</summary>
	public float LifetimeMin;
	public float LifetimeMax;
	/// <summary>Fraction of the parent's velocity the child keeps, 0..1.
	/// 0 = starts still (plus its own launch), 1 = keeps the parent's full velocity.</summary>
	public float VelocityInheritance;
	/// <summary>Maximum recursion depth. Default 1: depth-0 parents spawn depth-1
	/// children, and those children spawn nothing further unless their own spec
	/// explicitly allows a deeper depth (MaxDepth >= 2).</summary>
	public int MaxDepth = 1;

	// Explicit parameterless constructor: required by CS8983 once a struct
	// carries a field initializer (MaxDepth = 1 above runs from here).
	public Particle2DChildSpec() { }

	/// <summary>Loud failure on any invalid spec: negative count, negative or
	/// inverted speed/lifetime ranges, non-positive minimum lifetime,
	/// inheritance outside [0,1], negative ring radius, MaxDepth below 1,
	/// or an unknown (empty) emitter shape. Non-finite values fail too.</summary>
	public void Validate()
	{
		if ( ChildCount < 0 ) throw new ArgumentOutOfRangeException( nameof( ChildCount ), "ChildCount must be >= 0." );
		if ( !float.IsFinite( SpeedMin ) || SpeedMin < 0 ) throw new ArgumentOutOfRangeException( nameof( SpeedMin ), "SpeedMin must be finite and >= 0." );
		if ( !float.IsFinite( SpeedMax ) || SpeedMax < SpeedMin ) throw new ArgumentOutOfRangeException( nameof( SpeedMax ), "SpeedMax must be finite and >= SpeedMin." );
		if ( !float.IsFinite( LifetimeMin ) || LifetimeMin <= 0 ) throw new ArgumentOutOfRangeException( nameof( LifetimeMin ), "LifetimeMin must be finite and > 0 (matching the 2D sim's 0 < LifetimeMin rule)." );
		if ( !float.IsFinite( LifetimeMax ) || LifetimeMax < LifetimeMin ) throw new ArgumentOutOfRangeException( nameof( LifetimeMax ), "LifetimeMax must be finite and >= LifetimeMin." );
		if ( !float.IsFinite( VelocityInheritance ) || VelocityInheritance < 0 || VelocityInheritance > 1 ) throw new ArgumentOutOfRangeException( nameof( VelocityInheritance ), "VelocityInheritance must be in [0, 1]." );
		if ( !float.IsFinite( RingRadius ) || RingRadius < 0 ) throw new ArgumentOutOfRangeException( nameof( RingRadius ), "RingRadius must be finite and >= 0." );
		if ( MaxDepth < 1 ) throw new ArgumentOutOfRangeException( nameof( MaxDepth ), "MaxDepth must be >= 1." );
		switch ( Shape )
		{
			case Particle2DSubShape.Point: case Particle2DSubShape.Ring: break;
			default: throw new ArgumentException( $"Unknown (empty) child emitter shape {Shape}." );
		}
	}

	/// <summary>True when a particle at <paramref name="parentDepth"/> may spawn
	/// children under this spec (i.e. parentDepth < MaxDepth).</summary>
	public bool ChildMaySpawn( int parentDepth ) => parentDepth >= 0 && parentDepth < MaxDepth;

	/// <summary>Convenience: SpawnChildren with this spec as the receiver.</summary>
	public List<Particle2DChildSpawn> SpawnChildren( Vector2 parentPosition, Vector2 parentVelocity, ref ParticleRng rng, int parentDepth = 0 )
		=> Particle2DSubSpawner.SpawnChildren( parentPosition, parentVelocity, this, ref rng, parentDepth );
}

/// <summary>Pure child-spawn function for the 2D sim. No state, no I/O, no
/// System.Random: every random value comes from <paramref name="rng"/> in
/// the fixed per-child draw order documented in the file header, so a fixed
/// seed gives identical output. The caller's rng is advanced (ref), exactly
/// like Particle2DSystem's emitter spawns.</summary>
public static class Particle2DSubSpawner
{
	/// <summary>Spawn for a depth-0 parent (the usual case: an original particle dies).</summary>
	public static List<Particle2DChildSpawn> SpawnChildren( Vector2 parentPosition, Vector2 parentVelocity, Particle2DChildSpec spec, ref ParticleRng rng )
		=> SpawnChildren( parentPosition, parentVelocity, spec, ref rng, 0 );

	/// <summary>Spawn for a parent at <paramref name="parentDepth"/>. Loud
	/// failure (InvalidOperationException) when parentDepth >= spec.MaxDepth:
	/// the depth limit has been reached and this parent must not spawn
	/// grandchildren. Negative parentDepth fails with ArgumentOutOfRangeException.
	/// An invalid spec fails via spec.Validate() before any RNG is drawn.</summary>
	public static List<Particle2DChildSpawn> SpawnChildren( Vector2 parentPosition, Vector2 parentVelocity, Particle2DChildSpec spec, ref ParticleRng rng, int parentDepth )
	{
		spec.Validate();
		if ( parentDepth < 0 ) throw new ArgumentOutOfRangeException( nameof( parentDepth ), "parentDepth must be >= 0." );
		if ( parentDepth >= spec.MaxDepth ) throw new InvalidOperationException(
			$"Sub-emitter depth limit reached: parent at depth {parentDepth} may not spawn under MaxDepth {spec.MaxDepth}. " +
			"A child spawns grandchildren only when its own spec explicitly allows a deeper depth (MaxDepth > parentDepth)." );
		if ( !Particle2DMath.Finite( parentPosition ) || !Particle2DMath.Finite( parentVelocity ) )
			throw new ArgumentException( "Parent position and velocity must be finite." );

		var children = new List<Particle2DChildSpawn>( spec.ChildCount );
		for ( int i = 0; i < spec.ChildCount; i++ )
		{
			Vector2 pos = parentPosition;
			if ( spec.Shape == Particle2DSubShape.Ring && spec.RingRadius > 0 )
			{
				float ringAngle = rng.NextFloat() * 2f * MathF.PI;
				pos = parentPosition + new Vector2( MathF.Cos( ringAngle ), MathF.Sin( ringAngle ) ) * spec.RingRadius;
			}
			float dirAngle = rng.NextFloat() * 2f * MathF.PI;
			Vector2 dir = new( MathF.Cos( dirAngle ), MathF.Sin( dirAngle ) );
			float speed = rng.Range( spec.SpeedMin, spec.SpeedMax );
			float lifetime = rng.Range( spec.LifetimeMin, spec.LifetimeMax );
			Vector2 vel = parentVelocity * spec.VelocityInheritance + dir * speed;
			children.Add( new Particle2DChildSpawn( pos, vel, lifetime, parentDepth + 1 ) );
		}
		return children;
	}
}
