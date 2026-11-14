using System;
using System.Collections.Generic;

namespace Effigy.Map;

// MapCsg — CSG subtract / merge / hollow / intersect for plane-based map brushes.
// Board: hammer-brush-csg-subtract-merge-hollow (Geppetto Desktop, Hammer P1)
// and hammer-csg-intersect (Geppetto Desktop, Hammer P2).
//
// WHAT IT DOES. First-class CSG on brush solids, with the solve DEFERRED to
// bake time (the HammerForge pattern): callers stage pending cuts on a
// MapCsgPlan, previews stay cheap, and Solve() runs the booleans once.
//   Subtract — the "carve that works": target minus cutter, via IMeshBoolean.
//   Merge    — union of two brushes, accepted ONLY when the union stays
//              convex; anything else is refused with reason "merge-not-convex".
//   Hollow   — a convex solid becomes a room: every face plane moves inward
//              by the wall thickness, the inset solid is rebuilt from the
//              planes, and it is subtracted from the outer solid.
//   Intersect — the convex overlap of two brushes, via IMeshBoolean. Two
//              solids that share no volume (disjoint, or merely touching on
//              a face / edge / point) give the named empty result
//              "csg-empty-result" — never a zero-volume brush smuggled
//              through as a result.
//
// There is NO Carve operation here, ever. Carve (naive per-face splitting)
// creates slivers and bad visleaves; the board and the Reddit record both
// forbid it. A reflection check in tests/MapCsgTests.cs asserts no public
// member of this file is named Carve.
//
// PROVIDER. Every solve goes through IMeshBoolean: Manifold first
// (ManifoldBoolean, when installed — it falls back to the exact boolean
// itself when its library is missing or an input is not manifold), exact
// ExactMeshBoolean otherwise. The caller may pass an explicit provider; the
// default is MeshBoolean.Provider. When NO provider is installed at all
// (MeshBoolean.Provider is null) the solve fails with the named capability
// reason "csg-no-boolean-provider" — it never returns an uncut mesh and
// pretends the cut happened.
//
// FAILURE SAFETY (board hammer-csg-failure-safety). A failed CSG/Hollow
// operation must NEVER destroy the source brush — the Reddit failure this
// ticket answers is "make hollow deletes the brush itself". The guard is
// structural, not a promise: every Try* op runs inside Guard() (any
// unexpected exception becomes the named reason "csg-operation-failed"
// instead of escaping), and Apply() hands the provider CLONES of the
// inputs, never the caller's meshes. A provider that mutates its inputs,
// refuses, throws, or returns a degenerate result can therefore only ever
// damage the clones; on any failure the originals are untouched by
// construction and the result is a named CsgResult with Mesh null. The
// current built-in and Manifold providers already only read their inputs,
// but IMeshBoolean is an open interface — a host/engine provider is not
// trusted to behave — so the clone happens here, at the MapCsg boundary,
// for every op (subtract / merge / hollow / intersect), including the
// Manifold-missing fallback path, which re-enters through the same
// Apply(). Staging already clones (MapCsgPlan), so a bake re-run sees the
// same snapshots.
//
// RESULTS ARE DATA, NOT EXCEPTIONS. A refused operation returns a CsgResult
// with Ok false and a machine-readable Reason slug (listed below) plus a
// human Message. Invalid input is a named reason, never a silent fix.
//   csg-input-null               one of the solids was null
//   csg-input-empty              a solid has no faces
//   csg-input-open               a solid is not closed, so it has no inside
//   csg-no-boolean-provider      no IMeshBoolean is installed (capability)
//   csg-provider-refused         the provider refused; Message carries why
//   csg-provider-threw           the provider threw instead of answering
//   csg-empty-result             the boolean left nothing behind; for
//                                Intersect this is also the answer when the
//                                solids share no volume (disjoint, or merely
//                                touching) — the provider's no-overlap refusal
//                                and a zero-volume overlap are both translated
//                                to this slug, never to a zero-volume brush
//   csg-result-not-closed        the boolean result is not a closed solid
//   csg-non-positive-volume      the result has no positive volume
//   csg-merge-failed             CoplanarMerge threw on the result
//   csg-operation-failed         an unexpected exception escaped the op
//                                itself (not the provider — see
//                                csg-provider-threw); inputs untouched
//   merge-not-convex             the union is not convex, so no one brush
//   hollow-thickness-not-positive  wall thickness was <= 0
//   hollow-needs-convex          hollow needs a convex solid to inset
//   hollow-too-thick             the inset faces meet or cross: no inner solid
//
// MAPBRUSH MISMATCH (2026-11-13, reported to the coordinator). This ticket
// depends on hammer-brush-core-plane-based-brushes (MapBrush), but the type
// has no stable API to bind to yet, so MapCsg is built on PolyMesh — the
// solid type IMeshBoolean actually consumes — plus the plane layer the
// MapBrush spec calls for (BrushPlanes / MeshFromPlanes: plane-intersection
// vertex solve, the FromPlanes equivalent). Timeline, read from the tree:
//   * 16:04 hammer-01 landed the real MapBrush.cs (753 lines, namespace
//     Effigy): MapBrushFace (normal + distance + FaceMaterial), MapBrushDefect
//     named codes, FromPlanes(IEnumerable<MapBrushFace>) / FromVertices /
//     FromFaceVertices / Box, float Volume, Centroid, IsValid,
//     Validate() -> IReadOnlyList<MapBrushDefect>, ToMesh(Func<string,int>),
//     MapBrushFeature wired to FeatureRegistry.
//   * 16:06 the file was overwritten in place (6,171 bytes, 197 lines) by a
//     stub whose own header says "STUB ONLY ... NEVER in the real repo" — it
//     is the MapClip worker's /tmp verify-copy stub: MapFace (normal +
//     distance + string material + UV fields), MapBrush.FromPlanes
//     (IReadOnlyList<MapFace>), double Volume, Validate() ->
//     IReadOnlyList<string>, no FromVertices, no ToMesh. The real
//     implementation is currently not on disk; the task is still in-progress
//     under hammer-01. This overwrite is not MapCsg's to repair (the file is
//     hammer-01's claim) — it is flagged here for the coordinator.
// MapCsg deliberately binds to NEITHER shape: the stub will be replaced, and
// binding to it would break when the real API returns. When the real MapBrush
// is restored and frozen, the integrator adds the thin adapters (MapBrush ->
// PolyMesh via ToMesh(), PolyMesh -> MapBrush via BrushPlanes + FromPlanes)
// without changing this file's ops. Until then this file compiles and tests
// stand alone.
//
// NO DESKTOP UI. Backend / data-model / API / headless only, per Pooh
// 2026-11-13. The map document that will own a MapCsgPlan
// (hammer-map-document-model) does not exist yet either; the plan is the
// staging area that document will hold.

