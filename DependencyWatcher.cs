#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Categories of watched dependencies.
/// </summary>
public enum DependencyKind
{
	ProjectManifest,
	SourceModel,
	AssemblyModel,
	ControlRig,
	Clip,
	Texture,
	Material,
	Other
}

/// <summary>
/// A registered dependency entry.
/// </summary>
public sealed record DependencyEntry(
	string Path,
	DependencyKind Kind = DependencyKind.Other,
	string? LogicalId = null
);

/// <summary>
/// Outcome of a reload pass.
/// </summary>
public enum ReloadStatus
{
	Success,
	StaleDiscarded,
	ConflictLocalDirty,
	PartialWriteRetryExhausted,
	Error,
	Cancelled
}

/// <summary>
/// Detached reload job information.
/// </summary>
public sealed record ReloadJob(
	long JobGeneration,
	long CapturedRevision,
	string? DocumentId,
	IReadOnlyList<string> ChangedPaths,
	IReadOnlyList<DependencyEntry> ChangedDependencies
);

/// <summary>
/// Immutable result of a reload operation.
/// </summary>
public sealed record ReloadResult(
	ReloadStatus Status,
	long JobGeneration,
	long CapturedRevision,
	IReadOnlyList<string> ChangedPaths,
	RenderSnapshot? Snapshot = null,
	string? ErrorMessage = null
)
{
	public bool IsSuccess => Status == ReloadStatus.Success;
}

/// <summary>
/// Event arguments for reload conflict notifications.
/// </summary>
public sealed class ReloadConflictEventArgs : EventArgs
{
	public string ChangedPath { get; }
	public long SessionRevision { get; }
	public string DocumentId { get; }
	public string Message { get; }

	public ReloadConflictEventArgs( string changedPath, long sessionRevision, string documentId, string message )
	{
		ChangedPath = changedPath;
		SessionRevision = sessionRevision;
		DocumentId = documentId;
		Message = message;
	}
}

/// <summary>
/// Event arguments for reload start.
/// </summary>
public sealed class ReloadStartedEventArgs : EventArgs
{
	public ReloadJob Job { get; }
	public ReloadStartedEventArgs( ReloadJob job ) => Job = job;
}

/// <summary>
/// Event arguments for completed reload.
/// </summary>
public sealed class ReloadCompletedEventArgs : EventArgs
{
	public ReloadResult Result { get; }
	public ReloadCompletedEventArgs( ReloadResult result ) => Result = result;
}

/// <summary>
/// Event arguments for reload failure.
/// </summary>
public sealed class ReloadFailedEventArgs : EventArgs
{
	public ReloadJob Job { get; }
	public string ErrorMessage { get; }
	public ReloadFailedEventArgs( ReloadJob job, string errorMessage )
	{
		Job = job;
		ErrorMessage = errorMessage;
	}
}

/// <summary>
/// Event arguments when a reload result is discarded as stale.
/// </summary>
public sealed class StaleDiscardedEventArgs : EventArgs
{
	public long JobGeneration { get; }
	public long LatestCompletedGeneration { get; }
	public long CapturedRevision { get; }
	public long CurrentSessionRevision { get; }

	public StaleDiscardedEventArgs( long jobGeneration, long latestCompletedGeneration, long capturedRevision, long currentSessionRevision )
	{
		JobGeneration = jobGeneration;
		LatestCompletedGeneration = latestCompletedGeneration;
		CapturedRevision = capturedRevision;
		CurrentSessionRevision = currentSessionRevision;
	}
}

/// <summary>
/// Event arguments when a new render snapshot is promoted.
/// </summary>
public sealed class SnapshotPromotedEventArgs : EventArgs
{
	public RenderSnapshot Snapshot { get; }
	public long JobGeneration { get; }

	public SnapshotPromotedEventArgs( RenderSnapshot snapshot, long jobGeneration )
	{
		Snapshot = snapshot;
		JobGeneration = jobGeneration;
	}
}

