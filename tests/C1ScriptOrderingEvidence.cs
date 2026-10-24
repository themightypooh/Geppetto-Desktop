using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Effigy;
using Marionette.Desktop;

// Independent C1 acceptance helper. No production files or shared registrations
// are changed. The isolated runner described in the evidence report calls this.
internal static class C1ScriptOrderingEvidence
{
    public sealed record Evidence(string Name, bool Passed, string Root,
        string[] Operations, string? Error);

    static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    static JsonObject Latest(string root) => AiLiveRun.ReadLatest(root) ?? new JsonObject();

    static void Wait(Func<bool> predicate, string message, Task task, int milliseconds = 12000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            if (predicate()) return;
            if (task.IsFaulted) task.GetAwaiter().GetResult();
            if (task.IsCompleted) throw new InvalidOperationException(message + ": run ended early");
            Thread.Sleep(10);
        }
        throw new TimeoutException(message);
    }

    static void Command(string root, string kind)
    {
        string id = Guid.NewGuid().ToString("N");
        var command = new JsonObject
        {
            ["schemaVersion"] = 1, ["runId"] = Latest(root)["runId"]!.GetValue<string>(),
            ["id"] = id, ["command"] = kind
        };
        string path = Path.Combine(root, ".live/commands", id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AiLiveRun.AtomicWrite(path, command.ToJsonString());
    }

    static string[] Operations(string root)
    {
        string file = Path.Combine(root, ".live/events.jsonl");
        if (!File.Exists(file)) return Array.Empty<string>();
        // Atomic latest is the synchronization source. Ignore a journal's final
        // partially-written line while the isolated runner is still live.
        var result = new List<string>();
        foreach (string line in File.ReadAllLines(file))
        {
            try
            {
                var e = JsonNode.Parse(line)!;
                if (e["state"]?.GetValue<string>() == "revision")
                    result.Add(e["operation"]?.GetValue<string>() ?? "initial");
            }
            catch (JsonException) { }
        }
        return result.ToArray();
    }

    static bool Has(string root, string text) => Operations(root).Any(op => op.Contains(text));

    public static Evidence RunBoundaryCase(string parent, string scenario)
    {
        Require(new[] { "idle-open", "checkpoint", "earlier-step" }.Contains(scenario), "unknown scenario");
        string root = Path.Combine(parent, "stop-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string baseLine = "add Primitive shape=Box name=Base width=2 depth=2 height=2\n";
        const string tail = "add Primitive shape=Box name=TailA position=6,0,0\n" +
                            "add Primitive shape=Box name=TailB position=9,0,0\n";
        string initial = baseLine + (scenario switch
        {
            "checkpoint" => "checkpoint \"Review me\" focus=Base\n" + tail,
            "earlier-step" => "add Primitive shape=Box name=LaterGone position=3,0,0\n",
            _ => ""
        });
        File.WriteAllText(Path.Combine(root, "input.txt"), initial);
        File.WriteAllText(Path.Combine(root, "script.txt"), initial);
        using var cancel = new CancellationTokenSource();
        var task = Task.Run(() => ScriptLive.Execute(new PartStudio(), initial, root, root,
            0.8, cancel, open: true, idleMinutes: 1));
        string? error = null;
        try
        {
            if (scenario == "checkpoint")
                Wait(() => Latest(root)["state"]?.GetValue<string>() == "checkpoint",
                    "fixture did not reach its checkpoint", task);
            else
                Wait(() => File.Exists(Path.Combine(root, ".live/answers/start.txt")),
                    "initial batch did not become idle/open", task);

            if (scenario == "earlier-step")
            {
                Inbox.Put(root, "txt", "add Primitive shape=Box name=CompletedMore position=0,3,0\n" + tail);
                Wait(() => Has(root, "name=CompletedMore"), "rewind fixture did not start its more batch", task);
            }

            Command(root, "review");
            Wait(() => Latest(root)["control"]?["paused"]?.GetValue<bool>() == true,
                "Stop did not acknowledge paused at " + scenario, task);
            Require(!Has(root, "name=TailA") && !Has(root, "name=TailB"), "tail escaped Stop at " + scenario);
            long held = Latest(root)["snapshot"]!["revision"]!.GetValue<long>();
            var history = Directory.GetFiles(Path.Combine(root, ".live/revisions"), "*.effigy")
                .ToDictionary(p => p, File.ReadAllBytes);

            long selected = held;
            if (scenario == "earlier-step")
            {
                selected = 2; // initial empty revision + Base's committed operation
                string earlier = history.Keys.Single(p => Path.GetFileName(p).StartsWith("00000002-"));
                Require(File.Exists(earlier), "selected saved step is missing");
                Inbox.Put(root, "rewind", selected.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            File.WriteAllText(Path.Combine(root, "notes.jsonl"), JsonSerializer.Serialize(new
            {
                time = DateTime.UtcNow.ToString("O"), run = Path.GetFileName(root),
                rev = selected, op = "acceptance correction", text = "Set Base width to 7 on this saved step", read = false
            }) + "\n");
            Command(root, "resume");

            if (scenario == "earlier-step")
            {
                Wait(() => Has(root, "rewind to step 2"), "saved-step rewind did not commit", task);
                Wait(() => Latest(root)["state"]?.GetValue<string>() == "open", "rewound fixture did not reopen", task);
                held = Latest(root)["snapshot"]!["revision"]!.GetValue<long>();
            }
            Thread.Sleep(1100); // longer than the mutation pace; no modeller fix yet
            Require(!task.IsCompleted, "run ended during correction delivery at " + scenario);
            Require(Latest(root)["snapshot"]!["revision"]!.GetValue<long>() == held,
                "run advanced before the correction at " + scenario);
            Require(!Has(root, "name=TailA") && !Has(root, "name=TailB"),
                "tail advanced during correction delivery at " + scenario);

            string fix = Inbox.Put(root, "txt", "set Base width=7\n" +
                "add Primitive shape=Box name=CorrectionMarker position=0,6,0\n");
            Wait(() => File.Exists(Path.Combine(root, ".live/answers", fix + ".txt")),
                "correction did not finish at " + scenario, task);
            string answer = File.ReadAllText(Path.Combine(root, ".live/answers", fix + ".txt"));
            Require(!answer.Contains("ERROR"), "correction failed: " + answer);
            string[] expected = scenario switch
            {
                "checkpoint" => new[] { "name=Base", "checkpoint \"Review me\"", "set Base width=7", "name=CorrectionMarker", "name=TailA", "name=TailB" },
                "earlier-step" => new[] { "name=Base", "name=LaterGone", "name=CompletedMore", "rewind to step 2", "set Base width=7", "name=CorrectionMarker" },
                _ => new[] { "name=Base", "set Base width=7", "name=CorrectionMarker" }
            };
            string[] operations = Operations(root);
            int previous = -1;
            foreach (string fragment in expected)
            {
                Require(operations.Count(op => op.Contains(fragment)) == 1, "missing or repeated operation: " + fragment);
                int index = Array.FindIndex(operations, op => op.Contains(fragment));
                Require(index > previous, "operations reordered at " + fragment);
                previous = index;
            }
            Require(operations.Length == expected.Length + 1, "unexpected committed operation at " + scenario);
            if (scenario == "earlier-step")
                Require(!Has(root, "name=TailA") && !Has(root, "name=TailB"),
                    "rewind failed to discard the old revision's queued tail");

            Inbox.Put(root, "close");
            Require(task.Wait(5000), "fixture did not close at " + scenario);
            var outcome = task.GetAwaiter().GetResult();
            Require(outcome.State == "completed" && outcome.Result.Ok, "fixture failed at " + scenario);
            string[] names = outcome.Studio.Features.Select(f => f.Name).ToArray();
            Require(names.SequenceEqual(scenario == "checkpoint"
                ? new[] { "Base", "CorrectionMarker", "TailA", "TailB" }
                : new[] { "Base", "CorrectionMarker" }), "wrong retained features: " + string.Join(",", names));
            var bounds = ModelMeasure.Bounds(outcome.Studio.Bodies.Single(b => b.Name == "Base").Mesh);
            Require(Math.Abs(bounds.Item2.x - bounds.Item1.x - 7) < 0.0001, "saved-step correction did not change the retained mesh");
            foreach (var pair in history)
                Require(File.Exists(pair.Key) && File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value),
                    "saved history was overwritten or deleted: " + pair.Key);
            var note = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "notes.jsonl")).Trim())!;
            Require(note["rev"]!.GetValue<long>() == selected, "correction note lost the selected revision");
        }
        catch (Exception e) { error = e.ToString(); }
        finally
        {
            if (!task.IsCompleted) { cancel.Cancel(); task.Wait(5000); }
        }
        var evidence = new Evidence("stop-" + scenario, error is null, root, Operations(root), error);
        File.WriteAllText(Path.Combine(root, "acceptance.json"), JsonSerializer.Serialize(evidence,
            new JsonSerializerOptions { WriteIndented = true }));
        return evidence;
    }

    public static Evidence Run(string parent, bool duringMore)
    {
        string name = duringMore ? "stop-mid-more" : "stop-mid-initial";
        string root = Path.Combine(parent, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string baseLine = "add Primitive shape=Box name=Base width=2 depth=2 height=2\n";
        const string completed = "add Primitive shape=Box name=CompletedMore position=3,0,0\n";
        const string tail = "add Primitive shape=Box name=TailA position=6,0,0\n" +
                            "add Primitive shape=Box name=TailB position=9,0,0\n";
        string initial = duringMore ? baseLine : baseLine + tail;
        File.WriteAllText(Path.Combine(root, "input.txt"), initial);
        File.WriteAllText(Path.Combine(root, "script.txt"), initial);
        using var cancel = new CancellationTokenSource();
        var task = Task.Run(() => ScriptLive.Execute(new PartStudio(), initial, root, root,
            0.8, cancel, open: true, idleMinutes: 1));
        string? error = null;
        try
        {
            if (duringMore)
            {
                Wait(() => File.Exists(Path.Combine(root, ".live/answers/start.txt")), "initial batch did not open", task);
                Inbox.Put(root, "txt", completed + tail);
                Wait(() => Has(root, "name=CompletedMore"), "more batch did not commit its first operation", task);
            }
            else
                Wait(() => Has(root, "name=Base"), "initial batch did not commit its first operation", task);

            Command(root, "review"); // the viewport Stop protocol, not cancellation
            Wait(() => Latest(root)["control"]?["paused"]?.GetValue<bool>() == true,
                "review did not acknowledge a safe paused boundary", task);
            long heldRevision = Latest(root)["snapshot"]!["revision"]!.GetValue<long>();
            Require(!Has(root, "name=TailA") && !Has(root, "name=TailB"), "tail raced ahead of Stop");

            // ViewportCorrections.send writes a note and RESUME before the
            // external modeller returns its corrective `more`. Reproduce that
            // real delivery gap and wait longer than the 0.8-second build pace.
            File.WriteAllText(Path.Combine(root, "notes.jsonl"), JsonSerializer.Serialize(new
            {
                time = DateTime.UtcNow.ToString("O"), run = Path.GetFileName(root),
                rev = heldRevision, op = "acceptance correction", text = "Set Base width to 7 before continuing", read = false
            }) + "\n");
            Command(root, "resume");
            Thread.Sleep(1100);
            Require(!task.IsCompleted, "resume unexpectedly ended the open run");
            Require(Latest(root)["snapshot"]!["revision"]!.GetValue<long>() == heldRevision,
                "preserved tail advanced before the modeller supplied a correction");
            Require(!Has(root, "name=TailA"), "tail ran during the correction delivery gap");

            string fix = Inbox.Put(root, "txt", "set Base width=7\n" +
                "add Primitive shape=Box name=CorrectionMarker position=0,3,0\n");
            Wait(() => File.Exists(Path.Combine(root, ".live/answers", fix + ".txt")),
                "correction batch and preserved tail did not finish", task);
            string answer = File.ReadAllText(Path.Combine(root, ".live/answers", fix + ".txt"));
            Require(!answer.Contains("ERROR"), "correction answer contains an error: " + answer);

            string[] operations = Operations(root);
            string[] expected = duringMore
                ? new[] { "name=Base", "name=CompletedMore", "set Base width=7", "name=CorrectionMarker", "name=TailA", "name=TailB" }
                : new[] { "name=Base", "set Base width=7", "name=CorrectionMarker", "name=TailA", "name=TailB" };
            int last = -1;
            foreach (string fragment in expected)
            {
                Require(operations.Count(op => op.Contains(fragment)) == 1,
                    $"operation must commit exactly once: {fragment}; got {string.Join(" | ", operations)}");
                int index = Array.FindIndex(operations, op => op.Contains(fragment));
                Require(index > last, "correction/preserved-tail order violated: " + fragment);
                last = index;
            }
            Require(operations.Length == expected.Length + 1,
                "unexpected or repeated committed operations: " + string.Join(" | ", operations));

            Inbox.Put(root, "close");
            Require(task.Wait(5000), "fixture did not close");
            var outcome = task.GetAwaiter().GetResult();
            Require(outcome.State == "completed" && outcome.Result.Ok, "fixture ended failed: " + outcome.Result);
            string[] names = outcome.Studio.Features.Select(f => f.Name).ToArray();
            string[] wantNames = duringMore
                ? new[] { "Base", "CompletedMore", "CorrectionMarker", "TailA", "TailB" }
                : new[] { "Base", "CorrectionMarker", "TailA", "TailB" };
            Require(names.SequenceEqual(wantNames), "completed features repeated, lost or reordered: " + string.Join(",", names));
            var body = outcome.Studio.Bodies.Single(b => b.Name == "Base");
            var bounds = ModelMeasure.Bounds(body.Mesh);
            Require(Math.Abs(bounds.Item2.x - bounds.Item1.x - 7) < 0.0001,
                "correction did not affect the retained model");
        }
        catch (Exception e) { error = e.ToString(); }
        finally
        {
            if (!task.IsCompleted) { cancel.Cancel(); task.Wait(5000); }
        }
        var evidence = new Evidence(name, error is null, root, Operations(root), error);
        File.WriteAllText(Path.Combine(root, "acceptance.json"), JsonSerializer.Serialize(evidence,
            new JsonSerializerOptions { WriteIndented = true }));
        return evidence;
    }
}
