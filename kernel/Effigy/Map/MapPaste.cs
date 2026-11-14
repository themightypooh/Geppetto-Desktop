// Hammer Paste Special API — copies, rotation, pivot, centre-of-original (no UI).
// Board task: hammer-paste-special (worker muse-3-desktop-hammer-28).
//
// WHAT IT DOES. Paste Special as a document operation on a selection of map
// brushes: PasteAtCentre (a copy at the centre of the original — the default
// paste position, not the cursor or the origin), PasteWithRotation (N copies
// rotated by an angle about the pivot), PasteWithOffset, all with grid snap
// applied to the pasted geometry and per-face materials/UVs preserved with
// texture lock. A pasted brush is a valid convex brush or the whole paste is
// rejected with a named reason — never a half-built copy.
//
// DEPENDENCIES, AS LANDED (2026-11-13, checked on disk before writing):
//   * MapTransform HAS landed (kernel/Effigy/Map/MapTransform.cs, worker
//     hammer-04) with exactly the API its desc specifies: SnapToGrid,
//     SnapAngle, TranslationMatrix, RotationMatrix(axis, angle, pivot),
//     Transform(planes, matrix), ApplyTextureLock. This file builds on it
//     directly — no local copies of snap/rotation/texture-lock math.
//   * GizmoModel HAS landed (kernel/Effigy/Map/GizmoModel.cs, worker
//     hammer-32), namespace Effigy, System.Numerics. The pivot comes from
//     there when present: PasteSpecialOptions.FromGizmo(gizmo) copies the
//     gizmo's Pivot, GridSize and (when snapping is on) AngleSnapDegrees.
//     When no gizmo/pivot is supplied the pivot is the selection centre.
//   * MapBrush HAS now landed (kernel/Effigy/Map/MapBrush.cs, restored by
//     worker hammer-01 at 16:08, after this file was written; namespace
//     Effigy; MapBrushFace = Vec3 Normal + float Distance with Normal.p
//     = Distance + FaceMaterial; FromPlanes(IEnumerable&lt;MapBrushFace&gt;),
//     FromVertices, FromFaceVertices, Box(Vec3, Vec3, string);
//     Faces / FaceCount / Vertices / Volume / Centroid / IsValid /
//     Validate() returning MapBrushDefect codes). It was ABSENT when this
//     file was written (and the shared tree still does not compile:
//     MapClip.cs / MapBrushPrimitives.cs reference the old `MapFace`
//     name, not the landed MapBrushFace — a mismatch for the final gate,
//     not this ticket). This file therefore still works on the face
//     representation the core spec and MapTransform both define — one
//     System.Numerics.Plane per face plus one MapUvMapping per face plus
//     one material name per face — carried by MapPasteBrush below, and
//     must be replaced by a thin adapter over the real MapBrush, not
//     duplicated, when the gate reconciles the tree. The adapter sketch
//     at the bottom of this header now uses the LANDED member names.
//     One bridge is still OPEN and belongs to the integrator (or the
//     texture-uv-locks ticket): MapBrush stores FaceUv (face-space UV),
//     while MapTransform's texture lock works on MapUvMapping
//     (world-space Valve-style UV) — no FaceUv &lt;-&gt; MapUvMapping
//     conversion exists in the tree yet, so the adapter carries the
//     material name exactly and the UV mapping through the paste; it
//     must not invent a UV conversion silently.
//
// BRUSH REPRESENTATION. MapPasteBrush is the paste-side working copy of a
// MapBrush (the same role MapVertexBrush plays for vertex editing and
// BrushPlane/MeshFromPlanes play for CSG): face planes + UV mappings +
// material names, in parallel face order. It is NOT a second brush type —
// it carries no persistence and no document ownership — and must be replaced
// by a thin adapter over the real MapBrush, not duplicated, when the core
// lands. Materials are string names (as in MapVertexBrush); UVs are
// MapUvMapping (MapTransform's world-space Valve-style mapping, which is
// what ApplyTextureLock consumes — FaceMaterial's FaceUv is the face-space
// form and is not used here).
//
// GRID SNAP (the off-grid drift fix). A paste transforms the planes exactly
// (MapTransform, rigid, so validity is preserved), solves the copy's
// vertices by plane intersection, snaps every vertex to the grid, then
// re-solves each face's plane from its snapped vertices (Newell normal +
// distance — the same re-solve MapVertexEdit does after a vertex move), so
// the stored planes always match the stored vertices. The snap is the
// choke point: offsets and rotations are NOT pre-snapped separately, the
// final geometry is. Snapping can nudge a face's vertices off their
// best-fit plane by up to ~a grid step, so post-snap validity is judged
// with a snap-aware solve tolerance (the paste grid size): a snapped
// vertex counts as on a face when it is within one grid step of the
// re-solved plane. MapPasteBrush.Validate() takes that tolerance
// (default 1e-3, strict); the paste gate calls it with the grid size and
// records the choice in PasteResult. A paste whose snapped solid is not
// closed, has no positive volume, or moved more than 50% in volume from
// its source is rejected with a named reason.
//
// PIVOT. Options.Pivot (explicit Vector3) wins; else the gizmo pivot when
// the options came from FromGizmo; else the selection centre — the mean of
// all solved vertices of the selection, which is exactly what
// GizmoModel.SetPivotFromSelection computes for the same points, so the
// gizmo and no-gizmo paths agree. Rotation is about the Z axis by default
// (Hammer yaw; the kernel is Z-up per CONTRACTS.md); any axis can be set.
// Rotation happens about the pivot; the offset is applied after the
// rotation, in world space.
//
// TEXTURE LOCK. Default on (the ticket: "texture lock applied"): each
// copy's UV mappings go through MapTransform.ApplyTextureLock with the
// copy's matrix, so the UV of a material point is unchanged by the paste.
// With TextureLock off the mappings are carried unchanged (world-anchored
// texture: Valve's "texture lock ON" behaviour).
//
// RESULTS ARE DATA, NOT EXCEPTIONS. A refused paste returns a PasteResult
// with Ok false, a machine-readable Reason slug (listed below) and a human
// Message, and no brushes. Invalid input is a named reason, never a silent
// fix. Option/brush construction errors (null arrays, mismatched counts,
// zero normals) throw — those are programmer errors, as in MapTransform.
//   paste-selection-null        selection was null
//   paste-selection-empty       selection had no brushes
//   paste-brush-null            brush i in the selection was null
//   paste-source-invalid        a source brush failed Validate() (strict);
//                               Message carries the first defect
//   paste-copies-not-positive   Copies < 1
//   paste-too-many-copies       Copies > MaxCopies (1024, resource guard)
//   paste-grid-not-positive     GridSize not positive/finite
//   paste-angle-snap-not-positive  AngleSnapDegrees negative/non-finite
//   paste-rotation-not-finite   RotationDegrees non-finite
//   paste-rotation-axis-invalid    RotationAxis zero/non-finite
//   paste-offset-not-finite     Offset non-finite
//   paste-pivot-not-finite      explicit Pivot non-finite
//   paste-result-degenerate     a copy collapsed while snapping (a face
//                               kept fewer than 3 distinct snapped vertices,
//                               a zero Newell normal, or the pre-snap solve
//                               lost a face — rigid transforms never do)
//   paste-result-not-convex     the rebuilt copy failed Validate(gridSize);
//                               Message carries the defects
//   paste-result-volume-changed the copy's volume moved more than 50%
//                               from its source (snap-collapse guard)
//
// NO DESKTOP UI. Backend / data-model / API / headless only, per Pooh
// 2026-11-13. Units are the kernel's inches, Z-up (CONTRACTS.md).
//
// MapBrush adapter sketch (final integrator, once the tree reconciles
// the MapFace/MapBrushFace mismatch and the UV bridge exists — see the
// dependency note above):
//   static MapBrush ToMapBrush(MapPasteBrush b) => MapBrush.FromPlanes(
//       b.Planes.Select((p, i) => new MapBrushFace(
//           new Vec3(p.Normal.X, p.Normal.Y, p.Normal.Z), -p.D,
//           new FaceMaterial(b.Materials[i], /* FaceUv from b.UvMappings[i]: OPEN */))));
//   static MapPasteBrush FromMapBrush(MapBrush b) => new MapPasteBrush(
//       b.Faces.Select(f => new Plane(
//           new Vector3(f.Normal.x, f.Normal.y, f.Normal.z), -f.Distance)).ToArray(),
//       b.Faces.Select(f => /* MapUvMapping from f.Uv: OPEN */).ToArray(),
//       b.Faces.Select(f => f.MaterialId).ToArray());
// PasteSpecial itself is unchanged: it pastes MapPasteBrush values, the
// document layer converts at the boundary. Plane sign: MapBrush stores
// Normal.p = Distance, System.Numerics.Plane stores n.x + D = 0, so
// Distance = -D in both directions.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Effigy.Map;

