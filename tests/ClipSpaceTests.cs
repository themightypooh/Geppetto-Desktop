using System.Numerics;
using Effigy;
using Marionette.Desktop;

internal static class ClipSpaceTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        registry.Add("clip-space", "foreign mismatched bones retain bind translation on both sampling paths", () =>
        {
            foreach (bool fallback in new[] {false, true})
            {
                var (rig, clip) = Fixture();
                Add(clip, "odd", 20, 20, 20);
                if (fallback) { clip.PlayInPlace = true; clip.RootMotion = new RootMotionSettings { Enabled = true, Bone = "root" }; }
                var player = new MarionettePlayer(rig);
                player.LoadClip(clip); player.Step(0);
                Near(player.WorldPose[rig.IndexOf("odd")].Origin.x, 1);
                if (player.UsesKernelSolver != fallback) throw new Exception("wrong sampling path");
            }
        });
        registry.Add("clip-space", "foreign twist offsets and animated incompatible chains cannot stretch the mesh", () =>
        {
            var (rig, clip) = Fixture();
            rig.AddBone("twist", 0, Xform.Identity);
            Add(clip, "twist", 10, 12, 10);
            Add(clip, "odd", 20, 25, 20);
            var player = new MarionettePlayer(rig); player.LoadClip(clip); player.Time = .5f; player.Step(0);
            Near(player.WorldPose[rig.IndexOf("twist")].Origin.x, 0);
            Near(player.WorldPose[rig.IndexOf("odd")].Origin.x, 1);
        });
        registry.Add("clip-space", "foreign root and intentionally animated translations are scaled and preserved", () =>
        {
            var (rig, clip) = Fixture();
            Add(clip, "root", 0, 2.54f, 5.08f);
            Add(clip, "odd", 2.54f, 5.08f, 2.54f);
            var player = new MarionettePlayer(rig);
            player.LoadClip(clip); player.Time = .5f; player.Step(0);
            Near(player.WorldPose[0].Origin.x, 1);
            Near(player.WorldPose[rig.IndexOf("odd")].Origin.x, 3);
        });
        registry.Add("clip-space", "same-unit and unknown-unit clips keep authored translations", () =>
        {
            foreach (float factor in new[] {1f, 4f})
            {
                var (rig, clip) = Fixture(factor);
                Add(clip, "odd", 20, 20, 20);
                var player = new MarionettePlayer(rig); player.LoadClip(clip); player.Step(0);
                Near(player.ClipPositionScale, 1);
                Near(player.WorldPose[rig.IndexOf("odd")].Origin.x, 20);
            }
        });
        registry.Add("clip-space", "clip authored on a weight-culled skeleton follows the clip's parents", () =>
        {
            // Mesh file: root(yawed) > clavicle > upper > lower > hand. The compiled model
            // culls the unweighted root, clavicle and lower, so the clip keys upper in s&box
            // model space (90 degrees from the rig's) and hand relative to upper.
            var rig = new Skeleton();
            rig.AddBone("root", -1, new Xform(new Vec3(0,-1,0), new Vec3(1,0,0), new Vec3(0,0,1), new Vec3(0,0,-3)));
            rig.AddBone("clavicle", 0, Xform.Translate(new Vec3(0,1,2)));
            rig.AddBone("upper", 1, Xform.Translate(new Vec3(2,0,0)));
            rig.AddBone("lower", 2, Xform.Translate(new Vec3(4,0,0)));
            rig.AddBone("hand", 3, Xform.Translate(new Vec3(4,0,0)));
            var player = new MarionettePlayer(rig);
            var bind = player.WorldPose.ToArray();
            var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
            var u = bind[2].Origin; // rig model space -> s&box model space is +90 yaw
            Key(clip, "upper", new Vec3(-u.y, u.x, u.z), (0, 0));
            // Elbow bent 90 degrees about Z at frame 30: hand moves from (8,0,0) to (4,4,0).
            Key(clip, "hand", new Vec3(8,0,0), (0, 0));
            clip.Tracks[^1].Keys.Add(new RigKey { Frame = 30, Local = new RigPose(new Vec3(4,4,0), Quat.Identity, Vec3.One) });
            player.LoadClip(clip); player.Step(0);
            if (!player.ClipSpaceNote.Contains("omits unweighted")) throw new Exception(player.ClipSpaceNote);
            for (int b = 0; b < rig.Count; b++)
                if (Dist(player.WorldPose[b].Origin, bind[b].Origin) > 1e-4f) throw new Exception($"bone {b} moved in the bind-matching first frame");
            player.Time = 1; player.Step(0);
            var expected = bind[2].TransformPoint(new Vec3(4,4,0));
            if (Dist(player.WorldPose[4].Origin, expected) > 1e-4f) throw new Exception($"hand {player.WorldPose[4].Origin} != {expected}");
            // Props are placed in s&box model space and come back through the same yaw.
            var prop = RigAnimFile.Read("{\"ReferenceProps\":[{\"Name\":\"p\",\"Position\":\"0,10,0\",\"Rotation\":\"0,0,0\",\"Scale\":1}]}");
            using var mesh = new ViewportMesh();
            using var scene = new PropScene("p", "owned.fbx", mesh, 1, true);
            var o = Vector3.Transform(Vector3.Zero, scene.WorldMatrix(prop, 0, 1, player.RigFromClipModel));
            Near(o.X, 10); Near(o.Y, 0);
        });
        registry.Add("clip-space", "prop mesh scaling leaves placement translation intact", () =>
        {
            var clip = new RigAnimFile();
            // Exercise the retained JSON parser, as the real loader does.
            clip = RigAnimFile.Read("{\"ReferenceProps\":[{\"Name\":\"prop\",\"Position\":\"25.4,50.8,76.2\",\"Rotation\":\"0,0,0\",\"Scale\":1}]}" );
            using var mesh = new ViewportMesh();
            using var prop = new PropScene("prop", "owned.fbx", mesh, .25f, true);
            var matrix = prop.WorldMatrix(clip, 0, 1 / 2.54f);
            var origin = Vector3.Transform(Vector3.Zero, matrix);
            Near(origin.X, 10); Near(origin.Y, 20); Near(origin.Z, 30);
            Near(Vector3.Distance(Vector3.Transform(Vector3.UnitX, matrix), origin), .25f);
        });
    }
    static (Skeleton, RigAnimFile) Fixture(float factor = 2.54f)
    {
        var rig = new Skeleton(); rig.AddBone("root", -1, Xform.Identity);
        var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
        for (int i = 0; i < 4; i++) { rig.AddBone("b" + i, 0, Xform.Translate(new Vec3(1,0,0))); Add(clip, "b" + i, factor, factor, factor); }
        rig.AddBone("odd", 0, Xform.Translate(new Vec3(1,0,0)));
        return (rig, clip);
    }
    static void Add(RigAnimFile clip, string name, float a, float b, float c)
    {
        var track = new RigTrack { Name = name, Kind = RigTrackKind.Bone };
        foreach (var (frame, x) in new[] {(0,a),(15,b),(30,c)}) track.Keys.Add(new RigKey { Frame = frame, Local = new RigPose(new Vec3(x,0,0), Quat.Identity, Vec3.One) });
        clip.Tracks.Add(track);
    }
    static void Key(RigAnimFile clip, string name, Vec3 p, (int frame, int unused) at)
    {
        var track = new RigTrack { Name = name, Kind = RigTrackKind.Bone };
        track.Keys.Add(new RigKey { Frame = at.frame, Local = new RigPose(p, Quat.Identity, Vec3.One) });
        clip.Tracks.Add(track);
    }
    static float Dist(Vec3 a, Vec3 b) => MathF.Sqrt((a.x-b.x)*(a.x-b.x) + (a.y-b.y)*(a.y-b.y) + (a.z-b.z)*(a.z-b.z));
    static void Near(float actual, float expected) { if (MathF.Abs(actual - expected) > .0001f) throw new Exception($"expected {expected}, got {actual}"); }
}
