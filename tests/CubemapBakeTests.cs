#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Effigy;
using Effigy.Map;

/// <summary>
/// hammer-cubemap-probes-bake: env_cubemap probe entities (position,
/// resolution) + native 6-face capture (buildcubemaps equivalent) feeding
/// the material system.
///
/// DEPENDENCY NOTE (2026-11-13): CompileRunner HAS landed
/// (kernel/Effigy/Map/CompileRunner.cs) and is consumed directly — the
/// fixture room is the same sealed room as
/// hammer-compile-native-pipeline/sealed-room.json, and the BSP-backed
/// validation check runs a real QuickPlay compile for its BSP. The
/// EntityRegistry (hammer-entity-registry-fgd-json, DONE) has no
/// env_cubemap class in its JSON, so MapEntities cannot place a probe
/// today: CubemapProbe.FromMapEntity's wrong-class path is therefore NOT
/// covered here (a MapEntity cannot even be constructed for it without
/// the registry class) — only the null-argument path is tested, and the
/// class-addition follow-up is named in the report. Probes here are built
/// directly or loaded from the fixture JSON in
/// tests/fixtures/hammer/hammer-cubemap-probes-bake/room-with-probe.json.
/// NOT registered in TestRegistry.cs yet — the final integrator adds the
/// one line; temp-register only to run this group, then revert.
/// </summary>
internal static class CubemapBakeTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "cubemapbake", name, test );
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
		bool NearF( float a, float b, float eps = 1e-3f ) => Math.Abs( a - b ) <= eps;

		string FixtureDir()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-cubemap-probes-bake" );
					if ( File.Exists( Path.Combine( candidate, "room-with-probe.json" ) ) ) return candidate;
					dir = dir.Parent;
				}
			}
			throw new Exception( "cubemapbake fixture dir not found (room-with-probe.json missing above cwd / BaseDirectory)" );
		}

		(CompileInput Input, CubemapProbeRegistry Registry, CubemapBakeScene Scene) LoadFixture()
		{
			var path = Path.Combine( FixtureDir(), "room-with-probe.json" );
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var root = doc.RootElement;
			var input = new CompileInput( root.GetProperty( "name" ).GetString() ?? "room-with-probe" );
			if ( root.TryGetProperty( "playerStart", out var start ) && start.ValueKind == JsonValueKind.Array )
				input.PlayerStart = new Vec3( start[0].GetSingle(), start[1].GetSingle(), start[2].GetSingle() );
			foreach ( var light in root.GetProperty( "lights" ).EnumerateArray() )
			{
				var p = light.GetProperty( "position" );
				var c = light.GetProperty( "color" );
				input.Lights.Add( new CompileLight(
					new Vec3( p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle() ),
					light.GetProperty( "intensity" ).GetSingle(),
					new Vec3( c[0].GetSingle(), c[1].GetSingle(), c[2].GetSingle() ),
					light.TryGetProperty( "range", out var range ) ? range.GetSingle() : 0f ) );
			}
			foreach ( var brush in root.GetProperty( "brushes" ).EnumerateArray() )
			{
				var box = brush.GetProperty( "box" );
				var min = box.GetProperty( "min" );
				var max = box.GetProperty( "max" );
				input.Brushes.Add( CompileBrush.Box(
					brush.GetProperty( "name" ).GetString() ?? "brush",
					brush.GetProperty( "id" ).GetInt32(),
					new Vec3( min[0].GetSingle(), min[1].GetSingle(), min[2].GetSingle() ),
					new Vec3( max[0].GetSingle(), max[1].GetSingle(), max[2].GetSingle() ),
					brush.GetProperty( "material" ).GetString() ?? "default",
					brush.TryGetProperty( "detail", out var detail ) && detail.GetBoolean() ) );
			}
			var registry = new CubemapProbeRegistry();
			foreach ( var probe in root.GetProperty( "probes" ).EnumerateArray() )
			{
				var p = probe.GetProperty( "position" );
				registry.Add( new CubemapProbe(
					probe.GetProperty( "name" ).GetString() ?? "env_cubemap",
					new Vec3( p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle() ),
					probe.TryGetProperty( "resolution", out var res ) ? res.GetInt32() : CubemapBake.DefaultResolution ) );
			}
			return ( input, registry, CubemapBakeScene.FromCompileInput( input ) );
		}

		CubemapProbeRegistry Only( CubemapProbeRegistry registry, string name )
		{
			var single = new CubemapProbeRegistry();
			single.Add( registry.Find( name ) ?? throw new Exception( $"fixture probe '{name}' missing" ) );
			return single;
		}

		Check( "one probe in a room captures 6 faces", () =>
		{
			var (input, all, scene) = LoadFixture();
			_ = input;
			var result = CubemapBake.Bake( Only( all, "room_probe" ), scene );
			Require( result.Succeeded, "bake failed: " + result.Error );
			Require( result.Set is not null, "bake succeeded with no set" );
			Require( result.Set!.ProbeCount == 1, $"expected 1 probe, got {result.Set.ProbeCount}" );
			Require( result.Set.FaceCount == 6, $"expected 6 faces, got {result.Set.FaceCount}" );
			Require( result.Set.FacesFor( "room_probe" ).Count == 6, "FacesFor(room_probe) is not 6" );
			foreach ( var face in result.Set.Faces )
			{
				Require( face.Resolution == 32, $"face {face.Face} resolution {face.Resolution} != 32" );
				Require( face.TotalTexels == 32 * 32, "face texel count is not 32x32" );
				// Sealed room: every texel's ray hits a wall, floor or ceiling.
				Require( face.HitTexels == face.TotalTexels, $"face {face.Face} has {face.SkyTexels} sky texels in a sealed room" );
				Require( face.Mean > 0f, $"face {face.Face} is black with a light in the room" );
				foreach ( var v in face.Rgb )
					Require( float.IsFinite( v ) && v >= 0f && v <= 1f, $"face {face.Face} has an out-of-range texel {v}" );
			}
			// Opposite faces see differently-lit surfaces (ceiling near the light vs floor far from it).
			var means = result.Set.Faces.Select( f => f.Mean ).ToList();
			Require( means.Max() - means.Min() > 0.01f, $"faces do not differ (max {means.Max():0.###} min {means.Min():0.###})" );
			Require( result.Issues.Count == 0, "clean bake reported issues: " + string.Join( "; ", result.Issues ) );
		} );

		Check( "probe inside a wall is a validation issue", () =>
		{
			var (_, all, scene) = LoadFixture();
			var registry = Only( all, "wall_probe" );
			var issues = CubemapBake.ValidateProbes( registry, scene );
			Require( issues.Any( i => i.Code == CubemapBake.IssueCodes.ProbeInsideSolid && i.Severity == CompileSeverity.Error ),
				"no probe-inside-solid error; got: " + string.Join( "; ", issues ) );
			var result = CubemapBake.Bake( registry, scene );
			Require( !result.Succeeded, "bake with a probe inside a wall succeeded" );
			Require( result.Set is null, "failed bake still produced a set" );
			Require( result.Error is not null && result.Error.Contains( "inside a solid brush" ), "bake error does not name the defect: " + result.Error );
			Require( result.Issues.Any( i => i.Code == CubemapBake.IssueCodes.ProbeInsideSolid ), "bake issues do not carry probe-inside-solid" );
		} );

		Check( "capture output is referenced by a shiny prop material", () =>
		{
			var (_, all, scene) = LoadFixture();
			var result = CubemapBake.Bake( Only( all, "room_probe" ), scene );
			Require( result.Succeeded, "bake failed: " + result.Error );
			var set = result.Set!;
			// A shiny prop (prop_static, material metal/shiny) at (10, 0, 64)
			// stores as its $envmap the reference of the nearest baked probe.
			// The bake only names the reference; MaterialBridge owns $envmap
			// storage (it already preserves "$envmap env_cubemap" through VMT).
			var envmap = set.ResolveEnvmap( new Vec3( 10, 0, 64 ) );
			Require( envmap == "env_cubemap:room_probe", $"shiny prop resolved to '{envmap}', not 'env_cubemap:room_probe'" );
			Require( set.ReferenceForProbe( "room_probe" ) == "env_cubemap:room_probe", "ReferenceForProbe mismatch" );
			Require( set.ReferenceForProbe( "missing_probe" ) is null, "unknown probe produced a reference" );
			Require( set.NearestProbe( new Vec3( 10, 0, 64 ) ) == "room_probe", "NearestProbe mismatch" );
			var text = CubemapBake.WriteText( set );
			Require( text.Contains( "probe room_probe resolution 32 position 0 0 64" ), "cubemap text does not name the probe the material references" );
		} );

		Check( "probe too close to a wall warns but still bakes", () =>
		{
			var (_, all, scene) = LoadFixture();
			var registry = Only( all, "low_probe" );
			var issues = CubemapBake.ValidateProbes( registry, scene );
			Require( issues.Any( i => i.Code == CubemapBake.IssueCodes.ProbeTooCloseToWall && i.Severity == CompileSeverity.Warning ),
				"no probe-too-close-to-wall warning; got: " + string.Join( "; ", issues ) );
			Require( !issues.Any( i => i.Severity == CompileSeverity.Error ), "close probe also errored: " + string.Join( "; ", issues ) );
			var result = CubemapBake.Bake( registry, scene );
			Require( result.Succeeded, "bake with a close probe failed: " + result.Error );
			Require( result.Set!.FaceCount == 6, "close probe did not capture 6 faces" );
			Require( result.Issues.Any( i => i.Code == CubemapBake.IssueCodes.ProbeTooCloseToWall ), "bake issues lost the warning" );
		} );

		Check( "probe outside the map is an error", () =>
		{
			var (_, all, scene) = LoadFixture();
			var registry = Only( all, "outside_probe" );
			var issues = CubemapBake.ValidateProbes( registry, scene );
			Require( issues.Any( i => i.Code == CubemapBake.IssueCodes.ProbeOutsideMap && i.Severity == CompileSeverity.Error ),
				"no probe-outside-map error; got: " + string.Join( "; ", issues ) );
			var result = CubemapBake.Bake( registry, scene );
			Require( !result.Succeeded, "bake with an outside probe succeeded" );
			Require( result.Set is null, "failed bake still produced a set" );
		} );

		Check( "duplicate probe names are rejected", () =>
		{
			var registry = new CubemapProbeRegistry();
			registry.Add( new CubemapProbe( "room_probe", new Vec3( 0, 0, 64 ) ) );
			Throws<CubemapBakeException>( () => registry.Add( new CubemapProbe( "ROOM_PROBE", new Vec3( 10, 0, 64 ) ) ), "case-insensitive duplicate name" );
			Require( registry.Count == 1, "duplicate add changed the registry" );
		} );

		Check( "bad resolution is an invalid-probe issue", () =>
		{
			var (_, _, scene) = LoadFixture();
			foreach ( var bad in new[] { 24, 0, 512 } )
			{
				var registry = new CubemapProbeRegistry();
				registry.Add( new CubemapProbe( "bad_probe", new Vec3( 0, 0, 64 ), bad ) );
				var issues = CubemapBake.ValidateProbes( registry, scene );
				Require( issues.Any( i => i.Code == CubemapBake.IssueCodes.InvalidProbe && i.Severity == CompileSeverity.Error ),
					$"resolution {bad} gave no invalid-probe error; got: " + string.Join( "; ", issues ) );
				var result = CubemapBake.Bake( registry, scene );
				Require( !result.Succeeded, $"bake with resolution {bad} succeeded" );
			}
			// Boundary resolutions are valid.
			foreach ( var good in new[] { 16, 256 } )
			{
				var probe = new CubemapProbe( "ok_probe", new Vec3( 0, 0, 64 ), good );
				Require( probe.Validate().Count == 0, $"resolution {good} should be valid: " + string.Join( ",", probe.Validate() ) );
			}
		} );

		Check( "zero probes bakes an empty set with a warning", () =>
		{
			var (_, _, scene) = LoadFixture();
			var result = CubemapBake.Bake( new CubemapProbeRegistry(), scene );
			Require( result.Succeeded, "empty bake failed: " + result.Error );
			Require( result.Set is not null && result.Set.FaceCount == 0 && result.Set.ProbeCount == 0, "empty bake did not produce an empty set" );
			Require( result.Issues.Any( i => i.Code == CubemapBake.IssueCodes.NoProbes && i.Severity == CompileSeverity.Warning ),
				"empty bake gave no no-probes warning" );
		} );

		Check( "cubemap text names the header, probe and all six faces", () =>
		{
			var (_, all, scene) = LoadFixture();
			var result = CubemapBake.Bake( Only( all, "room_probe" ), scene );
			Require( result.Succeeded, "bake failed: " + result.Error );
			var text = CubemapBake.WriteText( result.Set! );
			Require( text.StartsWith( "GEPPETTO-CUBEMAP 1\n", StringComparison.Ordinal ), "text header is not GEPPETTO-CUBEMAP 1" );
			Require( text.Contains( "probes 1\n" ), "text does not declare 1 probe" );
			Require( text.Contains( "faces 6\n" ), "text does not declare 6 faces" );
			Require( text.Contains( "probe room_probe resolution 32 position 0 0 64" ), "text probe line mismatch" );
			foreach ( var face in CubemapBake.AllFaces )
				Require( text.Contains( $"face room_probe {face} mean " ), $"text has no face line for {face}" );
			var written = CubemapBake.WriteFile( Path.Combine( settings.WorkDirectory, "room.cubemap" ), result.Set! );
			Require( File.Exists( written ), "WriteFile did not write the file" );
			Require( File.ReadAllText( written ) == text, "written file differs from WriteText" );
		} );

		Check( "capture is deterministic", () =>
		{
			var (_, all, scene) = LoadFixture();
			var first = CubemapBake.Bake( Only( all, "room_probe" ), scene );
			var second = CubemapBake.Bake( Only( all, "room_probe" ), scene );
			Require( first.Succeeded && second.Succeeded, "determinism bakes failed" );
			Require( CubemapBake.WriteText( first.Set! ) == CubemapBake.WriteText( second.Set! ), "same input baked different text" );
			for ( var i = 0; i < first.Set!.Faces.Count; i++ )
			{
				Require( first.Set.Faces[i].Rgb.SequenceEqual( second.Set!.Faces[i].Rgb ), $"face {i} differs between runs" );
				Require( NearF( first.Set.Faces[i].Mean, second.Set.Faces[i].Mean ), $"face {i} mean differs between runs" );
			}
		} );

		Check( "pre-cancelled token cancels the bake", () =>
		{
			var (_, all, scene) = LoadFixture();
			using var cts = new CancellationTokenSource();
			cts.Cancel();
			var result = CubemapBake.Bake( Only( all, "room_probe" ), scene, cancel: cts.Token );
			Require( result.Cancelled, "pre-cancelled bake did not report Cancelled" );
			Require( !result.Succeeded, "cancelled bake reports Succeeded" );
			Require( result.Set is null, "cancelled bake produced a set" );
		} );

		Check( "bsp-backed validation puts the probe in an empty leaf", () =>
		{
			var (input, all, scene) = LoadFixture();
			var compiled = CompileRunner.Run( input, CompilePreset.QuickPlay, new CompileOptions() );
			Require( compiled.Succeeded, "fixture room did not compile: " + compiled.Error );
			Require( compiled.Bsp is not null, "QuickPlay run produced no BSP" );
			var good = CubemapBake.ValidateProbes( Only( all, "room_probe" ), scene, compiled.Bsp );
			Require( good.Count == 0, "room probe failed BSP validation: " + string.Join( "; ", good ) );
			var bad = CubemapBake.ValidateProbes( Only( all, "wall_probe" ), scene, compiled.Bsp );
			Require( bad.Any( i => i.Code == CubemapBake.IssueCodes.ProbeInsideSolid && i.Severity == CompileSeverity.Error ),
				"BSP validation missed the probe inside the wall; got: " + string.Join( "; ", bad ) );
			var outside = CubemapBake.ValidateProbes( Only( all, "outside_probe" ), scene, compiled.Bsp );
			Require( outside.Any( i => i.Code == CubemapBake.IssueCodes.ProbeOutsideMap && i.Severity == CompileSeverity.Error ),
				"BSP validation missed the outside probe; got: " + string.Join( "; ", outside ) );
		} );

		Check( "two probes capture 12 faces and the nearer probe wins the envmap reference", () =>
		{
			var (_, all, scene) = LoadFixture();
			var registry = new CubemapProbeRegistry();
			registry.Add( all.Find( "room_probe" )! );
			registry.Add( all.Find( "far_probe" )! );
			var result = CubemapBake.Bake( registry, scene );
			Require( result.Succeeded, "bake failed: " + result.Error );
			Require( result.Set!.ProbeCount == 2, $"expected 2 probes, got {result.Set.ProbeCount}" );
			Require( result.Set.FaceCount == 12, $"expected 12 faces, got {result.Set.FaceCount}" );
			Require( result.Set.FacesFor( "far_probe" ).Count == 6, "far_probe did not capture 6 faces" );
			Require( result.Set.ResolveEnvmap( new Vec3( -90, 0, 64 ) ) == "env_cubemap:far_probe",
				"position near far_probe did not resolve to it: " + result.Set.ResolveEnvmap( new Vec3( -90, 0, 64 ) ) );
			Require( result.Set.ResolveEnvmap( new Vec3( 10, 0, 64 ) ) == "env_cubemap:room_probe",
				"position near room_probe did not resolve to it" );
		} );

		Check( "registry loads probes from json", () =>
		{
			var registry = CubemapProbeRegistry.LoadJson(
				"{ \"probes\": [ " +
				"{ \"name\": \"a\", \"position\": [0, 0, 64], \"resolution\": 16 }, " +
				"{ \"name\": \"b\", \"position\": [10, 20, 30] } ] }" );
			Require( registry.Count == 2, $"expected 2 probes, got {registry.Count}" );
			Require( registry.Find( "a" )!.Resolution == 16, "probe a resolution did not load" );
			Require( registry.Find( "b" )!.Resolution == CubemapBake.DefaultResolution, "probe b did not default to 32" );
			Require( registry.Find( "b" )!.Position.x == 10f && registry.Find( "b" )!.Position.z == 30f, "probe b position did not load" );
			Throws<CubemapBakeException>( () => CubemapProbeRegistry.LoadJson( "{ \"probes\": [ { \"name\": \"x\" } ] }" ), "probe without position" );
			Throws<CubemapBakeException>( () => CubemapProbeRegistry.LoadJson( "not json" ), "malformed json" );
			Throws<CubemapBakeException>( () => CubemapProbeRegistry.LoadJson( "{ \"probes\": [ { \"name\": \"x\", \"position\": [0, 0, 64] }, { \"name\": \"x\", \"position\": [1, 1, 64] } ] }" ), "duplicate names in json" );
		} );

		Check( "progress reports capturing and ends at 100", () =>
		{
			var (_, all, scene) = LoadFixture();
			var reports = new List<CubemapBakeProgress>();
			var progress = new Progress<CubemapBakeProgress>( r => reports.Add( r ) );
			var result = CubemapBake.Bake( Only( all, "room_probe" ), scene, progress: progress );
			Require( result.Succeeded, "bake failed: " + result.Error );
			Require( reports.Count > 0, "no progress reports" );
			Require( reports.Any( r => r.Phase == CubemapBakePhase.Capturing ), "no Capturing progress phase" );
			Require( NearF( reports[^1].Percent, 100f ), $"last progress is {reports[^1].Percent}, not 100" );
			Require( reports[^1].Phase == CubemapBakePhase.Done, "last progress phase is not Done" );
			float previous = -1f;
			foreach ( var r in reports )
			{
				Require( r.Percent >= previous, $"progress went backwards: {previous} -> {r.Percent}" );
				previous = r.Percent;
			}
		} );

		Check( "from-map-entity rejects null", () =>
		{
			Throws<ArgumentNullException>( () => CubemapProbe.FromMapEntity( null! ), "FromMapEntity(null)" );
			// Wrong-class rejection is NOT covered here: a MapEntity can only
			// be built through MapEntities + the registry, and the registry
			// JSON has no env_cubemap class yet (see the file header). It is
			// covered when the registry owner adds the class.
		} );
	}
}