/// <summary>
/// Time abstraction for deterministic testing.
/// </summary>
public interface IWatcherClock
{
	DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IWatcherClock
{
	public static SystemClock Instance { get; } = new();
	public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Debounce scheduler abstraction for deterministic testing without fragile sleeps.
/// </summary>
public interface IDebounceScheduler
{
	void Schedule( TimeSpan delay, Action action );
	void Cancel();
}

public sealed class SystemDebounceScheduler : IDebounceScheduler, IDisposable
{
	Timer? _timer;
	readonly object _lock = new();
	Action? _action;

	public void Schedule( TimeSpan delay, Action action )
	{
		lock ( _lock )
		{
			_action = action;
			if ( _timer == null )
			{
				_timer = new Timer( _ => Fire(), null, delay, Timeout.InfiniteTimeSpan );
			}
			else
			{
				_timer.Change( delay, Timeout.InfiniteTimeSpan );
			}
		}
	}

	void Fire()
	{
		Action? act;
		lock ( _lock )
		{
			act = _action;
			_action = null;
		}
		act?.Invoke();
	}

	public void Cancel()
	{
		lock ( _lock )
		{
			_action = null;
			_timer?.Change( Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan );
		}
	}

	public void Dispose()
	{
		lock ( _lock )
		{
			_action = null;
			_timer?.Dispose();
			_timer = null;
		}
	}
}

/// <summary>
/// Dependency watching and atomic reload promotion service (P3).
///
/// 1. Watches resolved dependencies (textures, assembly models, control rigs, clips, project manifests).
/// 2. Deduplicates filesystem events and debounces bursts.
/// 3. Executes off-thread background rebuilds; treats partially written files as retryable bounded failures.
/// 4. Builds detached snapshots tagged with revision/job generation; rejects stale out-of-order completions.
/// 5. Respects local dirty session state by producing conflict notifications without mutating authored data.
/// 6. Suppresses own-save echoes (including atomic temp files).
/// 7. Manages clean cancellation and disposal of in-flight work.
/// </summary>
public sealed class DependencyWatcher : IDisposable
{
	readonly IFileSystem _fs;
	readonly IWatcherClock _clock;
	readonly IDebounceScheduler _debounceScheduler;
	readonly bool _enableRealFileSystemWatcher;

	readonly Dictionary<string, DependencyEntry> _dependencies;
	readonly HashSet<string> _pendingDirtyPaths;
	readonly Dictionary<string, DateTimeOffset> _suppressedSaves;
	readonly Dictionary<string, FileSystemWatcher> _fileSystemWatchers;
	readonly Dictionary<long, Task<ReloadResult>> _runningTasks = new();
	readonly Dictionary<long, CancellationTokenSource> _activeCts = new();

	readonly object _dependencyLock = new();
	readonly object _debounceLock = new();
	readonly object _suppressLock = new();
	readonly object _watchersLock = new();
	readonly object _jobsLock = new();

	readonly CancellationTokenSource _disposeCts = new();

	long _jobGeneration;
	long _latestCompletedGeneration;
	bool _isDisposed;

	DocumentSession? _session;
	DesktopProject? _project;
	AssetPathResolver? _resolver;
	PartStudio? _reloadedStudio;

	public DocumentSession? Session => _session;
	public DesktopProject? Project => _project;
	public AssetPathResolver? Resolver => _resolver;

	public TimeSpan DebounceWindow { get; set; } = TimeSpan.FromMilliseconds( 50 );
	public int MaxRetries { get; set; } = 3;
	public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds( 20 );
	public bool AutoPromote { get; set; } = true;

	public long CurrentJobGeneration => Volatile.Read( ref _jobGeneration );
	public long LatestCompletedGeneration => Volatile.Read( ref _latestCompletedGeneration );
	public bool IsDisposed => _isDisposed;

	public RenderSnapshot? LatestSnapshot { get; private set; }
	public ReloadResult? LatestResult { get; private set; }

	public Func<ReloadJob, CancellationToken, Task<RenderSnapshot?>>? CustomRebuilder { get; set; }

	public event EventHandler<ReloadStartedEventArgs>? ReloadStarted;
	public event EventHandler<ReloadCompletedEventArgs>? ReloadCompleted;
	public event EventHandler<ReloadConflictEventArgs>? ConflictDetected;
	public event EventHandler<ReloadFailedEventArgs>? ReloadFailed;
	public event EventHandler<StaleDiscardedEventArgs>? StaleResultDiscarded;
	public event EventHandler<SnapshotPromotedEventArgs>? SnapshotPromoted;

	static StringComparer PathComparer =>
		OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	public DependencyWatcher(
		DocumentSession? session = null,
		IFileSystem? fileSystem = null,
		IWatcherClock? clock = null,
		IDebounceScheduler? debounceScheduler = null,
		TimeSpan? debounceWindow = null,
		bool startWatching = false )
	{
		_fs = fileSystem ?? PhysicalFileSystem.Instance;
		_clock = clock ?? SystemClock.Instance;
		_debounceScheduler = debounceScheduler ?? new SystemDebounceScheduler();
		if ( debounceWindow.HasValue ) DebounceWindow = debounceWindow.Value;
		_enableRealFileSystemWatcher = startWatching;

		_dependencies = new Dictionary<string, DependencyEntry>( PathComparer );
		_pendingDirtyPaths = new HashSet<string>( PathComparer );
		_suppressedSaves = new Dictionary<string, DateTimeOffset>( PathComparer );
		_fileSystemWatchers = new Dictionary<string, FileSystemWatcher>( PathComparer );

		if ( session != null )
		{
			AttachSession( session );
		}
	}

	public IReadOnlyCollection<DependencyEntry> Dependencies
	{
		get
		{
			lock ( _dependencyLock )
			{
				return _dependencies.Values.ToList();
			}
		}
	}

	public bool HasPendingJobs
	{
		get
		{
			lock ( _jobsLock )
			{
				return _runningTasks.Count > 0;
			}
		}
	}

	public static string NormalizePath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return "";
		try
		{
			return Path.GetFullPath( path );
		}
		catch
		{
			return path.Replace( '\\', '/' );
		}
	}

	public static bool IsTemporaryFile( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return false;
		string fileName = Path.GetFileName( path );
		if ( fileName.StartsWith( "." ) && fileName.Contains( ".tmp" ) ) return true;
		if ( fileName.EndsWith( ".tmp", StringComparison.OrdinalIgnoreCase ) ) return true;
		if ( fileName.Contains( ".tmp." ) ) return true;
		return false;
	}

	public void AttachSession( DocumentSession session )
	{
		ArgumentNullException.ThrowIfNull( session );
		_session = session;

		if ( !string.IsNullOrWhiteSpace( session.SourcePath ) )
		{
			AddDependency( session.SourcePath, session.SourceKind == DocumentSourceKind.Fbx ? DependencyKind.SourceModel : DependencyKind.SourceModel );
		}
		if ( !string.IsNullOrWhiteSpace( session.ClipPath ) )
		{
			AddDependency( session.ClipPath, DependencyKind.Clip );
		}
	}

	public void WatchProject( DesktopProject project, AssetPathResolver resolver, string? manifestPath = null )
	{
		ArgumentNullException.ThrowIfNull( project );
		ArgumentNullException.ThrowIfNull( resolver );

		_project = project;
		_resolver = resolver;

		if ( !string.IsNullOrWhiteSpace( manifestPath ) )
		{
			AddDependency( manifestPath, DependencyKind.ProjectManifest );
		}

		foreach ( var doc in project.SourceDocuments )
		{
			if ( doc.Asset != null && !string.IsNullOrWhiteSpace( doc.Asset.Path ) )
			{
				try
				{
					string resolved = resolver.ResolveRead( doc.Asset, requireExists: false ).CanonicalPath;
					AddDependency( resolved, DependencyKind.SourceModel, doc.Id );
				}
				catch { }
			}
		}

		foreach ( var clip in project.Clips )
		{
			if ( clip.Asset != null && !string.IsNullOrWhiteSpace( clip.Asset.Path ) )
			{
				try
				{
					string resolved = resolver.ResolveRead( clip.Asset, requireExists: false ).CanonicalPath;
					AddDependency( resolved, DependencyKind.Clip, clip.Id );
				}
				catch { }
			}
		}

		foreach ( var mo in project.MaterialOverrides )
		{
			if ( mo.MaterialAsset != null && !string.IsNullOrWhiteSpace( mo.MaterialAsset.Path ) )
			{
				try
				{
					string resolved = resolver.ResolveRead( mo.MaterialAsset, requireExists: false ).CanonicalPath;
					AddDependency( resolved, DependencyKind.Material, mo.Id );
				}
				catch { }
			}
			if ( mo.Descriptor != null )
			{
				WatchMaterialDescriptor( mo.Descriptor, resolver, mo.Id );
			}
		}
	}

	void WatchMaterialDescriptor( MaterialDescriptor descriptor, AssetPathResolver resolver, string overrideId )
	{
		void WatchBinding( TextureBinding? binding, string channel )
		{
			if ( binding?.Reference != null && !string.IsNullOrWhiteSpace( binding.Reference.Path ) )
			{
				try
				{
					string resolved = resolver.ResolveRead( binding.Reference, requireExists: false ).CanonicalPath;
					AddDependency( resolved, DependencyKind.Texture, $"{overrideId}:{channel}" );
				}
				catch { }
			}
		}

		WatchBinding( descriptor.BaseColorMap, "baseColor" );
		WatchBinding( descriptor.NormalMap, "normal" );
		WatchBinding( descriptor.RoughnessMap, "roughness" );
		WatchBinding( descriptor.MetallicMap, "metallic" );
		WatchBinding( descriptor.EmissionMap, "emission" );
		WatchBinding( descriptor.AmbientOcclusionMap, "ao" );
	}

	public void AddDependency( string path, DependencyKind kind = DependencyKind.Other, string? logicalId = null )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return;
		string normalized = NormalizePath( path );

		lock ( _dependencyLock )
		{
			_dependencies[normalized] = new DependencyEntry( normalized, kind, logicalId );
		}

		EnsureWatcherForPath( normalized );
	}

