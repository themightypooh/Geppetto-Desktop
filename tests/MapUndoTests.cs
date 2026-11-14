#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using Effigy;
using Marionette.Desktop;

internal static class MapUndoTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		// Fixture for hammer-undo-autosave-map-commands. MapDocument
		// (hammer-map-document-model) had NOT landed when this file was written, so
		// the tests drive the undo/autosave contract through InMemoryMapUndoTarget,
		// the reference IMapUndoTarget implementation — see MapUndo.cs header.
		//
		// Not yet registered in tests/TestRegistry.cs: that file is
		// coordinator-owned and the final integrator adds the one
		// MapUndoTests.Register(registry, settings); line.
		void Check( string name, Action test ) => registry.Add( "mapundo", name, test );

		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new InvalidOperationException( message );
		}

		void AssertThrows<T>( Action action, string message ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new InvalidOperationException( message + " (threw " + ex.GetType().Name + " instead)" ); }
			throw new InvalidOperationException( message + " (did not throw)" );
		}

		string CreateTestDir( string subName )
		{
			string dir = Path.Combine( settings.WorkDirectory, "mapundo-" + Guid.NewGuid().ToString( "N" ), subName );
			Directory.CreateDirectory( dir );
			return dir;
		}

		InMemoryMapUndoTarget NewTarget( string dir, string id = "map-test" )
		{
			string mapPath = Path.Combine( dir, "room.effigy-map" );
			return new InMemoryMapUndoTarget( id, mapPath );
		}

		MapBrushState Brush( string id, float minX, float minY, float minZ, float maxX, float maxY, float maxZ, string material = "concrete/wall" ) =>
			new MapBrushState { Id = id, Min = new Vec3( minX, minY, minZ ), Max = new Vec3( maxX, maxY, maxZ ), Material = material };

		MapEntityState Entity( string id, string className, float x, float y, float z ) =>
			new MapEntityState { Id = id, ClassName = className, Origin = new Vec3( x, y, z ) };

		MapIoState Io( string id, string source, string target, string output, string input ) =>
			new MapIoState { Id = id, SourceId = source, TargetId = target, Output = output, Input = input, Parameter = "", DelaySeconds = 0, TimesToFire = -1 };

		AutosaveScheduler NewScheduler( string recoveryDir, double intervalSeconds = 60.0 )
		{
			var prefs = new DesktopPreferences
			{
				Autosave = new AutosavePreferences
				{
					Enabled = true,
					IntervalSeconds = intervalSeconds,
					MaxRecoverySnapshots = 20,
					RetentionDays = 7.0
				}
			};
			return new AutosaveScheduler( null, recoveryDir, prefs );
		}

		// 1. brush move is one undoable command, with redo
		Check( "brush move undo and redo is one command", () =>
		{
			string dir = CreateTestDir( "brush-move" );
			var target = NewTarget( dir );
			var undo = new MapUndo( target );
			target.AddBrush( Brush( "b1", 0, 0, 0, 64, 64, 64 ) );
			undo.MarkSaved();

			undo.Execute( new MoveBrushCommand( "b1", new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ), new Vec3( 16, 0, 0 ), new Vec3( 80, 64, 64 ) ) );
			Assert( target.FindBrush( "b1" )!.Min.Equals( new Vec3( 16, 0, 0 ) ), "move must apply" );
			Assert( undo.UndoCount == 1 && undo.CanUndo, "one move is one undo entry" );

			undo.Undo();
			Assert( target.FindBrush( "b1" )!.Min.Equals( new Vec3( 0, 0, 0 ) ), "undo must restore the original bounds" );
			Assert( undo.CanRedo, "undo must leave a redo entry" );

			undo.Redo();
			Assert( target.FindBrush( "b1" )!.Min.Equals( new Vec3( 16, 0, 0 ) ), "redo must re-apply the move" );
		} );

		// 2. entity add undo/redo
		Check( "entity add undo and redo", () =>
		{
			string dir = CreateTestDir( "entity-add" );
			var target = NewTarget( dir );
			var undo = new MapUndo( target );

			undo.Execute( new AddEntityCommand( Entity( "e1", "light", 0, 0, 96 ) ) );
			Assert( target.FindEntity( "e1" ) != null, "entity must exist after add" );
			undo.Undo();
			Assert( target.FindEntity( "e1" ) == null, "undo must remove the entity" );
			undo.Redo();
			Assert( target.FindEntity( "e1" ) != null && target.FindEntity( "e1" )!.ClassName == "light", "redo must restore the entity" );
		} );

		// 3. displacement set undo/redo
		Check( "displacement set undo and redo", () =>
		{
			string dir = CreateTestDir( "disp-set" );
			var target = NewTarget( dir );
			var undo = new MapUndo( target );
			target.AddBrush( Brush( "b1", 0, 0, 0, 64, 64, 0 ) );
			target.AddDisplacement( new MapDisplacementState { BrushId = "b1", FaceIndex = 0, Power = 2, Heights = new float[] { 0, 0, 0, 0 } } );

			undo.Execute( new SetDisplacementCommand( "b1", 0, new float[] { 0, 0, 0, 0 }, 2, new float[] { 4, 8, 4, 0 }, 2 ) );
			Assert( target.FindDisplacement( "b1", 0 )!.Heights.SequenceEqual( new float[] { 4, 8, 4, 0 } ), "displacement edit must apply" );
			undo.Undo();
			Assert( target.FindDisplacement( "b1", 0 )!.Heights.SequenceEqual( new float[] { 0, 0, 0, 0 } ), "undo must restore flat heights" );
			undo.Redo();
			Assert( target.FindDisplacement( "b1", 0 )!.Heights[1] == 8, "redo must re-apply the sculpted heights" );
		} );

		// 4. I/O add undo/redo
		Check( "io add undo and redo", () =>
		{
			string dir = CreateTestDir( "io-add" );
			var target = NewTarget( dir );
			var undo = new MapUndo( target );
			target.AddEntity( Entity( "btn", "func_button", 0, 0, 32 ) );
			target.AddEntity( Entity( "door", "func_door", 128, 0, 32 ) );

			undo.Execute( new AddIoCommand( Io( "io1", "btn", "door", "OnPressed", "Open" ) ) );
			Assert( target.FindIo( "io1" ) != null, "I/O must exist after add" );
			undo.Undo();
			Assert( target.FindIo( "io1" ) == null, "undo must remove the I/O connection" );
			undo.Redo();
			Assert( target.FindIo( "io1" ) != null && target.FindIo( "io1" )!.Output == "OnPressed", "redo must restore the I/O connection" );
		} );

		// 5. the desc's named test: brush move + entity add + I/O add undo in reverse order, then redo
		Check( "brush move entity add io add undo in reverse order then redo", () =>
		{
			string dir = CreateTestDir( "reverse-order" );
			var target = NewTarget( dir );
			var undo = new MapUndo( target );
			target.AddBrush( Brush( "b1", 0, 0, 0, 64, 64, 64 ) );
			target.AddEntity( Entity( "btn", "func_button", 0, 0, 32 ) );
			target.AddEntity( Entity( "door", "func_door", 128, 0, 32 ) );

			undo.Execute( new MoveBrushCommand( "b1", new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ), new Vec3( 16, 0, 0 ), new Vec3( 80, 64, 64 ) ) );
			undo.Execute( new AddEntityCommand( Entity( "e1", "light", 0, 0, 96 ) ) );
			undo.Execute( new AddIoCommand( Io( "io1", "btn", "door", "OnPressed", "Open" ) ) );
			Assert( undo.UndoCount == 3, "three edits are three undo entries" );

			undo.Undo();
			Assert( target.FindIo( "io1" ) == null && target.FindEntity( "e1" ) != null, "first undo removes the I/O only" );
			undo.Undo();
			Assert( target.FindEntity( "e1" ) == null && target.FindBrush( "b1" )!.Min.x == 16, "second undo removes the entity only" );
			undo.Undo();
			Assert( target.FindBrush( "b1" )!.Min.x == 0, "third undo restores the brush" );
			Assert( !undo.CanUndo && undo.RedoCount == 3, "all three undone, three redo entries" );

			undo.Redo();
			Assert( target.FindBrush( "b1" )!.Min.x == 16, "first redo re-applies the brush move" );
			undo.Redo();
			Assert( target.FindEntity( "e1" ) != null, "second redo re-adds the entity" );
			undo.Redo();
			Assert( target.FindIo( "io1" ) != null, "third redo re-adds the I/O connection" );
		} );

		// 6. empty undo/redo throw instead of silently doing nothing
		Check( "undo and redo on an empty stack throw", () =>
		{
			string dir = CreateTestDir( "empty-stack" );
			var undo = new MapUndo( NewTarget( dir ) );
			Assert( !undo.CanUndo && !undo.CanRedo, "fresh stack can neither undo nor redo" );
			AssertThrows<InvalidOperationException>( () => undo.Undo(), "Undo on an empty stack must throw" );
			AssertThrows<InvalidOperationException>( () => undo.Redo(), "Redo on an empty stack must throw" );
		} );

		// 7. a new edit clears redo
		Check( "new execute clears the redo stack", () =>
		{
			string dir = CreateTestDir( "redo-cleared" );
			var target = NewTarget( dir );
			var undo = new MapUndo( target );
			target.AddBrush( Brush( "b1", 0, 0, 0, 64, 64, 64 ) );
			undo.Execute( new MoveBrushCommand( "b1", new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ), new Vec3( 16, 0, 0 ), new Vec3( 80, 64, 64 ) ) );
			undo.Undo();
			Assert( undo.CanRedo, "redo must be available after undo" );
			undo.Execute( new MoveBrushCommand( "b1", new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ), new Vec3( 0, 16, 0 ), new Vec3( 64, 80, 64 ) ) );
			Assert( !undo.CanRedo && undo.RedoCount == 0, "a new edit must clear redo" );
			Assert( target.FindBrush( "b1" )!.Min.y == 16, "the new edit, not the redone one, must be live" );
		} );

		// 8. stroke dabs coalesce into one command
		Check( "displacement stroke dabs coalesce into one undo entry", () =>
		{
			string dir = CreateTestDir( "stroke-coalesce" );
			var target = NewTarget( dir );
			var undo = new MapUndo( target );
			target.AddBrush( Brush( "b1", 0, 0, 0, 64, 64, 0 ) );
			target.AddDisplacement( new MapDisplacementState { BrushId = "b1", FaceIndex = 0, Power = 2, Heights = new float[] { 0, 0, 0, 0 } } );

			undo.BeginStroke( "Sculpt displacement" );
			undo.AddDisplacementDab( "b1", 0, new float[] { 1, 0, 0, 0 }, 2 );
			undo.AddDisplacementDab( "b1", 0, new float[] { 2, 3, 0, 0 }, 2 );
			undo.AddDisplacementDab( "b1", 0, new float[] { 4, 8, 4, 0 }, 2 );
			Assert( target.FindDisplacement( "b1", 0 )!.Heights.SequenceEqual( new float[] { 4, 8, 4, 0 } ), "dabs must apply live during the stroke" );
			Assert( undo.UndoCount == 0, "dabs must not reach the undo stack mid-stroke" );
			undo.EndStroke();
			Assert( undo.UndoCount == 1, "three dabs are ONE undo entry, got " + undo.UndoCount );

			undo.Undo();
			Assert( target.FindDisplacement( "b1", 0 )!.Heights.SequenceEqual( new float[] { 0, 0, 0, 0 } ),
				"one undo must restore the pre-stroke heights, not an intermediate dab" );
			undo.Redo();
			Assert( target.FindDisplacement( "b1", 0 )!.Heights.SequenceEqual( new float[] { 4, 8, 4, 0 } ), "redo must restore the finished stroke" );
		} );

		// 9. cancelling a stroke restores the pre-stroke state and pushes nothing
		Check( "cancelled stroke restores pre-stroke state and pushes nothing", () =>
		{
			string dir = CreateTestDir( "stroke-cancel" );
			var target = NewTarget( dir );
			var undo = new MapUndo( target );
			target.AddBrush( Brush( "b1", 0, 0, 0, 64, 64, 0 ) );
			target.AddDisplacement( new MapDisplacementState { BrushId = "b1", FaceIndex = 0, Power = 2, Heights = new float[] { 0, 0, 0, 0 } } );

			undo.BeginStroke( "Sculpt displacement" );
			undo.AddDisplacementDab( "b1", 0, new float[] { 5, 5, 5, 5 }, 2 );
			undo.CancelStroke();
			Assert( target.FindDisplacement( "b1", 0 )!.Heights.SequenceEqual( new float[] { 0, 0, 0, 0 } ), "cancel must restore flat heights" );
			Assert( undo.UndoCount == 0, "a cancelled stroke pushes nothing" );
			AssertThrows<InvalidOperationException>( () => undo.EndStroke(), "EndStroke with no active stroke must throw" );
		} );

		// 10. command repetition re-applies the same delta from the current state
		Check( "repeat last move applies the same delta again", () =>
		{
			string dir = CreateTestDir( "repeat" );
			var target = NewTarget( dir );
			var undo = new MapUndo( target );
			target.AddBrush( Brush( "b1", 0, 0, 0, 64, 64, 64 ) );
			undo.Execute( new MoveBrushCommand( "b1", new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ), new Vec3( 16, 0, 0 ), new Vec3( 80, 64, 64 ) ) );
			undo.RepeatLast();
			Assert( target.FindBrush( "b1" )!.Min.x == 32, "repeat must move +16 again from the current position, got x=" + target.FindBrush( "b1" )!.Min.x );
			Assert( undo.UndoCount == 2, "the repeat is its own undo entry" );
			undo.Undo();
			Assert( target.FindBrush( "b1" )!.Min.x == 16, "undoing the repeat leaves the first move" );
		} );

		// 11. a no-op command is never pushed
		Check( "no-op move is not pushed onto the stack", () =>
		{
			string dir = CreateTestDir( "noop" );
			var target = NewTarget( dir );
			var undo = new MapUndo( target );
			target.AddBrush( Brush( "b1", 0, 0, 0, 64, 64, 64 ) );
			undo.Execute( new MoveBrushCommand( "b1", new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ), new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ) ) );
			Assert( undo.UndoCount == 0, "a move to the same bounds must not create an undo entry" );
		} );

		// 12. bounded capacity evicts the oldest entry first
		Check( "capacity evicts the oldest undo entry", () =>
		{
			string dir = CreateTestDir( "capacity" );
			var target = NewTarget( dir );
			var undo = new MapUndo( target, capacity: 2 );
			target.AddBrush( Brush( "b1", 0, 0, 0, 64, 64, 64 ) );
			for ( int i = 1; i <= 3; i++ )
				undo.Execute( new MoveBrushCommand( "b1", new Vec3( ( i - 1 ) * 16, 0, 0 ), new Vec3( 64 + ( i - 1 ) * 16, 64, 64 ),
					new Vec3( i * 16, 0, 0 ), new Vec3( 64 + i * 16, 64, 64 ) ) );
			Assert( undo.UndoCount == 2, "capacity 2 must hold 2 entries after 3 moves, got " + undo.UndoCount );
			undo.Undo();
			undo.Undo();
			Assert( target.FindBrush( "b1" )!.Min.x == 16, "after eviction, undoing twice reaches the state after the first move, not the origin" );
			Assert( !undo.CanUndo, "the evicted first move is gone for good" );
		} );

		// 13. dirty tracking: undoing back to the saved state clears dirty
		Check( "mark saved and undo-to-saved clears dirty", () =>
		{
			string dir = CreateTestDir( "dirty" );
			var target = NewTarget( dir );
			var undo = new MapUndo( target );
			target.AddBrush( Brush( "b1", 0, 0, 0, 64, 64, 64 ) );
			undo.MarkSaved();
			Assert( !undo.IsDirty && !target.IsDirty, "freshly saved map is clean" );
			undo.Execute( new MoveBrushCommand( "b1", new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ), new Vec3( 16, 0, 0 ), new Vec3( 80, 64, 64 ) ) );
			Assert( undo.IsDirty && target.IsDirty, "an edit dirties the map" );
			undo.Undo();
			Assert( !undo.IsDirty, "undoing back to the saved state clears dirty" );
		} );

		// 14. snapshot JSON round-trip via the sealed-room fixture, plus corrupt-input rejection
		Check( "sealed-room fixture loads and snapshot json round-trips", () =>
		{
			string fixture = FindFixture( "sealed-room.json" );
			string json = File.ReadAllText( fixture );
			var snapshot = MapUndoJson.Read( json );
			Assert( snapshot.Brushes.Count == 6, "sealed room must have 6 brushes, got " + snapshot.Brushes.Count );
			Assert( snapshot.Entities.Count == 2, "sealed room must have player start + light, got " + snapshot.Entities.Count );
			Assert( snapshot.Entities.Any( e => e.ClassName == "info_player_start" ), "player start must be present" );
			Assert( snapshot.Entities.Any( e => e.ClassName == "light" ), "light must be present" );

			string rewritten = MapUndoJson.Write( snapshot );
			var reloaded = MapUndoJson.Read( rewritten );
			Assert( reloaded.Brushes.Count == 6 && reloaded.Entities.Count == 2, "write/read round-trip must preserve the room" );
			Assert( reloaded.Brushes.First( b => b.Id == "brush-floor" ).Min.z == -16, "floor bounds must survive the round-trip" );

			var target = new InMemoryMapUndoTarget( snapshot.DocumentId, "" );
			target.ApplySnapshot( snapshot );
			Assert( target.Brushes.Count == 6, "applying the fixture snapshot must build the room in the target" );

			string badVersion = json.Replace( "\"SchemaVersion\": 1", "\"SchemaVersion\": 99" );
			AssertThrows<InvalidDataException>( () => MapUndoJson.Read( badVersion ), "a newer schema version must fail loudly, never load silently" );
			string duplicate = json.Replace( "\"brush-wall-east\"", "\"brush-floor\"" );
			AssertThrows<InvalidDataException>( () => MapUndoJson.Read( duplicate ), "a duplicate brush id must fail validation" );
		} );

		// 15. scheduler trigger writes the autosave next to the map + a recovery record
		Check( "scheduler trigger writes autosave next to the map", () =>
		{
			string dir = CreateTestDir( "sched-trigger" );
			string recoveryDir = Path.Combine( dir, "recoveries" );
			var target = NewTarget( dir );
			var undo = new MapUndo( target );
			undo.Execute( new AddBrushCommand( Brush( "b1", 0, 0, 0, 64, 64, 64 ) ) );
			using var scheduler = NewScheduler( recoveryDir );
			scheduler.ActiveMap = target;

			var result = scheduler.TriggerMapAutosaveAsync().GetAwaiter().GetResult();
			Assert( result.IsSuccess, "map autosave must succeed: " + result.Message );
			string autosavePath = MapAutosave.AutosaveFilePathFor( target.SourcePath, target.DocumentId, recoveryDir );
			Assert( File.Exists( autosavePath ), "autosave file must exist next to the map at " + autosavePath );
			Assert( Path.GetDirectoryName( autosavePath ) == dir, "autosave must sit beside the map, not in the recovery dir" );
			Assert( !Directory.Exists( recoveryDir ) || Directory.GetFiles( recoveryDir, "*.autosave*" ).Length == 0,
				"no autosave payload may land in the recovery directory" );

			var record = MapAutosave.TryFindRecovery( target.SourcePath, target.DocumentId, recoveryDir );
			Assert( record != null && record.Crashed, "autosave must leave a recovery record marked crashed" );
			Assert( record!.Revision == target.Revision, "recovery record must carry the autosaved revision" );

			var again = scheduler.TriggerMapAutosaveAsync().GetAwaiter().GetResult();
			Assert( again.Status == AutosaveStatus.SkippedUnchangedRevision, "same revision must not autosave twice, got " + again.Status );

			var cleanTarget = NewTarget( dir, "map-clean" );
			var cleanResult = scheduler.TriggerMapAutosaveAsync( cleanTarget ).GetAwaiter().GetResult();
			Assert( cleanResult.Status == AutosaveStatus.SkippedClean, "a clean map must be skipped, got " + cleanResult.Status );
		} );

		// 16. the scheduled timer writes the autosave after the interval
		Check( "scheduler timer writes autosave after the interval", () =>
		{
			string dir = CreateTestDir( "sched-timer" );
			string recoveryDir = Path.Combine( dir, "recoveries" );
			var target = NewTarget( dir );
			var undo = new MapUndo( target );
			undo.Execute( new AddBrushCommand( Brush( "b1", 0, 0, 0, 64, 64, 64 ) ) );
			using var scheduler = NewScheduler( recoveryDir, intervalSeconds: 0.1 );
			scheduler.ActiveMap = target;
			string autosavePath = MapAutosave.AutosaveFilePathFor( target.SourcePath, target.DocumentId, recoveryDir );

			scheduler.Start( TimeSpan.FromMilliseconds( 100 ) );
			bool written = false;
			for ( int i = 0; i < 50 && !written; i++ )
			{
				Thread.Sleep( 100 );
				written = File.Exists( autosavePath );
			}
			scheduler.Stop();
			Assert( written, "timer autosave must write within 5s at a 100ms interval" );
			Assert( scheduler.LastMapAutosavedDocumentId == target.DocumentId, "scheduler must record the autosaved map" );
		} );

		// 17. a crash marker causes recovery to surface the autosave; clean shutdown clears the offer
		Check( "crash marker surfaces autosave and clean shutdown clears the offer", () =>
		{
			string dir = CreateTestDir( "crash-marker" );
			string recoveryDir = Path.Combine( dir, "recoveries" );
			var target = NewTarget( dir );
			var undo = new MapUndo( target );
			undo.Execute( new AddBrushCommand( Brush( "b1", 0, 0, 0, 64, 64, 64 ) ) );

			Assert( !MapAutosave.ShouldOfferRecovery( target.SourcePath, target.DocumentId, recoveryDir ), "no autosave yet means no offer" );
			MapAutosave.WriteAutosave( target, target.SourcePath, recoveryDir );
			Assert( MapAutosave.ShouldOfferRecovery( target.SourcePath, target.DocumentId, recoveryDir ),
				"a fresh autosave (crash assumed) must be offered on open" );
			var found = AutosaveScheduler.FindMapRecovery( target.SourcePath );
			Assert( found != null && found.Crashed, "scheduler discovery must surface the crashed record" );

			MapAutosave.MarkCleanShutdown( target.SourcePath, target.DocumentId, recoveryDir );
			Assert( !MapAutosave.ShouldOfferRecovery( target.SourcePath, target.DocumentId, recoveryDir ),
				"a clean shutdown must clear the recovery offer" );
			Assert( MapAutosave.TryFindRecovery( target.SourcePath, target.DocumentId, recoveryDir ) != null,
				"clean shutdown clears the marker, it must not delete the autosave itself" );

			MapAutosave.DiscardRecovery( target.SourcePath, target.DocumentId, recoveryDir );
			Assert( MapAutosave.TryFindRecovery( target.SourcePath, target.DocumentId, recoveryDir ) == null,
				"discarding recovery must remove the record" );
		} );

		// 18. restoring from the autosave brings back the autosaved state, not the later edits
		Check( "restore from autosave applies the autosaved state", () =>
		{
			string dir = CreateTestDir( "restore" );
			string recoveryDir = Path.Combine( dir, "recoveries" );
			var target = NewTarget( dir );
			var undo = new MapUndo( target );
			undo.Execute( new AddBrushCommand( Brush( "b1", 0, 0, 0, 64, 64, 64 ) ) );
			undo.Execute( new AddEntityCommand( Entity( "e1", "light", 0, 0, 96 ) ) );
			var record = MapAutosave.WriteAutosave( target, target.SourcePath, recoveryDir );

			undo.Execute( new MoveBrushCommand( "b1", new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ), new Vec3( 128, 0, 0 ), new Vec3( 192, 64, 64 ) ) );
			undo.Execute( new RemoveEntityCommand( target.FindEntity( "e1" )! ) );
			Assert( target.FindBrush( "b1" )!.Min.x == 128 && target.FindEntity( "e1" ) == null, "later edits must be live before restore" );

			MapAutosave.Restore( target, record );
			Assert( target.FindBrush( "b1" )!.Min.x == 0, "restore must bring the brush back to its autosaved bounds" );
			Assert( target.FindEntity( "e1" ) != null, "restore must bring back the entity deleted after the autosave" );
		} );

		// 19. backgrounding autosaves even when the interval has not fired
		Check( "save on backgrounding writes the autosave", () =>
		{
			string dir = CreateTestDir( "backgrounding" );
			string recoveryDir = Path.Combine( dir, "recoveries" );
			var target = NewTarget( dir );
			var undo = new MapUndo( target );
			undo.Execute( new AddBrushCommand( Brush( "b1", 0, 0, 0, 64, 64, 64 ) ) );
			using var scheduler = NewScheduler( recoveryDir, intervalSeconds: 3600 );
			scheduler.ActiveMap = target;

			var result = scheduler.SaveMapOnBackgroundingAsync().GetAwaiter().GetResult();
			Assert( result.IsSuccess, "backgrounding autosave must succeed: " + result.Message );
			Assert( File.Exists( MapAutosave.AutosaveFilePathFor( target.SourcePath, target.DocumentId, recoveryDir ) ),
				"backgrounding must write the autosave file next to the map" );
		} );

		string FindFixture( string fileName )
		{
			string relative = Path.Combine( "tests", "fixtures", "hammer", "hammer-undo-autosave-map-commands", fileName );
			string fromCwd = Path.Combine( Directory.GetCurrentDirectory(), relative );
			if ( File.Exists( fromCwd ) ) return fromCwd;
			string fromAssembly = Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "..", relative ) );
			if ( File.Exists( fromAssembly ) ) return fromAssembly;
			throw new InvalidOperationException( "Fixture not found: searched " + fromCwd + " and " + fromAssembly );
		}
	}
}
