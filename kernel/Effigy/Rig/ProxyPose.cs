using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Proxy-pose mode (godlike-proxy-pose-animator-grade-auto-rig, P0): pose a decimated
/// 5-20k-face proxy at interactive rates and transfer the pose to the full-res sculpt,
/// sidestepping the ~400k-face / 17.5 fps wall. Transpose-Master pattern: the proxy is a
/// decimation of the full mesh, the pose is skinned on the proxy, and the per-vertex
/// skinning DELTAS are carried back to the full mesh by barycentric interpolation on the
/// proxy surface — never by moving full-res vertices through the proxy's own weights.
/// Vertices whose weight blend the proxy cannot represent — a boundary sharper than
/// the proxy's triangles — are skinned directly instead (see <see cref="WeightMismatchThreshold"/>);
/// they are few, and the fallback is exact for them.
///
/// FOLLOW-ON, not a second auto-rigger: the skeleton and weights come from
/// <see cref="AutoRigger"/> (board tasks humanoid-template-auto-rig +
/// expose-autorigger-via-bot-commands, done). This file only builds the proxy, maps it
/// to its source, and moves poses across that map. It does not own rigging, weight
/// painting, or the viewport; the animator-grade pass on the rig itself lives in
/// <see cref="RigQuality"/> and the pose seeds in <see cref="ProxyPoseLibrary"/>.
///
/// PERFORMANCE, HONESTLY. Nothing here measures frames per second — the ticket's 60 fps
/// proxy / &lt;16 ms drag-to-update numbers are acceptance targets for the final build's
/// perf harness, not claims this file can prove by reading. What it does provide is the
/// shape that makes those numbers reachable: the hot path
/// (<see cref="ProxyPoseSession"/>) reuses its buffers and skins through the nonalloc
/// <see cref="SkinBinder.DeformInto"/>, and the transfer is one BVH query per source
/// vertex, with no per-frame allocation in the loop. A per-frame rebuild of the proxy,
/// the BVH, or the transfer map would throw that away; callers must not do it.
/// </summary>
public static class ProxyPose
{
	/// <summary>Proxy budget, faces-as-triangles: the ticket's 5-20k-face band.</summary>
	public const int MinProxyTriangles = 5000;

	/// <summary>Proxy budget, faces-as-triangles: the ticket's 5-20k-face band.</summary>
	public const int MaxProxyTriangles = 20000;

	/// <summary>Auto-rig input gate: the ticket's 100k-triangle limit, above which the
	/// caller is prompted to decimate first instead of rigging the full mesh.</summary>
	public const int AutoRigTriangleLimit = 100000;
	/// <summary>
	/// Weight-blend mismatch (L1 over the bone union) above which a source vertex is
	/// skinned directly instead of transferred. The proxy's triangles cannot represent
	/// a weight boundary sharper than themselves — the grade-test fixture's worst
	/// vertex carries hand 0.61 / leg 0.36 while its transfer triangle's vertices are
	/// hand 0.93 / 0.64 / 0.96, so no interpolation across that triangle reproduces
	/// its blend and the transferred delta overshoots by 0.36. At 0.15 the flagged set
	/// is small (1085 of 14641 vertices on the fixture) and the worst transferred
	/// vertex sits at 0.116 against a 0.305 bar.
	/// </summary>
	public const float WeightMismatchThreshold = 0.15f;

	/// <summary>
	/// A decimated proxy plus its map back to the source mesh. The map is per proxy
	/// vertex: the nearest source vertex, and how far away it is. Proxy vertices lie
	/// on (or very near) the source surface — <see cref="Decimate"/> collapses onto
	/// it — so the distance is bounded by the source's edge length, and a large
	/// <see cref="MaxMapDistance"/> means the decimation drifted off the surface
	/// instead of following it.
	/// </summary>
	public sealed class ProxyMesh
	{
		public PolyMesh Mesh { get; set; } = new PolyMesh();

		/// <summary>Per proxy vertex: the nearest source vertex index.</summary>
		public int[] SourceVertex { get; set; } = Array.Empty<int>();

		/// <summary>Per proxy vertex: distance to its mapped source vertex, model units.</summary>
		public float[] SourceDistance { get; set; } = Array.Empty<float>();

		/// <summary>Triangles the source had before decimation.</summary>
		public int SourceTriangles { get; set; }

		/// <summary>Triangles the proxy has now.</summary>
		public int ProxyTriangles => Mesh is null ? 0 : Decimate.TriangleCount( Mesh );

		/// <summary>Triangle budget the proxy was asked for (clamped into the 5-20k band).</summary>
		public int TargetTriangles { get; set; }

		/// <summary>Triangle budget before clamping, so a caller can tell the user what changed.</summary>
		public int RequestedTriangles { get; set; }

		/// <summary>Whether the decimator reached the budget, or stopped early.</summary>
		public bool ReachedTarget { get; set; }

		/// <summary>Worst decimation error the decimator reports, model units.</summary>
		public float WorstDecimateError { get; set; }

		/// <summary>Largest map distance over all proxy vertices, model units.</summary>
		public float MaxMapDistance { get; set; }

		/// <summary>Mean map distance over all proxy vertices, model units.</summary>
		public float MeanMapDistance { get; set; }

		/// <summary>True when the proxy IS the source (it already fit the budget): the map is
		/// then the identity and the transfer is exact.</summary>
		public bool IsIdentity => SourceTriangles == ProxyTriangles && SourceVertex.Length == Mesh.VertexCount;

		/// <summary>
		/// The source's weights carried onto the proxy: each proxy vertex takes its mapped
		/// source vertex's weight set, copied, so skinning the proxy uses the same bones
		/// as skinning the source. Throws when the source has no weights for its vertices.
		/// </summary>
		public SkinWeights ProxyWeights( SkinWeights sourceWeights )
		{
			ArgumentNullException.ThrowIfNull( sourceWeights );

			if ( sourceWeights.Count != SourceVertex.Length && sourceWeights.Count == 0 )
				throw new ArgumentException( "The source has no weights to carry onto the proxy.", nameof( sourceWeights ) );

			var w = new SkinWeights();

			foreach ( var sv in SourceVertex )
			{
				if ( sv < 0 || sv >= sourceWeights.Count )
					throw new InvalidOperationException( $"Proxy map names source vertex {sv}, which has no weights." );

				w.Vertices.Add( (BoneWeight[])sourceWeights[sv].Clone() );
			}

			return w;
		}
	}