	public void RemoveDependency( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return;
		string normalized = NormalizePath( path );

		lock ( _dependencyLock )
		{
			_dependencies.Remove( normalized );
		}
	}

	public void ClearDependencies()
	{
		lock ( _dependencyLock )
		{
			_dependencies.Clear();
		}
	}

	public bool IsWatching( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return false;
		string normalized = NormalizePath( path );

		lock ( _dependencyLock )
		{
			return _dependencies.ContainsKey( normalized );
		}
	}

	public DependencyEntry? GetDependency( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return null;
		string normalized = NormalizePath( path );

		lock ( _dependencyLock )
		{
			_dependencies.TryGetValue( normalized, out var entry );
			return entry;
		}
	}

	// --- Echo Suppression -------------------------------------------------------------------

	public bool IsEchoSuppressed( string path )
	{
		string norm = NormalizePath( path );
		lock ( _suppressLock )
		{
			if ( _suppressedSaves.TryGetValue( norm, out var expiresAt ) )
			{
				if ( _clock.UtcNow <= expiresAt )
				{
					return true;
				}
				_suppressedSaves.Remove( norm );
			}
			return false;
		}
	}

	public void RegisterOwnSave( string path, TimeSpan? window = null )
	{
		string norm = NormalizePath( path );
		var duration = window ?? TimeSpan.FromMilliseconds( 500 );
		lock ( _suppressLock )
		{
			_suppressedSaves[norm] = _clock.UtcNow + duration;
		}
	}

