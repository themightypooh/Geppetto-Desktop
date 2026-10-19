using System.Numerics;
using Marionette.Desktop;

// Register with the GPU-required runner, not the CPU-only default. No EGL-unavailable skip/fallback.
internal static class HeadlessImageTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        registry.Add("egl-image", "flat-material-lighting-and-shadow", () => Run(settings.WorkDirectory),
            missing: () => Environment.GetEnvironmentVariable("V3_REQUIRE_EGL") == "1" ? null :
                "Set V3_REQUIRE_EGL=1 for windowless hardware image probes");
    }

    public static void Run(string artifacts)
    {
        Directory.CreateDirectory(artifacts);
        using var egl = new HeadlessGlContext();
        using var renderer = new SceneRenderer(egl.Gl);
        var camera = HeadlessImageChecks.Camera();
        var white = new MaterialPalette.Entry("neutral", .55f, .55f, .55f, .9f);
        var green = new MaterialPalette.Entry("green-emitter", .04f, .15f, .04f, .8f, er: .02f, eg: 1.2f, eb: .02f);
        var red = new MaterialPalette.Entry("red-emitter", .15f, .04f, .04f, .8f, er: 1.2f, eg: .02f, eb: .02f);
        var chartFixture = new HeadlessImageChecks.Fixture()
            .Quad("green", new(-1, 0, 0), new(.38f, 0, 0), new(0, .38f, 0), Vector3.UnitZ, green)
            .Quad("red", new(1, 0, 0), new(.38f, 0, 0), new(0, .38f, 0), Vector3.UnitZ, red)
            .Quad("neutral", new(0, -1, 0), new(.38f, 0, 0), new(0, .38f, 0), Vector3.UnitZ, white);
        using var chart = chartFixture.Mesh();
        var color = HeadlessImageChecks.Render(egl, renderer, chart, camera);
        color.SavePpm(Path.Combine(artifacts, "v3-material.ppm"));
        var greenPoint = HeadlessImageChecks.Project(camera, new(-1, 0, 0));
        var redPoint = HeadlessImageChecks.Project(camera, new(1, 0, 0));
        var neutralPoint = HeadlessImageChecks.Project(camera, new(0, -1, 0));
        var g = color.At(greenPoint.X, greenPoint.Y);
        var r = color.At(redPoint.X, redPoint.Y);
        var n = color.At(neutralPoint.X, neutralPoint.Y);
        Check(g.G > g.R + 45 && g.G > g.B + 45, $"green emission probe {g}");
        Check(r.R > r.G + 45 && r.R > r.B + 45, $"red emission probe {r}");
        Check(Math.Abs(n.R - n.G) < 35 && Math.Abs(n.G - n.B) < 35, $"neutral surface {n}");
        Check(color.ClippedChannels() < HeadlessImageChecks.Width * HeadlessImageChecks.Height / 20,
            "material chart has unexpectedly extensive clipping");
        using var chartLow = chartFixture.Mesh();
        var lowExposure = HeadlessImageChecks.Render(egl, renderer, chartLow, camera, .25f);
        lowExposure.SavePpm(Path.Combine(artifacts, "v3-quarter-exposure.ppm"));
        Check(lowExposure.At(greenPoint.X, greenPoint.Y).G < g.G - 25,
            "quarter exposure failed to dim green emission");
        try
        {
            HeadlessImageChecks.Render(egl, renderer, chart, camera, float.NaN);
            throw new InvalidOperationException("NaN exposure was accepted");
        }
        catch (ArgumentOutOfRangeException) { }

        // Same geometry/camera/exposure; only metallic changes. Away from the small
        // specular lobe, removing the dielectric diffuse term should darken this patch.
        HeadlessImageChecks.Fixture Patch(float metallic) => new HeadlessImageChecks.Fixture()
            .Quad("patch", Vector3.Zero, new(.65f, 0, 0), new(0, .65f, 0), Vector3.UnitZ,
                new MaterialPalette.Entry("patch", .5f, .5f, .5f, 1f, metallic));
        using var dielectricMesh = Patch(0).Mesh();
        using var metallicMesh = Patch(1).Mesh();
        var dielectric = HeadlessImageChecks.Render(egl, renderer, dielectricMesh, camera);
        var metal = HeadlessImageChecks.Render(egl, renderer, metallicMesh, camera);
        dielectric.SavePpm(Path.Combine(artifacts, "v3-dielectric.ppm"));
        metal.SavePpm(Path.Combine(artifacts, "v3-metal.ppm"));
        var patchPoint = HeadlessImageChecks.Project(camera, Vector3.Zero);
        double diffuseLuma = dielectric.MeanLuma(patchPoint.X, patchPoint.Y);
        double metalLuma = metal.MeanLuma(patchPoint.X, patchPoint.Y);
        Check(diffuseLuma > metalLuma + 8,
            $"metallic patch did not lose diffuse energy: dielectric {diffuseLuma:F1}, metal {metalLuma:F1}");

        using var receiver = new HeadlessImageChecks.Fixture()
            .Quad("receiver", Vector3.Zero, new(2, 0, 0), new(0, 2, 0), Vector3.UnitZ, white).Mesh();
        using var occluded = new HeadlessImageChecks.Fixture()
            .Quad("receiver", Vector3.Zero, new(2, 0, 0), new(0, 2, 0), Vector3.UnitZ, white)
            .Quad("caster", new(0, 0, .8f), new(.27f, 0, 0), new(0, .27f, 0), Vector3.UnitZ,
                new MaterialPalette.Entry("caster", .1f, .1f, .1f)).Mesh();
        var lit = HeadlessImageChecks.Render(egl, renderer, receiver, camera);
        var shadow = HeadlessImageChecks.Render(egl, renderer, occluded, camera);
        lit.SavePpm(Path.Combine(artifacts, "v3-receiver.ppm"));
        shadow.SavePpm(Path.Combine(artifacts, "v3-shadow.ppm"));
        var light = KeyShadowMap.Direction;
        var shadowWorld = new Vector3(-light.X * .8f / light.Z, -light.Y * .8f / light.Z, 0);
        var shadowPoint = HeadlessImageChecks.Project(camera, shadowWorld);
        var clearPoint = HeadlessImageChecks.Project(camera, new(1.3f, -.9f, 0));
        double litShadow = lit.MeanLuma(shadowPoint.X, shadowPoint.Y);
        double darkShadow = shadow.MeanLuma(shadowPoint.X, shadowPoint.Y);
        double litClear = lit.MeanLuma(clearPoint.X, clearPoint.Y);
        double clear = shadow.MeanLuma(clearPoint.X, clearPoint.Y);
        HeadlessImageChecks.SaveMetadata(Path.Combine(artifacts, "v3-probes.json"), new
        {
            greenPoint, redPoint, neutralPoint, g, r, n, shadowWorld, shadowPoint, clearPoint,
            litShadow, darkShadow, litClear, clear, clippedChannels = color.ClippedChannels(),
            quarterExposureGreen = lowExposure.At(greenPoint.X, greenPoint.Y), patchPoint, diffuseLuma, metalLuma
        });
        Check(litShadow - darkShadow > 15, $"key shadow failed: lit {litShadow:F1}, occluded {darkShadow:F1}");
        Check(Math.Abs(litClear - clear) < 5, $"unoccluded receiver changed: {litClear:F1} vs {clear:F1}");
        Check(darkShadow < clear - 15, $"shadow not darker than lit receiver: {darkShadow:F1} vs {clear:F1}");
    }

    static void Check(bool ok, string reason)
    {
        if (!ok) throw new InvalidOperationException(reason);
    }
}
