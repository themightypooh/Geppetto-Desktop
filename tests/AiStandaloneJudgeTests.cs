using System;
using System.Linq;
using System.Text.Json.Nodes;
using Effigy;
using Marionette.Desktop;

internal static class AiStandaloneJudgeTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        void Check(string name, Action test) => registry.Add("standalone-judge", name, test);
        void Require(bool condition, string message = "assertion failed")
        {
            if (!condition) throw new Exception(message);
        }

        static PolyMesh Box()
        {
            var m = new PolyMesh();
            double h = 1;
            var v = new (double, double, double)[] { (-h, -h, -h), (h, -h, -h), (h, h, -h), (-h, h, -h), (-h, -h, h), (h, -h, h), (h, h, h), (-h, h, h) };
            foreach (var p in v) m.AddVertex(new Vec3((float)p.Item1, (float)p.Item2, (float)p.Item3));
            // Proper outward quad order (each face's normal points out). NOTE: the
            // backstop suite's Box() orders its first face (0,2,3,1), which is a bowtie
            // (diagonal vertex order) whose fan-triangulated signed volume is exactly 0
            // — it passes gates only because 0 is not negative. This box has volume +8,
            // so the reversed copy below genuinely reads as inside-out (-8).
            foreach (var f in new[] { new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 }, new[] { 2, 3, 7, 6 }, new[] { 0, 4, 7, 3 }, new[] { 1, 2, 6, 5 } }) m.AddFace(f);
            return m;
        }

        static PolyMesh ReversedBox()
        {
            var m = Box();
            var faces = m.Faces.Select(f => f.Indices.Reverse().ToArray()).ToArray();
            var r = new PolyMesh();
            foreach (var p in m.Positions) r.AddVertex(p);
            foreach (var f in faces) r.AddFace(f);
            return r;
        }

        Check("closed box passes with no hard fails", () =>
        {
            var report = AiStandaloneJudge.JudgeBodies("test.effigy", new[] { ("Box", Box()) });
            Require(report.Verdict == "pass", "expected pass, got " + report.Verdict);
            Require(report.ExitCode == 0, "expected exit 0");
            Require(report.HardFails.Count == 0, "no hard fails expected");
            Require(report.Bodies[0].Gates.NonManifoldEdges == 0, "box must be manifold");
            Require(report.Bodies[0].Gates.BoundaryEdges == 0, "box must be closed");
            Require(report.Bodies[0].LooseVertices == 0, "box must have no loose vertices");
            Require(report.Bodies[0].Gates.SignedVolume > 0, "proper box must have positive signed volume, got " + report.Bodies[0].Gates.SignedVolume);
            Require(Math.Abs(report.Bodies[0].Gates.SignedVolume - 8) < 1e-6, "side-2 box volume must be 8, got " + report.Bodies[0].Gates.SignedVolume);
            Require(report.Bodies[0].Gates.Degenerate == 0, "proper box must have no degenerate faces");
        });

        Check("non-manifold third face on one edge vetoes", () =>
        {
            var m = Box();
            m.AddFace(new[] { 0, 1, 2 });
            var report = AiStandaloneJudge.JudgeBodies("test.effigy", new[] { ("Bad", m) });
            Require(report.Verdict == "veto", "expected veto, got " + report.Verdict);
            Require(report.ExitCode == 2, "expected exit 2");
            Require(report.HardFails.Count > 0, "hard fail must be listed");
            Require(report.Bodies[0].Gates.NonManifoldEdges > 0, "shared edge must count as non-manifold");
        });

        Check("inside-out winding vetoes even with perfect topology", () =>
        {
            var report = AiStandaloneJudge.JudgeBodies("test.effigy", new[] { ("InsideOut", ReversedBox()) });
            Require(report.Verdict == "veto", "expected veto, got " + report.Verdict + "; inverted=" + report.Bodies[0].Gates.InvertedWinding + "; vol=" + report.Bodies[0].Gates.SignedVolume + "; boundary=" + report.Bodies[0].Gates.BoundaryEdges + "; nonmanifold=" + report.Bodies[0].Gates.NonManifoldEdges);
            Require(report.Bodies[0].Gates.InvertedWinding, "reversed box must read as inverted winding");
            Require(report.Bodies[0].Gates.SignedVolume < 0, "reversed box must have negative signed volume");
        });

        Check("open mesh is noted, not vetoed (boundary edges are soft)", () =>
        {
            var m = new PolyMesh();
            double h = 1;
            var v = new (double, double, double)[] { (-h, -h, -h), (h, -h, -h), (h, h, -h), (-h, h, -h), (-h, -h, h), (h, -h, h), (h, h, h), (-h, h, h) };
            foreach (var p in v) m.AddVertex(new Vec3((float)p.Item1, (float)p.Item2, (float)p.Item3));
            foreach (var f in new[] { new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 } }) m.AddFace(f);
            var report = AiStandaloneJudge.JudgeBodies("test.effigy", new[] { ("Open", m) });
            Require(report.Bodies[0].Gates.BoundaryEdges > 0, "missing face must leave boundary edges");
            Require(report.HardFails.Count == 0, "boundary edges alone must not veto per backstop semantics");
            Require(report.Verdict == "pass", "open-but-otherwise-clean mesh passes with a note, got " + report.Verdict);
        });

        Check("report never claims vision or inter-body coverage", () =>
        {
            var report = AiStandaloneJudge.JudgeBodies("test.effigy", new[] { ("Box", Box()) });
            var json = report.Json();
            Require(json["vision"]?["scored"]?.GetValue<bool>() == false, "vision.scored must be false");
            Require(json["notMeasured"]?["interBodyClipping"]?.GetValue<string>()?.Contains("NOT CHECKED") == true, "inter-body gap must be explicit in JSON");
            Require(report.HumanReport().Contains("NOT SCORED"), "human report must say vision NOT SCORED");
            Require(report.HumanReport().Contains("NOT CHECKED"), "human report must say inter-body NOT CHECKED");
        });

        Check("judging nothing is an error, not a pass", () =>
        {
            var report = AiStandaloneJudge.JudgeBodies("test.effigy", Array.Empty<(string, PolyMesh)>());
            Require(report.Verdict == "error", "empty body list must not pass, got " + report.Verdict);
            Require(report.ExitCode == 3, "expected exit 3");
        });
    }
}