	public IDisposable SuppressEcho( string path, TimeSpan? window = null )
	{
		string norm = NormalizePath( path );
		RegisterOwnSave( norm, window ?? TimeSpan.FromSeconds( 5 ) );
		return new EchoSuppressionScope( this, norm, window ?? TimeSpan.FromMilliseconds( 500 ) );
	}

	sealed class EchoSuppressionScope : IDisposable
	{
		readonly DependencyWatcher _watcher;
		readonly string _path;
		readonly TimeSpan _postSaveWindow;

		public EchoSuppressionScope( DependencyWatcher watcher, string path, TimeSpan postSaveWindow )
		{
			_watcher = watcher;
			_path = path;
			_postSaveWindow = postSaveWindow;
		}

		public void Dispose()
		{
			_watcher.RegisterOwnSave( _path, _postSaveWindow );
		}
	}

	// --- Event Intake & Debounce -------------------------------------------------------------

	public void NotifyFileSystemChange( string fullPath, WatcherChangeTypes changeType = WatcherChangeTypes.Changed )
	{
		if ( _isDisposed ) return;

		string normalized = NormalizePath( fullPath );

		if ( IsTemporaryFile( normalized ) )
		{
			return;
		}

		if ( IsEchoSuppressed( normalized ) )
		{
			return;
		}

		if ( !IsWatching( normalized ) )
		{
			return;
		}

		lock ( _debounceLock )
		{
			_pendingDirtyPaths.Add( normalized );
			_debounceScheduler.Schedule( DebounceWindow, () => OnDebounceFired() );
		}
	}

