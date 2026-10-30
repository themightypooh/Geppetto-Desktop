#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Godot;

namespace Geppetto;

/// <summary>
/// Rig workspace: NLA strips - take files (<see cref="RigAnimFile"/>) placed on a timeline
/// (<see cref="NlaTimeline"/>), Blender-style. Strips are non-destructive: they reference takes,
/// never rewrite them. The .effigy saves the take reference; the take itself is reloaded on open,
/// so a strip never freezes a stale copy of an edited take.
/// </summary>
public static class GeppettoRigNla
{
	public static IReadOnlyList<NlaStrip> List( GeppettoDocument doc ) => doc.Studio.NlaStrips;

	/// <summary>
	/// Add a strip for a take file. <paramref name="takePath"/> may be absolute or relative to the
	/// .effigy; it is stored relative when it sits beside the .effigy. Plays once from
	/// <paramref name="start"/> unless <paramref name="end"/> is past it.
	/// </summary>
	public static bool AddStrip( GeppettoDocument doc, string takePath, float start = 0f, float end = 0f,
		string? name = null )
	{
		if ( string.IsNullOrWhiteSpace( takePath ) )
		{
			GeppettoRig.Refuse( "Add strip: pick a take file first" );
			return false;
		}
		string dir = Path.GetDirectoryName( doc.DiskPath ) ?? "";
		string full = Path.IsPathRooted( takePath ) ? takePath : Path.Combine( dir, takePath );
		RigAnimFile action;
		try { action = RigAnimFile.ReadFile( full ); }
		catch ( Exception e )
		{
			GeppettoRig.Refuse( $"Add strip: could not read take '{takePath}': {e.Message}" );
			return false;
		}
		name = string.IsNullOrWhiteSpace( name ) ? Path.GetFileNameWithoutExtension( full ) : name.Trim();
		string stored = full.StartsWith( dir, StringComparison.Ordinal )
			? Path.GetRelativePath( dir, full ) : full;
		var strip = new NlaStrip
		{
			Name = name,
			Action = action,
			TakePath = stored,
			Start = start,
			End = end,
		};
		var problems = strip.Validate();
		if ( problems.Count > 0 )
		{
			GeppettoRig.Refuse( $"Add strip: the take contributes nothing - {problems[0]}" );
			return false;
		}
		return GeppettoRig.RigEdit( doc, $"Add NLA strip {name}", studio =>
		{
			studio.NlaStrips.Add( strip );
			return true;
		} );
	}

	public static bool RemoveStrip( GeppettoDocument doc, int index )
	{
		var list = doc.Studio.NlaStrips;
		if ( index < 0 || index >= list.Count )
		{
			GeppettoRig.Refuse( "Remove strip: nothing selected" );
			return false;
		}
		var name = list[index].Name;
		return GeppettoRig.RigEdit( doc, $"Remove NLA strip {name}", studio =>
		{
			studio.NlaStrips.RemoveAt( index );
			return true;
		} );
	}

	public static bool SetMuted( GeppettoDocument doc, int index, bool muted )
	{
		var list = doc.Studio.NlaStrips;
		if ( index < 0 || index >= list.Count )
		{
			GeppettoRig.Refuse( "Mute strip: nothing selected" );
			return false;
		}
		return GeppettoRig.RigEdit( doc, $"{(muted ? "Mute" : "Unmute")} NLA strip {list[index].Name}", studio =>
		{
			studio.NlaStrips[index].Muted = muted;
			return true;
		} );
	}

	public static bool SetWeight( GeppettoDocument doc, int index, float weight )
	{
		var list = doc.Studio.NlaStrips;
		if ( index < 0 || index >= list.Count )
		{
			GeppettoRig.Refuse( "Set strip weight: nothing selected" );
			return false;
		}
		return GeppettoRig.RigEdit( doc, $"Set NLA strip weight {list[index].Name}", studio =>
		{
			studio.NlaStrips[index].Weight = Math.Clamp( weight, 0f, 1f );
			return true;
		} );
	}

	/// <summary>Evaluate the timeline at a frame: one local pose per bone the strips drive.</summary>
	public static (Dictionary<string, RigPose>? Poses, string? Error) Evaluate( GeppettoDocument doc, float frame )
	{
		var timeline = new NlaTimeline { Strips = doc.Studio.NlaStrips.ToList() };
		try { return (timeline.Evaluate( frame ), null); }
		catch ( Exception e ) { return (null, e.Message); }
	}
}
#endif
