#nullable enable
using System;
using System.Collections.Generic;
using Effigy;

/// <summary>
/// USD stage round-trip tests (godlike-usd-stage-alembic-materialx): a layered
/// character+environment stage opens without overwriting source assets, hierarchy,
/// materials, variants and instancing survive, and every drop is a named loss.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is coordinator-owned;
/// the final integrator adds the one UsdStageTests.Register(registry, settings);
/// line, as with KernelParityTests. The Blender/Maya/Houdini leg of the
/// round-trip runs in CI via the headless packaging task; these checks are the
/// in-repo headless round-trip (Geppetto -> text -> Geppetto).
/// </summary>
internal static class UsdStageTests
{
    const string StageFixture =
        "#usda 1.0\n" +
        "(\n" +
        "    defaultPrim = \"Char\"\n" +
        "    doc = \"test stage\"\n" +
        "    metersPerUnit = 1\n" +
        "    upAxis = \"Z\"\n" +
        ")\n" +
        "def Xform \"Char\" (\n" +
        "    kind = \"component\"\n" +
        ")\n" +
        "{\n" +
        "    def Mesh \"Body\" (\n" +
        "        prepend apiSchemas = [\"MaterialBindingAPI\"]\n" +
        "    )\n" +
        "    {\n" +
        "        uniform bool doubleSided = 0\n" +
        "        float3[] extent = [(0, 0, 0), (0.0254, 0.0254, 0)]\n" +
        "        int[] faceVertexCounts = [4]\n" +
        "        int[] faceVertexIndices = [0, 1, 2, 3]\n" +
        "        point3f[] points = [(0, 0, 0), (0.0254, 0, 0), (0.0254, 0.0254, 0), (0, 0.0254, 0)]\n" +
        "        texCoord2f[] primvars:st = [(0.1, 0.9), (0.9, 0.9), (0.9, 0.1), (0.1, 0.1)] (\n" +
        "            interpolation = \"faceVarying\"\n" +
        "        )\n" +
        "        uniform token subdivisionScheme = \"none\"\n" +
        "        rel material:binding = </Char/Looks/Body_mat>\n" +
        "    }\n" +
        "    def Scope \"Looks\"\n" +
        "    {\n" +
        "        def Material \"Body_mat\"\n" +
        "        {\n" +
        "            token outputs:surface.connect = </Char/Looks/Body_mat/Surface.outputs:surface>\n" +
        "            def Shader \"Surface\"\n" +
        "            {\n" +
        "                uniform token info:id = \"UsdPreviewSurface\"\n" +
        "                color3f inputs:diffuseColor = (0.8, 0.2, 0.1)\n" +
        "                float inputs:roughness = 0.35\n" +
        "                float inputs:metallic = 0.9\n" +
        "            }\n" +
        "        }\n" +
        "    }\n" +
        "}\n";

    const string VariantFixture =
        "#usda 1.0\n" +
        "(\n" +
        "    defaultPrim = \"Char\"\n" +
        "    metersPerUnit = 1\n" +
        "    upAxis = \"Z\"\n" +
        ")\n" +
        "def Xform \"Char\" (\n" +
        "    variants = { string modeling = \"high\" }\n" +
        ")\n" +
        "{\n" +
        "    variantSet \"modeling\"\n" +
        "    {\n" +
        "        \"high\"\n" +
        "        {\n" +
        "            def Mesh \"BodyHigh\"\n" +
        "            {\n" +
        "                point3f[] points = [(0, 0, 0), (0.0254, 0, 0), (0.0254, 0.0254, 0)]\n" +
        "                int[] faceVertexCounts = [3]\n" +
        "                int[] faceVertexIndices = [0, 1, 2]\n" +
        "            }\n" +
        "        }\n" +
        "        \"low\"\n" +
        "        {\n" +
        "            def Mesh \"BodyLow\"\n" +
        "            {\n" +
        "                point3f[] points = [(0, 0, 0), (0.0254, 0, 0), (0.0254, 0.0254, 0)]\n" +
        "                int[] faceVertexCounts = [3]\n" +
        "                int[] faceVertexIndices = [0, 1, 2]\n" +
        "            }\n" +
        "        }\n" +
        "    }\n" +
        "}\n";

