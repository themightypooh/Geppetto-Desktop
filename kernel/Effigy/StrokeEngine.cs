using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// The professional stroke engine (godlike-stroke-engine-sculpt-layers, Wave 2 P1).
///
/// What already existed and is REUSED, not re-implemented:
/// - <see cref="StrokePath"/> (SculptSession.cs): Space/Anchored/DragDot/Roll/Line/Lazy
///   expansion plus the EMA <c>Smooth</c> stabilizer. The six modes it owns are
///   delegated to it by <see cref="StrokeEngineApi.Expand"/>; this file only adds the
///   modes it cannot express (Dots, Airbrush, Curve) because <see cref="StrokeMode"/>
///   lives in SculptSession.cs, a file another worker owns this morning.
/// - <see cref="StrokeStabilizer"/>: the 2D spring-mass pen-tip stabilizer for the editor.
/// - <see cref="Brush.Falloff"/>: the four named falloffs (Smooth/Linear/Sharp/Constant).
/// - <see cref="BrushAlpha"/>: procedural alphas. <see cref="VdmStampLibrary"/>: VDM stamps.
/// - <see cref="PaintReplay"/>: replay for PAINT strokes onto a canvas. This file's
///   <see cref="StrokeReplay"/> is the SCULPT counterpart: it re-expands a recorded
///   stroke into the identical dab list, it never touches a canvas.
/// - Sculpt layers live in SculptLayer.cs (godlike-sculpt-layers-morph-targets-polygroups,
///   another worker's ticket). This engine applies dabs through a <see cref="BrushStroke"/>
///   the caller owns, so a layer-aware caller records the stroke on its layer; the engine
///   itself holds no layer state.
///
/// Everything random here flows through <see cref="SeededRandom"/> with an explicit seed,
/// so a replay under the same seed is bit-identical: same seed, same floats, same order.
/// No <c>System.Random</c>, whose sequence is not guaranteed stable across runtimes.
/// </summary>
public static class StrokeEngine
{
	/// <summary>Spacing is measured in world units along the stroke.</summary>
	public const float DefaultSpacing = 0.1f;
}

/// <summary>
/// Deterministic PRNG (xorshift32) for per-dab randomisation and replay.
/// Same seed, same sequence, on every runtime — that is the whole point.
/// </summary>
public sealed class SeededRandom
{
	uint _state;

	public SeededRandom( int seed )
	{
		_state = (uint)seed == 0u ? 0x9E3779B9u : (uint)seed;
	}

	public uint NextUInt()
	{
		var x = _state;
		x ^= x << 13;
		x ^= x >> 17;
		x ^= x << 5;
		_state = x;
		return x;
	}

	/// <summary>Uniform float in [0, 1).</summary>
	public float NextFloat() => (NextUInt() >> 8) * (1f / 16777216f);

	/// <summary>Uniform float in [min, max).</summary>
	public float NextRange( float min, float max ) => min + (max - min) * NextFloat();

	/// <summary>Uniform int in [0, count).</summary>
	public int NextInt( int count ) => count <= 0 ? 0 : (int)(NextFloat() * count);
}

/// <summary>
/// A piecewise-linear curve through control points, the falloff/pressure curve
/// editor primitive: Add/Move/Remove points, evaluate by linear interpolation.
/// Outside the first/last point the end values hold (clamped, never extrapolated).
/// </summary>
public sealed class PiecewiseCurve
{
	public readonly List<(float X, float Y)> Points = new();

	public PiecewiseCurve() { }

	public PiecewiseCurve( params (float X, float Y)[] points )
	{
		foreach ( var p in points )
			AddPoint( p.X, p.Y );
	}

	/// <summary>The identity curve: output equals input.</summary>
	public static PiecewiseCurve Identity() => new( (0f, 0f), (1f, 1f) );

	/// <summary>A flat curve: every input maps to <paramref name="value"/>.</summary>
	public static PiecewiseCurve Flat( float value ) => new( (0f, value), (1f, value) );

