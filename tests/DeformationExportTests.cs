#nullable enable
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Marionette.Desktop;

/// <summary>Deformation export correctness (godlike-deformation-export-correctness): the DQS
/// preview against LBS on a shoulder fixture, twist-bone generation and roll distribution,
/// pose-driven correctives, skeleton mapping to Unity/Unreal/Mixamo/HumanIK with round-trip,
/// pre-export validation, and the deformation stress test. All checks run on small synthetic
/// skeletons and meshes — no files, no GPU, no viewer. The dual-quaternion skinning itself
/// lives in RiggingWave3.cs (DualQuaternionSkinning, preview-only); this task adds the
/// acceptance surface around it, it does not re-implement it.</summary>
internal static class DeformationExportTests
{
	/// <summary>A T-pose humanoid: arms out to the sides, legs straight down, root at the
	/// origin. Bone directions are what ExportValidation reads, so the arm bones' local +Y
	/// points sideways (clavicle Local = Translate * RotZ(±90°), children inherit it) and
	/// the leg bones' local +Y points down (leg_upper Local = Translate * RotX(180°)).</summary>
	static Skeleton Humanoid( bool armsDown = false )
	{
		var s = new Skeleton();
		var root = s.AddBone( "root", -1, Xform.Identity, 1f );
		var pelvis = s.AddBone( "pelvis", root, Xform.Translate( new Vec3( 0, 1, 0 ) ), 0.3f );
		var spine0 = s.AddBone( "spine_0", pelvis, Xform.Translate( new Vec3( 0, 0.2f, 0 ) ), 0.2f );
		var spine1 = s.AddBone( "spine_1", spine0, Xform.Translate( new Vec3( 0, 0.2f, 0 ) ), 0.2f );
		var spine2 = s.AddBone( "spine_2", spine1, Xform.Translate( new Vec3( 0, 0.15f, 0 ) ), 0.15f );
		var neck = s.AddBone( "neck_0", spine2, Xform.Translate( new Vec3( 0, 0.15f, 0 ) ), 0.1f );
		_ = s.AddBone( "head", neck, Xform.Translate( new Vec3( 0, 0.1f, 0 ) ), 0.25f );
		var z = new Vec3( 0, 0, 1 );
		var x = new Vec3( 1, 0, 0 );
		foreach ( var side in new[] { "L", "R" } )
		{
			var sign = side == "L" ? -1f : 1f;
			var twist = side == "L" ? MathF.PI / 2 : -MathF.PI / 2;
			var clav = armsDown
				? s.AddBone( $"clavicle_{side}", spine2, Xform.Translate( new Vec3( sign * 0.15f, 0.1f, 0 ) ) * Xform.Rotate( x, MathF.PI ), 0.15f )
				: s.AddBone( $"clavicle_{side}", spine2, Xform.Translate( new Vec3( sign * 0.15f, 0.1f, 0 ) ) * Xform.Rotate( z, twist ), 0.15f );
			var upper = s.AddBone( $"arm_upper_{side}", clav, Xform.Translate( new Vec3( 0, 0.15f, 0 ) ), 0.3f );
			var lower = s.AddBone( $"arm_lower_{side}", upper, Xform.Translate( new Vec3( 0, 0.3f, 0 ) ), 0.3f );
			_ = s.AddBone( $"hand_{side}", lower, Xform.Translate( new Vec3( 0, 0.3f, 0 ) ), 0.15f );
			var legUpper = s.AddBone( $"leg_upper_{side}", pelvis, Xform.Translate( new Vec3( sign * 0.1f, -0.05f, 0 ) ) * Xform.Rotate( x, MathF.PI ), 0.4f );
			var legLower = s.AddBone( $"leg_lower_{side}", legUpper, Xform.Translate( new Vec3( 0, 0.4f, 0 ) ), 0.4f );
			var ankle = s.AddBone( $"ankle_{side}", legLower, Xform.Translate( new Vec3( 0, 0.4f, 0 ) ), 0.1f );
			_ = s.AddBone( $"ball_{side}", ankle, Xform.Translate( new Vec3( 0, 0.1f, 0 ) ), 0.15f );
		}
		return s;
	}

