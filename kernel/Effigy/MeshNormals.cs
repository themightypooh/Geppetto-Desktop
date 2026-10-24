using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Shared normal generation. Lives here rather than inside a writer because every export format
/// needs the same answer — OBJ and SMD differ in how they spell a normal, not in what it should
/// be. Two copies of this would eventually disagree, and the symptom would be a model that shades
/// differently depending on which format it was exported through.
/// </summary>
/// <summary>
/// How much each neighbouring face counts when its normal is averaged into a corner.
/// Area is the historical behaviour. Angle weights by the corner's own angle, so a face
/// that only touches a vertex at a point counts for less than one that wraps around it.
/// FaceWeighted (roadmap B2) weights by area squared times corner angle, so a large flat
/// panel dominates the shared vertex normal and the narrow chamfer strip next to it
/// absorbs the curvature instead of puffing the panel out.
/// </summary>
public enum NormalWeightMode
{
	Area,
	Angle,
	FaceWeighted,
}

public static class MeshNormals
{
	/// <summary>Faces meeting at a sharper angle than this keep a hard edge; anything softer gets
	/// averaged. Without this a box comes out with rounded-looking corners and a cylinder comes out
	/// faceted — one threshold fixes both.</summary>
	public const float DefaultSmoothingAngleDegrees = 40f;

	/// <summary>
	/// One normal per face corner, averaged only across faces that agree to within the threshold.
	///
	/// The naive version — one averaged normal per vertex — rounds off every hard edge, so a box
	/// renders like a pillow. The other naive version — one normal per face — facets every curve,
	/// so a 32-segment cylinder looks like a nut. Thresholding gets both right from the same code
	/// and means the parametric stage never has to hand-author smoothing groups.
	/// </summary>
	public static (int[][] CornerNormals, List<Vec3> Normals) ComputeCornerNormals( PolyMesh mesh, float angleDegrees )
		=> ComputeCornerNormals( mesh, angleDegrees, NormalWeightMode.Area );

	public static (int[][] CornerNormals, List<Vec3> Normals) ComputeCornerNormals( PolyMesh mesh, float angleDegrees, NormalWeightMode mode )
	{
		var cosLimit = MathF.Cos( angleDegrees * MathF.PI / 180f );
		var faceCount = mesh.FaceCount;
		var faceNormals = new Vec3[faceCount];
		var faceAreas = new float[faceCount];

		// ONE PASS FOR BOTH, rather than FaceNormal then FaceArea.
		//
		// PolyMesh.FaceArea needs the centroid and the normal to project onto, so it computes both
		// itself - which meant a face's Newell normal was computed twice and its centroid once
		// more on top, three walks of the corners where two will do.
		for ( var fi = 0; fi < faceCount; fi++ )
		{
			var f = mesh.Faces[fi];
			var n = mesh.FaceNormal( f );
			faceNormals[fi] = n;
			faceAreas[fi] = mesh.FaceArea( f, n );
		}

		// Corner angles, for Angle and FaceWeighted modes: the angle between the two
		// edges of the face meeting at each corner.
		var cornerAngles = new float[faceCount][];

		for ( var fi = 0; fi < faceCount; fi++ )
		{
			var f = mesh.Faces[fi];
			var angles = new float[f.Count];
			cornerAngles[fi] = angles;

			for ( var i = 0; i < f.Count; i++ )
			{
				var prev = mesh.Positions[f.Indices[(i + f.Count - 1) % f.Count]];
				var cur = mesh.Positions[f.Indices[i]];
				var next = mesh.Positions[f.Indices[(i + 1) % f.Count]];
				var e0 = (prev - cur).Normal;
				var e1 = (next - cur).Normal;
				angles[i] = MathF.Acos( Math.Clamp( Vec3.Dot( e0, e1 ), -1f, 1f ) );
			}
		}

		// Marked sharp edges split normals regardless of angleDegrees. The roadmap
		// names this set PolyMesh.SharpEdges; this tree does not have that member — the
		// marked-edge set it does have is PolyMesh.Seams (the same set CatmullClark
		// preserves as marked seams), so that is what splits here.
		var sharp = mesh.Seams;

		var vertexFaces = VertexFaces.Build( mesh );

		// Presized to the vertex count, which is what the answer comes to for a fully smooth mesh
		// and the right order of magnitude for any other. Both of these grew from empty otherwise,
		// and on a dense body that is a couple of dozen doublings - each one reallocating and,
		// for the dictionary, rehashing every entry so far.
		var normals = new List<Vec3>( mesh.VertexCount );
		var dedupe = new Dictionary<long, int>( mesh.VertexCount );
		var cornerNormals = new int[faceCount][];

		// THE ANSWER FOR A CORNER WHOSE WHOLE FAN AGREES, CACHED PER VERTEX.
		//
		// When every face meeting a vertex is inside the smoothing angle of the one being asked
		// about, the sum is the whole fan's area-weighted total - which does not depend on WHICH
		// face asked. So all four corners at an ordinary vertex of a smooth mesh compute the same
		// vector, normalise it, and look it up in the dedupe table four times over.
		//
		// That is the common case by a wide margin: a subdivided or sculpted body is smooth almost
		// everywhere, and the hard edges that break the rule are a thin seam through it. Caching
		// the interned index the first time a corner at this vertex sees a full fan cuts the
		// square roots and the dictionary probes to roughly one per vertex instead of one per
		// corner, and leaves every crease to be worked out corner by corner exactly as before.
		//
		// EXACT, NOT AN APPROXIMATION. The cache is only read when the scan just established that
		// this corner's fan is full too, so the vector it stands for is the one this corner would
		// have computed.
		var fullFanNormal = new int[mesh.VertexCount];
		Array.Fill( fullFanNormal, -1 );

		for ( var fi = 0; fi < faceCount; fi++ )
		{
			var f = mesh.Faces[fi];
			var corners = new int[f.Count];
			cornerNormals[fi] = corners;
			var own = faceNormals[fi];

			for ( var i = 0; i < f.Count; i++ )
			{
				var vertex = f.Indices[i];
				var neighbours = vertexFaces[vertex];
				var sum = Vec3.Zero;
				var taken = 0;

				for ( var k = 0; k < neighbours.Length; k++ )
				{
					var other = neighbours[k];

					if ( Vec3.Dot( own, faceNormals[other] ) < cosLimit )
						continue;

					// A sharp edge between this face and the neighbour is a hard split:
					// the two sides never average, however soft the angle threshold is.
					if ( other != fi && SharesSharpEdge( mesh.Faces[fi], mesh.Faces[other], vertex, sharp ) )
						continue;

					var angle = 0f;
					var of = mesh.Faces[other];

					for ( var ci = 0; ci < of.Count; ci++ )
					{
						if ( of.Indices[ci] == vertex )
						{
							angle = cornerAngles[other][ci];
							break;
						}
					}

					var weight = mode switch
					{
						NormalWeightMode.Angle => angle,
						NormalWeightMode.FaceWeighted => faceAreas[other] * faceAreas[other] * angle,
						_ => faceAreas[other],
					};

					sum += faceNormals[other] * weight;
					taken++;
				}

				var full = taken == neighbours.Length;

				if ( full && fullFanNormal[vertex] >= 0 )
				{
					corners[i] = fullFanNormal[vertex];
					continue;
				}

				var n = sum.Normal;

				// A degenerate cluster can cancel to zero; fall back to the face's own normal
				// rather than emitting (0,0,0), which some importers reject outright.
				if ( n.LengthSquared < 0.5f )
					n = own;

				var id = Intern( n, dedupe, normals );
				corners[i] = id;

				if ( full )
					fullFanNormal[vertex] = id;
			}
		}

		return (cornerNormals, normals);
	}

