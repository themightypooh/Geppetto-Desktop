using System;
using System.Collections.Generic;
using Marionette.Desktop;

// Paint layer clipping, adjustments and PBR brush behaviour (Desktop ticket
// bring-paint-layer-clipping-adjustments-and-pbr-brush-behavior-to-parity):
// ClipToBelow / PaintAdjust params, PaintSession Normal + radial + PBR material
// strokes, PaintLayerOps add/remove/move/merge/flatten/clip/adjust, and the
// TexturePaintService material dab. NOT yet registered in tests/TestRegistry.cs
// (coordinator-owned); the final integrator adds the one Register line.
// Implementation done is NOT runtime verified: no full build or app launch was
// run for this ticket (PO workflow override).
internal static class PaintClippingTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "paint-clipping", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void NearF( float actual, float expected, float tolerance, string what )
		{
			if ( Math.Abs( actual - expected ) > tolerance ) throw new Exception( what + ": expected " + expected + " ±" + tolerance + ", got " + actual );
		}
		void NearB( byte actual, int expected, int tolerance, string what )
		{
			if ( Math.Abs( actual - expected ) > tolerance ) throw new Exception( what + ": expected " + expected + " ±" + tolerance + ", got " + actual );
		}
		Effigy.PaintCanvas Solid( int w, int h, byte r, byte g, byte b, byte a = 255 )
		{
			var c = new Effigy.PaintCanvas( w, h );
			for ( var i = 0; i < w * h; i++ )
			{
				c.Rgba[i * 4] = r; c.Rgba[i * 4 + 1] = g; c.Rgba[i * 4 + 2] = b; c.Rgba[i * 4 + 3] = a;
			}
			return c;
		}
		Effigy.PolyMesh QuadMesh()
		{
			var mesh = new Effigy.PolyMesh();
			var a = mesh.AddVertex( new Effigy.Vec3( -1f, -1f, 0f ) );
			var b = mesh.AddVertex( new Effigy.Vec3( 1f, -1f, 0f ) );
			var c = mesh.AddVertex( new Effigy.Vec3( 1f, 1f, 0f ) );
			var d = mesh.AddVertex( new Effigy.Vec3( -1f, 1f, 0f ) );
			mesh.AddFace( new[] { a, b, c }, new[] { new Effigy.Vec2( 0f, 0f ), new Effigy.Vec2( 1f, 0f ), new Effigy.Vec2( 1f, 1f ) } );
			mesh.AddFace( new[] { a, c, d }, new[] { new Effigy.Vec2( 0f, 0f ), new Effigy.Vec2( 1f, 1f ), new Effigy.Vec2( 0f, 1f ) } );
			return mesh;
		}
		Effigy.PartStudio StudioWithBody()
		{
			var studio = new Effigy.PartStudio();
			studio.Bodies.Add( new Effigy.Body( "body1", "Body", QuadMesh() ) );
			return studio;
		}
		int PaintLayerCount( Effigy.PartStudio studio )
		{
			var n = 0;
			foreach ( var f in studio.Features )
				if ( f is Effigy.PaintFeature ) n++;
			return n;
		}

		Check( "pbr material channels are colour roughness metalness height in order", () =>
		{
			var gold = Effigy.PaintPbrMaterial.Presets()[0];
			Require( gold.Name == "Gold", "first preset is gold" );
			var channels = gold.Channels();
			Require( channels.Count == 3, "gold paints 3 channels, got " + channels.Count );
			Require( channels[0] == Effigy.PaintChannel.Color, "colour first" );
			Require( channels[1] == Effigy.PaintChannel.Roughness, "roughness second" );
			Require( channels[2] == Effigy.PaintChannel.Metalness, "metalness third" );
			var rust = Effigy.PaintPbrMaterial.Presets()[3];
			Require( rust.Channels().Contains( Effigy.PaintChannel.Height ), "rust paints height" );
			var none = new Effigy.PaintPbrMaterial { UseColor = false, UseRoughness = false, UseMetalness = false, UseHeight = false };
			Require( none.Channels().Count == 0, "all-off material paints nothing" );
		} );

		Check( "pbr material values are per channel", () =>
		{
			var gold = Effigy.PaintPbrMaterial.Presets()[0];
			var (r, g, b) = gold.ValueFor( Effigy.PaintChannel.Color );
			NearF( r, 1.00f, 0.01f, "gold red" );
			NearF( g, 0.766f, 0.01f, "gold green" );
			NearF( b, 0.336f, 0.01f, "gold blue" );
			var (rr, _, _) = gold.ValueFor( Effigy.PaintChannel.Roughness );
			NearF( rr, 0.25f, 0.01f, "gold roughness grey" );
			var (mr, _, _) = gold.ValueFor( Effigy.PaintChannel.Metalness );
			NearF( mr, 1f, 0.01f, "gold metalness grey" );
			var rust = Effigy.PaintPbrMaterial.Presets()[3];
			var (hr, _, _) = rust.ValueFor( Effigy.PaintChannel.Height );
			NearF( hr, 0.58f, 0.01f, "rust height grey" );
		} );

		Check( "normal colour is flat at zero lean and leans the right way", () =>
		{
			var (r, g, b) = Effigy.PaintSession.NormalColour( 0f, 0f );
			NearF( r, 0.5f, 0.01f, "flat red" );
			NearF( g, 0.5f, 0.01f, "flat green" );
			NearF( b, 1f, 0.01f, "flat blue" );
			var (rr, gg, _) = Effigy.PaintSession.NormalColour( 1f, 0f );
			Require( rr > 0.9f, "lean right pushes red up, got " + rr );
			NearF( gg, 0.5f, 0.01f, "lean right leaves green flat" );
			var (_, gu, _) = Effigy.PaintSession.NormalColour( 0f, 1f );
			Require( gu < 0.1f, "lean up pulls green down (DirectX green), got " + gu );
		} );

		Check( "material stroke paints one stroke per channel", () =>
		{
			var session = new Effigy.PaintSession( QuadMesh(), 16 );
			session.Channel = Effigy.PaintChannel.Color;
			session.Material = Effigy.PaintPbrMaterial.Presets()[0];
			session.Radius = 0.5f;
			session.Strength = 1f;
			Require( session.BeginStroke( new Effigy.Vec3( 0f, 0f, 2f ), new Effigy.Vec3( 0f, 0f, -1f ) ), "begin hits the quad" );
			var stroke = session.EndStroke();
			Require( stroke is not null, "stroke committed" );
			Require( session.LastStrokes.Count == 3, "gold paints 3 strokes, got " + session.LastStrokes.Count );
			var seen = new HashSet<Effigy.PaintChannel>();
			foreach ( var s in session.LastStrokes )
				seen.Add( s.Channel );
			Require( seen.Contains( Effigy.PaintChannel.Color ), "colour stroke present" );
			Require( seen.Contains( Effigy.PaintChannel.Roughness ), "roughness stroke present" );
			Require( seen.Contains( Effigy.PaintChannel.Metalness ), "metalness stroke present" );
			Require( session.LastStrokes[session.LastStrokes.Count - 1].Channel == Effigy.PaintChannel.Color,
				"tracked stroke last is the on-screen channel" );
			var i = ( 8 * 16 + 8 ) * 4;
			Require( session.Canvas.Rgba[i] > 200, "gold red on the canvas, got " + session.Canvas.Rgba[i] );
			NearB( session.Canvas.Rgba[i + 1], 195, 30, "gold green on the canvas" );
			NearB( session.Canvas.Rgba[i + 2], 86, 30, "gold blue on the canvas" );
		} );

		Check( "layer ops add remove move", () =>
		{
			var studio = StudioWithBody();
			var bottom = new Effigy.PaintFeature();
			var top = new Effigy.PaintFeature();
			studio.Add( bottom );
			studio.Add( top );
			Require( Effigy.PaintLayerOps.LayersOf( studio, top ).Count == 2, "two layers in the stack" );
			var added = Effigy.PaintLayerOps.AddLayer( studio, top, false, null, out var error );
			Require( added is not null && error is null, "add succeeds: " + error );
			Require( studio.Features.IndexOf( added ) == studio.Features.IndexOf( top ) + 1, "added above the top layer" );
			Require( Effigy.PaintLayerOps.LayersOf( studio, top ).Count == 3, "three layers after add" );
			Require( Effigy.PaintLayerOps.MoveLayer( studio, added, -1, out error ), "move down: " + error );
			Require( studio.Features.IndexOf( added ) < studio.Features.IndexOf( top ), "moved below the top layer" );
			Require( !Effigy.PaintLayerOps.MoveLayer( studio, bottom, -1, out error ) && error is not null,
				"moving the bottom layer down is refused" );
			var next = Effigy.PaintLayerOps.RemoveLayer( studio, added, out error );
			Require( next is not null && error is null, "remove returns a neighbour: " + error );
			Require( !studio.Features.Contains( added ), "removed layer leaves the studio" );
			Require( Effigy.PaintLayerOps.RemoveLayer( studio, added, out error ) is null && error is not null,
				"removing a layer that is not in the studio is refused" );
		} );

		Check( "merge down folds strokes fill and opacity", () =>
		{
			var studio = StudioWithBody();
			var below = new Effigy.PaintFeature();
			var upper = new Effigy.PaintFeature();
			studio.Add( below );
			studio.Add( upper );
			var stroke = new Effigy.PaintStroke
			{
				R = 1f, G = 0f, B = 0f, Radius = 0.1f, Strength = 0.5f, Channel = Effigy.PaintChannel.Color,
			};
			stroke.Path.Add( new Effigy.PaintStrokePoint( new Effigy.Vec3( 0f, 0f, 0f ), new Effigy.Vec3( 0f, 0f, 1f ) ) );
			upper.AddStroke( stroke );
			upper.Opacity.Value = 0.5f;
			upper.Fill.Value = true;
			var merged = Effigy.PaintLayerOps.MergeLayerDown( studio, upper, out var error );
			Require( merged is not null && error is null, "merge succeeds: " + error );
			Require( ReferenceEquals( merged, below ), "merge returns the layer below" );
			Require( below.Strokes is { Count: 1 }, "stroke folded into the layer below" );
			NearF( below.Strokes[0].Strength, 0.25f, 0.01f, "upper opacity baked into strength (0.5 * 0.5)" );
			Require( below.Fill.Value, "upper fill inherited when below has none" );
			Require( !studio.Features.Contains( upper ), "merged layer removed" );
			Require( Effigy.PaintLayerOps.MergeLayerDown( studio, below, out error ) is null && error is not null,
				"merging the bottom layer is refused" );
		} );

		Check( "flatten folds every layer into the bottom", () =>
		{
			var studio = StudioWithBody();
			var bottom = new Effigy.PaintFeature();
			var mid = new Effigy.PaintFeature();
			var top = new Effigy.PaintFeature();
			studio.Add( bottom );
			studio.Add( mid );
			studio.Add( top );
			mid.AddStroke( new Effigy.PaintStroke { Channel = Effigy.PaintChannel.Color, Strength = 1f } );
			var flat = Effigy.PaintLayerOps.FlattenLayers( studio, top, out var error );
			Require( flat is not null && error is null, "flatten succeeds: " + error );
			Require( ReferenceEquals( flat, bottom ), "flatten returns the bottom layer" );
			Require( PaintLayerCount( studio ) == 1, "one layer left after flatten" );
			Require( bottom.Strokes is { Count: 1 }, "middle stroke folded into the bottom layer" );
			Require( Effigy.PaintLayerOps.FlattenLayers( studio, bottom, out error ) is null && error is not null,
				"flattening a single layer is refused" );
		} );

		Check( "clip to below refused on the bottom and settable above; adjust round-trips", () =>
		{
			var studio = StudioWithBody();
			var bottom = new Effigy.PaintFeature();
			var top = new Effigy.PaintFeature();
			studio.Add( bottom );
			studio.Add( top );
			Require( !Effigy.PaintLayerOps.SetClipToBelow( studio, bottom, true, out var error ) && error is not null,
				"clipping the bottom layer is refused" );
			Require( Effigy.PaintLayerOps.SetClipToBelow( studio, top, true, out error ), "clip above: " + error );
			Require( top.ClipToBelow.Value, "clip flag set on the layer" );
			Require( Effigy.PaintLayerOps.SetClipToBelow( studio, top, true, out error ), "setting clip twice is a no-op" );
			Require( Effigy.PaintLayerOps.SetClipToBelow( studio, top, false, out error ) && !top.ClipToBelow.Value,
				"clip turns back off: " + error );
			var adjust = new Effigy.PaintAdjust( 30f, 1.2f, 0.9f, 0.1f, 0.9f, 1.5f );
			Require( Effigy.PaintLayerOps.SetAdjust( studio, top, adjust, out error ), "set adjust: " + error );
			NearF( top.Adjust.Hue, 30f, 0.01f, "adjust hue" );
			NearF( top.Adjust.Saturation, 1.2f, 0.01f, "adjust saturation" );
			NearF( top.Adjust.Black, 0.1f, 0.01f, "adjust black" );
			NearF( top.Adjust.White, 0.9f, 0.01f, "adjust white" );
			NearF( top.Adjust.Gamma, 1.5f, 0.01f, "adjust gamma" );
			var stray = new Effigy.PaintFeature();
			Require( !Effigy.PaintLayerOps.SetAdjust( studio, stray, adjust, out error ) && error is not null,
				"adjusting a layer outside the studio is refused" );
		} );

		Check( "material dab on canvas touches only the channels the material uses", () =>
		{
			var canvas = new Effigy.PaintCanvas( 16, 16 );
			var brush = new TexturePaintService.TextureBrush { X = 8, Y = 8, Radius = 4f, Strength = 1f };
			var touched = TexturePaintService.DabMaterialOnCanvas( canvas, brush, Effigy.PaintPbrMaterial.Presets()[0] );
			Require( touched > 0, "gold dab touches texels" );
			Require( canvas.RoughnessMap is not null, "gold paints roughness" );
			Require( canvas.MetalnessMap is not null, "gold paints metalness" );
			Require( canvas.HeightMap is null, "gold has no height, so no height map is created" );
			Require( canvas.EmissiveMap is null, "gold has no emissive, so no emissive map is created" );
			var i = ( 8 * 16 + 8 ) * 4;
			Require( canvas.Rgba[i] > 200, "gold red on the canvas" );
			NearB( canvas.RoughnessMap.Rgba[i], 64, 12, "gold roughness grey" );
			NearB( canvas.MetalnessMap.Rgba[i], 255, 5, "gold metalness grey" );
			var rustCanvas = new Effigy.PaintCanvas( 16, 16 );
			TexturePaintService.DabMaterialOnCanvas( rustCanvas, brush, Effigy.PaintPbrMaterial.Presets()[3] );
			Require( rustCanvas.HeightMap is not null, "rust paints height" );
			NearB( rustCanvas.HeightMap.Rgba[i], 148, 15, "rust height grey" );
			var erased = TexturePaintService.DabMaterialOnCanvas( canvas,
				new TexturePaintService.TextureBrush { X = 8, Y = 8, Radius = 4f, Strength = 1f, Erase = true },
				Effigy.PaintPbrMaterial.Presets()[0] );
			Require( erased > 0, "erase dab touches texels" );
		} );

		Check( "adjustment recolours colour but leaves emissive alone", () =>
		{
			var maps = new Effigy.PaintLayerMaps
			{
				Size = 4,
				Color = Solid( 4, 4, 200, 100, 50 ),
				Emissive = Solid( 4, 4, 10, 20, 30 ),
			};
			var adjusted = new Effigy.PaintAdjust( 120f ).Apply( maps );
			Require( ReferenceEquals( adjusted.Emissive, maps.Emissive ), "adjustment shares the emissive map untouched" );
			Require( adjusted.Color.Rgba[0] < 150, "hue +120 moves red down, got " + adjusted.Color.Rgba[0] );
			Require( adjusted.Color.Rgba[1] > 150, "hue +120 moves green up, got " + adjusted.Color.Rgba[1] );
			Require( adjusted.Color.Rgba[3] == 255, "adjustment never touches alpha" );
		} );
	}
}
