using System;
using System.Text.Json.Nodes;
using Effigy;
using Marionette.Desktop;

internal static class AiJudgeBackstopTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        void Check(string name, Action test) => registry.Add("judge-backstop", name, test);
        void Require(bool condition, string message = "assertion failed")
        {
            if (!condition) throw new Exception(message);
        }

        static AiScore Vision(double total, double lowest) =>
            new(total, lowest, total >= 9 && lowest >= 7, new JsonObject { ["categories"] = new JsonArray() });

        static PolyMesh Box(double size = 2)
        {
            var m = new PolyMesh();
            double h = size / 2;
            var v = new (double, double, double)[] { (-h, -h, -h), (h, -h, -h), (h, h, -h), (-h, h, -h), (-h, -h, h), (h, -h, h), (h, h, h), (-h, h, h) };
            foreach (var p in v) m.AddVertex(new Vec3((float)p.Item1, (float)p.Item2, (float)p.Item3));
            foreach (var f in new[] { new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 } }) m.AddFace(f);
            return m;
        }

        Check("known-good model passes and is not tanked by one harsh vision category", () =>
        {
            // Vision total 9 meets target 9, but one category scored 5 (below floor 7).
            // Old HybridScore kept Lowest and the floor could sink it; the backstop
            // passes it — floor miss is advisory, written into Notes, not a veto.
            var gates = AiGateInput.FromMesh(Box());
            var verdict = AiJudgeBackstop.Apply(Vision(9, 5), new AiMeasuredFacts(gates), target: 9, floor: 7);
            Require(verdict.Passed, "good model should pass despite low vision category; notes: " + verdict.Notes);
            Require(verdict.HardFails.Count == 0, "no hard fails expected");
            Require(verdict.Notes.Contains("below floor"), "floor miss must still be reported, advisory");
            Require(verdict.Soft.Style == 9, "style soft score is the vision total");
        });

        Check("known-bad model: vision 8.57 cannot overrule measured hard fail", () =>
        {
            // The qwen3-vl:4b failure shape: a pretty-looking model scored 0.857
            // (8.57/10) while actually broken. Non-manifold edges veto it here.
            var gates = new AiGateInput(NonManifoldEdges: 3);
            var verdict = AiJudgeBackstop.Apply(Vision(8.57, 8.57), new AiMeasuredFacts(gates), target: 9, floor: 7);
            Require(!verdict.Passed, "non-manifold model must not pass on vision alone");
            Require(verdict.HardFails.Count > 0, "hard fail must be listed");
            Require(verdict.VisionTotal == 8.57, "vision total is preserved, not hidden");
        });

        Check("inside-out winding and size over 1.6x are hard fails", () =>
        {
            var inverted = AiJudgeBackstop.Apply(Vision(10, 10), new AiMeasuredFacts(new AiGateInput(InvertedWinding: true)), target: 9);
            Require(!inverted.Passed, "inside-out must veto a perfect vision score");
            var oversize = AiJudgeBackstop.Apply(Vision(10, 10), new AiMeasuredFacts(new AiGateInput(), SizeRatio: 2.0, PropName: "barrel"), target: 9);
            Require(!oversize.Passed, "2.0x size must veto");
            Require(oversize.Soft.Size < 10, "size soft score must drop");
            var inTolerance = AiJudgeBackstop.Apply(Vision(10, 10), new AiMeasuredFacts(new AiGateInput(), SizeRatio: 1.5, PropName: "barrel"), target: 9);
            Require(inTolerance.Passed, "1.5x is inside the 1.6x rule and must not veto");
            Require(AiMeasuredFacts.IsSizeOff(40, 24), "40 vs typical 24 is over 1.6x");
            Require(!AiMeasuredFacts.IsSizeOff(34, 24), "34 vs typical 24 is inside 1.6x");
        });

        Check("soft scores stay separate, never minned into a ruling number", () =>
        {
            // Triangle mesh: quads 0% drags the geometry soft score down, but with
            // no hard fail and vision total at target, the verdict still passes —
            // the low soft score is reported for fixing, not used as a veto.
            var gates = new AiGateInput(QuadPercent: 0);
            var verdict = AiJudgeBackstop.Apply(Vision(9, 9), new AiMeasuredFacts(gates), target: 9);
            Require(verdict.Passed, "0% quads is soft, not a veto");
            Require(verdict.Soft.Geometry < 10, "geometry soft score must reflect the low quad share");
        });

        Check("loop caps: 1 pass + 2 fix loops, then user decides", () =>
        {
            var loop = new AiBackstopLoop();
            var bad = new AiMeasuredFacts(new AiGateInput(NonManifoldEdges: 1));
            var v1 = loop.Judge(Vision(9, 9), bad, target: 9);
            Require(!v1.Passed, "first verdict must fail on the hard fail");
            loop.NoteFix();
            var v2 = loop.Judge(Vision(9, 9), bad, target: 9);
            Require(!v2.Passed, "second verdict still fails");
            loop.NoteFix();
            var v3 = loop.Judge(Vision(9, 9), bad, target: 9);
            Require(loop.Exhausted, "3 verdicts exhausts the loop");
            var final = loop.FinalizeForUser(v3);
            Require(final.UserDecides, "exhausted loop must hand the model to the user");
            bool threw = false;
            try { loop.Judge(Vision(10, 10), new AiMeasuredFacts(new AiGateInput()), target: 9); } catch (InvalidOperationException) { threw = true; }
            Require(threw, "a 4th verdict must be refused");
            bool fixThrew = false;
            try { loop.NoteFix(); } catch (InvalidOperationException) { fixThrew = true; }
            Require(fixThrew, "a 3rd fix loop must be refused");
        });

        Check("unmeasured gates never veto", () =>
        {
            // AiGateInput default: no silhouette, no symmetry, no flex, no take.
            // Absence of evidence is reported, not assumed — and must not fail a
            // model the vision score passes.
            var verdict = AiJudgeBackstop.Apply(Vision(9, 9), new AiMeasuredFacts(new AiGateInput()), target: 9);
            Require(verdict.Passed, "unmeasured dimensions must not veto");
        });
    }
}