	static bool SharesSharpEdge( Face a, Face b, int vertex, HashSet<EdgeKey> sharp )
	{
		if ( sharp is null || sharp.Count == 0 )
			return false;

		foreach ( var w in a.Indices )
		{
			if ( w == vertex )
				continue;

			if ( !b.Indices.Contains( w ) )
				continue;

			if ( sharp.Contains( new EdgeKey( vertex, w ) ) )
				return true;
		}

		return false;
	}

	/// <summary>
	/// Index of this normal in the shared list, adding it if it is new. Quantised to 1e-4 so two
	/// corners that agree to within rounding share one entry - which is what keeps an exported
	/// normal list the size of the smooth groups rather than the size of the corner count.
	/// </summary>
	static int Intern( Vec3 n, Dictionary<long, int> dedupe, List<Vec3> normals )
	{
		var key = Quantise( n );

		if ( dedupe.TryGetValue( key, out var idx ) )
			return idx;

		idx = normals.Count;
		normals.Add( n );
		dedupe[key] = idx;
		return idx;
	}

	/// <summary>
	/// A normal's three rounded components packed into one long.
	///
	/// This was a Dictionary keyed on a (long, long, long) tuple, which is a 24-byte key hashed
	/// through ValueTuple's combiner on every one of the mesh's corners - just under a million
	/// lookups on a dense body, all of them on the viewport's rebuild path.
	///
	/// A unit normal's components are in [-1, 1], so rounding at 1e-4 lands in [-10000, 10000] and
	/// each fits in 21 bits with room to spare. Three of those is 63 bits, so the whole key is one
	/// long, hashed as one long. Same quantisation, same collision behaviour, no tuple.
	///
	/// Clamped rather than trusted: the caller normalises, but a NaN slipping through would
	/// otherwise shift into a bit pattern that aliases some unrelated normal, and a silently wrong
	/// shared normal is far harder to see than a clamped one.
	/// </summary>
	static long Quantise( Vec3 n )
	{
		return Pack( n.x ) | (Pack( n.y ) << 21) | (Pack( n.z ) << 42);

		static long Pack( float v )
		{
			var q = (long)MathF.Round( Math.Clamp( v, -1f, 1f ) * 1e4f );
			return (q + 1048576L) & 0x1FFFFF; // bias into 0..2^21-1
		}
	}
}
