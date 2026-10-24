#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Effigy;

namespace Marionette.Desktop;

/// <summary>Single writer for immutable AI revisions and a recoverable event journal.</summary>
public sealed class AiLiveRun
{
    readonly object gate = new();
    readonly AiSuiteTools owner;
    readonly Action<string>? emit;
    long sequence, revision;
    JsonObject? current;
    string? operationId;
    bool cancelling;
    JsonObject controls = new() { ["pauseRequested"] = false, ["paused"] = false, ["paceSeconds"] = 0d, ["pendingDirections"] = 0 };
    public string RunId { get; } = Guid.NewGuid().ToString("N");
    public string DocumentId { get; } = Guid.NewGuid().ToString("N");
    public long Revision => revision;
    public string? CurrentPath => current is null ? null : owner.Owned(current["path"]!.GetValue<string>());
    public AiLiveRun(AiSuiteTools owner, Action<string>? emit = null)
    {
        this.owner = owner; this.emit = emit;
        Directory.CreateDirectory(owner.Owned(".live/revisions"));
        Status("starting");
    }

    public void Status(string state, string? operation = null, string? outcome = null)
    {
        lock (gate)
        {
            if (state == "cancelling") cancelling = true;
            if (cancelling && state is not ("cancelling" or "cancelled" or "failed")) return;
            if (state == "building") operationId = Guid.NewGuid().ToString("N");
            else if (operation is null) operationId = null;
            Append(state, operation, outcome, current);
        }
    }

    public void Control(string state, bool pauseRequested, bool paused, double pace, int pending, string? id, string? outcome)
    {
        lock (gate)
        {
            controls = new JsonObject { ["pauseRequested"] = pauseRequested, ["paused"] = paused,
                ["paceSeconds"] = pace, ["pendingDirections"] = pending, ["commandId"] = id, ["outcome"] = outcome };
            if (state == "cancelling") { Status(state, outcome: outcome); return; }
            // Control events never start/clear an operation identity; an in-flight build keeps it.
            if (!cancelling) Append(state, null, outcome, current);
        }
    }

