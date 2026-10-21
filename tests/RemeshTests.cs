using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Effigy;
using Marionette.Desktop;

/// <summary>Procedural, in-memory remesh fixtures. Nothing here reads or writes user assets.</summary>
internal static class RemeshFixtures
{
	/// <summary>Deterministic hash noise in [-1, 1] keyed on a lattice coordinate.</summary>
	public static float Noise( int a, int b, int c )
	{
		unchecked
		{
			uint h = (uint)a * 73856093u ^ (uint)b * 19349663u ^ (uint)c * 83492791u;
			h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
			return ( h & 0xFFFFFF ) / (float)0xFFFFFF * 2f - 1f;
		}
	}

	/// <summary>
	/// A triangulated cube-sphere of radius <paramref name="radius"/>: 6·n²·2 triangles, every vertex
	/// pushed radially by up to <paramref name="noise"/>·radius. With an even n the equator z = 0 runs
	/// along edges. Optional per-hemisphere UV charts (a closed UV seam on the equator) and
	/// per-hemisphere materials (a closed material boundary on the equator).
	/// </summary>
	public static PolyMesh CubeSphere( int n, float radius = 1f, float noise = 0f, bool hemisphereCharts = false, bool hemisphereMaterials = false )
	{
		var mesh = new PolyMesh();
		var index = new Dictionary<(int, int, int), int>();
		int Vertex( int x, int y, int z )
		{
			if ( index.TryGetValue( (x, y, z), out var v ) ) return v;
			var p = new Vec3( x / (float)n * 2f - 1f, y / (float)n * 2f - 1f, z / (float)n * 2f - 1f ).Normal;
			// Radial noise keeps z = 0 vertices on z = 0, so the equator stays a clean feature line.
			p = p * ( radius * ( 1f + noise * Noise( x, y, z ) ) );
			v = mesh.Positions.Count;
			mesh.Positions.Add( p );
			index[(x, y, z)] = v;
			return v;
		}
		Vec2 Uv( Vec3 p ) => p.z >= 0 || !hemisphereCharts ? new Vec2( p.x * 0.25f + 0.25f, p.y * 0.25f + 0.25f ) : new Vec2( p.x * 0.25f + 0.75f, p.y * 0.25f + 0.25f );
		void Quad( (int, int, int) a, (int, int, int) b, (int, int, int) c, (int, int, int) d )
		{
			int ia = Vertex( a.Item1, a.Item2, a.Item3 ), ib = Vertex( b.Item1, b.Item2, b.Item3 );
			int ic = Vertex( c.Item1, c.Item2, c.Item3 ), id = Vertex( d.Item1, d.Item2, d.Item3 );
			foreach ( var tri in new[] { new[] { ia, ib, ic }, new[] { ia, ic, id } } )
			{
				var centroid = ( mesh.Positions[tri[0]] + mesh.Positions[tri[1]] + mesh.Positions[tri[2]] ) / 3f;
				bool south = centroid.z < 0;
				var uvs = new Vec2[3];
				for ( int k = 0; k < 3; k++ )
				{
					var p = mesh.Positions[tri[k]];
					uvs[k] = hemisphereCharts && south ? new Vec2( p.x * 0.25f + 0.75f, p.y * 0.25f + 0.25f ) : new Vec2( p.x * 0.25f + 0.25f, p.y * 0.25f + 0.25f );
				}
				mesh.Faces.Add( new Face( tri, uvs, hemisphereMaterials && south ? 1 : 0 ) );
			}
		}
		for ( int i = 0; i < n; i++ )
		for ( int j = 0; j < n; j++ )
		{
			// Six faces, all wound outward.
			Quad( (i, j, n), (i + 1, j, n), (i + 1, j + 1, n), (i, j + 1, n) );
			Quad( (i, j + 1, 0), (i + 1, j + 1, 0), (i + 1, j, 0), (i, j, 0) );
			Quad( (n, i, j), (n, i + 1, j), (n, i + 1, j + 1), (n, i, j + 1) );
			Quad( (0, i, j + 1), (0, i + 1, j + 1), (0, i + 1, j), (0, i, j) );
			Quad( (i, 0, j), (i + 1, 0, j), (i + 1, 0, j + 1), (i, 0, j + 1) );
			Quad( (i, n, j + 1), (i + 1, n, j + 1), (i + 1, n, j), (i, n, j) );
		}
		_ = Uv( Vec3.Zero );
		return mesh;
	}

