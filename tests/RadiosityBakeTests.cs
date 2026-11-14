#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Effigy;
using Effigy.Map;

// Hammer Desktop P3: native radiosity-quality bake (direct + bounced, AO, HDR,
// static-prop lighting). RadiosityBake.Bake(faces, lights, settings) -> per-face
// HDR luxel grids; bounce + AO + HDR + prop lighting + atlas packing + the GPU
// 1K test-bake trust rule.
//
// GROUND TRUTH. Every expected value here is analytic (derived by hand or from a
// textbook result), NOT a snapshot recorded from an engine's own output:
//   - point-light luxel: intensity * NdotL * 1/(1+(d/64)^2), computed by hand.
//   - sun HDR: overhead sun, intensity 4 -> luxel exactly 4 (above the LDR range).
//   - shadow: a plate between light and floor -> direct exactly 0.
//   - disk form factor: a differential element above a parallel disk of radius R
//     at distance S has form factor EXACTLY R^2/(R^2+S^2) (standard result).
//   - enclosure: form factors from one patch over a closed box sum to EXACTLY 1.
//   - parallel plates: compared against an independent fine-grid double integral
//     computed in the test (a commonly-quoted closed form for aligned parallel
//     plates was checked against that integral and did NOT reproduce it, so it is
//     deliberately not used — do not reintroduce it without verifying it first).
//   - two-room fixture: the unlit room's floor gets light ONLY from bounce
//     (no ambient in the fixture; three of its four luxels have no line of sight
//     to the light — see fixtures/hammer/hammer-radiosity-quality-bake/README.md).
// worker 23's bake-reference.json is a snapshot from LiveLighting's own Full
// evaluation, NOT an independent bake; these tests never read it. The one
// LiveLighting comparison uses the analytic single-light case (tolerance 0.05),
// same as LiveLighting's own cross-check.
//
// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned and
// the final integrator adds the one RadiosityBakeTests.Register(registry, settings);
// line. The group is "radiositybake".
internal static class RadiosityBakeTests
{
	static void Check( bool ok, string message ) { if ( !ok ) throw new Exception( message ); }
	static bool Near( float a, float b, float eps = 1e-4f ) => MathF.Abs( a - b ) <= eps;
	static bool Near( Vec3 a, Vec3 b, float eps = 1e-4f ) =>
		Near( a.x, b.x, eps ) && Near( a.y, b.y, eps ) && Near( a.z, b.z, eps );

	/// <summary>Direct-only settings: 0 bounces, AO off — the bake is exactly albedo*(direct+ambient).</summary>
	static RadiositySettings DirectOnly() => new() { Bounces = 0, AoRays = 0 };

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

