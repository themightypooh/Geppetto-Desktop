using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Effigy;

// ============================================================================
// Hammer Desktop P2: parallel compile stage execution (multithreaded native
// build). Board task hammer-compile-parallel-stages.
//
// Runner-level orchestration only — the per-stage algorithms come from their
// own tickets (BSP build / PVS / lightmap bake live in CompileRunner, the
// BSP ticket's BspBuilder has its own parallel PVS). This file is the
// generic machinery CompileRunner's stages run on:
//
//   * a fixed worker pool sized to the machine (Parallel.For with
//     MaxDegreeOfParallelism, never an unbounded spawn per item);
//   * deterministic merge: results are returned in input order (index-ordered
//     array), and MergeByKey orders (key, value) pairs by stable key — so
//     the same input produces the same output bytes regardless of thread
//     count or completion order;
//   * progress aggregation: completion counts are delivered monotonically
//     (a stale count is dropped, never delivered out of order), so a
//     consumer that turns counts into percents never sees progress go
//     backwards;
//   * prompt cancel: the token is checked before every item and handed to
//     ParallelOptions, so cancelling stops all workers within one item's
//     work; a cancel surfaces as OperationCanceledException, which
//     CompileRunner.Run already maps to a cancelled result.
//
// The single-threaded fallback lives in CompileOptions (SingleThreaded /
// MaxDegreeOfParallelism = 1): with it, RunParallel runs the same items in
// the same order on the calling thread, so the fallback path is the old
// sequential behaviour, not a second implementation.
//
// Thread-safety contract: the work function must be pure with respect to
// shared state — it may read the shared input, but must not mutate anything
// another item (or the caller) can see. It writes only its own return value.
// CompileRunner's three uses satisfy this: per-brush polygon build (pure
// function of one brush), per-leaf PVS (pure function of one leaf + the
// read-only adjacency), per-face lightmap bake (pure function of one face
// polygon + read-only lights/solids). No UI code; backend only.
// ============================================================================

/// <summary>
/// Fixed worker pool + deterministic merge for one compile stage's
/// independent work items. See the file header for the contract.
/// Progress counts are REPORTED monotonically (see RunParallel); a
/// Progress&lt;T&gt; consumer may still observe them out of order.
/// </summary>
public static class ParallelStageRunner
{
	/// <summary>
	/// Resolve a requested degree of parallelism to a worker count:
	/// -1 or 0 = one worker per processor (the machine default, mirroring
	/// <c>BspBuildOptions.MaxDegreeOfParallelism</c>); 1 = single-threaded;
	/// anything larger is clamped to the processor count — the pool is sized
	/// to the machine, never larger. Always at least 1.
	/// </summary>
	public static int ResolveDegreeOfParallelism( int requested ) =>
		requested <= 0 ? Environment.ProcessorCount : Math.Min( requested, Environment.ProcessorCount );

	/// <summary>True when the resolved worker count is 1: the run is effectively single-threaded.</summary>
	public static bool IsSingleThreaded( int requested ) => ResolveDegreeOfParallelism( requested ) <= 1;

	/// <summary>
	/// Run one independent work item per input on a fixed worker pool and
	/// return the results in input order — the deterministic merge: result
	/// [i] is always the result for work[i], however the items completed.
	/// With a resolved degree of 1 the items run in order on the calling
	/// thread (the single-threaded fallback). Cancellation is prompt: the
	/// token is checked before every item and passed to ParallelOptions, so
	/// a cancel throws <see cref="OperationCanceledException"/> within one
	/// item's work. When <paramref name="completed"/> is given, it is
	/// REPORTED monotonically: each Report call carries a larger finished
	/// count than the one before it (a count that arrives late is dropped,
	/// never reported out of order), so a consumer can turn counts into a
	/// never-backwards percent. Note the limit: reporting order is not
	/// delivery order for a <see cref="Progress{T}"/> consumer — Progress
	/// posts asynchronously and does not preserve order (verified 2026-11-13
	/// on .NET 10: single-threaded rapid reports arrived shuffled). Use a
	/// synchronous <see cref="IProgress{T}"/> implementation when the
	/// consumer must observe the counts in order.
	/// </summary>
	public static TResult[] RunParallel<TWork, TResult>(
		IReadOnlyList<TWork> work,
		Func<TWork, CancellationToken, TResult> func,
		int maxDegreeOfParallelism,
		CancellationToken cancel,
		IProgress<int> completed = null )
	{
		if ( work is null ) throw new ArgumentNullException( nameof( work ) );
		if ( func is null ) throw new ArgumentNullException( nameof( func ) );
		var results = new TResult[work.Count];
		if ( work.Count == 0 ) return results;

		var degree = ResolveDegreeOfParallelism( maxDegreeOfParallelism );
		if ( degree <= 1 )
		{
			for ( var i = 0; i < work.Count; i++ )
			{
				cancel.ThrowIfCancellationRequested();
				results[i] = func( work[i], cancel );
				completed?.Report( i + 1 );
			}
			return results;
		}

		// Monotonic delivery: Interlocked counts are unique and increasing,
		// but two workers can finish in either order, so a raw Report could
		// deliver count 17 before count 16. Hold the gate and drop a stale
		// count instead of delivering it out of order.
		var done = 0;
		var lastReported = 0;
		var gate = new object();
		var options = new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancel };
		Parallel.For( 0, work.Count, options, i =>
		{
			cancel.ThrowIfCancellationRequested();
			results[i] = func( work[i], cancel );
			var finished = Interlocked.Increment( ref done );
			if ( completed is null ) return;
			lock ( gate )
			{
				if ( finished > lastReported )
				{
					lastReported = finished;
					completed.Report( finished );
				}
			}
		} );
		return results;
	}

	/// <summary>
	/// Deterministic merge for results that arrive keyed instead of indexed:
	/// order (key, value) pairs by stable key (ascending) and return the
	/// values in that order. Same keys + same values -> same order, whatever
	/// order the pairs were produced in.
	/// </summary>
	public static List<TValue> MergeByKey<TValue>( IEnumerable<(int Key, TValue Value)> pairs )
	{
		if ( pairs is null ) throw new ArgumentNullException( nameof( pairs ) );
		var sorted = pairs.ToList();
		sorted.Sort( ( a, b ) => a.Key.CompareTo( b.Key ) );
		return sorted.Select( p => p.Value ).ToList();
	}
}
