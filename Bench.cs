#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// --bench DIR [--doc in.effigy|-] [--from TEXT]: your own hands on a model. The cabinet's tool wheel,
/// opened by hand, sends each tool here as a build-script line; the bench applies it to its own copy
/// of the document and publishes a revision with the same journal a live run writes, so the viewport
/// follows it and the run card scrubs it like any run.
///
/// WHY A COPY. Editing a step you scrubbed to in someone's run must not touch that run: its revisions,
/// script and journal stay exactly as they were. The bench starts from a copy of that revision
/// (`--doc`) in its own directory; the run never learns the bench exists.
///
/// stdin, one per line:  apply LINE | undo | place DOC X Y Z | describe KEY | state | quit
/// stdout: '@' + JSON events: ready, applied, failed, undone, describe, state.
/// </summary>
public static class Bench
{
    public static int Run(string[] args)
    {
        string? dir = null, doc = null, from = null;
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(args[i] + " needs a value.");
            switch (args[i])
            {
                case "--doc": doc = Value(); break;
                case "--from": from = Value(); break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal) || dir is not null) throw new ArgumentException("Unknown bench option: " + args[i]);
                    dir = args[i]; break;
            }
        }
        if (dir is null) throw new ArgumentException("usage: --bench DIR [--doc in.effigy|-] [--from TEXT]");

        string root = Path.GetFullPath(dir);
        if (File.Exists(root) || (Directory.Exists(root) && Directory.GetFileSystemEntries(root).Length > 0))
            throw new ArgumentException("the bench needs a new or empty directory: " + root);
        var studio = doc is not null && doc != "-" ? StudioCache.Load(doc) : new PartStudio();
        var bench = new Session(root, studio, from ?? (doc is null || doc == "-" ? "empty" : doc), Emit);

        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line == "quit") break;
            try { bench.Command(line); }
            catch (Exception e) { Emit(new JsonObject { ["ev"] = "failed", ["line"] = line, ["error"] = e.Message }); }
        }
        bench.Close();
        return 0;
    }

    static readonly object OutLock = new();
    static void Emit(JsonObject ev)
    {
        lock (OutLock) { Console.Out.Write("@" + ev.ToJsonString() + "\n"); Console.Out.Flush(); }
    }

    /// <summary>The bench without the console: tests drive it directly.</summary>
    public sealed class Session
    {
        readonly string root;
        PartStudio studio;
        readonly AiLiveRun live;
        readonly Action<JsonObject> emit;
        readonly List<string> script = new();
        // the revision file after each applied line, newest last; [0] is the starting document
        readonly List<string> history = new();
        readonly List<string> applied = new();      // the line behind each history entry after [0]

        public Session(string root, PartStudio studio, string from, Action<JsonObject> emit)
        {
            this.root = root; this.studio = studio; this.emit = emit;
            WriteMeta(root, from);
            live = new AiLiveRun(new AiSuiteTools(root));
            live.Commit(studio, "start: " + from);
            history.Add(live.CurrentPath!);
            live.Status("building", "bench");
            script.Add("# bench from " + from);
            SaveScript();
            emit(State("ready"));
        }

        public PartStudio Studio => studio;
        public long Revision => live.Revision;
        public IReadOnlyList<string> Script => script;

        public void Command(string line)
        {
            int space = line.IndexOf(' ');
            string verb = space < 0 ? line : line[..space], rest = space < 0 ? "" : line[(space + 1)..].Trim();
            switch (verb)
            {
                case "apply": Apply(rest); break;
                case "undo": Undo(); break;
                case "facematerial": FaceMaterial(rest); break;
                case "place": Place(rest); break;
                case "describe": emit(Describe(rest)); break;
                case "state": emit(State("state")); break;
                default: emit(new JsonObject { ["ev"] = "failed", ["line"] = line, ["error"] = "unknown bench command: " + verb }); break;
            }
        }

        /// <summary>Run one build-script line on the bench's document. A line that fails leaves the
        /// document as it was: the bench reloads the last good revision rather than keep a half-made feature.</summary>
        public bool Apply(string line)
        {
            // the cabinet sends everything as `apply`; slot assignment is a bench edit, not a script verb
            if (line.StartsWith("facematerial ", StringComparison.Ordinal)) return FaceMaterial(line[13..].Trim());
            if (string.IsNullOrWhiteSpace(line) || line.Contains('\n'))
            {
                emit(new JsonObject { ["ev"] = "failed", ["line"] = line, ["error"] = "one script line, please" });
                return false;
            }
            var result = BuildScript.Run(studio, line, root);
            string verb = BuildScript.VerbOf(line) ?? "";
            if (!result.Ok)
            {
                Restore(history[^1]);
                emit(new JsonObject { ["ev"] = "failed", ["line"] = line, ["error"] = result.Error ?? "failed" });
                return false;
            }
            var ev = new JsonObject { ["ev"] = "applied", ["line"] = line, ["log"] = string.Join("\n", result.Log) };
            if (BuildScript.Mutates(verb))
            {
                script.Add(line);
                SaveScript();
                live.Commit(studio, $"line {script.Count}: {Clip(line)}");
                history.Add(live.CurrentPath!);
                applied.Add(line);
                ev["revision"] = live.Revision;
            }
            ev["state"] = State("state");
            emit(ev);
            return true;
        }

        /// <summary>Step back one applied line: the document before it becomes a new revision, so the
        /// journal only ever grows and a replay still shows what was undone.</summary>
        public bool Undo()
        {
            if (history.Count < 2)
            {
                emit(new JsonObject { ["ev"] = "failed", ["line"] = "undo", ["error"] = "nothing to undo" });
                return false;
            }
            history.RemoveAt(history.Count - 1);
            string undone = applied[^1];
            applied.RemoveAt(applied.Count - 1);
            Restore(history[^1]);
            script.Add("# undo: " + undone);
            SaveScript();
            live.Commit(studio, $"line {script.Count}: undo {Clip(undone)}");
            emit(new JsonObject { ["ev"] = "undone", ["line"] = undone, ["revision"] = live.Revision, ["state"] = State("state") });
            return true;
        }

        /// <summary>`place DOC X Y Z`: another model dropped into this one, standing on the point it was
        /// dropped on. DOC's built bodies are written as one OBJ beside the bench, their bottom centre
        /// moved to X,Y,Z, and come in through `add Import`, so the drop is one revision, one undo
        /// step and a line that replays. Its material slots follow this document's (slot 0 stays 0)
        /// and keep their looks and .vmat names, so it lands looking the way it did.</summary>
        public bool Place(string args)
        {
            string line = ("place " + args).Trim();
            bool Fail(string why)
            {
                Restore(history[^1]);
                emit(new JsonObject { ["ev"] = "failed", ["line"] = line, ["error"] = why });
                return false;
            }
            var words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 4) return Fail("place needs [name=N] DOC X Y Z");
            var at = new float[3];
            for (int i = 0; i < 3; i++)
                if (!float.TryParse(words[words.Length - 3 + i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out at[i]))
                    return Fail("place: bad coordinate " + words[words.Length - 3 + i]);
            // place [name=N] [size=S] DOC X Y Z: S is a readable size where it lands
            string label = ""; float size = 0; int skip = 0;
            for (; skip < words.Length - 3; skip++)
            {
                if (words[skip].StartsWith("name=")) label = words[skip][5..];
                else if (words[skip].StartsWith("size=")) float.TryParse(words[skip][5..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out size);
                else break;
            }
            string doc = string.Join(" ", words.Skip(skip).Take(words.Length - 3 - skip));
            if (!File.Exists(doc)) return Fail("place: no such model " + doc);

            var src = StudioCache.Load(doc);
            var bodies = src.Bodies.Where(b => b.Mesh is { FaceCount: > 0 } && b.Visible && !b.IsReference).ToList();
            if (bodies.Count == 0) return Fail("place: " + Path.GetFileName(doc) + " has nothing built to drop in");

            // its slots after ours: the next free number up, so nothing of this model changes colour
            var used = new HashSet<int>(studio.MaterialNames.Keys.Concat(studio.MaterialLooks.Keys).Concat(studio.MaterialScales.Keys));
            foreach (var b in studio.Bodies)
                if (b.Mesh is not null) foreach (var f in b.Mesh.Faces) used.Add(f.Material);
            int next = used.Count == 0 ? 1 : Math.Max(1, used.Max() + 1);
            var slots = new Dictionary<int, int> { [0] = 0 };
            foreach (var b in bodies)
                foreach (var f in b.Mesh.Faces)
                    if (!slots.ContainsKey(f.Material)) slots[f.Material] = next++;

            // stand it on the drop point: bottom centre of everything it built
            Vec3 lo = new(float.MaxValue, float.MaxValue, float.MaxValue), hi = new(float.MinValue, float.MinValue, float.MinValue);
            foreach (var b in bodies)
                foreach (var v in b.Mesh.Positions)
                {
                    lo = new Vec3(Math.Min(lo.x, v.x), Math.Min(lo.y, v.y), Math.Min(lo.z, v.z));
                    hi = new Vec3(Math.Max(hi.x, v.x), Math.Max(hi.y, v.y), Math.Max(hi.z, v.z));
                }
            // models come in all scales: a speck or a thing swallowing the view is brought to the size where it lands
            float extent = Math.Max(hi.x - lo.x, Math.Max(hi.y - lo.y, hi.z - lo.z)), fit = 1f;
            if (size > 0 && extent > 1e-6f && (extent < size * 0.35f || extent > size * 3f)) fit = size / extent;
            var foot = new Vec3((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, lo.z);
            var pieces = new List<ObjReader.ObjPiece>();
            foreach (var b in bodies)
            {
                var m = b.Mesh.Clone();
                for (int i = 0; i < m.Positions.Count; i++)
                {
                    var q = m.Positions[i];
                    m.Positions[i] = new Vec3((q.x - foot.x) * fit + at[0], (q.y - foot.y) * fit + at[1], (q.z - foot.z) * fit + at[2]);
                }
                foreach (var f in m.Faces) f.Material = slots[f.Material];
                pieces.Add(new ObjReader.ObjPiece(b.Name ?? b.Id, m));
            }
            string stem = new string((label.Length > 0 ? label : Path.GetFileNameWithoutExtension(doc)).Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            if (stem.Length == 0) stem = "model";
            string dir = Path.Combine(root, "placed");
            Directory.CreateDirectory(dir);
            string obj = Path.Combine(dir, stem + ".obj");
            for (int k = 2; File.Exists(obj); k++) obj = Path.Combine(dir, $"{stem}-{k}.obj");
            ObjWriter.WriteFile(pieces, obj);

            var looks = new List<string>();
            foreach (var (from, to) in slots)
            {
                if (from == 0) continue;
                if (src.MaterialNames.TryGetValue(from, out var vmat) && !string.IsNullOrWhiteSpace(vmat)) studio.MaterialNames[to] = vmat;
                if (src.MaterialScales.TryGetValue(from, out var scale)) studio.MaterialScales[to] = scale;
                if (src.MaterialLooks.TryGetValue(from, out var look))
                {
                    studio.MaterialLooks[to] = look.Clone();
                    looks.Add($"material {to} color={Hex(look.Color)} rough={F(look.Rough)} metal={F(look.Metal)}" + (look.Glows ? " emit=" + Hex(look.Emit) : ""));
                }
            }
            string name = new string(stem.Where(char.IsLetterOrDigit).ToArray());
            if (!Apply($"add Import name={(name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : "Placed")} source={obj}"))
                return false;
            if (looks.Count > 0) { script.AddRange(looks); SaveScript(); }   // so a replay of script.txt gets the colours too
            return true;
        }

        static string F(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        static string Hex(Vec3 c) => "#" + string.Concat(new[] { c.x, c.y, c.z }.Select(v => ((int)Math.Round(Math.Clamp(v, 0f, 1f) * 255)).ToString("x2")));

        /// <summary>`facematerial slot=N faces=Body:x,y,z:nx,ny,nz;…`: the selected faces onto slot N, the
        /// way s&box's face menu does it: one FaceMaterialEdit.Assign per picked face (slot 0 detaches),
        /// faces already on N skipped. Every face is resolved before anything changes, so a bad one fails
        /// the whole line; the edit is one revision and one undo step. The line is also a build-script verb
        /// (BuildScript `facematerial`), so a saved bench script.txt replays it.</summary>
        public bool FaceMaterial(string args)
        {
            string line = ("facematerial " + args).Trim();
            bool Fail(string why)
            {
                Restore(history[^1]);
                emit(new JsonObject { ["ev"] = "failed", ["line"] = line, ["error"] = why });
                return false;
            }
            string? slotText = null, faces = null;
            foreach (var tok in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (tok.StartsWith("slot=", StringComparison.Ordinal)) slotText = tok[5..];
                else if (tok.StartsWith("faces=", StringComparison.Ordinal)) faces = tok[6..];
                else return Fail($"facematerial: unexpected '{tok}'. Use: facematerial slot=N faces=Body:x,y,z:nx,ny,nz;…");
            }
            if (slotText is null || !int.TryParse(slotText, out int slot) || slot < 0)
                return Fail("facematerial needs slot=N (0 or more; 0 takes the faces off their slot).");
            if (string.IsNullOrWhiteSpace(faces))
                return Fail("facematerial needs faces=Body:x,y,z:nx,ny,nz;… (select faces first).");

            // the same code the build script's `facematerial` verb runs, so the line saved in script.txt
            // replays to the same FaceMaterial features (BuildScript.AssignFaceSlots, rebuilds when changed)
            if (!BuildScript.AssignFaceSlots(studio, slot, faces, out int changed, out int already, out var why))
                return Fail(why ?? "facematerial: bad faces");
            var ev = new JsonObject { ["ev"] = "applied", ["line"] = line };
            if (changed == 0)
            {
                Restore(history[^1]);
                ev["log"] = $"{already} face(s) already on {studio.NameForSlot(slot)}";
            }
            else
            {
                ev["log"] = $"{changed} face(s) -> {studio.NameForSlot(slot)}" + (already > 0 ? $" ({already} already there)" : "");
                script.Add(line);
                SaveScript();
                live.Commit(studio, $"line {script.Count}: {Clip(line)}");
                history.Add(live.CurrentPath!);
                applied.Add(line);
                ev["revision"] = live.Revision;
            }
            ev["state"] = State("state");
            emit(ev);
            return true;
        }

        public void Close() => live.Status("completed", outcome: "bench closed");

        // the last good revision, read back as it was saved: nothing of a failed or undone line survives
        void Restore(string path) => studio = StudioCache.Load(path);

        JsonObject State(string ev) => new()
        {
            ["ev"] = ev, ["dir"] = root, ["revision"] = live.Revision, ["canUndo"] = history.Count > 1,
            ["bodies"] = new JsonArray(studio.Bodies.Select(b => (JsonNode?)JsonValue.Create(b.Name ?? b.Id)).Distinct().ToArray()),
            ["features"] = new JsonArray(studio.Features.Select(f => (JsonNode?)JsonValue.Create(f.Name ?? f.Id)).ToArray()),
            ["slots"] = Slots(studio),
            ["presets"] = Presets(),
        };

        void SaveScript() => AiLiveRun.AtomicWrite(Path.Combine(root, "script.txt"), string.Join("\n", script) + "\n");

        static string Clip(string line) => line.Length <= 80 ? line : line[..77] + "...";

        static void WriteMeta(string root, string from)
        {
            Directory.CreateDirectory(root);
            string meta = root + ".json";
            if (!File.Exists(meta))
                AiLiveRun.AtomicWrite(meta, new JsonObject { ["prompt"] = "bench: " + from, ["bench"] = true,
                    ["script"] = Path.Combine(root, "script.txt"), ["pid"] = Environment.ProcessId }.ToJsonString());
        }
    }

    // ---- the material browser ----------------------------------------------------------------

    /// <summary>Every slot the model uses or has a look for, with its look, its .vmat and the bodies on
    /// it; and each body's own feature when that feature takes a slot (`set Box1 material=2` moves it).</summary>
    public static JsonArray Slots(PartStudio studio)
    {
        var onSlot = new SortedDictionary<int, List<string>>();
        foreach (var b in studio.Bodies)
            foreach (int m in b.Mesh.Faces.Select(f => f.Material).Distinct())
                (onSlot.TryGetValue(m, out var l) ? l : onSlot[m] = new()).Add(b.Name ?? b.Id);
        foreach (int k in studio.MaterialLooks.Keys.Concat(studio.MaterialNames.Keys))
            if (!onSlot.ContainsKey(k)) onSlot[k] = new();
        var slots = new JsonArray();
        foreach (var (slot, bodies) in onSlot)
        {
            var look = studio.MaterialLooks.GetValueOrDefault(slot);
            var o = new JsonObject { ["slot"] = slot, ["path"] = studio.NameForSlot(slot), ["hasLook"] = look is not null };
            Look(o, look ?? new SlotLook());
            o["bodies"] = new JsonArray(bodies.Select(b => new JsonObject { ["name"] = b, ["feature"] = SlotFeature(studio, b) }).ToArray<JsonNode?>());
            slots.Add(o);
        }
        return slots;
    }

    // the feature a body came from, if it has a slot to set; else null (its faces need picking)
    static string? SlotFeature(PartStudio studio, string body)
    {
        var f = studio.Features.LastOrDefault(x => (x.Name ?? x.Id) == body);
        return f is not null && f.Parameters.Any(p => p.Label == "Material slot") ? f.Name ?? f.Id : null;
    }

    public static JsonArray Presets() => new(SlotLook.Presets.Select(kv => (JsonNode?)Look(new JsonObject { ["name"] = kv.Key }, kv.Value)).ToArray());

    static JsonObject Look(JsonObject o, SlotLook l)
    {
        o["color"] = Hex(l.Color); o["rough"] = Math.Round(l.Rough, 3); o["metal"] = Math.Round(l.Metal, 3);
        o["emit"] = l.Glows ? Hex(l.Emit) : ""; o["preset"] = l.Preset ?? "";
        return o;
    }

    static string Hex(Vec3 c) => "#" + string.Concat(new[] { c.x, c.y, c.z }.Select(v => ((int)Math.Round(Math.Clamp(v, 0f, 1f) * 255)).ToString("x2")));

    // ---- what a tool takes ---------------------------------------------------------------------

    /// <summary>The fields a wheel tool takes, with their defaults, for the cabinet to build a form from.
    /// KEY is a wheel key: a feature type ("primitive"), an edit operation ("edit:bevel") or a script verb
    /// ("set"); a verb takes one free line of arguments.</summary>
    public static JsonObject Describe(string key)
    {
        var ev = new JsonObject { ["ev"] = "describe", ["key"] = key };
        var fields = new JsonArray();
        ev["fields"] = fields;
        if (key.StartsWith("edit:", StringComparison.Ordinal))
        {
            string op = key[5..];
            int index = Array.FindIndex(EditOpFeature.Operations, o => Norm(o) == op);
            if (index < 0) { ev["error"] = "no edit operation " + op; return ev; }
            ev["kind"] = "edit";
            ev["verb"] = "edit";
            ev["op"] = Norm(EditOpFeature.Operations[index]);
            var feature = new EditOpFeature();
            foreach (var p in feature.Parameters)
                if (p != feature.Op) fields.Add(Field(p));
            PutAccepts(ev, feature);
            return ev;
        }
        var made = BuildScript.CreateFeature(key);
        // `facematerial` is both a feature (the wheel's FACE MATERIAL tool) and the bench's slot verb; the wheel means the feature
        if (made is not null && (!IsScriptVerb(key) || key == "facematerial"))
        {
            ev["kind"] = "add";
            ev["verb"] = "add";
            ev["type"] = made.GetType().Name.EndsWith("Feature") ? made.GetType().Name[..^7] : made.GetType().Name;
            foreach (var p in made.Parameters.Concat(made.AdvancedParameters).Distinct())
                fields.Add(Field(p));
            foreach (var f in made.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.GetValue(made) is IParam || f.Name is "Id" or "Name" or "Suppressed" or "Visible") continue;
                string? kind = f.FieldType == typeof(List<Vec3>) ? "points" : f.FieldType == typeof(List<Vec2>) ? "pairs"
                    : f.FieldType == typeof(string) ? "feature" : f.FieldType == typeof(List<string>) ? "features"
                    : f.FieldType == typeof(List<FaceRef>) ? "faces" : f.FieldType == typeof(List<EdgeRef>) ? "edges" : null;
                if (kind is not null) fields.Add(new JsonObject { ["key"] = Norm(f.Name), ["label"] = f.Name, ["kind"] = kind, ["value"] = "" });
            }
            fields.Add(new JsonObject { ["key"] = "name", ["label"] = "Name", ["kind"] = "text", ["value"] = "" });
            PutAccepts(ev, made);
            return ev;
        }
        // a script verb: set, remove, measure, render ... one line of arguments, as the script takes them
        ev["kind"] = "verb";
        ev["verb"] = key;
        fields.Add(new JsonObject { ["key"] = "", ["label"] = "Arguments", ["kind"] = "text", ["value"] = "" });
        return ev;
    }

    // A script verb is a name the script dispatches, not anything Mutates() gates: Mutates is fail-closed for STEP
    // pacing, so every unknown word (and every feature type such as primitive) counts as mutating there.
    static bool IsScriptVerb(string key) =>
        Array.Exists(BuildScript.VerbNames, v => string.Equals(v, key, StringComparison.OrdinalIgnoreCase))
        || Array.Exists(BuildScript.RigVerbNames, v => string.Equals(v, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>What a click on the model gives this tool. The cabinet leaves body mode for face or edge
    /// so the thing under the cursor lights up; a verb takes no geometry.</summary>
    static void PutAccepts(JsonObject ev, Feature feature)
    {
        var kinds = new JsonArray();
        var a = feature.Accepts;
        if ((a & GeometryKind.Face) != 0) kinds.Add(JsonValue.Create("face"));
        if ((a & GeometryKind.Edge) != 0) kinds.Add(JsonValue.Create("edge"));
        if ((a & GeometryKind.Body) != 0) kinds.Add(JsonValue.Create("body"));
        if ((a & GeometryKind.SketchRegion) != 0) kinds.Add(JsonValue.Create("sketch"));
        ev["accepts"] = kinds;
    }

    static JsonObject Field(IParam p)
    {
        var o = new JsonObject { ["key"] = Norm(p.Label), ["label"] = p.Label };
        switch (p)
        {
            case FloatParam f:
                o["kind"] = "number"; o["value"] = f.Value;
                if (f.Min > float.MinValue) o["min"] = f.Min;
                if (f.Max < float.MaxValue) o["max"] = f.Max;
                break;
            case IntParam i: o["kind"] = "int"; o["value"] = i.Value; o["min"] = i.Min; o["max"] = i.Max; break;
            case BoolParam b: o["kind"] = "bool"; o["value"] = b.Value; break;
            case ChoiceParam c:
                o["kind"] = "choice"; o["value"] = c.Value;
                o["options"] = new JsonArray(c.Options.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
                break;
            case Vec3Param v: o["kind"] = "point"; o["value"] = $"{v.Value.x:0.###},{v.Value.y:0.###},{v.Value.z:0.###}"; break;
            case BodySelectionParam: o["kind"] = "bodies"; o["value"] = ""; break;
            case StringParam s: o["kind"] = "text"; o["value"] = s.Value ?? ""; break;
            default: o["kind"] = "text"; o["value"] = ""; break;
        }
        return o;
    }

    static string Norm(string s) => (s ?? "").ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("'", "");
}
