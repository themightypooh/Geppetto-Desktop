#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Marionette.Desktop;

/// <summary>Measured problem vector for one mesh, as the judge measures it.
/// This is the healer's only source of truth: a fix counts only when a re-measure
/// through AiJudge.Gate's inputs shows the problems actually dropped. The healer
/// never trusts its own edit — it trusts the re-measure.</summary>
public sealed record HealProblemCounts(
    int Vertices,
    int Faces,
    int NonManifoldEdges,
    int ExcessIncidence,
    int BoundaryEdges,
    int Degenerate,
    int Ngons,
    double QuadPercent,
    double SignedVolume,
    bool InvertedWinding,
    double? SizeRatio,
    double? SymmetryError,
    double? SilhouetteIou)
{
    public bool SizeOff => SizeRatio is double r && r > 1.6;

    /// <summary>Veto kinds, mirroring the backstop's hard fails: a non-manifold edge,
    /// inside-out winding, size off by more than 1.6x, silhouette IoU under 0.80.
    /// Symmetry is deliberately NOT a veto (gate score 4, never a hard fail).</summary>
    public IReadOnlyList<string> VetoKinds
    {
        get
        {
            var kinds = new List<string>();
            if (NonManifoldEdges > 0) kinds.Add("nonmanifold");
            if (InvertedWinding) kinds.Add("inverted");
            if (SizeOff) kinds.Add("size");
            if (SilhouetteIou is double iou && iou < 0.80) kinds.Add("silhouette");
            return kinds;
        }
    }

    public int VetoCount => VetoKinds.Count;

    /// <summary>Single ordering for acceptance. Vetoes dominate (1000 each) so clearing
    /// a veto always beats any soft tradeoff; inside one veto level, excess incidence
    /// gives stepwise gradient (a 4-shared edge dropping to 3-shared is progress even
    /// though the edge is still non-manifold, and excess weighs 10 so that progress
    /// is not masked by the boundary edges the removal opens) and then boundary/degenerate counts.</summary>
    public int ProblemScore => VetoCount * 1000 + ExcessIncidence * 10 + NonManifoldEdges + BoundaryEdges + Degenerate;

    public bool HasNewVetoKindVs(HealProblemCounts before) =>
        VetoKinds.Any(k => !before.VetoKinds.Contains(k));

    public bool IsBetterThan(HealProblemCounts before) =>
        !HasNewVetoKindVs(before) && ProblemScore < before.ProblemScore;
}

/// <summary>One attempted fix. Applied=false with a RefusalReason means the healer
/// deliberately declined (design intent, no target, infeasible) — that is a result,
/// not a missing feature. Applied=false with a null refusal means the strategy
/// simply had nothing to do or no improving edit.</summary>
public sealed record HealAttempt(
    string Strategy,
    bool Applied,
    HealProblemCounts Before,
    HealProblemCounts After,
    string Note,
    string? RefusalReason = null);

