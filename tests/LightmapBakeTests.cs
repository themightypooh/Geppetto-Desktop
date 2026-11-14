#nullable enable
using Effigy;
using Effigy.Map;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

// Hammer Desktop P2: progressive native lightmap bake (low-res first, refine in background).
// LightmapBakeJob: per-face lightmap UV atlas, direct + bounced light from the lighting
// entities, low-res pass first then refinement in the background, invalidation only of
// changed leaves/faces, CPU first with the GpuBake trust rule (1K test-bake must match CPU
// before the GPU path is trusted).
//
// The shared room fixture (room.json) is worker 23's file in
// tests/fixtures/hammer/hammer-preview-live-lighting/ — this file loads it from there, it
// does not copy it. The bake reference (bake-reference.json) in THIS ticket's fixture dir
// is recorded from the real bake, not from the preview engine (worker 23's file was).
//
// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned and the
// final integrator adds the one LightmapBakeTests.Register(registry, settings); line.
internal static class LightmapBakeTests
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

	/// <summary>A 256x256 ceiling quad, z=128, normal -Z.</summary>
	static LiveFace BigCeiling( int id = 1 ) => new( id, new[]
	{
		new Vec3( 0f, 0f, 128f ), new Vec3( 0f, 256f, 128f ),
		new Vec3( 256f, 256f, 128f ), new Vec3( 256f, 0f, 128f ),
	} );

	/// <summary>Six outward faces of an axis box; ids base..base+5.</summary>
	static IEnumerable<LiveFace> BoxFaces( int @base, Vec3 min, Vec3 size )
	{
		var (x0, y0, z0) = (min.x, min.y, min.z);
		var (x1, y1, z1) = (min.x + size.x, min.y + size.y, min.z + size.z);
		yield return new LiveFace( @base + 0, new[] { new Vec3( x0, y0, z0 ), new Vec3( x1, y0, z0 ), new Vec3( x1, y1, z0 ), new Vec3( x0, y1, z0 ) } );
		yield return new LiveFace( @base + 1, new[] { new Vec3( x0, y0, z1 ), new Vec3( x0, y1, z1 ), new Vec3( x1, y1, z1 ), new Vec3( x1, y0, z1 ) } );
		yield return new LiveFace( @base + 2, new[] { new Vec3( x0, y0, z0 ), new Vec3( x0, y0, z1 ), new Vec3( x1, y0, z1 ), new Vec3( x1, y0, z0 ) } );
		yield return new LiveFace( @base + 3, new[] { new Vec3( x1, y1, z0 ), new Vec3( x1, y1, z1 ), new Vec3( x0, y1, z1 ), new Vec3( x0, y1, z0 ) } );
		yield return new LiveFace( @base + 4, new[] { new Vec3( x0, y0, z0 ), new Vec3( x0, y1, z0 ), new Vec3( x0, y1, z1 ), new Vec3( x0, y0, z1 ) } );
		yield return new LiveFace( @base + 5, new[] { new Vec3( x1, y0, z0 ), new Vec3( x1, y0, z1 ), new Vec3( x1, y1, z1 ), new Vec3( x1, y1, z0 ) } );
	}

	static string FindFixture( string dir, string name )
	{
		var rel = Path.Combine( dir, name );
		var candidates = new[]
		{
			Path.GetFullPath( Path.Combine( "tests", "fixtures", "hammer", rel ) ),
			Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "fixtures", "hammer", rel ) ),
		};
		foreach ( var candidate in candidates )
			if ( File.Exists( candidate ) ) return candidate;
		var d = new DirectoryInfo( Directory.GetCurrentDirectory() );
		for ( var i = 0; i < 6 && d is not null; i++, d = d.Parent )
		{
			var probe = Path.Combine( d.FullName, "tests", "fixtures", "hammer", rel );
			if ( File.Exists( probe ) ) return probe;
		}
		throw new Exception( $"Fixture not found: {rel} (searched from {Directory.GetCurrentDirectory()})." );
	}

	static Vec3 ReadVec3( JsonArray pair )
	{
		if ( pair.Count != 3 ) throw new Exception( $"Expected a [x, y, z] triple, got {pair.Count} values." );
		return new Vec3( (float)pair[0]!.GetValue<double>(), (float)pair[1]!.GetValue<double>(), (float)pair[2]!.GetValue<double>() );
	}

	/// <summary>Load the shared sealed-room fixture (worker 23's room.json): 6 inward faces + centre point, overhead sun, environment ambient.</summary>
	static (List<LiveFace> faces, List<LiveLight> lights) LoadRoom()
	{
		var json = JsonNode.Parse( File.ReadAllText( FindFixture( "hammer-preview-live-lighting", "room.json" ) ) )!.AsObject();
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
				default: throw new Exception( $"room.json: unknown light kind '{l["kind"]}'." );
			}
		}
		return (faces, lights);
	}

	/// <summary>A fake GPU backend for the trust-rule tests: Matching re-runs the CPU bake (identical), Diverging re-runs it with every light doubled, DeclineFinal passes the 1K test then declines the final.</summary>
	sealed class FakeGpuBackend : LightmapBakeJob.IGpuLightmapBackend
	{
		public enum Mode { Matching, Diverging, DeclineFinal }
		public Mode Behaviour;
		public bool TryBake( IReadOnlyList<LiveFace> faces, IReadOnlyList<LiveLight> lights, LightmapBakeOptions options, out LightmapBakeResult result, out string why )
		{
			why = null!;
			if ( Behaviour == Mode.DeclineFinal && options.AtlasSize != options.GpuTestSize )
			{
				result = null!;
				why = "final size not supported";
				return false;
			}
			var useLights = Behaviour == Mode.Diverging ? lights.Select( DoubleLight ).ToList() : lights;
			result = new LightmapBakeJob( faces, useLights, options ).Refine();
			return result.Succeeded;
		}

		static LiveLight DoubleLight( LiveLight light ) => light.Kind switch
		{
			LiveLightKind.Point => LiveLight.Point( light.Id, light.Position, light.Color, light.Intensity * 2f, light.Range ),
			LiveLightKind.Spot => LiveLight.Spot( light.Id, light.Position, light.Direction, light.Color, light.Intensity * 2f,
				light.InnerConeDegrees, light.OuterConeDegrees, light.Range ),
			LiveLightKind.Sun => LiveLight.Sun( light.Id, light.Direction, light.Color, light.Intensity * 2f ),
			LiveLightKind.Environment => LiveLight.Environment( light.Id, light.Color, light.Intensity * 2f ),
			_ => light,
		};
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "lightmapbake", "analytic point light matches the preview value exactly", () =>
		{
			// Face centre is 64 below the light: NdotL 1, falloff 1/(1+(64/64)^2) = 0.5, intensity 2 -> 1.0.
			var face = SmallFloor();
			var light = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f );
			var result = new LightmapBakeJob( new[] { face }, new[] { light } ).Refine();
			Check( result.Succeeded, $"bake failed: {result.Error}" );
			Check( result.Pass == LightmapBakePass.Refined, $"pass: {result.Pass}" );
			var baked = result.Face( 0 )!;
			Check( baked.Rows == 1 && baked.Cols == 1, $"1x1 grid expected, got {baked.Rows}x{baked.Cols}" );
			Check( Near( baked.LuxelAt( 0, 0 ), new Vec3( 1f, 1f, 1f ) ), $"centre luxel: {baked.LuxelAt( 0, 0 )}" );
			Check( Near( baked.Average, new Vec3( 1f, 1f, 1f ) ), $"average: {baked.Average}" );
			Check( Near( baked.BounceAverage, Vec3.Zero ), $"single face has no bounce source: {baked.BounceAverage}" );
			Check( !baked.DirectOnly, "refined pass must not be direct-only." );
			Check( result.LuxelCount == 1, $"luxel count: {result.LuxelCount}" );
		} );

		registry.Add( "lightmapbake", "preview and bake agree within 0.05 on the analytic case", () =>
		{
			var face = SmallFloor();
			var light = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f );
			var preview = LiveLighting.Evaluate( new[] { face }, new[] { light }, LiveLightingQuality.Draft );
			var bake = new LightmapBakeJob( new[] { face }, new[] { light } ).Refine();
			Check( bake.Succeeded, $"bake failed: {bake.Error}" );
			var comparison = LiveLighting.CompareToBake( preview, bake.BakedAveragePerFace, 0.05f );
			Check( comparison.WithinTolerance, $"max relative difference {comparison.MaxRelativeDifference} exceeds 0.05." );
			Check( Near( comparison.MaxRelativeDifference, 0f ), $"exact match expected: {comparison.MaxRelativeDifference}" );
			Check( comparison.ComparedFaceCount == 1, $"compared faces: {comparison.ComparedFaceCount}" );
		} );

		registry.Add( "lightmapbake", "two-pass bake refines: coarse is coarser and direct-only, refined adds bounce", () =>
		{
			var (faces, lights) = LoadRoom();
			var job = new LightmapBakeJob( faces, lights );
			var result = job.Run();
			Check( result.Succeeded, $"bake failed: {result.Error}" );
			Check( result.Coarse is not null, "Run must attach the coarse preview." );
			Check( result.Coarse!.Pass == LightmapBakePass.Coarse, $"coarse pass: {result.Coarse.Pass}" );
			var coarseFloor = result.Coarse.Face( 0 )!;
			var refinedFloor = result.Face( 0 )!;
			Check( coarseFloor.Rows == 4 && coarseFloor.Cols == 4,
				$"256 units at scale 16 x coarse factor 4 is 4x4, got {coarseFloor.Rows}x{coarseFloor.Cols}" );
			Check( refinedFloor.Rows == 16 && refinedFloor.Cols == 16,
				$"256 units at scale 16 is 16x16, got {refinedFloor.Rows}x{refinedFloor.Cols}" );
			Check( coarseFloor.DirectOnly && !refinedFloor.DirectOnly, "coarse must be direct-only, refined must not be." );
			Check( Near( coarseFloor.BounceAverage, Vec3.Zero ), $"coarse has no bounce: {coarseFloor.BounceAverage}" );
			Check( refinedFloor.BounceAverage.x > 0f, $"refined floor must gain bounce: {refinedFloor.BounceAverage}" );
			Check( refinedFloor.Average.x > coarseFloor.Average.x,
				$"refined floor {refinedFloor.Average} must be brighter than coarse {coarseFloor.Average}." );
			Check( result.Route == "cpu", $"route: {result.Route}" );
		} );

		registry.Add( "lightmapbake", "lightmap scale 4 vs 1 is 4x fewer luxels per axis on that face", () =>
		{
			var verts = new[]
			{
				new Vec3( 0f, 0f, 0f ), new Vec3( 128f, 0f, 0f ),
				new Vec3( 128f, 128f, 0f ), new Vec3( 0f, 128f, 0f ),
			};
			var options = new LightmapBakeOptions { MaxGridPerAxis = 256 };
			var fine = new LiveFace( 0, verts,
				new FaceMaterial( "brick/brickwall001", new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 0f, 1f, 0 ) ), Vec3.One );
			var coarse = new LiveFace( 0, verts,
				new FaceMaterial( "brick/brickwall001", new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 0f, 4f, 0 ) ), Vec3.One );
			var lights = new[] { LiveLight.Environment( 0, Vec3.One, 0.1f ) };
			var fineBake = new LightmapBakeJob( new[] { fine }, lights, options ).Refine();
			var coarseBake = new LightmapBakeJob( new[] { coarse }, lights, options ).Refine();
			Check( fineBake.Face( 0 )!.Rows == 128 && fineBake.Face( 0 )!.Cols == 128,
				$"128 units at scale 1 is 128x128, got {fineBake.Face( 0 )!.Rows}x{fineBake.Face( 0 )!.Cols}" );
			Check( coarseBake.Face( 0 )!.Rows == 32 && coarseBake.Face( 0 )!.Cols == 32,
				$"128 units at scale 4 is 32x32, got {coarseBake.Face( 0 )!.Rows}x{coarseBake.Face( 0 )!.Cols}" );
		} );

		registry.Add( "lightmapbake", "changing one brush invalidates only its faces", () =>
		{
			var (faces, lights) = LoadRoom();
			var job = new LightmapBakeJob( faces, lights );
			var first = job.Refine();
			Check( first.Succeeded, $"first bake failed: {first.Error}" );
			// Move one vertex of face 2 by 32 units; everything else is identical.
			var moved = faces.Select( f => f.Id == 2
				? new LiveFace( f.Id, f.Vertices.Select( ( v, i ) => i == 0 ? v + new Vec3( 32f, 0f, 0f ) : v ).ToArray(), f.Material, f.Albedo )
				: f ).ToList();
			var job2 = new LightmapBakeJob( moved, lights );
			var dirty = job2.PlanRebake( first );
			Check( dirty.Count == 1 && dirty[0] == 2, $"only face 2 must be dirty, got [{string.Join( ", ", dirty )}]" );
			var second = job2.BakeIncremental( first );
			Check( second.Succeeded, $"incremental bake failed: {second.Error}" );
			Check( second.ReusedFaceIds.Count == 5 && !second.ReusedFaceIds.Contains( 2 ),
				$"5 faces must be reused, got [{string.Join( ", ", second.ReusedFaceIds )}]" );
			Check( !second.ReusedPrevious, "a dirty face means the previous result is not reused wholesale." );
			foreach ( var id in second.ReusedFaceIds )
				Check( Near( second.Face( id )!.DirectAverage, first.Face( id )!.DirectAverage ),
					$"reused face {id} must keep its direct average: {second.Face( id )!.DirectAverage} vs {first.Face( id )!.DirectAverage}" );
			var changedLuxels = second.Face( 2 )!.Luxels.Zip( first.Face( 2 )!.Luxels,
				( a, b ) => MathF.Abs( a.x - b.x ) + MathF.Abs( a.y - b.y ) + MathF.Abs( a.z - b.z ) ).Count( d => d > 1e-3f );
			Check( changedLuxels > 0,
				$"moved face 2 must change luxels: {changedLuxels} luxels differ (first avg {first.Face( 2 )!.Average}, second avg {second.Face( 2 )!.Average})." );
			Check( second.Fingerprint != first.Fingerprint, "the job fingerprint must change when a face moves." );
		} );

		registry.Add( "lightmapbake", "incremental bake with no changes reuses the previous result", () =>
		{
			var (faces, lights) = LoadRoom();
			var first = new LightmapBakeJob( faces, lights ).Refine();
			var second = new LightmapBakeJob( faces, lights ).BakeIncremental( first );
			Check( second.Succeeded, $"incremental bake failed: {second.Error}" );
			Check( second.ReusedPrevious, "unchanged inputs must reuse the previous result." );
			Check( second.Faces!.Count == first.Faces!.Count, "reused result must keep every face." );
			Check( second.Fingerprint == first.Fingerprint, "fingerprints must match for unchanged inputs." );
		} );

		registry.Add( "lightmapbake", "a light change dirties every face", () =>
		{
			var (faces, lights) = LoadRoom();
			var first = new LightmapBakeJob( faces, lights ).Refine();
			var movedLight = lights.Select( l => l.Kind == LiveLightKind.Point
				? LiveLight.Point( l.Id, l.Position + new Vec3( 0f, 0f, 1f ), l.Color, l.Intensity, l.Range )
				: l ).ToList();
			var dirty = new LightmapBakeJob( faces, movedLight ).PlanRebake( first );
			Check( dirty.Count == 6, $"moving a light must dirty all 6 faces, got {dirty.Count}." );
		} );

		registry.Add( "lightmapbake", "gpu path without a backend is refused with a named reason", () =>
		{
			var (faces, lights) = LoadRoom();
			var previous = LightmapBakeJob.Provider;
			try
			{
				LightmapBakeJob.Provider = null;
				var attempt = new LightmapBakeJob( faces, lights ).TryGpuBake();
				Check( !attempt.Accepted, "no backend must not accept the GPU path." );
				Check( attempt.RefusedReason is not null && attempt.RefusedReason.Contains( "no gpu backend installed" ),
					$"refused reason must name the missing backend: {attempt.RefusedReason}" );
				Check( attempt.Result is not null && attempt.Result.Succeeded, "the refused attempt still returns the usable CPU bake." );
				Check( attempt.Route.StartsWith( "cpu", StringComparison.Ordinal ), $"route: {attempt.Route}" );
			}
			finally { LightmapBakeJob.Provider = previous; }
		} );

		registry.Add( "lightmapbake", "gpu path with a diverging backend is refused with a named reason", () =>
		{
			var (faces, lights) = LoadRoom();
			var previous = LightmapBakeJob.Provider;
			try
			{
				LightmapBakeJob.Provider = new FakeGpuBackend { Behaviour = FakeGpuBackend.Mode.Diverging };
				var attempt = new LightmapBakeJob( faces, lights ).TryGpuBake();
				Check( !attempt.Accepted, "a diverging backend must not be trusted." );
				Check( attempt.RefusedReason is not null && attempt.RefusedReason.Contains( "diverged on face" ),
					$"refused reason must name the divergence: {attempt.RefusedReason}" );
				Check( attempt.Deviations.Count == 6, $"one deviation per face, got {attempt.Deviations.Count}." );
				Check( attempt.Result is not null && attempt.Result.Succeeded, "the refused attempt still returns the usable CPU bake." );
			}
			finally { LightmapBakeJob.Provider = previous; }
		} );

		registry.Add( "lightmapbake", "gpu path with a matching backend is accepted", () =>
		{
			var (faces, lights) = LoadRoom();
			var previous = LightmapBakeJob.Provider;
			try
			{
				LightmapBakeJob.Provider = new FakeGpuBackend { Behaviour = FakeGpuBackend.Mode.Matching };
				var attempt = new LightmapBakeJob( faces, lights ).TryGpuBake();
				Check( attempt.Accepted, $"a matching backend must be accepted: {attempt.RefusedReason}" );
				Check( attempt.RefusedReason is null, $"no refusal on acceptance: {attempt.RefusedReason}" );
				Check( attempt.Route == "gpu", $"route: {attempt.Route}" );
				Check( attempt.Result is not null && attempt.Result.Succeeded, "the accepted GPU result must succeed." );
				Check( attempt.Deviations.Values.All( d => d.Mean == 0.0 && d.Max == 0.0 ),
					"a matching backend must deviate by exactly 0." );
			}
			finally { LightmapBakeJob.Provider = previous; }
		} );

		registry.Add( "lightmapbake", "gpu backend declining the final bake is refused with a named reason", () =>
		{
			var (faces, lights) = LoadRoom();
			var previous = LightmapBakeJob.Provider;
			try
			{
				// Final atlas 2048 vs test-bake 1024: the fake passes the 1K test (1024) and
				// declines the final (2048), which is the discrimination under test.
				LightmapBakeJob.Provider = new FakeGpuBackend { Behaviour = FakeGpuBackend.Mode.DeclineFinal };
				var attempt = new LightmapBakeJob( faces, lights, new LightmapBakeOptions { AtlasSize = 2048 } ).TryGpuBake();
				Check( !attempt.Accepted, "a backend declining the final bake must not be accepted." );
				Check( attempt.RefusedReason is not null && attempt.RefusedReason.Contains( "declined the final bake" ),
					$"refused reason must name the declined final: {attempt.RefusedReason}" );
				Check( attempt.Result is not null && attempt.Result.Succeeded, "the refused attempt still returns the usable CPU bake." );
			}
			finally { LightmapBakeJob.Provider = previous; }
		} );

		registry.Add( "lightmapbake", "sun bakes a lit lightmap on an open floor, and the ceiling shadows it in a sealed room", () =>
		{
			var sun = LiveLight.Sun( 0, new Vec3( 0f, 0f, -1f ), Vec3.One, 1.5f );
			var open = new LightmapBakeJob( new[] { BigFloor() }, new[] { sun } ).Refine();
			Check( open.Succeeded, $"open bake failed: {open.Error}" );
			Check( Near( open.Face( 0 )!.Average, new Vec3( 1.5f, 1.5f, 1.5f ), 1e-3f ),
				$"sunlit open floor: {open.Face( 0 )!.Average}" );
			Check( open.Warnings.Count == 1 && open.Warnings[0].Contains( "light_environment" ),
				$"sun without environment must warn: {string.Join( "; ", open.Warnings )}" );

			// Sealed room: the ceiling blocks the overhead sun, so the baked floor loses the
			// sun the shadow-free preview still shows — the bake is darker, correctly.
			var (faces, lights) = LoadRoom();
			var preview = LiveLighting.Evaluate( faces, lights, LiveLightingQuality.Full );
			var bake = new LightmapBakeJob( faces, lights ).Refine();
			Check( bake.Succeeded, $"room bake failed: {bake.Error}" );
			Check( bake.Face( 0 )!.Average.x > 0f, $"baked floor must not be black: {bake.Face( 0 )!.Average}" );
			Check( bake.Face( 0 )!.Average.x < preview.Face( 0 )!.Average.x - 0.3f,
				$"baked floor {bake.Face( 0 )!.Average} must lose the shadowed sun vs preview {preview.Face( 0 )!.Average}." );
			Check( bake.Face( 1 )!.Average.x > 0f, $"baked ceiling must not be black: {bake.Face( 1 )!.Average}" );
		} );

		registry.Add( "lightmapbake", "nodraw face is skipped and still blocks the sun", () =>
		{
			var ceiling = new LiveFace( 1, BigCeiling().Vertices, FaceMaterial.Default, Vec3.One );
			Check( ceiling.IsNoDraw, "FaceMaterial.Default must be nodraw." );
			var sun = LiveLight.Sun( 0, new Vec3( 0f, 0f, -1f ), Vec3.One, 1.5f );
			var result = new LightmapBakeJob( new[] { BigFloor( 0 ), ceiling }, new[] { sun } ).Refine();
			Check( result.Succeeded, $"bake failed: {result.Error}" );
			var skipped = result.Face( 1 )!;
			Check( skipped.SkippedNoDraw, "nodraw face must be marked skipped." );
			Check( skipped.Rows == 0 && skipped.Cols == 0 && skipped.Luxels.Count == 0, "nodraw face must have an empty grid." );
			Check( Near( result.Face( 0 )!.Average, Vec3.Zero ),
				$"floor under a nodraw ceiling must get no sun: {result.Face( 0 )!.Average}" );
		} );

		registry.Add( "lightmapbake", "atlas rects tile without overlap", () =>
		{
			var (faces, lights) = LoadRoom();
			var result = new LightmapBakeJob( faces, lights ).Refine();
			Check( result.Succeeded, $"bake failed: {result.Error}" );
			var rects = result.Faces!.Where( f => !f.SkippedNoDraw ).Select( f => (f.FaceId, f.AtlasRect, f.Rows, f.Cols) ).ToList();
			Check( rects.Count == 6, $"6 faces need atlas rects, got {rects.Count}." );
			foreach ( var (id, r, rows, cols) in rects )
			{
				Check( r.U0 >= 0f && r.V0 >= 0f && r.U1 <= 1f && r.V1 <= 1f, $"face {id} rect out of the atlas: {r}." );
				Check( Near( r.U1 - r.U0, cols / (float)result.AtlasWidth, 1e-6f ),
					$"face {id} rect width must be cols/atlas: {r.U1 - r.U0} vs {cols}/{result.AtlasWidth}." );
				Check( Near( r.V1 - r.V0, rows / (float)result.AtlasHeight, 1e-6f ),
					$"face {id} rect height must be rows/atlas: {r.V1 - r.V0} vs {rows}/{result.AtlasHeight}." );
			}
			for ( var i = 0; i < rects.Count; i++ )
				for ( var j = i + 1; j < rects.Count; j++ )
				{
					var a = rects[i].AtlasRect;
					var b = rects[j].AtlasRect;
					var overlap = a.U0 < b.U1 && b.U0 < a.U1 && a.V0 < b.V1 && b.V0 < a.V1;
					Check( !overlap, $"faces {rects[i].FaceId} and {rects[j].FaceId} overlap in the atlas." );
				}
		} );

		registry.Add( "lightmapbake", "atlas too small is a named error, not a silent downscale", () =>
		{
			// 256-unit floor at lightmap scale 4 is a 64x64 grid, which plus its 1-luxel
			// gutter does not fit a 64x64 atlas.
			var options = new LightmapBakeOptions { AtlasSize = 64 };
			var face = new LiveFace( 0, BigFloor().Vertices,
				new FaceMaterial( "brick/brickwall001", new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 0f, 4f, 0 ) ), Vec3.One );
			var result = new LightmapBakeJob( new[] { face },
				new[] { LiveLight.Environment( 0, Vec3.One, 0.1f ) }, options ).Run();
			Check( !result.Succeeded, "a bake that does not fit the atlas must fail." );
			Check( result.Error is not null && result.Error.Contains( "Atlas" ),
				$"error must name the atlas: {result.Error}" );
			Check( result.Faces is null, "a failed bake must not claim faces." );
		} );

		registry.Add( "lightmapbake", "invalid inputs throw naming the problem", () =>
		{
			void Throws<T>( Action action, string contains, string what ) where T : Exception
			{
				var threw = false;
				try { action(); }
				catch ( T ex ) { threw = ex.Message.Contains( contains ); }
				Check( threw, $"{what} must throw {typeof( T ).Name} naming '{contains}'." );
			}
			Throws<ArgumentException>( () => _ = new LightmapBakeJob( new[] { SmallFloor( 0 ), SmallFloor( 0 ) }, Array.Empty<LiveLight>() ),
				"duplicate", "duplicate face ids" );
			Throws<ArgumentException>( () => _ = new LightmapBakeJob( new[]
				{
					new LiveFace( 0, new[] { new Vec3( 0f, 0f, 0f ), new Vec3( 1f, 0f, 0f ), new Vec3( 2f, 0f, 0f ) } ),
				}, Array.Empty<LiveLight>() ), "face 0", "a zero-area face" );
			Throws<ArgumentException>( () => _ = new LightmapBakeJob( Array.Empty<LiveFace>(), Array.Empty<LiveLight>() ),
				"at least one face", "an empty face list" );
			Throws<ArgumentException>( () => _ = new LightmapBakeJob( new[] { SmallFloor() }, Array.Empty<LiveLight>(),
					new LightmapBakeOptions { AtlasSize = 100 } ), "AtlasSize", "a non-power-of-two atlas" );
			Throws<ArgumentException>( () => _ = new LightmapBakeJob( new[] { SmallFloor() }, Array.Empty<LiveLight>(),
					new LightmapBakeOptions { CoarseFactor = 0 } ), "CoarseFactor", "a coarse factor below 1" );
			Throws<ArgumentNullException>( () => _ = new LightmapBakeJob( null!, Array.Empty<LiveLight>() ),
				"faces", "a null face list" );
			var noLights = new LightmapBakeJob( new[] { SmallFloor() }, Array.Empty<LiveLight>() ).BakeCoarse();
			Check( noLights.Succeeded, $"a bake with no lights must still succeed: {noLights.Error}" );
			Check( noLights.Warnings.Count == 1 && Near( noLights.Face( 0 )!.Average, Vec3.Zero ),
				"no lights warns and bakes black instead of throwing." );
		} );

		registry.Add( "lightmapbake", "cancelled bake reports cancelled, never throws", () =>
		{
			var (faces, lights) = LoadRoom();
			var cancelled = new CancellationToken( canceled: true );
			var result = new LightmapBakeJob( faces, lights ).Run( null, cancelled );
			Check( result.Cancelled, "a pre-cancelled run must report cancelled." );
			Check( result.Faces is null, "a cancelled run must not claim faces." );
			Check( !result.Succeeded, "a cancelled run must not succeed." );
		} );

		registry.Add( "lightmapbake", "run reports real progress ending at done 100", () =>
		{
			var (faces, lights) = LoadRoom();
			var reports = new List<BakeProgress>();
			var progress = new Progress<BakeProgress>( r => { lock ( reports ) reports.Add( r ); } );
			var result = new LightmapBakeJob( faces, lights ).Run( progress );
			Check( result.Succeeded, $"bake failed: {result.Error}" );
			Check( reports.Count > 0, "a run must report progress." );
			Check( reports.Any( r => r.Phase == BakePhase.Baking ), "progress must include the baking phase." );
			var last = reports[reports.Count - 1];
			Check( last.Phase == BakePhase.Done && Near( last.Percent, 100f ), $"last report must be Done at 100, got {last.Phase} {last.Percent}." );
		} );

		registry.Add( "lightmapbake", "triangle face shades only inside-polygon luxels", () =>
		{
			var triangle = new LiveFace( 0, new[]
			{
				new Vec3( 0f, 0f, 0f ), new Vec3( 64f, 0f, 0f ), new Vec3( 0f, 64f, 0f ),
			} );
			var result = new LightmapBakeJob( new[] { triangle },
				new[] { LiveLight.Environment( 0, Vec3.One, 1f ) } ).Refine();
			Check( result.Succeeded, $"bake failed: {result.Error}" );
			var baked = result.Face( 0 )!;
			Check( baked.Rows == 4 && baked.Cols == 4, $"64 units at scale 16 is 4x4, got {baked.Rows}x{baked.Cols}" );
			Check( baked.InsideCount > 0 && baked.InsideCount < 16,
				$"a triangle covers part of its bounds: {baked.InsideCount} inside of 16." );
			for ( var i = 0; i < baked.Luxels.Count; i++ )
			{
				if ( baked.InsideMask[i] )
					Check( Near( baked.Luxels[i], Vec3.One ), $"inside luxel {i} must be ambient 1.0: {baked.Luxels[i]}" );
				else
					Check( Near( baked.Luxels[i], Vec3.Zero ), $"outside luxel {i} must stay zero: {baked.Luxels[i]}" );
			}
			Check( Near( baked.Average, Vec3.One ), $"average is over inside luxels only: {baked.Average}" );
		} );

		registry.Add( "lightmapbake", "bounce brightens facing faces", () =>
		{
			var faces = new[] { BigFloor( 0 ), BigCeiling( 1 ) };
			var lights = new[]
			{
				LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f ),
				LiveLight.Environment( 1, new Vec3( 0.2f, 0.2f, 0.25f ), 1f ),
			};
			var job = new LightmapBakeJob( faces, lights );
			var coarse = job.BakeCoarse();
			var refined = job.Refine();
			Check( coarse.Succeeded && refined.Succeeded, "both passes must succeed." );
			Check( refined.Face( 0 )!.Average.x > coarse.Face( 0 )!.Average.x,
				$"floor gains bounce: coarse {coarse.Face( 0 )!.Average} -> refined {refined.Face( 0 )!.Average}." );
			Check( refined.Face( 1 )!.Average.x > coarse.Face( 1 )!.Average.x,
				$"ceiling gains bounce: coarse {coarse.Face( 1 )!.Average} -> refined {refined.Face( 1 )!.Average}." );
		} );

		registry.Add( "lightmapbake", "coarse preview is fast on a 25-brush room", () =>
		{
			var faces = new List<LiveFace>();
			for ( var i = 0; i < 25; i++ )
			{
				var min = new Vec3( ( i % 5 ) * 72f, ( i / 5 ) * 72f, 0f );
				faces.AddRange( BoxFaces( i * 6, min, new Vec3( 64f, 64f, 64f ) ) );
			}
			var lights = new[]
			{
				LiveLight.Point( 0, new Vec3( 160f, 160f, 200f ), Vec3.One, 2f ),
				LiveLight.Sun( 1, new Vec3( 0f, 0f, -1f ), Vec3.One, 0.5f ),
				LiveLight.Environment( 2, new Vec3( 0.2f, 0.2f, 0.25f ), 1f ),
			};
			var clock = Stopwatch.StartNew();
			var result = new LightmapBakeJob( faces, lights ).BakeCoarse();
			clock.Stop();
			Check( result.Succeeded, $"bake failed: {result.Error}" );
			Check( result.Faces!.Count == 150, $"25 brushes is 150 faces, got {result.Faces.Count}" );
			Check( result.MaxLuxel.x > 0f, "the room must not bake black." );
			Check( clock.ElapsedMilliseconds < 5000,
				$"coarse 25-brush room took {clock.ElapsedMilliseconds} ms (budget 5000 ms)." );
		} );

		registry.Add( "lightmapbake", "room fixture bakes and matches the recorded reference", () =>
		{
			var (faces, lights) = LoadRoom();
			Check( faces.Count == 6, $"room fixture has 6 faces, got {faces.Count}" );
			Check( lights.Count == 3, $"room fixture has 3 lights, got {lights.Count}" );
			var result = new LightmapBakeJob( faces, lights ).Refine();
			Check( result.Succeeded, $"bake failed: {result.Error}" );
			var floor = result.Face( 0 )!;
			var ceiling = result.Face( 1 )!;
			// Symmetry, not a bug: the point light sits exactly midway between floor and
			// ceiling, the sun is blocked for both (floor by the ceiling, ceiling faces
			// away), and the ambient is uniform — so the bake gives them equal averages.
			// (The shadow-free preview gives the floor the unshadowed sun, so it is
			// brighter there; see the sun test above for the difference.)
			Check( Near( floor.Average, ceiling.Average, 1e-3f ),
				$"floor {floor.Average} and ceiling {ceiling.Average} must match by symmetry." );
			Check( floor.Average.x > result.Face( 2 )!.Average.x,
				$"floor {floor.Average} must be brighter than a wall {result.Face( 2 )!.Average}." );
			Check( floor.Rows == 16 && floor.Cols == 16, $"floor grid: {floor.Rows}x{floor.Cols}" );
			Check( result.Warnings.Count == 0, $"room fixture must not warn: {string.Join( "; ", result.Warnings )}" );
			// Bake reference: recorded from THIS bake's refined pass of room.json.
			// If the file is missing, this throws carrying the values to record into it.
			string snapshotPath;
			try { snapshotPath = FindFixture( "hammer-lightmap-progressive-bake", "bake-reference.json" ); }
			catch ( Exception )
			{
				throw new Exception( "bake-reference.json is missing; record this bake's refined averages into it: " +
					result.ToReferenceJson() );
			}
			var snapshot = JsonNode.Parse( File.ReadAllText( snapshotPath ) )!.AsObject();
			var baked = new Dictionary<int, Vec3>();
			foreach ( var pair in snapshot["averages"]!.AsObject() )
				baked[int.Parse( pair.Key )] = ReadVec3( pair.Value!.AsArray() );
			foreach ( var pair in baked )
			{
				var face = result.Face( pair.Key );
				Check( face is not null, $"reference has face {pair.Key} but the bake does not." );
				Check( Near( face!.Average, pair.Value, 1e-4f ),
					$"face {pair.Key} average {face.Average} does not match the recorded reference {pair.Value}." );
			}
			Check( baked.Count == 6, $"reference must cover 6 faces, got {baked.Count}." );
		} );
	}
}
