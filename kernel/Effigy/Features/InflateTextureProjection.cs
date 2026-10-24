using System;
using System.Collections.Generic;
using System.IO;

namespace Effigy;

/// <summary>Roadmap step 4 of Inflate2D: project a drawing/layer image onto an inflated mesh
/// through the planar UVs Inflate2DFeature.Build already wrote (Inflate2DFeature.cs:202-203:
/// "Planar UVs over the union bounds, so a later texture-projection step has something to
/// project onto"). This file IS that later step. It reads Inflate2DFeature.Build output and
/// touches neither Inflate2DFeature.cs nor UVProjection.cs.
///
/// WHAT "PROJECT" MEANS HERE, EXACTLY. An inflated mesh's face UVs are already the drawing
/// coordinates normalized over the profile union bounds: UV (0,0) is the profile's
/// (minX, minY) corner, UV (1,1) is (maxX, maxY). So projecting a drawing image onto the mesh
/// is NOT re-projecting geometry — it is building a texture canvas in that same UV space and
/// letting the face UVs index it at render/export time. The canvas a face UV reads is the
/// drawing colour at that drawing point, which is the whole job.
///
/// COORDINATE CONVENTIONS (read before changing a sign).
/// - PaintCanvas rows: row 0 is the TOP of the image, straight (non-premultiplied) alpha.
/// - UV v increases with drawing y (Inflate2DFeature's Uv: (q.y - minY) / spanY).
/// - Therefore a UV (u, v) reads source texel column floor(u * W), row floor((1 - v) * H):
///   drawing-up becomes image-up. A drawing whose y runs DOWN (screen-space raster) needs
///   UvScale (1, -1) with UvOffset (0, 1) to flip back — that is what the affine is for,
///   not a special case.
/// - The affine, in UV space: sourceUv = UvOffset + uv * UvScale (component-wise). It is
///   how a source rasterized over DIFFERENT bounds than the profile union bounds still lands
///   right: if the source image covers drawing bounds [srcMin, srcMax] and the profiles cover
///   [profMin, profMax], set UvScale = (profMax - profMin) / (srcMax - srcMin) and
///   UvOffset = (profMin - srcMin) / (srcMax - srcMin), and every face UV reads the drawing
///   colour at its own drawing point. Identity (offset 0, scale 1) is the case where the
///   source was rasterized over exactly the profile bounds — the case a future RenderLayer
///   (lane 10) produces when handed those bounds.
///
/// WHAT THIS IS NOT. Not a brush engine (sculpt owns that), not a particle sim (particles
/// owns that), not a rasterizer (lane 10 owns RenderLayer — this lane consumes the image it
/// produces, or any PNG). Not per-point/per-stroke depth control and not Monster-Mash
/// symmetry-plane editing: both are a separate follow-up and are deliberately not here.
/// Bilinear sampling interpolates straight-alpha channels independently; on a hard
/// transparent/opaque boundary that can produce a half-alpha fringe texel, which is the
/// honest behaviour of straight-alpha filtering, not a bug to "fix" by premultiplying —
/// premultiplied data would round-trip through PngWriter wrong (see PaintCanvas).</summary>
public static class InflateTextureProjection
{
	/// <summary>How a UV between texel centres is read. Nearest is the pixel-art-safe choice:
	/// no colour is ever invented. Bilinear blends the four neighbours.</summary>
	public enum Sampling { Nearest, Bilinear }

	/// <summary>How a UV outside [0, 1] is read. Clamp pins to the edge texel; Repeat wraps
	/// (tiling), so a UV of 1.2 reads as 0.2.</summary>
	public enum Wrap { Clamp, Repeat }

	public sealed class Options
	{
		/// <summary>Output canvas size. 0 means "the source size" (per axis, independently).</summary>
		public int OutputWidth;
		public int OutputHeight;
		public Sampling Sampling = Sampling.Nearest;
		public Wrap Wrap = Wrap.Clamp;
		public Vec2 UvOffset = Vec2.Zero;
		public Vec2 UvScale = new( 1f, 1f );
	}