/// <summary>Which CSG operation a staged cut performs at bake time.</summary>
public enum CsgOpKind
{
	/// <summary>Target minus cutter.</summary>
	Subtract,

	/// <summary>Union of two brushes; accepted only while convex.</summary>
	Merge,

	/// <summary>Solid minus its own inset: a room with wall thickness.</summary>
	Hollow,

	/// <summary>Only the volume both solids share. Disjoint or merely
	/// touching solids give the named empty result, not a brush.</summary>
	Intersect,
}

/// <summary>One face plane of a brush: outward unit normal + distance, so the
/// solid is the intersection of the half-spaces n·x &lt;= Distance. The same
/// plane convention MapBrush.FromPlanes specifies.</summary>
public readonly struct BrushPlane
{
	public readonly Vec3 Normal;
	public readonly float Distance;
	public readonly int Material;

	public BrushPlane( Vec3 normal, float distance, int material = 0 )
	{
		Normal = normal.LengthSquared > 1e-18f ? normal.Normal : new Vec3( 1, 0, 0 );
		Distance = distance;
		Material = material;
	}

	/// <summary>Signed distance from the plane: &lt;= 0 is inside the half-space.</summary>
	public float SignedDistance( Vec3 p ) => Vec3.Dot( Normal, p ) - Distance;
}

