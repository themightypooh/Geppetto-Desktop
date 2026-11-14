#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Effigy;
using Marionette.Desktop;
using NormalMapConvention = Marionette.Desktop.NormalMapConvention;

/// <summary>
/// Tests for godlike-engine-export-presets-file-safety: engine export presets
/// and channel packing, batch export fail-safety, autosave + crash recovery,
/// incremental versioned saves + revert, external-reference relinking, and the
/// ship checklist. Registration (coordinator-owned shared entry point):
/// add `EngineExportPresetsTests.Register( registry, settings );` to
/// DesktopTestHost.Run in tests/TestRegistry.cs.
/// </summary>
internal static class EngineExportPresetsTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "engine-export-presets", name, test );

		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new InvalidOperationException( message );
		}

		string CreateTestDir( string subName )
		{
			string dir = Path.Combine( settings.WorkDirectory, "engine-presets-" + Guid.NewGuid().ToString( "N" ), subName );
			Directory.CreateDirectory( dir );
			return dir;
		}

		DocumentSession BoxSession( string dir, string fileName = "hero.effigy", float sizeX = 15f )
		{
			string path = Path.Combine( dir, fileName );
			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = sizeX } } );
			studio.Rebuild();
			return DocumentSession.CreateEffigy( path, studio );
		}

		static float BoxSizeX( DocumentSession session )
		{
			var studio = session.Studio ?? throw new InvalidOperationException( "Session has no studio." );
			return ( (PrimitiveFeature)studio.Features[0] ).SizeX.Value;
		}

		static void EditBoxSizeX( DocumentSession session, float next )
		{
			var studio = session.Studio ?? throw new InvalidOperationException( "Session has no studio." );
			var feature = (PrimitiveFeature)studio.Features[0];
			float previous = feature.SizeX.Value;
			session.History.Execute( new DelegateEditCommand( "edit size",
				() => feature.SizeX.Value = next,
				() => feature.SizeX.Value = previous ) );
		}

		// 2x2 RGBA map where every pixel is (r, g, b, a).
		static byte[] FlatMap( byte r, byte g, byte b, byte a ) => new[]
		{
			r, g, b, a, r, g, b, a,
			r, g, b, a, r, g, b, a
		};

		static TextureMapSet FullMaps() => new(
			2, 2,
			Albedo: FlatMap( 10, 20, 30, 255 ),
			Normal: FlatMap( 128, 128, 255, 255 ),
			Roughness: FlatMap( 100, 0, 0, 255 ),
			Metallic: FlatMap( 50, 0, 0, 255 ),
			AmbientOcclusion: FlatMap( 200, 0, 0, 255 ) );

		(byte R, byte G, byte B, byte A) Pixel0( PackedMap packed )
		{
			var (rgba, width, height) = PngReader.Read( packed.PngBytes );
			Assert( width == 2 && height == 2, $"Packed map should decode as 2x2, got {width}x{height}." );
			return (rgba[0], rgba[1], rgba[2], rgba[3]);
		}

		// 1. every preset definition is well formed
		Check( "all preset definitions validate", () =>
		{
			Assert( EngineExportPresets.All.Count == 8, $"Expected 8 presets, got {EngineExportPresets.All.Count}." );
			foreach ( var preset in EngineExportPresets.All )
			{
				var errors = EngineExportPresets.ValidatePreset( preset );
				Assert( errors.Count == 0, $"Preset '{preset.Id}' failed validation: {string.Join( "; ", errors )}" );
			}
		} );

		// 2. preset lookup by id, unknown id is null
		Check( "preset lookup by id", () =>
		{
			Assert( EngineExportPresets.Get( "unreal-5" )?.Target == EngineTarget.Unreal5, "unreal-5 lookup failed." );
			Assert( EngineExportPresets.Get( "UNREAL-5" ) != null, "Lookup should be case-insensitive." );
			Assert( EngineExportPresets.Get( "no-such-preset" ) == null, "Unknown id should return null." );
			Assert( EngineExportPresets.All.All( p => !p.DracoSupported ), "No preset may claim Draco: Desktop has no encoder." );
		} );

		// 3. Unreal ORM packs R=AO G=Roughness B=Metallic A=opaque
		Check( "unreal orm packs occlusion roughness metallic", () =>
		{
			var preset = EngineExportPresets.Get( "unreal-5" )!;
			var pack = EngineExportPresets.Pack( preset, FullMaps(), NormalMapConvention.OpenGL );
			Assert( pack.Success, $"Pack failed: {string.Join( "; ", pack.Errors )}" );
			var orm = pack.PackedMaps.First( m => m.Spec.Name == "ORM" );
			var pixel = Pixel0( orm );
			Assert( pixel.R == 200 && pixel.G == 100 && pixel.B == 50 && pixel.A == 255, $"ORM pixel should be (200,100,50,255), got {pixel}." );
		} );

		// 4. DirectX normal flips green, OpenGL passes it through
		Check( "directx normal flips green, opengl passes through", () =>
		{
			var unreal = EngineExportPresets.Get( "unreal-5" )!;
			var godot = EngineExportPresets.Get( "godot-4" )!;
			var maps = FullMaps();
			var unrealPack = EngineExportPresets.Pack( unreal, maps, NormalMapConvention.OpenGL );
			var godotPack = EngineExportPresets.Pack( godot, maps, NormalMapConvention.OpenGL );
			Assert( unrealPack.Success && godotPack.Success, "Packs should succeed." );
			var unrealNormal = Pixel0( unrealPack.PackedMaps.First( m => m.Spec.Name == "Normal" ) );
			var godotNormal = Pixel0( godotPack.PackedMaps.First( m => m.Spec.Name == "Normal" ) );
			Assert( unrealNormal.R == 128 && unrealNormal.G == 127 && unrealNormal.B == 255 && unrealNormal.A == 255, $"DirectX normal pixel should be (128,127,255,255), got {unrealNormal}." );
			Assert( godotNormal.R == 128 && godotNormal.G == 128 && godotNormal.B == 255 && godotNormal.A == 255, $"OpenGL normal pixel should be (128,128,255,255), got {godotNormal}." );
		} );

		// 5. Unity URP metallic-smoothness: metallic in R, inverted roughness in A
		Check( "unity urp packs metallic smoothness", () =>
		{
			var preset = EngineExportPresets.Get( "unity-urp" )!;
			var pack = EngineExportPresets.Pack( preset, FullMaps(), NormalMapConvention.OpenGL );
			Assert( pack.Success, $"Pack failed: {string.Join( "; ", pack.Errors )}" );
			var pixel = Pixel0( pack.PackedMaps.First( m => m.Spec.Name == "MetallicSmoothness" ) );
			Assert( pixel.R == 50 && pixel.G == 0 && pixel.B == 0 && pixel.A == 155, $"Metallic-smoothness pixel should be (50,0,0,155), got {pixel}." );
		} );

		// 6. Unity HDRP mask map: metallic / occlusion / detail 0 / smoothness
		Check( "unity hdrp packs mask map", () =>
		{
			var preset = EngineExportPresets.Get( "unity-hdrp" )!;
			var pack = EngineExportPresets.Pack( preset, FullMaps(), NormalMapConvention.OpenGL );
			Assert( pack.Success, $"Pack failed: {string.Join( "; ", pack.Errors )}" );
			var pixel = Pixel0( pack.PackedMaps.First( m => m.Spec.Name == "MaskMap" ) );
			Assert( pixel.R == 50 && pixel.G == 200 && pixel.B == 0 && pixel.A == 155, $"Mask-map pixel should be (50,200,0,155), got {pixel}." );
		} );

		// 7. missing metallic fails the pack with a readable error; missing AO defaults to white
		Check( "missing metallic fails, missing ao defaults white", () =>
		{
			var preset = EngineExportPresets.Get( "unreal-5" )!;
			var noMetal = new TextureMapSet( 2, 2,
				Albedo: FlatMap( 10, 20, 30, 255 ),
				Normal: FlatMap( 128, 128, 255, 255 ),
				Roughness: FlatMap( 100, 0, 0, 255 ),
				AmbientOcclusion: FlatMap( 200, 0, 0, 255 ) );
			var failed = EngineExportPresets.Pack( preset, noMetal, NormalMapConvention.OpenGL );
			Assert( !failed.Success, "Pack without metallic should fail." );
			Assert( failed.Errors.Any( e => e.Contains( "Metallic", StringComparison.Ordinal ) ), "Error should name the missing metallic map." );
			Assert( failed.PackedMaps.All( m => m.Spec.Name != "ORM" ), "ORM must not be produced without metallic." );
			Assert( failed.PackedMaps.Any( m => m.Spec.Name == "Normal" ), "Normal should still pack without metallic." );

			var noAo = new TextureMapSet( 2, 2,
				Albedo: FlatMap( 10, 20, 30, 255 ),
				Normal: FlatMap( 128, 128, 255, 255 ),
				Roughness: FlatMap( 100, 0, 0, 255 ),
				Metallic: FlatMap( 50, 0, 0, 255 ) );
			var warned = EngineExportPresets.Pack( preset, noAo, NormalMapConvention.OpenGL );
			Assert( warned.Success, $"Pack without AO should succeed with a warning: {string.Join( "; ", warned.Errors )}" );
			Assert( warned.Warnings.Count > 0, "Missing AO should warn." );
			var pixel = Pixel0( warned.PackedMaps.First( m => m.Spec.Name == "ORM" ) );
			Assert( pixel.R == 255, $"ORM red should default to white without AO, got {pixel.R}." );
		} );

		// 8. dimension mismatch is refused, not silently packed
		Check( "dimension mismatch is refused", () =>
		{
			var preset = EngineExportPresets.Get( "unreal-5" )!;
			var bad = new TextureMapSet( 2, 2,
				Normal: FlatMap( 128, 128, 255, 255 ),
				Roughness: new byte[3 * 4],
				Metallic: FlatMap( 50, 0, 0, 255 ),
				AmbientOcclusion: FlatMap( 200, 0, 0, 255 ) );
			var pack = EngineExportPresets.Pack( preset, bad, NormalMapConvention.OpenGL );
			Assert( !pack.Success, "Mismatched map sizes should fail." );
			Assert( pack.Errors.Any( e => e.Contains( "Roughness", StringComparison.Ordinal ) ), "Error should name the mismatched map." );
		} );

		// 9. naming template and sanitisation
		Check( "naming template and sanitisation", () =>
		{
			var preset = EngineExportPresets.Get( "unreal-5" )!;
			var orm = preset.PackedMaps.First( m => m.Name == "ORM" );
			Assert( EngineExportPresets.FileNameFor( preset, "Hero", orm ) == "Hero_ORM.png",
				$"Expected Hero_ORM.png, got {EngineExportPresets.FileNameFor( preset, "Hero", orm )}." );
			Assert( EngineExportPresets.FileNameFor( preset, "Hero Asset!", orm ) == "Hero_Asset__ORM.png",
				$"Expected Hero_Asset__ORM.png, got {EngineExportPresets.FileNameFor( preset, "Hero Asset!", orm )}." );
			Assert( EngineExportPresets.ModelFileNameFor( preset, "Hero" ) == "Hero.glb", "Unreal model file should be Hero.glb." );
			Assert( EngineExportPresets.ModelFileNameFor( EngineExportPresets.Get( "unity-urp" )!, "Hero" ) == "Hero.fbx", "Unity model file should be Hero.fbx." );
			Assert( EngineExportPresets.SanitizeFileName( "" ) == "Asset", "Empty name should become Asset." );
			Assert( EngineExportPresets.SanitizeFileName( "a/b\\c:d" ) == "a_b_c_d", "Invalid file-name characters should become underscores." );
		} );

		// 10. unsupported presets refuse to pack with a readable reason
		Check( "unsupported presets refuse to pack", () =>
		{
			var usd = EngineExportPresets.Get( "usd" )!;
			var print = EngineExportPresets.Get( "print-3d" )!;
			Assert( !usd.Supported && !print.Supported, "USD and print presets must report unsupported." );
			var usdPack = EngineExportPresets.Pack( usd, FullMaps(), NormalMapConvention.OpenGL );
			Assert( !usdPack.Success, "USD pack must fail." );
			Assert( usdPack.Errors.Any( e => e.Contains( "USD", StringComparison.Ordinal ) ), "USD error should name USD." );
			var printPack = EngineExportPresets.Pack( print, FullMaps(), NormalMapConvention.OpenGL );
			Assert( !printPack.Success, "Print pack must fail." );
			Assert( printPack.Errors.Any( e => e.Contains( "STL", StringComparison.Ordinal ) ), "Print error should name STL/3MF." );
		} );

		// 11. batch export produces every preset's model + packed maps
		Check( "batch export produces all presets", () =>
		{
			string dir = CreateTestDir( "batch-ok" );
			var session = BoxSession( dir );
			string outDir = Path.Combine( dir, "out" );
			var service = new ExportBatchService();
			var presets = new[]
			{
				EngineExportPresets.Get( "unreal-5" )!,
				EngineExportPresets.Get( "godot-4" )!,
				EngineExportPresets.Get( "gltf-2" )!
			};
			var result = service.ExportAll( session, presets, outDir, FullMaps(), NormalMapConvention.OpenGL, new FakeExportRunner() );
			Assert( result.OverallSuccess, $"Batch should succeed: {string.Join( "; ", result.Results.SelectMany( r => new[] { r.ErrorMessage ?? "" } ) )}" );
			foreach ( var preset in presets )
			{
				string presetDir = Path.Combine( outDir, preset.Id );
				Assert( File.Exists( Path.Combine( presetDir, "hero.glb" ) ), $"Missing model file for {preset.Id}." );
				Assert( File.Exists( Path.Combine( presetDir, "hero_ORM.png" ) ), $"Missing ORM map for {preset.Id}." );
				Assert( File.Exists( Path.Combine( presetDir, "hero_Normal.png" ) ), $"Missing normal map for {preset.Id}." );
			}
		} );

		// 12. a failing preset leaves earlier good files untouched and cleans staging
		Check( "failing preset leaves good files untouched", () =>
		{
			string dir = CreateTestDir( "batch-fail" );
			var session = BoxSession( dir );
			string outDir = Path.Combine( dir, "out" );
			string godotDir = Path.Combine( outDir, "godot-4" );
			Directory.CreateDirectory( godotDir );
			string goodFile = Path.Combine( godotDir, "hero.glb" );
			File.WriteAllText( goodFile, "old-good" );
			var service = new ExportBatchService();
			var presets = new[]
			{
				EngineExportPresets.Get( "unreal-5" )!,
				EngineExportPresets.Get( "godot-4" )!
			};
			var runner = new FakeExportRunner { FailPresetId = "godot-4" };
			var result = service.ExportAll( session, presets, outDir, FullMaps(), NormalMapConvention.OpenGL, runner );
			Assert( !result.OverallSuccess, "Batch with a failing preset must not report overall success." );
			var unreal = result.Results.First( r => r.PresetId == "unreal-5" );
			var godot = result.Results.First( r => r.PresetId == "godot-4" );
			Assert( unreal.Success, "Unreal preset should still succeed." );
			Assert( !godot.Success, "Godot preset should fail." );
			Assert( godot.ErrorMessage != null && godot.ErrorMessage.Contains( "boom", StringComparison.Ordinal ), "Godot error should carry the runner's readable message." );
			Assert( File.ReadAllText( goodFile ) == "old-good", "The previous good godot file must be byte-identical after the failure." );
			string stagingRoot = Path.Combine( outDir, ".staging" );
			if ( Directory.Exists( stagingRoot ) )
			{
				var leftovers = Directory.GetDirectories( stagingRoot ).Where( d => d.Contains( "godot-4", StringComparison.Ordinal ) ).ToList();
				Assert( leftovers.Count == 0, "Failed preset staging must be cleaned up." );
			}
		} );

		// 13. unsupported presets in a batch fail without touching disk
		Check( "batch refuses unsupported presets without touching disk", () =>
		{
			string dir = CreateTestDir( "batch-unsupported" );
			var session = BoxSession( dir );
			string outDir = Path.Combine( dir, "out" );
			var service = new ExportBatchService();
			var result = service.ExportAll( session, new[] { EngineExportPresets.Get( "usd" )! }, outDir, FullMaps(), NormalMapConvention.OpenGL, new FakeExportRunner() );
			Assert( !result.OverallSuccess, "Batch with only USD must fail." );
			Assert( result.Results[0].ErrorMessage != null && result.Results[0].ErrorMessage.Contains( "USD", StringComparison.Ordinal ), "USD error should name USD." );
			Assert( !Directory.Exists( Path.Combine( outDir, "usd" ) ), "No output directory should be created for an unsupported preset." );
		} );

		// 14. autosave + crash recovery restores the edited document
		Check( "autosave and crash recovery restores the doc", () =>
		{
			string dir = CreateTestDir( "recovery" );
			var session = BoxSession( dir );
			EditBoxSizeX( session, 25f );
			Assert( session.IsDirty, "Session must be dirty after the edit." );
			string recoveryDir = Path.Combine( dir, "recoveries" );
			var saveService = new DocumentSaveService();
			var scheduler = new AutosaveScheduler( saveService, recoveryDir, fileSystem: PhysicalFileSystem.Instance );
			var autosaved = scheduler.TriggerAutosaveAsync( session ).GetAwaiter().GetResult();
			Assert( autosaved.IsSuccess, $"Autosave failed: {autosaved.Message}" );
			var safety = new FileSafetyService( saveService, PhysicalFileSystem.Instance, scheduler );
			var plan = safety.PlanRestore( recoveryDir, session.DocumentId );
			Assert( plan.Found && plan.Metadata != null, "Recovery plan should find the autosaved snapshot." );
			var (_, payload) = safety.ReadRestorePayload( recoveryDir, plan.Metadata.SnapshotId );
			var restored = StudioDocument.Read( payload );
			restored.Rebuild();
			Assert( Math.Abs( ( (PrimitiveFeature)restored.Features[0] ).SizeX.Value - 25f ) < 0.001f, "Recovered payload should carry the edited SizeX=25." );
			string restoredPath = Path.Combine( dir, "restored.effigy" );
			var restoredSession = safety.RestoreSnapshotToSession( recoveryDir, plan.Metadata.SnapshotId, restoredPath );
			Assert( Math.Abs( BoxSizeX( restoredSession ) - 25f ) < 0.001f, "Restored session should carry SizeX=25." );
			var emptyPlan = safety.PlanRestore( Path.Combine( dir, "no-such-dir" ), session.DocumentId );
			Assert( !emptyPlan.Found, "An empty recovery directory should plan nothing." );
		} );

		// 15. incremental versions save and revert
		Check( "incremental versions save and revert", () =>
		{
			string dir = CreateTestDir( "versions" );
			var session = BoxSession( dir );
			var saveService = new DocumentSaveService();
			var saved = saveService.Save( session );
			Assert( saved.IsSuccess, $"Live save failed: {saved.ErrorMessage}" );
			var safety = new FileSafetyService( saveService );
			EditBoxSizeX( session, 25f );
			var v1 = safety.SaveVersion( session, dir );
			Assert( v1.Success && v1.VersionNumber == 1, $"v001 failed: {v1.ErrorMessage}" );
			Assert( v1.Path != null && File.Exists( v1.Path ), "v001 file must exist." );
			EditBoxSizeX( session, 35f );
			var v2 = safety.SaveVersion( session, dir );
			Assert( v2.Success && v2.VersionNumber == 2, $"v002 failed: {v2.ErrorMessage}" );
			var versions = safety.ListVersions( dir, "hero" );
			Assert( versions.Count == 2, $"Expected 2 versions, got {versions.Count}." );
			Assert( File.Exists( Path.Combine( dir, "hero.versions.json" ) ), "Version manifest must exist." );
			var reverted = safety.RevertToVersion( dir, "hero", 1 );
			Assert( reverted.Success, $"Revert failed: {reverted.ErrorMessage}" );
			Assert( reverted.BackupPath != null && File.Exists( reverted.BackupPath ), "Revert must keep a .pre-revert.bak backup." );
			var live = StudioDocument.ReadFile( Path.Combine( dir, "hero.effigy" ) );
			live.Rebuild();
			Assert( Math.Abs( ( (PrimitiveFeature)live.Features[0] ).SizeX.Value - 25f ) < 0.001f, "Live file should read back SizeX=25 after revert to v001." );
			var missing = safety.RevertToVersion( dir, "hero", 9 );
			Assert( !missing.Success, "Reverting to a version that was never saved must fail." );
		} );

		// 16. version pruning keeps only the newest
		Check( "version pruning keeps only the newest", () =>
		{
			string dir = CreateTestDir( "prune" );
			var session = BoxSession( dir );
			var safety = new FileSafetyService( new DocumentSaveService() );
			var v1 = safety.SaveVersion( session, dir, maxVersions: 2 );
			var v2 = safety.SaveVersion( session, dir, maxVersions: 2 );
			var v3 = safety.SaveVersion( session, dir, maxVersions: 2 );
			Assert( v1.Success && v2.Success && v3.Success, "Version saves should succeed." );
			var versions = safety.ListVersions( dir, "hero" );
			Assert( versions.Count == 2, $"Expected 2 retained versions, got {versions.Count}." );
			Assert( versions[0].Number == 2 && versions[1].Number == 3, "Only v002 and v003 should remain." );
			Assert( !File.Exists( Path.Combine( dir, "hero_v001.effigy" ) ), "v001 file should be pruned." );
		} );

		// 17. relink finds moved files, refuses ambiguity, needs opt-in outside the root
		Check( "relink finds moved files, refuses ambiguity", () =>
		{
			string dir = CreateTestDir( "relink" );
			string texturesDir = Path.Combine( dir, "textures" );
			Directory.CreateDirectory( texturesDir );
			var resolver = new AssetPathResolver( dir );
			var safety = new FileSafetyService();
			// moved to the project top level: reference still points at textures/
			File.WriteAllText( Path.Combine( dir, "hero.png" ), "pixels" );
			var found = safety.RelinkMissing( resolver,
				new[] { new AssetReference( "tex1", "textures/hero.png" ) },
				Array.Empty<string>() );
			Assert( found[0].Outcome == RelinkOutcome.Relinked, $"Expected relinked, got {found[0].Outcome}: {found[0].Reason}" );
			Assert( found[0].NewReference?.Path == "hero.png", $"Relinked reference should be hero.png, got {found[0].NewReference?.Path}." );
			// unchanged when it already resolves
			var unchanged = safety.RelinkMissing( resolver,
				new[] { new AssetReference( "tex1", "textures/hero.png" ) },
				Array.Empty<string>() );
			Assert( unchanged[0].Outcome == RelinkOutcome.Unchanged, "An existing reference should be unchanged." );
			// ambiguous across two search roots
			string rootA = Path.Combine( dir, "a" );
			string rootB = Path.Combine( dir, "b" );
			Directory.CreateDirectory( rootA );
			Directory.CreateDirectory( rootB );
			File.WriteAllText( Path.Combine( rootA, "dup.png" ), "a" );
			File.WriteAllText( Path.Combine( rootB, "dup.png" ), "b" );
			var ambiguous = safety.RelinkMissing( resolver,
				new[] { new AssetReference( "tex2", "dup.png" ) },
				new[] { rootA, rootB } );
			Assert( ambiguous[0].Outcome == RelinkOutcome.Ambiguous, $"Expected ambiguous, got {ambiguous[0].Outcome}." );
			Assert( ambiguous[0].Candidates.Count == 2, "Ambiguity should list both candidates." );
			// still missing when nothing matches
			var missing = safety.RelinkMissing( resolver,
				new[] { new AssetReference( "tex3", "gone.png" ) },
				new[] { rootA, rootB } );
			Assert( missing[0].Outcome == RelinkOutcome.StillMissing, "A file found nowhere should stay missing." );
			// external candidate needs opt-in
			string outside = Path.Combine( dir, "outside" );
			Directory.CreateDirectory( outside );
			File.WriteAllText( Path.Combine( outside, "ext.png" ), "x" );
			var noOptIn = safety.RelinkMissing( resolver,
				new[] { new AssetReference( "tex4", "ext.png" ) },
				new[] { outside }, allowExternal: false );
			Assert( noOptIn[0].Outcome == RelinkOutcome.StillMissing, "An outside candidate without opt-in should stay missing." );
			var optIn = safety.RelinkMissing( resolver,
				new[] { new AssetReference( "tex4", "ext.png" ) },
				new[] { outside }, allowExternal: true );
			Assert( optIn[0].Outcome == RelinkOutcome.Relinked, "An outside candidate with opt-in should relink." );
			Assert( optIn[0].NewReference?.Kind == AssetReferenceKind.ExternalReadOnly, "An outside relink must be an explicit external reference." );
		} );

		// 18. ship checklist passes a good asset
		Check( "ship checklist passes a good asset", () =>
		{
			string dir = CreateTestDir( "ship-good" );
			var session = BoxSession( dir );
			var studio = session.Studio ?? throw new InvalidOperationException( "Session has no studio." );
			var preset = EngineExportPresets.Get( "gltf-2" )!;
			var sets = new[]
			{
				new TextureSetInfo( "body", 2048, 2048, new[]
				{
					SourceChannel.Albedo, SourceChannel.Normal, SourceChannel.Roughness,
					SourceChannel.Metallic, SourceChannel.AmbientOcclusion
				} )
			};
			var input = ShipChecklist.FromStudio( studio, preset, sets );
			Assert( input.TriangleCount == 12, $"Box should have 12 triangles, got {input.TriangleCount}." );
			double xExtent = input.BoundsMax.x - input.BoundsMin.x;
			Assert( Math.Abs( xExtent - 15 ) < 0.001, $"Box X extent should be 15 in, got {xExtent}." );
			var result = ShipChecklist.Run( input );
			Assert( result.Passed, $"Good asset should pass: {string.Join( "; ", result.Errors.Select( c => c.Name + ": " + c.Detail ) )}" );
		} );

		// 19. ship checklist fails a bad asset
		Check( "ship checklist fails a bad asset", () =>
		{
			var preset = EngineExportPresets.Get( "gltf-2" )!;
			var input = new ShipChecklistInput(
				0, 0, 0, false, false, false, false, 0, 0, false,
				new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 0 ), "Z", "inches",
				Array.Empty<TextureSetInfo>(), preset, 0 );
			var result = ShipChecklist.Run( input );
			Assert( !result.Passed, "An empty asset must fail." );
			Assert( result.Errors.Any( c => c.Name == "geometry present" ), "Geometry check should fail." );
			Assert( result.Errors.Any( c => c.Name == "triangle budget" ), "Triangle budget should fail on zero triangles." );
			Assert( result.Errors.Any( c => c.Name == "bounds sane" ), "Bounds check should fail on a zero-size asset." );
			Assert( result.Errors.Any( c => c.Name.StartsWith( "map completeness", StringComparison.Ordinal ) ), "Map completeness should fail with no texture sets." );
		} );

		// 20. print preset demands watertight; viewer settings validate
		Check( "print preset demands watertight and viewer settings validate", () =>
		{
			var print = EngineExportPresets.Get( "print-3d" )!;
			var input = new ShipChecklistInput(
				6, 12, 8, false, true, false, false, 0, 0, false,
				new Vec3( 0, 0, 0 ), new Vec3( 10, 10, 10 ), "Z", "inches",
				Array.Empty<TextureSetInfo>(), print, 0 );
			var result = ShipChecklist.Run( input );
			var watertight = result.Checks.First( c => c.Name == "watertight" );
			Assert( !watertight.Passed && watertight.Severity == ShipCheckSeverity.Error, "A holed mesh must fail the print watertight check as an error." );
			var holes = result.Checks.First( c => c.Name == "mesh holes" );
			Assert( !holes.Passed && holes.Severity == ShipCheckSeverity.Error, "Holes must be errors for the print preset." );

			var defaults = new FinishViewerSettings();
			Assert( defaults.Validate().Any( e => e.Contains( "HDRI", StringComparison.Ordinal ) ), "Default viewer settings should report the missing HDRI." );
			var withHdri = new FinishViewerSettings( HdriPath: "studio.hdr" );
			Assert( withHdri.Validate().Count == 0, "Viewer settings with an HDRI should validate." );
			var badLod = new FinishViewerSettings( HdriPath: "studio.hdr", LodIndex: 4 );
			Assert( badLod.Validate().Count > 0, "LOD index 4 should not validate." );
		} );
	}
}

internal sealed class FakeExportRunner : IExportRunner
{
	public string FailPresetId = "";

	public ExportResult Run( EngineExportPreset preset, DocumentSession session, string stagingDirectory )
	{
		if ( string.Equals( preset.Id, FailPresetId, StringComparison.Ordinal ) )
			return new ExportResult( false, ErrorMessage: "boom: fake runner failure" );
		string fileName = EngineExportPresets.ModelFileNameFor( preset, "hero" );
		string path = Path.Combine( stagingDirectory, fileName );
		Directory.CreateDirectory( stagingDirectory );
		File.WriteAllText( path, "fake-model:" + preset.Id );
		return new ExportResult( true, DestinationPath: path, GeneratedFiles: new[] { path } );
	}
}
