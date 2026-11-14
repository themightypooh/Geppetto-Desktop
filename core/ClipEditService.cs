#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Reversible clip-key editing operations adhering to E2 edit history.
/// Manages insert, replace, delete, move, and IK goal operations, maintaining
/// stable frame ordering, tangent arrays, ease modes, and clip length policies.
/// </summary>
public static class ClipEditService
{
	public static RigKey CloneKey( RigKey key ) => new()
	{
		Frame = key.Frame,
		Local = key.Local,
		Ease = key.Ease,
		TangentIn = (float[]?)key.TangentIn?.Clone(),
		TangentOut = (float[]?)key.TangentOut?.Clone()
	};

	public static void ValidatePose( int frame, RigPose pose )
	{
		if ( frame < 0 ) throw new ArgumentOutOfRangeException( nameof( frame ), "Key frame cannot be negative." );
		if ( !float.IsFinite( pose.Position.x ) || !float.IsFinite( pose.Position.y ) || !float.IsFinite( pose.Position.z ) )
			throw new ArgumentException( "Pose position coordinates must be finite numbers." );
		if ( !float.IsFinite( pose.Rotation.x ) || !float.IsFinite( pose.Rotation.y ) || !float.IsFinite( pose.Rotation.z ) || !float.IsFinite( pose.Rotation.w ) )
			throw new ArgumentException( "Pose rotation coordinates must be finite numbers." );
		if ( !float.IsFinite( pose.Scale.x ) || !float.IsFinite( pose.Scale.y ) || !float.IsFinite( pose.Scale.z ) )
			throw new ArgumentException( "Pose scale coordinates must be finite numbers." );
	}

	static RigTrack GetOrCreateTrack( RigAnimFile clip, RigTrackKind kind, string targetName, out bool created )
	{
		var track = clip.Tracks.FirstOrDefault( t => t.Kind == kind && t.Name == targetName );
		if ( track is null )
		{
			track = new RigTrack { Kind = kind, Name = targetName };
			clip.Tracks.Add( track );
			created = true;
		}
		else
		{
			created = false;
		}
		return track;
	}

	public static void InsertOrReplaceKey(
		DocumentSession session,
		RigTrackKind kind,
		string targetName,
		int frame,
		RigPose pose,
		RigEase ease = RigEase.Smooth,
		float[]? tangentIn = null,
		float[]? tangentOut = null,
		MarionettePlayer? player = null ) =>
		session.History.Execute( BuildInsertOrReplaceKey(
			session, kind, targetName, frame, pose, ease, tangentIn, tangentOut, player ) );

	/// <summary>Builds the key command without executing it, so a caller can join it into an
	/// existing transaction and commit the key with the pose override as one undo entry.</summary>
	public static IEditCommand BuildInsertOrReplaceKey(
		DocumentSession session,
		RigTrackKind kind,
		string targetName,
		int frame,
		RigPose pose,
		RigEase ease = RigEase.Smooth,
		float[]? tangentIn = null,
		float[]? tangentOut = null,
		MarionettePlayer? player = null )
	{
		ArgumentNullException.ThrowIfNull( session );
		if ( string.IsNullOrWhiteSpace( targetName ) ) throw new ArgumentException( "Target name cannot be empty." );
		var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );

		ValidatePose( frame, pose );

		var newKey = new RigKey
		{
			Frame = frame,
			Local = pose,
			Ease = ease,
			TangentIn = (float[]?)tangentIn?.Clone(),
			TangentOut = (float[]?)tangentOut?.Clone()
		};

		var track = clip.Tracks.FirstOrDefault( t => t.Kind == kind && t.Name == targetName );
		bool trackExisted = track is not null;
		int existingIdx = track?.Keys.FindIndex( k => k.Frame == frame ) ?? -1;
		RigKey? replacedKey = (track is not null && existingIdx >= 0) ? CloneKey( track.Keys[existingIdx] ) : null;

		int initialFrameCount = clip.FrameCount;
		int newFrameCount = frame >= initialFrameCount ? frame + 1 : initialFrameCount;

		var cmd = new DelegateEditCommand(
			$"Insert key {kind}/{targetName} at frame {frame}",
			execute: () =>
			{
				var t = GetOrCreateTrack( clip, kind, targetName, out _ );
				int idx = t.Keys.FindIndex( k => k.Frame == frame );
				if ( idx >= 0 )
				{
					t.Keys[idx] = CloneKey( newKey );
				}
				else
				{
					t.Keys.Add( CloneKey( newKey ) );
					t.Keys.Sort( ( a, b ) => a.Frame.CompareTo( b.Frame ) );
				}
				clip.FrameCount = newFrameCount;
				player?.InvalidateClipCache( preservePlayhead: true );
			},
			undo: () =>
			{
				var t = clip.Tracks.FirstOrDefault( tr => tr.Kind == kind && tr.Name == targetName );
				if ( t is not null )
				{
					if ( replacedKey is not null )
					{
						int idx = t.Keys.FindIndex( k => k.Frame == frame );
						if ( idx >= 0 ) t.Keys[idx] = CloneKey( replacedKey );
					}
					else
					{
						t.Keys.RemoveAll( k => k.Frame == frame );
					}
				}
				clip.FrameCount = initialFrameCount;
				player?.InvalidateClipCache( preservePlayhead: true );
			}
		);

		return cmd;
	}

	public static void DeleteKey(
		DocumentSession session,
		RigTrackKind kind,
		string targetName,
		int frame,
		MarionettePlayer? player = null )
	{
		ArgumentNullException.ThrowIfNull( session );
		var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
		if ( frame < 0 ) throw new ArgumentOutOfRangeException( nameof( frame ), "Key frame cannot be negative." );

		var track = clip.Tracks.FirstOrDefault( t => t.Kind == kind && t.Name == targetName )
			?? throw new InvalidOperationException( $"Track {kind}/{targetName} not found." );

		int idx = track.Keys.FindIndex( k => k.Frame == frame );
		if ( idx < 0 ) throw new InvalidOperationException( $"No key on track {kind}/{targetName} at frame {frame}." );

		var deletedKey = CloneKey( track.Keys[idx] );

		var cmd = new DelegateEditCommand(
			$"Delete key {kind}/{targetName} at frame {frame}",
			execute: () =>
			{
				var t = clip.Tracks.FirstOrDefault( tr => tr.Kind == kind && tr.Name == targetName );
				t?.Keys.RemoveAll( k => k.Frame == frame );
				player?.InvalidateClipCache( preservePlayhead: true );
			},
			undo: () =>
			{
				var t = GetOrCreateTrack( clip, kind, targetName, out _ );
				t.Keys.Add( CloneKey( deletedKey ) );
				t.Keys.Sort( ( a, b ) => a.Frame.CompareTo( b.Frame ) );
				player?.InvalidateClipCache( preservePlayhead: true );
			}
		);

		session.History.Execute( cmd );
	}

	public static void MoveKey(
		DocumentSession session,
		RigTrackKind kind,
		string targetName,
		int fromFrame,
		int toFrame,
		MarionettePlayer? player = null )
	{
		ArgumentNullException.ThrowIfNull( session );
		var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
		if ( fromFrame < 0 || toFrame < 0 ) throw new ArgumentOutOfRangeException( "Frames cannot be negative." );
		if ( fromFrame == toFrame ) return; // No-op

		var track = clip.Tracks.FirstOrDefault( t => t.Kind == kind && t.Name == targetName )
			?? throw new InvalidOperationException( $"Track {kind}/{targetName} not found." );

		int fromIdx = track.Keys.FindIndex( k => k.Frame == fromFrame );
		if ( fromIdx < 0 ) throw new InvalidOperationException( $"No key on track {kind}/{targetName} at frame {fromFrame}." );

		var movingKey = CloneKey( track.Keys[fromIdx] );
		int toIdx = track.Keys.FindIndex( k => k.Frame == toFrame );
		RigKey? displacedKey = toIdx >= 0 ? CloneKey( track.Keys[toIdx] ) : null;

		int initialFrameCount = clip.FrameCount;
		int newFrameCount = toFrame >= initialFrameCount ? toFrame + 1 : initialFrameCount;

		var cmd = new DelegateEditCommand(
			$"Move key {kind}/{targetName} from {fromFrame} to {toFrame}",
			execute: () =>
			{
				var t = clip.Tracks.FirstOrDefault( tr => tr.Kind == kind && tr.Name == targetName );
				if ( t is null ) return;
				t.Keys.RemoveAll( k => k.Frame == fromFrame || k.Frame == toFrame );
				var placedKey = CloneKey( movingKey );
				placedKey.Frame = toFrame;
				t.Keys.Add( placedKey );
				t.Keys.Sort( ( a, b ) => a.Frame.CompareTo( b.Frame ) );
				clip.FrameCount = newFrameCount;
				player?.InvalidateClipCache( preservePlayhead: true );
			},
			undo: () =>
			{
				var t = clip.Tracks.FirstOrDefault( tr => tr.Kind == kind && tr.Name == targetName );
				if ( t is null ) return;
				t.Keys.RemoveAll( k => k.Frame == toFrame || k.Frame == fromFrame );
				var restoredMoving = CloneKey( movingKey );
				restoredMoving.Frame = fromFrame;
				t.Keys.Add( restoredMoving );
				if ( displacedKey is not null )
				{
					t.Keys.Add( CloneKey( displacedKey ) );
				}
				t.Keys.Sort( ( a, b ) => a.Frame.CompareTo( b.Frame ) );
				clip.FrameCount = initialFrameCount;
				player?.InvalidateClipCache( preservePlayhead: true );
			}
		);

		session.History.Execute( cmd );
	}

	public static void SetIkGoals(
		DocumentSession session,
		string boneName,
		IEnumerable<ClipKeys.Target> targets,
		MarionettePlayer? player = null ) =>
		session.History.Execute( BuildSetIkGoals( session, boneName, targets, player ) );

	/// <summary>Builds the IK goal/weight command without executing it, so a caller can join it
	/// into an existing transaction and commit the goal with the drag as one undo entry.</summary>
	public static IEditCommand BuildSetIkGoals(
		DocumentSession session,
		string boneName,
		IEnumerable<ClipKeys.Target> targets,
		MarionettePlayer? player = null )
	{
		ArgumentNullException.ThrowIfNull( session );
		var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
		var targetList = targets?.ToList() ?? throw new ArgumentNullException( nameof( targets ) );

		foreach ( var t in targetList )
		{
			if ( t.Frame < 0 ) throw new ArgumentOutOfRangeException( nameof( targets ), "Target frame cannot be negative." );
			if ( !float.IsFinite( t.Position.x ) || !float.IsFinite( t.Position.y ) || !float.IsFinite( t.Position.z ) )
				throw new ArgumentException( "IK goal position must be finite." );
			if ( t.Rotation is { } rot && (!float.IsFinite( rot.x ) || !float.IsFinite( rot.y ) || !float.IsFinite( rot.z ) || !float.IsFinite( rot.w )) )
				throw new ArgumentException( "IK goal rotation must be finite." );
		}

		var goalTrack = clip.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.IkGoal && t.Name == boneName );
		var weightTrack = clip.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.IkWeight && t.Name == boneName );

		var oldGoalKeys = goalTrack?.Keys.Select( CloneKey ).ToList() ?? new List<RigKey>();
		var oldWeightKeys = weightTrack?.Keys.Select( CloneKey ).ToList() ?? new List<RigKey>();
		int initialFrameCount = clip.FrameCount;

		int maxTargetFrame = targetList.Count > 0 ? targetList.Max( t => t.Frame ) : 0;
		int newFrameCount = maxTargetFrame >= initialFrameCount ? maxTargetFrame + 1 : initialFrameCount;

		var cmd = new DelegateEditCommand(
			$"Set IK goals for {boneName}",
			execute: () =>
			{
				ClipKeys.Goals( clip, boneName, targetList );
				clip.FrameCount = newFrameCount;
				player?.InvalidateClipCache( preservePlayhead: true );
			},
			undo: () =>
			{
				var gt = clip.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.IkGoal && t.Name == boneName );
				if ( gt is not null )
				{
					gt.Keys.Clear();
					gt.Keys.AddRange( oldGoalKeys.Select( CloneKey ) );
				}
				var wt = clip.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.IkWeight && t.Name == boneName );
				if ( wt is not null )
				{
					wt.Keys.Clear();
					wt.Keys.AddRange( oldWeightKeys.Select( CloneKey ) );
				}
				clip.FrameCount = initialFrameCount;
				player?.InvalidateClipCache( preservePlayhead: true );
			}
		);

		return cmd;
	}
