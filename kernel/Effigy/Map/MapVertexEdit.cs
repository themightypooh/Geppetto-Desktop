using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Per-face defect flags for a map brush — the vertex-edit extension of the
/// validity checker from hammer-brush-core-plane-based-brushes.
///
/// DEPENDENCY NOTE (2026-11-13, muse-3-desktop-hammer-07): the core task
/// (hammer-brush-core-plane-based-brushes, worker muse-3-desktop-hammer-01)
/// had NOT landed when this file was written — there is no
/// kernel/Effigy/Map/MapBrush.cs on disk yet, so this file cannot call
/// <c>MapBrush.Validate()</c> and must not invent a competing MapBrush type.
/// It is therefore written against the core spec exactly as filed:
///   * a MapBrush is faces-as-planes (normal + distance) + per-face material
///     id + UV offset/scale/rotation + lightmap scale + smoothing group;
///   * <c>FromPlanes</c> / <c>FromVertices</c> constructors, plane-intersection
///     vertex solve, volume + centroid, <c>IsValid</c> + <c>Validate()</c>
///     returning a list of named defects; invalid = named reason, never a
///     silent fix.
/// The bridge, for the final integrator once MapBrush lands, is
/// <c>MapVertexBrush</c> below: it is the VERTEX-FORM working copy of a
/// MapBrush (the form a vertex/edge drag actually edits), built from
/// <c>MapBrush.FromVertices</c> output / feeding back into it via
/// <c>MapVertexBrush.ToPlanes()</c> (re-solved adjacent planes). It is NOT a
/// second brush type — it carries no plane set, no UVs, no persistence — and
/// must be replaced by a thin adapter over the real MapBrush, not duplicated,
/// when the core lands. The per-face flags here are the extension the core
/// checker lacks: <c>MapBrush.Validate()</c> reports brush-level named
/// defects; <see cref="MapVertexEdit.Validate"/> reports the same defects
/// PER FACE so a caller can highlight invalid faces (Hammer++: red, realtime).
///
/// Rules this file enforces (ticket hammer-brush-vertex-edit-invalid-face):
///   * every edit returns a NEW brush + a per-face validity report; the
///     input brush is never mutated;
///   * an edit that twists a brush out of shape is RETURNED, not repaired —
///     the twisted vertices stay exactly where the drag put them and the
///     affected faces are flagged (NonPlanar / NonConvex / Degenerate /
///     Inverted / DuplicateFace) with a named reason;
///   * plane re-solve: after a vertex/edge move, each adjacent face's plane
///     is re-derived from its (moved) vertices (Newell normal + distance),
///     so the plane set always matches the vertices;
///   * watertight/manifold is checked by reusing <see cref="GeometryLinter"/>
///     on the fan-triangulated mesh, where it applies — the linter cannot see
///     non-planarity (a fan-triangulated quad is planar triangle by triangle),
///     which is exactly why the per-face flags exist.
/// No UI, no Hammer app, no shell-out: backend/data-model only. Units are the
/// kernel's inches, Z-up (CONTRACTS.md).
/// </summary>
[Flags]
public enum MapFaceDefect
{
	None = 0,
	/// <summary>Face vertices are not coplanar (max deviation &gt; tolerance).</summary>
	NonPlanar = 1,
	/// <summary>Face polygon has a reflex (interior &gt; 180 deg) vertex.</summary>
	NonConvex = 2,
	/// <summary>Face has fewer than 3 distinct vertices (collapsed / repeated).</summary>
	Degenerate = 4,
	/// <summary>Face area is ~0 (collinear / coincident vertices).</summary>
	ZeroArea = 8,
	/// <summary>Face normal points at the brush centroid instead of away from it.</summary>
	Inverted = 16,
	/// <summary>Another face uses exactly the same vertex set.</summary>
	DuplicateFace = 32,
}

/// <summary>Validity of one face, for invalid-face highlighting.</summary>
public sealed class MapFaceValidity
{
	public int FaceIndex;
	public MapFaceDefect Defects;
	public bool IsValid => Defects == MapFaceDefect.None;
	/// <summary>Named reasons, MapBrush.Validate()-style: "face 1: non-planar (max deviation 0.5)".</summary>
	public List<string> Reasons = new List<string>();
	/// <summary>Largest distance of a face vertex from the face's best-fit plane.</summary>
	public double MaxPlanarDeviation;
	public double Area;
	public Vec3 Normal;
	/// <summary>Re-solved plane: Normal . x = Distance.</summary>
	public float PlaneDistance;
}

/// <summary>Whole-brush validity: per-face flags + brush-level named defects.</summary>
public sealed class MapVertexValidity
{
	public bool IsValid;
	/// <summary>One entry per face, in face order.</summary>
	public List<MapFaceValidity> Faces = new List<MapFaceValidity>();
	/// <summary>Brush-level named defects, same shape as MapBrush.Validate(): e.g.
	/// "face 1: non-planar (max deviation 0.5)", "brush: zero volume",
	/// "brush: 2 boundary edges (not watertight)".</summary>
	public List<string> Defects = new List<string>();
	/// <summary>Indices of invalid faces — the highlight set (Hammer++: red).</summary>
	public int[] InvalidFaceIndices = Array.Empty<int>();
	/// <summary>GeometryLinter result on the fan-triangulated mesh (watertight /
	/// manifold / Euler), where it applies. Null only if the brush has no faces.</summary>
	public LintResult Lint;
	public double Volume;
}

/// <summary>
/// Vertex-form working copy of a MapBrush: ordered vertex loops per face.
/// See the file header: this is the edit-side representation, not a second
/// brush type. Faces are outward-wound (Newell normal points away from the
/// brush centroid for a valid brush); <see cref="MapVertexEdit"/> never
/// re-winds a face — a flipped face is reported as <c>Inverted</c>, not fixed.
/// </summary>
public sealed class MapVertexBrush
{
	public Vec3[] Vertices { get; }
	public int[][] Faces { get; }
	/// <summary>Per-face material id, carried through edits/splits (index = face).</summary>
	public string[] Materials { get; }

