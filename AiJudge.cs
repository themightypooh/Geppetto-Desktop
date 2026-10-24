#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Marionette.Desktop;

public sealed record AiCriterion(string Name, double Weight, string Description);
public sealed record AiScore(double Total, double Lowest, bool Passed, JsonNode Evidence);

public sealed record AiRubric(double Target, double Floor, IReadOnlyList<AiCriterion> Categories)
{
    public static JsonNode Json(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int newline = text.IndexOf('\n'); int end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (newline < 0 || end <= newline) throw new ArgumentException("Expected JSON, not an incomplete code fence.");
            text = text[(newline + 1)..end];
        }
        return JsonNode.Parse(text) ?? throw new ArgumentException("Expected JSON object.");
    }

    public static double Number(JsonNode? node, double min, double max)
    {
        double n = node?.GetValue<double>() ?? throw new ArgumentException("Missing numeric value.");
        if (!double.IsFinite(n) || n < min || n > max) throw new ArgumentException($"Value must be finite in [{min},{max}].");
        return n;
    }

    public static AiRubric Parse(string text, double? target = null, double? floor = null)
    {
        var j = Json(text);
        double t = Number(JsonValue.Create(target) ?? j["target"], 0, 10);
        double f = Number(JsonValue.Create(floor) ?? j["floor"], 0, 10);
        if (f > t) throw new ArgumentException("Category floor cannot exceed target.");
        var categories = new List<AiCriterion>();
        foreach (var c in j["categories"] as JsonArray ?? throw new ArgumentException("Missing categories."))
        {
            string name = c?["name"]?.GetValue<string>() ?? "", description = c?["description"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description) || categories.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Rubric categories require unique names and checkable descriptions.");
            categories.Add(new(name, Number(c?["weight"], 0.001, 100), description));
        }
        if (categories.Count is < 1 or > 12) throw new ArgumentException("Use 1–12 rubric categories.");
        return new(t, f, categories);
    }

    public string Serialize() => new JsonObject {
        ["target"] = Target, ["floor"] = Floor,
        ["categories"] = new JsonArray(Categories.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["weight"] = c.Weight, ["description"] = c.Description }).ToArray())
    }.ToJsonString();

    public AiScore Score(JsonNode report)
    {
        var entries = report["categories"] as JsonArray ?? throw new ArgumentException("Judge must score every category.");
        if (entries.Count != Categories.Count) throw new ArgumentException("Judge changed the rubric.");
        double weighted = 0, lowest = 10;
        foreach (var c in Categories)
        {
            var matching = entries.Where(x => x?["name"]?.GetValue<string>() == c.Name).ToArray();
            if (matching.Length != 1) throw new ArgumentException("Judge changed or duplicated a category.");
            var e = matching[0]!;
            if (string.IsNullOrWhiteSpace(e["evidence"]?.GetValue<string>())) throw new ArgumentException("A score requires observed evidence.");
            if (e["fix"] == null) throw new ArgumentException("Each score requires a fix field (empty if none).");
            double score = Number(e["score"], 0, 10);
            weighted += score * c.Weight; lowest = Math.Min(lowest, score);
        }
        double total = weighted / Categories.Sum(c => c.Weight);
        return new(total, lowest, total >= Target && lowest >= Floor, report.DeepClone());
    }
}

/// <summary>Plateau measures cumulative progress from its last significant high-water mark.</summary>
public sealed class AiProgress
{
    readonly int patience; readonly double delta;
    double highWater = double.NegativeInfinity, best = double.NegativeInfinity;
    int stale, round = -1;
    public int BestRound { get; private set; } = -1;
    public AiProgress(int patience, double delta)
    {
        if (patience < 1 || !double.IsFinite(delta) || delta <= 0) throw new ArgumentException("Plateau needs positive patience and delta.");
        this.patience = patience; this.delta = delta;
    }
    public bool Add(double score)
    {
        if (!double.IsFinite(score)) throw new ArgumentException("Non-finite score.");
        round++;
        if (score > best) { best = score; BestRound = round; }
        if (score >= highWater + delta) { highWater = score; stale = 0; } else stale++;
        return stale >= patience;
    }
}

