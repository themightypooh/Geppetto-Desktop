using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Effigy;

/// <summary>
/// Targeted tests for the professional stroke engine and brush library
/// (godlike-stroke-engine-sculpt-layers). The engine is pure functions plus an
/// explicit seed, so replay is asserted exactly, not approximately.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// StrokeEngineTests.Register(registry, settings); line, as was done for
/// KernelParityTests.
/// </summary>
internal static class StrokeEngineTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        registry.Add("stroke-engine", "dots stamps once per input point", () =>
        {
            var points = new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(3, 0, 0) };
            var samples = StrokeEngineApi.Expand(StrokeEngineMode.Dots, points, 0.5f);
            if (samples.Count != 3) throw new Exception("dots resampled: " + samples.Count);
            for (var i = 0; i < 3; i++)
                if (samples[i].Point.x != points[i].x) throw new Exception("dots moved point " + i);
        });

        registry.Add("stroke-engine", "space spacing is scene-based", () =>
        {
            var points = new[] { new Vec3(0, 0, 0), new Vec3(10, 0, 0) };
            var samples = StrokeEngineApi.Expand(StrokeEngineMode.Space, points, 2f);
            if (samples.Count != 6) throw new Exception("expected 6 samples, got " + samples.Count);
            if (samples[0].Point.x != 0f || samples[5].Point.x != 10f) throw new Exception("endpoints wrong");
            if (samples[3].Point.x != 6f) throw new Exception("spacing wrong: " + samples[3].Point.x);
        });

        registry.Add("stroke-engine", "screen spacing converts with pixels per unit", () =>
        {
            if (StrokeEngineApi.ToSceneSpacing(20f, StrokeSpacingKind.Screen, 10f) != 2f) throw new Exception("screen conversion wrong");
            if (StrokeEngineApi.ToSceneSpacing(20f, StrokeSpacingKind.Scene, 10f) != 20f) throw new Exception("scene passthrough wrong");
        });

        registry.Add("stroke-engine", "dash ratio gates dabs", () =>
        {
            var points = new[] { new Vec3(0, 0, 0), new Vec3(10, 0, 0) };
            var samples = StrokeEngineApi.Expand(StrokeEngineMode.Space, points, 1f);
            var kept = StrokeEngineApi.ApplyDash(samples, new DashPattern { DashRatio = 0.5f, DashLength = 4f });
            var xs = new List<float>();
            foreach (var s in kept) xs.Add(s.Point.x);
            var expected = new[] { 0f, 1f, 4f, 5f, 8f, 9f };
            if (xs.Count != expected.Length) throw new Exception("dash kept " + xs.Count + ", expected 6");
            for (var i = 0; i < expected.Length; i++)
                if (xs[i] != expected[i]) throw new Exception("dash kept x=" + xs[i] + ", expected " + expected[i]);
        });

        registry.Add("stroke-engine", "lazy smooths a jittered path", () =>
        {
            var points = new[] { new Vec3(0, 0, 0), new Vec3(1, 0.5f, 0), new Vec3(2, -0.5f, 0), new Vec3(3, 0.5f, 0), new Vec3(4, -0.5f, 0), new Vec3(5, 0, 0) };
            var samples = StrokeEngineApi.Expand(StrokeEngineMode.Lazy, points, 1f, 2f);
            var maxY = 0f;
            foreach (var s in samples) maxY = Math.Max(maxY, Math.Abs(s.Point.y));
            if (maxY >= 0.3f) throw new Exception("lazy did not smooth: max |y| = " + maxY);
        });

        registry.Add("stroke-engine", "pressure curve maps as configured", () =>
        {
            var curve = new StrokePressureCurve { Strength = new PiecewiseCurve((0f, 0f), (0.5f, 0.25f), (1f, 1f)) };
            var (radius, strength) = curve.Apply(0.5f, 2f, 4f);
            if (radius != 2f) throw new Exception("radius should be unaffected by default: " + radius);
            if (strength != 1f) throw new Exception("strength at 0.5 should be 4*0.25=1: " + strength);
            var (_, zero) = curve.Apply(0f, 2f, 4f);
            if (zero != 0f) throw new Exception("strength at 0 should be 0: " + zero);
        });

        registry.Add("stroke-engine", "replay is bit-identical under a fixed seed", () =>
        {
            var record = new StrokeRecord
            {
                Mode = StrokeEngineMode.Space,
                Spacing = 1f,
                BaseRadius = 0.5f,
                BaseStrength = 0.8f,
                Seed = 42,
                Dash = new DashPattern { DashRatio = 0.75f, DashLength = 3f },
                Jitter = new StrokeJitter { PositionAmount = 0.3f, AngleDegrees = 20f },
            };
            record.Points.AddRange(new[] { new Vec3(0, 0, 0), new Vec3(2, 0.5f, 0), new Vec3(4, -0.5f, 0), new Vec3(6, 0, 0) });
            record.Pressures.AddRange(new[] { 0.2f, 0.6f, 1f, 0.4f });
            var first = StrokeReplay.Replay(record);
            var second = StrokeReplay.Replay(record.Clone());
            if (first.Count == 0) throw new Exception("replay produced nothing");
            if (first.Count != second.Count) throw new Exception("replay counts differ");
            for (var i = 0; i < first.Count; i++)
            {
                var a = first[i];
                var b = second[i];
                if (a.Position.x != b.Position.x || a.Position.y != b.Position.y || a.Position.z != b.Position.z
                    || a.Direction.x != b.Direction.x || a.Direction.y != b.Direction.y || a.Direction.z != b.Direction.z
                    || a.Radius != b.Radius || a.Strength != b.Strength || a.AlphaRotation != b.AlphaRotation)
                    throw new Exception("replay differs at dab " + i);
            }
        });

        registry.Add("stroke-engine", "same seed jitters identically, different seed differs", () =>
        {
            var points = new[] { new Vec3(0, 0, 0), new Vec3(10, 0, 0) };
            var samples = StrokeEngineApi.Expand(StrokeEngineMode.Space, points, 1f);
            var jitter = new StrokeJitter { PositionAmount = 0.5f, AngleDegrees = 30f };
            var a = StrokeEngineApi.ApplyJitter(samples, jitter, 7);
            var b = StrokeEngineApi.ApplyJitter(samples, jitter, 7);
            var c = StrokeEngineApi.ApplyJitter(samples, jitter, 8);
            for (var i = 0; i < a.Count; i++)
                if (a[i].Point.x != b[i].Point.x || a[i].Point.y != b[i].Point.y) throw new Exception("same seed differed at " + i);
            var differs = false;
            for (var i = 0; i < a.Count; i++)
                if (a[i].Point.x != c[i].Point.x || a[i].Point.y != c[i].Point.y || a[i].Direction.x != c[i].Direction.x) differs = true;
            if (!differs) throw new Exception("different seed jitters identically");
        });

        registry.Add("stroke-engine", "airbrush lays rate dabs while held", () =>
        {
            var dabs = StrokeEngineApi.AirbrushDabs(new Vec3(1, 2, 3), new Vec3(0, 0, 1), 1f, 10f);
            if (dabs.Count != 10) throw new Exception("expected 10 dabs, got " + dabs.Count);
            var none = StrokeEngineApi.AirbrushDabs(new Vec3(1, 2, 3), new Vec3(0, 0, 1), 0.05f, 10f);
            if (none.Count != 0) throw new Exception("short hold should lay nothing");
        });

        registry.Add("stroke-engine", "curve follows the spline", () =>
        {
            var points = new[] { new Vec3(0, 0, 0), new Vec3(1, 2, 0), new Vec3(2, 0, 0), new Vec3(3, 2, 0) };
            var samples = StrokeEngineApi.Expand(StrokeEngineMode.Curve, points, 0.5f);
            if (samples.Count < 8) throw new Exception("curve too sparse: " + samples.Count);
            if (samples[0].Point.x != 0f || samples[^1].Point.x != 3f) throw new Exception("curve endpoints wrong");
            var maxY = 0f;
            foreach (var s in samples) maxY = Math.Max(maxY, s.Point.y);
            if (maxY <= 1.5f) throw new Exception("curve never bulges: max y = " + maxY);
        });

        registry.Add("stroke-engine", "randomise picks valid stamps within range", () =>
        {
            var samples = StrokeEngineApi.Expand(StrokeEngineMode.Space, new[] { new Vec3(0, 0, 0), new Vec3(5, 0, 0) }, 1f);
            var brush = new List<BrushSample>();
            foreach (var s in samples) brush.Add(new BrushSample(s.Point, Vec3.Zero, 1f, 1f, s.Direction));
            var alphas = new[] { "proc:pores", "proc:scales" };
            var vdms = new[] { "vdm:ear", "vdm:nose" };
            var dabs = StrokeEngineApi.Randomise(brush, 3, alphas, vdms, 30f);
            foreach (var d in dabs)
            {
                if (Array.IndexOf(alphas, d.AlphaName) < 0) throw new Exception("bad alpha pick: " + d.AlphaName);
                if (Array.IndexOf(vdms, d.VdmName) < 0) throw new Exception("bad vdm pick: " + d.VdmName);
                if (Math.Abs(d.Sample.AlphaRotation) > 30f) throw new Exception("rotation out of range: " + d.Sample.AlphaRotation);
            }
        });

        registry.Add("stroke-engine", "piecewise curve editor interpolates and edits", () =>
        {
            var curve = new PiecewiseCurve((0f, 0f), (1f, 1f));
            if (curve.Evaluate(0.5f) != 0.5f) throw new Exception("identity interpolate wrong");
            if (curve.Evaluate(-1f) != 0f || curve.Evaluate(2f) != 1f) throw new Exception("clamp wrong");
            curve.AddPoint(0.5f, 0.75f);
            if (curve.Evaluate(0.5f) != 0.75f) throw new Exception("add point wrong");
            if (!curve.MovePoint(1, 0.5f, 0.25f)) throw new Exception("move failed");
            if (curve.Evaluate(0.5f) != 0.25f) throw new Exception("move wrong");
            if (!curve.RemovePoint(1)) throw new Exception("remove failed");
            if (curve.Evaluate(0.5f) != 0.5f) throw new Exception("remove did not restore");
            if (curve.MovePoint(9, 0f, 0f) || curve.RemovePoint(9)) throw new Exception("out-of-range edit should fail");
        });

        registry.Add("brush-library", "catalog holds 200+ presets", () =>
        {
            var library = new BrushLibrary();
            if (library.Count < 200) throw new Exception("catalog only has " + library.Count + " presets");
        });

        registry.Add("brush-library", "search finds a named preset fast", () =>
        {
            var library = new BrushLibrary();
            var clock = Stopwatch.StartNew();
            var named = library.Search("Crease Sharp");
            clock.Stop();
            if (clock.Elapsed.TotalSeconds >= 1.0) throw new Exception("search took " + clock.Elapsed);
            if (named.Count == 0 || named[0].Name != "Crease Sharp") throw new Exception("named search missed");
            if (library.Search("crease").Count == 0) throw new Exception("tag/name search missed crease");
            if (library.Search("pores").Count == 0) throw new Exception("alpha search missed pores");
        });

        registry.Add("brush-library", "falloff weight uses custom curve when set", () =>
        {
            var plain = new BrushPreset { Falloff = BrushFalloff.Smooth };
            if (plain.FalloffWeight(0f) != 1f || plain.FalloffWeight(1f) != 0f) throw new Exception("named smooth falloff wrong");
            var custom = new BrushPreset { FalloffCurve = new PiecewiseCurve((0f, 0f), (1f, 1f)) };
            if (custom.FalloffWeight(0.5f) != 0.5f) throw new Exception("custom curve not used");
        });

        registry.Add("brush-library", "by-name and by-tag lookups", () =>
        {
            var library = new BrushLibrary();
            var clay = library.ByName("Clay Build");
            if (clay is null || clay.Kind != BrushKind.Clay) throw new Exception("ByName missed Clay Build");
            var hard = library.ByTag("hard-surface");
            if (hard.Count == 0) throw new Exception("ByTag missed hard-surface");
            foreach (var p in hard)
            {
                var has = false;
                foreach (var t in p.Tags) if (t == "hard-surface") has = true;
                if (!has) throw new Exception("ByTag returned preset without the tag: " + p.Name);
            }
        });

        registry.Add("brush-library", "shared folder loads presets and skips bad files", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "brushlib-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "clay.brush.json"), "{\"name\": \"Test Clay\", \"kind\": \"Clay\", \"falloff\": \"Smooth\", \"tags\": [\"test\"]}");
                File.WriteAllText(Path.Combine(dir, "pores.brush.json"), "{\"name\": \"Test Pores\", \"kind\": \"Draw\", \"alpha\": \"proc:pores\", \"tags\": [\"test\"]}");
                File.WriteAllText(Path.Combine(dir, "broken.brush.json"), "{not json");
                File.WriteAllText(Path.Combine(dir, "unknown.brush.json"), "{\"name\": \"Bad\", \"kind\": \"NoSuchKind\"}");
                var library = new BrushLibrary();
                var added = library.ScanFolder(dir, out var skipped);
                if (added != 2) throw new Exception("expected 2 added, got " + added);
                if (skipped != 2) throw new Exception("expected 2 skipped, got " + skipped);
                if (library.ByName("Test Clay") is null) throw new Exception("scanned preset not found");
                if (library.Search("Test Pores").Count != 1) throw new Exception("scanned search missed");
                if (library.ScanFolder(Path.Combine(dir, "no-such-dir")) != 0) throw new Exception("missing folder should add 0");
            }
            finally { Directory.Delete(dir, true); }
        });
    }
}