/// <summary>
/// Paste-side working copy of a map brush: one plane, one UV mapping and
/// one material name per face, in parallel face order. See the file header:
/// this is the representation PasteSpecial pastes, not a second brush type.
/// Planes are stored with unit normals (the constructor normalizes, scaling
/// D with the normal). Inputs are copied; a brush is never mutated after
/// construction — every paste returns new brushes.
/// </summary>
public sealed class MapPasteBrush
{
	public IReadOnlyList<Plane> Planes { get; }
	public IReadOnlyList<MapUvMapping> UvMappings { get; }
	public IReadOnlyList<string> Materials { get; }

	public int FaceCount => Planes.Count;

	public MapPasteBrush(
		IReadOnlyList<Plane> planes,
		IReadOnlyList<MapUvMapping> uvMappings = null,
		IReadOnlyList<string> materials = null )
	{
		if ( planes == null ) throw new ArgumentNullException( nameof( planes ) );
		if ( uvMappings != null && uvMappings.Count != planes.Count )
			throw new ArgumentException( "UV mappings must be one per face.", nameof( uvMappings ) );
		if ( materials != null && materials.Count != planes.Count )
			throw new ArgumentException( "Materials must be one per face.", nameof( materials ) );

		var normalized = new Plane[planes.Count];
		for ( int i = 0; i < planes.Count; i++ )
		{
			var n = planes[i].Normal;
			var len = n.Length();
			if ( !float.IsFinite( len ) || len < 1e-12f )
				throw new ArgumentException( $"Face {i} has a zero or non-finite normal.", nameof( planes ) );
			if ( !float.IsFinite( planes[i].D ) )
				throw new ArgumentException( $"Face {i} has a non-finite plane distance.", nameof( planes ) );
			normalized[i] = new Plane( n / len, planes[i].D / len );
		}
		Planes = normalized;
		UvMappings = uvMappings != null
			? uvMappings.ToArray()
			: planes.Select( p => MapUvMapping.DefaultForNormal( p.Normal ) ).ToArray();
		Materials = materials != null
			? materials.Select( m => m ?? "default" ).ToArray()
			: Enumerable.Repeat( "default", planes.Count ).ToArray();
	}