	// --- Fake GPU backends (the trust-rule tests) ---

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
			result = ScaleResult( cpu, 2f );
			return true;
		}
	}

	sealed class DecliningGpuBackend : IRadiosityGpuBackend
	{
		public bool TryBake( IReadOnlyList<LiveFace> faces, IReadOnlyList<LiveLight> lights,
			RadiositySettings settings, out RadiosityResult result, out string why )
		{
			result = null!;
			why = "no GL context";
			return false;
		}
	}

	static RadiosityResult ScaleResult( RadiosityResult source, float factor )
	{
		var faces = new List<RadiosityFaceResult>();
		var samples = new List<RadiosityPatchSample>();
		foreach ( var f in source.Faces )
		{
			faces.Add( new RadiosityFaceResult( f.FaceId, f.Rows, f.Cols, f.LuxelSize,
				f.Luxels.Select( l => l * factor ).ToList(),
				f.DirectLuxels.Select( l => l * factor ).ToList(),
				f.IndirectLuxels.Select( l => l * factor ).ToList(),
				f.AoFactors.ToList(), f.SkippedNoDraw ) );
		}
		foreach ( var s in source.PatchSamples )
			samples.Add( new RadiosityPatchSample( s.FaceId, s.Centre, s.Normal, s.Area, s.Outgoing * factor ) );
		return new RadiosityResult( faces, samples, source.Quality, source.BouncesRun, source.Converged,
			source.ResidualEnergy, source.InvalidationKey, source.Warnings, "gpu", false, null );
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "radiositybake", "point light analytic direct value matches the documented falloff", () =>
		{
			// Face centre is 64 below the light: NdotL 1, falloff 1/(1+(64/64)^2) = 0.5.
			var face = SmallFloor();
			var light = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f );
			var result = RadiosityBake.Bake( new[] { face }, new[] { light }, DirectOnly() );
			Check( result.Succeeded, $"bake failed: {result.Error}" );
			var lit = result.Face( 0 )!;
			Check( lit.Rows == 1 && lit.Cols == 1, $"1x1 grid expected, got {lit.Rows}x{lit.Cols}" );
			Check( Near( lit.LuxelAt( 0, 0 ), new Vec3( 1f, 1f, 1f ) ), $"centre luxel: {lit.LuxelAt( 0, 0 )}" );
			Check( Near( lit.Average, new Vec3( 1f, 1f, 1f ) ), $"average: {lit.Average}" );
			Check( Near( lit.DirectLuxels[0], new Vec3( 1f, 1f, 1f ) ), $"direct luxel: {lit.DirectLuxels[0]}" );
			Check( Near( lit.IndirectLuxels[0], Vec3.Zero ), $"no ambient, no bounce: indirect must be zero, got {lit.IndirectLuxels[0]}" );
			Check( result.BouncesRun == 0, $"direct-only bake runs 0 bounces, ran {result.BouncesRun}" );
			Check( result.Route == "cpu", $"cpu route expected, got {result.Route}" );
		} );

		registry.Add( "radiositybake", "sun HDR values exceed the LDR range where the sun hits", () =>
		{
			var face = BigFloor();
			var sun = LiveLight.Sun( 0, new Vec3( 0f, 0f, -1f ), Vec3.One, 4f );
			var result = RadiosityBake.Bake( new[] { face }, new[] { sun }, DirectOnly() );
			Check( result.Succeeded, $"bake failed: {result.Error}" );
			Check( Near( result.Face( 0 )!.Average, new Vec3( 4f, 4f, 4f ), 1e-3f ), $"sunlit floor: {result.Face( 0 )!.Average}" );
			Check( result.MaxLuxel.x > 1f, $"HDR: max luxel {result.MaxLuxel} must exceed the LDR range (1.0)" );
			Check( Near( result.MaxLuxel, new Vec3( 4f, 4f, 4f ), 1e-3f ), $"max luxel: {result.MaxLuxel}" );
		} );

		registry.Add( "radiositybake", "direct light is shadow-tested: a plate between light and floor blocks it", () =>
		{
			var floor = SmallFloor( 0 );
			// Winding matters: these vertices must wind so Newell's normal is +Z
			// (facing the light above). An earlier draft wound them the other way
			// (normal -Z), so the "lit from above" assertion saw black; fixed 2026-11-13.
			var plate = new LiveFace( 1, new[]
			{
				new Vec3( 112f, 112f, 32f ), new Vec3( 144f, 112f, 32f ),
				new Vec3( 144f, 144f, 32f ), new Vec3( 112f, 144f, 32f ),
			} );
			var light = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f );
			var blocked = RadiosityBake.Bake( new[] { floor, plate }, new[] { light }, DirectOnly() );
			Check( blocked.Succeeded, $"bake failed: {blocked.Error}" );
			Check( Near( blocked.Face( 0 )!.Average, Vec3.Zero ), $"shadowed floor must be black: {blocked.Face( 0 )!.Average}" );
			Check( blocked.Face( 0 )!.DirectAverage.x == 0f, $"shadowed floor direct must be exactly 0: {blocked.Face( 0 )!.DirectAverage}" );
			// Control: without the plate the same floor is lit (value 1.0 from the analytic test).
			var open = RadiosityBake.Bake( new[] { floor }, new[] { light }, DirectOnly() );
			Check( Near( open.Face( 0 )!.Average, new Vec3( 1f, 1f, 1f ), 1e-3f ), $"unshadowed floor: {open.Face( 0 )!.Average}" );
			// The plate itself is lit from above (it faces the light).
			Check( blocked.Face( 1 )!.Average.x > 0.5f, $"plate lit from above: {blocked.Face( 1 )!.Average}" );
		} );

		registry.Add( "radiositybake", "two-room fixture: the unlit room gets bounced light, not direct light", () =>
		{
			var (faces, lights) = LoadScene( "two-room.json" );
			var settings = RadiositySettings.Preset( RadiosityQuality.Standard );
			settings.AoRays = 0;
			var result = RadiosityBake.Bake( faces, lights, settings );
			Check( result.Succeeded, $"bake failed: {result.Error}" );
			Check( result.BouncesRun == 3, $"3 bounces expected, ran {result.BouncesRun}" );
			var floorA = result.Face( 0 )!;
			var floorB = result.Face( 10 )!;
			// The three dark luxels: no direct (wall blocks the line of sight), non-zero indirect (bounce only — no ambient in this fixture).
			foreach ( var (row, col) in new[] { (0, 0), (0, 1), (1, 1) } )
			{
				Check( Near( floorB.DirectAt( row, col ), Vec3.Zero, 1e-4f ),
					$"room B floor luxel ({row},{col}) must have zero direct: {floorB.DirectAt( row, col )}" );
				Check( floorB.IndirectAt( row, col ).x > 0.01f,
					$"room B floor luxel ({row},{col}) must have bounced light: indirect {floorB.IndirectAt( row, col )}" );
				Check( floorB.LuxelAt( row, col ).x > 0.01f,
					$"room B floor luxel ({row},{col}) must be non-zero: {floorB.LuxelAt( row, col )}" );
			}
			// The doorway luxel (1,0) DOES see the light directly.
			Check( floorB.DirectAt( 1, 0 ).x > 0.05f, $"doorway luxel (1,0) direct: {floorB.DirectAt( 1, 0 )}" );
			// The lit room is brighter than the unlit room, and the unlit room is not black.
			Check( floorB.Average.x > 0.01f, $"room B floor average: {floorB.Average}" );
			Check( floorA.Average.x > floorB.Average.x * 2f,
				$"lit room {floorA.Average} must be brighter than unlit room {floorB.Average}" );
			Check( floorB.IndirectAverage.x > floorB.DirectAverage.x,
				$"room B floor is mostly indirect: indirect {floorB.IndirectAverage} vs direct {floorB.DirectAverage}" );
		} );

		registry.Add( "radiositybake", "more bounces refine the unlit room and the residual shrinks", () =>
		{
			var (faces, lights) = LoadScene( "two-room.json" );
			RadiosityResult BakeBounces( int bounces )
			{
				var s = RadiositySettings.Preset( RadiosityQuality.Standard );
				s.AoRays = 0;
				s.Bounces = bounces;
				return RadiosityBake.Bake( faces, lights, s );
			}
			var zero = BakeBounces( 0 );
			var one = BakeBounces( 1 );
			var three = BakeBounces( 3 );
			Check( zero.Succeeded && one.Succeeded && three.Succeeded, "all three bakes must succeed" );
			Check( zero.BouncesRun == 0 && one.BouncesRun == 1 && three.BouncesRun == 3,
				$"bounce runs: 0/1/3 expected, got {zero.BouncesRun}/{one.BouncesRun}/{three.BouncesRun}" );
			var dark0 = zero.Face( 10 )!.Average.x;
			var dark1 = one.Face( 10 )!.Average.x;
			var dark3 = three.Face( 10 )!.Average.x;
			// 0 bounces is NOT black: the doorway luxel (1,0) sees the light directly
			// (the earlier test asserts its direct > 0.05), so the 0-bounce average is
			// exactly the direct average — one lit luxel out of four — and the
			// indirect average is exactly zero. (An earlier draft asserted dark0 == 0,
			// which contradicts the doorway direct; fixed 2026-11-13.)
			Check( Near( dark0, zero.Face( 10 )!.DirectAverage.x, 1e-4f ),
				$"0 bounces: unlit room floor average {dark0} must equal its direct average {zero.Face( 10 )!.DirectAverage.x}" );
			Check( dark0 > 0.01f, $"0 bounces: doorway direct must keep the floor non-zero, got {dark0}" );
			Check( Near( zero.Face( 10 )!.IndirectAverage.x, 0f ),
				$"0 bounces: indirect must be exactly zero, got {zero.Face( 10 )!.IndirectAverage.x}" );
			Check( dark1 > 0.01f, $"1 bounce: unlit room floor {dark1}" );
			Check( dark3 > dark1, $"3 bounces must add more light than 1 ({dark3} vs {dark1})" );
			Check( three.ResidualEnergy < one.ResidualEnergy,
				$"residual must shrink with bounces ({three.ResidualEnergy} vs {one.ResidualEnergy})" );
		} );

		registry.Add( "radiositybake", "disk form factor matches the exact R^2/(R^2+S^2) result", () =>
		{
			// A differential element above the centre of a parallel disk of radius R at
			// distance S has form factor EXACTLY R^2/(R^2+S^2) = 0.2 for R=32, S=64.
			// Sum the public differential form factor over small patches of the disk.
			const float radius = 32f;
			const float distance = 64f;
			const float cell = 2f;
			var from = new Vec3( 0f, 0f, distance );
			var fromNormal = new Vec3( 0f, 0f, -1f );
			var toNormal = new Vec3( 0f, 0f, 1f );
			var sum = 0f;
			var cells = 0;
			for ( var x = -radius + cell / 2; x < radius; x += cell )
			{
				for ( var y = -radius + cell / 2; y < radius; y += cell )
				{
					if ( x * x + y * y > radius * radius ) continue;
					sum += RadiosityBake.FormFactor( from, fromNormal, new Vec3( x, y, 0f ), toNormal, cell * cell );
					cells++;
				}
			}
			Check( cells > 0, "disk must contain patches" );
			var exact = radius * radius / (radius * radius + distance * distance);
			Check( MathF.Abs( sum - exact ) <= 0.01f, $"disk form factor: summed {sum:0.####} vs exact {exact:0.####} (cells {cells})" );
		} );

		registry.Add( "radiositybake", "enclosure invariant: form factors from one patch over a closed box sum to 1", () =>
		{
			// Energy conservation: in a sealed box every ray leaving a patch hits a face.
			var faces = InwardBoxFaces( 0, Vec3.Zero, new Vec3( 128f, 128f, 128f ), 8f ).ToList();
			var floorCentre = new Vec3( 60f, 60f, 0f ); // centre of a central floor patch (8-unit grid)
			var floorNormal = new Vec3( 0f, 0f, 1f );
			var sum = 0f;
			foreach ( var face in faces )
			{
				if ( face.Id == 0 ) continue; // coplanar with the shooter: cos = 0
				var (uAxis, vAxis) = FaceUv.PlaneAxes( face.Normal );
				var (uMin, uMax, vMin, vMax) = FaceUv.ProjectedBounds( face.Vertices, face.Normal );
				var cols = (int)MathF.Ceiling( (uMax - uMin) / face.LightmapScale );
				var rows = (int)MathF.Ceiling( (vMax - vMin) / face.LightmapScale );
				var area = 64f; // 8x8 patches
				for ( var row = 0; row < rows; row++ )
				{
					for ( var col = 0; col < cols; col++ )
					{
						var u = uMin + (col + 0.5f) * (uMax - uMin) / cols;
						var v = vMin + (row + 0.5f) * (vMax - vMin) / rows;
						var centre = face.Vertices[0] + uAxis * u + vAxis * v;
						sum += RadiosityBake.FormFactor( floorCentre, floorNormal, centre, face.Normal, area );
					}
				}
			}
			Check( MathF.Abs( sum - 1f ) <= 0.05f, $"enclosure row sum: {sum:0.####} (must be ~1)" );
		} );

		registry.Add( "radiositybake", "parallel plates form factor matches an independent fine-grid double integral", () =>
		{
			var (faces, _) = LoadScene( "parallel-plates.json" );
			var plateA = faces[0];
			var plateB = faces[1];
			// Code path: average over plate A's patches of the summed public FormFactor to plate B's patches.
			float RowSum( LiveFace to, Vec3 point, Vec3 normal )
			{
				var (uAxis, vAxis) = FaceUv.PlaneAxes( to.Normal );
				var (uMin, uMax, vMin, vMax) = FaceUv.ProjectedBounds( to.Vertices, to.Normal );
				var cols = (int)MathF.Ceiling( (uMax - uMin) / to.LightmapScale );
				var rows = (int)MathF.Ceiling( (vMax - vMin) / to.LightmapScale );
				var area = (uMax - uMin) / cols * ((vMax - vMin) / rows);
				var total = 0f;
				for ( var row = 0; row < rows; row++ )
				{
					for ( var col = 0; col < cols; col++ )
					{
						var u = uMin + (col + 0.5f) * (uMax - uMin) / cols;
						var v = vMin + (row + 0.5f) * (vMax - vMin) / rows;
						total += RadiosityBake.FormFactor( point, normal, to.Vertices[0] + uAxis * u + vAxis * v, to.Normal, area );
					}
				}
				return total;
			}
			void PatchCentres( LiveFace face, List<(Vec3 point, Vec3 normal)> @out )
			{
				var (uAxis, vAxis) = FaceUv.PlaneAxes( face.Normal );
				var (uMin, uMax, vMin, vMax) = FaceUv.ProjectedBounds( face.Vertices, face.Normal );
				var cols = (int)MathF.Ceiling( (uMax - uMin) / face.LightmapScale );
				var rows = (int)MathF.Ceiling( (vMax - vMin) / face.LightmapScale );
				for ( var row = 0; row < rows; row++ )
					for ( var col = 0; col < cols; col++ )
					{
						var u = uMin + (col + 0.5f) * (uMax - uMin) / cols;
						var v = vMin + (row + 0.5f) * (vMax - vMin) / rows;
						@out.Add( (face.Vertices[0] + uAxis * u + vAxis * v, face.Normal) );
					}
			}
			var centresA = new List<(Vec3 point, Vec3 normal)>();
			PatchCentres( plateA, centresA );
			var codeSum = 0f;
			foreach ( var (point, normal) in centresA )
				codeSum += RowSum( plateB, point, normal );
			var codeAverage = codeSum / centresA.Count;
			// Independent reference: the defining double integral, midpoint rule at
			// 2-unit cells, computed directly here (no FormFactor call).
			const float cell = 2f;
			var refSum = 0f;
			var refCount = 0;
			for ( var ax = -32f + cell / 2; ax < 32f; ax += cell )
			{
				for ( var ay = -32f + cell / 2; ay < 32f; ay += cell )
				{
					var row = 0f;
					for ( var bx = -32f + cell / 2; bx < 32f; bx += cell )
					{
						for ( var by = -32f + cell / 2; by < 32f; by += cell )
						{
							var dx = bx - ax;
							var dy = by - ay;
							var dz = 64f;
							var r2 = dx * dx + dy * dy + dz * dz;
							var cos = dz / MathF.Sqrt( r2 );
							row += cos * cos * cell * cell / (MathF.PI * r2);
						}
					}
					refSum += row;
					refCount++;
				}
			}
			var reference = refSum / refCount;
			Check( MathF.Abs( codeAverage - reference ) <= 0.02f,
				$"parallel plates: code {codeAverage:0.####} vs fine-grid reference {reference:0.####}" );
		} );

		registry.Add( "radiositybake", "AO is exactly 1 in an open field (no occluders)", () =>
		{
			var face = BigFloor();
			var sun = LiveLight.Sun( 0, new Vec3( 0f, 0f, -1f ), Vec3.One, 1f );
			var settings = new RadiositySettings { Bounces = 0, AoRays = 32, AoDistance = 128f, AoStrength = 0.8f };
			var result = RadiosityBake.Bake( new[] { face }, new[] { sun }, settings );
			Check( result.Succeeded, $"bake failed: {result.Error}" );
			var lit = result.Face( 0 )!;
			Check( lit.AoFactors.All( f => f == 1f ), $"open-field AO factors must be exactly 1, got min {lit.AoFactors.Min():0.###} max {lit.AoFactors.Max():0.###}" );
			Check( Near( lit.MeanAoFactor, 1f ), $"mean AO: {lit.MeanAoFactor}" );
			Check( Near( lit.Average, new Vec3( 1f, 1f, 1f ), 1e-3f ), $"open floor under unit sun: {lit.Average}" );
		} );

		registry.Add( "radiositybake", "AO darkens corners more than the centre, vs the no-AO bake", () =>
		{
			var faces = InwardBoxFaces( 0, Vec3.Zero, new Vec3( 128f, 128f, 128f ), 32f ).ToList();
			var lights = new[]
			{
				LiveLight.Point( 0, new Vec3( 64f, 64f, 96f ), Vec3.One, 2f ),
				LiveLight.Environment( 1, new Vec3( 0.5f, 0.5f, 0.5f ), 1f ),
			};
			var noAoSettings = new RadiositySettings { Bounces = 1, AoRays = 0 };
			var aoSettings = new RadiositySettings { Bounces = 1, AoRays = 32, AoDistance = 128f, AoStrength = 0.8f };
			var noAo = RadiosityBake.Bake( faces, lights, noAoSettings );
			var withAo = RadiosityBake.Bake( faces, lights, aoSettings );
			Check( noAo.Succeeded && withAo.Succeeded, "both bakes must succeed" );
			var floorNoAo = noAo.Face( 0 )!;
			var floorAo = withAo.Face( 0 )!;
			// Floor is 4x4: corner luxel (0,0) centre (16,16), centre luxel (1,1) centre (48,48).
			var cornerNoAo = floorNoAo.LuxelAt( 0, 0 ).x;
			var centreNoAo = floorNoAo.LuxelAt( 1, 1 ).x;
			var cornerAo = floorAo.LuxelAt( 0, 0 ).x;
			var centreAo = floorAo.LuxelAt( 1, 1 ).x;
			Check( cornerAo < cornerNoAo, $"AO must darken the corner ({cornerAo:0.###} vs {cornerNoAo:0.###})" );
			Check( cornerAo / centreAo < cornerNoAo / centreNoAo,
				$"AO must darken the corner MORE than the centre (ratio {cornerAo / centreAo:0.###} vs {cornerNoAo / centreNoAo:0.###})" );
			Check( floorAo.MeanAoFactor < 1f, $"room mean AO must be below 1: {floorAo.MeanAoFactor}" );
			Check( floorAo.AoFactors.All( f => f >= 0f && f <= 1f ), "AO factors must stay in [0,1]" );
			Check( floorNoAo.AoFactors.All( f => f == 1f ), "AO off means every factor is exactly 1" );
		} );

		registry.Add( "radiositybake", "prop vertex under a point light gets the exact direct value", () =>
		{
			var face = BigFloor();
			var sun = LiveLight.Sun( 0, new Vec3( 0f, 0f, -1f ), Vec3.One, 1.5f );
			var settings = new RadiositySettings { Bounces = 0, AoRays = 0, PropSampleRadius = 8f };
			var baked = RadiosityBake.Bake( new[] { face }, new[] { sun }, settings );
			Check( baked.Succeeded, $"bake failed: {baked.Error}" );
			// Vertex high above the floor, outside the prop sample radius: no indirect.
			var vertex = new RadiosityPropVertex( new Vec3( 128f, 128f, 64f ), new Vec3( 0f, 0f, 1f ) );
			var lit = RadiosityBake.LightPropVertices( baked, new[] { vertex }, new[] { sun }, new[] { face }, settings );
			Check( Near( lit[0], new Vec3( 1.5f, 1.5f, 1.5f ), 1e-3f ), $"prop vertex under overhead sun: {lit[0]}" );
		} );

		registry.Add( "radiositybake", "prop vertex in the unlit room gets indirect light but no direct light", () =>
		{
			var (faces, lights) = LoadScene( "two-room.json" );
			var settings = RadiositySettings.Preset( RadiosityQuality.Standard );
			settings.AoRays = 0;
			settings.PropSampleRadius = 128f;
			var baked = RadiosityBake.Bake( faces, lights, settings );
			Check( baked.Succeeded, $"bake failed: {baked.Error}" );
			// Just above the dark south-west luxel of room B's floor: the light in room A
			// cannot reach it directly (wall blocks), but bounced light can.
			var vertex = new RadiosityPropVertex( new Vec3( 160f, 32f, 8f ), new Vec3( 0f, 0f, 1f ) );
			var lit = RadiosityBake.LightPropVertices( baked, new[] { vertex }, lights, faces, settings );
			Check( lit[0].x > 0.005f, $"shadowed prop vertex must still get bounced light: {lit[0]}" );
			// Direct alone would be ~0: the vertex colour must be dominated by indirect,
			// so it is far dimmer than a vertex standing in room A under the light.
			var litVertex = new RadiosityPropVertex( new Vec3( 32f, 32f, 8f ), new Vec3( 0f, 0f, 1f ) );
			var litInA = RadiosityBake.LightPropVertices( baked, new[] { litVertex }, lights, faces, settings );
			Check( litInA[0].x > lit[0].x * 3f, $"room A vertex {litInA[0]} must be much brighter than room B vertex {lit[0]}" );
		} );

		registry.Add( "radiositybake", "atlas pack places charts exactly with padding and no overlap", () =>
		{
			var charts = new[]
			{
				new AtlasChart( 0, 8, 4 ),
				new AtlasChart( 1, 4, 4 ),
				new AtlasChart( 2, 16, 16 ),
			};
			var packed = RadiosityAtlasPacker.Pack( charts, 32, 2 );
			Check( packed.PageCount == 1, $"one page expected, got {packed.PageCount}" );
			// Sorted by rows desc: face 2 (16), face 0 (4 rows, 8 cols), face 1 (4x4).
			var p2 = packed.PlacementFor( 2 )!;
			var p0 = packed.PlacementFor( 0 )!;
			var p1 = packed.PlacementFor( 1 )!;
			Check( p2.X == 2 && p2.Y == 2 && p2.Page == 0, $"face 2 at (2,2): got ({p2.X},{p2.Y}) page {p2.Page}" );
			Check( p0.X == 20 && p0.Y == 2, $"face 0 at (20,2): got ({p0.X},{p0.Y})" );
			Check( p1.X == 2 && p1.Y == 20, $"face 1 at (2,20): got ({p1.X},{p1.Y})" );
			Check( Near( p2.U0, 2f / 32f ) && Near( p2.U1, 18f / 32f ) && Near( p2.V0, 2f / 32f ) && Near( p2.V1, 18f / 32f ),
				$"face 2 UV rect: ({p2.U0:0.###},{p2.V0:0.###})-({p2.U1:0.###},{p2.V1:0.###})" );
			// No two charts (plus their gutters) overlap, and all stay in bounds.
			var placed = packed.Placements.ToList();
			for ( var i = 0; i < placed.Count; i++ )
			{
				for ( var j = i + 1; j < placed.Count; j++ )
				{
					var a = placed[i];
					var b = placed[j];
					if ( a.Page != b.Page ) continue;
					var overlapX = a.X < b.X + b.Cols + 2 && b.X < a.X + a.Cols + 2;
					var overlapY = a.Y < b.Y + b.Rows + 2 && b.Y < a.Y + a.Rows + 2;
					Check( !(overlapX && overlapY), $"charts {a.FaceId} and {b.FaceId} overlap (gutter included)" );
				}
				Check( placed[i].X + placed[i].Cols <= 32 - 2 && placed[i].Y + placed[i].Rows <= 32 - 2,
					$"chart {placed[i].FaceId} leaves the atlas bounds" );
			}
			Check( packed.Utilization > 0f && packed.Utilization <= 1f, $"utilization: {packed.Utilization}" );
		} );

		registry.Add( "radiositybake", "atlas pack spills to more pages and reports utilization", () =>
		{
			var charts = new[]
			{
				new AtlasChart( 0, 16, 16 ),
				new AtlasChart( 1, 16, 16 ),
				new AtlasChart( 2, 16, 16 ),
				new AtlasChart( 3, 16, 16 ),
			};
			var packed = RadiosityAtlasPacker.Pack( charts, 32, 2 );
			// A 16-wide chart with a 2-texel gutter needs 2 + 16 + 2 = 20 texels
			// before the next chart, so a 32 atlas holds exactly ONE 16x16 chart
			// per shelf and one shelf per page: 4 charts spill to 4 pages.
			// (An earlier draft expected 2 pages / 2 charts per page, forgetting
			// the gutter at the page edges as well as between charts; fixed 2026-11-13.)
			Check( packed.PageCount == 4, $"four pages expected, got {packed.PageCount}" );
			Check( packed.PlacementFor( 0 )!.Page == 0 && packed.PlacementFor( 1 )!.Page == 1 &&
				packed.PlacementFor( 2 )!.Page == 2 && packed.PlacementFor( 3 )!.Page == 3,
				"one chart per page expected" );
			Check( Near( packed.Utilization, 0.25f, 1e-4f ), $"utilization: {packed.Utilization} (4x256 texels on 4x1024)" );
		} );

		registry.Add( "radiositybake", "GPU bake without a backend is refused with a named reason", () =>
		{
			var face = SmallFloor();
			var light = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f );
			var settings = DirectOnly();
			var trust = RadiosityBake.VerifyGpuTrust( new ExactGpuBackend(), new[] { face }, new[] { light }, settings );
			Check( trust.Passed, $"exact backend must pass trust: {trust.Reason}" );
			var saved = RadiosityBake.GpuBackend;
			try
			{
				RadiosityBake.GpuBackend = null!;
				var result = RadiosityBake.BakeGpu( new[] { face }, new[] { light }, settings, trust );
				Check( !result.Succeeded, "GPU bake without a backend must fail" );
				Check( result.Error is not null && result.Error.Contains( "GpuBackendMissing" ),
					$"refusal must name GpuBackendMissing: {result.Error}" );
			}
			finally { RadiosityBake.GpuBackend = saved; }
		} );

		registry.Add( "radiositybake", "GPU bake without a trust verification is refused with a named reason", () =>
		{
			var face = SmallFloor();
			var light = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f );
			var result = RadiosityBake.BakeGpu( new[] { face }, new[] { light }, DirectOnly(), null! );
			Check( !result.Succeeded, "GPU bake without trust must fail" );
			Check( result.Error is not null && result.Error.Contains( "GpuTrustNotVerified" ),
				$"refusal must name GpuTrustNotVerified: {result.Error}" );
		} );

		registry.Add( "radiositybake", "diverging GPU backend fails the 1K test-bake and the GPU bake is refused", () =>
		{
			var face = SmallFloor();
			var light = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f );
			var trust = RadiosityBake.VerifyGpuTrust( new DivergingGpuBackend(), new[] { face }, new[] { light }, DirectOnly() );
			Check( !trust.Passed, "a backend returning 2x luxels must fail trust" );
			Check( trust.Reason.Contains( "GpuDiverged" ), $"trust failure must name GpuDiverged: {trust.Reason}" );
			Check( trust.WorstDeviation > 0.05f, $"worst deviation: {trust.WorstDeviation}" );
			var result = RadiosityBake.BakeGpu( new[] { face }, new[] { light }, DirectOnly(), trust );
			Check( !result.Succeeded, "GPU bake with a failed trust token must fail" );
			Check( result.Error is not null && result.Error.Contains( "GpuTrustNotVerified" ),
				$"refusal must name GpuTrustNotVerified: {result.Error}" );
		} );

		registry.Add( "radiositybake", "exact GPU backend passes trust and the GPU bake takes the gpu route", () =>
		{
			var face = SmallFloor();
			var light = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f );
			var settings = DirectOnly();
			var trust = RadiosityBake.VerifyGpuTrust( new ExactGpuBackend(), new[] { face }, new[] { light }, settings );
			Check( trust.Passed, $"exact backend must pass trust: {trust.Reason}" );
			Check( trust.WorstDeviation == 0f, $"exact backend deviation must be 0: {trust.WorstDeviation}" );
			var saved = RadiosityBake.GpuBackend;
			try
			{
				RadiosityBake.GpuBackend = new ExactGpuBackend();
				var result = RadiosityBake.BakeGpu( new[] { face }, new[] { light }, settings, trust );
				Check( result.Succeeded, $"GPU bake failed: {result.Error}" );
				Check( result.Route == "gpu", $"gpu route expected, got {result.Route}" );
				Check( Near( result.Face( 0 )!.Average, new Vec3( 1f, 1f, 1f ), 1e-3f ), $"gpu luxel: {result.Face( 0 )!.Average}" );
			}
			finally { RadiosityBake.GpuBackend = saved; }
		} );

		registry.Add( "radiositybake", "GPU backend declining the final bake falls back to CPU and says so", () =>
		{
			var face = SmallFloor();
			var light = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f );
			var settings = DirectOnly();
			var trust = RadiosityBake.VerifyGpuTrust( new ExactGpuBackend(), new[] { face }, new[] { light }, settings );
			Check( trust.Passed, $"exact backend must pass trust: {trust.Reason}" );
			var saved = RadiosityBake.GpuBackend;
			try
			{
				RadiosityBake.GpuBackend = new DecliningGpuBackend();
				var result = RadiosityBake.BakeGpu( new[] { face }, new[] { light }, settings, trust );
				Check( result.Succeeded, $"declined GPU bake must fall back to CPU: {result.Error}" );
				Check( result.Route.StartsWith( "cpu (gpu declined", StringComparison.Ordinal ),
					$"route must say the GPU declined: {result.Route}" );
				Check( result.Route.Contains( "no GL context" ), $"route must carry the decline reason: {result.Route}" );
				Check( Near( result.Face( 0 )!.Average, new Vec3( 1f, 1f, 1f ), 1e-3f ), $"fallback luxel: {result.Face( 0 )!.Average}" );
			}
			finally { RadiosityBake.GpuBackend = saved; }
		} );

		registry.Add( "radiositybake", "invalidation key changes with inputs and is stable under reordering", () =>
		{
			var face = SmallFloor();
			var light = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f );
			var movedLight = LiveLight.Point( 0, new Vec3( 120f, 128f, 64f ), Vec3.One, 2f );
			var faces = new[] { face };
			var lights = new[] { light };
			var settings = new RadiositySettings();
			var key = RadiosityBake.ComputeInvalidationKey( faces, lights, settings );
			var faces2 = new[] { SmallFloor( 0 ), SmallFloor( 1 ) };
			var key2 = RadiosityBake.ComputeInvalidationKey( faces2, lights, settings );
			var key2Reordered = RadiosityBake.ComputeInvalidationKey( faces2.Reverse().ToArray(), lights, settings );
			Check( key2 == key2Reordered, "reordering faces must not change the key" );
			Check( RadiosityBake.ComputeInvalidationKey( faces2, new[] { movedLight }, settings ) != key2,
				"moving a light must change the key" );
			var otherSettings = new RadiositySettings { Bounces = 5 };
			Check( RadiosityBake.ComputeInvalidationKey( faces, lights, otherSettings ) != key,
				"changing settings must change the key" );
			var darker = new LiveFace( 0, face.Vertices, face.Material, new Vec3( 0.5f, 0.5f, 0.5f ) );
			Check( RadiosityBake.ComputeInvalidationKey( new[] { darker }, lights, settings ) != key,
				"changing albedo must change the key" );
			Check( RadiosityBake.ComputeTrustKey( faces, lights ) != key,
				"the trust key (scene only) must differ from the bake key (scene + settings)" );
		} );

		registry.Add( "radiositybake", "PlanRebake flags the changed face and what it can see, nothing sealed away", () =>
		{
			var boxA = InwardBoxFaces( 0, Vec3.Zero, new Vec3( 128f, 128f, 128f ) ).ToList();
			var boxB = InwardBoxFaces( 100, new Vec3( 256f, 0f, 0f ), new Vec3( 128f, 128f, 128f ) ).ToList();
			var faces = boxA.Concat( boxB ).ToList();
			var flagged = RadiosityBake.PlanRebake( faces, new[] { 0 }, lightsChanged: false );
			Check( flagged.Count == 6, $"changing box A floor must flag exactly box A's 6 faces, got {string.Join( ",", flagged )}" );
			foreach ( var id in Enumerable.Range( 0, 6 ) )
				Check( flagged.Contains( id ), $"box A face {id} must be flagged" );
			foreach ( var id in Enumerable.Range( 100, 6 ) )
				Check( !flagged.Contains( id ), $"sealed-away box B face {id} must NOT be flagged" );
			var all = RadiosityBake.PlanRebake( faces, Array.Empty<int>(), lightsChanged: true );
			Check( all.Count == 12, $"a light change re-bakes everything (12 faces), got {all.Count}" );
			var none = RadiosityBake.PlanRebake( faces, Array.Empty<int>(), lightsChanged: false );
			Check( none.Count == 0, "no change means no re-bake" );
		} );

		registry.Add( "radiositybake", "direct-only bake agrees with the LiveLighting preview on an unoccluded scene", () =>
		{
			var nearCentre = SmallFloor( 0 );
			var nearCorner = new LiveFace( 1, new[]
			{
				new Vec3( 8f, 8f, 0f ), new Vec3( 24f, 8f, 0f ),
				new Vec3( 24f, 24f, 0f ), new Vec3( 8f, 24f, 0f ),
			} );
			var faces = new[] { nearCentre, nearCorner };
			var light = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f );
			var preview = LiveLighting.Evaluate( faces, new[] { light }, LiveLightingQuality.Draft );
			var baked = RadiosityBake.Bake( faces, new[] { light }, DirectOnly() );
			Check( baked.Succeeded, $"bake failed: {baked.Error}" );
			foreach ( var face in faces )
			{
				var previewAvg = preview.Face( face.Id )!.Average;
				var bakedAvg = baked.Face( face.Id )!.Average;
				var worst = 0f;
				var p = new[] { previewAvg.x, previewAvg.y, previewAvg.z };
				var b = new[] { bakedAvg.x, bakedAvg.y, bakedAvg.z };
				for ( var i = 0; i < 3; i++ )
					worst = MathF.Max( worst, MathF.Abs( p[i] - b[i] ) / MathF.Max( MathF.Abs( b[i] ), 0.01f ) );
				Check( worst <= 0.05f, $"face {face.Id}: preview {previewAvg} vs bake {bakedAvg} (rel diff {worst:0.###})" );
			}
		} );

		registry.Add( "radiositybake", "nodraw faces are skipped, not lit", () =>
		{
			var floor = SmallFloor( 0 );
			var nodraw = new LiveFace( 1, new[]
			{
				new Vec3( 120f, 120f, 0f ), new Vec3( 136f, 120f, 0f ),
				new Vec3( 136f, 136f, 0f ), new Vec3( 120f, 136f, 0f ),
			}, new FaceMaterial( "tools/toolsnodraw", FaceUv.Default ), Vec3.One );
			var light = LiveLight.Point( 0, new Vec3( 128f, 128f, 64f ), Vec3.One, 2f );
			var result = RadiosityBake.Bake( new[] { floor, nodraw }, new[] { light }, DirectOnly() );
			Check( result.Succeeded, $"bake failed: {result.Error}" );
			var skipped = result.Face( 1 )!;
			Check( skipped.SkippedNoDraw, "nodraw face must be skipped" );
			Check( skipped.Luxels.Count == 0 && skipped.Rows == 0 && skipped.Cols == 0, "skipped face has an empty grid" );
			Check( Near( skipped.Average, Vec3.Zero ), $"skipped face average: {skipped.Average}" );
			Check( result.LuxelCount == 1, $"only the real floor contributes luxels: {result.LuxelCount}" );
		} );

		registry.Add( "radiositybake", "invalid inputs throw with the face or setting named", () =>
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
			Throws<ArgumentException>( () => RadiosityBake.Bake( new[] { face, SmallFloor( 0 ) }, new[] { light }, DirectOnly() ), "duplicate face id 0" );
			var degenerate = new LiveFace( 9, new[] { new Vec3( 0f, 0f, 0f ), new Vec3( 1f, 0f, 0f ), new Vec3( 2f, 0f, 0f ) } );
			Throws<ArgumentException>( () => RadiosityBake.Bake( new[] { degenerate }, new[] { light }, DirectOnly() ), "face 9 is zero-area" );
			Throws<ArgumentException>( () => RadiosityBake.Bake( new[] { face }, new[] { light }, new RadiositySettings { Bounces = -1 } ), "Bounces -1" );
			Throws<ArgumentException>( () => RadiosityBake.Bake( new[] { face }, new[] { light }, new RadiositySettings { Bounces = 9 } ), "Bounces 9" );
			Throws<ArgumentException>( () => RadiosityBake.Bake( new[] { face }, new[] { light }, new RadiositySettings { AoStrength = 2f } ), "AoStrength 2" );
			Throws<ArgumentException>( () => RadiosityBake.Bake( new[] { face }, new[] { light }, new RadiositySettings { MaxGridPerAxis = 0 } ), "MaxGridPerAxis 0" );
			Throws<ArgumentNullException>( () => RadiosityBake.Bake( null!, new[] { light }, DirectOnly() ), "faces" );
			Throws<ArgumentException>( () => RadiosityAtlasPacker.Pack( new[] { new AtlasChart( 0, 64, 64 ) }, 32 ), "larger than the 32 atlas" );
			Throws<ArgumentException>( () => RadiosityBake.ToneMap( Vec3.One, 0f ), "exposure 0" );
		} );

		registry.Add( "radiositybake", "identical bakes are bit-for-bit deterministic", () =>
		{
			var (faces, lights) = LoadScene( "two-room.json" );
			var settings = RadiositySettings.Preset( RadiosityQuality.Standard );
			settings.AoRays = 16;
			var first = RadiosityBake.Bake( faces, lights, settings );
			var second = RadiosityBake.Bake( faces, lights, settings );
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

		registry.Add( "radiositybake", "cancelling before the bake returns Cancelled, not a throw", () =>
		{
			var (faces, lights) = LoadScene( "two-room.json" );
			using var source = new System.Threading.CancellationTokenSource();
			source.Cancel();
			var result = RadiosityBake.Bake( faces, lights, RadiositySettings.Preset( RadiosityQuality.Standard ), null, source.Token );
			Check( result.Cancelled, "a pre-cancelled bake must report Cancelled" );
			Check( !result.Succeeded, "a cancelled bake must not succeed" );
			Check( result.Faces.Count == 0, "a cancelled bake has no faces" );
		} );

		registry.Add( "radiositybake", "Reinhard tone mapping stays in range and is monotonic", () =>
		{
			var dark = RadiosityBake.ToneMap( new Vec3( 1f, 1f, 1f ) );
			var bright = RadiosityBake.ToneMap( new Vec3( 4f, 4f, 4f ) );
			Check( Near( RadiosityBake.ToneMap( Vec3.Zero ), Vec3.Zero ), "tone map of black is black" );
			Check( bright.x < 1f && bright.x > dark.x, $"tone map: 4.0 -> {bright.x:0.###}, 1.0 -> {dark.x:0.###} (must be < 1 and monotonic)" );
			Check( Near( dark, new Vec3( 0.5f, 0.5f, 0.5f ) ), $"Reinhard(1.0) is 0.5: {dark}" );
			Check( Near( bright, new Vec3( 0.8f, 0.8f, 0.8f ) ), $"Reinhard(4.0) is 0.8: {bright}" );
		} );

		registry.Add( "radiositybake", "fixtures load: two-room and parallel-plates scenes parse", () =>
		{
			var (roomFaces, roomLights) = LoadScene( "two-room.json" );
			Check( roomFaces.Count == 16, $"two-room faces: {roomFaces.Count}" );
			Check( roomLights.Count == 1, $"two-room lights: {roomLights.Count}" );
			Check( roomFaces.All( f => f.Normal.LengthSquared > 1e-12f ), "every two-room face must have a real normal" );
			var (plateFaces, plateLights) = LoadScene( "parallel-plates.json" );
			Check( plateFaces.Count == 2, $"parallel-plates faces: {plateFaces.Count}" );
			Check( plateLights.Count == 0, "parallel-plates has no lights" );
			Check( plateFaces[0].Normal.z > 0.99f && plateFaces[1].Normal.z < -0.99f, "plates must face each other" );
		} );
	}
}
