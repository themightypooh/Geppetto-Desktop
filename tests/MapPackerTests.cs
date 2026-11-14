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
/// hammer-compile-packing-cubemap-build: release packing (asset bundle) +
/// cubemap build stage for the native compile runner.
///
/// Covers MapPacker (GatherUsedAssets / Pack / WriteText / BuildCubemaps)
/// and the CubemapBuild + Pack stages wired into CompileRunner.Run:
/// a fixture map packs exactly its used assets (unused registry entries
/// excluded), a missing asset is a named issue and the pack fails loudly
/// (no package file), and the cubemap stage produces 6 faces per probe.
/// The FromDocument adapter (CompileInput.FromDocument) is covered here
/// too, now that MapDocument has landed — brushes, probes and asset refs
/// round-trip from a document built in code.
///
/// NOT registered in TestRegistry.cs yet — the final integrator adds the
/// one line; temp-register only to run this group, then revert.
/// </summary>
internal static class MapPackerTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "mappacker", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
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
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-compile-packing-cubemap-build" );
					if ( File.Exists( Path.Combine( candidate, "packed-room.json" ) ) ) return candidate;
					dir = dir.Parent;
				}
			}
			throw new Exception( "mappacker fixture dir not found (packed-room.json missing above cwd / BaseDirectory)" );
		}

		CompileInput LoadFixture()
		{
			var path = Path.Combine( FixtureDir(), "packed-room.json" );
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var root = doc.RootElement;
			var input = new CompileInput( root.GetProperty( "name" ).GetString() ?? "packed-room" );
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
			foreach ( var probe in root.GetProperty( "probes" ).EnumerateArray() )
			{
				var p = probe.GetProperty( "position" );
				input.Probes.Add( new CubemapProbe(
					probe.GetProperty( "name" ).GetString() ?? "env_cubemap",
					new Vec3( p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle() ),
					probe.TryGetProperty( "resolution", out var res ) ? res.GetInt32() : CubemapBake.DefaultResolution ) );
			}
			foreach ( var model in root.GetProperty( "models" ).EnumerateArray() )
				input.Models.Add( model.GetString() ?? "" );
			foreach ( var sound in root.GetProperty( "sounds" ).EnumerateArray() )
				input.Sounds.Add( sound.GetString() ?? "" );
			foreach ( var particle in root.GetProperty( "particles" ).EnumerateArray() )
				input.Particles.Add( particle.GetString() ?? "" );
			return input;
		}

		AssetRegistries SeedRegistries( string projectRoot, bool includeUnused )
		{
			var all = new AssetRegistries( projectRoot );
			Require( all.Models.Register( new ModelEntry { Name = "Crate", Path = "models/props/crate.mdl" } ), "crate did not register" );
			Require( all.Sounds.Register( new SoundEntry { Name = "Beep", Path = "sounds/beep.wav", Volume = 1f } ), "beep did not register" );
			Require( all.Particles.Register( new ParticleEntry { SystemName = "spark_burst", DefinitionPath = "particles/spark.json" } ), "spark did not register" );
			if ( includeUnused )
			{
				Require( all.Models.Register( new ModelEntry { Name = "Unused", Path = "models/props/unused.mdl" } ), "unused model did not register" );
				Require( all.Sounds.Register( new SoundEntry { Name = "UnusedSound", Path = "sounds/unused.wav", Volume = 1f } ), "unused sound did not register" );
			}
			return all;
		}

		string NewWorkDir( string name )
		{
			var dir = Path.Combine( settings.WorkDirectory, "mappacker", name );
			if ( Directory.Exists( dir ) ) Directory.Delete( dir, recursive: true );
			Directory.CreateDirectory( dir );
			return dir;
		}

		CompileResult Run( CompileInput input, CompilePreset preset, string dir, AssetRegistries? registries )
		{
			var options = new CompileOptions { OutputFolder = dir, AssetRegistries = registries };
			return CompileRunner.Run( input, preset, options );
		}

		Check( "fixture map packs exactly its used assets", () =>
		{
			var input = LoadFixture();
			var regs = SeedRegistries( FixtureDir(), includeUnused: true );
			var gather = MapPacker.GatherUsedAssets( input, regs );
			Require( gather.Succeeded, "gather failed: " + gather.Error );
			Require( gather.ModelCount == 1, $"expected 1 model, got {gather.ModelCount}" );
			Require( gather.MaterialCount == 2, $"expected 2 materials, got {gather.MaterialCount}" );
			Require( gather.SoundCount == 1, $"expected 1 sound, got {gather.SoundCount}" );
			Require( gather.ParticleCount == 1, $"expected 1 particle, got {gather.ParticleCount}" );
			Require( gather.TotalCount == 5, $"expected 5 assets, got {gather.TotalCount}" );
			Require( gather.Summary == "5 assets (1 models, 2 materials, 1 sounds, 1 particles)", "summary wrong: " + gather.Summary );
			var manifest = MapPacker.WriteText( input.Name, gather.Assets );
			Require( manifest.StartsWith( "GEPPETTO-MAPPACK 1\n", StringComparison.Ordinal ), "manifest header wrong" );
			Require( manifest.Contains( "models/props/crate.mdl" ), "manifest misses the used model" );
			Require( manifest.Contains( "brick/brickwall" ), "manifest misses the wall material" );
			Require( manifest.Contains( "concrete/concretefloor" ), "manifest misses the floor material" );
			Require( manifest.Contains( "sounds/beep.wav" ), "manifest misses the used sound" );
			Require( manifest.Contains( "spark_burst" ), "manifest misses the used particle" );
			Require( !manifest.Contains( "unused" ), "unused registry entry leaked into the pack" );
		} );

		Check( "missing model is a named issue and the pack fails loudly", () =>
		{
			var input = LoadFixture();
			input.Models.Add( "models/props/ghost.mdl" );
			var regs = SeedRegistries( FixtureDir(), includeUnused: false );
			var gather = MapPacker.GatherUsedAssets( input, regs );
			Require( !gather.Succeeded, "gather with an unregistered model succeeded" );
			Require( gather.Issues.Any( i => i.Code == AssetRegistryIssue.CodeUnknownModel && i.Severity == CompileSeverity.Error ),
				"no UnknownModel error; got: " + string.Join( "; ", gather.Issues ) );
			Require( gather.Error is not null && gather.Error.Contains( "ghost.mdl" ), "error does not name the missing model: " + gather.Error );
			var dir = NewWorkDir( "missing-model" );
			var pack = MapPacker.Pack( input, regs, Path.Combine( dir, "packed.mappack" ) );
			Require( !pack.Succeeded, "pack with a missing model succeeded" );
			Require( pack.PackagePath is not null && !File.Exists( pack.PackagePath ), "failed pack wrote a package file" );
		} );

		Check( "registered model missing on disk is a named missing-model issue", () =>
		{
			var input = LoadFixture();
			input.Models.Add( "models/props/nofile.mdl" );
			var regs = SeedRegistries( FixtureDir(), includeUnused: false );
			Require( regs.Models.Register( new ModelEntry { Name = "Nofile", Path = "models/props/nofile.mdl" } ), "nofile model did not register" );
			var gather = MapPacker.GatherUsedAssets( input, regs );
			Require( !gather.Succeeded, "gather with a missing-on-disk model succeeded" );
			Require( gather.Issues.Any( i => i.Code == AssetRegistryIssue.CodeMissingModel && i.Severity == CompileSeverity.Error ),
				"no MISSING_MODEL error; got: " + string.Join( "; ", gather.Issues ) );
			var dir = NewWorkDir( "missing-on-disk" );
			var pack = MapPacker.Pack( input, regs, Path.Combine( dir, "packed.mappack" ) );
			Require( !pack.Succeeded, "pack with a missing-on-disk model succeeded" );
			Require( pack.PackagePath is not null && !File.Exists( pack.PackagePath ), "failed pack wrote a package file" );
		} );

		Check( "missing sound and particle are named issues", () =>
		{
			var input = LoadFixture();
			input.Sounds.Add( "sounds/nope.wav" );
			input.Particles.Add( "ghost_fx" );
			var regs = SeedRegistries( FixtureDir(), includeUnused: false );
			var gather = MapPacker.GatherUsedAssets( input, regs );
			Require( !gather.Succeeded, "gather with missing sound + particle succeeded" );
			Require( gather.Issues.Any( i => i.Code == AssetRegistryIssue.CodeUnknownSound ), "no UnknownSound issue; got: " + string.Join( "; ", gather.Issues ) );
			Require( gather.Issues.Any( i => i.Code == AssetRegistryIssue.CodeUnknownParticle ), "no UnknownParticle issue; got: " + string.Join( "; ", gather.Issues ) );
			Require( gather.Error is not null && gather.Error.Contains( "Missing 2 asset(s)" ), "error does not count both missing assets: " + gather.Error );
		} );

		Check( "nodraw, tool and default materials are not packed", () =>
		{
			var input = new CompileInput( "mat-check" );
			input.Brushes.Add( CompileBrush.Box( "nodraw-box", 0, new Vec3( -16, -16, 0 ), new Vec3( 16, 16, 32 ), "tools/toolsnodraw" ) );
			input.Brushes.Add( CompileBrush.Box( "default-box", 1, new Vec3( -16, -16, 64 ), new Vec3( 16, 16, 96 ), "default" ) );
			input.Brushes.Add( CompileBrush.Box( "brick-box", 2, new Vec3( -16, -16, 128 ), new Vec3( 16, 16, 160 ), "brick/brickwall" ) );
			var regs = SeedRegistries( FixtureDir(), includeUnused: false );
			var gather = MapPacker.GatherUsedAssets( input, regs );
			Require( gather.Succeeded, "gather failed: " + gather.Error );
			Require( gather.MaterialCount == 1, $"expected 1 material, got {gather.MaterialCount}" );
			Require( gather.Assets[0].Key == "brick/brickwall", "packed material is not the brick wall: " + gather.Assets[0].Key );
		} );

		Check( "buildcubemaps wrapper bakes 6 faces per probe", () =>
		{
			var bake = MapPacker.BuildCubemaps( LoadFixture() );
			Require( bake.Succeeded, "cubemap bake failed: " + bake.Error );
			Require( bake.Set is not null, "bake succeeded with no set" );
			Require( bake.Set!.ProbeCount == 1, $"expected 1 probe, got {bake.Set.ProbeCount}" );
			Require( bake.Set.FaceCount == 6, $"expected 6 faces, got {bake.Set.FaceCount}" );
			Require( bake.Set.FacesFor( "room_probe" ).Count == 6, "FacesFor(room_probe) is not 6" );
		} );

		Check( "cubemap stage produces 6 faces per probe", () =>
		{
			var dir = NewWorkDir( "cubemap-stage" );
			var result = Run( LoadFixture(), CompilePreset.Final, dir, null );
			Require( result.Succeeded, "compile failed: " + result.Error );
			Require( result.Cubemaps is not null, "Final compile with a probe produced no cubemaps" );
			Require( result.Cubemaps!.ProbeCount == 1, $"expected 1 probe, got {result.Cubemaps.ProbeCount}" );
			Require( result.Cubemaps.FaceCount == 6, $"expected 6 faces, got {result.Cubemaps.FaceCount}" );
			Require( result.Cubemaps.FacesFor( "room_probe" ).Count == 6, "FacesFor(room_probe) is not 6" );
			Require( result.CompletedStages.Contains( CompileStage.CubemapBuild ), "CubemapBuild is not in the completed stages: " + string.Join( ",", result.CompletedStages ) );
			var cubemapPath = Path.Combine( dir, "packed-room.cubemap" );
			Require( result.OutputFiles.Contains( cubemapPath ), ".cubemap is not in the output files" );
			Require( File.Exists( cubemapPath ), ".cubemap file was not written" );
			var text = File.ReadAllText( cubemapPath );
			Require( text.StartsWith( "GEPPETTO-CUBEMAP 1\n", StringComparison.Ordinal ), "cubemap header wrong" );
			Require( text.Contains( "room_probe" ), "cubemap text does not name the probe" );
		} );

		Check( "quickplay skips cubemap and pack", () =>
		{
			var dir = NewWorkDir( "quickplay-skips" );
			var result = Run( LoadFixture(), CompilePreset.QuickPlay, dir, SeedRegistries( FixtureDir(), includeUnused: true ) );
			Require( result.Succeeded, "compile failed: " + result.Error );
			Require( result.Cubemaps is null, "QuickPlay produced cubemaps" );
			Require( result.Pack is null, "QuickPlay produced a pack" );
			Require( result.CompletedStages.SequenceEqual( new[] { CompileStage.BspBuild } ), "QuickPlay stages wrong: " + string.Join( ",", result.CompletedStages ) );
			Require( !File.Exists( Path.Combine( dir, "packed-room.cubemap" ) ), "QuickPlay wrote a .cubemap" );
			Require( !File.Exists( Path.Combine( dir, "packed-room.mappack" ) ), "QuickPlay wrote a .mappack" );
		} );

		Check( "final with registries packs used assets only", () =>
		{
			var dir = NewWorkDir( "final-pack" );
			var result = Run( LoadFixture(), CompilePreset.Final, dir, SeedRegistries( FixtureDir(), includeUnused: true ) );
			Require( result.Succeeded, "compile failed: " + result.Error );
			Require( result.Pack is not null, "Final compile with registries produced no pack" );
			Require( result.Pack!.Succeeded, "pack failed: " + result.Pack.Error );
			Require( result.CompletedStages.SequenceEqual( new[] { CompileStage.BspBuild, CompileStage.Visibility, CompileStage.LightmapBake, CompileStage.CubemapBuild, CompileStage.Pack } ),
				"Final stages wrong: " + string.Join( ",", result.CompletedStages ) );
			var packPath = Path.Combine( dir, "packed-room.mappack" );
			Require( result.OutputFiles.Contains( packPath ), ".mappack is not in the output files" );
			Require( result.Pack.PackagePath == packPath, "pack path is not the written .mappack: " + result.Pack.PackagePath );
			Require( File.Exists( packPath ), ".mappack file was not written" );
			var manifest = File.ReadAllText( packPath );
			Require( manifest.Contains( "models/props/crate.mdl" ), "package misses the used model" );
			Require( manifest.Contains( "sounds/beep.wav" ), "package misses the used sound" );
			Require( manifest.Contains( "spark_burst" ), "package misses the used particle" );
			Require( !manifest.Contains( "unused" ), "unused registry entry leaked into the package" );
		} );

		Check( "pack manifest embeds cubemap inventory", () =>
		{
			var dir = NewWorkDir( "pack-cubemap" );
			var result = Run( LoadFixture(), CompilePreset.Final, dir, SeedRegistries( FixtureDir(), includeUnused: false ) );
			Require( result.Succeeded, "compile failed: " + result.Error );
			Require( result.Pack is not null && result.Pack!.Manifest is not null, "no pack manifest" );
			var manifest = result.Pack.Manifest!;
			Require( manifest.StartsWith( "GEPPETTO-MAPPACK 1\n", StringComparison.Ordinal ), "manifest header wrong" );
			Require( manifest.Contains( "cubemaps 1" ), "manifest has no cubemap count" );
			Require( manifest.Contains( "cubemap room_probe resolution 32 faces 6 envmap env_cubemap:room_probe" ), "manifest cubemap line wrong: " + manifest );
			Require( manifest.Contains( "cubemap-face room_probe PositiveX mean " ), "manifest has no per-face cubemap lines" );
		} );

		Check( "from-document adapter carries brushes, probes and asset refs", () =>
		{
			var doc = MapDocument.Create( "doc-room" );
			doc.AddBrush( MapBrushData.Box( "b0", "floor", new Vec3( -144, -144, -16 ), new Vec3( 144, 144, 0 ), "concrete/concretefloor" ) );
			doc.AddBrush( MapBrushData.Box( "b1", "ceiling", new Vec3( -144, -144, 128 ), new Vec3( 144, 144, 144 ), "brick/brickwall" ) );
			doc.AddBrush( MapBrushData.Box( "b2", "wall_west", new Vec3( -144, -144, 0 ), new Vec3( -128, 144, 128 ), "brick/brickwall" ) );
			doc.AddBrush( MapBrushData.Box( "b3", "wall_east", new Vec3( 128, -144, 0 ), new Vec3( 144, 144, 128 ), "brick/brickwall" ) );
			doc.AddBrush( MapBrushData.Box( "b4", "wall_south", new Vec3( -128, -144, 0 ), new Vec3( 128, -128, 128 ), "brick/brickwall" ) );
			doc.AddBrush( MapBrushData.Box( "b5", "wall_north", new Vec3( -128, 128, 0 ), new Vec3( 128, 144, 128 ), "brick/brickwall" ) );
			doc.AddEntity( new MapEntityData { Id = "e0", ClassName = "info_player_start", Origin = new Vec3( 0, 0, 32 ) } );
			doc.AddEntity( new MapEntityData { Id = "e1", ClassName = "light", Origin = new Vec3( 0, 0, 96 ),
				KeyValues = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase ) { ["light"] = "255 255 255 200" } } );
			doc.AddEntity( new MapEntityData { Id = "e2", ClassName = "prop_static", Origin = new Vec3( 0, 0, 32 ),
				KeyValues = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase ) { ["model"] = "models/props/crate.mdl" } } );
			doc.AddEntity( new MapEntityData { Id = "e3", ClassName = "env_cubemap", Origin = new Vec3( 0, 0, 64 ),
				KeyValues = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase ) { ["targetname"] = "room_probe", ["cubemapsize"] = "32" } } );
			doc.AddEntity( new MapEntityData { Id = "e4", ClassName = "ambient_generic", Origin = new Vec3( 0, 0, 32 ),
				KeyValues = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase ) { ["message"] = "sounds/beep.wav" } } );
			doc.AddEntity( new MapEntityData { Id = "e5", ClassName = "info_particle_system", Origin = new Vec3( 0, 0, 32 ),
				KeyValues = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase ) { ["effect_name"] = "spark_burst" } } );
			var input = CompileInput.FromDocument( doc );
			Require( input.Brushes.Count == 6, $"expected 6 brushes, got {input.Brushes.Count}" );
			Require( input.Probes.Count == 1, $"expected 1 probe, got {input.Probes.Count}" );
			Require( input.Probes[0].Name == "room_probe", "probe name wrong: " + input.Probes[0].Name );
			Require( input.Probes[0].Resolution == 32, $"probe resolution wrong: {input.Probes[0].Resolution}" );
			Require( Near( input.Probes[0].Position, new Vec3( 0, 0, 64 ) ), "probe position wrong" );
			Require( input.Models.Contains( "models/props/crate.mdl" ), "model ref missing from input" );
			Require( input.Sounds.Contains( "sounds/beep.wav" ), "sound ref missing from input" );
			Require( input.Particles.Contains( "spark_burst" ), "particle ref missing from input" );
			Require( input.PlayerStart.HasValue && Near( input.PlayerStart.Value, new Vec3( 0, 0, 32 ) ), "player start missing or wrong" );
			Require( input.Lights.Count == 1, $"expected 1 light, got {input.Lights.Count}" );
			Require( NearF( input.Lights[0].Intensity, 200f ), $"light intensity wrong: {input.Lights[0].Intensity}" );
			var regs = SeedRegistries( FixtureDir(), includeUnused: true );
			var gather = MapPacker.GatherUsedAssets( doc, regs );
			Require( gather.Succeeded, "document gather failed: " + gather.Error );
			Require( gather.TotalCount == 5, $"expected 5 assets from the document, got {gather.TotalCount}" );
			var dir = NewWorkDir( "doc-pack" );
			var pack = MapPacker.Pack( doc, regs, Path.Combine( dir, "doc-room.mappack" ) );
			Require( pack.Succeeded, "document pack failed: " + pack.Error );
			Require( pack.PackagePath is not null && File.Exists( pack.PackagePath ), "document pack wrote no file" );
			Require( File.ReadAllText( pack.PackagePath ).StartsWith( "GEPPETTO-MAPPACK 1\n", StringComparison.Ordinal ), "document pack header wrong" );
		} );

		Check( "duplicate probe names fail the cubemap stage loudly", () =>
		{
			var input = LoadFixture();
			input.Probes.Add( new CubemapProbe( "room_probe", new Vec3( 64, 0, 64 ), 32 ) );
			var dir = NewWorkDir( "duplicate-probe" );
			var result = Run( input, CompilePreset.Final, dir, null );
			Require( !result.Succeeded, "compile with duplicate probes succeeded" );
			Require( result.FailedStage == CompileStage.CubemapBuild, "failed stage is not CubemapBuild: " + result.FailedStage );
			Require( result.Error is not null && result.Error.Contains( "Duplicate" ), "error does not name the duplicate: " + result.Error );
			Require( result.Issues.Any( i => i.Code == CubemapBake.IssueCodes.DuplicateProbe ), "no duplicate-probe issue; got: " + string.Join( "; ", result.Issues ) );
			Require( !File.Exists( Path.Combine( dir, "packed-room.cubemap" ) ), "failed cubemap stage wrote a .cubemap" );
		} );
	}
}
