#nullable enable
using System;
using System.Numerics;

namespace Marionette.Desktop;

// P76: pure 2D collision RESPONSES for particles. This lane owns what happens
// to velocity/position AFTER a hit is known. It does NOT detect collisions,
// does NOT define shapes, and does NOT reimplement any sweep/contains test —
// that is P13's file (Particle2DColliders.cs), which this file never touches.
//
// Units, matching Particle2D.cs: flat XY, Y-DOWN canvas pixels, seconds.
// Positions are px, velocities px/s.
//
// Y-DOWN SIGN CONVENTIONS (read twice — every sign below follows from these):
// - "Down" is +Y. Gravity points +Y; a particle falling onto a floor moves
//   with velocity.Y > 0.
// - A surface normal points INTO FREE SPACE, away from the surface, toward
//   where the particle should end up. A flat floor (surface below the
//   particle, free space above it) has normal (0,-1): "up" in Y-down space
//   is (0,-1). A ceiling (surface above, free space below) has (0,+1).
// - vn = velocity . normal. vn < 0 means the particle moves INTO the
//   surface (approaching); vn >= 0 means it already moves away from it or
//   slides exactly along it (separating), and every response below leaves
//   such a velocity untouched (position still snaps to the contact point).
// - Hit.Point is the PARTICLE-CENTRE position at contact, on the surface
//   already expanded by the particle radius (the P23 convention: P23's
//   Resolve snaps the centre to Hit.Point with no extra radius push-out).
//   The radius itself is therefore NOT re-applied here; ParticleRadius is
//   validated (finite, >= 0) so a caller passing a nonsense radius fails
//   loudly instead of silently colliding with the wrong expanded surface.
//
// HIT INPUT (P13 does not exist yet at authoring time):
// P13's hit struct had not landed when this file was written, so this lane
// defines its own minimal hit input, Particle2DResponseHit, named so it
// cannot collide with P13's later type (P13 owns collider shapes; its hit
// type will be named after colliders, not after responses). SWEEP MAPPING
// (for the coordinator sweep, when P13 lands): convert P13's hit with
//   new Particle2DResponseHit( p13Hit.Hit, p13Hit.Point, p13Hit.Normal )
// where p13Hit.Point is P13's radius-corrected centre-at-contact and
// p13Hit.Normal is its outward (free-space) normal. If a later sweep
// replaces this struct with P13's type outright, every function below
// keeps its meaning: only the parameter type changes.
//
// RESPONSE RULES:
// - Bounce mirrors P23's ParticleColliderResponse.Resolve exactly, in 2D:
//   vt = velocity - normal * vn; out = vt * (1 - friction)
//   - normal * (vn * restitution). Restitution/friction are 0..1, the
//   sim's own ranges (Particle2DSettings.Validate).
// - Bounce + StickyThreshold: a slow particle must not jitter on a floor
//   forever. THRESHOLD RULE: only for an APPROACHING contact (vn < 0) with
//   StickyThreshold > 0, if the incoming SPEED |velocity| is at or below
//   the threshold (speed <= StickyThreshold), the bounce degrades to a
//   full stick: velocity exactly zero, Stuck = true, position snaps to
//   contact. Speed strictly above the threshold bounces normally. A
//   separating contact (vn >= 0) never sticks via the threshold — a
//   particle already at rest on a surface is not jittering. Threshold 0
//   (the default) disables the rule entirely.
// - Sticky damps BOTH components: out = velocity * (1 - Stickiness).
//   Stickiness 1 = fully stuck: velocity exactly zero, Stuck = true, so
//   the caller can stop integrating the particle. Stickiness 0 leaves
//   velocity unchanged (position still snaps). Stuck is true ONLY at
//   full stickiness (Stickiness >= 1, i.e. == 1 after validation).
// - Slide removes the normal component entirely: out = vt * (1 - friction),
//   so the particle glides along the surface. This is slope-safe by
//   construction: vt is perpendicular to the normal for ANY normal, so a
//   45-degree slope yields a velocity parallel to the slope, with normal
//   component ~0 (proved in tests, not assumed).
// - Miss (Hit = false): every response returns the inputs unchanged
//   (position NOT snapped, velocity untouched, Stuck = false). A miss
//   still validates its settings — bad settings fail loudly even when
//   nothing collides.
// - All three responses normalise the hit normal (a non-unit normal
//   gives the same result as its unit form); a zero-length, NaN, or
//   infinite normal, a non-finite point, velocity, or position, or an
//   out-of-range setting throws loudly. No silent clamping anywhere.

