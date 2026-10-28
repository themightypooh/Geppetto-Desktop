using System.Text.Json.Nodes;
using Marionette.Desktop;

internal static class PhaseFourTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        void Check(string name, Action action) => registry.Add("phase4", name, action);
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        Check("lane 17: timeline_control manages frames, playback, and keyframes", () =>
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

            // Keyframe
            var resKey = JsonNode.Parse(tools.ExecuteAsync("timeline_control", new JsonObject
            {
                ["action"] = "add_keyframe",
                ["frame"] = 12,
                ["boneName"] = "Head"
            }, default).GetAwaiter().GetResult())!;
            Require(resKey["bone"]?.GetValue<string>() == "Head", "Head bone keyframe");
            Require(resKey["totalKeyframes"]?.GetValue<int>() == 1, "1 keyframe");

            // Playback
            var resPlay = JsonNode.Parse(tools.ExecuteAsync("timeline_control", new JsonObject
            {
                ["action"] = "playback",
                ["state"] = "play"
            }, default).GetAwaiter().GetResult())!;
            Require(resPlay["state"]?.GetValue<string>() == "play", "playing");
        });

        Check("lane 18: material_preset assigns PBR properties and validate_materials checks ranges", () =>
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

            // Assign Skin with custom color
            var resSkin = JsonNode.Parse(tools.ExecuteAsync("material_preset", new JsonObject
            {
                ["slot"] = "Face",
                ["preset"] = "Skin",
                ["roughness"] = 0.55
            }, default).GetAwaiter().GetResult())!;
            Require(resSkin["roughness"]?.GetValue<double>() == 0.55, "skin roughness 0.55");

            // Validate
            var resVal = JsonNode.Parse(tools.ExecuteAsync("validate_materials", new JsonObject(), default).GetAwaiter().GetResult())!;
            Require(resVal["valid"]?.GetValue<bool>() == true, "materials valid");
            Require(resVal["slotCount"]?.GetValue<int>() == 2, "2 slots assigned");
        });

        Check("lane 19: modelsheet_sync aligns views and calculates scale/ground bounds", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p4-sheet-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var resViews = JsonNode.Parse(tools.ExecuteAsync("modelsheet_sync", new JsonObject
            {
                ["action"] = "get_views"
            }, default).GetAwaiter().GetResult())!;
            var views = resViews["views"]!.AsArray();
            Require(views.Count == 3, "3 orthographic views");

            var resAlign = JsonNode.Parse(tools.ExecuteAsync("modelsheet_sync", new JsonObject
            {
                ["action"] = "set_alignment",
                ["scaleRatio"] = 1.05,
                ["groundOffset"] = 2.0
            }, default).GetAwaiter().GetResult())!;
            Require(resAlign["aligned"]?.GetValue<bool>() == true, "aligned true");
            Require(resAlign["scaleRatio"]?.GetValue<double>() == 1.05, "ratio 1.05");
        });

        Check("lane 20: layout_preset returns dockable panels and switches presets", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p4-lay-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var resDef = JsonNode.Parse(tools.ExecuteAsync("layout_preset", new JsonObject { ["action"] = "get" }, default).GetAwaiter().GetResult())!;
            Require(resDef["activePreset"]?.GetValue<string>() == "Modeling", "default is Modeling");

            var resSculpt = JsonNode.Parse(tools.ExecuteAsync("layout_preset", new JsonObject
            {
                ["action"] = "set",
                ["preset"] = "Sculpting"
            }, default).GetAwaiter().GetResult())!;
            Require(resSculpt["activePreset"]?.GetValue<string>() == "Sculpting", "switched to Sculpting");
            var panels = resSculpt["panels"]!.AsArray().Select(p => p!.GetValue<string>()).ToArray();
            Require(panels.Contains("Dyntopo"), "Dyntopo panel in Sculpting");
            Require(panels.Contains("BrushSettings"), "BrushSettings in Sculpting");
        });

        Check("lane 21: selection_tool executes element selection modes and operations", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p4-sel-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            // Create a test primitive
            tools.ExecuteAsync("add_feature", new JsonObject
            {
                ["type"] = "Primitive",
                ["fields"] = new JsonObject { ["SizeX"] = 10, ["SizeY"] = 10, ["SizeZ"] = 10 }
            }, default).GetAwaiter().GetResult();

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
        });
    }
}
