using System;
using System.Collections.Generic;
using Effigy;

namespace Marionette.Desktop;

/// <summary>Per-clip export for the game-clip pipeline
/// (<c>godlike-game-clip-pipeline</c>, Wave 2 P1): one named clip from the library,
/// baked and sampled at 30 fps, out through the existing animated-GLB path.
///
/// GLB only, by design: <see cref="FbxExportAdapter"/> writes static rigged FBX
/// only (mesh + skeleton + skin, no animation curves — <c>FbxWriter.WriteBinaryFile</c>
/// takes a mesh and a rig, not a clip; Desktop has not ported Mobile's
/// <c>AnimExport.AddFbxAnimations</c>). Asking for FBX here is an honest refusal,
/// not a silent static export. Animated portable interchange on Desktop is GLB
/// (<see cref="GlbExport.WriteAnimated"/>) or native <c>.riganim</c>.</summary>
public static class ClipExportService
{
	/// <summary>Export one library entry as a single-clip GLB: the entry resolves
	/// (trim/scale/repeat) and the resolved clip goes to
	/// <see cref="GlbExport.WriteAnimated"/> with the given parts and skeleton.</summary>
	public static byte[] ExportClip( ClipLibraryEntry entry, Skeleton skeleton, IReadOnlyList<GlbPart> parts, string format = "glb" )
	{
		if ( entry is null )
			throw new ArgumentNullException( nameof( entry ) );
		if ( skeleton is null )
			throw new ArgumentNullException( nameof( skeleton ) );
		if ( parts is null )
			throw new ArgumentNullException( nameof( parts ) );
		if ( !string.Equals( format, "glb", StringComparison.OrdinalIgnoreCase ) )
			throw new ArgumentException(
				$"per-clip '{format}' export is not supported: FbxExportAdapter writes static rigged FBX only (mesh + skeleton + skin, no animation) — animated portable interchange on Desktop is GLB",
				nameof( format ) );
		return ExportClip( entry.Resolve(), entry.Name, skeleton, parts );
	}

	/// <summary>Export one clip directly as a single-clip GLB.</summary>
	public static byte[] ExportClip( RigAnimFile clip, string name, Skeleton skeleton, IReadOnlyList<GlbPart> parts )
	{
		if ( clip is null )
			throw new ArgumentNullException( nameof( clip ) );
		if ( skeleton is null )
			throw new ArgumentNullException( nameof( skeleton ) );
		if ( parts is null )
			throw new ArgumentNullException( nameof( parts ) );
		return GlbExport.WriteAnimated( parts, skeleton, new[] { new GlbClip( name, clip ) } );
	}
}