	void OnDebounceFired()
	{
		if ( _isDisposed ) return;

		List<string> pathsToRebuild;
		lock ( _debounceLock )
		{
			if ( _pendingDirtyPaths.Count == 0 ) return;
			pathsToRebuild = _pendingDirtyPaths.ToList();
			_pendingDirtyPaths.Clear();
		}

		_ = EnqueueReloadJobAsync( pathsToRebuild );
	}

	public Task<ReloadResult> TriggerReloadAsync( IEnumerable<string>? changedPaths = null, CancellationToken cancellationToken = default )
	{
		var list = changedPaths?.Select( NormalizePath ).ToList() ?? Dependencies.Select( d => d.Path ).ToList();
		return EnqueueReloadJobAsync( list, cancellationToken );
	}

	Task<ReloadResult> EnqueueReloadJobAsync( IReadOnlyList<string> changedPaths, CancellationToken externalToken = default )
	{
		long generation = Interlocked.Increment( ref _jobGeneration );
		long capturedRevision = _session?.Revision ?? 0;
		string? docId = _session?.DocumentId;

		var changedDeps = changedPaths
			.Select( p => GetDependency( p ) ?? new DependencyEntry( p, DependencyKind.Other ) )
			.ToList();

		var job = new ReloadJob( generation, capturedRevision, docId, changedPaths, changedDeps );

		var cts = CancellationTokenSource.CreateLinkedTokenSource( _disposeCts.Token, externalToken );
		lock ( _jobsLock )
		{
			_activeCts[generation] = cts;
		}

		var task = Task.Run( async () =>
		{
			try
			{
				return await ExecuteReloadJobAsync( job, cts.Token );
			}
			finally
			{
				lock ( _jobsLock )
				{
					_activeCts.Remove( generation );
					_runningTasks.Remove( generation );
				}
				cts.Dispose();
			}
		}, cts.Token );

		lock ( _jobsLock )
		{
			_runningTasks[generation] = task;
		}

		return task;
	}

	// --- Reload Worker Execution -------------------------------------------------------------

