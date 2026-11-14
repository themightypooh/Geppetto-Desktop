using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

/// <summary>Finish: decimate + auto-UV/packing/texel-density + pre-export validation
/// (godlike-finish-decimate-auto-uv-validation, Wave 2 P1).
/// Fixtures are built directly as PolyMesh, like MeshDoctorRetopoTests. The unit cube
/// below is outward-wound (signed volume +8); the open cube omits the top face. All
/// fixture faces carry non-zero UVs unless the test is about UVs, because a face with
/// all-zero UVs is reported as UV-missing by design.
/// REGISTRATION (integrator step, TestRegistry.cs left untouched to avoid clobbering
/// concurrent workers): add FinishDecimateTests.Register( registry, settings );
/// to DesktopTestHost.Run, after DecimationProjectionTests.Register.</summary>
internal static class FinishDecimateTests
{
	static void Check( bool ok, string message ) { if ( !ok ) throw new Exception( message ); }

	static Vec2[] QuadUvs() => new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) };
	static Vec2[] TriUvs() => new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 0, 1 ) };

	static Vec3 V( float x, float y, float z ) => new( x, y, z );

	/// <summary>Each face gets its own small UV square in a 3x2 atlas (with gutters),
	/// so the cube's UV layout is itself clean: shared 0..1 UVs on every face would
	/// overlap and fail the UV gate before any test begins.</summary>
	static Vec2[] FaceUvs( int face )
	{
		var u0 = ( face % 3 ) * 0.333f + 0.01f;
		var v0 = ( face / 3 ) * 0.5f + 0.01f;
		return new[] { new Vec2( u0, v0 ), new Vec2( u0 + 0.3f, v0 ), new Vec2( u0 + 0.3f, v0 + 0.45f ), new Vec2( u0, v0 + 0.45f ) };
	}

	static PolyMesh Cube( bool closed = true, bool flipped = false )
	{
		var m = new PolyMesh();
		m.Positions.AddRange( new[]
		{
			V( -1, -1, -1 ), V( 1, -1, -1 ), V( 1, 1, -1 ), V( -1, 1, -1 ),
			V( -1, -1, 1 ), V( 1, -1, 1 ), V( 1, 1, 1 ), V( -1, 1, 1 )
		} );
		var faces = new List<int[]>
		{
			new[] { 0, 3, 2, 1 }, // bottom, normal -z
			new[] { 4, 5, 6, 7 }, // top, normal +z
			new[] { 0, 1, 5, 4 }, // front, normal -y
			new[] { 3, 7, 6, 2 }, // back, normal +y
			new[] { 0, 4, 7, 3 }, // left, normal -x
			new[] { 1, 2, 6, 5 } // right, normal +x
		};
		if ( !closed ) faces.RemoveAt( 1 );
		for ( var i = 0; i < faces.Count; i++ )
		{
			var indices = flipped ? faces[i].Reverse().ToArray() : faces[i];
			m.Faces.Add( new Face( indices, FaceUvs( i ) ) );
		}
		return m;
	}

	/// <summary>Flat n x n quad grid in the z=0 plane, triangulated, planar UVs 0..1.
