#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Map;
using Marionette.Desktop;

/// <summary>
/// hammer-interop-export-roundtrip-validation: export -&gt; re-import -&gt;
/// compare for all three map export paths (VMF/.map plane fidelity,
/// OBJ/GLB baked mesh + materials, native compile BSP + PVS + lightmaps).
/// Fixtures live in tests/fixtures/hammer/hammer-interop-export-roundtrip-validation/:
///   room.vmf / room.map — the sealed room, generated from the same box
///   math as MapExportRoundtrip.SealedRoom() (see the generator note in
///   the fixture README); the fixture test proves the checked-in files
///   match the builder exactly.
/// Group "exportroundtrip". Registered in TestRegistry.cs (line added by
/// hammer-76, marked TEMP — the integrator decides whether that line stays
/// or the group is registered another way).
///
/// HONEST LIMITS, asserted not hidden:
///   - GLB: MapBakeExport.ExportGlb is a minimal stub (header + asset
///     JSON, no mesh payload — MapBakeExport.GlbNote). CheckGlb reports
///     the path BLOCKED. The real mesh round-trip is proven at the core
///     seam instead (GlbExport.Write -&gt; GlbImport.Read, one part per
///     material) — that is what worker 17 must wire into ExportGlb.
///   - Displacements are preserved by the VMF path and reported as a
///     known loss by the .map path; they are not carried into the bake
///     or compile input by this harness (see MapExportRoundtrip.cs).
///   - MapDocument HAS landed since this file was written, but the shared
///     tree does not compile it yet (EntityOutput shape mismatch): the
///     canonical input stays VmfDocument and the adapters are the seams
///     the integrator replaces.
/// </summary>
internal static class MapExportRoundtripTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "exportroundtrip", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: wrong exception {ex.GetType().Name}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, got silent success" );
		}

		string Work()
		{
			var dir = Path.Combine( settings.WorkDirectory, "exportroundtrip", Guid.NewGuid().ToString( "N" ) );
			Directory.CreateDirectory( dir );
			return dir;
		}

		string Fixture( string name )
		{
			var tried = new List<string>();
			string Here( string path ) => Path.GetFullPath( path );
			var candidates = new List<string>
			{
				Here( Path.Combine( "tests", "fixtures", "hammer", "hammer-interop-export-roundtrip-validation", name ) ),
				Here( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "..", "tests", "fixtures", "hammer", "hammer-interop-export-roundtrip-validation", name ) ),
			};
			var dir = new DirectoryInfo( Directory.GetCurrentDirectory() );
			for ( int i = 0; i < 5 && dir != null; i++, dir = dir.Parent )
				candidates.Add( Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-interop-export-roundtrip-validation", name ) );
			foreach ( var c in candidates )
			{
				tried.Add( c );
				if ( File.Exists( c ) ) return c;
			}
			throw new Exception( $"fixture '{name}' not found; tried: {string.Join( "; ", tried )}" );
		}

		VmfDocument Room() => MapExportRoundtrip.SealedRoom();

		void RequirePassed( RoundtripPathResult result, string what )
		{
			Require( result.Passed, $"{what}: path '{result.Path}' did not pass: {string.Join( "; ", result.Issues.Select( i => i.ToString() ) )}" );
			Require( result.Issues.Count == 0, $"{what}: path '{result.Path}' passed with issues attached" );
		}

		MapBakeMesh BakedRoom() =>
			MapBakeExport.Bake( MapExportRoundtrip.ToMapBrushes( Room() ), new List<MapBakeDisplacement>() );

		Check( "sealed-room fixture is valid", () =>
		{
			var room = Room();
			Require( room.Validate().Count == 0, "sealed room has structural defects: " + string.Join( "; ", room.Validate() ) );
			Require( room.AllBrushes().Count() == MapExportRoundtrip.SealedRoomBrushCount, $"brush count {room.AllBrushes().Count()} != {MapExportRoundtrip.SealedRoomBrushCount}" );
			Require( room.AllBrushes().Sum( b => b.Faces.Count ) == MapExportRoundtrip.SealedRoomFaceCount, "face count is not 42" );
			Require( room.Entities.Count == 2, $"entity count {room.Entities.Count} != 2" );
			Require( room.Entities.Any( e => e.ClassName == "info_player_start" ), "no info_player_start" );
			Require( room.Entities.Any( e => e.ClassName == "light" ), "no light" );
			var copy = room.Clone();
			copy.AttachStoredVertices();
			Require( copy.VerifyStoredVertices().Count == 0, "stored vertices do not sit on their planes: " + string.Join( "; ", copy.VerifyStoredVertices() ) );
			Require( copy.AllBrushes().All( b => b.Faces.All( f => f.StoredVertices.Count == 4 ) ), "every face must solve a 4-vertex polygon" );
		} );

		Check( "sealed-room passes vmf round-trip exact", () =>
		{
			var result = MapExportRoundtrip.CheckVmf( Room() );
			RequirePassed( result, "vmf" );
			Require( result.SourceBrushCount == 7 && result.ResultBrushCount == 7, "brush counts" );
			Require( result.SourceFaceCount == 42 && result.ResultFaceCount == 42, "face counts" );
			Require( result.SourceMaterialCount == 4 && result.ResultMaterialCount == 4, "material counts" );
			Require( result.SourceEntityCount == 2 && result.ResultEntityCount == 2, "entity counts" );
		} );

		Check( "vmf text carries exact fractional vertices", () =>
		{
			var text = VmfIo.Write( Room() );
			Require( text.Contains( MapExportRoundtrip.ExactVertexProbe, StringComparison.Ordinal ),
				$"VMF text lacks the exact fractional vertex {MapExportRoundtrip.ExactVertexProbe}" );
			Require( text.Contains( "( -16.0625 48.5 64.1 )", StringComparison.Ordinal ), "VMF text lacks the crate top corner" );
			Require( text.Contains( "( -48.125 16.375 0 )", StringComparison.Ordinal ), "VMF text lacks the crate base corner" );
		} );

		Check( "sealed-room passes map220 round-trip exact", () =>
		{
			var result = MapExportRoundtrip.CheckMap220( Room() );
			RequirePassed( result, "map220" );
			Require( result.SourceBrushCount == 7 && result.ResultBrushCount == 7, "brush counts" );
			Require( result.SourceFaceCount == 42 && result.ResultFaceCount == 42, "face counts" );
			Require( result.SourceMaterialCount == 4 && result.ResultMaterialCount == 4, "material counts" );
			Require( result.SourceEntityCount == 2 && result.ResultEntityCount == 2, "entity counts" );
		} );

		Check( "map220 parser rejects the vmf scale form", () =>
		{
			// Worker 16's bug class: a .map writer emitting the VMF scale
			// form "[u] scale [v] scale" instead of Valve 220
			// "[u] [v] rotation xscale yscale". The corrupted line must
			// either be refused by the parser or flagged by the
			// comparison — never silently accepted as the same map.
			var room = Room();
			var text = Map220Io.Write( room );
			var line = text.Split( '\n' ).First( l => l.Contains( "[ 1 0 0 0 ] [ 0 -1 0 0 ] 0 0.25 0.25", StringComparison.Ordinal ) );
			var corrupt = text.Replace( line, line.Replace( "[ 1 0 0 0 ] [ 0 -1 0 0 ] 0 0.25 0.25", "[ 1 0 0 0 ] 0.25 [ 0 -1 0 0 ] 0 0.25" ) );
			Require( corrupt != text, "test setup: corrupt replacement did not change the text" );
			VmfDocument? reparsed = null;
			var threw = false;
			try { reparsed = Map220Io.Parse( corrupt ); }
			catch ( MapParseException ) { threw = true; }
			if ( !threw )
			{
				var diffs = MapExportRoundtrip.CompareDocuments( room, reparsed!, "map220" );
				Require( diffs.Count > 0, "VMF-form .map line parsed silently with no detectable difference" );
				Require( diffs.Any( d => d.Code.Contains( "UV", StringComparison.Ordinal ) ), "VMF-form .map line was not flagged as a UV difference" );
			}
		} );

		Check( "sealed-room passes obj round-trip", () =>
		{
			var result = MapExportRoundtrip.CheckObj( Room(), Work() );
			RequirePassed( result, "obj" );
			Require( result.SourceFaceCount == 42 && result.ResultFaceCount == 42, $"face counts {result.SourceFaceCount}/{result.ResultFaceCount}" );
			Require( result.SourceMaterialCount == 4 && result.ResultMaterialCount == 4, "material counts" );
		} );

		Check( "obj re-import catches a dropped material", () =>
		{
			// Deliberately broken export: the crate's usemtl run is
			// rewritten to the wall material. The comparison must name
			// it as a material loss, not pass with the wrong texture.
			var work = Work();
			var baked = BakedRoom();
			var objPath = Path.Combine( work, "room.obj" );
			MapBakeExport.ExportObj( baked, objPath, null );
			var text = File.ReadAllText( objPath );
			Require( text.Contains( "usemtl wood/woodbox001", StringComparison.Ordinal ), "fixture OBJ has no wood usemtl run to tamper with" );
			var tampered = text.Replace( "usemtl wood/woodbox001", "usemtl brick/brickwall001" );
			var reimported = ObjReader.Read( tampered );
			var names = MapExportRoundtrip.ScanNames( tampered, "usemtl" );
			var diffs = MapExportRoundtrip.CompareObjMesh( baked, reimported, names );
			Require( diffs.Any( d => d.Code.Contains( "MATERIAL", StringComparison.Ordinal ) ),
				"swapped usemtl was not caught as a material issue: " + string.Join( "; ", diffs.Select( d => d.ToString() ) ) );
		} );

		Check( "document comparison catches material and plane drift", () =>
		{
			var room = Room();
			var materialSwapped = room.Clone();
			materialSwapped.WorldBrushes[0].Faces[0].Material = "brick/brickwall001";
			var materialDiffs = MapExportRoundtrip.CompareDocuments( room, materialSwapped, "vmf" );
			Require( materialDiffs.Any( d => d.Code.Contains( "MATERIAL", StringComparison.Ordinal ) ), "material swap was not caught" );
			Require( materialDiffs.All( d => !d.Code.Contains( "PLANE_DRIFT", StringComparison.Ordinal ) ), "material swap must not also report plane drift" );

			var planeDrifted = room.Clone();
			planeDrifted.WorldBrushes[6].Faces[5].PlanePoints[2] = new VmfVec( -16.0625, 48.5, 64.6 );
			var planeDiffs = MapExportRoundtrip.CompareDocuments( room, planeDrifted, "vmf" );
			Require( planeDiffs.Any( d => d.Code.Contains( "PLANE_DRIFT", StringComparison.Ordinal ) ), "0.5-unit plane drift was not caught" );
		} );

		Check( "glb container is valid but the mesh payload is blocked", () =>
		{
			var result = MapExportRoundtrip.CheckGlb( Room(), Work() );
			Require( result.Blocked, "GLB path must be BLOCKED until the payload wiring lands, not passed" );
			Require( result.BlockedReason.Contains( "minimal", StringComparison.Ordinal ), "blocked reason must name the minimal-GLB stub" );
			Require( result.Issues.Any( i => i.Code == "EXPORT_ROUNDTRIP_GLB_PAYLOAD_MISSING" ), "missing payload must be a named issue" );
			Require( result.Issues.All( i => i.Code != "EXPORT_ROUNDTRIP_GLB_CONTAINER" ), "container itself must validate: " + string.Join( "; ", result.Issues.Select( i => i.ToString() ) ) );
			Require( !result.Passed, "a blocked path must not report Passed" );
		} );

		Check( "glb core writer round-trips the baked mesh", () =>
		{
			// The seam worker 17 must wire into ExportGlb, proven here
			// at the core level: one GlbPart per material, Write ->
			// GlbImport.Read, then pieces / names / triangles /
			// positions compared with the baked mesh. Units convert
			// inches -> metres -> inches; positions compare at 0.01 in.
			var baked = BakedRoom();
			var parts = new List<GlbPart>();
			for ( var slot = 0; slot < baked.Materials.Count; slot++ )
			{
				var mesh = new PolyMesh { Positions = baked.Mesh.Positions };
				foreach ( var f in baked.Mesh.Faces.Where( f => f.Material == slot ) )
					mesh.AddFace( f.Indices, f.UVs ?? new Vec2[f.Indices.Length], 0 );
				parts.Add( new GlbPart( baked.Materials[slot], mesh,
					new GlbMaterial( baked.Materials[slot], new Vec4( 1f, 1f, 1f, 1f ), 0f, 1f, new Vec3( 0f, 0f, 0f ), null, null, null, null ) ) );
			}
			var bytes = GlbExport.Write( parts );
			var result = GlbImport.Read( bytes, "sealed-room.glb" );
			Require( result.Pieces.Count == baked.Materials.Count,
				$"GLB pieces {result.Pieces.Count} != materials {baked.Materials.Count}" );
			var pieceNames = result.Pieces.Select( p => p.Name ).OrderBy( n => n, StringComparer.Ordinal ).ToList();
			var materialNames = baked.Materials.OrderBy( n => n, StringComparer.Ordinal ).ToList();
			Require( pieceNames.SequenceEqual( materialNames, StringComparer.Ordinal ),
				$"GLB piece names [{string.Join( ", ", pieceNames )}] != materials [{string.Join( ", ", materialNames )}]" );
			var expectedTriangles = baked.Mesh.Faces.Sum( f => f.Indices.Length - 2 );
			var importedFaces = result.Pieces.Sum( p => p.Mesh.FaceCount );
			Require( importedFaces == expectedTriangles, $"GLB triangles {importedFaces} != baked triangulated {expectedTriangles}" );
			var importedPositions = result.Pieces.SelectMany( p => p.Mesh.Positions ).ToList();
			foreach ( var p in baked.Mesh.Positions )
			{
				var found = false;
				foreach ( var q in importedPositions )
					if ( Math.Abs( p.x - q.x ) <= 0.01f && Math.Abs( p.y - q.y ) <= 0.01f && Math.Abs( p.z - q.z ) <= 0.01f ) { found = true; break; }
				Require( found, $"baked position {p} has no match in the GLB round-trip" );
			}
			var slotNames = result.MaterialSlots.Values.OrderBy( n => n, StringComparer.Ordinal ).ToList();
			Require( slotNames.SequenceEqual( materialNames, StringComparer.Ordinal ),
				$"GLB material slots [{string.Join( ", ", slotNames )}] != materials [{string.Join( ", ", materialNames )}]" );
		} );

		Check( "sealed-room passes native compile round-trip", () =>
		{
			var work = Work();
			var result = MapExportRoundtrip.CheckCompile( Room(), work, CompilePreset.Preview );
			RequirePassed( result, "compile" );
			Require( result.SourceFaceCount == 42 && result.ResultFaceCount == 42, $"lightmap faces {result.ResultFaceCount} != input faces 42" );
			Require( result.Notes.Any( n => n.Contains( "bsp leaves", StringComparison.Ordinal ) ), "compile notes must carry the BSP leaf counts" );
			foreach ( var file in new[] { "sealed-room.bsp", "sealed-room.pvs", "sealed-room.lightmap", "sealed-room.compile.log" } )
				Require( File.Exists( Path.Combine( work, "compile", file ) ), $"compile product '{file}' is missing" );
			Require( !File.Exists( Path.Combine( work, "compile", "sealed-room.lin" ) ), "sealed room must not write a leak point file" );
		} );

		Check( "compile catches a leaking room", () =>
		{
			var work = Work();
			var leaking = Room();
			leaking.WorldBrushes.RemoveAt( 1 ); // ceiling gone: the void is reachable
			var result = MapExportRoundtrip.CheckCompile( leaking, work, CompilePreset.Preview );
			Require( !result.Passed, "leaking room must not pass the compile round-trip" );
			Require( result.Issues.Any( i => i.Code == "EXPORT_ROUNDTRIP_COMPILE_LEAK" ),
				"leak was not named: " + string.Join( "; ", result.Issues.Select( i => i.ToString() ) ) );
			Require( File.Exists( Path.Combine( work, "compile", "sealed-room.lin" ) ), "leaking compile must write the .lin point file" );
			Require( !File.Exists( Path.Combine( work, "compile", "sealed-room.bsp" ) ), "failed compile must claim no .bsp product" );
		} );

		Check( "export round-trip rule plugs into IssueEngine", () =>
		{
			var engine = new IssueEngine();
			Require( engine.Rules.Count == 10, $"engine must start with its 10 default rules, has {engine.Rules.Count}" );
			engine.Register( new ExportRoundtripRule() );
			Require( engine.Rules.Count == 11, "export-roundtrip rule did not register" );
			Throws<ArgumentException>( () => engine.Register( new ExportRoundtripRule() ), "duplicate export-roundtrip rule must be refused" );

			var clean = engine.Run( MapExportRoundtrip.ToIssueDocument( Room() ) );
			Require( clean.All( i => !i.Code.StartsWith( "EXPORT_ROUNDTRIP", StringComparison.Ordinal ) ),
				"clean sealed room must raise no export-roundtrip issues: " + string.Join( "; ", clean.Where( i => i.Code.StartsWith( "EXPORT_ROUNDTRIP", StringComparison.Ordinal ) ).Select( i => i.ToString() ) ) );

			// A non-finite plane normal is silently zeroed by the writer
			// (MapIoText.Num maps NaN to "0") — the rule must name that
			// loss instead of letting it through.
			var broken = MapExportRoundtrip.ToIssueDocument( Room() );
			broken.Brushes[0].Faces[0].Normal = new Vec3( float.NaN, 0f, 0f );
			var found = engine.Run( broken );
			Require( found.Any( i => i.Code.StartsWith( "EXPORT_ROUNDTRIP", StringComparison.Ordinal ) ),
				"NaN plane normal was not caught by the export-roundtrip rule" );
		} );

		Check( "checked-in fixtures match the builder", () =>
		{
			var room = Room();
			var vmf = VmfIo.ParseFile( Fixture( "room.vmf" ) );
			var vmfDiffs = MapExportRoundtrip.CompareDocuments( room, vmf, "vmf" );
			Require( vmfDiffs.Count == 0, "room.vmf differs from the builder: " + string.Join( "; ", vmfDiffs.Select( d => d.ToString() ) ) );
			var map = Map220Io.ParseFile( Fixture( "room.map" ) );
			var mapDiffs = MapExportRoundtrip.CompareDocuments( room, map, "map220" );
			Require( mapDiffs.Count == 0, "room.map differs from the builder: " + string.Join( "; ", mapDiffs.Select( d => d.ToString() ) ) );
		} );

		Check( "validate-all aggregates all five paths", () =>
		{
			var result = MapExportRoundtrip.ValidateAll( Room(), Work() );
			Require( result.Paths.Count == 6, $"expected source + 5 export paths, got {result.Paths.Count}" );
			Require( result.Paths.First( p => p.Path == "source" ).Passed, "source must pass" );
			foreach ( var path in new[] { "vmf", "map220", "obj", "compile" } )
				Require( result.Paths.First( p => p.Path == path ).Passed, $"path '{path}' must pass" );
			var glb = result.Paths.First( p => p.Path == "glb" );
			Require( glb.Blocked, "glb must be blocked (payload stub)" );
			Require( !result.Passed, "overall result must not pass while the GLB payload is blocked" );
			Require( result.AllIssues.Any( i => i.Code == "EXPORT_ROUNDTRIP_GLB_PAYLOAD_MISSING" ), "overall issues must name the GLB payload block" );
			var issueEngineIssues = result.ToIssues();
			Require( issueEngineIssues.Any( i => i.Code == "EXPORT_ROUNDTRIP_GLB_PAYLOAD_MISSING" ), "ToIssues must carry the GLB payload block" );
		} );
	}
}
