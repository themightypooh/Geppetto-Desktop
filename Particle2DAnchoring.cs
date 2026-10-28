#nullable enable
using System;
using System.Numerics;

namespace Marionette.Desktop;

// Lane P48 (particles session, 2026-10-24): bone/slot anchoring SPECIFICATION for
// 2D emitters — data + evaluation rules, NOT a bone solver and NOT a sim.
//
// The anchor target's position is supplied by the caller, per step, as a
// Particle2DAnchorTransform. This module only decides how particles relate to
// that transform. It never resolves a bone or slot name to a position: bone
// resolution needs a viewport/rig owner (same rule as lane 4's cue bridge),
// so a caller that cannot resolve a name must skip the effect, never invent
// a position. It also never draws anything (the 2D suite owns the canvas)
// and never touches the 2D sim's step (see the sweep integration lines in
// the test file header — the wiring is the coordinator's, not this file's).
//
// UNITS AND CONVENTIONS (same as Particle2D.cs, read before changing these):
// flat XY canvas pixels, seconds, Y-DOWN (+Y is down the screen, as in QML
// Canvas). Angles are DEGREES in Y-down space: 0 = +X (right), 90 = +Y
// (down), so a positive rotation turns CLOCKWISE on screen — the same
// convention as Particle2D's EmitAngleDegrees / GravityAngleDegrees, and the
// same rotation matrix Particle2DSystem.Spawn uses for spread:
//   world = anchor.Position + R(angle) * (anchor.Scale * local),
//   R(angle) = [ cos -sin ; sin cos ] in Y-down coordinates.
// Scale is applied to positions (a local offset stretches with its anchor),
// NOT to velocities: TransformDirection rotates only, so a released
// particle's speed is the speed it had in world terms, not a scaled copy.
// Negative scale components are allowed (a mirror); zero is not — a zero
// scale collapses every local offset onto the anchor point and hides the
// effect instead of failing, so Validate() rejects it loudly.
//
// ANCHOR KINDS: BoneSlot (default) is a bone/slot anchor identified by
// AnchorName, which the caller resolves to a transform per step; FixedPoint
// is a plain fixed-point anchor that needs NO name — the caller simply
// supplies the same (static) transform every step, and the particle
// follows/releases against that point exactly like a bone anchor. A
// fixed-point spec with an empty name is valid; a bone/slot spec with an
// empty name fails loudly, because there is nothing to resolve.
//
// VELOCITY HANDOFF AT RELEASE: Step() records the step's local velocity
// (anchor-local px/s) as LocalVelocity, and WorldVelocity(anchor) rotates
// it into world space with TransformDirection (rotation only, never
// scale — see above). At the release step, WorldVelocity under THAT
// step's anchor is the particle's world velocity at release; after
// release Step() is a no-op so LocalVelocity is frozen, and the same
// call under the release anchor returns the identical vector — no
// velocity jump. The sim must carry that handoff velocity into its own
// world-space integration; this tracker never integrates world motion.
//
// RELEASE MODES (SpineFX/SlotVFX prior art, lane-list section 18, J8):
// - LOCAL: a particle is spawned in the anchor's local space and moves WITH
//   the anchor for its whole following life. Its world position at any
//   moment is the anchor transform applied to its (possibly drifting)
//   local position.
// - WORLD: a particle is spawned at the anchor's world position AT SPAWN
//   TIME, then moves independently in world space. Later anchor motion
//   never touches it — a moving anchor leaves a trail, a LOCAL anchor
//   carries its cloud.
//
// FOLLOW-TIME: for a LOCAL particle, the seconds it follows its anchor
// before releasing to world space. Stepping is the tracker's job: while
// following, Step() re-applies the current anchor transform; at the first
// step where Age >= FollowTimeSeconds the particle is marked Released and
// its world position is ALREADY the transform at that step's anchor, so
// release never teleports — the release position IS the last followed
// position under that anchor. After release the tracker deliberately stops
// moving the particle: the 2D sim owns world-space motion from there on,
// and WorldPosition freezes as the release handoff point (the sim copies
// it into its own particle state; this tracker is not a second sim).
// Endpoints, both documented and both tested:
// - FollowTimeSeconds = 0 means release immediately: a LOCAL particle
//   with follow-time 0 behaves EXACTLY like a WORLD particle (Released
//   at spawn, world position fixed at the spawn transform).
// - FollowTimeSeconds = +Infinity (the default, FollowForever) means
//   follow for life: the particle never releases on its own.
//
// SLOT-ALPHA (SpineFX-style): an optional per-anchor alpha multiplier
// applied to spawned particles, for effects tied to a slot that fades
// (e.g. a slot fading out takes its particles with it). Default 1.0 =
// no change; RGB channels are never touched, only alpha, and the result
// is clamped to 0..1. The multiplier lives on the spec, so one fading
// slot scales every particle it spawns, and the caller re-reads it per
// spawn rather than per particle — a particle keeps the alpha it was
// spawned with even if the slot fades further mid-flight.
//
// WHAT THIS FILE DOES NOT DO (out of scope, stated so it is not re-added
// by accident): canvas/QML/UI drawing; bone-name resolution; 3D anchoring
// (3D emitters follow bones via the cue bridge); save/serialization
// (lane 5's authoring format); wiring into Particle2DSystem.Step.

