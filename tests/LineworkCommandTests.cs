using System;
using System.Collections.Generic;
using System.IO;
using Effigy;

internal static class LineworkCommandTests
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
		int CountChan( PaintCanvas canvas, int chan, byte over )
		{
			var n = 0;
			for ( var i = chan; i < canvas.Rgba.Length; i += 4 ) if ( canvas.Rgba[i] > over ) n++;
			return n;
		}
		int CountAlpha( PaintCanvas canvas )
		{
			var n = 0;
			for ( var i = 3; i < canvas.Rgba.Length; i += 4 ) if ( canvas.Rgba[i] > 0 ) n++;
			return n;
		}
		List<Vec2> Line( float x0, float y0, float x1, float y1 ) => new() { new Vec2( x0, y0 ), new Vec2( x1, y1 ) };
		List<Vec2> Square( float x0, float y0, float x1, float y1 ) => new()
		{
			new Vec2( x0, y0 ), new Vec2( x1, y0 ), new Vec2( x1, y1 ), new Vec2( x0, y1 ),
		};

		// ---- bot draw commands ----

		Check( "cmd: stroke rasterizes to the expected pixels", () =>
		{
			var doc = LineworkCommands.NewDocument( "cmd-stroke" );
			var drawn = LineworkCommands.AddStroke( doc, "Ink", Line( 0f, 0f, 10f, 0f ),
				null, "pen", "#ff0000", 3f );
			Require( drawn.Ok, "stroke refused: " + drawn.Error );
			Require( drawn.StrokeId.Length > 0, "stroke has no id" );
			Require( drawn.SampledPoints == 9, $"pressure spline should sample 9 points, got {drawn.SampledPoints}" );
			var (canvas, result, error) = LineworkCommands.Rasterize( doc, 32, 32 );
			Require( error is null, "rasterize refused: " + error );
			Require( result.StrokesDrawn == 1, $"expected 1 stroke drawn, got {result.StrokesDrawn}" );
			Require( result.Pixels > 0, "stroke painted no pixels" );
			Require( CountChan( canvas, 0, 200 ) > 0, "no red texel in the raster" );
			Require( CountChan( canvas, 2, 200 ) == 0, "blue texels in a red-only raster" );
		} );

		Check( "cmd: fill fills the bounded region", () =>
		{
			var doc = LineworkCommands.NewDocument( "cmd-fill" );
			var filled = LineworkCommands.Fill( doc, "Ink", Square( 0f, 0f, 10f, 10f ), "#0000ff" );
			Require( filled.Ok, "fill refused: " + filled.Error );
			var (canvas, result, error) = LineworkCommands.Rasterize( doc, 32, 32 );
			Require( error is null, "rasterize refused: " + error );
			Require( result.FillsDrawn == 1, $"expected 1 fill drawn, got {result.FillsDrawn}" );
			Require( CountChan( canvas, 2, 200 ) > 100, $"fill painted too few blue texels: {CountChan( canvas, 2, 200 )}" );
			Require( CountChan( canvas, 0, 200 ) == 0, "red texels in a blue-only fill" );
		} );

		Check( "cmd: erase clears pixels without deleting strokes", () =>
		{
			var doc = LineworkCommands.NewDocument( "cmd-erase" );
			Require( LineworkCommands.Fill( doc, "Ink", Square( 0f, 0f, 10f, 10f ), "#ff0000" ).Ok );
			var (before, _, _) = LineworkCommands.Rasterize( doc, 32, 32 );
			var redBefore = CountChan( before, 0, 200 );
			Require( redBefore > 100, "fill did not paint" );
			var erased = LineworkCommands.Erase( doc, "Ink", Line( 0f, 5f, 10f, 5f ), 4f );
			Require( erased.Ok, "erase refused: " + erased.Error );
			Require( doc.Layers[0].Strokes.Count == 2, "erase must add an eraser stroke, not delete the fill" );
			var (after, _, _) = LineworkCommands.Rasterize( doc, 32, 32 );
			var redAfter = CountChan( after, 0, 200 );
			Require( redAfter < redBefore, $"erase cleared nothing: {redBefore} -> {redAfter}" );
			Require( CountAlpha( after ) > 0, "erase cleared the whole canvas" );
		} );

		Check( "cmd: invalid layer, colour, tool and size are refused honestly", () =>
		{
			var doc = LineworkCommands.NewDocument( "cmd-invalid" );
			var empty = LineworkCommands.AddStroke( doc, "   ", Line( 0f, 0f, 1f, 0f ), null, "pen", "#ff0000", 2f );
			Require( !empty.Ok && empty.Error.Contains( "empty" ), "empty layer name was not refused" );
			Require( doc.Layers.Count == 0, "refused stroke created a layer" );
			var badColor = LineworkCommands.AddStroke( doc, "Ink", Line( 0f, 0f, 1f, 0f ), null, "pen", "red", 2f );
			Require( !badColor.Ok && badColor.Error.Contains( "#RRGGBB" ), "bad colour was not refused" );
			var badTool = LineworkCommands.AddStroke( doc, "Ink", Line( 0f, 0f, 1f, 0f ), null, "brush", "#ff0000", 2f );
			Require( !badTool.Ok, "bad tool was not refused" );
			var badSize = LineworkCommands.AddStroke( doc, "Ink", Line( 0f, 0f, 1f, 0f ), null, "pen", "#ff0000", 0f );
			Require( !badSize.Ok, "zero size was not refused" );
			var badRing = LineworkCommands.Fill( doc, "Ink", Line( 0f, 0f, 1f, 0f ), "#ff0000" );
			Require( !badRing.Ok, "two-point fill was not refused" );
			Require( doc.Layers.Count == 0, "refused commands added layers" );
		} );

		Check( "cmd: save and reopen round-trips the layer", () =>
		{
			var doc = LineworkCommands.NewDocument( "cmd-roundtrip" );
			Require( LineworkCommands.AddStroke( doc, "Ink", Line( 0f, 0f, 10f, 0f ),
				new[] { 0.5f, 1f }, "pen", "#ff0000", 2f ).Ok );
			var path = Path.Combine( settings.WorkDirectory, "cmd-linework.linework" );
			Require( LineworkCommands.TrySave( doc, path, out var saveError ), "save failed: " + saveError );
			Require( LineworkCommands.TryLoadFile( path, out var reloaded, out var loadError ), "load failed: " + loadError );
			Require( reloaded.Layers.Count == 1, $"expected 1 layer, got {reloaded.Layers.Count}" );
			Require( reloaded.Layers[0].Name == "Ink", "layer name did not survive" );
			Require( reloaded.Layers[0].Strokes.Count == 1, "stroke did not survive" );
			Require( reloaded.Layers[0].Strokes[0].Points.Count == 2, "stroke points did not survive" );
			Require( reloaded.Layers[0].Strokes[0].Color == "#ff0000", "stroke colour did not survive" );
			var json = LineworkWriter.Serialize( doc );
			Require( LineworkCommands.TryLoadJson( json, out var fromJson, out var jsonError ), "json load failed: " + jsonError );
			Require( fromJson.Layers[0].Strokes.Count == 1, "json round-trip lost the stroke" );
			Require( !LineworkCommands.TryLoadJson( "{not json", out _, out var badError ), "bad json was not refused" );
			Require( badError.Length > 0, "bad json refusal has no message" );
		} );

		// ---- magic wand (LineworkSelection.cs) ----

		PaintCanvas TwoColour( int w, int h )
		{
			var c = new PaintCanvas( w, h );
			for ( var y = 0; y < h; y++ )
				for ( var x = 0; x < w; x++ )
				{
					var o = (y * w + x) * 4;
					if ( x < w / 2 ) { c.Rgba[o] = 255; c.Rgba[o + 1] = 0; c.Rgba[o + 2] = 0; c.Rgba[o + 3] = 255; }
					else { c.Rgba[o] = 0; c.Rgba[o + 1] = 0; c.Rgba[o + 2] = 255; c.Rgba[o + 3] = 255; }
				}
			return c;
		}

		Check( "wand: click on red selects exactly the red region, not blue", () =>
		{
			var canvas = TwoColour( 8, 8 );
			var red = LineworkMagicWand.Select( canvas, 1, 1, 0.05f, LineworkWandMode.Contiguous );
			Require( red.Count == 32, $"expected 32 red texels, got {red.Count}" );
			Require( red.MinX == 0 && red.MaxX == 3 && red.MinY == 0 && red.MaxY == 7, "red bounds are wrong" );
			Require( !red.Contains( 4, 1 ), "selection leaked into the blue region" );
			var blue = LineworkMagicWand.Select( canvas, 6, 1, 0.05f, LineworkWandMode.Contiguous );
			Require( blue.Count == 32, $"expected 32 blue texels, got {blue.Count}" );
			var global = LineworkMagicWand.SelectColour( canvas, 255, 0, 0, 255, 0.05f, LineworkWandMode.Global );
			Require( global.Count == 32, $"global red should be 32, got {global.Count}" );
		} );

		Check( "wand: a blue wall blocks the contiguous fill but not the global one", () =>
		{
			var c = new PaintCanvas( 8, 8 );
			for ( var y = 0; y < 8; y++ )
				for ( var x = 0; x < 8; x++ )
				{
					var o = (y * 8 + x) * 4;
					var red = x <= 1 || (x >= 3 && x <= 4);
					c.Rgba[o] = red ? (byte)255 : (byte)0;
					c.Rgba[o + 2] = red ? (byte)0 : (byte)255;
					c.Rgba[o + 3] = 255;
				}
			var contig = LineworkMagicWand.Select( c, 0, 0, 0.05f, LineworkWandMode.Contiguous );
			Require( contig.Count == 16, $"contiguous should stop at the wall: {contig.Count}" );
			var global = LineworkMagicWand.SelectColour( c, 255, 0, 0, 255, 0.05f, LineworkWandMode.Global );
			Require( global.Count == 32, $"global should take both red blocks: {global.Count}" );
		} );

		Check( "wand: raising tolerance grows the selection predictably", () =>
		{
			var c = new PaintCanvas( 8, 8 );
			for ( var y = 0; y < 8; y++ )
				for ( var x = 0; x < 8; x++ )
				{
					var o = (y * 8 + x) * 4;
					if ( x <= 1 ) { c.Rgba[o] = 255; c.Rgba[o + 1] = 0; c.Rgba[o + 2] = 0; }
					else if ( x <= 3 ) { c.Rgba[o] = 255; c.Rgba[o + 1] = 20; c.Rgba[o + 2] = 20; }
					else { c.Rgba[o] = 0; c.Rgba[o + 1] = 0; c.Rgba[o + 2] = 255; }
					c.Rgba[o + 3] = 255;
				}
			var tight = LineworkMagicWand.Select( c, 0, 0, 0.05f, LineworkWandMode.Contiguous );
			Require( tight.Count == 16, $"tight tolerance should take exact red only: {tight.Count}" );
			var loose = LineworkMagicWand.Select( c, 0, 0, 0.1f, LineworkWandMode.Contiguous );
			Require( loose.Count == 32, $"loose tolerance should take red + near-red: {loose.Count}" );
		} );

		Check( "wand: selection survives conversion to a mask and back", () =>
		{
			var canvas = TwoColour( 8, 8 );
			var sel = LineworkMagicWand.Select( canvas, 1, 1, 0.05f, LineworkWandMode.Contiguous );
			var back = LineworkSelection.FromMask( sel.ToMask(), sel.Width, sel.Height );
			Require( back.Count == sel.Count, "mask round-trip changed the count" );
			Require( back.Contains( 1, 1 ) && !back.Contains( 7, 7 ), "mask round-trip changed the shape" );
			var viaAlpha = LineworkSelection.FromBrushAlpha( sel.ToBrushAlpha(), 0.5f );
			Require( viaAlpha.Count == sel.Count, "brush-alpha round-trip changed the count" );
		} );

		Check( "wand: expand, shrink and feather behave", () =>
		{
			var mask = new bool[64];
			mask[3 * 8 + 3] = mask[3 * 8 + 4] = mask[4 * 8 + 3] = mask[4 * 8 + 4] = true;
			var sel = LineworkSelection.FromMask( mask, 8, 8 );
			Require( sel.Count == 4, "fixture should select 4" );
			var grown = sel.Expand( 1 );
			Require( grown.Count == 12, $"expand(1) on a 2x2 block should be the 12-texel diamond: {grown.Count}" );
			Require( grown.Shrink( 1 ).Count == 4, "shrink(expand(1)) should restore the block" );
			var feathered = sel.Feather( 1f );
			Require( feathered.Values[3 * 8 + 3] > 0.25f && feathered.Values[3 * 8 + 3] < 1f,
				"feather should soften the block edge, not keep it hard or erase it" );
			Require( feathered.Values[0] == 0f, "feather leaked to a far texel" );
			var hard = sel.Feather( 0f );
			Require( hard.Values[3 * 8 + 3] == 1f && hard.Values[0] == 0f, "feather(0) should stay hard 0/1" );
		} );

		Check( "wand: out-of-bounds seed and null canvas are refused", () =>
		{
			var canvas = TwoColour( 8, 8 );
			try { LineworkMagicWand.Select( canvas, -1, 0, 0.1f ); Require( false, "negative seed was not refused" ); }
			catch ( ArgumentOutOfRangeException ) { }
			try { LineworkMagicWand.Select( null, 0, 0, 0.1f ); Require( false, "null canvas was not refused" ); }
			catch ( ArgumentNullException ) { }
		} );
	}
}
