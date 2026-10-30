#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Godot;

namespace Geppetto;

/// <summary>
/// Rig workspace: IK chains (<see cref="RigIk"/> / <see cref="ChainIk"/>). Chains live on the studio
/// (<c>PartStudio.IkChains</c>), save in the .effigy rig block in the .ctrlrig IkConstraints shape, and
/// solve with the same code the export and the take checks use. Chain length picks the solver:
/// 1 aims, 2 is the exact two-bone solve, 3+ is CCD (or FABRIK when set). No IK/FK switch - s&box
/// had none, and blending is what <see cref="RigIk.Weight"/> is for.
/// </summary>
public static class GeppettoRigIk
{
	public static IReadOnlyList<RigIk> List( GeppettoDocument doc ) => doc.Studio.IkChains;

	/// <summary>Add an IK chain ending at <paramref name="endBoneIndex"/>, running up
	/// <paramref name="chainLength"/> bones.</summary>
	public static bool AddIk( GeppettoDocument doc, int endBoneIndex, int chainLength = 2, string? name = null )
	{
		var rig = doc.Studio.Rig;
		if ( endBoneIndex < 0 || endBoneIndex >= rig.Count )
			{ GeppettoRig.Refuse( "Add IK: select the end bone first" ); return false; }
		if ( chainLength < 1 )
			{ GeppettoRig.Refuse( "Add IK: chain length must be at least 1" ); return false; }
		var bone = rig.Bones[endBoneIndex].Name;
		name = string.IsNullOrWhiteSpace( name ) ? $"IK_{bone}" : name.Trim();
		if ( doc.Studio.IkChains.Any( k => k.Name == name ) )
			{ GeppettoRig.Refuse( $"Add IK: a chain named '{name}' already exists" ); return false; }
		return GeppettoRig.RigEdit( doc, $"Add IK {name}", studio =>
		{
			studio.IkChains.Add( new RigIk { Name = name, EndBone = bone, ChainLength = chainLength } );
			return true;
		} );
	}

	public static bool RemoveIk( GeppettoDocument doc, int index )
	{
		var list = doc.Studio.IkChains;
		if ( index < 0 || index >= list.Count )
			{ GeppettoRig.Refuse( "Remove IK: nothing selected" ); return false; }
		var name = list[index].Name;
		return GeppettoRig.RigEdit( doc, $"Remove IK {name}", studio =>
		{
			studio.IkChains.RemoveAt( index );
			return true;
		} );
	}

	public static bool SetPole( GeppettoDocument doc, int index, Vec3 pole )
	{
		var list = doc.Studio.IkChains;
		if ( index < 0 || index >= list.Count )
			{ GeppettoRig.Refuse( "Set pole: nothing selected" ); return false; }
		return GeppettoRig.RigEdit( doc, $"Set IK pole {list[index].Name}", studio =>
		{
			studio.IkChains[index].Pole = pole;
			return true;
		} );
	}

	public static bool SetEnabled( GeppettoDocument doc, int index, bool enabled )
	{
		var list = doc.Studio.IkChains;
		if ( index < 0 || index >= list.Count )
			{ GeppettoRig.Refuse( "Toggle IK: nothing selected" ); return false; }
		return GeppettoRig.RigEdit( doc, $"{(enabled ? "Enable" : "Disable")} IK {list[index].Name}", studio =>
		{
			studio.IkChains[index].Enabled = enabled;
			return true;
		} );
	}

	/// <summary>
	/// Solve once from the bind pose toward <paramref name="target"/>; returns the end bone's position
	/// after the solve. A reachable target should land on (or very near) the goal.
	/// </summary>
	public static (Vec3 End, string? Error) SolveOnce( GeppettoDocument doc, int index, Vec3 target )
	{
		var list = doc.Studio.IkChains;
		if ( index < 0 || index >= list.Count )
			return (new Vec3(), "no IK chain selected");
		var ik = list[index];
		var rig = doc.Studio.Rig;
		int end = rig.IndexOf( ik.EndBone );
		if ( end < 0 )
			return (new Vec3(), $"end bone '{ik.EndBone}' is not on the rig");
		var world = Enumerable.Range( 0, rig.Count ).Select( i => rig.WorldBind( i ) ).ToArray();
		var solved = ChainIk.Apply( rig, world, ik,
			new RigPose( target, Quat.Identity, Vec3.One ), null );
		if ( solved == null )
			return (new Vec3(), "solve failed");
		return (solved[end].Origin, null);
	}
}
#endif
