#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Effigy;
using Marionette.Desktop;

internal static class MorphEvaluationTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "morph-eval", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		bool VecEqual( Vec3 a, Vec3 b, float eps = 1e-5f ) =>
			MathF.Abs( a.x - b.x ) <= eps && MathF.Abs( a.y - b.y ) <= eps && MathF.Abs( a.z - b.z ) <= eps;

		PolyMesh CreateTriangleMesh()
		{
			var mesh = new PolyMesh();
			mesh.Positions.Add( new Vec3( 0, 0, 0 ) );
			mesh.Positions.Add( new Vec3( 10, 0, 0 ) );
			mesh.Positions.Add( new Vec3( 0, 10, 0 ) );
			mesh.Faces.Add( new Face(
				new[] { 0, 1, 2 },
				new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 0, 1 ) },
				0 ) );
			return mesh;
		}

		RigKey CreateMorphKey( int frame, float value ) =>
			new()
			{
				Frame = frame,
				Local = new RigPose( Vec3.Zero, Quat.Identity, new Vec3( value, 1f, 1f ) )
			};

		// 1. weights 0, 0.5, 1.0 produce exact linear delta scaling
		Check( "weights 0, 0.5, 1.0 produce exact linear delta scaling", () =>
		{
			var mesh = CreateTriangleMesh();
			var deltas = new[]
			{
				new Vec3( 2, 4, 6 ),
				new Vec3( 0, 5, 0 ),
				new Vec3( -1, -2, -3 )
			};
			var morph = new MeshMorph { Name = "test_target", Deltas = deltas };
			mesh.Morphs.Add( morph );

			var evaluator = MorphEvaluator.FromMesh( mesh );
			var outPos = new Vec3[3];

			// Weight 0.0 -> exact base positions
			evaluator.Evaluate( null, 0f );
			evaluator.DeformPositions( mesh.Positions.ToArray(), outPos );
			Require( outPos[0].Equals( mesh.Positions[0] ), "Weight 0 must match base pos exactly" );
			Require( outPos[1].Equals( mesh.Positions[1] ), "Weight 0 must match base pos exactly" );
			Require( outPos[2].Equals( mesh.Positions[2] ), "Weight 0 must match base pos exactly" );

			// Weight 0.5 -> exact 0.5 scaling
			var weights05 = new Dictionary<string, float> { ["test_target"] = 0.5f };
			MorphEvaluator.DeformPositions( mesh.Positions.ToArray(), weights05, evaluator.Morphs, outPos );
			Require( VecEqual( outPos[0], new Vec3( 1, 2, 3 ) ), "Weight 0.5 scale failed on v0" );
			Require( VecEqual( outPos[1], new Vec3( 10, 2.5f, 0 ) ), "Weight 0.5 scale failed on v1" );
			Require( VecEqual( outPos[2], new Vec3( -0.5f, 9, -1.5f ) ), "Weight 0.5 scale failed on v2" );

			// Weight 1.0 -> exact 1.0 scaling
			var weights10 = new Dictionary<string, float> { ["test_target"] = 1.0f };
			MorphEvaluator.DeformPositions( mesh.Positions.ToArray(), weights10, evaluator.Morphs, outPos );
			Require( VecEqual( outPos[0], new Vec3( 2, 4, 6 ) ), "Weight 1.0 scale failed on v0" );
			Require( VecEqual( outPos[1], new Vec3( 10, 5, 0 ) ), "Weight 1.0 scale failed on v1" );
			Require( VecEqual( outPos[2], new Vec3( -1, 8, -3 ) ), "Weight 1.0 scale failed on v2" );
		} );

		// 2. two concurrent morph targets sum deltas correctly
		Check( "two concurrent morph targets sum deltas correctly", () =>
		{
			var mesh = CreateTriangleMesh();
			var deltasA = new[] { new Vec3( 1, 0, 0 ), new Vec3( 2, 0, 0 ), new Vec3( 3, 0, 0 ) };
			var deltasB = new[] { new Vec3( 0, 2, 0 ), new Vec3( 0, 4, 0 ), new Vec3( 0, 6, 0 ) };

			mesh.Morphs.Add( new MeshMorph { Name = "targetA", Deltas = deltasA } );
			mesh.Morphs.Add( new MeshMorph { Name = "targetB", Deltas = deltasB } );

			var evaluator = MorphEvaluator.FromMesh( mesh );
			var outPos = new Vec3[3];

			var weights = new Dictionary<string, float>
			{
				["targetA"] = 0.4f,
				["targetB"] = 0.6f
			};

			MorphEvaluator.DeformPositions( mesh.Positions.ToArray(), weights, evaluator.Morphs, outPos );

			// v0: (0,0,0) + 0.4*(1,0,0) + 0.6*(0,2,0) = (0.4, 1.2, 0)
			Require( VecEqual( outPos[0], new Vec3( 0.4f, 1.2f, 0 ) ), "Target sum failed on v0" );
			// v1: (10,0,0) + 0.4*(2,0,0) + 0.6*(0,4,0) = (10.8, 2.4, 0)
			Require( VecEqual( outPos[1], new Vec3( 10.8f, 2.4f, 0 ) ), "Target sum failed on v1" );
			// v2: (0,10,0) + 0.4*(3,0,0) + 0.6*(0,6,0) = (1.2, 13.6, 0)
			Require( VecEqual( outPos[2], new Vec3( 1.2f, 13.6f, 0 ) ), "Target sum failed on v2" );
		} );

		// 3. UV duplicate vertices receive identical morph offsets
		Check( "UV duplicate vertices receive identical morph offsets", () =>
		{
			var mesh = new PolyMesh();
			mesh.Positions.Add( new Vec3( 0, 0, 0 ) );   // 0
			mesh.Positions.Add( new Vec3( 10, 0, 0 ) );  // 1 (shared)
			mesh.Positions.Add( new Vec3( 10, 10, 0 ) ); // 2 (shared)
			mesh.Positions.Add( new Vec3( 0, 10, 0 ) );  // 3

			// Face 0: 0, 1, 2 with UVs [ (0,0), (0.5,0), (0.5,1) ]
			mesh.Faces.Add( new Face(
				new[] { 0, 1, 2 },
				new[] { new Vec2( 0f, 0f ), new Vec2( 0.5f, 0f ), new Vec2( 0.5f, 1f ) },
				0 ) );

			// Face 1: 0, 2, 3 with UV seam on vertex 2: [ (0,0), (1.0f, 1.0f), (0,1) ]
			mesh.Faces.Add( new Face(
				new[] { 0, 2, 3 },
				new[] { new Vec2( 0f, 0f ), new Vec2( 1.0f, 1.0f ), new Vec2( 0f, 1f ) },
				0 ) );

			var delta = new Vec3( 3, -2, 5 );
			var deltas = new[] { Vec3.Zero, Vec3.Zero, delta, Vec3.Zero };
			mesh.Morphs.Add( new MeshMorph { Name = "seam_morph", Deltas = deltas } );

			var map = CornerVertexMap.FromMesh( mesh );
			Require( map.CornerCount == 6, $"CornerCount should be 6 but was {map.CornerCount}" );

			// Find corners mapping to source vertex 2
			var cornersForV2 = map.SourceToCorners[2];
			Require( cornersForV2.Length == 2, "Vertex 2 should be duplicated into 2 corners" );

			int cA = cornersForV2[0];
			int cB = cornersForV2[1];

			// Verify they have different UVs (UV seam)
			Require( map.BaseUVs[cA].x != map.BaseUVs[cB].x || map.BaseUVs[cA].y != map.BaseUVs[cB].y,
				"Corners must have distinct UVs across seam" );

			var retained = RetainedMeshMorphs.FromMesh( mesh );
			var weights = new Dictionary<string, float> { ["seam_morph"] = 0.8f };

			var outCornerPos = new Vec3[map.CornerCount];
			var outCornerNorm = new Vec3[map.CornerCount];

			MorphEvaluator.DeformCorners( map, weights, retained, outCornerPos, outCornerNorm );

			// Both duplicate corners MUST receive identical morph offsets
			Require( VecEqual( outCornerPos[cA], outCornerPos[cB] ),
				$"Duplicate corners must receive identical deformed positions: {outCornerPos[cA]} vs {outCornerPos[cB]}" );

			var expectedPos = mesh.Positions[2] + delta * 0.8f;
			Require( VecEqual( outCornerPos[cA], expectedPos ), "Corner position did not match expected delta" );
		} );

		// 4. keyed vs event vs driver precedence order
		Check( "keyed vs event vs driver precedence order", () =>
		{
			var skeleton = new Skeleton();
			skeleton.Bones = new List<Bone> { new Bone( "head", -1, Xform.Identity, 1f ) };

			var driver = new MorphDriver
			{
				Morph = "blink",
				Bone = "head",
				Channel = DriverChannel.Rotation,
				Axis = DriverAxis.Y,
				Rest = 0f,
				Target = 90f,
				Ease = RigEase.Linear,
				Clamp = true,
				Value = 0.35f,
				Enabled = true
			};

			var track = new RigTrack { Kind = RigTrackKind.Morph, Name = "blink" };
			track.Keys.Add( CreateMorphKey( 10, 0.65f ) );

			var morphEvent = new RigMorphEvent
			{
				Morph = "blink",
				Start = 5,
				End = 15,
				FadeIn = 0,
				FadeOut = 0,
				Value = 0.90f
			};

			var pose = new[] { Quat.FromAxisAngle( new Vec3( 0, 1, 0 ), MathF.PI / 2f ).ToXform( Vec3.Zero ) };

			// Case A: Driver only -> weight is 0.35
			var weightsA = MorphEvaluator.EvaluateWeights(
				new[] { driver }, null, null, 10f, skeleton, pose );
			Require( MathF.Abs( weightsA["blink"] - 0.35f ) < 1e-4f,
				$"Driver-only expected 0.35 but got {weightsA["blink"]}" );

			// Case B: Driver + Keyed track -> Keyed overrides driver (0.65)
			var weightsB = MorphEvaluator.EvaluateWeights(
				new[] { driver }, new[] { track }, null, 10f, skeleton, pose );
			Require( MathF.Abs( weightsB["blink"] - 0.65f ) < 1e-4f,
				$"Keyed-over-driver expected 0.65 but got {weightsB["blink"]}" );

			// Case C: Driver + Keyed track + Active event -> Event overrides keyed (0.90)
			var weightsC = MorphEvaluator.EvaluateWeights(
				new[] { driver }, new[] { track }, new[] { morphEvent }, 10f, skeleton, pose );
			Require( MathF.Abs( weightsC["blink"] - 0.90f ) < 1e-4f,
				$"Event-over-keyed expected 0.90 but got {weightsC["blink"]}" );

			// Case D: Outside event window (frame 20) -> event inactive, falls back to Keyed curve
			var weightsD = MorphEvaluator.EvaluateWeights(
				new[] { driver }, new[] { track }, new[] { morphEvent }, 20f, skeleton, pose );
			Require( MathF.Abs( weightsD["blink"] - 0.65f ) < 1e-4f,
				$"Inactive event expected fallback to keyed (0.65) but got {weightsD["blink"]}" );
		} );

		// 5. pose-space corrective activates at bent joint angle
		Check( "pose-space corrective activates at bent joint angle", () =>
		{
			var skeleton = new Skeleton();
			skeleton.Bones = new List<Bone>
			{
				new Bone( "root", -1, Xform.Identity, 1f ),
				new Bone( "knee_L", 0, Xform.Identity, 1f )
			};

			var driver = new MorphDriver
			{
				Morph = "knee_fold",
				Bone = "knee_L",
				Channel = DriverChannel.Rotation,
				Axis = DriverAxis.X,
				Rest = 0f,
				Target = 90f,
				Ease = RigEase.Linear,
				Clamp = true,
				Value = 1.0f,
				Enabled = true
			};

			var mesh = CreateTriangleMesh();
			mesh.Morphs.Add( new MeshMorph
			{
				Name = "knee_fold",
				Deltas = new[] { new Vec3( 0, 0, 5 ), new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 0 ) },
				Driver = driver
			} );

			var evaluator = MorphEvaluator.FromMesh( mesh );

			// Pose 1: Unbent knee (0 deg) -> weight 0
			var unbentPose = new[] { Xform.Identity, Xform.Identity };
			var w0 = evaluator.Evaluate( new[] { driver }, null, null, 0f, skeleton, unbentPose );
			Require( MathF.Abs( w0["knee_fold"] ) < 1e-5f, "Unbent knee must evaluate to 0 weight" );

			var outPos = new Vec3[3];
			evaluator.DeformPositions( mesh.Positions.ToArray(), outPos );
			Require( outPos[0].Equals( mesh.Positions[0] ), "Unbent knee must not deform mesh" );

			// Pose 2: Bent knee at 45 deg -> weight 0.5
			var bent45 = new[] { Xform.Identity, Quat.FromAxisAngle( new Vec3( 1, 0, 0 ), MathF.PI / 4f ).ToXform( Vec3.Zero ) };
			var w45 = evaluator.Evaluate( new[] { driver }, null, null, 0f, skeleton, bent45 );
			Require( MathF.Abs( w45["knee_fold"] - 0.5f ) < 1e-3f, $"Bent 45 deg expected 0.5 but got {w45["knee_fold"]}" );
			evaluator.DeformPositions( mesh.Positions.ToArray(), outPos );
			Require( VecEqual( outPos[0], new Vec3( 0, 0, 2.5f ) ), "Bent 45 deg must deform v0 by 2.5" );

			// Pose 3: Bent knee at 90 deg -> weight 1.0
			var bent90 = new[] { Xform.Identity, Quat.FromAxisAngle( new Vec3( 1, 0, 0 ), MathF.PI / 2f ).ToXform( Vec3.Zero ) };
			var w90 = evaluator.Evaluate( new[] { driver }, null, null, 0f, skeleton, bent90 );
			Require( MathF.Abs( w90["knee_fold"] - 1.0f ) < 1e-3f, $"Bent 90 deg expected 1.0 but got {w90["knee_fold"]}" );
			evaluator.DeformPositions( mesh.Positions.ToArray(), outPos );
			Require( VecEqual( outPos[0], new Vec3( 0, 0, 5.0f ) ), "Bent 90 deg must deform v0 by 5.0" );
		} );

		// 6. base geometry remains immutable after evaluation
		Check( "base geometry remains immutable after evaluation", () =>
		{
			var mesh = CreateTriangleMesh();
			var snap0 = mesh.Positions[0];
			var snap1 = mesh.Positions[1];
			var snap2 = mesh.Positions[2];

			mesh.Morphs.Add( new MeshMorph
			{
				Name = "heavy_warp",
				Deltas = new[] { new Vec3( 100, 200, 300 ), new Vec3( -50, 40, -10 ), new Vec3( 1, 2, 3 ) }
			} );

			var evaluator = MorphEvaluator.FromMesh( mesh );
			var outPos = new Vec3[3];

			for ( int i = 0; i < 5; i++ )
			{
				var weights = new Dictionary<string, float> { ["heavy_warp"] = 1.5f };
				evaluator.DeformPositions( mesh.Positions.ToArray(), outPos );
				MorphEvaluator.DeformPositions( mesh.Positions.ToArray(), weights, evaluator.Morphs, outPos );
			}

			Require( mesh.Positions[0].Equals( snap0 ), "Base position 0 was mutated!" );
			Require( mesh.Positions[1].Equals( snap1 ), "Base position 1 was mutated!" );
			Require( mesh.Positions[2].Equals( snap2 ), "Base position 2 was mutated!" );
		} );

		// 7. reset / zero weights restores base mesh exactly
		Check( "reset / zero weights restores base mesh exactly", () =>
		{
			var mesh = CreateTriangleMesh();
			mesh.Morphs.Add( new MeshMorph
			{
				Name = "warp",
				Deltas = new[] { new Vec3( 15, -20, 30 ), new Vec3( 5, 5, 5 ), new Vec3( -1, -2, -3 ) }
			} );

			var evaluator = MorphEvaluator.FromMesh( mesh );
			var outPos = new Vec3[3];

			var activeWeights = new Dictionary<string, float> { ["warp"] = 1.0f };
			MorphEvaluator.DeformPositions( mesh.Positions.ToArray(), activeWeights, evaluator.Morphs, outPos );
			Require( !outPos[0].Equals( mesh.Positions[0] ), "Mesh should be deformed" );

			var zeroWeights = new Dictionary<string, float> { ["warp"] = 0.0f };
			MorphEvaluator.DeformPositions( mesh.Positions.ToArray(), zeroWeights, evaluator.Morphs, outPos );
			Require( outPos[0].Equals( mesh.Positions[0] ), "Zero weights did not restore pos 0" );
			Require( outPos[1].Equals( mesh.Positions[1] ), "Zero weights did not restore pos 1" );
			Require( outPos[2].Equals( mesh.Positions[2] ), "Zero weights did not restore pos 2" );

			evaluator.Reset();
			evaluator.DeformPositions( mesh.Positions.ToArray(), outPos );
			Require( outPos[0].Equals( mesh.Positions[0] ), "Reset did not restore pos 0" );
			Require( outPos[1].Equals( mesh.Positions[1] ), "Reset did not restore pos 1" );
			Require( outPos[2].Equals( mesh.Positions[2] ), "Reset did not restore pos 2" );
		} );

		// 8. missing morph target handled safely without NaNs
		Check( "missing morph target handled safely without NaNs", () =>
		{
			var mesh = CreateTriangleMesh();
			mesh.Morphs.Add( new MeshMorph
			{
				Name = "valid_morph",
				Deltas = new[] { new Vec3( 1, 1, 1 ), new Vec3( 2, 2, 2 ), new Vec3( 3, 3, 3 ) }
			} );

			var evaluator = MorphEvaluator.FromMesh( mesh );
			var outPos = new Vec3[3];

			var badWeights = new Dictionary<string, float>
			{
				["valid_morph"] = 0.5f,
				["ghost_morph_xyz"] = 1.0f,
				["another_missing"] = float.NaN,
				["infinite_target"] = float.PositiveInfinity
			};

			MorphEvaluator.DeformPositions( mesh.Positions.ToArray(), badWeights, evaluator.Morphs, outPos );

			Require( VecEqual( outPos[0], new Vec3( 0.5f, 0.5f, 0.5f ) ), "Valid morph failed" );
			Require( VecEqual( outPos[1], new Vec3( 11.0f, 1.0f, 1.0f ) ), "Valid morph failed on v1" );
			Require( VecEqual( outPos[2], new Vec3( 1.5f, 11.5f, 1.5f ) ), "Valid morph failed on v2" );

			for ( int i = 0; i < 3; i++ )
			{
				Require( !float.IsNaN( outPos[i].x ) && !float.IsInfinity( outPos[i].x ), $"NaN in outPos[{i}].x" );
				Require( !float.IsNaN( outPos[i].y ) && !float.IsInfinity( outPos[i].y ), $"NaN in outPos[{i}].y" );
				Require( !float.IsNaN( outPos[i].z ) && !float.IsInfinity( outPos[i].z ), $"NaN in outPos[{i}].z" );
			}
		} );

		// 9. weight bounds: clamp vs overdrive policy
		Check( "weight bounds clamp vs overdrive policy", () =>
		{
			var track = new RigTrack { Kind = RigTrackKind.Morph, Name = "over" };
			track.Keys.Add( CreateMorphKey( 0, 1.5f ) );

			// Clamped policy -> clamped to 1.0
			var wClamped = MorphEvaluator.EvaluateWeights( null, new[] { track }, null, 0f, null, null, MorphPolicy.Default );
			Require( MathF.Abs( wClamped["over"] - 1.0f ) < 1e-5f, "Default policy must clamp to 1.0" );

			// Overdrive policy -> keeps 1.5f
			var wOverdrive = MorphEvaluator.EvaluateWeights( null, new[] { track }, null, 0f, null, null, MorphPolicy.Overdrive );
			Require( MathF.Abs( wOverdrive["over"] - 1.5f ) < 1e-5f, "Overdrive policy must allow 1.5" );
		} );

		// 10. active target limit and overflow rejection
		Check( "active target limit and overflow rejection", () =>
		{
			var tracks = new[]
			{
				new RigTrack { Kind = RigTrackKind.Morph, Name = "m1", Keys = { CreateMorphKey( 0, 1f ) } },
				new RigTrack { Kind = RigTrackKind.Morph, Name = "m2", Keys = { CreateMorphKey( 0, 1f ) } },
				new RigTrack { Kind = RigTrackKind.Morph, Name = "m3", Keys = { CreateMorphKey( 0, 1f ) } }
			};

			var limitedPolicy = new MorphPolicy { MaxActiveTargets = 2 };

			bool threw = false;
			try
			{
				MorphEvaluator.EvaluateWeights( null, tracks, null, 0f, null, null, limitedPolicy );
			}
			catch ( InvalidOperationException ex )
			{
				threw = true;
				Require( ex.Message.Contains( "exceeded" ), "Exception should describe overflow" );
			}

			Require( threw, "Evaluator must throw InvalidOperationException when active targets exceed policy limit" );
		} );

		// 11. bind space morph before skinning order
		Check( "bind space morph before skinning order", () =>
		{
			var skeleton = new Skeleton();
			skeleton.Bones = new List<Bone> { new Bone( "bone0", -1, Xform.Identity, 1f ) };

			var bindWorld = new[] { Xform.Identity };
			var poseWorld = new[] { Quat.FromAxisAngle( new Vec3( 0, 0, 1 ), MathF.PI / 2f ).ToXform( Vec3.Zero ) };

			var basePos = new[] { new Vec3( 5, 0, 0 ) };
			var skinWeights = SkinWeights.AllTo( 1, 0 );

			var morphs = new RetainedMeshMorphs( 1, new[]
			{
				new MorphTarget( "stretch", new[] { new Vec3( 0, 2, 0 ) } )
			} );

			var weights = new Dictionary<string, float> { ["stretch"] = 1.0f };

			var skinned = MorphEvaluator.DeformAndSkin( basePos, weights, morphs, skinWeights, bindWorld, poseWorld );

			// Morph in bind space: (5, 0, 0) + (0, 2, 0) = (5, 2, 0)
			// Skin rotation 90 deg around Z: (5, 2, 0) rotated by 90 deg -> (-2, 5, 0)
			Require( VecEqual( skinned[0], new Vec3( -2, 5, 0 ) ),
				$"Morph before skinning should yield (-2, 5, 0) but got {skinned[0]}" );
		} );
	}
}
