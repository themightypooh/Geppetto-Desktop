using System;
using System.Collections.Generic;

namespace Effigy;

public enum ExportIssueSeverity { Warning, Error }

/// <summary>One thing wrong (or worth knowing) about a rig that is about to be exported.</summary>
public sealed class ExportIssue
{
	public ExportIssueSeverity Severity { get; }
	public string Code { get; }
	public string Message { get; }
	public string Bone { get; }
	public ExportIssue( ExportIssueSeverity severity, string code, string message, string bone = null )
		{ Severity = severity; Code = code; Message = message; Bone = bone; }
}

/// <summary>Result of a pre-export validation. Pass means the rig is safe to export: no errors.
/// Warnings are things an engine tolerates but a careful exporter still wants to know about.</summary>
public sealed class ExportValidationReport
{
	public bool Pass { get; }
	public List<ExportIssue> Issues { get; } = new();
	public ExportValidationReport( bool pass, List<ExportIssue> issues )
		{ Pass = pass; Issues = issues; }
}

/// <summary>Pre-export validation: the checks an engine does when a rig arrives, run here
/// before the file is written, so a bad rest pose or a broken weight set is caught in the
/// studio instead of in Unity or Unreal.
///
/// What already exists and is NOT duplicated: <see cref="RigDiagnostics.Check"/> (skeleton
/// structure only — names, parents, cycles) and <see cref="SkinWeights.Validate"/> (the
/// weights themselves). This runs those and adds the export contract: exactly one root at the
/// origin, a T- or A-pose rest pose, and weights that sum to one. See also
/// <see cref="RigDiagnostics"/> for what "structure" covers.</summary>
public static class ExportValidation
{
	/// <summary>Validate a rig for export. <paramref name="weights"/> and
	/// <paramref name="vertexCount"/> are optional: pass them when the mesh is rigged, and
	/// the weight contract is checked too. Pass = no errors.</summary>
	public static ExportValidationReport Validate( Skeleton skeleton, SkinWeights weights = null, int vertexCount = 0 )
	{
		var issues = new List<ExportIssue>();
		if ( skeleton is null )
		{
			issues.Add( new ExportIssue( ExportIssueSeverity.Error, "no-skeleton", "There is no skeleton to export." ) );
			return new ExportValidationReport( false, issues );
		}
		if ( skeleton.Count == 0 )
		{
			issues.Add( new ExportIssue( ExportIssueSeverity.Error, "empty-skeleton", "The skeleton has no bones." ) );
			return new ExportValidationReport( false, issues );
		}

		// The structure checks that already exist, folded in so one call is the whole answer.
		foreach ( var problem in RigDiagnostics.Check( skeleton ) )
			issues.Add( new ExportIssue(
				problem.Severity == RigSeverity.Error ? ExportIssueSeverity.Error : ExportIssueSeverity.Warning,
				"structure",
				string.IsNullOrWhiteSpace( problem.Cause ) ? problem.Problem : problem.Problem + " — " + problem.Cause,
				problem.Bone >= 0 && problem.Bone < skeleton.Count ? skeleton.Bones[problem.Bone].Name : null ) );

		var roots = new List<int>();
		for ( var i = 0; i < skeleton.Count; i++ )
			if ( skeleton.Bones[i].Parent < 0 )
				roots.Add( i );
		if ( roots.Count != 1 )
			issues.Add( new ExportIssue( ExportIssueSeverity.Error, "root-count",
				$"Export needs exactly one root bone; this skeleton has {roots.Count}." ) );
		else
		{
			var root = skeleton.Bones[roots[0]];
			if ( root.Local.Origin.Length > 0.001f )
				issues.Add( new ExportIssue( ExportIssueSeverity.Error, "root-off-origin",
					$"Root bone '{root.Name}' sits at {root.Local.Origin}, not the origin. Engines place the character at the root; an offset root makes the character stand off the ground.",
					root.Name ) );
			if ( root.Name != "root" )
				issues.Add( new ExportIssue( ExportIssueSeverity.Warning, "root-name",
					$"Root bone is named '{root.Name}', not 'root'. Engines accept it, but standard presets and retargeting expect 'root'.",
					root.Name ) );
		}

		CheckRestPose( skeleton, issues );
		CheckTwists( skeleton, issues );
		if ( weights is not null )
			CheckWeights( weights, vertexCount, skeleton, issues );

		var pass = true;
		foreach ( var issue in issues )
			if ( issue.Severity == ExportIssueSeverity.Error )
				pass = false;
		return new ExportValidationReport( pass, issues );
	}