	public void AddPoint( float x, float y )
	{
		var at = Points.FindIndex( p => p.X >= x );
		if ( at >= 0 && Points[at].X == x )
			Points[at] = (x, y);
		else if ( at >= 0 )
			Points.Insert( at, (x, y) );
		else
			Points.Add( (x, y) );
	}

	/// <summary>Move the point at <paramref name="index"/>; returns false when out of range.</summary>
	public bool MovePoint( int index, float x, float y )
	{
		if ( index < 0 || index >= Points.Count )
			return false;
		Points.RemoveAt( index );
		AddPoint( x, y );
		return true;
	}

	/// <summary>Remove the point at <paramref name="index"/>; returns false when out of range.</summary>
	public bool RemovePoint( int index )
	{
		if ( index < 0 || index >= Points.Count )
			return false;
		Points.RemoveAt( index );
		return true;
	}

	public float Evaluate( float x )
	{
		if ( Points.Count == 0 )
			return 0f;
		if ( Points.Count == 1 || x <= Points[0].X )
			return Points[0].Y;
		if ( x >= Points[^1].X )
			return Points[^1].Y;
		for ( var i = 1; i < Points.Count; i++ )
		{
			if ( x <= Points[i].X )
			{
				var (x0, y0) = Points[i - 1];
				var (x1, y1) = Points[i];
				if ( x1 <= x0 )
					return y1;
				var t = (x - x0) / (x1 - x0);
				return y0 + (y1 - y0) * t;
			}
		}
		return Points[^1].Y;
	}

	public PiecewiseCurve Clone()
	{
		var copy = new PiecewiseCurve();
		copy.Points.AddRange( Points );
		return copy;
	}
}

/// <summary>
/// Per-channel pressure curves (Blender: stylus pressure drives each channel through its
/// own curve). Strength defaults to identity (pressure scales strength 1:1); Radius
/// defaults to flat 1 (pressure leaves radius alone) — a caller that wants radius to
/// grow with pressure sets a rising curve. Pressures are 0..1 and clamped.
/// </summary>
public sealed class StrokePressureCurve
{
	public PiecewiseCurve Strength { get; set; } = PiecewiseCurve.Identity();
	public PiecewiseCurve Radius { get; set; } = PiecewiseCurve.Flat( 1f );

	public (float Radius, float Strength) Apply( float pressure, float baseRadius, float baseStrength )
	{
		var p = Math.Clamp( pressure, 0f, 1f );
		return (baseRadius * Radius.Evaluate( p ), baseStrength * Strength.Evaluate( p ));
	}

	public StrokePressureCurve Clone() => new() { Strength = Strength.Clone(), Radius = Radius.Clone() };
}

/// <summary>Whether stroke spacing is measured in world units or screen pixels.</summary>
public enum StrokeSpacingKind
{
	/// <summary>Spacing is world units along the path (Blender's scene spacing).</summary>
	Scene,
	/// <summary>Spacing is screen pixels; converted with the view's pixels-per-unit.</summary>
	Screen,
}

/// <summary>
/// Dash pattern (Blender's Dash Ratio): the stroke only lays dabs while the distance
/// along the path is inside the ON part of each dash cycle. DashRatio is the ON
/// fraction of a cycle (1 = solid, 0 = nothing); DashLength is one full cycle in
/// world units.
/// </summary>
public sealed class DashPattern
{
	public float DashRatio = 1f;
	public float DashLength = 1f;

	public bool IsOn( float distanceAlongPath )
	{
		var ratio = Math.Clamp( DashRatio, 0f, 1f );
		if ( ratio >= 1f )
			return true;
		if ( ratio <= 0f )
			return false;
		var cycle = Math.Max( DashLength, 1e-4f );
		var inCycle = distanceAlongPath - MathF.Floor( distanceAlongPath / cycle ) * cycle;
		return inCycle < ratio * cycle;
	}
}

/// <summary>
/// Per-dab jitter and rake (Blender's stroke jitter). PositionAmount is the maximum
/// sideways offset in world units; AngleDegrees is the maximum rake rotation of the
/// dab direction, in degrees either way. Both are drawn from <see cref="SeededRandom"/>,
/// so the same seed jitters identically.
/// </summary>
public sealed class StrokeJitter
{
	public float PositionAmount = 0f;
	public float AngleDegrees = 0f;

