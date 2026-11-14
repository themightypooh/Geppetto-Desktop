#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Effigy;
using Effigy.Map;

// Hammer Desktop Interop: paint / PBR material layers on brush + displacement faces
// (hammer-interop-paint-layers-on-faces).
//
// PaintOnMap bridges the sculpt side's PBR layer stack (PaintLayerStack) onto map
// faces: a brush face keeps its FaceMaterial and gains a FacePaint layered over it,
// and a displacement's blend alpha is driven by the stack's coverage with the same
// move-toward-target maths the sculpt PaintAlpha dab uses.
//
// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned and the
// final integrator adds the one PaintOnMapTests.Register(registry, settings); line.
internal static class PaintOnMapTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "paintonmap", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, float tolerance, string what )
		{
			if ( MathF.Abs( actual - expected ) > tolerance )
				throw new Exception( $"{what}: expected {expected:0.####}, got {actual:0.####}" );
		}
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: expected {typeof( T ).Name}, got {ex.GetType().Name}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, got silent success" );
		}

		string FindFixture()
		{
			const string rel = "hammer-interop-paint-layers-on-faces/face-paint.json";
			var candidates = new[]
			{
				Path.GetFullPath( Path.Combine( "tests", "fixtures", "hammer", rel ) ),
				Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "fixtures", "hammer", rel ) ),
			};
			foreach ( var candidate in candidates )
				if ( File.Exists( candidate ) ) return candidate;
			var dir = new DirectoryInfo( Directory.GetCurrentDirectory() );
			for ( var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent )
			{
				var probe = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", rel );
				if ( File.Exists( probe ) ) return probe;
			}
			throw new Exception( $"fixture tests/fixtures/hammer/{rel} not found" );
		}

		/// <summary>
		/// A 64x64 quad in the z=0 plane, the displacement face every alpha check uses.
		/// With the default (world-scale) UVs the face spans 64 UV units; pass 1/64 to
		/// map it to 0..1. Either way U follows the grid ROW for this face (its tangent
		/// frame puts U along +Y, corner 0 -&gt; corner 3) and V follows the column, negated.
		/// </summary>
		Effigy.Map.MapBrushFace QuadFace( Vec2 uvScale = default ) => Effigy.Map.MapBrushFace.FromCorners( new[]
		{
			new Vec3( 0f, 0f, 0f ), new Vec3( 64f, 0f, 0f ),
			new Vec3( 64f, 64f, 0f ), new Vec3( 0f, 64f, 0f ),
		}, 0, default, uvScale );

		Check( "red layer paints a brush face red and the base material survives", () =>
		{
			var brush = MapBrush.Box( new Vec3( 0f, 0f, 0f ), new Vec3( 64f, 64f, 64f ), "brick/brickwall001" );
			Require( brush.Faces.Count == 6, $"box faces: {brush.Faces.Count}" );
			var paint = PaintOnMap.ForBrushFace( brush, 0 );
			paint.Stack.Add( PaintLayer.Solid( "red", 8, 255, 0, 0 ) );
			var composite = paint.Composite();
			Require( composite is not null, "a stack with a solid layer must composite" );
			var (r, g, b, a) = PaintOnMap.SampleColor( composite, 0.5f, 0.5f );
			Require( r == 255 && g == 0 && b == 0 && a == 255, $"centre texel: ({r},{g},{b},{a})" );
			Require( brush.Faces[0].MaterialId == "brick/brickwall001",
				$"brush face re-textured by paint: {brush.Faces[0].MaterialId}" );
			Require( paint.Base.MaterialName == "brick/brickwall001",
				$"paint base is not the face material: {paint.Base.MaterialName}" );
		} );

		Check( "material read-back blends by coverage: painted is second material, bare is base", () =>
		{
			var brush = MapBrush.Box( new Vec3( 0f, 0f, 0f ), new Vec3( 64f, 64f, 64f ), "brick/brickwall001" );
			var paint = PaintOnMap.ForBrushFace( brush, 0, "grass/grass001" );
			var bare = PaintOnMap.MaterialAt( paint, 0.5f, 0.5f );
			Require( bare.BaseMaterial == "brick/brickwall001", $"bare base: {bare.BaseMaterial}" );
			Require( bare.SecondMaterial == "grass/grass001", $"bare second: {bare.SecondMaterial}" );
			Near( bare.Factor, 0f, 1e-6f, "bare coverage" );
			paint.Stack.Add( PaintLayer.Solid( "red", 8, 255, 0, 0 ) );
			var painted = PaintOnMap.MaterialAt( paint, 0.5f, 0.5f );
			Near( painted.Factor, 1f, 1e-6f, "painted coverage" );
			Require( painted.BaseMaterial == "brick/brickwall001", "painted base changed" );
			Require( painted.SecondMaterial == "grass/grass001", "painted second changed" );
		} );

		Check( "roughness metalness height ride the same stack; an unpainted channel is its base", () =>
		{
			var paint = new FacePaint( new FaceMaterial( "brick/brickwall001" ), new PaintLayerStack() );
			var layer = PaintLayer.Solid( "pbr", 8, 255, 0, 0 );
			layer.SetChannel( PaintChannel.Roughness, PaintLayers.Solid( 8, 8, 128, 128, 128 ) );
			layer.SetChannel( PaintChannel.Metalness, PaintLayers.Solid( 8, 8, 255, 255, 255 ) );
			layer.SetChannel( PaintChannel.Height, PaintLayers.Solid( 8, 8, 0, 0, 0 ) );
			paint.Stack.Add( layer );
			var composite = paint.Composite();
			Require( composite is not null, "pbr stack must composite" );
			var (r, g, b, _) = PaintOnMap.SampleColor( composite, 0.5f, 0.5f );
			Require( r == 255 && g == 0 && b == 0, $"colour channel lost: ({r},{g},{b})" );
			Near( PaintOnMap.SampleChannel( composite, PaintChannel.Roughness, 0.5f, 0.5f ), 128f / 255f, 1e-6f, "roughness" );
			Near( PaintOnMap.SampleChannel( composite, PaintChannel.Metalness, 0.5f, 0.5f ), 1f, 1e-6f, "metalness" );
			Near( PaintOnMap.SampleChannel( composite, PaintChannel.Height, 0.5f, 0.5f ), 0f, 1e-6f, "height" );
			Near( PaintOnMap.SampleChannel( composite, PaintChannel.Emissive, 0.5f, 0.5f ), PaintChannels.Base( PaintChannel.Emissive ), 1e-6f, "unpainted emissive" );
			Near( PaintOnMap.SampleChannel( composite, PaintChannel.Opacity, 0.5f, 0.5f ), PaintChannels.Base( PaintChannel.Opacity ), 1e-6f, "unpainted opacity" );
		} );

		Check( "layer opacity scales the coverage", () =>
		{
			var paint = new FacePaint( new FaceMaterial( "brick/brickwall001" ), new PaintLayerStack() );
			paint.Stack.Add( PaintLayer.Solid( "red", 8, 255, 0, 0, null, PaintBlendMode.Normal, 0.5f ) );
			var composite = paint.Composite();
			Require( composite is not null, "half-opacity stack must composite" );
			var (r, g, b, a) = PaintOnMap.SampleColor( composite, 0.5f, 0.5f );
			Require( r == 255 && g == 0 && b == 0, $"half-opacity colour: ({r},{g},{b})" );
			Require( a == 128, $"half-opacity alpha: {a}" );
			Near( PaintOnMap.CoverageAt( composite, 0.5f, 0.5f ), 128f / 255f, 1e-6f, "half-opacity coverage" );
		} );

		Check( "layer mask scales the coverage per texel", () =>
		{
			var mask = new float[16];
			for ( var i = 0; i < 8; i++ ) mask[i] = 1f;
			var paint = new FacePaint( new FaceMaterial( "brick/brickwall001" ), new PaintLayerStack() );
			paint.Stack.Add( PaintLayer.Solid( "red", 4, 255, 0, 0, mask ) );
			var composite = paint.Composite();
			Require( composite is not null, "masked stack must composite" );
			Near( PaintOnMap.CoverageAt( composite, 0.125f, 0.125f ), 1f, 1e-6f, "masked-in texel" );
			Near( PaintOnMap.CoverageAt( composite, 0.125f, 0.625f ), 0f, 1e-6f, "masked-out texel" );
		} );

		Check( "sampling wraps UVs outside 0..1, including negatives", () =>
		{
			var canvas = new PaintCanvas( 2, 2 );
			void Texel( int x, int y, byte r, byte g, byte b )
			{
				var i = ( y * 2 + x ) * 4;
				canvas.Rgba[i] = r; canvas.Rgba[i + 1] = g; canvas.Rgba[i + 2] = b; canvas.Rgba[i + 3] = 255;
			}
			Texel( 0, 0, 255, 0, 0 );
			Texel( 1, 0, 0, 255, 0 );
			Texel( 0, 1, 0, 0, 255 );
			Texel( 1, 1, 255, 255, 255 );
			var stack = new PaintLayerStack();
			stack.Add( new PaintLayer( "quad", new PaintLayerMaps { Size = 2, Color = canvas } ) );
			var composite = stack.Composite();
			Require( composite is not null, "quad stack must composite" );
			var (rr, rg, rb, ra) = PaintOnMap.SampleColor( composite, 0.25f, 0.25f );
			Require( rr == 255 && rg == 0 && rb == 0 && ra == 255, $"centre red: ({rr},{rg},{rb},{ra})" );
			var (gr, gg, gb, ga) = PaintOnMap.SampleColor( composite, 1.75f, 0.25f );
			Require( gr == 0 && gg == 255 && gb == 0 && ga == 255, $"u=1.75 wraps to green: ({gr},{gg},{gb},{ga})" );
			var (nr, ng, nb, na) = PaintOnMap.SampleColor( composite, -0.25f, 0.25f );
			Require( nr == 0 && ng == 255 && nb == 0 && na == 255, $"u=-0.25 wraps to green: ({nr},{ng},{nb},{na})" );
			var (br, bg, bb, ba) = PaintOnMap.SampleColor( composite, 0.25f, 1.75f );
			Require( br == 0 && bg == 0 && bb == 255 && ba == 255, $"v=1.75 wraps to blue: ({br},{bg},{bb},{ba})" );
			var (xr, xg, xb, xa) = PaintOnMap.SampleColor( composite, 0.25f, -0.75f );
			Require( xr == 255 && xg == 0 && xb == 0 && xa == 255, $"v=-0.75 wraps to red: ({xr},{xg},{xb},{xa})" );
		} );

		Check( "displacement alpha from paint layers matches the sculpt-mode blend exactly", () =>
		{
			var painted = Displacement.Create( QuadFace(), 2 );
			var sculpted = Displacement.Create( QuadFace(), 2 );
			var dab = new BrushSample( new Vec3( 32f, 32f, 0f ), new Vec3( 0f, 0f, 1f ), 100f, 0.5f );
			DisplacementSculpt.ApplyDab( sculpted, dab, DisplacementSculptMode.PaintAlpha,
				BrushFalloff.Constant, null, new DisplacementSculptOptions { PaintAlphaTarget = 1f } );
			var stack = new PaintLayerStack();
			stack.Add( PaintLayer.Solid( "red", 8, 255, 0, 0 ) );
			var result = PaintOnMap.ApplyPaintAlpha( painted, stack, 0.5f );
			Require( result.VerticesTouched == 25, $"touched: {result.VerticesTouched}" );
			Near( result.MaxAlphaDelta, 0.5f, 1e-6f, "max alpha delta" );
			for ( var i = 0; i < painted.Alphas.Length; i++ )
			{
				Near( sculpted.Alphas[i], 0.5f, 1e-6f, $"sculpted alpha[{i}]" );
				Near( painted.Alphas[i], sculpted.Alphas[i], 1e-6f, $"paint alpha[{i}] vs sculpt" );
			}
			Near( painted.GetOffset( 2, 2 ), 0f, 1e-6f, "paint alpha must not move offsets" );
		} );

		Check( "painting twice converges like repeated sculpt dabs", () =>
		{
			var painted = Displacement.Create( QuadFace(), 2 );
			var sculpted = Displacement.Create( QuadFace(), 2 );
			var dab = new BrushSample( new Vec3( 32f, 32f, 0f ), new Vec3( 0f, 0f, 1f ), 100f, 0.5f );
			var options = new DisplacementSculptOptions { PaintAlphaTarget = 1f };
			var stack = new PaintLayerStack();
			stack.Add( PaintLayer.Solid( "red", 8, 255, 0, 0 ) );
			for ( var pass = 0; pass < 2; pass++ )
			{
				DisplacementSculpt.ApplyDab( sculpted, dab, DisplacementSculptMode.PaintAlpha,
					BrushFalloff.Constant, null, options );
				PaintOnMap.ApplyPaintAlpha( painted, stack, 0.5f );
			}
			for ( var i = 0; i < painted.Alphas.Length; i++ )
			{
				Near( sculpted.Alphas[i], 0.75f, 1e-6f, $"sculpted alpha[{i}] after two dabs" );
				Near( painted.Alphas[i], sculpted.Alphas[i], 1e-6f, $"paint alpha[{i}] after two passes" );
			}
		} );

		Check( "paint alpha follows the layer coverage, not a uniform target", () =>
		{
			// Face mapped to 0..1 UVs (1/64 scale). U follows the grid ROW here, so a
			// mask that paints texel columns x<2 paints rows 0, 1 and 4 (row 4 is U=1,
			// which wraps to the painted texel — the tiling edge, not a bug).
			var disp = Displacement.Create( QuadFace( new Vec2( 1f / 64f, 1f / 64f ) ), 2 );
			var mask = new float[16];
			for ( var y = 0; y < 4; y++ )
				for ( var x = 0; x < 4; x++ )
					mask[y * 4 + x] = x < 2 ? 1f : 0f;
			var stack = new PaintLayerStack();
			stack.Add( PaintLayer.Solid( "red", 4, 255, 0, 0, mask ) );
			var result = PaintOnMap.ApplyPaintAlpha( disp, stack, 1f );
			Require( result.VerticesTouched == 15, $"touched (3 of 5 rows x 5 columns): {result.VerticesTouched}" );
			Near( result.MaxAlphaDelta, 1f, 1e-6f, "max alpha delta" );
			Near( disp.GetAlpha( 0, 2 ), 1f, 1e-6f, "row 0 paints in" );
			Near( disp.GetAlpha( 1, 2 ), 1f, 1e-6f, "row 1 paints in" );
			Near( disp.GetAlpha( 2, 2 ), 0f, 1e-6f, "row 2 stays out" );
			Near( disp.GetAlpha( 3, 2 ), 0f, 1e-6f, "row 3 stays out" );
			Near( disp.GetAlpha( 4, 2 ), 1f, 1e-6f, "row 4 wraps to the painted texel (tiling edge)" );
		} );

		Check( "layer stack round-trips through JSON, groups included, composites byte-equal", () =>
		{
			var stack = new PaintLayerStack();
			var red = PaintLayer.Solid( "red", 4, 255, 0, 0 );
			red.SetChannel( PaintChannel.Roughness, PaintLayers.Solid( 4, 4, 128, 128, 128 ) );
			red.SetChannelOpacity( PaintChannel.Roughness, 0.5f );
			stack.Add( red );
			stack.Add( PaintLayer.Solid( "blue", 4, 0, 0, 255, null, PaintBlendMode.Multiply, 0.5f ) );
			stack.Add( PaintLayer.Group( "greens",
				new List<PaintLayer> { PaintLayer.Solid( "green", 4, 0, 255, 0 ) } ) );
			var paint = new FacePaint( new FaceMaterial( "brick/brickwall001" ), stack, "grass/grass001" );
			var before = paint.Composite();
			Require( before is not null, "stack must composite before serialisation" );
			var loaded = FacePaint.FromJsonString( paint.ToJsonString() );
			Require( loaded.Base.Equals( paint.Base ), "base material did not round-trip" );
			Require( loaded.SecondMaterial == "grass/grass001", $"second material: {loaded.SecondMaterial}" );
			Require( loaded.Stack.Count == 3, $"layer count: {loaded.Stack.Count}" );
			Require( loaded.Stack[1].Blend == PaintBlendMode.Multiply, $"blue blend: {loaded.Stack[1].Blend}" );
			Near( loaded.Stack[1].Opacity, 0.5f, 1e-6f, "blue opacity" );
			Require( loaded.Stack[2].IsGroup, "group did not round-trip as a group" );
			Require( loaded.Stack[2].Children is not null && loaded.Stack[2].Children.Count == 1
				&& loaded.Stack[2].Children[0].Name == "green", "group child did not round-trip" );
			Near( loaded.Stack[0].OpacityFor( PaintChannel.Roughness ), 0.5f, 1e-6f, "per-channel opacity" );
			var after = loaded.Composite();
			Require( after is not null && before.SameAs( after ), "composite changed across the round-trip" );
			Require( paint.Equals( loaded ), "FacePaint equality must hold across the round-trip" );
		} );

		Check( "fixture face-paint.json loads and composites red", () =>
		{
			var paint = FacePaint.FromJsonString( File.ReadAllText( FindFixture() ) );
			Require( paint.Base.MaterialName == "brick/brickwall001", $"fixture base: {paint.Base.MaterialName}" );
			Require( paint.SecondMaterial == "grass/grass001", $"fixture second: {paint.SecondMaterial}" );
			Require( paint.Stack.Count == 1, $"fixture layers: {paint.Stack.Count}" );
			var composite = paint.Composite();
			Require( composite is not null, "fixture stack must composite" );
			var (r, g, b, a) = PaintOnMap.SampleColor( composite, 0.5f, 0.5f );
			Require( r == 255 && g == 0 && b == 0 && a == 255, $"fixture centre texel: ({r},{g},{b},{a})" );
		} );

		Check( "validation names defects and bad inputs throw", () =>
		{
			var stack = new PaintLayerStack();
			Throws<ArgumentNullException>( () => new FacePaint( null!, stack ), "null base" );
			Throws<ArgumentNullException>( () => new FacePaint( new FaceMaterial( "brick/brickwall001" ), null! ), "null stack" );
			Throws<ArgumentException>( () => new FacePaint( new FaceMaterial( "brick/brickwall001" ), stack, "  " ), "blank second material" );
			Throws<ArgumentNullException>( () => PaintOnMap.ForBrushFace( null!, 0 ), "null brush" );
			var brush = MapBrush.Box( new Vec3( 0f, 0f, 0f ), new Vec3( 64f, 64f, 64f ), "brick/brickwall001" );
			Throws<ArgumentOutOfRangeException>( () => PaintOnMap.ForBrushFace( brush, 6 ), "face index past the end" );
			Throws<ArgumentOutOfRangeException>( () => PaintOnMap.ForBrushFace( brush, -1 ), "negative face index" );
			var disp = Displacement.Create( QuadFace(), 2 );
			Throws<ArgumentOutOfRangeException>( () => PaintOnMap.ApplyPaintAlpha( disp, stack, 2f ), "strength above 1" );
			Throws<ArgumentOutOfRangeException>( () => PaintOnMap.ApplyPaintAlpha( disp, stack, -0.1f ), "strength below 0" );
			Throws<ArgumentNullException>( () => PaintOnMap.SampleColor( null!, 0.5f, 0.5f ), "null canvas sample" );
			Throws<ArgumentException>( () => PaintOnMap.SampleColor( new PaintCanvas( 4, 4 ), float.NaN, 0f ), "NaN UV sample" );

			var badMask = new PaintLayer( "bad-mask",
				new PaintLayerMaps { Size = 4, Color = PaintLayers.Solid( 4, 4, 255, 0, 0 ) },
				PaintBlendMode.Normal, 1f, new float[5] );
			var badOpacity = new PaintLayer( "bad-opacity",
				new PaintLayerMaps { Size = 4, Color = PaintLayers.Solid( 4, 4, 255, 0, 0 ) },
				PaintBlendMode.Normal, 2f );
			var defects = new FacePaint( new FaceMaterial( "brick/brickwall001" ),
				new PaintLayerStack() ).Validate();
			Require( defects.Count == 0, $"a clean face must validate clean: {string.Join( ", ", defects )}" );
			var bad = new FacePaint( new FaceMaterial( "brick/brickwall001" ),
				new PaintLayerStack { Layers = new List<PaintLayer> { badMask, badOpacity } } );
			var badDefects = bad.Validate().ToList();
			Require( badDefects.Contains( "paint-layer-mask-length:bad-mask" ),
				$"mask defect missing: {string.Join( ", ", badDefects )}" );
			Require( badDefects.Contains( "paint-layer-opacity:bad-opacity" ),
				$"opacity defect missing: {string.Join( ", ", badDefects )}" );

			Throws<ArgumentException>( () => PaintOnMap.LayerFromJson( JsonNode.Parse(
				"""{"name":"x","blend":"NoSuchMode","opacity":1,"enabled":true,"channels":{}}""" )!.AsObject() ),
				"unknown blend mode in JSON" );
			Throws<ArgumentException>( () => PaintOnMap.CanvasFromJson( JsonNode.Parse(
				"""{"width":2,"height":2,"rgba":"AAAAAA=="}""" )!.AsObject() ),
				"canvas base64 short of width*height*4" );
		} );

		Check( "empty stack composites to nothing and changes no alpha", () =>
		{
			var paint = new FacePaint( new FaceMaterial( "brick/brickwall001" ), new PaintLayerStack() );
			Require( paint.Composite() is null, "an empty stack must composite to null, not a blank canvas" );
			Near( PaintOnMap.CoverageAt( null!, 0.5f, 0.5f ), 0f, 1e-6f, "null composite coverage" );
			Near( PaintOnMap.MaterialAt( paint, 0.5f, 0.5f ).Factor, 0f, 1e-6f, "empty stack factor" );
			var disp = Displacement.Create( QuadFace(), 2 );
			var result = PaintOnMap.ApplyPaintAlpha( disp, paint );
			Require( result.VerticesTouched == 0, $"empty stack touched: {result.VerticesTouched}" );
			Near( result.MaxAlphaDelta, 0f, 1e-6f, "empty stack delta" );
			Require( disp.Alphas.All( a => a == 0f ), "empty stack moved an alpha" );
		} );
	}
}
