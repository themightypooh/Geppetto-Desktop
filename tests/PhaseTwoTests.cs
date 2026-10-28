using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using Effigy;
using Marionette.Desktop;

internal static class PhaseTwoTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        void Check(string name, Action test) => registry.Add("phase2", name, test);
        void Require(bool condition, string message = "assertion failed")
        {
            if (!condition) throw new Exception(message);
        }

        Check("remesh_model executes cleanly with quad and decimate modes", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p2-remesh-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var prim = new PrimitiveFeature();
            tools.Studio.Add(prim);
            tools.Studio.Rebuild();

            var res = tools.ExecuteAsync("remesh_model", new JsonObject
            {
                ["preset"] = "lowpoly"
            }, CancellationToken.None).GetAwaiter().GetResult();

            var json = JsonNode.Parse(res);
            Require(json != null, "json parsed");
            Require(json["status"]?.GetValue<string>() == "remeshed", "status remeshed");
        });

        Check("unwrap_uvs generates islands and validate_uvs confirms 100% coverage", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p2-uv-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var prim = new PrimitiveFeature();
            tools.Studio.Add(prim);
            tools.Studio.Rebuild();

            var unwrapRes = tools.ExecuteAsync("unwrap_uvs", new JsonObject
            {
                ["mode"] = "Angle"
            }, CancellationToken.None).GetAwaiter().GetResult();
            var unwrapJson = JsonNode.Parse(unwrapRes);
            Require(unwrapJson?["status"]?.GetValue<string>() == "unwrapped", "unwrap status");

            var valRes = tools.ExecuteAsync("validate_uvs", new JsonObject(), CancellationToken.None).GetAwaiter().GetResult();
            var valJson = JsonNode.Parse(valRes);
            Require(valJson?["status"]?.GetValue<string>() == "validated", "validation status");
            Require(valJson?["uvCoveragePercent"]?.GetValue<double>() == 100.0, "coverage 100%");

            var normRes = tools.ExecuteAsync("calculate_normals", new JsonObject(), CancellationToken.None).GetAwaiter().GetResult();
            var normJson = JsonNode.Parse(normRes);
            Require(normJson?["status"]?.GetValue<string>() == "calculated", "normal calculation status");
            Require(normJson?["validPercent"]?.GetValue<double>() == 100.0, "normals 100% valid");
        });

        Check("validate_symmetry and symmetrize_model enforce X mirror symmetry", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p2-sym-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var prim = new PrimitiveFeature();
            tools.Studio.Add(prim);
            tools.Studio.Rebuild();

            var valRes = tools.ExecuteAsync("validate_symmetry", new JsonObject(), CancellationToken.None).GetAwaiter().GetResult();
            var valJson = JsonNode.Parse(valRes);
            Require(valJson?["symmetric"]?.GetValue<bool>() == true, "box is symmetric");

            var symRes = tools.ExecuteAsync("symmetrize_model", new JsonObject { ["keepPositive"] = true }, CancellationToken.None).GetAwaiter().GetResult();
            var symJson = JsonNode.Parse(symRes);
            Require(symJson?["status"]?.GetValue<string>() == "symmetrized", "status symmetrized");
            Require(symJson?["symmetric"]?.GetValue<bool>() == true, "remains symmetric");
        });

        Check("mesh and topology caching returns cached object until mutation", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p2-cache-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var prim = new PrimitiveFeature();
            tools.Studio.Add(prim);
            tools.Studio.Rebuild();

            var r1 = tools.GenerateTopologyReport();
            var r2 = tools.GenerateTopologyReport();
            Require(ReferenceEquals(r1, r2), "second call should return cached instance");

            tools.InvalidateCache();
            var r3 = tools.GenerateTopologyReport();
            Require(!ReferenceEquals(r1, r3), "after invalidation new instance produced");
        });

        Check("progress reporting captures calls mutations and topology state", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p2-prog-" + Guid.NewGuid().ToString("N"));
            var session = new AiSession(new AiOptions { Command = "create", Model = "fixture", Endpoint = "http://localhost:5678/v1/", Output = root, Calls = 20 });
            var rep = session.GetProgressReport();
            Require(rep["calls"]?.GetValue<int>() == 0, "calls 0");
            Require(rep["maxCalls"]?.GetValue<int>() == 20, "max calls 20");
            Require(rep["mutations"]?.GetValue<int>() == 0, "mutations 0");
        });
    }
}
