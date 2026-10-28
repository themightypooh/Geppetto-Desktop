#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Marionette.Desktop;

public enum AutosaveStatus
{
	Success,
	SkippedClean,
	SkippedBusy,
	SkippedUnchangedRevision,
	NoSession,
	Failed,
	Cancelled
}

public sealed record AutosaveResult(
	AutosaveStatus Status,
	string? DocumentId = null,
	long Revision = 0,
	string? Message = null,
	RecoverySnapshotMetadata? Metadata = null
)
{
	public bool IsSuccess => Status == AutosaveStatus.Success;
}

public sealed record RecoveryPurgeResult(
	int PurgedCount,
	int RetainedCount,
	IReadOnlyList<string> PurgedSnapshotIds
);

/// <summary>
/// Service coordinating periodic and on-demand recovery snapshots via DocumentSaveService.
/// Enforces bounded scheduling without overlapping save storms, executes disk IO off the render thread,
/// preserves the session's dirty state, and manages recovery discovery and retention purging.
/// </summary>
public sealed class AutosaveScheduler : IDisposable
{
	readonly DocumentSaveService _saveService;
	readonly IFileSystem _fs;
	readonly SemaphoreSlim _saveLock = new( 1, 1 );
	readonly Action<string>? _logDiagnostic;

	Timer? _timer;
	CancellationTokenSource? _cts;
	bool _disposed;

	public string RecoveryDirectory { get; }
	public DesktopPreferences Preferences { get; }
	public DocumentSession? ActiveSession { get; set; }

	public long LastAutosavedRevision { get; private set; }
	public string? LastAutosavedDocumentId { get; private set; }
	public bool IsSaveInProgress => _saveLock.CurrentCount == 0;

	public AutosaveScheduler(
		DocumentSaveService? saveService = null,
		string? recoveryDirectory = null,
		DesktopPreferences? preferences = null,
		IFileSystem? fileSystem = null,
		Action<string>? logDiagnostic = null )
	{
		_fs = fileSystem ?? PhysicalFileSystem.Instance;
		_saveService = saveService ?? new DocumentSaveService( _fs );
		Preferences = preferences ?? DesktopPreferences.CreateDefault();
		_logDiagnostic = logDiagnostic;

		if ( !string.IsNullOrWhiteSpace( recoveryDirectory ) )
		{
			RecoveryDirectory = recoveryDirectory;
		}
		else
		{
			var pathEnv = new DesktopPathEnvironment();
			RecoveryDirectory = pathEnv.GetRecoveriesDirectory();
		}
	}

	/// <summary>
	/// Triggers an autosave pass on the specified session or ActiveSession.
	/// Skips clean documents, bounded to prevent overlapping saves, and captures snapshot off-thread.
	/// Main document session dirty state is never cleared.
	/// </summary>
	public async Task<AutosaveResult> TriggerAutosaveAsync(
		DocumentSession? session = null,
		CancellationToken cancellationToken = default )
	{
		ObjectDisposedException.ThrowIf( _disposed, this );

		var targetSession = session ?? ActiveSession;
		if ( targetSession == null )
		{
			return new AutosaveResult( AutosaveStatus.NoSession, null, 0, "No active session to autosave." );
		}

		// 1. Clean documents do not trigger autosave
		if ( !targetSession.IsDirty )
		{
			return new AutosaveResult(
				AutosaveStatus.SkippedClean,
				targetSession.DocumentId,
				targetSession.Revision,
				"Document session is clean."
			);
		}

		// 2. Skip if revision was already autosaved
		if ( targetSession.Revision == LastAutosavedRevision &&
		     targetSession.DocumentId == LastAutosavedDocumentId )
		{
			return new AutosaveResult(
				AutosaveStatus.SkippedUnchangedRevision,
				targetSession.DocumentId,
				targetSession.Revision,
				"Revision has already been autosaved."
			);
		}

		// 3. Bounded scheduling: no overlapping save storms
		if ( !_saveLock.Wait( 0 ) )
		{
			return new AutosaveResult(
				AutosaveStatus.SkippedBusy,
				targetSession.DocumentId,
				targetSession.Revision,
				"Another autosave operation is already in progress."
			);
		}

		try
		{
			if ( cancellationToken.IsCancellationRequested )
			{
				return new AutosaveResult( AutosaveStatus.Cancelled, targetSession.DocumentId, targetSession.Revision, "Autosave cancelled." );
			}

			// Capture revision before launching background worker
			long capturedRevision = targetSession.Revision;
			string capturedDocId = targetSession.DocumentId;

			// Run recovery snapshot on worker task (off caller/render thread)
			var metadata = await Task.Run( () =>
			{
				return _saveService.CreateRecoverySnapshot( targetSession, RecoveryDirectory );
			}, cancellationToken );

			LastAutosavedRevision = metadata.Revision;
			LastAutosavedDocumentId = metadata.DocumentId;

			// Run retention purge
			try
			{
				PurgeExpiredRecoveries(
					RecoveryDirectory,
					Preferences.Autosave.MaxRecoverySnapshots,
					TimeSpan.FromDays( Preferences.Autosave.RetentionDays ),
					_fs,
					_logDiagnostic
				);
			}
			catch ( Exception ex )
			{
				_logDiagnostic?.Invoke( $"Recovery retention purge failed: {ex.Message}" );
			}

			return new AutosaveResult(
				AutosaveStatus.Success,
				capturedDocId,
				capturedRevision,
				Metadata: metadata
			);
		}
		catch ( OperationCanceledException )
		{
			return new AutosaveResult( AutosaveStatus.Cancelled, targetSession.DocumentId, targetSession.Revision, "Autosave cancelled." );
		}
		catch ( Exception ex )
		{
			_logDiagnostic?.Invoke( $"Autosave snapshot failed: {ex.Message}" );
			return new AutosaveResult( AutosaveStatus.Failed, targetSession.DocumentId, targetSession.Revision, ex.Message );
		}
		finally
		{
			_saveLock.Release();
		}
	}