/// <summary>Result of a heal run. Mesh is a CLONE — the input mesh is never mutated.
/// Healed means no vetoes remain on re-measure (soft defects may remain and are
/// reported in Notes). HandToUser means vetoes remain after the loop cap: the model
/// goes back to the user, never silently passed.</summary>
public sealed record HealReport(
    PolyMesh Mesh,
    HealProblemCounts Before,
    HealProblemCounts After,
    IReadOnlyList<HealAttempt> Attempts,
    IReadOnlyList<string> Notes,
    int LoopsUsed,
    bool Healed,
    bool HandToUser)
{
    public string HumanReport()
    {
        var lines = new List<string>
        {
            $"Builder healer: {Before.Faces} faces -> {After.Faces} faces, " +
            $"vetoes {Before.VetoCount} -> {After.VetoCount}, " +
            $"problem score {Before.ProblemScore} -> {After.ProblemScore}, loops {LoopsUsed}",
            $"  before: {Before.NonManifoldEdges} non-manifold edges, {Before.BoundaryEdges} boundary, " +
            $"{Before.Degenerate} degenerate, inverted={Before.InvertedWinding}, " +
            $"sizeRatio={(Before.SizeRatio is double r ? r.ToString("0.00") : "n/a")}",
            $"  after:  {After.NonManifoldEdges} non-manifold edges, {After.BoundaryEdges} boundary, " +
            $"{After.Degenerate} degenerate, inverted={After.InvertedWinding}, " +
            $"sizeRatio={(After.SizeRatio is double s ? s.ToString("0.00") : "n/a")}",
        };
        foreach (var a in Attempts)
            lines.Add($"  {(a.Applied ? "APPLIED " : "skipped ")} {a.Strategy}: {a.Note}" +
                (a.RefusalReason is null ? "" : $" [refused: {a.RefusalReason}]"));
        foreach (var n in Notes) lines.Add("  note: " + n);
        lines.Add(Healed
            ? "VERDICT: HEALED — no vetoes remain on re-measure"
            : "VERDICT: HAND TO USER — vetoes remain after the loop cap; no pass claimed");
        return string.Join("\n", lines);
    }
}

/// <summary>Builder healer — the fix loop that acts on judge vetoes (wave 5, split off
/// from the standalone-judge lane; the judge agent owns invocation, this file owns
/// fixing, and the two never share code paths beyond the judge's own measurement
/// inputs, which are the healer's acceptance oracle).
/// Builder-first rule stands: the healer suggests/applies concrete fixes and
/// re-measures. It never replaces the builder, never fakes a pass, and never
/// claims a fix it did not measure. Strategies it deliberately does NOT attempt:
/// symmetry (asymmetry may be design intent — the gate itself never vetoes it),
/// silhouette (no reference image to reshape toward), hole filling (no strategy
/// in this version — open edges are reported, not silently patched).
/// Loop cap mirrors the backstop: at most 2 fix loops, then hand to the user.</summary>
public static class AiBuilderHealer
{
    public const int MaxFixLoops = 2;
    private const int MaxNonManifoldEdgesForSearch = 32;
    private const int MaxCandidateEvaluations = 128;
    private const int MinFacesKept = 4;

    /// <summary>Measure a mesh exactly the way the judge does (AiGateInput.FromMesh),
    /// plus the healer's own edge-incidence counts and an optional size ratio.
/// Throws ArgumentException on garbage face indices — a mesh the judge cannot
    /// even measure cannot be healed, and that must be loud, not silent.</summary>
    public static HealProblemCounts Measure(PolyMesh mesh, double[]? typicalDims = null, double? silhouetteIou = null)
    {
        if (mesh is null) throw new ArgumentException("A mesh is required.");
        ValidateIndices(mesh);
        var gates = AiGateInput.FromMesh(mesh);
        var edgeMap = EdgeMap(mesh);
        int excess = 0;
        foreach (var faces in edgeMap.Values)
            if (faces.Count > 2) excess += faces.Count - 2;
        double? ratio = null;
        if (typicalDims is not null)
        {
            var actual = BoundsDims(mesh);
            if (actual is not null) ratio = AiMeasuredFacts.WorstRatio(actual, typicalDims);
        }
        return new HealProblemCounts(
            mesh.VertexCount, mesh.FaceCount,
            gates.NonManifoldEdges, excess, gates.BoundaryEdges, gates.Degenerate,
            gates.Ngons, gates.QuadPercent, gates.SignedVolume, gates.InvertedWinding,
            ratio, gates.SymmetryError, silhouetteIou);
    }

