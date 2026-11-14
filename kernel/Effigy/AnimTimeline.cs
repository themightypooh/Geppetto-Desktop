using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// The one shared playhead for the Graph + Dope + NLA triad: the Dope sheet, the Graph
/// editor and the NLA strips all read this frame, so scrubbing in one editor never leaves
/// the others behind. The frame is a float (fractional-frame scrubbing is real), the
/// range is whole frames, and the step size is the stepped-preview step: 1 plays every
/// frame, 2 plays on-2s, 3 on-3s.
///
/// Looping wraps modulo the span, so the end frame is the loop point: stepping past End
/// lands back near Start. A non-looping playhead clamps at the ends instead.
/// </summary>
public sealed class AnimPlayhead
{
	public float Frame;
	public float FrameRate = 30f;
	public int StartFrame;
	public int EndFrame;
	public bool Loop;
	public int StepFrames = 1;

	float Rate => FrameRate > 0f && float.IsFinite( FrameRate ) ? FrameRate : 30f;

	/// <summary>The playhead position in seconds.</summary>
	public float TimeSeconds => Frame / Rate;

	/// <summary>The frame playing at <paramref name="seconds"/>.</summary>
	public float FrameAt( float seconds ) => seconds * Rate;

	/// <summary>Move the playhead to a frame, clamped to the range (or wrapped when looping).</summary>
	public void Seek( float frame )
	{
		Frame = ClampOrWrap( frame );
	}

	/// <summary>Advance one stepped-preview step (StepFrames frames).</summary>
	public void StepForward() => Seek( Frame + StepFrames );

	/// <summary>Step back one stepped-preview step.</summary>
	public void StepBack() => Seek( Frame - StepFrames );

	float ClampOrWrap( float frame )
	{
		if ( EndFrame <= StartFrame )
			return Math.Clamp( frame, StartFrame, Math.Max( StartFrame, EndFrame ) );

		if ( !Loop )
			return Math.Clamp( frame, StartFrame, EndFrame );

		var span = EndFrame - StartFrame;
		var wrapped = (frame - StartFrame) % span;
		if ( wrapped < 0f )
			wrapped += span;

		return StartFrame + wrapped;
	}

	/// <summary>Problems with this playhead's settings, for a loud report. A playhead
	/// sitting outside its range is a note, not an error — scrubbing past the end and
	/// coming back is ordinary.</summary>
	public List<string> Validate()
	{
		var notes = new List<string>();

		if ( FrameRate <= 0f || !float.IsFinite( FrameRate ) )
			notes.Add( $"frame rate {FrameRate} is not positive, treated as 30" );

		if ( EndFrame < StartFrame )
			notes.Add( $"end frame {EndFrame} is before start frame {StartFrame}" );

		if ( StepFrames < 1 )
			notes.Add( $"step {StepFrames} is below 1, treated as 1" );

		if ( Frame < StartFrame || Frame > EndFrame )
			notes.Add( $"playhead frame {Frame} is outside {StartFrame}..{EndFrame}" );

		return notes;
	}
}

/// <summary>
/// A keying set: a named group of tracks (and, optionally, one channel of a track) that
/// the animator keys together with one action — "all arm bones", "face", "left hand" —
/// instead of hunting tracks one by one. Keying a whole track writes the pose the track
/// already has at the frame (as ClipKeyEditor.Insert does), so the motion is unchanged
/// until the key is moved; keying a channel writes that channel's current value.
/// </summary>
public sealed class AnimKeyingSet
{
	/// <summary>One entry: a track by name, and the channel of it to key — null means the
	/// whole track (a pose key).</summary>
	public readonly record struct KeyingEntry( string Track, RigChannel? Channel );

	public string Name = "";
	public List<KeyingEntry> Entries = new();

	public sealed class KeyingResult
	{
		public int KeysWritten;
		public List<string> Notes = new();
	}

	/// <summary>Key every entry at <paramref name="frame"/> on <paramref name="clip"/>.
	/// A key already at the frame is refreshed in place (its value replaced, its ease
	/// kept); a missing track is reported in <see cref="KeyingResult.Notes"/>, never
	/// silently skipped. Returns how many keys were written.</summary>
	public KeyingResult KeyAt( float frame, RigAnimFile clip )
	{
		ArgumentNullException.ThrowIfNull( clip );

		var result = new KeyingResult();
		var frameInt = Math.Max( 0, (int)MathF.Round( frame ) );

		foreach ( var entry in Entries )
		{
			var track = clip.Tracks.FirstOrDefault( t => t.Name == entry.Track );

			if ( track is null )
			{
				result.Notes.Add( $"{entry.Track}: no such track" );
				continue;
			}

			if ( entry.Channel is null )
			{
				var pose = track.Evaluate( frame );
				var existing = track.Keys.FirstOrDefault( k => k.Frame == frameInt );

				if ( existing is not null )
					existing.Local = pose;
				else
				{
					track.Keys.Add( new RigKey { Frame = frameInt, Local = pose, Ease = EaseOfSegment( track, frameInt ) } );
					track.Keys.Sort( ( a, b ) => a.Frame.CompareTo( b.Frame ) );
				}

				result.KeysWritten++;
			}
			else
			{
				var channel = entry.Channel.Value;
				var value = track.ChannelValue( channel, frame ) ?? ChannelValueFromPose( track, frame, channel );

				if ( !track.ChannelCurves.TryGetValue( channel, out var curve ) || curve is null )
				{
					curve = new List<ScalarKey>();
					track.ChannelCurves[channel] = curve;
				}

				var existing = curve.FirstOrDefault( k => k.Frame == frameInt );

				if ( existing is not null )
					existing.Value = value;
				else
				{
					curve.Add( new ScalarKey( frameInt, value ) );
					curve.Sort( ( a, b ) => a.Frame.CompareTo( b.Frame ) );
				}

				result.KeysWritten++;
			}
		}

		return result;
	}

