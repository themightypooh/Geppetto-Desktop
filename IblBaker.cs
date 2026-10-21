#nullable enable
using System;
using System.Numerics;
using System.Threading;

namespace Marionette.Desktop;

// CPU-only environment projection. Radiance and irradiance are linear RGB, Z-up.
// Decode/projection can run on a worker; no GL resources are created here.
public sealed class IblBaker
{
    public sealed record PreparedEnvironment(Vector3[] Irradiance, SpecularMip[] Specular, Vector2[] Brdf, int LutSize)
    {
        /// <summary>Validate before changing live GL resources. Payload ownership
        /// transfers to the renderer; do not mutate arrays during/after publication.</summary>
        public void Validate()
        {
            if (Irradiance is null || Irradiance.Length != 9 || Specular is null || Specular.Length < 2
                || Specular.Length > 9 || Brdf is null || LutSize < 2 || LutSize > 512 || Brdf.Length != LutSize * LutSize)
                throw new ArgumentException("Invalid prepared IBL payload dimensions.");
            foreach (var coefficient in Irradiance)
                if (!Finite(coefficient)) throw new ArgumentException("Invalid IBL irradiance coefficient.");
            int size = Specular[0]?.Size ?? 0;
            if (size < 2 || size > 256 || (size & (size - 1)) != 0 || Specular.Length != 1 + (int)Math.Log2(size))
                throw new ArgumentException("IBL requires a complete power-of-two mip chain.");
            for (int level = 0; level < Specular.Length; level++)
            {
                var mip = Specular[level];
                if (mip is null || mip.Size != size >> level || mip.Faces is null || mip.Faces.Length != 6
                    || !float.IsFinite(mip.Roughness) || MathF.Abs(mip.Roughness - (float)level / (Specular.Length - 1)) > 1e-6f)
                    throw new ArgumentException("Invalid IBL roughness mip.");
                foreach (var face in mip.Faces)
                {
                    if (face is null || face.Length != mip.Size * mip.Size) throw new ArgumentException("Invalid IBL cube face.");
                    foreach (var pixel in face)
                        if (!Finite(pixel) || pixel.X < 0 || pixel.Y < 0 || pixel.Z < 0
                            || pixel.X > 65504 || pixel.Y > 65504 || pixel.Z > 65504)
                            throw new ArgumentException("IBL radiance exceeds finite RGB16F range.");
                }
            }
            foreach (var pixel in Brdf)
                if (!float.IsFinite(pixel.X) || !float.IsFinite(pixel.Y) || pixel.X < 0 || pixel.Y < 0 || pixel.X > 2 || pixel.Y > 2)
                    throw new ArgumentException("Invalid split-sum BRDF LUT.");
        }

        static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    public static PreparedEnvironment Prepare(WorldEnvironment environment, int cubeSize = 32, int samples = 1024,
        CancellationToken cancellationToken = default)
    {
        var baker = new IblBaker();
        cancellationToken.ThrowIfCancellationRequested();
        baker.BakeEnvironment(environment, cancellationToken: cancellationToken);
        var prepared = new PreparedEnvironment(baker.IrradianceCoefficients.ToArray(),
            BakeSpecularCube(environment, cubeSize, samples, cancellationToken), BakeBrdfLut(64, samples, cancellationToken), 64);
        prepared.Validate();
        return prepared;
    }

    public static WorldEnvironment ProceduralEnvironment()
    {
        const int width = 128, height = 64;
        var pixels = new Vector3[width * height];
        for (int y = 0; y < height; y++)
        {
            float z = MathF.Cos(MathF.PI * (y + 0.5f) / height);
            var color = Vector3.Lerp(new Vector3(0.04f, 0.035f, 0.03f), new Vector3(0.6f, 0.8f, 1.2f), (z + 1) * 0.5f);
            for (int x = 0; x < width; x++) pixels[y * width + x] = color;
        }
        return new WorldEnvironment(width, height, pixels);
    }
    public string? SourcePath { get; set; }
    public bool HasEnvironment { get; private set; }
    readonly Vector3[] _irradiance = new Vector3[9];
    public ReadOnlySpan<Vector3> IrradianceCoefficients => _irradiance;
    // Compatibility scalar view: red-channel coefficients, not luminance.
    public float[] IrradianceSH => Array.ConvertAll(_irradiance, value => value.X);

