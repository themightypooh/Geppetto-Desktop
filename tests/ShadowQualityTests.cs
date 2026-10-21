using System.Numerics;
using System.Text.Json;
using Marionette.Desktop;
using Silk.NET.OpenGL;

internal static class ShadowQualityTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        registry.Add("aa", "sloped receiver is acne-free and shadow radius widens the penumbra", () =>
        {
            const int size=384;
            using var context=new HeadlessGlContext(); var gl=context.Gl;
            using var renderer=new SceneRenderer(gl) {SsaoStrength=0,SsrStrength=0};
            using var target=new CaptureTarget(gl,size,size);
            var camera=HeadlessImageChecks.Camera();
            using var withCaster=Fixture(true); using var receiver=Fixture(false);
            withCaster.Upload(gl); receiver.Upload(gl);
            try
            {
                float[] Draw(ViewportMesh mesh,float radius,float bias=1.5f)
                {
                    renderer.ShadowRadius=radius; renderer.ShadowConstantBias=bias;
                    renderer.Draw(mesh,null,camera,size,size,1,target.Handle);
                    gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer,renderer.PostChainTargets.ColorFbo);
                    gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
                    var pixels=new float[size*size*4];
                    gl.ReadPixels<float>(0,0,size,size,PixelFormat.Rgba,PixelType.Float,pixels.AsSpan());
                    Check(gl.GetError()==GLEnum.NoError,"GL error in shadow fixture");
                    return pixels;
                }
                float At(float[] image,Vector3 world)
                {
                    var (x,y)=HeadlessImageChecks.Project(camera,world);
                    Check(x>1&&x<size-2&&y>1&&y<size-2,"shadow probe outside image");
                    return image[(y*size+x)*4];
                }
                var baseline=Draw(receiver,0);
                var acneOracle=Draw(receiver,0,1000);
                float maxAcne=0;
                for(float x=-2;x<=2;x+=.08f)
                for(float y=-2;y<=2;y+=.08f)
                {
                    var p=new Vector3(x,y,.3f*x);
                    maxAcne=Math.Max(maxAcne,Math.Abs(At(baseline,p)-At(acneOracle,p)));
                }
                Check(maxAcne<.015f,$"sloped receiver self-shadow bands: max linear difference {maxAcne}");
                var softBaseline=Draw(receiver,32);
                float softAcne=0;
                for(float x=-2;x<=2;x+=.08f)
                for(float y=-2;y<=2;y+=.08f)
                {
                    var p=new Vector3(x,y,.3f*x);
                    softAcne=Math.Max(softAcne,Math.Abs(At(softBaseline,p)-At(acneOracle,p)));
                }
                Check(softAcne<.015f,$"soft shadow creates slope acne bands: max linear difference {softAcne}");
                // Radius 12 leaves an umbra inside this small caster; radius 32 above
                // deliberately stress-tests slope acne across the wider kernel.
                var hard=Draw(withCaster,0); var soft=Draw(withCaster,12);
                var light=KeyShadowMap.Direction;
                float travel=1/(light.Z-.3f*light.X), shadowY=-light.Y*travel;
                var hardDrop=new List<float>();var softDrop=new List<float>();
                for(float x=-2.4f;x<.6f;x+=.01f)
                {
                    var p=new Vector3(x,shadowY,.3f*x);
                    hardDrop.Add(At(baseline,p)-At(hard,p)); softDrop.Add(At(softBaseline,p)-At(soft,p));
                }
                float full=hardDrop.Max();
                Check(full>.15f,$"fixture has no shadow contrast: {full}");
                int hardWidth=hardDrop.Count(d=>d>full*.1&&d<full*.9);
                int softWidth=softDrop.Count(d=>d>full*.1&&d<full*.9);
                Console.WriteLine($"Shadow metrics acne={maxAcne:F6}, contrast={full:F4}, hard={hardWidth}, soft={softWidth}, softContrast={softDrop.Max():F4}");
                Check(softWidth>hardWidth+10,$"shadow edge did not widen: hard={hardWidth}, soft={softWidth}, softContrast={softDrop.Max():F4}");
                Check(softDrop.Max()>full*.75,$"radius erased the shadow rather than widening it");
                renderer.AntiAliasing=AntiAliasingMode.Temporal;
                var temporalFirst=Draw(withCaster,12);var temporalSecond=Draw(withCaster,12);
                var previous=temporalSecond;var last=temporalSecond;
                for(int i=2;i<64;i++){previous=last;last=Draw(withCaster,12);}
                double Delta(float[] a,float[] b) => Enumerable.Range(0,size*size).Average(i=>(double)Math.Abs(a[i*4]-b[i*4]));
                double earlyDelta=Delta(temporalFirst,temporalSecond),lateDelta=Delta(previous,last);
                Console.WriteLine($"Temporal shadow delta early={earlyDelta:F6}, late={lateDelta:F6}");
                Check(lateDelta<.005 && lateDelta<earlyDelta*.8,$"temporal shadow not converged: early={earlyDelta}, late={lateDelta}");
                renderer.ResetTemporalHistory();
                float[] repeat=Array.Empty<float>();
                for(int i=0;i<64;i++)repeat=Draw(withCaster,12);
                Check(last.SequenceEqual(repeat),"same temporal shadow sequence changed HDR pixels");
                File.WriteAllText(Path.Combine(settings.WorkDirectory,"r3-shadow-metrics.json"),JsonSerializer.Serialize(new {maxAcne,softAcne,full,hardWidth,softWidth,earlyDelta,lateDelta,hardDrop,softDrop,step=.01}));
            }
            finally {withCaster.ReleaseGpu(gl);receiver.ReleaseGpu(gl);}
        });
    }
    static ViewportMesh Fixture(bool caster)
    {
        var normal=Vector3.Normalize(new Vector3(-.3f,0,1));
        var fixture=new HeadlessImageChecks.Fixture()
            .Quad("receiver",Vector3.Zero,new Vector3(3,0,.9f),new Vector3(0,3,0),normal,new MaterialPalette.Entry("receiver",.5f,.5f,.5f,1))
            .Quad("caster",new Vector3(0,0,1),new Vector3(.4f,0,0),new Vector3(0,.4f,0),Vector3.UnitZ,new MaterialPalette.Entry("caster",.1f,.1f,.1f,1));
        var mesh=fixture.Mesh(); mesh.BaseVertices=(float[])mesh.Vertices.Clone();
        // Identical bounds in baseline/caster renders isolate shadowing from fit changes.
        if(!caster)mesh.Batches=new[]{mesh.Batches[0]};
        return mesh;
    }
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
}