	public int VertexCount => Vertices.Length;
	public int FaceCount => Faces.Length;

	public MapVertexBrush( Vec3[] vertices, int[][] faces, string[] materials = null )
	{
		Vertices = vertices is null ? Array.Empty<Vec3>() : (Vec3[]) vertices.Clone();
		Faces = faces is null ? Array.Empty<int[]>()
			: faces.Select( f => f is null ? Array.Empty<int>() : (int[]) f.Clone() ).ToArray();
		Materials = materials is null
			? Enumerable.Repeat( "default", Faces.Length ).ToArray()
			: Enumerable.Range( 0, Faces.Length )
				.Select( i => i < materials.Length && materials[i] != null ? materials[i] : "default" )
				.ToArray();
	}

	/// <summary>Cube centred on the origin, side 2*half. Face order: bottom, top,
	/// front(-Y), back(+Y), left(-X), right(+X). Outward wound.</summary>
	public static MapVertexBrush Box( float half = 1f )
	{
		var h = half;
		var v = new[]
		{
			new Vec3( -h, -h, -h ), new Vec3( h, -h, -h ), new Vec3( h, h, -h ), new Vec3( -h, h, -h ),
			new Vec3( -h, -h, h ), new Vec3( h, -h, h ), new Vec3( h, h, h ), new Vec3( -h, h, h ),
		};
		var f = new[]
		{
			new[] { 0, 3, 2, 1 },
			new[] { 4, 5, 6, 7 },
			new[] { 0, 1, 5, 4 },
			new[] { 3, 7, 6, 2 },
			new[] { 0, 4, 7, 3 },
			new[] { 1, 2, 6, 5 },
		};
		return new MapVertexBrush( v, f );
	}

	/// <summary>Square pyramid: base square (side 2*halfBase) on z=0, apex on +Z.
	/// Face order: base, front, right, back, left.</summary>
	public static MapVertexBrush SquarePyramid( float halfBase = 1f, float height = 2f )
	{
		var h = halfBase;
		var v = new[]
		{
			new Vec3( -h, -h, 0 ), new Vec3( h, -h, 0 ), new Vec3( h, h, 0 ), new Vec3( -h, h, 0 ),
			new Vec3( 0, 0, height ),
		};
		var f = new[]
		{
			new[] { 0, 3, 2, 1 },
			new[] { 0, 1, 4 },
			new[] { 1, 2, 4 },
			new[] { 2, 3, 4 },
			new[] { 3, 0, 4 },
		};
		return new MapVertexBrush( v, f );
	}

	/// <summary>Signed volume (divergence theorem over fan triangles); positive
	/// for outward-wound brushes. Absolute value is the brush volume.</summary>
	public double SignedVolume()
	{
		double v = 0;
		foreach ( var face in Faces )
		{
			if ( face.Length < 3 ) continue;
			var a = Vertices[face[0]];
			for ( var i = 1; i + 1 < face.Length; i++ )
			{
				var b = Vertices[face[i]];
				var c = Vertices[face[i + 1]];
				v += Vec3.Dot( a, Vec3.Cross( b, c ) ) / 6.0;
			}
		}
		return v;
	}

	public double Volume() => Math.Abs( SignedVolume() );

	/// <summary>Volume-weighted centroid; falls back to the mean of the
	/// vertices for a (near-)zero-volume brush.</summary>
	public Vec3 Centroid()
	{
		double vol6 = 0;
		var acc = Vec3.Zero;
		foreach ( var face in Faces )
		{
			if ( face.Length < 3 ) continue;
			var a = Vertices[face[0]];
			for ( var i = 1; i + 1 < face.Length; i++ )
			{
				var b = Vertices[face[i]];
				var c = Vertices[face[i + 1]];
				var v6 = Vec3.Dot( a, Vec3.Cross( b, c ) );
				vol6 += v6;
				acc += ( a + b + c ) * (float) v6;
			}
		}
		if ( Math.Abs( vol6 ) > 1e-12 )
			return acc / (float) ( 4.0 * vol6 );
		if ( Vertices.Length == 0 ) return Vec3.Zero;
		var mean = Vec3.Zero;
		foreach ( var p in Vertices ) mean += p;
		return mean / Vertices.Length;
	}

	/// <summary>Newell normal of a face (unit, or Zero for a degenerate face).</summary>
	public Vec3 FaceNormal( int faceIndex )
	{
		var face = Faces[faceIndex];
		var n = Vec3.Zero;
		for ( var i = 0; i < face.Length; i++ )
		{
			var a = Vertices[face[i]];
			var b = Vertices[face[( i + 1 ) % face.Length]];
			n += new Vec3(
				( a.y - b.y ) * ( a.z + b.z ),
				( a.z - b.z ) * ( a.x + b.x ),
				( a.x - b.x ) * ( a.y + b.y ) );
		}
		return n.Normal;
	}

	/// <summary>Re-solved plane of a face from its current vertices:
	/// (Normal, Distance) with Normal . x = Distance. This is the "re-solve
	/// adjacent planes" step after a vertex/edge move.</summary>
	public (Vec3 Normal, float Distance) FacePlane( int faceIndex )
	{
		var n = FaceNormal( faceIndex );
		var face = Faces[faceIndex];
		var d = face.Length == 0 ? 0f : Vec3.Dot( n, Vertices[face[0]] );
		return ( n, d );
	}

	/// <summary>Re-solved planes for all faces, in face order — the plane set
	/// a MapBrush.FromPlanes rebuild consumes.</summary>
	public (Vec3 Normal, float Distance)[] ToPlanes()
	{
		var planes = new (Vec3, float)[Faces.Length];
		for ( var i = 0; i < Faces.Length; i++ ) planes[i] = FacePlane( i );
		return planes;
	}