	public bool IsOff => PositionAmount <= 0f && AngleDegrees <= 0f;
}

/// <summary>
/// Stroke modes this engine expands. The six names shared with <see cref="StrokeMode"/>
/// delegate to <see cref="StrokePath"/>; Dots, Airbrush and Curve are new here.
/// </summary>
public enum StrokeEngineMode
{
	Space,
	Dots,
	DragDot,
	Anchored,
	Roll,
	Line,
	Lazy,
	Airbrush,
	Curve,
}

/// <summary>One randomised dab: the brush sample plus which alpha/VDM stamp it drew.</summary>
public readonly struct RandomisedDab
{
	public readonly BrushSample Sample;
	public readonly string AlphaName;
	public readonly string VdmName;

	public RandomisedDab( BrushSample sample, string alphaName, string vdmName )
	{
		Sample = sample;
		AlphaName = alphaName;
		VdmName = vdmName;
	}
}

/// <summary>
/// The stroke engine: expand a pointer path into brush samples, then run the
/// dash / jitter / pressure / randomisation pipeline over them. Every method is a
/// pure function of its inputs (plus an explicit seed) so a recorded stroke replays
/// bit-identically — see <see cref="StrokeRecord"/>.
/// </summary>
public static class StrokeEngineApi
{
	/// <summary>Convert a spacing to world units for the given spacing kind.</summary>
	public static float ToSceneSpacing( float spacing, StrokeSpacingKind kind, float pixelsPerUnit )
	{
		if ( kind == StrokeSpacingKind.Scene )
			return spacing;
		if ( pixelsPerUnit <= 0f )
			return spacing;
		return spacing / pixelsPerUnit;
	}

	/// <summary>
	/// Expand a raw pointer path into samples for a mode. The six modes
	/// <see cref="StrokePath"/> owns delegate to it unchanged; Dots stamps once per
	/// input point with no resampling; Airbrush needs a hold time, so use
	/// <see cref="AirbrushDabs"/> for it (this method treats it as Dots);
	/// Curve resamples a Catmull-Rom spline through the points at <paramref name="spacing"/>.
	/// </summary>
	public static List<StrokePathSample> Expand( StrokeEngineMode mode, IReadOnlyList<Vec3> points, float spacing, float lazyRadius = 0f )
	{
		switch ( mode )
		{
			case StrokeEngineMode.Space: return StrokePath.Expand( StrokeMode.Space, points, spacing );
			case StrokeEngineMode.Anchored: return StrokePath.Expand( StrokeMode.Anchored, points, spacing );
			case StrokeEngineMode.DragDot: return StrokePath.Expand( StrokeMode.DragDot, points, spacing );
			case StrokeEngineMode.Roll: return StrokePath.Expand( StrokeMode.Roll, points, spacing );
			case StrokeEngineMode.Line: return StrokePath.Expand( StrokeMode.Line, points, spacing );
			case StrokeEngineMode.Lazy: return StrokePath.Expand( StrokeMode.Lazy, points, spacing, lazyRadius );
			case StrokeEngineMode.Curve: return ExpandCurve( points, spacing );
			case StrokeEngineMode.Airbrush:
			case StrokeEngineMode.Dots:
			default: return ExpandDots( points );
		}
	}

	/// <summary>One sample per input point, direction along the segment. No resampling.</summary>
	public static List<StrokePathSample> ExpandDots( IReadOnlyList<Vec3> points )
	{
		var output = new List<StrokePathSample>();
		if ( points is null || points.Count == 0 )
			return output;
		for ( var i = 0; i < points.Count; i++ )
		{
			Vec3 dir = Vec3.Zero;
			if ( i + 1 < points.Count && (points[i + 1] - points[i]).LengthSquared > 1e-12f )
				dir = points[i + 1] - points[i];
			else if ( i > 0 && (points[i] - points[i - 1]).LengthSquared > 1e-12f )
				dir = points[i] - points[i - 1];
			output.Add( new StrokePathSample( points[i], dir ) );
		}
		return output;
	}