/// 2*n*n triangles, outward (+z) winding.</summary>
	static PolyMesh Grid( int n )
	{
		var m = new PolyMesh();
		for ( var j = 0; j <= n; j++ )
			for ( var i = 0; i <= n; i++ )
				m.Positions.Add( V( i, j, 0 ) );
		for ( var j = 0; j < n; j++ )
			for ( var i = 0; i < n; i++ )
			{
				var a = j * ( n + 1 ) + i;
				var b = a + 1;
				var c = a + n + 1;
				var d = c + 1;
				var uvA = new Vec2( (float)i / n, (float)j / n );
				var uvB = new Vec2( (float)( i + 1 ) / n, (float)j / n );
				var uvC = new Vec2( (float)i / n, (float)( j + 1 ) / n );
				var uvD = new Vec2( (float)( i + 1 ) / n, (float)( j + 1 ) / n );
				m.Faces.Add( new Face( new[] { a, b, d }, new[] { uvA, uvB, uvD } ) );
				m.Faces.Add( new Face( new[] { a, d, c }, new[] { uvA, uvD, uvC } ) );
			}
		return m;
	}

	/// <summary>Two separate 1x1 quads. Quad A gets full 0..1 UVs; quad B gets
	/// <paramref name="uvB"/> for its corners, so the two islands sit at
	/// different texel densities / stretches by construction.</summary>
	static PolyMesh TwoQuads( Vec2[] uvB )
	{
		var m = new PolyMesh();
		m.Positions.AddRange( new[]
		{
			V( 0, 0, 0 ), V( 1, 0, 0 ), V( 1, 1, 0 ), V( 0, 1, 0 ),
			V( 2, 0, 0 ), V( 3, 0, 0 ), V( 3, 1, 0 ), V( 2, 1, 0 ),
		} );
		m.Faces.Add( new Face( new[] { 0, 1, 2, 3 }, QuadUvs() ) );
		m.Faces.Add( new Face( new[] { 4, 5, 6, 7 }, uvB ) );
		return m;
	}

	/// <summary>Single 2x2 quad in the z=0 plane: surface area exactly 4.</summary>
	static PolyMesh Plane2x2()
	{
		var m = new PolyMesh();
		m.Positions.AddRange( new[] { V( 0, 0, 0 ), V( 2, 0, 0 ), V( 2, 2, 0 ), V( 0, 2, 0 ) } );
		m.Faces.Add( new Face( new[] { 0, 1, 2, 3 }, QuadUvs() ) );
		return m;
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "finish-decimate", "decimation halves a grid and never touches the source", () =>
		{
			var grid = Grid( 20 );
			var before = Decimate.TriangleCount( grid );
			Check( before == 800, "grid triangles: " + before );
			var r = Decimation.DecimateToRatio( grid, 0.5f, name: "grid" );
			Check( r.ReachedTarget, "target not reached: " + r.ToTriangles + " from " + r.FromTriangles );
			Check( r.ToTriangles < r.FromTriangles, $"no reduction: {r.ToTriangles} from {r.FromTriangles}" );
			Check( r.ToTriangles >= 380 && r.ToTriangles <= 420, "half of 800 expected, got " + r.ToTriangles );
			Check( Decimate.TriangleCount( grid ) == 800, "source mesh was modified" );
			Check( !r.Validation.Block, "decimated grid blocked: " + r.Validation.Summary );
		} );

		registry.Add( "finish-decimate", "LOD chain names LOD0..LOD3 with decreasing triangles", () =>
		{
			var grid = Grid( 10 );
			var chain = Decimation.BuildNamedChain( grid, "grid" );
			Check( chain.Count == 4, "chain levels: " + chain.Count );
			for ( var i = 0; i < 4; i++ )
				Check( chain[i].Name == $"grid_LOD{i}", "level name: " + chain[i].Name );
			Check( chain[0].Level.Triangles == 200, "LOD0 triangles: " + chain[0].Level.Triangles );
			for ( var i = 1; i < 4; i++ )
				Check( chain[i].Level.Triangles <= chain[i - 1].Level.Triangles,
					$"LOD{i} ({chain[i].Level.Triangles}) above LOD{i - 1} ({chain[i - 1].Level.Triangles})" );
			Check( chain[3].Level.Triangles < chain[0].Level.Triangles, "LOD3 did not reduce" );
		} );

		registry.Add( "finish-decimate", "auto-UV packs a cube without overlap at the set margin", () =>
		{
			var cube = Cube();
			var r = FinishUv.AutoUv( cube, new AutoUvOptions { TextureSize = 1024, PackMarginPixels = 4f } );
			Check( r.Unwrap.Faces == 6, "unwrapped faces: " + r.Unwrap.Faces );
			Check( r.Unwrap.Charts >= 1, "charts: " + r.Unwrap.Charts );
			Check( r.Layout.OverlappingTexels == 0, "overlapping texels: " + r.Layout.OverlappingTexels );
			Check( r.Layout.OutsideFaces == 0, "outside faces: " + r.Layout.OutsideFaces );
			Check( r.Layout.Verdict != "FAIL", "layout failed: " + r.Layout );
			Check( r.Pack.Fits, "layout does not fit at 1024px" );
		} );

		registry.Add( "finish-decimate", "auto-UV with generated seams unwraps without overlap", () =>
		{
			var cube = Cube();
			var r = FinishUv.AutoUv( cube, new AutoUvOptions { TextureSize = 1024, UseAutoSeams = true } );
			Check( r.Unwrap.Charts >= 1, "charts: " + r.Unwrap.Charts );
			Check( r.Layout.OverlappingTexels == 0, "overlapping texels: " + r.Layout.OverlappingTexels );
			Check( r.Layout.OutsideFaces == 0, "outside faces: " + r.Layout.OutsideFaces );
			Check( r.Layout.Verdict != "FAIL", "layout failed: " + r.Layout );
		} );

		registry.Add( "finish-decimate", "texel density equalises within tolerance without re-unwrapping", () =>
		{
			// Quad B covers 0..0.25 in UV over the same 1x1 surface as quad A:
			// densities 1024 vs 256 px per unit at 1024px — a 4x spread.
			var m = TwoQuads( new[] { new Vec2( 0, 0 ), new Vec2( 0.25f, 0 ), new Vec2( 0.25f, 0.25f ), new Vec2( 0, 0.25f ) } );
			var before = UVDensity.Measure( m, 1024 );
			Check( before.Min > 0f && before.Max / before.Min > 2f,
				$"fixture has no spread: {before.Min:0.#}..{before.Max:0.#}" );
			Check( FinishUv.TexelDensityWarnings( m, 1024 ).Any( w => w.Contains( "spread" ) ),
				"spread warning missing before equalisation" );
			var after = FinishUv.EqualiseDensity( m, 1024, 64f );
			Check( after.Max / after.Min <= 1.05f,
				$"spread after equalise: {after.Min:0.##}..{after.Max:0.##}" );
			Check( !FinishUv.TexelDensityWarnings( m, 1024 ).Any( w => w.Contains( "spread" ) ),
				"spread warning still present after equalisation" );
		} );

		registry.Add( "finish-decimate", "stretch heatmap flags a squashed face and names it worst", () =>
		{
			// Quad B is squashed to 0.02 wide in U over the same 1x1 surface.
			var m = TwoQuads( new[] { new Vec2( 0, 0 ), new Vec2( 0.02f, 0 ), new Vec2( 0.02f, 1 ), new Vec2( 0, 1 ) } );
			var r = FinishStretch.Measure( m );
			Check( r.Max > 1.5f, "max stretch: " + r.Max );
			Check( r.OverThreshold > 0, "no face over the guide" );
			Check( r.WorstFaces.Count > 0 && r.WorstFaces[0] == 1, "worst face is not the squashed one: "
				+ string.Join( ",", r.WorstFaces ) );
			var islands = FinishStretch.IslandsOverThreshold( m );
			Check( islands.Count >= 1, "no island over the guide" );
			Check( islands[0].Island == 1, "worst island is not the squashed one: " + islands[0].Island );
		} );

		registry.Add( "finish-decimate", "clean cube validates pass with no hard fails", () =>
		{
			var r = FinishValidation.Validate( Cube(), "cube" );
			Check( !r.Blocked && !r.Warn, "clean cube should pass: " + r.Summary );
			Check( r.HardFails.Count == 0, "hard fails: " + string.Join( "; ", r.HardFails ) );
			Check( r.Doctor.IsClean, "doctor report not clean: " + r.Doctor.Describe() );
			Check( r.Summary.Contains( "pass" ), "summary: " + r.Summary );
		} );

		registry.Add( "finish-decimate", "open cube warns, and blocks when watertight is required", () =>
		{
			var loose = FinishValidation.Validate( Cube( closed: false ), "open cube" );
			Check( !loose.Blocked, "open cube should not block without strict holes: " + loose.Summary );
			Check( loose.Warn, "open cube should warn: " + loose.Summary );
			Check( loose.Doctor.Holes == 1, "holes: " + loose.Doctor.Holes );
			var strict = FinishValidation.Validate( Cube( closed: false ), "open cube", strictHoles: true );
			Check( strict.Blocked, "open cube should block with strict holes: " + strict.Summary );
		} );

		registry.Add( "finish-decimate", "validation catches flipped faces as a hard fail", () =>
		{
			var r = FinishValidation.Validate( Cube( flipped: true ), "flipped cube" );
			Check( r.Doctor.FlippedFaces == 6, "flipped faces: " + r.Doctor.FlippedFaces );
			Check( r.Blocked, "flipped cube should block: " + r.Summary );
			Check( r.HardFails.Any( h => h.Contains( "flipped" ) ), "no flipped hard fail: "
				+ string.Join( "; ", r.HardFails ) );
		} );

		registry.Add( "finish-decimate", "validation catches degenerate and duplicate faces", () =>
		{
			var m = Cube();
			m.Positions.Add( V( 0, -1, -1 ) ); // vertex 8, on the bottom front edge
			m.Faces.Add( new Face( new[] { 0, 8, 1 }, TriUvs() ) ); // collinear: zero area
			var r = FinishValidation.Validate( m, "degenerate cube" );
			Check( r.Doctor.DegenerateFaces >= 1, "degenerate faces: " + r.Doctor.DegenerateFaces );
			Check( r.Blocked, "degenerate cube should block: " + r.Summary );

			var dup = Cube();
			dup.Faces.Add( new Face( new[] { 0, 3, 2, 1 }, QuadUvs() ) ); // exact copy of the bottom face
			var rd = FinishValidation.Validate( dup, "duplicate cube" );
			Check( rd.Doctor.DuplicateFaces >= 1, "duplicate faces: " + rd.Doctor.DuplicateFaces );
			Check( rd.Blocked, "duplicate cube should block: " + rd.Summary );
		} );

		registry.Add( "finish-decimate", "one-click fix clears loose, degenerate and hole defects", () =>
		{
			var m = Cube( closed: false );
			m.Positions.Add( V( 5, 5, 5 ) ); // loose vertex, used by nothing
			m.Positions.Add( V( 0, -1, -1 ) ); // vertex 9, on the bottom front edge
			m.Faces.Add( new Face( new[] { 0, 9, 1 }, TriUvs() ) ); // collinear: zero area
			var fix = FinishValidation.FixAll( m, "messy cube" );
			Check( fix.Before.Doctor.Holes == 1, "before holes: " + fix.Before.Doctor.Holes );
			Check( fix.Before.Doctor.LooseVertices == 1, "before loose: " + fix.Before.Doctor.LooseVertices );
			Check( fix.Before.Doctor.DegenerateFaces >= 1, "before degenerate: " + fix.Before.Doctor.DegenerateFaces );
			Check( fix.Before.Blocked, "messy cube should block before the fix" );
			Check( fix.After.Doctor.Holes == 0, "after holes: " + fix.After.Doctor.Holes );
			Check( fix.After.Doctor.LooseVertices == 0, "after loose: " + fix.After.Doctor.LooseVertices );
			Check( fix.After.Doctor.DegenerateFaces == 0, "after degenerate: " + fix.After.Doctor.DegenerateFaces );
			Check( !fix.After.Blocked, "messy cube still blocked after the fix: " + fix.After.Summary );
			Check( fix.Steps.Count > 0, "fix logged no steps" );
			// UvReworked is not asserted here: the filled hole's new face may carry
			// no UVs, and a UV rebuild in that case is correct behaviour, not a failure.
		} );

		registry.Add( "finish-decimate", "one-click fix rebuilds fully overlapping UVs", () =>
		{
			var m = Cube();
			foreach ( var f in m.Faces )
				f.UVs = QuadUvs(); // every face on the same 0..1 square: total overlap
			var before = FinishValidation.Validate( m, "overlap cube" );
			Check( before.Layout.OverlappingTexels > 0, "fixture has no overlap: " + before.Layout );
			var fix = FinishValidation.FixAll( m, "overlap cube" );
			Check( fix.UvReworked, "UVs were not rebuilt" );
			Check( fix.After.Layout.OverlappingTexels == 0,
				"overlap remains: " + fix.After.Layout.OverlappingTexels );
			Check( fix.After.Layout.Verdict != "FAIL", "layout still failing: " + fix.After.Layout );
		} );

		registry.Add( "finish-decimate", "required texture size is exact on a known area", () =>
		{
			// 2x2 plane: area 4, sqrt(4) x 512 = 1024.
			Check( FinishUv.RequiredTextureSize( Plane2x2(), 512f ) == 1024,
				"required size: " + FinishUv.RequiredTextureSize( Plane2x2(), 512f ) );
			Check( FinishUv.RequiredTextureSize( Plane2x2(), 256f ) == 512,
				"required size at 256: " + FinishUv.RequiredTextureSize( Plane2x2(), 256f ) );
		} );

		registry.Add( "finish-decimate", "export size estimate is exact and the cube fits the budget", () =>
		{
			var cube = Cube();
			var (obj, glb) = FinishPipeline.EstimateExportBytes( cube );
			// 8 verts x 30 + 6 faces x 22 + 1024 header = 1396; 8 x 32 + 12 tris x 12 + 2048 = 2448.
			Check( obj == 1396, "OBJ estimate: " + obj );
			Check( glb == 2448, "GLB estimate: " + glb );
			Check( FinishPipeline.FitsExportBudget( cube ), "cube should fit the 50 MB budget" );
			Check( !FinishPipeline.FitsExportBudget( cube, 1000 ), "cube should not fit a 1 KB budget" );
		} );

		registry.Add( "finish-decimate", "texel density warning fires on spread and clears on equalise", () =>
		{
			var m = TwoQuads( new[] { new Vec2( 0, 0 ), new Vec2( 0.25f, 0 ), new Vec2( 0.25f, 0.25f ), new Vec2( 0, 0.25f ) } );
			Check( FinishUv.TexelDensityWarnings( m, 1024 ).Any( w => w.Contains( "spread" ) ),
				"no spread warning on a 4x spread layout" );
			FinishUv.EqualiseDensity( m, 1024, 64f );
			Check( !FinishUv.TexelDensityWarnings( m, 1024 ).Any( w => w.Contains( "spread" ) ),
				"spread warning survives equalisation" );
		} );

		registry.Add( "finish-decimate", "finish pipeline end to end: decimate, UV, validate, budget", () =>
		{
			var grid = Grid( 20 );
			var r = FinishPipeline.Finish( grid, "grid", new FinishPipeline.FinishOptions
			{
				TargetRatio = 0.5f,
				TextureSize = 512,
				TargetPixelsPerUnit = 32f,
				FixBeforeExport = false,
			} );
			Check( r.Decimation is not null, "no decimation step ran" );
			Check( r.Decimation.ToTriangles < 800 && r.Decimation.ToTriangles > 0,
				"triangles after decimate: " + r.Decimation.ToTriangles );
			Check( Decimate.TriangleCount( r.Mesh ) == r.Decimation.ToTriangles,
				"finished mesh triangles do not match the decimation result" );
			Check( Decimate.TriangleCount( grid ) == 800, "source grid was modified" );
			Check( r.Uv.Layout.Verdict != "FAIL", "layout failed: " + r.Uv.Layout );
			Check( r.Uv.Layout.OverlappingTexels == 0, "overlap: " + r.Uv.Layout.OverlappingTexels );
			Check( !r.After.Blocked, "finished grid blocked: " + r.After.Summary );
			Check( r.FitsExportBudget, "finished grid over the export budget" );
			Check( r.ToString().Contains( "triangles" ), "summary: " + r );
		} );
	}
}
