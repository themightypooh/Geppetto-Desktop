using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// How a closed stroke nested inside another closed stroke is rasterized. This is
/// a NEW, explicit mode — it never changes the reader's semantics, where a nested
/// closed stroke is a second profile, not an inferred hole.
/// </summary>
public enum LineworkNestingMode
{
	/// <summary>
	/// Reader semantics: every closed stroke fills its own outer minus only the hole
	/// rings that stroke itself carries, in layer order, later strokes painting over
	/// earlier ones. A nested stroke paints its own colour over its parent. Default.
	/// </summary>
	Separate,

	/// <summary>
	/// Even-odd over ALL rings of the layer (every closed outer plus every carried
	/// hole ring): a texel inside an odd number of rings is filled. A nested stroke
	/// therefore punches a hole in its parent, and a third level fills again. Opt in
	/// only — a drawing that never said "hole" should not silently lose its middle.
	/// </summary>
	Hole,
}

/// <summary>Which raster-space boolean to apply between two layer masks.</summary>
public enum LineworkMaskOp { Union, Intersect, Difference, Xor }

/// <summary>Options for <see cref="LineworkRaster"/> — all defaults preserve reader semantics.</summary>
public sealed class LineworkRasterOptions
{
	public LineworkNestingMode Nesting = LineworkNestingMode.Separate;
	public bool FillClosed = true;
	public bool DrawOutlines = true;
	public int SamplesPerSegment = 8;

	/// <summary>
	/// Optional stamp pattern laid over open-stroke and outline dabs: each dab's
	/// weight is multiplied by this alpha sampled across the dab's footprint. This
	/// is BrushAlpha DATA fed into the rasterizer — the brush engine itself is
	/// untouched, and a null stamp means a plain round dab.
	/// </summary>
	public BrushAlpha Stamp;
}

/// <summary>What one raster pass did, in canvas texels. -1 bounds mean nothing was painted.</summary>
public sealed class LineworkRasterResult
{
	public int StrokesDrawn;
	public int FillsDrawn;
	public int Pixels;
	public int MinX = -1, MinY = -1, MaxX = -1, MaxY = -1;
	public RasterMap Map;

	public void Absorb( LineworkRasterResult other )
	{
		StrokesDrawn += other.StrokesDrawn;
		FillsDrawn += other.FillsDrawn;
		Pixels += other.Pixels;
		if ( other.MinX < 0 ) return;
		if ( MinX < 0 ) { MinX = other.MinX; MinY = other.MinY; MaxX = other.MaxX; MaxY = other.MaxY; return; }
		MinX = Math.Min( MinX, other.MinX ); MinY = Math.Min( MinY, other.MinY );
		MaxX = Math.Max( MaxX, other.MaxX ); MaxY = Math.Max( MaxY, other.MaxY );
	}
}

/// <summary>
/// Drawing coordinates to canvas texels. Texel (0,0) is the canvas's top-left texel
/// (row 0 is the top, matching <see cref="PaintCanvas"/>). By default drawing Y runs
/// DOWN like canvas rows; set <see cref="FlipY"/> for a Y-up drawing. The map is a
/// plain affine transform — it never guesses a drawing's units for you, except via
/// <see cref="AutoFitForPoints"/>, which says so in its name.
/// </summary>
public sealed class RasterMap
{
	public float OriginX, OriginY;
	public float Scale = 1f;
	public bool FlipY;

	public Vec2 Map( Vec2 p, int canvasHeight )
	{
		var tx = (p.x - OriginX) * Scale;
		var ty = (p.y - OriginY) * Scale;
		if ( FlipY ) ty = canvasHeight - 1 - ty;
		return new Vec2( tx, ty );
	}

