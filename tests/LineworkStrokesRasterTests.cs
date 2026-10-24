using System;
using System.Collections.Generic;
using Effigy;

internal static class LineworkStrokesRasterTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "linework2d", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, float tol, string message )
		{
			if ( MathF.Abs( actual - expected ) > tol )
				throw new Exception( $"{message}: expected {expected}, got {actual}" );
		}
		LineworkStroke Stroke( string id, bool closed, float size, string color,
			(float, float)[] pts, float[] pressures = null, string tool = "pen", float opacity = 1f )
		{
			var s = new LineworkStroke
			{
				Id = id, Closed = closed, Size = size, Color = color, Tool = tool, Opacity = opacity,
			};
			foreach ( var (x, y) in pts ) s.Points.Add( new Vec2( x, y ) );
			if ( pressures is not null ) s.Pressures.AddRange( pressures );
			return s;
		}
		LineworkLayer Layer( string name, bool visible, params LineworkStroke[] strokes )
		{
			var l = new LineworkLayer { Name = name, Id = name, Visible = visible };
			l.Strokes.AddRange( strokes );
			return l;
		}
		RasterMap Identity() => new() { OriginX = 0f, OriginY = 0f, Scale = 1f };
		byte AlphaAt( PaintCanvas canvas, int x, int y ) => canvas.Rgba[(y * canvas.Width + x) * 4 + 3];
		byte ChanAt( PaintCanvas canvas, int x, int y, int chan ) => canvas.Rgba[(y * canvas.Width + x) * 4 + chan];

		// ---- splines ----
		Check( "open line keeps endpoints and sample count", () =>
		{
			var s = Stroke( "a", false, 2f, "#ff0000", new[] { (0f, 0f), (10f, 0f) } );
			var spline = LineworkStrokes.StrokeToSpline( s, "L", 4 );
			Require( spline is not null );
			Require( spline.Points.Count == 5, $"expected 5 points, got {spline.Points.Count}" );
			Near( spline.Points[0].x, 0f, 1e-5f, "first point x" );
			Near( spline.Points[^1].x, 10f, 1e-5f, "last point x" );
		} );

		Check( "two-point pressure interpolates linearly", () =>
		{
			var s = Stroke( "a", false, 2f, "#ff0000", new[] { (0f, 0f), (10f, 0f) }, new[] { 0f, 1f } );
			var spline = LineworkStrokes.StrokeToSpline( s, "L", 4 );
			Near( spline.Pressures[2], 0.5f, 1e-5f, "mid pressure" );
		} );

		Check( "catmull-rom passes through controls and bulges", () =>
		{
			var s = Stroke( "a", false, 2f, "#ff0000", new[] { (0f, 0f), (5f, 10f), (10f, 0f) } );
			var spline = LineworkStrokes.StrokeToSpline( s, "L", 8 );
			Near( spline.Points[8].x, 5f, 1e-4f, "middle control x" );
			Near( spline.Points[8].y, 10f, 1e-4f, "middle control y" );
			Require( spline.Points[4].y > 2f, $"first segment should bulge upward, got y={spline.Points[4].y}" );
		} );

		Check( "closed and empty strokes produce no spline, single point is a dot", () =>
		{
			Require( LineworkStrokes.StrokeToSpline( Stroke( "c", true, 2f, "#ff0000", new[] { (0f, 0f), (1f, 0f) } ) ) is null );
			Require( LineworkStrokes.StrokeToSpline( Stroke( "e", false, 2f, "#ff0000", Array.Empty<(float, float)>() ) ) is null );
			var dot = LineworkStrokes.StrokeToSpline( Stroke( "d", false, 3f, "#ff0000", new[] { (2f, 3f) }, new[] { 0.5f } ) );
			Require( dot is not null && dot.Points.Count == 1 );
			Near( dot.WidthAt( 0 ), 1.5f, 1e-5f, "dot width is size times pressure" );
		} );

		Check( "duplicate points and missing pressures are harmless", () =>
		{
			var s = Stroke( "a", false, 2f, "#ff0000", new[] { (0f, 0f), (0f, 0f), (4f, 0f) } );
			var spline = LineworkStrokes.StrokeToSpline( s, "L", 4 );
			Require( spline is not null );
			foreach ( var p in spline.Points ) Require( float.IsFinite( p.x ) && float.IsFinite( p.y ), "non-finite point" );
			foreach ( var p in spline.Pressures ) Near( p, 1f, 1e-5f, "missing pressure pads to 1" );
		} );

		Check( "pressures clamp to 0..1", () =>
		{
			var s = Stroke( "a", false, 2f, "#ff0000", new[] { (0f, 0f), (4f, 0f) }, new[] { -2f, 5f } );
			var spline = LineworkStrokes.StrokeToSpline( s, "L", 2 );
			Near( spline.Pressures[0], 0f, 1e-5f, "low clamp" );
			Near( spline.Pressures[^1], 1f, 1e-5f, "high clamp" );
		} );

		Check( "sym mirrors splines with mirror flag and id", () =>
		{
			var doc = new LineworkDocument { SymOn = true, SymX = 10f };
			doc.Layers.Add( Layer( "L", true, Stroke( "a", false, 2f, "#ff0000", new[] { (0f, 0f), (4f, 0f) } ) ) );
			var splines = LineworkStrokes.ToPressureSplines( doc );
			Require( splines.Count == 2, $"expected 2 splines, got {splines.Count}" );
			Require( !splines[0].Mirrored && splines[1].Mirrored );
			Require( splines[1].StrokeId == "a#mirror" );
			Near( splines[1].Points[0].x, 20f, 1e-5f, "mirrored x" );
			Near( splines[1].Points[^1].x, 16f, 1e-5f, "mirrored end x" );
		} );

		Check( "layer filter and hidden layers follow reader semantics", () =>
		{
			var doc = new LineworkDocument();
			doc.Layers.Add( Layer( "A", true, Stroke( "a", false, 2f, "#ff0000", new[] { (0f, 0f), (1f, 0f) } ) ) );
			doc.Layers.Add( Layer( "B", false, Stroke( "b", false, 2f, "#ff0000", new[] { (0f, 0f), (1f, 0f) } ) ) );
			Require( LineworkStrokes.ToPressureSplines( doc ).Count == 1 );
			var filtered = LineworkStrokes.ToPressureSplines( doc, "b" );
			Require( filtered.Count == 1 && filtered[0].StrokeId == "b" );
		} );

		// ---- raster: fills and nesting ----
		Check( "closed square fills centre, not outside", () =>
		{
			var canvas = new PaintCanvas( 12, 12 );
			var layer = Layer( "L", true, Stroke( "sq", true, 0f, "#FF0000",
				new[] { (2f, 2f), (8f, 2f), (8f, 8f), (2f, 8f) } ) );
			var result = LineworkRaster.RenderLayer( canvas, layer, Identity() );
			Require( result.FillsDrawn == 1 );
			Require( AlphaAt( canvas, 5, 5 ) == 255, $"centre alpha {AlphaAt( canvas, 5, 5 )}" );
			Require( ChanAt( canvas, 5, 5, 0 ) == 255, "centre is red" );
			Require( AlphaAt( canvas, 0, 0 ) == 0, "outside unpainted" );
			Require( result.MinX >= 0 && result.MaxX < 12, "dirty bounds inside canvas" );
		} );

		Check( "carried hole stays unpainted", () =>
		{
			var canvas = new PaintCanvas( 12, 12 );
			var s = Stroke( "sq", true, 0f, "#FF0000",
				new[] { (1f, 1f), (10f, 1f), (10f, 10f), (1f, 10f) } );
			s.Holes.Add( new List<Vec2> { new( 4f, 4f ), new( 6f, 4f ), new( 6f, 6f ), new( 4f, 6f ) } );
			LineworkRaster.RenderLayer( canvas, Layer( "L", true, s ), Identity() );
			Require( AlphaAt( canvas, 5, 5 ) == 0, "hole centre unpainted" );
			Require( AlphaAt( canvas, 2, 2 ) == 255, "outer area painted" );
		} );

		Check( "nested stroke paints over parent in Separate mode", () =>
		{
			var canvas = new PaintCanvas( 12, 12 );
			var outer = Stroke( "o", true, 0f, "#FF0000",
				new[] { (1f, 1f), (10f, 1f), (10f, 10f), (1f, 10f) } );
			var inner = Stroke( "i", true, 0f, "#0000FF",
				new[] { (4f, 4f), (6f, 4f), (6f, 6f), (4f, 6f) } );
			LineworkRaster.RenderLayer( canvas, Layer( "L", true, outer, inner ), Identity() );
			Require( ChanAt( canvas, 5, 5, 2 ) == 255, "inner colour wins in Separate mode" );
			Require( ChanAt( canvas, 5, 5, 0 ) == 0, "outer red replaced by inner blue" );
		} );

		Check( "nested stroke punches a hole in Hole mode", () =>
		{
			var canvas = new PaintCanvas( 12, 12 );
			var outer = Stroke( "o", true, 0f, "#FF0000",
				new[] { (1f, 1f), (10f, 1f), (10f, 10f), (1f, 10f) } );
			var inner = Stroke( "i", true, 0f, "#0000FF",
				new[] { (4f, 4f), (6f, 4f), (6f, 6f), (4f, 6f) } );
			var opts = new LineworkRasterOptions { Nesting = LineworkNestingMode.Hole, DrawOutlines = false };
			LineworkRaster.RenderLayer( canvas, Layer( "L", true, outer, inner ), Identity(), opts );
			Require( AlphaAt( canvas, 5, 5 ) == 0, "nested area unpainted in Hole mode" );
			Require( AlphaAt( canvas, 2, 2 ) == 255, "outer area still painted" );
		} );

		// ---- raster: strokes, colour, eraser, stamp ----
		Check( "open stroke paints its line, not the rest of the canvas", () =>
		{
			var canvas = new PaintCanvas( 12, 12 );
			var layer = Layer( "L", true, Stroke( "ln", false, 2f, "#FF0000",
				new[] { (1f, 5f), (10f, 5f) } ) );
			var result = LineworkRaster.RenderLayer( canvas, layer, Identity() );
			Require( result.StrokesDrawn == 1 );
			Require( AlphaAt( canvas, 5, 5 ) > 0, "line texel painted" );
			Require( AlphaAt( canvas, 5, 0 ) == 0, "off-line texel unpainted" );
		} );

		Check( "low pressure draws a hairline, full pressure draws wide", () =>
		{
			int PaintedRows( float pressure )
			{
				var canvas = new PaintCanvas( 12, 12 );
				var layer = Layer( "L", true, Stroke( "ln", false, 4f, "#FF0000",
					new[] { (1f, 5f), (10f, 5f) }, new[] { pressure, pressure } ) );
				LineworkRaster.RenderLayer( canvas, layer, Identity() );
				var rows = 0;
				for ( var y = 0; y < 12; y++ ) if ( AlphaAt( canvas, 5, y ) > 0 ) rows++;
				return rows;
			}
			Require( PaintedRows( 0.25f ) == 1, "hairline is one row" );
			Require( PaintedRows( 1f ) == 4, $"full pressure should paint 4 rows, got {PaintedRows( 1f )}" );
		} );

		Check( "opacity halves the painted alpha", () =>
		{
			var canvas = new PaintCanvas( 12, 12 );
			var layer = Layer( "L", true, Stroke( "ln", false, 2f, "#FF0000",
				new[] { (1f, 5f), (10f, 5f) }, null, "pen", 0.5f ) );
			LineworkRaster.RenderLayer( canvas, layer, Identity() );
			var a = AlphaAt( canvas, 5, 5 );
			Require( a >= 80 && a <= 170, $"half-opacity alpha should be near 127, got {a}" );
		} );

		Check( "eraser tool removes paint it crosses", () =>
		{
			var canvas = new PaintCanvas( 12, 12 );
			var fill = Stroke( "sq", true, 0f, "#FF0000",
				new[] { (1f, 1f), (10f, 1f), (10f, 10f), (1f, 10f) } );
			var erase = Stroke( "e", false, 4f, "#000000",
				new[] { (1f, 5f), (10f, 5f) }, null, "eraser" );
			LineworkRaster.RenderLayer( canvas, Layer( "L", true, fill, erase ), Identity() );
			Require( AlphaAt( canvas, 5, 5 ) < 64, $"erased centre alpha {AlphaAt( canvas, 5, 5 )}" );
			Require( AlphaAt( canvas, 2, 2 ) == 255, "off-line paint untouched" );
		} );

		Check( "colour parsing: short hex, long hex, invalid falls back", () =>
		{
			Require( LineworkRaster.TryParseColor( "#f00", out var r1, out var g1, out var b1 ) && r1 == 255 && g1 == 0 && b1 == 0 );
			Require( LineworkRaster.TryParseColor( "#FF8000", out var r2, out var g2, out var b2 ) && r2 == 255 && g2 == 128 && b2 == 0 );
			Require( !LineworkRaster.TryParseColor( "zzz", out _, out _, out _ ) );
			var canvas = new PaintCanvas( 12, 12 );
			var layer = Layer( "L", true, Stroke( "sq", true, 0f, "not-a-colour",
				new[] { (2f, 2f), (8f, 2f), (8f, 8f), (2f, 8f) } ) );
			LineworkRaster.RenderLayer( canvas, layer, Identity(),
				new LineworkRasterOptions { DrawOutlines = false } );
			Require( ChanAt( canvas, 5, 5, 0 ) == 0xEC && ChanAt( canvas, 5, 5, 1 ) == 0xEB,
				"invalid colour falls back to #ecebe6" );
		} );

		Check( "zero stamp paints nothing, no stamp paints normally", () =>
		{
			var layer = Layer( "L", true, Stroke( "ln", false, 2f, "#FF0000",
				new[] { (1f, 5f), (10f, 5f) } ) );
			var zeroStamp = new BrushAlpha { Values = new float[4], Width = 2, Height = 2 };
			var canvas = new PaintCanvas( 12, 12 );
			var result = LineworkRaster.RenderLayer( canvas, layer, Identity(),
				new LineworkRasterOptions { Stamp = zeroStamp } );
			Require( result.Pixels == 0, "zero stamp paints zero pixels" );
			var canvas2 = new PaintCanvas( 12, 12 );
			LineworkRaster.RenderLayer( canvas2, layer, Identity() );
			Require( AlphaAt( canvas2, 5, 5 ) > 0, "no stamp paints" );
		} );

		// ---- brush alpha + masks ----
		Check( "ToBrushAlpha matches fill geometry and range", () =>
		{
			var layer = Layer( "L", true, Stroke( "sq", true, 0f, "#FF0000",
				new[] { (2f, 2f), (8f, 2f), (8f, 8f), (2f, 8f) } ) );
			var alpha = LineworkRaster.ToBrushAlpha( layer, 12, 12, Identity(),
				new LineworkRasterOptions { DrawOutlines = false } );
			Require( alpha.Width == 12 && alpha.Height == 12 );
			Near( alpha.Values[5 * 12 + 5], 1f, 1e-5f, "fill centre coverage 1" );
			Near( alpha.Values[0], 0f, 1e-5f, "outside coverage 0" );
			foreach ( var v in alpha.Values ) Require( v >= 0f && v <= 1f, "coverage out of range" );
		} );

		Check( "mask booleans give exact union, intersect, difference, xor counts", () =>
		{
			var opts = new LineworkRasterOptions { DrawOutlines = false };
			var aLayer = Layer( "A", true, Stroke( "a", true, 0f, "#FF0000",
				new[] { (1f, 1f), (6f, 1f), (6f, 6f), (1f, 6f) } ) );
			var bLayer = Layer( "B", true, Stroke( "b", true, 0f, "#FF0000",
				new[] { (4f, 4f), (9f, 4f), (9f, 9f), (4f, 9f) } ) );
			var a = LineworkRaster.LayerMask( aLayer, 12, 12, Identity(), opts );
			var b = LineworkRaster.LayerMask( bLayer, 12, 12, Identity(), opts );
			int Count( bool[] m ) { var n = 0; foreach ( var v in m ) if ( v ) n++; return n; }
			Require( Count( a ) == 25 && Count( b ) == 25, $"each square is 25 texels, got {Count( a )}/{Count( b )}" );
			Require( Count( LineworkRaster.CombineMasks( a, b, LineworkMaskOp.Intersect ) ) == 4 );
			Require( Count( LineworkRaster.CombineMasks( a, b, LineworkMaskOp.Union ) ) == 46 );
			Require( Count( LineworkRaster.CombineMasks( a, b, LineworkMaskOp.Difference ) ) == 21 );
			Require( Count( LineworkRaster.CombineMasks( a, b, LineworkMaskOp.Xor ) ) == 42 );
		} );

		Check( "point-in-ring: inside true, outside false", () =>
		{
			var ring = new List<Vec2> { new( 0f, 0f ), new( 10f, 0f ), new( 10f, 10f ), new( 0f, 10f ) };
			Require( LineworkRaster.PointInRing( new Vec2( 5f, 5f ), ring ) );
			Require( !LineworkRaster.PointInRing( new Vec2( 20f, 5f ), ring ) );
		} );

		// ---- maps and documents ----
		Check( "AutoFit fits bounds inside the canvas, aspect preserved", () =>
		{
			var pts = new List<Vec2> { new( 0f, 0f ), new( 100f, 50f ) };
			var map = RasterMap.AutoFitForPoints( pts, 20, 10, 1f );
			Require( map is not null );
			Near( map.Scale, 0.16f, 1e-5f, "scale is min(18/100, 8/50)" );
			var lo = map.Map( pts[0], 10 );
			var hi = map.Map( pts[1], 10 );
			Require( lo.x >= 0f && lo.x < 20f && lo.y >= 0f && lo.y < 10f, "low corner inside" );
			Require( hi.x >= 0f && hi.x < 20f && hi.y >= 0f && hi.y < 10f, "high corner inside" );
			Require( RasterMap.AutoFitForPoints( new List<Vec2>(), 20, 10 ) is null );
		} );

		Check( "RenderDocument mirrors visible layers and skips hidden ones", () =>
		{
			var doc = new LineworkDocument { SymOn = true, SymX = 10f };
			doc.Layers.Add( Layer( "V", true, Stroke( "sq", true, 0f, "#FF0000",
				new[] { (2f, 2f), (4f, 2f), (4f, 4f), (2f, 4f) } ) ) );
			doc.Layers.Add( Layer( "H", false, Stroke( "sq", true, 0f, "#FF0000",
				new[] { (8f, 8f), (10f, 8f), (10f, 10f), (8f, 10f) } ) ) );
			var canvas = new PaintCanvas( 24, 24 );
			LineworkRaster.RenderDocument( canvas, doc, null, Identity(),
				new LineworkRasterOptions { DrawOutlines = false } );
			Require( AlphaAt( canvas, 3, 3 ) > 0, "original painted" );
			Require( AlphaAt( canvas, 17, 3 ) > 0, "mirrored copy painted" );
			Require( AlphaAt( canvas, 9, 9 ) == 0, "hidden layer skipped" );
		} );

		Check( "null canvas, layer, and map throw", () =>
		{
			var layer = Layer( "L", true );
			var canvas = new PaintCanvas( 4, 4 );
			var map = Identity();
			void Throws<T>( Action action ) where T : Exception
			{
				try { action(); }
				catch ( T ) { return; }
				throw new Exception( $"expected {typeof( T ).Name}" );
			}
			Throws<ArgumentNullException>( () => LineworkRaster.RenderLayer( null, layer, map ) );
			Throws<ArgumentNullException>( () => LineworkRaster.RenderLayer( canvas, null, map ) );
			Throws<ArgumentNullException>( () => LineworkRaster.RenderLayer( canvas, layer, null ) );
			Throws<ArgumentNullException>( () => LineworkStrokes.ToPressureSplines( null ) );
		} );
	}
}