	/// <summary>
	/// Airbrush dabs: while the pointer is held still, dabs keep landing at the same
	/// point at <paramref name="ratePerSecond"/>. Count is floor(hold * rate); a hold
	/// shorter than one interval lays nothing, exactly like a held brush.
	/// </summary>
	public static List<StrokePathSample> AirbrushDabs( Vec3 point, Vec3 direction, float holdSeconds, float ratePerSecond )
	{
		var output = new List<StrokePathSample>();
		if ( holdSeconds <= 0f || ratePerSecond <= 0f )
			return output;
		var count = (int)MathF.Floor( holdSeconds * ratePerSecond );
		for ( var i = 0; i < count; i++ )
			output.Add( new StrokePathSample( point, direction ) );
		return output;
	}

	/// <summary>Catmull-Rom spline through the points, resampled at <paramref name="spacing"/>.</summary>
	public static List<StrokePathSample> ExpandCurve( IReadOnlyList<Vec3> points, float spacing )
	{
		var output = new List<StrokePathSample>();
		if ( points is null || points.Count == 0 )
			return output;
		if ( points.Count == 1 )
		{
			output.Add( new StrokePathSample( points[0], Vec3.Zero ) );
			return output;
		}
		var step = MathF.Max( spacing, 1e-4f );
		var dense = new List<Vec3>();
		for ( var i = 0; i + 1 < points.Count; i++ )
		{
			var p0 = i > 0 ? points[i - 1] : points[i];
			var p1 = points[i];
			var p2 = points[i + 1];
			var p3 = i + 2 < points.Count ? points[i + 2] : points[i + 1];
			var segLen = (p2 - p1).Length;
			var n = Math.Max( 1, (int)MathF.Ceiling( segLen / (step * 0.25f) ) );
			for ( var k = 0; k < n; k++ )
			{
				var t = k / (float)n;
				dense.Add( CatmullRom( p0, p1, p2, p3, t ) );
			}
		}
		dense.Add( points[^1] );
		var acc = 0f;
		var next = 0f;
		output.Add( new StrokePathSample( dense[0], dense.Count > 1 ? dense[1] - dense[0] : Vec3.Zero ) );
		for ( var i = 1; i < dense.Count; i++ )
		{
			acc += (dense[i] - dense[i - 1]).Length;
			if ( acc + 1e-6f < next + step )
				continue;
			next += step;
			Vec3 dir = Vec3.Zero;
			if ( i + 1 < dense.Count && (dense[i + 1] - dense[i]).LengthSquared > 1e-12f )
				dir = dense[i + 1] - dense[i];
			else if ( (dense[i] - dense[i - 1]).LengthSquared > 1e-12f )
				dir = dense[i] - dense[i - 1];
			output.Add( new StrokePathSample( dense[i], dir ) );
		}
		// The resample steps off the end of the spline: always land exactly on the
		// last control point, like StrokePath.Resample lands on the path end.
		if ( (dense[^1] - output[^1].Point).LengthSquared > 1e-8f )
			output.Add( new StrokePathSample( dense[^1], dense[^1] - dense[^2] ) );
		return output;
	}

	static Vec3 CatmullRom( Vec3 p0, Vec3 p1, Vec3 p2, Vec3 p3, float t )
	{
		var t2 = t * t;
		var t3 = t2 * t;
		return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
	}

	/// <summary>Cumulative chord length at each sample, for dash filtering.</summary>
	public static List<float> DistancesAlong( IReadOnlyList<StrokePathSample> samples )
	{
		var output = new List<float>( samples.Count );
		var acc = 0f;
		for ( var i = 0; i < samples.Count; i++ )
		{
			if ( i > 0 )
				acc += (samples[i].Point - samples[i - 1].Point).Length;
			output.Add( acc );
		}
		return output;
	}

