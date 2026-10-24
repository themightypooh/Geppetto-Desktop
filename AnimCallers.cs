using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Wave 3: the desktop callers for animation that had a service but no way in.
/// AdvancedIkService.PlanGait planned feet and nothing ever turned a plan into a
/// clip; ClipBlendService blended poses and nothing ever loaded the blend.
/// Both builders live here (not in ServeMode's switch) so they stay testable
/// without a pane, and ServeMode's `gait` / `blend` commands are thin wires.
/// This file never touches stop/comment/continue run control: it builds clips
/// from the pane's own rig and hands them to the existing player.
/// </summary>
public static class AnimCallers
{
	/// <summary>
	/// A gait clip from AdvancedIkService.PlanGait, one IkGoal track per foot plus a
	/// root Bone track from GaitRootTravel. PlanGait positions already include the
	/// foot's bind position (they are goals, not offsets), and the kernel gait verb
	/// writes its goals the same way, so a PlanGait clip and a kernel-gait clip
	/// solve through the same IK path. Feet that are not on the rig fail loudly
	/// instead of silently planning for a ghost bone.
	/// </summary>
	public static RigAnimFile BuildGaitClip( Skeleton rig, string typeText, IReadOnlyList<string> feet, float stride, float height, int frames, string rootName )
	{
		if ( rig is null ) throw new ArgumentNullException( nameof( rig ) );
		if ( feet is null || feet.Count == 0 ) throw new ArgumentException( "gait needs at least one foot bone." );
		if ( frames <= 0 ) throw new ArgumentException( "gait needs frames above 0." );
		if ( !Enum.TryParse<AdvancedIkService.GaitType>( typeText, ignoreCase: true, out var type ) )
			throw new ArgumentException( $"gait '{typeText}' is not a gait (walk, run, sneak, quad_trot, arachnid)." );

		var clip = new RigAnimFile { FrameRate = 30, FrameCount = frames + 1 };
		for ( var fi = 0; fi < feet.Count; fi++ )
		{
			var foot = feet[fi];
			var footIdx = rig.IndexOf( foot );
			if ( footIdx < 0 ) throw new ArgumentException( $"skeleton has no bone '{foot}' for gait." );
			var bind = rig.Bones[footIdx].Local;
			var track = new RigTrack { Kind = RigTrackKind.IkGoal, Name = foot };
			foreach ( var plan in AdvancedIkService.PlanGait( type, fi, feet.Count, frames, stride, height, bind.Origin ) )
				track.Keys.Add( new RigKey { Frame = plan.Frame, Local = new RigPose( plan.Position, Quat.FromXform( bind ), Quat.ScaleOf( bind ) ), Ease = RigEase.Linear } );
			clip.Tracks.Add( track );
		}

		if ( !string.IsNullOrWhiteSpace( rootName ) )
		{
			var rootIdx = rig.IndexOf( rootName );
			if ( rootIdx < 0 ) throw new ArgumentException( $"skeleton has no bone '{rootName}' for gait root." );
			var bind = rig.Bones[rootIdx].Local;
			var track = new RigTrack { Kind = RigTrackKind.Bone, Name = rootName };
			for ( var f = 0; f <= frames; f++ )
				track.Keys.Add( new RigKey { Frame = f, Local = new RigPose( new Vec3( bind.Origin.x + AdvancedIkService.GaitRootTravel( f, frames, stride ), bind.Origin.y, bind.Origin.z ), Quat.FromXform( bind ), Quat.ScaleOf( bind ) ), Ease = RigEase.Linear } );
			clip.Tracks.Add( track );
		}

		return clip;
	}

