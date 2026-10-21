using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Effigy;
using Marionette.Desktop;
using Silk.NET.OpenGL;

internal static class PostEffectsTests
{
    public static void Register(TestRegistry registry,TestSettings settings)
    {
        registry.Add("post-effects","hardware EGL physical focus and aperture control background blur",()=>
        {
            using var context=new HeadlessGlContext();var gl=context.Gl;
            using var renderer=new SceneRenderer(gl){SsaoStrength=0,SsrStrength=0,DepthOfFieldFocusDistance=5,View=new ViewSettings{Transform=ViewTransform.Standard}};
            renderer.PostChainTargets.Intensity=0;
            using var target=new CaptureTarget(gl,128,128);
            var camera=new OrbitCamera{Distance=5,YawDegrees=0,PitchDegrees=0,Near=.1f,Far=40};
            var f=new HeadlessImageChecks.Fixture();
            for(int i=0;i<16;i++)
            {
                float y=(i-7.5f)*.075f;
                var mat=new MaterialPalette.Entry("stripe",0,0,0,1,0,i%2,i%2,i%2);
                f.Quad("focus",new Vector3(0,-.85f+y,0),new Vector3(0,.0375f,0),new Vector3(0,0,.6f),Vector3.UnitX,mat);
                f.Quad("far",new Vector3(-3,1.35f+y*1.6f,0),new Vector3(0,.06f,0),new Vector3(0,0,.96f),Vector3.UnitX,mat);
            }
            // A small near-field red emitter exercises the signed foreground CoC
            // and its coverage spreading, clear of the in-focus/remote stripe probes.
            f.Quad("near",new Vector3(2,.1f,.9f),new Vector3(0,.1f,0),new Vector3(0,0,.1f),Vector3.UnitX,new MaterialPalette.Entry("near",0,0,0,1,0,2,0,0));
            using var mesh=f.Mesh();mesh.BaseVertices=(float[])mesh.Vertices.Clone();mesh.Upload(gl);
            try
            {
                byte[] Draw(float aperture)
                {
                    renderer.DepthOfFieldFStop=aperture;renderer.Draw(mesh,null,camera,128,128,1,target.Handle);
                    gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer,target.Handle);
                    var pixels=new byte[128*128*4];gl.ReadPixels<byte>(0,0,128,128,PixelFormat.Rgba,PixelType.UnsignedByte,pixels.AsSpan());
                    Check(gl.GetError()==GLEnum.NoError,"GL error in DoF fixture");return pixels;
                }
                float Coc(Vector3 world)
                {
                    var prep=(RenderTarget)typeof(BloomPass).GetField("_dofPrep",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(renderer.PostChainTargets)!;
                    var p=Project(camera,world);var pixel=new float[4];gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer,prep.Framebuffer);
                    gl.ReadPixels<float>((int)p.X/2,(int)p.Y/2,1,1,PixelFormat.Rgba,PixelType.Float,pixel.AsSpan());return pixel[3];
                }
                var off=Draw(0);var stopped=Draw(8);
                float coc8=Coc(new Vector3(-3,1.35f,0));
                var wide=Draw(2);float coc2=Coc(new Vector3(-3,1.35f,0)),focused=Coc(new Vector3(0,-.85f,0)),nearCoc=Coc(new Vector3(2,.1f,.9f));
                double Focal=50/25.4,SensorHeight=2*Focal*Math.Tan(Math.PI/8);
                double reference=Focal*Focal*(8-5)/(2*8*(5-Focal))/SensorHeight*128/2;
                Check(Math.Abs(coc2-reference)<.02&&Math.Abs(coc2/coc8-4)<.02,$"DoF CoC mismatch: {coc2}, {coc8}, expected {reference}");
                Check(Math.Abs(focused)<.02,$"in-focus CoC is {focused}");
                double nearReference=Focal*Focal*(3-5)/(2*3*(5-Focal))/SensorHeight*128/2;
                Check(Math.Abs(nearCoc-nearReference)<.03,$"foreground CoC mismatch: {nearCoc}, expected {nearReference}");
                int RedSpread(byte[] pixels)
                {
                    int n=0;for(int y=88;y<128;y++)for(int x=45;x<94;x++)
                        if((x<63||x>75||y<104||y>117)&&pixels[(y*128+x)*4]>pixels[(y*128+x)*4+1]+20)n++;
                    return n;
                }
                int nearSharp=RedSpread(off),nearBlurred=RedSpread(wide);
                Check(nearBlurred>nearSharp+20,$"near field did not spread beyond its silhouette: {nearSharp} to {nearBlurred}");
                renderer.PostChainTargets.ApertureBlades=5;
                var pentagon=Draw(2);renderer.PostChainTargets.ApertureRotation=.6f;var rotated=Draw(2);
                Check(!wide.SequenceEqual(pentagon)&&!pentagon.SequenceEqual(rotated),"bokeh blade count/rotation did not affect the image");
                double Contrast(byte[] image,int x0,int x1)
                {
                    var values=new List<double>();for(int y=57;y<71;y++)for(int x=x0;x<x1;x++)values.Add(image[(y*128+x)*4]);
                    double mean=values.Average();return Math.Sqrt(values.Average(x=>(x-mean)*(x-mean)));
                }
                double far8=Contrast(stopped,76,91),far2=Contrast(wide,76,91);
                double focusDifference=0;int count=0;
                for(int y=55;y<73;y++)for(int x=23;x<51;x++){focusDifference+=Math.Abs(off[(y*128+x)*4]-wide[(y*128+x)*4]);count++;}
                focusDifference/=count;
                Console.WriteLine($"DoF CoC f8={coc8:F3}, f2={coc2:F3}, focused={focused}; background contrast f8={far8:F2}, f2={far2:F2}; focus delta={focusDifference:F3}");
                Check(far2<far8*.75,"larger aperture did not blur background more");
                Check(focusDifference<2,"DoF blurred the in-focus subject");
                File.WriteAllText(Path.Combine(settings.WorkDirectory,"r7-dof.json"),JsonSerializer.Serialize(new{coc8,coc2,focused,nearCoc,nearReference,nearSharp,nearBlurred,reference,far8,far2,focusDifference}));
            }
            finally{mesh.ReleaseGpu(gl);}
        });
        registry.Add("post-effects","hardware EGL skinned motion smears along velocity and rejects stale history",()=>
        {
            using var context=new HeadlessGlContext();var gl=context.Gl;
            int before=RenderTarget.LiveObjects;
            using(var renderer=new SceneRenderer(gl){SsaoStrength=0,SsrStrength=0,MotionBlurShutter=1})
            using(var target=new CaptureTarget(gl,128,128))
            using(var resized=new CaptureTarget(gl,96,64))
            {
                var camera=new OrbitCamera{Distance=5,YawDegrees=0,PitchDegrees=0,Near=.1f,Far=40};
                using var mesh=new HeadlessImageChecks.Fixture().Quad("moving",Vector3.Zero,new Vector3(0,.08f,0),new Vector3(0,0,.5f),Vector3.UnitX,new MaterialPalette.Entry("moving",0,0,0,1,0,4,0,0)).Mesh();
                mesh.BaseVertices=(float[])mesh.Vertices.Clone();
                var rig=new Skeleton();rig.AddBone("root",-1,Xform.Identity);mesh.Rig=rig;mesh.HasSkin=true;
                var player=new MarionettePlayer(rig){Loop=false};
                var track=new RigTrack{Name="root",Kind=RigTrackKind.Bone};
                track.Keys.Add(new RigKey{Frame=0,Local=new RigPose(new Vec3(0,-.5f,0),Quat.Identity,Vec3.One)});
                track.Keys.Add(new RigKey{Frame=30,Local=new RigPose(new Vec3(0,.5f,0),Quat.Identity,Vec3.One)});
                var clip=new RigAnimFile{FrameRate=30,FrameCount=31};clip.Tracks.Add(track);player.LoadClip(clip);
                mesh.Upload(gl);
                try
                {
                    float[] Draw(int w=128,int h=128,uint output=0,ViewportMesh? source=null)
                    {
                        renderer.Draw(source??mesh,player,camera,w,h,1,output==0?target.Handle:output);
                        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer,renderer.PostChainTargets.ColorFbo);
                        var image=new float[w*h*4];gl.ReadPixels<float>(0,0,(uint)w,(uint)h,PixelFormat.Rgba,PixelType.Float,image.AsSpan());
                        Check(gl.GetError()==GLEnum.NoError&&image.All(float.IsFinite),"invalid motion blur output");return image;
                    }
                    player.Time=0;player.Step(0);Draw();player.Time=1;player.Step(0);var moving=Draw();
                    var still=Draw(); // same pose has zero velocity on the next frame
                    int Count(float[] a,bool horizontal)=>Enumerable.Range(0,128).Count(i=>a[((horizontal?64:i)*128+(horizontal?i:79))*4]>.25f);
                    int blurWidth=Count(moving,true),sharpWidth=Count(still,true),height=Count(moving,false);
                    Console.WriteLine($"Motion blur horizontal width={blurWidth}, sharp={sharpWidth}, vertical={height}");
                    Check(blurWidth>sharpWidth+6,$"moving geometry did not smear along velocity: {blurWidth} vs {sharpWidth}");
                    Check(height<40,"horizontal velocity created perpendicular smear");
                    player.Time=0;player.Step(0);Draw();camera.Distance=8;player.Time=1;player.Step(0);var cut=Draw();
                    renderer.MotionBlurShutter=0;var reference=Draw();
                    Check(cut.SequenceEqual(reference),"camera cut retained a stale blur streak");
                    renderer.MotionBlurShutter=1;Draw(96,64,resized.Handle);var resize=Draw();renderer.MotionBlurShutter=0;reference=Draw();
                    Check(resize.SequenceEqual(reference),"resize retained stale motion history");
                    using var reloaded=new HeadlessImageChecks.Fixture().Quad("moving",Vector3.Zero,new Vector3(0,.08f,0),new Vector3(0,0,.5f),Vector3.UnitX,new MaterialPalette.Entry("moving",0,0,0,1,0,4,0,0)).Mesh();
                    reloaded.Rig=rig;reloaded.HasSkin=true;reloaded.Upload(gl);
                    try
                    {
                        player.Time=0;player.Step(0);renderer.MotionBlurShutter=1;Draw();
                        player.Time=1;player.Step(0);var reload=Draw(source:reloaded);
                        renderer.MotionBlurShutter=0;reference=Draw(source:reloaded);
                        Check(reload.SequenceEqual(reference),"source reload retained stale velocity history");
                    }
                    finally{reloaded.ReleaseGpu(gl);}
                    File.WriteAllText(Path.Combine(settings.WorkDirectory,"r7-motion.json"),JsonSerializer.Serialize(new{blurWidth,sharpWidth,height}));
                }
                finally{mesh.ReleaseGpu(gl);}
            }
            Check(RenderTarget.LiveObjects==before,"motion blur disposal leaked targets");
        });
    }
    static Vector2 Project(OrbitCamera camera,Vector3 p)
    {
        var c=Vector4.Transform(new Vector4(p,1),camera.ViewMatrix()*camera.ProjectionMatrix(1));return new((c.X/c.W*.5f+.5f)*128,(c.Y/c.W*.5f+.5f)*128);
    }
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
}