/// <summary>What kind of anchor a spec binds to: a named bone/slot the
/// caller resolves per step, or a plain fixed point needing no name.</summary>
public enum Particle2DAnchorKind
{
	/// <summary>Bone/slot anchor, identified by AnchorName (required).</summary>
	BoneSlot,
	/// <summary>Plain fixed-point anchor; AnchorName is ignored, may be empty.</summary>
	FixedPoint,
}

/// <summary>How an anchored particle relates to its anchor after spawn.</summary>
public enum Particle2DReleaseMode
{
	/// <summary>Spawned in anchor-local space; follows the anchor until its follow-time elapses.</summary>
	Local,
	/// <summary>Spawned at the anchor's world position at spawn time; independent afterwards.</summary>
	World,
}

/// <summary>One anchor pose, supplied by the caller per step. Position is in
/// canvas pixels (Y-down); rotation is degrees clockwise on screen (see the
/// file header); scale is applied to local offsets, never to velocities.
/// The default struct value is NOT valid: Scale defaults to (0, 0), so a
/// caller must build transforms with the constructor or Identity — this is
/// deliberate, so a forgotten scale fails in Validate() instead of silently
/// collapsing every particle onto the anchor point.</summary>
public struct Particle2DAnchorTransform
{
	public Vector2 Position;
	public float RotationDegrees;
	public Vector2 Scale;

	public Particle2DAnchorTransform( Vector2 position, float rotationDegrees, Vector2 scale )
	{
		Position = position;
		RotationDegrees = rotationDegrees;
		Scale = scale;
	}

	/// <summary>Origin, no rotation, unit scale.</summary>
	public static Particle2DAnchorTransform Identity => new( Vector2.Zero, 0f, Vector2.One );

	/// <summary>Loud failure on a non-finite position/rotation or a zero or
	/// non-finite scale component. Negative scales (mirrors) pass.</summary>
	public void Validate()
	{
		if ( !Particle2DMath.Finite( Position ) || !float.IsFinite( RotationDegrees ) )
			throw new ArgumentException( "Anchor transform position and rotation must be finite." );
		if ( !float.IsFinite( Scale.X ) || !float.IsFinite( Scale.Y ) )
			throw new ArgumentException( "Anchor transform scale must be finite." );
		if ( Scale.X == 0f || Scale.Y == 0f )
			throw new ArgumentException( $"Anchor transform scale must be non-zero on both axes, got ({Scale.X}, {Scale.Y})." );
	}

	/// <summary>Local point to world point: position + R(angle) * (scale * local).
	/// Assumes a validated transform (Spawn/Step validate; direct callers
	/// should call Validate() first when the transform is hand-built).</summary>
	public Vector2 TransformPoint( Vector2 local )
	{
		float rad = RotationDegrees * MathF.PI / 180f;
		float c = MathF.Cos( rad ), s = MathF.Sin( rad );
		var scaled = new Vector2( Scale.X * local.X, Scale.Y * local.Y );
		return Position + new Vector2( scaled.X * c - scaled.Y * s, scaled.X * s + scaled.Y * c );
	}

	/// <summary>Local direction to world direction: rotation ONLY — no
	/// translation, no scale (see the file header for why velocities are
	/// not scaled with the anchor).</summary>
	public Vector2 TransformDirection( Vector2 direction )
	{
		float rad = RotationDegrees * MathF.PI / 180f;
		float c = MathF.Cos( rad ), s = MathF.Sin( rad );
		return new Vector2( direction.X * c - direction.Y * s, direction.X * s + direction.Y * c );
	}
}

