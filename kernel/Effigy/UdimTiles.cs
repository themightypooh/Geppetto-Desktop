using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>UDIM multi-tile painting: one texture set split across numbered tiles, each tile its
/// own canvas at its own resolution.
///
/// Numbering is the UDIM standard: tile 1001 is the 0..1 UV square, 1002 is 1..2 in U, 1011 is
/// 0..1 in U and 1..2 in V — tile 1001 + u + 10 * v, u and v in 0..9. A UV outside the 0..10
/// square has no UDIM tile and is refused instead of silently wrapped onto 1001.
///
/// The synced 2D UV view is an atlas: <see cref="CompositeAtlas"/> lays every tile into one
/// canvas in a grid (v = 0 at the top, matching the paint canvas's row-0-is-top convention),
/// and <see cref="ApplyAtlas"/> splits an edited atlas back into the tiles. The view and the
/// tiles can never drift apart silently — the atlas is always rebuilt from the tiles, and an
/// atlas edit only lands by an explicit ApplyAtlas.
///
/// Seam handling is edge bleed: after painting, <see cref="BleedEdges"/> dilates each tile's
/// painted texels outward into the empty border (the same <see cref="PaintCanvas.Dilate"/> the
/// normal baker uses), so a sampler reading just past a tile's edge finds the painted colour
/// instead of transparent gutter.</summary>
public sealed class UdimTileSet
{
	/// <summary>Tile id (1001-based) to its canvas. Tiles are created on demand; a tile nobody
	/// painted on does not exist, and that is not an error — it is an empty tile.</summary>
	public Dictionary<int, PaintCanvas> Tiles { get; } = new();

	public int TileCount => Tiles.Count;

	/// <summary>UDIM tile id for tile coordinates (u, v), each 0..9. Throws outside the range:
	/// UDIM has no tile 101 for u = 100, and inventing one would mislabel the export.</summary>
	public static int TileId( int u, int v )
	{
		if ( u < 0 || u > 9 || v < 0 || v > 9 )
			throw new ArgumentOutOfRangeException( $"UDIM tiles cover u, v in 0..9; got ({u}, {v})." );
		return 1001 + u + 10 * v;
	}

	/// <summary>Tile coordinates back out of a tile id.</summary>
	public static (int U, int V) TileCoords( int tileId )
	{
		if ( tileId < 1001 || tileId > 1100 )
			throw new ArgumentOutOfRangeException( nameof( tileId ), $"Not a UDIM tile id: {tileId}." );
		return ((tileId - 1001) % 10, (tileId - 1001) / 10);
	}

	/// <summary>Tile id for a UV in tile units (u = 1.3 means tile column 1). Throws when the UV
	/// is outside the 0..10 square — there is no tile for it, and wrapping it onto 1001 would
	/// paint the wrong part of the model.</summary>
	public static int TileForUv( float u, float v )
	{
		var tu = (int)MathF.Floor( u );
		var tv = (int)MathF.Floor( v );
		if ( u < 0f || v < 0f || tu > 9 || tv > 9 )
			throw new ArgumentOutOfRangeException( $"UV ({u}, {v}) is outside the UDIM 0..10 square." );
		return TileId( tu, tv );
	}

	/// <summary>Split a UV into its tile and its local UV within that tile (0..1).</summary>
	public static (int TileId, float LocalU, float LocalV) Locate( float u, float v )
	{
		var tile = TileForUv( u, v );
		return (tile, u - MathF.Floor( u ), v - MathF.Floor( v ));
	}

	/// <summary>Get a tile's canvas, creating it at the given resolution if it does not exist
	/// yet. Per-tile resolution is the point: a face tile can be 8K while a body tile is 4K.
	/// A tile created at one resolution keeps it; asking for another size is refused rather
	/// than silently resampling the paint already on it.</summary>
	public PaintCanvas GetOrCreate( int tileId, int width, int height )
	{
		_ = TileCoords( tileId );
		if ( width <= 0 || height <= 0 )
			throw new ArgumentOutOfRangeException( "A tile needs a positive width and height." );
		if ( Tiles.TryGetValue( tileId, out var existing ) )
		{
			if ( existing.Width != width || existing.Height != height )
				throw new ArgumentException( $"Tile {tileId} already exists at {existing.Width}x{existing.Height}, not {width}x{height}." );
			return existing;
		}
		var canvas = new PaintCanvas( width, height );
		Tiles[tileId] = canvas;
		return canvas;
	}

