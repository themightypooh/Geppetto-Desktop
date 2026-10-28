using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// A curve drawn in the viewport — click the points, the curve runs through them — and, when
/// asked, the tube along it.
///
/// ONE FEATURE, TWO JOBS, on purpose. A spline that only ever existed as a path would need a
/// second feature to become a cable, and "draw a cable" is the thing people come here for; a
/// tube that could not also be used as a bare path would need the curve drawn twice for a
/// sweep or a bend. So this publishes the curve for anything downstream (Bend along curve,
/// Sweep) AND builds the tube when Tube is on — off, it is a path and nothing more.
///
/// The points are NOT parameters: they are drawn, not typed, and the dialog shows the radius
/// and the sides while the viewport owns the curve. Saved as a vec3s line like a sketch's
/// region seeds.
///
/// Profile Band makes a flat strap instead of a round tube: Width across, Thickness through, its
/// face following the curve (or kept looking along Band faces). See <see cref="SplineBand"/>.
/// </summary>
public sealed class SplineFeature : Feature
{
	public override string TypeName => "Spline";

	public List<Vec3> Points = new();

	public readonly BoolParam Closed = new( "Closed loop", false );
	public readonly BoolParam Smooth = new( "Smooth", true );
	public readonly BoolParam Tube = new( "Make a tube", true );
	public readonly ChoiceParam Profile = new( "Profile", new[] { "Round", "Band" } );
	public readonly FloatParam Radius = new( "Radius", 2f, 0.0001f, unit: "u" );
	public readonly FloatParam Width = new( "Width", 2f, 0.0001f, unit: "u" );
	public readonly FloatParam Thickness = new( "Thickness", 0.2f, 0.0001f, unit: "u" );
	/// <summary>Band only: the way its flat face looks. Zero follows the curve.</summary>
	public readonly Vec3Param Facing = new( "Band faces", Vec3.Zero );
	public readonly FloatParam Taper = new( "End radius ×", 1f, 0f, 8f );
	public readonly IntParam Sides = new( "Sides", 12, 3, 64 );
	public readonly FloatParam Twist = new( "Twist", 0f, unit: "deg" );
	public readonly IntParam Resolution = new( "Points per segment", 8, 1, 64 );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	/// <summary>Per-station radius as "t:×,…" pairs, t in 0..1 along the path by arc length,
	/// × a multiplier of Radius (1 = the base radius). Empty keeps the old end-taper tube.</summary>
	public readonly StringParam RadiusProfile = new( "Radius profile (t:×,…)", "" );

	/// <summary>Frame mode: Parallel transport (default), Fixed up, or Frenet.</summary>
	public readonly ChoiceParam FrameMode = new( "Frame mode", new[] { "Parallel transport", "Fixed up", "Frenet" } );

