using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy.Map;

namespace Effigy;

// MeshMode — mesh-based geometry mode for Hammer Desktop P3 (Geppetto Desktop).
// Board: hammer-mesh-geometry-mode.
//
// A Source brush is convex by definition (MapBrush: planes + half-spaces). Mesh mode
// is the other side of that coin: select mesh faces / edges / verts on a map mesh and
// extrude, bevel, inset or bridge directly, with NO convexity constraint — concave
// meshes are allowed in Geppetto-native maps. Export back to Source 1 converts the
// mesh to convex brushes (convex decomposition) with a conversion report naming any
// geometry that could not be represented exactly.
//
// BACKEND ONLY, NO UI: no panels, dialogs, viewport or Face Edit UI. Where a feature
// normally needs a UI, this file builds the backend and marks the UI deferred (see
// MeshModeFeature: the parametric feature a UI would drive later).
//
// REAL TYPES ONLY. This file builds against the landed API, never against a stub:
//   - PolyMesh / Face / EdgeKey (kernel/Effigy/PolyMesh.cs): n-gon mesh, per-corner UVs.
//   - MapBrush / MapBrushFace (kernel/Effigy/Map/MapBrush.cs): plane-based convex brush.
//   - ConvexDecomp / ConvexHull (kernel/Effigy/ConvexDecomp.cs, ConvexHull.cs).
//   - VmfIo / VmfDocument / VmfBrush / VmfFace (kernel/Effigy/Map/VmfIo.cs,
//     namespace Effigy.Map — hence the using above; everything else here is
//     namespace Effigy).
//   - DirectEditFeature.Apply (kernel/Effigy/Features/DirectEditFeature.cs): the
//     "refuse out loud with a reason, never silently fix" pattern this file follows.
// There is NO MapFace type in the kernel (MapClip.cs / MapBrushPrimitives.cs were
// built against an assumed MapFace surface that does not exist); nothing here uses it.
//
// CONVEXHULL LANDMINE (measured 2026-11-13, not assumed): ConvexHull.Build on a
// point set that contains points lying ON a hull face but inside it — the L-shape
// fixture's reflex vertices (2, 2, 0) / (2, 2, 2) — returns a DENTED hull: 10
// points, and a brush built from it encloses 28 instead of the true 32. So the
// k-means fallback path (which feeds the whole point set to ConvexDecomp) can
// under-fill a concave mesh by that much, and the reflex-split path is the one
// that gets the L exactly — its leaves are clean 8-corner boxes with no interior
// points, which ConvexHull handles correctly. The MaxBrushes = 1 test asserts the
// honest 28 (excess 1/6), not the true hull 32. Fixing ConvexHull itself belongs
// to its owner lane, not this ticket; this file works around it and says so.
//
// UNITS. Kernel inches, Z-up, like the rest of Effigy.
//
// PURITY. Every op takes a PolyMesh and returns a NEW PolyMesh; the input is never
// mutated. A refusal throws InvalidOperationException naming the reason (the
// DirectEditFeature.Apply pattern), it never returns a silently different mesh.
//
// NOT RUNTIME VERIFIED. Per the PO workflow override (2026-11-13) this ticket is
// implemented + tested without a full project build or app launch; the final
// integration gate owns the full build / named-group run / playtest acceptance.

/// <summary>Which mesh op to apply (see <see cref="MeshMode.Apply"/>).</summary>
public enum MeshModeOp
{
	Extrude,
	Bevel,
	Inset,
	Bridge,
}

/// <summary>
/// What is selected on a map mesh: face / edge / vertex index sets. Indices are
/// PolyMesh indices (face index into <c>Faces</c>, vertex index into
/// <c>Positions</c>). A selection is data, not a live pick: it names the elements
/// an op acts on, and an op refuses an index that does not exist.
/// </summary>
public sealed class MeshSelection
{
	public HashSet<int> Faces = new();
	public HashSet<EdgeKey> Edges = new();
	public HashSet<int> Vertices = new();

	public bool IsEmpty => Faces.Count == 0 && Edges.Count == 0 && Vertices.Count == 0;

	public static MeshSelection Face( int face ) => new() { Faces = { face } };

	public static MeshSelection FacesOf( IEnumerable<int> faces )
	{
		var selection = new MeshSelection();
		foreach ( var face in faces ) selection.Faces.Add( face );
		return selection;
	}

	public static MeshSelection Edge( int a, int b ) => new() { Edges = { new EdgeKey( a, b ) } };

	public static MeshSelection EdgesOf( IEnumerable<(int A, int B)> edges )
	{
		var selection = new MeshSelection();
		foreach ( var (a, b) in edges ) selection.Edges.Add( new EdgeKey( a, b ) );
		return selection;
	}

	public static MeshSelection Vertex( int vertex ) => new() { Vertices = { vertex } };
}

/// <summary>
/// One named thing wrong with a <see cref="PolyMesh"/> under mesh-mode rules.
/// <see cref="Code"/> is the machine-readable reason; <see cref="Message"/> says it
/// in words; <see cref="ElementIndex"/> is the face / edge number it is about, or
/// -1 when it is about the whole mesh.
/// </summary>
public readonly struct MeshModeDefect
{
	/// <summary>The mesh has no faces at all.</summary>
	public const string EmptyMesh = "EmptyMesh";

	/// <summary>An edge is used by only one face: the mesh is open there.</summary>
	public const string BoundaryEdge = "BoundaryEdge";

	/// <summary>An edge is used by more than two faces: not a manifold solid.</summary>
	public const string NonManifoldEdge = "NonManifoldEdge";

	/// <summary>The two faces on an edge traverse it in the same direction: one of them is wound backwards.</summary>
	public const string InconsistentWinding = "InconsistentWinding";

	/// <summary>A face has fewer than 3 corners, a repeated corner, or (near-)zero area.</summary>
	public const string DegenerateFace = "DegenerateFace";

	/// <summary>A face's corners leave its plane: a mesh face must be planar.</summary>
	public const string NonPlanarFace = "NonPlanarFace";

	/// <summary>The mesh encloses (near-)zero volume.</summary>
	public const string ZeroVolume = "ZeroVolume";

	/// <summary>A face or edge index does not name an element of this mesh.</summary>
	public const string InvalidIndex = "InvalidIndex";

	public readonly string Code;
	public readonly string Message;
	public readonly int ElementIndex;

	public MeshModeDefect( string code, string message, int elementIndex = -1 )
	{
		Code = code;
		Message = message;
		ElementIndex = elementIndex;
	}

	public override string ToString() => ElementIndex >= 0
		? $"{Code} (element {ElementIndex}): {Message}"
		: $"{Code}: {Message}";
}

/// <summary>Options for <see cref="MeshMode.ExportToBrushes"/>.</summary>
public sealed class MeshExportOptions
{
	/// <summary>
	/// Relative volume tolerance for <see cref="MeshConversionReport.Exact"/>:
	/// the brushes are exact when |brush volume - mesh volume| is at most this
	/// fraction of the mesh volume. Default 1e-3 (0.1%).
	/// </summary>
	public float Tolerance = 1e-3f;

	/// <summary>Most convex brushes the export may produce. Default 12, the ConvexDecomp band.</summary>
	public int MaxBrushes = 12;

	/// <summary>
	/// Material every exported brush face gets when the mesh carries no material
	/// name for it (a hull point cloud carries no textures). Default is a dev
	/// measure material, so an exported brush is visible and measurable in Hammer.
	/// </summary>
	public string DefaultMaterial = "dev/dev_measuregeneric01";

	/// <summary>
	/// Optional material name for a PolyMesh material slot. Called with the most
	/// common slot in the mesh; null means <see cref="DefaultMaterial"/> for every
	/// brush. Per-face slot names are NOT carried: a convex hull regroups the mesh
	/// points, so a hull face does not correspond to one mesh face.
	/// </summary>
	public Func<int, string> MaterialForSlot;
}

/// <summary>
/// What the Source-1 export did. <see cref="Exact"/> is true only when the brushes'
/// union volume matches the mesh volume within
/// <see cref="MeshExportOptions.Tolerance"/> AND every mesh vertex lies inside at
/// least one brush hull AND every brush is a valid <see cref="MapBrush"/>. Anything
/// else is listed in <see cref="Unrepresentable"/> with numbers, never hidden.
/// </summary>
public sealed class MeshConversionReport
{
	/// <summary>Enclosed volume of the source mesh, cubic inches.</summary>
	public float MeshVolume;

	/// <summary>Sum of the exported brushes' volumes, cubic inches.</summary>
	public float BrushVolumeTotal;

	/// <summary>BrushVolumeTotal - MeshVolume. Positive means the brushes overfill the mesh (hulls fill a notch).</summary>
	public float ExcessVolume => BrushVolumeTotal - MeshVolume;

	/// <summary>ExcessVolume / MeshVolume (0 when the mesh has no volume).</summary>
	public float ExcessRatio => MeshVolume > 1e-6f ? ExcessVolume / MeshVolume : 0f;

	/// <summary>True when the conversion is exact within tolerance (see the class doc).</summary>
	public bool Exact;

	/// <summary>How many brushes were produced.</summary>
	public int BrushCount;

	/// <summary>How the conversion went, one line per fact (which path won, per-path totals).</summary>
	public List<string> Notes = new();

	/// <summary>Geometry that could not be represented exactly, each with its numbers. Empty when <see cref="Exact"/>.</summary>
	public List<string> Unrepresentable = new();

	public override string ToString() =>
		Exact
			? $"exact: {BrushCount} brush(es), mesh volume {MeshVolume:0.###}, brush volume {BrushVolumeTotal:0.###}"
			: $"inexact: {BrushCount} brush(es), mesh volume {MeshVolume:0.###}, brush volume {BrushVolumeTotal:0.###}, excess {ExcessRatio:P2} — {string.Join( "; ", Unrepresentable )}";
}

