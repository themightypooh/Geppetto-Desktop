#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>
/// 3D trail buffers (lane P24): self-contained trail processing over
/// strand-history point lists. This file does NOT draw anything — the
/// strand layer (lane P02, <see cref="ParticleRenderLayers"/>) draws
/// ribbons; this file only prepares the point/width lists it consumes.
///
/// Units: kernel inches, Z-up, seconds — the same as
/// <see cref="ParticleSystem"/>. Point order is newest-first, exactly
/// the order <see cref="ParticleSystem.WriteStrand"/> writes: element 0
/// is the particle's current (head) position, the last element is the
/// oldest (tail) point. Every method below preserves that order; none
/// of them reverses a trail.
///
/// Width-curve convention: normalised position t runs 0 at the head
/// (newest point) to 1 at the tail (oldest point) — the same t the
/// strand layer already uses (i / (count - 1)). The head multiplier
/// is 1 in the default taper, so "head = 1" means a full-width head,
/// not a position: t = 0 is the head, and its width multiplier is 1
/// unless the caller's curve says otherwise.
/// </summary>

/// <summary>One keyframe of a trail width curve: normalised position
/// (0 = head, 1 = tail) and the width multiplier at that position.</summary>
public readonly struct TrailWidthKey
{
	public float Position { get; }
	public float Multiplier { get; }

	public TrailWidthKey( float position, float multiplier )
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
/// extrapolated).</summary>
public sealed class TrailWidthCurve
{
	public IReadOnlyList<TrailWidthKey> Keys { get; }

	public TrailWidthCurve( IReadOnlyList<TrailWidthKey> keys )
	{
		if ( keys is null ) throw new ArgumentNullException( nameof( keys ) );
		if ( keys.Count == 0 ) throw new ArgumentException( "A width curve needs at least one keyframe.", nameof( keys ) );
		for ( int i = 1; i < keys.Count; i++ )
			if ( keys[i].Position <= keys[i - 1].Position )
				throw new ArgumentException( "Width-key positions must be strictly increasing.", nameof( keys ) );
		Keys = new List<TrailWidthKey>( keys );
	}

	/// <summary>Constant width along the whole trail.</summary>
	public static TrailWidthCurve Constant( float multiplier ) => new( new[] { new TrailWidthKey( 0f, multiplier ) } );

	/// <summary>Two-key taper: full (or <paramref name="headMultiplier"/>)
/// at the head, <paramref name="tailMultiplier"/> at the tail. The
/// default is the strand layer's old shape with a controllable tail:
/// head 1, tail as given.</summary>
	public static TrailWidthCurve Taper( float tailMultiplier, float headMultiplier = 1f ) =>
		new( new[] { new TrailWidthKey( 0f, headMultiplier ), new TrailWidthKey( 1f, tailMultiplier ) } );

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

	/// <summary>One width per trail point: baseWidth × curve(t) with
/// t = i / (count - 1), head first. A single-point trail gets the
/// head multiplier.</summary>
	public float[] WidthsFor( int count, float baseWidth )
	{
		ParticleTrails.ValidateCount( count );
		ParticleTrails.ValidateBaseWidth( baseWidth );
		var widths = new float[count];
		for ( int i = 0; i < count; i++ )
		{
			float t = count == 1 ? 0f : i / (float)( count - 1 );
			widths[i] = baseWidth * Evaluate( t );
		}
		return widths;
	}
}

/// <summary>Point + width lists a strand/ribbon layer can consume
/// directly: <see cref="Points"/>[i] and <see cref="Widths"/>[i] are
/// the same trail station, newest-first (index 0 = head). Widths are
/// full widths (not half-widths): the strand layer halves them the
/// way it already halves Size × WidthScale.</summary>
public readonly struct TrailData
{
	public Vector3[] Points { get; }
	public float[] Widths { get; }
	public int Count => Points.Length;

	public TrailData( Vector3[] points, float[] widths )
	{
		if ( points is null ) throw new ArgumentNullException( nameof( points ) );
		if ( widths is null ) throw new ArgumentNullException( nameof( widths ) );
		if ( points.Length != widths.Length )
			throw new ArgumentException( $"Trail points ({points.Length}) and widths ({widths.Length}) must have the same length." );
		for ( int i = 0; i < points.Length; i++ )
		{
			if ( !float.IsFinite( points[i].X ) || !float.IsFinite( points[i].Y ) || !float.IsFinite( points[i].Z ) )
				throw new ArgumentException( $"Trail point {i} has a non-finite component." );
			if ( !float.IsFinite( widths[i] ) || widths[i] < 0f )
				throw new ArgumentException( $"Trail width {i} must be finite and >= 0." );
		}
		Points = points;
		Widths = widths;
	}

	public static TrailData Empty => new( Array.Empty<Vector3>(), Array.Empty<float>() );

	/// <summary>Zero every point and width in place, so a stale trail
/// held by a reused slot draws nothing (all widths 0) instead of
/// leaking the dead particle's shape into its replacement. The
/// arrays keep their length; callers that own the slot should also
/// drop/replace the struct (see <see cref="TrailBuffer.Clear"/>),
/// which does both.</summary>
	public void Clear()
	{
		Array.Clear( Points, 0, Points.Length );
		Array.Clear( Widths, 0, Widths.Length );
	}
}

/// <summary>One reusable trail slot (one particle's trail). A dead
/// particle's trail must never leak into the particle that reuses
/// its slot: call <see cref="Clear"/> when the particle dies, before
/// the slot is handed to a replacement, and the old points/widths
/// are zeroed and the slot reads empty.</summary>
public sealed class TrailBuffer
{
	public TrailData Current { get; private set; } = TrailData.Empty;
	public int Count => Current.Count;

	public void Set( TrailData data ) => Current = data;

	public void Clear()
	{
		Current.Clear();
		Current = TrailData.Empty;
	}
}

/// <summary>Trail processing: resampling, smoothing, width curves.
/// All methods validate loudly (see <see cref="Validate"/>) and
/// return new arrays — they never mutate or alias the input list,
/// so a <see cref="ParticleSystem.WriteStrand"/> buffer can be
/// passed in and reused for the next particle unchanged.</summary>
public static class ParticleTrails
{
	/// <summary>Shared loud validation. Throws
/// <see cref="ArgumentException"/> (or a subclass) on: null or empty
/// point lists, non-finite points, non-positive spacing/count/window/
/// baseWidth, or a null/empty width curve. Empty lists are rejected
/// wherever a trail is required — a trail with no points is a bug,
/// not an empty ribbon.</summary>
	public static void Validate( IReadOnlyList<Vector3>? points, float spacing, int count, int window )
	{
		ValidatePoints( points );
		ValidateSpacing( spacing );
		ValidateCount( count );
		ValidateWindow( window );
	}

	public static void ValidatePoints( IReadOnlyList<Vector3>? points )
	{
		if ( points is null ) throw new ArgumentNullException( nameof( points ) );
		if ( points.Count == 0 ) throw new ArgumentException( "A trail point list must not be empty.", nameof( points ) );
		for ( int i = 0; i < points.Count; i++ )
			if ( !float.IsFinite( points[i].X ) || !float.IsFinite( points[i].Y ) || !float.IsFinite( points[i].Z ) )
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
	public static Vector3[] ResampleBySpacing( IReadOnlyList<Vector3> points, float spacing )
	{
		ValidatePoints( points );
		ValidateSpacing( spacing );
		if ( points.Count == 1 ) return new[] { points[0] };
		var kept = new List<Vector3> { points[0] };
		for ( int i = 1; i < points.Count; i++ )
			if ( Vector3.Distance( points[i], kept[kept.Count - 1] ) >= spacing )
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
	public static Vector3[] ResampleByCount( IReadOnlyList<Vector3> points, int count )
	{
		ValidatePoints( points );
		ValidateCount( count );
		if ( count == 1 ) return new[] { points[0] };
		if ( points.Count == 1 )
		{
			var single = new Vector3[count];
			Array.Fill( single, points[0] );
			return single;
		}
		var cumulative = new float[points.Count];
		for ( int i = 1; i < points.Count; i++ )
			cumulative[i] = cumulative[i - 1] + Vector3.Distance( points[i], points[i - 1] );
		float total = cumulative[points.Count - 1];
		if ( total <= 0f )
		{
			var flat = new Vector3[count];
			Array.Fill( flat, points[0] );
			return flat;
		}
		var result = new Vector3[count];
		int seg = 0;
		for ( int i = 0; i < count; i++ )
		{
			float target = total * i / ( count - 1 );
			if ( i == count - 1 ) { result[i] = points[points.Count - 1]; continue; }
			while ( seg < points.Count - 2 && cumulative[seg + 1] < target ) seg++;
			float segLen = cumulative[seg + 1] - cumulative[seg];
			float f = segLen <= 0f ? 0f : ( target - cumulative[seg] ) / segLen;
			result[i] = Vector3.Lerp( points[seg], points[seg + 1], f );
		}
		return result;
	}

	/// <summary>Moving-average smoothing. For each point, average
/// <paramref name="window"/> consecutive samples centred on it
/// (starting at i − window/2); indices past either end are clamped
/// to the endpoint, so the endpoints are pulled toward themselves
/// rather than dropped — the output always has exactly the input
/// length, never fewer points. Window 1 is the identity.</summary>
	public static Vector3[] Smooth( IReadOnlyList<Vector3> points, int window )
	{
		ValidatePoints( points );
		ValidateWindow( window );
		if ( window == 1 ) return new List<Vector3>( points ).ToArray();
		var result = new Vector3[points.Count];
		int start0 = window / 2;
		for ( int i = 0; i < points.Count; i++ )
		{
			Vector3 sum = Vector3.Zero;
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
/// are baseWidth × curve(t), t head-to-tail.</summary>
	public static TrailData BuildTrail( IReadOnlyList<Vector3> points, TrailWidthCurve curve, float baseWidth )
	{
		if ( curve is null ) throw new ArgumentNullException( nameof( curve ) );
		ValidatePoints( points );
		ValidateBaseWidth( baseWidth );
		return new TrailData( new List<Vector3>( points ).ToArray(), curve.WidthsFor( points.Count, baseWidth ) );
	}

	/// <summary>Pipeline: optionally smooth first (window &gt; 1),
/// then resample by minimum spacing, then attach width-curve
/// widths. This is the strand-layer path: feed it a
/// <see cref="ParticleSystem.WriteStrand"/> list (newest-first)
/// and hand the result to the ribbon builder.</summary>
	public static TrailData BuildTrailBySpacing( IReadOnlyList<Vector3> points, TrailWidthCurve curve, float baseWidth, float spacing, int smoothWindow = 1 )
	{
		if ( curve is null ) throw new ArgumentNullException( nameof( curve ) );
		var processed = smoothWindow > 1 ? Smooth( points, smoothWindow ) : points;
		var resampled = ResampleBySpacing( processed, spacing );
		return new TrailData( resampled, curve.WidthsFor( resampled.Length, baseWidth ) );
	}

	/// <summary>Pipeline: optionally smooth first (window &gt; 1),
/// then resample to an exact point count by arc length, then
/// attach width-curve widths.</summary>
	public static TrailData BuildTrailByCount( IReadOnlyList<Vector3> points, TrailWidthCurve curve, float baseWidth, int count, int smoothWindow = 1 )
	{
		if ( curve is null ) throw new ArgumentNullException( nameof( curve ) );
		var processed = smoothWindow > 1 ? Smooth( points, smoothWindow ) : points;
		var resampled = ResampleByCount( processed, count );
		return new TrailData( resampled, curve.WidthsFor( resampled.Length, baseWidth ) );
	}
}
