using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>Game-clip pipeline, part 2: loop seams, root-motion policy and baking
/// (<c>godlike-game-clip-pipeline</c>, Wave 2 P1).
///
/// Builds on what already exists and does not re-implement it: the Graph+Dope+NLA
/// triad (AnimCurve / AnimLayers / AnimTimeline) for curve and layer editing,
/// <see cref="RootMotion"/> for extract and in-place, <see cref="ClipBake.Reduce"/>
/// for redundant-key removal, and <see cref="RigAnimFile.SampleLocal"/> as the
/// control-rig evaluation (keys + limits + IK, as played). The new pieces are the
/// seam measurement, the close-the-loop blend, the missing half of the root-motion
/// policy (strip), and the sample-to-deform-bones bake.
///
/// Limits, stated plainly: the seam is judged on the key evaluation
/// (<see cref="RigAnimFile.SampleLocal"/>), not on Lane K channel curves — the same
/// limit the triad's NLA has. CloseLoop clones by a save round-trip, so it preserves
/// exactly what a saved .riganim preserves (keys and tangents, morph events, marks,
/// root-motion settings); Lane K channel curves are not in the .riganim serialization
/// and do not survive the clone — every such track is named in the result's notes.
/// Bake samples the same key evaluation, so a clip whose motion lives only in channel
/// curves bakes its bind pose — that is the triad's gap, not this file's.</summary>
public sealed class SeamReport
{
	public string ClipName = "";
	public float MaxRotationDegrees;
	public float MaxTranslation;
	public string WorstBone = "";
	public float RotationToleranceDegrees = 1f;
	public float TranslationTolerance = 0.01f;

	public bool Pass => MaxRotationDegrees <= RotationToleranceDegrees && MaxTranslation <= TranslationTolerance;

	public override string ToString() =>
		Pass
			? $"seam closed: worst bone '{WorstBone}' {MaxRotationDegrees:0.##}° / {MaxTranslation:0.###} units"
			: $"seam pops: worst bone '{WorstBone}' {MaxRotationDegrees:0.##}° (tolerance {RotationToleranceDegrees}°) / {MaxTranslation:0.###} units (tolerance {TranslationTolerance})";
}

/// <summary>What <see cref="LoopSeamChecker.CloseLoop"/> did: the closed clip, the
/// seam before and after, and notes (for example tracks whose channel curves were
/// not blended).</summary>
public sealed class CloseLoopResult
{
	public RigAnimFile Clip;
	public SeamReport Before;
	public SeamReport After;
	public List<string> Notes = new();
}

/// <summary>A loop's seam, measured as played: the pose at the last frame against the
/// pose at frame 0, per bone. Rotation pop in degrees (worst bone), root travel in
/// units. A clip whose root motion plays in place is judged in place — that is what
/// the loop looks like to the game.</summary>
public static class LoopSeamChecker
{
	public static SeamReport Check( RigAnimFile clip, Skeleton skeleton,
		float rotationToleranceDegrees = 1f, float translationTolerance = 0.01f )
	{
		if ( clip is null )
			throw new ArgumentNullException( nameof( clip ) );
		if ( skeleton is null )
			throw new ArgumentNullException( nameof( skeleton ) );

		var report = new SeamReport
		{
			RotationToleranceDegrees = rotationToleranceDegrees,
			TranslationTolerance = translationTolerance,
		};

		var last = clip.LastFrame;
		if ( last <= 0 || skeleton.Count == 0 )
			return report;

		var first = clip.SampleLocal( skeleton, 0 );
		var end = clip.SampleLocal( skeleton, last );
		var root = skeleton.IndexOf( RootMotionPolicy.RootBoneOf( clip, skeleton ) );
		if ( root < 0 )
			root = 0;

		for ( var b = 0; b < skeleton.Count; b++ )
		{
			var degrees = RotationBetween( first[b], end[b] );
			if ( degrees > report.MaxRotationDegrees )
			{
				report.MaxRotationDegrees = degrees;
				report.WorstBone = skeleton.Bones[b].Name;
			}
		}

		report.MaxTranslation = ( end[root].Origin - first[root].Origin ).Length;
		return report;
	}

