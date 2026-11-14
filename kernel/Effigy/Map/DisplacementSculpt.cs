using System;
using System.Collections.Generic;

namespace Effigy.Map;

/// <summary>
/// Which sculpt operation a dab performs on a displacement grid.
/// </summary>
public enum DisplacementSculptMode
{
	/// <summary>Push vertices out along the raise axis (Hammer Paint Geometry, Clay/Draw).</summary>
	Raise,
	/// <summary>Pull vertices in along the raise axis (Hammer Paint Geometry, Scrape).</summary>
	Lower,
	/// <summary>Move each vertex toward the average of its grid neighbours.</summary>
	Smooth,
	/// <summary>Move vertices toward the height under the dab centre (Hammer Flatten).</summary>
	Flatten,
	/// <summary>Blend the per-vertex material alpha toward <see cref="DisplacementSculptOptions.PaintAlphaTarget"/>
	/// (Hammer Paint Alpha: 0 is the first material, 1 the second).</summary>
	PaintAlpha,
}

/// <summary>
/// Which world direction a Raise/Lower dab pushes along. Displacement offsets are scalars
/// along the face normal, so a requested axis only contributes its component along that
/// normal: raising along X on a Z-facing displacement is a (correct) no-op, raising along X
/// on an X-facing displacement is full strength. The signed dot product is the factor.
/// </summary>
public enum DisplacementRaiseAxis
{
	FaceNormal,
	X,
	Y,
	Z,
}

/// <summary>
/// The binding surface a displacement must expose for sculpting.
///
/// Bound to the landed <c>Displacement</c> (hammer-displacement-data-model) by
/// <see cref="DisplacementAdapter"/> below: <c>Displacement</c> already exposes
/// everything here under its own names — <c>GridSize</c> for <see cref="Side"/>,
/// <c>Alphas</c> for <see cref="Alpha"/>, <c>VertexPosition(row, col)</c>,
/// row-major <c>Offsets</c> — except <c>Sew</c>, which reports a bool where this
/// surface counts matched vertices (the adapter reports its own edge's vertex
/// count when a sew lands). The interface stays because it is also the seam the
/// tests use: the in-test stand-in <c>TestDisplacement</c> implements the same
/// grid semantics without needing a brush face. No competing displacement type
/// is invented here; the adapter is the only binding, and it is thin.
/// </summary>
public interface IDisplacementSurface
{
	/// <summary>Subdivision power, 2..4. Side length is (1 &lt;&lt; Power) + 1.</summary>
	int Power { get; }

	/// <summary>Vertices per side; must equal (1 &lt;&lt; Power) + 1.</summary>
	int Side { get; }

	/// <summary>World position of the (row 0, col 0) corner of the undisplaced base grid.</summary>
	Vec3 Origin { get; }

	/// <summary>Full edge vector from the (row 0, col 0) corner to the (row 0, last) corner.</summary>
	Vec3 UAxis { get; }

	/// <summary>Full edge vector from the (row 0, col 0) corner to the (last, col 0) corner.</summary>
	Vec3 VAxis { get; }

	/// <summary>Unit face normal; offsets are scalars along this direction.</summary>
	Vec3 Normal { get; }

	/// <summary>Row-major offsets, Side * Side, one scalar per vertex along <see cref="Normal"/>.</summary>
	float[] Offsets { get; }

	/// <summary>Row-major blend alpha, Side * Side, 0..1 (0 = first material, 1 = second).</summary>
	float[] Alpha { get; }

	/// <summary>Displaced world position of the vertex at (row, col).</summary>
	Vec3 VertexPosition( int row, int col );

	/// <summary>
	/// Match shared edges with <paramref name="other"/> and equalise the offsets and alpha on
	/// them (both sides end up holding the mean), so a stroke on one displacement does not tear
	/// it away from its sewn neighbour. Returns the number of vertices matched; 0 when the two
	/// displacements share no edge. Mirrors the spec's <c>Displacement.Sew(other)</c>.
	/// </summary>
	int Sew( IDisplacementSurface other );
}

