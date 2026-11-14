using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>Twist bones for a limb: the bones that unwind an axial roll gradually along the
/// limb instead of landing the whole rotation on one ring of vertices.
///
/// The mesh already knows how to USE twist bones — <see cref="TwistWeights.Spread"/> splits a
/// limb bone's weights across whatever `<see cref="Bone"/>'s name ends in `_twist<n>` — but
/// nothing in the tree could MAKE them: a mesh that arrived from somewhere else had a single
/// `forearm_L` and no twist bones at all, so the first wrist roll pinched the limb shut.
/// This is the missing half: generate the bones, spread the weights, distribute the roll.
///
/// CONVENTION (citizen's, which <see cref="TwistWeights.Spread"/> already assumes): twist
/// bones are children of the limb bone, named `{limb}_twist{n}`, twist 0 at the limb's head,
/// the rest spaced evenly down it, each spanning an equal share of the limb. The limb bone
/// itself is the last station, at its tail.</summary>
public static class TwistBones
{
	/// <summary>Append <paramref name="count"/> twist bones to <paramref name="limbBone"/>:
	/// `{limb}_twist0` at the head, `{limb}_twist{count-1}` partway down, each spanning
	/// Length / count. Returns the new bone indices, head-first. Names are made unique when
	/// a twist bone of that name already exists.</summary>
	public static int[] AddTwists( Skeleton skeleton, int limbBone, int count )
	{
		if ( skeleton is null )
			throw new ArgumentNullException( nameof( skeleton ) );
		if ( limbBone < 0 || limbBone >= skeleton.Count )
			throw new ArgumentOutOfRangeException( nameof( limbBone ), "No such bone in this skeleton." );
		if ( count < 1 )
			throw new ArgumentOutOfRangeException( nameof( count ), "A limb needs at least one twist bone." );
		var limb = skeleton.Bones[limbBone];
		if ( limb.Length <= 0f )
			throw new ArgumentException( $"Bone '{limb.Name}' has no length, so there is no limb to put twist bones on." );

		var added = new int[count];
		for ( var i = 0; i < count; i++ )
		{
			var fraction = (float)i / count;
			var name = skeleton.UniqueName( $"{limb.Name}_twist{i}" );
			var local = Xform.Translate( new Vec3( 0, fraction * limb.Length, 0 ) );
			added[i] = skeleton.AddBone( name, limbBone, local, limb.Length / count );
		}
		return added;
	}

	/// <summary>Twist bones of a limb, head-first. Empty when the limb has none.</summary>
	public static int[] OfLimb( Skeleton skeleton, int limbBone )
	{
		if ( skeleton is null )
			throw new ArgumentNullException( nameof( skeleton ) );
		if ( limbBone < 0 || limbBone >= skeleton.Count )
			throw new ArgumentOutOfRangeException( nameof( limbBone ), "No such bone in this skeleton." );
		var prefix = skeleton.Bones[limbBone].Name + "_twist";
		var found = new List<(int Number, int Bone)>();
		for ( var b = 0; b < skeleton.Count; b++ )
		{
			var bone = skeleton.Bones[b];
			if ( bone.Parent != limbBone || !bone.Name.StartsWith( prefix, StringComparison.Ordinal ) )
				continue;
			if ( int.TryParse( bone.Name[prefix.Length..], out var n ) )
				found.Add( (n, b) );
		}
		found.Sort( (x, y) => x.Number.CompareTo( y.Number ) );
		var result = new int[found.Count];
		for ( var i = 0; i < result.Length; i++ )
			result[i] = found[i].Bone;
		return result;
	}

	/// <summary>Distribute an axial roll over a limb's twist bones: twist bone i takes
	/// (i + 1) / (count + 1) of <paramref name="twistRadians"/>, so the roll ramps from a
	/// small turn at the head to nearly the full turn at the tail, and the child joint
	/// inherits the whole twist through the pose. The rotation is about the limb's own
	/// axis (+Y, head to tail) through the bone head; positions do not move. Call this on
	/// the POSED skeleton, after the limb's own pose is set. Bones with no twist bones are
	/// left alone.</summary>
	public static void ApplyTwist( Skeleton posed, int limbBone, float twistRadians )
	{
		if ( posed is null )
			throw new ArgumentNullException( nameof( posed ) );
		var twists = OfLimb( posed, limbBone );
		if ( twists.Length == 0 )
			return;
		var axis = new Vec3( 0, 1, 0 );
		for ( var i = 0; i < twists.Length; i++ )
		{
			var share = (float)(i + 1) / (twists.Length + 1);
			var bone = posed.Bones[twists[i]];
			bone.Local = bone.Local * Xform.Rotate( axis, twistRadians * share );
		}
	}

	/// <summary>Twist bones of a limb as (bone, along-fraction) pairs, for a caller that needs
	/// the stations — the same stations <see cref="TwistWeights.Spread"/> measures from the
	/// world bind positions.</summary>
	public static (int Bone, float Along)[] Stations( Skeleton skeleton, int limbBone )
	{
		var twists = OfLimb( skeleton, limbBone );
		var limb = skeleton.Bones[limbBone];
		var result = new (int, float)[twists.Length];
		for ( var i = 0; i < twists.Length; i++ )
		{
			var along = Vec3.Dot(
				skeleton.WorldBind( twists[i] ).Origin - skeleton.WorldBind( limbBone ).Origin,
				skeleton.BoneDirection( limbBone ) );
			result[i] = (twists[i], limb.Length > 0f ? along / limb.Length : 0f);
		}
		return result;
	}
}
