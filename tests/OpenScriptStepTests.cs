using System.Diagnostics;
using System.Text.Json.Nodes;
using Effigy;
using Marionette.Desktop;

internal static class OpenScriptStepTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        registry.Add("open-script-step", "STEP stops after var and rebuild, not the following add", () =>
        {
            using var run = new Fixture(settings.WorkDirectory,
                "var length=3\nrebuild\nadd Primitive shape=Box name=TailA\n");
            run.Step();
            run.Step();
            run.Step();
            run.Close();
            Require(run.Task.Result.Result.Ok, "script failed: " + run.Task.Result.Result);
        });
        registry.Add("open-script-step", "pause then repeated STEP permits exactly one mutation each", () =>
        {
            using var run = new Fixture(settings.WorkDirectory);
            run.Step();
            run.Step();
            run.Close();
        });
        foreach (bool afterStep in new[] { false, true })
            registry.Add("open-script-step", afterStep
                ? "review after STEP keeps correction ahead of preserved tail"
                : "bare resume keeps correction ahead of preserved tail", () =>
            {
                using var run = new Fixture(settings.WorkDirectory);
                if (afterStep) run.Step();
                run.Command("review");
                long held = run.Revision;
                run.Command("resume");
                run.Unchanged(held);
                string edit = Inbox.Put(run.Root, "txt", "set Base width=7\n");
                run.Wait(() => File.Exists(Path.Combine(run.Root, ".live/answers", edit + ".txt")), "correction answer");
                string[] operations = File.ReadAllLines(Path.Combine(run.Root, ".live/events.jsonl"))
                    .Select(line => JsonNode.Parse(line)!)
                    .Where(e => e["state"]?.GetValue<string>() == "revision")
                    .Select(e => e["operation"]?.GetValue<string>() ?? "initial").ToArray();
                int fix = Array.FindIndex(operations, op => op.Contains("set Base width=7"));
                int tail = Array.FindIndex(operations, op => op.Contains(afterStep ? "name=TailB" : "name=TailA"));
                Require(fix >= 0 && tail > fix, "preserved tail preceded correction: " + string.Join(" | ", operations));
                Require(run.Revision == 6, "expected initial, Base, three tails and exactly one correction");
                run.Close();
                Require(run.Task.Result.Result.Ok, "correction failed: " + run.Task.Result.Result);
            });
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    sealed class Fixture : IDisposable
    {
        readonly CancellationTokenSource cancel = new();
        public string Root { get; }
        public Task<ScriptLive.Outcome> Task { get; }
        JsonObject Latest => AiLiveRun.ReadLatest(Root) ?? new();
        public long Revision => Latest["snapshot"]?["revision"]?.GetValue<long>() ?? 0;

        public Fixture(string parent, string? tail = null)
        {
            Root = Path.Combine(parent, "open-script-step-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            string script = "add Primitive shape=Box name=Base\n"
                + (tail ?? "add Primitive shape=Box name=TailA\n"
                + "add Primitive shape=Box name=TailB\n"
                + "add Primitive shape=Box name=TailC\n");
            Task = System.Threading.Tasks.Task.Run(() => ScriptLive.Execute(new PartStudio(), script,
                Root, Root, 0.8, cancel, open: true, idleMinutes: 1));
            try
            {
                Wait(() => Revision == 2, "first mutation");
                Command("pause");
                Wait(() => Latest["control"]?["paused"]?.GetValue<bool>() == true, "pause boundary");
                Unchanged(2);
            }
            catch { Dispose(); throw; }
        }

        public void Wait(Func<bool> predicate, string message)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate())
            {
                if (Task.IsFaulted) Task.GetAwaiter().GetResult();
                Require(!Task.IsCompleted, "run ended before " + message);
                if (clock.ElapsedMilliseconds > 10000) throw new TimeoutException(message + "; " + Root);
                Thread.Sleep(10);
            }
        }

        public void Command(string kind)
        {
            string id = DateTime.UtcNow.Ticks.ToString("D19") + Guid.NewGuid().ToString("N");
            var command = new JsonObject { ["schemaVersion"] = 1, ["runId"] = Latest["runId"]!.GetValue<string>(),
                ["id"] = id, ["command"] = kind };
            AiLiveRun.AtomicWrite(Path.Combine(Root, ".live/commands", id + ".json"), command.ToJsonString());
            Wait(() => File.ReadAllText(Path.Combine(Root, ".live/events.jsonl")).Contains(id), kind + " acknowledgement");
        }

        public void Unchanged(long revision)
        {
            Thread.Sleep(1000); // Longer than the mutation pace: catch unintended resume/extra STEP.
            Require(Revision == revision, $"expected held revision {revision}, got {Revision}; {Root}");
        }

        public void Step()
        {
            long before = Revision;
            Command("step");
            Wait(() => Revision == before + 1, "STEP mutation");
            Unchanged(before + 1);
            Require(Latest["control"]?["pauseRequested"]?.GetValue<bool>() == true, "STEP released pause");
        }

        public void Close()
        {
            Inbox.Put(Root, "close");
            Require(Task.Wait(5000), "close did not reach the script hold");
            Require(Task.Result.State == "completed", "unexpected close state: " + Task.Result.State);
        }

        public void Dispose()
        {
            cancel.Cancel();
            try { Require(Task.Wait(5000), "isolated fixture did not cancel"); }
            finally { cancel.Dispose(); }
        }
    }
}