/// <summary>The outcome of one CSG operation. Ok false always carries a
/// machine-readable Reason slug and a human Message, and Mesh null — a
/// refusal is never a half-built mesh.</summary>
public sealed class CsgResult
{
	public CsgResult( bool ok, PolyMesh mesh, string reason, string message, string route, int facesMerged )
	{
		Ok = ok;
		Mesh = mesh;
		Reason = reason;
		Message = message;
		Route = route;
		FacesMerged = facesMerged;
	}

	public bool Ok { get; }
	public PolyMesh Mesh { get; }
	public string Reason { get; }
	public string Message { get; }
	public string Route { get; }
	public int FacesMerged { get; }

	public static CsgResult Succeeded( PolyMesh mesh, string route, int facesMerged ) =>
		new( true, mesh, null, null, route, facesMerged );

	public static CsgResult Failed( string reason, string message ) =>
		new( false, null, reason, message, "", 0 );
}

/// <summary>One staged CSG operation. The meshes are snapshots: staging
/// clones them, so edits to the caller's brushes after staging never change
/// what bake solves.</summary>
public sealed class CsgPendingOp
{
	public CsgPendingOp( CsgOpKind op, PolyMesh target, PolyMesh tool, float thickness, string label )
	{
		Op = op;
		Target = target;
		Tool = tool;
		Thickness = thickness;
		Label = label;
	}

	public CsgOpKind Op { get; }
	public PolyMesh Target { get; }
	public PolyMesh Tool { get; }
	public float Thickness { get; }
	public string Label { get; }
}

/// <summary>The outcome of solving a whole plan: one CsgResult per staged op,
/// in stage order. Ok is true only when every op succeeded.</summary>
public sealed class CsgSolveResult
{
	public CsgSolveResult( IReadOnlyList<CsgResult> results )
	{
		Results = results;
		var ok = true;
		foreach ( var r in results ) if ( !r.Ok ) { ok = false; break; }
		Ok = ok;
	}

	public IReadOnlyList<CsgResult> Results { get; }
	public bool Ok { get; }
}

/// <summary>Deferred CSG staging: pending cuts live here until bake. Staging
/// never runs a boolean — it only records and snapshots — so previews stay
/// cheap and a document can stage cuts with no provider installed at all.
/// Solve() runs every staged op through MapCsg and returns one result per
/// op; it does not clear the plan, so a bake can be re-run and the caller
/// clears explicitly with Clear().</summary>
public sealed class MapCsgPlan
{
	readonly List<CsgPendingOp> _pending = new();

	public IReadOnlyList<CsgPendingOp> Pending => _pending;
	public int PendingCount => _pending.Count;

	/// <summary>Stage target-minus-cutter. Returns the op index.</summary>
	public int StageSubtract( PolyMesh target, PolyMesh cutter, string label = null )
	{
		ArgumentNullException.ThrowIfNull( target );
		ArgumentNullException.ThrowIfNull( cutter );
		_pending.Add( new CsgPendingOp( CsgOpKind.Subtract, target.Clone(), cutter.Clone(), 0f, label ) );
		return _pending.Count - 1;
	}

	/// <summary>Stage the union of two brushes (accepted at bake only while convex).</summary>
	public int StageMerge( PolyMesh first, PolyMesh second, string label = null )
	{
		ArgumentNullException.ThrowIfNull( first );
		ArgumentNullException.ThrowIfNull( second );
		_pending.Add( new CsgPendingOp( CsgOpKind.Merge, first.Clone(), second.Clone(), 0f, label ) );
		return _pending.Count - 1;
	}

