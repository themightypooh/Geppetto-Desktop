#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Effigy;

namespace Marionette.Desktop;

public enum ExportFormat
{
	Obj,
	Effigy,
	RigAnim
}

public enum PoseMode
{
	BindPose,
	Posed
}

public enum MaterialTexturePolicy
{
	None,
	ReferenceOnly,
	CopyTextures
}

public enum OverwritePolicy
{
	FailIfExists,
	Overwrite
}

public sealed record ExportCapabilityReport
{
	public bool CanExport { get; init; }
	public string? ErrorMessage { get; init; }
	public IReadOnlyList<string> PreservedFeatures { get; init; } = Array.Empty<string>();
	public IReadOnlyList<string> LostFeatures { get; init; } = Array.Empty<string>();
	public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
	public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
	public bool PreservesGeometry { get; init; }
	public bool PreservesRig { get; init; }
	public bool PreservesSkinning { get; init; }
	public bool PreservesMorphs { get; init; }
	public bool PreservesAnimation { get; init; }
	public bool PreservesTangents { get; init; }
	public bool PreservesTextures { get; init; }
	public string UnitsAndAxes { get; init; } = "";
}

public sealed record ExportRequest
{
	public DocumentSession Session { get; init; }
	public ExportFormat Format { get; init; }
	public string DestinationPath { get; init; }
	public PoseMode PoseMode { get; init; } = PoseMode.BindPose;
	public MaterialTexturePolicy MaterialPolicy { get; init; } = MaterialTexturePolicy.ReferenceOnly;
	public OverwritePolicy OverwritePolicy { get; init; } = OverwritePolicy.FailIfExists;
	public long? Revision { get; init; }
	public float? PlayheadTime { get; init; }
	public bool BakePosedMesh { get; init; } = false;

	public ExportRequest(
		DocumentSession session,
		ExportFormat format,
		string destinationPath,
		PoseMode poseMode = PoseMode.BindPose,
		MaterialTexturePolicy materialPolicy = MaterialTexturePolicy.ReferenceOnly,
		OverwritePolicy overwritePolicy = OverwritePolicy.FailIfExists,
		long? revision = null,
		float? playheadTime = null,
		bool bakePosedMesh = false )
	{
		Session = session ?? throw new ArgumentNullException( nameof( session ) );
		Format = format;
		DestinationPath = destinationPath ?? throw new ArgumentNullException( nameof( destinationPath ) );
		PoseMode = poseMode;
		MaterialPolicy = materialPolicy;
		OverwritePolicy = overwritePolicy;
		Revision = revision;
		PlayheadTime = playheadTime;
		BakePosedMesh = bakePosedMesh;
	}
}

public sealed record ExportResult(
	bool Success,
	string? DestinationPath = null,
	IReadOnlyList<string>? GeneratedFiles = null,
	ExportCapabilityReport? CapabilityReport = null,
	string? ErrorMessage = null,
	long ExportedRevision = 0
);

