// Tests for the virtualized viewport + standing perf harness (godlike Platform P1).
//
// Group "viewport-perf". Kernel-only and CPU-only: proxy LOD, frustum /
// cluster streaming, the GPU-subdivision table + provider slot, and the
// published targets. The host-side checks (profiler, perf suite, shader
// precompile, brush measurement, real-OrbitCamera frustum) live in
// ViewportPerfHostTests.cs — they need the Desktop host project and run in
// the final build's full suite.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Effigy;

internal static class ViewportPerfTests
{
	static void Require( bool condition, string message )
	{
		if ( !condition ) throw new Exception( message );
	}
	static void Near( float actual, float expected, string what, float tol = 1e-4f )
	{
		if ( Math.Abs( actual - expected ) > tol ) throw new Exception( $"{what}: expected {expected}, got {actual}" );
	}
	static void NearVec( Vec3 actual, Vec3 expected, string what, float tol = 1e-4f )
	{
		if ( (actual - expected).Length > tol ) throw new Exception( $"{what}: expected ({expected.x}, {expected.y}, {expected.z}), got ({actual.x}, {actual.y}, {actual.z})" );
	}

	/// <summary>A bumpy grid: n x n quads over [-size, size]^2, z = 0.3 sin(x) cos(y).
	/// Non-flat so decimation has a real error signal; closed enough to subdivide.</summary>
	static PolyMesh BumpyGrid( int n, float size, Vec3 offset )
	{
		var mesh = new PolyMesh();
		for ( var j = 0; j <= n; j++ )
			for ( var i = 0; i <= n; i++ )
			{
				var x = -size + 2f * size * i / n;
				var y = -size + 2f * size * j / n;
				mesh.AddVertex( new Vec3( x + offset.x, y + offset.y, 0.3f * MathF.Sin( x ) * MathF.Cos( y ) + offset.z ) );
			}
		for ( var j = 0; j < n; j++ )
			for ( var i = 0; i < n; i++ )
			{
				var a = j * (n + 1) + i;
				mesh.AddFace( new[] { a, a + 1, a + n + 2, a + n + 1 }, null );
			}
		return mesh;
	}

	/// <summary>A closed unit box: 8 vertices, 6 quad faces.</summary>
	static PolyMesh UnitBox()
	{
		var mesh = new PolyMesh();
		var p = new[]
		{
			new Vec3( -1, -1, -1 ), new Vec3( 1, -1, -1 ), new Vec3( 1, 1, -1 ), new Vec3( -1, 1, -1 ),
			new Vec3( -1, -1, 1 ), new Vec3( 1, -1, 1 ), new Vec3( 1, 1, 1 ), new Vec3( -1, 1, 1 ),
		};
		foreach ( var v in p ) mesh.AddVertex( v );
		mesh.AddFace( new[] { 0, 1, 2, 3 }, null ); // bottom
		mesh.AddFace( new[] { 4, 7, 6, 5 }, null ); // top
		mesh.AddFace( new[] { 0, 4, 5, 1 }, null ); // front
		mesh.AddFace( new[] { 1, 5, 6, 2 }, null ); // right
		mesh.AddFace( new[] { 2, 6, 7, 3 }, null ); // back
		mesh.AddFace( new[] { 3, 7, 4, 0 }, null ); // left
		return mesh;
	}

