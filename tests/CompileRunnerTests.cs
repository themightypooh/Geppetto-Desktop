#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Effigy;

/// <summary>
/// hammer-compile-native-pipeline: the NATIVE Geppetto compile pipeline
/// (CompileRunner) — preset definitions, stage orchestration (BSP build -&gt;
/// visibility -&gt; lightmap bake) reusing Baker's progress/cancel pattern,
/// log capture to a file, stage failure mapped to named issues.
///
/// DEPENDENCY NOTE (2026-11-13): only LeakFinder has landed
/// (kernel/Effigy/Map/LeakFinder.cs) and is reused here through its real API
/// (the leak point file round-trips through it). BspBuilder / PortalGraph,
/// MapDocument / MapBrush, VmfIo and IssueEngine have NOT landed — their descs
/// name no concrete member signatures. These tests therefore exercise
/// CompileRunner against the specs exactly as filed, via the JSON fixtures in
/// tests/fixtures/hammer/hammer-compile-native-pipeline/ (box brushes =
/// plane-based brushes per the brush-core spec; player start + lights per the
/// map-document / leak specs) and via CompileInput built in code. When the
/// sibling types land, the final integrator adds the FromDocument adapter and
/// re-runs this group; no test here asserts an API the filed specs do not
/// name. NOT registered in TestRegistry.cs yet — the final integrator adds
/// the one line.
/// </summary>
internal static class CompileRunnerTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "compilerunner", name, test );
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
		bool Near( Vec3 a, Vec3 b, float eps = 1e-3f ) => a.AlmostEquals( b, eps );
		bool NearF( float a, float b, float eps = 1e-3f ) => Math.Abs( a - b ) <= eps;

		string FixtureDir()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-compile-native-pipeline" );
					if ( File.Exists( Path.Combine( candidate, "sealed-room.json" ) ) ) return candidate;
					dir = dir.Parent;
				}
			}
			throw new Exception( "compilerunner fixture dir not found (sealed-room.json missing above cwd / BaseDirectory)" );
		}

		CompileInput LoadFixture( string name )
		{
			var path = Path.Combine( FixtureDir(), name + ".json" );
			if ( !File.Exists( path ) ) throw new Exception( $"fixture {name}.json not found under tests/fixtures/hammer/hammer-compile-native-pipeline/" );
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var root = doc.RootElement;
			var input = new CompileInput( root.GetProperty( "name" ).GetString() ?? name );
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
					light.TryGetProperty( "range", out var r ) ? r.GetSingle() : 0f ) );
			}
			foreach ( var brush in root.GetProperty( "brushes" ).EnumerateArray() )
			{
				var material = brush.TryGetProperty( "material", out var m ) ? m.GetString() ?? "default" : "default";
				var isDetail = brush.TryGetProperty( "detail", out var d ) && d.GetBoolean();
				var id = brush.GetProperty( "id" ).GetInt32();
				var brushName = brush.GetProperty( "name" ).GetString() ?? $"brush-{id}";
				if ( brush.TryGetProperty( "box", out var box ) )
				{
					var min = box.GetProperty( "min" );
					var max = box.GetProperty( "max" );
					input.Brushes.Add( CompileBrush.Box( brushName, id,
						new Vec3( min[0].GetSingle(), min[1].GetSingle(), min[2].GetSingle() ),
						new Vec3( max[0].GetSingle(), max[1].GetSingle(), max[2].GetSingle() ),
						material, isDetail ) );
				}
				else if ( brush.TryGetProperty( "faces", out var faces ) )
				{
					var built = new CompileBrush( brushName, id, Array.Empty<CompileFace>(), isDetail );
					foreach ( var face in faces.EnumerateArray() )
					{
						var n = face.GetProperty( "normal" );
						built.Faces.Add( new CompileFace(
							new Vec3( n[0].GetSingle(), n[1].GetSingle(), n[2].GetSingle() ),
							face.GetProperty( "distance" ).GetSingle(),
							face.TryGetProperty( "material", out var fm ) ? fm.GetString() ?? material : material ) );
					}
					input.Brushes.Add( built );
				}
				else throw new Exception( $"fixture brush '{brushName}' has neither 'box' nor 'faces'" );
			}
			return input;
		}

		string WorkDir( string name )
		{
			var dir = Path.Combine( settings.WorkDirectory, "compilerunner", name );
			// Fresh output every run: a stale .pvs/.lightmap from an earlier
			// run would make the "failed run wrote no product" assertions lie.
			if ( Directory.Exists( dir ) ) Directory.Delete( dir, recursive: true );
			Directory.CreateDirectory( dir );
			return dir;
		}

		CompileResult Run( CompileInput input, CompilePreset preset, string workName,
			IProgress<CompileProgress>? progress = null, CancellationToken cancel = default )
		{
			var options = new CompileOptions { OutputFolder = WorkDir( workName ) };
			return CompileRunner.Run( input, preset, options, progress, cancel );
		}

		Check( "presets define the right stages, visibility and lightmap resolution", () =>
		{
			var quick = CompilePresetDef.For( CompilePreset.QuickPlay );
			var preview = CompilePresetDef.For( CompilePreset.Preview );
			var final = CompilePresetDef.For( CompilePreset.Final );
			Require( quick.Stages().SequenceEqual( new[] { CompileStage.BspBuild } ), "Quick Play must be BSP-only: " + string.Join( ",", quick.Stages() ) );
			Require( preview.Stages().SequenceEqual( new[] { CompileStage.BspBuild, CompileStage.Visibility, CompileStage.LightmapBake } ), "Preview stages wrong" );
			Require( final.Stages().SequenceEqual( new[] { CompileStage.BspBuild, CompileStage.Visibility, CompileStage.LightmapBake } ), "Final stages wrong" );
			Require( quick.Visibility == VisibilityQuality.None && quick.LightmapResolution == 0, "Quick Play must have no visibility and no lightmap" );
			Require( preview.Visibility == VisibilityQuality.Fast, "Preview visibility must be Fast" );
			Require( final.Visibility == VisibilityQuality.Full, "Final visibility must be Full" );
			Require( preview.LightmapResolution == 4 && final.LightmapResolution == 16, $"lightmap resolutions {preview.LightmapResolution}/{final.LightmapResolution}, expected 4/16" );
			Require( preview.LightmapResolution < final.LightmapResolution, "Preview lightmap must be lower-res than Final" );
			Throws<ArgumentException>( () => CompilePresetDef.For( (CompilePreset)99 ), "unknown preset" );
		} );

		Check( "quickplay runs bsp only and writes only a bsp file", () =>
		{
			var dir = WorkDir( "quickplay" );
			var result = Run( LoadFixture( "sealed-room" ), CompilePreset.QuickPlay, "quickplay" );
			Require( result.Succeeded, "Quick Play failed: " + result.Error );
			Require( result.CompletedStages.SequenceEqual( new[] { CompileStage.BspBuild } ), "Quick Play stages: " + string.Join( ",", result.CompletedStages ) );
			Require( result.Bsp is not null && result.Bsp.LeafCount > 0, "Quick Play produced no BSP" );
			Require( result.Pvs is null, "Quick Play must not compute visibility" );
			Require( result.Lightmaps is null, "Quick Play must not bake lightmaps" );
			Require( File.Exists( Path.Combine( dir, "sealed-room.bsp" ) ), "no .bsp output" );
			Require( !File.Exists( Path.Combine( dir, "sealed-room.pvs" ) ), "Quick Play wrote a .pvs it should not have" );
			Require( !File.Exists( Path.Combine( dir, "sealed-room.lightmap" ) ), "Quick Play wrote a .lightmap it should not have" );
			Require( result.Duration >= TimeSpan.Zero, "no elapsed time recorded" );
			Require( result.Issues.Count == 0, "clean map produced issues: " + string.Join( "; ", result.Issues ) );
		} );

		Check( "preview runs bsp visibility and a low-res lightmap", () =>
		{
			var result = Run( LoadFixture( "sealed-room" ), CompilePreset.Preview, "preview" );
			Require( result.Succeeded, "Preview failed: " + result.Error );
			Require( result.CompletedStages.SequenceEqual( new[] { CompileStage.BspBuild, CompileStage.Visibility, CompileStage.LightmapBake } ), "Preview stages: " + string.Join( ",", result.CompletedStages ) );
			Require( result.Pvs is not null && result.Pvs.Quality == VisibilityQuality.Fast, "Preview PVS must be Fast quality" );
			Require( result.Lightmaps is not null && result.Lightmaps.Resolution == 4, "Preview lightmap must be 4x4" );
			Require( result.Lightmaps.Faces.Count > 0, "Preview baked no faces" );
		} );

		Check( "final runs all stages with full visibility and a full-res lightmap", () =>
		{
			var dir = WorkDir( "final" );
			var result = Run( LoadFixture( "sealed-room" ), CompilePreset.Final, "final" );
			Require( result.Succeeded, "Final failed: " + result.Error );
			Require( result.CompletedStages.SequenceEqual( new[] { CompileStage.BspBuild, CompileStage.Visibility, CompileStage.LightmapBake } ), "Final stages: " + string.Join( ",", result.CompletedStages ) );
			Require( result.Bsp is not null && result.Pvs is not null && result.Lightmaps is not null, "Final is missing a stage product" );
			Require( result.Pvs.Quality == VisibilityQuality.Full, "Final PVS must be Full quality" );
			Require( result.Lightmaps.Resolution == 16, "Final lightmap must be 16x16" );
			Require( File.Exists( Path.Combine( dir, "sealed-room.bsp" ) ), "no .bsp output" );
			Require( File.Exists( Path.Combine( dir, "sealed-room.pvs" ) ), "no .pvs output" );
			Require( File.Exists( Path.Combine( dir, "sealed-room.lightmap" ) ), "no .lightmap output" );
			Require( File.ReadAllText( Path.Combine( dir, "sealed-room.bsp" ) ).StartsWith( "GEPPETTO-BSP 1", StringComparison.Ordinal ), ".bsp header wrong" );
			Require( File.ReadAllText( Path.Combine( dir, "sealed-room.pvs" ) ).StartsWith( "GEPPETTO-PVS 1", StringComparison.Ordinal ), ".pvs header wrong" );
			Require( File.ReadAllText( Path.Combine( dir, "sealed-room.lightmap" ) ).StartsWith( "GEPPETTO-LIGHTMAP 1", StringComparison.Ordinal ), ".lightmap header wrong" );
		} );

		Check( "progress reports stages in order, monotonic, and ends at 100", () =>
		{
			var reports = new List<CompileProgress>();
			var progress = new Progress<CompileProgress>( r => reports.Add( r ) );
			var result = Run( LoadFixture( "sealed-room" ), CompilePreset.Final, "progress", progress );
			Require( result.Succeeded, "run failed: " + result.Error );
			Require( reports.Count > 0, "no progress reports" );
			var phases = reports.Select( r => r.Phase ).ToList();
			int Index( CompilePhase phase ) => phases.IndexOf( phase );
			Require( Index( CompilePhase.BspBuild ) >= 0 && Index( CompilePhase.Visibility ) > Index( CompilePhase.BspBuild ), "Visibility did not follow BSP in progress" );
			Require( Index( CompilePhase.LightmapBake ) > Index( CompilePhase.Visibility ), "Lightmap did not follow Visibility in progress" );
			Require( Index( CompilePhase.Done ) > Index( CompilePhase.LightmapBake ), "Done did not follow Lightmap in progress" );
			Require( NearF( reports[^1].Percent, 100f ), $"last progress {reports[^1].Percent}, expected 100" );
			Require( reports[^1].Phase == CompilePhase.Done, "last progress phase is not Done" );
			for ( var i = 1; i < reports.Count; i++ )
				Require( reports[i].Percent >= reports[i - 1].Percent - 1e-6f, $"progress went backwards: {reports[i - 1].Percent} -> {reports[i].Percent}" );
			foreach ( var r in reports )
				Require( r.Percent >= 0f && r.Percent <= 100f, $"progress out of range: {r.Percent}" );
		} );

		Check( "cancel during lightmap stops the run with no lightmaps and no lightmap file", () =>
		{
			var dir = WorkDir( "cancel-lightmap" );
			var cts = new CancellationTokenSource();
			var progress = new Progress<CompileProgress>( r =>
			{
				if ( r.Phase == CompilePhase.LightmapBake ) cts.Cancel();
			} );
			var result = Run( LoadFixture( "sealed-room" ), CompilePreset.Final, "cancel-lightmap", progress, cts.Token );
			Require( !result.Succeeded, "cancelled run reported success" );
			Require( result.Cancelled, "run did not report cancelled" );
			Require( result.CancelledStage == CompileStage.LightmapBake, $"cancelled stage {result.CancelledStage}, expected LightmapBake" );
			Require( result.CompletedStages.SequenceEqual( new[] { CompileStage.BspBuild, CompileStage.Visibility } ), "stages before cancel: " + string.Join( ",", result.CompletedStages ) );
			Require( result.Lightmaps is null, "cancelled run claims lightmaps" );
			Require( !File.Exists( Path.Combine( dir, "sealed-room.lightmap" ) ), "cancelled run wrote a .lightmap" );
			Require( result.Log.Any( l => l.Contains( "Cancelled", StringComparison.Ordinal ) ), "log does not record the cancel" );
		} );

		Check( "pre-cancelled token cancels before any stage", () =>
		{
			var cts = new CancellationTokenSource();
			cts.Cancel();
			var result = Run( LoadFixture( "sealed-room" ), CompilePreset.Final, "cancel-before-start", cancel: cts.Token );
			Require( !result.Succeeded && result.Cancelled, "pre-cancelled run did not cancel" );
			Require( result.CompletedStages.Count == 0, "pre-cancelled run completed stages" );
			Require( result.CancelledStage is null, "pre-cancelled run names a stage it never entered" );
			Require( result.Bsp is null && result.Pvs is null && result.Lightmaps is null, "pre-cancelled run claims products" );
		} );

		Check( "leaking map fails at the bsp stage with a named leak issue and a point file", () =>
		{
			var dir = WorkDir( "leak" );
			var input = LoadFixture( "leaking-room" );
			var result = Run( input, CompilePreset.Final, "leak" );
			Require( !result.Succeeded, "leaking map compiled successfully" );
			Require( result.FailedStage == CompileStage.BspBuild, $"failed stage {result.FailedStage}, expected BspBuild" );
			var leak = result.Issues.FirstOrDefault( i => i.Code == "leak" );
			Require( leak is not null, "no 'leak' issue: " + string.Join( "; ", result.Issues ) );
			Require( leak.Severity == CompileSeverity.Error, "leak issue is not an error" );
			Require( NearF( leak.Location.x, 136f, 9f ) && Math.Abs( leak.Location.y ) <= 1f && leak.Location.z >= 0f && leak.Location.z <= 128f,
				$"leak location {leak.Location} is not at the gap (x 128..144, y 0, z 0..128)" );
			Require( leak.FixHint.Length > 0, "leak issue has no fix hint" );
			Require( result.Leak is not null && result.Leak.IsLeaked, "no leak result recorded" );
			Require( Near( result.Leak.Path[0], new Vec3( 0, 0, 32 ) ), "leak path does not start at the player start" );
			Require( result.Leak.Path[^1].x > 128f, $"leak path does not exit past the gap: {result.Leak.Path[^1]}" );
			Require( result.Pvs is null && result.Lightmaps is null, "failed run claims later-stage products" );
			var lin = Path.Combine( dir, "leaking-room.lin" );
			Require( File.Exists( lin ), "no leak point file written" );
			Require( result.OutputFiles.Contains( lin ), ".lin is not listed in OutputFiles" );
			var points = LeakFinder.ReadPointFile( lin );
			Require( points.Count == result.Leak.Path.Count, "point file point count moved" );
			Require( Near( points[0], new Vec3( 0, 0, 32 ) ), "point file does not start at the player start" );
			Require( !File.Exists( Path.Combine( dir, "leaking-room.pvs" ) ), "failed run wrote a .pvs" );
			Require( !File.Exists( Path.Combine( dir, "leaking-room.lightmap" ) ), "failed run wrote a .lightmap" );
		} );

		Check( "sealed room has one interior leaf that sees only itself", () =>
		{
			var input = LoadFixture( "sealed-room" );
			var result = Run( input, CompilePreset.Final, "sealed-leaf" );
			Require( result.Succeeded, "run failed: " + result.Error );
			Require( result.Leak is not null && !result.Leak.IsLeaked, "sealed room reported a leak" );
			var startLeaf = result.Bsp!.LeafContaining( new Vec3( 0, 0, 32 ) );
			Require( startLeaf is not null, "no leaf contains the player start" );
			Require( !startLeaf.IsSolid && !startLeaf.IsVoid, "player start leaf is solid/void" );
			var visible = result.Pvs!.VisibleFrom( startLeaf.Id );
			Require( visible.Count == 1 && visible[0] == startLeaf.Id, "sealed interior leaf sees more than itself: " + string.Join( ",", visible ) );
			Require( result.Bsp.EmptyLeafCount == 1, $"empty leaves {result.Bsp.EmptyLeafCount}, expected exactly one interior leaf (every exterior leaf touches the outer bound, so all of them are void)" );
			Require( result.Bsp.VoidLeafCount >= 1, "no void leaf" );
		} );

		Check( "two rooms see each other through the doorway and never see the void", () =>
		{
			var input = LoadFixture( "two-room" );
			var result = Run( input, CompilePreset.Final, "two-room" );
			Require( result.Succeeded, "run failed: " + result.Error );
			Require( result.Leak is not null && !result.Leak.IsLeaked, "sealed two-room map reported a leak" );
			Require( result.Bsp!.PortalCount >= 1, "two-room BSP has no portals" );
			// Points off the doorway axis (y=100): the doorway leaf itself
			// spans both rooms through the opening (no x-plane crosses it —
			// the jamb faces were partitioned away by the y=±32 splits), so
			// the room centres on the doorway axis share one leaf. Off-axis
			// points sit in distinct room leaves, and the PVS must still
			// connect them through the doorway.
			var leafA = result.Bsp.LeafContaining( new Vec3( -160, 100, 32 ) );
			var leafB = result.Bsp.LeafContaining( new Vec3( 160, 100, 32 ) );
			Require( leafA is not null && leafB is not null, "a room centre is in no leaf" );
			Require( leafA.Id != leafB.Id, "both room centres landed in one leaf" );
			Require( result.Pvs!.VisibleFrom( leafA.Id ).Contains( leafB.Id ), "room A does not see room B" );
			Require( result.Pvs.VisibleFrom( leafB.Id ).Contains( leafA.Id ), "room B does not see room A (PVS must be symmetric)" );
			var voidIds = result.Bsp.Leaves.Where( l => l.IsVoid ).Select( l => l.Id ).ToHashSet();
			foreach ( var pair in result.Pvs.Sets )
				Require( !pair.Value.Any( id => voidIds.Contains( id ) ), $"leaf {pair.Key} sees void leaf {string.Join( ",", pair.Value.Where( id => voidIds.Contains( id ) ) )}" );
		} );

		Check( "invalid zero-volume brush fails at the bsp stage with an invalid-brush issue", () =>
		{
			var input = LoadFixture( "sealed-room" );
			input.Brushes.Add( CompileBrush.Box( "bad", 99, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 0 ) ) );
			var result = Run( input, CompilePreset.Final, "invalid-brush" );
			Require( !result.Succeeded, "map with a zero-volume brush compiled" );
			Require( result.FailedStage == CompileStage.BspBuild, $"failed stage {result.FailedStage}, expected BspBuild" );
			var issue = result.Issues.FirstOrDefault( i => i.Code == "invalid-brush" );
			Require( issue is not null, "no 'invalid-brush' issue: " + string.Join( "; ", result.Issues ) );
			Require( issue.Subject == "bad", $"issue subject '{issue.Subject}', expected the brush name 'bad'" );
			Require( issue.Message.Contains( "zero-volume", StringComparison.Ordinal ), "issue does not name the defect: " + issue.Message );
		} );

		Check( "empty map fails at the bsp stage with an empty-map issue", () =>
		{
			var result = Run( new CompileInput( "empty" ), CompilePreset.Final, "empty-map" );
			Require( !result.Succeeded, "empty map compiled" );
			Require( result.FailedStage == CompileStage.BspBuild, $"failed stage {result.FailedStage}, expected BspBuild" );
			Require( result.Issues.Any( i => i.Code == "empty-map" ), "no 'empty-map' issue" );
		} );

		Check( "over-limit brush count fails with a limit-exceeded issue", () =>
		{
			var input = LoadFixture( "sealed-room" );
			var options = new CompileOptions { OutputFolder = WorkDir( "over-limit" ), MaxBrushes = 2 };
			var result = CompileRunner.Run( input, CompilePreset.Final, options );
			Require( !result.Succeeded, "over-limit map compiled" );
			Require( result.FailedStage == CompileStage.BspBuild, $"failed stage {result.FailedStage}, expected BspBuild" );
			Require( result.Issues.Any( i => i.Code == "limit-exceeded" ), "no 'limit-exceeded' issue: " + string.Join( "; ", result.Issues ) );
		} );

		Check( "missing player start warns and skips the leak check", () =>
		{
			var input = LoadFixture( "sealed-room" );
			input.PlayerStart = null;
			var result = Run( input, CompilePreset.Final, "no-start" );
			Require( result.Succeeded, "map without a player start failed: " + result.Error );
			var warning = result.Issues.FirstOrDefault( i => i.Code == "missing-player-start" );
			Require( warning is not null, "no 'missing-player-start' issue" );
			Require( warning.Severity == CompileSeverity.Warning, "missing player start must be a warning, not an error" );
			Require( result.Leak is null, "leak check ran with no player start" );
		} );

		Check( "detail brush is excluded from the bsp solid but still gets a lightmap", () =>
		{
			var input = LoadFixture( "sealed-room" );
			input.Brushes.Add( CompileBrush.Box( "pillar", 50, new Vec3( -16, -16, 0 ), new Vec3( 16, 16, 64 ), isDetail: true ) );
			var result = Run( input, CompilePreset.Final, "detail-brush" );
			Require( result.Succeeded, "run failed: " + result.Error );
			Require( result.Leak is not null && !result.Leak.IsLeaked, "detail brush must not leak the room" );
			Require( result.Lightmaps!.Faces.Any( f => f.BrushId == 50 ), "detail brush got no lightmap faces" );
			var startLeaf = result.Bsp!.LeafContaining( new Vec3( 0, 0, 96 ) );
			Require( startLeaf is not null && !startLeaf.IsSolid, "detail brush made the room interior solid" );
		} );

		Check( "log file captures stages and the result", () =>
		{
			var dir = WorkDir( "log" );
			var logPath = Path.Combine( dir, "custom.log" );
			var options = new CompileOptions { OutputFolder = dir, LogPath = logPath };
			var result = CompileRunner.Run( LoadFixture( "sealed-room" ), CompilePreset.Final, options );
			Require( result.Succeeded, "run failed: " + result.Error );
			Require( result.LogPath == logPath, $"LogPath '{result.LogPath}' is not the requested path" );
			Require( File.Exists( logPath ), "no log file written" );
			var text = File.ReadAllText( logPath );
			Require( text.Contains( "BspBuild", StringComparison.Ordinal ), "log has no BSP stage" );
			Require( text.Contains( "Visibility", StringComparison.Ordinal ), "log has no visibility stage" );
			Require( text.Contains( "LightmapBake", StringComparison.Ordinal ), "log has no lightmap stage" );
			Require( text.Contains( "Compile finished", StringComparison.Ordinal ), "log has no finish line" );
			Require( result.Log.Count > 0, "in-memory log is empty" );
			Require( result.Log.Any( l => l.Contains( "Compile started", StringComparison.Ordinal ) ), "in-memory log has no start line" );
		} );

		Check( "same input compiles deterministically", () =>
		{
			var dirA = WorkDir( "determinism-a" );
			var dirB = WorkDir( "determinism-b" );
			var a = CompileRunner.Run( LoadFixture( "sealed-room" ), CompilePreset.Final, new CompileOptions { OutputFolder = dirA } );
			var b = CompileRunner.Run( LoadFixture( "sealed-room" ), CompilePreset.Final, new CompileOptions { OutputFolder = dirB } );
			Require( a.Succeeded && b.Succeeded, "runs failed" );
			Require( a.Bsp!.LeafCount == b.Bsp!.LeafCount, $"leaf count moved {a.Bsp.LeafCount} -> {b.Bsp.LeafCount}" );
			Require( a.Bsp.PortalCount == b.Bsp.PortalCount, $"portal count moved {a.Bsp.PortalCount} -> {b.Bsp.PortalCount}" );
			foreach ( var pair in a.Pvs!.Sets )
				Require( b.Pvs!.Sets.TryGetValue( pair.Key, out var other ) && other.SequenceEqual( pair.Value ), $"PVS set for leaf {pair.Key} moved" );
			Require( a.Lightmaps!.Faces.Count == b.Lightmaps!.Faces.Count, "lightmap face count moved" );
			for ( var i = 0; i < a.Lightmaps.Faces.Count; i++ )
				Require( NearF( a.Lightmaps.Faces[i].Mean, b.Lightmaps.Faces[i].Mean, 0f ), $"lightmap face {i} mean moved {a.Lightmaps.Faces[i].Mean} -> {b.Lightmaps.Faces[i].Mean}" );
			Require( File.ReadAllText( Path.Combine( dirA, "sealed-room.bsp" ) ) == File.ReadAllText( Path.Combine( dirB, "sealed-room.bsp" ) ), ".bsp bytes moved" );
		} );

		Check( "far room wall is darker than near room wall with one light in room a", () =>
		{
			var input = LoadFixture( "two-room" );
			var result = Run( input, CompilePreset.Final, "shadow" );
			Require( result.Succeeded, "run failed: " + result.Error );
			var westId = input.Brushes.First( b => b.Name == "wall_west" ).Id;
			var eastId = input.Brushes.First( b => b.Name == "wall_east" ).Id;
			// Box face order: 0 = +X, 1 = -X. West wall's inner face is +X (0); east wall's inner face is -X (1).
			var nearFace = result.Lightmaps!.Faces.First( f => f.BrushId == westId && f.FaceIndex == 0 );
			var farFace = result.Lightmaps.Faces.First( f => f.BrushId == eastId && f.FaceIndex == 1 );
			Require( nearFace.Mean > 0.05f, $"near wall mean {nearFace.Mean} is not lit" );
			Require( farFace.Mean < nearFace.Mean * 0.5f, $"far wall mean {farFace.Mean} is not darker than near wall {nearFace.Mean}" );
			Require( result.Lightmaps.Faces.All( f => f.Mean >= 0f && f.Mean <= 1f ), "a lightmap mean is out of [0,1]" );
		} );

		Check( "bad construction fails loudly", () =>
		{
			Throws<ArgumentNullException>( () => new CompileRunner( null!, CompilePreset.Final ), "null input" );
			Throws<ArgumentException>( () => new CompileRunner( new CompileInput( "x" ), (CompilePreset)99 ), "unknown preset" );
			Throws<ArgumentException>( () => new CompileRunner( new CompileInput( "x" ), CompilePreset.Final, new CompileOptions { MaxBrushes = 0 } ), "MaxBrushes 0" );
			Throws<ArgumentException>( () => new CompileRunner( new CompileInput( "x" ), CompilePreset.Final, new CompileOptions { LightmapResolutionOverride = 65 } ), "lightmap override 65" );
		} );
	}
}
