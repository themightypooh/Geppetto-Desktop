using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Body axes read off a rig, not assumed from the mesh: the shoulder line is the
/// lateral axis, the pelvis-to-head line is up, and the ball of the foot points
/// along facing (AutoRigger documents that feet point along +facing). A rig with no
/// left/right pair has no lateral axis — callers must handle that honestly instead
/// of guessing one from the world axes.
/// </summary>
public static class RigAxes
{
	public static bool TryLateral( Skeleton rig, out Vec3 lateral )
	{
		lateral = Vec3.Zero;

		if ( rig is null )
			return false;

		foreach ( var (left, right) in new[] { ("arm_upper_L", "arm_upper_R"), ("clavicle_L", "clavicle_R") } )
		{
			var li = rig.IndexOf( left );
			var ri = rig.IndexOf( right );

			if ( li < 0 || ri < 0 )
				continue;

			var axis = rig.HeadWorld( li ) - rig.HeadWorld( ri );

			if ( axis.LengthSquared < 1e-12f )
				continue;

			lateral = axis.Normal;
			return true;
		}

		return false;
	}

	/// <summary>Pelvis-to-head direction, or world +Z when the rig has no spine to read.</summary>
	public static Vec3 Up( Skeleton rig )
	{
		if ( rig is null )
			return new Vec3( 0, 0, 1 );

		var pelvis = rig.IndexOf( rig.RootBone.Length > 0 ? rig.RootBone : "pelvis" );
		var head = rig.IndexOf( "head" );

		if ( pelvis < 0 || head < 0 )
			return new Vec3( 0, 0, 1 );

		var axis = rig.HeadWorld( head ) - rig.HeadWorld( pelvis );

		return axis.LengthSquared < 1e-12f ? new Vec3( 0, 0, 1 ) : axis.Normal;
	}

	/// <summary>
	/// The foot's direction with the up component removed: the ball of the foot
	/// points along facing. Falls back to lateral × up — a guess, and documented as
	/// one; the foot is the ground truth whenever the rig has feet.
	/// </summary>
	public static Vec3 Facing( Skeleton rig )
	{
		if ( rig is null )
			return new Vec3( 0, 1, 0 );

		var up = Up( rig );

		foreach ( var ball in new[] { "ball_L", "ball_R", "foot_L", "foot_R" } )
		{
			var i = rig.IndexOf( ball );

			if ( i < 0 )
				continue;

			var dir = rig.TailWorld( i ) - rig.HeadWorld( i );
			dir -= up * Vec3.Dot( dir, up );

			if ( dir.LengthSquared > 1e-12f )
				return dir.Normal;
		}

		if ( TryLateral( rig, out var lateral ) )
		{
			var facing = Vec3.Cross( up, lateral );

			if ( facing.LengthSquared > 1e-12f )
				return facing.Normal;
		}

		return new Vec3( 0, 1, 0 );
	}

	/// <summary>Longest span of the rig's joint positions, for scale-relative tolerances.</summary>
	public static float Height( Skeleton rig )
	{
		if ( rig is null || rig.Count == 0 )
			return 0f;

		var min = rig.HeadWorld( 0 );
		var max = min;

		for ( var i = 0; i < rig.Count; i++ )
		{
			foreach ( var p in new[] { rig.HeadWorld( i ), rig.TailWorld( i ) } )
			{
				min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
				max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
			}
		}

		return (max - min).Length;
	}
}

/// <summary>
/// The Rig Check: what is wrong with a rig's weights, as data. The export gate is
/// <see cref="RigCheckReport.Passed"/> — zero unnormalized vertices, zero unweighted
/// vertices, and a cap loss under 5% (a fifth influence worth more than 5% of a vertex
/// would bend differently in the game, which keeps the strongest four). The raw
/// over-influence count is reported alongside, because a rig that needs its fifth
/// influence pruned everywhere is a rig whose weights were never finished, even when
/// the loss itself is small.
/// </summary>
public sealed class RigCheckReport
{
	/// <summary>Set when the check itself could not run (no skeleton or no weights).</summary>
	public string Fatal;
	public int UnnormalizedVertices;
	public int ZeroWeightVertices;
	public int OverInfluenceVertices;
	public float WorstCapLoss;
	public int WorstCapLossVertex = -1;
	public int NonDefWeightedVertices;
	public List<string> MissingSideBones = new();
	public List<string> AsymmetricPairs = new();
	public float MaxSymmetryDeviation;
	public int DefBoneCount;
	public int ControlBoneCount;

	/// <summary>The export gate: 0 unnormalized, 0 zero-weight, cap loss ≤ 5%, no
	/// vertex weighted to a non-deform bone. Symmetry and missing side bones are
	/// reported, not gated — a one-armed character is a valid character.</summary>
	public bool Passed => UnnormalizedVertices == 0 && ZeroWeightVertices == 0
		&& WorstCapLoss <= 0.05f && NonDefWeightedVertices == 0;