/// <summary>Minimal hit input for the P76 responses. See the file header for the P13 sweep mapping.</summary>
public readonly record struct Particle2DResponseHit( bool Hit, Vector2 Point, Vector2 Normal )
{
	public static Particle2DResponseHit Miss => new( false, Vector2.Zero, Vector2.Zero );
}

/// <summary>Post-collision state. Stuck = true means the caller may stop integrating this particle.</summary>
public readonly record struct Particle2DResponseResult( Vector2 Position, Vector2 Velocity, bool Stuck );

/// <summary>Response parameters. Ranges match Particle2DSettings: restitution/friction/stickiness 0..1.</summary>
public sealed class Particle2DResponseSettings
{
	/// <summary>Normal velocity retained on bounce (0 = dead stop, 1 = perfect bounce).</summary>
	public float Restitution = 0.3f;
	/// <summary>Tangential velocity retained is (1 - Friction), in Bounce and Slide.</summary>
	public float Friction = 0.2f;
	/// <summary>Sticky damping: out = velocity * (1 - Stickiness). 1 = fully stuck.</summary>
	public float Stickiness = 1f;
	/// <summary>Speed (px/s) at or below which Bounce degrades to a full stick. 0 disables.</summary>
	public float StickyThreshold = 0f;
	/// <summary>Particle radius (px), already baked into Hit.Point by the sweep. Validated, never re-applied.</summary>
	public float ParticleRadius = 0f;

	public void Validate()
	{
		if ( !float.IsFinite( Restitution ) || Restitution < 0 || Restitution > 1 ) throw new ArgumentOutOfRangeException( nameof( Restitution ), "Restitution must be 0..1." );
		if ( !float.IsFinite( Friction ) || Friction < 0 || Friction > 1 ) throw new ArgumentOutOfRangeException( nameof( Friction ), "Friction must be 0..1." );
		if ( !float.IsFinite( Stickiness ) || Stickiness < 0 || Stickiness > 1 ) throw new ArgumentOutOfRangeException( nameof( Stickiness ), "Stickiness must be 0..1." );
		if ( !float.IsFinite( StickyThreshold ) || StickyThreshold < 0 ) throw new ArgumentOutOfRangeException( nameof( StickyThreshold ), "StickyThreshold must be >= 0." );
		if ( !float.IsFinite( ParticleRadius ) || ParticleRadius < 0 ) throw new ArgumentOutOfRangeException( nameof( ParticleRadius ), "ParticleRadius must be >= 0." );
	}
}

/// <summary>Pure 2D collision responses. No state, no RNG, no sim wiring — the coordinator sweep owns the hookup (see the note at the bottom of this file).</summary>
public static class Particle2DResponse
{
	/// <summary>Bounce: P23 Resolve semantics in 2D, plus the sticky-threshold degrade rule (see file header).</summary>
	public static Particle2DResponseResult Bounce( Vector2 position, Vector2 velocity, Particle2DResponseHit hit, Particle2DResponseSettings settings )
	{
		CheckedState( position, velocity, settings );
		if ( !hit.Hit ) return new Particle2DResponseResult( position, velocity, false );
		var n = CheckedNormal( hit );
		float vn = Vector2.Dot( velocity, n );
		if ( vn >= 0 ) return new Particle2DResponseResult( hit.Point, velocity, false );
		if ( settings.StickyThreshold > 0 && velocity.Length() <= settings.StickyThreshold )
			return new Particle2DResponseResult( hit.Point, Vector2.Zero, true );
		var vt = velocity - n * vn;
		return new Particle2DResponseResult( hit.Point, vt * (1f - settings.Friction) - n * (vn * settings.Restitution), false );
	}