	/// <summary>Build from face planes (UV mappings / materials default when omitted).</summary>
	public static MapPasteBrush FromPlanes(
		IReadOnlyList<Plane> planes,
		IReadOnlyList<MapUvMapping> uvMappings = null,
		IReadOnlyList<string> materials = null ) =>
		new MapPasteBrush( planes, uvMappings, materials );

	/// <summary>
	/// Axis-aligned box, min (inclusive) to max (exclusive corner). Face
	/// order matches MapVertexBrush.Box: bottom, top, front(-Y), back(+Y),
	/// left(-X), right(+X). Every face gets the same material.
	/// </summary>
	public static MapPasteBrush FromBox( Vector3 min, Vector3 max, string material = "default" ) =>
		FromBox( min, max, Enumerable.Repeat( material ?? "default", 6 ).ToArray(), null );

	/// <summary>Axis-aligned box with one material per face, in face order.</summary>
	public static MapPasteBrush FromBox(
		Vector3 min, Vector3 max,
		IReadOnlyList<string> materials,
		IReadOnlyList<MapUvMapping> uvMappings = null )
	{
		if ( !( min.X < max.X ) || !( min.Y < max.Y ) || !( min.Z < max.Z ) )
			throw new ArgumentException( "A box needs min < max on every axis.", nameof( min ) );
		var planes = new[]
		{
			new Plane( new Vector3( 0, 0, -1 ), min.Z ),
			new Plane( new Vector3( 0, 0, 1 ), -max.Z ),
			new Plane( new Vector3( 0, -1, 0 ), min.Y ),
			new Plane( new Vector3( 0, 1, 0 ), -max.Y ),
			new Plane( new Vector3( -1, 0, 0 ), min.X ),
			new Plane( new Vector3( 1, 0, 0 ), -max.X ),
		};
		return new MapPasteBrush( planes, uvMappings, materials );
	}

	/// <summary>
	/// Solved vertices of the solid (triple plane intersections inside
	/// every half-space within tolerance), in first-found order. Empty
	/// when the planes enclose no solid.
	/// </summary>
	public IReadOnlyList<Vector3> Vertices( double tolerance = 1e-3 ) => SolveVertices( tolerance );

	/// <summary>
	/// Vertices of one face: solved vertices lying on that face's plane
	/// within tolerance. Empty when the face has none.
	/// </summary>
	public IReadOnlyList<Vector3> FaceVertices( int faceIndex, double tolerance = 1e-3 )
	{
		if ( faceIndex < 0 || faceIndex >= Planes.Count )
			throw new ArgumentOutOfRangeException( nameof( faceIndex ) );
		var plane = Planes[faceIndex];
		var result = new List<Vector3>();
		foreach ( var v in SolveVertices( tolerance ) )
			if ( MathF.Abs( Vector3.Dot( plane.Normal, v ) + plane.D ) <= tolerance )
				result.Add( v );
		return result;
	}

	/// <summary>Mean of a face's solved vertices; Zero when the face has none.</summary>
	public Vector3 FaceCentroid( int faceIndex, double tolerance = 1e-3 )
	{
		var verts = FaceVertices( faceIndex, tolerance );
		if ( verts.Count == 0 ) return Vector3.Zero;
		var sum = Vector3.Zero;
		foreach ( var v in verts ) sum += v;
		return sum / verts.Count;
	}

