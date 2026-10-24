#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;

internal static class PoseLibraryTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "pose-library", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		bool PoseNear( RigPose a, RigPose b, float eps = 1e-4f ) =>
			MathF.Abs( a.Position.x - b.Position.x ) < eps && MathF.Abs( a.Position.y - b.Position.y ) < eps && MathF.Abs( a.Position.z - b.Position.z ) < eps
			&& MathF.Abs( a.Rotation.x - b.Rotation.x ) < eps && MathF.Abs( a.Rotation.y - b.Rotation.y ) < eps && MathF.Abs( a.Rotation.z - b.Rotation.z ) < eps && MathF.Abs( a.Rotation.w - b.Rotation.w ) < eps
			&& MathF.Abs( a.Scale.x - b.Scale.x ) < eps && MathF.Abs( a.Scale.y - b.Scale.y ) < eps && MathF.Abs( a.Scale.z - b.Scale.z ) < eps;

		RigPose Pose( float x, float y, float z ) => new( new Vec3( x, y, z ), Quat.FromAngles( 10, 20, 30 ), new Vec3( 1, 2, 3 ) );

		string TempDir()
		{
			var dir = Path.Combine( Path.GetTempPath(), "poselib-" + Guid.NewGuid().ToString( "N" ) );
			Directory.CreateDirectory( dir );
			return dir;
		}

		Check( "save/load round-trips a pose exactly", () =>
		{
			var dir = TempDir();
			var path = Path.Combine( dir, "lib.poselib.json" );
			var lib = new PoseLibrary();
			lib.SetPose( "wave", new Dictionary<string, RigPose> { ["arm_L"] = Pose( 1, 2, 3 ), ["arm_R"] = Pose( -1, 0, 4 ) } );
			lib.Save( path );
			var back = PoseLibrary.Load( path );
			var pose = back.GetPose( "wave" );
			Require( pose is not null && pose.Count == 2, "expected 2 bones after round-trip" );
			Require( PoseNear( pose["arm_L"], Pose( 1, 2, 3 ) ), "arm_L did not round-trip" );
			Require( PoseNear( pose["arm_R"], Pose( -1, 0, 4 ) ), "arm_R did not round-trip" );
		} );

		Check( "adding a second pose keeps the first", () =>
		{
			var dir = TempDir();
			var path = Path.Combine( dir, "lib.poselib.json" );
			var lib = new PoseLibrary();
			lib.SetPose( "a", new Dictionary<string, RigPose> { ["root"] = RigPose.Identity } );
			lib.Save( path );
			var lib2 = PoseLibrary.Load( path );
			lib2.SetPose( "b", new Dictionary<string, RigPose> { ["root"] = Pose( 5, 5, 5 ) } );
			lib2.Save( path );
			var back = PoseLibrary.Load( path );
			Require( back.HasPose( "a" ) && back.HasPose( "b" ), "both poses must survive" );
			Require( PoseNear( back.GetPose( "a" )!["root"], RigPose.Identity ), "first pose must be untouched" );
		} );

		Check( "pose names are case-insensitive", () =>
		{
			var lib = new PoseLibrary();
			lib.SetPose( "Wave", new Dictionary<string, RigPose> { ["root"] = RigPose.Identity } );
			Require( lib.GetPose( "WAVE" ) is not null, "lookup must ignore case" );
			Require( lib.Names().SequenceEqual( new[] { "Wave" } ), "Names must list the stored spelling once" );
		} );

		Check( "sidecar path mirrors the facerig convention", () =>
		{
			var path = PoseLibrary.SidecarPath( "/tmp/models/hero.effigy" );
			Require( path == "/tmp/models/hero.poselib.json", $"unexpected sidecar path '{path}'" );
		} );

		Check( "missing pose returns null, missing file throws", () =>
		{
			var lib = new PoseLibrary();
			Require( lib.GetPose( "nope" ) is null, "unknown pose must be null, not an exception" );
			var threw = false;
			try { PoseLibrary.Load( Path.Combine( TempDir(), "nope.poselib.json" ) ); } catch ( FileNotFoundException ) { threw = true; }
			Require( threw, "loading a missing file must throw FileNotFoundException" );
		} );

		Check( "malformed library fails loudly", () =>
		{
			var dir = TempDir();
			var path = Path.Combine( dir, "bad.poselib.json" );
			File.WriteAllText( path, "{\"version\":1,\"poses\":{\"x\":{\"bones\":{\"root\":{\"pos\":[1,2],\"rot\":[0,0,0,1],\"scale\":[1,1,1]}}}}}" );
			var threw = false;
			try { PoseLibrary.Load( path ); } catch ( InvalidDataException ) { threw = true; }
			Require( threw, "a pos array of length 2 must fail loudly" );
			File.WriteAllText( path, "this is not json" );
			threw = false;
			try { PoseLibrary.Load( path ); } catch ( InvalidDataException ) { threw = true; }
			Require( threw, "non-JSON must fail loudly as InvalidDataException" );
		} );

		Check( "zero-length rotation loads as identity", () =>
		{
			var dir = TempDir();
			var path = Path.Combine( dir, "zero.poselib.json" );
			File.WriteAllText( path, "{\"version\":1,\"poses\":{\"x\":{\"bones\":{\"root\":{\"pos\":[0,0,0],\"rot\":[0,0,0,0],\"scale\":[1,1,1]}}}}}" );
			var pose = PoseLibrary.Load( path ).GetPose( "x" )!;
			Require( PoseNear( pose["root"], RigPose.Identity ), "zero rotation must normalise to Identity" );
		} );

		RigAnimFile ClipWithKeys()
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 11 };
			var track = new RigTrack { Kind = RigTrackKind.Bone, Name = "arm_L" };
			track.Keys.Add( new RigKey { Frame = 0, Local = new RigPose( new Vec3( 0, 0, 0 ), Quat.Identity, Vec3.One ), Ease = RigEase.Linear } );
			track.Keys.Add( new RigKey { Frame = 10, Local = new RigPose( new Vec3( 10, 0, 0 ), Quat.Identity, Vec3.One ), Ease = RigEase.Linear } );
			clip.Tracks.Add( track );
			return clip;
		}

		Check( "capture reads evaluated bone tracks at a frame", () =>
		{
			var pose = PoseLibrary.Capture( ClipWithKeys(), 5 );
			Require( pose.Count == 1 && MathF.Abs( pose["arm_L"].Position.x - 5f ) < 1e-3f,
				$"linear capture at frame 5 must be x=5, got {pose["arm_L"].Position.x}" );
		} );

		Check( "apply weight 1 keys the pose, weight 0.5 blends from current", () =>
		{
			var clip = ClipWithKeys();
			var applied = PoseLibrary.Apply( clip, new Dictionary<string, RigPose> { ["arm_L"] = new RigPose( new Vec3( 20, 0, 0 ), Quat.Identity, Vec3.One ) }, 10, 0.5f );
			Require( applied == 1, "exactly one bone must be keyed" );
			Require( MathF.Abs( clip.Tracks[0].Evaluate( 10 ).Position.x - 15f ) < 1e-3f,
				$"half blend from x=10 toward x=20 must be 15, got {clip.Tracks[0].Evaluate( 10 ).Position.x}" );
			PoseLibrary.Apply( clip, new Dictionary<string, RigPose> { ["leg_L"] = new RigPose( new Vec3( 3, 3, 3 ), Quat.Identity, Vec3.One ) }, 0, 1f );
			Require( clip.Tracks.Any( t => t.Name == "leg_L" ), "apply must create a track for a bone with none" );
		} );

		Check( "apply mask keys only masked bones", () =>
		{
			var clip = new RigAnimFile { FrameRate = 30 };
			var pose = new Dictionary<string, RigPose> { ["arm_L"] = Pose( 1, 0, 0 ), ["leg_L"] = Pose( 0, 1, 0 ) };
			var applied = PoseLibrary.Apply( clip, pose, 0, 1f, PoseLibrary.ParseMask( "arm_L" ) );
			Require( applied == 1 && clip.Tracks.Count == 1 && clip.Tracks[0].Name == "arm_L", "mask must key only arm_L" );
		} );

		Check( "glob mask matches a bone family", () =>
		{
			Require( PoseLibrary.MatchesMask( "arm_L", PoseLibrary.ParseMask( "arm_*" ) ), "arm_* must match arm_L" );
			Require( !PoseLibrary.MatchesMask( "leg_L", PoseLibrary.ParseMask( "arm_*" ) ), "arm_* must not match leg_L" );
			Require( PoseLibrary.MatchesMask( "arm_L", null ), "no mask must match everything" );
		} );

		Check( "blend endpoints and midpoint", () =>
		{
			var a = new Dictionary<string, RigPose> { ["root"] = new RigPose( new Vec3( 0, 0, 0 ), Quat.Identity, Vec3.One ) };
			var b = new Dictionary<string, RigPose> { ["root"] = new RigPose( new Vec3( 10, 0, 0 ), Quat.Identity, Vec3.One ) };
			Require( PoseNear( PoseLibrary.Blend( a, b, 0f )["root"], a["root"] ), "weight 0 must be pose a" );
			Require( PoseNear( PoseLibrary.Blend( a, b, 1f )["root"], b["root"] ), "weight 1 must be pose b" );
			Require( MathF.Abs( PoseLibrary.Blend( a, b, 0.5f )["root"].Position.x - 5f ) < 1e-4f, "weight 0.5 must be the midpoint" );
			Require( MathF.Abs( PoseLibrary.Blend( a, b, 2f )["root"].Position.x - 10f ) < 1e-4f, "weight above 1 must clamp to 1" );
		} );

		Check( "blend mask keeps unmasked bones from a and drops b-only unmasked bones", () =>
		{
			var a = new Dictionary<string, RigPose> { ["arm_L"] = new RigPose( new Vec3( 0, 0, 0 ), Quat.Identity, Vec3.One ), ["leg_L"] = new RigPose( new Vec3( 1, 0, 0 ), Quat.Identity, Vec3.One ) };
			var b = new Dictionary<string, RigPose> { ["arm_L"] = new RigPose( new Vec3( 10, 0, 0 ), Quat.Identity, Vec3.One ), ["head"] = new RigPose( new Vec3( 7, 0, 0 ), Quat.Identity, Vec3.One ) };
			var blended = PoseLibrary.Blend( a, b, 1f, PoseLibrary.ParseMask( "arm_L" ) );
			Require( MathF.Abs( blended["arm_L"].Position.x - 10f ) < 1e-4f, "masked arm must blend" );
			Require( MathF.Abs( blended["leg_L"].Position.x - 1f ) < 1e-4f, "unmasked leg must stay at a" );
			Require( !blended.ContainsKey( "head" ), "b-only unmasked bone must not be carried over" );
		} );

		Check( "blend against identity for a b-only masked bone", () =>
		{
			var a = new Dictionary<string, RigPose>();
			var b = new Dictionary<string, RigPose> { ["head"] = new RigPose( new Vec3( 8, 0, 0 ), Quat.Identity, Vec3.One ) };
			var blended = PoseLibrary.Blend( a, b, 0.5f );
			Require( MathF.Abs( blended["head"].Position.x - 4f ) < 1e-4f, "b-only bone at 0.5 must be halfway from Identity" );
		} );

		Skeleton TwoBoneSkeleton()
		{
			var skeleton = new Skeleton();
			skeleton.AddBone( "root", -1, Xform.Identity, 5f );
			skeleton.AddBone( "arm_L", 0, Xform.Identity, 4f );
			return skeleton;
		}

		Check( "verbs: pose_preset -> pose_save -> pose_apply round-trips through a file", () =>
		{
			var dir = TempDir();
			var path = Path.Combine( dir, "verbs.poselib.json" );
			var skeleton = TwoBoneSkeleton();
			var save = BuildScriptAnim.Run( $"pose_preset name=wave bone=arm_L pos=1,2,3 rot=0,0,0\npose_save name=wave preset=wave path={path}", skeleton );
			Require( save.Ok, $"pose_save script failed: {save.Error}" );
			Require( File.Exists( path ), "pose_save must write the library file" );
			var apply = BuildScriptAnim.Run( $"clip name=c fps=30 frames=10\npose_apply name=wave path={path} frame=0", skeleton );
			Require( apply.Ok, $"pose_apply script failed: {apply.Error}" );
			var track = apply.Clips["c"].Tracks.FirstOrDefault( t => t.Name == "arm_L" );
			Require( track is not null && track.Keys.Count == 1, "pose_apply must key arm_L once" );
			Require( MathF.Abs( track!.Evaluate( 0 ).Position.x - 1f ) < 1e-4f, "applied key must carry the saved position" );
		} );

		Check( "verbs: pose_save captures the current clip and pose_blend writes a new pose", () =>
		{
			var dir = TempDir();
			var path = Path.Combine( dir, "blend.poselib.json" );
			var skeleton = TwoBoneSkeleton();
			var script =
				"clip name=c fps=30 frames=10\n" +
				"key bone=arm_L frame=0 pos=0,0,0\n" +
				"pose_save name=rest path=" + path + " frame=0\n" +
				"pose_preset name=high bone=arm_L pos=10,0,0\n" +
				"pose_save name=high preset=high path=" + path + "\n" +
				"pose_blend a=rest b=high out=mid weight=0.5 path=" + path;
			var result = BuildScriptAnim.Run( script, skeleton );
			Require( result.Ok, $"blend script failed: {result.Error}" );
			var mid = PoseLibrary.Load( path ).GetPose( "mid" );
			Require( mid is not null && MathF.Abs( mid["arm_L"].Position.x - 5f ) < 1e-3f,
				$"blended pose must be x=5, got {( mid is null ? "null" : mid["arm_L"].Position.x.ToString() )}" );
		} );

		Check( "verbs: unknown pose and missing clip fail loudly", () =>
		{
			var dir = TempDir();
			var path = Path.Combine( dir, "empty.poselib.json" );
			new PoseLibrary().SetPose( "only", new Dictionary<string, RigPose> { ["root"] = RigPose.Identity } );
			// write a library with one pose via a save, then apply a missing name
			var skeleton = TwoBoneSkeleton();
			var saved = BuildScriptAnim.Run( "pose_preset name=only bone=root pos=0,0,0\npose_save name=only preset=only path=" + path, skeleton );
			Require( saved.Ok, $"setup save failed: {saved.Error}" );
			var bad = BuildScriptAnim.Run( "clip name=c\npose_apply name=nope path=" + path, skeleton );
			Require( !bad.Ok && bad.Error is not null && bad.Error.Contains( "nope" ), "applying an unknown pose must fail with its name" );
			var nocclip = BuildScriptAnim.Run( "pose_apply name=only path=" + path, skeleton );
			Require( !nocclip.Ok, "pose_apply with no clip selected must fail" );
		} );
	}
}
