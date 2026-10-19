#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Effigy;
using Marionette.Desktop;

internal static class DependencyWatcherTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "dependency-watcher", "debounce: 20 rapid events produce 1 rebuild", () =>
		{
			var clock = new ManualClock();
			var scheduler = new ManualDebounceScheduler();
			var mockFs = new InMemoryFileSystem();
			string path = "/project/models/box.effigy";
			mockFs.WriteAllText( path, "effigy 1\norigin 0,0,0\n" );

			int rebuildCount = 0;
			using var watcher = new DependencyWatcher(
				fileSystem: mockFs,
				clock: clock,
				debounceScheduler: scheduler,
				debounceWindow: TimeSpan.FromMilliseconds( 50 )
			);
			watcher.AddDependency( path, DependencyKind.SourceModel );

			watcher.CustomRebuilder = ( job, ct ) =>
			{
				Interlocked.Increment( ref rebuildCount );
				return Task.FromResult<RenderSnapshot?>( null );
			};

			// Fire 20 rapid notifications
			for ( int i = 0; i < 20; i++ )
			{
				watcher.NotifyFileSystemChange( path, WatcherChangeTypes.Changed );
			}

			Assert( scheduler.HasPending, "Debounce scheduler should have pending callback." );
			Assert( rebuildCount == 0, "No rebuild should execute prior to debounce trigger." );

			// Trigger debounce once
			scheduler.Trigger();
			watcher.WaitForPendingJobs();

			Assert( rebuildCount == 1, $"Expected exactly 1 rebuild for 20 rapid notifications, got {rebuildCount}" );
		} );

		registry.Add( "dependency-watcher", "out-of-order job completion: newer generation wins, older discarded", () =>
		{
			var clock = new ManualClock();
			var scheduler = new ImmediateDebounceScheduler();
			var mockFs = new InMemoryFileSystem();
			string path = "/project/models/hero.effigy";
			mockFs.WriteAllText( path, "effigy 1\n" );

			var studio = new PartStudio();
			var session = DocumentSession.CreateEffigy( path, studio );

			using var watcher = new DependencyWatcher(
				session: session,
				fileSystem: mockFs,
				clock: clock,
				debounceScheduler: scheduler
			);
			watcher.AddDependency( path, DependencyKind.SourceModel );

			var tcs1 = new TaskCompletionSource<RenderSnapshot?>();
			var tcs2 = new TaskCompletionSource<RenderSnapshot?>();

			int jobCounter = 0;
			watcher.CustomRebuilder = ( job, ct ) =>
			{
				int id = Interlocked.Increment( ref jobCounter );
				if ( id == 1 ) return tcs1.Task;
				if ( id == 2 ) return tcs2.Task;
				return Task.FromResult<RenderSnapshot?>( null );
			};

			// Fire first event -> Job 1 (generation 1)
			watcher.NotifyFileSystemChange( path, WatcherChangeTypes.Changed );

			// Fire second event -> Job 2 (generation 2)
			watcher.NotifyFileSystemChange( path, WatcherChangeTypes.Changed );

			var mesh1 = CreateEmptyMesh();
			var snap1 = new RenderSnapshot( session.DocumentId, session.Revision, mesh1 );

			var mesh2 = CreateEmptyMesh();
			var snap2 = new RenderSnapshot( session.DocumentId, session.Revision, mesh2 );

			bool job1Discarded = false;
			watcher.StaleResultDiscarded += ( s, e ) =>
			{
				if ( e.JobGeneration == 1 ) job1Discarded = true;
			};

			// Complete Job 2 first!
			tcs2.SetResult( snap2 );
			watcher.WaitForPendingJobs();

			Assert( watcher.LatestCompletedGeneration == 2, $"Expected generation 2 completed, got {watcher.LatestCompletedGeneration}" );
			Assert( session.CurrentSnapshot == snap2, "Session should hold snap2 from newer generation." );

			// Now complete Job 1 afterwards!
			tcs1.SetResult( snap1 );
			watcher.WaitForPendingJobs();

			Assert( job1Discarded, "Job 1 should have been discarded as stale because generation 2 completed earlier." );
			Assert( session.CurrentSnapshot == snap2, "Session must continue to hold snap2, not overwritten by stale snap1." );
		} );

		registry.Add( "dependency-watcher", "dirty conflict: dirty local edits preserved", () =>
		{
			var clock = new ManualClock();
			var scheduler = new ImmediateDebounceScheduler();
			var mockFs = new InMemoryFileSystem();
			string path = "/project/models/part.effigy";

			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 12f } } );
			studio.Rebuild();
			mockFs.WriteAllText( path, StudioDocument.Write( studio ) );

			var session = DocumentSession.CreateEffigy( path, studio );

			// Make local edit in session
			session.History.Execute( new DelegateEditCommand(
				"modify size",
				() => ((PrimitiveFeature)studio.Features[0]).SizeX.Value = 999f,
				() => ((PrimitiveFeature)studio.Features[0]).SizeX.Value = 12f
			) );

			Assert( session.IsDirty, "Session must be dirty after edit." );

			using var watcher = new DependencyWatcher(
				session: session,
				fileSystem: mockFs,
				clock: clock,
				debounceScheduler: scheduler
			);
			watcher.AddDependency( path, DependencyKind.SourceModel );

			bool conflictReported = false;
			string? conflictPath = null;
			watcher.ConflictDetected += ( s, e ) =>
			{
				conflictReported = true;
				conflictPath = e.ChangedPath;
			};

			// External file change arrives
			mockFs.WriteAllText( path, "effigy 1\norigin 10,10,10\n" );
			watcher.NotifyFileSystemChange( path, WatcherChangeTypes.Changed );
			watcher.WaitForPendingJobs();

			Assert( conflictReported, "Conflict notification must be produced when local session has dirty edits." );
			Assert( conflictPath == path, $"Conflict path should be {path}, got {conflictPath}" );
			Assert( session.IsDirty, "Session must remain dirty; dirty state must not be cleared or overwritten." );
			Assert( Math.Abs( ((PrimitiveFeature)studio.Features[0]).SizeX.Value - 999f ) < 0.001f,
				"Authored data in DocumentSession must be left completely untouched!" );
		} );

		registry.Add( "dependency-watcher", "own-save echo suppressed", () =>
		{
			var clock = new ManualClock();
			var scheduler = new ImmediateDebounceScheduler();
			var mockFs = new InMemoryFileSystem();
			string path = "/project/models/save_target.effigy";
			mockFs.WriteAllText( path, "effigy 1\n" );

			var studio = new PartStudio();
			var session = DocumentSession.CreateEffigy( path, studio );

			int rebuildCount = 0;
			using var watcher = new DependencyWatcher(
				session: session,
				fileSystem: mockFs,
				clock: clock,
				debounceScheduler: scheduler
			);
			watcher.AddDependency( path, DependencyKind.SourceModel );
			watcher.ReloadStarted += ( s, e ) => Interlocked.Increment( ref rebuildCount );

			// 1. Temporary atomic save file is ignored
			string tempSaveFile = "/project/models/.save_target.effigy.84a7e930.tmp";
			watcher.NotifyFileSystemChange( tempSaveFile, WatcherChangeTypes.Created );
			watcher.WaitForPendingJobs();
			Assert( rebuildCount == 0, "Temporary save files must be ignored." );

			// 2. Events inside SuppressEcho scope are ignored
			using ( watcher.SuppressEcho( path ) )
			{
				watcher.NotifyFileSystemChange( path, WatcherChangeTypes.Changed );
				watcher.WaitForPendingJobs();
			}
			Assert( rebuildCount == 0, "Changes during own-save scope must be suppressed." );

			// 3. Events within post-save window are also suppressed
			watcher.NotifyFileSystemChange( path, WatcherChangeTypes.Changed );
			watcher.WaitForPendingJobs();
			Assert( rebuildCount == 0, "Changes within post-save echo window must be suppressed." );

			// 4. After advancing clock past suppression window, external events trigger rebuild
			clock.Advance( TimeSpan.FromSeconds( 2 ) );
			watcher.NotifyFileSystemChange( path, WatcherChangeTypes.Changed );
			watcher.WaitForPendingJobs();
			Assert( rebuildCount == 1, "External change after echo window must trigger rebuild." );
		} );

		registry.Add( "dependency-watcher", "delete and recreate file handled", () =>
		{
			var clock = new ManualClock();
			var scheduler = new ImmediateDebounceScheduler();
			var mockFs = new InMemoryFileSystem();
			string path = "/project/models/recreate_target.effigy";

			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 20f } } );
			studio.Rebuild();
			mockFs.WriteAllText( path, StudioDocument.Write( studio ) );

			var session = DocumentSession.CreateEffigy( path, studio );
			var initialSnapshot = session.BuildSnapshot();
			session.TryPromoteSnapshot( initialSnapshot );

			using var watcher = new DependencyWatcher(
				session: session,
				fileSystem: mockFs,
				clock: clock,
				debounceScheduler: scheduler
			);
			watcher.AddDependency( path, DependencyKind.SourceModel );

			// File is deleted
			mockFs.DeleteFile( path );
			watcher.NotifyFileSystemChange( path, WatcherChangeTypes.Deleted );
			watcher.WaitForPendingJobs();

			// Last good snapshot must remain visible through reload failure!
			Assert( session.CurrentSnapshot == initialSnapshot, "Last good snapshot must stay visible when file is deleted." );

			// File is recreated with new contents
			var newStudio = new PartStudio();
			newStudio.Add( new PrimitiveFeature { SizeX = { Value = 80f } } );
			newStudio.Rebuild();
			mockFs.WriteAllText( path, StudioDocument.Write( newStudio ) );

			watcher.NotifyFileSystemChange( path, WatcherChangeTypes.Created );
			watcher.WaitForPendingJobs();

			Assert( session.CurrentSnapshot != initialSnapshot, "Snapshot must be updated after file recreate." );
			Assert( session.CurrentSnapshot != null, "CurrentSnapshot must not be null." );
		} );

		registry.Add( "dependency-watcher", "partial write retry", () =>
		{
			var clock = new ManualClock();
			var scheduler = new ImmediateDebounceScheduler();
			var mockFs = new InMemoryFileSystem();
			string path = "/project/models/partial.effigy";

			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 30f } } );
			studio.Rebuild();
			mockFs.WriteAllText( path, StudioDocument.Write( studio ) );

			var session = DocumentSession.CreateEffigy( path, studio );
			var initialSnapshot = session.BuildSnapshot();
			session.TryPromoteSnapshot( initialSnapshot );

			int readAttempts = 0;
			mockFs.OnReadFile = p =>
			{
				if ( p == path )
				{
					readAttempts++;
					if ( readAttempts <= 2 )
					{
						// Simulate partial write / file lock
						throw new IOException( "File in use / partial write in progress." );
					}
				}
			};

			using var watcher = new DependencyWatcher(
				session: session,
				fileSystem: mockFs,
				clock: clock,
				debounceScheduler: scheduler
			)
			{
				MaxRetries = 4,
				RetryDelay = TimeSpan.Zero // instant retry for tests
			};
			watcher.AddDependency( path, DependencyKind.SourceModel );

			watcher.NotifyFileSystemChange( path, WatcherChangeTypes.Changed );
			watcher.WaitForPendingJobs();

			Assert( readAttempts >= 3, $"Expected at least 3 read attempts due to retry, got {readAttempts}" );
			Assert( watcher.LatestResult != null && watcher.LatestResult.IsSuccess,
				$"Expected success after partial write retries, got {watcher.LatestResult?.Status}" );
		} );

		registry.Add( "dependency-watcher", "watcher cancellation and disposal during active job", () =>
		{
			var clock = new ManualClock();
			var scheduler = new ImmediateDebounceScheduler();
			var mockFs = new InMemoryFileSystem();
			string path = "/project/models/active_job.effigy";
			mockFs.WriteAllText( path, "effigy 1\n" );

			var studio = new PartStudio();
			var session = DocumentSession.CreateEffigy( path, studio );

			var watcher = new DependencyWatcher(
				session: session,
				fileSystem: mockFs,
				clock: clock,
				debounceScheduler: scheduler
			);
			watcher.AddDependency( path, DependencyKind.SourceModel );

			var jobStarted = new TaskCompletionSource<bool>();
			var blockTcs = new TaskCompletionSource<bool>();
			bool observedCancellation = false;

			watcher.CustomRebuilder = async ( job, ct ) =>
			{
				jobStarted.TrySetResult( true );
				try
				{
					await blockTcs.Task.WaitAsync( ct );
					return null;
				}
				catch ( OperationCanceledException )
				{
					observedCancellation = true;
					throw;
				}
			};

			watcher.NotifyFileSystemChange( path, WatcherChangeTypes.Changed );

			// Wait until background job has entered execution
			jobStarted.Task.GetAwaiter().GetResult();

			// Dispose watcher while job is running
			watcher.Dispose();
			watcher.WaitForPendingJobs();

			Assert( watcher.IsDisposed, "Watcher must report IsDisposed true." );
			Assert( observedCancellation, "Active job must observe cancellation when watcher is disposed." );
			Assert( !watcher.HasPendingJobs, "Pending job entries must be cleared upon disposal." );
		} );

		registry.Add( "dependency-watcher", "clean reload updates snapshot", () =>
		{
			var clock = new ManualClock();
			var scheduler = new ImmediateDebounceScheduler();
			var mockFs = new InMemoryFileSystem();
			string path = "/project/models/clean_reload.effigy";

			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 15f } } );
			studio.Rebuild();
			mockFs.WriteAllText( path, StudioDocument.Write( studio ) );

			var session = DocumentSession.CreateEffigy( path, studio );
			var initialSnapshot = session.BuildSnapshot();
			session.TryPromoteSnapshot( initialSnapshot );
			Assert( !session.IsDirty, "Session must be clean." );

			using var watcher = new DependencyWatcher(
				session: session,
				fileSystem: mockFs,
				clock: clock,
				debounceScheduler: scheduler
			);
			watcher.AddDependency( path, DependencyKind.SourceModel );

			// External write
			var updatedStudio = new PartStudio();
			updatedStudio.Add( new PrimitiveFeature { SizeX = { Value = 75f } } );
			updatedStudio.Rebuild();
			mockFs.WriteAllText( path, StudioDocument.Write( updatedStudio ) );

			bool snapshotPromotedFired = false;
			watcher.SnapshotPromoted += ( s, e ) => snapshotPromotedFired = true;

			watcher.NotifyFileSystemChange( path, WatcherChangeTypes.Changed );
			watcher.WaitForPendingJobs();

			Assert( snapshotPromotedFired, "SnapshotPromoted event must fire on clean reload." );
			Assert( session.CurrentSnapshot != null, "CurrentSnapshot must not be null." );
			Assert( session.CurrentSnapshot != initialSnapshot, "Snapshot must be replaced by reloaded snapshot." );
			Assert( !session.IsDirty, "Session must still be clean after external reload." );
		} );

		registry.Add( "dependency-watcher", "bad replacement retains old snapshot", () =>
		{
			var clock = new ManualClock();
			var scheduler = new ImmediateDebounceScheduler();
			var mockFs = new InMemoryFileSystem();
			string path = "/project/models/bad_replacement.effigy";

			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 25f } } );
			studio.Rebuild();
			mockFs.WriteAllText( path, StudioDocument.Write( studio ) );

			var session = DocumentSession.CreateEffigy( path, studio );
			var initialSnapshot = session.BuildSnapshot();
			session.TryPromoteSnapshot( initialSnapshot );

			using var watcher = new DependencyWatcher(
				session: session,
				fileSystem: mockFs,
				clock: clock,
				debounceScheduler: scheduler
			)
			{
				MaxRetries = 1,
				RetryDelay = TimeSpan.Zero
			};
			watcher.AddDependency( path, DependencyKind.SourceModel );

			bool reloadFailedFired = false;
			watcher.ReloadFailed += ( s, e ) => reloadFailedFired = true;

			// Write completely corrupted content
			mockFs.WriteAllText( path, "NOT AN EFFIGY OR JSON %%% INVALID SYNTAX" );

			watcher.NotifyFileSystemChange( path, WatcherChangeTypes.Changed );
			watcher.WaitForPendingJobs();

			Assert( reloadFailedFired, "ReloadFailed event must fire on invalid replacement." );
			Assert( session.CurrentSnapshot == initialSnapshot, "Last good snapshot must stay visible after reload failure!" );
		} );

		registry.Add( "dependency-watcher", "watch project dependencies resolves all referenced assets", () =>
		{
			string root = Path.Combine( settings.WorkDirectory, "watch_project_deps" );
			Directory.CreateDirectory( root );
			Directory.CreateDirectory( Path.Combine( root, "models" ) );
			Directory.CreateDirectory( Path.Combine( root, "anims" ) );
			Directory.CreateDirectory( Path.Combine( root, "textures" ) );

			var mockFs = new InMemoryFileSystem();
			var resolver = new AssetPathResolver( root );

			var project = new DesktopProject
			{
				Id = "manifest_test",
				Name = "Manifest Test",
				ProjectRoot = root,
				SourceDocuments = new List<SourceDocument>
				{
					new SourceDocument( "hero_doc", new AssetReference( "hero_doc", "models/hero.effigy" ) )
				},
				Clips = new List<ProjectClip>
				{
					new ProjectClip( "run_clip", new AssetReference( "run_clip", "anims/run.riganim" ) )
				},
				MaterialOverrides = new List<MaterialOverrideRecord>
				{
					new MaterialOverrideRecord( "hero_mat", "body_slot", new MaterialDescriptor( "hero_mat", "Hero Material" )
					{
						BaseColorMap = new TextureBinding( new AssetReference( "tex1", "textures/albedo.png" ) ),
						NormalMap = new TextureBinding( new AssetReference( "tex2", "textures/normal.png" ) )
					} )
				}
			};

			using var watcher = new DependencyWatcher( fileSystem: mockFs );
			string manifestPath = Path.Combine( root, "project.geppetto-project.json" );
			watcher.WatchProject( project, resolver, manifestPath );

			Assert( watcher.IsWatching( manifestPath ), "Must watch manifest file." );
			Assert( watcher.IsWatching( Path.Combine( root, "models/hero.effigy" ) ), "Must watch source model." );
			Assert( watcher.IsWatching( Path.Combine( root, "anims/run.riganim" ) ), "Must watch clip." );
			Assert( watcher.IsWatching( Path.Combine( root, "textures/albedo.png" ) ), "Must watch albedo texture." );
			Assert( watcher.IsWatching( Path.Combine( root, "textures/normal.png" ) ), "Must watch normal texture." );
		} );
	}

	static ViewportMesh CreateEmptyMesh() => new()
	{
		Vertices = Array.Empty<float>(),
		Indices = Array.Empty<uint>(),
		Batches = Array.Empty<ViewportMesh.DrawBatch>()
	};

	static void Assert( bool condition, string message )
	{
		if ( !condition ) throw new InvalidOperationException( message );
	}
}

