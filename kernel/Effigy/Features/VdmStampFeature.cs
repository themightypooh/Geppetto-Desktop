using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// A VDM (Vector Displacement Mesh) stamp as a feature: <c>add VdmStamp</c>.
///
/// It takes a body and stamps one of the built-in vector fields from
/// <see cref="VdmStampLibrary"/> onto it — <c>ear</c>, <c>nose</c>, <c>horn_hook</c>,
/// <c>scale_overhang</c>, <c>wound_lip</c> — displacing vertices along the field's vectors,
/// not just along the surface normal. That is the difference from a <see cref="StrokeFeature"/>
/// alpha stamp: a heightfield alpha can only bump, while a VDM stamp can curl a hook back
/// over its own base or jut a scale lip sideways past its footprint.
///
/// PLACEMENT. <see cref="Center"/> is a point in model space (landmarks allowed in a script).
/// When <see cref="Direction"/> is zero — the default — the stamp snaps to the nearest surface
/// point and takes that point's normal, the way a brush snaps to the skin it touches; when a
/// direction is given, the stamp is placed exactly at Center facing that direction.
/// <see cref="Tangent"/> aims the stamp's curl: the field's x-axis runs along the part of
/// Tangent perpendicular to the normal (a horn_hook curls toward +Tangent). Zero Tangent means
/// the frame is built from world +Y, or +X when the normal is parallel to +Y — deterministic,
/// but not aimed, so aim a hook explicitly.

/// FIELD FILES. <see cref="FieldFile"/> loads a field from disk instead of the built-in library:
/// a .json grid ({"width":W,"height":H,"vectors":[[x,y,z],...]}, row 0 = the field's bottom) or a
/// 3-channel RGB PNG (R = tangent x, G = bitangent y, B = normal z, 0..1 mapped to -1..1; the
/// PNG's top row is the field's top). Capture one with <c>add VdmCapture</c>, which bakes a mesh
/// region into exactly that .json format.
///
/// A stamp that moves nothing is an error, not a silent no-op: it is in the history claiming
/// to be a horn, and the skin is untouched. The usual causes are a centre off the body, a
/// radius smaller than the mesh's edge length, or zero strength, and the failure says which
/// to check — the same rule <see cref="StrokeFeature"/> follows.
/// </summary>
public sealed class VdmStampFeature : Feature
{
	public override string TypeName => "VdmStamp";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );

	/// <summary>The stamp: one of the built-in VDM library names.</summary>
	public readonly ChoiceParam Stamp = new( "Stamp", VdmStampLibrary.Names, 2 );

	/// <summary>A .json or .png field file to stamp instead of a built-in name. Empty means built-in.</summary>
	public readonly StringParam FieldFile = new( "VDM file", "" );

	/// <summary>The stamp's centre in model space (landmarks allowed in a script). Snapped to the nearest surface point when Direction is zero.</summary>
	public readonly Vec3Param Center = new( "Center", Vec3.Zero );

	/// <summary>The stamp's facing direction. Zero (default) means: take the nearest surface point's normal.</summary>
	public readonly Vec3Param Direction = new( "Direction", Vec3.Zero );

	/// <summary>Which way the stamp's curl points: the field's x-axis. Zero (default) means an automatic frame from world +Y.</summary>
	public readonly Vec3Param Tangent = new( "Tangent", Vec3.Zero );

	/// <summary>The stamp disc's radius, in model units.</summary>
	public readonly FloatParam Radius = new( "Radius", 1f, 0.001f, unit: "u" );

	/// <summary>How strongly the field displaces, in model units at full field value.</summary>
	public readonly FloatParam Strength = new( "Strength", 0.5f, 0f, 10f );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, Stamp, FieldFile, Center, Direction, Tangent, Radius, Strength };

	/// <summary>The last stamp result per body, for dialogs and tests. Empty before the first rebuild.</summary>
	public Dictionary<string, VdmStampResult> LastResults { get; } = new();

	VdmField LoadField()
	{
		var file = (FieldFile.Value ?? "").Trim();
		if ( file.Length == 0 )
			return VdmStampLibrary.Get( Stamp.Value );
		try
		{
			return VdmField.Load( file );
		}
		catch ( Exception ex )
		{
			FailOn( "VDM file",
				"The VDM field file could not be loaded",
				ex.Message,
				"Give a .json field file or a 3-channel RGB PNG",
				"Or clear VDM file to use a built-in stamp" );
			return null;
		}
	}


	protected override void Execute( FeatureContext ctx )
	{
		var field = LoadField();
		if ( field is null )
			Fail( "Unknown VDM stamp",
				$"No built-in stamp is called '{Stamp.Value}'.",
				"Pick one of: " + string.Join( ", ", VdmStampLibrary.Names ) );

		var radius = Radius.Clamped;
		var strength = Strength.Clamped;
		var touched = 0;
		var missed = new List<string>();
		var zero = new List<string>();
		LastResults.Clear();

		foreach ( var body in RequireBodies( ctx, Bodies ) )
		{
			var mesh = body.Mesh;
			var origin = Center.Value;
			var normal = Direction.Value;

			if ( normal.LengthSquared < 1e-12f )
			{
				var bvh = MeshBVH.Build( mesh );
				var (lo, hi) = ModelMeasure.Bounds( mesh );
				var reach = MathF.Max( radius * 4f, (hi - lo).Length * 0.05f );
				var hit = bvh.NearestSurface( mesh, origin, reach );
				if ( hit is null )
				{
					missed.Add( body.Name ?? body.Id );
					continue;
				}
				origin = hit.Value.Point;
				normal = hit.Value.Normal;
			}

			var upHint = Tangent.Value.LengthSquared > 1e-12f ? Tangent.Value : new Vec3( 0, 1, 0 );
			var result = VdmStamp.Apply( mesh, field, origin, normal, upHint, radius, strength );
			LastResults[body.Id] = result;

			if ( result.Affected == 0 )
			{
				zero.Add( body.Name ?? body.Id );
				continue;
			}
			touched += result.Affected;
		}

		if ( touched == 0 )
		{
			if ( missed.Count > 0 )
				Fail( "The stamp missed every body",
					"The centre is nowhere near the surface of the bodies it names.",
					"Move the centre onto the body, or use a landmark like @Body.front",
					"Check the Bodies selection" );
			Fail( "The stamp moved nothing",
				"No vertex sits under the stamp, or the strength is zero.",
				"Put a Subdivide above this, or make the radius bigger",
				"Check Strength and Center" );
		}

		if ( missed.Count + zero.Count > 0 )
			Warn( "The stamp missed some bodies",
				string.Join( "; ", missed.ConvertAll( m => m + ": the centre is nowhere near it" )
					.Concat( zero.ConvertAll( z => z + ": no vertex under the stamp" ) ) ) + ".",
				"Take those bodies out of the selection, or move the centre onto them" );
	}
}