	/// <summary>Fan-triangulated area of a face (0 for &lt;3 vertices).</summary>
	public double FaceArea( int faceIndex )
	{
		var face = Faces[faceIndex];
		if ( face.Length < 3 ) return 0;
		double area = 0;
		var a = Vertices[face[0]];
		for ( var i = 1; i + 1 < face.Length; i++ )
			area += Vec3.Cross( Vertices[face[i]] - a, Vertices[face[i + 1]] - a ).Length / 2.0;
		return area;
	}

	/// <summary>Fan triangulation of the whole brush (for GeometryLinter).</summary>
	public LintMesh ToLintMesh()
	{
		var tris = new List<int[]>();
		foreach ( var face in Faces )
		{
			if ( face.Length < 3 ) continue;
			for ( var i = 1; i + 1 < face.Length; i++ )
				tris.Add( new[] { face[0], face[i], face[i + 1] } );
		}
		return new LintMesh( (Vec3[]) Vertices.Clone(), tris.ToArray() );
	}

	/// <summary>Per-face validity + brush-level named defects. This is the
	/// per-face extension of MapBrush.Validate(): same named-defect shape,
	/// plus one <see cref="MapFaceValidity"/> per face with defect flags.</summary>
	public MapVertexValidity Validate( double planarTolerance = 1e-3 )
	{
		var result = new MapVertexValidity { Volume = Volume() };
		var centroid = Centroid();
		var seenSets = new Dictionary<string, int>();
		for ( var fi = 0; fi < Faces.Length; fi++ )
		{
			var face = Faces[fi];
			var fv = new MapFaceValidity { FaceIndex = fi, Area = FaceArea( fi ) };
			var (normal, dist) = FacePlane( fi );
			fv.Normal = normal;
			fv.PlaneDistance = dist;

			// Distinct vertices (a repeated vertex collapses the face).
			// Distinct by POSITION, not index: collapsing several vertices
			// onto one point collapses the face even though the indices differ.
			var distinct = face.Select( vi => Vertices[vi] ).Distinct().Count();
			if ( face.Length < 3 || distinct < 3 )
			{
				fv.Defects |= MapFaceDefect.Degenerate;
				fv.Reasons.Add( $"face {fi}: degenerate (only {distinct} distinct vertices)" );
			}
			if ( fv.Area <= 1e-12 )
			{
				fv.Defects |= MapFaceDefect.ZeroArea;
				fv.Reasons.Add( $"face {fi}: zero area" );
			}

			// Planarity: distance of each vertex from the best-fit plane
			// (Newell normal through the face centroid).
			if ( face.Length >= 3 && distinct >= 3 )
			{
				var fc = Vec3.Zero;
				foreach ( var vi in face ) fc += Vertices[vi];
				fc /= face.Length;
				double maxDev = 0;
				foreach ( var vi in face )
					maxDev = Math.Max( maxDev, Math.Abs( Vec3.Dot( normal, Vertices[vi] - fc ) ) );
				fv.MaxPlanarDeviation = maxDev;
				if ( maxDev > planarTolerance )
				{
					fv.Defects |= MapFaceDefect.NonPlanar;
					fv.Reasons.Add( $"face {fi}: non-planar (max deviation {maxDev:0.###})" );
				}
			}

			// Convexity: project onto the face plane, check the turn sign.
			if ( face.Length >= 4 && distinct >= 4 && normal.LengthSquared > 1e-12f )
			{
				var u = Math.Abs( normal.x ) < 0.9f
					? Vec3.Cross( new Vec3( 1, 0, 0 ), normal ).Normal
					: Vec3.Cross( new Vec3( 0, 1, 0 ), normal ).Normal;
				var w = Vec3.Cross( normal, u );
				var pts = face.Select( vi => new Vec2( Vec3.Dot( Vertices[vi], u ), Vec3.Dot( Vertices[vi], w ) ) ).ToArray();
				double sign = 0;
				for ( var i = 0; i < pts.Length; i++ )
				{
					var a = pts[i];
					var b = pts[( i + 1 ) % pts.Length];
					var c = pts[( i + 2 ) % pts.Length];
					var cross = Vec2.Cross( b - a, c - b );
					if ( Math.Abs( cross ) <= 1e-9 ) continue; // collinear edge: no turn
					if ( sign == 0 ) sign = Math.Sign( cross );
					else if ( Math.Sign( cross ) != Math.Sign( sign ) )
					{
						fv.Defects |= MapFaceDefect.NonConvex;
						fv.Reasons.Add( $"face {fi}: non-convex (reflex vertex {face[( i + 1 ) % face.Length]})" );
						break;
					}
				}
			}

			// Winding: normal must point away from the brush centroid.
			// (Heuristic for strongly non-convex brushes, same caveat as
			// GeometryLinter; exact for convex brushes, which is the
			// MapBrush domain.)
			if ( face.Length >= 3 && normal.LengthSquared > 1e-12f )
			{
				var fc = Vec3.Zero;
				foreach ( var vi in face ) fc += Vertices[vi];
				fc /= face.Length;
				if ( Vec3.Dot( normal, fc - centroid ) < -1e-6f )
				{
					fv.Defects |= MapFaceDefect.Inverted;
					fv.Reasons.Add( $"face {fi}: inverted (normal points at the brush centroid)" );
				}
			}

			// Duplicate face: same vertex set as an earlier face.
			var key = string.Join( ",", face.OrderBy( x => x ) );
			if ( seenSets.TryGetValue( key, out var first ) )
			{
				fv.Defects |= MapFaceDefect.DuplicateFace;
				fv.Reasons.Add( $"face {fi}: duplicate of face {first} (same vertices)" );
			}
			else seenSets[key] = fi;

			result.Faces.Add( fv );
			result.Defects.AddRange( fv.Reasons );
		}

		if ( Faces.Length == 0 )
			result.Defects.Add( "brush: no faces" );
		if ( result.Volume <= 1e-12 )
			result.Defects.Add( $"brush: zero volume ({result.Volume:0.###})" );

		// Watertight / manifold via GeometryLinter, where it applies.
		if ( Faces.Length > 0 )
		{
			var lint = GeometryLinter.Lint( ToLintMesh() );
			result.Lint = lint;
			if ( lint.BoundaryEdges > 0 )
				result.Defects.Add( $"brush: {lint.BoundaryEdges} boundary edges (not watertight)" );
			if ( lint.NonManifoldEdges > 0 )
				result.Defects.Add( $"brush: {lint.NonManifoldEdges} non-manifold edges" );
		}

		result.InvalidFaceIndices = result.Faces.Where( f => !f.IsValid ).Select( f => f.FaceIndex ).ToArray();
		result.IsValid = result.Defects.Count == 0;
		return result;
	}
}

