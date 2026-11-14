using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;

internal static class InsertMeshTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "insert-mesh", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Same( Vec3 a, Vec3 b, string message )
		{
			Require( MathF.Abs( a.x - b.x ) < 1e-5f && MathF.Abs( a.y - b.y ) < 1e-5f && MathF.Abs( a.z - b.z ) < 1e-5f,
				$"{message}: expected {a.x},{a.y},{a.z} got {b.x},{b.y},{b.z}" );
		}

		PartStudio BoxStudio()
		{
			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature() );
			var report = studio.Rebuild();
			Require( report.Errors.Count == 0, "box studio did not rebuild clean" );
			return studio;
		}

		/// <summary>A closed square pyramid: base quad + apex, 5 verts, 6 faces.</summary>
		PolyMesh RivetMesh()
		{
			var mesh = new PolyMesh();
			mesh.Positions.AddRange( new[]
			{
				new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 1, 1, 0 ), new Vec3( 0, 1, 0 ),
				new Vec3( 0.5f, 0.5f, 1 ),
			} );
			mesh.Faces.Add( new Face( new[] { 0, 2, 1 } ) );
			mesh.Faces.Add( new Face( new[] { 0, 3, 2 } ) );
			mesh.Faces.Add( new Face( new[] { 0, 1, 4 } ) );
			mesh.Faces.Add( new Face( new[] { 1, 2, 4 } ) );
			mesh.Faces.Add( new Face( new[] { 2, 3, 4 } ) );
			mesh.Faces.Add( new Face( new[] { 3, 0, 4 } ) );
			return mesh;
		}

		/// <summary>Flat subdivided grid over [-half, half]^2 at z = 0, n cells per side.</summary>
		PolyMesh FlatGrid( int n, float half )
		{
			var mesh = new PolyMesh();
			for ( var j = 0; j <= n; j++ )
			for ( var i = 0; i <= n; i++ )
				mesh.Positions.Add( new Vec3( -half + 2f * half * i / n, -half + 2f * half * j / n, 0 ) );
			for ( var j = 0; j < n; j++ )
			for ( var i = 0; i < n; i++ )
			{
				var a = j * (n + 1) + i;
				var b = a + 1;
				var c = a + (n + 1);
				var d = c + 1;
				mesh.Faces.Add( new Face( new[] { a, b, d } ) );
				mesh.Faces.Add( new Face( new[] { a, d, c } ) );
			}
			return mesh;
		}

		Check( "insert placement round-trip", () =>
		{
			var brush = new InsertBrush( "rivet", RivetMesh() );
			var session = new InsertMeshSession( brush );
			session.Place( new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ) );
			Require( session.Instances.Count == 1, "one placement did not make one instance" );
			Require( session.RealizedMesh().FaceCount == 6, "realised mesh is not the rivet" );

			var studio = BoxStudio();
			var target = studio.Bodies[0].Mesh;
			var facesBefore = target.FaceCount;
			var vertsBefore = target.VertexCount;
			var commit = session.Commit( target );
			Require( commit.CommittedFaces == 6, $"commit merged {commit.CommittedFaces} faces, expected 6" );
			Require( commit.CommittedVertices == 5, $"commit merged {commit.CommittedVertices} verts, expected 5" );
			Require( target.FaceCount == facesBefore + 6, "target faces did not grow by the rivet" );
			Require( target.VertexCount == vertsBefore + 5, "target verts did not grow by the rivet" );
			Require( session.LastCommittedFaces == 6 && session.LastCommittedVertices == 5, "session did not record the commit" );
		} );

		Check( "curve placement round-trip", () =>
		{
			var brush = new InsertBrush( "rivet", RivetMesh() );
			var session = new InsertMeshSession( brush );
			var points = new List<Vec3> { new( 0, 0, 0 ), new( 2, 0, 0 ), new( 2, 2, 0 ) };
			var placed = session.PlaceAlongCurve( points, 1f );
			Require( placed == 5, $"curve placement made {placed} instances, expected 5" );
			Same( new Vec3( 0, 0, 0 ), session.Instances[0].Placement.Origin, "first curve instance is not at the start" );
			Same( new Vec3( 2, 2, 0 ), session.Instances[4].Placement.Origin, "last curve instance is not at the end" );

			var studio = BoxStudio();
			var target = studio.Bodies[0].Mesh;
			var facesBefore = target.FaceCount;
			session.Commit( target );
			Require( target.FaceCount == facesBefore + 5 * 6, "curve commit did not merge all instances" );
		} );

		Check( "edit-source-updates-instances", () =>
		{
			var source = RivetMesh();
			var session = new InsertMeshSession( new InsertBrush( "rivet", source ) );
			session.Place( new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ) );
			session.Place( new Vec3( 3, 0, 0 ), new Vec3( 0, 0, 1 ) );
			var before = session.RealizedInstance( 0 ).Positions[4].z;
			source.Positions[4] = source.Positions[4] + new Vec3( 0, 0, 2 );
			var after = session.RealizedInstance( 0 ).Positions[4].z;
			Require( MathF.Abs( (after - before) - 2f ) < 1e-5f, "editing the source did not move the live instance" );
			var second = session.RealizedInstance( 1 ).Positions[4].z;
			Require( MathF.Abs( (second - before) - 2f ) < 1e-5f, "editing the source did not move the second instance" );
		} );

		Check( "randomisation deterministic under a seed", () =>
		{
			InsertMeshSession Scatter( int seed )
			{
				var s = new InsertMeshSession( new InsertBrush( "rivet", RivetMesh() ) );
				s.Scatter( 6, new Vec3( 0, 0, 0 ), 2f, seed );
				return s;
			}
			var a = Scatter( 7 );
			var b = Scatter( 7 );
			for ( var i = 0; i < 6; i++ )
			{
				Same( a.Instances[i].Placement.Origin, b.Instances[i].Placement.Origin, $"same-seed instance {i} moved" );
				Same( a.Instances[i].Placement.X, b.Instances[i].Placement.X, $"same-seed instance {i} rescaled" );
			}
			var c = Scatter( 8 );
			Require( Enumerable.Range( 0, 6 ).Any( i =>
				MathF.Abs( a.Instances[i].Placement.Origin.x - c.Instances[i].Placement.Origin.x ) > 1e-5f
				|| MathF.Abs( a.Instances[i].Placement.Origin.y - c.Instances[i].Placement.Origin.y ) > 1e-5f ),
				"a different seed placed identical instances" );
		} );

		Check( "commit merges and is undoable", () =>
		{
			var studio = BoxStudio();
			var target = studio.Bodies[0].Mesh;
			var beforePositions = target.Positions.ToList();
			var facesBefore = target.FaceCount;
			var session = new InsertMeshSession( new InsertBrush( "rivet", RivetMesh() ) );
			session.Place( new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ) );
			var commit = session.Commit( target );
			Require( target.FaceCount == facesBefore + 6, "commit did not merge" );
			commit.Restore( target );
			Require( target.FaceCount == facesBefore, "undo did not restore the face count" );
			Require( target.VertexCount == beforePositions.Count, "undo did not restore the vertex count" );
			for ( var i = 0; i < beforePositions.Count; i++ )
				Same( beforePositions[i], target.Positions[i], $"undo moved vertex {i}" );
		} );

		Check( "commit with no instances refuses", () =>
		{
			var studio = BoxStudio();
			var session = new InsertMeshSession( new InsertBrush( "rivet", RivetMesh() ) );
			var threw = false;
			try { session.Commit( studio.Bodies[0].Mesh ); }
			catch ( InvalidOperationException ) { threw = true; }
			Require( threw, "committing with no instances did not refuse" );
		} );

		Check( "commit 200 instances along a curve stays closed", () =>
		{
			var session = new InsertMeshSession( new InsertBrush( "rivet", RivetMesh() ) );
			var points = new List<Vec3> { new( 0, 0, 0 ), new( 24.875f, 0, 0 ) };
			var placed = session.PlaceAlongCurve( points, 0.125f );
			Require( placed == 200, $"expected 200 instances, got {placed}" );
			var studio = BoxStudio();
			var target = studio.Bodies[0].Mesh;
			var facesBefore = target.FaceCount;
			session.Commit( target );
			Require( target.FaceCount == facesBefore + 200 * 6, "200x commit did not merge all instances" );
			var validation = MeshValidator.Validate( target );
			Require( validation.NonManifoldEdges == 0, $"committed mesh has {validation.NonManifoldEdges} non-manifold edges" );
			Require( validation.BoundaryEdges == 0, $"committed mesh has {validation.BoundaryEdges} boundary edges" );
			Require( validation.IsClosed, "committed mesh is not closed: " + validation );
		} );

		Check( "VDM stamp applies vector displacement", () =>
		{
			var grid = FlatGrid( 8, 1f );
			var before = grid.Positions.ToList();
			var field = VdmStampLibrary.Get( "horn_hook" );
			Require( field is not null, "horn_hook stamp is missing from the library" );
			var result = VdmStamp.Apply( grid, field, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), new Vec3( 0, 1, 0 ), 1f, 1f );
			Require( result.Affected > 0, "the stamp affected no vertex" );
			Require( result.MaxTangential > 0f, "the stamp moved nothing sideways — that is a bump, not a VDM" );
			Require( result.MaxNormal > 0f, "the stamp moved nothing along the normal" );
			var movedSideways = grid.Positions.Where( ( p, i ) =>
				MathF.Abs( p.x - before[i].x ) > 1e-6f || MathF.Abs( p.y - before[i].y ) > 1e-6f ).Count();
			Require( movedSideways > 0, "no vertex actually moved sideways" );
		} );

		Check( "VDM library search returns a named stamp", () =>
		{
			Require( VdmStampLibrary.Search( "horn" ).Contains( "horn_hook" ), "search 'horn' missed horn_hook" );
			Require( VdmStampLibrary.Search( "HOOK" ).Contains( "horn_hook" ), "search is not case-insensitive" );
			Require( VdmStampLibrary.Search( "curl" ).Contains( "horn_hook" ), "search by tag 'curl' missed horn_hook" );
			Require( VdmStampLibrary.Search( "scale" ).Contains( "scale_overhang" ), "search 'scale' missed scale_overhang" );
			Require( VdmStampLibrary.Search( "no-such-stamp" ).Count == 0, "search for a missing stamp returned results" );
			foreach ( var name in VdmStampLibrary.Names )
				Require( VdmStampLibrary.Search( "" ).Contains( name ), $"empty search missed built-in {name}" );
			Require( VdmStampLibrary.Tags( "horn_hook" ).Contains( "curl" ), "horn_hook carries no 'curl' tag" );
			Require( VdmStampLibrary.Tags( "no-such-stamp" ).Length == 0, "tags for an unknown stamp are not empty" );
		} );

		Check( "VDM shared folder registers a stamp", () =>
		{
			var dir = Path.Combine( Path.GetTempPath(), "vdm-shared-test-" + Guid.NewGuid().ToString( "N" ) );
			try
			{
				Directory.CreateDirectory( dir );
				VdmStampLibrary.Get( "horn_hook" ).Save( Path.Combine( dir, "test_stamp_xyz.json" ) );
				var added = VdmStampLibrary.AddSharedFolder( dir );
				Require( added == 1, $"shared folder added {added} stamps, expected 1" );
				Require( VdmStampLibrary.Search( "test_stamp_xyz" ).Contains( "test_stamp_xyz" ), "shared stamp is not searchable" );
				Require( VdmStampLibrary.Get( "test_stamp_xyz" ) is not null, "shared stamp does not load" );
				Require( VdmStampLibrary.Info( "test_stamp_xyz" )?.Source == "shared", "shared stamp is not marked shared" );
				Require( VdmStampLibrary.SharedFolders.Contains( dir ), "shared folder was not recorded" );
				Require( VdmStampLibrary.AddSharedFolder( dir ) == 0, "re-adding a shared folder re-registered its stamps" );
			}
			finally
			{
				try { if ( Directory.Exists( dir ) ) Directory.Delete( dir, true ); } catch { }
			}
		} );

		Check( "add InsertMesh resolves through the script verb", () =>
		{
			Require( BuildScript.CreateFeature( "InsertMesh" ) is InsertMeshFeature, "CreateFeature(InsertMesh) did not resolve" );

			var studio = new PartStudio();
			var source = new PrimitiveFeature();
			source.Name = "Rivet";
			studio.Add( source );
			var target = new PrimitiveFeature();
			target.Name = "Target";
			studio.Add( target );
			var report = studio.Rebuild();
			Require( report.Errors.Count == 0, "rivet+target studio did not rebuild clean" );
			var targetBody = studio.Bodies.First( b => b.Name == "Target" );
			var sourceFaces = studio.Bodies.First( b => b.Name == "Rivet" ).Mesh.FaceCount;
			var targetFacesBefore = targetBody.Mesh.FaceCount;

			var insert = studio.Add( new InsertMeshFeature() );
			insert.SourceBody.Value = "Rivet";
			insert.Bodies.BodyIds.Add( targetBody.Id );
			insert.Center.Value = new Vec3( 0, 0, 0.5f );
			insert.Normal.Value = new Vec3( 0, 0, 1 );
			var rebuilt = studio.Rebuild();
			Require( rebuilt.Errors.Count == 0, "insert mesh rebuild failed: " + string.Join( "; ", rebuilt.Errors.Select( e => e.Message ) ) );
			Require( insert.LastInstanceCount == 1, $"click mode placed {insert.LastInstanceCount} instances, expected 1" );
			// Bodies are re-made from scratch every rebuild (see Body), and the
			// rebuild cache clones them twice over — the targetBody captured
			// before the insert rebuild is a stale object whose mesh the feature
			// never touched. Re-fetch the current body to see the merged faces.
			var targetAfter = studio.Bodies.First( b => b.Name == "Target" );
			Require( targetAfter.Mesh.FaceCount == targetFacesBefore + sourceFaces, "target did not gain the source faces" );

			var studio2 = new PartStudio();
			var source2 = new PrimitiveFeature();
			source2.Name = "Rivet";
			studio2.Add( source2 );
			var target2 = new PrimitiveFeature();
			target2.Name = "Target";
			studio2.Add( target2 );
			Require( studio2.Rebuild().Errors.Count == 0, "script studio did not rebuild clean" );
			var result = BuildScript.Run( studio2, "add InsertMesh sourcebody=Rivet bodies=Target mode=Scatter count=3\n" );
			Require( result.Ok, $"script failed: {result.Error}" );
			var scripted = studio2.Features.OfType<InsertMeshFeature>().Single();
			Require( scripted.LastInstanceCount == 3, $"script scatter placed {scripted.LastInstanceCount} instances, expected 3" );
		} );
	}
}
