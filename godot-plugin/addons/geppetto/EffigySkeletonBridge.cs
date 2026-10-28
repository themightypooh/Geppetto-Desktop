using System;
using Godot;
using Effigy;

namespace Geppetto;

/// <summary>
/// Converts Geppetto <see cref="Skeleton"/> into Godot <see cref="Skeleton3D"/> and <see cref="Skin"/>.
/// Transforms local rest poses with Basis' = M * B * M^T and origin' = 0.0254 * M * o.
/// </summary>
public static class EffigySkeletonBridge
{
	/// <summary>
	/// Transforms an Effigy local bone transform <see cref="Xform"/> to Godot <see cref="Transform3D"/>.
	/// </summary>
	public static Transform3D TransformXform( in Xform xf )
	{
		// Basis' = M * B * M^T:
		// Col0 = (Y.y, Y.z, Y.x)
		// Col1 = (Z.y, Z.z, Z.x)
		// Col2 = (X.y, X.z, X.x)
		var col0 = new Vector3( xf.Y.y, xf.Y.z, xf.Y.x );
		var col1 = new Vector3( xf.Z.y, xf.Z.z, xf.Z.x );
		var col2 = new Vector3( xf.X.y, xf.X.z, xf.X.x );
		var basis = new Basis( col0, col1, col2 );

		var origin = EffigyMeshBridge.TransformPosition( xf.Origin.x, xf.Origin.y, xf.Origin.z );
		return new Transform3D( basis, origin );
	}

	/// <summary>
	/// Builds a <see cref="Skeleton3D"/> with all bones, hierarchy, and rest transforms.
	/// </summary>
	public static Skeleton3D ToGodotSkeleton( Skeleton rig )
	{
		ArgumentNullException.ThrowIfNull( rig );
		var skel = new Skeleton3D();
		skel.Name = "Skeleton3D";

		for ( int i = 0; i < rig.Bones.Count; i++ )
		{
			var bone = rig.Bones[i];
			int boneIdx = skel.GetBoneCount();
			skel.AddBone( bone.Name );
			if ( bone.Parent >= 0 )
				skel.SetBoneParent( boneIdx, bone.Parent );

			var rest = TransformXform( bone.Local );
			skel.SetBoneRest( boneIdx, rest );
		}

		skel.ResetBonePoses();
		return skel;
	}

	/// <summary>
	/// Generates a <see cref="Skin"/> resource for a <see cref="Skeleton3D"/> from its rest transforms.
	/// </summary>
	public static Skin CreateSkin( Skeleton3D skeleton )
	{
		ArgumentNullException.ThrowIfNull( skeleton );
		return skeleton.CreateSkinFromRestTransforms();
	}
}