// Lane K additions are guarded: they need Lane K kernel types (RigChannel,
// FCurveModifier, ScalarKey, RigTrack.ChannelCurves/EvaluateWithChannels) that
// the desktop kernel mirror (kernel/Effigy) does not have until Lane P syncs
// RigAnimFile.cs/ClipKeys.cs into it. Lane P: the #if/#endif pair was deleted at sync (wave 2 cleanup).
	/// <summary>Lane K: key one scalar channel curve, as an undoable command. The
	/// sampling stays in the kernel (<see cref="RigTrack.ChannelValue"/>); this only
	/// writes the key and remembers the old curve for undo.</summary>
	public static void SetChannelKey(
		DocumentSession session,
		string boneName,
		RigChannel channel,
		int frame,
		float value,
		MarionettePlayer? player = null ) =>
		session.History.Execute( BuildSetChannelKey( session, boneName, channel, frame, value, player ) );

	public static IEditCommand BuildSetChannelKey(
		DocumentSession session,
		string boneName,
		RigChannel channel,
		int frame,
		float value,
		MarionettePlayer? player = null )
	{
		ArgumentNullException.ThrowIfNull( session );
		var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
		if ( string.IsNullOrWhiteSpace( boneName ) ) throw new ArgumentException( "Target name cannot be empty." );
		if ( frame < 0 ) throw new ArgumentOutOfRangeException( nameof( frame ), "Key frame cannot be negative." );
		if ( !float.IsFinite( value ) ) throw new ArgumentException( "Channel value must be finite." );

		var track = clip.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.Bone && t.Name == boneName );
		bool trackExisted = track is not null;
		List<ScalarKey>? oldKeys = null;

		if ( track is not null && track.ChannelCurves.TryGetValue( channel, out var existing ) && existing is not null )
			oldKeys = existing.Select( k => new ScalarKey( k.Frame, k.Value ) ).ToList();

		int initialFrameCount = clip.FrameCount;

		return new DelegateEditCommand(
			$"Set channel key {boneName}.{channel} at frame {frame}",
			execute: () =>
			{
				ClipKeys.SetChannelKey( clip, boneName, channel, frame, value );
				player?.InvalidateClipCache( preservePlayhead: true );
			},
			undo: () =>
			{
				var t = clip.Tracks.FirstOrDefault( tr => tr.Kind == RigTrackKind.Bone && tr.Name == boneName );

				if ( !trackExisted )
				{
					if ( t is not null )
						clip.Tracks.Remove( t );
				}
				else if ( t is not null )
				{
					if ( oldKeys is null )
						t.ChannelCurves.Remove( channel );
					else
						t.ChannelCurves[channel] = oldKeys.Select( k => new ScalarKey( k.Frame, k.Value ) ).ToList();
				}

				clip.FrameCount = initialFrameCount;
				player?.InvalidateClipCache( preservePlayhead: true );
			}
		);
	}

	/// <summary>Lane K: bake a bone's channel curves into ordinary keys, as an undoable
	/// command. Undo restores the keys, curves, modifiers and frame count exactly.</summary>
	public static void BakeChannels(
		DocumentSession session,
		string boneName,
		MarionettePlayer? player = null ) =>
		session.History.Execute( BuildBakeChannels( session, boneName, player ) );

	public static IEditCommand BuildBakeChannels(
		DocumentSession session,
		string boneName,
		MarionettePlayer? player = null )
	{
		ArgumentNullException.ThrowIfNull( session );
		var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );

		var track = clip.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.Bone && t.Name == boneName )
			?? throw new InvalidOperationException( $"Track Bone/{boneName} not found." );

		var oldKeys = track.Keys.Select( CloneKey ).ToList();
		var oldCurves = track.ChannelCurves.ToDictionary(
			kvp => kvp.Key,
			kvp => kvp.Value.Select( k => new ScalarKey( k.Frame, k.Value ) ).ToList() );
		var oldModifiers = track.Modifiers.Select( m => new FCurveModifier( m.Kind, m.Amplitude, m.Period, m.Phase, m.Steps ) ).ToList();
		int initialFrameCount = clip.FrameCount;

		return new DelegateEditCommand(
			$"Bake channels {boneName}",
			execute: () =>
			{
				ClipKeys.BakeChannels( clip, boneName );
				player?.InvalidateClipCache( preservePlayhead: true );
			},
			undo: () =>
			{
				var t = clip.Tracks.FirstOrDefault( tr => tr.Kind == RigTrackKind.Bone && tr.Name == boneName );

				if ( t is not null )
				{
					t.Keys.Clear();
					t.Keys.AddRange( oldKeys.Select( CloneKey ) );
					t.ChannelCurves.Clear();

					foreach ( var kvp in oldCurves )
						t.ChannelCurves[kvp.Key] = kvp.Value.Select( k => new ScalarKey( k.Frame, k.Value ) ).ToList();

					t.Modifiers.Clear();
					t.Modifiers.AddRange( oldModifiers.Select( m => new FCurveModifier( m.Kind, m.Amplitude, m.Period, m.Phase, m.Steps ) ) );
				}

				clip.FrameCount = initialFrameCount;
				player?.InvalidateClipCache( preservePlayhead: true );
			}
		);
	}

	/// <summary>Lane K: add a channel-curve modifier to a bone track, as an undoable command.</summary>
	public static void AddModifier(
		DocumentSession session,
		string boneName,
		FCurveModifier modifier,
		MarionettePlayer? player = null ) =>
		session.History.Execute( BuildAddModifier( session, boneName, modifier, player ) );

	public static IEditCommand BuildAddModifier(
		DocumentSession session,
		string boneName,
		FCurveModifier modifier,
		MarionettePlayer? player = null )
	{
		ArgumentNullException.ThrowIfNull( session );
		ArgumentNullException.ThrowIfNull( modifier );
		var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );

		var track = clip.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.Bone && t.Name == boneName );
		bool trackExisted = track is not null;
		var oldModifiers = track?.Modifiers.Select( m => new FCurveModifier( m.Kind, m.Amplitude, m.Period, m.Phase, m.Steps ) ).ToList()
			?? new List<FCurveModifier>();

		return new DelegateEditCommand(
			$"Add channel modifier {boneName}.{modifier.Kind}",
			execute: () =>
			{
				var t = GetOrCreateTrack( clip, RigTrackKind.Bone, boneName, out _ );
				t.Modifiers.Add( new FCurveModifier( modifier.Kind, modifier.Amplitude, modifier.Period, modifier.Phase, modifier.Steps ) );
				player?.InvalidateClipCache( preservePlayhead: true );
			},
			undo: () =>
			{
				var t = clip.Tracks.FirstOrDefault( tr => tr.Kind == RigTrackKind.Bone && tr.Name == boneName );

				if ( !trackExisted )
				{
					if ( t is not null )
						clip.Tracks.Remove( t );
				}
				else if ( t is not null )
				{
					t.Modifiers.Clear();
					t.Modifiers.AddRange( oldModifiers.Select( m => new FCurveModifier( m.Kind, m.Amplitude, m.Period, m.Phase, m.Steps ) ) );
				}

				player?.InvalidateClipCache( preservePlayhead: true );
			}
		);
	}

	/// <summary>Time remap: bake the session's clip into a new clip. The session's clip is the
	/// source and is left exactly as it was; the baked clip is returned, not installed.</summary>
	public static RigAnimFile BakeRemappedClip( DocumentSession session, ClipTimeRemap remap )
	{
		ArgumentNullException.ThrowIfNull( session );
		ArgumentNullException.ThrowIfNull( remap );
		var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
		return remap.Bake( clip );
	}

	/// <summary>Time remap: write the session's clip, baked through <paramref name="remap"/>, as
	/// .riganim JSON. The session's retained source JSON is the base, so the baked file keeps the
	/// clip's non-track data the way <see cref="RigAnimWriter"/> preserves it.</summary>
	public static string ExportRemappedClip( DocumentSession session, ClipTimeRemap remap )
	{
		var baked = BakeRemappedClip( session, remap );
		return RigAnimWriter.Write( baked, session.ClipSourceJson );
	}

	/// <summary>
	/// Time remap, baked: a NEW clip whose keys play the source clip through
	/// <paramref name="remap"/>, without altering the source clip's keys.
	/// The bake itself lives on the remap (<see cref="ClipTimeRemap.Bake"/>); this is the
	/// desktop-side entry point so callers do not reach into the kernel type's members.
	/// </summary>
	public static RigAnimFile BakeRemappedClip( RigAnimFile source, ClipTimeRemap remap )
	{
		ArgumentNullException.ThrowIfNull( source );
		ArgumentNullException.ThrowIfNull( remap );
		return remap.Bake( source );
	}
