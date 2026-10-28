using System.Numerics;
using System.Reflection;
using Effigy;
using Marionette.Desktop;
using Silk.NET.OpenGL;

// Every measurement reads the shared renderer's scene-linear HDR target. Subtracting
// a black-environment render removes the unchanged analytic studio lights.
internal static class IblGpuTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        registry.Add("ibl-gpu", "gradient diffuse, rough metal broadening and dielectric Fresnel", () =>
        {
            using var context = new HeadlessGlContext();
            var gl = context.Gl;
            using var renderer = new SceneRenderer(gl) { SsaoStrength = 0, SsrStrength = 0 };
            using var target = new CaptureTarget(gl, 64, 64);
            var camera = new OrbitCamera { Distance = 5, YawDegrees = 0, PitchDegrees = 0 };
            var black = Prepare(_ => Vector3.Zero);
            var gradient = Prepare(d => new Vector3(1 + .8f * d.Z));
            var patch = Prepare(d => d.X > .98f ? new Vector3(10) : Vector3.Zero);
            var white = Prepare(_ => Vector3.One);
            float Draw(Vector3 normal, float rough, float metal, IblBaker.PreparedEnvironment env)
            {
                using var mesh = Card(normal, rough, metal);
                mesh.Upload(gl);
                try
                {
                    renderer.SetEnvironment(env);
                    renderer.Draw(mesh, null, camera, 64, 64, 1, target.Handle);
                    gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, renderer.PostChainTargets.ColorFbo);
                    gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
                    var pixels = new float[3 * 3 * 4];
                    gl.ReadPixels<float>(31, 31, 3, 3, PixelFormat.Rgba, PixelType.Float, pixels.AsSpan());
                    Check(gl.GetError() == GLEnum.NoError, "GL error in IBL measurement");
                    float value = Enumerable.Range(0, 9).Average(i => pixels[4 * i]);
                    return value;
                }
                finally { mesh.ReleaseGpu(gl); }
            }
            float Ambient(Vector3 n, float r, float m, IblBaker.PreparedEnvironment e) =>
                Draw(n, r, m, e) - Draw(n, r, m, black);
            var up = Vector3.Normalize(new Vector3(.2f, 0, 1));
            var down = Vector3.Normalize(new Vector3(.2f, 0, -1));
            float top = Ambient(up, 1, 0, gradient), bottom = Ambient(down, 1, 0, gradient);
            Check(top > bottom * 1.5f && bottom > 0, $"gradient diffuse up={top}, down={bottom}");
            float sharp = Ambient(Vector3.UnitX, .05f, 1, patch);
            float broad = Ambient(Vector3.UnitX, .6f, 1, patch);
            var off = Vector3.Normalize(new Vector3(1, 0, .3f));
            float sharpOff = Ambient(off, .05f, 1, patch), broadOff = Ambient(off, .6f, 1, patch);
            Check(sharp > broad * 2 && broadOff > sharpOff * 2 && broadOff > .03f,
                $"reflection peak sharp={sharp}, rough={broad}; shoulder sharp={sharpOff}, rough={broadOff}");
            float face = Ambient(Vector3.UnitX, .1f, 0, white);
            float rim = Ambient(Vector3.Normalize(new Vector3(.1f, 0, 1)), .1f, 0, white);
            Check(rim > face + .2f, $"dielectric Fresnel face={face}, rim={rim}");
            File.WriteAllText(Path.Combine(settings.WorkDirectory, "r2-gpu-measurements.txt"),
                $"diffuse up={top}, down={bottom}\nmetal sharp={sharp}, rough={broad}, offSharp={sharpOff}, offRough={broadOff}\nFresnel face={face}, rim={rim}\n");
        });
        registry.Add("ibl-gpu", "HDR file publication, invalid replacement and GL texture lifecycle", () =>
        {
            using var context = new HeadlessGlContext();
            var gl = context.Gl;
            var renderer = new SceneRenderer(gl);
            using var target = new CaptureTarget(gl, 48, 48);
            using var mesh = Card(Vector3.UnitX, .5f, 0);
            mesh.Upload(gl);
            uint Id(string field) => (uint)typeof(SceneRenderer).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
            void Draw() => renderer.Draw(mesh, null, new OrbitCamera { Distance = 5, YawDegrees = 0, PitchDegrees = 0 }, 48, 48, 1, target.Handle);
            try
            {
                string path = Path.Combine(settings.WorkDirectory, "owned-world.hdr");
                var header = System.Text.Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 1\n");
                File.WriteAllBytes(path, header.Concat(new byte[] {128, 64, 32, 130}).ToArray());
                Draw();
                uint previousCube = Id("_envMap"), previousLut = Id("_brdfLut");
                renderer.LoadEnvironmentAsync(path).GetAwaiter().GetResult();
                Check(Id("_envMap") == previousCube, "worker performed GL publication");
                Draw();
                Check(!gl.IsTexture(previousCube) && !gl.IsTexture(previousLut), "replaced environment leaked textures");
                float Pixel()
                {
                    gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, renderer.PostChainTargets.ColorFbo);
                    gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
                    var pixel = new float[4];
                    gl.ReadPixels<float>(24, 24, 1, 1, PixelFormat.Rgba, PixelType.Float, pixel.AsSpan());
                    return pixel[0];
                }
                float fromFile = Pixel();
                renderer.SetEnvironment(Prepare(_ => new Vector3(2, 1, .5f)));
                Draw();
                Check(MathF.Abs(fromFile - Pixel()) < .002f, "HDR file radiance differs from the equivalent linear environment");
                for (int i = 0; i < 3; i++)
                {
                    previousCube = Id("_envMap"); previousLut = Id("_brdfLut");
                    renderer.SetEnvironment(Prepare(_ => new Vector3(i + 1)));
                    Check(!gl.IsTexture(previousCube) && !gl.IsTexture(previousLut), "reload leaked textures");
                    Draw();
                }
                uint cube = Id("_envMap"), lut = Id("_brdfLut");
                var bad = Prepare(_ => Vector3.One) with { Irradiance = new Vector3[1] };
                try { renderer.SetEnvironment(bad); throw new Exception("accepted invalid environment"); }
                catch (ArgumentException) { }
                try { renderer.LoadEnvironmentAsync(path + ".missing").GetAwaiter().GetResult(); throw new Exception("accepted missing HDR"); }
                catch (FileNotFoundException) { }
                using var cancel = new CancellationTokenSource(); cancel.Cancel();
                try { renderer.LoadEnvironmentAsync(path, cancel.Token).GetAwaiter().GetResult(); throw new Exception("ignored cancellation"); }
                catch (OperationCanceledException) { }
                Draw();
                Check(Id("_envMap") == cube && Id("_brdfLut") == lut, "failed load replaced the live environment");
                renderer.Dispose();
                Check(!gl.IsTexture(cube) && !gl.IsTexture(lut), "dispose leaked environment textures");
                Check(gl.GetError() == GLEnum.NoError, "GL error in environment lifecycle");
            }
            finally { mesh.ReleaseGpu(gl); renderer.Dispose(); }
        });
    }

    static IblBaker.PreparedEnvironment Prepare(Func<Vector3, Vector3> sky)
    {
        const int w = 128, h = 64;
        var pixels = new Vector3[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float phi = ((x + .5f) / w - .5f) * 2 * MathF.PI, theta = (y + .5f) / h * MathF.PI;
            pixels[y * w + x] = sky(new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Sin(theta) * MathF.Sin(phi), MathF.Cos(theta)));
        }
        return IblBaker.Prepare(new WorldEnvironment(w, h, pixels), 32, 512);
    }

    static ViewportMesh Card(Vector3 n, float rough, float metal)
    {
        var vertices = new List<float>();
        foreach (var p in new[] {new Vector3(0,-1,-1), new Vector3(0,1,-1), new Vector3(0,1,1), new Vector3(0,-1,1)})
            vertices.AddRange(new[] {p.X,p.Y,p.Z,n.X,n.Y,n.Z,0,0,0,0,1,0,0,0,0,0,0,1,0,1f});
        return new ViewportMesh { Vertices = vertices.ToArray(), Indices = new uint[] {0,1,2,0,2,3},
            Batches = new[] {new CpuMesh.DrawBatch("card", new MaterialPalette.Entry("card", .5f,.5f,.5f,rough,metal),0,6)},
            Centre = Vector3.Zero, Radius = 2, TriangleCount = 2, BodyCount = 1 };
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
