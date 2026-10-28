#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

// P20 — 2D draw-record LOD (particles session, wave 6).
//
// This file collapses DRAW RECORDS, never particles. The 2D sim keeps
// simulating every particle; this pass only shrinks the record list
// that WriteDrawRecords produced before the canvas consumes it. It
// never changes a particle's count, position, or any sim state — it
// takes an IReadOnlyList<Particle2DDrawRecord> and returns a NEW list,
// leaving the input untouched. It draws nothing and owns no canvas/UI
// (the 2D suite owns that); it is the records-only analogue of the 3D
// ParticleLod.cs (lane P35), mirroring its band-table shape but with
// 2D collapse rules instead of 3D spawn-rate/size multipliers.
//
// DISTANCE MEASURE (explicit — the 2D canvas has no 3D camera, so
// there is no camera distance to reuse). The measure is the Euclidean
// distance, in flat canvas pixels (Y-down), from a CALLER-SUPPLIED
// reference point to the record's Position:
//
//     distance = (record.Position - referencePoint).Length()
//
// The reference point is an input to Apply(), never a hidden constant.
// The expected source is the viewport centre in canvas coordinates —
// the sweep must decide the exact source (see the integration lines
// in the lane report) — but any point works: the caller owns it, and
// the tests prove a different reference point changes the result.
// On-screen size is NOT the measure: the draw record's Size field is
// preserved verbatim through every collapse rule, so size never
// decides a band here.
//
// BAND TABLE. A particle's record belongs to the FIRST band whose
// upper threshold the distance is STRICTLY BELOW (mirroring P35
// exactly). AT-THRESHOLD RULE: a distance exactly equal to a band's
// upper threshold does NOT belong to that band; it falls through to
// the next (farther) band. The last band's upper threshold is
// conventionally PositiveInfinity so every distance lands somewhere.
//
// COLLAPSE RULES (one per band; SampleStride is only meaningful for
// Reduced and is ignored for Full/Collapsed):
//   Full      — one record per particle, unchanged.
//   Reduced   — every Nth record in that band emits, where N is the
//               band's SampleStride and the counting is PER BAND, in
//               input order, 0-based: the 0th, Nth, 2Nth, ... records
//               assigned to that band are kept, the rest dropped.
//               Example: 10 records all in a stride-2 Reduced band
//               emit exactly 5 records (band-subsequence indices
//               0, 2, 4, 6, 8). Counting per band (not globally) keeps
//               the count exact no matter how bands interleave.
//   Collapsed — one record per grid cell: records in this band are
//               grouped by the cell containing their Position,
//               cell = (Floor(X / CellSize), Floor(Y / CellSize)),
//               and the FIRST record in input order per cell is
//               emitted UNCHANGED as that cell's representative
//               (no averaging, no synthesized position — the kept
//               record is byte-for-byte one of the inputs).
// The "no record at all below a size threshold" variant is NOT built:
// Collapsed always keeps one representative per occupied cell.
//
// BUDGET. MaxRecords caps the number of records Apply() returns, and
// the cap is applied AFTER band collapse (so the budget counts
// emitted records, not input particles). Selection priority is
// NEAREST FIRST: the MaxRecords emitted records with the smallest
// distances are kept, ties broken by earlier input order, and a near
// record is never dropped for a far one. Survivors keep their input
// order in the output (the output is not re-sorted nearest-first;
// only the kept SET is nearest-first). MaxRecords = 0 is legal and
// emits nothing; negative fails loudly.
//
// UNITS: flat XY canvas pixels, Y-down, seconds are irrelevant here
// (records are a snapshot, not a step). No randomness anywhere:
// every output is a deterministic function of (input, settings,
// reference point) — same inputs, same outputs, every run.

/// <summary>How one LOD band collapses its records. See the file header
/// for the exact meaning of each rule.</summary>
public enum Particle2DLodCollapse
{
	Full,
	Reduced,
	Collapsed,
}

/// <summary>One LOD band. A record belongs to the first band whose
/// UpperDistance its distance is strictly below; the last band's
/// UpperDistance is conventionally PositiveInfinity. SampleStride is
/// the Reduced sampling stride N (every Nth record in the band emits);
/// it is ignored for Full and Collapsed bands.</summary>
public readonly record struct Particle2DLodBand( float UpperDistance, Particle2DLodCollapse Collapse, int SampleStride );

/// <summary>Draw-record LOD + budget configuration. Validate() must be
/// called before any query; every query also re-checks the fields it
/// depends on, so a mutated settings object fails loudly instead of
/// silently applying a nonsense table.</summary>
public sealed class Particle2DLodSettings
{
	/// <summary>Distance bands, strictly increasing UpperDistance, at
	/// least one band. Default: near &lt; 200 px (full), mid &lt; 600 px
	/// (every 2nd record), far (one record per 32 px cell).</summary>
	public Particle2DLodBand[] Bands =
	{
		new( 200f, Particle2DLodCollapse.Full, 1 ),
		new( 600f, Particle2DLodCollapse.Reduced, 2 ),
		new( float.PositiveInfinity, Particle2DLodCollapse.Collapsed, 1 ),
	};

	/// <summary>Hard cap on records Apply() returns, applied after
	/// band collapse. 0 is legal and means "emit nothing"; negative
	/// is rejected.</summary>
	public int MaxRecords = 4096;

	/// <summary>Grid cell size in canvas pixels for Collapsed bands.
	/// Must be finite and &gt; 0.</summary>
	public float CellSize = 32f;

	public static Particle2DLodSettings Default() => new();