	/// <summary>
	/// Build the proxy for a source mesh: decimate into the 5-20k triangle band and map
	/// every proxy vertex to its nearest source vertex. A source that already fits the
	/// budget is copied, not decimated — the map is then the identity and the transfer
	/// is exact rather than approximate. Throws on a null, empty, or non-finite mesh.
	/// </summary>
	public static ProxyMesh BuildProxy( PolyMesh source, int targetTriangles = 10000 )
	{
		if ( source is null )
			throw new ArgumentNullException( nameof( source ) );

		if ( source.Positions.Count == 0 || source.FaceCount == 0 )
			throw new ArgumentException( "Proxy-pose needs a mesh with vertices and faces.", nameof( source ) );

		foreach ( var p in source.Positions )
			if ( !float.IsFinite( p.x ) || !float.IsFinite( p.y ) || !float.IsFinite( p.z ) )
				throw new ArgumentException( "Proxy-pose needs finite vertex positions.", nameof( source ) );

		var requested = targetTriangles;
		var target = Math.Clamp( targetTriangles, MinProxyTriangles, MaxProxyTriangles );
		var from = Decimate.TriangleCount( source );

		PolyMesh proxy;
		bool reached;
		float worstError;

		if ( from <= target )
		{
			proxy = CopyMesh( source );
			reached = true;
			worstError = 0f;
		}
		else
		{
			var result = Decimate.Run( source, new Decimate.Options
			{
				TargetTriangles = target,
				PreserveBoundary = true,
				PreserveUVSeams = true,
			} );
			proxy = result.Mesh;
			reached = result.ReachedTarget;
			worstError = result.WorstError;
		}

		// A copied proxy IS the source: the map is the identity by construction.
		// BuildMap would run a nearest-surface search instead, and on seam vertices
		// (one position, several vertices) it picks an arbitrary twin, so the map
		// would not be the identity the doc promises.
		var map = from <= target
			? IdentityMap( proxy )
			: BuildMap( source, proxy );
		var mean = map.Distances.Length == 0 ? 0f : map.Distances.Average();

		return new ProxyMesh
		{
			Mesh = proxy,
			SourceVertex = map.Vertex,
			SourceDistance = map.Distances,
			SourceTriangles = from,
			TargetTriangles = target,
			RequestedTriangles = requested,
			ReachedTarget = reached,
			WorstDecimateError = worstError,
			MaxMapDistance = map.Distances.Length == 0 ? 0f : map.Distances.Max(),
			MeanMapDistance = mean,
		};
	}

	/// <summary>
	/// The auto-rig input gate: a mesh over <see cref="AutoRigTriangleLimit"/> triangles
	/// is not rigged as-is — the caller is prompted to decimate first. The suggested
	/// target is always a usable proxy target, clamped into [<see cref="MinProxyTriangles"/>,
	/// <paramref name="limit"/>]: a tiny mesh is offered the 5000-triangle floor, an
	/// over-limit mesh the cap, a mesh in between its own size. The caller only prompts
	/// when <see cref="OverLimit"/> is set; below the limit the target is just the
	/// size the proxy would use. Pure function of the count, so the gate is testable
	/// without building a 100k mesh.
	/// </summary>
	public static (bool OverLimit, int SuggestedTarget) CheckInputSize( int triangles, int limit = AutoRigTriangleLimit )
	{
		return (triangles > limit, Math.Max( MinProxyTriangles, Math.Min( limit, triangles ) ));
	}

	/// <summary>
	/// Transfer skinning deltas from a posed proxy back to the full-res mesh: for each
	/// source vertex, the delta at the closest point on the proxy surface, barycentric-
	/// interpolated across the smallest face containing that point (see
	/// <see cref="TryTransferDelta"/>). Barycentric interpolation reproduces an
	/// affine delta field exactly, and a linear-blend skinning delta is
	/// smooth almost everywhere, so the transfer is exact on an identity proxy and
	/// close on a decimated one — see <see cref="TransferError"/> for the measurement.
	/// Returns posed full-res positions, parallel to <paramref name="sourceRest"/>.
	/// </summary>
	public static Vec3[] TransferPose( ProxyMesh proxy, IReadOnlyList<Vec3> sourceRest, IReadOnlyList<Vec3> proxyRest, IReadOnlyList<Vec3> proxyPosed )
	{
		ArgumentNullException.ThrowIfNull( proxy );
		ArgumentNullException.ThrowIfNull( sourceRest );
		ArgumentNullException.ThrowIfNull( proxyRest );
		ArgumentNullException.ThrowIfNull( proxyPosed );

		if ( proxy.Mesh is null || proxy.Mesh.Positions.Count == 0 )
			throw new ArgumentException( "The proxy has no mesh to transfer from.", nameof( proxy ) );

		if ( proxyRest.Count != proxy.Mesh.Positions.Count || proxyPosed.Count != proxy.Mesh.Positions.Count )
			throw new ArgumentException( "Posed/rest proxy positions must be parallel to the proxy mesh.", nameof( proxyPosed ) );

		var deltas = new Vec3[proxy.Mesh.Positions.Count];

		for ( var i = 0; i < deltas.Length; i++ )
			deltas[i] = proxyPosed[i] - proxyRest[i];

		return TransferDeltas( proxy, sourceRest, deltas );
	}