/// <summary>Hybrid objective+vision judging (Lane O O2).
/// A vision rubric score alone can be fooled by a pretty render: a model with a hidden
/// non-manifold hole, inside-out winding, or a silhouette that does not match its reference
/// can still photograph well. So the vision score is capped by deterministic geometric gates
/// measured on the mesh itself, and the three hidden-fault gates are hard fails: a model that
/// trips one can never pass, whatever its vision score.
/// Rule, in one line: final = min(vision total, lowest measured gate score); a hard fail
/// also forces Passed = false. A gate that was not measured never caps anything — absence of
/// evidence is reported, not assumed to be a pass.
/// Gate scores: manifold 0 on any non-manifold edge, 5 on any open (boundary) edge, 5 on any
/// degenerate face, else 10; ngons 10 - count, floored at 0; quad% maps linearly to 10 at 85%
/// (the TopologyReport craft target); normals 0 when a closed mesh's signed volume is negative
/// (winding inside-out), 10 otherwise — for an open mesh winding is not judged and the detail
/// says so; silhouette = IoU x 10, hard fail under 0.80 (ReferenceMatch's own "recognisably
/// the same thing" line); symmetry only fails when lopsided (error over 0.05, SymmetryCheck's
/// Lopsided line — an intentionally asymmetric character is not a defect); flex 5 on any new
/// crossing or volume ratio under 0.8; take 5 when the audit is not clear.
/// AiGateInput is deliberately numbers, not meshes: the caller measures with TopologyReport /
/// SymmetryCheck / FlexReport / TakeAudit / ReferenceMatch.Compare and hands the numbers in,
/// so the gate is testable without a render. FromMesh is the one convenience that measures
/// topology + symmetry + winding straight off a PolyMesh.</summary>
public sealed record AiGateInput(
    int NonManifoldEdges = 0,
    int BoundaryEdges = 0,
    int Ngons = 0,
    double QuadPercent = 100,
    int Degenerate = 0,
    bool InvertedWinding = false,
    double SignedVolume = 0,
    double? SilhouetteIou = null,
    double? SymmetryError = null,
    int? FlexNewCrossings = null,
    double? FlexMinVolumeRatio = null,
    bool? TakeClear = null,
    int? TakeWorstCrossings = null)
{
    public static AiGateInput FromMesh(Effigy.PolyMesh mesh)
    {
        if (mesh is null) throw new ArgumentException("A mesh is required.");
        var topo = Effigy.TopologyReport.Measure(mesh, "judge");
        var sym = Effigy.SymmetryCheck.Measure(mesh);
        // Signed volume of the closed surface, fan-triangulated: negative means every face is
        // wound backwards, i.e. the normals point in. Only meaningful on a closed mesh.
        double volume = 0;
        foreach (var face in mesh.Faces)
        {
            var idx = face.Indices;
            if (idx is null || idx.Length < 3) continue;
            var a = mesh.Positions[idx[0]];
            for (int k = 1; k + 1 < idx.Length; k++)
                volume += Effigy.Vec3.Dot(a, Effigy.Vec3.Cross(mesh.Positions[idx[k]] - a, mesh.Positions[idx[k + 1]] - a)) / 6.0;
        }
        bool closed = topo.BoundaryEdges == 0;
        return new(
            NonManifoldEdges: topo.NonManifoldEdges,
            BoundaryEdges: topo.BoundaryEdges,
            Ngons: topo.Ngons,
            QuadPercent: topo.QuadPercent,
            Degenerate: topo.Degenerate,
            InvertedWinding: closed && volume < 0,
            SignedVolume: volume,
            SymmetryError: sym.Error);
    }

    public AiGateInput WithReference(Effigy.Render.ReferenceMatch.Result match) =>
        match is null ? throw new ArgumentException("A reference match is required.")
            : this with { SilhouetteIou = match.Overlap };

    public AiGateInput WithFlex(Effigy.FlexReport.Result flex)
    {
        if (flex is null) throw new ArgumentException("A flex report is required.");
        int crossings = 0;
        double minVolume = double.NaN;
        foreach (var b in flex.Bends)
        {
            crossings += b.NewCrossings;
            if (!double.IsNaN(b.VolumeRatio)) minVolume = double.IsNaN(minVolume) ? b.VolumeRatio : Math.Min(minVolume, b.VolumeRatio);
        }
        return this with { FlexNewCrossings = crossings, FlexMinVolumeRatio = double.IsNaN(minVolume) ? null : minVolume };
    }

    public AiGateInput WithTake(Effigy.TakeAudit.Result take)
    {
        if (take is null) throw new ArgumentException("A take audit is required.");
        int worst = 0;
        foreach (var c in take.Checks) worst = Math.Max(worst, c.WorstCrossings);
        return this with { TakeClear = take.Clear, TakeWorstCrossings = worst };
    }
}

public sealed record AiGate(
    string Name, double Score, bool HardFail, string Detail);

public sealed record AiGateResult(
    IReadOnlyList<AiGate> Gates, double Cap, bool HardFail, IReadOnlyList<string> Failures)
{
    public JsonObject Json()
    {
        var gates = new JsonArray();
        foreach (var g in Gates)
            gates.Add(new JsonObject { ["name"] = g.Name, ["score"] = Math.Round(g.Score, 2), ["hardFail"] = g.HardFail, ["detail"] = g.Detail });
        var failures = new JsonArray();
        foreach (var f in Failures) failures.Add(f);
        return new JsonObject { ["cap"] = Math.Round(Cap, 2), ["hardFail"] = HardFail, ["gates"] = gates, ["failures"] = failures };
    }
}

