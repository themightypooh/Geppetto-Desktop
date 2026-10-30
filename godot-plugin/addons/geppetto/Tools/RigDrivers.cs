#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Godot;

namespace Geppetto;

/// <summary>
/// Rig workspace: morph drivers - bone pose -> morph weight (<see cref="MorphDriver"/>), the rig-side
/// half of driven shape keys. Drivers live on the studio (<c>PartStudio.Drivers</c>) and save in the
/// .effigy rig block; the shape-key features carry their own driver fields for the feature side.
/// Drivers on one morph multiply (<see cref="MorphDriver.WeightsAt"/>).
/// </summary>
public static class GeppettoRigDrivers
{
	public static IReadOnlyList<MorphDriver> List( GeppettoDocument doc ) => doc.Studio.Drivers;

	/// <summary>Add a driver: <paramref name="morph"/> fades in as <paramref name="bone"/> turns from
	/// <paramref name="rest"/>° to <paramref name="target"/>° about <paramref name="axis"/>.</summary>
	public static bool AddDriver( GeppettoDocument doc, string morph, string bone,
		DriverChannel channel = DriverChannel.Rotation, DriverAxis axis = DriverAxis.Y,
		float rest = 0f, float target = 90f )
	{
		if ( string.IsNullOrWhiteSpace( morph ) )
			{ GeppettoRig.Refuse( "Add driver: name the morph it drives" ); return false; }
		if ( doc.Studio.Rig.IndexOf( bone ) < 0 )
			{ GeppettoRig.Refuse( $"Add driver: no bone '{bone}' on the rig" ); return false; }
		if ( doc.Studio.Drivers.Any( d => d.Morph == morph.Trim() && d.Bone == bone ) )
			{ GeppettoRig.Refuse( $"Add driver: '{morph.Trim()}' is already driven by '{bone}'" ); return false; }
		return GeppettoRig.RigEdit( doc, $"Add driver {morph.Trim()} <- {bone}", studio =>
		{
			studio.Drivers.Add( new MorphDriver
			{
				Morph = morph.Trim(),
				Bone = bone,
				Channel = channel,
				Axis = axis,
				Rest = rest,
				Target = target,
			} );
			return true;
		} );
	}

	public static bool RemoveDriver( GeppettoDocument doc, int index )
	{
		var list = doc.Studio.Drivers;
		if ( index < 0 || index >= list.Count )
			{ GeppettoRig.Refuse( "Remove driver: nothing selected" ); return false; }
		var name = $"{list[index].Morph} <- {list[index].Bone}";
		return GeppettoRig.RigEdit( doc, $"Remove driver {name}", studio =>
		{
			studio.Drivers.RemoveAt( index );
			return true;
		} );
	}

	/// <summary>
	/// Preview: pose the driver bone to <paramref name="degrees"/> and read the weight back. Should be
	/// ~0 at the rest angle and ~1 at the target angle.
	/// </summary>
	public static (float Weight, string? Error) PreviewDriver( GeppettoDocument doc, int index, float degrees )
	{
		var list = doc.Studio.Drivers;
		if ( index < 0 || index >= list.Count )
			return (0, "no driver selected");
		var driver = list[index];
		var rig = doc.Studio.Rig;
		var posed = PoseSpaceCorrective.PosedFor( rig, driver, degrees );
		if ( posed == null )
			return (0, "driver cannot be posed (distance drivers pose nothing)" );
		var local = posed.Bones.Select( b => b.Local ).ToList();
		var world = Enumerable.Range( 0, posed.Count ).Select( i => posed.WorldBind( i ) ).ToList();
		return (driver.Evaluate( rig, local, world ), null);
	}
}
#endif
