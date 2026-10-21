#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Effigy;
using Marionette.Desktop;

internal static class ClipCapabilityTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "clip-capability", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		Skeleton MakeRig()
		{
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Translate( new Vec3( 0, 0, 0 ) ) );
			rig.AddBone( "spine_0", 0, Xform.Translate( new Vec3( 0, 2, 0 ) ) );
			rig.AddBone( "arm_L", 1, Xform.Translate( new Vec3( 3, 2, 0 ) ) );
			return rig;
		}

		RigAnimFile MakeValidClip( string boneName = "root", int frameCount = 31, int frameRate = 30 )
		{
			var clip = new RigAnimFile { FrameRate = frameRate, FrameCount = frameCount };
			var track = new RigTrack { Name = boneName, Kind = RigTrackKind.Bone };
			track.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 1, 2, 3 ), Quat.Identity, Vec3.One ) } );
			track.Keys.Add( new RigKey { Frame = frameCount - 1, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 5, 2, 3 ), Quat.Identity, Vec3.One ) } );
			clip.Tracks.Add( track );
			return clip;
		}

		// 1. Valid compatible clip passes audit
		Check( "valid compatible clip passes audit", () =>
		{
			var rig = MakeRig();
			var clip = MakeValidClip( "root" );
			var trackSpine = new RigTrack { Name = "spine_0", Kind = RigTrackKind.Bone };
			trackSpine.Keys.Add( new RigKey { Frame = 0, Local = RigPose.Identity } );
			clip.Tracks.Add( trackSpine );

			var audit = ClipCapabilityValidator.Validate( clip, rig );

			Require( audit.IsPlayable, "Valid compatible clip must be playable" );
			Require( audit.IsCompatible, "Valid compatible clip must be compatible" );
			Require( audit.Errors.Count == 0, $"Expected 0 errors, got: {string.Join( "; ", audit.Errors )}" );
			Require( audit.MatchedTargets.Count == 2, $"Expected 2 matched targets, got {audit.MatchedTargets.Count}" );
			Require( audit.MatchedTargets.Contains( "root" ) && audit.MatchedTargets.Contains( "spine_0" ) );
			Require( audit.MissingTargets.Count == 0, $"Expected 0 missing targets, got: {string.Join( "; ", audit.MissingTargets )}" );
			Require( audit.UnmappedSkeletonBones.Contains( "arm_L" ), "arm_L should be in unmapped skeleton bones" );
			Require( audit.SupportedFeatures.Contains( "BoneTracks" ) );
		} );

		// 2. Completely incompatible clip (zero target matches) rejected
		Check( "completely incompatible clip zero target matches rejected", () =>
		{
			var rig = MakeRig();
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var track1 = new RigTrack { Name = "foreign_leg", Kind = RigTrackKind.Bone };
			track1.Keys.Add( new RigKey { Frame = 0, Local = RigPose.Identity } );
			var track2 = new RigTrack { Name = "foreign_arm", Kind = RigTrackKind.Bone };
			track2.Keys.Add( new RigKey { Frame = 0, Local = RigPose.Identity } );
			clip.Tracks.Add( track1 );
			clip.Tracks.Add( track2 );

			var audit = ClipCapabilityValidator.Validate( clip, rig );

			Require( !audit.IsPlayable, "Completely incompatible clip must not be playable" );
			Require( !audit.IsCompatible, "Completely incompatible clip must not be compatible" );
			Require( audit.Errors.Count > 0, "Audit must report errors" );
			Require( audit.MatchedTargets.Count == 0, "Matched targets must be 0" );
			Require( audit.MissingTargets.Count == 2, "Both tracks must be listed in missing targets" );
			Require( audit.MissingTargets.Contains( "foreign_leg" ) && audit.MissingTargets.Contains( "foreign_arm" ) );
			Require( audit.Errors.Any( e => e.Contains( "zero target bones match", StringComparison.OrdinalIgnoreCase ) ) );

			// Also test clip with no tracks at all
			var emptyClip = new RigAnimFile { FrameRate = 30, FrameCount = 10 };
			var emptyAudit = ClipCapabilityValidator.Validate( emptyClip, rig );
			Require( !emptyAudit.IsPlayable, "Empty tracks clip must not be playable" );
			Require( emptyAudit.Errors.Count > 0 );
		} );

		// 3. Partial target match produces warnings and identifies unmapped bones
		Check( "partial target match produces warnings and identifies unmapped bones", () =>
		{
			var rig = MakeRig(); // has root, spine_0, arm_L
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };

			var tRoot = new RigTrack { Name = "root", Kind = RigTrackKind.Bone };
			tRoot.Keys.Add( new RigKey { Frame = 0, Local = RigPose.Identity } );
			var tSpine = new RigTrack { Name = "spine_0", Kind = RigTrackKind.Bone };
			tSpine.Keys.Add( new RigKey { Frame = 0, Local = RigPose.Identity } );
			var tUnmapped = new RigTrack { Name = "weapon_socket", Kind = RigTrackKind.Bone };
			tUnmapped.Keys.Add( new RigKey { Frame = 0, Local = RigPose.Identity } );

			clip.Tracks.Add( tRoot );
			clip.Tracks.Add( tSpine );
			clip.Tracks.Add( tUnmapped );

			// Default policy: AllowPartialWithWarnings
			var defaultAudit = ClipCapabilityValidator.Validate( clip, rig );
			Require( defaultAudit.IsPlayable, "Default policy must allow partial matches to play" );
			Require( defaultAudit.Errors.Count == 0, "Default policy should not produce hard errors for partial match" );
			Require( defaultAudit.Warnings.Count > 0, "Default policy must emit warnings for partial match" );
			Require( defaultAudit.MissingTargets.Contains( "weapon_socket" ), "weapon_socket must be identified as unmapped" );
			Require( defaultAudit.MatchedTargets.Contains( "root" ) && defaultAudit.MatchedTargets.Contains( "spine_0" ) );
			Require( defaultAudit.UnmappedSkeletonBones.Contains( "arm_L" ), "arm_L must be identified as unmapped skeleton bone" );

			// Strict policy: RequireAllTargets
			var strictOptions = new ClipValidationOptions { TargetPolicy = TargetMatchingPolicy.RequireAllTargets };
			var strictAudit = ClipCapabilityValidator.Validate( clip, rig, strictOptions );
			Require( !strictAudit.IsPlayable, "RequireAllTargets policy must reject partial matches" );
			Require( strictAudit.Errors.Count > 0, "RequireAllTargets policy must produce hard errors" );
			Require( strictAudit.Errors.Any( e => e.Contains( "weapon_socket" ) ) );
		} );

		// 4. Morph event without morph tracks diagnosed
		Check( "morph event without morph tracks diagnosed", () =>
		{
			var rig = MakeRig();
			var clip = MakeValidClip( "root" );
			clip.MorphEvents = new System.Collections.Generic.List<RigMorphEvent>
			{
				new() { Morph = "blink", Start = 0, End = 10, Value = 1f },
				new() { Morph = "smile", Start = 5, End = 20, Value = 0.75f }
			};

			var audit = ClipCapabilityValidator.Validate( clip, rig );

			Require( audit.MissingMorphTracks.Contains( "blink" ), "blink must be diagnosed in MissingMorphTracks" );
			Require( audit.MissingMorphTracks.Contains( "smile" ), "smile must be diagnosed in MissingMorphTracks" );
			Require( audit.Warnings.Any( w => w.Contains( "blink" ) && w.Contains( "morph track" ) ) );
			Require( audit.Warnings.Any( w => w.Contains( "smile" ) && w.Contains( "morph track" ) ) );

			// Invalid morph event range diagnosis
			var badClip = MakeValidClip( "root" );
			badClip.MorphEvents = new System.Collections.Generic.List<RigMorphEvent>
			{
				new() { Morph = "inverted_range", Start = 20, End = 5, Value = 1f }
			};
			var badAudit = ClipCapabilityValidator.Validate( badClip, rig );
			Require( !badAudit.IsPlayable, "Clip with corrupted morph event range must not be playable" );
			Require( badAudit.Errors.Any( e => e.Contains( "inverted_range" ) && e.Contains( "invalid frame range" ) ) );
		} );

		// 5. Missing referenced control rig reported clearly
		Check( "missing referenced control rig reported clearly", () =>
		{
			var rig = MakeRig();
			string work = Path.Combine( settings.WorkDirectory, "rig_test_" + Guid.NewGuid().ToString( "N" ) );
			Directory.CreateDirectory( work );

			var clip = MakeValidClip( "root" );
			clip.RigAsset = "missing_character.ctrlrig";

			// Without PathResolver
			var auditNoResolver = ClipCapabilityValidator.Validate( clip, rig, new ClipValidationOptions { ClipDirectory = work } );
			Require( !auditNoResolver.IsPlayable, "Clip with missing RigAsset must not be playable" );
			Require( auditNoResolver.Errors.Any( e => e.Contains( "missing_character.ctrlrig" ) ) );

			// With AssetPathResolver
			var resolver = new AssetPathResolver( work );
			var auditResolver = ClipCapabilityValidator.Validate( clip, rig, new ClipValidationOptions { PathResolver = resolver } );
			Require( !auditResolver.IsPlayable );
			Require( auditResolver.Errors.Any( e => e.Contains( "missing_character.ctrlrig" ) ) );

			// Malformed rig JSON
			string malformedPath = Path.Combine( work, "broken.ctrlrig" );
			File.WriteAllText( malformedPath, "{ this is invalid json content ]" );
			clip.RigAsset = "broken.ctrlrig";

			var auditMalformed = ClipCapabilityValidator.Validate( clip, rig, new ClipValidationOptions { PathResolver = resolver } );
			Require( !auditMalformed.IsPlayable, "Clip with malformed RigAsset must not be playable" );
			Require( auditMalformed.Errors.Any( e => e.Contains( "broken.ctrlrig" ) && (e.Contains( "malformed" ) || e.Contains( "JSON" )) ) );

			// Valid rig JSON
			string validPath = Path.Combine( work, "valid.ctrlrig" );
			File.WriteAllText( validPath, "{}" );
			clip.RigAsset = "valid.ctrlrig";

			var auditValid = ClipCapabilityValidator.Validate( clip, rig, new ClipValidationOptions { PathResolver = resolver } );
			Require( auditValid.IsPlayable, "Clip with valid RigAsset should be playable" );
			Require( auditValid.RigResolved );
			Require( auditValid.SupportedFeatures.Contains( "ControlRig" ) );
		} );

		// 6. Rejected clip leaves session/player state unchanged
		Check( "rejected clip leaves session player state unchanged", () =>
		{
			var rig = MakeRig();
			var validClip1 = MakeValidClip( "root", frameCount: 31 );
			validClip1.Tracks[0].Keys[1].Local = new RigPose( new Vec3( 15, 0, 0 ), Quat.Identity, Vec3.One );

			// Setup initial valid player
			var player = new MarionettePlayer( rig );
			player.LoadClip( validClip1 );
			player.Playing = true;
			player.Time = 0.5f;
			player.Step( 0 );

			float originalTime = player.Time;
			bool originalPlaying = player.Playing;
			var originalPose = player.WorldPose[0];
			var originalMatrix = player.GpuBones[0];
			var originalClip = player.Clip;

			// Construct invalid clip (zero matching targets)
			var rejectedClip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var badTrack = new RigTrack { Name = "completely_unknown_bone", Kind = RigTrackKind.Bone };
			badTrack.Keys.Add( new RigKey { Frame = 0, Local = RigPose.Identity } );
			rejectedClip.Tracks.Add( badTrack );

			bool applied = ClipCapabilityValidator.TryApply( player, rejectedClip, out var audit );
			Require( !applied, "TryApply must return false for rejected clip" );
			Require( player.Clip == originalClip, "Player clip must remain the original clip" );
			Require( player.Time == originalTime, "Player playhead time must not be mutated" );
			Require( player.Playing == originalPlaying, "Player playing state must not be mutated" );
			Require( (player.WorldPose[0].Origin - originalPose.Origin).Length < 1e-5f, "Player WorldPose must remain identical" );
			Require( player.GpuBones[0] == originalMatrix, "Player GpuBones must remain identical" );

			// DocumentSession state immutability on rejected clip
			var studio = new PartStudio();
			studio.Rig.AddBone( "root", -1, Xform.Identity );
			studio.Rig.AddBone( "spine_0", 0, Xform.Identity );
			studio.Rig.AddBone( "arm_L", 1, Xform.Identity );
			studio.Rebuild();

			var session = DocumentSession.CreateEffigy( "model.effigy", studio, clip: validClip1 );
			session.PlayheadTime = 0.8f;
			session.IsPlaying = true;
			long originalRev = session.Revision;

			bool sessionApplied = ClipCapabilityValidator.TryApply( session, rejectedClip, out var auditSession );
			Require( !sessionApplied, "Session TryApply must return false for rejected clip" );
			Require( session.Clip == validClip1, "Session clip must remain original clip" );
			Require( session.Revision == originalRev, "Session revision must not be incremented" );
			Require( session.PlayheadTime == 0.8f, "Session playhead time must remain unchanged" );
			Require( session.IsPlaying == true, "Session playing state must remain unchanged" );

			// ApplySafe must throw and leave player untouched
			bool threw = false;
			try { ClipCapabilityValidator.ApplySafe( player, rejectedClip ); }
			catch ( InvalidOperationException ) { threw = true; }
			Require( threw, "ApplySafe must throw InvalidOperationException" );
			Require( player.Clip == originalClip );
			Require( player.Time == originalTime );
		} );

		// 7. Unknown track kind / corrupted data diagnosed
		Check( "unknown track kind corrupted data diagnosed", () =>
		{
			var rig = MakeRig();

			// Unknown track kind enum value
			var clipUnknownKind = MakeValidClip( "root" );
			clipUnknownKind.Tracks.Add( new RigTrack { Name = "mystery_track", Kind = (RigTrackKind)999 } );
			var auditUnknown = ClipCapabilityValidator.Validate( clipUnknownKind, rig );
			Require( !auditUnknown.IsPlayable, "Unknown track kind must be rejected" );
			Require( auditUnknown.UnsupportedFeatures.Any( f => f.Contains( "999" ) ) );
			Require( auditUnknown.Errors.Any( e => e.Contains( "unknown track kind" ) && e.Contains( "999" ) ) );

			// Corrupted NaN pose coordinates
			var clipNaN = MakeValidClip( "root" );
			clipNaN.Tracks[0].Keys[0].Local = new RigPose( new Vec3( float.NaN, 0, 0 ), Quat.Identity, Vec3.One );
			var auditNaN = ClipCapabilityValidator.Validate( clipNaN, rig );
			Require( !auditNaN.IsPlayable, "Clip with NaN keyframe coordinates must be rejected" );
			Require( auditNaN.Errors.Any( e => e.Contains( "non-finite position" ) ) );

			// Negative keyframe
			var clipNegativeKey = MakeValidClip( "root" );
			clipNegativeKey.Tracks[0].Keys[0].Frame = -12;
			var auditNegative = ClipCapabilityValidator.Validate( clipNegativeKey, rig );
			Require( !auditNegative.IsPlayable, "Negative frame must be rejected" );
			Require( auditNegative.Errors.Any( e => e.Contains( "negative frame" ) ) );

			// Non-ascending keyframes
			var clipUnsorted = MakeValidClip( "root" );
			clipUnsorted.Tracks[0].Keys.Clear();
			clipUnsorted.Tracks[0].Keys.Add( new RigKey { Frame = 20, Local = RigPose.Identity } );
			clipUnsorted.Tracks[0].Keys.Add( new RigKey { Frame = 10, Local = RigPose.Identity } );
			var auditUnsorted = ClipCapabilityValidator.Validate( clipUnsorted, rig );
			Require( !auditUnsorted.IsPlayable, "Non-ascending keyframes must be rejected" );
			Require( auditUnsorted.Errors.Any( e => e.Contains( "ascending order" ) ) );

			// Unsupported track type for desktop playback (e.g. Camera)
			var clipCamera = MakeValidClip( "root" );
			clipCamera.Tracks.Add( new RigTrack { Name = "MainCamera", Kind = RigTrackKind.Camera } );
			var auditCamera = ClipCapabilityValidator.Validate( clipCamera, rig );
			Require( !auditCamera.IsPlayable, "Camera track must be reported unsupported for desktop playback" );
			Require( auditCamera.UnsupportedFeatures.Contains( "Camera" ) );
		} );

		// 8. Root motion configuration validated
		Check( "root motion configuration validated", () =>
		{
			var rig = MakeRig(); // has root, spine_0, arm_L

			// Valid root motion
			var clipValid = MakeValidClip( "root" );
			clipValid.RootMotion = new RootMotionSettings
			{
				Bone = "root",
				Source = "spine_0",
				Mode = RootMotionMode.XY,
				Enabled = true
			};
			var auditValid = ClipCapabilityValidator.Validate( clipValid, rig );
			Require( auditValid.IsPlayable, "Valid root motion clip must be playable" );
			Require( auditValid.SupportedFeatures.Contains( "RootMotion" ) );

			// Missing root bone
			var clipMissingRoot = MakeValidClip( "root" );
			clipMissingRoot.RootMotion = new RootMotionSettings
			{
				Bone = "nonexistent_root_bone",
				Source = "spine_0"
			};
			var auditMissingRoot = ClipCapabilityValidator.Validate( clipMissingRoot, rig );
			Require( !auditMissingRoot.IsPlayable, "Root motion with missing root bone must be rejected" );
			Require( auditMissingRoot.Errors.Any( e => e.Contains( "nonexistent_root_bone" ) ) );

			// Missing source bone
			var clipMissingSource = MakeValidClip( "root" );
			clipMissingSource.RootMotion = new RootMotionSettings
			{
				Bone = "root",
				Source = "nonexistent_hips"
			};
			var auditMissingSource = ClipCapabilityValidator.Validate( clipMissingSource, rig );
			Require( !auditMissingSource.IsPlayable, "Root motion with missing source bone must be rejected" );
			Require( auditMissingSource.Errors.Any( e => e.Contains( "nonexistent_hips" ) ) );

			// Empty bone name
			var clipEmptyBone = MakeValidClip( "root" );
			clipEmptyBone.RootMotion = new RootMotionSettings { Bone = "" };
			var auditEmptyBone = ClipCapabilityValidator.Validate( clipEmptyBone, rig );
			Require( !auditEmptyBone.IsPlayable, "Root motion with empty bone name must be rejected" );
			Require( auditEmptyBone.Errors.Any( e => e.Contains( "empty" ) && e.Contains( "Root motion bone" ) ) );

			// Unknown root motion mode
			var clipBadMode = MakeValidClip( "root" );
			clipBadMode.RootMotion = new RootMotionSettings { Bone = "root", Mode = (RootMotionMode)88 };
			var auditBadMode = ClipCapabilityValidator.Validate( clipBadMode, rig );
			Require( !auditBadMode.IsPlayable, "Root motion with invalid mode must be rejected" );
			Require( auditBadMode.Errors.Any( e => e.Contains( "invalid mode" ) ) );
		} );

		// 9. Morph drivers validated
		Check( "morph drivers validated", () =>
		{
			var rig = MakeRig();
			var clip = MakeValidClip( "root" );
			clip.Drivers = new System.Collections.Generic.List<MorphDriver>
			{
				new() { Morph = "arm_bulge", Bone = "arm_L", Channel = DriverChannel.Rotation, Axis = DriverAxis.X, Rest = 0, Target = 90, Value = 1f },
				new() { Morph = "unmapped_driver", Bone = "missing_bone", Channel = DriverChannel.Rotation, Axis = DriverAxis.Y, Rest = 0, Target = 45, Value = 0.5f }
			};

			var audit = ClipCapabilityValidator.Validate( clip, rig );
			Require( audit.IsPlayable, "Clip with valid driver params and partial missing bone warning should play" );
			Require( audit.Warnings.Any( w => w.Contains( "missing_bone" ) && w.Contains( "Morph driver" ) ) );
			Require( audit.SupportedFeatures.Contains( "MorphDrivers" ) );

			// Driver with non-finite parameters
			clip.Drivers.Add( new MorphDriver { Morph = "broken", Bone = "arm_L", Rest = float.NaN } );
			var badAudit = ClipCapabilityValidator.Validate( clip, rig );
			Require( !badAudit.IsPlayable );
			Require( badAudit.Errors.Any( e => e.Contains( "non-finite" ) && e.Contains( "broken" ) ) );
		} );

		// 10. File path validation and loading
		Check( "file path validation and loading", () =>
		{
			var rig = MakeRig();
			var clip = MakeValidClip( "root" );
			string filePath = Path.Combine( settings.WorkDirectory, "valid_test_" + Guid.NewGuid().ToString( "N" ) + ".riganim" );
			File.WriteAllText( filePath, RigAnimWriter.Write( clip ) );

			var audit = ClipCapabilityValidator.Validate( filePath, rig );
			Require( audit.IsPlayable, "Valid saved clip must pass Validate(filePath)" );

			var player = new MarionettePlayer( rig );
			bool applied = ClipCapabilityValidator.TryApply( player, filePath, out var applyAudit );
			Require( applied, "TryApply(player, filePath) must succeed" );
			Require( player.Clip is not null );

			// Missing file path
			string missingPath = Path.Combine( settings.WorkDirectory, "definitely_does_not_exist.riganim" );
			var missingAudit = ClipCapabilityValidator.Validate( missingPath, rig );
			Require( !missingAudit.IsPlayable );
			Require( missingAudit.Errors.Any( e => e.Contains( "not found" ) ) );
		} );
	}
}
