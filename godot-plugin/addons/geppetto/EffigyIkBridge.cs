#nullable enable
using System;
using Godot;
using Marionette.Desktop;
using Effigy;

namespace Geppetto;

/// <summary>Which kernel solver a chain solve uses.</summary>
public enum EffigyIkSolver
{
	/// <summary>Closed-form two-bone (law of cosines, bends toward the pole). Chain length 2.</summary>
	TwoBone,

	/// <summary>Cyclic coordinate descent (kernel <see cref="ChainIk"/>, chain length 1 or 3+).</summary>
	Ccd,

	/// <summary>FABRIK (kernel <see cref="ChainIk"/> with <see cref="RigIk.Fabrik"/>, chain length 3+).</summary>
	Fabrik
}

/// <summary>
/// G10: runs the kernel's IK solvers (<see cref="IkTargetService.SolveTwoBone"/>,
/// <see cref="IkTargetService.Solve"/> = <see cref="ChainIk"/>) against a Godot
/// <see cref="Skeleton3D"/>. The current Skeleton3D pose is read back into kernel space, solved
/// there, and the solved local pose written onto the Skeleton3D through
/// <see cref="EffigyPoseBridge"/>. Godot never does any IK maths itself, so the result is the
/// kernel's answer, the same one Marionette and headless clip sampling produce.
///
/// Targets and poles are in the Skeleton3D's own space (metres, Y-up) - the space of
/// <see cref="Skeleton3D.GetBoneGlobalPose"/>. Use <see cref="SkeletonSpaceFromGlobal"/> for a
/// scene-global target such as a handle node's GlobalPosition.
/// </summary>
public static class EffigyIkBridge
{
	/// <summary>Scene-global point to the skeleton's own space.</summary>
	public static Vector3 SkeletonSpaceFromGlobal( Skeleton3D skeleton, Vector3 globalPoint ) =>
		skeleton.IsInsideTree() ? skeleton.GlobalTransform.AffineInverse() * globalPoint : globalPoint;

	/// <summary>
	/// Two-bone IK on <paramref name="endBone"/>, its parent and grandparent. Writes the solved
	/// pose onto <paramref name="skeleton"/> when it succeeds; the result's poses are in kernel space.
	/// </summary>
	public static IkSolveResult SolveTwoBone(
		Skeleton rig,
		Skeleton3D skeleton,
		string endBone,
		Vector3 targetInSkeleton,
		Vector3 poleInSkeleton,
		float softnessInches = 0f,
		bool writeBack = true )
	{
		ArgumentNullException.ThrowIfNull( rig );
		ArgumentNullException.ThrowIfNull( skeleton );

		int end = rig.IndexOf( endBone );
		if ( end < 0 )
			return IkSolveResult.Failed( $"End bone '{endBone}' not found in rig." );

		var world = EffigyPoseBridge.ReadModelPose( rig, skeleton );
		var result = IkTargetService.SolveTwoBone(
			rig, world, end,
			EffigyPoseBridge.ToEffigyPosition( targetInSkeleton ),
			EffigyPoseBridge.ToEffigyDirection( poleInSkeleton ),
			softnessInches );

		if ( result.Success && writeBack )
			EffigyPoseBridge.ApplyLocalPose( rig, skeleton, result.SolvedLocalPoses );

		return result;
	}

	/// <summary>
	/// Chain IK through the kernel's <see cref="ChainIk"/>: <paramref name="chainLength"/> bones above
	/// <paramref name="endBone"/> turn so it reaches the target. <see cref="EffigyIkSolver.Ccd"/> or
	/// <see cref="EffigyIkSolver.Fabrik"/> for chains of 3+; length 2 is always the exact two-bone
	/// solve (the kernel's rule). The end bone keeps its rotation (GoalRotation off).
	/// </summary>
	public static IkSolveResult SolveChain(
		Skeleton rig,
		Skeleton3D skeleton,
		string endBone,
		int chainLength,
		Vector3 targetInSkeleton,
		EffigyIkSolver solver = EffigyIkSolver.Ccd,
		int iterations = 10,
		Vector3? poleInSkeleton = null,
		float maxStretch = 1f,
		bool writeBack = true )
	{
		var ik = new RigIk
		{
			Name = $"G10 {solver} {endBone}",
			EndBone = endBone,
			ChainLength = chainLength,
			Iterations = iterations,
			Fabrik = solver == EffigyIkSolver.Fabrik,
			GoalRotation = false,
			MaxStretch = maxStretch,
			Pole = poleInSkeleton is { } p ? EffigyPoseBridge.ToEffigyDirection( p ) : new Vec3( 0, 0, 1 ),
		};
		return Solve( rig, skeleton, ik, targetInSkeleton, null, writeBack );
	}

	/// <summary>
	/// Solve one kernel <see cref="RigIk"/> constraint (as read from a .ctrlrig or a clip) toward a
	/// Godot-space goal. <paramref name="goalRotationInSkeleton"/> is used when the constraint has
	/// GoalRotation on.
	/// </summary>
	public static IkSolveResult Solve(
		Skeleton rig,
		Skeleton3D skeleton,
		RigIk constraint,
		Vector3 targetInSkeleton,
		Quaternion? goalRotationInSkeleton = null,
		bool writeBack = true,
		System.Collections.Generic.IReadOnlyList<RigLimit>? limits = null )
	{
		ArgumentNullException.ThrowIfNull( rig );
		ArgumentNullException.ThrowIfNull( skeleton );
		ArgumentNullException.ThrowIfNull( constraint );

		var world = EffigyPoseBridge.ReadModelPose( rig, skeleton );
		var rotation = Quat.Identity;
		if ( goalRotationInSkeleton is { } q )
			rotation = Quat.FromXform( EffigyPoseBridge.ToEffigyXform( new Transform3D( new Basis( q ), Vector3.Zero ) ) );

		var goal = new RigPose( EffigyPoseBridge.ToEffigyPosition( targetInSkeleton ), rotation, Vec3.One );
		var result = IkTargetService.Solve( rig, world, constraint, goal, limits );

		if ( result.Success && writeBack )
			EffigyPoseBridge.ApplyLocalPose( rig, skeleton, result.SolvedLocalPoses );

		return result;
	}

	/// <summary>The bones a constraint turns, top first, end last (rig indices), or null when the
	/// chain runs off the top of the rig.</summary>
	public static int[]? ChainBones( Skeleton rig, string endBone, int chainLength )
	{
		int end = rig.IndexOf( endBone );
		if ( end < 0 || chainLength < 1 )
			return null;

		var chain = new int[chainLength + 1];
		chain[^1] = end;
		for ( int k = chain.Length - 2; k >= 0; k-- )
		{
			chain[k] = rig.Bones[chain[k + 1]].Parent;
			if ( chain[k] < 0 )
				return null;
		}
		return chain;
	}
}
