using System;
using System.Collections.Generic;
using Godot;
using Effigy;

namespace Geppetto;

/// <summary>
/// Converts Geppetto skeletal clips (<see cref="RigAnimFile"/>) and morph tracks
/// into Godot <see cref="Animation"/> and <see cref="AnimationLibrary"/> resources.
/// Samples constrained local bone poses via <see cref="RigAnimFile.SampleConstrained"/>,
/// converting each pose via <see cref="EffigySkeletonBridge.TransformXform"/>.
/// </summary>
public static class EffigyClipBridge
{
	/// <summary>
	/// Converts a <see cref="RigAnimFile"/> into a Godot <see cref="Animation"/>.
	/// </summary>
	public static Animation ToGodotAnimation(
		RigAnimFile clip,
		Skeleton skeleton,
		string skeletonPath = "Skeleton3D",
		string meshPath = "Skeleton3D/MeshInstance3D" )
	{
		ArgumentNullException.ThrowIfNull( clip );
		var anim = new Animation();

		int fps = clip.FrameRate > 0 ? clip.FrameRate : 30;
		float frameStep = 1f / fps;
		int frameCount = Math.Max( clip.FrameCount, clip.LastFrame + 1 );
		anim.Length = Math.Max( (frameCount - 1) * frameStep, 0.001f );
		anim.Step = frameStep;

		if ( skeleton != null && skeleton.Count > 0 )
		{
			// Sample each frame and insert position, rotation, and scale keys per bone
			var posTracks = new int[skeleton.Count];
			var rotTracks = new int[skeleton.Count];
			var scaleTracks = new int[skeleton.Count];

			for ( int b = 0; b < skeleton.Count; b++ )
			{
				string boneName = skeleton.Bones[b].Name;
				posTracks[b] = anim.AddTrack( Animation.TrackType.Position3D );
				anim.TrackSetPath( posTracks[b], $"{skeletonPath}:{boneName}" );

				rotTracks[b] = anim.AddTrack( Animation.TrackType.Rotation3D );
				anim.TrackSetPath( rotTracks[b], $"{skeletonPath}:{boneName}" );

				scaleTracks[b] = anim.AddTrack( Animation.TrackType.Scale3D );
				anim.TrackSetPath( scaleTracks[b], $"{skeletonPath}:{boneName}" );
			}

			for ( int frame = 0; frame < frameCount; frame++ )
			{
				float time = frame * frameStep;
				var localPoses = clip.SampleConstrained( skeleton, frame );

				for ( int b = 0; b < skeleton.Count; b++ )
				{
					var localXf = localPoses[b];
					var godotTransform = EffigySkeletonBridge.TransformXform( localXf );

					anim.PositionTrackInsertKey( posTracks[b], time, godotTransform.Origin );
					anim.RotationTrackInsertKey( rotTracks[b], time, godotTransform.Basis.GetRotationQuaternion() );
					anim.ScaleTrackInsertKey( scaleTracks[b], time, godotTransform.Basis.Scale );
				}
			}
		}

		// Morph / blend shape tracks
		var morphNames = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var track in clip.Tracks )
		{
			if ( track.Kind == RigTrackKind.Morph && !string.IsNullOrWhiteSpace( track.Name ) )
				morphNames.Add( track.Name );
		}
		foreach ( var evt in clip.MorphEvents )
		{
			if ( !string.IsNullOrWhiteSpace( evt.Morph ) )
				morphNames.Add( evt.Morph );
		}

		if ( morphNames.Count > 0 && !string.IsNullOrEmpty( meshPath ) )
		{
			foreach ( var morph in morphNames )
			{
				int trackIdx = anim.AddTrack( Animation.TrackType.BlendShape );
				anim.TrackSetPath( trackIdx, $"{meshPath}:{morph}" );

				for ( int frame = 0; frame < frameCount; frame++ )
				{
					float time = frame * frameStep;
					var weights = clip.MorphWeightsAt( frame );
					float weight = weights.TryGetValue( morph, out float w ) ? w : 0f;
					anim.BlendShapeTrackInsertKey( trackIdx, time, weight );
				}
			}
		}

		return anim;
	}

	/// <summary>
	/// Converts a <see cref="RigAnimFile"/> into an <see cref="AnimationLibrary"/>.
	/// </summary>
	public static AnimationLibrary ToGodotLibrary(
		RigAnimFile clip,
		Skeleton skeleton,
		string clipName = "default",
		string skeletonPath = "Skeleton3D",
		string meshPath = "Skeleton3D/MeshInstance3D" )
	{
		var lib = new AnimationLibrary();
		var anim = ToGodotAnimation( clip, skeleton, skeletonPath, meshPath );
		lib.AddAnimation( clipName, anim );
		return lib;
	}
}
