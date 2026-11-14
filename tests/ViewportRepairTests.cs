using Effigy;
using Marionette.Desktop;
using Silk.NET.OpenGL;
using System.Text.Json.Nodes;

internal static class ViewportRepairTests
{
    static void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
    static PartStudio Box()
    {
        var s = new PartStudio();
        var r = BuildScript.Run(s, "add Primitive name=Body width=4 depth=4 height=4");
        Require(r.Ok, r.ToString());
        return s;
    }
    static PaintCanvas Solid(byte red, byte green, byte blue)
    {
        var c = new PaintCanvas(8, 8);
        for (int i=0; i<c.Rgba.Length; i+=4) { c.Rgba[i]=red; c.Rgba[i+1]=green; c.Rgba[i+2]=blue; c.Rgba[i+3]=255; }
        return c;
    }
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        registry.Add("viewport-repair", "material discovery skips directory symlink cycles", () =>
        {
            if (!OperatingSystem.IsLinux()) return;
            var root=Path.Combine(settings.WorkDirectory,"material-cycle"); Directory.CreateDirectory(root);
            Directory.CreateSymbolicLink(Path.Combine(root,"again"),root);
            Require(VmdlReader.FindForRenderMesh(root,"absent.fbx") is null,"unexpected ModelDoc");
        });
        registry.Add("viewport-repair", "body atlases sharing a saved slot remain independent", () =>
        {
            var s = Box();
            var second = s.Bodies[0].Clone();
            s.Bodies.Add(new Body("second", "second", second.Mesh));
            s.Bodies[0].Mesh.Paint = Solid(255,0,0);
            second.Mesh.Paint = Solid(0,0,255);
            using var snap = DocumentSession.CreateEffigy(Path.Combine(settings.WorkDirectory,"paint.effigy"),s).BuildSnapshot();
            var images = snap.Mesh.Batches.Select(b => b.AlbedoImage).Where(i => i is not null).ToArray();
            Require(images.Length == 2, "atlases merged into one batch");
            Require(images[0]!.Rgba[0]==255 && images[0]!.Rgba[2]==0 && images[1]!.Rgba[0]==0 && images[1]!.Rgba[2]==255,"body paint colours crossed");
            Require(snap.Mesh.PaintCanvases.Values.Distinct().Count() == 2, "canvas identity lost");
            Require(s.Bodies.All(b => b.Mesh.Faces.All(f=>f.Material==0)), "snapshot changed saved material slots");

            // PaintCanvas row 0 is UV V=0; GL upload flips DecodedImage rows, so PaintImage must pre-flip
            // row 0 to the bottom of DecodedImage (otherwise UV islands sample 1-V upside down).
            var vFlipCheck = new PaintCanvas(2, 2);
            // Row 0 (V=0): red; Row 1 (V=1): green
            for (int x = 0; x < 2; x++)
            {
                int r0 = x * 4, r1 = (2 + x) * 4;
                vFlipCheck.Rgba[r0] = 255; vFlipCheck.Rgba[r0 + 3] = 255;
                vFlipCheck.Rgba[r1 + 1] = 255; vFlipCheck.Rgba[r1 + 3] = 255;
            }
            var quadStudio = new PartStudio();
            var quadMesh = new PolyMesh();
            quadMesh.Positions.AddRange(new[] { new Vec3(0, -1, -1), new Vec3(0, 1, -1), new Vec3(0, 1, 1), new Vec3(0, -1, 1) });
            quadMesh.AddFace(new[] { 0, 1, 2, 3 }, new[] { new Vec2(0, 0), new Vec2(1, 0), new Vec2(1, 1), new Vec2(0, 1) }, 0);
            quadMesh.Paint = vFlipCheck;
            quadStudio.Bodies.Add(new Body("quad", "quad", quadMesh));
            using var quadSnap = DocumentSession.CreateEffigy(Path.Combine(settings.WorkDirectory, "quad-vflip.effigy"), quadStudio).BuildSnapshot();
            var decoded = quadSnap.Mesh.Batches[0].AlbedoImage!;
            // DecodedImage row 0 is V=1 (green) and row 1 is V=0 (red) before GL upload flips it back.
            Require(decoded.Rgba[1] == 255 && decoded.Rgba[0] == 0, "DecodedImage top row should hold PaintCanvas V=1 (row 1)");
            Require(decoded.Rgba[2 * 4] == 255 && decoded.Rgba[2 * 4 + 1] == 0, "DecodedImage bottom row should hold PaintCanvas V=0 (row 0)");
        });
        registry.Add("viewport-repair", "isolated script tool executes and publishes a step", () =>
        {
            var tools = new AiSuiteTools(Path.Combine(settings.WorkDirectory,"script-worker")) { Isolated=true };
            tools.Live = new AiLiveRun(tools);
            tools.ExecuteAsync("add_feature", JsonNode.Parse("{\"type\":\"Primitive\",\"fields\":{}}")!, default).GetAwaiter().GetResult();
            Require(tools.Studio.Bodies.Count == 1 && tools.GenerateTopologyReport().Faces > 0, "isolated operation left parent tools without mesh state");
            long before = tools.Live.Revision;
            string result = tools.ExecuteAsync("measure_model", new JsonObject { ["script"]="describe\ndescribe" }, default).GetAwaiter().GetResult();
            Require(!string.IsNullOrWhiteSpace(result), "script returned no result");
            Require(tools.Live.Revision == before+2, "script step not published");
            Require(tools.Studio.Features.Count == 1, "worker lost document");
            long good=tools.Live.Revision;
            try { tools.ExecuteAsync("bake_export",new JsonObject { ["script"]="export ../../outside.obj" },default).GetAwaiter().GetResult(); throw new Exception("escaping export accepted"); }
            catch(InvalidOperationException ex) { Require(ex.Message.Contains("escapes"),ex.Message); }
            Require(tools.Live.Revision==good,"failed script published a revision");
        });
        registry.Add("viewport-repair", "late preview receives existing steps in order", () =>
        {
            var tools = new AiSuiteTools(Path.Combine(settings.WorkDirectory,"late-preview"));
            var live = new AiLiveRun(tools);
            for (int i=0;i<4;i++) live.Commit(tools.Studio,"step "+i);
            using var preview = new AiLivePreview(tools.Root);
            for(long expected=1;expected<=4;expected++)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                AiLivePreview.Update? update=null;
                while(timer.ElapsedMilliseconds<10000 && (update=preview.Take()) is null) Thread.Sleep(10);
                Require(update is not null,"step timed out: "+preview.Error);
                using var mesh=update!.Mesh;
                Require(update.Revision==expected,$"expected {expected}, received {update.Revision}");
            }
        });
        registry.Add("viewport-repair", "hardware EGL fur masks shells and preserves coat colour", () =>
        {
            var s=Box(); var fur=s.Add(new FurFeature()); fur.Coverage.Index=0;
            fur.Bodies.BodyIds.Add(s.Bodies[0].Id); fur.Length.Value=0.6f; fur.Density.Value=8;
            var report=s.Rebuild(); Require(!report.HasErrors,"fur rebuild failed");
            var path=Path.Combine(settings.WorkDirectory,"fur.effigy");
            File.WriteAllText(path,StudioDocument.Write(s));
            Require(StudioCache.TrySave(s,path,out var saved),"fur cache save failed: "+saved);
            var restored=StudioCache.Load(path,out var note);
            Require(note.StartsWith("restored"),"fur fixture did not exercise cache restore: "+note);
            using var snap=DocumentSession.CreateEffigy(path,restored).BuildSnapshot();
            Require(snap.Mesh.FurSlots.Count>0,"fur metadata not in snapshot");
            using var context=new HeadlessGlContext(); var gl=context.Gl;
            using var renderer=new SceneRenderer(gl); renderer.GroundShadow=false;
            using var target=new CaptureTarget(gl,256,256);
            var camera=new OrbitCamera(); camera.Frame(snap.Mesh.Centre,snap.Mesh.Radius); camera.YawDegrees=30; camera.PitchDegrees=20;
            snap.Mesh.Upload(gl);
            byte[] Draw(string name)
            {
                renderer.Draw(snap.Mesh,null,camera,256,256,1,target.Handle); gl.Finish();
                var bytes=new byte[256*256*3]; gl.ReadPixels<byte>(0,0,256,256,PixelFormat.Rgb,PixelType.UnsignedByte,bytes.AsSpan());
                var rgba = new byte[256*256*4];
                for(int i=0;i<256*256;i++) { int row=i/256, col=i%256; int src=((255-row)*256+col)*3; for(int ch=0;ch<3;ch++) rgba[i*4+ch]=bytes[src+ch]; rgba[i*4+3]=255; }
                PngWriter.WriteFileRgba(Path.Combine(settings.WorkDirectory,name+".png"),rgba,256,256);
                return bytes;
            }
            var masked=Draw("fur-masked"); snap.Mesh.FurSlots.Clear(); var solid=Draw("fur-solid-control");
            int changed=masked.Zip(solid,(a,b)=>Math.Abs(a-b)>8?1:0).Sum();
            Require(changed>200,"fur shading did not visibly change shells");
            snap.Mesh.ReleaseGpu(gl); Require(gl.GetError()==GLEnum.NoError,"GPU error");
        });
    }
}