    /// <summary>Heal a mesh. The input is never mutated; the returned mesh is a clone
    /// with accepted fixes applied. An edit is kept ONLY if the re-measure is better
    /// (no new veto kind, lower problem score) — otherwise it is reverted and the
    /// attempt is recorded as not applied.</summary>
    public static HealReport TryHeal(PolyMesh mesh, double[]? typicalDims = null, double? silhouetteIou = null, int maxLoops = MaxFixLoops)
    {
        if (mesh is null) throw new ArgumentException("A mesh is required.");
        if (maxLoops < 0) throw new ArgumentException("maxLoops must be >= 0.");
        var before = Measure(mesh, typicalDims, silhouetteIou);
        var working = mesh.Clone();
        var attempts = new List<HealAttempt>();
        var notes = new List<string>();

        // Observation notes: problems the healer sees but deliberately does not touch.
        if (before.SymmetryError is double sym && sym > 0.05)
            notes.Add($"symmetry: mirror error {sym * 100:0.#}% — not attempted; asymmetry may be design intent (gate never vetoes it)");
        if (before.SilhouetteIou is double iou && iou < 0.80)
            notes.Add("silhouette: IoU under 0.80 — refused; the healer has no reference image to reshape toward");
        if (before.BoundaryEdges > 0)
            notes.Add($"boundary: {before.BoundaryEdges} open edges — no hole-fill strategy in this version; reported, not silently patched");

        int loops = 0;
        while (loops < maxLoops)
        {
            var loopBefore = Measure(working, typicalDims, silhouetteIou);
            if (loopBefore.VetoCount == 0 && loopBefore.Degenerate == 0) break;
            loops++;
            int appliedThisLoop = 0;
            appliedThisLoop += TryStrategy(working, attempts, "flip-winding",
                () => loopBefore.InvertedWinding,
                m => FlipWinding(m),
                null, typicalDims, silhouetteIou);
            appliedThisLoop += TryStrategy(working, attempts, "remove-degenerate",
                () => MeasureLight(working).Degenerate > 0,
                m => RemoveDegenerateFaces(m),
                null, typicalDims, silhouetteIou);
            appliedThisLoop += TryStrategy(working, attempts, "remove-duplicate-faces",
                () => HasDuplicateFaces(working),
                m => RemoveDuplicateFaces(m),
                null, typicalDims, silhouetteIou);
            appliedThisLoop += ResolveNonManifold(working, attempts, typicalDims);
            {
                var sizeBefore = MeasureLight(working, typicalDims, silhouetteIou);
                if (sizeBefore.SizeOff)
                {
                    if (typicalDims is null)
                        attempts.Add(new HealAttempt("rescale-to-typical", false, sizeBefore, sizeBefore, "declined",
                            "no typical size supplied — nothing to scale toward"));
                    else if (!RescaleFeasible(working, typicalDims))
                        attempts.Add(new HealAttempt("rescale-to-typical", false, sizeBefore, sizeBefore, "declined",
                            "uniform scale cannot satisfy all axes — model proportions differ from typical, not just size"));
                    else
                        appliedThisLoop += TryStrategy(working, attempts, "rescale-to-typical",
                            () => true,
                            m => RescaleToTypical(m, typicalDims),
                            null, typicalDims, silhouetteIou);
                }
            }
            if (appliedThisLoop == 0) break;
        }

        var after = Measure(working, typicalDims, silhouetteIou);
        bool healed = after.VetoCount == 0;
        return new HealReport(working, before, after, attempts, notes, loops, healed, !healed);
    }

    // --- strategy plumbing: clone, mutate, re-measure, keep only if better. ---

