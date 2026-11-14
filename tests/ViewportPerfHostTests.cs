// Host-side tests for the virtualized viewport + standing perf harness
// (godlike Platform P1). Group "viewport-perf-host": the profiler, the CI
// perf suite, shader precompile, brush-latency measurement, and the real
// OrbitCamera -> frustum integration check. These need the Desktop host
// project (SceneRenderer, HeadlessGlContext, OrbitCamera) and so cannot run
// in the kernel-only harness; they run in the final build's full suite.
// The two shader-precompile checks need a headless GL context and are skipped
// unless V4_REQUIRE_EGL=1 (same gate as the existing hardware-EGL test).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Effigy;
using Marionette.Desktop;

internal static class ViewportPerfHostTests
{
	static void Require( bool condition, string message )
	{
		if ( !condition ) throw new Exception( message );
	}

	/// <summary>A bumpy grid: n x n quads over [-size, size]^2, z = 0.3 sin(x) cos(y).</summary>
	static PolyMesh BumpyGrid( int n, float size, Vec3 offset )
	{
		var mesh = new PolyMesh();
		for ( var j = 0; j <= n; j++ )
			for ( var i = 0; i <= n; i++ )
			{
				var x = -size + 2f * size * i / n;
				var y = -size + 2f * size * j / n;
				mesh.AddVertex( new Vec3( x + offset.x, y + offset.y, 0.3f * MathF.Sin( x ) * MathF.Cos( y ) + offset.z ) );
			}
		for ( var j = 0; j < n; j++ )
			for ( var i = 0; i < n; i++ )
			{
				var a = j * (n + 1) + i;
				mesh.AddFace( new[] { a, a + 1, a + n + 2, a + n + 1 }, null );
			}
		return mesh;
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "viewport-perf-host", "frustum from a real OrbitCamera keeps the target and culls behind it", () =>
		{
			var camera = new OrbitCamera();
			camera.Target = new Vector3( 0, 0, 0 );
			camera.Frame( new Vector3( 0, 0, 0 ), 5f );
			var vp = camera.ViewMatrix() * camera.ProjectionMatrix( 1f );
			var m = new[]
			{
				vp.M11, vp.M12, vp.M13, vp.M14,
				vp.M21, vp.M22, vp.M23, vp.M24,
				vp.M31, vp.M32, vp.M33, vp.M34,
				vp.M41, vp.M42, vp.M43, vp.M44,
			};
			var frustum = ViewFrustum.FromMatrix( m );
			var eye = new Vec3( camera.Eye.X, camera.Eye.Y, camera.Eye.Z );
			var target = new Vec3( camera.Target.X, camera.Target.Y, camera.Target.Z );
			Require( frustum.IntersectsSphere( target, 0.1f ), "the camera target must be inside the frustum" );
			Require( !frustum.IntersectsSphere( eye + (eye - target) * 2f, 0.1f ), "a point behind the camera must be outside" );
			Require( !frustum.IntersectsSphere( target + new Vec3( 1000, 0, 0 ), 0.1f ), "a point far to the side must be outside" );
		} );
registry.Add( "viewport-perf-host", "profiler names the slowest fixture object and the slowest pass", () =>
		{
			var passes = new[]
			{
				new RenderPassTimings( "Shadow", 1.0, 3.0, 0 ),
				new RenderPassTimings( "DepthPrePass", 0.5, 1.0, 0 ),
				new RenderPassTimings( "Lit", 4.0, 12.0, 0 ),
				new RenderPassTimings( "PostChain", 1.5, 2.0, 0 ),
			};
			var objects = new (string, int, int, bool)[]
			{
				("hero", 1_000_000, 4, true),
				("prop", 100, 0, false),
			};
			var report = ViewportProfiler.Profile( passes, objects );
			Require( report.SlowestObject == "hero", $"slowest object should be 'hero', got '{report.SlowestObject}'" );
			Require( report.SlowestObjectShare > 0.9, $"hero should dominate the cost (share {report.SlowestObjectShare})" );
			Require( report.SlowestPass == "Lit", $"slowest pass should be Lit, got '{report.SlowestPass}'" );
			Require( report.SlowestShader == "lit (RendererShaders)", $"slowest shader should be the lit program, got '{report.SlowestShader}'" );
			Require( report.GpuTimingAvailable, "GPU timings are present, so the ranking must use them" );
			Require( report.Recommendations.Count > 0 && report.Recommendations[0].Contains( "hero" ), "the first recommendation must name the slow object" );

			// No GPU timings yet: fall back to CPU ms for the pass ranking.
			var cpuOnly = new[]
			{
				new RenderPassTimings( "Shadow", 1.0, 0, 0 ),
				new RenderPassTimings( "Lit", 4.0, 0, 0 ),
			};
			var cpuReport = ViewportProfiler.Profile( cpuOnly, objects );
			Require( !cpuReport.GpuTimingAvailable, "zero GPU ms means GPU timing is not yet available" );
			Require( cpuReport.SlowestPass == "Lit", "CPU fallback must still rank Lit slowest" );
		} );
registry.Add( "viewport-perf-host", "perf suite passes a fast fixture and fails an artificially slowed one", () =>
		{
			var fast = new PerformanceLifecycleMatrix.Sample( "frame-draw", "fast fixture", new[] { 10.0, 11.0, 10.5 }, null, "measured" );
			var slow = new PerformanceLifecycleMatrix.Sample( "frame-draw", "slowed fixture", new[] { 100.0, 101.0, 99.0 }, null, "measured" );
			var untested = PerformanceLifecycleMatrix.Untested( "frame-draw", "no GL context", "No GL context." );

			var passing = PerfSuite.Run(
				new[] { (PerfTargets.Orbit1M60, (Func<PerformanceLifecycleMatrix.Sample>)(() => fast)) },
				"test-machine", "test-renderer" );
			Require( passing.Pass, "a fast fixture must pass the suite" );

			var failing = PerfSuite.Run(
				new[] { (PerfTargets.Orbit1M60, (Func<PerformanceLifecycleMatrix.Sample>)(() => slow)) },
				"test-machine", "test-renderer" );
			Require( !failing.Pass, "an artificially slowed fixture must fail the suite" );
			Require( failing.Results[0].MarginMs < 0, "the failure must report a negative margin (overage)" );

			var skipped = PerfSuite.Run(
				new[] { (PerfTargets.Orbit1M60, (Func<PerformanceLifecycleMatrix.Sample>)(() => untested)) },
				"test-machine", "test-renderer" );
			Require( skipped.Pass, "an untested target must not fail the suite" );
			Require( skipped.Results[0].Detail.Contains( "UNTESTED" ), "an untested target must be labelled as such" );
		} );
registry.Add( "viewport-perf-host", "shader precompile hashes and default specs are well-formed", () =>
		{
			var specs = ShaderPrecompile.DefaultSpecs();
			Require( specs.Count == 8, $"there should be 8 default programs, got {specs.Count}" );
			foreach ( var s in specs )
				Require( !string.IsNullOrWhiteSpace( s.Name ) && !string.IsNullOrWhiteSpace( s.VertexSource ) && !string.IsNullOrWhiteSpace( s.FragmentSource ),
					$"program '{s.Name}' needs a name and both sources" );
			var a = new ProgramSpec( "x", "vs", "fs" );
			var b = new ProgramSpec( "y", "vs", "fs" );
			var c = new ProgramSpec( "x", "vs", "different" );
			Require( ShaderPrecompile.Hash( a ) == ShaderPrecompile.Hash( b ), "same sources must hash the same" );
			Require( ShaderPrecompile.Hash( a ) != ShaderPrecompile.Hash( c ), "different sources must hash differently" );
		} );
registry.Add( "viewport-perf-host", "brush update measurement runs CPU-only without GL", () =>
		{
			var mesh = BumpyGrid( 4, 2f, new Vec3( 0, 0, 0 ) );
			var sample = PerfSuite.MeasureBrushUpdate( mesh,
				m =>
				{
					for ( var i = 0; i < m.VertexCount; i++ )
					{
						var p = m.Positions[i];
						m.Positions[i] = new Vec3( p.x, p.y, p.z + 0.01f );
					}
				},
				_ => "body", upload: false, warmups: 1, repetitions: 3 );
			Require( sample.Status == "measured", $"brush update should measure CPU-only, got {sample.Status}" );
			Require( sample.Milliseconds.Length == 3, "3 repetitions were requested" );
			Require( sample.MedianMilliseconds >= 0, "a measured median cannot be negative" );
		} );
registry.Add( "viewport-perf-host", "shader precompile completes before the first orbit", () =>
		{
			// No SceneRenderer is created and no Draw ever runs in this check:
			// precompile is the first and only GL work, which is the whole point.
			using var context = new HeadlessGlContext();
			var report = ShaderPrecompile.Precompile( context.Gl, ShaderPrecompile.DefaultSpecs() );
			Require( report.Programs.Count == 8, $"all 8 programs must compile, got {report.Programs.Count}" );
			Require( report.Programs.All( p => p.Milliseconds >= 0 ), "every program needs a measured compile time" );
			Require( report.Programs.All( p => !p.Duplicate ), "the 8 default programs have distinct sources" );
			Require( !string.IsNullOrWhiteSpace( report.SlowestProgram ), "the report must name the slowest program" );
			Require( report.TotalMilliseconds > 0, "precompile must take measurable time" );
			Require( report.Path == "Sequential", $"sequential path expected, got '{report.Path}'" );
		}, () => Environment.GetEnvironmentVariable( "V4_REQUIRE_EGL" ) == "1" ? null :
			"Set V4_REQUIRE_EGL=1 for explicit windowless hardware EGL measurement", required: false );
registry.Add( "viewport-perf-host", "shader precompile runs in parallel across worker contexts", () =>
		{
			var report = ShaderPrecompile.PrecompileParallel( ShaderPrecompile.DefaultSpecs() );
			Require( report.Programs.Count == 8, $"all 8 programs must compile, got {report.Programs.Count}" );
			Require( report.Path is "Parallel" or "ParallelWithSequentialFallback", $"parallel path expected, got '{report.Path}'" );
			Require( report.Programs.All( p => p is not null ), "every program needs a result" );
		}, () => Environment.GetEnvironmentVariable( "V4_REQUIRE_EGL" ) == "1" ? null :
			"Set V4_REQUIRE_EGL=1 for explicit windowless hardware EGL measurement", required: false );
	}
}
