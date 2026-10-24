using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Effigy.Render;

/// <summary>How a sprite tile is photographed. Ortho views are the measurable
/// ones (the same convention as <see cref="ModelRender"/>); persp is the
/// prettier one, for icons that want depth.</summary>
public enum SpriteCamera { OrthoIso, OrthoSide, Persp }

/// <summary>Options for <see cref="SpriteSheetBaker.Bake"/>. The grid is
/// directions across, frames down: one row per animation frame, one column
/// per direction, so a consumer reads tile (direction, frame) at
/// (direction * Tile, frame * Tile).</summary>
public sealed class SpriteSheetOptions
{
    /// <summary>1, 4, or 8. 1 is an icon sheet; 8 is the full 8-way walk.</summary>
    public int Directions = 8;
    /// <summary>Frames sampled across the clip (1 = the rest pose / first frame).</summary>
    public int Frames = 1;
    /// <summary>Tile edge in pixels. The roadmap's standard sizes are 64, 128, 256;
    /// anything from 16 to 512 is accepted so a caller is not forced to pad.</summary>
    public int Tile = 128;
    public SpriteCamera Camera = SpriteCamera.OrthoIso;
    /// <summary>Render at 4x and nearest-neighbour downsample, so a pixel-art
    /// sprite has crisp texels instead of a blurred shrink.</summary>
    public bool PixelArt = false;
    /// <summary>Palette size for pixel-art colour quantisation: 0 (off), 16, 32, or 64.
    /// Applies to the colour sheet only — quantising normals would break lighting.</summary>
    public int PaletteSize = 0;
    /// <summary>With pixel art, paint a 1px outline round the silhouette.</summary>
    public bool Outline = true;
    public int OutlineColour = 0x000000;
    /// <summary>Empty space round the model, as a fraction of the tile.</summary>
    public float Margin = 0.10f;
}

/// <summary>What <see cref="SpriteSheetBaker.Bake"/> produced. Colour is RGB
/// with a separate coverage mask because <see cref="Raster"/> PNGs carry no
/// alpha: <see cref="Alpha"/> is 255 where a triangle covers the pixel, 0
/// elsewhere, and <see cref="WriteFiles"/> writes the colour sheet as a true
/// RGBA PNG. Normal is a VIEW-space (screen-space) normal map, +Z toward the
/// viewer — not tangent-space: the baker cannot assume UVs or tangents exist.
/// Material packs depth in R (near = 255, far = 0, per tile) and emission in
/// G; G is 0 today because <see cref="PolyMesh"/> carries no emission channel
/// (paint-emission sampling is a follow-up, not faked here).</summary>
public sealed class SpriteSheetResult
{
    public bool Ok;
    public string Error = "";
    public int Directions, Frames, Tile;
    public Raster Colour;
    public byte[] Alpha;
    public Raster Normal;
    public Raster Material;
    public List<string> Notes = new();

    /// <summary>Write the colour sheet as RGBA <c>out.png</c> plus
    /// <c>out_n.png</c> (normal) and <c>out_m.png</c> (depth/emission), the
    /// suffix inserted before the extension.</summary>
    public void WriteFiles(string outPath)
    {
        if (!Ok) throw new InvalidOperationException($"Sprite sheet failed: {Error}");
        ArgumentNullException.ThrowIfNull(Colour);
        ArgumentNullException.ThrowIfNull(Alpha);
        ArgumentNullException.ThrowIfNull(Normal);
        ArgumentNullException.ThrowIfNull(Material);
        File.WriteAllBytes(outPath, SpriteSheetBaker.EncodeRgbaPng(Colour, Alpha));
        Normal.SavePng(SpriteSheetBaker.Suffixed(outPath, "_n"));
        Material.SavePng(SpriteSheetBaker.Suffixed(outPath, "_m"));
    }
}