internal sealed class ManualClock : IWatcherClock
{
	public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
	public void Advance( TimeSpan span ) => UtcNow += span;
}

internal sealed class ManualDebounceScheduler : IDebounceScheduler
{
	Action? _pending;
	public bool HasPending => _pending != null;

	public void Schedule( TimeSpan delay, Action action )
	{
		_pending = action;
	}

	public void Cancel()
	{
		_pending = null;
	}

	public void Trigger()
	{
		var act = _pending;
		_pending = null;
		act?.Invoke();
	}
}

internal sealed class ImmediateDebounceScheduler : IDebounceScheduler
{
	public void Schedule( TimeSpan delay, Action action )
	{
		action?.Invoke();
	}

	public void Cancel() { }
}

internal sealed class InMemoryFileSystem : IFileSystem
{
	readonly Dictionary<string, string> _files = new(
		OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
	);
	readonly HashSet<string> _directories = new(
		OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
	);

	public Action<string>? OnReadFile { get; set; }

	static string Norm( string path ) => DependencyWatcher.NormalizePath( path );

	public bool FileExists( string path ) => _files.ContainsKey( Norm( path ) );
	public bool DirectoryExists( string path ) => _directories.Contains( Norm( path ) ) || true; // allow all virtual dirs
	public void CreateDirectory( string path ) => _directories.Add( Norm( path ) );

