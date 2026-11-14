using System;
using System.Collections.Generic;
using Effigy;

// Rig metadata parity with Mobile (preserve-mobile-rig-metadata-and-deformation-behavior).
// Mobile adds per-bone Bendy / Deform / EnvelopeRadius, PartStudio rig fields
// (bind method, envelope scale, per-bone flags, opaque workspace JSON), pole-bone IK,
// non-allocating SkinBinder.DeformInto and WeightStroke.MirrorBone. These checks prove
// the metadata survives a Desktop edit/save/reopen round-trip through StudioDocument,
// that a pole bone steers the IK solve, that DeformInto agrees with Deform, and that
// mirrored weight painting lands on the mirrored bone.
//
// NOT yet registered in TestRegistry.cs (coordinator-owned; the final integrator adds
// the one Register line, as with KernelParityTests.cs).
internal static class RigMetadataParityTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "rig-metadata-parity", name, test );
		void Require( bool condition, string message )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, string what, float tol = 1e-5f ) =>
			Require( MathF.Abs( actual - expected ) <= tol, $"{what}: expected {expected}, got {actual}" );
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, got silent success" );
		}

		static Skeleton TwoBoneRig()
		{
			var rig = new Skeleton();
			rig.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, 0 ), new Vec3( 0, 1, 0 ) );
			rig.AddBoneFromPoints( "arm_L", 0, new Vec3( 1, 0, 0 ), new Vec3( 5, 0, 0 ) );
			return rig;
		}

		Check( "bone bendy/deform/envelope survive document round-trip", () =>
		{
			var studio = new PartStudio { Rig = TwoBoneRig() };
			var arm = studio.Rig.Bones[1];
			arm.Deform = false;
			arm.EnvelopeRadius = 2f;
			arm.Bendy = new BendyBoneSettings { Segments = 4, EaseIn = 0.5f, EaseOut = 1.5f };

			var back = StudioDocument.Read( StudioDocument.Write( studio ) );
			Require( back.Rig.Count == 2, "rig should round-trip with both bones" );
			var backArm = back.Rig.Bones[1];
			Require( backArm.Name == "arm_L", "bone name should survive" );
			Require( backArm.Deform == false, "Deform=false should survive (control bone, not a deformer)" );
			Near( backArm.EnvelopeRadius, 2f, "EnvelopeRadius" );
			Require( backArm.Bendy is not null, "Bendy settings should survive" );
			Require( backArm.Bendy.Segments == 4, "Bendy.Segments" );
			Near( backArm.Bendy.EaseIn, 0.5f, "Bendy.EaseIn" );
			Near( backArm.Bendy.EaseOut, 1.5f, "Bendy.EaseOut" );

			// Defaults stay defaults: a rigid, unadorned bone writes no extra lines.
			var root = back.Rig.Bones[0];
			Require( root.Deform, "default Deform is true" );
			Near( root.EnvelopeRadius, 0f, "default EnvelopeRadius" );
			Require( root.Bendy is null, "default Bendy is null" );
		} );

		Check( "partstudio rig fields survive document round-trip", () =>
		{
			var studio = new PartStudio { Rig = TwoBoneRig() };
			studio.Rig.AddBoneFromPoints( "lower", 0, new Vec3( 0, 0, 0 ), new Vec3( 0, -1, 0 ) );
			studio.RigBindMethod = "envelope";
			studio.RigEnvelopeScale = 1.75f;
			studio.RigBoneFlags["lower"] = "connected,lockloc:xz";
			studio.RigWorkspaceJson = "{\"poses\":[\"idle\"]}";

			var back = StudioDocument.Read( StudioDocument.Write( studio ) );
			Require( back.RigBindMethod == "envelope", "RigBindMethod should survive" );
			Near( back.RigEnvelopeScale, 1.75f, "RigEnvelopeScale" );
			Require( back.RigBoneFlags.TryGetValue( "lower", out var flags ) && flags == "connected,lockloc:xz",
				"RigBoneFlags should survive" );
			Require( back.RigWorkspaceJson == "{\"poses\":[\"idle\"]}", "RigWorkspaceJson should survive" );

			// A document that never set them keeps the legacy defaults, byte-stable.
			var plain = StudioDocument.Read( StudioDocument.Write( new PartStudio { Rig = TwoBoneRig() } ) );
			Require( plain.RigBindMethod == "", "default RigBindMethod is empty" );
			Near( plain.RigEnvelopeScale, 1f, "default RigEnvelopeScale" );
			Require( plain.RigBoneFlags.Count == 0, "default RigBoneFlags is empty" );
			Require( plain.RigWorkspaceJson == "", "default RigWorkspaceJson is empty" );
		} );

		Check( "pole bone steers the two-bone solve; missing pole falls back", () =>
		{
			var rig = new Skeleton();
			rig.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, 0 ), new Vec3( 0, 1, 0 ) );
			rig.AddBoneFromPoints( "upper", 0, new Vec3( 0, 1, 0 ), new Vec3( 0, 2, 0 ) );
			rig.AddBoneFromPoints( "lower", 1, new Vec3( 0, 2, 0 ), new Vec3( 0, 3, 0 ) );
			rig.AddBoneFromPoints( "pole_L", -1, new Vec3( 0, 1, -2 ), new Vec3( 0, 2, -2 ) );
			var world = new[] { rig.WorldBind( 0 ), rig.WorldBind( 1 ), rig.WorldBind( 2 ), rig.WorldBind( 3 ) };
			var goal = new RigPose( new Vec3( 0.5f, 1.5f, 0 ), Quat.Identity, Vec3.One );

			var fixedPole = new RigIk { EndBone = "lower", ChainLength = 2, Pole = new Vec3( 0, 0, 1 ), GoalRotation = false };
			var solvedFixed = ChainIk.Apply( rig, world, fixedPole, goal );
			Require( solvedFixed is not null, "fixed-pole solve should succeed" );
			Require( solvedFixed[1].Origin.z > 0.1f, $"fixed pole +Z should bend the elbow +Z, got z={solvedFixed[1].Origin.z}" );
			Near( (solvedFixed[2].Origin - goal.Position).Length, 0f, "end should reach the goal", 1e-3f );

			var poleBone = new RigIk { EndBone = "lower", ChainLength = 2, Pole = new Vec3( 0, 0, 1 ), PoleBone = "pole_L", GoalRotation = false };
			// PoleFor: the named bone's head, relative to the chain root.
			var pole = poleBone.PoleFor( rig, world, world[0].Origin );
			Near( pole.x, 0f, "pole bone offset x" );
			Near( pole.y, 1f, "pole bone offset y" );
			Near( pole.z, -2f, "pole bone offset z" );
			var solvedPole = ChainIk.Apply( rig, world, poleBone, goal );
			Require( solvedPole is not null, "pole-bone solve should succeed" );
			Require( solvedPole[1].Origin.z < -0.1f, $"pole bone at -Z should bend the elbow -Z, got z={solvedPole[1].Origin.z}" );
			Near( (solvedPole[2].Origin - goal.Position).Length, 0f, "end should reach the goal with a pole bone", 1e-3f );

			// A named pole that does not exist, and an empty one, both fall back to Pole.
			var missing = new RigIk { Pole = new Vec3( 0, 0, 1 ), PoleBone = "no_such_bone" };
			Near( missing.PoleFor( rig, world, world[0].Origin ).z, 1f, "missing pole bone falls back to Pole" );
			var empty = new RigIk { Pole = new Vec3( 0, 0, 1 ), PoleBone = "" };
			Near( empty.PoleFor( rig, world, world[0].Origin ).z, 1f, "empty PoleBone falls back to Pole" );
		} );

		Check( "pole bone round-trips through RigIk json and the rig block", () =>
		{
			var ik = new RigIk { Name = "ArmIK", EndBone = "lower", ChainLength = 2, Pole = new Vec3( 0, 0, 1 ), PoleBone = "pole_L" };
			var json = ik.ToJson().ToJsonString();
			Require( json.Contains( "\"PoleBone\":\"pole_L\"" ), "ToJson should carry PoleBone" );
			var read = RigIk.ReadRig( "{\"IkConstraints\":[" + json + "]}" );
			Require( read.Count == 1 && read[0].PoleBone == "pole_L", "ReadRig should restore PoleBone" );

			var studio = new PartStudio { Rig = TwoBoneRig() };
			studio.IkChains.Add( ik );
			var back = StudioDocument.Read( StudioDocument.Write( studio ) );
			Require( back.IkChains.Count == 1 && back.IkChains[0].PoleBone == "pole_L",
				"PoleBone should survive the .effigy rig block" );
		} );

		Check( "DeformInto matches Deform and rejects short buffers", () =>
		{
			var positions = new[] { new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 1, 1, 0 ) };
			var weights = SkinWeights.AllTo( 4, 0 );
			weights[1] = new[] { new BoneWeight( 0, 0.5f ), new BoneWeight( 1, 0.5f ) };
			weights[2] = new[] { new BoneWeight( 1, 1f ) };
			var bind = new[] { Xform.Identity, Xform.Translate( new Vec3( 0, 1, 0 ) ) };
			var pose = new[] { Xform.Identity, Xform.Translate( new Vec3( 0, 2, 0 ) ) };

			var expected = SkinBinder.Deform( positions, weights, bind, pose );
			var into = new Vec3[positions.Length];
			var skin = new Xform[bind.Length];
			SkinBinder.DeformInto( positions, weights, bind, pose, into, skin );
			for ( var v = 0; v < positions.Length; v++ )
				Near( (into[v] - expected[v]).Length, 0f, $"DeformInto vertex {v}", 1e-5f );

			// Ready-made skin matrices, and the scalar path (a List, not an array).
			var into2 = new Vec3[positions.Length];
			var ready = new Xform[bind.Length];
			for ( var b = 0; b < bind.Length; b++ ) ready[b] = pose[b] * bind[b].Inverse;
			SkinBinder.DeformInto( positions, weights, ready, into2 );
			for ( var v = 0; v < positions.Length; v++ )
				Near( (into2[v] - expected[v]).Length, 0f, $"DeformInto ready-skin vertex {v}", 1e-5f );
			var listPositions = new List<Vec3>( positions );
			var into3 = new Vec3[positions.Length];
			SkinBinder.DeformInto( listPositions, weights, ready, into3 );
			for ( var v = 0; v < positions.Length; v++ )
				Near( (into3[v] - expected[v]).Length, 0f, $"DeformInto list vertex {v}", 1e-5f );

			Throws<ArgumentException>( () => SkinBinder.DeformInto( positions, weights, bind, pose, new Vec3[2], new Xform[2] ),
				"short result buffer" );
			Throws<ArgumentException>( () => SkinBinder.DeformInto( positions, weights, bind, pose, new Vec3[4], new Xform[1] ),
				"short skin buffer" );
			Throws<ArgumentNullException>( () => SkinBinder.DeformInto( positions, weights, null, pose, new Vec3[4], new Xform[2] ),
				"null bind" );
		} );

		Check( "WeightStroke.MirrorBone paints the mirrored bone", () =>
		{
			var mesh = new PolyMesh();
			for ( var j = 0; j <= 10; j++ )
				for ( var i = 0; i <= 10; i++ )
					mesh.AddVertex( new Vec3( i - 5, j - 5, 0 ) );
			for ( var j = 0; j < 10; j++ )
				for ( var i = 0; i < 10; i++ )
					mesh.AddFace( new[] { j * 11 + i, j * 11 + i + 1, (j + 1) * 11 + i + 1, (j + 1) * 11 + i } );
			var rig = TwoBoneRig();
			mesh.Skin = SkinWeights.AllTo( mesh.VertexCount, 0 );
			static float W( PolyMesh m, int v, int bone ) =>
				m.Skin[v] is { } w ? System.Linq.Enumerable.Sum( System.Linq.Enumerable.Where( w, x => x.Bone == bone ), x => x.Weight ) : 0f;
			static int At( int x, int y ) => (y + 5) * 11 + (x + 5);

			var stroke = new WeightStroke
			{
				Kind = WeightBrushKind.Add,
				Falloff = BrushFalloff.Constant,
				Bone = 1,
				Mirror = MirrorAxis.X,
				MirrorBone = 2,
			};
			stroke.Samples.Add( new WeightSample( new Vec3( 2, 0, 0 ), 0.5f, 1f ) );
			WeightBrush.Apply( mesh, mesh.Skin, stroke );
			Require( W( mesh, At( 2, 0 ), 1 ) > 0.5f, "original side should gain arm_L" );
			Require( W( mesh, At( -2, 0 ), 2 ) > 0.5f, "mirrored side should gain arm_R via MirrorBone" );
			Require( W( mesh, At( -2, 0 ), 1 ) < 0.01f, "mirrored side should not gain arm_L when MirrorBone is set" );

			// Without MirrorBone the mirrored sample paints the same bone on both sides.
			var mesh2 = new PolyMesh();
			for ( var j = 0; j <= 10; j++ )
				for ( var i = 0; i <= 10; i++ )
					mesh2.AddVertex( new Vec3( i - 5, j - 5, 0 ) );
			for ( var j = 0; j < 10; j++ )
				for ( var i = 0; i < 10; i++ )
					mesh2.AddFace( new[] { j * 11 + i, j * 11 + i + 1, (j + 1) * 11 + i + 1, (j + 1) * 11 + i } );
			mesh2.Skin = SkinWeights.AllTo( mesh2.VertexCount, 0 );
			var plain = new WeightStroke
			{
				Kind = WeightBrushKind.Add,
				Falloff = BrushFalloff.Constant,
				Bone = 1,
				Mirror = MirrorAxis.X,
			};
			Require( plain.MirrorBone == -1, "MirrorBone defaults to -1 (original bone)" );
			plain.Samples.Add( new WeightSample( new Vec3( 2, 0, 0 ), 0.5f, 1f ) );
			WeightBrush.Apply( mesh2, mesh2.Skin, plain );
			Require( W( mesh2, At( -2, 0 ), 1 ) > 0.5f, "without MirrorBone the mirrored side paints the same bone" );
		} );

		Check( "clone and mirror keep bendy/deform/envelope; side names pair up", () =>
		{
			var rig = TwoBoneRig();
			var arm = rig.Bones[1];
			arm.Deform = false;
			arm.EnvelopeRadius = 2f;
			arm.Bendy = new BendyBoneSettings { Segments = 4, EaseIn = 0.5f, EaseOut = 1.5f };
			var clone = arm.Clone();
			Require( clone.Deform == false && MathF.Abs( clone.EnvelopeRadius - 2f ) < 1e-6f, "Clone keeps Deform/EnvelopeRadius" );
			Require( clone.Bendy is not null && clone.Bendy.Segments == 4, "Clone keeps Bendy" );
			clone.Bendy.Segments = 9;
			Require( arm.Bendy.Segments == 4, "Clone deep-copies Bendy (editing the clone must not move the original)" );

			var mirrored = rig.MirrorSubtree( 1, new Vec3( 1, 0, 0 ) );
			Require( mirrored >= 0, "MirrorSubtree should create the counterpart" );
			var twin = rig.Bones[mirrored];
			Require( twin.Name == "arm_R", $"mirrored bone should be arm_R, got {twin.Name}" );
			Require( twin.Deform == false && MathF.Abs( twin.EnvelopeRadius - 2f ) < 1e-6f, "mirror keeps Deform/EnvelopeRadius" );
			Require( twin.Bendy is not null && twin.Bendy.Segments == 4, "mirror keeps Bendy" );

			Require( Skeleton.FlipSideSuffix( "left_arm" ) == "right_arm", "Left/Right word form" );
			Require( Skeleton.FlipSideSuffix( "l_leg" ) == "r_leg", "l_/r_ prefix" );
			Require( Skeleton.FlipSideSuffix( "finger_L1" ) == "finger_R1", "side letter with trailing digits" );
			Require( Skeleton.FlipSideSuffix( "arm.L" ) == "arm.R", ".L/.R form" );
			Require( Skeleton.FlipSideSuffix( "LeftHand" ) == "RightHand", "Left prefix word" );
			Require( Skeleton.FlipSideSuffix( "spine" ) is null, "centre bone has no partner" );
			Require( Skeleton.FlipSideSuffix( null ) is null, "null name has no partner" );
			Require( Skeleton.FlipSideSuffix( "" ) is null, "empty name has no partner" );
		} );
	}
}
