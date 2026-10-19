using System;
using Effigy;
using Marionette.Desktop;

internal static class EditHistoryTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "edit-history", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		Check( "execute undo redo exact equality", () =>
		{
			var history = new EditHistory();
			string target = "initial";

			var cmd = new ValueChangeCommand<string>( "change text", v => target = v, "initial", "modified" );
			history.Execute( cmd );
			Require( target == "modified", "execute did not apply value" );
			Require( history.CanUndo && !history.CanRedo );
			Require( history.UndoCount == 1 );

			history.Undo();
			Require( target == "initial", "undo did not restore initial value" );
			Require( !history.CanUndo && history.CanRedo );
			Require( history.RedoCount == 1 );

			history.Redo();
			Require( target == "modified", "redo did not reapply modified value" );
			Require( history.CanUndo && !history.CanRedo );
		} );

		Check( "100 preview updates produce one entry", () =>
		{
			var history = new EditHistory();
			float dragPosition = 0f;
			float initialPosition = dragPosition;

			using ( var tx = history.BeginTransaction( "drag slider", () => dragPosition = initialPosition ) )
			{
				for ( int i = 1; i <= 100; i++ )
				{
					float currentVal = i * 1.5f;
					tx.UpdatePreview( () => dragPosition = currentVal );
				}
				float finalVal = 100 * 1.5f;
				tx.Commit( () => dragPosition = finalVal, () => initialPosition == finalVal );
			}

			Require( history.UndoCount == 1, $"expected 1 history entry, got {history.UndoCount}" );
			Require( MathF.Abs( dragPosition - 150f ) < 1e-4f );

			history.Undo();
			Require( MathF.Abs( dragPosition - 0f ) < 1e-4f, "undo must restore initial state before drag" );

			history.Redo();
			Require( MathF.Abs( dragPosition - 150f ) < 1e-4f, "redo must restore final state after drag" );
		} );

		Check( "cancel restores pre-transaction state without recording history", () =>
		{
			var history = new EditHistory();
			int value = 42;
			int initial = value;

			using ( var tx = history.BeginTransaction( "tentative edit", () => value = initial ) )
			{
				tx.UpdatePreview( () => value = 100 );
				tx.UpdatePreview( () => value = 200 );
				tx.Cancel();
			}

			Require( value == 42, "cancel must restore pre-transaction state" );
			Require( history.UndoCount == 0, "cancel must not add to undo history" );
			Require( !history.IsDirty, "cancelled transaction must not leave history dirty" );
		} );

		Check( "no-op edits create no entry", () =>
		{
			var history = new EditHistory();
			int value = 10;

			// Command where before == after
			var noOpCmd = new ValueChangeCommand<int>( "no-op", v => value = v, 10, 10 );
			history.Execute( noOpCmd );
			Require( history.UndoCount == 0, "no-op command must create no entry" );
			Require( !history.IsDirty );

			// Transaction where final == initial
			using ( var tx = history.BeginTransaction( "no-op drag", () => value = 10 ) )
			{
				tx.UpdatePreview( () => value = 99 );
				tx.Commit( () => value = 10, isNoOp: () => true );
			}

			Require( value == 10 );
			Require( history.UndoCount == 0, "no-op transaction must create no entry" );
			Require( !history.IsDirty );
		} );

		Check( "failed execution leaves state and history unchanged", () =>
		{
			var history = new EditHistory();
			string value = "safe";

			var failingCmd = new DelegateEditCommand(
				"failing",
				execute: () => throw new InvalidOperationException( "validation error" ),
				undo: () => value = "safe"
			);

			bool threw = false;
			try { history.Execute( failingCmd ); }
			catch ( InvalidOperationException ) { threw = true; }

			Require( threw, "expected execute to throw" );
			Require( value == "safe", "state must remain unchanged" );
			Require( history.UndoCount == 0 && history.RedoCount == 0, "history must remain empty" );
			Require( !history.IsDirty );
		} );

		Check( "new committed edit after undo discards redo branch", () =>
		{
			var history = new EditHistory();
			string text = "";

			history.Execute( new ValueChangeCommand<string>( "step 1", v => text = v, "", "A" ) );
			history.Execute( new ValueChangeCommand<string>( "step 2", v => text = v, "A", "B" ) );
			Require( history.UndoCount == 2 );

			history.Undo();
			Require( text == "A" );
			Require( history.CanRedo && history.RedoCount == 1 );

			// Branch: new edit while redo is available
			history.Execute( new ValueChangeCommand<string>( "step 2 branch", v => text = v, "A", "C" ) );
			Require( text == "C" );
			Require( !history.CanRedo && history.RedoCount == 0, "new edit must discard redo branch" );
			Require( history.UndoCount == 2 );

			history.Undo();
			Require( text == "A" );

			history.Undo();
			Require( text == "" );
		} );

		Check( "saved state tracking and branching at same depth", () =>
		{
			var history = new EditHistory();
			string val = "0";

			history.Execute( new ValueChangeCommand<string>( "1", v => val = v, "0", "1" ) );
			history.Execute( new ValueChangeCommand<string>( "2", v => val = v, "1", "2" ) );

			// Mark saved at state "2"
			history.MarkSaved();
			Require( !history.IsDirty, "marked saved state must be clean" );

			// Undo to "1": dirty
			history.Undo();
			Require( val == "1" );
			Require( history.IsDirty, "undone state must be dirty" );

			// Redo back to "2": clean again!
			history.Redo();
			Require( val == "2" );
			Require( !history.IsDirty, "redone to saved state must be clean" );

			// Undo to "1", then branch to "3" at the same stack depth (depth 2)
			history.Undo();
			Require( history.IsDirty );
			history.Execute( new ValueChangeCommand<string>( "3", v => val = v, "1", "3" ) );
			Require( val == "3" );
			Require( history.UndoCount == 2, "stack depth is still 2" );
			Require( history.IsDirty, "branch at same depth must stay dirty because state ID differs from saved state" );
		} );

		Check( "bounded history eviction policy", () =>
		{
			var history = new EditHistory( capacity: 2 );
			int val = 0;

			// S0 is initial saved state
			history.MarkSaved();
			Require( !history.IsDirty );

			// Push 1 and 2
			history.Execute( new ValueChangeCommand<int>( "1", v => val = v, 0, 1 ) );
			history.Execute( new ValueChangeCommand<int>( "2", v => val = v, 1, 2 ) );
			Require( history.UndoCount == 2 );
			Require( !history.SavedStateEvicted );

			// Push 3: exceeds capacity 2, oldest entry (which led from S0) is evicted
			history.Execute( new ValueChangeCommand<int>( "3", v => val = v, 2, 3 ) );
			Require( history.UndoCount == 2, "history count capped at capacity" );
			Require( history.SavedStateEvicted, "S0 should be reported evicted" );

			// Undo both remaining items
			history.Undo(); // back to 2
			history.Undo(); // back to 1 (cannot undo further to 0)
			Require( !history.CanUndo );
			Require( val == 1 );
			Require( history.IsDirty, "evicted saved state means history stays dirty even after full undo" );
		} );

		Check( "two sessions isolated", () =>
		{
			var studio1 = new PartStudio();
			studio1.Add( new PrimitiveFeature { Name = "s1_box" } );
			studio1.Rebuild();

			var studio2 = new PartStudio();
			studio2.Add( new PrimitiveFeature { Name = "s2_box" } );
			studio2.Rebuild();

			var session1 = DocumentSession.CreateEffigy( "s1.effigy", studio1 );
			var session2 = DocumentSession.CreateEffigy( "s2.effigy", studio2 );

			Require( !session1.IsDirty && !session2.IsDirty );

			string name1 = "orig1";
			session1.History.Execute( new ValueChangeCommand<string>( "edit s1", v => name1 = v, "orig1", "new1" ) );

			Require( session1.History.UndoCount == 1 );
			Require( session1.IsDirty );
			Require( session1.Revision == 2, "session revision must bump on command execution" );

			Require( session2.History.UndoCount == 0, "session 2 history must be isolated" );
			Require( !session2.IsDirty, "session 2 must not be dirty" );
			Require( session2.Revision == 1, "session 2 revision must not bump" );

			session1.History.Undo();
			Require( name1 == "orig1" );
			Require( session1.Revision == 3, "session revision must bump on undo" );
			Require( session2.Revision == 1 );
		} );

		Check( "actual feature tree fixture goes through history service", () =>
		{
			var studio = new PartStudio();
			var feat1 = new PrimitiveFeature { Name = "base_box" };
			studio.Add( feat1 );
			studio.Rebuild();

			Require( studio.Features.Count == 1 );
			Require( studio.Bodies.Count == 1 );

			var session = DocumentSession.CreateEffigy( "model.effigy", studio );
			var feat2 = new PrimitiveFeature { Name = "added_cylinder" };
			feat2.SizeX.Value = 2f;

			// Undoable command that adds a feature to the parametric PartStudio
			var addFeatureCmd = new DelegateEditCommand(
				"add cylinder",
				execute: () =>
				{
					studio.Add( feat2 );
					studio.Rebuild();
				},
				undo: () =>
				{
					studio.Remove( feat2 );
					studio.Rebuild();
				}
			);

			session.History.Execute( addFeatureCmd );
			Require( studio.Features.Count == 2 );
			Require( studio.Bodies.Count == 2 );
			Require( studio.Features[1].Name == "added_cylinder" );
			Require( session.IsDirty );

			// Undo feature addition
			session.History.Undo();
			Require( studio.Features.Count == 1 );
			Require( studio.Bodies.Count == 1 );
			Require( studio.Features[0].Name == "base_box" );

			// Redo feature addition restores exact same instance and rebuilt state
			session.History.Redo();
			Require( studio.Features.Count == 2 );
			Require( studio.Bodies.Count == 2 );
			Require( ReferenceEquals( studio.Features[1], feat2 ), "redo must restore the exact feature instance" );
		} );
	}
}