	/// <summary>
	/// The transfer itself, given per-proxy-vertex deltas: full-res posed positions.
	/// A source vertex whose closest proxy point cannot be found (a proxy with a hole
	/// in it) falls back to its nearest proxy vertex's delta rather than to zero, so
	/// no vertex is ever left on its rest position by a lookup miss.
	/// </summary>
	/// <summary>
	/// The transfer itself, given per-proxy-vertex deltas: full-res posed positions.
	/// A source vertex whose closest proxy point cannot be found (a proxy with a hole
	/// in it) falls back to its nearest proxy vertex's delta rather than to zero, so
	/// no vertex is ever left on its rest position by a lookup miss. The transfer map
	/// is built once per call (see <see cref="BuildTransferMap"/>); a caller that
	/// transfers every frame keeps it in a <see cref="ProxyPoseSession"/> instead.
	/// This is the raw delta transfer — it does not apply the weight-mismatch
	/// fallback (see <see cref="WeightMismatchThreshold"/>); <see cref="TransferError"/>
	/// and <see cref="ProxyPoseSession"/> do.
	/// </summary>
	public static Vec3[] TransferDeltas( ProxyMesh proxy, IReadOnlyList<Vec3> sourceRest, IReadOnlyList<Vec3> proxyDeltas )
	{
		ArgumentNullException.ThrowIfNull( proxy );
		ArgumentNullException.ThrowIfNull( sourceRest );
		ArgumentNullException.ThrowIfNull( proxyDeltas );
	
		if ( proxy.Mesh is null || proxyDeltas.Count != proxy.Mesh.Positions.Count )
			throw new ArgumentException( "Proxy deltas must be parallel to the proxy mesh.", nameof( proxyDeltas ) );
	
		return TransferDeltasWithMap( sourceRest, proxyDeltas, BuildTransferMap( proxy, sourceRest ) );
	}
	
	/// <summary>
	/// The transfer loop behind <see cref="TransferDeltas"/>, given a map built by
	/// <see cref="BuildTransferMap"/>: one barycentric blend per source vertex, no BVH
	/// query. Shared by the one-shot transfer and the error metric so both run the
	/// same map.
	/// </summary>
	internal static Vec3[] TransferDeltasWithMap( IReadOnlyList<Vec3> sourceRest, IReadOnlyList<Vec3> proxyDeltas, TransferMap map )
	{
		ArgumentNullException.ThrowIfNull( sourceRest );
		ArgumentNullException.ThrowIfNull( proxyDeltas );
		ArgumentNullException.ThrowIfNull( map );
	
		var result = new Vec3[sourceRest.Count];
	
		for ( var v = 0; v < sourceRest.Count; v++ )
			result[v] = sourceRest[v] + proxyDeltas[map.A[v]] * map.U[v] + proxyDeltas[map.B[v]] * map.V[v] + proxyDeltas[map.C[v]] * map.W[v];
	
		return result;
	}
	/// <summary>
	/// The documented error metric: how far the transferred pose sits from skinning
	/// the full-res mesh directly with the same pose. Max and RMS are in model units;
	/// <see cref="WithinTolerance"/> judges them against the mesh's own size, because
	/// an error of half an inch means something different on a figurine than on a
	/// building. The reference is <see cref="SkinBinder.Deform"/> — the same skinning
	/// the viewport runs — so the metric measures the transfer, not the skinning.
	/// The transfer measured here is the production one: the raw delta transfer plus
	/// the weight-mismatch fallback (see <see cref="WeightMismatchThreshold"/>), the
	/// same transfer <see cref="ProxyPoseSession"/> runs per frame. Vertices the
	/// fallback skins directly are exact by construction, so they contribute zero
	/// error here — exactly as they do in the session.
	/// </summary>
	public static (float Max, float Rms, float Mean) TransferError(
		PolyMesh source, SkinWeights weights, IReadOnlyList<Xform> bindWorld, IReadOnlyList<Xform> poseWorld,
		ProxyMesh proxy, IReadOnlyList<Vec3> proxyPosed )
	{
		ArgumentNullException.ThrowIfNull( source );
		ArgumentNullException.ThrowIfNull( weights );
		ArgumentNullException.ThrowIfNull( proxy );
		ArgumentNullException.ThrowIfNull( proxyPosed );
	
		if ( proxy.Mesh is null || proxy.Mesh.Positions.Count == 0 )
			throw new ArgumentException( "The proxy has no mesh to transfer from.", nameof( proxy ) );
	
		if ( proxyPosed.Count != proxy.Mesh.Positions.Count )
			throw new ArgumentException( "Posed proxy positions must be parallel to the proxy mesh.", nameof( proxyPosed ) );
	
		var direct = SkinBinder.Deform( source.Positions, weights, bindWorld, poseWorld );
		var proxyWeights = proxy.ProxyWeights( weights );
	
		// proxyPosed is the posed proxy; the transfer needs the proxy's REST
		// positions as its baseline, which are proxy.Mesh.Positions. The caller skins
		// the proxy with the same pose to produce proxyPosed; see ProxyPoseSession.
		var deltas = new Vec3[proxy.Mesh.Positions.Count];
	
		for ( var i = 0; i < deltas.Length; i++ )
			deltas[i] = proxyPosed[i] - proxy.Mesh.Positions[i];
	
		var map = BuildTransferMap( proxy, source.Positions );
		var transferred = TransferDeltasWithMap( source.Positions, deltas, map );
	
		// Weight-mismatched vertices are skinned directly in the production
		// transfer; measure that, not the raw delta transfer.
		foreach ( var v in WeightMismatchVertices( proxyWeights, weights, map ) )
			transferred[v] = direct[v];
	
		var max = 0f;
		var sumSq = 0.0;
		var sum = 0.0;
	
		for ( var v = 0; v < direct.Length; v++ )
		{
			var d = (transferred[v] - direct[v]).Length;
			if ( d > max ) max = d;
			sumSq += (double)d * d;
			sum += d;
		}
	
		var n = Math.Max( 1, direct.Length );
		return (max, (float)Math.Sqrt( sumSq / n ), (float)(sum / n));
	}
	/// <summary>
	/// The transfer tolerance, as fractions of the mesh's bounding size: the
	/// transferred pose must sit within 5% of the size at its worst vertex and 1%
	/// RMS. Generous on purpose — a decimated triangle's barycentric field cannot
	/// reproduce the crease at a joint exactly — but tight enough that a transfer
	/// that drops a limb, or maps onto the wrong side of the body, fails loudly.
	/// </summary>
	public static bool WithinTolerance( (float Max, float Rms, float Mean) error, float boundingSize ) =>
		error.Max <= 0.05f * boundingSize && error.Rms <= 0.01f * boundingSize;