	async Task<ReloadResult> ExecuteReloadJobAsync( ReloadJob job, CancellationToken ct )
	{
		if ( ct.IsCancellationRequested || _isDisposed )
		{
			return new ReloadResult( ReloadStatus.Cancelled, job.JobGeneration, job.CapturedRevision, job.ChangedPaths, ErrorMessage: "Reload cancelled." );
		}

		// Conflict check: if local session has uncommitted dirty edits, do NOT overwrite dirty state!
		if ( _session != null && _session.IsDirty )
		{
			var conflictArgs = new ReloadConflictEventArgs(
				job.ChangedPaths.FirstOrDefault() ?? _session.SourcePath,
				_session.Revision,
				_session.DocumentId,
				"Local session has uncommitted dirty edits. External reload aborted to preserve authored data."
			);
			ConflictDetected?.Invoke( this, conflictArgs );
			return new ReloadResult(
				ReloadStatus.ConflictLocalDirty,
				job.JobGeneration,
				job.CapturedRevision,
				job.ChangedPaths,
				ErrorMessage: conflictArgs.Message
			);
		}

		ReloadStarted?.Invoke( this, new ReloadStartedEventArgs( job ) );

		RenderSnapshot? snapshot = null;
		Exception? lastException = null;

		for ( int attempt = 1; attempt <= MaxRetries; attempt++ )
		{
			if ( ct.IsCancellationRequested || _isDisposed )
			{
				return new ReloadResult( ReloadStatus.Cancelled, job.JobGeneration, job.CapturedRevision, job.ChangedPaths, ErrorMessage: "Reload cancelled." );
			}

			try
			{
				if ( CustomRebuilder != null )
				{
					snapshot = await CustomRebuilder( job, ct );
				}
				else
				{
					snapshot = await PerformDefaultRebuildAsync( job, ct );
				}
				lastException = null;
				break;
			}
			catch ( OperationCanceledException ) when ( ct.IsCancellationRequested )
			{
				return new ReloadResult( ReloadStatus.Cancelled, job.JobGeneration, job.CapturedRevision, job.ChangedPaths, ErrorMessage: "Reload cancelled." );
			}
			catch ( Exception ex ) when ( IsRetryableException( ex ) && attempt < MaxRetries )
			{
				lastException = ex;
				if ( RetryDelay > TimeSpan.Zero )
				{
					await Task.Delay( RetryDelay, ct );
				}
			}
			catch ( Exception ex )
			{
				lastException = ex;
				break;
			}
		}

		if ( lastException != null )
		{
			snapshot?.Dispose();
			var failArgs = new ReloadFailedEventArgs( job, lastException.Message );
			ReloadFailed?.Invoke( this, failArgs );
			return new ReloadResult(
				IsRetryableException( lastException ) ? ReloadStatus.PartialWriteRetryExhausted : ReloadStatus.Error,
				job.JobGeneration,
				job.CapturedRevision,
				job.ChangedPaths,
				ErrorMessage: lastException.Message
			);
		}

		if ( ct.IsCancellationRequested || _isDisposed )
		{
			snapshot?.Dispose();
			return new ReloadResult( ReloadStatus.Cancelled, job.JobGeneration, job.CapturedRevision, job.ChangedPaths, ErrorMessage: "Reload cancelled." );
		}

		// Staleness check:
		// 1. If newer job generation completed, this job is stale.
		// 2. If session revision changed during reload, this result is stale.
		long latestCompleted = Volatile.Read( ref _latestCompletedGeneration );
		long currentSessionRev = _session?.Revision ?? job.CapturedRevision;

		bool isStale = (job.JobGeneration < latestCompleted) ||
			(_session != null && _session.Revision != job.CapturedRevision);

		if ( isStale )
		{
			snapshot?.Dispose();
			StaleResultDiscarded?.Invoke( this, new StaleDiscardedEventArgs( job.JobGeneration, latestCompleted, job.CapturedRevision, currentSessionRev ) );
			return new ReloadResult(
				ReloadStatus.StaleDiscarded,
				job.JobGeneration,
				job.CapturedRevision,
				job.ChangedPaths,
				ErrorMessage: "Reload result discarded because a newer reload completed or session revision advanced."
			);
		}

		// Update latest completed generation monotonically
		long prev;
		do
		{
			prev = Volatile.Read( ref _latestCompletedGeneration );
			if ( job.JobGeneration <= prev )
			{
				// Another newer generation was recorded concurrently
				snapshot?.Dispose();
				StaleResultDiscarded?.Invoke( this, new StaleDiscardedEventArgs( job.JobGeneration, prev, job.CapturedRevision, currentSessionRev ) );
				return new ReloadResult(
					ReloadStatus.StaleDiscarded,
					job.JobGeneration,
					job.CapturedRevision,
					job.ChangedPaths,
					ErrorMessage: "Newer reload generation completed concurrently."
				);
			}
		} while ( Interlocked.CompareExchange( ref _latestCompletedGeneration, job.JobGeneration, prev ) != prev );

		LatestSnapshot = snapshot;

		// Promotion
		if ( AutoPromote && _session != null && snapshot != null )
		{
			if ( !_session.IsDirty && job.ChangedPaths.Contains( _session.SourcePath ) && _reloadedStudio != null )
			{
				_session.Studio?.Features.Clear();
				foreach ( var f in _reloadedStudio.Features ) _session.Studio?.Add( f );
				_session.Studio?.Rebuild();
				_session.MarkSaved();
			}

			bool promoted = _session.TryPromoteSnapshot( snapshot );
			if ( promoted )
			{
				SnapshotPromoted?.Invoke( this, new SnapshotPromotedEventArgs( snapshot, job.JobGeneration ) );
			}
		}

		var successResult = new ReloadResult( ReloadStatus.Success, job.JobGeneration, job.CapturedRevision, job.ChangedPaths, snapshot );
		LatestResult = successResult;
		ReloadCompleted?.Invoke( this, new ReloadCompletedEventArgs( successResult ) );
		return successResult;
	}