	/// <summary>
	/// Rebuild the convex solid from the face planes: plane-intersection
	/// vertex solve (the MapBrush.FromPlanes equivalent — the same
	/// algorithm as MapCsg.MeshFromPlanes, with a caller-supplied
	/// on-plane/inside tolerance so a snapped brush can be judged at its
	/// grid size). Returns null when the planes enclose no valid closed
	/// solid with positive volume. The mesh is a validation vehicle only:
	/// face materials live on the brush, so faces carry their face index
	/// as the mesh material slot.
	/// </summary>
	public PolyMesh SolveMesh( double tolerance = 1e-3 )
	{
		if ( Planes.Count < 4 ) return null;
		var verts = SolveVertices( tolerance );
		if ( verts.Count < 4 ) return null;
		var mesh = new PolyMesh();
		foreach ( var v in verts ) mesh.AddVertex( new Vec3( v.X, v.Y, v.Z ) );
		for ( int fi = 0; fi < Planes.Count; fi++ )
		{
			var plane = Planes[fi];
			var on = new List<int>();
			for ( int vi = 0; vi < verts.Count; vi++ )
				if ( MathF.Abs( Vector3.Dot( plane.Normal, verts[vi] ) + plane.D ) <= tolerance )
					on.Add( vi );
			if ( on.Count < 3 ) continue;
			var c = Vector3.Zero;
			foreach ( var vi in on ) c += verts[vi];
			c /= on.Count;
			var u = Vector3.Normalize( Vector3.Cross( plane.Normal,
				MathF.Abs( plane.Normal.Z ) < 0.9f ? new Vector3( 0, 0, 1 ) : new Vector3( 0, 1, 0 ) ) );
			var v = Vector3.Cross( plane.Normal, u );
			on.Sort( ( a, b ) => MathF.Atan2( Vector3.Dot( verts[a] - c, v ), Vector3.Dot( verts[a] - c, u ) )
				.CompareTo( MathF.Atan2( Vector3.Dot( verts[b] - c, v ), Vector3.Dot( verts[b] - c, u ) ) ) );
			// Winding: the face's Newell normal must point along the plane
			// normal (outward); reverse when the sort came out the other way.
			if ( Vector3.Dot( NewellNormal( verts, on ), plane.Normal ) < 0f ) on.Reverse();
			mesh.AddFace( on.ToArray(), null, fi );
		}
		if ( mesh.FaceCount < 4 ) return null;
		if ( !MeshValidator.Validate( mesh ).IsClosed ) return null;
		if ( mesh.SignedVolume() <= 1e-6f ) return null;
		return mesh;
	}

	/// <summary>Absolute volume of the solved solid; 0 when there is none.</summary>
	public double Volume( double tolerance = 1e-3 )
	{
		var mesh = SolveMesh( tolerance );
		return mesh == null ? 0.0 : Math.Abs( (double) mesh.SignedVolume() );
	}

	/// <summary>
	/// Volume-weighted centroid of the solved solid; the mean of the
	/// vertices for a (near-)zero-volume brush; Zero when there is none.
	/// </summary>
	public Vector3 Centroid( double tolerance = 1e-3 )
	{
		var mesh = SolveMesh( tolerance );
		if ( mesh == null ) return Vector3.Zero;
		double vol6 = 0;
		var acc = Vector3.Zero;
		foreach ( var face in mesh.Faces )
		{
			if ( face.Indices.Length < 3 ) continue;
			var a = ToVector3( mesh.Positions[face.Indices[0]] );
			for ( int i = 1; i + 1 < face.Indices.Length; i++ )
			{
				var b = ToVector3( mesh.Positions[face.Indices[i]] );
				var c = ToVector3( mesh.Positions[face.Indices[i + 1]] );
				var v6 = Vector3.Dot( a, Vector3.Cross( b, c ) );
				vol6 += v6;
				acc += ( a + b + c ) * (float) v6;
			}
		}
		if ( Math.Abs( vol6 ) > 1e-12 ) return acc / (float) ( 4.0 * vol6 );
		var verts = SolveVertices( tolerance );
		if ( verts.Count == 0 ) return Vector3.Zero;
		var mean = Vector3.Zero;
		foreach ( var v in verts ) mean += v;
		return mean / verts.Count;
	}

	/// <summary>
	/// Named defects, never a silent fix. Empty means a valid convex
	/// brush at this tolerance: at least 4 faces, non-empty materials,
	/// and planes enclosing a closed solid with positive volume (the
	/// solve itself is the convexity certificate — every recovered vertex
	/// lies inside every half-space within tolerance). Pass the paste
	/// grid size for a snapped brush; the strict 1e-3 default is for
	/// exact, unsnapped geometry.
	/// </summary>
	public IReadOnlyList<string> Validate( double tolerance = 1e-3 )
	{
		var defects = new List<string>();
		if ( FaceCount < 4 )
			defects.Add( $"brush: {FaceCount} faces (a convex brush needs at least 4)" );
		for ( int i = 0; i < Materials.Count; i++ )
			if ( string.IsNullOrEmpty( Materials[i] ) )
				defects.Add( $"face {i}: material is empty" );
		if ( SolveMesh( tolerance ) == null )
			defects.Add( "brush: planes do not enclose a closed solid with positive volume" );
		return defects;
	}

	/// <summary>True when <see cref="Validate(double)"/> reports no defects at this tolerance.</summary>
	public bool IsValid( double tolerance = 1e-3 ) => Validate( tolerance ).Count == 0;

	/// <summary>
	/// True when every solved vertex sits on a multiple of the grid size
	/// (within tolerance) on every axis — the pasted-geometry-is-on-grid
	/// check. False when the brush has no solved solid.
	/// </summary>
	public bool IsOnGrid( double gridSize, double tolerance = 1e-3 )
	{
		if ( !double.IsFinite( gridSize ) || gridSize <= 0.0 ) return false;
		var verts = SolveVertices( tolerance );
		if ( verts.Count == 0 ) return false;
		foreach ( var v in verts )
		{
			if ( Math.Abs( v.X - MapTransform.SnapToGrid( (double) v.X, gridSize ) ) > tolerance ) return false;
			if ( Math.Abs( v.Y - MapTransform.SnapToGrid( (double) v.Y, gridSize ) ) > tolerance ) return false;
			if ( Math.Abs( v.Z - MapTransform.SnapToGrid( (double) v.Z, gridSize ) ) > tolerance ) return false;
		}
		return true;
	}

