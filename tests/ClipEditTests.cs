using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Effigy;
using Marionette.Desktop;

internal static class ClipEditTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "clip-edit", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"Expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"Expected {typeof(T).Name}, got silent success" );
		}

		Skeleton MakeRig()
		{
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Translate( new Vec3( 0, 0, 0 ) ) );
			rig.AddBone( "arm_L", 0, Xform.Translate( new Vec3( 5, 0, 0 ) ) );
			return rig;
		}

		DocumentSession MakeSessionWithClip( int frameCount = 31, int frameRate = 30 )
		{
			var clip = new RigAnimFile { FrameRate = frameRate, FrameCount = frameCount };
			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { Name = "box" } );
			studio.Rebuild();
			return DocumentSession.CreateEffigy( "anim.effigy", studio, clip: clip );
		}

		Check( "first last only key insertion and deletion", () =>
		{
			var session = MakeSessionWithClip( 100 );
			var clip = session.Clip!;

			// 1. Only key
			var pose10 = new RigPose( new Vec3( 0, 10, 0 ), Quat.Identity, Vec3.One );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 10, pose10 );
			var track = clip.Tracks.First( t => t.Kind == RigTrackKind.Bone && t.Name == "root" );
			Require( track.Keys.Count == 1 && track.Keys[0].Frame == 10 );

			// 2. First key (before existing)
			var pose0 = new RigPose( new Vec3( 0, 0, 0 ), Quat.Identity, Vec3.One );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 0, pose0 );
			Require( track.Keys.Count == 2 && track.Keys[0].Frame == 0 && track.Keys[1].Frame == 10 );

			// 3. Last key (after existing)
			var pose50 = new RigPose( new Vec3( 0, 50, 0 ), Quat.Identity, Vec3.One );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 50, pose50 );
			Require( track.Keys.Count == 3 );
			Require( track.Keys[0].Frame == 0 && track.Keys[1].Frame == 10 && track.Keys[2].Frame == 50 );

			// Delete middle
			ClipEditService.DeleteKey( session, RigTrackKind.Bone, "root", 10 );
			Require( track.Keys.Count == 2 && track.Keys[0].Frame == 0 && track.Keys[1].Frame == 50 );

			// Delete last
			ClipEditService.DeleteKey( session, RigTrackKind.Bone, "root", 50 );
			Require( track.Keys.Count == 1 && track.Keys[0].Frame == 0 );

			// Delete only
			ClipEditService.DeleteKey( session, RigTrackKind.Bone, "root", 0 );
			Require( track.Keys.Count == 0 );

			// Undo all deletions
			session.History.Undo(); // restores 0
			Require( track.Keys.Count == 1 && track.Keys[0].Frame == 0 );

			session.History.Undo(); // restores 50
			Require( track.Keys.Count == 2 && track.Keys[0].Frame == 0 && track.Keys[1].Frame == 50 );

			session.History.Undo(); // restores 10
			Require( track.Keys.Count == 3 && track.Keys[0].Frame == 0 && track.Keys[1].Frame == 10 && track.Keys[2].Frame == 50 );
		} );

		Check( "every ease mode is preserved", () =>
		{
			var session = MakeSessionWithClip( 50 );
			var easeModes = (RigEase[])Enum.GetValues( typeof(RigEase) );

			for ( int i = 0; i < easeModes.Length; i++ )
			{
				var ease = easeModes[i];
				var pose = new RigPose( new Vec3( i, 0, 0 ), Quat.Identity, Vec3.One );
				ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", i * 5, pose, ease: ease );
			}

			var track = session.Clip!.Tracks.First( t => t.Name == "root" );
			Require( track.Keys.Count == easeModes.Length );

			for ( int i = 0; i < easeModes.Length; i++ )
			{
				Require( track.Keys[i].Ease == easeModes[i], $"expected ease {easeModes[i]}, got {track.Keys[i].Ease}" );
			}

			// Undo all and redo all
			for ( int i = 0; i < easeModes.Length; i++ ) session.History.Undo();
			Require( track.Keys.Count == 0 );

			for ( int i = 0; i < easeModes.Length; i++ ) session.History.Redo();
			Require( track.Keys.Count == easeModes.Length );
			for ( int i = 0; i < easeModes.Length; i++ )
			{
				Require( track.Keys[i].Ease == easeModes[i] );
			}
		} );

		Check( "tangents round trip cleanly", () =>
		{
			var session = MakeSessionWithClip( 30 );
			float[] tangentIn = Enumerable.Range( 0, RigHermite.Channels ).Select( i => (float)i * 1.5f ).ToArray();
			float[] tangentOut = Enumerable.Range( 0, RigHermite.Channels ).Select( i => (float)i * -2.0f ).ToArray();

			var pose = new RigPose( new Vec3( 1, 2, 3 ), Quat.Identity, Vec3.One );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 12, pose,
				ease: RigEase.Cubic, tangentIn: tangentIn, tangentOut: tangentOut );

			var track = session.Clip!.Tracks.First( t => t.Name == "root" );
			var key = track.Keys[0];
			Require( key.TangentIn != null && key.TangentOut != null );
			Require( key.TangentIn!.SequenceEqual( tangentIn ) );
			Require( key.TangentOut!.SequenceEqual( tangentOut ) );

			// Mutate external arrays: must not affect stored tangents
			tangentIn[0] = 999f;
			Require( key.TangentIn![0] != 999f, "stored tangents must be independent clones" );

			// Undo and redo
			session.History.Undo();
			Require( track.Keys.Count == 0 );
			session.History.Redo();
			Require( track.Keys[0].TangentIn!.SequenceEqual( Enumerable.Range( 0, RigHermite.Channels ).Select( i => (float)i * 1.5f ) ) );
		} );

		Check( "occupied frame move replaces target and undo restores both", () =>
		{
			var session = MakeSessionWithClip( 40 );
			var pose5 = new RigPose( new Vec3( 5, 0, 0 ), Quat.Identity, Vec3.One );
			var pose15 = new RigPose( new Vec3( 15, 0, 0 ), Quat.Identity, Vec3.One );

			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 5, pose5, ease: RigEase.Linear );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 15, pose15, ease: RigEase.Smooth );

			var track = session.Clip!.Tracks.First( t => t.Name == "root" );
			Require( track.Keys.Count == 2 );

			// Move key from frame 5 to occupied frame 15
			ClipEditService.MoveKey( session, RigTrackKind.Bone, "root", 5, 15 );
			Require( track.Keys.Count == 1, "moving onto occupied frame replaces the displaced key" );
			Require( track.Keys[0].Frame == 15 );
			Require( track.Keys[0].Local.Position.x == 5f, "moved key pose must be at frame 15" );
			Require( track.Keys[0].Ease == RigEase.Linear, "moved key ease must be preserved" );

			// Undo move
			session.History.Undo();
			Require( track.Keys.Count == 2 );
			Require( track.Keys[0].Frame == 5 && track.Keys[0].Local.Position.x == 5f );
			Require( track.Keys[1].Frame == 15 && track.Keys[1].Local.Position.x == 15f );
			Require( track.Keys[1].Ease == RigEase.Smooth, "displaced key ease must be restored" );

			// Redo move
			session.History.Redo();
			Require( track.Keys.Count == 1 && track.Keys[0].Frame == 15 && track.Keys[0].Local.Position.x == 5f );
		} );

		Check( "end frame cache update and playhead preservation", () =>
		{
			var rig = MakeRig();
			var session = MakeSessionWithClip( frameCount: 10, frameRate: 30 );
			var player = new MarionettePlayer( rig );
			player.LoadClip( session.Clip! );

			// Add initial key at frame 8
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 8, RigPose.Identity, player: player );
			Require( player.Clip!.LastKeyFrame == 8 );

			// Player paused at time 0.2s (frame 6)
			player.Playing = false;
			player.Time = 0.2f;
			player.Step( 0 );

			// Extend clip by adding key at frame 50
			var farPose = new RigPose( new Vec3( 100, 0, 0 ), Quat.Identity, Vec3.One );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 50, farPose, player: player );

			Require( session.Clip!.FrameCount == 51, "clip FrameCount must extend to cover frame 50" );
			Require( session.Clip.LastFrame == 50, "last frame must be updated to 50" );

			// Cache re-evaluation must preserve playhead and pause state
			Require( !player.Playing, "playback state must be preserved" );
			Require( MathF.Abs( player.Time - 0.2f ) < 1e-4f, "playhead time must be preserved" );

			// Undo: restores FrameCount and re-evaluates player cache
			session.History.Undo();
			Require( session.Clip.FrameCount == 10 );
			Require( session.Clip.LastFrame <= 9 );
			Require( !player.Playing );
			Require( MathF.Abs( player.Time - 0.2f ) < 1e-4f );
		} );

		Check( "paused last frame scrub", () =>
		{
			var rig = MakeRig();
			var session = MakeSessionWithClip( frameCount: 31, frameRate: 30 );
			var player = new MarionettePlayer( rig );
			player.LoadClip( session.Clip! );

			var key0 = new RigPose( new Vec3( 0, 0, 0 ), Quat.Identity, Vec3.One );
			var key30 = new RigPose( new Vec3( 30, 0, 0 ), Quat.Identity, Vec3.One );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 0, key0, player: player );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 30, key30, player: player );

			player.Playing = false;
			// Scrub to exact last frame (frame 30 = 1.0s at 30 fps)
			player.Time = 1.0f;
			player.Step( 0 );
			Require( MathF.Abs( player.WorldPose[0].Origin.x - 30f ) < 1e-3f, "pose must evaluate to frame 30" );

			// Edit key at frame 30
			var updatedKey30 = new RigPose( new Vec3( 75, 0, 0 ), Quat.Identity, Vec3.One );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 30, updatedKey30, player: player );

			// Playhead was preserved and pose immediately refreshed to updated key
			Require( MathF.Abs( player.WorldPose[0].Origin.x - 75f ) < 1e-3f, "invalidated player pose must reflect edited key" );
		} );

		Check( "negative and NaN coordinates are rejected", () =>
		{
			var session = MakeSessionWithClip( 30 );

			Throws<ArgumentOutOfRangeException>( () =>
				ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", -1, RigPose.Identity ) );

			Throws<ArgumentException>( () =>
				ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 5,
					new RigPose( new Vec3( float.NaN, 0, 0 ), Quat.Identity, Vec3.One ) ) );

			Throws<ArgumentException>( () =>
				ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 5,
					new RigPose( Vec3.Zero, new Quat( 0, float.PositiveInfinity, 0, 1 ), Vec3.One ) ) );

			Throws<ArgumentException>( () =>
				ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 5,
					new RigPose( Vec3.Zero, Quat.Identity, new Vec3( 1, float.NaN, 1 ) ) ) );

			Throws<ArgumentOutOfRangeException>( () =>
				ClipEditService.MoveKey( session, RigTrackKind.Bone, "root", 5, -2 ) );

			Throws<ArgumentOutOfRangeException>( () =>
				ClipEditService.DeleteKey( session, RigTrackKind.Bone, "root", -5 ) );
		} );

		Check( "unlike track kinds with the same target name remain distinct", () =>
		{
			var session = MakeSessionWithClip( 30 );
			var clip = session.Clip!;

			// Insert on Bone track "arm_L"
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "arm_L", 0,
				new RigPose( new Vec3( 1, 0, 0 ), Quat.Identity, Vec3.One ) );

			// Insert on IkGoal track "arm_L"
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.IkGoal, "arm_L", 0,
				new RigPose( new Vec3( 10, 0, 0 ), Quat.Identity, Vec3.One ) );

			// Insert on IkWeight track "arm_L"
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.IkWeight, "arm_L", 0,
				new RigPose( Vec3.Zero, Quat.Identity, new Vec3( 0.5f, 0.5f, 0.5f ) ) );

			Require( clip.Tracks.Count == 3, "three distinct track kinds must exist" );
			var boneTrack = clip.Tracks.First( t => t.Kind == RigTrackKind.Bone && t.Name == "arm_L" );
			var goalTrack = clip.Tracks.First( t => t.Kind == RigTrackKind.IkGoal && t.Name == "arm_L" );
			var weightTrack = clip.Tracks.First( t => t.Kind == RigTrackKind.IkWeight && t.Name == "arm_L" );

			Require( boneTrack.Keys[0].Local.Position.x == 1f );
			Require( goalTrack.Keys[0].Local.Position.x == 10f );
			Require( weightTrack.Keys[0].Local.Scale.x == 0.5f );

			// Delete only the IkGoal key
			ClipEditService.DeleteKey( session, RigTrackKind.IkGoal, "arm_L", 0 );
			Require( goalTrack.Keys.Count == 0 );
			Require( boneTrack.Keys.Count == 1, "bone track must be untouched" );
			Require( weightTrack.Keys.Count == 1, "weight track must be untouched" );

			// Undo deletion restores IkGoal key
			session.History.Undo();
			Require( goalTrack.Keys.Count == 1 && goalTrack.Keys[0].Local.Position.x == 10f );
		} );

		Check( "clip length policy on deletion preserves intentionally held duration", () =>
		{
			var session = MakeSessionWithClip( frameCount: 20 );
			var clip = session.Clip!;

			// Add key beyond frame 20 extending clip
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 60, RigPose.Identity );
			Require( clip.FrameCount == 61 );

			// Delete key at frame 60
			ClipEditService.DeleteKey( session, RigTrackKind.Bone, "root", 60 );
			Require( clip.FrameCount == 61, "deleting a late key must preserve intentionally held clip FrameCount" );

			// Undo delete
			session.History.Undo();
			Require( clip.FrameCount == 61 );
			Require( clip.Tracks[0].Keys.Count == 1 );

			// Undo insert restores initial FrameCount
			session.History.Undo();
			Require( clip.FrameCount == 20, "undoing insert must restore initial FrameCount" );
		} );

		Check( "IK goals service integrates with history and updates tracks", () =>
		{
			var session = MakeSessionWithClip( frameCount: 30 );
			var clip = session.Clip!;

			var targets = new[]
			{
				new ClipKeys.Target( 0, new Vec3( 10, 5, 2 ), Quat.Identity, RigEase.Linear, 1f ),
				new ClipKeys.Target( 15, new Vec3( 20, 5, 2 ), Quat.Identity, RigEase.Smooth, 0.8f )
			};

			ClipEditService.SetIkGoals( session, "arm_L", targets );
			Require( session.IsDirty );

			var goalTrack = clip.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.IkGoal && t.Name == "arm_L" );
			var weightTrack = clip.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.IkWeight && t.Name == "arm_L" );
			Require( goalTrack != null && goalTrack.Keys.Count == 2 );
			Require( weightTrack != null && weightTrack.Keys.Count == 2 );

			Require( goalTrack!.Keys[0].Frame == 0 && goalTrack.Keys[1].Frame == 15 );
			Require( weightTrack!.Keys[1].Local.Scale.x == 0.8f );

			// Undo restores empty tracks
			session.History.Undo();
			Require( goalTrack.Keys.Count == 0 );
			Require( weightTrack.Keys.Count == 0 );

			// Redo reapplies IK goals
			session.History.Redo();
			Require( goalTrack.Keys.Count == 2 );
			Require( weightTrack.Keys.Count == 2 );
		} );
	}
}
