using System;

namespace Effigy;

/// <summary>SpotLight-style projection painting: a photo is placed over the mesh's UVs, warped
/// and positioned without touching the paint (preview), then committed into the paint canvas.
///
/// UV CONVENTION, matching <see cref="PaintReplay"/>: a UV (u, v) names texel
/// (floor(u * Width), floor(v * Height)) — v = 0 is the TOP row, no flip. The placement quad
/// lives in the same UV space, so the identity placement puts the photo exactly where the UVs
/// say it goes. Faces whose UV triangle is degenerate (all corners equal, or zero area — the
/// shape an un-unwrapped face has) are skipped and counted, never painted with a guessed UV.
/// Only PNG photos are accepted: <see cref="PngReader"/> is the kernel's only image reader,
/// and anything else is refused clearly instead of half-decoded.</summary>
public sealed class ProjectionImage
{
	public PaintCanvas Image { get; }

	/// <summary>Where the photo sits in UV space. Identity covers the 0..1 square exactly.</summary>
	public ProjectionPlacement Placement { get; set; } = ProjectionPlacement.Identity;

	/// <summary>How hard the projection presses, 0..1. 1 replaces the texel's colour with the
	/// sampled photo colour (source-over at full weight); lower values blend toward it.</summary>
	public float Strength { get; set; } = 1f;

	public ProjectionImage( PaintCanvas image )
	{
		Image = image ?? throw new ArgumentNullException( nameof( image ) );
	}

	/// <summary>Decode a PNG photo into a projection image. Throws on anything that is not a
	/// readable PNG — a JPEG, an EXR, or a truncated file is refused, not guessed at.</summary>
	public static ProjectionImage FromPng( byte[] png )
	{
		if ( png is null )
			throw new ArgumentNullException( nameof( png ) );
		var (rgba, width, height) = PngReader.Read( png );
		var canvas = new PaintCanvas( width, height );
		Array.Copy( rgba, canvas.Rgba, rgba.Length );
		return new ProjectionImage( canvas );
	}
}

/// <summary>Where a photo sits in UV space: the four corners of the image, in UV coordinates.
/// Image row 0 (the top of the photo) is the TopLeft/TopRight edge. A rectangle is an affine
/// placement (translate/scale/rotate/shear); moving one corner off the rectangle makes it a
/// bilinear warp, which is what "warp-before-commit" means here: the artist drags corners until
/// the photo sits right, then commits.</summary>
public sealed class ProjectionPlacement
{
	public Vec2 TopLeft { get; set; } = new( 0, 0 );
	public Vec2 TopRight { get; set; } = new( 1, 0 );
	public Vec2 BottomRight { get; set; } = new( 1, 1 );
	public Vec2 BottomLeft { get; set; } = new( 0, 1 );

	public static ProjectionPlacement Identity => new();

	/// <summary>A rectangle placement covering the UV rect (u0, v0)..(u1, v1).</summary>
	public static ProjectionPlacement Fit( float u0, float v0, float u1, float v1 ) => new()
	{
		TopLeft = new Vec2( u0, v0 ),
		TopRight = new Vec2( u1, v0 ),
		BottomRight = new Vec2( u1, v1 ),
		BottomLeft = new Vec2( u0, v1 ),
	};

	/// <summary>Image UV (iu, iv in 0..1) to mesh UV, bilinear through the quad.</summary>
	public Vec2 Map( float iu, float iv )
	{
		var a = TopLeft * ((1f - iu) * (1f - iv));
		var b = TopRight * (iu * (1f - iv));
		var c = BottomRight * (iu * iv);
		var d = BottomLeft * ((1f - iu) * iv);
		return a + b + c + d;
	}

