using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// Insert-and-repeat as a feature: <c>add InsertMesh sourcebody=Rivet mode=Curve curvepoints="0,0,0;4,0,0;4,4,0" spacing=1</c>.
///
/// What it does: it takes the mesh of another body in the studio — the source, a
/// rivet or a scale the user sculpted — and stamps it onto the selected bodies as
/// live instances of an <see cref="InsertBrush"/>, then commits them into each
/// target mesh (see <see cref="InsertMeshSession.Commit"/>). A feature rebuilds from
/// its parameters, so editing the source body and rebuilding re-stamps the new
/// shape everywhere: that is the live-instancing story at the document level. The
/// interactive, pre-commit form is <see cref="InsertMeshSession"/>.
///
/// MODES. Click stamps one instance at Center facing Normal (zero Normal snaps to
/// the nearest surface point of the first target, the way a brush snaps to the skin
/// it touches). Curve stamps along a polyline given as "x,y,z;x,y,z;..." every
/// Spacing units. Scatter stamps Count instances in a disc of Radius around Center,
/// with seeded scale/spin/jitter — the same seed places the same instances.
///
/// The source body must not be one of the targets: stamping a body into itself is
/// nonsense, and the rebuild says so instead of growing the mesh.
/// </summary>
public sealed class InsertMeshFeature : Feature
{
	public override string TypeName => "Insert Mesh";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );

	/// <summary>The body whose mesh is the insert source, by name (or id).</summary>
	public readonly StringParam SourceBody = new( "Source body", "" );

	public readonly ChoiceParam Mode = new( "Mode", new[] { "Click", "Curve", "Scatter" }, 0 );

	/// <summary>The click point / curve plane centre / scatter disc centre, in model space.</summary>
	public readonly Vec3Param Center = new( "Center", Vec3.Zero );

	/// <summary>Which way the click instance faces. Zero (default) snaps to the nearest surface point's normal.</summary>
	public readonly Vec3Param Normal = new( "Normal", Vec3.Zero );

	/// <summary>Which way is up for the instance frame. Zero (default) means world +Y.</summary>
	public readonly Vec3Param Tangent = new( "Tangent", Vec3.Zero );

	public readonly FloatParam Scale = new( "Scale", 1f, 0.01f, 100f );
	public readonly FloatParam Rotation = new( "Rotation", 0f, -180f, 180f, unit: "deg" );
	public readonly FloatParam Spacing = new( "Spacing", 1f, 0.001f, unit: "u" );
	public readonly FloatParam Radius = new( "Radius", 1f, 0.001f, unit: "u" );

	/// <summary>Curve points as "x,y,z;x,y,z;..." — semicolons between points, commas inside.</summary>
	public readonly StringParam Curve = new( "Curve points", "" );

	public readonly IntParam Count = new( "Count", 10, 0, 100000 ) { Slider = false };
	public readonly IntParam Seed = new( "Seed", 1, 0, int.MaxValue ) { Slider = false };
	public readonly FloatParam ScaleMin = new( "Scale min", 0.8f, 0.01f, 100f );
	public readonly FloatParam ScaleMax = new( "Scale max", 1.2f, 0.01f, 100f );
	public readonly FloatParam Jitter = new( "Jitter", 0f, 0f, 10f, unit: "u" );

	public readonly BoolParam DecimateAfterCommit = new( "Decimate after commit", false );
	public readonly IntParam DecimateTarget = new( "Decimate target", 0, 0, int.MaxValue ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Bodies, SourceBody, Mode, Center, Normal, Tangent, Scale, Rotation, Spacing, Radius, Curve, Count, Seed, ScaleMin, ScaleMax, Jitter, DecimateAfterCommit, DecimateTarget };

	/// <summary>Instances the last rebuild placed, per target body.</summary>
	public int LastInstanceCount { get; private set; }

	/// <summary>Vertices the last rebuild committed, across all target bodies.</summary>
	public int LastCommittedVertices { get; private set; }

	/// <summary>Faces the last rebuild committed, across all target bodies.</summary>
	public int LastCommittedFaces { get; private set; }

	protected override void Execute( FeatureContext ctx )
	{
		var sourceName = (SourceBody.Value ?? "").Trim();
		if ( sourceName.Length == 0 )
			FailOn( "Source body",
				"No source body for the insert",
				"An insert brush needs a body to copy.",
				"Name the body that holds the rivet, scale or stitch" );

		var source = ctx.Bodies.FirstOrDefault( b => b.Name == sourceName )
			?? ctx.Bodies.FirstOrDefault( b => string.Equals( b.Name, sourceName, StringComparison.OrdinalIgnoreCase ) )
			?? ctx.Bodies.FirstOrDefault( b => b.Id == sourceName );
		if ( source is null )
			FailOn( "Source body",
				$"No body called '{sourceName}'",
				"The studio has no body by that name or id.",
				"Check the spelling, or pick the body in the studio first" );
		if ( source.Mesh is null || source.Mesh.FaceCount == 0 )
			FailOn( "Source body",
				$"The source body '{sourceName}' has no mesh",
				"An insert brush needs faces to copy.",
				"Build the detail on that body first" );

		var targets = RequireBodies( ctx, Bodies ).Where( b => b.Mesh is not null && b.Mesh.FaceCount > 0 ).ToList();
		if ( targets.Count == 0 )
			Fail( "Insert Mesh needs a body to stamp onto.", "The selected bodies have no mesh with faces.", "Add a target body first, or pick bodies=Head" );
		if ( targets.Any( b => b.Id == source.Id ) )
			Fail( "Insert Mesh cannot stamp a body into itself.",
				$"The source body '{sourceName}' is also a target.",
				"Take it out of Bodies, or pick another source" );

		var brush = new InsertBrush( source.Name, source.Mesh );
		var session = new InsertMeshSession( brush );
		var rotationRadians = Rotation.Clamped * MathF.PI / 180f;
		var upHint = Tangent.Value.LengthSquared > 1e-12f ? Tangent.Value : new Vec3( 0, 1, 0 );

		switch ( Mode.Value )
		{
			case "Click":
			{
				var origin = Center.Value;
				var normal = Normal.Value;
				if ( normal.LengthSquared < 1e-12f )
				{
					var mesh = targets[0].Mesh;
					var bvh = MeshBVH.Build( mesh );
					var (lo, hi) = ModelMeasure.Bounds( mesh );
					var reach = MathF.Max( Scale.Clamped * 4f, (hi - lo).Length * 0.05f );
					var hit = bvh.NearestSurface( mesh, origin, reach );
					if ( hit is null )
						Fail( "The insert missed every body",
							"The centre is nowhere near the surface of the first target.",
							"Move the centre onto the body, or give a Normal to place it exactly" );
					origin = hit.Value.Point;
					normal = hit.Value.Normal;
				}
				session.Place( origin, normal, Scale.Clamped, rotationRadians, upHint );
				break;
			}
			case "Curve":
			{
				var points = ParseCurve( Curve.Value );
				if ( points.Count < 2 )
					FailOn( "Curve points",
						"A curve needs at least two points",
						$"Curve points is '{Curve.Value}'.",
						"Write points as \"x,y,z;x,y,z\", e.g. \"0,0,0;4,0,0;4,4,0\"" );
				session.PlaceAlongCurve( points, Spacing.Clamped, Scale.Clamped, upHint );
				break;
			}
			case "Scatter":
			{
				var normal = Normal.Value.LengthSquared > 1e-12f ? Normal.Value : new Vec3( 0, 0, 1 );
				session.Scatter( Count.Clamped, Center.Value, Radius.Clamped, Seed.Value,
					ScaleMin.Clamped, ScaleMax.Clamped, MathF.PI, Jitter.Clamped, normal );
				break;
			}
			default:
				FailOn( "Mode", $"Unknown insert mode '{Mode.Value}'", "The mode is Click, Curve or Scatter.", "Pick one of those" );
				break;
		}

		if ( session.Instances.Count == 0 )
			Fail( "Insert Mesh placed nothing.",
				"The placement produced no instances.",
				"Check Count and Radius in Scatter mode, or Spacing in Curve mode" );

		var vertices = 0;
		var faces = 0;
		foreach ( var body in targets )
		{
			var commit = session.Commit( body.Mesh, DecimateAfterCommit.Value, DecimateTarget.Clamped );
			vertices += commit.CommittedVertices;
			faces += commit.CommittedFaces;
		}

		LastInstanceCount = session.Instances.Count;
		LastCommittedVertices = vertices;
		LastCommittedFaces = faces;
	}

	static List<Vec3> ParseCurve( string text )
	{
		var points = new List<Vec3>();
		if ( string.IsNullOrWhiteSpace( text ) )
			return points;
		foreach ( var chunk in text.Split( ';' ) )
		{
			var part = chunk.Trim();
			if ( part.Length == 0 )
				continue;
			var nums = part.Split( ',' );
			if ( nums.Length != 3 )
				return new List<Vec3>();
			if ( !float.TryParse( nums[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x )
				|| !float.TryParse( nums[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y )
				|| !float.TryParse( nums[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var z ) )
				return new List<Vec3>();
			points.Add( new Vec3( x, y, z ) );
		}
		return points;
	}
}
