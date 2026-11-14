using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Lane A: build ragdoll joint constraints (PhysicsJointList) for a skeleton.
/// Produces conical spine/pelvis joints and revolute hinge joints for knees/elbows,
/// matching the s&box / Source 2 PhysicsJointConical and PhysicsJointRevolute nodes.
/// Supports both Citizen and Unreal/tutorial skeleton naming conventions.
/// </summary>
public static class RagdollBuilder
{
	public static List<VmdlPhysics.PhysicsJoint> BuildBipedJoints( Skeleton skeleton )
	{
		var joints = new List<VmdlPhysics.PhysicsJoint>();
		if ( skeleton is null )
			return joints;

		// Pelvis -> Spine (conical, limited swing)
		Add( joints, skeleton, "pelvis", "spine_0", "conical", new Vec3( -10f, -10f, -10f ), new Vec3( 10f, 10f, 10f ) );
		Add( joints, skeleton, "spine_0", "spine_2", "conical", new Vec3( -15f, -15f, -15f ), new Vec3( 15f, 15f, 15f ) );
		// Hips
		Add( joints, skeleton, "pelvis", "leg_upper_L", "conical", new Vec3( -45f, -30f, -30f ), new Vec3( 45f, 30f, 30f ) );
		Add( joints, skeleton, "pelvis", "leg_upper_R", "conical", new Vec3( -45f, -30f, -30f ), new Vec3( 45f, 30f, 30f ) );
		// Shoulders
		Add( joints, skeleton, "spine_2", "arm_upper_L", "conical", new Vec3( -45f, -45f, -45f ), new Vec3( 45f, 45f, 45f ) );
		Add( joints, skeleton, "spine_2", "arm_upper_R", "conical", new Vec3( -45f, -45f, -45f ), new Vec3( 45f, 45f, 45f ) );
		// Neck / head
		Add( joints, skeleton, "spine_2", "head", "conical", new Vec3( -30f, -30f, -30f ), new Vec3( 30f, 30f, 30f ) );
		// Knees & elbows (revolute hinge: 0..130 deg, zero twist)
		AddHinge( joints, skeleton, "leg_upper_L", "leg_lower_L", 0f, 130f );
		AddHinge( joints, skeleton, "leg_upper_R", "leg_lower_R", 0f, 130f );
		AddHinge( joints, skeleton, "arm_upper_L", "arm_lower_L", 0f, 130f );
		AddHinge( joints, skeleton, "arm_upper_R", "arm_lower_R", 0f, 130f );
		return joints;
	}

	static string FindBone( Skeleton skeleton, string name )
	{
		if ( skeleton.IndexOf( name ) >= 0 )
			return name;

		var unreal = CitizenBoneMap.UnrealStyle();
		foreach ( var kvp in unreal )
		{
			if ( string.Equals( kvp.Value, name, StringComparison.OrdinalIgnoreCase ) && skeleton.IndexOf( kvp.Key ) >= 0 )
				return kvp.Key;
		}

		if ( unreal.TryGetValue( name, out var mapped ) && skeleton.IndexOf( mapped ) >= 0 )
			return mapped;

		return null;
	}

	static void Add( List<VmdlPhysics.PhysicsJoint> joints, Skeleton skeleton, string parentName, string childName, string type, Vec3 min, Vec3 max )
	{
		var parent = FindBone( skeleton, parentName );
		var child = FindBone( skeleton, childName );
		if ( parent is null || child is null )
			return;
		joints.Add( new VmdlPhysics.PhysicsJoint { ParentBone = parent, ChildBone = child, Type = type, MinAngles = min, MaxAngles = max } );
	}

	static void AddHinge( List<VmdlPhysics.PhysicsJoint> joints, Skeleton skeleton, string parentName, string childName, float min, float max )
	{
		var parent = FindBone( skeleton, parentName );
		var child = FindBone( skeleton, childName );
		if ( parent is null || child is null )
			return;
		joints.Add( new VmdlPhysics.PhysicsJoint { ParentBone = parent, ChildBone = child, Type = "revolute", MinAngles = new Vec3( 0f, 0f, min ), MaxAngles = new Vec3( 0f, 0f, max ) } );
	}
}