    public static void Register(TestRegistry registry, TestSettings settings)
    {
        void Check(string name, Action test) => registry.Add("usd-stage", name, test);
        void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message = "assertion failed")
        {
            if (!condition) throw new Exception(message);
        }
        void Near(float actual, float expected, float tolerance, string what)
        {
            if (Math.Abs(actual - expected) > tolerance)
                throw new Exception($"{what}: expected {expected}, got {actual}");
        }

        Check("parse Mobile-shaped stage: hierarchy, material, binding", () =>
        {
            var stage = UsdStage.Parse(StageFixture);
            Require(stage.Losses.Count == 0, "fixture must parse with no losses, got: " + string.Join("; ", stage.Losses));
            Require(stage.DefaultPrim == "Char", "defaultPrim must be Char");
            Require(stage.MetersPerUnitValue == 1f, "metersPerUnit must be 1");
            Require(stage.UpAxis == "Z", "upAxis must be Z");
            var root = stage.Find("/Char");
            Require(root is not null && root.TypeName == "Xform", "root Xform /Char must exist");
            Require(root.Kind == "component", "root kind must be component");
            var body = stage.Find("/Char/Body");
            Require(body is not null && body.TypeName == "Mesh", "Mesh /Char/Body must exist");
            Require(body.MaterialBinding == "/Char/Looks/Body_mat", "material binding must be /Char/Looks/Body_mat, got " + body.MaterialBinding);
            Require(body.Attribute("doubleSided")?.Value is bool b && !b, "doubleSided must parse as bool false");
            var material = stage.Find("/Char/Looks/Body_mat");
            Require(material is not null && material.TypeName == "Material", "Material /Char/Looks/Body_mat must exist");
            var surface = UsdPreviewSurface.FromMaterial(material, new List<string>());
            Near(surface.DiffuseColor.x, 0.8f, 1e-4f, "diffuseColor.r");
            Near(surface.DiffuseColor.y, 0.2f, 1e-4f, "diffuseColor.g");
            Near(surface.DiffuseColor.z, 0.1f, 1e-4f, "diffuseColor.b");
            Near(surface.Roughness, 0.35f, 1e-4f, "roughness");
            Near(surface.Metallic, 0.9f, 1e-4f, "metallic");
        });

        Check("mesh prim converts to a PolyMesh in inches with UVs", () =>
        {
            var stage = UsdStage.Parse(StageFixture);
            var losses = new List<string>();
            var mesh = UsdStage.PrimToMesh(stage.Find("/Char/Body"), losses);
            Require(losses.Count == 0, "conversion must not lose anything, got: " + string.Join("; ", losses));
            Require(mesh is not null, "mesh must convert");
            Require(mesh.VertexCount == 4, "expected 4 vertices, got " + mesh.VertexCount);
            Require(mesh.FaceCount == 1, "expected 1 face, got " + mesh.FaceCount);
            Near(mesh.Positions[1].x, 1f, 1e-4f, "vertex 1 x (0.0254 m must read back as 1 inch)");
            Near(mesh.Positions[2].y, 1f, 1e-4f, "vertex 2 y");
            Near(mesh.Positions[3].x, 0f, 1e-4f, "vertex 3 x");
            var uvs = mesh.Faces[0].UVs;
            Require(uvs is not null && uvs.Length == 4, "face must carry 4 UVs");
            Near(uvs[0].x, 0.1f, 1e-4f, "uv 0 u");
            Near(uvs[0].y, 0.1f, 1e-4f, "uv 0 v (st V-up must flip back to V-down)");
            Near(uvs[2].x, 0.9f, 1e-4f, "uv 2 u");
            Near(uvs[2].y, 0.9f, 1e-4f, "uv 2 v");
        });

