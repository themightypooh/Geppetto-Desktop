using System.Numerics;
using Marionette.Desktop;

// Wave-6 lane P77 tests. Group: particle-mesh-emit.
// Sweep line (scratch only — do NOT add to the canonical TestRegistry.cs):
//   ParticleMeshEmitTests.Register( registry, settings );
// after the GpuParticleSystemTests.Register line.
// v1 area-weighted MeshSurface sampling is NOT touched by this lane: no
// test here constructs a ParticleSystem emitter, and the sampler under
// test is a pure function of mesh + density + seeded ParticleRng.

internal static class ParticleMeshEmitTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( double got, double want, double tol, string what ) { if ( Math.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; }
			throw new Exception( $"{what}: expected an ArgumentException, none thrown" );
		}

		// Two triangles sharing vertices: big area 2, small area 0.5 (ratio 4:1).
		static ParticleMesh TwoTri() => new(
			new[] { new Vector3( 0, 0, 0 ), new Vector3( 4, 0, 0 ), new Vector3( 0, 1, 0 ), new Vector3( 1, 0, 0 ) },
			new[] { 0, 1, 2, 0, 3, 2 } );
		// Two triangles with separate vertices, so one can be all-black.
		static ParticleMesh TwoSeparate() => new(
			new[] { new Vector3( 0, 0, 0 ), new Vector3( 4, 0, 0 ), new Vector3( 0, 1, 0 ), new Vector3( 10, 0, 0 ), new Vector3( 11, 0, 0 ), new Vector3( 10, 1, 0 ) },
			new[] { 0, 1, 2, 3, 4, 5 } );
		static ParticleMesh Single() => new(
			new[] { new Vector3( 0, 0, 0 ), new Vector3( 4, 0, 0 ), new Vector3( 0, 3, 0 ) },
			new[] { 0, 1, 2 } );
		static (float u, float v, float w) Bary( Vector3 a, Vector3 b, Vector3 c, Vector3 p )
		{
			var v0 = b - a; var v1 = c - a; var v2 = p - a;
			float d00 = Vector3.Dot( v0, v0 ), d01 = Vector3.Dot( v0, v1 ), d11 = Vector3.Dot( v1, v1 );
			float d20 = Vector3.Dot( v2, v0 ), d21 = Vector3.Dot( v2, v1 );
			float denom = d00 * d11 - d01 * d01;
			float v = ( d11 * d20 - d01 * d21 ) / denom;
			float w = ( d00 * d21 - d01 * d20 ) / denom;
			return ( 1 - v - w, v, w );
		}
		static float[] Ones( int n ) { var a = new float[n]; for ( int i = 0; i < n; i++ ) a[i] = 1f; return a; }

		registry.Add( "particle-mesh-emit", "uniform density reproduces area-weighted behaviour", () =>
		{
			var mesh = TwoTri();
			var s = ParticleMeshDensitySampler.FromVertexDensity( mesh, Ones( 4 ) );
			// Weights must match v1's own area CDF differences exactly (same area formula).
			Near( s.Weights[0], mesh.AreaCdf[0], 1e-6, "big triangle weight" );
			Near( s.Weights[1], mesh.AreaCdf[1] - mesh.AreaCdf[0], 1e-6, "small triangle weight" );
			Near( s.Weights[0] / s.Weights[1], 4.0, 1e-6, "area ratio" );
			// Statistical band, explained: expected big share 2 / 2.5 = 0.8.
			// N = 4000, sigma = sqrt(4000 * 0.8 * 0.2) = 25.3 counts (0.63%).
			// Band 3000..3400 (75%..85%) is mean 3200 ± 200 = ±7.9 sigma, so a
			// correct sampler essentially never leaves it, while a sampler
			// ignoring area (50/50, mean 2000) never enters it.
			var rng = new ParticleRng( 42 );
			int big = 0; const int n = 4000;
			for ( int i = 0; i < n; i++ ) if ( s.Sample( ref rng ).Triangle == 0 ) big++;
			Check( big >= 3000 && big <= 3400, $"uniform density: big triangle got {big}/{n}, expected 3000..3400 (80% ± 5%)" );
		} );

		registry.Add( "particle-mesh-emit", "black vertex-colour triangle is never picked", () =>
		{
			var mesh = TwoSeparate();
			var colours = new[] { new Vector3( 1, 1, 1 ), new Vector3( 1, 1, 1 ), new Vector3( 1, 1, 1 ), Vector3.Zero, Vector3.Zero, Vector3.Zero };
			var s = ParticleMeshDensitySampler.FromVertexColours( mesh, colours );
			Near( s.Weights[1], 0.0, 1e-12, "black triangle weight" );
			Check( s.TotalWeight > 0, "white triangle must keep total weight positive" );
			var rng = new ParticleRng( 7 );
			int dead = 0; const int n = 5000;
			for ( int i = 0; i < n; i++ ) if ( s.Sample( ref rng ).Triangle == 1 ) dead++;
			Check( dead == 0, $"black triangle picked {dead}/{n} times, expected exactly 0" );
		} );

		registry.Add( "particle-mesh-emit", "vertex density weight is area times mean vertex density", () =>
		{
			var s = ParticleMeshDensitySampler.FromVertexDensity( Single(), new[] { 0f, 2f, 4f } );
			// Area 6, mean density (0+2+4)/3 = 2, weight 12.
			Near( s.Weights[0], 12.0, 1e-6, "mean-density weight" );
			var half = ParticleMeshDensitySampler.FromVertexDensity( Single(), new[] { 0f, 1f, 1f } );
			Near( half.Weights[0], 4.0, 1e-6, "one dead vertex leaves 2/3 of the weight" );
		} );

		registry.Add( "particle-mesh-emit", "vertex colour density is the mean RGB channel", () =>
		{
			// Each vertex is a pure primary: mean channel = 1/3 each, so the
			// triangle mean is 1/3 and weight = area 6 / 3 = 2.
			var s = ParticleMeshDensitySampler.FromVertexColours( Single(),
				new[] { new Vector3( 1, 0, 0 ), new Vector3( 0, 1, 0 ), new Vector3( 0, 0, 1 ) } );
			Near( s.Weights[0], 2.0, 1e-6, "mean-RGB weight" );
		} );

		registry.Add( "particle-mesh-emit", "UV density concentrates samples on the live half", () =>
		{
			var mesh = TwoSeparate();
			var uvs = new[] { new Vector2( 0, 0 ), new Vector2( 0.2f, 0 ), new Vector2( 0, 0.2f ), new Vector2( 0.8f, 0 ), new Vector2( 1, 0 ), new Vector2( 0.8f, 0.2f ) };
			// Centroids: live triangle x = 0.067, dead triangle x = 0.867.
			var s = ParticleMeshDensitySampler.FromUvDensity( mesh, uvs, uv => uv.X < 0.5f ? 1f : 0f );
			Near( s.Weights[1], 0.0, 1e-12, "dead-half weight" );
			var rng = new ParticleRng( 99 );
			int dead = 0; const int n = 2000;
			for ( int i = 0; i < n; i++ ) if ( s.Sample( ref rng ).Triangle == 1 ) dead++;
			Check( dead == 0, $"dead UV half picked {dead}/{n} times, expected exactly 0" );
		} );

		registry.Add( "particle-mesh-emit", "UV density is evaluated at the triangle centroid", () =>
		{
			// One triangle straddling x = 0.5: vertices at x = 0, 1, 0, but
			// centroid x = 1/3 < 0.5, so the WHOLE triangle is live at full
			// weight — density does not vary within a triangle in this lane.
			var s = ParticleMeshDensitySampler.FromUvDensity( Single(),
				new[] { new Vector2( 0, 0 ), new Vector2( 1, 0 ), new Vector2( 0, 0 ) },
				uv => uv.X < 0.5f ? 1f : 0f );
			Near( s.Weights[0], 6.0, 1e-6, "straddling triangle keeps full area weight" );
			// Graded function: weight = area * centroid density.
			var g = ParticleMeshDensitySampler.FromUvDensity( TwoTri(),
				new[] { new Vector2( 0, 0 ), new Vector2( 1, 0 ), new Vector2( 0, 1 ), new Vector2( 0.5f, 0 ) },
				uv => uv.X );
			Near( g.Weights[0], 2.0 * ( 1.0 / 3.0 ), 1e-4, "graded big weight" );
			Near( g.Weights[1], 0.5 * ( 0.5 / 3.0 ), 1e-4, "graded small weight" );
		} );

		registry.Add( "particle-mesh-emit", "determinism: same seed bit-identical, different seed differs", () =>
		{
			var s = ParticleMeshDensitySampler.FromVertexDensity( TwoTri(), Ones( 4 ) );
			const int n = 500;
			var rngA = new ParticleRng( 1234 );
			var first = new MeshSurfaceSample[n];
			for ( int i = 0; i < n; i++ ) first[i] = s.Sample( ref rngA );
			var rngB = new ParticleRng( 1234 );
			for ( int i = 0; i < n; i++ )
			{
				var again = s.Sample( ref rngB );
				Check( again.Point == first[i].Point && again.Triangle == first[i].Triangle, $"same seed differs at sample {i}" );
			}
			var rngC = new ParticleRng( 1235 );
			int diff = 0;
			for ( int i = 0; i < n; i++ ) if ( s.Sample( ref rngC ).Point != first[i].Point ) diff++;
			Check( diff > 400, $"different seed should differ almost everywhere, only {diff}/{n} differ" );
		} );

		registry.Add( "particle-mesh-emit", "barycentric sampling is uniform inside one triangle", () =>
		{
			var mesh = Single();
			var s = ParticleMeshDensitySampler.FromVertexDensity( mesh, Ones( 3 ) );
			var rng = new ParticleRng( 5 );
			const int n = 20000;
			int[] sub = new int[4];
			double mu = 0, mv = 0, mw = 0;
			mesh.GetTriangle( 0, out var a, out var b, out var c );
			for ( int i = 0; i < n; i++ )
			{
				var p = s.Sample( ref rng ).Point;
				var (u, v, w) = Bary( a, b, c, p );
				Check( u >= -1e-4f && v >= -1e-4f && w >= -1e-4f && u + v + w <= 1 + 1e-4f, $"sample {p} outside triangle" );
				mu += u; mv += v; mw += w;
				// Medial subdivision: each corner subtriangle and the central
				// one has exactly 1/4 of the area.
				if ( u > 0.5f ) sub[0]++;
				else if ( v > 0.5f ) sub[1]++;
				else if ( w > 0.5f ) sub[2]++;
				else sub[3]++;
			}
			// Band explained: expected 25% each; N = 20000, sigma = 61 counts
			// (0.31%). Band 22%..28% is ±600 counts = ±9.8 sigma, while a
			// biased sampler (e.g. no fold, clustering at a corner) misses
			// it by far. Mean barycentric must be ~1/3 each (sigma 0.0017).
			for ( int k = 0; k < 4; k++ )
				Check( sub[k] >= 4400 && sub[k] <= 5600, $"subtriangle {k} got {sub[k]}/{n}, expected 4400..5600 (25% ± 3%)" );
			Near( mu / n, 1.0 / 3.0, 0.02, "mean barycentric u" );
			Near( mv / n, 1.0 / 3.0, 0.02, "mean barycentric v" );
			Near( mw / n, 1.0 / 3.0, 0.02, "mean barycentric w" );
		} );

		registry.Add( "particle-mesh-emit", "sampled points lie on their own triangles", () =>
		{
			var mesh = TwoTri();
			var s = ParticleMeshDensitySampler.FromVertexDensity( mesh, Ones( 4 ) );
			var rng = new ParticleRng( 31 );
			for ( int i = 0; i < 1000; i++ )
			{
				var sample = s.Sample( ref rng );
				Check( MathF.Abs( sample.Point.Z ) < 1e-5f, $"sample {sample.Point} off the z=0 plane" );
				mesh.GetTriangle( sample.Triangle, out var a, out var b, out var c );
				var (u, v, w) = Bary( a, b, c, sample.Point );
				Check( u >= -1e-4f && v >= -1e-4f && w >= -1e-4f && u + v + w <= 1 + 1e-4f, $"sample {sample.Point} outside triangle {sample.Triangle}" );
				Check( sample.Normal == new Vector3( 0, 0, 1 ), $"triangle normal {sample.Normal} is not +Z" );
			}
		} );

		registry.Add( "particle-mesh-emit", "zero-area triangle is never picked", () =>
		{
			var mesh = new ParticleMesh(
				new[] { new Vector3( 0, 0, 0 ), new Vector3( 1, 0, 0 ), new Vector3( 2, 0, 0 ), new Vector3( 0, 1, 0 ) },
				new[] { 0, 1, 2, 0, 1, 3 } );
			var s = ParticleMeshDensitySampler.FromVertexDensity( mesh, Ones( 4 ) );
			Near( s.Weights[0], 0.0, 1e-12, "degenerate triangle weight" );
			var rng = new ParticleRng( 3 );
			int dead = 0; const int n = 2000;
			for ( int i = 0; i < n; i++ ) if ( s.Sample( ref rng ).Triangle == 0 ) dead++;
			Check( dead == 0, $"degenerate triangle picked {dead}/{n} times, expected exactly 0" );
		} );

		registry.Add( "particle-mesh-emit", "degenerate inputs fail loudly", () =>
		{
			// Empty mesh: ParticleMesh itself rejects it before any sampler
			// could be built — the failure must be loud, not an empty sampler.
			Throws( () => new ParticleMesh( Array.Empty<Vector3>(), Array.Empty<int>() ), "empty mesh" );
			Throws( () => new ParticleMesh( new[] { new Vector3( 0, 0, 0 ) }, new[] { 0, 0 } ), "non-multiple-of-3 indices" );
			var mesh = TwoTri();
			Throws( () => ParticleMeshDensitySampler.FromVertexDensity( mesh, new[] { 0f, 0f, 0f, 0f } ), "all-zero density (zero total weight)" );
			Throws( () => ParticleMeshDensitySampler.FromVertexDensity( mesh, new[] { 1f, -1f, 1f, 1f } ), "negative density" );
			Throws( () => ParticleMeshDensitySampler.FromVertexDensity( mesh, new[] { 1f, float.NaN, 1f, 1f } ), "NaN density" );
			Throws( () => ParticleMeshDensitySampler.FromVertexDensity( mesh, new[] { 1f, 1f, 1f } ), "density length mismatch" );
			Throws( () => ParticleMeshDensitySampler.FromVertexColours( mesh, new[] { new Vector3( 1, 1, 1 ), new Vector3( -1, 0, 0 ), new Vector3( 1, 1, 1 ), new Vector3( 1, 1, 1 ) } ), "negative colour channel" );
			Throws( () => ParticleMeshDensitySampler.FromVertexDensity( null!, Ones( 4 ) ), "null mesh" );
			Throws( () => ParticleMeshDensitySampler.FromUvDensity( mesh, new[] { Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero }, null! ), "null density function" );
			Throws( () => ParticleMeshDensitySampler.FromUvDensity( mesh, new[] { Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero }, _ => -1f ), "negative UV density" );
			Throws( () => ParticleMeshDensitySampler.FromUvDensity( mesh, new[] { Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero }, _ => float.PositiveInfinity ), "non-finite UV density" );
			Throws( () => s_OutOfRange( mesh ), "triangle normal out of range" );
			static Vector3 s_OutOfRange( ParticleMesh m )
			{
				var s = ParticleMeshDensitySampler.FromVertexDensity( m, new[] { 1f, 1f, 1f, 1f } );
				return s.TriangleNormal( 99 );
			}
		} );

		registry.Add( "particle-mesh-emit", "CDF is monotonic and ends at total weight", () =>
		{
			var s = ParticleMeshDensitySampler.FromVertexDensity( TwoTri(), new[] { 1f, 1f, 1f, 3f } );
			double sum = 0;
			for ( int t = 0; t < s.TriangleCount; t++ )
			{
				sum += s.Weights[t];
				if ( t > 0 ) Check( s.Cdf[t] >= s.Cdf[t - 1], $"CDF decreases at {t}" );
				Near( s.Cdf[t], sum, 1e-9, $"CDF[{t}]" );
			}
			Near( s.Cdf[s.TriangleCount - 1], s.TotalWeight, 1e-9, "CDF last == TotalWeight" );
			Near( s.TotalWeight, sum, 1e-9, "TotalWeight == weight sum" );
		} );
	}
}
