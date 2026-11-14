using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Effigy;
using Marionette.Desktop;

/// <summary>Mesh Doctor + manual retopo (godlike-mesh-doctor-manual-retopo, Wave 1 P0).
/// Fixtures are built directly as PolyMesh, like BaselineTests.Triangle(). Every count in
/// the report tests is exact: a wrong count is a wrong checker, not a flaky fixture.
/// The unit cube below is outward-wound (signed volume +8); the open cube omits the top face.
/// All fixture faces carry non-zero UVs unless the test is about UVs, because a face with
/// all-zero UVs is reported as UV-missing by design.
/// REGISTRATION (integrator step, TestRegistry.cs left untouched to avoid clobbering
/// concurrent workers): add MeshDoctorRetopoTests.Register( registry, settings );
/// to DesktopTestHost.Run, after RemeshTests.Register.</summary>
internal static class MeshDoctorRetopoTests
{
	static void Check( bool ok, string message ) { if ( !ok ) throw new Exception( message ); }

	static Vec2[] QuadUvs() => new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) };
	static Vec2[] TriUvs() => new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 0, 1 ) };

	static Vec3 V( float x, float y, float z ) => new( x, y, z );

	static PolyMesh Cube( bool closed = true, bool flipped = false )
	{
		var m = new PolyMesh();
		m.Positions.AddRange( new[]
		{
			V( -1, -1, -1 ), V( 1, -1, -1 ), V( 1, 1, -1 ), V( -1, 1, -1 ),
			V( -1, -1, 1 ), V( 1, -1, 1 ), V( 1, 1, 1 ), V( -1, 1, 1 )
		} );
		var faces = new List<int[]>
		{
			new[] { 0, 3, 2, 1 }, // bottom, normal -z
			new[] { 4, 5, 6, 7 }, // top, normal +z
			new[] { 0, 1, 5, 4 }, // front, normal -y
			new[] { 3, 7, 6, 2 }, // back, normal +y
			new[] { 0, 4, 7, 3 }, // left, normal -x
			new[] { 1, 2, 6, 5 } // right, normal +x
		};
		if ( !closed ) faces.RemoveAt( 1 );
		foreach ( var f in faces )
		{
			var indices = flipped ? f.Reverse().ToArray() : f;
			m.Faces.Add( new Face( indices, QuadUvs() ) );
		}
		return m;
	}

	static string Fingerprint( PolyMesh m ) => string.Join( ";",
		m.Positions.Select( p => $"{p.x:0.###},{p.y:0.###},{p.z:0.###}" ) )
		+ "|" + string.Join( ";", m.Faces.Select( f => string.Join( ",", f.Indices ) ) );

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "mesh-doctor", "clean cube reports zero defects and is clean", () =>
		{
			var r = MeshDoctor.Check( Cube(), "cube" );
			Check( r.Holes == 0, "holes: " + r.Holes );
			Check( r.BoundaryEdges == 0, "boundary: " + r.BoundaryEdges );
			Check( r.NonManifoldEdges == 0 && r.NonManifoldVertices == 0, "non-manifold present" );
			Check( r.DegenerateFaces == 0 && r.ZeroAreaFaces == 0, "degenerate present" );
			Check( r.DuplicateFaces == 0, "duplicates: " + r.DuplicateFaces );
			Check( r.FlippedFaces == 0 && r.IsOutward, "flipped faces present" );
			Check( r.LooseVertices == 0, "loose: " + r.LooseVertices );
			Check( r.Shells == 1 && r.Euler == 2, $"shells {r.Shells} euler {r.Euler}" );
			Check( r.QuadPercent == 100f, "quad%: " + r.QuadPercent );
			Check( r.SelfIntersections == 0, "self-intersections: " + r.SelfIntersections );
			Check( r.IsClosed && r.IsManifold && r.IsClean, "cube should be clean: " + r.Describe() );
		} );

		registry.Add( "mesh-doctor", "open cube reports exactly one hole and four boundary edges", () =>
		{
			var r = MeshDoctor.Check( Cube( closed: false ), "open cube" );
			Check( r.Holes == 1, "holes: " + r.Holes );
			Check( r.ClosedLoops == 1 && r.OpenChains == 0, $"loops {r.ClosedLoops}/{r.OpenChains}" );
			Check( r.BoundaryEdges == 4, "boundary: " + r.BoundaryEdges );
			Check( r.NonManifoldEdges == 0, "non-manifold present" );
			Check( r.Euler == 1, "euler: " + r.Euler );
			Check( !r.BlocksExport, "holes warn, they do not block: " + r.Describe() );
			Check( r.Warnings.Any( w => w.Contains( "holes" ) ), "hole warning missing: " + r.Describe() );
			var loops = MeshDoctor.FindBoundaryLoops( Cube( closed: false ) );
			Check( loops.Count == 1 && loops[0].Closed && loops[0].Vertices.Count == 4,
				"boundary loop should be the 4 top-rim verts" );
		} );

		registry.Add( "mesh-doctor", "three triangles on one edge report one non-manifold edge", () =>
		{
			var m = new PolyMesh();
			m.Positions.AddRange( new[] { V( 0, 0, 0 ), V( 1, 0, 0 ), V( 0, 1, 0 ), V( 0, 0, 1 ), V( 1, 1, 0 ) } );
			m.Faces.Add( new Face( new[] { 0, 1, 2 }, TriUvs() ) );
			m.Faces.Add( new Face( new[] { 0, 1, 3 }, TriUvs() ) );
			m.Faces.Add( new Face( new[] { 0, 1, 4 }, TriUvs() ) );
			var r = MeshDoctor.Check( m, "fin" );
			Check( r.NonManifoldEdges == 1, "non-manifold edges: " + r.NonManifoldEdges );
			Check( r.BoundaryEdges == 6, "boundary: " + r.BoundaryEdges );
			Check( r.Holes == 2 && r.ClosedLoops == 1 && r.OpenChains == 1,
				$"holes {r.Holes} closed {r.ClosedLoops} open {r.OpenChains}" );
			Check( r.BlocksExport, "non-manifold must block export: " + r.Describe() );
		} );

		registry.Add( "mesh-doctor", "duplicate, degenerate and zero-area faces are counted separately", () =>
		{
			var dup = new PolyMesh();
			dup.Positions.AddRange( new[] { V( 0, 0, 0 ), V( 1, 0, 0 ), V( 1, 1, 0 ), V( 0, 1, 0 ) } );
			dup.Faces.Add( new Face( new[] { 0, 1, 2, 3 }, QuadUvs() ) );
			dup.Faces.Add( new Face( new[] { 0, 1, 2, 3 }, QuadUvs() ) );
			var rd = MeshDoctor.Check( dup, "dup" );
			Check( rd.DuplicateFaces == 1, "duplicates: " + rd.DuplicateFaces );
			Check( rd.DegenerateFaces == 0 && rd.ZeroAreaFaces == 0, "dup counted as degenerate" );
			Check( rd.BoundaryEdges == 0 && rd.Holes == 0, "dup quad edges should be shared, not boundary" );
			Check( rd.BlocksExport, "duplicates must block export" );

			var degen = new PolyMesh();
			degen.Positions.AddRange( new[] { V( 0, 0, 0 ), V( 1, 0, 0 ), V( 1, 1, 0 ), V( 0, 1, 0 ), V( 2, 0, 0 ), V( 3, 0, 0 ) } );
			degen.Faces.Add( new Face( new[] { 0, 1, 2, 3 }, QuadUvs() ) );
			degen.Faces.Add( new Face( new[] { 4, 5 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ) } ) );
			var rg = MeshDoctor.Check( degen, "degen" );
			Check( rg.DegenerateFaces == 1, "degenerate: " + rg.DegenerateFaces );
			Check( rg.ZeroAreaFaces == 0, "degenerate counted as zero-area" );
			Check( rg.BlocksExport, "degenerate must block export" );

			var flat = new PolyMesh();
			flat.Positions.AddRange( new[] { V( 0, 0, 0 ), V( 1, 0, 0 ), V( 1, 1, 0 ), V( 0, 1, 0 ), V( 2, 0, 0 ), V( 3, 0, 0 ), V( 4, 0, 0 ) } );
			flat.Faces.Add( new Face( new[] { 0, 1, 2, 3 }, QuadUvs() ) );
			flat.Faces.Add( new Face( new[] { 4, 5, 6 }, TriUvs() ) );
			var rf = MeshDoctor.Check( flat, "flat" );
			Check( rf.ZeroAreaFaces == 1, "zero-area: " + rf.ZeroAreaFaces );
			Check( rf.DegenerateFaces == 0, "zero-area counted as degenerate" );
			Check( rf.BlocksExport, "zero-area must block export" );
		} );

		registry.Add( "mesh-doctor", "inward cube reports all six faces flipped", () =>
		{
			var m = Cube( flipped: true );
			Check( m.SignedVolume() < 0f, "flipped cube should have negative volume" );
			var r = MeshDoctor.Check( m, "inward cube" );
			Check( r.FlippedFaces == 6, "flipped: " + r.FlippedFaces );
			Check( !r.IsOutward, "inward cube reported outward" );
			Check( r.BlocksExport, "flipped faces must block export: " + r.Describe() );
		} );

		registry.Add( "mesh-doctor", "loose vertex is a warning, not a block", () =>
		{
			var m = Cube();
			m.Positions.Add( V( 9, 9, 9 ) );
			var r = MeshDoctor.Check( m, "loose" );
			Check( r.LooseVertices == 1, "loose: " + r.LooseVertices );
			Check( !r.BlocksExport && r.Warnings.Count > 0, "loose vertex should warn, not block" );
		} );

		registry.Add( "mesh-doctor", "fix closes the open cube and the result is manifold", () =>
		{
			var m = Cube( closed: false );
			var fixed_ = MeshDoctor.Fix( m );
			Check( fixed_.AppliedFixes.Count == 5, "expected 5 fix steps, got " + fixed_.AppliedFixes.Count );
			Check( fixed_.After.Holes == 0, "holes after fix: " + fixed_.After.Holes );
			Check( fixed_.After.BoundaryEdges == 0, "boundary after fix: " + fixed_.After.BoundaryEdges );
			Check( fixed_.After.NonManifoldEdges == 0, "non-manifold after fix" );
			Check( fixed_.After.IsClosed && fixed_.After.IsManifold, "not closed/manifold: " + fixed_.After.Describe() );
			Check( m.FaceCount == 7, "open cube (5 faces) + 2 fill triangles = 7, got " + m.FaceCount );
		} );

		registry.Add( "mesh-doctor", "fix undoes per step: normals undo restores the flipped cube", () =>
		{
			var m = Cube( flipped: true );
			var original = Fingerprint( m );
			var fixed_ = MeshDoctor.Fix( m );
			Check( fixed_.After.FlippedFaces == 0 && fixed_.After.IsOutward, "fix should flip the cube outward" );
			Check( Fingerprint( m ) == Fingerprint( Cube() ), "fixed cube should match the clean cube" );
			Check( MeshDoctor.UndoLast( m, fixed_ ), "UndoLast should succeed" );
			Check( MeshDoctor.Check( m, "undone" ).FlippedFaces == 6, "undoing normals should restore the flipped cube" );
			MeshDoctor.UndoAll( m, fixed_ );
			Check( Fingerprint( m ) == original, "UndoAll should restore the original mesh exactly" );
			Check( !MeshDoctor.UndoLast( m, fixed_ ), "nothing left to undo" );
		} );

		registry.Add( "mesh-doctor", "pre-export hook blocks non-manifold, warns on holes, passes clean", () =>
		{
			var fin = new PolyMesh();
			fin.Positions.AddRange( new[] { V( 0, 0, 0 ), V( 1, 0, 0 ), V( 0, 1, 0 ), V( 0, 0, 1 ), V( 1, 1, 0 ) } );
			fin.Faces.Add( new Face( new[] { 0, 1, 2 }, TriUvs() ) );
			fin.Faces.Add( new Face( new[] { 0, 1, 3 }, TriUvs() ) );
			fin.Faces.Add( new Face( new[] { 0, 1, 4 }, TriUvs() ) );
			var blocked = MeshDoctor.PreExportCheck( fin, "fin" );
			Check( blocked.Block && blocked.Summary.Contains( "non-manifold" ), "fin should block: " + blocked.Summary );

			var open = MeshDoctor.PreExportCheck( Cube( closed: false ), "open cube" );
			Check( !open.Block && open.Warn && open.Summary.Contains( "holes" ), "open cube should warn: " + open.Summary );
			var strict = MeshDoctor.PreExportCheck( Cube( closed: false ), "open cube", strictHoles: true );
			Check( strict.Block, "strict holes should block" );

			var clean = MeshDoctor.PreExportCheck( Cube(), "cube" );
			Check( !clean.Block && !clean.Warn, "clean cube should pass: " + clean.Summary );
		} );

		registry.Add( "mesh-doctor", "export gate blocks a non-manifold body and passes a clean cube", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "mesh-doctor-export" );
			Directory.CreateDirectory( testDir );

			var cleanStudio = new PartStudio();
			cleanStudio.Add( new PrimitiveFeature { SizeX = { Value = 10f }, SizeY = { Value = 10f }, SizeZ = { Value = 10f } } );
			cleanStudio.Rebuild();
			var cleanSession = DocumentSession.CreateEffigy( Path.Combine( testDir, "clean.effigy" ), cleanStudio );
			var cleanResult = new ExportService().Export( new ExportRequest( cleanSession, ExportFormat.Obj, Path.Combine( testDir, "clean.obj" ) ) );
			Check( cleanResult.Success, "clean cube export should pass the gate: " + cleanResult.ErrorMessage );

			var brokenStudio = new PartStudio();
			brokenStudio.Add( new PrimitiveFeature { SizeX = { Value = 10f }, SizeY = { Value = 10f }, SizeZ = { Value = 10f } } );
			brokenStudio.Rebuild();
			var brokenSession = DocumentSession.CreateEffigy( Path.Combine( testDir, "broken.effigy" ), brokenStudio );
			var mesh = brokenSession.Studio.Bodies[0].Mesh;
			var edge = mesh.BuildEdgeFaces().First( kv => kv.Value.Count == 2 ).Key;
			var mid = (mesh.Positions[edge.A] + mesh.Positions[edge.B]) * 0.5f;
			var lo = new Vec3( mesh.Positions.Min( p => p.x ), mesh.Positions.Min( p => p.y ), mesh.Positions.Min( p => p.z ) );
			var hi = new Vec3( mesh.Positions.Max( p => p.x ), mesh.Positions.Max( p => p.y ), mesh.Positions.Max( p => p.z ) );
			var center = (lo + hi) * 0.5f;
			var outward = (mid - center).Normal;
			if ( outward.LengthSquared < 1e-6f ) outward = new Vec3( 0, 0, 1 );
			var tip = mesh.AddVertex( mid + outward * 5f );
			mesh.AddFace( new[] { edge.A, edge.B, tip }, TriUvs() );
			Check( MeshDoctor.Check( mesh, "broken" ).NonManifoldEdges == 1, "fixture should be non-manifold before export" );
			var brokenResult = new ExportService().Export( new ExportRequest( brokenSession, ExportFormat.Obj, Path.Combine( testDir, "broken.obj" ) ) );
			Check( !brokenResult.Success, "non-manifold export should be blocked" );
			Check( (brokenResult.ErrorMessage ?? "").Contains( "Mesh Doctor" ), "block reason should name Mesh Doctor: " + brokenResult.ErrorMessage );
			Check( !File.Exists( Path.Combine( testDir, "broken.obj" ) ), "blocked export must not write the destination" );
		} );

		registry.Add( "mesh-doctor", "check on a 250k-face grid finishes in seconds", () =>
		{
			const int n = 500;
			var m = new PolyMesh();
			for ( var j = 0; j <= n; j++ )
				for ( var i = 0; i <= n; i++ )
					m.Positions.Add( new Vec3( i * 0.01f, j * 0.01f, 0 ) );
			for ( var j = 0; j < n; j++ )
				for ( var i = 0; i < n; i++ )
				{
					var a = j * (n + 1) + i;
					m.Faces.Add( new Face( new[] { a, a + 1, a + n + 2, a + n + 1 }, QuadUvs() ) );
				}
			var clock = Stopwatch.StartNew();
			var r = MeshDoctor.Check( m, "grid" );
			clock.Stop();
			Check( r.Faces == n * n, "faces: " + r.Faces );
			Check( r.Holes == 1 && r.BoundaryEdges == 4 * n, $"grid boundary: holes {r.Holes} edges {r.BoundaryEdges}" );
			Check( clock.ElapsedMilliseconds < 30000, $"check took {clock.ElapsedMilliseconds} ms on {r.Faces} faces" );
		} );

		registry.Add( "manual-retopo", "placed points snap onto the source surface", () =>
		{
			var retopo = new ManualRetopo( Cube() );
			var v = retopo.AddPoint( new Vec3( 1.3f, 0.2f, -0.4f ) );
			Check( retopo.Target.Positions[v].AlmostEquals( new Vec3( 1, 0.2f, -0.4f ), 1e-4f ),
				"snapped point: " + retopo.Target.Positions[v] );
			Check( retopo.MaxSnapDeviation() < 1e-4f, "deviation: " + retopo.MaxSnapDeviation() );
			var threw = false;
			try { retopo.AddPoint( new Vec3( 10, 10, 10 ) ); }
			catch ( InvalidOperationException ) { threw = true; }
			Check( threw, "a point far from the surface should be refused, not silently placed" );
		} );

		registry.Add( "manual-retopo", "symmetry adds a mirrored twin and mirrored quads keep outward normals", () =>
		{
			var retopo = new ManualRetopo( Cube() ) { Symmetry = RetopoMirrorAxis.Y };
			var p0 = retopo.AddPoint( new Vec3( -0.5f, 1.4f, -0.5f ) );
			var p1 = retopo.AddPoint( new Vec3( -0.5f, 1.4f, 0.5f ) );
			var p2 = retopo.AddPoint( new Vec3( 0.5f, 1.4f, 0.5f ) );
			var p3 = retopo.AddPoint( new Vec3( 0.5f, 1.4f, -0.5f ) );
			Check( retopo.TwinOf( p0 ) >= 0, "point off the mirror plane should have a twin" );
			Check( retopo.Target.Positions[retopo.TwinOf( p0 )].AlmostEquals( new Vec3( -0.5f, -1, -0.5f ), 1e-4f ),
				"twin should mirror y: " + retopo.Target.Positions[retopo.TwinOf( p0 )] );
			retopo.AddQuadMirrored( p0, p1, p2, p3 );
			Check( retopo.Target.FaceNormal( retopo.Target.Faces[0] ).y > 0.99f, "source quad should face +y" );
			Check( retopo.Target.FaceNormal( retopo.Target.Faces[1] ).y < -0.99f, "mirrored quad should face -y, not +y" );
			var onPlane = retopo.AddPoint( new Vec3( 0, 0, 1.4f ) );
			Check( retopo.TwinOf( onPlane ) < 0, "a point on the mirror plane is its own mirror" );
		} );

		registry.Add( "manual-retopo", "relax pulls a perturbed vertex back onto the surface", () =>
		{
			var retopo = new ManualRetopo( Cube() );
			var idx = new int[3, 3];
			for ( var j = 0; j < 3; j++ )
				for ( var i = 0; i < 3; i++ )
					idx[i, j] = retopo.AddPoint( new Vec3( -0.5f + i * 0.5f, -0.5f + j * 0.5f, 1.3f ) );
			for ( var j = 0; j < 2; j++ )
				for ( var i = 0; i < 2; i++ )
					retopo.AddQuad( idx[i, j], idx[i + 1, j], idx[i + 1, j + 1], idx[i, j + 1] );
			var center = idx[1, 1];
			retopo.Target.Positions[center] = new Vec3( 0.1f, -0.2f, 1.5f );
			var corner = retopo.Target.Positions[idx[0, 0]];
			var relaxed = retopo.Relax( 1 );
			Check( relaxed.Moved == 1 && relaxed.Snapped == 1 && relaxed.Unsnapped == 0,
				$"relax: moved {relaxed.Moved} snapped {relaxed.Snapped} unsnapped {relaxed.Unsnapped}" );
			Check( retopo.Target.Positions[center].AlmostEquals( new Vec3( 0.05f, -0.1f, 1 ), 1e-3f ),
				"relaxed center: " + retopo.Target.Positions[center] );
			Check( retopo.Target.Positions[idx[0, 0]].AlmostEquals( corner, 1e-6f ), "relax must not move boundary vertices" );
			Check( retopo.MaxSnapDeviation() < 1e-4f, "deviation after relax: " + retopo.MaxSnapDeviation() );
		} );

		registry.Add( "manual-retopo", "hero cage validates all-quad and manifold; a triangle fails all-quad", () =>
		{
			var retopo = new ManualRetopo( Cube() );
			var c = new int[8];
			var corners = new[]
			{
				new Vec3( -1.2f, -1.2f, -1.2f ), new Vec3( 1.2f, -1.2f, -1.2f ), new Vec3( 1.2f, 1.2f, -1.2f ), new Vec3( -1.2f, 1.2f, -1.2f ),
				new Vec3( -1.2f, -1.2f, 1.2f ), new Vec3( 1.2f, -1.2f, 1.2f ), new Vec3( 1.2f, 1.2f, 1.2f ), new Vec3( -1.2f, 1.2f, 1.2f )
			};
			for ( var i = 0; i < 8; i++ ) c[i] = retopo.AddPoint( corners[i] );
			retopo.AddQuad( c[0], c[3], c[2], c[1] );
			retopo.AddQuad( c[4], c[5], c[6], c[7] );
			retopo.AddQuad( c[0], c[1], c[5], c[4] );
			retopo.AddQuad( c[3], c[7], c[6], c[2] );
			retopo.AddQuad( c[0], c[4], c[7], c[3] );
			retopo.AddQuad( c[1], c[2], c[6], c[5] );
			var cage = retopo.ValidateCage();
			Check( cage.AllQuad && cage.QuadCount == 6, "cage quads: " + cage.QuadCount );
			Check( cage.IsManifold && cage.IsClosed, "cage should be manifold and closed: " + cage.Describe() );
			Check( cage.BoundaryLoops == 0 && cage.OpenLoops == 0, "closed cage should have no boundary loops" );

			var tri = new ManualRetopo( Cube() );
			var t0 = tri.AddPoint( new Vec3( -0.5f, -0.5f, 1.3f ) );
			var t1 = tri.AddPoint( new Vec3( 0.5f, -0.5f, 1.3f ) );
			var t2 = tri.AddPoint( new Vec3( 0, 0.5f, 1.3f ) );
			tri.AddTriangle( t0, t1, t2 );
			var triCage = tri.ValidateCage();
			Check( !triCage.AllQuad && triCage.TriangleCount == 1, "triangle cage must fail all-quad" );
			Check( triCage.BoundaryLoops == 1 && triCage.OpenLoops == 0, "single-triangle patch should have one closed boundary loop" );
		} );

		registry.Add( "manual-retopo", "weld merges a duplicated snapped point", () =>
		{
			var retopo = new ManualRetopo( Cube() );
			retopo.AddPoint( new Vec3( 1.01f, 0.2f, -0.4f ) );
			retopo.AddPoint( new Vec3( 1.01f, 0.2f, -0.4f ) );
			Check( retopo.Target.VertexCount == 2, "two identical snaps should start as two vertices" );
			retopo.WeldClose( 1e-3f );
			Check( retopo.Target.VertexCount == 1, "weld should merge the duplicated point, got " + retopo.Target.VertexCount );
		} );
	}
}
