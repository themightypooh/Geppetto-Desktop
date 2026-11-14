using System;
using System.Collections.Generic;
using Effigy;

internal static class LineworkMagicWandTests
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
		void Throws( Action action )
		{
			try { action(); }
			catch ( Exception ) { return; }
			throw new Exception( "expected an error, got silent success" );
		}
		LineworkStroke Stroke( string id, bool closed, float size, string color,
			(float, float)[] pts, string tool = "pen", float opacity = 1f )
		{
			var s = new LineworkStroke
			{
				Id = id, Closed = closed, Size = size, Color = color, Tool = tool, Opacity = opacity,
			};
			foreach ( var (x, y) in pts ) s.Points.Add( new Vec2( x, y ) );
			return s;
		}
		LineworkLayer Layer( string name, bool visible, params LineworkStroke[] strokes )
		{
			var l = new LineworkLayer { Name = name, Id = name, Visible = visible };
			l.Strokes.AddRange( strokes );
			return l;
		}
		RasterMap Identity() => new() { OriginX = 0f, OriginY = 0f, Scale = 1f };
		LineworkRasterOptions NoOutlines() => new() { DrawOutlines = false };
		byte AlphaAt( PaintCanvas canvas, int x, int y ) => canvas.Rgba[(y * canvas.Width + x) * 4 + 3];

		Check( "wand selects exactly the red region, not the blue region", () =>
		{
			var canvas = new PaintCanvas( 12, 12 );
			var layer = Layer( "L", true,
				Stroke( "red", true, 0f, "#FF0000", new[] { (1f, 1f), (6f, 1f), (6f, 6f), (1f, 6f) } ),
				Stroke( "blue", true, 0f, "#0000FF", new[] { (7f, 1f), (11f, 1f), (11f, 5f), (7f, 5f) } ) );
			LineworkRaster.RenderLayer( canvas, layer, Identity(), NoOutlines() );
			var sel = LineworkMagicWand.Select( canvas, 3, 3, 0f );
			Require( sel.Count == 25, $"red square is 25 texels, got {sel.Count}" );
			Require( sel.Contains( 3, 3 ) && sel.Contains( 5, 5 ), "seed region selected" );
			Require( !sel.Contains( 8, 2 ), "blue region not selected" );
			Require( sel.MinX == 1 && sel.MinY == 1 && sel.MaxX == 5 && sel.MaxY == 5,
				$"bounds should be 1..5, got {sel.MinX},{sel.MinY}..{sel.MaxX},{sel.MaxY}" );
		} );

		Check( "global wand selects both matching regions, contiguous selects one", () =>
		{
			var canvas = new PaintCanvas( 9, 9 );
			void Red( int x, int y ) => canvas.Blend( x, y, 255, 0, 0, 1f );
			Red( 1, 1 ); Red( 2, 1 ); Red( 1, 2 ); Red( 2, 2 );
			Red( 6, 1 ); Red( 7, 1 ); Red( 6, 2 ); Red( 7, 2 );
			for ( var y = 0; y < 9; y++ ) canvas.Blend( 4, y, 0, 0, 255, 1f );
			var one = LineworkMagicWand.Select( canvas, 1, 1, 0f );
			Require( one.Count == 4, $"contiguous should stop at the blue wall, got {one.Count}" );
			Require( !one.Contains( 6, 1 ), "second region not reached contiguously" );
			var both = LineworkMagicWand.SelectColour( canvas, 255, 0, 0, 255, 0f, LineworkWandMode.Global );
			Require( both.Count == 8, $"global should take both regions, got {both.Count}" );
		} );

		Check( "tolerance grows the selection predictably", () =>
		{
			var canvas = new PaintCanvas( 5, 5 );
			canvas.Blend( 1, 1, 255, 0, 0, 1f );
			canvas.Blend( 2, 1, 230, 0, 0, 1f );
			canvas.Blend( 3, 1, 128, 0, 0, 1f );
			int At( float tol ) =>
				LineworkMagicWand.SelectColour( canvas, 255, 0, 0, 255, tol, LineworkWandMode.Global ).Count;
			Require( At( 0f ) == 1, "exact match only at tolerance 0" );
			Require( At( -1f ) == 1, "negative tolerance clamps to 0" );
			Require( At( 0.1f ) == 2, "near red (distance 0.057) joins at 0.1" );
			Require( At( 0.3f ) == 3, "far red (distance 0.288) joins at 0.3" );
			Require( At( 1f ) == 25, "tolerance 1 selects everything" );
		} );

		Check( "alpha participates in matching", () =>
		{
			var canvas = new PaintCanvas( 5, 5 );
			canvas.Blend( 1, 1, 255, 0, 0, 1f );
			canvas.Blend( 2, 1, 255, 0, 0, 0.5f );
			var half = AlphaAt( canvas, 2, 1 );
			Require( half >= 100 && half <= 160, $"half-weight alpha should be near 127, got {half}" );
			Require( LineworkMagicWand.SelectColour( canvas, 255, 0, 0, 255, 0f, LineworkWandMode.Global ).Count == 1,
				"same RGB at half alpha is not an exact match" );
			Require( LineworkMagicWand.SelectColour( canvas, 255, 0, 0, 255, 0.6f, LineworkWandMode.Global ).Count == 2,
				"alpha distance 0.5 joins at tolerance 0.6" );
		} );

		Check( "wand on unpainted area selects the connected background", () =>
		{
			var canvas = new PaintCanvas( 12, 12 );
			var layer = Layer( "L", true, Stroke( "red", true, 0f, "#FF0000",
				new[] { (1f, 1f), (6f, 1f), (6f, 6f), (1f, 6f) } ) );
			LineworkRaster.RenderLayer( canvas, layer, Identity(), NoOutlines() );
			var bg = LineworkMagicWand.Select( canvas, 0, 0, 0f );
			Require( bg.Count == 144 - 25, $"background is 119 texels, got {bg.Count}" );
			Require( !bg.Contains( 3, 3 ), "red square not in the background" );
			var bgGlobal = LineworkMagicWand.SelectColour( canvas, 0, 0, 0, 0, 0f, LineworkWandMode.Global );
			Require( bgGlobal.Count == bg.Count, "global transparent matches the background exactly" );
		} );

		Check( "diagonal contact does not connect a contiguous selection", () =>
		{
			var canvas = new PaintCanvas( 5, 5 );
			canvas.Blend( 2, 2, 255, 0, 0, 1f );
			canvas.Blend( 3, 3, 255, 0, 0, 1f );
			Require( LineworkMagicWand.Select( canvas, 2, 2, 0f ).Count == 1,
				"corner-touching texels are not 4-connected" );
			Require( LineworkMagicWand.SelectColour( canvas, 255, 0, 0, 255, 0f, LineworkWandMode.Global ).Count == 2,
				"global still takes both" );
		} );

		Check( "invalid wand inputs are refused", () =>
		{
			var canvas = new PaintCanvas( 12, 12 );
			Throws( () => LineworkMagicWand.Select( null, 0, 0, 0f ) );
			Throws( () => LineworkMagicWand.Select( canvas, -1, 0, 0f ) );
			Throws( () => LineworkMagicWand.Select( canvas, 12, 0, 0f ) );
			Throws( () => LineworkMagicWand.Select( canvas, 0, 0, float.NaN ) );
			Throws( () => LineworkMagicWand.SelectColour( canvas, 255, 0, 0, 255, 0f, LineworkWandMode.Contiguous ) );
			Throws( () => new LineworkSelection( 2, 2, new bool[3] ) );
		} );

		Check( "expand grows and shrink shrinks a selection", () =>
		{
			var canvas = new PaintCanvas( 9, 9 );
			for ( var y = 3; y <= 5; y++ )
				for ( var x = 3; x <= 5; x++ ) canvas.Blend( x, y, 255, 0, 0, 1f );
			var sel = LineworkMagicWand.SelectColour( canvas, 255, 0, 0, 255, 0f, LineworkWandMode.Global );
			Require( sel.Count == 9, $"3x3 block is 9, got {sel.Count}" );
			var grown = sel.Expand( 1 );
			Require( grown.Count == 25, $"expanded 5x5 is 25, got {grown.Count}" );
			Require( grown.MinX == 2 && grown.MaxX == 6, "expand grows one texel each way" );
			Require( sel.Expand( 0 ).Count == 9, "expand 0 changes nothing" );
			Require( grown.Shrink( 1 ).Count == 9, "shrink undoes one expand" );
			Require( sel.Count == 9, "expand/shrink never mutate the original" );
			var dotCanvas = new PaintCanvas( 9, 9 );
			dotCanvas.Blend( 4, 4, 255, 0, 0, 1f );
			var dot = LineworkMagicWand.Select( dotCanvas, 4, 4, 0f );
			Require( dot.Count == 1, "lone texel selects alone" );
			Require( dot.Shrink( 1 ).Count == 0, "a single texel shrinks to nothing" );
			Require( dot.Shrink( 1 ).MinX == -1, "empty selection has -1 bounds" );
		} );

		Check( "feather softens the edge, keeps the centre and the far outside", () =>
		{
			var canvas = new PaintCanvas( 13, 13 );
			for ( var y = 4; y <= 8; y++ )
				for ( var x = 4; x <= 8; x++ ) canvas.Blend( x, y, 255, 0, 0, 1f );
			var sel = LineworkMagicWand.SelectColour( canvas, 255, 0, 0, 255, 0f, LineworkWandMode.Global );
			var soft = sel.Feather( 2f );
			Require( soft.Width == 13 && soft.Height == 13, "feather keeps the canvas size" );
			Near( soft.Values[6 * 13 + 6], 1f, 1e-5f, "deep centre stays 1" );
			var edge = soft.Values[6 * 13 + 4];
			Require( edge > 0f && edge < 1f, $"edge texel should be soft, got {edge}" );
			Near( soft.Values[0], 0f, 1e-5f, "far outside stays 0" );
			foreach ( var v in soft.Values ) Require( v >= 0f && v <= 1f, "feather out of range" );
			var hard = sel.Feather( 0f );
			Near( hard.Values[6 * 13 + 6], 1f, 1e-5f, "radius 0 keeps the centre" );
			Near( hard.Values[6 * 13 + 3], 0f, 1e-5f, "radius 0 keeps the outside" );
		} );

		Check( "selection survives conversion to a mask and back", () =>
		{
			var canvas = new PaintCanvas( 12, 12 );
			var layer = Layer( "L", true, Stroke( "red", true, 0f, "#FF0000",
				new[] { (1f, 1f), (6f, 1f), (6f, 6f), (1f, 6f) } ) );
			LineworkRaster.RenderLayer( canvas, layer, Identity(), NoOutlines() );
			var sel = LineworkMagicWand.Select( canvas, 3, 3, 0f );
			var fromMask = LineworkSelection.FromMask( sel.ToMask(), sel.Width, sel.Height );
			Require( fromMask.Count == sel.Count && fromMask.Contains( 3, 3 ) && !fromMask.Contains( 8, 2 ),
				"mask round-trip changes the selection" );
			var alpha = sel.ToBrushAlpha();
			Require( alpha.Width == 12 && alpha.Height == 12, "brush alpha keeps the size" );
			Near( alpha.Values[3 * 12 + 3], 1f, 1e-5f, "selected texel is 1 in the alpha" );
			Near( alpha.Values[2 * 12 + 8], 0f, 1e-5f, "unselected texel is 0 in the alpha" );
			var fromAlpha = LineworkSelection.FromBrushAlpha( alpha );
			Require( fromAlpha.Count == sel.Count, "brush alpha round-trip changes the selection" );
			Require( LineworkSelection.FromBrushAlpha( alpha, 1.5f ).Count == 0,
				"threshold above 1 selects nothing" );
		} );
	}
}