/// <summary>Lane N: bake a studio into a direction x frame sprite sheet plus
/// matching normal and depth/emission atlases, so 2D sprites and UI icons
/// modelled in Geppetto react to dynamic 2D lighting in s&amp;box.
/// This is a clean render, not <see cref="ModelRender"/>: no grid, no wires,
/// no labels, no per-body legend — a sprite tile must contain only the model.
/// Colour shading reuses ModelRender's three-light model (key over the
/// viewer's right shoulder, fill from the left, rim from behind-above) so a
/// baked sprite reads like the viewport. Animated frames pose rigged meshes
/// through <see cref="SkinBinder.Deform"/>; an unrigged mesh, or a clip with
/// no skeleton, renders its rest pose for every frame and says so in
/// <see cref="SpriteSheetResult.Notes"/> instead of pretending to move.
/// The <c>effigy.sh spritesheet</c> CLI dispatch and the BuildScript
/// <c>spritesheet</c> verb belong to Lane P (they live in EffigyCli.cs /
/// BuildScript.Bake.cs, outside this lane); this file is the bake they call.</summary>
public static class SpriteSheetBaker
{
    static readonly int[] Palette =
    {
        0x8fa3b8, 0xd4a35c, 0x7bbf8f, 0xc77b7b, 0x9a8fd0, 0x67b8c6, 0xc9a2d6, 0xb5b56a,
    };

    public static SpriteSheetResult Bake(PartStudio studio, SpriteSheetOptions options = null)
        => Bake(studio, null, options);

