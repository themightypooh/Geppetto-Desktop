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
