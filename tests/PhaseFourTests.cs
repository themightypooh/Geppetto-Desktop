using System.Text.Json.Nodes;
using Marionette.Desktop;

internal static class PhaseFourTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        void Check(string name, Action action) => registry.Add("phase4", name, action);
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        Check("lane 17: timeline_control manages frames, playback, and keyframes — as run notes, not a clip", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p4-time-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            // Range
            var resRange = JsonNode.Parse(tools.ExecuteAsync("timeline_control", new JsonObject
            {
                ["action"] = "set_range",
                ["startFrame"] = 0,
                ["endFrame"] = 60,
                ["fps"] = 24
            }, default).GetAwaiter().GetResult())!;
            Require(resRange["endFrame"]?.GetValue<int>() == 60, "endFrame 60");
            Require(resRange["fps"]?.GetValue<int>() == 24, "fps 24");
            Require(resRange["backedByClip"]?.GetValue<bool>() == false, "range is not backed by a clip");
            Require(!string.IsNullOrWhiteSpace(resRange["note"]?.GetValue<string>()), "range carries the run-notes note");

            // Keyframe
            var resKey = JsonNode.Parse(tools.ExecuteAsync("timeline_control", new JsonObject
            {
                ["action"] = "add_keyframe",
                ["frame"] = 12,
                ["boneName"] = "Head"
            }, default).GetAwaiter().GetResult())!;
            Require(resKey["bone"]?.GetValue<string>() == "Head", "Head bone keyframe");
            Require(resKey["totalKeyframes"]?.GetValue<int>() == 1, "1 keyframe");
            Require(resKey["backedByClip"]?.GetValue<bool>() == false, "keyframe is not backed by a clip");

            // Playback
            var resPlay = JsonNode.Parse(tools.ExecuteAsync("timeline_control", new JsonObject
            {
                ["action"] = "playback",
                ["state"] = "play"
            }, default).GetAwaiter().GetResult())!;
            Require(resPlay["state"]?.GetValue<string>() == "play", "playing");
        });

        Check("lane 18: material_preset builds real descriptors, persists them, and validate_materials checks the file", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p4-mat-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            // Assign Metal
            var resMetal = JsonNode.Parse(tools.ExecuteAsync("material_preset", new JsonObject
            {
                ["slot"] = "Armor",
                ["preset"] = "Metal"
            }, default).GetAwaiter().GetResult())!;
            Require(resMetal["metallic"]?.GetValue<double>() == 1.0, "metallic 1.0");
            Require(resMetal["roughness"]?.GetValue<double>() == 0.2, "roughness 0.2");
            Require(resMetal["slotIndex"]?.GetValue<int>() == 0, "Armor is document slot 0");
            Require(resMetal["descriptorId"]?.GetValue<string>() == "ai-armor", "Armor descriptor id");
            Require(resMetal["materialFile"]?.GetValue<string>() == "materials.json", "materials file named");

            // Assign Skin with custom color
            var resSkin = JsonNode.Parse(tools.ExecuteAsync("material_preset", new JsonObject
            {
                ["slot"] = "Face",
                ["preset"] = "Skin",
                ["roughness"] = 0.55
            }, default).GetAwaiter().GetResult())!;
            Require(resSkin["roughness"]?.GetValue<double>() == 0.55, "skin roughness 0.55");
            Require(resSkin["slotIndex"]?.GetValue<int>() == 1, "Face is document slot 1");
            Require(resSkin["descriptorId"]?.GetValue<string>() == "ai-face", "Face descriptor id");

            // materials.json on disk holds both slots.
            string matPath = Path.Combine(root, "materials.json");
            Require(File.Exists(matPath), "materials.json written");
            var matDoc = JsonNode.Parse(File.ReadAllText(matPath))!;
            var matSlots = matDoc["slots"]!.AsArray();
            Require(matSlots.Any(s => s!["slot"]!.GetValue<string>() == "Armor"), "Armor recorded in materials.json");
            Require(matSlots.Any(s => s!["slot"]!.GetValue<string>() == "Face"), "Face recorded in materials.json");

            // Validate reads the persisted records and the real document slot names.
            var resVal = JsonNode.Parse(tools.ExecuteAsync("validate_materials", new JsonObject(), default).GetAwaiter().GetResult())!;
            Require(resVal["valid"]?.GetValue<bool>() == true, "materials valid");
            Require(resVal["slotCount"]?.GetValue<int>() == 2, "2 slots assigned");
            Require(resVal["documentSlotNames"]?.GetValue<int>() == 2, "2 document slot names");

            // Bad inputs are refused, not stored.
            bool threwRough = false;
            try
            {
                tools.ExecuteAsync("material_preset", new JsonObject
                {
                    ["slot"] = "Bad",
                    ["preset"] = "Metal",
                    ["roughness"] = 5.0
                }, default).GetAwaiter().GetResult();
            }
            catch { threwRough = true; }
            Require(threwRough, "roughness 5.0 throws");

            bool threwColor = false;
            try
            {
                tools.ExecuteAsync("material_preset", new JsonObject
                {
                    ["slot"] = "Bad",
                    ["preset"] = "Metal",
                    ["color"] = "red"
                }, default).GetAwaiter().GetResult();
            }
            catch { threwColor = true; }
            Require(threwColor, "color red throws");
        });

        Check("lane 19: modelsheet_sync measures alignment against a real sheet and persists the record", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p4-sheet-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var resViews = JsonNode.Parse(tools.ExecuteAsync("modelsheet_sync", new JsonObject
            {
                ["action"] = "get_views"
            }, default).GetAwaiter().GetResult())!;
            var views = resViews["views"]!.AsArray();
            Require(views.Count == 3, "3 orthographic views");
            Require(resViews["aligned"] is null, "get_views claims no alignment");

            // No sheet in the run: nothing to align against.
            var resNoSheet = JsonNode.Parse(tools.ExecuteAsync("modelsheet_sync", new JsonObject
            {
                ["action"] = "set_alignment",
                ["scaleRatio"] = 1.05,
                ["groundOffset"] = 2.0
            }, default).GetAwaiter().GetResult())!;
            Require(resNoSheet["status"]?.GetValue<string>() == "no_sheet", "no sheet, no alignment");

            // A real sheet (overall length 10 in) and a 10-inch cube: measured, not echoed.
            File.WriteAllText(Path.Combine(root, "model-sheet.json"), "{\"overall\":{\"length\":{\"mm\":254,\"in\":10}}}");
            tools.ExecuteAsync("add_feature", new JsonObject
            {
                ["type"] = "Primitive",
                ["fields"] = new JsonObject { ["SizeX"] = 10, ["SizeY"] = 10, ["SizeZ"] = 10 }
            }, default).GetAwaiter().GetResult();
            var size = tools.MeasureSize();
            double longest = Math.Max(size.Width, Math.Max(size.Depth, size.Height));
            var resAlign = JsonNode.Parse(tools.ExecuteAsync("modelsheet_sync", new JsonObject
            {
                ["action"] = "set_alignment"
            }, default).GetAwaiter().GetResult())!;
            double ratio = resAlign["scaleRatio"]!.GetValue<double>();
            Require(Math.Abs(ratio - longest / 10) < 0.01, "scale ratio is measured longest / sheet length");
            Require(resAlign["syncFile"]?.GetValue<string>() == "modelsheet-sync.json", "sync file named");
            Require(File.Exists(Path.Combine(root, "modelsheet-sync.json")), "modelsheet-sync.json written");

            // verify_sheet recomputes: the same ratio, and the stored record is current.
            var resVerify = JsonNode.Parse(tools.ExecuteAsync("modelsheet_sync", new JsonObject
            {
                ["action"] = "verify_sheet"
            }, default).GetAwaiter().GetResult())!;
            Require(Math.Abs(resVerify["scaleRatio"]!.GetValue<double>() - ratio) < 0.001, "verify recomputes the same ratio");
            Require(resVerify["syncRecordCurrent"]?.GetValue<bool>() == true, "stored sync record is current");
        });

        Check("lane 20: layout_preset stores a name only — no panels, nothing applied", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p4-lay-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var resDef = JsonNode.Parse(tools.ExecuteAsync("layout_preset", new JsonObject { ["action"] = "get" }, default).GetAwaiter().GetResult())!;
            Require(resDef["activePreset"]?.GetValue<string>() == "Modeling", "default is Modeling");
            Require(resDef["applied"]?.GetValue<bool>() == false, "default layout not applied");
            Require(resDef["panels"] is null, "no panels field");

            var resSculpt = JsonNode.Parse(tools.ExecuteAsync("layout_preset", new JsonObject
            {
                ["action"] = "set",
                ["preset"] = "Sculpting"
            }, default).GetAwaiter().GetResult())!;
            Require(resSculpt["activePreset"]?.GetValue<string>() == "Sculpting", "switched to Sculpting");
            Require(resSculpt["applied"]?.GetValue<bool>() == false, "Sculpting not applied");
            Require(resSculpt["panels"] is null, "still no panels field");

            bool threw = false;
            try
            {
                tools.ExecuteAsync("layout_preset", new JsonObject
                {
                    ["action"] = "set",
                    ["preset"] = "Banana"
                }, default).GetAwaiter().GetResult();
            }
            catch { threw = true; }
            Require(threw, "unknown preset throws");
        });

        Check("lane 21: selection_tool uses real totals and real adjacency", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p4-sel-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            // Create a test primitive
            tools.ExecuteAsync("add_feature", new JsonObject
            {
                ["type"] = "Primitive",
                ["fields"] = new JsonObject { ["SizeX"] = 10, ["SizeY"] = 10, ["SizeZ"] = 10 }
            }, default).GetAwaiter().GetResult();

            // Real totals for a cube: 8 vertices, 12 edges, 6 faces, 6 UV islands, 0 bones.
            var resVertex = JsonNode.Parse(tools.ExecuteAsync("selection_tool", new JsonObject
            {
                ["mode"] = "Vertex",
                ["operation"] = "get_count"
            }, default).GetAwaiter().GetResult())!;
            Require(resVertex["totalElements"]?.GetValue<int>() == 8, "cube has 8 vertices");

            var resEdge = JsonNode.Parse(tools.ExecuteAsync("selection_tool", new JsonObject
            {
                ["mode"] = "Edge",
                ["operation"] = "get_count"
            }, default).GetAwaiter().GetResult())!;
            Require(resEdge["totalElements"]?.GetValue<int>() == 12, "cube has 12 edges");

            var resIsland = JsonNode.Parse(tools.ExecuteAsync("selection_tool", new JsonObject
            {
                ["mode"] = "Island",
                ["operation"] = "get_count"
            }, default).GetAwaiter().GetResult())!;
            Require(resIsland["totalElements"]?.GetValue<int>() == 6, "cube has 6 UV islands");

            var resBone = JsonNode.Parse(tools.ExecuteAsync("selection_tool", new JsonObject
            {
                ["mode"] = "Bone",
                ["operation"] = "select_all"
            }, default).GetAwaiter().GetResult())!;
            Require(resBone["totalElements"]?.GetValue<int>() == 0, "no rig, no bones");
            Require(resBone["selectedCount"]?.GetValue<int>() == 0, "0 bones selected");

            // Select all faces
            var resAll = JsonNode.Parse(tools.ExecuteAsync("selection_tool", new JsonObject
            {
                ["mode"] = "Face",
                ["operation"] = "select_all"
            }, default).GetAwaiter().GetResult())!;
            Require(resAll["selectedCount"]?.GetValue<int>() == 6, "cube has 6 faces selected");

            // Deselect
            var resNone = JsonNode.Parse(tools.ExecuteAsync("selection_tool", new JsonObject
            {
                ["mode"] = "Face",
                ["operation"] = "deselect"
            }, default).GetAwaiter().GetResult())!;
            Require(resNone["selectedCount"]?.GetValue<int>() == 0, "0 faces selected");

            // Select indices
            var resIdx = JsonNode.Parse(tools.ExecuteAsync("selection_tool", new JsonObject
            {
                ["mode"] = "Face",
                ["operation"] = "set_indices",
                ["indices"] = new JsonArray(0, 1)
            }, default).GetAwaiter().GetResult())!;
            Require(resIdx["selectedCount"]?.GetValue<int>() == 2, "2 faces selected");

            // Invert
            var resInv = JsonNode.Parse(tools.ExecuteAsync("selection_tool", new JsonObject
            {
                ["mode"] = "Face",
                ["operation"] = "invert"
            }, default).GetAwaiter().GetResult())!;
            Require(resInv["selectedCount"]?.GetValue<int>() == 4, "4 faces selected after inversion");

            // Real adjacency: one cube face touches four neighbours, then the opposite face.
            tools.ExecuteAsync("selection_tool", new JsonObject
            {
                ["mode"] = "Face",
                ["operation"] = "set_indices",
                ["indices"] = new JsonArray(0)
            }, default).GetAwaiter().GetResult();
            var resGrow1 = JsonNode.Parse(tools.ExecuteAsync("selection_tool", new JsonObject
            {
                ["mode"] = "Face",
                ["operation"] = "grow"
            }, default).GetAwaiter().GetResult())!;
            Require(resGrow1["selectedCount"]?.GetValue<int>() == 5, "grow from one face reaches its 4 neighbours");
            var resGrow2 = JsonNode.Parse(tools.ExecuteAsync("selection_tool", new JsonObject
            {
                ["mode"] = "Face",
                ["operation"] = "grow"
            }, default).GetAwaiter().GetResult())!;
            Require(resGrow2["selectedCount"]?.GetValue<int>() == 6, "second grow reaches the opposite face");
        });
    }
}
