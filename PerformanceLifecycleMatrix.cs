#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>Explicit, reproducible stage samples; no monitor FPS or cross-machine speed claim.</summary>
public static class PerformanceLifecycleMatrix
{
	public sealed record Workload( string Name, string AssetKind, int Vertices, int Triangles,
		int Batches, int Bones, int Textures );
	public sealed record Sample( string Stage, string Definition, double[] Milliseconds, long[]? AllocatedBytes,
		string Status, string? Detail = null )
	{
		public double MedianMilliseconds => Milliseconds.Length == 0 ? 0 : Milliseconds.OrderBy( x => x ).ElementAt( Milliseconds.Length / 2 );
	}
	public sealed record Capability( string Feature, string Status, string Evidence );
	public sealed record Report( Workload Workload, string Os, string Architecture, string Runtime,
		string? GlVendor, string? GlRenderer, string? GlVersion, string? EglMode,
		IReadOnlyList<Sample> Samples, IReadOnlyList<Capability> Capabilities,
		IReadOnlyDictionary<string, long> RetainedResources );

	/// <summary>Only synchronous same-thread work is sampled. Warmup is outside both counters.</summary>
	public static Sample Measure( string stage, string definition, Action action, int warmups = 3,
		int repetitions = 7, bool allocations = true )
	{
		if ( string.IsNullOrWhiteSpace( stage ) || string.IsNullOrWhiteSpace( definition ) )
			throw new ArgumentException( "Stage and definition are required." );
		ArgumentNullException.ThrowIfNull( action );
		if ( warmups < 1 || repetitions < 1 ) throw new ArgumentOutOfRangeException( nameof( repetitions ) );
		for ( int i = 0; i < warmups; i++ ) action();
		var times = new double[repetitions];
		var bytes = allocations ? new long[repetitions] : null;
		for ( int i = 0; i < repetitions; i++ )
		{
			long startBytes = allocations ? GC.GetAllocatedBytesForCurrentThread() : 0;
			long start = Stopwatch.GetTimestamp();
			action();
			long end = Stopwatch.GetTimestamp();
			long endBytes = allocations ? GC.GetAllocatedBytesForCurrentThread() : 0;
			times[i] = (end - start) * 1000.0 / Stopwatch.Frequency;
			if ( bytes is not null ) bytes[i] = endBytes - startBytes;
		}
		return new Sample( stage, definition, times, bytes, "measured" );
	}

	public static Sample Untested( string stage, string definition, string reason ) =>
		new( stage, definition, Array.Empty<double>(), null, "untested", reason );

	/// <summary>Compare only identical workloads and stage definitions; allowance covers noisy hosts.</summary>
	public static bool WithinGuardrail( Workload baselineWorkload, Sample baseline,
		Workload currentWorkload, Sample current, double jitterFraction, out string reason )
	{
		if ( baselineWorkload != currentWorkload || baseline.Stage != current.Stage ||
			baseline.Definition != current.Definition || baseline.Status != "measured" ||
			current.Status != "measured" || baseline.Milliseconds.Length == 0 || current.Milliseconds.Length == 0 ||
			baseline.MedianMilliseconds <= 0 || !double.IsFinite( jitterFraction ) || jitterFraction < 0 )
			throw new ArgumentException( "Comparable measured workloads, stages and nonnegative jitter are required." );
		double limit = baseline.MedianMilliseconds * (1 + jitterFraction);
		reason = $"{current.MedianMilliseconds:F4} ms versus {baseline.MedianMilliseconds:F4} ms; limit {limit:F4} ms ({jitterFraction:P0} jitter)";
		return current.MedianMilliseconds <= limit;
	}

	public static Workload Describe( string name, string assetKind, ViewportMesh mesh, int textures = 0 )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		return new Workload( name, assetKind, (mesh.Vertices?.Length ?? 0) / ViewportMesh.FloatsPerVertex,
			mesh.TriangleCount, mesh.Batches?.Length ?? 0, mesh.Rig?.Count ?? 0, textures );
	}

	/// <summary>Explicit hardware-only EGL probe. Must run on the GL owning thread; no display fallback.</summary>
	public static (string Vendor, string Renderer, string Version, string Mode) ProbeHardware( HeadlessGlContext context )
	{
		ArgumentNullException.ThrowIfNull( context );
		var gl = context.Gl;
		string vendor = gl.GetStringS( StringName.Vendor ) ?? "";
		string renderer = gl.GetStringS( StringName.Renderer ) ?? "";
		string version = gl.GetStringS( StringName.Version ) ?? "";
		if ( string.IsNullOrWhiteSpace( vendor ) || string.IsNullOrWhiteSpace( renderer ) ||
			renderer.Contains( "llvmpipe", StringComparison.OrdinalIgnoreCase ) ||
			renderer.Contains( "softpipe", StringComparison.OrdinalIgnoreCase ) ||
			renderer.Contains( "software", StringComparison.OrdinalIgnoreCase ) )
			throw new NotSupportedException( $"Hardware GL required, reported {vendor} / {renderer}." );
		return (vendor, renderer, version, "EGL device/pbuffer; no display server or present/vsync");
	}

	/// <summary>Separate CPU submission and completed GPU duration; Finish is verification-only.</summary>
	public static (Sample Submission, Sample Completion) MeasureUpload( HeadlessGlContext context,
		Func<ViewportMesh> createMesh, int repetitions = 5 )
	{
		ProbeHardware( context );
		ArgumentNullException.ThrowIfNull( createMesh );
		if ( repetitions < 1 ) throw new ArgumentOutOfRangeException( nameof( repetitions ) );
		var submit = new double[repetitions]; var complete = new double[repetitions];
		var gl = context.Gl;
		for ( int i = -1; i < repetitions; i++ )
		{
			using var mesh = createMesh(); // extraction outside timed upload
			gl.Finish();
			long start = Stopwatch.GetTimestamp();
			try
			{
				mesh.Upload( gl );
				long submitted = Stopwatch.GetTimestamp();
				gl.Finish();
				long finished = Stopwatch.GetTimestamp();
				if ( i >= 0 )
				{
					submit[i] = (submitted - start) * 1000.0 / Stopwatch.Frequency;
					complete[i] = (finished - start) * 1000.0 / Stopwatch.Frequency;
				}
			}
			finally { mesh.ReleaseGpu( gl ); }
		}
		if ( gl.GetError() != GLEnum.NoError ) throw new InvalidOperationException( "GL upload/release error" );
		return (new( "cpu-upload-submission", "ViewportMesh.Upload only; fresh buffers, no extraction", submit, null, "measured" ),
			new( "gpu-upload-completion", "ViewportMesh.Upload plus gl.Finish, fresh buffers; no present/vsync", complete, null, "measured" ));
	}

	public static void WriteReport( string path, Report report )
	{
		ArgumentNullException.ThrowIfNull( report );
		Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( path ) )! );
		File.WriteAllText( path, JsonSerializer.Serialize( report, new JsonSerializerOptions { WriteIndented = true } ) );
	}
}