	/// <summary>
	/// A map that fits <paramref name="points"/> inside a canvas of the given size
	/// with <paramref name="margin"/> texels of border, aspect preserved, centred.
	/// Returns null when there are no points. A degenerate (zero-area) bounds falls
	/// back to scale 1 centred on the canvas rather than dividing by zero.
	/// </summary>
	public static RasterMap AutoFitForPoints(
		IReadOnlyList<Vec2> points, int width, int height, float margin = 1f )
	{
		if ( points is null || points.Count == 0 ) return null;
		var minX = float.MaxValue; var minY = float.MaxValue;
		var maxX = float.MinValue; var maxY = float.MinValue;
		foreach ( var p in points )
		{
			if ( p.x < minX ) minX = p.x;
			if ( p.y < minY ) minY = p.y;
			if ( p.x > maxX ) maxX = p.x;
			if ( p.y > maxY ) maxY = p.y;
		}
		var availW = width - 2f * margin;
		var availH = height - 2f * margin;
		if ( availW <= 0f || availH <= 0f ) return new RasterMap { OriginX = minX, OriginY = minY, Scale = 1f };
		var spanX = maxX - minX;
		var spanY = maxY - minY;
		var scale = spanX > 0f && spanY > 0f
			? Math.Min( availW / spanX, availH / spanY )
			: spanX > 0f ? availW / spanX : spanY > 0f ? availH / spanY : 1f;
		// Centre the fitted drawing: origin is the drawing point that lands on the
		// margin, after the leftover space is split equally on both sides.
		var usedW = spanX * scale;
		var usedH = spanY * scale;
		var originX = minX - (margin + (availW - usedW) / 2f) / scale;
		var originY = minY - (margin + (availH - usedH) / 2f) / scale;
		return new RasterMap { OriginX = originX, OriginY = originY, Scale = scale };
	}
}

