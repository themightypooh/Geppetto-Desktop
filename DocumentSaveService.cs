#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Effigy;

namespace Marionette.Desktop;

public enum SaveStatus
{
	Success,
	Unsupported,
	Cancelled,
	ValidationFailed,
	IoError
}

public sealed record SaveResult(
	SaveStatus Status,
	string? DestinationPath = null,
	long SavedRevision = 0,
	string? ErrorMessage = null
)
{
	public bool IsSuccess => Status == SaveStatus.Success;
}

public interface IFileSystem
{
	bool FileExists( string path );
	bool DirectoryExists( string path );
	void CreateDirectory( string path );
	void WriteAllText( string path, string content );
	string ReadAllText( string path );
	void DeleteFile( string path );
	void MoveFile( string sourceFile, string destinationFile, bool overwrite = false );
	void ReplaceFile( string sourceFile, string destinationFile, string? backupFile = null );
	Stream OpenWrite( string path );
	IEnumerable<string> EnumerateFiles( string path, string searchPattern );
}

public sealed class PhysicalFileSystem : IFileSystem
{
	public static PhysicalFileSystem Instance { get; } = new();

	public bool FileExists( string path ) => File.Exists( path );
	public bool DirectoryExists( string path ) => Directory.Exists( path );
	public void CreateDirectory( string path ) => Directory.CreateDirectory( path );
	public void WriteAllText( string path, string content ) => File.WriteAllText( path, content );
	public string ReadAllText( string path ) => File.ReadAllText( path );
	public void DeleteFile( string path ) => File.Delete( path );
	public void MoveFile( string sourceFile, string destinationFile, bool overwrite = false ) =>
		File.Move( sourceFile, destinationFile, overwrite );

	public void ReplaceFile( string sourceFile, string destinationFile, string? backupFile = null )
	{
		if ( OperatingSystem.IsWindows() )
		{
			File.Replace( sourceFile, destinationFile, backupFile );
		}
		else
		{
			// On Linux/POSIX, File.Move with overwrite: true uses atomic rename(2)
			if ( backupFile != null && File.Exists( destinationFile ) )
			{
				File.Copy( destinationFile, backupFile, overwrite: true );
			}
			File.Move( sourceFile, destinationFile, overwrite: true );
		}
	}

	public Stream OpenWrite( string path ) => new FileStream( path, FileMode.Create, FileAccess.Write, FileShare.None );

	public IEnumerable<string> EnumerateFiles( string path, string searchPattern ) =>
		Directory.Exists( path ) ? Directory.EnumerateFiles( path, searchPattern ) : Enumerable.Empty<string>();
}

public sealed record RecoverySnapshotMetadata
{
	public int SchemaVersion { get; init; } = 1;
	public string SnapshotId { get; init; } = "";
	public string DocumentId { get; init; } = "";
	public string SourcePath { get; init; } = "";
	public DocumentSourceKind SourceKind { get; init; }
	public long Revision { get; init; }
	public DateTimeOffset TimestampUtc { get; init; }
	public string PayloadFileName { get; init; } = "";
}

/// <summary>
/// Service providing fail-safe, atomic saving of Effigy and RigAnim documents.
/// Captures immutable revisions on caller thread; disk IO runs in worker tasks.
/// Enforces temp-file validation, atomic replacement, and recovery snapshotting.
/// </summary>
public sealed class DocumentSaveService
{
	readonly IFileSystem _fs;

	public DocumentSaveService( IFileSystem? fileSystem = null )
	{
		_fs = fileSystem ?? PhysicalFileSystem.Instance;
	}

	public SaveResult Save( DocumentSession session, string? destinationPath = null, AssetPathResolver? resolver = null )
	{
		return SaveAsync( session, destinationPath, resolver, CancellationToken.None ).GetAwaiter().GetResult();
	}

