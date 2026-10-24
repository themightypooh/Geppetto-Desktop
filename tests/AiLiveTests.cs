using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Effigy;
using Marionette.Desktop;

internal static class AiLiveTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        registry.Add("ai-live", "successful mutations, empty removal and restore publish immutable revisions", () =>
        {
            var tools = new AiSuiteTools(Path.Combine(settings.WorkDirectory, "live-commits"));
            tools.Live = new AiLiveRun(tools);
            tools.Live.Commit(tools.Studio, "initial");
            string first = tools.Owned(AiLiveRun.ReadLatest(tools.Root)!["snapshot"]!["path"]!.GetValue<string>());
            tools.ExecuteAsync("add_feature", JsonNode.Parse("{\"type\":\"Primitive\",\"fields\":{\"SizeX\":4}}")!, default).GetAwaiter().GetResult();
            string checkpoint = tools.SaveCheckpoint(0), id = tools.Studio.Features[0].Id;
            long before = tools.Live.Revision;
            try { tools.ExecuteAsync("edit_feature", JsonNode.Parse("{\"id\":\"missing\",\"fields\":{}}")!, default).GetAwaiter().GetResult(); }
            catch (ArgumentException) { }
            Check(tools.Live.Revision == before, "failed edit published a revision");
            tools.ExecuteAsync("remove_feature", new JsonObject { ["id"] = id }, default).GetAwaiter().GetResult();
            Check(tools.Studio.Features.Count == 0 && tools.Live.Revision == 3, "empty removal missing");
            tools.Restore(checkpoint);
            var last = AiLiveRun.ReadLatest(tools.Root)!;
            Check(tools.Live.Revision == 4 && last["snapshot"]!["checkpoint"]!.GetValue<string>() == "round-00.effigy", "restore did not advance publication sequence");
            Check(StudioDocument.ReadFile(first).Features.Count == 0, "old revision was overwritten");
            File.AppendAllText(tools.Owned(".live/events.jsonl"), "\n{not complete");
            Check(AiLiveRun.ReadLatest(tools.Root)!["sequence"]!.GetValue<long>() == last["sequence"]!.GetValue<long>(), "torn journal lost valid state");
        });
        registry.Add("ai-live", "pause, pace and command validation at operation boundaries", () =>
        {
            var tools = new AiSuiteTools(Path.Combine(settings.WorkDirectory, "live-steer-session"));
            var live = new AiLiveRun(tools);
            using var run = new CancellationTokenSource(); using var stop = new CancellationTokenSource();
            var session = new AiLiveControl.Session(tools.Root, live, run, allowDirections: false);
            var watch = session.WatchAsync(stop.Token);
            string root = tools.Root, id = live.RunId;
            string Outcome(string c) => Acknowledged(root, c)["outcome"]!.GetValue<string>();
            Check(Outcome(Command(root, id, "pace", new JsonObject { ["seconds"] = 31 })) == "pace-must-be-0-to-30-seconds", "oversized pace accepted");
            Check(Outcome(Command(root, id, "instruct", new JsonObject { ["text"] = "taller" })) == "judged-rubric-is-locked", "judged run accepted a direction");
            Check(Outcome(Command(root, id, "explode")) == "unknown-command", "unknown command accepted");
            string foreign = Command(root, "other", "pause");
            Check(Outcome(Command(root, id, "pace", new JsonObject { ["seconds"] = 0.4 })) == "accepted", "pace rejected");
            Check(AiLiveRun.ReadLatest(root)!["control"]!["paceSeconds"]!.GetValue<double>() == 0.4 && !AiLiveRun.ReadLatest(root)!["control"]!["pauseRequested"]!.GetValue<bool>(), "foreign pause or pace state wrong");
            // First mutation has no artificial delay; the next waits for the pace gap.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            session.BoundaryAsync(default, mutation: true).GetAwaiter().GetResult();
            Check(clock.ElapsedMilliseconds < 200, "first mutation was delayed");
            session.MutationFinished(); clock.Restart();
            session.BoundaryAsync(default, mutation: true).GetAwaiter().GetResult();
            Check(clock.ElapsedMilliseconds is >= 380 and < 1500, $"pace gap not honoured ({clock.ElapsedMilliseconds} ms)");
            // Lowering pace during a wait takes effect immediately.
            session.MutationFinished(); Command(root, id, "pace", new JsonObject { ["seconds"] = 30 });
            var waiting = session.BoundaryAsync(default, mutation: true);
            Thread.Sleep(300); Check(!waiting.IsCompleted, "30 s pace not waiting");
            Acknowledged(root, Command(root, id, "pace", new JsonObject { ["seconds"] = 0 }));
            Check(waiting.Wait(1000), "pace change was not recomputed during the wait");
            // Pause blocks the next boundary until resume, and stop works while paused.
            Acknowledged(root, Command(root, id, "pause"));
            var blocked = session.BoundaryAsync(default);
            Thread.Sleep(300);
            Check(!blocked.IsCompleted && AiLiveRun.ReadLatest(root)!["state"]!.GetValue<string>() == "paused", "pause did not hold the boundary");
            Acknowledged(root, Command(root, id, "resume"));
            Check(blocked.Wait(1000), "resume did not release the boundary");
            // STEP is refused unless paused; while paused it lets exactly one change through.
            Check(Outcome(Command(root, id, "step")) == "step-only-while-paused", "step accepted while running");
            Acknowledged(root, Command(root, id, "pause"));
            session.BoundaryAsync(default).Wait(100);
            Check(Outcome(Command(root, id, "step")) == "accepted", "step rejected while paused");
            Check(session.BoundaryAsync(default).Wait(1000), "step did not let the run reach its next change");
            Check(session.BoundaryAsync(default, mutation: true).Wait(1000), "step did not let one change through");
            var afterStep = session.BoundaryAsync(default, mutation: true);
            Thread.Sleep(300); Check(!afterStep.IsCompleted, "step let a second change through");
            Acknowledged(root, Command(root, id, "resume"));
            Check(afterStep.Wait(1000), "resume after step did not release");
            Acknowledged(root, Command(root, id, "pause"));
            using var paused = new CancellationTokenSource();
            var held = session.BoundaryAsync(paused.Token);
            Command(root, id, "cancel");
            Check(watch.Wait(2000) && run.IsCancellationRequested, "cancel while paused not accepted");
            paused.Cancel();
            Check(held.ContinueWith(t => t.IsCanceled).Wait(1000), "paused boundary ignored cancellation");
            Check(!File.ReadAllLines(Path.Combine(root, ".live/events.jsonl")).Any(l => l.Contains(foreign)), "foreign command acknowledged");
            stop.Cancel();
        });
        registry.Add("ai-live", "direction during provider wait supersedes unstarted calls and steers the model", () =>
        {
            string root = Path.Combine(settings.WorkDirectory, "live-steer-direction");
            string? ack = null;
            var handler = new Scripted((call, body) =>
            {
                if (call == 1)
                {
                    // Player directs while the provider is still answering with a three-call plan.
                    string runId = AiLiveRun.ReadLatest(root)!["runId"]!.GetValue<string>();
                    ack = Command(root, runId, "instruct", new JsonObject { ["text"] = "Make ONE block, 9 wide." });
                    Acknowledged(root, ack);
                    return new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = new JsonArray(Call("a", 2), Call("b", 2), Call("c", 2)) };
                }
                if (call == 2) return new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = new JsonArray(Call("d", 9)) };
                return new JsonObject { ["role"] = "assistant", ["content"] = "One 9-wide block built." };
            });
            var options = new AiOptions { Command = "create", Model = "fixture", Endpoint = "http://localhost:5678/v1/", Output = root, Prompt = "three blocks", Calls = 8 };
            Check(new AiSession(options, new HttpClient(handler)).RunAsync(default).GetAwaiter().GetResult() == 0, "steered session failed");
            string second = handler.Bodies[1];
            Check(second.Contains("PLAYER DIRECTIONS") && second.Contains("9 wide"), "direction not delivered to the builder");
            Check(System.Text.RegularExpressions.Regex.Matches(second, "not_executed").Count == 3, "superseded calls lack explicit not-executed results");
            var events = File.ReadAllLines(Path.Combine(root, ".live/events.jsonl")).Select(x => JsonNode.Parse(x)!).ToArray();
            Check(events.Any(e => e["state"]!.GetValue<string>() == "direction-delivered" && e["control"]!["commandId"]!.GetValue<string>() == ack), "delivery not acknowledged");
            var last = events.Last();
            var studio = StudioDocument.ReadFile(Path.Combine(root, last["snapshot"]!["path"]!.GetValue<string>()));
            Check(last["state"]!.GetValue<string>() == "completed" && studio.Features.Count == 1, $"superseded operations ran ({studio.Features.Count} features)");
            Check(!events.Any(e => e.ToJsonString().Contains("9 wide")), "direction text leaked into public events");
        });
        registry.Add("ai-live", "direction racing the final answer is applied before completion", () =>
        {
            string root = Path.Combine(settings.WorkDirectory, "live-steer-final");
            var handler = new Scripted((call, body) =>
            {
                if (call == 1) return new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = new JsonArray(Call("a", 2)) };
                if (call == 2)
                {
                    string runId = AiLiveRun.ReadLatest(root)!["runId"]!.GetValue<string>();
                    Acknowledged(root, Command(root, runId, "instruct", new JsonObject { ["text"] = "Add a second block." }));
                    return new JsonObject { ["role"] = "assistant", ["content"] = "Done." };
                }
                if (call == 3) return new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = new JsonArray(Call("b", 4)) };
                return new JsonObject { ["role"] = "assistant", ["content"] = "Second block added." };
            });
            var options = new AiOptions { Command = "create", Model = "fixture", Endpoint = "http://localhost:5678/v1/", Output = root, Prompt = "one block", Calls = 8 };
            Check(new AiSession(options, new HttpClient(handler)).RunAsync(default).GetAwaiter().GetResult() == 0, "session failed");
            Check(handler.Bodies.Count == 4 && handler.Bodies[2].Contains("Add a second block"), "late direction was dropped");
            var last = AiLiveRun.ReadLatest(root)!;
            var studio = StudioDocument.ReadFile(Path.Combine(root, last["snapshot"]!["path"]!.GetValue<string>()));
            Check(last["state"]!.GetValue<string>() == "completed" && studio.Features.Count == 2, "late direction not built before completion");
            string late = Command(root, last["runId"]!.GetValue<string>(), "pause");
            Thread.Sleep(200);
            Check(!File.ReadAllText(Path.Combine(root, ".live/events.jsonl")).Contains(late), "finished run processed a command");
        });
        registry.Add("ai-live", "provider loop publishes five worker-built revisions and reusable caches", () =>
        {
            string root = Path.Combine(settings.WorkDirectory, "live-provider");
            var options = new AiOptions { Command = "create", Model = "fixture", Endpoint = "http://localhost:5678/v1/", Output = root, Prompt = "five growing blocks", Calls = 8 };
            Check(new AiSession(options, new HttpClient(new FiveOperations())).RunAsync(default).GetAwaiter().GetResult() == 0, "session failed");
            var events = File.ReadAllLines(Path.Combine(root, ".live/events.jsonl")).Select(x => JsonNode.Parse(x)!).ToArray();
            var mutations = events.Where(x => x["state"]!.GetValue<string>() == "revision" && x["operation"]!.GetValue<string>() == "add_feature").ToArray();
            Check(mutations.Length == 5, "missing committed operations");
            for (int i = 0; i < mutations.Length; i++)
            {
                string path = Path.Combine(root, mutations[i]["snapshot"]!["path"]!.GetValue<string>());
                var studio = StudioDocument.ReadFile(path);
                Check(studio.Features.Count == i + 1, "revision order or content incorrect");
                Check(StudioCache.TryRestore(studio, path, out _), "worker geometry cache not reusable");
                Check(studio.TotalFaceCount > 0, "cache restored no geometry");
            }
            Check(events.Last()["state"]!.GetValue<string>() == "completed", "terminal state missing");
            Check(events.Select(x => x["sequence"]!.GetValue<long>()).SequenceEqual(Enumerable.Range(1, events.Length).Select(x => (long)x)), "nonmonotonic sequence");
        });
        registry.Add("ai-live", "cancel mailbox rejects foreign identity and blocks late commits", () =>
        {
            var tools = new AiSuiteTools(Path.Combine(settings.WorkDirectory, "live-control"));
            var live = new AiLiveRun(tools);
            live.Commit(tools.Studio, "initial");
            using var run = new CancellationTokenSource(); using var stop = new CancellationTokenSource();
            var watch = AiLiveControl.WatchAsync(tools.Root, live, run, stop.Token);
            string path = tools.Owned(".live/control.json");
            AiLiveRun.AtomicWrite(path, new JsonObject { ["schemaVersion"] = 1, ["runId"] = "other", ["command"] = "cancel" }.ToJsonString());
            Thread.Sleep(200);
            Check(!run.IsCancellationRequested, "foreign run cancelled this run");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            AiLiveRun.AtomicWrite(path, new JsonObject { ["schemaVersion"] = 1, ["runId"] = live.RunId, ["command"] = "cancel" }.ToJsonString());
            Check(watch.Wait(2000) && run.IsCancellationRequested, "cancel was not acknowledged");
            Check(clock.ElapsedMilliseconds < 1000, "local cancel acknowledgement exceeded one second");
            Check(AiLiveRun.ReadLatest(tools.Root)!["state"]!.GetValue<string>() == "cancelling", "missing acknowledgement event");
            bool rejected = false;
            try { live.Commit(tools.Studio, "late"); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected && live.Revision == 1, "late commit survived cancellation");
        });
        registry.Add("ai-live", "cancel during provider wait preserves last committed revision", () =>
        {
            string root = Path.Combine(settings.WorkDirectory, "live-cancel-provider");
            var handler = new WaitingProvider();
            var options = new AiOptions { Command = "create", Model = "fixture", Endpoint = "http://localhost:5678/v1/", Output = root, Prompt = "wait" };
            var session = new AiSession(options, new HttpClient(handler));
            var build = Task.Run(() => session.RunAsync(default));
            Check(handler.Started.Wait(3000), "provider did not start");
            var state = AiLiveRun.ReadLatest(root)!;
            AiLiveRun.AtomicWrite(Path.Combine(root, ".live/control.json"), new JsonObject { ["schemaVersion"] = 1, ["runId"] = state["runId"]!.GetValue<string>(), ["command"] = "cancel" }.ToJsonString());
            bool cancelled = false;
            try { build.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && handler.Cancelled, "provider request was not cancelled");
            var last = AiLiveRun.ReadLatest(root)!;
            Check(last["state"]!.GetValue<string>() == "cancelled" && last["snapshot"]!["revision"]!.GetValue<long>() == 1, "terminal state lost committed snapshot");
        });

        registry.Add("ai-live", "cancelling in-flight native worker leaves no process or new revision", () =>
        {
            var tools = new AiSuiteTools(Path.Combine(settings.WorkDirectory, "live-cancel-native")) { Isolated = true };
            tools.Live = new AiLiveRun(tools);
            tools.ExecuteAsync("add_feature", JsonNode.Parse("""{"type":"Primitive","fields":{}}""")!, default).GetAwaiter().GetResult();
            long revision = tools.Live.Revision;
            var existing = Directory.GetDirectories(tools.Owned(".ai-jobs")).ToHashSet();
            using var cancel = new CancellationTokenSource();
            var work = Task.Run(() => tools.ExecuteAsync("add_feature", JsonNode.Parse("""{"type":"Subdivide","fields":{"Levels":6}}""")!, cancel.Token));
            string? running = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 5000 && !work.IsCompleted)
            {
                running = Directory.GetDirectories(tools.Owned(".ai-jobs")).Where(d => !existing.Contains(d)).Select(d => Path.Combine(d, "running.json")).FirstOrDefault(File.Exists);
                if (running is not null) break;
                Thread.Sleep(2);
            }
            Check(running is not null, "native process did not reach its operation");
            int pid = JsonNode.Parse(File.ReadAllText(running!))!["pid"]!.GetValue<int>();
            cancel.Cancel();
            bool cancelled = false;
            try { work.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && tools.Live.Revision == revision, "cancelled worker committed geometry");
            bool alive;
            try { using var process = System.Diagnostics.Process.GetProcessById(pid); alive = !process.HasExited; }
            catch (ArgumentException) { alive = false; }
            Check(!alive, "cancel left an orphan worker");
        });

        registry.Add("ai-live", "incremental journal waits for complete records and recovers from malformed lines", () =>
        {
            var tools = new AiSuiteTools(Path.Combine(settings.WorkDirectory, "live-journal"));
            var live = new AiLiveRun(tools); live.Commit(tools.Studio, "initial");
            using var reader = new AiLiveJournalReader(tools.Root);
            var first = reader.Poll()!;
            var next = (JsonObject)first.DeepClone(); next["sequence"] = 100;
            string text = next.ToJsonString(), path = tools.Owned(".live/events.jsonl");
            File.AppendAllText(path, text[..20]);
            Check(reader.Poll()!["sequence"]!.GetValue<long>() == first["sequence"]!.GetValue<long>(), "partial record advanced state");
            File.AppendAllText(path, text[20..] + "\n{bad}\n");
            Check(reader.Poll()!["sequence"]!.GetValue<long>() == 100, "completed tail was lost");
        });
        registry.Add("ai-live", "hardware EGL rapid revisions, invalid model, empty model and switching runs", () =>
        {
            var tools = new AiSuiteTools(Path.Combine(settings.WorkDirectory, "live-stress-a"));
            tools.Live = new AiLiveRun(tools); tools.Live.Commit(tools.Studio, "initial");
            tools.ExecuteAsync("add_feature", JsonNode.Parse("""{"type":"Primitive","fields":{}}""")!, default).GetAwaiter().GetResult();
            string id = tools.Studio.Features[0].Id;
            using var viewer = new Viewer(tools.Root);
            viewer.Wait(x => x["ev"]!.GetValue<string>() == "ready");
            var first = viewer.Wait(x => x["ev"]!.GetValue<string>() == "frame" && x["revision"]!.GetValue<long>() == 2);
            viewer.Send("ack");
            for (int i = 0; i < 10; i++)
                tools.ExecuteAsync("edit_feature", new JsonObject { ["id"] = id, ["fields"] = new JsonObject { ["SizeX"] = i + 2d } }, default).GetAwaiter().GetResult();
            var rapid = viewer.Wait(x => x["ev"]!.GetValue<string>() == "frame" && x["revision"]!.GetValue<long>() == 12);
            Check(first["mesh_textures"]!.GetValue<long>() == rapid["mesh_textures"]!.GetValue<long>(), "rapid updates leaked textures");
            viewer.Send("ack");
            // Deliberately corrupt only this test's immutable publication; the prior mesh must survive.
            tools.Live.Commit(tools.Studio, "corrupt-fixture");
            File.WriteAllText(tools.Live.CurrentPath!, "{broken");
            viewer.Wait(x => x["ev"]!.GetValue<string>() == "ai-preview-error");
            viewer.Send("orbit 1 0");
            var retained = viewer.Wait(x => x["ev"]!.GetValue<string>() == "frame");
            Check(retained["revision"]!.GetValue<long>() == 12, "invalid model displaced valid preview");
            viewer.Send("ack");
            tools.Live.Commit(tools.Studio, "recovered-fixture");
            viewer.Wait(x => x["ev"]!.GetValue<string>() == "frame" && x["revision"]!.GetValue<long>() == 14);
            viewer.Send("ack");
            tools.ExecuteAsync("remove_feature", new JsonObject { ["id"] = id }, default).GetAwaiter().GetResult();
            var empty = viewer.Wait(x => x["ev"]!.GetValue<string>() == "frame" && x["revision"]!.GetValue<long>() == 15);
            Check(empty["tris"]!.GetValue<int>() == 0, "empty removal kept the old geometry");
            viewer.Send("ack");
            var other = new AiSuiteTools(Path.Combine(settings.WorkDirectory, "live-stress-b"));
            other.Live = new AiLiveRun(other);
            other.ExecuteAsync("add_feature", JsonNode.Parse("""{"type":"Primitive","fields":{"SizeZ":9}}""")!, default).GetAwaiter().GetResult();
            viewer.Send("follow " + other.Root);
            viewer.Wait(x => x["ev"]!.GetValue<string>() == "frame" && x["runId"]?.GetValue<string>() == other.Live.RunId);
            viewer.Send("ack");
            tools.Live.Commit(tools.Studio, "old-run-late");
            viewer.Send("orbit 1 0");
            var switched = viewer.Wait(x => x["ev"]!.GetValue<string>() == "frame");
            Check(switched["runId"]!.GetValue<string>() == other.Live.RunId, "old run overwrote active run");
            viewer.Send("ack");
        });

        registry.Add("ai-live", "hardware EGL viewer shows every revision in order, none skipped (po: a pane follows each step)", () =>
        {
            var tools = new AiSuiteTools(Path.Combine(settings.WorkDirectory, "live-every-step"));
            tools.Live = new AiLiveRun(tools); tools.Live.Commit(tools.Studio, "initial");
            tools.ExecuteAsync("add_feature", JsonNode.Parse("""{"type":"Primitive","fields":{}}""")!, default).GetAwaiter().GetResult();
            string id = tools.Studio.Features[0].Id;
            using var viewer = new Viewer(tools.Root);
            viewer.Wait(x => x["ev"]!.GetValue<string>() == "ready");
            viewer.Wait(x => x["ev"]!.GetValue<string>() == "ai-preview" && x["revision"]!.GetValue<long>() == 2);
            // ten commits in a burst, far faster than the viewer loads: each one must still get its turn on screen
            for (int i = 0; i < 10; i++)
                tools.ExecuteAsync("edit_feature", new JsonObject { ["id"] = id, ["fields"] = new JsonObject { ["SizeX"] = i + 2d } }, default).GetAwaiter().GetResult();
            var seen = new List<long>();
            while (seen.Count < 10)
            {
                var ev = viewer.Wait(x => x["ev"]!.GetValue<string>() == "ai-preview");
                seen.Add(ev["revision"]!.GetValue<long>());
            }
            Check(string.Join(",", seen) == "3,4,5,6,7,8,9,10,11,12", "viewer skipped or reordered revisions: " + string.Join(",", seen));
        });
        registry.Add("ai-live", "hardware EGL cache-eligible 49k-triangle commit latency", () =>
        {
            var tools = new AiSuiteTools(Path.Combine(settings.WorkDirectory, "live-latency"));
            tools.Live = new AiLiveRun(tools);
            tools.ExecuteAsync("add_feature", JsonNode.Parse("""{"type":"Primitive","fields":{"SizeX":8,"SizeY":8,"SizeZ":8}}""")!, default).GetAwaiter().GetResult();
            string id = tools.Studio.Features[0].Id;
            tools.ExecuteAsync("add_feature", JsonNode.Parse("""{"type":"Subdivide","fields":{"Levels":6}}""")!, default).GetAwaiter().GetResult();
            using var viewer = new Viewer(tools.Root, "1024x576");
            viewer.Wait(x => x["ev"]!.GetValue<string>() == "ready");
            var initial = viewer.Wait(x => x["ev"]!.GetValue<string>() == "frame" && x["revision"]!.GetValue<long>() == 2);
            Check(initial["tris"]!.GetValue<int>() is >= 40000 and <= 100000, "benchmark geometry outside intended budget");
            viewer.Send("ack");
            var samples = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                tools.ExecuteAsync("edit_feature", new JsonObject { ["id"] = id, ["fields"] = new JsonObject { ["SizeX"] = 9d + i } }, default).GetAwaiter().GetResult();
                long revision = tools.Live.Revision;
                var frame = viewer.Wait(x => x["ev"]!.GetValue<string>() == "frame" && x["revision"]!.GetValue<long>() == revision);
                samples.Add(frame["commit_to_frame_ms"]!.GetValue<double>());
                viewer.Send("ack");
            }
            samples.Sort();
            Console.WriteLine($"49k triangle live preview at 1024x576: commit-to-frame p50 {samples[2]:F1} ms, p95 {samples[4]:F1} ms");
            File.WriteAllText(Path.Combine(settings.WorkDirectory, "live-latency.json"), new JsonObject {
                ["triangles"] = initial["tris"]!.GetValue<int>(), ["width"] = 1024, ["height"] = 576,
                ["samplesMs"] = new JsonArray(samples.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["p50Ms"] = samples[2], ["p95Ms"] = samples[4]
            }.ToJsonString());
            Check(samples[4] <= 500, "cache-eligible commit-to-frame p95 exceeded the 500 ms target");
        });

        registry.Add("ai-live", "hardware EGL persistent viewer follows provider operations and retains camera", () =>
        {
            string root = Path.Combine(settings.WorkDirectory, "live-viewer");
            using var viewer = new Viewer(root);
            var ready = viewer.Wait(x => x["ev"]!.GetValue<string>() == "ready");
            string shm = ready["shm"]!.GetValue<string>();
            var options = new AiOptions { Command = "create", Model = "fixture", Endpoint = "http://localhost:5678/v1/", Output = root, Prompt = "five growing blocks", Calls = 8 };
            var build = Task.Run(() => new AiSession(options, new HttpClient(new FiveOperations(delay: true))).RunAsync(default));
            var hashes = new HashSet<string>();
            var byRevision = new Dictionary<long, string>();
            var revisions = new HashSet<long>();
            float? yaw = null;
            long? textureCount = null, targetCount = null;
            var latencies = new List<double>();
            while (!build.IsCompleted || revisions.Count < 5 || !revisions.Contains(7))
            {
                var frame = viewer.Wait(x => x["ev"]!.GetValue<string>() == "frame");
                long revision = frame["revision"]!.GetValue<long>();
                if (revision >= 2)
                {
                    revisions.Add(revision);
                    textureCount ??= frame["mesh_textures"]!.GetValue<long>();
                    targetCount ??= frame["render_target_objects"]!.GetValue<long>();
                    Check(frame["mesh_textures"]!.GetValue<long>() == textureCount && frame["render_target_objects"]!.GetValue<long>() == targetCount, "repeated swaps leaked GL resources");
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(shm)));
                    hashes.Add(hash); byRevision[revision] = hash;
                    latencies.Add(frame["commit_to_frame_ms"]!.GetValue<double>());
                    if (yaw is null)
                    {
                        viewer.Send("orbit 20 0");
                        yaw = frame["yaw"]!.GetValue<float>() + 7;
                    }
                    else Check(Math.Abs(frame["yaw"]!.GetValue<float>() - yaw.Value) < .01, "revision reset the camera");
                }
                viewer.Send("ack");
            }
            Check(build.GetAwaiter().GetResult() == 0 && hashes.Count >= 4, "model did not visibly evolve");
            Check(byRevision[6] == byRevision[7], "restoring final checkpoint changed its geometry");
            Console.WriteLine($"live preview: {revisions.Count} revisions, {hashes.Count} image hashes, max commit-to-frame {latencies.Max():F1} ms");
            // Detach preserves the last preview and the provider result; reconnect replays it.
            viewer.Send("follow");
            viewer.Wait(x => x["ev"]!.GetValue<string>() == "ai-follow");
            // An emptied pane shows nothing: the last revision must not stay on screen.
            var emptied = viewer.Wait(x => x["ev"]!.GetValue<string>() == "frame");
            Check(emptied["tris"]!.GetValue<int>() == 0, "bare follow kept the last model on screen");
            viewer.Send("ack");
            viewer.Send("follow " + root);
            viewer.Wait(x => x["ev"]!.GetValue<string>() == "ai-preview" && x["revision"]!.GetValue<long>() == 7);
            Check(viewer.HardwareEgl, "viewer did not use windowless EGL");
        });

    }

    static string Command(string root, string runId, string command, JsonObject? extra = null)
    {
        string id = $"{DateTime.UtcNow.Ticks:D20}-{Guid.NewGuid():N}";
        var body = extra ?? new JsonObject();
        body["schemaVersion"] = 1; body["runId"] = runId; body["id"] = id; body["command"] = command;
        Directory.CreateDirectory(Path.Combine(root, ".live/commands"));
        AiLiveRun.AtomicWrite(Path.Combine(root, ".live/commands", id + ".json"), body.ToJsonString());
        return id;
    }
    static JsonObject Acknowledged(string root, string id)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 3000)
        {
            foreach (string line in File.ReadAllLines(Path.Combine(root, ".live/events.jsonl")))
                if (JsonNode.Parse(line) is JsonObject ev && ev["control"]?["commandId"]?.GetValue<string>() == id) return ev;
            Thread.Sleep(10);
        }
        throw new Exception("command was not acknowledged: " + id);
    }
    static JsonObject Call(string id, double size) => new() { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = "add_feature",
        ["arguments"] = new JsonObject { ["type"] = "Primitive", ["fields"] = new JsonObject { ["SizeX"] = size } }.ToJsonString() } };

    /// <summary>Provider script: callback sees call number and request body; may block to stage controls.</summary>
    sealed class Scripted(Func<int, string, JsonObject> reply) : HttpMessageHandler
    {
        int calls;
        public readonly List<string> Bodies = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = await request.Content!.ReadAsStringAsync(ct);
            Bodies.Add(body);
            var message = await Task.Run(() => reply(++calls, body), ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = message }) }.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    sealed class FiveOperations(bool delay = false) : HttpMessageHandler
    {
        int calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (delay) await Task.Delay(400, ct);
            calls++;
            var message = calls <= 5 ? new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = new JsonArray(new JsonObject {
                ["id"] = "operation-" + calls, ["type"] = "function", ["function"] = new JsonObject { ["name"] = "add_feature",
                ["arguments"] = new JsonObject { ["type"] = "Primitive", ["fields"] = new JsonObject { ["SizeX"] = calls * 3, ["SizeY"] = calls * 2, ["SizeZ"] = calls } }.ToJsonString() }
            }) } : new JsonObject { ["role"] = "assistant", ["content"] = "Five editable blocks built." };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = message }) }.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
    sealed class WaitingProvider : HttpMessageHandler
    {
        public ManualResetEventSlim Started = new();
        public bool Cancelled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Started.Set();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            throw new InvalidOperationException();
        }
    }

    sealed class Viewer : IDisposable
    {
        readonly System.Diagnostics.Process process;
        readonly System.Threading.Channels.Channel<JsonObject> events = System.Threading.Channels.Channel.CreateUnbounded<JsonObject>();
        readonly Task output;
        readonly Task<string> errors;
        public bool HardwareEgl;
        public Viewer(string root, string size = "192x128")
        {
            var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { typeof(Marionette.Desktop.Program).Assembly.Location, "--serve", "--watch-run", root, "--size", size }) start.ArgumentList.Add(arg);
            start.Environment.Remove("DISPLAY"); start.Environment.Remove("WAYLAND_DISPLAY");
            process = System.Diagnostics.Process.Start(start)!;
            errors = process.StandardError.ReadToEndAsync();
            output = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (line.Contains("EGL device/pbuffer")) HardwareEgl = true;
                    if (line.StartsWith('@') && JsonNode.Parse(line[1..]) is JsonObject ev) await events.Writer.WriteAsync(ev);
                }
                events.Writer.TryComplete();
            });
        }
        public void Send(string command) { process.StandardInput.WriteLine(command); process.StandardInput.Flush(); }
        public JsonObject Wait(Func<JsonObject, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                var ev = events.Reader.ReadAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
                string kind = ev["ev"]!.GetValue<string>();
                if (predicate(ev)) return ev;
                Check(kind is not ("error" or "ai-preview-error"), "viewer error: " + ev);
                if (kind == "frame") Send("ack");
            }
        }
        public void Dispose()
        {
            if (!process.HasExited)
            {
                Send("quit");
                if (!process.WaitForExit(3000)) process.Kill(entireProcessTree: true);
            }
            process.WaitForExit(); output.GetAwaiter().GetResult();
            string stderr = errors.GetAwaiter().GetResult();
            Check(process.ExitCode == 0, "viewer failed: " + stderr);
            process.Dispose();
        }
    }

}
