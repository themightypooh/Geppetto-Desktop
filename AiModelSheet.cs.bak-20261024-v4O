#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Marionette.Desktop;

/// <summary>The model sheet: what a modeller needs before the first shape. Overall and per-part sizes (each
/// measured with a source, read off a saved photo, or estimated with how), cross-sections, materials with
/// colours sampled from the photos, the build order, a scale cue and the reference photos with ready
/// refimage lines. The researcher fills it in through write_sheet; this checks it and does the arithmetic.</summary>
public static class AiModelSheet
{
    public const string Schema = """
{"object":"what it is","summary":"one paragraph: form, construction, period",
 "overall":{"length":DIM,"width":DIM,"thickness":DIM},
 "parts":[{"name":"blade","along":[0.42,1.0],"dims":{"length":DIM,"width":DIM,"thickness":DIM},
           "cross_section":"flattened diamond","shape":"straight taper to a needle point",
           "material":{"name":"steel","sample":{"ref":"refs/01-x.png","along":[0.5,0.9]},"metal":true,"roughness":0.3,"wear":"light pitting"}}],
 "build_order":["blade","guard rondel","grip","pommel rondel"],
 "scale_cue":"overall length 38.8 cm ≈ a forearm and hand",
 "main_ref":"refs/01-x.png","refs":[{"file":"refs/01-x.png","view":"side","note":"whole dagger, point to the right"}],
 "gaps":["no measured guard diameter"]}
DIM = {"value":38.8,"unit":"cm","basis":"measured","source":"record page URL"}
    | {"basis":"photo","ref":"refs/01-x.png","fraction":0.12}   (fraction of the overall length, read off that photo's profile; Geppetto computes the value)
    | {"value":3,"unit":"cm","basis":"estimated","how":"typical rondel width in the V&A record's description"}
units: mm, cm, m, in, ft. part "along" = where the part sits on the main photo's long axis (0..1), so its colour and width can be measured.
material: "sample" (preferred: Geppetto takes the median colour of those pixels) or "hex" plus "why_not_sampled".
""";

    static readonly Dictionary<string, double> ToMm = new() { ["mm"] = 1, ["cm"] = 10, ["m"] = 1000, ["in"] = 25.4, ["inch"] = 25.4, ["inches"] = 25.4, ["ft"] = 304.8 };
    static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
    static string F(double v, int places = 2) => (Math.Round(v, places) + 0.0).ToString(CultureInfo.InvariantCulture);

    public sealed class Result
    {
        public JsonObject Sheet = new();
        public string Markdown = "";
        public List<string> Errors = new();
    }