/// <summary>Result of one vertex/edge edit: the new brush + its validity.</summary>
public sealed class MapVertexEditResult
{
	public MapVertexBrush Brush;
	/// <summary>Per-face validity after the edit (invalid faces named, never hidden).</summary>
	public MapVertexValidity Validity;
	/// <summary>Faces touched by the edit (moved vertex/edge incident faces, or the split face's children).</summary>
	public int[] AffectedFaces = Array.Empty<int>();
	public bool IsValid => Validity != null && Validity.IsValid;
	/// <summary>Highlight set for the caller: indices of invalid faces.</summary>
	public int[] InvalidFaceIndices => Validity?.InvalidFaceIndices ?? Array.Empty<int>();
}

/// <summary>How <see cref="MapVertexEdit.TriangulateInvalid"/> produced its pieces.</summary>
public enum MapTriangulationMode
{
	/// <summary>The input was already valid; the single piece is the input brush, untouched.</summary>
	AlreadyValid,
	/// <summary>Non-planar / non-convex faces were fan-triangulated into triangles
	/// (exact: the same fan SignedVolume() uses, so the volume is preserved).</summary>
	FanTriangulation,
	/// <summary>The fan-triangulated brush was still invalid, so the brush was split
	/// into convex hull pieces via ConvexDecomp.</summary>
	ConvexDecomposition,
	/// <summary>No fully-valid piece set could be produced; Pieces holds the
	/// best-effort result (the fan-triangulated brush, or the hull pieces),
	/// AllValid is false, and Reason names why.</summary>
	Failed,
}

/// <summary>Result of <see cref="MapVertexEdit.TriangulateInvalid"/>: the valid pieces
/// (if any), how they were produced, and whether the volume survived. Triangulation is
/// explicit only: nothing in MapVertexEdit calls this on its own — a plain vertex move
/// returns the twisted brush with its faces flagged, and only an explicit
/// TriangulateInvalid call repairs it.</summary>
public sealed class MapTriangulationResult
{
	/// <summary>The pieces: one piece for the fan path, one per convex hull for the
	/// ConvexDecomp path, the input brush itself when it was already valid.</summary>
	public List<MapVertexBrush> Pieces = new List<MapVertexBrush>();
	/// <summary>Per-piece validity, same order as <see cref="Pieces"/>.</summary>
	public List<MapVertexValidity> PieceValidity = new List<MapVertexValidity>();
	/// <summary>True when at least one face was actually triangulated or the brush
	/// was split into hulls; false when the input was already valid (or when a
	/// failed triangulation changed nothing).</summary>
	public bool WasTriangulated;
	public MapTriangulationMode Mode;
	public double OriginalVolume;
	/// <summary>Sum of the piece volumes.</summary>
	public double TotalVolume;
	/// <summary>|TotalVolume - OriginalVolume| within tolerance (relative to
	/// max(1, |OriginalVolume|)). The fan path preserves it exactly; the
	/// ConvexDecomp path reports it honestly — hulls of a vertex subset do not
	/// in general enclose the original volume.</summary>
	public bool VolumePreserved;
	/// <summary>Every returned piece validates.</summary>
	public bool AllValid;
	/// <summary>Empty on success; a named reason when Mode is Failed (or when hull
	/// pieces are not all valid).</summary>
	public string Reason = "";
}