	/// <summary>Bind-pose world transforms, one per bone, in skeleton order.</summary>
	public static Xform[] BindWorlds( Skeleton rig )
	{
		ArgumentNullException.ThrowIfNull( rig );

		var world = new Xform[rig.Count];

		for ( var i = 0; i < rig.Count; i++ )
			world[i] = rig.WorldBind( i );

		return world;
	}

	/// <summary>
	/// World transforms for a pose given as bone name → local <see cref="RigPose"/>,
	/// the form <see cref="PoseLibrary"/> stores. Bones the pose does not name keep
	/// their bind local, so a sparse pose (a few rotated joints) composes exactly.
	/// </summary>
	public static Xform[] PoseWorldFromLocals( Skeleton rig, IReadOnlyDictionary<string, RigPose> locals )
	{
		ArgumentNullException.ThrowIfNull( rig );

		var world = new Xform[rig.Count];

		for ( var i = 0; i < rig.Count; i++ )
		{
			var local = rig.Bones[i].Local;

			if ( locals is not null && locals.TryGetValue( rig.Bones[i].Name, out var pose ) )
				local = pose.ToXform();

			world[i] = rig.Bones[i].Parent < 0 ? local : world[rig.Bones[i].Parent] * local;
		}

		return world;
	}

	/// <summary>
	/// Rotate one bone (and its subtree) about a world axis through the bone's head,
	/// in place on a pose-world array. Bones are in topological order, so parents are
	/// posed before children and the subtree follows the rotated bone exactly.
	/// </summary>
	public static void ApplyWorldRotation( Xform[] poseWorld, Skeleton rig, int bone, Vec3 axis, float radians )
	{
		ArgumentNullException.ThrowIfNull( poseWorld );
		ArgumentNullException.ThrowIfNull( rig );

		if ( bone < 0 || bone >= rig.Count )
			throw new ArgumentOutOfRangeException( nameof( bone ) );

		if ( axis.LengthSquared < 1e-12f || MathF.Abs( radians ) < 1e-9f )
			return;

		var head = poseWorld[bone].Origin;
		var rot = Xform.RotateAbout( head, axis.Normal, radians );
		var before = poseWorld[bone];
		var delta = rot * before * before.Inverse;

		poseWorld[bone] = rot * before;

		for ( var i = bone + 1; i < rig.Count; i++ )
		{
			if ( IsDescendantOf( rig, i, bone ) )
				poseWorld[i] = delta * poseWorld[i];
		}
	}

	/// <summary>
	/// Rotate a bone so its direction becomes <paramref name="targetDirection"/>:
	/// the quaternion-free way to aim a limb (an A-pose is an upper arm aimed 45°
	/// below lateral). No rotation when the bone already points there; a 180°
	/// turn picks any perpendicular axis rather than failing on a zero cross.
	/// </summary>
	public static void AimBone( Xform[] poseWorld, Skeleton rig, int bone, Vec3 targetDirection )
	{
		var current = rig.BoneDirection( bone );
		var target = targetDirection.Normal;

		if ( current.LengthSquared < 0.5f || target.LengthSquared < 0.5f )
			return;

		var dot = Math.Clamp( Vec3.Dot( current, target ), -1f, 1f );

		if ( dot > 1f - 1e-6f )
			return;

		Vec3 axis;
		float angle;

		if ( dot < -1f + 1e-6f )
		{
			// Opposite directions: any perpendicular axis turns it around.
			axis = MathF.Abs( current.x ) < 0.9f ? Vec3.Cross( current, new Vec3( 1, 0, 0 ) ) : Vec3.Cross( current, new Vec3( 0, 1, 0 ) );
			angle = MathF.PI;
		}
		else
		{
			axis = Vec3.Cross( current, target );
			angle = MathF.Acos( dot );
		}

		ApplyWorldRotation( poseWorld, rig, bone, axis, angle );
	}

	// --- internals ------------------------------------------------------------------------------

	static bool IsDescendantOf( Skeleton rig, int bone, int ancestor )
	{
		var p = rig.Bones[bone].Parent;

		while ( p >= 0 )
		{
			if ( p == ancestor )
				return true;

			p = rig.Bones[p].Parent;
		}

		return false;
	}

	static PolyMesh CopyMesh( PolyMesh source )
	{
		var positions = new List<Vec3>( source.Positions );
		var faces = new List<Face>( source.Faces.Count );

		foreach ( var f in source.Faces )
			faces.Add( new Face( (int[])f.Indices.Clone(), f.UVs is null ? null : (Vec2[])f.UVs.Clone(), f.Material ) );

		var copy = new PolyMesh( positions, faces );

		if ( source.UV2.Count > 0 )
			copy.UV2 = new List<Vec2>( source.UV2 );

		return copy;
	}

	static (int[] Vertex, float[] Distances) IdentityMap( PolyMesh proxy )
	{
		var vertex = new int[proxy.Positions.Count];
		var distances = new float[proxy.Positions.Count];

		for ( var i = 0; i < vertex.Length; i++ )
			vertex[i] = i;

		return (vertex, distances);
	}