	/// <summary>Shoulder fixture: two bones, both at identity in bind, and one quad whose top
	/// edge is split 50/50 between them. Rotate the second bone 180° about X and LBS folds
	/// the top edge onto the joint while DQS swings it round.</summary>
	static (Skeleton Bind, PolyMesh Mesh, int Upper, int Lower) Shoulder()
	{
		var s = new Skeleton();
		var root = s.AddBone( "root", -1, Xform.Identity, 1f );
		var upper = s.AddBone( "arm_upper_L", root, Xform.Identity, 1f );
		var lower = s.AddBone( "arm_lower_L", upper, Xform.Identity, 1f );
		var mesh = new PolyMesh();
		mesh.AddVertex( new Vec3( 0, 0, 0 ) );
		mesh.AddVertex( new Vec3( 0, 1, 0 ) );
		mesh.AddVertex( new Vec3( 0, 1, 1 ) );
		mesh.AddVertex( new Vec3( 0, 0, 1 ) );
		mesh.AddFace( new[] { 0, 1, 2, 3 } );
		var weights = new SkinWeights( 4 );
		weights[0] = new[] { new BoneWeight( upper, 1f ) };
		weights[1] = new[] { new BoneWeight( upper, 0.5f ), new BoneWeight( lower, 0.5f ) };
		weights[2] = new[] { new BoneWeight( upper, 0.5f ), new BoneWeight( lower, 0.5f ) };
		weights[3] = new[] { new BoneWeight( upper, 1f ) };
		mesh.Skin = weights;
		return (s, mesh, upper, lower);
	}