	/// <summary>Blend the tail of a clip into its first frame so the loop closes:
	/// the last <paramref name="blendFrames"/> frames ease from the played pose to
	/// the frame-0 pose (position and scale lerped, rotation slerped), so the last
	/// frame IS the first frame. The source clip is untouched; the result is a new
	/// clip built by a save round-trip (see the file header for what survives it).
	/// A track carrying channel curves loses them in the round-trip and is named
	/// in the result's notes.</summary>
	public static CloseLoopResult CloseLoop( RigAnimFile clip, Skeleton skeleton, int blendFrames = 6,
		float rotationToleranceDegrees = 1f, float translationTolerance = 0.01f )
	{
		if ( clip is null )
			throw new ArgumentNullException( nameof( clip ) );
		if ( skeleton is null )
			throw new ArgumentNullException( nameof( skeleton ) );

		var before = Check( clip, skeleton, rotationToleranceDegrees, translationTolerance );

		// Canonical deep copy: the writer is the serializer, so a write/read round-trip
		// preserves exactly what a saved clip preserves.
		var closed = RigAnimFile.Read( RigAnimWriter.Write( clip ) );
		var result = new CloseLoopResult { Clip = closed, Before = before };
		var last = closed.LastFrame;

		if ( last > 0 && blendFrames > 0 )
		{
			var window = Math.Min( blendFrames, last );
			var start = last - window;

			// Sample the whole tail first: setting a key at frame f changes how the
			// next frame evaluates, so no sample may be taken after a write.
			var first = closed.SampleLocal( skeleton, 0 );
			var tail = new Xform[window + 1][];
			for ( var f = start; f <= last; f++ )
				tail[f - start] = closed.SampleLocal( skeleton, f );

			for ( var f = start; f <= last; f++ )
			{
				var w = ( f - start ) / (float)window;

				for ( var b = 0; b < skeleton.Count; b++ )
				{
					var track = closed.Find( skeleton.Bones[b].Name );
					if ( track is null )
						continue;
					var blended = RigPose.Lerp( RigPose.FromXform( tail[f - start][b] ), RigPose.FromXform( first[b] ), w );
					ClipKeys.Set( track, f, blended, RigEase.Linear );

					if ( track.ChannelCurves.Count > 0 )
					{
						var note = $"track '{track.Name}' carries channel curves, which the .riganim round-trip does not preserve — the closed clip blends its keys only";
						if ( !result.Notes.Contains( note ) )
							result.Notes.Add( note );
					}
				}
			}
		}

		result.After = Check( closed, skeleton, rotationToleranceDegrees, translationTolerance );
		return result;
	}

	/// <summary>The angle in degrees between two local poses' rotations.</summary>
	public static float RotationBetween( Xform a, Xform b )
	{
		var qa = Quat.FromXform( a );
		var qb = Quat.FromXform( b );
		var dot = MathF.Abs( qa.x * qb.x + qa.y * qb.y + qa.z * qb.z + qa.w * qb.w );
		if ( dot > 1f )
			dot = 1f;
		return 2f * MathF.Acos( dot ) * 180f / MathF.PI;
	}
}

/// <summary>Root-motion policy: one place where the rules live. Exactly one bone
/// carries the travel — the skeleton's top bone, named by
/// <see cref="RootMotionSettings.Bone"/> — and the game reads it from there.
/// Extract moves the travel onto that bone (<see cref="RootMotion.Extract"/>);
/// Strip takes it back out again (new here); InPlace is the clip as the character
/// sees it while the game moves it (<see cref="RootMotion.InPlace"/>).</summary>
public static class RootMotionPolicy
{
	/// <summary>The bone the game reads root motion from: the clip's root-motion bone
	/// when it has one, else the skeleton's root bone, else "".</summary>
	public static string RootBoneOf( RigAnimFile clip, Skeleton skeleton )
	{
		if ( clip?.RootMotion is { Bone: { Length: > 0 } bone } )
			return bone;
		if ( skeleton is not null && !string.IsNullOrEmpty( skeleton.RootBone ) )
			return skeleton.RootBone;
		return "";
	}

	/// <summary>Extract root motion onto the top bone, with the discipline checked
	/// first: the bone must exist and must be a top bone, and the travel source (when
	/// named) must exist. On a discipline failure the clip is untouched and the
	/// result says why.</summary>
	public static RootMotion.Result Extract( RigAnimFile clip, Skeleton skeleton, RootMotionSettings settings )
	{
		if ( clip is null || skeleton is null || settings is null )
			return new RootMotion.Result { Error = "no clip, skeleton or settings" };

		var root = skeleton.IndexOf( settings.Bone ?? "" );

		if ( root < 0 )
			return new RootMotion.Result { Error = $"the model has no bone '{settings.Bone}' to carry root motion — pick its top bone (Root Bone)" };

		if ( skeleton.Bones[root].Parent >= 0 )
			return new RootMotion.Result { Error = $"'{settings.Bone}' is not a top bone — root motion must sit on the skeleton's top bone, so the game reads the travel from exactly one place" };

		if ( !string.IsNullOrEmpty( settings.Source ) && skeleton.IndexOf( settings.Source ) < 0 )
			return new RootMotion.Result { Error = $"the model has no bone '{settings.Source}' to take the travel from — pick the hips or pelvis (Travel From)" };

		return RootMotion.Extract( clip, skeleton, settings );
	}