	/// <summary>Stage hollowing a solid into a room with the given wall thickness.</summary>
	public int StageHollow( PolyMesh solid, float thickness, string label = null )
	{
		ArgumentNullException.ThrowIfNull( solid );
		_pending.Add( new CsgPendingOp( CsgOpKind.Hollow, solid.Clone(), null, thickness, label ) );
		return _pending.Count - 1;
	}

	/// <summary>Stage the overlap of two brushes (solved at bake; disjoint or
	/// merely touching solids give the named empty result).</summary>
	public int StageIntersect( PolyMesh first, PolyMesh second, string label = null )
	{
		ArgumentNullException.ThrowIfNull( first );
		ArgumentNullException.ThrowIfNull( second );
		_pending.Add( new CsgPendingOp( CsgOpKind.Intersect, first.Clone(), second.Clone(), 0f, label ) );
		return _pending.Count - 1;
	}

	/// <summary>Bake: solve every staged op, in stage order, through
	/// <paramref name="provider"/> (null means MeshBoolean.Provider). A failed
	/// op does not stop the others; each result carries its own reason.</summary>
	public CsgSolveResult Solve( IMeshBoolean provider = null )
	{
		var results = new List<CsgResult>( _pending.Count );
		foreach ( var op in _pending )
		{
			CsgResult r = op.Op switch
			{
				CsgOpKind.Subtract => MapCsg.TrySubtract( op.Target, op.Tool, provider ),
				CsgOpKind.Merge => MapCsg.TryMerge( op.Target, op.Tool, provider ),
				CsgOpKind.Intersect => MapCsg.TryIntersect( op.Target, op.Tool, provider ),
				_ => MapCsg.TryHollow( op.Target, op.Thickness, provider ),
			};
			results.Add( r );
		}
		return new CsgSolveResult( results );
	}

	public void Clear() => _pending.Clear();
}

/// <summary>CSG subtract / merge / hollow / intersect for brush solids.
/// Immediate ops (Try*) solve at once; MapCsgPlan stages them for bake.
/// No Carve.</summary>
public static class MapCsg
{
	const string NoProviderReason = "csg-no-boolean-provider";
	const string NoProviderMessage =
		"No mesh boolean provider is installed, so this CSG operation cannot run. " +
		"Install ManifoldBoolean (or the engine provider) instead of returning an uncut mesh.";

	/// <summary>Target minus cutter — the "carve that works". A cutter that
	/// does not touch the target is a no-op: the target comes back unchanged.
	/// On any failure the target and cutter are returned untouched (see the
	/// FAILURE SAFETY note in the file header).</summary>
	public static CsgResult TrySubtract( PolyMesh target, PolyMesh tool, IMeshBoolean provider = null ) =>
		Guard( () => TrySubtractCore( target, tool, provider ) );

	static CsgResult TrySubtractCore( PolyMesh target, PolyMesh tool, IMeshBoolean provider = null )
	{
		var problem = InputProblem( target, tool );
		if ( problem != null ) return problem;
		var p = ResolveProvider( provider );
		if ( p is null ) return CsgResult.Failed( NoProviderReason, NoProviderMessage );
		return Apply( BooleanOp.Subtract, target, tool, p );
	}

	/// <summary>Union of two brushes. Accepted only when the union stays
	/// convex; a non-convex union is refused with "merge-not-convex" and the
	/// inputs are left untouched.</summary>
	public static CsgResult TryMerge( PolyMesh first, PolyMesh second, IMeshBoolean provider = null ) =>
		Guard( () => TryMergeCore( first, second, provider ) );

	static CsgResult TryMergeCore( PolyMesh first, PolyMesh second, IMeshBoolean provider = null )
	{
		var problem = InputProblem( first, second );
		if ( problem != null ) return problem;
		var p = ResolveProvider( provider );
		if ( p is null ) return CsgResult.Failed( NoProviderReason, NoProviderMessage );
		var r = Apply( BooleanOp.Union, first, second, p );
		if ( !r.Ok ) return r;
		if ( !IsConvex( r.Mesh ) )
			return CsgResult.Failed( "merge-not-convex",
				"The union of the two brushes is not convex, so it cannot stay one brush. " +
				"Keep them as two brushes, or split the union into convex pieces." );
		return r;
	}