	List<Vector3> SolveVertices( double tolerance )
	{
		var verts = new List<Vector3>();
		if ( Planes.Count < 4 ) return verts;
		for ( int i = 0; i < Planes.Count; i++ )
		for ( int j = i + 1; j < Planes.Count; j++ )
		for ( int k = j + 1; k < Planes.Count; k++ )
		{
			if ( !TryIntersectPlanes( Planes[i], Planes[j], Planes[k], out var p ) ) continue;
			if ( !InsideAll( p, tolerance ) ) continue;
			var dup = false;
			foreach ( var v in verts )
				if ( ( v - p ).LengthSquared() < 1e-8f ) { dup = true; break; }
			if ( !dup ) verts.Add( p );
		}
		return verts;
	}

	bool InsideAll( Vector3 p, double tolerance )
	{
		foreach ( var plane in Planes )
			if ( Vector3.Dot( plane.Normal, p ) + plane.D > tolerance )
				return false;
		return true;
	}

	static bool TryIntersectPlanes( Plane a, Plane b, Plane c, out Vector3 p )
	{
		p = default;
		var n1 = a.Normal;
		var n2 = b.Normal;
		var n3 = c.Normal;
		var det = Vector3.Dot( n1, Vector3.Cross( n2, n3 ) );
		if ( MathF.Abs( det ) < 1e-12f ) return false;
		var d1 = -a.D;
		var d2 = -b.D;
		var d3 = -c.D;
		p = ( d1 * Vector3.Cross( n2, n3 ) + d2 * Vector3.Cross( n3, n1 ) + d3 * Vector3.Cross( n1, n2 ) ) / det;
		return float.IsFinite( p.X ) && float.IsFinite( p.Y ) && float.IsFinite( p.Z );
	}

	static Vector3 NewellNormal( IReadOnlyList<Vector3> verts, IReadOnlyList<int> loop )
	{
		var n = Vector3.Zero;
		for ( int i = 0; i < loop.Count; i++ )
		{
			var a = verts[loop[i]];
			var b = verts[loop[( i + 1 ) % loop.Count]];
			n += new Vector3(
				( a.Y - b.Y ) * ( a.Z + b.Z ),
				( a.Z - b.Z ) * ( a.X + b.X ),
				( a.X - b.X ) * ( a.Y + b.Y ) );
		}
		return n;
	}

	static Vector3 NewellNormal( IReadOnlyList<Vector3> points )
	{
		var n = Vector3.Zero;
		for ( int i = 0; i < points.Count; i++ )
		{
			var a = points[i];
			var b = points[( i + 1 ) % points.Count];
			n += new Vector3(
				( a.Y - b.Y ) * ( a.Z + b.Z ),
				( a.Z - b.Z ) * ( a.X + b.X ),
				( a.X - b.X ) * ( a.Y + b.Y ) );
		}
		return n;
	}

	static Vector3 ToVector3( Vec3 v ) => new Vector3( v.x, v.y, v.z );
}

/// <summary>
/// Options for <see cref="MapPaste.PasteSpecial"/>: how many copies, how
/// far each one rotates about the pivot, the offset applied after the
/// rotation, the grid the pasted geometry snaps to, and the pivot itself.
/// Defaults are one copy, no rotation, no offset, grid 16 (the Hammer
/// default, GizmoModel.DefaultGridSize), no angle snap, texture lock on,
/// and no explicit pivot (the selection centre). A null options argument
/// to PasteSpecial means these defaults.
/// </summary>
public sealed class PasteSpecialOptions
{
	/// <summary>Resource guard: no paste produces more than this many copies per brush.</summary>
	public const int MaxCopies = 1024;

	/// <summary>Number of copies per source brush (copy k rotates by k * RotationDegrees). Must be &gt;= 1.</summary>
	public int Copies { get; set; } = 1;

	/// <summary>Rotation per copy in degrees, about RotationAxis through the pivot. 0 = no rotation.</summary>
	public double RotationDegrees { get; set; } = 0.0;

	/// <summary>Rotation axis through the pivot; +Z (Hammer yaw) by default. Must be non-zero and finite.</summary>
	public Vector3 RotationAxis { get; set; } = Vector3.UnitZ;

	/// <summary>World-space offset applied to every copy after its rotation. Snapped via the geometry, not separately.</summary>
	public Vector3 Offset { get; set; } = Vector3.Zero;

	/// <summary>Grid size the pasted vertices snap to (Hammer sizes 1024..0.125; any positive finite size snaps). Must be &gt; 0.</summary>
	public double GridSize { get; set; } = GizmoModel.DefaultGridSize;

	/// <summary>Angle snap in degrees for the rotation (MapTransform.SnapAngle). 0 = no snap; otherwise must be &gt; 0.</summary>
	public double AngleSnapDegrees { get; set; } = 0.0;

	/// <summary>
	/// Explicit pivot (rotation centre). Null means the selection centre
	/// (see <see cref="MapPaste.SelectionCentre"/>). This is the slot the
	/// gizmo/pivot ticket fills: pass gizmo.Pivot here, or use
	/// <see cref="FromGizmo"/>.
	/// </summary>
	public Vector3? Pivot { get; set; } = null;

