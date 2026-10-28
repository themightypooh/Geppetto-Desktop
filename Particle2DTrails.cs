#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

// 2D trail data (particles wave-6 lane P14). Data only: this file never
// draws anything. The 2D suite's canvas code owns drawing; it consumes
// Particle2DTrailDrawRecord (points + widths, index-aligned, newest-first).
//
// Units: flat XY canvas pixels, Y-DOWN, seconds — the same as
// Particle2D. Distances are Euclidean pixel distances; Y-down changes
// nothing about the math, only about what "up" means to a caller.
//
// Order convention (deliberate, mirrors P24 / ParticleSystem.WriteStrand):
// element 0 is the head — the particle's newest position — and the last
// element is the oldest tail point. Every method in this file preserves
// that order; none of them reverses a trail. Consistency with the 3D
// strand order was preferred over an oldest-first 2D order so a canvas
// that already walks 3D strands newest-first walks 2D trails the same way.
//
// Pre-birth convention (mirrors P24's TrailBuffer rule): a newly spawned
// slot's trail starts FILLED with its spawn position, so the trail is a
// zero-length stub at the spawn point instead of a streak line back to
// the origin (0, 0). See Particle2DTrailBuffer.SpawnAt.
//
// Lifecycle the sweep wires (this file only supplies the pieces, it does
// not touch Particle2DSystem): on spawn call TrailSet.Spawn(slot, pos);
// once per sim step, for each LIVE slot only, call TrailSet.Record(slot,
// currentPosition) — dead slots are never written, so a dead particle's
// trail simply stops growing; on death call TrailSet.Clear(slot) before
// the slot is handed to a replacement, so the old trail cannot leak.
// A buffer that was never spawned, or was cleared, has Count 0 and no
// draw record: the sweep must skip empty buffers (BuildTrail throws on
// an empty snapshot — an empty trail is a bug at draw time, not a ribbon).
//
// Width-curve convention (mirrors P24): normalised position t runs 0 at
// the head (newest point) to 1 at the tail (oldest point). Widths in a
// draw record are FULL widths (not half-widths), baseWidth x curve(t).

/// <summary>One keyframe of a 2D trail width curve: normalised position
/// (0 = head, 1 = tail) and the width multiplier at that position.</summary>
public readonly struct Particle2DTrailWidthKey
{
	public float Position { get; }
	public float Multiplier { get; }

	public Particle2DTrailWidthKey( float position, float multiplier )
	{
		if ( !float.IsFinite( position ) || position < 0f || position > 1f )
			throw new ArgumentOutOfRangeException( nameof( position ), "Width-key position must be in [0, 1] (0 = head, 1 = tail)." );
		if ( !float.IsFinite( multiplier ) || multiplier < 0f )
			throw new ArgumentOutOfRangeException( nameof( multiplier ), "Width multiplier must be finite and >= 0." );
		Position = position;
		Multiplier = multiplier;
	}
}

/// <summary>Width multiplier over normalised trail position, as a
/// keyframe list with linear interpolation between keys. Outside the
/// key range the nearest key's multiplier holds (clamped, never
/// extrapolated). Mirrors P24's TrailWidthCurve in 2D.</summary>
public sealed class Particle2DTrailWidthCurve
{
	public IReadOnlyList<Particle2DTrailWidthKey> Keys { get; }

	public Particle2DTrailWidthCurve( IReadOnlyList<Particle2DTrailWidthKey> keys )
	{
		if ( keys is null ) throw new ArgumentNullException( nameof( keys ) );
		if ( keys.Count == 0 ) throw new ArgumentException( "A width curve needs at least one keyframe.", nameof( keys ) );
		for ( int i = 1; i < keys.Count; i++ )
			if ( keys[i].Position <= keys[i - 1].Position )
				throw new ArgumentException( "Width-key positions must be strictly increasing.", nameof( keys ) );
		Keys = new List<Particle2DTrailWidthKey>( keys );
	}

	/// <summary>Constant width along the whole trail.</summary>
	public static Particle2DTrailWidthCurve Constant( float multiplier ) => new( new[] { new Particle2DTrailWidthKey( 0f, multiplier ) } );

	/// <summary>Two-key taper: <paramref name="headMultiplier"/> at the
	/// head, <paramref name="tailMultiplier"/> at the tail. The default
	/// is a full-width head tapering to the given tail (0 = point).</summary>
	public static Particle2DTrailWidthCurve Taper( float tailMultiplier, float headMultiplier = 1f ) =>
		new( new[] { new Particle2DTrailWidthKey( 0f, headMultiplier ), new Particle2DTrailWidthKey( 1f, tailMultiplier ) } );

