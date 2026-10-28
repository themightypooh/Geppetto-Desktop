using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Blender-style SurfaceDeform / MeshDeform binding: an accessory mesh bound to a cage (or body)
/// mesh follows that mesh when it deforms — a pauldron riding a bending arm, a belt on a twisting
/// torso, a badge on a stretching chest.
///
/// TWO BINDINGS, DIFFERENT MATH, NO MYSTERY.
/// Surface (Blender's SurfaceDeform): every accessory vertex is projected onto the nearest cage
/// triangle and stored as barycentric weights plus a signed offset along that triangle's normal.
/// When the cage deforms, the vertex is rebuilt from the deformed triangle plus its offset along
/// the deformed normal — so a thing riding a surface stays glued to that surface, offset and all,
/// instead of sliding or sinking.
/// Cage (Blender's MeshDeform, simplified): every accessory vertex is bound to its k nearest cage
/// vertices with inverse-distance weights, and follows by the weighted average of those vertices'
/// displacements. This is what a simplified proxy cage wants: the accessory may sit INSIDE the
/// cage volume, far from any surface, where a surface projection would be meaningless. It is NOT
/// Blender's harmonic-coordinate MeshDeform (no volumetric solve); that limit is stated here, not
/// hidden, and the mode is still genuinely useful for layered accessories on proxy cages.
///
/// WHAT THIS IS NOT: LatticeCage (Effigy/Lattice.cs) deforms one mesh by its own control points,
/// and SkeletonCage builds a cage from a stick figure. Neither binds one mesh to another. This
/// file is the first binding between two meshes in the kernel, and it deliberately does not
/// touch SDF code (Implicit.cs belongs to the parallel SDF agent's lane).
/// </summary>
public enum CageBindingMode
{
	Surface,
	Cage,
}

/// <summary>One accessory vertex bound to a cage triangle (Surface mode).</summary>
public readonly struct CageBindPoint
{
	public readonly bool Bound;
	public readonly int FaceIndex;
	public readonly int A, B, C;
	public readonly float U, V, W;
	public readonly float Offset;
	public readonly Vec3 Normal;
	public readonly Vec3 Original;

	public CageBindPoint( bool bound, int faceIndex, int a, int b, int c, float u, float v, float w, float offset, Vec3 normal, Vec3 original )
	{
		Bound = bound;
		FaceIndex = faceIndex;
		A = a; B = b; C = c;
		U = u; V = v; W = w;
		Offset = offset;
		Normal = normal;
		Original = original;
	}

	public static CageBindPoint Unbound( Vec3 original ) => new( false, -1, -1, -1, -1, 0f, 0f, 0f, 0f, Vec3.Zero, original );
}

/// <summary>One accessory vertex bound to nearby cage vertices (Cage mode).</summary>
public readonly struct CagePoint
{
	public readonly bool Bound;
	public readonly int[] Indices;
	public readonly float[] Weights;
	public readonly Vec3 Original;

	public CagePoint( bool bound, int[] indices, float[] weights, Vec3 original )
	{
		Bound = bound;
		Indices = indices;
		Weights = weights;
		Original = original;
	}

	public static CagePoint Unbound( Vec3 original ) => new( false, Array.Empty<int>(), Array.Empty<float>(), original );
}

public sealed class CageBinding
{
	public CageBindingMode Mode { get; }
	public Vec3[] Original { get; }
	public CageBindPoint[] SurfacePoints { get; }
	public CagePoint[] CagePoints { get; }
	public Vec3[] BindCagePositions { get; }
	public int BindCageVertexCount { get; }
	public int BindCageFaceCount { get; }
	public int BoundCount { get; }
	public int UnboundCount { get; }
	public float MaxOffset { get; }

	CageBinding( CageBindingMode mode, Vec3[] original, CageBindPoint[] surface, CagePoint[] cagePoints, Vec3[] bindCage, int bound, int cageVerts, int cageFaces )
	{
		Mode = mode;
		Original = original;
		SurfacePoints = surface;
		CagePoints = cagePoints;
		BindCagePositions = bindCage;
		BindCageVertexCount = cageVerts;
		BindCageFaceCount = cageFaces;
		BoundCount = bound;
		UnboundCount = original.Length - bound;
		var max = 0f;
		if ( surface is not null )
			foreach ( var p in surface )
				if ( p.Bound && MathF.Abs( p.Offset ) > max ) max = MathF.Abs( p.Offset );
		MaxOffset = max;
	}

