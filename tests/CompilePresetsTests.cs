#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Effigy;

/// <summary>
/// hammer-compile-presets-only-entities: the P2 fast-iteration extensions to
/// CompileRunner — Only-Entities (rebuild the entity lump only, reuse BSP +
/// PVS + lightmaps), the FastVis preset + FastVis flag, saved named presets
/// (CompileNamedPreset + CompilePresetStore, JSON sidecar) and structured
/// go-to log entries (CompileLogEntry + CompileSourceRef).
///
/// Fixtures: sealed-room / leaking-room / two-room from
/// tests/fixtures/hammer/hammer-compile-native-pipeline/ (pass 1) plus
/// entities-room.json from tests/fixtures/hammer/
/// hammer-compile-presets-only-entities/ (sealed room + 3 entities).
/// NOT registered in TestRegistry.cs yet — the final integrator adds the one
/// line, same as CompileRunnerTests.
/// </summary>
internal static class CompilePresetsTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "compilepresets", name, test );
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

		string FixtureDir( string subdir, string probeFile )
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", subdir );
					if ( File.Exists( Path.Combine( candidate, probeFile ) ) ) return candidate;
					dir = dir.Parent;
				}
			}
			throw new Exception( $"compilepresets fixture dir '{subdir}' not found ({probeFile} missing above cwd / BaseDirectory)" );
		}

		string RunnerSourcePath()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					var candidate = Path.Combine( dir.FullName, "kernel", "Effigy", "Map", "CompileRunner.cs" );
					if ( File.Exists( candidate ) ) return candidate;
					dir = dir.Parent;
				}
			}
			throw new Exception( "CompileRunner.cs not found above cwd / BaseDirectory" );
		}

		CompileInput LoadFixture( string subdir, string name )
		{
			var path = Path.Combine( FixtureDir( subdir, name + ".json" ), name + ".json" );
			if ( !File.Exists( path ) ) throw new Exception( $"fixture {subdir}/{name}.json not found" );
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
				else throw new Exception( $"fixture brush '{brushName}' has no 'box'" );
			}
			if ( root.TryGetProperty( "entities", out var entities ) && entities.ValueKind == JsonValueKind.Array )
			{
				foreach ( var entity in entities.EnumerateArray() )
				{
					var built = new CompileEntity(
						entity.GetProperty( "id" ).GetInt32(),
						entity.GetProperty( "classname" ).GetString() ?? "",
						entity.TryGetProperty( "origin", out var o ) && o.ValueKind == JsonValueKind.Array
							? new Vec3( o[0].GetSingle(), o[1].GetSingle(), o[2].GetSingle() ) : Vec3.Zero,
						entity.TryGetProperty( "targetname", out var t ) ? t.GetString() ?? "" : "" );
					if ( entity.TryGetProperty( "keyvalues", out var kvs ) && kvs.ValueKind == JsonValueKind.Object )
						foreach ( var kv in kvs.EnumerateObject() )
							built.Keyvalues[kv.Name] = kv.Value.GetString() ?? "";
					input.Entities.Add( built );
				}
			}
			return input;
		}

		CompileInput SealedRoom() => LoadFixture( "hammer-compile-native-pipeline", "sealed-room" );
		CompileInput EntitiesRoom() => LoadFixture( "hammer-compile-presets-only-entities", "entities-room" );

		string WorkDir( string name )
		{
			var dir = Path.Combine( settings.WorkDirectory, "compilepresets", name );
			if ( Directory.Exists( dir ) ) Directory.Delete( dir, recursive: true );
			Directory.CreateDirectory( dir );
			return dir;
		}

		Check( "fastvis preset defines bsp plus fast visibility and no lightmap", () =>
		{
			var def = CompilePresetDef.For( CompilePreset.FastVis );
			Require( def.Name == "Fast-VIS", $"FastVis name '{def.Name}'" );
			Require( def.Stages().SequenceEqual( new[] { CompileStage.BspBuild, CompileStage.Visibility } ), "FastVis stages: " + string.Join( ",", def.Stages() ) );
			Require( def.Visibility == VisibilityQuality.Fast, "FastVis visibility must be Fast" );
			Require( def.LightmapResolution == 0, "FastVis must bake no lightmap" );
		} );

		Check( "fastvis run writes bsp and pvs only, with fast pvs", () =>
		{
			var dir = WorkDir( "fastvis" );
			var result = CompileRunner.Run( SealedRoom(), CompilePreset.FastVis, new CompileOptions { OutputFolder = dir } );
			Require( result.Succeeded, "FastVis run failed: " + result.Error );
			Require( result.CompletedStages.SequenceEqual( new[] { CompileStage.BspBuild, CompileStage.Visibility } ), "FastVis stages: " + string.Join( ",", result.CompletedStages ) );
			Require( result.Pvs is not null && result.Pvs.Quality == VisibilityQuality.Fast, "FastVis PVS must be Fast quality" );
			Require( result.Lightmaps is null, "FastVis must not bake lightmaps" );
			Require( File.Exists( Path.Combine( dir, "sealed-room.bsp" ) ), "no .bsp output" );
			Require( File.Exists( Path.Combine( dir, "sealed-room.pvs" ) ), "no .pvs output" );
			Require( !File.Exists( Path.Combine( dir, "sealed-room.lightmap" ) ), "FastVis wrote a .lightmap it should not have" );
		} );

		Check( "fastvis option forces fast visibility on final but keeps its lightmap", () =>
		{
			var result = CompileRunner.Run( SealedRoom(), CompilePreset.Final,
				new CompileOptions { OutputFolder = WorkDir( "fastvis-flag" ), FastVis = true } );
			Require( result.Succeeded, "run failed: " + result.Error );
			Require( result.Pvs is not null && result.Pvs.Quality == VisibilityQuality.Fast, "FastVis flag must relax Final to Fast PVS" );
			Require( result.Lightmaps is not null && result.Lightmaps.Resolution == 16, "FastVis flag must not touch the lightmap resolution" );
			Require( result.CompletedStages.Count == 3, "flagged Final must still run all three stages" );
		} );

		Check( "fastvis option does not add visibility to quickplay", () =>
		{
			var result = CompileRunner.Run( SealedRoom(), CompilePreset.QuickPlay,
				new CompileOptions { OutputFolder = WorkDir( "fastvis-quick" ), FastVis = true } );
			Require( result.Succeeded, "run failed: " + result.Error );
			Require( result.Pvs is null, "FastVis flag must not add visibility to QuickPlay" );
			Require( result.CompletedStages.SequenceEqual( new[] { CompileStage.BspBuild } ), "QuickPlay stages moved: " + string.Join( ",", result.CompletedStages ) );
		} );

		Check( "only-entities reuses the prior bsp pvs and lightmaps without rewriting them", () =>
		{
			var dir = WorkDir( "only-entities" );
			var input = EntitiesRoom();
			var full = CompileRunner.Run( input, CompilePreset.Final, new CompileOptions { OutputFolder = dir } );
			Require( full.Succeeded, "full run failed: " + full.Error );
			Require( full.Entities is not null && full.Entities.Count == 3, "full run did not build the 3-entity lump" );
			var entitiesPath = Path.Combine( dir, "entities-room.entities" );
			Require( File.Exists( entitiesPath ), "full run wrote no .entities lump" );
			Require( File.ReadAllText( entitiesPath ).StartsWith( "GEPPETTO-ENTITIES 1", StringComparison.Ordinal ), ".entities header wrong" );
			var bspBefore = File.ReadAllText( Path.Combine( dir, "entities-room.bsp" ) );
			var pvsBefore = File.ReadAllText( Path.Combine( dir, "entities-room.pvs" ) );
			var lightmapBefore = File.ReadAllText( Path.Combine( dir, "entities-room.lightmap" ) );
			var artefacts = full.ToArtefacts( input );
			Require( artefacts.Bsp is not null && artefacts.Pvs is not null && artefacts.Lightmaps is not null, "artefacts are missing a stage product" );

			// Only entities change: move the light, rename the crate.
			var input2 = EntitiesRoom();
			input2.Entities[0].Origin = new Vec3( 0, 0, 100 );
			input2.Entities[1].TargetName = "crate2";
			var only = CompileRunner.RunOnlyEntities( input2, artefacts, new CompileOptions { OutputFolder = dir } );
			Require( only.Succeeded, "Only-Entities failed: " + only.Error );
			Require( only.OnlyEntities, "result does not report OnlyEntities" );
			Require( only.ReusedPriorArtefacts, "result does not report reused artefacts" );
			Require( ReferenceEquals( only.Bsp, full.Bsp ), "Only-Entities rebuilt the BSP instead of reusing it" );
			Require( ReferenceEquals( only.Pvs, full.Pvs ), "Only-Entities rebuilt the PVS instead of reusing it" );
			Require( ReferenceEquals( only.Lightmaps, full.Lightmaps ), "Only-Entities rebuilt the lightmaps instead of reusing them" );
			Require( only.CompletedStages.SequenceEqual( new[] { CompileStage.EntityLump } ), "Only-Entities stages: " + string.Join( ",", only.CompletedStages ) );
			Require( only.Entities is not null && only.Entities.Count == 3, "Only-Entities lump has the wrong entity count" );
			Require( File.ReadAllText( Path.Combine( dir, "entities-room.bsp" ) ) == bspBefore, ".bsp was rewritten by Only-Entities" );
			Require( File.ReadAllText( Path.Combine( dir, "entities-room.pvs" ) ) == pvsBefore, ".pvs was rewritten by Only-Entities" );
			Require( File.ReadAllText( Path.Combine( dir, "entities-room.lightmap" ) ) == lightmapBefore, ".lightmap was rewritten by Only-Entities" );
			var lump = File.ReadAllText( entitiesPath );
			Require( lump.Contains( "origin 0 0 100", StringComparison.Ordinal ), "lump does not carry the moved light origin" );
			Require( lump.Contains( "crate2", StringComparison.Ordinal ), "lump does not carry the renamed crate" );
			Require( !lump.Contains( "crate1", StringComparison.Ordinal ), "lump still carries the old crate name" );
			Require( only.ReusedFiles.Contains( Path.Combine( dir, "entities-room.bsp" ) ), "reused .bsp is not listed in ReusedFiles" );
			Require( only.OutputFiles.Count == 1 && only.OutputFiles[0] == entitiesPath, "Only-Entities must write exactly the .entities file: " + string.Join( ",", only.OutputFiles ) );
			Require( only.Log.Any( l => l.Contains( "reused", StringComparison.OrdinalIgnoreCase ) ), "log does not record the reuse" );
		} );

		Check( "only-entities refuses a changed brush with a stale-build issue", () =>
		{
			var dir = WorkDir( "only-entities-stale" );
			var input = EntitiesRoom();
			var full = CompileRunner.Run( input, CompilePreset.Final, new CompileOptions { OutputFolder = dir } );
			Require( full.Succeeded, "full run failed: " + full.Error );
			var artefacts = full.ToArtefacts( input );

			var changed = EntitiesRoom();
			changed.Brushes[0] = CompileBrush.Box( "floor", 0, new Vec3( -144, -144, -16 ), new Vec3( 144, 144, 1 ) );
			var only = CompileRunner.RunOnlyEntities( changed, artefacts, new CompileOptions { OutputFolder = dir } );
			Require( !only.Succeeded, "Only-Entities accepted a changed brush" );
			Require( only.FailedStage == CompileStage.EntityLump, $"failed stage {only.FailedStage}, expected EntityLump" );
			var stale = only.Issues.FirstOrDefault( i => i.Code == "stale-build" );
			Require( stale is not null, "no 'stale-build' issue: " + string.Join( "; ", only.Issues ) );
			Require( stale.Severity == CompileSeverity.Error, "stale-build must be an error" );
			Require( only.Bsp is null && only.Pvs is null && only.Lightmaps is null, "stale run claims reused products" );
			Require( !File.Exists( Path.Combine( dir, "entities-room.entities" ) ) || File.ReadAllText( Path.Combine( dir, "entities-room.entities" ) ).Contains( "lamp1", StringComparison.Ordinal ),
				"stale run overwrote the .entities lump" );
		} );

		Check( "only-entities with no prior bsp throws", () =>
		{
			var empty = new CompileBuildArtefacts( "x", null!, null, null, CompilePreset.Final, "fp", Array.Empty<string>() );
			Throws<ArgumentException>( () => CompileRunner.RunOnlyEntities( EntitiesRoom(), empty ), "artefacts with no BSP" );
			Throws<ArgumentNullException>( () => CompileRunner.RunOnlyEntities( EntitiesRoom(), null! ), "null artefacts" );
			Throws<ArgumentNullException>( () => CompileRunner.RunOnlyEntities( null!, empty ), "null input" );
		} );

		Check( "only-entities rejects a duplicate entity id with an invalid-entity issue", () =>
		{
			var dir = WorkDir( "only-entities-dup-id" );
			var input = EntitiesRoom();
			var full = CompileRunner.Run( input, CompilePreset.Final, new CompileOptions { OutputFolder = dir } );
			Require( full.Succeeded, "full run failed: " + full.Error );
			var artefacts = full.ToArtefacts( input );

			var bad = EntitiesRoom();
			bad.Entities.Add( new CompileEntity( 2, "prop_static", new Vec3( 0, 0, 32 ), "copy" ) );
			var only = CompileRunner.RunOnlyEntities( bad, artefacts, new CompileOptions { OutputFolder = dir } );
			Require( !only.Succeeded, "Only-Entities accepted a duplicate entity id" );
			var issue = only.Issues.FirstOrDefault( i => i.Code == "invalid-entity" );
			Require( issue is not null, "no 'invalid-entity' issue: " + string.Join( "; ", only.Issues ) );
			Require( issue.EntityId == 2, $"issue entity id {issue.EntityId}, expected 2" );
			Require( issue.SourceRef.EntityId == 2 && issue.SourceRef.HasTarget, "issue has no go-to entity reference" );
			var entry = only.LogEntries.FirstOrDefault( e => e.Code == "invalid-entity" );
			Require( entry is not null && entry.SourceRef.EntityId == 2, "no go-to log entry for the duplicate entity" );
		} );

		Check( "only-entities rejects an entity with no classname", () =>
		{
			var dir = WorkDir( "only-entities-no-class" );
			var input = EntitiesRoom();
			var full = CompileRunner.Run( input, CompilePreset.Final, new CompileOptions { OutputFolder = dir } );
			Require( full.Succeeded, "full run failed: " + full.Error );
			var artefacts = full.ToArtefacts( input );

			var bad = EntitiesRoom();
			bad.Entities.Add( new CompileEntity( 7, "", new Vec3( 0, 0, 32 ) ) );
			var only = CompileRunner.RunOnlyEntities( bad, artefacts, new CompileOptions { OutputFolder = dir } );
			Require( !only.Succeeded, "Only-Entities accepted a classless entity" );
			Require( only.Issues.Any( i => i.Code == "invalid-entity" && i.EntityId == 7 ), "no 'invalid-entity' issue for entity #7" );
			Require( only.FailedStage == CompileStage.EntityLump, $"failed stage {only.FailedStage}, expected EntityLump" );
		} );

		Check( "named preset round-trips by name through the json sidecar", () =>
		{
			var dir = WorkDir( "presets" );
			var store = new CompilePresetStore();
			var preset = new CompileNamedPreset( "final-fast-8", CompilePreset.Final )
			{
				Description = "Final with Fast-VIS and 8x8 lightmaps.",
				FastVis = true,
				LightmapResolutionOverride = 8,
			};
			store.Save( preset );
			store.Save( new CompileNamedPreset( "quick-test", CompilePreset.QuickPlay ) );
			Require( store.Count == 2, $"store count {store.Count}, expected 2" );
			Require( store.List().SequenceEqual( new[] { "final-fast-8", "quick-test" } ), "preset list is not sorted: " + string.Join( ",", store.List() ) );
			var got = store.Get( "FINAL-FAST-8" );
			Require( got.BasePreset == CompilePreset.Final && got.FastVis && got.LightmapResolutionOverride == 8, "lookup lost the overrides (names must be case-insensitive)" );
			Require( got.Description == "Final with Fast-VIS and 8x8 lightmaps.", "lookup lost the description" );
			Throws<KeyNotFoundException>( () => store.Get( "no-such-preset" ), "unknown preset name" );
			Throws<ArgumentException>( () => new CompileNamedPreset( "bad/name", CompilePreset.Final ), "preset name with a slash" );
			Throws<ArgumentException>( () => new CompileNamedPreset( "", CompilePreset.Final ), "empty preset name" );

			var sidecar = Path.Combine( dir, "compile-presets.json" );
			store.SaveToFile( sidecar );
			Require( File.Exists( sidecar ), "no sidecar file written" );
			var loaded = CompilePresetStore.LoadFromFile( sidecar );
			Require( loaded.Count == 2, $"loaded store count {loaded.Count}, expected 2" );
			var reloaded = loaded.Get( "final-fast-8" );
			Require( reloaded.BasePreset == CompilePreset.Final && reloaded.FastVis && reloaded.LightmapResolutionOverride == 8
				&& reloaded.Description == "Final with Fast-VIS and 8x8 lightmaps.", "sidecar round-trip lost preset data" );
			Require( loaded.Remove( "quick-test" ), "remove reported false for a saved preset" );
			Require( loaded.Count == 1 && loaded.List().SequenceEqual( new[] { "final-fast-8" } ), "remove did not drop the preset" );
			Require( !loaded.Remove( "quick-test" ), "second remove reported true" );
			var missing = CompilePresetStore.LoadFromFile( Path.Combine( dir, "does-not-exist.json" ) );
			Require( missing.Count == 0, "missing sidecar must load as an empty store" );
		} );

		Check( "named preset applies its overrides on a run", () =>
		{
			var dir = WorkDir( "preset-run" );
			var store = new CompilePresetStore();
			store.Save( new CompileNamedPreset( "final-fast-8", CompilePreset.Final ) { FastVis = true, LightmapResolutionOverride = 8 } );
			var result = CompileRunner.Run( SealedRoom(), store.Get( "final-fast-8" ), new CompileOptions { OutputFolder = dir } );
			Require( result.Succeeded, "named-preset run failed: " + result.Error );
			Require( result.NamedPreset == "final-fast-8", $"result NamedPreset '{result.NamedPreset}'" );
			Require( result.Preset == CompilePreset.Final, "named preset must report its base preset" );
			Require( result.Pvs is not null && result.Pvs.Quality == VisibilityQuality.Fast, "named FastVis override did not relax the PVS" );
			Require( result.Lightmaps is not null && result.Lightmaps.Resolution == 8, $"named lightmap override gave {result.Lightmaps?.Resolution}, expected 8" );
			Require( result.CompletedStages.Count == 3, "named Final-based preset must run all three stages" );
			Require( result.Log.Any( l => l.Contains( "final-fast-8", StringComparison.Ordinal ) ), "log does not name the preset" );
		} );

		Check( "leak log entry carries the gap coordinates and invalid-brush entry carries the brush id", () =>
		{
			var leaking = LoadFixture( "hammer-compile-native-pipeline", "leaking-room" );
			var result = CompileRunner.Run( leaking, CompilePreset.Final, new CompileOptions { OutputFolder = WorkDir( "log-leak" ) } );
			Require( !result.Succeeded, "leaking map compiled" );
			var leakEntry = result.LogEntries.FirstOrDefault( e => e.Code == "leak" );
			Require( leakEntry is not null, "no 'leak' log entry: " + string.Join( "; ", result.LogEntries.Select( e => e.Code ) ) );
			Require( leakEntry.Severity == CompileSeverity.Error, "leak entry is not an error" );
			Require( leakEntry.SourceRef.HasTarget, "leak entry has no go-to reference" );
			Require( leakEntry.SourceRef.Location is not null, "leak entry carries no coordinates" );
			var gap = leakEntry.SourceRef.Location.Value;
			Require( gap.x >= 128f && gap.x <= 144f && Math.Abs( gap.y ) <= 1f && gap.z >= 0f && gap.z <= 128f,
				$"leak entry coordinates {gap} are not at the gap (x 128..144, y 0, z 0..128)" );
			Require( leakEntry.SourceRef.LeafId is not null, "leak entry carries no leaf id" );
			Require( leakEntry.ToString().Contains( "136", StringComparison.Ordinal ) || leakEntry.ToString().Contains( gap.x.ToString( "0.###", System.Globalization.CultureInfo.InvariantCulture ), StringComparison.Ordinal ),
				"leak entry text does not show the gap coordinates: " + leakEntry );
			Require( result.LogEntries.Count == result.Log.Count + result.Issues.Count,
				$"log entries {result.LogEntries.Count} != log lines {result.Log.Count} + issues {result.Issues.Count}" );

			var bad = SealedRoom();
			bad.Brushes.Add( CompileBrush.Box( "bad", 99, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 0 ) ) );
			var badResult = CompileRunner.Run( bad, CompilePreset.Final, new CompileOptions { OutputFolder = WorkDir( "log-bad-brush" ) } );
			Require( !badResult.Succeeded, "map with a zero-volume brush compiled" );
			var brushEntry = badResult.LogEntries.FirstOrDefault( e => e.Code == "invalid-brush" );
			Require( brushEntry is not null, "no 'invalid-brush' log entry" );
			Require( brushEntry.SourceRef.BrushId == 99, $"invalid-brush entry brush id {brushEntry.SourceRef.BrushId}, expected 99" );

			var noStart = SealedRoom();
			noStart.PlayerStart = null;
			var warnResult = CompileRunner.Run( noStart, CompilePreset.Final, new CompileOptions { OutputFolder = WorkDir( "log-no-start" ) } );
			Require( warnResult.Succeeded, "map without a player start failed: " + warnResult.Error );
			var warnEntry = warnResult.LogEntries.FirstOrDefault( e => e.Code == "missing-player-start" );
			Require( warnEntry is not null && warnEntry.Severity == CompileSeverity.Warning, "no 'missing-player-start' warning entry" );
		} );

		Check( "entity lump text is deterministic and carries classname origin and keyvalues", () =>
		{
			var dirA = WorkDir( "lump-a" );
			var dirB = WorkDir( "lump-b" );
			var a = CompileRunner.Run( EntitiesRoom(), CompilePreset.Final, new CompileOptions { OutputFolder = dirA } );
			var b = CompileRunner.Run( EntitiesRoom(), CompilePreset.Final, new CompileOptions { OutputFolder = dirB } );
			Require( a.Succeeded && b.Succeeded, "runs failed" );
			Require( a.Entities is not null && b.Entities is not null, "no entity lump built" );
			Require( a.Entities.Text == b.Entities.Text, "lump text is not deterministic" );
			Require( a.Entities.Text.StartsWith( "GEPPETTO-ENTITIES 1\ncount 3", StringComparison.Ordinal ), "lump header/count wrong: " + a.Entities.Text.Split( '\n' )[0] );
			Require( a.Entities.Text.Contains( "entity 1 light_spot origin 0 0 96 targetname lamp1", StringComparison.Ordinal ), "lump lost the light_spot line" );
			Require( a.Entities.Text.Contains( "_cone=\"45\"", StringComparison.Ordinal ) && a.Entities.Text.Contains( "_light=\"255 255 255\"", StringComparison.Ordinal ), "lump lost the light keyvalues" );
			Require( a.Entities.Text.Contains( "entity 2 prop_static origin 64 0 32 targetname crate1 model=\"models/crate.mdl\" skin=\"0\"", StringComparison.Ordinal ),
				"lump lost the prop_static line or its keyvalues are not sorted: " + a.Entities.Text );
			var reversed = EntitiesRoom();
			reversed.Entities.Reverse();
			var c = CompileRunner.Run( reversed, CompilePreset.Final, new CompileOptions { OutputFolder = WorkDir( "lump-c" ) } );
			Require( c.Succeeded && c.Entities is not null, "reversed run failed" );
			Require( c.Entities.Text == a.Entities.Text, "lump text depends on input entity order" );
		} );

		Check( "no external process is spawned anywhere in the runner source", () =>
		{
			var text = File.ReadAllText( RunnerSourcePath() );
			foreach ( var banned in new[] { "Process.Start", "ProcessStartInfo", "new Process(", "CreateNoWindow", "cmd.exe", "/bin/sh", "ShellExecute", "UseShellExecute" } )
				Require( !text.Contains( banned, StringComparison.Ordinal ), $"runner source contains '{banned}'" );
			Require( text.Contains( "RunOnlyEntities", StringComparison.Ordinal ), "runner source has no Only-Entities path to check" );
		} );

		Check( "pre-cancelled only-entities cancels with no entities file", () =>
		{
			var dir = WorkDir( "only-entities-cancel" );
			var input = EntitiesRoom();
			var full = CompileRunner.Run( input, CompilePreset.Final, new CompileOptions { OutputFolder = dir } );
			Require( full.Succeeded, "full run failed: " + full.Error );
			var artefacts = full.ToArtefacts( input );
			var cts = new CancellationTokenSource();
			cts.Cancel();
			var only = CompileRunner.RunOnlyEntities( EntitiesRoom(), artefacts,
				new CompileOptions { OutputFolder = dir }, cancel: cts.Token );
			Require( !only.Succeeded && only.Cancelled, "pre-cancelled Only-Entities did not cancel" );
			Require( only.CancelledStage is null, "pre-cancelled run names a stage it never entered" );
			Require( only.Bsp is null && only.Pvs is null && only.Lightmaps is null, "cancelled run claims reused products" );
			Require( only.CompletedStages.Count == 0, "cancelled run completed stages" );
		} );
	}
}