	public readonly BoolParam BendGuard = new( "Bend guard", true );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Closed, Smooth, Tube, Profile, Radius, Width, Thickness, Taper, Sides, Twist, Resolution, RadiusProfile, FrameMode, Material };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Facing, Resolution, BendGuard, Material };

	/// <summary>The curve as this feature would publish it, for the viewport to draw while it is
	/// being edited without waiting on a rebuild.</summary>
	public Spline3 ToSpline() => new() { Points = Points, Closed = Closed.Value, Smooth = Smooth.Value };

	protected override void Execute( FeatureContext ctx )
	{
		var spline = new Spline3
		{
			Points = new List<Vec3>( Points ),
			Closed = Closed.Value,
			Smooth = Smooth.Value,
		};

		ctx.Splines[Id] = spline;

		// Scatter's Curve/CurveIMM modes look a spline up by the Curve string, and a
		// script can only name a spline it can spell: publish under the feature name
		// too, so curve=ChainPath works and not just the unreadable id. A later spline
		// with the same name wins, like bodies by name.
		if ( !string.IsNullOrWhiteSpace( Name ) )
			ctx.Splines[Name] = spline;
		ctx.LastSplineId = Id;

		// Fewer than two points is a spline being drawn, not a broken one. Nothing is built and
		// nothing goes red; the warning says what the next click does.
		if ( Points.Count < 2 )
		{
			Warning = "Click two or more points in the viewport to draw the curve.";
			return;
		}

		if ( !Tube.Value )
			return;

		var path = spline.Sample( Resolution.Clamped );

		if ( Closed.Value && path.Count < 3 )
		{
			Warning = "A closed loop needs three or more points.";
			return;
		}

		// Bend guard (wave 3): the bend radius at an interior station must exceed the
		// tube radius / band extent toward the centre of curvature, or the tube folds.
		if ( BendGuard.Value )
		{
			var frames = Spline3.Frames( path, Closed.Value, FrameMode.Index );
			var totalLen = Spline3.Length( path, Closed.Value );
			var along = 0f;
			for ( var i = 0; i < path.Count; i++ )
			{
				if ( i > 0 )
					along += (path[i] - path[i - 1]).Length;
				if ( Closed.Value )
				{
					if ( path.Count < 3 )
						continue;
				}
				else if ( i == 0 || i == path.Count - 1 )
					continue;
				var n = path.Count;
				var prev = path[(i - 1 + n) % n];
				var next = path[(i + 1) % n];
				var cur = path[i];
				var tIn = (cur - prev).LengthSquared < 1e-12f ? new Vec3( 1, 0, 0 ) : (cur - prev).Normal;
				var tOut = (next - cur).LengthSquared < 1e-12f ? new Vec3( 1, 0, 0 ) : (next - cur).Normal;
				var theta = MathF.Acos( Math.Clamp( Vec3.Dot( tIn, tOut ), -1f, 1f ) );
				if ( theta < 1e-4f )
					continue;
				var radius = (next - prev).Length / (2f * MathF.Sin( theta ));
				var centreDir = tOut - tIn;
				if ( centreDir.LengthSquared < 1e-12f )
					continue;
				centreDir = centreDir.Normal;
				float extent;
				if ( Profile.Index == 1 )
				{
					var f = totalLen > 1e-8f ? along / totalLen : 0f;
					var hw = (Closed.Value ? Width.Clamped : Width.Clamped + (Width.Clamped * Taper.Clamped - Width.Clamped) * f) * 0.5f;
					var ht = Thickness.Clamped * 0.5f;
					// Safe upper bound over any band facing: a ring corner can reach
					// sqrt(hw^2 + ht^2) from the path in the bend direction.
					extent = MathF.Sqrt( hw * hw + ht * ht );
				}
				else
				{
					extent = Radius.Clamped;
				}
				if ( extent > 0f && radius <= extent )
					Fail(
						$"This spline folds at station {i}: the bend radius is {radius:0.##} but the tube reaches {extent:0.##} toward the centre of the bend.",
						"On the inside of a bend, tube walls converge; when the bend radius drops below the tube radius, the tube passes through itself.",
						"Straighten the spline",
						"Shrink the radius, or turn Bend guard off to build it anyway" );
			}
		}

		PolyMesh mesh;

		if ( Profile.Index == 1 )
		{
			var width = Width.Clamped;
			mesh = SplineBand.Build( path, Closed.Value, width, width * Taper.Clamped, Thickness.Clamped, Facing.Value, Twist.Value, Material.Clamped, frameMode: FrameMode.Index );
		}
		else
		{
			var radius = Radius.Clamped;
			var profile = ParseRadiusProfile( RadiusProfile.Value );
			if ( profile is null )
			{
				mesh = SplineTube.Build( path, Closed.Value, radius, radius * Taper.Clamped, Sides.Clamped, Twist.Value, Material.Clamped, frameMode: FrameMode.Index );
			}
			else
			{
				var totalLen = Spline3.Length( path, Closed.Value );
				var along = 0f;
				var radii = new List<float>( path.Count );
				for ( var i = 0; i < path.Count; i++ )
				{
					if ( i > 0 )
						along += (path[i] - path[i - 1]).Length;
					var f = totalLen > 1e-8f ? along / totalLen : 0f;
					var mult = ProfileAt( profile, f );
					var taperMult = Closed.Value ? 1f : 1f + (Taper.Clamped - 1f) * f;
					radii.Add( radius * mult * taperMult );
				}
				mesh = SplineTube.BuildWithRadiusProfile( path, Closed.Value, radii, Sides.Clamped, Twist.Value, Material.Clamped, FrameMode.Index );
			}
		}

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );
	}

	/// <summary>Parse "t:×,t:×,…" into sorted (t, multiplier) pairs, or null when empty.
	/// A bad pair fails with the offending text, the same way Sweep's taper does.</summary>
	static List<(float T, float Mult)> ParseRadiusProfile( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) )
			return null;
		var pairs = new List<(float, float)>();
		foreach ( var part in text.Split( ',' ) )
		{
			var kv = part.Split( ':' );
			float t = 0f, m = 0f;
			if ( kv.Length != 2 || !float.TryParse( kv[0].Trim(), out t ) || !float.TryParse( kv[1].Trim(), out m ) )
				Fail(
					$"Radius profile '{text}' is not t:mult pairs.",
					"Each pair is a position along the path (0 at the start, 1 at the end) and a radius multiplier.",
					"Write it like 0:1,0.5:0.5,1:1",
					"Leave it empty for the plain end-taper tube" );
			pairs.Add( (t, m) );
		}
		pairs.Sort( (a, b) => a.Item1.CompareTo( b.Item1 ) );
		return pairs;
	}

	static float ProfileAt( List<(float T, float Mult)> pairs, float f )
	{
		if ( pairs.Count == 1 )
			return pairs[0].Item2;
		if ( f <= pairs[0].Item1 )
			return pairs[0].Item2;
		if ( f >= pairs[^1].Item1 )
			return pairs[^1].Item2;
		for ( var i = 0; i + 1 < pairs.Count; i++ )
		{
			if ( f >= pairs[i].Item1 && f <= pairs[i + 1].Item1 )
			{
				var span = pairs[i + 1].Item1 - pairs[i].Item1;
				var local = span < 1e-9f ? 0f : (f - pairs[i].Item1) / span;
				return pairs[i].Item2 + (pairs[i + 1].Item2 - pairs[i].Item2) * local;
			}
		}
		return pairs[^1].Item2;
	}
}