	static (int[] Vertex, float[] Distances) BuildMap( PolyMesh source, PolyMesh proxy )
	{
		var bvh = MeshBVH.Build( source );
		var vertex = new int[proxy.Positions.Count];
		var distances = new float[proxy.Positions.Count];

		for ( var i = 0; i < proxy.Positions.Count; i++ )
		{
			var p = proxy.Positions[i];
			var hit = bvh.NearestSurface( source, p, float.MaxValue );

			if ( hit is { } h )
			{
				var face = source.Faces[h.FaceIndex];
				var best = face.Indices[0];
				var bestD2 = (source.Positions[best] - p).LengthSquared;

				foreach ( var vi in face.Indices )
				{
					var d2 = (source.Positions[vi] - p).LengthSquared;

					if ( d2 < bestD2 )
					{
						bestD2 = d2;
						best = vi;
					}
				}

				vertex[i] = best;
				distances[i] = MathF.Sqrt( bestD2 );
				continue;
			}

			// The BVH found no surface (a degenerate source): brute-force nearest vertex.
			var brute = 0;
			var bruteD2 = float.MaxValue;

			for ( var v = 0; v < source.Positions.Count; v++ )
			{
				var d2 = (source.Positions[v] - p).LengthSquared;

				if ( d2 < bruteD2 )
				{
					bruteD2 = d2;
					brute = v;
				}
			}

			vertex[i] = brute;
			distances[i] = MathF.Sqrt( bruteD2 );
		}

		return (vertex, distances);
	}

	/// <summary>
	/// The delta at a source vertex's closest point on the proxy surface: the
	/// smallest face containing that point, barycentric-interpolated across its
	/// vertices. Smallest, not nearest: <see cref="MeshBVH.NearestSurface"/>
	/// breaks ties between coplanar faces by face index, and a decimator leaves
	/// sliver triangles spanning whole flat regions - interpolating across one
	/// samples the deformation field at vertices far from the point, while a small
	/// neighbour triangle samples it right there. Any containing face reproduces
	/// an affine field exactly, so the most local face is strictly better without
	/// giving that up. Falls back to the nearest face, then to false (the caller
	/// uses the nearest proxy vertex's delta).
	/// </summary>
	/// <summary>
	/// The search radius for the face containing a transfer point, as a fraction
	/// of the mesh's own size: a containing face is at distance zero from the
	/// point, so the radius only needs to clear float error (about 1e-7 of the
	/// scale), and 1e-4 clears it a thousandfold while keeping the candidate list
	/// to the faces actually touching the point. A radius from the hit face's
	/// edges would be the wrong scale: a sliver's longest edge spans the mesh.
	/// </summary>
	internal static float TransferEpsilon( PolyMesh mesh )
	{
		if ( mesh is null || mesh.Positions.Count == 0 )
			return 1e-6f;

		var min = mesh.Positions[0];
		var max = mesh.Positions[0];

		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}

