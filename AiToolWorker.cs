#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Effigy;

namespace Marionette.Desktop;

/// <summary>Secret hygiene for child processes (F1): a spawned child that parses model
/// documents or untrusted image bytes never needs a provider key, so no key reaches
/// its environment — and therefore never its address space (/proc/self/environ) if a
/// native parser is exploited. Everything else (PATH, HOME, locale, runtime vars) is
/// inherited unchanged; DISPLAY handling stays with each caller, as before.</summary>
public static class AiChildEnvironment
{
    static readonly string[] KnownKeys =
        { "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "GEMINI_API_KEY", "BRAVE_SEARCH_API_KEY" };

    /// <summary>Remove provider keys from a not-yet-started process: the named keys above
    /// plus any variable ending in _API_KEY (other providers configured via key_env).</summary>
    public static void ScrubSecrets(ProcessStartInfo start)
    {
        foreach (string name in start.Environment.Keys.ToArray())
            if (KnownKeys.Contains(name, StringComparer.OrdinalIgnoreCase)
                || name.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase))
                start.Environment.Remove(name);
    }
}

/// <summary>Native rebuild/render isolation: cancellation kills the child, not player work.</summary>
public static class AiToolWorker
{
    public static async Task<string> ExecuteAsync(AiSuiteTools owner, string name, JsonNode args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string relative = ".ai-jobs/" + Guid.NewGuid().ToString("N");
        string directory = owner.Owned(relative); Directory.CreateDirectory(directory);
        var job = new JsonObject {
            ["name"] = name, ["arguments"] = args.DeepClone(), ["studio"] = StudioDocument.Write(owner.Studio),
            ["frozen"] = new JsonArray(owner.Frozen.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray())
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "request.json"), job.ToJsonString(), ct);
        string assembly = typeof(Program).Assembly.Location;
        string appDirectory = Path.GetDirectoryName(assembly)!;
        string app = Path.Combine(appDirectory, OperatingSystem.IsWindows() ? "marionette-desktop.exe" : "marionette-desktop");
        bool selfContained = File.Exists(app) && File.Exists(Path.Combine(appDirectory, OperatingSystem.IsWindows() ? "hostfxr.dll" : "libhostfxr.so"));
        // Framework-dependent apphosts cannot always discover a per-user .NET installation.
        // Use the current runtime's host directly; self-contained distributions need no host on PATH.
        string dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        if (!File.Exists(dotnet)) dotnet = "dotnet";
        var start = new ProcessStartInfo { FileName = selfContained ? app : dotnet, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (!selfContained) start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--ai-worker"); start.ArgumentList.Add(directory);
        // Native worker is always windowless, including when launched from a desktop terminal.
        start.Environment.Remove("DISPLAY"); start.Environment.Remove("WAYLAND_DISPLAY");
        // The worker parses model-generated documents and needs no provider key (F1).
        AiChildEnvironment.ScrubSecrets(start);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start native AI worker.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(ct); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
            throw;
        }
        await Task.WhenAll(output, error);
        await File.WriteAllTextAsync(Path.Combine(directory, "stdout.log"), await output, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "stderr.log"), await error, ct);
        string responsePath = Path.Combine(directory, "response.json");
        if (!File.Exists(responsePath) || new FileInfo(responsePath).Length > 32 * 1024 * 1024) throw new IOException("Native worker failed or exceeded response budget; prior model retained.");
        var response = JsonNode.Parse(await File.ReadAllTextAsync(responsePath, ct))!;
        if (response["error"] != null) throw new InvalidOperationException(response["error"]!.GetValue<string>());
        if (process.ExitCode != 0) throw new IOException("Native worker exited unsuccessfully.");
        ct.ThrowIfCancellationRequested();
        owner.AcceptWorker(response, name != "render", name);
        return response["result"]!.GetValue<string>();
    }

    public static int Run(string directory)
    {
        var tools = new AiSuiteTools(directory);
        JsonObject response;
        try
        {
            string path = tools.Owned("request.json");
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new ArgumentException("Native job too large.");
            var request = JsonNode.Parse(File.ReadAllText(path))!;
            string name = request["name"]!.GetValue<string>();
            if (name is not ("add_feature" or "edit_feature" or "remove_feature" or "render")) throw new ArgumentException("Unsupported native worker operation.");
            tools.Studio = StudioDocument.Read(request["studio"]!.GetValue<string>());
            tools.RejectUnownedStrokeAlpha(); // F3 belt-and-braces: before the first rebuild can read the path
            var report = tools.Studio.Rebuild();
            if (report.HasErrors) throw new ArgumentException("Invalid native worker input.");
            foreach (var id in (JsonArray)request["frozen"]!) tools.Freeze(id!.GetValue<string>());
            AiLiveRun.AtomicWrite(tools.Owned("running.json"), new JsonObject { ["pid"] = Environment.ProcessId, ["phase"] = "native-operation" }.ToJsonString());
            string result = name == "render" ? JsonSerializer.Serialize(tools.Render("evidence"))
                : tools.ExecuteAsync(name, request["arguments"]!, default).GetAwaiter().GetResult();
            string prepared = tools.Owned("preview.effigy");
            AiLiveRun.AtomicWrite(prepared, StudioDocument.Write(tools.Studio), overwrite: false);
            StudioCache.TrySave(tools.Studio, prepared, out _);
            response = new JsonObject { ["prepared"] = prepared, ["result"] = result, ["studio"] = StudioDocument.Write(tools.Studio), ["bodies"] = tools.Studio.Bodies.Count, ["vertices"] = tools.Studio.TotalVertexCount, ["faces"] = tools.Studio.TotalFaceCount };
        }
        catch (Exception ex) { response = new JsonObject { ["error"] = ex.Message }; }
        using var file = new FileStream(tools.Owned("response.json"), FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(file); writer.Write(response.ToJsonString());
        return response["error"] == null ? 0 : 1;
    }
}
