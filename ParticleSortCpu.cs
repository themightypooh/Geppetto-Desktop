#nullable enable
using System;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>
/// CPU back-to-front sort reference for validating GPU particle sorts
/// (particles wave-6 lane P60; the GPU sort itself is lane 8's file,
/// <c>GpuParticleSort.cs</c>, whose verdict was still pending when this
/// reference was written — see the interface contract below).
///
/// BLEND-MODE NOTE: this sort exists for ALPHA blending only. Additive
/// blending (emits light, does not block it) and multiplicative blending
/// (blocks light, does not emit it) are commutative — order-independent,
/// no sort needed. Only particles that both emit AND occlude need a
/// back-to-front order, and Geppetto's billboards are alpha-blended, so
/// they do. A per-effect blend-mode switch (additive glow on = skip the
/// sort) is the honest alternative to sorting, not a second sort.
///
/// UNITS / SPACE: positions and camera position are in kernel inches,
/// Z-up, the same space as <c>ParticleSystem</c> / <c>ParticleState</c>.
/// This reference sorts by EUCLIDEAN camera distance, not by the
/// projected depth <c>ParticleSystem.SortBackToFront</c> uses
/// (depth = dot(pos - camera, normalized view forward), live slots only,
/// stable radix sort). The two orders AGREE whenever particles sit on
/// one view ray, and DIFFER for laterally offset particles at equal
/// depth but unequal distance — so if lane 8's verdict shows a
/// projected-depth key, that is an interface mismatch to report, not a
/// bug in either sort. This reference deliberately follows the roster's
/// stated GPU-sort concept: key = camera distance, index = particle slot.
///
/// EXPECTED GPU-SORT INTERFACE (what a GPU sort must satisfy for
/// <see cref="CompareToGpu"/> to report an exact match):
///   - Input: particle positions in slot order, camera position in the
///     same units/space as here; one output index per input slot.
///   - Output: a permutation of 0..n-1 (each slot exactly once).
///   - Key: camera distance (Euclidean), FARTHEST first.
///   - Tie-break: lower slot index first (deterministic; repeated sorts
///     of the same positions must return the same order).
///   - Dead slots are the CALLER's problem: this reference sorts every
///     position it is handed and does no alive filtering, so the caller
///     must pass live positions only (or the same slot subset to both
///     sorts) before comparing. <c>ParticleSystem.SortBackToFront</c>
///     filters dead slots itself; this reference does not.
///   - NaN/Infinity positions are rejected loudly by
///     <see cref="Validate"/> / <see cref="SortByDistance"/>, never
///     sorted silently: a NaN key has no order, so any "sorted" output
///     containing one would be meaningless.
/// </summary>
public static class ParticleSortCpu
{
	/// <summary>
	/// Returns particle indices sorted farthest-first by Euclidean
	/// distance from <paramref name="cameraPosition"/>. Ties (exactly
	/// equal float distances) break toward the lower index, so repeated
	/// sorts of the same positions are identical. The input array is
	/// never mutated. Throws on null, empty, or non-finite input (see
	/// <see cref="Validate"/>): a sort is meaningless without positions
	/// to sort or with a key that has no order.
	/// </summary>
	public static int[] SortByDistance( Vector3[] positions, Vector3 cameraPosition )
	{
		Validate( positions, cameraPosition );
		int n = positions.Length;
		var order = new int[n];
		var keys = new float[n];
		for ( int i = 0; i < n; i++ )
		{
			order[i] = i;
			keys[i] = Vector3.Distance( positions[i], cameraPosition );
		}
		Array.Sort( order, ( a, b ) =>
		{
			// Farthest first; exactly-equal distances fall through to
			// the index tie-break, which makes the order total (and
			// therefore deterministic) rather than comparison-dependent.
			int byDistance = keys[b].CompareTo( keys[a] );
			return byDistance != 0 ? byDistance : a.CompareTo( b );
		} );
		return order;
	}

