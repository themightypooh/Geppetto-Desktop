using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Effigy;
using Marionette.Desktop;

internal static class TopologyTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        void Check(string name, Action test) => registry.Add("topology", name, test);
        void Require(bool condition, string message = "assertion failed")
        {
            if (!condition) throw new Exception(message);
        }

        Check("empty studio produces empty valid topology report", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "topo-empty-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var report = tools.GenerateTopologyReport();
            Require(report.Vertices == 0, "vertices should be 0");
            Require(report.Faces == 0, "faces should be 0");
            Require(report.IsValid, "empty report should be valid");
            Require(!report.HasCriticalIssues, "empty report should have no critical issues");
            Require(report.ProblemScore == 0, "problem score should be 0");
        });

        Check("box mesh topology validation report has 100% quads and zero issues", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "topo-box-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var prim = new PrimitiveFeature();
            tools.Studio.Add(prim);
            var rebuild = tools.Studio.Rebuild();
            Require(!rebuild.HasErrors, "rebuild should succeed");

            var report = tools.GenerateTopologyReport();
            Require(report.Vertices == 8, $"expected 8 vertices, got {report.Vertices}");
            Require(report.Faces == 6, $"expected 6 faces, got {report.Faces}");
            Require(report.NonManifoldEdges == 0, "expected 0 non-manifold edges");
            Require(report.BoundaryEdges == 0, "expected 0 boundary edges on closed box");
            Require(!report.InvertedWinding, "box should have outward winding");
            Require(report.DegenerateFaces == 0, "expected 0 degenerate faces");
            Require(report.Ngons == 0, "expected 0 ngons");
            Require(report.QuadPercent == 100.0, $"expected 100% quads, got {report.QuadPercent}");
            Require(report.IsValid, "box should be valid topology");
            Require(!report.HasCriticalIssues, "box should have no critical issues");
        });

        Check("validate_topology tool execution returns structured json", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "topo-tool-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var prim = new PrimitiveFeature();
            tools.Studio.Add(prim);
            tools.Studio.Rebuild();

            var jsonStr = tools.ExecuteAsync("validate_topology", new JsonObject(), CancellationToken.None).GetAwaiter().GetResult();
            var json = JsonNode.Parse(jsonStr);
            Require(json != null, "should parse json");
            Require(json["vertices"]?.GetValue<int>() == 8, "json vertices should be 8");
            Require(json["faces"]?.GetValue<int>() == 6, "json faces should be 6");
            Require(json["isValid"]?.GetValue<bool>() == true, "json isValid should be true");
            Require(json["hasCriticalIssues"]?.GetValue<bool>() == false, "json hasCriticalIssues should be false");
            Require(json["summary"]?.GetValue<string>()?.Contains("V:8") == true, "summary should mention V:8");
        });

        Check("TopologyReport problem scoring and critical issues flags behave accurately", () =>
        {
            var clean = new Marionette.Desktop.TopologyReport(8, 6, 0, false, 0, 0, 0, 100.0, 0.0);
            Require(clean.IsValid, "clean report must be valid");
            Require(!clean.HasCriticalIssues, "clean report has no critical issues");
            Require(clean.ProblemScore == 0, "clean report has problem score 0");

            var badNonManifold = new Marionette.Desktop.TopologyReport(10, 8, 2, false, 0, 0, 0, 100.0, 0.0);
            Require(!badNonManifold.IsValid, "non-manifold must not be valid");
            Require(badNonManifold.HasCriticalIssues, "non-manifold edges are critical");
            Require(badNonManifold.ProblemScore >= 2000, "non-manifold edge score >= 2000");

            var badWinding = new Marionette.Desktop.TopologyReport(8, 6, 0, true, 0, 0, 0, 100.0, 0.0);
            Require(!badWinding.IsValid, "inverted winding must not be valid");
            Require(badWinding.HasCriticalIssues, "inverted winding is critical");
            Require(badWinding.ProblemScore >= 1000, "inverted winding score >= 1000");

            var openMesh = new Marionette.Desktop.TopologyReport(10, 6, 0, false, 12, 0, 0, 100.0, 0.0);
            Require(!openMesh.IsValid, "open mesh has boundary edges, not fully valid");
            Require(openMesh.HasCriticalIssues, "open mesh with > 10 boundary edges has critical issues flag");
        });
    }
}
