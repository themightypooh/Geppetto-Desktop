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
        void That(bool b, string what) { if (!b) throw new Exception("AI assertion failed: " + what); }
        void Throws(Action body) { try { body(); } catch { return; } throw new Exception("Expected rejection"); }
        Check("drawer provider options reach wire request without overriding tools", () => {
            var options = AiOptions.Parse(new[]{"chat", "--model", "fixture", "--out", Path.Combine(settings.WorkDirectory, "drawer-options"), "--endpoint", "http://localhost:5678/v1/",
                "--auth-header", "api-key", "--request-options", "{\"thinking\":{\"type\":\"disabled\"},\"reasoning_effort\":\"none\"}"});
            var connection = options.Connection();
            Assert(connection.AuthHeader == "api-key");
            using var http = new HttpClient();
            var request = new AiProvider(connection, http).Request("system", new[]{new AiTurn("user", "hi")}, new JsonArray());
            Assert(request["thinking"]?["type"]?.GetValue<string>() == "disabled");
            Assert(request["reasoning_effort"]?.GetValue<string>() == "none");
            Assert(request["model"]?.GetValue<string>() == "fixture");
            Throws(() => new AiOptions { Model = "x", Endpoint = "http://localhost:9/", RequestOptions = "{\"messages\":[]}" }.Connection());
            Throws(() => new AiOptions { Model = "x", Endpoint = "http://localhost:9/", AuthHeader = "Host" }.Connection());
        });
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
        // F3: Stroke's Alpha PNG is a kernel-read filesystem path. AI assignment is confined to
        // the run's refs/ at assignment time, and every failure mode returns ONE generic message
        // (distinct messages / echoed paths would be a file-existence + PNG-validity oracle).
        string AlphaAssign(AiSuiteTools t, string alpha)
        {
            try { t.ExecuteAsync("add_feature", new JsonObject { ["type"] = "Stroke", ["fields"] = new JsonObject { ["Alpha"] = alpha } }, default).GetAwaiter().GetResult(); }
            catch (ArgumentException ex) { return ex.Message; }
            catch (InvalidOperationException) { return ""; } // validation passed; the path-less stroke fails its rebuild instead
            return "";
        }
        Check("stroke alpha rejects a system file with one generic message", () => {
            var tools = new AiSuiteTools(Path.Combine(settings.WorkDirectory, "ai-alpha-system"));
            string msg = AlphaAssign(tools, "/etc/passwd");
            That(msg == AiSuiteTools.AlphaPngError, "generic rejection, got: " + msg);
            That(!msg.Contains("/etc/passwd"), "resolved path is never echoed");
        });
        Check("stroke alpha rejects a nonexistent outside path with the identical message", () => {
            var tools = new AiSuiteTools(Path.Combine(settings.WorkDirectory, "ai-alpha-missing"));
            string msg = AlphaAssign(tools, Path.Combine(settings.WorkDirectory, "no-such-place", "missing.png"));
            That(msg == AiSuiteTools.AlphaPngError, "identical generic rejection (oracle closed), got: " + msg);
        });
        Check("stroke alpha accepts a png inside the run refs and an empty clear", () => {
            string root = Path.Combine(settings.WorkDirectory, "ai-alpha-valid");
            var tools = new AiSuiteTools(root);
            string refs = Path.Combine(root, "refs"); Directory.CreateDirectory(refs);
            var rgb = new byte[4 * 4 * 3]; for (int i = 0; i < rgb.Length; i++) rgb[i] = 200;
            Effigy.PngWriter.WriteFile(Path.Combine(refs, "valid.png"), rgb, 4, 4);
            That(AlphaAssign(tools, "refs/valid.png") == "", "refs/ png passes assignment validation");
            That(AlphaAssign(tools, "") == "", "empty value clears and is allowed");
        });
        Check("stroke alpha rejects traversal out of refs", () => {
            var tools = new AiSuiteTools(Path.Combine(settings.WorkDirectory, "ai-alpha-traversal"));
            string msg = AlphaAssign(tools, "refs/../../escape.png");
            That(msg == AiSuiteTools.AlphaPngError, "generic rejection, got: " + msg);
        });
        Check("stroke alpha rejects a symlink inside refs pointing outside", () => {
            string root = Path.Combine(settings.WorkDirectory, "ai-alpha-symlink");
            var tools = new AiSuiteTools(root);
            string refs = Path.Combine(root, "refs"); Directory.CreateDirectory(refs);
            var rgb = new byte[4 * 4 * 3]; for (int i = 0; i < rgb.Length; i++) rgb[i] = 200;
            string outside = Path.Combine(settings.WorkDirectory, "outside-alpha.png");
            Effigy.PngWriter.WriteFile(outside, rgb, 4, 4);
            File.CreateSymbolicLink(Path.Combine(refs, "linked.png"), outside);
            string msg = AlphaAssign(tools, "refs/linked.png");
            That(msg == AiSuiteTools.AlphaPngError, "generic rejection, got: " + msg);
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
        Check("model sheet measures photos, samples colours and refuses unsourced sizes", () => {
            // a 400x100 photo: white ground, a 300 px long object, steel-grey left 2/3 and brown right 1/3
            string dir = Path.Combine(settings.WorkDirectory, "sheet-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(dir, "refs"));
            var rgb = new byte[400 * 100 * 3];
            for (int y = 0; y < 100; y++) for (int x = 0; x < 400; x++)
            {
                int i = (y * 400 + x) * 3; bool inside = x >= 50 && x < 350 && y >= 40 && y < 60;
                byte[] c = !inside ? new byte[]{255,255,255} : x < 250 ? new byte[]{120,124,128} : new byte[]{100,60,30};
                rgb[i] = c[0]; rgb[i + 1] = c[1]; rgb[i + 2] = c[2];
            }
            string png = Path.Combine(dir, "refs", "01-test.png");
            Effigy.PngWriter.WriteFile(png, rgb, 400, 100);
            var research = new AiResearch(Path.Combine(dir, "refs"));
            research.Sources.Add("https://museum.example/record/1");
            research.Photos.Add(new RefPhoto { File = "refs/01-test.png", Original = "refs/01-test.png", View = "side", Width = 400, Height = 100, Licence = "CC0", Analysis = PhotoAnalysis.Read(png) });
            var a = research.Photos[0].Analysis!;
            if (!(a.Clean && a.Horizontal && Math.Abs(a.LongPx - 300) <= 2)) throw new Exception(a.Summary().ToJsonString());
            string sheet = """
{"object":"test","overall":{"length":{"value":30,"unit":"cm","basis":"measured","source":"https://museum.example/record/1"}},
 "parts":[{"name":"blade","along":[0,0.66],"dims":{"length":{"basis":"photo","ref":"refs/01-test.png","fraction":0.66}},"cross_section":"diamond",
           "material":{"name":"steel","sample":{"ref":"refs/01-test.png"},"metal":true,"roughness":0.3,"wear":"none"}},
          {"name":"grip","along":[0.7,1],"dims":{"length":{"value":9,"unit":"cm","basis":"estimated","how":"rest of the length"}},"cross_section":"round",
           "material":{"name":"wood","sample":{"ref":"refs/01-test.png"},"metal":false,"roughness":0.8,"wear":"worn"}}],
 "build_order":["blade","grip"],"scale_cue":"about a forearm long","main_ref":"refs/01-test.png"}
""";
            var r = AiModelSheet.Build(JsonNode.Parse(sheet), research, "test");
            That(r.Errors.Count == 0, "check 1");
            var parts = r.Sheet["parts"]!.AsArray();
            That(parts[0]!["material"]!["hex"]!.GetValue<string>() == "#787c80" && parts[1]!["material"]!["hex"]!.GetValue<string>() == "#643c1e", "check 2");
            That(Math.Abs(parts[0]!["dims"]!["length"]!["mm"]!.GetValue<double>() - 198) < 0.5, "check 3");
            // 30 cm over 300 px: the 100 px tall photo stands 3.94 in tall, object centred on the origin
            string line = r.Sheet["refimage"]![0]!["line"]!.GetValue<string>();
            That(line.StartsWith("refimage side refs/01-test.png height=3.94 offset=0,0 ", StringComparison.Ordinal), line);
            That(r.Markdown.Contains("#787c80") && r.Markdown.Contains("museum.example"), "check 5");
            var bad = AiModelSheet.Build(JsonNode.Parse(sheet.Replace("https://museum.example/record/1", "https://made.up/")), research, "test");
            That(bad.Errors.Any(e => e.Contains("measured needs a source")), "check 6");
        });
        Check("research command needs a prompt and never builds", () => {
            Throws(() => AiOptions.Parse(new[]{"research", "--model", "x", "--out", "new"}));
            var o = AiOptions.Parse(new[]{"research", "--model", "x", "--out", "new", "--prompt", "rondel dagger"});
            That(o.Research && o.Command == "research", "check 7");
            That(AiSuiteTools.ResearchDefinitions.All(t => AiSuiteTools.IsResearchTool(t!["name"]!.GetValue<string>())), "check 8");
            That(!AiSuiteTools.BuilderDefinitions.Any(t => AiSuiteTools.IsResearchTool(t!["name"]!.GetValue<string>())), "check 9");
        });
        Check("prompt file supplies the prompt and conflicts with --prompt", () => {
            string file = Path.Combine(settings.WorkDirectory, "prompt-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(file, "rondel dagger");
            var o = AiOptions.Parse(new[]{"research", "--model", "x", "--out", "new", "--prompt-file", file});
            That(o.Prompt == "rondel dagger", "prompt file text becomes the prompt");
            Throws(() => AiOptions.Parse(new[]{"research", "--model", "x", "--out", "new", "--prompt", "dagger", "--prompt-file", file}));
            Throws(() => AiOptions.Parse(new[]{"research", "--model", "x", "--out", "new", "--prompt-file", file + ".missing"}));
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
        Check("builder can use more than 24 tool rounds within its explicit budget", () => {
            var handler = new LongBuildHandler();
            var options = new AiOptions { Command = "create", Model = "fixture", Endpoint = "http://localhost:5678/v1/", Output = Path.Combine(settings.WorkDirectory, "ai-long-build"), Prompt = "make a test block", Calls = 28 };
            Assert(new AiSession(options, new HttpClient(handler)).RunAsync(default).GetAwaiter().GetResult() == 0);
            Assert(handler.Calls == 28);
            Assert(File.Exists(Path.Combine(options.Output, "task-00-best.effigy")));
        });
        Check("legacy judge flags do not grade revise render or require another provider", () => {
            var handler = new ScriptHandler();
            var options = new AiOptions { Command = "create", Model = "fixture", Endpoint = "http://localhost:5678/v1/", Output = Path.Combine(settings.WorkDirectory, "ai-no-auto-judge"), Prompt = "make a test block", Judge = true, JudgeProvider = "not-a-provider", RubricPath = "/missing/rubric.json", Target = 10, Floor = 10, Rounds = 12, Calls = 2 };
            Assert(new AiSession(options, new HttpClient(handler)).RunAsync(default).GetAwaiter().GetResult() == 0);
            Assert(handler.BuildCalls == 2);
            Assert(Directory.GetFiles(options.Output, "*.png").Length == 0);
            Assert(!Directory.GetFiles(options.Output, "*judge*.json").Any());
            Assert(!Directory.GetFiles(options.Output, "*rubric*.json").Any());
            Assert(!Directory.GetFiles(options.Output, "*backstop*.json").Any());
            var result = JsonNode.Parse(File.ReadAllText(Path.Combine(options.Output, "task-00-result.json")))!;
            Assert(result["status"]!.GetValue<string>() == "built-unjudged");
            Assert(!result["targetMet"]!.GetValue<bool>());
            Assert(result["rounds"]!.AsArray().Count == 0);
            var best = Effigy.StudioDocument.ReadFile(Path.Combine(options.Output, "task-00-best.effigy"));
            Assert(((Effigy.PrimitiveFeature)best.Features.Single()).SizeX.Value == 12);
        });
        Check("legacy judge flag cannot spend an extra call after the builder budget", () => {
            var options = new AiOptions { Command = "create", Model = "fixture", Endpoint = "http://localhost:5678/v1/", Output = Path.Combine(settings.WorkDirectory, "ai-no-judge-budget"), Prompt = "make a test block", Judge = true, Calls = 1 };
            Throws(() => new AiSession(options, new HttpClient(new ScriptHandler())).RunAsync(default).GetAwaiter().GetResult());
            var stop = JsonNode.Parse(File.ReadAllText(Path.Combine(options.Output, "stopped.json")))!;
            Assert(stop["calls"]!.GetValue<int>() == 1);
            Assert(File.Exists(Path.Combine(options.Output, "round-00.effigy")));
        });
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

    sealed class LongBuildHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var message = new JsonObject { ["role"] = "assistant", ["content"] = "Built." };
            if (Calls <= 27)
            {
                string name = Calls == 27 ? "add_feature" : "catalog";
                var args = Calls == 27 ? new JsonObject { ["type"] = "Primitive", ["fields"] = new JsonObject() } : new JsonObject();
                message["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "tool-" + Calls, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args.ToJsonString() } });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = message }) }.ToJsonString(), Encoding.UTF8, "application/json") });
        }
    }

    sealed class ScriptHandler : HttpMessageHandler
    {
        public int BuildCalls;
        string featureId = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            string system = body["messages"]![0]!["content"]!.GetValue<string>();
            string text; JsonArray? calls = null;
            if (!system.StartsWith("You are Geppetto's creation assistant.", StringComparison.Ordinal))
                throw new Exception("Unexpected non-builder provider call: " + system);
            foreach (string removed in new[] { "LOCKED RUBRIC", "primitive pile", "primary forms", "TOPOLOGY RULES" })
                if (system.Contains(removed)) throw new Exception("Prescriptive workflow was injected: " + removed);
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
