using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Effigy;
using Marionette.Desktop;

internal static class PerformanceLifecycleTests
{
	static int _sink;
	static void Require( bool condition, string message )
	{
		if ( !condition ) throw new Exception( message );
	}

	static PartStudio Studio()
	{
		var studio = new PartStudio();
		studio.Add( new PrimitiveFeature { SizeX = { Value = 12f } } );
		studio.Rebuild();
		return studio;
	}

	static void Fails( Action action )
	{
		try { action(); }
		catch ( ArgumentException ) { return; }
		throw new Exception( "Expected an incompatible performance comparison to fail" );
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "performance-lifecycle", "same-thread warmed allocation counter and raw samples", () =>
		{
			int calls = 0;
			var sample = PerformanceLifecycleMatrix.Measure( "evaluation", "synchronous deterministic integer arithmetic",
				() => { calls++; _sink = calls * 13; }, warmups: 4, repetitions: 9 );
			Require( calls == 13 && sample.Milliseconds.Length == 9 && sample.AllocatedBytes!.Length == 9,
				"Warmup and sample iteration counts must be distinct" );
			Require( sample.AllocatedBytes!.All( b => b == 0 ) && sample.Milliseconds.All( t => t >= 0 ),
				"Synchronous work must not pick up harness allocations" );
			Fails( () => PerformanceLifecycleMatrix.Measure( "invalid", "negative repetitions", () => { }, repetitions: 0 ) );
		} );

		registry.Add( "performance-lifecycle", "guardrail refuses unlike workloads and distinguishes restore from rebuild", () =>
		{
			var workload = new PerformanceLifecycleMatrix.Workload( "box-v1", "effigy", 24, 12, 1, 0, 0 );
			var baseline = new PerformanceLifecycleMatrix.Sample( "cache-restore", "StudioCache.Load cache hit", new[] { 10.0, 11.0, 12.0 }, null, "measured" );
			var current = baseline with { Milliseconds = new[] { 12.0, 13.0, 14.0 } };
			Require( PerformanceLifecycleMatrix.WithinGuardrail( workload, baseline, workload, current, .2, out _ ), "20% allowance must accept median 13 vs 11" );
			Require( !PerformanceLifecycleMatrix.WithinGuardrail( workload, baseline, workload, current, .1, out _ ), "10% allowance must reject slowdown" );
			Fails( () => PerformanceLifecycleMatrix.WithinGuardrail( workload, baseline, workload with { Textures = 1 }, current, .2, out _ ) );
			Fails( () => PerformanceLifecycleMatrix.WithinGuardrail( workload, baseline, workload, current with { Stage = "cold-rebuild" }, .2, out _ ) );
			Fails( () => PerformanceLifecycleMatrix.WithinGuardrail( workload, baseline, workload, current, double.NaN, out _ ) );
		} );