	static (PolyMesh Mesh, PolyMesh BlobA, PolyMesh BlobB, List<MeshCluster> Clusters) TwoBlobs()
	{
		// Two bumpy grids, 40 units apart: A at the origin, B at (40, 0, 0).
		// The merged mesh is what the partition check uses; the per-blob meshes
		// are what the visibility check uses, because clusters are built per
		// object in the real system (one cluster set per ViewportMesh/batch) and
		// a merged mesh's Morton chunking can straddle the gap between its parts.
		var blobA = BumpyGrid( 5, 3f, new Vec3( 0, 0, 0 ) );
		var blobB = BumpyGrid( 5, 3f, new Vec3( 40, 0, 0 ) );
		var mesh = new PolyMesh();
		Append( blobA );
		Append( blobB );
		void Append( PolyMesh part )
		{
			var baseIndex = mesh.VertexCount;
			foreach ( var v in part.Positions ) mesh.AddVertex( v );
			foreach ( var f in part.Faces )
				mesh.AddFace( f.Indices.Select( i => i + baseIndex ).ToArray(), null );
		}
		var clusters = MeshClusters.Build( mesh, targetTrianglesPerCluster: 8 );
		return (mesh, blobA, blobB, clusters);
	}

	/// <summary>Frustum for a camera looking at <paramref name="centre"/>,
	/// built from System.Numerics matrices only (no host types). The projection
	/// is adapted for OpenGL -1..1 clip depth exactly as
	/// OrbitCamera.ProjectionMatrix does (M33/M43 rewritten), so this verifies
	/// ViewFrustum.FromMatrix against the matrix shape the real camera produces.</summary>
	static ViewFrustum CameraFrustum( Vec3 centre, float radius )
	{
		var eye = new Vector3( centre.x - radius * 2.4f, centre.y - radius * 1.8f, centre.z + radius * 1.2f );
		var target = new Vector3( centre.x, centre.y, centre.z );
		var view = Matrix4x4.CreateLookAt( eye, target, Vector3.UnitZ );
		var near = 0.01f;
		var far = Math.Max( radius * 40f, 200f );
		var proj = Matrix4x4.CreatePerspectiveFieldOfView( 45f * MathF.PI / 180f, 1f, near, far );
		// OrbitCamera's OpenGL depth adaptation (-1..1 instead of 0..1).
		proj.M33 = -(far + near) / (far - near);
		proj.M43 = -2f * far * near / (far - near);
		var vp = view * proj;
		var m = new[]
		{
			vp.M11, vp.M12, vp.M13, vp.M14,
			vp.M21, vp.M22, vp.M23, vp.M24,
			vp.M31, vp.M32, vp.M33, vp.M34,
			vp.M41, vp.M42, vp.M43, vp.M44,
		};
		return ViewFrustum.FromMatrix( m );
	}

	/// <summary>Hand-built adapted view-projection matrix for a camera at
	/// <paramref name="eye"/> looking at <paramref name="target"/> (same
	/// adaptation as CameraFrustum), so the camera-convention check can assert
	/// on the eye/target directly.</summary>
	static (ViewFrustum Frustum, Vector3 Eye, Vector3 Target) AdaptedCamera( Vector3 eye, Vector3 target )
	{
		var view = Matrix4x4.CreateLookAt( eye, target, Vector3.UnitZ );
		var near = 0.01f;
		var far = 200f;
		var proj = Matrix4x4.CreatePerspectiveFieldOfView( 45f * MathF.PI / 180f, 1f, near, far );
		proj.M33 = -(far + near) / (far - near);
		proj.M43 = -2f * far * near / (far - near);
		var vp = view * proj;
		var m = new[]
		{
			vp.M11, vp.M12, vp.M13, vp.M14,
			vp.M21, vp.M22, vp.M23, vp.M24,
			vp.M31, vp.M32, vp.M33, vp.M34,
			vp.M41, vp.M42, vp.M43, vp.M44,
		};
		return (ViewFrustum.FromMatrix( m ), eye, target);
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "viewport-perf", "proxy LOD switches at the configured distance, with hysteresis", () =>
		{
			var mesh = BumpyGrid( 7, 3f, new Vec3( 0, 0, 0 ) );
			var obj = ProxyLod.ForObject( "grid", mesh, new Vec3( 0, 0, 0 ), 5f );
			var d1 = obj.Levels[1].SwitchDistance;
			var d2 = obj.Levels[2].SwitchDistance;
			var d3 = obj.Levels[3].SwitchDistance;
			Require( d1 > 0 && d2 > d1 && d3 > d2, $"switch distances should increase: {d1}, {d2}, {d3}" );
			Require( ProxyLod.SelectLevel( obj, d1 * 0.5f ) == 0, "well inside LOD1's distance stays LOD0" );
			obj.CurrentLevel = ProxyLod.SelectLevel( obj, d1 * 1.1f );
			Require( obj.CurrentLevel == 1, $"past LOD1's distance should be LOD1, got {obj.CurrentLevel}" );
			obj.CurrentLevel = ProxyLod.SelectLevel( obj, d2 * 1.1f );
			Require( obj.CurrentLevel == 2, $"past LOD2's distance should be LOD2, got {obj.CurrentLevel}" );
			obj.CurrentLevel = ProxyLod.SelectLevel( obj, d3 * 1.1f );
			Require( obj.CurrentLevel == 3, $"past LOD3's distance should be LOD3, got {obj.CurrentLevel}" );
			// Hysteresis: coming back to 90% of D3 stays LOD3; 80% drops to LOD2.
			Require( ProxyLod.SelectLevel( obj, d3 * 0.9f ) == 3, "90% of the switch distance must not step back up (hysteresis)" );
			Require( ProxyLod.SelectLevel( obj, d3 * 0.8f ) == 2, "80% of the switch distance should step back up to LOD2" );
		} );

