// 3D-first direct editing for plane-based map brushes (no UI).
// Board task: hammer-edit-3d-first-direct (worker muse-3-desktop-hammer-33).
//
// TrenchBroom is "built from the ground-up for 3D editing" — drag
// faces/edges/vertices directly in 3D, extrude, shear, scale; "you don't
// need to use the 2D grid view unless you want to". Geppetto is a 3D asset
// toolsuite, so this is the native 3D form of the pass-1 2D-oriented APIs:
// a fast 3D pick (ray -> face/edge/vertex/brush) plus drag / extrude /
// shear / scale operations in 3D, as pure math + document ops, no viewport.
//
// DEPENDENCY NOTES (2026-11-13, muse-3-desktop-hammer-33) — read before
// wiring this into a caller; each dependency is recorded exactly as found:
//
//   * MapVertexEdit (hammer-brush-vertex-edit-invalid-face): LANDED, DONE.
//     This file builds directly on it: the brush representation is
//     MapVertexBrush (the vertex-form working copy), drags delegate to
//     MapVertexEdit.MoveVertex/MoveEdge/MoveVertices, and every result
//     carries MapVertexEdit's per-face validity (MapVertexValidity).
//     An edit that twists a brush is RETURNED with the bad faces flagged
//     (NonPlanar / NonConvex / ...), never silently repaired.
//
//   * MapBrush (hammer-brush-core-plane-based-brushes): NOT landed — no
//     kernel/Effigy/Map/MapBrush.cs on disk. Like MapVertexEdit and
//     MapClip, this file is written against the core spec exactly as filed
//     and does NOT invent a MapBrush type or member names. The bridge for
//     the final integrator is the same as MapVertexEdit's: a MapBrush
//     feeds in via MapBrush.FromVertices -> MapVertexBrush, and the result
//     feeds back via MapVertexBrush.ToPlanes() -> MapBrush.FromPlanes.
//     No test here asserts an API the core spec does not name.
//
//   * MapTransform (hammer-brush-transform-grid-snap-api): IN PROGRESS
//     (muse-3-desktop-hammer-04), but kernel/Effigy/Map/MapTransform.cs
//     IS on disk and its filed numeric behaviour is implemented there
//     (SnapToGrid/SnapAngle, pivot matrices on System.Numerics types).
//     This file reuses MapTransform.SnapToGrid for grid-snapped drag
//     deltas. It does NOT reuse the Matrix4x4 plane transforms: those
//     operate on System.Numerics.Plane lists, while the vertex-form brush
//     here is kernel Vec3 loops — the shear/scale math below is the same
//     affine math applied per vertex, and the integrator's MapBrush
//     adapter can swap in MapTransform.TransformPlane per face once
//     MapBrush lands. GizmoMath.cs itself is untouched (and, as the
//     MapTransform header notes, GizmoMath lives in Marionette.Desktop,
//     which the engine-free kernel cannot reference).
//
//   * GizmoModel (hammer-gizmo-pivot-tool-model): IN PROGRESS
//     (muse-3-desktop-hammer-32), NOT landed — no
//     kernel/Effigy/Map/GizmoModel.cs on disk. The desc says shear/scale
//     happen "about the gizmo pivot"; until GizmoModel lands, the pivot is
//     an explicit Vec3 parameter on Shear3D/Scale3D (a caller passes
//     GizmoState.Pivot once that type exists). No GizmoModel type is
//     invented here. Drag planes use the kernel's ClipPlane (the plane
//     math MapClip builds with MapClip.PlaneFromTwoPoints) — see
//     TryDragInPlane / DragPlaneForFace.
//
//   * Ray: GizmoMath.Ray lives in Marionette.Desktop and the kernel cannot
//     reference it, so the ray here is DirectEditRay on kernel Vec3
//     (same shape: origin + direction). A desktop caller converts with
//     new DirectEditRay((Vec3)ray.Origin, (Vec3)ray.Direction) at the
//     boundary; the conversion is a handful of lines, paid once (Vec.cs).
//
// Rules this file enforces (ticket hammer-edit-3d-first-direct):
//   * every edit returns a NEW brush + per-face validity; the input brush
//     is never mutated;
//   * ExtrudeFace adds real side faces for an OUTWARD extrude (the cap
//     moves out, quads bridge the old loop to the new one) — it is NOT
//     DragFaceAlongNormal, which moves the face's shared vertices in
//     place and stretches the adjacent faces. Both grow a box's volume
//     by face-area x distance; the tests assert both, plus the
//     face-count difference. An INWARD extrude cannot build a skirt
//     (its side quads would double-cover the side walls and the cap
//     would sit inside the solid, so the divergence volume would be
//     meaningless) — it moves the face in place instead, documented
//     on ExtrudeFace; either way the volume changes by face-area x
//     distance;
//   * operations return the new brush state + validity, never a silent
//     fix: a drag that twists a face out of plane, a negative scale that
//     flips winding, or an inward extrude that collapses the solid all
//     come back with the affected faces named in Validity;
//   * zero scale on any axis throws (same rule as MapTransform); a zero
//     direction ray throws; a shear needs a non-zero axis and a
//     shear direction perpendicular to it (a pure shear preserves volume).
// No UI, no Hammer app, no shell-out: backend/data-model only. Units are
// the kernel's inches, Z-up (CONTRACTS.md).