	static Skeleton Posed( Skeleton bind, int bone, Vec3 axis, float degrees )
	{
		var posed = bind.Clone();
		var b = posed.Bones[bone];
		b.Local = b.Local * Xform.Rotate( axis, degrees * MathF.PI / 180f );
		return posed;
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "deformation-export", name, test );
		void Require( bool condition, string message )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, float tol, string what ) =>
			Require( MathF.Abs( actual - expected ) <= tol, $"{what}: expected {expected}, got {actual}" );
		void NearVec( Vec3 actual, Vec3 expected, float tol, string what )
		{
			Near( actual.x, expected.x, tol, what + ".x" );
			Near( actual.y, expected.y, tol, what + ".y" );
			Near( actual.z, expected.z, tol, what + ".z" );
		}
		bool HasCode( ExportValidationReport report, string code ) =>
			report.Issues.Exists( i => i.Code == code );
		bool HasError( ExportValidationReport report, string code ) =>
			report.Issues.Exists( i => i.Code == code && i.Severity == ExportIssueSeverity.Error );

		Check( "DQS keeps the shoulder's radius where LBS collapses it", () =>
		{
			var (bind, mesh, _, lower) = Shoulder();
			var posed = Posed( bind, lower, new Vec3( 1, 0, 0 ), 180f );
			var bindWorld = PoseSpaceCorrective.World( bind );
			var poseWorld = PoseSpaceCorrective.World( posed );
			var lbs = SkinBinder.Deform( mesh.Positions, mesh.Skin, bindWorld, poseWorld );
			var dqs = DualQuaternionSkinning.Deform( mesh.Positions, mesh.Skin, bindWorld, poseWorld, out var scaled, out var scaledVerts );
			// LBS averages two 180°-apart matrices to zero: the blended vertex lands on the joint.
			NearVec( lbs[1], new Vec3( 0, 0, 0 ), 1e-4f, "LBS vertex 1" );
			NearVec( lbs[2], new Vec3( 0, 0, 0 ), 1e-4f, "LBS vertex 2" );
			// DQS averages two rotations to a 90° rotation: the vertex keeps its radius of 1.
			NearVec( dqs[1], new Vec3( 0, 0, 1 ), 1e-4f, "DQS vertex 1" );
			NearVec( dqs[2], new Vec3( 0, -1, 1 ), 1e-4f, "DQS vertex 2" );
			Near( dqs[1].Length, 1f, 1e-4f, "DQS vertex 1 radius" );
			Require( scaled.Count == 0 && scaledVerts == 0, $"no bone is scaled, but {scaled.Count} bones / {scaledVerts} vertices were reported scaled" );
		} );

		Check( "DQS at rest is the rest pose, and a single bone matches LBS exactly", () =>
		{
			var (bind, mesh, upper, lower) = Shoulder();
			var bindWorld = PoseSpaceCorrective.World( bind );
			var dqsRest = DualQuaternionSkinning.Deform( mesh.Positions, mesh.Skin, bindWorld, bindWorld, out _, out _ );
			for ( var i = 0; i < mesh.Positions.Count; i++ )
				NearVec( dqsRest[i], mesh.Positions[i], 1e-5f, $"rest vertex {i}" );
			// Re-weight every vertex to the upper bone alone and rotate it: one influence
			// means one rigid transform under both methods, so the results must agree.
			var single = new SkinWeights( 4 );
			for ( var i = 0; i < 4; i++ )
				single[i] = new[] { new BoneWeight( upper, 1f ) };
			var posed = Posed( bind, upper, new Vec3( 1, 0, 0 ), 90f );
			var poseWorld = PoseSpaceCorrective.World( posed );
			var lbs = SkinBinder.Deform( mesh.Positions, single, bindWorld, poseWorld );
			var dqs = DualQuaternionSkinning.Deform( mesh.Positions, single, bindWorld, poseWorld, out _, out _ );
			for ( var i = 0; i < 4; i++ )
				NearVec( dqs[i], lbs[i], 1e-5f, $"single-bone vertex {i}" );
			_ = lower;
		} );

		Check( "DQS-minus-LBS bakes to the corrective morph", () =>
		{
			var (bind, mesh, _, lower) = Shoulder();
			var posed = Posed( bind, lower, new Vec3( 1, 0, 0 ), 180f );
			var bindWorld = PoseSpaceCorrective.World( bind );
			var poseWorld = PoseSpaceCorrective.World( posed );
			var deltas = DualQuaternionSkinning.BakeCorrective( mesh.Positions, mesh.Skin, bindWorld, poseWorld );
			// The morph that, added under LBS, reproduces the DQS shape: vertex 1 moves
			// from the collapsed origin back out to (0,0,1).
			NearVec( deltas[1], new Vec3( 0, 0, 1 ), 1e-4f, "corrective delta vertex 1" );
			NearVec( deltas[0], new Vec3( 0, 0, 0 ), 1e-5f, "corrective delta vertex 0 (unblended, no fix needed)" );
		} );

		Check( "twist bones are generated on the limb at even stations", () =>
		{
			var s = Humanoid();
			var lower = s.IndexOf( "arm_lower_L" );
			var added = TwistBones.AddTwists( s, lower, 2 );
			Require( added.Length == 2, $"expected 2 twist bones, got {added.Length}" );
			Require( s.Bones[added[0]].Name == "arm_lower_L_twist0", $"twist0 is '{s.Bones[added[0]].Name}'" );
			Require( s.Bones[added[1]].Name == "arm_lower_L_twist1", $"twist1 is '{s.Bones[added[1]].Name}'" );
			Require( s.Bones[added[0]].Parent == lower && s.Bones[added[1]].Parent == lower, "twist bones must be children of the limb" );
			var stations = TwistBones.Stations( s, lower );
			Near( stations[0].Along, 0f, 1e-4f, "twist0 station" );
			Near( stations[1].Along, 0.5f, 1e-4f, "twist1 station" );
			Require( TwistBones.OfLimb( s, lower ).Length == 2, "OfLimb did not find both twist bones" );
		} );

		Check( "ApplyTwist ramps the roll along the limb", () =>
		{
			var s = Humanoid();
			var lower = s.IndexOf( "arm_lower_L" );
			var twists = TwistBones.AddTwists( s, lower, 2 );
			TwistBones.ApplyTwist( s, lower, MathF.PI / 2 );
			// Twist i takes (i+1)/(count+1) of the total: 90° over 2 bones is 30° then 60°.
			foreach ( var (bone, expected) in new[] { (twists[0], MathF.PI / 6), (twists[1], MathF.PI / 3) } )
			{
				var q = Quat.FromXform( s.Bones[bone].Local );
				var angle = 2f * MathF.Acos( MathF.Min( 1f, MathF.Abs( q.w ) ) );
				Near( angle, expected, 1e-4f, $"twist bone {s.Bones[bone].Name} angle" );
			}
		} );

		Check( "TwistWeights.Spread moves weights onto the twist bones", () =>
		{
			var s = Humanoid();
			var lower = s.IndexOf( "arm_lower_L" );
			TwistBones.AddTwists( s, lower, 2 );
			// Three vertices down the forearm (world x -0.65 / -0.75 / -0.85, y 1.65),
			// all on the lower arm to start.
			var mesh = new PolyMesh();
			mesh.AddVertex( new Vec3( -0.65f, 1.65f, 0 ) );
			mesh.AddVertex( new Vec3( -0.75f, 1.65f, 0 ) );
			mesh.AddVertex( new Vec3( -0.85f, 1.65f, 0 ) );
			mesh.AddFace( new[] { 0, 1, 2 } );
			var weights = new SkinWeights( 3 );
			for ( var i = 0; i < 3; i++ )
				weights[i] = new[] { new BoneWeight( lower, 1f ) };
			mesh.Skin = weights;
			var changed = TwistWeights.Spread( mesh, s );
			Require( changed > 0, "Spread changed no vertices" );
			var twist0 = s.IndexOf( "arm_lower_L_twist0" );
			var twist1 = s.IndexOf( "arm_lower_L_twist1" );
			var sawTwist = false;
			for ( var v = 0; v < 3; v++ )
			{
				var sum = 0f;
				foreach ( var w in mesh.Skin[v] )
				{
					sum += w.Weight;
					if ( w.Bone == twist0 || w.Bone == twist1 )
						sawTwist = true;
				}
				Near( sum, 1f, 1e-4f, $"vertex {v} weight sum after spread" );
			}
			Require( sawTwist, "no influence landed on a twist bone" );
		} );

		Check( "a corrective fires at its joint angle and adds its deltas", () =>
		{
			var bind = Humanoid();
			var upper = bind.IndexOf( "arm_upper_L" );
			var set = new CorrectiveSet();
			set.Drivers.Add( new MorphDriver { Morph = "shoulder_fix", Bone = "arm_upper_L", Axis = DriverAxis.Y } );
			var morph = new MeshMorph { Name = "shoulder_fix", Deltas = new[] { new Vec3( 0, 0.1f, 0 ), new Vec3( 0, 0, 0 ) } };
			set.Morphs.Add( morph );
			Require( set.Problems( bind ).Count == 0, $"corrective set has problems: {string.Join( "; ", set.Problems( bind ) )}" );
			var locals0 = bind.Bones.Select( b => b.Local ).ToList();
			Near( set.Evaluate( bind, locals0 )["shoulder_fix"], 0f, 1e-4f, "corrective weight at rest" );
			var posed45 = Posed( bind, upper, new Vec3( 0, 1, 0 ), 45f );
			Near( set.Evaluate( bind, posed45.Bones.Select( b => b.Local ).ToList() )["shoulder_fix"], 0.5f, 1e-4f, "corrective weight at 45° (Smooth ease)" );
			var posed90 = Posed( bind, upper, new Vec3( 0, 1, 0 ), 90f );
			Near( set.Evaluate( bind, posed90.Bones.Select( b => b.Local ).ToList() )["shoulder_fix"], 1f, 1e-4f, "corrective weight at 90°" );
			var moved = set.Apply( new[] { new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ) },
				new Dictionary<string, float> { ["shoulder_fix"] = 0.5f } );
			NearVec( moved[0], new Vec3( 0, 0.05f, 0 ), 1e-5f, "half-weight delta" );
			NearVec( moved[1], new Vec3( 1, 0, 0 ), 1e-5f, "zero delta stays put" );
		} );

		Check( "skeleton maps to Unity and Unreal with the right names", () =>
		{
			var s = Humanoid();
			_ = s.AddBone( "tail_0", s.IndexOf( "pelvis" ), Xform.Translate( new Vec3( 0, 0.2f, 0 ) ), 0.2f );
			Require( SkeletonMapping.MapName( "arm_upper_L", SkeletonPreset.UnityHumanoid ) == "LeftUpperArm", "Unity arm name" );
			Require( SkeletonMapping.MapName( "leg_upper_L", SkeletonPreset.UnityHumanoid ) == "LeftUpperLeg", "Unity leg name" );
			Require( SkeletonMapping.MapName( "arm_upper_L", SkeletonPreset.UnrealManny ) == "upperarm_l", "Manny arm name" );
			Require( SkeletonMapping.MapName( "root", SkeletonPreset.UnrealManny ) == "root", "Manny keeps the root" );
			var unity = SkeletonMapping.MapSkeleton( s, SkeletonPreset.UnityHumanoid, out var unmapped );
			Require( unity.Bones[unity.IndexOf( "LeftUpperArm" )].Name == "LeftUpperArm", "Unity rename landed" );
			Require( unmapped.Contains( "root" ), "Unity has no root bone — it must be reported, not dropped" );
			Require( unmapped.Contains( "tail_0" ), "a custom bone must be reported, not dropped" );
			Require( unity.IndexOf( "tail_0" ) >= 0, "the custom bone must be kept under its own name" );
			Require( unity.IndexOf( "root" ) >= 0, "the root must be kept under its own name" );
		} );

		Check( "skeleton maps to Mixamo and HumanIK, and round-trips", () =>
		{
			var s = Humanoid();
			Require( SkeletonMapping.MapName( "arm_upper_L", SkeletonPreset.Mixamo ) == "mixamorig:LeftArm", "Mixamo arm name" );
			Require( SkeletonMapping.MapName( "pelvis", SkeletonPreset.Mixamo ) == "mixamorig:Hips", "Mixamo hips name" );
			Require( SkeletonMapping.MapName( "arm_upper_L", SkeletonPreset.HumanIK ) == "LeftArm", "HumanIK arm name" );
			Require( SkeletonMapping.MapName( "spine_2", SkeletonPreset.HumanIK ) is null, "HumanIK has two spine bones — the third is unmapped, not forced" );
			foreach ( var preset in new[] { SkeletonPreset.UnityHumanoid, SkeletonPreset.UnrealManny, SkeletonPreset.Mixamo, SkeletonPreset.HumanIK } )
			{
				var exact = SkeletonMapping.RoundTrip( s, preset, out var normalised, out var unmapped );
				Require( exact, $"{preset} round-trip lost a bone" );
				Require( normalised.Count == 0, $"{preset} round-trip normalised {normalised.Count} bones on a canonical rig" );
				Require( unmapped.Contains( "root" ) == (preset != SkeletonPreset.UnrealManny),
					$"{preset} root handling is wrong (unmapped: {string.Join( ",", unmapped )})" );
			}
			// An alias round-trips as a normalisation, not a loss: thigh_L comes back as leg_upper_L.
			var alias = new Skeleton();
			_ = alias.AddBone( "root", -1, Xform.Identity, 1f );
			_ = alias.AddBone( "thigh_L", 0, Xform.Translate( new Vec3( -0.1f, -0.05f, 0 ) ), 0.4f );
			var aliasExact = SkeletonMapping.RoundTrip( alias, SkeletonPreset.UnityHumanoid, out var aliasNormalised, out _ );
			Require( aliasExact, "alias round-trip must still be exact" );
			Require( aliasNormalised.Contains( "thigh_L" ), "thigh_L must be reported as normalised to leg_upper_L" );
		} );

		Check( "validation passes a good T-pose and only warns about missing twist bones", () =>
		{
			var report = ExportValidation.Validate( Humanoid() );
			Require( report.Pass, $"good T-pose failed: {string.Join( "; ", report.Issues.ConvertAll( i => i.Code + ": " + i.Message ) )}" );
			Require( HasCode( report, "no-twist-bones" ), "missing twist bones must at least warn" );
			Require( !report.Issues.Exists( i => i.Severity == ExportIssueSeverity.Error ), "a good rig must have no errors" );
		} );

		Check( "validation fails a hanging-arms rest pose and an off-origin root", () =>
		{
			var hanging = ExportValidation.Validate( Humanoid( armsDown: true ) );
			Require( !hanging.Pass, "arms hanging at the sides must fail the rest-pose check" );
			Require( HasError( hanging, "rest-pose-arms" ), "expected the rest-pose-arms error" );
			var moved = Humanoid();
			moved.Bones[moved.IndexOf( "root" )].Local = Xform.Translate( new Vec3( 0, 0.5f, 0 ) );
			var offOrigin = ExportValidation.Validate( moved );
			Require( !offOrigin.Pass, "a root off the origin must fail" );
			Require( HasError( offOrigin, "root-off-origin" ), "expected the root-off-origin error" );
		} );

		Check( "validation fails unnormalised, over-count and empty weights", () =>
		{
			var s = Humanoid();
			var upper = s.IndexOf( "arm_upper_L" );
			var lower = s.IndexOf( "arm_lower_L" );
			var hand = s.IndexOf( "hand_L" );
			var clav = s.IndexOf( "clavicle_L" );
			var spine = s.IndexOf( "spine_2" );
			var weights = new SkinWeights( 3 );
			weights[0] = new[] { new BoneWeight( upper, 0.75f ), new BoneWeight( lower, 0.75f ) };
			weights[1] = new[]
			{
				new BoneWeight( upper, 0.2f ), new BoneWeight( lower, 0.2f ), new BoneWeight( hand, 0.2f ),
				new BoneWeight( clav, 0.2f ), new BoneWeight( spine, 0.2f ),
			};
			weights[2] = System.Array.Empty<BoneWeight>();
			var report = ExportValidation.Validate( s, weights, 3 );
			Require( !report.Pass, "bad weights must fail" );
			Require( HasError( report, "weight-sum" ), "expected the weight-sum error (1.5, not 1)" );
			Require( HasError( report, "too-many-influences" ), "expected the too-many-influences error (5, cap 4)" );
			Require( HasError( report, "zero-influence" ), "expected the zero-influence error" );
		} );

		Check( "stress: a 180° shoulder roll pinches under LBS and not under DQS", () =>
		{
			var (bind, mesh, _, lower) = Shoulder();
			var roll = new (string Name, List<(string Bone, Vec3 Axis, float Degrees)> Script)[]
			{
				("shoulder-roll", new List<(string, Vec3, float)> { ("arm_lower_L", new Vec3( 1, 0, 0 ), 180f ) }),
			};
			_ = lower;
			var lbs = DeformationStress.Run( mesh, bind, SkinMethod.Lbs, roll );
			Require( lbs.TotalPinched > 0, "LBS must pinch on the shoulder roll" );
			var worst = lbs.Worst;
			Require( worst is not null, "LBS report needs a worst pinch" );
			Near( worst.Severity, 1f, 0.01f, "LBS worst pinch severity (the edge collapses to zero)" );
			Require( lbs.Pinches.Count > 0 && lbs.Pinches[0].Severity == worst.Severity, "pinch list must be worst-first" );
			var dqs = DeformationStress.Run( mesh, bind, SkinMethod.DualQuaternion, roll );
			Require( dqs.TotalPinched == 0, $"DQS must not pinch on the shoulder roll, but found {dqs.TotalPinched}" );
			Require( dqs.Worst is null, "DQS report must have no worst pinch" );
		} );

		Check( "stress: the three built-in poses run and a hinge fold pinches under both methods", () =>
		{
			var s = Humanoid();
			// A quad straddling the left knee: top edge on the thigh, bottom edge on the calf.
			var mesh = new PolyMesh();
			mesh.AddVertex( new Vec3( -0.15f, 0.6f, 0 ) );
			mesh.AddVertex( new Vec3( -0.05f, 0.6f, 0 ) );
			mesh.AddVertex( new Vec3( -0.05f, 0.4f, 0 ) );
			mesh.AddVertex( new Vec3( -0.15f, 0.4f, 0 ) );
			mesh.AddFace( new[] { 0, 1, 2, 3 } );
			var weights = new SkinWeights( 4 );
			var thigh = s.IndexOf( "leg_upper_L" );
			var calf = s.IndexOf( "leg_lower_L" );
			weights[0] = new[] { new BoneWeight( thigh, 1f ) };
			weights[1] = new[] { new BoneWeight( thigh, 1f ) };
			weights[2] = new[] { new BoneWeight( calf, 1f ) };
			weights[3] = new[] { new BoneWeight( calf, 1f ) };
			mesh.Skin = weights;
			var lbs = DeformationStress.Run( mesh, s, SkinMethod.Lbs );
			Require( lbs.Poses.Count == 3, $"expected 3 poses, got {lbs.Poses.Count}" );
			Require( lbs.Poses[0] == "deep-squat" && lbs.Poses[1] == "overhead-reach" && lbs.Poses[2] == "fist", "pose names/order wrong" );
			Require( lbs.TotalPinched > 0, "the deep squat must fold the knee quad" );
			Require( lbs.Worst is not null && lbs.Worst.Pose == "deep-squat", "the worst pinch must be in the squat" );
			Require( lbs.Pinches.Count <= 100, "the highlight list is capped at 100" );
			// No blended vertices here, so DQS deforms identically to LBS: a hinge fold is
			// real geometry, not a blend artefact, and DQS does not pretend otherwise.
			var dqs = DeformationStress.Run( mesh, s, SkinMethod.DualQuaternion );
			Require( dqs.TotalPinched == lbs.TotalPinched, $"DQS ({dqs.TotalPinched}) and LBS ({lbs.TotalPinched}) must agree on an unblended mesh" );
		} );
	}
}
