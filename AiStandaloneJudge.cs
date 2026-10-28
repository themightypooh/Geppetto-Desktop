#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Effigy;

namespace Marionette.Desktop;

/// <summary>Standalone judge for a PREBUILT .effigy (wave 5, production punch item M4).
/// The gap: AiCli is build-coupled. AiSession requires --model and --out (a NEW run
/// directory) and judges only checkpoints of a model it just built, so a script-built
/// .effigy had no judge invocation at all. The production pass could only exercise the
/// measured topology gates plus the AiJudgeBackstop harness, and produced NO vision
/// score for the buoy — recorded as a gap, not faked. This command closes the
/// invocation gap without inventing the missing piece:
///   marionette-desktop.dll --ai judge --file MODEL.effigy [--body NAME] [--out REPORT.json]
/// It loads the document, rebuilds it, measures every non-reference body with
/// AiGateInput.FromMesh + AiJudge.Gate (the same gates the backstop uses), and prints
/// a verdict. Builder-first rule stands: the judge is a backstop only. Hard fails are
/// vetoes (non-manifold, inside-out winding, silhouette under 0.80 IoU, size over 1.6x
/// — the last two need a brief/reference the standalone judge does not have, so they
/// are reported as NOT MEASURED, never assumed). Everything else is soft: reported,
/// never minned into a ruling number.
/// What this judge deliberately does NOT cover, stated in every report:
///   - Vision: NOT SCORED. No vision model is run. The only vision path in this codebase
///     is AiSession's build-coupled judge loop, which generates its own rubric and scores
///     checkpoints of a model it just built. There is no standalone render-to-vision
///     -to-score entry point, and a numeric vision score without a locked rubric would
///     be invented precision. Local qwen3-vl:4b exists in Ollama but is unwired to this
///     path; wiring it is a separate integration (render via HeadlessRender + an
///     Ollama-compatible vision call + a locked rubric), not a flag on this command.
///   - Inter-body clipping: NOT CHECKED. Like topology, this judge measures each body
///     alone. Overlap between bodies (production M1: chain discs into the buoy sphere)
///     is a parallel wave-5 check, not covered here.
///   - Size vs typical prop, silhouette IoU, flex, take: NOT MEASURED (no brief, no
///     reference image, no clip was supplied). Unmeasured never vetoes.
/// Exit codes: 0 = pass (no hard fails), 2 = vetoed (at least one hard fail),
/// 3 = usage or load error (missing file, unknown body, rebuild errors, no bodies).
/// This command never opens a window, never touches the live cabinet, and never
/// touches GPT's stop/comment/continue run-control area (it shares no code with it).</summary>
public sealed record StandaloneBodyReport(
    string Name,
    int Vertices,
    int Faces,
    int LooseVertices,
    AiGateInput Gates,
    AiGateResult Result)
{
    public IReadOnlyList<string> HardFails =>
        Result.Gates.Where(g => g.HardFail).Select(g => Name + ": " + g.Name + ": " + g.Detail).ToArray();

    public JsonObject Json()
    {
        var o = new JsonObject
        {
            ["name"] = Name,
            ["vertices"] = Vertices,
            ["faces"] = Faces,
            ["looseVertices"] = LooseVertices,
            ["nonManifoldEdges"] = Gates.NonManifoldEdges,
            ["boundaryEdges"] = Gates.BoundaryEdges,
            ["ngons"] = Gates.Ngons,
            ["quadPercent"] = Math.Round(Gates.QuadPercent, 2),
            ["degenerateFaces"] = Gates.Degenerate,
            ["signedVolume"] = Math.Round(Gates.SignedVolume, 2),
            ["invertedWinding"] = Gates.InvertedWinding,
            ["symmetryError"] = Gates.SymmetryError is double s ? Math.Round(s, 4) : null,
            ["gates"] = Result.Json(),
        };
        var hard = new JsonArray();
        foreach (var f in HardFails) hard.Add(f);
        o["hardFails"] = hard;
        return o;
    }

    public string HumanLine()
    {
        var flags = new List<string>();
        if (Gates.NonManifoldEdges > 0) flags.Add($"{Gates.NonManifoldEdges} non-manifold edges (VETO)");
        if (Gates.BoundaryEdges > 0) flags.Add($"{Gates.BoundaryEdges} open boundary edges");
        if (Gates.Ngons > 0) flags.Add($"{Gates.Ngons} n-gons");
        if (Gates.Degenerate > 0) flags.Add($"{Gates.Degenerate} degenerate faces");
        if (Gates.InvertedWinding) flags.Add("wound inside-out (VETO)");
        flags.Add($"{Gates.QuadPercent:0.#}% quads");
        flags.Add(Gates.SymmetryError is double s ? $"symmetry error {s * 100:0.##}%" : "symmetry not measured");
        return $"  {Name}: {Vertices} verts, {Faces} faces, {LooseVertices} loose verts — " + string.Join("; ", flags);
    }
}