/// <summary>The result of <see cref="MeshMode.ExportToBrushes"/>: the brushes plus their report.</summary>
public sealed class MeshExportResult
{
	public List<MapBrush> Brushes = new();
	public MeshConversionReport Report = new();
}

/// <summary>
/// Mesh-based geometry mode: select faces / edges / verts on a map mesh and edit
/// them directly (extrude, bevel, inset, bridge), then export back to convex
/// brushes for Source 1. Static, pure, backend-only: no UI, no viewport, no
/// shelling out to Hammer.
/// </summary>
public static class MeshMode
{
	/// <summary>Planarity tolerance, kernel inches (same order as MapBrush.PlaneEps).</summary>
	public const float PlaneEps = 1e-3f;

	/// <summary>Volume tolerance, cubic inches: at or below this the mesh is zero-volume.</summary>
	public const float VolumeEps = 1e-6f;

	// ---- geometry helpers -------------------------------------------------

	/// <summary>Outward (Newell) normal of one face: the cross-sum over its corners, normalised. Zero when the face is degenerate.</summary>
	public static Vec3 FaceNormal( PolyMesh mesh, int faceIndex )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( faceIndex < 0 || faceIndex >= mesh.Faces.Count ) throw new ArgumentOutOfRangeException( nameof( faceIndex ) );
		var face = mesh.Faces[faceIndex];
		var sum = Vec3.Zero;
		for ( var i = 0; i < face.Count; i++ )
		{
			var a = mesh.Positions[face.Indices[i]];
			var b = mesh.Positions[face.Indices[( i + 1 ) % face.Count]];
			sum += Vec3.Cross( a, b );
		}
		return sum.LengthSquared < 1e-12f ? Vec3.Zero : sum.Normal;
	}

	/// <summary>Area of one face, square inches (half the Newell cross-sum length).</summary>
	public static float FaceArea( PolyMesh mesh, int faceIndex )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( faceIndex < 0 || faceIndex >= mesh.Faces.Count ) throw new ArgumentOutOfRangeException( nameof( faceIndex ) );
		var face = mesh.Faces[faceIndex];
		var sum = Vec3.Zero;
		for ( var i = 0; i < face.Count; i++ )
		{
			var a = mesh.Positions[face.Indices[i]];
			var b = mesh.Positions[face.Indices[( i + 1 ) % face.Count]];
			sum += Vec3.Cross( a, b );
		}
		return sum.Length / 2f;
	}

	/// <summary>Mean of one face's corners.</summary>
	public static Vec3 FaceCentroid( PolyMesh mesh, int faceIndex )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( faceIndex < 0 || faceIndex >= mesh.Faces.Count ) throw new ArgumentOutOfRangeException( nameof( faceIndex ) );
		var face = mesh.Faces[faceIndex];
		var sum = Vec3.Zero;
		foreach ( var index in face.Indices ) sum += mesh.Positions[index];
		return face.Count == 0 ? Vec3.Zero : sum / face.Count;
	}

	/// <summary>Enclosed volume, cubic inches (absolute value of PolyMesh.SignedVolume).</summary>
	public static float Volume( PolyMesh mesh )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		return MathF.Abs( mesh.SignedVolume() );
	}

	/// <summary>True when the mesh is a closed manifold: no boundary edges, no non-manifold edges, no winding defects.</summary>
	public static bool IsClosed( PolyMesh mesh )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		foreach ( var defect in Validate( mesh ) )
			if ( defect.Code is MeshModeDefect.BoundaryEdge or MeshModeDefect.NonManifoldEdge
				or MeshModeDefect.InconsistentWinding or MeshModeDefect.EmptyMesh )
				return false;
		return true;
	}

	/// <summary>
	/// All named defects, or empty when the mesh is a valid closed solid. Never throws
	/// for a geometrically bad mesh: the defects ARE the answer.
	/// </summary>
	public static IReadOnlyList<MeshModeDefect> Validate( PolyMesh mesh )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		var defects = new List<MeshModeDefect>();

		if ( mesh.Faces.Count == 0 )
		{
			defects.Add( new MeshModeDefect( MeshModeDefect.EmptyMesh, "The mesh has no faces." ) );
			return defects;
		}

		for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			var face = mesh.Faces[fi];
			if ( face.Indices is null || face.Count < 3 )
			{
				defects.Add( new MeshModeDefect( MeshModeDefect.DegenerateFace,
					$"Face {fi} has {face.Indices?.Length ?? 0} corners; a face needs at least 3.", fi ) );
				continue;
			}

			var badIndex = -1;
			var hasBadIndex = false;
			var repeated = false;
			for ( var i = 0; i < face.Count; i++ )
			{
				if ( face.Indices[i] < 0 || face.Indices[i] >= mesh.Positions.Count ) { badIndex = face.Indices[i]; hasBadIndex = true; }
				if ( face.Indices[i] == face.Indices[( i + 1 ) % face.Count] ) repeated = true;
			}
			if ( hasBadIndex )
			{
				defects.Add( new MeshModeDefect( MeshModeDefect.InvalidIndex,
					$"Face {fi} names vertex {badIndex}, but the mesh has {mesh.Positions.Count} vertices.", fi ) );
				continue;
			}
			if ( repeated )
			{
				defects.Add( new MeshModeDefect( MeshModeDefect.DegenerateFace,
					$"Face {fi} repeats a corner.", fi ) );
				continue;
			}

			var area = FaceArea( mesh, fi );
			if ( area <= 1e-9f )
			{
				defects.Add( new MeshModeDefect( MeshModeDefect.DegenerateFace,
					$"Face {fi} has (near-)zero area.", fi ) );
				continue;
			}

			var normal = FaceNormal( mesh, fi );
			var d = Vec3.Dot( normal, mesh.Positions[face.Indices[0]] );
			var maxDev = 0f;
			foreach ( var index in face.Indices )
				maxDev = MathF.Max( maxDev, MathF.Abs( Vec3.Dot( normal, mesh.Positions[index] ) - d ) );
			if ( maxDev > PlaneEps )
				defects.Add( new MeshModeDefect( MeshModeDefect.NonPlanarFace,
					$"Face {fi} leaves its plane by up to {maxDev:0.####}; a mesh face must be planar.", fi ) );
		}

		var edgeFaces = mesh.BuildEdgeFaces();
		foreach ( var (edge, faces) in edgeFaces )
		{
			if ( faces.Count == 1 )
			{
				defects.Add( new MeshModeDefect( MeshModeDefect.BoundaryEdge,
					$"Edge {edge} is used by only face {faces[0]}: the mesh is open there.", faces[0] ) );
			}
			else if ( faces.Count > 2 )
			{
				defects.Add( new MeshModeDefect( MeshModeDefect.NonManifoldEdge,
					$"Edge {edge} is used by {faces.Count} faces ({string.Join( ", ", faces )}): not a manifold solid.", faces[0] ) );
			}
			else
			{
				// Two faces must traverse the edge in opposite directions.
				var dir0 = EdgeDirection( mesh, faces[0], edge );
				var dir1 = EdgeDirection( mesh, faces[1], edge );
				if ( dir0 == dir1 )
					defects.Add( new MeshModeDefect( MeshModeDefect.InconsistentWinding,
						$"Edge {edge} is traversed the same way by faces {faces[0]} and {faces[1]}: one of them is wound backwards.", faces[1] ) );
			}
		}

		if ( Volume( mesh ) <= VolumeEps )
			defects.Add( new MeshModeDefect( MeshModeDefect.ZeroVolume,
				$"The mesh encloses volume {Volume( mesh ):0.######}: a solid must enclose space." ) );

		return defects;
	}

	/// <summary>True when the mesh is a valid closed solid (Validate() finds nothing).</summary>
	public static bool IsValid( PolyMesh mesh ) => Validate( mesh ).Count == 0;

	// +1 when face traverses edge A->B, -1 when B->A, 0 when the face has no such edge.
	static int EdgeDirection( PolyMesh mesh, int faceIndex, EdgeKey edge )
	{
		var face = mesh.Faces[faceIndex];
		for ( var i = 0; i < face.Count; i++ )
		{
			var a = face.Indices[i];
			var b = face.Indices[( i + 1 ) % face.Count];
			if ( a == edge.A && b == edge.B ) return 1;
			if ( a == edge.B && b == edge.A ) return -1;
		}
		return 0;
	}

	// ---- fixture format ---------------------------------------------------
	//
	// One mesh per file, the little line format tests/fixtures/hammer/
	// hammer-mesh-geometry-mode/README.md documents:
	//   # comment
	//   v x y z
	//   f i j k ... [materialSlot]
	// Indices are 0-based, faces are wound outward (counter-clockwise seen from
	// outside, so the Newell normal points out of the solid).

	/// <summary>Parse the fixture mesh format above. Throws FormatException naming the line on bad input.</summary>
	public static PolyMesh ParseMesh( string text )
	{
		if ( text is null ) throw new ArgumentNullException( nameof( text ) );
		var mesh = new PolyMesh();
		var lines = text.Split( new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries );
		for ( var lineNo = 0; lineNo < lines.Length; lineNo++ )
		{
			var line = lines[lineNo].Trim();
			if ( line.Length == 0 || line.StartsWith( "#", StringComparison.Ordinal ) ) continue;
			var parts = line.Split( (char[])null, StringSplitOptions.RemoveEmptyEntries );
			if ( parts[0] == "v" && parts.Length == 4 )
			{
				if ( !float.TryParse( parts[1], out var x ) || !float.TryParse( parts[2], out var y ) || !float.TryParse( parts[3], out var z ) )
					throw new FormatException( $"Mesh line {lineNo + 1}: bad vertex '{line}'." );
				mesh.AddVertex( new Vec3( x, y, z ) );
			}
			else if ( parts[0] == "f" && parts.Length >= 4 )
			{
				var indices = new List<int>();
				var material = 0;
				for ( var i = 1; i < parts.Length; i++ )
				{
					if ( !int.TryParse( parts[i], out var index ) ) break;
					indices.Add( index );
				}
				// A trailing non-index token after the corners is the material slot.
				if ( indices.Count < parts.Length - 1 && int.TryParse( parts[^1], out var slot ) )
					material = slot;
				if ( indices.Count < 3 )
					throw new FormatException( $"Mesh line {lineNo + 1}: face '{line}' needs at least 3 corners." );
				mesh.AddFace( indices.ToArray(), null, material );
			}
			else
			{
				throw new FormatException( $"Mesh line {lineNo + 1}: unrecognised line '{line}' (expected 'v x y z' or 'f i j k ...')." );
			}
		}
		return mesh;
	}

	/// <summary>Load a fixture mesh file (see <see cref="ParseMesh"/>).</summary>
	public static PolyMesh LoadMesh( string path ) => ParseMesh( File.ReadAllText( path ) );

	// ---- shared op plumbing ----------------------------------------------

	static List<int> RequireFaces( PolyMesh mesh, MeshSelection selection, string op )
	{
		if ( selection is null ) throw new ArgumentNullException( nameof( selection ) );
		if ( selection.Faces.Count == 0 )
			throw new InvalidOperationException( $"{op} needs at least one selected face." );
		var faces = selection.Faces.OrderBy( f => f ).ToList();
		foreach ( var face in faces )
			if ( face < 0 || face >= mesh.Faces.Count )
				throw new InvalidOperationException( $"{op}: face {face} does not exist (mesh has {mesh.Faces.Count} faces)." );
		return faces;
	}

	// Boundary edges of a selected face set: edges used by exactly one selected
	// face, with the direction that face traverses them. Interior edges (used by
	// two selected faces) get no side wall.
	static Dictionary<EdgeKey, (int From, int To)> SelectionBoundary( PolyMesh mesh, IReadOnlyList<int> faces )
	{
		var use = new Dictionary<EdgeKey, int>();
		var dir = new Dictionary<EdgeKey, (int From, int To)>();
		foreach ( var fi in faces )
		{
			var face = mesh.Faces[fi];
			for ( var i = 0; i < face.Count; i++ )
			{
				var a = face.Indices[i];
				var b = face.Indices[( i + 1 ) % face.Count];
				var key = new EdgeKey( a, b );
				use[key] = use.TryGetValue( key, out var count ) ? count + 1 : 1;
				if ( !dir.ContainsKey( key ) ) dir[key] = (a, b);
			}
		}
		return dir.Where( pair => use[pair.Key] == 1 )
			.ToDictionary( pair => pair.Key, pair => pair.Value );
	}

	static PolyMesh WithoutFaces( PolyMesh mesh, IReadOnlyCollection<int> remove )
	{
		var result = mesh.Clone();
		var kept = new List<Face>();
		for ( var i = 0; i < result.Faces.Count; i++ )
			if ( !remove.Contains( i ) ) kept.Add( result.Faces[i] );
		result.Faces.Clear();
		result.Faces.AddRange( kept );
		result.InvalidateVertexCache();
		return result;
	}

	static void ThrowIfInvalid( PolyMesh mesh, string op )
	{
		var defects = Validate( mesh );
		if ( defects.Count > 0 )
			throw new InvalidOperationException(
				$"{op} produced an invalid mesh: {string.Join( "; ", defects.Select( d => d.ToString() ) )}" );
	}

	// ---- extrude ----------------------------------------------------------
	//
	// OUTWARD (offset along the face normal, out of the solid): the selected
	// faces are removed and replaced by a cap at the offset plus a side wall on
	// every boundary edge of the selection (an edge shared by two selected faces
	// is interior and gets no wall). The original corners stay: neighbouring
	// faces still use them, so nothing tears. Wall quad for a boundary edge
	// a -&gt; b (in the face's loop order) is [a, b, b', a']: its normal is
	// (b - a) x (b' - b), which points out of the solid for an outward offset,
	// and it traverses the cap edge b' -&gt; a' opposite to the cap's a' -&gt; b'
	// and the neighbour edge b -&gt; a opposite to the neighbour's a -&gt; b.
	// (The [a, a', b', b] order tried first has the wall normal right but
	// traverses the cap edge the same way as the cap — an inconsistent winding
	// the validator rightly refuses.)
	//
	// INWARD (offset into the solid): duplicating the face and building walls
	// would leave a zero-thickness fin — the old side faces and the new walls
	// would cover the same strip with the same normal — so an inward extrude
	// DRAGS the face instead: the face's vertices move by the offset in place
	// (shared with the neighbouring faces, which stretch with it), no new
	// geometry is created, and the volume shrinks by the dragged amount. Faces
	// in one call must agree on inward vs outward, and a dragged vertex shared
	// by two selected faces must get the same offset from both, or the call is
	// refused with a named error.

	/// <summary>
	/// Extrude the selected faces by <paramref name="distance"/> inches along
	/// each face's own outward normal (or along <paramref name="direction"/> when
	/// given, which must be non-zero). A negative distance, or a direction
	/// pointing into the solid, drags the faces inward instead of building
	/// walls (see the note above). Returns a new mesh; the input is untouched.
	/// </summary>
	public static PolyMesh Extrude( PolyMesh mesh, MeshSelection selection, float distance, Vec3? direction = null )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( distance == 0f )
			throw new InvalidOperationException( "Extrude needs a non-zero distance: 0 moves nothing." );
		var faces = RequireFaces( mesh, selection, "Extrude" );

		Vec3? unitDirection = null;
		if ( direction.HasValue )
		{
			if ( direction.Value.LengthSquared < 1e-12f )
				throw new InvalidOperationException( "Extrude needs a non-zero direction." );
			unitDirection = direction.Value.Normal;
		}

		// Per-face offset direction and inward/outward sign. A face is inward
		// when its offset points into the solid (against its own normal).
		var dirs = new Vec3[faces.Count];
		var inward = new bool[faces.Count];
		for ( var i = 0; i < faces.Count; i++ )
		{
			var dir = unitDirection ?? FaceNormal( mesh, faces[i] );
			if ( dir.LengthSquared < 1e-12f )
				throw new InvalidOperationException( $"Extrude: face {faces[i]} is degenerate and has no normal to extrude along." );
			dirs[i] = dir;
			inward[i] = distance * Vec3.Dot( dir, FaceNormal( mesh, faces[i] ) ) < 0f;
		}
		if ( inward.Any( x => x ) && inward.Any( x => !x ) )
			throw new InvalidOperationException(
				"Extrude: the selection mixes faces extruding outward with faces extruding inward; extrude one direction at a time." );

		if ( inward[0] )
			return ExtrudeInward( mesh, faces, dirs, distance );

		var boundary = SelectionBoundary( mesh, faces );
		var result = WithoutFaces( mesh, faces );

		for ( var f = 0; f < faces.Count; f++ )
		{
			var face = mesh.Faces[faces[f]];
			var dir = dirs[f];

			// One new vertex per corner of this face, offset by distance along dir.
			var cap = new int[face.Count];
			for ( var i = 0; i < face.Count; i++ )
				cap[i] = result.AddVertex( mesh.Positions[face.Indices[i]] + dir * distance );

			result.AddFace( cap, (Vec2[])face.UVs.Clone(), face.Material );

			// Side walls on the boundary edges of this face. Quad [a, b, b', a']:
			// see the section note above for why this order and not [a, a', b', b].
			for ( var i = 0; i < face.Count; i++ )
			{
				var a = face.Indices[i];
				var b = face.Indices[( i + 1 ) % face.Count];
				if ( !boundary.TryGetValue( new EdgeKey( a, b ), out var edge ) ) continue;
				if ( edge.From != a ) continue; // one wall per boundary edge, built from its owner face
				var aPrime = cap[i];
				var bPrime = cap[( i + 1 ) % face.Count];
				result.AddFace( new[] { a, b, bPrime, aPrime },
					new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) },
					face.Material );
			}
		}

		result.InvalidateVertexCache();
		ThrowIfInvalid( result, "Extrude" );
		return result;
	}

	// Inward extrude: drag the selected faces' vertices by their offsets in
	// place. A vertex shared by two selected faces must agree on its offset.
	static PolyMesh ExtrudeInward( PolyMesh mesh, IReadOnlyList<int> faces, Vec3[] dirs, float distance )
	{
		var offsets = new Dictionary<int, Vec3>();
		for ( var f = 0; f < faces.Count; f++ )
		{
			var offset = dirs[f] * distance;
			foreach ( var v in mesh.Faces[faces[f]].Indices )
			{
				if ( offsets.TryGetValue( v, out var existing ) )
				{
					if ( ( existing - offset ).LengthSquared > 1e-12f )
						throw new InvalidOperationException(
							$"Extrude: vertex {v} is shared by two selected faces that would drag it in different directions; extrude them one at a time." );
				}
				else offsets[v] = offset;
			}
		}

		var result = mesh.Clone();
		foreach ( var (v, offset) in offsets )
			result.Positions[v] = mesh.Positions[v] + offset;
		result.InvalidateVertexCache();
		ThrowIfInvalid( result, "Extrude" );
		return result;
	}

	// ---- inset ------------------------------------------------------------
	//
	// The selected face is replaced by a smaller copy of itself in the same plane
	// (scaled about its centroid) plus a flat frame of side faces joining the
	// original loop to the inset loop. The volume does not change: the frame is
	// coplanar with the original face. Selected faces must not share an edge in
	// one call — two insets on one edge would build two different frames over the
	// same edge — so inset them one at a time.

	/// <summary>
	/// Inset the selected faces by <paramref name="amount"/> inches toward their
	/// centroids (uniform scale about the centroid: scale = 1 - amount / mean
	/// corner radius). Returns a new mesh; the input is untouched. The volume is
	/// unchanged.
	/// </summary>
	public static PolyMesh Inset( PolyMesh mesh, MeshSelection selection, float amount )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( amount <= 0f )
			throw new InvalidOperationException( "Inset needs a positive amount: 0 changes nothing." );
		var faces = RequireFaces( mesh, selection, "Inset" );

		var shared = new Dictionary<EdgeKey, int>();
		foreach ( var fi in faces )
		{
			var face = mesh.Faces[fi];
			for ( var i = 0; i < face.Count; i++ )
			{
				var key = new EdgeKey( face.Indices[i], face.Indices[( i + 1 ) % face.Count] );
				shared[key] = shared.TryGetValue( key, out var count ) ? count + 1 : 1;
			}
		}
		if ( shared.Values.Any( count => count > 1 ) )
			throw new InvalidOperationException(
				"Inset faces share an edge in this selection; inset them one at a time, or the two frames would overlap." );

		var result = WithoutFaces( mesh, faces );

		foreach ( var fi in faces )
		{
			var face = mesh.Faces[fi];
			var centroid = FaceCentroid( mesh, fi );
			var radius = 0f;
			foreach ( var index in face.Indices )
				radius += ( mesh.Positions[index] - centroid ).Length;
			radius /= face.Count;
			if ( amount >= radius )
				throw new InvalidOperationException(
					$"Inset amount {amount:0.###} reaches the centre of face {fi} (corner radius {radius:0.###}); use less than the radius." );

			var scale = 1f - amount / radius;
			var inset = new int[face.Count];
			for ( var i = 0; i < face.Count; i++ )
			{
				var p = mesh.Positions[face.Indices[i]];
				inset[i] = result.AddVertex( centroid + ( p - centroid ) * scale );
			}

			result.AddFace( inset, (Vec2[])face.UVs.Clone(), face.Material );

			// Frame: quad [a, b, b', a'] per edge. Both loops are in the face
			// plane, so (b - a) x (b' - a) points along the face normal: outward.
			for ( var i = 0; i < face.Count; i++ )
			{
				var a = face.Indices[i];
				var b = face.Indices[( i + 1 ) % face.Count];
				result.AddFace( new[] { a, b, inset[( i + 1 ) % face.Count], inset[i] },
					new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) },
					face.Material );
			}
		}

		result.InvalidateVertexCache();
		ThrowIfInvalid( result, "Inset" );
		return result;
	}

	// ---- bevel ------------------------------------------------------------
	//
	// Chamfer (segments = 1) or rounded (segments > 1) bevel of one manifold edge.
	// The edge's two faces are trimmed back by <width> along their other edges,
	// a chamfer face (or a fan of them) joins the two trim lines, and the faces
	// meeting the edge's endpoints gain the trim points as extra corners — an
	// n-gon can do that, which is why the end faces stay faces instead of
	// becoming a fan of triangles. For segments > 1 the profile is an arc of
	// radius <width> centred on the edge endpoint, so a 90-degree edge rounds
	// instead of chamfering. Width must be shorter than the shortest edge
	// adjacent to the bevelled edge, or the trim would run past a vertex.

	/// <summary>
	/// Bevel one manifold edge by <paramref name="width"/> inches with
	/// <paramref name="segments"/> profile steps (1 = chamfer, more = rounded).
	/// Returns a new mesh; the input is untouched.
	/// </summary>
	public static PolyMesh Bevel( PolyMesh mesh, EdgeKey edge, float width, int segments = 1 )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( width <= 0f )
			throw new InvalidOperationException( "Bevel needs a positive width: 0 cuts nothing." );
		if ( segments < 1 )
			throw new InvalidOperationException( "Bevel needs at least 1 segment." );
		if ( edge.A < 0 || edge.A >= mesh.Positions.Count || edge.B < 0 || edge.B >= mesh.Positions.Count )
			throw new InvalidOperationException(
				$"Bevel: edge {edge} does not name vertices of this mesh ({mesh.Positions.Count} vertices)." );

		var edgeFaces = mesh.BuildEdgeFaces();
		if ( !edgeFaces.TryGetValue( edge, out var faces ) || faces.Count != 2 )
			throw new InvalidOperationException(
				$"Bevel: edge {edge} is shared by {faces?.Count ?? 0} face(s); a bevel needs exactly 2 (a manifold edge)." );

		var f1 = faces[0];
		var f2 = faces[1];
		var n1 = FaceNormal( mesh, f1 );
		var n2 = FaceNormal( mesh, f2 );
		if ( n1.LengthSquared < 1e-12f || n2.LengthSquared < 1e-12f )
			throw new InvalidOperationException( $"Bevel: edge {edge} borders a degenerate face with no normal." );
		if ( Vec3.Dot( n1, n2 ) < -1f + 1e-6f )
			throw new InvalidOperationException( $"Bevel: edge {edge} borders two coplanar-opposite faces (a 180-degree fold has no bevel)." );

		// Profile: for each endpoint, the tangent arc where the bevel meets the
		// two faces. P1 / P2 are the points at distance `width` from the edge
		// along each face (d[0] / d[1] are the in-face directions away from the
		// edge, so P1 / P2 lie on the faces' edges out of the endpoint). For
		// segments = 1 the profile is just the chamfer P1 -&gt; P2. For more
		// segments it is the arc TANGENT to both faces at P1 / P2: its centre C
		// sits on the interior bisector at distance width / cos(psi / 2) from
		// the endpoint (psi = angle between d[0] and d[1]) and its radius is
		// width * tan(psi / 2), so the arc bulges TOWARD the corner and removes
		// less than the chamfer. (An arc centred on the endpoint itself, as
		// first written, bulges away from the corner and removes MORE than the
		// chamfer — the segments = 2 test caught that.)
		var endpoints = new[] { edge.A, edge.B };
		var profile = new List<Vec3>[2];
		for ( var e = 0; e < 2; e++ )
		{
			var v = endpoints[e];
			var other = endpoints[1 - e];
			var d = new Vec3[2];
			for ( var f = 0; f < 2; f++ )
			{
				var face = mesh.Faces[faces[f]];
				var neighbour = -1;
				var neighbourLen = 0f;
				for ( var i = 0; i < face.Count; i++ )
				{
					if ( face.Indices[i] != v ) continue;
					var prev = face.Indices[( i + face.Count - 1 ) % face.Count];
					var next = face.Indices[( i + 1 ) % face.Count];
					var candidate = prev == other ? next : prev;
					if ( candidate == other ) continue;
					neighbour = candidate;
					neighbourLen = ( mesh.Positions[candidate] - mesh.Positions[v] ).Length;
					break;
				}
				if ( neighbour < 0 )
					throw new InvalidOperationException(
						$"Bevel: face {faces[f]} has no edge away from vertex {v} on edge {edge}." );
				if ( width >= neighbourLen )
					throw new InvalidOperationException(
						$"Bevel width {width:0.###} reaches past the neighbouring vertex of {v} on face {faces[f]} (that edge is {neighbourLen:0.###} long); use a smaller width." );
				d[f] = ( mesh.Positions[neighbour] - mesh.Positions[v] ).Normal;
			}

			var origin = mesh.Positions[v];
			var p1 = origin + d[0] * width;
			var p2 = origin + d[1] * width;
			var cosPsi = Math.Clamp( Vec3.Dot( d[0], d[1] ), -1f, 1f );
			var psi = MathF.Acos( cosPsi );
			if ( psi < 0.02f )
				throw new InvalidOperationException(
					$"Bevel: edge {edge} is flat at vertex {v} (the two faces meet at {psi:0.###} rad); there is no corner to bevel." );

			var points = new List<Vec3>( segments + 1 );
			if ( segments == 1 )
			{
				points.Add( p1 );
				points.Add( p2 );
			}
			else
			{
				var radius = width * MathF.Tan( psi / 2f );
				var centre = origin + ( d[0] + d[1] ).Normal * ( width / MathF.Cos( psi / 2f ) );
				var u0 = ( p1 - centre ) / radius;
				var u1 = ( p2 - centre ) / radius;
				var axis = ( mesh.Positions[other] - origin ).Normal;
				// Rotate u0 toward u1 by psi around the edge axis; flip the axis
				// when the rotation lands on the wrong side.
				var rotated = Rodrigues( u0, axis, psi );
				if ( Vec3.Dot( rotated, u1 ) < 0f ) axis = -axis;
				var cross = Vec3.Cross( axis, u0 );
				for ( var k = 0; k <= segments; k++ )
				{
					var theta = psi * k / segments;
					var dir = u0 * MathF.Cos( theta ) + cross * MathF.Sin( theta );
					points.Add( centre + dir * radius );
				}
			}
			profile[e] = points;
		}

		var result = mesh.Clone();

		// Chamfer faces, oriented so their normal follows n1 + n2 (outward).
		var pa = new int[segments + 1];
		var pb = new int[segments + 1];
		for ( var k = 0; k <= segments; k++ )
		{
			pa[k] = result.AddVertex( profile[0][k] );
			pb[k] = result.AddVertex( profile[1][k] );
		}
		var outward = ( n1 + n2 ).Normal;
		for ( var k = 0; k < segments; k++ )
		{
			var quad = new[] { pa[k], pb[k], pb[k + 1], pa[k + 1] };
			if ( QuadNormal( result, quad ).LengthSquared > 1e-12f
				&& Vec3.Dot( QuadNormal( result, quad ), outward ) < 0f )
				quad = new[] { pa[k + 1], pb[k + 1], pb[k], pa[k] };
			result.AddFace( quad,
				new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) },
				mesh.Faces[f1].Material );
		}

		// Trim the two faces the edge belonged to.
		ReplaceVertexInFace( result, f1, edge.A, pa[0] );
		ReplaceVertexInFace( result, f1, edge.B, pb[0] );
		ReplaceVertexInFace( result, f2, edge.A, pa[segments] );
		ReplaceVertexInFace( result, f2, edge.B, pb[segments] );

		// Every other face touching an endpoint gains the profile as extra
		// corners, in loop order: the profile point on the edge to the loop's
		// next neighbour goes next to that neighbour.
		for ( var e = 0; e < 2; e++ )
		{
			var v = endpoints[e];
			var prof = e == 0 ? pa : pb;
			var faceIndices = mesh.BuildVertexFaces()[v];
			foreach ( var gi in faceIndices )
			{
				if ( gi == f1 || gi == f2 ) continue;
				var face = mesh.Faces[gi];
				var pos = Array.IndexOf( face.Indices, v );
				if ( pos < 0 ) continue;
				var next = face.Indices[( pos + 1 ) % face.Count];
				// The offset neighbour of v on f1: the profile point prof[0]
				// lies on the edge (v, that neighbour).
				var neighbourOnF1 = OtherNeighbour( mesh, f1, v, endpoints[1 - e] );
				var sequence = next == neighbourOnF1
					? prof.Reverse().ToArray()
					: prof.ToArray();
				ReplaceVertexInFaceWithSequence( result, gi, v, sequence );
			}
		}

		result.InvalidateVertexCache();
		ThrowIfInvalid( result, "Bevel" );
		return result;
	}

	/// <summary>
	/// Bevel every edge in <paramref name="selection"/> by <paramref name="width"/>
	/// with <paramref name="segments"/> profile steps. The edges must not share a
	/// vertex in one call (bevel them one at a time when they meet at a corner).
	/// </summary>
	public static PolyMesh Bevel( PolyMesh mesh, MeshSelection selection, float width, int segments = 1 )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( selection is null ) throw new ArgumentNullException( nameof( selection ) );
		if ( selection.Edges.Count == 0 )
			throw new InvalidOperationException( "Bevel needs at least one selected edge." );
		var seen = new HashSet<int>();
		foreach ( var edge in selection.Edges )
		{
			if ( !seen.Add( edge.A ) || !seen.Add( edge.B ) )
				throw new InvalidOperationException(
					$"Bevel: edges {edge} shares a vertex with another selected edge; bevel edges one at a time when they meet at a corner." );
		}
		var result = mesh;
		foreach ( var edge in selection.Edges.OrderBy( e => e.A ).ThenBy( e => e.B ) )
			result = Bevel( result, edge, width, segments );
		return result;
	}

	// Rotate vector v around unit axis a by angle (radians), right-hand rule.
	static Vec3 Rodrigues( Vec3 v, Vec3 a, float angle ) =>
		v * MathF.Cos( angle ) + Vec3.Cross( a, v ) * MathF.Sin( angle )
		+ a * Vec3.Dot( a, v ) * ( 1f - MathF.Cos( angle ) );

	static Vec3 QuadNormal( PolyMesh mesh, IReadOnlyList<int> quad )
	{
		var sum = Vec3.Zero;
		for ( var i = 0; i < quad.Count; i++ )
		{
			var a = mesh.Positions[quad[i]];
			var b = mesh.Positions[quad[( i + 1 ) % quad.Count]];
			sum += Vec3.Cross( a, b );
		}
		return sum.LengthSquared < 1e-12f ? Vec3.Zero : sum.Normal;
	}

	// The neighbour of v on face fi that is NOT `other` (the bevel edge partner).
	static int OtherNeighbour( PolyMesh mesh, int faceIndex, int v, int other )
	{
		var face = mesh.Faces[faceIndex];
		for ( var i = 0; i < face.Count; i++ )
		{
			if ( face.Indices[i] != v ) continue;
			var prev = face.Indices[( i + face.Count - 1 ) % face.Count];
			var next = face.Indices[( i + 1 ) % face.Count];
			return prev == other ? next : prev;
		}
		return -1;
	}

	static void ReplaceVertexInFace( PolyMesh mesh, int faceIndex, int oldVertex, int newVertex )
	{
		var face = mesh.Faces[faceIndex];
		for ( var i = 0; i < face.Count; i++ )
			if ( face.Indices[i] == oldVertex ) face.Indices[i] = newVertex;
	}

	static void ReplaceVertexInFaceWithSequence( PolyMesh mesh, int faceIndex, int oldVertex, int[] sequence )
	{
		var face = mesh.Faces[faceIndex];
		var pos = Array.IndexOf( face.Indices, oldVertex );
		if ( pos < 0 ) return;
		var indices = new List<int>();
		var uvs = new List<Vec2>();
		for ( var i = 0; i < face.Count; i++ )
		{
			if ( i == pos )
			{
				indices.AddRange( sequence );
				for ( var k = 0; k < sequence.Length; k++ ) uvs.Add( face.UVs[i] );
			}
			else
			{
				indices.Add( face.Indices[i] );
				uvs.Add( face.UVs[i] );
			}
		}
		face.Indices = indices.ToArray();
		face.UVs = uvs.ToArray();
	}

	// ---- bridge -----------------------------------------------------------
	//
	// Remove two faces and join their loops with side quads, one per loop edge.
	// The loops must have the same corner count and must not share a vertex.
	// Correspondence is the rotation (and optional reversal) of the second loop
	// that minimises total corner distance, so the bridge does not twist. Each
	// quad is wound outward by a radial check against the tube axis, so the
	// bridge works whether the two loops wind the same way or opposite ways
	// (e.g. the top of one box to the bottom of the box above it).

	/// <summary>
	/// Bridge faces <paramref name="faceA"/> and <paramref name="faceB"/>: remove
	/// both and join their loops with side quads. Returns a new mesh; the input
	/// is untouched. The loops must have the same corner count.
	/// </summary>
	public static PolyMesh Bridge( PolyMesh mesh, int faceA, int faceB )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( faceA < 0 || faceA >= mesh.Faces.Count || faceB < 0 || faceB >= mesh.Faces.Count )
			throw new InvalidOperationException(
				$"Bridge: faces {faceA} and {faceB} do not both exist (mesh has {mesh.Faces.Count} faces)." );
		if ( faceA == faceB )
			throw new InvalidOperationException( "Bridge needs two different faces." );

		var loopA = mesh.Faces[faceA].Indices;
		var loopB = mesh.Faces[faceB].Indices;
		if ( loopA.Length != loopB.Length )
			throw new InvalidOperationException(
				$"Bridge needs two loops with the same corner count (got {loopA.Length} and {loopB.Length}); resample one loop first." );
		if ( loopA.Intersect( loopB ).Any() )
			throw new InvalidOperationException( "Bridge faces must not share vertices; bridging a face to its neighbour has no tube." );

		var n = loopA.Length;
		var centreA = FaceCentroid( mesh, faceA );
		var centreB = FaceCentroid( mesh, faceB );
		var axisMid = ( centreA + centreB ) / 2f;

		// Best correspondence: rotation, with or without reversal, minimising
		// total corner distance.
		var bestMap = new int[n];
		var bestCost = float.MaxValue;
		for ( var reversed = 0; reversed < 2; reversed++ )
		{
			for ( var rot = 0; rot < n; rot++ )
			{
				var cost = 0f;
				for ( var i = 0; i < n; i++ )
				{
					var j = reversed == 0 ? ( rot + i ) % n : ( rot - i + n * 2 ) % n;
					cost += ( mesh.Positions[loopA[i]] - mesh.Positions[loopB[j]] ).Length;
				}
				if ( cost < bestCost )
				{
					bestCost = cost;
					for ( var i = 0; i < n; i++ )
						bestMap[i] = reversed == 0 ? ( rot + i ) % n : ( rot - i + n * 2 ) % n;
				}
			}
		}

		var result = WithoutFaces( mesh, new[] { faceA, faceB } );
		for ( var i = 0; i < n; i++ )
		{
			var a0 = loopA[i];
			var a1 = loopA[( i + 1 ) % n];
			var b0 = loopB[bestMap[i]];
			var b1 = loopB[bestMap[( i + 1 ) % n]];
			var quad = new[] { a0, a1, b1, b0 };
			// Outward = away from the tube axis at this quad's centroid.
			var centroid = ( mesh.Positions[a0] + mesh.Positions[a1] + mesh.Positions[b1] + mesh.Positions[b0] ) / 4f;
			var radial = centroid - axisMid;
			var normal = QuadNormal( result, quad );
			if ( normal.LengthSquared > 1e-12f && radial.LengthSquared > 1e-12f
				&& Vec3.Dot( normal, radial ) < 0f )
				quad = new[] { a0, b0, b1, a1 };
			result.AddFace( quad,
				new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) },
				mesh.Faces[faceA].Material );
		}

		result.InvalidateVertexCache();
		// A bridge between non-parallel faces builds quads that are not planar
		// (two loops in different planes cannot be joined by flat quads). That is
		// the shape of a bridge, not a defect in the edit, so NonPlanarFace is
		// allowed here; everything else still refuses.
		var defects = Validate( result )
			.Where( d => d.Code != MeshModeDefect.NonPlanarFace ).ToList();
		if ( defects.Count > 0 )
			throw new InvalidOperationException(
				$"Bridge produced an invalid mesh: {string.Join( "; ", defects.Select( d => d.ToString() ) )}" );
		return result;
	}

	// ---- dispatcher -------------------------------------------------------

	/// <summary>
	/// Apply one op, the DirectEditFeature.Apply shape: Extrude / Inset use
	/// <paramref name="distance"/> on <paramref name="selection"/>.Faces, Bevel
	/// uses <paramref name="width"/> / <paramref name="segments"/> on
	/// <paramref name="selection"/>.Edges, Bridge uses the two faces in
	/// <paramref name="selection"/>.Faces. Throws InvalidOperationException with
	/// a named reason on a refusal.
	/// </summary>
	public static PolyMesh Apply( MeshModeOp op, PolyMesh mesh, MeshSelection selection,
		float distance, float width = 0.5f, int segments = 1 )
	{
		switch ( op )
		{
			case MeshModeOp.Extrude: return Extrude( mesh, selection, distance );
			case MeshModeOp.Inset: return Inset( mesh, selection, distance );
			case MeshModeOp.Bevel: return Bevel( mesh, selection, width, segments );
			case MeshModeOp.Bridge:
			{
				if ( selection is null ) throw new ArgumentNullException( nameof( selection ) );
				var faces = selection.Faces.OrderBy( f => f ).ToList();
				if ( faces.Count != 2 )
					throw new InvalidOperationException(
						$"Bridge needs exactly 2 selected faces, got {faces.Count}." );
				return Bridge( mesh, faces[0], faces[1] );
			}
			default: throw new ArgumentOutOfRangeException( nameof( op ), op, "Unknown mesh mode op." );
		}
	}

	// ---- brush conversion -------------------------------------------------

	/// <summary>Bake one convex brush to its mesh (MapBrush.ToMesh: nodraw faces are dropped from the baked mesh, as in the brush core).</summary>
	public static PolyMesh FromBrush( MapBrush brush )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		return brush.ToMesh();
	}

	/// <summary>Merge several brushes into one mesh (one shell per brush, vertices offset per brush).</summary>
	public static PolyMesh FromBrushes( IEnumerable<MapBrush> brushes )
	{
		if ( brushes is null ) throw new ArgumentNullException( nameof( brushes ) );
		var mesh = new PolyMesh();
		foreach ( var brush in brushes )
		{
			if ( brush is null ) throw new ArgumentException( "A brush in the list is null.", nameof( brushes ) );
			var part = brush.ToMesh();
			var offset = mesh.Positions.Count;
			mesh.Positions.AddRange( part.Positions );
			foreach ( var face in part.Faces )
				mesh.AddFace( face.Indices.Select( i => i + offset ).ToArray(),
					(Vec2[])face.UVs.Clone(), face.Material );
		}
		mesh.InvalidateVertexCache();
		return mesh;
	}

	// ---- Source-1 export --------------------------------------------------
	//
	// Two paths, best exact-ish wins:
	//   1. Reflex-plane split (deterministic, exact for Hammer-style concave
	//      solids): a concave mesh has vertices strictly inside its convex hull.
	//      Split the point cloud by a plane through such a vertex — a face plane
	//      incident to it, or an axis plane through it — and recurse. An
	//      axis-aligned notch (the L-shape) splits into two boxes that tile it
	//      exactly. A split only counts when it strictly reduces the number of
	//      interior vertices, so the recursion always terminates, and it is
	//      scored by |children hull volume - piece target volume| (see
	//      SplitPieces): both under-fill and over-fill count against it.
	//   2. ConvexDecomp bisecting k-means sweep (the general fallback): try
	//      k = 1..MaxBrushes hulls and keep the smallest volume error. This is
	//      approximate by construction — hulls of point clusters fill notches —
	//      so it only wins when the reflex split cannot do better, and the
	//      report says so with the numbers. It also inherits the ConvexHull
	//      landmine in the file header (a concave point set can hull dented),
	//      which is why the reflex path is preferred whenever it can run.
	// Exact (see MeshConversionReport) needs the volume match AND every mesh
	// vertex inside at least one brush hull. Anything else is reported, not hidden.

	/// <summary>
	/// Convert a mesh to convex brushes for Source 1. Returns the brushes plus a
	/// conversion report; an open or zero-volume mesh yields no brushes and a
	/// report naming why.
	/// </summary>
	public static MeshExportResult ExportToBrushes( PolyMesh mesh, MeshExportOptions options = null )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		options ??= new MeshExportOptions();
		if ( options.MaxBrushes < 1 )
			throw new ArgumentException( "MaxBrushes must be at least 1.", nameof( options ) );
		if ( options.Tolerance < 0f )
			throw new ArgumentException( "Tolerance must not be negative.", nameof( options ) );

		var result = new MeshExportResult();
		var report = result.Report;
		report.MeshVolume = Volume( mesh );

		if ( mesh.Faces.Count == 0 )
		{
			report.Unrepresentable.Add( "The mesh has no faces, so there is nothing to convert." );
			return result;
		}
		if ( report.MeshVolume <= VolumeEps )
		{
			report.Unrepresentable.Add(
				$"The mesh encloses no volume ({report.MeshVolume:0.######}), so no convex brush can represent it." );
			return result;
		}
		var defects = Validate( mesh );
		var open = defects.Where( d => d.Code is MeshModeDefect.BoundaryEdge
			or MeshModeDefect.NonManifoldEdge or MeshModeDefect.InconsistentWinding ).ToList();
		if ( open.Count > 0 )
		{
			report.Unrepresentable.Add(
				$"The mesh is not a closed solid ({string.Join( "; ", open.Select( d => d.ToString() ) )}); convex brushes cannot represent an open mesh." );
			return result;
		}

		var material = MaterialNameForExport( mesh, options );

		// Path 1: reflex-plane split.
		List<(List<Vec3> Points, List<ConvexHull.HullFace> Faces)> reflexHulls = null;
		var leaves = SplitPieces( mesh, options );
		if ( leaves.Count <= options.MaxBrushes )
		{
			var reflexList = new List<(List<Vec3>, List<ConvexHull.HullFace>)>();
			var ok = true;
			foreach ( var leaf in leaves )
			{
				var hull = ConvexHull.Build( leaf );
				if ( hull is null ) { ok = false; break; }
				reflexList.Add( hull.Value );
			}
			if ( ok ) reflexHulls = reflexList;
			else report.Notes.Add( "Reflex-plane split produced a leaf with no hull; falling back to the k-means sweep." );
		}
		else
		{
			report.Notes.Add(
				$"Reflex-plane split needed {leaves.Count} pieces (limit {options.MaxBrushes}); falling back to the k-means sweep." );
		}

		// Path 2: k-means sweep, k = 1..MaxBrushes.
		List<(List<Vec3> Points, List<ConvexHull.HullFace> Faces)> kmeansHulls = null;
		var kmeansTotal = float.MaxValue;
		for ( var k = 1; k <= options.MaxBrushes; k++ )
		{
			var decomp = ConvexDecomp.Decompose( mesh.Positions,
				new ConvexDecomp.Options { TargetHulls = k, MinHulls = 1, MaxHulls = options.MaxBrushes, SurfaceProp = "flesh" } );
			if ( decomp.Hulls.Count == 0 ) continue;
			var sweepHulls = decomp.Hulls
				.Select( h => (h.Points, h.Faces) ).ToList();
			var total = decomp.Hulls.Sum( h => h.Volume );
			if ( MathF.Abs( total - report.MeshVolume ) < MathF.Abs( kmeansTotal - report.MeshVolume ) )
			{
				kmeansHulls = sweepHulls;
				kmeansTotal = total;
			}
			if ( MathF.Abs( total - report.MeshVolume ) <= options.Tolerance * MathF.Max( 1f, report.MeshVolume ) )
				break; // fewest hulls first, so the first exact-enough k wins.
		}

		// Pick the better path by volume error; the reflex split wins ties.
		List<(List<Vec3> Points, List<ConvexHull.HullFace> Faces)> hulls;
		string path;
		if ( reflexHulls is not null )
		{
			var reflexTotal = reflexHulls.Sum( h => HullVolume( h.Points, h.Faces ) );
			if ( kmeansHulls is null || MathF.Abs( reflexTotal - report.MeshVolume ) <= MathF.Abs( kmeansTotal - report.MeshVolume ) )
			{
				hulls = reflexHulls;
				path = $"reflex-plane split ({leaves.Count} piece(s))";
				report.Notes.Add( $"Reflex-plane split total {reflexTotal:0.###} vs mesh {report.MeshVolume:0.###}." );
			}
			else
			{
				hulls = kmeansHulls;
				path = $"k-means sweep ({kmeansHulls.Count} hull(s))";
				report.Notes.Add( $"K-means sweep total {kmeansTotal:0.###} vs mesh {report.MeshVolume:0.###}; the reflex split was worse." );
			}
		}
		else
		{
			hulls = kmeansHulls;
			path = kmeansHulls is null ? "none" : $"k-means sweep ({kmeansHulls.Count} hull(s))";
			if ( kmeansHulls is not null )
				report.Notes.Add( $"K-means sweep total {kmeansTotal:0.###} vs mesh {report.MeshVolume:0.###}." );
		}

		if ( hulls is null )
		{
			report.Unrepresentable.Add( "No convex decomposition produced a hull: the mesh encloses volume but its points do not hull." );
			return result;
		}

		foreach ( var (points, _) in hulls )
		{
			var brush = MapBrush.FromVertices( points );
			if ( !brush.IsValid )
			{
				report.Unrepresentable.Add(
					$"A decomposition hull is not a valid brush ({string.Join( "; ", brush.Validate().Select( d => d.ToString() ) )}); the export is incomplete." );
				return result;
			}
			result.Brushes.Add( WithMaterial( brush, material ) );
		}

		report.BrushCount = result.Brushes.Count;
		report.BrushVolumeTotal = result.Brushes.Sum( b => b.Volume );

		var uncovered = 0;
		foreach ( var p in mesh.Positions )
		{
			var covered = false;
			foreach ( var (points, faces) in hulls )
				if ( PointInHullOrOn( p, points, faces ) ) { covered = true; break; }
			if ( !covered ) uncovered++;
		}
		if ( uncovered > 0 )
			report.Unrepresentable.Add( $"{uncovered} mesh vertice(s) lie outside every brush hull, so the brushes do not cover the mesh." );

		report.Exact = MathF.Abs( report.ExcessVolume ) <= options.Tolerance * MathF.Max( 1f, report.MeshVolume )
			&& uncovered == 0;
		if ( !report.Exact && report.Unrepresentable.Count == 0 )
			report.Unrepresentable.Add(
				$"Convex brushes overfill the mesh by {report.ExcessRatio:P2} (brush volume {report.BrushVolumeTotal:0.###} vs mesh {report.MeshVolume:0.###}): hulls fill a concave notch the brushes cannot carve out. Split the notch with a reflex-plane cut, or accept the overfill." );
		report.Notes.Add( $"Export via {path}: {report}." );
		return result;
	}

	/// <summary>
	/// Convert a mesh to a Source-1 VMF document (world brushes only, no
	/// entities). Each brush face carries its plane points (first three loop
	/// vertices), its material, rotation / lightmap / smoothing from the brush
	/// face, and the full loop as stored vertices. LIMITATION: the UV axes are
	/// the VmfFace defaults — FaceUv offset/scale are not projected into
	/// VmfUvAxis here; that projection belongs to the VMF ticket (VmfIo).
	/// </summary>
	public static VmfDocument ExportToVmf( PolyMesh mesh, MeshExportOptions options = null )
	{
		var result = ExportToBrushes( mesh, options );
		var doc = new VmfDocument();
		var id = 1;
		foreach ( var brush in result.Brushes )
		{
			var vmfBrush = new VmfBrush { Id = id++ };
			for ( var fi = 0; fi < brush.FaceCount; fi++ )
			{
				var loop = brush.FaceVertices( fi );
				if ( loop.Count < 3 ) continue;
				var face = brush.Faces[fi];
				vmfBrush.Faces.Add( new VmfFace
				{
					Id = fi + 1,
					PlanePoints = new[] { ToVmf( loop[0] ), ToVmf( loop[1] ), ToVmf( loop[2] ) },
					Material = face.MaterialId,
					Rotation = face.Uv.RotationDegrees,
					LightmapScale = Math.Max( 1, (int)MathF.Round( face.Uv.LightmapScale ) ),
					SmoothingGroups = face.Uv.SmoothingGroup,
					StoredVertices = loop.Select( p => ToVmf( p ) ).ToList(),
				} );
			}
			doc.WorldBrushes.Add( vmfBrush );
		}
		return doc;
	}

	static VmfVec ToVmf( Vec3 p ) => new( p.x, p.y, p.z );

	static string MaterialNameForExport( PolyMesh mesh, MeshExportOptions options )
	{
		if ( options.MaterialForSlot is not null )
		{
			var mostCommon = mesh.Faces
				.GroupBy( f => f.Material )
				.OrderByDescending( g => g.Count() )
				.FirstOrDefault()?.Key ?? 0;
			var name = options.MaterialForSlot( mostCommon );
			if ( !string.IsNullOrWhiteSpace( name ) ) return name;
		}
		return string.IsNullOrWhiteSpace( options.DefaultMaterial )
			? "dev/dev_measuregeneric01"
			: options.DefaultMaterial;
	}

	static MapBrush WithMaterial( MapBrush brush, string materialName )
	{
		var faces = brush.Faces
			.Select( f => new MapBrushFace( f.Normal, f.Distance, new FaceMaterial( materialName, f.Uv ) ) )
			.ToList();
		return MapBrush.FromPlanes( faces );
	}

	// Volume enclosed by outward-wound hull faces (same formula as ConvexDecomp).
	static float HullVolume( List<Vec3> points, List<ConvexHull.HullFace> faces )
	{
		var total = 0f;
		foreach ( var face in faces )
			total += Vec3.Dot( points[face.A], Vec3.Cross( points[face.B], points[face.C] ) ) / 6f;
		return MathF.Abs( total );
	}

	// A mesh vertex is "interior" to its own convex hull when it is not a hull
	// vertex at all: strictly inside the hull, or lying on a hull face that
	// spans over it. (A reflex corner of a prismatic notch sits ON the hull's
	// top/bottom face plane, so a strict-inside test would miss it entirely.)
	// A convex piece has no interior vertices; a concave one always has some.
	// Points STRICTLY inside a hull solid: behind every face plane by more than
	// eps. A point ON a face is on the boundary, not interior — counting it
	// (as the first version did, via hull-Points membership) rejects good
	// splits: a split child's own corner can sit on its hull face, e.g. the
	// L-shape's (2, 2) on the split box's y = 2 face. The ROOT interior list in
	// TrySplitPiece still uses hull-Points membership (a point the hull builder
	// did not use at all is concave by construction); this strict test is only
	// for judging whether a split actually buried fewer points.
	static int CountInterior( IReadOnlyList<Vec3> points, (List<Vec3> Points, List<ConvexHull.HullFace> Faces) hull )
	{
		var count = 0;
		foreach ( var p in points )
		{
			var strictlyInside = hull.Faces.Count > 0;
			foreach ( var face in hull.Faces )
			{
				var a = hull.Points[face.A];
				var b = hull.Points[face.B];
				var c = hull.Points[face.C];
				var n = Vec3.Cross( b - a, c - a );
				if ( n.LengthSquared < 1e-12f ) continue;
				n = n.Normal;
				if ( Vec3.Dot( n, p ) > Vec3.Dot( n, a ) - 1e-3f ) { strictlyInside = false; break; }
			}
			if ( strictlyInside ) count++;
		}
		return count;
	}

	// True when p is inside the hull or on its surface.
	static bool PointInHullOrOn( Vec3 p, List<Vec3> points, List<ConvexHull.HullFace> faces )
	{
		foreach ( var face in faces )
		{
			var a = points[face.A];
			var b = points[face.B];
			var c = points[face.C];
			var n = Vec3.Cross( b - a, c - a );
			if ( n.LengthSquared < 1e-12f ) continue;
			n = n.Normal;
			if ( Vec3.Dot( n, p ) > Vec3.Dot( n, a ) + 1e-3f ) return false;
		}
		return true;
	}

	static List<Vec3> Dedup( IReadOnlyList<Vec3> points )
	{
		var seen = new HashSet<Vec3>();
		var list = new List<Vec3>( points.Count );
		foreach ( var p in points )
			if ( seen.Add( p ) ) list.Add( p );
		return list;
	}

	// Recursively split a point cloud by reflex planes until every piece is
	// convex (no interior vertices) or the piece budget runs out.
	// Each piece carries the volume it is supposed to tile: the root's is the
	// mesh volume, a child's is its parent's target split in proportion to the
	// children's hull volumes. A split is scored by how far its children's
	// hull volumes land from that target — under-fill and over-fill both count
	// against it. (Scoring by the smallest total, as first written, prefers
	// LOSSY splits: the L-shape's x = 2 cut totals 12 against the true 24,
	// because the cut crosses an edge at (2, 0), which is not a mesh vertex,
	// so both children under-fill. The y = 2 cut through the reflex vertex
	// totals exactly 24 and wins on the corrected score.)
	static List<List<Vec3>> SplitPieces( PolyMesh mesh, MeshExportOptions options )
	{
		var leaves = new List<List<Vec3>>();
		var stack = new List<(List<Vec3> Points, float Target)> { (Dedup( mesh.Positions ), Volume( mesh )) };
		var vertexFaces = mesh.BuildVertexFaces();
		var positionToIndex = new Dictionary<Vec3, List<int>>();
		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			if ( !positionToIndex.TryGetValue( mesh.Positions[i], out var list ) )
				positionToIndex[mesh.Positions[i]] = list = new List<int>();
			list.Add( i );
		}
		// Every mesh edge (deduped by endpoint pair), for the split cut points.
		var meshEdges = new List<(Vec3 P, Vec3 Q)>();
		var seenEdges = new HashSet<EdgeKey>();
		foreach ( var face in mesh.Faces )
		{
			for ( var i = 0; i < face.Count; i++ )
			{
				var a = face.Indices[i];
				var b = face.Indices[( i + 1 ) % face.Count];
				if ( seenEdges.Add( new EdgeKey( a, b ) ) )
					meshEdges.Add( (mesh.Positions[a], mesh.Positions[b]) );
			}
		}

		var guard = 0;
		while ( stack.Count > 0 && guard++ < 64 )
		{
			var piece = stack[^1];
			stack.RemoveAt( stack.Count - 1 );
			if ( leaves.Count + stack.Count + 2 > options.MaxBrushes )
			{
				leaves.Add( piece.Points );
				continue;
			}
			var split = TrySplitPiece( mesh, piece.Points, piece.Target, vertexFaces, positionToIndex, meshEdges );
			if ( split is null ) leaves.Add( piece.Points );
			else
			{
				stack.Add( split.Value.A );
				stack.Add( split.Value.B );
			}
		}
		leaves.AddRange( stack.Select( p => p.Points ) );
		return leaves;
	}

	// One reflex split of a point cloud, or null when no candidate plane helps.
	// A candidate only counts when it strictly reduces the interior-vertex count.
	// NOTE: the "interior" set comes from ConvexHull.Build, which has a known
	// landmine (see the file header): with interior points on hull faces it can
	// return a dented hull, so the interior set may name real corners instead
	// of the reflex vertices. The split candidates are still the planes through
	// those vertices, and the volume-target score below is what picks the
	// exact cut — the L-shape's y = 2 plane is incident to the corner (4, 2)
	// as well as to the reflex vertex, so it is tried and wins.
	//
	// CUT POINTS. Partitioning the existing vertices alone UNDER-FILLS: the
	// L-shape cut by y = 2 needs the corner (0, 2), where the plane crosses the
	// mesh edge (0, 4) - (0, 0) — that point is not a mesh vertex, and without
	// it the lower piece hulls to a trapezoid (12) instead of the true
	// rectangle (16). So every mesh edge of this piece that straddles the
	// plane contributes its intersection point to BOTH children. That is what
	// makes the L split exact (16 + 8 = 24).
	static ((List<Vec3> Points, float Target) A, (List<Vec3> Points, float Target) B)? TrySplitPiece(
		PolyMesh mesh, List<Vec3> piece, float target,
		List<int>[] vertexFaces, Dictionary<Vec3, List<int>> positionToIndex,
		IReadOnlyList<(Vec3 P, Vec3 Q)> meshEdges )
	{
		var hull = ConvexHull.Build( piece );
		if ( hull is null ) return null;
		var hullSet = new HashSet<Vec3>( hull.Value.Points );
		var interior = piece.Where( p => !hullSet.Contains( p ) ).ToList();
		if ( interior.Count == 0 ) return null;

		// Mesh adjacency inside this piece, for the plane-point rule below.
		var pieceSet = new HashSet<Vec3>( piece );
		var neighbours = new Dictionary<Vec3, List<Vec3>>();
		foreach ( var (p, q) in meshEdges )
		{
			if ( !pieceSet.Contains( p ) || !pieceSet.Contains( q ) ) continue;
			if ( !neighbours.TryGetValue( p, out var lp ) ) neighbours[p] = lp = new List<Vec3>();
			if ( !neighbours.TryGetValue( q, out var lq ) ) neighbours[q] = lq = new List<Vec3>();
			lp.Add( q );
			lq.Add( p );
		}

		((List<Vec3> Points, float Target) A, (List<Vec3> Points, float Target) B)? best = null;
		var bestScore = float.MaxValue;
		var bestInterior = int.MaxValue;

		foreach ( var v in interior )
		{
			if ( !positionToIndex.TryGetValue( v, out var indices ) || indices.Count == 0 ) continue;
			var vi = indices[0];
			var candidates = new List<(Vec3 Normal, float Distance)>();
			foreach ( var fi in vertexFaces[vi] )
			{
				var normal = FaceNormal( mesh, fi );
				if ( normal.LengthSquared < 1e-12f ) continue;
				candidates.Add( (normal, Vec3.Dot( normal, v )) );
			}
			candidates.Add( (new Vec3( 1, 0, 0 ), v.x) );
			candidates.Add( (new Vec3( 0, 1, 0 ), v.y) );
			candidates.Add( (new Vec3( 0, 0, 1 ), v.z) );

			var tried = new List<(Vec3 Normal, float Distance)>();
			foreach ( var (normal, distance) in candidates )
			{
				if ( tried.Any( c => Vec3.Dot( c.Normal, normal ) > 1f - 1e-6f
						&& MathF.Abs( c.Distance - distance ) <= 1e-3f ) )
					continue; // duplicate plane, already tried
				tried.Add( (normal, distance) );

				var a = new List<Vec3>();
				var b = new List<Vec3>();
				foreach ( var p in piece )
				{
					var signed = Vec3.Dot( normal, p ) - distance;
					if ( signed < -1e-3f ) { a.Add( p ); continue; }
					if ( signed > 1e-3f ) { b.Add( p ); continue; }
					// On the plane: a side gets the point only if a mesh edge of
					// this piece reaches from it strictly to that side. Sending
					// every plane point to both sides over-fills: the L-shape's
					// (4, 2) sits on the y = 2 plane but belongs to the lower
					// piece only, and giving it to the upper piece inflates that
					// piece's hull from the true 8 to 12. A point with no edge
					// to either side lies flat on the shared face and goes to both.
					var toA = false;
					var toB = false;
					if ( neighbours.TryGetValue( p, out var nbs ) )
					{
						foreach ( var nb in nbs )
						{
							var s = Vec3.Dot( normal, nb ) - distance;
							if ( s < -1e-3f ) toA = true;
							else if ( s > 1e-3f ) toB = true;
						}
					}
					if ( !toA && !toB ) { toA = true; toB = true; }
					if ( toA ) a.Add( p );
					if ( toB ) b.Add( p );
				}
				// Cut points: mesh edges of this piece that straddle the plane.
				foreach ( var (p, q) in meshEdges )
				{
					if ( !pieceSet.Contains( p ) || !pieceSet.Contains( q ) ) continue;
					var sp = Vec3.Dot( normal, p ) - distance;
					var sq = Vec3.Dot( normal, q ) - distance;
					if ( sp <= -1e-3f && sq >= 1e-3f || sp >= 1e-3f && sq <= -1e-3f )
					{
						var cut = p + ( q - p ) * ( sp / ( sp - sq ) );
						a.Add( cut );
						b.Add( cut );
					}
				}
				if ( a.Count < 4 || b.Count < 4 ) continue;
				if ( a.Count == piece.Count && b.Count == piece.Count ) continue; // plane cuts nothing
				var hullA = ConvexHull.Build( a );
				var hullB = ConvexHull.Build( b );
				if ( hullA is null || hullB is null ) continue;
				var volA = HullVolume( hullA.Value.Points, hullA.Value.Faces );
				var volB = HullVolume( hullB.Value.Points, hullB.Value.Faces );
				if ( volA <= 1e-6f || volB <= 1e-6f ) continue;
				var interiorAfter = CountInterior( a, hullA.Value ) + CountInterior( b, hullB.Value );
				if ( interiorAfter >= interior.Count ) continue;
				var score = MathF.Abs( volA + volB - target );
				if ( score < bestScore - 1e-6f
					|| ( MathF.Abs( score - bestScore ) <= 1e-6f && interiorAfter < bestInterior ) )
				{
					bestScore = score;
					bestInterior = interiorAfter;
					var sum = volA + volB;
					best = ((Dedup( a ), target * volA / sum), (Dedup( b ), target * volB / sum));
				}
			}
		}
		return best;
	}
}

