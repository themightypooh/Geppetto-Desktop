using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>How an NLA strip's pose lands on the pose under it.</summary>
public enum NlaBlendMode
{
	/// <summary>The strip's pose replaces the pose under it, by effective weight (fades included).</summary>
	Replace,

	/// <summary>The strip's motion away from its first key is added on top, as <see cref="ClipLayer.Compose"/> does.</summary>
	Additive
}

/// <summary>
/// One Blender-style NLA action strip: an action clip (<see cref="RigAnimFile"/>) placed on a
/// timeline, with its own start/end, weight, fades, time scale, loop flag and bone mask.
///
/// NON-DESTRUCTIVE, unlike <see cref="ClipLayer.Merge"/>: strips never rewrite the actions they
/// reference, so the same action can sit on several strips (or several timelines) and stay
/// editable. Evaluation is a pure function of the timeline frame.
///
/// TIME: timeline frame f plays the action at (f - Start) * TimeScale. A looping strip repeats
/// over its explicit [Start, End] span; a one-shot strip only counts inside its own span and
/// contributes nothing outside it — the same rule ClipLayer.LayerFrame uses, extended with fades.
/// A strip with End &lt;= Start takes its end from the action: Start + span / TimeScale, so a
/// plain placed action just plays once, start to finish.
/// </summary>
public sealed class NlaStrip
{
	public string Name = "";
	public RigAnimFile Action;
	public float Start;
	public float End;
	public float Weight = 1f;
	public float FadeIn;
	public float FadeOut;
	public NlaBlendMode Mode = NlaBlendMode.Replace;
	public bool Loop;
	public float TimeScale = 1f;
	public bool Muted;

	/// <summary>Bone names to take from the action, MotionLint globs ("arm_*"); empty = all.</summary>
	public List<string> Mask = new();

	/// <summary>The action's span in its own frames: its last keyed frame (0 for an empty action).</summary>
	public float ActionSpan => Action?.LastKeyFrame ?? 0;

	/// <summary>The timeline frame the strip stops at: the explicit End, or one action span past Start.</summary>
	public float EffectiveEnd
	{
		get
		{
			if ( End > Start )
				return End;

			var rate = TimeScale > 0f ? TimeScale : 1f;
			return Start + ActionSpan / rate;
		}
	}

	/// <summary>The action frame the timeline frame plays, or null where the strip is not playing.</summary>
	public float? LocalFrame( float frame )
	{
		if ( Muted || Action is null )
			return null;

		if ( frame < Start - 1e-4f || frame > EffectiveEnd + 1e-4f )
			return null;

		var rate = TimeScale > 0f ? TimeScale : 1f;
		var t = (frame - Start) * rate;
		var span = ActionSpan;

		if ( Loop )
		{
			if ( span <= 0f )
				return 0f;

			t %= span;
			return t < 0f ? t + span : t;
		}

		if ( t < -1e-4f || t > span + 1e-4f )
			return null;

		return Math.Clamp( t, 0f, span );
	}

	/// <summary>Effective weight at a timeline frame: base weight times the fade ramps, 0 outside the strip.</summary>
	public float WeightAt( float frame )
	{
		if ( LocalFrame( frame ) is null )
			return 0f;

		var up = FadeIn > 0f ? Smooth( (frame - Start) / FadeIn ) : 1f;
		var down = FadeOut > 0f ? Smooth( (EffectiveEnd - frame) / FadeOut ) : 1f;
		return Math.Clamp( Weight, 0f, 1f ) * MathF.Min( up, down );
	}

	static float Smooth( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - 2f * t);
	}

	/// <summary>Problems that make this strip contribute nothing, for a loud report instead of a silent gap.</summary>
	public List<string> Validate()
	{
		var notes = new List<string>();

		if ( Muted )
			notes.Add( $"{Name}: muted" );

		if ( Action is null )
			notes.Add( $"{Name}: no action" );
		else if ( Action.Tracks.All( t => t.Kind != RigTrackKind.Bone || t.Keys.Count == 0 ) )
			notes.Add( $"{Name}: action keys no bone track" );

		if ( TimeScale <= 0f )
			notes.Add( $"{Name}: time scale {TimeScale} is not positive, treated as 1" );

		if ( Weight < 0f || Weight > 1f )
			notes.Add( $"{Name}: weight {Weight} is outside 0..1, clamped" );

		return notes;
	}
}

