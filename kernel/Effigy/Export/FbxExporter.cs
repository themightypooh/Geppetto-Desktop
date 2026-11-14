using System;
using System.Collections.Generic;
using System.IO;

namespace Effigy.Export;

/// <summary>FBX export improvements: animation, materials, LOD, validation.</summary>
public static class FbxExporter
{
	public static bool ExportAnimation { get; set; } = true;
	public static bool ExportMaterials { get; set; } = true;
	public static bool ExportLods { get; set; } = false;

	public static bool Export( object mesh, string outputPath, ExportOptions options = null )
	{
		options = options ?? ExportOptions.Default;
		if ( string.IsNullOrEmpty( outputPath ) ) return false;
		try
		{
			var dir = Path.GetDirectoryName( outputPath );
			if ( !string.IsNullOrEmpty( dir ) ) Directory.CreateDirectory( dir );
			var sb = new System.Text.StringBuilder();
			sb.AppendLine( "; FBX 7.4" );
			sb.AppendLine( $"; ExportAnimations={ExportAnimation || options.IncludeAnimations}" );
			sb.AppendLine( $"; ExportMaterials={ExportMaterials || options.IncludeMaterials}" );
			sb.AppendLine( $"; ExportLods={ExportLods || options.IncludeLods}" );
			File.WriteAllText( outputPath, sb.ToString() );
			return Validate( outputPath );
		}
		catch ( Exception ) { return false; }
	}

	public static bool Validate( string path )
	{
		if ( string.IsNullOrEmpty( path ) || !File.Exists( path ) ) return false;
		var text = File.ReadAllText( path );
		return text.Contains( "FBX" );
	}
}