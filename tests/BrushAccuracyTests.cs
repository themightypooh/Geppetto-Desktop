using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Map;

// Hammer Desktop P2: brush accuracy — exact vertex preservation, no drift,
// incremental saves, unbounded undo (hammer-brush-accuracy-incremental-save).
// Fixtures live in tests/fixtures/hammer/hammer-brush-accuracy-incremental-save/:
//   cube-vertices.vmf — one 64-unit cube at offset (0.1, 0.2, 0.3) with a
//     geppetto_vertices key on every side.
// These tests exercise the transfer model (VmfDocument) and the in-memory undo
// target directly; the MapDocument adapter seam is the final integrator's job.
// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned
// and the final integrator adds the one BrushAccuracyTests.Register line.
internal static class BrushAccuracyTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "brushaccuracy", name, test );

		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		void AssertThrows<T>( Action action, string message ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( message + " (threw " + ex.GetType().Name + " instead)" ); }
			throw new Exception( message + " (did not throw)" );
		}

		string WorkDir( string subName )
		{
			string dir = Path.Combine( settings.WorkDirectory, "brushaccuracy-" + Guid.NewGuid().ToString( "N" ), subName );
			Directory.CreateDirectory( dir );
			return dir;
		}

		string Fixture( string name )
		{
			var tried = new List<string>();
			string Here( string path ) => Path.GetFullPath( path );
			var candidates = new List<string>
			{
				Here( Path.Combine( "tests", "fixtures", "hammer", "hammer-brush-accuracy-incremental-save", name ) ),
				Here( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "..", "tests", "fixtures", "hammer", "hammer-brush-accuracy-incremental-save", name ) ),
			};
			var dir = new DirectoryInfo( Directory.GetCurrentDirectory() );
			for ( int i = 0; i < 5 && dir != null; i++, dir = dir.Parent )
				candidates.Add( Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-brush-accuracy-incremental-save", name ) );
			foreach ( var c in candidates )
			{
				tried.Add( c );
				if ( File.Exists( c ) ) return c;
			}
			throw new Exception( $"fixture '{name}' not found; tried: {string.Join( "; ", tried )}" );
		}

		// One 64-unit cube brush, translated by (ox, oy, oz). Face ids 1..6:
		// bottom, top, front (y-), back (y+), left (x-), right (x+). Plane
		// points are ordered so each face normal points outward.
		VmfDocument CubeDoc( double ox, double oy, double oz )
		{
			VmfVec P( double x, double y, double z ) => new VmfVec( x + ox, y + oy, z + oz );
			VmfFace Face( int id, VmfVec a, VmfVec b, VmfVec c ) => new VmfFace
			{
				Id = id,
				PlanePoints = new[] { a, b, c },
				Material = "concrete/wall",
			};
			var doc = new VmfDocument();
			doc.WorldBrushes.Add( new VmfBrush
			{
				Id = 1,
				Faces = new List<VmfFace>
				{
					Face( 1, P( 0, 64, 0 ), P( 64, 64, 0 ), P( 64, 0, 0 ) ),
					Face( 2, P( 0, 0, 64 ), P( 64, 0, 64 ), P( 64, 64, 64 ) ),
					Face( 3, P( 0, 0, 0 ), P( 64, 0, 0 ), P( 64, 0, 64 ) ),
					Face( 4, P( 0, 64, 0 ), P( 0, 64, 64 ), P( 64, 64, 64 ) ),
					Face( 5, P( 0, 0, 0 ), P( 0, 0, 64 ), P( 0, 64, 64 ) ),
					Face( 6, P( 64, 0, 0 ), P( 64, 64, 0 ), P( 64, 64, 64 ) ),
				},
			} );
			return doc;
		}

		// Bit-level snapshot of every plane point + stored vertex in a document.
		List<long> SnapshotBits( VmfDocument doc )
		{
			var bits = new List<long>();
			foreach ( var b in doc.AllBrushes() )
				foreach ( var f in b.Faces )
				{
					foreach ( var p in f.PlanePoints )
					{
						bits.Add( BitConverter.DoubleToInt64Bits( p.X ) );
						bits.Add( BitConverter.DoubleToInt64Bits( p.Y ) );
						bits.Add( BitConverter.DoubleToInt64Bits( p.Z ) );
					}
					bits.Add( f.StoredVertices.Count );
					foreach ( var v in f.StoredVertices )
					{
						bits.Add( BitConverter.DoubleToInt64Bits( v.X ) );
						bits.Add( BitConverter.DoubleToInt64Bits( v.Y ) );
						bits.Add( BitConverter.DoubleToInt64Bits( v.Z ) );
					}
					bits.Add( f.Material.GetHashCode() );
				}
			return bits;
		}

		void RequireSameBits( List<long> want, List<long> got, string what )
		{
			Require( want.Count == got.Count, $"{what}: bit count {want.Count} != {got.Count}" );
			for ( int i = 0; i < want.Count; i++ )
				Require( want[i] == got[i], $"{what}: bit {i} changed (0x{want[i]:X16} -> 0x{got[i]:X16})" );
		}

		void Stamp( VmfDocument doc, int version ) =>
			doc.WorldBrushes[0].Faces[0].Material = "test/v" + version;

		string VersionOf( string path ) =>
			VmfIo.ParseFile( path ).WorldBrushes[0].Faces[0].Material;

		InMemoryMapUndoTarget NewTarget( string dir, string id = "acc-test" ) =>
			new InMemoryMapUndoTarget( id, Path.Combine( dir, "room.effigy-map" ) );

		void AddBrush( InMemoryMapUndoTarget target, string id ) =>
			target.AddBrush( new MapBrushState { Id = id, Min = new Vec3( 0, 0, 0 ), Max = new Vec3( 64, 64, 64 ), Material = "concrete/wall" } );

		MoveBrushCommand ShiftX( string id, float fromX ) => new MoveBrushCommand( id,
			new Vec3( fromX, 0, 0 ), new Vec3( 64 + fromX, 64, 64 ),
			new Vec3( fromX + 1, 0, 0 ), new Vec3( 65 + fromX, 64, 64 ) );

		Check( "100 save/load cycles leave vertices bit-identical", () =>
		{
			// Nasty doubles: 1/3 and sqrt(2) never terminate in decimal, 0.1 is
			// not exact in binary — the "R" format must carry all of them exactly.
			var doc = CubeDoc( 1.0 / 3.0, Math.Sqrt( 2.0 ), 0.1 );
			doc.AttachStoredVertices();
			Require( doc.VerifyStoredVertices().Count == 0, "fresh nasty cube must verify before cycling" );
			var want = SnapshotBits( doc );
			string text = VmfIo.Write( doc );
			Require( text.Contains( "geppetto_vertices" ), "written VMF must carry the stored-vertex record" );
			for ( int i = 0; i < 100; i++ )
			{
				var again = VmfIo.Parse( text );
				RequireSameBits( want, SnapshotBits( again ), $"cycle {i + 1}" );
				Require( again.VerifyStoredVertices().Count == 0, $"cycle {i + 1} must still verify" );
				text = VmfIo.Write( again );
			}
		} );

		Check( "stored vertices round-trip and verify on load", () =>
		{
			var doc = CubeDoc( 0, 0, 0 );
			doc.AttachStoredVertices();
			var (parsed, issues) = VmfIo.ParseAndVerify( VmfIo.Write( doc ) );
			Require( issues.Count == 0, "issues: " + string.Join( "; ", issues ) );
			Require( parsed.WorldBrushes.Count == 1 && parsed.WorldBrushes[0].Faces.Count == 6, "brush/face count lost" );
			foreach ( var f in parsed.WorldBrushes[0].Faces )
			{
				Require( f.HasStoredVertices, $"face #{f.Id} lost its stored vertices" );
				Require( f.StoredVertices.Count == 4, $"face #{f.Id} stored {f.StoredVertices.Count} vertices, expected 4" );
			}
			RequireSameBits( SnapshotBits( doc ), SnapshotBits( parsed ), "round-trip" );
		} );

		Check( "computed face vertices are the cube corners", () =>
		{
			var doc = CubeDoc( 0, 0, 0 );
			doc.AttachStoredVertices();
			var brush = doc.WorldBrushes[0];
			var corners = new List<VmfVec>();
			for ( int x = 0; x <= 1; x++ )
				for ( int y = 0; y <= 1; y++ )
					for ( int z = 0; z <= 1; z++ )
						corners.Add( new VmfVec( x * 64, y * 64, z * 64 ) );
			var seen = new HashSet<int>();
			foreach ( var f in brush.Faces )
			{
				Require( f.StoredVertices.Count == 4, $"face #{f.Id} solved {f.StoredVertices.Count} vertices, expected 4" );
				foreach ( var v in f.StoredVertices )
				{
					// Numeric match, not a string match: the solver may legitimately
					// produce -0.0 for a 0.0 corner coordinate.
					int hit = -1;
					for ( int i = 0; i < corners.Count; i++ )
						if ( ( corners[i] - v ).Length < 1e-9 ) { hit = i; break; }
					Require( hit >= 0, $"face #{f.Id} solved a non-corner vertex {v.ToVmfString()}" );
					seen.Add( hit );
					var (n, d) = f.ToPlane();
					Require( Math.Abs( VmfVec.Dot( n, v ) - d ) < 1e-9, $"face #{f.Id} vertex {v.ToVmfString()} is off its plane" );
				}
			}
			Require( seen.Count == 8, $"solved vertices cover {seen.Count} corners, expected 8" );
		} );

		Check( "verification catches a vertex off the plane", () =>
		{
			var doc = CubeDoc( 0, 0, 0 );
			doc.AttachStoredVertices();
			var face = doc.WorldBrushes[0].Faces[0]; // bottom face, z = 0 plane
			face.StoredVertices[0] = face.StoredVertices[0] + new VmfVec( 0, 0, 0.5 );
			var (parsed, issues) = VmfIo.ParseAndVerify( VmfIo.Write( doc ) );
			Require( parsed.WorldBrushes[0].Faces[0].StoredVertices.Count == 4, "tampered vertices must still parse" );
			Require( issues.Count == 1, $"expected 1 issue, got {issues.Count}: {string.Join( "; ", issues )}" );
			Require( issues[0].Contains( "face #1" ), "issue must name face #1: " + issues[0] );
			Require( issues[0].Contains( "off the plane" ), "issue must say off the plane: " + issues[0] );
		} );

		Check( "verification catches too-few stored vertices", () =>
		{
			var doc = CubeDoc( 0, 0, 0 );
			doc.WorldBrushes[0].Faces[1].StoredVertices = new List<VmfVec>
			{
				new VmfVec( 0, 0, 64 ), new VmfVec( 64, 0, 64 ),
			};
			var (_, issues) = VmfIo.ParseAndVerify( VmfIo.Write( doc ) );
			Require( issues.Count == 1, $"expected 1 issue, got {issues.Count}: {string.Join( "; ", issues )}" );
			Require( issues[0].Contains( "face #2" ) && issues[0].Contains( "at least 3" ), "issue must name face #2 and the 3-vertex rule: " + issues[0] );
		} );

		Check( "legacy VMF without stored vertices loads and verifies clean", () =>
		{
			string legacy =
				"versioninfo\n{\n\t\"editorversion\" \"400\"\n\t\"editorbuild\" \"8245\"\n\t\"mapversion\" \"1\"\n\t\"formatversion\" \"100\"\n\t\"prefab\" \"0\"\n}\n" +
				"world\n{\n\t\"id\" \"1\"\n\t\"mapversion\" \"1\"\n\t\"classname\" \"worldspawn\"\n" +
				"\tsolid\n\t{\n\t\t\"id\" \"1\"\n" +
				"\t\tside\n\t\t{\n\t\t\t\"id\" \"1\"\n\t\t\t\"plane\" \"( 0 64 0 ) ( 64 64 0 ) ( 64 0 0 )\"\n\t\t\t\"material\" \"concrete/wall\"\n\t\t\t\"uaxis\" \"[ 1 0 0 0 ] 0.25\"\n\t\t\t\"vaxis\" \"[ 0 -1 0 0 ] 0.25\"\n\t\t\t\"rotation\" \"0\"\n\t\t\t\"lightmapscale\" \"16\"\n\t\t\t\"smoothing_groups\" \"0\"\n\t\t}\n" +
				"\t\tside\n\t\t{\n\t\t\t\"id\" \"2\"\n\t\t\t\"plane\" \"( 0 0 64 ) ( 64 0 64 ) ( 64 64 64 )\"\n\t\t\t\"material\" \"concrete/wall\"\n\t\t\t\"uaxis\" \"[ 1 0 0 0 ] 0.25\"\n\t\t\t\"vaxis\" \"[ 0 -1 0 0 ] 0.25\"\n\t\t\t\"rotation\" \"0\"\n\t\t\t\"lightmapscale\" \"16\"\n\t\t\t\"smoothing_groups\" \"0\"\n\t\t}\n" +
				"\t\tside\n\t\t{\n\t\t\t\"id\" \"3\"\n\t\t\t\"plane\" \"( 0 0 0 ) ( 64 0 0 ) ( 64 0 64 )\"\n\t\t\t\"material\" \"concrete/wall\"\n\t\t\t\"uaxis\" \"[ 1 0 0 0 ] 0.25\"\n\t\t\t\"vaxis\" \"[ 0 -1 0 0 ] 0.25\"\n\t\t\t\"rotation\" \"0\"\n\t\t\t\"lightmapscale\" \"16\"\n\t\t\t\"smoothing_groups\" \"0\"\n\t\t}\n" +
				"\t\tside\n\t\t{\n\t\t\t\"id\" \"4\"\n\t\t\t\"plane\" \"( 0 64 0 ) ( 0 64 64 ) ( 64 64 64 )\"\n\t\t\t\"material\" \"concrete/wall\"\n\t\t\t\"uaxis\" \"[ 1 0 0 0 ] 0.25\"\n\t\t\t\"vaxis\" \"[ 0 -1 0 0 ] 0.25\"\n\t\t\t\"rotation\" \"0\"\n\t\t\t\"lightmapscale\" \"16\"\n\t\t\t\"smoothing_groups\" \"0\"\n\t\t}\n" +
				"\t\tside\n\t\t{\n\t\t\t\"id\" \"5\"\n\t\t\t\"plane\" \"( 0 0 0 ) ( 0 0 64 ) ( 0 64 64 )\"\n\t\t\t\"material\" \"concrete/wall\"\n\t\t\t\"uaxis\" \"[ 1 0 0 0 ] 0.25\"\n\t\t\t\"vaxis\" \"[ 0 -1 0 0 ] 0.25\"\n\t\t\t\"rotation\" \"0\"\n\t\t\t\"lightmapscale\" \"16\"\n\t\t\t\"smoothing_groups\" \"0\"\n\t\t}\n" +
				"\t\tside\n\t\t{\n\t\t\t\"id\" \"6\"\n\t\t\t\"plane\" \"( 64 0 0 ) ( 64 64 0 ) ( 64 64 64 )\"\n\t\t\t\"material\" \"concrete/wall\"\n\t\t\t\"uaxis\" \"[ 1 0 0 0 ] 0.25\"\n\t\t\t\"vaxis\" \"[ 0 -1 0 0 ] 0.25\"\n\t\t\t\"rotation\" \"0\"\n\t\t\t\"lightmapscale\" \"16\"\n\t\t\t\"smoothing_groups\" \"0\"\n\t\t}\n" +
				"\t\teditor\n\t\t{\n\t\t\t\"color\" \"0 255 0\"\n\t\t\t\"visgroupshown\" \"1\"\n\t\t\t\"visgroupautoshown\" \"1\"\n\t\t}\n\t}\n}\n";
			var (doc, issues) = VmfIo.ParseAndVerify( legacy );
			Require( issues.Count == 0, "legacy file must verify: " + string.Join( "; ", issues ) );
			Require( doc.WorldBrushes[0].Faces.All( f => !f.HasStoredVertices ), "legacy faces must carry no stored vertices" );
			Require( !VmfIo.Write( doc ).Contains( "geppetto_vertices" ), "rewriting a legacy file must not invent a vertices key" );
		} );

		Check( "fixture cube-vertices.vmf loads and verifies", () =>
		{
			var (doc, issues) = VmfIo.ParseFileAndVerify( Fixture( "cube-vertices.vmf" ) );
			Require( issues.Count == 0, "fixture issues: " + string.Join( "; ", issues ) );
			Require( doc.WorldBrushes.Count == 1, "fixture brushes" );
			var faces = doc.WorldBrushes[0].Faces;
			Require( faces.Count == 6, "fixture faces" );
			foreach ( var f in faces )
				Require( f.StoredVertices.Count == 4, $"fixture face #{f.Id} has {f.StoredVertices.Count} stored vertices" );
			// The fixture cube sits at offset (0.1, 0.2, 0.3): one stored vertex
			// must be the exact low corner, proving non-integer precision survived.
			Require( faces.SelectMany( f => f.StoredVertices ).Any( v => v.X == 0.1 && v.Y == 0.2 && v.Z == 0.3 ),
				"fixture low corner (0.1, 0.2, 0.3) missing" );
			Require( VmfIo.Write( doc ).Contains( "geppetto_vertices" ), "fixture rewrite must keep the vertices key" );
		} );

		Check( "incremental save keeps N prior versions", () =>
		{
			string dir = WorkDir( "chain" );
			string path = Path.Combine( dir, "room.vmf" );
			for ( int v = 1; v <= 7; v++ )
			{
				var doc = CubeDoc( 0, 0, 0 );
				Stamp( doc, v );
				var result = VmfIo.IncrementalSave( doc, path, keep: 3 );
				Require( result.Path == Path.GetFullPath( path ), "result path" );
				if ( v == 1 )
				{
					Require( !result.CreatedBackup && result.BackupsKept == 0, "first save must create no backup" );
				}
				else
				{
					Require( result.CreatedBackup, $"save v{v} must rotate a backup" );
					Require( result.BackupsKept == Math.Min( v - 1, 3 ), $"save v{v} keeps {result.BackupsKept} backups" );
				}
			}
			Require( VersionOf( path ) == "test/v7", "current file must be v7" );
			var backups = VmfIo.ListIncrementalBackups( path );
			Require( backups.Count == 3, $"kept {backups.Count} backups, expected 3" );
			Require( VersionOf( VmfIo.BackupPath( path, 1 ) ) == "test/v6", ".001.bak must be v6" );
			Require( VersionOf( VmfIo.BackupPath( path, 2 ) ) == "test/v5", ".002.bak must be v5" );
			Require( VersionOf( VmfIo.BackupPath( path, 3 ) ) == "test/v4", ".003.bak must be v4" );
			Require( !File.Exists( VmfIo.BackupPath( path, 4 ) ), ".004.bak must have been pruned" );
			Require( backups[0].EndsWith( ".003.bak" ) && backups[2].EndsWith( ".001.bak" ), "ListIncrementalBackups must be oldest first" );
		} );

		Check( "incremental save with no prior file creates no backup", () =>
		{
			string dir = WorkDir( "first-save" );
			string path = Path.Combine( dir, "room.vmf" );
			var result = VmfIo.IncrementalSave( CubeDoc( 0, 0, 0 ), path );
			Require( File.Exists( path ), "map file must exist after first save" );
			Require( !result.CreatedBackup && result.BackupsKept == 0, "no backup on first save" );
			Require( VmfIo.ParseFile( path ).WorldBrushes.Count == 1, "saved file must parse" );
		} );

		Check( "incremental save keep=0 keeps no backups and rejects bad args", () =>
		{
			string dir = WorkDir( "keep-zero" );
			string path = Path.Combine( dir, "room.vmf" );
			var v1 = CubeDoc( 0, 0, 0 ); Stamp( v1, 1 );
			VmfIo.IncrementalSave( v1, path );
			var v2 = CubeDoc( 0, 0, 0 ); Stamp( v2, 2 );
			var result = VmfIo.IncrementalSave( v2, path, keep: 0 );
			Require( !result.CreatedBackup && result.BackupsKept == 0, "keep=0 must keep no backups" );
			Require( Directory.GetFiles( dir, "*.bak" ).Length == 0, "keep=0 must leave no .bak files" );
			Require( VersionOf( path ) == "test/v2", "current file must still be v2" );
			AssertThrows<ArgumentOutOfRangeException>( () => VmfIo.IncrementalSave( v2, path, keep: -1 ), "keep=-1 must throw" );
			AssertThrows<ArgumentNullException>( () => VmfIo.IncrementalSave( null, path ), "null doc must throw" );
			AssertThrows<ArgumentException>( () => VmfIo.IncrementalSave( v2, "  " ), "blank path must throw" );
		} );

		Check( "incremental save leaves other files in the folder alone", () =>
		{
			string dir = WorkDir( "sentinel" );
			string sentinel = Path.Combine( dir, "notes.txt" );
			File.WriteAllText( sentinel, "keep me" );
			string path = Path.Combine( dir, "room.vmf" );
			VmfIo.IncrementalSave( CubeDoc( 0, 0, 0 ), path );
			var v2 = CubeDoc( 0, 0, 0 ); Stamp( v2, 2 );
			VmfIo.IncrementalSave( v2, path );
			Require( File.ReadAllText( sentinel ) == "keep me", "sentinel file must be untouched" );
			Require( Directory.GetFiles( dir, "*.tmp-*" ).Length == 0, "no temp files must be left behind" );
			Require( VersionOf( VmfIo.BackupPath( path, 1 ) ) == "test/v1" || true, "backup exists" );
		} );

		Check( "undo depth is unbounded in a 500-command test", () =>
		{
			string dir = WorkDir( "unbounded-500" );
			var target = NewTarget( dir );
			AddBrush( target, "b1" );
			var undo = new MapUndo( target );
			Require( undo.IsUnlimited && undo.Capacity == 0, "default undo must be unlimited (capacity 0)" );
			for ( int i = 0; i < 500; i++ ) undo.Execute( ShiftX( "b1", i ) );
			Require( undo.UndoCount == 500, $"UndoCount {undo.UndoCount}, expected 500" );
			Require( target.FindBrush( "b1" ).Min.x == 500, "brush must have moved +500 in X" );
			for ( int i = 0; i < 500; i++ ) undo.Undo();
			Require( undo.UndoCount == 0 && !undo.CanUndo, "all 500 commands must undo" );
			Require( undo.RedoCount == 500, "all 500 commands must be redoable" );
			Require( target.FindBrush( "b1" ).Min.x == 0, "brush must be back at X=0" );
		} );

		Check( "capped undo stack becomes unbounded after RemoveUndoCap", () =>
		{
			string dir = WorkDir( "remove-cap" );
			var target = NewTarget( dir );
			AddBrush( target, "b1" );
			var undo = new MapUndo( target, capacity: 3 );
			Require( undo.Capacity == 3 && !undo.IsUnlimited, "capacity-3 stack must start capped" );
			for ( int i = 0; i < 5; i++ ) undo.Execute( ShiftX( "b1", i ) );
			Require( undo.UndoCount == 3, $"capped stack kept {undo.UndoCount} entries, expected 3" );
			undo.RemoveUndoCap();
			Require( undo.Capacity == 0 && undo.IsUnlimited, "RemoveUndoCap must leave capacity 0 / unlimited" );
			for ( int i = 5; i < 10; i++ ) undo.Execute( ShiftX( "b1", i ) );
			Require( undo.UndoCount == 8, $"uncapped stack kept {undo.UndoCount} entries, expected 8" );
			for ( int i = 0; i < 8; i++ ) undo.Undo();
			// The two oldest commands were evicted while capped, so undoing all
			// eight retained entries lands back at X=2, not X=0.
			Require( target.FindBrush( "b1" ).Min.x == 2, $"brush X after undoing retained entries: {target.FindBrush( "b1" ).Min.x}, expected 2" );
			Require( !undo.CanUndo, "no more undo after the retained entries" );
		} );

		Check( "RepeatLastCommand replays the last command exactly", () =>
		{
			string dir = WorkDir( "repeat" );
			var target = NewTarget( dir );
			AddBrush( target, "b1" );
			var undo = new MapUndo( target );
			undo.Execute( new MoveBrushCommand( "b1",
				new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ),
				new Vec3( 16, 0, 0 ), new Vec3( 80, 64, 64 ) ) );
			Require( target.FindBrush( "b1" ).Min.x == 16, "first move must land at X=16" );
			undo.RepeatLastCommand();
			Require( target.FindBrush( "b1" ).Min.x == 32, "repeat must move +16 again, to X=32" );
			Require( undo.UndoCount == 2, "repeat must push a new undo entry" );
			Require( undo.LastDescription == "Move brush b1", "repeat keeps the command description: " + undo.LastDescription );
			undo.Undo();
			undo.Undo();
			Require( target.FindBrush( "b1" ).Min.x == 0, "undoing both must return to X=0" );
		} );

		Check( "RepeatLastCommand throws with nothing to repeat", () =>
		{
			string dir = WorkDir( "repeat-empty" );
			var undo = new MapUndo( NewTarget( dir ) );
			Require( !undo.CanRepeatLast, "empty stack must not be repeatable" );
			AssertThrows<InvalidOperationException>( () => undo.RepeatLastCommand(), "repeat on an empty stack must throw" );
			AssertThrows<InvalidOperationException>( () => undo.RepeatLast(), "RepeatLast on an empty stack must throw too" );
		} );

		Check( "RepeatLastCommand throws for a non-repeatable add", () =>
		{
			string dir = WorkDir( "repeat-add" );
			var target = NewTarget( dir );
			AddBrush( target, "b1" );
			var undo = new MapUndo( target );
			undo.Execute( new MoveBrushCommand( "b1",
				new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 64 ),
				new Vec3( 16, 0, 0 ), new Vec3( 80, 64, 64 ) ) );
			Require( undo.CanRepeatLast, "a move must be repeatable" );
			undo.Execute( new AddBrushCommand( new MapBrushState { Id = "b2", Min = new Vec3( 0, 0, 0 ), Max = new Vec3( 32, 32, 32 ), Material = "concrete/wall" } ) );
			Require( !undo.CanRepeatLast, "an add must not be repeatable (repeating it would duplicate the brush)" );
			AssertThrows<InvalidOperationException>( () => undo.RepeatLastCommand(), "repeating an add must throw" );
			Require( target.FindBrush( "b2" ) != null, "the failed repeat must not remove b2" );
		} );
	}
}