	/// <summary>The ease governing the segment a new key at <paramref name="frame"/> splits:
	/// the ease of the key at or before it, Linear when there is none. RigTrack.Evaluate
	/// reads the segment from its left key, so the split key inherits that key's ease.</summary>
	static RigEase EaseOfSegment( RigTrack track, int frame )
	{
		var ease = RigEase.Linear;

		foreach ( var k in track.Keys )
		{
			if ( k.Frame <= frame )
				ease = k.Ease;
			else
				break;
		}

		return ease;
	}

	/// <summary>One channel's current value from the evaluated pose, with the same
	/// extraction <see cref="RigTrack.EvaluateWithChannels"/> uses: positions and scales
	/// straight off the pose, Euler angles through Xform.ToEulerXyz in degrees.</summary>
	static float ChannelValueFromPose( RigTrack track, float frame, RigChannel channel )
	{
		var pose = track.EvaluateWithChannels( frame );

		return channel switch
		{
			RigChannel.PosX => pose.Position.x,
			RigChannel.PosY => pose.Position.y,
			RigChannel.PosZ => pose.Position.z,
			RigChannel.ScaleX => pose.Scale.x,
			RigChannel.ScaleY => pose.Scale.y,
			RigChannel.ScaleZ => pose.Scale.z,
			RigChannel.EulerX or RigChannel.EulerY or RigChannel.EulerZ => EulerOf( pose, channel ),
			_ => 0f,
		};
	}

	static float EulerOf( RigPose pose, RigChannel channel )
	{
		var euler = pose.Rotation.ToXform( Vec3.Zero, Vec3.One ).ToEulerXyz() * (180f / MathF.PI);
		return channel == RigChannel.EulerX ? euler.x : channel == RigChannel.EulerY ? euler.y : euler.z;
	}
}

/// <summary>
/// A playblast plan: the schedule for a review render — frames per second (24/30/60),
/// frame size (720p by default), the frame range, and whether camera keys and audio sync
/// along. The plan enumerates every frame exactly once, so a 10-second shot at 24 fps is
/// 240 frames with no dropped frames in the schedule.
///
/// The plan is deliberately NOT the render: the kernel has no video encoder and no GL
/// context. Capturing the frames runs through the headless render path and encoding them
/// to MP4/WebM belongs to the export layer; this plan is what both are handed, and its
/// Validate is what refuses a bad plan before any frame is rendered.
/// </summary>
public sealed class PlayblastPlan
{
	public float Fps = 24f;
	public int Width = 1280;
	public int Height = 720;
	public int StartFrame;
	public int EndFrame = 239;

	/// <summary>Play the shot's audio in sync with the pictures, when the shot has any.</summary>
	public bool AudioSync;

	/// <summary>The camera shot to render (a camera track name); empty means the orbit camera.</summary>
	public string CameraShot = "";

	/// <summary>How many frames the plan renders: End - Start + 1, 0 when the range is empty.</summary>
	public int FrameCount => Math.Max( 0, EndFrame - StartFrame + 1 );

	/// <summary>The shot's length in seconds.</summary>
	public float DurationSeconds => Fps > 0f ? FrameCount / Fps : 0f;

	/// <summary>Every frame in the plan, in order, each exactly once.</summary>
	public IEnumerable<int> Frames()
	{
		for ( var f = StartFrame; f <= EndFrame; f++ )
			yield return f;
	}

	/// <summary>The time in seconds at which <paramref name="frame"/> plays.</summary>
	public float FrameTime( int frame ) => (frame - StartFrame) / (Fps > 0f ? Fps : 24f);

	/// <summary>Problems that make this plan unrenderable, for a loud report before any
	/// frame is rendered. Odd widths/heights are refused because video encoders need
	/// even dimensions; anything else is a note the caller can fix.</summary>
	public List<string> Validate()
	{
		var notes = new List<string>();

		if ( Fps is not 24f and not 30f and not 60f )
			notes.Add( $"fps {Fps} is not one of 24/30/60" );

		if ( Width <= 0 || Height <= 0 )
			notes.Add( $"size {Width}x{Height} is not positive" );
		else if ( Width % 2 != 0 || Height % 2 != 0 )
			notes.Add( $"size {Width}x{Height} has an odd dimension; encoders need even dimensions" );

		if ( EndFrame < StartFrame )
			notes.Add( $"end frame {EndFrame} is before start frame {StartFrame}" );

		if ( FrameCount == 0 )
			notes.Add( "the plan renders zero frames" );

		return notes;
	}

	/// <summary>A 10-second shot at 720p: 240 frames at 24 fps, 300 at 30, 600 at 60.</summary>
	public static PlayblastPlan TenSeconds720p( float fps = 24f ) => new()
	{
		Fps = fps,
		Width = 1280,
		Height = 720,
		StartFrame = 0,
		EndFrame = (int)(fps * 10f) - 1,
	};
}
