using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Marionette.Desktop;

/// <summary>
/// Decimation presets + silhouette + projection + LOD naming, on procedural in-memory
/// fixtures. Nothing here reads or writes user assets. The closed fixtures reuse
/// <see cref="RemeshFixtures.CubeSphere"/> (same test assembly); the open grid is built
/// here because the border and UV checks need a mesh with a border and planar UVs.
/// </summary>
internal static class DecimationProjectionTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "decimation-projection", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws( Action action )
		{
			try { action(); }
			catch ( Exception ) { return; }
			throw new Exception( "expected an error, got silent success" );
		}
		void Near( float actual, float expected, float tolerance, string message )
		{
			Require( MathF.Abs( actual - expected ) <= tolerance, $"{message}: got {actual}, want {expected} ± {tolerance}" );
		}

		/// <summary>Open grid, x/y in [0,1], z a smooth bump; planar UVs (u=x, v=y).</summary>
		PolyMesh HeightGrid( int n, float amp )
		{
			var mesh = new PolyMesh();
			var idx = new int[n + 1, n + 1];
			Vec2 Uv( int v )
			{
				var p = mesh.Positions[v];
				return new Vec2( p.x, p.y );
			}

			for ( var y = 0; y <= n; y++ )
			{
				for ( var x = 0; x <= n; x++ )
				{
					var fx = x / (float)n;
					var fy = y / (float)n;
					var z = amp * MathF.Sin( MathF.PI * fx ) * MathF.Cos( MathF.PI * fy );
					idx[x, y] = mesh.AddVertex( new Vec3( fx, fy, z ) );
				}
			}

			for ( var y = 0; y < n; y++ )
			{
				for ( var x = 0; x < n; x++ )
				{
					var a = idx[x, y];
					var b = idx[x + 1, y];
					var c = idx[x + 1, y + 1];
					var d = idx[x, y + 1];
					mesh.AddFace( new[] { a, b, c }, new[] { Uv( a ), Uv( b ), Uv( c ) } );
					mesh.AddFace( new[] { a, c, d }, new[] { Uv( a ), Uv( c ), Uv( d ) } );
				}
			}

			return mesh;
		}

		/// <summary>Positions of vertices on open borders (edges with exactly one face).</summary>
		List<Vec3> BorderPositions( PolyMesh mesh )
		{
			var pts = new List<Vec3>();
			var seen = new HashSet<int>();

			foreach ( var (key, faces) in mesh.BuildEdgeFaces() )
			{
				if ( faces.Count != 1 ) continue;

				foreach ( var vi in new[] { key.A, key.B } )
				{
					if ( seen.Add( vi ) ) pts.Add( mesh.Positions[vi] );
				}
			}

			return pts;
		}

		// ---- Decimation: target count and percentage ----

		Check( "decimate to target count: 6912-triangle sphere reaches 500", () =>
		{
			var sphere = RemeshFixtures.CubeSphere( 24, 1f, 0.05f );
			Require( Decimate.TriangleCount( sphere ) == 6912, $"fixture is {Decimate.TriangleCount( sphere )} triangles, want 6912" );

			var result = Decimation.DecimateTo( sphere, 500 );
			Require( result.ReachedTarget, "decimation should reach 500 on a bumpy sphere" );
			Require( result.ToTriangles <= 500 && result.ToTriangles >= 480, $"got {result.ToTriangles} triangles, want 480–500" );
			Require( result.FromTriangles == 6912, $"from {result.FromTriangles}, want 6912" );
		} );

		Check( "decimate to percentage: 50% halves the triangles", () =>
		{
			var sphere = RemeshFixtures.CubeSphere( 16, 1f, 0.02f );
			var result = Decimation.DecimateToRatio( sphere, 0.5f );
			Require( result.ReachedTarget, "decimation should reach 50%" );
			Require( result.ToTriangles >= 1459 && result.ToTriangles <= 1613, $"got {result.ToTriangles} triangles, want 1536 ± 5%" );
			Throws( () => Decimation.DecimateToRatio( sphere, 0f ) );
			Throws( () => Decimation.DecimateToRatio( sphere, 1.5f ) );
		} );

		// ---- Borders frozen, UVs kept ----

		Check( "borders frozen: open grid keeps every border vertex in place", () =>
		{
			var grid = HeightGrid( 24, 0.1f );
			var before = BorderPositions( grid );
			Require( before.Count == 96, $"grid border is {before.Count} vertices, want 96" );

			var result = Decimation.DecimateTo( grid, 200, freezeBorders: true );
			var after = BorderPositions( result.Mesh );
			Require( after.Count == before.Count, $"border went {before.Count} -> {after.Count} vertices with FreezeBoundary on" );

			var used = new bool[before.Count];

			foreach ( var p in after )
			{
				var found = -1;

				for ( var i = 0; i < before.Count; i++ )
				{
					if ( !used[i] && ( before[i] - p ).LengthSquared < 1e-8f )
					{
						found = i;
						break;
					}
				}

				Require( found >= 0, $"result border vertex {p} is not a source border vertex" );
				used[found] = true;
			}

			var minX = result.Mesh.Positions.Min( p => p.x );
			var maxX = result.Mesh.Positions.Max( p => p.x );
			var minY = result.Mesh.Positions.Min( p => p.y );
			var maxY = result.Mesh.Positions.Max( p => p.y );
			Near( minX, 0f, 1e-4f, "border min x" );
			Near( maxX, 1f, 1e-4f, "border max x" );
			Near( minY, 0f, 1e-4f, "border min y" );
			Near( maxY, 1f, 1e-4f, "border max y" );
		} );

		Check( "borders unfrozen: the opening still holds its shape", () =>
		{
			var grid = HeightGrid( 24, 0.1f );
			var result = Decimation.DecimateTo( grid, 200, freezeBorders: false );
			Near( result.Mesh.Positions.Min( p => p.x ), 0f, 1e-4f, "border min x" );
			Near( result.Mesh.Positions.Max( p => p.x ), 1f, 1e-4f, "border max x" );
			Near( result.Mesh.Positions.Min( p => p.y ), 0f, 1e-4f, "border min y" );
			Near( result.Mesh.Positions.Max( p => p.y ), 1f, 1e-4f, "border max y" );
		} );

		Check( "UVs kept: the corner UV survives decimation and no UV leaves the chart", () =>
		{
			var grid = HeightGrid( 24, 0.1f );
			var result = Decimation.DecimateTo( grid, 200, keepUvs: true );

			Vec2? cornerUv = null;

			foreach ( var f in result.Mesh.Faces )
			{
				Require( f.UVs is not null && f.UVs.Length == 3, "every decimated face must carry per-corner UVs" );

				for ( var k = 0; k < 3; k++ )
				{
					Require( f.UVs[k].x >= -0.01f && f.UVs[k].x <= 1.01f && f.UVs[k].y >= -0.01f && f.UVs[k].y <= 1.01f,
						$"UV {f.UVs[k]} left the [0,1] chart" );

					if ( ( result.Mesh.Positions[f.Indices[k]] - Vec3.Zero ).LengthSquared < 1e-8f )
						cornerUv = f.UVs[k];
				}
			}

			Require( cornerUv.HasValue, "the (0,0) corner vertex did not survive decimation" );
			Near( cornerUv.Value.x, 0f, 1e-4f, "corner u" );
			Near( cornerUv.Value.y, 0f, 1e-4f, "corner v" );
		} );

		// ---- Presets ----

		Check( "presets: the five triangle targets, names and nearest preset", () =>
		{
			Require( Decimation.Presets.Length == 5, "five presets" );
			Require( Decimation.Presets[0] == 20000 && Decimation.Presets[1] == 35000 && Decimation.Presets[2] == 75000
				&& Decimation.Presets[3] == 150000 && Decimation.Presets[4] == 250000, "preset values are 20K/35K/75K/150K/250K" );
			Require( Decimation.PresetName( 20000 ) == "20K", "20K name" );
			Require( Decimation.PresetName( 75000 ) == "75K", "75K name" );
			Require( Decimation.PresetName( 250000 ) == "250K", "250K name" );
			Require( Decimation.NearestPreset( 40000 ) == 35000, "40K is nearest to 35K" );
			Require( Decimation.NearestPreset( 100000 ) == 75000, "100K is nearest to 75K" );
			Throws( () => Decimation.PresetName( 12345 ) );
		} );

		Check( "preset decimation: 39k-triangle grid reaches the 35K preset", () =>
		{
			var grid = HeightGrid( 140, 0.05f );
			Require( Decimate.TriangleCount( grid ) == 39200, $"fixture is {Decimate.TriangleCount( grid )} triangles, want 39200" );

			var result = Decimation.DecimateToPreset( grid, 35000 );
			Require( result.ReachedTarget, "decimation should reach the 35K preset" );
			Require( result.ToTriangles <= 35000 && result.ToTriangles >= 34000, $"got {result.ToTriangles} triangles, want 34000–35000" );
			Throws( () => Decimation.DecimateToPreset( grid, 12345 ) );
		} );

		// ---- Silhouette deviation ----

		Check( "silhouette: identical meshes deviate zero", () =>
		{
			var sphere = RemeshFixtures.CubeSphere( 16, 1f, 0.02f );
			var report = Decimation.SilhouetteDeviation( sphere, sphere );
			Require( report.Samples > 0, "silhouette sampled nothing" );
			Require( report.Misses == 0, $"{report.Misses} samples missed their own mesh" );
			Near( report.MaxDeviation, 0f, 1e-5f, "identical max deviation" );
			Near( report.MeanDeviation, 0f, 1e-6f, "identical mean deviation" );
			Require( report.WithinTolerance( 0.01f ), "identical meshes are within any tolerance" );
		} );

		Check( "silhouette: decimated sphere stays within tolerance", () =>
		{
			var sphere = RemeshFixtures.CubeSphere( 24, 1f, 0.05f );
			var result = Decimation.DecimateTo( sphere, 500 );
			Require( result.Silhouette.Misses == 0, "silhouette samples missed" );
			Require( result.Silhouette.MaxDeviation < 0.15f, $"worst deviation {result.Silhouette.MaxDeviation}, want < 0.15" );
			Require( result.Silhouette.WithinRelativeTolerance( 0.1f ), $"relative deviation {result.Silhouette.RelativeMax}, want < 0.1 of the diagonal" );
		} );

		// ---- Projection ----

		Check( "projection: decimated sphere samples the dense surface", () =>
		{
			var high = RemeshFixtures.CubeSphere( 24, 1f, 0.05f );
			var low = Decimation.DecimateTo( high, 300 ).Mesh;
			var projection = DetailProjection.Project( high, low );
			Near( projection.HitRate, 1f, 1e-6f, "every low vertex must hit the high surface" );
			Require( projection.MeanAbsDisplacement < 0.05f, $"mean displacement {projection.MeanAbsDisplacement}, want < 0.05" );
			Require( projection.MaxAbsDisplacement < 0.2f, $"max displacement {projection.MaxAbsDisplacement}, want < 0.2" );
			Require( projection.Notes.Count == 0, "a clean projection carries no notes" );
		} );

		Check( "projection: snapped mesh sits on the high surface", () =>
		{
			var high = RemeshFixtures.CubeSphere( 24, 1f, 0.05f );
			var low = Decimation.DecimateTo( high, 300 ).Mesh;
			var projection = DetailProjection.Project( high, low );
			var snapped = DetailProjection.Snap( low, projection );
			var (mean, max, hitRate) = DetailProjection.ProjectionError( high, snapped );
			Near( hitRate, 1f, 1e-6f, "snapped hit rate" );
			Require( max < 1e-3f, $"snapped max displacement {max}, want < 0.001" );
			Require( mean < 1e-4f, $"snapped mean displacement {mean}, want < 0.0001" );
			Require( snapped.VertexCount == low.VertexCount, "snap must not change the vertex count" );
		} );

		Check( "projection: displacement is signed — a scaled-down sphere reads all positive", () =>
		{
			var high = RemeshFixtures.CubeSphere( 12, 1f, 0f );
			var low = high.Clone();

			for ( var i = 0; i < low.VertexCount; i++ )
				low.Positions[i] = low.Positions[i] * 0.9f;

			low.InvalidateVertexCache();
			var projection = DetailProjection.Project( high, low );
			Near( projection.HitRate, 1f, 1e-6f, "scaled hit rate" );
			Require( projection.Displacement.All( d => d > 1e-4f ), "every displacement must be positive (high outside low)" );
			Require( projection.MeanAbsDisplacement > 0.05f && projection.MeanAbsDisplacement < 0.12f,
				$"mean displacement {projection.MeanAbsDisplacement}, want the 0.1 scale gap" );
		} );

		// ---- LOD chain naming + shared validation ----

		Check( "LOD chain: names _LOD0.._LOD3, LOD0 is the source, levels shrink", () =>
		{
			var names = Decimation.LodNames( "Head" );
			Require( names.Count == 4, "four LOD names" );
			Require( names[0] == "Head_LOD0" && names[1] == "Head_LOD1" && names[2] == "Head_LOD2" && names[3] == "Head_LOD3",
				$"names are {string.Join( ", ", names )}" );
			Throws( () => Decimation.LodName( "Head", 4 ) );
			Throws( () => Decimation.LodName( "", 0 ) );

			var sphere = RemeshFixtures.CubeSphere( 16, 1f, 0.02f );
			var chain = Decimation.BuildNamedChain( sphere, "Head" );
			Require( chain.Count == 4, "four chain levels" );
			Require( ReferenceEquals( chain[0].Level.Mesh, sphere ), "LOD0 is the source mesh itself" );
			Require( chain[0].Level.Triangles > chain[1].Level.Triangles
				&& chain[1].Level.Triangles > chain[2].Level.Triangles
				&& chain[2].Level.Triangles > chain[3].Level.Triangles, "triangles must strictly decrease down the chain" );
			Require( chain[0].Level.SwitchDistance == 0f, "LOD0 switches at distance 0" );
			Require( chain[1].Level.SwitchDistance <= chain[2].Level.SwitchDistance
				&& chain[2].Level.SwitchDistance <= chain[3].Level.SwitchDistance, "switch distances must not decrease" );
			var half = sphere.VertexCount / 2;
			Require( Math.Abs( chain[1].Level.VertexCount - half ) <= Math.Max( 2, half / 10 ),
				$"LOD1 has {chain[1].Level.VertexCount} vertices, want ~{half}" );
		} );

		Check( "validation: decimated clean mesh passes the shared MeshDoctor check", () =>
		{
			var sphere = RemeshFixtures.CubeSphere( 16, 1f, 0.02f );
			var result = Decimation.DecimateTo( sphere, 500 );
			Require( result.Validation is not null, "decimation must carry the MeshDoctor decision" );
			Require( result.Validation.Report is not null, "the decision must carry the MeshDoctor report" );
			Require( !result.Validation.Block, $"clean decimated mesh must pass, got: {result.Validation.Summary}" );
			Require( result.Validation.Summary.Contains( "pass" ), $"summary should say pass, got: {result.Validation.Summary}" );
		} );

		Check( "decimation does not modify the source mesh", () =>
		{
			var sphere = RemeshFixtures.CubeSphere( 16, 1f, 0.02f );
			var vertsBefore = sphere.VertexCount;
			var trisBefore = Decimate.TriangleCount( sphere );
			var sampleBefore = sphere.Positions[0];
			Decimation.DecimateTo( sphere, 500 );
			Require( sphere.VertexCount == vertsBefore, "source vertex count changed" );
			Require( Decimate.TriangleCount( sphere ) == trisBefore, "source triangle count changed" );
			Require( ( sphere.Positions[0] - sampleBefore ).LengthSquared < 1e-12f, "source vertex moved" );
		} );
	}
}
