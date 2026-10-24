using System;
using System.Linq;
using Effigy;
using Marionette.Desktop;

internal static class AiBuilderHealerTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        void Check(string name, Action test) => registry.Add("builder-healer", name, test);
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
            foreach (var f in new[] { new[] { 0, 2, 1 }, new[] { 0, 3, 2 }, new[] { 4, 5, 6 }, new[] { 4, 6, 7 }, new[] { 0, 5, 4 }, new[] { 0, 1, 5 }, new[] { 2, 7, 6 }, new[] { 2, 3, 7 }, new[] { 0, 7, 3 }, new[] { 0, 4, 7 }, new[] { 1, 6, 5 }, new[] { 1, 2, 6 } }) m.AddFace(f);
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

        Check("inverted box flips to outward, veto cleared, face count unchanged", () =>
        {
            var box = ReversedBox();
            var before = AiBuilderHealer.Measure(box);
            Require(before.InvertedWinding, "reversed box should measure inverted");
            Require(before.VetoCount == 1, "expected exactly the inverted veto, got " + before.VetoCount);
            var report = AiBuilderHealer.TryHeal(box);
            Require(report.Healed, "expected healed, got: " + report.HumanReport());
            Require(report.After.InvertedWinding == false, "winding still inverted after heal");
            Require(report.After.Faces == 12, "flip must not add or remove faces, got " + report.After.Faces);
            Require(report.After.SignedVolume > 0, "signed volume still not positive");
            Require(box.Faces.Count == 12 && AiBuilderHealer.Measure(box).InvertedWinding,
                "input mesh must never be mutated by healing");
        });

        Check("degenerate faces removed, counts drop, no new veto", () =>
        {
            var m = Box();
            // Isolated degenerate faces on fresh vertices: one collinear triangle
            // (zero area, valid ring) and one repeated-corner triangle.
            m.AddVertex(new Vec3(5, 5, 5));
            m.AddVertex(new Vec3(6, 5, 5));
            m.AddVertex(new Vec3(7, 5, 5));
            m.AddFace(new[] { 8, 9, 10 });
            m.AddFace(new[] { 8, 8, 9 });
            var before = AiBuilderHealer.Measure(m);
            Require(before.Degenerate == 2, "expected 2 degenerate faces, got " + before.Degenerate);
            var report = AiBuilderHealer.TryHeal(m);
            Require(report.After.Degenerate == 0, "degenerate faces remain: " + report.After.Degenerate);
            Require(report.After.Faces == 12, "expected 12 faces after removal, got " + report.After.Faces);
            Require(report.After.VetoCount == 0, "heal introduced a veto: " + report.HumanReport());
            Require(report.Attempts.Any(a => a.Strategy == "remove-degenerate" && a.Applied),
                "degenerate removal attempt not recorded as applied");
        });

        Check("triplicated face non-manifold healed by duplicate removal", () =>
        {
            var m = Box();
            var face0 = m.Faces[0].Indices.ToArray();
            m.AddFace((int[])face0.Clone());
            m.AddFace((int[])face0.Clone());
            var before = AiBuilderHealer.Measure(m);
            Require(before.NonManifoldEdges == 3, "expected 3 non-manifold edges, got " + before.NonManifoldEdges);
            var report = AiBuilderHealer.TryHeal(m);
            Require(report.Healed, "expected healed, got: " + report.HumanReport());
            Require(report.After.NonManifoldEdges == 0, "non-manifold edges remain");
            Require(report.After.Faces == 12, "expected 12 faces after dedup, got " + report.After.Faces);
        });

        Check("shared-edge tetras heal the veto, boundary tradeoff recorded not hidden", () =>
        {
            var m = new PolyMesh();
            foreach (var p in new[] { (0f, 0f, 0f), (2f, 0f, 0f), (0f, 2f, 0f), (0f, 0f, 2f), (1f, -2f, 0f), (1f, 0f, 2f) })
                m.AddVertex(new Vec3(p.Item1, p.Item2, p.Item3));
            // Tet 1 (outward): edge (0,1) shared by 2 of its faces.
            foreach (var f in new[] { new[] { 0, 2, 1 }, new[] { 0, 1, 3 }, new[] { 1, 2, 3 }, new[] { 2, 0, 3 } }) m.AddFace(f);
            // Tet 2 (outward, reversed pattern): edge (0,1) shared by 2 of its faces.
            foreach (var f in new[] { new[] { 0, 1, 4 }, new[] { 0, 5, 1 }, new[] { 1, 5, 4 }, new[] { 4, 5, 0 } }) m.AddFace(f);
            var before = AiBuilderHealer.Measure(m);
            Require(before.NonManifoldEdges == 1, "expected 1 non-manifold edge, got " + before.NonManifoldEdges);
            var report = AiBuilderHealer.TryHeal(m);
            Require(report.Healed, "expected veto cleared, got: " + report.HumanReport());
            Require(report.After.BoundaryEdges == 4, "expected the 4 boundary edges the removal opens, got " + report.After.BoundaryEdges);
            Require(report.Attempts.Any(a => a.Strategy == "resolve-nonmanifold" && a.Applied && a.Note.Contains("boundary")),
                "tradeoff must be recorded in the attempt note");
        });

        Check("oversized box rescales to within the 1.6x rule", () =>
        {
            var m = Box();
            for (int i = 0; i < m.Positions.Count; i++) m.Positions[i] = m.Positions[i] * 5;
            double[] typical = { 2, 2, 2 };
            var before = AiBuilderHealer.Measure(m, typical);
            Require(before.SizeOff, "expected size veto, ratio " + before.SizeRatio);
            var report = AiBuilderHealer.TryHeal(m, typical);
            Require(report.Healed, "expected healed, got: " + report.HumanReport());
            Require(report.After.SizeRatio is double r && r <= 1.6, "size ratio still over rule: " + report.After.SizeRatio);
            Require(report.After.Faces == 12, "rescale must not change face count");
        });

        Check("stretched proportions refuse rescale, mesh unchanged, handed to user", () =>
        {
            // 10 x 1 x 1 vs typical 5 x 5 x 5: x is too big AND y/z too small, so no
            // uniform scale can satisfy every axis — proportions differ, not just size.
            var m = new PolyMesh();
            double hx = 5, hy = 0.5, hz = 0.5;
            var v = new (double, double, double)[] { (-hx, -hy, -hz), (hx, -hy, -hz), (hx, hy, -hz), (-hx, hy, -hz), (-hx, -hy, hz), (hx, -hy, hz), (hx, hy, hz), (-hx, hy, hz) };
            foreach (var p in v) m.AddVertex(new Vec3((float)p.Item1, (float)p.Item2, (float)p.Item3));
            foreach (var f in new[] { new[] { 0, 2, 1 }, new[] { 0, 3, 2 }, new[] { 4, 5, 6 }, new[] { 4, 6, 7 }, new[] { 0, 5, 4 }, new[] { 0, 1, 5 }, new[] { 2, 7, 6 }, new[] { 2, 3, 7 }, new[] { 0, 7, 3 }, new[] { 0, 4, 7 }, new[] { 1, 6, 5 }, new[] { 1, 2, 6 } }) m.AddFace(f);
            double[] typical = { 5, 5, 5 };
            var before = AiBuilderHealer.Measure(m, typical);
            Require(before.SizeOff, "expected size veto for stretched box");
            var report = AiBuilderHealer.TryHeal(m, typical);
            Require(report.HandToUser, "stretched size must hand to user, got: " + report.HumanReport());
            Require(report.After.Faces == 12 && report.After.SizeOff, "refused heal must leave the mesh as measured");
            Require(report.Attempts.Any(a => a.RefusalReason is not null) || report.Notes.Count > 0,
                "refusal must be recorded somewhere");
        });

        Check("lopsided symmetry is refused, never averaged away", () =>
        {
            // Single box offset in both X and Y: it mirrors across neither X=0 nor Y=0,
            // so every vertex misses its mirror and the error clears the 0.05 line.
            var m = new PolyMesh();
            foreach (var p in new[] { (2f, 2f, -1f), (4f, 2f, -1f), (4f, 4f, -1f), (2f, 4f, -1f), (2f, 2f, 1f), (4f, 2f, 1f), (4f, 4f, 1f), (2f, 4f, 1f) })
                m.AddVertex(new Vec3(p.Item1, p.Item2, p.Item3));
            foreach (var f in new[] { new[] { 0, 2, 1 }, new[] { 0, 3, 2 }, new[] { 4, 5, 6 }, new[] { 4, 6, 7 }, new[] { 0, 5, 4 }, new[] { 0, 1, 5 }, new[] { 2, 7, 6 }, new[] { 2, 3, 7 }, new[] { 0, 7, 3 }, new[] { 0, 4, 7 }, new[] { 1, 6, 5 }, new[] { 1, 2, 6 } }) m.AddFace(f);
            var before = AiBuilderHealer.Measure(m);
            Require(before.SymmetryError is double s && s > 0.05, "expected lopsided symmetry error, got " + before.SymmetryError);
            var report = AiBuilderHealer.TryHeal(m);
            Require(report.After.Faces == report.Before.Faces && report.After.Vertices == report.Before.Vertices,
                "symmetry refusal must leave the mesh untouched");
            Require(report.Notes.Any(n => n.Contains("symmetry")),
                "symmetry refusal must be recorded in notes: " + report.HumanReport());
        });

        Check("silhouette veto is refused, no reshape without a reference", () =>
        {
            var report = AiBuilderHealer.TryHeal(Box(), silhouetteIou: 0.5);
            Require(report.HandToUser, "silhouette veto must hand to user, got: " + report.HumanReport());
            Require(report.Notes.Any(n => n.Contains("silhouette")),
                "silhouette refusal must be recorded");
        });

        Check("loop cap respected: unhealable veto stops at 2 loops", () =>
        {
            var m = Box();
            for (int i = 0; i < m.Positions.Count; i++) m.Positions[i] = m.Positions[i] * 5;
            // No typical dims at all: size cannot even be measured, so force the cap
            // path with a silhouette veto instead — it can never be healed.
            var report = AiBuilderHealer.TryHeal(m, silhouetteIou: 0.5);
            Require(report.LoopsUsed <= 2, "loop cap exceeded: " + report.LoopsUsed);
            Require(report.HandToUser, "unhealable veto must hand to user");
        });

        Check("clean box passes through untouched", () =>
        {
            var report = AiBuilderHealer.TryHeal(Box());
            Require(report.Healed, "clean box should report healed");
            Require(report.After.Faces == 12 && report.After.VetoCount == 0, "clean box changed by healing");
            Require(report.LoopsUsed == 0, "clean box should use zero loops, used " + report.LoopsUsed);
        });
    }
}