    /// <param name="fileLabel">Lane D user edits: a fixed label (e.g. "user") instead of a random
    /// suffix, so a user-edit revision reads <c>00000014-user.effigy</c>. The revision number
    /// still makes it unique; the preview loader matches the <c>DDDDDDDD-*.effigy</c> prefix.</param>
    public void Commit(PartStudio studio, string operation, string? prepared = null, string? checkpoint = null, string[]? affected = null, string? fileLabel = null)
    {
        lock (gate)
        {
            if (cancelling) throw new OperationCanceledException("Run cancellation accepted; late revision discarded.");
            long next = revision + 1;
            string relative = fileLabel is null
                ? $".live/revisions/{next:D8}-{Guid.NewGuid():N}.effigy"
                : $".live/revisions/{next:D8}-{fileLabel}.effigy";
            string destination = owner.Owned(relative);
            string json = StudioDocument.Write(studio);
            AtomicWrite(destination, json, overwrite: false);
            string cacheNote = "rebuild required";
            bool cached = false;
            // Worker payload contains native document JSON; cached geometry is optional and
            // the kernel validates its key again on restore. A cache miss never loses a revision.
            try
            {
                if (prepared is not null && File.Exists(StudioCache.PathFor(prepared)))
                {
                    File.Copy(StudioCache.PathFor(prepared), StudioCache.PathFor(destination), overwrite: false);
                    cached = true; cacheNote = "worker geometry cache copied";
                }
                else if (prepared is null) cached = StudioCache.TrySave(studio, destination, out cacheNote);
            }
            catch (IOException) { cacheNote = "cache copy unavailable; rebuild required"; }
            catch (UnauthorizedAccessException) { cacheNote = "cache unavailable; rebuild required"; }
            var snapshot = new JsonObject {
                ["committedAt"] = DateTimeOffset.UtcNow, ["revision"] = next, ["path"] = relative, ["cached"] = cached, ["cacheNote"] = cacheNote,
                ["bytes"] = new FileInfo(destination).Length, ["checkpoint"] = checkpoint,
                ["affectedFeatureIds"] = new JsonArray((affected ?? Array.Empty<string>()).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["featureIds"] = new JsonArray(studio.Features.Select(f => (JsonNode?)JsonValue.Create(f.Id)).ToArray())
            };
            operationId ??= Guid.NewGuid().ToString("N");
            Append("revision", operation, "committed", snapshot);
            operationId = null;
            revision = next; current = snapshot;
        }
    }

    void Append(string state, string? operation, string? outcome, JsonObject? snapshot)
    {
        var ev = new JsonObject {
            ["schemaVersion"] = 1, ["ev"] = "ai-live", ["runId"] = RunId, ["documentId"] = DocumentId,
            ["sequence"] = sequence + 1, ["timestamp"] = DateTimeOffset.UtcNow,
            ["state"] = state, ["operationId"] = operationId, ["operation"] = operation, ["outcome"] = outcome,
            ["control"] = controls.DeepClone(), ["snapshot"] = snapshot?.DeepClone()
        };
        string line = ev.ToJsonString();
        using (var stream = new FileStream(owner.Owned(".live/events.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n"); stream.Write(bytes); stream.Flush(flushToDisk: true);
        }
        sequence++;
        // The journal is authoritative if a crash occurs between it and this index.
        try { AtomicWrite(owner.Owned(".live/latest.json"), line); }
        catch (IOException) { /* Journal remains authoritative; preview can recover without the index. */ }
        catch (UnauthorizedAccessException) { }
        try { emit?.Invoke("@" + line); } catch (IOException) { /* Detached preview does not cancel creation. */ }
    }

    public static void AtomicWrite(string path, string text, bool overwrite = true)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(text); stream.Write(bytes); stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>Reads complete records; long-lived followers retain a journal reader instead.</summary>
    public static JsonObject? ReadLatest(string root)
    {
        using var reader = new AiLiveJournalReader(root);
        return reader.Poll();
    }
}

/// <summary>Incremental reader with bounded partial-record storage. A torn tail can complete later.</summary>
public sealed class AiLiveJournalReader : IDisposable
{
    readonly string path;
    StreamReader? reader;
    readonly StringBuilder partial = new();
    bool oversized;
    JsonObject? latest;
    readonly List<JsonObject> fresh = new();
    DateTime lastPathWriteUtc = DateTime.MinValue;   // Lane D (Backlog B29): the path file's mtime when last observed
    public AiLiveJournalReader(string root) => path = Path.Combine(root, ".live/events.jsonl");
    /// <summary>The newest complete record so far (what <see cref="Poll"/> last returned).</summary>
    public JsonObject? Latest => latest;
    /// <summary>Every complete record accepted since the last call, oldest first: a follower that must show
    /// each revision in turn reads these, where <see cref="Poll"/> only tells it the newest.</summary>
    public List<JsonObject> PollNew()
    {
        Poll();
        var result = new List<JsonObject>(fresh); fresh.Clear();
        return result;
    }
    public JsonObject? Poll()
    {
        // Lane D (Backlog B29): the journal file can be replaced under a long-lived reader
        // (a new run reusing the root, a rotation). An open handle follows the old inode, so a
        // same-inode append grows the open stream's length, but a replacement does not: a path
        // length that differs from the open stream's length is a replacement, and a same-length
        // file with a newer mtime and a different first record's runId is one too. Either way
        // the reader reopens from the start and drops the old run's identity, or the new run's
        // records would be filtered out as a "journal graft" forever.
        if (reader is not null)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    long streamLen = -1;
                    try { streamLen = reader.BaseStream.Length; } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                    bool replaced = false;
                    if (streamLen >= 0 && info.Length != streamLen) replaced = true;
                    else if (streamLen >= 0 && reader.BaseStream.Position >= streamLen
                        && lastPathWriteUtc != DateTime.MinValue && info.LastWriteTimeUtc != lastPathWriteUtc)
                    {
                        string? firstRun = null;
                        try
                        {
                            using var probe = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                            if (probe.ReadLine() is string first && JsonNode.Parse(first) is JsonObject ev)
                                firstRun = ev["runId"]?.GetValue<string>();
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
                        if (firstRun is not null && latest is not null && firstRun != latest["runId"]?.GetValue<string>()) replaced = true;
                    }
                    lastPathWriteUtc = info.LastWriteTimeUtc;
                    if (replaced)
                    {
                        reader.Dispose(); reader = null; latest = null; fresh.Clear(); partial.Clear(); oversized = false;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Keep the old reader. */ }
        }
        if (reader is null)
        {
            if (!File.Exists(path)) return latest;
            reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            try { lastPathWriteUtc = new FileInfo(path).LastWriteTimeUtc; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (reader.BaseStream.Length < reader.BaseStream.Position)
        {
            reader.DiscardBufferedData(); reader.BaseStream.Position = 0; partial.Clear(); oversized = false;
        }
        int c;
        while ((c = reader.Read()) >= 0)
        {
            if (c != '\n')
            {
                if (partial.Length < 65536 && !oversized) partial.Append((char)c);
                else { oversized = true; partial.Clear(); }
                continue;
            }
            if (!oversized)
            {
                try
                {
                    if (JsonNode.Parse(partial.ToString()) is JsonObject ev && ev["schemaVersion"]?.GetValue<int>() == 1
                        && ev["ev"]?.GetValue<string>() == "ai-live" && !string.IsNullOrWhiteSpace(ev["runId"]?.GetValue<string>())
                        && !string.IsNullOrWhiteSpace(ev["documentId"]?.GetValue<string>()) && ev["sequence"]?.GetValue<long>() is > 0
                        && (latest is null || ev["runId"]!.GetValue<string>() == latest["runId"]!.GetValue<string>()
                            && ev["sequence"]!.GetValue<long>() > latest["sequence"]!.GetValue<long>())) { latest = ev; fresh.Add(ev); }
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
            }
            oversized = false; partial.Clear();
        }
        return latest;
    }
    public void Dispose() => reader?.Dispose();
}