    private static int TryStrategy(PolyMesh working, List<HealAttempt> attempts, string name,
        Func<bool> applicable, Action<PolyMesh> mutate, string? refusalIfApplicable,
        double[]? typicalDims = null, double? silhouetteIou = null)
    {
        var before = MeasureLight(working, typicalDims, silhouetteIou);
        if (!applicable())
            return 0;
        if (refusalIfApplicable is not null)
        {
            attempts.Add(new HealAttempt(name, false, before, before, "declined", refusalIfApplicable));
            return 0;
        }
        var candidate = working.Clone();
        mutate(candidate);
        if (candidate.FaceCount < MinFacesKept)
        {
            attempts.Add(new HealAttempt(name, false, before, before, "declined: result would have fewer than 4 faces", "result too destructive"));
            return 0;
        }
        var after = MeasureLight(candidate, typicalDims, silhouetteIou);
        if (!after.IsBetterThan(before))
        {
            attempts.Add(new HealAttempt(name, false, before, before, "no improving edit found"));
            return 0;
        }
        ReplaceContents(working, candidate);
        attempts.Add(new HealAttempt(name, true, before, after,
            $"faces {before.Faces} -> {after.Faces}, score {before.ProblemScore} -> {after.ProblemScore}"));
        return 1;
    }

    // Light measure used inside strategy evaluation: full gate inputs but no
    // symmetry pass (face removal does not meaningfully change it, and skipping it
    // keeps candidate search cheap). Size needs typical dims — passed through.
    private static HealProblemCounts MeasureLight(PolyMesh mesh, double[]? typicalDims = null, double? silhouetteIou = null)
    {
        var gates = AiGateInput.FromMesh(mesh);
        var edgeMap = EdgeMap(mesh);
        int excess = 0;
        foreach (var faces in edgeMap.Values)
            if (faces.Count > 2) excess += faces.Count - 2;
        double? ratio = null;
        if (typicalDims is not null)
        {
            var actual = BoundsDims(mesh);
            if (actual is not null) ratio = AiMeasuredFacts.WorstRatio(actual, typicalDims);
        }
        // FromMesh already runs SymmetryCheck; reuse it — no extra cost beyond the gate call.
        return new HealProblemCounts(
            mesh.VertexCount, mesh.FaceCount,
            gates.NonManifoldEdges, excess, gates.BoundaryEdges, gates.Degenerate,
            gates.Ngons, gates.QuadPercent, gates.SignedVolume, gates.InvertedWinding,
            ratio, gates.SymmetryError, silhouetteIou);
    }

    // --- non-manifold resolution: best single-face removal, stepwise. ---

    private static int ResolveNonManifold(PolyMesh working, List<HealAttempt> attempts, double[]? typicalDims = null)
    {
        var before = MeasureLight(working, typicalDims);
        if (before.NonManifoldEdges == 0) return 0;
        var map = EdgeMap(working);
        int badEdges = map.Values.Count(v => v.Count > 2);
        if (badEdges > MaxNonManifoldEdgesForSearch)
        {
            attempts.Add(new HealAttempt("resolve-nonmanifold", false, before, before, "declined",
                $"too many non-manifold edges ({badEdges}) for candidate search — handed to user"));
            return 0;
        }
        int removed = 0, evaluations = 0;
        while (true)
        {
            var current = MeasureLight(working, typicalDims);
            if (current.NonManifoldEdges == 0) break;
            if (evaluations >= MaxCandidateEvaluations)
            {
                attempts.Add(new HealAttempt("resolve-nonmanifold", removed > 0, before, current,
                    $"stopped after {evaluations} candidate evaluations with {current.NonManifoldEdges} non-manifold edges left",
                    "candidate budget exhausted"));
                return removed > 0 ? 1 : 0;
            }
            var edgeMap = EdgeMap(working);
            PolyMesh? best = null;
            HealProblemCounts? bestCounts = null;
            int bestFace = -1;
            foreach (var faces in edgeMap.Values.Where(v => v.Count > 2))
            {
                foreach (var fi in faces.Distinct().ToArray())
                {
                    if (evaluations >= MaxCandidateEvaluations) break;
                    evaluations++;
                    var candidate = working.Clone();
                    RemoveFaceAt(candidate, fi);
                    if (candidate.FaceCount < MinFacesKept) continue;
                    var counts = MeasureLight(candidate, typicalDims);
                    if (counts.HasNewVetoKindVs(current)) continue;
                    if (bestCounts is null || counts.ProblemScore < bestCounts.ProblemScore ||
                        (counts.ProblemScore == bestCounts.ProblemScore && counts.BoundaryEdges < bestCounts.BoundaryEdges))
                    {
                        best = candidate; bestCounts = counts; bestFace = fi;
                    }
                }
                if (evaluations >= MaxCandidateEvaluations) break;
            }
            if (best is null || bestCounts is null || !bestCounts.IsBetterThan(current))
            {
                attempts.Add(new HealAttempt("resolve-nonmanifold", removed > 0, before, current,
                    removed > 0
                        ? $"removed {removed} face(s) but {current.NonManifoldEdges} non-manifold edges remain — no further improving removal"
                        : "no single-face removal improves the mesh",
                    removed > 0 ? null : "no safe removal found"));
                return removed > 0 ? 1 : 0;
            }
            ReplaceContents(working, best);
            removed++;
            _ = bestFace;
        }
        var after = MeasureLight(working);
        attempts.Add(new HealAttempt("resolve-nonmanifold", true, before, after,
            $"removed {removed} face(s), non-manifold edges {before.NonManifoldEdges} -> {after.NonManifoldEdges}, " +
            $"boundary edges {before.BoundaryEdges} -> {after.BoundaryEdges} (tradeoff recorded, not hidden)"));
        return 1;
    }