	/// <summary>Mesh UV back to image UV, by Newton iteration on the bilinear map. False when
	/// the UV is outside the quad or the quad is degenerate (zero area, or a folded corner) —
	/// the caller skips that texel instead of painting a guessed colour.</summary>
	public bool TryInverse( Vec2 uv, out Vec2 imageUv )
	{
		imageUv = new Vec2( 0.5f, 0.5f );
		var iu = 0.5f;
		var iv = 0.5f;
		for ( var i = 0; i < 8; i++ )
		{
			var p = Map( iu, iv );
			var fx = p.x - uv.x;
			var fy = p.y - uv.y;
			if ( MathF.Abs( fx ) < 1e-6f && MathF.Abs( fy ) < 1e-6f )
			{
				imageUv = new Vec2( iu, iv );
				return iu >= -1e-4f && iu <= 1f + 1e-4f && iv >= -1e-4f && iv <= 1f + 1e-4f;
			}
			// Jacobian of the bilinear map at (iu, iv).
			var du = new Vec2(
				(TopRight.x - TopLeft.x) * (1f - iv) + (BottomRight.x - BottomLeft.x) * iv,
				(TopRight.y - TopLeft.y) * (1f - iv) + (BottomRight.y - BottomLeft.y) * iv );
			var dv = new Vec2(
				(BottomLeft.x - TopLeft.x) * (1f - iu) + (BottomRight.x - TopRight.x) * iu,
				(BottomLeft.y - TopLeft.y) * (1f - iu) + (BottomRight.y - TopRight.y) * iu );
			var det = du.x * dv.y - du.y * dv.x;
			if ( MathF.Abs( det ) < 1e-12f )
				return false;
			var stepU = (fx * dv.y - fy * dv.x) / det;
			var stepV = (du.x * fy - du.y * fx) / det;
			iu -= stepU;
			iv -= stepV;
			if ( iu < -0.5f || iu > 1.5f || iv < -0.5f || iv > 1.5f )
				return false;
		}
		var end = Map( iu, iv );
		if ( MathF.Abs( end.x - uv.x ) > 1e-4f || MathF.Abs( end.y - uv.y ) > 1e-4f )
			return false;
		imageUv = new Vec2( iu, iv );
		return iu >= 0f && iu <= 1f && iv >= 0f && iv <= 1f;
	}
}

/// <summary>What a projection commit did, so the caller can report it honestly instead of
/// claiming a photo "landed" when its faces had no UVs to land on.</summary>
public sealed class ProjectionResult
{
	public int TexelsWritten;
	public int FacesProjected;
	public int FacesSkipped;
}

