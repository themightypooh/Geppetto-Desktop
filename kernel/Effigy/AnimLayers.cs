using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// One Maya-style animation layer: a clip played over the stack under it, with its own
/// mode, weight, mute/solo and ghost flags, time offset/rate/loop and bone mask.
///
/// This is NOT <see cref="ClipLayer"/> (a one-shot baked merge) and NOT an
/// <see cref="NlaStrip"/> (an action placed on a timeline): a layer is a persistent,
/// live, non-destructive override that stays editable while the base clip underneath
/// stays untouched. The blend math is ClipLayer's — <see cref="ClipLayer.Compose"/> —
/// so a live layer and a baked layer agree by construction.
/// </summary>
public sealed class AnimLayer
{
	public string Name = "";
	public RigAnimFile Clip;
	public RigLayerMode Mode = RigLayerMode.Overlay;
	public float Weight = 1f;
	public bool Muted;
	public bool Solo;

	/// <summary>Show this layer's pose as a ghost while another layer is soloed or the
	/// base is previewed, so the animator can see what the layer adds without turning it on.</summary>
	public bool Ghost;

	public int Offset;
	public float Rate = 1f;
	public bool Loop;

	/// <summary>Bone names to take from the clip, MotionLint globs ("arm_*"); empty = all.</summary>
	public List<string> Mask = new();

	/// <summary>The action's span in its own frames: its last keyed frame (0 for an empty action).</summary>
	public float ActionSpan => Clip?.LastKeyFrame ?? 0;

	/// <summary>The clip frame the timeline frame plays, or null where the layer is not
	/// playing. Same rule as <see cref="NlaStrip.LocalFrame"/>: a looping layer repeats
	/// over its span, a one-shot layer only counts inside its own span.</summary>
	public float? LocalFrame( float frame )
	{
		if ( Muted || Clip is null )
			return null;

		var rate = Rate > 0f ? Rate : 1f;
		var t = (frame - Offset) * rate;
		var span = ActionSpan;

		if ( Loop )
		{
			if ( span <= 0 )
				return 0f;

			t %= span;
			return t < 0f ? t + span : t;
		}

		if ( t < -1e-4f || t > span + 1e-4f )
			return null;

		return Math.Clamp( t, 0f, span );
	}

	/// <summary>Effective weight at a timeline frame: base weight clamped to 0..1, 0 where
	/// the layer is muted or not playing.</summary>
	public float EffectiveWeight( float frame )
	{
		if ( LocalFrame( frame ) is null )
			return 0f;

		return Math.Clamp( Weight, 0f, 1f );
	}

	/// <summary>Problems that make this layer contribute nothing, for a loud report instead
	/// of a silent gap.</summary>
	public List<string> Validate()
	{
		var notes = new List<string>();

		if ( Muted )
			notes.Add( $"{Name}: muted" );

		if ( Clip is null )
			notes.Add( $"{Name}: no clip" );
		else if ( Clip.Tracks.All( t => t.Kind != RigTrackKind.Bone || t.Keys.Count == 0 ) )
			notes.Add( $"{Name}: clip keys no bone track" );

		if ( Rate <= 0f )
			notes.Add( $"{Name}: rate {Rate} is not positive, treated as 1" );

		if ( Weight < 0f || Weight > 1f )
			notes.Add( $"{Name}: weight {Weight} is outside 0..1, clamped" );

		return notes;
	}
}

/// <summary>
/// A live animation layer stack: a base clip with a stack of <see cref="AnimLayer"/>s on
/// top, bottom to top. Evaluation returns one local pose per bone, so it feeds anything
/// that reads a pose per frame — including <see cref="SmartBake"/>, which writes the
/// stack out as an ordinary keyed clip.
///
/// WHAT IT DOES NOT DO, on purpose: it does not know about skeletons, so a bone the base
/// never keys and no layer touches has no pose here (callers that need a full skeleton
/// fill from bind pose, as ClipLayer.Merge does with its skeleton argument). It evaluates
/// plain track keys, not Lane K channel curves — <see cref="RigTrack.Evaluate(float)"/>,
/// the same call NlaTimeline uses — so a layer over a channel-curved clip samples its
/// keys, not its curves. IK goals, cameras and morphs are not layer targets: layers
/// drive Bone tracks only, the same limit NlaTimeline states.
/// </summary>
public sealed class AnimLayerStack
{
	/// <summary>Played under every layer at the timeline frame itself; null means untouched
	/// bones stay at identity.</summary>
	public RigAnimFile Base;

	public List<AnimLayer> Layers = new();

	/// <summary>All bone names the stack can pose: base tracks plus every layer clip's bone tracks.</summary>
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

		foreach ( var layer in Layers )
			Add( layer.Clip );