using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>A 3D ray on kernel Vec3: origin + unit direction.</summary>
public readonly struct DirectEditRay
{
	public Vec3 Origin { get; }
	public Vec3 Direction { get; }

	public DirectEditRay( Vec3 origin, Vec3 direction )
	{
		if ( !IsFinite( origin ) || !IsFinite( direction ) )
			throw new ArgumentException( "a pick ray's origin and direction must be finite." );
		if ( direction.LengthSquared < 1e-12f )
			throw new ArgumentException( "a pick ray needs a non-zero direction.", nameof( direction ) );
		Origin = origin;
		Direction = direction.Normal;
	}

	static bool IsFinite( Vec3 v ) => float.IsFinite( v.x ) && float.IsFinite( v.y ) && float.IsFinite( v.z );

	public Vec3 PointAt( double t ) => Origin + Direction * (float) t;
}

/// <summary>What a 3D pick hit: a whole face, an edge of it, or a vertex of it.</summary>
public enum DirectEditHitKind
{
	None,
	Face,
	Edge,
	Vertex,
}

/// <summary>One 3D pick result: the nearest hit over all brushes, if any.</summary>
public sealed class DirectEditHit
{
	public bool Hit { get; }
	public DirectEditHitKind Kind { get; }
	/// <summary>Index into the brush list passed to Pick; 0 for the single-brush overload.</summary>
	public int BrushIndex { get; }
	/// <summary>Face index on the hit brush, or -1 when there is no hit.</summary>
	public int FaceIndex { get; }
	/// <summary>Vertex index for a Vertex hit, or -1.</summary>
	public int VertexIndex { get; }
	/// <summary>Edge endpoints for an Edge hit; -1 / -1 otherwise.</summary>
	public int EdgeA { get; }
	public int EdgeB { get; }
	/// <summary>World point where the ray met the brush.</summary>
	public Vec3 Point { get; }
	/// <summary>Ray parameter of the hit (distance along the unit direction).</summary>
	public double Distance { get; }
	/// <summary>Outward normal of the hit face (Zero when there is no hit).</summary>
	public Vec3 Normal { get; }

	internal DirectEditHit( bool hit, DirectEditHitKind kind, int brush, int face, int vertex, int edgeA, int edgeB, Vec3 point, double distance, Vec3 normal )
	{
		Hit = hit;
		Kind = kind;
		BrushIndex = brush;
		FaceIndex = face;
		VertexIndex = vertex;
		EdgeA = edgeA;
		EdgeB = edgeB;
		Point = point;
		Distance = distance;
		Normal = normal;
	}

	public static DirectEditHit Miss => new DirectEditHit( false, DirectEditHitKind.None, -1, -1, -1, -1, -1, Vec3.Zero, double.PositiveInfinity, Vec3.Zero );
}