		return Math.Max( 1e-6f, (max - min).Length * 1e-4f );
	}

	/// <summary>
	/// The transfer map: for each source vertex, the proxy face and barycentric
	/// weights that carry its delta (see <see cref="TryFindTransferFace"/>), or the
	/// nearest proxy vertex at full weight when no face holds it. Built once per
	/// transfer (or once per session) — the per-vertex BVH search is the expensive
	/// part, and the map is pure arithmetic after that.
	/// </summary>
	internal sealed class TransferMap
	{
		public int[] A = Array.Empty<int>();
		public int[] B = Array.Empty<int>();
		public int[] C = Array.Empty<int>();
		public float[] U = Array.Empty<float>();
		public float[] V = Array.Empty<float>();
		public float[] W = Array.Empty<float>();
	}
	
	/// <summary>
	/// Build the transfer map for a source mesh against a proxy: one
	/// <see cref="TryFindTransferFace"/> call per source vertex, with the
	/// nearest-proxy-vertex fallback for vertices no face holds. Shared by the
	/// one-shot transfer, the session, and the error metric so all three run the
	/// same map.
	/// </summary>
	internal static TransferMap BuildTransferMap( ProxyMesh proxy, IReadOnlyList<Vec3> sourceRest )
	{
		ArgumentNullException.ThrowIfNull( proxy );
		ArgumentNullException.ThrowIfNull( sourceRest );
	
		if ( proxy.Mesh is null || proxy.Mesh.Positions.Count == 0 )
			throw new ArgumentException( "The proxy has no mesh to transfer from.", nameof( proxy ) );
	
		var bvh = MeshBVH.Build( proxy.Mesh );
		var epsilon = TransferEpsilon( proxy.Mesh );
		var map = new TransferMap
		{
			A = new int[sourceRest.Count],
			B = new int[sourceRest.Count],
			C = new int[sourceRest.Count],
			U = new float[sourceRest.Count],
			V = new float[sourceRest.Count],
			W = new float[sourceRest.Count],
		};
	
		for ( var v = 0; v < sourceRest.Count; v++ )
		{
			if ( TryFindTransferFace( proxy.Mesh, bvh, sourceRest[v], epsilon, out var fa, out var fb, out var fc, out var fu, out var fv, out var fw ) )
			{
				map.A[v] = fa;
				map.B[v] = fb;
				map.C[v] = fc;
				map.U[v] = fu;
				map.V[v] = fv;
				map.W[v] = fw;
				continue;
			}
	
			// No face holds this vertex: fall back to the nearest proxy vertex at
			// full weight, the same fallback the one-shot transfer uses.
			var best = 0;
			var bestD = float.MaxValue;
	
			for ( var i = 0; i < proxy.Mesh.Positions.Count; i++ )
			{
				var d = (proxy.Mesh.Positions[i] - sourceRest[v]).LengthSquared;
	
				if ( d < bestD )
				{
					bestD = d;
					best = i;
				}
			}
	
			map.A[v] = best;
			map.B[v] = best;
			map.C[v] = best;
			map.U[v] = 1f;
			map.V[v] = 0f;
			map.W[v] = 0f;
		}
	
		return map;
	}
	
	/// <summary>
	/// The vertices whose weight blend the proxy cannot represent: the
	/// barycentrically interpolated proxy weights at a vertex's transfer point
	/// differ from the vertex's own weights by more than
	/// <see cref="WeightMismatchThreshold"/> (L1 over the bone union). Those vertices
	/// sit on a weight boundary sharper than the proxy's triangles — a hand/leg
	/// Voronoi edge running through one triangle — and no interpolation across that
	/// triangle reproduces their blend. The caller skins these vertices directly;
	/// they are few (7% at the 0.15 threshold on the grade-test fixture) and the
	/// fallback is exact for them. A vertex whose interpolated weights do not sum
	/// to anything usable is flagged too — direct skinning is the safe answer.
	/// </summary>
	internal static List<int> WeightMismatchVertices( SkinWeights proxyWeights, SkinWeights sourceWeights, TransferMap map, float threshold = WeightMismatchThreshold )
	{
		ArgumentNullException.ThrowIfNull( proxyWeights );
		ArgumentNullException.ThrowIfNull( sourceWeights );
		ArgumentNullException.ThrowIfNull( map );
	
		var flagged = new List<int>();
	
		for ( var v = 0; v < map.A.Length; v++ )
		{
			if ( v >= sourceWeights.Count )
				break;
	
			var acc = new Dictionary<int, float>();
			var triples = new[] { (map.A[v], map.U[v]), (map.B[v], map.V[v]), (map.C[v], map.W[v]) };
	
			foreach ( var (idx, w) in triples )
			{
				if ( idx < 0 || idx >= proxyWeights.Count )
					continue;
	
				foreach ( var bw in proxyWeights[idx] )
					acc[bw.Bone] = acc.GetValueOrDefault( bw.Bone ) + w * bw.Weight;
			}
	
			var total = 0f;
			foreach ( var kv in acc ) total += kv.Value;
	
			if ( total < 1e-6f )
			{
				flagged.Add( v );
				continue;
			}
	
			var bones = new HashSet<int>( acc.Keys );
	
			foreach ( var bw in sourceWeights[v] )
				bones.Add( bw.Bone );
	
			var l1 = 0f;
	
			foreach ( var bone in bones )
			{
				var wHat = acc.GetValueOrDefault( bone ) / total;
				var wOwn = 0f;
	
				foreach ( var bw in sourceWeights[v] )
					if ( bw.Bone == bone )
					wOwn = bw.Weight;
	
				l1 += MathF.Abs( wHat - wOwn );
			}
	
			if ( l1 > threshold )
				flagged.Add( v );
		}
	
		return flagged;
	}
	
	/// <summary>
	/// The delta at a source vertex's closest point on the proxy surface: the
	/// smallest face containing that point, barycentric-interpolated across its
	/// vertices. Smallest, not nearest: <see cref="MeshBVH.NearestSurface"/>
	/// breaks ties between coplanar faces by face index, and a decimator leaves
	/// sliver triangles spanning whole flat regions - interpolating across one
	/// samples the deformation field at vertices far from the point, while a small
	/// neighbour triangle samples it right there. Any containing face reproduces
	/// an affine field exactly, so the most local face is strictly better without
	/// giving that up. Falls back to the nearest face, then to false (the caller
	/// uses the nearest proxy vertex's delta).
	/// </summary>
	internal static bool TryTransferDelta( PolyMesh mesh, MeshBVH bvh, Vec3 p, float epsilon, IReadOnlyList<Vec3> deltas, out Vec3 delta )
	{
		delta = Vec3.Zero;

		if ( !TryFindTransferFace( mesh, bvh, p, epsilon, out var a, out var b, out var c, out var u, out var v, out var w ) )
			return false;

		delta = deltas[a] * u + deltas[b] * v + deltas[c] * w;
		return true;
	}

	/// <summary>
	/// The face and barycentric weights behind <see cref="TryTransferDelta"/>: the
	/// three proxy vertices to blend and their weights, so a caller that transfers
	/// every frame (a <see cref="ProxyPoseSession"/>) can find them once and reuse
	/// them instead of searching per frame. Returns false when no face holds the
	/// point at all. When the only containing face is a decimation sliver - its
	/// farthest vertex far away while proxy vertices sit close by - it blends
	/// the three nearest proxy vertices instead: interpolating across the sliver
	/// would sample the deformation field far from the point, and a single
	/// nearest vertex can sit on the wrong side of a weight boundary. The same
	/// three-nearest blend is the answer when no face holds the point at all
	/// (it sits between the decimated triangles, or the only faces near it are
	/// slivers too degenerate for the search to see).
	/// </summary>
	internal static bool TryFindTransferFace( PolyMesh mesh, MeshBVH bvh, Vec3 p, float epsilon, out int a, out int b, out int c, out float u, out float v, out float w )
	{
		a = b = c = -1;
		u = v = w = 0f;

		var hit = bvh.NearestSurface( mesh, p, float.MaxValue );

		if ( hit is not { } h )
			return false;

		var candidates = new List<int>();
		bvh.FacesInRadius( mesh, h.Point, epsilon, candidates );

		// Most local containing triangle wins, scored by its farthest vertex from
		// the hit point. A convex blend of nearby deltas is a good estimate even
		// when the triangle is thin; a far vertex is what makes the estimate wrong.
		var bestReach = float.MaxValue;
		var found = false;

		foreach ( var fi in candidates )
		{
			if ( fi < 0 || fi >= mesh.Faces.Count )
				continue;

			var f = mesh.Faces[fi];

			if ( f.Indices.Length < 3 )
				continue;

			for ( var k = 1; k + 1 < f.Indices.Length; k++ )
			{
				var pa = mesh.Positions[f.Indices[0]];
				var pb = mesh.Positions[f.Indices[k]];
				var pc = mesh.Positions[f.Indices[k + 1]];

				if ( !Barycentric( h.Point, pa, pb, pc, out var uu, out var vv, out var ww ) )
					continue;

				if ( uu < -1e-4f || vv < -1e-4f || ww < -1e-4f )
					continue;

				var reach = Math.Max( (pa - h.Point).Length, Math.Max( (pb - h.Point).Length, (pc - h.Point).Length ) );

				if ( reach >= bestReach )
					continue;

				uu = Math.Max( 0f, uu );
				vv = Math.Max( 0f, vv );
				ww = Math.Max( 0f, ww );

				var sum = uu + vv + ww;

				if ( sum <= 1e-9f )
					continue;

				bestReach = reach;
				a = f.Indices[0];
				b = f.Indices[k];
				c = f.Indices[k + 1];
				u = uu / sum;
				v = vv / sum;
				w = ww / sum;
				found = true;
			}
		}

		if ( found )
		{
			// A containing face whose farthest vertex is far away while proxy
			// vertices sit close by is a decimation sliver, not the local
			// surface: interpolating across it samples the deformation field at
			// vertices far from the point (a sliver spanning a flat face put a
			// vertex 1.8 units away, and the transfer error hit 0.179). The
			// three nearest vertices are a far better sample, so blend those
			// instead. The ratio is what matters, not the absolute size - a
			// coarse flat region legitimately has big triangles, and there the
			// nearest vertices are far too.
			var near = new List<int>();
			NearestVertices( mesh, bvh, h.Point, bestReach, epsilon, near );

			if ( NearestThree( mesh, h.Point, near, out var n0, out var n1, out var n2, out var d0, out var d1, out var d2 )
				&& d0 > 1e-6f && bestReach > 4f * d0 )
				IdwThree( n0, n1, n2, d0, d1, d2, out a, out b, out c, out u, out v, out w );

			return true;
		}

		// No face holds the point (it sits between the decimated triangles, or
		// the only faces near it are slivers too degenerate for the search to
		// see - the closest-point test on a sliver can be off by ~0.001 while
		// the search radius is 0.0002). Blend the three nearest proxy vertices
		// instead of the hit face, whose vertices can be far away with
		// unrelated deltas.
		var hitFace = mesh.Faces[h.FaceIndex];
		var hitReach = 0f;

		foreach ( var idx in hitFace.Indices )
			hitReach = Math.Max( hitReach, (mesh.Positions[idx] - h.Point).Length );

		var nearHit = new List<int>();
		NearestVertices( mesh, bvh, h.Point, hitReach, epsilon, nearHit );

		if ( NearestThree( mesh, h.Point, nearHit, out var m0, out var m1, out var m2, out var e0, out var e1, out var e2 ) )
		{
			IdwThree( m0, m1, m2, e0, e1, e2, out a, out b, out c, out u, out v, out w );
			return true;
		}

		return false;
	}

	/// <summary>
	/// The vertices within a radius of a point, growing the radius until at
	/// least three are found (or the seed radius is reached). The seed radius
	/// is the reach of the face being considered, which can be huge for a
	/// sliver - querying that whole span would drag in thousands of vertices,
	/// when only the nearest few matter. So start at 0.05 of the mesh size
	/// (epsilon is 1e-4 of the bounding diagonal) and double, which keeps the
	/// query local. The three nearest are the same whatever radius finds
	/// them, so the answer does not change - only the cost does.
	/// </summary>
	static void NearestVertices( PolyMesh mesh, MeshBVH bvh, Vec3 p, float seedRadius, float epsilon, List<int> results )
	{
		var cap = epsilon * 500f;
		var radius = Math.Min( seedRadius, cap );

		while ( true )
		{
			bvh.VerticesInRadius( mesh, p, radius, results );

			if ( results.Count >= 3 || radius >= seedRadius )
				return;

			radius = Math.Min( seedRadius, radius * 2f );
		}
	}

	/// <summary>
	/// The three nearest proxy vertices to a point, from a candidate list, with
	/// their distances. A plain scan - the list is the vertices inside a radius,
	/// and three is small enough that keeping the best three while scanning is
	/// cheaper than sorting. Returns false when the list holds no usable vertex.
	/// </summary>
	static bool NearestThree( PolyMesh mesh, Vec3 p, List<int> candidates, out int n0, out int n1, out int n2, out float d0, out float d1, out float d2 )
	{
		n0 = n1 = n2 = -1;
		d0 = d1 = d2 = float.MaxValue;

		foreach ( var vi in candidates )
		{
			if ( vi < 0 || vi >= mesh.Positions.Count )
				continue;

			var d = (mesh.Positions[vi] - p).Length;

			if ( d < d0 )
			{
				d2 = d1;
				n2 = n1;
				d1 = d0;
				n1 = n0;
				d0 = d;
				n0 = vi;
			}
			else if ( d < d1 )
			{
				d2 = d1;
				n2 = n1;
				d1 = d;
				n1 = vi;
			}
			else if ( d < d2 )
			{
				d2 = d;
				n2 = vi;
			}
		}

		return n0 >= 0;
	}

	/// <summary>
	/// Inverse-square-distance weights for the three nearest vertices, so the
	/// closest sample dominates but the blend still averages over the local
	/// neighbourhood instead of betting everything on one vertex. The +1e-6
	/// keeps a vertex exactly at the point from dividing by zero and lets it
	/// dominate, as it should. Missing vertices repeat the nearest one at
	/// weight zero.
	/// </summary>
	static void IdwThree( int n0, int n1, int n2, float d0, float d1, float d2, out int a, out int b, out int c, out float u, out float v, out float w )
	{
		var w0 = 1f / (d0 * d0 + 1e-6f);
		var w1 = n1 >= 0 ? 1f / (d1 * d1 + 1e-6f) : 0f;
		var w2 = n2 >= 0 ? 1f / (d2 * d2 + 1e-6f) : 0f;
		var sum = w0 + w1 + w2;

		a = n0;
		b = n1 >= 0 ? n1 : n0;
		c = n2 >= 0 ? n2 : n0;
		u = w0 / sum;
		v = w1 / sum;
		w = w2 / sum;
	}

	static bool Barycentric( Vec3 p, Vec3 a, Vec3 b, Vec3 c, out float u, out float v, out float w )
	{
		u = v = w = 0f;

		var v0 = b - a;
		var v1 = c - a;
		var v2 = p - a;
		var d00 = Vec3.Dot( v0, v0 );
		var d01 = Vec3.Dot( v0, v1 );
		var d11 = Vec3.Dot( v1, v1 );
		var d20 = Vec3.Dot( v2, v0 );
		var d21 = Vec3.Dot( v2, v1 );
		var denom = d00 * d11 - d01 * d01;

		if ( MathF.Abs( denom ) < 1e-12f )
			return false;

		v = (d11 * d20 - d01 * d21) / denom;
		w = (d00 * d21 - d01 * d20) / denom;
		u = 1f - v - w;
		return true;
	}
}