    public static SpriteSheetResult Bake(PartStudio studio, RigAnimFile clip, SpriteSheetOptions options = null)
    {
        options ??= new SpriteSheetOptions();
        if (studio is null) throw new ArgumentNullException(nameof(studio));
        if (options.Directions is not (1 or 4 or 8))
            throw new ArgumentException("SpriteSheet directions must be 1, 4, or 8.");
        if (options.Frames < 1) throw new ArgumentException("SpriteSheet frames must be >= 1.");
        if (options.Tile < 16 || options.Tile > 512) throw new ArgumentException("SpriteSheet tile must be 16..512.");
        if (options.PaletteSize is not (0 or 16 or 32 or 64))
            throw new ArgumentException("SpriteSheet palette must be 0 (off), 16, 32, or 64.");
        if (options.Margin < 0f || options.Margin >= 0.5f) throw new ArgumentException("SpriteSheet margin must be 0..0.5.");

        var bodies = studio.Bodies
            .Where(b => b?.Mesh is not null && b.Visible && b.Mesh.Positions.Count > 0 && b.Mesh.Faces.Count > 0)
            .ToList();
        if (bodies.Count == 0)
            return new SpriteSheetResult { Ok = false, Error = "No visible bodies with geometry to bake." };

        var skeleton = studio.Rig;
        bool animated = clip is not null && skeleton is { Count: > 0 } && bodies.Any(b => b.Mesh.IsRigged);

        int dirs = options.Directions, frames = options.Frames, tile = options.Tile;
        int render = options.PixelArt ? tile * 4 : tile;
        var colour = new Raster(dirs * tile, frames * tile, 0x000000);
        var normal = new Raster(dirs * tile, frames * tile, Raster.Rgb(128, 128, 255));
        var material = new Raster(dirs * tile, frames * tile, 0x000000);
        var alpha = new byte[dirs * tile * frames * tile];

        // One scale for the whole sheet, from the rest pose over all directions:
        // direction tiles must share a scale or a walk cycle changes size mid-turn.
        var restBounds = RestBounds(bodies);
        var views = new RenderView[dirs];
        for (int d = 0; d < dirs; d++)
        {
            float yaw = 360f * d / dirs;
            views[d] = options.Camera == SpriteCamera.OrthoSide ? RenderView.Turn(yaw, 0f) : RenderView.Turn(yaw, 18f);
        }
        float span = 1e-3f;
        var centres = new (float cx, float cy)[dirs];
        for (int d = 0; d < dirs; d++)
        {
            var (right, up, _) = views[d].Axes();
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var p in restBounds)
            {
                float sx = Vec3.Dot(p, right), sy = Vec3.Dot(p, up);
                minX = MathF.Min(minX, sx); maxX = MathF.Max(maxX, sx);
                minY = MathF.Min(minY, sy); maxY = MathF.Max(maxY, sy);
            }
            centres[d] = ((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
            span = MathF.Max(span, MathF.Max(maxX - minX, maxY - minY));
        }
        float scale = render * (1f - options.Margin * 2f) / span;
        var centre = restBounds.Aggregate(Vec3.Zero, (a, p) => a + p) / restBounds.Count;
        float radius = 1e-3f;
        foreach (var p in restBounds) radius = MathF.Max(radius, (p - centre).Length);
        const float fov = 30f;
        float tanHalf = MathF.Tan(fov * MathF.PI / 360f);
        float perspDist = radius / MathF.Sin(fov * MathF.PI / 360f) * 1.08f;

        var result = new SpriteSheetResult { Ok = true, Directions = dirs, Frames = frames, Tile = tile };
        if (clip is not null && !animated)
            result.Notes.Add("clip supplied but no rigged mesh + skeleton to pose; every frame renders the rest pose.");
        if (options.Camera == SpriteCamera.Persp)
            result.Notes.Add("persp camera: tiles are perspective renders, not measurable drawings.");

        for (int f = 0; f < frames; f++)
        {
            float clipFrame = clip is null || frames == 1 ? 0f : clip.LastFrame * f / (frames - 1);
            var posed = animated
                ? PoseBodies(bodies, skeleton!, clip!, clipFrame)
                : bodies.Select(b => b.Mesh.Positions.ToArray()).ToList();
            for (int d = 0; d < dirs; d++)
            {
                var tileBuf = RenderTile(bodies, posed, views[d], render, scale, centres[d],
                    centre, perspDist, tanHalf, options.Camera == SpriteCamera.Persp);
                Blit(colour, normal, material, alpha, tileBuf, d, f, tile, render);
            }
        }

        if (options.PixelArt && options.PaletteSize > 0)
            Quantize(colour, alpha, options.PaletteSize);
        if (options.PixelArt && options.Outline)
            Outline(colour, alpha, tile, dirs, frames, options.OutlineColour);

        result.Colour = colour; result.Alpha = alpha; result.Normal = normal; result.Material = material;
        return result;
    }

    static List<Vec3> RestBounds(List<Body> bodies)
    {
        var pts = new List<Vec3>();
        foreach (var b in bodies) pts.AddRange(b.Mesh.Positions);
        return pts;
    }

    static List<Vec3[]> PoseBodies(List<Body> bodies, Skeleton skeleton, RigAnimFile clip, float frame)
    {
        var bindLocal = skeleton.Bones.Select(b => b.Local).ToArray();
        var bindWorld = RigAnimFile.ModelSpace(skeleton, bindLocal);
        var poseWorld = RigAnimFile.ModelSpace(skeleton, clip.SampleLocal(skeleton, frame));
        var posed = new List<Vec3[]>();
        foreach (var b in bodies)
            posed.Add(b.Mesh.IsRigged
                ? SkinBinder.Deform(b.Mesh.Positions, b.Mesh.Skin, bindWorld, poseWorld)
                : b.Mesh.Positions.ToArray());
        return posed;
    }



    sealed class TileBuf
    {
        public int[] Colour = Array.Empty<int>();
        public byte[] Alpha = Array.Empty<byte>();
        public int[] Normal = Array.Empty<int>();
        public float[] Depth = Array.Empty<float>();
    }

    static TileBuf RenderTile(List<Body> bodies, List<Vec3[]> posed, RenderView view, int size,
        float scale, (float cx, float cy) centre2d, Vec3 centre3d, float perspDist, float tanHalf, bool persp)
    {
        var (right, up, forward) = view.Axes();
        var key = (forward * -0.6f + up * 0.55f + right * 0.55f).Normal;
        var fill = (forward * -0.5f + up * 0.1f - right * 0.8f).Normal;
        var rim = (forward * 0.6f + up * 0.7f - right * 0.2f).Normal;
        var eye = centre3d - forward * perspDist;
        var buf = new TileBuf
        {
            Colour = new int[size * size],
            Alpha = new byte[size * size],
            Normal = new int[size * size],
            Depth = new float[size * size],
        };
        Array.Fill(buf.Depth, float.MaxValue);
        Array.Fill(buf.Normal, Raster.Rgb(128, 128, 255));

        for (int bi = 0; bi < bodies.Count; bi++)
        {
            var mesh = bodies[bi].Mesh;
            var pos = posed[bi];
            var vnorm = VertexNormals(mesh, pos);
            int baseColour = Palette[bi % Palette.Length];
            bool vertexColour = mesh.HasVertexColors;
            foreach (var face in mesh.Faces)
            {
                if (face.Indices.Length < 3) continue;
                for (int k = 1; k + 1 < face.Indices.Length; k++)
                {
                    int ia = face.Indices[0], ib = face.Indices[k], ic = face.Indices[k + 1];
                    if (!InRange(ia, pos.Length) || !InRange(ib, pos.Length) || !InRange(ic, pos.Length)) continue;
                    var pa = pos[ia]; var pb = pos[ib]; var pc = pos[ic];
                    var fn = Vec3.Cross(pb - pa, pc - pa);
                    if (fn.LengthSquared < 1e-12f) continue;
                    fn = fn.Normal;
                    bool facing = Vec3.Dot(fn, forward) < 0f;
                    Project(pa, out float ax, out float ay, out float ad);
                    Project(pb, out float bx, out float by, out float bd);
                    Project(pc, out float cx, out float cy, out float cd);
                    if (persp && (ad <= 1e-6f || bd <= 1e-6f || cd <= 1e-6f)) continue;
                    Fill(buf, size, ax, ay, ad, bx, by, bd, cx, cy, cd,
                        vnorm[ia], vnorm[ib], vnorm[ic], fn,
                        vertexColour ? mesh.VertexColors[ia] : default, vertexColour ? mesh.VertexColors[ib] : default,
                        vertexColour ? mesh.VertexColors[ic] : default, vertexColour, baseColour,
                        facing, right, up, forward, key, fill, rim, persp);
                }
            }
        }
        return buf;

        bool InRange(int i, int n) => (uint)i < (uint)n;

        void Project(Vec3 p, out float sx, out float sy, out float depth)
        {
            if (persp)
            {
                var rel = p - eye;
                float df = Vec3.Dot(rel, forward);
                depth = df;
                sx = size * 0.5f + Vec3.Dot(rel, right) / (df * tanHalf) * size * 0.5f;
                sy = size * 0.5f - Vec3.Dot(rel, up) / (df * tanHalf) * size * 0.5f;
            }
            else
            {
                sx = size * 0.5f + (Vec3.Dot(p, right) - centre2d.cx) * scale;
                sy = size * 0.5f - (Vec3.Dot(p, up) - centre2d.cy) * scale;
                depth = Vec3.Dot(p, forward);
            }
        }
    }

    static Vec3[] VertexNormals(PolyMesh mesh, Vec3[] pos)
    {
        var acc = new Vec3[pos.Length];
        foreach (var face in mesh.Faces)
        {
            if (face.Indices.Length < 3) continue;
            var a = pos[face.Indices[0]];
            var n = Vec3.Zero;
            for (int k = 1; k + 1 < face.Indices.Length; k++)
            {
                var c = Vec3.Cross(pos[face.Indices[k]] - a, pos[face.Indices[k + 1]] - a);
                n += c;
            }
            if (n.LengthSquared < 1e-12f) continue;
            foreach (var i in face.Indices)
                if ((uint)i < (uint)acc.Length) acc[i] += n;
        }
        for (int i = 0; i < acc.Length; i++)
            acc[i] = acc[i].LengthSquared > 1e-12f ? acc[i].Normal : Vec3.Zero;
        return acc;
    }

    static void Fill(TileBuf buf, int size,
        float ax, float ay, float ad, float bx, float by, float bd, float cx, float cy, float cd,
        Vec3 na, Vec3 nb, Vec3 nc, Vec3 faceNormal,
        Vec4 ca, Vec4 cb, Vec4 cc, bool vertexColour, int baseColour,
        bool facing, Vec3 right, Vec3 up, Vec3 forward, Vec3 key, Vec3 fill, Vec3 rim, bool persp)
    {
        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))));
        int maxX = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))));
        int maxY = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
        float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        if (MathF.Abs(area) < 1e-8f) return;
        float inv = 1f / area;
        float wa = persp ? 1f / ad : 1f, wb = persp ? 1f / bd : 1f, wc = persp ? 1f / cd : 1f;
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            float px = x + 0.5f, py = y + 0.5f;
            float w0 = ((bx - px) * (cy - py) - (by - py) * (cx - px)) * inv;
            float w1 = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) * inv;
            float w2 = 1f - w0 - w1;
            if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
            float ws = w0 * wa + w1 * wb + w2 * wc;
            if (ws <= 1e-12f) continue;
            float u0 = w0 * wa / ws, u1 = w1 * wb / ws, u2 = w2 * wc / ws;
            float depth = u0 * ad + u1 * bd + u2 * cd;
            int i = y * size + x;
            if (depth >= buf.Depth[i]) continue;
            var n = na * u0 + nb * u1 + nc * u2;
            if (n.LengthSquared < 1e-12f) n = faceNormal;
            else n = n.Normal;
            if (!facing) n = -n;
            float shade = 0.24f
                + 0.56f * MathF.Max(0f, Vec3.Dot(n, key))
                + 0.20f * MathF.Max(0f, Vec3.Dot(n, fill))
                + 0.16f * MathF.Pow(MathF.Max(0f, Vec3.Dot(n, rim)), 3f);
            shade = MathF.Min(shade, 1.05f);
            if (!facing) shade *= 0.45f;
            float r, g, b;
            if (vertexColour)
            {
                r = ca.x * u0 + cb.x * u1 + cc.x * u2;
                g = ca.y * u0 + cb.y * u1 + cc.y * u2;
                b = ca.z * u0 + cb.z * u1 + cc.z * u2;
            }
            else
            {
                r = ((baseColour >> 16) & 0xFF) / 255f;
                g = ((baseColour >> 8) & 0xFF) / 255f;
                b = (baseColour & 0xFF) / 255f;
            }
            buf.Depth[i] = depth;
            buf.Alpha[i] = 255;
            buf.Colour[i] = Raster.Rgb((int)(r * shade * 255f), (int)(g * shade * 255f), (int)(b * shade * 255f));
            var ncam = new Vec3(Vec3.Dot(n, right), Vec3.Dot(n, up), -Vec3.Dot(n, forward)).Normal;
            buf.Normal[i] = Raster.Rgb(
                (int)((ncam.x * 0.5f + 0.5f) * 255f),
                (int)((ncam.y * 0.5f + 0.5f) * 255f),
                (int)((ncam.z * 0.5f + 0.5f) * 255f));
        }
    }

    static void Blit(Raster colour, Raster normal, Raster material, byte[] alpha, TileBuf tile,
        int d, int f, int tileSize, int render)
    {
        int factor = render / tileSize;
        int ox = d * tileSize, oy = f * tileSize;
        var sampledDepth = new float[tileSize * tileSize];
        Array.Fill(sampledDepth, float.MaxValue);
        for (int y = 0; y < tileSize; y++)
        for (int x = 0; x < tileSize; x++)
        {
            int sx = factor == 1 ? x : x * factor + factor / 2;
            int sy = factor == 1 ? y : y * factor + factor / 2;
            int si = sy * render + sx;
            int di = (oy + y) * colour.Width + (ox + x);
            colour.Pixels[di] = tile.Colour[si];
            normal.Pixels[di] = tile.Normal[si];
            alpha[di] = tile.Alpha[si];
            if (tile.Alpha[si] != 0) sampledDepth[y * tileSize + x] = tile.Depth[si];
        }
        float min = float.MaxValue, max = float.MinValue;
        foreach (var v in sampledDepth)
            if (v != float.MaxValue) { min = MathF.Min(min, v); max = MathF.Max(max, v); }
        for (int y = 0; y < tileSize; y++)
        for (int x = 0; x < tileSize; x++)
        {
            int di = (oy + y) * colour.Width + (ox + x);
            float v = sampledDepth[y * tileSize + x];
            int depthByte = v == float.MaxValue || max <= min ? 0
                : (int)((1f - (v - min) / (max - min)) * 255f);
            material.Pixels[di] = Raster.Rgb(depthByte, 0, 0);
        }
    }

    static void Quantize(Raster colour, byte[] alpha, int paletteSize)
    {
        var counts = new Dictionary<int, int>();
        for (int i = 0; i < colour.Pixels.Length; i++)
            if (alpha[i] != 0)
                counts[colour.Pixels[i]] = counts.TryGetValue(colour.Pixels[i], out var c) ? c + 1 : 1;
        if (counts.Count <= paletteSize) return;
        var boxes = new List<List<(int colour, int count)>> { counts.Select(kv => (kv.Key, kv.Value)).ToList() };
        while (boxes.Count < paletteSize)
        {
            int split = -1, bestRange = -1;
            for (int b = 0; b < boxes.Count; b++)
            {
                if (boxes[b].Count < 2) continue;
                int rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0;
                foreach (var (c, _) in boxes[b])
                {
                    rMin = Math.Min(rMin, (c >> 16) & 0xFF); rMax = Math.Max(rMax, (c >> 16) & 0xFF);
                    gMin = Math.Min(gMin, (c >> 8) & 0xFF); gMax = Math.Max(gMax, (c >> 8) & 0xFF);
                    bMin = Math.Min(bMin, c & 0xFF); bMax = Math.Max(bMax, c & 0xFF);
                }
                int range = Math.Max(rMax - rMin, Math.Max(gMax - gMin, bMax - bMin));
                if (range > bestRange) { bestRange = range; split = b; }
            }
            if (split < 0) break;
            var box = boxes[split];
            int rMin2 = 255, rMax2 = 0, gMin2 = 255, gMax2 = 0, bMin2 = 255, bMax2 = 0;
            foreach (var (c, _) in box)
            {
                rMin2 = Math.Min(rMin2, (c >> 16) & 0xFF); rMax2 = Math.Max(rMax2, (c >> 16) & 0xFF);
                gMin2 = Math.Min(gMin2, (c >> 8) & 0xFF); gMax2 = Math.Max(gMax2, (c >> 8) & 0xFF);
                bMin2 = Math.Min(bMin2, c & 0xFF); bMax2 = Math.Max(bMax2, c & 0xFF);
            }
            int channel = rMax2 - rMin2 >= gMax2 - gMin2 && rMax2 - rMin2 >= bMax2 - bMin2 ? 0
                : gMax2 - gMin2 >= bMax2 - bMin2 ? 1 : 2;
            box.Sort((a, b2) => Channel(a.colour, channel).CompareTo(Channel(b2.colour, channel)));
            int total = box.Sum(e => e.count), acc = 0, at = 0;
            while (at < box.Count - 1 && acc + box[at].count < total / 2) { acc += box[at].count; at++; }
            boxes[split] = box.Take(at + 1).ToList();
            boxes.Add(box.Skip(at + 1).ToList());
        }
        var map = new Dictionary<int, int>();
        foreach (var box in boxes)
        {
            long r = 0, g = 0, b = 0, n = 0;
            foreach (var (c, count) in box)
            {
                r += (long)((c >> 16) & 0xFF) * count; g += (long)((c >> 8) & 0xFF) * count; b += (long)(c & 0xFF) * count; n += count;
            }
            int avg = Raster.Rgb((int)(r / n), (int)(g / n), (int)(b / n));
            foreach (var (c, _) in box) map[c] = avg;
        }
        for (int i = 0; i < colour.Pixels.Length; i++)
            if (alpha[i] != 0 && map.TryGetValue(colour.Pixels[i], out var q)) colour.Pixels[i] = q;

        static int Channel(int c, int channel) => channel == 0 ? (c >> 16) & 0xFF : channel == 1 ? (c >> 8) & 0xFF : c & 0xFF;
    }

    static void Outline(Raster colour, byte[] alpha, int tileSize, int dirs, int frames, int outlineColour)
    {
        for (int f = 0; f < frames; f++)
        for (int d = 0; d < dirs; d++)
        {
            int ox = d * tileSize, oy = f * tileSize;
            var edge = new List<int>();
            for (int y = 0; y < tileSize; y++)
            for (int x = 0; x < tileSize; x++)
            {
                int i = (oy + y) * colour.Width + (ox + x);
                if (alpha[i] == 0) continue;
                bool border = x == 0 || y == 0 || x == tileSize - 1 || y == tileSize - 1
                    || alpha[i - 1] == 0 || alpha[i + 1] == 0
                    || alpha[i - colour.Width] == 0 || alpha[i + colour.Width] == 0;
                if (border) edge.Add(i);
            }
            foreach (var i in edge) colour.Pixels[i] = outlineColour;
        }
    }

    internal static string Suffixed(string path, string suffix)
    {
        int slash = path.LastIndexOfAny(new[] { '/', '\\' });
        int dot = path.LastIndexOf('.');
        return dot > slash ? path[..dot] + suffix + path[dot..] : path + suffix;
    }

    internal static byte[] EncodeRgbaPng(Raster colour, byte[] alpha)
    {
        int w = colour.Width, h = colour.Height;
        var raw = new byte[h * (w * 4 + 1)];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0;
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, p = colour.Pixels[i];
                raw[o++] = (byte)((p >> 16) & 0xFF);
                raw[o++] = (byte)((p >> 8) & 0xFF);
                raw[o++] = (byte)(p & 0xFF);
                raw[o++] = alpha[i];
            }
        }
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        WriteBe(ihdr, 0, w); WriteBe(ihdr, 4, h);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", Zlib(raw));
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    static void WriteBe(byte[] b, int offset, int value)
    {
        b[offset] = (byte)(value >> 24); b[offset + 1] = (byte)(value >> 16);
        b[offset + 2] = (byte)(value >> 8); b[offset + 3] = (byte)value;
    }

    static void Chunk(Stream ms, string type, byte[] data)
    {
        var header = new byte[4];
        WriteBe(header, 0, data.Length);
        ms.Write(header);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        ms.Write(typeBytes);
        ms.Write(data);
        uint crc = 0xFFFFFFFFu;
        foreach (var v in typeBytes) crc = CrcTable[(crc ^ v) & 0xFF] ^ (crc >> 8);
        foreach (var v in data) crc = CrcTable[(crc ^ v) & 0xFF] ^ (crc >> 8);
        crc ^= 0xFFFFFFFFu;
        var tail = new byte[4];
        WriteBe(tail, 0, (int)crc);
        ms.Write(tail);
    }

    static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); ms.WriteByte(0x01);
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data);
        uint a = 1, b = 0;
        foreach (var x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
        uint adler = (b << 16) | a;
        ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
        return ms.ToArray();
    }

    static readonly uint[] CrcTable = BuildCrcTable();

    static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
