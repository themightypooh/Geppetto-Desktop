#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Batch export across engine presets (godlike File-Finish-Bake P2).
///
/// One call exports the same document once per preset into per-preset folders.
/// The fail-safety contract, which is the point of this service:
/// - Every preset exports into its own staging directory first.
/// - On success the staged files move into place with an atomic replace, so a
///   later export never leaves a half-written model file behind.
/// - On failure the staging directory is removed and the previous good files in
///   the destination are left completely untouched, with a readable error.
/// - A preset Desktop cannot write (USD, 3D print) is refused before any disk
///   work, and never produces a partial file.
///
/// Packed maps are written alongside the model file when a TextureMapSet is
/// supplied; without one the batch is model-only and the per-preset result
/// says so. The model export itself is delegated to an IExportRunner so tests
/// can substitute a fake; the default runner uses the real ExportService
/// (GLB) and FbxExportAdapter (FBX, static mesh/rig only, no animation
/// curves). USD / print presets never reach a runner.
/// </summary>
public sealed record PresetBatchResult(
	string PresetId,
	bool Success,
	IReadOnlyList<string> Files,
	string? ErrorMessage,
	IReadOnlyList<string> MapsWritten,
	bool MapsSkippedNoSource );

public sealed record BatchExportResult(
	bool OverallSuccess,
	IReadOnlyList<PresetBatchResult> Results,
	string OutputDirectory );

/// <summary>Runs one preset's model export into a staging directory.</summary>
public interface IExportRunner
{
	ExportResult Run( EngineExportPreset preset, DocumentSession session, string stagingDirectory );
}

/// <summary>Default runner: real ExportService for GLB, real FbxExportAdapter for FBX.</summary>
public sealed class ExportServiceRunner : IExportRunner
{
	readonly ExportService _exportService;

	public ExportServiceRunner( ExportService? exportService = null )
	{
		_exportService = exportService ?? new ExportService();
	}

	public ExportResult Run( EngineExportPreset preset, DocumentSession session, string stagingDirectory )
	{
		ArgumentNullException.ThrowIfNull( preset );
		ArgumentNullException.ThrowIfNull( session );
		if ( string.IsNullOrWhiteSpace( stagingDirectory ) )
			throw new ArgumentException( "Staging directory cannot be empty.", nameof( stagingDirectory ) );
		if ( !preset.Supported )
		{
			return new ExportResult( false, ErrorMessage:
				preset.UnsupportedReason ?? $"Preset '{preset.Id}' is not supported on Desktop." );
		}

		string modelName = EngineExportPresets.ModelFileNameFor( preset, ModelAssetName( session ) );
		string destination = Path.Combine( stagingDirectory, modelName );
		return preset.Container switch
		{
			PresetContainer.Glb => _exportService.Export( new ExportRequest(
				session, ExportFormat.Glb, destination,
				PoseMode.BindPose, MaterialTexturePolicy.CopyTextures, OverwritePolicy.Overwrite ) ),
			PresetContainer.Fbx => FbxExportAdapter.Export(
				session, destination, OverwritePolicy.Overwrite ),
			PresetContainer.Obj => _exportService.Export( new ExportRequest(
				session, ExportFormat.Obj, destination,
				PoseMode.BindPose, MaterialTexturePolicy.CopyTextures, OverwritePolicy.Overwrite ) ),
			_ => new ExportResult( false, ErrorMessage:
				$"Preset '{preset.Id}' uses container '{preset.Container}', which has no Desktop writer." )
		};
	}

	static string ModelAssetName( DocumentSession session )
	{
		string? source = session.SourcePath;
		if ( !string.IsNullOrWhiteSpace( source ) )
		{
			string name = Path.GetFileNameWithoutExtension( source );
			if ( !string.IsNullOrWhiteSpace( name ) )
				return name;
		}
		return string.IsNullOrWhiteSpace( session.DocumentId ) ? "Asset" : session.DocumentId;
	}
}

public sealed class ExportBatchService
{
	readonly IFileSystem _fs;

	public ExportBatchService( IFileSystem? fileSystem = null )
	{
		_fs = fileSystem ?? PhysicalFileSystem.Instance;
	}

	/// <summary>
	/// Exports one document once per preset. Presets that fail do not disturb the
	/// destination: their staging directory is deleted and previously exported
	/// good files stay exactly as they were.
	/// </summary>
	public BatchExportResult ExportAll(
		DocumentSession session,
		IReadOnlyList<EngineExportPreset> presets,
		string outputDirectory,
		TextureMapSet? maps = null,
		NormalMapConvention sourceNormalConvention = NormalMapConvention.OpenGL,
		IExportRunner? runner = null )
	{
		ArgumentNullException.ThrowIfNull( session );
		ArgumentNullException.ThrowIfNull( presets );
		if ( string.IsNullOrWhiteSpace( outputDirectory ) )
			throw new ArgumentException( "Output directory cannot be empty.", nameof( outputDirectory ) );

		runner ??= new ExportServiceRunner();
		var results = new List<PresetBatchResult>();
		string assetName = RunnerAssetName( session );

		foreach ( var preset in presets )
		{
			results.Add( ExportOne( session, preset, outputDirectory, assetName, maps, sourceNormalConvention, runner ) );
		}

		return new BatchExportResult( results.Count > 0 && results.All( r => r.Success ), results, outputDirectory );
	}