	static void Validate( PaintCanvas source, Options options )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		if ( options is null ) throw new ArgumentNullException( nameof( options ) );
		if ( source.Width <= 0 || source.Height <= 0 || source.Rgba.Length != source.Width * source.Height * 4 )
			throw new ArgumentException( "Source canvas is not a well-formed RGBA image." );
		if ( options.UvScale.x == 0f || options.UvScale.y == 0f )
			throw new ArgumentException( "UvScale of 0 collapses the whole drawing onto one texel; it is refused rather than silently painted." );
		if ( options.OutputWidth < 0 || options.OutputHeight < 0 )
			throw new ArgumentException( "Output size must be 0 (source size) or positive." );
	}

	static Vec2 SourceUv( Vec2 uv, Options options ) =>
		new( options.UvOffset.x + uv.x * options.UvScale.x, options.UvOffset.y + uv.y * options.UvScale.y );

	/// <summary>One texel of <paramref name="source"/> at a mesh UV, through the affine,
	/// wrap and sampling of <paramref name="options"/>. This is the probe tests use to prove
	/// a face shows the drawing region its UV claims: it shares no code path with Project's
	/// texel loop beyond Sample itself, so a loop bug cannot hide behind a probe bug.</summary>
	public static (byte R, byte G, byte B, byte A) Sample( PaintCanvas source, Vec2 uv, Options options )
	{
		Validate( source, options );
		var suv = SourceUv( uv, options );
		// UV -> texel-centre coordinates. u * W - 0.5 because texel centres sit at
		// (i + 0.5) / W; the (1 - v) is the drawing-up / row-0-top flip, see header.
		var px = suv.x * source.Width - 0.5f;
		var py = (1f - suv.y) * source.Height - 0.5f;
		if ( options.Sampling == Sampling.Nearest )
		{
			var x = Index( (int)MathF.Floor( px + 0.5f ), source.Width, options.Wrap );
			var y = Index( (int)MathF.Floor( py + 0.5f ), source.Height, options.Wrap );
			var i = (y * source.Width + x) * 4;
			return (source.Rgba[i], source.Rgba[i + 1], source.Rgba[i + 2], source.Rgba[i + 3]);
		}
		var x0 = (int)MathF.Floor( px );
		var y0 = (int)MathF.Floor( py );
		var fx = px - x0;
		var fy = py - y0;
		var c00 = Texel( source, Index( x0, source.Width, options.Wrap ), Index( y0, source.Height, options.Wrap ) );
		var c10 = Texel( source, Index( x0 + 1, source.Width, options.Wrap ), Index( y0, source.Height, options.Wrap ) );
		var c01 = Texel( source, Index( x0, source.Width, options.Wrap ), Index( y0 + 1, source.Height, options.Wrap ) );
		var c11 = Texel( source, Index( x0 + 1, source.Width, options.Wrap ), Index( y0 + 1, source.Height, options.Wrap ) );
		byte Mix( byte a, byte b, byte c, byte d ) =>
			(byte)Math.Clamp( MathF.Round( a * (1 - fx) * (1 - fy) + b * fx * (1 - fy) + c * (1 - fx) * fy + d * fx * fy ), 0f, 255f );
		return (Mix( c00.R, c10.R, c01.R, c11.R ), Mix( c00.G, c10.G, c01.G, c11.G ),
			Mix( c00.B, c10.B, c01.B, c11.B ), Mix( c00.A, c10.A, c01.A, c11.A ));
	}

	static (byte R, byte G, byte B, byte A) Texel( PaintCanvas source, int x, int y )
	{
		var i = (y * source.Width + x) * 4;
		return (source.Rgba[i], source.Rgba[i + 1], source.Rgba[i + 2], source.Rgba[i + 3]);
	}

	static int Index( int i, int n, Wrap wrap ) =>
		wrap == Wrap.Repeat ? ((i % n) + n) % n : Math.Clamp( i, 0, n - 1 );

	/// <summary>Mean of a face's corner UVs — the UV the face "shows" on average. Wall faces
	/// of an inflated mesh share two ring points, so their centre is a ring-edge midpoint,
	/// not a drawing interior point; cap faces are the ones that show the drawing.</summary>
	public static Vec2 FaceUvCentre( Face face )
	{
		if ( face is null ) throw new ArgumentNullException( nameof( face ) );
		var sum = Vec2.Zero;
		foreach ( var uv in face.UVs ) sum += uv;
		return sum / MathF.Max( 1, face.UVs.Length );
	}

	/// <summary>What one face of a mesh shows when its texture is <paramref name="source"/>:
	/// the sample at the face's UV centre. A test that checks every face this way proves the
	/// projection lands on the right region without assuming anything about triangulation.</summary>
	public static (byte R, byte G, byte B, byte A) SampleFace( PolyMesh mesh, PaintCanvas source, int faceIndex, Options options )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( faceIndex < 0 || faceIndex >= mesh.Faces.Count )
			throw new ArgumentOutOfRangeException( nameof( faceIndex ) );
		return Sample( source, FaceUvCentre( mesh.Faces[faceIndex] ), options );
	}

	/// <summary>Builds the texture canvas a mesh's face UVs index: one output texel per UV-space
	/// texel, sampled from the source through the affine/wrap/sampling options. The mesh is
	/// deliberately NOT an input — the canvas lives in UV space, and the face UVs are what
	/// bind it to geometry at render/export time. Apply is the mesh-binding entry point.</summary>
	public static PaintCanvas Project( PaintCanvas source, Options options )
	{
		Validate( source, options );
		var w = options.OutputWidth > 0 ? options.OutputWidth : source.Width;
		var h = options.OutputHeight > 0 ? options.OutputHeight : source.Height;
		var output = new PaintCanvas( w, h );
		var o = new Options
		{
			Sampling = options.Sampling, Wrap = options.Wrap,
			UvOffset = options.UvOffset, UvScale = options.UvScale,
		};
		for ( var y = 0; y < h; y++ )
			for ( var x = 0; x < w; x++ )
			{
				// Texel centre in UV space, v-up: row 0 (top) is v near 1.
				var uv = new Vec2( (x + 0.5f) / w, 1f - (y + 0.5f) / h );
				var (r, g, b, a) = Sample( source, uv, o );
				var i = (y * w + x) * 4;
				output.Rgba[i] = r; output.Rgba[i + 1] = g; output.Rgba[i + 2] = b; output.Rgba[i + 3] = a;
			}
		return output;
	}

	/// <summary>Projects <paramref name="source"/> onto <paramref name="mesh"/> by setting its
	/// live texture (mesh.Paint, the canvas the editor uploads and export bakes to PNG) to
	/// the projected canvas, and returns that canvas. Throws when the mesh has no faces —
	/// a texture with no face to index it is a silent no-op, and those are refused here.
	/// Re-running on every rebuild (as the feature below does) is what makes this durable:
	/// unlike a direct texel scrub, a rebuild re-projects instead of wiping.</summary>
	public static PaintCanvas Apply( PolyMesh mesh, PaintCanvas source, Options options )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( mesh.Faces.Count == 0 )
			throw new ArgumentException( "Cannot project a texture onto a mesh with no faces." );
		var canvas = Project( source, options );
		mesh.Paint = canvas;
		return canvas;
	}

	/// <summary>Reads a PNG file's bytes into a PaintCanvas (row 0 top, straight alpha — the
	/// same convention as PaintCanvas itself). PngReader refuses paletted and interlaced
	/// files; that refusal propagates, loudly, rather than producing a wrong-coloured canvas.</summary>
	public static PaintCanvas FromPng( byte[] png )
	{
		if ( png is null ) throw new ArgumentNullException( nameof( png ) );
		var (rgba, width, height) = PngReader.Read( png );
		var canvas = new PaintCanvas( width, height );
		Array.Copy( rgba, canvas.Rgba, rgba.Length );
		return canvas;
	}
}

