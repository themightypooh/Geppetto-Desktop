using System.Numerics;
using Effigy;
using Marionette.Desktop;

return DesktopTestHost.Run( args );

internal static class BaselineTests
{
public static void Register( TestRegistry registry, TestSettings settings )
{
string work = settings.WorkDirectory;
void Check( string name, Action test ) => registry.Add( "baseline", name, test );
void Assert( bool condition, string message = "assertion failed" )
{
    if ( !condition ) throw new Exception( message );
}
void Throws( Action action )
{
    try { action(); }
    catch ( Exception ) { return; }
    throw new Exception( "expected an error, got silent success" );
}
Skeleton Rig()
{
    var rig = new Skeleton();
    rig.AddBone( "root", -1, Xform.Translate( new Vec3( 2, 3, 4 ) ) );
    rig.AddBone( "child", 0, Xform.Translate( new Vec3( 0, 2, 0 ) ) );
    return rig;
}
string ClipFile()
{
    var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
    var track = new RigTrack { Name = "root", Kind = RigTrackKind.Bone };
    track.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 2, 3, 4 ), Quat.Identity, Vec3.One ) } );
    track.Keys.Add( new RigKey { Frame = 30, Local = new RigPose( new Vec3( 12, 3, 4 ), Quat.Identity, Vec3.One ) } );
    clip.Tracks.Add( track );
    var path = Path.Combine( work, Guid.NewGuid() + ".riganim" );
    File.WriteAllText( path, RigAnimWriter.Write( clip ) );
    return path;
}

Check( "bind palette is identity", () =>
{
    var player = new MarionettePlayer( Rig() );
    foreach ( var matrix in player.GpuBones ) Assert( matrix == Matrix4x4.Identity );
} );
Check( "missing explicit clip reports an error", () => Throws( () => new MarionettePlayer( Rig(), Path.Combine( work, "missing.riganim" ) ) ) );
Check( "scrubbing a paused clip updates the pose", () =>
{
    var player = new MarionettePlayer( Rig(), ClipFile() ) { Playing = false, Time = 0.5f };
    player.Step( 0 );
    Assert( MathF.Abs( player.WorldPose[0].Origin.x - 7 ) < 1e-4f, "paused pose did not follow Time" );
} );
Check( "loading a new clip resets the playhead", () =>
{
    var player = new MarionettePlayer( Rig(), ClipFile() ) { Time = 0.7f };
    player.LoadClip( ClipFile() );
    Assert( player.Time == 0, "new clip inherited previous clip time" );
} );
Check( "bone clip matches kernel at fractional frames", () =>
{
    var player = new MarionettePlayer( Rig(), ClipFile() );
    player.Step( 0.25f );
    var expected = RigAnimFile.ModelSpace( player.Rig, player.Clip.SampleLocal( player.Rig, 7.5f ) );
    Assert( (player.WorldPose[1].Origin - expected[1].Origin).Length < 1e-4f );
} );
Check( "clip playback allocates zero bytes after warmup", () =>
{
    var player = new MarionettePlayer( Rig(), ClipFile() );
    for ( int i = 0; i < 3000; i++ ) player.Step( 1f / 60 );
    long before = GC.GetAllocatedBytesForCurrentThread();
    for ( int i = 0; i < 1000; i++ ) player.Step( 1f / 60 );
    long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
    Assert( bytes == 0, $"allocated {bytes} bytes in 1000 frames" );
} );
Check( "over-capacity skeleton is rejected instead of clamping bone IDs", () =>
{
    var rig = new Skeleton();
    for ( int b = 0; b <= MarionettePlayer.MaxGpuBones; b++ ) rig.AddBone( "bone" + b, -1, Xform.Identity );
    Throws( () => new MarionettePlayer( rig ) );
} );
Check( "singular bind transform is rejected", () =>
{
    var rig = new Skeleton();
    rig.AddBone( "broken", -1, Xform.Scale( Vec3.Zero ) );
    Throws( () => new MarionettePlayer( rig ) );
} );
Check( "OpenGL projection maps near plane to -1", () =>
{
    var camera = new OrbitCamera { Near = 1, Far = 100 };
    var p = Vector4.Transform( new Vector4( 0, 0, -1, 1 ), camera.ProjectionMatrix( 1 ) );
    Assert( MathF.Abs( p.Z / p.W + 1 ) < 1e-5f, $"near depth was {p.Z / p.W}" );
} );
Check( "screen centre ray follows camera forward", () =>
{
    var camera = new OrbitCamera();
    var (_, direction) = camera.ScreenRay( 400, 300, 800, 600 );
    Assert( Vector3.Dot( direction, camera.Forward ) > 0.9999f );
} );
Check( "green lamps are green, not orange", () =>
{
    var green = MaterialPalette.ForName( "hab_glow_green" );
    Assert( green.Eg > green.Er && green.G > green.R );
} );