/// <summary>
/// A real <see cref="Displacement"/> behind the <see cref="IDisplacementSurface"/>
/// seam, so the sculpt engine drives the landed type with no logic of its own.
/// The arrays are the displacement's own (sculpting writes in place, as
/// <c>Displacement</c> documents); <c>Origin</c>/<c>UAxis</c>/<c>VAxis</c> are the
/// face's corner frame (corner 0, corner 1 - corner 0, corner 3 - corner 0).
/// Sewing delegates to <see cref="Displacement.Sew(Displacement)"/> and reports
/// this side's equalised vertex count (its edge length) when a sew lands, 0
/// when there is no shared coplanar edge. Sewing to a surface that is not a
/// <c>Displacement</c> throws: shared-edge matching across different surface
/// kinds has no defined semantics.
///
/// NAME COLLISION, integrator's job, not this adapter's: the brush core's face
/// type is <c>Effigy.MapBrushFace</c> (plane + FaceMaterial, vertices solved
/// from planes, no stored corners) while <c>Displacement</c> binds
/// <c>Effigy.Map.MapBrushFace</c> (4 stored corners). Same name, different
/// namespaces, different types — <c>Displacement.Create</c> takes the latter,
/// so building one from a brush face's solved polygon belongs to the
/// integrator, not here.
/// </summary>
public sealed class DisplacementAdapter : IDisplacementSurface
{
	public DisplacementAdapter( Displacement inner )
	{
		Inner = inner ?? throw new ArgumentNullException( nameof( inner ) );
	}

	/// <summary>The wrapped displacement.</summary>
	public Displacement Inner { get; }

	public int Power => Inner.Power;
	public int Side => Inner.GridSize;
	public Vec3 Origin => Inner.Face.Corners[0];
	public Vec3 UAxis => Inner.Face.Corners[1] - Inner.Face.Corners[0];
	public Vec3 VAxis => Inner.Face.Corners[3] - Inner.Face.Corners[0];
	public Vec3 Normal => Inner.Normal;
	public float[] Offsets => Inner.Offsets;
	public float[] Alpha => Inner.Alphas;
	public Vec3 VertexPosition( int row, int col ) => Inner.VertexPosition( row, col );

	public int Sew( IDisplacementSurface other )
	{
		if ( other is null )
			throw new ArgumentNullException( nameof( other ) );
		if ( other is not DisplacementAdapter wrapped )
			throw new ArgumentException( "a Displacement can only sew to another Displacement", nameof( other ) );
		return Inner.Sew( wrapped.Inner ) ? Inner.GridSize : 0;
	}
}
public sealed class DisplacementSculptOptions
{
	/// <summary>Which axis Raise/Lower pushes along. Default is the face normal.</summary>
	public DisplacementRaiseAxis RaiseAxis = DisplacementRaiseAxis.FaceNormal;

	/// <summary>
	/// Where Paint Alpha dabs move the blend: 1 paints the second material in, 0 paints it
	/// back out. Must be 0..1.
	/// </summary>
	public float PaintAlphaTarget = 1f;

	/// <summary>When true (default), sewn neighbours are re-sewn after every dab.</summary>
	public bool AutoSew = true;
}

/// <summary>What one dab changed.</summary>
public readonly struct DisplacementDabResult
{
	public readonly int VerticesTouched;
	public readonly float MaxOffsetDelta;
	public readonly float MaxAlphaDelta;

	public DisplacementDabResult( int verticesTouched, float maxOffsetDelta, float maxAlphaDelta )
	{
		VerticesTouched = verticesTouched;
		MaxOffsetDelta = maxOffsetDelta;
		MaxAlphaDelta = maxAlphaDelta;
	}
}