	/// <summary>Only the volume both solids share. The overlap of two convex
	/// brushes is convex, so the result is a valid brush: closed, positive
	/// volume, coplanar faces merged by <see cref="Apply"/> on the way out.
	/// Two solids that share no volume — disjoint, or merely touching on a
	/// face, edge or point — give the named empty result
	/// "csg-empty-result" with Mesh null: never a zero-volume brush smuggled
	/// through. The providers report that case as a refusal ("do not
	/// overlap" / "left nothing behind"), so it is translated here; any
	/// other provider refusal stays "csg-provider-refused".</summary>
	public static CsgResult TryIntersect( PolyMesh first, PolyMesh second, IMeshBoolean provider = null ) =>
		Guard( () => TryIntersectCore( first, second, provider ) );

	static CsgResult TryIntersectCore( PolyMesh first, PolyMesh second, IMeshBoolean provider = null )
	{
		var problem = InputProblem( first, second );
		if ( problem != null ) return problem;
		var p = ResolveProvider( provider );
		if ( p is null ) return CsgResult.Failed( NoProviderReason, NoProviderMessage );
		var r = Apply( BooleanOp.Intersect, first, second, p );
		if ( r.Ok ) return r;
		if ( r.Reason == "csg-provider-refused" && IsEmptyResultRefusal( r.Message ) )
			return CsgResult.Failed( "csg-empty-result",
				"The two solids share no volume, so their intersection is empty. " +
				"Move them so they overlap, or treat the empty result as no brush." );
		if ( r.Reason == "csg-non-positive-volume" )
			return CsgResult.Failed( "csg-empty-result",
				"The two solids only touch, so their shared volume is zero and their intersection is empty. " +
				"Move them so they overlap, or treat the empty result as no brush." );
		return r;
	}

	/// <summary>True when a provider refusal means "no shared volume":
	/// the exact boolean's "do not overlap" / "left nothing behind" and
	/// Manifold's "do not overlap". Anything else is a real refusal, not
	/// an empty intersection, and must keep its own reason.</summary>
	static bool IsEmptyResultRefusal( string message )
	{
		if ( string.IsNullOrEmpty( message ) ) return false;
		return message.Contains( "do not overlap", StringComparison.OrdinalIgnoreCase )
			|| message.Contains( "left nothing behind", StringComparison.OrdinalIgnoreCase );
	}

	/// <summary>A convex solid becomes a room: every face plane moves inward
	/// by <paramref name="thickness"/>, the inset solid is rebuilt from the
	/// planes, and it is subtracted from the outer solid. The result is one
	/// closed shell (outer faces + inner faces); its signed volume is the
	/// remaining wall volume, outer minus inner. A hollow that cannot be
	/// built — thickness too large, non-convex source, degenerate boolean
	/// result — fails with a named reason and leaves the source brush
	/// intact: it never deletes the brush it was asked to hollow.</summary>
	public static CsgResult TryHollow( PolyMesh solid, float thickness, IMeshBoolean provider = null ) =>
		Guard( () => TryHollowCore( solid, thickness, provider ) );

