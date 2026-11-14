#nullable enable
using System;
using System.Collections.Generic;
using Effigy;

/// <summary>
/// Alembic cache + FPS time-mapping tests (godlike-usd-stage-alembic-materialx):
/// per-frame baked geometry survives the cache round-trip, time mapping follows
/// the scene frame rate (never a hardcoded 24 fps), and the .abc binary codec
/// refuses honestly until the native backend lands.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is coordinator-owned;
/// the final integrator adds the one AlembicCacheTests.Register(registry, settings);
/// line, as with KernelParityTests.
/// </summary>
internal static class AlembicCacheTests
{
    static PolyMesh Quad(float xOffset)
    {
        var m = new PolyMesh();
        m.AddVertex(new Vec3(xOffset + 0f, 0f, 0f));
        m.AddVertex(new Vec3(xOffset + 1f, 0f, 0f));
        m.AddVertex(new Vec3(xOffset + 1f, 1f, 0f));
        m.AddVertex(new Vec3(xOffset + 0f, 1f, 0f));
        m.AddFace(new[] { 0, 1, 2, 3 });
        return m;
    }

    public static void Register(TestRegistry registry, TestSettings settings)
    {
        void Check(string name, Action test) => registry.Add("alembic-cache", name, test);
        void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message = "assertion failed")
        {
            if (!condition) throw new Exception(message);
        }
        void Near(double actual, double expected, double tolerance, string what)
        {
            if (Math.Abs(actual - expected) > tolerance)
                throw new Exception($"{what}: expected {expected}, got {actual}");
        }

        Check("time mapping follows the scene fps, never a hardcoded 24", () =>
        {
            var at24 = new AlembicTimeSampling(24);
            var at30 = new AlembicTimeSampling(30);
            var at25 = new AlembicTimeSampling(25);
            var at60 = new AlembicTimeSampling(60);
            Near(at24.SecondsAtFrame(48), 2.0, 1e-9, "frame 48 at 24 fps");
            Near(at30.SecondsAtFrame(48), 1.6, 1e-9, "frame 48 at 30 fps is 1.6 s, not 2.0 s");
            Near(at25.SecondsAtFrame(50), 2.0, 1e-9, "frame 50 at 25 fps");
            Near(at60.SecondsAtFrame(120), 2.0, 1e-9, "frame 120 at 60 fps");
            Require(at24.FrameAtSeconds(2.0) == 48, "2.0 s at 24 fps must be frame 48");
            Require(at30.FrameAtSeconds(2.0) == 60, "2.0 s at 30 fps must be frame 60");
            var times = at25.SampleTimes(0, 3);
            Near(times[0], 0.0, 1e-9, "sample 0");
            Near(times[1], 0.04, 1e-9, "sample 1 at 25 fps");
            Near(times[2], 0.08, 1e-9, "sample 2 at 25 fps");
            var threw = false;
            try { new AlembicTimeSampling(0); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            Require(threw, "fps 0 must be refused, not silently defaulted");
        });

        Check("per-frame baked geometry survives the cache round-trip", () =>
        {
            var cache = new AlembicCache(new AlembicTimeSampling(30));
            cache.AddFrame(0, Quad(0f));
            cache.AddFrame(1, Quad(1f));
            cache.AddFrame(2, Quad(2f));
            Require(cache.Validate().Count == 0, "clean cache must validate, got: " + string.Join("; ", cache.Validate()));
            var atStart = cache.SampleAtTime(0.0);
            Require(atStart is not null && atStart.Frame == 0, "time 0 must sample frame 0");
            var atMid = cache.SampleAtTime(0.033);
            Require(atMid is not null && atMid.Frame == 1, "time 0.033 s at 30 fps must sample frame 1, got " + atMid?.Frame);
            var json = cache.ToJson();
            Require(json.Contains("geppetto-alembic-cache/1", StringComparison.Ordinal), "interchange must name its format");
            var back = AlembicCache.FromJson(json);
            Near(back.TimeSampling.FramesPerSecond, 30.0, 1e-9, "fps after round-trip");
            Require(back.Frames.Count == 3, "frame count must survive, got " + back.Frames.Count);
            Require(back.Frames[2].Frame == 2, "frame numbers must survive");
            Near(back.Frames[2].Mesh.Positions[1].x, 3f, 1e-4f, "frame 2 vertex must survive (quad moved +2 in x)");
            Near(back.Frames[0].Mesh.Positions[1].x, 1f, 1e-4f, "frame 0 vertex must survive");
            Require(back.Validate().Count == 0, "round-tripped cache must validate");
        });

        Check("topology drift across frames is reported, not merged", () =>
        {
            var cache = new AlembicCache(new AlembicTimeSampling(24));
            cache.AddFrame(0, Quad(0f));
            var triangle = new PolyMesh();
            triangle.AddVertex(new Vec3(0f, 0f, 0f));
            triangle.AddVertex(new Vec3(1f, 0f, 0f));
            triangle.AddVertex(new Vec3(1f, 1f, 0f));
            triangle.AddFace(new[] { 0, 1, 2 });
            cache.AddFrame(1, triangle);
            var issues = cache.Validate();
            Require(issues.Count == 1 && issues[0].Contains("topology drift", StringComparison.Ordinal),
                "a frame with different topology must be one named issue, got: " + string.Join("; ", issues));
        });

        Check("duplicate frame is refused and the mesh is cloned on add", () =>
        {
            var cache = new AlembicCache(new AlembicTimeSampling(30));
            var mesh = Quad(0f);
            cache.AddFrame(0, mesh);
            var threw = false;
            try { cache.AddFrame(0, Quad(5f)); }
            catch (ArgumentException) { threw = true; }
            Require(threw, "a second sample for frame 0 must be refused");
            mesh.Positions[1] = new Vec3(99f, 99f, 99f);
            Near(cache.Frames[0].Mesh.Positions[1].x, 1f, 1e-4f, "editing the caller's mesh after AddFrame must not rewrite the cached frame");
        });

        Check(".abc binary codec refuses honestly until the native backend lands", () =>
        {
            var codec = AlembicAbcCodec.Unavailable;
            Require(codec.LastRoute.Contains("unavailable", StringComparison.Ordinal), "LastRoute must say unavailable");
            Require(codec.LastRoute.Contains("package-universal-desktop-cli", StringComparison.Ordinal), "LastRoute must name the owning packaging task");
            var readThrew = false;
            try { codec.ReadAbc(new byte[] { 1, 2, 3 }); }
            catch (NotSupportedException e) { readThrew = e.Message.Contains("package-universal-desktop-cli", StringComparison.Ordinal); }
            Require(readThrew, "ReadAbc must throw naming the packaging task");
            var writeThrew = false;
            try { codec.WriteAbc(new AlembicCache(new AlembicTimeSampling(24))); }
            catch (NotSupportedException e) { writeThrew = e.Message.Contains(".abc", StringComparison.Ordinal); }
            Require(writeThrew, "WriteAbc must throw naming .abc");
        });
    }
}