	/// <summary>Loudly rejects: a null or empty band table; unsorted
	/// (non strictly increasing) band thresholds; a non-finite
	/// threshold other than PositiveInfinity on the last band; a
	/// Reduced stride below 1 (any stride below 1 is rejected, even
	/// for bands that ignore it, so a typo cannot hide); a negative
	/// budget; a non-finite or non-positive cell size.</summary>
	public void Validate()
	{
		if ( Bands is null || Bands.Length == 0 )
			throw new ArgumentException( "LOD band table must contain at least one band." );
		float prev = float.NegativeInfinity;
		for ( int i = 0; i < Bands.Length; i++ )
		{
			var b = Bands[i];
			bool last = i == Bands.Length - 1;
			if ( !last && !float.IsFinite( b.UpperDistance ) )
				throw new ArgumentException( $"LOD band {i}: only the last band may use an infinite upper distance." );
			if ( !( b.UpperDistance > prev ) )
				throw new ArgumentException( $"LOD band {i}: upper distances must be strictly increasing (got {b.UpperDistance} after {prev}); unsorted band thresholds are rejected." );
			if ( b.SampleStride < 1 )
				throw new ArgumentOutOfRangeException( nameof( Bands ), $"LOD band {i}: sample stride must be >= 1 (got {b.SampleStride})." );
			prev = b.UpperDistance;
		}
		if ( MaxRecords < 0 )
			throw new ArgumentOutOfRangeException( nameof( MaxRecords ), "Record budget must not be negative." );
		if ( !float.IsFinite( CellSize ) || CellSize <= 0f )
			throw new ArgumentOutOfRangeException( nameof( CellSize ), "Cell size must be finite and > 0." );
	}

	/// <summary>Euclidean distance in canvas pixels from
	/// <paramref name="referencePoint"/> to a record position.</summary>
	public static float DistanceFrom( Vector2 position, Vector2 referencePoint )
		=> ( position - referencePoint ).Length();

	/// <summary>Index of the band for a distance in canvas pixels.
	/// Negative or NaN distances are rejected (a distance is >= 0).
	/// First band whose upper threshold the distance is strictly
	/// below wins, so a distance exactly on a threshold falls to the
	/// next (farther) band.</summary>
	public int BandIndexFor( float distance )
	{
		if ( float.IsNaN( distance ) || distance < 0f )
			throw new ArgumentOutOfRangeException( nameof( distance ), "Distance must be >= 0." );
		Validate();
		for ( int i = 0; i < Bands.Length; i++ )
			if ( distance < Bands[i].UpperDistance ) return i;
		return Bands.Length - 1;
	}

	/// <summary>Collapse rule for a distance in canvas pixels.</summary>
	public Particle2DLodCollapse CollapseFor( float distance ) => Bands[BandIndexFor( distance )].Collapse;

	/// <summary>Applies band collapse then the record budget to
	/// <paramref name="records"/> measured from
	/// <paramref name="referencePoint"/> (caller-supplied, typically
	/// the viewport centre in canvas coordinates). Returns a NEW list;
	/// the input list is never modified and its records are never
	/// rewritten — collapsed output records are verbatim inputs.
	/// Zero records in means zero records out, never an error. A
	/// non-finite reference point fails loudly.</summary>
	public List<Particle2DDrawRecord> Apply( IReadOnlyList<Particle2DDrawRecord> records, Vector2 referencePoint )
	{
		if ( records is null ) throw new ArgumentNullException( nameof( records ) );
		if ( !float.IsFinite( referencePoint.X ) || !float.IsFinite( referencePoint.Y ) )
			throw new ArgumentException( "Reference point must be finite." );
		Validate();
		var emitted = new List<Particle2DDrawRecord>();
		var distances = new List<float>();
		int[] bandCounters = new int[Bands.Length];
		var seenCells = new HashSet<(int, int)>();
		for ( int i = 0; i < records.Count; i++ )
		{
			var rec = records[i];
			float d = DistanceFrom( rec.Position, referencePoint );
			int band = BandIndexFor( d );
			switch ( Bands[band].Collapse )
			{
				case Particle2DLodCollapse.Full:
					emitted.Add( rec );
					distances.Add( d );
					break;
				case Particle2DLodCollapse.Reduced:
					int n = bandCounters[band]++;
					if ( n % Bands[band].SampleStride == 0 )
					{
						emitted.Add( rec );
						distances.Add( d );
					}
					break;
				case Particle2DLodCollapse.Collapsed:
					var cell = ( (int)MathF.Floor( rec.Position.X / CellSize ), (int)MathF.Floor( rec.Position.Y / CellSize ) );
					if ( seenCells.Add( cell ) )
					{
						emitted.Add( rec );
						distances.Add( d );
					}
					break;
			}
		}
		if ( emitted.Count <= MaxRecords ) return emitted;
		// Budget: nearest first, ties by earlier emitted (input) order.
		// Select the kept set by distance, then rebuild in input order.
		int[] order = new int[emitted.Count];
		for ( int i = 0; i < order.Length; i++ ) order[i] = i;
		Array.Sort( order, ( a, b ) =>
		{
			int c = distances[a].CompareTo( distances[b] );
			return c != 0 ? c : a.CompareTo( b );
		} );
		var kept = new bool[emitted.Count];
		for ( int i = 0; i < MaxRecords; i++ ) kept[order[i]] = true;
		var capped = new List<Particle2DDrawRecord>( MaxRecords );
		for ( int i = 0; i < emitted.Count; i++ )
			if ( kept[i] ) capped.Add( emitted[i] );
		return capped;
	}
}
