#nullable enable
using System;
using System.IO;
using Effigy;

/// <summary>
/// Shared-fixture parity for UVLayoutCheck (sync-uv-layout-check-parity):
/// a layout that passes/fails Mobile's check must pass/fail Desktop's
/// identically, and a layout too degenerate to rasterise must still get
/// the rest of the grade instead of throwing. The pass/fail grade is the
/// behaviour that must match; the only Mobile delta is the soft-fail log
/// route (Mobile: Effigy.KernelLog.Soft; Desktop: same "[kernel soft-fail]"
/// text direct to stderr until the KernelLog ticket lands — see
/// UVLayoutCheck.cs).
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// UVLayoutCheckTests.Register(registry, settings); line, as with
/// KernelParityTests.
/// </summary>
internal static class UVLayoutCheckTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        void Check(string name, Action test) => registry.Add("uv-layout-check", name, test);
        void Require(bool condition, string message = "assertion failed")
        {
            if (!condition) throw new Exception(message);
        }

        // One unit quad in 3D, UVs inset 0.1..0.9 so no wrap/outside rule fires.
        static PolyMesh CleanQuad()
        {
            var m = new PolyMesh();
            m.AddVertex(new Vec3(0f, 0f, 0f));
            m.AddVertex(new Vec3(1f, 0f, 0f));
            m.AddVertex(new Vec3(1f, 1f, 0f));
            m.AddVertex(new Vec3(0f, 1f, 0f));
            m.AddFace(new[] { 0, 1, 2, 3 }, new[]
            {
                new Vec2(0.1f, 0.1f), new Vec2(0.9f, 0.1f),
                new Vec2(0.9f, 0.9f), new Vec2(0.1f, 0.9f),
            });
            return m;
        }

        Check("clean inset quad passes with bake coverage measured", () =>
        {
            var report = UVLayoutCheck.Check(CleanQuad());
            Require(report.Verdict == "PASS", "clean quad must PASS, got " + report.Verdict + ": " + report);
            Require(report.Islands == 1, "expected 1 island, got " + report.Islands);
            Require(report.Faces == 1, "expected 1 face, got " + report.Faces);
            Require(report.FlippedFaces == 0, "clean quad must have no flipped faces");
            Require(report.CoveredFraction > 0f, "bake coverage must be measured, got " + report.CoveredFraction);
            Require(report.OverlappingTexels == 0, "clean quad must not overlap texels");
            Require(report.OutsideSquareFaces == 0, "clean quad must stay inside the square");
            Require(report.Reasons.Count == 0, "clean quad must have no reasons, got: " + string.Join("; ", report.Reasons));
        });

        Check("face outside the square fails", () =>
        {
            var m = CleanQuad();
            m.Faces[0].UVs[3] = new Vec2(0.1f, 1.5f);
            var report = UVLayoutCheck.Check(m);
            Require(report.Verdict == "FAIL", "outside face must FAIL, got " + report.Verdict + ": " + report);
            Require(report.OutsideFaces == 1, "expected 1 outside face, got " + report.OutsideFaces);
        });

        Check("one flipped triangle in a shared island fails with exactly one flipped face", () =>
        {
            // Two triangles sharing the 0-2 diagonal and agreeing on its UVs, so
            // they are one island; the second triangle's UVs wind the other way.
            var m = new PolyMesh();
            m.AddVertex(new Vec3(0f, 0f, 0f));
            m.AddVertex(new Vec3(1f, 0f, 0f));
            m.AddVertex(new Vec3(1f, 1f, 0f));
            m.AddVertex(new Vec3(0f, 1f, 0f));
            m.AddFace(new[] { 0, 1, 2 }, new[]
            {
                new Vec2(0.1f, 0.1f), new Vec2(0.9f, 0.1f), new Vec2(0.9f, 0.9f),
            });
            m.AddFace(new[] { 0, 2, 3 }, new[]
            {
                new Vec2(0.1f, 0.1f), new Vec2(0.9f, 0.9f), new Vec2(0.9f, 0.1f),
            });
            var report = UVLayoutCheck.Check(m);
            Require(report.Islands == 1, "the two triangles must share one island, got " + report.Islands);
            Require(report.Verdict == "FAIL", "flipped triangle must FAIL, got " + report.Verdict + ": " + report);
            Require(report.FlippedFaces == 1, "expected exactly 1 flipped face, got " + report.FlippedFaces);
        });

        Check("bake failure keeps the rest of the grade and is logged, not swallowed", () =>
        {
            // bakeResolution 0 makes NormalBake.Measure throw inside Check's
            // try block: the grade must still come back, and the soft fail
            // must reach stderr instead of vanishing silently.
            var writer = new StringWriter();
            var previous = Console.Error;
            try
            {
                Console.SetError(writer);
                var report = UVLayoutCheck.Check(CleanQuad(), bakeResolution: 0);
                Require(report.Verdict == "PASS", "grade must survive the bake failure, got " + report.Verdict + ": " + report);
                Require(report.Islands == 1 && report.Faces == 1, "islands/faces must still be graded");
                Require(report.CoveredFraction == -1f, "coverage must stay unmeasured when the bake throws, got " + report.CoveredFraction);
                var logged = writer.ToString();
                Require(logged.Contains("[kernel soft-fail]", StringComparison.Ordinal),
                    "soft fail must be logged, stderr was: " + logged);
                Require(logged.Contains("UVLayoutCheck.Grade bake coverage", StringComparison.Ordinal),
                    "log must name the soft-fail site, stderr was: " + logged);
            }
            finally
            {
                Console.SetError(previous);
            }
        });
    }
}