/// <summary>Caller choices shared by the DirectEdit3D operations.</summary>
public sealed class DirectEditOptions
{
	/// <summary>Grid size to snap drag deltas to (MapTransform.SnapToGrid,
	/// nearest multiple, ties away from zero). Null = no snapping.</summary>
	public double? GridSize { get; set; }
	/// <summary>Planarity tolerance forwarded to MapVertexEdit validation.</summary>
	public double PlanarTolerance { get; set; } = 1e-3;
	/// <summary>Pick: a hit point within this distance (inches) of a face
	/// vertex counts as a Vertex hit. Default 0.25 inches — a whole-face
	/// grab must not turn into a vertex grab on a small brush.</summary>
	public double PickVertexTolerance { get; set; } = 0.25;
	/// <summary>Pick: a hit point within this distance (inches) of a face
	/// edge counts as an Edge hit (checked after the vertex test).
	/// Default 0.5 inches.</summary>
	public double PickEdgeTolerance { get; set; } = 0.5;
}

/// <summary>Result of one 3D direct edit: the new brush + its validity.</summary>
public sealed class DirectEditResult
{
	public MapVertexBrush Brush;
	/// <summary>Per-face validity after the edit (invalid faces named, never hidden).</summary>
	public MapVertexValidity Validity;
	/// <summary>Faces touched by the edit (moved face, split/extruded faces, incident faces).</summary>
	public int[] AffectedFaces = Array.Empty<int>();
	/// <summary>Volume before the edit, for the volume-delta acceptance checks.</summary>
	public double VolumeBefore;
	/// <summary>Volume after the edit.</summary>
	public double VolumeAfter;
	/// <summary>Volume change (after - before). Extruding a face by d grows it by face-area x d.</summary>
	public double VolumeDelta => VolumeAfter - VolumeBefore;
	public bool IsValid => Validity != null && Validity.IsValid;
	/// <summary>Highlight set for the caller: indices of invalid faces.</summary>
	public int[] InvalidFaceIndices => Validity?.InvalidFaceIndices ?? Array.Empty<int>();
}

/// <summary>
/// 3D-first direct editing for a MapBrush (hammer-edit-3d-first-direct).
/// All edits are pure: they return a new brush and never mutate the input,
/// and they never silently repair an invalid result.
/// </summary>
public static class DirectEdit3D
{
	const double RayEps = 1e-6;

	// ---------------------------------------------------------------- pick

	/// <summary>Pick the nearest face/edge/vertex over a list of brushes.
	/// Double-sided ray/triangle test (Moller-Trumbore, double precision),
	/// nearest t wins; the hit is then classified by its distance to the
	/// hit face's vertices (Vertex) and edges (Edge), else Face. A ray
	/// that hits nothing returns <see cref="DirectEditHit.Miss"/> — a
	/// named miss, not an exception. Degenerate (zero-area) triangles are
	/// skipped by the determinant test.</summary>
	public static DirectEditHit Pick( IReadOnlyList<MapVertexBrush> brushes, DirectEditRay ray, DirectEditOptions options = null )
	{
		if ( brushes is null ) throw new ArgumentNullException( nameof( brushes ) );
		var opt = options ?? new DirectEditOptions();
		var best = DirectEditHit.Miss;
		for ( var bi = 0; bi < brushes.Count; bi++ )
		{
			var brush = brushes[bi];
			if ( brush is null ) throw new ArgumentException( $"brush {bi} in the pick list is null.", nameof( brushes ) );
			for ( var fi = 0; fi < brush.FaceCount; fi++ )
			{
				var face = brush.Faces[fi];
				if ( face.Length < 3 ) continue;
				var a = brush.Vertices[face[0]];
				for ( var i = 1; i + 1 < face.Length; i++ )
				{
					var b = brush.Vertices[face[i]];
					var c = brush.Vertices[face[i + 1]];
					if ( !RayTriangle( ray, a, b, c, out var t, out var point ) ) continue;
					if ( t < RayEps || t >= best.Distance ) continue;
					best = Classify( brush, bi, fi, point, t, opt );
				}
			}
		}
		return best;
	}

