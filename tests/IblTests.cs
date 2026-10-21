using System;
using System.Numerics;
using System.IO;
using System.Text;
using Marionette.Desktop;

internal static class IblTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        IblGpuTests.Register(registry, settings);
        registry.Add("ibl", "specular cube has explicit roughness mip chain and GL face axes", () =>
        {
            var axes = new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
            for (int i = 0; i < 6; i++) Near(IblBaker.CubeDirection(i, 0.5f, 0.5f), axes[i], 0);
            var source = new WorldEnvironment(1, 1, new[] { new Vector3(4, 2, 1) });
            var mips = IblBaker.BakeSpecularCube(source, 8, 64);
            if (mips.Length != 4 || mips[0].Roughness != 0 || mips[3].Roughness != 1)
                throw new Exception("Incomplete roughness mip chain");
            for (int i = 0; i < mips.Length; i++)
            {
                if (mips[i].Size != (8 >> i) || mips[i].Faces.Length != 6) throw new Exception("Invalid mip dimensions");
                foreach (var face in mips[i].Faces)
                {
                    if (face.Length != mips[i].Size * mips[i].Size) throw new Exception("Invalid face dimensions");
                    foreach (var p in face) Near(p, new Vector3(4, 2, 1), 0.001f);
                }
            }
        });
        registry.Add("ibl", "HDR flat and RLE preserve linear radiance", () =>
        {
            using var flat = Hdr(2, 1, new byte[] { 128, 64, 32, 130, 128, 64, 32, 130 });
            Near(WorldEnvironment.ReadRadiance(flat).Sample(Vector3.UnitX), new Vector3(2, 1, 0.5f), 0);
            using var rle = Hdr(8, 1, new byte[] { 2, 2, 0, 8, 136, 128, 136, 64, 136, 32, 136, 130 });
            Near(WorldEnvironment.ReadRadiance(rle).Sample(Vector3.UnitY), new Vector3(2, 1, 0.5f), 0);
        });
        registry.Add("ibl", "HDR truncation invalid runs and orientation rejected", () =>
        {
            foreach (var bytes in new[] {
                new byte[] { 2, 2, 0, 8, 0 },
                new byte[] { 2, 2, 0, 8, 137, 128 },
                new byte[] { 2, 2, 0, 7 },
                new byte[] { 2, 2, 0, 8, 136 } })
            {
                using var input = Hdr(8, 1, bytes);
                try { WorldEnvironment.ReadRadiance(input); throw new Exception("accepted corrupt HDR"); }
                catch (InvalidDataException) { }
                catch (EndOfStreamException) { }
            }
            using var wrong = new MemoryStream(Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n+Y 1 +X 1\n"));
            try { WorldEnvironment.ReadRadiance(wrong); throw new Exception("accepted unsupported orientation"); }
            catch (InvalidDataException) { }
        });
        registry.Add("ibl", "equirect sampler wraps seam and clamps poles", () =>
        {
            var pixels = new[] { new Vector3(2), new Vector3(2), new Vector3(2), new Vector3(2),
                new Vector3(0.5f), new Vector3(0.5f), new Vector3(0.5f), new Vector3(0.5f) };
            var env = new WorldEnvironment(4, 2, pixels);
            pixels[0] = new Vector3(100); // constructor must own its snapshot
            Near(env.Sample(Vector3.UnitZ), new Vector3(2), 0);
            Near(env.Sample(-Vector3.UnitZ), new Vector3(0.5f), 0);
            Near(env.Sample(new Vector3(-1, 0.00001f, 0)), env.Sample(new Vector3(-1, -0.00001f, 0)), 0.0001f);
            var baker = new IblBaker();
            baker.BakeEnvironment(env);
            if (baker.EvaluateIrradiance(Vector3.UnitZ).X <= baker.EvaluateIrradiance(-Vector3.UnitZ).X)
                throw new Exception("decoded environment not used by baker");
        });
        registry.Add("ibl", "GGX prefilter conserves constant sky and broadens bright patch", () =>
        {
            Near(IblBaker.PrefilterSpecular(_ => new Vector3(3, 2, 1), Vector3.UnitZ, 0.6f), new Vector3(3, 2, 1), 0.001f);
            static Vector3 Patch(Vector3 d) => d.Z > 0.98f ? new Vector3(10) : Vector3.Zero;
            var sharp = IblBaker.PrefilterSpecular(Patch, Vector3.UnitZ, 0.05f, 8192);
            var rough = IblBaker.PrefilterSpecular(Patch, Vector3.UnitZ, 0.6f, 8192);
            var off = Vector3.Normalize(new Vector3(0.5f, 0, 1));
            var sharpOff = IblBaker.PrefilterSpecular(Patch, off, 0.05f, 8192);
            var roughOff = IblBaker.PrefilterSpecular(Patch, off, 0.6f, 8192);
            if (sharp.X < 9 || rough.X >= sharp.X * 0.5f || roughOff.X < 0.05f || roughOff.X <= sharpOff.X * 2)
                throw new Exception($"No GGX broadening: {sharp}, {rough}, {sharpOff}, {roughOff}");
        });
        registry.Add("ibl", "BRDF mirror limit Fresnel and LUT finite energy", () =>
        {
            foreach (float ndv in new[] { 0.1f, 0.5f, 1f })
            {
                var integrated = IblBaker.IntegrateBrdf(ndv, 0);
                float f = MathF.Pow(1 - ndv, 5);
                if (Vector2.Distance(integrated, new Vector2(1 - f, f)) > 0.0001f)
                    throw new Exception("Mirror limit does not match Schlick Fresnel");
            }
            foreach (var p in IblBaker.BakeBrdfLut(16))
                if (!float.IsFinite(p.X + p.Y) || p.X < 0 || p.Y < 0 || p.X + p.Y > 1.02f)
                    throw new Exception($"Invalid BRDF energy {p}");
            var rim = IblBaker.IntegrateBrdf(0.1f, 0.1f, 8192);
            var face = IblBaker.IntegrateBrdf(1, 0.1f, 8192);
            if (rim.X * 0.04f + rim.Y <= face.X * 0.04f + face.Y)
                throw new Exception("Missing dielectric Fresnel rim");
        });
        registry.Add("ibl", "constant HDR sky integrates to pi radiance", () =>
        {
            var b = new IblBaker();
            var sky = new Vector3(2, 4, 8);
            b.BakeIrradiance(_ => sky);
            foreach (var n in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
                Near(b.EvaluateIrradiance(n), sky * MathF.PI, 0.002f);
        });
        registry.Add("ibl", "gradient agrees with independent Monte Carlo reference", () =>
        {
            static Vector3 Sky(Vector3 d) => new Vector3(1, 2, 3) * (1 + 0.8f * d.Z);
            var b = new IblBaker();
            b.BakeIrradiance(Sky);
            var random = new Random(723);
            foreach (var n in new[] { Vector3.UnitZ, -Vector3.UnitZ, Vector3.Normalize(new Vector3(1, 2, 3)) })
            {
                Vector3 sum = Vector3.Zero;
                const int count = 200000;
                for (int i = 0; i < count; i++)
                {
                    float z = (float)(2 * random.NextDouble() - 1);
                    float phi = (float)(2 * Math.PI * random.NextDouble());
                    float r = MathF.Sqrt(1 - z * z);
                    var d = new Vector3(r * MathF.Cos(phi), r * MathF.Sin(phi), z);
                    sum += Sky(d) * MathF.Max(0, Vector3.Dot(n, d));
                }
                // 1% absolute relative-to-reference-length tolerance for seeded MC noise.
                var reference = sum * (4 * MathF.PI / count);
                Near(b.EvaluateIrradiance(n), reference, reference.Length() * 0.01f);
            }
            if (b.EvaluateIrradiance(Vector3.UnitZ).X <= b.EvaluateIrradiance(-Vector3.UnitZ).X)
                throw new Exception("gradient orientation inverted");
        });
        registry.Add("ibl", "invalid bake preserves previous environment", () =>
        {
            var b = new IblBaker();
            b.BakeProceduralFallback();
            var before = b.EvaluateIrradiance(Vector3.UnitZ);
            try { b.BakeIrradiance(_ => new Vector3(float.NaN)); throw new Exception("accepted NaN"); }
            catch (ArgumentException) { }
            Near(b.EvaluateIrradiance(Vector3.UnitZ), before, 0);
            try { b.EvaluateIrradiance(Vector3.Zero); throw new Exception("accepted zero normal"); }
            catch (ArgumentException) { }
        });
        registry.Add("ibl", "baker starts without environment", () =>
        {
            var b = new IblBaker();
            if (b.HasEnvironment) throw new Exception("unexpected environment");
        });
        registry.Add("ibl", "procedural fallback marks environment", () =>
        {
            var b = new IblBaker();
            b.BakeProceduralFallback();
            if (!b.HasEnvironment) throw new Exception("no environment");
            if (b.IrradianceSH[0] <= 0f) throw new Exception("monopole zero");
        });
        registry.Add("ibl", "cosine weight clamps negative", () =>
        {
            if (IblBaker.CosineWeight(-0.5f) != 0f) throw new Exception("clamp failed");
            if (IblBaker.CosineWeight(0.5f) != 0.5f) throw new Exception("pass failed");
        });
    }

    static void Near(Vector3 actual, Vector3 expected, float tolerance)
    {
        if (!float.IsFinite(actual.Length()) || Vector3.Distance(actual, expected) > tolerance)
            throw new Exception($"Expected {expected}, got {actual}; tolerance {tolerance}");
    }

    static MemoryStream Hdr(int width, int height, byte[] pixels)
    {
        var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {height} +X {width}\n"));
        stream.Write(pixels);
        stream.Position = 0;
        return stream;
    }
}