/// <summary>
/// Parametric feature for mesh mode (DirectEditFeature pattern): the feature a
/// UI would drive later. It holds a source mesh + a selection + the op
/// parameters, applies the op on rebuild, and emits the edited mesh as a body.
/// UI: DEFERRED — this ticket builds the backend only; no panel drives this
/// feature yet, and nothing in the viewer constructs one.
/// </summary>
[Feature( "Mesh Mode", Category = "Map", Description = "Mesh-based geometry mode for map meshes: extrude, bevel, inset and bridge on a PolyMesh, with export back to convex brushes. Backend only; no viewport UI." )]
public sealed class MeshModeFeature : Feature
{
	public override string TypeName => "Mesh Mode";

	public readonly ChoiceParam Op = new( "Operation", new[] { "Extrude", "Bevel", "Inset", "Bridge" } );

	/// <summary>Extrude distance / inset amount, inches.</summary>
	public readonly FloatParam Distance = new( "Distance", 1f, unit: "in" );

	/// <summary>Bevel width, inches.</summary>
	public readonly FloatParam Width = new( "Width", 0.5f, 0.0001f, unit: "in" );

	/// <summary>Bevel profile segments (1 = chamfer, more = rounded).</summary>
	public readonly IntParam Segments = new( "Segments", 1, 1, 8 );

