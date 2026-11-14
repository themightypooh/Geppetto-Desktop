using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// The pose library seed for the proxy-pose task (godlike-proxy-pose-animator-grade-auto-rig, P0):
/// the named poses an animator starts from, plus mirroring. Poses are plain
/// <c>bone name → local <see cref="RigPose"/></c> dictionaries — the form
/// <see cref="PoseLibrary"/> stores — so they survive a round-trip through a
/// <c>.poselib.json</c> sidecar unchanged.
///
/// SEED CONVENTION. "T-Pose" IS the bind pose: whatever the auto-rigger produced,
/// arms included, is the T. "A-Pose" is computed from the bind by aiming both upper
/// arms 45° below the shoulder line (the A of A/T-pose detection), forearms and
/// hands following the upper arm rigidly. "Idle" bends both forearms 15° toward
/// facing and leans the spine 3° forward — a relaxed standing pose, not a bind.
/// This differs from <see cref="PoseLibrary.SeedHumanoid"/> (added by a sibling
/// worker the same morning), which seeds "A-pose" as the bind and "T-pose" as the
/// computed lateral pose: the two libraries disagree on which letter is the bind,
/// and the integrator picks one seed set. The poses themselves are interchangeable
/// data either way — only the names' meanings differ.
///
/// MIRROR CONVENTION. <see cref="MirrorPose"/> reflects across the sagittal plane
/// through the pelvis head, along the rig's own lateral axis — not a fixed x = 0
/// plane. On a rig centred at the origin with lateral = X the two agree exactly
/// (position x negated, rotation (x, −y, −z, w)); on an offset or rotated rig only
/// the rig-relative plane is correct. <see cref="PoseLibrary.Mirror"/> is the fixed
/// x = 0 form, for poses that were authored against the origin.
/// </summary>
public static class ProxyPoseLibrary
{
	/// <summary>
	/// The three seed poses for a humanoid rig: "T-Pose" (the bind), "A-Pose"
	/// (upper arms aimed 45° below lateral, computed from the bind — never a fixed
	/// angle from a guessed rest pose), and "Idle" (forearms 15° toward facing,
	/// spine 3° forward). Throws when the rig has no left/right pair: without a
	/// lateral axis there is no A to aim at, and a silent bind-shaped A-pose would
	/// be a lie. Arms that do not exist are not posed; the pose is still stored.
	/// </summary>
	public static PoseLibrary SeedPoses( Skeleton rig )
	{
		ArgumentNullException.ThrowIfNull( rig );

		if ( !RigAxes.TryLateral( rig, out var lateral ) )
			throw new ArgumentException( "SeedPoses needs a rig with a left/right pair (no lateral axis, so no A-pose to aim at).", nameof( rig ) );

		var lib = new PoseLibrary();
		lib.SetPose( "T-Pose", BindLocals( rig ) );

		var up = RigAxes.Up( rig );
		var diag = MathF.Sqrt( 0.5f );
		var aWorlds = ProxyPose.BindWorlds( rig );
		var armL = rig.IndexOf( "arm_upper_L" );
		var armR = rig.IndexOf( "arm_upper_R" );

		if ( armL >= 0 )
			ProxyPose.AimBone( aWorlds, rig, armL, lateral * diag - up * diag );

		if ( armR >= 0 )
			ProxyPose.AimBone( aWorlds, rig, armR, -lateral * diag - up * diag );

		lib.SetPose( "A-Pose", LocalsFromWorlds( rig, aWorlds ) );

		var facing = RigAxes.Facing( rig );
		var idleWorlds = ProxyPose.BindWorlds( rig );

		foreach ( var side in new[] { "L", "R" } )
		{
			var forearm = rig.IndexOf( $"arm_lower_{side}" );

			if ( forearm < 0 )
				continue;

			// Rotating the forearm about Cross(direction, facing) by a positive
			// angle swings its tip toward facing, whatever the rig's handedness:
			// (d × facing) × d is facing when d is perpendicular to facing.
			var axis = Vec3.Cross( rig.BoneDirection( forearm ), facing );

			if ( axis.LengthSquared > 1e-6f )
				ProxyPose.ApplyWorldRotation( idleWorlds, rig, forearm, axis.Normal, 15f * MathF.PI / 180f );
		}

		// Citizen middle spine (spine_1); the auto-rigger's source name is
		// spine_02, kept as a fallback for rigs that were never mapped.
		var spine = rig.IndexOf( "spine_1" );

		if ( spine < 0 )
			spine = rig.IndexOf( "spine_02" );

		if ( spine >= 0 )
		{
			var leanAxis = Vec3.Cross( up, facing );

			if ( leanAxis.LengthSquared > 1e-6f )
				ProxyPose.ApplyWorldRotation( idleWorlds, rig, spine, leanAxis.Normal, 3f * MathF.PI / 180f );
		}

		lib.SetPose( "Idle", LocalsFromWorlds( rig, idleWorlds ) );

		return lib;
	}

