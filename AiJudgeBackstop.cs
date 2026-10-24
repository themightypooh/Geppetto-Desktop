#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Marionette.Desktop;

/// <summary>Light judge backstop — builder-first, judge as safety net only (wave 3).
/// Joshua's correction, 2026-10-24: builders being competent matters MORE than judges.
/// Judges are NOT co-equal. Budget is roughly 85% building / 15% judging, and judging
/// is the first thing cut when a build runs long. This file is deliberately small and
/// cheap: it takes numbers in, it never loads a model, never renders, never calls a
/// vision provider. A vision score arrives here as a VALUE (AiScore), so by construction
/// builder + vision judge + renderer are never resident together through this API —
/// the caller must finish with the vision model before calling Apply, and the backstop
/// itself holds no model handle at all.
/// What changed vs Lane O's AiJudge.HybridScore, and why:
///   - HybridScore returns min(vision total, lowest gate score) and keeps vision.Lowest
///     as the score's lowest. One harsh gate — or one harsh vision category — tanks a
///     good model. The backstop does NOT do that. Hard fails are vetoes only
///     (non-manifold, inside-out, silhouette way off, size off by 1.6x). Everything
///     else is a separate soft score, reported, never minned into a ruling number.
///   - The vision category floor is advisory, not a veto. A model whose vision total
///     meets its target but whose lowest category misses the floor still passes the
///     backstop, with the miss written into Notes. The user in the viewport is the
///     final judge; taps win, these notes are advisory except for hard fails.
///   - Size vs prop_sizes.yaml (1.6x rule) is a hard fail here, not a soft hint.
///     AiJudge.Gate has no size input at all; the backstop adds it.
/// A gate that was not measured never vetoes anything — absence of evidence is
/// reported in Notes, not assumed to be a pass or a fail.</summary>
public sealed record AiMeasuredFacts(
    AiGateInput Gates,
    double? SizeRatio = null,
    string? PropName = null)
{
    /// <summary>Size is off when the worst axis ratio exceeds 1.6x, either direction.</summary>
    public bool SizeOff => SizeRatio is double r && r > 1.6;

    public static bool IsSizeOff(double actual, double typical)
    {
        if (actual <= 0 || typical <= 0) throw new ArgumentException("Sizes must be positive.");
        return Math.Max(actual / typical, typical / actual) > 1.6;
    }

    /// <summary>Worst-axis ratio from actual vs typical [W, D, H]; null when either is missing.</summary>
    public static double? WorstRatio(double[]? actual, double[]? typical)
    {
        if (actual is null || typical is null || actual.Length < 3 || typical.Length < 3) return null;
        double worst = 1.0;
        for (int i = 0; i < 3; i++)
        {
            if (actual[i] <= 0 || typical[i] <= 0) continue;
            worst = Math.Max(worst, Math.Max(actual[i] / typical[i], typical[i] / actual[i]));
        }
        return worst;
    }
}

/// <summary>Soft scores, kept separate on purpose. A null means that dimension was not
/// measured for this verdict — it is not a zero. Style is the vision total, labelled
/// as what it is: an opinion about looks, not a measurement.</summary>
public sealed record AiSoftScores(
    double? Geometry,
    double? Size,
    double? Symmetry,
    double? Style);

public sealed record AiBackstopVerdict(
    double VisionTotal,
    double? VisionLowest,
    AiSoftScores Soft,
    IReadOnlyList<string> HardFails,
    bool Passed,
    bool UserDecides,
    string Notes,
    AiGateResult Gates)
{
    public JsonObject Json()
    {
        var hard = new JsonArray();
        foreach (var f in HardFails) hard.Add(f);
        var soft = new JsonObject
        {
            ["geometry"] = Soft.Geometry is double g ? Math.Round(g, 2) : null,
            ["size"] = Soft.Size is double s ? Math.Round(s, 2) : null,
            ["symmetry"] = Soft.Symmetry is double y ? Math.Round(y, 2) : null,
            ["style"] = Soft.Style is double t ? Math.Round(t, 2) : null,
        };
        return new JsonObject
        {
            ["visionTotal"] = Math.Round(VisionTotal, 2),
            ["visionLowest"] = VisionLowest is double l ? Math.Round(l, 2) : null,
            ["soft"] = soft,
            ["hardFails"] = hard,
            ["passed"] = Passed,
            ["userDecides"] = UserDecides,
            ["notes"] = Notes,
            ["gates"] = Gates.Json(),
        };
    }

    public AiBackstopVerdict ForUser() => this with { UserDecides = true };
}

