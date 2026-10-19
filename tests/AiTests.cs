using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Marionette.Desktop;

internal static class AiTests
{
    public static void Register( TestRegistry r, TestSettings settings )
    {
        void Check(string name, Action body) => r.Add("ai", name, body);
        void Assert(bool b) { if (!b) throw new Exception("AI assertion failed"); }
        void Throws(Action body) { try { body(); } catch { return; } throw new Exception("Expected rejection"); }
        Check("rubric rejects missing evidence and invalid scores", () => {
            var rubric = AiRubric.Parse("{\"target\":9,\"floor\":7,\"categories\":[{\"name\":\"form\",\"weight\":1,\"description\":\"silhouette\"}]}");
            Throws(() => rubric.Score(JsonNode.Parse("{\"categories\":[{\"name\":\"form\",\"score\":10,\"evidence\":\"\",\"fix\":\"\"}]}")!));
            Throws(() => rubric.Score(JsonNode.Parse("{\"categories\":[{\"name\":\"form\",\"score\":11,\"evidence\":\"front\",\"fix\":\"\"}]}")!));
            Assert(rubric.Score(JsonNode.Parse("{\"categories\":[{\"name\":\"form\",\"score\":9,\"evidence\":\"front silhouette reads\",\"fix\":\"\"}]}")!).Passed);
        });
        Check("rubric rejects duplicates and invalid weights", () => {
            Throws(() => AiRubric.Parse("{\"target\":9,\"floor\":7,\"categories\":[]}"));
            Throws(() => AiRubric.Parse("{\"target\":9,\"floor\":7,\"categories\":[{\"name\":\"a\",\"weight\":0,\"description\":\"a\"}]}"));
            Throws(() => AiRubric.Parse("{\"target\":9,\"floor\":7,\"categories\":[{\"name\":\"a\",\"weight\":1,\"description\":\"a\"},{\"name\":\"a\",\"weight\":1,\"description\":\"a\"}]}"));
        });
        Check("plateau uses best score and retains earlier checkpoint", () => {
            var loop = new AiProgress(2, 0.2);
            Assert(!loop.Add(8)); Assert(!loop.Add(7)); Assert(loop.Add(7.5)); Assert(loop.BestRound == 0);
            Assert(!loop.Add(8.5)); Assert(loop.BestRound == 3);
        });
        Check("run paths reject escapes and links", () => {
            string root = Path.Combine(settings.WorkDirectory, "ai-paths"); Directory.CreateDirectory(root);
            var tools = new AiSuiteTools(root);
            Throws(() => tools.Owned("../escape")); Throws(() => tools.Owned("/etc/passwd"));
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), "/tmp/opencode");
            Throws(() => tools.Owned("link/escape"));
        });
        Check("native feature tool creates editable checkpoint", () => {
            var tools = new AiSuiteTools(Path.Combine(settings.WorkDirectory, "ai-native"));
            var feature = tools.ExecuteAsync("add_feature", JsonNode.Parse("{\"type\":\"Primitive\",\"fields\":{\"SizeX\":12,\"SizeY\":8,\"SizeZ\":4}}")!, default).GetAwaiter().GetResult();
            Assert(!feature.Contains("error", StringComparison.OrdinalIgnoreCase));
            string path = tools.SaveCheckpoint(0); Assert(File.Exists(path));
            Assert(Effigy.StudioDocument.ReadFile(path).Features.Count == 1);
        });
        Check("invalid edits roll back and frozen features reject changes", () => {
            var tools = new AiSuiteTools(Path.Combine(settings.WorkDirectory, "ai-rollback"));
            tools.ExecuteAsync("add_feature", JsonNode.Parse("{\"type\":\"Primitive\",\"fields\":{}}")!, default).GetAwaiter().GetResult();
            string original = tools.Inspect(), id = tools.Studio.Features[0].Id;
            Throws(() => tools.ExecuteAsync("edit_feature", new JsonObject { ["id"] = id, ["fields"] = new JsonObject { ["SizeX"] = 15, ["NO_SUCH_FIELD"] = 1 } }, default).GetAwaiter().GetResult());
            Assert(tools.Inspect() == original);
            tools.Freeze(id);
            Throws(() => tools.ExecuteAsync("remove_feature", new JsonObject { ["id"] = id }, default).GetAwaiter().GetResult());
            Throws(() => tools.ExecuteAsync("add_feature", JsonNode.Parse("{\"type\":\"Import\",\"fields\":{}}")!, default).GetAwaiter().GetResult());
        });
        Check("references reject private and transition addresses", () => {
            foreach (string ip in new[]{"127.0.0.1", "10.0.0.2", "169.254.169.254", "192.168.0.1", "192.0.0.192", "100.64.0.1", "::1", "::ffff:127.0.0.1", "fc00::1", "2002:7f00:1::", "2001:db8::1"}) Assert(!AiResearch.PublicAddress(IPAddress.Parse(ip)));
            Assert(AiResearch.PublicAddress(IPAddress.Parse("8.8.8.8")));
            Throws(() => new AiResearch().FetchAsync("http://localhost/private", default).GetAwaiter().GetResult());
        });
        Check("configuration rejects unknown flags and unsafe providers", () => {
            Throws(() => AiOptions.Parse(new[]{"create", "--no-such-option"}));
            Throws(() => AiOptions.Parse(new[]{"create", "--model", "x", "--out", "new"}));
            Throws(() => new AiProvider(new("openai", "x", new Uri("http://example.com/"), "secret", true), new HttpClient()));
            Throws(() => new AiOptions { Model = "x", Endpoint = "https://other-company.example/v1/" }.Connection());
            Assert(new AiOptions { Model = "x", Endpoint = "http://localhost:11434/v1/" }.Connection().Key == "");
        });
        Check("whole native creation uses provider loop and writes result", () => {
            var handler = new ScriptHandler();
            var options = new AiOptions { Command = "create", Model = "fixture", Endpoint = "http://localhost:5678/v1/", Output = Path.Combine(settings.WorkDirectory, "ai-whole"), Prompt = "make a test block", Calls = 10 };
            Assert(new AiSession(options, new HttpClient(handler)).RunAsync(default).GetAwaiter().GetResult() == 0);
            Assert(File.Exists(Path.Combine(options.Output, "task-00-best.effigy")));
            Assert(handler.BuildCalls == 2);
        });
        Check("budget stop preserves generated editable checkpoint", () => {
            var options = new AiOptions { Command = "create", Model = "fixture", Endpoint = "http://localhost:5678/v1/", Output = Path.Combine(settings.WorkDirectory, "ai-budget"), Prompt = "make a test block", Calls = 1 };
            Throws(() => new AiSession(options, new HttpClient(new ScriptHandler())).RunAsync(default).GetAwaiter().GetResult());
            Assert(File.Exists(Path.Combine(options.Output, "round-00.effigy")));
            Assert(JsonNode.Parse(File.ReadAllText(Path.Combine(options.Output, "stopped.json")))!["targetMet"]!.GetValue<bool>() == false);
        });
        r.Add("ai", "actual EGL build judge revise reaches target", () => {
            var handler = new ScriptHandler();
            var options = new AiOptions { Command = "create", Model = "fixture", Endpoint = "http://localhost:5678/v1/", Output = Path.Combine(settings.WorkDirectory, "ai-judge-egl"), Prompt = "make a test block", Judge = true, Vision = true, Calls = 15 };
            Assert(new AiSession(options, new HttpClient(handler)).RunAsync(default).GetAwaiter().GetResult() == 0);
            Assert(handler.JudgeCalls == 2 && handler.BuildCalls == 4);
            Assert(Directory.GetFiles(options.Output, "*.png").Length == 10);
            var result = JsonNode.Parse(File.ReadAllText(Path.Combine(options.Output, "task-00-result.json")))!;
            Assert(result["status"]!.GetValue<string>() == "target-met");
            var best = Effigy.StudioDocument.ReadFile(Path.Combine(options.Output, "task-00-best.effigy"));
            Assert(((Effigy.PrimitiveFeature)best.Features.Single()).SizeX.Value == 16);
        }, () => Environment.GetEnvironmentVariable("AI_REQUIRE_EGL") == "1" ? null : "Set AI_REQUIRE_EGL=1 for real windowless rendering + mocked provider integration.");
        r.Add("ai", "actual EGL plateau returns below target and best not latest", () => {
            var handler = new ScriptHandler(new[]{6, 5, 5});
            var options = new AiOptions { Command = "create", Model = "fixture", Endpoint = "http://localhost:5678/v1/", Output = Path.Combine(settings.WorkDirectory, "ai-plateau-egl"), Prompt = "make a test block", Judge = true, Vision = true, Calls = 15 };
            Assert(new AiSession(options, new HttpClient(handler)).RunAsync(default).GetAwaiter().GetResult() == 2);
            var result = JsonNode.Parse(File.ReadAllText(Path.Combine(options.Output, "task-00-result.json")))!;
            Assert(result["status"]!.GetValue<string>() == "plateau-below-target");
            Assert(!result["targetMet"]!.GetValue<bool>());
            var best = Effigy.StudioDocument.ReadFile(Path.Combine(options.Output, "task-00-best.effigy"));
            Assert(((Effigy.PrimitiveFeature)best.Features.Single()).SizeX.Value == 12);
        }, () => Environment.GetEnvironmentVariable("AI_REQUIRE_EGL") == "1" ? null : "Set AI_REQUIRE_EGL=1 for real windowless plateau regression.");
        r.Add("ai", "actual EGL budget stop records earlier best selection", () => {
            var options = new AiOptions { Command = "create", Model = "fixture", Endpoint = "http://localhost:5678/v1/", Output = Path.Combine(settings.WorkDirectory, "ai-judge-budget-egl"), Prompt = "make a test block", Judge = true, Vision = true, Calls = 4 };
            Throws(() => new AiSession(options, new HttpClient(new ScriptHandler())).RunAsync(default).GetAwaiter().GetResult());
            var stop = JsonNode.Parse(File.ReadAllText(Path.Combine(options.Output, "stopped.json")))!;
            Assert(stop["calls"]!.GetValue<int>() == 4);
            Assert(stop["bestCheckpoint"]!.GetValue<string>() == "round-00.effigy");
        }, () => Environment.GetEnvironmentVariable("AI_REQUIRE_EGL") == "1" ? null : "Set AI_REQUIRE_EGL=1 for checkpoint stop regression.");
        foreach (string kind in new[]{"openai", "anthropic", "google"})
        Check(kind + " request and tool response round trip", () => {
            var handler = new StubHandler(kind);
            using var http = new HttpClient(handler);
            var provider = new AiProvider(new AiConnection(kind, "test-model", new Uri("http://localhost:4567/"), "", true), http);
            var history = new List<AiTurn>{new("user", "make one thing")};
            var answer = provider.CompleteAsync("system", history, AiSuiteTools.Definitions, default).GetAwaiter().GetResult();
            Assert(answer.Calls.Count == 1 && answer.Calls[0].Name == "catalog");
            history.Add(answer); history.Add(AiTurn.Tool(answer.Calls[0], "catalog result"));
            provider.CompleteAsync("system", history, AiSuiteTools.Definitions, default).GetAwaiter().GetResult();
            Assert(handler.Requests.Count == 2);
            Assert(handler.Requests[1].Contains("catalog result"));
            if (kind == "google") Assert(handler.Requests[1].Contains("thoughtSignature"));
            if (kind == "anthropic") Assert(handler.Requests[1].Contains("tool_use_id"));
            if (kind == "openai") Assert(handler.Requests[1].Contains("tool_call_id"));
        });
    }

    sealed class ScriptHandler(int[]? scores = null) : HttpMessageHandler
    {
        public int BuildCalls, JudgeCalls;
        string featureId = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            string system = body["messages"]![0]!["content"]!.GetValue<string>();
            string text; JsonArray? calls = null;
            if (system.StartsWith("Design an evidence-based", StringComparison.Ordinal)) text = "{\"target\":9,\"floor\":7,\"maxRounds\":3,\"plateauRounds\":2,\"minImprovement\":0.2,\"categories\":[{\"name\":\"form\",\"weight\":1,\"description\":\"proportions against brief\"}]}";
            else if (system.StartsWith("You are an independent", StringComparison.Ordinal))
            {
                JudgeCalls++;
                // Actual PNGs must be carried to the independent judge over the protocol.
                if (!body.ToJsonString().Contains("data:image/png;base64,")) throw new Exception("No visual evidence supplied");
                int score = scores == null ? (JudgeCalls == 1 ? 6 : 9) : scores[Math.Min(scores.Length - 1, JudgeCalls - 1)];
                text = $"{{\"categories\":[{{\"name\":\"form\",\"score\":{score},\"evidence\":\"front/side render proportions\",\"fix\":\"increase width to 16\"}}]}}";
            }
            else
            {
                BuildCalls++;
                if (BuildCalls % 2 == 0)
                {
                    var toolTurn = ((JsonArray)body["messages"]!).Last(x => x?["role"]?.GetValue<string>() == "tool")!;
                    var tool = JsonNode.Parse(toolTurn["content"]!.GetValue<string>().Split('\n')[0])!;
                    if (tool["error"] != null) throw new Exception("Native tool failed: " + tool["error"]!.GetValue<string>());
                    featureId = tool["id"]!.GetValue<string>(); text = "Built the editable test block.";
                }
                else
                {
                    text = "";
                    var fields = new JsonObject { ["SizeX"] = BuildCalls == 1 ? 12 : 16, ["SizeY"] = 8, ["SizeZ"] = 4 };
                    var args = BuildCalls == 1 ? new JsonObject { ["type"] = "Primitive", ["fields"] = fields } : new JsonObject { ["id"] = featureId, ["fields"] = fields };
                    calls = new JsonArray(new JsonObject { ["id"] = "call-" + BuildCalls, ["type"] = "function", ["function"] = new JsonObject { ["name"] = BuildCalls == 1 ? "add_feature" : "edit_feature", ["arguments"] = args.ToJsonString() } });
                }
            }
            var message = new JsonObject { ["role"] = "assistant", ["content"] = text };
            if (calls != null) message["tool_calls"] = calls;
            return new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = message }) }.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    sealed class StubHandler(string kind) : HttpMessageHandler
    {
        public List<string> Requests = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(ct));
            string response = kind switch {
                "anthropic" => "{\"content\":[{\"type\":\"tool_use\",\"id\":\"call1\",\"name\":\"catalog\",\"input\":{}}]}",
                "google" => "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"functionCall\":{\"name\":\"catalog\",\"args\":{}},\"thoughtSignature\":\"sig\"}]}}]}",
                _ => "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"call1\",\"type\":\"function\",\"function\":{\"name\":\"catalog\",\"arguments\":\"{}\"}}]}}]}"
            };
            return new HttpResponseMessage(HttpStatusCode.OK){Content = new StringContent(response, Encoding.UTF8, "application/json")};
        }
    }
}