/// <summary>
/// Service providing fail-safe, capability-checked export of 3D models and animation clips.
/// Reads strictly from retained CPU source data (PartStudio or FbxSource), never GPU buffers.
/// Enforces path sanitization, multi-file atomic staging, and non-destructive session retention.
/// </summary>
public sealed class ExportService
{
	public ExportCapabilityReport EvaluateCapabilities( ExportRequest request )
	{
		ArgumentNullException.ThrowIfNull( request );
		ArgumentNullException.ThrowIfNull( request.Session );

		var preserved = new List<string>();
		var lost = new List<string>();
		var warnings = new List<string>();
		var errors = new List<string>();

		var session = request.Session;
		bool hasStudio = session.Studio is not null;
		bool hasFbx = session.FbxSource is not null;
		bool hasGeometry = hasStudio || hasFbx;

		bool hasSkeleton = (session.Studio?.Rig is { Count: > 0 }) || (session.FbxSource?.Skeleton is { Count: > 0 });
		bool hasSkinning = (session.Studio?.Rig is { Count: > 0 } && session.Studio.BodyBoneMap.Count > 0) ||
			(session.FbxSource?.Result?.Weights is { Count: > 0 } && session.FbxSource.Result.Weights.Any( w => w is not null ));
		bool hasAnimation = session.Clip is not null || !string.IsNullOrWhiteSpace( session.ClipPath );
		bool hasMorphs = false;

		bool canExport = true;
		string? primaryError = null;

		bool preservesGeometry = false;
		bool preservesRig = false;
		bool preservesSkinning = false;
		bool preservesMorphs = false;
		bool preservesAnimation = false;
		bool preservesTangents = false;
		bool preservesTextures = false;
		string unitsAndAxes = "";

		if ( string.IsNullOrWhiteSpace( request.DestinationPath ) )
		{
			canExport = false;
			primaryError = "Export destination path must not be empty.";
			errors.Add( primaryError );
		}

		switch ( request.Format )
		{
			case ExportFormat.Obj:
			{
				unitsAndAxes = "Wavefront OBJ: inches, Z-up, UV V-flipped (origin bottom-left)";
				if ( !hasGeometry )
				{
					canExport = false;
					primaryError = "Session contains no geometry to export as OBJ.";
					errors.Add( primaryError );
				}
				else
				{
					preservesGeometry = true;
					preserved.Add( "Polygonal mesh geometry (vertices, normals, UV coordinates, faces)" );
				}

				if ( request.MaterialPolicy != MaterialTexturePolicy.None )
				{
					preservesTextures = true;
					preserved.Add( "Material library assignments and MTL descriptor" );
				}
				else
				{
					lost.Add( "Material library and texture assignments (suppressed by policy)" );
				}

				if ( hasSkeleton )
				{
					lost.Add( "Skeletal hierarchy and bone nodes (unsupported by OBJ)" );
				}

				if ( hasSkinning )
				{
					lost.Add( "Vertex skinning weights and bone bindings (unsupported by OBJ)" );
				}

				if ( hasMorphs )
				{
					lost.Add( "Morph targets and blendshapes (unsupported by OBJ)" );
				}

				if ( hasAnimation )
				{
					lost.Add( "Skeletal animation tracks and keyframes (unsupported by OBJ)" );
					if ( request.PoseMode == PoseMode.Posed )
					{
						if ( request.BakePosedMesh )
						{
							preserved.Add( "Current pose baked to static polygonal mesh" );
							warnings.Add( "Animation tracks discarded; current playhead pose baked to static geometry." );
						}
						else
						{
							canExport = false;
							primaryError = "OBJ cannot export skeletal animation. Explicitly set BakePosedMesh = true to bake this pose to a static mesh.";
							errors.Add( primaryError );
						}
					}
					else
					{
						preserved.Add( "Static rest / bind pose geometry" );
						warnings.Add( "Exporting rest / bind pose; animation keyframes will not be included in OBJ output." );
					}
				}
				else if ( request.PoseMode == PoseMode.Posed && request.BakePosedMesh )
				{
					preserved.Add( "Current pose baked to static polygonal mesh" );
				}

				if ( !string.IsNullOrWhiteSpace( request.DestinationPath ) &&
					!request.DestinationPath.EndsWith( ".obj", StringComparison.OrdinalIgnoreCase ) )
				{
					warnings.Add( "Destination path does not end with .obj extension." );
				}
				break;
			}

			case ExportFormat.Effigy:
			{
				unitsAndAxes = "Effigy: inches, Z-up";
				if ( !hasStudio )
				{
					canExport = false;
					primaryError = "Cannot export as .effigy: session is not an Effigy PartStudio document (external FBX cannot masquerade as .effigy without explicit conversion).";
					errors.Add( primaryError );
				}
				else
				{
					preservesGeometry = true;
					preservesRig = hasSkeleton;
					preservesSkinning = hasSkinning;
					preservesMorphs = hasMorphs;
					preservesTextures = true;
					preserved.Add( "PartStudio feature tree, parameters, and expressions" );
					preserved.Add( "Polygonal geometry and body structure" );
					if ( hasSkeleton ) preserved.Add( "Skeletal hierarchy and bind poses" );
					if ( hasSkinning ) preserved.Add( "Body-to-bone binding maps" );
					preserved.Add( "Material slot assignments and UV scales" );
				}

				if ( hasAnimation )
				{
					lost.Add( "Skeletal animation clip (PartStudio documents store model definition, not animation clips; export as .riganim to preserve motion)" );
				}

				if ( !string.IsNullOrWhiteSpace( request.DestinationPath ) &&
					!request.DestinationPath.EndsWith( ".effigy", StringComparison.OrdinalIgnoreCase ) )
				{
					warnings.Add( "Destination path does not end with .effigy extension." );
				}
				break;
			}

			case ExportFormat.RigAnim:
			{
				unitsAndAxes = "RigAnim: frame rate, local bone transforms";
				if ( session.Clip is null && string.IsNullOrWhiteSpace( session.ClipPath ) )
				{
					canExport = false;
					primaryError = "Cannot export as .riganim: session contains no animation clip.";
					errors.Add( primaryError );
				}
				else
				{
					preservesAnimation = true;
					preserved.Add( "Skeletal animation tracks, bone keyframes, and tangents" );
					preserved.Add( "Clip frame rate, duration, and root motion settings" );
					if ( !string.IsNullOrEmpty( session.ClipSourceJson ) )
					{
						preserved.Add( "Unknown JSON sentinels, reference properties, marks, and events" );
					}
				}

				if ( hasGeometry )
				{
					lost.Add( "Mesh geometry, bodies, materials, and textures (RigAnim carries skeletal animation only, not 3D meshes)" );
				}

				if ( hasSkeleton )
				{
					lost.Add( "Skeleton mesh and bone geometry (RigAnim references rig assets, does not embed skeleton definitions)" );
				}

				if ( !string.IsNullOrWhiteSpace( request.DestinationPath ) &&
					!request.DestinationPath.EndsWith( ".riganim", StringComparison.OrdinalIgnoreCase ) )
				{
					warnings.Add( "Destination path does not end with .riganim extension." );
				}
				break;
			}

			default:
				canExport = false;
				primaryError = $"Unsupported export format: {request.Format}";
				errors.Add( primaryError );
				break;
		}

		return new ExportCapabilityReport
		{
			CanExport = canExport,
			ErrorMessage = primaryError,
			PreservedFeatures = preserved,
			LostFeatures = lost,
			Warnings = warnings,
			Errors = errors,
			PreservesGeometry = preservesGeometry,
			PreservesRig = preservesRig,
			PreservesSkinning = preservesSkinning,
			PreservesMorphs = preservesMorphs,
			PreservesAnimation = preservesAnimation,
			PreservesTangents = preservesTangents,
			PreservesTextures = preservesTextures,
			UnitsAndAxes = unitsAndAxes
		};
	}

