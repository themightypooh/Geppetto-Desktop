#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Effigy;
using Marionette.Desktop;

internal static class PreferencesAutosaveTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "preferences-autosave", name, test );

		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new InvalidOperationException( message );
		}

		string CreateTestDir( string subName )
		{
			string dir = Path.Combine( settings.WorkDirectory, "pref-auto-" + Guid.NewGuid().ToString( "N" ), subName );
			Directory.CreateDirectory( dir );
			return dir;
		}

		// 1. preferences atomic save and load round-trip
		Check( "preferences atomic save and load round-trip", () =>
		{
			string dir = CreateTestDir( "roundtrip" );
			string prefPath = Path.Combine( dir, "preferences.json" );

			var pathEnv = new DesktopPathEnvironment(
				getEnv: name => name == "XDG_CONFIG_HOME" ? dir : null,
				getHome: () => dir
			);
			var service = new PreferencesService( pathEnv );

			var prefs = new DesktopPreferences
			{
				Exposure = 1.45f,
				Snap = new SnapPreferences
				{
					TranslationEnabled = true,
					TranslationStep = 2.5f,
					RotationEnabled = true,
					RotationDegrees = 30.0f,
					ScaleEnabled = true,
					ScaleStep = 0.25f
				},
				Playback = new PlaybackPreferences
				{
					DefaultFps = 60.0f,
					LoopPlayback = false
				},
				Autosave = new AutosavePreferences
				{
					Enabled = true,
					IntervalSeconds = 120.0,
					MaxRecoverySnapshots = 15,
					RetentionDays = 14.0
				}
			};

			string projectA = Path.Combine( dir, "projectA.geppetto-project.json" );
			string projectB = Path.Combine( dir, "projectB.geppetto-project.json" );
			prefs.AddRecentProject( projectA, "Project Alpha" );
			prefs.AddRecentProject( projectB, "Project Beta" );

			bool saved = service.Save( prefs, prefPath );
			Assert( saved, "Preferences Save should return true." );
			Assert( File.Exists( prefPath ), "Preferences file must exist on disk after save." );

			// Ensure atomic temp file was cleaned up
			var tmpFiles = Directory.GetFiles( dir, "*.tmp" );
			Assert( tmpFiles.Length == 0, "Atomic temporary files must be cleaned up." );

			var loaded = service.Load( prefPath );
			Assert( Math.Abs( loaded.Exposure - 1.45f ) < 0.001f, "Exposure should match." );
			Assert( loaded.Snap.TranslationEnabled == true, "Snap TranslationEnabled should match." );
			Assert( Math.Abs( loaded.Snap.TranslationStep - 2.5f ) < 0.001f, "TranslationStep should match." );
			Assert( loaded.Snap.RotationEnabled == true, "RotationEnabled should match." );
			Assert( Math.Abs( loaded.Snap.RotationDegrees - 30.0f ) < 0.001f, "RotationDegrees should match." );
			Assert( loaded.Snap.ScaleEnabled == true, "ScaleEnabled should match." );
			Assert( Math.Abs( loaded.Snap.ScaleStep - 0.25f ) < 0.001f, "ScaleStep should match." );

			Assert( Math.Abs( loaded.Playback.DefaultFps - 60.0f ) < 0.001f, "DefaultFps should match." );
			Assert( loaded.Playback.LoopPlayback == false, "LoopPlayback should match." );

			Assert( loaded.Autosave.Enabled == true, "Autosave Enabled should match." );
			Assert( Math.Abs( loaded.Autosave.IntervalSeconds - 120.0 ) < 0.001, "Autosave IntervalSeconds should match." );
			Assert( loaded.Autosave.MaxRecoverySnapshots == 15, "MaxRecoverySnapshots should match." );
			Assert( Math.Abs( loaded.Autosave.RetentionDays - 14.0 ) < 0.001, "RetentionDays should match." );

			Assert( loaded.RecentProjects.Count == 2, "Recent projects count should match." );
			Assert( loaded.RecentProjects[0].Name == "Project Beta", "Latest project should be first (MRU)." );
			Assert( loaded.RecentProjects[1].Name == "Project Alpha", "Earlier project should be second." );
		} );

		// 2. XDG environment variable overrides respected
		Check( "XDG environment variable overrides respected", () =>
		{
			string fakeHome = "/custom/home";
			string fakeConfig = "/custom/xdg_config";
			string fakeCache = "/custom/xdg_cache";
			string fakeState = "/custom/xdg_state";

			var envDict = new Dictionary<string, string>
			{
				["HOME"] = fakeHome,
				["XDG_CONFIG_HOME"] = fakeConfig,
				["XDG_CACHE_HOME"] = fakeCache,
				["XDG_STATE_HOME"] = fakeState
			};

			var pathEnvWithOverrides = new DesktopPathEnvironment(
				getEnv: name => envDict.TryGetValue( name, out var val ) ? val : null,
				getHome: () => fakeHome
			);

			Assert( pathEnvWithOverrides.GetConfigDirectory() == Path.Combine( fakeConfig, "geppetto" ),
				"Config dir should respect XDG_CONFIG_HOME." );
			Assert( pathEnvWithOverrides.GetCacheDirectory() == Path.Combine( fakeCache, "geppetto" ),
				"Cache dir should respect XDG_CACHE_HOME." );
			Assert( pathEnvWithOverrides.GetStateDirectory() == Path.Combine( fakeState, "geppetto" ),
				"State dir should respect XDG_STATE_HOME." );
			Assert( pathEnvWithOverrides.GetRecoveriesDirectory() == Path.Combine( fakeState, "geppetto", "recoveries" ),
				"Recoveries dir should be within state dir." );
			Assert( pathEnvWithOverrides.GetPreferencesFilePath() == Path.Combine( fakeConfig, "geppetto", "preferences.json" ),
				"Preferences file path should be within config dir." );

			// Test fallback when XDG environment variables are unset
			var envEmpty = new Dictionary<string, string>
			{
				["HOME"] = fakeHome
			};
			var pathEnvFallback = new DesktopPathEnvironment(
				getEnv: name => envEmpty.TryGetValue( name, out var val ) ? val : null,
				getHome: () => fakeHome
			);

			if ( !OperatingSystem.IsWindows() )
			{
				Assert( pathEnvFallback.GetConfigDirectory() == Path.Combine( fakeHome, ".config", "geppetto" ),
					"Config fallback should use ~/.config/geppetto on POSIX." );
				Assert( pathEnvFallback.GetCacheDirectory() == Path.Combine( fakeHome, ".cache", "geppetto" ),
					"Cache fallback should use ~/.cache/geppetto on POSIX." );
				Assert( pathEnvFallback.GetStateDirectory() == Path.Combine( fakeHome, ".local", "state", "geppetto" ),
					"State fallback should use ~/.local/state/geppetto on POSIX." );
			}
		} );

		// 3. corrupt preferences file restores safe defaults gracefully
		Check( "corrupt preferences file restores safe defaults gracefully", () =>
		{
			string dir = CreateTestDir( "corrupt" );
			string prefPath = Path.Combine( dir, "preferences.json" );

			File.WriteAllText( prefPath, "{ \"exposure\": \"not_a_valid_float\", broken json ::: " );

			var diagnostics = new List<string>();
			var service = new PreferencesService( logDiagnostic: msg => diagnostics.Add( msg ) );

			var loaded = service.Load( prefPath );

			Assert( loaded != null, "Load should return safe default object on corrupted file." );
			Assert( Math.Abs( loaded!.Exposure - 1.0f ) < 0.001f, "Default exposure should be 1.0f." );
			Assert( loaded.Autosave.Enabled == true, "Default autosave enabled should be true." );
			Assert( loaded.Snap.TranslationStep == 1.0f, "Default translation step should be 1.0f." );
			Assert( diagnostics.Count > 0, "Corrupt file must log a diagnostic message." );
			Assert( diagnostics[0].Contains( "Corrupted preferences file" ), "Diagnostic must explain corruption." );
		} );

		// 4. bad values in preferences are sanitized
		Check( "bad values in preferences are sanitized", () =>
		{
			var diagnostics = new List<string>();
			var prefs = new DesktopPreferences
			{
				Exposure = -5.0f,
				Snap = new SnapPreferences
				{
					TranslationStep = -2.0f,
					RotationDegrees = 0f,
					ScaleStep = float.NaN
				},
				Playback = new PlaybackPreferences
				{
					DefaultFps = -30f
				},
				Autosave = new AutosavePreferences
				{
					IntervalSeconds = -10.0,
					MaxRecoverySnapshots = -5,
					RetentionDays = -1.0
				}
			};

			prefs.ValidateAndSanitize( msg => diagnostics.Add( msg ) );

			Assert( Math.Abs( prefs.Exposure - 1.0f ) < 0.001f, "Invalid exposure must be reset to 1.0." );
			Assert( Math.Abs( prefs.Snap.TranslationStep - 1.0f ) < 0.001f, "Invalid translation step must be reset to 1.0." );
			Assert( Math.Abs( prefs.Snap.RotationDegrees - 15.0f ) < 0.001f, "Invalid rotation degrees must be reset to 15.0." );
			Assert( Math.Abs( prefs.Snap.ScaleStep - 0.1f ) < 0.001f, "Invalid scale step must be reset to 0.1." );
			Assert( Math.Abs( prefs.Playback.DefaultFps - 30.0f ) < 0.001f, "Invalid DefaultFps must be reset to 30.0." );
			Assert( Math.Abs( prefs.Autosave.IntervalSeconds - 60.0 ) < 0.001, "Invalid IntervalSeconds must be reset to 60.0." );
			Assert( prefs.Autosave.MaxRecoverySnapshots == 20, "Invalid MaxRecoverySnapshots must be reset to 20." );
			Assert( Math.Abs( prefs.Autosave.RetentionDays - 7.0 ) < 0.001, "Invalid RetentionDays must be reset to 7.0." );
			Assert( diagnostics.Count >= 7, "Diagnostics should be logged for each sanitized property." );
		} );

		// 5. missing or readonly preferences file handled safely
		Check( "missing or readonly preferences file handled safely", () =>
		{
			string dir = CreateTestDir( "missing" );
			string nonExistentPath = Path.Combine( dir, "does_not_exist.json" );

			var service = new PreferencesService();
			var loaded = service.Load( nonExistentPath );

			Assert( loaded != null, "Load should return defaults for non-existent file." );
			Assert( loaded!.Exposure == 1.0f, "Should have default settings." );

			// Simulate read-only write failure
			var diagnostics = new List<string>();
			var faultFs = new PrefFaultInjectingFileSystem( PhysicalFileSystem.Instance )
			{
				FailOpenWrite = true
			};
			var faultService = new PreferencesService( fileSystem: faultFs, logDiagnostic: msg => diagnostics.Add( msg ) );

			bool saveResult = faultService.Save( loaded, Path.Combine( dir, "readonly.json" ) );
			Assert( !saveResult, "Save should return false on IO failure." );
			Assert( diagnostics.Count > 0, "Diagnostic should be logged on save failure." );
		} );

		// 6. recent projects portable path and bounded ordering
		Check( "recent projects portable path and bounded ordering", () =>
		{
			var prefs = new DesktopPreferences();
			prefs.AddRecentProject( "/path/to/project1", "Proj 1", maxEntries: 3 );
			prefs.AddRecentProject( "/path/to/project2", "Proj 2", maxEntries: 3 );
			prefs.AddRecentProject( "/path/to/project3", "Proj 3", maxEntries: 3 );

			Assert( prefs.RecentProjects.Count == 3, "Should have 3 projects." );
			Assert( prefs.RecentProjects[0].Name == "Proj 3", "Most recent should be Proj 3." );

			// Re-adding project 1 should bump it to front
			prefs.AddRecentProject( "/path/to/project1", "Proj 1 Bumped", maxEntries: 3 );
			Assert( prefs.RecentProjects.Count == 3, "Count should still be 3 after bump." );
			Assert( prefs.RecentProjects[0].Name == "Proj 1 Bumped", "Proj 1 should now be at index 0." );
			Assert( prefs.RecentProjects[1].Name == "Proj 3", "Proj 3 should now be at index 1." );

			// Adding project 4 should drop the oldest (Proj 2)
			prefs.AddRecentProject( "/path/to/project4", "Proj 4", maxEntries: 3 );
			Assert( prefs.RecentProjects.Count == 3, "Count should be bounded at maxEntries (3)." );
			Assert( prefs.RecentProjects.All( p => p.Name != "Proj 2" ), "Oldest project Proj 2 should have been evicted." );
		} );

		// 7. autosave scheduler triggers on dirty session
		Check( "autosave scheduler triggers on dirty session", () =>
		{
			string dir = CreateTestDir( "dirty_trigger" );
			string recoveryDir = Path.Combine( dir, "recoveries" );
			string docPath = Path.Combine( dir, "cube.effigy" );

			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 10f } } );
			studio.Rebuild();

			var session = DocumentSession.CreateEffigy( docPath, studio );
			session.History.Execute( new DelegateEditCommand( "dirty edit",
				() => ((PrimitiveFeature)studio.Features[0]).SizeX.Value = 20f,
				() => ((PrimitiveFeature)studio.Features[0]).SizeX.Value = 10f
			) );

			Assert( session.IsDirty, "Session must be dirty before autosave." );

			using var scheduler = new AutosaveScheduler( recoveryDirectory: recoveryDir );
			var result = scheduler.TriggerAutosaveAsync( session ).GetAwaiter().GetResult();

			Assert( result.IsSuccess, $"Autosave failed: {result.Message}" );
			Assert( result.Status == AutosaveStatus.Success, "Status must be Success." );
			Assert( result.Metadata != null, "Metadata must not be null." );
			Assert( Directory.Exists( recoveryDir ), "Recovery directory must exist." );

			var staged = AutosaveScheduler.DiscoverRecoveries( recoveryDir );
			Assert( staged.Count == 1, $"Expected 1 staged recovery, found {staged.Count}." );
			Assert( staged[0].DocumentId == session.DocumentId, "Staged recovery document ID must match session." );
			Assert( staged[0].Revision == session.Revision, "Staged recovery revision must match session revision." );
		} );

		// 8. autosave scheduler skips clean session
		Check( "autosave scheduler skips clean session", () =>
		{
			string dir = CreateTestDir( "clean_skip" );
			string recoveryDir = Path.Combine( dir, "recoveries" );
			string docPath = Path.Combine( dir, "clean.effigy" );

			var studio = new PartStudio();
			var session = DocumentSession.CreateEffigy( docPath, studio );

			Assert( !session.IsDirty, "Session must be clean." );

			using var scheduler = new AutosaveScheduler( recoveryDirectory: recoveryDir );
			var result = scheduler.TriggerAutosaveAsync( session ).GetAwaiter().GetResult();

			Assert( result.Status == AutosaveStatus.SkippedClean, "Autosave should skip clean session." );
			var staged = AutosaveScheduler.DiscoverRecoveries( recoveryDir );
			Assert( staged.Count == 0, "No recovery snapshots should be generated for clean session." );
		} );

		// 9. autosave never clears session dirty state
		Check( "autosave never clears session dirty state", () =>
		{
			string dir = CreateTestDir( "dirty_preservation" );
			string recoveryDir = Path.Combine( dir, "recoveries" );
			string docPath = Path.Combine( dir, "model.effigy" );

			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 5f } } );
			studio.Rebuild();

			var session = DocumentSession.CreateEffigy( docPath, studio );
			session.History.Execute( new DelegateEditCommand( "modify",
				() => ((PrimitiveFeature)studio.Features[0]).SizeX.Value = 15f,
				() => ((PrimitiveFeature)studio.Features[0]).SizeX.Value = 5f
			) );

			long dirtyRev = session.Revision;
			Assert( session.IsDirty, "Session must be dirty before autosave." );

			using var scheduler = new AutosaveScheduler( recoveryDirectory: recoveryDir );
			var result = scheduler.TriggerAutosaveAsync( session ).GetAwaiter().GetResult();

			Assert( result.IsSuccess, "Autosave must succeed." );
			Assert( session.IsDirty, "Session MUST REMAIN DIRTY after autosave snapshot." );
			Assert( session.Revision == dirtyRev, "Session revision must be unchanged." );
		} );

		// 10. recovery discovery finds staged recoveries
		Check( "recovery discovery finds staged recoveries", () =>
		{
			string dir = CreateTestDir( "discovery" );
			string recoveryDir = Path.Combine( dir, "recoveries" );
			string docPath = Path.Combine( dir, "anim.riganim" );

			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 20 };
			var session = DocumentSession.CreateEffigy( docPath, new PartStudio(), clip: clip );

			using var scheduler = new AutosaveScheduler( recoveryDirectory: recoveryDir );

			// Snapshot 1
			session.BumpRevision();
			session.History.Execute( new DelegateEditCommand( "rev2", () => { }, () => { } ) );
			scheduler.TriggerAutosaveAsync( session ).GetAwaiter().GetResult();

			// Snapshot 2
			session.BumpRevision();
			session.History.Execute( new DelegateEditCommand( "rev3", () => { }, () => { } ) );
			scheduler.TriggerAutosaveAsync( session ).GetAwaiter().GetResult();

			var recoveries = AutosaveScheduler.DiscoverRecoveries( recoveryDir );
			Assert( recoveries.Count == 2, $"Expected 2 recoveries, found {recoveries.Count}." );

			// Ordered descending by timestamp / revision
			Assert( recoveries[0].Revision >= recoveries[1].Revision, "Recoveries should be ordered descending." );
			Assert( recoveries[0].DocumentId == session.DocumentId, "Document ID must match." );
		} );

		// 11. recovery retention policy deletes only expired snapshots
		Check( "recovery retention policy deletes only expired snapshots", () =>
		{
			string dir = CreateTestDir( "retention" );
			string recoveryDir = Path.Combine( dir, "recoveries" );
			Directory.CreateDirectory( recoveryDir );

			// Create 4 synthetic recovery metadata and payload files for docA
			string docA = "doc_A";
			var now = DateTimeOffset.UtcNow;

			for ( int i = 1; i <= 4; i++ )
			{
				string snapId = $"{docA}_rev{i}";
				string payloadName = $"{snapId}.payload.effigy";
				var meta = new RecoverySnapshotMetadata
				{
					SchemaVersion = 1,
					SnapshotId = snapId,
					DocumentId = docA,
					SourcePath = "/models/model.effigy",
					SourceKind = DocumentSourceKind.Effigy,
					Revision = i,
					TimestampUtc = now.AddMinutes( i * 10 ), // snap 4 is newest, snap 1 is oldest
					PayloadFileName = payloadName
				};

				File.WriteAllText( Path.Combine( recoveryDir, $"{snapId}.recovery.json" ),
					JsonSerializer.Serialize( meta ) );
				File.WriteAllText( Path.Combine( recoveryDir, payloadName ), $"payload data {i}" );
			}

			// Also create an unowned user file in the directory
			string unownedFile = Path.Combine( recoveryDir, "user_notes.txt" );
			File.WriteAllText( unownedFile, "Important project notes." );

			// Retention policy: maxPerDocument = 2
			var purgeResult = AutosaveScheduler.PurgeExpiredRecoveries(
				recoveryDir,
				maxPerDocument: 2,
				maxAge: TimeSpan.FromDays( 7 )
			);

			Assert( purgeResult.PurgedCount == 2, $"Expected 2 purged, got {purgeResult.PurgedCount}." );
			Assert( purgeResult.RetainedCount == 2, $"Expected 2 retained, got {purgeResult.RetainedCount}." );

			// Check that oldest snapshots (1 and 2) are deleted
			Assert( !File.Exists( Path.Combine( recoveryDir, $"{docA}_rev1.recovery.json" ) ), "Snap 1 meta should be deleted." );
			Assert( !File.Exists( Path.Combine( recoveryDir, $"{docA}_rev1.payload.effigy" ) ), "Snap 1 payload should be deleted." );
			Assert( !File.Exists( Path.Combine( recoveryDir, $"{docA}_rev2.recovery.json" ) ), "Snap 2 meta should be deleted." );
			Assert( !File.Exists( Path.Combine( recoveryDir, $"{docA}_rev2.payload.effigy" ) ), "Snap 2 payload should be deleted." );

			// Check that newest snapshots (3 and 4) are retained
			Assert( File.Exists( Path.Combine( recoveryDir, $"{docA}_rev3.recovery.json" ) ), "Snap 3 meta should be retained." );
			Assert( File.Exists( Path.Combine( recoveryDir, $"{docA}_rev3.payload.effigy" ) ), "Snap 3 payload should be retained." );
			Assert( File.Exists( Path.Combine( recoveryDir, $"{docA}_rev4.recovery.json" ) ), "Snap 4 meta should be retained." );
			Assert( File.Exists( Path.Combine( recoveryDir, $"{docA}_rev4.payload.effigy" ) ), "Snap 4 payload should be retained." );

			// Unowned file must remain untouched
			Assert( File.Exists( unownedFile ), "Unowned file in recovery dir must not be deleted." );
		} );

		// 12. bounded scheduling prevents overlapping save storms
		Check( "bounded scheduling prevents overlapping save storms", () =>
		{
			string dir = CreateTestDir( "bounded" );
			string recoveryDir = Path.Combine( dir, "recoveries" );

			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 100f } } );
			var session = DocumentSession.CreateEffigy( Path.Combine( dir, "bounded.effigy" ), studio );
			session.History.Execute( new DelegateEditCommand( "edit", () => { }, () => { } ) );

			var slowFs = new DelayingFileSystem( PhysicalFileSystem.Instance, delayMs: 100 );
			var saveService = new DocumentSaveService( slowFs );
			using var scheduler = new AutosaveScheduler( saveService, recoveryDirectory: recoveryDir );

			var task1 = scheduler.TriggerAutosaveAsync( session );
			// Immediately trigger a second autosave while task1 is running
			var result2 = scheduler.TriggerAutosaveAsync( session ).GetAwaiter().GetResult();

			var result1 = task1.GetAwaiter().GetResult();

			Assert( result1.IsSuccess, "First autosave should succeed." );
			Assert( result2.Status == AutosaveStatus.SkippedBusy || result2.Status == AutosaveStatus.SkippedUnchangedRevision,
				$"Second autosave should be skipped (busy or unchanged), got: {result2.Status}" );
		} );

		// 13. recovery snapshot reconstructs session source state without altering original source
		Check( "recovery snapshot reconstructs session source state without altering original source", () =>
		{
			string dir = CreateTestDir( "reconstruct" );
			string recoveryDir = Path.Combine( dir, "recoveries" );
			string sourceFile = Path.Combine( dir, "original.effigy" );

			var initialStudio = new PartStudio();
			initialStudio.Add( new PrimitiveFeature { SizeX = { Value = 10f } } );
			initialStudio.Rebuild();
			File.WriteAllText( sourceFile, StudioDocument.Write( initialStudio ) );

			var session = DocumentSession.CreateEffigy( sourceFile, initialStudio );
			session.History.Execute( new DelegateEditCommand( "modify to 99",
				() => ((PrimitiveFeature)initialStudio.Features[0]).SizeX.Value = 99f,
				() => ((PrimitiveFeature)initialStudio.Features[0]).SizeX.Value = 10f
			) );

			using var scheduler = new AutosaveScheduler( recoveryDirectory: recoveryDir );
			var result = scheduler.TriggerAutosaveAsync( session ).GetAwaiter().GetResult();
			Assert( result.IsSuccess, "Autosave should succeed." );

			// Read staged recovery snapshot
			var saveService = new DocumentSaveService();
			var (meta, payload) = saveService.ReadRecoverySnapshot( recoveryDir, result.Metadata!.SnapshotId );
			Assert( meta.DocumentId == session.DocumentId, "DocumentId matches in recovery." );

			var reconstructedStudio = StudioDocument.Read( payload );
			reconstructedStudio.Rebuild();
			var feature = (PrimitiveFeature)reconstructedStudio.Features[0];
			Assert( Math.Abs( feature.SizeX.Value - 99f ) < 0.001f, "Recovery snapshot captured uncommitted edit (SizeX=99)." );

			// Original source file on disk must remain untouched
			var onDiskStudio = StudioDocument.ReadFile( sourceFile );
			onDiskStudio.Rebuild();
			var onDiskFeature = (PrimitiveFeature)onDiskStudio.Features[0];
			Assert( Math.Abs( onDiskFeature.SizeX.Value - 10f ) < 0.001f, "Original source file on disk was not modified." );
		} );
	}
}

