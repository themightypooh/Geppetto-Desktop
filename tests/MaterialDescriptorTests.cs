using System;
using System.IO;
using System.Linq;
using Effigy;
using Marionette.Desktop;

internal static class MaterialDescriptorTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "materials", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		(string Root, AssetPathResolver Resolver) CreateProjectFixture( string subfolder )
		{
			string root = Path.Combine( settings.WorkDirectory, "materials-tests", Guid.NewGuid().ToString( "N" ), subfolder );
			Directory.CreateDirectory( root );
			Directory.CreateDirectory( Path.Combine( root, "textures" ) );
			return (root, new AssetPathResolver( root ));
		}

		byte[] Make2x2RgbaBytes()
		{
			return new byte[]
			{
				255, 0, 0, 255,    // top-left red
				0, 255, 0, 255,    // top-right green
				0, 0, 255, 255,    // bottom-left blue
				255, 255, 0, 255   // bottom-right yellow
			};
		}

		byte[] MakePalettedPngBytes()
		{
			// Minimal PNG with colour type 3 (paletted) in IHDR
			return new byte[]
			{
				0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
				0x00, 0x00, 0x00, 0x0D,                         // IHDR chunk length = 13
				(byte)'I', (byte)'H', (byte)'D', (byte)'R',     // chunk type
				0x00, 0x00, 0x00, 0x01,                         // width = 1
				0x00, 0x00, 0x00, 0x01,                         // height = 1
				0x08,                                           // bit depth = 8
				0x03,                                           // colour type = 3 (PALETTED)
				0x00,                                           // compression = 0
				0x00,                                           // filter = 0
				0x00,                                           // interlace = 0
				0x29, 0x93, 0xD4, 0x1A                          // CRC (calculated)
			};
		}

		Check( "two same-named materials in different roots do not collide", () =>
		{
			var (rootA, pathResA) = CreateProjectFixture( "projA" );
			var (rootB, pathResB) = CreateProjectFixture( "projB" );

			var resolverA = new MaterialResolver( pathResA, "docA" );
			var resolverB = new MaterialResolver( pathResB, "docB" );

			var descA = new MaterialDescriptor( "suit_body", "Suit Body A" )
			{
				BaseColorFactor = new Vec4( 1f, 0f, 0f, 1f ),
				RoughnessFactor = 0.2f
			};
			var descB = new MaterialDescriptor( "suit_body", "Suit Body B" )
			{
				BaseColorFactor = new Vec4( 0f, 1f, 0f, 1f ),
				RoughnessFactor = 0.8f
			};

			resolverA.SetOverride( "suit_body", descA );
			resolverB.SetOverride( "suit_body", descB );

			var resA = resolverA.ResolveSlot( "suit_body" );
			var resB = resolverB.ResolveSlot( "suit_body" );

			Require( resA.Descriptor.BaseColorFactor.x == 1f && resA.Descriptor.BaseColorFactor.y == 0f, "Resolver A must use descA" );
			Require( resB.Descriptor.BaseColorFactor.x == 0f && resB.Descriptor.BaseColorFactor.y == 1f, "Resolver B must use descB" );
			Require( resA.Descriptor.RoughnessFactor == 0.2f, "Resolver A roughness mismatch" );
			Require( resB.Descriptor.RoughnessFactor == 0.8f, "Resolver B roughness mismatch" );
		} );

		Check( "renamed or relocated project directory resolves correctly via AssetPathResolver", () =>
		{
			var (root1, _) = CreateProjectFixture( "original" );
			string texPath1 = Path.Combine( root1, "textures", "albedo.png" );
			byte[] rgba = Make2x2RgbaBytes();
			PngWriter.WriteFileRgba( texPath1, rgba, 2, 2 );

			string parent = Path.GetDirectoryName( root1 )!;
			string root2 = Path.Combine( parent, "relocated" );
			Directory.Move( root1, root2 );

			var pathRes2 = new AssetPathResolver( root2 );
			var resolver2 = new MaterialResolver( pathRes2, "relocated_doc" );

			var desc = new MaterialDescriptor( "crate" )
			{
				BaseColorMap = TextureBinding.ForBaseColor( new AssetReference( "crate_col", "textures/albedo.png" ) )
			};
			resolver2.SetOverride( "crate", desc );

			var res = resolver2.ResolveSlot( "crate" );
			Require( res.Success, "Resolution must succeed in relocated project" );
			var imgResult = resolver2.LoadTexture( res.Descriptor.BaseColorMap! );
			Require( imgResult.Success && imgResult.Image != null, "Decoded image must load from relocated root" );
			Require( imgResult.Image!.Width == 2 && imgResult.Image.Height == 2 );
		} );

		Check( "missing PNG file reported with clear diagnostic and neutral fallback", () =>
		{
			var (_, pathRes) = CreateProjectFixture( "missing_tex" );
			var resolver = new MaterialResolver( pathRes );

			var desc = new MaterialDescriptor( "missing_mat" )
			{
				BaseColorMap = TextureBinding.ForBaseColor( new AssetReference( "missing_ref", "textures/absent_texture.png" ) )
			};
			resolver.SetOverride( "missing_mat", desc );

			var res = resolver.ResolveSlot( "missing_mat" );
			Require( res.Diagnostics.Count > 0, "Diagnostic must be generated for missing map" );
			Require( res.Diagnostics.Any( d => d.Message.Contains( "absent_texture.png" ) ), "Diagnostic must name missing path" );
			Require( res.Diagnostics.Any( d => d.MaterialId == "missing_mat" ), "Diagnostic must name material" );

			// Neutral fallback check
			var fallbackImg = resolver.GetChannelImageOrFallback( res.Descriptor, MaterialChannel.BaseColor );
			Require( fallbackImg.Width == 1 && fallbackImg.Height == 1, "Fallback pixel must be 1x1" );
			Require( fallbackImg.Rgba[0] == 255 && fallbackImg.Rgba[1] == 255 && fallbackImg.Rgba[2] == 255, "Neutral albedo fallback must be white" );
		} );

		Check( "corrupt PNG reported with clear diagnostic", () =>
		{
			var (root, pathRes) = CreateProjectFixture( "corrupt_png" );
			string badPng = Path.Combine( root, "textures", "corrupt.png" );
			// Write valid PNG signature followed by truncated/garbage chunk
			byte[] corruptBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x05, 0x49, 0x44, 0x41, 0x54, 0x11, 0x22 };
			File.WriteAllBytes( badPng, corruptBytes );

			var resolver = new MaterialResolver( pathRes );
			var desc = new MaterialDescriptor( "corrupt_mat" )
			{
				BaseColorMap = TextureBinding.ForBaseColor( new AssetReference( "bad_ref", "textures/corrupt.png" ) )
			};
			resolver.SetOverride( "corrupt_mat", desc );

			var res = resolver.ResolveSlot( "corrupt_mat" );
			Require( res.Diagnostics.Any( d => d.Message.Contains( "corrupt", StringComparison.OrdinalIgnoreCase ) ||
			                                   d.Message.Contains( "PNG", StringComparison.OrdinalIgnoreCase ) ),
				"Clear diagnostic must report corrupt PNG" );
		} );

		Check( "unsupported paletted or invalid PNG rejected with diagnostic", () =>
		{
			byte[] palettedBytes = MakePalettedPngBytes();
			var palettedResult = CpuImageReader.DecodePng( palettedBytes, "paletted_test" );
			Require( !palettedResult.Success, "Paletted PNG must not succeed" );
			Require( palettedResult.Diagnostic != null && palettedResult.Diagnostic.Message.Contains( "paletted", StringComparison.OrdinalIgnoreCase ),
				"Diagnostic must state that paletted PNG is unsupported" );

			byte[] jpegHeader = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };
			var jpegResult = CpuImageReader.DecodePng( jpegHeader, "jpeg_test" );
			Require( !jpegResult.Success, "JPEG must not succeed" );
			Require( jpegResult.Diagnostic != null && jpegResult.Diagnostic.Message.Contains( "JPEG" ),
				"Diagnostic must state unsupported JPEG format" );
		} );

		Check( "explicit relative override mapped correctly to slot", () =>
		{
			var (root, pathRes) = CreateProjectFixture( "override_test" );
			var resolver = new MaterialResolver( pathRes );

			var desc = new MaterialDescriptor( "custom_slot_mat", "Custom Slot Material" )
			{
				BaseColorFactor = new Vec4( 0.3f, 0.4f, 0.5f, 1f ),
				RoughnessFactor = 0.75f,
				MetallicFactor = 0.1f
			};
			resolver.SetOverride( "slot_head", desc );

			var res = resolver.ResolveSlot( 2, "slot_head" );
			Require( res.SlotIndex == 2, "Slot index must be preserved" );
			Require( res.SlotName == "slot_head", "Slot name must be preserved" );
			Require( res.Descriptor.Id == "custom_slot_mat" );
			Require( res.Descriptor.RoughnessFactor == 0.75f );
			Require( res.Success, "Resolution must be marked successful" );
		} );

		Check( "unknown shader and unsupported vmat syntax diagnosed", () =>
		{
			string vmatContent =
				"// Test vmat with unsupported shader\n" +
				"Layer0\n" +
				"{\n" +
				"    shader \"shaders/fancy_water_liquid.shader\"\n" +
				"    F_SPECULAR_CUBE_MAP 1\n" +
				"    TextureColor \"textures/water_color.png\"\n" +
				"    TextureNormal \"textures/water_normal.png\"\n" +
				"}\n";

			var (desc, diagnostics) = VmatParser.Parse( vmatContent, "water_mat" );

			Require( desc.BaseColorMap != null, "Color map should still parse" );
			Require( desc.NormalMap != null, "Normal map should still parse" );
			Require( diagnostics.Any( d => d.Message.Contains( "fancy_water_liquid.shader" ) ),
				"Diagnostic must report unsupported shader" );
			Require( diagnostics.Any( d => d.Message.Contains( "F_SPECULAR_CUBE_MAP" ) ),
				"Diagnostic must report unsupported parameter" );
		} );

		Check( "albedo sRGB vs data Linear color space metadata tagged correctly", () =>
		{
			string vmatContent =
				"Layer0\n" +
				"{\n" +
				"    shader \"shaders/complex.shader\"\n" +
				"    TextureColor \"textures/color.png\"\n" +
				"    TextureNormal \"textures/normal.png\"\n" +
				"    TextureRoughness \"textures/roughness.png\"\n" +
				"    TextureMetalness \"textures/metalness.png\"\n" +
				"    TextureEmission \"textures/emission.png\"\n" +
				"    TextureAmbientOcclusion \"textures/ao.png\"\n" +
				"}\n";

			var (desc, _) = VmatParser.Parse( vmatContent, "spaces_mat" );

			Require( desc.BaseColorMap!.ColorSpace == ColorSpaceInterpretation.Srgb, "BaseColor must be sRGB" );
			Require( desc.EmissionMap!.ColorSpace == ColorSpaceInterpretation.Srgb, "Emission must be sRGB" );
			Require( desc.NormalMap!.ColorSpace == ColorSpaceInterpretation.Linear, "Normal must be Linear" );
			Require( desc.RoughnessMap!.ColorSpace == ColorSpaceInterpretation.Linear, "Roughness must be Linear" );
			Require( desc.MetallicMap!.ColorSpace == ColorSpaceInterpretation.Linear, "Metallic must be Linear" );
			Require( desc.AmbientOcclusionMap!.ColorSpace == ColorSpaceInterpretation.Linear, "AO must be Linear" );
		} );

		Check( "no source file writes (resolver is strictly read-only)", () =>
		{
			var (root, pathRes) = CreateProjectFixture( "readonly_check" );
			string texPath = Path.Combine( root, "textures", "read.png" );
			byte[] rgba = Make2x2RgbaBytes();
			PngWriter.WriteFileRgba( texPath, rgba, 2, 2 );

			var beforeFiles = Directory.GetFiles( root, "*", SearchOption.AllDirectories )
				.ToDictionary( f => f, f => new FileInfo( f ).LastWriteTimeUtc );

			var resolver = new MaterialResolver( pathRes );
			var desc = new MaterialDescriptor( "mat_ro" )
			{
				BaseColorMap = TextureBinding.ForBaseColor( new AssetReference( "ro_tex", "textures/read.png" ) )
			};
			resolver.SetOverride( "slot_ro", desc );

			// Perform operations
			var res = resolver.ResolveSlot( "slot_ro" );
			resolver.LoadTexture( res.Descriptor.BaseColorMap! );
			resolver.GetChannelImageOrFallback( res.Descriptor, MaterialChannel.BaseColor );

			var afterFiles = Directory.GetFiles( root, "*", SearchOption.AllDirectories )
				.ToDictionary( f => f, f => new FileInfo( f ).LastWriteTimeUtc );

			Require( beforeFiles.Count == afterFiles.Count, "No new files must be written by resolver" );
			foreach ( var (path, beforeTime) in beforeFiles )
			{
				Require( afterFiles.TryGetValue( path, out var afterTime ), $"File {path} must still exist" );
				Require( beforeTime == afterTime, $"File {path} must not be modified" );
			}
		} );

		Check( "missing required override fails resolution", () =>
		{
			var (_, pathRes) = CreateProjectFixture( "req_override" );
			var resolver = new MaterialResolver( pathRes );

			resolver.SetRequiredSlot( "engine_chassis" );

			var res = resolver.ResolveSlot( 1, "engine_chassis" );
			Require( !res.Success, "Missing required override must result in resolution failure" );
			Require( res.Diagnostics.Any( d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains( "Required override" ) ),
				"Must emit Error diagnostic when required override is missing" );
		} );

		Check( "valid PNG decodes correctly to expected RGBA dimensions and bytes", () =>
		{
			byte[] original = Make2x2RgbaBytes();
			byte[] pngBytes = PngWriter.ToBytesRgba( original, 2, 2 );

			var result = CpuImageReader.DecodePng( pngBytes, "valid_2x2" );
			Require( result.Success, "Valid PNG must decode successfully" );
			Require( result.Image != null, "Decoded image must not be null" );
			Require( result.Image!.Width == 2 && result.Image.Height == 2, "Dimensions must match 2x2" );
			Require( result.Image.Rgba.Length == 16, "Must be 16 bytes (2x2 * 4 bpp)" );

			for ( int i = 0; i < 16; i++ )
			{
				Require( result.Image.Rgba[i] == original[i], $"Byte at index {i} must match original pixel byte" );
			}
		} );

		Check( "distinguish no map authored from authored map missing", () =>
		{
			var (_, pathRes) = CreateProjectFixture( "distinguish_maps" );
			var resolver = new MaterialResolver( pathRes );

			var unauthored = new MaterialDescriptor( "unauthored_mat" );
			resolver.SetOverride( "slot_unauthored", unauthored );

			var resUnauthored = resolver.ResolveSlot( "slot_unauthored" );
			Require( resUnauthored.Diagnostics.Count == 0, "No diagnostics should be emitted when channels are simply unauthored" );

			var missing = new MaterialDescriptor( "missing_mat" )
			{
				NormalMap = TextureBinding.ForNormal( new AssetReference( "missing_norm", "textures/ghost_norm.png" ) )
			};
			resolver.SetOverride( "slot_missing", missing );

			var resMissing = resolver.ResolveSlot( "slot_missing" );
			Require( resMissing.Diagnostics.Any( d => d.Message.Contains( "ghost_norm.png" ) ),
				"Diagnostic must be emitted when authored map is missing" );
		} );

		Check( "one-time diagnostic does not duplicate on repeated slot resolution", () =>
		{
			var (_, pathRes) = CreateProjectFixture( "dedup_diag" );
			var resolver = new MaterialResolver( pathRes );

			var desc = new MaterialDescriptor( "dedup_mat" )
			{
				BaseColorMap = TextureBinding.ForBaseColor( new AssetReference( "dedup_ref", "textures/absent.png" ) )
			};
			resolver.SetOverride( "slot_dedup", desc );

			for ( int i = 0; i < 5; i++ )
			{
				resolver.ResolveSlot( "slot_dedup" );
			}

			int count = resolver.Diagnostics.Count( d => d.Message.Contains( "absent.png" ) );
			Require( count == 1, $"Diagnostic must appear exactly once in resolver diagnostics, found {count}" );
		} );
	}
}