    // --- individual fixes (each pure on its input mesh). ---

    private static void FlipWinding(PolyMesh mesh)
    {
        for (int i = 0; i < mesh.Faces.Count; i++)
        {
            var f = mesh.Faces[i];
            var idx = (int[])f.Indices.Clone();
            Array.Reverse(idx);
            Vec2[]? uvs = null;
            if (f.UVs is not null)
            {
                uvs = (Vec2[])f.UVs.Clone();
                Array.Reverse(uvs);
            }
            mesh.Faces[i] = new Face(idx, uvs, f.Material);
            if (mesh.FaceUV2 is not null && i < mesh.FaceUV2.Length && mesh.FaceUV2[i] is not null)
                Array.Reverse(mesh.FaceUV2[i]);
        }
    }

    private static void RemoveDegenerateFaces(PolyMesh mesh)
    {
        for (int i = mesh.Faces.Count - 1; i >= 0; i--)
            if (IsDegenerate(mesh, mesh.Faces[i])) RemoveFaceAt(mesh, i);
    }

    private static bool IsDegenerate(PolyMesh mesh, Face face)
    {
        var ring = new List<int>();
        foreach (var idx in face.Indices)
        {
            if (ring.Count == 0 || ring[^1] != idx) ring.Add(idx);
        }
        if (ring.Count > 1 && ring[^1] == ring[0]) ring.RemoveAt(ring.Count - 1);
        if (ring.Count < 3 || ring.Distinct().Count() != ring.Count) return true;
        return mesh.FaceArea(face) <= 1e-12f;
    }

    private static bool HasDuplicateFaces(PolyMesh mesh)
    {
        var seen = new HashSet<string>();
        foreach (var f in mesh.Faces)
        {
            var key = string.Join(",", f.Indices.OrderBy(v => v));
            if (!seen.Add(key)) return true;
        }
        return false;
    }

    private static void RemoveDuplicateFaces(PolyMesh mesh)
    {
        var seen = new HashSet<string>();
        for (int i = mesh.Faces.Count - 1; i >= 0; i--)
        {
            var key = string.Join(",", mesh.Faces[i].Indices.OrderBy(v => v));
            if (!seen.Add(key)) RemoveFaceAt(mesh, i);
        }
    }

