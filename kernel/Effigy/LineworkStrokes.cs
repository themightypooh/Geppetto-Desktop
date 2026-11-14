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

/// <summary>
/// What one bot-facing Linework command did. <see cref="Ok"/> is false with
/// <see cref="Error"/> set when the command was refused — a refused command never
/// touches the document, so a bot cannot mistake a no-op for a drawing.
/// Raster bounds are -1 when nothing was painted.
/// </summary>
public sealed class LineworkCommandResult
{
	public bool Ok;
	public string Error = "";
	public string StrokeId = "";
	public string Layer = "";
	public int StrokesDrawn;
	public int FillsDrawn;
	public int Pixels;
	public int MinX = -1, MinY = -1, MaxX = -1, MaxY = -1;
	public int StrokeCount;
	public int SampledPoints;

	public static LineworkCommandResult Fail( string error ) => new() { Ok = false, Error = error };
}

/// <summary>
/// Bot-facing Linework draw commands: stroke in with a pressure spline, fill,
/// erase, rasterize-and-read-back, and document save/reopen. This is the command
/// layer only — it builds <see cref="LineworkStroke"/> values and hands them to
/// the existing <see cref="LineworkStrokes"/> / <see cref="LineworkRaster"/> /
/// <see cref="LineworkReader"/> / <see cref="LineworkWriter"/> pipeline, so a
/// bot-drawn stroke rasterizes exactly like a hand-drawn one. It never invents a
/// second document model or a second rasterizer.
/// </summary>
public static class LineworkCommands
{
	/// <summary>
	/// Find a layer by name or id (case-insensitive, the reader's rule), creating
	/// it when <paramref name="createIfMissing"/> and it does not exist. An empty,
	/// over-long or control-character layer name is refused honestly instead of
	/// creating a layer the save round-trip cannot name.
	/// </summary>
	public static bool TryResolveLayer( LineworkDocument doc, string layerName, bool createIfMissing, out LineworkLayer layer, out string error )
	{
		layer = null;
		error = null;
		if ( doc is null ) { error = "no Linework document is open"; return false; }
		if ( string.IsNullOrWhiteSpace( layerName ) ) { error = "layer name is empty: name the layer to draw on"; return false; }
		var name = layerName.Trim();
		if ( name.Length > 128 ) { error = "layer name is longer than 128 characters"; return false; }
		foreach ( var c in name )
			if ( char.IsControl( c ) ) { error = "layer name contains a control character"; return false; }
		foreach ( var l in doc.Layers )
			if ( l.Name.Equals( name, StringComparison.OrdinalIgnoreCase )
				|| l.Id.Equals( name, StringComparison.OrdinalIgnoreCase ) )
			{ layer = l; return true; }
		if ( !createIfMissing ) { error = $"no layer named '{name}'"; return false; }
		layer = new LineworkLayer { Id = name, Name = name, Visible = true };
		doc.Layers.Add( layer );
		return true;
	}

	/// <summary>
	/// Draw an open stroke on a layer: control points plus per-point pressures
	/// (missing pressures are full pressure, the spline rule). The stroke is
	/// validated through the real pressure-spline path before it is added, so a
	/// stored stroke is always one the rasterizer can draw.
	/// </summary>
	public static LineworkCommandResult AddStroke( LineworkDocument doc, string layerName,
		IReadOnlyList<Vec2> points, IReadOnlyList<float> pressures,
		string tool, string color, float size, bool closed = false, float opacity = 1f )
	{
		var invalid = ValidateStrokeArgs( points, tool, color, size, closed, opacity );
		if ( invalid is not null ) return LineworkCommandResult.Fail( invalid );
		if ( !TryResolveLayer( doc, layerName, true, out var layer, out var error ) )
			return LineworkCommandResult.Fail( error );
		return AddStrokeCore( layer, points, pressures, tool, color, size, closed, opacity );
	}