	public List<string> Errors
	{
		get
		{
			var errors = new List<string>();

			if ( Fatal is not null )
				errors.Add( Fatal );

			if ( UnnormalizedVertices > 0 )
				errors.Add( $"{UnnormalizedVertices} vertices do not sum to 1" );

			if ( ZeroWeightVertices > 0 )
				errors.Add( $"{ZeroWeightVertices} vertices have no weights" );

			if ( WorstCapLoss > 0.05f )
				errors.Add( $"vertex {WorstCapLossVertex} loses {WorstCapLoss:P0} of its weight to the 4-influence cap" );

			if ( NonDefWeightedVertices > 0 )
				errors.Add( $"{NonDefWeightedVertices} vertices are weighted to non-deform bones" );

			return errors;
		}
	}

	public override string ToString() =>
		Passed
			? $"Rig Check passed: {DefBoneCount} DEF + {ControlBoneCount} control bones, {OverInfluenceVertices} vertices over 4 influences (worst loss {WorstCapLoss:P1})"
			: "Rig Check failed: " + string.Join( "; ", Errors );
}

/// <summary>
/// How the animator-grade pass reweights: Heat is the real bone-heat diffusion
/// (<see cref="AutoWeights.Heat"/>), Distance is AutoRigger's own inverse-distance
/// scheme extended to the new bones. Heat is the default because the ticket names
/// heat auto-weights; Distance is the deterministic fallback for meshes Heat
/// refuses (and the one the tests pin exactly).
/// </summary>
public enum ReweightMode
{
	Heat,
	Distance,
}

/// <summary>
/// The animator-grade pass (godlike-proxy-pose-animator-grade-auto-rig, P0): what turns
/// the board auto-rigger's output into a rig an animator can use. It runs on a CLONE of
/// the auto-rig result — the input is never mutated — and returns the graded rig, its
/// weights, and the Rig Check that gates export.
///
///   fingers + toes — Citizen-named chains off the hands and balls of the feet
///     (index/middle/ring × 3, thumb × 3, one toe per foot; citizen has no pinky,
///     so it rides the ring finger, exactly as CitizenBoneMap documents);
///   twists — two citizen <c>_twist0/_twist1</c> stations per limb bone, via
///     <see cref="RigBuild.AddTwist"/>, with weights shared by
///     <see cref="TwistWeights.Spread"/> (the same flow BindFeature runs);
///   controls — five Deform=false placement targets (hands, feet, head) for the
///     animator; the IK/FK drivers that couple them to the DEF chain belong to
///     the godlike-ik-fk task, not this one, and the bones say so in their doc;
///   root at origin — the pelvis is rebased onto the origin, mesh and skeleton
///     together, so the bind survives the move;
///   reweight — Heat by default, then twist spread, then prune to 4;
///   symmetrise — bones averaged across the sagittal plane, weights mirrored.
///
/// HONEST LIMITS. The fingers are placed from measured proportions (hand length,
/// shoulder line, foot direction), not from landmarks — the same posture as
/// AutoRigger, which places joints from height fractions. The thumb side assumes
/// palms face backward in the rest pose (thumbs forward); a rig posed otherwise
/// gets index and ring swapped, which is cosmetically wrong but structurally
/// sound, and the Rig Check still passes. A/T-pose detection is included
/// (<see cref="DetectRestPose"/>); draggable markers are a viewport concern and
/// are not in this backend ticket — the kernel exposes joint positions through
/// <see cref="Skeleton.HeadWorld"/> for whoever builds them.
/// </summary>
public static class RigQuality
{
	/// <summary>DEF bones an animator-grade humanoid must have: 50-80, fingers/toes/twists included.</summary>
	public const int MinDefBones = 50;

	/// <summary>DEF bones an animator-grade humanoid must have: 50-80, fingers/toes/twists included.</summary>
	public const int MaxDefBones = 80;

	/// <summary>Limb bones that get twist stations, in citizen names, both sides.</summary>
	public static readonly string[] TwistLimbs = { "arm_upper", "arm_lower", "leg_upper", "leg_lower" };

	/// <summary>Result of the grade pass: the graded rig, its weights, and the report that gates export.</summary>
	public sealed class AnimatorGradeResult
	{
		public Skeleton Skeleton { get; set; } = new Skeleton();
		public SkinWeights Weights { get; set; } = new SkinWeights();
		public RigCheckReport Report { get; set; } = new RigCheckReport();
		public string RestPose { get; set; } = "Unknown";
		public float RestArmAngleDegrees { get; set; }
		public Vec3 RootOffset { get; set; }
		public BindReport HeatReport { get; set; }
		public string ReweightMethod { get; set; } = "";
		public string Summary => Report.ToString() + $"; rest pose {RestPose} ({RestArmAngleDegrees:0}° arms)";
	}

