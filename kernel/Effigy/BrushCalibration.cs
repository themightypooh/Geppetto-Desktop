using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// What a brush's Strength MEANS. The brushes in <see cref="Brush"/> each take their strength in
/// their own kernel unit — Draw in inches per dab, Smooth as a share of the way to the neighbours'
/// average, Twist as radians — so the same number did wildly different amounts across brushes
/// (measured against Blender at strength 0.5: from an eighth as much to more than double). A
/// calibrated stroke (<see cref="BrushStroke.Calibrated"/>) reads Strength on one scale instead:
/// strength s does what s does in Blender's matching brush at the same radius, for every brush.
///
/// THE TABLE IS MEASURED, NOT GUESSED. Each <see cref="Scale"/> entry is the multiplier on the
/// kernel strength that makes Effigy's displacement match Blender 5.2's on the parity test (a 10 in
/// bumpy sheet, a 1.5 in brush, Blender's own strength): Effigy.Tests --parity prints it as SCALE
/// for every brush, measured 2026-10-25 against Blender 5.2.2 LTS by Effigy.Tests --parity (windowed oracle,
/// one Blender launch, bumpy 10 in sheet, 1.5 in brush, Blender strength 0.5). ParityTests holds
/// each calibrated gain near 1, so a brush change that moves the gain fails the suite until the
/// table is re-measured.
///
/// RADIUS. Blender's brushes are scale-free: a brush twice the size raises a bump twice as tall.
/// Effigy's distance brushes (Draw, Inflate…) move a fixed number of units whatever the radius.
/// So for those, the calibrated strength also scales with radius, by radius / 1.5 (the radius the
/// table was measured at); share-of-the-way brushes (Smooth, Flatten…) and the drag and angle
/// brushes don't need it.
///
/// OLD WORK KEEPS ITS LOOK. Calibration is opt-in per stroke. Uncalibrated strokes — generators,
/// part studies, every saved Stroke feature written before calibration (StrokeFeature stores which
/// it was) — keep the raw kernel units they were tuned in.
/// </summary>
public static class BrushCalibration
{
	/// <summary>The radius the table was measured at (the parity harness's brush).</summary>
	public const float ReferenceRadius = 1.5f;

	/// <summary>Kernel strength per unit of calibrated strength at the reference radius, per brush.
	/// Brushes absent here are 1 (measured at about 1, or with no reference to measure against).</summary>
	/// Measured SCALE per brush (see the class doc). Absent brushes stay at 1:
/// Crease (SCALE NaN in both its cases — no strength multiplier brings its gain to 1),
/// ClothDrag (SCALE NaN; the Blender reference barely moves), and Relax (no brush parity
/// case — the Relax row in the parity table is a mesh filter, not a brush). Slide uses the
/// Nudge case (0.143); the Relax Slide case measured 0.094. Pinch uses Pinch/Magnify
/// (0.428); Relax Pinch measured 0.150. MultiplaneScrape's shape correlation is only 0.17,
/// so its entry matches magnitude, not shape.
static readonly Dictionary<BrushKind, float> _scale = new()
	{
		{ BrushKind.Smooth, 1.754f },
		{ BrushKind.Draw, 0.862f },
		{ BrushKind.Inflate, 0.221f },
		{ BrushKind.Grab, 1.0f },
		{ BrushKind.Flatten, 0.469f },
		{ BrushKind.Pinch, 0.428f },
		{ BrushKind.Clay, 0.399f },
		{ BrushKind.ClayStrips, 0.088f },
		{ BrushKind.Layer, 0.121f },
		{ BrushKind.Blob, 0.866f },
		{ BrushKind.DrawSharp, 1.314f },
		{ BrushKind.Fill, 0.547f },
		{ BrushKind.Scrape, 0.410f },
		{ BrushKind.ElasticGrab, 0.599f },
		{ BrushKind.SnakeHook, 1.0f },
		{ BrushKind.Thumb, 1.0f },
		{ BrushKind.Slide, 0.143f },
		{ BrushKind.Twist, 0.785f },
		{ BrushKind.Boundary, 2.288f },
		{ BrushKind.Pose, 1.665f },
		{ BrushKind.ClothGrab, 0.121f },
		{ BrushKind.ClothPush, 0.210f },
		{ BrushKind.ClothInflate, 0.139f },
		{ BrushKind.ClothPinchFolds, 0.037f },
		{ BrushKind.ClothExpand, 0.008f },
		{ BrushKind.ClothTwist, 0.010f },
		{ BrushKind.Plateau, 0.079f },
		{ BrushKind.ClayThumb, 0.196f },
		{ BrushKind.MultiplaneScrape, 0.097f },
	};

	/// <summary>Brushes whose kernel strength is a distance, so calibrated strength scales with radius.</summary>
	public static bool IsDistance( BrushKind kind ) => kind is BrushKind.Draw or BrushKind.DrawSharp or BrushKind.Plateau
		or BrushKind.Inflate or BrushKind.Blob or BrushKind.Crease or BrushKind.Layer or BrushKind.Slide
		or BrushKind.ClothPush or BrushKind.ClothInflate or BrushKind.ClothDrag or BrushKind.ClothPinchFolds;

	/// <summary>The measured multiplier for a brush (1 when it has no entry).</summary>
	public static float Scale( BrushKind kind ) => _scale.TryGetValue( kind, out var s ) ? s : 1f;

	/// <summary>The kernel strength a calibrated strength stands for, for this brush at this radius.</summary>
	public static float ToKernel( BrushKind kind, float strength, float radius ) =>
		strength * Scale( kind ) * (IsDistance( kind ) ? MathF.Max( radius, 0f ) / ReferenceRadius : 1f);

	/// <summary>A sample with its strength converted from calibrated to kernel units.</summary>
	public static BrushSample ToKernel( BrushKind kind, BrushSample s ) =>
		new( s.Position, s.Normal, s.Radius, ToKernel( kind, s.Strength, s.Radius ), s.Direction );
}
