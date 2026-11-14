using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Blue/black/red in one word, the way Fusion and Onshape colour a sketch.
///
/// UnderDefined is blue: the sketch still has freedom and will move when a dimension changes.
/// FullyDefined is black: every point is pinned down by the rules, and the only thing left is
/// the solver's pin itself, which is not a freedom the user can see. OverDefined is red for
/// a different reason than Conflicting: a redundant rule is harmless but says the sketch is
/// harder to reason about than it needs to be, while a conflict means two rules disagree and
/// the geometry is a compromise neither of them asked for.
///
/// THE PIN'S ROTATION IS NOT A FREEDOM. The solver pins one point to give the sketch a frame,
/// which kills translation but not rotation, so a sketch whose only remaining freedom is the
/// spin around that pin reports 1 degree of freedom. That is still UnderDefined here: a
/// Horizontal — or any rule that fixes orientation — is what takes it to FullyDefined, and
/// calling the spinning sketch fully defined would be the lie.
public enum SketchStatus
{
	UnderDefined,
	FullyDefined,
	OverDefined,
	Conflicting
}

/// <summary>
/// What a solve found out about a sketch, in words a user can act on, plus the driven-dimension
/// half of the story: the solver reports and the diagnosis measures.
///
/// The solver owns the numbers (see <see cref="SolveResult"/>); this owns turning them into a
/// sentence and measuring the dimensions that report instead of drive. Keeping the two apart is
/// what lets the diagnosis be computed from a solve that already happened, without re-solving.
public static class SketchDiagnosis
{
	/// <summary>
	/// One line for a warning or status bar, naming the rules that are wrong instead of only
	/// saying that something is. A conflicting sketch names its violated rules; an over-defined
	/// one names its redundant rules; a fully defined one says so; an under-defined one says
	/// how many freedoms are left.
	/// </summary>
	public static string Describe( Sketch sketch, SolveResult result )
	{
		if ( sketch is null || result is null )
			return "No sketch to diagnose.";

		switch ( result.Status )
		{
			case SketchStatus.Conflicting:
				return result.ViolatedConstraints.Count > 0
					? $"Conflicting: {ListRules( sketch, result.ViolatedConstraints )} disagree — the geometry is the closest fit found."
					: $"Conflicting: the rules disagree (residual {result.Residual:0.###e0} after {result.Iterations} iterations).";

			case SketchStatus.OverDefined:
				return result.RedundantConstraintIndices.Count > 0
					? $"Over-defined: {ListRules( sketch, result.RedundantConstraintIndices )} repeat what the others already imply."
					: $"{result.RedundantConstraints} constraint(s) repeat something already implied by the others.";

			case SketchStatus.FullyDefined:
				return "Fully defined.";

			default:
				return result.DegreesOfFreedom == 1
					? "Under-defined by 1: the sketch can still be moved."
					: $"Under-defined by {result.DegreesOfFreedom}: the sketch can still be moved in {result.DegreesOfFreedom} ways.";
		}
	}

	static string ListRules( Sketch sketch, IReadOnlyList<int> indices )
	{
		var names = new List<string>();

		foreach ( var i in indices )
		{
			if ( i < 0 || i >= sketch.Constraints.Count )
				continue;

			names.Add( $"{sketch.Constraints[i].Kind} #{i}" );
		}

		return names.Count > 0 ? string.Join( ", ", names ) : "the rules";
	}

	/// <summary>
	/// Write the measured value into every driven dimension, from the points as they stand after
	/// a solve. A driven rule is never evaluated by the solver, so this is the only place its
	/// <see cref="SketchConstraint.Value"/> gets set — and a driven rule that disagrees with a
	/// driving one reads back the driven geometry, which is exactly what makes the disagreement
	/// visible instead of a fight.
	///
	/// Rules with no scalar to report (Horizontal, Coincident, EqualLength, Parallel, ...) are
	/// left alone. A rule whose points no longer exist is skipped rather than measured at zero:
	/// a stale rule reporting 0 would be a number the sketch never had.
	/// </summary>
	public static void MeasureDriven( Sketch sketch )
	{
		if ( sketch is null )
			return;

		foreach ( var c in sketch.Constraints )
		{
			if ( !c.Driven )
				continue;

			switch ( c.Kind )
			{
				case SketchConstraintKind.Distance:
					if ( Valid( sketch, c.PointA ) && Valid( sketch, c.PointB ) )
						c.Value = Distance( sketch.Points[c.PointA], sketch.Points[c.PointB] );
					break;

				case SketchConstraintKind.Radius:
					if ( Valid( sketch, c.PointA ) && Valid( sketch, c.PointB ) )
						c.Value = Distance( sketch.Points[c.PointA], sketch.Points[c.PointB] );
					break;

				case SketchConstraintKind.Diameter:
					if ( Valid( sketch, c.PointA ) && Valid( sketch, c.PointB ) )
						c.Value = 2f * Distance( sketch.Points[c.PointA], sketch.Points[c.PointB] );
					break;

				case SketchConstraintKind.Angle:
					if ( Valid( sketch, c.PointA ) && Valid( sketch, c.PointB )
						&& Valid( sketch, c.PointC ) && Valid( sketch, c.PointD ) )
					{
						var ux = (double)sketch.Points[c.PointB].x - sketch.Points[c.PointA].x;
						var uy = (double)sketch.Points[c.PointB].y - sketch.Points[c.PointA].y;
						var vx = (double)sketch.Points[c.PointD].x - sketch.Points[c.PointC].x;
						var vy = (double)sketch.Points[c.PointD].y - sketch.Points[c.PointC].y;
						c.Value = (float)( Math.Abs( Math.Atan2( ux * vy - uy * vx, ux * vx + uy * vy ) ) * 180.0 / Math.PI );
					}
					break;

				case SketchConstraintKind.Fixed:
					if ( Valid( sketch, c.PointA ) )
					{
						c.Value = sketch.Points[c.PointA].x;
						c.ValueY = sketch.Points[c.PointA].y;
					}
					break;

				default:
					break;
			}
		}
	}

	static bool Valid( Sketch sketch, int point ) => point >= 0 && point < sketch.Points.Count;

	static float Distance( Vec2 a, Vec2 b )
	{
		var dx = (double)a.x - b.x;
		var dy = (double)a.y - b.y;
		return (float)Math.Sqrt( dx * dx + dy * dy );
	}
}
