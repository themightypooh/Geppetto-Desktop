using System;
using System.Collections.Generic;

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
                if (PreserveLength && strand.RestLengths is { } rest)
                    for (int i = 1; i < strand.Points.Count; i++)
                    {
                        var (ax, ay, az) = strand.Points[i - 1]; var (bx, by, bz) = strand.Points[i];
                        float dx = bx - ax, dy = by - ay, dz = bz - az, d = MathF.Sqrt(dx*dx + dy*dy + dz*dz);
                        if (d < 1e-9f) continue;
                        float k = rest[i - 1] / d;
                        strand.Points[i] = (ax + dx*k, ay + dy*k, az + dz*k);
                    }
            }
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

    public void Comb((float x,float y,float z) direction)
    {
        foreach (var strand in _strands)
        {
            for (int i = 1; i < strand.Points.Count; i++)
            {
                var p = strand.Points[i];
                strand.Points[i] = (p.x + direction.x*0.1f, p.y + direction.y*0.1f, p.z + direction.z*0.1f);
            }
        }
    }

    public void Cut(int maxPoints)
    {
        foreach (var strand in _strands)
        {
            if (strand.Points.Count > maxPoints)
                strand.Points.RemoveRange(maxPoints, strand.Points.Count - maxPoints);
        }
    }

    public void Clump(float radius)
    {
        if (_strands.Count == 0) return;
        var center = _strands[0].Points.Count > 0 ? _strands[0].Points[0] : (0f,0f,0f);
        foreach (var strand in _strands)
        {
            if (strand.Points.Count == 0) continue;
            strand.Points[0] = (center.Item1, center.Item2, center.Item3);
        }
    }
}