	/// <summary>Multiplier at normalised position <paramref name="t"/>
	/// (0 = head, 1 = tail). t outside [0, 1] is clamped to the ends.</summary>
	public float Evaluate( float t )
	{
		if ( !float.IsFinite( t ) ) throw new ArgumentOutOfRangeException( nameof( t ), "Normalised trail position must be finite." );
		t = Math.Clamp( t, 0f, 1f );
		if ( Keys.Count == 1 ) return Keys[0].Multiplier;
		if ( t <= Keys[0].Position ) return Keys[0].Multiplier;
		for ( int i = 1; i < Keys.Count; i++ )
		{
			if ( t > Keys[i].Position ) continue;
			var a = Keys[i - 1];
			var b = Keys[i];
			float f = ( t - a.Position ) / ( b.Position - a.Position );
			return a.Multiplier + ( b.Multiplier - a.Multiplier ) * f;
		}
		return Keys[Keys.Count - 1].Multiplier;
	}

	/// <summary>One width per trail point: baseWidth x curve(t) with
	/// t = i / (count - 1), head first. A single-point trail gets the
	/// head multiplier.</summary>
	public float[] WidthsFor( int count, float baseWidth )
	{
		Particle2DTrails.ValidateCount( count );
		Particle2DTrails.ValidateBaseWidth( baseWidth );
		var widths = new float[count];
		for ( int i = 0; i < count; i++ )
		{
			float t = count == 1 ? 0f : i / (float)( count - 1 );
			widths[i] = baseWidth * Evaluate( t );
		}
		return widths;
	}
}

/// <summary>The exact record a 2D trail produces for the canvas: the
/// shape the 2D suite consumes. <see cref="Points"/>[i] and
/// <see cref="Widths"/>[i] are the same trail station, index-aligned,
/// newest-first (index 0 = head, which coincides with the particle's
/// Particle2DDrawRecord.Position). Widths are full widths. This is a
/// sidecar record: Particle2DDrawRecord carries no trail field today,
/// so the sweep either adds a trail field to that record or carries a
/// parallel Particle2DTrailDrawRecord array in WriteDrawRecords order
/// (that decision is the sweep's, not this file's — see the header).
/// Data only; nothing here draws.</summary>
public readonly struct Particle2DTrailDrawRecord
{
	public Vector2[] Points { get; }
	public float[] Widths { get; }
	public int Count => Points.Length;

	public Particle2DTrailDrawRecord( Vector2[] points, float[] widths )
	{
		if ( points is null ) throw new ArgumentNullException( nameof( points ) );
		if ( widths is null ) throw new ArgumentNullException( nameof( widths ) );
		if ( points.Length != widths.Length )
			throw new ArgumentException( $"Trail points ({points.Length}) and widths ({widths.Length}) must have the same length." );
		for ( int i = 0; i < points.Length; i++ )
		{
			if ( !float.IsFinite( points[i].X ) || !float.IsFinite( points[i].Y ) )
				throw new ArgumentException( $"Trail point {i} has a non-finite component." );
			if ( !float.IsFinite( widths[i] ) || widths[i] < 0f )
				throw new ArgumentException( $"Trail width {i} must be finite and >= 0." );
		}
		Points = points;
		Widths = widths;
	}

	public static Particle2DTrailDrawRecord Empty => new( Array.Empty<Vector2>(), Array.Empty<float>() );

	/// <summary>Zero every point and width in place, so a stale record
	/// held past a particle's death draws nothing (all widths 0)
	/// instead of leaking the dead particle's shape. The arrays keep
	/// their length; the owning buffer should also be cleared (see
	/// <see cref="Particle2DTrailBuffer.Clear"/>).</summary>
	public void Clear()
	{
		Array.Clear( Points, 0, Points.Length );
		Array.Clear( Widths, 0, Widths.Length );
	}
}

/// <summary>One particle slot's trail: a ring of past positions,
/// newest-first on read. Capacity is fixed at construction (the sweep
/// owns the trail-length setting — this class only enforces that the
/// length is positive). A buffer that has never been spawned, or has
/// been cleared, holds no points at all: it is NOT padded with zeros,
/// so a never-spawned slot can never masquerade as a trail at the
/// origin. Record() on a full ring overwrites the oldest point.</summary>
public sealed class Particle2DTrailBuffer
{
	readonly Vector2[] _points;
	int _head;

	public Particle2DTrailBuffer( int trailLength )
	{
		if ( trailLength <= 0 )
			throw new ArgumentOutOfRangeException( nameof( trailLength ), "Trail length must be > 0." );
		_points = new Vector2[trailLength];
		_head = 0;
		Capacity = trailLength;
	}