	public void WriteAllText( string path, string content )
	{
		string n = Norm( path );
		_files[n] = content;
	}

	public string ReadAllText( string path )
	{
		string n = Norm( path );
		OnReadFile?.Invoke( n );
		if ( !_files.TryGetValue( n, out var content ) )
		{
			throw new FileNotFoundException( $"File not found in InMemoryFileSystem: {n}", n );
		}
		return content;
	}

	public void DeleteFile( string path )
	{
		_files.Remove( Norm( path ) );
	}

	public void MoveFile( string sourceFile, string destinationFile, bool overwrite = false )
	{
		string s = Norm( sourceFile ), d = Norm( destinationFile );
		if ( !_files.TryGetValue( s, out var content ) )
			throw new FileNotFoundException( $"Source file not found: {s}" );
		if ( !overwrite && _files.ContainsKey( d ) )
			throw new IOException( $"Destination already exists: {d}" );
		_files[d] = content;
		_files.Remove( s );
	}

	public void ReplaceFile( string sourceFile, string destinationFile, string? backupFile = null )
	{
		string s = Norm( sourceFile ), d = Norm( destinationFile );
		if ( !_files.TryGetValue( s, out var content ) )
			throw new FileNotFoundException( $"Source file not found: {s}" );
		if ( backupFile != null && _files.TryGetValue( d, out var oldContent ) )
		{
			_files[Norm( backupFile )] = oldContent;
		}
		_files[d] = content;
		_files.Remove( s );
	}

	public Stream OpenWrite( string path )
	{
		string n = Norm( path );
		return new InMemoryWriteStream( bytes =>
		{
			_files[n] = Encoding.UTF8.GetString( bytes );
		} );
	}

	public IEnumerable<string> EnumerateFiles( string path, string searchPattern )
	{
		string n = Norm( path );
		return _files.Keys.Where( k => k.StartsWith( n, StringComparison.Ordinal ) );
	}

	sealed class InMemoryWriteStream : MemoryStream
	{
		readonly Action<byte[]> _onClosed;
		public InMemoryWriteStream( Action<byte[]> onClosed ) => _onClosed = onClosed;
		protected override void Dispose( bool disposing )
		{
			if ( disposing )
			{
				_onClosed( ToArray() );
			}
			base.Dispose( disposing );
		}
	}
}