	/// <summary>Sticky: both components damped by Stickiness; Stickiness 1 = exactly zero + Stuck.</summary>
	public static Particle2DResponseResult Sticky( Vector2 position, Vector2 velocity, Particle2DResponseHit hit, Particle2DResponseSettings settings )
	{
		CheckedState( position, velocity, settings );
		if ( !hit.Hit ) return new Particle2DResponseResult( position, velocity, false );
		_ = CheckedNormal( hit );
		return new Particle2DResponseResult( hit.Point, velocity * (1f - settings.Stickiness), settings.Stickiness >= 1f );
	}

	/// <summary>Slide: normal component removed entirely; the particle glides along the surface (any slope).</summary>
	public static Particle2DResponseResult Slide( Vector2 position, Vector2 velocity, Particle2DResponseHit hit, Particle2DResponseSettings settings )
	{
		CheckedState( position, velocity, settings );
		if ( !hit.Hit ) return new Particle2DResponseResult( position, velocity, false );
		var n = CheckedNormal( hit );
		float vn = Vector2.Dot( velocity, n );
		if ( vn >= 0 ) return new Particle2DResponseResult( hit.Point, velocity, false );
		var vt = velocity - n * vn;
		return new Particle2DResponseResult( hit.Point, vt * (1f - settings.Friction), false );
	}

	static void CheckedState( Vector2 position, Vector2 velocity, Particle2DResponseSettings settings )
	{
		ArgumentNullException.ThrowIfNull( settings );
		settings.Validate();
		if ( !Finite( position ) || !Finite( velocity ) ) throw new ArgumentException( "Response position/velocity must be finite." );
	}

	static Vector2 CheckedNormal( Particle2DResponseHit hit )
	{
		if ( !Finite( hit.Point ) || !Finite( hit.Normal ) || hit.Normal.LengthSquared() < 1e-12f )
			throw new ArgumentException( "Response needs a hit with a finite point and non-zero normal." );
		return Vector2.Normalize( hit.Normal );
	}

	static bool Finite( Vector2 v ) => float.IsFinite( v.X ) && float.IsFinite( v.Y );
}

// ---- Coordinator sweep: response hookup at the collision point (NOT made by this lane) ----
//
// P13 (Particle2DColliders.cs) owns the sweep; Particle2DSystem owns the step.
// Neither file is edited by P76. The sweep's edit, in the particle step where a
// collider hit is found (after position integration, alongside the existing
// floor check — NOT replacing the floor, which stays as-is):
//
//   var hit2d = new Particle2DResponseHit( hit.Hit, hit.Point, hit.Normal ); // P13 hit -> response hit (see header mapping)
//   var response = Particle2DResponse.Bounce( p.Position, p.Velocity, hit2d, responseSettings );
//   p.Position = response.Position;
//   p.Velocity = response.Velocity;
//   // if ( response.Stuck ) the caller stops integrating this particle (Sticky / threshold-degraded Bounce only).
//
// Swap Bounce for Slide or Sticky for a surface that should glide or grab.
// responseSettings is a Particle2DResponseSettings built from the system's
// Restitution/Friction (and StickyThreshold/Stickiness where the effect wants
// them); P13's hit type name replaces the mapping line's fields if P13 names
// them differently. Test registration (also sweep-owned, NOT added by P76):
//   Particle2DResponseTests.Register( registry, settings );
// placed after the GpuParticleSystemTests.Register line, and after the
// Particle2DTests.Register line once the sweep adds it.