	public int Capacity { get; }
	public int Count { get; private set; }
	public bool IsEmpty => Count == 0;

	/// <summary>Newest point. Throws when the buffer holds no points.</summary>
	public Vector2 Head
	{
		get
		{
			if ( Count == 0 ) throw new InvalidOperationException( "Trail buffer holds no points." );
			return _points[_head];
		}
	}

	/// <summary>Spawn fill: the whole ring starts at
	/// <paramref name="position"/>, so the newborn trail is a
	/// zero-length stub at the spawn point — no streak to the origin,
	/// and no zero padding. Overwrites any previous contents, so a
	/// slot respawned at a new position cannot leak its old trail
	/// even if Clear() was skipped.</summary>
	public void SpawnAt( Vector2 position )
	{
		Particle2DTrails.ValidatePoint( position, nameof( position ) );
		Array.Fill( _points, position );
		_head = 0;
		Count = Capacity;
	}

	/// <summary>Record one step's position as the new head. Call once
	/// per sim step for live slots only; dead slots are untouched by
	/// simply never calling this for them.</summary>
	public void Record( Vector2 position )
	{
		Particle2DTrails.ValidatePoint( position, nameof( position ) );
		if ( Count == 0 )
		{
			_head = 0;
			_points[0] = position;
			Count = 1;
			return;
		}
		_head = ( _head + 1 ) % Capacity;
		_points[_head] = position;
		if ( Count < Capacity ) Count++;
	}

	/// <summary>Newest-first copy of the valid points (length
	/// <see cref="Count"/>). Never aliases the ring, so the caller may
	/// resample or smooth the result without disturbing the buffer.</summary>
	public Vector2[] Snapshot()
	{
		var result = new Vector2[Count];
		for ( int i = 0; i < Count; i++ )
		{
			int idx = _head - i;
			if ( idx < 0 ) idx += Capacity;
			result[i] = _points[idx];
		}
		return result;
	}

	/// <summary>Zero the ring in place and drop the count to 0, so a
	/// dead trail cannot leak into the slot's replacement. Call on
	/// death, before the slot is reused.</summary>
	public void Clear()
	{
		Array.Clear( _points, 0, _points.Length );
		_head = 0;
		Count = 0;
	}
}

/// <summary>One trail buffer per particle slot, sized to a system's
/// capacity. Thin delegation: every slot rule lives in
/// <see cref="Particle2DTrailBuffer"/>; this class only owns the slot
/// range check and the per-slot collection.</summary>
public sealed class Particle2DTrailSet
{
	readonly Particle2DTrailBuffer[] _buffers;

	public Particle2DTrailSet( int slotCount, int trailLength )
	{
		if ( slotCount <= 0 )
			throw new ArgumentOutOfRangeException( nameof( slotCount ), "Trail set slot count must be > 0." );
		_buffers = new Particle2DTrailBuffer[slotCount];
		for ( int i = 0; i < slotCount; i++ ) _buffers[i] = new Particle2DTrailBuffer( trailLength );
		SlotCount = slotCount;
		TrailLength = trailLength;
	}

	public int SlotCount { get; }
	public int TrailLength { get; }

	public Particle2DTrailBuffer Buffer( int slot )
	{
		if ( slot < 0 || slot >= _buffers.Length )
			throw new ArgumentOutOfRangeException( nameof( slot ), $"Trail slot {slot} is out of range [0, { _buffers.Length })." );
		return _buffers[slot];
	}

	public void Spawn( int slot, Vector2 position ) => Buffer( slot ).SpawnAt( position );
	public void Record( int slot, Vector2 position ) => Buffer( slot ).Record( position );
	public void Clear( int slot ) => Buffer( slot ).Clear();
	public Vector2[] Snapshot( int slot ) => Buffer( slot ).Snapshot();
	public int Count( int slot ) => Buffer( slot ).Count;
	public Vector2 Head( int slot ) => Buffer( slot ).Head;
	public void ClearAll()
	{
		foreach ( var b in _buffers ) b.Clear();
	}
}