	public async Task<SaveResult> SaveAsync(
		DocumentSession session,
		string? destinationPath = null,
		AssetPathResolver? resolver = null,
		CancellationToken cancellationToken = default )
	{
		ArgumentNullException.ThrowIfNull( session );

		// 1. Capture immutable state on caller thread
		long capturedRevision = session.Revision;
		Guid capturedStateId = session.History.CurrentStateId;
		DocumentSourceKind kind = session.SourceKind;
		string originalPath = session.SourcePath;
		AssetReference? originalAssetRef = session.AssetReference;
		PartStudio? studio = session.Studio;
		RigAnimFile? clip = session.Clip;
		string? clipJson = session.ClipSourceJson;

		string targetPath = destinationPath ?? originalPath;
		bool isSaveAs = !string.Equals(
			Path.GetFullPath( targetPath ),
			Path.GetFullPath( originalPath ),
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
		);

		if ( cancellationToken.IsCancellationRequested )
		{
			return new SaveResult( SaveStatus.Cancelled, targetPath, capturedRevision, "Save cancelled before execution." );
		}

		// 2. Worker task for serialization, validation, and atomic disk IO
		try
		{
			return await Task.Run( () =>
			{
				if ( cancellationToken.IsCancellationRequested )
				{
					return new SaveResult( SaveStatus.Cancelled, targetPath, capturedRevision, "Save cancelled before execution." );
				}

			// Validate format & capability
			string ext = Path.GetExtension( targetPath ).ToLowerInvariant();

			if ( kind == DocumentSourceKind.Fbx && ext != ".riganim" )
			{
				return new SaveResult(
					SaveStatus.Unsupported,
					targetPath,
					capturedRevision,
					"Authoring or saving imported FBX models is unsupported. External FBX sources are read-only."
				);
			}

			string serializedText;
			if ( ext == ".effigy" )
			{
				if ( studio == null )
				{
					return new SaveResult( SaveStatus.Unsupported, targetPath, capturedRevision, "Session has no PartStudio to save." );
				}

				try
				{
					serializedText = StudioDocument.Write( studio );
				}
				catch ( Exception ex )
				{
					return new SaveResult( SaveStatus.IoError, targetPath, capturedRevision, $"Failed to serialize studio document: {ex.Message}" );
				}

				// Validate / reparse before touching disk destination
				try
				{
					var reparsed = StudioDocument.Read( serializedText );
					if ( reparsed == null )
					{
						return new SaveResult( SaveStatus.ValidationFailed, targetPath, capturedRevision, "Reparsing produced a null studio." );
					}
				}
				catch ( Exception ex )
				{
					return new SaveResult( SaveStatus.ValidationFailed, targetPath, capturedRevision, $"Validation failed: {ex.Message}" );
				}
			}
			else if ( ext == ".riganim" )
			{
				if ( clip == null )
				{
					return new SaveResult( SaveStatus.Unsupported, targetPath, capturedRevision, "Session has no animation clip to save." );
				}

				try
				{
					// Use RigAnimWriter.Write(clip, clipJson) to preserve unknown fields/props/events/cameras
					serializedText = RigAnimWriter.Write( clip, clipJson );
				}
				catch ( Exception ex )
				{
					return new SaveResult( SaveStatus.IoError, targetPath, capturedRevision, $"Failed to serialize clip: {ex.Message}" );
				}

				// Validate / reparse before touching disk destination
				try
				{
					var reparsed = RigAnimFile.Read( serializedText );
					if ( reparsed == null )
					{
						return new SaveResult( SaveStatus.ValidationFailed, targetPath, capturedRevision, "Reparsing produced a null animation clip." );
					}
				}
				catch ( Exception ex )
				{
					return new SaveResult( SaveStatus.ValidationFailed, targetPath, capturedRevision, $"Validation failed: {ex.Message}" );
				}
			}
			else
			{
				return new SaveResult( SaveStatus.Unsupported, targetPath, capturedRevision, $"Unsupported file extension '{ext}'." );
			}

			if ( cancellationToken.IsCancellationRequested )
			{
				return new SaveResult( SaveStatus.Cancelled, targetPath, capturedRevision, "Save cancelled after serialization." );
			}

			// Atomic temp write
			string dir = Path.GetDirectoryName( Path.GetFullPath( targetPath ) ) ?? "";
			if ( string.IsNullOrWhiteSpace( dir ) ) dir = ".";

			try
			{
				if ( !_fs.DirectoryExists( dir ) )
				{
					_fs.CreateDirectory( dir );
				}
			}
			catch ( Exception ex )
			{
				return new SaveResult( SaveStatus.IoError, targetPath, capturedRevision, $"Cannot create directory '{dir}': {ex.Message}" );
			}

			string tempPath = Path.Combine( dir, $".{Path.GetFileName( targetPath )}.{Guid.NewGuid():N}.tmp" );

			try
			{
				using ( var stream = _fs.OpenWrite( tempPath ) )
				using ( var writer = new StreamWriter( stream, new UTF8Encoding( false ) ) )
				{
					writer.Write( serializedText );
					writer.Flush();
					stream.Flush();
				}
			}
			catch ( Exception ex )
			{
				TryCleanup( tempPath );
				return new SaveResult( SaveStatus.IoError, targetPath, capturedRevision, $"Write or flush failed: {ex.Message}" );
			}

			if ( cancellationToken.IsCancellationRequested )
			{
				TryCleanup( tempPath );
				return new SaveResult( SaveStatus.Cancelled, targetPath, capturedRevision, "Save cancelled before atomic replacement." );
			}

			// Atomic replacement / move
			try
			{
				if ( _fs.FileExists( targetPath ) )
				{
					_fs.ReplaceFile( tempPath, targetPath );
				}
				else
				{
					_fs.MoveFile( tempPath, targetPath, overwrite: false );
				}
			}
			catch ( Exception ex )
			{
				TryCleanup( tempPath );
				return new SaveResult( SaveStatus.IoError, targetPath, capturedRevision, $"Atomic replace failed: {ex.Message}" );
			}

			// 3. Post-save session updates
			if ( isSaveAs )
			{
				AssetReference? newAssetRef = null;
				if ( resolver != null && originalAssetRef != null )
				{
					try
					{
						// If within same canonical root, relocate relative reference
						if ( targetPath.StartsWith( resolver.CanonicalRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal ) )
						{
							string rel = Path.GetRelativePath( resolver.CanonicalRoot, targetPath ).Replace( '\\', '/' );
							newAssetRef = new AssetReference( originalAssetRef.Id, rel, AssetReferenceKind.ProjectRelative );
						}
						else
						{
							newAssetRef = new AssetReference( originalAssetRef.Id, Path.GetFullPath( targetPath ), AssetReferenceKind.ExternalReadOnly );
						}
					}
					catch
					{
						newAssetRef = null;
					}
				}
				session.Relocate( targetPath, newAssetRef );
			}

			// Mark saved revision and state identity
			session.MarkSaved( capturedRevision, capturedStateId );

			return new SaveResult( SaveStatus.Success, targetPath, capturedRevision );
		}, cancellationToken );
		}
		catch ( OperationCanceledException )
		{
			return new SaveResult( SaveStatus.Cancelled, targetPath, capturedRevision, "Save was cancelled." );
		}
	}