	/// <summary>
	/// Grade an auto-rig result. The input rig and mesh weights are not mutated — the
	/// skeleton and weights are cloned first. The MESH positions ARE translated when
	/// <paramref name="rebaseRoot"/> is set (default true): root-at-origin moves the
	/// mesh and the skeleton together, which is the only way to move the root without
	/// breaking the bind; the applied offset is returned so the caller can compensate
	/// other bodies. Returns false with a reason when the rig is not the humanoid
	/// template — a bounds rig has no limbs to grade.
	/// </summary>
	public static bool TryGrade( AutoRigger.RigResult rig, PolyMesh mesh, out AnimatorGradeResult result, out string why,
		bool rebaseRoot = true, ReweightMode mode = ReweightMode.Heat )
	{
		result = new AnimatorGradeResult();

		if ( rig is null )
		{
			why = "Grade needs an auto-rig result.";
			return false;
		}

		ArgumentNullException.ThrowIfNull( mesh );

		if ( rig.Template != "humanoid" )
		{
			why = $"The animator-grade pass needs the humanoid template; this is a '{rig.Template}' rig with no limbs to grade.";
			return false;
		}

		var skeleton = rig.Skeleton.Clone();
		var weights = rig.Weights.Clone();

		Vec3 offset = Vec3.Zero;

		if ( rebaseRoot )
		{
			if ( !RebaseRootToOrigin( mesh, skeleton, out offset, out var rebaseWhy ) )
			{
				why = rebaseWhy;
				return false;
			}
		}

		AddFingers( skeleton );
		AddToes( skeleton );
		AddTwists( skeleton );

		SkinWeights reweighted;
		BindReport heatReport = null;

		if ( mode == ReweightMode.Heat )
		{
			reweighted = AutoWeights.Heat( mesh, skeleton, out var report, new AutoWeightOptions { MaxInfluences = 4, MinWeight = 0.01f } );
			heatReport = report;
		}
		else
		{
			reweighted = ReweightDistance( mesh, skeleton );
		}

		weights = reweighted;
		StripNonDefWeights( skeleton, weights );

		// TwistWeights.Spread reads mesh.Skin, so the mesh carries the new weights
		// for the duration of the call and gets its old skin back afterwards.
		var savedSkin = mesh.Skin;
		try
		{
			mesh.Skin = weights;
			TwistWeights.Spread( mesh, skeleton );
			weights = mesh.Skin;
		}
		finally
		{
			mesh.Skin = savedSkin;
		}

		for ( var v = 0; v < weights.Count; v++ )
			weights[v] = SkinWeights.Prune( weights[v], 4 );

		// Controls join AFTER the weights are built: Heat weights every bone it
		// is given (a ctrl_* name is not "auxiliary", so it is not skipped), and
		// the strip above would then empty any vertex Heat gave to a control alone.
		// Controls never carry weight, so they must never enter the computation.
		AddControls( skeleton );

		SymmetriseBones( skeleton );
		SymmetriseWeights( mesh, skeleton, weights );

		// No vertex may leave the grade unweighted: the strip can empty a vertex
		// whose influences were all twist bones, and SymmetriseWeights only fills
		// an empty vertex when its mirror partner is weighted. NeighbourAverage
		// fills from weighted neighbours, so the fill follows the surface and can
		// only name bones the neighbours already use; NearestBone is the backstop
		// for an island with no weighted neighbour at all.
		var (filledWeights, _) = SkinBinder.FillUnweighted( mesh, weights, skeleton, UnweightedMode.NeighbourAverage );
		weights = filledWeights;

		var stillEmpty = false;

		for ( var v = 0; v < weights.Count; v++ )
			if ( weights[v].Length == 0 )
			{
				stillEmpty = true;
				break;
			}

		if ( stillEmpty )
			(weights, _) = SkinBinder.FillUnweighted( mesh, weights, skeleton, UnweightedMode.NearestBone );

		var report2 = CheckRig( skeleton, weights );
		var (rest, armAngle) = DetectRestPose( skeleton );

		result = new AnimatorGradeResult
		{
			Skeleton = skeleton,
			Weights = weights,
			Report = report2,
			RestPose = rest,
			RestArmAngleDegrees = armAngle,
			RootOffset = offset,
			HeatReport = heatReport,
			ReweightMethod = mode == ReweightMode.Heat ? "Heat" : "Distance",
		};
		why = null;
		return true;
	}

