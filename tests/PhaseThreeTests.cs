using System.Text.Json.Nodes;
using Marionette.Desktop;

internal static class PhaseThreeTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        void Check(string name, Action action) => registry.Add("phase3", name, action);
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        Check("lane 11: list_services discovers all 36 services and filters categories", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p3-srv-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var resAll = JsonNode.Parse(tools.ExecuteAsync("list_services", new JsonObject(), default).GetAwaiter().GetResult())!;
            Require(resAll["count"]?.GetValue<int>() == 36, "should have exactly 36 services");
            var services = resAll["services"]!.AsArray();
            Require(services.Any(s => s!["name"]!.GetValue<string>() == "RetopoService"), "RetopoService present");
            Require(services.Any(s => s!["name"]!.GetValue<string>() == "UvEditService"), "UvEditService present");
            Require(services.Any(s => s!["name"]!.GetValue<string>() == "AdvancedIkService"), "AdvancedIkService present");
            Require(services.Any(s => s!["name"]!.GetValue<string>() == "ClothSimService"), "ClothSimService present");
            Require(services.Any(s => s!["name"]!.GetValue<string>() == "SceneRenderer"), "SceneRenderer present");
            Require(services.Any(s => s!["name"]!.GetValue<string>() == "DesktopProject"), "DesktopProject present");

            // Filter category
            var resUv = JsonNode.Parse(tools.ExecuteAsync("list_services", new JsonObject { ["category"] = "UV" }, default).GetAwaiter().GetResult())!;
            Require(resUv["count"]?.GetValue<int>() == 5, "UV category has 5 services");
        });

        Check("lane 12: configure_renderer updates HDR, AA, and IBL settings", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p3-rend-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var res = JsonNode.Parse(tools.ExecuteAsync("configure_renderer", new JsonObject
            {
                ["hdr"] = true,
                ["antiAliasing"] = "MSAA4x",
                ["iblPreset"] = "outdoor",
                ["exposure"] = 1.25,
                ["bloom"] = false
            }, default).GetAwaiter().GetResult())!;

            Require(res["hdr"]?.GetValue<bool>() == true, "hdr true");
            Require(res["antiAliasing"]?.GetValue<string>() == "MSAA4x", "aa MSAA4x");
            Require(res["iblPreset"]?.GetValue<string>() == "outdoor", "ibl outdoor");
            double expVal = res["exposure"]!.GetValue<double>();
            Require(Math.Abs(expVal - 1.25) < 0.01, "exposure 1.25");
            Require(res["bloom"]?.GetValue<bool>() == false, "bloom false");
        });

        Check("lane 13: set_camera presets and bookmark recall", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p3-cam-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            // Set preset
            var resFront = JsonNode.Parse(tools.ExecuteAsync("set_camera", new JsonObject { ["preset"] = "front" }, default).GetAwaiter().GetResult())!;
            Require(resFront["azimuth"]?.GetValue<double>() == 0, "front azimuth 0");
            Require(resFront["elevation"]?.GetValue<double>() == 0, "front elevation 0");

            // Save bookmark
            var resSave = JsonNode.Parse(tools.ExecuteAsync("set_camera", new JsonObject
            {
                ["action"] = "save_bookmark",
                ["bookmark"] = "HeroAngle",
                ["azimuth"] = 42,
                ["elevation"] = 15,
                ["distance"] = 8
            }, default).GetAwaiter().GetResult())!;
            Require(resSave["status"]?.GetValue<string>() == "bookmark_saved", "saved");

            // Switch to top
            tools.ExecuteAsync("set_camera", new JsonObject { ["preset"] = "top" }, default).GetAwaiter().GetResult();

            // Recall bookmark
            var resRecall = JsonNode.Parse(tools.ExecuteAsync("set_camera", new JsonObject
            {
                ["action"] = "recall_bookmark",
                ["bookmark"] = "HeroAngle"
            }, default).GetAwaiter().GetResult())!;
            Require(resRecall["azimuth"]?.GetValue<double>() == 42, "recalled azimuth 42");
        });

        Check("lane 14: validate_import supports OBJ, STL, PLY, and Effigy with transform parameters", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p3-imp-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            var resObj = JsonNode.Parse(tools.ExecuteAsync("validate_import", new JsonObject
            {
                ["format"] = "OBJ",
                ["scale"] = 0.0254,
                ["upAxis"] = "Y"
            }, default).GetAwaiter().GetResult())!;
            Require(resObj["valid"]?.GetValue<bool>() == true, "OBJ valid");
            Require(resObj["targetSystem"]?.GetValue<string>() == "Z-up", "target Z-up");

            bool threw = false;
            try { tools.ExecuteAsync("validate_import", new JsonObject { ["format"] = "INVALID" }, default).GetAwaiter().GetResult(); }
            catch { threw = true; }
            Require(threw, "invalid format throws");
        });

        Check("lane 15: validate_export and export_preset for sbox game ready formats", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p3-exp-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            // Export validation
            var resVal = JsonNode.Parse(tools.ExecuteAsync("validate_export", new JsonObject { ["format"] = "vmdl" }, default).GetAwaiter().GetResult())!;
            Require(resVal["format"]?.GetValue<string>() == "vmdl", "vmdl format");

            // Preset
            var resPre = JsonNode.Parse(tools.ExecuteAsync("export_preset", new JsonObject { ["preset"] = "sbox_character" }, default).GetAwaiter().GetResult())!;
            Require(resPre["format"]?.GetValue<string>() == "vmdl", "char format vmdl");
            Require(resPre["targetFaces"]?.GetValue<int>() == 30000, "char max faces 30000");
            Require(resPre["lods"]?.GetValue<int>() == 3, "char lods 3");
        });

        Check("lane 16: project_manage template application and autosave backups", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p3-proj-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            // Apply template
            var resTpl = JsonNode.Parse(tools.ExecuteAsync("project_manage", new JsonObject
            {
                ["action"] = "apply_template",
                ["template"] = "character"
            }, default).GetAwaiter().GetResult())!;
            Require(resTpl["template"]?.GetValue<string>() == "character", "character template applied");

            // Autosave snapshot
            var resSave = JsonNode.Parse(tools.ExecuteAsync("project_manage", new JsonObject
            {
                ["action"] = "autosave"
            }, default).GetAwaiter().GetResult())!;
            Require(resSave["totalBackups"]?.GetValue<int>() == 1, "1 backup created");

            // List backups
            var resList = JsonNode.Parse(tools.ExecuteAsync("project_manage", new JsonObject
            {
                ["action"] = "list_backups"
            }, default).GetAwaiter().GetResult())!;
            Require(resList["backups"]!.AsArray().Count == 1, "list has 1 backup");
        });
    }
}
