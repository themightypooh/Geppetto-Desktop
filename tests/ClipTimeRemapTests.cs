using System;
using System.Linq;
using Effigy;
using Marionette.Desktop;

internal static class ClipTimeRemapTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "clip-time-remap", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws( Action action )
		{
			try { action(); }
			catch ( Exception ) { return; }
			throw new Exception( "expected an error, got silent success" );
		}

		RigPose At( float y ) => new( new Vec3( 0, y, 0 ), Quat.Identity, Vec3.One );

		// A 2-second clip at 30 fps: keys at 0s, 1s, 2s on root, a morph event 1s..2s
		// and a mark at 1s. FrameCount 60 (frames 0..59 by the clip's own convention;
		// the last key sits on frame 60, which the writer keeps via LastKeyFrame).
		RigAnimFile MakeClip()
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 60 };
			clip.Tracks.Add( new RigTrack { Name = "root", Kind = RigTrackKind.Bone, Keys =
				{ new RigKey { Frame = 0, Local = At( 0 ), Ease = RigEase.Linear },
				  new RigKey { Frame = 30, Local = At( 10 ), Ease = RigEase.Linear },
				  new RigKey { Frame = 60, Local = At( 20 ), Ease = RigEase.Linear } } } );
			clip.MorphEvents.Add( new RigMorphEvent { Morph = "smile", Start = 30, End = 60, FadeIn = 6, FadeOut = 6, Value = 1f } );
			clip.Marks = new() { new RigAnimMark { Name = "middle", Frame = 30, Note = "1s" } };
			return clip;
		}

		int[] Frames( RigAnimFile clip ) => clip.Tracks[0].Keys.Select( k => k.Frame ).ToArray();

		Check( "a 2-second clip remapped to 4 seconds plays at half speed with keys at doubled times", () =>
		{
			var source = MakeClip();
			var remap = ClipTimeRemap.ConstantSpeed( 0.5f, 2f );
			Require( MathF.Abs( remap.OutputDuration - 4f ) < 1e-6f, $"output length {remap.OutputDuration}" );
			Require( MathF.Abs( remap.MapOutputToSource( 2f ) - 1f ) < 1e-6f, "output 2s must read source 1s" );

			var baked = ClipEditService.BakeRemappedClip( source, remap );
			Require( baked.FrameCount == 120, $"baked length {baked.FrameCount}" );
			Require( Frames( baked ).SequenceEqual( new[] { 0, 60, 120 } ), string.Join( ",", Frames( baked ) ) );
			Require( baked.MorphEvents.Count == 1 && baked.MorphEvents[0].Start == 60 && baked.MorphEvents[0].End == 120,
				"morph event did not retime" );
			Require( baked.Marks is { Count: 1 } && baked.Marks[0].Frame == 60, "mark did not retime" );

			// The baked clip poses match the source at the mapped time, and the source is untouched.
			Require( Frames( source ).SequenceEqual( new[] { 0, 30, 60 } ), "source keys moved" );
			Require( source.FrameCount == 60, "source length moved" );
			var bakedPose = baked.Tracks[0].Evaluate( 60 );
			var sourcePose = source.Tracks[0].Evaluate( 30 );
			Require( MathF.Abs( bakedPose.Position.y - sourcePose.Position.y ) < 1e-4f, "baked pose mismatch" );
		} );

		Check( "a baked export of the remapped clip has the retimed keys and the source export is unchanged", () =>
		{
			var source = MakeClip();
			var before = RigAnimWriter.Write( source );
			var baked = ClipEditService.BakeRemappedClip( source, ClipTimeRemap.ConstantSpeed( 0.5f, 2f ) );
			var roundTripped = RigAnimFile.Read( RigAnimWriter.Write( baked ) );
			// The writer stamps max(FrameCount, LastKeyFrame + 1), so a clip whose last key
			// sits on its final frame re-reads one frame longer; the keys are the assertion.
			Require( roundTripped.FrameCount == 121, $"exported length {roundTripped.FrameCount}" );
			Require( roundTripped.LastKeyFrame == 120, "exported last key moved" );
			Require( roundTripped.Tracks[0].Keys.Select( k => k.Frame ).SequenceEqual( new[] { 0, 60, 120 } ),
				"exported keys did not retime" );
			Require( RigAnimWriter.Write( source ) == before, "source export changed" );
		} );

		Check( "ease in/out starts and ends slowly and keeps the endpoints exact", () =>
		{
			var remap = ClipTimeRemap.EaseInOut( 2f, 4f );
			Require( remap.MapOutputToSource( 0f ) == 0f && remap.MapOutputToSource( 4f ) == 2f, "endpoints moved" );
			var early = remap.MapOutputToSource( 1f );
			var mid = remap.MapOutputToSource( 2f );
			Require( early < 0.5f, $"first second should cover less than half the clip, covered {early}" );
			Require( MathF.Abs( mid - 1f ) < 1e-4f, $"midpoint {mid}" );
			var baked = ClipEditService.BakeRemappedClip( MakeClip(), remap );
			Require( Frames( baked ).SequenceEqual( new[] { 0, 60, 120 } ), "ease endpoints moved keys" );
		} );

		Check( "a freeze holds the frame and the keys inside it collapse onto the frozen pose", () =>
		{
			var remap = ClipTimeRemap.Hold( 1f, 0.5f, 2f );
			Require( MathF.Abs( remap.OutputDuration - 2.5f ) < 1e-6f, $"output length {remap.OutputDuration}" );
			Require( remap.MapOutputToSource( 1.25f ) == 1f, "freeze did not hold source 1s" );
			Require( remap.MapOutputToSource( 2.5f ) == 2f, "clip did not resume after the freeze" );

			var baked = ClipEditService.BakeRemappedClip( MakeClip(), remap );
			// Source frames 30 (1s) and 60 (2s) map to output 30 and 75; the freeze adds a
			// frozen key at its end (output frame 45), so the hold reads flat.
			Require( Frames( baked ).SequenceEqual( new[] { 0, 30, 45, 75 } ), string.Join( ",", Frames( baked ) ) );
			// During the hold the baked pose is the frozen one: output 1.25s == source 1s.
			var held = baked.Tracks[0].Evaluate( 37.5f );
			Require( MathF.Abs( held.Position.y - 10f ) < 1e-4f, $"held pose {held.Position.y}" );
		} );

		Check( "reverse plays the clip backwards with the first key last", () =>
		{
			var remap = ClipTimeRemap.Reverse( 2f );
			Require( remap.MapOutputToSource( 0f ) == 2f && remap.MapOutputToSource( 2f ) == 0f, "reverse endpoints wrong" );
			var baked = ClipEditService.BakeRemappedClip( MakeClip(), remap );
			Require( Frames( baked ).SequenceEqual( new[] { 0, 30, 60 } ), string.Join( ",", Frames( baked ) ) );
			Require( baked.Tracks[0].Keys[0].Local.Position.y == 20f, "first baked key is not the old last pose" );
			Require( baked.Tracks[0].Keys[2].Local.Position.y == 0f, "last baked key is not the old first pose" );
		} );

		Check( "an invalid remap or a null clip is refused, not silently accepted", () =>
		{
			Throws( () => ClipTimeRemap.ConstantSpeed( 0f, 2f ) );
			Throws( () => ClipTimeRemap.ConstantSpeed( -1f, 2f ) );
			Throws( () => ClipTimeRemap.Hold( 3f, 0.5f, 2f ) );
			Throws( () => ClipTimeRemap.FromPoints( new[] { new TimeRemapPoint( 0f, 0f ), new TimeRemapPoint( 1f, 2f ), new TimeRemapPoint( 1f, 3f ) }, 3f ) );
			Throws( () => ClipEditService.BakeRemappedClip( (RigAnimFile)null!, ClipTimeRemap.ConstantSpeed( 1f, 2f ) ) );
			Throws( () => ClipEditService.BakeRemappedClip( MakeClip(), null! ) );
		} );

		Check( "channel-curve keys retime with the track keys", () =>
		{
			var source = MakeClip();
			source.Tracks[0].ChannelCurves[RigChannel.PosY] = new()
			{
				new ScalarKey( 0, 0f ), new ScalarKey( 30, 10f ), new ScalarKey( 60, 20f ),
			};
			var baked = ClipEditService.BakeRemappedClip( source, ClipTimeRemap.ConstantSpeed( 0.5f, 2f ) );
			var curve = baked.Tracks[0].ChannelCurves[RigChannel.PosY];
			Require( curve.Select( k => k.Frame ).SequenceEqual( new[] { 0, 60, 120 } ), "curve keys did not retime" );
			Require( curve[1].Value == 10f, "curve value moved" );
			Require( source.Tracks[0].ChannelCurves[RigChannel.PosY][1].Frame == 30, "source curve moved" );
		} );
		Check( "playback through MarionettePlayer samples the remapped frame", () =>
		{
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Translate( new Vec3( 0, 0, 0 ) ) );
			rig.AddBone( "arm_L", 0, Xform.Translate( new Vec3( 5, 0, 0 ) ) );
			var player = new MarionettePlayer( rig );
			player.LoadClip( MakeClip() );
			player.TimeRemap = ClipTimeRemap.ConstantSpeed( 0.5f, 2f );
			player.Playing = true;
			player.Step( 2.0f );
			// Output 2s reads source 1s (frame 30, y = 10); output 3s reads source 1.5s (y = 15).
			Require( MathF.Abs( player.WorldPose[0].Origin.y - 10f ) < 0.001f, $"half-speed playback at 2s gave y={player.WorldPose[0].Origin.y}" );
			player.Step( 1.0f );
			Require( MathF.Abs( player.WorldPose[0].Origin.y - 15f ) < 0.001f, $"half-speed playback at 3s gave y={player.WorldPose[0].Origin.y}" );
		} );

	}
}