/// <summary>
/// Bake a mesh region into a VDM field file: <c>add VdmCapture</c>.
///
/// It records, for each vertex inside the disc, its displacement from the base plane through
/// <see cref="Center"/> facing <see cref="Direction"/> (zero Direction snaps to the nearest
/// surface point and takes its normal, the same rule VdmStamp uses), averaged per grid cell
/// and faded at the rim — see <see cref="VdmField.Capture"/>. The result is written to
/// <see cref="Out"/> as .json, in exactly the format VdmStampFeature's VDM file loads, so a
/// captured horn re-stamped on another body with the same radius and strength reproduces the
/// source shape's silhouette. A capture that caught nothing is an error, not an empty file.
/// </summary>
public sealed class VdmCaptureFeature : Feature
{
	public override string TypeName => "VdmCapture";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );

	/// <summary>The capture centre in model space (landmarks allowed in a script).</summary>
	public readonly Vec3Param Center = new( "Center", Vec3.Zero );

	/// <summary>The base plane's facing direction. Zero (default) means: snap to the nearest surface point's normal.</summary>
	public readonly Vec3Param Direction = new( "Direction", Vec3.Zero );

	/// <summary>Which way the field's x-axis points. Zero (default) means an automatic frame from world +Y.</summary>
	public readonly Vec3Param Tangent = new( "Tangent", Vec3.Zero );

	/// <summary>The capture disc's radius, in model units.</summary>
	public readonly FloatParam Radius = new( "Radius", 1f, 0.001f, unit: "u" );

	/// <summary>Grid cells per side of the captured field.</summary>
	public readonly IntParam Grid = new( "Grid", 64, 2, 256 );

	/// <summary>Where the captured .json field is written.</summary>
	public readonly StringParam Out = new( "Output file", "" );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, Center, Direction, Tangent, Radius, Grid, Out };

	protected override void Execute( FeatureContext ctx )
	{
		var outPath = (Out.Value ?? "").Trim();
		if ( outPath.Length == 0 )
			FailOn( "Output file",
				"No output file for the capture",
				"The capture has nowhere to write its field.",
				"Give a .json path in Output file" );
		var radius = Radius.Clamped;
		var origin = Center.Value;
		var normal = Direction.Value;
		var upHint = Tangent.Value.LengthSquared > 1e-12f ? Tangent.Value : new Vec3( 0, 1, 0 );
		VdmField field = null;
		foreach ( var body in RequireBodies( ctx, Bodies ) )
		{
			var mesh = body.Mesh;
			var o = origin;
			var n = normal;
			if ( n.LengthSquared < 1e-12f )
			{
				var bvh = MeshBVH.Build( mesh );
				var (lo, hi) = ModelMeasure.Bounds( mesh );
				var reach = MathF.Max( radius * 4f, (hi - lo).Length * 0.05f );
				var hit = bvh.NearestSurface( mesh, o, reach );
				if ( hit is null )
					continue;
				o = hit.Value.Point;
				n = hit.Value.Normal;
			}
			var one = VdmField.Capture( mesh, o, n, upHint, radius, Grid.Clamped );
			if ( field is null )
				field = one;
			else
				for ( var i = 0; i < field.Vectors.Length && i < one.Vectors.Length; i++ )
					field.Vectors[i] += one.Vectors[i];
		}
		if ( field is null )
			Fail( "The capture missed every body",
				"The centre is nowhere near the surface of the bodies it names.",
				"Move the centre onto the body, or use a landmark like @Body.front",
				"Check the Bodies selection" );
		var any = false;
		foreach ( var v in field.Vectors )
			if ( v.LengthSquared > 1e-12f ) { any = true; break; }
		if ( !any )
			Fail( "The capture caught nothing",
				"No vertex inside the disc sits off the base plane.",
				"Put the centre on a raised region, or make the radius bigger",
				"Check Strength and Center" );
		field.Save( outPath );
	}
}
