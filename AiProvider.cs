#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Marionette.Desktop;

// No secrets are serialized: the CLI resolves API-key environment variables at startup.
public sealed record AiConnection(string Kind, string Model, Uri Endpoint, string Key, bool Vision);
public sealed record AiCall(string Id, string Name, JsonNode Arguments);
public sealed record AiTurn(string Role, string Text)
{
    public List<string> Images { get; init; } = new();
    public List<AiCall> Calls { get; init; } = new();
    public JsonNode? Native { get; init; }
    public string? CallId { get; init; }
    public string? ToolName { get; init; }
    public static AiTurn Tool(AiCall call, string text) => new("tool", text) { CallId = call.Id, ToolName = call.Name };
}

/// <summary>Official HTTP wire formats, not an emulation of any provider's consumer chat app.</summary>
public sealed class AiProvider
{
    public AiConnection Connection { get; }
    readonly HttpClient http;
    public AiProvider(AiConnection connection, HttpClient http)
    {
        if (connection.Kind is not ("openai" or "anthropic" or "google")) throw new ArgumentException("Provider must be openai, anthropic, or google. Use openai for compatible/local servers.");
        if (string.IsNullOrWhiteSpace(connection.Model)) throw new ArgumentException("A model ID is required.");
        if (connection.Endpoint.Scheme != "https" && !(connection.Endpoint.Scheme == "http" && connection.Endpoint.IsLoopback))
            throw new ArgumentException("Provider endpoints require HTTPS, except loopback local servers.");
        if (!string.IsNullOrEmpty(connection.Endpoint.UserInfo) || !string.IsNullOrEmpty(connection.Endpoint.Query))
            throw new ArgumentException("Do not put credentials or query strings in the endpoint.");
        Connection = connection; this.http = http;
    }

