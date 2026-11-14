#nullable enable
using Effigy;
using Marionette.Desktop;
using System;
using System.Collections.Generic;

/// <summary>
/// Dynamic topology (godlike-dyntopo-dynamic-topology): the stroke-local half —
/// subdivide over-long edges under the brush, collapse short edges on Smooth,
/// a hard triangle cap, a BVH rebuild after every topology change, and the
/// sculpt-layer bake warning. Covers <see cref="DynTopoSession"/>,
/// <see cref="DynTopoOptions"/> and <see cref="DynTopoStrokeResult"/>.
///
/// The voxel half is NOT covered here: <see cref="DynTopoSession.VoxelRemesh"/>
/// is null until the OpenVDB board task assigns its backend, and the only
/// voxel check is that <see cref="DynTopoSession.ApplyVoxelRemesh"/> refuses
/// honestly until then. Latency (<16 ms at 1M faces) and 1M-face undo timing
/// are measurements for the final integrator — these checks prove the
/// behaviour the latency depends on (locality, cap, BVH rebuild), not the
/// timings themselves.
/// NOT registered in TestRegistry.cs yet — the final integrator adds the one line.
/// </summary>
internal static class DynTopoTests
{
	static PolyMesh Grid( int nx, int nz, float size, bool quads )
	{
		var mesh = new PolyMesh();
		var stepX = size / nx;
		var stepY = size / nz;
		for ( var j = 0; j <= nz; j++ )
			for ( var i = 0; i <= nx; i++ )
				mesh.AddVertex( new Vec3( -size / 2 + i * stepX, -size / 2 + j * stepY, 0 ) );
		for ( var j = 0; j < nz; j++ )
			for ( var i = 0; i < nx; i++ )
			{
				var a = j * ( nx + 1 ) + i;
				var b = a + 1;
				var c = a + nx + 2;
				var d = a + nx + 1;
				if ( quads )
					mesh.AddFace( new[] { a, b, c, d } );
				else
				{
					mesh.AddFace( new[] { a, b, c } );
					mesh.AddFace( new[] { a, c, d } );
				}
			}
		return mesh;
	}

	static float MaxEdgeInRadius( PolyMesh mesh, Vec3 center, float radius )
	{
		var max = 0f;
		var seen = new HashSet<EdgeKey>();
		foreach ( var f in mesh.Faces )
		{
			if ( ( mesh.FaceCentroid( f ) - center ).Length > radius ) continue;
			for ( var k = 0; k < f.Count; k++ )
			{
				var key = new EdgeKey( f.Indices[k], f.Indices[( k + 1 ) % f.Count] );
				if ( !seen.Add( key ) ) continue;
				var len = ( mesh.Positions[key.A] - mesh.Positions[key.B] ).Length;
				if ( len > max ) max = len;
			}
		}
		return max;
	}

	static float MinEdgeInRadius( PolyMesh mesh, Vec3 center, float radius )
	{
		var min = float.MaxValue;
		var seen = new HashSet<EdgeKey>();
		foreach ( var f in mesh.Faces )
		{
			if ( ( mesh.FaceCentroid( f ) - center ).Length > radius ) continue;
			for ( var k = 0; k < f.Count; k++ )
			{
				var key = new EdgeKey( f.Indices[k], f.Indices[( k + 1 ) % f.Count] );
				if ( !seen.Add( key ) ) continue;
				var len = ( mesh.Positions[key.A] - mesh.Positions[key.B] ).Length;
				if ( len < min ) min = len;
			}
		}
		return min;
	}

	static void RequireValid( PolyMesh mesh, string what )
	{
		var v = MeshValidator.Validate( mesh );
		if ( !v.IsValid )
			throw new Exception( $"{what}: mesh invalid: {string.Join( "; ", v.Errors )}" );
		if ( v.NonManifoldEdges != 0 )
			throw new Exception( $"{what}: {v.NonManifoldEdges} non-manifold edges" );
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "dyntopo", name, test );

