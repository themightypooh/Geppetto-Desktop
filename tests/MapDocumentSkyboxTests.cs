using MapCompileSettings = Effigy.MapCompileSettings;
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Map;

/// <summary>
/// MapDocument binding for 2D/3D skybox + sky_camera + worldspawn map
/// properties (see kernel/Effigy/Map/MapDocumentSkybox.cs, board
/// hammer-skybox-2d-3d-worldspawn).
///
/// The acceptance numbers live in the fixture
/// tests/fixtures/hammer/hammer-skybox-2d-3d-worldspawn/mapdocument-skybox.gmap
/// and in the synthetic builders below; a missing fixture SKIPs its check,
/// it never passes silently.
///
/// These checks run against MapDocument — the native document model — plus
/// the real MapDocumentIo (.gmap), VmfIo (VMF text) and MapBakeExport.
/// The worldspawn chapter/title/detail/fog fields have no MapDocument slot
/// yet (see MapDocumentSkybox.cs): the gap check asserts the named
/// warning, and the VMF leg for those fields is covered by
/// SkyboxWorldspawnTests.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// MapDocumentSkyboxTests.Register(registry, settings); line.
/// </summary>
internal static class MapDocumentSkyboxTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test, Func<string?>? missing = null ) => registry.Add( "mapdocumentskybox", name, test, missing );

		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		bool HasWarning( IReadOnlyList<SkyboxWarning> warnings, string code )
		{
			foreach ( var w in warnings ) if ( w.Code == code ) return true;
			return false;
		}

		static string? FindFixture( string name )
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( Path.GetFullPath( start ) );
				for ( int depth = 0; depth < 10 && dir != null; depth++, dir = dir.Parent )
				{
					string candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-skybox-2d-3d-worldspawn", name );
					if ( File.Exists( candidate ) ) return candidate;
				}
			}
			return null;
		}

		static string? FindRegistry()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( Path.GetFullPath( start ) );
				for ( int depth = 0; depth < 10 && dir != null; depth++, dir = dir.Parent )
				{
					string candidate = Path.Combine( dir.FullName, "kernel", "Effigy", "Map", "registry", "core-source1.json" );
					if ( File.Exists( candidate ) ) return candidate;
				}
			}
			return null;
		}

		/// <summary>Axis-aligned box brush with outward-wound plane points (VmfFace.Normal must point out). Same winding as SkyboxWorldspawnTests.</summary>
		static VmfBrush BoxBrush( int id, double minX, double minY, double minZ, double maxX, double maxY, double maxZ, string material, int visGroupId = 0 )
		{
			var b = new VmfBrush { Id = id, VisGroupId = visGroupId };
			int faceId = id * 100;
			void Face( double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz )
			{
				b.Faces.Add( new VmfFace
				{
					Id = ++faceId,
					PlanePoints = new[] { new VmfVec( ax, ay, az ), new VmfVec( bx, by, bz ), new VmfVec( cx, cy, cz ) },
					Material = material,
				} );
			}
			Face( maxX, minY, minZ, maxX, maxY, minZ, maxX, maxY, maxZ ); // +X
			Face( minX, maxY, minZ, minX, minY, minZ, minX, minY, maxZ ); // -X
			Face( maxX, maxY, minZ, minX, maxY, minZ, minX, maxY, maxZ ); // +Y
			Face( minX, minY, minZ, maxX, minY, minZ, maxX, minY, maxZ ); // -Y
			Face( minX, minY, maxZ, maxX, minY, maxZ, maxX, maxY, maxZ ); // +Z
			Face( maxX, minY, minZ, minX, minY, minZ, minX, maxY, minZ ); // -Z
			return b;
		}

		/// <summary>Section box as MapBrushData, with a non-default FaceUv on face 0 (the -X face) so UV preservation is measured.</summary>
		static MapBrushData SectionBox( string id, float minX, float minY, float minZ, float maxX, float maxY, float maxZ, string material )
		{
			var brush = MapBrushData.Box( id, "Mountain", new Vec3( minX, minY, minZ ), new Vec3( maxX, maxY, maxZ ), material );
			var face0 = brush.Faces[0];
			brush.Faces[0] = new MapBrushFaceData( face0.Normal, face0.Distance,
				new FaceMaterial( material, new FaceUv( new Vec2( 0.25f, 0.5f ), new Vec2( 0.5f, 2f ), 30f, 32f, 3 ) ) );
			return brush;
		}

		static MapEntityData SkyCameraEntity( string id, float x, float y, float z, string scale, string targetName )
		{
			var entity = new MapEntityData { Id = id, ClassName = "sky_camera", Origin = new Vec3( x, y, z ) };
			entity.KeyValues["scale"] = scale;
			if ( !string.IsNullOrEmpty( targetName ) ) entity.KeyValues["targetname"] = targetName;
			return entity;
		}

		/// <summary>A doc with a room brush, a sky_camera and one section brush in the "3D Skybox" visgroup, sky sky_day01_01.</summary>
		static MapDocument SkyboxMapDocument()
		{
			var doc = MapDocument.Create( "skybox-test" );
			doc.AddVisGroup( new MapVisGroup { Id = "vg-skybox", Name = "3D Skybox", Colour = "#0080FF", Visible = true, CompileVisible = true } );
			doc.AddBrush( MapBrushData.Box( "brush-room", "Room", new Vec3( -512f, -512f, 0f ), new Vec3( 384f, 448f, 256f ), "BRICK/BRICKWALL001A" ) );
// NOTE: deliberately asymmetric — MapBrushData.Validate reports the
// opposite faces of an origin-centred box (min == -max) as "the same
// plane" (its same-plane check treats opposite normals with equal
// distances as one plane). That is a MapDocument bug, filed in the
// handoff for the MapDocument owner; the room here is scenery, so an
// asymmetric room keeps this ticket's checks on the skybox, not on it.
			var section = SectionBox( "2", 512f, 512f, 512f, 672f, 672f, 672f, "NATURE/CLIFF" );
			section.VisGroupIds.Add( "vg-skybox" );
			doc.AddBrush( section );
			doc.AddEntity( SkyCameraEntity( "3", 512f, 512f, 512f, "16", "sky_cam" ) );
			doc.SetCompileSettings( new MapCompileSettings { SkyName = "sky_day01_01" } );
			return doc;
		}

		/// <summary>The same data as a SkyboxWorldspawn aggregate (the apply leg's input).</summary>
		static SkyboxWorldspawn SkyboxAggregate()
		{
			var skybox = new SkyboxWorldspawn { MapId = "agg" };
			skybox.Worldspawn.SkyboxName = "sky_day01_01";
			skybox.Cameras.Add( new SkyCamera { Id = 3, TargetName = "sky_cam", Origin = new VmfVec( 512, 512, 512 ), Scale = 16 } );
			skybox.SectionOrigin = new VmfVec( 512, 512, 512 );
			skybox.SectionScale = 16;
			skybox.SectionBrushes.Add( BoxBrush( 2, 512, 512, 512, 672, 672, 672, "NATURE/CLIFF" ) );
			return skybox;
		}

		void RequirePlane( MapBrushFaceData face, Vec3 wantNormal, float wantDistance, float eps, string message )
		{
			Require( ( face.Normal - wantNormal ).Length < eps, $"{message}: normal {face.Normal} want {wantNormal}" );
			Require( Math.Abs( face.Distance - wantDistance ) < eps, $"{message}: distance {face.Distance} want {wantDistance}" );
		}

		void RequireUv( FaceUv got, FaceUv want, float eps, string message )
		{
			Require( Math.Abs( got.Offset.x - want.Offset.x ) <= eps && Math.Abs( got.Offset.y - want.Offset.y ) <= eps, $"{message}: offset" );
			Require( Math.Abs( got.Scale.x - want.Scale.x ) <= eps && Math.Abs( got.Scale.y - want.Scale.y ) <= eps, $"{message}: scale" );
			Require( Math.Abs( got.RotationDegrees - want.RotationDegrees ) <= eps, $"{message}: rotation" );
			Require( Math.Abs( got.LightmapScale - want.LightmapScale ) <= eps, $"{message}: lightmap" );
			Require( got.SmoothingGroup == want.SmoothingGroup, $"{message}: smoothing" );
		}

		Check( "gmap round-trip preserves sky, camera, section planes, materials and uvs", () =>
		{
			var doc = SkyboxMapDocument();
			var (skybox, readWarnings) = MapDocumentSkybox.FromMapDocument( doc );
			Require( readWarnings.Count == 0, "read warnings: " + string.Join( "; ", readWarnings.Select( w => w.Code ) ) );
			Require( skybox.Worldspawn.SkyboxName == "sky_day01_01", "skyname" );
			Require( skybox.HasSkyCamera, "camera missing" );
			Require( skybox.SectionBrushes.Count == 1, $"section brushes {skybox.SectionBrushes.Count}" );
			Require( skybox.Validate().Count == 0, "validate warnings: " + string.Join( "; ", skybox.Validate().Select( w => w.Code ) ) );

			var again = MapDocumentIo.Read( MapDocumentIo.Write( doc ) );
			Require( again.Validate().Count == 0, "reloaded doc defects: " + string.Join( "; ", again.Validate() ) );
			var (skybox2, readWarnings2) = MapDocumentSkybox.FromMapDocument( again );
			Require( readWarnings2.Count == 0, "reloaded read warnings: " + string.Join( "; ", readWarnings2.Select( w => w.Code ) ) );
			Require( skybox2.Worldspawn.SkyboxName == "sky_day01_01", "skyname lost" );
			Require( skybox2.HasSkyCamera && skybox2.Camera.TargetName == "sky_cam", "camera lost" );
			Require( Math.Abs( skybox2.Camera.Origin.X - 512 ) < 1e-6 && Math.Abs( skybox2.Camera.Origin.Y - 512 ) < 1e-6 && Math.Abs( skybox2.Camera.Origin.Z - 512 ) < 1e-6, "camera origin lost" );
			Require( Math.Abs( skybox2.Camera.Scale - 16 ) < 1e-9, "camera scale lost" );
			Require( skybox2.SectionBrushes.Count == 1, $"section brushes lost ({skybox2.SectionBrushes.Count})" );
			var want = MapBrushData.Box( "2", "Mountain", new Vec3( 512f, 512f, 512f ), new Vec3( 672f, 672f, 672f ), "NATURE/CLIFF" );
			var got = again.FindBrush( "2" );
			Require( got != null, "section brush id lost" );
			Require( got.Faces.Count == 6, $"section faces {got.Faces.Count}" );
			for ( int i = 0; i < 6; i++ )
			{
				RequirePlane( got.Faces[i], want.Faces[i].Normal, want.Faces[i].Distance, 1e-4f, $"face {i} plane" );
				Require( got.Faces[i].Material.MaterialName == "NATURE/CLIFF", $"face {i} material" );
			}
			RequireUv( got.Faces[0].Material.Uv,
				new FaceUv( new Vec2( 0.25f, 0.5f ), new Vec2( 0.5f, 2f ), 30f, 32f, 3 ), 1e-6f, "face 0 uv" );
			Require( skybox2.Validate().Count == 0, "reloaded validate warnings: " + string.Join( "; ", skybox2.Validate().Select( w => w.Code ) ) );
		} );

		Check( "worldspawn props without a slot warn by name and read back empty", () =>
		{
			var skybox = SkyboxAggregate();
			skybox.Worldspawn.Chapter = "CHAPTER 1: FIRST LIGHT";
			skybox.Worldspawn.Title = "First Light";
			skybox.Worldspawn.DetailProps = false; // the dropped case: the Map leg always reads the default ON
			skybox.Worldspawn.Fog = "light-fog";
			skybox.FogParams = new WorldspawnFog { Present = true, Enabled = true, Start = 128, End = 8192, Color = new Vec4( 1, 1, 1, 1 ), MaxDensity = 0.8 };
			var doc = MapDocument.Create( "gap-test" );
			var warnings = MapDocumentSkybox.ApplyToMapDocument( skybox, doc );
			Require( HasWarning( warnings, MapDocumentSkybox.WorldspawnNotInMapDocument ), "worldspawn gap warning absent: " + string.Join( ",", warnings.Select( w => w.Code ) ) );
			var (back, _) = MapDocumentSkybox.FromMapDocument( doc );
			Require( back.Worldspawn.Chapter == "", "chapter must read back empty (no slot)" );
			Require( back.Worldspawn.Title == "", "title must read back empty (no slot)" );
			Require( back.Worldspawn.DetailProps, "detail props must read back the Source default (on): the Map leg cannot hold 'off'" );
			Require( back.Worldspawn.Fog == "", "fog preset must read back empty (no slot)" );
			Require( !back.FogParams.Present, "fog params must read back absent (no slot)" );
			Require( back.Worldspawn.SkyboxName == "sky_day01_01", "sky name must still land" );
			Require( back.HasSkyCamera && back.SectionBrushes.Count == 1, "camera/section must still land" );

			var clean = SkyboxAggregate();
			var cleanWarnings = MapDocumentSkybox.ApplyToMapDocument( clean, MapDocument.Create( "gap-clean" ) );
			Require( !HasWarning( cleanWarnings, MapDocumentSkybox.WorldspawnNotInMapDocument ), "empty worldspawn props must not warn" );
		} );

		Check( "missing sky_camera with a 3D section warns skybox-missing-sky-camera", () =>
		{
			var doc = MapDocument.Create( "no-camera" );
			doc.AddVisGroup( new MapVisGroup { Id = "vg-skybox", Name = "3D Skybox", Colour = "#0080FF", Visible = true, CompileVisible = true } );
			var section = SectionBox( "2", 512f, 512f, 512f, 672f, 672f, 672f, "NATURE/CLIFF" );
			section.VisGroupIds.Add( "vg-skybox" );
			doc.AddBrush( section );
			var (skybox, _) = MapDocumentSkybox.FromMapDocument( doc );
			Require( !skybox.HasSkyCamera, "camera unexpectedly present" );
			Require( skybox.Has3DSection, "section unexpectedly absent" );
			Require( HasWarning( skybox.Validate(), SkyboxPreview.MissingSkyCamera ), "missing-sky-camera warning absent" );
			var bake = MapDocumentSkybox.ToBakeData( doc );
			Require( bake.AnchorSource == "section", $"camera-less bake anchor source {bake.AnchorSource}" );
		} );

		Check( "sky_camera validates against the registry", () =>
		{
			var path = FindRegistry();
			if ( path is null ) throw new Exception( "registry core-source1.json not found (this check skips only via its missing-func)" );
			var reg = EntityRegistry.Load( path );
			Require( reg.Contains( "sky_camera" ), "sky_camera missing from the core registry" );
			Require( SkyboxWorldspawn.IsSkyCameraInRegistry( reg ), "IsSkyCameraInRegistry disagrees with the registry" );
			var cls = reg.Get( "sky_camera" );
			Require( cls.Kind == EntityKind.Point, $"sky_camera kind is {cls.Kind}, expected Point" );
			Require( cls.FindKeyValue( "scale" )?.Type == KeyValueType.Float, "sky_camera 'scale' must be float" );
			Require( reg.ValidateEntity( "sky_camera", new Dictionary<string, string> { { "scale", "16" }, { "targetname", "sky_cam" } } ).Count == 0,
				"valid sky_camera rejected" );
			var bad = reg.ValidateEntity( "sky_camera", new Dictionary<string, string> { { "scale", "banana" } } );
			Require( bad.Count == 1 && bad[0].Code == EntityRegistry.CodeInvalidKeyValueType, "bad scale must be InvalidKeyValueType" );

			var doc = SkyboxMapDocument();
			Require( doc.Validate( reg ).Count == 0, "doc with sky_camera defects: " + string.Join( "; ", doc.Validate( reg ) ) );
			var badDoc = SkyboxMapDocument();
			badDoc.SetEntityProperty( "3", "scale", "banana" );
			Require( badDoc.Validate( reg ).Any( d => d.Contains( EntityRegistry.CodeInvalidKeyValueType ) ),
				"doc with bad camera scale must fail registry validation" );
		}, missing: () => FindRegistry() == null ? "registry kernel/Effigy/Map/registry/core-source1.json not found" : null );

		Check( "section round-trips through VMF text", () =>
		{
			var doc = SkyboxMapDocument();
			var (skybox, _) = MapDocumentSkybox.FromMapDocument( doc );
			var vmf = new VmfDocument();
			skybox.ApplyToVmfDocument( vmf );
			var (skybox2, _) = SkyboxWorldspawn.FromVmfDocument( VmfIo.Parse( VmfIo.Write( vmf ) ) );
			Require( skybox2.Worldspawn.SkyboxName == "sky_day01_01", "skyname lost through VMF" );
			Require( skybox2.HasSkyCamera && skybox2.Camera.TargetName == "sky_cam", "camera lost through VMF" );
			Require( skybox2.SectionBrushes.Count == 1, $"section brushes lost through VMF ({skybox2.SectionBrushes.Count})" );

			var doc2 = MapDocument.Create( "vmf-back" );
			var applyWarnings = MapDocumentSkybox.ApplyToMapDocument( skybox2, doc2 );
			Require( applyWarnings.Count == 0, "apply warnings: " + string.Join( "; ", applyWarnings.Select( w => w.Code ) ) );
			var (skybox3, _) = MapDocumentSkybox.FromMapDocument( doc2 );
			Require( skybox3.Worldspawn.SkyboxName == "sky_day01_01", "skyname lost on the way back" );
			Require( skybox3.HasSkyCamera, "camera lost on the way back" );
			Require( skybox3.SectionBrushes.Count == 1, "section lost on the way back" );
			var want = MapBrushData.Box( "2", "Mountain", new Vec3( 512f, 512f, 512f ), new Vec3( 672f, 672f, 672f ), "NATURE/CLIFF" );
			var got = doc2.FindBrush( "2" );
			Require( got != null, "section brush id lost on the way back" );
			for ( int i = 0; i < 6; i++ )
				RequirePlane( got.Faces[i], want.Faces[i].Normal, want.Faces[i].Distance, 1e-4f, $"face {i} plane" );
			Require( skybox3.Validate().Count == 0, "round-tripped validate warnings" );
		} );

		Check( "bake includes the section at world scale (real MapBakeExport)", () =>
		{
			var doc = SkyboxMapDocument();
			var (brushes, skipped, warnings) = MapDocumentSkybox.ToMapBakeBrushes( doc );
			Require( warnings.Count == 0, "bake warnings: " + string.Join( "; ", warnings.Select( w => w.Code ) ) );
			Require( skipped == 0, $"skipped {skipped}" );
			Require( brushes.Count == 1, $"bake brushes {brushes.Count}" );
			var baked = MapBakeExport.Bake( new MapBakeDocument( "skybox", brushes, Array.Empty<MapBakeDisplacement>() ) );
			Require( baked.FaceCount == 6, $"baked faces {baked.FaceCount}" );
			Require( baked.Materials.Contains( "NATURE/CLIFF" ), "section material missing from baked scene: " + string.Join( ",", baked.Materials ) );
			Require( baked.Issues.Count == 0, "bake issues: " + string.Join( "; ", baked.Issues ) );
		} );

		Check( "non-numeric brush ids keep gmap identity, vmf export mints", () =>
		{
			var doc = MapDocument.Create( "named-brush" );
			doc.AddVisGroup( new MapVisGroup { Id = "vg-skybox", Name = "3D Skybox", Colour = "#0080FF", Visible = true, CompileVisible = true } );
			var section = SectionBox( "brush-mountain", 512f, 512f, 512f, 672f, 672f, 672f, "NATURE/CLIFF" );
			section.VisGroupIds.Add( "vg-skybox" );
			doc.AddBrush( section );
			var again = MapDocumentIo.Read( MapDocumentIo.Write( doc ) );
			Require( again.FindBrush( "brush-mountain" ) != null, "non-numeric brush id lost through .gmap" );
			var (skybox, _) = MapDocumentSkybox.FromMapDocument( again );
			Require( skybox.SectionBrushes.Count == 1, "section lost" );
			Require( skybox.SectionBrushes[0].Id == 1, $"minted vmf id must be 1, got {skybox.SectionBrushes[0].Id}" );
		} );

		Check( "fixture parses: gmap with sky, camera, section", () =>
		{
			var gmap = FindFixture( "mapdocument-skybox.gmap" );
			if ( gmap is null ) throw new Exception( "fixture mapdocument-skybox.gmap not found (this check skips only via its missing-func)" );
			var doc = MapDocumentIo.Read( File.ReadAllText( gmap ) );
			Require( doc.Validate().Count == 0, "fixture doc defects: " + string.Join( "; ", doc.Validate() ) );
			var (skybox, warnings) = MapDocumentSkybox.FromMapDocument( doc );
			Require( warnings.Count == 0, "fixture read warnings: " + string.Join( "; ", warnings.Select( w => w.Code ) ) );
			Require( skybox.MapId == "a1b2c3d4", $"map id {skybox.MapId}" );
			Require( skybox.Worldspawn.SkyboxName == "sky_day01_01", "skyname" );
			Require( skybox.HasSkyCamera && skybox.Camera.TargetName == "sky_cam", "camera" );
			Require( Math.Abs( skybox.Camera.Scale - 16 ) < 1e-9, "camera scale" );
			Require( skybox.SectionBrushes.Count == 1, $"section brushes {skybox.SectionBrushes.Count}" );
			Require( skybox.SectionBrushes[0].Faces.Count == 6, "section faces" );
			Require( skybox.Validate().Count == 0, "fixture validate warnings" );
		}, missing: () => FindFixture( "mapdocument-skybox.gmap" ) == null ? "fixture mapdocument-skybox.gmap not found" : null );

		Check( "foreign uv axes default with one named warning per brush", () =>
		{
			var brush = BoxBrush( 2, 512, 512, 512, 672, 672, 672, "NATURE/CLIFF" );
			brush.Faces[0].UAxis = new VmfUvAxis( new VmfVec( 0, 1, 0 ), 0, 0.25 );
			var data = MapDocumentSkybox.FromVmfBrush( brush, out var warnings );
			Require( warnings.Count( w => w.Code == MapDocumentSkybox.SectionUvDefaulted ) == 1,
				$"expected exactly 1 uv-defaulted warning, got {warnings.Count}: " + string.Join( ",", warnings.Select( w => w.Code ) ) );
			Require( data.Faces.Count == 6, "faces lost" );
			Require( data.Faces[0].Material.Uv.Equals( FaceUv.Default ), "foreign-axis face must use the default UV" );
		} );

		Check( "displaced section face warns but keeps geometry", () =>
		{
			var brush = BoxBrush( 2, 512, 512, 512, 672, 672, 672, "NATURE/CLIFF" );
			brush.Faces[0].Displacement = new VmfDisplacement( 2 );
			var data = MapDocumentSkybox.FromVmfBrush( brush, out var warnings );
			Require( warnings.Count( w => w.Code == MapDocumentSkybox.SectionDisplacementDropped ) == 1,
				"displacement warning absent: " + string.Join( ",", warnings.Select( w => w.Code ) ) );
			Require( data.Faces.Count == 6, "displaced brush faces lost" );
		} );

		Check( "uv round-trips through vmf when geppetto-authored", () =>
		{
			var brush = SectionBox( "2", 512f, 512f, 512f, 672f, 672f, 672f, "NATURE/CLIFF" );
			var vmfBrush = MapDocumentSkybox.ToVmfBrush( brush, 2 );
			var back = MapDocumentSkybox.FromVmfBrush( vmfBrush, out var warnings );
			Require( !warnings.Any( w => w.Code == MapDocumentSkybox.SectionUvDefaulted ), "geppetto-authored uvs must not warn" );
			Require( back.Faces.Count == 6, "faces lost" );
			RequireUv( back.Faces[0].Material.Uv,
				new FaceUv( new Vec2( 0.25f, 0.5f ), new Vec2( 0.5f, 2f ), 30f, 32f, 3 ), 1e-6f, "face 0 uv" );
			RequireUv( back.Faces[1].Material.Uv, FaceUv.Default, 1e-6f, "face 1 uv" );
			for ( int i = 0; i < 6; i++ )
				RequirePlane( back.Faces[i], brush.Faces[i].Normal, brush.Faces[i].Distance, 1e-4f, $"face {i} plane" );
		} );

		Check( "stored vertices verify against the plane", () =>
		{
			var brush = MapBrushData.Box( "2", "Mountain", new Vec3( 512f, 512f, 512f ), new Vec3( 672f, 672f, 672f ), "NATURE/CLIFF" );
			var vmfBrush = MapDocumentSkybox.ToVmfBrush( brush, 2 );
			foreach ( var face in vmfBrush.Faces )
			{
				Require( face.StoredVertices.Count >= 3, $"face #{face.Id} has no stored vertices" );
				Require( face.VerifyStoredVertices().Count == 0, $"face #{face.Id} stored-vertex issues: " + string.Join( "; ", face.VerifyStoredVertices() ) );
			}
		} );

		Check( "apply is idempotent: second apply appends no history", () =>
		{
			var doc = MapDocument.Create( "idem" );
			var skybox = SkyboxAggregate();
			var first = MapDocumentSkybox.ApplyToMapDocument( skybox, doc );
			// The aggregate's section brush carries Hammer-default UV axes,
			// so the one honest warning is the UV defaulting (see the
			// foreign-axes check); the worldspawn fields are all defaults
			// here, so there is no gap warning.
			Require( first.Count == 1 && HasWarning( first, MapDocumentSkybox.SectionUvDefaulted ), "first apply warnings: " + string.Join( "; ", first.Select( w => w.Code ) ) );
			Require( !HasWarning( first, MapDocumentSkybox.WorldspawnNotInMapDocument ), "clean worldspawn must not warn" );
			var revision = doc.Revision;
			var history = doc.History.Count;
			var second = MapDocumentSkybox.ApplyToMapDocument( skybox, doc );
			Require( second.Count == first.Count && second.Select( w => w.Code ).SequenceEqual( first.Select( w => w.Code ) ), "second apply warnings differ: " + string.Join( "; ", second.Select( w => w.Code ) ) );
			Require( doc.Revision == revision, $"second apply bumped Revision {revision} -> {doc.Revision}" );
			Require( doc.History.Count == history, $"second apply appended history {history} -> {doc.History.Count}" );
			var (back, _) = MapDocumentSkybox.FromMapDocument( doc );
			Require( back.Worldspawn.SkyboxName == "sky_day01_01" && back.HasSkyCamera && back.SectionBrushes.Count == 1, "data drifted across applies" );
		} );
	}
}