	/// <summary>
	/// A blended clip: every whole frame sampled through ClipBlendService.EvaluateBlend
	/// (the desktop's own blend, with its channel/mask handling) and baked as Bone
	/// keys, the same shape save_clip bakes. Bones only one clip has are copied
	/// unchanged, not dropped and not blended against a zero pose.
	/// </summary>
	public static RigAnimFile BuildBlendClip( RigAnimFile baseClip, RigAnimFile layerClip, float weight, BlendMode mode, Skeleton rig )
	{
		if ( baseClip is null ) throw new ArgumentNullException( nameof( baseClip ) );
		if ( layerClip is null ) throw new ArgumentNullException( nameof( layerClip ) );
		if ( weight < 0f || weight > 1f ) throw new ArgumentException( "blend weight must be 0..1." );

		var fps = baseClip.FrameRate > 0 ? baseClip.FrameRate : 30;
		var last = 0;
		foreach ( var clip in new[] { baseClip, layerClip } )
			foreach ( var track in clip.Tracks.Where( tr => tr.Kind is RigTrackKind.Bone or RigTrackKind.IkGoal ) )
				foreach ( var k in track.Keys ) last = Math.Max( last, k.Frame );

		var out_ = new RigAnimFile { SourceModel = baseClip.SourceModel, RigAsset = baseClip.RigAsset, FrameRate = fps, FrameCount = last + 1 };
		// Bone AND IkGoal tracks (see the CLI note): gait feet are IkGoal tracks, and
		// dropping them would hand back a walking clip with no feet. Bone tracks go
		// through ClipBlendService (skeleton-aware); goal tracks blend directly, since
		// EvaluateBlend only returns bone poses.
		var keys = baseClip.Tracks.Concat( layerClip.Tracks ).Where( tr => tr.Kind is RigTrackKind.Bone or RigTrackKind.IkGoal ).Select( tr => (tr.Kind, tr.Name) ).Distinct().ToList();
		foreach ( var (kind, name) in keys )
		{
			var track = new RigTrack { Kind = kind, Name = name };
			var tb_ = baseClip.Tracks.FirstOrDefault( tr => tr.Kind == kind && string.Equals( tr.Name, name, StringComparison.OrdinalIgnoreCase ) );
			var tl_ = layerClip.Tracks.FirstOrDefault( tr => tr.Kind == kind && string.Equals( tr.Name, name, StringComparison.OrdinalIgnoreCase ) );
			var inBase = tb_ is not null;
			var inLayer = tl_ is not null;
			for ( var f = 0; f <= last; f++ )
			{
				var t = f / (float)fps;
				RigPose pose;
				if ( inBase && inLayer && kind == RigTrackKind.Bone )
				{
					var blended = ClipBlendService.EvaluateBlend( baseClip, t, layerClip, t, weight, mode, skeleton: rig );
					pose = blended.TryGetValue( name, out var p ) ? p : RigPose.Identity;
				}
				else if ( inBase && inLayer )
				{
					pose = mode == BlendMode.Additive ? ClipLayer.Compose( tb_.Evaluate( f ), tl_.Evaluate( 0 ), tl_.Evaluate( f ), weight, RigLayerMode.Additive ) : RigPose.Lerp( tb_.Evaluate( f ), tl_.Evaluate( f ), weight );
				}
				else
				{
					var only = inBase ? tb_ : tl_;
					pose = only.Evaluate( f );
				}
				track.Keys.Add( new RigKey { Frame = f, Local = pose, Ease = RigEase.Linear } );
			}
			out_.Tracks.Add( track );
		}
		return out_;
	}

	/// <summary>Parse key=value pairs from a ServeMode argument tail (after the command word).</summary>
	public static Dictionary<string, string> Opts( string arg )
	{
		var opts = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var part in (arg ?? "").Split( ' ', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var eq = part.IndexOf( '=' );
			if ( eq > 0 ) opts[part[..eq]] = part[(eq + 1)..];
		}
		return opts;
	}

	public static float OptF( Dictionary<string, string> opts, string key, float fallback )
		=> opts.TryGetValue( key, out var v ) && float.TryParse( v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f ) ? f : fallback;

	public static int OptI( Dictionary<string, string> opts, string key, int fallback )
		=> opts.TryGetValue( key, out var v ) && int.TryParse( v, out var i ) ? i : fallback;
}