	/// <summary>
	/// T if the arms point along the shoulder line (within 20°), A if they hang 25-65°
	/// below it, Unknown otherwise — the auto-rigger's rest pose, arms hanging almost
	/// straight down, is honestly Unknown rather than either letter. Returns the arm
	/// angle from lateral in degrees alongside, so the caller can see the measurement.
	/// </summary>
	public static (string Pose, float ArmAngleDegrees) DetectRestPose( Skeleton rig, Xform[] poseWorld = null )
	{
		if ( rig is null )
			return ("Unknown", 0f);

		if ( !RigAxes.TryLateral( rig, out var lateral ) )
			return ("Unknown", 0f);

		var up = RigAxes.Up( rig );
		var arm = rig.IndexOf( "arm_upper_L" );

		if ( arm < 0 )
			return ("Unknown", 0f);

		var dir = poseWorld is null ? rig.BoneDirection( arm ) : PosedDirection( rig, poseWorld, arm );
		var angle = MathF.Acos( Math.Clamp( Vec3.Dot( dir.Normal, lateral ), -1f, 1f ) ) * 180f / MathF.PI;
		var downward = Vec3.Dot( dir, up ) < 0f;

		if ( angle < 20f )
			return ("T", angle);

		if ( downward && angle >= 25f && angle <= 65f )
			return ("A", angle);

		return ("Unknown", angle);
	}

	/// <summary>
	/// Citizen names with the side suffix in Blender/Mixamo form: <c>arm_upper_L</c>
	/// becomes <c>arm_upper.L</c>, <c>finger_index_0_L</c> becomes
	/// <c>finger_index_0.L</c>, <c>arm_upper_L_twist0</c> becomes
	/// <c>arm_upper.L_twist0</c>. Centre bones are unchanged. The live rig keeps its
	/// citizen names — this map is for export only, so a rename never strands a
	/// weight or a BodyBoneMap entry.
	/// </summary>
	public static Dictionary<string, string> ExportNames( Skeleton rig )
	{
		var map = new Dictionary<string, string>( StringComparer.Ordinal );

		if ( rig is null )
			return map;

		foreach ( var bone in rig.Bones )
		{
			var name = bone.Name;
			var exported = name;

			if ( name.EndsWith( "_L", StringComparison.Ordinal ) )
				exported = name[..^2] + ".L";
			else if ( name.EndsWith( "_R", StringComparison.Ordinal ) )
				exported = name[..^2] + ".R";
			else if ( name.Contains( "_L_", StringComparison.Ordinal ) )
				exported = name.Replace( "_L_", ".L_", StringComparison.Ordinal );
			else if ( name.Contains( "_R_", StringComparison.Ordinal ) )
				exported = name.Replace( "_R_", ".R_", StringComparison.Ordinal );

			map[name] = exported;
		}

		return map;
	}

	/// <summary>
	/// Run the Rig Check over a skeleton and its weights: unnormalized and
	/// unweighted vertices, over-influence counts and cap loss, weights on
	/// non-deform bones, missing mirror partners, and symmetry deviation. Never
	/// throws on bad input — a check that throws is no use mid-edit.
	/// </summary>
	public static RigCheckReport CheckRig( Skeleton rig, SkinWeights weights, float symmetryTolerance = -1f )
	{
		var report = new RigCheckReport();

		if ( rig is null || weights is null )
		{
			report.Fatal = "Rig Check needs a skeleton and weights.";
			return report;
		}

		report.DefBoneCount = rig.Bones.Count( b => b.Deform );
		report.ControlBoneCount = rig.Bones.Count( b => !b.Deform );

		for ( var v = 0; v < weights.Count; v++ )
		{
			var w = weights[v];

			if ( w.Length == 0 )
			{
				report.ZeroWeightVertices++;
				continue;
			}

			var sum = 0f;
			var nonDef = false;

			foreach ( var bw in w )
			{
				sum += bw.Weight;

				if ( bw.Bone < 0 || bw.Bone >= rig.Count || !rig.Bones[bw.Bone].Deform )
					nonDef = true;
			}

			if ( MathF.Abs( sum - 1f ) > 1e-3f )
				report.UnnormalizedVertices++;

			if ( w.Count( bw => bw.Weight > 0f ) > 4 )
				report.OverInfluenceVertices++;

			if ( nonDef )
				report.NonDefWeightedVertices++;
		}

		var (_, worst, at) = weights.CapLoss( 4 );
		report.WorstCapLoss = worst;
		report.WorstCapLossVertex = at;

		for ( var i = 0; i < rig.Count; i++ )
		{
			var name = rig.Bones[i].Name;

			if ( FlipSide( name ) is { } flipped && rig.IndexOf( flipped ) < 0 )
				report.MissingSideBones.Add( name );
		}

		var (maxDeviation, pairs) = CheckSymmetry( rig, symmetryTolerance );
		report.MaxSymmetryDeviation = maxDeviation;
		report.AsymmetricPairs = pairs;

		return report;
	}