	Task<RenderSnapshot?> PerformDefaultRebuildAsync( ReloadJob job, CancellationToken ct )
	{
		if ( _session == null )
		{
			return Task.FromResult<RenderSnapshot?>( null );
		}

		ct.ThrowIfCancellationRequested();

		string sourcePath = NormalizePath( _session.SourcePath );
		bool sourceChanged = job.ChangedPaths.Any( p => NormalizePath( p ) == sourcePath );
		string? clipPath = _session.ClipPath != null ? NormalizePath( _session.ClipPath ) : null;
		bool clipChanged = clipPath != null && job.ChangedPaths.Any( p => NormalizePath( p ) == clipPath );

		if ( sourceChanged )
		{
			if ( !_fs.FileExists( _session.SourcePath ) )
			{
				throw new FileNotFoundException( $"Source document file missing: {_session.SourcePath}" );
			}

			if ( _session.SourceKind == DocumentSourceKind.Effigy )
			{
				string content = _fs.ReadAllText( _session.SourcePath );
				if ( string.IsNullOrWhiteSpace( content ) )
				{
					throw new EndOfStreamException( "Effigy file is empty (partial write in progress)." );
				}

				var detachedStudio = StudioDocument.Read( content );
				if ( detachedStudio == null )
				{
					throw new InvalidDataException( "StudioDocument.Read returned null." );
				}

				var report = detachedStudio.Rebuild();
				if ( report.HasErrors )
				{
					throw new InvalidOperationException( $"Detached studio rebuild failed: {report.Errors.Count} error(s)." );
				}

				_reloadedStudio = detachedStudio;
				var mesh = ViewportMesh.FromStudio( detachedStudio, 0, _session.SourcePath );
				MarionettePlayer? player = null;
				if ( mesh.Rig is { Count: > 0 } )
				{
					player = new MarionettePlayer( mesh.Rig, startPlaying: _session.IsPlaying );
					if ( _session.Clip != null ) player.LoadClip( _session.Clip );
					else if ( !string.IsNullOrEmpty( _session.ClipPath ) ) player.LoadClip( _session.ClipPath );
				}

				return Task.FromResult<RenderSnapshot?>( new RenderSnapshot( _session.DocumentId, job.CapturedRevision, mesh, player ) );
			}
			else if ( _session.SourceKind == DocumentSourceKind.Fbx )
			{
				byte[] bytes = _fs is PhysicalFileSystem
					? File.ReadAllBytes( _session.SourcePath )
					: Encoding.UTF8.GetBytes( _fs.ReadAllText( _session.SourcePath ) );

				if ( bytes.Length == 0 )
				{
					throw new EndOfStreamException( "FBX file is empty (partial write in progress)." );
				}

				var imported = Effigy.FbxImport.Read( bytes );
				var mesh = ViewportMesh.FromFbx( imported, 0, _session.SourcePath );
				return Task.FromResult<RenderSnapshot?>( new RenderSnapshot( _session.DocumentId, job.CapturedRevision, mesh ) );
			}
		}

		if ( clipChanged && _session.ClipPath != null )
		{
			if ( !_fs.FileExists( _session.ClipPath ) )
			{
				throw new FileNotFoundException( $"Clip file missing: {_session.ClipPath}" );
			}

			string clipJson = _fs.ReadAllText( _session.ClipPath );
			if ( string.IsNullOrWhiteSpace( clipJson ) )
			{
				throw new EndOfStreamException( "Clip JSON is empty (partial write in progress)." );
			}

			var clip = RigAnimFile.Read( clipJson );
			_session.SetClip( _session.ClipPath, clipJson, clip );
			return Task.FromResult<RenderSnapshot?>( _session.BuildSnapshot() );
		}

		return Task.FromResult<RenderSnapshot?>( _session.BuildSnapshot() );
	}