	/// <summary>Strip root motion out of a clip: the root bone is re-keyed with its
	/// in-place local at every frame (the travel is gone, the body walks on the
	/// spot), and the clip's root-motion settings are cleared. The world poses of
	/// the stripped clip are exactly the in-place poses of the original — nothing
	/// else moves. A clip with no root motion is left alone.</summary>
	public static RootMotion.Result Strip( RigAnimFile clip, Skeleton skeleton )
	{
		if ( clip is null || skeleton is null )
			return new RootMotion.Result { Error = "no clip or skeleton" };

		if ( clip.RootMotion is not { Enabled: true } settings )
			return new RootMotion.Result { Notes = { "the clip carries no root motion — nothing to strip" } };

		var root = skeleton.IndexOf( settings.Bone );

		if ( root < 0 )
			return new RootMotion.Result { Error = $"the model has no bone '{settings.Bone}' carrying root motion" };

		var last = clip.LastFrame;
		var cycle = RootMotion.CycleOf( clip, skeleton );

		// Sample every in-place pose first: rewriting the root track changes how
		// later frames evaluate, so no sample may be taken after a write.
		var inPlace = new Xform[last + 1][];
		for ( var f = 0; f <= last; f++ )
			inPlace[f] = RootMotion.InPlace( clip, skeleton, clip.SampleLocal( skeleton, f ), f );

		var track = clip.Find( skeleton.Bones[root].Name );

		if ( track is null )
			return new RootMotion.Result { Notes = { $"the root bone '{settings.Bone}' has no keys — nothing to strip" } };

		track.Keys.RemoveAll( k => k.Frame >= 0 && k.Frame <= last );

		for ( var f = 0; f <= last; f++ )
			ClipKeys.Set( track, f, RigPose.FromXform( inPlace[f][root] ), RigEase.Linear );

		var removed = ClipBake.Reduce( track, 0, last, 0.01f, 0.1f );

		clip.RootMotion = null;

		return new RootMotion.Result
		{
			Keys = last + 1 - removed,
			Frames = last + 1,
			Travel = cycle.Origin,
			Yaw = RootMotion.YawBetween( Xform.Identity, cycle ) * 180f / MathF.PI,
			Notes = { "root motion stripped: the root is re-keyed in place and the settings are cleared" },
		};
	}

	/// <summary>A local pose with the root's travel taken out — the clip as it plays
	/// on a character the game moves by that travel. Unchanged without root motion.</summary>
	public static Xform[] InPlace( RigAnimFile clip, Skeleton skeleton, Xform[] local, float frame ) =>
		RootMotion.InPlace( clip, skeleton, local, frame );
}

/// <summary>How close a baked clip came to the control rig it was baked from.</summary>
public sealed class BakeReport
{
	public int SampledFrames;
	public float MaxPositionError;
	public float MaxRotationDegrees;
	public string WorstBone = "";
	public bool Pass;

	public override string ToString() =>
		Pass
			? $"bake matches the control rig: worst {MaxPositionError:0.###} units / {MaxRotationDegrees:0.##}° on '{WorstBone}' over {SampledFrames} frames"
			: $"bake drifts from the control rig: worst {MaxPositionError:0.###} units / {MaxRotationDegrees:0.##}° on '{WorstBone}'";
}