	/// <summary>
	/// How far the rig is from mirror-symmetric: for each left/right pair, the
	/// distance between the right bone's head/tail and the reflection of the left
	/// bone's. Tolerance defaults to 1% of the rig's height. Returns the worst
	/// deviation and the pairs beyond tolerance.
	/// </summary>
	public static (float MaxDeviation, List<string> Pairs) CheckSymmetry( Skeleton rig, float tolerance = -1f )
	{
		var pairs = new List<string>();
		var max = 0f;

		if ( rig is null || !RigAxes.TryLateral( rig, out var lateral ) )
			return (0f, pairs);

		if ( tolerance < 0f )
			tolerance = 0.01f * Math.Max( 1f, RigAxes.Height( rig ) );

		var pelvis = rig.IndexOf( rig.RootBone.Length > 0 ? rig.RootBone : "pelvis" );
		var point = pelvis >= 0 ? rig.HeadWorld( pelvis ) : Vec3.Zero;
		Vec3 Reflect( Vec3 p ) => p - lateral * (2f * Vec3.Dot( p - point, lateral ));

		var partners = MirrorPartnerMap( rig );

		for ( var i = 0; i < rig.Count; i++ )
		{
			var j = partners[i];

			if ( j <= i )
				continue;

			var dHead = (rig.HeadWorld( j ) - Reflect( rig.HeadWorld( i ) )).Length;
			var dTail = (rig.TailWorld( j ) - Reflect( rig.TailWorld( i ) )).Length;
			var d = Math.Max( dHead, dTail );

			if ( d > max )
				max = d;

			if ( d > tolerance )
				pairs.Add( $"{rig.Bones[i].Name} / {rig.Bones[j].Name}: {d:0.###}" );
		}

		return (max, pairs);
	}

	/// <summary>
	/// Move the root (pelvis) onto the origin, mesh and skeleton together, so the
	/// bind survives the move: only the root bone's local origin shifts, every other
	/// local is relative and follows. Returns false when the rig has no root to move.
	/// </summary>
	public static bool RebaseRootToOrigin( PolyMesh mesh, Skeleton rig, out Vec3 offset, out string why )
	{
		offset = Vec3.Zero;
		why = null;

		if ( mesh is null || rig is null )
		{
			why = "Rebase needs a mesh and a skeleton.";
			return false;
		}

		var root = rig.IndexOf( rig.RootBone.Length > 0 ? rig.RootBone : "pelvis" );

		if ( root < 0 )
		{
			why = "The rig has no pelvis or root bone to rebase onto the origin.";
			return false;
		}

		offset = -rig.HeadWorld( root );

		if ( offset.LengthSquared < 1e-12f )
			return true;

		for ( var i = 0; i < mesh.Positions.Count; i++ )
			mesh.Positions[i] = mesh.Positions[i] + offset;

		var local = rig.Bones[root].Local;
		rig.Bones[root].Local = new Xform( local.X, local.Y, local.Z, local.Origin + offset );
		return true;
	}

	/// <summary>
	/// Average each left/right pair across the sagittal plane (and project centre
	/// bones onto it), so the rig is exactly symmetric instead of approximately.
	/// Bone-local frames are rebuilt from the averaged head/tail, so children follow.
	/// </summary>
	/// <summary>
	/// Pair a left/right bone name with its partner. <see cref="Skeleton.FlipSideSuffix"/>
	/// handles a trailing side marker; RigBuild twist bones carry it in the middle
	/// (<c>arm_upper_L_twist0</c>), which that function does not see, so an interior
	/// <c>_L_</c>/<c>_R_</c> segment is flipped as well. Null when the name does not
	/// name a side. The shared fix belongs to Skeleton.FlipSideSuffix; this is the
	/// local workaround so the grade does not silently treat twist bones as centre
	/// bones (SymmetriseBones would project them onto the sagittal plane).
	/// </summary>
	public static string FlipSide( string name )
	{
		if ( Skeleton.FlipSideSuffix( name ) is { } flipped )
			return flipped;

		if ( string.IsNullOrEmpty( name ) )
			return null;

		var l = name.IndexOf( "_L_", StringComparison.Ordinal );
		if ( l >= 0 )
			return name[..l] + "_R_" + name[(l + 3)..];

		var r = name.IndexOf( "_R_", StringComparison.Ordinal );
		if ( r >= 0 )
			return name[..r] + "_L_" + name[(r + 3)..];

		return null;
	}

	/// <summary>
	/// The mirror-partner index for every bone, via <see cref="FlipSide"/> (which sees
	/// RigBuild twist names that <see cref="Skeleton.MirrorPartners"/> misses).
	/// Unpaired bones map to themselves, as MirrorPartners does.
	/// </summary>
	static int[] MirrorPartnerMap( Skeleton rig )
	{
		var map = new int[rig.Count];

		for ( var i = 0; i < rig.Count; i++ )
		{
			map[i] = i;

			if ( FlipSide( rig.Bones[i].Name ) is { } flipped )
			{
				var j = rig.IndexOf( flipped );

				if ( j >= 0 )
					map[i] = j;
			}
		}

		return map;
	}