	public ExportResult Export(
		ExportRequest request,
		IProgress<float>? progress = null,
		CancellationToken cancellationToken = default )
	{
		ArgumentNullException.ThrowIfNull( request );
		long capturedRevision = request.Revision ?? request.Session.Revision;

		var capabilityReport = EvaluateCapabilities( request );
		if ( !capabilityReport.CanExport )
		{
			return new ExportResult(
				Success: false,
				DestinationPath: request.DestinationPath,
				CapabilityReport: capabilityReport,
				ErrorMessage: capabilityReport.ErrorMessage ?? "Export capability evaluation failed.",
				ExportedRevision: capturedRevision
			);
		}

		string fullDestPath = Path.GetFullPath( request.DestinationPath );
		string destDir = Path.GetDirectoryName( fullDestPath )!;
		if ( !Directory.Exists( destDir ) )
		{
			Directory.CreateDirectory( destDir );
		}

		// Check overwrite refusal on destination file before staging
		if ( File.Exists( fullDestPath ) && request.OverwritePolicy == OverwritePolicy.FailIfExists )
		{
			return new ExportResult(
				Success: false,
				DestinationPath: fullDestPath,
				CapabilityReport: capabilityReport,
				ErrorMessage: $"Refusing to overwrite existing destination file: '{fullDestPath}'.",
				ExportedRevision: capturedRevision
			);
		}

		string? stagingDir = null;
		var generatedFiles = new List<string>();

		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			progress?.Report( 0.1f );

			stagingDir = Path.Combine( destDir, $".export_staging_{Guid.NewGuid():N}" );
			Directory.CreateDirectory( stagingDir );

			cancellationToken.ThrowIfCancellationRequested();
			progress?.Report( 0.3f );

			switch ( request.Format )
			{
				case ExportFormat.Obj:
					generatedFiles = ExportObjStaged( request, stagingDir, fullDestPath, cancellationToken );
					break;
				case ExportFormat.Effigy:
					generatedFiles = ExportEffigyStaged( request, stagingDir, fullDestPath, cancellationToken );
					break;
				case ExportFormat.RigAnim:
					generatedFiles = ExportRigAnimStaged( request, stagingDir, fullDestPath, cancellationToken );
					break;
				default:
					throw new NotSupportedException( $"Unsupported export format: {request.Format}" );
			}

			cancellationToken.ThrowIfCancellationRequested();
			progress?.Report( 0.7f );

			// Recheck overwrite refusal in case target was created concurrently
			if ( File.Exists( fullDestPath ) && request.OverwritePolicy == OverwritePolicy.FailIfExists )
			{
				return new ExportResult(
					Success: false,
					DestinationPath: fullDestPath,
					CapabilityReport: capabilityReport,
					ErrorMessage: $"Refusing to overwrite existing destination file: '{fullDestPath}'.",
					ExportedRevision: capturedRevision
				);
			}

			// Atomic move / replace from staging into final destination
			var finalFiles = new List<string>();
			foreach ( var stagedFile in Directory.EnumerateFiles( stagingDir, "*", SearchOption.AllDirectories ) )
			{
				string relPath = Path.GetRelativePath( stagingDir, stagedFile );
				string targetFile = Path.Combine( destDir, relPath );
				string? targetSubDir = Path.GetDirectoryName( targetFile );
				if ( !string.IsNullOrEmpty( targetSubDir ) && !Directory.Exists( targetSubDir ) )
				{
					Directory.CreateDirectory( targetSubDir );
				}

				if ( File.Exists( targetFile ) )
				{
					if ( request.OverwritePolicy == OverwritePolicy.FailIfExists )
					{
						throw new IOException( $"Refusing to overwrite existing file '{targetFile}'." );
					}
					File.Move( stagedFile, targetFile, overwrite: true );
				}
				else
				{
					File.Move( stagedFile, targetFile );
				}
				finalFiles.Add( targetFile );
			}

			progress?.Report( 1.0f );

			return new ExportResult(
				Success: true,
				DestinationPath: fullDestPath,
				GeneratedFiles: finalFiles,
				CapabilityReport: capabilityReport,
				ErrorMessage: null,
				ExportedRevision: capturedRevision
			);
		}
		catch ( OperationCanceledException )
		{
			return new ExportResult(
				Success: false,
				DestinationPath: fullDestPath,
				CapabilityReport: capabilityReport,
				ErrorMessage: "Export was cancelled.",
				ExportedRevision: capturedRevision
			);
		}
		catch ( Exception ex )
		{
			return new ExportResult(
				Success: false,
				DestinationPath: fullDestPath,
				CapabilityReport: capabilityReport,
				ErrorMessage: ex.Message,
				ExportedRevision: capturedRevision
			);
		}
		finally
		{
			// Safe cleanup of staging directory on completion, failure, or cancellation
			try
			{
				if ( Directory.Exists( stagingDir ) )
				{
					Directory.Delete( stagingDir, recursive: true );
				}
			}
			catch
			{
				// Ignore cleanup errors
			}
		}
	}

	public Task<ExportResult> ExportAsync(
		ExportRequest request,
		IProgress<float>? progress = null,
		CancellationToken cancellationToken = default )
	{
		return Task.Run( () => Export( request, progress, cancellationToken ), cancellationToken );
	}

	List<string> ExportObjStaged( ExportRequest request, string stagingDir, string finalDestPath, CancellationToken cancellationToken )
	{
		var session = request.Session;
		string baseFileName = Path.GetFileNameWithoutExtension( finalDestPath );
		string safeMtlFileName = SanitizeMaterialName( baseFileName ) + ".mtl";

		string stagedObjPath = Path.Combine( stagingDir, Path.GetFileName( finalDestPath ) );
		string stagedMtlPath = Path.Combine( stagingDir, safeMtlFileName );

		Func<int, string> materialResolver = slot =>
		{
			string? raw = null;
			if ( session.Studio is not null )
			{
				raw = session.Studio.NameForSlot( slot );
			}
			else if ( session.FbxSource?.Result?.MaterialSlots is not null &&
				session.FbxSource.Result.MaterialSlots.TryGetValue( slot, out var fbxMat ) )
			{
				raw = fbxMat;
			}
			return SanitizeMaterialName( raw, slot );
		};

		var pieces = ExtractPieces( session, request.PoseMode, request.PlayheadTime );
		if ( pieces.Count == 0 )
		{
			throw new InvalidOperationException( "No mesh pieces could be extracted from retained source data." );
		}

		cancellationToken.ThrowIfCancellationRequested();

		var usedSlots = new HashSet<int>();
		foreach ( var piece in pieces )
		{
			if ( piece.Mesh is null ) continue;
			foreach ( var f in piece.Mesh.Faces )
			{
				usedSlots.Add( f.Material );
			}
		}

		// Write OBJ with mtllib reference
		var sb = new StringBuilder();
		if ( request.MaterialPolicy != MaterialTexturePolicy.None )
		{
			sb.Append( $"mtllib {safeMtlFileName}\n" );
		}
		sb.Append( ObjWriter.Write( pieces, ObjWriter.DefaultSmoothingAngleDegrees, materialResolver ) );
		File.WriteAllText( stagedObjPath, sb.ToString() );

		// Write companion MTL file
		if ( request.MaterialPolicy != MaterialTexturePolicy.None )
		{
			var mtlSb = new StringBuilder();
			mtlSb.Append( "# Effigy Wavefront MTL Material Library\n" );
			mtlSb.Append( $"# Staged for: {Path.GetFileName( finalDestPath )}\n\n" );

			foreach ( int slot in usedSlots.OrderBy( s => s ) )
			{
				string matName = materialResolver( slot );
				var entry = MaterialPalette.ForName( matName );

				mtlSb.Append( $"newmtl {matName}\n" );
				mtlSb.Append( "Ka 1.000000 1.000000 1.000000\n" );
				mtlSb.Append( string.Format( CultureInfo.InvariantCulture, "Kd {0:0.000000} {1:0.000000} {2:0.000000}\n", entry.R, entry.G, entry.B ) );
				mtlSb.Append( string.Format( CultureInfo.InvariantCulture, "Ks {0:0.000000} {1:0.000000} {2:0.000000}\n", entry.Metallic, entry.Metallic, entry.Metallic ) );
				float ns = Math.Clamp( (1f - entry.Roughness) * 128f + 2f, 0f, 1000f );
				mtlSb.Append( string.Format( CultureInfo.InvariantCulture, "Ns {0:0.000000}\n", ns ) );
				mtlSb.Append( "d 1.000000\n" );
				mtlSb.Append( "illum 2\n\n" );
			}

			File.WriteAllText( stagedMtlPath, mtlSb.ToString() );
		}

		return new List<string> { stagedObjPath, stagedMtlPath };
	}

	List<string> ExportEffigyStaged( ExportRequest request, string stagingDir, string finalDestPath, CancellationToken cancellationToken )
	{
		var session = request.Session;
		if ( session.Studio is null )
		{
			throw new InvalidOperationException( "Effigy export requires a PartStudio." );
		}

		cancellationToken.ThrowIfCancellationRequested();
		string stagedPath = Path.Combine( stagingDir, Path.GetFileName( finalDestPath ) );
		string content = StudioDocument.Write( session.Studio );
		File.WriteAllText( stagedPath, content );
		return new List<string> { stagedPath };
	}

	List<string> ExportRigAnimStaged( ExportRequest request, string stagingDir, string finalDestPath, CancellationToken cancellationToken )
	{
		var session = request.Session;
		if ( session.Clip is null )
		{
			throw new InvalidOperationException( "RigAnim export requires an animation clip." );
		}

		cancellationToken.ThrowIfCancellationRequested();
		string stagedPath = Path.Combine( stagingDir, Path.GetFileName( finalDestPath ) );
		string content = RigAnimWriter.Write( session.Clip, session.ClipSourceJson );
		File.WriteAllText( stagedPath, content );
		return new List<string> { stagedPath };
	}

	static List<ObjReader.ObjPiece> ExtractPieces( DocumentSession session, PoseMode poseMode, float? playheadTime )
	{
		var pieces = new List<ObjReader.ObjPiece>();

		if ( session.Studio is not null )
		{
			var studio = session.Studio;
			if ( studio.Bodies.Count == 0 && studio.Features.Count > 0 )
			{
				studio.Rebuild();
			}

			var rig = studio.Rig;
			bool hasRig = rig is { Count: > 0 };
			Xform[]? bindWorld = null;
			Xform[]? poseWorld = null;

			if ( hasRig && poseMode == PoseMode.Posed )
			{
				bindWorld = RigAnimFile.ModelSpace( rig, rig.Bones.Select( b => b.Local ).ToArray() );
				var player = new MarionettePlayer( rig, startPlaying: false );
				if ( session.Clip is not null )
				{
					player.LoadClip( session.Clip );
				}
				else if ( !string.IsNullOrWhiteSpace( session.ClipPath ) && File.Exists( session.ClipPath ) )
				{
					player.LoadClip( session.ClipPath );
				}
				player.Time = playheadTime ?? session.PlayheadTime;
				player.Step( 0 );
				poseWorld = player.WorldPose.ToArray();
			}

			int pieceIdx = 0;
			foreach ( var body in studio.Bodies )
			{
				if ( !body.Visible || body.Mesh is null || body.Mesh.VertexCount == 0 )
					continue;

				string pieceName = string.IsNullOrWhiteSpace( body.Name ) ? $"body_{pieceIdx}" : body.Name;
				pieceIdx++;

				PolyMesh meshToExport;
				if ( hasRig && poseMode == PoseMode.Posed && bindWorld is not null && poseWorld is not null )
				{
					SkinWeights skin = body.Mesh.Skin;
					if ( skin is null || skin.Count == 0 )
					{
						if ( studio.BodyBoneMap.TryGetValue( body.Id, out var boneName ) )
						{
							int boneIdx = rig.IndexOf( boneName );
							if ( boneIdx >= 0 )
							{
								skin = new SkinWeights( body.Mesh.VertexCount );
								for ( int v = 0; v < body.Mesh.VertexCount; v++ )
									skin[v] = new[] { new BoneWeight( boneIdx, 1f ) };
							}
						}
					}

					if ( skin is not null && skin.Count == body.Mesh.VertexCount )
					{
						var deformed = SkinBinder.Deform( body.Mesh.Positions, skin, bindWorld, poseWorld );
						meshToExport = new PolyMesh { Positions = new List<Vec3>( deformed ) };
						foreach ( var f in body.Mesh.Faces )
							meshToExport.AddFace( (int[])f.Indices.Clone(), (Vec2[])f.UVs.Clone(), f.Material );
						if ( body.Mesh.VertexColors is not null )
							meshToExport.VertexColors = (Vec4[])body.Mesh.VertexColors.Clone();
					}
					else
					{
						meshToExport = body.Mesh.Clone();
					}
				}
				else
				{
					meshToExport = body.Mesh.Clone();
				}

				pieces.Add( new ObjReader.ObjPiece( pieceName, meshToExport ) );
			}

			// If no bodies were extracted individually, try ToMeshWithBodies
			if ( pieces.Count == 0 )
			{
				var (merged, ranges) = studio.ToMeshWithBodies( b => b.Visible );
				if ( merged is not null && merged.VertexCount > 0 )
				{
					PolyMesh meshToExport;
					if ( hasRig && poseMode == PoseMode.Posed && bindWorld is not null && poseWorld is not null )
					{
						SkinWeights skin = SkinBinder.BindBodies( merged, ranges, studio.BodyBoneMap, rig );
						if ( skin is not null && skin.Count == merged.VertexCount )
						{
							var deformed = SkinBinder.Deform( merged.Positions, skin, bindWorld, poseWorld );
							meshToExport = new PolyMesh { Positions = new List<Vec3>( deformed ) };
							foreach ( var f in merged.Faces )
								meshToExport.AddFace( (int[])f.Indices.Clone(), (Vec2[])f.UVs.Clone(), f.Material );
							if ( merged.VertexColors is not null )
								meshToExport.VertexColors = (Vec4[])merged.VertexColors.Clone();
						}
						else
						{
							meshToExport = merged.Clone();
						}
					}
					else
					{
						meshToExport = merged.Clone();
					}
					pieces.Add( new ObjReader.ObjPiece( "model", meshToExport ) );
				}
			}
		}
		else if ( session.FbxSource is not null )
		{
			var fbx = session.FbxSource;
			var rig = fbx.Skeleton;
			bool hasRig = rig is { Count: > 0 };
			Xform[]? bindWorld = null;
			Xform[]? poseWorld = null;

			if ( hasRig && poseMode == PoseMode.Posed && rig is not null )
			{
				bindWorld = RigAnimFile.ModelSpace( rig, rig.Bones.Select( b => b.Local ).ToArray() );
				var player = new MarionettePlayer( rig, startPlaying: false );
				if ( session.Clip is not null )
				{
					player.LoadClip( session.Clip );
				}
				else if ( !string.IsNullOrWhiteSpace( session.ClipPath ) && File.Exists( session.ClipPath ) )
				{
					player.LoadClip( session.ClipPath );
				}
				player.Time = playheadTime ?? session.PlayheadTime;
				player.Step( 0 );
				poseWorld = player.WorldPose.ToArray();
			}

			for ( int pi = 0; pi < fbx.Result.Pieces.Count; pi++ )
			{
				var piece = fbx.Result.Pieces[pi];
				if ( piece.Mesh is null || piece.Mesh.VertexCount == 0 )
					continue;

				PolyMesh meshToExport;
				if ( hasRig && poseMode == PoseMode.Posed && bindWorld is not null && poseWorld is not null )
				{
					var rawWeights = pi < fbx.Result.Weights.Count ? fbx.Result.Weights[pi] : null;
					var skin = piece.Mesh.Skin ?? (rawWeights is not null ? FbxImport.Resolve( rawWeights, rig, null, out _ ) : null);
					if ( skin is not null && skin.Count == piece.Mesh.VertexCount )
					{
						var deformed = SkinBinder.Deform( piece.Mesh.Positions, skin, bindWorld, poseWorld );
						meshToExport = new PolyMesh { Positions = new List<Vec3>( deformed ) };
						foreach ( var f in piece.Mesh.Faces )
							meshToExport.AddFace( (int[])f.Indices.Clone(), (Vec2[])f.UVs.Clone(), f.Material );
						if ( piece.Mesh.VertexColors is not null )
							meshToExport.VertexColors = (Vec4[])piece.Mesh.VertexColors.Clone();
					}
					else
					{
						meshToExport = piece.Mesh.Clone();
					}
				}
				else
				{
					meshToExport = piece.Mesh.Clone();
				}

				pieces.Add( new ObjReader.ObjPiece( piece.Name, meshToExport ) );
			}
		}

		return pieces;
	}

	/// <summary>
	/// Sanitizes material names so they cannot contain directory traversal characters ('..', '/', '\')
	/// or characters invalid for file names, preventing directory escapes in .mtl descriptors.
	/// </summary>
	public static string SanitizeMaterialName( string? rawName, int slot = 0 )
	{
		if ( string.IsNullOrWhiteSpace( rawName ) )
			return ObjWriter.DefaultMaterialName( slot );

		string s = rawName.Replace( '\\', '/' );
		while ( s.Contains( "../", StringComparison.Ordinal ) )
		{
			s = s.Replace( "../", "", StringComparison.Ordinal );
		}
		s = Path.GetFileName( s );

		var sb = new StringBuilder();
		foreach ( char c in s )
		{
			if ( char.IsLetterOrDigit( c ) || c == '_' || c == '-' || c == '.' )
			{
				sb.Append( c );
			}
			else
			{
				sb.Append( '_' );
			}
		}
		string result = sb.ToString().Trim( '.', '_', ' ' );
		if ( string.IsNullOrWhiteSpace( result ) )
			return ObjWriter.DefaultMaterialName( slot );

		return result;
	}
}
