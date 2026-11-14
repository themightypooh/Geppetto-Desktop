using System;
using System.Collections.Generic;
using Marionette.Desktop;

// Non-destructive PBR layer stack + integrated baker + Unreal ORM preset
// (Desktop godlike task godlike-pbr-layer-stack-baker-unreal-orm, Wave 2 P1):
// per-channel opacity/blend, groups, live reorder; bake AO/curvature/normal/
// ID from high-poly into the stack; Unreal packed ORM, DirectX normal, 4K
// working / 8K export with a VRAM warning instead of a silent downscale.
// NOT yet registered in tests/TestRegistry.cs (coordinator-owned); the final
// integrator adds the one Register line. Implementation done is NOT runtime
// verified: no full build or app launch was run for this ticket (PO workflow
// override).
internal static class PbrLayerStackTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "pbr-layer-stack", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( byte actual, int expected, string what )
		{
			if ( Math.Abs( actual - expected ) > 1 ) throw new Exception( what + ": expected " + expected + " ±1, got " + actual );
		}
		void InRange( byte actual, int lo, int hi, string what )
		{
			if ( actual < lo || actual > hi ) throw new Exception( what + ": expected " + lo + ".." + hi + ", got " + actual );
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

		Effigy.PaintCanvas Below( byte color, byte rough )
		{
			var c = Solid( 4, 4, color, color, color );
			c.RoughnessMap = Solid( 4, 4, rough, rough, rough );
			return c;
		}

		(byte R, byte G, byte B, byte A) Texel( Effigy.PaintCanvas c, int x = 0, int y = 0 )
		{
			var i = (y * c.Width + x) * 4;
			return (c.Rgba[i], c.Rgba[i + 1], c.Rgba[i + 2], c.Rgba[i + 3]);
		}

		Effigy.PolyMesh Quad( float z )
		{
			var m = new Effigy.PolyMesh();
			m.AddVertex( new Effigy.Vec3( 0, 0, z ) );
			m.AddVertex( new Effigy.Vec3( 1, 0, z ) );
			m.AddVertex( new Effigy.Vec3( 1, 1, z ) );
			m.AddVertex( new Effigy.Vec3( 0, 1, z ) );
			m.AddFace( new[] { 0, 1, 2 }, new[] { new Effigy.Vec2( 0, 0 ), new Effigy.Vec2( 1, 0 ), new Effigy.Vec2( 1, 1 ) } );
			m.AddFace( new[] { 0, 2, 3 }, new[] { new Effigy.Vec2( 0, 0 ), new Effigy.Vec2( 1, 1 ), new Effigy.Vec2( 0, 1 ) } );
			return m;
		}

		Effigy.PaintTextureSet.Image FindImage( List<Effigy.PaintTextureSet.Image> images, string suffix )
		{
			foreach ( var image in images )
				if ( image.Suffix == suffix )
					return image;
			throw new Exception( "no image '" + suffix + "'" );
		}

		Check( "per-channel opacity multiplies master", () =>
		{
			var layer = Effigy.PaintLayer.Solid( "l", 4, 255, 255, 255, blend: Effigy.PaintBlendMode.Normal, opacity: 0.5f );
			layer.SetChannel( Effigy.PaintChannel.Roughness, Solid( 4, 4, 200, 200, 200 ) );
			layer.SetChannelOpacity( Effigy.PaintChannel.Color, 0.5f );
			var stack = new Effigy.PaintLayerStack();
			stack.Add( layer );
			var result = stack.Composite( Below( 0, 0 ) );
			// Colour at 0.5 master x 0.5 channel = 0.25 over black: 255 * 0.25 = 64.
			Near( Texel( result ).R, 64, "colour at 0.25" );
			// Roughness at master 0.5 only: 200 * 0.5 = 100.
			Near( Texel( result.RoughnessMap ).R, 100, "roughness at 0.5" );
			Require( Math.Abs( layer.OpacityFor( Effigy.PaintChannel.Color ) - 0.25f ) < 0.001f, "OpacityFor is master x channel" );
			Require( Math.Abs( layer.OpacityFor( Effigy.PaintChannel.Roughness ) - 0.5f ) < 0.001f, "OpacityFor falls back to master" );
		} );

		Check( "per-channel blend overrides layer blend", () =>
		{
			var layer = Effigy.PaintLayer.Solid( "l", 4, 200, 200, 200, blend: Effigy.PaintBlendMode.Normal, opacity: 0.5f );
			layer.SetChannel( Effigy.PaintChannel.Roughness, Solid( 4, 4, 100, 100, 100 ) );
			layer.SetChannelBlend( Effigy.PaintChannel.Color, Effigy.PaintBlendMode.Darken );
			var stack = new Effigy.PaintLayerStack();
			stack.Add( layer );
			var result = stack.Composite( Below( 100, 200 ) );
			// Colour Darken at 0.5: 0.5 * min(100, 200) + 0.5 * 100 = 100.
			Near( Texel( result ).R, 100, "colour Darken" );
			// Roughness Normal at 0.5: 100 * 0.5 + 200 * 0.5 = 150.
			Near( Texel( result.RoughnessMap ).R, 150, "roughness Normal" );
			Require( layer.BlendFor( Effigy.PaintChannel.Color ) == Effigy.PaintBlendMode.Darken, "BlendFor returns the override" );
			Require( layer.BlendFor( Effigy.PaintChannel.Roughness ) == Effigy.PaintBlendMode.Normal, "BlendFor falls back to layer" );
		} );

		Check( "groups composite children then apply group params", () =>
		{
			var children = new List<Effigy.PaintLayer>
			{
				Effigy.PaintLayer.Solid( "blue", 4, 0, 0, 255 ),
				Effigy.PaintLayer.Solid( "red", 4, 255, 0, 0 ),
			};
			var group = Effigy.PaintLayer.Group( "g", children, opacity: 0.5f );
			Require( group.IsGroup, "group reports IsGroup" );
			var stack = new Effigy.PaintLayerStack();
			stack.Add( group );
			var result = stack.Composite( Below( 0, 0 ) );
			// Children: blue under red = red; group at 0.5 over black: 255 * 0.5 = 128.
			Near( Texel( result ).R, 128, "group result at 0.5" );
			Near( Texel( result ).G, 0, "group green" );
			Near( Texel( result ).B, 0, "group blue" );

			var mask = new float[16];
			var masked = Effigy.PaintLayer.Group( "m", new List<Effigy.PaintLayer> { Effigy.PaintLayer.Solid( "red", 4, 255, 0, 0 ) }, mask: mask );
			var stack2 = new Effigy.PaintLayerStack();
			stack2.Add( masked );
			var result2 = stack2.Composite( Below( 0, 0 ) );
			Near( Texel( result2 ).R, 0, "zero mask hides the group" );
		} );

		Check( "live reorder changes the composite and bumps Revision", () =>
		{
			var stack = new Effigy.PaintLayerStack();
			stack.Add( Effigy.PaintLayer.Solid( "red", 4, 255, 0, 0 ) );
			stack.Add( Effigy.PaintLayer.Solid( "blue", 4, 0, 0, 255 ) );
			Require( stack.Revision == 2, "two Adds bump Revision to 2" );
			Near( Texel( stack.Composite( Below( 0, 0 ) ) ).B, 255, "blue on top" );
			stack.Move( 1, 0 );
			Require( stack.Revision == 3, "a real Move bumps Revision" );
			Near( Texel( stack.Composite( Below( 0, 0 ) ) ).R, 255, "red on top after Move" );
			stack.Move( 0, 0 );
			Require( stack.Revision == 3, "a no-op Move does not bump Revision" );
			stack.RemoveAt( 0 );
			Require( stack.Revision == 4 && stack.Count == 1, "RemoveAt bumps Revision" );
		} );

		Check( "baker: flat quad bakes flat normal, AO and one ID", () =>
		{
			var group = new Effigy.BakeGroup { Name = "q", Low = Quad( 0f ), High = Quad( 0.1f ) };
			var result = Effigy.PaintBake.Bake( group, 8 );
			Require( result.Layer is not null && result.Layer.Name == "Bake", "bake layer named Bake" );
			Require( result.Layer.Maps.Normal is not null, "baked normal on the layer" );
			var n = Texel( result.Layer.Maps.Normal, 4, 4 );
			Near( n.R, 128, "flat normal R" );
			Near( n.G, 128, "flat normal G" );
			Near( n.B, 255, "flat normal B" );
			Require( result.AmbientOcclusion is not null, "baked AO present" );
			var ao = Texel( result.AmbientOcclusion, 4, 4 );
			Require( ao.R >= 240, "open flat quad is unoccluded, got " + ao.R );
			Require( result.FilledFraction > 0.9f, "quad covers the texture, got " + result.FilledFraction );
			Require( result.Curvature is not null, "baked curvature present" );
			InRange( Texel( Effigy.PaintBake.FromBaked( result.Curvature ), 4, 4 ).R, 120, 136, "flat curvature is mid grey" );
			Require( result.MaterialId is not null, "baked ID map present" );
			Require( result.IdColors is not null && result.IdColors.Count == 1, "one material id" );
		} );

		Check( "FromBaked copies RGB and marks padding transparent", () =>
		{
			var map = new Effigy.BakedMap( 2, 1 );
			map.Rgb[0] = 10; map.Rgb[1] = 20; map.Rgb[2] = 30;
			map.Rgb[3] = 40; map.Rgb[4] = 50; map.Rgb[5] = 60;
			map.Filled[0] = true; map.Filled[1] = false;
			var canvas = Effigy.PaintBake.FromBaked( map );
			var a = Texel( canvas, 0, 0 );
			var b = Texel( canvas, 1, 0 );
			Require( a.R == 10 && a.G == 20 && a.B == 30 && a.A == 255, "filled texel opaque" );
			Require( b.R == 40 && b.G == 50 && b.B == 60 && b.A == 0, "padding texel transparent" );
			Require( Effigy.PaintBake.FromBaked( null ) is null, "null map stays null" );
		} );

		Check( "ORM packing puts each channel in its slot", () =>
		{
			var orm = Effigy.UnrealOrmPreset.PackOrm(
				new byte[] { 10, 0, 0, 255 }, new byte[] { 20, 0, 0, 255 }, new byte[] { 30, 0, 0, 255 }, 1, 1 );
			Require( orm[0] == 10 && orm[1] == 20 && orm[2] == 30 && orm[3] == 255, "R=AO, G=rough, B=metal, A=255" );
		} );

		Check( "DirectX normal flip is correct", () =>
		{
			var input = new byte[] { 10, 20, 30, 255 };
			var flipped = Effigy.UnrealOrmPreset.FlipGreen( input );
			Require( flipped[0] == 10 && flipped[1] == 235 && flipped[2] == 30 && flipped[3] == 255, "flip inverts green only" );
			Require( input[1] == 20, "FlipGreen does not mutate its input" );
			var direct = Effigy.UnrealOrmPreset.ToDirectX( input, Effigy.NormalMapConvention.DirectX );
			Require( direct[1] == 20, "DirectX passes through unchanged" );
			var fromGl = Effigy.UnrealOrmPreset.ToDirectX( input, Effigy.NormalMapConvention.OpenGL );
			Require( fromGl[1] == 235, "OpenGL flips to DirectX" );
		} );

		Check( "naming template is {Asset}_{MapType}", () =>
		{
			Require( Effigy.UnrealOrmPreset.FileName( "Hero", "BaseColor" ) == "Hero_BaseColor.png", "BaseColor name" );
			Require( Effigy.UnrealOrmPreset.FileName( "Hero", "ORM" ) == "Hero_ORM.png", "ORM name" );
			Require( Effigy.UnrealOrmPreset.FileName( "Hero", "Normal" ) == "Hero_Normal.png", "Normal name" );
		} );

		Check( "VRAM warning fires only above 4K and never downscales", () =>
		{
			var warn = Effigy.UnrealOrmPreset.VramWarning( 8192, 100 );
			Require( warn is not null && warn.Contains( "2.5" ), "8K on 100 MB warns about 2.5 GB" );
			Require( Effigy.UnrealOrmPreset.VramWarning( 8192, 3000 ) is null, "8K on 3 GB is fine" );
			Require( Effigy.UnrealOrmPreset.VramWarning( 4096, 100 ) is null, "4K working size never warns" );
			Require( Effigy.UnrealOrmPreset.VramWarning( 8192, null ) is null, "unknown VRAM cannot warn" );
		} );

		Check( "Unreal Build end-to-end at small size", () =>
		{
			var paint = Solid( 4, 4, 200, 100, 50 );
			paint.OpacityMap = Solid( 4, 4, 128, 128, 128 );
			paint.HeightMap = Solid( 4, 4, 128, 128, 128 );
			paint.RoughnessMap = Solid( 4, 4, 153, 153, 153 );
			paint.MetalnessMap = Solid( 4, 4, 0, 0, 0 );
			var export = Effigy.UnrealOrmPreset.Build( paint, "Hero", 4, null );
			Require( export.Warning is null, "4K build with no VRAM figure has no warning" );
			Require( export.Images.Count == 3, "BaseColor + Normal + ORM, no Emissive when unpainted" );
			var color = FindImage( export.Images, "Hero_BaseColor.png" );
			Require( color.Width == 4 && color.Height == 4, "BaseColor at requested size" );
			Require( color.Rgba[0] == 200 && color.Rgba[1] == 100 && color.Rgba[2] == 50 && color.Rgba[3] == 128, "BaseColor RGB with opacity stamped into alpha" );
			var normal = FindImage( export.Images, "Hero_Normal.png" );
			Near( normal.Rgba[0], 128, "flat normal R" );
			Near( normal.Rgba[1], 128, "flat normal G" );
			Near( normal.Rgba[2], 255, "flat normal B" );
			var orm = FindImage( export.Images, "Hero_ORM.png" );
			// Flat height: HeightToAo gives 1 - 0.15 * (1 - 128/255) = 0.925 -> 236.
			Require( orm.Rgba[0] >= 230, "ORM R is AO, flat height is nearly unoccluded" );
			Require( orm.Rgba[1] == 153, "ORM G is roughness" );
			Require( orm.Rgba[2] == 0, "ORM B is metallic" );
			Require( orm.Rgba[3] == 255, "ORM A is 255" );

			var small = Effigy.UnrealOrmPreset.Build( paint, "Hero", 4, 100 );
			Require( small.Warning is null && small.Images.Count == 3, "low VRAM at working size changes nothing" );
			var threw = false;
			try { Effigy.UnrealOrmPreset.Build( paint, "Hero", 8193, null ); }
			catch ( ArgumentOutOfRangeException ) { threw = true; }
			Require( threw, "above 8K is refused, not silently capped" );
		} );
	}
}
