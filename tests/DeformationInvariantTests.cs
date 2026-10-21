#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Effigy;
using Marionette.Desktop;

internal static class DeformationInvariantTests
{
	/// <summary>
	/// Documented tolerance for exact numerical comparisons of position, basis vectors,
	/// and quaternions against Effigy kernel reference values (1e-4).
	/// </summary>
	public const float NumericalTolerance = 1e-4f;

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "deformation-invariant", name, test );

		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		// -----------------------------------------------------------------------------------------
		// Helper functions
		// -----------------------------------------------------------------------------------------

		bool VecEqual( Vec3 a, Vec3 b ) => a.x == b.x && a.y == b.y && a.z == b.z;

		/// <summary>
		/// Computes the geodesic-aware distance between two unit quaternions under antipodal sign symmetry:
		/// min(||q1 - q2||, ||q1 + q2||).
		/// </summary>
		float QuatDistance( Quat a, Quat b )
		{
			float d1 = MathF.Sqrt( (a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z) + (a.w - b.w) * (a.w - b.w) );
			float d2 = MathF.Sqrt( (a.x + b.x) * (a.x + b.x) + (a.y + b.y) * (a.y + b.y) + (a.z + b.z) * (a.z + b.z) + (a.w + b.w) * (a.w + b.w) );
			return MathF.Min( d1, d2 );
		}

		// -----------------------------------------------------------------------------------------
		// 1. Synthetic multi-bone rigs: hierarchical rotated/nonuniformly scaled parents + unkeyed bones
		// -----------------------------------------------------------------------------------------
		Check( "hierarchical rig with rotated nonuniformly scaled parents and unkeyed bones matches kernel at fractional frames", () =>
		{
			// Construct hierarchical rig:
			// Root (bone 0, parent -1): translated (10, 20, 30), rotated 35 deg, non-uniform scale (1.5, 0.8, 2.0)
			// Child1 (bone 1, parent 0): translated (2, 5, -1), rotated 45 deg around Z, non-uniform scale (0.5, 1.2, 1.0)
			// GrandChild (bone 2, parent 1): translated (0, 3, 0), unkeyed in clip!
			// Child2 (bone 3, parent 0): translated (-4, 1, 2), rotated 60 deg around Y
			var rig = new Skeleton();
			var rootRot = Quat.FromAxisAngle( new Vec3( 1, 1, 0 ).Normal, 35f * MathF.PI / 180f );
			rig.AddBone( "root", -1, rootRot.ToXform( new Vec3( 10, 20, 30 ), new Vec3( 1.5f, 0.8f, 2.0f ) ) );

			var child1Rot = Quat.FromAxisAngle( new Vec3( 0, 0, 1 ), 45f * MathF.PI / 180f );
			rig.AddBone( "child1", 0, child1Rot.ToXform( new Vec3( 2, 5, -1 ), new Vec3( 0.5f, 1.2f, 1.0f ) ) );

			rig.AddBone( "grandchild", 1, Xform.Translate( new Vec3( 0, 3, 0 ) ) ); // unkeyed

			var child2Rot = Quat.FromAxisAngle( new Vec3( 0, 1, 0 ), 60f * MathF.PI / 180f );
			rig.AddBone( "child2", 0, child2Rot.ToXform( new Vec3( -4, 1, 2 ) ) );

			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };

			var tRoot = new RigTrack { Name = "root", Kind = RigTrackKind.Bone };
			tRoot.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 10, 20, 30 ), rootRot, new Vec3( 1.5f, 0.8f, 2.0f ) ) } );
			tRoot.Keys.Add( new RigKey { Frame = 15, Ease = RigEase.Smooth, Local = new RigPose( new Vec3( 15, 22, 28 ), Quat.FromAngles( 10, 20, 30 ), new Vec3( 1.2f, 1.0f, 1.8f ) ) } );
			tRoot.Keys.Add( new RigKey { Frame = 30, Ease = RigEase.EaseIn, Local = new RigPose( new Vec3( 20, 25, 25 ), Quat.FromAngles( -20, 40, 10 ), new Vec3( 1.0f, 1.2f, 1.5f ) ) } );
			clip.Tracks.Add( tRoot );

			var tChild1 = new RigTrack { Name = "child1", Kind = RigTrackKind.Bone };
			tChild1.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.EaseOut, Local = new RigPose( new Vec3( 2, 5, -1 ), child1Rot, new Vec3( 0.5f, 1.2f, 1.0f ) ) } );
			tChild1.Keys.Add( new RigKey { Frame = 30, Ease = RigEase.Linear, Local = new RigPose( new Vec3( -3, 8, 4 ), Quat.FromAngles( 30, -15, 60 ), new Vec3( 0.8f, 0.9f, 1.1f ) ) } );
			clip.Tracks.Add( tChild1 );

			var tChild2 = new RigTrack { Name = "child2", Kind = RigTrackKind.Bone };
			tChild2.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( -4, 1, 2 ), child2Rot, Vec3.One ) } );
			tChild2.Keys.Add( new RigKey { Frame = 30, Ease = RigEase.Linear, Local = new RigPose( new Vec3( -1, 4, 6 ), Quat.Identity, new Vec3( 1.1f, 1.1f, 1.1f ) ) } );
			clip.Tracks.Add( tChild2 );

			var player = new MarionettePlayer( rig );
			player.LoadClip( clip );
			player.Playing = false;

			float[] testFrames = new[] { 0f, 0.25f, 3.5f, 7.333f, 15.0f, 22.75f, 29.8f, 30.0f };

			foreach ( var f in testFrames )
			{
				player.Time = f / 30f;
				player.Step( 0 );

				var kernelLocal = clip.SampleLocal( rig, f );
				var kernelWorld = RigAnimFile.ModelSpace( rig, kernelLocal );

				for ( int b = 0; b < rig.Count; b++ )
				{
					var pWorld = player.WorldPose[b];
					var kWorld = kernelWorld[b];

					float posErr = (pWorld.Origin - kWorld.Origin).Length;
					Require( posErr < NumericalTolerance,
						$"Frame {f} bone {b} ({rig.Bones[b].Name}) world position error {posErr:E4} exceeded tolerance {NumericalTolerance}" );

					float xErr = (pWorld.X - kWorld.X).Length;
					float yErr = (pWorld.Y - kWorld.Y).Length;
					float zErr = (pWorld.Z - kWorld.Z).Length;
					Require( xErr < NumericalTolerance && yErr < NumericalTolerance && zErr < NumericalTolerance,
						$"Frame {f} bone {b} ({rig.Bones[b].Name}) basis error (x={xErr:E4}, y={yErr:E4}, z={zErr:E4}) exceeded tolerance {NumericalTolerance}" );

					// Grandchild is unkeyed: verify its local pose is exactly its bind pose
					if ( b == 2 )
					{
						var bindLocal = rig.Bones[2].Local;
						float unkeyedErr = (kernelLocal[2].Origin - bindLocal.Origin).Length;
						Require( unkeyedErr < NumericalTolerance, "Unkeyed grandchild local position mutated from bind pose" );
					}
				}
			}
		} );

		// -----------------------------------------------------------------------------------------
		// 2. Exact numeric comparison: Antipodal quaternion sign equivalence
		// -----------------------------------------------------------------------------------------
		Check( "antipodal quaternion sign equivalence min(|q1-q2|, |q1+q2|) < eps", () =>
		{
			var q1 = Quat.FromAngles( 45, 60, -30 );
			var q2 = new Quat( -q1.x, -q1.y, -q1.z, -q1.w ); // exact antipodal opposite

			// Naive component difference ||q1 - q2|| without sign equivalence yields ~2.0
			float naiveDiff = MathF.Sqrt( (q1.x - q2.x) * (q1.x - q2.x) + (q1.y - q2.y) * (q1.y - q2.y) + (q1.z - q2.z) * (q1.z - q2.z) + (q1.w - q2.w) * (q1.w - q2.w) );
			Require( naiveDiff > 1.9f, $"Naive Euclidean difference expected ~2.0, got {naiveDiff}" );

			// Antipodal metric correctly resolves equivalence
			float antipodalDist = QuatDistance( q1, q2 );
			Require( antipodalDist < 1e-6f, $"Antipodal distance must be 0, got {antipodalDist}" );

			// Both antipodal representations produce identical transformed basis vectors
			var xf1 = q1.ToXform( new Vec3( 1, 2, 3 ) );
			var xf2 = q2.ToXform( new Vec3( 1, 2, 3 ) );
			Require( (xf1.X - xf2.X).Length < NumericalTolerance, "Basis X differed between antipodal quaternions" );
			Require( (xf1.Y - xf2.Y).Length < NumericalTolerance, "Basis Y differed between antipodal quaternions" );
			Require( (xf1.Z - xf2.Z).Length < NumericalTolerance, "Basis Z differed between antipodal quaternions" );
			Require( (xf1.Origin - xf2.Origin).Length < NumericalTolerance, "Origin differed between antipodal quaternions" );

			// Verify across multiple randomized orientations
			var rng = new Random( 1337 );
			for ( int i = 0; i < 20; i++ )
			{
				float p = (float)(rng.NextDouble() * 360 - 180);
				float y = (float)(rng.NextDouble() * 360 - 180);
				float r = (float)(rng.NextDouble() * 360 - 180);
				var qA = Quat.FromAngles( p, y, r );
				var qB = new Quat( -qA.x, -qA.y, -qA.z, -qA.w );
				Require( QuatDistance( qA, qB ) < NumericalTolerance, $"Antipodal test failed for angles ({p}, {y}, {r})" );
			}
		} );

		// -----------------------------------------------------------------------------------------
		// 3. Multi-root disconnected rigs
		// -----------------------------------------------------------------------------------------
		Check( "multiple roots and disconnected hierarchies evaluate identically to kernel forward kinematics", () =>
		{
			// Three roots: rootA (-1), rootB (-1), rootC (-1)
			var rig = new Skeleton();
			rig.AddBone( "rootA", -1, Xform.Translate( new Vec3( 10, 0, 0 ) ) );
			rig.AddBone( "childA1", 0, Xform.Translate( new Vec3( 0, 5, 0 ) ) );
			rig.AddBone( "childA2", 1, Xform.Translate( new Vec3( 0, 0, 3 ) ) );
			rig.AddBone( "rootB", -1, Xform.Translate( new Vec3( -20, 10, 5 ) ) );
			rig.AddBone( "childB1", 3, Xform.Translate( new Vec3( 2, 2, 2 ) ) );
			rig.AddBone( "rootC", -1, Xform.Translate( new Vec3( 0, 0, 50 ) ) );

			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var tRootB = new RigTrack { Name = "rootB", Kind = RigTrackKind.Bone };
			tRootB.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( -20, 10, 5 ), Quat.Identity, Vec3.One ) } );
			tRootB.Keys.Add( new RigKey { Frame = 30, Ease = RigEase.Linear, Local = new RigPose( new Vec3( -20, 20, 15 ), Quat.FromAngles( 0, 90, 0 ), Vec3.One ) } );
			clip.Tracks.Add( tRootB );

			var player = new MarionettePlayer( rig );
			player.LoadClip( clip );
			player.Time = 0.5f; // Frame 15
			player.Step( 0 );

			var expectedLocal = clip.SampleLocal( rig, 15f );
			var expectedWorld = RigAnimFile.ModelSpace( rig, expectedLocal );

			for ( int b = 0; b < rig.Count; b++ )
			{
				float dist = (player.WorldPose[b].Origin - expectedWorld[b].Origin).Length;
				Require( dist < NumericalTolerance, $"Bone {b} ({rig.Bones[b].Name}) world position mismatch in multi-root rig: {dist}" );
			}
		} );

		// -----------------------------------------------------------------------------------------
		// 4. Boundary clips: zero-frame and one-frame clips
		// -----------------------------------------------------------------------------------------
		Check( "boundary clips: zero-frame and one-frame clips evaluate without NaN or out-of-bounds", () =>
		{
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Translate( new Vec3( 1, 2, 3 ) ) );
			rig.AddBone( "child", 0, Xform.Translate( new Vec3( 0, 1, 0 ) ) );

			// Zero-frame clip (FrameCount = 0)
			var zeroClip = new RigAnimFile { FrameRate = 30, FrameCount = 0 };
			var pZero = new MarionettePlayer( rig );
			pZero.LoadClip( zeroClip );
			pZero.Playing = true;
			pZero.Step( 0.5f );
			Require( !float.IsNaN( pZero.WorldPose[0].Origin.x ) && !float.IsInfinity( pZero.WorldPose[0].Origin.x ),
				"Zero-frame clip produced NaN/infinite origin" );
			Require( MathF.Abs( pZero.WorldPose[0].Origin.x - 1f ) < NumericalTolerance, "Zero-frame clip deviated from bind pose" );

			// One-frame clip (FrameCount = 1, single key at frame 0)
			var oneClip = new RigAnimFile { FrameRate = 30, FrameCount = 1 };
			var t = new RigTrack { Name = "root", Kind = RigTrackKind.Bone };
			t.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 7, 8, 9 ), Quat.Identity, Vec3.One ) } );
			oneClip.Tracks.Add( t );

			var pOne = new MarionettePlayer( rig );
			pOne.LoadClip( oneClip );
			pOne.Playing = true;
			pOne.Step( 1.0f );
			Require( !float.IsNaN( pOne.WorldPose[0].Origin.x ), "One-frame clip produced NaN origin" );
			Require( MathF.Abs( pOne.WorldPose[0].Origin.x - 7f ) < NumericalTolerance,
				$"One-frame clip expected x=7, got {pOne.WorldPose[0].Origin.x}" );

			// Empty track with no keys
			var emptyTrackClip = new RigAnimFile { FrameRate = 30, FrameCount = 10 };
			emptyTrackClip.Tracks.Add( new RigTrack { Name = "root", Kind = RigTrackKind.Bone } );
			var pEmpty = new MarionettePlayer( rig );
			pEmpty.LoadClip( emptyTrackClip );
			pEmpty.Step( 0.2f );
			Require( MathF.Abs( pEmpty.WorldPose[0].Origin.x - 1f ) < NumericalTolerance, "Empty track clip deviated from bind" );
		} );

		// -----------------------------------------------------------------------------------------
		// 5. Singular transform rejection: direct, singular axis, degenerate basis, and inherited
		// -----------------------------------------------------------------------------------------
		Check( "singular bind transform rejection for zero-scale, non-invertible axis, and inherited singular parent", () =>
		{
			// Direct zero scale
			var rigZero = new Skeleton();
			rigZero.AddBone( "broken", -1, Xform.Scale( Vec3.Zero ) );
			bool threwZero = false;
			try { new MarionettePlayer( rigZero ); }
			catch ( InvalidDataException ex ) when ( ex.Message.Contains( "singular bind transform" ) ) { threwZero = true; }
			Require( threwZero, "Rig with zero-scale bind transform must be rejected with InvalidDataException" );

			// Partial zero scale (det == 0)
			var rigAxisZero = new Skeleton();
			rigAxisZero.AddBone( "brokenY", -1, Xform.Scale( new Vec3( 1, 0, 1 ) ) );
			bool threwAxis = false;
			try { new MarionettePlayer( rigAxisZero ); }
			catch ( InvalidDataException ex ) when ( ex.Message.Contains( "singular bind transform" ) ) { threwAxis = true; }
			Require( threwAxis, "Rig with non-invertible scale (1,0,1) must be rejected" );

			// Linearly dependent / degenerate basis vectors
			var rigDegenerate = new Skeleton();
			var colX = new Vec3( 1, 0, 0 );
			var colY = new Vec3( 1, 0, 0 ); // identical to X -> determinant = 0
			var colZ = new Vec3( 0, 0, 1 );
			rigDegenerate.AddBone( "coplanar", -1, new Xform( colX, colY, colZ, Vec3.Zero ) );
			bool threwDegen = false;
			try { new MarionettePlayer( rigDegenerate ); }
			catch ( InvalidDataException ex ) when ( ex.Message.Contains( "singular bind transform" ) ) { threwDegen = true; }
			Require( threwDegen, "Rig with linearly dependent basis vectors must be rejected" );

			// Inherited singular parent
			var rigInherited = new Skeleton();
			rigInherited.AddBone( "singularParent", -1, Xform.Scale( Vec3.Zero ) );
			rigInherited.AddBone( "child", 0, Xform.Identity );
			bool threwInherited = false;
			try { new MarionettePlayer( rigInherited ); }
			catch ( InvalidDataException ) { threwInherited = true; }
			Require( threwInherited, "Child inheriting singular transform from parent must cause rejection" );
		} );

		// -----------------------------------------------------------------------------------------
		// 6. Over-capacity skeleton rejection (limit 220) without clamping IDs
		// -----------------------------------------------------------------------------------------
		Check( "over-capacity skeleton (>220 bones) is rejected with NotSupportedException without clamping bone IDs", () =>
		{
			// Exactly 220 bones: supported
			var rigExact = new Skeleton();
			for ( int i = 0; i < MarionettePlayer.MaxGpuBones; i++ )
				rigExact.AddBone( $"b_{i}", -1, Xform.Identity );
			var pExact = new MarionettePlayer( rigExact );
			Require( pExact.GpuBones.Length == MarionettePlayer.MaxGpuBones, "Exact limit (220) must be supported" );

			// 221 bones: rejected with NotSupportedException
			var rigOver = new Skeleton();
			for ( int i = 0; i < MarionettePlayer.MaxGpuBones + 1; i++ )
				rigOver.AddBone( $"b_{i}", -1, Xform.Identity );

			bool threwOver = false;
			string? errorMsg = null;
			try
			{
				new MarionettePlayer( rigOver );
			}
			catch ( NotSupportedException ex )
			{
				threwOver = true;
				errorMsg = ex.Message;
			}
			Require( threwOver, "Rig with 221 bones must throw NotSupportedException" );
			Require( errorMsg != null && errorMsg.Contains( "220" ) && errorMsg.Contains( "221" ),
				$"Exception message must state limit 220 and actual 221, got: {errorMsg}" );
		} );

		// -----------------------------------------------------------------------------------------
		// 7. CPU Linear Blend Skinning vs GPU matrix palette agreement across frames
		// -----------------------------------------------------------------------------------------
		Check( "skinning palette agreement between CPU Linear Blend Skinning and GPU matrix palette across animated frames", () =>
		{
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Translate( new Vec3( 1, 2, 3 ) ) );
			rig.AddBone( "mid", 0, Xform.Translate( new Vec3( 0, 4, 0 ) ) );
			rig.AddBone( "tip", 1, Xform.Translate( new Vec3( 0, 3, 0 ) ) );

			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var tRoot = new RigTrack { Name = "root", Kind = RigTrackKind.Bone };
			tRoot.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 1, 2, 3 ), Quat.Identity, Vec3.One ) } );
			tRoot.Keys.Add( new RigKey { Frame = 30, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 5, 2, 6 ), Quat.FromAngles( 20, 0, 15 ), Vec3.One ) } );
			clip.Tracks.Add( tRoot );

			var tMid = new RigTrack { Name = "mid", Kind = RigTrackKind.Bone };
			tMid.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Smooth, Local = new RigPose( new Vec3( 0, 4, 0 ), Quat.Identity, Vec3.One ) } );
			tMid.Keys.Add( new RigKey { Frame = 30, Ease = RigEase.Smooth, Local = new RigPose( new Vec3( 0, 4, 0 ), Quat.FromAngles( 0, 45, 0 ), Vec3.One ) } );
			clip.Tracks.Add( tMid );

			var player = new MarionettePlayer( rig );
			player.LoadClip( clip );

			var positions = new[]
			{
				new Vec3( 1, 2, 3 ),
				new Vec3( 1, 4, 3 ),
				new Vec3( 1, 7, 3 ),
				new Vec3( 2, 8, 4 )
			};
			var weights = new SkinWeights( 4 );
			weights[0] = new[] { new BoneWeight( 0, 1f ) };
			weights[1] = new[] { new BoneWeight( 0, 0.4f ), new BoneWeight( 1, 0.6f ) };
			weights[2] = new[] { new BoneWeight( 1, 0.3f ), new BoneWeight( 2, 0.7f ) };
			weights[3] = new[] { new BoneWeight( 0, 0.2f ), new BoneWeight( 1, 0.3f ), new BoneWeight( 2, 0.5f ) };

			var bindWorld = RigAnimFile.ModelSpace( rig, rig.Bones.Select( b => b.Local ).ToArray() );

			foreach ( float frame in new[] { 0f, 5.25f, 15.0f, 22.5f, 30.0f } )
			{
				player.Time = frame / 30f;
				player.Step( 0 );

				// CPU linear blend deformation
				var cpuDeformed = SkinBinder.Deform( positions, weights, bindWorld, player.WorldPose.ToArray() );

				// GPU palette transformation: Vector3.Transform(pos, GpuBones[bone]) * weight
				for ( int v = 0; v < positions.Length; v++ )
				{
					var p = positions[v];
					var pVec = new Vector3( p.x, p.y, p.z );
					var gpuPos = Vector3.Zero;
					foreach ( var bw in weights[v] )
						gpuPos += Vector3.Transform( pVec, player.GpuBones[bw.Bone] ) * bw.Weight;

					var cpuPos = new Vector3( cpuDeformed[v].x, cpuDeformed[v].y, cpuDeformed[v].z );
					float diff = Vector3.Distance( cpuPos, gpuPos );
					Require( diff < NumericalTolerance,
						$"Frame {frame} vertex {v}: CPU ({cpuPos}) vs GPU ({gpuPos}) difference {diff:E4} exceeded tolerance {NumericalTolerance}" );
				}
			}
		} );

		// -----------------------------------------------------------------------------------------
		// 8. Top-4 skin weight normalization and invalid weight detection
		// -----------------------------------------------------------------------------------------
		Check( "top-four skin weight pruning and normalization guarantees partition of unity and detects invalid weights", () =>
		{
			// 6 influences pruned to top 4
			var multiWeights = new[]
			{
				new BoneWeight( 0, 0.30f ),
				new BoneWeight( 1, 0.25f ),
				new BoneWeight( 2, 0.20f ),
				new BoneWeight( 3, 0.15f ),
				new BoneWeight( 4, 0.08f ),
				new BoneWeight( 5, 0.02f )
			};
			var pruned = SkinWeights.Prune( multiWeights, 4 );
			Require( pruned.Length == 4, $"Expected 4 weights after pruning, got {pruned.Length}" );
			Require( pruned[0].Bone == 0 && pruned[1].Bone == 1 && pruned[2].Bone == 2 && pruned[3].Bone == 3,
				"Prune must retain the 4 strongest bone influences" );

			float sum = pruned.Sum( w => w.Weight );
			Require( MathF.Abs( sum - 1.0f ) < 1e-6f, $"Pruned weights must sum to 1.0, got {sum}" );

			// Unnormalized input weights with > 4 influences re-normalize to 1.0 upon pruning
			var unnormalized5 = new[]
			{
				new BoneWeight( 0, 1.5f ),
				new BoneWeight( 1, 1.5f ),
				new BoneWeight( 2, 0.5f ),
				new BoneWeight( 3, 0.5f ),
				new BoneWeight( 4, 0.2f )
			};
			var prunedUnnorm = SkinWeights.Prune( unnormalized5, 4 );
			float unnormSum = prunedUnnorm.Sum( w => w.Weight );
			Require( MathF.Abs( unnormSum - 1.0f ) < 1e-6f, $"Pruned unnormalized weights must re-normalize to 1.0, got {unnormSum}" );

			// Detection of invalid weights (unnormalized <= 4, out-of-range bone, negative weight, unweighted)
			var skin = new SkinWeights( 4 );
			skin[0] = new[] { new BoneWeight( 0, 1.0f ) }; // valid
			skin[1] = new[] { new BoneWeight( 99, 1.0f ) }; // out-of-range bone ID
			skin[2] = new[] { new BoneWeight( 0, -0.5f ), new BoneWeight( 1, 1.5f ) }; // negative weight
			skin[3] = Array.Empty<BoneWeight>(); // unweighted

			var errors = skin.Validate( 4, boneCount: 5 );
			Require( errors.Count >= 3, $"Expected at least 3 validation errors, got {errors.Count}: {string.Join( "; ", errors )}" );
			Require( errors.Any( e => e.Contains( "out of range" ) ), "Missing out-of-range bone error" );
			Require( errors.Any( e => e.Contains( "negative weight" ) ), "Missing negative weight error" );
			Require( errors.Any( e => e.Contains( "unweighted" ) ), "Missing unweighted error" );
		} );

		// -----------------------------------------------------------------------------------------
		// 9. Input invariants: Animated triangle picking follows deformed mesh
		// -----------------------------------------------------------------------------------------
		Check( "animated triangle picking follows deformed mesh across time and rejects missed rays", () =>
		{
			// Triangle spanning (0,0,0)-(2,0,0)-(0,2,0)
			var mesh = new PolyMesh();
			mesh.Positions.AddRange( new[] { new Vec3( 0, 0, 0 ), new Vec3( 2, 0, 0 ), new Vec3( 0, 2, 0 ) } );
			mesh.Faces.Add( new Face( new[] { 0, 1, 2 } ) );

			mesh.Skin = new SkinWeights( 3 );
			for ( int i = 0; i < 3; i++ )
				mesh.Skin[i] = new[] { new BoneWeight( 0, 1.0f ) };

			var picker = new MeshPicker();
			picker.Add( mesh );

			// Identity palette: ray through triangle center (0.5, 0.5, 5) heading -Z hits piece 0
			var idPalette = new[] { Matrix4x4.Identity };
			int hitBind = picker.Pick( new Vector3( 0.5f, 0.5f, 5f ), -Vector3.UnitZ, idPalette );
			Require( hitBind == 0, $"Expected hit at bind pose, got {hitBind}" );

			// Ray outside bind triangle misses
			int missAtDisplaced = picker.Pick( new Vector3( 10.5f, 0.5f, 5f ), -Vector3.UnitZ, idPalette );
			Require( missAtDisplaced == -1, $"Expected miss outside bind triangle, got {missAtDisplaced}" );

			// Displace bone 0 by translation (10, 0, 0)
			var displacedPalette = new[] { Matrix4x4.CreateTranslation( 10, 0, 0 ) };

			// Ray at old bind location must MISS deformed mesh
			int missOldBind = picker.Pick( new Vector3( 0.5f, 0.5f, 5f ), -Vector3.UnitZ, displacedPalette );
			Require( missOldBind == -1, $"Ray at old bind position must miss deformed mesh, got {missOldBind}" );

			// Ray at new displaced position must HIT
			int hitDisplaced = picker.Pick( new Vector3( 10.5f, 0.5f, 5f ), -Vector3.UnitZ, displacedPalette );
			Require( hitDisplaced == 0, $"Ray at deformed position must hit piece 0, got {hitDisplaced}" );

			// Ray far outside bounds is rejected immediately in broad phase
			int hitFar = picker.Pick( new Vector3( 100f, 100f, 5f ), -Vector3.UnitZ, displacedPalette );
			Require( hitFar == -1, $"Ray far outside bounds must miss, got {hitFar}" );
		} );

		// -----------------------------------------------------------------------------------------
		// 10. Input invariants: Bind pose and original source data remain completely immutable
		// -----------------------------------------------------------------------------------------
		Check( "bind pose and original source data remain completely immutable across animation scrub, posing, and IK", () =>
		{
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Translate( new Vec3( 2, 4, 6 ) ) );
			rig.AddBone( "mid", 0, Xform.Translate( new Vec3( 0, 3, 0 ) ) );
			rig.AddBone( "end", 1, Xform.Translate( new Vec3( 0, 3, 0 ) ) );

			var origBones = rig.Bones.Select( b => (b.Name, b.Parent, b.Local) ).ToArray();

			var mesh = new PolyMesh();
			mesh.Positions.AddRange( new[] { new Vec3( 1, 2, 3 ), new Vec3( 4, 5, 6 ), new Vec3( 7, 8, 9 ) } );
			mesh.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			mesh.Skin = new SkinWeights( 3 );
			mesh.Skin[0] = new[] { new BoneWeight( 0, 1f ) };
			mesh.Skin[1] = new[] { new BoneWeight( 1, 1f ) };
			mesh.Skin[2] = new[] { new BoneWeight( 2, 1f ) };

			var origPositions = mesh.Positions.ToArray();
			var origWeights = mesh.Skin.Vertices.Select( w => (BoneWeight[])w.Clone() ).ToArray();

			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var ikTrack = new RigTrack { Kind = RigTrackKind.IkGoal, Name = "end" };
			ikTrack.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 5, 5, 5 ), Quat.Identity, Vec3.One ) } );
			ikTrack.Keys.Add( new RigKey { Frame = 30, Ease = RigEase.Linear, Local = new RigPose( new Vec3( -5, 5, 2 ), Quat.Identity, Vec3.One ) } );
			clip.Tracks.Add( ikTrack );

			// Exercise player, scrubbing, and posing
			var player = new MarionettePlayer( rig );
			player.LoadClip( clip );
			player.Time = 0.5f; player.Step( 0 );
			player.Step( 0.25f );
			player.Step( 1.0f );
			player.ResetBindPose();

			// Exercise CPU deformation
			var bindWorld = RigAnimFile.ModelSpace( rig, rig.Bones.Select( b => b.Local ).ToArray() );
			_ = SkinBinder.Deform( mesh.Positions, mesh.Skin, bindWorld, player.WorldPose.ToArray() );

			// Exercise picker
			var picker = new MeshPicker();
			picker.Add( mesh );
			_ = picker.Bounds( 0, player.GpuBones );
			_ = picker.Pick( new Vector3( 2, 3, 10 ), -Vector3.UnitZ, player.GpuBones );

			// Verify rig immutability
			for ( int b = 0; b < rig.Count; b++ )
			{
				var cur = rig.Bones[b];
				var orig = origBones[b];
				Require( cur.Name == orig.Name, $"Bone {b} name was mutated" );
				Require( cur.Parent == orig.Parent, $"Bone {b} parent was mutated" );
				Require( VecEqual( cur.Local.Origin, orig.Local.Origin ), $"Bone {b} local origin was mutated: {cur.Local.Origin} vs {orig.Local.Origin}" );
				Require( VecEqual( cur.Local.X, orig.Local.X ) && VecEqual( cur.Local.Y, orig.Local.Y ) && VecEqual( cur.Local.Z, orig.Local.Z ),
					$"Bone {b} local basis was mutated" );
			}

			// Verify mesh positions immutability
			for ( int v = 0; v < mesh.Positions.Count; v++ )
			{
				Require( VecEqual( mesh.Positions[v], origPositions[v] ), $"Mesh position {v} was mutated" );
			}

			// Verify mesh weights immutability
			for ( int v = 0; v < mesh.Skin.Count; v++ )
			{
				var curW = mesh.Skin[v];
				var origW = origWeights[v];
				Require( curW.Length == origW.Length, $"Mesh weight count for vertex {v} was mutated" );
				for ( int w = 0; w < curW.Length; w++ )
				{
					Require( curW[w].Bone == origW[w].Bone && curW[w].Weight == origW[w].Weight,
						$"Mesh weight {w} for vertex {v} was mutated" );
				}
			}
		} );

		// -----------------------------------------------------------------------------------------
		// 11. Seeded deterministic randomness: Randomized pose generation emits failing seed and inputs
		// -----------------------------------------------------------------------------------------
		Check( "seeded deterministic randomness: randomized multi-bone poses emit failing seed and input on discrepancy", () =>
		{
			const int seed = 0x5EEDF00D;
			var rng = new Random( seed );

			for ( int iter = 0; iter < 30; iter++ )
			{
				var rig = new Skeleton();
				float r0x = (float)(rng.NextDouble() * 20 - 10);
				float r0y = (float)(rng.NextDouble() * 20 - 10);
				float r0z = (float)(rng.NextDouble() * 20 - 10);
				float rot0 = (float)(rng.NextDouble() * MathF.PI);
				var rootXf = Xform.Rotate( new Vec3( 0, 1, 0 ), rot0 ) * Xform.Translate( new Vec3( r0x, r0y, r0z ) );
				rig.AddBone( "bone0", -1, rootXf );

				rig.AddBone( "bone1", 0, Xform.Translate( new Vec3( (float)rng.NextDouble() * 5, 2, 0 ) ) );
				rig.AddBone( "bone2", 1, Xform.Translate( new Vec3( 0, (float)rng.NextDouble() * 4, 1 ) ) );
				rig.AddBone( "bone3", 0, Xform.Translate( new Vec3( -2, 0, (float)rng.NextDouble() * 3 ) ) );

				var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
				for ( int b = 0; b < rig.Count; b++ )
				{
					if ( rng.NextDouble() > 0.3 ) // Some bones unkeyed
					{
						var track = new RigTrack { Name = rig.Bones[b].Name, Kind = RigTrackKind.Bone };
						track.Keys.Add( new RigKey
						{
							Frame = 0,
							Ease = (RigEase)(rng.Next( 0, 5 )),
							Local = new RigPose(
								new Vec3( (float)rng.NextDouble() * 10 - 5, (float)rng.NextDouble() * 10 - 5, (float)rng.NextDouble() * 10 - 5 ),
								Quat.FromAngles( (float)rng.NextDouble() * 90, (float)rng.NextDouble() * 90, (float)rng.NextDouble() * 90 ),
								Vec3.One )
						} );
						track.Keys.Add( new RigKey
						{
							Frame = 30,
							Ease = RigEase.Linear,
							Local = new RigPose(
								new Vec3( (float)rng.NextDouble() * 10 - 5, (float)rng.NextDouble() * 10 - 5, (float)rng.NextDouble() * 10 - 5 ),
								Quat.FromAngles( (float)rng.NextDouble() * 90, (float)rng.NextDouble() * 90, (float)rng.NextDouble() * 90 ),
								Vec3.One )
						} );
						clip.Tracks.Add( track );
					}
				}

				var player = new MarionettePlayer( rig );
				player.LoadClip( clip );

				float testFrame = (float)(rng.NextDouble() * 30);
				player.Time = testFrame / 30f;
				player.Step( 0 );

				var expectedLocal = clip.SampleLocal( rig, testFrame );
				var expectedWorld = RigAnimFile.ModelSpace( rig, expectedLocal );

				for ( int b = 0; b < rig.Count; b++ )
				{
					var pWorld = player.WorldPose[b];
					var eWorld = expectedWorld[b];
					float posErr = (pWorld.Origin - eWorld.Origin).Length;
					float xErr = (pWorld.X - eWorld.X).Length;
					float yErr = (pWorld.Y - eWorld.Y).Length;
					float zErr = (pWorld.Z - eWorld.Z).Length;

					if ( posErr >= NumericalTolerance || xErr >= NumericalTolerance || yErr >= NumericalTolerance || zErr >= NumericalTolerance )
					{
						throw new Exception( $"Deterministic seeded test failure! "
							+ $"Seed: 0x{seed:X8}, Iteration: {iter}, Frame: {testFrame:F4}, Bone: {b} ({rig.Bones[b].Name}), "
							+ $"Position Error: {posErr:E4}, Expected: {eWorld.Origin}, Actual: {pWorld.Origin}" );
					}
				}
			}
		} );
	}
}