	/// <summary>True (default): UV mappings are recomputed so the texture stays glued to each face. False: mappings are carried unchanged (world-anchored).</summary>
	public bool TextureLock { get; set; } = true;

	/// <summary>
	/// Options carrying a gizmo's pivot and snap settings: Pivot is the
	/// gizmo's Pivot, GridSize the gizmo's GridSize, and the angle snap
	/// the gizmo's AngleSnapDegrees — but only when the gizmo has
	/// snapping enabled (matching DragToTransform, which snaps only when
	/// SnapEnabled). Copies, rotation and offset are left at their
	/// defaults for the caller to set.
	/// </summary>
	public static PasteSpecialOptions FromGizmo( GizmoModel gizmo )
	{
		if ( gizmo == null ) throw new ArgumentNullException( nameof( gizmo ) );
		return new PasteSpecialOptions
		{
			Pivot = gizmo.Pivot,
			GridSize = gizmo.GridSize,
			AngleSnapDegrees = gizmo.SnapEnabled ? gizmo.AngleSnapDegrees : 0.0,
		};
	}
}

/// <summary>The outcome of one paste. Ok false always carries a machine-readable Reason slug (see the file header) and a human Message, and no brushes.</summary>
public sealed class PasteResult
{
	public PasteResult( bool ok, IReadOnlyList<MapPasteBrush> brushes, string reason, string message, Vector3 pivotUsed, int copiesRequested )
	{
		Ok = ok;
		Brushes = brushes ?? Array.Empty<MapPasteBrush>();
		Reason = reason ?? "";
		Message = message ?? "";
		PivotUsed = pivotUsed;
		CopiesRequested = copiesRequested;
	}

	public bool Ok { get; }
	public IReadOnlyList<MapPasteBrush> Brushes { get; }
	public string Reason { get; }
	public string Message { get; }
	public Vector3 PivotUsed { get; }
	public int CopiesRequested { get; }

	public static PasteResult Succeeded( IReadOnlyList<MapPasteBrush> brushes, Vector3 pivotUsed, int copiesRequested ) =>
		new PasteResult( true, brushes, "", "Paste produced " + brushes.Count + " brush(es).", pivotUsed, copiesRequested );

	public static PasteResult Failed( string reason, string message, int copiesRequested = 0, Vector3 pivotUsed = default ) =>
		new PasteResult( false, Array.Empty<MapPasteBrush>(), reason, message, pivotUsed, copiesRequested );
}

/// <summary>
/// Paste Special: paste copies of a brush selection with rotation about
/// a pivot, an offset, and grid snap — the daily duplication tool
/// (Hammer's Paste Special), as a document operation. No UI; pure math
/// on MapTransform + the gizmo pivot. Inputs are never mutated: every
/// paste returns new brushes, and the source selection comes back
/// untouched. See the file header for the reason slugs and the
/// dependency notes.
/// </summary>
public static class MapPaste
{
	/// <summary>Strict solve tolerance for source validation (exact geometry).</summary>
	public const double StrictTolerance = 1e-3;

	/// <summary>A pasted copy's volume may move this far (fraction of its source volume) before the paste is rejected as a snap collapse.</summary>
	public const double MaxVolumeChangeFraction = 0.5;