	/// <summary>
	/// Compares a CPU reference order against a GPU sort order.
	/// Returns an exact-match flag plus, on mismatch, the FIRST differing
	/// position and the COUNT of differing positions, so a GPU failure
	/// is diagnosable ("wrong at position 3, 7 positions differ"), not
	/// just "wrong". A GPU order that is not a permutation of 0..n-1
	/// (wrong length, duplicated index, out-of-range index) is reported
	/// as a mismatch with <see cref="SortComparison.Problem"/> set, never
	/// thrown: a broken GPU order is exactly what this diagnostic is for.
	/// Null orders throw; empty-vs-empty compares exact.
	/// </summary>
	public static SortComparison CompareToGpu( int[] cpuOrder, int[] gpuOrder )
	{
		ArgumentNullException.ThrowIfNull( cpuOrder );
		ArgumentNullException.ThrowIfNull( gpuOrder );
		if ( cpuOrder.Length != gpuOrder.Length )
		{
			int shared = Math.Min( cpuOrder.Length, gpuOrder.Length );
			int first = -1, count = Math.Abs( cpuOrder.Length - gpuOrder.Length );
			for ( int i = 0; i < shared; i++ )
			{
				if ( cpuOrder[i] != gpuOrder[i] ) { if ( first < 0 ) first = i; count++; }
			}
			if ( first < 0 ) first = shared;
			return new SortComparison( false, first, count,
				$"Length mismatch: CPU order has {cpuOrder.Length} entries, GPU order has {gpuOrder.Length}." );
		}
		string? problem = CheckPermutation( gpuOrder );
		int firstDiff = -1, diffs = 0;
		for ( int i = 0; i < cpuOrder.Length; i++ )
		{
			if ( cpuOrder[i] != gpuOrder[i] ) { if ( firstDiff < 0 ) firstDiff = i; diffs++; }
		}
		if ( diffs == 0 && problem is null ) return new SortComparison( true, -1, 0, null );
		return new SortComparison( false, firstDiff, diffs, problem );
	}

	static string? CheckPermutation( int[] order )
	{
		var seen = new bool[order.Length];
		for ( int i = 0; i < order.Length; i++ )
		{
			int v = order[i];
			if ( v < 0 || v >= order.Length ) return $"GPU order is not a permutation: entry {v} at position {i} is out of range 0..{order.Length - 1}.";
			if ( seen[v] ) return $"GPU order is not a permutation: slot {v} appears more than once (first duplicate at position {i}).";
			seen[v] = true;
		}
		return null;
	}

	/// <summary>
	/// Loudly rejects input a sort cannot meaningfully handle: null or
	/// EMPTY position lists (a sort is required here, so "nothing to
	/// sort" is a caller bug, not a vacuous success), a non-finite
	/// camera position, or any non-finite (NaN/Infinity) position.
	/// Throws <see cref="ArgumentException"/> (or
	/// <see cref="ArgumentNullException"/> for null).
	/// </summary>
	public static void Validate( Vector3[] positions, Vector3 cameraPosition )
	{
		ArgumentNullException.ThrowIfNull( positions );
		if ( positions.Length == 0 ) throw new ArgumentException( "Cannot sort an empty position list: a sort is required here, so an empty list is a caller bug, not an empty order.", nameof( positions ) );
		if ( !Finite( cameraPosition ) ) throw new ArgumentException( "Camera position must be finite (no NaN/Infinity).", nameof( cameraPosition ) );
		for ( int i = 0; i < positions.Length; i++ )
		{
			if ( !Finite( positions[i] ) ) throw new ArgumentException( $"Position {i} must be finite (no NaN/Infinity).", nameof( positions ) );
		}
	}

	static bool Finite( Vector3 v ) => float.IsFinite( v.X ) && float.IsFinite( v.Y ) && float.IsFinite( v.Z );
}

/// <summary>
/// Result of <see cref="ParticleSortCpu.CompareToGpu"/>.
/// <see cref="FirstMismatchIndex"/> is -1 when the orders match exactly.
/// <see cref="Problem"/> is set only when the GPU order is structurally
/// broken (length mismatch / not a permutation); a plain wrong-but-valid
/// order has <see cref="Problem"/> null and the mismatch position/count
/// carry the diagnosis.
/// </summary>
public readonly record struct SortComparison( bool ExactMatch, int FirstMismatchIndex, int MismatchCount, string? Problem );
