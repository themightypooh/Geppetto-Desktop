#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Effigy;
using Effigy.Map;

// Hammer Desktop P3: native radiosity-quality bake, reached from the
// progressive bake — LightmapBakeQuality.BakeRadiosity(job, settings) runs
// RadiosityBake on the job's faces + lights; PackAtlas / LightPropVertices /
// ToneMap / VerifyGpuTrust / BakeGpuRadiosity / CompareToProgressive are the
// other quality entry points. These tests exercise the bake THROUGH the job
// (the acceptance shape: two-room bounce, AO corner darkening, HDR sun,
// prop lighting, atlas improvements, GPU 1K trust rule), not the engine
// directly — the engine's own analytic tests live in RadiosityBakeTests
// ("radiositybake"). Fixtures are the same analytic two-room scene:
// tests/fixtures/hammer/hammer-radiosity-quality-bake/two-room.json.
//
// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned
// and the final integrator adds the one RadiosityQualityIntegrationTests.
// Register(registry, settings); line. The group is "radiosityquality".
internal static class RadiosityQualityIntegrationTests
{
	static void Check( bool ok, string message ) { if ( !ok ) throw new Exception( message ); }
	static bool Near( float a, float b, float eps = 1e-4f ) => MathF.Abs( a - b ) <= eps;
	static bool Near( Vec3 a, Vec3 b, float eps = 1e-4f ) =>
		Near( a.x, b.x, eps ) && Near( a.y, b.y, eps ) && Near( a.z, b.z, eps );

	/// <summary>A 16x16 floor quad centred on (128,128,0), normal +Z, lightmap scale 16: exactly one luxel, at its centre.</summary>
	static LiveFace SmallFloor( int id = 0 ) => new( id, new[]
	{
		new Vec3( 120f, 120f, 0f ), new Vec3( 136f, 120f, 0f ),
		new Vec3( 136f, 136f, 0f ), new Vec3( 120f, 136f, 0f ),
	} );

	/// <summary>A 256x256 floor quad, z=0, normal +Z.</summary>
	static LiveFace BigFloor( int id = 0 ) => new( id, new[]
	{
		new Vec3( 0f, 0f, 0f ), new Vec3( 256f, 0f, 0f ),
		new Vec3( 256f, 256f, 0f ), new Vec3( 0f, 256f, 0f ),
	} );