	/// <summary>
	/// PasteSpecial(selection, options) -&gt; new brushes. Copy k of each
	/// source brush (k = 1..Copies) is rotated by k * RotationDegrees
	/// (angle-snapped when AngleSnapDegrees &gt; 0) about RotationAxis
	/// through the pivot, then moved by Offset, then grid-snapped; UVs
	/// follow the texture-lock setting and materials survive per face.
	/// The pivot is options.Pivot when set, else the selection centre.
	/// </summary>
	public static PasteResult PasteSpecial( IReadOnlyList<MapPasteBrush> selection, PasteSpecialOptions options = null )
	{
		options ??= new PasteSpecialOptions();
		if ( selection == null )
			return PasteResult.Failed( "paste-selection-null", "A paste needs a selection; none was given." );
		if ( selection.Count == 0 )
			return PasteResult.Failed( "paste-selection-empty", "A paste needs at least one brush in the selection." );
		for ( int i = 0; i < selection.Count; i++ )
			if ( selection[i] == null )
				return PasteResult.Failed( "paste-brush-null", $"Brush {i} in the selection is null." );
		if ( options.Copies < 1 )
			return PasteResult.Failed( "paste-copies-not-positive", $"Copies is {options.Copies}: a paste needs at least 1 copy." );
		if ( options.Copies > PasteSpecialOptions.MaxCopies )
			return PasteResult.Failed( "paste-too-many-copies", $"Copies is {options.Copies}: a paste produces at most {PasteSpecialOptions.MaxCopies} copies per brush." );
		if ( !double.IsFinite( options.GridSize ) || options.GridSize <= 0.0 )
			return PasteResult.Failed( "paste-grid-not-positive", $"Grid size is {options.GridSize}: it must be a positive finite number." );
		if ( !double.IsFinite( options.AngleSnapDegrees ) || options.AngleSnapDegrees < 0.0 )
			return PasteResult.Failed( "paste-angle-snap-not-positive", $"Angle snap is {options.AngleSnapDegrees}: it must be 0 (off) or a positive finite number." );
		if ( !double.IsFinite( options.RotationDegrees ) )
			return PasteResult.Failed( "paste-rotation-not-finite", "Rotation angle must be finite." );
		if ( options.RotationAxis.LengthSquared() < 1e-12f || !IsFinite( options.RotationAxis ) )
			return PasteResult.Failed( "paste-rotation-axis-invalid", "Rotation axis must be finite and non-zero." );
		if ( !IsFinite( options.Offset ) )
			return PasteResult.Failed( "paste-offset-not-finite", "Offset must be finite." );
		if ( options.Pivot.HasValue && !IsFinite( options.Pivot.Value ) )
			return PasteResult.Failed( "paste-pivot-not-finite", "Pivot must be finite." );

		for ( int i = 0; i < selection.Count; i++ )
		{
			var defects = selection[i].Validate( StrictTolerance );
			if ( defects.Count > 0 )
				return PasteResult.Failed( "paste-source-invalid",
					$"Brush {i} is not a valid convex brush: {string.Join( "; ", defects )}" );
		}

		var effectiveAngle = options.AngleSnapDegrees > 0.0
			? MapTransform.SnapAngle( options.RotationDegrees, options.AngleSnapDegrees )
			: options.RotationDegrees;
		var pivot = options.Pivot ?? SelectionCentre( selection );
		var axis = Vector3.Normalize( options.RotationAxis );

		var sourceVolumes = new double[selection.Count];
		for ( int i = 0; i < selection.Count; i++ )
			sourceVolumes[i] = selection[i].Volume( StrictTolerance );

		var pasted = new List<MapPasteBrush>();
		for ( int k = 1; k <= options.Copies; k++ )
		{
			var matrix = MapTransform.RotationMatrix( axis, (float) ( k * effectiveAngle ), pivot )
				* MapTransform.TranslationMatrix( options.Offset );
			for ( int bi = 0; bi < selection.Count; bi++ )
			{
				var copy = PasteOne( selection[bi], matrix, options, k, bi, sourceVolumes[bi], out var failure );
				if ( failure != null ) return failure;
				pasted.Add( copy );
			}
		}
		return PasteResult.Succeeded( pasted, pivot, options.Copies );
	}

	/// <summary>
	/// PasteAtCentre: one copy of each selected brush at the centre of the
	/// original — the default paste position (not the cursor, not the
	/// origin). Copies, rotation and offset are forced to 1 / 0 / zero;
	/// grid size, angle snap, pivot and texture lock come from options
	/// when given. The copy still grid-snaps, so an off-grid source
	/// pastes on-grid.
	/// </summary>
	public static PasteResult PasteAtCentre( IReadOnlyList<MapPasteBrush> selection, PasteSpecialOptions options = null )
	{
		var atCentre = new PasteSpecialOptions
		{
			Copies = 1,
			RotationDegrees = 0.0,
			Offset = Vector3.Zero,
			GridSize = options?.GridSize ?? GizmoModel.DefaultGridSize,
			AngleSnapDegrees = options?.AngleSnapDegrees ?? 0.0,
			Pivot = options?.Pivot,
			TextureLock = options?.TextureLock ?? true,
		};
		return PasteSpecial( selection, atCentre );
	}

	/// <summary>
	/// PasteWithRotation: N copies, copy k rotated by k * angleDegrees
	/// about the pivot (angle-snapped when the options carry a snap).
	/// Offset stays as the options carry it (zero by default). Four
	/// copies at 90 degrees return the last copy to its start orientation.
	/// </summary>
	public static PasteResult PasteWithRotation(
		IReadOnlyList<MapPasteBrush> selection, int copies, double angleDegrees,
		PasteSpecialOptions options = null )
	{
		var withRotation = CloneWith( options );
		withRotation.Copies = copies;
		withRotation.RotationDegrees = angleDegrees;
		return PasteSpecial( selection, withRotation );
	}

	/// <summary>
	/// PasteWithOffset: copies moved by offset (then grid-snapped, so an
	/// offset of 10 on grid 16 lands at 16). Copies and rotation stay as
	/// the options carry them (1 copy, no rotation by default).
	/// </summary>
	public static PasteResult PasteWithOffset(
		IReadOnlyList<MapPasteBrush> selection, Vector3 offset,
		PasteSpecialOptions options = null )
	{
		var withOffset = CloneWith( options );
		withOffset.Offset = offset;
		return PasteSpecial( selection, withOffset );
	}

	/// <summary>
	/// Selection centre: the mean of all solved vertices of the selection
	/// — exactly what GizmoModel.SetPivotFromSelection computes for the
	/// same points, so the no-gizmo default pivot and the gizmo pivot
	/// agree. Zero when no brush solves (callers validate sources first).
	/// </summary>
	public static Vector3 SelectionCentre( IReadOnlyList<MapPasteBrush> selection )
	{
		if ( selection == null ) throw new ArgumentNullException( nameof( selection ) );
		if ( selection.Count == 0 )
			throw new ArgumentException( "A selection needs at least one brush to place the pivot.", nameof( selection ) );
		var sum = Vector3.Zero;
		var count = 0;
		foreach ( var brush in selection )
		{
			if ( brush == null ) continue;
			foreach ( var v in brush.Vertices( StrictTolerance ) ) { sum += v; count++; }
		}
		return count == 0 ? Vector3.Zero : sum / count;
	}