/// <summary>
/// The second of LineworkReader's stated NOT-dones: layer rasterization into
/// PaintCanvas / BrushAlpha.
///
/// A layer renders in stroke order, later strokes over earlier ones, exactly like
/// paint: closed strokes fill (plus their carried holes) and draw their outline,
/// open strokes draw as pressure-varying lines via <see cref="LineworkStrokes"/>.
/// Nothing here edits the brush engine: <see cref="ToBrushAlpha"/> PRODUCES a
/// BrushAlpha value (float coverage data) the engine can consume, and the optional
/// stamp in <see cref="LineworkRasterOptions"/> only modulates dab weights.
///
/// Colours come from the stroke's own Color string (#RRGGBB or #RGB); an unparsable
/// colour falls back to the reader's default #ecebe6 rather than failing the whole
/// layer — one bad colour string should not eat a drawing.
///
/// VECTOR booleans between strokes are deliberately NOT here: a correct polygon
/// clipper (nested holes, self-touching rings, winding) is a clipping kernel, not
/// a two-file add-on, and it would need a spec decision on nesting first. What fits
/// cleanly, and is implemented, is raster-space booleans: <see cref="LayerMask"/>
/// plus <see cref="CombineMasks"/> (union / intersect / difference / xor over
/// coverage masks), which is what layer compositing and cutouts actually consume.
/// </summary>
public static class LineworkRaster
{
	/// <summary>
	/// Renders one document into a canvas. Layer selection follows
	/// <see cref="LineworkReader.Profiles"/>: filter names a layer by name or id,
	/// empty means every VISIBLE layer. When the document has sym on, each layer is
	/// also rendered mirrored across x = SymX (reader semantics, mirrored copy after
	/// the original). The canvas is never cleared — this composites over it.
	/// </summary>
	public static LineworkRasterResult RenderDocument(
		PaintCanvas canvas, LineworkDocument doc, string layerFilter,
		RasterMap map, LineworkRasterOptions options = null )
	{
		if ( canvas is null ) throw new ArgumentNullException( nameof( canvas ) );
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		if ( map is null ) throw new ArgumentNullException( nameof( map ) );
		options ??= new LineworkRasterOptions();
		var total = new LineworkRasterResult { Map = map };
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
			total.Absorb( RenderLayer( canvas, layer, map, options ) );
			if ( doc.SymOn ) total.Absorb( RenderLayer( canvas, MirrorLayer( layer, doc.SymX ), map, options ) );
		}
		return total;
	}

	/// <summary>Renders one layer into a canvas. No mirroring here — that is document-level.</summary>
	public static LineworkRasterResult RenderLayer(
		PaintCanvas canvas, LineworkLayer layer, RasterMap map, LineworkRasterOptions options = null )
	{
		if ( canvas is null ) throw new ArgumentNullException( nameof( canvas ) );
		if ( layer is null ) throw new ArgumentNullException( nameof( layer ) );
		if ( map is null ) throw new ArgumentNullException( nameof( map ) );
		options ??= new LineworkRasterOptions();
		var result = new LineworkRasterResult { Map = map };
		var plot = PlotForCanvas( canvas, result );

		if ( options.Nesting == LineworkNestingMode.Hole && options.FillClosed )
		{
			// Even-odd over every ring of the layer, painted once in layer order:
			// the first filled stroke's colour wins the shared region, matching
			// "later strokes over earlier" only where regions do not overlap.
			var rings = new List<(List<Vec2> Ring, LineworkStroke Stroke)>();
			foreach ( var s in layer.Strokes )
			{
				if ( !s.Closed ) continue;
				if ( !LineworkReader.CleanRing( s.Points, out var outer ) ) continue;
				rings.Add( (outer, s) );
				foreach ( var h in s.Holes )
					if ( LineworkReader.CleanRing( h, out var hole ) ) rings.Add( (hole, s) );
			}
			if ( rings.Count > 0 )
			{
				var texelRings = new List<(List<Vec2> Ring, LineworkStroke Stroke)>();
				foreach ( var (ring, stroke) in rings )
				{
					var tr = new List<Vec2>( ring.Count );
					foreach ( var p in ring ) tr.Add( map.Map( p, canvas.Height ) );
					texelRings.Add( (tr, stroke) );
				}
				FillEvenOdd( texelRings, canvas, plot, options, result );
			}
		}

		foreach ( var stroke in layer.Strokes )
		{
			var (r, g, b) = ParseColorOrDefault( stroke.Color );
			var opacity = Math.Clamp( stroke.Opacity, 0f, 1f );
			if ( opacity <= 0f ) continue;
			var erase = stroke.Tool is not null
				&& stroke.Tool.Equals( "eraser", StringComparison.OrdinalIgnoreCase );
			if ( stroke.Closed )
			{
				if ( options.Nesting == LineworkNestingMode.Separate && options.FillClosed )
				{
					if ( LineworkReader.CleanRing( stroke.Points, out var outer ) )
					{
						var texelOuter = MapRing( outer, map, canvas.Height );
						var texelHoles = new List<List<Vec2>>();
						foreach ( var h in stroke.Holes )
							if ( LineworkReader.CleanRing( h, out var hole ) )
								texelHoles.Add( MapRing( hole, map, canvas.Height ) );
						FillOuterMinusHoles( texelOuter, texelHoles, canvas, plot, r, g, b, opacity, erase, options, result );
						result.FillsDrawn++;
					}
				}
				else if ( options.Nesting == LineworkNestingMode.Hole )
				{
					// Fill already painted above in the even-odd pass; count it once.
					if ( LineworkReader.CleanRing( stroke.Points, out _ ) ) result.FillsDrawn++;
				}
				if ( options.DrawOutlines )
				{
					var loop = new List<Vec2>( stroke.Points );
					if ( loop.Count > 1 ) loop.Add( loop[0] );
					var pressures = new List<float>();
					for ( var i = 0; i < loop.Count; i++ )
						pressures.Add( i < stroke.Pressures.Count ? Math.Clamp( stroke.Pressures[i], 0f, 1f ) : 1f );
					DrawPolyline( loop, pressures, stroke.Size > 0f ? stroke.Size : 1f,
						map, canvas, plot, r, g, b, opacity, erase, options, result );
				}
				result.StrokesDrawn++;
			}
			else
			{
				var spline = LineworkStrokes.StrokeToSpline( stroke, layer.Name, options.SamplesPerSegment );
				if ( spline is null ) continue;
				DrawPolyline( spline.Points, spline.Pressures, spline.BaseWidth,
					map, canvas, plot, r, g, b, opacity, erase, options, result );
				result.StrokesDrawn++;
			}
		}
		return result;
	}

	/// <summary>
	/// The same layer as a BrushAlpha: coverage 0..1 per texel, row-major, row 0 at
	/// the top, matching <see cref="BrushAlpha.Values"/>. Closed fills contribute 1
	/// (times stroke opacity), open strokes and outlines contribute their dab
	/// falloff; overlapping coverage takes the max, never the sum, so a stamp stays
	/// a stamp instead of blowing out where a line crosses itself.
	/// </summary>
	public static BrushAlpha ToBrushAlpha(
		LineworkLayer layer, int width, int height, RasterMap map, LineworkRasterOptions options = null )
	{
		if ( layer is null ) throw new ArgumentNullException( nameof( layer ) );
		if ( map is null ) throw new ArgumentNullException( nameof( map ) );
		if ( width <= 0 || height <= 0 ) throw new ArgumentException( "brush alpha size must be positive" );
		options ??= new LineworkRasterOptions();
		var values = new float[width * height];
		void Plot( int x, int y, float weight )
		{
			if ( x < 0 || y < 0 || x >= width || y >= height || weight <= 0f ) return;
			var i = y * width + x;
			if ( weight > values[i] ) values[i] = Math.Min( weight, 1f );
		}
		// Fill + stroke walk, mirroring RenderLayer but writing coverage only.
		// (A shared sink would need colour/erase threading; the two walks stay
		// in sync by construction — both iterate strokes in order, both honour
		// Nesting, FillClosed, DrawOutlines, and opacity.)
		var sink = new PlotSink( Plot );
		RenderIntoSink( layer, map, width, height, options, sink, new LineworkRasterResult { Map = map } );
		return new BrushAlpha { Values = values, Width = width, Height = height };
	}

	/// <summary>
	/// Boolean coverage mask for a layer: true where any fill or stroke of the layer
	/// covers the texel (coverage &gt; 0). The raw material for <see cref="CombineMasks"/>.
	/// </summary>
	public static bool[] LayerMask(
		LineworkLayer layer, int width, int height, RasterMap map, LineworkRasterOptions options = null )
	{
		var alpha = ToBrushAlpha( layer, width, height, map, options );
		var mask = new bool[width * height];
		for ( var i = 0; i < mask.Length; i++ ) mask[i] = alpha.Values[i] > 0f;
		return mask;
	}

	/// <summary>
	/// Raster-space boolean between two masks of identical size. Difference is
	/// a-minus-b (order matters); the other three ops are symmetric.
	/// </summary>
	public static bool[] CombineMasks( bool[] a, bool[] b, LineworkMaskOp op )
	{
		if ( a is null ) throw new ArgumentNullException( nameof( a ) );
		if ( b is null ) throw new ArgumentNullException( nameof( b ) );
		if ( a.Length != b.Length ) throw new ArgumentException( "masks must be the same size" );
		var out_ = new bool[a.Length];
		for ( var i = 0; i < out_.Length; i++ )
		{
			out_[i] = op switch
			{
				LineworkMaskOp.Union => a[i] || b[i],
				LineworkMaskOp.Intersect => a[i] && b[i],
				LineworkMaskOp.Difference => a[i] && !b[i],
				LineworkMaskOp.Xor => a[i] ^ b[i],
				_ => throw new ArgumentOutOfRangeException( nameof( op ) ),
			};
		}
		return out_;
	}

	/// <summary>Even-odd point-in-ring test. A point exactly on an edge counts as inside.</summary>
	public static bool PointInRing( Vec2 p, IReadOnlyList<Vec2> ring )
	{
		var inside = false;
		for ( var i = 0; i < ring.Count; i++ )
		{
			var a = ring[i];
			var b = ring[(i + 1) % ring.Count];
			if ( (a.y > p.y) != (b.y > p.y) )
			{
				var xint = a.x + (p.y - a.y) * (b.x - a.x) / (b.y - a.y);
				if ( p.x < xint ) inside = !inside;
			}
		}
		return inside;
	}

	/// <summary>Parses #RRGGBB or #RGB (with or without the #). False on anything else.</summary>
	public static bool TryParseColor( string text, out byte r, out byte g, out byte b )
	{
		r = g = b = 0;
		if ( string.IsNullOrWhiteSpace( text ) ) return false;
		var s = text.Trim();
		if ( s.StartsWith( "#" ) ) s = s[1..];
		bool Hex( string part, out byte v )
		{
			v = 0;
			if ( part.Length != 2 ) return false;
			foreach ( var c in part )
				if ( !Uri.IsHexDigit( c ) ) return false;
			v = Convert.ToByte( part, 16 );
			return true;
		}
		if ( s.Length == 6 )
			return Hex( s[..2], out r ) && Hex( s[2..4], out g ) && Hex( s[4..], out b );
		if ( s.Length == 3 )
		{
			var doubled = new string( new[] { s[0], s[0], s[1], s[1], s[2], s[2] } );
			return Hex( doubled[..2], out r ) && Hex( doubled[2..4], out g ) && Hex( doubled[4..], out b );
		}
		return false;
	}

	// ---- internals ----

	sealed class PlotSink
	{
		public readonly Action<int, int, float, byte, byte, byte, bool> Plot;
		public PlotSink( Action<int, int, float> coverage )
		{
			Plot = ( x, y, w, cr, cg, cb, er ) => coverage( x, y, w );
		}
		public PlotSink( Action<int, int, float, byte, byte, byte, bool> plot ) => Plot = plot;
	}

	static Action<int, int, float, byte, byte, byte, bool> PlotForCanvas( PaintCanvas canvas, LineworkRasterResult result )
	{
		return ( x, y, weight, r, g, b, erase ) =>
		{
			if ( x < 0 || y < 0 || x >= canvas.Width || y >= canvas.Height || weight <= 0f ) return;
			if ( erase )
			{
				canvas.Erase( x, y, weight );
				// A texel erased to fully transparent must read as cleared, not as
				// a red texel with zero alpha: Erase is destination-out on straight
				// alpha, so it leaves RGB untouched, and a read-back counting the
				// colour channel would still see the "erased" colour. Zero the
				// colour once nothing is left to show it; a partly erased texel
				// keeps its colour, as straight alpha says it should.
				var i = (y * canvas.Width + x) * 4;
				if ( canvas.Rgba[i + 3] == 0 )
				{
					canvas.Rgba[i] = 0;
					canvas.Rgba[i + 1] = 0;
					canvas.Rgba[i + 2] = 0;
				}
			}
			else canvas.Blend( x, y, r, g, b, weight );
			result.Pixels++;
			if ( result.MinX < 0 ) { result.MinX = result.MaxX = x; result.MinY = result.MaxY = y; }
			else
			{
				if ( x < result.MinX ) result.MinX = x;
				if ( x > result.MaxX ) result.MaxX = x;
				if ( y < result.MinY ) result.MinY = y;
				if ( y > result.MaxY ) result.MaxY = y;
			}
		};
	}

	public static (byte R, byte G, byte B) ParseColorOrDefault( string color )
	{
		if ( TryParseColor( color, out var r, out var g, out var b ) ) return (r, g, b);
		return (0xEC, 0xEB, 0xE6); // reader default #ecebe6
	}

	static List<Vec2> MapRing( List<Vec2> ring, RasterMap map, int canvasHeight )
	{
		var out_ = new List<Vec2>( ring.Count );
		foreach ( var p in ring ) out_.Add( map.Map( p, canvasHeight ) );
		return out_;
	}

	static LineworkLayer MirrorLayer( LineworkLayer layer, float symX )
	{
		List<Vec2> Flip( List<Vec2> pts )
		{
			var m = new List<Vec2>( pts.Count );
			foreach ( var p in pts ) m.Add( new Vec2( 2f * symX - p.x, p.y ) );
			return m;
		}
		var copy = new LineworkLayer { Id = layer.Id, Name = layer.Name, Visible = layer.Visible, Ref = layer.Ref };
		foreach ( var s in layer.Strokes )
		{
			var c = new LineworkStroke
			{
				Id = s.Id + "#mirror", Tool = s.Tool, Color = s.Color, Size = s.Size,
				Opacity = s.Opacity, Closed = s.Closed, Points = Flip( s.Points ),
				Pressures = new List<float>( s.Pressures ),
			};
			foreach ( var h in s.Holes ) c.Holes.Add( Flip( h ) );
			copy.Strokes.Add( c );
		}
		return copy;
	}

	static void FillEvenOdd(
		List<(List<Vec2> Ring, LineworkStroke Stroke)> texelRings,
		PaintCanvas canvas,
		Action<int, int, float, byte, byte, byte, bool> plot,
		LineworkRasterOptions options, LineworkRasterResult result )
	{
		// Bounding box over all rings, clamped to the canvas.
		var minX = int.MaxValue; var minY = int.MaxValue;
		var maxX = int.MinValue; var maxY = int.MinValue;
		foreach ( var (ring, _) in texelRings )
			foreach ( var p in ring )
			{
				var x = (int)MathF.Floor( p.x ); var y = (int)MathF.Floor( p.y );
				if ( x < minX ) minX = x; if ( y < minY ) minY = y;
				if ( x > maxX ) maxX = x; if ( y > maxY ) maxY = y;
			}
		minX = Math.Max( minX, 0 ); minY = Math.Max( minY, 0 );
		maxX = Math.Min( maxX, canvas.Width - 1 ); maxY = Math.Min( maxY, canvas.Height - 1 );
		for ( var y = minY; y <= maxY; y++ )
		{
			for ( var x = minX; x <= maxX; x++ )
			{
				var centre = new Vec2( x + 0.5f, y + 0.5f );
				var count = 0;
				LineworkStroke first = null;
				foreach ( var (ring, stroke) in texelRings )
				{
					if ( PointInRing( centre, ring ) ) { count++; first ??= stroke; }
				}
				if ( count % 2 == 1 && first is not null )
				{
					var (r, g, b) = ParseColorOrDefault( first.Color );
					var erase = first.Tool is not null
						&& first.Tool.Equals( "eraser", StringComparison.OrdinalIgnoreCase );
					plot( x, y, Math.Clamp( first.Opacity, 0f, 1f ), r, g, b, erase );
				}
			}
		}
	}

	static void FillOuterMinusHoles(
		List<Vec2> outer, List<List<Vec2>> holes, PaintCanvas canvas,
		Action<int, int, float, byte, byte, byte, bool> plot,
		byte r, byte g, byte b, float opacity, bool erase,
		LineworkRasterOptions options, LineworkRasterResult result )
	{
		var minX = int.MaxValue; var minY = int.MaxValue;
		var maxX = int.MinValue; var maxY = int.MinValue;
		foreach ( var p in outer )
		{
			var x = (int)MathF.Floor( p.x ); var y = (int)MathF.Floor( p.y );
			if ( x < minX ) minX = x; if ( y < minY ) minY = y;
			if ( x > maxX ) maxX = x; if ( y > maxY ) maxY = y;
		}
		minX = Math.Max( minX, 0 ); minY = Math.Max( minY, 0 );
		maxX = Math.Min( maxX, canvas.Width - 1 ); maxY = Math.Min( maxY, canvas.Height - 1 );
		for ( var y = minY; y <= maxY; y++ )
		{
			for ( var x = minX; x <= maxX; x++ )
			{
				var centre = new Vec2( x + 0.5f, y + 0.5f );
				if ( !PointInRing( centre, outer ) ) continue;
				var inHole = false;
				foreach ( var hole in holes )
					if ( PointInRing( centre, hole ) ) { inHole = true; break; }
				if ( inHole ) continue;
				plot( x, y, opacity, r, g, b, erase );
			}
		}
	}

	static void DrawPolyline(
		List<Vec2> drawingPoints, List<float> pressures, float baseWidth,
		RasterMap map, PaintCanvas canvas,
		Action<int, int, float, byte, byte, byte, bool> plot,
		byte r, byte g, byte b, float opacity, bool erase,
		LineworkRasterOptions options, LineworkRasterResult result )
	{
		if ( drawingPoints.Count == 0 ) return;
		var texelPoints = new List<Vec2>( drawingPoints.Count );
		foreach ( var p in drawingPoints ) texelPoints.Add( map.Map( p, canvas.Height ) );
		// A polyline in TEXEL space: pressures already run parallel to the points.
		// (Splines arrive pre-sampled; closed outlines arrive as raw ring points
		// with per-point pressures, which is the same shape.)
		if ( texelPoints.Count == 1 )
		{
			Stamp( texelPoints[0], pressures.Count > 0 ? pressures[0] : 1f, baseWidth,
				map, canvas, plot, r, g, b, opacity, erase, options );
			return;
		}
		for ( var s = 0; s < texelPoints.Count - 1; s++ )
		{
			var a = texelPoints[s];
			var pa = s < pressures.Count ? pressures[s] : 1f;
			var bb = texelPoints[s + 1];
			var pb = s + 1 < pressures.Count ? pressures[s + 1] : 1f;
			var len = (bb - a).Length;
			var steps = Math.Max( 1, (int)Math.Ceiling( len / 0.5f ) );
			for ( var i = 0; i <= steps; i++ )
			{
				var t = i / (float)steps;
				Stamp( a + (bb - a) * t, pa + (pb - pa) * t, baseWidth,
					map, canvas, plot, r, g, b, opacity, erase, options );
			}
		}
	}

	static void Stamp(
		Vec2 centre, float pressure, float baseWidth,
		RasterMap map, PaintCanvas canvas,
		Action<int, int, float, byte, byte, byte, bool> plot,
		byte r, byte g, byte b, float opacity, bool erase,
		LineworkRasterOptions options )
	{
		var radius = baseWidth * Math.Clamp( pressure, 0f, 1f ) * map.Scale / 2f;
		if ( radius <= 0f ) return;
		var cx = (int)MathF.Floor( centre.x );
		var cy = (int)MathF.Floor( centre.y );
		var rad = (int)Math.Ceiling( radius );
		for ( var y = cy - rad; y <= cy + rad; y++ )
		{
			for ( var x = cx - rad; x <= cx + rad; x++ )
			{
				var dx = (x + 0.5f) - centre.x;
				var dy = (y + 0.5f) - centre.y;
				var d = MathF.Sqrt( dx * dx + dy * dy );
				if ( d >= radius && radius > 0.5f ) continue;
				// Hairlines (radius <= 0.5) still paint their centre texel at full
				// weight; wider dabs fall off quadratically to a soft edge.
				var falloff = radius <= 0.5f ? 1f : Math.Max( 0f, 1f - (d / radius) * (d / radius) );
				var weight = falloff * opacity;
				if ( options.Stamp is not null )
				{
					var u = radius > 0f ? dx / (2f * radius) + 0.5f : 0.5f;
					var v = radius > 0f ? dy / (2f * radius) + 0.5f : 0.5f;
					weight *= options.Stamp.Sample( u, v );
				}
				if ( weight > 0f ) plot( x, y, weight, r, g, b, erase );
			}
		}
	}

	// Coverage-sink walk for ToBrushAlpha: same stroke order, same options, but
	// every plot writes max-coverage into the sink instead of blending colour.
	// Kept as a second walk (rather than threading colour through the canvas walk)
	// because the canvas walk's plot carries colour/erase, and a stamp must never
	// be able to change a layer's colours by accident.
	static void RenderIntoSink(
		LineworkLayer layer, RasterMap map, int width, int height,
		LineworkRasterOptions options, PlotSink sink, LineworkRasterResult result )
	{
		void Plot( int x, int y, float weight, byte cr, byte cg, byte cb, bool er )
		{
			if ( x < 0 || y < 0 || x >= width || y >= height || weight <= 0f ) return;
			sink.Plot( x, y, weight, 0, 0, 0, false );
		}
		// Fill pass mirrors RenderLayer: Hole mode paints even-odd once, Separate
		// paints each stroke's outer minus its own holes.
		if ( options.Nesting == LineworkNestingMode.Hole && options.FillClosed )
		{
			var rings = new List<(List<Vec2> Ring, LineworkStroke Stroke)>();
			foreach ( var s in layer.Strokes )
			{
				if ( !s.Closed ) continue;
				if ( !LineworkReader.CleanRing( s.Points, out var outer ) ) continue;
				rings.Add( (outer, s) );
				foreach ( var h in s.Holes )
					if ( LineworkReader.CleanRing( h, out var hole ) ) rings.Add( (hole, s) );
			}
			if ( rings.Count > 0 )
			{
				var texelRings = new List<(List<Vec2> Ring, LineworkStroke Stroke)>();
				foreach ( var (ring, stroke) in rings )
				{
					var tr = new List<Vec2>( ring.Count );
					foreach ( var p in ring ) tr.Add( map.Map( p, height ) );
					texelRings.Add( (tr, stroke) );
				}
				// Even-odd fill into the sink (canvas-sized bounds via a shim):
				var shimCanvas = new PaintCanvas( width, height );
				FillEvenOdd( texelRings, shimCanvas, Plot, options, result );
			}
		}
		foreach ( var stroke in layer.Strokes )
		{
			var opacity = Math.Clamp( stroke.Opacity, 0f, 1f );
			if ( opacity <= 0f ) continue;
			if ( stroke.Closed )
			{
				if ( options.Nesting == LineworkNestingMode.Separate && options.FillClosed )
				{
					if ( LineworkReader.CleanRing( stroke.Points, out var outer ) )
					{
						var texelOuter = MapRing( outer, map, height );
						var texelHoles = new List<List<Vec2>>();
						foreach ( var h in stroke.Holes )
							if ( LineworkReader.CleanRing( h, out var hole ) )
								texelHoles.Add( MapRing( hole, map, height ) );
						var shimCanvas = new PaintCanvas( width, height );
						FillOuterMinusHoles( texelOuter, texelHoles, shimCanvas, Plot, 0, 0, 0, opacity, false, options, result );
					}
				}
				if ( options.DrawOutlines )
				{
					var loop = new List<Vec2>( stroke.Points );
					if ( loop.Count > 1 ) loop.Add( loop[0] );
					var pressures = new List<float>();
					for ( var i = 0; i < loop.Count; i++ )
						pressures.Add( i < stroke.Pressures.Count ? Math.Clamp( stroke.Pressures[i], 0f, 1f ) : 1f );
					var shimCanvas = new PaintCanvas( width, height );
					DrawPolyline( loop, pressures, stroke.Size > 0f ? stroke.Size : 1f,
						map, shimCanvas, Plot, 0, 0, 0, opacity, false, options, result );
				}
			}
			else
			{
				var spline = LineworkStrokes.StrokeToSpline( stroke, layer.Name, options.SamplesPerSegment );
				if ( spline is null ) continue;
				var shimCanvas = new PaintCanvas( width, height );
				DrawPolyline( spline.Points, spline.Pressures, spline.BaseWidth,
					map, shimCanvas, Plot, 0, 0, 0, opacity, false, options, result );
			}
		}
	}
}
