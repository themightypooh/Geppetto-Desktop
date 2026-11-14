#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;

/// <summary>
/// hammer-obj-glb-map-export: bake a MapDocument (brushes + displacements)
/// to a static mesh with per-face materials and export OBJ + GLB through the
/// existing exporters (ObjWriter + GlTfExporter), preserving material names.
///
/// DEPENDENCY NOTE (2026-11-13, muse-3-desktop-hammer-17): FaceMaterial,
/// MapBrush and Displacement HAVE landed and are used directly (the last two
/// landed mid-task; the adapters MapBakeBrush.FromMapBrush and
/// MapBakeDisplacement.FromDisplacement plus the MapBrush Bake overload exist
/// for them and are covered below). MapDocument has NOT landed — no
/// kernel/Effigy/Map/MapDocument.cs on disk — so the document half of these
/// tests still exercises MapBakeExport against that spec exactly as filed,
/// via MapBakeDocument; when it lands, the integrator maps
/// MapDocument.Brushes/Displacements onto it and re-runs this group.
/// Sealed-room input mirrors the fixture in
/// tests/fixtures/hammer/hammer-obj-glb-map-export/.
/// One honest limit is asserted, not hidden: GlTfExporter currently writes
/// a minimal GLB (header + asset JSON, mesh argument ignored), so the GLB
/// test checks the file/header plus the counts/names on the export result —
/// it does NOT claim a GLB round-trip until the core GlbExport wiring lands
/// (see MapBakeExport.GlbNote).
/// </summary>
internal static class MapBakeExportTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "mapbakeexport", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, float tolerance, string what )
		{
			if ( MathF.Abs( actual - expected ) > tolerance ) throw new Exception( what + ": expected " + expected + " ±" + tolerance + ", got " + actual );
		}
		void Throws<T>( Action f, string what ) where T : Exception
		{
			try { f(); }
			catch ( T ) { return; }
			catch ( Exception e ) { throw new Exception( what + ": threw " + e.GetType().Name + ", not " + typeof( T ).Name ); }
			throw new Exception( what + ": did not throw" );
		}

		const string Concrete = "concrete/floor001";
		const string Plaster = "plaster/ceiling001";
		const string Brick = "brick/brickwall001";
		const string Grass = "nature/grass001";
		const string Dirt = "nature/dirt001";
		string NoDraw = ToolTexture.NoDrawName;

		FaceMaterial Mat( string name ) => FaceMaterial.ForMaterial( name );
		FaceMaterial NoDrawMat() => FaceMaterial.NoDraw;

		// Face materials in Box order: -X, +X, -Y, +Y, -Z (floor), +Z (ceiling).
		MapBakeBrush RoomBox( string name, Vec3 min, Vec3 max, int keptFace, string keptMaterial )
		{
			var faces = new FaceMaterial[6];
			for ( var i = 0; i < 6; i++ ) faces[i] = NoDrawMat();
			faces[keptFace] = Mat( keptMaterial );
			var brush = MapBakeBrush.Box( min, max, faces );
			return new MapBakeBrush( name, brush.Faces );
		}

		MapBakeDisplacement RoomPatch()
		{
			var corners = new[]
			{
				new Vec3( -64f, -64f, 1f ), new Vec3( 64f, -64f, 1f ),
				new Vec3( 64f, 64f, 1f ), new Vec3( -64f, 64f, 1f ),
			};
			var offsets = new float[]
			{
				0, 0, 0, 0, 0,
				0, 1, 2, 1, 0,
				0, 2, 4, 2, 0,
				0, 1, 2, 1, 0,
				0, 0, 0, 0, 0,
			};
			return new MapBakeDisplacement( "floor-patch", corners, 2, offsets, new float[25], Mat( Grass ), Mat( Dirt ) );
		}

		MapBakeDocument BuildSealedRoom() => new( "sealed-room",
			new[]
			{
				RoomBox( "floor", new Vec3( -128f, -128f, -16f ), new Vec3( 128f, 128f, 0f ), 5, Concrete ),
				RoomBox( "ceiling", new Vec3( -128f, -128f, 128f ), new Vec3( 128f, 128f, 144f ), 4, Plaster ),
				RoomBox( "wall-north", new Vec3( -128f, 112f, 0f ), new Vec3( 128f, 128f, 128f ), 2, Brick ),
				RoomBox( "wall-south", new Vec3( -128f, -128f, 0f ), new Vec3( 128f, -112f, 128f ), 3, Brick ),
				RoomBox( "wall-west", new Vec3( -128f, -128f, 0f ), new Vec3( -112f, 128f, 128f ), 1, Brick ),
				RoomBox( "wall-east", new Vec3( 112f, -128f, 0f ), new Vec3( 128f, 128f, 128f ), 0, Brick ),
			},
			new[] { RoomPatch() } );

		string? FindFixture( string name )
		{
			var rel = Path.Combine( "tests", "fixtures", "hammer", "hammer-obj-glb-map-export", name + ".json" );
			var starts = new[]
			{
				Directory.GetCurrentDirectory(),
				AppContext.BaseDirectory,
				Path.GetDirectoryName( typeof( MapBakeExportTests ).Assembly.Location ) ?? "",
			};
			foreach ( var start in starts )
			{
				var dir = new DirectoryInfo( start );
				while ( dir != null )
				{
					var candidate = Path.Combine( dir.FullName, rel );
					if ( File.Exists( candidate ) ) return candidate;
					dir = dir.Parent;
				}
			}
			return null;
		}

		MapBakeDocument LoadFixtureDocument()
		{
			var path = FindFixture( "sealed-room" );
			if ( path is null ) throw new Exception( "fixture sealed-room.json not found under tests/fixtures/hammer/hammer-obj-glb-map-export/" );
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var root = doc.RootElement;
			var brushes = new List<MapBakeBrush>();
			foreach ( var b in root.GetProperty( "brushes" ).EnumerateArray() )
			{
				var faces = new List<MapBakePlaneFace>();
				foreach ( var f in b.GetProperty( "faces" ).EnumerateArray() )
				{
					var n = f.GetProperty( "normal" );
					var normal = new Vec3( n[0].GetSingle(), n[1].GetSingle(), n[2].GetSingle() );
					var distance = f.GetProperty( "distance" ).GetSingle();
					var mat = FaceMaterial.FromJsonString( f.GetProperty( "faceMaterial" ).GetRawText() );
					faces.Add( new MapBakePlaneFace( normal, distance, mat ) );
				}
				brushes.Add( new MapBakeBrush( b.GetProperty( "name" ).GetString() ?? "brush", faces ) );
			}
			var disps = new List<MapBakeDisplacement>();
			foreach ( var d in root.GetProperty( "displacements" ).EnumerateArray() )
			{
				var corners = d.GetProperty( "corners" ).EnumerateArray()
					.Select( c => new Vec3( c[0].GetSingle(), c[1].GetSingle(), c[2].GetSingle() ) ).ToArray();
				var power = d.GetProperty( "power" ).GetInt32();
				var offsets = d.GetProperty( "offsets" ).EnumerateArray().Select( x => x.GetSingle() ).ToArray();
				var alphas = d.GetProperty( "alphas" ).EnumerateArray().Select( x => x.GetSingle() ).ToArray();
				var matA = FaceMaterial.FromJsonString( d.GetProperty( "materialA" ).GetRawText() );
				var matB = FaceMaterial.FromJsonString( d.GetProperty( "materialB" ).GetRawText() );
				disps.Add( new MapBakeDisplacement( d.GetProperty( "name" ).GetString() ?? "displacement", corners, power, offsets, alphas, matA, matB ) );
			}
			return new MapBakeDocument( root.GetProperty( "name" ).GetString() ?? "map", brushes, disps );
		}

		void RequireRoomCounts( MapBakeMesh baked, string what )
		{
			Require( baked.FaceCount == 38, what + ": expected 38 mesh faces (6 brush quads + 32 displacement tris), got " + baked.FaceCount );
			Require( baked.VertexCount == 120, what + ": expected 120 vertices (24 brush + 96 displacement), got " + baked.VertexCount );
			Require( baked.BrushFaceCount == 6, what + ": expected 6 brush faces, got " + baked.BrushFaceCount );
			Require( baked.DisplacementFaceCount == 32, what + ": expected 32 displacement tris, got " + baked.DisplacementFaceCount );
			Require( baked.Materials.Count == 4, what + ": expected 4 materials, got " + baked.Materials.Count );
			Require( baked.Materials[0] == Concrete && baked.Materials[1] == Plaster && baked.Materials[2] == Brick && baked.Materials[3] == Grass,
				what + ": materials in first-appearance order should be concrete/plaster/brick/grass, got " + string.Join( ", ", baked.Materials ) );
			Require( baked.ExcludedNoDrawFaces == 30, what + ": expected 30 excluded nodraw faces, got " + baked.ExcludedNoDrawFaces );
			Require( baked.ExcludedDegenerateFaces == 0, what + ": expected 0 degenerate exclusions, got " + baked.ExcludedDegenerateFaces );
			Require( baked.Issues.Count == 0, what + ": expected no bake issues, got: " + string.Join( "; ", baked.Issues ) );
		}

		Check( "sealed-room-bake-counts", () =>
		{
			var baked = MapBakeExport.Bake( BuildSealedRoom() );
			RequireRoomCounts( baked, "sealed room" );
		} );

		Check( "sealed-room-fixture-matches-code", () =>
		{
			var fromFixture = MapBakeExport.Bake( LoadFixtureDocument() );
			RequireRoomCounts( fromFixture, "fixture room" );
			var fromCode = MapBakeExport.Bake( BuildSealedRoom() );
			Require( fromFixture.FaceCount == fromCode.FaceCount && fromFixture.VertexCount == fromCode.VertexCount,
				"fixture and in-code rooms must bake identically" );
			Require( fromFixture.Materials.SequenceEqual( fromCode.Materials ), "fixture and in-code material order must match" );
		} );

		Check( "nodraw-excluded-brush-kept", () =>
		{
			var doc = BuildSealedRoom();
			var floor = doc.Brushes[0];
			Require( floor.FaceCount == 6, "floor brush keeps all 6 faces" );
			var baked = MapBakeExport.Bake( new MapBakeDocument( "one-box",
				new[] { RoomBox( "floor", new Vec3( -128f, -128f, -16f ), new Vec3( 128f, 128f, 0f ), 5, Concrete ) },
				Array.Empty<MapBakeDisplacement>() ) );
			Require( baked.FaceCount == 1, "one textured face bakes to one quad, got " + baked.FaceCount );
			Require( baked.ExcludedNoDrawFaces == 5, "five nodraw faces excluded, got " + baked.ExcludedNoDrawFaces );
			Require( baked.Materials.Count == 1 && baked.Materials[0] == Concrete, "only the kept material is registered" );
			Require( floor.FaceCount == 6, "input brush still has all 6 faces after baking (nodraw stays in the brush)" );
		} );

		Check( "nodraw-only-material-absent", () =>
		{
			var baked = MapBakeExport.Bake( new MapBakeDocument( "all-nodraw",
				new[] { MapBakeBrush.Box( new Vec3( 0f, 0f, 0f ), new Vec3( 64f, 64f, 64f ), FaceMaterial.NoDraw ) },
				Array.Empty<MapBakeDisplacement>() ) );
			Require( baked.FaceCount == 0, "all-nodraw box bakes to no faces" );
			Require( baked.Materials.Count == 0, "a material used only by nodraw faces is never registered" );
			Require( baked.ExcludedNoDrawFaces == 6, "six nodraw faces excluded, got " + baked.ExcludedNoDrawFaces );
			Require( baked.Issues.Any( i => i.Contains( "nodraw" ) ), "all-nodraw brush is reported, not silent: " + string.Join( "; ", baked.Issues ) );
		} );

		Check( "obj-export-preserves-materials", () =>
		{
			var baked = MapBakeExport.Bake( BuildSealedRoom() );
			var objPath = Path.Combine( settings.WorkDirectory, "sealed-room.obj" );
			var result = MapBakeExport.ExportObj( baked, objPath );
			Require( result.ObjWritten, "OBJ export must report written" );
			Require( File.Exists( objPath ), "OBJ file must exist" );
			Require( File.Exists( result.MtlPath ), "MTL file must exist next to the OBJ" );
			var text = File.ReadAllText( objPath );
			Require( text.Contains( "mtllib sealed-room.mtl" ), "OBJ must reference its MTL library" );
			foreach ( var name in new[] { Concrete, Plaster, Brick, Grass } )
				Require( text.Contains( "usemtl " + name ), "OBJ must carry usemtl " + name );
			Require( !text.Contains( "usemtl " + NoDraw ), "nodraw must never appear as a usemtl" );
			Require( !text.Contains( Dirt ), "dirt (unused blend material) must not appear in the OBJ" );
			var mtl = File.ReadAllText( result.MtlPath );
			foreach ( var name in new[] { Concrete, Plaster, Brick, Grass } )
				Require( mtl.Contains( "newmtl " + name ), "MTL must carry newmtl " + name );
			var roundTrip = ObjReader.Read( text );
			Require( roundTrip.FaceCount == 38, "OBJ round-trip must give 38 faces, got " + roundTrip.FaceCount );
			Require( roundTrip.VertexCount == 120, "OBJ round-trip must give 120 vertices, got " + roundTrip.VertexCount );
			// ObjReader only recovers material_N slots: SlotFromMaterialName
			// ignores any other usemtl name by design (read-only exporter, not
			// edited here), so the round-tripped mesh collapses all named
			// materials to slot 0. Material identity is therefore checked at
			// the TEXT level: the faces following each usemtl run must be
			// concrete 1, plaster 1, brick 4, grass 32.
			var facesByMaterial = new Dictionary<string, int>( StringComparer.Ordinal );
			var current = "";
			foreach ( var line in text.Split( '\n' ) )
			{
				if ( line.StartsWith( "usemtl ", StringComparison.Ordinal ) ) current = line.Substring( "usemtl ".Length ).Trim();
				else if ( line.StartsWith( "f ", StringComparison.Ordinal ) && current.Length > 0 )
					facesByMaterial[current] = facesByMaterial.TryGetValue( current, out var n ) ? n + 1 : 1;
			}
			Require( facesByMaterial.Count == 4, "OBJ must carry 4 usemtl runs, got " + facesByMaterial.Count );
			Require( facesByMaterial.TryGetValue( Concrete, out var concreteFaces ) && concreteFaces == 1, "concrete run must hold 1 face" );
			Require( facesByMaterial.TryGetValue( Plaster, out var plasterFaces ) && plasterFaces == 1, "plaster run must hold 1 face" );
			Require( facesByMaterial.TryGetValue( Brick, out var brickFaces ) && brickFaces == 4, "brick run must hold 4 faces" );
			Require( facesByMaterial.TryGetValue( Grass, out var grassFaces ) && grassFaces == 32, "grass run must hold 32 faces" );
			Require( roundTrip.Faces.Count( f => f.Count == 4 ) == 6, "OBJ round-trip must keep the 6 brush quads as quads" );
			Require( roundTrip.Faces.Count( f => f.Count == 3 ) == 32, "OBJ round-trip must keep the 32 displacement tris" );
		} );

		Check( "glb-export-counts-and-header", () =>
		{
			var baked = MapBakeExport.Bake( BuildSealedRoom() );
			var glbPath = Path.Combine( settings.WorkDirectory, "sealed-room.glb" );
			var result = MapBakeExport.ExportGlb( baked, glbPath );
			Require( result.GlbWritten, "GLB export must report written" );
			Require( File.Exists( glbPath ), "GLB file must exist" );
			var bytes = File.ReadAllBytes( glbPath );
			Require( bytes.Length >= 12, "GLB must at least hold its header" );
			Require( bytes[0] == (byte)'g' && bytes[1] == (byte)'l' && bytes[2] == (byte)'T' && bytes[3] == (byte)'F',
				"GLB must start with the glTF magic" );
			Require( BitConverter.ToUInt32( bytes, 4 ) == 2, "GLB version must be 2" );
			Require( result.MeshFaceCount == 38, "GLB result must carry the baked face count (38), got " + result.MeshFaceCount );
			Require( result.MaterialCount == 4, "GLB result must carry the baked material count (4), got " + result.MaterialCount );
			Require( result.MaterialNames.SequenceEqual( new[] { Concrete, Plaster, Brick, Grass } ),
				"GLB result must preserve material names in order" );
			Require( result.Note.Contains( "GlTfExporter" ), "GLB result must disclose the stub exporter limit, not hide it" );
		} );

		Check( "export-all-writes-both", () =>
		{
			var baked = MapBakeExport.Bake( BuildSealedRoom() );
			var result = MapBakeExport.ExportAll( baked,
				Path.Combine( settings.WorkDirectory, "both.obj" ),
				Path.Combine( settings.WorkDirectory, "both.glb" ) );
			Require( result.ObjWritten && result.GlbWritten, "ExportAll must write both files" );
			Require( File.Exists( result.ObjPath ) && File.Exists( result.MtlPath ) && File.Exists( result.GlbPath ),
				"ExportAll must leave OBJ + MTL + GLB on disk" );
			Require( result.MeshFaceCount == 38 && result.MaterialCount == 4, "ExportAll counts must match the bake" );
		} );

		Check( "displacement-power3-grid", () =>
		{
			var corners = new[]
			{
				new Vec3( 0f, 0f, 0f ), new Vec3( 128f, 0f, 0f ),
				new Vec3( 128f, 128f, 0f ), new Vec3( 0f, 128f, 0f ),
			};
			var disp = MapBakeDisplacement.Flat( "patch", corners, 3, Mat( Grass ) );
			Require( disp.GridSize == 9, "power 3 gives a 9x9 grid, got " + disp.GridSize );
			var baked = MapBakeExport.Bake( new MapBakeDocument( "disp-only", Array.Empty<MapBakeBrush>(), new[] { disp } ) );
			Require( baked.DisplacementFaceCount == 128, "power-3 grid has 64 cells = 128 tris, got " + baked.DisplacementFaceCount );
			Require( baked.VertexCount == 128 * 3, "face-split tris give 384 vertices, got " + baked.VertexCount );
			var uMin = float.MaxValue; var uMax = float.MinValue; var vMin = float.MaxValue; var vMax = float.MinValue;
			foreach ( var f in baked.Mesh.Faces )
				foreach ( var uv in f.UVs )
				{
					if ( uv.x < uMin ) uMin = uv.x;
					if ( uv.x > uMax ) uMax = uv.x;
					if ( uv.y < vMin ) vMin = uv.y;
					if ( uv.y > vMax ) vMax = uv.y;
				}
			Near( uMin, 0f, 1e-4f, "displacement UV uMin" );
			Near( uMax, 1f, 1e-4f, "displacement UV uMax" );
			Near( vMin, 0f, 1e-4f, "displacement UV vMin" );
			Near( vMax, 1f, 1e-4f, "displacement UV vMax" );
			Require( baked.Issues.Count == 0, "flat power-3 displacement must bake clean: " + string.Join( "; ", baked.Issues ) );
		} );

		Check( "displacement-blend-splits-materials", () =>
		{
			var corners = new[]
			{
				new Vec3( 0f, 0f, 0f ), new Vec3( 128f, 0f, 0f ),
				new Vec3( 128f, 128f, 0f ), new Vec3( 0f, 128f, 0f ),
			};
			var alphas = new float[25];
			for ( var j = 0; j < 5; j++ )
				for ( var i = 0; i < 5; i++ )
					alphas[j * 5 + i] = i >= 3 ? 1f : 0f;
			var disp = new MapBakeDisplacement( "blend", corners, 2, new float[25], alphas, Mat( Grass ), Mat( Dirt ) );
			var baked = MapBakeExport.Bake( new MapBakeDocument( "blend-only", Array.Empty<MapBakeBrush>(), new[] { disp } ) );
			Require( baked.Materials.Count == 2, "blend must register both materials, got " + string.Join( ", ", baked.Materials ) );
			Require( baked.Materials.Contains( Grass ) && baked.Materials.Contains( Dirt ), "blend materials must be grass + dirt" );
			var grassTris = baked.FaceInfo.Count( f => f.IsDisplacement && f.Material == Grass );
			var dirtTris = baked.FaceInfo.Count( f => f.IsDisplacement && f.Material == Dirt );
			Require( grassTris > 0 && dirtTris > 0, $"blend must put tris on both sides (grass {grassTris}, dirt {dirtTris})" );
			Require( grassTris + dirtTris == 32, "blend tris must total 32" );
		} );

		Check( "displacement-invalid-input-throws", () =>
		{
			var corners = new[]
			{
				new Vec3( 0f, 0f, 0f ), new Vec3( 128f, 0f, 0f ),
				new Vec3( 128f, 128f, 0f ), new Vec3( 0f, 128f, 0f ),
			};
			Throws<ArgumentException>( () => new MapBakeDisplacement( "bad-power", corners, 5, new float[25], new float[25], Mat( Grass ), Mat( Dirt ) ), "power 5" );
			Throws<ArgumentException>( () => new MapBakeDisplacement( "bad-power", corners, 1, new float[25], new float[25], Mat( Grass ), Mat( Dirt ) ), "power 1" );
			Throws<ArgumentException>( () => new MapBakeDisplacement( "bad-grid", corners, 2, new float[24], new float[25], Mat( Grass ), Mat( Dirt ) ), "24 offsets for a 25-vertex grid" );
			var badAlpha = new float[25];
			badAlpha[7] = 1.5f;
			Throws<ArgumentException>( () => new MapBakeDisplacement( "bad-alpha", corners, 2, new float[25], badAlpha, Mat( Grass ), Mat( Dirt ) ), "alpha 1.5" );
			Throws<ArgumentException>( () => new MapBakeDisplacement( "bad-corners", corners.Take( 3 ).ToArray(), 2, new float[25], new float[25], Mat( Grass ), Mat( Dirt ) ), "3 corners" );
			var flat = MapBakeDisplacement.Flat( "flat", corners, 2, Mat( Grass ) );
			Require( flat.Validate().Count == 0, "a valid displacement validates clean" );
			Require( flat.GridSize == 5 && flat.Offsets.All( o => o == 0f ), "Flat() gives a zero-offset power-2 grid" );
		} );

		Check( "box-volume", () =>
		{
			var baked = MapBakeExport.Bake( new MapBakeDocument( "one-box",
				new[] { MapBakeBrush.Box( new Vec3( 0f, 0f, 0f ), new Vec3( 64f, 32f, 16f ), Brick ) },
				Array.Empty<MapBakeDisplacement>() ) );
			Require( baked.FaceCount == 6, "a box bakes to 6 faces" );
			Near( baked.Mesh.SignedVolume(), 64f * 32f * 16f, 1f, "box signed volume" );
			Require( baked.Issues.Count == 0, "a valid box bakes clean: " + string.Join( "; ", baked.Issues ) );
		} );

		Check( "degenerate-brush-reported", () =>
		{
			var baked = MapBakeExport.Bake( new MapBakeDocument( "flat-box",
				new[] { MapBakeBrush.Box( new Vec3( 0f, 0f, 0f ), new Vec3( 0f, 0f, 0f ), Brick ) },
				Array.Empty<MapBakeDisplacement>() ) );
			Require( baked.FaceCount == 0, "a zero-volume box bakes to no faces" );
			Require( baked.Issues.Any( i => i.Contains( "zero volume" ) ),
			 "a zero-volume brush is reported by name, not silent: " + string.Join( "; ", baked.Issues ) );
		} );

		Check( "redundant-plane-reported", () =>
		{
			var faces = MapBakeBrush.Box( new Vec3( 0f, 0f, 0f ), new Vec3( 64f, 64f, 64f ), Brick ).Faces.ToList();
			faces.Add( new MapBakePlaneFace( new Vec3( 0f, 0f, 1f ), 1000f, Mat( Brick ) ) );
			var brush = new MapBakeBrush( "redundant", faces );
			var baked = MapBakeExport.Bake( new MapBakeDocument( "redundant-plane",
				new[] { brush }, Array.Empty<MapBakeDisplacement>() ) );
			Require( baked.FaceCount == 6, "the box still bakes its 6 faces, got " + baked.FaceCount );
			Require( baked.Issues.Any( i => i.Contains( "no polygon" ) ),
				"the plane that misses the solid is reported, not silent: " + string.Join( "; ", baked.Issues ) );
		} );

		Check( "uv-rotation-90-swaps-axes", () =>
		{
			var plain = MapBakeBrush.Box( new Vec3( 0f, 0f, 0f ), new Vec3( 64f, 64f, 64f ), Mat( "test/rot" ) );
			var rotatedFaces = plain.Faces.Select( ( f, i ) => i == 5
				? new MapBakePlaneFace( f.Normal, f.Distance, new FaceMaterial( f.Material.MaterialName, new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 90f, FaceUv.DefaultLightmapScale, 0 ) ) )
				: f ).ToList();
			var rotated = new MapBakeBrush( "rotated", rotatedFaces );
			var baked0 = MapBakeExport.Bake( new MapBakeDocument( "rot0", new[] { plain }, Array.Empty<MapBakeDisplacement>() ) );
			var baked90 = MapBakeExport.Bake( new MapBakeDocument( "rot90", new[] { rotated }, Array.Empty<MapBakeDisplacement>() ) );
			var uv0 = baked0.Mesh.Faces[baked0.FaceInfo.First( f => f.FaceIndex == 5 ).MeshFaceIndex].UVs;
			var uv90 = baked90.Mesh.Faces[baked90.FaceInfo.First( f => f.FaceIndex == 5 ).MeshFaceIndex].UVs;
			Require( uv0.Length == uv90.Length, "rotation must not change the corner count" );
			for ( var i = 0; i < uv0.Length; i++ )
			{
				Near( uv90[i].x, -uv0[i].y, 1e-4f, $"90deg rotation swaps axes (corner {i} u)" );
				Near( uv90[i].y, uv0[i].x, 1e-4f, $"90deg rotation swaps axes (corner {i} v)" );
			}
		} );

		Check( "fit-uvs-span-0-to-1", () =>
		{
			var probe = MapBakeExport.Bake( new MapBakeDocument( "probe",
				new[] { MapBakeBrush.Box( new Vec3( 0f, 0f, 0f ), new Vec3( 64f, 64f, 64f ), Mat( "test/fit" ) ) },
				Array.Empty<MapBakeDisplacement>() ) );
			var probeInfo = probe.FaceInfo.First( f => f.FaceIndex == 5 );
			var probeFace = probe.Mesh.Faces[probeInfo.MeshFaceIndex];
			var verts = probeFace.Indices.Select( i => probe.Mesh.Positions[i] ).ToList();
			var (uMin, uMax, vMin, vMax) = FaceUv.ProjectedBounds( verts, new Vec3( 0f, 0f, 1f ) );
			var fit = FaceUv.AlignFit( uMin, uMax, vMin, vMax );
			var box = MapBakeBrush.Box( new Vec3( 0f, 0f, 0f ), new Vec3( 64f, 64f, 64f ), Mat( "test/fit" ) );
			var fittedFaces = box.Faces.Select( ( f, i ) => i == 5
				? new MapBakePlaneFace( f.Normal, f.Distance, new FaceMaterial( f.Material.MaterialName, fit ) )
				: f ).ToList();
			var baked = MapBakeExport.Bake( new MapBakeDocument( "fitted",
				new[] { new MapBakeBrush( "fitted", fittedFaces ) }, Array.Empty<MapBakeDisplacement>() ) );
			var info = baked.FaceInfo.First( f => f.FaceIndex == 5 );
			var uvs = baked.Mesh.Faces[info.MeshFaceIndex].UVs;
			Near( uvs.Min( u => u.x ), 0f, 1e-4f, "Fit uMin" );
			Near( uvs.Max( u => u.x ), 1f, 1e-4f, "Fit uMax" );
			Near( uvs.Min( u => u.y ), 0f, 1e-4f, "Fit vMin" );
			Near( uvs.Max( u => u.y ), 1f, 1e-4f, "Fit vMax" );
		} );

		Check( "face-info-carries-smoothing-and-lightmap", () =>
		{
			var uv = new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 0f, 8f, 3 );
			var faces = new FaceMaterial[6];
			for ( var i = 0; i < 6; i++ ) faces[i] = Mat( Brick );
			faces[5] = new FaceMaterial( Brick, uv );
			var baked = MapBakeExport.Bake( new MapBakeDocument( "smooth",
				new[] { MapBakeBrush.Box( new Vec3( 0f, 0f, 0f ), new Vec3( 64f, 64f, 64f ), faces ) },
				Array.Empty<MapBakeDisplacement>() ) );
			var info = baked.FaceInfo.First( f => f.FaceIndex == 5 );
			Require( info.SmoothingGroup == 3, "smoothing group 3 must survive the bake, got " + info.SmoothingGroup );
			Near( info.LightmapScale, 8f, 1e-4f, "lightmap scale" );
			Require( info.Material == Brick && info.BrushIndex == 0 && !info.IsDisplacement, "face info must name brush, material and kind" );
			var disp = MapBakeDisplacement.Flat( "patch",
				new[] { new Vec3( 0f, 0f, 0f ), new Vec3( 64f, 0f, 0f ), new Vec3( 64f, 64f, 0f ), new Vec3( 0f, 64f, 0f ) },
				2, new FaceMaterial( Grass, uv ) );
			var bakedDisp = MapBakeExport.Bake( new MapBakeDocument( "smooth-disp", Array.Empty<MapBakeBrush>(), new[] { disp } ) );
			Require( bakedDisp.FaceInfo.All( f => f.IsDisplacement && f.DisplacementIndex == 0 && f.SmoothingGroup == 3 ),
				"displacement face info must carry its smoothing group" );
		} );

		Check( "zero-uv-scale-reported-not-silent", () =>
		{
			var badUv = new FaceUv( Vec2.Zero, new Vec2( 0f, 1f ), 0f, FaceUv.DefaultLightmapScale, 0 );
			var faces = new FaceMaterial[6];
			for ( var i = 0; i < 6; i++ ) faces[i] = Mat( Brick );
			faces[5] = new FaceMaterial( Brick, badUv );
			var baked = MapBakeExport.Bake( new MapBakeDocument( "bad-uv",
				new[] { MapBakeBrush.Box( new Vec3( 0f, 0f, 0f ), new Vec3( 64f, 64f, 64f ), faces ) },
				Array.Empty<MapBakeDisplacement>() ) );
			Require( baked.FaceCount == 6, "a zero UV scale must not drop the face" );
			Require( baked.Issues.Any( i => i.Contains( "UV scale X is zero" ) ),
				"a zero UV scale is reported by name, not silent: " + string.Join( "; ", baked.Issues ) );
		} );

		Check( "empty-document-bakes-empty", () =>
		{
			var baked = MapBakeExport.Bake( new MapBakeDocument( "empty", null, null ) );
			Require( baked.FaceCount == 0 && baked.VertexCount == 0, "an empty document bakes to an empty mesh" );
			Require( baked.Materials.Count == 0 && baked.Issues.Count == 0, "an empty document has no materials and no issues" );
			var empty = MapBakeExport.Bake( Array.Empty<MapBakeBrush>(), Array.Empty<MapBakeDisplacement>() );
			Require( empty.FaceCount == 0, "the list overload also bakes empty" );
		} );

		Check( "null-and-blank-args-throw", () =>
		{
			Throws<ArgumentNullException>( () => MapBakeExport.Bake( (MapBakeDocument)null! ), "Bake(null document)" );
			Throws<ArgumentNullException>( () => MapBakeExport.Bake( (IReadOnlyList<MapBakeBrush>)null!, null! ), "Bake(null brushes)" );
			var baked = MapBakeExport.Bake( BuildSealedRoom() );
			Throws<ArgumentNullException>( () => MapBakeExport.ExportObj( null!, "x.obj" ), "ExportObj(null baked)" );
			Throws<ArgumentException>( () => MapBakeExport.ExportObj( baked, "" ), "ExportObj(blank path)" );
			Throws<ArgumentNullException>( () => MapBakeExport.ExportGlb( null!, "x.glb" ), "ExportGlb(null baked)" );
			Throws<ArgumentException>( () => MapBakeExport.ExportGlb( baked, "  " ), "ExportGlb(blank path)" );
			Throws<ArgumentException>( () => new MapBakeBrush( "few", new[]
			{
				new MapBakePlaneFace( new Vec3( 1f, 0f, 0f ), 1f, Mat( Brick ) ),
				new MapBakePlaneFace( new Vec3( 0f, 1f, 0f ), 1f, Mat( Brick ) ),
				new MapBakePlaneFace( new Vec3( 0f, 0f, 1f ), 1f, Mat( Brick ) ),
			} ), "a brush with 3 planes" );
			Throws<ArgumentException>( () => new MapBakePlaneFace( Vec3.Zero, 0f, Mat( Brick ) ), "a zero-length plane normal" );
			Throws<ArgumentException>( () => MapBakeBrush.Box( new Vec3( 0f, 0f, 0f ), new Vec3( 1f, 1f, 1f ), new FaceMaterial[5] ), "a box with 5 face materials" );
		} );
		Check( "sealed-room-from-real-mapbrushes", () =>
		{
			MapBrush RoomMapBrush( string name, Vec3 min, Vec3 max, int keptFace, string keptMaterial )
			{
				var mats = new FaceMaterial[6];
				for ( var i = 0; i < 6; i++ ) mats[i] = NoDrawMat();
				mats[keptFace] = Mat( keptMaterial );
				var normals = new[]
				{
					new Vec3( -1f, 0f, 0f ), new Vec3( 1f, 0f, 0f ), new Vec3( 0f, -1f, 0f ),
					new Vec3( 0f, 1f, 0f ), new Vec3( 0f, 0f, -1f ), new Vec3( 0f, 0f, 1f ),
				};
				var distances = new[] { -min.x, max.x, -min.y, max.y, -min.z, max.z };
				var faces = new MapBrushFace[6];
				for ( var i = 0; i < 6; i++ ) faces[i] = new MapBrushFace( normals[i], distances[i], mats[i] );
				return MapBrush.FromPlanes( faces );
			}
			var brushes = new[]
			{
				RoomMapBrush( "floor", new Vec3( -128f, -128f, -16f ), new Vec3( 128f, 128f, 0f ), 5, Concrete ),
				RoomMapBrush( "ceiling", new Vec3( -128f, -128f, 128f ), new Vec3( 128f, 128f, 144f ), 4, Plaster ),
				RoomMapBrush( "wall-north", new Vec3( -128f, 112f, 0f ), new Vec3( 128f, 128f, 128f ), 2, Brick ),
				RoomMapBrush( "wall-south", new Vec3( -128f, -128f, 0f ), new Vec3( 128f, -112f, 128f ), 3, Brick ),
				RoomMapBrush( "wall-west", new Vec3( -128f, -128f, 0f ), new Vec3( -112f, 128f, 128f ), 1, Brick ),
				RoomMapBrush( "wall-east", new Vec3( 112f, -128f, 0f ), new Vec3( 128f, 128f, 128f ), 0, Brick ),
			};
			foreach ( var b in brushes )
				Require( b.IsValid, "room brush must validate clean: " + string.Join( "; ", b.Validate() ) );
			var baked = MapBakeExport.Bake( brushes, new[] { RoomPatch() } );
			RequireRoomCounts( baked, "real-MapBrush sealed room" );
			var objPath = Path.Combine( settings.WorkDirectory, "real-brush-room.obj" );
			var result = MapBakeExport.ExportObj( baked, objPath );
			Require( result.ObjWritten && File.Exists( objPath ), "real-MapBrush room must export OBJ" );
			Require( ObjReader.Read( File.ReadAllText( objPath ) ).FaceCount == 38, "real-MapBrush OBJ round-trip must give 38 faces" );
		} );

		Check( "real-mapbrush-defects-surface", () =>
		{
			var degenerate = MapBrush.Box( new Vec3( 0f, 0f, 0f ), new Vec3( 0f, 0f, 0f ), Brick );
			Require( !degenerate.IsValid, "a zero-volume MapBrush must not validate" );
			Require( degenerate.Validate().Any( d => d.Code == MapBrushDefect.ZeroVolume ),
				"a zero-volume MapBrush must report ZeroVolume: " + string.Join( "; ", degenerate.Validate() ) );
			var baked = MapBakeExport.Bake( new[] { degenerate }, Array.Empty<MapBakeDisplacement>() );
			Require( baked.FaceCount == 0, "a zero-volume MapBrush bakes to no faces" );
			Require( baked.Issues.Any( i => i.Contains( "ZeroVolume" ) ),
				"MapBrush Validate() defects must surface as bake issues: " + string.Join( "; ", baked.Issues ) );
		} );

		Check( "real-displacement-adapter-preserves-uvs", () =>
		{
			var corners = new[]
			{
				new Vec3( -64f, -64f, 1f ), new Vec3( 64f, -64f, 1f ),
				new Vec3( 64f, 64f, 1f ), new Vec3( -64f, 64f, 1f ),
			};
			var face = Effigy.Map.MapBrushFace.FromCorners( corners );
			var disp = Effigy.Map.Displacement.Create( face, 2 );
			for ( var j = 0; j < 5; j++ )
				for ( var i = 0; i < 5; i++ )
					disp.SetOffset( j, i, RoomPatch().Offsets[j * 5 + i] );
			Require( disp.IsValid, "the adapter fixture displacement must validate clean" );
			var adapted = MapBakeDisplacement.FromDisplacement( disp, Mat( Grass ), Mat( Dirt ), "floor-patch" );
			Require( adapted.GridSize == 5 && adapted.Offsets[12] == 4f, "adapter must carry power, offsets and grid" );
			Require( adapted.Uvs is not null && adapted.Uvs.Length == 25, "adapter must carry the exact corner UVs" );
			for ( var k = 0; k < 25; k++ )
			{
				var expected = disp.UvAt( k / 5, k % 5 );
				Near( adapted.Uvs![k].x, expected.x, 1e-4f, $"adapter UV {k} u" );
				Near( adapted.Uvs![k].y, expected.y, 1e-4f, $"adapter UV {k} v" );
			}
			var baked = MapBakeExport.Bake( new MapBakeDocument( "adapted-disp",
				Array.Empty<MapBakeBrush>(), new[] { adapted } ) );
			Require( baked.DisplacementFaceCount == 32, "adapted displacement bakes 32 tris, got " + baked.DisplacementFaceCount );
			Require( baked.Materials.Count == 1 && baked.Materials[0] == Grass, "all-zero alphas bake to grass only" );
			var bakedUvs = baked.Mesh.Faces.SelectMany( f => f.UVs ).ToList();
			Require( bakedUvs.Any( u => MathF.Abs( u.x - 0f ) < 1e-4f && MathF.Abs( u.y - 0f ) < 1e-4f ),
				"baked UVs must include the exact corner UV (0, 0)" );
			Require( bakedUvs.Any( u => MathF.Abs( u.x - 128f ) < 1e-4f && MathF.Abs( u.y + 128f ) < 1e-4f ),
				"baked UVs must include the exact corner UV (128, -128)" );
			Require( baked.Issues.Count == 0, "adapted displacement must bake clean: " + string.Join( "; ", baked.Issues ) );
			Throws<ArgumentNullException>( () => MapBakeDisplacement.FromDisplacement( null!, Mat( Grass ), Mat( Dirt ) ), "FromDisplacement(null)" );
			Throws<ArgumentNullException>( () => MapBakeDisplacement.FromDisplacement( disp, null!, Mat( Dirt ) ), "FromDisplacement(null materialA)" );
		} );

	}
}
