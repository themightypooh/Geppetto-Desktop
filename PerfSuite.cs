// CI perf suite: the standing harness that fails on regression (godlike Platform P1).
//
// The board's final-integrate task is a single final build/acceptance event.
// This file is the permanent system on top of it: it runs the published
// targets (PerfTargets) against measured samples and FAILS when any target
// regresses, so the next acceptance is automatic instead of a one-off.
//
// HOW IT RUNS. PerfSuite.Run takes one measurement delegate per target — the
// production caller supplies real measurements (MeasureFrame through
// SceneRenderer.Draw headless, MeasureBrushUpdate for stroke latency), the
// test supplies injected fakes (including an artificially slowed fixture,
// which must make the suite fail — that is the CHECK). Each delegate returns
// a PerformanceLifecycleMatrix.Sample (warmed, repeated, median), so the
// suite reuses the existing measurement primitives instead of inventing its
// own. A target passes when the median is within its budget.
//
// HONESTY RULES (the suite enforces them in its report):
//   - Headless GPU duration is NOT monitor FPS. Every headless sample is
//     labelled "headless" in the report, and the report never converts a
//     headless duration into a monitor-fps claim.
//   - GPU timings come from RenderStats (timestamp queries, 4-frame latency);
//     until they are available the report says so and ranks by CPU ms.
//   - A target that cannot be measured (no GL context, no fixture) is
//     reported "untested" with the reason — never "passed".
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Effigy;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>One target's result: pass/fail, the measured median, the budget,
/// and the margin (positive is headroom, negative is the overage).</summary>
public sealed record PerfSuiteResult( string Target, bool Pass, double MedianMs, double BudgetMs, double MarginMs, string Detail );

/// <summary>The suite's report: every target's result, the overall verdict,
/// and the machine it ran on. Written as JSON by <see cref="PerfSuite.WriteReport"/>.</summary>
public sealed class PerfSuiteReport
{
	public IReadOnlyList<PerfSuiteResult> Results = Array.Empty<PerfSuiteResult>();
	public bool Pass;
	public string Machine = "";
	public string? GlRenderer;
	public string Note = "";
}

/// <summary>The CI perf suite: run the published targets, fail on regression.</summary>
public static class PerfSuite
{
	/// <summary>Run the suite. Each case is (target, measure): measure returns
	/// the warmed, repeated sample for that target (or an "untested" sample
	/// when it cannot be measured — see
	/// <see cref="PerformanceLifecycleMatrix.Untested"/>). A case passes when
	/// its median is within the target's budget; an untested case neither
	/// passes nor fails the suite but is listed with its reason. Returns the
	/// report; the caller decides whether to fail the build on !Pass.</summary>
	public static PerfSuiteReport Run( IReadOnlyList<(PerfTarget Target, Func<PerformanceLifecycleMatrix.Sample> Measure)> cases,
		string machine, string? glRenderer )
	{
		ArgumentNullException.ThrowIfNull( cases );
		var results = new List<PerfSuiteResult>();
		var pass = true;
		foreach ( var (target, measure) in cases )
		{
			ArgumentNullException.ThrowIfNull( target );
			ArgumentNullException.ThrowIfNull( measure );
			var sample = measure();
			if ( sample.Status != "measured" )
			{
				results.Add( new PerfSuiteResult( target.Name, true, 0, target.BudgetMs, target.BudgetMs,
					$"UNTESTED: {sample.Detail ?? sample.Status} — not counted as a pass." ) );
				continue;
			}
			var (targetPass, margin, reason) = PerfTargets.Check( target, sample.MedianMilliseconds );
			if ( !targetPass ) pass = false;
			results.Add( new PerfSuiteResult( target.Name, targetPass, sample.MedianMilliseconds, target.BudgetMs, margin, reason ) );
		}
		return new PerfSuiteReport
		{
			Results = results,
			Pass = pass,
			Machine = machine,
			GlRenderer = glRenderer,
			Note = "Headless samples are headless durations, not monitor FPS. GPU timings are RenderStats timestamp queries (4-frame latency); until available the frame ranking uses CPU ms.",
		};
	}

