using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using Effigy;
using Marionette.Desktop;

internal static class SizeTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        void Check(string name, Action test) => registry.Add("size", name, test);
        void Require(bool condition, string message = "assertion failed")
        {
            if (!condition) throw new Exception(message);
        }

        Check("empty studio returns zero size with withinTolerance true", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "size-empty-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var m = tools.MeasureSize();
            Require(m.Width == 0 && m.Depth == 0 && m.Height == 0, "empty bounds");
            Require(m.WithinTolerance, "empty within tolerance");
        });

        Check("primitive box measures exact width depth and height", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "size-box-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var prim = new PrimitiveFeature();
            prim.SizeX.Value = 10f;
            prim.SizeY.Value = 20f;
            prim.SizeZ.Value = 30f;
            tools.Studio.Add(prim);
            tools.Studio.Rebuild();

            var m = tools.MeasureSize(expW: 10, expD: 20, expH: 30);
            Require(Math.Abs(m.Width - 10) < 0.1, $"width {m.Width} approx 10");
            Require(Math.Abs(m.Depth - 20) < 0.1, $"depth {m.Depth} approx 20");
            Require(Math.Abs(m.Height - 30) < 0.1, $"height {m.Height} approx 30");
            Require(m.WithinTolerance, "dimensions within tolerance");
        });

        Check("validate_size tool detects oversized dimensions", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "size-tool-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var prim = new PrimitiveFeature();
            prim.SizeX.Value = 10f;
            prim.SizeY.Value = 10f;
            prim.SizeZ.Value = 50f; // 5x expected
            tools.Studio.Add(prim);
            tools.Studio.Rebuild();

            var res = tools.ExecuteAsync("validate_size", new JsonObject
            {
                ["expectedWidth"] = 10.0,
                ["expectedDepth"] = 10.0,
                ["expectedHeight"] = 10.0,
                ["toleranceRatio"] = 1.6
            }, CancellationToken.None).GetAwaiter().GetResult();

            var json = JsonNode.Parse(res);
            Require(json != null, "json parsed");
            Require(json["withinTolerance"]?.GetValue<bool>() == false, "should be outside tolerance");
            Require(json["maxRatio"]?.GetValue<double>() >= 4.0, "max ratio reflects 50/10");
        });

        Check("scale_model tool scales mesh vertices to target height", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "size-scale-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var prim = new PrimitiveFeature();
            prim.SizeX.Value = 10f;
            prim.SizeY.Value = 10f;
            prim.SizeZ.Value = 20f;
            tools.Studio.Add(prim);
            tools.Studio.Rebuild();

            var res = tools.ExecuteAsync("scale_model", new JsonObject
            {
                ["targetHeight"] = 40.0
            }, CancellationToken.None).GetAwaiter().GetResult();

            var json = JsonNode.Parse(res);
            Require(json != null, "json parsed");
            double h = json["height"]?.GetValue<double>() ?? 0;
            double f = json["factor"]?.GetValue<double>() ?? 0;
            Require(Math.Abs(h - 40.0) < 0.5, $"height scaled to 40, was {h}");
            Require(Math.Abs(f - 2.0) < 0.1, $"factor is 2.0, was {f}");
        });
    }
}