/// <summary>Anchor binding for one emitter: which anchor, which release mode,
/// how long a LOCAL particle follows before releasing, and the slot-alpha
/// multiplier for its spawned particles. Pure specification — it holds no
/// per-particle state and no bone resolver.</summary>
public sealed class Particle2DAnchorSpec
{
	/// <summary>Follow for life; the particle never releases on its own.</summary>
	public const float FollowForever = float.PositiveInfinity;
	/// <summary>Release immediately; a LOCAL particle behaves exactly like a WORLD one.</summary>
	public const float ReleaseImmediately = 0f;

	/// <summary>Which kind of anchor this spec binds to (default BoneSlot).</summary>
	public Particle2DAnchorKind AnchorKind = Particle2DAnchorKind.BoneSlot;
	/// <summary>Bone/slot name the caller resolves to a transform per step.
	/// Required for BoneSlot: an unnamed bone anchor cannot be resolved,
	/// so it fails loudly instead of silently attaching to a default.
	/// Ignored for FixedPoint, where no resolution happens.</summary>
	public string AnchorName = "";
	/// <summary>LOCAL (follow, then release) or WORLD (spawn position only).</summary>
	public Particle2DReleaseMode ReleaseMode = Particle2DReleaseMode.Local;
	/// <summary>Seconds a LOCAL particle follows before releasing. 0 =
	/// immediate release (= WORLD behaviour); +Infinity (default) = follow
	/// for life. Only LOCAL particles read this; WORLD particles ignore it.</summary>
	public float FollowTimeSeconds = FollowForever;
	/// <summary>Per-anchor alpha multiplier for spawned particles (SpineFX
	/// slot-alpha). 1.0 (default) = no change; range is 0..1.</summary>
	public float SlotAlpha = 1f;

	/// <summary>Loud failure on a missing bone/slot anchor name, an unknown
	/// anchor kind or release mode, a negative or NaN follow-time, or a
	/// slot-alpha outside 0..1. A FixedPoint spec needs no name.</summary>
	public void Validate()
	{
		switch ( AnchorKind )
		{
			case Particle2DAnchorKind.BoneSlot:
				if ( string.IsNullOrWhiteSpace( AnchorName ) )
					throw new ArgumentException( "Anchor spec needs a non-empty AnchorName: an unnamed bone/slot anchor cannot be resolved to a transform." );
				break;
			case Particle2DAnchorKind.FixedPoint:
				break;
			default:
				throw new ArgumentException( $"Unknown anchor kind {AnchorKind}." );
		}
		switch ( ReleaseMode )
		{
			case Particle2DReleaseMode.Local:
			case Particle2DReleaseMode.World:
				break;
			default:
				throw new ArgumentException( $"Unknown anchor release mode {ReleaseMode}." );
		}
		if ( float.IsNaN( FollowTimeSeconds ) || FollowTimeSeconds < 0f )
			throw new ArgumentException( $"FollowTimeSeconds must be >= 0 or +Infinity (follow for life), got {FollowTimeSeconds}." );
		if ( float.IsNaN( SlotAlpha ) || SlotAlpha < 0f || SlotAlpha > 1f )
			throw new ArgumentException( $"SlotAlpha must be in 0..1, got {SlotAlpha}." );
	}

	/// <summary>Spawn colour with this anchor's slot-alpha applied: alpha is
	/// multiplied and clamped to 0..1; RGB is untouched.</summary>
	public Particle2DColor ApplySlotAlpha( Particle2DColor color ) =>
		color.WithAlpha( Math.Clamp( color.A * SlotAlpha, 0f, 1f ) );

	/// <summary>Spawn alpha with this anchor's slot-alpha applied, clamped to 0..1.</summary>
	public float EffectiveAlpha( float baseAlpha ) => Math.Clamp( baseAlpha * SlotAlpha, 0f, 1f );

	/// <summary>Spawn one anchored particle at <paramref name="localPosition"/>
	/// (anchor-local space) under <paramref name="anchorAtSpawn"/>. WORLD
	/// particles and LOCAL particles with follow-time 0 are Released at
	/// spawn with their world position fixed at the spawn transform;
	/// other LOCAL particles start following. Validates the spec and the
	/// spawn transform, so a bad anchor fails here, not mid-flight.</summary>
	public Particle2DAnchoredParticle Spawn( Vector2 localPosition, Particle2DAnchorTransform anchorAtSpawn )
	{
		Validate();
		anchorAtSpawn.Validate();
		if ( !Particle2DMath.Finite( localPosition ) )
			throw new ArgumentException( "Spawn local position must be finite." );
		var world = anchorAtSpawn.TransformPoint( localPosition );
		bool released = ReleaseMode == Particle2DReleaseMode.World || FollowTimeSeconds == 0f;
		return new Particle2DAnchoredParticle( this, localPosition, world, released );
	}
}

