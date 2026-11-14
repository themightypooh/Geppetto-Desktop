using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>Viewer-side strand list: a groom is a set of polylines rooted on the scalp.
///
/// GROOMING RULE (spec section 1, fix-hair-clump-and-grooming-brushes): every brush in this
/// file is root-preserving. Point 0 of every strand is the scalp root and no brush ever
/// moves it — brushes work on points 1..n-1, with their effect weighted by t = i/(n-1)
/// (0 at the root, 1 at the tip), so the root is pinned even when the weight formula is
/// evaluated at the root. The old Clump moved every root to _strands[0].Points[0], which
/// collapsed the whole groom to one point; that behaviour is gone, and the old
/// "clump aligns roots" test expectation goes with it (roots must NOT be equal after Clump).
/// </summary>
public sealed class HairStrandService
{
    public struct Strand
    {
        public List<(float x, float y, float z)> Points;
        public float Stiffness;
        /// <summary>Segment rest lengths (root first), captured at AddStrand.</summary>
        public float[] RestLengths;
    }

    /// <summary>R9: keep each segment at its rest length after every substep (root stays put), so
    /// strands hang instead of stretching without bound. Off by default for the legacy behaviour.</summary>
    public bool PreserveLength { get; set; }

    /// <summary>Clump falloff exponent: point weight w(t) = t^gamma * ClumpProfile, so the
    /// root (t=0) never moves and the tip (t=1) moves most.</summary>
    public float ClumpGamma { get; set; } = 2f;

    /// <summary>Clump profile multiplier, 0..1. 1 is the full clump; 0 disables it.</summary>
    public float ClumpProfile { get; set; } = 1f;

    private readonly List<Strand> _strands = new();
    public IReadOnlyList<Strand> Strands => _strands;

    public void AddStrand(IEnumerable<(float, float, float)> points, float stiffness = 0.5f)
    {
        var s = new Strand { Points = new List<(float, float, float)>(points), Stiffness = stiffness };
        s.RestLengths = new float[Math.Max(0, s.Points.Count - 1)];
        for (int i = 1; i < s.Points.Count; i++)
        {
            var (ax, ay, az) = s.Points[i - 1]; var (bx, by, bz) = s.Points[i];
            s.RestLengths[i - 1] = MathF.Sqrt((bx-ax)*(bx-ax) + (by-ay)*(by-ay) + (bz-az)*(bz-az));
        }
        _strands.Add(s);
    }

    public void Step(float dt, float gravity = -9.81f, (float x,float y,float z)? bodyCenter = null, float bodyRadius = 0f, (float x,float y,float z)? wind = null, int substeps = 1, (float x,float y,float z)? turbulence = null, (float x,float y,float z)? vortex = null)
    {
        float sdt = dt / Math.Max(1, substeps);
        for (int s = 0; s < Math.Max(1, substeps); s++)
        {
            foreach (var strand in _strands)
            {
                for (int i = 1; i < strand.Points.Count; i++)
                {
                    var p = strand.Points[i];
                    float ny = p.y + gravity * sdt * sdt * 0.5f;
                    if (wind.HasValue)
                    {
                        p = (p.x + wind.Value.x*sdt*sdt*0.5f, ny, p.z + wind.Value.z*sdt*sdt*0.5f);
                        ny = p.y;
                    }
                    if (turbulence.HasValue)
                    {
                        p = (p.x + turbulence.Value.x*sdt*sdt*0.5f, ny, p.z + turbulence.Value.z*sdt*sdt*0.5f);
                        ny = p.y;
                    }
                    if (vortex.HasValue)
                    {
                        float vx = p.y - vortex.Value.y;
                        float vz = p.z - vortex.Value.z;
                        p = (p.x + (-vz)*sdt*sdt*0.5f, ny, p.z + (vx)*sdt*sdt*0.5f);
                        ny = p.y;
                    }
                    if (bodyCenter.HasValue)
                    {
                        float dx = p.x - bodyCenter.Value.x;
                        float dy = ny - bodyCenter.Value.y;
                        float dz = p.z - bodyCenter.Value.z;
                        float dist = MathF.Sqrt(dx*dx+dy*dy+dz*dz);
                        if (dist < bodyRadius && dist > 1e-6f)
                        {
                            float scale = bodyRadius / dist;
                            ny = bodyCenter.Value.y + (ny - bodyCenter.Value.y) * scale;
                        }
                    }
                    strand.Points[i] = (p.x, ny, p.z);
                }
                if (PreserveLength)
                    EnforceRestLengths(strand);
            }
        }
    }