    private static bool RescaleFeasible(PolyMesh mesh, double[] typicalDims)
    {
        var actual = BoundsDims(mesh);
        if (actual is null) return false;
        double lo = 0, hi = double.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            if (actual[i] <= 0 || typicalDims[i] <= 0) return false;
            lo = Math.Max(lo, typicalDims[i] / (1.5 * actual[i]));
            hi = Math.Min(hi, 1.5 * typicalDims[i] / actual[i]);
        }
        return lo <= hi;
    }

    private static void RescaleToTypical(PolyMesh mesh, double[] typicalDims)
    {
        var actual = BoundsDims(mesh);
        if (actual is null) return;
        // Feasible uniform scale interval: every axis ratio within 1.5x (margin under the 1.6x rule).
        double lo = 0, hi = double.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            if (actual[i] <= 0 || typicalDims[i] <= 0) return;
            lo = Math.Max(lo, typicalDims[i] / (1.5 * actual[i]));
            hi = Math.Min(hi, 1.5 * typicalDims[i] / actual[i]);
        }
        if (lo > hi) return; // Infeasible: proportions differ, not just size — caller records the refusal.
        double s = lo > 1 ? lo : hi;
        var (min, max) = Bounds(mesh);
        var center = (min + max) / 2;
        for (int i = 0; i < mesh.Positions.Count; i++)
            mesh.Positions[i] = center + (mesh.Positions[i] - center) * (float)s;
    }

    // --- mesh helpers. ---

    private static void RemoveFaceAt(PolyMesh mesh, int faceIndex)
    {
        mesh.Faces.RemoveAt(faceIndex);
        if (mesh.FaceUV2 is not null && faceIndex < mesh.FaceUV2.Length)
        {
            var rows = mesh.FaceUV2.ToList();
            rows.RemoveAt(faceIndex);
            mesh.FaceUV2 = rows.ToArray();
        }
    }

    private static void ReplaceContents(PolyMesh target, PolyMesh source)
    {
        target.Positions = new List<Vec3>(source.Positions);
        target.Faces = source.Faces;
        target.UV2 = source.UV2;
        target.FaceUV2 = source.FaceUV2;
        target.VertexColors = source.VertexColors;
        target.VertexEmission = source.VertexEmission;
        target.Paint = source.Paint;
        target.Morphs = source.Morphs;
        target.Seams = source.Seams;
        target.Skin = source.Skin;
    }

    private static Dictionary<(int, int), List<int>> EdgeMap(PolyMesh mesh)
    {
        var map = new Dictionary<(int, int), List<int>>();
        for (int fi = 0; fi < mesh.Faces.Count; fi++)
        {
            var idx = mesh.Faces[fi].Indices;
            for (int k = 0; k < idx.Length; k++)
            {
                int a = idx[k], b = idx[(k + 1) % idx.Length];
                var key = a < b ? (a, b) : (b, a);
                if (!map.TryGetValue(key, out var list)) map[key] = list = new List<int>();
                list.Add(fi);
            }
        }
        return map;
    }

    private static void ValidateIndices(PolyMesh mesh)
    {
        for (int fi = 0; fi < mesh.Faces.Count; fi++)
        {
            foreach (var idx in mesh.Faces[fi].Indices)
                if (idx < 0 || idx >= mesh.VertexCount)
                    throw new ArgumentException($"Face {fi} references vertex {idx} outside 0..{mesh.VertexCount - 1} — the judge cannot measure this mesh, so it cannot be healed.");
        }
    }

    private static (Vec3 Min, Vec3 Max) Bounds(PolyMesh mesh)
    {
        var min = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (var p in mesh.Positions)
        {
            min = new Vec3(MathF.Min(min.x, p.x), MathF.Min(min.y, p.y), MathF.Min(min.z, p.z));
            max = new Vec3(MathF.Max(max.x, p.x), MathF.Max(max.y, p.y), MathF.Max(max.z, p.z));
        }
        return (min, max);
    }

    private static double[]? BoundsDims(PolyMesh mesh)
    {
        if (mesh.Positions.Count == 0) return null;
        var (min, max) = Bounds(mesh);
        var dims = new double[] { max.x - min.x, max.y - min.y, max.z - min.z };
        if (dims.Any(d => d <= 0)) return null;
        return dims;
    }
}
