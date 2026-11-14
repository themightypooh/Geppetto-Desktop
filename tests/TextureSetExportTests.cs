using System;
using System.Collections.Generic;
using Marionette.Desktop;

// Texture Sets + UDIM + OpenPBR + per-engine export presets (godlike-texture-sets-udim-
// export-presets, GODLIKE WAVE 3 P2): per-ID independence, UDIM tile math and per-tile
// resolution, OpenPBR round-trip and library, triplanar projection, per-engine channel
// packing (pixel-exact on synthetic greys), naming templates. NOT yet registered in
// tests/TestRegistry.cs (coordinator-owned); the final integrator adds the one Register
// line. Implementation done is NOT runtime verified: no full build or app launch was run
// for this ticket (PO workflow override).
internal static class TextureSetExportTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "texture-sets", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, string what )
		{
			if ( MathF.Abs( actual - expected ) > 1e-5f ) throw new Exception( what + ": expected " + expected + ", got " + actual );
		}
		void Throws<T>( Action f, string what ) where T : Exception
		{
			try { f(); }
			catch ( T ) { return; }
			catch ( Exception e ) { throw new Exception( what + ": threw " + e.GetType().Name + ", not " + typeof( T ).Name ); }
			throw new Exception( what + ": did not throw" );
		}
		void ThrowsAny( Action f, string what )
		{
			try { f(); }
			catch ( Exception ) { return; }
			throw new Exception( what + ": did not throw" );
		}

		byte[] Grey( byte v, int w = 2, int h = 2 ) => ChannelPacker.SolidGrey( v, w, h );
		(byte R, byte G, byte B, byte A) Texel( byte[] rgba, int w, int x, int y )
		{
			var i = (y * w + x) * 4;
			return (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
		}
		OpenPbrMaterial Skin() => OpenPbrLibrary.Get( "skin" );
		TextureSet Set( string id, int resolution, OpenPbrMaterial? material = null,
			IEnumerable<UdimTileEntry>? tiles = null, IEnumerable<Effigy.PaintChannel>? channels = null ) =>
			new( id, id, resolution, material ?? Skin(), tiles, channels );

		Check( "udim tile numbers are 1001 + u + 10v (reused kernel UdimTileSet)", () =>
		{
			Require( Effigy.UdimTileSet.TileId( 0, 0 ) == 1001, "origin is 1001" );
			Require( Effigy.UdimTileSet.TileId( 1, 0 ) == 1002, "one tile in U is 1002" );
			Require( Effigy.UdimTileSet.TileId( 0, 1 ) == 1011, "one tile in V is 1011" );
			Require( Effigy.UdimTileSet.TileForUv( 9.9f, 9.9f ) == 1100, "last tile is 1100" );
			var origin = Effigy.UdimTileSet.TileCoords( 1001 );
			Require( origin.U == 0 && origin.V == 0, "1001 is (0,0)" );
			var last = Effigy.UdimTileSet.TileCoords( 1100 );
			Require( last.U == 9 && last.V == 9, "1100 is (9,9)" );
			var mid = Effigy.UdimTileSet.TileCoords( 1022 );
			Require( mid.U == 1 && mid.V == 2, "1022 is (1,2)" );
			Throws<ArgumentOutOfRangeException>( () => Effigy.UdimTileSet.TileForUv( -0.1f, 0f ), "negative U refused" );
			Throws<ArgumentOutOfRangeException>( () => Effigy.UdimTileSet.TileForUv( 10f, 0f ), "U at 10 refused" );
			Throws<ArgumentOutOfRangeException>( () => Effigy.UdimTileSet.TileCoords( 1000 ), "1000 refused" );
			Throws<ArgumentOutOfRangeException>( () => Effigy.UdimTileSet.TileCoords( 1101 ), "1101 refused" );
		} );

		Check( "udim uv in tile is the fractional part", () =>
		{
			var (tile, u, v) = Effigy.UdimTileSet.Locate( 1.25f, 2.75f );
			Require( tile == 1022, "1.25,2.75 lives in 1022" );
			Near( u, 0.25f, "u in tile" );
			Near( v, 0.75f, "v in tile" );
			Require( Effigy.UdimTileSet.TileForUv( 1.5f, 2.5f ) == 1022, "same square, same tile" );
			Require( Effigy.UdimTileSet.TileForUv( 2.5f, 2.5f ) != 1022, "next square, different tile" );
		} );

		Check( "texture set resolution must be a power of two in range", () =>
		{
			Require( TextureSetRules.IsValidResolution( 4096 ), "4096 valid" );
			Require( !TextureSetRules.IsValidResolution( 3000 ), "3000 not a power of two" );
			Require( !TextureSetRules.IsValidResolution( 32 ), "32 below minimum" );
			Require( !TextureSetRules.IsValidResolution( 32768 ), "32768 above maximum" );
			Throws<ArgumentOutOfRangeException>( () => Set( "x", 3000 ), "3000 set refused" );
			Throws<ArgumentException>( () => Set( "x", 1024, null, null,
				new[] { Effigy.PaintChannel.Roughness } ), "set without Colour refused" );
			Throws<ArgumentException>( () => new TextureSet( "x", "x", 1024, Skin(), new[]
			{
				new UdimTileEntry( 1001, 1024 ),
				new UdimTileEntry( 1001, 1024 ),
			} ), "duplicate tile refused" );
			var single = Set( "solo", 2048 );
			Require( single.Tiles.Count == 1 && single.Tiles[0].TileNumber == 1001, "no tiles means one tile 1001" );
			Require( single.ResolutionForTile( 1001 ) == 2048, "single tile at set resolution" );
			Require( single.TileForUv( 0.5f, 0.5f ).TileNumber == 1001, "uv in the set's tile finds it" );
			Throws<ArgumentException>( () => single.TileForUv( 1.5f, 0.5f ), "uv outside the set's tiles refused" );
			Throws<ArgumentException>( () => single.ResolutionForTile( 1002 ), "unknown tile resolution refused" );
		} );

		Check( "per-id sets export independently", () =>
		{
			var sets = new[]
			{
				Set( "face", 8192 ), Set( "body", 4096 ), Set( "metal", 2048 ),
			};
			var all = TextureSetExporter.Plan( "hero", sets, ExportEngine.Unreal );
			var face = TextureSetExporter.PlanForSet( "hero", sets[0], ExportEngine.Unreal );
			var body = TextureSetExporter.PlanForSet( "hero", sets[1], ExportEngine.Unreal );
			var metal = TextureSetExporter.PlanForSet( "hero", sets[2], ExportEngine.Unreal );
			Require( all.Count == face.Count + body.Count + metal.Count, "whole plan is the sum of the per-set plans" );
			Require( face.Count > 0 && body.Count > 0 && metal.Count > 0, "every set plans files" );
			foreach ( var f in face )
			{
				Require( f.SetId == "face", "face plan names only face" );
				Require( f.FileName.Contains( "face" ), "face file names the set" );
				Require( !f.FileName.Contains( "body" ) && !f.FileName.Contains( "metal" ), "face file names no other set" );
			}
			var names = new HashSet<string>();
			foreach ( var f in all )
				Require( names.Add( f.FileName ), "file names are unique across sets: " + f.FileName );
		} );

		Check( "udim tiles carry their own resolution in the plan", () =>
		{
			var face = Set( "face", 8192, null, new[]
				{ new UdimTileEntry( 1001, 8192 ) } );
			var body = Set( "body", 4096, null, new[]
			{
				new UdimTileEntry( 1001, 4096 ),
				new UdimTileEntry( 1002, 4096 ),
			} );
			var plan = TextureSetExporter.Plan( "hero", new[] { face, body }, ExportEngine.Gltf );
			foreach ( var f in plan )
			{
				if ( f.SetId == "face" ) Require( f.Width == 8192 && f.Height == 8192, "face tile at 8K" );
				else Require( f.Width == 4096 && f.Height == 4096, "body tile at 4K" );
			}
			var bodyFiles = new List<string>();
			foreach ( var f in plan ) if ( f.SetId == "body" ) bodyFiles.Add( f.FileName );
			Require( bodyFiles.Exists( n => n.Contains( "_1001_" ) ), "body plan names tile 1001" );
			Require( bodyFiles.Exists( n => n.Contains( "_1002_" ) ), "body plan names tile 1002" );
			var faceFiles = new List<string>();
			foreach ( var f in plan ) if ( f.SetId == "face" ) faceFiles.Add( f.FileName );
			Require( faceFiles.TrueForAll( n => !n.Contains( "_1001" ) ), "single-tile face omits the tile number" );
		} );

		Check( "openpbr material round-trips through json", () =>
		{
			var original = new OpenPbrMaterial( MaterialLibrary.Get( "skin" ) )
			{
				SubsurfaceColor = new Effigy.Vec3( 0.9f, 0.55f, 0.45f ), SubsurfaceWeight = 0.5f,
				ClearcoatFactor = 0.2f, ClearcoatRoughness = 0.3f,
				SheenColor = new Effigy.Vec3( 1f, 0.9f, 0.8f ), SheenWeight = 0.4f, SheenRoughness = 0.6f,
				Anisotropy = 0.25f, AnisotropyRotation = 0.5f,
				TriplanarEnabled = true, TriplanarScale = 2f, TriplanarBlendSharpness = 6f,
			};
			var back = OpenPbrMaterial.FromJson( original.ToJson() );
			Require( back.Descriptor.Id == original.Descriptor.Id, "id round-trips" );
			Require( back.Descriptor.Name == original.Descriptor.Name, "name round-trips" );
			Near( back.Descriptor.BaseColorFactor.x, original.Descriptor.BaseColorFactor.x, "base colour r" );
			Near( back.Descriptor.BaseColorFactor.y, original.Descriptor.BaseColorFactor.y, "base colour g" );
			Near( back.Descriptor.BaseColorFactor.z, original.Descriptor.BaseColorFactor.z, "base colour b" );
			Near( back.Descriptor.BaseColorFactor.w, original.Descriptor.BaseColorFactor.w, "base colour a" );
			Near( back.Descriptor.RoughnessFactor, original.Descriptor.RoughnessFactor, "roughness" );
			Near( back.Descriptor.MetallicFactor, original.Descriptor.MetallicFactor, "metallic" );
			Near( back.Descriptor.EmissionFactor.x, original.Descriptor.EmissionFactor.x, "emission r" );
			Require( back.Descriptor.AlphaMode == original.Descriptor.AlphaMode, "alpha mode round-trips" );
			Near( back.SubsurfaceWeight, 0.5f, "subsurface weight" );
			Near( back.SubsurfaceColor.x, 0.9f, "subsurface colour r" );
			Near( back.ClearcoatFactor, 0.2f, "clearcoat" );
			Near( back.ClearcoatRoughness, 0.3f, "clearcoat roughness" );
			Near( back.SheenWeight, 0.4f, "sheen weight" );
			Near( back.Anisotropy, 0.25f, "anisotropy" );
			Near( back.AnisotropyRotation, 0.5f, "anisotropy rotation" );
			Require( back.TriplanarEnabled, "triplanar enabled round-trips" );
			Near( back.TriplanarScale, 2f, "triplanar scale" );
			Near( back.TriplanarBlendSharpness, 6f, "triplanar sharpness" );
			Require( back.ToDescriptor().Id == original.ToDescriptor().Id, "descriptor identity survives the round-trip" );
			Throws<ArgumentException>( () => OpenPbrMaterial.FromJson( "" ), "empty json refused" );
			ThrowsAny( () => OpenPbrMaterial.FromJson( "{not json" ), "malformed json refused" );
		} );

		Check( "openpbr library wraps known presets with extension defaults", () =>
		{
			Require( OpenPbrLibrary.Get( "skin" ).SubsurfaceWeight == 0.5f, "skin gets subsurface" );
			Require( OpenPbrLibrary.Get( "glass-clear" ).ClearcoatFactor == 1f, "glass gets clearcoat" );
			Near( OpenPbrLibrary.Get( "glass-clear" ).ClearcoatRoughness, 0.05f, "glass clearcoat roughness" );
			Require( OpenPbrLibrary.Get( "fabric-silk" ).SheenWeight == 0.6f, "silk gets sheen" );
			Require( OpenPbrLibrary.Get( "metal" ).SubsurfaceWeight == 0f, "metal gets no subsurface" );
			ThrowsAny( () => OpenPbrLibrary.Get( "no-such-material" ), "unknown preset refused" );
		} );

		Check( "triplanar picks the dominant axis and blends by sharpness", () =>
		{
			var (u, v, axis) = OpenPbrMaterial.TriplanarUv(
				new Effigy.Vec3( 2f, 3f, 4f ), new Effigy.Vec3( 1f, 0f, 0f ) );
			Require( axis == 0, "x-dominant normal projects on X" );
			Near( u, 3f, "x-plane u is y" );
			Near( v, 4f, "x-plane v is z" );
			var z = OpenPbrMaterial.TriplanarUv( new Effigy.Vec3( 2f, 3f, 4f ), new Effigy.Vec3( 0f, 0f, -1f ) );
			Require( z.Axis == 2, "z-dominant normal projects on Z" );
			Near( z.U, 2f, "z-plane u is x" );
			Near( z.V, 3f, "z-plane v is y" );
			var even = OpenPbrMaterial.TriplanarBlendWeights( new Effigy.Vec3( 1f, 1f, 1f ), 1f );
			Near( even.x, 1f / 3f, "equal normal blends a third per axis" );
			Near( even.y, 1f / 3f, "equal normal blends a third per axis (y)" );
			Near( even.z, 1f / 3f, "equal normal blends a third per axis (z)" );
			var sharp = OpenPbrMaterial.TriplanarBlendWeights( new Effigy.Vec3( 1f, 0.5f, 0f ), 8f );
			Require( sharp.x > 0.9f, "sharpness concentrates on the dominant axis" );
			var zero = OpenPbrMaterial.TriplanarBlendWeights( Effigy.Vec3.Zero, 4f );
			Near( zero.x, 1f / 3f, "zero normal blends evenly instead of dividing by zero" );
			Throws<ArgumentOutOfRangeException>(
				() => OpenPbrMaterial.TriplanarBlendWeights( new Effigy.Vec3( 1f, 0f, 0f ), 0f ), "zero sharpness refused" );
		} );

		Check( "unreal orm packs ao rough metal", () =>
		{
			var orm = ChannelPacker.PackOrm( Grey( 10 ), Grey( 100 ), Grey( 200 ), 2, 2 );
			var p = Texel( orm, 2, 1, 1 );
			Require( p.R == 10 && p.G == 100 && p.B == 200 && p.A == 255, "orm pixel is (ao, rough, metal, 255)" );
			var noAo = ChannelPacker.PackOrm( null, Grey( 100 ), Grey( 200 ), 2, 2 );
			Require( Texel( noAo, 2, 0, 0 ).R == 255, "missing occlusion packs white" );
			Throws<ArgumentException>( () => ChannelPacker.PackOrm( Grey( 10 ), Grey( 100, 2, 2 ), Grey( 200, 4, 4 ), 2, 2 ),
				"mismatched channel sizes refused" );
		} );

		Check( "gltf metallic-roughness packs g rough b metal", () =>
		{
			var mr = ChannelPacker.PackMetallicRoughness( Grey( 100 ), Grey( 200 ), 2, 2 );
			var p = Texel( mr, 2, 0, 0 );
			Require( p.R == 255 && p.G == 100 && p.B == 200 && p.A == 255, "gltf pixel is (255, rough, metal, 255)" );
		} );

		Check( "unity urp metallic-smoothness and hdrp mask map pack correctly", () =>
		{
			var ms = ChannelPacker.PackMetallicSmoothness( Grey( 200 ), Grey( 100 ), 2, 2 );
			var p = Texel( ms, 2, 0, 0 );
			Require( p.R == 200 && p.G == 0 && p.B == 0 && p.A == 155, "urp pixel is (metal, 0, 0, 1-rough)" );
			var mask = ChannelPacker.PackMaskMap( Grey( 200 ), Grey( 10 ), Grey( 100 ), 2, 2 );
			var q = Texel( mask, 2, 0, 0 );
			Require( q.R == 200 && q.G == 10 && q.B == 0 && q.A == 155, "hdrp pixel is (metal, ao, 0, 1-rough)" );
			var noAo = ChannelPacker.PackMaskMap( Grey( 200 ), null, Grey( 100 ), 2, 2 );
			Require( Texel( noAo, 2, 0, 0 ).G == 255, "hdrp missing occlusion packs white" );
		} );

		Check( "normal green flips between conventions and double flip is identity", () =>
		{
			var normal = new byte[] { 128, 100, 255, 255, 128, 100, 255, 255, 128, 100, 255, 255, 128, 100, 255, 255 };
			var flipped = ChannelPacker.FlipGreen( normal, 2, 2 );
			Require( Texel( flipped, 2, 0, 0 ).G == 155, "green flips to 255-100" );
			Require( Texel( flipped, 2, 0, 0 ).R == 128 && Texel( flipped, 2, 0, 0 ).B == 255, "red and blue untouched" );
			var back = ChannelPacker.FlipGreen( flipped, 2, 2 );
			Require( Texel( back, 2, 0, 0 ).G == 100, "double flip restores green" );
			Require( ExportPreset.For( ExportEngine.Unreal ).NormalConvention == NormalMapConvention.DirectX,
				"unreal uses directx green" );
			Require( !ExportPreset.For( ExportEngine.Unreal ).FlipGreenFromDirectX, "unreal needs no green flip" );
			Require( ExportPreset.For( ExportEngine.UnityUrp ).NormalConvention == NormalMapConvention.OpenGL,
				"unity urp uses opengl green" );
			Require( ExportPreset.For( ExportEngine.UnityUrp ).FlipGreenFromDirectX, "unity urp flips green" );
			Require( ExportPreset.For( ExportEngine.Gltf ).FlipGreenFromDirectX, "gltf flips green" );
			Require( ExportPreset.For( ExportEngine.Godot ).FlipGreenFromDirectX, "godot flips green" );
		} );

		Check( "naming template is stable and sanitised", () =>
		{
			Require( TextureSetExporter.FileName( "hero", "body", 1001, 1, "BaseColor" ) == "hero_body.png",
				"single-tile colour takes no suffix" );
			Require( TextureSetExporter.FileName( "hero", "body", 1002, 2, "ORM" ) == "hero_body_1002_ORM.png",
				"multi-tile names the tile and the map" );
			Require( TextureSetExporter.FileName( "hero asset", "body", 1001, 1, "BaseColor" ) == "hero_asset_body.png",
				"spaces sanitise to underscores" );
			Throws<ArgumentException>( () => TextureSetExporter.FileName( "", "body", 1001, 1, "BaseColor" ),
				"empty asset name refused" );
		} );

		Check( "preset maps follow the channels the set carries", () =>
		{
			var colourOnly = Set( "plain", 1024, null, null, new[] { Effigy.PaintChannel.Color } );
			var keys = new List<string>();
			foreach ( var f in TextureSetExporter.PlanForSet( "hero", colourOnly, ExportEngine.Unreal ) )
				keys.Add( f.MapKey );
			Require( keys.Contains( "BaseColor" ), "colour-only set still exports its albedo" );
			Require( !keys.Contains( "ORM" ) && !keys.Contains( "Normal" ) && !keys.Contains( "Emissive" ),
				"colour-only set exports no packed, normal or emissive map" );
			var full = Set( "full", 1024 );
			var fullKeys = new List<string>();
			foreach ( var f in TextureSetExporter.PlanForSet( "hero", full, ExportEngine.Gltf ) )
				fullKeys.Add( f.MapKey );
			Require( fullKeys.Contains( "BaseColor" ) && fullKeys.Contains( "Normal" )
				&& fullKeys.Contains( "MetallicRoughness" ) && fullKeys.Contains( "Occlusion" )
				&& fullKeys.Contains( "Emissive" ), "full set exports every gltf map" );
		} );
	}
}