		registry.Add( "viewport-perf", "device bias steps down sooner on a Low tier", () =>
		{
			var mesh = BumpyGrid( 7, 3f, new Vec3( 0, 0, 0 ) );
			var high = ProxyLod.ForObject( "grid", mesh, new Vec3( 0, 0, 0 ), 5f, DeviceTier.High );
			var low = ProxyLod.ForObject( "grid", mesh, new Vec3( 0, 0, 0 ), 5f, DeviceTier.Low );
			var d1 = high.Levels[1].SwitchDistance;
			Require( ProxyLod.SelectLevel( high, d1 * 0.6f ) == 0, "High tier at 60% of D1 stays LOD0" );
			Require( ProxyLod.SelectLevel( low, d1 * 0.6f ) == 1, "Low tier at 60% of D1 is already LOD1 (bias 0.5)" );
		} );

		registry.Add( "viewport-perf", "triangle budget degrades farthest-first and never drops an object", () =>
		{
			var mesh = BumpyGrid( 7, 3f, new Vec3( 0, 0, 0 ) );
			var near = ProxyLod.ForObject( "near", mesh, new Vec3( 0, 0, 0 ), 5f );
			var far = ProxyLod.ForObject( "far", mesh, new Vec3( 0, 0, 300 ), 5f );
			var eye = new Vec3( 0, 0, 0 );
			var budget = near.Levels[0].Triangles + far.Levels[0].Triangles - 1;
			var changed = ProxyLod.UpdateAll( new[] { near, far }, eye, budget, out var drawn );
			Require( changed > 0, "over budget must change a level" );
			Require( near.CurrentLevel == 0, "the near object must stay at full detail" );
			Require( far.CurrentLevel >= 1, "the far object must step down" );
			Require( drawn <= budget, $"drawn {drawn} must fit the budget {budget}" );
			Require( near.TrianglesDrawn > 0 && far.TrianglesDrawn > 0, "both objects must still draw" );
		} );

		registry.Add( "viewport-perf", "adaptive tier steps down after slow frames and back up after fast ones", () =>
		{
			var adaptive = new AdaptiveTier( DeviceTier.High, 16.7 );
			Require( !adaptive.ObserveFrame( 20 ), "one slow frame must not step down" );
			Require( !adaptive.ObserveFrame( 20 ), "two slow frames must not step down" );
			Require( adaptive.ObserveFrame( 20 ), "three slow frames in a row must step down" );
			Require( adaptive.Tier == DeviceTier.Medium, "tier should be Medium after stepping down" );
			for ( var i = 0; i < 59; i++ ) Require( !adaptive.ObserveFrame( 10 ), "fast frames must not step up early" );
			Require( adaptive.ObserveFrame( 10 ), "60 fast frames in a row must step back up" );
			Require( adaptive.Tier == DeviceTier.High, "tier should be High again" );
		} );

