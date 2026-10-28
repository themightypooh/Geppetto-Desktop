using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>
/// "Is this mesh good enough to ship?" as numbers, not eyeballing. Wraps the topology
/// measurement the tool already trusts (quad fraction, poles by type and location, edge CV,
/// aspect, skew, manifold/open edges, joint loop counts when a rig is given) and adds the
/// retopo-specific bits: UV coverage after a transfer, and — when a source mesh is supplied —
/// surface deviation of the new mesh from the source, measured by projecting new vertices onto
/// the source with <see cref="MeshBVH"/> (the same projection
/// <see cref="AttributeTransfer"/> uses).
///
/// Self-intersection is deliberately NOT claimed here: the kernel has no triangle-triangle
/// intersection test, and a home-grown one that flags every shared-edge touch as a hit would
/// be worse than no number. The manifold/open-edge counts catch the holes a bad remesh
/// actually leaves; a self-intersection check is follow-up, not a faked line in this report.
/// The joint-bend deformation test is likewise not faked: bending a joint needs a posed
/// deform pass through the rig, which the script verb cannot drive without a pose, so the
/// report names the poles near joints (TopologyReport already measures JointDistance) and
/// leaves the bend test to a posed run.
/// </summary>
public static class RetopoReport
{
	public sealed class Report
	{
		public string Topology = "";
		public int FacesWithUvs;
		public int Faces;
		public float UvCoverage => Faces == 0 ? 0f : (float)FacesWithUvs / Faces;
		public float MaxDeviation = float.NaN;
		public float MeanDeviation = float.NaN;
		public bool HasDeviation;

		public override string ToString()
		{
			var sb = new StringBuilder();
			sb.Append( Topology );
			sb.AppendLine( $"  UV coverage: {FacesWithUvs}/{Faces} faces ({UvCoverage:P0} with a non-zero UV)" );
			if ( HasDeviation )
				sb.AppendLine( $"  surface deviation from source: max {MaxDeviation:0.###}, mean {MeanDeviation:0.###} (new vertices projected onto source)" );
			else
				sb.AppendLine( "  surface deviation: no source mesh supplied" );
			return sb.ToString().TrimEnd();
		}
	}

	public static Report Measure( PolyMesh mesh, PolyMesh source = null, Skeleton rig = null, string name = null )
	{
		var report = new Report { Faces = mesh?.FaceCount ?? 0 };
		if ( mesh is null || mesh.FaceCount == 0 )
		{
			report.Topology = "retopo report: empty mesh";
			return report;
		}
		report.Topology = TopologyReport.Measure( mesh, name ?? "mesh", rig ).Describe();
		report.FacesWithUvs = mesh.Faces.Count( f => f.UVs.Any( uv => uv.x != 0f || uv.y != 0f ) );
		if ( source is not null && source.FaceCount > 0 )
		{
			var bvh = MeshBVH.Build( source );
			var maxDistance = MathF.Max( source.BoundsDiagonal, 1e-6f ) * 2f + 1f;
			double sum = 0;
			var count = 0;
			var max = 0f;
			foreach ( var p in mesh.Positions )
			{
				var hit = bvh.NearestSurface( source, p, maxDistance );
				if ( hit is not { } h ) continue;
				sum += h.Distance;
				if ( h.Distance > max ) max = h.Distance;
				count++;
			}
			if ( count > 0 )
			{
				report.MaxDeviation = max;
				report.MeanDeviation = (float)(sum / count);
				report.HasDeviation = true;
			}
		}
		return report;
	}
}