	/// <summary>Six INWARD faces of an axis box (a room interior); ids base..base+5: floor, ceiling, west, east, south, north.</summary>
	static IEnumerable<LiveFace> InwardBoxFaces( int @base, Vec3 min, Vec3 size, float lightmapScale = 16f )
	{
		var (x0, y0, z0) = (min.x, min.y, min.z);
		var (x1, y1, z1) = (min.x + size.x, min.y + size.y, min.z + size.z);
		LiveFace Face( int id, Vec3 a, Vec3 b, Vec3 c, Vec3 d ) =>
			new( id, new[] { a, b, c, d },
				new FaceMaterial( "concrete/wall", new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 0f, lightmapScale, 0 ) ), Vec3.One );
		yield return Face( @base + 0, new Vec3( x0, y0, z0 ), new Vec3( x1, y0, z0 ), new Vec3( x1, y1, z0 ), new Vec3( x0, y1, z0 ) );
		yield return Face( @base + 1, new Vec3( x0, y0, z1 ), new Vec3( x0, y1, z1 ), new Vec3( x1, y1, z1 ), new Vec3( x1, y0, z1 ) );
		yield return Face( @base + 2, new Vec3( x0, y0, z0 ), new Vec3( x0, y1, z0 ), new Vec3( x0, y1, z1 ), new Vec3( x0, y0, z1 ) );
		yield return Face( @base + 3, new Vec3( x1, y0, z0 ), new Vec3( x1, y0, z1 ), new Vec3( x1, y1, z1 ), new Vec3( x1, y1, z0 ) );
		yield return Face( @base + 4, new Vec3( x0, y0, z0 ), new Vec3( x0, y0, z1 ), new Vec3( x1, y0, z1 ), new Vec3( x1, y0, z0 ) );
		yield return Face( @base + 5, new Vec3( x0, y1, z0 ), new Vec3( x1, y1, z0 ), new Vec3( x1, y1, z1 ), new Vec3( x0, y1, z1 ) );
	}

	static string FindFixture( string name )
	{
		var rel = Path.Combine( "hammer-radiosity-quality-bake", name );
		var candidates = new[]
		{
			Path.GetFullPath( Path.Combine( "tests", "fixtures", "hammer", rel ) ),
			Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "fixtures", "hammer", rel ) ),
		};
		foreach ( var candidate in candidates )
			if ( File.Exists( candidate ) ) return candidate;
		var dir = new DirectoryInfo( Directory.GetCurrentDirectory() );
		for ( var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent )
		{
			var probe = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", rel );
			if ( File.Exists( probe ) ) return probe;
		}
		throw new Exception( $"Fixture not found: {rel} (searched from {Directory.GetCurrentDirectory()})." );
	}

	static Vec3 ReadVec3( JsonArray pair )
	{
		if ( pair.Count != 3 ) throw new Exception( $"Expected a [x, y, z] triple, got {pair.Count} values." );
		return new Vec3( (float)pair[0]!.GetValue<double>(), (float)pair[1]!.GetValue<double>(), (float)pair[2]!.GetValue<double>() );
	}

	static (List<LiveFace> faces, List<LiveLight> lights) LoadScene( string name )
	{
		var json = JsonNode.Parse( File.ReadAllText( FindFixture( name ) ) )!.AsObject();
		var faces = new List<LiveFace>();
		foreach ( var node in json["faces"]!.AsArray() )
		{
			var f = node!.AsObject();
			var verts = f["vertices"]!.AsArray().Select( v => ReadVec3( v!.AsArray() ) ).ToArray();
			var uv = f["lightmapScale"] is null ? FaceUv.Default : new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 0f, (float)f["lightmapScale"]!.GetValue<double>(), 0 );
			var material = new FaceMaterial( f["material"]!.GetValue<string>(), uv );
			var albedo = f["albedo"] is JsonArray a ? ReadVec3( a ) : Vec3.One;
			faces.Add( new LiveFace( f["id"]!.GetValue<int>(), verts, material, albedo ) );
		}
		var lights = new List<LiveLight>();
		foreach ( var node in json["lights"]!.AsArray() )
		{
			var l = node!.AsObject();
			var id = l["id"]!.GetValue<int>();
			var color = ReadVec3( l["color"]!.AsArray() );
			var intensity = (float)l["intensity"]!.GetValue<double>();
			switch ( l["kind"]!.GetValue<string>() )
			{
				case "point":
					lights.Add( LiveLight.Point( id, ReadVec3( l["position"]!.AsArray() ), color, intensity,
						l["range"] is null ? 0f : (float)l["range"]!.GetValue<double>() ) );
					break;
				case "spot":
					lights.Add( LiveLight.Spot( id, ReadVec3( l["position"]!.AsArray() ), ReadVec3( l["direction"]!.AsArray() ), color, intensity,
						(float)l["innerCone"]!.GetValue<double>(), (float)l["outerCone"]!.GetValue<double>() ) );
					break;
				case "sun":
					lights.Add( LiveLight.Sun( id, ReadVec3( l["direction"]!.AsArray() ), color, intensity ) );
					break;
				case "environment":
					lights.Add( LiveLight.Environment( id, color, intensity ) );
					break;
				default: throw new Exception( $"{name}: unknown light kind '{l["kind"]}'." );
			}
		}
		return (faces, lights);
	}

	// --- Fake GPU backends (the trust-rule tests through the job) ---

	sealed class ExactGpuBackend : IRadiosityGpuBackend
	{
		public bool TryBake( IReadOnlyList<LiveFace> faces, IReadOnlyList<LiveLight> lights,
			RadiositySettings settings, out RadiosityResult result, out string why )
		{
			why = null!;
			result = RadiosityBake.Bake( faces, lights, settings );
			return result.Succeeded;
		}
	}

	sealed class DivergingGpuBackend : IRadiosityGpuBackend
	{
		public bool TryBake( IReadOnlyList<LiveFace> faces, IReadOnlyList<LiveLight> lights,
			RadiositySettings settings, out RadiosityResult result, out string why )
		{
			why = null!;
			var cpu = RadiosityBake.Bake( faces, lights, settings );
			var scaledFaces = cpu.Faces.Select( f => new RadiosityFaceResult( f.FaceId, f.Rows, f.Cols, f.LuxelSize,
				f.Luxels.Select( l => l * 2f ).ToList(), f.DirectLuxels.Select( l => l * 2f ).ToList(),
				f.IndirectLuxels.Select( l => l * 2f ).ToList(), f.AoFactors.ToList(), f.SkippedNoDraw ) ).ToList();
			result = new RadiosityResult( scaledFaces, cpu.PatchSamples, cpu.Quality, cpu.BouncesRun,
				cpu.Converged, cpu.ResidualEnergy, cpu.InvalidationKey, cpu.Warnings, "gpu", false, null );
			return true;
		}
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "radiosityquality", "quality bake through the job: two-room unlit room gets bounced light", () =>
		{
			var (faces, lights) = LoadScene( "two-room.json" );
			var job = new LightmapBakeJob( faces, lights );
			var quality = RadiositySettings.Preset( RadiosityQuality.Standard );
			quality.AoRays = 0;
			var result = LightmapBakeQuality.BakeRadiosity( job, quality );
			Check( result.Succeeded, $"bake failed: {result.Error}" );
			Check( result.BouncesRun == 3, $"3 bounces expected, ran {result.BouncesRun}" );
			Check( result.Route == "cpu", $"cpu route expected, got {result.Route}" );
			var floorA = result.Face( 0 )!;
			var floorB = result.Face( 10 )!;
			foreach ( var (row, col) in new[] { (0, 0), (0, 1), (1, 1) } )
			{
				Check( Near( floorB.DirectAt( row, col ), Vec3.Zero, 1e-4f ),
					$"room B floor luxel ({row},{col}) must have zero direct: {floorB.DirectAt( row, col )}" );
				Check( floorB.IndirectAt( row, col ).x > 0.01f,
					$"room B floor luxel ({row},{col}) must have bounced light: indirect {floorB.IndirectAt( row, col )}" );
			}
			Check( floorB.DirectAt( 1, 0 ).x > 0.05f, $"doorway luxel (1,0) direct: {floorB.DirectAt( 1, 0 )}" );
			Check( floorA.Average.x > floorB.Average.x * 2f,
				$"lit room {floorA.Average} must be brighter than unlit room {floorB.Average}" );
		} );

		registry.Add( "radiosityquality", "AO through the job darkens corners more than the centre", () =>
		{
			var faces = InwardBoxFaces( 0, Vec3.Zero, new Vec3( 128f, 128f, 128f ), 32f ).ToList();
			var lights = new[]
			{
				LiveLight.Point( 0, new Vec3( 64f, 64f, 96f ), Vec3.One, 2f ),
				LiveLight.Environment( 1, new Vec3( 0.5f, 0.5f, 0.5f ), 1f ),
			};
			var job = new LightmapBakeJob( faces, lights );
			var noAo = LightmapBakeQuality.BakeRadiosity( job,
				new RadiositySettings { Bounces = 1, AoRays = 0 } );
			var withAo = LightmapBakeQuality.BakeRadiosity( job,
				new RadiositySettings { Bounces = 1, AoRays = 32, AoDistance = 128f, AoStrength = 0.8f } );
			Check( noAo.Succeeded && withAo.Succeeded, "both bakes must succeed" );
			var floorNoAo = noAo.Face( 0 )!;
			var floorAo = withAo.Face( 0 )!;
			var cornerNoAo = floorNoAo.LuxelAt( 0, 0 ).x;
			var centreNoAo = floorNoAo.LuxelAt( 1, 1 ).x;
			var cornerAo = floorAo.LuxelAt( 0, 0 ).x;
			var centreAo = floorAo.LuxelAt( 1, 1 ).x;
			Check( cornerAo < cornerNoAo, $"AO must darken the corner ({cornerAo:0.###} vs {cornerNoAo:0.###})" );
			Check( cornerAo / centreAo < cornerNoAo / centreNoAo,
				$"AO must darken the corner MORE than the centre (ratio {cornerAo / centreAo:0.###} vs {cornerNoAo / centreNoAo:0.###})" );
			Check( floorAo.MeanAoFactor < 1f, $"room mean AO must be below 1: {floorAo.MeanAoFactor}" );
		} );

		registry.Add( "radiosityquality", "HDR through the job: sun luxels exceed the LDR range, tone map stays in range", () =>
		{
			var face = BigFloor();
			var sun = LiveLight.Sun( 0, new Vec3( 0f, 0f, -1f ), Vec3.One, 4f );
			var job = new LightmapBakeJob( new[] { face }, new[] { sun } );
			var result = LightmapBakeQuality.BakeRadiosity( job,
				new RadiositySettings { Bounces = 0, AoRays = 0 } );
			Check( result.Succeeded, $"bake failed: {result.Error}" );
			Check( result.MaxLuxel.x > 1f, $"HDR: max luxel {result.MaxLuxel} must exceed the LDR range (1.0)" );
			Check( Near( result.MaxLuxel, new Vec3( 4f, 4f, 4f ), 1e-3f ), $"max luxel: {result.MaxLuxel}" );
			Check( Near( LightmapBakeQuality.ToneMap( new Vec3( 4f, 4f, 4f ) ), new Vec3( 0.8f, 0.8f, 0.8f ) ),
				$"Reinhard(4.0) is 0.8: {LightmapBakeQuality.ToneMap( new Vec3( 4f, 4f, 4f ) )}" );
			Check( Near( LightmapBakeQuality.ToneMap( Vec3.Zero ), Vec3.Zero ), "tone map of black is black" );
		} );

		registry.Add( "radiosityquality", "prop vertices through the job get bounced light in the unlit room", () =>
		{
			var (faces, lights) = LoadScene( "two-room.json" );
			var job = new LightmapBakeJob( faces, lights );
			var quality = RadiositySettings.Preset( RadiosityQuality.Standard );
			quality.AoRays = 0;
			quality.PropSampleRadius = 128f;
			var baked = LightmapBakeQuality.BakeRadiosity( job, quality );
			Check( baked.Succeeded, $"bake failed: {baked.Error}" );
			var darkVertex = new RadiosityPropVertex( new Vec3( 160f, 32f, 8f ), new Vec3( 0f, 0f, 1f ) );
			var litVertex = new RadiosityPropVertex( new Vec3( 32f, 32f, 8f ), new Vec3( 0f, 0f, 1f ) );
			var lit = LightmapBakeQuality.LightPropVertices( baked, new[] { darkVertex, litVertex }, lights, faces, quality );
			Check( lit[0].x > 0.005f, $"shadowed prop vertex must still get bounced light: {lit[0]}" );
			Check( lit[1].x > lit[0].x * 3f, $"room A vertex {lit[1]} must be much brighter than room B vertex {lit[0]}" );
		} );

		registry.Add( "radiosityquality", "atlas pack through the job: gutters, bounds, and a spill to more pages", () =>
		{
			var (faces, lights) = LoadScene( "two-room.json" );
			var job = new LightmapBakeJob( faces, lights );
			var quality = RadiositySettings.Preset( RadiosityQuality.Standard );
			quality.AoRays = 0;
			var baked = LightmapBakeQuality.BakeRadiosity( job, quality );
			Check( baked.Succeeded, $"bake failed: {baked.Error}" );
			var wide = LightmapBakeQuality.PackAtlas( baked, 64, 2 );
			var narrow = LightmapBakeQuality.PackAtlas( baked, 16, 2 );
			Check( wide.Placements.Count == baked.Faces.Count( f => !f.SkippedNoDraw ),
				$"every baked face gets a chart: {wide.Placements.Count} placements" );
			Check( narrow.PageCount > wide.PageCount,
				$"a smaller atlas must spill to more pages ({narrow.PageCount} vs {wide.PageCount})" );
			foreach ( var packed in new[] { wide, narrow } )
			{
				Check( packed.Utilization > 0f && packed.Utilization <= 1f, $"utilization: {packed.Utilization}" );
				var placed = packed.Placements.ToList();
				for ( var i = 0; i < placed.Count; i++ )
				{
					Check( placed[i].X + placed[i].Cols <= packed.AtlasSize - 2 &&
						placed[i].Y + placed[i].Rows <= packed.AtlasSize - 2,
						$"chart {placed[i].FaceId} leaves the atlas bounds" );
					for ( var j = i + 1; j < placed.Count; j++ )
					{
						var a = placed[i];
						var b = placed[j];
						if ( a.Page != b.Page ) continue;
						var overlapX = a.X < b.X + b.Cols + 2 && b.X < a.X + a.Cols + 2;
						var overlapY = a.Y < b.Y + b.Rows + 2 && b.Y < a.Y + a.Rows + 2;
						Check( !(overlapX && overlapY), $"charts {a.FaceId} and {b.FaceId} overlap (gutter included)" );
					}
				}
			}
		} );

		registry.Add( "radiosityquality", "GPU trust through the job: exact backend passes, diverging fails, no trust is refused", () =>
		{
			var face = SmallFloor();
			var light = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f );
			var job = new LightmapBakeJob( new[] { face }, new[] { light } );
			var settings = new RadiositySettings { Bounces = 0, AoRays = 0 };
			var exact = LightmapBakeQuality.VerifyGpuTrust( new ExactGpuBackend(), job, settings );
			Check( exact.Passed, $"exact backend must pass trust: {exact.Reason}" );
			Check( exact.WorstDeviation == 0f, $"exact backend deviation must be 0: {exact.WorstDeviation}" );
			var diverging = LightmapBakeQuality.VerifyGpuTrust( new DivergingGpuBackend(), job, settings );
			Check( !diverging.Passed, "a backend returning 2x luxels must fail trust" );
			Check( diverging.Reason.Contains( "GpuDiverged" ), $"trust failure must name GpuDiverged: {diverging.Reason}" );
			var saved = RadiosityBake.GpuBackend;
			try
			{
				RadiosityBake.GpuBackend = new ExactGpuBackend();
				var gpu = LightmapBakeQuality.BakeGpuRadiosity( job, settings, exact );
				Check( gpu.Succeeded, $"GPU bake failed: {gpu.Error}" );
				Check( gpu.Route == "gpu", $"gpu route expected, got {gpu.Route}" );
				Check( Near( gpu.Face( 0 )!.Average, new Vec3( 1f, 1f, 1f ), 1e-3f ), $"gpu luxel: {gpu.Face( 0 )!.Average}" );
				var refused = LightmapBakeQuality.BakeGpuRadiosity( job, settings, null! );
				Check( !refused.Succeeded, "GPU bake without trust must fail" );
				Check( refused.Error is not null && refused.Error.Contains( "GpuTrustNotVerified" ),
					$"refusal must name GpuTrustNotVerified: {refused.Error}" );
			}
			finally { RadiosityBake.GpuBackend = saved; }
		} );

		registry.Add( "radiosityquality", "direct-only quality bake agrees with the progressive refined bake (cross-check)", () =>
		{
			var nearCentre = SmallFloor( 0 );
			var nearCorner = new LiveFace( 1, new[]
			{
				new Vec3( 8f, 8f, 0f ), new Vec3( 24f, 8f, 0f ),
				new Vec3( 24f, 24f, 0f ), new Vec3( 8f, 24f, 0f ),
			} );
			var faces = new[] { nearCentre, nearCorner };
			var light = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f );
			var options = new LightmapBakeOptions { BouncePasses = 0 };
			var job = new LightmapBakeJob( faces, new[] { light }, options );
			var progressive = job.Refine();
			Check( progressive.Succeeded, $"progressive bake failed: {progressive.Error}" );
			var quality = LightmapBakeQuality.BakeRadiosity( job,
				new RadiositySettings { Bounces = 0, AoRays = 0 } );
			Check( quality.Succeeded, $"quality bake failed: {quality.Error}" );
			var deviations = LightmapBakeQuality.CompareToProgressive( quality, progressive );
			Check( deviations.Count == 2, $"both faces must be comparable, got {deviations.Count}" );
			foreach ( var (id, deviation) in deviations )
			{
				var qAvg = quality.Face( int.Parse( id ) )!.Average;
				var pAvg = progressive.Face( int.Parse( id ) )!.Average;
				var worst = 0f;
				var q = new[] { qAvg.x, qAvg.y, qAvg.z };
				var p = new[] { pAvg.x, pAvg.y, pAvg.z };
				for ( var i = 0; i < 3; i++ )
					worst = MathF.Max( worst, MathF.Abs( q[i] - p[i] ) / MathF.Max( MathF.Abs( p[i] ), 0.01f ) );
				Check( worst <= 0.05f, $"face {id}: quality {qAvg} vs progressive {pAvg} (rel diff {worst:0.###})" );
				Check( deviation.Max <= 0.05, $"face {id}: CompareToProgressive max deviation {deviation.Max:0.###}" );
			}
			// Same scene, direct-only, through the two-room fixture: the doorway luxel
			// agrees too (both bakes shadow-test the same way).
			var (roomFaces, roomLights) = LoadScene( "two-room.json" );
			var roomJob = new LightmapBakeJob( roomFaces, roomLights, new LightmapBakeOptions { BouncePasses = 0 } );
			var roomProgressive = roomJob.Refine();
			var roomQuality = LightmapBakeQuality.BakeRadiosity( roomJob,
				new RadiositySettings { Bounces = 0, AoRays = 0 } );
			Check( roomProgressive.Succeeded && roomQuality.Succeeded, "both two-room direct bakes must succeed" );
			var roomDeviations = LightmapBakeQuality.CompareToProgressive( roomQuality, roomProgressive );
			Check( roomDeviations.Count == roomFaces.Count, $"all {roomFaces.Count} room faces comparable, got {roomDeviations.Count}" );
			foreach ( var (id, deviation) in roomDeviations )
				Check( deviation.Max <= 0.05f, $"two-room face {id}: max deviation {deviation.Max:0.###}" );
		} );

		registry.Add( "radiosityquality", "null settings bake at the job's grid cap and bounce count, AO off", () =>
		{
			var face = BigFloor();
			var sun = LiveLight.Sun( 0, new Vec3( 0f, 0f, -1f ), Vec3.One, 1f );
			var options = new LightmapBakeOptions { MaxGridPerAxis = 4, BouncePasses = 1 };
			var job = new LightmapBakeJob( new[] { face }, new[] { sun }, options );
			var mapped = LightmapBakeQuality.ToRadiositySettings( job.Options );
			Check( mapped.MaxGridPerAxis == 4, $"mapped grid cap: {mapped.MaxGridPerAxis}" );
			Check( mapped.Bounces == 1, $"mapped bounces: {mapped.Bounces}" );
			Check( mapped.AoRays == 0, $"mapped AO rays: {mapped.AoRays} (AO is opt-in)" );
			var result = LightmapBakeQuality.BakeRadiosity( job );
			Check( result.Succeeded, $"bake failed: {result.Error}" );
			var baked = result.Face( 0 )!;
			Check( baked.Rows == 4 && baked.Cols == 4, $"job grid cap 4 gives a 4x4 grid, got {baked.Rows}x{baked.Cols}" );
			Check( result.BouncesRun == 1, $"job bounce count 1 gives 1 bounce run, ran {result.BouncesRun}" );
			Check( baked.AoFactors.All( f => f == 1f ), "AO off means every factor is exactly 1" );
		} );

		registry.Add( "radiosityquality", "identical quality bakes through the job are bit-for-bit deterministic", () =>
		{
			var (faces, lights) = LoadScene( "two-room.json" );
			var job = new LightmapBakeJob( faces, lights );
			var quality = RadiositySettings.Preset( RadiosityQuality.Standard );
			quality.AoRays = 16;
			var first = LightmapBakeQuality.BakeRadiosity( job, quality );
			var second = LightmapBakeQuality.BakeRadiosity( job, quality );
			Check( first.Succeeded && second.Succeeded, "both bakes must succeed" );
			Check( first.InvalidationKey == second.InvalidationKey, "keys must match" );
			foreach ( var face in first.Faces )
			{
				var other = second.Face( face.FaceId )!;
				for ( var i = 0; i < face.Luxels.Count; i++ )
					Check( face.Luxels[i].Equals( other.Luxels[i] ),
						$"face {face.FaceId} luxel {i} differs between identical bakes" );
			}
		} );

		registry.Add( "radiosityquality", "invalid inputs throw with the problem named; a cancelled bake reports Cancelled", () =>
		{
			var face = SmallFloor();
			var light = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f );
			void Throws<T>( Action action, string contains ) where T : Exception
			{
				try { action(); }
				catch ( T ex )
				{
					Check( ex.Message.Contains( contains ), $"exception must name '{contains}': {ex.Message}" );
					return;
				}
				throw new Exception( $"expected {typeof( T ).Name} containing '{contains}'" );
			}
			Throws<ArgumentNullException>( () => LightmapBakeQuality.BakeRadiosity( null! ), "job" );
			Throws<ArgumentNullException>( () => LightmapBakeQuality.ToRadiositySettings( null! ), "options" );
			Throws<ArgumentNullException>( () => LightmapBakeQuality.PackAtlas( null!, 64 ), "baked" );
			var job = new LightmapBakeJob( new[] { face }, new[] { light } );
			using var source = new System.Threading.CancellationTokenSource();
			source.Cancel();
			var cancelled = LightmapBakeQuality.BakeRadiosity( job,
				RadiositySettings.Preset( RadiosityQuality.Standard ), null, source.Token );
			Check( cancelled.Cancelled, "a pre-cancelled bake must report Cancelled" );
			Check( !cancelled.Succeeded, "a cancelled bake must not succeed" );
		} );
	}
}
