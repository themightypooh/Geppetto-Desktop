using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Bounding-volume hierarchy over a PolyMesh for spatial queries and clash detection.
/// </summary>
public sealed class BVH
{
	private readonly PolyMesh _mesh;
	private readonly MeshBVH _inner;

	public BVH( PolyMesh mesh )
	{
		_mesh = mesh ?? throw new ArgumentNullException( nameof( mesh ) );
		_inner = MeshBVH.Build( mesh );
	}

	public PolyMesh Mesh => _mesh;

	public MeshHit? Raycast( Vec3 origin, Vec3 direction ) => _inner.Raycast( _mesh, origin, direction );

	public List<int> QuerySphere( Vec3 center, float radius )
	{
		var results = new List<int>();
		_inner.VerticesInRadius( _mesh, center, radius, results );
		return results;
	}

	public List<int> QueryFacesInRadius( Vec3 center, float radius )
	{
		var results = new List<int>();
		_inner.FacesInRadius( _mesh, center, radius, results );
		return results;
	}

	public void Refit() => _inner.Refit( _mesh );
}