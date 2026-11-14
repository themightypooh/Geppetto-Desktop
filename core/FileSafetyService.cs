#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// File safety for the godlike File-Finish-Bake P2 ticket: incremental versioned
/// saves, crash-recovery orchestration, and external-reference relinking.
///
/// This builds on what already exists and does not duplicate it:
/// - Autosave every N minutes + recovery snapshots already live in AutosaveScheduler
///   (driven by DesktopPreferences.Autosave) and DocumentSaveService.
///   This service wires them into a launch-time recovery flow (discover, plan,
///   restore a real session) instead of re-implementing the timer or snapshots.
/// - Atomic saves already live in DocumentSaveService.Save (same-directory temp +
///   flush + atomic rename, failed saves never clear the dirty state).
///   Versioned saves reuse that path once per version file.
/// - Save-As relinking already lives in AssetPathResolver.RelocateReference.
///   RelinkMissing adds the missing-file search: find a moved file by name and
///   return a corrected reference, or report it still missing / ambiguous.
///
/// Versioning covers .effigy studio documents in this ticket. .riganim
/// versioning is a follow-on: the version file format and manifest are the
/// same shape, but the revert verification (StudioDocument round-trip) is
/// studio-specific and must not be faked for clips.
/// </summary>
public sealed record VersionEntry(
	int Number,
	string FileName,
	long Revision,
	DateTimeOffset TimestampUtc,
	string SourcePath );

public sealed record VersionManifest(
	string DocumentId,
	string BaseName,
	List<VersionEntry> Versions );

public sealed record VersionSaveResult(
	bool Success,
	int VersionNumber,
	string? Path,
	long SavedRevision,
	string? ErrorMessage );

public sealed record RevertResult(
	bool Success,
	int VersionNumber,
	string? CurrentPath,
	string? BackupPath,
	string? ErrorMessage );

public sealed record RestorePlan(
	RecoverySnapshotMetadata? Metadata,
	bool Found,
	string? Reason );

public enum RelinkOutcome
{
	Unchanged,
	Relinked,
	StillMissing,
	Ambiguous,
	Error
}

public sealed record RelinkResult(
	AssetReference Reference,
	RelinkOutcome Outcome,
	AssetReference? NewReference,
	IReadOnlyList<string> Candidates,
	string Reason );

public sealed class FileSafetyService
{
	readonly DocumentSaveService _saveService;
	readonly AutosaveScheduler _autosave;
	readonly IFileSystem _fs;

	public FileSafetyService(
		DocumentSaveService? saveService = null,
		IFileSystem? fileSystem = null,
		AutosaveScheduler? autosaveScheduler = null )
	{
		_fs = fileSystem ?? PhysicalFileSystem.Instance;
		_saveService = saveService ?? new DocumentSaveService( _fs );
		_autosave = autosaveScheduler ?? new AutosaveScheduler( _saveService, fileSystem: _fs );
	}

	public AutosaveScheduler Autosave => _autosave;

	// ---- Incremental versioned saves -------------------------------------

	/// <summary>
	/// Saves the session as the next incremental version, {base}_v001.effigy,
	/// {base}_v002.effigy, ... next to the project file, and records it in
	/// {base}.versions.json. Old versions past maxVersions are deleted, oldest
	/// first. The live document file is never touched by a version save.
	/// </summary>
	public VersionSaveResult SaveVersion( DocumentSession session, string? projectDirectory = null, int maxVersions = 10 )
	{
		ArgumentNullException.ThrowIfNull( session );
		if ( maxVersions < 1 )
			throw new ArgumentOutOfRangeException( nameof( maxVersions ), "Keep at least one version." );
		if ( session.SourceKind != DocumentSourceKind.Effigy )
		{
			return new VersionSaveResult( false, 0, null, session.Revision,
				"Versioned saves cover .effigy studio documents in this ticket; .riganim versioning is a follow-on." );
		}

		string baseName = BaseNameFor( session );
		string directory = ResolveDirectory( session, projectDirectory );
		_fs.CreateDirectory( directory );

		var manifest = LoadManifest( directory, baseName, session.DocumentId );
		int next = manifest.Versions.Count == 0 ? 1 : manifest.Versions.Max( v => v.Number ) + 1;
		string fileName = $"{baseName}_v{next:D3}.effigy";
		string versionPath = Path.Combine( directory, fileName );

		var saved = _saveService.Save( session, versionPath );
		if ( !saved.IsSuccess )
		{
			return new VersionSaveResult( false, next, versionPath, session.Revision,
				"Version save failed: " + ( saved.ErrorMessage ?? saved.Status.ToString() ) );
		}

		manifest.Versions.Add( new VersionEntry( next, fileName, saved.SavedRevision, DateTimeOffset.UtcNow, session.SourcePath ) );
		manifest.Versions.Sort( ( a, b ) => a.Number.CompareTo( b.Number ) );
		while ( manifest.Versions.Count > maxVersions )
		{
			var oldest = manifest.Versions[0];
			manifest.Versions.RemoveAt( 0 );
			try
			{
				string oldPath = Path.Combine( directory, oldest.FileName );
				if ( _fs.FileExists( oldPath ) )
					_fs.DeleteFile( oldPath );
			}
			catch { }
		}
		WriteManifest( directory, manifest );
		return new VersionSaveResult( true, next, versionPath, saved.SavedRevision, null );
	}

