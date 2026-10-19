#nullable enable
using System;
using System.IO;
using System.Linq;
using Effigy;
using Marionette.Desktop;

internal static class FbxAnimationFeasibilityTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "fbx_anim_feasibility", name, test );

		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		void AssertNear( float actual, float expected, float tolerance = 1e-4f, string message = "" )
		{
			if ( MathF.Abs( actual - expected ) > tolerance )
				throw new Exception( $"{message}: expected {expected:F6}, got {actual:F6} (diff {MathF.Abs( actual - expected ):E4} > {tolerance:E4})" );
		}

		void AssertVecNear( Vec3 actual, Vec3 expected, float tolerance = 1e-4f, string message = "" )
		{
			float diff = (actual - expected).Length;
			if ( diff > tolerance )
				throw new Exception( $"{message}: expected ({expected.x:F4}, {expected.y:F4}, {expected.z:F4}), got ({actual.x:F4}, {actual.y:F4}, {actual.z:F4}) (diff {diff:E4} > {tolerance:E4})" );
		}

		void AssertQuatNear( Quat actual, Quat expected, float tolerance = 1e-4f, string message = "" )
		{
			// Quaternions q and -q represent the identical rotation.
			float diffSame = MathF.Abs( actual.x - expected.x ) + MathF.Abs( actual.y - expected.y ) + MathF.Abs( actual.z - expected.z ) + MathF.Abs( actual.w - expected.w );
			float diffOpp = MathF.Abs( actual.x + expected.x ) + MathF.Abs( actual.y + expected.y ) + MathF.Abs( actual.z + expected.z ) + MathF.Abs( actual.w + expected.w );
			float minDiff = MathF.Min( diffSame, diffOpp );
			if ( minDiff > tolerance )
				throw new Exception( $"{message}: expected {expected}, got {actual} (diff {minDiff:E4} > {tolerance:E4})" );
		}

		// 1. Feasibility audit accurately reports supported subset vs unsupported features
		Check( "feasibility audit accurately reports supported subset vs unsupported features", () =>
		{
			var refData = FbxAnimationReferenceFixture.CreateReferenceAnimationData();
			var refAudit = FbxAnimationAuditor.Audit( refData );

			Assert( refAudit.IsFeasible, "Reference fixture must be feasible under first subset" );
			Assert( !refAudit.HasErrors, "Reference fixture must have zero errors" );
			Assert( refAudit.SupportedBones.Contains( "root" ), "root bone must be supported" );
			Assert( refAudit.SupportedBones.Contains( "child" ), "child bone must be supported" );
			Assert( refAudit.UnsupportedBones.Count == 0, "Reference fixture has no unsupported bones" );

			// Test unsupported features: unbaked pivots & pre-rotation
			var badData = FbxAnimationReferenceFixture.CreateReferenceAnimationData();
			badData.Takes[0].Nodes[0].RotationPivot = new Vec3( 2f, 0, 0 );
			badData.Takes[0].Nodes[1].PreRotation = new Vec3( 45f, 0, 0 );
			var badAudit = FbxAnimationAuditor.Audit( badData );

			Assert( !badAudit.IsFeasible, "Data with unbaked pivots and pre-rotation must not be feasible" );
			Assert( badAudit.HasErrors, "Must report hard errors for unsupported transform features" );
			Assert( badAudit.Issues.Any( i => i.Feature == FbxAnimationContract.FeatureUnbakedPivot ), "Must report FeatureUnbakedPivot" );
			Assert( badAudit.Issues.Any( i => i.Feature == FbxAnimationContract.FeaturePrePostRotation ), "Must report FeaturePrePostRotation" );
			Assert( badAudit.UnsupportedBones.Contains( "root" ), "root must be marked unsupported" );
			Assert( badAudit.UnsupportedBones.Contains( "child" ), "child must be marked unsupported" );
		} );

		// 2. Golden reference fixture verifies parent-child rotation composition
		Check( "golden reference fixture verifies parent-child rotation composition", () =>
		{
			// Keyframe 0 (t = 0.0s):
			// root is identity at (0,0,0); child local is (0,10,0); child world is (0,10,0)
			var rootWorld0 = Xform.Translate( FbxAnimationReferenceFixture.GoldenRootPos0 );
			var childLocal0 = Xform.Translate( FbxAnimationReferenceFixture.GoldenChildLocalPos0 );
			var childWorld0 = FbxAnimationMath.ComposeWorld( rootWorld0, childLocal0 );
			AssertVecNear( childWorld0.Origin, FbxAnimationReferenceFixture.GoldenChildWorldPos0, 1e-4f, "Frame 0 child world position" );
			AssertQuatNear( Quat.FromXform( childWorld0 ), FbxAnimationReferenceFixture.GoldenChildWorldRot0, 1e-4f, "Frame 0 child world rotation" );

			// Keyframe 15 (t = 0.5s):
			// Root has nontrivial rotation (90° about Z) and scale (2,2,2) at origin (5,10,0).
			// Child has local rotation (90° about X) at local offset (0,4,0).
			var rootRot15 = FbxAnimationReferenceFixture.GoldenRootRot15;
			var rootWorld15 = rootRot15.ToXform( FbxAnimationReferenceFixture.GoldenRootPos15, FbxAnimationReferenceFixture.GoldenRootScale15 );

			var childRot15 = FbxAnimationReferenceFixture.GoldenChildLocalRot15;
			var childLocal15 = childRot15.ToXform( FbxAnimationReferenceFixture.GoldenChildLocalPos15, FbxAnimationReferenceFixture.GoldenChildLocalScale15 );

			// Compose world transform via parentWorld * childLocal
			var childWorld15 = FbxAnimationMath.ComposeWorld( rootWorld15, childLocal15 );

			// Verify against independent analytical oracle:
			// Origin must be (-3, 10, 0), rotation must be (0.5, 0.5, 0.5, 0.5), scale must be (2, 2, 2)
			AssertVecNear( childWorld15.Origin, FbxAnimationReferenceFixture.GoldenChildWorldPos15, 1e-4f, "Frame 15 child golden world position" );
			AssertQuatNear( Quat.FromXform( childWorld15 ), FbxAnimationReferenceFixture.GoldenChildWorldRot15, 1e-4f, "Frame 15 child golden world rotation" );
			AssertVecNear( Quat.ScaleOf( childWorld15 ), FbxAnimationReferenceFixture.GoldenChildWorldScale15, 1e-4f, "Frame 15 child golden world scale" );

			// Verify inverse derivation: childLocal = Inverse(parentWorld) * childWorld
			var derivedChildLocal15 = FbxAnimationMath.DeriveLocal( rootWorld15, childWorld15 );
			AssertVecNear( derivedChildLocal15.Origin, FbxAnimationReferenceFixture.GoldenChildLocalPos15, 1e-4f, "Derived child local position at frame 15" );
			AssertQuatNear( Quat.FromXform( derivedChildLocal15 ), FbxAnimationReferenceFixture.GoldenChildLocalRot15, 1e-4f, "Derived child local rotation at frame 15" );

			// Keyframe 30 (t = 1.0s):
			// Root rotated 180° about Z at (10,20,0); child local offset (0,5,0)
			// Child world must be (10, 15, 0)
			var rootRot30 = FbxAnimationReferenceFixture.GoldenRootRot30;
			var rootWorld30 = rootRot30.ToXform( FbxAnimationReferenceFixture.GoldenRootPos30, FbxAnimationReferenceFixture.GoldenRootScale30 );
			var childLocal30 = Xform.Translate( FbxAnimationReferenceFixture.GoldenChildLocalPos30 );
			var childWorld30 = FbxAnimationMath.ComposeWorld( rootWorld30, childLocal30 );
			AssertVecNear( childWorld30.Origin, FbxAnimationReferenceFixture.GoldenChildWorldPos30, 1e-4f, "Frame 30 child golden world position" );
			AssertQuatNear( Quat.FromXform( childWorld30 ), FbxAnimationReferenceFixture.GoldenChildWorldRot30, 1e-4f, "Frame 30 child golden world rotation" );
		} );

		// 3. Time conversion between FBX time and kernel frames
		Check( "time conversion between FBX time and kernel frames", () =>
		{
			long oneSecTicks = FbxAnimationTime.FbxTicksPerSecond;
			Assert( oneSecTicks == 46_186_158_000L, "FbxTicksPerSecond must match KTime standard" );

			AssertNear( (float)FbxAnimationTime.TicksToSeconds( 0 ), 0f, 1e-6f );
			AssertNear( (float)FbxAnimationTime.TicksToSeconds( oneSecTicks ), 1.0f, 1e-6f );
			Assert( FbxAnimationTime.SecondsToTicks( 1.0 ) == oneSecTicks, "1.0s must round-trip to oneSecTicks" );

			// Frame conversions at 30 fps
			Assert( FbxAnimationTime.TicksToKernelFrame( 0, 30 ) == 0, "0 ticks -> frame 0" );
			Assert( FbxAnimationTime.TicksToKernelFrame( oneSecTicks, 30 ) == 30, "1s ticks -> frame 30 at 30fps" );
			Assert( FbxAnimationTime.TicksToKernelFrame( oneSecTicks / 2, 30 ) == 15, "0.5s ticks -> frame 15 at 30fps" );

			// Frame conversions at 60 fps
			Assert( FbxAnimationTime.TicksToKernelFrame( oneSecTicks, 60 ) == 60, "1s ticks -> frame 60 at 60fps" );

			// Inverse frame -> ticks
			Assert( FbxAnimationTime.KernelFrameToTicks( 15, 30 ) == oneSecTicks / 2, "frame 15 at 30fps -> 0.5s ticks" );
			Assert( FbxAnimationTime.KernelFrameToTicks( 30, 30 ) == oneSecTicks, "frame 30 at 30fps -> 1s ticks" );

			// Frame -> seconds
			AssertNear( FbxAnimationTime.KernelFrameToSeconds( 15, 30 ), 0.5f, 1e-6f );
			AssertNear( FbxAnimationTime.KernelFrameToSeconds( 30, 30 ), 1.0f, 1e-6f );
			Assert( FbxAnimationTime.SecondsToKernelFrame( 0.5f, 30 ) == 15, "0.5s -> frame 15" );

			// Multi-frame round-trip check
			for ( int f = 0; f <= 120; f++ )
			{
				long ticks = FbxAnimationTime.KernelFrameToTicks( f, 60 );
				int frameBack = FbxAnimationTime.TicksToKernelFrame( ticks, 60 );
				Assert( frameBack == f, $"Frame {f} round trip failed; got {frameBack}" );
			}
		} );

		// 4. Euler rotation order conversion to Quat
		Check( "Euler rotation order conversion to Quat", () =>
		{
			// Single axis rotations
			var qx90 = FbxAnimationMath.EulerToQuat( 90f, 0, 0, FbxRotationOrder.EulerXYZ );
			var qy90 = FbxAnimationMath.EulerToQuat( 0, 90f, 0, FbxRotationOrder.EulerXYZ );
			var qz90 = FbxAnimationMath.EulerToQuat( 0, 0, 90f, FbxRotationOrder.EulerXYZ );

			// 90° about X rotates (0,1,0) to (0,0,1)
			AssertVecNear( qx90.Rotate( new Vec3( 0, 1, 0 ) ), new Vec3( 0, 0, 1 ), 1e-4f, "Rotate (0,1,0) by 90° X" );
			// 90° about Y rotates (0,0,1) to (1,0,0)
			AssertVecNear( qy90.Rotate( new Vec3( 0, 0, 1 ) ), new Vec3( 1, 0, 0 ), 1e-4f, "Rotate (0,0,1) by 90° Y" );
			// 90° about Z rotates (1,0,0) to (0,1,0)
			AssertVecNear( qz90.Rotate( new Vec3( 1, 0, 0 ) ), new Vec3( 0, 1, 0 ), 1e-4f, "Rotate (1,0,0) by 90° Z" );

			// Compound rotation: 90° X then 90° Y in EulerXYZ
			// Vector (0,1,0) rotated by 90° X becomes (0,0,1); then rotated by 90° Y becomes (1,0,0)
			var qCompound = FbxAnimationMath.EulerToQuat( 90f, 90f, 0, FbxRotationOrder.EulerXYZ );
			AssertVecNear( qCompound.Rotate( new Vec3( 0, 1, 0 ) ), new Vec3( 1, 0, 0 ), 1e-4f, "Compound EulerXYZ rotation" );

			// Verify that different rotation orders produce distinct rotations
			var qOrderXZY = FbxAnimationMath.EulerToQuat( 90f, 90f, 45f, FbxRotationOrder.EulerXZY );
			var qOrderXYZ = FbxAnimationMath.EulerToQuat( 90f, 90f, 45f, FbxRotationOrder.EulerXYZ );
			Assert( MathF.Abs( qOrderXZY.x - qOrderXYZ.x ) > 1e-3f || MathF.Abs( qOrderXZY.y - qOrderXYZ.y ) > 1e-3f,
				"Distinct Euler rotation orders must produce distinct quaternions" );
		} );

		// 5. Unsupported layered animation produces structured error
		Check( "unsupported layered animation produces structured error", () =>
		{
			var multiLayerData = FbxAnimationReferenceFixture.CreateReferenceAnimationData();
			multiLayerData.Takes[0].LayerCount = 2; // 2 layers: unsupported!

			var audit = FbxAnimationAuditor.Audit( multiLayerData );
			Assert( !audit.IsFeasible, "Multi-layer take must be marked infeasible" );
			Assert( audit.HasErrors, "Multi-layer take must produce structured error" );

			var layerIssue = audit.Issues.FirstOrDefault( i => i.Feature == FbxAnimationContract.FeatureLayeredAnimation );
			Assert( layerIssue != null, "Must report FeatureLayeredAnimation issue" );
			Assert( layerIssue!.Severity == FeasibilityIssueSeverity.Error, "LayeredAnimation issue must be an Error" );
			Assert( layerIssue!.Message.Contains( "unsupported" ), "Error message must state unsupported" );

			// Also test raw FbxNode tree with multiple layers
			var nodeTree = FbxAnimationReferenceFixture.CreateReferenceFbxNodeTree( layerCount: 3 );
			var nodeAudit = FbxAnimationAuditor.Audit( nodeTree );
			Assert( !nodeAudit.IsFeasible, "FbxNode tree with 3 layers must be infeasible" );
			Assert( nodeAudit.Issues.Any( i => i.Feature == FbxAnimationContract.FeatureLayeredAnimation && i.Severity == FeasibilityIssueSeverity.Error ),
				"FbxNode tree must diagnose layered animation" );
		} );

		// 6. Unit scale conversion (e.g. cm to inch) preserves physical dimensions
		Check( "unit scale conversion (e.g. cm to inch) preserves physical dimensions", () =>
		{
			// In FBX with UnitScale = 1.0 (cm), a distance of 100 cm is 100 / 2.54 inches
			var cmData = FbxAnimationReferenceFixture.CreateReferenceAnimationData( unitScale: 1.0 );
			var cmClip = FbxAnimationConverter.ConvertToRigAnimFile( cmData );

			float expectedInchScale = (float)(1.0 / FbxExportAdapter.InchesToCentimetres);
			var rootTrackCm = cmClip.Tracks.First( t => t.Name == "root" );

			// Root position at frame 15 in reference fixture was (5, 10, 0)
			Vec3 rootPos15Cm = rootTrackCm.Keys.First( k => k.Frame == 15 ).Local.Position;
			AssertNear( rootPos15Cm.x, 5f * expectedInchScale, 1e-4f, "Converted cm X position in inches" );
			AssertNear( rootPos15Cm.y, 10f * expectedInchScale, 1e-4f, "Converted cm Y position in inches" );

			// In FBX with UnitScale = 2.54 (inches), scale factor is 1.0
			var inchData = FbxAnimationReferenceFixture.CreateReferenceAnimationData( unitScale: 2.54 );
			var inchClip = FbxAnimationConverter.ConvertToRigAnimFile( inchData );
			var rootTrackInch = inchClip.Tracks.First( t => t.Name == "root" );
			Vec3 rootPos15Inch = rootTrackInch.Keys.First( k => k.Frame == 15 ).Local.Position;
			AssertNear( rootPos15Inch.x, 5f, 1e-4f, "Inch X position preserved exactly" );
			AssertNear( rootPos15Inch.y, 10f, 1e-4f, "Inch Y position preserved exactly" );
		} );

		// 7. Missing target bone diagnosed cleanly
		Check( "missing target bone diagnosed cleanly", () =>
		{
			var refData = FbxAnimationReferenceFixture.CreateReferenceAnimationData();

			// Skeleton only has "root", missing "child"
			var partialSkeleton = new Skeleton();
			partialSkeleton.AddBone( "root", -1, Xform.Identity, 10f );

			var audit = FbxAnimationAuditor.Audit( refData, partialSkeleton );
			Assert( audit.MissingTargetBones.Contains( "child" ), "child must be recorded in MissingTargetBones" );
			Assert( audit.Issues.Any( i => i.Feature == FbxAnimationContract.FeatureMissingTargetBone && i.Severity == FeasibilityIssueSeverity.Warning ),
				"Must diagnose MissingTargetBone warning" );

			// Conversion succeeds gracefully, mapping available bones without crashing
			var clip = FbxAnimationConverter.ConvertToRigAnimFile( refData, partialSkeleton );
			Assert( clip.Tracks.Any( t => t.Name == "root" ), "Must contain track for root" );
			Assert( clip.Tracks.All( t => t.Name != "child" ), "Must cleanly omit missing target bone" );
		} );

		// 8. Conversion produces valid RigAnimFile data
		Check( "conversion produces valid RigAnimFile data", () =>
		{
			var refData = FbxAnimationReferenceFixture.CreateReferenceAnimationData();
			var skeleton = FbxAnimationReferenceFixture.CreateReferenceSkeleton();

			var clip = FbxAnimationConverter.ConvertToRigAnimFile( refData, skeleton );
			Assert( clip.FrameRate == 30, "Clip FrameRate must be 30" );
			Assert( clip.FrameCount == 31, "Clip FrameCount must be 31" );
			Assert( clip.Tracks.Count == 2, "Clip must have 2 bone tracks" );

			// Verify RigAnimWriter serializes valid JSON
			string json = RigAnimWriter.Write( clip );
			Assert( !string.IsNullOrWhiteSpace( json ), "RigAnimWriter produced non-empty JSON" );
			Assert( json.Contains( "\"AnimationSpeed\": 30" ), "JSON contains AnimationSpeed" );
			Assert( json.Contains( "\"FrameCount\": 31" ), "JSON contains FrameCount" );
			Assert( json.Contains( "\"root\"" ) && json.Contains( "\"child\"" ), "JSON contains bone names" );

			// Verify kernel evaluation of local poses matches golden reference
			var local0 = clip.SampleLocal( skeleton, 0 );
			AssertVecNear( local0[0].Origin, FbxAnimationReferenceFixture.GoldenRootPos0, 1e-4f, "Evaluated local root at frame 0" );
			AssertVecNear( local0[1].Origin, FbxAnimationReferenceFixture.GoldenChildLocalPos0, 1e-4f, "Evaluated local child at frame 0" );

			var local15 = clip.SampleLocal( skeleton, 15 );
			AssertVecNear( local15[0].Origin, FbxAnimationReferenceFixture.GoldenRootPos15, 1e-4f, "Evaluated local root at frame 15" );
			AssertVecNear( local15[1].Origin, FbxAnimationReferenceFixture.GoldenChildLocalPos15, 1e-4f, "Evaluated local child at frame 15" );

			// Verify model-space evaluation matches independent golden world poses
			var world15 = RigAnimFile.ModelSpace( skeleton, local15 );
			AssertVecNear( world15[1].Origin, FbxAnimationReferenceFixture.GoldenChildWorldPos15, 1e-3f, "Evaluated world child at frame 15 matches golden oracle" );

			// Fractional frame evaluation
			var localMid = clip.SampleLocal( skeleton, 7.5f );
			Assert( localMid.Length == 2, "Fractional frame evaluation returned 2 bone transforms" );
			Assert( !float.IsNaN( localMid[0].Origin.x ) && !float.IsNaN( localMid[1].Origin.x ), "Fractional pose is free of NaNs" );
		} );

		// 9. Raw FBX node tree audit detects unbaked pivots and custom rotation orders
		Check( "raw FBX node tree audit detects unbaked pivots and custom rotation orders", () =>
		{
			var nodeTree = FbxAnimationReferenceFixture.CreateReferenceFbxNodeTree(
				layerCount: 1,
				rootRotationOrder: FbxRotationOrder.EulerYZX,
				rootRotationPivot: new Vec3( 10, 0, 0 ),
				rootPreRotation: new Vec3( 0, 45, 0 )
			);

			var audit = FbxAnimationAuditor.Audit( nodeTree );
			Assert( !audit.IsFeasible, "Node tree with custom order and pivots must be infeasible" );
			Assert( audit.Issues.Any( i => i.Feature == FbxAnimationContract.FeatureCustomRotationOrder ), "Must diagnose FeatureCustomRotationOrder" );
			Assert( audit.Issues.Any( i => i.Feature == FbxAnimationContract.FeatureUnbakedPivot ), "Must diagnose FeatureUnbakedPivot" );
			Assert( audit.Issues.Any( i => i.Feature == FbxAnimationContract.FeaturePrePostRotation ), "Must diagnose FeaturePrePostRotation" );
		} );
	}
}