	/// <summary>
	/// Mirror a pose across the rig's sagittal plane (through the pelvis head,
	/// along the lateral axis): each bone's local moves to its mirror partner
	/// (<c>_L</c> ↔ <c>_R</c> via <see cref="RigQuality.FlipSide"/>, which also sees the
	/// interior marker in RigBuild twist names), conjugated
	/// by the reflection — <c>M · L · M</c>, where M is the mirror Xform. Centre
	/// bones mirror onto themselves. Mirroring twice returns the pose, and a
	/// mirror-symmetric pose (the bind, the T-Pose) is fixed by it.
	///
	/// Why the conjugation is exact: the mirrored skeleton's world transforms are
	/// <c>M · W · M</c>, so a mirrored local is <c>(M·W_parent·M)⁻¹ · (M·W·M) =
	/// M · L · M</c> — the parent's mirror cancels in the middle, and the result
	/// holds for a full pose including translations, by induction up the chain.
	/// It also holds bone-by-bone for a sparse pose: each named local is mirrored
	/// as if its parent were mirrored, which is the definition of the mirror image.
	/// Uniform scale only — a non-uniform local scale mirrors approximately, and
	/// no pose in this library uses one.
	/// </summary>
	public static Dictionary<string, RigPose> MirrorPose( IReadOnlyDictionary<string, RigPose> pose, Skeleton rig )
	{
		ArgumentNullException.ThrowIfNull( pose );
		ArgumentNullException.ThrowIfNull( rig );

		var pelvis = rig.IndexOf( rig.RootBone.Length > 0 ? rig.RootBone : "pelvis" );

		if ( pelvis < 0 )
			throw new ArgumentException( "MirrorPose needs a rig with a pelvis (or root bone) to place the mirror plane.", nameof( rig ) );

		if ( !RigAxes.TryLateral( rig, out var lateral ) )
			throw new ArgumentException( "MirrorPose needs a rig with a left/right pair (no lateral axis).", nameof( rig ) );

		var mirror = Xform.Mirror( rig.HeadWorld( pelvis ), lateral );
		var mirrored = new Dictionary<string, RigPose>( StringComparer.OrdinalIgnoreCase );

		foreach ( var (name, local) in pose )
		{
			var partner = RigQuality.FlipSide( name ) ?? name;
			mirrored[partner] = RigPose.FromXform( mirror * local.ToXform() * mirror );
		}

		return mirrored;
	}

	static Dictionary<string, RigPose> BindLocals( Skeleton rig )
	{
		var locals = new Dictionary<string, RigPose>( StringComparer.OrdinalIgnoreCase );

		foreach ( var bone in rig.Bones )
			locals[bone.Name] = RigPose.FromXform( bone.Local );

		return locals;
	}

	static Dictionary<string, RigPose> LocalsFromWorlds( Skeleton rig, Xform[] worlds )
	{
		var locals = new Dictionary<string, RigPose>( StringComparer.OrdinalIgnoreCase );

		for ( var i = 0; i < rig.Count; i++ )
		{
			var local = rig.Bones[i].Parent < 0 ? worlds[i] : worlds[rig.Bones[i].Parent].Inverse * worlds[i];
			locals[rig.Bones[i].Name] = RigPose.FromXform( local );
		}

		return locals;
	}
}