		registry.Add( "performance-lifecycle", "real source parse rebuild extraction and animation stages persist raw report", () =>
		{
			string root = Path.Combine( settings.WorkDirectory, "v4-stage-fixture" );
			Directory.CreateDirectory( root );
			var studio = Studio();
			string source = Path.Combine( root, "box.effigy" );
			File.WriteAllText( source, StudioDocument.Write( studio ) );
			string text = File.ReadAllText( source ); // IO is not included in parse sample
			var samples = new List<PerformanceLifecycleMatrix.Sample>();
			PartStudio? lastParsed = null;
			samples.Add( PerformanceLifecycleMatrix.Measure( "source-parse", "StudioDocument.Read of already-read fixture text; excludes filesystem IO", () =>
			{
				lastParsed = StudioDocument.Read( text );
			}, repetitions: 5 ) );
			Require( lastParsed?.Features.Count == 1, "Parse lost the fixture feature" );
			PartStudio? lastRebuilt = null;
			samples.Add( PerformanceLifecycleMatrix.Measure( "cold-rebuild", "StudioDocument.Read then PartStudio.Rebuild; cache bypassed; excludes disk IO", () =>
			{
				lastRebuilt = StudioDocument.Read( text );
				lastRebuilt.Rebuild();
			}, repetitions: 5 ) );
			Require( lastRebuilt?.Bodies.Count > 0, "Rebuild produced no body" );
			var mesh = ViewportMesh.FromStudio( studio, 0, source );
			var workload = PerformanceLifecycleMatrix.Describe( "box-v1", "effigy", mesh );
			Require( workload.Vertices > 0 && workload.Triangles > 0 && workload.Batches > 0 && workload.Bones == 0 && workload.Textures == 0,
				"Report must describe actual extracted geometry" );
			int lastTriangles = 0;
			samples.Add( PerformanceLifecycleMatrix.Measure( "extraction", "ViewportMesh.FromStudio of prebuilt PartStudio; CPU geometry and picking, no upload", () =>
			{
				using var extracted = ViewportMesh.FromStudio( studio, 0, source );
				lastTriangles = extracted.TriangleCount;
			}, repetitions: 5 ) );
			Require( lastTriangles == workload.Triangles, "Extraction workload changed" );
			var rig = new Skeleton(); rig.AddBone( "root", -1, Xform.Identity );
			MarionettePlayer? lastPlayer = null;
			samples.Add( PerformanceLifecycleMatrix.Measure( "animation-preparation", "new MarionettePlayer on single-bone synthetic rig; no clip IO", () =>
			{
				lastPlayer = new MarionettePlayer( rig );
			}, repetitions: 5 ) );
			Require( lastPlayer?.GpuBones.Length == 1, "Animation preparation failed" );
			var playerHot = new MarionettePlayer( rig );
			samples.Add( PerformanceLifecycleMatrix.Measure( "animation-evaluation", "MarionettePlayer.Step(1/60) with one bind bone; same-thread warm evaluation", () => playerHot.Step( 1f / 60 ), repetitions: 20 ) );
			samples.Add( PerformanceLifecycleMatrix.Untested( "cache-restore", "StudioCache.Load cache hit", "Cache state is external/global; not conflated with cold rebuild" ) );
			samples.Add( PerformanceLifecycleMatrix.Untested( "texture-decode", "PNG decode", "Fixture has zero textures" ) );
			samples.Add( PerformanceLifecycleMatrix.Untested( "gpu-upload-completion", "EGL gl.Finish", "Run V4_REQUIRE_EGL=1 for a windowless hardware measurement" ) );
			var report = new PerformanceLifecycleMatrix.Report( workload, RuntimeInformation.OSDescription,
				RuntimeInformation.OSArchitecture.ToString(), RuntimeInformation.FrameworkDescription,
				null, null, null, null, samples,
				new[] { new PerformanceLifecycleMatrix.Capability( "Effigy parse/extract", "measured", "synthetic box-v1" ),
					new PerformanceLifecycleMatrix.Capability( "FBX import", "untested", "No FBX fixture in this CPU stage test" ),
					new PerformanceLifecycleMatrix.Capability( "Linux hardware EGL", "untested", "CPU test does not open GL" ),
					new PerformanceLifecycleMatrix.Capability( "other OS/GPUs", "untested", "No cross-platform inference" ) },
				new Dictionary<string, long> { ["retainedCpuSnapshots"] = 0, ["retainedGpuMeshes"] = 0 } );
			string path = Path.Combine( root, "cpu-stages.json" );
			PerformanceLifecycleMatrix.WriteReport( path, report );
			using var json = JsonDocument.Parse( File.ReadAllText( path ) );
			Require( json.RootElement.GetProperty( "Samples" ).GetArrayLength() == samples.Count, "Raw samples not retained" );
			mesh.Dispose();
		} );

