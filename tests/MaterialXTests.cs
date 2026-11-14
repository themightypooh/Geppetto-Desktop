#nullable enable
using System;
using System.Collections.Generic;
using Effigy;

/// <summary>
/// MaterialX / OpenPBR round-trip tests (godlike-usd-stage-alembic-materialx):
/// a look survives MaterialX -> PbrMaterial -> MaterialX with its values intact,
/// and the USD <-> MaterialX bridge carries UsdPreviewSurface both ways.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is coordinator-owned;
/// the final integrator adds the one MaterialXTests.Register(registry, settings);
/// line, as with KernelParityTests.
/// </summary>
internal static class MaterialXTests
{
    const string StandardSurfaceFixture =
        "<?xml version=\"1.0\"?>\n" +
        "<materialx version=\"1.39\">\n" +
        "  <nodegraph name=\"NG_Skin\">\n" +
        "    <image name=\"Skin_base_tex\" type=\"image\">\n" +
        "      <input name=\"file\" type=\"filename\" value=\"skin_base.png\"/>\n" +
        "      <input name=\"sourceColorSpace\" type=\"string\" value=\"srgb_texture\"/>\n" +
        "      <output name=\"out\" type=\"color3\"/>\n" +
        "    </image>\n" +
        "    <image name=\"Skin_normal_tex\" type=\"image\">\n" +
        "      <input name=\"file\" type=\"filename\" value=\"skin_normal.png\"/>\n" +
        "      <input name=\"sourceColorSpace\" type=\"string\" value=\"raw\"/>\n" +
        "      <output name=\"out\" type=\"color3\"/>\n" +
        "    </image>\n" +
        "    <normalmap name=\"Skin_normalmap\" type=\"normalmap\">\n" +
        "      <input name=\"in\" type=\"vector3\" nodename=\"Skin_normal_tex\" output=\"out\"/>\n" +
        "      <input name=\"space\" type=\"string\" value=\"tangent\"/>\n" +
        "      <output name=\"out\" type=\"vector3\"/>\n" +
        "    </normalmap>\n" +
        "    <standard_surface name=\"Skin_shader\" type=\"standard_surface\">\n" +
        "      <input name=\"base_color\" type=\"color3\" nodename=\"Skin_base_tex\" output=\"out\"/>\n" +
        "      <input name=\"base_metalness\" type=\"float\" value=\"0.1\"/>\n" +
        "      <input name=\"specular_roughness\" type=\"float\" value=\"0.4\"/>\n" +
        "      <input name=\"normal\" type=\"vector3\" nodename=\"Skin_normalmap\" output=\"out\"/>\n" +
        "      <input name=\"opacity\" type=\"float\" value=\"0.95\"/>\n" +
        "      <input name=\"emission_color\" type=\"color3\" value=\"0, 0, 0\"/>\n" +
        "      <input name=\"coat_weight\" type=\"float\" value=\"0.5\"/>\n" +
        "      <input name=\"mystery_knob\" type=\"float\" value=\"7\"/>\n" +
        "    </standard_surface>\n" +
        "  </nodegraph>\n" +
        "  <surfacematerial name=\"Skin\" type=\"surfacematerial\">\n" +
        "    <input name=\"surfaceshader\" type=\"surfaceshader\" nodename=\"Skin_shader\" output=\"out\"/>\n" +
        "    <output name=\"out\" type=\"material\"/>\n" +
        "  </surfacematerial>\n" +
        "  <fancynode name=\"Weird\" type=\"fancynode\"/>\n" +
        "</materialx>\n";

    public static void Register(TestRegistry registry, TestSettings settings)
    {
        void Check(string name, Action test) => registry.Add("materialx", name, test);
        void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message = "assertion failed")
        {
            if (!condition) throw new Exception(message);
        }
        void Near(float actual, float expected, float tolerance, string what)
        {
            if (Math.Abs(actual - expected) > tolerance)
                throw new Exception($"{what}: expected {expected}, got {actual}");
        }

        Check("standard_surface parses to a PbrMaterial with maps resolved", () =>
        {
            var doc = MaterialXDocument.Parse(StandardSurfaceFixture);
            Require(doc.Losses.Count == 1, "parse must lose only the unsupported node type, got: " + string.Join("; ", doc.Losses));
            var pbr = doc.ToPbr("Skin");
            Require(doc.Losses.Count == 2, "ToPbr must add the unmapped input as a second loss, got: " + string.Join("; ", doc.Losses));
            Require(pbr is not null, "material Skin must resolve");
            Require(pbr.BaseColorMap == "skin_base.png", "base_color must resolve to skin_base.png, got " + pbr.BaseColorMap);
            Near(pbr.Metallic, 0.1f, 1e-4f, "base_metalness");
            Near(pbr.Roughness, 0.4f, 1e-4f, "specular_roughness");
            Require(pbr.NormalMap == "skin_normal.png", "normal must resolve through the normalmap node, got " + pbr.NormalMap);
            Near(pbr.Opacity, 0.95f, 1e-4f, "opacity");
            Near(pbr.Clearcoat, 0.5f, 1e-4f, "coat_weight");
            Require(doc.Losses.Exists(l => l.Contains("fancynode", StringComparison.Ordinal)), "unsupported node type must be a named loss");
            Require(doc.Losses.Exists(l => l.Contains("mystery_knob", StringComparison.Ordinal)), "unmapped input must be a named loss");
        });

