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
}