        Check("stage round-trips through Write with no losses", () =>
        {
            var first = UsdStage.Parse(StageFixture);
            var text = first.Write();
            Require(text.Contains("metersPerUnit = 1", StringComparison.Ordinal), "written stage must carry metersPerUnit");
            Require(text.Contains("upAxis = \"Z\"", StringComparison.Ordinal), "written stage must carry upAxis Z");
            var second = UsdStage.Parse(text);
            Require(second.Losses.Count == 0, "re-parse must have no losses, got: " + string.Join("; ", second.Losses));
            Require(second.Find("/Char/Body") is not null, "Body must survive the round-trip");
            Require(second.Find("/Char/Looks/Body_mat") is not null, "material must survive the round-trip");
            var surface = UsdPreviewSurface.FromMaterial(second.Find("/Char/Looks/Body_mat"), new List<string>());
            Near(surface.Roughness, 0.35f, 1e-4f, "roughness after round-trip");
            Near(surface.Metallic, 0.9f, 1e-4f, "metallic after round-trip");
            var mesh = UsdStage.PrimToMesh(second.Find("/Char/Body"), new List<string>());
            Near(mesh.Positions[1].x, 1f, 1e-4f, "vertex 1 x after round-trip");
            Near(mesh.Faces[0].UVs[2].y, 0.9f, 1e-4f, "uv 2 v after round-trip");
        });

        Check("FromMeshes builds the Mobile-convention stage and geometry round-trips", () =>
        {
            var mesh = new PolyMesh();
            mesh.AddVertex(new Vec3(0f, 0f, 0f));
            mesh.AddVertex(new Vec3(1f, 0f, 0f));
            mesh.AddVertex(new Vec3(1f, 1f, 0f));
            mesh.AddVertex(new Vec3(0f, 1f, 0f));
            mesh.AddFace(new[] { 0, 1, 2, 3 }, new[]
            {
                new Vec2(0.1f, 0.1f), new Vec2(0.9f, 0.1f),
                new Vec2(0.9f, 0.9f), new Vec2(0.1f, 0.9f),
            });
            var material = new UsdPreviewSurface { DiffuseColor = new Vec3(0.5f, 0.25f, 0.125f), Roughness = 0.4f, Metallic = 0.2f };
            var stage = UsdStage.FromMeshes("Char", new List<(string, PolyMesh, UsdPreviewSurface)> { ("Body", mesh, material) });
            Require(stage.Losses.Count == 0, "FromMeshes must not lose anything, got: " + string.Join("; ", stage.Losses));
            var text = stage.Write();
            Require(text.Contains("primvars:st", StringComparison.Ordinal), "written stage must carry primvars:st");
            Require(text.Contains("(0.1, 0.9)", StringComparison.Ordinal), "st must flip V (uv v=0.1 writes as st 0.9)");
            Require(text.Contains("rel material:binding = </Char/Looks/Body_mat>", StringComparison.Ordinal), "binding must point at /Char/Looks/Body_mat");
            var back = UsdStage.Parse(text);
            Require(back.Losses.Count == 0, "re-parse must have no losses, got: " + string.Join("; ", back.Losses));
            var roundTripped = UsdStage.PrimToMesh(back.Find("/Char/Body"), new List<string>());
            Near(roundTripped.Positions[1].x, 1f, 1e-4f, "vertex 1 x after export round-trip");
            Near(roundTripped.Faces[0].UVs[0].y, 0.1f, 1e-4f, "uv 0 v after export round-trip");
            var surface = UsdPreviewSurface.FromMaterial(back.Find("/Char/Looks/Body_mat"), new List<string>());
            Near(surface.DiffuseColor.x, 0.5f, 1e-4f, "material diffuseColor.r after export round-trip");
            Near(surface.Roughness, 0.4f, 1e-4f, "material roughness after export round-trip");
        });

