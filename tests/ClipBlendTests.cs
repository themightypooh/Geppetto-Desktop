#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Effigy;
using Marionette.Desktop;

internal static class ClipBlendTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "clip-blend", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		bool VecEqual( Vec3 a, Vec3 b ) => a.x == b.x && a.y == b.y && a.z == b.z;
		bool QuatEqual( Quat a, Quat b ) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
		bool PoseExactEqual( RigPose a, RigPose b ) => VecEqual( a.Position, b.Position ) && QuatEqual( a.Rotation, b.Rotation ) && VecEqual( a.Scale, b.Scale );

		// 1. Weight 0 returns exact base pose
		Check( "weight 0 returns exact base pose", () =>
		{
			var basePose = new RigPose( new Vec3( 10, 20, 30 ), Quat.FromAngles( 15, 30, 45 ), new Vec3( 2, 2, 2 ) );
			var targetPose = new RigPose( new Vec3( -5, 12, 40 ), Quat.FromAngles( 45, 60, 90 ), new Vec3( 0.5f, 1f, 1.5f ) );
			var refPose = new RigPose( new Vec3( 0, 0, 0 ), Quat.Identity, Vec3.One );

			// Override mode
			var blendedOverride = ClipBlendService.BlendPose( basePose, targetPose, refPose, 0f, BlendMode.Override );
			Require( PoseExactEqual( blendedOverride, basePose ), "Weight 0 in Override must return exact base pose" );
			Require( VecEqual( blendedOverride.Position, basePose.Position ) );
			Require( QuatEqual( blendedOverride.Rotation, basePose.Rotation ) );
			Require( VecEqual( blendedOverride.Scale, basePose.Scale ) );

			// Additive mode
			var blendedAdditive = ClipBlendService.BlendPose( basePose, targetPose, refPose, 0f, BlendMode.Additive );
			Require( PoseExactEqual( blendedAdditive, basePose ), "Weight 0 in Additive must return exact base pose" );
		} );

		// 2. Weight 1 returns exact target pose
		Check( "weight 1 returns exact target pose", () =>
		{
			var basePose = new RigPose( new Vec3( 10, 20, 30 ), Quat.FromAngles( 15, 30, 45 ), new Vec3( 2, 2, 2 ) );
			var targetPose = new RigPose( new Vec3( -5, 12, 40 ), Quat.FromAngles( 45, 60, 90 ), new Vec3( 0.5f, 1f, 1.5f ) );
			var refPose = new RigPose( new Vec3( 0, 0, 0 ), Quat.Identity, Vec3.One );

			var blended = ClipBlendService.BlendPose( basePose, targetPose, refPose, 1f, BlendMode.Override );
			Require( PoseExactEqual( blended, targetPose ), "Weight 1 in Override must return exact target pose" );
			Require( VecEqual( blended.Position, targetPose.Position ) );
			Require( QuatEqual( blended.Rotation, targetPose.Rotation ) );
			Require( VecEqual( blended.Scale, targetPose.Scale ) );
		} );

		// 3. Weight 0.5 interpolates linearly in translation and slerp in rotation
		Check( "weight 0.5 interpolates linearly in translation and slerp in rotation", () =>
		{
			var basePos = new Vec3( 0, 0, 0 );
			var targetPos = new Vec3( 10, 20, 30 );
			var baseRot = Quat.Identity;
			var targetRot = Quat.FromAxisAngle( new Vec3( 0, 0, 1 ), MathF.PI / 2f ); // 90 deg around Z
			var baseScale = new Vec3( 1, 1, 1 );
			var targetScale = new Vec3( 3, 5, 7 );

			var basePose = new RigPose( basePos, baseRot, baseScale );
			var targetPose = new RigPose( targetPos, targetRot, targetScale );

			var blended = ClipBlendService.BlendPose( basePose, targetPose, 0.5f, BlendMode.Override );

			// Translation is linear: (0 + 10)*0.5 = 5, (0 + 20)*0.5 = 10, (0 + 30)*0.5 = 15
			Require( MathF.Abs( blended.Position.x - 5f ) < 1e-5f, $"Expected pos.x=5, got {blended.Position.x}" );
			Require( MathF.Abs( blended.Position.y - 10f ) < 1e-5f, $"Expected pos.y=10, got {blended.Position.y}" );
			Require( MathF.Abs( blended.Position.z - 15f ) < 1e-5f, $"Expected pos.z=15, got {blended.Position.z}" );

			// Scale is linear: (1 + 3)*0.5 = 2, (1 + 5)*0.5 = 3, (1 + 7)*0.5 = 4
			Require( MathF.Abs( blended.Scale.x - 2f ) < 1e-5f, $"Expected scale.x=2, got {blended.Scale.x}" );
			Require( MathF.Abs( blended.Scale.y - 3f ) < 1e-5f, $"Expected scale.y=3, got {blended.Scale.y}" );
			Require( MathF.Abs( blended.Scale.z - 4f ) < 1e-5f, $"Expected scale.z=4, got {blended.Scale.z}" );

			// Rotation is slerp: 45 deg around Z
			var expectedRot = Quat.FromAxisAngle( new Vec3( 0, 0, 1 ), MathF.PI / 4f );
			float dot = MathF.Abs( ClipBlendService.Dot( blended.Rotation, expectedRot ) );
			Require( dot > 0.9999f, $"Expected slerped rotation dot > 0.9999, got {dot}" );
		} );

		// 4. Antipodal quaternions blend smoothly along shortest arc
		Check( "antipodal quaternions blend smoothly along shortest arc", () =>
		{
			// Rotation R: 30 degrees around Z axis
			var q1 = Quat.FromAxisAngle( new Vec3( 0, 0, 1 ), MathF.PI / 6f );
			// q2 is antipodal representation of the EXACT SAME orientation: -q1
			var q2 = ClipBlendService.Negate( q1 );
			Require( ClipBlendService.Dot( q1, q2 ) < -0.99f, "q1 and q2 must have negative dot product (antipodal)" );

			var pose1 = new RigPose( Vec3.Zero, q1, Vec3.One );
			var pose2 = new RigPose( Vec3.Zero, q2, Vec3.One );

			// Blend at 0.5: should not degenerate to (0,0,0,0) or take 360-degree loop!
			var blended = ClipBlendService.BlendPose( pose1, pose2, 0.5f, BlendMode.Override );
			float dot = MathF.Abs( ClipBlendService.Dot( blended.Rotation, q1 ) );
			Require( dot > 0.9999f, $"Blended antipodal quaternion must equal q1 orientation, dot={dot}" );
			float norm = blended.Rotation.x * blended.Rotation.x + blended.Rotation.y * blended.Rotation.y + blended.Rotation.z * blended.Rotation.z + blended.Rotation.w * blended.Rotation.w;
			Require( MathF.Abs( norm - 1f ) < 1e-4f, "Blended rotation must be normalized unit quaternion" );

			// Also test a small angular delta with opposite signs: q_a (0 deg) and q_b (10 deg with negative sign)
			var rotA = Quat.Identity;
			var rotB = ClipBlendService.Negate( Quat.FromAxisAngle( new Vec3( 0, 1, 0 ), 10f * MathF.PI / 180f ) );
			Require( ClipBlendService.Dot( rotA, rotB ) < 0f, "rotA and rotB dot < 0" );
			var blendedSmall = ClipBlendService.BlendPose( new RigPose( Vec3.Zero, rotA, Vec3.One ), new RigPose( Vec3.Zero, rotB, Vec3.One ), 0.5f, BlendMode.Override );
			var expectedSmall = Quat.FromAxisAngle( new Vec3( 0, 1, 0 ), 5f * MathF.PI / 180f );
			float dotSmall = MathF.Abs( ClipBlendService.Dot( blendedSmall.Rotation, expectedSmall ) );
			Require( dotSmall > 0.9999f, $"Small delta with flipped sign must blend along shortest 5-deg arc, got dot={dotSmall}" );
		} );

		// 5. Additive delta from reference pose
		Check( "additive delta from reference pose", () =>
		{
			// Reference pose: bone at rest position (0, 5, 0), rot Identity, scale (1, 1, 1)
			var refPose = new RigPose( new Vec3( 0, 5, 0 ), Quat.Identity, new Vec3( 1, 1, 1 ) );
			// Base pose: bone currently in walk animation at (10, 0, 0), rot 15 deg X, scale (2, 2, 2)
			var basePose = new RigPose( new Vec3( 10, 0, 0 ), Quat.FromAngles( 15, 0, 0 ), new Vec3( 2, 2, 2 ) );
			// Layer pose: layer keys a nod: pos (0, 7, 0) [+2 Y delta], rot 20 deg Y, scale (1, 1.5, 1) [1.5 Y scale ratio]
			var layerPose = new RigPose( new Vec3( 0, 7, 0 ), Quat.FromAngles( 0, 20, 0 ), new Vec3( 1, 1.5f, 1 ) );

			var blended = ClipBlendService.BlendPose( basePose, layerPose, refPose, 1f, BlendMode.Additive );

			// Position: base + (layer - ref) = (10, 0, 0) + (0, 2, 0) = (10, 2, 0)
			// NOT naive component addition 10 + 0 = 10, 0 + 7 = 7
			Require( MathF.Abs( blended.Position.x - 10f ) < 1e-4f, $"Expected additive X=10, got {blended.Position.x}" );
			Require( MathF.Abs( blended.Position.y - 2f ) < 1e-4f, $"Expected additive Y=2, got {blended.Position.y}" );
			Require( MathF.Abs( blended.Position.z - 0f ) < 1e-4f, $"Expected additive Z=0, got {blended.Position.z}" );

			// Scale: base * (layer / ref) = (2 * 1, 2 * 1.5, 2 * 1) = (2, 3, 2)
			Require( MathF.Abs( blended.Scale.x - 2f ) < 1e-4f, $"Expected scale X=2, got {blended.Scale.x}" );
			Require( MathF.Abs( blended.Scale.y - 3f ) < 1e-4f, $"Expected scale Y=3, got {blended.Scale.y}" );
			Require( MathF.Abs( blended.Scale.z - 2f ) < 1e-4f, $"Expected scale Z=2, got {blended.Scale.z}" );

			// Rotation delta: ref is Identity, so delta is layer rotation (20 deg Y)
			// Blended rot = (base.Rotation * delta).Normalized
			var expectedRot = (basePose.Rotation * layerPose.Rotation).Normalized;
			float dotRot = MathF.Abs( ClipBlendService.Dot( blended.Rotation, expectedRot ) );
			Require( dotRot > 0.9999f, $"Expected composed rotation dot > 0.9999, got {dotRot}" );

			// At weight 0.5: delta is half
			var halfBlended = ClipBlendService.BlendPose( basePose, layerPose, refPose, 0.5f, BlendMode.Additive );
			Require( MathF.Abs( halfBlended.Position.y - 1f ) < 1e-4f, $"Expected half pos Y=1, got {halfBlended.Position.y}" );
			Require( MathF.Abs( halfBlended.Scale.y - 2.5f ) < 1e-4f, $"Expected half scale Y=2.5, got {halfBlended.Scale.y}" );
		} );

		// 6. Bone masking: masked bones blend, unmasked stay base
		Check( "bone masking: masked bones blend, unmasked stay base", () =>
		{
			var baseClip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var baseRoot = new RigTrack { Name = "root", Kind = RigTrackKind.Bone };
			baseRoot.Keys.Add( new RigKey { Frame = 0, Local = new RigPose( new Vec3( 1, 0, 0 ), Quat.Identity, Vec3.One ) } );
			var baseArmL = new RigTrack { Name = "arm_L", Kind = RigTrackKind.Bone };
			baseArmL.Keys.Add( new RigKey { Frame = 0, Local = new RigPose( new Vec3( 2, 0, 0 ), Quat.Identity, Vec3.One ) } );
			var baseArmR = new RigTrack { Name = "arm_R", Kind = RigTrackKind.Bone };
			baseArmR.Keys.Add( new RigKey { Frame = 0, Local = new RigPose( new Vec3( 3, 0, 0 ), Quat.Identity, Vec3.One ) } );
			baseClip.Tracks.AddRange( new[] { baseRoot, baseArmL, baseArmR } );

			var layerClip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var layerRoot = new RigTrack { Name = "root", Kind = RigTrackKind.Bone };
			layerRoot.Keys.Add( new RigKey { Frame = 0, Local = new RigPose( new Vec3( 10, 0, 0 ), Quat.Identity, Vec3.One ) } );
			var layerArmL = new RigTrack { Name = "arm_L", Kind = RigTrackKind.Bone };
			layerArmL.Keys.Add( new RigKey { Frame = 0, Local = new RigPose( new Vec3( 20, 0, 0 ), Quat.Identity, Vec3.One ) } );
			var layerArmR = new RigTrack { Name = "arm_R", Kind = RigTrackKind.Bone };
			layerArmR.Keys.Add( new RigKey { Frame = 0, Local = new RigPose( new Vec3( 30, 0, 0 ), Quat.Identity, Vec3.One ) } );
			layerClip.Tracks.AddRange( new[] { layerRoot, layerArmL, layerArmR } );

			// Whitelist only "arm_L"
			var mask = BoneMask.CreateWhitelist( "arm_L" );
			var result = ClipBlendService.EvaluateBlend( baseClip, 0f, layerClip, 0f, weight: 1.0f, BlendMode.Override, mask: mask );

			// arm_L should have blended to target (20, 0, 0)
			Require( MathF.Abs( result["arm_L"].Position.x - 20f ) < 1e-4f, $"arm_L should blend to 20, got {result["arm_L"].Position.x}" );
			// root and arm_R should stay exact base (1, 0, 0) and (3, 0, 0)
			Require( VecEqual( result["root"].Position, baseRoot.Keys[0].Local.Position ), "root must stay exact base" );
			Require( VecEqual( result["arm_R"].Position, baseArmR.Keys[0].Local.Position ), "arm_R must stay exact base" );

			// Blacklist "arm_R" and use glob pattern for whitelist "arm_*"
			var mask2 = new BoneMask( whitelist: new[] { "arm_*" }, blacklist: new[] { "arm_R" } );
			var result2 = ClipBlendService.EvaluateBlend( baseClip, 0f, layerClip, 0f, weight: 1.0f, BlendMode.Override, mask: mask2 );
			Require( MathF.Abs( result2["arm_L"].Position.x - 20f ) < 1e-4f, "arm_L should blend" );
			Require( VecEqual( result2["root"].Position, baseRoot.Keys[0].Local.Position ), "root not in whitelist should stay base" );
			Require( VecEqual( result2["arm_R"].Position, baseArmR.Keys[0].Local.Position ), "arm_R in blacklist should stay base" );
		} );

		// 7. Unkeyed tracks fallback to reference/bind pose
		Check( "unkeyed tracks fallback to reference/bind pose", () =>
		{
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Translate( new Vec3( 0, 0, 0 ) ) );
			rig.AddBone( "spine_0", 0, Xform.Translate( new Vec3( 0, 10, 0 ) ) ); // bind pose Y = 10
			rig.AddBone( "arm_L", 1, Xform.Translate( new Vec3( 5, 10, 0 ) ) );

			// Base clip keys root and spine_0, but NOT arm_L
			var baseClip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var bRoot = new RigTrack { Name = "root", Kind = RigTrackKind.Bone };
			bRoot.Keys.Add( new RigKey { Frame = 0, Local = new RigPose( new Vec3( 1, 0, 0 ), Quat.Identity, Vec3.One ) } );
			var bSpine = new RigTrack { Name = "spine_0", Kind = RigTrackKind.Bone };
			bSpine.Keys.Add( new RigKey { Frame = 0, Local = new RigPose( new Vec3( 0, 12, 0 ), Quat.Identity, Vec3.One ) } );
			baseClip.Tracks.AddRange( new[] { bRoot, bSpine } );

			// Layer clip keys ONLY root, NOT spine_0 and NOT arm_L
			var layerClip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var lRoot = new RigTrack { Name = "root", Kind = RigTrackKind.Bone };
			lRoot.Keys.Add( new RigKey { Frame = 0, Local = new RigPose( new Vec3( 10, 0, 0 ), Quat.Identity, Vec3.One ) } );
			layerClip.Tracks.Add( lRoot );

			// 1. In Additive mode: unkeyed bone in layer should have zero delta, so spine_0 retains base pose (0, 12, 0)
			var addResult = ClipBlendService.EvaluateBlend( baseClip, 0f, layerClip, 0f, weight: 1.0f, BlendMode.Additive, skeleton: rig );
			Require( MathF.Abs( addResult["spine_0"].Position.y - 12f ) < 1e-4f, $"spine_0 should remain base (12), got {addResult["spine_0"].Position.y}" );
			// arm_L unkeyed in both clips falls back to Skeleton bind pose (5, 10, 0), NOT (0, 0, 0)
			Require( MathF.Abs( addResult["arm_L"].Position.x - 5f ) < 1e-4f, $"arm_L x should be bind pose (5), got {addResult["arm_L"].Position.x}" );
			Require( MathF.Abs( addResult["arm_L"].Position.y - 10f ) < 1e-4f, $"arm_L y should be bind pose (10), got {addResult["arm_L"].Position.y}" );

			// 2. In Override mode: unkeyed bone in layer evaluates to bind pose (0, 10, 0).
			// At weight 1.0, spine_0 blends towards its bind pose (10), not identity (0).
			var overResult = ClipBlendService.EvaluateBlend( baseClip, 0f, layerClip, 0f, weight: 1.0f, BlendMode.Override, skeleton: rig );
			Require( MathF.Abs( overResult["spine_0"].Position.y - 10f ) < 1e-4f, $"spine_0 should blend to bind pose (10), got {overResult["spine_0"].Position.y}" );
			Require( MathF.Abs( overResult["arm_L"].Position.y - 10f ) < 1e-4f, $"arm_L should be bind pose (10), got {overResult["arm_L"].Position.y}" );
		} );

		// 8. Looping vs one-shot crossfade evaluation
		Check( "looping vs one-shot crossfade evaluation", () =>
		{
			// Clip A: 30 frames at 30 fps (duration = 1.0s, last frame = 30)
			var clipA = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var tA = new RigTrack { Name = "root", Kind = RigTrackKind.Bone };
			tA.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 0, 0, 0 ), Quat.Identity, Vec3.One ) } );
			tA.Keys.Add( new RigKey { Frame = 30, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 30, 0, 0 ), Quat.Identity, Vec3.One ) } );
			clipA.Tracks.Add( tA );

			// Clip B: 60 frames at 30 fps (duration = 2.0s, last frame = 60)
			var clipB = new RigAnimFile { FrameRate = 30, FrameCount = 61 };
			var tB = new RigTrack { Name = "root", Kind = RigTrackKind.Bone };
			tB.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 0, 100, 0 ), Quat.Identity, Vec3.One ) } );
			tB.Keys.Add( new RigKey { Frame = 60, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 60, 100, 0 ), Quat.Identity, Vec3.One ) } );
			clipB.Tracks.Add( tB );

			// Test CalculateFrame:
			// Looping clip A at time 1.5s: 1.5 * 30 = 45 -> 45 % 30 = 15
			float loopFrameA = ClipBlendService.CalculateFrame( clipA, 1.5f, loop: true );
			Require( MathF.Abs( loopFrameA - 15f ) < 1e-4f, $"Looping frame should be 15, got {loopFrameA}" );

			// One-shot clip A at time 1.5s: clamped to LastFrame (30)
			float oneshotFrameA = ClipBlendService.CalculateFrame( clipA, 1.5f, loop: false );
			Require( MathF.Abs( oneshotFrameA - 30f ) < 1e-4f, $"One-shot frame should clamp to 30, got {oneshotFrameA}" );

			// Crossfade evaluation from looping clip A to one-shot clip B over 0.5s duration
			// At elapsedTime = 0: weight should be 0, pose is exact clip A at its time
			var sample0 = ClipBlendService.EvaluateCrossfade( clipA, fromStartTime: 0f, fromLoop: true, clipB, toStartTime: 0f, toLoop: false, elapsedTime: 0f, duration: 0.5f );
			Require( VecEqual( sample0["root"].Position, tA.Keys[0].Local.Position ), "At elapsed 0, crossfade must return clip A" );

			// At elapsedTime = 0.5s: weight should be 1.0, pose is clip B at toStartTime + 0.5s (frame 15)
			var sample1 = ClipBlendService.EvaluateCrossfade( clipA, fromStartTime: 0f, fromLoop: true, clipB, toStartTime: 0f, toLoop: false, elapsedTime: 0.5f, duration: 0.5f );
			// Clip B at frame 15 has pos (15, 100, 0)
			Require( MathF.Abs( sample1["root"].Position.x - 15f ) < 1e-4f, $"Expected clip B at frame 15 (x=15), got {sample1["root"].Position.x}" );
			Require( MathF.Abs( sample1["root"].Position.y - 100f ) < 1e-4f, $"Expected clip B pos y=100, got {sample1["root"].Position.y}" );

			// Beyond duration (elapsedTime = 3.0s): clip B is one-shot, clamped at frame 60 (x=60, y=100)
			var sampleBeyond = ClipBlendService.EvaluateCrossfade( clipA, fromStartTime: 0f, fromLoop: true, clipB, toStartTime: 0f, toLoop: false, elapsedTime: 3.0f, duration: 0.5f );
			Require( MathF.Abs( sampleBeyond["root"].Position.x - 60f ) < 1e-4f, $"Expected one-shot clip B clamped at frame 60 (x=60), got {sampleBeyond["root"].Position.x}" );
		} );

		// 9. Scale additive identity
		Check( "scale additive identity", () =>
		{
			var basePose = new RigPose( new Vec3( 1, 2, 3 ), Quat.Identity, new Vec3( 2.5f, 4f, 0.5f ) );
			// Target scale equals reference scale
			var targetPose = new RigPose( new Vec3( 5, 5, 5 ), Quat.Identity, new Vec3( 1, 1, 1 ) );
			var refPose = new RigPose( new Vec3( 0, 0, 0 ), Quat.Identity, new Vec3( 1, 1, 1 ) );

			var blended = ClipBlendService.BlendPose( basePose, targetPose, refPose, weight: 1.0f, BlendMode.Additive );
			Require( MathF.Abs( blended.Scale.x - basePose.Scale.x ) < 1e-6f, "Scale X must match base" );
			Require( MathF.Abs( blended.Scale.y - basePose.Scale.y ) < 1e-6f, "Scale Y must match base" );
			Require( MathF.Abs( blended.Scale.z - basePose.Scale.z ) < 1e-6f, "Scale Z must match base" );
		} );

		// 10. Different frame rates and crossfade duration 0
		Check( "different frame rates and crossfade duration 0", () =>
		{
			var clip30 = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var t30 = new RigTrack { Name = "root", Kind = RigTrackKind.Bone };
			t30.Keys.Add( new RigKey { Frame = 0, Local = new RigPose( new Vec3( 0, 0, 0 ), Quat.Identity, Vec3.One ) } );
			t30.Keys.Add( new RigKey { Frame = 30, Local = new RigPose( new Vec3( 30, 0, 0 ), Quat.Identity, Vec3.One ) } );
			clip30.Tracks.Add( t30 );

			var clip60 = new RigAnimFile { FrameRate = 60, FrameCount = 61 };
			var t60 = new RigTrack { Name = "root", Kind = RigTrackKind.Bone };
			t60.Keys.Add( new RigKey { Frame = 0, Local = new RigPose( new Vec3( 0, 10, 0 ), Quat.Identity, Vec3.One ) } );
			t60.Keys.Add( new RigKey { Frame = 60, Local = new RigPose( new Vec3( 0, 70, 0 ), Quat.Identity, Vec3.One ) } );
			clip60.Tracks.Add( t60 );

			// At 0.5s: clip30 is at frame 15 (x = 15), clip60 is at frame 30 (y = 40)
			float f30 = ClipBlendService.CalculateFrame( clip30, 0.5f, false );
			float f60 = ClipBlendService.CalculateFrame( clip60, 0.5f, false );
			Require( MathF.Abs( f30 - 15f ) < 1e-4f, $"Expected f30=15, got {f30}" );
			Require( MathF.Abs( f60 - 30f ) < 1e-4f, $"Expected f60=30, got {f60}" );

			// Duration 0 snaps immediately
			float wPre = ClipBlendService.EvaluateCrossfadeWeight( -0.1f, 0f );
			float wPost = ClipBlendService.EvaluateCrossfadeWeight( 0f, 0f );
			Require( wPre == 0f, "Duration 0 pre-snap is 0" );
			Require( wPost == 1f, "Duration 0 at 0 or post is 1" );
		} );

		// 11. Underlying clips and tracks are not mutated
		Check( "underlying clips and tracks are not mutated", () =>
		{
			var clipA = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var trackA = new RigTrack { Name = "boneA", Kind = RigTrackKind.Bone };
			trackA.Keys.Add( new RigKey { Frame = 0, Local = new RigPose( new Vec3( 1, 2, 3 ), Quat.Identity, Vec3.One ) } );
			clipA.Tracks.Add( trackA );

			var clipB = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var trackB = new RigTrack { Name = "boneB", Kind = RigTrackKind.Bone };
			trackB.Keys.Add( new RigKey { Frame = 0, Local = new RigPose( new Vec3( 4, 5, 6 ), Quat.Identity, Vec3.One ) } );
			clipB.Tracks.Add( trackB );

			int trackCountA = clipA.Tracks.Count;
			int trackCountB = clipB.Tracks.Count;
			int keyCountA = trackA.Keys.Count;
			int keyCountB = trackB.Keys.Count;

			for ( int i = 0; i < 10; i++ )
			{
				float w = i / 10f;
				ClipBlendService.EvaluateBlend( clipA, 0.1f * i, clipB, 0.2f * i, w, BlendMode.Additive );
				ClipBlendService.EvaluateBlend( clipA, 0.1f * i, clipB, 0.2f * i, w, BlendMode.Override );
			}

			Require( clipA.Tracks.Count == trackCountA, "Tracks count on clipA must not mutate" );
			Require( clipB.Tracks.Count == trackCountB, "Tracks count on clipB must not mutate" );
			Require( trackA.Keys.Count == keyCountA, "Keys on trackA must not mutate" );
			Require( trackB.Keys.Count == keyCountB, "Keys on trackB must not mutate" );
			Require( VecEqual( trackA.Keys[0].Local.Position, new Vec3( 1, 2, 3 ) ), "Keys on trackA values must not mutate" );
		} );

		// 12. Allocation measurement zero alloc in warm evaluator loop
		Check( "allocation measurement zero alloc in warm evaluator loop", () =>
		{
			var rig = new Skeleton();
			for ( int i = 0; i < 20; i++ )
				rig.AddBone( $"bone_{i}", i == 0 ? -1 : 0, Xform.Translate( new Vec3( i, 0, 0 ) ) );

			var baseClip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var layerClip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };

			for ( int i = 0; i < 20; i++ )
			{
				var tb = new RigTrack { Name = $"bone_{i}", Kind = RigTrackKind.Bone };
				tb.Keys.Add( new RigKey { Frame = 0, Local = new RigPose( new Vec3( i, 0, 0 ), Quat.Identity, Vec3.One ) } );
				baseClip.Tracks.Add( tb );

				var tl = new RigTrack { Name = $"bone_{i}", Kind = RigTrackKind.Bone };
				tl.Keys.Add( new RigKey { Frame = 0, Local = new RigPose( new Vec3( i, 1, 0 ), Quat.Identity, Vec3.One ) } );
				layerClip.Tracks.Add( tl );
			}

			var evaluator = new ClipBlendEvaluator( rig );
			evaluator.SetClips( baseClip, layerClip );

			// Warm up JIT
			for ( int i = 0; i < 50; i++ )
				evaluator.Evaluate( 0.1f, 0.1f, 0.5f, BlendMode.Override );

			long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
			for ( int i = 0; i < 1000; i++ )
				evaluator.Evaluate( 0.033f * (i % 30), 0.033f * (i % 30), 0.5f, BlendMode.Override );
			long bytesAfter = GC.GetAllocatedBytesForCurrentThread();

			long allocated = bytesAfter - bytesBefore;
			Require( allocated == 0, $"Evaluator must allocate 0 bytes in hot path, allocated {allocated} bytes across 1000 frames" );
		} );
	}
}
