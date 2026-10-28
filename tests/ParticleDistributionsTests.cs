using System.Numerics;
using Marionette.Desktop;

// Wave-6 lane P32 tests. Group: particle-distributions.
// Sweep line (scratch only — do NOT add to the canonical TestRegistry.cs):
//   ParticleDistributionsTests.Register( registry, settings );
// after the GpuParticleSystemTests.Register line.

internal static class ParticleDistributionsTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; }
			catch ( InvalidOperationException ) { return; }
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}
		static bool Inside( Vector3 p, Vector3 center, Vector3 size )
		{
			var half = size * 0.5f;
			return p.X >= center.X - half.X - 1e-4f && p.X <= center.X + half.X + 1e-4f
				&& p.Y >= center.Y - half.Y - 1e-4f && p.Y <= center.Y + half.Y + 1e-4f
				&& p.Z >= center.Z - half.Z - 1e-4f && p.Z <= center.Z + half.Z + 1e-4f;
		}
		static float MinMaxNn( Vector3[] pts, out float min, out float max )
		{
			min = float.MaxValue; max = 0; double sum = 0; int n = 0;
			for ( int i = 0; i < pts.Length; i++ )
			{
				float best = float.MaxValue;
				for ( int j = 0; j < pts.Length; j++ )
					if ( j != i ) best = MathF.Min( best, (pts[i] - pts[j]).Length() );
				min = MathF.Min( min, best ); max = MathF.Max( max, best ); sum += best; n++;
			}
			return (float)(sum / n);
		}
		var Center = new Vector3( 10, -20, 30 );
		var Size = new Vector3( 4, 6, 8 );

		registry.Add( "particle-distributions", "jittered box: exact count, all inside, deterministic", () =>
		{
			var rng = new ParticleRng( 7 );
			var pts = ParticleDistributions.JitteredBox( ref rng, 500, Center, Size, 0.8f );
			Check( pts.Length == 500, $"expected 500 points, got {pts.Length}" );
			for ( int i = 0; i < pts.Length; i++ )
				Check( Inside( pts[i], Center, Size ), $"jittered point {i} {pts[i]} outside box" );
			var rng2 = new ParticleRng( 7 );
			var again = ParticleDistributions.JitteredBox( ref rng2, 500, Center, Size, 0.8f );
			for ( int i = 0; i < pts.Length; i++ )
				Check( pts[i] == again[i], $"same seed differs at point {i}" );
			var rng3 = new ParticleRng( 8 );
			var other = ParticleDistributions.JitteredBox( ref rng3, 500, Center, Size, 0.8f );
			int diff = 0;
			for ( int i = 0; i < pts.Length; i++ ) if ( pts[i] != other[i] ) diff++;
			Check( diff > 400, $"different seed should differ almost everywhere, only {diff}/500 differ" );
		} );

		registry.Add( "particle-distributions", "jittered jitter 0 equals the exact grid", () =>
		{
			var rng = new ParticleRng( 3 );
			var jittered = ParticleDistributions.JitteredBox( ref rng, 8, Vector3.Zero, new Vector3( 4, 4, 4 ), 0f );
			var (nx, ny, nz) = ParticleDistributions.GridAxisCounts( 8, new Vector3( 4, 4, 4 ) );
			Check( nx == 2 && ny == 2 && nz == 2, $"cube count 8 should give 2x2x2 axes, got {nx}x{ny}x{nz}" );
			var grid = ParticleDistributions.GridBoxExact( Vector3.Zero, new Vector3( 4, 4, 4 ), nx, ny, nz );
			Check( jittered.Length == grid.Length, "length mismatch" );
			for ( int i = 0; i < grid.Length; i++ )
				Check( jittered[i] == grid[i], $"jitter-0 point {i} {jittered[i]} != grid {grid[i]}" );
		} );

		registry.Add( "particle-distributions", "jittered jitter 1 leaves the grid almost everywhere", () =>
		{
			var rng = new ParticleRng( 11 );
			var pts = ParticleDistributions.JitteredBox( ref rng, 200, Vector3.Zero, new Vector3( 10, 10, 10 ), 1f );
			var grid = ParticleDistributions.GridBox( 200, Vector3.Zero, new Vector3( 10, 10, 10 ) );
			int offGrid = 0;
			for ( int i = 0; i < pts.Length; i++ )
				if ( pts[i] != grid[i] ) offGrid++;
			Check( offGrid > 190, $"jitter 1 should move nearly every point off its cell centre, only {offGrid}/200 moved" );
			for ( int i = 0; i < pts.Length; i++ )
				Check( Inside( pts[i], Vector3.Zero, new Vector3( 10, 10, 10 ) ), $"jitter-1 point {i} outside box" );
		} );

		registry.Add( "particle-distributions", "grid exact: 2x2x2 cell centres and exact spacing", () =>
		{
			var pts = ParticleDistributions.GridBoxExact( Vector3.Zero, new Vector3( 4, 4, 4 ), 2, 2, 2 );
			Check( pts.Length == 8, $"expected 8 points, got {pts.Length}" );
			var want = new HashSet<Vector3>();
			foreach ( float x in new[] { -1f, 1f } )
			foreach ( float y in new[] { -1f, 1f } )
			foreach ( float z in new[] { -1f, 1f } )
				want.Add( new Vector3( x, y, z ) );
			foreach ( var p in pts )
				Check( want.Remove( p ), $"unexpected or duplicate grid point {p}" );
			Check( want.Count == 0, $"missing grid points: {want.Count}" );
			var line = ParticleDistributions.GridBoxExact( Vector3.Zero, new Vector3( 9, 0, 0 ), 3, 1, 1 );
			Near( line[0].X, -3, 1e-5f, "3-grid first x" );
			Near( line[1].X, 0, 1e-5f, "3-grid middle x" );
			Near( line[2].X, 3, 1e-5f, "3-grid last x" );
		} );

		registry.Add( "particle-distributions", "grid count: exact N for a non-product count, all inside", () =>
		{
			var pts = ParticleDistributions.GridBox( 100, Center, Size );
			Check( pts.Length == 100, $"expected exactly 100 points, got {pts.Length}" );
			var (nx, ny, nz) = ParticleDistributions.GridAxisCounts( 100, Size );
			Check( nx * ny * nz >= 100, $"axis product {nx}x{ny}x{nz} must cover 100" );
			for ( int i = 0; i < pts.Length; i++ )
				Check( Inside( pts[i], Center, Size ), $"grid point {i} outside box" );
			// Remainder rule: the kept prefix is the full grid's head, so the
			// last kept point is not past the dropped tail's first cell.
			var full = ParticleDistributions.GridBoxExact( Center, Size, nx, ny, nz );
			for ( int i = 0; i < pts.Length; i++ )
				Check( pts[i] == full[i], $"grid-count point {i} is not the full-grid prefix (tail-drop rule)" );
		} );

		registry.Add( "particle-distributions", "fibonacci sphere: all on surface, poles for N=1 and N=2", () =>
		{
			var one = ParticleDistributions.FibonacciSphere( 1, Center, 5f );
			Near( (one[0] - Center).Length(), 5f, 1e-4f, "N=1 radius" );
			Check( one[0] == Center + new Vector3( 0, 0, 5 ), "N=1 should be the north pole" );
			var two = ParticleDistributions.FibonacciSphere( 2, Center, 5f );
			Check( two[0] == Center + new Vector3( 0, 0, 5 ) && two[1] == Center - new Vector3( 0, 0, 5 ), "N=2 should be both poles" );
			var pts = ParticleDistributions.FibonacciSphere( 300, Center, 2.5f );
			Check( pts.Length == 300, "length" );
			for ( int i = 0; i < pts.Length; i++ )
				Near( (pts[i] - Center).Length(), 2.5f, 1e-4f, $"fibonacci point {i} radius" );
			// No duplicates: every pair is separated.
			float mean = MinMaxNn( pts, out float min, out float max );
			Check( min > 0.1f, $"fibonacci points too close together: min NN {min}" );
			Check( max / min < 2.5f, $"fibonacci NN spread too wide for even coverage: min {min}, max {max}, mean {mean}" );
		} );

		registry.Add( "particle-distributions", "fibonacci sphere: evenness beats random, seeded phase preserves it", () =>
		{
			const int n = 500;
			var fib = ParticleDistributions.FibonacciSphere( n, Vector3.Zero, 1f );
			float fibMean = MinMaxNn( fib, out float fibMin, out float fibMax );
			// Pure-random comparison, same N, fixed seed.
			var rng = new ParticleRng( 99 );
			var rand = new Vector3[n];
			for ( int i = 0; i < n; i++ )
			{
				float z = rng.NextFloat() * 2 - 1, a = rng.NextFloat() * MathF.PI * 2, r = MathF.Sqrt( 1 - z * z );
				rand[i] = new Vector3( r * MathF.Cos( a ), r * MathF.Sin( a ), z );
			}
			float randMean = MinMaxNn( rand, out float randMin, out float randMax );
			Check( fibMin > 2 * randMin, $"fibonacci min NN {fibMin} should be well above random min NN {randMin}" );
			Check( fibMax / fibMin < randMax / randMin, $"fibonacci spread {fibMax / fibMin} should be tighter than random spread {randMax / randMin}" );
			// Seeded phase: same seed identical, different seed differs, NN unchanged, still on sphere.
			var r1 = new ParticleRng( 5 );
			var a1 = ParticleDistributions.FibonacciSphere( ref r1, n, Vector3.Zero, 1f );
			var r2 = new ParticleRng( 5 );
			var a2 = ParticleDistributions.FibonacciSphere( ref r2, n, Vector3.Zero, 1f );
			var r3 = new ParticleRng( 6 );
			var a3 = ParticleDistributions.FibonacciSphere( ref r3, n, Vector3.Zero, 1f );
			for ( int i = 0; i < n; i++ )
			{
				Check( a1[i] == a2[i], $"seeded fibonacci same seed differs at {i}" );
				Near( (a1[i]).Length(), 1f, 1e-4f, $"seeded fibonacci point {i} radius" );
			}
			int diff = 0;
			for ( int i = 0; i < n; i++ ) if ( a1[i] != a3[i] ) diff++;
			Check( diff > n / 2, $"different phase seed should rotate most points, only {diff}/{n} differ" );
			float _ = MinMaxNn( a1, out float pMin, out float pMax );
			Near( pMin, fibMin, 1e-4f, "phase rotation must preserve min NN" );
			Near( pMax, fibMax, 1e-4f, "phase rotation must preserve max NN" );
			// Report the measured evenness numbers in the failure text path only;
			// the passing values are: fibMean, randMean recorded by the runner log.
			Check( fibMean > randMean, $"fibonacci mean NN {fibMean} should exceed random mean NN {randMean}" );
		} );

		registry.Add( "particle-distributions", "stratified: exactly one sample per stratum, full coverage", () =>
		{
			var rng = new ParticleRng( 21 );
			var pts = ParticleDistributions.StratifiedBox( ref rng, Vector3.Zero, new Vector3( 8, 6, 4 ), 4, 3, 2 );
			Check( pts.Length == 24, $"expected 4x3x2=24 points, got {pts.Length}" );
			var seen = new HashSet<int>();
			var cell = new Vector3( 8f / 4, 6f / 3, 4f / 2 );
			var lo = new Vector3( -4, -3, -2 );
			foreach ( var p in pts )
			{
				Check( Inside( p, Vector3.Zero, new Vector3( 8, 6, 4 ) ), $"stratified point {p} outside box" );
				int ix = Math.Min( 3, (int)MathF.Floor( (p.X - lo.X) / cell.X ) );
				int iy = Math.Min( 2, (int)MathF.Floor( (p.Y - lo.Y) / cell.Y ) );
				int iz = Math.Min( 1, (int)MathF.Floor( (p.Z - lo.Z) / cell.Z ) );
				Check( seen.Add( ix + 4 * iy + 12 * iz ), $"two stratified samples in stratum ({ix},{iy},{iz})" );
			}
			Check( seen.Count == 24, $"stratified coverage incomplete: {seen.Count}/24 strata hit" );
			var rng2 = new ParticleRng( 21 );
			var again = ParticleDistributions.StratifiedBox( ref rng2, Vector3.Zero, new Vector3( 8, 6, 4 ), 4, 3, 2 );
			for ( int i = 0; i < pts.Length; i++ )
				Check( pts[i] == again[i], $"same seed stratified differs at {i}" );
		} );

		registry.Add( "particle-distributions", "stratified count: exact N, all inside", () =>
		{
			var rng = new ParticleRng( 31 );
			var pts = ParticleDistributions.StratifiedBox( ref rng, 100, Center, Size );
			Check( pts.Length == 100, $"expected exactly 100 points, got {pts.Length}" );
			for ( int i = 0; i < pts.Length; i++ )
				Check( Inside( pts[i], Center, Size ), $"stratified-count point {i} outside box" );
		} );

		registry.Add( "particle-distributions", "validate rejects bad inputs loudly, accepts edges", () =>
		{
			var rng = new ParticleRng( 1 );
			Throws( () => ParticleDistributions.JitteredBox( ref rng, 0, Vector3.Zero, Size, 0.5f ), "N=0" );
			Throws( () => ParticleDistributions.JitteredBox( ref rng, -5, Vector3.Zero, Size, 0.5f ), "N negative" );
			Throws( () => ParticleDistributions.JitteredBox( ref rng, 10, Vector3.Zero, new Vector3( -1, 1, 1 ), 0.5f ), "negative size X" );
			Throws( () => ParticleDistributions.JitteredBox( ref rng, 10, Vector3.Zero, new Vector3( 1, 1, -2 ), 0.5f ), "negative size Z" );
			Throws( () => ParticleDistributions.JitteredBox( ref rng, 10, Vector3.Zero, Size, -0.1f ), "jitter below 0" );
			Throws( () => ParticleDistributions.JitteredBox( ref rng, 10, Vector3.Zero, Size, 1.1f ), "jitter above 1" );
			Throws( () => ParticleDistributions.JitteredBox( ref rng, 10, Vector3.Zero, Size, float.NaN ), "jitter NaN" );
			Throws( () => ParticleDistributions.FibonacciSphere( 0, Vector3.Zero, 1f ), "sphere N=0" );
			Throws( () => ParticleDistributions.FibonacciSphere( 10, Vector3.Zero, -1f ), "negative radius" );
			Throws( () => ParticleDistributions.GridBoxExact( Vector3.Zero, Size, 0, 2, 2 ), "grid axis 0" );
			Throws( () => ParticleDistributions.StratifiedBox( ref rng, Vector3.Zero, Size, 2, 0, 2 ), "strata axis 0" );
			Throws( () => ParticleDistributions.Validate( 5, Size, 2f ), "Validate jitter" );
			// Edges that must pass: jitter 0 and 1, zero radius, zero size component.
			ParticleDistributions.Validate( 1, new Vector3( 0, 1, 1 ), 0f );
			ParticleDistributions.Validate( 1, new Vector3( 0, 1, 1 ), 1f );
			ParticleDistributions.ValidateSphere( 1, 0f );
			var flat = ParticleDistributions.JitteredBox( ref rng, 4, Vector3.Zero, new Vector3( 0, 2, 2 ), 1f );
			Check( flat.Length == 4, "flat box count" );
			foreach ( var p in flat ) Near( p.X, 0, 1e-6f, "flat box X must be 0" );
		} );

		registry.Add( "particle-distributions", "large count stays inside and fast", () =>
		{
			var rng = new ParticleRng( 77 );
			var pts = ParticleDistributions.JitteredBox( ref rng, 10000, Vector3.Zero, new Vector3( 20, 20, 20 ), 0.5f );
			Check( pts.Length == 10000, "length" );
			for ( int i = 0; i < pts.Length; i += 37 )
				Check( Inside( pts[i], Vector3.Zero, new Vector3( 20, 20, 20 ) ), $"large jittered point {i} outside" );
			var fib = ParticleDistributions.FibonacciSphere( 10000, Vector3.Zero, 3f );
			for ( int i = 0; i < fib.Length; i += 53 )
				Near( fib[i].Length(), 3f, 1e-3f, $"large fibonacci point {i} radius" );
		} );
	}
}