		registry.Add( "performance-lifecycle", "repeated snapshot swap, stale rejection, dispose and failed loader recovery", () =>
		{
			string root = Path.Combine( settings.WorkDirectory, "v4-lifecycle-fixture" );
			Directory.CreateDirectory( root );
			string path = Path.Combine( root, "box.effigy" );
			File.WriteAllText( path, StudioDocument.Write( Studio() ) );
			var session = DocumentSession.CreateEffigy( path, Studio() );
			int created = 0, disposed = 0;
			for ( int i = 0; i < 16; i++ )
			{
				var next = session.BuildSnapshot(); created++;
				var previous = session.CurrentSnapshot;
				Require( session.TryPromoteSnapshot( next ), "Current snapshot not promoted" );
				Require( session.CurrentSnapshot == next, "Swap retained wrong snapshot" );
				if ( previous is not null )
				{
					previous.Dispose(); disposed++;
					Require( previous.Mesh.Vertices is null && previous.Mesh.Indices is null &&
						!ReferenceEquals( session.CurrentSnapshot, previous ), "Retired snapshot remains referenced or retains CPU arrays" );
				}
			}
			var obsolete = session.BuildSnapshot(); created++;
			session.BumpRevision();
			Require( !session.TryPromoteSnapshot( obsolete ), "Stale result overwrote live snapshot" );
			obsolete.Dispose(); disposed++;
			Require( session.CurrentSnapshot is not null && session.CurrentSnapshot.Mesh.Vertices is not null,
				"Stale rejection damaged live snapshot" );
			// Session has no ClearSnapshot/Dispose method: final retained snapshot is measured, not hidden.
			Require( created - disposed == 1, "Unexpected retained CPU snapshot count" );
			var loader = new DocumentLoader();
			loader.Start( Path.Combine( root, "missing.effigy" ) );
			Require( loader.Completion.Wait( TimeSpan.FromSeconds( 15 ) ) && loader.Status == DocumentLoader.State.Failed,
				"Failed background job did not publish failure" );
			loader.Reset();
			string brokenClip = Path.Combine( root, "broken.riganim" );
			File.WriteAllText( brokenClip, "not valid riganim json" );
			loader.Start( path, brokenClip );
			Require( loader.Completion.Wait( TimeSpan.FromSeconds( 15 ) ) && loader.Status == DocumentLoader.State.Failed,
				"Invalid clip job did not publish failure" );
			loader.Reset();
			loader.Start( path );
			Require( loader.Completion.Wait( TimeSpan.FromSeconds( 30 ) ) && loader.Status == DocumentLoader.State.Ready && loader.Snapshot is not null,
				"Loader could not recover after Reset" );
			loader.Snapshot!.Dispose();
			// The session API currently retains CurrentSnapshot; do not assert reclamation it cannot provide.
			session.CurrentSnapshot!.Dispose();
		} );

		registry.Add( "performance-lifecycle", "hardware EGL upload, completion and resource release", () =>
		{
			string root = Path.Combine( settings.WorkDirectory, "v4-egl-fixture" );
			Directory.CreateDirectory( root );
			string path = Path.Combine( root, "box.effigy" );
			var studio = Studio();
			File.WriteAllText( path, StudioDocument.Write( studio ) );
			using var context = new HeadlessGlContext();
			var hardware = PerformanceLifecycleMatrix.ProbeHardware( context );
			var workloadMesh = ViewportMesh.FromStudio( studio, 0, path );
			var workload = PerformanceLifecycleMatrix.Describe( "box-v1", "effigy", workloadMesh );
			workloadMesh.Dispose();
			var (submit, complete) = PerformanceLifecycleMatrix.MeasureUpload( context,
				() => ViewportMesh.FromStudio( studio, 0, path ) );
			Require( submit.Milliseconds.Length == 5 && complete.Milliseconds.Length == 5 &&
				complete.Milliseconds.Zip( submit.Milliseconds ).All( pair => pair.First >= pair.Second ),
				"Completion cannot precede CPU submission" );
			PerformanceLifecycleMatrix.WriteReport( Path.Combine( root, "hardware-egl.json" ),
				new PerformanceLifecycleMatrix.Report( workload, RuntimeInformation.OSDescription,
					RuntimeInformation.OSArchitecture.ToString(), RuntimeInformation.FrameworkDescription,
					hardware.Vendor, hardware.Renderer, hardware.Version, hardware.Mode,
					new[] { submit, complete }, new[] { new PerformanceLifecycleMatrix.Capability( "Linux EGL upload", "measured", hardware.Renderer ),
						new PerformanceLifecycleMatrix.Capability( "monitor present/vsync", "untested", "No visible window" ) },
					new Dictionary<string, long> { ["retainedGpuMeshes"] = 0 } ) );
		}, () => Environment.GetEnvironmentVariable( "V4_REQUIRE_EGL" ) == "1" ? null :
			"Set V4_REQUIRE_EGL=1 for explicit windowless hardware EGL measurement", required: false );
	}
}