public static class AiJudge
{
    /// <summary>Run the gates over an input. No vision score is involved here, so this is
    /// unit-testable on its own: feed numbers, read the cap.</summary>
    public static AiGateResult Gate(AiGateInput input)
    {
        var gates = new List<AiGate>();
        var failures = new List<string>();
        double cap = 10;
        bool hard = false;

        void Add(string name, double score, string detail, bool hardFail = false)
        {
            score = Math.Clamp(score, 0, 10);
            gates.Add(new(name, score, hardFail, detail));
            if (score < 10) failures.Add(name + ": " + detail);
            if (hardFail) { hard = true; cap = Math.Min(cap, 8.9); }
            else cap = Math.Min(cap, score);
        }

        // Manifold: the hidden-hole gate. Any non-manifold edge is a hard fail.
        double manifold = 10;
        string manifoldDetail;
        if (input.NonManifoldEdges > 0) { manifold = 0; manifoldDetail = $"{input.NonManifoldEdges} non-manifold edges (3+ faces share an edge)"; }
        else if (input.BoundaryEdges > 0) { manifold = 5; manifoldDetail = $"{input.BoundaryEdges} open boundary edges — the mesh is not closed"; }
        else manifoldDetail = "closed and manifold";
        if (input.Degenerate > 0) { manifold = Math.Min(manifold, 5); manifoldDetail += $"; {input.Degenerate} degenerate faces"; }
        Add("manifold", manifold, manifoldDetail, input.NonManifoldEdges > 0);

        // Normals: inside-out winding, judged only on a closed mesh.
        bool inverted = input.InvertedWinding;
        Add("normals", inverted ? 0 : 10,
            inverted ? $"signed volume {input.SignedVolume:0.##} is negative — faces are wound inside-out"
                : input.BoundaryEdges > 0 ? "open mesh: winding not judged (signed volume is meaningless open)"
                : $"signed volume {input.SignedVolume:0.##} is positive — faces wound outward",
            inverted);

        // Ngons and quad share: craft targets, soft caps, never hard fails on their own.
        Add("ngons", Math.Max(0, 10 - input.Ngons),
            input.Ngons == 0 ? "no n-gons" : $"{input.Ngons} n-gons; target 0");
        Add("quads", input.QuadPercent >= 85 ? 10 : input.QuadPercent / 85.0 * 10,
            $"{input.QuadPercent:0.#}% quads; target at least 85%");

        // Silhouette: the mismatch gate. Under 0.80 IoU the shape is a different shape.
        if (input.SilhouetteIou is double iou)
            Add("silhouette", iou * 10,
                $"silhouette IoU {iou:0.00}; 0.80 is recognisably the same thing",
                iou < 0.80);

        // Symmetry: only lopsided fails. Asymmetry by design scores 10.
        if (input.SymmetryError is double sym)
            Add("symmetry", sym > 0.05 ? 4 : 10,
                sym > 0.05 ? $"mirror error {sym * 100:0.#}% of width — visibly lopsided"
                    : $"mirror error {sym * 100:0.#}% of width");

        // Flex: a joint that collapses or newly crosses itself when bent.
        if (input.FlexNewCrossings is int crossings || input.FlexMinVolumeRatio is double)
        {
            double vol = input.FlexMinVolumeRatio ?? double.NaN;
            bool bad = (input.FlexNewCrossings ?? 0) > 0 || (!double.IsNaN(vol) && vol < 0.8);
            Add("flex", bad ? 5 : 10,
                bad ? $"flex: {input.FlexNewCrossings ?? 0} new crossings, min volume ratio {(double.IsNaN(vol) ? "n/a" : vol.ToString("0.00"))}; targets 0 crossings, ratio at least 0.8"
                    : "flex: no new crossings, volume held");
        }

        // Take: an animated take that passes through itself or a prop.
        if (input.TakeClear is bool clear)
            Add("take", clear ? 10 : 5,
                clear ? "take audit clear"
                    : $"take audit: worst {input.TakeWorstCrossings ?? 0} new crossings in a sample");

        return new(gates, cap, hard, failures);
    }

    /// <summary>Vision score first, gates second. The returned score's total is the vision
    /// total capped by the gates, and Passed is false on any hard fail. Evidence carries the
    /// vision report plus the full gate breakdown, so a capped score always says why.</summary>
    public static AiScore HybridScore(AiScore vision, AiGateInput gates)
    {
        var result = Gate(gates);
        double total = Math.Min(vision.Total, result.Cap);
        bool passed = vision.Passed && !result.HardFail && total >= 0 && vision.Total >= 0;
        // Passed must also respect the vision floor logic already inside AiScore.Passed; a hard
        // fail alone is enough to fail it, and a capped total below the vision total is the
        // visible reason. Keep the vision Passed value unless a hard fail vetoes it.
        passed = vision.Passed && !result.HardFail;
        var evidence = vision.Evidence is JsonObject vo ? (JsonObject)vo.DeepClone()
            : new JsonObject { ["vision"] = vision.Evidence?.DeepClone() };
        evidence["gates"] = result.Json();
        evidence["gateCap"] = Math.Round(result.Cap, 2);
        return new(total, vision.Lowest, passed, evidence);
    }

    public static AiScore HybridScore(AiRubric rubric, JsonNode visionReport, AiGateInput gates) =>
        HybridScore(rubric.Score(visionReport), gates);
}