/// <summary>Projection painting onto a mesh's paint canvas, through the mesh's UVs.</summary>
public static class ProjectionPaint
{
	/// <summary>The non-destructive preview: the photo applied to a COPY of the target. The
	/// target is untouched, so the artist warps and re-previews until it sits right.</summary>
	public static PaintCanvas Preview( PolyMesh mesh, PaintCanvas target, ProjectionImage projection )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );
		if ( target is null )
			throw new ArgumentNullException( nameof( target ) );
		var preview = target.Clone();
		Commit( mesh, preview, projection );
		return preview;
	}

	/// <summary>Commit the photo into <paramref name="target"/> in place. Each face's UV
	/// triangle is rasterised into the canvas; a texel inside the triangle samples the photo
	/// at the placement's inverse UV and blends it in at the projection strength. A texel is
	/// written at most once per face: a quad's fan triangles share their diagonal, and the
	/// texels on it belong to both triangles, so without dedup they would be blended (and
	/// counted) twice — 20 writes for a 16-texel quad, and a visible over-blend at partial
	/// strength. The dedup is per face, not per commit: two faces whose UVs genuinely overlap
	/// both paint the texel, the later face over the earlier one.</summary>
	public static ProjectionResult Commit( PolyMesh mesh, PaintCanvas target, ProjectionImage projection )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );
		if ( target is null )
			throw new ArgumentNullException( nameof( target ) );
		if ( projection is null )
			throw new ArgumentNullException( nameof( projection ) );

		var result = new ProjectionResult();
		var strength = Math.Clamp( projection.Strength, 0f, 1f );
		if ( strength <= 0f )
			return result;

		// One stamp per target texel, one stamp value per face: a texel already written by
		// this face's earlier fan triangle is skipped. One int per texel — the same order of
		// memory as the canvas itself — allocated once per commit, not per face.
		var written = new int[target.Width * target.Height];
		var stamp = 0;
		foreach ( var face in mesh.Faces )
		{
			stamp++;
			if ( face is null || face.Indices.Length < 3 || face.UVs is null || face.UVs.Length != face.Indices.Length )
			{
				result.FacesSkipped++;
				continue;
			}
			// Rasterise the UV polygon as a fan from corner 0; a degenerate (zero-area) UV
			// triangle is an unwrapped face and is skipped, not painted.
			var projected = false;
			for ( var c = 1; c + 1 < face.UVs.Length; c++ )
				projected |= RasteriseTriangle( face.UVs[0], face.UVs[c], face.UVs[c + 1], target, projection, strength, result, written, stamp );
			if ( projected )
				result.FacesProjected++;
			else
				result.FacesSkipped++;
		}
		return result;
	}

	static bool RasteriseTriangle( Vec2 a, Vec2 b, Vec2 c, PaintCanvas target, ProjectionImage projection, float strength, ProjectionResult result, int[] written, int stamp )
	{
		var area2 = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
		if ( MathF.Abs( area2 ) < 1e-12f )
			return false;

		var minU = MathF.Min( a.x, MathF.Min( b.x, c.x ) );
		var maxU = MathF.Max( a.x, MathF.Max( b.x, c.x ) );
		var minV = MathF.Min( a.y, MathF.Min( b.y, c.y ) );
		var maxV = MathF.Max( a.y, MathF.Max( b.y, c.y ) );
		var x0 = Math.Max( 0, (int)MathF.Floor( minU * target.Width ) );
		var x1 = Math.Min( target.Width - 1, (int)MathF.Floor( maxU * target.Width ) );
		var y0 = Math.Max( 0, (int)MathF.Floor( minV * target.Height ) );
		var y1 = Math.Min( target.Height - 1, (int)MathF.Floor( maxV * target.Height ) );

		var wrote = false;
		for ( var y = y0; y <= y1; y++ )
		for ( var x = x0; x <= x1; x++ )
		{
			// Texel-centre UV, same convention as the paint replay (v = 0 is the top row).
			var u = (x + 0.5f) / target.Width;
			var v = (y + 0.5f) / target.Height;
			var (w0, w1, w2) = Barycentric( a, b, c, u, v, area2 );
			if ( w0 < -1e-6f || w1 < -1e-6f || w2 < -1e-6f )
				continue;
			if ( !projection.Placement.TryInverse( new Vec2( u, v ), out var imageUv ) )
				continue;
			// Already written by an earlier fan triangle of this same face (the shared
			// diagonal): blend and count it once, not twice.
			var ti = y * target.Width + x;
			if ( written[ti] == stamp )
				continue;
			written[ti] = stamp;
			var (r, g, blue, _) = SampleBilinear( projection.Image, imageUv.x, imageUv.y );
			target.Blend( x, y, r, g, blue, strength );
			result.TexelsWritten++;
			wrote = true;
		}
		return wrote;
	}

	static (float, float, float) Barycentric( Vec2 a, Vec2 b, Vec2 c, float u, float v, float area2 )
	{
		var w0 = ((b.x - u) * (c.y - v) - (b.y - v) * (c.x - u)) / area2;
		var w1 = ((c.x - u) * (a.y - v) - (c.y - v) * (a.x - u)) / area2;
		var w2 = 1f - w0 - w1;
		return (w0, w1, w2);
	}

	/// <summary>Bilinear sample of a canvas at image UV (0..1). Edges clamp: a photo has no
	/// texels past its border, and wrapping would smear the opposite edge across the seam.</summary>
	internal static (byte R, byte G, byte B, byte A) SampleBilinear( PaintCanvas image, float iu, float iv )
	{
		var fx = Math.Clamp( iu, 0f, 1f ) * (image.Width - 1);
		var fy = Math.Clamp( iv, 0f, 1f ) * (image.Height - 1);
		var x0 = (int)MathF.Floor( fx );
		var y0 = (int)MathF.Floor( fy );
		var x1 = Math.Min( x0 + 1, image.Width - 1 );
		var y1 = Math.Min( y0 + 1, image.Height - 1 );
		var tx = fx - x0;
		var ty = fy - y0;
		byte Mix( int channel )
		{
			var c00 = image.Rgba[(y0 * image.Width + x0) * 4 + channel];
			var c10 = image.Rgba[(y0 * image.Width + x1) * 4 + channel];
			var c01 = image.Rgba[(y1 * image.Width + x0) * 4 + channel];
			var c11 = image.Rgba[(y1 * image.Width + x1) * 4 + channel];
			var top = c00 * (1f - tx) + c10 * tx;
			var bottom = c01 * (1f - tx) + c11 * tx;
			return (byte)MathF.Round( top * (1f - ty) + bottom * ty );
		}
		return (Mix( 0 ), Mix( 1 ), Mix( 2 ), Mix( 3 ));
	}
}
