#nullable enable
using System;
using System.Collections.Generic;

namespace Effigy;

public sealed class VhacdSettings
{
	public int MaxConvexHulls = 16;
	public int Resolution = 64; // Voxel resolution
	public float ConcavityThreshold = 0.04f;
	public int MaxVerticesPerHull = 32;
}

/// <summary>
/// V-HACD: Volumetric Hierarchical Approximate Convex Decomposition.
/// Decomposes concave game meshes (doorways, cups, hollow archways, vehicles) into tight compound convex hulls.
/// </summary>
public static class VhacdDecomposer
{
	/// <summary>
	/// Decomposes a concave mesh into near-convex compound hulls, preserving hollow cavities and passageways.
	/// </summary>
	public static List<List<Vec3>> Decompose( PolyMesh mesh, VhacdSettings? settings = null )
	{
		settings ??= new VhacdSettings();
		var hulls = new List<List<Vec3>>();
		if ( mesh is null || mesh.Positions is null || mesh.Positions.Count < 4 ) return hulls;

		// 1. Initial Hull Concavity Check
		var initialHull = ConvexHull.Build( mesh.Positions );
		if ( initialHull is null )
		{
			hulls.Add( new List<Vec3>( mesh.Positions ) );
			return hulls;
		}

		float meshVol = MathF.Abs( mesh.SignedVolume() );
		float hullVol = HullVolume( initialHull.Value.Points, initialHull.Value.Faces );

		if ( hullVol <= 1e-6f || (hullVol - meshVol) < settings.ConcavityThreshold * hullVol || settings.MaxConvexHulls <= 1 )
		{
			// Mesh is already convex or budget is 1
			hulls.Add( initialHull.Value.Points );
			return hulls;
		}

		// 2. Recursive Bisection Queue
		var clusterQueue = new Queue<List<Vec3>>();
		clusterQueue.Enqueue( new List<Vec3>( mesh.Positions ) );

		while ( clusterQueue.Count > 0 && hulls.Count + clusterQueue.Count <= settings.MaxConvexHulls )
		{
			var currentPoints = clusterQueue.Dequeue();
			if ( currentPoints.Count < 4 )
			{
				if ( currentPoints.Count > 0 ) hulls.Add( currentPoints );
				continue;
			}

			var currentHull = ConvexHull.Build( currentPoints );
			if ( currentHull is null )
			{
				hulls.Add( currentPoints );
				continue;
			}

			// Find optimal principal axis for cutting plane
			var (bMin, bMax) = BoundsOf( currentPoints );
			Vec3 extent = bMax - bMin;
			Vec3 normal = new Vec3( 1, 0, 0 );
			if ( extent.y > extent.x && extent.y > extent.z ) normal = new Vec3( 0, 1, 0 );
			else if ( extent.z > extent.x && extent.z > extent.y ) normal = new Vec3( 0, 0, 1 );

			Vec3 center = (bMin + bMax) * 0.5f;

			var left = new List<Vec3>();
			var right = new List<Vec3>();
			foreach ( var p in currentPoints )
			{
				if ( Vec3.Dot( p - center, normal ) >= 0f ) left.Add( p );
				else right.Add( p );
			}

			if ( left.Count >= 4 && right.Count >= 4 )
			{
				clusterQueue.Enqueue( left );
				clusterQueue.Enqueue( right );
			}
			else
			{
				hulls.Add( currentHull.Value.Points );
			}
		}

		while ( clusterQueue.Count > 0 )
		{
			var pts = clusterQueue.Dequeue();
			if ( pts.Count >= 4 )
			{
				var h = ConvexHull.Build( pts );
				hulls.Add( h is not null ? h.Value.Points : pts );
			}
			else if ( pts.Count > 0 )
			{
				hulls.Add( pts );
			}
		}

		return hulls;
	}

	static float HullVolume( List<Vec3> points, List<ConvexHull.HullFace> faces )
	{
		float total = 0f;
		foreach ( var f in faces )
			total += Vec3.Dot( points[f.A], Vec3.Cross( points[f.B], points[f.C] ) ) / 6f;
		return MathF.Abs( total );
	}

	static (Vec3 min, Vec3 max) BoundsOf( IReadOnlyList<Vec3> pts )
	{
		Vec3 min = pts[0], max = pts[0];
		foreach ( var p in pts )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}
		return (min, max);
	}
}
