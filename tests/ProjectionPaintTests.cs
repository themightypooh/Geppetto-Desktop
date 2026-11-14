#nullable enable
using Effigy;
using Marionette.Desktop;

/// <summary>Projection / stencil / clone painting, UDIM tiles, and 16-bit EXR export
/// (godlike-projection-stencil-udim-painting). All checks run on small synthetic canvases and
/// meshes — no photo files, no GPU, no viewer.</summary>
internal static class ProjectionPaintTests
{
	static PolyMesh QuadUv()
	{
		var mesh = new PolyMesh();
		mesh.AddVertex( new Vec3( -1, -1, 0 ) );
		mesh.AddVertex( new Vec3( 1, -1, 0 ) );
		mesh.AddVertex( new Vec3( 1, 1, 0 ) );
		mesh.AddVertex( new Vec3( -1, 1, 0 ) );
		mesh.AddFace( new[] { 0, 1, 2, 3 } );
		mesh.Faces[0].UVs = new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) };
		return mesh;
	}

	/// <summary>2x2 photo: top-left red, top-right green, bottom-left blue, bottom-right white.</summary>
	static PaintCanvas Photo()
	{
		var photo = new PaintCanvas( 2, 2 );
		void Set( int x, int y, byte r, byte g, byte b )
		{
			var i = (y * 2 + x) * 4;
			photo.Rgba[i] = r;
			photo.Rgba[i + 1] = g;
			photo.Rgba[i + 2] = b;
			photo.Rgba[i + 3] = 255;
		}
		Set( 0, 0, 255, 0, 0 );
		Set( 1, 0, 0, 255, 0 );
		Set( 0, 1, 0, 0, 255 );
		Set( 1, 1, 255, 255, 255 );
		return photo;
	}

	static (int R, int G, int B, int A) Texel( PaintCanvas c, int x, int y )
	{
		var i = (y * c.Width + x) * 4;
		return (c.Rgba[i], c.Rgba[i + 1], c.Rgba[i + 2], c.Rgba[i + 3]);
	}

	static void SetTexel( PaintCanvas c, int x, int y, byte r, byte g, byte b, byte a = 255 )
	{
		var i = (y * c.Width + x) * 4;
		c.Rgba[i] = r;
		c.Rgba[i + 1] = g;
		c.Rgba[i + 2] = b;
		c.Rgba[i + 3] = a;
	}

	static float Linear( float srgb ) => srgb <= 0.04045f ? srgb / 12.92f : MathF.Pow( (srgb + 0.055f) / 1.055f, 2.4f );

	static float Aces( float x ) => Math.Clamp( (x * (2.51f * x + 0.03f)) / (x * (2.43f * x + 0.59f) + 0.14f), 0f, 1f );

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "projection-paint", name, test );
		void Require( bool condition, string message )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, float tol, string what ) =>
			Require( MathF.Abs( actual - expected ) <= tol, $"{what}: expected {expected}, got {actual}" );
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, got silent success" );
		}

		Check( "projection commits the photo to the right UVs", () =>
		{
			var target = new PaintCanvas( 4, 4 );
			var projection = new ProjectionImage( Photo() );
			var result = ProjectionPaint.Commit( QuadUv(), target, projection );
			Require( result.FacesProjected == 1 && result.FacesSkipped == 0,
				$"faces: projected {result.FacesProjected}, skipped {result.FacesSkipped}" );
			Require( result.TexelsWritten == 16, $"texels written: {result.TexelsWritten}, expected 16" );
			// Bilinear sample of the 2x2 photo at texel-centre UVs: (0,0) is mostly red with a
			// touch of green/blue, (3,3) is mostly white with a touch of green/blue.
			Require( Texel( target, 0, 0 ) == (199, 32, 32, 255), $"texel (0,0) is {Texel( target, 0, 0 )}, expected (199, 32, 32, 255)" );
			Require( Texel( target, 3, 3 ) == (199, 223, 223, 255), $"texel (3,3) is {Texel( target, 3, 3 )}, expected (199, 223, 223, 255)" );
		} );

		Check( "projection preview does not mutate the target", () =>
		{
			var target = new PaintCanvas( 4, 4 );
			var preview = ProjectionPaint.Preview( QuadUv(), target, new ProjectionImage( Photo() ) );
			Require( Texel( preview, 0, 0 ) == (199, 32, 32, 255), "preview did not get the photo" );
			Require( Texel( target, 0, 0 ) == (0, 0, 0, 0), "preview mutated the target canvas" );
		} );

		Check( "projection skips faces without UVs and says so", () =>
		{
			var mesh = QuadUv();
			mesh.AddFace( new[] { 0, 1, 2, 3 } );
			var target = new PaintCanvas( 4, 4 );
			var result = ProjectionPaint.Commit( mesh, target, new ProjectionImage( Photo() ) );
			Require( result.FacesProjected == 1 && result.FacesSkipped == 1,
				$"faces: projected {result.FacesProjected}, skipped {result.FacesSkipped}" );
			Require( result.TexelsWritten == 16, "the unwrapped face contributed texels it should not have" );
		} );

		Check( "stencil masks the stroke", () =>
		{
			var target = new PaintCanvas( 4, 4 );
			var mask = new PaintCanvas( 4, 4 );
			for ( var y = 0; y < 4; y++ )
			for ( var x = 0; x < 2; x++ )
				SetTexel( mask, x, y, 255, 255, 255, 255 );
			var stroke = new TexelStroke { R = 1f, G = 0f, B = 0f, Radius = 1f, Strength = 1f, Falloff = BrushFalloff.Constant };
			stroke.Path.Add( new TexelStrokePoint( 0, 2 ) );
			stroke.Path.Add( new TexelStrokePoint( 3, 2 ) );
			var painted = StencilBrush.Apply( target, stroke, mask );
			Require( painted > 0, "stencil blocked the whole stroke" );
			Require( Texel( target, 1, 2 ).A == 255 && Texel( target, 1, 2 ).R == 255, "masked-in texel was not painted" );
			Require( Texel( target, 2, 2 ).A == 0, "masked-out texel was painted" );
			Require( Texel( target, 3, 2 ).A == 0, "masked-out texel was painted" );
		} );

		Check( "clone copies from the source point", () =>
		{
			var source = new PaintCanvas( 4, 4 );
			SetTexel( source, 3, 1, 255, 0, 0 );
			var target = new PaintCanvas( 4, 4 );
			var stroke = new TexelStroke { Radius = 0.5f, Strength = 1f, Falloff = BrushFalloff.Constant };
			stroke.Path.Add( new TexelStrokePoint( 1, 1 ) );
			var painted = StencilBrush.Clone( target, source, stroke, 2, 0 );
			Require( painted == 1, $"clone painted {painted} texels, expected 1" );
			// Bilinear sample of the lone red texel at (3,1) from (1,1)+(2,0): R and A both 139.
			Require( Texel( target, 1, 1 ) == (139, 0, 0, 139), $"clone texel is {Texel( target, 1, 1 )}, expected (139, 0, 0, 139)" );
			Require( Texel( target, 0, 1 ).A == 0, "clone leaked outside the dab" );
		} );

		Check( "UDIM tiles route and paint independently", () =>
		{
			Require( UdimTileSet.TileForUv( 0.2f, 0.3f ) == 1001, "UV (0.2, 0.3) did not route to 1001" );
			Require( UdimTileSet.TileForUv( 1.2f, 0.3f ) == 1002, "UV (1.2, 0.3) did not route to 1002" );
			Require( UdimTileSet.TileForUv( 0.2f, 1.3f ) == 1011, "UV (0.2, 1.3) did not route to 1011" );
			Throws<ArgumentOutOfRangeException>( () => UdimTileSet.TileForUv( 10f, 0f ), "UV outside the UDIM square" );
			var set = new UdimTileSet();
			set.GetOrCreate( 1001, 2, 2 );
			set.GetOrCreate( 1002, 2, 2 );
			set.PaintTexel( 1001, 0, 0, 255, 0, 0, 1f );
			set.PaintTexel( 1002, 0, 0, 0, 0, 255, 1f );
			var atlas = set.CompositeAtlas( 2, 2 );
			Require( atlas.Width == 20 && atlas.Height == 20, $"atlas is {atlas.Width}x{atlas.Height}, expected 20x20" );
			Require( Texel( atlas, 0, 0 ).R == 255, "tile 1001 did not land in its atlas cell" );
			Require( Texel( atlas, 2, 0 ).B == 255, "tile 1002 did not land in its atlas cell" );
			Require( Texel( atlas, 4, 0 ).A == 0, "a tile nobody painted on shows paint in the atlas" );
		} );

		Check( "UDIM atlas round-trips edits back into the tiles", () =>
		{
			var set = new UdimTileSet();
			set.GetOrCreate( 1001, 2, 2 );
			var atlas = set.CompositeAtlas( 2, 2 );
			SetTexel( atlas, 1, 0, 0, 255, 0 );
			set.ApplyAtlas( atlas, 2, 2 );
			Require( Texel( set.Tiles[1001], 1, 0 ) == (0, 255, 0, 255), "atlas edit did not reach tile 1001" );
		} );

		Check( "UDIM edge bleed covers the seam", () =>
		{
			var set = new UdimTileSet();
			set.GetOrCreate( 1001, 4, 4 );
			set.PaintTexel( 1001, 0, 0, 255, 0, 0, 1f );
			set.BleedEdges( 1 );
			Require( Texel( set.Tiles[1001], 1, 0 ).R == 255, "one bleed pass did not reach the neighbouring texel" );
			Require( Texel( set.Tiles[1001], 2, 0 ).A == 0, "one bleed pass reached two texels out" );
		} );

		Check( "EXR export writes a readable half-float header and texels", () =>
		{
			var canvas = new PaintCanvas( 4, 2 );
			SetTexel( canvas, 0, 0, 255, 255, 255 );
			SetTexel( canvas, 1, 0, 128, 128, 128 );
			SetTexel( canvas, 2, 0, 0, 0, 0 );
			var exr = ExrExport.Write( canvas );
			Require( ExrExport.TryReadHeader( exr, out var w, out var h, out var channels, out var headerLength ),
				"the exported EXR header did not parse" );
			Require( w == 4 && h == 2, $"header size is {w}x{h}, expected 4x2" );
			Require( channels.Length == 4 && channels[0] == "A" && channels[3] == "R",
				$"channels are [{string.Join( ",", channels )}], expected A,B,G,R" );
			var y0 = BitConverter.ToInt32( exr, headerLength );
			var size0 = BitConverter.ToInt32( exr, headerLength + 4 );
			Require( y0 == 0 && size0 == 32, $"first chunk: y={y0}, size={size0}, expected 0 and 32" );
			// R row is the last of the four channel rows in the chunk.
			var r0 = ExrExport.HalfToFloat( BitConverter.ToUInt16( exr, headerLength + 8 + 24 ) );
			var r1 = ExrExport.HalfToFloat( BitConverter.ToUInt16( exr, headerLength + 8 + 26 ) );
			Near( r0, 1f, 0.001f, "white texel R" );
			Near( r1, Linear( 128f / 255f ), 0.005f, "mid-grey texel R" );
			Require( !ExrExport.TryReadHeader( new byte[] { 1, 2, 3 }, out _, out _, out _, out _ ),
				"a 3-byte file parsed as an EXR header" );
		} );

		Check( "EXR ACES tonemap applies and the projection survives a PNG round-trip", () =>
		{
			var canvas = new PaintCanvas( 4, 4 );
			ProjectionPaint.Commit( QuadUv(), canvas, new ProjectionImage( Photo() ) );
			var plain = ExrExport.Write( canvas );
			var aces = ExrExport.Write( canvas, applyAces: true );
			Require( !plain.AsSpan().SequenceEqual( aces ), "ACES export is byte-identical to the linear export" );
			Require( ExrExport.TryReadHeader( aces, out _, out _, out _, out var headerLength ), "ACES EXR header did not parse" );
			// Texel (0,0) of the projected canvas is (199, 32, 32): its R goes through
			// sRGB decode, then the ACES curve.
			var r0 = ExrExport.HalfToFloat( BitConverter.ToUInt16( aces, headerLength + 8 + 24 ) );
			Near( r0, Aces( Linear( 199f / 255f ) ), 0.01f, "ACES texel (0,0) R" );
			var png = PngWriter.ToBytesRgba( canvas.Rgba, canvas.Width, canvas.Height );
			var (rgba, pw, ph) = PngReader.Read( png );
			Require( pw == 4 && ph == 4, "PNG round-trip changed the size" );
			Require( rgba.AsSpan().SequenceEqual( canvas.Rgba ), "PNG round-trip changed the projected texels" );
		} );
	}
}
