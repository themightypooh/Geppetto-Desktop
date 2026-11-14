using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Where a feature that cuts or reflects across a plane finds that plane: a picked face, a datum
/// Plane feature, or the typed point and normal.
///
/// THE ORDER IS THE MOST SPECIFIC ANSWER FIRST, the same rule PlaneFeature.ResolveBasePlane writes
/// down: a picked face, then a datum plane, then the numbers. The editor's picker clears the other
/// two on every pick, so only one is ever set; this is what a file that disagrees gets.
///
/// The face is stored as geometry (a FaceRef) and re-found on every rebuild, so editing the part
/// upstream moves the cut with the face instead of leaving it at the old index.
/// </summary>
public static class CutPlane
{
	/// <summary>Resolves the plane, or returns false with <paramref name="problem"/> saying why.</summary>
	public static bool TryResolve( FeatureContext ctx, FaceRef? face, string planeId, Vec3 point, Vec3 normal,
		out Vec3 origin, out Vec3 direction, out string problem )
	{
		origin = point;
		direction = normal;
		problem = null;

		if ( face is { } f )
		{
			if ( !FacePlane.TryResolve( ctx.Bodies, f, out var resolved ) )
			{
				problem = "The face this plane was picked from is gone";
				return false;
			}
			origin = resolved.Origin;
			direction = resolved.Normal;
			return true;
		}

		if ( !string.IsNullOrEmpty( planeId ) )
		{
			if ( !ctx.Planes.TryGetValue( planeId, out var datum ) )
			{
				problem = "The Plane this was picked from is not there any more";
				return false;
			}
			origin = datum.Origin;
			direction = datum.Normal;
			return true;
		}

		if ( normal.LengthSquared < 1e-12f )
		{
			problem = "Plane normal cannot be zero";
			return false;
		}
		direction = normal.Normal;
		return true;
	}
}

/// <summary>
/// Cuts bodies in two across a plane, each half capped so it stays a solid. Onshape's Split.
///
/// The plane comes from <see cref="CutPlane"/>: a picked face, a datum Plane, or Plane through /
/// Plane normal. "Both halves" keeps the part above the plane in the original body and adds the part
/// below as a new body, so features further down that point at the original still find it. A body
/// the plane misses is left whole and warned about rather than duplicated.
/// </summary>
public sealed class SplitFeature : Feature
{
	public override string TypeName => "Split";

	public override GeometryKind Accepts => GeometryKind.Body;

	public static readonly string[] KeepOptions = { "Both halves", "Above the plane", "Below the plane" };

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly Vec3Param PlanePoint = new( "Plane through", Vec3.Zero );
	public readonly Vec3Param PlaneNormal = new( "Plane normal", new Vec3( 0, 0, 1 ) );
	public readonly ChoiceParam Keep = new( "Keep", KeepOptions );

	/// <summary>The face the cut lies on, when one was picked (wins over the numbers).</summary>
	public FaceRef? PlaneFace;

	/// <summary>The datum Plane feature the cut lies on, when one was picked.</summary>
	public string PlaneId = "";

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, PlanePoint, PlaneNormal, Keep };

	protected override void Execute( FeatureContext ctx )
	{
		if ( !CutPlane.TryResolve( ctx, PlaneFace, PlaneId, PlanePoint.Value, PlaneNormal.Value, out var origin, out var n, out var problem ) )
		{
			FailOn( "Plane normal", problem,
				"Split needs a plane to cut along.",
				"Pick a face or a plane to cut along", "Type a Plane normal that is not zero" );
		}

		var offset = Vec3.Dot( origin, n );
		var missed = 0;

		foreach ( var body in RequireBodies( ctx, Bodies ) )
		{
			var mesh = body.Mesh;
			if ( mesh is null || mesh.FaceCount == 0 )
				continue;

			// Which side each vertex is on decides whether the plane cuts at all: a plane clear of the
			// body would "split" it into itself and nothing.
			bool above = false, below = false;
			foreach ( var p in mesh.Positions )
			{
				var d = Vec3.Dot( p, n ) - offset;
				if ( d > 1e-5f ) above = true;
				else if ( d < -1e-5f ) below = true;
			}
			if ( !above || !below )
			{
				missed++;
				continue;
			}

			var top = MeshClip.TrimPlane( mesh, new ClipPlane( n, offset ) );
			var bottom = MeshClip.TrimPlane( mesh, new ClipPlane( -n, -offset ) );

			switch ( Keep.Index )
			{
				case 1: body.Mesh = top; break;
				case 2: body.Mesh = bottom; break;
				default:
					body.Mesh = top;
					ctx.Bodies.Add( new Body( ctx.NewBodyId(), $"{body.Name} (split)", bottom ) );
					break;
			}
		}

		if ( missed > 0 )
			Warn( $"The plane misses {missed} of the picked bodies, so they were left whole.",
				"Split only cuts a body the plane passes through.",
				"Move the plane into the body", "Pick a face that crosses it" );
	}
}

/// <summary>
/// Gives an open surface a wall thickness so it becomes a solid. Onshape's Thicken.
///
/// Uses <see cref="MeshSolidify"/>: the surface is offset by Thickness (along its normals, or against
/// them with Inward on) and the open rims are closed with side walls. A closed solid is refused,
/// because thickening one only makes a second skin inside it — that is Shell's job.
/// </summary>
public sealed class ThickenFeature : Feature
{
	public override string TypeName => "Thicken";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly FloatParam Thickness = new( "Thickness", 0.1f, 0.001f, 1000f, "u" );
	public readonly BoolParam Inward = new( "Inward", false );
	public readonly BoolParam Smooth = new( "Smooth offset", false );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Thickness, Inward };
	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Smooth };

	protected override void Execute( FeatureContext ctx )
	{
		var thickness = Thickness.Clamped * ( Inward.Value ? -1f : 1f );
		var closed = 0;

		foreach ( var body in RequireBodies( ctx, Bodies ) )
		{
			if ( body.Mesh is null || body.Mesh.FaceCount == 0 )
				continue;
			if ( PressureBalloon.BoundaryEdgeCount( body.Mesh ) == 0 )
			{
				closed++;
				continue;
			}
			try
			{
				body.Mesh = MeshSolidify.Solidify( body.Mesh, thickness, Smooth.Value );
			}
			catch ( InvalidOperationException e )
			{
				Fail( $"'{body.Name}' cannot be thickened: {e.Message}",
					"Thicken needs a clean surface: every edge shared by at most two faces.",
					"Pick a different body", "Clean the surface up first" );
			}
		}

		if ( closed > 0 )
			Warn( $"{closed} of the picked bodies are already closed solids, so they were left alone.",
				"Thicken turns an open surface into a solid; a closed one has no rim to wall in.",
				"Use Shell to hollow a solid", "Delete a face first to open it" );
	}
}