    public static float CosineWeight(float cosine) => MathF.Max(0, cosine);

    public void BakeEnvironment(WorldEnvironment environment, int sampleCount = 32768, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);
        BakeIrradiance(environment.Sample, sampleCount, cancellationToken);
    }

    public void BakeProceduralFallback(int sampleCount = 32768) => BakeIrradiance(
        direction => Vector3.Lerp(new Vector3(0.04f, 0.035f, 0.03f),
            new Vector3(0.6f, 0.8f, 1.2f), (direction.Z + 1) * 0.5f), sampleCount);

    /// <summary>Projects world radiance into SH9 and convolves with max(N.L,0).
    /// EvaluateIrradiance returns irradiance (constant sky L gives pi*L); diffuse
    /// shading must multiply by albedo/pi. SH9 deliberately loses high frequencies.</summary>
    public void BakeIrradiance(Func<Vector3, Vector3> radiance, int sampleCount = 32768, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(radiance);
        if (sampleCount < 64 || sampleCount > 4194304)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        var projected = new Vector3[9];
        var sums = new double[27];
        Span<float> basis = stackalloc float[9];
        for (int i = 0; i < sampleCount; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            float z = 1 - 2 * ((i + 0.5f) / sampleCount);
            double phi = i * 2.39996322972865332;
            float radius = MathF.Sqrt(MathF.Max(0, 1 - z * z));
            var direction = new Vector3(radius * (float)Math.Cos(phi), radius * (float)Math.Sin(phi), z);
            Vector3 value = radiance(direction);
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)
                || value.X < 0 || value.Y < 0 || value.Z < 0)
                throw new ArgumentException("Environment radiance must be finite and nonnegative.", nameof(radiance));
            Basis(direction, basis);
            for (int j = 0; j < 9; j++)
            {
                sums[j * 3] += (double)value.X * basis[j];
                sums[j * 3 + 1] += (double)value.Y * basis[j];
                sums[j * 3 + 2] += (double)value.Z * basis[j];
            }
        }
        for (int j = 0; j < 9; j++)
        {
            float convolution = j == 0 ? MathF.PI : j < 4 ? 2 * MathF.PI / 3 : MathF.PI / 4;
            double scale = (4 * Math.PI / sampleCount) * convolution;
            projected[j] = new Vector3((float)(sums[j * 3] * scale),
                (float)(sums[j * 3 + 1] * scale), (float)(sums[j * 3 + 2] * scale));
        }
        cancellationToken.ThrowIfCancellationRequested();
        projected.CopyTo(_irradiance, 0);
        HasEnvironment = true;
    }

    public Vector3 EvaluateIrradiance(Vector3 normal)
    {
        float length = normal.Length();
        if (!float.IsFinite(length) || length < 1e-10f) throw new ArgumentException("Normal must be finite and nonzero.", nameof(normal));
        Span<float> basis = stackalloc float[9];
        Basis(normal / length, basis);
        Vector3 result = Vector3.Zero;
        for (int i = 0; i < 9; i++) result += _irradiance[i] * basis[i];
        return Vector3.Max(result, Vector3.Zero);
    }

    /// <summary>GGX split-sum prefilter at reflection direction (N=V=R).
    /// Bake this over each cubemap mip with roughness=mip/(mipCount-1).
    /// Roughness zero preserves the unfiltered source.</summary>
    public static Vector3 PrefilterSpecular(Func<Vector3, Vector3> radiance, Vector3 reflection,
        float roughness, int sampleCount = 1024, CancellationToken cancellationToken = default)
        => PrefilterSpecularCore(radiance, reflection, roughness, sampleCount, cancellationToken, null);

    static Vector3 PrefilterSpecularCore(Func<Vector3, Vector3> radiance, Vector3 reflection,
        float roughness, int sampleCount, CancellationToken cancellationToken, WorldEnvironment? filteredSource)
    {
        ArgumentNullException.ThrowIfNull(radiance);
        ValidateIntegration(reflection, roughness, sampleCount);
        cancellationToken.ThrowIfCancellationRequested();
        var n = Vector3.Normalize(reflection);
        if (roughness == 0) return radiance(n);
        Vector3 sum = Vector3.Zero;
        double weight = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var h = GgxHalf(n, roughness, (i + 0.5f) / sampleCount, RadicalInverse((uint)i));
            var l = 2 * Vector3.Dot(n, h) * h - n;
            float ndl = MathF.Max(0, Vector3.Dot(n, l));
            if (ndl > 0)
            {
                Vector3 value;
                if (filteredSource is null) value = radiance(l);
                else
                {
                    float alpha2 = MathF.Pow(roughness, 4);
                    float nh = MathF.Max(0, Vector3.Dot(n, h));
                    float denominator = nh * nh * (alpha2 - 1) + 1;
                    float pdf = alpha2 / MathF.Max(4 * MathF.PI * denominator * denominator, 1e-20f);
                    float sampleArea = 1 / MathF.Max(sampleCount * pdf, 1e-20f);
                    float sineLatitude = MathF.Sqrt(MathF.Max(0, 1 - l.Z * l.Z));
                    float texelArea = 2 * MathF.PI * MathF.PI * MathF.Max(sineLatitude, 1f / filteredSource.Height)
                        / ((float)filteredSource.Width * filteredSource.Height);
                    float lod = MathF.Max(0, 0.5f * MathF.Log2(sampleArea / texelArea));
                    value = filteredSource.Sample(l, lod);
                }
                sum += value * ndl; weight += ndl;
            }
        }
        return weight > 0 ? sum / (float)weight : Vector3.Zero;
    }

    /// <summary>Returns A,B for F0*A+B, using Schlick Fresnel and Smith GGX.
    /// LUT texel centres are (NdotV, perceptual roughness); values stay linear.</summary>
    public static Vector2 IntegrateBrdf(float ndv, float roughness, int sampleCount = 1024, CancellationToken cancellationToken = default)
    {
        if (!float.IsFinite(ndv) || ndv <= 0 || ndv > 1) throw new ArgumentOutOfRangeException(nameof(ndv));
        ValidateIntegration(Vector3.UnitZ, roughness, sampleCount);
        var v = new Vector3(MathF.Sqrt(1 - ndv * ndv), 0, ndv);
        double a = 0, b = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var h = GgxHalf(Vector3.UnitZ, roughness, (i + 0.5f) / sampleCount, RadicalInverse((uint)i));
            float vdh = MathF.Max(0, Vector3.Dot(v, h));
            var l = 2 * vdh * h - v;
            float ndl = MathF.Max(0, l.Z), ndh = MathF.Max(0, h.Z);
            if (ndl <= 0 || ndh <= 0) continue;
            float alpha2 = MathF.Pow(roughness, 4);
            float gv = 2 * ndv / (ndv + MathF.Sqrt(alpha2 + (1 - alpha2) * ndv * ndv));
            float gl = 2 * ndl / (ndl + MathF.Sqrt(alpha2 + (1 - alpha2) * ndl * ndl));
            float visibility = gv * gl * vdh / (ndh * ndv);
            float fresnel = MathF.Pow(1 - vdh, 5);
            a += (1 - fresnel) * visibility;
            b += fresnel * visibility;
        }
        return new Vector2((float)(a / sampleCount), (float)(b / sampleCount));
    }

    public static Vector2[] BakeBrdfLut(int size = 64, int sampleCount = 1024, CancellationToken cancellationToken = default)
    {
        if (size < 2 || size > 512) throw new ArgumentOutOfRangeException(nameof(size));
        var pixels = new Vector2[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                pixels[y * size + x] = IntegrateBrdf((x + 0.5f) / size, (y + 0.5f) / size, sampleCount, cancellationToken);
        return pixels;
    }

    public sealed record SpecularMip(int Size, float Roughness, Vector3[][] Faces);

    /// <summary>CPU cubemap data, faces +X,-X,+Y,-Y,+Z,-Z, matching OpenGL's
    /// cube face mapping. Each face is row-major, texture t increases with row.
    /// Upload each level explicitly: glGenerateMipmap is not a GGX convolution.</summary>
    public static SpecularMip[] BakeSpecularCube(WorldEnvironment environment, int size = 32, int sampleCount = 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (size < 2 || size > 256 || (size & (size - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(size));
        ValidateIntegration(Vector3.UnitZ, 0, sampleCount);
        int count = 1 + (int)Math.Log2(size);
        var mips = new SpecularMip[count];
        Func<Vector3, Vector3> sample = environment.Sample;
        for (int level = 0; level < count; level++)
        {
            int edge = size >> level;
            float roughness = (float)level / (count - 1);
            var faces = new Vector3[6][];
            for (int face = 0; face < 6; face++)
            {
                faces[face] = new Vector3[edge * edge];
                for (int y = 0; y < edge; y++)
                    for (int x = 0; x < edge; x++)
                        faces[face][y * edge + x] = PrefilterSpecularCore(sample,
                            CubeDirection(face, (x + 0.5f) / edge, (y + 0.5f) / edge), roughness, sampleCount, cancellationToken, environment);
            }
            mips[level] = new SpecularMip(edge, roughness, faces);
        }
        return mips;
    }

    public static Vector3 CubeDirection(int face, float u, float v)
    {
        if (!float.IsFinite(u) || !float.IsFinite(v) || u < 0 || u > 1 || v < 0 || v > 1)
            throw new ArgumentOutOfRangeException(nameof(u));
        float s = 2 * u - 1, t = 2 * v - 1;
        return Vector3.Normalize(face switch
        {
            0 => new Vector3(1, -t, -s), 1 => new Vector3(-1, -t, s),
            2 => new Vector3(s, 1, t), 3 => new Vector3(s, -1, -t),
            4 => new Vector3(s, -t, 1), 5 => new Vector3(-s, -t, -1),
            _ => throw new ArgumentOutOfRangeException(nameof(face))
        });
    }

    static void ValidateIntegration(Vector3 direction, float roughness, int samples)
    {
        if (!float.IsFinite(direction.Length()) || direction.LengthSquared() < 1e-20f)
            throw new ArgumentException("Direction must be finite and nonzero.");
        if (!float.IsFinite(roughness) || roughness < 0 || roughness > 1)
            throw new ArgumentOutOfRangeException(nameof(roughness));
        if (samples < 64 || samples > 1048576) throw new ArgumentOutOfRangeException(nameof(samples));
    }

    static Vector3 GgxHalf(Vector3 n, float roughness, float u, float v)
    {
        float alpha2 = MathF.Pow(roughness, 4);
        float cosine = MathF.Sqrt((1 - v) / (1 + (alpha2 - 1) * v));
        float sine = MathF.Sqrt(MathF.Max(0, 1 - cosine * cosine));
        float phi = 2 * MathF.PI * u;
        var tangent = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.Z) < 0.999f ? Vector3.UnitZ : Vector3.UnitX, n));
        var bitangent = Vector3.Cross(n, tangent);
        return Vector3.Normalize(tangent * (sine * MathF.Cos(phi)) + bitangent * (sine * MathF.Sin(phi)) + n * cosine);
    }

    static float RadicalInverse(uint bits)
    {
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xaaaaaaaau) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xccccccccu) >> 2);
        bits = ((bits & 0x0f0f0f0fu) << 4) | ((bits & 0xf0f0f0f0u) >> 4);
        bits = ((bits & 0x00ff00ffu) << 8) | ((bits & 0xff00ff00u) >> 8);
        return (float)(bits * 2.3283064365386963e-10);
    }

    static void Basis(Vector3 n, Span<float> b)
    {
        b[0] = 0.2820947918f;
        b[1] = 0.4886025119f * n.Y;
        b[2] = 0.4886025119f * n.Z;
        b[3] = 0.4886025119f * n.X;
        b[4] = 1.0925484306f * n.X * n.Y;
        b[5] = 1.0925484306f * n.Y * n.Z;
        b[6] = 0.3153915653f * (3 * n.Z * n.Z - 1);
        b[7] = 1.0925484306f * n.X * n.Z;
        b[8] = 0.5462742153f * (n.X * n.X - n.Y * n.Y);
    }
}