	public IReadOnlyList<VersionEntry> ListVersions( string projectDirectory, string baseName )
	{
		if ( string.IsNullOrWhiteSpace( projectDirectory ) || string.IsNullOrWhiteSpace( baseName ) )
			return Array.Empty<VersionEntry>();
		if ( !_fs.DirectoryExists( projectDirectory ) )
			return Array.Empty<VersionEntry>();
		var manifest = LoadManifest( projectDirectory, baseName, "" );
		return manifest.Versions.OrderBy( v => v.Number ).ToList();
	}

	/// <summary>
	/// Reverts the live document file to a saved version. The version file is
	/// verified by reading it back as a studio document first, then its content
	/// is written over the live file atomically with a .pre-revert.bak backup.
	/// The version file itself is kept, so the same version can be restored
	/// again. The live session is not mutated: reopen the document to load
	/// the reverted revision.
	/// </summary>
	public RevertResult RevertToVersion( string projectDirectory, string baseName, int versionNumber )
	{
		if ( string.IsNullOrWhiteSpace( projectDirectory ) )
			return new RevertResult( false, versionNumber, null, null, "Project directory cannot be empty." );
		if ( string.IsNullOrWhiteSpace( baseName ) )
			return new RevertResult( false, versionNumber, null, null, "Base name cannot be empty." );

		var versions = ListVersions( projectDirectory, baseName );
		var entry = versions.FirstOrDefault( v => v.Number == versionNumber );
		if ( entry == null )
			return new RevertResult( false, versionNumber, null, null, $"No saved version v{versionNumber:D3} for '{baseName}'." );

		string versionPath = Path.Combine( projectDirectory, entry.FileName );
		if ( !_fs.FileExists( versionPath ) )
			return new RevertResult( false, versionNumber, null, null, $"Version file is gone: {versionPath}" );

		try
		{
			var studio = StudioDocument.ReadFile( versionPath );
			studio.Rebuild();
		}
		catch ( Exception ex )
		{
			return new RevertResult( false, versionNumber, null, null,
				$"Version v{versionNumber:D3} does not read back as a studio document: {ex.Message}" );
		}

		string currentPath = Path.Combine( projectDirectory, baseName + ".effigy" );
		string backupPath = currentPath + ".pre-revert.bak";
		try
		{
			string content = _fs.ReadAllText( versionPath );
			string tempPath = currentPath + ".revert.tmp";
			_fs.WriteAllText( tempPath, content );
			_fs.ReplaceFile( tempPath, currentPath, backupPath );
			var check = StudioDocument.ReadFile( currentPath );
			check.Rebuild();
		}
		catch ( Exception ex )
		{
			return new RevertResult( false, versionNumber, currentPath, null,
				$"Revert failed while restoring the live file: {ex.Message}" );
		}
		return new RevertResult( true, versionNumber, currentPath, backupPath, null );
	}

	// ---- Crash recovery on launch ----------------------------------------

	/// <summary>All recovery snapshots in a directory, newest first.</summary>
	public IReadOnlyList<RecoverySnapshotMetadata> DiscoverRecoveries( string recoveryDirectory ) =>
		AutosaveScheduler.DiscoverRecoveries( recoveryDirectory, _fs );

	/// <summary>
	/// Picks the snapshot a launch-time recovery should offer: the newest
	/// snapshot for the document, or the newest overall when no document id
	/// is given. Found == false means there is nothing to recover.
	/// </summary>
	public RestorePlan PlanRestore( string recoveryDirectory, string? documentId = null )
	{
		var all = DiscoverRecoveries( recoveryDirectory );
		var candidates = string.IsNullOrWhiteSpace( documentId )
			? all
			: all.Where( m => string.Equals( m.DocumentId, documentId, StringComparison.Ordinal ) ).ToList();
		if ( candidates.Count == 0 )
		{
			return new RestorePlan( null, false,
				string.IsNullOrWhiteSpace( documentId )
					? "No recovery snapshots in " + recoveryDirectory + "."
					: "No recovery snapshots for document '" + documentId + "'." );
		}
		return new RestorePlan( candidates[0], true, null );
	}