/// <summary>Feature form of InflateTextureProjection, discovered by reflection like every
/// other Feature (no dispatch line needed for the feature itself — see the test-registration
/// note in the test file). Runs after an Inflate2D in the tree and paints the inflated
/// bodies' live texture from an image file: a PNG raster of the drawing (lane 10's
/// RenderLayer output, or any image rasterized over the profile bounds, with UvOffset /
/// UvScale correcting a bounds mismatch per the projector header).
///
///   add InflateTextureProjection image=drawing.png
///   add InflateTextureProjection image=drawing.png sampling=Bilinear wrap=Repeat</summary>
public sealed class InflateTextureProjectionFeature : Feature
{
	public override string TypeName => "InflateTextureProjection";
	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Body" );
	public readonly StringParam Image = new( "Image", "" );
	public readonly IntParam OutputWidth = new( "Output width", 0, 0, 4096 ) { Slider = false };
	public readonly IntParam OutputHeight = new( "Output height", 0, 0, 4096 ) { Slider = false };
	public readonly ChoiceParam Sampling = new( "Sampling", new[] { "Nearest", "Bilinear" } );
	public readonly ChoiceParam Wrap = new( "Wrap", new[] { "Clamp", "Repeat" } );
	public readonly FloatParam UvOffsetU = new( "UV offset U", 0f );
	public readonly FloatParam UvOffsetV = new( "UV offset V", 0f );
	public readonly FloatParam UvScaleU = new( "UV scale U", 1f );
	public readonly FloatParam UvScaleV = new( "UV scale V", 1f );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, Image, OutputWidth, OutputHeight, Sampling, Wrap, UvOffsetU, UvOffsetV, UvScaleU, UvScaleV };

	/// <summary>Body ids this feature last projected onto, for tests and callers that want
	/// to know what actually got textured (same pattern as Inflate2DFeature.ProfilesUsed).</summary>
	public List<string> ProjectedBodyIds { get; } = new();

	protected override void Execute( FeatureContext ctx )
	{
		ProjectedBodyIds.Clear();
		var targets = RequireBodies( ctx, Bodies );
		var path = Image.Value.Trim();
		if ( path.Length == 0 )
			Fail( "InflateTextureProjection needs an image.",
				"Neither an image path nor a raster was given, so there is nothing to project.",
				"Give image=<PNG path>, e.g. the raster lane 10's RenderLayer wrote" );
		if ( !File.Exists( path ) )
			Fail( $"InflateTextureProjection cannot find image '{path}'.",
				"The path did not resolve to a file.",
				"Check the path, or copy the PNG next to the document" );
		PaintCanvas source;
		try { source = InflateTextureProjection.FromPng( File.ReadAllBytes( path ) ); }
		catch ( Exception ex ) when ( ex is IOException or InvalidDataException or NotSupportedException )
		{
			Fail( $"InflateTextureProjection cannot read image '{path}': {ex.Message}",
				"PngReader reads 8/16-bit greyscale, RGB and RGBA PNGs; paletted and interlaced files are refused.",
				"Re-export the image as a plain RGBA PNG" );
		}
		var options = new InflateTextureProjection.Options
		{
			OutputWidth = OutputWidth.Clamped,
			OutputHeight = OutputHeight.Clamped,
			Sampling = Sampling.Value == "Bilinear" ? InflateTextureProjection.Sampling.Bilinear : InflateTextureProjection.Sampling.Nearest,
			Wrap = Wrap.Value == "Repeat" ? InflateTextureProjection.Wrap.Repeat : InflateTextureProjection.Wrap.Clamp,
			UvOffset = new Vec2( UvOffsetU.Value, UvOffsetV.Value ),
			UvScale = new Vec2( UvScaleU.Value, UvScaleV.Value ),
		};
		foreach ( var body in targets )
		{
			InflateTextureProjection.Apply( body.Mesh, source, options );
			ProjectedBodyIds.Add( body.Id );
		}
	}
}
