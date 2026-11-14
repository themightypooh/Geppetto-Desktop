using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// A spiral path, published as a spline the way <see cref="SplineFeature"/> publishes one, so Sweep,
/// Bend Along Curve and Curve Pattern can follow it. With Tube on it also builds a round tube along the
/// spiral, so a spring or a thread is one feature. Onshape's Helix.
///
/// The helix stands on a plane: a picked face, a datum Plane, or Base point / Axis (see
/// <see cref="CutPlane"/>). It starts on that plane at Start angle and climbs Height along the axis in
/// Turns turns. End radius other than zero narrows (or widens) it toward the top, for a cone spring.
///
/// The path is sampled at a fixed 24 points per turn rather than stored, so changing Turns or Radius
/// moves the curve without a second list of points that could disagree with the numbers.
/// </summary>
public sealed class HelixFeature : Feature
{
	public override string TypeName => "Helix";

	public override GeometryKind Accepts => GeometryKind.None;

	const int PointsPerTurn = 24;

	public readonly Vec3Param BasePoint = new( "Base point", Vec3.Zero );
	public readonly Vec3Param Axis = new( "Axis", new Vec3( 0, 0, 1 ) );
	public readonly FloatParam Radius = new( "Radius", 5f, 0.001f, 10000f, "u" );
	public readonly FloatParam EndRadius = new( "End radius (0 = same)", 0f, 0f, 10000f, "u" );
	public readonly FloatParam Turns = new( "Turns", 5f, 0.1f, 500f );
	public readonly FloatParam Height = new( "Height", 20f, 0f, 100000f, "u" );
	public readonly FloatParam StartAngle = new( "Start angle", 0f, -360f, 360f, "deg" );
	public readonly BoolParam Clockwise = new( "Clockwise", false );
	public readonly BoolParam Tube = new( "Make a tube", true );
	public readonly FloatParam TubeRadius = new( "Tube radius", 0.5f, 0.0001f, 1000f, "u" );
	public readonly IntParam Sides = new( "Sides", 12, 3, 64 );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	/// <summary>The face the helix stands on, when one was picked (wins over Base point / Axis).</summary>
	public FaceRef? PlaneFace;

	/// <summary>The datum Plane feature the helix stands on, when one was picked.</summary>
	public string PlaneId = "";

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { BasePoint, Axis, Radius, EndRadius, Turns, Height, StartAngle, Clockwise, Tube, TubeRadius };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Sides, Material };

	/// <summary>The spiral's points, for the feature and for the viewport to draw.</summary>
	public static List<Vec3> Points( Vec3 origin, Vec3 axis, float radius, float endRadius, float turns, float height,
		float startDegrees, bool clockwise )
	{
		var n = axis.Normal;
		var helper = MathF.Abs( n.z ) < 0.92f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		var u = Vec3.Cross( helper, n ).Normal;
		var v = Vec3.Cross( n, u );
		var end = endRadius > 0f ? endRadius : radius;
		var count = Math.Max( 2, (int)MathF.Ceiling( turns * PointsPerTurn ) + 1 );
		var sign = clockwise ? -1f : 1f;
		var start = startDegrees * MathF.PI / 180f;
		var points = new List<Vec3>( count );

		for ( var i = 0; i < count; i++ )
		{
			var t = i / (float)(count - 1);
			var a = start + sign * t * turns * MathF.Tau;
			var r = radius + (end - radius) * t;
			points.Add( origin + u * (MathF.Cos( a ) * r) + v * (MathF.Sin( a ) * r) + n * (t * height) );
		}

		return points;
	}

	protected override void Execute( FeatureContext ctx )
	{
		if ( !CutPlane.TryResolve( ctx, PlaneFace, PlaneId, BasePoint.Value, Axis.Value, out var origin, out var axis, out var problem ) )
		{
			FailOn( "Axis", problem,
				"A helix needs a plane to stand on: a picked face, a Plane, or an Axis that is not zero.",
				"Pick a face or a plane for the helix to stand on", "Set Axis to the way the helix climbs" );
		}

		var points = Points( origin, axis, Radius.Clamped, EndRadius.Clamped, Turns.Clamped, Height.Clamped,
			StartAngle.Clamped, Clockwise.Value );
		var spline = new Spline3 { Points = points, Closed = false, Smooth = true };

		ctx.Splines[Id] = spline;
		if ( !string.IsNullOrWhiteSpace( Name ) )
			ctx.Splines[Name] = spline;
		ctx.LastSplineId = Id;

		if ( !Tube.Value )
			return;

		if ( Height.Clamped / Turns.Clamped < TubeRadius.Clamped * 2f && EndRadius.Clamped <= 0f )
		{
			Warn( "The coils touch: each turn climbs less than the tube is thick.",
				"Turns that overlap make a tube that runs through itself.",
				"Raise Height", "Lower Turns", "Thin the Tube radius" );
		}

		var mesh = SplineTube.Build( spline.Sample( 2 ), false, TubeRadius.Clamped, TubeRadius.Clamped, Sides.Clamped, 0f, Material.Clamped );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );
	}
}
