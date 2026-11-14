using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>
/// One-click Mesh Doctor: the single integrity contract for a <see cref="PolyMesh"/>.
///
/// WHY THIS EXISTS WHEN MeshRepair/MeshFix ALREADY EXIST: the pieces were scattered and
/// nothing tied them together. <see cref="MeshValidator.Validate"/> counts boundary and
/// non-manifold edges; <see cref="MeshRepair.Validate"/> grades a mesh against a target;
/// <see cref="MeshFix.Apply"/> fixes by name with no undo; nothing ran before export, so
/// holes, non-manifold edges and flipped shells shipped silently. MeshDoctor is the thin
/// contract on top of those facilities, not a second implementation of them:
///
///   - <see cref="Check"/> — one structured report with EXACT counts (holes as boundary
///     loops, non-manifold edges/vertices, flipped faces, degenerate faces, zero-area
///     faces, duplicate faces, loose vertices), built from the existing counters.
///   - <see cref="Fix"/> — one-click fix through <see cref="MeshFix.Apply"/>, one named
///     fix at a time, with a pre-fix snapshot per step so every fix is individually
///     undoable (<see cref="UndoLast"/>, <see cref="UndoAll"/>).
///   - <see cref="PreExportCheck(PolyMesh, string, bool)"/> — the pre-export hook: BLOCK
///     on the geometric hard fails, WARN on holes and craft misses. The single caller that
///     makes it automatic is the gate in ExportService.Export; see the wiring note there.
///
/// BLOCK vs WARN, and why holes only warn: the hard-fail set mirrors the MeshRepair gate
/// table (FINDINGS.md P1-P10) for non-manifold edges/vertices, degenerate and duplicate
/// faces, inward shells and measured self-intersections. Boundary edges are hard FAIL in
/// that table, but this codebase legitimately ships open surfaces — single-sided cloth,
/// single-triangle fixtures, FBX character parts with open boundaries — so the export gate
/// reports every hole exactly and warns instead of blocking on it. A print pipeline that
/// needs zero holes passes strictHoles: true. A mesh with a hole is never silent again;
/// it is never a surprise either.
/// </summary>
public sealed class MeshDoctorReport
{
	public string Name = "";
	public int Vertices, Faces, Triangles, Quads, Ngons;
	public int BoundaryEdges, Holes, ClosedLoops, OpenChains;
	public int NonManifoldEdges, NonManifoldVertices;
	public int DegenerateFaces, ZeroAreaFaces, DuplicateFaces, FlippedFaces, LooseVertices;
	public int Shells;
	public List<float> ShellVolumes = new List<float>();
	public int Euler;
	public int UvMissingFaces;
	/// <summary>Triangle self-intersections; -1 when not measured (mesh too large or clash failed).</summary>
	public int SelfIntersections = -1;

	public float QuadPercent => Faces == 0 ? 0f : 100f * Quads / Faces;
	public bool IsClosed => BoundaryEdges == 0 && NonManifoldEdges == 0;
	public bool IsManifold => NonManifoldEdges == 0 && NonManifoldVertices == 0;
	/// <summary>No face sits in an inward (negative-volume) closed shell.</summary>
	public bool IsOutward => FlippedFaces == 0;

	/// <summary>Defects that block export: non-manifold topology, flipped shells, degenerate
	/// or duplicate faces, measured self-intersections. Holes are NOT in this list (see class doc).</summary>
	public List<string> HardFails = new List<string>();
	/// <summary>Defects that warn on export: holes, loose vertices, missing UVs, low quad share,
	/// unmeasured self-intersections.</summary>
	public List<string> Warnings = new List<string>();

	public bool BlocksExport => HardFails.Count > 0;
	public bool IsClean => HardFails.Count == 0 && Warnings.Count == 0;

	public string Describe()
	{
		var sb = new StringBuilder();
		sb.Append( $"{Name}: {Vertices}v {Faces}f ({Triangles}t {Quads}q {Ngons}n), " );
		sb.Append( $"holes {Holes} ({BoundaryEdges} boundary edges), non-manifold edges {NonManifoldEdges}, " );
		sb.Append( $"non-manifold verts {NonManifoldVertices}, degenerate {DegenerateFaces}, zero-area {ZeroAreaFaces}, " );
		sb.Append( $"duplicate {DuplicateFaces}, flipped {FlippedFaces}, loose verts {LooseVertices}, " );
		sb.Append( $"shells {Shells}, euler {Euler}, quad {QuadPercent:0.#}%" );
		if ( HardFails.Count > 0 ) sb.Append( $"; BLOCK: {string.Join( ", ", HardFails )}" );
		if ( Warnings.Count > 0 ) sb.Append( $"; warn: {string.Join( ", ", Warnings )}" );
		return sb.ToString();
	}
}

