using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Marionette.Desktop;

internal static class ClipAnimOpsTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "clip-anim-ops", name, test );
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
			rig.AddBone( "hand_L", 1, Xform.Translate( new Vec3( 9, 0, 0 ) ) );
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

		RigPose PoseAt( float x ) => new( new Vec3( x, 0, 0 ), Quat.Identity, Vec3.One );

		List<ClipEditService.SelectedKey> Sel( RigTrackKind kind, string name, params int[] frames ) =>
			frames.Select( f => new ClipEditService.SelectedKey( kind, name, f ) ).ToList();

		Check( "reverse flips frames poses and ease, undo restores", () =>
		{
			var session = MakeSessionWithClip( 31 );
			var clip = session.Clip!;
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 0, PoseAt( 0 ), RigEase.EaseIn );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 10, PoseAt( 10 ), RigEase.Linear );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 20, PoseAt( 20 ), RigEase.EaseOut );
			ClipEditService.ReverseKeys( session, Sel( RigTrackKind.Bone, "root", 0, 10, 20 ) );
			var track = clip.Tracks.First( t => t.Name == "root" );
			Require( track.Keys[0].Frame == 0 && track.Keys[0].Local.Position.x == 20, "frame 0 takes the old frame-20 pose" );
			Require( track.Keys[1].Frame == 10 && track.Keys[1].Local.Position.x == 10, "middle key stays" );
			Require( track.Keys[2].Frame == 20 && track.Keys[2].Local.Position.x == 0, "frame 20 takes the old frame-0 pose" );
			Require( track.Keys[0].Ease == RigEase.EaseOut, "ease flips with the pose (old frame-20 EaseOut lands at 0)" );
			Require( track.Keys[2].Ease == RigEase.EaseIn, "ease flips with the pose (old frame-0 EaseIn lands at 20)" );
			session.History.Undo();
			Require( track.Keys[0].Frame == 0 && track.Keys[0].Local.Position.x == 0, "undo restores frame 0" );
			Require( track.Keys[2].Frame == 20 && track.Keys[2].Local.Position.x == 20, "undo restores frame 20" );
			session.History.Redo();
			Require( track.Keys[0].Local.Position.x == 20, "redo reverses again" );
		} );

		Check( "reverse single-frame selection refused", () =>
		{
			var session = MakeSessionWithClip( 31 );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 10, PoseAt( 10 ) );
			Throws<InvalidOperationException>( () => ClipEditService.ReverseKeys( session, Sel( RigTrackKind.Bone, "root", 10 ) ) );
		} );

		Check( "retime doubles frames about pivot, scales tangents, undo restores", () =>
		{
			var session = MakeSessionWithClip( 61 );
			var clip = session.Clip!;
			var tin = new float[RigHermite.Channels]; tin[0] = 2f;
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 0, PoseAt( 0 ) );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 10, PoseAt( 10 ), RigEase.Linear, tin, null );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 20, PoseAt( 20 ) );
			ClipEditService.RetimeKeys( session, Sel( RigTrackKind.Bone, "root", 0, 10, 20 ), 2f );
			var track = clip.Tracks.First( t => t.Name == "root" );
			Require( track.Keys[0].Frame == 0 && track.Keys[1].Frame == 20 && track.Keys[2].Frame == 40, "frames double about 0" );
			Require( Math.Abs( track.Keys[1].TangentIn![0] - 1f ) < 1e-6f, "tangent divides by the factor" );
			Require( clip.FrameCount == 61, "frame count holds (61 already covers 40)" );
			session.History.Undo();
			Require( track.Keys[1].Frame == 10 && Math.Abs( track.Keys[1].TangentIn![0] - 2f ) < 1e-6f, "undo restores frames and tangents" );
			Throws<ArgumentException>( () => ClipEditService.RetimeKeys( session, Sel( RigTrackKind.Bone, "root", 0 ), 1f ) );
			Throws<ArgumentException>( () => ClipEditService.RetimeKeys( session, Sel( RigTrackKind.Bone, "root", 0 ), 0f ) );
		} );

		Check( "reduce drops a redundant middle key, keeps endpoints, undo restores", () =>
		{
			var session = MakeSessionWithClip( 31 );
			var clip = session.Clip!;
			// Straight line: the frame-10 key carries nothing the endpoints do not.
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 0, PoseAt( 0 ), RigEase.Linear );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 10, PoseAt( 10 ), RigEase.Linear );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 20, PoseAt( 20 ), RigEase.Linear );
			ClipEditService.ReduceKeys( session, Sel( RigTrackKind.Bone, "root", 0, 10, 20 ) );
			var track = clip.Tracks.First( t => t.Name == "root" );
			Require( track.Keys.Count == 2 && track.Keys[0].Frame == 0 && track.Keys[1].Frame == 20, "middle key goes, endpoints stay" );
			Require( Math.Abs( track.Evaluate( 10 ).Position.x - 10 ) < 0.05f, "sampled pose at 10 is unchanged" );
			session.History.Undo();
			Require( track.Keys.Count == 3, "undo restores the removed key" );
		} );

		Check( "reduce refuses a bent curve", () =>
		{
			var session = MakeSessionWithClip( 31 );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 0, PoseAt( 0 ), RigEase.Linear );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 10, new RigPose( new Vec3( 10, 50, 0 ), Quat.Identity, Vec3.One ), RigEase.Linear );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 20, PoseAt( 20 ), RigEase.Linear );
			Throws<InvalidOperationException>( () => ClipEditService.ReduceKeys( session, Sel( RigTrackKind.Bone, "root", 0, 10, 20 ) ) );
		} );

		Check( "offset shifts and clamps at zero, undo restores", () =>
		{
			var session = MakeSessionWithClip( 31 );
			var clip = session.Clip!;
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 5, PoseAt( 5 ) );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 10, PoseAt( 10 ) );
			ClipEditService.OffsetKeys( session, Sel( RigTrackKind.Bone, "root", 5, 10 ), -8 );
			var track = clip.Tracks.First( t => t.Name == "root" );
			Require( track.Keys[0].Frame == 0 && track.Keys[1].Frame == 2, "shift clamps at frame 0" );
			session.History.Undo();
			Require( track.Keys[0].Frame == 5 && track.Keys[1].Frame == 10, "undo restores" );
			Throws<InvalidOperationException>( () => ClipEditService.OffsetKeys( session, Sel( RigTrackKind.Bone, "root", 5, 10 ), 0 ) );
		} );

		Check( "make loop copies first pose to the end, drops past it, undo restores", () =>
		{
			var session = MakeSessionWithClip( 31 );
			var clip = session.Clip!;
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 0, PoseAt( 0 ) );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 10, PoseAt( 10 ) );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 25, PoseAt( 25 ) );
			ClipEditService.MakeLoop( session, 20 );
			var track = clip.Tracks.First( t => t.Name == "root" );
			Require( track.Keys.Count == 3, "first, middle, loop end" );
			Require( track.Keys[2].Frame == 20 && track.Keys[2].Local.Position.x == 0, "loop end copies the first pose" );
			Require( track.Keys.All( k => k.Frame <= 20 ), "nothing past the loop end" );
			session.History.Undo();
			Require( track.Keys.Count == 3 && track.Keys[2].Frame == 25, "undo restores the dropped key" );
			Throws<ArgumentOutOfRangeException>( () => ClipEditService.MakeLoop( session, 0 ) );
		} );

		Check( "copy paste cut round-trip as undo entries", () =>
		{
			var session = MakeSessionWithClip( 121 );
			var clip = session.Clip!;
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 10, PoseAt( 10 ) );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 20, PoseAt( 20 ) );
			var clipboard = ClipEditService.CopyKeys( clip, Sel( RigTrackKind.Bone, "root", 10, 20 ) );
			ClipEditService.PasteKeys( session, clipboard, 100 );
			var track = clip.Tracks.First( t => t.Name == "root" );
			Require( track.Keys.Count == 4, "pasted keys join the originals" );
			Require( track.Keys[2].Frame == 100 && track.Keys[2].Local.Position.x == 10, "paste lands at the target, offsets kept" );
			Require( track.Keys[3].Frame == 110 && track.Keys[3].Local.Position.x == 20, "second pasted key keeps its offset" );
			session.History.Undo();
			Require( track.Keys.Count == 2, "undo removes the pasted keys" );
			var cut = ClipEditService.CutKeys( session, Sel( RigTrackKind.Bone, "root", 10, 20 ) );
			Require( track.Keys.Count == 0, "cut deletes" );
			Require( cut.Entries.Count == 2 && cut.MinFrame == 10, "cut returns the clipboard" );
			session.History.Undo();
			Require( track.Keys.Count == 2, "undo restores cut keys" );
			ClipEditService.PasteKeys( session, cut, 50 );
			Require( track.Keys[2].Frame == 50 && track.Keys[3].Frame == 60, "cut clipboard pastes elsewhere" );
		} );

		Check( "tangents: set, auto clears, flat zeroes, undo restores", () =>
		{
			var session = MakeSessionWithClip( 31 );
			var clip = session.Clip!;
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 0, PoseAt( 0 ), RigEase.Linear );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 10, PoseAt( 10 ), RigEase.Linear );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "root", 20, PoseAt( 20 ), RigEase.Linear );
			var track = clip.Tracks.First( t => t.Name == "root" );
			var auto = ClipEditService.AutoTangent( track, 1, monotone: false );
			Require( auto.Length == RigHermite.Channels && Math.Abs( auto[0] - 1f ) < 1e-5f, "auto slope through a straight line is 1 per frame" );
			var written = new float[RigHermite.Channels]; written[0] = 3f;
			ClipEditService.SetKeyTangents( session, RigTrackKind.Bone, "root", 10, written, written );
			Require( track.Keys[1].TangentIn![0] == 3f && track.Keys[1].TangentOut![0] == 3f, "explicit tangents land" );
			ClipEditService.AutoTangents( session, Sel( RigTrackKind.Bone, "root", 10 ) );
			Require( track.Keys[1].TangentIn is null && track.Keys[1].TangentOut is null, "auto clears stored tangents" );
			session.History.Undo();
			Require( track.Keys[1].TangentIn![0] == 3f, "undo restores the written tangents" );
			ClipEditService.FlatTangents( session, Sel( RigTrackKind.Bone, "root", 10 ) );
			Require( track.Keys[1].TangentIn!.All( v => v == 0f ), "flat zeroes tangents" );
			Throws<InvalidOperationException>( () => ClipEditService.SetKeyTangents( session, RigTrackKind.Bone, "root", 99, null, null ) );
		} );

		Check( "add IK refuses duplicate end bone and unknown bone, clamps chain to depth", () =>
		{
			var session = MakeSessionWithClip( 31 );
			var clip = session.Clip!;
			var rig = MakeRig();
			var ik = ClipEditService.AddIk( session, "hand_L", 5, rig: rig );
			Require( ik.ChainLength == 2, "chain clamps to the bone depth (hand_L is 2 above root)" );
			Require( ik.Live && ik.Enabled && ik.Weight == 1f, "new chain is live, enabled, weight 1" );
			Require( clip.Ik!.Count == 1, "constraint lands on the clip" );
			Throws<InvalidOperationException>( () => ClipEditService.AddIk( session, "hand_L", 2, rig: rig ) );
			Throws<InvalidOperationException>( () => ClipEditService.AddIk( session, "ghost", 2, rig: rig ) );
			session.History.Undo();
			Require( clip.Ik is null || clip.Ik.Count == 0, "undo removes the constraint" );
			session.History.Redo();
			Require( clip.Ik!.Count == 1, "redo re-adds it" );
		} );

		Check( "delete IK drops goal and weight tracks, undo restores all", () =>
		{
			var session = MakeSessionWithClip( 31 );
			var clip = session.Clip!;
			var ik = ClipEditService.AddIk( session, "hand_L", 2 );
			ClipEditService.SetIkGoals( session, "hand_L", new[]
			{
				new ClipKeys.Target( 0, new Vec3( 9, 0, 0 ), null, RigEase.Linear, 1f ),
			} );
			Require( clip.Tracks.Any( t => t.Kind == RigTrackKind.IkGoal ), "goal track exists" );
			Require( clip.Tracks.Any( t => t.Kind == RigTrackKind.IkWeight ), "weight track exists" );
			ClipEditService.DeleteIk( session, ik.Name );
			Require( clip.Ik!.Count == 0, "constraint goes" );
			Require( !clip.Tracks.Any( t => t.Kind == RigTrackKind.IkGoal ), "goal track goes with it" );
			Require( !clip.Tracks.Any( t => t.Kind == RigTrackKind.IkWeight ), "weight track goes with it" );
			session.History.Undo();
			Require( clip.Ik.Count == 1, "undo restores the constraint" );
			Require( clip.Tracks.Any( t => t.Kind == RigTrackKind.IkGoal ), "undo restores the goal track" );
			Throws<InvalidOperationException>( () => ClipEditService.DeleteIk( session, "nope" ) );
		} );

		Check( "IK field edits and reorder, undo restores", () =>
		{
			var session = MakeSessionWithClip( 31 );
			var clip = session.Clip!;
			var a = ClipEditService.AddIk( session, "arm_L", 2 );
			var b = ClipEditService.AddIk( session, "hand_L", 2 );
			ClipEditService.SetIkChainLength( session, a.Name, 99 );
			Require( clip.Ik![0].ChainLength == 8, "chain length clamps to 1..8" );
			ClipEditService.SetIkSolver( session, a.Name, fabrik: true, spline: true );
			Require( clip.Ik[0].Spline && !clip.Ik[0].Fabrik, "spline wins over fabrik" );
			ClipEditService.SetIkGoalRotation( session, a.Name, false );
			Require( !clip.Ik[0].GoalRotation, "goal rotation off" );
			ClipEditService.SetIkLive( session, a.Name, false );
			Require( !clip.Ik[0].Live, "live off" );
			ClipEditService.SetIkEnabled( session, a.Name, false );
			Require( !clip.Ik[0].Enabled, "enabled off" );
			ClipEditService.MoveIk( session, a.Name, +1 );
			Require( clip.Ik[0].Name == b.Name, "move down swaps the order" );
			Throws<InvalidOperationException>( () => ClipEditService.MoveIk( session, b.Name, -1 ) );
			session.History.Undo();
			Require( clip.Ik[0].Name == a.Name, "undo restores the order" );
		} );

		Check( "lookat is a one-bone position-only chain", () =>
		{
			var session = MakeSessionWithClip( 31 );
			var clip = session.Clip!;
			var ik = ClipEditService.LookAt( session, "arm_L", MakeRig() );
			Require( ik.ChainLength == 1 && !ik.GoalRotation && ik.Name == "Look", "look-at chain" );
			Require( clip.Ik!.Count == 1, "lands on the clip" );
		} );

		Check( "reset IK handle keys the goal at the playhead FK pose", () =>
		{
			var session = MakeSessionWithClip( 31 );
			var clip = session.Clip!;
			var rig = MakeRig();
			var ik = ClipEditService.AddIk( session, "hand_L", 2, rig: rig );
			session.PlayheadTime = 10f / 30f;
			ClipEditService.ResetIkHandle( session, ik.Name, rig );
			var goal = clip.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.IkGoal && t.Name == "hand_L" );
			Require( goal is not null && goal.Keys.Count == 1 && goal.Keys[0].Frame == 10, "goal keyed at the playhead frame" );
			Require( Math.Abs( goal.Keys[0].Local.Position.x - 9f ) < 0.01f, "goal sits on the end bone bind position" );
			session.History.Undo();
			Require( !clip.Tracks.Any( t => t.Kind == RigTrackKind.IkGoal ), "undo removes the goal" );
		} );

		Check( "limits add range delete, undo restores", () =>
		{
			var session = MakeSessionWithClip( 31 );
			var clip = session.Clip!;
			var limit = ClipEditService.AddLimit( session, "arm_L", rig: MakeRig() );
			Require( limit.Enabled && limit.Weight == 1f, "new limit enabled, weight 1" );
			Require( limit.Min.x == -45f && limit.Max.x == 45f, "default +-45 degrees" );
			Throws<InvalidOperationException>( () => ClipEditService.AddLimit( session, "arm_L" ) );
			Throws<InvalidOperationException>( () => ClipEditService.AddLimit( session, "ghost", rig: MakeRig() ) );
			ClipEditService.SetLimitRange( session, "arm_L", new Vec3( 30, 0, 0 ), new Vec3( -30, 0, 0 ) );
			Require( clip.Limits![0].Min.x == -30f && clip.Limits[0].Max.x == 30f, "range orders min below max per axis" );
			ClipEditService.SetLimitEnabled( session, "arm_L", false );
			Require( !clip.Limits[0].Enabled, "limit disabled" );
			ClipEditService.DeleteLimit( session, "arm_L" );
			Require( clip.Limits.Count == 0, "limit goes" );
			Throws<InvalidOperationException>( () => ClipEditService.DeleteLimit( session, "arm_L" ) );
			session.History.Undo();
			Require( clip.Limits.Count == 1, "undo restores the limit" );
		} );

		Check( "marks add move remove, duplicate frame refused, undo restores", () =>
		{
			var session = MakeSessionWithClip( 31 );
			var clip = session.Clip!;
			var mark = ClipEditService.AddMark( session, 10, note: "hit" );
			Require( mark.Name == "mark 1" && mark.Frame == 10 && mark.Note == "hit", "mark lands with a generated name" );
			Require( clip.Marks!.Count == 1, "mark on the clip" );
			Throws<InvalidOperationException>( () => ClipEditService.AddMark( session, 10 ) );
			ClipEditService.MoveMark( session, mark, 99 );
			Require( mark.Frame == 30, "move clamps to the clip's last frame" );
			ClipEditService.RemoveMark( session, mark );
			Require( clip.Marks.Count == 0, "mark goes" );
			Throws<InvalidOperationException>( () => ClipEditService.RemoveMark( session, mark ) );
			session.History.Undo();
			Require( clip.Marks.Count == 1 && clip.Marks[0].Frame == 30, "undo restores the removed mark" );
		} );

		Check( "morph events add remove, weights reflect, bad ranges refused", () =>
		{
			var session = MakeSessionWithClip( 31 );
			var clip = session.Clip!;
			var e = ClipEditService.AddMorphEvent( session, "smile", 5, 15, value: 0.8f, fadeIn: 2, fadeOut: 2 );
			Require( clip.MorphEvents.Count == 1, "event lands" );
			Require( Math.Abs( clip.MorphWeightsAt( 10 )["smile"] - 0.8f ) < 1e-5f, "mid-event weight is the value" );
			Require( !clip.MorphWeightsAt( 0 ).ContainsKey( "smile" ), "outside the range the morph is absent" );
			Throws<ArgumentException>( () => ClipEditService.AddMorphEvent( session, "frown", 10, 5 ) );
			Throws<ArgumentException>( () => ClipEditService.AddMorphEvent( session, "", 0, 5 ) );
			ClipEditService.RemoveMorphEvent( session, e );
			Require( clip.MorphEvents.Count == 0, "event goes" );
			session.History.Undo();
			Require( clip.MorphEvents.Count == 1, "undo restores the event" );
		} );

		Check( "retarget renames bones and lists unmapped, source untouched", () =>
		{
			var session = MakeSessionWithClip( 31 );
			var clip = session.Clip!;
			var sourceRig = MakeRig();
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "arm_L", 0, PoseAt( 1 ) );
			ClipEditService.InsertOrReplaceKey( session, RigTrackKind.Bone, "hand_L", 0, PoseAt( 2 ) );
			var targetRig = new Skeleton();
			targetRig.AddBone( "root", -1, Xform.Translate( new Vec3( 0, 0, 0 ) ) );
			targetRig.AddBone( "upper_L", 0, Xform.Translate( new Vec3( 10, 0, 0 ) ) );
			var result = ClipEditService.RetargetClip( session, sourceRig, targetRig, new Dictionary<string, string> { ["arm_L"] = "upper_L" } );
			Require( result.Clip.Tracks.Any( t => t.Name == "upper_L" ), "mapped bone retargets" );
			Require( !result.Clip.Tracks.Any( t => t.Name == "hand_L" ), "unmapped bone does not transfer" );
			Require( result.Unmapped.Contains( "hand_L" ), "unmapped bone is listed, not dropped silently" );
			Require( Math.Abs( result.RootMotionScale - 2f ) < 0.01f, "root motion scales with skeleton height (10 vs 5)" );
			Require( clip.Tracks.Any( t => t.Name == "arm_L" ), "source clip untouched" );
			Throws<ArgumentNullException>( () => ClipEditService.RetargetClip( session, sourceRig, targetRig, null! ) );
		} );

		Check( "NLA bake through AnimCallers", () =>
		{
			var baseClip = new RigAnimFile { FrameRate = 30, FrameCount = 11 };
			var baseTrack = new RigTrack { Kind = RigTrackKind.Bone, Name = "root" };
			baseTrack.Keys.Add( new RigKey { Frame = 0, Local = PoseAt( 0 ), Ease = RigEase.Linear } );
			baseTrack.Keys.Add( new RigKey { Frame = 10, Local = PoseAt( 10 ), Ease = RigEase.Linear } );
			baseClip.Tracks.Add( baseTrack );
			var action = new RigAnimFile { FrameRate = 30, FrameCount = 11 };
			var actionTrack = new RigTrack { Kind = RigTrackKind.Bone, Name = "root" };
			actionTrack.Keys.Add( new RigKey { Frame = 0, Local = PoseAt( 100 ), Ease = RigEase.Linear } );
			actionTrack.Keys.Add( new RigKey { Frame = 10, Local = PoseAt( 110 ), Ease = RigEase.Linear } );
			action.Tracks.Add( actionTrack );
			var timeline = new NlaTimeline { Base = baseClip };
			timeline.Strips.Add( new NlaStrip { Name = "wave", Action = action, Start = 0, End = 10, Weight = 1f, Mode = NlaBlendMode.Replace } );
			var baked = AnimCallers.BuildNlaClip( timeline, 0, 10 );
			var bakedTrack = baked.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.Bone && t.Name == "root" );
			Require( bakedTrack is not null && bakedTrack.Keys.Count == 11, "baked strip has one key per frame" );
			Require( Math.Abs( bakedTrack.Keys[0].Local.Position.x - 100f ) < 0.01f, "frame 0 plays the strip, not the base" );
			Require( Math.Abs( bakedTrack.Keys[10].Local.Position.x - 110f ) < 0.01f, "frame 10 plays the strip end" );
			Throws<ArgumentNullException>( () => AnimCallers.BuildNlaClip( null!, 0, 10 ) );
			Throws<ArgumentException>( () => AnimCallers.BuildNlaClip( timeline, 10, 0 ) );
		} );
	}
}
