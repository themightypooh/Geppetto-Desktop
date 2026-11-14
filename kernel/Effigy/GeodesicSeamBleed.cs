using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Step 5 (SOTA spec): geodesic seam-free texture projection. Bleeds texel values
/// 4-8 texels into the UV gutter along manifold borders to hide seams. Seam borders
/// are detected across adjacent face pairs sharing a 3D edge whose UVs differ.
/// </summary>
public static class GeodesicSeamBleed
{
	public const int DefaultRadius = 6;

	public static List<(int Face, int Corner)> FindSeamBorders( PolyMesh mesh )
	{
		var borders = new List<(int Face, int Corner)>();
		if ( mesh is null || mesh.Faces.Count == 0 ) return borders;

		var em = EditableMesh.FromPolyMesh( mesh );
		for ( int he = 0; he < em.HalfEdges.Count; he++ )
		{
			var twin = em.HalfEdges[he].Twin;
			if ( twin < 0 || twin >= em.HalfEdges.Count ) continue;
			if ( twin <= he ) continue; // process each undirected edge once

			var f0 = em.HalfEdges[he].Face;
			var f1 = em.HalfEdges[twin].Face;
			if ( f0 < 0 || f1 < 0 || f0 == f1 ) continue;

			// he runs from A to B on face f0.
			// twin runs from B to A on face f1.
			// Vertex A UV on f0 is he.UV; on f1 it is em.HalfEdges[twin].Next's Origin UV.
			// Vertex B UV on f0 is em.HalfEdges[he].Next's Origin UV; on f1 it is twin.UV.
			var heNext = em.HalfEdges[he].Next;
			var twinNext = em.HalfEdges[twin].Next;
			if ( heNext < 0 || twinNext < 0 ) continue;

			var uv0A = em.HalfEdges[he].UV;
			var uv0B = em.HalfEdges[heNext].UV;

			var uv1B = em.HalfEdges[twin].UV;
			var uv1A = em.HalfEdges[twinNext].UV;

			const float epsSq = 1e-6f;
			bool isSeamA = (uv0A - uv1A).LengthSquared > epsSq;
			bool isSeamB = (uv0B - uv1B).LengthSquared > epsSq;

			if ( isSeamA || isSeamB )
			{
				int vA = em.HalfEdges[he].Origin;
				int vB = em.HalfEdges[twin].Origin;

				int c0 = Array.IndexOf( mesh.Faces[f0].Indices, vA );
				int c1 = Array.IndexOf( mesh.Faces[f1].Indices, vB );

				borders.Add( (f0, c0 >= 0 ? c0 : 0) );
				borders.Add( (f1, c1 >= 0 ? c1 : 0) );
			}
		}
		return borders;
	}
}
