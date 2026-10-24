#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// A reference prop from the clip: a model the clip places in the scene beside the character.
///
/// A .riganim's "ReferenceProps" name a model, a placement and whether they are visible. The
/// kernel's parser keeps the placement but not the model, so the desktop reads the clip's own
/// retained JSON for the model path and the visibility flag, and asks the kernel for the
/// placement as the animation runs.
///
/// The prop's mesh is a separate model with its own ModelDoc, so it has its own materials
/// and its own import scale. Its size is relative to the character: s&box imports each
/// ModelDoc with its own scale, so the prop's scale is the ratio between the prop's import
/// scale and the character's. The position comes from the clip, in the clip's own unit,
/// which the clip-space adapter has already measured.
/// </summary>
public sealed class PropScene : IDisposable
{
	public string Name { get; }
	/// <summary>The ModelDoc the prop names, project-relative.</summary>
	public string ModelPath { get; }
	public ViewportMesh Mesh { get; }
	/// <summary>Scale applied to the prop's mesh, from the ModelDoc import chain.</summary>
	public float MeshScale { get; }
	/// <summary>Whether the clip shows this prop.</summary>
	public bool Visible { get; }
	/// <summary>What the prop's own material resolution had to say.</summary>
	public IReadOnlyList<MaterialDiagnostic> Diagnostics { get; }

	public PropScene( string name, string modelPath, ViewportMesh mesh, float meshScale, bool visible,
		IReadOnlyList<MaterialDiagnostic>? diagnostics = null )
	{
		Name = name;
		ModelPath = modelPath;
		Mesh = mesh;
		MeshScale = meshScale;
		Visible = visible;
		Diagnostics = diagnostics ?? Array.Empty<MaterialDiagnostic>();
	}

	public void Dispose() => Mesh.Dispose();

	/// <summary>The prop's world matrix at a clip frame: the clip's placement, with the
	/// placement's translation converted into the rig's unit, and the prop's own import
	/// scale applied to the mesh.</summary>
	public Matrix4x4 WorldMatrix( RigAnimFile clip, float frame, float positionScale ) =>
		WorldMatrix( clip, frame, positionScale, Xform.Identity );

	/// <summary>The prop's matrix in the rig's model space. The clip places it in s&box model
	/// space and its mesh is authored in that space too, so both ends of the placement go
	/// through <paramref name="rigFromClip"/>.</summary>
	public Matrix4x4 WorldMatrix( RigAnimFile clip, float frame, float positionScale, Xform rigFromClip )
	{
		Matrix4x4 result = Matrix4x4.Identity;
		var placement = clip.PlacementAt( Name, frame );
		if ( placement is { } pose )
		{
			var scaled = new RigPose(
				new Vec3( pose.Position.x * positionScale, pose.Position.y * positionScale, pose.Position.z * positionScale ),
				pose.Rotation, pose.Scale );
			result = MarionettePlayer.ToGlMatrix( scaled.ToXform() );
		}
		// System.Numerics uses row vectors: scale mesh-local vertices before placement.
		var toRig = MarionettePlayer.ToGlMatrix( rigFromClip );
		Matrix4x4.Invert( toRig, out var toClip );
		return Matrix4x4.CreateScale( MeshScale ) * toClip * result * toRig;
	}
}

/// <summary>Builds the clip's reference props from the project's own files.</summary>
public static class PropSceneBuilder
{
	/// <summary>One prop as the clip's JSON states it.</summary>
	public sealed record Definition( string Name, string Model, bool Visible );

	/// <summary>Read the prop definitions from a clip's own JSON. The kernel keeps the
	/// placement but not the model, so this is where the model path and the visibility
	/// come from. A clip with no props gives an empty list, not an error.</summary>
	public static IReadOnlyList<Definition> ReadDefinitions( string? clipJson )
	{
		if ( string.IsNullOrWhiteSpace( clipJson ) ) return Array.Empty<Definition>();
		var definitions = new List<Definition>();
		try
		{
			using var document = JsonDocument.Parse( clipJson );
			if ( !document.RootElement.TryGetProperty( "ReferenceProps", out var props ) ||
				props.ValueKind != JsonValueKind.Array )
				return definitions;
			foreach ( var prop in props.EnumerateArray() )
			{
				string name = prop.TryGetProperty( "Name", out var n ) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
				string model = prop.TryGetProperty( "Model", out var m ) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
				bool visible = !prop.TryGetProperty( "Visible", out var v ) || v.ValueKind != JsonValueKind.False;
				if ( name.Length == 0 || model.Length == 0 ) continue;
				definitions.Add( new Definition( name, model, visible ) );
			}
		}
		catch ( JsonException ) { return definitions; }
		return definitions;
	}

	/// <summary>
	/// Build every visible prop the clip names. A prop whose ModelDoc or mesh file cannot be
	/// read is skipped with a reason rather than failing the whole load: the character is
	/// still worth showing without its scenery.
	/// </summary>
	public static IReadOnlyList<PropScene> Build( IReadOnlyList<Definition> definitions, AssetPathResolver paths,
		string documentId, float characterImportScale, List<string>? skipped = null )
	{
		var scenes = new List<PropScene>();
		if ( definitions.Count == 0 ) return scenes;
		foreach ( var definition in definitions )
		{
			if ( !definition.Visible ) continue;
			try
			{
				var reference = new AssetReference( documentId + ":" + definition.Name, definition.Model );
				var resolved = paths.ResolveRead( reference, requireExists: true );
				var model = VmdlReader.Read( resolved.CanonicalPath );
				if ( model is null )
				{
					skipped?.Add( $"{definition.Name}: '{definition.Model}' could not be read." );
					continue;
				}
				if ( model.RenderMeshFile.Length == 0 )
				{
					skipped?.Add( $"{definition.Name}: '{definition.Model}' names no render mesh." );
					continue;
				}
				var meshReference = new AssetReference( documentId + ":" + definition.Name + ":mesh", model.RenderMeshFile );
				var meshAsset = paths.ResolveRead( meshReference, requireExists: true );
				byte[] bytes = File.ReadAllBytes( meshAsset.CanonicalPath );
				var imported = FbxImport.Read( bytes );
				var mesh = ViewportMesh.FromFbx( imported, 0, meshAsset.CanonicalPath );
				// The prop's own ModelDoc says which .vmat each of its materials means.
				var map = VmdlMaterialMap.ForModel( paths, model, documentId );
				var resolver = map?.Resolver ?? new MaterialResolver( paths, documentId );
				mesh.ApplyMaterials( resolver );
				// s&box imports each ModelDoc with its own scale; the ratio keeps the prop
				// the size the clip's scene has it, whatever the character used.
				float scale = characterImportScale > 0 ? model.ImportScale / characterImportScale : model.ImportScale;
				scenes.Add( new PropScene( definition.Name, definition.Model, mesh, scale, true, resolver.Diagnostics ) );
			}
			catch ( Exception ex )
			{
				skipped?.Add( $"{definition.Name}: {ex.Message}" );
			}
		}
		return scenes;
	}
}
