#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Marionette.Desktop;

internal static class TimelineEventTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "timeline_event", name, test );

		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		void Throws<TException>( Action action ) where TException : Exception
		{
			try
			{
				action();
			}
			catch ( TException )
			{
				return;
			}
			catch ( Exception ex )
			{
				throw new Exception( $"Expected {typeof(TException).Name}, but got {ex.GetType().Name}: {ex.Message}" );
			}
			throw new Exception( $"Expected {typeof(TException).Name}, but action succeeded silently." );
		}

		// 1. events dispatched when frame crosses interval (prev, curr]
		Check( "events dispatched when frame crosses interval (prev, curr]", () =>
		{
			var dispatcher = new TimelineEventDispatcher { FrameRate = 30f, Loop = false };
			dispatcher.AddMark( "cue_5", 5 );
			dispatcher.AddMark( "cue_10", 10 );
			dispatcher.AddMark( "cue_15", 15 );
			dispatcher.ClipEndFrame = 30;

			// Invalid inputs throw
			Throws<ArgumentOutOfRangeException>( () => dispatcher.Step( -1f ) );
			Throws<ArgumentOutOfRangeException>( () => dispatcher.Step( float.NaN ) );
			Throws<ArgumentOutOfRangeException>( () => dispatcher.Seek( float.NaN ) );

			dispatcher.Play();

			// First step: from frame 0 to 6. Interval is (0, 6]
			// Should dispatch cue_5 (frame 5), but NOT cue_10 or cue_15
			var batch1 = dispatcher.Step( 6f / 30f );
			Assert( batch1.Count == 1, $"Expected 1 notification, got {batch1.Count}" );
			Assert( batch1[0].Name == "cue_5" );
			Assert( MathF.Abs( batch1[0].Frame - 5f ) < 1e-4f );
			Assert( batch1[0].Kind == TimelineEventKind.Mark );

			// Second step: from 6 to 8. Interval is (6, 8]
			// No events inside (6, 8]
			var batch2 = dispatcher.Step( 2f / 30f );
			Assert( batch2.Count == 0, $"Expected 0 notifications, got {batch2.Count}" );

			// Third step: from 8 to 12. Interval is (8, 12]
			// Should dispatch cue_10 (frame 10)
			var batch3 = dispatcher.Step( 4f / 30f );
			Assert( batch3.Count == 1, $"Expected 1 notification, got {batch3.Count}" );
			Assert( batch3[0].Name == "cue_10" );
			Assert( MathF.Abs( batch3[0].Frame - 10f ) < 1e-4f );
		} );

		// 2. events on exact boundary fired
		Check( "events on exact boundary fired", () =>
		{
			var dispatcher = new TimelineEventDispatcher { FrameRate = 30f, Loop = false };
			dispatcher.AddMark( "exact_5", 5 );
			dispatcher.AddMark( "exact_10", 10 );
			dispatcher.ClipEndFrame = 20;

			dispatcher.Play();

			// Step 1: 0 to exactly 5.0f. Interval is (0, 5.0].
			// The event on frame 5.0 must fire because 5.0 is included in (0, 5.0].
			var batch1 = dispatcher.Step( 5f / 30f );
			Assert( batch1.Count == 1, $"Expected exact_5 to fire, got {batch1.Count} events" );
			Assert( batch1[0].Name == "exact_5" );

			// Step 2: 5.0 to exactly 10.0f. Interval is (5.0, 10.0].
			// exact_5 must NOT fire again (5.0 is excluded by open lower bound).
			// exact_10 must fire (10.0 is included by closed upper bound).
			var batch2 = dispatcher.Step( 5f / 30f );
			Assert( batch2.Count == 1, $"Expected exact_10 only, got {batch2.Count} events" );
			Assert( batch2[0].Name == "exact_10" );
			Assert( batch2.All( e => e.Name != "exact_5" ), "exact_5 must not re-fire on subsequent step" );
		} );

		// 3. two events on same frame fire in stable order
		Check( "two events on same frame fire in stable order", () =>
		{
			var dispatcher = new TimelineEventDispatcher { FrameRate = 30f, Loop = false };
			dispatcher.AddMark( "first_mark_at_7", 7 );
			dispatcher.AddMark( "second_mark_at_7", 7 );
			dispatcher.AddMark( "third_mark_at_7", 7 );
			dispatcher.AddEvent( new RigEvent { Name = "prop_event_at_7", StartFrame = 7, EndFrame = 12 } );
			dispatcher.ClipEndFrame = 20;

			dispatcher.Play();

			var batch = dispatcher.Step( 10f / 30f ); // 0 to 10
			Assert( batch.Count == 4, $"Expected 4 notifications at frame 7, got {batch.Count}" );

			// Verify deterministic insertion sequence
			Assert( batch[0].Name == "first_mark_at_7" && batch[0].Kind == TimelineEventKind.Mark );
			Assert( batch[1].Name == "second_mark_at_7" && batch[1].Kind == TimelineEventKind.Mark );
			Assert( batch[2].Name == "third_mark_at_7" && batch[2].Kind == TimelineEventKind.Mark );
			Assert( batch[3].Name == "prop_event_at_7" && batch[3].Kind == TimelineEventKind.EventStart );
		} );

		// 4. skipped frames over multiple events fires all intermediate events
		Check( "skipped frames over multiple events fires all intermediate events", () =>
		{
			var dispatcher = new TimelineEventDispatcher { FrameRate = 30f, Loop = false };
			for ( int f = 1; f <= 10; f++ )
			{
				dispatcher.AddMark( $"mark_frame_{f}", f );
			}
			dispatcher.ClipEndFrame = 30;

			dispatcher.Play();

			// Single large step from 0 to 11 (skipping across all 10 marks)
			var batch = dispatcher.Step( 11f / 30f );
			Assert( batch.Count == 10, $"Expected all 10 intermediate events, got {batch.Count}" );

			for ( int i = 0; i < 10; i++ )
			{
				int expectedFrame = i + 1;
				Assert( batch[i].Name == $"mark_frame_{expectedFrame}" );
				Assert( MathF.Abs( batch[i].Frame - expectedFrame ) < 1e-4f );
			}
		} );

		// 5. loop wrap fires events before wrap and after wrap
		Check( "loop wrap fires events before wrap and after wrap", () =>
		{
			var dispatcher = new TimelineEventDispatcher { FrameRate = 30f, Loop = true };
			dispatcher.ClipEndFrame = 30; // frames 0 to 30

			dispatcher.AddMark( "before_wrap_25", 25 );
			dispatcher.AddMark( "at_end_30", 30 );
			dispatcher.AddMark( "at_start_0", 0 );
			dispatcher.AddMark( "after_wrap_4", 4 );

			dispatcher.CurrentFrame = 20f;
			dispatcher.Play();

			// Advance from 20f by 14 frames (rawTarget = 34 -> wrappedTarget = 4f)
			var batch = dispatcher.Step( 14f / 30f );

			Assert( batch.Count == 4, $"Expected 4 notifications across wrap, got {batch.Count}" );

			// Pre-wrap events: from 20 to 30 -> 25, 30
			Assert( batch[0].Name == "before_wrap_25" && batch[0].LoopIteration == 0 );
			Assert( batch[1].Name == "at_end_30" && batch[1].LoopIteration == 0 );

			// Post-wrap events: from 0 to 4 -> 0, 4
			Assert( batch[2].Name == "at_start_0" && batch[2].LoopIteration == 1 );
			Assert( batch[3].Name == "after_wrap_4" && batch[3].LoopIteration == 1 );

			Assert( MathF.Abs( dispatcher.CurrentFrame - 4f ) < 1e-4f );
			Assert( dispatcher.IsPlaying );
		} );

		// 6. seeking backward emits no forward play events
		Check( "seeking backward emits no forward play events", () =>
		{
			var dispatcher = new TimelineEventDispatcher { FrameRate = 30f, Loop = false };
			dispatcher.AddMark( "mark_5", 5 );
			dispatcher.AddMark( "mark_10", 10 );
			dispatcher.AddMark( "mark_15", 15 );
			dispatcher.ClipEndFrame = 30;

			dispatcher.CurrentFrame = 20f;

			// Seek backward from 20 to 8 without replay
			var backwardEvents = dispatcher.Seek( 8f, replay: false );
			Assert( backwardEvents.Count == 0, "Seeking backward must emit 0 events" );
			Assert( MathF.Abs( dispatcher.CurrentFrame - 8f ) < 1e-4f );

			// Seek backward from 8 to 2 with explicit replay requested
			// Backward seeking must still suppress forward play events
			var backwardReplayEvents = dispatcher.Seek( 2f, replay: true );
			Assert( backwardReplayEvents.Count == 0, "Backward seek with replay must emit no forward play events" );
			Assert( MathF.Abs( dispatcher.CurrentFrame - 2f ) < 1e-4f );

			// Forward playback from 2 to 6 should now only fire mark_5
			dispatcher.Play();
			var forwardBatch = dispatcher.Step( 4f / 30f ); // 2 to 6
			Assert( forwardBatch.Count == 1 && forwardBatch[0].Name == "mark_5" );
		} );

		// 7. pause and resume at same time does not re-fire
		Check( "pause and resume at same time does not re-fire", () =>
		{
			var dispatcher = new TimelineEventDispatcher { FrameRate = 30f, Loop = false };
			dispatcher.AddMark( "cue_7", 7 );
			dispatcher.AddMark( "cue_12", 12 );
			dispatcher.ClipEndFrame = 30;

			dispatcher.Play();

			// Step 0 to 7: cue_7 fires
			var batch1 = dispatcher.Step( 7f / 30f );
			Assert( batch1.Count == 1 && batch1[0].Name == "cue_7" );

			// Pause at 7
			dispatcher.Pause();
			Assert( !dispatcher.IsPlaying );
			Assert( MathF.Abs( dispatcher.CurrentFrame - 7f ) < 1e-4f );

			// Stepping while paused emits 0 events
			var pausedBatch = dispatcher.Step( 1f / 30f );
			Assert( pausedBatch.Count == 0 );

			// Resume playback at unchanged time
			dispatcher.Play();
			Assert( dispatcher.IsPlaying );

			// Step from 7 to 9: cue_7 must NOT re-fire! Interval is (7, 9]
			var resumedBatch = dispatcher.Step( 2f / 30f );
			Assert( resumedBatch.Count == 0, $"Expected 0 events on resume, got {resumedBatch.Count}" );

			// Step from 9 to 13: cue_12 fires
			var nextBatch = dispatcher.Step( 4f / 30f );
			Assert( nextBatch.Count == 1 && nextBatch[0].Name == "cue_12" );
		} );

		// 8. consumer exception does not crash dispatcher
		Check( "consumer exception does not crash dispatcher", () =>
		{
			var dispatcher = new TimelineEventDispatcher { FrameRate = 30f, Loop = false };
			dispatcher.AddMark( "safe_1", 1 );
			dispatcher.AddMark( "faulty_2", 2 );
			dispatcher.AddMark( "safe_3", 3 );
			dispatcher.ClipEndFrame = 10;

			var received = new List<string>();

			dispatcher.NotificationDispatched += n =>
			{
				received.Add( n.Name );
				if ( n.Name == "faulty_2" )
				{
					throw new InvalidOperationException( "Simulated consumer callback crash" );
				}
			};

			dispatcher.Play();

			// Stepping from 0 to 5 covers all three marks
			var returnedBatch = dispatcher.Step( 5f / 30f );

			// Verify dispatcher did not crash and playhead advanced
			Assert( MathF.Abs( dispatcher.CurrentFrame - 5f ) < 1e-4f );
			Assert( returnedBatch.Count == 3, $"Expected all 3 notifications in return batch, got {returnedBatch.Count}" );

			// Verify faulty callback was called and all subsequent callbacks were also called
			Assert( received.Contains( "safe_1" ) );
			Assert( received.Contains( "faulty_2" ) );
			Assert( received.Contains( "safe_3" ) );

			// Verify error was safely recorded
			Assert( dispatcher.Errors.Count == 1, $"Expected 1 recorded error, got {dispatcher.Errors.Count}" );
			Assert( dispatcher.Errors[0].Notification.Name == "faulty_2" );
			Assert( dispatcher.Errors[0].ConsumerException is InvalidOperationException );
		} );

		// 9. multiple loop wraps in one huge delta step
		Check( "multiple loop wraps in one huge delta step", () =>
		{
			var dispatcher = new TimelineEventDispatcher { FrameRate = 30f, Loop = true };
			dispatcher.ClipEndFrame = 10; // 0 to 10 frames cycle
			dispatcher.AddMark( "mid_5", 5 );
			dispatcher.AddMark( "end_10", 10 );

			dispatcher.CurrentFrame = 8f;
			dispatcher.Play();

			// Step advancing 25 frames (2.5 complete loops)
			// Cycle 0: 8 -> 10: end_10 fires (LoopIteration 0)
			// Cycle 1: 0 -> 10: mid_5, end_10 fire (LoopIteration 1)
			// Cycle 2: 0 -> 10: mid_5, end_10 fire (LoopIteration 2)
			// Remainder: 0 -> 3: no marks in (0, 3] (LoopIteration 3)
			var batch = dispatcher.Step( 25f / 30f );

			Assert( batch.Count == 5, $"Expected 5 notifications over 2.5 cycles, got {batch.Count}" );
			Assert( batch[0].Name == "end_10" && batch[0].LoopIteration == 0 );
			Assert( batch[1].Name == "mid_5" && batch[1].LoopIteration == 1 );
			Assert( batch[2].Name == "end_10" && batch[2].LoopIteration == 1 );
			Assert( batch[3].Name == "mid_5" && batch[3].LoopIteration == 2 );
			Assert( batch[4].Name == "end_10" && batch[4].LoopIteration == 2 );

			Assert( MathF.Abs( dispatcher.CurrentFrame - 3f ) < 1e-4f );
		} );

		// 10. clip replacement resets playhead and emits no spurious events
		Check( "clip replacement resets playhead and emits no spurious events", () =>
		{
			var dispatcher = new TimelineEventDispatcher();

			var clip1 = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			clip1.AddMark( new RigMark( "clip1_mark", 10 ) );

			var clip2 = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			clip2.AddMark( new RigMark( "clip2_mark", 8 ) );

			int dispatchedCount = 0;
			dispatcher.NotificationDispatched += _ => dispatchedCount++;

			dispatcher.LoadClip( clip1 );
			Assert( dispatchedCount == 0, "Loading clip1 must not emit events" );
			Assert( dispatcher.CurrentFrame == 0f );

			dispatcher.Play();
			dispatcher.Step( 15f / 30f );
			Assert( dispatchedCount == 1, "clip1_mark should have fired" );

			// Replace with clip2
			dispatcher.LoadClip( clip2 );
			Assert( dispatchedCount == 1, "Loading clip2 must not emit events" );
			Assert( dispatcher.CurrentFrame == 0f );
			Assert( dispatcher.Marks.Count == 1 && dispatcher.Marks[0].Name == "clip2_mark" );

			// Play clip2
			dispatcher.Play();
			var batch = dispatcher.Step( 10f / 30f );
			Assert( batch.Count == 1 && batch[0].Name == "clip2_mark" );
		} );

		// 11. explicit replay mode on forward seek
		Check( "explicit replay mode on forward seek", () =>
		{
			var dispatcher = new TimelineEventDispatcher { FrameRate = 30f };
			dispatcher.AddMark( "cue_3", 3 );
			dispatcher.AddMark( "cue_7", 7 );
			dispatcher.ClipEndFrame = 20;

			dispatcher.CurrentFrame = 0f;

			// Seek with replay: false -> no events
			var suppressed = dispatcher.Seek( 5f, replay: false );
			Assert( suppressed.Count == 0 );
			Assert( dispatcher.CurrentFrame == 5f );

			// Seek with replay: true -> cue_7 fires
			var replayed = dispatcher.Seek( 10f, replay: true );
			Assert( replayed.Count == 1 && replayed[0].Name == "cue_7" );
			Assert( replayed[0].IsReplay );
			Assert( dispatcher.CurrentFrame == 10f );
		} );

		// 12. json and rig anim file morph events and marks parsed
		Check( "json and rig anim file morph events and marks parsed", () =>
		{
			string json = """
			{
				"AnimationSpeed": 30,
				"FrameCount": 60,
				"Marks": [
					{ "Name": "footstep_L", "Frame": 12, "Note": "heel strike" }
				],
				"Events": [
					{
						"Name": "equip_torch",
						"StartFrame": 15,
						"EndFrame": 45,
						"Side": "Client",
						"AttachBone": "hand_R",
						"FollowBone": true,
						"PropModel": "models/torch.vmdl",
						"PositionOffset": "1.0,2.0,3.0",
						"RotationOffset": "10.0,20.0,30.0",
						"ScaleOffset": 1.5
					}
				],
				"MorphEvents": [
					{
						"MorphName": "blink",
						"StartFrame": 5,
						"EndFrame": 10,
						"FadeIn": 2,
						"FadeOut": 2,
						"Value": 1.0
					}
				]
			}
			""";

			var clip = RigAnimFile.Read( json );
			var dispatcher = new TimelineEventDispatcher( clip, json );

			Assert( dispatcher.Marks.Count == 1 );
			Assert( dispatcher.Marks[0].Name == "footstep_L" && dispatcher.Marks[0].Frame == 12 );

			Assert( dispatcher.Events.Count == 1 );
			Assert( dispatcher.Events[0].Name == "equip_torch" && dispatcher.Events[0].StartFrame == 15 && dispatcher.Events[0].EndFrame == 45 );
			Assert( dispatcher.Events[0].AttachBone == "hand_R" && dispatcher.Events[0].FollowBone );
			Assert( MathF.Abs( dispatcher.Events[0].PositionOffset.x - 1f ) < 1e-4f );
			Assert( MathF.Abs( dispatcher.Events[0].ScaleOffset - 1.5f ) < 1e-4f );

			Assert( dispatcher.MorphEvents.Count == 1 );
			Assert( dispatcher.MorphEvents[0].Morph == "blink" && dispatcher.MorphEvents[0].Start == 5 );

			// Active window checks
			Assert( !dispatcher.GetActiveEvents( 10f ).Any() );
			Assert( dispatcher.GetActiveEvents( 20f ).Count == 1 );
			Assert( dispatcher.GetActiveEvents( 20f )[0].Name == "equip_torch" );
			Assert( !dispatcher.GetActiveEvents( 50f ).Any() );

			// Active morph events
			Assert( dispatcher.GetActiveMorphEvents( 7f ).Count == 1 );
			Assert( !dispatcher.GetActiveMorphEvents( 12f ).Any() );
		} );
	}
}
