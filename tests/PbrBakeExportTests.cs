using System;
using System.Collections.Generic;
using Marionette.Desktop;

// PBR layer stack + integrated baker + Unreal ORM export (godlike-pbr-layer-stack-
// baker-unreal-orm, GODLIKE WAVE 2 P1): the INTEGRATION of the three landed pieces —
// UnifiedLayerStack composite, BakeJob/BakeSet seeding, UnrealOrmPreset.Build. Checks
// the pipeline end to end on synthetic 4x4 maps: bake seeding (straight row copy,
// green flip, slot placement), layer composite (reference values, live reorder), ORM packing
// (each channel in the right slot, baked AO wins), DirectX passthrough, VRAM warning,
// failed-bake refusal, non-destructive inputs. NOT yet registered in
// tests/TestRegistry.cs (coordinator-owned); the final integrator adds the one Register
// line. Implementation done is NOT runtime verified: no full build or app launch was run
// for this ticket (PO workflow override).
internal static class PbrBakeExportTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "pbr-bake-export", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( int actual, int expected, int tolerance, string what )
		{
			if ( Math.Abs( actual - expected ) > tolerance ) throw new Exception( what + ": expected " + expected + " ±" + tolerance + ", got " + actual );
		}
		void Throws<T>( Action f, string what ) where T : Exception
		{
			try { f(); }
			catch ( T ) { return; }
			catch ( Exception e ) { throw new Exception( what + ": threw " + e.GetType().Name + ", not " + typeof( T ).Name ); }
			throw new Exception( what + ": did not throw" );
		}

		(byte R, byte G, byte B, byte A) Texel( byte[] rgba, int w, int x, int y )
		{
			var i = (y * w + x) * 4;
			return (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
		}
		Effigy.BakedMap FillMap( int w, int h, byte r, byte g, byte b )
		{
			var map = new Effigy.BakedMap( w, h );
			for ( var i = 0; i < w * h; i++ ) { map.Rgb[i * 3] = r; map.Rgb[i * 3 + 1] = g; map.Rgb[i * 3 + 2] = b; }
			return map;
		}
		Effigy.BakedMap RowMap( int w, int h, byte bottom, byte top )
		{
			// Bake row 0 (v = 0) is the TOP; bake row h-1 is the bottom.
			var map = new Effigy.BakedMap( w, h );
			for ( var x = 0; x < w; x++ )
			{
				map.Rgb[x * 3] = top; map.Rgb[x * 3 + 1] = top; map.Rgb[x * 3 + 2] = top;
				var t = ((h - 1) * w + x) * 3;
				map.Rgb[t] = bottom; map.Rgb[t + 1] = bottom; map.Rgb[t + 2] = bottom;
			}
			return map;
		}
		Effigy.PaintCanvas GreyCanvas( int size, byte grey )
		{
			var canvas = new Effigy.PaintCanvas( size, size );
			for ( var i = 0; i < size * size; i++ ) { canvas.Rgba[i * 4] = grey; canvas.Rgba[i * 4 + 1] = grey; canvas.Rgba[i * 4 + 2] = grey; canvas.Rgba[i * 4 + 3] = 255; }
			return canvas;
		}
		Effigy.PaintCanvas ColourCanvas( int size, byte r, byte g, byte b )
		{
			var canvas = new Effigy.PaintCanvas( size, size );
			for ( var i = 0; i < size * size; i++ ) { canvas.Rgba[i * 4] = r; canvas.Rgba[i * 4 + 1] = g; canvas.Rgba[i * 4 + 2] = b; canvas.Rgba[i * 4 + 3] = 255; }
			return canvas;
		}
		Effigy.PaintLayer Layer( string name, Effigy.PaintLayerMaps maps, float opacity = 1f ) =>
			new( name, maps, Effigy.PaintBlendMode.Normal, opacity );
		Effigy.PaintLayerMaps Maps( int size, Effigy.PaintCanvas color = null, Effigy.PaintCanvas rough = null, Effigy.PaintCanvas metal = null ) =>
			new() { Size = size, Color = color, Roughness = rough, Metalness = metal };
		Effigy.BakeJobResult Baked( Effigy.BakeSet set, double missRate = 0 ) => new() { Set = set, MissRate = missRate };
		Effigy.PaintTextureSet.Image FindImage( List<Effigy.PaintTextureSet.Image> images, string suffix )
		{
			foreach ( var image in images )
				if ( image.Suffix == suffix )
					return image;
			throw new Exception( "no exported image named '" + suffix + "'" );
		}

		Check( "ToCanvas copies rows straight: bake row 0 (v=0, top) lands at canvas row 0 (top)", () =>
		{
			var map = RowMap( 4, 4, bottom: 0, top: 255 );
			var canvas = PbrBakeExport.ToCanvas( map );
			var top = Texel( canvas.Rgba, 4, 0, 0 );
			var bottom = Texel( canvas.Rgba, 4, 0, 3 );
			Require( top.R == 255 && top.G == 255 && top.B == 255 && top.A == 255, "canvas row 0 is the bake's row 0 (white, top)" );
			Require( bottom.R == 0 && bottom.G == 0 && bottom.B == 0 && bottom.A == 255, "canvas bottom row is the bake's bottom row (black)" );
			Require( canvas.Width == 4 && canvas.Height == 4, "size kept" );
		} );

		Check( "ToCanvas refuses null", () =>
		{
			Throws<ArgumentNullException>( () => PbrBakeExport.ToCanvas( null ), "null map refused" );
		} );

		Check( "SeedBaseCanvas puts AO, normal and height in the right slots", () =>
		{
			var set = new Effigy.BakeSet
			{
				AmbientOcclusion = FillMap( 4, 4, 10, 10, 10 ),
				Normal = FillMap( 4, 4, 128, 200, 255 ),
				Height = FillMap( 4, 4, 77, 77, 77 ),
			};
			// DirectX source: no green flip, so the seeded normal keeps G = 200.
			var canvas = PbrBakeExport.SeedBaseCanvas( set, Marionette.Desktop.NormalMapConvention.DirectX );
			var ao = Texel( canvas.AmbientOcclusionMap.Rgba, 4, 0, 0 );
			Require( ao.R == 10 && ao.A == 255, "AO seeded" );
			var normal = Texel( canvas.NormalMap.Rgba, 4, 0, 0 );
			Require( normal.R == 128 && normal.G == 200 && normal.B == 255, "normal seeded" );
			var height = Texel( canvas.HeightMap.Rgba, 4, 0, 0 );
			Require( height.R == 77, "height seeded" );
			var colour = Texel( canvas.Rgba, 4, 0, 0 );
			Require( colour.A == 255, "base colour is opaque" );
			var rough = Texel( canvas.RoughnessMap.Rgba, 4, 0, 0 );
			Require( rough.R == 153, "roughness base is 0.6 (153)" );
			var metal = Texel( canvas.MetalnessMap.Rgba, 4, 0, 0 );
			Require( metal.R == 0, "metalness base is 0" );
		} );

		Check( "SeedBaseCanvas with no maps is refused, and mismatched sizes are refused", () =>
		{
			Throws<ArgumentException>( () => PbrBakeExport.SeedBaseCanvas( new Effigy.BakeSet() ), "empty bake set refused" );
			var set = new Effigy.BakeSet
			{
				AmbientOcclusion = FillMap( 4, 4, 10, 10, 10 ),
				Normal = FillMap( 8, 8, 128, 128, 255 ),
			};
			Throws<ArgumentException>( () => PbrBakeExport.SeedBaseCanvas( set ), "4x4 AO + 8x8 normal refused" );
		} );

		Check( "SeedBaseCanvas flips OpenGL green to DirectX, passes DirectX through", () =>
		{
			var set = new Effigy.BakeSet { Normal = FillMap( 4, 4, 128, 200, 255 ) };
			var fromOpenGl = PbrBakeExport.SeedBaseCanvas( set, Marionette.Desktop.NormalMapConvention.OpenGL );
			Require( Texel( fromOpenGl.NormalMap.Rgba, 4, 0, 0 ).G == 55, "OpenGL G=200 flips to DirectX G=55" );
			var fromDirectX = PbrBakeExport.SeedBaseCanvas( set, Marionette.Desktop.NormalMapConvention.DirectX );
			Require( Texel( fromDirectX.NormalMap.Rgba, 4, 0, 0 ).G == 200, "DirectX G=200 passes through" );
		} );

		Check( "CurvatureMask returns the grey curvature canvas, null when absent", () =>
		{
			var with = new Effigy.BakeSet { Curvature = FillMap( 4, 4, 200, 200, 200 ) };
			var mask = PbrBakeExport.CurvatureMask( with );
			Require( mask is not null && Texel( mask.Rgba, 4, 0, 0 ).R == 200, "curvature mask carried" );
			Require( PbrBakeExport.CurvatureMask( new Effigy.BakeSet() ) is null, "no curvature -> null" );
		} );

		Check( "full pipeline: ORM packs baked AO / painted rough / painted metal in the right slots", () =>
		{
			var set = new Effigy.BakeSet
			{
				AmbientOcclusion = FillMap( 4, 4, 10, 10, 10 ),
				Normal = FillMap( 4, 4, 128, 128, 255 ),
			};
			var stack = new Effigy.UnifiedLayerStack();
			stack.AddPaint( Layer( "colour", Maps( 4, color: ColourCanvas( 4, 255, 0, 0 ) ) ) );
			stack.AddPaint( Layer( "pbr", Maps( 4, rough: GreyCanvas( 4, 100 ), metal: GreyCanvas( 4, 200 ) ) ) );
			var result = PbrBakeExport.Export( Baked( set ), stack, "Hero", Marionette.Desktop.NormalMapConvention.DirectX, size: 4 );
			Require( result.Size == 4, "size carried" );
			Require( result.PaintLayersApplied == 2, "two paint layers applied" );
			Require( result.BakedMapsUsed.Count == 2 && result.BakedMapsUsed[0] == "ao" && result.BakedMapsUsed[1] == "normal", "used maps listed" );
			Require( result.Warning is null, "no warning at 4K working size" );
			var orm = FindImage( result.Export.Images, "Hero_ORM" );
			var texel = Texel( orm.Rgba, 4, 0, 0 );
			Require( texel.R == 10, "ORM R is baked AO (10)" );
			Require( texel.G == 100, "ORM G is painted roughness (100)" );
			Require( texel.B == 200, "ORM B is painted metallic (200)" );
			Require( texel.A == 255, "ORM A is 255" );
			var baseColor = FindImage( result.Export.Images, "Hero_BaseColor" );
			var bc = Texel( baseColor.Rgba, 4, 0, 0 );
			Require( bc.R == 255 && bc.G == 0 && bc.B == 0 && bc.A == 255, "BaseColor is the painted red" );
			Require( FindImage( result.Export.Images, "Hero_Normal" ) is not null, "Normal image present" );
		} );

		Check( "baked AO beats height-derived AO in the ORM", () =>
		{
			var set = new Effigy.BakeSet
			{
				AmbientOcclusion = FillMap( 4, 4, 10, 10, 10 ),
				Height = FillMap( 4, 4, 128, 128, 128 ),
			};
			var result = PbrBakeExport.Export( Baked( set ), null, "Hero", Marionette.Desktop.NormalMapConvention.DirectX, size: 4 );
			var orm = FindImage( result.Export.Images, "Hero_ORM" );
			Require( Texel( orm.Rgba, 4, 0, 0 ).R == 10, "ORM R is baked AO (10), not height-derived AO" );
		} );

		Check( "DirectX normal passes through the preset unchanged", () =>
		{
			var baseCanvas = new Effigy.PaintCanvas( 4, 4 );
			baseCanvas.NormalMap = ColourCanvas( 4, 128, 200, 255 );
			var result = PbrBakeExport.ExportFromCanvas( baseCanvas, null, "Hero", size: 4 );
			var normal = FindImage( result.Export.Images, "Hero_Normal" );
			var texel = Texel( normal.Rgba, 4, 0, 0 );
			Require( texel.R == 128 && texel.G == 200 && texel.B == 255, "DirectX normal unchanged through the preset" );
		} );

		Check( "layer composite matches the reference: opaque layer wins, half opacity blends", () =>
		{
			var stack = new Effigy.UnifiedLayerStack();
			stack.AddPaint( Layer( "red", Maps( 4, color: ColourCanvas( 4, 255, 0, 0 ) ), opacity: 1f ) );
			var opaque = PbrBakeExport.ExportFromCanvas( PbrBakeExport.SeedBaseCanvas( new Effigy.BakeSet { Height = FillMap( 4, 4, 128, 128, 128 ) } ), stack, "Hero", size: 4 );
			var bc = FindImage( opaque.Export.Images, "Hero_BaseColor" );
			var t = Texel( bc.Rgba, 4, 0, 0 );
			Require( t.R == 255 && t.G == 0 && t.B == 0, "opaque red layer is the BaseColor" );

			var stack2 = new Effigy.UnifiedLayerStack();
			stack2.AddPaint( Layer( "red-half", Maps( 4, color: ColourCanvas( 4, 255, 0, 0 ) ), opacity: 0.5f ) );
			var baseCanvas = PbrBakeExport.SeedBaseCanvas( new Effigy.BakeSet { Height = FillMap( 4, 4, 128, 128, 128 ) } );
			var baseTexel = Texel( baseCanvas.Rgba, 4, 0, 0 );
			var half = PbrBakeExport.ExportFromCanvas( baseCanvas, stack2, "Hero", size: 4 );
			var ht = Texel( FindImage( half.Export.Images, "Hero_BaseColor" ).Rgba, 4, 0, 0 );
			Near( ht.R, (255 + baseTexel.R) / 2, 1, "half red over base, R" );
			Near( ht.G, (0 + baseTexel.G) / 2, 1, "half red over base, G" );
			Near( ht.B, (0 + baseTexel.B) / 2, 1, "half red over base, B" );
		} );

		Check( "live reorder changes the composite: the top layer wins", () =>
		{
			var stack = new Effigy.UnifiedLayerStack();
			stack.AddPaint( Layer( "red", Maps( 4, color: ColourCanvas( 4, 255, 0, 0 ) ) ) );
			stack.AddPaint( Layer( "blue", Maps( 4, color: ColourCanvas( 4, 0, 0, 255 ) ) ) );
			var baseCanvas = PbrBakeExport.SeedBaseCanvas( new Effigy.BakeSet { Height = FillMap( 4, 4, 128, 128, 128 ) } );
			var top = PbrBakeExport.ExportFromCanvas( baseCanvas, stack, "Hero", size: 4 );
			var tt = Texel( FindImage( top.Export.Images, "Hero_BaseColor" ).Rgba, 4, 0, 0 );
			Require( tt.B == 255 && tt.R == 0, "blue on top wins" );
			stack.Move( 1, 0 );
			var moved = PbrBakeExport.ExportFromCanvas( baseCanvas, stack, "Hero", size: 4 );
			var mt = Texel( FindImage( moved.Export.Images, "Hero_BaseColor" ).Rgba, 4, 0, 0 );
			Require( mt.R == 255 && mt.B == 0, "after reorder, red on top wins" );
		} );

		Check( "a failed or cancelled bake is refused, a bake with misses still exports", () =>
		{
			var failed = new Effigy.BakeJobResult { Set = new Effigy.BakeSet { Height = FillMap( 4, 4, 128, 128, 128 ) }, Error = "ray budget exceeded" };
			Throws<InvalidOperationException>( () => PbrBakeExport.Export( failed, null, "Hero", size: 4 ), "failed bake refused" );
			var cancelled = new Effigy.BakeJobResult { Set = new Effigy.BakeSet { Height = FillMap( 4, 4, 128, 128, 128 ) }, Cancelled = true };
			Throws<InvalidOperationException>( () => PbrBakeExport.Export( cancelled, null, "Hero", size: 4 ), "cancelled bake refused" );
			var missy = Baked( new Effigy.BakeSet { Height = FillMap( 4, 4, 128, 128, 128 ) }, missRate: 0.25 );
			var result = PbrBakeExport.Export( missy, null, "Hero", size: 4 );
			Require( Math.Abs( result.BakeMissRate - 0.25 ) < 1e-9, "miss rate rides on the result" );
		} );

		Check( "export is non-destructive: base canvas and stack come back unchanged", () =>
		{
			var set = new Effigy.BakeSet { AmbientOcclusion = FillMap( 4, 4, 10, 10, 10 ) };
			var baseCanvas = PbrBakeExport.SeedBaseCanvas( set );
			var before = (byte[])baseCanvas.Rgba.Clone();
			var stack = new Effigy.UnifiedLayerStack();
			var maps = Maps( 4, color: ColourCanvas( 4, 255, 0, 0 ) );
			stack.AddPaint( Layer( "red", maps ) );
			var first = PbrBakeExport.ExportFromCanvas( baseCanvas, stack, "Hero", size: 4 );
			for ( var i = 0; i < before.Length; i++ )
				Require( baseCanvas.Rgba[i] == before[i], "base canvas untouched at byte " + i );
			Require( stack.Count == 1 && stack.Entries[0].Paint.Maps == maps, "stack untouched" );
			var second = PbrBakeExport.ExportFromCanvas( baseCanvas, stack, "Hero", size: 4 );
			var a = FindImage( first.Export.Images, "Hero_ORM" ).Rgba;
			var b = FindImage( second.Export.Images, "Hero_ORM" ).Rgba;
			Require( a.Length == b.Length, "repeat export, same size" );
			for ( var i = 0; i < a.Length; i++ )
				Require( a[i] == b[i], "repeat export is byte-identical at " + i );
		} );

		Check( "ID map is carried for per-material-ID splitting, with its colours", () =>
		{
			var set = new Effigy.BakeSet
			{
				AmbientOcclusion = FillMap( 4, 4, 10, 10, 10 ),
				Normal = FillMap( 4, 4, 128, 128, 255 ),
				MaterialId = FillMap( 4, 4, 255, 0, 0 ),
				IdColors = new Dictionary<int, (byte R, byte G, byte B)> { [0] = (255, 0, 0), [1] = (0, 255, 0) },
			};
			var result = PbrBakeExport.Export( Baked( set ), null, "Hero", Marionette.Desktop.NormalMapConvention.DirectX, size: 4 );
			Require( result.BakedIdMap is not null, "ID map carried" );
			Require( Texel( result.BakedIdMap.Rgba, 4, 0, 0 ).R == 255, "ID colour carried" );
			Require( result.IdColors is not null && result.IdColors[1] == (0, 255, 0), "ID colours passed through" );
			Require( result.BakedMapsUsed.Contains( "id" ), "id listed in used maps" );
		} );

		Check( "VRAM warning: none at 4K, 8K needs 2.5 GB and never downscales", () =>
		{
			var baseCanvas = PbrBakeExport.SeedBaseCanvas( new Effigy.BakeSet { Height = FillMap( 4, 4, 128, 128, 128 ) } );
			var ok = PbrBakeExport.ExportFromCanvas( baseCanvas, null, "Hero", size: 4096, availableVramMb: 1000 );
			Require( ok.Warning is null, "4K working size needs no warning even with 1 GB" );
			var warn = Effigy.UnrealOrmPreset.VramWarning( 8192, 1000 );
			Require( warn is not null && warn.Contains( "2.5 GB" ), "8K with 1 GB warns about 2.5 GB" );
			Require( Effigy.UnrealOrmPreset.VramWarning( 8192, 3000 ) is null, "8K with 3 GB needs no warning" );
			Require( Effigy.UnrealOrmPreset.VramWarning( 8192, null ) is null, "8K with unknown VRAM needs no warning" );
		} );

		Check( "export size tops out at 8K and the asset name is required", () =>
		{
			var baseCanvas = PbrBakeExport.SeedBaseCanvas( new Effigy.BakeSet { Height = FillMap( 4, 4, 128, 128, 128 ) } );
			Throws<ArgumentOutOfRangeException>( () => PbrBakeExport.ExportFromCanvas( baseCanvas, null, "Hero", size: 8193 ), "8193 refused" );
			Throws<ArgumentException>( () => PbrBakeExport.ExportFromCanvas( baseCanvas, null, "  ", size: 4 ), "blank asset refused" );
			Throws<ArgumentNullException>( () => PbrBakeExport.ExportFromCanvas( null, null, "Hero", size: 4 ), "null canvas refused" );
		} );
	}
}
