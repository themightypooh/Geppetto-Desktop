using System.Numerics;
using System.Text.Json.Nodes;
using Effigy;
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

            // Every service is genuinely present (resolved from the live assembly),
            // and no invented hotkey is listed for any of them.
            Require(services.All(s => s!["active"]?.GetValue<bool>() == true), "every service active");
            Require(services.All(s => s!["hotkey"] is null), "no service lists a hotkey");

            // Filter category
            var resUv = JsonNode.Parse(tools.ExecuteAsync("list_services", new JsonObject { ["category"] = "UV" }, default).GetAwaiter().GetResult())!;
            Require(resUv["count"]?.GetValue<int>() == 5, "UV category has 5 services");
        });

        Check("lane 12: configure_renderer applies what reaches the renderer and labels the rest", () =>
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

            // Stored values are still recorded...
            Require(res["hdr"]?.GetValue<bool>() == true, "hdr true");
            Require(res["antiAliasing"]?.GetValue<string>() == "MSAA4x", "aa MSAA4x");
            Require(res["iblPreset"]?.GetValue<string>() == "outdoor", "ibl outdoor");
            double expVal = res["exposure"]!.GetValue<double>();
            Require(Math.Abs(expVal - 1.25) < 0.01, "exposure 1.25");
            Require(res["bloom"]?.GetValue<bool>() == false, "bloom false");
            // ...but only AA, exposure and bloom reach the renderer. HDR has no toggle
            // on this LDR pipeline and IBL presets have no environment files.
            Require(res["antiAliasingApplied"]?.GetValue<bool>() == true, "aa applied");
            Require(Math.Abs(res["exposureApplied"]!.GetValue<double>() - 1.25) < 0.01, "exposure applied 1.25");
            Require(res["bloomApplied"]?.GetValue<bool>() == true, "bloom applied");
            Require(res["hdrApplied"]?.GetValue<bool>() == false, "hdr not applied");
            Require(res["iblApplied"]?.GetValue<bool>() == false, "ibl not applied");

            bool threwAa = false;
            try { tools.ExecuteAsync("configure_renderer", new JsonObject { ["antiAliasing"] = "Banana" }, default).GetAwaiter().GetResult(); }
            catch { threwAa = true; }
            Require(threwAa, "invalid AA throws");

            bool threwExp = false;
            try { tools.ExecuteAsync("configure_renderer", new JsonObject { ["exposure"] = 0 }, default).GetAwaiter().GetResult(); }
            catch { threwExp = true; }
            Require(threwExp, "exposure 0 throws");
        });

        Check("lane 13: set_camera presets and bookmark recall, and the quarter view follows the stored camera", () =>
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

            // The evidence quarter view follows the stored camera — checked without GL.
            tools.ExecuteAsync("add_feature", new JsonObject
            {
                ["type"] = "Primitive",
                ["fields"] = new JsonObject { ["SizeX"] = 10, ["SizeY"] = 10, ["SizeZ"] = 10 }
            }, default).GetAwaiter().GetResult();
            var mesh = tools.Studio.ToMesh();
            var (min, max) = ModelMeasure.Bounds(mesh);
            var centre = new Vector3((min.x + max.x) / 2, (min.y + max.y) / 2, (min.z + max.z) / 2);
            var size = max - min;
            float radius = (float)Math.Sqrt(size.x * size.x + size.y * size.y + size.z * size.z) / 2;
            tools.ExecuteAsync("set_camera", new JsonObject { ["preset"] = "front" }, default).GetAwaiter().GetResult();
            var quarter = tools.QuarterCamera(centre, radius);
            Require(quarter.YawDegrees == 0 && quarter.PitchDegrees == 0, "quarter follows stored front camera");
            // Distance 20 doubles the framed distance (stored distance is a scale on it).
            tools.ExecuteAsync("set_camera", new JsonObject { ["distance"] = 20 }, default).GetAwaiter().GetResult();
            var quarterFar = tools.QuarterCamera(centre, radius);
            float framed = Math.Clamp(radius * 2.4f, 5f, 2000f);
            Require(Math.Abs(quarterFar.Distance - framed * 2) < 0.01, "quarter distance scales with stored distance");
        });

        Check("lane 14: validate_import checks a real file, parses .effigy, and refuses formats with no importer", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p3-imp-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);

            // No file named: nothing was checked.
            var resNone = JsonNode.Parse(tools.ExecuteAsync("validate_import", new JsonObject
            {
                ["format"] = "OBJ",
                ["scale"] = 0.0254,
                ["upAxis"] = "Y"
            }, default).GetAwaiter().GetResult())!;
            Require(resNone["valid"]?.GetValue<bool>() == false, "no filePath is not valid");
            Require(resNone["targetSystem"]?.GetValue<string>() == "Z-up", "target Z-up");

            // Named but missing.
            var resMissing = JsonNode.Parse(tools.ExecuteAsync("validate_import", new JsonObject
            {
                ["format"] = "OBJ",
                ["filePath"] = "missing.obj"
            }, default).GetAwaiter().GetResult())!;
            Require(resMissing["valid"]?.GetValue<bool>() == false, "missing file is not valid");

            // A real .effigy round-trips through the real parser.
            tools.ExecuteAsync("add_feature", new JsonObject
            {
                ["type"] = "Primitive",
                ["fields"] = new JsonObject { ["SizeX"] = 10, ["SizeY"] = 10, ["SizeZ"] = 10 }
            }, default).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(root, "fixture.effigy"), StudioDocument.Write(tools.Studio));
            var resEffigy = JsonNode.Parse(tools.ExecuteAsync("validate_import", new JsonObject
            {
                ["format"] = "Effigy",
                ["filePath"] = "fixture.effigy"
            }, default).GetAwaiter().GetResult())!;
            Require(resEffigy["valid"]?.GetValue<bool>() == true, "real .effigy parses");
            Require(resEffigy["bodies"]?.GetValue<int>() == tools.Studio.Bodies.Count, "bodies match live studio");
            Require(resEffigy["vertices"]?.GetValue<int>() == tools.Studio.TotalVertexCount, "vertices match live studio");
            Require(resEffigy["faces"]?.GetValue<int>() == tools.Studio.TotalFaceCount, "faces match live studio");

            // A real .obj file exists but there is no OBJ importer: refused, not read.
            File.WriteAllText(Path.Combine(root, "real.obj"), "o cube\nv 0 0 0\n");
            var resObj = JsonNode.Parse(tools.ExecuteAsync("validate_import", new JsonObject
            {
                ["format"] = "OBJ",
                ["filePath"] = "real.obj"
            }, default).GetAwaiter().GetResult())!;
            Require(resObj["valid"]?.GetValue<bool>() == false, "OBJ has no importer");
            Require(resObj["reason"]!.GetValue<string>().Contains("importer"), "reason names the missing importer");

            bool threw = false;
            try { tools.ExecuteAsync("validate_import", new JsonObject { ["format"] = "INVALID" }, default).GetAwaiter().GetResult(); }
            catch { threw = true; }
            Require(threw, "invalid format throws");
        });

        Check("lane 15: validate_export and export_preset use the real ExportService capability check", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p3-exp-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);

            // Empty studio: no geometry, nothing to export.
            var resEmpty = JsonNode.Parse(tools.ExecuteAsync("validate_export", new JsonObject { ["format"] = "obj" }, default).GetAwaiter().GetResult())!;
            Require(resEmpty["ready"]?.GetValue<bool>() == false, "empty studio is not ready");
            Require(resEmpty["canExport"]?.GetValue<bool>() == false, "empty studio cannot export");

            // A cube exports as OBJ, geometry preserved.
            tools.ExecuteAsync("add_feature", new JsonObject
            {
                ["type"] = "Primitive",
                ["fields"] = new JsonObject { ["SizeX"] = 10, ["SizeY"] = 10, ["SizeZ"] = 10 }
            }, default).GetAwaiter().GetResult();
            var resObj = JsonNode.Parse(tools.ExecuteAsync("validate_export", new JsonObject { ["format"] = "obj" }, default).GetAwaiter().GetResult())!;
            Require(resObj["ready"]?.GetValue<bool>() == true, "cube is ready for obj");
            Require(resObj["canExport"]?.GetValue<bool>() == true, "cube can export obj");
            Require(resObj["preservesGeometry"]?.GetValue<bool>() == true, "obj preserves geometry");

            // vmdl has no capability-checked exporter: refused with a reason, not a budget score.
            var resVal = JsonNode.Parse(tools.ExecuteAsync("validate_export", new JsonObject { ["format"] = "vmdl" }, default).GetAwaiter().GetResult())!;
            Require(resVal["format"]?.GetValue<string>() == "vmdl", "vmdl format");
            Require(resVal["canExport"]?.GetValue<bool>() == false, "vmdl cannot export");
            Require(!string.IsNullOrWhiteSpace(resVal["reason"]?.GetValue<string>()), "vmdl carries a reason");

            // Presets are plans, never exports.
            var resPre = JsonNode.Parse(tools.ExecuteAsync("export_preset", new JsonObject { ["preset"] = "sbox_character" }, default).GetAwaiter().GetResult())!;
            Require(resPre["format"]?.GetValue<string>() == "vmdl", "char format vmdl");
            Require(resPre["targetFaces"]?.GetValue<int>() == 30000, "char max faces 30000");
            Require(resPre["lods"]?.GetValue<int>() == 3, "char lods 3");
            Require(resPre["exported"]?.GetValue<bool>() == false, "preset exports nothing");
            Require(resPre["canExport"]?.GetValue<bool>() == false, "sbox_character target has no exporter");

            var resRaw = JsonNode.Parse(tools.ExecuteAsync("export_preset", new JsonObject { ["preset"] = "raw_obj" }, default).GetAwaiter().GetResult())!;
            Require(resRaw["format"]?.GetValue<string>() == "obj", "raw_obj format obj");
            Require(resRaw["canExport"]?.GetValue<bool>() == true, "raw_obj can export the cube");
            Require(resRaw["exported"]?.GetValue<bool>() == false, "raw_obj still exports nothing");
        });

        Check("lane 16: project_manage records templates and autosave writes a real backup file", () =>
        {
            var root = Path.Combine(settings.WorkDirectory, "p3-proj-" + Guid.NewGuid().ToString("N"));
            var tools = new AiSuiteTools(root);
            // Apply template: recorded only — no template files exist in this build.
            var resTpl = JsonNode.Parse(tools.ExecuteAsync("project_manage", new JsonObject
            {
                ["action"] = "apply_template",
                ["template"] = "character"
            }, default).GetAwaiter().GetResult())!;
            Require(resTpl["template"]?.GetValue<string>() == "character", "character template recorded");
            Require(resTpl["status"]?.GetValue<string>() == "recorded", "template status is recorded");
            Require(resTpl["applied"]?.GetValue<bool>() == false, "template not applied");

            // Autosave snapshot: the returned backup filename exists on disk.
            var resSave = JsonNode.Parse(tools.ExecuteAsync("project_manage", new JsonObject
            {
                ["action"] = "autosave"
            }, default).GetAwaiter().GetResult())!;
            Require(resSave["totalBackups"]?.GetValue<int>() == 1, "1 backup created");
            Require(File.Exists(Path.Combine(root, resSave["backup"]!.GetValue<string>())), "backup file exists");

            // List backups
            var resList = JsonNode.Parse(tools.ExecuteAsync("project_manage", new JsonObject
            {
                ["action"] = "list_backups"
            }, default).GetAwaiter().GetResult())!;
            Require(resList["backups"]!.AsArray().Count == 1, "list has 1 backup");
        });
    }
}