		registry.Add( "viewport-perf", "device tier detection from the renderer string", () =>
		{
			Require( ProxyLod.DetectTier( "llvmpipe (LLVM 15)", null ) == DeviceTier.Low, "software renderer is Low" );
			Require( ProxyLod.DetectTier( "NVIDIA GeForce RTX 4090", 8192 ) == DeviceTier.High, "discrete NVIDIA with 8 GB is High" );
			Require( ProxyLod.DetectTier( "NVIDIA GeForce RTX 4090", 2048 ) == DeviceTier.Medium, "discrete NVIDIA with 2 GB is not High" );
			Require( ProxyLod.DetectTier( "Intel UHD Graphics", null ) == DeviceTier.Medium, "integrated Intel is Medium" );
			Require( ProxyLod.DetectTier( "", null ) == DeviceTier.Medium, "unknown renderer is Medium" );
			Require( ProxyLod.DetectTier( "AMD Radeon RX 7900", 1024 ) == DeviceTier.Low, "under 2 GB VRAM is Low" );
		} );

		registry.Add( "viewport-perf", "frustum planes cull spheres and boxes", () =>
		{
			var frustum = ViewFrustum.FromPlanes( new (Vec3, float)[]
			{
				(new Vec3( 1, 0, 0 ), 1f),   // left:   x >= -1
				(new Vec3( -1, 0, 0 ), 1f),  // right:  x <= 1
				(new Vec3( 0, 1, 0 ), 1f),   // bottom: y >= -1
				(new Vec3( 0, -1, 0 ), 1f),  // top:    y <= 1
				(new Vec3( 0, 0, 1 ), 1f),   // near:   z >= -1
				(new Vec3( 0, 0, -1 ), 1f),  // far:    z <= 1
			} );
			Require( frustum.IntersectsSphere( new Vec3( 0, 0, 0 ), 0.5f ), "centre sphere is inside" );
			Require( !frustum.IntersectsSphere( new Vec3( 5, 0, 0 ), 0.5f ), "far sphere is outside" );
			Require( frustum.IntersectsSphere( new Vec3( 1.4f, 0, 0 ), 0.5f ), "sphere touching the right plane counts as inside" );
			Require( frustum.IntersectsBox( new Vec3( -0.5f, -0.5f, -0.5f ), new Vec3( 0.5f, 0.5f, 0.5f ) ), "centre box is inside" );
			Require( !frustum.IntersectsBox( new Vec3( 2, 2, 2 ), new Vec3( 3, 3, 3 ) ), "far box is outside" );
		} );

		registry.Add( "viewport-perf", "frustum from an adapted view-projection matrix keeps the target and culls behind it", () =>
		{
			var (frustum, eye, target) = AdaptedCamera( new Vector3( -10, -8, 5 ), new Vector3( 0, 0, 0 ) );
			var eyeV = new Vec3( eye.X, eye.Y, eye.Z );
			var targetV = new Vec3( target.X, target.Y, target.Z );
			Require( frustum.IntersectsSphere( targetV, 0.1f ), "the camera target must be inside the frustum" );
			Require( !frustum.IntersectsSphere( eyeV + (eyeV - targetV) * 2f, 0.1f ), "a point behind the camera must be outside" );
			Require( !frustum.IntersectsSphere( targetV + new Vec3( 1000, 0, 0 ), 0.1f ), "a point far to the side must be outside" );
		} );