	static CsgResult TryHollowCore( PolyMesh solid, float thickness, IMeshBoolean provider = null )
	{
		if ( solid is null )
			return CsgResult.Failed( "csg-input-null", "Hollow needs a solid; it was missing." );
		if ( solid.FaceCount == 0 )
			return CsgResult.Failed( "csg-input-empty", "Hollow needs a solid with faces; it has none." );
		if ( thickness <= 0f )
			return CsgResult.Failed( "hollow-thickness-not-positive",
				$"Wall thickness must be positive; got {thickness:0.###}." );
		var check = MeshValidator.Validate( solid );
		if ( !check.IsClosed )
			return CsgResult.Failed( "csg-input-open",
				$"The solid is not closed ({check.BoundaryEdges} boundary edge(s)), so it has no inside to hollow." );
		if ( !IsConvex( solid ) )
			return CsgResult.Failed( "hollow-needs-convex",
				"Hollow offsets each face plane inward, which is only defined for a convex solid. " +
				"Split the solid into convex pieces first." );
		var planes = BrushPlanes( solid );
		var innerPlanes = new List<BrushPlane>( planes.Count );
		foreach ( var pl in planes )
			innerPlanes.Add( new BrushPlane( pl.Normal, pl.Distance - thickness, pl.Material ) );
		var inner = MeshFromPlanes( innerPlanes );
		if ( inner is null )
			return CsgResult.Failed( "hollow-too-thick",
				$"Wall thickness {thickness:0.###} leaves no inner solid — the inset faces meet or cross. " +
				"Use a smaller thickness." );
		var p = ResolveProvider( provider );
		if ( p is null ) return CsgResult.Failed( NoProviderReason, NoProviderMessage );
		return Apply( BooleanOp.Subtract, solid, inner, p );
	}

	/// <summary>True when the mesh is a closed solid whose volume equals its
	/// convex hull's volume (within tolerance): the mesh already is its own
	/// hull. A hollow shell is not convex by this test — its hull encloses
	/// the void too.</summary>
	public static bool IsConvex( PolyMesh mesh, float relativeTolerance = 1e-3f )
	{
		if ( mesh is null || mesh.FaceCount == 0 ) return false;
		if ( !MeshValidator.Validate( mesh ).IsClosed ) return false;
		float volume = MathF.Abs( mesh.SignedVolume() );
		if ( volume <= 1e-6f ) return false;
		var hull = ConvexHull.ToMesh( mesh.Positions );
		if ( hull is null || hull.FaceCount == 0 ) return false;
		float hullVolume = MathF.Abs( hull.SignedVolume() );
		return MathF.Abs( volume - hullVolume ) <= relativeTolerance * MathF.Max( 1f, volume );
	}

	/// <summary>One plane per face (outward normal + distance), with
	/// coplanar-split faces deduplicated. This is the plane view a MapBrush
	/// carries; a boolean result's planes are recovered this way.</summary>
	public static List<BrushPlane> BrushPlanes( PolyMesh mesh )
	{
		var planes = new List<BrushPlane>();
		if ( mesh is null ) return planes;
		foreach ( var face in mesh.Faces )
		{
			if ( face.Indices is null || face.Indices.Length < 3 ) continue;
			var n = mesh.FaceNormal( face ).Normal;
			if ( n.LengthSquared < 1e-12f ) continue;
			float d = Vec3.Dot( n, mesh.Positions[face.Indices[0]] );
			var dup = false;
			foreach ( var p in planes )
				if ( Vec3.Dot( p.Normal, n ) > 0.9999f && MathF.Abs( p.Distance - d ) < 1e-3f ) { dup = true; break; }
			if ( !dup ) planes.Add( new BrushPlane( n, d, face.Material ) );
		}
		return planes;
	}

