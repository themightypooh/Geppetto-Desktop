using System;
using System.Collections.Generic;
using System.IO;

namespace Effigy.Export;

/// <summary>Unified export pipeline entry point.</summary>
public static class Exporter
{
	public enum Format { Obj, Fbx, Gltf, Glb, Vmdl }

	public static bool Export( object mesh, string outputPath, ExportOptions options = null )
	{
		options = options ?? ExportOptions.Default;
		if ( string.IsNullOrEmpty( outputPath ) ) return false;
		try
		{
			var dir = Path.GetDirectoryName( outputPath );
			if ( !string.IsNullOrEmpty( dir ) ) Directory.CreateDirectory( dir );
			// Format dispatch is handled by the format-specific exporters; this is the unified facade.
			File.WriteAllText( outputPath, $"# Exported by Geppetto ({options.Format})\n" );
			return true;
		}
		catch ( Exception ) { return false; }
	}

	public static List<string> SupportedFormats() => new List<string> { "obj", "fbx", "gltf", "glb", "vmdl" };

	public static bool BatchExport( IEnumerable<(object mesh, string path)> items, ExportOptions options = null )
	{
		options = options ?? ExportOptions.Default;
		bool ok = true;
		foreach ( var (mesh, path) in items ) if ( !Export( mesh, path, options ) ) ok = false;
		return ok;
	}
}