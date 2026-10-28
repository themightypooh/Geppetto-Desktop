using System.Numerics;
using Effigy;
using Marionette.Desktop;

// Wave-6 lane P78 tests. Group: particle-skinned-emit.
// Sweep line (scratch only — do NOT add to the canonical TestRegistry.cs):
//   ParticleSkinnedEmitTests.Register( registry, settings );
// after the GpuParticleSystemTests.Register line.

internal static class ParticleSkinnedEmitTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void NearVec( Vector3 got, Vector3 want, float tol, string what )
		{
			if ( (got - want).Length() > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" );
		}
		void ExactVec( Vector3 got, Vector3 want, string what ) { if ( got != want ) throw new Exception( $"{what}: got {got}, want {want} exactly" ); }
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; }
			catch ( InvalidOperationException ) { return; }
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}

		// 2x2 quad in the XY plane, two triangles of area 2 each, total area 4.
		ParticleMesh Quad() => new(
			new[] { new Vector3( 0, 0, 0 ), new Vector3( 2, 0, 0 ), new Vector3( 2, 2, 0 ), new Vector3( 0, 2, 0 ) },
			new[] { 0, 1, 2, 0, 2, 3 } );
		// Single triangle, area 2, normal (0,0,1).
		ParticleMesh Tri() => new(
			new[] { new Vector3( 0, 0, 0 ), new Vector3( 2, 0, 0 ), new Vector3( 0, 2, 0 ) },
			new[] { 0, 1, 2 } );
		SkinWeights Mixed( BoneWeight[] v0, BoneWeight[] v1, BoneWeight[] v2, BoneWeight[] v3 )
		{
			var w = new SkinWeights();
			w.Vertices.Add( v0 ); w.Vertices.Add( v1 ); w.Vertices.Add( v2 ); w.Vertices.Add( v3 );
			return w;
		}
		// Exact +90-degree rotation about Z (row-vector convention), built
		// from exact 0/1 entries so the posed coordinates are bit-exact
		// instead of carrying CreateRotationZ's cos(pi/2) = -4.37e-08 residue.
		Matrix4x4 RotZ90() => new( 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 );

		registry.Add( "particle-skinned-emit", "identity pose returns the bind pose bit-identically", () =>
		{
			var bind = Quad();
			var posed = ParticleSkinnedEmit.PoseVertices( bind, SkinWeights.AllTo( 4, 0 ), new[] { Matrix4x4.Identity } );
			for ( int i = 0; i < 4; i++ ) ExactVec( posed[i], bind.Vertices[i], $"vertex {i}" );
			var posedMesh = ParticleSkinnedEmit.PoseMesh( bind, SkinWeights.AllTo( 4, 0 ), new[] { Matrix4x4.Identity } );
			for ( int i = 0; i < 4; i++ ) ExactVec( posedMesh.Vertices[i], bind.Vertices[i], $"posed-mesh vertex {i}" );
			Near( posedMesh.TotalArea, bind.TotalArea, 0, "posed-mesh total area" );
		} );

		registry.Add( "particle-skinned-emit", "single bone translated by (10,0,0) moves fully-weighted vertices exactly (10,0,0)", () =>
		{
			var bind = Quad();
			var shift = Matrix4x4.CreateTranslation( 10, 0, 0 );
			var posed = ParticleSkinnedEmit.PoseVertices( bind, SkinWeights.AllTo( 4, 0 ), new[] { shift } );
			ExactVec( posed[0], new Vector3( 10, 0, 0 ), "vertex 0" );
			ExactVec( posed[1], new Vector3( 12, 0, 0 ), "vertex 1" );
			ExactVec( posed[2], new Vector3( 12, 2, 0 ), "vertex 2" );
			ExactVec( posed[3], new Vector3( 10, 2, 0 ), "vertex 3" );
		} );

		registry.Add( "particle-skinned-emit", "translated pose moves emitted samples by the same (10,0,0) translation", () =>
		{
			var bind = Quad();
			var weights = SkinWeights.AllTo( 4, 0 );
			var shift = new[] { Matrix4x4.CreateTranslation( 10, 0, 0 ) };
			var rngBind = new ParticleRng( 99 );
			var bindSamples = ParticleSkinnedEmit.SamplePoints( bind, weights, new[] { Matrix4x4.Identity }, 64, ref rngBind );
			var rngPosed = new ParticleRng( 99 );
			var posedSamples = ParticleSkinnedEmit.SamplePoints( bind, weights, shift, 64, ref rngPosed );
			Check( bindSamples.Length == 64 && posedSamples.Length == 64, "sample counts" );
			for ( int i = 0; i < 64; i++ )
			{
				NearVec( posedSamples[i].Position, bindSamples[i].Position + new Vector3( 10, 0, 0 ), 1e-5f, $"sample {i} position" );
				NearVec( posedSamples[i].Normal, bindSamples[i].Normal, 1e-6f, $"sample {i} normal" );
				Check( posedSamples[i].TriangleIndex == bindSamples[i].TriangleIndex, $"sample {i} picked a different triangle after a pure translation" );
			}
		} );

		registry.Add( "particle-skinned-emit", "50/50 two-bone blend puts a vertex exactly halfway between the two bone transforms", () =>
		{
			var bind = Quad();
			var mix = new BoneWeight[] { new( 0, 0.5f ), new( 1, 0.5f ) };
			var weights = Mixed( mix, mix, mix, new[] { new BoneWeight( 0, 1f ) } );
			var bones = new[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation( 10, 0, 0 ) };
			var posed = ParticleSkinnedEmit.PoseVertices( bind, weights, bones );
			ExactVec( posed[0], new Vector3( 5, 0, 0 ), "blended vertex 0: 0.5*0 + 0.5*10" );
			ExactVec( posed[1], new Vector3( 7, 0, 0 ), "blended vertex 1: 0.5*2 + 0.5*12" );
			ExactVec( posed[2], new Vector3( 7, 2, 0 ), "blended vertex 2: x halfway, y untouched by an X-only shift" );
			ExactVec( posed[3], new Vector3( 0, 2, 0 ), "unblended vertex 3 stays on bone 0" );
		} );

		registry.Add( "particle-skinned-emit", "bone rotated 90 degrees about Z rotates its vertices exactly", () =>
		{
			var bind = Tri();
			// Overwrite the triangle's vertices with rotation probes: the
			// Tri fixture is rebuilt here so the known vertices are (1,0,0),
			// (0,1,0) and (0,0,1).
			bind = new ParticleMesh(
				new[] { new Vector3( 1, 0, 0 ), new Vector3( 0, 1, 0 ), new Vector3( 0, 0, 1 ) },
				new[] { 0, 1, 2 } );
			var posed = ParticleSkinnedEmit.PoseVertices( bind, SkinWeights.AllTo( 3, 0 ), new[] { RotZ90() } );
			ExactVec( posed[0], new Vector3( 0, 1, 0 ), "(1,0,0) under +90deg Z" );
			ExactVec( posed[1], new Vector3( -1, 0, 0 ), "(0,1,0) under +90deg Z" );
			ExactVec( posed[2], new Vector3( 0, 0, 1 ), "(0,0,1) under +90deg Z (axis untouched)" );
		} );

		registry.Add( "particle-skinned-emit", "weight-sum violation throws instead of renormalising", () =>
		{
			var bind = Quad();
			var bones = new[] { Matrix4x4.Identity };
			var half = Mixed( new[] { new BoneWeight( 0, 0.5f ) }, new[] { new BoneWeight( 0, 1f ) }, new[] { new BoneWeight( 0, 1f ) }, new[] { new BoneWeight( 0, 1f ) } );
			Throws( () => ParticleSkinnedEmit.PoseVertices( bind, half, bones ), "weights summing to 0.5 must throw, not rescale to 1" );
			var over = Mixed( new[] { new BoneWeight( 0, 1.5f ) }, new[] { new BoneWeight( 0, 1f ) }, new[] { new BoneWeight( 0, 1f ) }, new[] { new BoneWeight( 0, 1f ) } );
			Throws( () => ParticleSkinnedEmit.PoseVertices( bind, over, bones ), "weights summing to 1.5 must throw" );
			var negative = Mixed( new[] { new BoneWeight( 0, -0.25f ), new BoneWeight( 0, 1.25f ) }, new[] { new BoneWeight( 0, 1f ) }, new[] { new BoneWeight( 0, 1f ) }, new[] { new BoneWeight( 0, 1f ) } );
			Throws( () => ParticleSkinnedEmit.PoseVertices( bind, negative, bones ), "a negative weight must throw even when the sum is 1" );
			var empty = Mixed( Array.Empty<BoneWeight>(), new[] { new BoneWeight( 0, 1f ) }, new[] { new BoneWeight( 0, 1f ) }, new[] { new BoneWeight( 0, 1f ) } );
			Throws( () => ParticleSkinnedEmit.PoseVertices( bind, empty, bones ), "an unweighted vertex must throw" );
		} );

		registry.Add( "particle-skinned-emit", "out-of-range bone index throws", () =>
		{
			var bind = Quad();
			var bones = new[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation( 1, 0, 0 ) };
			var high = Mixed( new[] { new BoneWeight( 2, 1f ) }, new[] { new BoneWeight( 0, 1f ) }, new[] { new BoneWeight( 0, 1f ) }, new[] { new BoneWeight( 0, 1f ) } );
			Throws( () => ParticleSkinnedEmit.PoseVertices( bind, high, bones ), "bone 2 with only 2 matrices must throw" );
			var neg = Mixed( new[] { new BoneWeight( -1, 1f ) }, new[] { new BoneWeight( 0, 1f ) }, new[] { new BoneWeight( 0, 1f ) }, new[] { new BoneWeight( 0, 1f ) } );
			Throws( () => ParticleSkinnedEmit.PoseVertices( bind, neg, bones ), "bone -1 must throw" );
		} );

		registry.Add( "particle-skinned-emit", "same seed and same pose give bit-identical samples, a different seed differs", () =>
		{
			var bind = Quad();
			var weights = SkinWeights.AllTo( 4, 0 );
			var bones = new[] { Matrix4x4.CreateTranslation( 10, 0, 0 ) };
			var rngA = new ParticleRng( 1234 );
			var a = ParticleSkinnedEmit.SamplePoints( bind, weights, bones, 64, ref rngA );
			var rngB = new ParticleRng( 1234 );
			var b = ParticleSkinnedEmit.SamplePoints( bind, weights, bones, 64, ref rngB );
			for ( int i = 0; i < 64; i++ )
			{
				ExactVec( a[i].Position, b[i].Position, $"sample {i} position bit-identical" );
				ExactVec( a[i].Normal, b[i].Normal, $"sample {i} normal bit-identical" );
				Check( a[i].TriangleIndex == b[i].TriangleIndex, $"sample {i} triangle bit-identical" );
			}
			var rngC = new ParticleRng( 4321 );
			var c = ParticleSkinnedEmit.SamplePoints( bind, weights, bones, 64, ref rngC );
			var anyDiff = false;
			for ( int i = 0; i < 64; i++ ) if ( c[i].Position != a[i].Position ) anyDiff = true;
			Check( anyDiff, "a different seed produced identical samples, which would mean the seed is ignored" );
		} );

		registry.Add( "particle-skinned-emit", "samples lie on the posed triangle with its normal", () =>
		{
			var bind = Tri();
			var weights = SkinWeights.AllTo( 3, 0 );
			var bones = new[] { Matrix4x4.CreateTranslation( 5, 5, 0 ) };
			var rng = new ParticleRng( 7 );
			var samples = ParticleSkinnedEmit.SamplePoints( bind, weights, bones, 200, ref rng );
			var posed = ParticleSkinnedEmit.PoseMesh( bind, weights, bones );
			posed.GetTriangle( 0, out var pa, out var pb, out var pc );
			var v0 = pb - pa; var v1 = pc - pa;
			float d00 = Vector3.Dot( v0, v0 ), d01 = Vector3.Dot( v0, v1 ), d11 = Vector3.Dot( v1, v1 );
			float denom = d00 * d11 - d01 * d01;
			foreach ( var s in samples )
			{
				var v2 = s.Position - pa;
				float d20 = Vector3.Dot( v2, v0 ), d21 = Vector3.Dot( v2, v1 );
				float bv = (d11 * d20 - d01 * d21) / denom;
				float bw = (d00 * d21 - d01 * d20) / denom;
				float bu = 1 - bv - bw;
				Check( bu >= -1e-4f && bv >= -1e-4f && bw >= -1e-4f && bu <= 1 + 1e-4f && bv <= 1 + 1e-4f && bw <= 1 + 1e-4f,
					$"sample {s.Position} outside posed triangle (barycentric {bu},{bv},{bw})" );
				NearVec( s.Normal, new Vector3( 0, 0, 1 ), 1e-6f, "posed triangle normal" );
				Check( s.TriangleIndex == 0, "single-triangle mesh must report triangle 0" );
			}
		} );

		registry.Add( "particle-skinned-emit", "area weighting favours the larger triangle in proportion", () =>
		{
			// Triangle 1 area 1, triangle 2 area 4, total 5: 80% of samples
			// must come from triangle 2. The triangles are far apart in X
			// so the sample's X alone classifies it.
			var mesh = new ParticleMesh(
				new[] { new Vector3( 0, 0, 0 ), new Vector3( 1, 0, 0 ), new Vector3( 0, 2, 0 ),
					new Vector3( 10, 0, 0 ), new Vector3( 14, 0, 0 ), new Vector3( 10, 2, 0 ) },
				new[] { 0, 1, 2, 3, 4, 5 } );
			Near( mesh.TotalArea, 5, 1e-5f, "two-triangle total area" );
			var rng = new ParticleRng( 7 );
			var samples = ParticleSkinnedEmit.SamplePoints( mesh, 4000, ref rng );
			var big = 0;
			foreach ( var s in samples ) if ( s.Position.X >= 5 ) big++;
			Near( big / 4000f, 0.8f, 0.05f, $"fraction of samples from the area-4 triangle (got {big}/4000)" );
		} );

		registry.Add( "particle-skinned-emit", "validate passes valid input and rejects a weight-count mismatch", () =>
		{
			var bind = Quad();
			ParticleSkinnedEmit.Validate( bind, SkinWeights.AllTo( 4, 0 ), new[] { Matrix4x4.Identity } );
			Throws( () => ParticleSkinnedEmit.Validate( bind, SkinWeights.AllTo( 3, 0 ), new[] { Matrix4x4.Identity } ),
				"3 weight sets for a 4-vertex mesh must throw" );
			var rng = new ParticleRng( 1 );
			Check( ParticleSkinnedEmit.SamplePoints( bind, 0, ref rng ).Length == 0, "zero-count sampling must return empty, not throw" );
			Throws( () => ParticleSkinnedEmit.SamplePoints( bind, -1, ref rng ), "negative sample count must throw" );
		} );

		registry.Add( "particle-skinned-emit", "posed mesh keeps the bind indices and rescales area with the pose", () =>
		{
			var bind = Quad();
			var posed = ParticleSkinnedEmit.PoseMesh( bind, SkinWeights.AllTo( 4, 0 ), new[] { Matrix4x4.CreateScale( 2f ) } );
			Check( posed.TriangleCount == 2, "posed triangle count" );
			for ( int i = 0; i < bind.Indices.Length; i++ ) Check( posed.Indices[i] == bind.Indices[i], $"posed index {i} changed" );
			Near( bind.TotalArea, 4, 1e-5f, "bind quad area" );
			Near( posed.TotalArea, 16, 1e-4f, "2x-scaled quad area (area scales with the square)" );
			ExactVec( posed.Vertices[2], new Vector3( 4, 4, 0 ), "scaled vertex 2" );
		} );
	}
}