/// <summary>2D trail processing: resampling, smoothing, width curves.
/// Mirrors P24's ParticleTrails semantics in Vector2, newest-first.
/// All methods validate loudly and return new arrays — they never
/// mutate or alias the input list, so a buffer snapshot can be reused
/// for the next particle unchanged.</summary>
public static class Particle2DTrails
{
	/// <summary>Shared loud validation. Throws
	/// <see cref="ArgumentException"/> (or a subclass) on: null or
	/// empty point lists, non-finite points, non-positive
	/// spacing/count/window/baseWidth, or a null width curve. Empty
	/// lists are rejected wherever a trail is required — a trail with
	/// no points is a bug, not an empty ribbon.</summary>
	public static void ValidatePoint( Vector2 point, string name )
	{
		if ( !float.IsFinite( point.X ) || !float.IsFinite( point.Y ) )
			throw new ArgumentException( $"Trail point has a non-finite component.", name );
	}

	public static void ValidatePoints( IReadOnlyList<Vector2>? points )
	{
		if ( points is null ) throw new ArgumentNullException( nameof( points ) );
		if ( points.Count == 0 ) throw new ArgumentException( "A trail point list must not be empty.", nameof( points ) );
		for ( int i = 0; i < points.Count; i++ )
			if ( !float.IsFinite( points[i].X ) || !float.IsFinite( points[i].Y ) )
				throw new ArgumentException( $"Trail point {i} has a non-finite component.", nameof( points ) );
	}

	public static void ValidateSpacing( float spacing )
	{
		if ( !float.IsFinite( spacing ) || spacing <= 0f )
			throw new ArgumentOutOfRangeException( nameof( spacing ), "Resample spacing must be finite and > 0." );
	}

	public static void ValidateCount( int count )
	{
		if ( count <= 0 )
			throw new ArgumentOutOfRangeException( nameof( count ), "Resample point count must be > 0." );
	}

	public static void ValidateWindow( int window )
	{
		if ( window <= 0 )
			throw new ArgumentOutOfRangeException( nameof( window ), "Smoothing window must be > 0." );
	}

	public static void ValidateBaseWidth( float baseWidth )
	{
		if ( !float.IsFinite( baseWidth ) || baseWidth <= 0f )
			throw new ArgumentOutOfRangeException( nameof( baseWidth ), "Base width must be finite and > 0." );
	}

	/// <summary>Minimum-distance resampling: walk the trail keeping
	/// the first point, then a point only once it is at least
	/// <paramref name="spacing"/> from the last kept point. The final
	/// raw point is always kept (unless it duplicates the last kept
	/// point), so the tail endpoint is never dropped by the rule.
	/// Input order (newest-first) is preserved.</summary>
	public static Vector2[] ResampleBySpacing( IReadOnlyList<Vector2> points, float spacing )
	{
		ValidatePoints( points );
		ValidateSpacing( spacing );
		if ( points.Count == 1 ) return new[] { points[0] };
		var kept = new List<Vector2> { points[0] };
		for ( int i = 1; i < points.Count; i++ )
			if ( Vector2.Distance( points[i], kept[kept.Count - 1] ) >= spacing )
				kept.Add( points[i] );
		var last = points[points.Count - 1];
		if ( kept[kept.Count - 1] != last ) kept.Add( last );
		return kept.ToArray();
	}

	/// <summary>Even resampling by arc length: <paramref name="count"/>
	/// points evenly spaced along the trail's total length, endpoints
	/// included (first = newest/head, last = oldest/tail). A zero-length
	/// trail (all points identical) returns <paramref name="count"/>
	/// copies of that point — the count is never shrunk silently. A
	/// count of 1 returns just the head point.</summary>
	public static Vector2[] ResampleByCount( IReadOnlyList<Vector2> points, int count )
	{
		ValidatePoints( points );
		ValidateCount( count );
		if ( count == 1 ) return new[] { points[0] };
		if ( points.Count == 1 )
		{
			var single = new Vector2[count];
			Array.Fill( single, points[0] );
			return single;
		}
		var cumulative = new float[points.Count];
		for ( int i = 1; i < points.Count; i++ )
			cumulative[i] = cumulative[i - 1] + Vector2.Distance( points[i], points[i - 1] );
		float total = cumulative[points.Count - 1];
		if ( total <= 0f )
		{
			var flat = new Vector2[count];
			Array.Fill( flat, points[0] );
			return flat;
		}
		var result = new Vector2[count];
		int seg = 0;
		for ( int i = 0; i < count; i++ )
		{
			float target = total * i / ( count - 1 );
			if ( i == count - 1 ) { result[i] = points[points.Count - 1]; continue; }
			while ( seg < points.Count - 2 && cumulative[seg + 1] < target ) seg++;
			float segLen = cumulative[seg + 1] - cumulative[seg];
			float f = segLen <= 0f ? 0f : ( target - cumulative[seg] ) / segLen;
			result[i] = Vector2.Lerp( points[seg], points[seg + 1], f );
		}
		return result;
	}