	/// <summary>
	/// A closed, capped, triangulated cylinder around Z: <paramref name="segments"/> around,
	/// <paramref name="rows"/> up the side, caps as concentric rings. The rims are 90° hard edges.
	/// The side carries one UV chart with a dangling seam at angle 0 (u = 0 vs u = 1); each cap is
	/// its own chart. Side vertices get radial noise of up to noise·radius (rims stay exact).
	/// </summary>
	public static PolyMesh Cylinder( int segments, int rows, int capRings, float radius = 1f, float height = 3f, float noise = 0f, bool sideMaterial = false )
	{
		var mesh = new PolyMesh();
		float half = height * 0.5f;
		int[,] side = new int[rows + 1, segments];
		for ( int r = 0; r <= rows; r++ )
		for ( int s = 0; s < segments; s++ )
		{
			float a = MathF.Tau * s / segments;
			bool rim = r == 0 || r == rows;
			float rr = radius * ( 1f + ( rim ? 0f : noise * Noise( r, s, 7 ) ) );
			side[r, s] = mesh.Positions.Count;
			mesh.Positions.Add( new Vec3( rr * MathF.Cos( a ), rr * MathF.Sin( a ), -half + height * r / rows ) );
		}
		for ( int r = 0; r < rows; r++ )
		for ( int s = 0; s < segments; s++ )
		{
			int s1 = ( s + 1 ) % segments;
			float u0 = s / (float)segments, u1 = ( s + 1 ) / (float)segments;
			float v0 = r / (float)rows * 0.5f, v1 = ( r + 1 ) / (float)rows * 0.5f;
			int a = side[r, s], b = side[r, s1], c = side[r + 1, s1], d = side[r + 1, s];
			int mat = sideMaterial ? 1 : 0;
			mesh.Faces.Add( new Face( new[] { a, b, c }, new[] { new Vec2( u0, v0 ), new Vec2( u1, v0 ), new Vec2( u1, v1 ) }, mat ) );
			mesh.Faces.Add( new Face( new[] { a, c, d }, new[] { new Vec2( u0, v0 ), new Vec2( u1, v1 ), new Vec2( u0, v1 ) }, mat ) );
		}
		// Caps: rings from the rim inwards, then a centre fan. Same segment count per ring keeps it simple.
		foreach ( var top in new[] { false, true } )
		{
			float z = top ? half : -half;
			int rimRow = top ? rows : 0;
			var rings = new List<int[]>();
			var rimRing = new int[segments];
			for ( int s = 0; s < segments; s++ ) rimRing[s] = side[rimRow, s];
			rings.Add( rimRing );
			for ( int k = 1; k < capRings; k++ )
			{
				float rr = radius * ( 1f - k / (float)capRings );
				var ring = new int[segments];
				for ( int s = 0; s < segments; s++ )
				{
					float a = MathF.Tau * ( s + 0.5f * k ) / segments;
					ring[s] = mesh.Positions.Count;
					mesh.Positions.Add( new Vec3( rr * MathF.Cos( a ), rr * MathF.Sin( a ), z ) );
				}
				rings.Add( ring );
			}
			int centre = mesh.Positions.Count;
			mesh.Positions.Add( new Vec3( 0, 0, z ) );
			Vec2 CapUv( int v ) { var p = mesh.Positions[v]; return new Vec2( p.x * 0.2f + ( top ? 0.25f : 0.75f ), p.y * 0.2f + 0.8f ); }
			void Tri( int a, int b, int c )
			{
				var idx = top ? new[] { a, b, c } : new[] { a, c, b };
				mesh.Faces.Add( new Face( idx, idx.Select( CapUv ).ToArray(), 0 ) );
			}
			for ( int k = 0; k + 1 < rings.Count; k++ )
			for ( int s = 0; s < segments; s++ )
			{
				int s1 = ( s + 1 ) % segments;
				Tri( rings[k][s], rings[k][s1], rings[k + 1][s] );
				Tri( rings[k][s1], rings[k + 1][s1], rings[k + 1][s] );
			}
			var inner = rings[^1];
			for ( int s = 0; s < segments; s++ ) Tri( inner[s], inner[( s + 1 ) % segments], centre );
		}
		return mesh;
	}