	/// <summary>Fill a closed ring on a layer with a solid colour (a closed pen stroke).</summary>
	public static LineworkCommandResult Fill( LineworkDocument doc, string layerName,
		IReadOnlyList<Vec2> ring, string color, float opacity = 1f )
	{
		var invalid = ValidateStrokeArgs( ring, "pen", color, 1f, true, opacity );
		if ( invalid is not null ) return LineworkCommandResult.Fail( invalid );
		if ( !TryResolveLayer( doc, layerName, true, out var layer, out var error ) )
			return LineworkCommandResult.Fail( error );
		return AddStrokeCore( layer, ring, null, "pen", color, 1f, true, opacity );
	}

	/// <summary>
	/// Erase along a stroke: an eraser stroke, so the rasterizer clears exactly
	/// the dabs the stroke covers and nothing else. Erasing never deletes strokes
	/// from the document — the eraser stroke itself is the record.
	/// </summary>
	public static LineworkCommandResult Erase( LineworkDocument doc, string layerName,
		IReadOnlyList<Vec2> points, float size, IReadOnlyList<float> pressures = null )
	{
		var invalid = ValidateStrokeArgs( points, "eraser", "#000000", size, false, 1f );
		if ( invalid is not null ) return LineworkCommandResult.Fail( invalid );
		if ( !TryResolveLayer( doc, layerName, true, out var layer, out var error ) )
			return LineworkCommandResult.Fail( error );
		return AddStrokeCore( layer, points, pressures, "eraser", "#000000", size, false, 1f );
	}

	/// <summary>
	/// Validate a stroke's arguments BEFORE any layer is resolved, so a refused
	/// command never leaves a layer behind. This mirrors the checks in
	/// <see cref="AddStrokeCore"/> exactly — the two must stay in sync; the core
	/// keeps its own checks as a second line of defence. Returns null when valid,
	/// else the refusal message.
	/// </summary>
	static string ValidateStrokeArgs( IReadOnlyList<Vec2> points,
		string tool, string color, float size, bool closed, float opacity )
	{
		if ( points is null || points.Count == 0 )
			return "stroke has no points: give at least one point";
		if ( tool is null || !(tool.Equals( "pen", StringComparison.OrdinalIgnoreCase )
			|| tool.Equals( "eraser", StringComparison.OrdinalIgnoreCase )) )
			return $"tool '{tool}' is not pen or eraser";
		if ( !LineworkRaster.TryParseColor( color, out _, out _, out _ ) )
			return $"color '{color}' is not #RRGGBB";
		if ( !float.IsFinite( size ) || size <= 0f )
			return "size must be a positive number";
		if ( !float.IsFinite( opacity ) || opacity <= 0f || opacity > 1f )
			return "opacity must be above 0 and at most 1";
		if ( closed && !LineworkReader.CleanRing( new List<Vec2>( points ), out _ ) )
			return "a closed fill needs at least 3 distinct points";
		return null;
	}

	static LineworkCommandResult AddStrokeCore( LineworkLayer layer,
		IReadOnlyList<Vec2> points, IReadOnlyList<float> pressures,
		string tool, string color, float size, bool closed, float opacity )
	{
		if ( points is null || points.Count == 0 )
			return LineworkCommandResult.Fail( "stroke has no points: give at least one point" );
		if ( tool is null || !(tool.Equals( "pen", StringComparison.OrdinalIgnoreCase )
			|| tool.Equals( "eraser", StringComparison.OrdinalIgnoreCase )) )
			return LineworkCommandResult.Fail( $"tool '{tool}' is not pen or eraser" );
		if ( !LineworkRaster.TryParseColor( color, out _, out _, out _ ) )
			return LineworkCommandResult.Fail( $"color '{color}' is not #RRGGBB" );
		if ( !float.IsFinite( size ) || size <= 0f )
			return LineworkCommandResult.Fail( "size must be a positive number" );
		if ( !float.IsFinite( opacity ) || opacity <= 0f || opacity > 1f )
			return LineworkCommandResult.Fail( "opacity must be above 0 and at most 1" );
		if ( closed && !LineworkReader.CleanRing( new List<Vec2>( points ), out _ ) )
			return LineworkCommandResult.Fail( "a closed fill needs at least 3 distinct points" );
		var stroke = new LineworkStroke
		{
			Id = NextStrokeId( layer ),
			Tool = tool.ToLowerInvariant(),
			Color = color.Trim(),
			Size = size,
			Opacity = opacity,
			Closed = closed,
		};
		foreach ( var p in points ) stroke.Points.Add( p );
		for ( var i = 0; i < points.Count; i++ )
			stroke.Pressures.Add( pressures is not null && i < pressures.Count
				? Math.Clamp( pressures[i], 0f, 1f ) : 1f );
		int sampled;
		if ( closed )
			sampled = stroke.Points.Count;
		else
		{
			var spline = LineworkStrokes.StrokeToSpline( stroke, layer.Name );
			if ( spline is null )
				return LineworkCommandResult.Fail( "stroke has no usable points after cleaning" );
			sampled = spline.Points.Count;
		}
		layer.Strokes.Add( stroke );
		return new LineworkCommandResult
		{
			Ok = true,
			StrokeId = stroke.Id,
			Layer = layer.Name,
			StrokeCount = layer.Strokes.Count,
			SampledPoints = sampled,
		};
	}

