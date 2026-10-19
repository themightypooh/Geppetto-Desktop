using System;
using System.IO;
using System.Linq;
using Effigy;
using Marionette.Desktop;

internal static class DocumentSessionTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "document-session", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		PartStudio MakeStudio( string name = "box" )
		{
			var studio = new PartStudio();
			var prim = new PrimitiveFeature();
			prim.Name = name;
			studio.Add( prim );
			studio.Rebuild();
			return studio;
		}

		string WriteEffigy( string filename, PartStudio studio )
		{
			string path = Path.Combine( settings.WorkDirectory, filename );
			File.WriteAllText( path, StudioDocument.Write( studio ) );
			return path;
		}

		Check( "open two documents", () =>
		{
			var studio1 = MakeStudio( "box1" );
			var studio2 = MakeStudio( "box2" );
			var session1 = DocumentSession.CreateEffigy( "doc1.effigy", studio1 );
			var session2 = DocumentSession.CreateEffigy( "doc2.effigy", studio2 );

			Require( session1.DocumentId != session2.DocumentId, "document IDs must be distinct" );
			Require( session1.Studio != null && session2.Studio != null );
			Require( session1.Studio!.Features[0].Name == "box1" );
			Require( session2.Studio!.Features[0].Name == "box2" );

			var snap1 = session1.BuildSnapshot();
			var snap2 = session2.BuildSnapshot();

			Require( snap1.DocumentId == session1.DocumentId );
			Require( snap2.DocumentId == session2.DocumentId );
			Require( snap1.Revision == session1.Revision );
			Require( snap2.Revision == session2.Revision );

			Require( session1.TryPromoteSnapshot( snap1 ), "snapshot 1 must promote into session 1" );
			Require( !session2.TryPromoteSnapshot( snap1 ), "snapshot 1 must be rejected by session 2" );
		} );

		Check( "failed second load retains the first", () =>
		{
			var studio = MakeStudio( "initial" );
			string validPath = WriteEffigy( "valid-initial.effigy", studio );

			var loader = new DocumentLoader();
			loader.Start( validPath );
			Require( loader.Completion.Wait( TimeSpan.FromSeconds( 10 ) ), "first load timed out" );
			Require( loader.Status == DocumentLoader.State.Ready, loader.Error ?? "expected ready" );

			var firstSession = loader.Session;
			var firstSnapshot = loader.Snapshot;
			var firstMesh = loader.Mesh;
			Require( firstSession != null && firstSnapshot != null && firstMesh != null );

			string invalidPath = Path.Combine( settings.WorkDirectory, "nonexistent-doc-" + Guid.NewGuid().ToString( "N" ) + ".effigy" );
			loader.Start( invalidPath );
			loader.Completion.Wait( TimeSpan.FromSeconds( 5 ) );

			Require( loader.Status == DocumentLoader.State.Failed, "second load should have failed" );
			Require( loader.Error != null, "error should be populated" );
			Require( ReferenceEquals( loader.Session, firstSession ), "failed load must retain previous valid session" );
			Require( ReferenceEquals( loader.Snapshot, firstSnapshot ), "failed load must retain previous valid snapshot" );
			Require( ReferenceEquals( loader.Mesh, firstMesh ), "failed load must retain previous valid mesh" );
			Require( loader.Session.Studio != null );
		} );

		Check( "source exists after GPU upload", () =>
		{
			var studio = MakeStudio( "retained_source" );
			var session = DocumentSession.CreateEffigy( "retained.effigy", studio );

			Require( session.Studio!.Bodies.Count > 0, "studio bodies must exist" );
			int bodyCountBefore = session.Studio.Bodies.Count;

			var snapshot = session.BuildSnapshot();
			Require( snapshot.Mesh.Vertices != null && snapshot.Mesh.Vertices.Length > 0 );
			Require( snapshot.Mesh.Indices != null && snapshot.Mesh.Indices.Length > 0 );

			// Simulate ViewportMesh.Upload behavior which nulls CPU-side upload arrays.
			snapshot.Mesh.Vertices = null;
			snapshot.Mesh.Indices = null;

			// Verify session source state is completely intact and not altered.
			Require( session.Studio != null );
			Require( session.Studio!.Bodies.Count == bodyCountBefore );
			Require( session.Studio.Features.Count > 0 );
			Require( session.Studio.Bodies[0].Mesh != null && session.Studio.Bodies[0].Mesh.Positions.Count > 0 );

			// Also verify FBX session source retention
			var fbxResult = new FbxImport.Result();
			fbxResult.Pieces.Add( new ObjReader.ObjPiece( "test_piece", Primitives.Box() ) );
			var fbxSession = DocumentSession.CreateFbx( "test.fbx", fbxResult );
			var fbxSnapshot = fbxSession.BuildSnapshot();
			fbxSnapshot.Mesh.Vertices = null;
			fbxSnapshot.Mesh.Indices = null;

			Require( fbxSession.FbxSource != null );
			Require( fbxSession.FbxSource!.PieceCount == 1 );
			Require( fbxSession.FbxSource.Result.Pieces[0].Name == "test_piece" );
		} );

		Check( "stale build completion is rejected", () =>
		{
			var studio = MakeStudio( "stale_test" );
			var session = DocumentSession.CreateEffigy( "stale.effigy", studio );
			long rev1 = session.Revision;

			var snap1 = session.BuildSnapshot();
			Require( snap1.Revision == rev1 );
			Require( !snap1.IsStaleFor( session ) );

			// Authoring edit bumps session revision
			session.BumpRevision();
			Require( session.Revision == rev1 + 1 );
			Require( snap1.IsStaleFor( session ), "snap1 must be stale for updated session" );
			Require( !session.TryPromoteSnapshot( snap1 ), "promoting stale snapshot must return false" );
			Require( session.CurrentSnapshot == null );

			// Build fresh snapshot for the updated revision
			var snap2 = session.BuildSnapshot();
			Require( snap2.Revision == session.Revision );
			Require( !snap2.IsStaleFor( session ) );
			Require( session.TryPromoteSnapshot( snap2 ), "fresh snapshot must be accepted" );
			Require( ReferenceEquals( session.CurrentSnapshot, snap2 ) );
		} );

		Check( "editing a session does not mutate a detached snapshot", () =>
		{
			var studio = MakeStudio( "detached_test" );
			var session = DocumentSession.CreateEffigy( "detached.effigy", studio );
			var snap1 = session.BuildSnapshot();

			long revBefore = snap1.Revision;
			int batchCountBefore = snap1.Mesh.Batches.Length;
			var boundsBefore = snap1.Mesh.BoundsMin;

			// Edit session
			session.PlayheadTime = 8.5f;
			session.IsPlaying = true;
			session.Selection = new SessionSelection { FeatureId = "feat_test", BodyId = "body_0" };
			session.BumpRevision();
			session.Studio!.Add( new PrimitiveFeature { Name = "box2" } );

			// Detached snapshot properties must remain unchanged
			Require( snap1.Revision == revBefore );
			Require( snap1.Mesh.Batches.Length == batchCountBefore );
			Require( snap1.Mesh.BoundsMin == boundsBefore );
			Require( snap1.TotalSeconds > 0 );
		} );

		Check( "Effigy session retains PartStudio and FBX does not masquerade as feature tree", () =>
		{
			var studio = MakeStudio( "effigy_feature_tree" );
			var effigySession = DocumentSession.CreateEffigy( "model.effigy", studio );
			Require( effigySession.SourceKind == DocumentSourceKind.Effigy );
			Require( effigySession.Studio != null );
			Require( effigySession.FbxSource == null );

			var fbxResult = new FbxImport.Result();
			fbxResult.Pieces.Add( new ObjReader.ObjPiece( "p1", new PolyMesh() ) );
			var fbxSession = DocumentSession.CreateFbx( "model.fbx", fbxResult );
			Require( fbxSession.SourceKind == DocumentSourceKind.Fbx );
			Require( fbxSession.Studio == null, "FBX session must not masquerade as having a PartStudio feature tree" );
			Require( fbxSession.FbxSource != null );
			Require( fbxSession.FbxSource!.PieceCount == 1 );
		} );

		Check( "retains original clip JSON alongside parsed RigAnimFile", () =>
		{
			string clipJson = "{\n  \"SourceModel\": \"hero\",\n  \"CustomEngineEvent\": \"on_fire\",\n  \"ExtraMetadata\": 42,\n  \"Tracks\": []\n}";
			string clipPath = Path.Combine( settings.WorkDirectory, "custom_clip.riganim" );
			File.WriteAllText( clipPath, clipJson );

			var clip = RigAnimFile.ReadFile( clipPath );
			var studio = MakeStudio( "clip_test" );
			var session = DocumentSession.CreateEffigy( "hero.effigy", studio, clipPath: clipPath, clipJson: clipJson, clip: clip );

			Require( session.ClipSourceJson == clipJson, "original JSON must be retained byte-for-byte" );
			Require( session.Clip != null );

			// RigAnimWriter with original JSON preserves unknown fields
			string outputJson = RigAnimWriter.Write( session.Clip, session.ClipSourceJson );
			Require( outputJson.Contains( "CustomEngineEvent" ), "Custom field must survive round trip" );
			Require( outputJson.Contains( "ExtraMetadata" ), "Extra metadata must survive round trip" );
		} );

		Check( "revision and saved-state tracking", () =>
		{
			var studio = MakeStudio( "dirty_tracking" );
			var session = DocumentSession.CreateEffigy( "doc.effigy", studio );

			Require( session.Revision == 1 );
			Require( session.SavedRevision == 1 );
			Require( !session.IsDirty, "new session at revision 1 is clean" );

			session.BumpRevision();
			Require( session.Revision == 2 );
			Require( session.IsDirty, "session with bumped revision is dirty" );

			session.MarkSaved();
			Require( session.SavedRevision == 2 );
			Require( !session.IsDirty, "session marked saved is clean" );
		} );

		Check( "loader reset clears session and snapshot cleanly", () =>
		{
			var loader = new DocumentLoader();
			loader.Reset();
			Require( loader.Session == null && loader.Snapshot == null && loader.Mesh == null && loader.Status == DocumentLoader.State.Idle );
		} );

		Check( "snapshot promote through DocumentLoader updates mesh and player", () =>
		{
			var studio = MakeStudio( "promote_test" );
			string path = WriteEffigy( "promote.effigy", studio );

			var loader = new DocumentLoader();
			loader.Start( path );
			Require( loader.Completion.Wait( TimeSpan.FromSeconds( 10 ) ) );
			Require( loader.Status == DocumentLoader.State.Ready );

			var initialSnapshot = loader.Snapshot;
			Require( initialSnapshot != null );

			// Create new snapshot for same revision
			var updatedSnapshot = loader.Session.BuildSnapshot();
			Require( loader.TryPromoteSnapshot( updatedSnapshot ) );
			Require( ReferenceEquals( loader.Snapshot, updatedSnapshot ) );
			Require( ReferenceEquals( loader.Mesh, updatedSnapshot.Mesh ) );

			// Bump revision: promoting previous snapshot should fail
			loader.Session.BumpRevision();
			Require( !loader.TryPromoteSnapshot( updatedSnapshot ), "stale snapshot promotion via loader must fail" );
		} );
	}
}