	/// <summary>Reads one snapshot's payload. The payload is the saved document text.</summary>
	public (RecoverySnapshotMetadata Metadata, string Payload) ReadRestorePayload( string recoveryDirectory, string snapshotId ) =>
		_saveService.ReadRecoverySnapshot( recoveryDirectory, snapshotId );

	/// <summary>
	/// Restores a snapshot as a real, reopenable document: the payload is written
	/// to restoredPath (atomic temp + move) and a session is opened on it.
	/// The caller owns offering this at launch and deciding whether the restored
	/// file replaces the crashed original.
	/// </summary>
	public DocumentSession RestoreSnapshotToSession( string recoveryDirectory, string snapshotId, string restoredPath )
	{
		if ( string.IsNullOrWhiteSpace( restoredPath ) )
			throw new ArgumentException( "Restored path cannot be empty.", nameof( restoredPath ) );
		var (_, payload) = _saveService.ReadRecoverySnapshot( recoveryDirectory, snapshotId );
		string? directory = Path.GetDirectoryName( Path.GetFullPath( restoredPath ) );
		if ( !string.IsNullOrWhiteSpace( directory ) )
			_fs.CreateDirectory( directory );
		string tempPath = restoredPath + ".restore.tmp";
		_fs.WriteAllText( tempPath, payload );
		_fs.MoveFile( tempPath, restoredPath, overwrite: true );
		var studio = StudioDocument.ReadFile( restoredPath );
		studio.Rebuild();
		return DocumentSession.CreateEffigy( restoredPath, studio );
	}

	// ---- External-reference relinking ------------------------------------

	/// <summary>
	/// For each reference that no longer resolves, searches the project root and
	/// the given search roots for the same file: first at its original relative
	/// path under each root, then by file name at the top level of each root.
	/// Exactly one candidate relinks the reference (project-relative when it
	/// sits inside the project root, otherwise external read-only with explicit
	/// opt-in). Zero candidates stays missing; several candidates is ambiguous
	/// and refuses to guess. Nothing on disk is moved or copied.
	/// </summary>
	public IReadOnlyList<RelinkResult> RelinkMissing(
		AssetPathResolver projectResolver,
		IReadOnlyList<AssetReference> references,
		IReadOnlyList<string> searchRoots,
		bool allowExternal = false )
	{
		ArgumentNullException.ThrowIfNull( projectResolver );
		ArgumentNullException.ThrowIfNull( references );
		ArgumentNullException.ThrowIfNull( searchRoots );
		var results = new List<RelinkResult>();

		foreach ( var reference in references )
		{
			ResolvedAsset resolved;
			try
			{
				resolved = projectResolver.ResolveRead( reference, requireExists: false );
			}
			catch ( Exception ex )
			{
				results.Add( new RelinkResult( reference, RelinkOutcome.Error, null,
					Array.Empty<string>(), "Reference cannot be resolved: " + ex.Message ) );
				continue;
			}
			if ( resolved.Exists )
			{
				results.Add( new RelinkResult( reference, RelinkOutcome.Unchanged, null,
					Array.Empty<string>(), "Reference still resolves." ) );
				continue;
			}

			var candidates = FindCandidates( projectResolver, reference, searchRoots );
			if ( candidates.Count == 0 )
			{
				results.Add( new RelinkResult( reference, RelinkOutcome.StillMissing, null,
					Array.Empty<string>(),
					$"File '{Path.GetFileName( reference.Path )}' was not found in the project root or search roots." ) );
			}
			else if ( candidates.Count > 1 )
			{
				results.Add( new RelinkResult( reference, RelinkOutcome.Ambiguous, null, candidates,
					$"Several files named '{Path.GetFileName( reference.Path )}' were found; refusing to guess." ) );
			}
			else
			{
				string candidate = candidates[0];
				if ( IsInside( candidate, projectResolver.CanonicalRoot ) )
				{
					string relative = Path.GetRelativePath( projectResolver.CanonicalRoot, candidate ).Replace( '\\', '/' );
					results.Add( new RelinkResult( reference, RelinkOutcome.Relinked,
						new AssetReference( reference.Id, relative, AssetReferenceKind.ProjectRelative ),
						candidates, "Found inside the project root." ) );
				}
				else if ( allowExternal )
				{
					results.Add( new RelinkResult( reference, RelinkOutcome.Relinked,
						new AssetReference( reference.Id, candidate, AssetReferenceKind.ExternalReadOnly ),
						candidates, "Found outside the project root; kept as an explicit external reference." ) );
				}
				else
				{
					results.Add( new RelinkResult( reference, RelinkOutcome.StillMissing, null, candidates,
						"Found outside the project root; external opt-in is required to relink it." ) );
				}
			}
		}
		return results;
	}