	static void CheckRestPose( Skeleton skeleton, List<ExportIssue> issues )
	{
		// A rest pose the engines recognise: arms out from the body (T or A), legs straight
		// down. A rig posed with its arms hanging at its sides imports as a T-pose anyway
		// in most engines, and the mismatch bends the shoulders on the first animation.
		var arms = new (string Upper, string Lower)[] { ("arm_upper_L", "arm_lower_L"), ("arm_upper_R", "arm_lower_R") };
		foreach ( var (upper, lower) in arms )
		{
			var u = skeleton.IndexOf( upper );
			if ( u < 0 )
				continue;
			var dir = skeleton.BoneDirection( u );
			// Arm direction against straight down: a T-pose arm is ~90° out, an A-pose ~45°.
			var down = new Vec3( 0, -1, 0 );
			var angleFromDown = AngleDegrees( dir, down );
			if ( angleFromDown < 30f )
				issues.Add( new ExportIssue( ExportIssueSeverity.Error, "rest-pose-arms",
					$"'{upper}' hangs {angleFromDown:F0}° from straight down — the rest pose is not a T- or A-pose. Raise the arms at least 30° from vertical before export.",
					upper ) );
		}
		var legs = new (string Upper, string Lower)[] { ("leg_upper_L", "leg_lower_L"), ("leg_upper_R", "leg_lower_R") };
		foreach ( var (upper, lower) in legs )
		{
			var u = skeleton.IndexOf( upper );
			if ( u < 0 )
				continue;
			var dir = skeleton.BoneDirection( u );
			var angleFromDown = AngleDegrees( dir, new Vec3( 0, -1, 0 ) );
			if ( angleFromDown > 20f )
				issues.Add( new ExportIssue( ExportIssueSeverity.Error, "rest-pose-legs",
					$"'{upper}' points {angleFromDown:F0}° from straight down — the legs are bent or splayed in the rest pose. Straighten them before export.",
					upper ) );
		}
	}

	static void CheckTwists( Skeleton skeleton, List<ExportIssue> issues )
	{
		foreach ( var limb in new[] { "arm_lower_L", "arm_lower_R", "leg_lower_L", "leg_lower_R" } )
		{
			var bone = skeleton.IndexOf( limb );
			if ( bone < 0 || TwistBones.OfLimb( skeleton, bone ).Length > 0 )
				continue;
			issues.Add( new ExportIssue( ExportIssueSeverity.Warning, "no-twist-bones",
				$"'{limb}' has no twist bones, so an axial roll lands on one ring of vertices and pinches. See TwistBones.AddTwists.",
				limb ) );
		}
	}

	static void CheckWeights( SkinWeights weights, int vertexCount, Skeleton skeleton, List<ExportIssue> issues )
	{
		if ( vertexCount > 0 && weights.Count != vertexCount )
			issues.Add( new ExportIssue( ExportIssueSeverity.Error, "weight-count",
				$"The weight set has {weights.Count} vertices and the mesh has {vertexCount}." ) );
		for ( var v = 0; v < weights.Count; v++ )
		{
			var influences = weights[v];
			if ( influences is null || influences.Length == 0 )
			{
				issues.Add( new ExportIssue( ExportIssueSeverity.Error, "zero-influence",
					$"Vertex {v} has no bone influences and will not move with the rig." ) );
				continue;
			}
			if ( influences.Length > 4 )
				issues.Add( new ExportIssue( ExportIssueSeverity.Error, "too-many-influences",
					$"Vertex {v} has {influences.Length} influences; engines cap at 4 and will drop the rest." ) );
			var sum = 0f;
			foreach ( var influence in influences )
			{
				if ( influence.Bone < 0 || influence.Bone >= skeleton.Count )
				{
					issues.Add( new ExportIssue( ExportIssueSeverity.Error, "bad-bone-index",
						$"Vertex {v} is weighted to bone index {influence.Bone}, which does not exist." ) );
					continue;
				}
				if ( influence.Weight < 0.01f )
					issues.Add( new ExportIssue( ExportIssueSeverity.Warning, "tiny-weight",
						$"Vertex {v} has a {influence.Weight:F3} weight on '{skeleton.Bones[influence.Bone].Name}' — below the 0.01 prune threshold, it costs a slot and does nothing." ) );
				sum += influence.Weight;
			}
			if ( MathF.Abs( sum - 1f ) > 0.01f )
				issues.Add( new ExportIssue( ExportIssueSeverity.Error, "weight-sum",
					$"Vertex {v} weights sum to {sum:F3}, not 1. The vertex will scale or sink when the rig moves." ) );
		}
	}

	static float AngleDegrees( Vec3 a, Vec3 b )
	{
		var dot = Vec3.Dot( a.Normal, b.Normal );
		if ( dot > 1f ) dot = 1f;
		if ( dot < -1f ) dot = -1f;
		return MathF.Acos( dot ) * (180f / MathF.PI);
	}
}
