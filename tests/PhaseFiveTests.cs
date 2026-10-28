using System.Text.Json.Nodes;
using Marionette.Desktop;

internal static class PhaseFiveTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        void Check(string name, Action action) => registry.Add("phase5", name, action);
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        Check("lane 22: get_performance_metrics tracks rebuild timing, cache hits/misses, and memory", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p5-perf-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);

            // Add geometry to trigger rebuild
            tools.ExecuteAsync("add_feature", new JsonObject
            {
                ["type"] = "Primitive",
                ["fields"] = new JsonObject { ["SizeX"] = 8, ["SizeY"] = 8, ["SizeZ"] = 8 }
            }, default).GetAwaiter().GetResult();

            // First calls (misses)
            tools.GenerateTopologyReport();
            tools.MeasureSize();

            // Repeated calls (hits)
            tools.GenerateTopologyReport();
            tools.MeasureSize();

            var res = JsonNode.Parse(tools.ExecuteAsync("get_performance_metrics", new JsonObject(), default).GetAwaiter().GetResult())!;
            Require(res["rebuildCount"]?.GetValue<int>() >= 1, "rebuildCount >= 1");
            Require(res["totalRebuildMs"]?.GetValue<double>() >= 0, "totalRebuildMs recorded");
            Require(res["cacheHits"]?.GetValue<int>() >= 2, "cacheHits >= 2");
            Require(res["memoryAllocatedMb"]?.GetValue<double>() > 0, "memoryAllocatedMb > 0");

            // Reset
            var resReset = JsonNode.Parse(tools.ExecuteAsync("get_performance_metrics", new JsonObject { ["reset"] = true }, default).GetAwaiter().GetResult())!;
            Require(resReset["cacheHits"]?.GetValue<int>() == 0, "cacheHits reset to 0");
        });

        Check("lane 23: full 23-lane master integration test verifies definitions and tools", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p5-all-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);

            // Verify definitions contain all tools required by the 23-lane master guide
            var defs = AiSuiteTools.Definitions;
            string[] requiredTools =
            {
                // Quality (1-5)
                "validate_topology", "heal_mesh", "validate_size", "scale_model", "validate_silhouette",
                // Prevention (6-10)
                "remesh_model", "unwrap_uvs", "validate_uvs", "calculate_normals", "validate_symmetry", "symmetrize_model",
                // Integration (11-16)
                "list_services", "configure_renderer", "set_camera", "validate_import", "validate_export", "export_preset", "project_manage",
                // UI/UX (17-21)
                "timeline_control", "material_preset", "validate_materials", "modelsheet_sync", "layout_preset", "selection_tool",
                // Final (22-23)
                "get_performance_metrics"
            };

            var registeredNames = defs.Select(d => d!["name"]!.GetValue<string>()).ToHashSet();
            foreach (var req in requiredTools)
            {
                Require(registeredNames.Contains(req), $"Tool definition missing: {req}");
            }

            // Create a base shape and exercise multi-lane pipeline
            tools.ExecuteAsync("add_feature", new JsonObject
            {
                ["type"] = "Primitive",
                ["fields"] = new JsonObject { ["SizeX"] = 10.0, ["SizeY"] = 10.0, ["SizeZ"] = 20.0 }
            }, default).GetAwaiter().GetResult();

            // Topology & Size
            var topo = JsonNode.Parse(tools.ExecuteAsync("validate_topology", new JsonObject(), default).GetAwaiter().GetResult())!;
            Require(topo["isValid"]?.GetValue<bool>() == true, "cylinder valid topology");

            var size = JsonNode.Parse(tools.ExecuteAsync("validate_size", new JsonObject { ["expectedHeight"] = 20.0 }, default).GetAwaiter().GetResult())!;
            Require(size["withinTolerance"]?.GetValue<bool>() == true, "cylinder height match");

            // UVs
            var uv = JsonNode.Parse(tools.ExecuteAsync("unwrap_uvs", new JsonObject(), default).GetAwaiter().GetResult())!;
            Require(uv["status"]?.GetValue<string>() == "unwrapped", "cylinder uv unwrap");

            // Symmetry
            var sym = JsonNode.Parse(tools.ExecuteAsync("validate_symmetry", new JsonObject(), default).GetAwaiter().GetResult())!;
            Require(sym["status"]?.GetValue<string>() == "measured", "cylinder symmetry check");

            // Materials & Export
            tools.ExecuteAsync("material_preset", new JsonObject { ["slot"] = "Base", ["preset"] = "Standard" }, default).GetAwaiter().GetResult();
            var exp = JsonNode.Parse(tools.ExecuteAsync("validate_export", new JsonObject { ["format"] = "vmdl" }, default).GetAwaiter().GetResult())!;
            Require(exp["ready"]?.GetValue<bool>() == true, "ready for vmdl export");

            // Layout & Camera
            tools.ExecuteAsync("layout_preset", new JsonObject { ["action"] = "set", ["preset"] = "Sculpting" }, default).GetAwaiter().GetResult();
            tools.ExecuteAsync("set_camera", new JsonObject { ["preset"] = "isometric" }, default).GetAwaiter().GetResult();

            // Metrics
            var metrics = JsonNode.Parse(tools.ExecuteAsync("get_performance_metrics", new JsonObject(), default).GetAwaiter().GetResult())!;
            Require(metrics["rebuildCount"]?.GetValue<int>() >= 1, "pipeline recorded rebuilds");
        });
    }
}