public sealed record StandaloneJudgeReport(
    string File,
    IReadOnlyList<StandaloneBodyReport> Bodies,
    int ReferenceBodiesSkipped,
    IReadOnlyList<string> RebuildWarnings,
    bool RebuildFailed,
    string Verdict)
{
    public IReadOnlyList<string> HardFails => Bodies.SelectMany(b => b.HardFails).ToArray();
    public int ExitCode => RebuildFailed || Verdict == "error" ? 3 : Verdict == "pass" ? 0 : 2;

    public JsonObject Json()
    {
        var bodies = new JsonArray();
        foreach (var b in Bodies) bodies.Add(b.Json());
        var hard = new JsonArray();
        foreach (var f in HardFails) hard.Add(f);
        var warnings = new JsonArray();
        foreach (var w in RebuildWarnings) warnings.Add(w);
        return new JsonObject
        {
            ["file"] = File,
            ["verdict"] = Verdict,
            ["exitCode"] = ExitCode,
            ["bodiesJudged"] = Bodies.Count,
            ["referenceBodiesSkipped"] = ReferenceBodiesSkipped,
            ["bodies"] = bodies,
            ["hardFails"] = hard,
            ["rebuildWarnings"] = warnings,
            ["rebuildFailed"] = RebuildFailed,
            ["vision"] = new JsonObject
            {
                ["scored"] = false,
                ["reason"] = "No vision model was run. The only vision path (AiSession judge loop) is build-coupled and generates its own rubric; there is no standalone render-to-vision-to-score entry point, and a numeric vision score without a locked rubric would be invented precision. Local qwen3-vl:4b is present in Ollama but unwired to this path.",
            },
            ["notMeasured"] = new JsonObject
            {
                ["sizeVsTypical"] = "no brief/prop match supplied — size vs typical not measured, never assumed",
                ["silhouetteIou"] = "no reference image supplied — silhouette not measured, never assumed",
                ["flex"] = "no clip supplied — flex not measured",
                ["take"] = "no clip supplied — take not measured",
                ["interBodyClipping"] = "NOT CHECKED — this judge measures each body alone; inter-body overlap is a parallel wave-5 check, not covered here",
            },
        };
    }

    public string HumanReport()
    {
        var lines = new List<string>
        {
            $"Standalone judge: {File}",
            $"Bodies judged: {Bodies.Count} (reference bodies skipped: {ReferenceBodiesSkipped})",
        };
        foreach (var b in Bodies) lines.Add(b.HumanLine());
        foreach (var w in RebuildWarnings) lines.Add("  rebuild note: " + w);
        if (HardFails.Count > 0)
        {
            lines.Add("Hard fails (vetoes):");
            foreach (var f in HardFails) lines.Add("  VETO: " + f);
        }
        lines.Add(RebuildFailed
            ? "VERDICT: ERROR — rebuild failed, geometry cannot be trusted; no pass/fail claimed"
            : Verdict == "pass"
                ? "VERDICT: PASS — no measured hard fails (vision NOT SCORED; inter-body clipping NOT CHECKED)"
                : "VERDICT: VETO — at least one measured hard fail (vision NOT SCORED; inter-body clipping NOT CHECKED)");
        return string.Join("\n", lines);
    }
}

public static class AiStandaloneJudge
{
    public const string Usage = """
Standalone judge for a prebuilt model (no build, no vision score):
  --ai judge --file MODEL.effigy [--body NAME] [--out REPORT.json]
    --file  existing .effigy document to judge (required)
    --body  judge only the named body (default: every non-reference body)
    --out   also write the full JSON report to this file (stdout stays human-readable)
  Exit codes: 0 pass (no hard fails), 2 vetoed (a hard fail), 3 usage/load error.
  Vision is NOT scored by this command and inter-body clipping is NOT checked;
  both are stated in every report rather than silently omitted.
""";