	/// <summary>Rebuild a convex solid from its face planes: plane-
	/// intersection vertex solve (the MapBrush.FromPlanes equivalent), then
	/// one face per plane with its vertices ordered around the centroid.
	/// Returns null when the planes enclose no valid closed solid with
	/// positive volume — e.g. an inset that met or crossed itself.</summary>
	public static PolyMesh MeshFromPlanes( IReadOnlyList<BrushPlane> planes, float tolerance = 1e-4f )
	{
		if ( planes is null || planes.Count < 4 ) return null;
		var verts = new List<Vec3>();
		for ( int i = 0; i < planes.Count; i++ )
		for ( int j = i + 1; j < planes.Count; j++ )
		for ( int k = j + 1; k < planes.Count; k++ )
		{
			if ( !TryIntersectPlanes( planes[i], planes[j], planes[k], out var p ) ) continue;
			if ( !InsideAll( planes, p ) ) continue;
			var dup = false;
			foreach ( var v in verts )
				if ( (v - p).LengthSquared < tolerance * tolerance ) { dup = true; break; }
			if ( !dup ) verts.Add( p );
		}
		if ( verts.Count < 4 ) return null;
		var mesh = new PolyMesh();
		foreach ( var v in verts ) mesh.AddVertex( v );
		foreach ( var plane in planes )
		{
			var on = new List<int>();
			for ( int vi = 0; vi < verts.Count; vi++ )
				if ( MathF.Abs( plane.SignedDistance( verts[vi] ) ) <= 1e-3f ) on.Add( vi );
			if ( on.Count < 3 ) continue;
			var c = Vec3.Zero;
			foreach ( var vi in on ) c += verts[vi];
			c /= on.Count;
			var u = Vec3.Cross( plane.Normal, MathF.Abs( plane.Normal.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 0, 1, 0 ) ).Normal;
			var v = Vec3.Cross( plane.Normal, u );
			on.Sort( ( a, b ) => MathF.Atan2( Vec3.Dot( verts[a] - c, v ), Vec3.Dot( verts[a] - c, u ) )
				.CompareTo( MathF.Atan2( Vec3.Dot( verts[b] - c, v ), Vec3.Dot( verts[b] - c, u ) ) ) );
			// Winding: the face's Newell normal must point along the plane
			// normal (outward); reverse when the sort came out the other way.
			if ( Vec3.Dot( Newell( verts, on ), plane.Normal ) < 0f ) on.Reverse();
			mesh.AddFace( on.ToArray(), null, plane.Material );
		}
		if ( mesh.FaceCount < 4 ) return null;
		if ( !MeshValidator.Validate( mesh ).IsClosed ) return null;
		if ( mesh.SignedVolume() <= 1e-6f ) return null;
		return mesh;
	}

	static CsgResult InputProblem( PolyMesh target, PolyMesh tool )
	{
		if ( target is null || tool is null )
			return CsgResult.Failed( "csg-input-null", "A CSG operation needs two solids; one of them was missing." );
		if ( target.FaceCount == 0 || tool.FaceCount == 0 )
			return CsgResult.Failed( "csg-input-empty", "A CSG operation needs two solids with faces; one of them has none." );
		var t = MeshValidator.Validate( target );
		if ( !t.IsClosed )
			return CsgResult.Failed( "csg-input-open",
				$"The target is not closed ({t.BoundaryEdges} boundary edge(s)), so it has no inside to combine." );
		var u = MeshValidator.Validate( tool );
		if ( !u.IsClosed )
			return CsgResult.Failed( "csg-input-open",
				$"The tool is not closed ({u.BoundaryEdges} boundary edge(s)), so it has no inside to combine." );
		return null;
	}

	static IMeshBoolean ResolveProvider( IMeshBoolean provider ) => provider ?? MeshBoolean.Provider;

	/// <summary>Failure guard for every CSG op (board
	/// hammer-csg-failure-safety): an unexpected exception escaping the
	/// attempt becomes the named reason "csg-operation-failed" instead of
	/// propagating, and the inputs are untouched — the attempt only ever
	/// ran on clones (see <see cref="Apply"/>), so there is nothing to
	/// restore. Provider throws are NOT caught here: Apply() already maps
	/// those to "csg-provider-threw" with the provider's message.</summary>
	static CsgResult Guard( Func<CsgResult> attempt )
	{
		try { return attempt(); }
		catch ( Exception e )
		{
			return CsgResult.Failed( "csg-operation-failed",
				$"The CSG operation failed unexpectedly ({e.GetType().Name}: {e.Message}). The source brushes were left untouched." );
		}
	}