	/// <summary>Bind every accessory vertex to the nearest cage triangle, within Reach.</summary>
	public static CageBinding BindSurface( PolyMesh cage, PolyMesh accessory, float maxDistance )
	{
		if ( cage is null ) throw new ArgumentNullException( nameof( cage ) );
		if ( accessory is null ) throw new ArgumentNullException( nameof( accessory ) );
		if ( !float.IsFinite( maxDistance ) || maxDistance < 0 ) throw new ArgumentException( "Reach must be finite and nonnegative" );

		var original = accessory.Positions.ToArray();
		var points = new CageBindPoint[original.Length];
		var bound = 0;
		var tree = MeshBVH.Build( cage );

		for ( var i = 0; i < original.Length; i++ )
		{
			var p = original[i];
			var hit = tree.NearestSurface( cage, p, maxDistance );
			CageBindPoint? best = null;
			var bestDist = float.MaxValue;

			if ( hit is { } h && h.FaceIndex >= 0 && h.FaceIndex < cage.Faces.Count )
			{
				best = TryBindOnFace( cage, h.FaceIndex, p, out bestDist );
				// The BVH face should contain the closest point; if what we computed disagrees
				// with the BVH distance, do not trust the face index — scan every face instead.
				if ( best is null || MathF.Abs( bestDist - h.Distance ) > 1e-3f )
				{
					best = null;
					bestDist = float.MaxValue;
				}
			}

			if ( best is null )
			{
				for ( var f = 0; f < cage.Faces.Count; f++ )
				{
					var cand = TryBindOnFace( cage, f, p, out var d );
					if ( cand is { } c && d < bestDist )
					{
						best = c;
						bestDist = d;
					}
				}
				if ( best is null || bestDist > maxDistance )
					best = null;
			}

			if ( best is { } b )
			{
				points[i] = b;
				bound++;
			}
			else
			{
				points[i] = CageBindPoint.Unbound( p );
			}
		}

		return new CageBinding( CageBindingMode.Surface, original, points, null, null, bound, cage.VertexCount, cage.Faces.Count );
	}

	/// <summary>Bind every accessory vertex to its k nearest cage vertices (proxy-cage mode).</summary>
	public static CageBinding BindCage( PolyMesh cage, PolyMesh accessory, int neighbours = 4 )
	{
		if ( cage is null ) throw new ArgumentNullException( nameof( cage ) );
		if ( accessory is null ) throw new ArgumentNullException( nameof( accessory ) );
		if ( cage.VertexCount == 0 ) throw new ArgumentException( "The cage has no vertices" );
		if ( neighbours < 1 ) throw new ArgumentException( "Need at least one neighbour" );

		var original = accessory.Positions.ToArray();
		var bindCage = cage.Positions.ToArray();
		var k = Math.Min( neighbours, bindCage.Length );
		var points = new CagePoint[original.Length];
		var bound = 0;

		for ( var i = 0; i < original.Length; i++ )
		{
			var p = original[i];
			var order = new int[bindCage.Length];
			for ( var j = 0; j < order.Length; j++ ) order[j] = j;
			Array.Sort( order, ( a, b ) => (bindCage[a] - p).LengthSquared.CompareTo( (bindCage[b] - p).LengthSquared ) );

			var indices = new int[k];
			var weights = new float[k];
			var sum = 0f;
			for ( var j = 0; j < k; j++ )
			{
				indices[j] = order[j];
				weights[j] = 1f / (MathF.Sqrt( (bindCage[order[j]] - p).LengthSquared ) + 1e-3f);
				sum += weights[j];
			}
			for ( var j = 0; j < k; j++ ) weights[j] /= sum;

			points[i] = new CagePoint( true, indices, weights, p );
			bound++;
		}

		return new CageBinding( CageBindingMode.Cage, original, null, points, bindCage, bound, bindCage.Length, cage.Faces.Count );
	}

	/// <summary>Where every accessory vertex goes when the cage looks like this. The cage must have
	/// the same topology it had at bind time; rebind after topology edits, not after moves.</summary>
	public Vec3[] Apply( PolyMesh deformedCage )
	{
		if ( deformedCage is null ) throw new ArgumentNullException( nameof( deformedCage ) );
		if ( Mode == CageBindingMode.Surface )
		{
			if ( deformedCage.Positions.Count != BindCageVertexCount || deformedCage.Faces.Count != BindCageFaceCount ) throw new ArgumentException( "The deformed cage has different topology — rebind after topology edits" );
			var result = new Vec3[Original.Length];
			for ( var i = 0; i < Original.Length; i++ )
			{
				var pt = SurfacePoints[i];
				if ( !pt.Bound )
				{
					result[i] = Original[i];
					continue;
				}
				var a = deformedCage.Positions[pt.A];
				var b = deformedCage.Positions[pt.B];
				var c = deformedCage.Positions[pt.C];
				var n = Vec3.Cross( b - a, c - a );
				var normal = n.LengthSquared > 1e-12f ? n.Normal : pt.Normal;
				result[i] = a * pt.U + b * pt.V + c * pt.W + normal * pt.Offset;
			}
			return result;
		}
		else
		{
			if ( deformedCage.Positions.Count != BindCagePositions.Length ) throw new ArgumentException( "The deformed cage has a different vertex count — rebind after topology edits" );
			var result = new Vec3[Original.Length];
			for ( var i = 0; i < Original.Length; i++ )
			{
				var pt = CagePoints[i];
				if ( !pt.Bound )
				{
					result[i] = Original[i];
					continue;
				}
				var shift = Vec3.Zero;
				for ( var j = 0; j < pt.Indices.Length; j++ )
					shift += (deformedCage.Positions[pt.Indices[j]] - BindCagePositions[pt.Indices[j]]) * pt.Weights[j];
				result[i] = Original[i] + shift;
			}
			return result;
		}
	}