/// <summary>One boundary loop: an edge-disjoint walk over boundary edges. Closed when the walk
/// returns to its start vertex; an open chain ends at a vertex whose remaining boundary edges
/// were all consumed by other loops (only possible around non-manifold vertices).</summary>
public sealed class BoundaryLoop
{
	public List<int> Vertices = new List<int>();
	public bool Closed;
}

/// <summary>One applied fix step: the label, what it did, and the mesh as it was before the step,
/// so the step can be undone without touching the other steps.</summary>
public sealed class AppliedFix
{
	public string Label = "";
	public string Detail = "";
	public PolyMesh Before;
}

/// <summary>Result of <see cref="MeshDoctor.Fix"/>: the applied steps in order, the reports
/// before/after, and the per-step log lines.</summary>
public sealed class MeshDoctorFixResult
{
	public List<AppliedFix> AppliedFixes = new List<AppliedFix>();
	public MeshDoctorReport Before;
	public MeshDoctorReport After;
	public List<string> Log = new List<string>();
}

/// <summary>Options for <see cref="MeshDoctor.Fix"/>. Defaults mirror MeshFix.Apply("all"):
/// loose, doubles, degenerate, holes, normals — in that order. UV box-projection and the
/// last-resort manifold rebuild are off unless explicitly asked for, matching MeshFix.</summary>
public sealed class MeshDoctorFixOptions
{
	public bool Loose = true;
	public bool Doubles = true;
	public bool Degenerate = true;
	public bool Holes = true;
	public bool Normals = true;
	public bool Uvs = false;
	public bool ManifoldFallback = false;
	public float DoublesTolerance = 0.001f;
}

/// <summary>Outcome of the pre-export hook for one mesh.</summary>
public sealed class PreExportDecision
{
	public bool Block;
	public bool Warn;
	public MeshDoctorReport Report;
	public string Summary = "";
}

public static class MeshDoctor
{
	/// <summary>Zero-area threshold, same as MeshRepair.RemoveDegenerate (1e-12).</summary>
	public const float ZeroAreaEpsilon = 1e-12f;

	/// <summary>Self-intersection is measured only up to this many triangles, same cap as
	/// MeshRepair.Validate; above it the check is skipped and reported as unmeasured.</summary>
	public const int SelfIntersectionTriangleCap = 30000;

	/// <summary>One structured integrity report with exact counts. Every count is defined in the
	/// code below; degenerate faces are topology-degenerate (&lt;3 corners or a repeated vertex)
	/// and zero-area faces are the disjoint remainder (area &lt;= 1e-12 with sound topology).
	/// Flipped faces are faces in a CLOSED shell with negative signed volume; an open shell's
	/// signed volume is not a reliable inward signal (a lone triangle's sign depends on where
	/// it floats), so open shells never contribute flipped faces.</summary>
	public static MeshDoctorReport Check( PolyMesh mesh, string name = null )
	{
		var r = new MeshDoctorReport { Name = name ?? "mesh" };
		if ( mesh is null || mesh.FaceCount == 0 )
		{
			r.HardFails.Add( "empty mesh" );
			r.Warnings.Add( "no faces to validate" );
			return r;
		}

		r.Vertices = mesh.VertexCount;
		r.Faces = mesh.FaceCount;

		var edgeFaces = mesh.BuildEdgeFaces();
		foreach ( var kv in edgeFaces )
		{
			if ( kv.Value.Count == 1 ) r.BoundaryEdges++;
			else if ( kv.Value.Count > 2 ) r.NonManifoldEdges++;
		}

		var loops = FindBoundaryLoops( mesh, edgeFaces );
		r.Holes = loops.Count;
		foreach ( var loop in loops )
		{
			if ( loop.Closed ) r.ClosedLoops++;
			else r.OpenChains++;
		}

		var used = new HashSet<int>();
		for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			var f = mesh.Faces[fi];
			if ( f.Count == 3 ) r.Triangles++;
			else if ( f.Count == 4 ) r.Quads++;
			else if ( f.Count > 4 ) r.Ngons++;

			foreach ( var i in f.Indices ) used.Add( i );

			var degenerate = f.Count < 3 || RepeatsAVertex( f.Indices );
			if ( degenerate ) r.DegenerateFaces++;
			else if ( mesh.FaceArea( f ) <= ZeroAreaEpsilon ) r.ZeroAreaFaces++;

			if ( f.UVs.Length != f.Count || AllZeroUvs( f.UVs ) ) r.UvMissingFaces++;
		}
		r.LooseVertices = mesh.VertexCount - used.Count;