	/// <summary>
	/// Starts the background periodic autosave timer.
	/// </summary>
	public void Start( TimeSpan? customInterval = null )
	{
		ObjectDisposedException.ThrowIf( _disposed, this );
		Stop();

		if ( !Preferences.Autosave.Enabled ) return;

		var interval = customInterval ?? TimeSpan.FromSeconds( Preferences.Autosave.IntervalSeconds );
		_cts = new CancellationTokenSource();
		var token = _cts.Token;

		_timer = new Timer( _ =>
		{
			if ( token.IsCancellationRequested || _disposed ) return;
			_ = Task.Run( async () =>
			{
				try
				{
					await TriggerAutosaveAsync( null, token );
				}
				catch
				{
					// Background timer tick swallows unhandled errors
				}
			}, token );
		}, null, interval, interval );
	}

	/// <summary>
	/// Stops the periodic timer without disposing.
	/// </summary>
	public void Stop()
	{
		_cts?.Cancel();
		_cts?.Dispose();
		_cts = null;

		_timer?.Dispose();
		_timer = null;
	}

	/// <summary>
	/// Discovers staged recoveries and returns metadata sorted by timestamp descending.
	/// </summary>
	public static IReadOnlyList<RecoverySnapshotMetadata> DiscoverRecoveries(
		string recoveryDirectory,
		IFileSystem? fileSystem = null )
	{
		var fs = fileSystem ?? PhysicalFileSystem.Instance;
		if ( !fs.DirectoryExists( recoveryDirectory ) )
			return Array.Empty<RecoverySnapshotMetadata>();

		var results = new List<RecoverySnapshotMetadata>();
		foreach ( var file in fs.EnumerateFiles( recoveryDirectory, "*.recovery.json" ) )
		{
			try
			{
				string json = fs.ReadAllText( file );
				var meta = JsonSerializer.Deserialize<RecoverySnapshotMetadata>( json, new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true
				} );
				if ( meta != null && !string.IsNullOrWhiteSpace( meta.SnapshotId ) )
				{
					results.Add( meta );
				}
			}
			catch
			{
				// Ignore corrupted or partially written recovery metadata
			}
		}

		return results
			.OrderByDescending( m => m.TimestampUtc )
			.ThenByDescending( m => m.Revision )
			.ToList();
	}

	/// <summary>
	/// Purges old recovery snapshots based on max per document and age retention policy.
	/// Strictly purges only owned recovery metadata and payload files, leaving unowned files untouched.
	/// </summary>
	public static RecoveryPurgeResult PurgeExpiredRecoveries(
		string recoveryDirectory,
		int maxPerDocument,
		TimeSpan maxAge,
		IFileSystem? fileSystem = null,
		Action<string>? logDiagnostic = null )
	{
		var fs = fileSystem ?? PhysicalFileSystem.Instance;
		if ( !fs.DirectoryExists( recoveryDirectory ) )
			return new RecoveryPurgeResult( 0, 0, Array.Empty<string>() );

		var allSnapshots = DiscoverRecoveries( recoveryDirectory, fs );
		var now = DateTimeOffset.UtcNow;
		var cutoff = now - maxAge;

		var purgedIds = new List<string>();
		int retainedCount = 0;

		// Group by document ID so each document retains its most recent snapshots
		var groups = allSnapshots.GroupBy( s => s.DocumentId );

		foreach ( var group in groups )
		{
			var ordered = group.OrderByDescending( s => s.TimestampUtc ).ThenByDescending( s => s.Revision ).ToList();

			for ( int i = 0; i < ordered.Count; i++ )
			{
				var snapshot = ordered[i];
				bool isExpiredAge = snapshot.TimestampUtc < cutoff;
				bool isExceedingCap = i >= maxPerDocument;

				if ( isExpiredAge || isExceedingCap )
				{
					// Delete metadata file
					string metaFile = Path.Combine( recoveryDirectory, $"{snapshot.SnapshotId}.recovery.json" );
					try
					{
						if ( fs.FileExists( metaFile ) ) fs.DeleteFile( metaFile );
					}
					catch ( Exception ex )
					{
						logDiagnostic?.Invoke( $"Failed to delete recovery metadata '{metaFile}': {ex.Message}" );
					}

					// Delete associated payload file
					if ( !string.IsNullOrWhiteSpace( snapshot.PayloadFileName ) )
					{
						string payloadFile = Path.Combine( recoveryDirectory, snapshot.PayloadFileName );
						try
						{
							if ( fs.FileExists( payloadFile ) ) fs.DeleteFile( payloadFile );
						}
						catch ( Exception ex )
						{
							logDiagnostic?.Invoke( $"Failed to delete recovery payload '{payloadFile}': {ex.Message}" );
						}
					}

					purgedIds.Add( snapshot.SnapshotId );
				}
				else
				{
					retainedCount++;
				}
			}
		}

		return new RecoveryPurgeResult( purgedIds.Count, retainedCount, purgedIds );
	}

	public void Dispose()
	{
		if ( _disposed ) return;
		_disposed = true;
		Stop();
		_saveLock.Dispose();
	}
}
