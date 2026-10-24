using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// One point of a texel stroke's path: a texel coordinate in the layer's canvas.
/// Row 0 is the TOP of the image, matching <see cref="PaintCanvas"/> (and the PNG writer).
/// Texel coordinates are resolution-relative only in the sense that they name texels of the
/// canvas the layer builds at: a stroke recorded at resolution 256 names texels of a 256 canvas.
/// If the layer's Resolution later changes, the same texel numbers apply at the new size —
/// the stroke does not rescale, because a canvas has no stored scale to rescale by. That is
/// the one documented limit of texel strokes versus mesh strokes, which are resolution-free.
/// </summary>
public readonly struct TexelStrokePoint
{
	public readonly float X;
	public readonly float Y;

	public TexelStrokePoint( float x, float y )
	{
		X = x;
		Y = y;
	}
}

/// <summary>
/// One stroke of texel paint, stored as a PATH of texel points rather than as a list of dabs —
/// the same smallness argument as <see cref="PaintStroke"/>: a stroke stores its settings once
/// and a handful of path points, then regenerates its dabs deterministically on replay, so it
/// lives in the .effigy text beside the feature tree instead of in a binary side-car.
///
/// WHY THIS TYPE EXISTS AT ALL. A <see cref="PaintStroke"/> is a sphere in object space: it
/// replays through the mesh and the atlas, so a re-unwrap moves it and a seam splits it. A
/// texel stroke is the opposite bargain — it names texels directly, so it survives a rebuild
/// EXACTLY, at the cost of not surviving a re-unwrap. That is the right bargain for texture
/// painting: the hand painted texels, not the surface that happened to sit under them, and
/// re-unwrapping a painted model is a destructive act the user chooses, not a rebuild side
/// effect. The desktop's TexturePaintService used to scrub texels straight into
/// <c>body.Mesh.Paint</c>, a derived canvas the next PaintFeature rebuild replaced — preview
/// only. Recording the stroke here, on the PaintFeature, is what makes texel paint durable:
/// the feature replays it on every rebuild, after the mesh strokes, so it is never wiped.
///
/// WHY NOT CONVERT TEXELS BACK TO MESH STROKES. The service's own docstring says it plainly
/// and it is still true: a stroke's path is 3D surface points with normals, and inventing those
/// from texels would paint the wrong faces (one texel can belong to several faces across a
/// seam, and a texel with no face under it has no 3D point at all). A texel stroke stays a
/// texel stroke; the two logs sit side by side on the feature and replay in order, mesh
/// strokes first, texel strokes on top.
/// </summary>
public sealed class TexelStroke
{
	/// <summary>Brush colour, straight RGBA in 0..1, same convention as <see cref="PaintStroke"/>.</summary>
	public float R = 1f;
	public float G = 1f;
	public float B = 1f;
	public float A = 1f;

	/// <summary>Brush radius, in TEXELS (not world units — that is the whole difference).</summary>
	public float Radius = 10f;

	/// <summary>How hard the stroke presses, 0..1.</summary>
	public float Strength = 1f;

	/// <summary>Reuses <see cref="BrushFalloff"/> — Smooth / Linear / Sharp / Constant.</summary>
	public BrushFalloff Falloff = BrushFalloff.Smooth;

	/// <summary>Distance between dabs along the path, as a fraction of <see cref="Radius"/>.</summary>
	public float Spacing = 0.5f;

	/// <summary>Whether this stroke TAKES paint away rather than laying it down.</summary>
	public bool Erase;

	/// <summary>Which map this stroke paints: colour, one of the surface channels, or the layer's own mask.</summary>
	public PaintChannel Channel = PaintChannel.Color;

	/// <summary>The path, in the order it was painted. Order matters: strokes are a log and colour blending does not commute.</summary>
	public readonly List<TexelStrokePoint> Path = new();
}

