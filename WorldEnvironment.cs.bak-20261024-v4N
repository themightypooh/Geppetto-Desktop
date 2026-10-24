#nullable enable
using System;
using System.IO;
using System.Numerics;
using System.Text;

namespace Marionette.Desktop;

/// <summary>CPU linear-radiance equirectangular image. Decode on a worker, then
/// publish to the GL owner. Longitude zero points +X, positive longitude +Y,
/// north is +Z; image row zero is north. No exposure or gamma is applied.</summary>
public sealed class WorldEnvironment
{
    public IblBaker? Ibl { get; set; }
    readonly Vector3[] _pixels;
    readonly Vector3[][] _levels;
    readonly int[] _widths, _heights;
    public int MipCount => _levels.Length;
    public int Width { get; }
    public int Height { get; }

    public WorldEnvironment() : this(1, 1, new[] { Vector3.Zero }) { }

    public WorldEnvironment(int width, int height, ReadOnlySpan<Vector3> pixels)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 16777216 || pixels.Length != (long)width * height)
            throw new ArgumentException("Invalid environment dimensions or pixel count.");
        foreach (var p in pixels)
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) || p.X < 0 || p.Y < 0 || p.Z < 0)
                throw new ArgumentException("Environment pixels must be finite nonnegative linear RGB.");
        Width = width;
        Height = height;
        _pixels = pixels.ToArray();
        int count = 1, w = width, h = height;
        while (w > 1 || h > 1) { w = (w + 1) / 2; h = (h + 1) / 2; count++; }
        _levels = new Vector3[count][];
        _widths = new int[count]; _heights = new int[count];
        _levels[0] = _pixels; _widths[0] = width; _heights[0] = height;
        for (int level = 1; level < count; level++)
        {
            int sw = _widths[level - 1], sh = _heights[level - 1];
            int dw = (sw + 1) / 2, dh = (sh + 1) / 2;
            var source = _levels[level - 1];
            var destination = new Vector3[dw * dh];
            for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                // Integrate exact source/destination bin overlap on the sphere.
                // Equal row weights over-represent poles in an equirectangular map.
                double x0 = (double)x * sw / dw, x1 = (double)(x + 1) * sw / dw;
                double y0 = (double)y * sh / dh, y1 = (double)(y + 1) * sh / dh;
                double r = 0, g = 0, b = 0, total = 0;
                for (int sy = (int)Math.Floor(y0); sy < Math.Ceiling(y1) && sy < sh; sy++)
                {
                    double rowWeight = Math.Cos(Math.PI * Math.Max(y0, sy) / sh)
                        - Math.Cos(Math.PI * Math.Min(y1, sy + 1) / sh);
                    for (int sx = (int)Math.Floor(x0); sx < Math.Ceiling(x1) && sx < sw; sx++)
                    {
                        double weight = rowWeight * (Math.Min(x1, sx + 1) - Math.Max(x0, sx));
                        var pixel = source[sy * sw + sx];
                        r += pixel.X * weight; g += pixel.Y * weight; b += pixel.Z * weight; total += weight;
                    }
                }
                destination[y * dw + x] = new Vector3((float)(r / total), (float)(g / total), (float)(b / total));
            }
            _levels[level] = destination; _widths[level] = dw; _heights[level] = dh;
        }
    }

    public Vector3 Sample(Vector3 direction) => Sample(direction, 0);

    /// <summary>Trilinear source filtering. Mips conserve spherical radiance,
    /// including odd image dimensions and the high-latitude rows.</summary>
    public Vector3 Sample(Vector3 direction, float lod)
    {
        if (!float.IsFinite(lod)) throw new ArgumentOutOfRangeException(nameof(lod));
        float length = direction.Length();
        if (!float.IsFinite(length) || length < 1e-10f) throw new ArgumentException("Direction must be finite and nonzero.");
        direction /= length;
        float u = MathF.Atan2(direction.Y, direction.X) / (2 * MathF.PI) + 0.5f;
        float v = MathF.Acos(Math.Clamp(direction.Z, -1, 1)) / MathF.PI;
        lod = Math.Clamp(lod, 0, _levels.Length - 1);
        int lower = (int)lod, upper = Math.Min(lower + 1, _levels.Length - 1);
        var a = SampleLevel(u, v, lower);
        return upper == lower ? a : Vector3.Lerp(a, SampleLevel(u, v, upper), lod - lower);
    }

    Vector3 SampleLevel(float u, float v, int level)
    {
        int width = _widths[level], height = _heights[level];
        var pixels = _levels[level];
        float x = u * width - 0.5f, y = Math.Clamp(v * height - 0.5f, 0, height - 1);
        int left = (int)MathF.Floor(x), top = (int)MathF.Floor(y);
        float fx = x - left, fy = y - top;
        int x0 = (left % width + width) % width, x1 = (x0 + 1) % width;
        int bottom = Math.Min(top + 1, height - 1);
        return Vector3.Lerp(Vector3.Lerp(pixels[top * width + x0], pixels[top * width + x1], fx),
            Vector3.Lerp(pixels[bottom * width + x0], pixels[bottom * width + x1], fx), fy);
    }

    public static WorldEnvironment LoadRadiance(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadRadiance(stream);
    }

    /// <summary>Radiance RGBE: modern scanline RLE and flat RGBE, standard -Y +X
    /// orientation. Other orientations, XYZE, and legacy repeat markers fail explicitly.</summary>
    public static WorldEnvironment ReadRadiance(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        string signature = Line(stream);
        if (signature != "#?RADIANCE" && signature != "#?RGBE") throw new InvalidDataException("Not a Radiance HDR image.");
        bool format = false;
        string line;
        int headerBytes = signature.Length;
        while ((line = Line(stream)).Length != 0)
        {
            headerBytes += line.Length + 1;
            if (headerBytes > 65536) throw new InvalidDataException("HDR header too large.");
            if (line.StartsWith("FORMAT=", StringComparison.Ordinal))
            {
                if (line != "FORMAT=32-bit_rle_rgbe") throw new InvalidDataException("Only RGBE HDR is supported.");
                format = true;
            }
        }
        if (!format) throw new InvalidDataException("Missing RGBE format.");
        var size = Line(stream).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (size.Length != 4 || size[0] != "-Y" || size[2] != "+X"
            || !int.TryParse(size[1], out int height) || !int.TryParse(size[3], out int width)
            || width <= 0 || height <= 0 || (long)width * height > 16777216)
            throw new InvalidDataException("Expected bounded -Y height +X width HDR image.");
        var pixels = new Vector3[width * height];
        var scan = new byte[width * 4];
        for (int y = 0; y < height; y++)
        {
            byte a = Byte(stream), b = Byte(stream), c = Byte(stream), d = Byte(stream);
            if (width >= 8 && width <= 32767 && a == 2 && b == 2 && (c & 128) == 0)
            {
                if ((c << 8 | d) != width) throw new InvalidDataException("HDR scanline width mismatch.");
                for (int channel = 0; channel < 4; channel++)
                {
                    int x = 0;
                    while (x < width)
                    {
                        int code = Byte(stream);
                        int count = code > 128 ? code - 128 : code;
                        if (count == 0 || count > width - x) throw new InvalidDataException("Invalid HDR scanline run.");
                        if (code > 128)
                        {
                            byte value = Byte(stream);
                            for (int j = 0; j < count; j++) scan[(x++) * 4 + channel] = value;
                        }
                        else for (int j = 0; j < count; j++) scan[(x++) * 4 + channel] = Byte(stream);
                    }
                }
            }
            else
            {
                scan[0] = a; scan[1] = b; scan[2] = c; scan[3] = d;
                stream.ReadExactly(scan.AsSpan(4));
                for (int x = 0; x < width; x++)
                    if (scan[x * 4] == 1 && scan[x * 4 + 1] == 1 && scan[x * 4 + 2] == 1)
                        throw new InvalidDataException("Legacy HDR repeat encoding is unsupported.");
            }
            for (int x = 0; x < width; x++)
            {
                int offset = x * 4;
                byte exponent = scan[offset + 3];
                float scale = exponent == 0 ? 0 : MathF.ScaleB(1, exponent - 136);
                pixels[y * width + x] = new Vector3(scan[offset], scan[offset + 1], scan[offset + 2]) * scale;
            }
        }
        return new WorldEnvironment(width, height, pixels);
    }

    static byte Byte(Stream stream)
    {
        int value = stream.ReadByte();
        if (value < 0) throw new EndOfStreamException("Truncated HDR image.");
        return (byte)value;
    }

    static string Line(Stream stream)
    {
        var text = new StringBuilder();
        for (int i = 0; i < 8192; i++)
        {
            byte value = Byte(stream);
            if (value == 10) return text.ToString().TrimEnd('\r');
            text.Append((char)value);
        }
        throw new InvalidDataException("HDR header line too long.");
    }
}
