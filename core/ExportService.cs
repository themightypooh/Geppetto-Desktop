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
	Glb,
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
		bool hasSkinning = (session.Studio?.Rig is { Count: > 0 } && (session.Studio.BodyBoneMap.Count > 0 || session.Studio.Bodies.Any( b => HasUsableSkin( b.Mesh ) ))) ||
			(session.FbxSource?.Result?.Weights is { Count: > 0 } && session.FbxSource.Result.Weights.Any( w => w is not null && w.Any( list => list is not null && list.Any( e => e.Weight > 0f ) ) )) ||
			(session.FbxSource?.Result?.Pieces is not null && session.FbxSource.Result.Pieces.Any( p => HasUsableSkin( p.Mesh ) ));
		bool hasAnimation = session.Clip is not null || !string.IsNullOrWhiteSpace( session.ClipPath );
		// Morphs live on the meshes, not the session: a Studio body or an FBX piece
		// with a valid morph (named, deltas matching the vertex count).
		bool hasMorphs = (session.Studio is not null && session.Studio.Bodies.Any( b => b.Mesh is not null && b.Mesh.HasMorphs ))
			|| (session.FbxSource?.Result?.Pieces is not null && session.FbxSource.Result.Pieces.Any( p => p.Mesh is not null && p.Mesh.HasMorphs ));

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

			case ExportFormat.Glb:
			{
				// glTF 2.0, metres, Y-up. The kernel is inches, Z-up, so geometry is
				// converted on write (× 0.0254, Z-up → Y-up) and converted back on
				// import. Textures embed as PNG when one can be produced (paint
				// canvases, or a descriptor binding resolving to a PNG file); anything
				// else falls back to the descriptor's factors, which the report states
				// plainly. BindPose + skinning or morphs takes the animated path
				// (bone nodes, one shared skin, morph targets, one animation per
				// session clip); anything else is static.
				unitsAndAxes = "glTF 2.0: metres, Y-up (kernel inches × 0.0254; Z-up → Y-up)";
				if ( !hasGeometry )
				{
					canExport = false;
					primaryError = "Session contains no geometry to export as GLB.";
					errors.Add( primaryError );
				}
				else
				{
					preservesGeometry = true;
					preservesTangents = true;
					preserved.Add( "Polygonal mesh geometry (vertices, corner normals, computed tangents, UV coordinates, faces)" );
					preserved.Add( "Body and material names" );
					preserved.Add( "Vertex colours, when present" );
				}

				if ( request.MaterialPolicy != MaterialTexturePolicy.None )
				{
					preservesTextures = true;
					preserved.Add( "PBR textures embedded as PNG when one can be produced (paint canvases; descriptor bindings resolving to PNG files)" );
				}
				else
				{
					lost.Add( "Material textures (suppressed by policy; factors only)" );
				}

				// The animated path needs a bind pose and something to animate: a
				// rigged part (skinning with a skeleton) or a morph. The writer
				// decides from the actual parts; this report estimates the same
				// decision from session flags, so the two agree except when the
				// weights turn out unusable (e.g. every FBX vertex unweighted),
				// in which case the export falls back to static and the skin is lost.
				bool animatedGlb = request.PoseMode == PoseMode.BindPose && ((hasSkeleton && hasSkinning) || hasMorphs);
				if ( animatedGlb )
				{
					if ( hasSkeleton )
					{
						preservesRig = true;
						preserved.Add( "Skeletal hierarchy and bone nodes (bind pose)" );
					}
					if ( hasSkinning )
					{
						preservesSkinning = true;
						preserved.Add( "Vertex skinning weights and bone bindings (one shared skin, top 4 weights per vertex)" );
					}
					if ( hasMorphs )
					{
						preservesMorphs = true;
						preserved.Add( "Morph targets and blendshapes (POSITION + NORMAL deltas, weights 0 at rest)" );
					}
					if ( hasAnimation )
					{
						preservesAnimation = true;
						preserved.Add( "Skeletal animation: the session clip as one glTF animation (bone TRS + morph weights, sampled at the clip frame rate)" );
						warnings.Add( "Part, camera and IK-goal tracks have no glTF representation and are not written; the clip's bone, IK-goal and limit tracks are folded into the sampled bone transforms." );
					}
				}
				else
				{
					if ( hasSkeleton )
					{
						lost.Add( "Skeletal hierarchy and bone nodes (GLB export is static; no skin is written)" );
					}

					if ( hasSkinning )
					{
						lost.Add( "Vertex skinning weights and bone bindings (static export writes no skin)" );
					}

					if ( hasMorphs )
					{
						lost.Add( "Morph targets and blendshapes (unsupported by static GLB export)" );
					}

					if ( hasAnimation )
					{
						lost.Add( "Skeletal animation tracks and keyframes (static export writes no animation)" );
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
								primaryError = "GLB cannot export skeletal animation. Explicitly set BakePosedMesh = true to bake this pose to a static mesh.";
								errors.Add( primaryError );
							}
						}
						else
						{
							preserved.Add( "Static rest / bind pose geometry" );
							warnings.Add( "Exporting rest / bind pose; animation keyframes will not be included in GLB output." );
						}
					}
					else if ( request.PoseMode == PoseMode.Posed && request.BakePosedMesh )
					{
						preserved.Add( "Current pose baked to static polygonal mesh" );
					}
				}

				if ( hasFbx )
				{
					warnings.Add( "FBX-source file textures embed only when they resolve to PNG files under the document directory; Survey .vmat remaps are not applied to GLB export." );
				}

				if ( !string.IsNullOrWhiteSpace( request.DestinationPath ) &&
					!request.DestinationPath.EndsWith( ".glb", StringComparison.OrdinalIgnoreCase ) )
				{
					warnings.Add( "Destination path does not end with .glb extension." );
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

		// Mesh Doctor pre-export gate (godlike-mesh-doctor-manual-retopo, Wave 1 P0):
		// a broken mesh no longer ships silently. Geometry formats only — RigAnim
		// exports no mesh. Blocks on the geometric hard fails (non-manifold topology,
		// flipped shells, degenerate/duplicate faces, measured self-intersections);
		// holes warn instead of blocking because open surfaces are legitimate here
		// (cloth, single-face fixtures, FBX parts) — see MeshDoctor for the rule.
		// Reference bodies (IsReference) are measured against, never shipped, so they
		// are skipped. Runs before any directory or file is created.
		if ( request.Format != ExportFormat.RigAnim )
		{
			var gateMeshes = new List<KeyValuePair<string, PolyMesh>>();
			if ( request.Session.Studio is not null )
			{
				foreach ( var body in request.Session.Studio.Bodies )
				{
					if ( body is null || body.IsReference || !body.Visible || body.Mesh is null || body.Mesh.FaceCount == 0 ) continue;
					gateMeshes.Add( new KeyValuePair<string, PolyMesh>( body.Name ?? body.Id ?? "body", body.Mesh ) );
				}
			}
			if ( request.Session.FbxSource?.Result?.Pieces is not null )
			{
				foreach ( var piece in request.Session.FbxSource.Result.Pieces )
				{
					if ( piece.Mesh is null || piece.Mesh.FaceCount == 0 ) continue;
					gateMeshes.Add( new KeyValuePair<string, PolyMesh>( piece.Name ?? "piece", piece.Mesh ) );
				}
			}
			var gate = MeshDoctor.PreExportCheck( gateMeshes );
			if ( gate.Block )
			{
				return new ExportResult(
					Success: false,
					DestinationPath: request.DestinationPath,
					CapabilityReport: capabilityReport,
					ErrorMessage: "Mesh Doctor pre-export gate blocked this export:\n" + gate.Summary,
					ExportedRevision: capturedRevision
				);
			}
			if ( gate.Warn )
			{
				capabilityReport = capabilityReport with { Warnings = capabilityReport.Warnings.Concat( new[] { "Mesh Doctor pre-export warnings:\n" + gate.Summary } ).ToList() };
			}
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
				case ExportFormat.Glb:
					generatedFiles = ExportGlbStaged( request, stagingDir, fullDestPath, cancellationToken );
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
				if ( session.Studio?.MaterialLooks.GetValueOrDefault( slot ) is { } look )
				{
					// The slot's own look, linear like the palette's entries.
					var d = MaterialPalette.ForLook( matName, look );
					entry = new MaterialPalette.Entry( matName, d.BaseColorFactor.x, d.BaseColorFactor.y, d.BaseColorFactor.z, d.RoughnessFactor, d.MetallicFactor );
				}

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

	/// <summary>Portable static GLB: one file, staged and atomically moved like the other
	/// formats. Geometry comes from <see cref="ExtractPieces"/> (so Posed + BakePosedMesh
	/// bakes the pose first, exactly as OBJ does); materials are assembled per body slot
	/// from paint canvases and descriptor bindings, then written by <see cref="GlbExport"/>.</summary>
	List<string> ExportGlbStaged( ExportRequest request, string stagingDir, string finalDestPath, CancellationToken cancellationToken )
	{
		var session = request.Session;
		string stagedGlbPath = Path.Combine( stagingDir, Path.GetFileName( finalDestPath ) );

		var pieces = ExtractPieces( session, request.PoseMode, request.PlayheadTime );
		if ( pieces.Count == 0 )
		{
			throw new InvalidOperationException( "No mesh pieces could be extracted from retained source data." );
		}

		cancellationToken.ThrowIfCancellationRequested();

		// Slot names are glTF JSON strings, so they are used raw (trimmed) — the OBJ
		// path's SanitizeMaterialName exists because .mtl names must survive a text
		// format; it would mangle names here for no reason.
		Func<int, string> slotName = slot =>
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
			return string.IsNullOrWhiteSpace( raw ) ? ObjWriter.DefaultMaterialName( slot ) : raw.Trim();
		};

		var resolver = BuildGlbMaterialResolver( session );
		bool embedTextures = request.MaterialPolicy != MaterialTexturePolicy.None;

		// The skeleton for the animated path: the Studio rig, else the FBX skeleton.
		Skeleton? skeleton = null;
		if ( session.Studio?.Rig is { Count: > 0 } studioRig ) skeleton = studioRig;
		else if ( session.FbxSource?.Skeleton is { Count: > 0 } fbxRig ) skeleton = fbxRig;

		List<GlbPart> BuildParts( List<ObjReader.ObjPiece> source )
		{
			var built = new List<GlbPart>();
			foreach ( var piece in source )
			{
				if ( piece.Mesh is null || piece.Mesh.FaceCount == 0 ) continue;
				foreach ( var (name, mesh, slot) in SplitPieceBySlot( piece, slotName ) )
				{
					var material = BuildGlbMaterial( mesh, slot, slotName( slot ), resolver, embedTextures );
					built.Add( new GlbPart( name, mesh, material ) );
				}
			}
			return built;
		}

		var parts = BuildParts( pieces );
		if ( parts.Count == 0 )
		{
			throw new InvalidOperationException( "No mesh pieces with faces could be extracted from retained source data." );
		}

		cancellationToken.ThrowIfCancellationRequested();

		// Animated path (BindPose only): a part that actually skins something, or
		// a part with a morph. A skin of all-empty rows skins nothing — see
		// HasUsableSkin — and must not trigger the animated path. Posed keeps the
		// baked static path: an animation is a clip, not a baked pose.
		bool animated = request.PoseMode == PoseMode.BindPose
			&& (parts.Any( p => HasUsableSkin( p.Mesh ) ) || parts.Any( p => p.Mesh.HasMorphs ));
		if ( request.PoseMode == PoseMode.BindPose && session.Studio is not null && skeleton is not null )
		{
			// The Studio pieces above carry each body's own skin, which is often
			// absent and never the smoothed, weight-painted binding; the
			// export-grade skin is bound on the merged mesh instead. Prefer the
			// merged pieces whenever they animate; otherwise keep the pieces
			// above (and whatever `animated` they gave).
			var animatedPieces = BuildAnimatedStudioPieces( session.Studio, skeleton );
			if ( animatedPieces is not null )
			{
				var animatedParts = BuildParts( animatedPieces );
				if ( animatedParts.Any( p => HasUsableSkin( p.Mesh ) ) || animatedParts.Any( p => p.Mesh.HasMorphs ) )
				{
					parts = animatedParts;
					animated = true;
				}
			}
		}

		byte[] glb;
		if ( animated )
		{
			List<GlbClip>? clips = session.Clip is not null
				? new List<GlbClip> { new GlbClip( ClipName( session ), session.Clip ) }
				: null;
			glb = GlbExport.WriteAnimated( parts, skeleton, clips );
		}
		else
		{
			glb = GlbExport.Write( parts );
		}
		File.WriteAllBytes( stagedGlbPath, glb );
		return new List<string> { stagedGlbPath };
	}

	/// <summary>Whether a mesh actually carries skinning: a skin whose rows contain at
	/// least one positive weight. A skin of all-empty rows passes
	/// <see cref="PolyMesh.IsRigged"/> but skins nothing, and must not make the
	/// capability report or the animated path claim otherwise.</summary>
	static bool HasUsableSkin( PolyMesh? mesh )
	{
		if ( mesh?.Skin is null || mesh.Skin.Count != mesh.Positions.Count ) return false;
		return mesh.Skin.Vertices.Any( row => row is not null && row.Any( w => w.Weight > 0f ) );
	}

	/// <summary>Clip name for the session clip: the clip file's name without extension,
	/// or "clip" when the clip came from memory (no path) or the path has no stem.</summary>
	static string ClipName( DocumentSession session )
	{
		if ( !string.IsNullOrWhiteSpace( session.ClipPath ) )
		{
			string stem = Path.GetFileNameWithoutExtension( session.ClipPath );
			if ( !string.IsNullOrWhiteSpace( stem ) ) return stem;
		}
		return "clip";
	}

	/// <summary>Studio pieces for the animated GLB path: each visible body's own mesh
	/// (morphs, paint, vertex colours intact) with the export-grade skin sliced from
	/// one <see cref="CpuMesh.BindLikeExport"/> run on the merged mesh — the same
	/// binding the viewport shows (BindBodies, then smoothing, then weight paint).
	/// Returns null when there is no merged mesh to bind (no visible bodies).
	/// Reference (wearer) bodies are excluded by
	/// <see cref="PartStudio.ToMeshWithBodies"/> — they are neither shipped nor
	/// weighted — so the caller keeps the <see cref="ExtractPieces"/> pieces for the
	/// static path, which does include them.</summary>
	static List<ObjReader.ObjPiece>? BuildAnimatedStudioPieces( PartStudio studio, Skeleton skeleton )
	{
		var (merged, ranges) = studio.ToMeshWithBodies( b => b.Visible && b.Mesh is not null && b.Mesh.VertexCount > 0 );
		if ( merged.VertexCount == 0 || ranges.Count == 0 ) return null;
		var weights = CpuMesh.BindLikeExport( studio, merged, ranges );
		if ( weights is null || weights.Count != merged.VertexCount )
			throw new InvalidOperationException( "Could not compute export skin weights for the animated GLB export." );
		var bodiesById = studio.Bodies.ToDictionary( b => b.Id, StringComparer.Ordinal );
		var result = new List<ObjReader.ObjPiece>();
		foreach ( var range in ranges )
		{
			if ( range.Count == 0 ) continue;
			if ( !bodiesById.TryGetValue( range.BodyId, out var body ) ) continue;
			if ( body.Mesh is null || body.Mesh.VertexCount != range.Count ) continue;
			var mesh = body.Mesh.Clone();
			var skin = new SkinWeights( mesh.VertexCount );
			for ( int v = 0; v < mesh.VertexCount; v++ ) skin[v] = weights.Vertices[range.Start + v];
			mesh.Skin = skin;
			string pieceName = string.IsNullOrWhiteSpace( body.Name ) ? $"body_{result.Count}" : body.Name;
			result.Add( new ObjReader.ObjPiece( pieceName, mesh ) );
		}
		return result.Count == 0 ? null : result;
	}

	/// <summary>Resolver for GLB material assembly: the document directory as project root,
	/// plus the Studio's own per-slot looks (the same overrides the snapshot path applies).
	/// Survey .vmat remaps are deliberately NOT applied here — v1 limitation, stated in the
	/// capability report — because they need the survey/vmdl map machinery, not a bare resolver.</summary>
	static MaterialResolver BuildGlbMaterialResolver( DocumentSession session )
	{
		string sourceDir = Path.GetDirectoryName( Path.GetFullPath( session.SourcePath ) ) ?? Directory.GetCurrentDirectory();
		var resolver = new MaterialResolver( sourceDir, session.DocumentId );
		if ( session.Studio is not null )
		{
			foreach ( var (slot, look) in session.Studio.MaterialLooks )
			{
				if ( look is null ) continue;
				resolver.SetOverride( slot, MaterialPalette.ForLook( session.Studio.NameForSlot( slot ), look ) );
			}
		}
		return resolver;
	}

	/// <summary>Split a piece into one mesh per material slot. glTF assigns a material per
	/// primitive, and a body part's faces can carry several slots; a per-slot mesh keeps the
	/// part name (suffixed with the slot name when it splits) and the paint canvas shared —
	/// <see cref="PaintTextureSet.Images"/> only reads it. Skin and morphs are carried through
	/// the vertex remap, so a split part stays rigged and keeps its shape keys; a morph is
	/// kept for a slot only when at least one of its remapped deltas is non-zero.</summary>
	static List<(string Name, PolyMesh Mesh, int Slot)> SplitPieceBySlot( ObjReader.ObjPiece piece, Func<int, string> slotName )
	{
		var slots = new List<int>();
		foreach ( var face in piece.Mesh.Faces )
			if ( !slots.Contains( face.Material ) ) slots.Add( face.Material );
		if ( slots.Count <= 1 )
			return new List<(string, PolyMesh, int)> { (piece.Name, piece.Mesh, slots.Count == 0 ? 0 : slots[0]) };

		var result = new List<(string, PolyMesh, int)>();
		foreach ( int slot in slots )
		{
			var sub = new PolyMesh();
			var remap = new Dictionary<int, int>();
			foreach ( var face in piece.Mesh.Faces )
			{
				if ( face.Material != slot ) continue;
				var newIndices = new int[face.Indices.Length];
				for ( int c = 0; c < face.Indices.Length; c++ )
				{
					int old = face.Indices[c];
					if ( !remap.TryGetValue( old, out int mapped ) )
					{
						mapped = sub.Positions.Count;
						remap[old] = mapped;
						sub.Positions.Add( piece.Mesh.Positions[old] );
					}
					newIndices[c] = mapped;
				}
				sub.AddFace( newIndices, (Vec2[])face.UVs.Clone(), slot );
			}
			if ( piece.Mesh.VertexColors is not null )
			{
				var colors = new Vec4[sub.Positions.Count];
				foreach ( var (old, mapped) in remap ) colors[mapped] = piece.Mesh.VertexColors[old];
				sub.VertexColors = colors;
			}
			if ( piece.Mesh.Skin is not null && piece.Mesh.Skin.Count == piece.Mesh.VertexCount )
			{
				var skin = new SkinWeights( sub.Positions.Count );
				foreach ( var (old, mapped) in remap ) skin[mapped] = piece.Mesh.Skin[old];
				sub.Skin = skin;
			}
			foreach ( var morph in piece.Mesh.ValidMorphs )
			{
				var deltas = new Vec3[sub.Positions.Count];
				bool any = false;
				foreach ( var (old, mapped) in remap )
				{
					deltas[mapped] = morph.Deltas[old];
					if ( morph.Deltas[old].LengthSquared > 0f ) any = true;
				}
				if ( any ) sub.Morphs.Add( new MeshMorph { Name = morph.Name, Deltas = deltas, Driver = morph.Driver } );
			}
			sub.Paint = piece.Mesh.Paint;
			result.Add( ($"{piece.Name}:{slotName( slot )}", sub, slot) );
		}
		return result;
	}

	/// <summary>Assemble one glTF material for a slot. Precedence mirrors the snapshot path:
	/// paint wins over descriptor bindings, and a channel with an embedded texture takes
	/// factor 1 (the image carries the value). Paint images come from
	/// <see cref="PaintTextureSet.Images"/>; descriptor bindings embed only when they
	/// resolve to an existing PNG file — anything else keeps the descriptor's factors, as
	/// the capability report states. The normal map's G channel is flipped (DirectX green
	/// in the kernel → OpenGL green in glTF); metallic-roughness is packed R=255, G=rough,
	/// B=metal, matching the kernel's packing. Paint emit becomes the emissive texture
	/// (factor 1); paint opacity stamps into the base colour's alpha and sets alphaMode
	/// BLEND; paint AO becomes the occlusion texture.</summary>
	static GlbMaterial BuildGlbMaterial( PolyMesh mesh, int slot, string slotName, MaterialResolver resolver, bool embedTextures )
	{
		var descriptor = resolver.ResolveSlot( slot, slotName, validateTextures: true ).Descriptor;
		Vec4 baseFactor = descriptor.BaseColorFactor;
		float metallic = descriptor.MetallicFactor;
		float roughness = descriptor.RoughnessFactor;
		Vec3 emission = descriptor.EmissionFactor;

		GlbImage? baseColor = null, metallicRoughness = null, normal = null, occlusion = null, emissive = null;
		byte[]? roughRgba = null, metalRgba = null;
		byte[]? opacityRgba = null;
		int rmWidth = 0, rmHeight = 0, opWidth = 0, opHeight = 0;
		bool alphaBlend = false;

		if ( embedTextures && mesh.Paint is not null )
		{
			foreach ( var image in PaintTextureSet.Images( mesh.Paint ) )
			{
				switch ( image.Suffix )
				{
					case "color":
						baseColor = new GlbImage( $"{slotName}_basecolor", PngWriter.ToBytesRgba( image.Rgba, image.Width, image.Height ), image.Width, image.Height );
						baseFactor = new Vec4( 1f, 1f, 1f, 1f );
						break;
					case "rough":
						roughRgba = image.Rgba;
						rmWidth = image.Width; rmHeight = image.Height;
						roughness = 1f;
						break;
					case "metal":
						metalRgba = image.Rgba;
						rmWidth = image.Width; rmHeight = image.Height;
						metallic = 1f;
						break;
					case "normal":
						normal = new GlbImage( $"{slotName}_normal", PngWriter.ToBytesRgba( FlipNormalGreen( image.Rgba ), image.Width, image.Height ), image.Width, image.Height );
						break;
					case "ao":
						occlusion = new GlbImage( $"{slotName}_occlusion", PngWriter.ToBytesRgba( image.Rgba, image.Width, image.Height ), image.Width, image.Height );
						break;
					case "emit":
						emissive = new GlbImage( $"{slotName}_emissive", PngWriter.ToBytesRgba( image.Rgba, image.Width, image.Height ), image.Width, image.Height );
						emission = new Vec3( 1f, 1f, 1f );
						break;
					case "opacity":
						opacityRgba = image.Rgba;
						opWidth = image.Width; opHeight = image.Height;
						break;
				}
			}
		}

		if ( embedTextures && resolver.PathResolver is not null )
		{
			if ( baseColor is null )
				baseColor = ResolvePngBinding( descriptor.GetChannel( MaterialChannel.BaseColor ), resolver, $"{slotName}_basecolor", ref baseFactor, isBaseColor: true );
			if ( normal is null )
				normal = ResolveNormalBinding( descriptor.GetChannel( MaterialChannel.Normal ), resolver, $"{slotName}_normal" );
			if ( occlusion is null )
				occlusion = ResolvePngBinding( descriptor.GetChannel( MaterialChannel.AmbientOcclusion ), resolver, $"{slotName}_occlusion", ref baseFactor, isBaseColor: false );
			if ( roughRgba is null )
			{
				(roughRgba, rmWidth, rmHeight) = DecodePngBinding( descriptor.GetChannel( MaterialChannel.Roughness ), resolver );
				if ( roughRgba is not null ) roughness = 1f;
			}
			if ( metalRgba is null )
			{
				(metalRgba, rmWidth, rmHeight) = DecodePngBinding( descriptor.GetChannel( MaterialChannel.Metallic ), resolver );
				if ( metalRgba is not null ) metallic = 1f;
			}
		}

		// Opacity stamps into the base colour's alpha (Mobile's rule): the opacity
		// image is grey, its value scales the albedo alpha per pixel. It needs a base
		// colour image of the same size; without one (or with a size mismatch, or an
		// unreadable base image) there is nowhere to stamp it, so it is dropped
		// rather than invented.
		if ( opacityRgba is not null && baseColor is not null )
		{
			try
			{
				var (baseRgba, baseWidth, baseHeight) = PngReader.Read( baseColor.PngBytes );
				if ( baseWidth == opWidth && baseHeight == opHeight && baseRgba.Length == opacityRgba.Length )
				{
					var stamped = (byte[])baseRgba.Clone();
					for ( int i = 0; i < opWidth * opHeight; i++ )
						stamped[i * 4 + 3] = (byte)( stamped[i * 4 + 3] * opacityRgba[i * 4] / 255 );
					baseColor = new GlbImage( baseColor.Name, PngWriter.ToBytesRgba( stamped, baseWidth, baseHeight ), baseWidth, baseHeight );
					alphaBlend = true;
				}
			}
			catch
			{
				// Unreadable base image: keep it un-stamped, like every other
				// texture fallback in this method.
			}
		}

		if ( roughRgba is not null || metalRgba is not null )
		{
			metallicRoughness = new GlbImage( $"{slotName}_metallicroughness",
				PngWriter.ToBytesRgba( PackMetallicRoughness( roughRgba, metalRgba, rmWidth, rmHeight, roughness, metallic ), rmWidth, rmHeight ),
				rmWidth, rmHeight );
		}

		return new GlbMaterial( slotName, baseFactor, metallic, roughness, emission, baseColor, metallicRoughness, normal, occlusion, emissive, alphaBlend );
	}

	/// <summary>Embed a descriptor texture binding as-is when it resolves to an existing
	/// PNG file. Returns null for anything else (missing file, non-PNG, no binding): the
	/// caller keeps the descriptor's factors instead. <paramref name="baseFactor"/> is set
	/// to white for a base-colour texture, matching the snapshot rule.</summary>
	static GlbImage? ResolvePngBinding( TextureBinding? binding, MaterialResolver resolver, string imageName, ref Vec4 baseFactor, bool isBaseColor )
	{
		if ( binding is null || resolver.PathResolver is null ) return null;
		var resolved = resolver.PathResolver.ResolveRead( binding.Reference, requireExists: false );
		if ( !resolved.Exists ) return null;
		if ( !resolved.CanonicalPath.EndsWith( ".png", StringComparison.OrdinalIgnoreCase ) ) return null;
		byte[] png = File.ReadAllBytes( resolved.CanonicalPath );
		try
		{
			var (_, width, height) = PngReader.Read( png );
			if ( isBaseColor ) baseFactor = new Vec4( 1f, 1f, 1f, 1f );
			return new GlbImage( imageName, png, width, height );
		}
		catch
		{
			return null;
		}
	}

	/// <summary>Normal bindings embed as-is for OpenGL green; a DirectX-green binding is
	/// decoded, flipped, and re-encoded, because glTF reads OpenGL green.</summary>
	static GlbImage? ResolveNormalBinding( TextureBinding? binding, MaterialResolver resolver, string imageName )
	{
		if ( binding is null || resolver.PathResolver is null ) return null;
		var resolved = resolver.PathResolver.ResolveRead( binding.Reference, requireExists: false );
		if ( !resolved.Exists ) return null;
		if ( !resolved.CanonicalPath.EndsWith( ".png", StringComparison.OrdinalIgnoreCase ) ) return null;
		byte[] png = File.ReadAllBytes( resolved.CanonicalPath );
		try
		{
			var (rgba, width, height) = PngReader.Read( png );
			if ( binding.NormalConvention == NormalMapConvention.DirectX )
				png = PngWriter.ToBytesRgba( FlipNormalGreen( rgba ), width, height );
			return new GlbImage( imageName, png, width, height );
		}
		catch
		{
			return null;
		}
	}

	/// <summary>Decode a descriptor binding to RGBA for packing (metallic-roughness needs
	/// pixels, not file bytes). Returns null when the binding cannot be used.</summary>
	static (byte[]? Rgba, int Width, int Height) DecodePngBinding( TextureBinding? binding, MaterialResolver resolver )
	{
		if ( binding is null || resolver.PathResolver is null ) return (null, 0, 0);
		var resolved = resolver.PathResolver.ResolveRead( binding.Reference, requireExists: false );
		if ( !resolved.Exists ) return (null, 0, 0);
		if ( !resolved.CanonicalPath.EndsWith( ".png", StringComparison.OrdinalIgnoreCase ) ) return (null, 0, 0);
		try
		{
			var (rgba, width, height) = PngReader.Read( File.ReadAllBytes( resolved.CanonicalPath ) );
			return (rgba, width, height);
		}
		catch
		{
			return (null, 0, 0);
		}
	}

	/// <summary>Flip a normal map's G channel: DirectX green (down) → OpenGL green (up).
	/// RGBA layout; only the G byte of each pixel changes.</summary>
	static byte[] FlipNormalGreen( byte[] rgba )
	{
		var flipped = (byte[])rgba.Clone();
		for ( int i = 0; i + 1 < flipped.Length; i += 4 )
			flipped[i + 1] = (byte)( 255 - flipped[i + 1] );
		return flipped;
	}

	/// <summary>Pack roughness + metalness into one metallic-roughness image: R = 255
	/// (occlusion-free), G = roughness, B = metalness. A missing channel is filled from
	/// its factor. The two maps share the paint canvas size when they come from paint;
	/// a lone map keeps its own size and the other channel is a flat fill.</summary>
	static byte[] PackMetallicRoughness( byte[]? roughRgba, byte[]? metalRgba, int width, int height, float roughnessFactor, float metallicFactor )
	{
		var packed = new byte[width * height * 4];
		byte roughFill = (byte)Math.Clamp( (int)( roughnessFactor * 255f ), 0, 255 );
		byte metalFill = (byte)Math.Clamp( (int)( metallicFactor * 255f ), 0, 255 );
		for ( int i = 0; i < width * height; i++ )
		{
			byte rough = roughRgba is not null && i * 4 < roughRgba.Length ? roughRgba[i * 4 + 1] : roughFill;
			byte metal = metalRgba is not null && i * 4 < metalRgba.Length ? metalRgba[i * 4 + 1] : metalFill;
			packed[i * 4] = 255;
			packed[i * 4 + 1] = rough;
			packed[i * 4 + 2] = metal;
			packed[i * 4 + 3] = 255;
		}
		return packed;
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
					// BindPose keeps the imported skin so the GLB animated path can
					// write it (and so the Posed branch above is not the only place
					// a skin exists). Resolve returns null when any vertex is
					// unweighted, so a partially-weighted piece exports unrigged
					// rather than half-skinned; the other formats ignore Skin.
					if ( hasRig && meshToExport.Skin is null )
					{
						var rawWeights = pi < fbx.Result.Weights.Count ? fbx.Result.Weights[pi] : null;
						if ( rawWeights is not null )
							meshToExport.Skin = FbxImport.Resolve( rawWeights, rig, null, out _ );
					}
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