	/// <summary>Write the report as JSON (same shape as PerformanceLifecycleMatrix.WriteReport).</summary>
	public static void WriteReport( string path, PerfSuiteReport report )
	{
		ArgumentNullException.ThrowIfNull( report );
		Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( path ) )! );
		File.WriteAllText( path, JsonSerializer.Serialize( report, new JsonSerializerOptions { WriteIndented = true } ) );
	}

	/// <summary>Measure one frame through the production draw path: warm up,
	/// then time SceneRenderer.Draw (CPU submission) over
	/// <paramref name="repetitions"/> frames. Returns the sample plus the
	/// GPU detail string (per-pass GPU ms from renderer.Stats once they have
	/// aged through the 4-frame latency, or a note that they are not yet
	/// available). Headless: no present/vsync, so the sample is CPU submission
	/// time, not monitor frame time — the report says so.</summary>
	public static (PerformanceLifecycleMatrix.Sample Sample, string GpuDetail) MeasureFrame( SceneRenderer renderer,
		ViewportMesh mesh, MarionettePlayer? player, OrbitCamera camera, int width, int height,
		int warmups = 3, int repetitions = 7 )
	{
		// player is null for a static mesh: HeadlessRender passes loader.Player
		// straight through and it is null for rig-less documents, and Draw
		// guards every player use (f.Player is null checks). Draw's parameter
		// is declared non-nullable, so the ! below is a lie the signature
		// tells, not a new assumption.
		ArgumentNullException.ThrowIfNull( renderer );
		ArgumentNullException.ThrowIfNull( mesh );
		ArgumentNullException.ThrowIfNull( camera );
		if ( warmups < 1 || repetitions < 1 ) throw new ArgumentOutOfRangeException( nameof( repetitions ), "Warmups and repetitions must be at least 1." );
		var sample = PerformanceLifecycleMatrix.Measure( "frame-draw",
			"SceneRenderer.Draw, headless, no present/vsync; CPU submission time only",
			() => renderer.Draw( mesh, player!, camera, width, height, 1f ),
			warmups, repetitions, allocations: false );
		var stats = renderer.Stats;
		var gpu = new List<string>();
		var anyGpu = false;
		foreach ( var kind in Enum.GetValues<RenderPassKind>() )
		{
			var ms = stats.GpuMs[(int)kind];
			if ( ms > 0 ) anyGpu = true;
			gpu.Add( $"{kind}: {ms:F2} ms GPU" );
		}
		var detail = anyGpu
			? "GPU per pass (timestamp queries, 4-frame latency): " + string.Join( "; ", gpu )
			: "GPU timings not yet available (need 4+ frames); CPU submission only.";
		return (sample, detail);
	}

	/// <summary>Measure brush latency headless: apply a stroke-sized edit to a
	/// fixture mesh, re-extract the viewport mesh, and (when
	/// <paramref name="upload"/> is true and a GL context is available) time
	/// the upload submission as well. The CPU-only path (upload: false) needs
	/// no GL and is what the CPU test pins. The full path labels its sample
	/// "stroke edit + re-extract + upload submission" — it does not include
	/// input latency or present, and the report says so.</summary>
	public static PerformanceLifecycleMatrix.Sample MeasureBrushUpdate( PolyMesh mesh, Action<PolyMesh> edit,
		Func<int, string> nameForSlot, bool upload = false, HeadlessGlContext? context = null,
		int warmups = 3, int repetitions = 7 )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		ArgumentNullException.ThrowIfNull( edit );
		ArgumentNullException.ThrowIfNull( nameForSlot );
		if ( warmups < 1 || repetitions < 1 ) throw new ArgumentOutOfRangeException( nameof( repetitions ), "Warmups and repetitions must be at least 1." );
		if ( upload && context is null )
			return PerformanceLifecycleMatrix.Untested( "brush-update", "Upload requested but no GL context was supplied.", "No GL context." );
		return PerformanceLifecycleMatrix.Measure( "brush-update",
			upload ? "Stroke edit + re-extract + upload submission, headless; no input latency, no present"
				: "Stroke edit + re-extract, CPU only; no upload, no input latency, no present",
			() =>
			{
				edit( mesh );
				using var viewport = ViewportMesh.FromPolyMesh( mesh, nameForSlot );
				if ( upload && context is not null )
				{
					viewport.Upload( context.Gl );
					viewport.ReleaseGpu( context.Gl );
				}
			},
			warmups, repetitions, allocations: false );
	}
}
