using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Effigy;
using Marionette.Desktop;

// NOTE FOR THE COORDINATOR SWEEP (do not add here — dispatch freeze):
//   InflateTextureProjectionTests.Register( registry, settings );
// goes in tests/TestRegistry.cs next to the other Register lines. This file alone
// registers nothing until that line lands, which is the point of the freeze.
internal static class InflateTextureProjectionTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "inflate-texture-projection", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"Expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"Expected {typeof( T ).Name}, got silent success" );
		}

		// KNOWN DRAWING. A 4x4 image in four solid quadrants, row 0 at the top
		// (the PaintCanvas convention): top-left red, top-right green, bottom-left blue,
		// bottom-right white. Every assertion below is a quadrant name, never a magic
		// number — a flipped V or a swapped axis changes the quadrant and fails.
		PaintCanvas QuadrantCanvas()
		{
			var c = new PaintCanvas( 4, 4 );
			for ( var y = 0; y < 4; y++ )
				for ( var x = 0; x < 4; x++ )
				{
					var (r, g, b) = (x < 2, y < 2) switch
					{
						(true, true) => (255, 0, 0),
						(false, true) => (0, 255, 0),
						(true, false) => (0, 0, 255),
						(false, false) => (255, 255, 255),
					};
					var i = (y * 4 + x) * 4;
					c.Rgba[i] = (byte)r; c.Rgba[i + 1] = (byte)g; c.Rgba[i + 2] = (byte)b; c.Rgba[i + 3] = 255;
				}
			return c;
		}

		(byte R, byte G, byte B, byte A) Texel( PaintCanvas c, int x, int y )
		{
			var i = (y * c.Width + x) * 4;
			return (c.Rgba[i], c.Rgba[i + 1], c.Rgba[i + 2], c.Rgba[i + 3]);
		}

		// Independent re-derivation of what Nearest sampling SHOULD return at a UV:
		// texel column floor(u*W), row floor((1-v)*H), clamped. It does not call Sample,
		// so a Sample bug cannot agree with itself here.
		(byte R, byte G, byte B, byte A) ExpectedNearest( PaintCanvas source, Vec2 uv )
		{
			var x = Math.Clamp( (int)MathF.Floor( uv.x * source.Width ), 0, source.Width - 1 );
			var y = Math.Clamp( (int)MathF.Floor( (1f - uv.y) * source.Height ), 0, source.Height - 1 );
			return Texel( source, x, y );
		}

		// KNOWN DRAWING AS GEOMETRY. The square profile 0..10 in both axes, inflated Flat,
		// so Inflate2DFeature's planar UVs are exactly (qx/10, qy/10): UV (0.2, 0.8) IS
		// drawing point (2, 8), the top-left quadrant, no triangulation assumption needed.
		PolyMesh SquareMesh()
		{
			var profile = new LineworkProfile
			{
				Outer = new()
				{
					new Vec2( 0, 0 ), new Vec2( 10, 0 ), new Vec2( 10, 10 ), new Vec2( 0, 10 ),
				},
			};
			var mesh = Inflate2DFeature.Build( new[] { profile }, 2f, 0f, "Flat", false, 0 );
			Require( mesh is not null, "square did not inflate" );
			return mesh;
		}

		// Minimal PNG writer for the feature test: colour type 6 (RGBA), 8-bit, filter 0,
		// zlib stream via ZLibStream, IEEE CRC32. It exercises the feature's real
		// File -> PngReader path instead of smuggling a canvas in through a back door.
		byte[] PngBytes( PaintCanvas canvas )
		{
			var crcTable = new uint[256];
			for ( uint n = 0; n < 256; n++ )
			{
				var c = n;
				for ( var k = 0; k < 8; k++ ) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
				crcTable[n] = c;
			}
			uint Crc( byte[] type, byte[] data )
			{
				var c = 0xFFFFFFFFu;
				foreach ( var b in type ) c = crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
				foreach ( var b in data ) c = crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
				return c ^ 0xFFFFFFFFu;
			}
			void Chunk( MemoryStream ms, string type, byte[] data )
			{
				var tb = System.Text.Encoding.ASCII.GetBytes( type );
				ms.Write( new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length } );
				ms.Write( tb );
				ms.Write( data );
				var crc = Crc( tb, data );
				ms.Write( new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc } );
			}
			void Be32( MemoryStream ms, int v ) =>
				ms.Write( new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v } );
			var raw = new MemoryStream();
			for ( var y = 0; y < canvas.Height; y++ )
			{
				raw.WriteByte( 0 );
				raw.Write( canvas.Rgba, y * canvas.Width * 4, canvas.Width * 4 );
			}
			var compressed = new MemoryStream();
			using ( var z = new ZLibStream( compressed, CompressionMode.Compress, true ) ) z.Write( raw.ToArray() );
			var png = new MemoryStream();
			png.Write( new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 } );
			var ihdr = new MemoryStream();
			Be32( ihdr, canvas.Width ); Be32( ihdr, canvas.Height );
			ihdr.Write( new byte[] { 8, 6, 0, 0, 0 } );
			Chunk( png, "IHDR", ihdr.ToArray() );
			Chunk( png, "IDAT", compressed.ToArray() );
			Chunk( png, "IEND", Array.Empty<byte>() );
			return png.ToArray();
		}

		Check( "quadrant sample orientation", () =>
		{
			var src = QuadrantCanvas();
			var o = new InflateTextureProjection.Options();
			(byte R, byte G, byte B, byte A) red = (255, 0, 0, 255);
			(byte R, byte G, byte B, byte A) green = (0, 255, 0, 255);
			(byte R, byte G, byte B, byte A) blue = (0, 0, 255, 255);
			(byte R, byte G, byte B, byte A) white = (255, 255, 255, 255);
			// Drawing-up = image-up: high v is the top row, low v the bottom row.
			Require( InflateTextureProjection.Sample( src, new Vec2( 0.2f, 0.8f ), o ) == red, "top-left should be red" );
			Require( InflateTextureProjection.Sample( src, new Vec2( 0.8f, 0.8f ), o ) == green, "top-right should be green" );
			Require( InflateTextureProjection.Sample( src, new Vec2( 0.2f, 0.2f ), o ) == blue, "bottom-left should be blue" );
			Require( InflateTextureProjection.Sample( src, new Vec2( 0.8f, 0.2f ), o ) == white, "bottom-right should be white" );
		} );

		Check( "every face shows its own uv region", () =>
		{
			var src = QuadrantCanvas();
			var mesh = SquareMesh();
			var canvas = InflateTextureProjection.Apply( mesh, src, new InflateTextureProjection.Options() );
			Require( mesh.Paint == canvas, "Apply must install the canvas as the mesh texture" );
			Require( canvas.Width == 4 && canvas.Height == 4, "default output size is the source size" );
			// Triangulation-independent: each face is checked against ITS OWN centre UV,
			// re-derived independently, so no assumption about which triangles the cap
			// triangulation produced.
			Require( mesh.Faces.Count > 0, "inflated square has faces" );
			foreach ( var face in mesh.Faces )
			{
				var centre = InflateTextureProjection.FaceUvCentre( face );
				var got = InflateTextureProjection.Sample( src, centre, new InflateTextureProjection.Options() );
				var want = ExpectedNearest( src, centre );
				Require( got == want, $"face at UV {centre} shows {got}, expected {want}" );
			}
		} );

		Check( "identity project copies the source", () =>
		{
			var src = QuadrantCanvas();
			var canvas = InflateTextureProjection.Project( src, new InflateTextureProjection.Options() );
			Require( canvas.Rgba.SequenceEqual( src.Rgba ), "identity projection must be byte-identical" );
		} );

		Check( "bilinear blends across a boundary", () =>
		{
			var src = QuadrantCanvas();
			var o = new InflateTextureProjection.Options { Sampling = InflateTextureProjection.Sampling.Bilinear };
			// Exactly on the red|green vertical boundary, top half: half red, half green.
			var (r, g, b, a) = InflateTextureProjection.Sample( src, new Vec2( 0.5f, 0.75f ), o );
			Require( r >= 120 && r <= 136 && g >= 120 && g <= 136, $"boundary blend should be half red half green, got R={r} G={g}" );
			Require( b == 0 && a == 255, $"boundary blend should have no blue and full alpha, got B={b} A={a}" );
		} );

		Check( "affine offset shifts the drawing", () =>
		{
			var src = QuadrantCanvas();
			var o = new InflateTextureProjection.Options { UvOffset = new Vec2( 0.25f, 0.25f ), UvScale = new Vec2( 0.5f, 0.5f ) };
			(byte R, byte G, byte B, byte A) blue = (0, 0, 255, 255);
			(byte R, byte G, byte B, byte A) green = (0, 255, 0, 255);
			Require( InflateTextureProjection.Sample( src, new Vec2( 0f, 0f ), o ) == blue, "shifted origin should read bottom-left blue" );
			Require( InflateTextureProjection.Sample( src, new Vec2( 1f, 1f ), o ) == green, "shifted far corner should read top-right green" );
		} );

		Check( "repeat wraps, clamp pins", () =>
		{
			var src = QuadrantCanvas();
			var rep = new InflateTextureProjection.Options { Wrap = InflateTextureProjection.Wrap.Repeat };
			var clp = new InflateTextureProjection.Options { Wrap = InflateTextureProjection.Wrap.Clamp };
			(byte R, byte G, byte B, byte A) red = (255, 0, 0, 255);
			(byte R, byte G, byte B, byte A) green = (0, 255, 0, 255);
			Require( InflateTextureProjection.Sample( src, new Vec2( 1.2f, 0.75f ), rep ) == red, "repeat past 1 should wrap to the left column" );
			Require( InflateTextureProjection.Sample( src, new Vec2( 1.2f, 0.75f ), clp ) == green, "clamp past 1 should pin to the right column" );
		} );

		Check( "degenerate inputs refuse loudly", () =>
		{
			var src = QuadrantCanvas();
			Throws<ArgumentException>( () => InflateTextureProjection.Project( src, new InflateTextureProjection.Options { UvScale = new Vec2( 0f, 1f ) } ) );
			Throws<ArgumentNullException>( () => InflateTextureProjection.Sample( null, Vec2.Zero, new InflateTextureProjection.Options() ) );
			Throws<ArgumentException>( () => InflateTextureProjection.Apply( new PolyMesh(), src, new InflateTextureProjection.Options() ) );
		} );

		Check( "feature projects inflated body from png", () =>
		{
			var studio = new PartStudio();
			var session = DocumentSession.CreateEffigy( "inflate_texture_projection.effigy", studio );
			var inflate = new Inflate2DFeature { Name = "Inflated" };
			inflate.Rings.Value = "0,0;10,0;10,10;0,10";
			ParametricEditService.AddFeature( session, inflate );
			Require( session.Studio.Bodies.Count == 1, "inflate should produce one body" );
			var path = Path.Combine( settings.WorkDirectory, "drawing.png" );
			File.WriteAllBytes( path, PngBytes( QuadrantCanvas() ) );
			var project = new InflateTextureProjectionFeature { Name = "Projected" };
			project.Image.Value = path;
			ParametricEditService.AddFeature( session, project );
			Require( session.Studio.Bodies.Count == 1, "projection must not add a body" );
			Require( project.ProjectedBodyIds.Count == 1, "feature should report the one projected body" );
			var mesh = session.Studio.Bodies[0].Mesh;
			Require( mesh.Paint is not null, "projected body must carry a texture" );
			Require( mesh.Paint.Width == 4 && mesh.Paint.Height == 4, "projected texture keeps the source size" );
			var src = QuadrantCanvas();
			for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
			{
				var centre = InflateTextureProjection.FaceUvCentre( mesh.Faces[fi] );
				var got = InflateTextureProjection.SampleFace( mesh, src, fi, new InflateTextureProjection.Options() );
				Require( got == ExpectedNearest( src, centre ), $"feature face {fi} at UV {centre} shows {got}, expected {ExpectedNearest( src, centre )}" );
			}
		} );

		Check( "feature without image projects nothing", () =>
		{
			var studio = new PartStudio();
			var session = DocumentSession.CreateEffigy( "inflate_texture_projection_noimage.effigy", studio );
			var inflate = new Inflate2DFeature { Name = "Inflated" };
			inflate.Rings.Value = "0,0;10,0;10,10;0,10";
			ParametricEditService.AddFeature( session, inflate );
			var project = new InflateTextureProjectionFeature { Name = "Projected" };
			ParametricEditService.AddFeature( session, project );
			Require( project.ProjectedBodyIds.Count == 0, "a feature with no image must project no body" );
			Require( session.Studio.Bodies[0].Mesh.Paint is null, "a feature with no image must leave the mesh untextured" );
		} );
	}
}
