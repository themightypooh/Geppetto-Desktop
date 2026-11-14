#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Effigy;

// Hammer Desktop P2: live lighting preview evaluation engine (no compile).
// LiveLighting.Evaluate(faces, lights, quality) -> per-face luxel grid + per-vertex
// colour; direct light from light/light_spot/light_environment + env_sun; cheap bounce
// approximation; Draft/Full quality; invalidation keyed on changed lights/faces.
//
// DEPENDENCY NOTE: the lighting-entities, progressive-bake and map-document tickets
// had NOT landed when this was written, so these checks build against the LiveFace /
// LiveLight input types the filed spec describes, not against MapDocument or
// LightmapBake. The bake comparison uses an analytic single-light case (tolerance
// 0.05) plus bake-reference.json, a regression snapshot recorded from this engine's
// own Full evaluation — the real bake does not exist yet to compare against, and the
// fixture README says so. The final integrator re-records that file from the real
// bake when hammer-lightmap-progressive-bake lands.
//
// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned and the
// final integrator adds the one LiveLightingTests.Register(registry, settings); line.
internal static class LiveLightingTests
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

	static string FindFixture( string name )
	{
		var rel = Path.Combine( "hammer-preview-live-lighting", name );
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

	static (List<LiveFace> faces, List<LiveLight> lights) LoadRoom()
	{
		var json = JsonNode.Parse( File.ReadAllText( FindFixture( "room.json" ) ) )!.AsObject();
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

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "livelighting", "point light analytic value matches the documented falloff", () =>
		{
			// Face centre is 64 below the light: NdotL 1, falloff 1/(1+(64/64)^2) = 0.5.
			var face = SmallFloor();
			var light = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f );
			var result = LiveLighting.Evaluate( new[] { face }, new[] { light }, LiveLightingQuality.Draft );
			var lit = result.Face( 0 )!;
			Check( lit.Rows == 1 && lit.Cols == 1, $"1x1 grid expected, got {lit.Rows}x{lit.Cols}" );
			Check( Near( lit.LuxelAt( 0, 0 ), new Vec3( 1f, 1f, 1f ) ), $"centre luxel: {lit.LuxelAt( 0, 0 )}" );
			Check( Near( lit.Average, new Vec3( 1f, 1f, 1f ) ), $"average: {lit.Average}" );
			Check( !result.HasBounce, "Draft must not include bounce." );
			Check( result.LuxelCount == 1, $"luxel count: {result.LuxelCount}" );
		} );

		registry.Add( "livelighting", "moving a light changes the lit faces", () =>
		{
			var nearCentre = new LiveFace( 0, new[]
			{
				new Vec3( 120f, 120f, 0f ), new Vec3( 136f, 120f, 0f ),
				new Vec3( 136f, 136f, 0f ), new Vec3( 120f, 136f, 0f ),
			} );
			var nearCorner = new LiveFace( 1, new[]
			{
				new Vec3( 8f, 8f, 0f ), new Vec3( 24f, 8f, 0f ),
				new Vec3( 24f, 24f, 0f ), new Vec3( 8f, 24f, 0f ),
			} );
			var faces = new[] { nearCentre, nearCorner };
			var atCentre = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f );
			var atCorner = LiveLight.Point( 0, new Vec3( 16f, 16f, 64f ), Vec3.One, 2f );
			var before = LiveLighting.Evaluate( faces, new[] { atCentre }, LiveLightingQuality.Draft );
			var after = LiveLighting.Evaluate( faces, new[] { atCorner }, LiveLightingQuality.Draft );
			Check( Near( before.Face( 0 )!.Average, new Vec3( 1f, 1f, 1f ), 1e-3f ), $"centre face before: {before.Face( 0 )!.Average}" );
			Check( before.Face( 1 )!.Average.x < 0.2f, $"corner face before: {before.Face( 1 )!.Average}" );
			Check( after.Face( 0 )!.Average.x < 0.2f, $"centre face after: {after.Face( 0 )!.Average}" );
			Check( Near( after.Face( 1 )!.Average, new Vec3( 1f, 1f, 1f ), 1e-3f ), $"corner face after: {after.Face( 1 )!.Average}" );
			Check( before.InvalidationKey != after.InvalidationKey, "moving the light must change the invalidation key." );
		} );

		registry.Add( "livelighting", "spot cone lights inside and dark outside", () =>
		{
			var centre = SmallFloor( 0 );
			var far = new LiveFace( 1, new[]
			{
				new Vec3( 8f, 8f, 0f ), new Vec3( 24f, 8f, 0f ),
				new Vec3( 24f, 24f, 0f ), new Vec3( 8f, 24f, 0f ),
			} );
			// ~20 degrees off axis: inside the 10..30 degree falloff band, so dim but lit.
			var edge = new LiveFace( 2, new[]
			{
				new Vec3( 143f, 120f, 0f ), new Vec3( 159f, 120f, 0f ),
				new Vec3( 159f, 136f, 0f ), new Vec3( 143f, 136f, 0f ),
			} );
			var spot = LiveLight.Spot( 0, new Vec3( 128f, 128f, 64f ), new Vec3( 0f, 0f, -1f ),
				Vec3.One, 2f, 10f, 30f );
			var result = LiveLighting.Evaluate( new[] { centre, far, edge }, new[] { spot }, LiveLightingQuality.Draft );
			Check( Near( result.Face( 0 )!.Average, new Vec3( 1f, 1f, 1f ), 1e-3f ), $"on-axis: {result.Face( 0 )!.Average}" );
			Check( Near( result.Face( 1 )!.Average, Vec3.Zero ), $"68deg off-axis must be dark: {result.Face( 1 )!.Average}" );
			var edgeAvg = result.Face( 2 )!.Average.x;
			Check( edgeAvg > 0.3f && edgeAvg < 0.9f, $"20deg off-axis must be dim but lit: {edgeAvg}" );
		} );

		registry.Add( "livelighting", "sun lights floor not ceiling and warns without environment", () =>
		{
			var faces = new[] { BigFloor( 0 ), BigCeiling( 1 ) };
			var sun = LiveLight.Sun( 0, new Vec3( 0f, 0f, -1f ), Vec3.One, 1.5f );
			var result = LiveLighting.Evaluate( faces, new[] { sun }, LiveLightingQuality.Draft );
			Check( Near( result.Face( 0 )!.Average, new Vec3( 1.5f, 1.5f, 1.5f ), 1e-3f ), $"sunlit floor: {result.Face( 0 )!.Average}" );
			Check( Near( result.Face( 1 )!.Average, Vec3.Zero ), $"ceiling faces away from the sun: {result.Face( 1 )!.Average}" );
			Check( result.Warnings.Count == 1 && result.Warnings[0].Contains( "light_environment" ),
				$"sun without environment must warn: {string.Join( "; ", result.Warnings )}" );
			var withEnv = LiveLighting.Evaluate( faces,
				new[] { sun, LiveLight.Environment( 1, new Vec3( 0.2f, 0.2f, 0.25f ), 1f ) }, LiveLightingQuality.Draft );
			Check( withEnv.Warnings.Count == 0, $"sun with environment must not warn: {string.Join( "; ", withEnv.Warnings )}" );
			Check( Near( withEnv.Face( 1 )!.Average, new Vec3( 0.2f, 0.2f, 0.25f ), 1e-3f ),
				$"ceiling gets ambient only: {withEnv.Face( 1 )!.Average}" );
		} );

		registry.Add( "livelighting", "environment ambient lifts every face", () =>
		{
			var faces = new[] { BigFloor( 0 ), BigCeiling( 1 ) };
			var env = LiveLight.Environment( 0, new Vec3( 0.2f, 0.2f, 0.25f ), 1f );
			var result = LiveLighting.Evaluate( faces, new[] { env }, LiveLightingQuality.Draft );
			Check( Near( result.Face( 0 )!.Average, new Vec3( 0.2f, 0.2f, 0.25f ), 1e-3f ), $"floor ambient: {result.Face( 0 )!.Average}" );
			Check( Near( result.Face( 1 )!.Average, new Vec3( 0.2f, 0.2f, 0.25f ), 1e-3f ), $"ceiling ambient: {result.Face( 1 )!.Average}" );
		} );

		registry.Add( "livelighting", "draft is direct plus ambient, full adds bounce", () =>
		{
			var face = SmallFloor();
			var lights = new[]
			{
				LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f ),
				LiveLight.Environment( 1, new Vec3( 0.2f, 0.2f, 0.2f ), 1f ),
			};
			var draft = LiveLighting.Evaluate( new[] { face }, lights, LiveLightingQuality.Draft );
			var full = LiveLighting.Evaluate( new[] { face }, lights, LiveLightingQuality.Full );
			Check( Near( draft.Face( 0 )!.LuxelAt( 0, 0 ), new Vec3( 1.2f, 1.2f, 1.2f ) ),
				$"draft = direct 1.0 + ambient 0.2: {draft.Face( 0 )!.LuxelAt( 0, 0 )}" );
			Check( Near( full.Face( 0 )!.LuxelAt( 0, 0 ), new Vec3( 1.24f, 1.24f, 1.24f ) ),
				$"full adds 0.2 * ambient bounce: {full.Face( 0 )!.LuxelAt( 0, 0 )}" );
			Check( full.Face( 0 )!.Average.x > draft.Face( 0 )!.Average.x, "Full must be brighter than Draft." );
			Check( full.HasBounce && !draft.HasBounce, "HasBounce must follow the quality." );
		} );

		registry.Add( "livelighting", "bounce uses the baked average where present", () =>
		{
			var face = SmallFloor();
			var lights = new[]
			{
				LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f ),
				LiveLight.Environment( 1, new Vec3( 0.2f, 0.2f, 0.2f ), 1f ),
			};
			var baked = new Dictionary<int, Vec3> { [0] = new Vec3( 2f, 2f, 2f ) };
			var full = LiveLighting.Evaluate( new[] { face }, lights, LiveLightingQuality.Full, baked );
			Check( Near( full.Face( 0 )!.LuxelAt( 0, 0 ), new Vec3( 1.9f, 1.9f, 1.9f ) ),
				$"full with bake = 1.0 + 0.2 + 0.35*2.0: {full.Face( 0 )!.LuxelAt( 0, 0 )}" );
			var draft = LiveLighting.Evaluate( new[] { face }, lights, LiveLightingQuality.Draft, baked );
			Check( Near( draft.Face( 0 )!.LuxelAt( 0, 0 ), new Vec3( 1.2f, 1.2f, 1.2f ) ),
				$"draft ignores the bake: {draft.Face( 0 )!.LuxelAt( 0, 0 )}" );
		} );

		registry.Add( "livelighting", "luxel grid honours lightmap scale and quality caps", () =>
		{
			var face = BigFloor();
			var lights = new[] { LiveLight.Environment( 0, Vec3.One, 0.1f ) };
			var full16 = LiveLighting.Evaluate( new[] { face }, lights, LiveLightingQuality.Full );
			Check( full16.Face( 0 )!.Rows == 16 && full16.Face( 0 )!.Cols == 16,
				$"256 units at scale 16 is 16x16, got {full16.Face( 0 )!.Rows}x{full16.Face( 0 )!.Cols}" );
			var draft16 = LiveLighting.Evaluate( new[] { face }, lights, LiveLightingQuality.Draft );
			Check( draft16.Face( 0 )!.Rows == 8 && draft16.Face( 0 )!.Cols == 8,
				$"draft caps at 8x8, got {draft16.Face( 0 )!.Rows}x{draft16.Face( 0 )!.Cols}" );
			var coarse = new LiveFace( 0, BigFloor().Vertices,
				new FaceMaterial( "brick/brickwall001", new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 0f, 64f, 0 ) ), Vec3.One );
			var full64 = LiveLighting.Evaluate( new[] { coarse }, lights, LiveLightingQuality.Full );
			Check( full64.Face( 0 )!.Rows == 4 && full64.Face( 0 )!.Cols == 4,
				$"256 units at scale 64 is 4x4, got {full64.Face( 0 )!.Rows}x{full64.Face( 0 )!.Cols}" );
			var fine = new LiveFace( 0, BigFloor().Vertices,
				new FaceMaterial( "brick/brickwall001", new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 0f, 4f, 0 ) ), Vec3.One );
			var full4 = LiveLighting.Evaluate( new[] { fine }, lights, LiveLightingQuality.Full );
			Check( full4.Face( 0 )!.Rows == 32 && full4.Face( 0 )!.Cols == 32,
				$"full caps at 32x32, got {full4.Face( 0 )!.Rows}x{full4.Face( 0 )!.Cols}" );
			Check( Near( full16.Face( 0 )!.LuxelSize, 16f ), $"luxel size: {full16.Face( 0 )!.LuxelSize}" );
		} );

		registry.Add( "livelighting", "nodraw face is skipped", () =>
		{
			var face = new LiveFace( 0, BigFloor().Vertices, FaceMaterial.Default, Vec3.One );
			Check( face.IsNoDraw, "FaceMaterial.Default must be nodraw." );
			var result = LiveLighting.Evaluate( new[] { face },
				new[] { LiveLight.Environment( 0, Vec3.One, 1f ) }, LiveLightingQuality.Full );
			var lit = result.Face( 0 )!;
			Check( lit.SkippedNoDraw, "nodraw face must be marked skipped." );
			Check( lit.Rows == 0 && lit.Cols == 0 && lit.Luxels.Count == 0, "nodraw face must have an empty grid." );
			Check( lit.VertexColors.All( c => c.Equals( Vec3.Zero ) ), "nodraw vertex colours must be zero." );
			Check( result.LuxelCount == 0, $"nodraw contributes no luxels: {result.LuxelCount}" );
		} );

		registry.Add( "livelighting", "invalidation key tracks lights and faces, cache reuses unchanged runs", () =>
		{
			var faces = new[] { SmallFloor() };
			var lights = new[] { LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f ) };
			var keyA = LiveLighting.ComputeInvalidationKey( faces, lights, LiveLightingQuality.Draft );
			var keyB = LiveLighting.ComputeInvalidationKey( faces, lights, LiveLightingQuality.Draft );
			Check( keyA == keyB, "same inputs must give the same key." );
			var movedLight = new[] { LiveLight.Point( 0, new Vec3( 128f, 128f, 63f ), Vec3.One, 2f ) };
			Check( LiveLighting.ComputeInvalidationKey( faces, movedLight, LiveLightingQuality.Draft ) != keyA,
				"moving a light by 1 unit must change the key." );
			var movedFace = new[] { new LiveFace( 0, new[]
			{
				new Vec3( 121f, 120f, 0f ), new Vec3( 136f, 120f, 0f ),
				new Vec3( 136f, 136f, 0f ), new Vec3( 120f, 136f, 0f ),
			} ) };
			Check( LiveLighting.ComputeInvalidationKey( movedFace, lights, LiveLightingQuality.Draft ) != keyA,
				"moving a face vertex by 1 unit must change the key." );
			Check( LiveLighting.ComputeInvalidationKey( faces, lights, LiveLightingQuality.Full ) != keyA,
				"changing quality must change the key." );
			var reordered = new[] { BigCeiling( 1 ), SmallFloor( 0 ) };
			var keyReordered = LiveLighting.ComputeInvalidationKey( reordered,
				new[] { LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f ) }, LiveLightingQuality.Draft );
			var keyWithCeiling = LiveLighting.ComputeInvalidationKey( new[] { SmallFloor( 0 ), BigCeiling( 1 ) },
				new[] { LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f ) }, LiveLightingQuality.Draft );
			Check( keyReordered == keyWithCeiling, "face order must not change the key." );
			var cache = new LiveLightingCache();
			var first = cache.Evaluate( faces, lights, LiveLightingQuality.Draft );
			var second = cache.Evaluate( faces, lights, LiveLightingQuality.Draft );
			Check( ReferenceEquals( first, second ), "unchanged inputs must return the cached result." );
			var third = cache.Evaluate( faces, movedLight, LiveLightingQuality.Draft );
			Check( !ReferenceEquals( first, third ), "changed lights must recompute." );
			Check( cache.CachedKey == third.InvalidationKey, "cache key must track the latest result." );
			cache.Invalidate();
			Check( !cache.HasCached, "Invalidate must drop the entry." );
		} );

		registry.Add( "livelighting", "draft evaluates a 100-brush room in well under a second", () =>
		{
			var faces = new List<LiveFace>();
			for ( var i = 0; i < 100; i++ )
			{
				var min = new Vec3( (i % 10) * 72f, (i / 10) * 72f, 0f );
				faces.AddRange( BoxFaces( i * 6, min, new Vec3( 64f, 64f, 64f ) ) );
			}
			var lights = new[]
			{
				LiveLight.Point( 0, new Vec3( 320f, 320f, 200f ), Vec3.One, 2f ),
				LiveLight.Sun( 1, new Vec3( 0f, 0f, -1f ), Vec3.One, 0.5f ),
				LiveLight.Environment( 2, new Vec3( 0.2f, 0.2f, 0.25f ), 1f ),
			};
			var clock = Stopwatch.StartNew();
			var result = LiveLighting.Evaluate( faces, lights, LiveLightingQuality.Draft );
			clock.Stop();
			Check( result.Faces.Count == 600, $"100 brushes is 600 faces, got {result.Faces.Count}" );
			Check( result.LuxelCount == 600 * 16, $"64-unit faces at scale 16 are 4x4: {result.LuxelCount} luxels" );
			Check( result.MaxLuxel.x > 0f, "the room must not evaluate black." );
			Check( clock.ElapsedMilliseconds < 1000,
				$"draft 100-brush room took {clock.ElapsedMilliseconds} ms (budget 1000 ms)." );
		} );

		registry.Add( "livelighting", "luxels match the bake within tolerance on the analytic case", () =>
		{
			var face = SmallFloor();
			var light = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f );
			var result = LiveLighting.Evaluate( new[] { face }, new[] { light }, LiveLightingQuality.Draft );
			// The "bake" here is the analytic value for this exact setup (direct 1.0):
			// an independent derivation of the documented falloff, not this engine's output.
			var baked = new Dictionary<int, Vec3> { [0] = new Vec3( 1f, 1f, 1f ) };
			var comparison = LiveLighting.CompareToBake( result, baked, 0.05f );
			Check( comparison.WithinTolerance, $"max relative difference {comparison.MaxRelativeDifference} exceeds 0.05." );
			Check( Near( comparison.MaxRelativeDifference, 0f ), $"exact match expected: {comparison.MaxRelativeDifference}" );
			Check( comparison.ComparedFaceCount == 1, $"compared faces: {comparison.ComparedFaceCount}" );
			var perturbed = new Dictionary<int, Vec3> { [0] = new Vec3( 2f, 2f, 2f ) };
			var bad = LiveLighting.CompareToBake( result, perturbed, 0.05f );
			Check( !bad.WithinTolerance && Near( bad.MaxRelativeDifference, 0.5f ),
				$"a 2x bake must fail at 0.05: {bad.MaxRelativeDifference}" );
			var threw = false;
			try { LiveLighting.CompareToBake( result, new Dictionary<int, Vec3> { [99] = Vec3.One }, 0.05f ); }
			catch ( ArgumentException ) { threw = true; }
			Check( threw, "comparing with no shared face must throw." );
		} );

		registry.Add( "livelighting", "per-vertex colours fall off with distance and stay HDR", () =>
		{
			var face = BigFloor();
			var light = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 10f );
			var result = LiveLighting.Evaluate( new[] { face }, new[] { light }, LiveLightingQuality.Draft );
			var lit = result.Face( 0 )!;
			Check( lit.VertexColors.Count == 4, $"one colour per vertex: {lit.VertexColors.Count}" );
			var corner = lit.VertexColorAt( 0 );
			Check( corner.x < 0.5f, $"far corner vertex must be dim: {corner}" );
			Check( result.MaxLuxel.x > 1f, $"HDR: brightest luxel must exceed 1: {result.MaxLuxel}" );
			var centreLuxel = lit.LuxelAt( 3, 3 );
			Check( centreLuxel.x > corner.x, $"centre luxel {centreLuxel} must beat the corner vertex {corner}." );
		} );

		registry.Add( "livelighting", "light range cuts off distant faces", () =>
		{
			var face = SmallFloor();
			var ranged = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f, range: 32f );
			var cut = LiveLighting.Evaluate( new[] { face }, new[] { ranged }, LiveLightingQuality.Draft );
			Check( Near( cut.Face( 0 )!.Average, Vec3.Zero ), $"64 units away with range 32 must be dark: {cut.Face( 0 )!.Average}" );
			var infinite = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f );
			var lit = LiveLighting.Evaluate( new[] { face }, new[] { infinite }, LiveLightingQuality.Draft );
			Check( Near( lit.Face( 0 )!.Average, new Vec3( 1f, 1f, 1f ) ), $"range 0 means infinite: {lit.Face( 0 )!.Average}" );
		} );

		registry.Add( "livelighting", "invalid inputs throw naming the problem", () =>
		{
			var threw = false;
			try { LiveLighting.Evaluate( null!, new LiveLight[0], LiveLightingQuality.Draft ); }
			catch ( ArgumentNullException ) { threw = true; }
			Check( threw, "null faces must throw ArgumentNullException." );
			threw = false;
			try
			{
				var degenerate = new LiveFace( 0, new[] { new Vec3( 0f, 0f, 0f ), new Vec3( 1f, 0f, 0f ), new Vec3( 2f, 0f, 0f ) } );
				LiveLighting.Evaluate( new[] { degenerate }, new LiveLight[0], LiveLightingQuality.Draft );
			}
			catch ( ArgumentException ex ) { threw = ex.Message.Contains( "face 0" ); }
			Check( threw, "a zero-area face must throw naming the face." );
			threw = false;
			try { LiveLighting.Evaluate( new[] { SmallFloor( 0 ), SmallFloor( 0 ) }, new LiveLight[0], LiveLightingQuality.Draft ); }
			catch ( ArgumentException ex ) { threw = ex.Message.Contains( "duplicate" ); }
			Check( threw, "duplicate face ids must throw." );
			threw = false;
			try { _ = LiveLight.Point( 0, Vec3.Zero, Vec3.One, -1f ); }
			catch ( ArgumentException ex ) { threw = ex.Message.Contains( "intensity" ); }
			Check( threw, "negative intensity must throw naming intensity." );
			threw = false;
			try { _ = LiveLight.Spot( 0, Vec3.Zero, new Vec3( 0f, 0f, -1f ), Vec3.One, 1f, 30f, 10f ); }
			catch ( ArgumentException ex ) { threw = ex.Message.Contains( "inner cone" ); }
			Check( threw, "a spot with inner >= outer cone must throw." );
			threw = false;
			try { _ = new LiveFace( 0, new[] { new Vec3( 0f, 0f, 0f ), new Vec3( 1f, 0f, 0f ) } ); }
			catch ( ArgumentException ) { threw = true; }
			Check( threw, "a face with fewer than 3 vertices must throw." );
			var noLights = LiveLighting.Evaluate( new[] { SmallFloor() }, Array.Empty<LiveLight>(), LiveLightingQuality.Draft );
			Check( noLights.Warnings.Count == 1 && Near( noLights.Face( 0 )!.Average, Vec3.Zero ),
				"no lights warns and evaluates black instead of throwing." );
		} );

		registry.Add( "livelighting", "room fixture evaluates and floor beats ceiling, bake snapshot matches", () =>
		{
			var (faces, lights) = LoadRoom();
			Check( faces.Count == 6, $"room fixture has 6 faces, got {faces.Count}" );
			Check( lights.Count == 3, $"room fixture has 3 lights, got {lights.Count}" );
			var result = LiveLighting.Evaluate( faces, lights, LiveLightingQuality.Full );
			var floor = result.Face( 0 )!;
			var ceiling = result.Face( 1 )!;
			Check( floor.Average.x > ceiling.Average.x,
				$"floor {floor.Average} must be brighter than ceiling {ceiling.Average}." );
			Check( floor.Rows == 16 && floor.Cols == 16, $"floor grid: {floor.Rows}x{floor.Cols}" );
			Check( result.Warnings.Count == 0, $"room fixture must not warn: {string.Join( "; ", result.Warnings )}" );
			// Bake snapshot: recorded from this engine's Full evaluation of room.json.
			// If the file is missing, this throws carrying the values to record into it.
			string snapshotPath;
			try { snapshotPath = FindFixture( "bake-reference.json" ); }
			catch ( Exception )
			{
				var values = result.Faces.ToDictionary( f => f.FaceId.ToString(), f => new[] { f.Average.x, f.Average.y, f.Average.z } );
				throw new Exception( "bake-reference.json is missing; record these Full averages into it: " +
					JsonSerializer.Serialize( new Dictionary<string, object> { ["averages"] = values } ) );
			}
			var snapshot = JsonNode.Parse( File.ReadAllText( snapshotPath ) )!.AsObject();
			var baked = new Dictionary<int, Vec3>();
			foreach ( var pair in snapshot["averages"]!.AsObject() )
				baked[int.Parse( pair.Key )] = ReadVec3( pair.Value!.AsArray() );
			var comparison = LiveLighting.CompareToBake( result, baked, 0.05f );
			Check( comparison.WithinTolerance,
				$"snapshot mismatch: max relative difference {comparison.MaxRelativeDifference} exceeds 0.05." );
		} );
	}
}
