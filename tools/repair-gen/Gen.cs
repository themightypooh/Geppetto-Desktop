using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Effigy;

// Fixture generator for mesh-repair validation (wave 3, proposals P1-P10).
// Usage: gen <outdir>  — writes one .effigy per fixture via StudioDocument.
class Gen
{
    static Vec2[] QuadUvs() => new[] { new Vec2(0, 0), new Vec2(1, 0), new Vec2(1, 1), new Vec2(0, 1) };
    static Vec2[] TriUvs() => new[] { new Vec2(0, 0), new Vec2(1, 0), new Vec2(0, 1) };
    static Vec2[] ZeroUvs(int n) => Enumerable.Repeat(new Vec2(0, 0), n).ToArray();

    static PolyMesh BoxMesh(float cx, float cy, float cz, float sx, float sy, float sz, bool reverse = false, bool zeroUvs = false)
    {
        var m = new PolyMesh();
        float x0 = cx - sx / 2, x1 = cx + sx / 2, y0 = cy - sy / 2, y1 = cy + sy / 2, z0 = cz - sz / 2, z1 = cz + sz / 2;
        var c = new[] {
            new Vec3(x0, y0, z0), new Vec3(x1, y0, z0), new Vec3(x1, y1, z0), new Vec3(x0, y1, z0),
            new Vec3(x0, y0, z1), new Vec3(x1, y0, z1), new Vec3(x1, y1, z1), new Vec3(x0, y1, z1) };
        foreach (var p in c) m.Positions.Add(p);
        int[][] faces = {
            new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 },
            new[] { 3, 7, 6, 2 }, new[] { 0, 4, 7, 3 }, new[] { 1, 2, 6, 5 } };
        foreach (var f in faces)
        {
            var idx = reverse ? f.Reverse().ToArray() : f;
            m.Faces.Add(new Face(idx, zeroUvs ? ZeroUvs(4) : QuadUvs()));
        }
        return m;
    }

    static PolyMesh Append(PolyMesh a, PolyMesh b)
    {
        int off = a.Positions.Count;
        foreach (var p in b.Positions) a.Positions.Add(p);
        foreach (var f in b.Faces)
            a.Faces.Add(new Face(f.Indices.Select(i => i + off).ToArray(), f.UVs.ToArray(), f.Material));
        return a;
    }

    static void Save(string dir, string name, PolyMesh mesh)
    {
        // NOTE: StudioDocument.WriteFile writes feature history only, NOT raw bodies,
        // so a raw-body .effigy round-trips empty. Fixtures are therefore written as
        // OBJ (importable via script: add Import source=...) and the .effigy write
        // is kept only as a record that it does not carry the mesh.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("o " + name);
        foreach (var p in mesh.Positions) sb.AppendLine($"v {p.x} {p.y} {p.z}");
        int vt = 0;
        foreach (var f in mesh.Faces)
        {
            var vtIdx = new int[f.Count];
            for (int k = 0; k < f.Count; k++)
            {
                var uv = f.UVs != null && k < f.UVs.Length ? f.UVs[k] : new Vec2(0, 0);
                // ObjReader flips V on import (vt v -> Effigy 1-v), so write
                // 1-v here to round-trip exactly; an all-zero UV face then
                // imports as all-zero, as the UV gate requires.
                sb.AppendLine($"vt {uv.x} {1f - uv.y}");
                vtIdx[k] = ++vt;
            }
            sb.Append("f");
            for (int k = 0; k < f.Count; k++) sb.Append($" {f.Indices[k] + 1}/{vtIdx[k]}");
            sb.AppendLine();
        }
        var objPath = Path.Combine(dir, name + ".obj");
        File.WriteAllText(objPath, sb.ToString());
        Console.WriteLine($"wrote {objPath} faces={mesh.FaceCount} verts={mesh.VertexCount}");
    }

    static int Main(string[] args)
    {
        var dir = args[0];
        Directory.CreateDirectory(dir);

        // 1. Closed box — must pass everything.
        Save(dir, "closed-box", BoxMesh(0, 0, 0, 2, 2, 2));

        // 2. Box minus one face — boundary edges, one hole ring.
        var open = BoxMesh(0, 0, 0, 2, 2, 2);
        open.Faces.RemoveAt(1); // top face
        Save(dir, "open-box", open);

        // 3. Bowtie vertex — two tetrahedra sharing exactly one vertex.
        var bow = new PolyMesh();
        var pts = new[] {
            new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1),
            new Vec3(-1, 0, 0), new Vec3(0, -1, 0), new Vec3(0, 0, -1) };
        foreach (var p in pts) bow.Positions.Add(p);
        int[][] tets = { new[] { 0, 2, 1 }, new[] { 0, 1, 3 }, new[] { 0, 3, 2 }, new[] { 1, 2, 3 },
                         new[] { 0, 4, 5 }, new[] { 0, 6, 4 }, new[] { 0, 5, 6 }, new[] { 4, 6, 5 } };
        // Note: second tet faces oriented outward for vertices (0,4,5,6) with
        // the same (0,2,1)-style pattern mirrored; winding is not the point
        // here — the shared vertex is.
        foreach (var f in tets) bow.Faces.Add(new Face(f, TriUvs()));
        Save(dir, "bowtie", bow);

        // 4. Inside-out box — all windings reversed, negative signed volume.
        Save(dir, "insideout-box", BoxMesh(0, 0, 0, 2, 2, 2, reverse: true));

        // 5. Duplicate face — one face of a box added twice.
        var dup = BoxMesh(0, 0, 0, 2, 2, 2);
        dup.Faces.Add(new Face(dup.Faces[1].Indices.ToArray(), QuadUvs()));
        Save(dir, "duplicate-face", dup);

        // 6. Self-intersecting prism — two boxes interpenetrating as one body.
        // Offset in y/z so faces properly pierce each other; axis-aligned boxes
        // with identical y/z spans only touch edge-on-face and do not cross.
        Save(dir, "selfintersect", Append(BoxMesh(0, 0, 0, 2, 2, 2), BoxMesh(1, 0.5f, 0.5f, 2, 2, 2)));

        // 7. Torus — genus 1, chi = 0, shell count 1; must pass, not fail.
        Save(dir, "torus", Primitives.Torus(4f, 0.5f, 32, 12));

        // 8. Two shells in one body — a box plus a detached shard.
        Save(dir, "twoshells", Append(BoxMesh(0, 0, 0, 2, 2, 2), BoxMesh(5, 0, 0, 1, 1, 1)));

        // 9a. 12-sided flat hole — cylinder with its top cap removed.
        var cyl = Primitives.Cylinder(2f, 2f, 12);
        float maxZ = cyl.Positions.Max(p => p.z);
        int removed = cyl.Faces.RemoveAll(f => f.Indices.All(i => Math.Abs(cyl.Positions[i].z - maxZ) < 1e-4f));
        Console.WriteLine($"cylinder faces={cyl.FaceCount} topFacesRemoved={removed}");
        Save(dir, "hole12", cyl);

        // 9b. Concave hole — box minus top, top ring dented inward (planar, concave).
        var conc = new PolyMesh();
        var cp = new[] {
            new Vec3(-1, -1, -1), new Vec3(1, -1, -1), new Vec3(1, 1, -1), new Vec3(-1, 1, -1),
            new Vec3(-1, -1, 1), new Vec3(1, -1, 1), new Vec3(1, 1, 1), new Vec3(0, 0.2f, 1), new Vec3(-1, 1, 1) };
        foreach (var p in cp) conc.Positions.Add(p);
        int[][] cf = {
            new[] { 0, 3, 2, 1 },            // bottom
            new[] { 0, 1, 5, 4 },            // front
            new[] { 1, 2, 6, 5 },            // right
            new[] { 0, 4, 8, 3 },            // left
            new[] { 3, 8, 7, 6, 2 },         // back (pentagon, dent vertex 7)
        };
        foreach (var f in cf) conc.Faces.Add(new Face(f, f.Length == 4 ? QuadUvs() : new[] { new Vec2(0, 0), new Vec2(1, 0), new Vec2(1, 1), new Vec2(0.5f, 0.5f), new Vec2(0, 1) }));
        Save(dir, "concave-hole", conc);

        // 10. Thin-walled hollow sphere — outer r=5, inner r=4.9 reversed.
        var outer = Primitives.QuadSphere(5f, 8);
        var inner = Primitives.QuadSphere(4.9f, 8);
        foreach (var f in inner.Faces) { Array.Reverse(f.Indices); Array.Reverse(f.UVs); }
        Save(dir, "thinwall", Append(outer, inner));

        // Extra: UV-less box (P10), degenerate faces (P4), weld gap (P7),
        // broken import for manifold fallback (P8).
        Save(dir, "uvless", BoxMesh(0, 0, 0, 2, 2, 2, zeroUvs: true));
        var deg = BoxMesh(0, 0, 0, 2, 2, 2);
        int v0 = deg.Positions.Count;
        deg.Positions.Add(new Vec3(0, 0, 0)); deg.Positions.Add(new Vec3(1, 0, 0)); deg.Positions.Add(new Vec3(2, 0, 0));
        deg.Faces.Add(new Face(new[] { v0, v0 + 1, v0 + 2 }, TriUvs())); // collinear, zero area
        deg.Faces.Add(new Face(new[] { 0, 1, 1, 4 }, QuadUvs()));        // repeated corner
        deg.Faces.Add(new Face(new[] { 0, 0, 0 }, TriUvs()));            // all same point
        Save(dir, "degenerate", deg);
        // Weld-gap: two boxes with a 1e-4 gap in x, plus a 0.01-scale detail
        // that must NOT collapse at the 0.001 cap tolerance.
        var weld = Append(BoxMesh(-1, 0, 0, 2, 2, 2), BoxMesh(1.0001f, 0, 0, 2, 2, 2));
        weld = Append(weld, BoxMesh(5, 0, 0, 0.01f, 0.01f, 0.01f));
        Save(dir, "weld-gap", weld);
        // Two shells, one flipped: P3 must repair only the flipped shell.
        Save(dir, "twoshells-oneflipped", Append(BoxMesh(0, 0, 0, 2, 2, 2), BoxMesh(5, 0, 0, 1, 1, 1, reverse: true)));
        var broken = BoxMesh(0, 0, 0, 2, 2, 2);
        broken.Faces.RemoveAt(1);
        broken = Append(broken, BoxMesh(5, 0, 0, 1, 1, 1));
        Save(dir, "manifold-broken", broken);
        return 0;
    }
}
