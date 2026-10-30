#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Godot;

namespace Geppetto;

/// <summary>
/// Rig workspace: corrective shapes - the morphs that put back the volume linear blend skinning
/// loses at a bent joint (<see cref="CorrectiveMorph"/>). Shapes live on the studio
/// (<c>PartStudio.Correctives</c>), save in the .effigy rig block, and are built into morphs at
/// export from the export's own weights; this tool manages the list and previews a build.
/// </summary>
public static class GeppettoRigCorrectives
{
	public static IReadOnlyList<CorrectiveShape> List( GeppettoDocument doc ) => doc.Studio.Correctives;

	/// <summary>Add a corrective for a bone: the bend it fully corrects (default 90°), axis picked by
	/// <see cref="CorrectiveMorph.AutoAxis"/>.</summary>
	public static bool AddCorrective( GeppettoDocument doc, int boneIndex, float angle = 90f )
	{
		var rig = doc.Studio.Rig;
		if ( boneIndex < 0 || boneIndex >= rig.Count )
			{ GeppettoRig.Refuse( "Add corrective: select a bone first" ); return false; }
		var bone = rig.Bones[boneIndex];
		if ( doc.Studio.Correctives.Any( c => c.Bone == bone.Name && MathF.Abs( c.Angle - angle ) < 0.01f ) )
			{ GeppettoRig.Refuse( $"Add corrective: '{bone.Name}' already corrects {angle}°" ); return false; }
		return GeppettoRig.RigEdit( doc, $"Add corrective {bone.Name} {angle}°", studio =>
		{
			studio.Correctives.Add( new CorrectiveShape
			{
				Bone = bone.Name,
				Angle = angle,
				Axis = CorrectiveMorph.AutoAxis( studio.Rig, studio.Rig.IndexOf( bone.Name ) ),
			} );
			return true;
		} );
	}

	public static bool RemoveCorrective( GeppettoDocument doc, int index )
	{
		var list = doc.Studio.Correctives;
		if ( index < 0 || index >= list.Count )
			{ GeppettoRig.Refuse( "Remove corrective: nothing selected" ); return false; }
		var name = list[index].ToString();
		return GeppettoRig.RigEdit( doc, $"Remove corrective {name}", studio =>
		{
			studio.Correctives.RemoveAt( index );
			return true;
		} );
	}

	/// <summary>
	/// Preview: build the analytic morph on a body and report how many vertices it moves and by how
	/// much. Exact at the shape's angle; driven in the animgraph by <see cref="CorrectiveMorph.WeightAt"/>.
	/// </summary>
	public static (int Moved, float MaxDelta, string? Error) PreviewCorrective(
		GeppettoDocument doc, int shapeIndex, string bodyId )
	{
		var shapes = doc.Studio.Correctives;
		if ( shapeIndex < 0 || shapeIndex >= shapes.Count )
			return (0, 0, "no corrective selected");
		var body = doc.Studio.Bodies.FirstOrDefault( b => b.Id == bodyId );
		if ( body == null )
			return (0, 0, $"no body '{bodyId}'");
		var morph = CorrectiveMorph.Build( body.Mesh, doc.Studio.Rig, shapes[shapeIndex] );
		if ( morph == null )
			return (0, 0, "no corrective could be built - the joint may be rigid (no shared vertices)" );
		int moved = 0;
		float max = 0;
		foreach ( var d in morph.Deltas )
		{
			var l = d.Length;
			if ( l > 1e-6f ) moved++;
			if ( l > max ) max = l;
		}
		return (moved, max, null);
	}
}
#endif