// ---- Animation-operation reconciliation (Mobile AnimOps / AnimSessionOps) ----
// Inventory, checked against Mobile app/AnimOps.cs, AnimSessionOps.cs, AnimDoc.cs,
// AnimClips.cs and the Desktop paths named in the ticket:
//
//   Timeline edits: insert/replace/delete/move key, SetEase -> ClipEditService /
//   ClipKeyEditor (parity). Reverse / Retime / Reduce / Offset / MakeLoop /
//   Copy / Paste / Cut -> MISSING, implemented below as undoable session ops.
//   Tangents: Mobile clears stored tangents for "auto" and writes zeroes for
//   "flat"; the auto value itself is AnimKeys.AutoTangent, mirrored here.
//   Noise = PerlinNoise FCurveModifier via AddModifier (parity); Smooth has no
//   Desktop equivalent and stays explicit (see the report).
//   IK / limits: clip.Ik / clip.Limits are READ by playback (IkTargetService,
//   LiveIkSolverBuffers, MarionettePlayer) but nothing on Desktop edited them.
//   Implemented below on the session clip. They are rig-owned data
//   (RigAnimFile.UseRig reads them from the .ctrlrig); persisting a
//   session-added constraint into the rig file is a rig-save follow-up, not
//   this file. HideBone has no Desktop home (no HiddenBones set) and is not
//   implemented. LookAt / ResetIkHandle are included (ResetIkHandle keys the
//   goal from the clip's own FK pose at the session playhead).
//   Marks: RigAnimWriter writes clip.Marks, so session marks round-trip.
//   Morph events: RigAnimFile.MorphWeightsAt consumes them, but RigAnimWriter
//   does NOT write them (only base-JSON passthrough) - a kernel-writer gap,
//   flagged in the report; the session ops below are still the real path.
//   Prop events / reference props / lights / objects / cameras: no Desktop
//   home (no AnimPropEvent / AnimLight types; clip.Props is a plain list) -
//   explicitly not implemented. KeyCamera is already reachable generically via
//   InsertOrReplaceKey with RigTrackKind.Camera.
//   Layers / NLA: kernel NlaTimeline.Evaluate/Bake exists with zero core
//   usage; the bot-command entry point is AnimCallers.BuildNlaClip. There is
//   no session strip store, so strip management itself is a follow-up.
//   Retarget: kernel SkeletonRetarget.RetargetClip exists with no Desktop
//   entry point; RetargetClip below is it (same shape as BakeRemappedClip).
//   Named poses: BuildScriptAnim pose_preset / pose_save / pose_apply /
//   pose_blend verbs (parity). Clip library: Mobile AnimClips persists named
//   clips; Desktop only has the per-run `clip` verb and no persistent store -
//   explicitly not implemented, needs a follow-up ticket with a chosen home.
//   Drag / mirror / pin / pose-part: PoseEditService + DragSession cover the
//   drag path; MirrorPose has no Desktop equivalent and is not implemented.
//   Playback / transport / undo / checkpoint: MarionettePlayer,
//   TimelineEventDispatcher, EditHistory (parity).
//
// Undo/redo discipline: every command below computes its plan at build time
// from the live clip, snapshots the affected state, and re-resolves tracks and
// keys BY NAME AND FRAME when it executes. Undo restores the snapshot, so a
// redo finds the keys back where the plan expects them; no execute closure
// holds a live key, track or constraint across the undo boundary.

/// <summary>One selected key, by track kind + name + frame (the same shape the
/// rest of this service uses; ClipKeyEditor.KeyRef is track-index based and
/// does not survive track edits).</summary>
public readonly record struct SelectedKey( RigTrackKind Kind, string Name, int Frame );

/// <summary>A copied key selection: keys relative to the earliest copied frame,
/// so a paste can land them at any target frame. Produced by CopyKeys / CutKeys,
/// consumed by PasteKeys. Plain data - the caller holds it, this service keeps
/// no clipboard state.</summary>
public sealed class KeyClipboard
{
	public IReadOnlyList<CopiedKey> Entries { get; }
	public int MinFrame { get; }
	public KeyClipboard( IReadOnlyList<CopiedKey> entries, int minFrame ) { Entries = entries; MinFrame = minFrame; }
}

/// <summary>One copied key: its track, its frame offset from the clipboard's
/// earliest frame, and a clone of the key itself.</summary>
public sealed record CopiedKey( RigTrackKind Kind, string Name, int FrameOffset, RigKey Key );

static RigTrack? FindTrack( RigAnimFile clip, RigTrackKind kind, string name ) =>
	clip.Tracks.FirstOrDefault( t => t.Kind == kind && t.Name == name );

static RigKey? FindKey( RigAnimFile clip, RigTrackKind kind, string name, int frame ) =>
	FindTrack( clip, kind, name )?.Keys.FirstOrDefault( k => k.Frame == frame );

static List<RigKey> CloneKeys( RigTrack track ) => track.Keys.Select( CloneKey ).ToList();

static void RestoreKeys( RigTrack track, List<RigKey> keys )
{
	track.Keys.Clear();
	track.Keys.AddRange( keys.Select( CloneKey ) );
}

/// <summary>Resolve a selection to live (track, key) pairs, in track order.
/// Throws when a named track or frame has no key - a transform must not
/// silently skip part of the selection.</summary>
static List<(RigTrack Track, RigKey Key)> ResolveSelection( RigAnimFile clip, IReadOnlyList<SelectedKey> keys )
{
	var resolved = new List<(RigTrack, RigKey)>();
	foreach ( var (kind, name, frame) in keys )
	{
		var track = FindTrack( clip, kind, name )
			?? throw new InvalidOperationException( $"Track {kind}/{name} not found." );
		var key = track.Keys.FirstOrDefault( k => k.Frame == frame )
			?? throw new InvalidOperationException( $"No key on track {kind}/{name} at frame {frame}." );
		resolved.Add( (track, key) );
	}
	return resolved;
}

static Dictionary<RigTrack, List<RigKey>> SnapshotTracks( IEnumerable<RigTrack> tracks ) =>
	tracks.Distinct().ToDictionary( t => t, CloneKeys );

static void RestoreTracks( Dictionary<RigTrack, List<RigKey>> snapshot )
{
	foreach ( var (track, keys) in snapshot )
		RestoreKeys( track, keys );
}

static RigEase FlipEase( RigEase ease ) => ease switch
{
	RigEase.EaseIn => RigEase.EaseOut,
	RigEase.EaseOut => RigEase.EaseIn,
	_ => ease
};

/// <summary>Degrees between two rotations - Mobile AnimKeys.AngleBetween,
/// formula for formula.</summary>
static float AngleBetween( Quat a, Quat b )
{
	var d = MathF.Abs( a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w );
	return 2f * MathF.Acos( Math.Clamp( d, 0f, 1f ) ) * 180f / MathF.PI;
}

static bool NearPose( RigPose a, RigPose b, float positionTolerance, float angleToleranceDegrees, float scaleTolerance ) =>
	(a.Position - b.Position).Length <= positionTolerance
	&& AngleBetween( a.Rotation, b.Rotation ) <= angleToleranceDegrees
	&& (a.Scale - b.Scale).Length <= scaleTolerance;

/// <summary>Mobile AnimKeyOps.Settle: after keys move, a moved key wins its
/// frame over any unmoved key there; the track stays sorted.</summary>
static void SettleMovedKeys( RigTrack track, IReadOnlyList<RigKey> moved )
{
	var winner = new Dictionary<int, RigKey>();
	foreach ( var key in moved )
		winner[key.Frame] = key;
	track.Keys.RemoveAll( k => winner.TryGetValue( k.Frame, out var kept ) && kept != k );
	track.Keys.Sort( ( a, b ) => a.Frame.CompareTo( b.Frame ) );
}

static string UniqueIkName( IEnumerable<string> taken, string baseName )
{
	var set = new HashSet<string>( taken );
	if ( !set.Contains( baseName ) )
		return baseName;
	for ( var n = 2; ; n++ )
		if ( !set.Contains( $"{baseName} {n}" ) )
			return $"{baseName} {n}";
}

