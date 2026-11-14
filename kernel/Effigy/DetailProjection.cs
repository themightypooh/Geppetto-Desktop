using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Projection: sample high-poly detail onto a decimated or retopo'd mesh.
///
/// THE OPERATION THE TICKET ASKED FOR. Decimation throws triangles away; the detail that
/// lived in them — the bump of a brow, the crease of a knuckle — has to land somewhere, or
/// the low mesh is a smooth ghost of the sculpt. This samples it, per low vertex, off the
/// high mesh: how far the high surface sits from the low vertex, along the low vertex's
/// normal, and which way the high surface faces there. That pair — displacement plus high
/// normal — is the detail, in the form a baker consumes: displacement becomes a height map,
/// the normal delta becomes a normal map, and the low mesh keeps its shape.
///
/// CLOSEST POINT, NOT A RAY. Each low vertex asks the high mesh's BVH for its nearest
/// surface point (<see cref="MeshBVH.NearestSurface"/>), not for a raycast along its normal.
/// A raycast misses wherever the low surface tilts away from the high one — a decimated
/// cheek curving off a sculpted brow — and a miss is a hole in the detail. The nearest
/// point always exists, and the signed distance along the normal says which side the high
/// surface is on. The cage-offset raycast variant belongs to the map baker
/// (<see cref="NormalBake"/>, which has a cage); this is the per-vertex sample, and it
/// refuses to invent data where there is none: a vertex farther than
/// <see cref="Options.MaxDistance"/> from the high surface is a miss, reported, not
/// silently zeroed.
///
/// WHAT IT IS NOT. It does not move the low mesh — that is <see cref="Snap"/>, a separate
/// call, because sampling and moving are different decisions and a caller that only wants
/// the numbers should not have to move anything to get them. It does not bake maps — the
/// baker reads these samples (or re-rays, its choice). It does not fix a bad decimation:
/// if the projection error is large, the decimation was too aggressive, and the honest
/// answer is a bigger budget, not a cleverer projection.
///
/// SIGNED DISPLACEMENT. Positive means the high surface sits outside the low surface
/// along the low vertex's normal — the low mesh cut a corner and the detail bulges out.
/// Negative means the high surface sits inside — the low mesh overshoots. A low mesh
/// built by scaling the high one down reads all-positive at the scale difference, which
/// is the fixture that proves the sign.
/// </summary>
public static class DetailProjection
{
	/// <summary>How far a low vertex may reach for the high surface.</summary>
	public sealed class Options
	{
		/// <summary>
		/// Search radius in model units. Zero means "auto": 5% of the high mesh's bounds
		/// diagonal, which comfortably covers any honest decimation — a low surface farther
		/// than that from its source is not a decimation, it is a different mesh.
		/// </summary>
		public float MaxDistance;

		/// <summary>Fraction of the high mesh's bounds diagonal used when MaxDistance is zero.</summary>
		public float AutoDistanceFraction = 0.05f;
	}

	/// <summary>Per-vertex detail sampled off the high mesh, plus the error summary.</summary>
	public sealed class Result
	{
		/// <summary>Closest high-surface point per low vertex; the vertex itself on a miss.</summary>
		public Vec3[] HitPoint;

		/// <summary>High-surface normal at the hit; the low vertex's own normal on a miss.</summary>
		public Vec3[] HighNormal;

		/// <summary>Signed distance from the low vertex to the high surface, along the low normal.</summary>
		public float[] Displacement;

		/// <summary>True where the high surface was found within range.</summary>
		public bool[] Hit;

		/// <summary>Vertices that found the high surface.</summary>
		public int Hits;

		/// <summary>Vertices that did not.</summary>
		public int Misses;

		/// <summary>Fraction of vertices that hit.</summary>
		public float HitRate => Hits + Misses == 0 ? 0f : Hits / (float)( Hits + Misses );

		/// <summary>Mean absolute displacement over the hits.</summary>
		public float MeanAbsDisplacement;

		/// <summary>Largest absolute displacement over the hits.</summary>
		public float MaxAbsDisplacement;

		/// <summary>Human-readable notes, including a miss count when there is one.</summary>
		public List<string> Notes = new();
	}