		registry.Add( "viewport-perf", "clusters partition every face exactly once", () =>
		{
			var (mesh, _, _, clusters) = TwoBlobs();
			Require( clusters.Count > 1, "two separated blobs must make more than one cluster" );
			var seen = new HashSet<int>();
			var tris = 0;
			foreach ( var c in clusters )
			{
				Require( c.Radius > 0, "every cluster needs a positive radius" );
				Require( c.FaceIndices.Length > 0, "every cluster needs faces" );
				foreach ( var fi in c.FaceIndices ) Require( seen.Add( fi ), $"face {fi} appears in two clusters" );
				tris += c.TriangleCount;
			}
			Require( seen.Count == mesh.FaceCount, $"every face must be in exactly one cluster (saw {seen.Count}, mesh has {mesh.FaceCount})" );
			var expectedTris = mesh.Faces.Sum( f => Math.Max( 1, f.Count - 2 ) );
			Require( tris == expectedTris, $"cluster triangles {tris} must equal mesh triangles {expectedTris}" );
		} );

		registry.Add( "viewport-perf", "frustum visibility culls the far blob", () =>
		{
			var (_, blobA, blobB, _) = TwoBlobs();
			var clusters = MeshClusters.Build( blobA, targetTrianglesPerCluster: 8 );
			clusters.AddRange( MeshClusters.Build( blobB, targetTrianglesPerCluster: 8 ) );
			var frustum = CameraFrustum( new Vec3( 0, 0, 0 ), 5f );
			var visible = clusters.Where( c => frustum.IntersectsSphere( c.Centre, c.Radius ) ).ToList();
			Require( visible.Count > 0, "the framed blob must have visible clusters" );
			Require( visible.Count < clusters.Count, "the far blob must be culled" );
			Require( visible.All( c => c.Centre.x < 20 ), "no visible cluster may sit near the far blob (x ~ 40)" );
		} );

		registry.Add( "viewport-perf", "streaming schedule evicts LRU invisible clusters, never visible ones", () =>
		{
			var (_, _, _, clusters) = TwoBlobs();
			var frustum = CameraFrustum( new Vec3( 0, 0, 0 ), 5f );
			var resident = clusters.Select( c => c.Index ).ToHashSet();
			var lastUsed = clusters.ToDictionary( c => c.Index, _ => 0 );
			var visibleTriangles = clusters.Where( c => frustum.IntersectsSphere( c.Centre, c.Radius ) ).Sum( c => c.TriangleCount );

			var schedule = ViewportStreaming.Schedule( frustum, clusters, resident, lastUsed, frame: 10, triangleBudget: visibleTriangles );
			Require( !schedule.OverBudget, "budget equal to the visible set must not be over budget" );
			Require( schedule.Evict.Count > 0, "invisible clusters must be evicted to fit the budget" );
			Require( schedule.Evict.All( i => !schedule.Visible.Contains( i ) ), "no visible cluster may be evicted" );
			Require( schedule.ResidentTriangles <= visibleTriangles, "resident total must fit the budget after eviction" );

			var tight = ViewportStreaming.Schedule( frustum, clusters, resident, lastUsed, frame: 10, triangleBudget: visibleTriangles - 1 );
			Require( tight.OverBudget, "a budget below the visible set must report over budget" );
			Require( tight.Evict.All( i => !tight.Visible.Contains( i ) ), "even over budget, no visible cluster may be evicted" );
			Require( tight.Load.Count == 0, "all clusters resident means nothing to load" );
		} );

