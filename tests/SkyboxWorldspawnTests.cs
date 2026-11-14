#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Map;

/// <summary>
/// 2D/3D skybox + sky_camera + worldspawn map properties (see
/// kernel/Effigy/Map/SkyboxWorldspawn.cs, board
/// hammer-skybox-2d-3d-worldspawn).
///
/// The acceptance numbers live in the fixture
/// tests/fixtures/hammer/hammer-skybox-2d-3d-worldspawn/cases.json and the
/// fixture checks below recompute them from that file + the .vmf fixtures;
/// the rest build the same cases synthetically so a missing fixture can
/// never hide a failure (a missing fixture SKIPs its check, it never
/// passes silently).
///
/// These checks run against VmfDocument — the in-tree document model —
/// exactly as the implementation does: MapDocument does not exist in the
/// tree yet, and the MapDocument adapter checks belong with it. Nothing
/// here assumes MapDocument signatures. The bake check runs the real
/// MapBakeExport.Bake over the section brushes this file produces, so
/// "present in the baked scene" is a measured face count, not a claim.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// SkyboxWorldspawnTests.Register(registry, settings); line.
/// </summary>
internal static class SkyboxWorldspawnTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test, Func<string?>? missing = null ) => registry.Add( "skyboxworldspawn", name, test, missing );

		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		void RequireVec( VmfVec got, VmfVec want, double eps, string message )
		{
			Require( got.ApproxEquals( want, eps ), $"{message}: got {got.ToVmfString()} want {want.ToVmfString()} (eps {eps})" );
		}

		void RequireVec4( Vec4 got, Vec4 want, float eps, string message )
		{
			Require( Math.Abs( got.x - want.x ) <= eps && Math.Abs( got.y - want.y ) <= eps && Math.Abs( got.z - want.z ) <= eps && Math.Abs( got.w - want.w ) <= eps,
				$"{message}: got ({got.x}, {got.y}, {got.z}, {got.w}) want ({want.x}, {want.y}, {want.z}, {want.w}) (eps {eps})" );
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

		/// <summary>Axis-aligned box brush with outward-wound plane points (VmfFace.Normal must point out). Same winding as SkyboxPreviewTests.</summary>
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

		/// <summary>A doc with a main room brush, a sky_camera, and one section brush in the "3D Skybox" visgroup.</summary>
		static VmfDocument SkyboxDoc()
		{
			var doc = new VmfDocument();
			doc.VisGroups.Add( new VmfVisGroup { Id = 7, Name = "3D Skybox", Color = "0 128 255" } );
			doc.WorldBrushes.Add( BoxBrush( 1, -512, -512, 0, 512, 512, 256, "BRICK/BRICKWALL001A" ) );
			doc.WorldBrushes.Add( BoxBrush( 2, 512, 512, 512, 672, 672, 672, "NATURE/CLIFF", visGroupId: 7 ) );
			doc.Entities.Add( PointEntity( 3, "sky_camera", ("targetname", "sky_cam"), ("origin", "512 512 512"), ("scale", "16") ) );
			return doc;
		}

		Check( "fixture parses: worldspawn keys, sky_camera, section in visgroup", () =>
		{
			var vmf = FindFixture( "skybox-worldspawn.vmf" );
			if ( vmf is null ) throw new Exception( "fixture skybox-worldspawn.vmf not found (this check skips only via its missing-func)" );
			var casesPath = FindFixture( "cases.json" );
			if ( casesPath is null ) throw new Exception( "fixture cases.json not found" );
			var expected = JsonDocument.Parse( File.ReadAllText( casesPath ) ).RootElement;
			var (skybox, warnings) = SkyboxWorldspawn.FromVmfDocument( VmfIo.ParseFile( vmf ) );
			Require( warnings.Count == 0, "parse warnings: " + string.Join( "; ", warnings.Select( w => w.Code + " " + w.Message ) ) );
			Require( skybox.Worldspawn.SkyboxName == expected.GetProperty( "skyName" ).GetString(), "skyname" );
			Require( skybox.Worldspawn.Chapter == expected.GetProperty( "chapter" ).GetString(), "chapter" );
			Require( skybox.Worldspawn.Title == expected.GetProperty( "title" ).GetString(), "title" );
			Require( skybox.Worldspawn.DetailProps, "detail props flag" );
			Require( skybox.DetailVbsp == expected.GetProperty( "detailVbsp" ).GetString(), "detailvbsp" );
			Require( skybox.DetailMaterial == expected.GetProperty( "detailMaterial" ).GetString(), "detailmaterial" );
			Require( skybox.FogParams.Present && skybox.FogParams.Enabled, "fog present+enabled" );
			Require( Math.Abs( skybox.FogParams.Start - expected.GetProperty( "fogStart" ).GetDouble() ) < 1e-9, "fogstart" );
			Require( Math.Abs( skybox.FogParams.End - expected.GetProperty( "fogEnd" ).GetDouble() ) < 1e-9, "fogend" );
			Require( Math.Abs( skybox.FogParams.MaxDensity - expected.GetProperty( "fogMaxDensity" ).GetDouble() ) < 1e-9, "fogmaxdensity" );
			Require( skybox.HasSkyCamera, "sky_camera missing" );
			RequireVec( skybox.Camera.Origin, new VmfVec( 512, 512, 512 ), 1e-9, "camera origin" );
			Require( Math.Abs( skybox.Camera.Scale - 16 ) < 1e-9, "camera scale" );
			Require( skybox.SectionBrushes.Count == expected.GetProperty( "sectionBrushes" ).GetInt32(), $"section brushes {skybox.SectionBrushes.Count}" );
			Require( skybox.SectionBrushes[0].Faces.Count == expected.GetProperty( "sectionFaces" ).GetInt32(), "section faces" );
			Require( skybox.Validate().Count == 0, "validate warnings: " + string.Join( "; ", skybox.Validate().Select( w => w.Code ) ) );
		}, missing: () => FindFixture( "skybox-worldspawn.vmf" ) == null || FindFixture( "cases.json" ) == null ? "fixture skybox-worldspawn.vmf/cases.json not found" : null );

		Check( "missing sky_camera fixture warns skybox-missing-sky-camera", () =>
		{
			var vmf = FindFixture( "missing-sky-camera.vmf" )!;
			var (skybox, _) = SkyboxWorldspawn.FromVmfDocument( VmfIo.ParseFile( vmf ) );
			Require( !skybox.HasSkyCamera, "camera unexpectedly present" );
			Require( skybox.Has3DSection, "section unexpectedly absent" );
			var warnings = skybox.Validate();
			Require( HasWarning( warnings, SkyboxPreview.MissingSkyCamera ), "missing-sky-camera warning absent: " + string.Join( ",", warnings.Select( w => w.Code ) ) );
		}, missing: () => FindFixture( "missing-sky-camera.vmf" ) == null ? "fixture missing-sky-camera.vmf not found" : null );

		Check( "vmf text round-trip preserves skybox data and section planes", () =>
		{
			var doc = SkyboxDoc();
			var skybox = new SkyboxWorldspawn { MapId = "roundtrip" };
			skybox.Worldspawn.SkyboxName = "sky_day01_01";
			skybox.Worldspawn.Chapter = "CHAPTER 1: FIRST LIGHT";
			skybox.Worldspawn.Title = "First Light";
			skybox.Worldspawn.DetailProps = true;
			skybox.DetailVbsp = "detail.vbsp";
			skybox.DetailMaterial = "detail/detailsprites";
			skybox.FogParams = new WorldspawnFog { Present = true, Enabled = true, Start = 128, End = 8192, Color = new Vec4( 180f / 255f, 200f / 255f, 1f, 1f ), MaxDensity = 0.8 };
			skybox.Cameras.Add( new SkyCamera { Id = 3, TargetName = "sky_cam", Origin = new VmfVec( 512, 512, 512 ), Scale = 16 } );
			skybox.SectionOrigin = new VmfVec( 512, 512, 512 );
			skybox.SectionScale = 16;
			skybox.SectionBrushes.Add( BoxBrush( 2, 512, 512, 512, 672, 672, 672, "NATURE/CLIFF" ) );
			skybox.ApplyToVmfDocument( doc );

			var again = SkyboxWorldspawn.FromVmfDocument( VmfIo.Parse( VmfIo.Write( doc ) ) ).Skybox;
			Require( again.Worldspawn.SkyboxName == "sky_day01_01", "skyname lost" );
			Require( again.Worldspawn.Chapter == "CHAPTER 1: FIRST LIGHT", "chapter lost" );
			Require( again.Worldspawn.Title == "First Light", "title lost" );
			Require( again.Worldspawn.DetailProps && again.DetailVbsp == "detail.vbsp", "detailvbsp lost" );
			Require( again.DetailMaterial == "detail/detailsprites", "detailmaterial lost" );
			Require( again.FogParams.Present && again.FogParams.Enabled, "fog lost" );
			Require( Math.Abs( again.FogParams.Start - 128 ) < 1e-9 && Math.Abs( again.FogParams.End - 8192 ) < 1e-9, "fog range lost" );
			Require( Math.Abs( again.FogParams.MaxDensity - 0.8 ) < 1e-9, "fog density lost" );
			Require( again.HasSkyCamera && again.Camera.TargetName == "sky_cam", "camera lost" );
			RequireVec( again.Camera.Origin, new VmfVec( 512, 512, 512 ), 1e-9, "camera origin lost" );
			Require( Math.Abs( again.Camera.Scale - 16 ) < 1e-9, "camera scale lost" );
			Require( again.SectionBrushes.Count == 1, $"section brushes {again.SectionBrushes.Count}" );
			var want = BoxBrush( 2, 512, 512, 512, 672, 672, 672, "NATURE/CLIFF" );
			var got = again.SectionBrushes[0];
			Require( got.Faces.Count == want.Faces.Count, "section face count changed" );
			for ( int i = 0; i < want.Faces.Count; i++ )
				Require( got.Faces[i].PlanePointsEqual( want.Faces[i] ), $"section face {i} plane points changed" );
			Require( again.Validate().Count == 0, "round-tripped data failed validation" );
		} );

		Check( "apply-to is idempotent across a second write/parse", () =>
		{
			var doc = SkyboxDoc();
			// Validate warns skybox-empty-sky-name whenever a section exists
			// with no 2D sky, so the doc under test names one; without it
			// this check could never pass (it asserted a clean Validate).
			doc.WorldspawnSet( "skyname", "sky_day01_01" );
			var first = SkyboxWorldspawn.FromVmfDocument( doc ).Skybox;
			first.Worldspawn.Title = "Second Pass";
			first.ApplyToVmfDocument( doc );
			var second = SkyboxWorldspawn.FromVmfDocument( VmfIo.Parse( VmfIo.Write( doc ) ) ).Skybox;
			second.ApplyToVmfDocument( doc );
			var third = SkyboxWorldspawn.FromVmfDocument( VmfIo.Parse( VmfIo.Write( doc ) ) ).Skybox;
			Require( third.Worldspawn.Title == "Second Pass", "title drifted" );
			Require( third.SectionBrushes.Count == 1 && third.SectionBrushes[0].Id == 2, $"section brush id drifted (got {( third.SectionBrushes.Count > 0 ? third.SectionBrushes[0].Id.ToString() : "none" )})" );
			Require( third.Cameras.Count == 1 && third.Camera.Id == 3, "camera id drifted" );
			Require( third.Validate().Count == 0, "idempotent data failed validation" );
		} );

		Check( "multiple sky_cameras warn skybox-multiple-sky-cameras", () =>
		{
			var doc = SkyboxDoc();
			doc.Entities.Add( PointEntity( 9, "sky_camera", ("origin", "0 0 0"), ("scale", "16") ) );
			var (skybox, _) = SkyboxWorldspawn.FromVmfDocument( doc );
			Require( skybox.Cameras.Count == 2, $"cameras {skybox.Cameras.Count}" );
			Require( HasWarning( skybox.Validate(), SkyboxPreview.MultipleSkyCameras ), "multiple-cameras warning absent" );
		} );

		Check( "invalid camera scale and origin fall back with warnings", () =>
		{
			var doc = new VmfDocument();
			doc.Entities.Add( PointEntity( 3, "sky_camera", ("origin", "banana"), ("scale", "0") ) );
			var (skybox, parseWarnings) = SkyboxWorldspawn.FromVmfDocument( doc );
			Require( HasWarning( parseWarnings, SkyboxPreview.InvalidSkyCameraOrigin ), "bad-origin warning absent" );
			Require( HasWarning( parseWarnings, SkyboxPreview.InvalidSkyCameraScale ), "bad-scale warning absent" );
			RequireVec( skybox.Camera.Origin, VmfVec.Zero, 1e-9, "bad origin fallback" );
			Require( Math.Abs( skybox.Camera.Scale - 16 ) < 1e-9, "bad scale fallback" );

			var doc2 = new VmfDocument();
			doc2.Entities.Add( PointEntity( 3, "sky_camera", ("origin", "1 2 3") ) );
			var (skybox2, parseWarnings2) = SkyboxWorldspawn.FromVmfDocument( doc2 );
			Require( !HasWarning( parseWarnings2, SkyboxPreview.InvalidSkyCameraScale ), "missing scale key must not warn" );
			Require( Math.Abs( skybox2.Camera.Scale - 16 ) < 1e-9, "missing scale must default to 16" );
		} );

		Check( "worldspawn fog round-trips including fogenable 0", () =>
		{
			var doc = new VmfDocument();
			var skybox = new SkyboxWorldspawn();
			skybox.FogParams = new WorldspawnFog { Present = true, Enabled = false, Start = 64, End = 4096, Color = new Vec4( 0.5f, 0.25f, 1f, 1f ), MaxDensity = 0.5 };
			skybox.ApplyToVmfDocument( doc );
			Require( doc.WorldspawnGet( "fogenable" ) == "0", "fogenable 0 not written" );
			var again = SkyboxWorldspawn.FromVmfDocument( VmfIo.Parse( VmfIo.Write( doc ) ) ).Skybox;
			Require( again.FogParams.Present && !again.FogParams.Enabled, "fog disable lost" );
			Require( Math.Abs( again.FogParams.Start - 64 ) < 1e-9 && Math.Abs( again.FogParams.End - 4096 ) < 1e-9, "fog range lost" );
			Require( Math.Abs( again.FogParams.MaxDensity - 0.5 ) < 1e-9, "fog density lost" );
			RequireVec4( again.FogParams.Color, new Vec4( 0.5f, 0.25f, 1f, 1f ), 1f / 255f + 1e-6f, "fog colour lost" );
		} );

		Check( "invalid fog values warn and keep defaults", () =>
		{
			var doc = new VmfDocument();
			doc.WorldspawnSet( "fogstart", "abc" );
			doc.WorldspawnSet( "fogcolor", "1 2" );
			doc.WorldspawnSet( "fogmaxdensity", "xyz" );
			var (skybox, warnings) = SkyboxWorldspawn.FromVmfDocument( doc );
			Require( warnings.Count(w => w.Code == SkyboxPreview.InvalidFog) == 3, $"expected 3 invalid-fog warnings, got {warnings.Count}" );
			Require( skybox.FogParams.Start == 0 && skybox.FogParams.End == 0, "bad fog range not defaulted" );
			Require( skybox.FogParams.MaxDensity == 1.0, "bad fog density not defaulted" );
			RequireVec4( skybox.FogParams.Color, new Vec4( 1, 1, 1, 1 ), 1e-6f, "bad fog colour not defaulted to white" );
		} );

		Check( "fog end at or before start warns", () =>
		{
			var skybox = new SkyboxWorldspawn();
			skybox.FogParams = new WorldspawnFog { Present = true, Enabled = true, Start = 8192, End = 128 };
			Require( HasWarning( skybox.Validate(), SkyboxPreview.InvalidFog ), "inverted-fog warning absent" );
			var ok = new SkyboxWorldspawn();
			ok.FogParams = new WorldspawnFog { Present = true, Enabled = true, Start = 128, End = 8192 };
			Require( !HasWarning( ok.Validate(), SkyboxPreview.InvalidFog ), "valid fog must not warn" );
		} );

		Check( "section/camera origin and scale mismatch warns", () =>
		{
			var skybox = new SkyboxWorldspawn();
			skybox.Cameras.Add( new SkyCamera { Id = 3, Origin = new VmfVec( 512, 512, 512 ), Scale = 16 } );
			skybox.SectionOrigin = new VmfVec( 0, 0, 0 );
			skybox.SectionScale = 16;
			skybox.SectionBrushes.Add( BoxBrush( 2, 512, 512, 512, 672, 672, 672, "NATURE/CLIFF" ) );
			Require( HasWarning( skybox.Validate(), SkyboxWorldspawn.SectionCameraMismatch ), "origin mismatch warning absent" );
			skybox.SectionOrigin = new VmfVec( 512, 512, 512 );
			skybox.SectionScale = 8;
			Require( HasWarning( skybox.Validate(), SkyboxWorldspawn.SectionCameraMismatch ), "scale mismatch warning absent" );
			skybox.SectionScale = 16;
			Require( !HasWarning( skybox.Validate(), SkyboxWorldspawn.SectionCameraMismatch ), "matching anchor must not warn" );
		} );

		Check( "empty sky name warns only with a 3D section", () =>
		{
			var withSection = new SkyboxWorldspawn();
			withSection.Worldspawn.SkyboxName = "";
			withSection.Cameras.Add( new SkyCamera { Id = 3, Origin = new VmfVec( 0, 0, 0 ), Scale = 16 } );
			withSection.SectionOrigin = new VmfVec( 0, 0, 0 );
			withSection.SectionBrushes.Add( BoxBrush( 2, 0, 0, 0, 160, 160, 160, "NATURE/CLIFF" ) );
			Require( HasWarning( withSection.Validate(), SkyboxWorldspawn.EmptySkyName ), "empty-sky-name warning absent" );
			var withoutSection = new SkyboxWorldspawn();
			withoutSection.Worldspawn.SkyboxName = "";
			Require( !HasWarning( withoutSection.Validate(), SkyboxWorldspawn.EmptySkyName ), "2D-only map with no sky name must not warn" );
		} );

		Check( "bake scales the section x16 about the camera (real MapBakeExport)", () =>
		{
			var doc = SkyboxDoc();
			var (skybox, _) = SkyboxWorldspawn.FromVmfDocument( doc );
			var bake = skybox.ToBakeData();
			Require( bake.AnchorSource == "sky_camera", $"anchor source {bake.AnchorSource}" );
			Require( bake.HasBounds, "bake has no bounds" );
			RequireVec( bake.BoundsMin, new VmfVec( 512, 512, 512 ), 1e-6, "bake bounds min" );
			RequireVec( bake.BoundsMax, new VmfVec( 3072, 3072, 3072 ), 1e-6, "bake bounds max" );
			Require( bake.SectionFaceCount == 6, $"bake faces {bake.SectionFaceCount}" );
			var bakeBrushes = bake.ToMapBakeBrushes( out int skipped );
			Require( skipped == 0, $"skipped {skipped}" );
			Require( bakeBrushes.Count == 1, $"bake brushes {bakeBrushes.Count}" );
			var baked = MapBakeExport.Bake( new MapBakeDocument( "skybox", bakeBrushes, Array.Empty<MapBakeDisplacement>() ) );
			Require( baked.FaceCount == 6, $"baked faces {baked.FaceCount}" );
			Require( baked.Materials.Contains( "NATURE/CLIFF" ), "section material missing from baked scene: " + string.Join( ",", baked.Materials ) );
			Require( baked.Issues.Count == 0, "bake issues: " + string.Join( "; ", baked.Issues ) );
		} );

		Check( "bake without a camera uses the section anchor", () =>
		{
			var skybox = new SkyboxWorldspawn();
			skybox.Worldspawn.SkyboxName = "sky_day01_01";
			skybox.SectionOrigin = new VmfVec( 0, 0, 0 );
			skybox.SectionScale = 8;
			skybox.SectionBrushes.Add( BoxBrush( 2, 0, 0, 0, 16, 16, 16, "NATURE/CLIFF" ) );
			var bake = skybox.ToBakeData();
			Require( !bake.HasSkyCamera && bake.AnchorSource == "section", $"anchor source {bake.AnchorSource}" );
			RequireVec( bake.BoundsMin, new VmfVec( 0, 0, 0 ), 1e-6, "camera-less bake bounds min" );
			RequireVec( bake.BoundsMax, new VmfVec( 128, 128, 128 ), 1e-6, "camera-less bake bounds max" );
			Require( HasWarning( skybox.Validate(), SkyboxPreview.MissingSkyCamera ), "camera-less section must still warn" );
		} );

		Check( "from-starter-map carries the outdoor section, minimal room has none", () =>
		{
			var outdoor = SkyboxWorldspawn.FromStarterMap( StarterTemplates.Build( "outdoor-displacement" ) );
			Require( outdoor.Has3DSection, "outdoor template section missing" );
			Require( outdoor.SectionBrushes.Count == 3, $"outdoor section brushes {outdoor.SectionBrushes.Count}" );
			Require( outdoor.SectionBrushes.All( b => b.Faces.Count == 6 ), "outdoor section brush face counts" );
			Require( Math.Abs( outdoor.SectionScale - 16 ) < 1e-9, "outdoor section scale" );
			Require( outdoor.HasSkyCamera, "outdoor sky_camera missing" );
			RequireVec( outdoor.Camera.Origin, new VmfVec( 0, 0, 0 ), 1e-6, "outdoor camera origin" );
			Require( Math.Abs( outdoor.Camera.Scale - 16 ) < 1e-9, "outdoor camera scale must default to 16 (no scale key in the template)" );
			RequireVec( outdoor.SectionOrigin, new VmfVec( 0, 0, 0 ), 1e-6, "outdoor section origin" );
			Require( outdoor.Validate().Count == 0, "outdoor aggregate failed validation: " + string.Join( "; ", outdoor.Validate().Select( w => w.Code ) ) );

			var room = SkyboxWorldspawn.FromStarterMap( StarterTemplates.Build( "minimal-room" ) );
			Require( !room.Has3DSection && !room.HasSkyCamera, "minimal room must be 2D-only" );
			Require( room.Worldspawn.SkyboxName == "sky_day01_01", "minimal room skyname" );
		} );

		Check( "detail props off removes the detail keys", () =>
		{
			var doc = new VmfDocument();
			doc.WorldspawnSet( "detailvbsp", "detail.vbsp" );
			doc.WorldspawnSet( "detailmaterial", "detail/detailsprites" );
			var skybox = new SkyboxWorldspawn();
			skybox.Worldspawn.DetailProps = false;
			skybox.ApplyToVmfDocument( doc );
			Require( doc.WorldspawnGet( "detailvbsp" ) is null, "detailvbsp not removed" );
			Require( doc.WorldspawnGet( "detailmaterial" ) is null, "detailmaterial not removed" );
			skybox.Worldspawn.DetailProps = true;
			skybox.ApplyToVmfDocument( doc );
			Require( doc.WorldspawnGet( "detailvbsp" ) == "detail.vbsp", "detailvbsp not written" );
			Require( doc.WorldspawnGet( "detailmaterial" ) == "detail/detailsprites", "detailmaterial not written" );
		} );

		Check( "registry contains sky_camera with a float scale", () =>
		{
			var path = FindRegistry();
			if ( path is null ) throw new Exception( "registry core-source1.json not found (this check skips only via its missing-func)" );
			var registry = EntityRegistry.Load( path );
			Require( registry.Contains( "sky_camera" ), "sky_camera missing from the registry" );
			Require( SkyboxWorldspawn.IsSkyCameraInRegistry( registry ), "IsSkyCameraInRegistry disagrees with the registry" );
			var cls = registry.Get( "sky_camera" );
			Require( cls.Kind == EntityKind.Point, $"sky_camera kind is {cls.Kind}, expected Point" );
			Require( cls.FindKeyValue( "scale" )?.Type == KeyValueType.Float, "sky_camera 'scale' must be float" );
			Require( registry.ValidateEntity( "sky_camera", new Dictionary<string, string> { { "scale", "16" }, { "targetname", "sky_cam" } } ).Count == 0,
				"valid sky_camera rejected" );
			var bad = registry.ValidateEntity( "sky_camera", new Dictionary<string, string> { { "scale", "banana" } } );
			Require( bad.Count == 1 && bad[0].Code == EntityRegistry.CodeInvalidKeyValueType, "bad scale must be InvalidKeyValueType" );
		}, missing: () => FindRegistry() == null ? "registry kernel/Effigy/Map/registry/core-source1.json not found" : null );

		Check( "section brushes without a visgroup are found by the spatial fallback", () =>
		{
			var doc = new VmfDocument();
			doc.Entities.Add( PointEntity( 3, "sky_camera", ("origin", "0 0 0"), ("scale", "16") ) );
			doc.WorldBrushes.Add( BoxBrush( 10, 0, 0, 0, 160, 160, 160, "NATURE/CLIFF" ) );
			doc.WorldBrushes.Add( BoxBrush( 11, 10000, 10000, 0, 10160, 10160, 160, "BRICK/BRICKWALL001A" ) );
			var (skybox, _) = SkyboxWorldspawn.FromVmfDocument( doc );
			Require( skybox.SectionBrushes.Count == 1, $"spatial fallback found {skybox.SectionBrushes.Count} brushes" );
			Require( skybox.SectionBrushes[0].Id == 10, "spatial fallback found the wrong brush" );
		} );
	}
}
