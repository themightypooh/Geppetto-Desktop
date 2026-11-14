#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

internal static class GpuBakeSuiteTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		// Fixture for godlike-gpu-bake-suite-smart-materials.
		// The GPU path is exercised through a fake backend (the real GL backend
		// is GpuBakeComputeBackend in the Desktop app, verified by the GPU smoke
		// test at the final build, not here); the job logic, the cross-check,
		// per-area rebake and the Smart Materials engine are all CPU-testable.
		//
		// Not yet registered in tests/TestRegistry.cs: that file is
		// coordinator-owned and the final integrator adds the one
		// GpuBakeSuiteTests.Register(registry, settings); line.
		registry.Add( "gpubake", "gpu bake job falls back to cpu with no backend", () =>
		{
			var previous = GpuBake.Provider;
			try
			{
				GpuBake.Provider = null;
				var job = new GpuBake.GpuBakeJob( MakeJob( BakeMaps.Normal | BakeMaps.Height, 256 ), testSize: 256, finalSize: 256 );
				var result = job.TestBake();
				Assert( result.Succeeded, "cpu fallback must still produce a set" );
				Assert( result.Route.Contains( "no gpu backend", StringComparison.Ordinal ), $"route must say why it is cpu, got '{result.Route}'" );
				Assert( result.Set.Normal is not null && result.Set.Normal.FilledCount > 0, "cpu fallback must bake a filled normal map" );
				Assert( result.Set.Height is not null, "cpu fallback must bake the height map" );
			}
			finally { GpuBake.Provider = previous; }
		} );

		registry.Add( "gpubake", "gpu bake matching cpu passes the test-bake", () =>
		{
			var previous = GpuBake.Provider;
			try
			{
				GpuBake.Provider = new FakeBackend( ( job, maps ) =>
				{
					var cpu = job.Run();
					return cpu.Succeeded ? (cpu.Set, null) : (null, cpu.Error ?? "cpu bake failed");
				} );
				var job = new GpuBake.GpuBakeJob( MakeJob( BakeMaps.Normal | BakeMaps.Height, 256 ), testSize: 256, finalSize: 256 );
				var result = job.TestBake();
				Assert( result.TestPassed, $"identical gpu output must pass, route='{result.Route}'" );
				Assert( result.Route.StartsWith( "gpu", StringComparison.Ordinal ), $"route must say gpu, got '{result.Route}'" );
				Assert( result.Deviations.TryGetValue( "normal", out var d ) && d.Mean < 0.01 && d.Max < 0.01,
					$"identical maps must deviate by ~0, got mean {d.Mean} max {d.Max}" );
			}
			finally { GpuBake.Provider = previous; }
		} );

		registry.Add( "gpubake", "gpu bake that diverges falls back to cpu", () =>
		{
			var previous = GpuBake.Provider;
			try
			{
				GpuBake.Provider = new FakeBackend( ( job, maps ) =>
				{
					var cpu = job.Run();
					if ( !cpu.Succeeded || cpu.Set.Normal is null ) return (null, "no normal map to corrupt");
					Array.Clear( cpu.Set.Normal.Rgb, 0, cpu.Set.Normal.Rgb.Length );
					return (cpu.Set, null);
				} );
				var job = new GpuBake.GpuBakeJob( MakeJob( BakeMaps.Normal | BakeMaps.Height, 256 ), testSize: 256, finalSize: 256 );
				var result = job.TestBake();
				Assert( !result.TestPassed, "a black normal map must not pass the cross-check" );
				Assert( result.Route.Contains( "diverged", StringComparison.Ordinal ), $"route must say diverged, got '{result.Route}'" );
				var at = result.Set.Normal!.At( 0, 0 );
				Assert( at.R == 128 && at.G == 128 && at.B == 255,
					$"the fallback set must be the cpu bake, not the corrupt gpu one (texel 0,0 = {at.R},{at.G},{at.B})" );
			}
			finally { GpuBake.Provider = previous; }
		} );

		registry.Add( "gpubake", "gpu job merges cpu maps for unsupported maps", () =>
		{
			var previous = GpuBake.Provider;
			try
			{
				GpuBake.Provider = new FakeBackend( ( job, maps ) =>
				{
					var cpu = job.Run();
					if ( !cpu.Succeeded ) return (null, cpu.Error ?? "cpu bake failed");
					cpu.Set.Height = null;
					return (cpu.Set, null);
				} );
				var job = new GpuBake.GpuBakeJob( MakeJob( BakeMaps.Normal | BakeMaps.Height, 256 ), testSize: 256, finalSize: 256 );
				var result = job.FinalBake();
				Assert( result.Succeeded, "merged final must succeed" );
				Assert( result.Set.Normal is not null && result.Set.Height is not null,
					"the final set must have the gpu normal and the cpu-filled height" );
				Assert( result.Route.Contains( "height filled from cpu", StringComparison.Ordinal ),
					$"route must name the filled map, got '{result.Route}'" );
			}
			finally { GpuBake.Provider = previous; }
		} );

		registry.Add( "gpubake", "per-area rebake reuses unchanged maps", () =>
		{
			var job = new GpuBake.GpuBakeJob( MakeJob( BakeMaps.Normal | BakeMaps.Height, 256 ), testSize: 256, finalSize: 256 );
			var first = job.FinalBake();
			Assert( first.Succeeded, "first bake must succeed" );
			var previous = new BakeJobResult { Set = first.Set, Report = first.Report, Fingerprint = job.Job.Fingerprint(), MapFingerprints = job.Job.MapFingerprints() };
			var reused = job.Run( previous );
			Assert( reused.ReusedPrevious, "an unchanged job must reuse the previous set" );
			Assert( ReferenceEquals( reused.Set, first.Set ), "reuse must be the same set by reference" );

			var flipped = new GpuBake.GpuBakeJob(
				new BakeJob( MakeJob( BakeMaps.Normal | BakeMaps.Height, 256 ).Groups, BakeMaps.Normal | BakeMaps.Height, 256, 256,
					new BakeOptions { FlipGreen = true } ),
				testSize: 256, finalSize: 256 );
			var third = flipped.Run( previous );
			Assert( !third.ReusedPrevious, "flipping green must re-bake" );
			Assert( ReferenceEquals( third.Set.Height, first.Set.Height ),
				"the height map must be reused by reference when only the normal family changed" );
			Assert( !ReferenceEquals( third.Set.Normal, first.Set.Normal ), "the normal map must be re-baked" );
			var differs = false;
			for ( var y = 0; y < 256 && !differs; y++ )
			for ( var x = 0; x < 256; x++ )
				if ( Math.Abs( third.Set.Normal!.At( x, y ).G - first.Set.Normal!.At( x, y ).G ) > 2 ) { differs = true; break; }
			Assert( differs, "the re-baked normal map must actually flip green somewhere on the slope" );
		} );

		registry.Add( "gpubake", "smart material adapts when the baked curvature changes", () =>
		{
			var flat = HandmadeSet( curvatureGrey: 128, aoGrey: 255 );
			var bumped = HandmadeSet( curvatureGrey: 128, aoGrey: 255 );
			bumped.Curvature!.Rgb[(4 * 8 + 4) * 3] = 255;
			bumped.Curvature.Rgb[(4 * 8 + 4) * 3 + 1] = 255;
			bumped.Curvature.Rgb[(4 * 8 + 4) * 3 + 2] = 255;
			var material = SmartMaterials.EdgeWearMetal();
			var flatOut = SmartMaterials.Render( material, flat );
			var bumpedOut = SmartMaterials.Render( material, bumped );
			var flatR = flatOut.Albedo.At( 4, 4 ).R;
			var bumpedR = bumpedOut.Albedo.At( 4, 4 ).R;
			Assert( bumpedR > flatR + 10,
				$"convex curvature must wear the edge brighter (flat R={flatR}, bumped R={bumpedR})" );
		} );

		registry.Add( "gpubake", "smart material library import export round-trips", () =>
		{
			var library = new SmartMaterials.SmartMaterialLibrary( withBuiltins: true );
			var custom = new SmartMaterials.SmartMaterial
			{
				Name = "TestBrass",
				Tags = new[] { "metal", "test" },
				BaseAlbedo = new Vec3( 0.7f, 0.55f, 0.2f ),
				BaseRoughness = 0.4f,
				BaseMetalness = 1f,
				Generators =
				{
					new SmartMaterials.SmartGenerator
					{
						Source = SmartMaterials.SmartSource.Curvature,
						Curve = new SmartMaterials.MaskCurve( (0f, 0f), (0.5f, 0.5f), (1f, 1f) ),
						Tint = new Vec3( 0.9f, 0.8f, 0.4f ),
					},
				},
			};
			library.Add( custom );
			var json = library.ExportJson();
			var (roundTripped, warnings) = SmartMaterials.SmartMaterialLibrary.ImportJson( json );
			Assert( warnings.Count == 0, $"round-trip must not warn: {string.Join( "; ", warnings )}" );
			Assert( roundTripped.Entries.Count == library.Entries.Count,
				$"round-trip must keep every material ({roundTripped.Entries.Count} vs {library.Entries.Count})" );
			var back = roundTripped.Get( "TestBrass" );
			Assert( back is not null, "round-trip must keep the custom material" );
			Assert( back!.Generators.Count == 1 && back.Generators[0].Source == SmartMaterials.SmartSource.Curvature,
				"round-trip must keep the generator" );
			Assert( back.Generators[0].Curve.Points.Length == 3
				&& MathF.Abs( back.Generators[0].Curve.Points[1].Output - 0.5f ) < 1e-6f,
				"round-trip must keep the curve points" );
			Assert( roundTripped.Search( "brass" ).Any( e => e.Name == "TestBrass" ), "search must find the round-tripped material" );
		} );

		registry.Add( "gpubake", "material-id workflow assigns per id", () =>
		{
			var set = HandmadeSet( curvatureGrey: 128, aoGrey: 255 );
			var id = new BakedMap( 8, 8 );
			for ( var i = 0; i < 64; i++ )
			{
				id.Filled[i] = true;
				id.Rgb[i * 3] = 255; id.Rgb[i * 3 + 1] = 0; id.Rgb[i * 3 + 2] = 0;
			}
			id.Rgb[(7 * 8 + 7) * 3] = 0; id.Rgb[(7 * 8 + 7) * 3 + 1] = 0; id.Rgb[(7 * 8 + 7) * 3 + 2] = 255;
			set.MaterialId = id;
			set.IdColors = new Dictionary<int, (byte R, byte G, byte B)> { [1] = (255, 0, 0), [2] = (0, 0, 255) };
			var library = new SmartMaterials.SmartMaterialLibrary( withBuiltins: false );
			library.Add( new SmartMaterials.SmartMaterial { Name = "Red", BaseAlbedo = new Vec3( 1f, 0f, 0f ) } );
			library.Add( new SmartMaterials.SmartMaterial { Name = "Blue", BaseAlbedo = new Vec3( 0f, 0f, 1f ) } );
			var fallback = new SmartMaterials.SmartMaterial { Name = "Fallback", BaseAlbedo = new Vec3( 0f, 1f, 0f ) };
			var output = SmartMaterials.ApplyPerId( set, new Dictionary<int, string> { [1] = "Red", [2] = "Blue" }, library, fallback );
			var first = output.Albedo.At( 0, 0 );
			var last = output.Albedo.At( 7, 7 );
			Assert( first.R > 200 && first.B < 50, $"id 1 texels must render red, got {first.R},{first.G},{first.B}" );
			Assert( last.B > 200 && last.R < 50, $"id 2 texels must render blue, got {last.R},{last.G},{last.B}" );
		} );

		registry.Add( "gpubake", "test-bake then final sizes are honoured", () =>
		{
			var job = new GpuBake.GpuBakeJob( MakeJob( BakeMaps.Normal, 256 ) );
			Assert( job.TestSize == 1024 && job.FinalSize == 4096,
				$"defaults must be 1K test / 4K final, got {job.TestSize} / {job.FinalSize}" );
			var small = new GpuBake.GpuBakeJob( MakeJob( BakeMaps.Normal, 256 ), testSize: 256, finalSize: 256 );
			var result = small.FinalBake();
			Assert( result.Succeeded && result.Set.Normal is not null
				&& result.Set.Normal.Width == 256 && result.Set.Normal.Height == 256,
				"a 256 final must produce 256x256 maps" );
		} );

		registry.Add( "gpubake", "sixteen-bit option flows through the job", () =>
		{
			var job = new GpuBake.GpuBakeJob( MakeJob( BakeMaps.Normal | BakeMaps.Height, 256, sixteen: true ), testSize: 256, finalSize: 256 );
			var result = job.FinalBake();
			Assert( result.Succeeded, "16-bit bake must succeed" );
			Assert( result.Set.Normal!.Rgb16 is not null, "16-bit normal map must carry its 16-bit copy" );
			Assert( result.Set.Height!.Rgb16 is not null, "16-bit height map must carry its 16-bit copy" );
		} );
	}

	static BakeJob MakeJob( BakeMaps maps, int size, bool sixteen = false )
	{
		var low = Primitives.Plane( 2f, 2f, 4, 4 );
		var high = Primitives.Plane( 2f, 2f, 4, 4 );
		for ( var i = 0; i < high.Positions.Count; i++ )
		{
			var p = high.Positions[i];
			high.Positions[i] = new Vec3( p.x, p.y, 0.3f * MathF.Exp( -(p.x * p.x + p.y * p.y) ) );
		}
		return new BakeJob(
			new[] { new BakeGroup { Name = "test", Low = low, High = high, Cage = low } },
			maps, size, size, new BakeOptions { SixteenBit = sixteen } );
	}

	/// <summary>A small hand-built set: flat curvature + open AO, all texels filled, 8x8.</summary>
	static BakeSet HandmadeSet( int curvatureGrey, int aoGrey )
	{
		var normal = new BakedMap( 8, 8 );
		var curvature = new BakedMap( 8, 8 );
		var ao = new BakedMap( 8, 8 );
		for ( var i = 0; i < 64; i++ )
		{
			normal.Filled[i] = curvature.Filled[i] = ao.Filled[i] = true;
			normal.Rgb[i * 3] = 128; normal.Rgb[i * 3 + 1] = 128; normal.Rgb[i * 3 + 2] = 255;
			curvature.Rgb[i * 3] = curvature.Rgb[i * 3 + 1] = curvature.Rgb[i * 3 + 2] = (byte)curvatureGrey;
			ao.Rgb[i * 3] = ao.Rgb[i * 3 + 1] = ao.Rgb[i * 3 + 2] = (byte)aoGrey;
		}
		return new BakeSet { Normal = normal, Curvature = curvature, AmbientOcclusion = ao, CurvatureRange = 1f };
	}

	sealed class FakeBackend : GpuBake.IGpuBakeBackend
	{
		readonly Func<BakeJob, BakeMaps, (BakeSet Set, string Why)> _run;
		public FakeBackend( Func<BakeJob, BakeMaps, (BakeSet, string)> run ) => _run = run;
		public bool TryBake( BakeJob job, BakeMaps maps, out BakeSet set, out string why )
		{
			(set, why) = _run( job, maps );
			return set is not null;
		}
	}

	static void Assert( bool condition, string message )
	{
		if ( !condition ) throw new Exception( message );
	}
}
