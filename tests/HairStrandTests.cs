using System;

internal static class HairStrandTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        registry.Add("hair", "gravity pulls strand down", () =>
        {
            var svc = new HairStrandService();
            svc.AddStrand(new[] { (0f, 0f, 0f), (0f, 1f, 0f) });
            svc.Step(1f);
            if (svc.Strands[0].Points[1].y >= 1f) throw new Exception("not moved");
        });
        registry.Add("hair", "collision keeps outside body", () =>
        {
            var svc = new HairStrandService();
            svc.AddStrand(new[] { (0f, 0f, 0f), (0f, 0.5f, 0f) });
            svc.Step(1f, -9.81f, (0f,0f,0f), 1f);
            var pt = svc.Strands[0].Points[1];
            float d = MathF.Sqrt(pt.x*pt.x+pt.y*pt.y+pt.z*pt.z);
            if (d < 0.99f) throw new Exception("penetrated");
        });
        registry.Add("hair", "deterministic repeated step", () =>
        {
            var a = new HairStrandService();
            var b = new HairStrandService();
            a.AddStrand(new[] { (1f,2f,3f), (4f,5f,6f) });
            b.AddStrand(new[] { (1f,2f,3f), (4f,5f,6f) });
            a.Step(0.3f); b.Step(0.3f);
            if (a.Strands[0].Points[1] != b.Strands[0].Points[1]) throw new Exception("nondeterministic");
        });
        registry.Add("hair", "cut truncates strand", () =>
        {
            var svc = new HairStrandService();
            svc.AddStrand(new[] { (0f,0f,0f),(1f,1f,1f),(2f,2f,2f),(3f,3f,3f) });
            svc.Cut(2);
            if (svc.Strands[0].Points.Count != 2) throw new Exception("not cut");
        });
        registry.Add("hair", "clump converges tips, roots stay put", () =>
        {
            var svc = new HairStrandService();
            svc.AddStrand(new[] { (0.1f,0f,0f),(1f,1f,0f) });
            svc.AddStrand(new[] { (-0.1f,0f,0f),(-1f,1f,0f) });
            svc.Clump(0.5f);
            if (svc.Strands[0].Points[0] != (0.1f,0f,0f) || svc.Strands[1].Points[0] != (-0.1f,0f,0f)) throw new Exception("clump moved a root");
            if (svc.Strands[0].Points[1] != svc.Strands[1].Points[1]) throw new Exception("clump did not converge tips");
        });
        registry.Add("hair", "wind moves strand", () =>
        {
            var svc = new HairStrandService();
            svc.AddStrand(new[] { (0f,0f,0f),(0f,1f,0f) });
            svc.Step(1f, 0f, null, 0f, (5f,0f,0f));
            if (svc.Strands[0].Points[1].x <= 0f) throw new Exception("no wind");
        });
        registry.Add("hair", "export curves", () =>
        {
            var svc = new HairStrandService();
            svc.AddStrand(new[] { (0f,0f,0f),(0f,1f,0f) });
            var s = svc.ExportCurves();
            if (!s.StartsWith("LINE")) throw new Exception("bad export");
        });
        registry.Add("hair", "substeps runs", () =>
        {
            var svc = new HairStrandService();
            svc.AddStrand(new[] { (0f,0f,0f),(0f,1f,0f) });
            svc.Step(1f, -9.81f, null, 0f, null, 4);
            if (float.IsNaN(svc.Strands[0].Points[1].y)) throw new Exception("nan");
        });
        registry.Add("hair", "turbulence moves strand", () =>
        {
            var svc = new HairStrandService();
            svc.AddStrand(new[] { (0f,0f,0f),(0f,1f,0f) });
            svc.Step(1f, 0f, null, 0f, null, 1, (3f,0f,0f));
            if (svc.Strands[0].Points[1].x <= 0f) throw new Exception("no turbulence");
        });
        registry.Add("hair", "vortex moves strand", () =>
        {
            var svc = new HairStrandService();
            svc.AddStrand(new[] { (1f,0f,0f),(1f,1f,0f) });
            svc.Step(1f, 0f, null, 0f, null, 1, null, (0f,0f,0f));
            if (svc.Strands[0].Points[1].z == 0f && svc.Strands[0].Points[1].x == 1f) throw new Exception("no vortex");
        });
    }
}