		return names;
	}

	static RigTrack BoneTrack( RigAnimFile clip, string bone ) =>
		clip?.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.Bone && t.Name == bone );

	/// <summary>One local pose per bone at a timeline frame. Layers apply bottom to top; a
	/// muted, out-of-range, or masked-out layer contributes nothing and never errors. When
	/// any layer is soloed, only soloed layers apply.</summary>
	public Dictionary<string, RigPose> Evaluate( float frame )
	{
		var poses = new Dictionary<string, RigPose>( StringComparer.Ordinal );
		var anySolo = Layers.Any( l => l.Solo );

		foreach ( var bone in BoneNames() )
		{
			var pose = BoneTrack( Base, bone )?.Evaluate( frame ) ?? RigPose.Identity;

			foreach ( var layer in Layers )
			{
				if ( layer.Muted || layer.Clip is null )
					continue;

				if ( anySolo && !layer.Solo )
					continue;

				var weight = layer.EffectiveWeight( frame );

				if ( weight <= 0f )
					continue;

				var track = BoneTrack( layer.Clip, bone );

				if ( track is null || track.Keys.Count == 0 )
					continue;

				if ( layer.Mask.Count > 0 && !MotionLint.Matches( bone, layer.Mask ) )
					continue;

				var local = layer.LocalFrame( frame );

				if ( local is null )
					continue;

				var layerPose = track.Evaluate( local.Value );
				pose = ClipLayer.Compose( pose, track.Keys[0].Local, layerPose, weight, layer.Mode );
			}

			poses[bone] = pose;
		}

		return poses;
	}

	/// <summary>The ghosted layers' own poses at a timeline frame, one per bone: what the
	/// animator sees while another layer is soloed or the base is previewed. Later ghost
	/// layers win a bone, the same order evaluation uses.</summary>
	public Dictionary<string, RigPose> GhostPoses( float frame )
	{
		var ghosts = new Dictionary<string, RigPose>( StringComparer.Ordinal );

		foreach ( var layer in Layers )
		{
			if ( !layer.Ghost || layer.Muted || layer.Clip is null )
				continue;

			var local = layer.LocalFrame( frame );

			if ( local is null )
				continue;

			foreach ( var track in layer.Clip.Tracks )
			{
				if ( track.Kind != RigTrackKind.Bone || track.Keys.Count == 0 )
					continue;

				ghosts[track.Name] = track.Evaluate( local.Value );
			}
		}

		return ghosts;
	}

	/// <summary>Every problem in every layer, for a loud report instead of a silent gap.</summary>
	public List<string> Validate()
	{
		var notes = new List<string>();

		foreach ( var layer in Layers )
			notes.AddRange( layer.Validate() );

		return notes;
	}

	/// <summary>
	/// Smart Bake: merge the stack into an ordinary keyed clip — the base deep-copied,
	/// then every active layer merged onto it in order with <see cref="ClipLayer.Merge"/>
	/// — so the baked clip evaluates to the same pose the live stack does, and the base
	/// and layer clips are never altered. Muted layers are skipped; when any layer is
	/// soloed, only soloed layers bake. The returned notes name every layer merge that
	/// contributed nothing (a layer with no motion, a mask matching nothing) instead of
	/// failing the whole bake for one quiet layer.
	///
	/// The baked clip carries Monotone keys per frame per touched bone, the same shape
	/// ClipLayer's bake writes, so the result feeds export, take checks and the editor
	/// unchanged. Time-independent rig data (IK, limits, collision, drivers,
	/// constraints, goal spaces, objects, props, root motion) is shared with the base,
	/// not copied, exactly as ClipTimeRemap.Bake does: sampling never mutates it.
	/// </summary>
	public (RigAnimFile Clip, List<string> Notes) SmartBake()
	{
		if ( Base is null )
			throw new ArgumentException( "A layer stack needs a base clip to bake onto." );

		var baked = CopyClip( Base );
		var notes = new List<string>();
		var anySolo = Layers.Any( l => l.Solo );

		foreach ( var layer in Layers )
		{
			if ( layer.Muted || layer.Clip is null )
				continue;

			if ( anySolo && !layer.Solo )
				continue;

			var result = ClipLayer.Merge( baked, layer.Clip, new ClipLayer.Settings
			{
				Mode = layer.Mode,
				Weight = layer.Weight,
				Offset = layer.Offset,
				Rate = layer.Rate,
				Loop = layer.Loop,
				Mask = new List<string>( layer.Mask ),
			} );

			notes.AddRange( result.Notes );

			if ( result.Error is not null )
				notes.Add( $"{layer.Name}: {result.Error}" );
		}

		return (baked, notes);
	}

	/// <summary>Deep copy of a clip's keyed data. Track keys, channel curves, modifiers,
	/// morph events and marks are copied value by value; rig data is shared by reference,
	/// as ClipTimeRemap.Bake does.</summary>
	static RigAnimFile CopyClip( RigAnimFile source )
	{
		var copy = new RigAnimFile
		{
			SourceModel = source.SourceModel,
			RigAsset = source.RigAsset,
			FrameRate = source.FrameRate,
			FrameCount = source.FrameCount,
			PlayInPlace = source.PlayInPlace,
			RootMotion = source.RootMotion,
			Ik = source.Ik,
			Limits = source.Limits,
			Collision = source.Collision,
			Drivers = source.Drivers,
			Constraints = source.Constraints,
			GoalSpaces = source.GoalSpaces,
			Objects = source.Objects,
			Props = source.Props,
		};

		foreach ( var track in source.Tracks )
		{
			var t = new RigTrack { Name = track.Name, Kind = track.Kind };

			foreach ( var k in track.Keys )
				t.Keys.Add( new RigKey
				{
					Frame = k.Frame,
					Local = k.Local,
					Ease = k.Ease,
					TangentIn = (float[])k.TangentIn?.Clone(),
					TangentOut = (float[])k.TangentOut?.Clone(),
				} );

			foreach ( var (channel, curve) in track.ChannelCurves )
				t.ChannelCurves[channel] = curve.Select( c => new ScalarKey( c.Frame, c.Value ) ).ToList();

			foreach ( var m in track.Modifiers )
				t.Modifiers.Add( new FCurveModifier( m.Kind, m.Amplitude, m.Period, m.Phase, m.Steps ) );

			copy.Tracks.Add( t );
		}

		foreach ( var e in source.MorphEvents )
			copy.MorphEvents.Add( new RigMorphEvent
			{
				Morph = e.Morph,
				Start = e.Start,
				End = e.End,
				FadeIn = e.FadeIn,
				FadeOut = e.FadeOut,
				Value = e.Value,
			} );

		if ( source.Marks is not null )
			copy.Marks = source.Marks.Where( m => m is not null ).Select( m => m.Clone() ).ToList();

		return copy;
	}
}
