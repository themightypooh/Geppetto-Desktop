using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using Effigy;
using Marionette.Desktop;

internal static class SilhouetteTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        void Check(string name, Action test) => registry.Add("silhouette", name, test);
        void Require(bool condition, string message = "assertion failed")
        {
            if (!condition) throw new Exception(message);
        }

        Check("validate_silhouette returns empty status on model without bodies", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "sil-empty-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var res = tools.CompareSilhouette("side");
            Require(res["status"]?.GetValue<string>() == "empty", "status should be empty");
        });

        Check("validate_silhouette reports no_reference when reference image missing", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "sil-noref-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var prim = new PrimitiveFeature();
            tools.Studio.Add(prim);
            tools.Studio.Rebuild();

            var res = tools.CompareSilhouette("side");
            Require(res["status"]?.GetValue<string>() == "no_reference", "status should be no_reference");
        });

        Check("validate_silhouette tool execution via ExecuteAsync handles empty gracefully", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "sil-tool-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var jsonStr = tools.ExecuteAsync("validate_silhouette", new JsonObject { ["view"] = "side" }, CancellationToken.None).GetAwaiter().GetResult();
            var json = JsonNode.Parse(jsonStr);
            Require(json != null, "parsed json");
            Require(json["status"]?.GetValue<string>() == "empty", "status empty");
        });
    }
}