	public static void SymmetriseBones( Skeleton rig )
	{
		ArgumentNullException.ThrowIfNull( rig );

		if ( !RigAxes.TryLateral( rig, out var lateral ) )
			return;

		var up = RigAxes.Up( rig );
		var facing = RigAxes.Facing( rig );
		var pelvis = rig.IndexOf( rig.RootBone.Length > 0 ? rig.RootBone : "pelvis" );
		var point = pelvis >= 0 ? rig.HeadWorld( pelvis ) : Vec3.Zero;
		Vec3 Reflect( Vec3 p ) => p - lateral * (2f * Vec3.Dot( p - point, lateral ));
		Vec3 OnPlane( Vec3 p ) => p - lateral * Vec3.Dot( p - point, lateral );

		// The roll a bone gets decides whether its frame is mirror-consistent:
		// SetHeadTail with no hint falls back to a seed axis, which hands the
		// pelvis and the arms frames no mirror can fix, and MirrorPose then
		// twists every pose it mirrors. The hint names the direction the bone's
		// +Z should lean (see Skeleton.PerpendicularAxes). Facing lies in the
		// sagittal plane, so the same hint on both sides of a pair gives
		// mirror-consistent frames, and a centre bone gets X = +/-lateral. A bone
		// pointing along facing needs a hint across it instead, decided once per
		// pair so the two sides cannot disagree at the threshold: opposite
		// lateral hints for a pair (only the opposition matters), up for a
		// centre bone.
		// Snapshot every bone's head/tail FIRST: changing a parent's roll swings
		// its children's world positions (a child's head is an offset in the
		// parent's frame), so reading head/tail live after an earlier bone was
		// set hands the next bone positions the roll already moved - the
		// clavicle's tails swing onto the facing axis and average to nothing.
		// The snapshot is symmetrised, then each bone is placed from it in
		// topological order, so a parent's new frame is already in place when
		// its children are expressed against it, and every bone lands exactly
		// on its symmetrised snapshot position.
		var heads = new Vec3[rig.Count];
		var tails = new Vec3[rig.Count];

		for ( var i = 0; i < rig.Count; i++ )
		{
			heads[i] = rig.HeadWorld( i );
			tails[i] = rig.TailWorld( i );
		}

		var partners = MirrorPartnerMap( rig );

		for ( var i = 0; i < rig.Count; i++ )
		{
			var j = partners[i];

			if ( j == i )
			{
				var head = OnPlane( heads[i] );
				var tail = OnPlane( tails[i] );
				var alongFacing = MathF.Abs( Vec3.Dot( (tail - head).Normal, facing ) ) >= 0.9f;
				rig.SetHeadTail( i, head, tail, alongFacing ? up : facing );
				continue;
			}

			if ( j < i )
				continue;

			var headI = (heads[i] + Reflect( heads[j] )) * 0.5f;
			var tailI = (tails[i] + Reflect( tails[j] )) * 0.5f;
			var headJ = Reflect( headI );
			var tailJ = Reflect( tailI );
			var alongFacingPair = MathF.Abs( Vec3.Dot( (tailI - headI).Normal, facing ) ) >= 0.9f;

			rig.SetHeadTail( i, headI, tailI, alongFacingPair ? lateral : facing );
			rig.SetHeadTail( j, headJ, tailJ, alongFacingPair ? -lateral : facing );
		}
	}

	/// <summary>
	/// Mirror the weights: each vertex takes the average of its own weights and its
	/// mirror vertex's side-swapped weights, so the weight field is exactly
	/// mirror-symmetric afterwards. The mirror vertex is the nearest vertex to the
	/// reflected position. Pruned to 4 influences per vertex, like export.
	/// </summary>
	public static void SymmetriseWeights( PolyMesh mesh, Skeleton rig, SkinWeights weights )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		ArgumentNullException.ThrowIfNull( rig );
		ArgumentNullException.ThrowIfNull( weights );

		if ( !RigAxes.TryLateral( rig, out var lateral ) || weights.Count != mesh.Positions.Count )
			return;

		var pelvis = rig.IndexOf( rig.RootBone.Length > 0 ? rig.RootBone : "pelvis" );
		var point = pelvis >= 0 ? rig.HeadWorld( pelvis ) : Vec3.Zero;
		Vec3 Reflect( Vec3 p ) => p - lateral * (2f * Vec3.Dot( p - point, lateral ));

		var partners = MirrorPartnerMap( rig );
		var bvh = MeshBVH.Build( mesh );
		var mirror = new int[mesh.Positions.Count];