/// <summary>What one whole stroke changed.</summary>
public sealed class DisplacementSculptResult
{
	/// <summary>Dabs applied (samples times symmetry copies).</summary>
	public int DabsApplied;
	/// <summary>Vertices touched, summed over dabs (one vertex can count more than once).</summary>
	public int VerticesTouched;
	/// <summary>Largest absolute offset change any one vertex saw in any dab.</summary>
	public float MaxOffsetDelta;
	/// <summary>Largest absolute alpha change any one vertex saw in any dab.</summary>
	public float MaxAlphaDelta;
	/// <summary>Shared-edge vertices equalised by auto-sew, summed over dabs.</summary>
	public int SewMatches;
	/// <summary>Symmetry copies whose dab touched no vertex at all (landed off the grid).</summary>
	public int CopiesMissed;
}

/// <summary>
/// Displacement sculpt modes on the existing sculpt brush engine (Hammer Paint Geometry /
/// Paint Alpha, backend only, no UI).
///
/// Drives <see cref="BrushStroke"/> samples onto a displacement's offsets/alpha:
/// radius and falloff come from the sample and <see cref="BrushFalloff"/>, the stamp from
/// <see cref="BrushStroke.Alpha"/>, mirror/radial symmetry from
/// <see cref="BrushStroke.SymmetryCopies"/> (the shared Brush.cs path built on
/// <see cref="Brush.Mirror(Vec3, MirrorAxis)"/>). <c>Brush.cs</c> is reused untouched.
/// After every dab, each registered sewn neighbour is re-sewn, so a stroke on one side of
/// a shared edge cannot tear it away from the other side. Callers holding a real
/// <see cref="Displacement"/> use the <c>Displacement</c> overloads, which wrap it in a
/// <see cref="DisplacementAdapter"/>; the engine itself works on
/// <see cref="IDisplacementSurface"/>.
///
/// Strength is in the same world units as the offsets (kernel geometry is inches, Z-up);
/// the stroke's <c>Calibrated</c> flag is not applied — there is no calibration table for
/// displacement offsets, and silently rescaling a strength would lie about the height.
/// </summary>
public static class DisplacementSculpt
{
	/// <summary>Vertices per side of a power-p grid: (2^p) + 1. Power must be 2..4 per spec.
	/// Delegates to <see cref="Displacement.GridSizeForPower(int)"/>: one source of truth.</summary>
	public static int GridSide( int power ) => Displacement.GridSizeForPower( power );

	/// <summary>Total vertices of a power-p grid.</summary>
	public static int GridVertexCount( int power )
	{
		var side = GridSide( power );
		return side * side;
	}

	/// <summary>
	/// Which displacement mode a sculpt <see cref="BrushKind"/> means. Paint Alpha has no
	/// BrushKind of its own: call <see cref="ApplyStroke(IDisplacementSurface, BrushStroke, DisplacementSculptMode, DisplacementSculptOptions, IReadOnlyList{IDisplacementSurface})"/>
	/// with <see cref="DisplacementSculptMode.PaintAlpha"/> explicitly. Kinds with no
	/// displacement meaning (Grab, Pinch, Twist, Slide, Pose, cloth, Snake Hook, ...) throw:
	/// silently mapping a drag-the-surface brush onto a normal-offset grid would corrupt the
	/// grid instead of sculpting it.
	/// </summary>
	public static DisplacementSculptMode ModeForKind( BrushKind kind ) => kind switch
	{
		BrushKind.Draw or BrushKind.DrawSharp or BrushKind.Clay or BrushKind.ClayStrips
			or BrushKind.Inflate or BrushKind.Blob or BrushKind.Layer or BrushKind.Plateau
			or BrushKind.Fill => DisplacementSculptMode.Raise,
		BrushKind.Scrape => DisplacementSculptMode.Lower,
		BrushKind.Smooth => DisplacementSculptMode.Smooth,
		BrushKind.Flatten => DisplacementSculptMode.Flatten,
		_ => throw new ArgumentException( $"brush kind {kind} has no displacement sculpt mode", nameof( kind ) ),
	};