	/// <summary>Moving-average smoothing. For each point, average
	/// <paramref name="window"/> consecutive samples centred on it
	/// (starting at i − window/2); indices past either end are clamped
	/// to the endpoint, so the endpoints are pulled toward themselves
	/// rather than dropped — the output always has exactly the input
	/// length, never fewer points. Window 1 is the identity.</summary>
	public static Vector2[] Smooth( IReadOnlyList<Vector2> points, int window )
	{
		ValidatePoints( points );
		ValidateWindow( window );
		if ( window == 1 ) return new List<Vector2>( points ).ToArray();
		var result = new Vector2[points.Count];
		int start0 = window / 2;
		for ( int i = 0; i < points.Count; i++ )
		{
			Vector2 sum = Vector2.Zero;
			for ( int k = 0; k < window; k++ )
			{
				int idx = Math.Clamp( i - start0 + k, 0, points.Count - 1 );
				sum += points[idx];
			}
			result[i] = sum / window;
		}
		return result;
	}

	/// <summary>Points + widths for a raw trail, no resampling: widths
	/// are baseWidth x curve(t), t head-to-tail.</summary>
	public static Particle2DTrailDrawRecord BuildTrail( IReadOnlyList<Vector2> points, Particle2DTrailWidthCurve curve, float baseWidth )
	{
		if ( curve is null ) throw new ArgumentNullException( nameof( curve ) );
		ValidatePoints( points );
		ValidateBaseWidth( baseWidth );
		return new Particle2DTrailDrawRecord( new List<Vector2>( points ).ToArray(), curve.WidthsFor( points.Count, baseWidth ) );
	}

	/// <summary>Points + widths for a buffer's current snapshot, no
	/// resampling. Throws on an empty buffer: a dead/never-spawned
	/// slot has no trail to draw, and the sweep must skip it.</summary>
	public static Particle2DTrailDrawRecord BuildTrail( Particle2DTrailBuffer buffer, Particle2DTrailWidthCurve curve, float baseWidth )
	{
		if ( buffer is null ) throw new ArgumentNullException( nameof( buffer ) );
		return BuildTrail( buffer.Snapshot(), curve, baseWidth );
	}

	/// <summary>Pipeline: optionally smooth first (window &gt; 1),
	/// then resample by minimum spacing, then attach width-curve
	/// widths. This is the canvas path: feed it a newest-first list
	/// and hand the result to the polyline builder.</summary>
	public static Particle2DTrailDrawRecord BuildTrailBySpacing( IReadOnlyList<Vector2> points, Particle2DTrailWidthCurve curve, float baseWidth, float spacing, int smoothWindow = 1 )
	{
		if ( curve is null ) throw new ArgumentNullException( nameof( curve ) );
		var processed = smoothWindow > 1 ? Smooth( points, smoothWindow ) : points;
		var resampled = ResampleBySpacing( processed, spacing );
		return new Particle2DTrailDrawRecord( resampled, curve.WidthsFor( resampled.Length, baseWidth ) );
	}

	/// <summary>Pipeline: optionally smooth first (window &gt; 1),
	/// then resample to an exact point count by arc length, then
	/// attach width-curve widths.</summary>
	public static Particle2DTrailDrawRecord BuildTrailByCount( IReadOnlyList<Vector2> points, Particle2DTrailWidthCurve curve, float baseWidth, int count, int smoothWindow = 1 )
	{
		if ( curve is null ) throw new ArgumentNullException( nameof( curve ) );
		var processed = smoothWindow > 1 ? Smooth( points, smoothWindow ) : points;
		var resampled = ResampleByCount( processed, count );
		return new Particle2DTrailDrawRecord( resampled, curve.WidthsFor( resampled.Length, baseWidth ) );
	}

	public static Particle2DTrailDrawRecord BuildTrailBySpacing( Particle2DTrailBuffer buffer, Particle2DTrailWidthCurve curve, float baseWidth, float spacing, int smoothWindow = 1 )
	{
		if ( buffer is null ) throw new ArgumentNullException( nameof( buffer ) );
		return BuildTrailBySpacing( buffer.Snapshot(), curve, baseWidth, spacing, smoothWindow );
	}

	public static Particle2DTrailDrawRecord BuildTrailByCount( Particle2DTrailBuffer buffer, Particle2DTrailWidthCurve curve, float baseWidth, int count, int smoothWindow = 1 )
	{
		if ( buffer is null ) throw new ArgumentNullException( nameof( buffer ) );
		return BuildTrailByCount( buffer.Snapshot(), curve, baseWidth, count, smoothWindow );
	}
}
