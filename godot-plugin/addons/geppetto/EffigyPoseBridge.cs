using System;
using System.Collections.Generic;
using Godot;
using Effigy;

namespace Geppetto;

/// <summary>
/// G10: moves poses between the kernel and a Godot <see cref="Skeleton3D"/>.
///
/// Kernel side: local bone transforms (<see cref="Xform"/>, inches, Z-up), one per bone of an
/// Effigy <see cref="Skeleton"/>. Godot side: the bone poses of a Skeleton3D built by
/// <see cref="EffigySkeletonBridge"/>, in metres, Y-up. The conversion is the skeleton bridge's
/// (Basis' = M * B * M^T, origin' = 0.0254 * M * o, M the cyclic permutation (x,y,z)->(y,z,x)),
/// and <see cref="ToEffigyXform"/> is its exact inverse.
///
/// Bones are matched by name, so a Skeleton3D whose bone order differs from the rig still works;
/// a rig bone the Skeleton3D lacks is skipped.
/// </summary>
public static class EffigyPoseBridge
{
	// ---- coordinate transforms ------------------------------------------------------------------

	/// <summary>Godot transform (metres, Y-up) back to an Effigy transform (inches, Z-up).
	/// Inverse of <see cref="EffigySkeletonBridge.TransformXform"/>: B = M^T * B' * M.</summary>
	public static Xform ToEffigyXform( in Transform3D t )
	{
		var b = t.Basis;
		// Effigy X axis is Godot column 2, Y is column 0, Z is column 1, each mapped back by M^T.
		var x = ToEffigyDirection( b.Column2 );
		var y = ToEffigyDirection( b.Column0 );
		var z = ToEffigyDirection( b.Column1 );
		return new Xform( x, y, z, ToEffigyPosition( t.Origin ) );
	}

	/// <summary>Godot point in metres to an Effigy point in inches: x_E = Z_G, y_E = X_G, z_E = Y_G.</summary>
	public static Vec3 ToEffigyPosition( Vector3 p )
	{
		const float inv = 1f / EffigyMeshBridge.ScaleToMetres;
		return new Vec3( p.Z * inv, p.X * inv, p.Y * inv );
	}

	/// <summary>Godot direction to an Effigy direction (no scale).</summary>
	public static Vec3 ToEffigyDirection( Vector3 d ) => new( d.Z, d.X, d.Y );

	/// <summary>Effigy point in inches to a Godot point in metres.</summary>
	public static Vector3 ToGodotPosition( Vec3 p ) => EffigyMeshBridge.TransformPosition( p.x, p.y, p.z );

	/// <summary>Effigy direction to a Godot direction (no scale).</summary>
	public static Vector3 ToGodotDirection( Vec3 d ) => EffigyMeshBridge.TransformNormal( d.x, d.y, d.z );

	// ---- bone mapping ---------------------------------------------------------------------------

	/// <summary>For each rig bone, its index in <paramref name="skeleton"/> (or -1).</summary>
	public static int[] BoneMap( Skeleton rig, Skeleton3D skeleton )
	{
		ArgumentNullException.ThrowIfNull( rig );
		ArgumentNullException.ThrowIfNull( skeleton );
		var map = new int[rig.Count];
		for ( int b = 0; b < rig.Count; b++ )
			map[b] = skeleton.FindBone( rig.Bones[b].Name );
		return map;
	}

	// ---- writing poses --------------------------------------------------------------------------

	/// <summary>Write one kernel local transform onto a Skeleton3D bone's pose.</summary>
	public static void SetBoneLocal( Skeleton3D skeleton, int godotBone, in Xform local )
	{
		var t = EffigySkeletonBridge.TransformXform( local );
		skeleton.SetBonePosePosition( godotBone, t.Origin );
		skeleton.SetBonePoseRotation( godotBone, t.Basis.GetRotationQuaternion() );
		skeleton.SetBonePoseScale( godotBone, t.Basis.Scale );
	}

	/// <summary>Write a full local pose (one <see cref="Xform"/> per rig bone, rig order) onto
	/// <paramref name="skeleton"/>.</summary>
	public static void ApplyLocalPose( Skeleton rig, Skeleton3D skeleton, IReadOnlyList<Xform> local )
	{
		ArgumentNullException.ThrowIfNull( local );
		if ( local.Count < rig.Count )
			throw new ArgumentException( $"Pose has {local.Count} bones, rig has {rig.Count}.", nameof( local ) );

		var map = BoneMap( rig, skeleton );
		for ( int b = 0; b < rig.Count; b++ )
		{
			if ( map[b] >= 0 )
				SetBoneLocal( skeleton, map[b], local[b] );
		}

		if ( !skeleton.IsInsideTree() )
			RefreshGlobalPoses( skeleton );
	}

