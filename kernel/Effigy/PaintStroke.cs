using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// One point on a paint stroke's path: where the brush touched, and which way the surface faced
/// there.
///
/// WHY THE NORMAL IS STORED BESIDE THE POSITION. A paint dab is a sphere in object space, and a
/// sphere at a point on a thin wall reaches both sides of the wall — paint it from one side and the
/// far face gets painted too, which reads as paint on the wrong surface rather than as thin
/// geometry. Recording the normal lets the replay reject faces that point away from the brush, the
/// same guard NormalBake gives the bake with its MaxDistance, and the same failure when it is
/// skipped.
/// </summary>
public readonly struct PaintStrokePoint
{
	public readonly Vec3 Position;
	public readonly Vec3 Normal;

	public PaintStrokePoint( Vec3 position, Vec3 normal )
	{
		Position = position;
		Normal = normal;
	}
}

/// <summary>
/// One stroke of paint, stored as a PATH rather than as a list of dabs.
///
/// WHY A PATH. A dab list at ten floats a dab grows without bound and bloats the hand-written text
/// format; a stroke stores its settings once and a handful of path points, then regenerates its dabs
/// deterministically along the path on replay, by the same spacing rule SculptSession already uses
/// to fill in a fast drag. That smallness is what lets paint live in the .effigy text beside the
/// feature tree instead of in a binary side-car — and what makes it diff, which for somebody's model
/// matters more than it sounds.
/// </summary>
public sealed class PaintStroke
{
	/// <summary>Brush colour, straight RGBA in 0..1.</summary>
	public float R = 1f;
	public float G = 1f;
	public float B = 1f;
	public float A = 1f;

	/// <summary>Brush radius, in world units.</summary>
	public float Radius = 0.1f;

	/// <summary>How hard the stroke presses, 0..1.</summary>
	public float Strength = 1f;

	/// <summary>Reuses <see cref="BrushFalloff"/> — Smooth / Linear / Sharp / Constant. Deliberately
	/// not a second enum: the replay weights dabs through <see cref="Brush.Falloff"/> and two copies
	/// of the same list is the thing that drifts.</summary>
	public BrushFalloff Falloff = BrushFalloff.Smooth;

	/// <summary>Distance between dabs along the path, as a fraction of <see cref="Radius"/>.</summary>
	public float Spacing = 0.5f;

	/// <summary>
	/// Whether this stroke TAKES paint away rather than laying it down.
	///
	/// AN ERASE IS A STROKE, NOT AN EDIT TO THE CANVAS. The canvas is a derived artifact replayed
	/// from this list on every rebuild, so an erase that scrubbed texels directly would be undone by
	/// the next rebuild — the paint would come back on its own, which is worse than having no eraser
	/// at all. Recorded here it sits in the log in the order the hand made it, and colour blending
	/// does not commute, so that order is the whole point.
	///
	/// THE COLOUR FIELDS ARE STILL CARRIED AND STILL MEAN NOTHING HERE. An erase subtracts its
	/// coverage from the canvas's alpha and never reads R/G/B. They stay rather than moving to a
	/// second type so that one writer and one reader serve both kinds — see StudioDocument, where an
	/// erase is a different LINE KIND rather than a ninth header field, because appending a field
	/// would shift the path every older document parses from.
	/// </summary>
	public bool Erase;

	/// <summary>
	/// Whether this stroke CLONES rather than lays down its own colour: every texel it covers takes
	/// the paint found on the surface at that texel's own 3D point plus <see cref="CloneOffset"/>.
	///
	/// THE SOURCE IS AN OFFSET IN OBJECT SPACE, NOT A UV REGION. Like every other mesh stroke, a
	/// clone has to survive a rebuild and a re-unwrap, so it names where on the SURFACE it copied
	/// from, never which texels. The replay finds the nearest surface point to <c>p + offset</c>,
	/// reads the canvas there as it stood before this stroke, and composites that colour at the
	/// stroke's coverage — so a clone copies paint that is already in the log ahead of it, which is
	/// the only order that can be replayed.
	///
	/// The colour fields are ignored, as they are for an erase. <see cref="Erase"/> wins if both
	/// are set. A clone that finds no paint at its source (an unpainted region, or no surface within
	/// reach) leaves that texel alone rather than painting it with nothing.
	/// </summary>
	public bool Clone;

	/// <summary>Source minus destination, in object space: where a clone stroke reads from, relative
	/// to where it paints. Fixed for the whole stroke, set at the press. Meaningless unless
	/// <see cref="Clone"/>.</summary>
	public Vec3 CloneOffset;

	/// <summary>
	/// Whether this stroke SMUDGES rather than lays down its own colour: the first dab picks up the
	/// paint under it, and every later dab lays the carried paint down and picks a little of what it
	/// lands on back up, so the colour is dragged along the path and blends as it goes (Nomad's and
	/// Procreate's smudge).
	///
	/// DAB BY DAB, NOT ONE COMPOSITE. Every other stroke is stamped to a coverage buffer and composited
	/// once; a smudge reads the canvas each dab has just written, so it has to walk its path in order
	/// against the canvas as it stands. The live session and the replay run the same walk over the
	/// same starting canvas, so the mark on screen and the mark after a rebuild are the same mark.
	///
	/// The colour fields are ignored. <see cref="Erase"/> wins over Smudge, and Smudge over
	/// <see cref="Clone"/>. Mirror is not applied, for the same reason as a clone.
	/// </summary>
	public bool Smudge;

	/// <summary>
	/// Whether this stroke BLURS the paint already there instead of laying down its own colour: each
	/// dab pulls every texel under the brush toward the gaussian-weighted average of its neighbours,
	/// by the dab's falloff weight times <see cref="Strength"/> (and <see cref="A"/>), so the mark
	/// softens where the brush lingers. Like a smudge it reads what the last dab just wrote, so it
	/// walks its path dab by dab, live and on replay alike.
	///
	/// NEIGHBOURS ARE ONLY TEXELS INSIDE THE DAB'S OWN 3D FOOTPRINT. UV space puts other islands
	/// right next to this one; averaging across that gap would pull in paint from somewhere else on
	/// the body. Texels the sphere did not reach are not sampled.
	///
	/// The colour fields are ignored. <see cref="Erase"/> and <see cref="Smudge"/> win over Blur, and
	/// Blur wins over <see cref="Clone"/>. Mirror applies: a blur needs no source.
	/// </summary>
	public bool Blur;

	/// <summary>
	/// Which map this stroke paints: colour, one of the surface channels, or the layer's own mask.
	///
	/// A SCALAR CHANNEL READS <see cref="R"/> AS ITS VALUE. Roughness, metalness, height and the
	/// mask are one number a texel; carrying that number in the colour's red keeps one replay path,
	/// one canvas type and one writer for every channel. The session writes the value into all three
	/// of R/G/B so a scalar canvas is a grey image, which is what it exports as.
	///
	/// Strokes on different channels never touch each other's canvas, so a layer's log can interleave
	/// them freely; strokes on the same channel keep the log's order, which is what matters.
	/// </summary>
	public PaintChannel Channel = PaintChannel.Color;

	/// <summary>The path, in the order it was painted, each point carrying a position and the
	/// surface normal there. Order is the whole point: strokes are a log and colour blending does
	/// not commute.</summary>
	public readonly List<PaintStrokePoint> Path = new();
}