	/// <summary>Paint one texel of one tile: the per-tile equivalent of painting the 0..1
	/// square. The tile must exist (see <see cref="GetOrCreate"/>).</summary>
	public void PaintTexel( int tileId, int x, int y, byte r, byte g, byte b, float weight )
	{
		if ( !Tiles.TryGetValue( tileId, out var tile ) )
			throw new ArgumentException( $"Tile {tileId} does not exist yet." );
		tile.Blend( x, y, r, g, b, weight );
	}

	/// <summary>Bleed every tile's painted edge texels outward by <paramref name="pixels"/>
	/// passes, so a seam sampler finds paint instead of transparent gutter. A tile with no
	/// paint is untouched.</summary>
	public void BleedEdges( int pixels )
	{
		if ( pixels <= 0 )
			return;
		foreach ( var tile in Tiles.Values )
			tile.Dilate( pixels );
	}

	/// <summary>One canvas holding every tile in a grid: 10 columns (u = 0..9 left to right),
	/// 10 rows (v = 0..9 top to bottom), each cell <paramref name="cellWidth"/> by
	/// <paramref name="cellHeight"/> texels. A tile larger than the cell is refused, not
	/// cropped; a missing tile leaves its cell transparent. This is the synced 2D UV view.</summary>
	public PaintCanvas CompositeAtlas( int cellWidth, int cellHeight )
	{
		if ( cellWidth <= 0 || cellHeight <= 0 )
			throw new ArgumentOutOfRangeException( "Atlas cells need a positive width and height." );
		var atlas = new PaintCanvas( cellWidth * 10, cellHeight * 10 );
		foreach ( var (tileId, tile) in Tiles )
		{
			if ( tile.Width > cellWidth || tile.Height > cellHeight )
				throw new ArgumentException( $"Tile {tileId} is {tile.Width}x{tile.Height}, larger than the {cellWidth}x{cellHeight} atlas cell." );
			var (u, v) = TileCoords( tileId );
			Blit( atlas, u * cellWidth, v * cellHeight, tile );
		}
		return atlas;
	}

	/// <summary>Split an edited atlas back into the tiles, using the same cell size as
	/// <see cref="CompositeAtlas"/>. Only tiles that already exist are written back — an atlas
	/// edit cannot conjure a tile into existence, and a cell that was never a tile stays
	/// transparent in the atlas and is ignored here. A tile's resolution is its own canvas
	/// size; the atlas cell may be larger, in which case only the tile's area is read back.</summary>
	public void ApplyAtlas( PaintCanvas atlas, int cellWidth, int cellHeight )
	{
		if ( atlas is null )
			throw new ArgumentNullException( nameof( atlas ) );
		if ( atlas.Width != cellWidth * 10 || atlas.Height != cellHeight * 10 )
			throw new ArgumentException( "This atlas was not built with this cell size." );
		foreach ( var (tileId, tile) in Tiles )
		{
			if ( tile.Width > cellWidth || tile.Height > cellHeight )
				throw new ArgumentException( $"Tile {tileId} is {tile.Width}x{tile.Height}, larger than the {cellWidth}x{cellHeight} atlas cell." );
			var (u, v) = TileCoords( tileId );
			Blit( tile, 0, 0, atlas, u * cellWidth, v * cellHeight, tile.Width, tile.Height );
		}
	}

	static void Blit( PaintCanvas dest, int destX, int destY, PaintCanvas src ) =>
		Blit( dest, destX, destY, src, 0, 0, src.Width, src.Height );

	static void Blit( PaintCanvas dest, int destX, int destY, PaintCanvas src, int srcX, int srcY, int width, int height )
	{
		for ( var y = 0; y < height; y++ )
		for ( var x = 0; x < width; x++ )
		{
			var s = ((srcY + y) * src.Width + (srcX + x)) * 4;
			var d = ((destY + y) * dest.Width + (destX + x)) * 4;
			dest.Rgba[d] = src.Rgba[s];
			dest.Rgba[d + 1] = src.Rgba[s + 1];
			dest.Rgba[d + 2] = src.Rgba[s + 2];
			dest.Rgba[d + 3] = src.Rgba[s + 3];
		}
	}
}
