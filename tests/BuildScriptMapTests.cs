// Hammer Desktop AI: BuildScript verbs for native map tools.
// Board: hammer-ai-buildscript-verbs-map. Group name 'buildscriptmap'.
//
// Drives BuildScript.Run with real map scripts (a fresh PartStudio; the map
// state lives in BuildScript's per-name sessions, outside the studio, so the
// studio here is only the Run entry point). Each check uses a fresh map name
// (guid suffix) because sessions are process-wide and a second run of the
// suite must not see the first run's brushes.
//
// Fixtures live in tests/fixtures/hammer/hammer-ai-buildscript-verbs-map/:
//   sealed-room.effigy-script  — the acceptance script (also inlined below as
//     SealedRoomScript so the test does not depend on the repo layout);
//   README.md                  — grammar, design decisions, known gaps.
// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned
// and the final integrator adds the one BuildScriptMapTests.Register line.
#nullable enable
using System;
using System.IO;
using System.Linq;
using Effigy;

internal static class BuildScriptMapTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		Directory.CreateDirectory( settings.WorkDirectory );

		void Check( string name, Action test ) => registry.Add( "buildscriptmap", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		string FreshMap( string tag ) => $"{tag}-{Guid.NewGuid().ToString( "N" )[..8]}";
		BuildScript.Result RunScript( string script ) =>
			BuildScript.Run( new PartStudio(), script, settings.WorkDirectory );
		void RequireOk( BuildScript.Result run, string what )
		{
			Require( run.Ok, $"{what} failed line {run.ErrorLine}: {run.Error}\nlog:\n{string.Join( "\n", run.Log )}" );
		}
		void RequireFail( BuildScript.Result run, string fragment, string what )
		{
			Require( !run.Ok, $"{what} unexpectedly succeeded. log:\n{string.Join( "\n", run.Log )}" );
			Require( ( run.Error ?? "" ).Contains( fragment, StringComparison.Ordinal ),
				$"{what} failed, but the error did not name '{fragment}': {run.Error}" );
		}
		string SealedRoomScript( string map ) => $$"""
			map_brush_create floor shape=box min=-256,-256,0 max=256,256,16 material=dev/dev_measuregeneric01 map={{map}}
			map_brush_create wall-n shape=box min=-256,-256,0 max=256,-240,192 material=brick/brickwall001 map={{map}}
			map_brush_create wall-s shape=box min=-256,240,0 max=256,256,192 material=brick/brickwall001 map={{map}}
			map_brush_create wall-w shape=box min=-256,-256,0 max=-240,256,192 material=brick/brickwall001 map={{map}}
			map_brush_create wall-e shape=box min=240,-256,0 max=256,256,192 material=brick/brickwall001 map={{map}}
			map_brush_create ceil shape=box min=-256,-256,176 max=256,256,192 material=dev/dev_measuregeneric01 map={{map}}
			map_entity_place class=info_player_start origin=0,0,32 map={{map}}
			map_entity_place class=light origin=0,0,160 map={{map}}
			map_brush_create door-panel shape=box min=-32,-128,16 max=32,-112,144 material=dev/dev_measuregeneric01 map={{map}}
			map_entity_place class=func_door brush=door-panel targetname=door1 map={{map}}
			map_entity_place class=logic_auto origin=0,0,32 targetname=auto1 map={{map}}
			map_entity_io_add source=auto1 event=OnMapSpawn target=door1 input=Open delay=1 map={{map}}
			map_validate map={{map}}
			map_compile preset=quickplay map={{map}}
			""";

		string[] SpecVerbs = {
			"map_brush_create", "map_brush_edit", "map_brush_clip", "map_brush_vertex",
			"map_displacement_create", "map_displacement_sculpt", "map_face_material", "map_face_uv",
			"map_entity_place", "map_entity_edit", "map_entity_io_add", "map_group", "map_visgroup",
			"map_import", "map_export", "map_compile", "map_validate", "map_lightmap_bake", "map_navmesh", "map_skybox",
		};

		// 1. The verb inventory is exactly the SEAM list, in order.
		Check( "map verbs equal the SEAM list in order", () =>
		{
			Require( BuildScript.MapVerbNames.SequenceEqual( SpecVerbs ),
				$"MapVerbNames drifted from the SEAM list. Got: {string.Join( ", ", BuildScript.MapVerbNames )}." );
		} );

		// 2. Acceptance: sealed room (brushes + player start + light + door + I/O),
		//    then validation + Quick Play succeed headlessly.
		Check( "sealed room script validates and quickplays", () =>
		{
			var map = FreshMap( "room" );
			var run = RunScript( SealedRoomScript( map ) );
			RequireOk( run, "sealed-room script" );
			Require( run.Log.Any( l => l.Contains( "quickplay sealed", StringComparison.Ordinal ) ),
				"quickplay did not report sealed. log:\n" + string.Join( "\n", run.Log ) );
			Require( run.Log.Any( l => l.Contains( "map_validate:", StringComparison.Ordinal ) ),
				"validate did not log its summary. log:\n" + string.Join( "\n", run.Log ) );
		} );

		// 3. All non-box shapes build.
		Check( "brush create wedge cylinder sphere arch planes", () =>
		{
			var map = FreshMap( "shapes" );
			var run = RunScript( $$"""
				map_brush_create wedge1 shape=wedge center=0,0,64 size=128,128,128 map={{map}}
				map_brush_create cyl1 shape=cylinder position=256,0,64 radius=64 height=128 segments=8 map={{map}}
				map_brush_create sph1 shape=sphere position=-256,0,64 radius=64 divisions=4 segments=8 map={{map}}
				map_brush_create arch1 shape=arch position=0,256,64 radius=64 height=128 sweep=90 segments=6 map={{map}}
				map_brush_create planes1 shape=planes faces="0,0,-1,0;0,0,1,128;-1,0,0,64;1,0,0,64;0,-1,0,64;0,1,0,64" map={{map}}
				""" );
			RequireOk( run, "shape script" );
			Require( run.Log.Count( l => l.StartsWith( "map_brush_create:", StringComparison.Ordinal ) ) == 5,
				$"expected 5 brush creates, got {run.Log.Count}. log:\n{string.Join( "\n", run.Log )}" );
		} );

		// 4. Brush edits: move, rotate, scale, snap, setmaterial.
		Check( "brush edit move rotate scale snap setmaterial", () =>
		{
			var map = FreshMap( "edit" );
			var run = RunScript( $$"""
				map_brush_create box1 shape=box min=0,0,0 max=64,64,64 map={{map}}
				map_brush_edit box1 op=move delta=16,0,0 map={{map}}
				map_brush_edit box1 op=rotate axis=z angle=90 map={{map}}
				map_brush_edit box1 op=scale factor=2 map={{map}}
				map_brush_edit box1 op=snap grid=16 map={{map}}
				map_brush_edit box1 op=setmaterial material=brick/brickwall001 map={{map}}
				""" );
			RequireOk( run, "brush edit script" );
		} );

		// 5. Clip: cut, split (second piece appears), and a named no-op.
		Check( "brush clip cut split and named no-op", () =>
		{
			var map = FreshMap( "clip" );
			var run = RunScript( $$"""
				map_brush_create cut1 shape=box min=0,0,0 max=128,128,128 map={{map}}
				map_brush_clip cut1 a=64,0,0 b=64,128,0 axis=z mode=cut map={{map}}
				map_brush_create split1 shape=box min=256,0,0 max=384,128,128 map={{map}}
				map_brush_clip split1 a=320,0,0 b=320,128,0 axis=z mode=split map={{map}}
				map_brush_create noop1 shape=box min=512,0,0 max=640,128,128 map={{map}}
				map_brush_clip noop1 a=1024,0,0 b=1024,128,0 axis=z mode=cut keep=back map={{map}}
				""" );
			RequireOk( run, "clip script" );
			Require( run.Log.Any( l => l.Contains( "split", StringComparison.Ordinal ) && l.Contains( "second piece", StringComparison.Ordinal ) ),
				"split did not report a second piece. log:\n" + string.Join( "\n", run.Log ) );
			Require( run.Log.Any( l => l.Contains( "no-op", StringComparison.Ordinal ) ),
				"plane-outside clip did not report a named no-op. log:\n" + string.Join( "\n", run.Log ) );
		} );

		// 6. Vertex move. A single-vertex move on a box corner always breaks one
		// face (3 mutually perpendicular faces share it) and the verb correctly
		// refuses it, so the success case uses a wedge: every wedge vertex
		// accepts a y-move (the -Y/+Y faces are triangles, always planar; the
		// bottom/-X/slope faces have no Y in their normals). Probe 2026-11-13:
		// wedge vertices 0..5 all accepted delta=0,8,0.
		Check( "brush vertex move", () =>
		{
			var map = FreshMap( "vertex" );
			var run = RunScript( $$"""
				map_brush_create wedge shape=wedge size=128,128,128 map={{map}}
				map_brush_vertex wedge vertex=0 delta=0,8,0 map={{map}}
				""" );
			RequireOk( run, "vertex script" );
			Require( run.Log.Any( l => l.Contains( "vertex 0 moved", StringComparison.Ordinal ) ),
				"vertex move did not log. log:\n" + string.Join( "\n", run.Log ) );
		} );

		// 7. Displacement create on the floor's top face (+Z, index 5) + sculpt.
		Check( "displacement create and sculpt", () =>
		{
			var map = FreshMap( "disp" );
			var run = RunScript( $$"""
				map_brush_create floor shape=box min=-256,-256,0 max=256,256,16 map={{map}}
				map_displacement_create brush=floor face=5 power=3 map={{map}}
				map_displacement_sculpt disp-1 mode=raise position=0,0,16 radius=64 strength=0.5 map={{map}}
				map_displacement_sculpt disp-1 mode=smooth position=0,0,16 radius=64 strength=0.5 map={{map}}
				""" );
			RequireOk( run, "displacement script" );
			Require( run.Log.Any( l => l.Contains( "grid 9x9", StringComparison.Ordinal ) ),
				"power-3 displacement did not report a 9x9 grid. log:\n" + string.Join( "\n", run.Log ) );
		} );

		// 8. Per-face material + UV.
		Check( "face material and uv", () =>
		{
			var map = FreshMap( "face" );
			var run = RunScript( $$"""
				map_brush_create fbox shape=box min=0,0,0 max=64,64,64 map={{map}}
				map_face_material fbox face=0 material=brick/brickwall001 map={{map}}
				map_face_uv fbox face=all scale=0.5,0.5 lightmapscale=32 map={{map}}
				""" );
			RequireOk( run, "face script" );
		} );

		// 9. Entity errors are named, not silent.
		Check( "entity place edit io named errors", () =>
		{
			var map = FreshMap( "enterr" );
			RequireFail( RunScript( $"map_entity_place class=no_such_class origin=0,0,0 map={map}" ),
				"Unknown entity class", "unknown class" );
			RequireFail( RunScript( $"map_entity_place class=func_door origin=0,0,0 map={map}" ),
				"NotPointEntity", "point-place of a brush class" );
			var ok = RunScript( $"map_entity_place class=light origin=0,0,64 targetname=lamp1 map={map}" );
			RequireOk( ok, "place light" );
			RequireFail( RunScript( $"map_entity_io_add source=lamp1 event=TurnOn target=no_such_target input=TurnOn map={map}" ),
				"io-target-not-found", "I/O to a missing target" );
			RequireFail( RunScript( $"map_entity_edit entity=ghost origin=1,2,3 map={map}" ),
				"entity-not-found", "edit of a missing entity" );
		} );

		// 10. Groups: define, link, edit member, unlink.
		Check( "group define link editmember unlink", () =>
		{
			var map = FreshMap( "group" );
			var run = RunScript( $$"""
				map_brush_create gbox1 shape=box min=0,0,0 max=64,64,64 map={{map}}
				map_brush_create gbox2 shape=box min=128,0,0 max=192,64,64 map={{map}}
				map_group op=define name=pair brush=gbox1,gbox2 map={{map}}
				map_group op=list map={{map}}
				""" );
			RequireOk( run, "group define/list" );
			Require( run.Log.Any( l => l.Contains( "1 definitions", StringComparison.Ordinal ) ),
				"group list did not report 1 definition. log:\n" + string.Join( "\n", run.Log ) );
		} );

		// 11. Visgroups: define, assign, hide.
		Check( "visgroup define assign setvisible", () =>
		{
			var map = FreshMap( "vis" );
			var run = RunScript( $$"""
				map_brush_create vbox shape=box min=0,0,0 max=64,64,64 map={{map}}
				map_visgroup op=define name=hidden color=#ff0000 map={{map}}
				map_visgroup op=assign name=hidden brush=vbox map={{map}}
				map_visgroup op=setvisible name=hidden visible=0 map={{map}}
				map_visgroup op=list map={{map}}
				""" );
			RequireOk( run, "visgroup script" );
		} );

		// 12. Export VMF/.map/.gmap, re-import each into a fresh map, counts match.
		Check( "export import round-trip vmf map gmap", () =>
		{
			var map = FreshMap( "rt" );
			var build = RunScript( $$"""
				map_brush_create rtbox shape=box min=0,0,0 max=64,64,64 material=brick/brickwall001 map={{map}}
				map_entity_place class=info_player_start origin=32,32,32 map={{map}}
				""" );
			RequireOk( build, "round-trip build" );
			foreach ( var format in new[] { "vmf", "map", "gmap" } )
			{
				var file = $"{map}-{format}";
				var exported = RunScript( $"map_export format={format} file={file} map={map}" );
				RequireOk( exported, $"export {format}" );
				Require( File.Exists( Path.Combine( settings.WorkDirectory, file ) ),
					$"export {format} did not write {file}." );
				var fresh = FreshMap( "rtin" );
				var imported = RunScript( $"map_import format={format} file={file} map={fresh}\nmap_validate map={fresh}" );
				RequireOk( imported, $"import {format}" );
				Require( imported.Log.Any( l => l.Contains( "1 brushes, 1 entities", StringComparison.Ordinal ) ),
					$"import {format} did not round-trip 1 brush + 1 entity. log:\n{string.Join( "\n", imported.Log )}" );
			}
		} );

		// 13. Bare verbs fail with a named error, never a silent success.
		Check( "bare verbs fail with named errors", () =>
		{
			var bare = new[]
			{
				("map_brush_create", "box needs"),
				("map_brush_edit", "needs a brush id"),
				("map_brush_clip", "needs a brush id"),
				("map_brush_vertex", "needs a brush id"),
				("map_displacement_create", "needs brush="),
				("map_displacement_sculpt", "needs a displacement id"),
				("map_face_material", "needs a brush id"),
				("map_face_uv", "needs a brush id"),
				("map_entity_place", "needs class="),
				("map_entity_edit", "needs an entity id"),
				("map_entity_io_add", "needs source="),
				("map_import", "format must be"),
				("map_export", "format must be"),
				("map_compile", "compile-empty-map"),
				("map_navmesh", "navmesh-failed"),
			};
			foreach ( var (verb, fragment) in bare )
				RequireFail( RunScript( $"{verb} map={FreshMap( "bare" )}" ), fragment, $"bare {verb}" );
		} );

		// 14. Lightmap bake (OBJ), navmesh, skybox + cubemap on the sealed room.
		Check( "lightmap bake navmesh skybox cubemap", () =>
		{
			var map = FreshMap( "bake" );
			var build = RunScript( SealedRoomScript( map ) );
			RequireOk( build, "bake-room build" );
			var bake = RunScript( $$"""
				map_lightmap_bake export=obj file={{map}}-bake map={{map}}
				map_navmesh map={{map}}
				map_entity_place class=env_cubemap origin=0,0,96 map={{map}}
				map_skybox sky=sky_day01_01 camera=1 cubemap=1 resolution=16 map={{map}}
				""" );
			RequireOk( bake, "bake/navmesh/skybox script" );
			Require( File.Exists( Path.Combine( settings.WorkDirectory, $"{map}-bake.obj" ) ),
				"lightmap bake did not write the .obj." );
			Require( bake.Log.Any( l => l.Contains( "map_navmesh:", StringComparison.Ordinal ) ),
				"navmesh did not log. log:\n" + string.Join( "\n", bake.Log ) );
			Require( bake.Log.Any( l => l.Contains( "cubemap baked", StringComparison.Ordinal ) ),
				"cubemap did not bake. log:\n" + string.Join( "\n", bake.Log ) );
		} );

		// 15. Native compile preset=quick (BSP-only) on the sealed room.
		Check( "native compile preset quick", () =>
		{
			var map = FreshMap( "compile" );
			var build = RunScript( SealedRoomScript( map ) );
			RequireOk( build, "compile-room build" );
			var compiled = RunScript( $"map_compile preset=quick out={map}-out map={map}" );
			RequireOk( compiled, "native quick compile" );
			Require( compiled.Log.Any( l => l.Contains( "quick succeeded", StringComparison.Ordinal ) ),
				"quick compile did not report success. log:\n" + string.Join( "\n", compiled.Log ) );
		} );
	}
}