/// <summary>
/// A Blender-style NLA timeline: an optional base action plus a stack of strips, bottom to top.
/// Evaluation returns one local pose per bone, so it feeds anything that reads a pose per frame —
/// including <see cref="Bake"/>, which writes the timeline out as an ordinary keyed clip.
///
/// WHAT IT DOES NOT DO, on purpose: it does not know about skeletons, so a bone the base never
/// keys and no strip touches has no pose here (callers that need a full skeleton fill from bind
/// pose, as ClipLayer.Merge does with its skeleton argument). It evaluates plain track keys, not
/// Lane K channel curves — <see cref="RigTrack.Evaluate(float)"/>, the same call ClipLayer uses —
/// so a strip over a channel-curved action samples its keys, not its curves. IK goals, cameras and
/// morphs are not strip targets: strips drive Bone tracks only.
/// </summary>
public sealed class NlaTimeline
{
	/// <summary>Played under every strip at the timeline frame itself; null means untouched bones stay at identity.</summary>
	public RigAnimFile Base;

	public List<NlaStrip> Strips = new();

	/// <summary>All bone names the timeline can pose: base tracks plus every strip action's bone tracks.</summary>
	public List<string> BoneNames()
	{
		var names = new List<string>();

		void Add( RigAnimFile clip )
		{
			if ( clip is null )
				return;

			foreach ( var track in clip.Tracks )
			{
				if ( track.Kind == RigTrackKind.Bone && track.Keys.Count > 0 && !names.Contains( track.Name ) )
					names.Add( track.Name );
			}
		}

		Add( Base );

		foreach ( var strip in Strips )
			Add( strip.Action );

		return names;
	}

	static RigTrack BoneTrack( RigAnimFile clip, string bone ) =>
		clip?.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.Bone && t.Name == bone );

	/// <summary>One local pose per bone at a timeline frame. Strips apply bottom to top; a muted,
	/// out-of-range, or masked-out strip contributes nothing and never errors.</summary>
	public Dictionary<string, RigPose> Evaluate( float frame )
	{
		var poses = new Dictionary<string, RigPose>( StringComparer.Ordinal );

		foreach ( var bone in BoneNames() )
		{
			var pose = BoneTrack( Base, bone )?.Evaluate( frame ) ?? RigPose.Identity;

			foreach ( var strip in Strips )
			{
				if ( strip.Muted || strip.Action is null )
					continue;

				var weight = strip.WeightAt( frame );

				if ( weight <= 0f )
					continue;

				var track = BoneTrack( strip.Action, bone );

				if ( track is null || track.Keys.Count == 0 )
					continue;

				if ( strip.Mask.Count > 0 && !MotionLint.Matches( bone, strip.Mask ) )
					continue;

				var local = strip.LocalFrame( frame );

				if ( local is null )
					continue;

				var layer = track.Evaluate( local.Value );

				pose = strip.Mode == NlaBlendMode.Replace
					? RigPose.Lerp( pose, layer, weight )
					: ClipLayer.Compose( pose, track.Keys[0].Local, layer, weight, RigLayerMode.Additive );
			}

			poses[bone] = pose;
		}

		return poses;
	}

	/// <summary>Every problem in every strip, for a loud report instead of a silent gap.</summary>
	public List<string> Validate()
	{
		var notes = new List<string>();

		foreach ( var strip in Strips )
			notes.AddRange( strip.Validate() );

		return notes;
	}

	/// <summary>
	/// Bake the timeline to an ordinary keyed clip over [startFrame, endFrame]: one Monotone key
	/// per frame per bone, the same shape ClipLayer's bake writes, so the result feeds export,
	/// take checks and the editor unchanged. FrameRate comes from the base action, else 30.
	/// </summary>
	public RigAnimFile Bake( int startFrame, int endFrame )
	{
		if ( endFrame < startFrame )
			throw new ArgumentException( "end frame is before start frame" );

		var clip = new RigAnimFile
		{
			FrameRate = Base is not null && Base.FrameRate > 0 ? Base.FrameRate : 30,
			FrameCount = endFrame - startFrame + 1
		};

		var bones = BoneNames();
		var tracks = bones.ToDictionary( b => b, b => new RigTrack { Kind = RigTrackKind.Bone, Name = b }, StringComparer.Ordinal );

		foreach ( var track in tracks.Values )
			clip.Tracks.Add( track );

		for ( var f = startFrame; f <= endFrame; f++ )
		{
			var poses = Evaluate( f );

			foreach ( var bone in bones )
				tracks[bone].Keys.Add( new RigKey { Frame = f, Local = poses[bone], Ease = RigEase.Monotone } );
		}

		return clip;
	}
}
// NOTE 2026-10-25 port: s&box NlaTrack.cs also defined PoseLibrary; desktop already has Rig/PoseLibrary.cs, so that duplicate class was omitted here.
