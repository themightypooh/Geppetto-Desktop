using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// One open Linework stroke resampled as a pressure spline.
///
/// A spline is NOT a profile: it comes only from an open stroke (closed strokes are
/// fills and stay with <see cref="LineworkReader.Profiles"/>). <see cref="Points"/> is the
/// sampled polyline the rasterizer and any later animator walk; <see cref="ControlPoints"/>
/// is the stroke as drawn. <see cref="Pressures"/> runs parallel to <see cref="Points"/>
/// (0..1 per sampled point, linearly interpolated between control points), and the
/// stroke's drawn width at a sampled point is <see cref="BaseWidth"/> times its pressure.
/// </summary>
public sealed class PressureSpline
{
	public string StrokeId = "";
	public string Layer = "";
	public float BaseWidth = 1f;
	public List<Vec2> ControlPoints = new();
	public List<Vec2> Points = new();
	public List<float> Pressures = new();
	public bool Mirrored;

	/// <summary>Width in drawing units at sampled point <paramref name="index"/>.</summary>
	public float WidthAt( int index ) => BaseWidth * Pressures[index];
}

/// <summary>
/// The first of LineworkReader's stated NOT-dones: open strokes as pressure splines.
///
/// Additive only — it reads <see cref="LineworkStroke"/> values and never edits
/// LineworkReader, its parsing, or its profile semantics. In particular a closed stroke
/// nested inside another is still a second profile over in the reader; this file says
/// nothing about nesting at all, because splines only ever come from open strokes.
/// </summary>
public static class LineworkStrokes
{
	/// <summary>
	/// Pressure splines for every open stroke of a document. Layer selection follows
	/// <see cref="LineworkReader.Profiles"/> exactly: <paramref name="layerFilter"/> names a
	/// layer by name or id (case-insensitive); empty means every VISIBLE layer. Closed
	/// strokes are skipped, never splined.
	///
	/// Symmetry follows the reader too: when the document has sym on, each spline is
	/// ALSO emitted mirrored across x = SymX, original first, mirrored copy flagged
	/// <see cref="PressureSpline.Mirrored"/> with StrokeId suffixed "#mirror".
	/// </summary>
	public static List<PressureSpline> ToPressureSplines(
		LineworkDocument doc, string layerFilter = null, int samplesPerSegment = 8 )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		var out_ = new List<PressureSpline>();
		var filter = string.IsNullOrWhiteSpace( layerFilter ) ? null : layerFilter.Trim();
		foreach ( var layer in doc.Layers )
		{
			if ( filter is not null )
			{
				if ( !layer.Name.Equals( filter, StringComparison.OrdinalIgnoreCase )
					&& !layer.Id.Equals( filter, StringComparison.OrdinalIgnoreCase ) )
					continue;
			}
			else if ( !layer.Visible ) continue;

			foreach ( var stroke in layer.Strokes )
			{
				var spline = StrokeToSpline( stroke, layer.Name, samplesPerSegment );
				if ( spline is null ) continue;
				out_.Add( spline );
				if ( !doc.SymOn ) continue;
				out_.Add( Mirror( spline, doc.SymX ) );
			}
		}
		return out_;
	}

	/// <summary>
	/// One open stroke as a spline, or null when the stroke is closed or has no usable
	/// points. A single distinct point becomes a one-point spline (a dot, not an error);
	/// two points interpolate linearly; three or more run Catmull-Rom through the control
	/// points, clamped at the ends by duplicating the end controls, so the curve passes
	/// through every drawn point. Pressure is linear between control points — a pen's
	/// pressure ramp is a ramp, not a curve to overshoot.
	/// </summary>
	public static PressureSpline StrokeToSpline(
		LineworkStroke stroke, string layer = "", int samplesPerSegment = 8 )
	{
		if ( stroke is null ) throw new ArgumentNullException( nameof( stroke ) );
		if ( stroke.Closed ) return null;
		samplesPerSegment = Math.Clamp( samplesPerSegment, 1, 64 );

		var controls = new List<Vec2>();
		foreach ( var p in stroke.Points )
		{
			if ( controls.Count > 0 && (controls[^1] - p).LengthSquared < 1e-12f ) continue;
			controls.Add( p );
		}
		if ( controls.Count == 0 ) return null;

		var controlPressures = new List<float>( controls.Count );
		for ( var i = 0; i < controls.Count; i++ )
		{
			// Pressures may be shorter than Points (a hand-built stroke) or longer
			// (a trailing triple the reader dropped); either way a missing pressure
			// is full pressure, never zero — a stroke with no pressure data is a
			// stroke, not an invisible one.
			var p = i < stroke.Pressures.Count ? stroke.Pressures[i] : 1f;
			controlPressures.Add( Math.Clamp( p, 0f, 1f ) );
		}

		var spline = new PressureSpline
		{
			StrokeId = stroke.Id,
			Layer = layer,
			BaseWidth = stroke.Size > 0f ? stroke.Size : 1f,
			ControlPoints = controls,
		};

		if ( controls.Count == 1 )
		{
			spline.Points.Add( controls[0] );
			spline.Pressures.Add( controlPressures[0] );
			return spline;
		}

		if ( controls.Count == 2 )
		{
			for ( var i = 0; i <= samplesPerSegment; i++ )
			{
				var t = i / (float)samplesPerSegment;
				spline.Points.Add( controls[0] + (controls[1] - controls[0]) * t );
				spline.Pressures.Add( controlPressures[0] + (controlPressures[1] - controlPressures[0]) * t );
			}
			return spline;
		}

		for ( var s = 0; s < controls.Count - 1; s++ )
		{
			var p0 = s > 0 ? controls[s - 1] : controls[s];
			var p1 = controls[s];
			var p2 = controls[s + 1];
			var p3 = s + 2 < controls.Count ? controls[s + 2] : controls[s + 1];
			for ( var i = 0; i < samplesPerSegment; i++ )
			{
				var t = i / (float)samplesPerSegment;
				var t2 = t * t;
				var t3 = t2 * t;
				// Per-component: Vec2 has no float*Vec2 or unary-minus operator,
				// so the Catmull-Rom sum is evaluated on x and y separately.
				float Catmull( float v0, float v1, float v2, float v3 ) =>
					0.5f * ((2f * v1) + (v2 - v0) * t
						+ (2f * v0 - 5f * v1 + 4f * v2 - v3) * t2
						+ (-v0 + 3f * v1 - 3f * v2 + v3) * t3);
				spline.Points.Add( new Vec2(
					Catmull( p0.x, p1.x, p2.x, p3.x ),
					Catmull( p0.y, p1.y, p2.y, p3.y ) ) );
				spline.Pressures.Add( controlPressures[s] + (controlPressures[s + 1] - controlPressures[s]) * t );
			}
		}
		spline.Points.Add( controls[^1] );
		spline.Pressures.Add( controlPressures[^1] );
		return spline;
	}

	/// <summary>Length of a sampled polyline, in its own units. Zero for fewer than 2 points.</summary>
	public static float PolylineLength( IReadOnlyList<Vec2> points )
	{
		var total = 0f;
		for ( var i = 1; i < points.Count; i++ ) total += (points[i] - points[i - 1]).Length;
		return total;
	}

	static PressureSpline Mirror( PressureSpline spline, float symX )
	{
		List<Vec2> Flip( List<Vec2> pts )
		{
			var m = new List<Vec2>( pts.Count );
			foreach ( var p in pts ) m.Add( new Vec2( 2f * symX - p.x, p.y ) );
			return m;
		}
		return new PressureSpline
		{
			StrokeId = spline.StrokeId + "#mirror",
			Layer = spline.Layer,
			BaseWidth = spline.BaseWidth,
			ControlPoints = Flip( spline.ControlPoints ),
			Points = Flip( spline.Points ),
			Pressures = new List<float>( spline.Pressures ),
			Mirrored = true,
		};
	}
}
