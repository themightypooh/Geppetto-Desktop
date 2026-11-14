using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>Which skinning a preview uses. LBS is what exports (glTF and every engine skin
/// with linear blend skinning); dual-quaternion is the PREVIEW that shows what the deformation
/// would look like without the candy-wrap collapse — see
/// <see cref="DualQuaternionSkinning"/>. Ship LBS-correct weights: DQS never exports, so a rig
/// that only looks right under DQS is a rig that ships broken.</summary>
public enum SkinMethod
{
	Lbs,
	DualQuaternion,
}

/// <summary>One vertex the stress test caught collapsing: where, in which pose, and how badly.</summary>
public sealed class PinchPoint
{
	public string Pose { get; }
	public int Vertex { get; }
	public float Severity { get; }
	public PinchPoint( string pose, int vertex, float severity )
		{ Pose = pose; Vertex = vertex; Severity = severity; }
}

/// <summary>What a deformation stress run found: every pose tried, how many vertices pinched,
/// the worst one, and the worst 100 to highlight.</summary>
public sealed class StressReport
{
	public List<string> Poses { get; } = new();
	public int TotalPinched { get; set; }
	public PinchPoint Worst { get; set; }
	public List<PinchPoint> Pinches { get; } = new();
}

/// <summary>Deformation stress test: take a rigged mesh, put it through the poses that break
/// rigs, and report where the surface collapses.
///
/// A pinch is measured, not eyeballed: for each vertex, the SHORTEST of its edges in the
/// posed mesh against the same edge at rest. When a ring of vertices folds through itself —
/// the classic candy-wrap at a shoulder or a hinge fold at a knee — the edges crossing the
/// fold shorten sharply while the edges along the limb stay rigid; averaging them would hide
/// the fold (a knee quad folded 120° averages to a 0.77 ratio and reads as fine, while its
/// shortest edge is at 0.66). A ratio under 0.75 is a pinch, and the severity is how far
/// under it the vertex fell; the worst 100 are the ones to highlight in red.
///
/// The poses are the ones that break rigs in practice: a deep squat (knees and hips at their
/// limit), an overhead reach (shoulders and elbows at their limit), and a fist (every finger
/// curled). DQS exists to fix exactly these; run the same mesh both ways and the report is
/// the acceptance test.</summary>
public static class DeformationStress
{
	/// <summary>Pose scripts as (bone, axis, degrees): a deep squat — hips and knees bent,
	/// arms folded forward.</summary>
	public static List<(string Bone, Vec3 Axis, float Degrees)> DeepSquat() => new()
	{
		("leg_upper_L", new Vec3( 1, 0, 0 ), 100f),
		("leg_upper_R", new Vec3( 1, 0, 0 ), 100f),
		("leg_lower_L", new Vec3( 1, 0, 0 ), 120f),
		("leg_lower_R", new Vec3( 1, 0, 0 ), 120f),
		("spine_0", new Vec3( 1, 0, 0 ), 30f),
		("arm_upper_L", new Vec3( 1, 0, 0 ), 60f),
		("arm_upper_R", new Vec3( 1, 0, 0 ), 60f),
	};

	/// <summary>Pose scripts as (bone, axis, degrees): an overhead reach — both arms up past
	/// the head, elbows bent.</summary>
	public static List<(string Bone, Vec3 Axis, float Degrees)> OverheadReach() => new()
	{
		("arm_upper_L", new Vec3( 0, 0, 1 ), 150f),
		("arm_upper_R", new Vec3( 0, 0, 1 ), -150f),
		("arm_lower_L", new Vec3( 0, 0, 1 ), 60f),
		("arm_lower_R", new Vec3( 0, 0, 1 ), -60f),
		("spine_1", new Vec3( 1, 0, 0 ), -15f),
	};

	/// <summary>Pose scripts as (bone, axis, degrees): a fist — every finger joint curled,
	/// thumbs across the palm.</summary>
	public static List<(string Bone, Vec3 Axis, float Degrees)> Fist() => new()
	{
		("finger_index_0_L", new Vec3( 0, 0, 1 ), 70f),
		("finger_index_1_L", new Vec3( 0, 0, 1 ), 80f),
		("finger_index_2_L", new Vec3( 0, 0, 1 ), 60f),
		("finger_middle_0_L", new Vec3( 0, 0, 1 ), 70f),
		("finger_middle_1_L", new Vec3( 0, 0, 1 ), 80f),
		("finger_middle_2_L", new Vec3( 0, 0, 1 ), 60f),
		("finger_ring_0_L", new Vec3( 0, 0, 1 ), 70f),
		("finger_ring_1_L", new Vec3( 0, 0, 1 ), 80f),
		("finger_ring_2_L", new Vec3( 0, 0, 1 ), 60f),
		("finger_thumb_0_L", new Vec3( 0, 0, 1 ), 45f),
		("finger_thumb_1_L", new Vec3( 0, 0, 1 ), 45f),
	};

	/// <summary>Apply a pose script to a rest skeleton: each named bone's local transform is
	/// multiplied by a rotation about the given axis (local space, through the bone head).
	/// Bones the skeleton does not have are skipped, not failed — a fist script on a rig
	/// without fingers still tests the wrist and arm.</summary>
	public static Skeleton PoseSkeleton( Skeleton bind, IEnumerable<(string Bone, Vec3 Axis, float Degrees)> rotations )
	{
		if ( bind is null )
			throw new ArgumentNullException( nameof( bind ) );
		if ( rotations is null )
			throw new ArgumentNullException( nameof( rotations ) );
		var posed = bind.Clone();
		foreach ( var (name, axis, degrees) in rotations )
		{
			var index = posed.IndexOf( name );
			if ( index < 0 )
				continue;
			var bone = posed.Bones[index];
			bone.Local = bone.Local * Xform.Rotate( axis, degrees * MathF.PI / 180f );
		}
		return posed;
	}

	/// <summary>Run the three stress poses on a rigged mesh and report the pinches. The mesh
	/// is posed with the given <paramref name="method"/> (LBS or DQS preview), so the same
	/// call run twice is the DQS acceptance test: the shoulder that pinches under LBS should
	/// not pinch under DQS.</summary>
	public static StressReport Run( PolyMesh mesh, Skeleton bind, SkinMethod method = SkinMethod.Lbs )
		=> Run( mesh, bind, method, new (string, List<(string, Vec3, float)>)[]
		{
			("deep-squat", DeepSquat()),
			("overhead-reach", OverheadReach()),
			("fist", Fist()),
		} );

	/// <summary>Run custom pose scripts — (name, rotations) pairs — on a rigged mesh and
	/// report the pinches. The DQS acceptance test uses this with a single 180° shoulder
	/// roll: the pose the built-ins never produce, and the one that candy-wraps under LBS.</summary>
	public static StressReport Run( PolyMesh mesh, Skeleton bind, SkinMethod method,
		IEnumerable<(string Name, List<(string Bone, Vec3 Axis, float Degrees)> Script)> poses )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );
		if ( bind is null )
			throw new ArgumentNullException( nameof( bind ) );
		if ( !mesh.IsRigged )
			throw new ArgumentException( "The mesh has no skin weights, so there is nothing to deform." );
		if ( mesh.Positions.Count == 0 )
			throw new ArgumentException( "The mesh has no vertices." );
		if ( poses is null )
			throw new ArgumentNullException( nameof( poses ) );

		var report = new StressReport();
		var neighbours = Neighbours( mesh );
		var bindWorld = PoseSpaceCorrective.World( bind );
		foreach ( var (name, script) in poses )
		{
			report.Poses.Add( name );
			var posed = PoseSkeleton( bind, script );
			var poseWorld = PoseSpaceCorrective.World( posed );
			Vec3[] deformed = method == SkinMethod.DualQuaternion
				? DualQuaternionSkinning.Deform( mesh.Positions, mesh.Skin, bindWorld, poseWorld, out _, out _ )
				: SkinBinder.Deform( mesh.Positions, mesh.Skin, bindWorld, poseWorld );
			var ratios = MinEdgeRatios( mesh.Positions, deformed, neighbours );
			for ( var v = 0; v < deformed.Length; v++ )
			{
				var ratio = ratios[v];
				if ( ratio >= 0.75f )
					continue;
				var severity = (0.75f - ratio) / 0.75f;
				report.TotalPinched++;
				report.Pinches.Add( new PinchPoint( name, v, severity ) );
				if ( report.Worst is null || severity > report.Worst.Severity )
					report.Worst = new PinchPoint( name, v, severity );
			}
		}
		// Worst first; the list is the highlight set, capped at 100.
		report.Pinches.Sort( (a, b) => b.Severity.CompareTo( a.Severity ) );
		if ( report.Pinches.Count > 100 )
			report.Pinches.RemoveRange( 100, report.Pinches.Count - 100 );
		return report;
	}

	static List<int>[] Neighbours( PolyMesh mesh )
	{
		var neighbours = new List<int>[mesh.Positions.Count];
		for ( var i = 0; i < neighbours.Length; i++ )
			neighbours[i] = new List<int>();
		foreach ( var face in mesh.Faces )
		{
			for ( var i = 0; i < face.Count; i++ )
			{
				var a = face.Indices[i];
				var b = face.Indices[(i + 1) % face.Count];
				if ( a == b || a < 0 || b < 0 || a >= neighbours.Length || b >= neighbours.Length )
					continue;
				if ( !neighbours[a].Contains( b ) )
					neighbours[a].Add( b );
				if ( !neighbours[b].Contains( a ) )
					neighbours[b].Add( a );
			}
		}
		return neighbours;
	}

	/// <summary>Per vertex, the smallest posed/rest edge-length ratio over its edges: 1
	/// when nothing moved, 0 when an edge collapsed. A vertex with no edges, or only
	/// zero-length rest edges, reports 1 (nothing to fold).</summary>
	static float[] MinEdgeRatios( IReadOnlyList<Vec3> rest, IReadOnlyList<Vec3> posed, List<int>[] neighbours )
	{
		var ratios = new float[rest.Count];
		for ( var v = 0; v < rest.Count; v++ )
		{
			if ( neighbours[v].Count == 0 ) { ratios[v] = 1f; continue; }
			var min = float.MaxValue;
			foreach ( var n in neighbours[v] )
			{
				var restLen = (rest[n] - rest[v]).Length;
				if ( restLen <= 0f )
					continue;
				min = MathF.Min( min, (posed[n] - posed[v]).Length / restLen );
			}
			ratios[v] = min == float.MaxValue ? 1f : min;
		}
		return ratios;
	}
}