Check( "one-shot clip stops on the final pose", () =>
{
    var player = new MarionettePlayer( Rig(), ClipFile() ) { Loop = false };
    player.Step( 2 );
    Assert( !player.Playing && MathF.Abs( player.WorldPose[0].Origin.x - 12 ) < 1e-4f );
} );
Check( "paused scrubbing can reach the exact last frame", () =>
{
    var player = new MarionettePlayer( Rig(), ClipFile() ) { Playing = false, Time = 1 };
    player.Step( 0 );
    Assert( MathF.Abs( player.WorldPose[0].Origin.x - 12 ) < 1e-4f );
} );
Check( "negative/NaN frame deltas are rejected", () =>
{
    var player = new MarionettePlayer( Rig() );
    Throws( () => player.Step( -1 ) ); Throws( () => player.Step( float.NaN ) );
} );
Check( "live IK matches kernel, including fractional frames", () =>
{
    var rig = new Skeleton();
    rig.AddBone( "root", -1, Xform.Identity );
    rig.AddBone( "middle", 0, Xform.Translate( new Vec3( 0, 3, 0 ) ) );
    rig.AddBone( "end", 1, Xform.Translate( new Vec3( 1, 2, 0 ) ) );
    var clip = new RigAnimFile { FrameCount = 31 };
    var track = new RigTrack { Kind = RigTrackKind.IkGoal, Name = "end" };
    track.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 2, 3, 1 ), Quat.Identity, Vec3.One ) } );
    track.Keys.Add( new RigKey { Frame = 30, Local = new RigPose( new Vec3( -1, 3, 2 ), Quat.Identity, Vec3.One ) } );
    clip.Tracks.Add( track );
    var player = new MarionettePlayer( rig ); player.LoadClip( clip );
    Assert( player.UsesKernelSolver );
    foreach ( float frame in new[] { 0f, 3.25f, 15.5f, 30f } )
    {
        player.Time = frame / 30; player.Step( 0 );
        var expected = RigAnimFile.ModelSpace( rig, clip.SampleLocal( rig, frame ) );
        Assert( (player.WorldPose[2].Origin - expected[2].Origin).Length < 1e-4f );
    }
} );
Check( "limits match kernel and allocate zero bytes", () =>
{
    var rig = Rig();
    var clip = RigAnimFile.ReadFile( ClipFile() );
    clip.Limits = new List<RigLimit> { new() { Bone = "root", Enabled = true, Weight = 0.5f } };
    clip.Tracks[0].Keys[1].Local = new RigPose( new Vec3( 12, 3, 4 ), Quat.FromAngles( 70, 80, 60 ), Vec3.One );
    var player = new MarionettePlayer( rig ); player.LoadClip( clip ); player.Playing = true;
    player.Step( 0.25f );
    var expected = RigAnimFile.ModelSpace( rig, clip.SampleLocal( rig, 7.5f ) );
    Assert( (player.WorldPose[0].X - expected[0].X).Length < 1e-4f );
    for ( int i = 0; i < 3000; i++ ) player.Step( 1f / 60 );
    long before = GC.GetAllocatedBytesForCurrentThread();
    for ( int i = 0; i < 1000; i++ ) player.Step( 1f / 60 );
    Assert( GC.GetAllocatedBytesForCurrentThread() == before );
} );
Check( "unsupported morph tracks fail explicitly", () =>
{
    var clip = new RigAnimFile(); clip.Tracks.Add( new RigTrack { Kind = RigTrackKind.Morph, Name = "blink" } );
    Throws( () => new MarionettePlayer( Rig() ).LoadClip( clip ) );
} );
Check( "GPU palette agrees with kernel skinning maths", () =>
{
    var rig = Rig(); var player = new MarionettePlayer( rig, ClipFile() ); player.Step( 0.4f );
    var positions = new[] { new Vec3( 2, 5, 4 ) };
    var weights = new SkinWeights( 1 ); weights[0] = new[] { new BoneWeight( 1, 1 ) };
    var bind = RigAnimFile.ModelSpace( rig, rig.Bones.Select( b => b.Local ).ToArray() );
    var expected = SkinBinder.Deform( positions, weights, bind, player.WorldPose.ToArray() );
    var actual = Vector3.Transform( new Vector3( 2, 5, 4 ), player.GpuBones[1] );
    Assert( Vector3.Distance( actual, new Vector3( expected[0].x, expected[0].y, expected[0].z ) ) < 1e-4f );
} );
PolyMesh Triangle()
{
    var mesh = new PolyMesh();
    mesh.Positions.AddRange( new[] { new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ) } );
    mesh.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
    return mesh;
}
Check( "body picking ignores empty space inside an AABB", () =>
{
    var picker = new MeshPicker(); picker.Add( Triangle() );
    Assert( picker.Pick( new Vector3( 0.9f, 0.9f, 2 ), -Vector3.UnitZ, default ) == -1 );
    Assert( picker.Pick( new Vector3( 0.1f, 0.1f, 2 ), -Vector3.UnitZ, default ) == 0 );
} );
Check( "body picking follows animated geometry", () =>
{
    var mesh = Triangle(); mesh.Skin = new SkinWeights( 3 );
    for ( int v = 0; v < 3; v++ ) mesh.Skin[v] = new[] { new BoneWeight( 0, 1 ) };
    var picker = new MeshPicker(); picker.Add( mesh );
    var palette = new[] { Matrix4x4.CreateTranslation( 10, 0, 0 ) };
    Assert( picker.Pick( new Vector3( 10.1f, 0.1f, 2 ), -Vector3.UnitZ, palette ) == 0 );
    Assert( picker.Pick( new Vector3( 0.1f, 0.1f, 2 ), -Vector3.UnitZ, palette ) == -1 );
} );
Check( "FBX missing weights do not silently rebind", () =>
{
    var imported = new FbxImport.Result { Skeleton = Rig() };
    imported.Pieces.Add( new ObjReader.ObjPiece( "broken", Triangle() ) );
    imported.Weights.Add( null );
    Throws( () => ViewportMesh.FromFbx( imported, 0, "test.fbx" ) );
} );
Check( "CLI rejects unknown flags, missing values and invalid sizes", () =>
{
    foreach ( var args in new[] { new[] { "--gen-clips" }, new[] { "--model" }, new[] { "--size", "0x600" }, new[] { "--frames", "oops" }, new[] { "--time", "NaN" } } )
        Throws( () => DesktopOptions.Parse( args ) );
    Assert( !DesktopOptions.Parse( Array.Empty<string>() ).Animate );
} );
Check( "headless/exposure options parse without enabling animation", () =>
{
    var options = DesktopOptions.Parse( new[] { "--headless", "--exposure", "1.2", "--frames", "1" } );
    Assert( options.Headless && options.Exposure == 1.2f && !options.Animate );
    Throws( () => DesktopOptions.Parse( new[] { "--exposure", "0" } ) );
    Throws( () => DesktopOptions.Parse( new[] { "--exposure", "NaN" } ) );
} );

