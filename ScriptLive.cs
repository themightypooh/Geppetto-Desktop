#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// A build script run as a live run: a revision per line that changes the document, written with
/// the same journal an AI run writes, so the cabinet watches it build and its pace, pause and
/// cancel controls work on it. `--script build.txt --live [--open [--idle MIN]] [--pace S] [--doc in.effigy] [--run DIR] [--out DIR]`.
/// `--open` keeps the run up after the script, taking more lines through <see cref="Inbox"/>.
/// </summary>
public static class ScriptLive
{
    public static string Runs => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/geppetto/research");

    public static int Run(string[] args)
    {
        string? script = null, doc = null, run = null, outDir = null;
        bool live = false, open = false;
        double pace = 0, idle = 30;
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(args[i] + " needs a value.");
            switch (args[i])
            {
                case "--live": live = true; break;
                case "--open": open = true; break;
                case "--pace":
                    pace = double.Parse(Value(), CultureInfo.InvariantCulture);
                    if (!double.IsFinite(pace) || pace < 0 || pace > 30) throw new ArgumentException("--pace is 0 to 30 seconds.");
                    break;
                case "--idle":
                    idle = double.Parse(Value(), CultureInfo.InvariantCulture);
                    if (!double.IsFinite(idle) || idle <= 0) throw new ArgumentException("--idle is minutes, more than 0.");
                    break;
                case "--doc": doc = Value(); break;
                case "--run": run = Value(); break;
                case "--out": outDir = Value(); break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal) || script is not null) throw new ArgumentException("Unknown script option: " + args[i]);
                    script = args[i]; break;
            }
        }
        if (script is null || !live) throw new ArgumentException("usage: --script build.txt --live [--open [--idle MIN]] [--pace S] [--doc in.effigy|-] [--run DIR] [--out DIR]");

        string text = File.ReadAllText(script);
        var studio = doc is not null && doc != "-" ? StudioCache.Load(doc) : new PartStudio();
        string baseDir = outDir ?? Path.GetDirectoryName(Path.GetFullPath(script)) ?? ".";
        string root = run is not null ? Path.GetFullPath(run) : NewRunDirectory(script);
        if (File.Exists(root) || (Directory.Exists(root) && Directory.GetFileSystemEntries(root).Length > 0))
            throw new ArgumentException("--run must be a new or empty directory: " + root);
        WriteMeta(root, script, text);

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        var result = Execute(studio, text, baseDir, root, pace, cancel, line => { Console.WriteLine(line); Console.Out.Flush(); }, open, idle);
        Console.WriteLine(result.Result);
        Console.Error.WriteLine($"live run {root}: {result.State}, {result.Revisions} revision(s)");
        return result.State == "completed" ? 0 : 1;
    }

    public sealed record Outcome(string State, long Revisions, BuildScript.Result Result, PartStudio Studio);

    /// <summary>The run itself, without the console: tests drive it with their own directory and controls.
    /// <paramref name="open"/>: when the script is done the run stays up with the model loaded, waiting for
    /// more lines in &lt;run&gt;/.live/inbox (see <see cref="Inbox"/>), so a change is a few lines on the same
    /// document, not a new run. A line that fails then holds the run there instead of ending it: the model
    /// goes back to the last good revision, and the lines after it wait for a fix (or `skip`).
    /// A <c>checkpoint "title" [focus=Body1,Body2] [vars=...]</c> line pauses the run at that stage
    /// (see <see cref="Drain"/>): it commits the current studio, publishes a <c>checkpoint</c> state,
    /// and blocks the lines after it until the cabinet or a tool continues it.</summary>
    public static Outcome Execute(PartStudio studio, string text, string baseDir, string root, double pace,
        CancellationTokenSource cancel, Action<string>? emit = null, bool open = false, double idleMinutes = 30)
    {
        var tools = new AiSuiteTools(root);
        var live = new AiLiveRun(tools, emit);
        var session = new AiLiveControl.Session(root, live, cancel, allowDirections: false, pace: pace, allowReview: open);
        using var stop = new CancellationTokenSource();
        var watch = session.WatchAsync(stop.Token);
        live.Commit(studio, "initial");
        string state = "completed";
        var result = new BuildScript.Result();     // one for the whole run: a pivot set early still counts at a later export
        // what is still to run, each piece with the script.txt line its first line sits after
        var pending = new LinkedList<(string Text, int Base)>();
        pending.AddLast((Normalise(text), 0));
        string all = Normalise(text);              // script.txt: every line the run was given, so `line n` is its line n
        string entry = Inbox.Start;
        int from = 0;
        try
        {
            while (true)
            {
                bool ok;
                try
                {
                    session.ReviewBoundary();
                    ok = Drain(ref studio, pending, result, baseDir, live, session, open, cancel.Token, root);
                }
                catch (AiLiveControl.ReviewRequestedException)
                {
                    // BeforeOperation is outside BuildScript's per-line catch. Keep
                    // the unexecuted tail; the next inbox edit runs ahead of it.
                    session.ReviewReady();
                    result.Ok = true; result.Error = null; result.ErrorLine = 0;
                    ok = true;
                }
                state = ok ? "completed" : "failed";
                if (!open) break;
                string phase = ok ? "open" : "held";
                Inbox.Answer(root, entry, result, from, ok, pending.Sum(p => Lines(p.Text)));
                live.Status(phase, outcome: ok ? "open for more lines" : $"line {result.ErrorLine}: {result.Error}");

                Inbox.Entry? next = null;
                bool StepCanRun() => pending.Count > 0 && session.StepPending;
                while (next is null && session.PauseRequested)
                {
                    next = Inbox.Next(root, cancel.Token, TimeSpan.FromSeconds(2), () =>
                    {
                        if (session.TakeReview()) session.ReviewReady();
                    }, StepCanRun);
                    if (next is null && StepCanRun()) break;
                }
                // STEP is an explicit allowance to run the preserved tail. Resume is
                // not: it must still wait for the corrective inbox edit below.
                if (next is null && StepCanRun()) continue;
                next ??= Inbox.Next(root, cancel.Token, TimeSpan.FromMinutes(idleMinutes), () =>
                {
                    if (session.TakeReview()) session.ReviewReady();
                }, StepCanRun);
                if (next is null)
                {
                    if (StepCanRun()) continue;
                    break;
                }
                entry = next.Value.Name;
                from = result.Log.Count;
                if (next.Value.Kind == "close") break;
                if (next.Value.Kind == "skip") { pending.Clear(); continue; }
                if (next.Value.Kind == "rewind")
                {
                    // po picked an earlier step: that revision becomes the model again (a new revision, so
                    // nothing is lost and the steps after it stay in the replay), and waiting lines are dropped
                    string? path = int.TryParse(next.Value.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int rev)
                        ? RevisionPath(root, rev) : null;
                    if (path is null) { result.Log.Add($"no step '{next.Value.Text.Trim()}' kept in this run: nothing rewound"); continue; }
                    studio = StudioCache.Load(path);
                    pending.Clear();
                    result.Ok = true; result.Error = null; result.ErrorLine = 0;
                    string mark = $"# rewound to step {rev}\n";
                    all += mark;
                    AiLiveRun.AtomicWrite(Path.Combine(root, "script.txt"), all);
                    live.Commit(studio, $"line {Lines(all)}: rewind to step {rev}");
                    session.MutationFinished();
                    result.Log.Add($"rewound to step {rev}: the model is as it was after that step; lines sent now build on it");
                    continue;
                }
                string piece = Normalise(next.Value.Text);
                pending.AddFirst((piece, Lines(all)));     // a fix goes in before the lines that were waiting on it
                all += piece;
                AiLiveRun.AtomicWrite(Path.Combine(root, "script.txt"), all);
            }
        }
        catch (OperationCanceledException)
        {
            result.Ok = false; result.Error = "cancelled";
            state = "cancelled";
        }
        finally { stop.Cancel(); try { watch.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } }

        if (state == "cancelled") live.Status("cancelled", outcome: "accepted");
        else if (state == "failed") live.Status("failed", outcome: $"line {result.ErrorLine}: {result.Error}");
        else session.TryClose("completed");
        if (open) Inbox.Answer(root, entry, result, result.Log.Count, state == "completed", 0, state);
        return new Outcome(state, live.Revision, result, studio);
    }

    /// <summary>Runs what is pending, in order. False when a line fails: the rest of its piece stays pending
    /// and, in an open run, the document goes back to the last revision so the half-made line is not in it.
    /// A checkpoint line (see <see cref="TryParseCheckpoint"/>) is not a build-script verb: Drain runs
    /// the lines before it, commits the studio as that checkpoint, publishes a <c>checkpoint</c> state
    /// the cabinet shows as a stage bar, and blocks the lines after it until it is continued (see
    /// <see cref="WaitForCheckpoint"/>). The lines after a checkpoint keep their original line numbers,
    /// so the chest's step list and script.txt still agree.</summary>
    /// <summary>A line that is a caption for the next one (echo) or nothing at all, never a step.</summary>
    static bool IsCaption(string verb) => string.IsNullOrEmpty(verb) || verb.Equals("echo", StringComparison.OrdinalIgnoreCase);

    static bool Drain(ref PartStudio studio, LinkedList<(string Text, int Base)> pending, BuildScript.Result result,
        string baseDir, AiLiveRun live, AiLiveControl.Session session, bool open, CancellationToken ct, string root)
    {
        while (pending.First is { } first)
        {
            var (text, at) = first.Value;
            var doc = studio;
            Action<int, string, string> before = (n, verb, line) =>
            {
                // Every line is a step the chest lists (po's rule: no step may go missing), so a
                // render, save, export or measure is announced like an add. Only an echo is not a
                // step of its own: it is the caption of the line after it. Pace and pause still
                // gate only the lines that change the model; the rest run at once.
                if (IsCaption(verb)) { ct.ThrowIfCancellationRequested(); return; }
                try { session.BoundaryAsync(ct, mutation: BuildScript.Mutates(verb)).GetAwaiter().GetResult(); }
                catch (AiLiveControl.ReviewRequestedException)
                {
                    first.Value = (string.Join("\n", text.Split('\n').Skip(n - 1)), at + n - 1);
                    throw;
                }
                live.Status("building", $"line {at + n}: {Clip(line)}");   // the whole line: the chest's tool wheel reads the tool from it
            };
            Action<int, string, string> after = (n, verb, line) =>
            {
                if (IsCaption(verb)) return;
                live.Commit(doc, $"line {at + n}: {Clip(line)}");
                if (BuildScript.Mutates(verb)) session.MutationFinished();
            };
            string[] lines = text.Split('\n');
            int cp = FindCheckpoint(lines);
            if (cp < 0)
            {
                BuildScript.Run(doc, text, baseDir, before, after, result);
                if (result.Ok) { pending.RemoveFirst(); continue; }
                int failed = result.ErrorLine;
                result.ErrorLine = at + failed;
                string rest = string.Join("\n", text.Split('\n').Skip(failed));
                if (string.IsNullOrWhiteSpace(rest.Replace("#", ""))) pending.RemoveFirst();
                else first.Value = (rest, at + failed);
                if (open && live.CurrentPath is { } last) studio = StudioCache.Load(last);
                return false;
            }
            // A checkpoint sits in this piece: run only the lines before it, then gate on it.
            string head = string.Join("\n", lines.Take(cp));
            if (!string.IsNullOrWhiteSpace(head))
            {
                BuildScript.Run(doc, head, baseDir, before, after, result);
                if (!result.Ok)
                {
                    // The checkpoint was not reached: the rest (checkpoint included) stays pending
                    // behind the failed line's fix, exactly like any other failed piece.
                    int failed = result.ErrorLine;
                    result.ErrorLine = at + failed;
                    string rest = string.Join("\n", lines.Skip(failed));
                    if (string.IsNullOrWhiteSpace(rest.Replace("#", ""))) pending.RemoveFirst();
                    else first.Value = (rest, at + failed);
                    if (open && live.CurrentPath is { } last) studio = StudioCache.Load(last);
                    return false;
                }
            }
            string tail = string.Join("\n", lines.Skip(cp + 1));
            if (!TryParseCheckpoint(lines[cp], out string title, out List<string> focus, out List<string> vars, out string? cpError))
            {
                result.Ok = false; result.Error = cpError; result.ErrorLine = at + cp + 1;
                if (string.IsNullOrWhiteSpace(tail.Replace("#", ""))) pending.RemoveFirst();
                else first.Value = (tail, at + cp + 1);
                if (open && live.CurrentPath is { } lastBad) studio = StudioCache.Load(lastBad);
                return false;
            }
            // The checkpoint is a revision of its own, so the cabinet's replay can land on it and
            // the focus bodies travel with it (AiLiveRun.Commit records them as affected ids).
            live.Commit(doc, $"checkpoint \"{title}\"", checkpoint: title, affected: focus.ToArray());
            // The outcome carries focus/vars for readers that only see the latest state (the
            // cabinet's run list): "awaiting-user focus=Body,Head vars=length,depth".
            string outcome = "awaiting-user"
                + (focus.Count > 0 ? " focus=" + string.Join(",", focus) : "")
                + (vars.Count > 0 ? " vars=" + string.Join(",", vars) : "");
            if (outcome.Length > 300) outcome = outcome[..300];
            live.Status("checkpoint", operation: title, outcome: outcome);
            CheckpointGate gate;
            try { gate = WaitForCheckpoint(root, live, session, title, ct); }
            catch (AiLiveControl.ReviewRequestedException)
            {
                if (string.IsNullOrWhiteSpace(tail)) pending.RemoveFirst();
                else first.Value = (tail, at + cp + 1);
                throw;
            }
            if (gate is CheckpointGate.Closed or CheckpointGate.Skip) { pending.Clear(); return true; }
            if (string.IsNullOrWhiteSpace(tail)) pending.RemoveFirst();
            else first.Value = (tail, at + cp + 1);
        }
        return true;
    }

    /// <summary>What unblocked a checkpoint: carry on with the lines after it, or drop them.</summary>
    enum CheckpointGate { Continue, Closed, Skip }

    /// <summary>The 0-based index of the first checkpoint line in a piece, or -1. A checkpoint line
    /// is a whole line (comments and blank lines are not checkpoints); its grammar is parsed by
    /// <see cref="TryParseCheckpoint"/>.</summary>
    static int FindCheckpoint(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            if (t.Equals("checkpoint", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("checkpoint ", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("checkpoint\t", StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary><c>checkpoint "Stage 1: Silhouette" [focus=Body,Head] [vars=length,depth]</c>.
    /// The title may also be unquoted words up to the first <c>key=value</c> token.</summary>
    static bool TryParseCheckpoint(string line, out string title, out List<string> focus, out List<string> vars, out string? error)
    {
        title = ""; focus = new(); vars = new(); error = null;
        string rest = line.Trim()["checkpoint".Length..].Trim();
        if (rest.Length == 0) { error = "checkpoint needs a title: checkpoint \"Stage 1: ...\" [focus=Body] [vars=...]"; return false; }
        if (rest[0] == '"')
        {
            int end = rest.IndexOf('"', 1);
            if (end < 0) { error = "checkpoint title is missing its closing quote"; return false; }
            title = rest[1..end];
            rest = rest[(end + 1)..].Trim();
        }
        else
        {
            var parts = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var titleWords = new List<string>();
            int j = 0;
            for (; j < parts.Length; j++) { if (parts[j].Contains('=')) break; titleWords.Add(parts[j]); }
            title = string.Join(" ", titleWords);
            rest = string.Join(" ", parts.Skip(j));
        }
        if (string.IsNullOrWhiteSpace(title)) { error = "checkpoint needs a title: checkpoint \"Stage 1: ...\" [focus=Body] [vars=...]"; return false; }
        foreach (string token in rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = token.IndexOf('=');
            if (eq <= 0) continue;
            string key = token[..eq].ToLowerInvariant();
            string val = token[(eq + 1)..];
            if (key == "focus") focus = val.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            else if (key == "vars") vars = val.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        }
        return true;
    }

    /// <summary>Blocks at a checkpoint until something continues it. Signals, in order:
    /// a <c>.continue</c> inbox file (the cabinet's continue_checkpoint, or a future
    /// <c>geppetto-live continue</c>); a <c>.close</c>/<c>.skip</c> inbox file (drop the lines
    /// after the checkpoint); a <c>.txt</c> inbox file left untouched for the open loop
    /// (<c>geppetto-live more</c>: its lines run after the checkpoint's remaining lines);
    /// an AiLiveControl <c>continue</c> command; or a cabinet direction whose text starts
    /// <c>CHECKPOINT-APPROVE</c>/<c>CHECKPOINT-REDO</c> (host.sendDirection, the only cabinet
    /// slot that reaches a run today). A continue note, and an approve/redo direction, are
    /// kept as a note on the run so the modeller behind it reads them in geppetto-notes.
    /// Cancellation (Ctrl-C) throws, as elsewhere in Drain.</summary>
    static CheckpointGate WaitForCheckpoint(string root, AiLiveRun live, AiLiveControl.Session session, string title, CancellationToken ct)
    {
        string inbox = Path.Combine(root, ".live/inbox");
        string commands = Path.Combine(root, ".live/commands");
        Directory.CreateDirectory(inbox);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            session.ReviewBoundary();
            if (session.TakeCheckpointContinue()) return CheckpointGate.Continue;
            if (Directory.Exists(inbox))
            {
                string? cont = Directory.EnumerateFiles(inbox, "*.continue")
                    .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal).FirstOrDefault();
                if (cont is not null)
                {
                    string note = "";
                    try { note = File.ReadAllText(cont); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    try { File.Move(cont, cont + ".taken", overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    if (!string.IsNullOrWhiteSpace(note)) AppendCheckpointNote(root, live, title, note.Trim());
                    return CheckpointGate.Continue;
                }
                if (Directory.EnumerateFiles(inbox, "*.close").Any()) return CheckpointGate.Closed;
                if (Directory.EnumerateFiles(inbox, "*.skip").Any()) return CheckpointGate.Skip;
                if (Directory.EnumerateFiles(inbox, "*.txt").Any()) return CheckpointGate.Continue;
            }
            if (Directory.Exists(commands))
            {
                foreach (string path in Directory.EnumerateFiles(commands, "*.json").OrderBy(p => p, StringComparer.Ordinal))
                {
                    JsonObject? cmd = null;
                    try { cmd = JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { continue; }
                    if (cmd is null || cmd["command"]?.GetValue<string>() != "instruct") continue;
                    if (cmd["runId"]?.GetValue<string>() != live.RunId) continue;
                    string text = cmd["text"]?.GetValue<string>() ?? "";
                    string t = text.Trim();
                    if (t.StartsWith("CHECKPOINT-APPROVE", StringComparison.OrdinalIgnoreCase)
                        || t.StartsWith("CHECKPOINT-REDO", StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Move(path, path + ".taken", overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                        AppendCheckpointNote(root, live, title, text.Trim());
                        return CheckpointGate.Continue;
                    }
                }
            }
            ct.WaitHandle.WaitOne(100);
        }
    }

    /// <summary>One notes.jsonl entry in the shape live_runs.add_note writes, so geppetto-notes
    /// prints it: time, run folder, revision, the checkpoint as its op, the text, unread.</summary>
    static void AppendCheckpointNote(string root, AiLiveRun live, string title, string text)
    {
        try
        {
            var note = new JsonObject {
                ["time"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                ["run"] = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                ["rev"] = live.Revision, ["op"] = $"checkpoint \"{title}\"", ["text"] = text,
                ["script"] = "", ["prompt"] = "", ["read"] = false
            };
            using var stream = new FileStream(Path.Combine(root, "notes.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(note.ToJsonString() + "\n");
            stream.Write(bytes); stream.Flush(flushToDisk: true);
        }
        catch (IOException) { /* A lost gate note must not lose the continue itself. */ }
        catch (UnauthorizedAccessException) { }
    }

    static string Normalise(string text)
    {
        text = text.Replace("\r\n", "\n");
        return text.Length == 0 || text.EndsWith('\n') ? text : text + "\n";
    }

    static int Lines(string text) => text.Count(c => c == '\n');

    static string Clip(string line) => line.Length <= 80 ? line : line[..77] + "...";

    static string NewRunDirectory(string script)
    {
        string stem = Path.GetFileNameWithoutExtension(script);
        string name = $"script-{stem}-{DateTime.Now:yyyyMMdd-HHmmss}";
        string path = Path.Combine(Runs, name);
        // claim the name by creating its .json (CreateNew fails if it exists): two runs started in the
        // same second get -2, -3 instead of sharing one folder and overwriting each other
        Directory.CreateDirectory(Runs);
        for (int k = 2; ; k++)
        {
            if (!Directory.Exists(path))
                try { new FileStream(path + ".json", FileMode.CreateNew).Dispose(); return path; }
                catch (IOException) { }
            path = Path.Combine(Runs, $"{name}-{k}");
        }
    }

    /// <summary>The run's listing entry: the cabinet reads the prompt from &lt;run&gt;.json beside the folder,
    /// and the pid, so a run that was killed stops counting as live at once.</summary>
    /// <summary>The kept .effigy of revision <paramref name="rev"/>, or null.</summary>
    static string? RevisionPath(string root, int rev)
    {
        string folder = Path.Combine(root, ".live/revisions");
        if (rev < 1 || !Directory.Exists(folder)) return null;
        string prefix = rev.ToString("D8", CultureInfo.InvariantCulture) + "-";
        return Directory.EnumerateFiles(folder, prefix + "*.effigy").FirstOrDefault();
    }

    static void WriteMeta(string root, string script, string text)
    {
        string parent = Path.GetDirectoryName(root)!;
        Directory.CreateDirectory(parent);
        string meta = root + ".json";
        if (!File.Exists(meta) || new FileInfo(meta).Length == 0)    // empty: the name NewRunDirectory claimed
            AiLiveRun.AtomicWrite(meta, new JsonObject { ["prompt"] = "script: " + Path.GetFileName(script), ["script"] = Path.GetFullPath(script), ["pid"] = Environment.ProcessId }.ToJsonString());
        // the script as it ran, so a replay shows the very line each step came from after build.txt is edited
        Directory.CreateDirectory(root);
        string copy = Path.Combine(root, "script.txt");
        if (!File.Exists(copy)) AiLiveRun.AtomicWrite(copy, text);
    }
}

/// <summary>
/// How an open script run takes more: files in &lt;run&gt;/.live/inbox, taken oldest name first.
/// `NAME.txt` is more script lines, `NAME.skip` drops the lines held behind a failed one, `NAME.close`
/// ends the run. Writers write `NAME.ext.tmp` and rename it, so a half-written file is never read.
/// The run answers each in &lt;run&gt;/.live/answers/NAME.txt: what the lines logged, the error if one
/// failed, and where the run is now. `geppetto-live more|skip|close RUN` writes one and prints the answer.
/// </summary>
public static class Inbox
{
    /// <summary>The answer name for the script the run started with.</summary>
    public const string Start = "start";

    public readonly record struct Entry(string Name, string Kind, string Text);

    static string Folder(string root) => Path.Combine(root, ".live/inbox");

    /// <summary>Puts a request in (tests and tools; geppetto-live writes the same files from the shell).</summary>
    public static string Put(string root, string kind, string text = "")
    {
        Directory.CreateDirectory(Folder(root));
        string name = DateTime.UtcNow.Ticks.ToString("D19", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N")[..6];
        string path = Path.Combine(Folder(root), name + "." + kind);
        File.WriteAllText(path + ".tmp", text);
        File.Move(path + ".tmp", path);
        return name;
    }

    /// <summary>Waits for the next request; null on idle timeout or an interrupt, without consuming an entry.</summary>
    public static Entry? Next(string root, CancellationToken ct, TimeSpan idle, Action? idleTick = null, Func<bool>? interrupt = null)
    {
        string folder = Folder(root);
        Directory.CreateDirectory(folder);
        var until = DateTime.UtcNow + idle;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            idleTick?.Invoke();
            string? path = Directory.EnumerateFiles(folder)
                .Where(p => Path.GetExtension(p) is ".txt" or ".skip" or ".close" or ".rewind")
                .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal).FirstOrDefault();
            if (path is not null)
            {
                string text = File.ReadAllText(path);
                File.Move(path, path + ".taken", overwrite: true);
                return new Entry(Path.GetFileNameWithoutExtension(path), Path.GetExtension(path)[1..], text);
            }
            // Durable edits have priority over a STEP wake, so a queued correction
            // remains ahead of the preserved tail. A wake never consumes the allowance.
            if (interrupt?.Invoke() == true) return null;
            ct.WaitHandle.WaitOne(100);
        }
        return null;
    }

    /// <summary>The answer to one request: its log lines, the error, and where the run stands.</summary>
    public static void Answer(string root, string name, BuildScript.Result result, int from, bool ok, int waiting, string? closed = null)
    {
        var text = new System.Text.StringBuilder();
        foreach (string line in result.Log.Skip(from)) text.AppendLine(line);
        if (!ok && result.Error is not null && result.Error != "cancelled") text.AppendLine($"ERROR line {result.ErrorLine}: {result.Error}");
        if (closed is not null)
            text.AppendLine($"run {closed}: it is closed, nothing more goes into it.");
        else if (ok)
            text.AppendLine($"run is open with the model loaded: change it with `geppetto-live more {root} edit.txt`, `geppetto-live close {root}` when done.");
        else
            text.AppendLine($"run is held at line {result.ErrorLine}: the model is back at its last good revision"
                + (waiting > 0 ? $" and {waiting} line(s) after the bad one are waiting" : "")
                + $". Send the fixed line(s) with `geppetto-live more {root} fix.txt`"
                + (waiting > 0 ? $" (the waiting lines run after them), or `geppetto-live skip {root}` to drop the waiting lines." : "."));
        string folder = Path.Combine(root, ".live/answers");
        Directory.CreateDirectory(folder);
        AiLiveRun.AtomicWrite(Path.Combine(folder, name + ".txt"), text.ToString());
    }
}