	List<string> FindCandidates( AssetPathResolver projectResolver, AssetReference reference, IReadOnlyList<string> searchRoots )
	{
		var found = new List<string>();
		void AddIfFile( string path )
		{
			try
			{
				string full = Path.GetFullPath( path );
				if ( _fs.FileExists( full ) && !found.Contains( full, StringComparer.Ordinal ) )
					found.Add( full );
			}
			catch { }
		}

		string fileName = Path.GetFileName( reference.Path );
		var roots = new List<string> { projectResolver.CanonicalRoot };
		roots.AddRange( searchRoots.Where( r => !string.IsNullOrWhiteSpace( r ) ) );

		// 1. Original relative path under each root (project-relative references).
		if ( reference.Kind == AssetReferenceKind.ProjectRelative && !Path.IsPathRooted( reference.Path ) )
		{
			foreach ( var root in roots )
				AddIfFile( Path.Combine( root, reference.Path ) );
		}
		// 2. Same file name at the top level of each root.
		foreach ( var root in roots )
		{
			try
			{
				foreach ( var match in _fs.EnumerateFiles( root, fileName ) )
					AddIfFile( match );
			}
			catch { }
		}
		return found;
	}

	// ---- Helpers -----------------------------------------------------------

	static string BaseNameFor( DocumentSession session )
	{
		string? source = session.SourcePath;
		if ( !string.IsNullOrWhiteSpace( source ) )
		{
			string name = Path.GetFileNameWithoutExtension( source );
			if ( !string.IsNullOrWhiteSpace( name ) )
				return EngineExportPresets.SanitizeFileName( name );
		}
		return EngineExportPresets.SanitizeFileName( session.DocumentId );
	}

	static string ResolveDirectory( DocumentSession session, string? projectDirectory )
	{
		if ( !string.IsNullOrWhiteSpace( projectDirectory ) )
			return Path.GetFullPath( projectDirectory );
		string? directory = Path.GetDirectoryName( Path.GetFullPath( session.SourcePath ) );
		if ( string.IsNullOrWhiteSpace( directory ) )
			throw new InvalidOperationException( "Session has no source path and no project directory was given." );
		return directory;
	}

	string ManifestPath( string directory, string baseName ) =>
		Path.Combine( directory, baseName + ".versions.json" );

	VersionManifest LoadManifest( string directory, string baseName, string documentId )
	{
		string manifestPath = ManifestPath( directory, baseName );
		if ( _fs.FileExists( manifestPath ) )
		{
			try
			{
				var manifest = JsonSerializer.Deserialize<VersionManifest>( _fs.ReadAllText( manifestPath ) );
				if ( manifest != null )
					return manifest;
			}
			catch { }
		}
		// No (or corrupt) manifest: rebuild from the version files on disk.
		var versions = new List<VersionEntry>();
		try
		{
			foreach ( var file in _fs.EnumerateFiles( directory, baseName + "_v*.effigy" ) )
			{
				string name = Path.GetFileName( file );
				if ( TryParseVersionNumber( baseName, name, out int number ) )
					versions.Add( new VersionEntry( number, name, 0, DateTimeOffset.UtcNow, "" ) );
			}
		}
		catch { }
		versions.Sort( ( a, b ) => a.Number.CompareTo( b.Number ) );
		return new VersionManifest( documentId, baseName, versions );
	}

	void WriteManifest( string directory, VersionManifest manifest )
	{
		string manifestPath = ManifestPath( directory, manifest.BaseName );
		string json = JsonSerializer.Serialize( manifest, new JsonSerializerOptions { WriteIndented = true } );
		string tempPath = manifestPath + ".tmp";
		_fs.WriteAllText( tempPath, json );
		_fs.MoveFile( tempPath, manifestPath, overwrite: true );
	}

	static bool TryParseVersionNumber( string baseName, string fileName, out int number )
	{
		number = 0;
		string prefix = baseName + "_v";
		if ( !fileName.StartsWith( prefix, StringComparison.Ordinal ) )
			return false;
		string rest = fileName[prefix.Length..];
		if ( !rest.EndsWith( ".effigy", StringComparison.Ordinal ) )
			return false;
		return int.TryParse( rest[..^".effigy".Length], out number );
	}

	static bool IsInside( string path, string root )
	{
		string full = Path.GetFullPath( path );
		string rootFull = Path.GetFullPath( root );
		if ( OperatingSystem.IsWindows() )
			return full.Equals( rootFull, StringComparison.OrdinalIgnoreCase ) ||
				full.StartsWith( rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase );
		return full.Equals( rootFull, StringComparison.Ordinal ) ||
			full.StartsWith( rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal );
	}
}