	/// <summary>
	/// Sample the high mesh's detail onto every vertex of the low mesh. Neither mesh is
	/// modified. A vertex that misses keeps its own position as its hit point and its own
	/// normal as its high normal, with displacement zero — flagged in <see cref="Result.Hit"/>,
	/// never silently filled with a neighbour's answer.
	/// </summary>
	public static Result Project( PolyMesh high, PolyMesh low, Options options = null )
	{
		if ( high is null )
			throw new ArgumentNullException( nameof( high ) );
		if ( low is null )
			throw new ArgumentNullException( nameof( low ) );
		if ( high.FaceCount == 0 )
			throw new ArgumentException( "projection needs a high mesh with faces", nameof( high ) );
		if ( low.VertexCount == 0 )
			throw new ArgumentException( "projection needs a low mesh with vertices", nameof( low ) );

		options ??= new Options();
		var maxDistance = options.MaxDistance;

		if ( maxDistance <= 0f )
		{
			if ( !float.IsFinite( options.AutoDistanceFraction ) || options.AutoDistanceFraction <= 0f || options.AutoDistanceFraction > 1f )
				throw new ArgumentOutOfRangeException( nameof( options ), "AutoDistanceFraction must be a fraction in (0, 1]" );

			maxDistance = high.BoundsDiagonal * options.AutoDistanceFraction;
		}

		if ( !float.IsFinite( maxDistance ) || maxDistance <= 0f )
			throw new ArgumentOutOfRangeException( nameof( options ), "MaxDistance must be positive" );

		var bvh = MeshBVH.Build( high );
		var normals = low.ComputeVertexNormals();
		var count = low.VertexCount;
		var result = new Result
		{
			HitPoint = new Vec3[count],
			HighNormal = new Vec3[count],
			Displacement = new float[count],
			Hit = new bool[count],
		};
		var absSum = 0.0;

		for ( var i = 0; i < count; i++ )
		{
			var p = low.Positions[i];
			var n = normals[i];
			var hit = bvh.NearestSurface( high, p, maxDistance );

			if ( hit is null )
			{
				result.HitPoint[i] = p;
				result.HighNormal[i] = n;
				result.Displacement[i] = 0f;
				result.Hit[i] = false;
				result.Misses++;
				continue;
			}

			var surface = hit.Value;
			result.HitPoint[i] = surface.Point;
			result.HighNormal[i] = surface.Normal;
			result.Displacement[i] = Vec3.Dot( surface.Point - p, n );
			result.Hit[i] = true;
			result.Hits++;

			var abs = MathF.Abs( result.Displacement[i] );
			absSum += abs;

			if ( abs > result.MaxAbsDisplacement )
				result.MaxAbsDisplacement = abs;
		}

		result.MeanAbsDisplacement = result.Hits > 0 ? (float)( absSum / result.Hits ) : 0f;

		if ( result.Misses > 0 )
			result.Notes.Add( $"{result.Misses} of {count} vertices missed the high mesh within {maxDistance} — displacement there is unset, not zero" );

		return result;
	}

	/// <summary>
	/// Move the low mesh onto the high surface: a new mesh whose hit vertices sit exactly
	/// at their sampled hit points, missed vertices untouched. The source mesh is never
	/// modified. This is the "project" half of the ticket made concrete — sample first with
	/// <see cref="Project"/>, then snap when the numbers say the projection is sound.
	/// </summary>
	public static PolyMesh Snap( PolyMesh low, Result projection )
	{
		if ( low is null )
			throw new ArgumentNullException( nameof( low ) );
		if ( projection is null )
			throw new ArgumentNullException( nameof( projection ) );
		if ( projection.HitPoint is null || projection.HitPoint.Length != low.VertexCount
			|| projection.Hit is null || projection.Hit.Length != low.VertexCount )
			throw new ArgumentException( "projection does not match this mesh — project onto this mesh first", nameof( projection ) );

		var snapped = low.Clone();

		for ( var i = 0; i < low.VertexCount; i++ )
		{
			if ( projection.Hit[i] )
				snapped.Positions[i] = projection.HitPoint[i];
		}

		snapped.InvalidateVertexCache();
		return snapped;
	}

	/// <summary>
	/// The ticket's projection check in one call: the mean and worst absolute displacement
	/// of the low mesh against the high one, and the hit rate. A sound projection has a hit
	/// rate of 1 and a worst displacement inside the fixture's tolerance.
	/// </summary>
	public static (float MeanAbs, float MaxAbs, float HitRate) ProjectionError( PolyMesh high, PolyMesh low, Options options = null )
	{
		var result = Project( high, low, options );
		return (result.MeanAbsDisplacement, result.MaxAbsDisplacement, result.HitRate);
	}
}