    /// <summary>Pull every segment back to its rest length, walking out from the root (which
    /// never moves). Shared by Step and the brushes, so a brush can never stretch a strand.</summary>
    private static void EnforceRestLengths(Strand strand)
    {
        if (strand.RestLengths is not { } rest) return;
        for (int i = 1; i < strand.Points.Count; i++)
        {
            if (i - 1 >= rest.Length) break;
            var (ax, ay, az) = strand.Points[i - 1]; var (bx, by, bz) = strand.Points[i];
            float dx = bx - ax, dy = by - ay, dz = bz - az, d = MathF.Sqrt(dx*dx + dy*dy + dz*dz);
            if (d < 1e-9f) continue;
            float k = rest[i - 1] / d;
            strand.Points[i] = (ax + dx*k, ay + dy*k, az + dz*k);
        }
    }

    public string ExportCurves()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var strand in _strands)
        {
            sb.Append("LINE");
            foreach (var pt in strand.Points) sb.Append($" {pt.x},{pt.y},{pt.z}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Read back what <see cref="ExportCurves"/> wrote, so a groom survives a
    /// save/reopen round trip with every root in place. Lines that do not parse are skipped;
    /// returns how many strands were imported.</summary>
    public int ImportCurves(string text)
    {
        _strands.Clear();
        if (string.IsNullOrEmpty(text)) return 0;
        var count = 0;
        foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (!line.StartsWith("LINE", StringComparison.Ordinal)) continue;
            var pts = new List<(float, float, float)>();
            foreach (var tok in line.Substring(4).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = tok.Split(',');
                if (parts.Length != 3) continue;
                if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) continue;
                if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) continue;
                if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) continue;
                pts.Add((x, y, z));
            }
            if (pts.Count == 0) continue;
            AddStrand(pts);
            count++;
        }
        return count;
    }

    /// <summary>Comb: re-orient strands along <paramref name="direction"/>. The offset grows
    /// with t (0 at the root, full at the tip), so the strand bends toward the comb direction
    /// instead of translating rigidly, and the root never moves.</summary>
    public void Comb((float x,float y,float z) direction, float strength = 0.1f)
    {
        var len = MathF.Sqrt(direction.x*direction.x + direction.y*direction.y + direction.z*direction.z);
        if (len < 1e-9f) return;
        var dx = direction.x / len; var dy = direction.y / len; var dz = direction.z / len;
        foreach (var strand in _strands)
        {
            var n = strand.Points.Count;
            if (n < 2) continue;
            for (int i = 1; i < n; i++)
            {
                var t = i / (float)(n - 1);
                var p = strand.Points[i];
                var k = strength * t;
                strand.Points[i] = (p.x + dx*k, p.y + dy*k, p.z + dz*k);
            }
            if (PreserveLength) EnforceRestLengths(strand);
        }
    }

    /// <summary>Part: push strands away from the parting plane (through
    /// <paramref name="point"/>, normal <paramref name="normal"/>) on both sides, so a part
    /// line opens up. Falloff is exp(-|distance to plane|); the root never moves.</summary>
    public void Part((float x, float y, float z) normal, (float x, float y, float z) point, float strength = 0.5f)
    {
        var len = MathF.Sqrt(normal.x*normal.x + normal.y*normal.y + normal.z*normal.z);
        if (len < 1e-9f) return;
        var nx = normal.x / len; var ny = normal.y / len; var nz = normal.z / len;
        foreach (var strand in _strands)
        {
            var n = strand.Points.Count;
            if (n < 2) continue;
            for (int i = 1; i < n; i++)
            {
                var t = i / (float)(n - 1);
                var p = strand.Points[i];
                var d = (p.x - point.x)*nx + (p.y - point.y)*ny + (p.z - point.z)*nz;
                var side = d >= 0f ? 1f : -1f;
                var k = strength * t * MathF.Exp(-MathF.Abs(d)) * side;
                strand.Points[i] = (p.x + nx*k, p.y + ny*k, p.z + nz*k);
            }
            if (PreserveLength) EnforceRestLengths(strand);
        }
    }

    /// <summary>Puff: expand each strand radially away from its own root, so the groom gains
    /// volume. The expansion grows with t; the root is the centre and never moves.</summary>
    public void Puff(float amount = 0.5f)
    {
        foreach (var strand in _strands)
        {
            var n = strand.Points.Count;
            if (n < 2) continue;
            var root = strand.Points[0];
            for (int i = 1; i < n; i++)
            {
                var t = i / (float)(n - 1);
                var p = strand.Points[i];
                var k = 1f + amount * t;
                strand.Points[i] = (root.x + (p.x - root.x)*k, root.y + (p.y - root.y)*k, root.z + (p.z - root.z)*k);
            }
            if (PreserveLength) EnforceRestLengths(strand);
        }
    }

    /// <summary>Smooth: Laplacian smooth of the interior points (each point toward the average
    /// of its neighbours). Roots and tips are never moved.</summary>
    public void Smooth(float strength = 0.5f)
    {
        strength = Math.Clamp(strength, 0f, 1f);
        foreach (var strand in _strands)
        {
            var n = strand.Points.Count;
            if (n < 3) continue;
            var copy = strand.Points.ToArray();
            for (int i = 1; i < n - 1; i++)
            {
                var (ax, ay, az) = copy[i - 1]; var (bx, by, bz) = copy[i + 1];
                var p = copy[i];
                strand.Points[i] = (
                    p.x + ((ax + bx) * 0.5f - p.x) * strength,
                    p.y + ((ay + by) * 0.5f - p.y) * strength,
                    p.z + ((az + bz) * 0.5f - p.z) * strength);
            }
            if (PreserveLength) EnforceRestLengths(strand);
        }
    }

    /// <summary>Cut: truncate every strand to at most <paramref name="maxPoints"/> points.
    /// Only tail points are removed, so roots never move. Rest lengths are trimmed to match.</summary>
    public void Cut(int maxPoints)
    {
        if (maxPoints < 1) maxPoints = 1;
        for (int si = 0; si < _strands.Count; si++)
        {
            var strand = _strands[si];
            if (strand.Points.Count <= maxPoints) continue;
            strand.Points.RemoveRange(maxPoints, strand.Points.Count - maxPoints);
            if (strand.RestLengths is not null && strand.RestLengths.Length > Math.Max(0, maxPoints - 1))
                Array.Resize(ref strand.RestLengths, Math.Max(0, maxPoints - 1));
            _strands[si] = strand;
        }
    }

    /// <summary>Cut by arc length instead of point count: shorten each strand so its total
    /// length is at most <paramref name="maxLength"/>, keeping at least the root and the
    /// first segment. Roots never move.</summary>
    public void CutLength(float maxLength)
    {
        if (maxLength < 0f) maxLength = 0f;
        for (int si = 0; si < _strands.Count; si++)
        {
            var strand = _strands[si];
            var n = strand.Points.Count;
            if (n < 2) continue;
            float acc = 0f;
            var keep = 1;
            for (int i = 1; i < n; i++)
            {
                var (ax, ay, az) = strand.Points[i - 1]; var (bx, by, bz) = strand.Points[i];
                acc += MathF.Sqrt((bx-ax)*(bx-ax) + (by-ay)*(by-ay) + (bz-az)*(bz-az));
                if (acc <= maxLength) keep = i;
                else break;
            }
            if (keep < n - 1)
            {
                strand.Points.RemoveRange(keep + 1, n - keep - 1);
                if (strand.RestLengths is not null && strand.RestLengths.Length > keep)
                    Array.Resize(ref strand.RestLengths, keep);
                _strands[si] = strand;
            }
        }
    }

    /// <summary>Clump, fixed: attract each strand's tip/mid-segments toward its local cluster
    /// guide curve, with weight w(t) = t^gamma * ClumpProfile (0 at the root, full at the tip).
    /// A cluster is the strands whose roots lie within <paramref name="radius"/> of each
    /// other; the guide is the per-index average of the cluster's strands, snapshotted before
    /// any strand moves so the result does not depend on iteration order. Roots are never
    /// touched — a clump that moves any root is a bug, not a feature.</summary>
    public void Clump(float radius, float strength = 1f)
    {
        if (_strands.Count == 0 || radius <= 0f) return;
        strength = Math.Clamp(strength, 0f, 1f);

        // Greedy clusters by root proximity, in insertion order (deterministic).
        var clusters = new List<List<int>>();
        var reps = new List<(float x, float y, float z)>();
        for (int si = 0; si < _strands.Count; si++)
        {
            if (_strands[si].Points.Count == 0) continue;
            var root = _strands[si].Points[0];
            var placed = false;
            for (int ci = 0; ci < clusters.Count; ci++)
            {
                var rep = reps[ci];
                var dx = root.x - rep.x; var dy = root.y - rep.y; var dz = root.z - rep.z;
                if (dx*dx + dy*dy + dz*dz <= radius*radius)
                {
                    clusters[ci].Add(si);
                    placed = true;
                    break;
                }
            }
            if (!placed)
            {
                clusters.Add(new List<int> { si });
                reps.Add(root);
            }
        }

        foreach (var cluster in clusters)
        {
            if (cluster.Count < 2) continue;
            var guideLen = int.MaxValue;
            foreach (var si in cluster)
                guideLen = Math.Min(guideLen, _strands[si].Points.Count);
            if (guideLen < 2) continue;

            // Snapshot the guide curve (per-index cluster average) before moving anything.
            var guide = new (float x, float y, float z)[guideLen];
            for (int i = 0; i < guideLen; i++)
            {
                float sx = 0f, sy = 0f, sz = 0f;
                foreach (var si in cluster)
                {
                    var p = _strands[si].Points[i];
                    sx += p.x; sy += p.y; sz += p.z;
                }
                guide[i] = (sx / cluster.Count, sy / cluster.Count, sz / cluster.Count);
            }

            foreach (var si in cluster)
            {
                var strand = _strands[si];
                for (int i = 1; i < guideLen; i++)
                {
                    var t = i / (float)(guideLen - 1);
                    var w = Math.Clamp(MathF.Pow(t, ClumpGamma) * ClumpProfile * strength, 0f, 1f);
                    if (w <= 0f) continue;
                    var p = strand.Points[i];
                    var g = guide[i];
                    strand.Points[i] = (p.x + (g.x - p.x)*w, p.y + (g.y - p.y)*w, p.z + (g.z - p.z)*w);
                }
                if (PreserveLength) EnforceRestLengths(strand);
            }
        }
    }

    /// <summary>Curl: twist each strand into a helix around its own root-to-tip axis. The
    /// twist angle grows with t (0 at the root, <paramref name="turns"/> full turns at the
    /// tip), so the root stays pinned and the tip curls most. The radial distance from the
    /// axis is preserved, so curling never collapses or stretches a strand.</summary>
    public void Curl(float turns = 1f)
    {
        foreach (var strand in _strands)
        {
            var n = strand.Points.Count;
            if (n < 2) continue;
            var root = strand.Points[0];
            var tip = strand.Points[n - 1];
            var ax = tip.x - root.x; var ay = tip.y - root.y; var az = tip.z - root.z;
            var alen = MathF.Sqrt(ax*ax + ay*ay + az*az);
            if (alen < 1e-9f) { ax = 0f; ay = 0f; az = 1f; alen = 1f; }
            ax /= alen; ay /= alen; az /= alen;
            for (int i = 1; i < n; i++)
            {
                var t = i / (float)(n - 1);
                var angle = turns * 2f * MathF.PI * t;
                var cos = MathF.Cos(angle); var sin = MathF.Sin(angle);
                var p = strand.Points[i];
                var ox = p.x - root.x; var oy = p.y - root.y; var oz = p.z - root.z;
                var along = ox*ax + oy*ay + oz*az;
                var rx = ox - along*ax; var ry = oy - along*ay; var rz = oz - along*az;
                // Rodrigues rotation of the radial offset about the strand axis.
                var cx = ay*rz - az*ry; var cy = az*rx - ax*rz; var cz = ax*ry - ay*rx;
                var nx = rx*cos + cx*sin; var ny = ry*cos + cy*sin; var nz = rz*cos + cz*sin;
                strand.Points[i] = (root.x + along*ax + nx, root.y + along*ay + ny, root.z + along*az + nz);
            }
            if (PreserveLength) EnforceRestLengths(strand);
        }
    }

    /// <summary>Pin: gather the strands near <paramref name="point"/> toward it (pinning hair
    /// up), with linear falloff over <paramref name="radius"/>. Only points inside the radius
    /// move, and roots never move.</summary>
    public void Pin((float x, float y, float z) point, float radius, float strength = 1f)
    {
        if (radius <= 0f) return;
        strength = Math.Clamp(strength, 0f, 1f);
        foreach (var strand in _strands)
        {
            var n = strand.Points.Count;
            if (n < 2) continue;
            for (int i = 1; i < n; i++)
            {
                var p = strand.Points[i];
                var dx = p.x - point.x; var dy = p.y - point.y; var dz = p.z - point.z;
                var d = MathF.Sqrt(dx*dx + dy*dy + dz*dz);
                if (d >= radius) continue;
                var w = (1f - d / radius) * strength;
                strand.Points[i] = (p.x + (point.x - p.x)*w, p.y + (point.y - p.y)*w, p.z + (point.z - p.z)*w);
            }
            if (PreserveLength) EnforceRestLengths(strand);
        }
    }
}
