using System.Numerics;
using Effigy;
using Marionette.Desktop;
using Silk.NET.OpenGL;

internal static class MaterialGpuTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Assert( bool value, string message ) { if ( !value ) throw new Exception( message ); }
		PolyMesh Quad()
		{
			var mesh = new PolyMesh();
			mesh.Positions.AddRange( new[] { new Vec3( 0, -1, -1 ), new Vec3( 0, 1, -1 ),
				new Vec3( 0, 1, 1 ), new Vec3( 0, -1, 1 ) } );
			mesh.AddFace( new[] { 0, 1, 2, 3 },
				new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) }, 0 );
			return mesh;
		}
		string root = Path.Combine( settings.WorkDirectory, "material-gpu" );
		Directory.CreateDirectory( root );
		string Write( string folder, string filename, byte[] pixels )
		{
			string path = Path.Combine( root, folder ); Directory.CreateDirectory( path );
			PngWriter.WriteFileRgba( Path.Combine( path, filename ), pixels, 2, 2 );
			return path;
		}
		MaterialResolver Resolver( string folder, string filename )
		{
			var resolver = new MaterialResolver( folder, "fixture" );
			resolver.SetOverride( "test", new MaterialDescriptor( "test" )
			{
				BaseColorMap = TextureBinding.ForBaseColor( new AssetReference( "albedo", filename ),
					filter: TextureFilter.Nearest ),
				RoughnessFactor = 1
			}, required: true );
			return resolver;
		}
		registry.Add( "material-gpu", "corner UVs preserve a shared-position seam and layout offsets", () =>
		{
			var source = Quad();
			source.AddFace( new[] { 0, 2, 3 }, new[] { new Vec2( .25f, .75f ),
				new Vec2( 1, 1 ), new Vec2( 0, 1 ) }, 0 );
			using var mesh = ViewportMesh.FromPolyMesh( source, _ => "test" );
			Assert( ViewportMesh.FloatsPerVertex >= 16, "UV stream must follow existing 14 floats" );
			Assert( mesh.Vertices[14] == 0 && mesh.Vertices[15] == 0, "First corner UV incorrect" );
			Assert( mesh.Vertices[4 * ViewportMesh.FloatsPerVertex + 14] == .25f &&
				mesh.Vertices[4 * ViewportMesh.FloatsPerVertex + 15] == .75f,
				"Shared position's second corner UV was collapsed" );
		} );
		registry.Add( "material-gpu", "same filename in separate roots resolves independent pixels", () =>
		{
			var red = new byte[] { 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255 };
			var blue = new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 };
			string a = Write( "a", "shared.png", red ); string b = Write( "b", "shared.png", blue );
			using var first = ViewportMesh.FromPolyMesh( Quad(), _ => "test" );
			using var second = ViewportMesh.FromPolyMesh( Quad(), _ => "test" );
			first.ApplyMaterials( Resolver( a, "shared.png" ) ); second.ApplyMaterials( Resolver( b, "shared.png" ) );
			Assert( first.Batches[0].AlbedoImage.Rgba[0] == 255 && second.Batches[0].AlbedoImage.Rgba[2] == 255,
				"Texture identity accidentally aliased by basename" );
		} );
		registry.Add( "material-gpu", "windowless renderer samples asymmetric sRGB albedo and releases textures", () =>
		{
			// PngWriter rows are top-first: blue/yellow at V=1, red/green at V=0.
			byte[] pixels = { 0, 0, 255, 255, 255, 255, 0, 255,
				255, 0, 0, 255, 0, 255, 0, 255 };
			string folder = Write( "corners", "four.png", pixels );
			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			using var renderer = new SceneRenderer( gl );
			using var target = new CaptureTarget( gl, 128, 128 );
			var camera = new OrbitCamera { Target = Vector3.Zero, Distance = 5,
				YawDegrees = 0, PitchDegrees = 0 };
			byte[] Draw( MaterialResolver? materials, PolyMesh? model = null )
			{
				using var mesh = ViewportMesh.FromPolyMesh( model ?? Quad(), slot => slot == 0 ? "test" : "other" );
				if ( materials != null ) mesh.ApplyMaterials( materials );
				mesh.Upload( gl );
				renderer.Draw( mesh, null, camera, 128, 128, 1, target.Handle );
				byte[] image = new byte[128 * 128 * 3];
				gl.Finish();
				gl.ReadPixels<byte>( 0, 0, 128, 128, PixelFormat.Rgb, PixelType.UnsignedByte, image.AsSpan() );
				mesh.ReleaseGpu( gl );
				Assert( gl.GetError() == GLEnum.NoError, "GL error after albedo draw/release" );
				return image;
			}
			var flat = Draw( null );
			var textured = Draw( Resolver( folder, "four.png" ) );
			byte[] Center( byte[] image, int x, int y ) => image.Skip( (y * 128 + x) * 3 ).Take( 3 ).ToArray();
			var bottomLeft = Center( textured, 42, 42 );
			var bottomRight = Center( textured, 86, 42 );
			var topLeft = Center( textured, 42, 86 );
			Assert( bottomLeft[0] > bottomLeft[1] + 20 && bottomLeft[0] > bottomLeft[2] + 20,
				$"V=0/U=0 expected red, got {string.Join( ',', bottomLeft )}" );
			Assert( bottomRight[1] > bottomRight[0] + 20 && bottomRight[1] > bottomRight[2] + 20,
				$"V=0/U=1 expected green, got {string.Join( ',', bottomRight )}" );
			Assert( topLeft[2] > topLeft[0] + 20 && topLeft[2] > topLeft[1] + 20,
				$"V=1/U=0 expected blue, got {string.Join( ',', topLeft )}" );
			Assert( !flat.SequenceEqual( textured ), "Mapless and authored map rendered identically" );
			Assert( Draw( Resolver( folder, "four.png" ) ).SequenceEqual( textured ), "Reloaded texture changed pixels" );

			var two = new PolyMesh();
			two.Positions.AddRange( new[] { new Vec3( 0, -1, -1 ), new Vec3( 0, 0, -1 ),
				new Vec3( 0, 0, 1 ), new Vec3( 0, -1, 1 ), new Vec3( 0, 1, -1 ), new Vec3( 0, 1, 1 ) } );
			var uv = new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) };
			two.AddFace( new[] { 0, 1, 2, 3 }, uv, 0 );
			two.AddFace( new[] { 1, 4, 5, 2 }, uv, 1 );
			string reds = Write( "red-map", "same.png", new byte[] {
				255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255 } );
			string blues = Write( "blue-map", "same.png", new byte[] {
				0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 } );
			var perSlot = new MaterialResolver( reds, "two-slots" );
			perSlot.SetOverride( 0, new MaterialDescriptor( "left" ) { BaseColorMap = TextureBinding.ForBaseColor(
				new AssetReference( "red", "same.png" ), filter: TextureFilter.Nearest ) } );
			perSlot.SetOverride( 1, new MaterialDescriptor( "right" ) { BaseColorMap = TextureBinding.ForBaseColor(
				new AssetReference( "blue", Path.Combine( blues, "same.png" ), AssetReferenceKind.ExternalReadOnly ),
					filter: TextureFilter.Nearest ) } );
			var split = Draw( perSlot, two );
			var left = Center( split, 48, 64 ); var right = Center( split, 80, 64 );
			Assert( left[0] > left[2] + 30 && right[2] > right[0] + 30,
				$"Two material batches did not bind separate maps: {string.Join( ',', left )} / {string.Join( ',', right )}" );
		} );
	}
}