    /// <summary>Check the researcher's sheet against what was really found, measure what can be measured
    /// from the saved photos, and produce the final json + markdown. Errors go back to the researcher.</summary>
    public static Result Build(JsonNode? input, AiResearch research, string request)
    {
        var r = new Result();
        var errors = r.Errors;
        if (input is not JsonObject sheet) { errors.Add("sheet must be an object: " + Schema); return r; }
        var sources = new HashSet<string>(research.Sources.Concat(research.Photos.Select(p => p.Page)).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
        bool Known(string url) => sources.Contains(url) || sources.Any(s => s.TrimEnd('/') == url.TrimEnd('/'));
        string mainRef = Str(sheet["main_ref"]);
        RefPhoto? main = null;
        if (mainRef.Length > 0) { try { main = research.Photo(mainRef); } catch (ArgumentException ex) { errors.Add("main_ref: " + ex.Message); } }

        // overall length first: photo-based part sizes are fractions of it
        double? overallMm = null;
        JsonObject? Dim(JsonNode? node, string where, double? lengthMm)
        {
            if (node is not JsonObject d) { errors.Add(where + ": give {value,unit,basis,...}"); return null; }
            string basis = Str(d["basis"]).ToLowerInvariant();
            var o = new JsonObject { ["basis"] = basis };
            double mm;
            if (basis == "photo")
            {
                double fraction = AiResearch.Number(d["fraction"]);
                string refFile = Str(d["ref"]);
                if (!(fraction > 0 && fraction <= 1)) { errors.Add(where + ": photo basis needs fraction 0..1 of the overall length"); return null; }
                if (lengthMm == null) { errors.Add(where + ": photo basis needs a measured or estimated overall length"); return null; }
                try { research.Photo(refFile); } catch (ArgumentException ex) { errors.Add(where + ": " + ex.Message); return null; }
                mm = fraction * lengthMm.Value;
                o["ref"] = refFile; o["fraction"] = fraction;
                o["how"] = $"{F(fraction, 3)} × overall length, measured on {refFile}";
                o["value"] = Math.Round(mm / 10, 2); o["unit"] = "cm";
            }
            else
            {
                double value = AiResearch.Number(d["value"]);
                string unit = Str(d["unit"]).ToLowerInvariant();
                if (!(value > 0)) { errors.Add(where + ": value must be a positive number"); return null; }
                if (!ToMm.TryGetValue(unit, out var k)) { errors.Add(where + ": unit must be mm, cm, m, in or ft"); return null; }
                mm = value * k;
                o["value"] = value; o["unit"] = unit;
                if (basis == "measured")
                {
                    string source = Str(d["source"]);
                    if (!Known(source)) { errors.Add(where + $": measured needs a source URL you actually searched or fetched (got '{source}'). If it isn't from a record, use basis estimated + how."); return null; }
                    o["source"] = source;
                }
                else if (basis == "estimated")
                {
                    string how = Str(d["how"]);
                    if (how.Length < 8) { errors.Add(where + ": estimated needs how (what the estimate is based on)"); return null; }
                    o["how"] = how;
                }
                else { errors.Add(where + ": basis is measured, photo or estimated"); return null; }
            }
            o["mm"] = Math.Round(mm, 1); o["in"] = Math.Round(mm / 25.4, 2);
            return o;
        }

        var overallIn = sheet["overall"] as JsonObject;
        var overall = new JsonObject();
        if (overallIn == null || overallIn.Count == 0) errors.Add("overall: give at least length (or height)");
        else
        {
            string lengthKey = overallIn.ContainsKey("length") ? "length" : overallIn.ContainsKey("height") ? "height" : "";
            if (lengthKey.Length == 0) errors.Add("overall: needs length or height");
            else if (Dim(overallIn[lengthKey], "overall." + lengthKey, null) is JsonObject l)
            {
                if (Str(l["basis"]) == "photo") errors.Add("overall length can't be photo-based; it is the scale for everything else");
                overall[lengthKey] = l; overallMm = l["mm"]!.GetValue<double>();
            }
            foreach (var (k, v) in overallIn)
                if (k != lengthKey && Dim(v, "overall." + k, overallMm) is JsonObject d) overall[k] = d;
        }

        var parts = new JsonArray();
        var names = new List<string>();
        foreach (var (p, i) in ((sheet["parts"] as JsonArray) ?? new JsonArray()).Select((p, i) => (p, i)))
        {
            string name = Str(p?["name"]);
            string where = $"parts[{i}]" + (name.Length > 0 ? $" ({name})" : "");
            if (name.Length == 0) { errors.Add(where + ": name"); continue; }
            names.Add(name);
            var part = new JsonObject { ["name"] = name };
            double[]? along = (p!["along"] as JsonArray)?.Select(v => AiResearch.Number(v)).ToArray();
            if (along is { Length: 2 } && along[0] >= 0 && along[1] <= 1 && along[1] > along[0]) part["along"] = new JsonArray(along[0], along[1]);
            else along = null;
            var dims = new JsonObject();
            foreach (var (k, v) in (p["dims"] as JsonObject) ?? new JsonObject())
                if (Dim(v, where + ".dims." + k, overallMm) is JsonObject d) dims[k] = d;
            if (dims.Count == 0) errors.Add(where + ": dims (length, width, thickness or diameter)");
            // the main photo gives a second opinion on this part's length and width when its stretch is known
            if (along != null && main?.Analysis is { Clean: true } a && overallMm != null)
                part["photo_check"] = $"on {main.File}: length ≈ {F((along[1] - along[0]) * overallMm.Value / 10, 1)} cm, widest ≈ {F(a.WidthFraction(along[0], along[1]) * overallMm.Value / 10, 1)} cm";
            part["dims"] = dims;
            part["cross_section"] = Str(p["cross_section"]);
            part["shape"] = Str(p["shape"]);
            if (Str(p["cross_section"]).Length == 0) errors.Add(where + ": cross_section");
            var m = p["material"] as JsonObject;
            if (m == null) { errors.Add(where + ": material"); continue; }
            var mat = new JsonObject { ["name"] = Str(m["name"]) };
            if (m["sample"] is JsonObject s)
            {
                try
                {
                    var photo = research.Photo(Str(s["ref"]));
                    double[]? box = (s["box"] as JsonArray)?.Select(v => AiResearch.Number(v)).ToArray();
                    double[] at = (s["along"] as JsonArray)?.Select(v => AiResearch.Number(v)).ToArray() ?? along ?? new[] { 0.0, 1.0 };
                    if (box is { Length: not 4 } || at.Length != 2) throw new ArgumentException("sample: along [from,to] or box [x0,y0,x1,y1], 0..1");
                    if (photo.Analysis!.Greyscale) throw new ArgumentException($"sample: {photo.File} is a black-and-white photo, so it has no colour; sample from a colour photo (its own along/box), or save one");
                    var c = photo.Analysis!.Sample(at[0], at[1], box);
                    if (c.Count < 20) throw new ArgumentException($"sample: only {c.Count} object pixels there; pick a stretch of the object");
                    mat["hex"] = c.Hex;
                    mat["sampled_from"] = photo.File + (box != null ? $" box {string.Join(",", box.Select(v => F(v)))}" : $" along {F(at[0])}-{F(at[1])}") + $" ({c.Count} px median)";
                }
                catch (ArgumentException ex) { errors.Add(where + ".material." + ex.Message); }
            }
            else
            {
                string hex = Str(m["hex"]).ToLowerInvariant();
                if (!System.Text.RegularExpressions.Regex.IsMatch(hex, "^#[0-9a-f]{6}$")) errors.Add(where + ".material: sample {ref, along} from a saved photo, or hex #rrggbb + why_not_sampled");
                else if (Str(m["why_not_sampled"]).Length < 6) errors.Add(where + ".material: say why_not_sampled (sampling from a photo is the default)");
                else { mat["hex"] = hex; mat["sampled_from"] = "not sampled: " + Str(m["why_not_sampled"]); }
            }
            if (m["metal"] is not JsonValue mv || !mv.TryGetValue<bool>(out var metal)) { errors.Add(where + ".material: metal true/false"); metal = false; }
            double rough = AiResearch.Number(m["roughness"]);
            if (!(rough >= 0 && rough <= 1)) errors.Add(where + ".material: roughness 0..1");
            mat["metal"] = metal; mat["roughness"] = double.IsNaN(rough) ? null : Math.Round(rough, 2);
            mat["wear"] = Str(m["wear"]);
            part["material"] = mat;
            parts.Add(part);
        }
        if (names.Count == 0) errors.Add("parts: list every part (name, dims, cross_section, shape, material)");
        var order = ((sheet["build_order"] as JsonArray) ?? new JsonArray()).Select(Str).Where(s => s.Length > 0).ToList();
        var missing = names.Where(n => !order.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        if (order.Count == 0 || missing.Count > 0) errors.Add("build_order: every part name, biggest primary form first" + (missing.Count > 0 ? "; missing " + string.Join(", ", missing) : ""));
        string scale = Str(sheet["scale_cue"]);
        if (scale.Length < 6) errors.Add("scale_cue: something familiar to judge its size by");
        if (research.Photos.Count == 0) errors.Add("refs: save at least one photo (save_image) before writing the sheet");
        if (errors.Count > 0) return r;

        // photos: views/notes from the researcher, licence and measurements from Geppetto
        var notes = ((sheet["refs"] as JsonArray) ?? new JsonArray()).OfType<JsonObject>().ToDictionary(o => Str(o["file"]).Replace("\\", "/").TrimStart('.', '/'), o => o);
        var refs = new JsonArray();
        var refimages = new JsonArray();
        double lengthIn = overallMm!.Value / 25.4;
        double thicknessIn = overall.Where(kv => kv.Key is "width" or "thickness" or "diameter" or "depth").Select(kv => kv.Value!["in"]!.GetValue<double>()).DefaultIfEmpty(lengthIn * 0.1).Max();
        bool lengthMeasured = Str(overall.First().Value!["basis"]) == "measured";
        main ??= research.Photos.FirstOrDefault(p => p.Analysis?.Clean == true && p.View is "side" or "front") ?? research.Photos[0];
        foreach (var photo in research.Photos)
        {
            if (notes.TryGetValue(photo.File, out var n) && Str(n["view"]) is { Length: > 0 } v && AiResearch.Views.Contains(v)) photo.View = v;
            if (n != null && Str(n["note"]) is { Length: > 0 } note) photo.Note = note;
            var o = new JsonObject
            {
                ["file"] = photo.File, ["original"] = photo.Original, ["view"] = photo.View, ["title"] = photo.Title, ["note"] = photo.Note,
                ["licence"] = photo.Licence, ["attribution"] = photo.Attribution, ["page"] = photo.Page, ["url"] = photo.Url,
                ["size"] = $"{photo.Width}x{photo.Height}", ["clean_profile"] = photo.Analysis?.Clean ?? false, ["main"] = photo == main,
            };
            // one reference plane only: the main photo (other photos are often different objects)
            if (photo == main && RefImageLine(photo, lengthIn, thicknessIn) is string line)
            {
                // absolute path, so the line pastes into a build.txt anywhere; "line" keeps the run-relative one
                string abs = research.RefsDir.Length > 0 ? line.Replace(" " + photo.File + " ", " " + Path.Combine(Path.GetDirectoryName(research.RefsDir)!, photo.File) + " ") : line;
                o["refimage"] = abs;
                refimages.Add(new JsonObject { ["file"] = photo.File, ["line"] = line, ["absolute"] = abs, ["scale"] = (lengthMeasured ? "measured" : "estimated") + " overall length " + F(lengthIn) + " in" });
            }
            refs.Add(o);
        }

        r.Sheet = new JsonObject
        {
            ["object"] = Str(sheet["object"]) is { Length: > 0 } obj ? obj : request,
            ["request"] = request,
            ["summary"] = Str(sheet["summary"]),
            ["units_note"] = "every size has mm and in (Effigy works in inches, Z-up); basis says measured (source), photo (read off a saved photo) or estimated (how)",
            ["overall"] = overall,
            ["parts"] = parts,
            ["build_order"] = new JsonArray(order.Select(x => (JsonNode)x).ToArray()),
            ["scale_cue"] = scale,
            ["main_ref"] = main.File,
            ["refs"] = refs,
            ["refimage"] = refimages,
            ["gaps"] = new JsonArray(((sheet["gaps"] as JsonArray) ?? new JsonArray()).Select(Str).Where(s => s.Length > 0).Select(s => (JsonNode)s).ToArray()),
            ["sources"] = new JsonArray(research.Sources.Where(s => s.Length > 0).Distinct().Select(s => (JsonNode)s).ToArray()),
            ["written"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        };
        r.Markdown = Markdown(r.Sheet);
        return r;
    }

    /// <summary>A refimage line that stands a clean profile photo at real size: the object's long side scaled to
    /// the overall length, centred on the origin (upright objects stand on z = 0), just behind the model.</summary>
    public static string? RefImageLine(RefPhoto photo, double lengthIn, double thicknessIn)
    {
        var a = photo.Analysis;
        if (a == null || !a.Clean || photo.View is not ("side" or "front" or "top")) return null;
        double k = photo.Width / (double)a.Width;               // analysis pixels -> photo pixels
        double s = lengthIn / (a.LongPx * k);                   // inches per photo pixel
        double cx = (a.X0 + a.X1 + 1) / 2.0 * k, cy = (a.Y0 + a.Y1 + 1) / 2.0 * k, bottom = (a.Y1 + 1) * k;
        double height = photo.Height * s;
        double ox = -(cx - photo.Width / 2.0) * s;
        // upright (taller than long) objects stand on the ground in side/front; flat or top views centre
        double oy = !a.Horizontal && photo.View != "top" ? -(photo.Height / 2.0 - bottom) * s : -(photo.Height / 2.0 - cy) * s;
        double depth = Math.Round(thicknessIn * 1.5 + 1, 1);
        string path = photo.File;
        return $"refimage {photo.View} {path} height={F(height)} offset={F(ox)},{F(oy)} depth={F(depth, 1)} opacity=0.5";
    }

    static string DimText(JsonNode? d) => d == null ? "" : $"{F(d["mm"]!.GetValue<double>(), 1)} mm ({F(d["in"]!.GetValue<double>())} in)";
    static string Basis(JsonNode? d) => d == null ? "" : Str(d["basis"]) switch
    {
        "measured" => "measured: " + Str(d["source"]),
        "photo" => "photo: " + Str(d["how"]),
        _ => "estimated: " + Str(d["how"]),
    };
    static string Cell(string s) => s.Replace("|", "\\|").Replace("\n", " ");

    public static string Markdown(JsonObject s)
    {
        var b = new StringBuilder();
        b.Append("# Model sheet: ").Append(Str(s["object"])).Append("\n\n");
        if (Str(s["summary"]).Length > 0) b.Append(Str(s["summary"])).Append("\n\n");
        b.Append("Request: ").Append(Str(s["request"])).Append("  \nWritten: ").Append(Str(s["written"])).Append(" by Geppetto research. Effigy units are inches, Z-up.\n\n");
        b.Append("**Scale cue:** ").Append(Str(s["scale_cue"])).Append("\n\n");
        b.Append("## Overall\n\n| | size | basis |\n|---|---|---|\n");
        foreach (var (k, v) in (JsonObject)s["overall"]!) b.Append($"| {k} | {DimText(v)} | {Cell(Basis(v))} |\n");
        b.Append("\n## Parts\n\n| part | dimension | size | basis |\n|---|---|---|---|\n");
        foreach (var p in (JsonArray)s["parts"]!)
            foreach (var (k, v) in (JsonObject)p!["dims"]!) b.Append($"| {Cell(Str(p["name"]))} | {k} | {DimText(v)} | {Cell(Basis(v))} |\n");
        b.Append("\n## Shapes and cross-sections\n\n");
        foreach (var p in (JsonArray)s["parts"]!)
        {
            b.Append("- **").Append(Str(p!["name"])).Append("**");
            if (p["along"] is JsonArray al) b.Append($" (at {F(al[0]!.GetValue<double>())}–{F(al[1]!.GetValue<double>())} of the main photo)");
            b.Append(": section ").Append(Str(p["cross_section"]));
            if (Str(p["shape"]).Length > 0) b.Append("; ").Append(Str(p["shape"]));
            if (Str(p["photo_check"]).Length > 0) b.Append(". Photo check ").Append(Str(p["photo_check"]));
            b.Append('\n');
        }
        b.Append("\n## Materials\n\n| part | material | colour | metal | roughness | wear | colour from |\n|---|---|---|---|---|---|---|\n");
        foreach (var p in (JsonArray)s["parts"]!)
        {
            var m = p!["material"]!;
            b.Append($"| {Cell(Str(p["name"]))} | {Cell(Str(m["name"]))} | `{Str(m["hex"])}` | {(m["metal"]!.GetValue<bool>() ? "yes" : "no")} | {m["roughness"]} | {Cell(Str(m["wear"]))} | {Cell(Str(m["sampled_from"]))} |\n");
        }
        b.Append("\n## Build order\n\n");
        int i = 1;
        foreach (var n in (JsonArray)s["build_order"]!) b.Append(i++).Append(". ").Append(Str(n)).Append('\n');
        b.Append("\n## Reference photos\n\nPaths are relative to this folder. `.png` is what refimage reads; `.orig.*` is the download as it came.\n\n");
        foreach (var r in (JsonArray)s["refs"]!)
        {
            b.Append($"- `{Str(r!["file"])}`: **{Str(r["view"])}**{(r["main"]!.GetValue<bool>() ? " (main)" : "")}, {Str(r["size"])}. {Str(r["title"])}");
            if (Str(r["note"]).Length > 0) b.Append(". ").Append(Str(r["note"]));
            b.Append($"  \n  Licence: {Str(r["licence"])}. Credit: {Str(r["attribution"])}. Record: {Str(r["page"])}  \n  Image: {Str(r["url"])}\n");
        }
        var lines = (JsonArray)s["refimage"]!;
        b.Append("\n## refimage lines\n\n");
        if (lines.Count == 0) b.Append("None: the main photo is not a clean side/front/top profile (whole object, plain background). Place it by hand.\n");
        else
        {
            b.Append("The main photo, scaled so the object's long side is the overall length, centred on the origin, a little behind the model. Paths are absolute, so paste the line as the first line of build.txt.\n\n```\n");
            foreach (var l in lines) b.Append(Str(l!["absolute"]) is { Length: > 0 } a ? a : Str(l["line"])).Append("   # ").Append(Str(l["scale"])).Append('\n');
            b.Append("```\n");
        }
        var gaps = (JsonArray)s["gaps"]!;
        if (gaps.Count > 0) { b.Append("\n## Gaps\n\n"); foreach (var g in gaps) b.Append("- ").Append(Str(g)).Append('\n'); }
        b.Append("\n## Sources\n\n");
        foreach (var src in (JsonArray)s["sources"]!) b.Append("- ").Append(Str(src)).Append('\n');
        return b.ToString();
    }
}
