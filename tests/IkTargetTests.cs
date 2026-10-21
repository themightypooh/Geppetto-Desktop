#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Effigy;
using Marionette.Desktop;

internal static class IkTargetTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "ik-target", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		Skeleton CreateTwoBoneSkeleton()
		{
			var rig = new Skeleton();
			// Bone 0: root at origin
			rig.AddBone( "root", -1, Xform.Identity, 5f );
			// Bone 1: elbow at (0, 5, 0)
			rig.AddBone( "elbow", 0, Xform.Translate( new Vec3( 0, 5, 0 ) ), 5f );
			// Bone 2: wrist at (0, 5, 0) relative to elbow (so (0, 10, 0) in model space)
			rig.AddBone( "wrist", 1, Xform.Translate( new Vec3( 0, 5, 0 ) ), 2f );
			return rig;
		}

		Skeleton CreateChainSkeleton( int boneCount = 4, float segmentLength = 3f )
		{
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Identity, segmentLength );
			for ( int i = 1; i < boneCount; i++ )
			{
				string name = i == boneCount - 1 ? "tip" : $"seg_{i}";
				rig.AddBone( name, i - 1, Xform.Translate( new Vec3( 0, segmentLength, 0 ) ), segmentLength );
			}
			return rig;
		}

		(DocumentSession Session, MarionettePlayer Player, Skeleton Rig, RigAnimFile Clip) CreateFixture(
			Skeleton rig,
			RigIk? ik = null,
			RigGoalSpace space = RigGoalSpace.Model,
			string? anchorObject = null,
			Xform? anchorXf = null )
		{
			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { Name = "base_box" } );
			studio.Rebuild();
			studio.Rig = rig;

			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			if ( ik != null )
			{
				clip.Ik = new List<RigIk> { ik };
				if ( space != RigGoalSpace.Model && !string.IsNullOrEmpty( anchorObject ) )
				{
					clip.GoalSpaces[ik.EndBone] = (space, anchorObject);
					clip.Objects.Add( new RigPlacement { Name = anchorObject, Pose = RigPose.FromXform( anchorXf ?? Xform.Identity ) } );
				}
			}

			var session = DocumentSession.CreateEffigy( "ik_test.effigy", studio, clip: clip );
			var player = new MarionettePlayer( rig );
			return (session, player, rig, clip);
		}

		// 1. Two-bone IK target drag
		Check( "two-bone IK target drag", () =>
		{
			var rig = CreateTwoBoneSkeleton();
			var ik = new RigIk
			{
				EndBone = "wrist",
				ChainLength = 2,
				Pole = new Vec3( 0, 0, 1 ),
				Weight = 1f,
				Enabled = true,
				Live = true
			};
			var (session, player, _, _) = CreateFixture( rig, ik );

			using var drag = IkTargetService.BeginIkDrag( session, "wrist", player );
			Require( drag.EndBoneIndex == 2, "End bone index should be 2." );
			Require( MathF.Abs( drag.StartTargetInModel.y - 10f ) < 1e-4f, "Start wrist position must be at Y=10." );

			// Drag target to reachable bent position (3, 7, 0)
			var target = new Vec3( 3, 7, 0 );
			bool ok = drag.UpdatePreview( target );
			Require( ok, "Preview update must succeed." );

			// Check wrist reached target within tolerance
			var currentWrist = drag.CurrentEndBoneWorldPose.Origin;
			float miss = (currentWrist - target).Length;
			Require( miss < 0.05f, $"Wrist missed target: distance = {miss}" );
			Require( drag.CurrentReached, "Target must be marked reached." );

			// Elbow must bend away from line X=0
			var currentElbow = drag.WorldPoses[1].Origin;
			Require( MathF.Abs( currentElbow.x ) > 0.1f || MathF.Abs( currentElbow.z ) > 0.1f,
				"Elbow must have bent to reach target." );

			// Hand bone must NOT have just translated locally to fake IK
			var wristLocal = drag.LocalPoses[2].Origin;
			Require( MathF.Abs( wristLocal.x ) < 1e-4f && MathF.Abs( wristLocal.z ) < 1e-4f,
				"Wrist local translation must not be faked." );

			// MarionettePlayer poses and GPU palette must reflect the preview
			Require( (player.WorldPose[2].Origin - target).Length < 0.05f,
				"Player world pose must reflect preview." );
			Require( player.GpuBones[2] != Matrix4x4.Identity,
				"Player GPU bone palette must be updated." );
		} );

		// 2. FABRIK / chain IK target drag
		Check( "FABRIK / chain IK target drag", () =>
		{
			var rig = CreateChainSkeleton( 4, 3f ); // root, seg_1, seg_2, tip (total length 9)
			var ik = new RigIk
			{
				EndBone = "tip",
				ChainLength = 3,
				Fabrik = true,
				Iterations = 15,
				Weight = 1f,
				Enabled = true,
				Live = true
			};
			var (session, player, _, _) = CreateFixture( rig, ik );

			using var drag = IkTargetService.BeginIkDrag( session, "tip", player );
			var target = new Vec3( 5, 2, 2 ); // distance ~ 5.74 < 9.0
			bool ok = drag.UpdatePreview( target );
			Require( ok, "FABRIK preview update must succeed." );

			float miss = (drag.CurrentEndBoneWorldPose.Origin - target).Length;
			Require( miss < 0.05f, $"FABRIK tip should reach target within tolerance, miss: {miss}" );
			Require( drag.CurrentReached, "FABRIK should report reached." );

			// Check that intermediate joints adjusted
			var seg1 = drag.WorldPoses[1].Origin;
			var seg2 = drag.WorldPoses[2].Origin;
			Require( MathF.Abs( seg1.x ) > 0.01f || MathF.Abs( seg1.z ) > 0.01f, "Segment 1 must rotate under FABRIK solve." );
			Require( MathF.Abs( seg2.x ) > 0.1f || MathF.Abs( seg2.z ) > 0.1f, "Segment 2 must rotate under FABRIK solve." );
		} );

		// 3. Pole vector influence
		Check( "pole vector influence", () =>
		{
			var rig = CreateTwoBoneSkeleton();
			var ikZPos = new RigIk
			{
				EndBone = "wrist",
				ChainLength = 2,
				Pole = new Vec3( 0, 0, 1 ),
				Weight = 1f,
				Enabled = true
			};
			var ikZNeg = new RigIk
			{
				EndBone = "wrist",
				ChainLength = 2,
				Pole = new Vec3( 0, 0, -1 ),
				Weight = 1f,
				Enabled = true
			};
			var (session, player, _, _) = CreateFixture( rig, ikZPos );

			var worldPoses = player.WorldPose.ToArray();
			var target = new Vec3( 0, 7, 0 ); // Closer target so elbow bends

			// Solve with +Z pole
			var resZPos = IkTargetService.Solve( rig, worldPoses, ikZPos, new RigPose( target, Quat.Identity, Vec3.One ) );
			Require( resZPos.Success, "ZPos solve must succeed." );
			float elbowZZPos = resZPos.SolvedWorldPoses![1].Origin.z;

			// Solve with -Z pole
			var resZNeg = IkTargetService.Solve( rig, worldPoses, ikZNeg, new RigPose( target, Quat.Identity, Vec3.One ) );
			Require( resZNeg.Success, "ZNeg solve must succeed." );
			float elbowZZNeg = resZNeg.SolvedWorldPoses![1].Origin.z;

			Require( elbowZZPos > 1.0f, $"Elbow should bend towards +Z, got Z={elbowZZPos}" );
			Require( elbowZZNeg < -1.0f, $"Elbow should bend towards -Z, got Z={elbowZZNeg}" );
			Require( elbowZZPos * elbowZZNeg < 0, "Opposite pole vectors must bend in opposite directions." );
		} );

		// 4. Zero IK weight leaves FK pose intact
		Check( "zero IK weight leaves FK pose intact", () =>
		{
			var rig = CreateTwoBoneSkeleton();
			var ik = new RigIk
			{
				EndBone = "wrist",
				ChainLength = 2,
				Pole = new Vec3( 0, 0, 1 ),
				Weight = 0f, // ZERO WEIGHT
				Enabled = true
			};
			var (session, player, _, _) = CreateFixture( rig, ik );

			// Capture FK poses before solve
			var initialLocal = player.Rig.Bones.Select( b => b.Local ).ToArray();
			var initialWorld = player.WorldPose.ToArray();

			using var drag = IkTargetService.BeginIkDrag( session, "wrist", player );
			// Drag target far away
			drag.UpdatePreview( new Vec3( 10, 20, 30 ) );

			// Zero weight must leave FK pose completely intact
			for ( int b = 0; b < rig.Count; b++ )
			{
				Require( PoseEditService.AreApproximatelyEqual( drag.LocalPoses[b], initialLocal[b], 1e-4f ),
					$"Bone '{rig.Bones[b].Name}' local pose changed under zero IK weight." );
				Require( PoseEditService.AreApproximatelyEqual( drag.WorldPoses[b], initialWorld[b], 1e-4f ),
					$"Bone '{rig.Bones[b].Name}' world pose changed under zero IK weight." );
			}
		} );

		// 5. Reachable target reaches goal within tolerance
		Check( "reachable target reaches goal within tolerance", () =>
		{
			var rig = CreateTwoBoneSkeleton();
			var ik = new RigIk
			{
				EndBone = "wrist",
				ChainLength = 2,
				Pole = new Vec3( 0, 0, 1 ),
				Weight = 1f,
				Enabled = true
			};
			var (session, player, _, _) = CreateFixture( rig, ik );

			// Arm total reach = 5 + 5 = 10. Target at distance ~ 7.35
			var target = new Vec3( 2, 7, 1 );
			var res = IkTargetService.Solve( rig, player.WorldPose.ToArray(), ik, new RigPose( target, Quat.Identity, Vec3.One ) );

			Require( res.Success, "Solve must succeed." );
			Require( res.Reached, "Target must be marked reached." );
			Require( res.MissDistance < 0.02f, $"Miss distance must be < 0.02, was {res.MissDistance}" );
			float dist = (res.SolvedEndPosition - target).Length;
			Require( dist < 0.02f, $"Actual end distance must be < 0.02, was {dist}" );
		} );

		// 6. Unreachable target clamps cleanly without NaNs
		Check( "unreachable target clamps cleanly without NaNs", () =>
		{
			var rig = CreateTwoBoneSkeleton(); // Reach = 10
			var ik = new RigIk
			{
				EndBone = "wrist",
				ChainLength = 2,
				Pole = new Vec3( 0, 0, 1 ),
				Weight = 1f,
				Enabled = true
			};
			var (session, player, _, _) = CreateFixture( rig, ik );

			// Target at distance 50 (far beyond 10)
			var target = new Vec3( 0, 50, 0 );
			var res = IkTargetService.Solve( rig, player.WorldPose.ToArray(), ik, new RigPose( target, Quat.Identity, Vec3.One ) );

			Require( res.Success, "Solve must succeed without crashing." );
			Require( !res.Reached, "Target must NOT be marked reached." );
			Require( res.MissDistance > 38f, $"Measurable miss distance expected, was {res.MissDistance}" );

			// Arm should be clamped near full reach ~ 10
			float reach = (res.SolvedEndPosition - rig.Bones[0].Local.Origin).Length;
			Require( MathF.Abs( reach - 10f ) < 0.1f, $"End effector should be clamped near full extension 10, got {reach}" );

			// Check all bones for NaNs or Infinities
			for ( int b = 0; b < rig.Count; b++ )
			{
				var xf = res.SolvedWorldPoses![b];
				Require( PoseEditService.IsValidTransform( xf ),
					$"Bone '{rig.Bones[b].Name}' transform contains NaNs or singular matrix." );
			}
		} );

		// 7. Commit creates 1 undo entry; cancel restores pose
		Check( "commit creates 1 undo entry; cancel restores pose", () =>
		{
			var rig = CreateTwoBoneSkeleton();
			var ik = new RigIk
			{
				EndBone = "wrist",
				ChainLength = 2,
				Pole = new Vec3( 0, 0, 1 ),
				Weight = 1f,
				Enabled = true
			};
			var (session, player, _, _) = CreateFixture( rig, ik );

			int initialUndoCount = session.History.UndoCount;
			var initialWristPos = player.WorldPose[2].Origin;

			// Step A: Drag and commit
			using ( var drag = IkTargetService.BeginIkDrag( session, "wrist", player ) )
			{
				// Perform 30 updates during drag
				for ( int i = 1; i <= 30; i++ )
				{
					drag.UpdatePreview( new Vec3( i * 0.1f, 8f, 0 ) );
				}
				drag.Commit();
			}

			// Must create EXACTLY 1 undo entry
			Require( session.History.UndoCount == initialUndoCount + 1,
				$"Expected 1 undo entry, found {session.History.UndoCount - initialUndoCount}" );
			Require( session.History.CanUndo, "Session should allow undo." );

			var committedWristPos = player.WorldPose[2].Origin;
			Require( (committedWristPos - new Vec3( 3.0f, 8f, 0 )).Length < 0.05f,
				"Committed wrist position must match final preview." );

			// Step B: Undo restores initial pose
			session.History.Undo();
			Require( (player.WorldPose[2].Origin - initialWristPos).Length < 1e-4f,
				"Undo must restore initial wrist pose." );

			// Step C: Redo re-applies committed pose
			session.History.Redo();
			Require( (player.WorldPose[2].Origin - committedWristPos).Length < 1e-4f,
				"Redo must re-apply committed wrist pose." );

			// Step D: Cancel restores initial pose without adding undo entry
			int preCancelCount = session.History.UndoCount;
			using ( var drag2 = IkTargetService.BeginIkDrag( session, "wrist", player ) )
			{
				drag2.UpdatePreview( new Vec3( -3f, 7f, 1f ) );
				drag2.Cancel();
			}

			Require( session.History.UndoCount == preCancelCount,
				"Cancel must not create an undo entry." );
			Require( (player.WorldPose[2].Origin - committedWristPos).Length < 1e-4f,
				"Cancel must restore pose to before drag2 started." );
		} );

		// 8. Missing constraint produces explicit diagnostic / safe return
		Check( "missing constraint produces explicit diagnostic / safe return", () =>
		{
			var rig = CreateTwoBoneSkeleton();
			// Fixture with NO IK constraint configured
			var (session, player, _, _) = CreateFixture( rig, ik: null );

			// Attempt to resolve on bone without constraint
			var res = IkTargetService.ResolveConstraint( session, "wrist" );
			Require( !res.Success, "Resolution must fail when no constraint exists." );
			Require( !string.IsNullOrWhiteSpace( res.Diagnostic ), "Diagnostic message must not be empty." );
			Require( res.Diagnostic?.Contains( "No IK constraint" ) ?? false, $"Diagnostic was: '{res.Diagnostic}'" );

			// Attempt TryBeginIkDrag
			bool started = IkTargetService.TryBeginIkDrag( session, "wrist", out var drag, out string? diag, player );
			Require( !started, "TryBeginIkDrag must safely return false." );
			Require( drag == null, "DragSession must be null on failure." );
			Require( !string.IsNullOrWhiteSpace( diag ), "Diagnostic must explain missing constraint." );

			// Attempt on non-existent bone
			bool nonExistent = IkTargetService.TryBeginIkDrag( session, "phantom_bone", out var drag2, out string? diag2, player );
			Require( !nonExistent, "TryBeginIkDrag must safely return false for missing bone." );
			Require( drag2 == null, "DragSession must be null." );
			Require( diag2!.Contains( "phantom_bone" ), "Diagnostic must reference the requested bone name." );

			// Verify session state was not corrupted
			Require( player.WorldPose.Length == rig.Count, "Player poses must remain intact." );
		} );

		// 9. Object moving under an anchored target
		Check( "object moving under an anchored target", () =>
		{
			var rig = CreateTwoBoneSkeleton();
			var ik = new RigIk
			{
				EndBone = "wrist",
				ChainLength = 2,
				Pole = new Vec3( 0, 0, 1 ),
				Weight = 1f,
				Enabled = true
			};
			var tableXf = Xform.Translate( new Vec3( 2, 5, 0 ) );
			var (session, player, _, _) = CreateFixture( rig, ik, RigGoalSpace.Object, "Table", tableXf );

			using var drag = IkTargetService.BeginIkDrag( session, "wrist", player );
			Require( drag.GoalSpace == RigGoalSpace.Object, "Drag session should recognize Object goal space." );

			// Place target at object-space relative position (0, 2, 0)
			// In model space, target is (2, 5, 0) + (0, 2, 0) = (2, 7, 0)
			drag.UpdatePreviewGoalInGoalSpace( new Vec3( 0, 2, 0 ) );
			Require( (drag.CurrentEndBoneWorldPose.Origin - new Vec3( 2, 7, 0 )).Length < 0.05f,
				"Wrist should reach target at initial table position." );

			// Now Table moves to (1, 6, 0)
			var newTableXf = Xform.Translate( new Vec3( 1, 6, 0 ) );
			drag.UpdateAnchorTransform( newTableXf );

			// In model space, target should now be (1, 6, 0) + (0, 2, 0) = (1, 8, 0)
			Require( (drag.CurrentEndBoneWorldPose.Origin - new Vec3( 1, 8, 0 )).Length < 0.05f,
				"Wrist must follow the moved anchor object." );
			Require( (drag.CurrentTargetInGoalSpace - new Vec3( 0, 2, 0 )).Length < 1e-4f,
				"Target in goal's declared space must remain constant." );
		} );

		// 10. Limits constrain joint rotation
		Check( "limits constrain joint rotation", () =>
		{
			var rig = CreateTwoBoneSkeleton();
			var ik = new RigIk
			{
				EndBone = "wrist",
				ChainLength = 2,
				Pole = new Vec3( 0, 0, 1 ),
				Weight = 1f,
				Enabled = true
			};
			// Add a tight limit on elbow: Max pitch 10 deg, yaw 10 deg, roll 10 deg
			var limits = new List<RigLimit>
			{
				new()
				{
					Bone = "elbow",
					Enabled = true,
					Weight = 1f,
					Min = new Vec3( -10, -10, -10 ),
					Max = new Vec3( 10, 10, 10 )
				}
			};

			var (session, player, _, _) = CreateFixture( rig, ik );
			var target = new Vec3( 0, 5, 0 ); // Would normally fold elbow ~90 degrees

			// Solve with limits
			var resLimited = IkTargetService.Solve( rig, player.WorldPose.ToArray(), ik, new RigPose( target, Quat.Identity, Vec3.One ), limits );
			Require( resLimited.Success, "Limited solve must succeed." );

			var (pitch, yaw, roll) = RigLimit.ToAngles( Quat.FromXform( resLimited.SolvedLocalPoses![1] ) );
			Require( MathF.Abs( pitch ) <= 10.1f && MathF.Abs( yaw ) <= 10.1f && MathF.Abs( roll ) <= 10.1f,
				$"Elbow rotation ({pitch:0.#}, {yaw:0.#}, {roll:0.#}) exceeded limit bounds ±10 deg." );
		} );

		// 11. Tentative solve does not mutate bind data or stored constraint
		Check( "tentative solve does not mutate bind data or stored constraint", () =>
		{
			var rig = CreateTwoBoneSkeleton();
			var ik = new RigIk
			{
				EndBone = "wrist",
				ChainLength = 2,
				Pole = new Vec3( 0, 0, 1 ),
				Weight = 1f,
				Enabled = true
			};
			var (session, player, _, _) = CreateFixture( rig, ik );

			// Capture bind data before drag
			var bindLocals = rig.Bones.Select( b => b.Local ).ToArray();
			float ikWeight = ik.Weight;
			var ikPole = ik.Pole;
			int ikChain = ik.ChainLength;

			using var drag = IkTargetService.BeginIkDrag( session, "wrist", player );
			for ( int i = 0; i < 50; i++ )
			{
				drag.UpdatePreview( new Vec3( i * 0.05f, 7f, 1f ) );
			}

			// Verify bind locals untouched
			for ( int b = 0; b < rig.Count; b++ )
			{
				Require( PoseEditService.AreApproximatelyEqual( rig.Bones[b].Local, bindLocals[b], 1e-6f ),
					$"Bind pose for bone '{rig.Bones[b].Name}' was mutated during drag preview!" );
			}

			// Verify stored constraint untouched
			Require( ik.Weight == ikWeight, "Stored constraint Weight was mutated!" );
			Require( (ik.Pole - ikPole).Length < 1e-5f, "Stored constraint Pole was mutated!" );
			Require( ik.ChainLength == ikChain, "Stored constraint ChainLength was mutated!" );
		} );

		// 12. Coordinate conversions between Model, World, and Object space
		Check( "coordinate conversions between Model, World, and Object space", () =>
		{
			var modelPoint = new Vec3( 4, 7, 2 );
			var objectXf = Xform.Translate( new Vec3( 10, -5, 3 ) ) * Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI / 4f );
			var worldXf = Xform.Translate( new Vec3( 100, 200, 300 ) );

			// Model space
			var inModel = IkTargetService.ModelToGoalSpace( modelPoint, RigGoalSpace.Model );
			Require( (inModel - modelPoint).Length < 1e-5f, "ModelToGoalSpace in Model space should be identity." );
			var backFromModel = IkTargetService.GoalSpaceToModel( inModel, RigGoalSpace.Model );
			Require( (backFromModel - modelPoint).Length < 1e-5f, "GoalSpaceToModel in Model space round-trip failed." );

			// World space
			var inWorld = IkTargetService.ModelToGoalSpace( modelPoint, RigGoalSpace.World, null, worldXf );
			var backFromWorld = IkTargetService.GoalSpaceToModel( inWorld, RigGoalSpace.World, null, worldXf );
			Require( (backFromWorld - modelPoint).Length < 1e-4f, "World space round-trip failed." );

			// Object space
			var inObject = IkTargetService.ModelToGoalSpace( modelPoint, RigGoalSpace.Object, objectXf );
			var backFromObject = IkTargetService.GoalSpaceToModel( inObject, RigGoalSpace.Object, objectXf );
			Require( (backFromObject - modelPoint).Length < 1e-4f, "Object space round-trip failed." );
		} );
	}
}