/// <summary>
/// Replay texel strokes onto layer maps: the texel half of a PaintFeature's rebuild.
/// Dabbing is deliberately the desktop TexturePaintService's dabbing, in the kernel, so a
/// stroke committed from the cabinet and the same stroke replayed here produce the same
/// texels: walk each path segment at Spacing intervals, blend (or erase) a falloff-weighted
/// disc at each dab. A scalar channel reads R as its value and writes it as grey, the same
/// convention the mesh-stroke replay uses.
/// </summary>
public static class TexelReplay
{
	public static void ApplyAll( PaintLayerMaps maps, IReadOnlyList<TexelStroke> strokes )
	{
		if ( maps is null || strokes is null )
			return;

		foreach ( var stroke in strokes )
			Apply( maps, stroke );
	}

	public static void Apply( PaintLayerMaps maps, TexelStroke stroke )
	{
		if ( maps is null || stroke is null || stroke.Path.Count == 0 )
			return;

		var target = maps.Get( stroke.Channel );

		if ( target is null )
		{
			// A channel nobody painted on yet has no canvas; the stroke creates it, empty,
			// so it bakes over its base value until painted — same rule ResolveChannel uses.
			target = new PaintCanvas( maps.Size, maps.Size );
			maps.Set( stroke.Channel, target );
		}

		var (r, g, b) = stroke.Channel == PaintChannel.Color
			? (ToByte( stroke.R ), ToByte( stroke.G ), ToByte( stroke.B ))
			: (ToByte( stroke.R ), ToByte( stroke.R ), ToByte( stroke.R ));

		float strength = Math.Clamp( stroke.Strength, 0f, 1f );

		if ( strength <= 0f || stroke.Radius <= 0f )
			return;

		// Dab at every path point, and fill the gaps between consecutive points at Spacing
		// intervals, so a fast drag is a continuous line, not a dotted trail. The step rule
		// is the desktop service's, so a committed cabinet stroke replays onto the same dabs.
		for ( var i = 0; i < stroke.Path.Count; i++ )
		{
			var p = stroke.Path[i];
			Dab( target, p.X, p.Y, stroke, r, g, b, strength );

			if ( i + 1 >= stroke.Path.Count )
				break;

			var q = stroke.Path[i + 1];
			var dx = q.X - p.X;
			var dy = q.Y - p.Y;
			var dist = MathF.Sqrt( dx * dx + dy * dy );
			var step = Math.Max( 1f, stroke.Radius * Math.Max( 0.05f, stroke.Spacing ) );
			var steps = Math.Max( 1, (int)MathF.Ceiling( dist / step ) );

			for ( var s = 1; s < steps; s++ )
			{
				var t = (float)s / steps;
				Dab( target, p.X + dx * t, p.Y + dy * t, stroke, r, g, b, strength );
			}
		}
	}

	static void Dab( PaintCanvas target, float x, float y, TexelStroke stroke, byte r, byte g, byte b, float strength )
	{
		var rad = (int)MathF.Ceiling( stroke.Radius );
		var cx = (int)MathF.Round( x );
		var cy = (int)MathF.Round( y );

		for ( var oy = -rad; oy <= rad; oy++ )
		for ( var ox = -rad; ox <= rad; ox++ )
		{
			var px = cx + ox;
			var py = cy + oy;

			if ( px < 0 || py < 0 || px >= target.Width || py >= target.Height )
				continue;

			var d = MathF.Sqrt( ox * ox + oy * oy );

			if ( d > stroke.Radius )
				continue;

			var w = Brush.Falloff( d / stroke.Radius, stroke.Falloff ) * strength;

			if ( w <= 0f )
				continue;

			if ( stroke.Erase )
				target.Erase( px, py, w );
			else
				target.Blend( px, py, r, g, b, w );
		}
	}

	static byte ToByte( float v ) => (byte)Math.Clamp( MathF.Round( v * 255f ), 0f, 255f );
}