	/// <summary>Move the accessory mesh toward its bound positions by Strength (1 = all the way).</summary>
	public void ApplyTo( PolyMesh accessory, PolyMesh deformedCage, float strength )
	{
		if ( accessory is null ) throw new ArgumentNullException( nameof( accessory ) );
		if ( accessory.Positions.Count != Original.Length ) throw new ArgumentException( "This binding was made for a different accessory mesh" );
		var s = Math.Clamp( strength, 0f, 1f );
		var moved = Apply( deformedCage );
		for ( var i = 0; i < Original.Length; i++ )
			accessory.Positions[i] = Original[i] + (moved[i] - Original[i]) * s;
	}

	static CageBindPoint? TryBindOnFace( PolyMesh cage, int faceIndex, Vec3 p, out float bestDist )
	{
		bestDist = float.MaxValue;
		CageBindPoint? best = null;
		var face = cage.Faces[faceIndex];
		if ( face.Count < 3 ) return null;

		for ( var k = 1; k < face.Count - 1; k++ )
		{
			var ia = face.Indices[0];
			var ib = face.Indices[k];
			var ic = face.Indices[k + 1];
			var a = cage.Positions[ia];
			var b = cage.Positions[ib];
			var c = cage.Positions[ic];
			var n = Vec3.Cross( b - a, c - a );
			if ( n.LengthSquared < 1e-12f ) continue;
			var normal = n.Normal;
			var closest = ClosestPointTriangle( p, a, b, c, out var u, out var v, out var w );
			var d = (p - closest).Length;
			if ( d >= bestDist ) continue;
			bestDist = d;
			var offset = Vec3.Dot( p - closest, normal );
			best = new CageBindPoint( true, faceIndex, ia, ib, ic, u, v, w, offset, normal, p );
		}

		return best;
	}

	/// <summary>Closest point on a triangle, with barycentric weights (u for a, v for b, w for c).
	/// Ericson's Voronoi-region test, so the weights are exact on edges and corners too.</summary>
	static Vec3 ClosestPointTriangle( Vec3 p, Vec3 a, Vec3 b, Vec3 c, out float u, out float v, out float w )
	{
		var ab = b - a;
		var ac = c - a;
		var ap = p - a;
		var d1 = Vec3.Dot( ab, ap );
		var d2 = Vec3.Dot( ac, ap );
		if ( d1 <= 0f && d2 <= 0f ) { u = 1f; v = 0f; w = 0f; return a; }

		var bp = p - b;
		var d3 = Vec3.Dot( ab, bp );
		var d4 = Vec3.Dot( ac, bp );
		if ( d3 >= 0f && d4 <= d3 ) { u = 0f; v = 1f; w = 0f; return b; }

		var vc = d1 * d4 - d3 * d2;
		if ( vc <= 0f && d1 >= 0f && d3 <= 0f )
		{
			var t = d1 / (d1 - d3);
			u = 1f - t; v = t; w = 0f;
			return a + ab * t;
		}

		var cp = p - c;
		var d5 = Vec3.Dot( ab, cp );
		var d6 = Vec3.Dot( ac, cp );
		if ( d6 >= 0f && d5 <= d6 ) { u = 0f; v = 0f; w = 1f; return c; }

		var vb = d5 * d2 - d1 * d6;
		if ( vb <= 0f && d2 >= 0f && d6 <= 0f )
		{
			var t = d2 / (d2 - d6);
			u = 1f - t; v = 0f; w = t;
			return a + ac * t;
		}

		var va = d3 * d6 - d5 * d4;
		if ( va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f )
		{
			var t = (d4 - d3) / ((d4 - d3) + (d5 - d6));
			u = 0f; v = 1f - t; w = t;
			return b + (c - b) * t;
		}

		var denom = 1f / (va + vb + vc);
		v = vb * denom;
		w = vc * denom;
		u = 1f - v - w;
		return a + ab * v + ac * w;
	}
}
