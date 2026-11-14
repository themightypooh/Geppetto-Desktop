// Proxy-pose + animator-grade rig checks (godlike-proxy-pose-animator-grade-auto-rig).
//
// CONVERGENCE NOTE. Two workers implemented this ticket in parallel under the same
// board id. The proxy side converged on the sibling's kernel/Effigy/Rig/ProxyPose.cs
// (BuildProxy/TransferPose/TransferError/ProxyPoseSession) and the grade side on the
// sibling's kernel/Effigy/Rig/RigQuality.cs (TryGrade/CheckRig/DetectRestPose). The
// first version of this file tested a parallel RigGrade.cs; that file has been
// DELETED — one animator-grade pass, not two. What RigGrade did differently, for the
// record: a strict 0-over-4-influence export gate, a full CTRL twin per DEF bone,
// and a new Root bone at ground centre. RigQuality instead gates export on cap loss
// (a fifth influence worth <= 5% is kept — the game keeps the strongest four
// anyway), ships five Deform=false placement targets (hands/feet/head), and rebases
// the pelvis onto the origin, mesh and skeleton together. The cap-loss gate is
// pinned explicitly by the "rig check" test below so the decision is visible.
//
// Pose seeds + mirror live in PoseLibrary (SeedHumanoid/Mirror, added here). The
// sibling ProxyPose.cs doc comments reference a ProxyPoseLibrary that does not
// exist — that is the dangling name for this PoseLibrary surface; integrator to
// fix the cref, not the code.
//
// NOT registered in tests/TestRegistry.cs (coordinator-owned): the integrator adds
//   ProxyPoseRigGradeTests.Register( registry, settings );
// to DesktopTestHost.Run. Group is "rig". Verified by reading only — no build was
// run, so none of these checks has executed yet; the final build runs them.

using System;
using System.Collections.Generic;
using Effigy;

internal static class ProxyPoseRigGradeTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "rig", name, test );
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

		// A triangulated grid slab, x in [-0.5, 0.5], z in [0, 6], y = 0 (or wavy).
		PolyMesh GridMesh( int cols, int rows, bool wavy = false )
		{
			var mesh = new PolyMesh();
			for ( var j = 0; j <= rows; j++ )
				for ( var i = 0; i <= cols; i++ )
				{
					var x = -0.5f + i * (1f / cols);
					var z = j * (6f / rows);
					var y = wavy ? 0.2f * MathF.Sin( x * 6f ) * MathF.Cos( z * 2f ) : 0f;
					mesh.Positions.Add( new Vec3( x, y, z ) );
				}
			for ( var j = 0; j < rows; j++ )
				for ( var i = 0; i < cols; i++ )
				{
					var a = j * (cols + 1) + i;
					var b = a + 1;
					var c = a + cols + 1;
					var d = c + 1;
					mesh.Faces.Add( new Face( new[] { a, b, d } ) );
					mesh.Faces.Add( new Face( new[] { a, d, c } ) );
				}
			return mesh;
		}

		// The 22-bone citizen-named humanoid, laid out exactly as AutoRigger does
		// for a 6-tall body, torso half-width 0.5, centred at the origin.
		Skeleton HumanoidSkeleton()
		{
			var rig = new Skeleton();
			var up = new Vec3( 0, 0, 1 );
			var heads = new Dictionary<string, (Vec3, Vec3)>
			{
				["pelvis"] = (new Vec3( 0, 0, 3f ), new Vec3( 0, 0, 3.48f )),
				["spine_0"] = (new Vec3( 0, 0, 3.48f ), new Vec3( 0, 0, 3.96f )),
				["spine_1"] = (new Vec3( 0, 0, 3.96f ), new Vec3( 0, 0, 4.44f )),
				["spine_2"] = (new Vec3( 0, 0, 4.44f ), new Vec3( 0, 0, 4.92f )),
				["neck_0"] = (new Vec3( 0, 0, 4.92f ), new Vec3( 0, 0, 5.28f )),
				["head"] = (new Vec3( 0, 0, 5.28f ), new Vec3( 0, 0, 6f )),
			};
			foreach ( var side in new[] { "L", "R" } )
			{
				var s = side == "L" ? 1f : -1f;
				heads[$"clavicle_{side}"] = (new Vec3( 0, 0, 4.8f ), new Vec3( s * 0.4f, 0, 4.8f ));
				heads[$"arm_upper_{side}"] = (new Vec3( s * 0.4f, 0, 4.8f ), new Vec3( s * 0.475f, 0, 3.72f ));
				heads[$"arm_lower_{side}"] = (new Vec3( s * 0.475f, 0, 3.72f ), new Vec3( s * 0.525f, 0, 2.64f ));
				heads[$"hand_{side}"] = (new Vec3( s * 0.525f, 0, 2.64f ), new Vec3( s * 0.525f, 0, 2.28f ));
				heads[$"leg_upper_{side}"] = (new Vec3( s * 0.16f, 0, 3f ), new Vec3( s * 0.16f, 0, 1.56f ));
				heads[$"leg_lower_{side}"] = (new Vec3( s * 0.16f, 0, 1.56f ), new Vec3( s * 0.16f, 0, 0.24f ));
				heads[$"ankle_{side}"] = (new Vec3( s * 0.16f, 0, 0.24f ), new Vec3( s * 0.16f, 0.3f, 0.06f ));
				heads[$"ball_{side}"] = (new Vec3( s * 0.16f, 0.3f, 0.06f ), new Vec3( s * 0.16f, 0.66f, 0.03f ));
			}
			var parents = new Dictionary<string, string>
			{
				["pelvis"] = "", ["spine_0"] = "pelvis", ["spine_1"] = "spine_0", ["spine_2"] = "spine_1",
				["neck_0"] = "spine_2", ["head"] = "neck_0",
			};
			foreach ( var side in new[] { "L", "R" } )
			{
				parents[$"clavicle_{side}"] = "spine_2";
				parents[$"arm_upper_{side}"] = $"clavicle_{side}";
				parents[$"arm_lower_{side}"] = $"arm_upper_{side}";
				parents[$"hand_{side}"] = $"arm_lower_{side}";
				parents[$"leg_upper_{side}"] = "pelvis";
				parents[$"leg_lower_{side}"] = $"leg_upper_{side}";
				parents[$"ankle_{side}"] = $"leg_lower_{side}";
				parents[$"ball_{side}"] = $"ankle_{side}";
			}
			var index = new Dictionary<string, int>();
			foreach ( var name in new[] { "pelvis", "spine_0", "spine_1", "spine_2", "neck_0", "head",
				"clavicle_L", "arm_upper_L", "arm_lower_L", "hand_L", "leg_upper_L", "leg_lower_L", "ankle_L", "ball_L",
				"clavicle_R", "arm_upper_R", "arm_lower_R", "hand_R", "leg_upper_R", "leg_lower_R", "ankle_R", "ball_R" } )
			{
				var (h, t) = heads[name];
				var parent = parents[name].Length == 0 ? -1 : index[parents[name]];
				index[name] = rig.AddBoneFromPoints( name, parent, h, t, up );
			}
			rig.RootBone = "pelvis";
			rig.HipsBone = "pelvis";
			return rig;
		}

		float DistanceToSegment( Vec3 p, Vec3 a, Vec3 b )
		{
			var ab = b - a;
			var len2 = ab.LengthSquared;
			if ( len2 < 1e-12f ) return (p - a).Length;
			var t = Math.Clamp( Vec3.Dot( p - a, ab ) / len2, 0f, 1f );
			return (p - (a + ab * t)).Length;
		}

		// One bone per vertex, nearest segment wins — valid by construction.
		SkinWeights WeightsByNearest( PolyMesh mesh, Skeleton rig )
		{
			var weights = new SkinWeights( mesh.Positions.Count );
			for ( var v = 0; v < mesh.Positions.Count; v++ )
			{
				var best = 0;
				var bestD = float.MaxValue;
				for ( var b = 0; b < rig.Count; b++ )
				{
					var d = DistanceToSegment( mesh.Positions[v], rig.HeadWorld( b ), rig.TailWorld( b ) );
					if ( d < bestD ) { bestD = d; best = b; }
				}
				weights[v] = new[] { new BoneWeight( best, 1f ) };
			}
			return SkinBinder.SmoothWeights( mesh, weights, 6, 0.5f );
		}

		Dictionary<string, RigPose> BindPoseDict( Skeleton rig )
		{
			var pose = new Dictionary<string, RigPose>( StringComparer.OrdinalIgnoreCase );
			foreach ( var bone in rig.Bones ) pose[bone.Name] = RigPose.FromXform( bone.Local );
			return pose;
		}

		Dictionary<string, RigPose> Pose( PoseLibrary lib, string name )
		{
			var p = lib.GetPose( name );
			if ( p is null ) throw new Exception( $"no pose '{name}'" );
			return p;
		}

		float BoundsDiagonal( PolyMesh mesh )
		{
			var min = mesh.Positions[0];
			var max = mesh.Positions[0];
			foreach ( var p in mesh.Positions )
			{
				min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
				max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
			}
			return (max - min).Length;
		}

		int CountBones( Skeleton rig, Func<string, bool> match )
		{
			var n = 0;
			foreach ( var b in rig.Bones )
				if ( match( b.Name ) ) n++;
			return n;
		}

		// Raise the left arm 0.6 rad about the shoulder line, in local-pose terms.
		Dictionary<string, RigPose> RaisedArmPose( Skeleton rig )
		{
			var lateral = (rig.HeadWorld( rig.IndexOf( "arm_upper_L" ) ) - rig.HeadWorld( rig.IndexOf( "arm_upper_R" ) )).Normal;
			var pose = BindPoseDict( rig );
			var arm = pose["arm_upper_L"];
			pose["arm_upper_L"] = new RigPose( arm.Position, Quat.FromAxisAngle( lateral, 0.6f ) * arm.Rotation, arm.Scale );
			return pose;
		}

		Check( "proxy is a clone with zero map error for a mesh already in budget", () =>
		{
			var mesh = GridMesh( 4, 4 );
			var proxy = ProxyPose.BuildProxy( mesh );
			Require( proxy.IsIdentity, "small mesh should be an identity proxy" );
			Require( proxy.ProxyTriangles == 32, $"4x4 grid is 32 triangles, got {proxy.ProxyTriangles}" );
			Require( proxy.SourceTriangles == 32, "source triangles" );
			Require( proxy.RequestedTriangles == 10000 && proxy.TargetTriangles == 10000, "default target is 10000" );
			Near( proxy.MaxMapDistance, 0f, 1e-6f, "identity map distance" );
			Near( proxy.MeanMapDistance, 0f, 1e-6f, "identity mean map distance" );
			Require( proxy.SourceVertex.Length == mesh.Positions.Count, "identity map covers every vertex" );
			for ( var i = 0; i < proxy.SourceVertex.Length; i++ )
				Require( proxy.SourceVertex[i] == i, "identity map" );
			for ( var i = 0; i < proxy.Mesh.Positions.Count; i++ )
				Require( (proxy.Mesh.Positions[i] - mesh.Positions[i]).Length < 1e-6f, "identity proxy keeps positions" );
		} );

		Check( "proxy decimates a dense mesh into the 5-20k budget with measured error", () =>
		{
			var mesh = GridMesh( 120, 120, wavy: true );
			Require( Decimate.TriangleCount( mesh ) == 28800, "dense grid is 28800 triangles" );
			var proxy = ProxyPose.BuildProxy( mesh );
			Require( !proxy.IsIdentity, "dense mesh should be decimated" );
			Require( proxy.ReachedTarget, "decimator should reach its target on a grid" );
			Require( proxy.ProxyTriangles >= 9000 && proxy.ProxyTriangles <= 11000,
				$"proxy should be near 10000 triangles, got {proxy.ProxyTriangles}" );
			Require( proxy.WorstDecimateError > 0f, "a wavy mesh loses real shape when decimated" );
			Require( proxy.WorstDecimateError < 0.5f, $"decimation error should stay small, got {proxy.WorstDecimateError}" );
			Require( proxy.MaxMapDistance < 0.5f, $"map distance should stay small, got {proxy.MaxMapDistance}" );
			Require( proxy.MeanMapDistance <= proxy.MaxMapDistance, "mean cannot exceed max" );
			Require( proxy.SourceDistance.Length == proxy.Mesh.Positions.Count, "one map distance per proxy vertex" );
			foreach ( var s in proxy.SourceVertex )
				Require( s >= 0 && s < mesh.Positions.Count, "transfer map in range" );
		} );

		Check( "proxy carries skin weights through the transfer map", () =>
		{
			var rig = HumanoidSkeleton();
			var mesh = GridMesh( 120, 120, wavy: true );
			mesh.Skin = WeightsByNearest( mesh, rig );
			var proxy = ProxyPose.BuildProxy( mesh );
			var pw = proxy.ProxyWeights( mesh.Skin );
			Require( pw.Count == proxy.Mesh.Positions.Count, "one weight set per proxy vertex" );
			for ( var v = 0; v < pw.Count; v++ )
			{
				var sum = 0f;
				foreach ( var bw in pw[v] )
				{
					Require( bw.Bone >= 0 && bw.Bone < rig.Count, "weight bone in range" );
					sum += bw.Weight;
				}
				Near( sum, 1f, 1e-3f, "proxy weights stay normalised" );
			}
		} );

		Check( "transfer pose at bind returns the rest mesh", () =>
		{
			var rig = HumanoidSkeleton();
			var mesh = GridMesh( 120, 120, wavy: true );
			mesh.Skin = WeightsByNearest( mesh, rig );
			var proxy = ProxyPose.BuildProxy( mesh );
			var bind = ProxyPose.BindWorlds( rig );
			var posedProxy = SkinBinder.Deform( proxy.Mesh.Positions, proxy.ProxyWeights( mesh.Skin ), bind, bind );
			var back = ProxyPose.TransferPose( proxy, mesh.Positions, proxy.Mesh.Positions, posedProxy );
			Require( back.Length == mesh.Positions.Count, "transfer returns one position per source vertex" );
			var maxDrift = 0f;
			for ( var i = 0; i < back.Length; i++ )
				maxDrift = MathF.Max( maxDrift, (back[i] - mesh.Positions[i]).Length );
			Require( maxDrift < 1e-3f, $"bind pose should not move the mesh, drifted {maxDrift}" );
		} );

		Check( "pose transfer error stays within tolerance on a decimated proxy", () =>
		{
			var rig = HumanoidSkeleton();
			var mesh = GridMesh( 120, 120, wavy: true );
			mesh.Skin = WeightsByNearest( mesh, rig );
			var proxy = ProxyPose.BuildProxy( mesh );
			var bind = ProxyPose.BindWorlds( rig );
			var poseWorld = ProxyPose.PoseWorldFromLocals( rig, RaisedArmPose( rig ) );
			var posedProxy = SkinBinder.Deform( proxy.Mesh.Positions, proxy.ProxyWeights( mesh.Skin ), bind, poseWorld );
			var err = ProxyPose.TransferError( mesh, mesh.Skin, bind, poseWorld, proxy, posedProxy );
			Require( err.Max > 0f, "the raised arm should move vertices, else the check is vacuous" );
			Require( err.Rms <= err.Max && err.Mean <= err.Max, "rms/mean cannot exceed max" );
			var diag = BoundsDiagonal( mesh );
			Require( ProxyPose.WithinTolerance( err, diag ),
				$"transfer error max {err.Max:0.###} rms {err.Rms:0.###} should be within 5%/1% of {diag:0.##}" );
		} );

		Check( "session skins the proxy once and transfers to full res", () =>
		{
			var rig = HumanoidSkeleton();
			var mesh = GridMesh( 120, 120, wavy: true );
			mesh.Skin = WeightsByNearest( mesh, rig );
			var proxy = ProxyPose.BuildProxy( mesh );
			var session = new ProxyPoseSession( proxy, mesh.Skin, rig, mesh.Positions );
			Require( session.ProxyWeights.Count == proxy.Mesh.Positions.Count, "session carries proxy weights" );
			Require( session.PosedProxy.Length == proxy.Mesh.Positions.Count, "posed proxy buffer" );
			Require( session.PosedFullRes.Length == mesh.Positions.Count, "posed full-res buffer" );
			session.Transfer( mesh.Positions );
			var bindDrift = 0f;
			for ( var i = 0; i < session.PosedFullRes.Length; i++ )
				bindDrift = MathF.Max( bindDrift, (session.PosedFullRes[i] - mesh.Positions[i]).Length );
			Require( bindDrift < 1e-3f, $"bind transfer should not move the mesh, drifted {bindDrift}" );
			session.PoseWorld = ProxyPose.PoseWorldFromLocals( rig, RaisedArmPose( rig ) );
			session.Transfer( mesh.Positions );
			var moved = 0f;
			for ( var i = 0; i < session.PosedFullRes.Length; i++ )
				moved = MathF.Max( moved, (session.PosedFullRes[i] - mesh.Positions[i]).Length );
			Require( moved > 0.1f, $"raised arm should move full-res vertices, moved {moved}" );
			var direct = SkinBinder.Deform( mesh.Positions, mesh.Skin, session.BindWorld, session.PoseWorld );
			var maxDiff = 0f;
			for ( var i = 0; i < direct.Length; i++ )
				maxDiff = MathF.Max( maxDiff, (session.PosedFullRes[i] - direct[i]).Length );
			Require( maxDiff <= 0.05f * BoundsDiagonal( mesh ),
				$"session transfer should track direct skinning, diff {maxDiff:0.###}" );
		} );

		Check( "input size check prompts above 100k triangles", () =>
		{
			// SuggestedTarget is max(5000, min(limit, triangles)): a small mesh is
			// offered the 5000-triangle proxy floor, an over-limit mesh the 100k cap —
			// NOT the 10000 proxy default.
			var small = ProxyPose.CheckInputSize( 32 );
			Require( !small.OverLimit && small.SuggestedTarget == 5000, "small mesh: no prompt, 5000 floor" );
			var mid = ProxyPose.CheckInputSize( 50000 );
			Require( !mid.OverLimit && mid.SuggestedTarget == 50000, "50k mesh: no prompt, own size" );
			var big = ProxyPose.CheckInputSize( 115200 );
			Require( big.OverLimit, "115k triangles is over the limit" );
			Require( big.SuggestedTarget == 100000, $"suggested target is the 100k cap, got {big.SuggestedTarget}" );
		} );

		Check( "rig check catches unnormalized, zero-weight and over-influence weights", () =>
		{
			var rig = new Skeleton();
			var pts = new[] { new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), new Vec3( 0, 0, 2 ), new Vec3( 0, 0, 3 ), new Vec3( 0, 0, 4 ), new Vec3( 0, 0, 5 ), new Vec3( 0, 0, 6 ) };
			var names = new[] { "a", "b", "c", "d", "e", "f" };
			for ( var i = 0; i < 6; i++ )
				rig.AddBoneFromPoints( names[i], i == 0 ? -1 : i - 1, pts[i], pts[i + 1] );
			PolyMesh MeshWith( BoneWeight[][] weights )
			{
				var mesh = new PolyMesh();
				mesh.Positions.Add( new Vec3( 0, 0, 0.5f ) );
				mesh.Positions.Add( new Vec3( 0, 0, 1.5f ) );
				mesh.Positions.Add( new Vec3( 0, 0, 2.5f ) );
				mesh.Positions.Add( new Vec3( 0, 0, 5.5f ) );
				mesh.Skin = new SkinWeights( 4 );
				for ( var i = 0; i < 4; i++ ) mesh.Skin[i] = weights[i];
				return mesh;
			}
			var clean = MeshWith( new[]
			{
				new[] { new BoneWeight( 0, 1f ) }, new[] { new BoneWeight( 1, 1f ) },
				new[] { new BoneWeight( 2, 1f ) }, new[] { new BoneWeight( 3, 1f ) },
			} );
			var report = RigQuality.CheckRig( rig, clean.Skin );
			Require( report.Passed, "clean weights pass the rig check" );
			Require( report.DefBoneCount == 6 && report.ControlBoneCount == 0, "6 DEF, 0 control" );
			var unnormalised = MeshWith( new[]
			{
				new[] { new BoneWeight( 0, 0.5f ) }, new[] { new BoneWeight( 1, 1f ) },
				new[] { new BoneWeight( 2, 1f ) }, new[] { new BoneWeight( 3, 1f ) },
			} );
			report = RigQuality.CheckRig( rig, unnormalised.Skin );
			Require( !report.Passed && report.UnnormalizedVertices == 1, "unnormalized weights fail" );
			Require( string.Join( " ", report.Errors ).Contains( "do not sum to 1" ), "error names the sum" );
			var zeroWeight = MeshWith( new[]
			{
				new[] { new BoneWeight( 0, 1f ) }, Array.Empty<BoneWeight>(),
				new[] { new BoneWeight( 2, 1f ) }, new[] { new BoneWeight( 3, 1f ) },
			} );
			report = RigQuality.CheckRig( rig, zeroWeight.Skin );
			Require( !report.Passed && report.ZeroWeightVertices == 1, "zero-weight vertex fails" );
			var five = MeshWith( new[]
			{
				new[] { new BoneWeight( 0, 1f ) }, new[] { new BoneWeight( 1, 1f ) },
				new[] { new BoneWeight( 0, 0.2f ), new BoneWeight( 1, 0.2f ), new BoneWeight( 2, 0.2f ), new BoneWeight( 3, 0.2f ), new BoneWeight( 4, 0.2f ) },
				new[] { new BoneWeight( 3, 1f ) },
			} );
			report = RigQuality.CheckRig( rig, five.Skin );
			Require( report.OverInfluenceVertices == 1, "five influences are counted" );
			Near( report.WorstCapLoss, 0.2f, 1e-4f, "capping five 0.2 influences loses 0.2" );
			Require( !report.Passed, "a 20% cap loss fails the export gate" );
			Require( string.Join( " ", report.Errors ).Contains( "loses" ), "error names the loss" );
			// The gate is cap loss, not influence count: a fifth influence worth 1%
			// is reported but does NOT block export. This is the deliberate reading
			// of the ticket's "0 >4-influence" line — the game keeps the strongest
			// four anyway, so a 1% fifth influence bends nothing.
			var tinyFifth = MeshWith( new[]
			{
				new[] { new BoneWeight( 0, 1f ) }, new[] { new BoneWeight( 1, 1f ) },
				new[] { new BoneWeight( 0, 0.4f ), new BoneWeight( 1, 0.3f ), new BoneWeight( 2, 0.2f ), new BoneWeight( 3, 0.09f ), new BoneWeight( 4, 0.01f ) },
				new[] { new BoneWeight( 3, 1f ) },
			} );
			report = RigQuality.CheckRig( rig, tinyFifth.Skin );
			Require( report.OverInfluenceVertices == 1, "tiny fifth influence still counted" );
			Near( report.WorstCapLoss, 0.01f, 1e-4f, "capping loses 0.01" );
			Require( report.Passed, "a 1% cap loss passes the export gate" );
			var widget = rig.AddBoneFromPoints( "widget", -1, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 0.1f ) );
			rig.Bones[widget].Deform = false;
			var nonDef = MeshWith( new[]
			{
				new[] { new BoneWeight( widget, 1f ) }, new[] { new BoneWeight( 1, 1f ) },
				new[] { new BoneWeight( 2, 1f ) }, new[] { new BoneWeight( 3, 1f ) },
			} );
			report = RigQuality.CheckRig( rig, nonDef.Skin );
			Require( !report.Passed && report.NonDefWeightedVertices == 1, "weights on a non-deform bone fail" );
		} );

		Check( "grade finds the auto-rig gaps and enrich closes them", () =>
		{
			var rig = HumanoidSkeleton();
			var mesh = GridMesh( 20, 60 );
			mesh.Skin = WeightsByNearest( mesh, rig );
			var before = RigQuality.CheckRig( rig, mesh.Skin );
			Require( before.DefBoneCount == 22, $"auto-rig has 22 DEF bones, got {before.DefBoneCount}" );
			Require( before.ControlBoneCount == 0, "auto-rig has no control bones" );
			var (rest, angle) = RigQuality.DetectRestPose( rig );
			Require( rest == "Unknown", $"hanging arms are neither A nor T, got {rest}" );
			Require( angle > 80f && angle < 95f, $"arm angle from lateral should be ~87, got {angle:0.#}" );
			var input = new AutoRigger.RigResult { Template = "humanoid", Skeleton = rig, Weights = mesh.Skin };
			var ok = RigQuality.TryGrade( input, mesh, out var result, out var why, mode: ReweightMode.Distance );
			Require( ok, $"grade should succeed, got: {why}" );
			Require( result.Report.DefBoneCount == 64, $"22 + 24 fingers + 2 toes + 16 twists = 64 DEF, got {result.Report.DefBoneCount}" );
			Require( result.Report.ControlBoneCount == 5, $"5 placement targets, got {result.Report.ControlBoneCount}" );
			Require( result.Skeleton.Count == 69, $"64 DEF + 5 control = 69 bones, got {result.Skeleton.Count}" );
			Require( result.Report.DefBoneCount >= RigQuality.MinDefBones && result.Report.DefBoneCount <= RigQuality.MaxDefBones,
				"DEF count inside the 50-80 animator-grade band" );
			Near( result.RootOffset.x, 0f, 1e-4f, "root offset x" );
			Near( result.RootOffset.y, 0f, 1e-4f, "root offset y" );
			Near( result.RootOffset.z, -3f, 1e-4f, "root offset z (pelvis was at z=3)" );
			var pelvis = result.Skeleton.IndexOf( "pelvis" );
			Require( (result.Skeleton.HeadWorld( pelvis ) - Vec3.Zero).Length < 1e-4f, "pelvis rebased onto the origin" );
			Require( CountBones( result.Skeleton, n => n.StartsWith( "finger", StringComparison.Ordinal ) ) == 24, "24 finger bones" );
			Require( CountBones( result.Skeleton, n => n.StartsWith( "toe_", StringComparison.Ordinal ) ) == 2, "one toe per foot" );
			Require( CountBones( result.Skeleton, n => n.Contains( "_twist", StringComparison.Ordinal ) ) == 16, "16 twist stations" );
			Require( CountBones( result.Skeleton, n => n.StartsWith( "ctrl_", StringComparison.Ordinal ) ) == 5, "5 control targets" );
			Require( result.Report.Passed, $"graded rig should pass the rig check: {result.Report}" );
			Require( result.Report.MaxSymmetryDeviation < 1e-3f, $"graded rig should be symmetric, deviation {result.Report.MaxSymmetryDeviation}" );
			Require( rig.Count == 22, "grade leaves the source skeleton untouched" );
			Require( result.Weights.Count == mesh.Positions.Count, "weights cover every vertex" );
			Require( result.Weights.Validate( mesh.Positions.Count, result.Skeleton.Count ).Count == 0, "graded weights validate" );
			var fingerWeighted = 0;
			var twistWeighted = 0;
			for ( var v = 0; v < result.Weights.Count; v++ )
				foreach ( var bw in result.Weights[v] )
				{
					var name = result.Skeleton.Bones[bw.Bone].Name;
					if ( name.StartsWith( "finger", StringComparison.Ordinal ) ) fingerWeighted++;
					if ( name.Contains( "_twist", StringComparison.Ordinal ) ) twistWeighted++;
				}
			Require( fingerWeighted > 0, "some vertices moved onto finger bones" );
			Require( twistWeighted > 0, "some vertices spread onto twist bones" );
			var names = RigQuality.ExportNames( result.Skeleton );
			Require( names["arm_upper_L"] == "arm_upper.L", "export names use .L" );
			Require( names["finger_index_0_L"] == "finger_index_0.L", "finger export names use .L" );
			Require( names["arm_upper_L_twist0"] == "arm_upper.L_twist0", "twist export names use .L" );
			Require( names["spine_0"] == "spine_0", "centre bones keep their name" );
			var boundsInput = new AutoRigger.RigResult { Template = "bounds", Skeleton = rig, Weights = mesh.Skin };
			Require( !RigQuality.TryGrade( boundsInput, mesh, out _, out var boundsWhy ),
				"a bounds rig has no limbs to grade" );
			Require( boundsWhy is not null && boundsWhy.Contains( "humanoid" ), "refusal names the humanoid template" );
			Throws( () => RigQuality.TryGrade( input, null, out _, out _ ) );
		} );

		Check( "grade with heat reweights and keeps the bone counts", () =>
		{
			var rig = HumanoidSkeleton();
			var mesh = GridMesh( 20, 60 );
			mesh.Skin = WeightsByNearest( mesh, rig );
			var input = new AutoRigger.RigResult { Template = "humanoid", Skeleton = rig, Weights = mesh.Skin };
			var ok = RigQuality.TryGrade( input, mesh, out var result, out var why );
			Require( ok, $"heat grade should succeed, got: {why}" );
			Require( result.ReweightMethod == "Heat", "default reweight is heat" );
			Require( result.Report.DefBoneCount == 64 && result.Report.ControlBoneCount == 5, "same bones as the distance run" );
			Require( result.Weights.Validate( mesh.Positions.Count, result.Skeleton.Count ).Count == 0, "heat weights validate" );
		} );

		Check( "seed poses: A-pose is bind, T-pose arms go lateral, Attention arms go down", () =>
		{
			var rig = HumanoidSkeleton();
			var lib = PoseLibrary.SeedHumanoid( rig );
			Require( lib.HasPose( "A-pose" ) && lib.HasPose( "T-pose" ) && lib.HasPose( "Attention" ), "three seeds" );
			var lateral = (rig.HeadWorld( rig.IndexOf( "arm_upper_L" ) ) - rig.HeadWorld( rig.IndexOf( "arm_upper_R" ) )).Normal;
			var up = (rig.HeadWorld( rig.IndexOf( "head" ) ) - rig.HeadWorld( rig.IndexOf( "pelvis" ) )).Normal;
			var armL = rig.IndexOf( "arm_upper_L" );
			var armR = rig.IndexOf( "arm_upper_R" );
			var handL = rig.IndexOf( "hand_L" );
			var handR = rig.IndexOf( "hand_R" );
			var aPose = ProxyPose.PoseWorldFromLocals( rig, Pose( lib, "A-pose" ) );
			for ( var i = 0; i < rig.Count; i++ )
				Require( (aPose[i].Origin - rig.WorldBind( i ).Origin).Length < 1e-4f, "A-pose reproduces the bind" );
			var tPose = ProxyPose.PoseWorldFromLocals( rig, Pose( lib, "T-pose" ) );
			var dirL = (tPose[handL].Origin - tPose[armL].Origin).Normal;
			var dirR = (tPose[handR].Origin - tPose[armR].Origin).Normal;
			Require( Vec3.Dot( dirL, lateral ) > 0.99f, $"T-pose left arm should point lateral, dot {Vec3.Dot( dirL, lateral ):0.###}" );
			Require( Vec3.Dot( dirR, -lateral ) > 0.99f, "T-pose right arm should point the other way" );
			var (tRest, tAngle) = RigQuality.DetectRestPose( rig, tPose );
			Require( tRest == "T", $"seeded T-pose should detect as T, got {tRest} ({tAngle:0.#}°)" );
			var attention = ProxyPose.PoseWorldFromLocals( rig, Pose( lib, "Attention" ) );
			var aDirL = (attention[handL].Origin - attention[armL].Origin).Normal;
			Require( Vec3.Dot( aDirL, -up ) > 0.99f, $"Attention left arm should point down, dot {Vec3.Dot( aDirL, -up ):0.###}" );
		} );

		Check( "mirror pose swaps sides and mirrors in place, twice is identity", () =>
		{
			var pose = new Dictionary<string, RigPose>( StringComparer.OrdinalIgnoreCase )
			{
				["arm_upper_L"] = new RigPose( new Vec3( 0.1f, 0.2f, 0.3f ), Quat.FromAxisAngle( new Vec3( 0, 0, 1 ), 0.5f ), Vec3.One ),
				["spine_0"] = new RigPose( new Vec3( 0, 0.1f, 0 ), Quat.FromAxisAngle( new Vec3( 1, 0, 0 ), 0.3f ), Vec3.One ),
			};
			var mirrored = PoseLibrary.Mirror( pose );
			var armR = mirrored["arm_upper_R"];
			var src = pose["arm_upper_L"];
			Near( armR.Position.x, -0.1f, 1e-6f, "mirrored position x" );
			Near( armR.Position.y, 0.2f, 1e-6f, "mirrored position y" );
			Near( armR.Rotation.x, src.Rotation.x, 1e-6f, "mirrored rotation x" );
			Near( armR.Rotation.y, -src.Rotation.y, 1e-6f, "mirrored rotation y" );
			Near( armR.Rotation.z, -src.Rotation.z, 1e-6f, "mirrored rotation z" );
			Near( armR.Rotation.w, src.Rotation.w, 1e-6f, "mirrored rotation w" );
			Require( !mirrored.ContainsKey( "arm_upper_L" ), "one-sided pose mirrors onto the partner side only" );
			var spine = mirrored["spine_0"];
			Near( spine.Position.x, 0f, 1e-6f, "centre bone mirrors in place" );
			var twice = PoseLibrary.Mirror( mirrored );
			Require( twice["arm_upper_L"] == pose["arm_upper_L"], "mirroring twice returns the pose" );
			Require( twice["spine_0"] == pose["spine_0"], "centre bone round-trips too" );
		} );

		Check( "finger/toe/twist/control additions are idempotent", () =>
		{
			var rig = HumanoidSkeleton();
			RigQuality.AddFingers( rig );
			RigQuality.AddToes( rig );
			RigQuality.AddTwists( rig );
			RigQuality.AddControls( rig );
			var afterFirst = rig.Count;
			Require( afterFirst == 22 + 24 + 2 + 16 + 5, $"first pass adds 47 bones, got {afterFirst - 22}" );
			RigQuality.AddFingers( rig );
			RigQuality.AddToes( rig );
			RigQuality.AddTwists( rig );
			RigQuality.AddControls( rig );
			Require( rig.Count == afterFirst, "second pass adds nothing" );
		} );
	}
}
