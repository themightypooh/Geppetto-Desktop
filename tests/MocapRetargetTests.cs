using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Marionette.Desktop;

/// <summary>
/// Mocap retargeting + cleanup (godlike-retargeting-mocap-cleanup): BVH import,
/// the Mixamo bone-map preset, rest-pose/scale retarget via
/// <see cref="SkeletonRetarget.RetargetClip"/>, foot-skate fix, Gaussian and
/// Butterworth smoothing, bake to editable keys. Against the Desktop kernel API
/// directly: fixtures are built in code, no UI, no app wrapper.
/// </summary>
internal static class MocapRetargetTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "mocap-retarget", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, float tolerance, string message = "" )
		{
			if ( MathF.Abs( actual - expected ) > tolerance )
				throw new Exception( $"{message}: expected {expected:F6}, got {actual:F6} (diff {MathF.Abs( actual - expected ):E4} > {tolerance:E4})" );
		}
		void VecNear( Vec3 actual, Vec3 expected, float tolerance, string message = "" )
		{
			var diff = ( actual - expected ).Length;
			if ( diff > tolerance )
				throw new Exception( $"{message}: expected ({expected.x:F4}, {expected.y:F4}, {expected.z:F4}), got ({actual.x:F4}, {actual.y:F4}, {actual.z:F4}) (diff {diff:E4} > {tolerance:E4})" );
		}

		// A small Mixamo-named BVH: Hips root + spine + one leg chain, 2 frames.
		// Frame 1 swings LeftUpLeg 90 degrees about Z and steps the root +0.5 in X.
		const string BvhFixture = """
			HIERARCHY
			ROOT mixamorig:Hips
			{
				OFFSET 0.00 10.00 0.00
				CHANNELS 6 Xposition Yposition Zposition Zrotation Xrotation Yrotation
				JOINT mixamorig:Spine
				{
					OFFSET 0.00 5.00 0.00
					CHANNELS 3 Zrotation Xrotation Yrotation
					End Site
					{
						OFFSET 0.00 5.00 0.00
					}
				}
				JOINT mixamorig:LeftUpLeg
				{
					OFFSET -1.00 -5.00 0.00
					CHANNELS 3 Zrotation Xrotation Yrotation
					JOINT mixamorig:LeftLeg
					{
						OFFSET 0.00 -5.00 0.00
						CHANNELS 3 Zrotation Xrotation Yrotation
						JOINT mixamorig:LeftFoot
						{
							OFFSET 0.00 -5.00 0.00
							CHANNELS 3 Zrotation Xrotation Yrotation
							End Site
							{
								OFFSET 0.00 -2.00 0.00
							}
						}
					}
				}
			}
			MOTION
			Frames: 2
			Frame Time: 0.0333333
			0.00 10.00 0.00 0.00 0.00 0.00 0.00 0.00 0.00 0.00 0.00 0.00 0.00 0.00 0.00 0.00 0.00 0.00
			0.50 10.00 0.00 0.00 0.00 0.00 0.00 0.00 0.00 90.00 0.00 0.00 0.00 0.00 0.00 0.00 0.00 0.00
			""";

		Vec3 WorldAt( RigAnimFile clip, Skeleton rig, string bone, float frame )
		{
			var local = clip.SampleLocal( rig, frame );
			var world = RigAnimFile.ModelSpace( rig, local );
			return world[rig.IndexOf( bone )].Origin;
		}

		Quat WorldRotAt( RigAnimFile clip, Skeleton rig, string bone, float frame )
		{
			var local = clip.SampleLocal( rig, frame );
			var world = RigAnimFile.ModelSpace( rig, local );
			return Quat.FromXform( world[rig.IndexOf( bone )] );
		}

		float AngleBetween( Quat a, Quat b )
		{
			var d = a * b.Inverse;
			var w = MathF.Max( -1f, MathF.Min( 1f, d.w ) );
			return 2f * MathF.Acos( MathF.Abs( w ) ) * 180f / MathF.PI;
		}

		// Least-squares fit of a series onto sin/cos at a known frequency: returns
		// (amplitude, residual RMS) so a smoothing check can say the signal survived
		// while the noise did not.
		(float Amplitude, float ResidualRms) FitSine( float[] series, float cyclesPerSeries )
		{
			var n = series.Length;
			var ss = 0f; var cc = 0f; var sc = 0f; var sy = 0f; var cy = 0f;
			for ( var i = 0; i < n; i++ )
			{
				var s = MathF.Sin( 2f * MathF.PI * cyclesPerSeries * i / n );
				var c = MathF.Cos( 2f * MathF.PI * cyclesPerSeries * i / n );
				ss += s * s; cc += c * c; sc += s * c; sy += s * series[i]; cy += c * series[i];
			}
			var det = ss * cc - sc * sc;
			var a = ( sy * cc - cy * sc ) / det;
			var b = ( cy * ss - sy * sc ) / det;
			var resid = 0f;
			for ( var i = 0; i < n; i++ )
			{
				var s = MathF.Sin( 2f * MathF.PI * cyclesPerSeries * i / n );
				var c = MathF.Cos( 2f * MathF.PI * cyclesPerSeries * i / n );
				var d = series[i] - ( a * s + b * c );
				resid += d * d;
			}
			return ( MathF.Sqrt( a * a + b * b ), MathF.Sqrt( resid / n ) );
		}

		Skeleton PelvisFeetRig()
		{
			var rig = new Skeleton();
			rig.AddBone( "pelvis", -1, new RigPose( new Vec3( 0, 10, 0 ), Quat.Identity, Vec3.One ).ToXform() );
			rig.AddBone( "ankle_L", 0, new RigPose( new Vec3( -1, -10, 0 ), Quat.Identity, Vec3.One ).ToXform() );
			rig.AddBone( "ankle_R", 0, new RigPose( new Vec3( 1, -10, 0 ), Quat.Identity, Vec3.One ).ToXform() );
			return rig;
		}

		Check( "bvh parses hierarchy and motion", () =>
		{
			var parsed = BvhImport.Parse( BvhFixture );
			Require( parsed.Ok, parsed.Error ?? "parse failed" );
			Require( parsed.Skeleton.Count == 5, $"expected 5 bones, got {parsed.Skeleton.Count}" );
			Require( parsed.Clip.FrameRate == 30, $"expected 30 fps, got {parsed.Clip.FrameRate}" );
			Require( parsed.Clip.FrameCount == 2, $"expected 2 frames, got {parsed.Clip.FrameCount}" );
			Require( parsed.Skeleton.Bones[parsed.Skeleton.IndexOf( "mixamorig:LeftUpLeg" )].Length > 4.9f, "thigh length should be 5" );
			Require( parsed.Skeleton.Bones[parsed.Skeleton.IndexOf( "mixamorig:LeftFoot" )].Length > 1.9f, "foot length should be the End Site offset (2)" );

			// Frame 0: the foot hangs straight down from the hips.
			VecNear( WorldAt( parsed.Clip, parsed.Skeleton, "mixamorig:LeftFoot", 0 ), new Vec3( -1, -5, 0 ), 1e-3f, "frame 0 foot" );
			// Frame 1: root stepped +0.5 in X, thigh swung 90 degrees about Z, so the
			// shin (offset 0,-5,0) now points +X: (0.5,10,0) + (-1,-5,0) + (5,0,0).
			VecNear( WorldAt( parsed.Clip, parsed.Skeleton, "mixamorig:LeftLeg", 1 ), new Vec3( 4.5f, 5, 0 ), 1e-3f, "frame 1 shin (Euler order Z,X,Y)" );
			VecNear( WorldAt( parsed.Clip, parsed.Skeleton, "mixamorig:Hips", 1 ), new Vec3( 0.5f, 10, 0 ), 1e-3f, "frame 1 root" );
		} );

		Check( "bvh rejects malformed input honestly", () =>
		{
			Require( !BvhImport.Parse( "" ).Ok, "empty input must fail" );
			Require( !BvhImport.Parse( "not a bvh file" ).Ok, "garbage must fail" );
			Require( !BvhImport.Parse( BvhFixture.Replace( "Frames: 2", "Frames: 5" ) ).Ok, "short motion data must fail" );
			Require( !BvhImport.Parse( BvhFixture.Replace( "mixamorig:LeftLeg", "mixamorig:LeftUpLeg" ) ).Ok, "duplicate joint names must fail" );
		} );

		Check( "mixamo map retargets onto citizen and preserves the motion", () =>
		{
			var map = MocapBoneMap.Mixamo();
			Require( map["mixamorig:Hips"] == "pelvis", "Hips -> pelvis" );
			Require( map["mixamorig:LeftUpLeg"] == "leg_upper_L", "LeftUpLeg -> leg_upper_L" );
			Require( map["mixamorig:LeftToeBase"] == "ball_L", "LeftToeBase -> ball_L" );
			Require( map["mixamorig:RightHandPinky1"] == "finger_ring_0_R", "pinky rides the ring chain" );

			var parsed = BvhImport.Parse( BvhFixture );
			Require( parsed.Ok, parsed.Error ?? "parse failed" );
			var target = CitizenSkeleton.Build();
			var ret = SkeletonRetarget.RetargetClip( parsed.Clip, parsed.Skeleton, target, map );
			Require( ret.Unmapped.Count == 0, $"expected no unmapped bones, got: {string.Join( ", ", ret.Unmapped )}" );
			Require( ret.Frames == 2, $"expected 2 baked frames, got {ret.Frames}" );

			// Root motion: the source root stepped +0.5 in X from bind; the target
			// pelvis must travel the same step scaled by the height ratio.
			var bindX = WorldAt( ret.Clip, target, "pelvis", 0 ).x;
			var stepX = WorldAt( ret.Clip, target, "pelvis", 1 ).x - bindX;
			Near( stepX, 0.5f * ret.RootMotionScale, 1e-3f, "pelvis step scaled by height ratio" );
			// Rotation: the thigh's world-rotation delta between frames is ~90 deg
			// about Z on both rigs (rest-pose differences cancel in the delta).
			var sourceSwing = AngleBetween(
				WorldRotAt( parsed.Clip, parsed.Skeleton, "mixamorig:LeftUpLeg", 1 ),
				WorldRotAt( parsed.Clip, parsed.Skeleton, "mixamorig:LeftUpLeg", 0 ) );
			var targetSwing = AngleBetween(
				WorldRotAt( ret.Clip, target, "leg_upper_L", 1 ),
				WorldRotAt( ret.Clip, target, "leg_upper_L", 0 ) );
			Near( sourceSwing, 90f, 0.5f, "source thigh swing" );
			Near( targetSwing, 90f, 1f, "retargeted thigh swing" );
		} );

		Check( "foot-skate fix pins a planted foot", () =>
		{
			var rig = PelvisFeetRig();
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 11 };
			var track = new RigTrack { Name = "pelvis", Kind = RigTrackKind.Bone };
			for ( var f = 0; f <= 10; f++ )
			{
				// The root drifts +0.01/frame while the foot should stand still
				// (frames 3..7), then holds. Slow enough to count as planted.
				var x = f <= 2 ? 0f : f <= 7 ? 0.01f * ( f - 2 ) : 0.05f;
				track.Keys.Add( new RigKey { Frame = f, Local = new RigPose( new Vec3( x, 10, 0 ), Quat.Identity, Vec3.One ) } );
			}
			clip.Tracks.Add( track );

			var opts = new MocapCleanup.Options { Smooth = false, RampFrames = 0 };
			var (fixedClip, before, after, spans) = MocapCleanup.FixFootSkate( clip, rig, opts );
			Require( spans == 2, $"expected 2 contact spans (one per foot), got {spans}" );
			Require( before > 0.04f, $"skate before should be ~0.05, got {before}" );
			Require( after < 1e-3f, $"skate after should be ~0, got {after}" );
			// The foot stands still now, at its span-start anchor.
			for ( var f = 0; f <= 10; f++ )
				VecNear( WorldAt( fixedClip, rig, "ankle_L", f ), new Vec3( -1, 0, 0 ), 1e-3f, $"ankle_L frame {f}" );
		} );

		Check( "foot-skate ramp does not pop at span edges", () =>
		{
			var rig = PelvisFeetRig();
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 11 };
			var track = new RigTrack { Name = "pelvis", Kind = RigTrackKind.Bone };
			for ( var f = 0; f <= 10; f++ )
			{
				var x = f <= 2 ? 0f : f <= 7 ? 0.01f * ( f - 2 ) : 0.05f;
				track.Keys.Add( new RigKey { Frame = f, Local = new RigPose( new Vec3( x, 10, 0 ), Quat.Identity, Vec3.One ) } );
			}
			clip.Tracks.Add( track );

			var opts = new MocapCleanup.Options { Smooth = false, RampFrames = 2 };
			var (fixedClip, _, _, _) = MocapCleanup.FixFootSkate( clip, rig, opts );
			// At the span's first frame the pin is fully ramped out (no correction),
			// mid-span it is fully on: the pelvis sits at its raw drift at frame 0
			// and at the corrected (un-drifted) position at frame 5.
			Near( WorldAt( fixedClip, rig, "pelvis", 0 ).x, 0f, 1e-4f, "ramp starts at zero" );
			Near( WorldAt( fixedClip, rig, "pelvis", 5 ).x, 0f, 1e-3f, "ramp fully on mid-span" );
		} );

		Check( "gaussian smoothing removes noise without flattening the motion", () =>
		{
			var rig = new Skeleton();
			rig.AddBone( "pelvis", -1, new RigPose( new Vec3( 0, 10, 0 ), Quat.Identity, Vec3.One ).ToXform() );
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 30 };
			var track = new RigTrack { Name = "pelvis", Kind = RigTrackKind.Bone };
			for ( var f = 0; f < 30; f++ )
			{
				// 1 Hz sway (amplitude 1) + 10 Hz noise (amplitude 0.1).
				var x = MathF.Sin( 2f * MathF.PI * f / 30f ) + 0.1f * MathF.Sin( 2f * MathF.PI * 10f * f / 30f );
				track.Keys.Add( new RigKey { Frame = f, Local = new RigPose( new Vec3( x, 10, 0 ), Quat.Identity, Vec3.One ) } );
			}
			clip.Tracks.Add( track );

			var raw = new float[30];
			for ( var f = 0; f < 30; f++ )
				raw[f] = clip.SampleLocal( rig, f )[0].Origin.x;
			var (rawAmp, rawNoise) = FitSine( raw, 1f );

			var smoothed = MocapCleanup.Smooth( clip, rig, new MocapCleanup.Options { Kind = MocapCleanup.SmoothKind.Gaussian, SigmaFrames = 1.5f } );
			var sm = new float[30];
			for ( var f = 0; f < 30; f++ )
				sm[f] = smoothed.SampleLocal( rig, f )[0].Origin.x;
			var (smAmp, smNoise) = FitSine( sm, 1f );

			Require( smNoise < rawNoise * 0.5f, $"noise should drop by half: {rawNoise:F4} -> {smNoise:F4}" );
			Require( MathF.Abs( smAmp - rawAmp ) < rawAmp * 0.15f, $"sway amplitude should survive: {rawAmp:F4} -> {smAmp:F4}" );
		} );

		Check( "butterworth smoothing removes noise without flattening the motion", () =>
		{
			var rig = new Skeleton();
			rig.AddBone( "pelvis", -1, new RigPose( new Vec3( 0, 10, 0 ), Quat.Identity, Vec3.One ).ToXform() );
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 30 };
			var track = new RigTrack { Name = "pelvis", Kind = RigTrackKind.Bone };
			for ( var f = 0; f < 30; f++ )
			{
				var x = MathF.Sin( 2f * MathF.PI * f / 30f ) + 0.1f * MathF.Sin( 2f * MathF.PI * 10f * f / 30f );
				track.Keys.Add( new RigKey { Frame = f, Local = new RigPose( new Vec3( x, 10, 0 ), Quat.Identity, Vec3.One ) } );
			}
			clip.Tracks.Add( track );

			var raw = new float[30];
			for ( var f = 0; f < 30; f++ )
				raw[f] = clip.SampleLocal( rig, f )[0].Origin.x;
			var (rawAmp, rawNoise) = FitSine( raw, 1f );

			var smoothed = MocapCleanup.Smooth( clip, rig, new MocapCleanup.Options { Kind = MocapCleanup.SmoothKind.Butterworth, CutoffHz = 3f } );
			var sm = new float[30];
			for ( var f = 0; f < 30; f++ )
				sm[f] = smoothed.SampleLocal( rig, f )[0].Origin.x;
			var (smAmp, smNoise) = FitSine( sm, 1f );

			Require( smNoise < rawNoise * 0.5f, $"noise should drop by half: {rawNoise:F4} -> {smNoise:F4}" );
			Require( MathF.Abs( smAmp - rawAmp ) < rawAmp * 0.15f, $"sway amplitude should survive: {rawAmp:F4} -> {smAmp:F4}" );
		} );

		Check( "bake produces editable keys and prunes redundant ones", () =>
		{
			var rig = new Skeleton();
			rig.AddBone( "pelvis", -1, new RigPose( new Vec3( 0, 10, 0 ), Quat.Identity, Vec3.One ).ToXform() );
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 11 };
			var track = new RigTrack { Name = "pelvis", Kind = RigTrackKind.Bone };
			for ( var f = 0; f <= 10; f++ )
				track.Keys.Add( new RigKey { Frame = f, Local = new RigPose( new Vec3( 0.1f * f, 10, 0 ), Quat.Identity, Vec3.One ) } );
			clip.Tracks.Add( track );

			var baked = MocapCleanup.Bake( clip, rig, everyN: 1, reduce: true );
			var bakedTrack = baked.Find( "pelvis" );
			Require( bakedTrack is not null && bakedTrack.Keys.Count == 2, $"linear motion should reduce to 2 keys, got {bakedTrack?.Keys.Count}" );
			for ( var f = 0; f <= 10; f++ )
				Near( baked.SampleLocal( rig, f )[0].Origin.x, 0.1f * f, 1e-3f, $"baked value frame {f}" );

			var stepped = MocapCleanup.Bake( clip, rig, everyN: 2, reduce: false );
			var steppedTrack = stepped.Find( "pelvis" );
			Require( steppedTrack is not null && steppedTrack.Keys.Count == 6, $"every-2 bake should have 6 keys, got {steppedTrack?.Keys.Count}" );
			Require( steppedTrack.Keys[^1].Frame == 10, "bake must always include the last frame" );
		} );

		Check( "retarget-and-clean pipeline runs end to end", () =>
		{
			var parsed = BvhImport.Parse( BvhFixture );
			Require( parsed.Ok, parsed.Error ?? "parse failed" );
			var target = CitizenSkeleton.Build();
			var report = MocapCleanup.RetargetAndClean( parsed.Clip, parsed.Skeleton, target, MocapBoneMap.Mixamo(),
				new MocapCleanup.Options { Smooth = true, FixFeet = true, BakeEveryN = 1 } );
			Require( report.Clip is not null, "pipeline must return a clip" );
			Require( report.Unmapped.Count == 0, $"no unmapped bones, got: {string.Join( ", ", report.Unmapped )}" );
			Require( report.Frames == 2, $"expected 2 frames, got {report.Frames}" );
			Require( report.KeysAfter > 0, "baked clip must carry keys" );
			Require( report.Clip.Find( "pelvis" ) is { Keys.Count: > 0 }, "pelvis track must survive the pipeline" );
			Require( report.SpansFixed == 0, "a 2-frame clip has no contact span to fix" );
		} );
	}
}