	/// <summary>An open tube (no caps) around Z, triangulated, all one chart without seams.</summary>
	public static PolyMesh Tube( int segments, int rows, float radius = 1f, float height = 3f, float noise = 0f )
	{
		var mesh = new PolyMesh();
		float half = height * 0.5f;
		for ( int r = 0; r <= rows; r++ )
		for ( int s = 0; s < segments; s++ )
		{
			float a = MathF.Tau * s / segments;
			float rr = radius * ( 1f + noise * Noise( r, s, 3 ) );
			mesh.Positions.Add( new Vec3( rr * MathF.Cos( a ), rr * MathF.Sin( a ), -half + height * r / rows ) );
		}
		for ( int r = 0; r < rows; r++ )
		for ( int s = 0; s < segments; s++ )
		{
			int s1 = ( s + 1 ) % segments;
			int a = r * segments + s, b = r * segments + s1, c = ( r + 1 ) * segments + s1, d = ( r + 1 ) * segments + s;
			mesh.Faces.Add( new Face( new[] { a, b, c } ) );
			mesh.Faces.Add( new Face( new[] { a, c, d } ) );
		}
		return mesh;
	}
}

internal static class RemeshTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "remesh", name, test );

		Check( "probe kernel quadremesh", () =>
		{
			var cyl = RemeshFixtures.Cylinder( 96, 60, 12, 1f, 3f, 0.005f );
			var clock = Stopwatch.StartNew();
			var r = QuadRemesh.Run( cyl, new QuadRemesh.Options { TargetFaces = 2000 } );
			Console.WriteLine( $"cyl {r} {MeshValidator.Validate( r.Mesh )}" );
			var tube = RemeshFixtures.Tube( 96, 60, 1f, 3f, 0.005f );
			r = QuadRemesh.Run( tube, new QuadRemesh.Options { TargetFaces = 2000 } );
			var v = MeshValidator.Validate( r.Mesh );
			// boundary vertex distance to rim plane
			float worst = 0;
			foreach ( var (key, faces) in r.Mesh.BuildEdgeFaces() )
				if ( faces.Count == 1 ) foreach ( var vi in new[] { key.A, key.B } ) worst = MathF.Max( worst, 1.5f - MathF.Abs( r.Mesh.Positions[vi].z ) );
			Console.WriteLine( $"tube {r} {v} worst border offset {worst}" );
			var big = RemeshFixtures.CubeSphere( 160, 1f, 0.01f );
			Console.WriteLine( $"sphere tris {big.Faces.Count}" );
			clock.Restart();
			var d = Decimate.Run( big, new Decimate.Options { TargetTriangles = 4000 } );
			Console.WriteLine( $"decimate 300k->4000 {clock.ElapsedMilliseconds} ms" );
			clock.Restart();
			r = QuadRemesh.Run( big, new QuadRemesh.Options { TargetFaces = 8000 } );
			Console.WriteLine( $"kernel quadremesh 300k {r} {clock.ElapsedMilliseconds} ms" );
		} );
	}
}
