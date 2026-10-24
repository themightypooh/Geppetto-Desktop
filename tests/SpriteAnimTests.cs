using System;
using System.Linq;
using Effigy.Render;

internal static class SpriteAnimTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "sprite-anim", name, test );
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

		SpriteSheet GridSheet() => SpriteSheet.FromGrid( 256, 128, 64, 64, fps: 10 );

		Check( "grid slicing is row-major with exact rects", () =>
		{
			var sheet = GridSheet();
			Require( sheet.Frames.Count == 8, $"expected 8 frames, got {sheet.Frames.Count}" );
			Require( sheet.Frames[0].X == 0 && sheet.Frames[0].Y == 0, "frame 0 at origin" );
			Require( sheet.Frames[4].X == 0 && sheet.Frames[4].Y == 64, "frame 4 starts row 2" );
			Require( sheet.Frames[7].X == 192 && sheet.Frames[7].Y == 64, "frame 7 at bottom-right" );
			Require( sheet.Frames.All( f => f.W == 64 && f.H == 64 ), "all tiles 64x64" );
		} );

		Check( "baker sheet slices directions across frames down", () =>
		{
			var sheet = SpriteSheet.FromBaker( 256, 192, 64, 4, 3 );
			Require( sheet.Frames.Count == 12, "4 dirs x 3 frames = 12" );
			// Direction 1, frame 2: column 1, row 2.
			var anim = sheet.AnimationForDirection( 1, 4, 3, fps: 10 );
			Require( anim.Frames.Count == 3, "one animation per direction has 3 frames" );
			Require( anim.Frames[0].X == 64 && anim.Frames[0].Y == 0, "dir1 frame0" );
			Require( anim.Frames[2].X == 64 && anim.Frames[2].Y == 128, "dir1 frame2" );
			Throws<ArgumentException>( () => SpriteSheet.FromBaker( 250, 192, 64, 4, 3 ) );
			Throws<ArgumentException>( () => SpriteSheet.FromBaker( 256, 192, 64, 3, 3 ) );
		} );

		Check( "texturepacker hash trim offsets stop jitter", () =>
		{
			var json = @"{""frames"": {
				""walk0"": {""frame"": {""x"": 0, ""y"": 0, ""w"": 40, ""h"": 60}, ""trimmed"": true,
					""spriteSourceSize"": {""x"": 12, ""y"": 2, ""w"": 40, ""h"": 60},
					""sourceSize"": {""w"": 64, ""h"": 64}, ""duration"": 100},
				""walk1"": {""frame"": {""x"": 64, ""y"": 0, ""w"": 52, ""h"": 60}, ""trimmed"": true,
					""spriteSourceSize"": {""x"": 6, ""y"": 2, ""w"": 52, ""h"": 60},
					""sourceSize"": {""w"": 64, ""h"": 64}, ""duration"": 100}
			}, ""meta"": {""image"": ""walk.png"", ""size"": {""w"": 128, ""h"": 64}}}";
			var sheet = SpriteSheet.ParseAtlas( json );
			Require( sheet.ImagePath == "walk.png", "meta.image kept" );
			var f0 = sheet.FindFrame( "walk0" )!;
			var f1 = sheet.FindFrame( "walk1" )!;
			Require( f0.SourceW == 64 && f0.SourceH == 64, "untrimmed size from sourceSize" );
			Require( f0.DrawOffsetX == 12 && f0.DrawOffsetY == 2, "draw offset = spriteSourceSize" );
			Require( f1.DrawOffsetX == 6, "second frame has its own offset" );
			Require( f0.IsTrimmed && f1.IsTrimmed, "both frames trimmed" );
		} );

		Check( "texturepacker array and rotated rejection", () =>
		{
			var json = @"{""frames"": [
				{""filename"": ""a.png"", ""frame"": {""x"": 0, ""y"": 0, ""w"": 32, ""h"": 32}, ""duration"": 80},
				{""filename"": ""b.png"", ""frame"": {""x"": 32, ""y"": 0, ""w"": 32, ""h"": 32}, ""duration"": 80}
			], ""meta"": {""size"": {""w"": 64, ""h"": 32}}}";
			var sheet = SpriteSheet.ParseAtlas( json );
			Require( sheet.Frames.Count == 2 && sheet.Frames[1].Name == "b.png", "array frames keep filenames" );
			var rotated = @"{""frames"": {""r"": {""frame"": {""x"": 0, ""y"": 0, ""w"": 32, ""h"": 32}, ""rotated"": true}},
				""meta"": {""size"": {""w"": 64, ""h"": 32}}}";
			Throws<NotSupportedException>( () => SpriteSheet.ParseAtlas( rotated ) );
			var outside = @"{""frames"": {""o"": {""frame"": {""x"": 60, ""y"": 0, ""w"": 32, ""h"": 32}}},
				""meta"": {""size"": {""w"": 64, ""h"": 32}}}";
			Throws<ArgumentException>( () => SpriteSheet.ParseAtlas( outside ) );
		} );

		Check( "aseprite tags forward reverse pingpong repeat", () =>
		{
			var json = @"{""frames"": {
				""f0"": {""frame"": {""x"": 0, ""y"": 0, ""w"": 16, ""h"": 16}, ""duration"": 100},
				""f1"": {""frame"": {""x"": 16, ""y"": 0, ""w"": 16, ""h"": 16}, ""duration"": 100},
				""f2"": {""frame"": {""x"": 32, ""y"": 0, ""w"": 16, ""h"": 16}, ""duration"": 100},
				""f3"": {""frame"": {""x"": 48, ""y"": 0, ""w"": 16, ""h"": 16}, ""duration"": 100}
			}, ""meta"": {""size"": {""w"": 64, ""h"": 16}, ""frameTags"": [
				{""name"": ""walk"", ""from"": 0, ""to"": 2, ""direction"": ""forward"", ""repeat"": 0},
				{""name"": ""idle"", ""from"": 3, ""to"": 3, ""direction"": ""forward"", ""repeat"": 0},
				{""name"": ""bounce"", ""from"": 0, ""to"": 2, ""direction"": ""pingpong"", ""repeat"": 1},
				{""name"": ""rewind"", ""from"": 0, ""to"": 2, ""direction"": ""reverse"", ""repeat"": 0}
			]}}";
			var sheet = SpriteSheet.ParseAtlas( json );
			Require( sheet.Tags.Count == 4, "four tags parsed" );
			var walk = sheet.TagAnimation( "walk" );
			Require( walk.Frames.Count == 3 && walk.LoopMode == SpriteLoopMode.Loop, "walk is loop over 0..2" );
			var bounce = sheet.TagAnimation( "bounce" );
			Require( bounce.LoopMode == SpriteLoopMode.PingPong && bounce.Repeat == 1, "bounce is one pingpong cycle" );
			Require( bounce.FrameIndexAt( 0 ) == 0 && bounce.FrameIndexAt( 150 ) == 1
				&& bounce.FrameIndexAt( 250 ) == 2 && bounce.FrameIndexAt( 350 ) == 1
				&& bounce.FrameIndexAt( 450 ) == 1, "pingpong ends on last sequence frame, finished" );
			var rewind = sheet.TagAnimation( "rewind" );
			Require( rewind.FrameIndexAt( 0 ) == 2 && rewind.FrameIndexAt( 150 ) == 1 && rewind.FrameIndexAt( 250 ) == 0,
				"reverse plays 2,1,0" );
			Throws<ArgumentException>( () => sheet.TagAnimation( "missing" ) );
		} );

		Check( "per-frame durations fps default and holds", () =>
		{
			var frames = new[]
			{
				new SpriteFrame( "a", 0, 0, 16, 16, 100 ),
				new SpriteFrame( "b", 16, 0, 16, 16, 300 ),
				new SpriteFrame( "c", 32, 0, 16, 16, 100 ),
			}.ToList();
			var anim = new SpriteAnimation( "t", frames, SpriteLoopMode.Loop, fps: 10 );
			Require( anim.FrameIndexAt( 0 ) == 0 && anim.FrameIndexAt( 99 ) == 0, "first frame 0..99" );
			Require( anim.FrameIndexAt( 150 ) == 1 && anim.FrameIndexAt( 399 ) == 1, "long middle frame holds" );
			Require( anim.FrameIndexAt( 450 ) == 2 && anim.FrameIndexAt( 550 ) == 0, "loop wraps after 500ms total" );
			anim.AddHold( 0, 100 );
			Require( anim.FrameIndexAt( 150 ) == 0, "hold extends first frame to 200ms" );
			Require( anim.TotalDurationMs == int.MaxValue, "looping animation is not finite" );

			var untimed = SpriteSheet.FromGrid( 64, 16, 16, 16, fps: 10 );
			var untimedAnim = new SpriteAnimation( "u", untimed.Frames, SpriteLoopMode.Loop, fps: 10 );
			Require( untimedAnim.DefaultDurationMs == 100, "fps 10 gives 100ms default" );
			Require( untimedAnim.FrameIndexAt( 250 ) == 2, "untimed frames use fps default" );
		} );

		Check( "loop pingpong reverse once frame math", () =>
		{
			var frames = Enumerable.Range( 0, 3 ).Select( i => new SpriteFrame( $"f{i}", i * 16, 0, 16, 16, 100 ) ).ToList();
			var loop = new SpriteAnimation( "l", frames, SpriteLoopMode.Loop, fps: 10 );
			Require( loop.FrameIndexAt( 350 ) == 0, "loop wraps" );
			var ping = new SpriteAnimation( "p", frames, SpriteLoopMode.PingPong, fps: 10 );
			Require( string.Join( ",", ping.SequenceIndices() ) == "0,1,2,1", "pingpong sequence has no doubled endpoints" );
			Require( ping.FrameIndexAt( 0 ) == 0 && ping.FrameIndexAt( 150 ) == 1 && ping.FrameIndexAt( 250 ) == 2
				&& ping.FrameIndexAt( 350 ) == 1 && ping.FrameIndexAt( 450 ) == 0, "pingpong walks back down" );
			var rev = new SpriteAnimation( "r", frames, SpriteLoopMode.Reverse, fps: 10 );
			Require( rev.FrameIndexAt( 0 ) == 2 && rev.FrameIndexAt( 150 ) == 1, "reverse starts at last" );
			var once = new SpriteAnimation( "o", frames, SpriteLoopMode.Once, fps: 10 );
			Require( once.FrameIndexAt( 9999 ) == 2, "once clamps on last frame" );
			Require( once.FrameIndexAt( -50 ) == 0, "negative time shows first frame" );
			Throws<InvalidOperationException>( () => new SpriteAnimation( "e", new(), SpriteLoopMode.Loop ).FrameIndexAt( 0 ) );
		} );

		Check( "playback play pause step seek finished", () =>
		{
			var frames = Enumerable.Range( 0, 3 ).Select( i => new SpriteFrame( $"f{i}", i * 16, 0, 16, 16, 100 ) ).ToList();
			var play = new SpritePlayback( new SpriteAnimation( "l", frames, SpriteLoopMode.Loop, fps: 10 ) );
			play.Play();
			play.Advance( 150 );
			Require( play.CurrentFrameIndex == 1, "advance moves to frame 1" );
			play.Pause();
			play.Advance( 500 );
			Require( play.CurrentFrameIndex == 1, "paused playback does not advance" );
			play.StepForward();
			Require( play.CurrentFrameIndex == 2, "step forward" );
			play.StepForward();
			Require( play.CurrentFrameIndex == 0, "looping step wraps" );
			play.StepBack();
			Require( play.CurrentFrameIndex == 2, "looping step back wraps to last" );
			play.Seek( 50 );
			Require( play.CurrentFrameIndex == 0, "seek to 50ms" );

			var once = new SpritePlayback( new SpriteAnimation( "o", frames, SpriteLoopMode.Once, fps: 10 ) );
			once.Play();
			once.Advance( 1000 );
			Require( once.Finished && once.CurrentFrameIndex == 2 && !once.Playing, "once finishes and stops on last frame" );
		} );

		Check( "slices keys pivot and frame lookup", () =>
		{
			var json = @"{""frames"": {
				""f0"": {""frame"": {""x"": 0, ""y"": 0, ""w"": 16, ""h"": 16}, ""duration"": 100},
				""f1"": {""frame"": {""x"": 16, ""y"": 0, ""w"": 16, ""h"": 16}, ""duration"": 100},
				""f2"": {""frame"": {""x"": 32, ""y"": 0, ""w"": 16, ""h"": 16}, ""duration"": 100}
			}, ""meta"": {""size"": {""w"": 48, ""h"": 16}, ""slices"": [
				{""name"": ""hitbox"", ""keys"": [
					{""frame"": 0, ""bounds"": {""x"": 2, ""y"": 2, ""w"": 12, ""h"": 12}, ""pivot"": {""x"": 0.5, ""y"": 1}},
					{""frame"": 2, ""bounds"": {""x"": 0, ""y"": 0, ""w"": 16, ""h"": 16}, ""pivot"": {""x"": 0.5, ""y"": 0.5}}
				]}
			]}}";
			var sheet = SpriteSheet.ParseAtlas( json );
			var slice = sheet.Slices.Single( s => s.Name == "hitbox" );
			Require( slice.BoundsAt( 1 )!.W == 12, "frame 1 still uses frame-0 key" );
			Require( slice.BoundsAt( 2 )!.W == 16, "frame 2 uses frame-2 key" );
			Require( slice.BoundsAt( 0 )!.PivotY == 1, "pivot from key kept" );
			Require( sheet.Slices.Single().BoundsAt( 0 ) is not null, "slice present" );
		} );

		Check( "json roundtrip preserves frames tags trim", () =>
		{
			var json = @"{""frames"": {
				""w0"": {""frame"": {""x"": 0, ""y"": 0, ""w"": 40, ""h"": 60}, ""trimmed"": true,
					""spriteSourceSize"": {""x"": 12, ""y"": 2, ""w"": 40, ""h"": 60},
					""sourceSize"": {""w"": 64, ""h"": 64}, ""duration"": 120}
			}, ""meta"": {""image"": ""w.png"", ""size"": {""w"": 64, ""h"": 64}, ""frameTags"": [
				{""name"": ""w"", ""from"": 0, ""to"": 0, ""direction"": ""forward"", ""repeat"": 2}
			]}}";
			var sheet = SpriteSheet.ParseAtlas( json );
			var again = SpriteSheet.ParseAtlas( sheet.ToJsonAtlas() );
			var f = again.FindFrame( "w0" )!;
			Require( f.DrawOffsetX == 12 && f.DurationMs == 120 && f.SourceW == 64, "frame survives roundtrip" );
			Require( again.FindTag( "w" )!.Repeat == 2, "tag repeat survives roundtrip" );
			Require( again.ImagePath == "w.png" && again.Width == 64, "meta survives roundtrip" );
		} );

		Check( "validation rejects bad sheets tags and grids", () =>
		{
			Throws<ArgumentException>( () => SpriteSheet.FromGrid( 0, 64, 16, 16 ) );
			Throws<ArgumentException>( () => SpriteSheet.FromGrid( 64, 64, 0, 16 ) );
			var badTag = @"{""frames"": {""f0"": {""frame"": {""x"": 0, ""y"": 0, ""w"": 16, ""h"": 16}}},
				""meta"": {""size"": {""w"": 16, ""h"": 16}, ""frameTags"": [
					{""name"": ""bad"", ""from"": 0, ""to"": 5, ""direction"": ""forward""}
				]}}";
			Throws<ArgumentException>( () => SpriteSheet.ParseAtlas( badTag ) );
			var badDir = @"{""frames"": {""f0"": {""frame"": {""x"": 0, ""y"": 0, ""w"": 16, ""h"": 16}}},
				""meta"": {""size"": {""w"": 16, ""h"": 16}, ""frameTags"": [
					{""name"": ""bad"", ""from"": 0, ""to"": 0, ""direction"": ""sideways""}
				]}}";
			Throws<ArgumentException>( () => SpriteSheet.ParseAtlas( badDir ) );
		} );
	}
}
