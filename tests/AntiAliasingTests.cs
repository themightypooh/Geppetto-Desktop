using System.Numerics;
using Effigy;
using System.Text.Json;
using Marionette.Desktop;
using Silk.NET.OpenGL;

internal static class AntiAliasingTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        ShadowQualityTests.Register(registry, settings);
        PostEffectsTests.Register(registry, settings);
        registry.Add("aa", "skinned motion disocclusion, camera cuts, resize and temporal resource disposal", () =>
        {
            using var context = new HeadlessGlContext(); var gl = context.Gl;
            int before = RenderTarget.LiveObjects;
            using (var renderer = new SceneRenderer(gl) { AntiAliasing = AntiAliasingMode.Temporal, SsaoStrength=0, SsrStrength=0 })
            using (var target = new CaptureTarget(gl,128,96))
            using (var resized = new CaptureTarget(gl,96,64))
            using (var mesh = Triangle(new[] {new Vector3(0,-1,-1),new Vector3(0,1,-.9f),new Vector3(0,-.8f,1.2f)}))
            {
                var rig = new Skeleton(); rig.AddBone("root",-1,Xform.Identity); mesh.Rig=rig; mesh.HasSkin=true;
                var clip = new RigAnimFile {FrameRate=30,FrameCount=31};
                var track = new RigTrack {Name="root",Kind=RigTrackKind.Bone};
                track.Keys.Add(new RigKey {Frame=0,Local=RigPose.Identity});
                track.Keys.Add(new RigKey {Frame=30,Local=new RigPose(new Vec3(0,1.8f,0),Quat.Identity,Vec3.One)});
                clip.Tracks.Add(track);
                var player = new MarionettePlayer(rig) {Loop=false}; player.LoadClip(clip);
                var camera = new OrbitCamera {Distance=5,YawDegrees=0,PitchDegrees=0};
                mesh.Upload(gl);
                try
                {
                    void Draw(int w=128,int h=96,uint output=0) => renderer.Draw(mesh,player,camera,w,h,1,output==0?target.Handle:output);
                    float At(Vector3 world)
                    {
                        var p = Project(camera,world,128,96);
                        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer,renderer.PostChainTargets.ColorFbo);
                        var pixel = new float[4];
                        gl.ReadPixels<float>((int)p.X,(int)p.Y,1,1,PixelFormat.Rgba,PixelType.Float,pixel.AsSpan());
                        return pixel[0];
                    }
                    var oldCenter = new Vector3(0,-.27f,-.23f);
                    for(int i=0;i<16;i++) Draw();
                    Check(At(oldCenter)>99,"skin fixture missing before motion");
                    player.Time=1;player.Step(0);Draw();
                    Check(At(oldCenter)<.2f,"disocclusion retained the old skinned silhouette");
                    var newCenter = oldCenter+new Vector3(0,1.8f,0);
                    Check(At(newCenter)>99,"new skinned position lost during reprojection");
                    int allocated = RenderTarget.LiveObjects;
                    for(int i=0;i<3;i++) {Draw(96,64,resized.Handle);Draw();}
                    Check(RenderTarget.LiveObjects==allocated,"TAA resize leaked render targets");
                    camera.Distance=10;Draw();
                    float cutValue=At(newCenter);
                    renderer.ResetTemporalHistory();Draw();
                    float resetValue=At(newCenter);
                    renderer.AntiAliasing=AntiAliasingMode.None;Draw();
                    float currentReference=At(newCenter);
                    // Compare against the current pose without history: lighting can
                    // vary with view distance, so an absolute emission value is no oracle.
                    Check(currentReference>80,"camera-cut fixture lost the foreground");
                    Check(Math.Abs(cutValue-currentReference)<.002f,"camera cut retained invalid foreground history");
                    Check(Math.Abs(resetValue-currentReference)<.002f,"explicit reset retained stale history");
                    Check(gl.GetError()==GLEnum.NoError,"GL error in temporal motion/resize checks");
                }
                finally {mesh.ReleaseGpu(gl);}
            }
            Check(RenderTarget.LiveObjects==before,"temporal renderer disposal leaked targets");
        });
        registry.Add("aa", "shared renderer analytic edge coverage, convergence, exact extent and repeatability", () =>
        {
            const int w = 128, h = 96;
            using var context = new HeadlessGlContext();
            var gl = context.Gl;
            using var renderer = new SceneRenderer(gl) { SsaoStrength = 0, SsrStrength = 0 };
            using var target = new CaptureTarget(gl, w, h);
            var camera = new OrbitCamera { Distance = 5, YawDegrees = 0, PitchDegrees = 0 };
            var points = new[] { new Vector3(0,-1,-1), new Vector3(0,1,-.9f), new Vector3(0,-.8f,1.2f) };
            using var mesh = Triangle(points);
            mesh.Upload(gl);
            try
            {
                var projected = points.Select(p => Project(camera, p, w, h)).ToList();
                float[] Draw()
                {
                    renderer.Draw(mesh, null, camera, w, h, 1, target.Handle);
                    gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, renderer.PostChainTargets.ColorFbo);
                    gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
                    var rgba = new float[w * h * 4];
                    gl.ReadPixels<float>(0, 0, w, h, PixelFormat.Rgba, PixelType.Float, rgba.AsSpan());
                    Check(gl.GetError() == GLEnum.NoError, "GL error in AA capture");
                    return Enumerable.Range(0, w * h).Select(i => rgba[4*i]).ToArray();
                }
                renderer.AntiAliasing = AntiAliasingMode.None;
                var aliased = Draw();
                float background = aliased[0], interior = aliased.Max();
                float[] Normalize(float[] pixels) => pixels.Select(p => (p-background)/(interior-background)).ToArray();
                var edge = new List<(int Index, float Coverage)>();
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float coverage = Coverage(projected, x, y);
                    if (coverage > .001f && coverage < .999f) edge.Add((y*w+x, coverage));
                }
                Check(edge.Count > 60, "analytic fixture has insufficient partial pixels");
                double Error(float[] values) => edge.Average(p => Math.Abs(values[p.Index]-p.Coverage));
                double noAa = Error(Normalize(aliased));
                renderer.AntiAliasing = AntiAliasingMode.Msaa4;
                var msaa = Draw();
                double msaaError = Error(Normalize(msaa));
                Check(msaaError < .15 && msaaError < noAa * .65, $"MSAA coverage error {msaaError:F4}, aliased {noAa:F4}");
                Check(msaa.Length == w*h, "MSAA changed capture extent");
                renderer.AntiAliasing = AntiAliasingMode.Temporal;
                float[] first = Normalize(Draw()), second = Normalize(Draw()), previous = second, last = second;
                for (int i = 2; i < 64; i++) { previous = last; last = Normalize(Draw()); }
                double Delta(float[] a, float[] b) => a.Zip(b, (x,y) => (double)Math.Abs(x-y)).Average();
                double early = Delta(first, second), late = Delta(previous, last), taaError = Error(last);
                Console.WriteLine($"AA metrics none={noAa:F5}, msaa={msaaError:F5}, taa={taaError:F5}, earlyDelta={early:F6}, lateDelta={late:F6}");
                Check(taaError < .20 && taaError < noAa * .8, $"TAA coverage error {taaError:F4}, aliased {noAa:F4}");
                Check(late < .005 && late < early * .8, $"TAA not converged: early={early}, late={late}");
                byte[] Display()
                {
                    gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, target.Handle);
                    var rgba = new byte[w*h*4];
                    gl.ReadPixels<byte>(0,0,w,h,PixelFormat.Rgba,PixelType.UnsignedByte,rgba.AsSpan());
                    return rgba;
                }
                var repeatA = Display();
                renderer.ResetTemporalHistory();
                for(int i=0;i<64;i++) Draw();
                Check(repeatA.SequenceEqual(Display()), "same static TAA sequence changed capture bytes");
                renderer.AntiAliasing = AntiAliasingMode.None;
                Draw(); var bindA = Display(); Draw();
                Check(bindA.SequenceEqual(Display()), "bind captures changed bytes");
                Check(MsaaPass.LiveObjects == 0, "leaving MSAA leaked renderbuffers/framebuffer");
                File.WriteAllText(Path.Combine(settings.WorkDirectory,"r3-aa-metrics.json"), JsonSerializer.Serialize(new {noAa,msaaError,taaError,early,late,partialPixels=edge.Count,w,h}));
            }
            finally { mesh.ReleaseGpu(gl); }
        });
    }

    static ViewportMesh Triangle(Vector3[] points)
    {
        var vertices = new List<float>();
        foreach(var p in points) vertices.AddRange(new[] {p.X,p.Y,p.Z,1,0,0,0,0,0,0,1,0,0,0,0,0,0,1,0,1f});
        var material = new MaterialPalette.Entry("edge",0,0,0,1,0,100,100,100);
        var data = vertices.ToArray();
        return new ViewportMesh { Vertices=data, BaseVertices=(float[])data.Clone(), Indices=new uint[]{0,1,2},
            Batches=new[]{new CpuMesh.DrawBatch("edge",material,0,3)}, Centre=Vector3.Zero,Radius=2,TriangleCount=1 };
    }
    static Vector2 Project(OrbitCamera c, Vector3 p,int w,int h)
    {
        var v=Vector4.Transform(new Vector4(p,1),c.ViewMatrix()*c.ProjectionMatrix(w/(float)h));
        return new Vector2((v.X/v.W*.5f+.5f)*w,(v.Y/v.W*.5f+.5f)*h);
    }
    // Exact polygon/pixel intersection, independent of raster sample positions.
    static float Coverage(List<Vector2> polygon,int x,int y)
    {
        var clipped=polygon;
        foreach(var (axis,bound,sign) in new[]{(0,(float)x,1),(0,x+1f,-1),(1,(float)y,1),(1,y+1f,-1)})
        {
            var output=new List<Vector2>();
            for(int i=0;i<clipped.Count;i++)
            {
                var a=clipped[i];var b=clipped[(i+1)%clipped.Count];
                float da=((axis==0?a.X:a.Y)-bound)*sign,db=((axis==0?b.X:b.Y)-bound)*sign;
                if(da>=0) output.Add(a);
                if((da>=0)!=(db>=0)) output.Add(Vector2.Lerp(a,b,da/(da-db)));
            }
            clipped=output;
        }
        double twice=0;
        for(int i=0;i<clipped.Count;i++) {var a=clipped[i];var b=clipped[(i+1)%clipped.Count];twice+=(double)a.X*b.Y-(double)b.X*a.Y;}
        return (float)(Math.Abs(twice)*.5);
    }
    static void Check(bool condition,string reason) {if(!condition)throw new Exception(reason);}
}