	static string NextStrokeId( LineworkLayer layer )
	{
		var n = layer.Strokes.Count + 1;
		while ( true )
		{
			var id = "stroke-" + n;
			var taken = false;
			foreach ( var s in layer.Strokes ) if ( s.Id == id ) { taken = true; break; }
			if ( !taken ) return id;
			n++;
		}
	}

	/// <summary>
	/// Rasterize a document (or one layer) into a fresh canvas and hand the canvas
	/// back for pixel read-back. The map auto-fits the drawn points, so the same
	/// drawing lands on the same texels whatever its drawing units were. The canvas
	/// is never cleared or reused between calls — each rasterize is its own evidence.
	/// </summary>
	public static (PaintCanvas Canvas, LineworkRasterResult Result, string Error) Rasterize(
		LineworkDocument doc, int width, int height, string layerFilter = null, LineworkRasterOptions options = null )
	{
		if ( doc is null ) return (null, null, "no Linework document is open");
		if ( width <= 0 || height <= 0 ) return (null, null, "raster size must be positive");
		var filter = string.IsNullOrWhiteSpace( layerFilter ) ? null : layerFilter.Trim();
		var points = new List<Vec2>();
		foreach ( var layer in doc.Layers )
		{
			if ( filter is not null )
			{
				if ( !layer.Name.Equals( filter, StringComparison.OrdinalIgnoreCase )
					&& !layer.Id.Equals( filter, StringComparison.OrdinalIgnoreCase ) )
					continue;
			}
			else if ( !layer.Visible ) continue;
			foreach ( var s in layer.Strokes ) points.AddRange( s.Points );
		}
		if ( points.Count == 0 ) return (null, null, "nothing to rasterize: the document has no points");
		var map = RasterMap.AutoFitForPoints( points, width, height, 1f );
		var canvas = new PaintCanvas( width, height );
		var result = LineworkRaster.RenderDocument( canvas, doc, layerFilter, map, options );
		return (canvas, result, null);
	}

	/// <summary>A fresh, empty document. Layers are created on demand by the first draw.</summary>
	public static LineworkDocument NewDocument( string name = "" ) => new() { Name = name ?? "" };

	/// <summary>Save a document to a .linework file. False + error on failure; nothing is written then.</summary>
	public static bool TrySave( LineworkDocument doc, string path, out string error )
	{
		error = null;
		if ( doc is null ) { error = "no Linework document is open"; return false; }
		if ( string.IsNullOrWhiteSpace( path ) ) { error = "save path is empty"; return false; }
		return LineworkWriter.TrySave( doc, path.Trim(), out error );
	}

	/// <summary>Load a .linework file (path, or a drawing id under ~/.local/share/linework/drawings/).</summary>
	public static bool TryLoadFile( string path, out LineworkDocument doc, out string error )
	{
		doc = null;
		error = null;
		if ( string.IsNullOrWhiteSpace( path ) ) { error = "load path is empty"; return false; }
		return LineworkReader.TryLoad( path.Trim(), out doc, out _, out error );
	}

	/// <summary>Parse .linework JSON text. False + error on bad input, never a throw.</summary>
	public static bool TryLoadJson( string json, out LineworkDocument doc, out string error ) =>
		LineworkReader.TryParse( json, out doc, out error );
}