	/// <summary>Pick on a single brush (BrushIndex is 0 on a hit).</summary>
	public static DirectEditHit Pick( MapVertexBrush brush, DirectEditRay ray, DirectEditOptions options = null )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		return Pick( new[] { brush }, ray, options );
	}

	static DirectEditHit Classify( MapVertexBrush brush, int brushIndex, int faceIndex, Vec3 point, double t, DirectEditOptions opt )
	{
		var face = brush.Faces[faceIndex];
		var normal = brush.FaceNormal( faceIndex );
		// Vertex first: a grab on a corner is a vertex drag, not an edge drag.
		var bestVertex = -1;
		var bestVertexDist = double.PositiveInfinity;
		foreach ( var vi in face )
		{
			var d = ( brush.Vertices[vi] - point ).Length;
			if ( d < bestVertexDist ) { bestVertexDist = d; bestVertex = vi; }
		}
		if ( bestVertexDist <= opt.PickVertexTolerance )
			return new DirectEditHit( true, DirectEditHitKind.Vertex, brushIndex, faceIndex, bestVertex, -1, -1, point, t, normal );
		// Then edges: nearest point on each loop edge segment.
		var bestA = -1;
		var bestB = -1;
		var bestEdgeDist = double.PositiveInfinity;
		for ( var i = 0; i < face.Length; i++ )
		{
			var a = face[i];
			var b = face[( i + 1 ) % face.Length];
			var d = PointSegmentDistance( point, brush.Vertices[a], brush.Vertices[b] );
			if ( d < bestEdgeDist ) { bestEdgeDist = d; bestA = a; bestB = b; }
		}
		if ( bestEdgeDist <= opt.PickEdgeTolerance )
			return new DirectEditHit( true, DirectEditHitKind.Edge, brushIndex, faceIndex, -1, bestA, bestB, point, t, normal );
		return new DirectEditHit( true, DirectEditHitKind.Face, brushIndex, faceIndex, -1, -1, -1, point, t, normal );
	}

	/// <summary>Moller-Trumbore, double precision, double-sided (no
	/// backface culling: a ray from inside the brush still hits).</summary>
	static bool RayTriangle( DirectEditRay ray, Vec3 a, Vec3 b, Vec3 c, out double t, out Vec3 point )
	{
		t = 0;
		point = Vec3.Zero;
		var e1x = (double) b.x - a.x; var e1y = (double) b.y - a.y; var e1z = (double) b.z - a.z;
		var e2x = (double) c.x - a.x; var e2y = (double) c.y - a.y; var e2z = (double) c.z - a.z;
		var dx = ray.Direction.x; var dy = ray.Direction.y; var dz = ray.Direction.z;
		// p = dir x e2
		var px = dy * e2z - dz * e2y;
		var py = dz * e2x - dx * e2z;
		var pz = dx * e2y - dy * e2x;
		var det = e1x * px + e1y * py + e1z * pz;
		if ( Math.Abs( det ) < 1e-12 ) return false;
		var inv = 1.0 / det;
		var tvx = (double) ray.Origin.x - a.x; var tvy = (double) ray.Origin.y - a.y; var tvz = (double) ray.Origin.z - a.z;
		var u = ( tvx * px + tvy * py + tvz * pz ) * inv;
		if ( u < 0 || u > 1 ) return false;
		// q = tvec x e1
		var qx = tvy * e1z - tvz * e1y;
		var qy = tvz * e1x - tvx * e1z;
		var qz = tvx * e1y - tvy * e1x;
		var v = ( dx * qx + dy * qy + dz * qz ) * inv;
		if ( v < 0 || u + v > 1 ) return false;
		t = ( e2x * qx + e2y * qy + e2z * qz ) * inv;
		if ( t < 0 ) return false;
		point = ray.PointAt( t );
		return true;
	}

	static double PointSegmentDistance( Vec3 p, Vec3 a, Vec3 b )
	{
		var ab = b - a;
		var len2 = ab.LengthSquared;
		if ( len2 < 1e-12f ) return ( p - a ).Length;
		var t = Vec3.Dot( p - a, ab ) / len2;
		t = Math.Clamp( t, 0f, 1f );
		return ( p - ( a + ab * t ) ).Length;
	}

	// ------------------------------------------------------------- extrude

	/// <summary>Extrude a face outward along its normal by
	/// <paramref name="distance"/> (Hammer/TrenchBroom face extrude).
	/// The cap is a copy of the face loop offset by normal x distance;
	/// one side quad bridges each loop edge from the old vertices to the
	/// new ones, so the brush stays closed and watertight and the old
	/// adjacent faces keep their old top edge. The cap inherits the
	/// face's material, and so do the side quads. Volume grows by
	/// face-area x distance.
	/// A NEGATIVE distance is an inward extrude, and it does NOT build a
	/// skirt: side quads from the old loop down to an interior cap would
	/// double-cover the side walls (the cap would sit inside the solid
	/// and the divergence volume would be meaningless), so the face
	/// moves in place instead — exactly <see cref="DragFaceAlongNormal"/>
	/// — and the result reports no new faces. Either way the volume
	/// changes by face-area x distance and the result carries its
	/// validity; an inward extrude that runs past the opposite face
	/// comes back flagged, never silently repaired.
	/// Distance 0 is a no-op: the brush comes back unchanged.</summary>
	public static DirectEditResult ExtrudeFace( MapVertexBrush brush, int face, float distance, DirectEditOptions options = null )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		RequireFace( brush, face );
		var opt = options ?? new DirectEditOptions();
		if ( !float.IsFinite( distance ) )
			throw new ArgumentException( "extrude distance must be finite.", nameof( distance ) );
		var volumeBefore = brush.Volume();
		if ( distance == 0f )
			return Finish( brush, new[] { face }, opt, volumeBefore );
		if ( distance < 0f )
			// Inward: no skirt (see the doc comment) — move in place.
			return DragFaceAlongNormal( brush, face, distance, options );

		var loop = brush.Faces[face];
		var normal = brush.FaceNormal( face );
		var offset = normal * distance;
		var verts = brush.Vertices.ToList();
		var capLoop = new int[loop.Length];
		for ( var i = 0; i < loop.Length; i++ )
		{
			capLoop[i] = verts.Count;
			verts.Add( brush.Vertices[loop[i]] + offset );
		}
		var faces = brush.Faces.Select( f => (int[]) f.Clone() ).ToList();
		var materials = brush.Materials.ToList();
		faces[face] = capLoop;
		var affected = new List<int> { face };
		for ( var i = 0; i < loop.Length; i++ )
		{
			var a = loop[i];
			var b = loop[( i + 1 ) % loop.Length];
			var ap = capLoop[i];
			var bp = capLoop[( i + 1 ) % loop.Length];
			// Outward only here (negative distances return above): the
			// (a, b, b', a') order points away from the solid.
			faces.Add( new[] { a, b, bp, ap } );
			materials.Add( brush.Materials[face] );
			affected.Add( faces.Count - 1 );
		}
		return Finish( new MapVertexBrush( verts.ToArray(), faces.ToArray(), materials.ToArray() ), affected.ToArray(), opt, volumeBefore );
	}

	// ---------------------------------------------------------------- drag

	/// <summary>Drag a face in 3D by a delta: every vertex of the face loop
	/// moves by the delta (shared vertices move for the adjacent faces
	/// too, which stretch to follow — the vertex-form re-solve). A delta
	/// along the face normal grows/shrinks the solid; an in-plane delta
	/// shears the adjacent faces. Snapped to GridSize when set. Delegates
	/// to <see cref="MapVertexEdit.MoveVertices"/> — the same re-solve as
	/// the 2D vertex-edit path, driven here by a 3D delta.</summary>
	public static DirectEditResult DragFace( MapVertexBrush brush, int face, Vec3 delta, DirectEditOptions options = null )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		RequireFace( brush, face );
		var opt = options ?? new DirectEditOptions();
		delta = SnapDelta( delta, opt );
		var volumeBefore = brush.Volume();
		var targets = brush.Faces[face].Distinct().ToDictionary( v => v, _ => brush.Vertices[_] + delta );
		var moved = MapVertexEdit.MoveVertices( brush, targets, opt.PlanarTolerance );
		return FromVertexEdit( moved, volumeBefore );
	}

	/// <summary>Drag a face along its outward normal by
	/// <paramref name="distance"/>: the in-place move (shared vertices),
	/// as opposed to <see cref="ExtrudeFace"/>, which adds side faces.
	/// Same volume growth (face-area x distance) on a box; the face
	/// count does not change here.</summary>
	public static DirectEditResult DragFaceAlongNormal( MapVertexBrush brush, int face, float distance, DirectEditOptions options = null )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		RequireFace( brush, face );
		if ( !float.IsFinite( distance ) )
			throw new ArgumentException( "drag distance must be finite.", nameof( distance ) );
		return DragFace( brush, face, brush.FaceNormal( face ) * distance, options );
	}

	/// <summary>Drag an edge (both endpoints) in 3D by a delta. Delegates
	/// to <see cref="MapVertexEdit.MoveEdge"/>.</summary>
	public static DirectEditResult DragEdge( MapVertexBrush brush, int a, int b, Vec3 delta, DirectEditOptions options = null )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		var opt = options ?? new DirectEditOptions();
		delta = SnapDelta( delta, opt );
		var volumeBefore = brush.Volume();
		return FromVertexEdit( MapVertexEdit.MoveEdge( brush, a, b, delta, opt.PlanarTolerance ), volumeBefore );
	}

	/// <summary>Drag a vertex in 3D to an absolute position. Delegates to
	/// <see cref="MapVertexEdit.MoveVertex"/> — a 3D vertex drag returns
	/// exactly the 2D vertex-edit result for the same target.</summary>
	public static DirectEditResult DragVertex( MapVertexBrush brush, int vertex, Vec3 to, DirectEditOptions options = null )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		var opt = options ?? new DirectEditOptions();
		var volumeBefore = brush.Volume();
		return FromVertexEdit( MapVertexEdit.MoveVertex( brush, vertex, to, opt.PlanarTolerance ), volumeBefore );
	}

	/// <summary>Drag a vertex in 3D by a delta. Delegates to
	/// <see cref="MapVertexEdit.MoveVertexBy"/>.</summary>
	public static DirectEditResult DragVertexBy( MapVertexBrush brush, int vertex, Vec3 delta, DirectEditOptions options = null )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		var opt = options ?? new DirectEditOptions();
		delta = SnapDelta( delta, opt );
		var volumeBefore = brush.Volume();
		return FromVertexEdit( MapVertexEdit.MoveVertexBy( brush, vertex, delta, opt.PlanarTolerance ), volumeBefore );
	}

	// ------------------------------------------------------- ray-driven drag

	/// <summary>The drag plane for a face: the face's plane through
	/// <paramref name="point"/> (usually the pick hit point), as a
	/// <see cref="ClipPlane"/> — the plane math MapClip builds with
	/// MapClip.PlaneFromTwoPoints, reused here for 3D dragging.</summary>
	public static ClipPlane DragPlaneForFace( MapVertexBrush brush, int face, Vec3 point )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		RequireFace( brush, face );
		var normal = brush.FaceNormal( face );
		if ( normal.LengthSquared < 1e-12f )
			throw new ArgumentException( $"face {face} is degenerate (no normal to drag in).", nameof( face ) );
		return new ClipPlane( normal, Vec3.Dot( normal, point ) );
	}

	/// <summary>Drag delta from a moved pointer ray: intersect the new ray
	/// with the drag plane and measure from the original grab point.
	/// Returns false (delta Zero) when the ray runs parallel to the plane
	/// or points away from it — the drag stalls, it is not guessed.</summary>
	public static bool TryDragInPlane( DirectEditHit hit, DirectEditRay ray, ClipPlane plane, out Vec3 delta )
	{
		delta = Vec3.Zero;
		if ( hit is null || !hit.Hit ) return false;
		var denom = Vec3.Dot( ray.Direction, plane.Normal );
		if ( Math.Abs( denom ) < 1e-9f ) return false;
		var t = ( plane.Offset - Vec3.Dot( ray.Origin, plane.Normal ) ) / denom;
		if ( t < 0 ) return false;
		delta = ray.PointAt( t ) - hit.Point;
		return true;
	}

	/// <summary>Drag delta along one axis from a moved pointer ray: the
	/// point on the axis line (through the grab point, along
	/// <paramref name="axis"/>) closest to the new ray, measured from the
	/// grab point. Returns false (delta Zero) when the axis line and the
	/// ray are parallel — the closest point is undefined, so the drag
	/// stalls instead of guessing.</summary>
	public static bool TryDragAlongAxis( DirectEditHit hit, DirectEditRay ray, Vec3 axis, out Vec3 delta )
	{
		delta = Vec3.Zero;
		if ( hit is null || !hit.Hit ) return false;
		if ( !float.IsFinite( axis.x ) || !float.IsFinite( axis.y ) || !float.IsFinite( axis.z ) || axis.LengthSquared < 1e-12f )
			throw new ArgumentException( "drag axis must be finite and non-zero.", nameof( axis ) );
		var u = axis.Normal;
		var d = ray.Direction;
		// Closest points on lines P + s*u and O + t*d: solve the 2x2.
		var w0 = hit.Point - ray.Origin;
		var a = 1.0; // u.u (unit)
		var b = Vec3.Dot( u, d );
		var c = 1.0; // d.d (unit)
		var dd = Vec3.Dot( u, w0 );
		var e = Vec3.Dot( d, w0 );
		var denom = a * c - b * b;
		if ( Math.Abs( denom ) < 1e-12 ) return false;
		var s = ( b * e - c * dd ) / denom;
		delta = u * (float) s;
		return true;
	}

	// ---------------------------------------------------------- shear/scale

	/// <summary>Shear a brush in 3D about a pivot: p' = p + shearDirection
	/// x factor x dot(p - pivot, axis). The pivot never moves. Axis and
	/// shearDirection must be non-zero and perpendicular (a pure shear,
	/// which preserves volume — determinant 1); anything else throws.
	/// The pivot is the gizmo pivot (GizmoModel, not yet landed — see the
	/// file header); a caller passes GizmoState.Pivot once it exists.</summary>
	public static DirectEditResult Shear3D( MapVertexBrush brush, Vec3 pivot, Vec3 axis, Vec3 shearDirection, float factor, DirectEditOptions options = null )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		RequireAxis( axis, nameof( axis ) );
		RequireAxis( shearDirection, nameof( shearDirection ) );
		if ( !float.IsFinite( factor ) )
			throw new ArgumentException( "shear factor must be finite.", nameof( factor ) );
		var a = axis.Normal;
		var s = shearDirection.Normal;
		if ( Math.Abs( Vec3.Dot( a, s ) ) > 1e-6f )
			throw new ArgumentException( "shear axis and shear direction must be perpendicular (a pure shear preserves volume).", nameof( shearDirection ) );
		var opt = options ?? new DirectEditOptions();
		var volumeBefore = brush.Volume();
		var verts = brush.Vertices.Select( p =>
		{
			var rel = p - pivot;
			return p + s * ( factor * Vec3.Dot( rel, a ) );
		} ).ToArray();
		return Finish( new MapVertexBrush( verts, brush.Faces, brush.Materials ),
			Enumerable.Range( 0, brush.FaceCount ).ToArray(), opt, volumeBefore );
	}

	/// <summary>Scale a brush in 3D about a pivot: p' = pivot +
	/// (p - pivot) x factors (component-wise). The pivot never moves.
	/// A zero factor on any axis throws (same rule as MapTransform: a
	/// zero axis flattens the solid to nothing). A negative factor mirrors
	/// and flips the winding — allowed, and the result comes back with
	/// the flipped faces flagged Inverted, never silently re-wound.
	/// The pivot is the gizmo pivot (GizmoModel, not yet landed).</summary>
	public static DirectEditResult Scale3D( MapVertexBrush brush, Vec3 pivot, Vec3 factors, DirectEditOptions options = null )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( !float.IsFinite( factors.x ) || !float.IsFinite( factors.y ) || !float.IsFinite( factors.z ) )
			throw new ArgumentException( "scale factors must be finite.", nameof( factors ) );
		if ( factors.x == 0f || factors.y == 0f || factors.z == 0f )
			throw new ArgumentException( "Scale cannot be zero on any axis: a zero axis flattens the solid to nothing.", nameof( factors ) );
		var opt = options ?? new DirectEditOptions();
		var volumeBefore = brush.Volume();
		var verts = brush.Vertices.Select( p => new Vec3(
			pivot.x + ( p.x - pivot.x ) * factors.x,
			pivot.y + ( p.y - pivot.y ) * factors.y,
			pivot.z + ( p.z - pivot.z ) * factors.z ) ).ToArray();
		return Finish( new MapVertexBrush( verts, brush.Faces, brush.Materials ),
			Enumerable.Range( 0, brush.FaceCount ).ToArray(), opt, volumeBefore );
	}

	/// <summary>Uniform scale about a pivot. A zero factor throws.</summary>
	public static DirectEditResult Scale3D( MapVertexBrush brush, Vec3 pivot, float uniformFactor, DirectEditOptions options = null )
	{
		if ( !float.IsFinite( uniformFactor ) || uniformFactor == 0f )
			throw new ArgumentException( "Scale factor must be finite and non-zero.", nameof( uniformFactor ) );
		return Scale3D( brush, pivot, new Vec3( uniformFactor, uniformFactor, uniformFactor ), options );
	}

	/// <summary>Scale a whole selection about one pivot (the gizmo pivot):
	/// one result per brush, in the same order; inputs untouched.</summary>
	public static IReadOnlyList<DirectEditResult> ScaleSelection( IReadOnlyList<MapVertexBrush> brushes, Vec3 pivot, Vec3 factors, DirectEditOptions options = null )
	{
		if ( brushes is null ) throw new ArgumentNullException( nameof( brushes ) );
		var results = new DirectEditResult[brushes.Count];
		for ( var i = 0; i < brushes.Count; i++ )
		{
			if ( brushes[i] is null ) throw new ArgumentException( $"brush {i} in the selection is null.", nameof( brushes ) );
			results[i] = Scale3D( brushes[i], pivot, factors, options );
		}
		return results;
	}

	/// <summary>Shear a whole selection about one pivot (the gizmo pivot):
	/// one result per brush, in the same order; inputs untouched.</summary>
	public static IReadOnlyList<DirectEditResult> ShearSelection( IReadOnlyList<MapVertexBrush> brushes, Vec3 pivot, Vec3 axis, Vec3 shearDirection, float factor, DirectEditOptions options = null )
	{
		if ( brushes is null ) throw new ArgumentNullException( nameof( brushes ) );
		var results = new DirectEditResult[brushes.Count];
		for ( var i = 0; i < brushes.Count; i++ )
		{
			if ( brushes[i] is null ) throw new ArgumentException( $"brush {i} in the selection is null.", nameof( brushes ) );
			results[i] = Shear3D( brushes[i], pivot, axis, shearDirection, factor, options );
		}
		return results;
	}

	// ---------------------------------------------------------------- helpers

	static void RequireFace( MapVertexBrush brush, int face )
	{
		if ( face < 0 || face >= brush.FaceCount )
			throw new ArgumentException( $"face {face} is outside 0..{brush.FaceCount - 1}", nameof( face ) );
	}

	static void RequireAxis( Vec3 axis, string name )
	{
		if ( !float.IsFinite( axis.x ) || !float.IsFinite( axis.y ) || !float.IsFinite( axis.z ) || axis.LengthSquared < 1e-12f )
			throw new ArgumentException( "axis must be finite and non-zero.", name );
	}

	/// <summary>Snap a drag delta to the grid (MapTransform.SnapToGrid,
	/// nearest multiple, ties away from zero); unchanged when GridSize
	/// is null.</summary>
	static Vec3 SnapDelta( Vec3 delta, DirectEditOptions opt )
	{
		if ( opt.GridSize is null ) return delta;
		var size = opt.GridSize.Value;
		return new Vec3(
			(float) MapTransform.SnapToGrid( (double) delta.x, size ),
			(float) MapTransform.SnapToGrid( (double) delta.y, size ),
			(float) MapTransform.SnapToGrid( (double) delta.z, size ) );
	}

	static DirectEditResult FromVertexEdit( MapVertexEditResult moved, double volumeBefore ) =>
		new DirectEditResult
		{
			Brush = moved.Brush,
			Validity = moved.Validity,
			AffectedFaces = moved.AffectedFaces,
			VolumeBefore = volumeBefore,
			VolumeAfter = moved.Brush.Volume(),
		};

	static DirectEditResult Finish( MapVertexBrush brush, int[] affected, DirectEditOptions opt, double volumeBefore ) =>
		new DirectEditResult
		{
			Brush = brush,
			Validity = brush.Validate( opt.PlanarTolerance ),
			AffectedFaces = affected,
			VolumeBefore = volumeBefore,
			VolumeAfter = brush.Volume(),
		};
}
