using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Marionette.Desktop;

/// <summary>
/// Tests for godlike-proxy-pose-animator-grade-auto-rig, against the API in
/// kernel/Effigy/Rig/ProxyPose.cs, RigQuality.cs and ProxyPoseLibrary.cs:
/// BuildProxy / TransferPose / TransferDeltas / TransferError / CheckInputSize,
/// RigQuality.TryGrade / CheckRig / DetectRestPose / ExportNames, and
/// ProxyPoseLibrary.SeedPoses / MirrorPose.
///
/// NOTE ON THE PARALLEL FILE. tests/ProxyPoseRigGradeTests.cs (landed the same
/// morning by a second worker on this task) was rewritten ~10:54 to test THESE
/// APIs — ProxyPose.BuildProxy / TransferPose / TransferError / CheckInputSize and
/// RigQuality.TryGrade / CheckRig / DetectRestPose — after its parallel RigGrade.cs
/// was deleted: one animator-grade pass, not two. The two test files are
/// complementary, not competing: that file (group "rig", 13 checks) covers
/// idempotent additions and the PoseLibrary.Mirror / SeedHumanoid pose slice; this
/// file (group "proxy-pose-rig", 20 checks) pins the exact grade counts (64 DEF /
/// 69 total), the affine-field transfer, target clamping, weight copying,
/// no-mutation, rebase preservation, session determinism, and the
/// ProxyPoseLibrary.SeedPoses / MirrorPose pose slice. The one piece still
/// parallel is the pose library itself: PoseLibrary.Mirror / SeedHumanoid (added
/// to the shared PoseLibrary.cs by the sibling worker) vs ProxyPoseLibrary
/// (this task's file) — different seed conventions (their A-pose is the bind,
/// mine is the computed 45-degree pose; their mirror is a fixed x = 0 plane,
/// mine takes the plane from the rig). The integrator picks one seed set.
/// </summary>
internal static class ProxyPoseRigTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "proxy-pose-rig", name, test );

		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		void Near( float actual, float expected, float tol, string message )
		{
			if ( MathF.Abs( actual - expected ) > tol )
				throw new Exception( $"{message}: expected {expected}, got {actual}" );
		}

		void Throws( Action action )
		{
			try { action(); }
			catch ( Exception ) { return; }
			throw new Exception( "expected an error, got silent success" );
		}

		// A subdivided box, x in [-0.25, 0.25], y in [-0.15, 0.15], z in [0, 1.8]:
		// tall enough to read as humanoid to the auto-rigger (height 6x depth),
		// 12*div*div triangles. Faces are separate grids (seam vertices duplicated);
		// the decimator welds them.
		PolyMesh BipedMesh( int div )
		{
			var mesh = new PolyMesh();

			void Grid( Vec3 origin, Vec3 u, Vec3 v )
			{
				var start = mesh.Positions.Count;

				for ( var j = 0; j <= div; j++ )
					for ( var i = 0; i <= div; i++ )
						mesh.Positions.Add( origin + u * (i / (float)div) + v * (j / (float)div) );

				for ( var j = 0; j < div; j++ )
					for ( var i = 0; i < div; i++ )
					{
						var a = start + j * (div + 1) + i;
						var b = a + 1;
						var c = a + div + 1;
						var d = c + 1;
						mesh.Faces.Add( new Face( new[] { a, b, d } ) );
						mesh.Faces.Add( new Face( new[] { a, d, c } ) );
					}
			}

			Grid( new Vec3( -0.25f, -0.15f, 0 ), new Vec3( 0.5f, 0, 0 ), new Vec3( 0, 0.3f, 0 ) );
			Grid( new Vec3( -0.25f, -0.15f, 1.8f ), new Vec3( 0.5f, 0, 0 ), new Vec3( 0, 0.3f, 0 ) );
			Grid( new Vec3( -0.25f, -0.15f, 0 ), new Vec3( 0.5f, 0, 0 ), new Vec3( 0, 0, 1.8f ) );
			Grid( new Vec3( -0.25f, 0.15f, 0 ), new Vec3( 0.5f, 0, 0 ), new Vec3( 0, 0, 1.8f ) );
			Grid( new Vec3( -0.25f, -0.15f, 0 ), new Vec3( 0, 0.3f, 0 ), new Vec3( 0, 0, 1.8f ) );
			Grid( new Vec3( 0.25f, -0.15f, 0 ), new Vec3( 0, 0.3f, 0 ), new Vec3( 0, 0, 1.8f ) );
			return mesh;
		}

		AutoRigger.RigResult AutoRigged( PolyMesh mesh )
		{
			Require( AutoRigger.TryRig( mesh, out var rig, out var why ), why ?? "auto-rig failed" );
			Require( rig.Template == "humanoid", $"fixture should rig as humanoid, got {rig.Template}" );
			Require( rig.Skeleton.Count == 22, $"auto-rig has 22 bones, got {rig.Skeleton.Count}" );
			return rig;
		}

		RigQuality.AnimatorGradeResult Graded()
		{
			var mesh = BipedMesh( 2 );
			var rig = AutoRigged( mesh );
			Require( RigQuality.TryGrade( rig, mesh, out var graded, out var why ), why ?? "grade failed" );
			return graded;
		}

		Check( "small mesh copies with an identity map", () =>
		{
			var mesh = BipedMesh( 1 );
			var proxy = ProxyPose.BuildProxy( mesh );
			Require( proxy.IsIdentity, "a 12-triangle mesh already fits the band" );
			Require( proxy.ProxyTriangles == 12, $"12 triangles in, got {proxy.ProxyTriangles}" );
			Require( proxy.SourceTriangles == 12, "source count kept" );
			Require( proxy.TargetTriangles == 10000 && proxy.RequestedTriangles == 10000, "default target is 10000" );
			Require( proxy.MaxMapDistance < 1e-6f, "identity map has zero distance" );
			for ( var i = 0; i < proxy.SourceVertex.Length; i++ )
				Require( proxy.SourceVertex[i] == i, "identity map" );
		} );

		Check( "dense mesh decimates into the 5-20k band", () =>
		{
			var mesh = BipedMesh( 48 );
			Require( Decimate.TriangleCount( mesh ) == 27648, "fixture is 27648 triangles" );
			var proxy = ProxyPose.BuildProxy( mesh );
			Require( !proxy.IsIdentity, "dense mesh is decimated, not copied" );
			Require( proxy.ReachedTarget, "decimator reaches 10000 on a box" );
			Require( proxy.ProxyTriangles >= 5000 && proxy.ProxyTriangles <= 20000,
				$"proxy should be in the 5-20k band, got {proxy.ProxyTriangles}" );
			Require( proxy.WorstDecimateError >= 0f, "decimation error is measured" );
			Require( proxy.MaxMapDistance < 0.09f, $"map within 5% of the 1.8 height, got {proxy.MaxMapDistance}" );
			Require( proxy.MeanMapDistance <= proxy.MaxMapDistance, "mean cannot exceed max" );
			foreach ( var s in proxy.SourceVertex )
				Require( s >= 0 && s < mesh.Positions.Count, "transfer map in range" );
		} );

		Check( "identity transfer is exact", () =>
		{
			var mesh = BipedMesh( 1 );
			var proxy = ProxyPose.BuildProxy( mesh );
			var moved = ProxyPose.TransferPose( proxy, mesh.Positions, proxy.Mesh.Positions, proxy.Mesh.Positions );
			Require( moved.Length == mesh.Positions.Count, "one position per source vertex" );
			for ( var i = 0; i < moved.Length; i++ )
				Require( (moved[i] - mesh.Positions[i]).Length < 1e-4f, "identity transfer moves nothing" );
		} );

		Check( "linear delta field transfers exactly", () =>
		{
			var mesh = BipedMesh( 1 );
			var proxy = ProxyPose.BuildProxy( mesh );
			var deltas = new Vec3[proxy.Mesh.Positions.Count];
			for ( var i = 0; i < deltas.Length; i++ )
			{
				var p = proxy.Mesh.Positions[i];
				deltas[i] = new Vec3( 0.1f * p.x, 0.05f * p.z, -0.02f * p.y + 0.01f );
			}
			var moved = ProxyPose.TransferDeltas( proxy, mesh.Positions, deltas );
			for ( var i = 0; i < moved.Length; i++ )
			{
				var p = mesh.Positions[i];
				var expected = p + new Vec3( 0.1f * p.x, 0.05f * p.z, -0.02f * p.y + 0.01f );
				Require( (moved[i] - expected).Length < 1e-4f, "barycentric transfer reproduces an affine field" );
			}
		} );

		Check( "posed transfer error is within tolerance", () =>
		{
			var mesh = BipedMesh( 32 );
			var rig = AutoRigged( mesh );
			var proxy = ProxyPose.BuildProxy( mesh );
			var bindWorld = ProxyPose.BindWorlds( rig.Skeleton );
			var poseWorld = ProxyPose.BindWorlds( rig.Skeleton );
			Require( RigAxes.TryLateral( rig.Skeleton, out var lateral ), "fixture has a lateral axis" );
			ProxyPose.ApplyWorldRotation( poseWorld, rig.Skeleton, rig.Skeleton.IndexOf( "arm_upper_L" ), lateral, 20f * MathF.PI / 180f );
			var proxyWeights = proxy.ProxyWeights( rig.Weights );
			var posedProxy = SkinBinder.Deform( proxy.Mesh.Positions, proxyWeights, bindWorld, poseWorld );
			var moved = 0f;
			for ( var i = 0; i < posedProxy.Length; i++ )
				moved = MathF.Max( moved, (posedProxy[i] - proxy.Mesh.Positions[i]).Length );
			Require( moved > 1e-3f, "the 20-degree arm raise moves the proxy" );
			var error = ProxyPose.TransferError( mesh, rig.Weights, bindWorld, poseWorld, proxy, posedProxy );
			Require( ProxyPose.WithinTolerance( error, 1.8f ), $"transfer error max {error.Max:0.###} rms {error.Rms:0.###} exceeds tolerance" );
			Require( error.Max <= 0.05f * 1.8f && error.Rms <= 0.01f * 1.8f, "max 5% / rms 1% of the height" );
			Require( error.Rms <= error.Max && error.Mean <= error.Max, "rms/mean cannot exceed max" );
		} );

		Check( "input gate clamps targets and refuses bad meshes", () =>
		{
			var mesh = BipedMesh( 48 );
			var low = ProxyPose.BuildProxy( mesh, 100 );
			Require( low.RequestedTriangles == 100, "requested budget kept" );
			Require( low.TargetTriangles == 5000, "target clamps up to 5000" );
			var high = ProxyPose.BuildProxy( mesh, 999999 );
			Require( high.RequestedTriangles == 999999, "requested budget kept" );
			Require( high.TargetTriangles == 20000, "target clamps down to 20000" );
			Require( high.ProxyTriangles <= 20000, "proxy stays in the band" );
			var tiny = ProxyPose.CheckInputSize( 32 );
			Require( !tiny.OverLimit && tiny.SuggestedTarget == 5000, $"tiny mesh: no prompt, 5000 floor, got {tiny.SuggestedTarget}" );
			var (over, suggested) = ProxyPose.CheckInputSize( 150000 );
			Require( over && suggested == 100000, "150k triangles is over the 100k auto-rig limit" );
			var (over2, _) = ProxyPose.CheckInputSize( 50000 );
			Require( !over2, "50k triangles fits" );
			Throws( () => ProxyPose.BuildProxy( null ) );
			Throws( () => ProxyPose.BuildProxy( new PolyMesh() ) );
		} );

		Check( "proxy weights copy the mapped source weights", () =>
		{
			var mesh = BipedMesh( 2 );
			var rig = AutoRigged( mesh );
			mesh.Skin = rig.Weights;
			var proxy = ProxyPose.BuildProxy( mesh );
			var proxyWeights = proxy.ProxyWeights( rig.Weights );
			Require( proxyWeights.Count == proxy.Mesh.Positions.Count, "one weight set per proxy vertex" );
			for ( var v = 0; v < proxyWeights.Count; v++ )
			{
				var src = proxy.SourceVertex[v];
				Require( proxyWeights[v].Length == rig.Weights[src].Length, "same influence count" );
				for ( var k = 0; k < proxyWeights[v].Length; k++ )
				{
					Require( proxyWeights[v][k].Bone == rig.Weights[src][k].Bone, "same bone" );
					Near( proxyWeights[v][k].Weight, rig.Weights[src][k].Weight, 1e-6f, "same weight" );
				}
			}
		} );

		Check( "grade turns the auto-rig into 64 DEF + 5 controls", () =>
		{
			var graded = Graded();
			Require( graded.Report.DefBoneCount == 64, $"expected 64 DEF bones, got {graded.Report.DefBoneCount}" );
			Require( graded.Skeleton.Count == 69, $"expected 69 bones total, got {graded.Skeleton.Count}" );
			Require( graded.Report.ControlBoneCount == 5, $"expected 5 controls, got {graded.Report.ControlBoneCount}" );
			foreach ( var name in new[] { "finger_index_0_L", "finger_middle_2_R", "finger_thumb_0_L", "finger_thumb_2_R",
				"toe_L", "toe_R", "arm_upper_L_twist0", "arm_upper_L_twist1", "leg_lower_R_twist0", "leg_lower_R_twist1",
				"ctrl_hand_L", "ctrl_foot_R", "ctrl_head" } )
				Require( graded.Skeleton.IndexOf( name ) >= 0, $"missing {name}" );
			Require( graded.RestPose == "Unknown", $"auto-rig rest (arms hanging) is neither T nor A, got {graded.RestPose}" );
			var pelvis = graded.Skeleton.IndexOf( "pelvis" );
			Require( graded.Skeleton.HeadWorld( pelvis ).Length < 1e-4f, "root at origin" );
			Require( graded.ReweightMethod == "Heat", "default reweight is Heat" );
		} );

		Check( "Rig Check passes on the graded rig", () =>
		{
			var graded = Graded();
			Require( graded.Report.Passed, graded.Report.ToString() );
			Require( graded.Report.UnnormalizedVertices == 0, "no unnormalized vertices" );
			Require( graded.Report.ZeroWeightVertices == 0, "no unweighted vertices" );
			Require( graded.Report.WorstCapLoss <= 0.05f, "cap loss under 5%" );
			Require( graded.Report.NonDefWeightedVertices == 0, "no weights on control bones" );
			Require( graded.Report.OverInfluenceVertices == 0, "pruned to 4 influences" );
			Require( graded.Report.AsymmetricPairs.Count == 0, "no asymmetric pairs" );
		} );

		Check( "Rig Check catches corrupted weights", () =>
		{
			var graded = Graded();
			var weights = graded.Weights.Clone();
			var w0 = weights[0];
			var doubled = new BoneWeight[w0.Length];
			for ( var i = 0; i < w0.Length; i++ )
				doubled[i] = new BoneWeight( w0[i].Bone, w0[i].Weight * 2f );
			weights[0] = doubled;
			weights[1] = Array.Empty<BoneWeight>();
			var five = new BoneWeight[5];
			for ( var i = 0; i < 5; i++ )
				five[i] = new BoneWeight( i, 0.2f );
			weights[2] = five;
			var report = RigQuality.CheckRig( graded.Skeleton, weights );
			Require( report.UnnormalizedVertices >= 1, "unnormalized caught" );
			Require( report.ZeroWeightVertices >= 1, "zero-weight caught" );
			Require( report.OverInfluenceVertices >= 1, "5-influence caught" );
			Require( report.WorstCapLoss >= 0.19f, $"fifth influence loses ~0.2, got {report.WorstCapLoss}" );
			Require( !report.Passed, "corrupted rig fails the gate" );
		} );

		Check( "bounds rig is refused", () =>
		{
			var bounds = new Skeleton();
			bounds.AddBoneFromPoints( "Root", -1, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ) );
			bounds.AddBoneFromPoints( "Tip", 0, new Vec3( 0, 0, 1 ), new Vec3( 0, 0, 2 ) );
			var result = new AutoRigger.RigResult { Skeleton = bounds, Weights = new SkinWeights( 1 ), Template = "bounds" };
			var mesh = BipedMesh( 1 );
			var ok = RigQuality.TryGrade( result, mesh, out _, out var why );
			Require( !ok, "bounds rig must be refused" );
			Require( why is not null && why.Contains( "humanoid" ), $"refusal should name the humanoid template, got: {why}" );
		} );

		Check( "grading does not mutate the input", () =>
		{
			var mesh = BipedMesh( 2 );
			var rig = AutoRigged( mesh );
			var beforePositions = mesh.Positions.ToArray();
			var beforeBones = rig.Skeleton.Count;
			var beforeWeights = rig.Weights.Count;
			var ok = RigQuality.TryGrade( rig, mesh, out _, out var why, rebaseRoot: false );
			Require( ok, why ?? "grade failed" );
			Require( rig.Skeleton.Count == beforeBones, "input skeleton untouched" );
			Require( rig.Weights.Count == beforeWeights, "input weights untouched" );
			for ( var i = 0; i < mesh.Positions.Count; i++ )
				Require( (mesh.Positions[i] - beforePositions[i]).Length < 1e-6f, "mesh untouched when rebase is off" );
		} );

		Check( "symmetry holds after grading", () =>
		{
			var graded = Graded();
			var (maxDev, pairs) = RigQuality.CheckSymmetry( graded.Skeleton );
			Require( maxDev < 1e-3f, $"bones symmetric, worst deviation {maxDev}" );
			Require( pairs.Count == 0, "no asymmetric pairs" );
			float Total( string name )
			{
				var b = graded.Skeleton.IndexOf( name );
				var total = 0f;
				for ( var v = 0; v < graded.Weights.Count; v++ )
					foreach ( var bw in graded.Weights[v] )
						if ( bw.Bone == b ) total += bw.Weight;
				return total;
			}
			var l = Total( "arm_upper_L" );
			var r = Total( "arm_upper_R" );
			Require( MathF.Abs( l - r ) <= 0.02f * MathF.Max( 1f, l + r ), $"arm weights symmetric: L {l:0.###} R {r:0.###}" );
		} );

		Check( "export names use .L/.R", () =>
		{
			var graded = Graded();
			var names = RigQuality.ExportNames( graded.Skeleton );
			Require( names["arm_upper_L"] == "arm_upper.L", "arm_upper_L exports as arm_upper.L" );
			Require( names["finger_index_0_L"] == "finger_index_0.L", "finger exports with .L" );
			Require( names["toe_R"] == "toe.R", "toe_R exports as toe.R" );
			Require( names["spine_0"] == "spine_0", "centre bones unchanged" );
			Require( names["arm_upper_L_twist0"] == "arm_upper.L_twist0", "twist keeps its station suffix" );
		} );

		Check( "rebase preserves skinning", () =>
		{
			var mesh = BipedMesh( 2 );
			var rig = AutoRigged( mesh );
			var before = mesh.Positions.ToArray();
			var ok = RigQuality.TryGrade( rig, mesh, out var graded, out var why );
			Require( ok, why ?? "grade failed" );
			var offset = graded.RootOffset;
			Require( offset.Length > 0.01f, "the pelvis was not at the origin" );
			for ( var i = 0; i < mesh.Positions.Count; i++ )
				Require( (mesh.Positions[i] - (before[i] + offset)).Length < 1e-5f, "mesh shifted by the root offset" );
			var bindWorld = ProxyPose.BindWorlds( graded.Skeleton );
			var deformed = SkinBinder.Deform( mesh.Positions, graded.Weights, bindWorld, bindWorld );
			for ( var i = 0; i < deformed.Length; i++ )
				Require( (deformed[i] - mesh.Positions[i]).Length < 1e-4f, "identity pose is the rebased mesh" );
		} );

		Check( "seed library has T, A and Idle poses", () =>
		{
			var graded = Graded();
			var lib = ProxyPoseLibrary.SeedPoses( graded.Skeleton );
			Require( lib.HasPose( "T-Pose" ) && lib.HasPose( "A-Pose" ) && lib.HasPose( "Idle" ), "three seeds" );
		} );

		Check( "T-Pose is the bind pose", () =>
		{
			var graded = Graded();
			var lib = ProxyPoseLibrary.SeedPoses( graded.Skeleton );
			var tPose = lib.GetPose( "T-Pose" );
			Require( tPose is not null, "T-Pose stored" );
			var worlds = ProxyPose.PoseWorldFromLocals( graded.Skeleton, tPose );
			for ( var i = 0; i < graded.Skeleton.Count; i++ )
				Require( (worlds[i].Origin - graded.Skeleton.WorldBind( i ).Origin).Length < 1e-4f, "T-Pose reproduces the bind" );
		} );

		Check( "A-Pose is detected as A", () =>
		{
			var graded = Graded();
			var lib = ProxyPoseLibrary.SeedPoses( graded.Skeleton );
			var aPose = lib.GetPose( "A-Pose" );
			Require( aPose is not null, "A-Pose stored" );
			var worlds = ProxyPose.PoseWorldFromLocals( graded.Skeleton, aPose );
			var (pose, angle) = RigQuality.DetectRestPose( graded.Skeleton, worlds );
			Require( pose == "A", $"A-Pose should detect as A, got {pose} at {angle:0.0} degrees" );
			Require( MathF.Abs( angle - 45f ) < 2f, $"A-pose arms at 45 degrees, got {angle:0.0}" );
		} );

		Check( "mirror is an involution and fixes symmetric poses", () =>
		{
			var graded = Graded();
			var rig = graded.Skeleton;
			var pose = new Dictionary<string, RigPose>( StringComparer.OrdinalIgnoreCase )
			{
				["arm_upper_L"] = new RigPose( new Vec3( 0.1f, 0.2f, 0.3f ), Quat.FromAxisAngle( new Vec3( 0, 0, 1 ), 0.5f ), Vec3.One ),
				["spine_1"] = new RigPose( new Vec3( 0, 0.1f, 0 ), Quat.FromAxisAngle( new Vec3( 1, 0, 0 ), 0.3f ), Vec3.One ),
			};
			var mirrored = ProxyPoseLibrary.MirrorPose( pose, rig );
			Require( !mirrored.ContainsKey( "arm_upper_L" ), "one-sided pose mirrors onto the partner side only" );
			var armR = mirrored["arm_upper_R"];
			var src = pose["arm_upper_L"];
			Near( armR.Position.x, -0.1f, 1e-4f, "mirrored position x" );
			Near( armR.Position.y, 0.2f, 1e-4f, "mirrored position y" );
			Near( armR.Position.z, 0.3f, 1e-4f, "mirrored position z" );
			Near( armR.Rotation.x, src.Rotation.x, 1e-4f, "mirrored rotation x" );
			Near( armR.Rotation.y, -src.Rotation.y, 1e-4f, "mirrored rotation y" );
			Near( armR.Rotation.z, -src.Rotation.z, 1e-4f, "mirrored rotation z" );
			Near( armR.Rotation.w, src.Rotation.w, 1e-4f, "mirrored rotation w" );
			var twice = ProxyPoseLibrary.MirrorPose( mirrored, rig );
			Near( twice["arm_upper_L"].Position.x, 0.1f, 1e-4f, "mirroring twice returns the pose" );
			Near( twice["arm_upper_L"].Position.y, 0.2f, 1e-4f, "mirroring twice returns the pose" );
			Near( twice["arm_upper_L"].Position.z, 0.3f, 1e-4f, "mirroring twice returns the pose" );
			var lib = ProxyPoseLibrary.SeedPoses( rig );
			var tPose = lib.GetPose( "T-Pose" );
			Require( tPose is not null, "T-Pose stored" );
			var mirroredT = ProxyPoseLibrary.MirrorPose( tPose, rig );
			foreach ( var (name, local) in tPose )
			{
				var m = mirroredT[name];
				Require( (m.Position - local.Position).Length < 1e-4f, $"T-Pose {name} position fixed by mirror" );
				var dot = MathF.Abs( m.Rotation.x * local.Rotation.x + m.Rotation.y * local.Rotation.y
					+ m.Rotation.z * local.Rotation.z + m.Rotation.w * local.Rotation.w );
				Require( MathF.Abs( dot - 1f ) < 1e-4f, $"T-Pose {name} rotation fixed by mirror" );
			}
		} );

		Check( "session transfers a pose without rebuilding the proxy", () =>
		{
			var mesh = BipedMesh( 32 );
			var rig = AutoRigged( mesh );
			var proxy = ProxyPose.BuildProxy( mesh );
			var session = new ProxyPoseSession( proxy, rig.Weights, rig.Skeleton, mesh.Positions );
			var poseWorld = ProxyPose.BindWorlds( rig.Skeleton );
			Require( RigAxes.TryLateral( rig.Skeleton, out var lateral ), "fixture has a lateral axis" );
			ProxyPose.ApplyWorldRotation( poseWorld, rig.Skeleton, rig.Skeleton.IndexOf( "arm_upper_L" ), lateral, 20f * MathF.PI / 180f );
			session.PoseWorld = poseWorld;
			session.Transfer( mesh.Positions );
			Require( session.PosedFullRes.Length == mesh.Positions.Count, "full-res buffer covers the mesh" );
			Require( session.PosedProxy.Length == proxy.Mesh.Positions.Count, "proxy buffer covers the proxy" );
			var moved = 0f;
			for ( var i = 0; i < session.PosedFullRes.Length; i++ )
				moved = MathF.Max( moved, (session.PosedFullRes[i] - mesh.Positions[i]).Length );
			Require( moved > 1e-3f, "the session pose moves the mesh" );
			var first = session.PosedFullRes.ToArray();
			session.Transfer( mesh.Positions );
			for ( var i = 0; i < first.Length; i++ )
				Require( (session.PosedFullRes[i] - first[i]).Length < 1e-6f, "repeat transfer is identical" );
		} );
	}
}
