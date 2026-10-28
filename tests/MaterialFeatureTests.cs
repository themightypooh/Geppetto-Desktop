using System.Numerics;
using System.Text.Json;
using Effigy;
using Marionette.Desktop;
using Silk.NET.OpenGL;

internal static class MaterialFeatureTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        registry.Add("material-feature", "lobe parameters are finite, bounded and preserve zero defaults", () =>
        {
            var d=new MaterialDescriptor("fixture");
            Check(MaterialLobes.From(d)==MaterialLobes.None,"zero-weight default changed");
            var clamped=MaterialLobes.From(d with {Transmission=2,Thickness=-1,Ior=.5f,Sheen=-1,Clearcoat=3,Anisotropy=4,AttenuationColor=new Vec3(-1,2,.5f)});
            Check(clamped.Transmission==1&&clamped.Thickness==0&&clamped.Ior==1&&clamped.Sheen==0&&clamped.Clearcoat==1&&clamped.Anisotropy==.95f,"scalar bounds not applied");
            Check(clamped.AttenuationColor==new Vector3(0,1,.5f),"absorption colour may amplify radiance");
            foreach(var bad in new[]{d with{Transmission=float.NaN},d with{Thickness=float.PositiveInfinity},d with{SubsurfaceRadius=new Vec3(1,float.NaN,1)},d with{SheenTint=new Vec3(float.PositiveInfinity,1,1)},d with{AnisotropyRotation=float.NaN}})
            {
                try {MaterialLobes.From(bad);throw new Exception("nonfinite lobe accepted");} catch(ArgumentException){}
            }
        });
        registry.Add("material-feature", "hardware EGL transmission obeys thickness, exposure and opaque backdrop", () =>
        {
            using var fixture=new Fixture(settings);
            var glass=new MaterialDescriptor("panel") {Transmission=1,Thickness=.1f,Ior=1.5f,RoughnessFactor=.04f,AttenuationColor=new Vec3(.5f,.8f,1),AttenuationDistance=1};
            var thin=fixture.Draw(glass,backdrop:true);
            var thick=fixture.Draw(glass with{Thickness=1.1f},backdrop:true);
            var doubleExposure=fixture.Draw(glass,backdrop:true,exposure:2);
            var dark=fixture.Draw(glass,backdrop:false);
            var msaa=fixture.Draw(glass,backdrop:true,aa:AntiAliasingMode.Msaa4);
            Check(Vector3.Distance(Center(msaa),Center(thin))<.02f,"MSAA opaque capture differs from the single-sample glass reference");
            var a=Center(thin);var b=Center(thick);var e=Center(doubleExposure);var c=Center(dark);
            Console.WriteLine($"Transmission thin={a}, thick={b}, exposure2={e}, noBackdrop={c}");
            Check(a.X>b.X+.3f&&a.Y>b.Y+.1f,"thickness did not attenuate colour");
            Check(b.Z>a.Z*.95f&&b.Z<a.Z*1.05f,"white attenuation channel changed with thickness");
            Check(Math.Abs(e.X/a.X-2)<.02&&Math.Abs(e.Y/a.Y-2)<.02,"transmitted colour applies exposure more than once");
            Check(a.X>c.X+1,"transmission is not sampling the opaque backdrop");
            fixture.Save("transmission",new {thin=a,thick=b,exposure2=e,noBackdrop=c});
        });
        registry.Add("material-feature", "hardware EGL subsurface, sheen, clearcoat and anisotropy have independent responses", () =>
        {
            using var fixture=new Fixture(settings);
            var baseline=new MaterialDescriptor("panel") {BaseColorFactor=new Vec4(.25f,.25f,.25f,1),RoughnessFactor=.7f};
            var plain=fixture.Draw(baseline);
            var sss=fixture.Draw(baseline with{Subsurface=1,SubsurfaceRadius=new Vec3(1,.08f,.02f),SubsurfaceScale=.05f},normal:Vector3.Normalize(new Vector3(.15f,.8f,-.6f)));
            var grazing=fixture.Draw(baseline,normal:Vector3.Normalize(new Vector3(.15f,.8f,-.6f)));
            var delta=Center(sss)-Center(grazing);
            Console.WriteLine($"SSS delta={delta}");
            Check(delta.X>.02f&&delta.X>delta.Y*2&&delta.X>delta.Z*2,"subsurface radius did not produce red grazing bleed");
            var sheenBase=baseline with{BaseColorFactor=new Vec4(.01f,.01f,.01f,1)};
            var sheen=fixture.Draw(sheenBase with{Sheen=1,SheenTint=new Vec3(.1f,.2f,1)},normal:Vector3.Normalize(new Vector3(.15f,.8f,.6f)));
            var noSheen=fixture.Draw(sheenBase,normal:Vector3.Normalize(new Vector3(.15f,.8f,.6f)));
            var sheenDelta=Center(sheen)-Center(noSheen);
            Check(sheenDelta.Z>.02f&&sheenDelta.Z>sheenDelta.X*2,"sheen tint/retroreflection absent");
            var coated=fixture.Draw(baseline with{Clearcoat=1,ClearcoatRoughness=.04f},point:10);
            var noCoat=fixture.Draw(baseline,point:10);
            var coatDifference=Center(coated)-Center(noCoat);
            float coatShoulder=coated[(64*128+80)*4]-noCoat[(64*128+80)*4];
            Check(coatDifference.X>.1f&&coatDifference.X>Math.Abs(coatShoulder)*4,"clearcoat did not add a narrow second highlight over the rough base");
            // Anisotropic tangent orientation must change the spatial highlight, not a constant tint.
            var metal=baseline with{MetallicFactor=1,RoughnessFactor=.35f,Anisotropy=.8f};
            var horizontal=fixture.Draw(metal,point:10);
            var vertical=fixture.Draw(metal with{AnisotropyRotation=MathF.PI/2},point:10);
            float xDiff=horizontal[(64*128+76)*4]-vertical[(64*128+76)*4];
            float yDiff=horizontal[(76*128+64)*4]-vertical[(76*128+64)*4];
            Console.WriteLine($"Sheen delta={sheenDelta}, coat delta={coatDifference}, anisotropic x={xDiff}, y={yDiff}");
            Check(xDiff*yDiff<0&&Math.Abs(xDiff)>.005f&&Math.Abs(yDiff)>.005f,"anisotropy rotation did not rotate the stretched highlight");
            fixture.Save("lobes",new {subsurfaceDelta=delta,sheenDelta,coatDifference,xDiff,yDiff});
        });
        registry.Add("material-feature", "hardware EGL hashed alpha is order independent and modes are explicit", () =>
        {
            using var fixture=new Fixture(settings);
            var hashed=new MaterialDescriptor("panel") {BaseColorFactor=new Vec4(1,1,1,.5f),EmissionFactor=new Vec3(2,0,0),AlphaMode=AlphaMode.Hashed};
            var a=fixture.Draw(hashed,secondLayer:true);
            var b=fixture.Draw(hashed,secondLayer:true,reverse:true);
            Check(a.SequenceEqual(b),"hashed alpha depends on draw order");
            int covered=0,total=0;
            for(int y=42;y<86;y++)for(int x=42;x<86;x++){total++;if(a[(y*128+x)*4]>1)covered++;}

            Check(covered>total*.4&&covered<total*.6,$"hashed alpha coverage {covered}/{total}");
            var masked=fixture.Draw(hashed with{AlphaMode=AlphaMode.Mask,AlphaCutoff=.6f});
            var opaque=fixture.Draw(hashed with{AlphaMode=AlphaMode.Opaque});
            var blended=fixture.Draw(hashed with{AlphaMode=AlphaMode.Blend});
            Check(Center(masked).X<.2f&&Center(opaque).X>2&&Center(blended).X>Center(masked).X+.5f&&Center(blended).X<Center(opaque).X,"opaque/mask/blend policies not distinct");
        });
    }

    sealed class Fixture : IDisposable
    {
        readonly HeadlessGlContext context=new();
        readonly SceneRenderer renderer;
        readonly CaptureTarget target;
        readonly string root;
        public Fixture(TestSettings settings)
        {
            renderer=new SceneRenderer(context.Gl){SsaoStrength=0,SsrStrength=0};
            target=new CaptureTarget(context.Gl,128,128);
            root=Path.Combine(settings.WorkDirectory,"r6");Directory.CreateDirectory(root);
        }
        public float[] Draw(MaterialDescriptor descriptor,bool backdrop=false,float exposure=1,Vector3? normal=null,float point=0,bool secondLayer=false,bool reverse=false,AntiAliasingMode aa=AntiAliasingMode.None)
        {
            var f=new HeadlessImageChecks.Fixture();
            f.Quad("panel",Vector3.Zero,new Vector3(0,1,0),new Vector3(0,0,1),normal??Vector3.UnitX,new MaterialPalette.Entry("panel",1,1,1));
            if(backdrop)f.Quad("back",new Vector3(-1,0,0),new Vector3(0,2,0),new Vector3(0,0,2),Vector3.UnitX,new MaterialPalette.Entry("back",0,0,0,1,0,4,4,4));
            if(secondLayer)f.Quad("second",new Vector3(-.3f,0,0),new Vector3(0,1,0),new Vector3(0,0,1),Vector3.UnitX,new MaterialPalette.Entry("second",1,1,1));
            using var mesh=f.Mesh();
            var resolver=new MaterialResolver(root,"r6");resolver.SetOverride("panel",descriptor,required:true);
            if(backdrop)resolver.SetOverride("back",new MaterialDescriptor("back"){BaseColorFactor=new Vec4(0,0,0,1),EmissionFactor=new Vec3(4,4,4)},required:true);
            if(secondLayer)resolver.SetOverride("second",descriptor with{Name="second",EmissionFactor=new Vec3(0,0,2)},required:true);
            mesh.ApplyMaterials(resolver);
            if(reverse)Array.Reverse(mesh.Batches);
            var gl=context.Gl;mesh.Upload(gl);
            try
            {
                renderer.AntiAliasing=aa;
                renderer.ResetTemporalHistory();renderer.PointLightPosition=new Vector3(5,0,0);renderer.PointLightIntensity=point;
                renderer.Draw(mesh,null,new OrbitCamera{Distance=5,YawDegrees=0,PitchDegrees=0},128,128,exposure,target.Handle);
                gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer,renderer.PostChainTargets.ColorFbo);
                gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
                var image=new float[128*128*4];gl.ReadPixels<float>(0,0,128,128,PixelFormat.Rgba,PixelType.Float,image.AsSpan());
                Check(gl.GetError()==GLEnum.NoError,"GL error in material feature fixture");
                Check(image.All(float.IsFinite),$"nonfinite HDR material result: {image.Count(float.IsNaN)} NaN, {image.Count(float.IsInfinity)} infinity; max finite {image.Where(float.IsFinite).Max()}");
                return image;
            }
            finally {mesh.ReleaseGpu(gl);}
        }
        public void Save(string name,object metrics)=>File.WriteAllText(Path.Combine(root,name+".json"),JsonSerializer.Serialize(metrics,new JsonSerializerOptions{IncludeFields=true}));
        public void Dispose(){target.Dispose();renderer.Dispose();context.Dispose();}
    }
    static Vector3 Center(float[] image) => new(image[(64*128+64)*4],image[(64*128+64)*4+1],image[(64*128+64)*4+2]);
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
}