	static PasteSpecialOptions CloneWith( PasteSpecialOptions options )
	{
		if ( options == null ) return new PasteSpecialOptions();
		return new PasteSpecialOptions
		{
			Copies = options.Copies,
			RotationDegrees = options.RotationDegrees,
			RotationAxis = options.RotationAxis,
			Offset = options.Offset,
			GridSize = options.GridSize,
			AngleSnapDegrees = options.AngleSnapDegrees,
			Pivot = options.Pivot,
			TextureLock = options.TextureLock,
		};
	}

	static MapPasteBrush PasteOne(
		MapPasteBrush source, Matrix4x4 matrix, PasteSpecialOptions options,
		int copyIndex, int brushIndex, double sourceVolume, out PasteResult failure )
	{
		failure = null;
		var planes = MapTransform.Transform( source.Planes, matrix );
		var uvs = new MapUvMapping[source.FaceCount];
		for ( int i = 0; i < source.FaceCount; i++ )
			uvs[i] = MapTransform.ApplyTextureLock( source.UvMappings[i], matrix, options.TextureLock );

		// Snap: solve the exactly-transformed copy, snap its vertices to
		// the grid, re-solve each face plane from its snapped vertices.
		// A rigid transform preserves the face structure, so the solved
		// mesh must have exactly one face per plane; anything else is a
		// degenerate copy, not a brush to repair silently.
		var transformed = new MapPasteBrush( planes, uvs, source.Materials );
		var mesh = transformed.SolveMesh( StrictTolerance );
		if ( mesh == null || mesh.FaceCount != planes.Length )
		{
			failure = PasteResult.Failed( "paste-result-degenerate",
				$"Copy {copyIndex} of brush {brushIndex} has no closed solid after its transform." );
			return null;
		}
		var snapped = new Vector3[mesh.Positions.Count];
		for ( int i = 0; i < mesh.Positions.Count; i++ )
		{
			var p = mesh.Positions[i];
			snapped[i] = MapTransform.SnapToGrid( new Vector3( p.x, p.y, p.z ), options.GridSize );
		}
		var snappedPlanes = new Plane[planes.Length];
		for ( int fi = 0; fi < planes.Length; fi++ )
		{
			var loop = mesh.Faces[fi].Indices;
			var points = new List<Vector3>();
			foreach ( var vi in loop )
			{
				var s = snapped[vi];
				var dup = false;
				foreach ( var q in points )
					if ( ( q - s ).LengthSquared() < 1e-8f ) { dup = true; break; }
				if ( !dup ) points.Add( s );
			}
			if ( points.Count < 3 )
			{
				failure = PasteResult.Failed( "paste-result-degenerate",
					$"Copy {copyIndex} of brush {brushIndex} collapsed while snapping: face {fi} kept fewer than 3 distinct vertices on grid {options.GridSize}." );
				return null;
			}
			var normal = NewellNormal( points );
			if ( normal.LengthSquared() < 1e-12f )
			{
				failure = PasteResult.Failed( "paste-result-degenerate",
					$"Copy {copyIndex} of brush {brushIndex} collapsed while snapping: face {fi} has a zero normal after the snap." );
				return null;
			}
			normal = Vector3.Normalize( normal );
			snappedPlanes[fi] = new Plane( normal, -Vector3.Dot( normal, points[0] ) );
		}

		var copy = new MapPasteBrush( snappedPlanes, uvs, source.Materials );
		var defects = copy.Validate( options.GridSize );
		if ( defects.Count > 0 )
		{
			failure = PasteResult.Failed( "paste-result-not-convex",
				$"Copy {copyIndex} of brush {brushIndex} is not a valid convex brush after grid snap: {string.Join( "; ", defects )}" );
			return null;
		}
		var volume = copy.Volume( options.GridSize );
		if ( sourceVolume > 0.0 && Math.Abs( volume - sourceVolume ) / sourceVolume > MaxVolumeChangeFraction )
		{
			failure = PasteResult.Failed( "paste-result-volume-changed",
				$"Copy {copyIndex} of brush {brushIndex} changed volume from {sourceVolume:0.##} to {volume:0.##} after grid snap (more than {MaxVolumeChangeFraction:P0})." );
			return null;
		}
		return copy;
	}

	static Vector3 NewellNormal( IReadOnlyList<Vector3> points )
	{
		var n = Vector3.Zero;
		for ( int i = 0; i < points.Count; i++ )
		{
			var a = points[i];
			var b = points[( i + 1 ) % points.Count];
			n += new Vector3(
				( a.Y - b.Y ) * ( a.Z + b.Z ),
				( a.Z - b.Z ) * ( a.X + b.X ),
				( a.X - b.X ) * ( a.Y + b.Y ) );
		}
		return n;
	}

	static bool IsFinite( Vector3 v ) => float.IsFinite( v.X ) && float.IsFinite( v.Y ) && float.IsFinite( v.Z );
}
