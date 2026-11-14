using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Effigy.Import;

public static class Importer
{
	public enum Format { Obj, Fbx, Gltf, Glb, Unknown }

	public static Format DetectFormat( string path )
	{
		var ext = Path.GetExtension( path ?? "" ).ToLowerInvariant();
		return ext switch
		{
			".obj" => Format.Obj,
			".fbx" => Format.Fbx,
			".gltf" => Format.Gltf,
			".glb" => Format.Glb,
			_ => Format.Unknown
		};
	}

	public static bool Validate( string path )
	{
		if ( string.IsNullOrEmpty( path ) ) return false;
		if ( !File.Exists( path ) ) return false;
		return DetectFormat( path ) != Format.Unknown;
	}

	public static object Import( string path, ImportOptions options = null )
	{
		options = options ?? ImportOptions.Default;
		if ( !Validate( path ) ) return null;
		// Format-specific importers (Obj/Fbx/Gltf) plug in here.
		return new { Path = path, Format = DetectFormat( path ).ToString() };
	}

	public static List<string> SupportedFormats() => new List<string> { "obj", "fbx", "gltf", "glb" };
}