internal sealed class DelayingFileSystem : IFileSystem
{
	readonly IFileSystem _inner;
	readonly int _delayMs;

	public DelayingFileSystem( IFileSystem inner, int delayMs )
	{
		_inner = inner;
		_delayMs = delayMs;
	}

	public bool FileExists( string path ) => _inner.FileExists( path );
	public bool DirectoryExists( string path ) => _inner.DirectoryExists( path );
	public void CreateDirectory( string path ) => _inner.CreateDirectory( path );
	public void WriteAllText( string path, string content ) => _inner.WriteAllText( path, content );
	public string ReadAllText( string path ) => _inner.ReadAllText( path );
	public void DeleteFile( string path ) => _inner.DeleteFile( path );
	public void MoveFile( string sourceFile, string destinationFile, bool overwrite = false )
	{
		Thread.Sleep( _delayMs );
		_inner.MoveFile( sourceFile, destinationFile, overwrite );
	}
	public void ReplaceFile( string sourceFile, string destinationFile, string? backupFile = null )
	{
		Thread.Sleep( _delayMs );
		_inner.ReplaceFile( sourceFile, destinationFile, backupFile );
	}
	public Stream OpenWrite( string path )
	{
		Thread.Sleep( _delayMs );
		return _inner.OpenWrite( path );
	}
	public IEnumerable<string> EnumerateFiles( string path, string searchPattern ) =>
		_inner.EnumerateFiles( path, searchPattern );
}