		r.NonManifoldVertices = MeshRepair.NonManifoldVertexList( mesh ).Count;
		r.DuplicateFaces = MeshRepair.DuplicateFaceCount( mesh );

		// Flipped faces: faces in a closed shell whose signed volume is negative.
		var shells = MeshRepair.ShellFaces( mesh );
		r.Shells = shells.Count;
		foreach ( var shell in shells )
		{
			var volume = MeshRepair.SignedVolume( mesh, shell );
			r.ShellVolumes.Add( volume );
			if ( volume >= 0f ) continue;
			if ( !ShellIsClosed( mesh, shell, edgeFaces ) ) continue;
			r.FlippedFaces += shell.Count;
		}

		r.Euler = MeshValidator.EulerCharacteristic( mesh );

		var triCount = 0;
		foreach ( var f in mesh.Faces ) triCount += Math.Max( 0, f.Count - 2 );
		if ( triCount > SelfIntersectionTriangleCap ) r.SelfIntersections = -1;
		else
		{
			try { r.SelfIntersections = MeshClash.Self( TriangleSet.From( mesh ) ).Crossings; }
			catch { r.SelfIntersections = -1; }
		}

		if ( r.NonManifoldEdges > 0 ) r.HardFails.Add( $"non-manifold edges: {r.NonManifoldEdges}" );
		if ( r.NonManifoldVertices > 0 ) r.HardFails.Add( $"non-manifold vertices: {r.NonManifoldVertices}" );
		if ( r.DegenerateFaces > 0 ) r.HardFails.Add( $"degenerate faces: {r.DegenerateFaces}" );
		if ( r.ZeroAreaFaces > 0 ) r.HardFails.Add( $"zero-area faces: {r.ZeroAreaFaces}" );
		if ( r.DuplicateFaces > 0 ) r.HardFails.Add( $"duplicate faces: {r.DuplicateFaces}" );
		if ( r.FlippedFaces > 0 ) r.HardFails.Add( $"flipped faces: {r.FlippedFaces}" );
		if ( r.SelfIntersections > 0 ) r.HardFails.Add( $"self-intersections: {r.SelfIntersections}" );

		if ( r.Holes > 0 ) r.Warnings.Add( $"holes: {r.Holes} boundary loop(s), {r.BoundaryEdges} boundary edges" );
		if ( r.LooseVertices > 0 ) r.Warnings.Add( $"loose vertices: {r.LooseVertices}" );
		if ( r.UvMissingFaces > 0 ) r.Warnings.Add( $"faces without UVs: {r.UvMissingFaces}" );
		if ( r.QuadPercent < 85f ) r.Warnings.Add( $"quad share {r.QuadPercent:0.#}% below 85%" );
		if ( r.SelfIntersections < 0 ) r.Warnings.Add( "self-intersections not measured (mesh too large or clash failed)" );

