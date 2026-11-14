using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Bone-name presets for motion-capture sources, onto <see cref="CitizenSkeleton"/>.
///
/// WHY THIS FILE AND NOT <c>CitizenBoneMap</c>: that file is another worker's this
/// morning (one writer per file), and the Mixamo preset belongs to the mocap ticket
/// anyway. The mapping decisions follow CitizenBoneMap's documented ones where they
/// overlap - notably the pinky.
///
/// A PINKY HAS NOWHERE TO GO, same as in CitizenBoneMap: Citizen's hand is index,
/// middle, ring and thumb, so Mixamo's pinky chain rides the ring chain and follows
/// the nearest finger instead of going rigid.
///
/// FBX is deliberately NOT covered here: <c>FbxReader</c> reads no animation stacks,
/// so there is no FBX clip to map yet. Mixamo arrives through its BVH export
/// (<see cref="BvhImport"/>), whose joint names carry the same <c>mixamorig:</c>
/// prefix this map keys on.
/// </summary>
public static class MocapBoneMap
{
	/// <summary>
	/// Mixamo's <c>mixamorig:</c> naming onto Citizen's. Keys are SOURCE bone names,
	/// as <see cref="SkeletonRetarget.RetargetClip"/> wants them. A source skeleton
	/// missing some of these is fine; entries with no matching bone are simply never
	/// consulted.
	/// </summary>
	public static Dictionary<string, string> Mixamo()
	{
		var map = new Dictionary<string, string>( StringComparer.Ordinal );
		void Add( string source, string target ) => map[source] = target;

		Add( "mixamorig:Hips", "pelvis" );
		Add( "mixamorig:Spine", "spine_0" );
		Add( "mixamorig:Spine1", "spine_1" );
		Add( "mixamorig:Spine2", "spine_2" );
		Add( "mixamorig:Neck", "neck_0" );
		Add( "mixamorig:Head", "head" );

		foreach ( var side in new[] { "Left", "Right" } )
		{
			var s = side == "Left" ? "L" : "R";
			Add( $"mixamorig:{side}Shoulder", $"clavicle_{s}" );
			Add( $"mixamorig:{side}Arm", $"arm_upper_{s}" );
			Add( $"mixamorig:{side}ForeArm", $"arm_lower_{s}" );
			Add( $"mixamorig:{side}Hand", $"hand_{s}" );
			Add( $"mixamorig:{side}HandThumb1", $"finger_thumb_0_{s}" );
			Add( $"mixamorig:{side}HandThumb2", $"finger_thumb_1_{s}" );
			Add( $"mixamorig:{side}HandThumb3", $"finger_thumb_2_{s}" );
			Add( $"mixamorig:{side}HandIndex1", $"finger_index_0_{s}" );
			Add( $"mixamorig:{side}HandIndex2", $"finger_index_1_{s}" );
			Add( $"mixamorig:{side}HandIndex3", $"finger_index_2_{s}" );
			Add( $"mixamorig:{side}HandMiddle1", $"finger_middle_0_{s}" );
			Add( $"mixamorig:{side}HandMiddle2", $"finger_middle_1_{s}" );
			Add( $"mixamorig:{side}HandMiddle3", $"finger_middle_2_{s}" );
			Add( $"mixamorig:{side}HandRing1", $"finger_ring_0_{s}" );
			Add( $"mixamorig:{side}HandRing2", $"finger_ring_1_{s}" );
			Add( $"mixamorig:{side}HandRing3", $"finger_ring_2_{s}" );
			// No pinky on Citizen: ride the ring chain (see the class doc).
			Add( $"mixamorig:{side}HandPinky1", $"finger_ring_0_{s}" );
			Add( $"mixamorig:{side}HandPinky2", $"finger_ring_1_{s}" );
			Add( $"mixamorig:{side}HandPinky3", $"finger_ring_2_{s}" );
			Add( $"mixamorig:{side}UpLeg", $"leg_upper_{s}" );
			Add( $"mixamorig:{side}Leg", $"leg_lower_{s}" );
			Add( $"mixamorig:{side}Foot", $"ankle_{s}" );
			Add( $"mixamorig:{side}ToeBase", $"ball_{s}" );
		}

		return map;
	}
}