internal sealed class PrefFaultInjectingFileSystem : IFileSystem
{
	readonly IFileSystem _inner;

	public bool FailOpenWrite { get; set; }
	public bool FailReplace { get; set; }

	public PrefFaultInjectingFileSystem( IFileSystem inner )
	{
		_inner = inner;
	}

	public bool FileExists( string path ) => _inner.FileExists( path );
	public bool DirectoryExists( string path ) => _inner.DirectoryExists( path );
	public void CreateDirectory( string path ) => _inner.CreateDirectory( path );
	public void WriteAllText( string path, string content ) => _inner.WriteAllText( path, content );
	public string ReadAllText( string path ) => _inner.ReadAllText( path );
	public void DeleteFile( string path ) => _inner.DeleteFile( path );
	public void MoveFile( string sourceFile, string destinationFile, bool overwrite = false )
	{
		if ( FailReplace ) throw new IOException( "Simulated move failure." );
		_inner.MoveFile( sourceFile, destinationFile, overwrite );
	}
	public void ReplaceFile( string sourceFile, string destinationFile, string? backupFile = null )
	{
		if ( FailReplace ) throw new IOException( "Simulated replace failure." );
		_inner.ReplaceFile( sourceFile, destinationFile, backupFile );
	}
	public Stream OpenWrite( string path )
	{
		if ( FailOpenWrite ) throw new UnauthorizedAccessException( "Simulated permission denied / read-only directory." );
		return _inner.OpenWrite( path );
	}
	public IEnumerable<string> EnumerateFiles( string path, string searchPattern ) =>
		_inner.EnumerateFiles( path, searchPattern );
}