    /// <summary>Judge already-loaded bodies. Pure verdict logic: no files, no rendering,
    /// no model calls — this is what the unit tests exercise.</summary>
    public static StandaloneJudgeReport JudgeBodies(
        string file,
        IEnumerable<(string Name, PolyMesh Mesh)> bodies,
        int referenceBodiesSkipped = 0,
        IReadOnlyList<string>? rebuildWarnings = null,
        bool rebuildFailed = false)
    {
        var reports = new List<StandaloneBodyReport>();
        foreach (var (name, mesh) in bodies)
        {
            var input = AiGateInput.FromMesh(mesh);
            var result = AiJudge.Gate(input);
            int loose = Effigy.TopologyReport.Measure(mesh, name).LooseVertices;
            reports.Add(new StandaloneBodyReport(name, mesh.VertexCount, mesh.FaceCount, loose, input, result));
        }
        string verdict = rebuildFailed || reports.Count == 0 ? "error" : reports.SelectMany(b => b.HardFails).Any() ? "veto" : "pass";
        return new StandaloneJudgeReport(file, reports, referenceBodiesSkipped, rebuildWarnings ?? Array.Empty<string>(), rebuildFailed, verdict);
    }

    /// <summary>Load a .effigy, rebuild it, judge every non-reference body.</summary>
    public static StandaloneJudgeReport JudgeFile(string path, string? onlyBody = null)
    {
        if (!File.Exists(path)) throw new ArgumentException("Model file not found: " + path);
        var studio = StudioDocument.ReadFile(path);
        var rebuilt = studio.Rebuild();
        var warnings = rebuilt.Warnings.Select(w => w.FeatureId + ": " + w.Message).ToArray();
        if (rebuilt.HasErrors)
        {
            // Judging partially rebuilt geometry as pass/fail would be a fib: the
            // rebuild itself failed, so the verdict is ERROR, not a veto or a pass.
            var errors = rebuilt.Errors.Select(e => e.FeatureId + ": " + e.Message).ToArray();
            return new StandaloneJudgeReport(Path.GetFullPath(path), Array.Empty<StandaloneBodyReport>(), 0,
                warnings.Concat(errors).ToArray(), RebuildFailed: true, Verdict: "error");
        }
        var skipped = studio.Bodies.Count(b => b.IsReference);
        var picked = studio.Bodies.Where(b => !b.IsReference).ToArray();
        if (onlyBody is not null)
        {
            picked = picked.Where(b => b.Name.Equals(onlyBody, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (picked.Length == 0) throw new ArgumentException("No body named '" + onlyBody + "' in " + path);
        }
        if (picked.Length == 0) throw new ArgumentException("No judgable bodies in " + path + " (only reference bodies, or empty document).");
        return JudgeBodies(Path.GetFullPath(path),
            picked.Select(b => (string.IsNullOrWhiteSpace(b.Name) ? b.Id : b.Name, b.Mesh)),
            skipped, warnings, rebuildFailed: false);
    }

    public static int Run(string[] args)
    {
        string? file = null, body = null, outPath = null;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--help": Console.WriteLine(Usage); return 0;
                    case "--file":
                        if (i + 1 >= args.Length) throw new ArgumentException("--file requires a value.");
                        file = args[++i]; break;
                    case "--body":
                        if (i + 1 >= args.Length) throw new ArgumentException("--body requires a value.");
                        body = args[++i]; break;
                    case "--out":
                        if (i + 1 >= args.Length) throw new ArgumentException("--out requires a value.");
                        outPath = args[++i]; break;
                    default: throw new ArgumentException("Unknown judge option: " + args[i]);
                }
            }
            if (string.IsNullOrWhiteSpace(file)) throw new ArgumentException("Standalone judge requires --file MODEL.effigy.\n" + Usage);
            var report = JudgeFile(file, body);
            Console.WriteLine(report.HumanReport());
            if (outPath is not null) File.WriteAllText(outPath, report.Json().ToJsonString());
            return report.ExitCode;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine("judge error: " + ex.Message);
            Console.Error.WriteLine(Usage);
            return 3;
        }
    }
}