	/// <summary>Keep only the samples whose distance along the path is inside a dash ON interval.</summary>
	public static List<StrokePathSample> ApplyDash( IReadOnlyList<StrokePathSample> samples, DashPattern dash )
	{
		var output = new List<StrokePathSample>();
		if ( dash is null )
		{
			output.AddRange( samples );
			return output;
		}
		var distances = DistancesAlong( samples );
		for ( var i = 0; i < samples.Count; i++ )
		{
			if ( dash.IsOn( distances[i] ) )
				output.Add( samples[i] );
		}
		return output;
	}

	/// <summary>
	/// Jitter samples: sideways position offset up to <see cref="StrokeJitter.PositionAmount"/>
	/// and rake rotation of the direction up to <see cref="StrokeJitter.AngleDegrees"/> either
	/// way, both drawn from a <paramref name="seed"/>ed sequence. The sideways axis is the
	/// direction crossed with up (Z when the direction is degenerate).
	/// </summary>
	public static List<StrokePathSample> ApplyJitter( IReadOnlyList<StrokePathSample> samples, StrokeJitter jitter, int seed )
	{
		var output = new List<StrokePathSample>( samples.Count );
		if ( jitter is null || jitter.IsOff )
		{
			output.AddRange( samples );
			return output;
		}
		var rng = new SeededRandom( seed );
		foreach ( var s in samples )
		{
			var point = s.Point;
			var dir = s.Direction;
			if ( jitter.PositionAmount > 0f )
			{
				var axis = Vec3.Cross( dir, new Vec3( 0, 0, 1 ) );
				if ( axis.LengthSquared < 1e-12f )
					axis = new Vec3( 1, 0, 0 );
				axis = axis.Normal;
				point += axis * rng.NextRange( -jitter.PositionAmount, jitter.PositionAmount );
			}
			if ( jitter.AngleDegrees > 0f && dir.LengthSquared > 1e-12f )
			{
				var angle = rng.NextRange( -jitter.AngleDegrees, jitter.AngleDegrees ) * MathF.PI / 180f;
				var n = dir.Normal;
				var perp = Vec3.Cross( new Vec3( 0, 0, 1 ), n );
				if ( perp.LengthSquared < 1e-12f )
					perp = Vec3.Cross( new Vec3( 0, 1, 0 ), n );
				perp = perp.Normal;
				var cos = MathF.Cos( angle );
				var sin = MathF.Sin( angle );
				dir = n * cos + perp * sin;
				dir *= s.Direction.Length;
			}
			output.Add( new StrokePathSample( point, dir ) );
		}
		return output;
	}

	/// <summary>
	/// Full pipeline: expand, dash, jitter, then turn samples into <see cref="BrushSample"/>s
	/// with per-sample pressure applied through <paramref name="pressureCurve"/>.
	/// <paramref name="pressures"/> aligns with the input <paramref name="points"/> (one per
	/// point, 0..1); each sample takes the pressure of its nearest input point, so a
	/// resampled stroke keeps the pressure the stylus actually reported. When null or
	/// short, missing pressures read as 1. Normals are zero here — the caller
	/// re-projects samples onto the mesh (as SculptSession does) before applying.
	/// </summary>
	public static List<BrushSample> BuildSamples( StrokeEngineMode mode, IReadOnlyList<Vec3> points, IReadOnlyList<float> pressures, float spacing, float baseRadius, float baseStrength, StrokePressureCurve pressureCurve, DashPattern dash, StrokeJitter jitter, int seed, float lazyRadius = 0f )
	{
		var samples = Expand( mode, points, spacing, lazyRadius );
		samples = ApplyDash( samples, dash );
		samples = ApplyJitter( samples, jitter, seed );
		var curve = pressureCurve ?? new StrokePressureCurve();
		var output = new List<BrushSample>( samples.Count );
		foreach ( var sample in samples )
		{
			var pressure = PressureAt( sample.Point, points, pressures );
			var (radius, strength) = curve.Apply( pressure, baseRadius, baseStrength );
			output.Add( new BrushSample( sample.Point, Vec3.Zero, radius, strength, sample.Direction ) );
		}
		return output;
	}