	/// <summary>
	/// Structural problems with a grid, empty when it is sound: power out of 2..4, Side not
	/// matching the power, Offsets/Alpha missing or the wrong length, a non-unit normal, or
	/// alpha outside 0..1. Does not judge sculpt quality, only that the grid is well formed.
	/// </summary>
	public static string[] ValidateSurface( IDisplacementSurface target )
	{
		if ( target is null )
			throw new ArgumentNullException( nameof( target ) );

		var errors = new List<string>();

		if ( target.Power < 2 || target.Power > 4 )
			errors.Add( $"power {target.Power} is outside 2..4" );

		var side = target.Power >= 2 && target.Power <= 4 ? GridSide( target.Power ) : -1;
		if ( side > 0 && target.Side != side )
			errors.Add( $"side {target.Side} does not match power {target.Power} (expected {side})" );

		if ( target.Offsets is null )
			errors.Add( "offsets are missing" );
		else if ( target.Side > 0 && target.Offsets.Length != target.Side * target.Side )
			errors.Add( $"offsets length {target.Offsets.Length} does not match {target.Side}x{target.Side}" );

		if ( target.Alpha is null )
			errors.Add( "alpha is missing" );
		else if ( target.Side > 0 && target.Alpha.Length != target.Side * target.Side )
			errors.Add( $"alpha length {target.Alpha.Length} does not match {target.Side}x{target.Side}" );

		var normalLength = target.Normal.Length;
		if ( MathF.Abs( normalLength - 1f ) > 1e-3f )
			errors.Add( $"normal is not unit length ({normalLength:0.###})" );

		if ( target.Alpha is not null )
			for ( var i = 0; i < target.Alpha.Length; i++ )
				if ( target.Alpha[i] < -1e-6f || target.Alpha[i] > 1f + 1e-6f )
				{
					errors.Add( $"alpha[{i}] is {target.Alpha[i]:0.###}, outside 0..1" );
					break;
				}

		return errors.ToArray();
	}

	static void CheckSurface( IDisplacementSurface target )
	{
		var errors = ValidateSurface( target );
		if ( errors.Length > 0 )
			throw new ArgumentException( "displacement grid is not well formed: " + string.Join( "; ", errors ) );
	}

	/// <summary>
	/// Falloff weight of a vertex at <paramref name="distance"/> from a dab of
	/// <paramref name="radius"/>: 0 at or beyond the radius (even for Constant falloff, which
	/// <see cref="Brush.Falloff(float, BrushFalloff)"/> reports as 1 at t = 1 — the brush
	/// footprint ends at the radius, it does not bleed past it), otherwise the Brush.cs
	/// falloff curve. Reuses <see cref="Brush.Falloff(float, BrushFalloff)"/> untouched.
	/// </summary>
	public static float FalloffWeight( float distance, float radius, BrushFalloff falloff )
	{
		if ( radius <= 0f )
			throw new ArgumentException( "radius must be positive", nameof( radius ) );
		if ( distance < 0f || distance >= radius )
			return 0f;
		return Brush.Falloff( distance / radius, falloff );
	}