		return r;
	}

	static bool RepeatsAVertex( int[] indices )
	{
		for ( var i = 1; i < indices.Length; i++ )
			for ( var j = 0; j < i; j++ )
				if ( indices[i] == indices[j] ) return true;
		return false;
	}

	static bool AllZeroUvs( Vec2[] uvs )
	{
		foreach ( var uv in uvs )
			if ( uv.x != 0f || uv.y != 0f ) return false;
		return true;
	}

	static bool ShellIsClosed( PolyMesh mesh, List<int> shellFaces, Dictionary<EdgeKey, List<int>> edgeFaces )
	{
		var shell = new HashSet<int>( shellFaces );
		foreach ( var kv in edgeFaces )
		{
			if ( kv.Value.Count != 1 ) continue;
			foreach ( var fi in kv.Value )
				if ( shell.Contains( fi ) ) return false;
		}
		return true;
	}

	/// <summary>Boundary loops of a mesh: edge-disjoint walks over the boundary edges (edges
	/// used by exactly one face). A walk that returns to its start vertex is a hole ring; a
	/// walk that strands is an open chain (possible only around non-manifold vertices, where
	/// one vertex fans into more than two boundary edges).</summary>
	public static List<BoundaryLoop> FindBoundaryLoops( PolyMesh mesh )
		=> FindBoundaryLoops( mesh, mesh.BuildEdgeFaces() );

	static List<BoundaryLoop> FindBoundaryLoops( PolyMesh mesh, Dictionary<EdgeKey, List<int>> edgeFaces )
	{
		var loops = new List<BoundaryLoop>();
		var neighbours = new Dictionary<int, List<int>>();
		foreach ( var kv in edgeFaces )
		{
			if ( kv.Value.Count != 1 ) continue;
			AddNeighbour( neighbours, kv.Key.A, kv.Key.B );
			AddNeighbour( neighbours, kv.Key.B, kv.Key.A );
		}

		var visited = new HashSet<EdgeKey>();
		foreach ( var kv in edgeFaces )
		{
			if ( kv.Value.Count != 1 || visited.Contains( kv.Key ) ) continue;
			var loop = new BoundaryLoop();
			var start = kv.Key.A;
			var current = kv.Key.B;
			loop.Vertices.Add( start );
			loop.Vertices.Add( current );
			visited.Add( kv.Key );
			while ( true )
			{
				var next = -1;
				if ( neighbours.TryGetValue( current, out var ns ) )
				{
					foreach ( var n in ns )
					{
						var e = new EdgeKey( current, n );
						if ( !visited.Contains( e ) ) { next = n; break; }
					}
				}
				if ( next < 0 ) break;
				visited.Add( new EdgeKey( current, next ) );
				if ( next == start ) { loop.Closed = true; break; }
				loop.Vertices.Add( next );
				current = next;
			}
			loops.Add( loop );
		}
		return loops;
	}

	static void AddNeighbour( Dictionary<int, List<int>> neighbours, int from, int to )
	{
		if ( !neighbours.TryGetValue( from, out var list ) ) neighbours[from] = list = new List<int>();
		if ( !list.Contains( to ) ) list.Add( to );
	}

	/// <summary>One-click fix: applies the named MeshFix steps in MeshFix order (loose,
	/// doubles, degenerate, holes, normals, then UVs / manifold fallback only when asked),
	/// snapshotting the mesh before each step. The mesh is fixed in place; the returned
	/// result carries one undoable step per fix. A step that changes nothing still records
	/// its snapshot, so undoing it is a no-op rather than a surprise.</summary>
	public static MeshDoctorFixResult Fix( PolyMesh mesh, MeshDoctorFixOptions options = null )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		options ??= new MeshDoctorFixOptions();
		var result = new MeshDoctorFixResult { Before = Check( mesh ) };

		ApplyStep( mesh, result, "loose", options.Loose, () => MeshFix.Apply( mesh, new[] { "loose" } ) );
		ApplyStep( mesh, result, "doubles", options.Doubles, () => MeshFix.Apply( mesh, new[] { "doubles" }, options.DoublesTolerance ) );
		ApplyStep( mesh, result, "degenerate", options.Degenerate, () => MeshFix.Apply( mesh, new[] { "degenerate" } ) );
		ApplyStep( mesh, result, "holes", options.Holes, () => MeshFix.Apply( mesh, new[] { "holes" } ) );
		ApplyStep( mesh, result, "uvs", options.Uvs, () => MeshFix.Apply( mesh, new[] { "uvs" } ) );
		ApplyStep( mesh, result, "normals", options.Normals, () => MeshFix.Apply( mesh, new[] { "normals" } ) );
		ApplyStep( mesh, result, "manifold", options.ManifoldFallback, () => MeshFix.Apply( mesh, new[] { MeshFix.ManifoldFix } ) );

		result.After = Check( mesh );
		return result;
	}

	static void ApplyStep( PolyMesh mesh, MeshDoctorFixResult result, string label, bool enabled, Func<List<string>> apply )
	{
		if ( !enabled ) return;
		var before = mesh.Clone();
		var lines = apply();
		result.AppliedFixes.Add( new AppliedFix { Label = label, Detail = string.Join( "; ", lines ), Before = before } );
		result.Log.AddRange( lines );
	}

	/// <summary>Undo the most recent applied fix step, restoring the mesh to its state before
	/// that step. Returns false when there is nothing to undo.</summary>
	public static bool UndoLast( PolyMesh mesh, MeshDoctorFixResult result )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( result is null || result.AppliedFixes.Count == 0 ) return false;
		var step = result.AppliedFixes[result.AppliedFixes.Count - 1];
		result.AppliedFixes.RemoveAt( result.AppliedFixes.Count - 1 );
		Restore( mesh, step.Before );
		return true;
	}

	/// <summary>Undo every applied fix step, restoring the mesh to its state before <see cref="Fix"/>.</summary>
	public static void UndoAll( PolyMesh mesh, MeshDoctorFixResult result )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( result is null || result.AppliedFixes.Count == 0 ) return;
		Restore( mesh, result.AppliedFixes[0].Before );
		result.AppliedFixes.Clear();
	}

	static void Restore( PolyMesh mesh, PolyMesh snapshot )
	{
		mesh.Positions.Clear();
		mesh.Positions.AddRange( snapshot.Positions );
		mesh.Faces.Clear();
		foreach ( var f in snapshot.Faces )
			mesh.Faces.Add( new Face( (int[])f.Indices.Clone(), (Vec2[])f.UVs.Clone(), f.Material ) );
		mesh.Skin = snapshot.Skin?.Clone();
		mesh.VertexColors = snapshot.VertexColors is null ? null : (Vec4[])snapshot.VertexColors.Clone();
		mesh.VertexEmission = snapshot.VertexEmission is null ? null : (Vec3[])snapshot.VertexEmission.Clone();
		mesh.UV2 = snapshot.UV2 is null ? new List<Vec2>() : new List<Vec2>( snapshot.UV2 );
		mesh.Seams = snapshot.Seams is null ? new HashSet<EdgeKey>() : new HashSet<EdgeKey>( snapshot.Seams );
		mesh.Morphs = snapshot.Morphs.Where( m => m is not null ).Select( m => m.Clone() ).ToList();
		mesh.InvalidateVertexCache();
	}

	/// <summary>The pre-export hook for one mesh. Blocks on the geometric hard fails; warns on
	/// holes and craft misses. Pass strictHoles: true when the caller needs a watertight
	/// solid (print pipeline) instead of an open surface.</summary>
	public static PreExportDecision PreExportCheck( PolyMesh mesh, string name = null, bool strictHoles = false )
	{
		var report = Check( mesh, name );
		var decision = new PreExportDecision { Report = report };
		decision.Block = report.BlocksExport || (strictHoles && report.Holes > 0);
		decision.Warn = !decision.Block && report.Warnings.Count > 0;
		var sb = new StringBuilder();
		sb.Append( $"{report.Name}: {(decision.Block ? "BLOCKED" : decision.Warn ? "warn" : "pass")} — {report.Describe()}" );
		decision.Summary = sb.ToString();
		return decision;
	}

	/// <summary>The pre-export hook for a whole export: blocks when any mesh blocks, warns when
	/// any mesh warns. Meshes with no faces are skipped (the capability check owns "has geometry").</summary>
	public static PreExportDecision PreExportCheck( IEnumerable<KeyValuePair<string, PolyMesh>> meshes, bool strictHoles = false )
	{
		var decision = new PreExportDecision { Report = new MeshDoctorReport { Name = "export" } };
		var sb = new StringBuilder();
		var any = false;
		MeshDoctorReport worst = null;
		foreach ( var kv in meshes )
		{
			if ( kv.Value is null || kv.Value.FaceCount == 0 ) continue;
			any = true;
			var one = PreExportCheck( kv.Value, kv.Key, strictHoles );
			if ( one.Block ) decision.Block = true;
			else if ( one.Warn ) decision.Warn = true;
			if ( worst is null || one.Report.HardFails.Count > worst.HardFails.Count
				|| (one.Report.HardFails.Count == worst.HardFails.Count && one.Report.Warnings.Count > worst.Warnings.Count) )
				worst = one.Report;
			sb.AppendLine( one.Summary );
		}
		if ( any && worst is not null ) decision.Report = worst;
		decision.Summary = sb.ToString().TrimEnd();
		return decision;
	}
}
