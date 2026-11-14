using System;
using System.Collections.Generic;

namespace Effigy.Import;

public sealed class ImportOptions
{
	public string Format { get; set; } = "auto"; // obj, fbx, gltf, glb, auto
	public bool MergeMeshes { get; set; } = false;
	public bool ImportMaterials { get; set; } = true;
	public bool ImportAnimations { get; set; } = true;
	public bool ImportSkeleton { get; set; } = true;
	public float Scale { get; set; } = 1f;
	public bool Validate { get; set; } = true;

	public static ImportOptions Default => new ImportOptions();
}