	/// <summary>
	/// Apply one whole stroke: every sample, every symmetry copy of every sample, in order.
	/// The mode comes from the caller (or from <see cref="ModeForKind(BrushKind)"/> via the
	/// two-argument overload). When <paramref name="sewnNeighbours"/> is given and
	/// <see cref="DisplacementSculptOptions.AutoSew"/> is on, the target is re-sewn with each
	/// neighbour after every dab — so after every stroke, and at no point during it, can a
	/// shared edge disagree.
	/// </summary>
	public static DisplacementSculptResult ApplyStroke(
		IDisplacementSurface target,
		BrushStroke stroke,
		DisplacementSculptMode mode,
		DisplacementSculptOptions options = null,
		IReadOnlyList<IDisplacementSurface> sewnNeighbours = null )
	{
		if ( target is null )
			throw new ArgumentNullException( nameof( target ) );
		if ( stroke is null )
			throw new ArgumentNullException( nameof( stroke ) );
		CheckSurface( target );
		options ??= new DisplacementSculptOptions();
		if ( mode == DisplacementSculptMode.PaintAlpha
			&& (options.PaintAlphaTarget < 0f || options.PaintAlphaTarget > 1f) )
			throw new ArgumentException( "PaintAlphaTarget must be 0..1", nameof( options ) );

		var result = new DisplacementSculptResult();

		// SymmetryCopies is the Brush.cs path (BrushSymmetry.Copies, built on MirrorAxis and
		// Brush.Mirror): identity first, then every mirror/radial copy. A copy that lands off
		// the grid touches nothing and is counted, not silently dropped from the report.
		// Turning brushes flip their sign in a reflected copy (Brush.Copy); no displacement
		// mode turns, so strength is carried unchanged into every copy.
		foreach ( var sample in stroke.Samples )
		{
			foreach ( var copy in stroke.SymmetryCopies() )
			{
				var moved = new BrushSample(
					copy.Apply( sample.Position ),
					copy.Apply( sample.Normal ),
					sample.Radius,
					sample.Strength,
					copy.Apply( sample.Direction ),
					sample.AlphaRotation );
				var dab = ApplyDab( target, moved, mode, stroke.Falloff, stroke.Alpha, options );
				result.DabsApplied++;
				result.VerticesTouched += dab.VerticesTouched;
				result.MaxOffsetDelta = MathF.Max( result.MaxOffsetDelta, dab.MaxOffsetDelta );
				result.MaxAlphaDelta = MathF.Max( result.MaxAlphaDelta, dab.MaxAlphaDelta );
				if ( dab.VerticesTouched == 0 )
					result.CopiesMissed++;

				if ( options.AutoSew && sewnNeighbours is not null )
					foreach ( var neighbour in sewnNeighbours )
					{
						if ( neighbour is null )
							continue;
						result.SewMatches += target.Sew( neighbour );
					}
			}
		}

		return result;
	}

	/// <summary>Same as the five-argument overload, with the mode derived from the stroke's kind.</summary>
	public static DisplacementSculptResult ApplyStroke(
		IDisplacementSurface target,
		BrushStroke stroke,
		DisplacementSculptOptions options = null,
		IReadOnlyList<IDisplacementSurface> sewnNeighbours = null ) =>
		ApplyStroke( target, stroke, ModeForKind( stroke?.Kind ?? throw new ArgumentNullException( nameof( stroke ) ) ), options, sewnNeighbours );

	/// <summary>Apply a stroke to a real <see cref="Displacement"/>, via a <see cref="DisplacementAdapter"/>.</summary>
	public static DisplacementSculptResult ApplyStroke(
		Displacement target,
		BrushStroke stroke,
		DisplacementSculptMode mode,
		DisplacementSculptOptions options = null,
		IReadOnlyList<Displacement> sewnNeighbours = null )
	{
		if ( target is null )
			throw new ArgumentNullException( nameof( target ) );
		List<IDisplacementSurface> neighbours = null;
		if ( sewnNeighbours is not null )
		{
			neighbours = new List<IDisplacementSurface>();
			foreach ( var neighbour in sewnNeighbours )
				neighbours.Add( neighbour is null ? null : new DisplacementAdapter( neighbour ) );
		}
		return ApplyStroke( new DisplacementAdapter( target ), stroke, mode, options, neighbours );
	}

	/// <summary>Same as the Displacement overload above, with the mode derived from the stroke's kind.</summary>
	public static DisplacementSculptResult ApplyStroke(
		Displacement target,
		BrushStroke stroke,
		DisplacementSculptOptions options = null,
		IReadOnlyList<Displacement> sewnNeighbours = null ) =>
		ApplyStroke( target, stroke, ModeForKind( stroke?.Kind ?? throw new ArgumentNullException( nameof( stroke ) ) ), options, sewnNeighbours );