		for ( var v = 0; v < mesh.Positions.Count; v++ )
		{
			var reflected = Reflect( mesh.Positions[v] );
			var hit = bvh.NearestSurface( mesh, reflected, float.MaxValue );
			var best = v;
			var bestD2 = float.MaxValue;

			if ( hit is { } h )
			{
				foreach ( var vi in mesh.Faces[h.FaceIndex].Indices )
				{
					var d2 = (mesh.Positions[vi] - reflected).LengthSquared;

					if ( d2 < bestD2 )
					{
						bestD2 = d2;
						best = vi;
					}
				}
			}
			else
			{
				for ( var i = 0; i < mesh.Positions.Count; i++ )
				{
					var d2 = (mesh.Positions[i] - reflected).LengthSquared;

					if ( d2 < bestD2 )
					{
						bestD2 = d2;
						best = i;
					}
				}
			}

			mirror[v] = best;
		}

		for ( var v = 0; v < weights.Count; v++ )
		{
			var swapped = SwapSides( weights[mirror[v]], partners );
			weights[v] = SkinWeights.Prune( SkinWeights.Blend( (weights[v], 0.5f), (swapped, 0.5f) ), 4 );
		}
	}

	// --- additions --------------------------------------------------------------------------------

	/// <summary>
	/// Citizen finger chains off each hand: index/middle/ring × 3 segments and thumb
	/// × 3, in citizen names (<c>finger_index_0_L</c> …). The knuckles spread along
	/// the palm (hand direction × facing) from the hand's tail; the thumb splays
	/// forward. Idempotent: a finger that already exists is left alone.
	/// </summary>
	public static void AddFingers( Skeleton rig )
	{
		ArgumentNullException.ThrowIfNull( rig );

		var facing = RigAxes.Facing( rig );

		foreach ( var side in new[] { "L", "R" } )
		{
			var hand = rig.IndexOf( $"hand_{side}" );

			if ( hand < 0 )
				continue;

			var handHead = rig.HeadWorld( hand );
			var handTail = rig.TailWorld( hand );
			var handDir = (handTail - handHead).Normal;
			var handLen = (handTail - handHead).Length;

			if ( handLen < 1e-6f || handDir.LengthSquared < 0.5f )
				continue;

			// Spread axis across the palm; the index side is the thumb side, which
			// is -spread for the left hand and +spread for the right (palms face
			// backward in the rest pose, thumbs forward — see the class notes).
			var spread = Vec3.Cross( handDir, facing ).Normal;
			var sideSign = side == "L" ? -1f : 1f;
			var step = handLen * 0.32f;

			if ( spread.LengthSquared < 0.5f )
				continue;

			var fingers = new[] { ("index", 1.5f), ("middle", 0.5f), ("ring", -0.5f) };

			foreach ( var (finger, factor) in fingers )
			{
				var root = handTail + spread * (sideSign * factor * step);
				var tip = root + handDir * (handLen * 1.1f);
				AddFingerChain( rig, hand, $"finger_{finger}", side, root, tip );
			}

			var thumbRoot = handHead + handDir * (handLen * 0.55f) + facing * (handLen * 0.35f) + spread * (sideSign * 1.0f * step);
			var thumbTip = thumbRoot + (handDir + facing * 0.9f).Normal * (handLen * 0.85f);
			AddFingerChain( rig, hand, "finger_thumb", side, thumbRoot, thumbTip );
		}
	}

	static void AddFingerChain( Skeleton rig, int hand, string finger, string side, Vec3 root, Vec3 tip )
	{
		// Chain names the bones "<finger>_<side>_00…" (e.g. finger_index_L_00);
		// citizen names index the segments 0/1/2, so rename them in place.
		if ( rig.IndexOf( $"{finger}_0_{side}" ) >= 0 )
			return;

		var joints = RigBuild.Chain( rig, $"{finger}_{side}", hand, new[] { root, tip }, 3 );

		for ( var k = 0; k < joints.Count; k++ )
			rig.RenameBone( joints[k], $"{finger}_{k}_{side}" );
	}

	/// <summary>One toe per foot, off the ball along the foot's direction. Citizen has
	/// no toe bones (retarget maps a source toe onto the ball); these are additive
	/// DEF bones for the animator, named in the map's own vocabulary.</summary>
	public static void AddToes( Skeleton rig )
	{
		ArgumentNullException.ThrowIfNull( rig );

		var facing = RigAxes.Facing( rig );

		foreach ( var side in new[] { "L", "R" } )
		{
			if ( rig.IndexOf( $"toe_{side}" ) >= 0 )
				continue;

			var ball = rig.IndexOf( $"ball_{side}" );

			if ( ball < 0 )
				continue;

			var head = rig.TailWorld( ball );
			var len = (rig.TailWorld( ball ) - rig.HeadWorld( ball )).Length;

			if ( len < 1e-6f )
				continue;

			var tip = head + facing * (len * 0.7f);
			rig.AddBoneFromPoints( $"toe_{side}", ball, head, tip );
		}
	}

	/// <summary>Two citizen twist stations per limb bone, via <see cref="RigBuild.AddTwist"/>.</summary>
	public static void AddTwists( Skeleton rig )
	{
		ArgumentNullException.ThrowIfNull( rig );

		foreach ( var side in new[] { "L", "R" } )
		{
			foreach ( var limb in TwistLimbs )
			{
				var bone = rig.IndexOf( $"{limb}_{side}" );

				if ( bone < 0 || rig.IndexOf( $"{limb}_{side}_twist0" ) >= 0 )
					continue;

				RigBuild.AddTwist( rig, bone, 2 );
			}
		}
	}

	/// <summary>
	/// Five Deform=false placement targets — hands, feet, head — parented to the
	/// pelvis so they ride the root. They carry no skin weights (Rig Check enforces
	/// that) and no drivers yet: the IK/FK drivers that couple them to the DEF chain
	/// belong to the godlike-ik-fk task. What they are is the control rig's skeleton:
	/// named targets an animator can grab, on bones the exporter ignores.
	/// </summary>
	public static void AddControls( Skeleton rig )
	{
		ArgumentNullException.ThrowIfNull( rig );

		var pelvis = rig.IndexOf( rig.RootBone.Length > 0 ? rig.RootBone : "pelvis" );

		if ( pelvis < 0 )
			return;

		var size = Math.Max( 1f, RigAxes.Height( rig ) * 0.05f );

		foreach ( var (name, source) in new[] { ("ctrl_hand_L", "hand_L"), ("ctrl_hand_R", "hand_R"), ("ctrl_foot_L", "ankle_L"), ("ctrl_foot_R", "ankle_R"), ("ctrl_head", "head") } )
		{
			if ( rig.IndexOf( name ) >= 0 )
				continue;

			var at = rig.IndexOf( source );

			if ( at < 0 )
				continue;

			var head = rig.HeadWorld( at );
			var idx = rig.AddBoneFromPoints( name, pelvis, head, head + new Vec3( 0, 0, size ) );
			rig.Bones[idx].Deform = false;
		}
	}

	// --- weights ----------------------------------------------------------------------------------

	static SkinWeights ReweightDistance( PolyMesh mesh, Skeleton rig )
	{
		var min = mesh.Positions[0];
		var max = mesh.Positions[0];

		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}

		// Same shape as AutoRigger.WeightsBySegment: inverse distance to the nearest
		// bone segments, normalised, pruned to 4. Twist and control bones are excluded
		// here — twists get their share from TwistWeights.Spread afterwards, and
		// controls must never carry weight.
		var falloff = 0.12f * Math.Max( 1e-6f, (max - min).Length );
		var deform = Enumerable.Range( 0, rig.Count )
			.Where( b => rig.Bones[b].Deform && !RigBuild.IsTwistBone( rig.Bones[b].Name ) )
			.ToArray();
		var heads = deform.ToDictionary( b => b, b => rig.HeadWorld( b ) );
		var tails = deform.ToDictionary( b => b, b => rig.TailWorld( b ) );

		var weights = new SkinWeights( mesh.Positions.Count );

		for ( var v = 0; v < mesh.Positions.Count; v++ )
		{
			var acc = new Dictionary<int, float>();

			foreach ( var b in deform )
			{
				var d = SkinBinder.DistanceToSegment( mesh.Positions[v], heads[b], tails[b] );
				var q = d / falloff;
				acc[b] = 1f / (1f + q * q);
			}

			weights[v] = SkinWeights.Prune( SkinWeights.Finish( acc ), 4 );
		}

		return weights;
	}

	static void StripNonDefWeights( Skeleton rig, SkinWeights weights )
	{
		for ( var v = 0; v < weights.Count; v++ )
		{
			var kept = weights[v].Where( bw => bw.Bone >= 0 && bw.Bone < rig.Count && rig.Bones[bw.Bone].Deform && !RigBuild.IsTwistBone( rig.Bones[bw.Bone].Name ) ).ToArray();

			if ( kept.Length != weights[v].Length )
				weights[v] = SkinWeights.Prune( kept, 4 );
		}
	}

	static BoneWeight[] SwapSides( BoneWeight[] weights, int[] partners )
	{
		var swapped = new BoneWeight[weights.Length];

		for ( var i = 0; i < weights.Length; i++ )
		{
			var bone = weights[i].Bone;
			swapped[i] = new BoneWeight( bone >= 0 && bone < partners.Length ? partners[bone] : bone, weights[i].Weight );
		}

		return swapped;
	}

	static Vec3 PosedDirection( Skeleton rig, Xform[] poseWorld, int bone )
	{
		var origin = poseWorld[bone].Origin;
		var best = Vec3.Zero;
		var bestLen = 0f;

		foreach ( var c in rig.Children( bone ) )
		{
			var v = poseWorld[c].Origin - origin;
			var len = v.Length;

			if ( len > bestLen )
			{
				bestLen = len;
				best = v;
			}
		}

		if ( bestLen > 1e-4f )
			return best / bestLen;

		return poseWorld[bone].Y.Normal;
	}
}