	public RecoverySnapshotMetadata CreateRecoverySnapshot( DocumentSession session, string recoveryDirectory )
	{
		ArgumentNullException.ThrowIfNull( session );
		if ( string.IsNullOrWhiteSpace( recoveryDirectory ) )
			throw new ArgumentException( "Recovery directory cannot be empty.", nameof( recoveryDirectory ) );

		long capturedRevision = session.Revision;
		string docId = session.DocumentId;
		string sourcePath = session.SourcePath;
		DocumentSourceKind kind = session.SourceKind;
		var now = DateTimeOffset.UtcNow;
		string snapshotId = $"{docId}_{capturedRevision}_{now.ToUnixTimeMilliseconds()}";

		if ( !_fs.DirectoryExists( recoveryDirectory ) )
		{
			_fs.CreateDirectory( recoveryDirectory );
		}

		// Prepare payload
		string payloadExt = kind == DocumentSourceKind.Effigy ? ".effigy" : (Path.GetExtension( sourcePath ) ?? ".dat");
		string payloadFileName = $"{snapshotId}.payload{payloadExt}";
		string payloadPath = Path.Combine( recoveryDirectory, payloadFileName );

		string payloadText = "";
		if ( kind == DocumentSourceKind.Effigy && session.Studio != null )
		{
			payloadText = StudioDocument.Write( session.Studio );
		}
		else if ( session.Clip != null )
		{
			payloadText = RigAnimWriter.Write( session.Clip, session.ClipSourceJson );
		}

		// Write payload atomically
		string tempPayload = Path.Combine( recoveryDirectory, $".{payloadFileName}.tmp" );
		using ( var stream = _fs.OpenWrite( tempPayload ) )
		using ( var writer = new StreamWriter( stream, new UTF8Encoding( false ) ) )
		{
			writer.Write( payloadText );
			writer.Flush();
			stream.Flush();
		}
		_fs.MoveFile( tempPayload, payloadPath, overwrite: true );

		var metadata = new RecoverySnapshotMetadata
		{
			SchemaVersion = 1,
			SnapshotId = snapshotId,
			DocumentId = docId,
			SourcePath = sourcePath,
			SourceKind = kind,
			Revision = capturedRevision,
			TimestampUtc = now,
			PayloadFileName = payloadFileName
		};

		string metaFileName = $"{snapshotId}.recovery.json";
		string metaPath = Path.Combine( recoveryDirectory, metaFileName );
		string tempMeta = Path.Combine( recoveryDirectory, $".{metaFileName}.tmp" );
		string metaJson = JsonSerializer.Serialize( metadata, new JsonSerializerOptions { WriteIndented = true } );

		using ( var stream = _fs.OpenWrite( tempMeta ) )
		using ( var writer = new StreamWriter( stream, new UTF8Encoding( false ) ) )
		{
			writer.Write( metaJson );
			writer.Flush();
			stream.Flush();
		}
		_fs.MoveFile( tempMeta, metaPath, overwrite: true );

		return metadata;
	}