	/// <summary>Pressure of the input point nearest to <paramref name="at"/> (1 when unknown).</summary>
	public static float PressureAt( Vec3 at, IReadOnlyList<Vec3> points, IReadOnlyList<float> pressures )
	{
		if ( points is null || points.Count == 0 || pressures is null || pressures.Count == 0 )
			return 1f;
		var best = 0;
		var bestD = float.MaxValue;
		var n = Math.Min( points.Count, pressures.Count );
		for ( var i = 0; i < n; i++ )
		{
			var d = (points[i] - at).LengthSquared;
			if ( d < bestD )
			{
				bestD = d;
				best = i;
			}
		}
		return pressures[best];
	}

	/// <summary>
	/// Per-dab alpha/VDM randomisation: each dab draws a stamp name from the given
	/// lists and a rotation in ±<paramref name="rotationRangeDegrees"/>, all from a
	/// seeded sequence. Empty lists mean "no stamp for this dab" (null name).
	/// The rotation lands on <see cref="BrushSample.AlphaRotation"/>, the field the
	/// brush already honours per dab.
	/// </summary>
	public static List<RandomisedDab> Randomise( IReadOnlyList<BrushSample> samples, int seed, IReadOnlyList<string> alphaNames, IReadOnlyList<string> vdmNames, float rotationRangeDegrees )
	{
		var output = new List<RandomisedDab>( samples.Count );
		var rng = new SeededRandom( seed );
		foreach ( var s in samples )
		{
			var rotation = rotationRangeDegrees > 0f ? rng.NextRange( -rotationRangeDegrees, rotationRangeDegrees ) : 0f;
			var sample = new BrushSample( s.Position, s.Normal, s.Radius, s.Strength, s.Direction, rotation );
			var alpha = alphaNames is not null && alphaNames.Count > 0 ? alphaNames[rng.NextInt( alphaNames.Count )] : null;
			var vdm = vdmNames is not null && vdmNames.Count > 0 ? vdmNames[rng.NextInt( vdmNames.Count )] : null;
			output.Add( new RandomisedDab( sample, alpha, vdm ) );
		}
		return output;
	}
}

/// <summary>
/// A recorded stroke: everything needed to re-expand it later, bit-identically.
/// The record holds the INPUTS (mode, points, pressures, options snapshot), never
/// the outputs, so a replay re-runs the same pure pipeline with the same seed.
/// </summary>
public sealed class StrokeRecord
{
	public StrokeEngineMode Mode;
	public List<Vec3> Points = new();
	public List<float> Pressures = new();
	public float Spacing = StrokeEngine.DefaultSpacing;
	public float LazyRadius;
	public float BaseRadius = 1f;
	public float BaseStrength = 1f;
	public StrokePressureCurve Pressure = new();
	public DashPattern Dash = new();
	public StrokeJitter Jitter = new();
	public int Seed;

	public StrokeRecord Clone()
	{
		var copy = new StrokeRecord
		{
			Mode = Mode,
			Spacing = Spacing,
			LazyRadius = LazyRadius,
			BaseRadius = BaseRadius,
			BaseStrength = BaseStrength,
			Pressure = Pressure.Clone(),
			Dash = new DashPattern { DashRatio = Dash.DashRatio, DashLength = Dash.DashLength },
			Jitter = new StrokeJitter { PositionAmount = Jitter.PositionAmount, AngleDegrees = Jitter.AngleDegrees },
			Seed = Seed,
		};
		copy.Points.AddRange( Points );
		copy.Pressures.AddRange( Pressures );
		return copy;
	}
}

/// <summary>
/// Sculpt-stroke replay: re-expand a <see cref="StrokeRecord"/> into the identical
/// dab list. Bit-identical under a fixed seed because every step is a pure function
/// of the record (same seed, same floats, same order) — the test asserts exact
/// equality, not closeness.
/// </summary>
public static class StrokeReplay
{
	public static List<BrushSample> Replay( StrokeRecord record )
	{
		if ( record is null )
			throw new ArgumentNullException( nameof( record ) );
		return StrokeEngineApi.BuildSamples( record.Mode, record.Points, record.Pressures, record.Spacing, record.BaseRadius, record.BaseStrength, record.Pressure, record.Dash, record.Jitter, record.Seed, record.LazyRadius );
	}
}
