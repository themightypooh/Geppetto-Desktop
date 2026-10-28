#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Effigy;

namespace Marionette.Desktop;

/// <summary>One background loader and one replaceable pending CPU snapshot. Never owns GL objects.</summary>
public sealed class AiLivePreview : IDisposable
{
    public sealed record Update(ViewportMesh Mesh, string Path, string RunId, string DocumentId, long Revision,
        double LoadMs, string CacheNote, string PublishedAt, ReferenceImage[] Refs);
    readonly object gate = new();
    readonly CancellationTokenSource stop = new();
    readonly string root;
    readonly ViewportMesh.PieceCache pieces = new();   // used only by the loader thread
    Update? pending;
    JsonObject? state;
    long seek, seekLoaded;      // replay: 0 follows the latest revision, N pins revision N
    /// <summary>Replay: show revision N (from the run's kept revisions) instead of the latest; 0 goes back to following.</summary>
    public void Seek(long revision) { lock (gate) { seek = Math.Max(0, revision); seekLoaded = 0; } }
    public bool Pinned { get { lock (gate) return seek > 0; } }
    bool disposed;
    string? error;
    public string? Error { get { lock (gate) return error; } }
    public Task Completion { get; }
    public AiLivePreview(string root)
    {
        this.root = Path.GetFullPath(root);
        Completion = Task.Run(LoadLoop);
    }
    public static ViewportMesh EmptyMesh() => new() { Vertices = Array.Empty<float>(), Indices = Array.Empty<uint>(),
        Batches = Array.Empty<ViewportMesh.DrawBatch>(), Radius = 1, BoundsMin = -Vector3.One, BoundsMax = Vector3.One };
    public Update? Take()
    {
        lock (gate) { var result = pending; pending = null; return result; }
    }
    public JsonObject? State { get { lock (gate) return (JsonObject?)state?.DeepClone(); } }
    /// <summary>How long each revision stays on screen before the next queued one replaces it, so a
    /// run that commits faster than the pace (or a viewer that loads slower than it) still shows every
    /// step in turn instead of jumping to the newest. The latest revision stays as long as it is latest.</summary>
    public const int MinDwellMs = 250;
    async Task LoadLoop()
    {
        long attempted = 0;                 // the newest revision queued or loaded
        string? run = null;
        var queue = new Queue<(string Path, long Revision, string CommittedAt)>();
        var shown = System.Diagnostics.Stopwatch.StartNew();
        using var journal = new AiLiveJournalReader(root);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var fresh = journal.PollNew();
                    var latest = journal.Latest;
                    if (latest is not null)
                    {
                        lock (gate) { if (!disposed) state = latest; }
                        string id = latest["runId"]!.GetValue<string>();
                        string documentId = latest["documentId"]!.GetValue<string>();
                        // A root belongs to one run. Never accept a journal graft from another run.
                        bool joined = run is null;
                        run ??= id;
                        long revision = latest["snapshot"]?["revision"]?.GetValue<long>() ?? 0;
                        long want; bool back;
                        lock (gate) { want = seek; back = want == 0 && seekLoaded != 0; if (back) seekLoaded = 0; }
                        if (want > 0)
                        {
                            bool fresh2; lock (gate) fresh2 = seekLoaded != want;
                            if (id == run && fresh2)
                            {
                                lock (gate) seekLoaded = want;     // one try per seek, even if it fails
                                string prefix = want.ToString("D8") + "-";
                                string? file = Directory.EnumerateFiles(Path.Combine(root, ".live/revisions"), prefix + "*.effigy").FirstOrDefault();
                                if (file is null) throw new IOException($"Revision {want} is not kept.");
                                queue.Clear();
                                Publish(".live/revisions/" + Path.GetFileName(file), id, documentId, want, File.GetLastWriteTimeUtc(file).ToString("O"));
                            }
                        }
                        else if (id == run)
                        {
                            if (joined || back)
                            {
                                // joining a run (or back from scrubbing it): straight to where it is now, not through its history
                                queue.Clear(); attempted = 0;
                                if (revision > 0 && latest["snapshot"]?["path"] is { } p0)
                                { queue.Enqueue((p0.GetValue<string>(), revision, latest["snapshot"]!["committedAt"]!.GetValue<string>())); attempted = revision; }
                            }
                            else
                            {
                                // every revision the run committed since the last look, in order: none is skipped
                                foreach (var ev in fresh)
                                {
                                    if (ev["state"]?.GetValue<string>() != "revision" || ev["runId"]!.GetValue<string>() != run) continue;
                                    long r = ev["snapshot"]?["revision"]?.GetValue<long>() ?? 0;
                                    if (r <= attempted || ev["snapshot"]?["path"] is not { } p) continue;
                                    queue.Enqueue((p.GetValue<string>(), r, ev["snapshot"]!["committedAt"]!.GetValue<string>())); attempted = r;
                                }
                            }
                            bool free; lock (gate) free = pending is null;
                            // Keep every committed step. Only lightweight revision paths queue here;
                            // pending backpressure keeps at most one loaded mesh waiting for the viewer.
                            // A slow pane must not silently discard the user's model changes to catch up.
                            if (queue.Count > 0 && free && shown.ElapsedMilliseconds >= MinDwellMs)
                            {
                                var next = queue.Dequeue();
                                try { Publish(next.Path, id, documentId, next.Revision, next.CommittedAt); }
                                finally { shown.Restart(); }    // a revision that fails to load still took its turn; the next one follows
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    lock (gate)
                    {
                        if (!disposed)
                        {
                            state ??= new JsonObject();
                            error = ex.Message;
                        }
                    }
                }
                await Task.Delay(Pinned ? 20 : queue.Count > 0 ? 25 : 100, stop.Token);   // scrubbing and a backlog want a quick turnaround
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    void Publish(string relative, string id, string documentId, long revision, string committedAt)
    {
        // Reuse the existing run-owned path and symlink policy.
        var paths = new AiSuiteTools(root);
        string path = paths.Owned(relative);
        if (!relative.StartsWith(".live/revisions/", StringComparison.Ordinal) || !path.EndsWith(".effigy", StringComparison.Ordinal))
            throw new IOException("Invalid preview revision path.");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var studio = StudioCache.Load(path, out string note);
        // per-body pieces from earlier revisions: only the bodies this revision changed are re-extracted
        ViewportMesh.LivePieces = pieces;
        ViewportMesh mesh;
        try
        {
            mesh = !studio.Bodies.Any(b => b.Visible && b.Mesh is { FaceCount: > 0 })
                ? EmptyMesh() : DocumentSession.CreateEffigy(path, studio).BuildSnapshot().Mesh;
        }
        finally { ViewportMesh.LivePieces = null; }
        note += $"; {pieces.Hits} body(ies) reused, {pieces.Misses} rebuilt";
        var refs = studio.ReferenceImages.Select(r => r.Clone()).ToArray();
        var update = new Update(mesh, path, id, documentId, revision, clock.Elapsed.TotalMilliseconds, note, committedAt, refs);
        lock (gate)
        {
            if (disposed) mesh.Dispose();
            else { pending?.Mesh.Dispose(); pending = update; error = null; }
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; pending?.Mesh.Dispose(); pending = null; stop.Cancel();
        }
        // Rebuilds cannot be cancelled inside the kernel; any late CPU result is discarded.
        _ = Completion.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
    }
}
