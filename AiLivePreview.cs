#nullable enable
using System;
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
        double LoadMs, string CacheNote, string PublishedAt);
    readonly object gate = new();
    readonly CancellationTokenSource stop = new();
    readonly string root;
    Update? pending;
    JsonObject? state;
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
    async Task LoadLoop()
    {
        long attempted = 0;
        string? run = null;
        using var journal = new AiLiveJournalReader(root);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var latest = journal.Poll();
                    if (latest is not null)
                    {
                        lock (gate) { if (!disposed) state = latest; }
                        string id = latest["runId"]!.GetValue<string>();
                        // A root belongs to one run. Never accept a journal graft from another run.
                        run ??= id;
                        long revision = latest["snapshot"]?["revision"]?.GetValue<long>() ?? 0;
                        if (id == run && revision > attempted)
                        {
                            attempted = revision;
                            string relative = latest["snapshot"]!["path"]!.GetValue<string>();
                            // Reuse the existing run-owned path and symlink policy.
                            var paths = new AiSuiteTools(root);
                            string path = paths.Owned(relative);
                            if (!relative.StartsWith(".live/revisions/", StringComparison.Ordinal) || !path.EndsWith(".effigy", StringComparison.Ordinal))
                                throw new IOException("Invalid preview revision path.");
                            var clock = System.Diagnostics.Stopwatch.StartNew();
                            var studio = StudioCache.Load(path, out string note);
                            var mesh = !studio.Bodies.Any(b => b.Visible && b.Mesh is { FaceCount: > 0 })
                                ? EmptyMesh() : DocumentSession.CreateEffigy(path, studio).BuildSnapshot().Mesh;
                            var update = new Update(mesh, path, id, latest["documentId"]!.GetValue<string>(), revision,
                                clock.Elapsed.TotalMilliseconds, note, latest["snapshot"]!["committedAt"]!.GetValue<string>());
                            lock (gate)
                            {
                                if (disposed) mesh.Dispose();
                                else { pending?.Mesh.Dispose(); pending = update; error = null; }
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
                await Task.Delay(100, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
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