	/// <summary>Apply one dab to a real <see cref="Displacement"/>, via a <see cref="DisplacementAdapter"/>.</summary>
	public static DisplacementDabResult ApplyDab(
		Displacement target,
		BrushSample sample,
		DisplacementSculptMode mode,
		BrushFalloff falloff,
		BrushAlpha stamp,
		DisplacementSculptOptions options = null )
	{
		if ( target is null )
			throw new ArgumentNullException( nameof( target ) );
		return ApplyDab( new DisplacementAdapter( target ), sample, mode, falloff, stamp, options );
	}

	/// <summary>
	/// Apply one dab. Smooth and Flatten read the offsets from a snapshot taken at dab start,
	/// so a vertex's new value never leaks into its neighbour's average (or the flatten height)
	/// mid-dab and the result cannot depend on vertex order.
	/// </summary>
	public static DisplacementDabResult ApplyDab(
		IDisplacementSurface target,
		BrushSample sample,
		DisplacementSculptMode mode,
		BrushFalloff falloff,
		BrushAlpha stamp,
		DisplacementSculptOptions options = null )
	{
		if ( target is null )
			throw new ArgumentNullException( nameof( target ) );
		CheckSurface( target );
		if ( sample.Radius <= 0f || !float.IsFinite( sample.Radius ) )
			throw new ArgumentException( "dab radius must be positive and finite", nameof( sample ) );
		if ( !float.IsFinite( sample.Strength ) )
			throw new ArgumentException( "dab strength must be finite", nameof( sample ) );
		options ??= new DisplacementSculptOptions();
		if ( mode == DisplacementSculptMode.PaintAlpha
			&& (options.PaintAlphaTarget < 0f || options.PaintAlphaTarget > 1f) )
			throw new ArgumentException( "PaintAlphaTarget must be 0..1", nameof( options ) );

		var side = target.Side;
		var offsets = target.Offsets;
		var alpha = target.Alpha;

		// Snapshot for Smooth / Flatten, and the flatten height: the offset under the dab
		// centre (nearest in-radius vertex), measured before this dab moves anything.
		float[] before = null;
		var flattenHeight = 0f;
		var nearest = float.MaxValue;
		if ( mode is DisplacementSculptMode.Smooth or DisplacementSculptMode.Flatten )
		{
			before = (float[])offsets.Clone();
			for ( var r = 0; r < side; r++ )
			for ( var c = 0; c < side; c++ )
			{
				var d = (target.VertexPosition( r, c ) - sample.Position).Length;
				if ( d < sample.Radius && d < nearest )
				{
					nearest = d;
					flattenHeight = before[r * side + c];
				}
			}
		}

		// Raise axis factor: the signed component of the requested axis along the face normal.
		var axisFactor = options.RaiseAxis switch
		{
			DisplacementRaiseAxis.X => Vec3.Dot( new Vec3( 1, 0, 0 ), target.Normal ),
			DisplacementRaiseAxis.Y => Vec3.Dot( new Vec3( 0, 1, 0 ), target.Normal ),
			DisplacementRaiseAxis.Z => Vec3.Dot( new Vec3( 0, 0, 1 ), target.Normal ),
			_ => 1f,
		};

		// Stamp basis: the dab normal is the stamp plane; Right projected onto it is u, the
		// cross product is v. Rotation is the per-dab AlphaRotation, in degrees.
		Vec3 stampRight = default, stampUp = default;
		var stampRotation = 0f;
		if ( stamp is not null )
		{
			var n = sample.Normal.LengthSquared > 1e-8f ? sample.Normal.Normal : target.Normal;
			var right = stamp.Right - n * Vec3.Dot( stamp.Right, n );
			if ( right.LengthSquared < 1e-6f )
				right = MathF.Abs( n.x ) < 0.9f
					? Vec3.Cross( n, new Vec3( 1, 0, 0 ) )
					: Vec3.Cross( n, new Vec3( 0, 1, 0 ) );
			stampRight = right.Normal;
			stampUp = Vec3.Cross( n, stampRight ).Normal;
			stampRotation = sample.AlphaRotation * MathF.PI / 180f;
		}

		var touched = 0;
		var maxOffsetDelta = 0f;
		var maxAlphaDelta = 0f;

		for ( var r = 0; r < side; r++ )
		for ( var c = 0; c < side; c++ )
		{
			var i = r * side + c;
			var pos = target.VertexPosition( r, c );
			var rel = pos - sample.Position;
			var weight = FalloffWeight( rel.Length, sample.Radius, falloff );
			if ( weight <= 0f )
				continue;

			if ( stamp is not null )
			{
				// Stamp spans the brush diameter times Scale; (u, v) 0..1 across it, row 0 at
				// the top. Sample is 0 outside 0..1, so a vertex the stamp does not cover is
				// untouched by this dab.
				var cu = Vec3.Dot( rel, stampRight ) / (2f * sample.Radius * stamp.Scale);
				var cv = -Vec3.Dot( rel, stampUp ) / (2f * sample.Radius * stamp.Scale);
				var cos = MathF.Cos( stampRotation );
				var sin = MathF.Sin( stampRotation );
				var u = 0.5f + cu * cos - cv * sin;
				var v = 0.5f + cu * sin + cv * cos;
				weight *= stamp.Sample( u, v );
				if ( weight <= 0f )
					continue;
			}

			touched++;

			switch ( mode )
			{
				case DisplacementSculptMode.Raise:
				case DisplacementSculptMode.Lower:
				{
					var sign = mode == DisplacementSculptMode.Raise ? 1f : -1f;
					var delta = sign * sample.Strength * weight * axisFactor;
					offsets[i] += delta;
					maxOffsetDelta = MathF.Max( maxOffsetDelta, MathF.Abs( delta ) );
					break;
				}
				case DisplacementSculptMode.Smooth:
				{
					// Neighbour average from the snapshot; the lerp factor clamps to 0..1 so
					// a strong dab converges on the average instead of overshooting past it.
					var sum = 0f;
					var count = 0;
					if ( r > 0 ) { sum += before[(r - 1) * side + c]; count++; }
					if ( r < side - 1 ) { sum += before[(r + 1) * side + c]; count++; }
					if ( c > 0 ) { sum += before[r * side + c - 1]; count++; }
					if ( c < side - 1 ) { sum += before[r * side + c + 1]; count++; }
					if ( count == 0 )
						break;
					var factor = Math.Clamp( sample.Strength * weight, 0f, 1f );
					var delta = (sum / count - before[i]) * factor;
					offsets[i] = before[i] + delta;
					maxOffsetDelta = MathF.Max( maxOffsetDelta, MathF.Abs( delta ) );
					break;
				}
				case DisplacementSculptMode.Flatten:
				{
					var factor = Math.Clamp( sample.Strength * weight, 0f, 1f );
					var delta = (flattenHeight - before[i]) * factor;
					offsets[i] = before[i] + delta;
					maxOffsetDelta = MathF.Max( maxOffsetDelta, MathF.Abs( delta ) );
					break;
				}
				case DisplacementSculptMode.PaintAlpha:
				{
					// Toward the target by strength * weight, clamped to a full move: a
					// strong dab lands exactly on the target, repeated dabs never pass it,
					// and alpha never leaves 0..1. Negative strength is a no-op (erase by
					// painting with PaintAlphaTarget 0, the Hammer behaviour).
					var factor = Math.Clamp( sample.Strength * weight, 0f, 1f );
					var next = Math.Clamp( alpha[i] + (options.PaintAlphaTarget - alpha[i]) * factor, 0f, 1f );
					maxAlphaDelta = MathF.Max( maxAlphaDelta, MathF.Abs( next - alpha[i] ) );
					alpha[i] = next;
					break;
				}
			}
		}

		return new DisplacementDabResult( touched, maxOffsetDelta, maxAlphaDelta );
	}
}
