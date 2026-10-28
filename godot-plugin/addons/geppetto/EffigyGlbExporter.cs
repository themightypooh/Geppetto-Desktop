#if TOOLS
using System;
using System.IO;
using Godot;

namespace Geppetto;

/// <summary>
/// G13: writes a Geppetto scene to a portable binary glTF (.glb) through Godot's own GLTFDocument.
/// Carries the mesh, its StandardMaterial3D materials (textures embedded as PNG), the Skeleton3D + Skin,
/// and every animation on the scene's AnimationPlayer.
///
/// Use from C#: <c>EffigyGlbExporter.ExportEffigy("res://models/x.effigy", "/tmp/x.glb")</c>, or with a scene
/// the importer already built: <c>EffigyGlbExporter.ExportScene(root, "/tmp/x.glb")</c>.
/// Use from GDScript: <c>load("res://addons/geppetto/EffigyGlbExporter.cs").new().ExportEffigyFile(in, out)</c>.
/// </summary>
[Tool]
public partial class EffigyGlbExporter : RefCounted
{
	/// <summary>Instance wrapper so GDScript tools can call the exporter.</summary>
	public Error ExportEffigyFile( string effigyPath, string glbPath ) => ExportEffigy( effigyPath, glbPath );

	/// <summary>Instance wrapper so GDScript tools can export a scene they already hold.</summary>
	public Error ExportSceneRoot( Node root, string glbPath ) => ExportScene( root, glbPath );

	/// <summary>Imports a .effigy (plus its companion .riganim clips) and writes it as .glb.</summary>
	public static Error ExportEffigy( string effigyPath, string glbPath )
	{
		var importer = new EffigySceneImporter();
		var root = importer._ImportScene( effigyPath, 0, new Godot.Collections.Dictionary() ) as Node;
		if ( root == null )
		{
			GD.PrintErr( $"[EffigyGlbExporter] Import failed for {effigyPath}" );
			return Error.CantOpen;
		}
		try
		{
			return ExportScene( root, glbPath );
		}
		finally
		{
			root.Free();
		}
	}

	/// <summary>Writes an already imported Geppetto scene (Node3D root) to .glb (or .gltf if the path says so).</summary>
	public static Error ExportScene( Node root, string glbPath )
	{
		if ( root == null )
			return Error.InvalidParameter;

		string outPath = Path.GetFullPath( ProjectSettings.GlobalizePath( glbPath ) );
		string outDir = Path.GetDirectoryName( outPath );
		if ( !string.IsNullOrEmpty( outDir ) )
			Directory.CreateDirectory( outDir );

		// Skinned meshes are posed by the skeleton; make sure each mesh points at its skeleton so the
		// glTF skin is written against the right joints.
		foreach ( var node in root.FindChildren( "*", "MeshInstance3D", true, false ) )
		{
			if ( node is MeshInstance3D mi && mi.Skin != null && mi.GetParent() is Skeleton3D && mi.Skeleton.IsEmpty )
				mi.Skeleton = new NodePath( ".." );
		}

		var doc = new GltfDocument
		{
			// PNG keeps textures lossless and embedded in the .glb buffer.
			ImageFormat = "PNG",
			// KeepRoot writes the scene root as a plain glTF node, so no Godot-only extension (GODOT_single_root) lands in the file.
			RootNodeMode = GltfDocument.RootNodeModeEnum.KeepRoot,
		};
		var state = new GltfState();

		var err = doc.AppendFromScene( root, state );
		if ( err != Error.Ok )
		{
			GD.PrintErr( $"[EffigyGlbExporter] AppendFromScene failed: {err}" );
			return err;
		}

		err = doc.WriteToFilesystem( state, outPath );
		if ( err != Error.Ok )
		{
			GD.PrintErr( $"[EffigyGlbExporter] WriteToFilesystem({outPath}) failed: {err}" );
			return err;
		}

		GD.Print( $"[EffigyGlbExporter] Wrote {outPath} (meshes={state.GetMeshes().Count}, materials={state.GetMaterials().Count}, " +
			$"images={state.GetImages().Count}, skins={state.GetSkins().Count}, animations={state.GetAnimations().Count})" );
		return Error.Ok;
	}
}
#endif