/// <summary>Bake a control rig to deform bones: sample the clip as played
/// (<see cref="RigAnimFile.SampleLocal"/> — keys, limits and IK, as the viewer
/// plays it) at every frame, and write the samples as keys on a new clip, with
/// redundant keys removed by <see cref="ClipBake.Reduce"/> — so the baked clip
/// holds sampled keys only, no key that re-evaluating the neighbours already
/// implies. Morph tracks bake the same way; part/camera/IK-goal/IK-weight tracks
/// are copied through unchanged, as are morph events and marks. A clip that played
/// in place bakes in place (its root-motion settings are dropped — carrying them
/// would take the travel out twice); any other clip carries its settings through.</summary>
public static class ControlRigBaker
{
	public static RigAnimFile Bake( RigAnimFile clip, Skeleton skeleton, int frameRate = 30,
		float positionTolerance = 0.02f, float degreesTolerance = 0.25f, float scaleTolerance = 0.005f )
	{
		if ( clip is null )
			throw new ArgumentNullException( nameof( clip ) );
		if ( skeleton is null )
			throw new ArgumentNullException( nameof( skeleton ) );
		if ( frameRate <= 0 )
			throw new ArgumentException( "frame rate must be positive", nameof( frameRate ) );

		var last = clip.LastFrame;
		var baked = new RigAnimFile
		{
			SourceModel = clip.SourceModel,
			RigAsset = clip.RigAsset,
			FrameRate = frameRate,
			FrameCount = last + 1,
		};

		// Sample every frame first: the baked clip is built from these samples, and
		// sampling the source never sees a key this method wrote.
		var poses = new Xform[last + 1][];
		for ( var f = 0; f <= last; f++ )
			poses[f] = clip.SampleLocal( skeleton, f );

		for ( var b = 0; b < skeleton.Count; b++ )
		{
			var track = new RigTrack { Name = skeleton.Bones[b].Name, Kind = RigTrackKind.Bone };

			for ( var f = 0; f <= last; f++ )
				track.Keys.Add( new RigKey { Frame = f, Local = RigPose.FromXform( poses[f][b] ), Ease = RigEase.Linear } );

			ClipBake.Reduce( track, 0, last, positionTolerance, degreesTolerance, scaleTolerance );
			baked.Tracks.Add( track );
		}

		foreach ( var morph in clip.Tracks.Where( t => t.Kind == RigTrackKind.Morph && t.Keys.Count > 0 ) )
		{
			var track = new RigTrack { Name = morph.Name, Kind = RigTrackKind.Morph };

			for ( var f = 0; f <= last; f++ )
				track.Keys.Add( new RigKey { Frame = f, Local = morph.Evaluate( f ), Ease = RigEase.Linear } );

			ClipBake.Reduce( track, 0, last, positionTolerance, degreesTolerance, scaleTolerance );
			baked.Tracks.Add( track );
		}

		foreach ( var other in clip.Tracks.Where( t => t.Kind != RigTrackKind.Bone && t.Kind != RigTrackKind.Morph ) )
			baked.Tracks.Add( ClipTrackCopy.DeepCopy( other ) );

		foreach ( var e in clip.MorphEvents )
			baked.MorphEvents.Add( new RigMorphEvent
			{
				Morph = e.Morph,
				Start = e.Start,
				End = e.End,
				FadeIn = e.FadeIn,
				FadeOut = e.FadeOut,
				Value = e.Value,
			} );

		if ( clip.Marks is not null )
		{
			baked.Marks = new List<RigAnimMark>();
			foreach ( var m in clip.Marks )
				baked.Marks.Add( m.Clone() );
		}

		// In-place samples already had the travel taken out; carrying the settings
		// would take it out a second time on playback.
		baked.PlayInPlace = false;
		baked.RootMotion = clip.PlayInPlace ? null : clip.RootMotion;
		return baked;
	}

	/// <summary>Check a baked clip against the control rig it came from: sample both
	/// at every frame and report the worst position and rotation error per bone.</summary>
	public static BakeReport Validate( RigAnimFile baked, RigAnimFile source, Skeleton skeleton,
		float positionTolerance = 0.02f, float degreesTolerance = 0.25f )
	{
		if ( baked is null )
			throw new ArgumentNullException( nameof( baked ) );
		if ( source is null )
			throw new ArgumentNullException( nameof( source ) );
		if ( skeleton is null )
			throw new ArgumentNullException( nameof( skeleton ) );

		var report = new BakeReport();
		var last = source.LastFrame;
		report.SampledFrames = last + 1;

		for ( var f = 0; f <= last; f++ )
		{
			var a = baked.SampleLocal( skeleton, f );
			var b = source.SampleLocal( skeleton, f );

			for ( var bone = 0; bone < skeleton.Count; bone++ )
			{
				var posErr = ( a[bone].Origin - b[bone].Origin ).Length;
				if ( posErr > report.MaxPositionError )
					report.MaxPositionError = posErr;

				var degrees = LoopSeamChecker.RotationBetween( a[bone], b[bone] );
				if ( degrees > report.MaxRotationDegrees )
				{
					report.MaxRotationDegrees = degrees;
					report.WorstBone = skeleton.Bones[bone].Name;
				}
			}
		}

		report.Pass = report.MaxPositionError <= positionTolerance && report.MaxRotationDegrees <= degreesTolerance;
		return report;
	}
}
