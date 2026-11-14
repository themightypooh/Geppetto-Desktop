using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Effigy;
using Marionette.Desktop;

/// <summary>OpenVDB voxel remesh + Instant Meshes auto retopo backends (see RetopoService.OpenVdb,
/// RetopoService.InstantMeshes, RetopoService.AutoRetopo). Mirrors Mobile's SculptOpenVdbChecks and
/// AutoRetopoChecks, adapted to the Desktop core: the native checks run when the bundled Linux .so is
/// found under packaging/native/ (copied there by this ticket); when it is not found they print SKIP
/// and pass, and the missing-library checks still prove the honest fallback. Nothing here reads or
/// writes user assets. The integrator registers this file with one line in tests/TestRegistry.cs
/// (RetopoBackendsTests.Register, after RemeshTests).</summary>
internal static class RetopoBackendsTests
{
	/// <summary>Walk up from the test binary looking for the bundled native library. Null when this
	/// checkout does not carry it (a published layout resolves it through the loader instead).</summary>
	static string NativeLib( string file )
	{
		var dir = new DirectoryInfo( AppContext.BaseDirectory );
		for ( int i = 0; i < 8 && dir != null; i++, dir = dir.Parent )
		{
			var candidate = Path.Combine( dir.FullName, "packaging", "native", "linux-x86_64", file );
			if ( File.Exists( candidate ) ) return candidate;
		}
		return null;
	}

	static bool SkipIfMissing( string file, out string lib )
	{
		lib = NativeLib( file );
		if ( lib == null )
		{
			Console.WriteLine( $"SKIP (native): {file} not found under packaging/native/ in this checkout; missing-library checks still run." );
			return true;
		}
		return false;
	}

	static PartStudio SeedStudio( PolyMesh mesh )
	{
		var studio = new PartStudio();
		var seed = new MeshEditFeature { Name = "Dense sculpt" };
		seed.FromScratch.Value = true;
		seed.Commit( mesh, new PolyMesh() );
		studio.Add( seed );
		studio.Rebuild();
		return studio;
	}