/// <summary>
/// Bend a body along a spline: the body's X (or Y, or Z) runs along the curve, and its other two
/// axes ride in the curve's frame. A straight tail becomes a curled one; a straight rope
/// segment follows the path it was drawn over. The curve modifier, as a feature in the history.
/// </summary>
public sealed class CurveDeformFeature : Feature
{
	public override string TypeName => "Bend along curve";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bend" );

	/// <summary>The spline to follow. Empty means the one published last — the usual case, where
	/// the curve was drawn just before this was added.</summary>
	public string SplineId = "";

	public readonly ChoiceParam Axis = new( "Along the body's", new[] { "X", "Y", "Z" } );
	public readonly BoolParam Stretch = new( "Fit the length to the curve", true );
	/// <summary>Bend every picked body in one shared box, so parts of one thing (a fish and its fins)
	/// keep their places on the curve instead of each being stretched over all of it.</summary>
	public readonly BoolParam Together = new( "Bend as one", false );
	public readonly IntParam Resolution = new( "Points per segment", 8, 1, 64 );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Axis, Stretch, Together, Resolution };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Resolution };

	protected override void Execute( FeatureContext ctx )
	{
		var spline = ResolveSpline( ctx );
		var path = spline.Sample( Resolution.Clamped );

		if ( path.Count < 2 )
		{
			Fail(
				"The spline has fewer than two points.",
				"A curve needs two points before anything can bend along it.",
				"Click more points on the spline above this" );
		}

		var bodies = RequireBodies( ctx, Bodies ).ToList();
		if ( Together.Value )
		{
			var (lo, hi) = CurveDeform.Bounds( bodies.Select( b => b.Mesh ) );
			foreach ( var body in bodies )
				CurveDeform.Apply( body.Mesh, path, spline.Closed, Axis.Index, Stretch.Value, lo, hi );
		}
		else
			foreach ( var body in bodies )
				CurveDeform.Apply( body.Mesh, path, spline.Closed, Axis.Index, Stretch.Value );
	}

	Spline3 ResolveSpline( FeatureContext ctx )
	{
		if ( !string.IsNullOrEmpty( SplineId ) && ctx.Splines.TryGetValue( SplineId, out var named ) )
			return named;

		if ( ctx.LastSplineId is not null && ctx.Splines.TryGetValue( ctx.LastSplineId, out var last ) )
			return last;

		Fail(
			"There is no spline above this to bend along.",
			"Bend along curve follows a Spline feature earlier in the history, and none has run yet.",
			"Add a Spline first and draw the path",
			"Move this feature below the spline" );

		return null;
	}
}