		registry.Add( "viewport-perf", "subdivision table reproduces the CPU reference exactly", () =>
		{
			foreach ( var mesh in new[] { UnitBox(), BumpyGrid( 4, 2f, new Vec3( 0, 0, 0 ) ) } )
			{
				var table = GpuSubdivision.BuildTable( mesh );
				var reference = CatmullClark.Subdivide( mesh, 1 );
				Require( table.OutputVertexCount == reference.VertexCount, $"table output {table.OutputVertexCount} must equal reference {reference.VertexCount}" );
				var evaluated = GpuSubdivision.EvaluateTable( table, mesh.Positions );
				for ( var i = 0; i < reference.VertexCount; i++ )
					NearVec( evaluated[i], reference.Positions[i], $"table vertex {i}", 1e-4f );
				for ( var i = 0; i < table.OutputVertexCount; i++ )
				{
					var sum = 0f;
					for ( var k = table.Offsets[i]; k < table.Offsets[i + 1]; k++ ) sum += table.Weights[k];
					Near( sum, 1f, $"stencil weights for output {i} must sum to 1", 1e-5f );
				}
			}
			// Creased: one sharp edge must match the CPU crease rule too.
			var box = UnitBox();
			var creases = new Dictionary<EdgeKey, float> { [new EdgeKey( 0, 1 )] = 1f };
			var creasedTable = GpuSubdivision.BuildTable( box, creases );
			var creasedReference = CatmullClark.Subdivide( box, 1, creases, out _ );
			var creased = GpuSubdivision.EvaluateTable( creasedTable, box.Positions );
			for ( var i = 0; i < creasedReference.VertexCount; i++ )
				NearVec( creased[i], creasedReference.Positions[i], $"creased table vertex {i}", 1e-4f );
		} );

		registry.Add( "viewport-perf", "GPU subdivision provider slot: no backend, declining backend, working backend", () =>
		{
			var mesh = UnitBox();
			var reference = CatmullClark.Subdivide( mesh, 1 );
			var previous = GpuSubdivision.Provider;
			try
			{
				GpuSubdivision.Provider = null;
				var cpu = GpuSubdivision.Subdivide( mesh, 1 );
				for ( var i = 0; i < reference.VertexCount; i++ )
					NearVec( cpu.Positions[i], reference.Positions[i], $"CPU fallback vertex {i}", 1e-6f );

				GpuSubdivision.Provider = new DecliningBackend();
				var declined = GpuSubdivision.Subdivide( mesh, 1 );
				for ( var i = 0; i < reference.VertexCount; i++ )
					NearVec( declined.Positions[i], reference.Positions[i], $"declined-backend vertex {i}", 1e-6f );

				GpuSubdivision.Provider = new FakeBackend( reference );
				var gpu = GpuSubdivision.Subdivide( mesh, 1 );
				Require( ReferenceEquals( gpu, reference ), "a succeeding backend's mesh must be the result" );
			}
			finally { GpuSubdivision.Provider = previous; }
		} );

		


		


		registry.Add( "viewport-perf", "published targets check against their budgets", () =>
		{
			Require( PerfTargets.All.Count == 4, $"there should be 4 published targets, got {PerfTargets.All.Count}" );
			var (pass, margin, _) = PerfTargets.Check( PerfTargets.Orbit1M60, 10.0 );
			Require( pass && margin > 0, "10 ms must pass the 16.7 ms orbit target with headroom" );
			var (fail, failMargin, _) = PerfTargets.Check( PerfTargets.Orbit1M60, 20.0 );
			Require( !fail && failMargin < 0, "20 ms must fail the 16.7 ms orbit target" );
			Near( (float)PerfTargets.Interactive5M.BudgetMs, 1000f / 30f, "5M interactive budget is 33.3 ms", 1e-3f );
			Near( (float)PerfTargets.BrushLatency.BudgetMs, 16f, "brush latency budget is 16 ms", 1e-3f );
		} );

		


		


		


		

	}

	sealed class DecliningBackend : IGpuSubdivisionBackend
	{
		public bool TrySubdivide( PolyMesh mesh, out PolyMesh? subdivided, out string? why )
		{
			subdivided = null;
			why = "DecliningBackend always declines.";
			return false;
		}
	}

	sealed class FakeBackend : IGpuSubdivisionBackend
	{
		readonly PolyMesh _result;
		public FakeBackend( PolyMesh result ) => _result = result;
		public bool TrySubdivide( PolyMesh mesh, out PolyMesh? subdivided, out string? why )
		{
			subdivided = _result;
			why = null;
			return true;
		}
	}
}