	/// <summary>Edges not shared by exactly two faces.</summary>
	static int OpenEdges( PolyMesh m )
	{
		var count = new Dictionary<EdgeKey, int>();
		foreach ( var f in m.Faces )
			for ( int c = 0; c < f.Count; c++ ) { var k = new EdgeKey( f.Indices[c], f.Indices[( c + 1 ) % f.Count] ); count[k] = count.GetValueOrDefault( k ) + 1; }
		return count.Values.Count( v => v != 2 );
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "retopo-backends", name, test );
		void Require( bool condition, string message )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"Expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"Expected {typeof( T ).Name}, got silent success" );
		}

		Check( "openvdb missing library is honest", () =>
		{
			var savedOverride = RetopoService.OpenVdb.LibraryPathOverride;
			var savedBackend = RemeshFeature.VoxelBackend;
			try
			{
				RetopoService.OpenVdb.LibraryPathOverride = "/nonexistent/libgmvdb.so";
				Require( !RetopoService.OpenVdb.Available(), "Available() is true with a nonexistent library path" );
				Require( !string.IsNullOrEmpty( RetopoService.OpenVdb.Missing ), "Missing is empty when the library is absent" );
				var sphere = RemeshFixtures.CubeSphere( 12, 1f );
				Require( RetopoService.OpenVdb.Remesh( new[] { sphere }, 0.1f ) == null, "Remesh returned a mesh with no library" );
				Require( RetopoService.OpenVdb.LastRoute.StartsWith( "built-in", StringComparison.Ordinal ), $"LastRoute does not say built-in: {RetopoService.OpenVdb.LastRoute}" );
				RetopoService.OpenVdb.Install();
				Require( RemeshFeature.VoxelBackend == null, "Install() wired a backend with no library" );
			}
			finally
			{
				RetopoService.OpenVdb.LibraryPathOverride = savedOverride;
				RetopoService.OpenVdb.ResetLoader();
				RemeshFeature.VoxelBackend = savedBackend;
			}
		} );

		Check( "openvdb key is stable and input-sensitive", () =>
		{
			var parts = new[] { RemeshFixtures.CubeSphere( 12, 1f ) };
			var a = RetopoService.OpenVdb.Key( parts, 0.1f, 2 );
			Require( RetopoService.OpenVdb.Key( parts, 0.1f, 2 ) == a, "same input gives a different key" );
			Require( RetopoService.OpenVdb.Key( parts, 0.05f, 2 ) != a, "different voxel gives the same key" );
			Require( RetopoService.OpenVdb.Key( parts, 0.1f, 3 ) != a, "different smooth passes give the same key" );
		} );

		Check( "openvdb remeshes a sphere (native)", () =>
		{
			if ( SkipIfMissing( "libgmvdb.so", out var lib ) ) return;
			var savedOverride = RetopoService.OpenVdb.LibraryPathOverride;
			try
			{
				RetopoService.OpenVdb.LibraryPathOverride = lib;
				Require( RetopoService.OpenVdb.Available(), $"libgmvdb loads: {RetopoService.OpenVdb.Missing}" );
				var sphere = RemeshFixtures.CubeSphere( 48, 1f );
				float want = sphere.SignedVolume();
				var sw = Stopwatch.StartNew();
				var m = RetopoService.OpenVdb.Remesh( new[] { sphere }, 0.1f );
				double ms = sw.Elapsed.TotalMilliseconds;
				Require( m != null && m.FaceCount > 0, $"remesh failed: {RetopoService.OpenVdb.LastError}" );
				var v = MeshValidator.Validate( m );
				Require( v.IsClosed, $"skin is not closed: {v}" );
				Require( v.Errors.Count == 0, $"skin is invalid: {v.Errors[0]}" );
				float vol = m.SignedVolume();
				Require( vol > 0 && MathF.Abs( vol - want ) < 0.03f * want, $"volume {vol:0.0000} vs {want:0.0000} (want within 3%)" );
				Console.WriteLine( $"openvdb sphere voxel 0.1: {m.FaceCount:N0} faces in {ms:0} ms" );
				int coarse = RetopoService.OpenVdb.Remesh( new[] { sphere }, 0.1f ).FaceCount;
				int fine = RetopoService.OpenVdb.Remesh( new[] { sphere }, 0.05f ).FaceCount;
				Require( fine > 3.4 * coarse && fine < 4.6 * coarse, $"face count does not track voxel size: {coarse:N0} -> {fine:N0} (want ~4x)" );
			}
			finally
			{
				RetopoService.OpenVdb.LibraryPathOverride = savedOverride;
				RetopoService.OpenVdb.ResetLoader();
			}
		} );

		Check( "voxel backend hook drives RemeshFeature voxel mode (native)", () =>
		{
			if ( SkipIfMissing( "libgmvdb.so", out var lib ) ) return;
			var savedOverride = RetopoService.OpenVdb.LibraryPathOverride;
			var savedBackend = RemeshFeature.VoxelBackend;
			try
			{
				RetopoService.OpenVdb.LibraryPathOverride = lib;
				var studio = SeedStudio( RemeshFixtures.CubeSphere( 24, 1f ) );
				RetopoService.OpenVdb.Install();
				Require( RemeshFeature.VoxelBackend != null, "Install() did not wire VoxelBackend" );
				var feature = new RemeshFeature { Name = "Voxel" };
				feature.Mode.Index = RemeshFeature.ModeVoxel;
				feature.Voxel.Value = 0.25f;
				feature.Fillet.Value = 0f;
				feature.Smooth.Value = 2;
				studio.Add( feature );
				studio.Rebuild();
				Require( feature.Error == null, $"voxel feature failed: {feature.Error}" );
				var body = studio.Bodies.Last();
				Require( body.Mesh.FaceCount > 0, "voxel remesh came out empty" );
				Require( RetopoService.OpenVdb.LastRoute == "openvdb", $"native engine did not run: {RetopoService.OpenVdb.LastRoute}" );
				// No backend installed: the built-in SurfaceNets path still remeshes.
				RemeshFeature.VoxelBackend = null;
				var fallback = new RemeshFeature { Name = "Voxel fallback" };
				fallback.Mode.Index = RemeshFeature.ModeVoxel;
				fallback.Voxel.Value = 0.25f;
				fallback.Fillet.Value = 0f;
				fallback.Smooth.Value = 2;
				studio.Add( fallback );
				studio.Rebuild();
				Require( fallback.Error == null, $"fallback voxel feature failed: {fallback.Error}" );
				Require( studio.Bodies.Last().Mesh.FaceCount > 0, "SurfaceNets fallback came out empty" );
			}
			finally
			{
				RetopoService.OpenVdb.LibraryPathOverride = savedOverride;
				RetopoService.OpenVdb.ResetLoader();
				RemeshFeature.VoxelBackend = savedBackend;
			}
		} );

		Check( "openvdb stash replays the prepared skin (native)", () =>
		{
			if ( SkipIfMissing( "libgmvdb.so", out var lib ) ) return;
			var savedOverride = RetopoService.OpenVdb.LibraryPathOverride;
			var savedBackend = RemeshFeature.VoxelBackend;
			try
			{
				RetopoService.OpenVdb.LibraryPathOverride = lib;
				var parts = new[] { RemeshFixtures.CubeSphere( 24, 1f ) };
				// The skin is worker-safe: it runs on a worker thread and only touches its inputs.
				var skin = Task.Run( () => RetopoService.OpenVdb.Skin( parts, 0.25f, 2 ) ).GetAwaiter().GetResult();
				Require( skin != null && skin.FaceCount > 0, "worker-thread Skin came out empty" );
				RetopoService.OpenVdb.Stash( RetopoService.OpenVdb.Key( parts, 0.25f, 2 ), skin );
				RetopoService.OpenVdb.Install();
				var via = RemeshFeature.VoxelBackend( parts, 0.25f, 2 );
				Require( ReferenceEquals( via, skin ), "the rebuild did not hand over the stashed skin" );
				var fresh = RemeshFeature.VoxelBackend( parts, 0.125f, 2 );
				Require( fresh != null && !ReferenceEquals( fresh, skin ), "a different voxel replayed the stashed skin" );
			}
			finally
			{
				RetopoService.OpenVdb.LibraryPathOverride = savedOverride;
				RetopoService.OpenVdb.ResetLoader();
				RemeshFeature.VoxelBackend = savedBackend;
			}
		} );

		Check( "instant meshes missing library is honest", () =>
		{
			var savedOverride = RetopoService.InstantMeshes.LibraryPathOverride;
			try
			{
				RetopoService.InstantMeshes.LibraryPathOverride = "/nonexistent/libgminstantmeshes.so";
				Require( !RetopoService.InstantMeshes.Available(), "Available() is true with a nonexistent library path" );
				Require( !string.IsNullOrEmpty( RetopoService.InstantMeshes.LoadError ), "LoadError is empty when the library is absent" );
				var mesh = RetopoService.InstantMeshes.Remesh(
					new RetopoService.InstantMeshes.Input { Positions = new float[9], Triangles = new uint[] { 0, 1, 7 } },
					100, true, 0, 1, new int[1], new int[1], out var err );
				Require( mesh == null && !string.IsNullOrEmpty( err ), "bad input was not refused with an error" );
			}
			finally
			{
				RetopoService.InstantMeshes.LibraryPathOverride = savedOverride;
				RetopoService.InstantMeshes.ResetLoader();
			}
		} );

		Check( "instant meshes From fan-triangulates n-gons", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 1, 0 ) );
			m.Positions.Add( new Vec3( 0, 1, 0 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 2, 3 } ) );
			var input = RetopoService.InstantMeshes.From( m );
			Require( input.Triangles.Length == 6, $"one quad should fan to 2 triangles, got {input.Triangles.Length / 3}" );
			Require( input.Positions.Length == 12, "positions are not 3 floats per vertex" );
		} );

		Check( "instant meshes remeshes near target, quad-dominant (native)", () =>
		{
			if ( SkipIfMissing( "libgminstantmeshes.so", out var lib ) ) return;
			var savedOverride = RetopoService.InstantMeshes.LibraryPathOverride;
			try
			{
				RetopoService.InstantMeshes.LibraryPathOverride = lib;
				Require( RetopoService.InstantMeshes.Available(), $"libgminstantmeshes loads: {RetopoService.InstantMeshes.LoadError}" );
				var dense = RemeshFixtures.CubeSphere( 96, 1f, 0.05f );
				var input = RetopoService.InstantMeshes.From( dense );
				long memBefore = Process.GetCurrentProcess().WorkingSet64;
				var sw = Stopwatch.StartNew();
				var m = RetopoService.InstantMeshes.Remesh( input, 2000, true, 2, 2, new int[1], new int[1], out var err );
				double ms = sw.Elapsed.TotalMilliseconds;
				long memAfter = Process.GetCurrentProcess().WorkingSet64;
				Require( m != null, $"remesh failed: {err}" );
				Require( m.FaceCount > 2000 * 0.75 && m.FaceCount < 2000 * 1.3, $"face count {m.FaceCount:N0} is not near target 2000" );
				int quads = m.Faces.Count( f => f.Count == 4 );
				Require( quads > m.FaceCount * 0.8, $"not quad-dominant: {quads * 100.0 / m.FaceCount:0.0}% quads" );
				var v = MeshValidator.Validate( m );
				// Instant Meshes does not guarantee a watertight result: the head / chisel /
				// bookends evaluation (handoffs/retopo-backend-evaluation.md) found holes on
				// every mesh, so this layer measures and reports them instead of claiming
				// closed. RetopoService.AutoRetopo is where the counts become part of the result message
				// (see the end-to-end check below).
				Console.WriteLine( $"instant meshes holes: {v.BoundaryEdges} boundary, {v.NonManifoldEdges} non-manifold edges" );
				var report = RetopoReport.Measure( m, dense );
				double diag = dense.BoundsDiagonal;
				Require( report.HasDeviation && report.MaxDeviation < 0.02 * diag && report.MeanDeviation < 0.004 * diag,
					$"surface deviation max {report.MaxDeviation:0.###} / mean {report.MeanDeviation:0.###} of diagonal {diag:0.###} (tolerance 2% / 0.4%)" );
				Console.WriteLine( $"instant meshes 2000 quads: {m.FaceCount:N0} faces in {ms:0} ms, working set +{( memAfter - memBefore ) / 1048576.0:0.0} MB" );
			}
			finally
			{
				RetopoService.InstantMeshes.LibraryPathOverride = savedOverride;
				RetopoService.InstantMeshes.ResetLoader();
			}
		} );

		Check( "instant meshes triangles mode is all triangles (native)", () =>
		{
			if ( SkipIfMissing( "libgminstantmeshes.so", out var lib ) ) return;
			var savedOverride = RetopoService.InstantMeshes.LibraryPathOverride;
			try
			{
				RetopoService.InstantMeshes.LibraryPathOverride = lib;
				var dense = RemeshFixtures.CubeSphere( 96, 1f, 0.05f );
				var m = RetopoService.InstantMeshes.Remesh( RetopoService.InstantMeshes.From( dense ), 2000, false, 2, 2, new int[1], new int[1], out var err );
				Require( m != null, $"remesh failed: {err}" );
				Require( m.Faces.All( f => f.Count == 3 ), $"{m.Faces.Count( f => f.Count != 3 )} non-triangles in triangles mode" );
				Require( m.FaceCount > 2000 * 0.75 && m.FaceCount < 2000 * 1.3, $"face count {m.FaceCount:N0} is not near target 2000" );
			}
			finally
			{
				RetopoService.InstantMeshes.LibraryPathOverride = savedOverride;
				RetopoService.InstantMeshes.ResetLoader();
			}
		} );

		Check( "instant meshes cancel stops the solve (native)", () =>
		{
			if ( SkipIfMissing( "libgminstantmeshes.so", out var lib ) ) return;
			var savedOverride = RetopoService.InstantMeshes.LibraryPathOverride;
			try
			{
				RetopoService.InstantMeshes.LibraryPathOverride = lib;
				var studio = SeedStudio( RemeshFixtures.CubeSphere( 96, 1f, 0.05f ) );
				int steps = studio.Features.Count;
				// Cancel before the solve starts: deterministic, the native side refuses up front.
				var early = new RetopoService.AutoRetopo( studio ) { TargetFaces = 10000 };
				var prep = early.Prepare();
				Require( prep != null, $"Prepare refused: {early.Refusal}" );
				early.Cancel[0] = 1;
				var none = early.Solve( prep.Value.Input, out var earlyErr );
				Require( none == null && earlyErr == "cancelled", $"pre-cancelled solve did not refuse as cancelled: '{earlyErr}'" );
				// Cancel mid-solve: the worker stops and nothing is applied.
				var mid = new RetopoService.AutoRetopo( studio ) { TargetFaces = 10000 };
				var prep2 = mid.Prepare();
				Require( prep2 != null, $"Prepare refused: {mid.Refusal}" );
				string err = null;
				var work = Task.Run( () => mid.Solve( prep2.Value.Input, out err ) );
				var deadline = Stopwatch.StartNew();
				while ( mid.Progress[0] < 10 && !work.IsCompleted && deadline.Elapsed.TotalSeconds < 60 ) Thread.Sleep( 5 );
				Require( !work.IsCompleted, "the solve finished before cancel could land (raise the target)" );
				mid.Cancel[0] = 1;
				var res = work.GetAwaiter().GetResult();
				Require( res == null && err == "cancelled", $"mid-solve cancel did not stop it: '{err}'" );
				Require( studio.Features.Count == steps, "a cancelled solve added a step" );
			}
			finally
			{
				RetopoService.InstantMeshes.LibraryPathOverride = savedOverride;
				RetopoService.InstantMeshes.ResetLoader();
			}
		} );

		Check( "auto retopo end-to-end is one step with bake (native)", () =>
		{
			if ( SkipIfMissing( "libgminstantmeshes.so", out var lib ) ) return;
			var savedOverride = RetopoService.InstantMeshes.LibraryPathOverride;
			try
			{
				RetopoService.InstantMeshes.LibraryPathOverride = lib;
				var dense = RemeshFixtures.CubeSphere( 48, 1f, 0.05f );
				var studio = SeedStudio( dense );
				int facesBefore = studio.Bodies.Last().Mesh.FaceCount;
				int steps = studio.Features.Count;
				var a = new RetopoService.AutoRetopo( studio ) { TargetFaces = 2000, Quads = true, BakeAfter = true, BakeSize = 256 };
				bool ok = a.RunBlocking();
				Require( ok, $"RunBlocking refused: {a.Refusal}" );
				Require( studio.Features.Count == steps + 1, "auto retopo did not add exactly one step" );
				Require( studio.Features.Last().Name == "Auto retopo", "the added step is not named 'Auto retopo'" );
				var m = studio.Bodies.Last().Mesh;
				Require( m.FaceCount > 2000 * 0.75 && m.FaceCount < 2000 * 1.3, $"face count {m.FaceCount:N0} is not near target 2000" );
				int quads = m.Faces.Count( f => f.Count == 4 );
				// The message counts the share with integer division; match it exactly, not a rounded double.
				int pct = quads * 100 / Math.Max( 1, m.FaceCount );
				Require( quads > m.FaceCount * 0.8, $"not quad-dominant: {pct}% quads" );
				Require( a.Message.Contains( $"{pct}% quads", StringComparison.Ordinal ),
					$"message does not report the counted quad share: '{a.Message}'" );
				Require( a.SolveMs > 0, "SolveMs was not measured" );
				Require( a.LastBake != null && a.LastBake.Normal != null && a.LastBake.Ao != null, "the bake did not come out" );
				Require( a.LastBake.Normal.Width == 256 && a.LastBake.Ao.Width == 256, "baked maps are not 256px" );
				Require( a.LastBake.Normal.Rgba.Where( ( b, i ) => i % 4 == 3 && b == 255 ).Any(), "normal map has no filled texels" );
				// A holed mesh is never labelled clean: the reported counts must match an
				// independent validation of the applied mesh, and the message must say so.
				var check = MeshValidator.Validate( m );
				Require( a.LastBoundaryEdges == check.BoundaryEdges && a.LastNonManifoldEdges == check.NonManifoldEdges,
					$"reported holes {a.LastBoundaryEdges}/{a.LastNonManifoldEdges} do not match the mesh: {check.BoundaryEdges}/{check.NonManifoldEdges}" );
				if ( check.BoundaryEdges > 0 || check.NonManifoldEdges > 0 )
					Require( a.Message.Contains( "boundary edges", StringComparison.Ordinal ) && a.Message.Contains( "not watertight", StringComparison.Ordinal ),
						$"a holed mesh was not labelled as such: '{a.Message}'" );
				// Orientation pin: the canvas must equal an independent re-bake of the normal map
				// with no flip (row 0 = v 0 on both sides). A flipped ToCanvas would fail here
				// even though the maps look fine in a thumbnail. Channel tolerance is +/-2 LSB;
				// alpha (Filled) must match exactly.
				var rebake = NormalBake.BakeAll( m, dense, 256, 256, BakeMaps.Normal, new BakeOptions { AoRays = 16 } );
				Require( rebake != null && rebake.Normal != null, "independent re-bake came out empty" );
				var expect = rebake.Normal;
				Require( expect.Width == 256 && expect.Height == 256, "re-bake is not 256px" );
				var got = a.LastBake.Normal;
				for ( int i = 0; i < 256 * 256; i++ )
				{
					for ( int c = 0; c < 3; c++ )
						Require( Math.Abs( got.Rgba[i * 4 + c] - expect.Rgb[i * 3 + c] ) <= 2,
							$"normal canvas differs from the bake at texel {i} channel {c} (rows flipped?)" );
					Require( got.Rgba[i * 4 + 3] == ( expect.Filled[i] ? (byte)255 : (byte)0 ), $"normal canvas alpha differs at texel {i}" );
				}
				var feature = (MeshEditFeature)studio.Features.Last();
				Require( feature.HasBake, "the Auto retopo step does not carry its bake" );
				Console.WriteLine( $"auto retopo 2000 quads: {m.FaceCount:N0} faces, solve {a.SolveMs:0} ms; {a.Message}" );
				// One Undo — remove the step and rebuild — restores the sculpt.
				studio.Remove( feature );
				studio.Rebuild();
				Require( studio.Bodies.Last().Mesh.FaceCount == facesBefore, "removing the step did not restore the sculpt" );
			}
			finally
			{
				RetopoService.InstantMeshes.LibraryPathOverride = savedOverride;
				RetopoService.InstantMeshes.ResetLoader();
			}
		} );

		Check( "auto retopo refuses honestly without the library", () =>
		{
			var savedOverride = RetopoService.InstantMeshes.LibraryPathOverride;
			try
			{
				RetopoService.InstantMeshes.LibraryPathOverride = "/nonexistent/libgminstantmeshes.so";
				var studio = SeedStudio( RemeshFixtures.CubeSphere( 24, 1f ) );
				int steps = studio.Features.Count;
				var a = new RetopoService.AutoRetopo( studio );
				Require( a.Prepare() == null, "Prepare succeeded with no library" );
				Require( !string.IsNullOrEmpty( a.Refusal ), "Prepare refused with no reason" );
				Require( a.Preview().Contains( "isn't in this build", StringComparison.Ordinal ), $"Preview does not say so: '{a.Preview()}'" );
				Require( studio.Features.Count == steps, "a refused run added a step" );
			}
			finally
			{
				RetopoService.InstantMeshes.LibraryPathOverride = savedOverride;
				RetopoService.InstantMeshes.ResetLoader();
			}
		} );
	}
}
