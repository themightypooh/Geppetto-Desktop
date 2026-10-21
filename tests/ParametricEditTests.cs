using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Effigy;
using Marionette.Desktop;

internal static class ParametricEditTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "parametric-edit", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"Expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"Expected {typeof( T ).Name}, got silent success" );
		}

		Check( "add feature -> parameter edit -> undo -> redo", () =>
		{
			var studio = new PartStudio();
			var session = DocumentSession.CreateEffigy( "parametric_add.effigy", studio );
			Require( session.Studio!.Features.Count == 0 );

			var box = new PrimitiveFeature { Name = "BaseBox" };
			ParametricEditService.AddFeature( session, box );

			Require( session.Studio.Features.Count == 1, "feature count should be 1" );
			Require( session.Studio.Bodies.Count == 1, "body count should be 1" );
			Require( session.Studio.TotalFaceCount == 6, "box should have 6 faces" );
			Require( MathF.Abs( box.SizeX.Value - 1f ) < 1e-4f );

			// Parameter edit
			ParametricEditService.ModifyParameter( session, box.Id, "Width", 5f );
			Require( MathF.Abs( box.SizeX.Value - 5f ) < 1e-4f );
			var (min, max) = ModelMeasure.Bounds( session.Studio.Bodies[0].Mesh );
			Require( MathF.Abs( (max.x - min.x) - 5f ) < 1e-3f, "box width geometry should be 5" );

			// Undo parameter edit
			session.History.Undo();
			Require( MathF.Abs( box.SizeX.Value - 1f ) < 1e-4f, "box width should undo to 1" );
			(min, max) = ModelMeasure.Bounds( session.Studio.Bodies[0].Mesh );
			Require( MathF.Abs( (max.x - min.x) - 1f ) < 1e-3f, "geometry should undo to 1" );

			// Undo add feature
			session.History.Undo();
			Require( session.Studio.Features.Count == 0, "feature count should undo to 0" );
			Require( session.Studio.Bodies.Count == 0, "bodies should undo to 0" );

			// Redo add feature
			session.History.Redo();
			Require( session.Studio.Features.Count == 1 );
			Require( session.Studio.Bodies.Count == 1 );
			Require( MathF.Abs( box.SizeX.Value - 1f ) < 1e-4f );

			// Redo parameter edit
			session.History.Redo();
			Require( MathF.Abs( box.SizeX.Value - 5f ) < 1e-4f );
			(min, max) = ModelMeasure.Bounds( session.Studio.Bodies[0].Mesh );
			Require( MathF.Abs( (max.x - min.x) - 5f ) < 1e-3f );
		} );

		Check( "remove feature and undo restoration", () =>
		{
			var studio = new PartStudio();
			var session = DocumentSession.CreateEffigy( "parametric_remove.effigy", studio );

			var box1 = new PrimitiveFeature { Name = "Box1" };
			var box2 = new PrimitiveFeature { Name = "Box2" };
			box2.Position.Value = new Vec3( 10, 0, 0 );

			ParametricEditService.AddFeature( session, box1 );
			ParametricEditService.AddFeature( session, box2 );

			string id1 = box1.Id;
			string id2 = box2.Id;

			Require( session.Studio!.Features.Count == 2 );
			Require( session.Studio.Bodies.Count == 2 );

			// Remove Box1
			ParametricEditService.RemoveFeature( session, id1 );
			Require( session.Studio.Features.Count == 1 );
			Require( session.Studio.Features[0].Id == id2 );
			Require( session.Studio.Bodies.Count == 1 );

			// Undo restoration
			session.History.Undo();
			Require( session.Studio.Features.Count == 2 );
			Require( session.Studio.Features[0].Id == id1, "Feature ID must be preserved on restoration" );
			Require( session.Studio.Features[1].Id == id2, "Downstream feature ID must be preserved" );
			Require( session.Studio.Bodies.Count == 2 );

			// Redo removal
			session.History.Redo();
			Require( session.Studio.Features.Count == 1 );
			Require( session.Studio.Features[0].Id == id2 );
			Require( session.Studio.Bodies.Count == 1 );
		} );

		Check( "reorder features and undo", () =>
		{
			var studio = new PartStudio();
			var session = DocumentSession.CreateEffigy( "parametric_reorder.effigy", studio );

			var box = new PrimitiveFeature { Name = "Box" };
			ParametricEditService.AddFeature( session, box );

			var xform = new TransformFeature { Name = "Move" };
			xform.Translate.Value = new Vec3( 5, 0, 0 );
			ParametricEditService.AddFeature( session, xform );

			Require( session.Studio!.Features[0] == box && session.Studio.Features[1] == xform );
			var (minBefore, maxBefore) = ModelMeasure.Bounds( session.Studio.Bodies[0].Mesh );
			float centerBeforeX = (minBefore.x + maxBefore.x) * 0.5f;
			Require( MathF.Abs( centerBeforeX - 5f ) < 1e-3f, "Transform should move box to x=5" );

			// Move Transform before Box
			ParametricEditService.MoveFeature( session, 1, 0 );
			Require( session.Studio.Features[0] == xform && session.Studio.Features[1] == box );

			// Box built at origin after Transform already ran
			var (minAfter, maxAfter) = ModelMeasure.Bounds( session.Studio.Bodies[0].Mesh );
			float centerAfterX = (minAfter.x + maxAfter.x) * 0.5f;
			Require( MathF.Abs( centerAfterX - 0f ) < 1e-3f, "Box built after Transform should remain at origin" );

			// Undo reorder
			session.History.Undo();
			Require( session.Studio.Features[0] == box && session.Studio.Features[1] == xform );
			var (minRestored, maxRestored) = ModelMeasure.Bounds( session.Studio.Bodies[0].Mesh );
			float centerRestoredX = (minRestored.x + maxRestored.x) * 0.5f;
			Require( MathF.Abs( centerRestoredX - 5f ) < 1e-3f, "Undo reorder should restore x=5" );

			// Redo reorder
			session.History.Redo();
			Require( session.Studio.Features[0] == xform && session.Studio.Features[1] == box );
			var (minRedo, maxRedo) = ModelMeasure.Bounds( session.Studio.Bodies[0].Mesh );
			Require( MathF.Abs( (minRedo.x + maxRedo.x) * 0.5f - 0f ) < 1e-3f );
		} );

		Check( "parameter edit with transaction coalescing", () =>
		{
			var studio = new PartStudio();
			var session = DocumentSession.CreateEffigy( "parametric_drag.effigy", studio );

			var box = new PrimitiveFeature { Name = "Box" };
			ParametricEditService.AddFeature( session, box );

			int initialUndoCount = session.History.UndoCount; // 1
			float initialWidth = box.SizeX.Value;

			// Coalesce 50 preview updates into 1 commit
			using ( var drag = ParametricEditService.BeginParameterDrag<float>( session, box.Id, "Width" ) )
			{
				for ( int i = 1; i <= 50; i++ )
				{
					float cur = 1f + i * 0.1f;
					drag.UpdatePreview( cur );
					Require( MathF.Abs( box.SizeX.Value - cur ) < 1e-4f );
				}
				drag.Commit( 10f );
			}

			Require( session.History.UndoCount == initialUndoCount + 1, "50 updates must coalesce into 1 history entry" );
			Require( MathF.Abs( box.SizeX.Value - 10f ) < 1e-4f );
			var (min, max) = ModelMeasure.Bounds( session.Studio!.Bodies[0].Mesh );
			Require( MathF.Abs( (max.x - min.x) - 10f ) < 1e-3f );

			// Undo drag
			session.History.Undo();
			Require( MathF.Abs( box.SizeX.Value - initialWidth ) < 1e-4f, "Undo must restore initial width" );
			(min, max) = ModelMeasure.Bounds( session.Studio.Bodies[0].Mesh );
			Require( MathF.Abs( (max.x - min.x) - 1f ) < 1e-3f );

			// Redo drag
			session.History.Redo();
			Require( MathF.Abs( box.SizeX.Value - 10f ) < 1e-4f );
			(min, max) = ModelMeasure.Bounds( session.Studio.Bodies[0].Mesh );
			Require( MathF.Abs( (max.x - min.x) - 10f ) < 1e-3f );
		} );

		Check( "feature error / invalid parameter reports failure and retains previous valid state", () =>
		{
			var studio = new PartStudio();
			var session = DocumentSession.CreateEffigy( "parametric_error.effigy", studio );

			var box = new PrimitiveFeature { Name = "Box" };
			ParametricEditService.AddFeature( session, box );
			float initialWidth = box.SizeX.Value;
			int undoCountBefore = session.History.UndoCount;

			// 1. Invalid parameter value (out of range negative size)
			Throws<ArgumentOutOfRangeException>( () => ParametricEditService.ModifyParameter( session, box.Id, "Width", -5f ) );

			// 2. Invalid parameter value (non-finite float)
			Throws<ArgumentException>( () => ParametricEditService.ModifyParameter( session, box.Id, "Width", float.NaN ) );

			// 3. Unrecognized / unwhitelisted parameter
			Throws<ArgumentException>( () => ParametricEditService.ModifyParameter( session, box.Id, "NonExistentProperty", 100f ) );

			// 4. Invalid property type (string on float parameter)
			Throws<ArgumentException>( () => ParametricEditService.ModifyParameter( session, box.Id, "Width", "string_value" ) );

			// Verify state is clean and previous valid values retained
			Require( MathF.Abs( box.SizeX.Value - initialWidth ) < 1e-4f );
			Require( session.History.UndoCount == undoCountBefore );
			Require( session.Studio!.Bodies.Count == 1 );

			// 5. Feature error during RebuildAsync retains previous valid geometry and snapshot
			var report = ParametricEditService.Rebuild( session );
			Require( !report.HasErrors );
			var validSnapshot = session.CurrentSnapshot;
			Require( validSnapshot != null );
			int validFloatCount = validSnapshot!.Mesh.Vertices.Length;
			Require( validFloatCount > 0 );

			// Add a feature that fails during execute (Transform with 0 scale on all axes)
			var failingTransform = new TransformFeature { Name = "FailingTransform" };
			failingTransform.Scale.Value = Vec3.Zero;
			session.Studio.Features.Add( failingTransform );

			var asyncResult = ParametricEditService.RebuildAsync( session ).GetAwaiter().GetResult();
			Require( !asyncResult.Success, "RebuildAsync must report failure on feature error" );
			Require( asyncResult.HasErrors, "RebuildResult must report HasErrors" );
			Require( asyncResult.Report?.Errors.Count > 0 );
			Require( session.CurrentSnapshot == validSnapshot, "Session snapshot must not be wiped out or replaced with empty geometry" );
			Require( session.CurrentSnapshot!.Mesh.Vertices.Length == validFloatCount );
		} );

		Check( "stale async rebuild discarded", () =>
		{
			var studio = new PartStudio();
			var session = DocumentSession.CreateEffigy( "parametric_stale.effigy", studio );

			var box = new PrimitiveFeature { Name = "Box" };
			ParametricEditService.AddFeature( session, box );
			ParametricEditService.Rebuild( session );

			var initialSnapshot = session.CurrentSnapshot;
			Require( initialSnapshot != null );

			// Start RebuildAsync and simulate a modification bumping session revision while worker is running
			var rebuildTask = ParametricEditService.RebuildAsync( session, onRebuilding: async () =>
			{
				await Task.Delay( 20 );
				session.BumpRevision(); // Session revision moves ahead
			} );

			var result = rebuildTask.GetAwaiter().GetResult();

			Require( !result.Success, "Stale job must not succeed" );
			Require( result.IsStale, "Stale result must be flagged IsStale" );
			Require( session.CurrentSnapshot == initialSnapshot, "Stale snapshot must not overwrite current snapshot" );
			Require( session.Revision != result.Revision, "Session revision has moved past job revision" );
		} );

		Check( "save via E4 and reload reproduces identical feature order and geometry", () =>
		{
			var studio = new PartStudio();
			var session = DocumentSession.CreateEffigy( "parametric_save.effigy", studio );

			var box1 = new PrimitiveFeature { Name = "Box1" };
			box1.SizeX.Value = 2f;
			box1.SizeY.Value = 3f;
			box1.SizeZ.Value = 4f;

			var box2 = new PrimitiveFeature { Name = "Cylinder2" };
			box2.Shape.Index = 1; // Cylinder
			box2.Radius.Value = 1.5f;
			box2.Position.Value = new Vec3( 10, 0, 0 );

			var xform = new TransformFeature { Name = "Shift" };
			xform.Translate.Value = new Vec3( 0, 5, 0 );

			ParametricEditService.AddFeature( session, box1 );
			ParametricEditService.AddFeature( session, box2 );
			ParametricEditService.AddFeature( session, xform );
			ParametricEditService.Rebuild( session );

			int expectedFeatureCount = session.Studio!.Features.Count;
			int expectedBodyCount = session.Studio.Bodies.Count;
			int expectedVertexCount = session.Studio.TotalVertexCount;
			int expectedFaceCount = session.Studio.TotalFaceCount;

			// Save via E4 DocumentSaveService
			string savePath = Path.Combine( settings.WorkDirectory, "e5_roundtrip.effigy" );
			var saveService = new DocumentSaveService();
			var saveResult = saveService.Save( session, savePath );
			Require( saveResult.IsSuccess, $"Save failed: {saveResult.ErrorMessage}" );

			// Reload via StudioDocument.ReadFile
			var reloaded = StudioDocument.ReadFile( savePath );
			var reloadReport = reloaded.Rebuild();
			Require( !reloadReport.HasErrors, "Reloaded rebuild must be clean" );

			Require( reloaded.Features.Count == expectedFeatureCount, "Feature count must match" );
			for ( int i = 0; i < expectedFeatureCount; i++ )
			{
				Require( reloaded.Features[i].Id == session.Studio.Features[i].Id, $"Feature {i} ID must match" );
				Require( reloaded.Features[i].TypeName == session.Studio.Features[i].TypeName, $"Feature {i} TypeName must match" );
				Require( reloaded.Features[i].Name == session.Studio.Features[i].Name, $"Feature {i} Name must match" );
			}

			Require( reloaded.Bodies.Count == expectedBodyCount, "Body count must match" );
			Require( reloaded.TotalVertexCount == expectedVertexCount, "Vertex count must match" );
			Require( reloaded.TotalFaceCount == expectedFaceCount, "Face count must match" );

			// Test edit after reload
			var reloadedSession = DocumentSession.CreateEffigy( savePath, reloaded );
			ParametricEditService.ModifyParameter( reloadedSession, reloaded.Features[0].Id, "Width", 6f );
			Require( MathF.Abs( ((PrimitiveFeature)reloadedSession.Studio!.Features[0]).SizeX.Value - 6f ) < 1e-4f );
			Require( reloadedSession.Studio.TotalVertexCount > 0 );
		} );

		Check( "two sessions' parametric edits remain isolated", () =>
		{
			var studioA = new PartStudio();
			studioA.Add( new PrimitiveFeature { Name = "BoxA" } );
			studioA.Rebuild();

			var studioB = new PartStudio();
			studioB.Add( new PrimitiveFeature { Name = "BoxB" } );
			studioB.Rebuild();

			var sessionA = DocumentSession.CreateEffigy( "a.effigy", studioA );
			var sessionB = DocumentSession.CreateEffigy( "b.effigy", studioB );

			Require( sessionA.Studio!.Features.Count == 1 );
			Require( sessionB.Studio!.Features.Count == 1 );
			Require( sessionA.History.UndoCount == 0 );
			Require( sessionB.History.UndoCount == 0 );

			// Edit on Session A
			var extraBox = new PrimitiveFeature { Name = "ExtraBox" };
			ParametricEditService.AddFeature( sessionA, extraBox );

			Require( sessionA.Studio.Features.Count == 2 );
			Require( sessionB.Studio.Features.Count == 1, "Session B must not gain features from Session A" );
			Require( sessionA.History.UndoCount == 1 );
			Require( sessionB.History.UndoCount == 0, "Session B history must remain clean" );

			// Edit on Session B
			string idB = sessionB.Studio.Features[0].Id;
			ParametricEditService.ModifyParameter( sessionB, idB, "Width", 8f );

			Require( MathF.Abs( ((PrimitiveFeature)sessionB.Studio.Features[0]).SizeX.Value - 8f ) < 1e-4f );
			Require( MathF.Abs( ((PrimitiveFeature)sessionA.Studio.Features[0]).SizeX.Value - 1f ) < 1e-4f, "Session A must not reflect Session B's parameter edit" );

			// Undo on Session A
			sessionA.History.Undo();
			Require( sessionA.Studio.Features.Count == 1 );
			Require( sessionB.Studio.Features.Count == 1 );
			Require( MathF.Abs( ((PrimitiveFeature)sessionB.Studio.Features[0]).SizeX.Value - 8f ) < 1e-4f, "Session B must retain its edit when Session A undos" );
		} );
	}
}