    static JsonArray Array(IEnumerable<JsonNode> items) => new(items.ToArray());
    static JsonNode Copy(JsonNode n) => n.DeepClone();
    static JsonObject TextPart(string text) => new() { ["type"] = "text", ["text"] = text };
    static string ImageData(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 12 * 1024 * 1024) throw new InvalidOperationException("Image exceeds 12 MiB.");
        return Convert.ToBase64String(File.ReadAllBytes(path));
    }

    public JsonObject Request(string system, IReadOnlyList<AiTurn> history, JsonArray tools)
    {
        string kind = Connection.Kind;
        if (!Connection.Vision && history.Any(x => x.Images.Count > 0)) throw new InvalidOperationException("This connection is not marked vision-capable. Select a vision judge.");
        var messages = new JsonArray();
        if (kind == "openai") messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        foreach (var turn in history)
        {
            // Preserve native assistant blocks (including Gemini thought signatures and Anthropic thinking).
            if (turn.Native != null) { messages.Add(Copy(turn.Native)); continue; }
            if (kind == "openai")
            {
                if (turn.Role == "tool") messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = turn.CallId, ["content"] = turn.Text });
                else
                {
                    var content = new JsonArray(TextPart(turn.Text));
                    foreach (string image in turn.Images) content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + ImageData(image) } });
                    messages.Add(new JsonObject { ["role"] = turn.Role, ["content"] = content });
                }
            }
            else if (kind == "anthropic")
            {
                var content = new JsonArray();
                if (turn.Role == "tool") content.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = turn.CallId, ["content"] = turn.Text });
                else
                {
                    content.Add(TextPart(turn.Text));
                    foreach (string image in turn.Images) content.Add(new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = ImageData(image) } });
                }
                // Consecutive tool results belong in one user message for parallel tool use.
                if (turn.Role == "tool" && messages.LastOrDefault() is JsonObject last && last["role"]?.GetValue<string>() == "user" && last["content"] is JsonArray prev && prev.Any(p => p?["type"]?.GetValue<string>() == "tool_result"))
                    prev.Add(content[0]!.DeepClone());
                else messages.Add(new JsonObject { ["role"] = turn.Role == "tool" ? "user" : turn.Role, ["content"] = content });
            }
            else
            {
                var parts = new JsonArray();
                if (turn.Role == "tool")
                {
                    var result = new JsonObject { ["name"] = turn.ToolName, ["response"] = new JsonObject { ["result"] = turn.Text } };
                    if (!string.IsNullOrEmpty(turn.CallId)) result["id"] = turn.CallId;
                    parts.Add(new JsonObject { ["functionResponse"] = result });
                }
                else
                {
                    parts.Add(new JsonObject { ["text"] = turn.Text });
                    foreach (string image in turn.Images) parts.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = "image/png", ["data"] = ImageData(image) } });
                }
                if (turn.Role == "tool" && messages.LastOrDefault() is JsonObject last && last["role"]?.GetValue<string>() == "user" && last["parts"] is JsonArray prev && prev.Any(p => p?["functionResponse"] != null))
                    foreach (var part in parts) prev.Add(part!.DeepClone());
                else messages.Add(new JsonObject { ["role"] = turn.Role == "assistant" ? "model" : "user", ["parts"] = parts });
            }
        }
        var request = kind switch
        {
            "anthropic" => new JsonObject { ["model"] = Connection.Model, ["max_tokens"] = 8192, ["system"] = system, ["messages"] = messages },
            "google" => new JsonObject { ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) }, ["contents"] = messages },
            _ => new JsonObject { ["model"] = Connection.Model, ["messages"] = messages }
        };
        if (tools.Count > 0)
        {
            if (kind == "google") request["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = tools.DeepClone() });
            else if (kind == "anthropic") request["tools"] = Array(tools.Select(t => (JsonNode)new JsonObject { ["name"] = t!["name"]!.GetValue<string>(), ["description"] = t["description"]!.GetValue<string>(), ["input_schema"] = Copy(t["parameters"]!) }));
            else request["tools"] = Array(tools.Select(t => (JsonNode)new JsonObject { ["type"] = "function", ["function"] = Copy(t!) }));
        }
        return request;
    }

    public async Task<AiTurn> CompleteAsync(string system, IReadOnlyList<AiTurn> history, JsonArray tools, CancellationToken ct)
    {
        string path = Connection.Kind switch { "anthropic" => "messages", "google" => "models/" + Uri.EscapeDataString(Connection.Model) + ":generateContent", _ => "chat/completions" };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Connection.Endpoint, path));
        if (Connection.Kind == "anthropic") { request.Headers.Add("x-api-key", Connection.Key); request.Headers.Add("anthropic-version", "2023-06-01"); }
        else if (Connection.Kind == "google") request.Headers.Add("x-goog-api-key", Connection.Key);
        else if (Connection.Key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Connection.Key);
        request.Content = new StringContent(Request(system, history, tools).ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        // Do not print error bodies: gateways can echo credentials or private request content.
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{Connection.Kind} returned HTTP {(int)response.StatusCode}. Check endpoint, model, credentials and quota.");
        var json = JsonNode.Parse(await ReadBoundedAsync(response.Content, 32 * 1024 * 1024, ct))!;
        return ParseResponse(Connection.Kind, json);
    }

    public static AiTurn ParseResponse(string kind, JsonNode json)
    {
        var calls = new List<AiCall>(); var text = new StringBuilder(); JsonNode native;
        if (kind == "openai")
        {
            native = json["choices"]?[0]?["message"] ?? throw new InvalidOperationException("No assistant response (possibly blocked or unsupported model).");
            text.Append(native["content"]?.GetValue<string>() ?? "");
            if (native["tool_calls"] is JsonArray tc) foreach (var call in tc)
                calls.Add(new(call!["id"]!.GetValue<string>(), call["function"]!["name"]!.GetValue<string>(), JsonNode.Parse(call["function"]!["arguments"]!.GetValue<string>())!));
        }
        else
        {
            native = kind == "google" ? json["candidates"]?[0]?["content"] ?? throw new InvalidOperationException("No candidate (possibly safety-blocked).")
                : new JsonObject { ["role"] = "assistant", ["content"] = json["content"]?.DeepClone() ?? throw new InvalidOperationException("No message content.") };
            var blocks = (JsonArray)native[kind == "google" ? "parts" : "content"]!;
            foreach (var block in blocks)
            {
                if (block?["text"] != null && block["thought"]?.GetValue<bool>() != true) text.AppendLine(block["text"]!.GetValue<string>());
                if (kind == "anthropic" && block?["type"]?.GetValue<string>() == "tool_use") calls.Add(new(block["id"]!.GetValue<string>(), block["name"]!.GetValue<string>(), Copy(block["input"]!)));
                if (kind == "google" && block?["functionCall"] is JsonObject fc) calls.Add(new(fc["id"]?.GetValue<string>() ?? "", fc["name"]!.GetValue<string>(), fc["args"]?.DeepClone() ?? new JsonObject()));
            }
        }
        if (text.Length == 0 && calls.Count == 0) throw new InvalidOperationException("Model returned no usable text or tool calls.");
        return new("assistant", text.ToString()) { Calls = calls, Native = native.DeepClone() };
    }

    public static async Task<string> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidOperationException("Response exceeds size limit.");
        using var stream = await content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream();
        byte[] chunk = new byte[16384]; int n;
        while ((n = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > limit) throw new InvalidOperationException("Response exceeds size limit.");
            buffer.Write(chunk, 0, n);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