	/// <summary>The mesh this edit runs on. Set by the caller, not by a dialog — like DirectEditFeature.Faces.</summary>
	public PolyMesh SourceMesh;

	/// <summary>What the op acts on (faces for Extrude / Inset / Bridge, edges for Bevel).</summary>
	public MeshSelection Selection = new();

	/// <summary>The mesh after this rebuild's edit. Empty before the first run.</summary>
	public PolyMesh ResultMesh;

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Op, Distance, Width, Segments };

	protected override void Execute( FeatureContext ctx )
	{
		if ( SourceMesh is null )
			FailOn( "Source mesh", "Mesh Mode has no source mesh",
				"Set SourceMesh before rebuilding.",
				"Add a Map Brush feature upstream and rebuild it into a mesh first" );

		PolyMesh result = null;
		try
		{
			var op = Op.Value switch
			{
				"Extrude" => MeshModeOp.Extrude,
				"Bevel" => MeshModeOp.Bevel,
				"Inset" => MeshModeOp.Inset,
				"Bridge" => MeshModeOp.Bridge,
				_ => throw new InvalidOperationException( $"Unknown mesh op '{Op.Value}'." ),
			};
			result = MeshMode.Apply( op, SourceMesh, Selection, Distance.Value, Width.Value, Segments.Value );
		}
		catch ( InvalidOperationException ex )
		{
			// The reason goes in the problem string, not the cause: Feature.Error
			// carries the problem, and the test (and the UI) reads the error.
			FailOn( "Operation", $"Mesh Mode refused the edit: {ex.Message}",
				"Adjust the selection or amount and rebuild" );
		}
		if ( result is null )
			FailOn( "Operation", "Mesh Mode produced no mesh",
				"The edit refused without a reason.",
				"Adjust the selection or amount and rebuild" );

		ResultMesh = result;
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, ResultMesh ) );
		Info( $"{Op.Value} applied: {ResultMesh.FaceCount} faces, volume {MeshMode.Volume( ResultMesh ):0.###}" );
	}
}
