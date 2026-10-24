using System.Diagnostics;
using System.Text.Json.Nodes;
using Marionette.Desktop;

internal static class ViewportCorrectionTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        foreach (bool more in new[] { false, true })
            registry.Add("viewport-correction", more ? "review mid-more preserves ordering" : "review mid-initial preserves ordering", () =>
            {
                var result = C1ScriptOrderingEvidence.Run(settings.WorkDirectory, more);
                if (!result.Passed) throw new Exception($"{result.Error}; evidence: {result.Root}");
            });
        foreach (string scenario in new[] { "idle-open", "checkpoint", "earlier-step" })
            registry.Add("viewport-correction", scenario, () =>
            {
                var result = C1ScriptOrderingEvidence.RunBoundaryCase(settings.WorkDirectory, scenario);
                if (!result.Passed) throw new Exception($"{result.Error}; evidence: {result.Root}");
            });
        registry.Add("viewport-correction", "native AI review holds directions until resume and delivers once", () =>
        {
            var tools = new AiSuiteTools(Path.Combine(settings.WorkDirectory, "native-review"));
            var live = new AiLiveRun(tools);
            using var cancel = new CancellationTokenSource();
            using var stop = new CancellationTokenSource();
            var control = new AiLiveControl.Session(tools.Root, live, cancel);
            var watch = control.WatchAsync(stop.Token);
            void Wait(Func<bool> predicate)
            {
                var clock = Stopwatch.StartNew();
                while (!predicate())
                {
                    if (clock.ElapsedMilliseconds > 5000) throw new TimeoutException("native review acknowledgement");
                    Thread.Sleep(10);
                }
            }
            void Send(string kind, string? text = null)
            {
                string id = DateTime.UtcNow.Ticks.ToString("D19") + Guid.NewGuid().ToString("N");
                var command = new JsonObject { ["schemaVersion"] = 1, ["runId"] = live.RunId,
                    ["id"] = id, ["command"] = kind, ["text"] = text };
                AiLiveRun.AtomicWrite(Path.Combine(tools.Root, ".live/commands", id + ".json"), command.ToJsonString());
                Wait(() => AiLiveRun.ReadLatest(tools.Root)?["control"]?["commandId"]?.GetValue<string>() == id);
            }
            Task? boundary = null;
            try
            {
                Send("review");
                boundary = control.BoundaryAsync(cancel.Token);
                Wait(() => AiLiveRun.ReadLatest(tools.Root)?["control"]?["paused"]?.GetValue<bool>() == true);
                Send("instruct", "Make the existing handle shorter, then continue.");
                if (control.TakeDirections().Count != 0 || boundary.IsCompleted)
                    throw new Exception("Native review leaked a direction or operation while held.");
                Send("resume");
                if (!boundary.Wait(2000)) throw new Exception("Native review never resumed.");
                var directions = control.TakeDirections();
                if (directions.Count != 1 || !directions[0].Text.Contains("existing handle") || control.TakeDirections().Count != 0)
                    throw new Exception("Native correction was lost or delivered twice.");
                if (cancel.IsCancellationRequested) throw new Exception("Review cancelled the run.");
            }
            finally
            {
                cancel.Cancel(); stop.Cancel();
                try { watch.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
                if (boundary is not null) try { boundary.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            }
        });
    }
}