        Check("PbrMaterial round-trips through MaterialX text", () =>
        {
            var material = new PbrMaterial
            {
                Name = "Skin",
                BaseColor = new Vec3(0.6f, 0.3f, 0.15f),
                BaseColorMap = "skin_base.png",
                Metallic = 0.1f,
                Roughness = 0.4f,
                NormalMap = "skin_normal.png",
                Opacity = 0.95f,
                Clearcoat = 0.5f,
                Ior = 1.45f,
            };
            var text = MaterialXDocument.FromPbr(material).Write();
            Require(text.Contains("standard_surface", StringComparison.Ordinal), "written doc must use standard_surface");
            Require(text.Contains("skin_base.png", StringComparison.Ordinal), "written doc must carry the base map");
            var back = MaterialXDocument.Parse(text);
            Require(back.Losses.Count == 0, "re-parse must have no losses, got: " + string.Join("; ", back.Losses));
            var roundTripped = back.ToPbr("Skin");
            Require(roundTripped is not null, "material must survive the round-trip");
            Require(roundTripped.BaseColorMap == "skin_base.png", "base map must survive");
            Require(roundTripped.NormalMap == "skin_normal.png", "normal map must survive");
            Near(roundTripped.Metallic, 0.1f, 1e-4f, "metallic after round-trip");
            Near(roundTripped.Roughness, 0.4f, 1e-4f, "roughness after round-trip");
            Near(roundTripped.Opacity, 0.95f, 1e-4f, "opacity after round-trip");
            Near(roundTripped.Clearcoat, 0.5f, 1e-4f, "clearcoat after round-trip");
            Near(roundTripped.Ior, 1.45f, 1e-4f, "ior after round-trip");
            // No base-colour assert here: with a base map present the scalar is not
            // written (the map wins), so it reads back as the default. The scalar
            // colour round-trip is covered by the open_pbr test, which has no map.
        });

        Check("open_pbr_surface round-trips with the same values", () =>
        {
            var material = new PbrMaterial
            {
                Name = "Armour",
                BaseColor = new Vec3(0.2f, 0.2f, 0.25f),
                Metallic = 0.85f,
                Roughness = 0.3f,
                Clearcoat = 0.25f,
            };
            var text = MaterialXDocument.FromPbr(material, openPbr: true).Write();
            Require(text.Contains("open_pbr_surface", StringComparison.Ordinal), "written doc must use open_pbr_surface");
            var back = MaterialXDocument.Parse(text);
            var roundTripped = back.ToPbr("Armour");
            Require(roundTripped is not null, "open_pbr material must survive the round-trip");
            Near(roundTripped.Metallic, 0.85f, 1e-4f, "metallic after open_pbr round-trip");
            Near(roundTripped.Roughness, 0.3f, 1e-4f, "roughness after open_pbr round-trip");
            Near(roundTripped.Clearcoat, 0.25f, 1e-4f, "clearcoat after open_pbr round-trip");
            Near(roundTripped.BaseColor.x, 0.2f, 1e-4f, "base colour after open_pbr round-trip");
        });

        Check("UsdPreviewSurface bridges to MaterialX and back", () =>
        {
            var surface = new UsdPreviewSurface
            {
                DiffuseColor = new Vec3(0.7f, 0.1f, 0.05f),
                DiffuseMap = "hero_base.png",
                Metallic = 0.2f,
                Roughness = 0.5f,
                NormalMap = "hero_normal.png",
                Opacity = 0.9f,
            };
            var pbr = PbrMaterial.FromUsd(surface, "Hero");
            Require(pbr.BaseColorMap == "hero_base.png", "bridge must carry the diffuse map");
            Require(pbr.NormalMap == "hero_normal.png", "bridge must carry the normal map");
            var text = MaterialXDocument.FromPbr(pbr).Write();
            var back = MaterialXDocument.Parse(text).ToPbr("Hero");
            Require(back is not null, "bridged material must survive MaterialX");
            var usd = back.ToUsd();
            // No diffuse-colour assert: with a diffuse map present the scalar is not
            // written (the map wins). The map itself is the bridged value.
            Require(usd.DiffuseMap == "hero_base.png", "diffuse map must survive the bridge both ways");
            Require(usd.NormalMap == "hero_normal.png", "normal map must survive the bridge both ways");
            Near(usd.Roughness, 0.5f, 1e-4f, "roughness after bridge");
            Near(usd.Opacity, 0.9f, 1e-4f, "opacity after bridge");
        });

        Check("not a MaterialX document is refused, missing material is a loss", () =>
        {
            var threw = false;
            try { MaterialXDocument.Parse("<html><body>nope</body></html>"); }
            catch (FormatException e) { threw = e.Message.Contains("MaterialX", StringComparison.Ordinal); }
            Require(threw, "a non-MaterialX root must throw naming MaterialX");
            var doc = MaterialXDocument.Parse(StandardSurfaceFixture);
            var missing = doc.ToPbr("NoSuchMaterial");
            Require(missing is null, "an unknown material must resolve to null");
            Require(doc.Losses.Exists(l => l.Contains("NoSuchMaterial", StringComparison.Ordinal)), "the missing material must be a named loss");
        });
    }
}