static RigLimit CloneLimit( RigLimit limit ) => new()
{
	Name = limit.Name, Bone = limit.Bone, Weight = limit.Weight,
	Enabled = limit.Enabled, Min = limit.Min, Max = limit.Max
};

static RigMorphEvent CloneMorphEvent( RigMorphEvent e ) => new()
{
	Morph = e.Morph, Start = e.Start, End = e.End,
	FadeIn = e.FadeIn, FadeOut = e.FadeOut, Value = e.Value
};

static List<RigIk>? CloneIkList( List<RigIk>? list ) => list?.Select( c => c.Clone() ).ToList();
static List<RigLimit>? CloneLimitList( List<RigLimit>? list ) => list?.Select( CloneLimit ).ToList();
static List<RigAnimMark>? CloneMarkList( List<RigAnimMark>? list ) => list?.Select( m => m.Clone() ).ToList();
static List<RigMorphEvent> CloneMorphEventList( List<RigMorphEvent> list ) => list.Select( CloneMorphEvent ).ToList();

static int BoneDepth( Skeleton rig, int boneIndex )
{
	var depth = 0;
	for ( var b = rig.Bones[boneIndex].Parent; b >= 0; b = rig.Bones[b].Parent )
		depth++;
	return depth;
}

/// <summary>Build (not yet add) an IK constraint, Mobile AnimSessionOps.AddIk
/// semantics: one per end bone, chain length clamped to the bone's depth when
/// a rig is given (1..8 otherwise), live + enabled + weight 1, unique name.
/// Pass <paramref name="rig"/> when the caller has one; without it the bone
/// is not validated against a skeleton and the chain is clamped to 1..8.</summary>
static RigIk CreateIk( RigAnimFile clip, string endBone, int chainLength, string? name, Skeleton? rig )
{
	if ( string.IsNullOrWhiteSpace( endBone ) )
		throw new ArgumentException( "End bone cannot be empty." );
	if ( clip.Ik is { } existing && existing.Any( c => c.EndBone == endBone ) )
		throw new InvalidOperationException( $"{endBone} already ends an IK chain." );
	int n;
	if ( rig is not null )
	{
		var i = rig.IndexOf( endBone );
		if ( i < 0 )
			throw new InvalidOperationException( $"skeleton has no bone '{endBone}' for IK." );
		n = Math.Clamp( chainLength, 1, Math.Max( BoneDepth( rig, i ), 1 ) );
	}
	else
	{
		n = Math.Clamp( chainLength, 1, 8 );
	}
	var taken = clip.Ik is { } list ? list.Select( c => c.Name ) : Enumerable.Empty<string>();
	return new RigIk
	{
		Name = UniqueIkName( taken, string.IsNullOrWhiteSpace( name ) ? "IK" : name ),
		EndBone = endBone, ChainLength = n, Live = true, Enabled = true, Weight = 1f
	};
}

/// <summary>Reverse the selected keys about their own span, Mobile
/// AnimKeyOps.Reverse: frames mirror about [first, last], EaseIn/Out flip,
/// tangents are swapped and negated. A selection spanning one frame is
/// refused - there is nothing to reverse.</summary>
public static void ReverseKeys(
	DocumentSession session,
	IReadOnlyList<SelectedKey> keys,
	MarionettePlayer? player = null ) =>
	session.History.Execute( BuildReverseKeys( session, keys, player ) );