		void Require( bool condition, string message )
		{
			if ( !condition ) throw new Exception( message );
		}

		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, got silent success" );
		}

		Check( "subdivide adds triangles only under the brush", () =>
		{
			var mesh = Grid( 8, 8, 2, false );
			var before = new List<Vec3>( mesh.Positions ).ToArray();
			var eulerBefore = MeshValidator.EulerCharacteristic( mesh );
			var session = new DynTopoSession( mesh, new DynTopoOptions { DetailSize = 0.1f } );
			var r = session.ApplyStroke( new Vec3( 0, 0, 0 ), 0.5f );
			Require( !r.Refused && !r.Capped, "stroke must apply cleanly" );
			Require( r.FacesAdded > 0, "stroke must add faces" );
			Require( r.EdgesSplit > 0, "stroke must split edges" );
			Require( session.Mesh.FaceCount == r.FacesAfter, "face count must match the result" );
			for ( var i = 0; i < before.Length; i++ )
				if ( ( before[i] - new Vec3( 0, 0, 0 ) ).Length > 1.1f )
					Require( ( session.Mesh.Positions[i] - before[i] ).Length == 0f, $"vertex {i} outside the brush moved" );
			var max = MaxEdgeInRadius( session.Mesh, new Vec3( 0, 0, 0 ), 0.5f );
			Require( max <= 0.1f * 1.5f + 1e-4f, $"edges under the brush must be at detail size, max {max}" );
			Require( MeshValidator.EulerCharacteristic( session.Mesh ) == eulerBefore, "subdivision must not change the Euler characteristic" );
			RequireValid( session.Mesh, "subdivided grid" );
		} );

		Check( "detail size changes the tessellation density", () =>
		{
			var coarse = Grid( 8, 8, 2, false );
			var sc = new DynTopoSession( coarse, new DynTopoOptions { DetailSize = 0.4f } );
			var rc = sc.ApplyStroke( new Vec3( 0, 0, 0 ), 0.8f );
			var fine = Grid( 8, 8, 2, false );
			var sf = new DynTopoSession( fine, new DynTopoOptions { DetailSize = 0.1f } );
			var rf = sf.ApplyStroke( new Vec3( 0, 0, 0 ), 0.8f );
			Require( rc.FacesAdded == 0, "coarse detail must add nothing on a 0.25 grid" );
			Require( rf.FacesAdded > rc.FacesAdded, $"finer detail must add more faces: {rf.FacesAdded} vs {rc.FacesAdded}" );
		} );

		Check( "cap is enforced", () =>
		{
			var mesh = Grid( 8, 8, 2, false );
			var session = new DynTopoSession( mesh, new DynTopoOptions { DetailSize = 0.05f, MaxTriangles = 130 } );
			var r = session.ApplyStroke( new Vec3( 0, 0, 0 ), 1.0f );
			Require( r.Capped, "stroke must report capped" );
			Require( session.TriangleCount <= 130, $"triangle count {session.TriangleCount} must not exceed the cap" );
			Require( r.TrianglesAfter <= 130, "result must report the capped count" );
			RequireValid( session.Mesh, "capped mesh" );
		} );

		Check( "collapse removes short edges on Smooth", () =>
		{
			var mesh = Grid( 6, 6, 2, false );
			var a = 1 * ( 6 + 1 ) + 3;
			var b = a + 1;
			mesh.Positions[b] = mesh.Positions[a] + new Vec3( 0.0001f, 0, 0 );
			var eulerBefore = MeshValidator.EulerCharacteristic( mesh );
			var session = new DynTopoSession( mesh, new DynTopoOptions { DetailSize = 0.1f } );
			var r = session.ApplySmooth( mesh.Positions[a], 0.6f );
			Require( r.EdgesCollapsed > 0, "short edges must collapse" );
			Require( r.FacesAfter < r.FacesBefore, "collapse must remove faces" );
			Require( MeshValidator.EulerCharacteristic( session.Mesh ) == eulerBefore, "collapse must not change the Euler characteristic" );
			var min = MinEdgeInRadius( session.Mesh, mesh.Positions[a], 0.6f );
			Require( min >= 0.1f * 0.5f - 1e-4f, $"no short edge may survive in the region, min {min}" );
			RequireValid( session.Mesh, "collapsed mesh" );
		} );

		Check( "decimate to target", () =>
		{
			var mesh = Grid( 8, 8, 2, false );
			var session = new DynTopoSession( mesh );
			var result = session.DecimateToTarget( 64 );
			Require( result.ToTriangles <= 64 && result.ToTriangles >= 4, $"decimation must land at or under target, got {result.ToTriangles}" );
			Require( session.TriangleCount == result.ToTriangles, "session count must match the decimation result" );
			RequireValid( session.Mesh, "decimated mesh" );
		} );

		Check( "bvh rebuild keeps picking correct", () =>
		{
			var mesh = Grid( 8, 8, 2, false );
			var session = new DynTopoSession( mesh, new DynTopoOptions { DetailSize = 0.1f } );
			var r = session.ApplyStroke( new Vec3( 0, 0, 0 ), 0.6f );
			Require( r.BvhRebuilt, "topology change must rebuild the BVH" );
			var hit = session.Bvh.Raycast( session.Mesh, new Vec3( 0.1f, 0.1f, 5 ), new Vec3( 0, 0, -1 ) );
			Require( hit is not null, "raycast must hit the subdivided surface" );
			Require( MathF.Abs( hit.Value.Point.z ) < 1e-4f, $"raycast must hit the grid plane, got z={hit.Value.Point.z}" );
			Require( hit.Value.FaceIndex >= 0 && hit.Value.FaceIndex < session.Mesh.FaceCount, "hit face must exist" );
			var near = session.Bvh.NearestSurface( session.Mesh, new Vec3( 0.1f, 0.1f, 0.5f ), 1.0f );
			Require( near is not null && MathF.Abs( near.Value.Distance - 0.5f ) < 1e-4f, $"nearest surface must be 0.5 away, got {near?.Distance}" );
		} );

		Check( "sculpt-layer bake warning refuses the stroke", () =>
		{
			var mesh = Grid( 4, 4, 2, false );
			var session = new DynTopoSession( mesh, new DynTopoOptions { DetailSize = 0.1f } );
			var stack = new SculptLayerStack( 0, mesh.VertexCount, new SculptLayer( new Vec3[mesh.VertexCount] ) );
			var deltas = new Vec3[mesh.VertexCount];
			deltas[12] = new Vec3( 0, 0, 0.1f );
			stack.Add( "bump", new SculptLayer( deltas ) );
			Require( stack.HasUnbakedDetail, "stack must report unbaked detail" );
			var facesBefore = session.Mesh.FaceCount;
			var refused = session.ApplyStroke( new Vec3( 0, 0, 0 ), 0.5f, null, stack );
			Require( refused.Refused, "stroke must refuse with unbaked layers" );
			Require( refused.Warning is not null && refused.Warning.Contains( "Bake" ), "refusal must carry the bake warning" );
			Require( session.Mesh.FaceCount == facesBefore, "refused stroke must not change the mesh" );
			stack.BakeAll();
			var applied = session.ApplyStroke( new Vec3( 0, 0, 0 ), 0.5f, null, stack );
			Require( !applied.Refused && applied.FacesAdded > 0, "stroke must apply after bake-all" );
		} );

		Check( "explicit override proceeds with unbaked layers", () =>
		{
			var mesh = Grid( 4, 4, 2, false );
			var session = new DynTopoSession( mesh, new DynTopoOptions { DetailSize = 0.1f, AllowUnbakedLayers = true } );
			var stack = new SculptLayerStack( 0, mesh.VertexCount, new SculptLayer( new Vec3[mesh.VertexCount] ) );
			var deltas = new Vec3[mesh.VertexCount];
			deltas[12] = new Vec3( 0, 0, 0.1f );
			stack.Add( "bump", new SculptLayer( deltas ) );
			var r = session.ApplyStroke( new Vec3( 0, 0, 0 ), 0.5f, null, stack );
			Require( !r.Refused && r.FacesAdded > 0, "override must let the stroke apply" );
			Require( r.Warning is not null, "override must still report the warning" );
		} );

		Check( "snapshot and restore undo a stroke", () =>
		{
			var mesh = Grid( 8, 8, 2, false );
			var session = new DynTopoSession( mesh, new DynTopoOptions { DetailSize = 0.1f } );
			var before = session.Snapshot();
			var r = session.ApplyStroke( new Vec3( 0, 0, 0 ), 0.6f );
			Require( r.FacesAdded > 0, "stroke must change the mesh" );
			session.Restore( before );
			Require( session.Mesh.FaceCount == before.FaceCount, "restore must return the face count" );
			Require( session.Mesh.VertexCount == before.VertexCount, "restore must return the vertex count" );
			for ( var i = 0; i < before.VertexCount; i++ )
				Require( ( session.Mesh.Positions[i] - before.Positions[i] ).Length == 0f, $"restore must return vertex {i} exactly" );
		} );

		Check( "quads fan-split under the brush", () =>
		{
			var mesh = Grid( 6, 6, 2, true );
			var eulerBefore = MeshValidator.EulerCharacteristic( mesh );
			var session = new DynTopoSession( mesh, new DynTopoOptions { DetailSize = 0.1f } );
			var r = session.ApplyStroke( new Vec3( 0, 0, 0 ), 0.6f );
			Require( r.FacesAdded > 0, "quad stroke must add faces" );
			foreach ( var f in session.Mesh.Faces )
				if ( ( session.Mesh.FaceCentroid( f ) - new Vec3( 0, 0, 0 ) ).Length <= 0.6f )
					Require( f.Count == 3, "faces under the brush must be triangles after the stroke" );
			Require( MeshValidator.EulerCharacteristic( session.Mesh ) == eulerBefore, "quad fan-split must not change the Euler characteristic" );
			RequireValid( session.Mesh, "subdivided quad mesh" );
		} );

		Check( "side-data meshes are refused, not corrupted", () =>
		{
			var mesh = Grid( 4, 4, 2, false );
			mesh.VertexColors = new Vec4[mesh.VertexCount];
			for ( var i = 0; i < mesh.VertexColors.Length; i++ ) mesh.VertexColors[i] = new Vec4( 1, 0, 0, 1 );
			Throws<InvalidOperationException>( () => _ = new DynTopoSession( mesh ), "coloured mesh must be refused" );
		} );

		Check( "voxel seam refuses honestly without a backend", () =>
		{
			var mesh = Grid( 4, 4, 2, false );
			var session = new DynTopoSession( mesh );
			Throws<InvalidOperationException>( () => _ = session.ApplyVoxelRemesh(), "voxel remesh without a backend must refuse" );
			session.VoxelRemesh = m => m;
			var back = session.ApplyVoxelRemesh();
			Require( object.ReferenceEquals( back, session.Mesh ), "identity voxel backend returns the mesh" );
		} );

		Check( "invalid inputs are refused", () =>
		{
			var mesh = Grid( 4, 4, 2, false );
			Throws<ArgumentOutOfRangeException>( () => _ = new DynTopoSession( mesh, new DynTopoOptions { DetailSize = 0f } ), "zero detail size" );
			Throws<ArgumentOutOfRangeException>( () => _ = new DynTopoSession( mesh, new DynTopoOptions { MaxTriangles = 2 } ), "cap below 4" );
			var session = new DynTopoSession( mesh );
			Throws<ArgumentOutOfRangeException>( () => _ = session.ApplyStroke( new Vec3( 0, 0, 0 ), 0f ), "zero radius" );
			Throws<ArgumentException>( () => _ = session.ApplyStroke( new Vec3( float.NaN, 0, 0 ), 0.5f ), "NaN center" );
			Throws<ArgumentNullException>( () => _ = new DynTopoSession( null ), "null mesh" );
		} );
	}
}
