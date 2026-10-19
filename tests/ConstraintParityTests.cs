#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Marionette.Desktop;

internal static class ConstraintParityTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "constraint-parity", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		// 1. Constraint audit correctly categorizes look-at, copy transform, IK, and springs
		Check( "constraint audit correctly categorizes look-at, copy transform, IK, and springs", () =>
		{
			string rigJson = """
			{
				"IkConstraints": [
					{ "Name": "ArmIK_L", "TargetBone": "wrist_L", "ChainLength": 2, "Solver": "Ccd", "Weight": 1.0, "Enabled": true }
				],
				"LimitConstraints": [
					{ "Name": "ElbowLimit_L", "TargetBone": "elbow_L", "MinAngles": "-10,-90,-10", "MaxAngles": "10,90,10", "Weight": 1.0 }
				],
				"SoftChains": [
					{ "Name": "AntennaSpring", "Bone": "antenna_tip", "Preset": "Loose", "Stiffness": 150.0, "Damping": 0.002, "Rope": false }
				],
				"SoftColliders": [
					{ "Bone": "head", "Radius": 6.5 }
				],
				"LookAtConstraints": [
					{ "Name": "EyeAim", "TargetBone": "eye_L", "SourceBone": "aim_target", "Weight": 0.8 }
				],
				"CopyRotationConstraints": [
					{ "Name": "ShoulderCopy", "TargetBone": "shoulder_R", "SourceBone": "shoulder_L", "Weight": 0.5 }
				],
				"CopyTransformConstraints": [
					{ "Name": "PropAttachment", "TargetBone": "prop_socket", "SourceBone": "hand_R", "Weight": 1.0 }
				],
				"MorphDrivers": [
					{ "Morph": "bicep_flex", "Bone": "elbow_L", "Channel": "Rotation" }
				]
			}
			""";

			var audit = ConstraintParityInventory.AuditRigJson( rigJson );

			Require( audit.TotalCount == 8, $"Expected 8 constraints audited, got {audit.TotalCount}" );
			Require( audit.HasCategory( ConstraintCategory.Ik ), "Expected IK category" );
			Require( audit.HasCategory( ConstraintCategory.Limit ), "Expected Limit category" );
			Require( audit.HasCategory( ConstraintCategory.SecondarySpring ), "Expected SecondarySpring category" );
			Require( audit.HasCategory( ConstraintCategory.LookAt ), "Expected LookAt category" );
			Require( audit.HasCategory( ConstraintCategory.CopyRotation ), "Expected CopyRotation category" );
			Require( audit.HasCategory( ConstraintCategory.CopyTransform ), "Expected CopyTransform category" );
			Require( audit.HasCategory( ConstraintCategory.MorphDriver ), "Expected MorphDriver category" );

			var ikItems = audit.GetByCategory( ConstraintCategory.Ik );
			Require( ikItems.Count == 1 );
			Require( ikItems[0].TargetBone == "wrist_L" );
			Require( ikItems[0].SupportStatus == ConstraintSupportStatus.StandaloneSupported );

			var lookAtItems = audit.GetByCategory( ConstraintCategory.LookAt );
			Require( lookAtItems.Count == 1 );
			Require( lookAtItems[0].TargetBone == "eye_L" );
			Require( lookAtItems[0].SourceBone == "aim_target" );
			Require( lookAtItems[0].SupportStatus == ConstraintSupportStatus.DesktopExtended );

			var copyRotItems = audit.GetByCategory( ConstraintCategory.CopyRotation );
			Require( copyRotItems.Count == 1 );
			Require( copyRotItems[0].TargetBone == "shoulder_R" );
			Require( copyRotItems[0].SourceBone == "shoulder_L" );

			var copyTxItems = audit.GetByCategory( ConstraintCategory.CopyTransform );
			Require( copyTxItems.Count == 1 );
			Require( copyTxItems[0].TargetBone == "prop_socket" );

			var springItems = audit.GetByCategory( ConstraintCategory.SecondarySpring );
			Require( springItems.Count == 2, $"Expected 2 spring/collider items, got {springItems.Count}" );
			Require( springItems.Any( s => s.TargetBone == "antenna_tip" ) );
			Require( springItems.Any( s => s.TargetBone == "head" ) );
		} );

		// 2. Secondary spring simulation advances with deterministic fixed time steps
		Check( "secondary spring simulation advances with deterministic fixed time steps", () =>
		{
			var sim1 = new SecondarySpringSimulation { FixedDeltaTime = 1f / 120f };
			var sim2 = new SecondarySpringSimulation { FixedDeltaTime = 1f / 120f };

			sim1.AddNode( "tail", new Vec3( 0, 0, 0 ), stiffness: 120f, damping: 0.01f );
			sim2.AddNode( "tail", new Vec3( 0, 0, 0 ), stiffness: 120f, damping: 0.01f );

			var target = new Dictionary<string, Vec3> { ["tail"] = new Vec3( 10, 5, -2 ) };

			// sim1 advances with one large 1/60s step (which sub-steps into two 1/120s sub-steps)
			int steps1 = sim1.Step( 1f / 60f, target );
			Require( steps1 == 2, $"Expected 2 sub-steps for 1/60s at 120Hz, got {steps1}" );

			// sim2 advances with two explicit 1/120s steps
			int sub1 = sim2.Step( 1f / 120f, target );
			int sub2 = sim2.Step( 1f / 120f, target );
			Require( sub1 == 1 && sub2 == 1 );

			var pos1 = sim1.GetPosition( "tail" );
			var pos2 = sim2.GetPosition( "tail" );

			Require( MathF.Abs( pos1.x - pos2.x ) < 1e-6f, $"Fixed step mismatch X: {pos1.x} vs {pos2.x}" );
			Require( MathF.Abs( pos1.y - pos2.y ) < 1e-6f, $"Fixed step mismatch Y: {pos1.y} vs {pos2.y}" );
			Require( MathF.Abs( pos1.z - pos2.z ) < 1e-6f, $"Fixed step mismatch Z: {pos1.z} vs {pos2.z}" );

			// Bit-exact reproducibility over 100 steps
			for ( int i = 0; i < 100; i++ )
			{
				float offset = MathF.Sin( i * 0.1f ) * 5f;
				var dynamicTarget = new Dictionary<string, Vec3> { ["tail"] = new Vec3( offset, 0, 0 ) };
				sim1.Step( 1f / 60f, dynamicTarget );
				sim2.Step( 1f / 60f, dynamicTarget );
			}

			var final1 = sim1.GetPosition( "tail" );
			var final2 = sim2.GetPosition( "tail" );
			Require( final1.Equals( final2 ), "100-step simulation must produce bit-exact identical positions" );
		} );

		// 3. Spring simulation resets cleanly on seek or clip reload
		Check( "spring simulation resets cleanly on seek or clip reload", () =>
		{
			var sim = new SecondarySpringSimulation { FixedDeltaTime = 1f / 120f };
			sim.AddNode( "cable", new Vec3( 0, 0, 0 ), stiffness: 100f, damping: 0.1f );

			// Advance simulation with a moving target to build momentum and displacement
			for ( int i = 0; i < 30; i++ )
			{
				sim.Step( 1f / 60f, new Dictionary<string, Vec3> { ["cable"] = new Vec3( i * 2f, 0, 0 ) } );
			}

			var movingPos = sim.GetPosition( "cable" );
			Require( movingPos.Length > 5f, "Simulation should have accumulated displacement" );

			// Seek to a new playhead frame at position (40, 0, 0)
			var seekTarget = new Dictionary<string, Vec3> { ["cable"] = new Vec3( 40, 0, 0 ) };
			sim.ResetOnSeek( seekTarget );

			Require( sim.TryGetNode( "cable", out var node ) && node != null );
			Require( node!.Tail.AlmostEquals( new Vec3( 40, 0, 0 ), 1e-5f ), "Tail must be at seek position" );
			Require( node.Previous.AlmostEquals( new Vec3( 40, 0, 0 ), 1e-5f ), "Previous must equal tail (zero velocity)" );
			Require( node.Velocity( sim.FixedDeltaTime ).Length < 1e-6f, "Velocity must be zero after seek" );

			// Next step holding target at (40, 0, 0) should remain at rest without flying off
			sim.Step( 1f / 120f, seekTarget );
			var afterSeekPos = sim.GetPosition( "cable" );
			float drift = (afterSeekPos - new Vec3( 40, 0, 0 )).Length;
			Require( drift < 0.1f, $"Post-seek drift should be near-zero, got {drift}" );

			// Test clip reload reset
			sim.ResetOnClipReload( new Dictionary<string, Vec3> { ["cable"] = new Vec3( 0, 0, 0 ) } );
			Require( sim.GetPosition( "cable" ).AlmostEquals( Vec3.Zero, 1e-5f ) );
			Require( node.Velocity( sim.FixedDeltaTime ).Length < 1e-6f );
		} );

		// 4. Look-at constraint points bone towards target position
		Check( "look-at constraint points bone towards target position", () =>
		{
			// Bone origin at (0, 0, 10), target at (0, 50, 10). Bone default aim axis is (0, 1, 0)
			var boneOrigin = new Vec3( 0, 0, 10 );
			var initialBone = Xform.Translate( boneOrigin );
			var targetPos = new Vec3( 20, 30, 40 );
			var aimAxis = new Vec3( 0, 1, 0 );

			var solved = ConstraintParityInventory.EvaluateLookAt( initialBone, targetPos, weight: 1.0f, aimAxis: aimAxis );

			var expectedDir = (targetPos - boneOrigin).Normal;
			var solvedAimDir = solved.TransformDirection( aimAxis ).Normal;

			float dot = Vec3.Dot( solvedAimDir, expectedDir );
			Require( dot > 0.999f, $"Look-at direction should point at target. Dot: {dot}" );
			Require( solved.Origin.AlmostEquals( boneOrigin, 1e-4f ), "Look-at should preserve bone origin" );

			// Test partial weight (0.5)
			var partialSolved = ConstraintParityInventory.EvaluateLookAt( initialBone, targetPos, weight: 0.5f, aimAxis: aimAxis );
			var partialAimDir = partialSolved.TransformDirection( aimAxis ).Normal;
			var initialAimDir = initialBone.TransformDirection( aimAxis ).Normal;

			float initialDot = Vec3.Dot( partialAimDir, initialAimDir );
			float targetDot = Vec3.Dot( partialAimDir, expectedDir );
			Require( initialDot > 0.5f && targetDot > 0.5f, "Partial look-at must blend toward target direction" );
		} );

		// 5. Copy rotation constraint transfers orientation with weight scaling
		Check( "copy rotation constraint transfers orientation with weight scaling", () =>
		{
			var q0 = Quat.Identity;
			var q1 = Quat.FromAngles( 45f, 60f, 30f );

			// Weight 0: original rotation
			var res0 = ConstraintParityInventory.EvaluateCopyRotation( q0, q1, 0.0f );
			Require( MathF.Abs( res0.x - q0.x ) < 1e-5f && MathF.Abs( res0.w - q0.w ) < 1e-5f, "Weight 0 should keep source" );

			// Weight 1: target rotation
			var res1 = ConstraintParityInventory.EvaluateCopyRotation( q0, q1, 1.0f );
			Require( MathF.Abs( res1.x - q1.x ) < 1e-4f && MathF.Abs( res1.w - q1.w ) < 1e-4f, "Weight 1 should reach target" );

			// Weight 0.5: midpoint slerp
			var resMid = ConstraintParityInventory.EvaluateCopyRotation( q0, q1, 0.5f );
			var expectedMid = Quat.Slerp( q0, q1, 0.5f );
			Require( MathF.Abs( resMid.x - expectedMid.x ) < 1e-4f && MathF.Abs( resMid.w - expectedMid.w ) < 1e-4f, "Weight 0.5 must match Slerp" );

			// Copy transform overload
			var xf0 = Xform.Translate( new Vec3( 1, 2, 3 ) );
			var xf1 = q1.ToXform( new Vec3( 10, 20, 30 ) );
			var xfMid = ConstraintParityInventory.EvaluateCopyTransform( xf0, xf1, 0.5f );
			Require( xfMid.Origin.AlmostEquals( new Vec3( 5.5f, 11f, 16.5f ), 1e-4f ), "Position must lerp" );
			var xfMidRot = Quat.FromXform( xfMid );
			Require( MathF.Abs( xfMidRot.w - expectedMid.w ) < 1e-3f, "Rotation must slerp" );
		} );

		// 6. Unsupported constraint in rig reports clear diagnostic rather than crashing
		Check( "unsupported constraint in rig reports clear diagnostic rather than crashing", () =>
		{
			string jsonWithUnsupported = """
			{
				"IkConstraints": [
					{ "Name": "LegIK", "TargetBone": "ankle_L", "ChainLength": 2 }
				],
				"Constraints": [
					{ "Type": "ClothSimulationConstraint", "Name": "CapeCloth", "TargetBone": "cape_0" },
					{ "Type": "FullRagdollPhysicsConstraint", "Name": "BodyRagdoll", "TargetBone": "pelvis" },
					{ "Type": "FutureConstraint_v10", "Name": "HyperspaceWarp", "TargetBone": "root" }
				],
				"VolumetricSoftBodyConstraints": [
					{ "Name": "MuscleDeform", "Bone": "thigh_L" }
				]
			}
			""";

			// Must not throw or crash
			var audit = ConstraintParityInventory.AuditRigJson( jsonWithUnsupported );

			Require( audit.HasUnsupported, "Rig with unknown constraints must flag HasUnsupported" );
			Require( audit.UnsupportedCount >= 3, $"Expected at least 3 unsupported constraints, got {audit.UnsupportedCount}" );
			Require( audit.Diagnostics.Count > 0, "Audit must contain diagnostic messages" );

			Require( audit.Diagnostics.Any( d => d.Contains( "ClothSimulationConstraint" ) || d.Contains( "FullRagdollPhysicsConstraint" ) ),
				"Diagnostics should name unsupported constraint types" );
			Require( audit.Diagnostics.Any( d => d.Contains( "VolumetricSoftBodyConstraints" ) ),
				"Diagnostics should report unrecognized top-level block" );

			var cloth = audit.Items.FirstOrDefault( i => i.Name == "CapeCloth" );
			Require( cloth != null );
			Require( !cloth!.IsSupported );
			Require( cloth.SupportStatus == ConstraintSupportStatus.EngineDependent );
			Require( cloth.Diagnostic != null && cloth.Diagnostic.Contains( "standalone engine-free mode" ) );
		} );

		// 7. Constraints documentation markdown file exists and has required sections
		Check( "constraints documentation markdown file exists and has required sections", () =>
		{
			string[] candidates = new[]
			{
				Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "../../../packaging/CONSTRAINTS.md" ) ),
				Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "../../../../packaging/CONSTRAINTS.md" ) ),
				Path.GetFullPath( "packaging/CONSTRAINTS.md" ),
				"/home/poo/games/projects/geppetto-desktop/packaging/CONSTRAINTS.md"
			};
			string path = candidates.FirstOrDefault( File.Exists ) ?? candidates[0];

			Require( File.Exists( path ), $"Constraints documentation file must exist at '{path}'" );

			string content = File.ReadAllText( path );
			Require( content.Length > 500, "CONSTRAINTS.md must contain comprehensive documentation" );

			// Check for required core sections
			Require( content.Contains( "Executive Summary", StringComparison.OrdinalIgnoreCase ), "Missing Executive Summary section" );
			Require( content.Contains( "Control Rig", StringComparison.OrdinalIgnoreCase ), "Missing Control Rig section" );
			Require( content.Contains( "Inverse Kinematics", StringComparison.OrdinalIgnoreCase ) || content.Contains( "IkConstraints", StringComparison.OrdinalIgnoreCase ), "Missing IK section" );
			Require( content.Contains( "Limit Constraints", StringComparison.OrdinalIgnoreCase ) || content.Contains( "RigLimit", StringComparison.OrdinalIgnoreCase ), "Missing Limits section" );
			Require( content.Contains( "Secondary Motion", StringComparison.OrdinalIgnoreCase ) || content.Contains( "Soft Chains", StringComparison.OrdinalIgnoreCase ), "Missing Secondary Motion section" );
			Require( content.Contains( "Look-At", StringComparison.OrdinalIgnoreCase ), "Missing Look-At section" );
			Require( content.Contains( "Copy Rotation", StringComparison.OrdinalIgnoreCase ), "Missing Copy Rotation section" );
			Require( content.Contains( "Morph Drivers", StringComparison.OrdinalIgnoreCase ) || content.Contains( "MorphDriver", StringComparison.OrdinalIgnoreCase ), "Missing Morph Drivers section" );
			Require( content.Contains( "Cross-Reference Matrix", StringComparison.OrdinalIgnoreCase ) || content.Contains( "Kernel Support", StringComparison.OrdinalIgnoreCase ), "Missing Matrix section" );
			Require( content.Contains( "Deterministic", StringComparison.OrdinalIgnoreCase ), "Missing Determinism section" );
			Require( content.Contains( "Diagnostics", StringComparison.OrdinalIgnoreCase ), "Missing Diagnostics section" );
			Require( content.Contains( "Roadmap", StringComparison.OrdinalIgnoreCase ) || content.Contains( "Follow-Up", StringComparison.OrdinalIgnoreCase ), "Missing Roadmap section" );

			// Check for cross-referenced kernel APIs
			Require( content.Contains( "TwoBoneIk" ), "Must document TwoBoneIk" );
			Require( content.Contains( "ChainIk" ), "Must document ChainIk" );
			Require( content.Contains( "ChainSpring" ), "Must document ChainSpring" );
			Require( content.Contains( "RigLimit" ), "Must document RigLimit" );
			Require( content.Contains( "MorphDriver" ), "Must document MorphDriver" );
		} );

		// 8. Spring stiffness and damping parameters alter settling response deterministically
		Check( "spring stiffness and damping parameters alter settling response deterministically", () =>
		{
			// Setup 3 simulations with varied parameters
			var simUnderdamped = new SecondarySpringSimulation { FixedDeltaTime = 1f / 120f };
			var simOverdamped = new SecondarySpringSimulation { FixedDeltaTime = 1f / 120f };
			var simStiff = new SecondarySpringSimulation { FixedDeltaTime = 1f / 120f };

			// Underdamped: damping = 0.5f (retains 50% velocity after 1 sec -> oscillates longer)
			simUnderdamped.AddNode( "joint", Vec3.Zero, stiffness: 100f, damping: 0.5f, gravityWeight: 0f );

			// Overdamped: damping = 0.0001f (retains almost 0 velocity after 1 sec -> settles quickly)
			simOverdamped.AddNode( "joint", Vec3.Zero, stiffness: 100f, damping: 0.0001f, gravityWeight: 0f );

			// Stiff: stiffness = 400f
			simStiff.AddNode( "joint", Vec3.Zero, stiffness: 400f, damping: 0.01f, gravityWeight: 0f );

			var stepDisplacement = new Dictionary<string, Vec3> { ["joint"] = new Vec3( 10, 0, 0 ) };

			var underdampedPositions = new List<float>();
			var overdampedPositions = new List<float>();
			var stiffPositions = new List<float>();

			for ( int step = 0; step < 80; step++ )
			{
				simUnderdamped.StepFixed( 1f / 120f, stepDisplacement );
				simOverdamped.StepFixed( 1f / 120f, stepDisplacement );
				simStiff.StepFixed( 1f / 120f, stepDisplacement );

				underdampedPositions.Add( simUnderdamped.GetPosition( "joint" ).x );
				overdampedPositions.Add( simOverdamped.GetPosition( "joint" ).x );
				stiffPositions.Add( simStiff.GetPosition( "joint" ).x );
			}

			// Parameter alterations must yield measurably different trajectories
			Require( !underdampedPositions.SequenceEqual( overdampedPositions ), "Damping must change trajectory" );
			Require( !underdampedPositions.SequenceEqual( stiffPositions ), "Stiffness must change trajectory" );

			// Underdamped should oscillate wildly with huge overshoot (> 18.0) and swing back (< 4.0)
			float underdampedMax = underdampedPositions.Max();
			float underdampedMinAfterPeak = underdampedPositions.Skip( 40 ).Min();
			Require( underdampedMax > 16f, $"Underdamped spring should have large overshoot, got {underdampedMax}" );
			Require( underdampedMinAfterPeak < 4f, $"Underdamped spring should swing back far below target, got {underdampedMinAfterPeak}" );

			// Overdamped should settle smoothly with much smaller overshoot (< 12.5) and no severe swing-back (> 9.0)
			float overdampedMax = overdampedPositions.Max();
			float overdampedMinAfterPeak = overdampedPositions.Skip( 40 ).Min();
			Require( overdampedMax < 12.5f, $"Overdamped spring should have small overshoot, got {overdampedMax}" );
			Require( overdampedMinAfterPeak > 9.0f, $"Overdamped spring should remain near target without swinging back, got {overdampedMinAfterPeak}" );

			// Stiff spring responds faster in initial steps
			Require( stiffPositions[10] > overdampedPositions[10], "Stiff spring must accelerate faster than normal stiffness" );
		} );
	}
}