public static class AiJudgeBackstop
{
    /// <summary>ONE judge pass, max TWO fix loops, then the model goes to the user.
    /// A fix loop is one builder fix plus one re-judge, so a backstop loop produces
    /// at most 3 verdicts total. See AiBackstopLoop.</summary>
    public const int MaxJudgePasses = 1;
    public const int MaxFixLoops = 2;
    public const int MaxVerdicts = MaxJudgePasses + MaxFixLoops;

    /// <summary>Apply measured facts to a vision score. Measured reality overrules
    /// vision: a vision score of 8.57 on a model with a non-manifold hole, inside-out
    /// winding, a silhouette under 0.80 IoU, or a size off by more than 1.6x does NOT
    /// pass, whatever the vision model thought. Conversely a vision total that meets
    /// its target passes even if one soft gate or one vision category is harsh —
    /// those are reported in Soft/Notes, not used as a veto.</summary>
    public static AiBackstopVerdict Apply(AiScore vision, AiMeasuredFacts facts, double target, double? floor = null)
    {
        if (vision is null) throw new ArgumentException("A vision score is required.");
        if (facts is null) throw new ArgumentException("Measured facts are required.");
        var gateResult = AiJudge.Gate(facts.Gates);
        var hardFails = new List<string>();
        foreach (var g in gateResult.Gates.Where(g => g.HardFail))
            hardFails.Add(g.Name + ": " + g.Detail);
        if (facts.SizeOff)
            hardFails.Add($"size: worst-axis ratio {facts.SizeRatio:0.00}x vs typical{(facts.PropName is null ? "" : " " + facts.PropName)} — over the 1.6x rule");

        double? GateScore(string name) => gateResult.Gates.FirstOrDefault(g => g.Name == name)?.Score;
        double? geometry = null;
        var geoParts = new[] { GateScore("manifold"), GateScore("quads"), GateScore("ngons") }.Where(v => v is not null).Select(v => v!.Value).ToArray();
        if (geoParts.Length > 0) geometry = geoParts.Average();
        double? size = facts.SizeRatio is null ? null
            : facts.SizeRatio.Value <= 1.6 ? 10.0
            : Math.Max(0.0, 10.0 * 1.6 / facts.SizeRatio.Value);
        var soft = new AiSoftScores(geometry, size, GateScore("symmetry"), vision.Total);

        var notes = new List<string>();
        foreach (var g in gateResult.Gates.Where(g => g.Score < 10))
            notes.Add(g.Name + ": " + g.Detail);
        if (facts.SizeRatio is double ratio)
            notes.Add($"size ratio {ratio:0.00}x vs typical{(facts.PropName is null ? "" : " " + facts.PropName)}");
        else
            notes.Add("size vs typical prop size not measured (no prop match) — not assumed good or bad");
        if (floor is double f && vision.Lowest < f)
            notes.Add($"vision lowest category {vision.Lowest:0.00} below floor {f:0.00} — advisory only, not a veto");
        if (hardFails.Count > 0)
            notes.Add("HARD FAIL — model does not pass, whatever the vision score");

        bool passed = vision.Total >= target && hardFails.Count == 0;
        return new AiBackstopVerdict(
            vision.Total, vision.Lowest, soft, hardFails, passed, false,
            notes.Count == 0 ? "no measured problems; vision stands" : string.Join("; ", notes),
            gateResult);
    }
}

/// <summary>The capped fix loop. One initial judge pass, then at most two fix loops
/// (each: NoteFix, then Judge again). After that, Judge throws and the caller must
/// hand the model to the user via FinalizeForUser — the loop never silently keeps
/// revising, and it never decides on its own that a model is done after the cap.
/// This loop is for new backstop callers; AiCli's existing round/plateau loop is
/// wired to the verdict (see AiCli.TaskAsync) but keeps its own round accounting,
/// so existing plateau/budget behaviour is unchanged.</summary>
public sealed class AiBackstopLoop
{
    public int Verdicts { get; private set; }
    public int FixLoops { get; private set; }
    public bool Exhausted => Verdicts >= AiJudgeBackstop.MaxVerdicts;

    public AiBackstopVerdict Judge(AiScore vision, AiMeasuredFacts facts, double target, double? floor = null)
    {
        if (Exhausted) throw new InvalidOperationException("Backstop loop exhausted: 1 judge pass + 2 fix loops used. Hand the model to the user.");
        Verdicts++;
        return AiJudgeBackstop.Apply(vision, facts, target, floor);
    }

    public void NoteFix()
    {
        if (FixLoops >= AiJudgeBackstop.MaxFixLoops) throw new InvalidOperationException("Max 2 fix loops used. Hand the model to the user.");
        FixLoops++;
    }

    public AiBackstopVerdict FinalizeForUser(AiBackstopVerdict last) => last.ForUser();
}
