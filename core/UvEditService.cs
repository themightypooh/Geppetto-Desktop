using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Marionette.Desktop;

public static class UvEditService
{
	public sealed class Options
	{
		/// <summary>Indices of vertices to pin (don't move).</summary>
		public int[] Pinned { get; set; } = Array.Empty<int>();
		public int Iterations { get; set; } = 5;
		public float Strength { get; set; } = 0.5f;
	}

	public static PolyMesh Relax( PolyMesh source, Options options = null )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		options ??= new Options();
		if ( options.Iterations < 0 ) throw new ArgumentException( "iterations must be >= 0" );

		var outMesh = new PolyMesh();
		outMesh.Positions.AddRange( source.Positions );
		outMesh.Faces.AddRange( source.Faces );
		outMesh.Skin = source.Skin;
		outMesh.VertexColors = source.VertexColors;
		// UVs are per-face corner; relax by averaging neighboring corner UVs per vertex.
		// Build per-vertex list of (face, corner) references.
		var outFaces = new Face[ source.Faces.Count ];
		for ( int fi = 0; fi < source.Faces.Count; fi++ )
			outFaces[ fi ] = new Face( (int[])source.Faces[ fi ].Indices.Clone(), (Vec2[])source.Faces[ fi ].UVs.Clone(), source.Faces[ fi ].Material );

		var pinned = new bool[ source.Positions.Count ];
		foreach ( var p in options.Pinned ) if ( p >= 0 && p < pinned.Length ) pinned[ p ] = true;

		for ( int it = 0; it < options.Iterations; it++ )
		{
			// accumulate neighbor UV sums per vertex
			var sumX = new float[ source.Positions.Count ];
			var sumY = new float[ source.Positions.Count ];
			var cnt = new int[ source.Positions.Count ];
			for ( int fi = 0; fi < outFaces.Length; fi++ )
			{
				var f = outFaces[ fi ];
				for ( int c = 0; c < f.Count; c++ )
				{
					int v = f.Indices[ c ];
					sumX[ v ] += f.UVs[ c ].x;
					sumY[ v ] += f.UVs[ c ].y;
					cnt[ v ]++;
					// neighbor prev/next corners
					int prev = ( c + f.Count - 1 ) % f.Count;
					int next = ( c + 1 ) % f.Count;
					int vp = f.Indices[ prev ];
					int vn = f.Indices[ next ];
					sumX[ v ] += f.UVs[ prev ].x + f.UVs[ next ].x;
					sumY[ v ] += f.UVs[ prev ].y + f.UVs[ next ].y;
					cnt[ v ] += 2;
					// (avoid unused)
					_ = vp; _ = vn;
				}
			}
			// apply
			for ( int fi = 0; fi < outFaces.Length; fi++ )
			{
				var f = outFaces[ fi ];
				for ( int c = 0; c < f.Count; c++ )
				{
					int v = f.Indices[ c ];
					if ( pinned[ v ] ) continue;
					if ( cnt[ v ] == 0 ) continue;
					float ax = sumX[ v ] / cnt[ v ];
					float ay = sumY[ v ] / cnt[ v ];
					f.UVs[ c ] = new Vec2(
						f.UVs[ c ].x + ( ax - f.UVs[ c ].x ) * options.Strength,
						f.UVs[ c ].y + ( ay - f.UVs[ c ].y ) * options.Strength );
				}
			}
		}
		outMesh.Faces.Clear();
		foreach ( var f in outFaces ) outMesh.Faces.Add( f );
		return outMesh;
	}

	// --- manual island edits -----------------------------------------------------------------
	//
	// UvEditService used to offer only Relax, which is not a saved UV editor: a Relax runs and
	// its result is whatever the mesh holds afterwards. Island edits are the saved half. An edit
	// is a <see cref="ManualUVIslandEdit"/> record (body id + island key + transform) that the
	// document keeps and the rebuild reapplies; these mesh-level operations are the same
	// transform, applied to one mesh, with the malformed cases reported instead of applied.
	// Geometry never moves here: only per-corner UVs change, and only on the named island.
	// Seams are honoured because islands are cut at seams (<see cref="UVIslands.Find"/>), so an
	// edit moves exactly one island and never drags its neighbour across a seam. Pins belong to
	// Relax (see <see cref="Options.Pinned"/>); an island edit does not move pinned corners
	// either, because it moves whole islands, never individual vertices.

	/// <summary>Key for the island <paramref name="faces"/> belong to, stable while the topology
	/// holds. The same key a stored edit carries; see <see cref="ManualUVIslandEdit.Key"/>.</summary>
	public static string IslandKey( PolyMesh mesh, IEnumerable<int> faces ) => ManualUVIslandEdit.Key( mesh, faces );

	/// <summary>Faces of the island a stored key names, or null when no current island matches.
	/// A null is the stale-topology case: the mesh changed under the edit, so the edit must be
	/// dropped or re-keyed, never applied to whatever island happens to sit there now.</summary>
	public static List<int> FindIslandFaces( PolyMesh mesh, string faceKey )
	{
		if ( mesh is null || string.IsNullOrWhiteSpace( faceKey ) ) return null;
		var layout = UVIslands.Find( mesh );
		return layout.Islands.FirstOrDefault( island => ManualUVIslandEdit.Key( mesh, island ) == faceKey );
	}

	/// <summary>Why <paramref name="edit"/> cannot be applied to <paramref name="mesh"/>, or
	/// null when it can. A negative scale is allowed (a deliberate mirror); a zero scale, a
	/// non-finite number, a missing body id or island key, and a key no island answers to are
	/// malformed and reported here instead of silently doing nothing or collapsing UVs.</summary>
	public static string ValidateIslandEdit( PolyMesh mesh, ManualUVIslandEdit edit )
	{
		if ( mesh is null ) return "mesh is null";
		if ( edit is null ) return "edit is null";
		if ( string.IsNullOrWhiteSpace( edit.BodyId ) ) return "edit has no body id";
		if ( string.IsNullOrWhiteSpace( edit.FaceKey ) ) return "edit has no island key";
		if ( !float.IsFinite( edit.Offset.x ) || !float.IsFinite( edit.Offset.y ) ) return "offset is not finite";
		if ( !float.IsFinite( edit.RotationDegrees ) ) return "rotation is not finite";
		if ( !float.IsFinite( edit.Scale.x ) || !float.IsFinite( edit.Scale.y ) ) return "scale is not finite";
		if ( edit.Scale.x == 0f || edit.Scale.y == 0f ) return "scale collapses the island to a line";
		if ( FindIslandFaces( mesh, edit.FaceKey ) is null ) return "no island matches the edit's key (topology changed)";
		return null;
	}

	/// <summary>Apply one stored island transform to <paramref name="mesh"/> in place, through
	/// the same <see cref="ManualUVIslandEdit.Apply"/> the studio rebuild uses (the mesh rides
	/// in a throwaway <see cref="Body"/> so there is exactly one transform implementation).
	/// Returns false and leaves the mesh untouched when the edit is malformed; the error string
	/// says why. Applying twice is a no-op the second time only via the rebuild's
	/// already-applied set; this mesh-level call applies once per call, so callers that re-run
	/// it must track that themselves.</summary>
	public static bool TryApplyIslandEdit( PolyMesh mesh, ManualUVIslandEdit edit, out string error )
	{
		error = ValidateIslandEdit( mesh, edit );
		if ( error is not null ) return false;
		var body = new Body( edit.BodyId, edit.BodyId, mesh );
		ManualUVIslandEdit.Apply( new[] { body }, new[] { edit } );
		return true;
	}
}