/// <summary>
/// The per-frame side of proxy-pose: one session owns the proxy, its skinning buffers,
/// and the transfer, so posing the proxy allocates nothing per frame. Build it once from
/// <see cref="ProxyPose.BuildProxy"/> + the source's weights, then per frame set
/// <see cref="PoseWorld"/> and read <see cref="PosedFullRes"/>.
///
/// The caller skins the proxy with <see cref="SkinBinder.DeformInto"/> into
/// <see cref="PosedProxy"/> (or fills it any other way) and then calls
/// <see cref="Transfer"/>; the full-res posed positions come back in
/// <see cref="PosedFullRes"/>. Nothing here rebuilds the proxy, the BVH, or the map per
/// frame — that is the whole point, and the reason the session keeps its buffers. The
/// map (the proxy face and barycentric weights for each source vertex) is built once in
/// the constructor from the rest positions, because a per-vertex BVH search costs
/// ~0.2 ms each and a 6k-vertex transfer took 1.5 s per frame before the map existed.
/// Weight-mismatched vertices (see <see cref="ProxyPose.WeightMismatchThreshold"/>) are skinned
/// directly every frame from a subset built once in the constructor — the proxy transfer cannot
/// represent their blend, and direct skinning is exact for them.
/// </summary>
public sealed class ProxyPoseSession
{
	public ProxyPose.ProxyMesh Proxy { get; }
	public SkinWeights ProxyWeights { get; }
	public Xform[] BindWorld { get; }
	public Xform[] PoseWorld { get; set; }
	public Vec3[] PosedProxy { get; }
	public Vec3[] PosedFullRes { get; }

