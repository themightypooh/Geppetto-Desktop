// Published performance targets (godlike Platform P1).
//
// The numbers the roadmap publishes, as data — not as comments, not as a
// wiki page. The CI perf suite (PerfSuite, host-side) runs these targets and
// FAILS on regression; the final-integrate acceptance measures against the
// same list, so there is one bar, not two.
//
// HONESTY RULES for these numbers (from the roadmap and the handoff rules):
//   - Headless GPU duration is NOT monitor FPS. The suite labels every
//     headless sample "headless" and compares it against headless baselines.
//     A headless 30 fps is not a claim of 30 fps on a monitor.
//   - The baseline to beat is 17.5 fps headless at 400k faces (1.1 GB heap),
//     measured 2026-11-12. It is the number to beat FIRST, in the viewport,
//     not headless only — the suite reports the viewport measurement when a
//     GL context is present and the headless proxy otherwise, and says which.
//   - "Openable" means: the scene loads without running out of memory and
//     renders a first frame. It is a capacity target, not a frame-rate one,
//     and its budget is the open time (load + first frame).
#nullable enable
using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>What a target measures: a frame rate, a latency, or an open.</summary>
public enum PerfTargetKind
{
	/// <summary>Sustained frame time (one frame of a running viewport).</summary>
	Frame,
	/// <summary>One-shot latency (stroke to updated mesh, upload, open).</summary>
	Latency,
	/// <summary>Capacity: the scene opens and renders a first frame at all.</summary>
	Capacity,
}

/// <summary>One published performance target: a name, what it measures, and
/// the budget in milliseconds. The budget is the median of the measured
/// samples (see PerformanceLifecycleMatrix.Sample.MedianMilliseconds).</summary>
public sealed class PerfTarget
{
	/// <summary>Short name, used in the suite report and the failure message.</summary>
	public string Name = "";
	/// <summary>What workload this measures, in plain words.</summary>
	public string Workload = "";
	/// <summary>Frame, Latency or Capacity.</summary>
	public PerfTargetKind Kind;
	/// <summary>Budget in milliseconds (median). Frame targets are 1000/fps.</summary>
	public double BudgetMs;
	/// <summary>Where the number comes from (roadmap section, benchmark).</summary>
	public string Source = "";
}

/// <summary>The published targets, and the baseline they have to beat.</summary>
public static class PerfTargets
{
	/// <summary>5M faces interactive at 30 fps: one frame every 33.3 ms while
	/// orbiting/sculpting. The credibility bar (roadmap §10).</summary>
	public static readonly PerfTarget Interactive5M = new()
	{
		Name = "5M faces interactive",
		Workload = "5,000,000 faces, orbit/sculpt, proxies on",
		Kind = PerfTargetKind.Frame,
		BudgetMs = 1000.0 / 30,
		Source = "godlike roadmap §10 Platform P1 (Nanite bar: tens of millions streamed)",
	};

	/// <summary>10M+ faces openable with proxies: the scene loads and renders a
	/// first frame. Capacity target — the budget is the open (load + first
	/// frame), not a frame rate.</summary>
	public static readonly PerfTarget Open10M = new()
	{
		Name = "10M+ openable with proxies",
		Workload = "10,000,000+ faces, proxies on, coarsest level resident",
		Kind = PerfTargetKind.Capacity,
		BudgetMs = 5000,
		Source = "godlike roadmap §10 Platform P1",
	};

	/// <summary>60 fps target for orbit/sculpt on a 1M-face mesh: one frame
	/// every 16.7 ms.</summary>
	public static readonly PerfTarget Orbit1M60 = new()
	{
		Name = "1M faces orbit/sculpt 60 fps",
		Workload = "1,000,000 faces, orbit/sculpt",
		Kind = PerfTargetKind.Frame,
		BudgetMs = 1000.0 / 60,
		Source = "godlike roadmap §10 Platform P1",
	};

	/// <summary>Brush latency under ~16 ms: stroke to updated mesh on screen.
	/// Measured headless as stroke edit + re-extract + upload submission (no
	/// input latency, no present — labelled as such in the report).</summary>
	public static readonly PerfTarget BrushLatency = new()
	{
		Name = "brush latency under 16 ms",
		Workload = "one stroke-sized edit, re-extract + upload submission",
		Kind = PerfTargetKind.Latency,
		BudgetMs = 16,
		Source = "godlike roadmap §10 Platform P1",
	};

	/// <summary>All published targets, in the order the suite runs them.</summary>
	public static readonly IReadOnlyList<PerfTarget> All = new[]
	{
		Interactive5M, Open10M, Orbit1M60, BrushLatency,
	};

	/// <summary>The number to beat first: 17.5 fps headless at 400k faces,
	/// 1.1 GB heap, measured 2026-11-12. Any target run reports its margin
	/// against this baseline as well as against its own budget.</summary>
	public const double BaselineFps400k = 17.5;
	/// <summary>400k faces, the mesh the baseline was measured on.</summary>
	public const int BaselineFaces400k = 400_000;
	/// <summary>1.1 GB heap at the baseline measurement.</summary>
	public const double BaselineHeapGb400k = 1.1;

	/// <summary>Check a measured median against a target. Returns (pass,
	/// margin, reason): margin is budget minus median in ms (positive is
	/// headroom, negative is the overage); reason is the one-line verdict.</summary>
	public static (bool Pass, double MarginMs, string Reason) Check( PerfTarget target, double medianMs )
	{
		ArgumentNullException.ThrowIfNull( target );
		if ( !double.IsFinite( medianMs ) || medianMs < 0 ) throw new ArgumentOutOfRangeException( nameof( medianMs ), "A measured median must be a finite non-negative number of milliseconds." );
		var margin = target.BudgetMs - medianMs;
		var pass = medianMs <= target.BudgetMs;
		var reason = pass
			? $"{target.Name}: {medianMs:F2} ms median, {margin:F2} ms under the {target.BudgetMs:F2} ms budget"
			: $"{target.Name}: {medianMs:F2} ms median, {-margin:F2} ms OVER the {target.BudgetMs:F2} ms budget";
		return (pass, margin, reason);
	}
}
