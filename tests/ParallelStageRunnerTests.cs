#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Effigy;

/// <summary>
/// hammer-compile-parallel-stages: parallel compile stage execution
/// (ParallelStageRunner + the CompileRunner wiring). A two-room compile
/// must be byte-identical single-threaded and parallel, progress must stay
/// in stage order under parallelism, and cancelling mid-parallel-stage must
/// stop all workers promptly.
///
/// Fixture: reuses the pipeline ticket's two-room map
/// (tests/fixtures/hammer/hammer-compile-native-pipeline/two-room.json —
/// two sealed rooms joined by a doorway, one light in room A), so the
/// parallel and sequential runs compile the exact input the pipeline tests
/// already prove sealed. No new fixture file: the two-room fixture is that
/// file.
///
/// NOT registered in TestRegistry.cs yet — the final integrator adds the one
/// line next to CompileRunnerTests (same as that group, still unregistered):
///   ParallelStageRunnerTests.Register( registry, settings );
/// Run the group with --filter compileparallel. No UI code; backend only.
/// </summary>
internal static class ParallelStageRunnerTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "compileparallel", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: wrong exception {ex.GetType().Name}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name }, got silent success" );
		}
		bool NearF( float a, float b, float eps = 1e-3f ) => Math.Abs( a - b ) <= eps;

		string FixtureDir()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-compile-native-pipeline" );
					if ( File.Exists( Path.Combine( candidate, "two-room.json" ) ) ) return candidate;
					dir = dir.Parent;
				}
			}
			throw new Exception( "two-room fixture dir not found (two-room.json missing above cwd / BaseDirectory)" );
		}

		CompileInput TwoRoom()
		{
			var path = Path.Combine( FixtureDir(), "two-room.json" );
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var root = doc.RootElement;
			var input = new CompileInput( root.GetProperty( "name" ).GetString() ?? "two-room" );
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
				var box = brush.GetProperty( "box" );
				var min = box.GetProperty( "min" );
				var max = box.GetProperty( "max" );
				input.Brushes.Add( CompileBrush.Box( brushName, id,
					new Vec3( min[0].GetSingle(), min[1].GetSingle(), min[2].GetSingle() ),
					new Vec3( max[0].GetSingle(), max[1].GetSingle(), max[2].GetSingle() ),
					material, isDetail ) );
			}
			return input;
		}

		string WorkDir( string name )
		{
			var dir = Path.Combine( settings.WorkDirectory, "compileparallel", name );
			if ( Directory.Exists( dir ) ) Directory.Delete( dir, recursive: true );
			Directory.CreateDirectory( dir );
			return dir;
		}

		CompileResult RunTwoRoom( CompilePreset preset, string workName, CompileOptions options,
			IProgress<CompileProgress>? progress = null, CancellationToken cancel = default )
		{
			options.OutputFolder = WorkDir( workName );
			return CompileRunner.Run( TwoRoom(), preset, options, progress, cancel );
		}

		Check( "parallel runner returns results in input order even when items finish out of order", () =>
		{
			// Later items sleep less, so they finish first; the merged array
			// must still be in input order.
			var work = Enumerable.Range( 0, 32 ).ToList();
			var results = ParallelStageRunner.RunParallel(
				work,
				( i, token ) => { Thread.Sleep( 32 - i ); return i * i; },
				maxDegreeOfParallelism: 8, CancellationToken.None );
			Require( results.Length == 32, $"result count {results.Length}, expected 32" );
			for ( var i = 0; i < 32; i++ )
				Require( results[i] == i * i, $"result[{i}] is {results[i]}, expected {i * i} (merge order moved)" );
		} );

		Check( "parallel runner resolves the worker count to the machine and clamps", () =>
		{
			var cpus = Environment.ProcessorCount;
			Require( cpus >= 1, "ProcessorCount < 1" );
			Require( ParallelStageRunner.ResolveDegreeOfParallelism( -1 ) == cpus, $"degree -1 resolved to {ParallelStageRunner.ResolveDegreeOfParallelism( -1 )}, expected {cpus}" );
			Require( ParallelStageRunner.ResolveDegreeOfParallelism( 0 ) == cpus, "degree 0 must resolve to the machine" );
			Require( ParallelStageRunner.ResolveDegreeOfParallelism( 1 ) == 1, "degree 1 must stay 1" );
			Require( ParallelStageRunner.ResolveDegreeOfParallelism( 4 ) == Math.Min( 4, cpus ), "degree 4 moved" );
			Require( ParallelStageRunner.ResolveDegreeOfParallelism( 1000 ) == cpus, "degree 1000 must clamp to the machine" );
			Require( ParallelStageRunner.IsSingleThreaded( 1 ), "degree 1 is not single-threaded" );
			Require( !ParallelStageRunner.IsSingleThreaded( -1 ) || cpus == 1, "machine default must be parallel on a multi-core box" );

			var defaults = new CompileOptions();
			Require( defaults.MaxDegreeOfParallelism == -1, $"default MaxDegreeOfParallelism {defaults.MaxDegreeOfParallelism}, expected -1" );
			Require( !defaults.SingleThreaded, "SingleThreaded must default off" );
			Throws<ArgumentException>( () => new CompileOptions { MaxDegreeOfParallelism = 0 }.Validate(), "MaxDegreeOfParallelism 0" );
			Throws<ArgumentException>( () => new CompileOptions { MaxDegreeOfParallelism = -2 }.Validate(), "MaxDegreeOfParallelism -2" );
			new CompileOptions { MaxDegreeOfParallelism = 1 }.Validate();
			new CompileOptions { MaxDegreeOfParallelism = -1 }.Validate();
		} );

		Check( "deterministic merge orders pairs by stable key", () =>
		{
			var merged = ParallelStageRunner.MergeByKey( new[]
			{
				( Key: 7, Value: "g" ), ( Key: 1, Value: "a" ), ( Key: 4, Value: "d" ), ( Key: 2, Value: "b" ),
			} );
			Require( merged.SequenceEqual( new[] { "a", "b", "d", "g" } ), "merge did not sort by key: " + string.Join( ",", merged ) );
			Require( ParallelStageRunner.MergeByKey( Array.Empty<(int, string)>() ).Count == 0, "empty merge must be empty" );
			Throws<ArgumentNullException>( () => ParallelStageRunner.MergeByKey<string>( null! ), "null pairs" );
		} );

		Check( "parallel runner honours cancel promptly and stops scheduling", () =>
		{
			var cts = new CancellationTokenSource();
			var ran = 0;
			Throws<OperationCanceledException>( () => ParallelStageRunner.RunParallel(
				Enumerable.Range( 0, 200 ).ToList(),
				( i, token ) =>
				{
					Interlocked.Increment( ref ran );
					if ( i == 5 ) cts.Cancel();
					Thread.Sleep( 5 );
					return i;
				},
				maxDegreeOfParallelism: 8, cts.Token ), "cancel mid-run" );
			Require( ran < 200, $"cancel did not stop scheduling: all 200 items ran" );
			// A pre-cancelled token throws before any item runs.
			var pre = new CancellationTokenSource();
			pre.Cancel();
			Throws<OperationCanceledException>( () => ParallelStageRunner.RunParallel(
				new[] { 1, 2, 3 }, ( i, token ) => i, maxDegreeOfParallelism: 4, pre.Token ), "pre-cancelled token" );
		} );

		Check( "two-room final is byte-identical single-threaded vs parallel", () =>
		{
			var singleDir = WorkDir( "two-room-single" );
			var par4Dir = WorkDir( "two-room-par4" );
			var deg1Dir = WorkDir( "two-room-deg1" );
			var defDir = WorkDir( "two-room-default" );
			var single = CompileRunner.Run( TwoRoom(), CompilePreset.Final,
				new CompileOptions { OutputFolder = singleDir, SingleThreaded = true } );
			var par4 = CompileRunner.Run( TwoRoom(), CompilePreset.Final,
				new CompileOptions { OutputFolder = par4Dir, MaxDegreeOfParallelism = 4 } );
			var deg1 = CompileRunner.Run( TwoRoom(), CompilePreset.Final,
				new CompileOptions { OutputFolder = deg1Dir, MaxDegreeOfParallelism = 1 } );
			var def = CompileRunner.Run( TwoRoom(), CompilePreset.Final,
				new CompileOptions { OutputFolder = defDir } );
			Require( single.Succeeded, "single-threaded run failed: " + single.Error );
			Require( par4.Succeeded, "parallel run failed: " + par4.Error );
			Require( deg1.Succeeded, "degree-1 run failed: " + deg1.Error );
			Require( def.Succeeded, "default run failed: " + def.Error );
			Require( single.Bsp!.LeafCount == par4.Bsp!.LeafCount && single.Bsp.PortalCount == par4.Bsp.PortalCount,
				$"BSP moved single {single.Bsp.LeafCount}/{single.Bsp.PortalCount} -> parallel {par4.Bsp.LeafCount}/{par4.Bsp.PortalCount}" );
			foreach ( var pair in single.Pvs!.Sets )
				Require( par4.Pvs!.Sets.TryGetValue( pair.Key, out var other ) && other.SequenceEqual( pair.Value ),
					$"PVS set for leaf {pair.Key} moved between single-threaded and parallel" );
			Require( single.Lightmaps!.Faces.Count == par4.Lightmaps!.Faces.Count, "lightmap face count moved" );
			for ( var i = 0; i < single.Lightmaps.Faces.Count; i++ )
				Require( single.Lightmaps.Faces[i].Mean == par4.Lightmaps!.Faces[i].Mean,
					$"lightmap face {i} mean moved {single.Lightmaps.Faces[i].Mean} -> {par4.Lightmaps!.Faces[i].Mean}" );
			foreach ( var file in new[] { "two-room.bsp", "two-room.pvs", "two-room.lightmap" } )
			{
				var a = File.ReadAllBytes( Path.Combine( singleDir, file ) );
				Require( File.ReadAllBytes( Path.Combine( par4Dir, file ) ).SequenceEqual( a ),
					$"{file} bytes moved between single-threaded and 4-worker runs" );
				Require( File.ReadAllBytes( Path.Combine( deg1Dir, file ) ).SequenceEqual( a ),
					$"{file} bytes moved between single-threaded and degree-1 runs" );
				Require( File.ReadAllBytes( Path.Combine( defDir, file ) ).SequenceEqual( a ),
					$"{file} bytes moved between single-threaded and default (machine) runs" );
			}
		} );

		Check( "progress events arrive in stage order under parallelism", () =>
		{
			var reports = new List<CompileProgress>();
			var progress = new SyncProgress( r => reports.Add( r ) );
			var result = RunTwoRoom( CompilePreset.Final, "progress-parallel",
				new CompileOptions { MaxDegreeOfParallelism = 4 }, progress );
			Require( result.Succeeded, "run failed: " + result.Error );
			Require( reports.Count > 0, "no progress reports" );
			var order = new[] { CompilePhase.Preparing, CompilePhase.BspBuild, CompilePhase.Visibility, CompilePhase.LightmapBake, CompilePhase.Writing, CompilePhase.Done };
			var seen = new List<CompilePhase>();
			foreach ( var r in reports )
				if ( seen.Count == 0 || seen[^1] != r.Phase ) seen.Add( r.Phase );
			var pos = -1;
			foreach ( var phase in seen )
			{
				var next = Array.IndexOf( order, phase );
				Require( next > pos, $"progress phases out of stage order: {string.Join( " -> ", seen )}" );
				pos = next;
			}
			Require( seen[^1] == CompilePhase.Done, "last progress phase is not Done: " + string.Join( " -> ", seen ) );
			Require( reports.Any( r => r.Phase == CompilePhase.LightmapBake ), "no LightmapBake progress report (aggregation reported nothing)" );
			for ( var i = 1; i < reports.Count; i++ )
				Require( reports[i].Percent >= reports[i - 1].Percent - 1e-6f, $"progress went backwards under parallelism: {reports[i - 1].Percent} -> {reports[i].Percent}" );
			Require( NearF( reports[^1].Percent, 100f ), $"last progress {reports[^1].Percent}, expected 100" );
			foreach ( var r in reports )
				Require( r.Percent >= 0f && r.Percent <= 100f, $"progress out of range: {r.Percent}" );
		} );

		Check( "cancelling mid-parallel-lightmap stops all workers promptly", () =>
		{
			// A long lightmap stage: the two rooms plus a grid of detail
			// pillars (detail brushes get lightmaps but never touch the BSP
			// solid, so the map stays sealed). The cancel fires on the first
			// LightmapBake progress report, as in the pipeline cancel test —
			// under parallelism every other worker must stop within one
			// face's work, so the cancelled run is much shorter than a full
			// run of the same input.
			CompileInput LongInput()
			{
				var input = TwoRoom();
				var id = 100;
				for ( var ix = 0; ix < 10; ix++ )
					for ( var iy = 0; iy < 6; iy++ )
					{
						var cx = -240 + ix * 48;
						var cy = -112 + iy * 44;
						if ( Math.Abs( cx ) < 32 && Math.Abs( cy ) < 48 ) continue; // keep the doorway clear
						input.Brushes.Add( CompileBrush.Box( $"pillar-{id}", id++,
							new Vec3( cx - 8, cy - 8, 0 ), new Vec3( cx + 8, cy + 8, 64 ), isDetail: true ) );
					}
				return input;
			}
			var pillarCount = LongInput().Brushes.Count - TwoRoom().Brushes.Count;
			Require( pillarCount >= 40, $"only {pillarCount} pillars, not enough faces for a cancellable bake" );

			var fullDir = WorkDir( "cancel-full" );
			var fullClock = Stopwatch.StartNew();
			var full = CompileRunner.Run( LongInput(), CompilePreset.Final,
				new CompileOptions { OutputFolder = fullDir, MaxDegreeOfParallelism = 4 } );
			fullClock.Stop();
			Require( full.Succeeded, "full run failed: " + full.Error );
			Require( full.Lightmaps!.Faces.Count > 300, $"full run baked only {full.Lightmaps.Faces.Count} faces" );

			var dir = WorkDir( "cancel-parallel" );
			var cts = new CancellationTokenSource();
			// Synchronous consumer again: the cancel must fire inline on the
			// first LightmapBake report, while workers are still baking —
			// a Progress<T> callback could be delivered after the stage.
			var progress = new SyncProgress( r =>
			{
				if ( r.Phase == CompilePhase.LightmapBake ) cts.Cancel();
			} );
			var clock = Stopwatch.StartNew();
			var result = CompileRunner.Run( LongInput(), CompilePreset.Final,
				new CompileOptions { OutputFolder = dir, MaxDegreeOfParallelism = 4 }, progress, cts.Token );
			clock.Stop();
			Require( !result.Succeeded, "cancelled run reported success" );
			Require( result.Cancelled, "run did not report cancelled" );
			Require( result.CancelledStage == CompileStage.LightmapBake, $"cancelled stage {result.CancelledStage}, expected LightmapBake" );
			Require( result.CompletedStages.SequenceEqual( new[] { CompileStage.BspBuild, CompileStage.Visibility } ),
				"stages before cancel: " + string.Join( ",", result.CompletedStages ) );
			Require( result.Lightmaps is null, "cancelled run claims lightmaps" );
			Require( !File.Exists( Path.Combine( dir, "two-room.lightmap" ) ), "cancelled run wrote a .lightmap" );
			Require( clock.Elapsed < fullClock.Elapsed,
				$"cancelled run took {clock.Elapsed} vs full run {fullClock.Elapsed}: workers did not stop promptly" );
		} );
	}

	/// <summary>
	/// Synchronous IProgress: Progress&lt;T&gt; posts asynchronously and does
	/// not preserve order (verified 2026-11-13 on .NET 10: rapid reports
	/// arrived shuffled), so a test that asserts report ORDER — or that must
	/// cancel inline on the first report of a stage — uses this consumer,
	/// which runs inline on the reporting thread under one lock.
	/// </summary>
	sealed class SyncProgress : IProgress<CompileProgress>
	{
		readonly Action<CompileProgress> _onReport;
		readonly object _gate = new();
		public SyncProgress( Action<CompileProgress> onReport ) => _onReport = onReport;
		public void Report( CompileProgress value ) { lock ( _gate ) _onReport( value ); }
	}
}
