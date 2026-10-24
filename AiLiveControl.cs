#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Marionette.Desktop;

/// <summary>Run-scoped durable controls and safe operation boundaries. No GL ownership.</summary>
public static class AiLiveControl
{
    // Compatibility with the first release's cancellation-only caller/test seam.
    public static Task WatchAsync(string root, AiLiveRun live, CancellationTokenSource run, CancellationToken stop)
        => new Session(root, live, run).WatchAsync(stop);

    public sealed record Direction(string Id, string Text);
    public sealed class Session
    {
        readonly object gate = new();
        readonly string root;
        readonly AiLiveRun live;
        readonly CancellationTokenSource run;
        readonly bool allowDirections;
        readonly HashSet<string> processed = new(StringComparer.Ordinal);
        readonly Queue<Direction> directions = new();
        readonly Stopwatch clock = Stopwatch.StartNew();
        bool pauseRequested, paused, closed;
        bool checkpointContinue;        // a `continue` command arrived while the run waits at a checkpoint (Lane D)
        int steps;                      // STEP while paused: changes allowed through before holding again
        double pace, lastMutation = double.NegativeInfinity;
        public Session(string root, AiLiveRun live, CancellationTokenSource run, bool allowDirections = true, double pace = 0)
        {
            this.root = root; this.live = live; this.run = run; this.allowDirections = allowDirections; this.pace = pace;
            // A starting pace is published, so the cabinet's pace control shows it before the first change.
            if (pace > 0) Publish("pace-changed", null, "initial");
        }
        public bool HasDirections { get { lock (gate) return directions.Count > 0; } }

        /// <summary>Takes a pending checkpoint-continue signal (a `continue` control command):
        /// true once per command, so a run gated at a checkpoint resumes exactly once.</summary>
        public bool TakeCheckpointContinue()
        {
            lock (gate) { bool v = checkpointContinue; checkpointContinue = false; return v; }
        }

        void Publish(string state, string? id = null, string? outcome = null)
            => live.Control(state, pauseRequested, paused, pace, directions.Count, id, outcome);

        public async Task BoundaryAsync(CancellationToken ct, bool mutation = false)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                lock (gate)
                {
                    if (pauseRequested && steps > 0)
                    {
                        // a step lets the run go on to its next change, does that one, and holds again
                        if (mutation) steps--;
                        return;
                    }
                    if (pauseRequested)
                    {
                        if (!paused) { paused = true; Publish("paused"); }
                    }
                    else
                    {
                        if (paused) { paused = false; Publish("resumed"); }
                        if (!mutation || clock.Elapsed.TotalSeconds - lastMutation >= pace) return;
                    }
                }
                await Task.Delay(25, ct);
            }
        }
        public void MutationFinished() { lock (gate) lastMutation = clock.Elapsed.TotalSeconds; }
        public IReadOnlyList<Direction> TakeDirections()
        {
            lock (gate)
            {
                if (pauseRequested) return Array.Empty<Direction>();
                var result = directions.ToArray(); directions.Clear();
                foreach (var direction in result) Publish("direction-delivered", direction.Id, "delivered-to-builder");
                return result;
            }
        }
        /// <summary>Atomically finishes the run unless a direction raced the final answer.</summary>
        public bool TryClose(string state)
        {
            lock (gate)
            {
                if (directions.Count > 0) return false;
                closed = true; live.Status(state); return true;
            }
        }
        public async Task WatchAsync(CancellationToken stop)
        {
            string legacy = Path.Combine(root, ".live/control.json");
            string directory = Path.Combine(root, ".live/commands");
            Directory.CreateDirectory(directory);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var commands = new List<JsonObject>();
                    foreach (string path in Directory.EnumerateFiles(directory, "*.json").OrderBy(p => p, StringComparer.Ordinal).Prepend(legacy))
                    {
                        try
                        {
                            if (!File.Exists(path) || new FileInfo(path).Length > 16384) continue;
                            if (JsonNode.Parse(await File.ReadAllTextAsync(path, stop)) is not JsonObject command) continue;
                            string? id = command["id"]?.GetValue<string>() ?? (path == legacy ? "legacy-cancel" : null);
                            if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || processed.Contains(id)) continue;
                            if (command["schemaVersion"]?.GetValue<int>() != 1 || command["runId"]?.GetValue<string>() != live.RunId) continue;
                            if (command["command"] is not JsonValue kind || !kind.TryGetValue<string>(out _)) continue;
                            command["id"] = id;
                            commands.Add(command);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
                    }
                    // A queued cancel wins over pause/direction/pace commands from this poll.
                    foreach (var command in commands.OrderByDescending(c => c["command"]?.GetValue<string>() == "cancel"))
                    {
                        lock (gate)
                        {
                            string id = command["id"]!.GetValue<string>();
                            if (!processed.Add(id)) continue;
                            if (closed) { Publish("control-rejected", id, "run-finished"); continue; }
                            try
                            {
                                switch (command["command"]?.GetValue<string>())
                                {
                                    case "cancel": Publish("cancelling", id, "accepted"); run.Cancel(); return;
                                    case "continue":
                                        // Lane D: unblocks a run waiting at a checkpoint. Not a direction
                                        // (it carries no modelling text), so it is accepted even when
                                        // directions are locked, and ScriptLive polls TakeCheckpointContinue.
                                        checkpointContinue = true; Publish("checkpoint-continue", id, "accepted"); break;
                                    case "pause": pauseRequested = true; Publish("pausing", id, "accepted"); break;
                                    case "resume": pauseRequested = paused = false; steps = 0; Publish("resumed", id, "accepted"); break;
                                    case "step":
                                        if (!pauseRequested) { Publish("control-rejected", id, "step-only-while-paused"); break; }
                                        steps = Math.Min(steps + 1, 8); Publish("stepping", id, "accepted"); break;
                                    case "pace":
                                        double seconds = command["seconds"]?.GetValue<double>() ?? double.NaN;
                                        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 30) { Publish("control-rejected", id, "pace-must-be-0-to-30-seconds"); break; }
                                        pace = seconds; Publish("pace-changed", id, "accepted"); break;
                                    case "instruct":
                                        string? text = command["text"]?.GetValue<string>();
                                        if (!allowDirections) { Publish("control-rejected", id, "judged-rubric-is-locked"); break; }
                                        if (string.IsNullOrWhiteSpace(text) || text.Length > 2000 || directions.Count >= 16)
                                        { Publish("control-rejected", id, "invalid-or-full-direction-queue"); break; }
                                        directions.Enqueue(new Direction(id, text.Trim())); Publish("direction-queued", id, "accepted"); break;
                                    default: Publish("control-rejected", id, "unknown-command"); break;
                                }
                            }
                            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
                            { Publish("control-rejected", id, "invalid-command-value"); }
                        }
                    }
                    await Task.Delay(50, stop);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }
}