public static IEditCommand BuildReverseKeys(
	DocumentSession session,
	IReadOnlyList<SelectedKey> keys,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	if ( keys is null || keys.Count == 0 )
		throw new ArgumentException( "Reverse needs at least one selected key." );
	var resolved = ResolveSelection( clip, keys );
	var first = resolved.Min( r => r.Key.Frame );
	var last = resolved.Max( r => r.Key.Frame );
	if ( first == last )
		throw new InvalidOperationException( "Reverse needs keys over more than one frame." );

	// The plan, computed now: (track, from-frame, to-frame, ease, tangents).
	var plan = new List<(RigTrackKind Kind, string Name, int From, int To, RigEase Ease, float[]? Tin, float[]? Tout)>();
	foreach ( var group in resolved.GroupBy( r => r.Track ) )
	{
		var selected = group.Select( r => r.Key ).OrderBy( k => k.Frame ).ToList();
		var count = selected.Count;
		var easing = selected.Select( k => k.Ease ).ToArray();
		var tin = selected.Select( k => k.TangentIn ).ToArray();
		var tout = selected.Select( k => k.TangentOut ).ToArray();
		for ( var i = 0; i < count; i++ )
		{
			var key = selected[i];
			plan.Add( (group.Key.Kind, group.Key.Name, key.Frame, first + last - key.Frame,
				FlipEase( easing[i > 0 ? i - 1 : count - 1] ),
				tout[i]?.Select( v => -v ).ToArray(),
				tin[i]?.Select( v => -v ).ToArray()) );
		}
	}
	var before = SnapshotTracks( resolved.Select( r => r.Track ) );
	int initialFrameCount = clip.FrameCount;

	return new DelegateEditCommand(
		$"Reverse {resolved.Count} keys",
		execute: () =>
		{
			var moved = new List<(RigTrack Track, RigKey Key)>();
			foreach ( var (kind, name, from, to, ease, tin, tout) in plan )
			{
				var key = FindKey( clip, kind, name, from );
				if ( key is null )
					continue;
				key.Ease = ease;
				key.TangentIn = (float[]?)tin?.Clone();
				key.TangentOut = (float[]?)tout?.Clone();
				key.Frame = to;
				moved.Add( (FindTrack( clip, kind, name )!, key) );
			}
			foreach ( var track in moved.Select( m => m.Track ).Distinct() )
				SettleMovedKeys( track, moved.Where( m => m.Track == track ).Select( m => m.Key ).ToList() );
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			RestoreTracks( before );
			clip.FrameCount = initialFrameCount;
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

/// <summary>Retime the selected keys by <paramref name="factor"/> about
/// <paramref name="pivot"/> (default: the earliest selected frame), Mobile
/// AnimKeyOps.Retime: frames scale, tangents divide by the factor. A factor of
/// 1 (or not positive, or one that moves no key) is refused.</summary>
public static void RetimeKeys(
	DocumentSession session,
	IReadOnlyList<SelectedKey> keys,
	float factor,
	int? pivot = null,
	MarionettePlayer? player = null ) =>
	session.History.Execute( BuildRetimeKeys( session, keys, factor, pivot, player ) );

public static IEditCommand BuildRetimeKeys(
	DocumentSession session,
	IReadOnlyList<SelectedKey> keys,
	float factor,
	int? pivot = null,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	if ( keys is null || keys.Count == 0 )
		throw new ArgumentException( "Retime needs at least one selected key." );
	if ( !( factor > 0f ) || MathF.Abs( factor - 1f ) < 1e-4f )
		throw new ArgumentException( "Retime factor must be positive and change the timing." );
	var resolved = ResolveSelection( clip, keys );
	var origin = pivot ?? resolved.Min( r => r.Key.Frame );
	var plan = resolved
		.Select( r => (r.Track.Kind, r.Track.Name, From: r.Key.Frame, To: Math.Max( origin + (int)MathF.Round( (r.Key.Frame - origin) * factor ), 0 )) )
		.ToList();
	if ( plan.All( p => p.To == p.From ) )
		throw new InvalidOperationException( "Retime moved no keys." );

	var before = SnapshotTracks( resolved.Select( r => r.Track ) );
	int initialFrameCount = clip.FrameCount;
	int newFrameCount = Math.Max( initialFrameCount, plan.Max( p => p.To ) + 1 );

	return new DelegateEditCommand(
		$"Retime {resolved.Count} keys x{factor}",
		execute: () =>
		{
			foreach ( var (kind, name, from, to) in plan )
			{
				var key = FindKey( clip, kind, name, from );
				if ( key is null )
					continue;
				key.TangentIn = key.TangentIn?.Select( v => v / factor ).ToArray();
				key.TangentOut = key.TangentOut?.Select( v => v / factor ).ToArray();
				key.Frame = to;
			}
			foreach ( var track in before.Keys )
				track.Keys.Sort( ( a, b ) => a.Frame.CompareTo( b.Frame ) );
			clip.FrameCount = newFrameCount;
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			RestoreTracks( before );
			clip.FrameCount = initialFrameCount;
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

/// <summary>Drop interior selected keys whose removal keeps the sampled poses
/// within tolerance, Mobile AnimKeyOps.Reduce. Endpoints are never removed.
/// Nothing removable is refused. The reduction is computed at build time from
/// a scratch copy, so building never mutates the live clip.</summary>
public static void ReduceKeys(
	DocumentSession session,
	IReadOnlyList<SelectedKey> keys,
	float positionTolerance = 0.05f,
	float angleToleranceDegrees = 0.5f,
	float scaleTolerance = 0.005f,
	MarionettePlayer? player = null ) =>
	session.History.Execute( BuildReduceKeys( session, keys, positionTolerance, angleToleranceDegrees, scaleTolerance, player ) );

public static IEditCommand BuildReduceKeys(
	DocumentSession session,
	IReadOnlyList<SelectedKey> keys,
	float positionTolerance = 0.05f,
	float angleToleranceDegrees = 0.5f,
	float scaleTolerance = 0.005f,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	if ( keys is null || keys.Count == 0 )
		throw new ArgumentException( "Reduce needs at least one selected key." );
	var resolved = ResolveSelection( clip, keys );

	var toRemove = new List<(RigTrackKind Kind, string Name, int Frame)>();
	foreach ( var group in resolved.GroupBy( r => r.Track ) )
	{
		var track = group.Key;
		var selected = group.Select( r => r.Key ).OrderBy( k => k.Frame ).ToList();
		if ( selected.Count < 3 )
			continue;
		var from = selected[0].Frame;
		var to = selected[^1].Frame;
		var scratch = new RigTrack { Kind = track.Kind, Name = track.Name };
		scratch.Keys.AddRange( track.Keys.Select( CloneKey ) );
		var original = new RigPose[to - from + 1];
		for ( var f = from; f <= to; f++ )
			original[f - from] = scratch.Evaluate( f );
		foreach ( var key in selected.Skip( 1 ).Take( selected.Count - 2 ).ToList() )
		{
			var at = scratch.Keys.FindIndex( k => k.Frame == key.Frame );
			if ( at <= 0 || at >= scratch.Keys.Count - 1 )
				continue;
			scratch.Keys.RemoveAt( at );
			var lo = Math.Max( scratch.Keys[Math.Max( at - 2, 0 )].Frame, from );
			var hi = Math.Min( scratch.Keys[Math.Min( at + 1, scratch.Keys.Count - 1 )].Frame, to );
			var fits = true;
			for ( var f = lo; f <= hi && fits; f++ )
				fits = NearPose( scratch.Evaluate( f ), original[f - from], positionTolerance, angleToleranceDegrees, scaleTolerance );
			if ( fits )
			{
				toRemove.Add( (track.Kind, track.Name, key.Frame) );
				continue;
			}
			scratch.Keys.Insert( at, CloneKey( key ) );
		}
	}
	if ( toRemove.Count == 0 )
		throw new InvalidOperationException( "No key could go - every selected key carries the curve." );

	var before = SnapshotTracks( resolved.Select( r => r.Track ) );
	int initialFrameCount = clip.FrameCount;

	return new DelegateEditCommand(
		$"Reduce keys ({toRemove.Count} removed)",
		execute: () =>
		{
			foreach ( var (kind, name, frame) in toRemove )
				FindTrack( clip, kind, name )?.Keys.RemoveAll( k => k.Frame == frame );
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			RestoreTracks( before );
			clip.FrameCount = initialFrameCount;
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

/// <summary>Shift the selected keys by a per-track frame count, Mobile
/// AnimKeyOps.Offset: keys never go below frame 0. A shift that moves no key
/// is refused.</summary>
public static void OffsetKeys(
	DocumentSession session,
	IReadOnlyList<SelectedKey> keys,
	Func<RigTrack, int> framesFor,
	MarionettePlayer? player = null ) =>
	session.History.Execute( BuildOffsetKeys( session, keys, framesFor, player ) );

/// <summary>Shift every selected key by the same <paramref name="delta"/> frames.</summary>
public static void OffsetKeys(
	DocumentSession session,
	IReadOnlyList<SelectedKey> keys,
	int delta,
	MarionettePlayer? player = null ) =>
	OffsetKeys( session, keys, _ => delta, player );

public static IEditCommand BuildOffsetKeys(
	DocumentSession session,
	IReadOnlyList<SelectedKey> keys,
	Func<RigTrack, int> framesFor,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	ArgumentNullException.ThrowIfNull( framesFor );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	if ( keys is null || keys.Count == 0 )
		throw new ArgumentException( "Offset needs at least one selected key." );
	var resolved = ResolveSelection( clip, keys );
	var plan = new List<(RigTrackKind Kind, string Name, int From, int To)>();
	foreach ( var group in resolved.GroupBy( r => r.Track ) )
	{
		var by = framesFor( group.Key );
		if ( by == 0 )
			continue;
		foreach ( var (_, key) in group )
			plan.Add( (group.Key.Kind, group.Key.Name, key.Frame, Math.Max( key.Frame + by, 0 )) );
	}
	if ( plan.Count == 0 || plan.All( p => p.To == p.From ) )
		throw new InvalidOperationException( "Offset moved no keys." );

	var before = SnapshotTracks( resolved.Select( r => r.Track ) );
	int initialFrameCount = clip.FrameCount;
	int newFrameCount = Math.Max( initialFrameCount, plan.Max( p => p.To ) + 1 );

	return new DelegateEditCommand(
		$"Offset {plan.Count} keys",
		execute: () =>
		{
			foreach ( var (kind, name, from, to) in plan )
			{
				var key = FindKey( clip, kind, name, from );
				if ( key is not null )
					key.Frame = to;
			}
			foreach ( var track in before.Keys )
				track.Keys.Sort( ( a, b ) => a.Frame.CompareTo( b.Frame ) );
			clip.FrameCount = newFrameCount;
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			RestoreTracks( before );
			clip.FrameCount = initialFrameCount;
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

/// <summary>Close every track into a loop at <paramref name="lastFrame"/>,
/// Mobile AnimKeyOps.MakeLoop: keys past the loop end go, and the first key's
/// pose (with its ease and tangents) is copied to the loop end. Tracks whose
/// first key is already at or past the loop end are untouched.</summary>
public static void MakeLoop(
	DocumentSession session,
	int lastFrame,
	MarionettePlayer? player = null ) =>
	session.History.Execute( BuildMakeLoop( session, lastFrame, player ) );

public static IEditCommand BuildMakeLoop(
	DocumentSession session,
	int lastFrame,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	if ( lastFrame < 1 )
		throw new ArgumentOutOfRangeException( nameof( lastFrame ), "Loop end must be at least frame 1." );

	var affected = clip.Tracks.Where( t => t.Keys.Count > 0 ).Select( t => (t.Kind, t.Name) ).ToList();
	var before = SnapshotTracks( clip.Tracks.Where( t => t.Keys.Count > 0 ) );
	int initialFrameCount = clip.FrameCount;
	int newFrameCount = Math.Max( initialFrameCount, lastFrame + 1 );

	return new DelegateEditCommand(
		$"Make loop at frame {lastFrame}",
		execute: () =>
		{
			foreach ( var (kind, name) in affected )
			{
				var track = FindTrack( clip, kind, name );
				if ( track is null || track.Keys.Count == 0 )
					continue;
				track.Keys.Sort( ( a, b ) => a.Frame.CompareTo( b.Frame ) );
				var first = track.Keys[0];
				if ( first.Frame >= lastFrame )
					continue;
				track.Keys.RemoveAll( k => k.Frame > lastFrame );
				ClipKeys.Set( track, lastFrame, first.Local, first.Ease );
				var end = track.Keys.First( k => k.Frame == lastFrame );
				end.TangentIn = (float[]?)first.TangentIn?.Clone();
				end.TangentOut = (float[]?)first.TangentOut?.Clone();
			}
			clip.FrameCount = newFrameCount;
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			RestoreTracks( before );
			clip.FrameCount = initialFrameCount;
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

/// <summary>Copy the selected keys into a clipboard payload, Mobile
/// CopySelection: frames are stored relative to the earliest copied frame,
/// so the same payload pastes at any target frame. Pure - no history entry,
/// no session state.</summary>
public static KeyClipboard CopyKeys(
	RigAnimFile clip,
	IReadOnlyList<SelectedKey> keys )
{
	ArgumentNullException.ThrowIfNull( clip );
	if ( keys is null || keys.Count == 0 )
		throw new ArgumentException( "Copy needs at least one selected key." );
	var resolved = ResolveSelection( clip, keys );
	var min = resolved.Min( r => r.Key.Frame );
	var entries = resolved
		.Select( r => new CopiedKey( r.Track.Kind, r.Track.Name, r.Key.Frame - min, CloneKey( r.Key ) ) )
		.ToList();
	return new KeyClipboard( entries, min );
}

/// <summary>Paste a clipboard at <paramref name="targetFrame"/> as one undo
/// entry, Mobile Paste: a pasted key replaces any key already on its frame.
/// The clip grows to hold the pasted keys.</summary>
public static void PasteKeys(
	DocumentSession session,
	KeyClipboard clipboard,
	int targetFrame,
	MarionettePlayer? player = null ) =>
	session.History.Execute( BuildPasteKeys( session, clipboard, targetFrame, player ) );

public static IEditCommand BuildPasteKeys(
	DocumentSession session,
	KeyClipboard clipboard,
	int targetFrame,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	ArgumentNullException.ThrowIfNull( clipboard );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	if ( clipboard.Entries.Count == 0 )
		throw new ArgumentException( "Clipboard is empty." );
	if ( targetFrame < 0 )
		throw new ArgumentOutOfRangeException( nameof( targetFrame ), "Paste target cannot be negative." );

	var affectedKinds = clipboard.Entries.Select( e => (e.Kind, e.Name) ).Distinct().ToList();
	var beforeTracks = clip.Tracks.ToList();
	var before = new Dictionary<RigTrack, List<RigKey>>();
	foreach ( var (kind, name) in affectedKinds )
		if ( FindTrack( clip, kind, name ) is { } t )
			before[t] = CloneKeys( t );
	int initialFrameCount = clip.FrameCount;
	int newFrameCount = Math.Max( initialFrameCount, targetFrame + clipboard.Entries.Max( e => e.FrameOffset ) + 1 );

	return new DelegateEditCommand(
		$"Paste {clipboard.Entries.Count} keys at frame {targetFrame}",
		execute: () =>
		{
			foreach ( var entry in clipboard.Entries )
			{
				var track = GetOrCreateTrack( clip, entry.Kind, entry.Name, out _ );
				var key = CloneKey( entry.Key );
				key.Frame = targetFrame + entry.FrameOffset;
				var idx = track.Keys.FindIndex( k => k.Frame == key.Frame );
				if ( idx >= 0 )
					track.Keys[idx] = key;
				else
				{
					track.Keys.Add( key );
					track.Keys.Sort( ( a, b ) => a.Frame.CompareTo( b.Frame ) );
				}
			}
			clip.FrameCount = newFrameCount;
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			clip.Tracks.Clear();
			clip.Tracks.AddRange( beforeTracks );
			RestoreTracks( before );
			clip.FrameCount = initialFrameCount;
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

/// <summary>Cut the selected keys as one undo entry, Mobile Cut: returns the
/// clipboard (so the cut keys can be pasted elsewhere) and deletes them. The
/// delete is the undo entry; the returned clipboard is plain data.</summary>
public static KeyClipboard CutKeys(
	DocumentSession session,
	IReadOnlyList<SelectedKey> keys,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	var clipboard = CopyKeys( clip, keys );
	var resolved = ResolveSelection( clip, keys );
	var plan = resolved.Select( r => (r.Track.Kind, r.Track.Name, r.Key.Frame) ).ToList();
	var before = SnapshotTracks( resolved.Select( r => r.Track ) );
	int initialFrameCount = clip.FrameCount;

	session.History.Execute( new DelegateEditCommand(
		$"Cut {resolved.Count} keys",
		execute: () =>
		{
			foreach ( var (kind, name, frame) in plan )
				FindTrack( clip, kind, name )?.Keys.RemoveAll( k => k.Frame == frame );
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			RestoreTracks( before );
			clip.FrameCount = initialFrameCount;
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	) );
	return clipboard;
}

/// <summary>Write explicit Hermite tangents onto one key (null clears one side).
/// Mobile's SetSlope edits one channel of one key through AnimGraph, which
/// Desktop does not have; this whole-tangent setter is the Desktop equivalent
/// - same stored data, same effect on the curve.</summary>
public static void SetKeyTangents(
	DocumentSession session,
	RigTrackKind kind,
	string targetName,
	int frame,
	float[]? tangentIn,
	float[]? tangentOut,
	MarionettePlayer? player = null ) =>
	session.History.Execute( BuildSetKeyTangents( session, kind, targetName, frame, tangentIn, tangentOut, player ) );

public static IEditCommand BuildSetKeyTangents(
	DocumentSession session,
	RigTrackKind kind,
	string targetName,
	int frame,
	float[]? tangentIn,
	float[]? tangentOut,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	var track = FindTrack( clip, kind, targetName )
		?? throw new InvalidOperationException( $"Track {kind}/{targetName} not found." );
	if ( !track.Keys.Any( k => k.Frame == frame ) )
		throw new InvalidOperationException( $"No key on track {kind}/{targetName} at frame {frame}." );
	if ( tangentIn is { Length: var li } && li != RigHermite.Channels )
		throw new ArgumentException( $"TangentIn must have {RigHermite.Channels} channels." );
	if ( tangentOut is { Length: var lo } && lo != RigHermite.Channels )
		throw new ArgumentException( $"TangentOut must have {RigHermite.Channels} channels." );

	var before = SnapshotTracks( new[] { track } );
	var inClone = (float[]?)tangentIn?.Clone();
	var outClone = (float[]?)tangentOut?.Clone();

	return new DelegateEditCommand(
		$"Set tangents {kind}/{targetName} at frame {frame}",
		execute: () =>
		{
			var key = FindKey( clip, kind, targetName, frame );
			if ( key is null )
				return;
			key.TangentIn = (float[]?)inClone?.Clone();
			key.TangentOut = (float[]?)outClone?.Clone();
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			RestoreTracks( before );
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

/// <summary>The slope the curve would take through a key with no stored tangent,
/// Mobile AnimKeys.AutoTangent, formula for formula (Catmull-Rom when
/// <paramref name="monotone"/> is false, Fritsch-Carlson held when true).</summary>
public static float[] AutoTangent( RigTrack track, int index, bool monotone )
{
	ArgumentNullException.ThrowIfNull( track );
	var tangent = new float[RigHermite.Channels];
	var keys = track.Keys;
	if ( index < 0 || index >= keys.Count )
		return tangent;
	Span<float> v = stackalloc float[RigHermite.Channels];
	Span<float> p = stackalloc float[RigHermite.Channels];
	Span<float> n = stackalloc float[RigHermite.Channels];
	RigHermite.Pack( keys[index].Local, v );
	var hasPrev = index > 0;
	var hasNext = index + 1 < keys.Count;
	if ( hasPrev ) { RigHermite.Pack( keys[index - 1].Local, p ); RigHermite.Align( v, p ); }
	if ( hasNext ) { RigHermite.Pack( keys[index + 1].Local, n ); RigHermite.Align( v, n ); }
	for ( var c = 0; c < RigHermite.Channels; c++ )
		tangent[c] = RigHermite.Slope( monotone, hasPrev ? keys[index - 1].Frame : null, p[c], keys[index].Frame, v[c], hasNext ? keys[index + 1].Frame : null, n[c] );
	return tangent;
}

/// <summary>Clear the stored tangents on the selected keys, Mobile
/// AutoTangents: a key with no stored tangents is evaluated with the automatic
/// slope above, so clearing is the auto-tangent operation.</summary>
public static void AutoTangents(
	DocumentSession session,
	IReadOnlyList<SelectedKey> keys,
	MarionettePlayer? player = null ) =>
	session.History.Execute( BuildAutoTangents( session, keys, player ) );

public static IEditCommand BuildAutoTangents(
	DocumentSession session,
	IReadOnlyList<SelectedKey> keys,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	var resolved = ResolveSelection( clip, keys );
	var plan = resolved.Select( r => (r.Track.Kind, r.Track.Name, r.Key.Frame) ).ToList();
	var before = SnapshotTracks( resolved.Select( r => r.Track ) );

	return new DelegateEditCommand(
		$"Auto tangents on {resolved.Count} keys",
		execute: () =>
		{
			foreach ( var (kind, name, frame) in plan )
			{
				var key = FindKey( clip, kind, name, frame );
				if ( key is null )
					continue;
				key.TangentIn = null;
				key.TangentOut = null;
			}
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			RestoreTracks( before );
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

/// <summary>Zero the stored tangents on the selected keys, Mobile
/// FlatTangents: a zero tangent holds the value flat either side of the key.</summary>
public static void FlatTangents(
	DocumentSession session,
	IReadOnlyList<SelectedKey> keys,
	MarionettePlayer? player = null ) =>
	session.History.Execute( BuildFlatTangents( session, keys, player ) );

public static IEditCommand BuildFlatTangents(
	DocumentSession session,
	IReadOnlyList<SelectedKey> keys,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	var resolved = ResolveSelection( clip, keys );
	var plan = resolved.Select( r => (r.Track.Kind, r.Track.Name, r.Key.Frame) ).ToList();
	var before = SnapshotTracks( resolved.Select( r => r.Track ) );

	return new DelegateEditCommand(
		$"Flat tangents on {resolved.Count} keys",
		execute: () =>
		{
			foreach ( var (kind, name, frame) in plan )
			{
				var key = FindKey( clip, kind, name, frame );
				if ( key is null )
					continue;
				key.TangentIn = new float[RigHermite.Channels];
				key.TangentOut = new float[RigHermite.Channels];
			}
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			RestoreTracks( before );
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

/// <summary>Add a live IK constraint ending at <paramref name="endBone"/>,
/// Mobile AnimSessionOps.AddIk: one per end bone, chain length clamped to the
/// bone's depth when <paramref name="rig"/> is given (1..8 otherwise), live +
/// enabled + weight 1, unique name. Returns the created constraint (its name
/// is the handle for the other IK ops).</summary>
public static RigIk AddIk(
	DocumentSession session,
	string endBone,
	int chainLength = 2,
	string? name = null,
	Skeleton? rig = null,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	var created = CreateIk( clip, endBone, chainLength, name, rig );
	session.History.Execute( BuildAddIk( session, created, player ) );
	return created;
}

public static IEditCommand BuildAddIk(
	DocumentSession session,
	RigIk ik,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	ArgumentNullException.ThrowIfNull( ik );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	var before = CloneIkList( clip.Ik );

	return new DelegateEditCommand(
		$"Add IK {ik.Name} ({ik.EndBone})",
		execute: () =>
		{
			clip.Ik ??= new List<RigIk>();
			if ( !clip.Ik.Contains( ik ) )
				clip.Ik.Add( ik );
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			clip.Ik = before;
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

/// <summary>Delete an IK constraint by name, Mobile AnimSessionOps.DeleteIk:
/// its goal and weight tracks go with it when no other constraint ends at the
/// same bone (the end bone's FK keys stay).</summary>
public static void DeleteIk(
	DocumentSession session,
	string name,
	MarionettePlayer? player = null ) =>
	session.History.Execute( BuildDeleteIk( session, name, player ) );

public static IEditCommand BuildDeleteIk(
	DocumentSession session,
	string name,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	var ik = clip.Ik?.FirstOrDefault( c => c.Name == name )
		?? throw new InvalidOperationException( $"No IK '{name}'." );
	var endBone = ik.EndBone;
	var beforeIk = CloneIkList( clip.Ik );
	var beforeTracks = clip.Tracks.ToList();
	var beforeKeys = SnapshotTracks( clip.Tracks.Where( t => t.Name == endBone && t.Kind is RigTrackKind.IkGoal or RigTrackKind.IkWeight ) );

	return new DelegateEditCommand(
		$"Delete IK {name}",
		execute: () =>
		{
			clip.Ik?.RemoveAll( c => c.Name == name );
			if ( clip.Ik is null || clip.Ik.All( c => c.EndBone != endBone ) )
				clip.Tracks.RemoveAll( t => t.Name == endBone && t.Kind is RigTrackKind.IkGoal or RigTrackKind.IkWeight );
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			clip.Ik = beforeIk;
			clip.Tracks.Clear();
			clip.Tracks.AddRange( beforeTracks );
			RestoreTracks( beforeKeys );
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

static IEditCommand BuildEditIk(
	DocumentSession session,
	string name,
	string label,
	Action<RigIk> edit,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	if ( clip.Ik?.FirstOrDefault( c => c.Name == name ) is null )
		throw new InvalidOperationException( $"No IK '{name}'." );
	var before = CloneIkList( clip.Ik );

	return new DelegateEditCommand(
		label,
		execute: () =>
		{
			var live = clip.Ik?.FirstOrDefault( c => c.Name == name );
			if ( live is not null )
				edit( live );
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			clip.Ik = before;
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

/// <summary>Enable / disable an IK constraint, Mobile SetIkEnabled.</summary>
public static void SetIkEnabled( DocumentSession session, string name, bool on, MarionettePlayer? player = null ) =>
	session.History.Execute( BuildEditIk( session, name, on ? "Enable IK" : "Disable IK", ik => ik.Enabled = on, player ) );

/// <summary>Live (solved at playback) vs baked (editor-only) IK, Mobile SetIkLive.</summary>
public static void SetIkLive( DocumentSession session, string name, bool live, MarionettePlayer? player = null ) =>
	session.History.Execute( BuildEditIk( session, name, live ? "IK live" : "IK baked", ik => ik.Live = live, player ) );

/// <summary>Chain length, Mobile SetIkChainLength: clamped to 1..8.</summary>
public static void SetIkChainLength( DocumentSession session, string name, int n, MarionettePlayer? player = null ) =>
	session.History.Execute( BuildEditIk( session, name, "IK chain length", ik => ik.ChainLength = Math.Clamp( n, 1, 8 ), player ) );

/// <summary>Solver, Mobile SetIkSolver: FABRIK and spline are exclusive;
/// asking for both picks spline, asking for neither picks CCD.</summary>
public static void SetIkSolver( DocumentSession session, string name, bool fabrik, bool spline, MarionettePlayer? player = null ) =>
	session.History.Execute( BuildEditIk( session, name, "IK solver", ik => { ik.Fabrik = fabrik && !spline; ik.Spline = spline; }, player ) );

/// <summary>Whether the end bone takes the goal's rotation, Mobile SetIkGoalRotation.</summary>
public static void SetIkGoalRotation( DocumentSession session, string name, bool on, MarionettePlayer? player = null ) =>
	session.History.Execute( BuildEditIk( session, name, "IK goal rotation", ik => ik.GoalRotation = on, player ) );

/// <summary>Move a constraint up (-1) or down (+1) the solve order, Mobile
/// MoveIk. Moving past either end is refused.</summary>
public static void MoveIk(
	DocumentSession session,
	string name,
	int delta,
	MarionettePlayer? player = null ) =>
	session.History.Execute( BuildMoveIk( session, name, delta, player ) );

public static IEditCommand BuildMoveIk(
	DocumentSession session,
	string name,
	int delta,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	var list = clip.Ik ?? throw new InvalidOperationException( $"No IK '{name}'." );
	var i = list.FindIndex( c => c.Name == name );
	if ( i < 0 )
		throw new InvalidOperationException( $"No IK '{name}'." );
	var j = i + Math.Sign( delta );
	if ( j < 0 || j >= list.Count )
		throw new InvalidOperationException( "IK constraint is already at the end of the list." );
	var before = CloneIkList( clip.Ik );

	return new DelegateEditCommand(
		"Reorder IK",
		execute: () =>
		{
			var current = clip.Ik;
			if ( current is null )
				return;
			var at = current.FindIndex( c => c.Name == name );
			if ( at < 0 )
				return;
			var to = at + Math.Sign( delta );
			if ( to < 0 || to >= current.Count )
				return;
			(current[at], current[to]) = (current[to], current[at]);
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			clip.Ik = before;
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

/// <summary>A one-bone aim constraint, Mobile AnimSessionOps.LookAt: a chain
/// of one, position goal only, named "Look". One command, one undo entry.</summary>
public static RigIk LookAt(
	DocumentSession session,
	string bone,
	Skeleton? rig = null,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	var created = CreateIk( clip, bone, 1, "Look", rig );
	created.GoalRotation = false;
	session.History.Execute( BuildAddIk( session, created, player ) );
	return created;
}

/// <summary>Reset an IK handle: the goal is keyed back onto the end bone where
/// it sits now in FK, at the session playhead - Mobile
/// AnimSessionOps.ResetIkHandle. Needs the rig: the FK pose is sampled from
/// the clip itself (<see cref="RigAnimFile.SampleLocal"/>) and the goal is
/// written through <see cref="ClipKeys.Goals"/>.</summary>
public static void ResetIkHandle(
	DocumentSession session,
	string name,
	Skeleton rig,
	MarionettePlayer? player = null ) =>
	session.History.Execute( BuildResetIkHandle( session, name, rig, player ) );

public static IEditCommand BuildResetIkHandle(
	DocumentSession session,
	string name,
	Skeleton rig,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	ArgumentNullException.ThrowIfNull( rig );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	var ik = clip.Ik?.FirstOrDefault( c => c.Name == name )
		?? throw new InvalidOperationException( $"No IK '{name}'." );
	var endBone = ik.EndBone;
	var boneIndex = rig.IndexOf( endBone );
	if ( boneIndex < 0 )
		throw new InvalidOperationException( $"skeleton has no bone '{endBone}' for IK." );
	var frame = Math.Max( (int)Math.Round( session.PlayheadTime * clip.FrameRate ), 0 );
	var locals = clip.SampleLocal( rig, frame );
	var world = RigAnimFile.ModelSpace( rig, locals );
	var goal = world[boneIndex].Origin;

	var goalTrack = FindTrack( clip, RigTrackKind.IkGoal, endBone );
	var weightTrack = FindTrack( clip, RigTrackKind.IkWeight, endBone );
	var beforeTracks = clip.Tracks.ToList();
	var before = new Dictionary<RigTrack, List<RigKey>>();
	if ( goalTrack is not null ) before[goalTrack] = CloneKeys( goalTrack );
	if ( weightTrack is not null ) before[weightTrack] = CloneKeys( weightTrack );
	int initialFrameCount = clip.FrameCount;
	int newFrameCount = Math.Max( initialFrameCount, frame + 1 );

	return new DelegateEditCommand(
		$"Reset IK handle {name}",
		execute: () =>
		{
			ClipKeys.Goals( clip, endBone, new[] { new ClipKeys.Target( frame, goal, null, RigEase.Smooth, null ) } );
			clip.FrameCount = newFrameCount;
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			clip.Tracks.Clear();
			clip.Tracks.AddRange( beforeTracks );
			RestoreTracks( before );
			clip.FrameCount = initialFrameCount;
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

/// <summary>Add a rotation limit on a bone, Mobile AnimSessionOps.AddLimit:
/// one per bone, enabled, weight 1, ±45 degrees unless a range is given.
/// Pass <paramref name="rig"/> when the caller has one; without it the bone
/// is not validated against a skeleton.</summary>
public static RigLimit AddLimit(
	DocumentSession session,
	string bone,
	Vec3? min = null,
	Vec3? max = null,
	Skeleton? rig = null,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	if ( string.IsNullOrWhiteSpace( bone ) )
		throw new ArgumentException( "Bone cannot be empty." );
	if ( rig is not null && rig.IndexOf( bone ) < 0 )
		throw new InvalidOperationException( $"skeleton has no bone '{bone}' for a limit." );
	if ( clip.Limits is { } existing && existing.Any( l => l.Bone == bone ) )
		throw new InvalidOperationException( $"{bone} already has a limit." );
	var created = new RigLimit { Name = bone + " limit", Bone = bone, Enabled = true, Weight = 1f };
	if ( min is { } a ) created.Min = a;
	if ( max is { } b ) created.Max = b;
	session.History.Execute( BuildAddLimit( session, created, player ) );
	return created;
}

public static IEditCommand BuildAddLimit(
	DocumentSession session,
	RigLimit limit,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	ArgumentNullException.ThrowIfNull( limit );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	var before = CloneLimitList( clip.Limits );

	return new DelegateEditCommand(
		$"Add limit {limit.Bone}",
		execute: () =>
		{
			clip.Limits ??= new List<RigLimit>();
			if ( !clip.Limits.Contains( limit ) )
				clip.Limits.Add( limit );
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			clip.Limits = before;
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

/// <summary>Delete a bone's limit, Mobile DeleteLimit. Refused when the bone
/// has no limit.</summary>
public static void DeleteLimit(
	DocumentSession session,
	string bone,
	MarionettePlayer? player = null ) =>
	session.History.Execute( BuildDeleteLimit( session, bone, player ) );

public static IEditCommand BuildDeleteLimit(
	DocumentSession session,
	string bone,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	if ( clip.Limits is not { } limits || !limits.Any( l => l.Bone == bone ) )
		throw new InvalidOperationException( $"{bone} has no limit." );
	var before = CloneLimitList( clip.Limits );

	return new DelegateEditCommand(
		$"Delete limit {bone}",
		execute: () =>
		{
			clip.Limits?.RemoveAll( l => l.Bone == bone );
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			clip.Limits = before;
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

static IEditCommand BuildEditLimit(
	DocumentSession session,
	string bone,
	string label,
	Action<RigLimit> edit,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	if ( clip.Limits?.FirstOrDefault( l => l.Bone == bone ) is null )
		throw new InvalidOperationException( $"{bone} has no limit." );
	var before = CloneLimitList( clip.Limits );

	return new DelegateEditCommand(
		label,
		execute: () =>
		{
			var live = clip.Limits?.FirstOrDefault( l => l.Bone == bone );
			if ( live is not null )
				edit( live );
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			clip.Limits = before;
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

/// <summary>Enable / disable a bone's limit, Mobile SetLimitEnabled.</summary>
public static void SetLimitEnabled( DocumentSession session, string bone, bool on, MarionettePlayer? player = null ) =>
	session.History.Execute( BuildEditLimit( session, bone, on ? "Enable limit" : "Disable limit", l => l.Enabled = on, player ) );

/// <summary>Set a bone's limit range, Mobile SetLimitRange: each axis is
/// ordered so min never exceeds max.</summary>
public static void SetLimitRange( DocumentSession session, string bone, Vec3 min, Vec3 max, MarionettePlayer? player = null ) =>
	session.History.Execute( BuildEditLimit( session, bone, "Limit range", l =>
	{
		l.Min = new Vec3( Math.Min( min.x, max.x ), Math.Min( min.y, max.y ), Math.Min( min.z, max.z ) );
		l.Max = new Vec3( Math.Max( min.x, max.x ), Math.Max( min.y, max.y ), Math.Max( min.z, max.z ) );
	}, player ) );

/// <summary>Add a mark on the timeline ruler, Mobile AnimOps.AddMark: one
/// mark per frame, named "mark {n}" unless a name is given. The frame is
/// validated, not clamped - a mark past the clip is refused by the caller.
/// RigAnimWriter writes clip.Marks, so a session mark survives save/reopen.</summary>
public static RigAnimMark AddMark(
	DocumentSession session,
	int frame,
	string? name = null,
	string note = "",
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	if ( frame < 0 )
		throw new ArgumentOutOfRangeException( nameof( frame ), "Mark frame cannot be negative." );
	if ( clip.Marks is { } existing && existing.Any( m => m.Frame == frame ) )
		throw new InvalidOperationException( $"Frame {frame} already has a mark." );
	var created = new RigAnimMark
	{
		Name = string.IsNullOrWhiteSpace( name ) ? $"mark {(clip.Marks?.Count ?? 0) + 1}" : name,
		Frame = frame,
		Note = note ?? ""
	};
	session.History.Execute( BuildAddMark( session, created, player ) );
	return created;
}

public static IEditCommand BuildAddMark(
	DocumentSession session,
	RigAnimMark mark,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	ArgumentNullException.ThrowIfNull( mark );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	var before = CloneMarkList( clip.Marks );

	return new DelegateEditCommand(
		$"Add mark {mark.Name}",
		execute: () =>
		{
			clip.Marks ??= new List<RigAnimMark>();
			if ( !clip.Marks.Contains( mark ) )
				clip.Marks.Add( mark );
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			clip.Marks = before;
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

/// <summary>Remove a mark, Mobile RemoveMark. Refused when the mark is not
/// on this clip. Removal is by frame - frames are unique per clip by
/// construction - so a redo finds the mark even after undo replaced the list
/// with clones.</summary>
public static void RemoveMark(
	DocumentSession session,
	RigAnimMark mark,
	MarionettePlayer? player = null ) =>
	session.History.Execute( BuildRemoveMark( session, mark, player ) );

public static IEditCommand BuildRemoveMark(
	DocumentSession session,
	RigAnimMark mark,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	ArgumentNullException.ThrowIfNull( mark );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	if ( clip.Marks is not { } marks || !marks.Contains( mark ) )
		throw new InvalidOperationException( "No such mark." );
	var frame = mark.Frame;
	var before = CloneMarkList( clip.Marks );

	return new DelegateEditCommand(
		$"Remove mark {mark.Name}",
		execute: () =>
		{
			clip.Marks?.RemoveAll( m => m.Frame == frame );
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			clip.Marks = before;
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

/// <summary>Move a mark, Mobile MoveMark: the frame clamps to the clip
/// (Desktop has no 900-frame timeline minimum, so the clamp is the clip's
/// own last frame, not Mobile's TimelineLastFrame).</summary>
public static void MoveMark(
	DocumentSession session,
	RigAnimMark mark,
	int frame,
	MarionettePlayer? player = null ) =>
	session.History.Execute( BuildMoveMark( session, mark, frame, player ) );

public static IEditCommand BuildMoveMark(
	DocumentSession session,
	RigAnimMark mark,
	int frame,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	ArgumentNullException.ThrowIfNull( mark );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	if ( clip.Marks is not { } marks || !marks.Contains( mark ) )
		throw new InvalidOperationException( "No such mark." );
	var before = CloneMarkList( clip.Marks );
	var originalFrame = mark.Frame;
	var name = mark.Name;
	var clamped = Math.Clamp( frame, 0, Math.Max( clip.FrameCount - 1, 0 ) );

	return new DelegateEditCommand(
		$"Move mark {name}",
		execute: () =>
		{
			var live = clip.Marks?.FirstOrDefault( m => ReferenceEquals( m, mark ) )
				?? clip.Marks?.FirstOrDefault( m => m.Frame == originalFrame );
			if ( live is not null )
				live.Frame = clamped;
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			clip.Marks = before;
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

/// <summary>Add a morph event (a morph held at a value over a frame range),
/// Mobile AnimSessionOps.AddMorphEvent. The clip plays it through
/// RigAnimFile.MorphWeightsAt; note RigAnimWriter does NOT write morph events
/// (only base-JSON passthrough), so persisting a session-added event needs a
/// kernel-writer follow-up - flagged in the report, not silently assumed.</summary>
public static RigMorphEvent AddMorphEvent(
	DocumentSession session,
	string morph,
	int start,
	int end,
	float value = 1f,
	int fadeIn = 0,
	int fadeOut = 0,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	if ( string.IsNullOrWhiteSpace( morph ) )
		throw new ArgumentException( "Name the shape key." );
	if ( end < start )
		throw new ArgumentException( "The event ends before it starts." );
	if ( fadeIn < 0 || fadeOut < 0 )
		throw new ArgumentException( "Fades can't be negative." );
	var created = new RigMorphEvent
	{
		Morph = morph, Start = start, End = end,
		Value = value, FadeIn = fadeIn, FadeOut = fadeOut
	};
	session.History.Execute( BuildAddMorphEvent( session, created, player ) );
	return created;
}

public static IEditCommand BuildAddMorphEvent(
	DocumentSession session,
	RigMorphEvent morphEvent,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	ArgumentNullException.ThrowIfNull( morphEvent );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	var before = CloneMorphEventList( clip.MorphEvents );

	return new DelegateEditCommand(
		$"Add morph event {morphEvent.Morph}",
		execute: () =>
		{
			if ( !clip.MorphEvents.Contains( morphEvent ) )
				clip.MorphEvents.Add( morphEvent );
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			clip.MorphEvents.Clear();
			clip.MorphEvents.AddRange( before.Select( CloneMorphEvent ) );
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

/// <summary>Remove a morph event, Mobile RemoveMorphEvent. Refused when the
/// event is not on this clip. Removal matches on the event's fields, so a
/// redo finds it even after undo replaced the list with clones.</summary>
public static void RemoveMorphEvent(
	DocumentSession session,
	RigMorphEvent morphEvent,
	MarionettePlayer? player = null ) =>
	session.History.Execute( BuildRemoveMorphEvent( session, morphEvent, player ) );

public static IEditCommand BuildRemoveMorphEvent(
	DocumentSession session,
	RigMorphEvent morphEvent,
	MarionettePlayer? player = null )
{
	ArgumentNullException.ThrowIfNull( session );
	ArgumentNullException.ThrowIfNull( morphEvent );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	if ( !clip.MorphEvents.Contains( morphEvent ) )
		throw new InvalidOperationException( "No such morph event." );
	var fields = (morphEvent.Morph, morphEvent.Start, morphEvent.End, morphEvent.FadeIn, morphEvent.FadeOut, morphEvent.Value);
	var before = CloneMorphEventList( clip.MorphEvents );

	return new DelegateEditCommand(
		$"Remove morph event {morphEvent.Morph}",
		execute: () =>
		{
			var at = clip.MorphEvents.FindIndex( e =>
				e.Morph == fields.Morph && e.Start == fields.Start && e.End == fields.End &&
				e.FadeIn == fields.FadeIn && e.FadeOut == fields.FadeOut && e.Value == fields.Value );
			if ( at >= 0 )
				clip.MorphEvents.RemoveAt( at );
			player?.InvalidateClipCache( preservePlayhead: true );
		},
		undo: () =>
		{
			clip.MorphEvents.Clear();
			clip.MorphEvents.AddRange( before.Select( CloneMorphEvent ) );
			player?.InvalidateClipCache( preservePlayhead: true );
		}
	);
}

/// <summary>Retarget the session's clip onto another rig, through the
/// kernel's <see cref="SkeletonRetarget.RetargetClip"/>. The session's clip
/// is the source and is left exactly as it was; the retargeted clip is
/// returned, not installed - same shape as BakeRemappedClip. Unmapped bones
/// are listed on the result, not silently dropped.</summary>
public static SkeletonRetarget.ClipResult RetargetClip(
	DocumentSession session,
	Skeleton sourceRig,
	Skeleton targetRig,
	IReadOnlyDictionary<string, string> boneMap )
{
	ArgumentNullException.ThrowIfNull( session );
	var clip = session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
	return RetargetClip( clip, sourceRig, targetRig, boneMap );
}

/// <summary>Retarget a clip onto another rig. Pure - the source clip is not
/// modified.</summary>
public static SkeletonRetarget.ClipResult RetargetClip(
	RigAnimFile source,
	Skeleton sourceRig,
	Skeleton targetRig,
	IReadOnlyDictionary<string, string> boneMap )
{
	ArgumentNullException.ThrowIfNull( source );
	ArgumentNullException.ThrowIfNull( sourceRig );
	ArgumentNullException.ThrowIfNull( targetRig );
	ArgumentNullException.ThrowIfNull( boneMap );
	return SkeletonRetarget.RetargetClip( source, sourceRig, targetRig, boneMap );
}

}