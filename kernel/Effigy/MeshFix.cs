using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Clean-ups by name, so a script or an agent can leave a mesh tidy without reading the check:
/// `loose`, `doubles`, `holes`, `normals`, or `all`. Each is an Edit-mode operation run headlessly
/// on the whole mesh and written back in place.
/// </summary>
public static class MeshFix
{
	public static readonly string[] Names = { "loose", "doubles", "degenerate", "holes", "uvs", "normals" };

	/// <summary>Last-resort rebuild, explicit only: never part of "all".</summary>
	public const string ManifoldFix = "manifold";

	/// <summary>Apply the named fixes to a mesh, in the order that makes sense: loose vertices
	/// first, merge doubles before filling holes (a hairline gap is a double, not a hole), fix normals last. Returns
	/// what was done, one line each.</summary>
	public static List<string> Apply( PolyMesh mesh, IEnumerable<string> fixes, float doublesTolerance = 0.001f, bool holesFan = false, float manifoldVoxel = 0.25f )
	{
		var done = new List<string>();

		if ( mesh is null || mesh.Positions.Count == 0 )
			return done;

		var wanted = new HashSet<string>( (fixes ?? Array.Empty<string>()).Select( f => f.Trim().ToLowerInvariant() ) );

		if ( wanted.Contains( "all" ) )
			wanted.UnionWith( Names );

		var session = new MeshEditSession( mesh );

		var beforeTopo = TopologyReport.Measure( mesh, "before" );

		// Loose first: a merge compacts the mesh too, so running it first would hide the loose
		// vertices inside the doubles count and leave DeleteLoose nothing to do (it throws on that).
		if ( wanted.Contains( "loose" ) )
		{
			var used = new HashSet<int>();
			foreach ( var f in session.Mesh.Faces )
				used.UnionWith( f.Indices );

			var before = session.Mesh.VertexCount;
			if ( used.Count < before )
				session.DeleteLoose();

			done.Add( $"loose: removed {before - session.Mesh.VertexCount} unused vertices" );
		}

		if ( wanted.Contains( "doubles" ) )
		{
			// Staged, scale-relative welding (repair proposal P7): tiny
			// tolerance first, growing only while seams keep closing.
			foreach ( var line in MeshRepair.WeldStaged( session.Mesh, doublesTolerance ) )
				done.Add( line );
		}

		if ( wanted.Contains( "degenerate" ) )
		{
			var removed = MeshRepair.RemoveDegenerate( session.Mesh );
			session.ClearSelection();
			done.Add( $"degenerate: removed {removed} degenerate faces" );
		}

		if ( wanted.Contains( "holes" ) )
		{
			if ( holesFan )
			{
				// Legacy single-fan close, kept behind fan=1 for scripts
				// that depended on it. The refusal rolls its step back, so
				// the mesh is untouched when there is nothing to fill.
				int closed;
				try { closed = session.FillHoles( 6 ); }
				catch ( InvalidOperationException ) { closed = 0; }
				done.Add( $"holes: closed {closed} with a fan (up to six sides; bigger openings were left)" );
			}
			else
			{
				// Ear-clipping triangulation (repair proposal P5): concave
				// and large flat holes close with triangles and interpolated
				// UVs; non-planar rings are reported, not folded shut.
				var (closed, unfilled) = MeshRepair.FillHolesV2( session.Mesh );
				done.Add( $"holes: closed {closed} with triangles (UVs interpolated)"
					+ (unfilled.Count > 0 ? $"; left open: {string.Join( "; ", unfilled )}" : "") );
			}
		}

		if ( wanted.Contains( "uvs" ) )
		{
			var fixed_ = MeshRepair.BoxProjectUvs( session.Mesh );
			done.Add( $"uvs: box-projected UVs onto {fixed_} face(s) that had none" );
		}

		if ( wanted.Contains( "normals" ) )
		{
			// Signed volume per shell first (repair proposal P3): a shell
			// with negative volume is flipped wholesale, then the flood
			// makes each piece locally consistent.
			var flips = MeshRepair.FlipShellsByVolume( session.Mesh );
			var flipped = flips.Count( l => l.Contains( "(flipped)" ) );
			session.ClearSelection();
			session.RecalculateNormals();
			done.Add( $"normals: flipped {flipped} inward shell(s) by signed volume, then made every piece consistent and outward" );
		}

		if ( wanted.Contains( ManifoldFix ) )
		{
			var (ok, deviation, note) = MeshRepair.ManifoldFallback( session.Mesh, manifoldVoxel );
			done.Add( ok ? $"manifold: rebuilt as a closed shell — {note}" : $"manifold: failed — {note}" );
		}

		var unknown = wanted.Where( w => w != "all" && !Names.Contains( w ) && w != ManifoldFix ).ToList();

		if ( unknown.Count > 0 )
			done.Add( $"ignored: {string.Join( ", ", unknown )} (fixes are {string.Join( ", ", Names )}, manifold, or all)" );

		// Before/after topology delta (repair proposal P6): a repair step
		// that cannot prove improvement is a guess. Measured on the session
		// mesh, before it is written back.
		{
			var afterTopo = TopologyReport.Measure( session.Mesh, "after" );
			var clean = afterTopo.BoundaryEdges == 0 && afterTopo.NonManifoldEdges == 0 && afterTopo.Degenerate == 0;
			done.Add( $"topology delta: open edges {beforeTopo.BoundaryEdges} -> {afterTopo.BoundaryEdges}, non-manifold edges {beforeTopo.NonManifoldEdges} -> {afterTopo.NonManifoldEdges}, degenerate {beforeTopo.Degenerate} -> {afterTopo.Degenerate}, loose vertices {beforeTopo.LooseVertices} -> {afterTopo.LooseVertices}; {(clean ? "clean" : "still open: see counts")}" );
		}

		// Write back in place: the body keeps its id and everything holding it.
		var result = session.Mesh;
		mesh.Positions.Clear();
		mesh.Positions.AddRange( result.Positions );
		mesh.Faces.Clear();
		mesh.Faces.AddRange( result.Faces );
		CopyOptional( result, mesh );

		return done;
	}

	static void CopyOptional( PolyMesh from, PolyMesh to )
	{
		to.Skin = from.Skin;
		to.VertexColors = from.HasVertexColors ? from.VertexColors : null;
	}
}
