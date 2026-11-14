using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

public static class SmartSelect
{
	public static List<int> EdgeLoop( PolyMesh mesh, int edgeA, int edgeB ) => new List<int>();
	public static List<int> Ring( PolyMesh mesh, int edge ) => new List<int>();
	public static List<int> SimilarFaces( PolyMesh mesh, int face, float normalTolerance = 0.9f ) =>
		mesh.Faces.Select( ( f, i ) => i ).ToList();
	public static List<int> Grow( PolyMesh mesh, IEnumerable<int> selection, int steps = 1 ) => selection.ToList();
	public static List<int> Shrink( PolyMesh mesh, IEnumerable<int> selection, int steps = 1 ) => selection.ToList();
}