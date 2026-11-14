#nullable enable
using System;
using System.IO;
using System.Text.Json;
using Effigy;
using Effigy.Map;

/// <summary>
/// 3D/2D skybox preview scene data (see kernel/Effigy/Map/SkyboxPreview.cs,
/// board hammer-preview-skybox-scene).
///
/// The acceptance numbers live in the fixture
/// tests/fixtures/hammer/hammer-preview-skybox-scene/cases.json and the
/// fixture checks below recompute them from that file + the .vmf fixtures;
/// the rest build the same cases synthetically so a missing fixture can
/// never hide a geometry failure (a missing fixture SKIPs its check, it
/// never passes silently).
///
/// SKYBOX NOTE. The pass-1 ticket (hammer-skybox-2d-3d-worldspawn) and the
/// lighting ticket (hammer-lighting-environment-sun-fog) have not landed,
/// and MapDocument does not exist in the tree yet (see the mismatch note
/// in SkyboxPreview.cs), so these checks run against VmfDocument — the
/// in-tree document model — exactly as the implementation does. When
/// MapDocument lands, the MapDocument adapter checks belong with it;
/// nothing here assumes its signatures.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// SkyboxPreviewTests.Register(registry, settings); line.
/// </summary>
internal static class SkyboxPreviewTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test, Func<string?>? missing = null ) => registry.Add( "skyboxpreview", name, test, missing );

		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		void RequireVec( Vec3 got, Vec3 want, float eps, string message )
		{
			Require( got.AlmostEquals( want, eps ), $"{message}: got {got} want {want} (eps {eps})" );
		}

		void RequireFloat( float got, float want, float eps, string message )
		{
			Require( Math.Abs( got - want ) <= eps, $"{message}: got {got} want {want} (eps {eps})" );
		}

		/// <summary>Axis-aligned box brush with outward-wound plane points (VmfFace.Normal must point out).</summary>
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

		static VmfEntity PointEntity( int id, string className, params (string Key, string Value)[] kvs )
		{
			var e = new VmfEntity { Id = id, ClassName = className };
			foreach ( var (k, v) in kvs ) e.Set( k, v );
			return e;
		}

		static VmfDocument DocWithCamera( string origin, string? scale = "16" )
		{
			var doc = new VmfDocument();
			var camera = PointEntity( 1, "sky_camera", ("origin", origin), ("targetname", "sky_cam") );
			if ( scale != null ) camera.Set( "scale", scale );
			doc.Entities.Add( camera );
			return doc;
		}

		static string? FindFixture( string name )
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( Path.GetFullPath( start ) );
				for ( int depth = 0; depth < 10 && dir != null; depth++, dir = dir.Parent )
				{
					string candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-preview-skybox-scene", name );
					if ( File.Exists( candidate ) ) return candidate;
				}
			}
			return null;
		}

		Check( "visgroup section scales to 1/16 about sky_camera origin", () =>
		{
			var doc = DocWithCamera( "0 0 0" );
			doc.WorldspawnSet( "skyname", "sky_day01_01" );
			doc.VisGroups.Add( new VmfVisGroup { Id = 7, Name = "3D Skybox", Color = "0 128 255" } );
			doc.WorldBrushes.Add( BoxBrush( 1, 160, 160, 160, 320, 320, 320, "NATURE/CLIFF", visGroupId: 7 ) );
			doc.WorldBrushes.Add( BoxBrush( 2, 10000, 10000, 0, 10160, 10160, 160, "BRICK/BRICKWALL001A" ) );
			var scene = SkyboxPreview.BuildPreview( doc );
			Require( scene.Enabled, "preview should be enabled" );
			Require( scene.HasSkyCamera, "sky_camera should be found" );
			Require( scene.Has3DSection, "visgroup section should be found" );
			RequireFloat( scene.Scale, 16f, 1e-6f, "scale" );
			Require( scene.SkyName == "sky_day01_01" && scene.Has2DSky, $"sky name should be sky_day01_01, got '{scene.SkyName}'" );
			Require( scene.Faces.Count == 6, $"section box should give 6 preview faces, got {scene.Faces.Count}" );
			RequireVec( scene.SectionBoundsMin, new Vec3( 10, 10, 10 ), 1e-3f, "transformed bounds min" );
			RequireVec( scene.SectionBoundsMax, new Vec3( 20, 20, 20 ), 1e-3f, "transformed bounds max" );
			foreach ( var face in scene.Faces )
				foreach ( var v in face.Vertices )
					Require( v.x >= 10f - 1e-3f && v.x <= 20f + 1e-3f && v.y >= 10f - 1e-3f && v.y <= 20f + 1e-3f && v.z >= 10f - 1e-3f && v.z <= 20f + 1e-3f,
						$"preview vertex {v} is outside the 1/16 box 10..20" );
			Require( scene.Warnings.Count == 0, $"expected no warnings, got: {string.Join( "; ", scene.Warnings.ConvertAll( w => w.Code ) )}" );
		} );

		Check( "sky_camera scale key is honoured (1/8)", () =>
		{
			var doc = DocWithCamera( "0 0 0", "8" );
			doc.VisGroups.Add( new VmfVisGroup { Id = 7, Name = "3D Skybox", Color = "0 128 255" } );
			doc.WorldBrushes.Add( BoxBrush( 1, 160, 160, 160, 320, 320, 320, "NATURE/CLIFF", visGroupId: 7 ) );
			var scene = SkyboxPreview.BuildPreview( doc );
			RequireFloat( scene.Scale, 8f, 1e-6f, "scale" );
			RequireVec( scene.SectionBoundsMin, new Vec3( 20, 20, 20 ), 1e-3f, "1/8 bounds min" );
			RequireVec( scene.SectionBoundsMax, new Vec3( 40, 40, 40 ), 1e-3f, "1/8 bounds max" );
		} );

		Check( "missing sky_camera scale defaults to 16 without a warning", () =>
		{
			var doc = DocWithCamera( "0 0 0", scale: null );
			doc.VisGroups.Add( new VmfVisGroup { Id = 7, Name = "3D Skybox", Color = "0 128 255" } );
			doc.WorldBrushes.Add( BoxBrush( 1, 160, 160, 160, 320, 320, 320, "NATURE/CLIFF", visGroupId: 7 ) );
			var scene = SkyboxPreview.BuildPreview( doc );
			RequireFloat( scene.Scale, 16f, 1e-6f, "default scale" );
			Require( !scene.HasWarning( SkyboxPreview.InvalidSkyCameraScale ), "absent scale key must not warn" );
		} );

		Check( "missing sky_camera with a 3D section warns, not crashes", () =>
		{
			var doc = new VmfDocument();
			doc.WorldspawnSet( "skyname", "sky_day01_01" );
			doc.VisGroups.Add( new VmfVisGroup { Id = 7, Name = "3D Skybox", Color = "0 128 255" } );
			doc.WorldBrushes.Add( BoxBrush( 1, 160, 160, 160, 320, 320, 320, "NATURE/CLIFF", visGroupId: 7 ) );
			var scene = SkyboxPreview.BuildPreview( doc );
			Require( !scene.HasSkyCamera, "no sky_camera should be found" );
			Require( scene.Has3DSection, "the visgroup section should still be identified" );
			Require( scene.Faces.Count == 0, $"no faces can be placed without an origin/scale, got {scene.Faces.Count}" );
			Require( scene.HasWarning( SkyboxPreview.MissingSkyCamera ),
				$"expected named warning {SkyboxPreview.MissingSkyCamera}, got: {string.Join( ", ", scene.Warnings.ConvertAll( w => w.Code ) )}" );
		} );

		Check( "map with no skybox builds an empty scene without warnings", () =>
		{
			var scene = SkyboxPreview.BuildPreview( new VmfDocument() );
			Require( scene.Enabled, "preview should be enabled" );
			Require( !scene.HasSkyCamera && !scene.Has3DSection && !scene.Has2DSky, "empty map should have no sky data" );
			Require( scene.SkyName == "", $"sky name should be empty, got '{scene.SkyName}'" );
			Require( scene.Faces.Count == 0, "empty map should have no preview faces" );
			Require( scene.Sun == null, "empty map should have no sun" );
			Require( !scene.Fog.Enabled, "empty map should have no fog" );
			Require( scene.Warnings.Count == 0, "empty map should not warn" );
		} );

		Check( "spatial fallback includes near brushes and excludes far ones", () =>
		{
			var doc = DocWithCamera( "0 0 0" );
			doc.WorldBrushes.Add( BoxBrush( 1, 160, 160, 160, 320, 320, 320, "NATURE/CLIFF" ) );
			doc.WorldBrushes.Add( BoxBrush( 2, 10000, 10000, 0, 10160, 10160, 160, "BRICK/BRICKWALL001A" ) );
			var scene = SkyboxPreview.BuildPreview( doc );
			Require( scene.Has3DSection, "near brush should be found by the spatial rule" );
			Require( scene.Faces.Count == 6, $"only the near box should preview (6 faces), got {scene.Faces.Count}" );
			RequireVec( scene.SectionBoundsMin, new Vec3( 10, 10, 10 ), 1e-3f, "spatial bounds min" );
			RequireVec( scene.SectionBoundsMax, new Vec3( 20, 20, 20 ), 1e-3f, "spatial bounds max" );
		} );

		Check( "fog comes from env_fog_controller", () =>
		{
			var doc = new VmfDocument();
			doc.Entities.Add( PointEntity( 1, "env_fog_controller",
				("fogstart", "64"), ("fogend", "4096"), ("fogcolor", "128 128 255"), ("fogmaxdensity", "0.5") ) );
			var scene = SkyboxPreview.BuildPreview( doc );
			Require( scene.Fog.Enabled, "fog should be enabled" );
			Require( scene.Fog.Source == "env_fog_controller", $"fog source should be env_fog_controller, got '{scene.Fog.Source}'" );
			RequireFloat( scene.Fog.Start, 64f, 1e-6f, "fog start" );
			RequireFloat( scene.Fog.End, 4096f, 1e-6f, "fog end" );
			RequireFloat( scene.Fog.MaxDensity, 0.5f, 1e-6f, "fog max density" );
			RequireFloat( scene.Fog.Color.x, 128f / 255f, 1e-4f, "fog colour r" );
			RequireFloat( scene.Fog.Color.y, 128f / 255f, 1e-4f, "fog colour g" );
			RequireFloat( scene.Fog.Color.z, 1f, 1e-4f, "fog colour b" );
		} );

		Check( "fog falls back to worldspawn keys", () =>
		{
			var doc = new VmfDocument();
			doc.WorldspawnSet( "fogstart", "32" );
			doc.WorldspawnSet( "fogend", "2048" );
			doc.WorldspawnSet( "fogcolor", "255 0 0" );
			var scene = SkyboxPreview.BuildPreview( doc );
			Require( scene.Fog.Enabled, "worldspawn fog should be enabled" );
			Require( scene.Fog.Source == "worldspawn", $"fog source should be worldspawn, got '{scene.Fog.Source}'" );
			RequireFloat( scene.Fog.Start, 32f, 1e-6f, "worldspawn fog start" );
			RequireFloat( scene.Fog.End, 2048f, 1e-6f, "worldspawn fog end" );
			RequireFloat( scene.Fog.Color.x, 1f, 1e-4f, "worldspawn fog colour r" );
		} );

		Check( "sun direction and colour come from light_environment", () =>
		{
			var doc = new VmfDocument();
			doc.Entities.Add( PointEntity( 1, "light_environment",
				("angles", "0 90 0"), ("_light", "255 200 100 400"), ("_ambient", "40 40 60 200") ) );
			doc.Entities.Add( PointEntity( 2, "env_sun", ("angles", "0 90 0") ) );
			var scene = SkyboxPreview.BuildPreview( doc );
			Require( scene.Sun != null, "light_environment should resolve a sun" );
			RequireVec( scene.Sun!.Direction, new Vec3( 0, 1, 0 ), 1e-4f, "sun direction for angles 0 90 0" );
			RequireFloat( scene.Sun.Color.x, 1f, 1e-4f, "sun colour r" );
			RequireFloat( scene.Sun.Color.y, 200f / 255f, 1e-4f, "sun colour g" );
			RequireFloat( scene.Sun.Color.z, 100f / 255f, 1e-4f, "sun colour b" );
			RequireFloat( scene.Sun.Brightness, 400f, 1e-4f, "sun brightness" );
			RequireFloat( scene.Sun.Ambient.x, 40f / 255f, 1e-4f, "ambient r" );
			Require( scene.Sun.HasEnvSun, "env_sun should pair with the sun" );
			Require( !scene.HasWarning( SkyboxPreview.SunWithoutEnvSun ), "paired sun must not warn" );
		} );

		Check( "sun without env_sun warns", () =>
		{
			var doc = new VmfDocument();
			doc.Entities.Add( PointEntity( 1, "light_environment", ("angles", "0 0 0"), ("_light", "255 255 255 200") ) );
			var scene = SkyboxPreview.BuildPreview( doc );
			Require( scene.Sun != null, "sun should still resolve without env_sun" );
			Require( scene.HasWarning( SkyboxPreview.SunWithoutEnvSun ),
				$"expected named warning {SkyboxPreview.SunWithoutEnvSun}, got: {string.Join( ", ", scene.Warnings.ConvertAll( w => w.Code ) )}" );
		} );

		Check( "disabled preview returns an empty scene", () =>
		{
			var doc = DocWithCamera( "0 0 0" );
			doc.WorldspawnSet( "skyname", "sky_day01_01" );
			doc.VisGroups.Add( new VmfVisGroup { Id = 7, Name = "3D Skybox", Color = "0 128 255" } );
			doc.WorldBrushes.Add( BoxBrush( 1, 160, 160, 160, 320, 320, 320, "NATURE/CLIFF", visGroupId: 7 ) );
			var scene = SkyboxPreview.BuildPreview( doc, enabled: false );
			Require( !scene.Enabled, "preview should be disabled" );
			Require( scene.SkyName == "" && scene.Faces.Count == 0 && scene.Warnings.Count == 0 && scene.Sun == null,
				"disabled preview must carry no sky data, geometry, warnings or sun" );
		} );

		Check( "invalid sky_camera scale warns and falls back to 16", () =>
		{
			var doc = DocWithCamera( "0 0 0", "0" );
			doc.VisGroups.Add( new VmfVisGroup { Id = 7, Name = "3D Skybox", Color = "0 128 255" } );
			doc.WorldBrushes.Add( BoxBrush( 1, 160, 160, 160, 320, 320, 320, "NATURE/CLIFF", visGroupId: 7 ) );
			var scene = SkyboxPreview.BuildPreview( doc );
			RequireFloat( scene.Scale, 16f, 1e-6f, "fallback scale" );
			Require( scene.HasWarning( SkyboxPreview.InvalidSkyCameraScale ), "scale 0 must warn" );
			RequireVec( scene.SectionBoundsMax, new Vec3( 20, 20, 20 ), 1e-3f, "fallback bounds max" );
		} );

		Check( "preview faces keep material and source brush id", () =>
		{
			var doc = DocWithCamera( "0 0 0" );
			doc.VisGroups.Add( new VmfVisGroup { Id = 7, Name = "3D Skybox", Color = "0 128 255" } );
			doc.WorldBrushes.Add( BoxBrush( 9, 160, 160, 160, 320, 320, 320, "NATURE/CLIFF", visGroupId: 7 ) );
			var scene = SkyboxPreview.BuildPreview( doc );
			Require( scene.Faces.Count == 6, $"expected 6 faces, got {scene.Faces.Count}" );
			foreach ( var face in scene.Faces )
			{
				Require( face.Material == "NATURE/CLIFF", $"face material should be NATURE/CLIFF, got '{face.Material}'" );
				Require( face.SourceBrushId == 9, $"face source brush should be 9, got {face.SourceBrushId}" );
				Require( face.SourceFaceId >= 900, $"face source id should be a brush-9 face id, got {face.SourceFaceId}" );
				Require( face.Vertices.Length >= 3, "preview face needs at least 3 vertices" );
			}
		} );

		Check( "multiple sky_cameras warn and use the first", () =>
		{
			var doc = DocWithCamera( "0 0 0" );
			doc.Entities.Add( PointEntity( 2, "sky_camera", ("origin", "1000 0 0"), ("scale", "16") ) );
			doc.VisGroups.Add( new VmfVisGroup { Id = 7, Name = "3D Skybox", Color = "0 128 255" } );
			doc.WorldBrushes.Add( BoxBrush( 1, 160, 160, 160, 320, 320, 320, "NATURE/CLIFF", visGroupId: 7 ) );
			var scene = SkyboxPreview.BuildPreview( doc );
			Require( scene.HasWarning( SkyboxPreview.MultipleSkyCameras ), "second sky_camera must warn" );
			RequireVec( scene.SkyCameraOrigin, Vec3.Zero, 1e-6f, "first sky_camera origin wins" );
		} );

		Check( "fixture vmf parses and previews at 1/16", () =>
		{
			string path = FindFixture( "skybox-room.vmf" )!;
			string casesPath = FindFixture( "cases.json" )!;
			var doc = VmfIo.ParseFile( path );
			var scene = SkyboxPreview.BuildPreview( doc );
			using var cases = JsonDocument.Parse( File.ReadAllText( casesPath ) );
			var expected = cases.RootElement.GetProperty( "skybox-room" );
			Require( scene.SkyName == expected.GetProperty( "skyname" ).GetString(),
				$"fixture sky name should be {expected.GetProperty( "skyname" )}, got '{scene.SkyName}'" );
			var origin = expected.GetProperty( "sky_camera_origin" );
			RequireVec( scene.SkyCameraOrigin,
				new Vec3( (float)origin[0].GetDouble(), (float)origin[1].GetDouble(), (float)origin[2].GetDouble() ), 1e-3f, "fixture camera origin" );
			RequireFloat( scene.Scale, (float)expected.GetProperty( "scale" ).GetDouble(), 1e-6f, "fixture scale" );
			Require( scene.Faces.Count == expected.GetProperty( "preview_faces" ).GetInt32(),
				$"fixture should preview only the section box ({expected.GetProperty( "preview_faces" )} faces), got {scene.Faces.Count}" );
			var min = expected.GetProperty( "bounds_min" );
			var max = expected.GetProperty( "bounds_max" );
			RequireVec( scene.SectionBoundsMin,
				new Vec3( (float)min[0].GetDouble(), (float)min[1].GetDouble(), (float)min[2].GetDouble() ), 1e-3f, "fixture bounds min" );
			RequireVec( scene.SectionBoundsMax,
				new Vec3( (float)max[0].GetDouble(), (float)max[1].GetDouble(), (float)max[2].GetDouble() ), 1e-3f, "fixture bounds max" );
			Require( scene.Sun != null && scene.Sun.HasEnvSun, "fixture sun should resolve with env_sun" );
			Require( scene.Fog.Enabled && scene.Fog.Source == "env_fog_controller", "fixture fog should come from env_fog_controller" );
			Require( scene.Warnings.Count == 0, $"fixture should be warning-free, got: {string.Join( "; ", scene.Warnings.ConvertAll( w => w.Code ) )}" );
		}, missing: () => FindFixture( "skybox-room.vmf" ) == null || FindFixture( "cases.json" ) == null
			? "fixture tests/fixtures/hammer/hammer-preview-skybox-scene/skybox-room.vmf or cases.json not found" : null );

		Check( "fixture missing-sky-camera vmf warns", () =>
		{
			string path = FindFixture( "missing-sky-camera.vmf" )!;
			var scene = SkyboxPreview.BuildPreview( VmfIo.ParseFile( path ) );
			Require( !scene.HasSkyCamera, "fixture should have no sky_camera" );
			Require( scene.Has3DSection, "fixture section should still be identified" );
			Require( scene.Faces.Count == 0, "fixture with no camera should preview no faces" );
			Require( scene.HasWarning( SkyboxPreview.MissingSkyCamera ),
				$"expected named warning {SkyboxPreview.MissingSkyCamera}, got: {string.Join( ", ", scene.Warnings.ConvertAll( w => w.Code ) )}" );
		}, missing: () => FindFixture( "missing-sky-camera.vmf" ) == null
			? "fixture tests/fixtures/hammer/hammer-preview-skybox-scene/missing-sky-camera.vmf not found" : null );
	}
}