/// <summary>One anchored particle's tracking state, owned by its spec.
/// Step() advances local drift while following and re-applies the current
/// anchor transform; at release it freezes WorldPosition as the handoff
/// point and stops moving the particle (the 2D sim owns world motion
/// after release). After release, Step() is a no-op by design — it does
/// not drag a released particle along with a moving anchor.</summary>
public sealed class Particle2DAnchoredParticle
{
	public Particle2DAnchorSpec Spec { get; }
	/// <summary>Position in anchor-local space. Advances by local velocity
	/// while following; frozen at release (kept for reference).</summary>
	public Vector2 LocalPosition { get; private set; }
	/// <summary>Current anchored world position. While following, the anchor
	/// transform applied to LocalPosition; after release, the release
	/// position (the handoff point for the sim), unchanged by Step().</summary>
	public Vector2 WorldPosition { get; private set; }
	/// <summary>Seconds since spawn, advanced by Step() while following.</summary>
	public float Age { get; private set; }
	/// <summary>True once the particle has released to world space (at spawn
	/// for WORLD particles and LOCAL particles with follow-time 0).</summary>
	public bool Released { get; private set; }
	/// <summary>True while the particle still follows its anchor.</summary>
	public bool IsFollowing => !Released;
	/// <summary>Most recent step's local velocity (anchor-local px/s).
	/// Zero at spawn; frozen at release, since post-release Step() calls
	/// are no-ops — so the release handoff velocity never changes.</summary>
	public Vector2 LocalVelocity { get; private set; }

	internal Particle2DAnchoredParticle( Particle2DAnchorSpec spec, Vector2 localPosition, Vector2 worldPosition, bool released )
	{
		Spec = spec;
		LocalPosition = localPosition;
		WorldPosition = worldPosition;
		Released = released;
		Age = 0f;
		LocalVelocity = Vector2.Zero;
	}

	/// <summary>World-space velocity of this particle's local drift under
	/// <paramref name="anchor"/>: rotation ONLY, no translation, no scale
	/// (see the file header). For a released particle pass the RELEASE
	/// step's anchor: that is the handoff velocity the sim must carry
	/// into world-space integration, and it is identical before and
	/// after the release step because LocalVelocity is frozen at release.</summary>
	public Vector2 WorldVelocity( Particle2DAnchorTransform anchor ) => anchor.TransformDirection( LocalVelocity );

	/// <summary>World position this particle WOULD have under
	/// <paramref name="anchor"/> right now, without advancing age or
	/// changing release state. For a following particle this is the live
	/// followed position; for a released particle it is meaningless as a
	/// position (the particle no longer follows) and WorldPosition —
	/// the release handoff point — is the value to use.</summary>
	public Vector2 FollowedPosition( Particle2DAnchorTransform anchor ) => anchor.TransformPoint( LocalPosition );

	/// <summary>Advance one step while following: age by <paramref name="dt"/>,
	/// drift the local position by <paramref name="localVelocity"/> * dt
	/// (velocity in anchor-local px/s), then re-apply the current anchor
	/// transform. At the first step where Age >= the spec's follow-time the
	/// particle releases, with WorldPosition already equal to the transform
	/// at this step's anchor — no teleport. After release this is a no-op.
	/// Loud failure on a negative/NaN dt, a non-finite local velocity, or
	/// an invalid anchor (including a zero scale).</summary>
	public void Step( Particle2DAnchorTransform anchor, float dt, Vector2 localVelocity )
	{
		if ( Released ) return;
		if ( !float.IsFinite( dt ) || dt < 0f )
			throw new ArgumentOutOfRangeException( nameof( dt ), "Anchor step dt must be finite and >= 0." );
		if ( !Particle2DMath.Finite( localVelocity ) )
			throw new ArgumentException( "Anchor step local velocity must be finite." );
		anchor.Validate();
		Age += dt;
		LocalVelocity = localVelocity;
		LocalPosition += localVelocity * dt;
		WorldPosition = anchor.TransformPoint( LocalPosition );
		if ( Age >= Spec.FollowTimeSeconds ) Released = true;
	}
}
