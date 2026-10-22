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
    /// goes back to the last good revision, and the lines after it wait for a fix (or `skip`).</summary>
    public static Outcome Execute(PartStudio studio, string text, string baseDir, string root, double pace,
        CancellationTokenSource cancel, Action<string>? emit = null, bool open = false, double idleMinutes = 30)
    {
        var tools = new AiSuiteTools(root);
        var live = new AiLiveRun(tools, emit);
        var session = new AiLiveControl.Session(root, live, cancel, allowDirections: false, pace: pace);
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
                bool ok = Drain(ref studio, pending, result, baseDir, live, session, open, cancel.Token);
                state = ok ? "completed" : "failed";
                if (!open) break;
                string phase = ok ? "open" : "held";
                Inbox.Answer(root, entry, result, from, ok, pending.Sum(p => Lines(p.Text)));
                live.Status(phase, outcome: ok ? "open for more lines" : $"line {result.ErrorLine}: {result.Error}");

                var next = Inbox.Next(root, cancel.Token, TimeSpan.FromMinutes(idleMinutes));
                if (next is null) break;
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
    /// and, in an open run, the document goes back to the last revision so the half-made line is not in it.</summary>
    static bool Drain(ref PartStudio studio, LinkedList<(string Text, int Base)> pending, BuildScript.Result result,
        string baseDir, AiLiveRun live, AiLiveControl.Session session, bool open, CancellationToken ct)
    {
        while (pending.First is { } first)
        {
            var (text, at) = first.Value;
            var doc = studio;
            BuildScript.Run(doc, text, baseDir,
                (n, verb, line) =>
                {
                    // Pace and pause only gate the lines that change the model; a render or a
                    // measure between them runs at once.
                    if (!BuildScript.Mutates(verb)) { ct.ThrowIfCancellationRequested(); return; }
                    session.BoundaryAsync(ct, mutation: true).GetAwaiter().GetResult();
                    live.Status("building", $"line {at + n}: {Clip(line)}");   // the whole line: the chest's tool wheel reads the tool from it
                },
                (n, verb, line) =>
                {
                    if (!BuildScript.Mutates(verb)) return;
                    live.Commit(doc, $"line {at + n}: {Clip(line)}");
                    session.MutationFinished();
                }, result);
            if (result.Ok) { pending.RemoveFirst(); continue; }
            int failed = result.ErrorLine;
            result.ErrorLine = at + failed;
            string rest = string.Join("\n", text.Split('\n').Skip(failed));
            if (string.IsNullOrWhiteSpace(rest.Replace("#", ""))) pending.RemoveFirst();
            else first.Value = (rest, at + failed);
            if (open && live.CurrentPath is { } last) studio = StudioCache.Load(last);
            return false;
        }
        return true;
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

    /// <summary>Waits for the next request; null when nothing came for <paramref name="idle"/>.</summary>
    public static Entry? Next(string root, CancellationToken ct, TimeSpan idle)
    {
        string folder = Folder(root);
        Directory.CreateDirectory(folder);
        var until = DateTime.UtcNow + idle;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            string? path = Directory.EnumerateFiles(folder)
                .Where(p => Path.GetExtension(p) is ".txt" or ".skip" or ".close" or ".rewind")
                .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal).FirstOrDefault();
            if (path is not null)
            {
                string text = File.ReadAllText(path);
                File.Move(path, path + ".taken", overwrite: true);
                return new Entry(Path.GetFileNameWithoutExtension(path), Path.GetExtension(path)[1..], text);
            }
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