        Check("references defer honestly and local opinions win on flatten", () =>
        {
            var body = new UsdPrim { Name = "Body", TypeName = "Mesh", Spec = "def" };
            body.Attributes.Add(new UsdAttribute { Name = "inputs:roughness", TypeName = "float", Value = 0.35f });
            body.References.Add("other.usd");
            var root = new UsdPrim { Name = "Char", TypeName = "Xform", Spec = "def" };
            body.Parent = root;
            root.Children.Add(body);
            var stage = new UsdStage { DefaultPrim = "Char" };
            stage.Roots.Add(root);

            var deferred = stage.Flatten();
            Require(deferred.Losses.Count == 1 && deferred.Losses[0].Contains("reference deferred", StringComparison.Ordinal),
                "flatten without the referenced layer must name the deferred reference, got: " + string.Join("; ", deferred.Losses));
            Require(deferred.Find("/Char/Body") is not null, "the prim itself must survive a deferred reference");

            var otherBody = new UsdPrim { Name = "Body", TypeName = "Mesh", Spec = "def" };
            otherBody.Attributes.Add(new UsdAttribute { Name = "inputs:roughness", TypeName = "float", Value = 0.9f });
            otherBody.Attributes.Add(new UsdAttribute { Name = "inputs:diffuseColor", TypeName = "color3f", Value = new Vec3(0.1f, 0.1f, 0.1f) });
            var otherRoot = new UsdPrim { Name = "Char", TypeName = "Xform", Spec = "def" };
            otherBody.Parent = otherRoot;
            otherRoot.Children.Add(otherBody);
            var other = new UsdStage { DefaultPrim = "Char" };
            other.Roots.Add(otherRoot);
            var flattened = stage.Flatten(new Dictionary<string, UsdStage> { ["other.usd"] = other });
            Require(flattened.Losses.Count == 0, "flatten with the layer supplied must have no losses, got: " + string.Join("; ", flattened.Losses));
            var flatBody = flattened.Find("/Char/Body");
            Near((float)flatBody.Attribute("inputs:roughness").Value, 0.35f, 1e-4f, "local roughness must beat the referenced 0.9 (LIVRPS)");
            var diffuse = (Vec3)flatBody.Attribute("inputs:diffuseColor").Value;
            Near(diffuse.x, 0.1f, 1e-4f, "referenced diffuseColor must be gained where the local prim has none");
        });

        Check("variant selection applies and unknown metadata is a named loss", () =>
        {
            var stage = UsdStage.Parse(VariantFixture);
            Require(stage.Losses.Count == 0, "variant fixture must parse cleanly, got: " + string.Join("; ", stage.Losses));
            var root = stage.Find("/Char");
            Require(root.VariantSets.TryGetValue("modeling", out var variants) && variants.Count == 2, "variantSet modeling must list high + low");
            Require(root.VariantSelection.TryGetValue("modeling", out var selected) && selected == "high", "variantSelection must be high");
            var flat = stage.Flatten();
            Require(flat.Find("/Char/BodyHigh") is not null, "selected variant BodyHigh must be present after flatten");
            Require(flat.Find("/Char/BodyLow") is null, "unselected variant BodyLow must be gone after flatten");

            var odd = UsdStage.Parse(
                "#usda 1.0\n(\n    defaultPrim = \"Char\"\n    metersPerUnit = 1\n    upAxis = \"Z\"\n)\n" +
                "def Xform \"Char\" (\n    frobnicate = 1\n)\n{\n}\n");
            Require(odd.Losses.Count == 1 && odd.Losses[0].Contains("frobnicate", StringComparison.Ordinal),
                "unknown prim metadata must be a named loss, got: " + string.Join("; ", odd.Losses));
            Require(odd.Find("/Char") is not null, "the prim must still parse despite the unknown metadata");
        });

        Check("binary .usdc is refused, not guessed", () =>
        {
            var threw = false;
            try { UsdStage.Parse("PXR-USDC" + new string('\0', 10)); }
            catch (FormatException e) { threw = e.Message.Contains(".usdc", StringComparison.Ordinal); }
            Require(threw, "a .usdc crate header must throw naming .usdc");
        });
    }
}