	PresetBatchResult ExportOne(
		DocumentSession session,
		EngineExportPreset preset,
		string outputDirectory,
		string assetName,
		TextureMapSet? maps,
		NormalMapConvention sourceNormalConvention,
		IExportRunner runner )
	{
		string presetDir = Path.Combine( outputDirectory, preset.Id );
		string stagingDir = Path.Combine( outputDirectory, ".staging", preset.Id + "-" + Guid.NewGuid().ToString( "N" ) );
		var files = new List<string>();
		var mapsWritten = new List<string>();

		try
		{
			if ( !preset.Supported )
			{
				return new PresetBatchResult( preset.Id, false, files,
					preset.UnsupportedReason ?? $"Preset '{preset.Id}' is not supported on Desktop.",
					mapsWritten, MapsSkippedNoSource: maps == null );
			}

			_fs.CreateDirectory( stagingDir );

			// 1. Pack the preset's maps into staging (when a source set was supplied).
			bool mapsSkipped = maps == null;
			if ( maps != null )
			{
				var pack = EngineExportPresets.Pack( preset, maps, sourceNormalConvention );
				if ( !pack.Success )
				{
					return FailAndCleanStaging( preset, stagingDir,
						"Map packing failed for preset '" + preset.Id + "': " + string.Join( "; ", pack.Errors ),
						mapsSkipped: false );
				}
				foreach ( var packedMap in pack.PackedMaps )
				{
					string mapFile = Path.Combine( stagingDir,
						EngineExportPresets.FileNameFor( preset, assetName, packedMap.Spec ) );
					using ( var stream = _fs.OpenWrite( mapFile ) )
						stream.Write( packedMap.PngBytes, 0, packedMap.PngBytes.Length );
					mapsWritten.Add( mapFile );
				}
			}

			// 2. Export the model into staging.
			ExportResult exported;
			try
			{
				exported = runner.Run( preset, session, stagingDir );
			}
			catch ( Exception ex )
			{
				return FailAndCleanStaging( preset, stagingDir,
					"Model export failed for preset '" + preset.Id + "': " + ex.Message,
					mapsSkipped );
			}
			if ( !exported.Success )
			{
				return FailAndCleanStaging( preset, stagingDir,
					"Model export failed for preset '" + preset.Id + "': " + ( exported.ErrorMessage ?? "unknown error" ),
					mapsSkipped );
			}

			// 3. Move staged files into place with atomic replace. Anything already
			// in the destination is only replaced after the whole preset succeeded.
			_fs.CreateDirectory( presetDir );
			var staged = new List<string>( _fs.EnumerateFiles( stagingDir, "*" ) );
			foreach ( var stagedFile in staged )
			{
				string finalPath = Path.Combine( presetDir, Path.GetFileName( stagedFile ) );
				_fs.ReplaceFile( stagedFile, finalPath );
				files.Add( finalPath );
			}
			DeleteStaging( stagingDir );
			// Report map files by their final paths, in pack order.
			var finalMaps = mapsWritten
				.Select( stagedMap => Path.Combine( presetDir, Path.GetFileName( stagedMap ) ) )
				.ToList();
			return new PresetBatchResult( preset.Id, true, files, null, finalMaps, mapsSkipped );
		}
		catch ( Exception ex )
		{
			return FailAndCleanStaging( preset, stagingDir,
				"Batch export failed for preset '" + preset.Id + "': " + ex.Message,
				maps == null );
		}
	}

	PresetBatchResult FailAndCleanStaging(
		EngineExportPreset preset,
		string stagingDir,
		string error,
		bool mapsSkipped )
	{
		DeleteStaging( stagingDir );
		return new PresetBatchResult( preset.Id, false, Array.Empty<string>(), error, Array.Empty<string>(), mapsSkipped );
	}

	void DeleteStaging( string stagingDir )
	{
		try
		{
			if ( !_fs.DirectoryExists( stagingDir ) )
				return;
			foreach ( var file in _fs.EnumerateFiles( stagingDir, "*" ) )
			{
				try { _fs.DeleteFile( file ); } catch { }
			}
			try { Directory.Delete( stagingDir, recursive: false ); } catch { }
		}
		catch { }
	}

	static string RunnerAssetName( DocumentSession session )
	{
		string? source = session.SourcePath;
		if ( !string.IsNullOrWhiteSpace( source ) )
		{
			string name = Path.GetFileNameWithoutExtension( source );
			if ( !string.IsNullOrWhiteSpace( name ) )
				return name;
		}
		return string.IsNullOrWhiteSpace( session.DocumentId ) ? "Asset" : session.DocumentId;
	}
}