string surveyPath = settings.SurveyPath;
string? SurveyMissing()
{
    var missing = new[] { surveyPath, Path.Combine( Path.GetDirectoryName( Path.GetFullPath( surveyPath ) )!, "player_head.fbx" ) }
        .Where( path => !File.Exists( path ) ).ToArray();
    return missing.Length == 0 ? null : "Survey fixture missing: " + string.Join( ", ", missing );
}
    registry.Add( "survey", "Survey assembly uses authored head and collar, not placeholder", () =>
    {
        var imported = SurveyPlayerAssembly.Load( surveyPath );
        Assert( imported.Pieces.Any( p => p.Name == "face_head" ) );
        Assert( imported.Pieces.Any( p => p.Name == "neck_ring" ) );
        Assert( imported.Pieces.All( p => p.Name != "suit_head" ) );
        var mesh = ViewportMesh.FromFbx( imported, 0, surveyPath );
        Assert( mesh.HasSkin && mesh.Rig.Count == 94 && mesh.BodyPicks.Length == 23 );
    }, SurveyMissing, settings.RequireLocalFixtures );
    registry.Add( "survey", "background loader publishes a complete result", () =>
    {
        var loader = new DocumentLoader(); loader.Start( surveyPath );
        Assert( loader.Completion.Wait( TimeSpan.FromSeconds( 30 ) ) );
        Assert( loader.Status == DocumentLoader.State.Ready, loader.Error );
        Assert( loader.Mesh is not null && loader.TotalSeconds >= loader.LoadSeconds + loader.ExtractSeconds );
    }, SurveyMissing, settings.RequireLocalFixtures );
Check( "background load errors are published and recoverable", () =>
{
    var loader = new DocumentLoader(); loader.Start( Path.Combine( work, "missing.fbx" ) );
    loader.Completion.Wait();
    Assert( loader.Status == DocumentLoader.State.Failed && loader.Error is not null && loader.Mesh is null );
} );

}
}