/// <summary>
/// Vertex/edge manipulation for a MapBrush (hammer-brush-vertex-edit-invalid-face).
/// All edits are pure: they return a new brush and never mutate the input, and
/// they never silently repair an invalid result — a twisted brush comes back
/// twisted, with the bad faces flagged in the returned validity report.
///
/// P2 (hammer-vertex-multi-edit-autotriangulate) adds, in the same class and the
/// same pure style: MoveVertices(set, delta) / MoveEdges(set, delta) /
/// MoveFaces(set, delta) — one operation, one returned result for the caller to
/// record as a single undoable command, validity reported per face afterwards —
/// and the EXPLICIT repair path TriangulateInvalid(brush). A plain multi-vertex
/// move still never triangulates: it returns the same face topology, twisted
/// faces flagged, and only TriangulateInvalid splits an invalid brush into
/// valid pieces (fan triangulation first, ConvexDecomp hulls only when the fan
/// result is still invalid).
/// </summary>
public static class MapVertexEdit
{
	/// <summary>Per-face validity extension of MapBrush.Validate().</summary>
	public static MapVertexValidity Validate( MapVertexBrush brush, double planarTolerance = 1e-3 )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		return brush.Validate( planarTolerance );
	}

	/// <summary>Named brush-level defects, MapBrush.Validate() shape.</summary>
	public static List<string> ValidateDefects( MapVertexBrush brush, double planarTolerance = 1e-3 ) =>
		Validate( brush, planarTolerance ).Defects;

	/// <summary>Indices of invalid faces — the highlight set (Hammer++: red).</summary>
	public static int[] InvalidFaces( MapVertexBrush brush, double planarTolerance = 1e-3 ) =>
		Validate( brush, planarTolerance ).InvalidFaceIndices;

	/// <summary>Move one vertex to an absolute position. Adjacent faces'
	/// planes are re-solved from the moved vertices (see FacePlane).</summary>
	public static MapVertexEditResult MoveVertex( MapVertexBrush brush, int vertex, Vec3 to, double planarTolerance = 1e-3 )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( vertex < 0 || vertex >= brush.VertexCount )
			throw new ArgumentException( $"vertex {vertex} is outside 0..{brush.VertexCount - 1}", nameof( vertex ) );
		var verts = (Vec3[]) brush.Vertices.Clone();
		verts[vertex] = to;
		return Finish( new MapVertexBrush( verts, brush.Faces, brush.Materials ), IncidentFaces( brush, vertex ), planarTolerance );
	}

	/// <summary>Move one vertex by a delta.</summary>
	public static MapVertexEditResult MoveVertexBy( MapVertexBrush brush, int vertex, Vec3 delta, double planarTolerance = 1e-3 )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( vertex < 0 || vertex >= brush.VertexCount )
			throw new ArgumentException( $"vertex {vertex} is outside 0..{brush.VertexCount - 1}", nameof( vertex ) );
		return MoveVertex( brush, vertex, brush.Vertices[vertex] + delta, planarTolerance );
	}

	/// <summary>Move several vertices at once (e.g. collapsing a top face to
	/// a pyramid apex). Keys are vertex indices, values absolute positions.</summary>
	public static MapVertexEditResult MoveVertices( MapVertexBrush brush, IReadOnlyDictionary<int, Vec3> targets, double planarTolerance = 1e-3 )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( targets is null ) throw new ArgumentNullException( nameof( targets ) );
		foreach ( var kv in targets )
			if ( kv.Key < 0 || kv.Key >= brush.VertexCount )
				throw new ArgumentException( $"vertex {kv.Key} is outside 0..{brush.VertexCount - 1}", nameof( targets ) );
		var verts = (Vec3[]) brush.Vertices.Clone();
		foreach ( var kv in targets ) verts[kv.Key] = kv.Value;
		return Finish( new MapVertexBrush( verts, brush.Faces, brush.Materials ),
			targets.Keys.SelectMany( v => IncidentFaces( brush, v ) ).Distinct().OrderBy( x => x ).ToArray(), planarTolerance );
	}

	/// <summary>Move an edge (both endpoints) by a delta. The two vertices
	/// must form an edge of at least one face — otherwise ArgumentException.
	/// A drag that stays in the adjacent faces' planes keeps the brush valid;
	/// a drag out of plane flags the affected faces NonPlanar (reported, not fixed).</summary>
	public static MapVertexEditResult MoveEdge( MapVertexBrush brush, int a, int b, Vec3 delta, double planarTolerance = 1e-3 )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( a < 0 || a >= brush.VertexCount || b < 0 || b >= brush.VertexCount )
			throw new ArgumentException( $"edge ({a}, {b}) references a vertex outside 0..{brush.VertexCount - 1}" );
		if ( a == b ) throw new ArgumentException( "an edge needs two distinct vertices" );
		if ( !IsEdge( brush, a, b ) )
			throw new ArgumentException( $"vertices {a} and {b} do not share an edge" );
		var verts = (Vec3[]) brush.Vertices.Clone();
		verts[a] += delta;
		verts[b] += delta;
		var affected = IncidentFaces( brush, a ).Concat( IncidentFaces( brush, b ) ).Distinct().OrderBy( x => x ).ToArray();
		return Finish( new MapVertexBrush( verts, brush.Faces, brush.Materials ), affected, planarTolerance );
	}

	/// <summary>Move a SET of vertices together by one delta (TrenchBroom-style
	/// multi-vertex drag): one operation, one returned result for the caller to
	/// record as a single undoable command, validity reported per face afterwards.
	/// A repeated index moves once; an empty set is a no-op returning an equal
	/// brush. Never auto-triangulates: the face topology is unchanged, so a drag
	/// that twists the brush comes back twisted, with the bad faces flagged —
	/// call <see cref="TriangulateInvalid"/> explicitly to repair it.</summary>
	public static MapVertexEditResult MoveVertices( MapVertexBrush brush, IEnumerable<int> vertices, Vec3 delta, double planarTolerance = 1e-3 )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( vertices is null ) throw new ArgumentNullException( nameof( vertices ) );
		var set = new HashSet<int>();
		foreach ( var v in vertices )
		{
			if ( v < 0 || v >= brush.VertexCount )
				throw new ArgumentException( $"vertex {v} is outside 0..{brush.VertexCount - 1}", nameof( vertices ) );
			set.Add( v );
		}
		var verts = (Vec3[]) brush.Vertices.Clone();
		foreach ( var v in set ) verts[v] += delta;
		var affected = set.SelectMany( v => IncidentFaces( brush, v ) ).Distinct().OrderBy( x => x ).ToArray();
		return Finish( new MapVertexBrush( verts, brush.Faces, brush.Materials ), affected, planarTolerance );
	}

	/// <summary>Move a SET of edges together by one delta: the union of their
	/// endpoints moves, each endpoint once even when shared by several edges.
	/// Every pair must be an edge of at least one face, else ArgumentException.
	/// One operation, one result, validity per face — and, like every move here,
	/// never an implicit triangulation.</summary>
	public static MapVertexEditResult MoveEdges( MapVertexBrush brush, IEnumerable<(int A, int B)> edges, Vec3 delta, double planarTolerance = 1e-3 )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( edges is null ) throw new ArgumentNullException( nameof( edges ) );
		var moved = new HashSet<int>();
		foreach ( var (a, b) in edges )
		{
			if ( a < 0 || a >= brush.VertexCount || b < 0 || b >= brush.VertexCount )
				throw new ArgumentException( $"edge ({a}, {b}) references a vertex outside 0..{brush.VertexCount - 1}" );
			if ( a == b ) throw new ArgumentException( "an edge needs two distinct vertices" );
			if ( !IsEdge( brush, a, b ) )
				throw new ArgumentException( $"vertices {a} and {b} do not share an edge" );
			moved.Add( a );
			moved.Add( b );
		}
		var verts = (Vec3[]) brush.Vertices.Clone();
		foreach ( var v in moved ) verts[v] += delta;
		var affected = moved.SelectMany( v => IncidentFaces( brush, v ) ).Distinct().OrderBy( x => x ).ToArray();
		return Finish( new MapVertexBrush( verts, brush.Faces, brush.Materials ), affected, planarTolerance );
	}

	/// <summary>Move a SET of faces together by one delta: the union of their
	/// vertices moves, each vertex once even when shared by several faces.
	/// One operation, one result, validity per face — never an implicit
	/// triangulation.</summary>
	public static MapVertexEditResult MoveFaces( MapVertexBrush brush, IEnumerable<int> faces, Vec3 delta, double planarTolerance = 1e-3 )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( faces is null ) throw new ArgumentNullException( nameof( faces ) );
		var moved = new HashSet<int>();
		var affected = new HashSet<int>();
		foreach ( var f in faces )
		{
			if ( f < 0 || f >= brush.FaceCount )
				throw new ArgumentException( $"face {f} is outside 0..{brush.FaceCount - 1}", nameof( faces ) );
			affected.Add( f );
			foreach ( var v in brush.Faces[f] ) moved.Add( v );
		}
		var verts = (Vec3[]) brush.Vertices.Clone();
		foreach ( var v in moved ) verts[v] += delta;
		// A moved vertex also touches faces outside the set (neighbours share
		// the vertex), so the affected set is the incident faces of every moved
		// vertex, not just the faces named by the caller.
		var allAffected = moved.SelectMany( v => IncidentFaces( brush, v ) ).Distinct().OrderBy( x => x ).ToArray();
		return Finish( new MapVertexBrush( verts, brush.Faces, brush.Materials ), allAffected, planarTolerance );
	}

	/// <summary>JACK-style auto-triangulation, EXPLICIT ONLY: split an invalid
	/// (non-planar / twisted) brush into valid pieces. Never called implicitly —
	/// no plain vertex move in this class triangulates, and a valid brush comes
	/// back untouched (Mode AlreadyValid, WasTriangulated false).
	/// <para>
	/// Stage 1 — fan triangulation: each invalid face with more than 3 vertices
	/// is split into triangles fanned from its first vertex. That is the same
	/// diagonal choice <see cref="MapVertexBrush.SignedVolume"/> uses, so the
	/// enclosed volume is preserved exactly, and every child triangle inherits
	/// its parent face's material. A twisted box is fixed here, as one piece.
	/// </para><para>
	/// Stage 2 — convex decomposition: only when the fan result is STILL invalid
	/// (degenerate / inverted faces survive triangulation — e.g. a face collapsed
	/// to a point). The brush's vertices are split into convex hulls via
	/// <see cref="ConvexDecomp"/> (2 hulls max: the fallback repairs, it does not
	/// shatter), each hull becoming one triangular-faced piece whose face
	/// materials come from the best-matching original face by normal. Hulls of a
	/// vertex subset do not in general enclose the original volume, so
	/// <see cref="MapTriangulationResult.VolumePreserved"/> reports the actual
	/// outcome instead of claiming it.
	/// </para><para>
	/// When neither stage yields a valid piece set, Mode is Failed, AllValid is
	/// false, Reason names why, and Pieces holds the best-effort fan result —
	/// the failure is reported, never hidden behind a silent partial fix.
	/// </para></summary>
	public static MapTriangulationResult TriangulateInvalid( MapVertexBrush brush, double planarTolerance = 1e-3, double volumeTolerance = 1e-6 )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		var originalVolume = brush.Volume();
		var validity = brush.Validate( planarTolerance );
		if ( validity.IsValid )
		{
			return new MapTriangulationResult
			{
				Pieces = new List<MapVertexBrush> { brush },
				PieceValidity = new List<MapVertexValidity> { validity },
				WasTriangulated = false,
				Mode = MapTriangulationMode.AlreadyValid,
				OriginalVolume = originalVolume,
				TotalVolume = originalVolume,
				VolumePreserved = true,
				AllValid = true,
			};
		}

		var fan = FanTriangulateInvalidFaces( brush, validity );
		var fanValidity = fan.Validate( planarTolerance );
		var fanVolume = fan.Volume();
		bool VolumeKept( double total ) => Math.Abs( total - originalVolume ) <= volumeTolerance * Math.Max( 1.0, Math.Abs( originalVolume ) );
		if ( fanValidity.IsValid )
		{
			return new MapTriangulationResult
			{
				Pieces = new List<MapVertexBrush> { fan },
				PieceValidity = new List<MapVertexValidity> { fanValidity },
				WasTriangulated = true,
				Mode = MapTriangulationMode.FanTriangulation,
				OriginalVolume = originalVolume,
				TotalVolume = fanVolume,
				VolumePreserved = VolumeKept( fanVolume ),
				AllValid = true,
			};
		}

		// Stage 2: the fan result is still invalid — split the vertex cloud into
		// convex hulls. Target 2 hulls (Min 1, Max 4): the fallback repairs, it
		// does not shatter. SurfaceProp is ConvexDecomp's required field and is
		// meaningless for a brush; the hulls' materials come from the original
		// faces (MaterialForHullFace), not from this property.
		var decomp = ConvexDecomp.Decompose( brush.Vertices, null,
			new ConvexDecomp.Options { TargetHulls = 2, MinHulls = 1, MaxHulls = 4 } );
		if ( decomp.Hulls.Count == 0 )
		{
			return FailedResult( brush, fan, fanValidity, originalVolume, VolumeKept( fanVolume ),
				$"fan-triangulated brush still invalid ({string.Join( "; ", fanValidity.Defects )}); " +
				$"convex decomposition produced no hulls: {decomp.Reason}" );
		}

		var pieces = new List<MapVertexBrush>();
		var pieceValidity = new List<MapVertexValidity>();
		foreach ( var hull in decomp.Hulls )
		{
			if ( hull.Points is null || hull.Points.Count < 4 || hull.Faces is null || hull.Faces.Count == 0 ) continue;
			var verts = hull.Points.ToArray();
			var faces = hull.Faces.Select( f => new[] { f.A, f.B, f.C } ).ToArray();
			var mats = hull.Faces.Select( f => MaterialForHullFace( brush, verts, f ) ).ToArray();
			var piece = new MapVertexBrush( verts, faces, mats );
			if ( piece.Volume() <= 1e-12 ) continue; // flat hull: no volume, not a piece
			pieces.Add( piece );
			pieceValidity.Add( piece.Validate( planarTolerance ) );
		}
		if ( pieces.Count == 0 )
		{
			return FailedResult( brush, fan, fanValidity, originalVolume, VolumeKept( fanVolume ),
				$"fan-triangulated brush still invalid ({string.Join( "; ", fanValidity.Defects )}); " +
				$"convex decomposition produced {decomp.Hulls.Count} hull(s) but none encloses a volume" );
		}

		var total = pieces.Sum( p => p.Volume() );
		var allValid = pieceValidity.All( v => v.IsValid );
		return new MapTriangulationResult
		{
			Pieces = pieces,
			PieceValidity = pieceValidity,
			WasTriangulated = true,
			Mode = allValid ? MapTriangulationMode.ConvexDecomposition : MapTriangulationMode.Failed,
			OriginalVolume = originalVolume,
			TotalVolume = total,
			VolumePreserved = VolumeKept( total ),
			AllValid = allValid,
			Reason = allValid ? "" : "convex-decomposition piece(s) still invalid: " +
				string.Join( "; ", pieceValidity.SelectMany( v => v.Defects ) ),
		};
	}

	/// <summary>Fan-triangulate the invalid faces only (valid faces keep their
	/// loops): each invalid face with &gt; 3 vertices becomes triangles fanned
	/// from its first vertex, each child inheriting the parent face's material.
	/// Faces with 3 or fewer vertices cannot be triangulated further and are
	/// kept as-is (a degenerate face stays degenerate — stage 2 handles it).</summary>
	private static MapVertexBrush FanTriangulateInvalidFaces( MapVertexBrush brush, MapVertexValidity validity )
	{
		var faces = new List<int[]>();
		var materials = new List<string>();
		for ( var fi = 0; fi < brush.Faces.Length; fi++ )
		{
			var face = brush.Faces[fi];
			var bad = fi < validity.Faces.Count && !validity.Faces[fi].IsValid;
			if ( !bad || face.Length <= 3 )
			{
				faces.Add( (int[]) face.Clone() );
				materials.Add( brush.Materials[fi] );
				continue;
			}
			// Fan from vertex 0 — the same diagonal choice SignedVolume() uses,
			// so the enclosed volume is preserved exactly by construction.
			for ( var i = 1; i + 1 < face.Length; i++ )
			{
				faces.Add( new[] { face[0], face[i], face[i + 1] } );
				materials.Add( brush.Materials[fi] );
			}
		}
		return new MapVertexBrush( brush.Vertices, faces.ToArray(), materials.ToArray() );
	}

	/// <summary>Material of the original face whose outward normal best matches
	/// a convex-hull triangle's normal. Falls back to "default" when the brush
	/// has no faces or the triangle is degenerate.</summary>
	private static string MaterialForHullFace( MapVertexBrush brush, Vec3[] hullVerts, ConvexHull.HullFace face )
	{
		if ( brush.Faces.Length == 0 ) return "default";
		var n = Vec3.Cross( hullVerts[face.B] - hullVerts[face.A], hullVerts[face.C] - hullVerts[face.A] );
		if ( n.LengthSquared <= 1e-12f ) return "default";
		n = n.Normal;
		var best = -2f;
		var bestMat = "default";
		for ( var i = 0; i < brush.Faces.Length; i++ )
		{
			var bn = brush.FaceNormal( i );
			if ( bn.LengthSquared <= 1e-12f ) continue;
			var d = Vec3.Dot( n, bn );
			if ( d > best ) { best = d; bestMat = brush.Materials[i]; }
		}
		return bestMat;
	}

	private static MapTriangulationResult FailedResult( MapVertexBrush brush, MapVertexBrush fan, MapVertexValidity fanValidity,
		double originalVolume, bool volumeKept, string reason ) =>
		new MapTriangulationResult
		{
			Pieces = new List<MapVertexBrush> { fan },
			PieceValidity = new List<MapVertexValidity> { fanValidity },
			WasTriangulated = fan.FaceCount != brush.FaceCount,
			Mode = MapTriangulationMode.Failed,
			OriginalVolume = originalVolume,
			TotalVolume = fan.Volume(),
			VolumePreserved = volumeKept,
			AllValid = false,
			Reason = reason,
		};

	/// <summary>Split a face (Ctrl+F) along the diagonal between two of its
	/// vertices. Both vertices must belong to the face and must NOT be
	/// adjacent in the loop (an adjacent pair is an existing edge, not a
	/// split). Produces two faces; both inherit the original face's material.
	/// A split of a planar face yields two planar faces.</summary>
	public static MapVertexEditResult SplitFace( MapVertexBrush brush, int face, int vertexA, int vertexB, double planarTolerance = 1e-3 )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( face < 0 || face >= brush.FaceCount )
			throw new ArgumentException( $"face {face} is outside 0..{brush.FaceCount - 1}", nameof( face ) );
		var loop = brush.Faces[face];
		var ia = Array.IndexOf( loop, vertexA );
		var ib = Array.IndexOf( loop, vertexB );
		if ( ia < 0 || ib < 0 )
			throw new ArgumentException( $"vertices {vertexA} and {vertexB} must both belong to face {face}" );
		if ( ia == ib ) throw new ArgumentException( "a face split needs two distinct vertices" );
		var n = loop.Length;
		if ( ( ia + 1 ) % n == ib || ( ib + 1 ) % n == ia )
			throw new ArgumentException( $"vertices {vertexA} and {vertexB} are adjacent on face {face} — that is an edge, not a split" );

		var first = new List<int>();
		for ( var i = ia; ; i = ( i + 1 ) % n )
		{
			first.Add( loop[i] );
			if ( i == ib ) break;
		}
		var second = new List<int>();
		for ( var i = ib; ; i = ( i + 1 ) % n )
		{
			second.Add( loop[i] );
			if ( i == ia ) break;
		}
		var faces = brush.Faces.Select( f => (int[]) f.Clone() ).ToList();
		var materials = brush.Materials.ToList();
		faces[face] = first.ToArray();
		faces.Add( second.ToArray() );
		materials.Add( brush.Materials[face] );
		return Finish( new MapVertexBrush( brush.Vertices, faces.ToArray(), materials.ToArray() ),
			new[] { face, faces.Count - 1 }, planarTolerance );
	}

	/// <summary>Split a face along the segment joining the midpoints of two
	/// of its edges (edge index = position of the edge's first vertex in the
	/// face loop). Two new vertices are appended; produces two faces, both
	/// inheriting the original material.</summary>
	public static MapVertexEditResult SplitFaceOnEdges( MapVertexBrush brush, int face, int edgeA, int edgeB, double planarTolerance = 1e-3 )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( face < 0 || face >= brush.FaceCount )
			throw new ArgumentException( $"face {face} is outside 0..{brush.FaceCount - 1}", nameof( face ) );
		var loop = brush.Faces[face];
		var n = loop.Length;
		if ( n < 3 ) throw new ArgumentException( $"face {face} has too few vertices to split" );
		if ( edgeA < 0 || edgeA >= n || edgeB < 0 || edgeB >= n )
			throw new ArgumentException( $"edge indices must be in 0..{n - 1} for face {face}" );
		if ( edgeA == edgeB ) throw new ArgumentException( "a face split needs two distinct edges" );

		var verts = brush.Vertices.ToList();
		var midA = ( brush.Vertices[loop[edgeA]] + brush.Vertices[loop[( edgeA + 1 ) % n]] ) / 2f;
		var midB = ( brush.Vertices[loop[edgeB]] + brush.Vertices[loop[( edgeB + 1 ) % n]] ) / 2f;
		var newA = verts.Count; verts.Add( midA );
		var newB = verts.Count; verts.Add( midB );

		var faces = brush.Faces.Select( f => f.ToList() ).ToList();
		var materials = brush.Materials.ToList();
		// The split edges are SHARED with neighbouring faces: a midpoint
		// inserted only into this face would crack the mesh along those
		// edges (the neighbour still uses the unsplit edge). Splice the new
		// vertex into every face that uses the edge, in loop order — the
		// split face itself included.
		SpliceEdgeMidpoint( faces, loop[edgeA], loop[( edgeA + 1 ) % n], newA );
		SpliceEdgeMidpoint( faces, loop[edgeB], loop[( edgeB + 1 ) % n], newB );
		loop = faces[face].ToArray();
		n = loop.Length;

		// The split face's loop already carries both midpoints (spliced in
		// above); walk it from one new vertex to the other for each half.
		var spliced = loop.ToList();
		var ia = spliced.IndexOf( newA );
		var ib = spliced.IndexOf( newB );
		var m = spliced.Count;
		var first = new List<int>();
		for ( var i = ia; ; i = ( i + 1 ) % m )
		{
			first.Add( spliced[i] );
			if ( i == ib ) break;
		}
		var second = new List<int>();
		for ( var i = ib; ; i = ( i + 1 ) % m )
		{
			second.Add( spliced[i] );
			if ( i == ia ) break;
		}
		faces[face] = first;
		faces.Add( second );
		materials.Add( brush.Materials[face] );
		return Finish( new MapVertexBrush( verts.ToArray(), faces.Select( f => f.ToArray() ).ToArray(), materials.ToArray() ),
			new[] { face, faces.Count - 1 }, planarTolerance );
	}

	/// <summary>Insert <paramref name="mid"/> into every face that uses the
	/// edge (a, b), right after whichever endpoint comes first in that
	/// face's loop.</summary>
	private static void SpliceEdgeMidpoint( List<List<int>> faces, int a, int b, int mid )
	{
		for ( var fi = 0; fi < faces.Count; fi++ )
		{
			var f = faces[fi];
			for ( var i = 0; i < f.Count; i++ )
			{
				if ( f[i] == a && f[( i + 1 ) % f.Count] == b ) { f.Insert( i + 1, mid ); break; }
				if ( f[i] == b && f[( i + 1 ) % f.Count] == a ) { f.Insert( i + 1, mid ); break; }
			}
		}
	}

	private static MapVertexEditResult Finish( MapVertexBrush brush, int[] affected, double planarTolerance ) =>
		new MapVertexEditResult { Brush = brush, Validity = brush.Validate( planarTolerance ), AffectedFaces = affected };

	private static int[] IncidentFaces( MapVertexBrush brush, int vertex )
	{
		var hit = new List<int>();
		for ( var i = 0; i < brush.Faces.Length; i++ )
			if ( Array.IndexOf( brush.Faces[i], vertex ) >= 0 ) hit.Add( i );
		return hit.ToArray();
	}

	private static bool IsEdge( MapVertexBrush brush, int a, int b )
	{
		foreach ( var face in brush.Faces )
		{
			for ( var i = 0; i < face.Length; i++ )
			{
				if ( face[i] == a && face[( i + 1 ) % face.Length] == b ) return true;
				if ( face[i] == b && face[( i + 1 ) % face.Length] == a ) return true;
			}
		}
		return false;
	}
}