	public static bool IsRetryableException( Exception ex )
	{
		if ( ex is IOException ) return true;
		if ( ex is EndOfStreamException ) return true;
		if ( ex is JsonException ) return true;
		if ( ex is FormatException ) return true;
		if ( ex is InvalidDataException ) return true;
		if ( ex.InnerException != null && IsRetryableException( ex.InnerException ) ) return true;
		return false;
	}

	void EnsureWatcherForPath( string fullPath )
	{
		if ( !_enableRealFileSystemWatcher ) return;
		try
		{
			string? dir = Path.GetDirectoryName( fullPath );
			if ( string.IsNullOrEmpty( dir ) || !_fs.DirectoryExists( dir ) ) return;

			string normDir = NormalizePath( dir );
			lock ( _watchersLock )
			{
				if ( _fileSystemWatchers.ContainsKey( normDir ) ) return;

				var fsw = new FileSystemWatcher( normDir )
				{
					NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
					EnableRaisingEvents = true
				};

				fsw.Changed += ( s, e ) => NotifyFileSystemChange( e.FullPath, e.ChangeType );
				fsw.Created += ( s, e ) => NotifyFileSystemChange( e.FullPath, e.ChangeType );
				fsw.Deleted += ( s, e ) => NotifyFileSystemChange( e.FullPath, e.ChangeType );
				fsw.Renamed += ( s, e ) =>
				{
					NotifyFileSystemChange( e.OldFullPath, WatcherChangeTypes.Deleted );
					NotifyFileSystemChange( e.FullPath, WatcherChangeTypes.Created );
				};

				_fileSystemWatchers[normDir] = fsw;
			}
		}
		catch { }
	}

	public void WaitForPendingJobs( TimeSpan? timeout = null )
	{
		Task[] tasks;
		lock ( _jobsLock )
		{
			tasks = _runningTasks.Values.ToArray();
		}
		if ( tasks.Length > 0 )
		{
			Task.WaitAll( tasks, timeout ?? TimeSpan.FromSeconds( 5 ) );
		}
	}

	public void CancelActiveJobs()
	{
		lock ( _jobsLock )
		{
			foreach ( var cts in _activeCts.Values )
			{
				try { cts.Cancel(); } catch { }
			}
		}
	}

	public void Dispose()
	{
		if ( _isDisposed ) return;
		_isDisposed = true;

		_disposeCts.Cancel();
		_debounceScheduler.Cancel();
		if ( _debounceScheduler is IDisposable d ) d.Dispose();

		CancelActiveJobs();

		lock ( _watchersLock )
		{
			foreach ( var w in _fileSystemWatchers.Values )
			{
				try
				{
					w.EnableRaisingEvents = false;
					w.Dispose();
				}
				catch { }
			}
			_fileSystemWatchers.Clear();
		}

		_disposeCts.Dispose();
	}
}