	/// <summary>
	/// Outside the scene tree, Skeleton3D's SetBonePose* do not mark global poses dirty, so
	/// GetBoneGlobalPose keeps returning the previous pose. Re-setting each root bone's rest (to the
	/// same value) marks its whole subtree dirty unconditionally; then recompute. Harmless in-tree.
	/// </summary>
	public static void RefreshGlobalPoses( Skeleton3D skeleton )
	{
		foreach ( int root in skeleton.GetParentlessBones() )
			skeleton.SetBoneRest( root, skeleton.GetBoneRest( root ) );
		skeleton.ForceUpdateAllBoneTransforms();
	}

	/// <summary>The rig's own pose: every bone at its bind/rest local transform.</summary>
	public static Xform[] RestPose( Skeleton rig )
	{
		var local = new Xform[rig.Count];
		for ( int b = 0; b < rig.Count; b++ )
			local[b] = rig.Bones[b].Local;
		return local;
	}

	/// <summary>Write the rig pose (bind pose) onto <paramref name="skeleton"/>.</summary>
	public static void ApplyRestPose( Skeleton rig, Skeleton3D skeleton ) =>
		ApplyLocalPose( rig, skeleton, RestPose( rig ) );

	/// <summary>The kernel's local pose of a clip at a time in seconds: channel curves, live IK and
	/// constraints, exactly as <see cref="EffigyClipBridge"/> bakes it. Fractional frames are fine.</summary>
	public static Xform[] SampleClip( RigAnimFile clip, Skeleton rig, double timeSeconds )
	{
		ArgumentNullException.ThrowIfNull( clip );
		ArgumentNullException.ThrowIfNull( rig );
		return clip.SampleConstrained( rig, FrameAt( clip, timeSeconds ) );
	}

	/// <summary>Sample <paramref name="clip"/> at <paramref name="timeSeconds"/> and write it onto
	/// <paramref name="skeleton"/>. Returns the kernel's local pose that was written.</summary>
	public static Xform[] ApplyClipPose( RigAnimFile clip, Skeleton rig, Skeleton3D skeleton, double timeSeconds )
	{
		var local = SampleClip( clip, rig, timeSeconds );
		ApplyLocalPose( rig, skeleton, local );
		return local;
	}

	/// <summary>Seconds to the clip's (fractional) frame.</summary>
	public static float FrameAt( RigAnimFile clip, double timeSeconds )
	{
		int fps = clip.FrameRate > 0 ? clip.FrameRate : 30;
		return (float)(timeSeconds * fps);
	}

	// ---- reading poses --------------------------------------------------------------------------

	/// <summary>A Skeleton3D bone's pose (position, rotation, scale) as one Godot transform.</summary>
	public static Transform3D GetBonePoseTransform( Skeleton3D skeleton, int godotBone )
	{
		var basis = new Basis( skeleton.GetBonePoseRotation( godotBone ) ) * Basis.FromScale( skeleton.GetBonePoseScale( godotBone ) );
		return new Transform3D( basis, skeleton.GetBonePosePosition( godotBone ) );
	}

	/// <summary>Read the Skeleton3D's current bone poses back into a kernel local pose (rig order).
	/// A rig bone the Skeleton3D lacks reads as its bind pose.</summary>
	public static Xform[] ReadLocalPose( Skeleton rig, Skeleton3D skeleton )
	{
		var map = BoneMap( rig, skeleton );
		var local = new Xform[rig.Count];
		for ( int b = 0; b < rig.Count; b++ )
			local[b] = map[b] >= 0 ? ToEffigyXform( GetBonePoseTransform( skeleton, map[b] ) ) : rig.Bones[b].Local;
		return local;
	}

	/// <summary>Read the Skeleton3D's current pose into kernel model space.</summary>
	public static Xform[] ReadModelPose( Skeleton rig, Skeleton3D skeleton ) =>
		RigAnimFile.ModelSpace( rig, ReadLocalPose( rig, skeleton ) );
}