	static CsgResult Apply( BooleanOp op, PolyMesh target, PolyMesh tool, IMeshBoolean provider )
	{
		// FAILURE GUARD (hammer-csg-failure-safety): the provider only ever
		// sees clones. IMeshBoolean is an open interface and a provider may
		// mutate its inputs in place, refuse halfway, or throw after a
		// partial mutation — if it ran on the caller's meshes, a failed op
		// could destroy the source brush (the "make hollow deletes the
		// brush" failure). Cloning here, at the MapCsg boundary, makes
		// failure safety structural: whatever the provider does to these
		// work copies, the originals are untouched by construction, and
		// every failure below returns a named CsgResult with Mesh null.
		// The result mesh is fresh provider output (or a clone of the work
		// copy for a disjoint no-op), never an input, so success is safe
		// the same way. Clone() is a deep copy (positions, faces, UVs,
		// materials — see PolyMesh.Clone), so no channel of the source can
		// leak into the attempt.
		var targetWork = target.Clone();
		var toolWork = tool.Clone();
		PolyMesh result;
		string error;
		bool ok;
		try { ok = provider.TryApply( op, targetWork, toolWork, out result, out error ); }
		catch ( Exception e )
		{
			return CsgResult.Failed( "csg-provider-threw",
				$"The boolean provider threw instead of returning a result: {e.Message}" );
		}
		if ( !ok )
			return CsgResult.Failed( "csg-provider-refused", error ?? "The boolean provider refused the operation without a reason." );
		if ( result is null || result.FaceCount == 0 )
			return CsgResult.Failed( "csg-empty-result", "The boolean left nothing behind." );
		int merged;
		try { merged = CoplanarMerge.Merge( result ); }
		catch ( Exception e )
		{
			return CsgResult.Failed( "csg-merge-failed", $"CoplanarMerge failed on the boolean result: {e.Message}" );
		}
		var check = MeshValidator.Validate( result );
		if ( !check.IsClosed )
			return CsgResult.Failed( "csg-result-not-closed",
				$"The boolean result is not closed ({check.BoundaryEdges} boundary edge(s))." );
		float volume = result.SignedVolume();
		if ( volume <= 1e-6f )
			return CsgResult.Failed( "csg-non-positive-volume",
				$"The boolean result has no positive volume ({volume:0.###}), so its winding is inward or it is degenerate." );
		return CsgResult.Succeeded( result, RouteOf( provider ), merged );
	}

	static string RouteOf( IMeshBoolean provider )
	{
		if ( provider is ManifoldBoolean )
			return string.IsNullOrEmpty( ManifoldBoolean.LastRoute ) ? "manifold" : ManifoldBoolean.LastRoute;
		if ( provider is ExactMeshBoolean ) return "built-in (exact)";
		return "custom (" + provider.GetType().Name + ")";
	}

	static bool TryIntersectPlanes( BrushPlane a, BrushPlane b, BrushPlane c, out Vec3 point )
	{
		point = Vec3.Zero;
		var n1 = a.Normal;
		var n2 = b.Normal;
		var n3 = c.Normal;
		float det = Vec3.Dot( n1, Vec3.Cross( n2, n3 ) );
		if ( MathF.Abs( det ) < 1e-9f ) return false;
		point = (a.Distance * Vec3.Cross( n2, n3 ) + b.Distance * Vec3.Cross( n3, n1 ) + c.Distance * Vec3.Cross( n1, n2 )) / det;
		return true;
	}

	static bool InsideAll( IReadOnlyList<BrushPlane> planes, Vec3 p )
	{
		foreach ( var pl in planes )
			if ( pl.SignedDistance( p ) > 1e-3f ) return false;
		return true;
	}

	static Vec3 Newell( IReadOnlyList<Vec3> verts, List<int> idx )
	{
		float nx = 0f, ny = 0f, nz = 0f;
		for ( int i = 0; i < idx.Count; i++ )
		{
			var a = verts[idx[i]];
			var b = verts[idx[(i + 1) % idx.Count]];
			nx += (a.y - b.y) * (a.z + b.z);
			ny += (a.z - b.z) * (a.x + b.x);
			nz += (a.x - b.x) * (a.y + b.y);
		}
		return new Vec3( nx, ny, nz );
	}
}
