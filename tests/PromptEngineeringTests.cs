using System;
using System.Collections.Generic;
using Marionette.Desktop;

internal static class PromptEngineeringTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        void Check(string name, Action test) => registry.Add("prompts", name, test);
        void Require(bool condition, string message = "assertion failed")
        {
            if (!condition) throw new Exception(message);
        }

        Check("prompt classification detects character, hardsurface, organic", () =>
        {
            Require(AiPrompts.ClassifyPrompt("build a humanoid character with arms") == "character", "character detection");
            Require(AiPrompts.ClassifyPrompt("model a scifi tank with turret") == "hardsurface", "hardsurface detection");
            Require(AiPrompts.ClassifyPrompt("make an organic pine tree with leaves") == "organic", "organic detection");
            Require(AiPrompts.ClassifyPrompt("make something cool") == "generic", "generic fallback");
        });

        Check("prompt validation flags empty or too short requests", () =>
        {
            Require(!AiPrompts.ValidatePrompt("", out var emptyWarnings), "empty should fail");
            Require(emptyWarnings.Count > 0, "should have empty warnings");

            Require(AiPrompts.ValidatePrompt("build a tall medieval wooden watchtower with four support posts", out var goodWarnings), "detailed prompt valid");
            Require(goodWarnings.Count == 0, "no warnings for good prompt");
        });

        Check("prompt templates exist and enforce topology quality requirements", () =>
        {
            foreach (var kind in new[] { "generic", "character", "hardsurface", "organic" })
            {
                string tmpl = AiPrompts.GetTemplate(kind);
                Require(tmpl.Contains("topology"), $"{kind} template should mention topology");
                Require(tmpl.Contains("manifold"), $"{kind} template should mention manifold");
                Require(tmpl.Contains("validate_topology"), $"{kind} template should mention validate_topology");
            }
        });

        Check("prompt performance metrics record properly", () =>
        {
            int before = AiJudge.Metrics.Count;
            AiJudge.RecordPromptMetric("character", 9.2, true, 0);
            Require(AiJudge.Metrics.Count == before + 1, "metric added");
            var last = AiJudge.Metrics[^1];
            Require(last.PromptKind == "character", "kind preserved");
            Require(last.QualityScore == 9.2, "score preserved");
            Require(last.Passed, "passed preserved");
        });
    }
}