	public IReadOnlyList<RecoverySnapshotMetadata> ListRecoverySnapshots( string recoveryDirectory )
	{
		if ( !_fs.DirectoryExists( recoveryDirectory ) )
			return Array.Empty<RecoverySnapshotMetadata>();

		var list = new List<RecoverySnapshotMetadata>();
		foreach ( var file in _fs.EnumerateFiles( recoveryDirectory, "*.recovery.json" ) )
		{
			try
			{
				string json = _fs.ReadAllText( file );
				var meta = JsonSerializer.Deserialize<RecoverySnapshotMetadata>( json );
				if ( meta != null ) list.Add( meta );
			}
			catch
			{
				// Corrupt or partial recovery file skipped
			}
		}
		return list.OrderByDescending( m => m.TimestampUtc ).ToList();
	}

	public (RecoverySnapshotMetadata Metadata, string Payload) ReadRecoverySnapshot( string recoveryDirectory, string snapshotId )
	{
		string metaPath = Path.Combine( recoveryDirectory, $"{snapshotId}.recovery.json" );
		if ( !_fs.FileExists( metaPath ) ) throw new FileNotFoundException( $"Recovery metadata not found: {metaPath}" );

		string metaJson = _fs.ReadAllText( metaPath );
		var meta = JsonSerializer.Deserialize<RecoverySnapshotMetadata>( metaJson )
			?? throw new InvalidDataException( "Invalid recovery metadata." );

		string payloadPath = Path.Combine( recoveryDirectory, meta.PayloadFileName );
		if ( !_fs.FileExists( payloadPath ) ) throw new FileNotFoundException( $"Recovery payload not found: {payloadPath}" );

		string payload = _fs.ReadAllText( payloadPath );
		return (meta, payload);
	}

	void TryCleanup( string path )
	{
		try
		{
			if ( _fs.FileExists( path ) ) _fs.DeleteFile( path );
		}
		catch { }
	}
}
