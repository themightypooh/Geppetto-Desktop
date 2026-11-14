using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// What <see cref="RigCheck.Check"/> found wrong with a rig's weights. Counts, not
/// exceptions — mid-edit states are allowed to be briefly invalid, and the report is
/// what the UI shows and what gates export. <see cref="Passes"/> is the gate: every
/// gate count at zero. <see cref="UnusedBones"/> is information only — a bone with no
/// weight anywhere is a rigging smell, not an export failure.
/// Named RigWeightCheckReport (not RigCheckReport) because RigQuality.cs already
/// defines a RigCheckReport for the auto-rig grading check (cap loss, symmetry,
/// non-deform bones); this one is the strict weight-paint gate.
/// </summary>
public sealed class RigWeightCheckReport
{
	/// <summary>Vertices examined.</summary>
	public int TotalVertices;

	/// <summary>Vertices with no influences at all. An unweighted vertex is pinned to
	/// bone 0 by the exporters — the whole mesh collapses onto the root if it is a body.</summary>
	public int Unweighted;

	/// <summary>Vertices whose influences are all zero (non-empty, but nothing there).</summary>
	public int ZeroWeight;

	/// <summary>Vertices whose influences do not sum to 1 (past <c>tolerance</c>). The
	/// mesh bends differently in the game than here.</summary>
	public int Unnormalized;

	/// <summary>Worst |sum − 1| over the unnormalized vertices, and which vertex it was.</summary>
	public float WorstDeviation;

	/// <summary>The sum of the worst vertex's influences.</summary>
	public float WorstSum;

	/// <summary>The worst vertex's index, or −1 when nothing is unnormalized.</summary>
	public int WorstVertex = -1;

	/// <summary>Vertices weighted to more than the influence cap. The game keeps the
	/// strongest <c>maxInfluences</c> and drops the rest.</summary>
	public int OverLimit;

	/// <summary>Influences naming a bone index that is not in the rig.</summary>
	public int BadBoneRef;

	/// <summary>Non-finite weights (NaN or Infinity). A weight that is not a number
	/// poisons every blend it touches.</summary>
	public int NonFinite;

	/// <summary>Bones with no weight anywhere. Information only — a control bone is
	/// supposed to have none; a deform bone with none is a mistake, but this report
	/// cannot tell them apart.</summary>
	public int UnusedBones;

	/// <summary>Every gate count at zero: the rig may be offered for export.</summary>
	public bool Passes;

	/// <summary>Human-readable lines, one per finding, plus the verdict first.</summary>
	public List<string> Lines = new();

	/// <summary>One line: "Rig Check: PASS — …" or "Rig Check: FAIL — …".</summary>
	public string Summary = "";
}

/// <summary>
/// The pre-export Rig Check: 0 unnormalized, 0 zero-weight, 0 over-limit — the bar the
/// roadmap sets before export is offered. Runs on the weights as they are, in the
/// kernel, so the editor, a bot command and the final build all ask the same question
/// and get the same answer. The fix is <see cref="WeightPaintTools"/> — Normalize,
/// LimitTotal, PruneThreshold — not this file: a checker that rewrites the rig it
/// checks is a checker you cannot trust.
/// </summary>
public static class RigCheck
{
	/// <summary>Check a rig's weights against the export bar. <paramref name="maxInfluences"/>
	/// is 4 for game export (8 PC-only); <paramref name="tolerance"/> is how far a
	/// vertex's sum may stray from 1.0 before it counts as unnormalized.</summary>
	public static RigWeightCheckReport Check( Skeleton skeleton, SkinWeights weights, int maxInfluences = 4, float tolerance = 1e-3f )
	{
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );
		if ( weights is null ) throw new ArgumentNullException( nameof( weights ) );
		if ( maxInfluences < 1 ) throw new ArgumentOutOfRangeException( nameof( maxInfluences ), "The influence cap is at least 1." );
		if ( !(tolerance >= 0f) ) throw new ArgumentOutOfRangeException( nameof( tolerance ), "Tolerance cannot be negative." );

		var report = new RigWeightCheckReport { TotalVertices = weights.Count };
		var boneTotals = new float[skeleton.Count];

		for ( var v = 0; v < weights.Count; v++ )
		{
			var w = weights[v];
			if ( w is null || w.Length == 0 )
			{
				report.Unweighted++;
				continue;
			}

			var positive = 0;
			var sum = 0f;
			var bad = false;

			foreach ( var bw in w )
			{
				if ( !float.IsFinite( bw.Weight ) )
				{
					report.NonFinite++;
					bad = true;
					continue;
				}
				if ( bw.Bone < 0 || bw.Bone >= skeleton.Count )
				{
					report.BadBoneRef++;
					bad = true;
					continue;
				}
				if ( bw.Weight <= 0f ) continue;
				positive++;
				sum += bw.Weight;
				boneTotals[bw.Bone] += bw.Weight;
			}

			if ( positive == 0 )
			{
				report.ZeroWeight++;
				continue;
			}

			if ( positive > maxInfluences ) report.OverLimit++;

			if ( !bad )
			{
				var deviation = MathF.Abs( sum - 1f );
				if ( deviation > tolerance )
				{
					report.Unnormalized++;
					if ( deviation > report.WorstDeviation )
					{
						report.WorstDeviation = deviation;
						report.WorstSum = sum;
						report.WorstVertex = v;
					}
				}
			}
		}

		report.UnusedBones = boneTotals.Count( t => t <= 0f );
		report.Passes = report.Unweighted == 0 && report.ZeroWeight == 0 && report.Unnormalized == 0
			&& report.OverLimit == 0 && report.BadBoneRef == 0 && report.NonFinite == 0;

		var lines = report.Lines;
		lines.Add( report.Passes
			? $"Rig Check: PASS — {report.TotalVertices} vertices, all normalized, at most {maxInfluences} influences, no unweighted vertices."
			: $"Rig Check: FAIL — {report.TotalVertices} vertices." );
		if ( report.Unweighted > 0 ) lines.Add( $"{report.Unweighted} unweighted vertices (no influences; export pins them to bone 0)." );
		if ( report.ZeroWeight > 0 ) lines.Add( $"{report.ZeroWeight} zero-weight vertices (influences sum to 0)." );
		if ( report.Unnormalized > 0 ) lines.Add( $"{report.Unnormalized} unnormalized vertices (worst vertex {report.WorstVertex} sums to {report.WorstSum:0.###}, off by {report.WorstDeviation:0.###})." );
		if ( report.OverLimit > 0 ) lines.Add( $"{report.OverLimit} vertices over {maxInfluences} influences (the game keeps the strongest {maxInfluences})." );
		if ( report.BadBoneRef > 0 ) lines.Add( $"{report.BadBoneRef} influences name a bone that is not in the rig." );
		if ( report.NonFinite > 0 ) lines.Add( $"{report.NonFinite} non-finite weights (NaN or Infinity)." );
		if ( report.UnusedBones > 0 ) lines.Add( $"{report.UnusedBones} bones have no weight anywhere." );
		report.Summary = lines[0];
		return report;
	}

	/// <summary>Whether export may be offered for this report — the gate. A report that
	/// passes is exportable; anything else needs the weight-paint tools first.</summary>
	public static bool CanExport( RigWeightCheckReport report )
	{
		if ( report is null ) throw new ArgumentNullException( nameof( report ) );
		return report.Passes;
	}
}
