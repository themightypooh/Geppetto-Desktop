using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

// Real IK/FK + production weight paint (godlike-ik-fk-production-weight-paint).
// IK/FK switching and snapping compose the existing solvers (ChainIk for the solve
// with pole bone + stretch, IkFk for the blend and the snap) — see IkFkSwitch.cs.
// The weight-paint tools are the batch whole-mesh side of the brush strokes
// (WeightPaintTools.cs); RigCheck.cs is the pre-export gate. These checks prove the
// ticket's CHECK list: IK/FK snap preserves the pose, the pole target changes the
// elbow plane, normalize sums to 1.0, Limit Total keeps <= 4 influences, prune
// removes < 0.01, mirror matches .L/.R, and Rig Check fails a bad rig and passes a
// good one.
//
// NOT yet registered in TestRegistry.cs (coordinator-owned; the final integrator adds
// the one Register line, as with KernelParityTests.cs).
internal static class IkFkWeightPaintTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "ikfk-weight-paint", name, test );
		void Require( bool condition, string message )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, string what, float tol = 1e-4f ) =>
			Require( MathF.Abs( actual - expected ) <= tol, $"{what}: expected {expected}, got {actual}" );
		void NearVec( Vec3 actual, Vec3 expected, string what, float tol = 1e-4f ) =>
			Require( (actual - expected).Length <= tol, $"{what}: expected {expected}, got {actual}" );

		// Three-bone arm: upper_L (0,0,0)->(1,0,0), lower_L (1,0,0)->(2,0,0),
		// hand_L (2,0,0)->(2.5,0,0), all on a root at the origin. Bind FK end is
		// (2,0,0); the chain's reach is 2.
		static Skeleton ArmRig()
		{
			var rig = new Skeleton();
			rig.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, 0 ), new Vec3( 0, 1, 0 ) );
			rig.AddBoneFromPoints( "upper_L", 0, new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ) );
			rig.AddBoneFromPoints( "lower_L", 1, new Vec3( 1, 0, 0 ), new Vec3( 2, 0, 0 ) );
			rig.AddBoneFromPoints( "hand_L", 2, new Vec3( 2, 0, 0 ), new Vec3( 2.5f, 0, 0 ) );
			return rig;
		}

		static LimbIkFk ArmLimb( float blend = 0f, float maxStretch = 1f ) => new()
		{
			Name = "arm_L",
			RootBone = "upper_L",
			MidBone = "lower_L",
			EndBone = "hand_L",
			Blend = blend,
			MaxStretch = maxStretch,
		};

		// 7x7 grid in the XY plane, x and y from -3 to 3 step 1, z = 0; vertex (i, j)
		// is j*7 + i at x = i-3, y = j-3. Skin: everything on root.
		static (PolyMesh Mesh, Skeleton Rig) Plane()
		{
			var mesh = new PolyMesh();
			for ( int j = 0; j <= 6; j++ )
				for ( int i = 0; i <= 6; i++ )
					mesh.AddVertex( new Vec3( i - 3, j - 3, 0 ) );
			for ( int j = 0; j < 6; j++ )
				for ( int i = 0; i < 6; i++ )
					mesh.AddFace( new[] { j * 7 + i, j * 7 + i + 1, (j + 1) * 7 + i + 1, (j + 1) * 7 + i } );
			var rig = new Skeleton();
			rig.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, -1 ), new Vec3( 0, 0, 0 ) );
			rig.AddBoneFromPoints( "arm_L", 0, new Vec3( 1, 0, 0 ), new Vec3( 5, 0, 0 ) );
			rig.AddBoneFromPoints( "arm_R", 0, new Vec3( -1, 0, 0 ), new Vec3( -5, 0, 0 ) );
			mesh.Skin = SkinWeights.AllTo( mesh.VertexCount, 0 );
			return (mesh, rig);
		}

		static int At( int x, int y ) => (y + 3) * 7 + (x + 3);
		static float W( SkinWeights weights, int v, int bone ) => WeightPaintTools.WeightOf( weights[v], bone );
		static float Sum( BoneWeight[] w ) => w.Sum( x => x.Weight );

		static Skeleton SixBoneRig()
		{
			var rig = new Skeleton();
			rig.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, -1 ), new Vec3( 0, 0, 0 ) );
			for ( var b = 1; b <= 5; b++ )
				rig.AddBoneFromPoints( $"bone{b}", 0, new Vec3( b, 0, 0 ), new Vec3( b + 1, 0, 0 ) );
			return rig;
		}

		Check( "IK/FK snap preserves the pose", () =>
		{
			var rig = ArmRig();
			var limb = ArmLimb( blend: 0f );
			var fkWorld = IkFk.BindWorld( rig );
			var goal = new RigPose( new Vec3( 1.5f, 1f, 0f ), Quat.Identity, Vec3.One );
			var ikWorld = IkFkSwitch.Solve( rig, limb, fkWorld, goal );
			Require( ikWorld is not null, "IK solve should succeed on a reachable goal" );
			NearVec( ikWorld[3].Origin, new Vec3( 1.5f, 1f, 0f ), "IK end should reach the goal" );

			var locals = IkFkSwitch.SnapToFk( rig, limb, fkWorld, ikWorld, out var maxError );
			Require( locals is not null, "snap should succeed on a reachable pose" );
			Require( maxError < 1e-4f, $"snap error should be ~0, got {maxError}" );
			var rebuilt = IkFk.WorldFromLocals( rig, locals );
			NearVec( rebuilt[3].Origin, new Vec3( 1.5f, 1f, 0f ), "rebuilt FK end" );
			NearVec( rebuilt[2].Origin, ikWorld[2].Origin, "rebuilt FK elbow" );
			NearVec( rebuilt[0].Origin, fkWorld[0].Origin, "bones outside the chain keep the FK pose" );
		} );

		Check( "slider blends FK and IK, 0 is IK and 1 is FK", () =>
		{
			var rig = ArmRig();
			var fkWorld = IkFk.BindWorld( rig );
			var goal = new RigPose( new Vec3( 1.5f, 1f, 0f ), Quat.Identity, Vec3.One );

			var ik = IkFkSwitch.Solve( rig, ArmLimb( blend: 0f ), fkWorld, goal );
			NearVec( ik[3].Origin, new Vec3( 1.5f, 1f, 0f ), "blend 0 is the IK pose" );

			var fk = IkFkSwitch.Solve( rig, ArmLimb( blend: 1f ), fkWorld, goal );
			NearVec( fk[3].Origin, new Vec3( 2f, 0f, 0f ), "blend 1 is the FK pose" );

			var half = IkFkSwitch.Solve( rig, ArmLimb( blend: 0.5f ), fkWorld, goal );
			NearVec( half[3].Origin, new Vec3( 1.75f, 0.5f, 0f ), "blend 0.5 is halfway" );
		} );

		Check( "pole target changes the elbow plane", () =>
		{
			var rig = ArmRig();
			var fkWorld = IkFk.BindWorld( rig );
			var goal = new RigPose( new Vec3( 1.5f, 1f, 0f ), Quat.Identity, Vec3.One );

			var up = ArmLimb();
			up.UseFixedPole = true;
			up.Pole = new Vec3( 0, 0, 1 );
			var upWorld = IkFkSwitch.Solve( rig, up, fkWorld, goal );

			var down = ArmLimb();
			down.UseFixedPole = true;
			down.Pole = new Vec3( 0, 0, -1 );
			var downWorld = IkFkSwitch.Solve( rig, down, fkWorld, goal );

			Require( upWorld[2].Origin.z > 0.1f, $"pole +Z should push the elbow up, got {upWorld[2].Origin}" );
			Require( downWorld[2].Origin.z < -0.1f, $"pole -Z should push the elbow down, got {downWorld[2].Origin}" );
			Near( upWorld[2].Origin.z, -downWorld[2].Origin.z, "opposite poles mirror the elbow", 1e-3f );

			var angled = IkFkSwitch.PoleAtAngle( new Vec3( 0, 0, 0 ), new Vec3( 2, 0, 0 ), new Vec3( 0, 0, 1 ), MathF.PI );
			NearVec( angled, new Vec3( 0, 0, -1 ), "pole angle 180 flips the pole" );
		} );

		Check( "IK stretch reaches past the chain, and 1 never stretches", () =>
		{
			var rig = ArmRig();
			var fkWorld = IkFk.BindWorld( rig );
			var goal = new RigPose( new Vec3( 3f, 0f, 0f ), Quat.Identity, Vec3.One );

			var stretched = IkFkSwitch.Solve( rig, ArmLimb( blend: 0f, maxStretch: 1.5f ), fkWorld, goal );
			// The two-bone solver always stops 0.001 short of full extension (singularity
			// guard), so 1.5x stretch of a 2-long chain lands at 2.999, not 3.0.
			NearVec( stretched[3].Origin, new Vec3( 2.999f, 0f, 0f ), "1.5x stretch should reach the stretch cap", 1e-3f );

			var clamped = IkFkSwitch.Solve( rig, ArmLimb( blend: 0f, maxStretch: 1f ), fkWorld, goal );
			Near( clamped[3].Origin.x, 2f, "no stretch stops at the chain length", 0.01f );
			Near( clamped[3].Origin.y, 0f, "no stretch stays on the line", 1e-4f );
		} );

		Check( "snap IK to FK gives the FK goal and pole", () =>
		{
			var rig = ArmRig();
			var limb = ArmLimb();
			var fkWorld = IkFk.BindWorld( rig );
			IkFkSwitch.SnapToIk( rig, limb, fkWorld, out var goal, out var pole );
			NearVec( goal.Position, new Vec3( 2f, 0f, 0f ), "IK goal is the FK end" );
			// The FK arm is straight along +X, so the default pole is the mid joint
			// pushed off the root-end line by +Z (see IkFk.DefaultPole).
			NearVec( pole, new Vec3( 0f, 0f, 1f ), "pole is the FK bend direction from the root", 1e-4f );
		} );

		Check( "auto-key writes chain keys and replaces the same frame", () =>
		{
			var rig = ArmRig();
			var limb = ArmLimb();
			var fkWorld = IkFk.BindWorld( rig );
			var goal = new RigPose( new Vec3( 1.5f, 1f, 0f ), Quat.Identity, Vec3.One );
			var ikWorld = IkFkSwitch.Solve( rig, limb, fkWorld, goal );
			var locals = IkFkSwitch.SnapToFk( rig, limb, fkWorld, ikWorld, out _ );

			var clip = new RigAnimFile();
			Require( IkFkSwitch.AutoKey( clip, limb, rig, 10, locals ) == 3, "one key per chain bone" );
			var track = clip.Find( "upper_L" );
			Require( track is not null && track.Keys.Count == 1, "upper_L should have one key" );
			Require( track.Keys[0].Frame == 10, "key should be on frame 10" );
			NearVec( track.Keys[0].Local.Position, locals[1].Origin, "keyed local matches the snap" );

			Require( IkFkSwitch.AutoKey( clip, limb, rig, 10, locals ) == 3, "re-keying the same frame replaces, not doubles" );
			Require( clip.Find( "upper_L" ).Keys.Count == 1, "still one key after re-keying" );

			IkFkSwitch.AutoKey( clip, limb, rig, 20, locals );
			Require( clip.Find( "upper_L" ).Keys.Count == 2, "a second frame adds a key" );
			Require( clip.Find( "upper_L" ).Keys[0].Frame == 10 && clip.Find( "upper_L" ).Keys[1].Frame == 20, "keys stay sorted by frame" );
		} );

		Check( "parent switch keeps the posed world", () =>
		{
			var rig = ArmRig();
			var fkWorld = IkFk.BindWorld( rig );

			// A second rig with the hand parented to the upper arm instead of the lower.
			var rig2 = new Skeleton();
			rig2.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, 0 ), new Vec3( 0, 1, 0 ) );
			rig2.AddBoneFromPoints( "upper_L", 0, new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ) );
			rig2.AddBoneFromPoints( "lower_L", 1, new Vec3( 1, 0, 0 ), new Vec3( 2, 0, 0 ) );
			rig2.AddBoneFromPoints( "hand_L", 1, new Vec3( 2, 0, 0 ), new Vec3( 2.5f, 0, 0 ) );

			var newLocal = IkFkSwitch.SwitchParentPosed( fkWorld, 3, 1 );
			var locals = RigAnimFile.LocalSpace( rig, fkWorld );
			locals[3] = newLocal;
			var world2 = RigAnimFile.ModelSpace( rig2, locals );
			NearVec( world2[3].Origin, fkWorld[3].Origin, "hand keeps its posed world after the switch" );
			Require( (newLocal.Origin - locals[2].Origin).Length > 1e-4f || (newLocal.Origin - RigAnimFile.LocalSpace( rig, fkWorld )[3].Origin).Length > 1e-4f,
				"the new local is not the old one" );
		} );

		Check( "foot-roll rotates about the pivot", () =>
		{
			var foot = Xform.Translate( new Vec3( 0, 0, 1 ) );
			var rolled = FootRoll.Roll( foot, new Vec3( 0, 0, 0 ), new Vec3( 0, 1, 0 ), MathF.PI / 2f );
			NearVec( rolled.Origin, new Vec3( 1, 0, 0 ), "a 90-degree roll about Y moves the foot" );

			var same = FootRoll.Roll( foot, new Vec3( 0, 0, 0 ), new Vec3( 0, 1, 0 ), 0f );
			NearVec( same.Origin, foot.Origin, "zero roll leaves the foot alone" );
			Require( (same.Y - foot.Y).Length < 1e-6f, "zero roll leaves the rotation alone" );

			var inPlace = FootRoll.Roll( foot, new Vec3( 0, 0, 1 ), new Vec3( 1, 0, 0 ), MathF.PI / 2f );
			NearVec( inPlace.Origin, foot.Origin, "a pivot on the foot keeps the origin in place" );
			NearVec( inPlace.Z, new Vec3( 0, -1, 0 ), "the foot turns in place" );
		} );

		Check( "normalize sums to 1.0 and keeps locked bones exact", () =>
		{
			var (mesh, rig) = Plane();
			var weights = mesh.Skin;
			weights[At( 0, 0 )] = new[] { new BoneWeight( 1, 0.3f ), new BoneWeight( 0, 0.2f ) };
			Require( WeightPaintTools.Normalize( weights ) == 1, "one vertex should change" );
			Near( Sum( weights[At( 0, 0 )] ), 1f, "normalized sum" );
			Near( W( weights, At( 0, 0 ), 1 ), 0.6f, "ratio preserved: arm_L 0.3/0.5" );
			Near( W( weights, At( 0, 0 ), 0 ), 0.4f, "ratio preserved: root 0.2/0.5" );

			weights[At( 1, 0 )] = new[] { new BoneWeight( 1, 0.3f ), new BoneWeight( 0, 0.2f ) };
			var locked = new HashSet<int> { 0 };
			Require( WeightPaintTools.Normalize( weights, locked, vertices: new[] { At( 1, 0 ) } ) == 1, "locked normalize should change one vertex" );
			Near( W( weights, At( 1, 0 ), 0 ), 0.2f, "locked root stays exact" );
			Near( W( weights, At( 1, 0 ), 1 ), 0.8f, "arm_L fills the rest" );
		} );

		Check( "Limit Total keeps at most 4 influences", () =>
		{
			var rig = SixBoneRig();
			var weights = new SkinWeights( 1 );
			weights[0] = new[]
			{
				new BoneWeight( 0, 0.30f ), new BoneWeight( 1, 0.25f ), new BoneWeight( 2, 0.20f ),
				new BoneWeight( 3, 0.15f ), new BoneWeight( 4, 0.06f ), new BoneWeight( 5, 0.04f ),
			};
			Require( WeightPaintTools.LimitTotal( weights, 4 ) == 1, "one vertex should change" );
			Require( weights[0].Length == 4, $"should keep 4, kept {weights[0].Length}" );
			Near( Sum( weights[0] ), 1f, "survivors renormalize" );
			Require( W( weights, 0, 4 ) == 0f && W( weights, 0, 5 ) == 0f, "the two smallest are dropped" );
			Near( W( weights, 0, 0 ), 0.30f / 0.90f, "strongest keeps its share" );

			var before = RigCheck.Check( rig, weights );
			Require( before.OverLimit == 0, "Rig Check should agree the vertex is within the cap" );
		} );

		Check( "prune removes influences below 0.01", () =>
		{
			var (mesh, rig) = Plane();
			var weights = mesh.Skin;
			weights[At( 0, 0 )] = new[] { new BoneWeight( 1, 0.005f ), new BoneWeight( 0, 0.995f ) };
			Require( WeightPaintTools.PruneThreshold( weights, 0.01f ) == 1, "one vertex should change" );
			Require( W( weights, At( 0, 0 ), 1 ) == 0f, "the 0.005 influence is gone" );
			Near( W( weights, At( 0, 0 ), 0 ), 1f, "root takes the whole vertex" );

			weights[At( 1, 0 )] = new[] { new BoneWeight( 1, 0.005f ), new BoneWeight( 0, 0.995f ) };
			var locked = new HashSet<int> { 1 };
			Require( WeightPaintTools.PruneThreshold( weights, 0.01f, locked, vertices: new[] { At( 1, 0 ) } ) == 0,
				"a locked influence is never pruned, so nothing changes" );
			Near( W( weights, At( 1, 0 ), 1 ), 0.005f, "locked influence survives" );
		} );

		Check( "mirror matches .L/.R on bones and on vertices", () =>
		{
			var (mesh, rig) = Plane();
			var weights = mesh.Skin;
			weights[At( 0, 0 )] = new[] { new BoneWeight( 1, 0.7f ), new BoneWeight( 0, 0.3f ) };
			Require( WeightPaintTools.MirrorBones( weights, rig ) == 1, "one vertex should change" );
			Near( W( weights, At( 0, 0 ), 2 ), 0.7f, "arm_L becomes arm_R" );
			Near( W( weights, At( 0, 0 ), 0 ), 0.3f, "root maps to itself" );
			Near( W( weights, At( 0, 0 ), 1 ), 0f, "arm_L is gone" );

			weights[At( -2, 0 )] = new[] { new BoneWeight( 1, 1f ) };
			Require( WeightPaintTools.MirrorVertices( mesh, weights, rig, new Vec3( 1, 0, 0 ) ) >= 1, "at least the painted pair should mirror" );
			Near( W( weights, At( 2, 0 ), 2 ), 1f, "the right vertex gets arm_R" );
			Near( W( weights, At( -2, 0 ), 1 ), 1f, "the source vertex is untouched" );
		} );

		Check( "gradient ramps along the stroke axis", () =>
		{
			var (mesh, rig) = Plane();
			var weights = mesh.Skin;
			Require( WeightPaintTools.Gradient( weights, mesh, 1, new Vec3( -3, 0, 0 ), new Vec3( 3, 0, 0 ), 0f, 1f ) > 0, "gradient should paint" );
			Near( W( weights, At( -3, 0 ), 1 ), 0f, "start of the ramp is 0" );
			Near( W( weights, At( 0, 0 ), 1 ), 0.5f, "middle of the ramp is 0.5" );
			Near( W( weights, At( 3, 0 ), 1 ), 1f, "end of the ramp is 1" );
			Near( W( weights, At( 1, 0 ), 1 ), 2f / 3f, "ramp is linear", 1e-4f );
		} );

		Check( "blur softens toward the neighbours, sharpen puts it back", () =>
		{
			var (mesh, rig) = Plane();
			var weights = mesh.Skin;
			weights[At( 0, 0 )] = new[] { new BoneWeight( 1, 0.5f ), new BoneWeight( 0, 0.5f ) };
			foreach ( var (x, y) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) } )
				weights[At( x, y )] = new[] { new BoneWeight( 1, 0.1f ), new BoneWeight( 0, 0.9f ) };

			Require( WeightPaintTools.Blur( weights, mesh, 1f ) > 0, "blur should change vertices" );
			// Neighbour average at the centre: (0.5 + 4 x 0.1) / 5 = 0.18.
			Near( W( weights, At( 0, 0 ), 1 ), 0.18f, "blur replaces the centre with its neighbour average" );

			weights[At( 0, 0 )] = new[] { new BoneWeight( 1, 0.5f ), new BoneWeight( 0, 0.5f ) };
			foreach ( var (x, y) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) } )
				weights[At( x, y )] = new[] { new BoneWeight( 1, 0.1f ), new BoneWeight( 0, 0.9f ) };
			Require( WeightPaintTools.Sharpen( weights, mesh, 1f ) > 0, "sharpen should change vertices" );
			// 0.5 + (0.5 - 0.18) = 0.82.
			Near( W( weights, At( 0, 0 ), 1 ), 0.82f, "sharpen moves away from the neighbour average" );
		} );

		Check( "Rig Check fails a bad rig and passes a good one", () =>
		{
			var rig = SixBoneRig();
			var good = new SkinWeights( 3 );
			for ( var v = 0; v < 3; v++ )
				good[v] = new[] { new BoneWeight( 0, 0.6f ), new BoneWeight( 1, 0.4f ) };
			var goodReport = RigCheck.Check( rig, good );
			Require( goodReport.Passes, $"good rig should pass: {goodReport.Summary}" );
			Require( RigCheck.CanExport( goodReport ), "export should be offered for a good rig" );
			Require( goodReport.Unweighted == 0 && goodReport.Unnormalized == 0 && goodReport.OverLimit == 0, "good rig counts are zero" );

			var bad = new SkinWeights( 3 );
			bad[0] = Array.Empty<BoneWeight>();
			bad[1] = new[] { new BoneWeight( 0, 0.3f ), new BoneWeight( 1, 0.2f ) };
			bad[2] = new[]
			{
				new BoneWeight( 0, 0.30f ), new BoneWeight( 1, 0.25f ), new BoneWeight( 2, 0.20f ),
				new BoneWeight( 3, 0.15f ), new BoneWeight( 4, 0.06f ), new BoneWeight( 5, 0.04f ),
			};
			var badReport = RigCheck.Check( rig, bad );
			Require( !badReport.Passes, "bad rig should fail" );
			Require( !RigCheck.CanExport( badReport ), "export must not be offered for a bad rig" );
			Require( badReport.Unweighted == 1, $"one unweighted vertex, got {badReport.Unweighted}" );
			Require( badReport.Unnormalized == 1, $"one unnormalized vertex, got {badReport.Unnormalized}" );
			Require( badReport.OverLimit == 1, $"one over-limit vertex, got {badReport.OverLimit}" );
			Require( badReport.WorstVertex == 1, $"worst vertex is 1, got {badReport.WorstVertex}" );
			Near( badReport.WorstDeviation, 0.5f, "worst deviation is 0.5" );
			Require( badReport.Lines.Count >= 4, "the report should list every finding" );
		} );
	}
}
