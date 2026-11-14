using System;
using System.Collections.Generic;
using Marionette.Desktop;

// Ported emissive / opacity / normal paint channels (Desktop ticket
// port-missing-emissive-opacity-and-normal-paint-channels): channel enums, map
// storage, compositing, texture-set export and the colour adjustment. NOT yet
// registered in tests/TestRegistry.cs (coordinator-owned); the final integrator
// adds the one Register line. Implementation done is NOT runtime verified:
// no full build or app launch was run for this ticket (PO workflow override).
internal static class PaintChannelsTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "paint-channels", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( byte actual, int expected, string what )
		{
			if ( Math.Abs( actual - expected ) > 1 ) throw new Exception( what + ": expected " + expected + " ±1, got " + actual );
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
		Effigy.PaintCanvas Grey( int w, int h, byte v, byte a = 255 ) => Solid( w, h, v, v, v, a );
		(byte R, byte G, byte B, byte A) Texel( Effigy.PaintCanvas c, int x = 0, int y = 0 )
		{
			var i = (y * c.Width + x) * 4;
			return (c.Rgba[i], c.Rgba[i + 1], c.Rgba[i + 2], c.Rgba[i + 3]);
		}
		Effigy.PaintTextureSet.Image Find( List<Effigy.PaintTextureSet.Image> images, string suffix )
		{
			foreach ( var image in images )
				if ( image.Suffix == suffix )
					return image;
			throw new Exception( "no '" + suffix + "' image exported" );
		}

		Check( "surface lists emissive opacity normal after height", () =>
		{
			var surface = Effigy.PaintChannels.Surface;
			Require( Array.IndexOf( surface, Effigy.PaintChannel.Color ) == 0, "colour first" );
			Require( Array.IndexOf( surface, Effigy.PaintChannel.Height ) == 3, "height fourth" );
			Require( Array.IndexOf( surface, Effigy.PaintChannel.Emissive ) == 4, "emissive fifth" );
			Require( Array.IndexOf( surface, Effigy.PaintChannel.Opacity ) == 5, "opacity sixth" );
			Require( Array.IndexOf( surface, Effigy.PaintChannel.Normal ) == 6, "normal seventh" );
		} );

		Check( "rgb channels are colour and normal only", () =>
		{
			Require( Effigy.PaintChannels.IsRgb( Effigy.PaintChannel.Color ), "colour is rgb" );
			Require( Effigy.PaintChannels.IsRgb( Effigy.PaintChannel.Normal ), "normal is rgb" );
			Require( !Effigy.PaintChannels.IsRgb( Effigy.PaintChannel.Emissive ), "emissive is grey" );
			Require( !Effigy.PaintChannels.IsRgb( Effigy.PaintChannel.Opacity ), "opacity is grey" );
			Require( !Effigy.PaintChannels.IsRgb( Effigy.PaintChannel.Roughness ), "roughness is grey" );
		} );

		Check( "labels tags and bases round-trip", () =>
		{
			Require( Effigy.PaintChannels.Label( Effigy.PaintChannel.Emissive ) == "Emissive", "emissive label" );
			Require( Effigy.PaintChannels.Label( Effigy.PaintChannel.Opacity ) == "Opacity", "opacity label" );
			Require( Effigy.PaintChannels.Label( Effigy.PaintChannel.Normal ) == "Normal", "normal label" );
			Require( Effigy.PaintChannels.Tag( Effigy.PaintChannel.Emissive ) == "emit", "emit tag" );
			Require( Effigy.PaintChannels.Tag( Effigy.PaintChannel.Opacity ) == "opacity", "opacity tag" );
			Require( Effigy.PaintChannels.Tag( Effigy.PaintChannel.Normal ) == "normal", "normal tag" );
			Require( Effigy.PaintChannels.FromTag( "emit" ) == Effigy.PaintChannel.Emissive, "emit untags" );
			Require( Effigy.PaintChannels.FromTag( "opacity" ) == Effigy.PaintChannel.Opacity, "opacity untags" );
			Require( Effigy.PaintChannels.FromTag( "normal" ) == Effigy.PaintChannel.Normal, "normal untags" );
			Require( Effigy.PaintChannels.Base( Effigy.PaintChannel.Emissive ) == 0f, "emissive base is dark" );
			Require( Effigy.PaintChannels.Base( Effigy.PaintChannel.Opacity ) == 1f, "opacity base is solid" );
			Require( Effigy.PaintChannels.Base( Effigy.PaintChannel.Normal ) == 0.5f, "normal base is half" );
			Require( Effigy.PaintChannels.FlatNormalR == 128 && Effigy.PaintChannels.FlatNormalG == 128
				&& Effigy.PaintChannels.FlatNormalB == 255, "flat normal is (128,128,255)" );
		} );

		Check( "layer maps carry the new channels", () =>
		{
			var maps = new Effigy.PaintLayerMaps { Size = 4 };
			Require( !maps.HasSurface, "empty maps have no surface" );
			maps.Set( Effigy.PaintChannel.Emissive, Grey( 4, 4, 200 ) );
			Require( maps.HasSurface, "emissive alone is surface content" );
			Require( ReferenceEquals( maps.Get( Effigy.PaintChannel.Emissive ), maps.Emissive ), "emissive get/set" );
			maps.Set( Effigy.PaintChannel.Opacity, Grey( 4, 4, 64 ) );
			maps.Set( Effigy.PaintChannel.Normal, Solid( 4, 4, 128, 128, 255 ) );
			Require( ReferenceEquals( maps.Get( Effigy.PaintChannel.Opacity ), maps.Opacity ), "opacity get/set" );
			Require( ReferenceEquals( maps.Get( Effigy.PaintChannel.Normal ), maps.Normal ), "normal get/set" );
			var maskOnly = new Effigy.PaintLayerMaps { Size = 4, MaskPaint = Grey( 4, 4, 255 ) };
			Require( !maskOnly.HasSurface, "a mask alone adds nothing" );
		} );

		Check( "clone and equality cover the new maps", () =>
		{
			var canvas = Solid( 4, 4, 10, 20, 30 );
			canvas.EmissiveMap = Grey( 4, 4, 200 );
			canvas.OpacityMap = Grey( 4, 4, 64 );
			canvas.NormalMap = Solid( 4, 4, 128, 128, 255 );
			canvas.AmbientOcclusionMap = Grey( 4, 4, 77 );
			var copy = canvas.Clone();
			Require( copy.SameAs( canvas ), "clone equals the original" );
			Require( !ReferenceEquals( copy.EmissiveMap, canvas.EmissiveMap ), "emissive is deep-copied" );
			Require( !ReferenceEquals( copy.AmbientOcclusionMap, canvas.AmbientOcclusionMap ), "ao is deep-copied" );
			copy.EmissiveMap.Rgba[0] = 201;
			Require( !copy.SameAs( canvas ), "an emissive edit breaks equality" );
			Require( Texel( canvas.EmissiveMap ).R == 200, "editing the clone leaves the original alone" );
			var noAo = canvas.Clone();
			noAo.AmbientOcclusionMap = null;
			Require( !noAo.SameAs( canvas ), "dropping the baked AO breaks equality" );
		} );

		Check( "composite carries emissive opacity normal and passes ao through", () =>
		{
			var below = Solid( 4, 4, 200, 200, 200 );
			below.AmbientOcclusionMap = Grey( 4, 4, 77 );
			var layer = new Effigy.PaintLayerMaps
			{
				Size = 4,
				Color = Solid( 4, 4, 10, 20, 30 ),
				Emissive = Grey( 4, 4, 128 ),
				Opacity = Grey( 4, 4, 64 ),
				Normal = Solid( 4, 4, 128, 128, 255 ),
			};
			var result = Effigy.PaintLayers.CompositeSet( below, layer, Effigy.PaintBlendMode.Normal, 1f, null );
			Require( result is not null, "composite exists" );
			Near( Texel( result ).R, 10, "colour composited" );
			Near( Texel( result.EmissiveMap ).R, 128, "emissive composited" );
			Near( Texel( result.OpacityMap ).R, 64, "opacity composited" );
			var n = Texel( result.NormalMap );
			Near( n.R, 128, "normal R" ); Near( n.G, 128, "normal G" ); Near( n.B, 255, "normal B" );
			Require( ReferenceEquals( result.AmbientOcclusionMap, below.AmbientOcclusionMap ),
				"baked AO passes through unchanged" );
		} );

		Check( "darken and lighten blend modes", () =>
		{
			var below = Solid( 4, 4, 200, 200, 200 );
			var layer = Solid( 4, 4, 100, 100, 100 );
			Near( Texel( Effigy.PaintLayers.Composite( below, layer, Effigy.PaintBlendMode.Darken, 1f, null ) ).R,
				100, "darken takes the darker texel" );
			Near( Texel( Effigy.PaintLayers.Composite( below, layer, Effigy.PaintBlendMode.Lighten, 1f, null ) ).R,
				200, "lighten takes the lighter texel" );
		} );

		Check( "texture set exports emit opacity and combined normal", () =>
		{
			var canvas = Solid( 4, 4, 200, 100, 50 );
			canvas.HeightMap = Grey( 4, 4, 128 );
			canvas.NormalMap = Solid( 4, 4, 128, 128, 255 );
			canvas.EmissiveMap = Grey( 4, 4, 255 );
			canvas.OpacityMap = Grey( 4, 4, 128 );
			var images = Effigy.PaintTextureSet.Images( canvas );
			var color = Find( images, "color" );
			Near( color.Rgba[0], 200, "colour R" ); Near( color.Rgba[1], 100, "colour G" ); Near( color.Rgba[2], 50, "colour B" );
			Near( Find( images, "emit" ).Rgba[0], 255, "emit is the mask value" );
			Near( Find( images, "opacity" ).Rgba[0], 128, "opacity is the grey value" );
			var normal = Find( images, "normal" );
			Near( normal.Rgba[0], 128, "combined normal R stays flat" );
			Near( normal.Rgba[1], 128, "combined normal G stays flat" );
			Near( normal.Rgba[2], 255, "combined normal B stays flat" );
			Require( Find( images, "ao" ).Rgba is not null, "height still yields an ao image" );
		} );

		Check( "combine normals flat over flat stays flat", () =>
		{
			var flat = new byte[2 * 2 * 4];
			for ( var i = 0; i < flat.Length; i += 4 )
			{
				flat[i] = 128; flat[i + 1] = 128; flat[i + 2] = 255; flat[i + 3] = 255;
			}
			var combined = Effigy.PaintTextureSet.CombineNormals( flat, 2, 2, flat, 2, 2 );
			Near( combined[0], 128, "combined R" ); Near( combined[1], 128, "combined G" );
			Near( combined[2], 255, "combined B" ); Near( combined[3], 255, "combined A" );
		} );

		Check( "adjust identity passes through and hue rotates", () =>
		{
			var identity = Effigy.PaintAdjust.Identity;
			Require( identity.IsIdentity, "identity reports itself" );
			var canvas = Solid( 4, 4, 255, 0, 0 );
			Require( ReferenceEquals( identity.Apply( canvas ), canvas ), "identity hands the canvas back" );
			var rotated = new Effigy.PaintAdjust( Hue: 120f );
			Require( !rotated.IsIdentity, "a hue shift is not identity" );
			var (r, g, b) = rotated.Apply( 255, 0, 0 );
			Near( r, 0, "rotated R" ); Near( g, 255, "rotated G" ); Near( b, 0, "rotated B" );
			var maps = new Effigy.PaintLayerMaps
			{
				Size = 4, Color = Solid( 4, 4, 255, 0, 0 ), Roughness = Grey( 4, 4, 200 ),
			};
			var adjusted = rotated.Apply( maps );
			Near( Texel( adjusted.Color ).G, 255, "adjustment recolours the colour map" );
			Require( ReferenceEquals( adjusted.Roughness, maps.Roughness ), "scalar channels are shared, not copied" );
		} );

		Check( "shade preview glows albedo times mask", () =>
		{
			var paint = Solid( 4, 4, 200, 100, 50 );
			paint.EmissiveMap = Grey( 4, 4, 128 );
			var shaded = Effigy.PaintTextureSet.Shade( paint );
			Require( shaded.EmissionMap is not null, "emission map exists when painted" );
			Near( shaded.EmissionMap[0], 100, "emission R is albedo times mask" );
			Near( shaded.EmissionMap[1], 50, "emission G is albedo times mask" );
			Near( shaded.EmissionMap[2], 25, "emission B is albedo times mask" );
			Require( shaded.EmissionMap[3] == 255, "emission alpha is opaque" );
			var plain = Effigy.PaintTextureSet.Shade( Solid( 4, 4, 200, 100, 50 ) );
			Require( plain.EmissionMap is null, "no emission map when nothing glows" );
		} );
	}
}
