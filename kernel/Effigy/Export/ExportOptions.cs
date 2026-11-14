using System;
using System.Collections.Generic;

namespace Effigy.Export;

/// <summary>Options for the unified export pipeline.</summary>
public sealed class ExportOptions
{
	public string Format { get; set; } = "obj"; // obj, fbx, gltf, glb, vmdl
	public string OutputPath { get; set; } = "";
	public bool IncludeMaterials { get; set; } = true;
	public bool IncludeAnimations { get; set; } = true;
	public bool IncludeLods { get; set; } = false;
	public int LodCount { get; set; } = 2;
	public bool Triangulate { get; set; } = true;
	public bool Compress { get; set; } = false;
	public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

	public static ExportOptions Default => new ExportOptions();
}