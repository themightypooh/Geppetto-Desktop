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

/// <summary>
/// One space-switching prop handoff: at <see cref="Frame"/> a prop stops living in
/// <see cref="FromSpace"/> and starts living in <see cref="ToSpace"/> — world to a hand, one
/// hand to the other — without jumping. The jump is what this type exists to prevent: the
/// prop's world matrix at the handoff frame is carried across unchanged, and only its local
/// matrix (relative to the new parent) is recomputed.
///
/// ROW VECTORS, like the rest of this file: a world matrix is local * parent, so the new
/// local matrix is world * inverse(new parent), and the preserved world matrix is
/// local * new parent. Get the order backwards and the prop teleports to the mirrored
/// position — the round-trip in the tests pins the order, not the comment.
/// </summary>
public sealed record PropHandoff( string PropName, float Frame, string FromSpace, string ToSpace, Matrix4x4 PreservedWorld );

/// <summary>
/// Space-switching math for prop handoffs, shared by the desktop preview and (in its own
/// Transform form) the runtime event player.
///
/// A handoff is not a new placement: it is the SAME world matrix expressed under a new
/// parent. <see cref="SwitchSpace"/> returns the local matrix that keeps the prop exactly
/// where it was, and <see cref="PreservedWorld"/> proves it by composing back. A prop handed
/// world → hand_L → hand_R goes through SwitchSpace twice and still lands on the same world
/// matrix it started with, to float precision — that chain is the whole feature, and it is
/// what the tests check.
/// </summary>
public static class PropHandoffMath
{
	/// <summary>The local matrix under <paramref name="newParentWorld"/> that keeps the prop at <paramref name="world"/>.</summary>
	public static Matrix4x4 SwitchSpace( Matrix4x4 world, Matrix4x4 newParentWorld )
	{
		if ( !Matrix4x4.Invert( newParentWorld, out var inverse ) )
			throw new InvalidOperationException( "the new parent's world matrix is not invertible, so the prop cannot be re-parented under it" );
		return world * inverse;
	}

	/// <summary>The world matrix a prop at <paramref name="local"/> under <paramref name="parentWorld"/> sits at. SwitchSpace's inverse.</summary>
	public static Matrix4x4 PreservedWorld( Matrix4x4 local, Matrix4x4 parentWorld ) => local * parentWorld;

	/// <summary>True when <paramref name="local"/> under <paramref name="parentWorld"/> reproduces <paramref name="world"/> within <paramref name="tolerance"/>.</summary>
	public static bool Preserves( Matrix4x4 world, Matrix4x4 local, Matrix4x4 parentWorld, float tolerance = 1e-4f )
	{
		var back = PreservedWorld( local, parentWorld );
		return Math.Abs( back.M11 - world.M11 ) <= tolerance && Math.Abs( back.M22 - world.M22 ) <= tolerance
			&& Math.Abs( back.M33 - world.M33 ) <= tolerance && Math.Abs( back.M44 - world.M44 ) <= tolerance
			&& Math.Abs( back.M41 - world.M41 ) <= tolerance && Math.Abs( back.M42 - world.M42 ) <= tolerance
			&& Math.Abs( back.M43 - world.M43 ) <= tolerance;
	}
}

/// <summary>
/// The desktop side of a prop handoff: a clip prop's world matrix at a frame, re-expressed
/// under a new parent (a bone's world matrix, or another prop's) without moving in the world.
/// </summary>
public static class PropSceneHandoffExtensions
{
	/// <summary>
	/// This prop's local matrix under <paramref name="newParentWorld"/> at <paramref name="frame"/>,
	/// preserving its clip world matrix exactly. The caller owns the new parent's matrix — the
	/// clip places props in the world, it does not know where a hand bone is; the preview's
	/// bone evaluation does, and it hands that matrix in here.
	/// </summary>
	public static Matrix4x4 HandoffLocal( this PropScene prop, RigAnimFile clip, float frame, float positionScale, Matrix4x4 newParentWorld ) =>
		PropHandoffMath.SwitchSpace( prop.WorldMatrix( clip, frame, positionScale ), newParentWorld );
}

/// <summary>
/// Pure camera-lens evaluation for the desktop preview: field of view and depth of field
/// from a camera track's FOV plus caller-supplied lens values.
///
/// THE MIRROR NOTE. The same math lives in <c>DmxCameraWriter.Evaluate</c> on the writer
/// side, because the desktop project cannot reference the editor project that owns the
/// writer. The two are deliberately small and identically documented so Lane P's sync can
/// unify them; until then, this is the one the preview calls, and its tests pin the numbers
/// here, not in the writer.
///
/// FOV: vertical from the track, horizontal from the aspect ratio. DOF: a thin-lens relative
/// blur curve, not a physical circle of confusion — no focal length is authored on a camera
/// track, so <see cref="CameraPreviewEvaluation.BlurAt"/> returns |1 − focal/subject| ÷
/// aperture, a dimensionless strength that is 0 at the focal plane, grows as the subject
/// leaves it, and shrinks as the aperture number grows (a larger f-number is a sharper
/// picture). It is comparable between shots in one clip, not an absolute blur radius in
/// pixels; a renderer that wants pixels multiplies by its own focal-length scale.
/// </summary>
public static class CameraPreviewEvaluation
{
	public sealed record Evaluation( float VerticalFieldOfViewDeg, float HorizontalFieldOfViewDeg, float FocalDistance, float Aperture )
	{
		/// <summary>Relative blur at a subject distance: 0 in focus, rising either side of it.</summary>
		public float BlurAt( float subjectDistance )
		{
			if ( FocalDistance <= 0f || Aperture <= 0f || subjectDistance <= 0f ) return 0f;
			return Math.Abs( 1f - FocalDistance / subjectDistance ) / Aperture;
		}
	}

	/// <summary>Evaluate a shot: vertical FOV clamped to the engine's 1..179° range, horizontal from <paramref name="aspect"/> (width ÷ height).</summary>
	public static Evaluation Evaluate( float fieldOfViewDeg, float aspect, float focalDistance = 0f, float aperture = 0f )
	{
		var vertical = Math.Clamp( fieldOfViewDeg, 1f, 179f );
		var halfVertical = vertical * MathF.PI / 360f;
		var horizontal = 2f * MathF.Atan( MathF.Tan( halfVertical ) * Math.Max( aspect, 1e-6f ) ) * 180f / MathF.PI;
		return new Evaluation( vertical, horizontal, Math.Max( focalDistance, 0f ), Math.Max( aperture, 0f ) );
	}
}