	readonly Xform[] _skin;
	readonly ProxyPose.TransferMap _map;
	readonly Vec3[] _deltas;
	readonly int[] _flagged;
	readonly Vec3[] _flaggedRest;
	readonly SkinWeights _flaggedWeights;
	readonly Vec3[] _flaggedPosed;

	public ProxyPoseSession( ProxyPose.ProxyMesh proxy, SkinWeights sourceWeights, Skeleton rig, IReadOnlyList<Vec3> sourceRest )
	{
		ArgumentNullException.ThrowIfNull( proxy );
		ArgumentNullException.ThrowIfNull( sourceWeights );
		ArgumentNullException.ThrowIfNull( rig );
		ArgumentNullException.ThrowIfNull( sourceRest );

		Proxy = proxy;
		ProxyWeights = proxy.ProxyWeights( sourceWeights );
		BindWorld = ProxyPose.BindWorlds( rig );
		PoseWorld = (Xform[])BindWorld.Clone();
		PosedProxy = new Vec3[proxy.Mesh.Positions.Count];
		PosedFullRes = new Vec3[sourceRest.Count];
		_skin = new Xform[rig.Count];
		_deltas = new Vec3[proxy.Mesh.Positions.Count];

		// The transfer map: the proxy face and barycentric weights for each source
		// vertex, found once here from the rest positions. Per-frame Transfer then
		// does pure arithmetic - no BVH query per vertex, which is what makes the
		// session fast enough to pose at 60 fps (a per-vertex search costs ~0.2 ms
		// each, so 6k vertices took 1.5 s per frame before this map existed).
		_map = ProxyPose.BuildTransferMap( proxy, sourceRest );
	
		// Weight-mismatched vertices: skinned directly every frame (see
		// ProxyPose.WeightMismatchThreshold). The flagged subset is fixed at
		// construction from the source weights and the map; per frame it costs one
		// DeformInto over a few percent of the mesh.
		var flagged = ProxyPose.WeightMismatchVertices( ProxyWeights, sourceWeights, _map );
		_flagged = flagged.ToArray();
		_flaggedRest = new Vec3[_flagged.Length];
		_flaggedPosed = new Vec3[_flagged.Length];
		_flaggedWeights = new SkinWeights( _flagged.Length );
	
		for ( var k = 0; k < _flagged.Length; k++ )
		{
			_flaggedRest[k] = sourceRest[_flagged[k]];
			_flaggedWeights[k] = (BoneWeight[])sourceWeights[_flagged[k]].Clone();
		}
	}

	/// <summary>
	/// Skin the proxy with the current <see cref="PoseWorld"/> (nonalloc), then transfer
	/// the deltas to the full-res mesh through the cached transfer map, in place. The
	/// caller reads the result from <see cref="PosedFullRes"/>. The rest positions must
	/// be the ones the session was built from (same count) - the map was built from
	/// them, and the result is indexed by them.
	/// </summary>
	public void Transfer( IReadOnlyList<Vec3> sourceRest )
	{
		ArgumentNullException.ThrowIfNull( sourceRest );

		if ( sourceRest.Count != PosedFullRes.Length )
			throw new ArgumentException( "The rest positions must be the ones the session was built from (same count).", nameof( sourceRest ) );

		SkinBinder.DeformInto( Proxy.Mesh.Positions, ProxyWeights, BindWorld, PoseWorld, PosedProxy, _skin );

		for ( var i = 0; i < _deltas.Length; i++ )
			_deltas[i] = PosedProxy[i] - Proxy.Mesh.Positions[i];

		for ( var v = 0; v < sourceRest.Count; v++ )
			PosedFullRes[v] = sourceRest[v] + _deltas[_map.A[v]] * _map.U[v] + _deltas[_map.B[v]] * _map.V[v] + _deltas[_map.C[v]] * _map.W[v];
	
		// Weight-mismatched vertices are skinned directly — the proxy transfer
		// cannot represent their blend, and this is exact for them.
		if ( _flagged.Length > 0 )
		{
			SkinBinder.DeformInto( _flaggedRest, _flaggedWeights, BindWorld, PoseWorld, _flaggedPosed, _skin );
	
			for ( var k = 0; k < _flagged.Length; k++ )
				PosedFullRes[_flagged[k]] = _flaggedPosed[k];
		}
	}

}
