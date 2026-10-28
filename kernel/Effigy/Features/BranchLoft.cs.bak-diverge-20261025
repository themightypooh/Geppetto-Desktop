using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Skin a trunk that SPLITS into branches — one section becoming several. The crane's
/// struts, a tree's limbs, a river delta: one column that forks into N tubes.
/// Onshape has no direct equivalent; this is the loft's branching cousin.
///
/// HOW THE JUNCTION STAYS ONE SOLID, NOT OVERLAPPING SOLIDS. A trunk ring cannot be
/// stitched straight to N branch rings and stay manifold — one ring's edges would end
/// up shared by the trunk and every branch at once, which is non-manifold by definition,
/// and a junction surface triangulated between them by hand would be a research project
/// dressed as a feature. So this builds honest closed solids and unions them: the trunk
/// skinned base-to-junction, and each branch skinned from a start ring pulled slightly
/// BACK into the trunk (so the overlap is volumetric, never just two flush faces
/// meeting at the junction plane) out to its tip section. ExactMeshBoolean unions them
/// into one closed mesh with the internal faces cut away. The delivered mesh is one
/// solid — check it with MeshValidator, not with a body count — and the overlap is a
/// construction detail the boolean removes, not a seam the user is handed.
///
/// V1 LIMITS, STATED NOT HIDDEN. Branches are single-section: each branch runs from the
/// junction section's shape to one tip section, with one midpoint ring for a less
/// ruled look. A branch with several sections of its own is the follow-up, not a silent
/// stretch of this one. No holes: a holed trunk splitting into holed branches needs the
/// multi-hole skinning carried through the union, which is not done here. Branch start
/// sections are all the junction section's shape — branches differ by their tip section
/// and direction, not by a per-branch start shape.
/// </summary>
public sealed class BranchLoftFeature : SketchConsumingFeature
{
	public override string TypeName => "Branch loft";

	/// <summary>
	/// The trunk's section sketches, in order. The last one is the junction — the
	/// section the branches split from. Unlike Loft, there is no "every sketch"
	/// fallback: with branches in play, guessing which sketches are trunk and which
	/// are tips would guess wrong in exactly the cases that matter.
	/// </summary>
	public List<string> Sections = new();

	/// <summary>
	/// One tip section sketch per branch, in order. Each branch runs from the junction
	/// section to its tip section. At least one is required; the task's crane case is
	/// four, and nothing here caps the count.
	/// </summary>
	public List<string> Branches = new();

	/// <summary>How many points each section is resampled to. More is smoother and heavier; the
	/// default is enough for a circle to read as round without subdivision.</summary>
	public readonly IntParam Segments = new( "Segments", 24, 3, 512 );

	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Segments, Result, Material };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Material };

	protected override void Execute( FeatureContext ctx )
	{
		if ( Sections.Count < 2 )
			Fail(
				"A branch loft needs at least two trunk sections.",
				$"Only {Sections.Count} trunk section(s) were given, so there is no trunk to split.",
				"Give the trunk at least a base section and a junction section",
				"Use a plain Loft for a trunk with no branches" );

		if ( Branches.Count < 1 )
			Fail(
				"A branch loft needs at least one branch.",
				"No branch tip section was given, so there is nothing to split into.",
				"Give at least one branch tip section",
				"Use a plain Loft for a trunk with no branches" );

		var trunkSketches = ResolveSections( ctx );
		var count = Segments.Clamped;

		// Trunk rings: resample + align, exactly the plain-loft path (no holes here),
		// so a trunk on its own rebuilds the way a loft would.
		var rings = new List<List<Vec3>>( trunkSketches.Count );

		foreach ( var sketch in trunkSketches )
		{
			var found = ProfileFinder.Find( sketch );

			if ( found.Profiles.Count == 0 )
				Fail(
					"One of the trunk's sections has no closed region in it.",
					"Every trunk section has to be a closed loop so it can be skinned to the next.",
					"Close that section's curves into a loop",
					"Remove that sketch from the trunk" );

			var ring = Skinner.Resample( found.Profiles[0].Outer, count )
				.Select( sketch.Plane.ToWorld ).ToList();

			if ( rings.Count > 0 )
				ring = Skinner.Align( ring, rings[^1] );

			rings.Add( ring );
		}

		var junction = rings[^1];
		var junctionCentre = SweepFeature.CentreOf3( junction );
		var prevCentre = SweepFeature.CentreOf3( rings[^2] );
		var trunkDir = (junctionCentre - prevCentre).Normal;

		// How far back into the trunk each branch starts: a quarter of the junction
		// section's mean radius. Enough that the union overlap is a volume, small
		// enough that the branch does not swallow the trunk's lower half.
		var meanRadius = 0f;

		foreach ( var p in junction )
			meanRadius += (p - junctionCentre).Length;

		meanRadius /= junction.Count;

		var overlap = meanRadius * 0.25f;

		var result = Skinner.SkinChecked(
			rings.Select( r => new List<Vec3>( r ) ).ToList(), capEnds: true, Material.Clamped );

		foreach ( var branchId in Branches )
		{
			if ( !ctx.Sketches.TryGetValue( branchId, out var tipSketch ) )
				Fail(
					$"Sketch '{branchId}' is not available at this point in the tree",
					"A branch this loft was pointed at has been deleted, suppressed, or sits below this feature.",
					"Pick a sketch that sits above this feature",
					"Move this feature below that sketch" );

			var found = ProfileFinder.Find( tipSketch );

			if ( found.Profiles.Count == 0 )
				Fail(
					"One of the branch tips has no closed region in it.",
					"Every branch tip has to be a closed loop so it can be skinned from the junction.",
					"Close that tip's curves into a loop",
					"Remove that sketch from the branches" );

			var tip = Skinner.Resample( found.Profiles[0].Outer, count )
				.Select( tipSketch.Plane.ToWorld ).ToList();
			tip = Skinner.Align( tip, junction );

			var start = junction.Select( p => p - trunkDir * overlap ).ToList();
			var mid = new List<Vec3>( count );

			for ( var i = 0; i < count; i++ )
				mid.Add( (start[i] + tip[i]) * 0.5f );

			var branch = Skinner.SkinChecked(
				new List<List<Vec3>> { start, mid, tip }, capEnds: true, Material.Clamped );

			if ( !ExactMeshBoolean.Instance.TryApply( BooleanOp.Union, result, branch, out var union, out var error ) )
				Fail(
					"Branch union failed",
					error ?? "The exact boolean refused the trunk/branch union.",
					"Simplify the branch sections", "Lower the segment count" );

			result = union;
		}

		if ( !MeshValidator.Validate( result ).IsClosed )
			Fail(
				"Branch loft came back open",
				"The trunk/branch union did not close, so the result is not a solid.",
				"Simplify the branch sections", "Lower the segment count" );

		Emit( ctx, result );
	}

	List<Sketch> ResolveSections( FeatureContext ctx )
	{
		var result = new List<Sketch>( Sections.Count );

		foreach ( var id in Sections )
		{
			if ( !ctx.Sketches.TryGetValue( id, out var sketch ) )
			{
				Fail(
					$"Sketch '{id}' is not available at this point in the tree",
					"A section this branch loft was pointed at has been deleted, suppressed, or sits below this feature.",
					"Pick a sketch that sits above this feature",
					"Move this feature below that sketch" );
			}

			result.Add( sketch );
		}

		return result;
	}
}
