using System;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>
/// Self-contained 3D spawn-distribution generators (wave-6 lane P32).
///
/// Every generator is a pure function of a seeded <see cref="ParticleRng"/>
/// (or, for the Fibonacci sphere, fully deterministic) returning N spawn
/// points inside a given volume or on a given surface. Kernel inches,
/// Z-up, matching <see cref="ParticleSystem"/>.
///
/// Conventions, all documented per method:
/// <list type="bullet">
/// <item>Box <c>size</c> is the FULL extent per axis; the box spans
/// <c>center ± size / 2</c> on each axis. A zero component is a flat box
/// (all points share that coordinate); a negative component is rejected.</item>
/// <item>Iteration order is x-fastest, z-slowest, so when a count-based
/// generator's lattice holds more cells than N, the dropped remainder is
/// always the tail: the highest-z layer first, then the highest-y row.
/// The returned array always has exactly N points.</item>
/// <item>No <see cref="System.Random"/> anywhere: determinism comes from
/// <see cref="ParticleRng"/>, the same SplitMix64/xorshift RNG the sim uses.</item>
/// </list>
///
/// These are BATCH generators: they hand back an array of positions.
/// They are NOT wired into <see cref="ParticleSystem"/>'s per-particle
/// spawn step — that step draws one particle at a time, so the sweep
/// integration must add a distribution queue to the emitter (see the
/// integration lines in the lane report), not a one-line drop-in.
/// </summary>
public static class ParticleDistributions
{
	/// <summary>Golden angle, π·(3 − √5), the Fibonacci-sphere step.</summary>
	public static readonly float GoldenAngle = MathF.PI * (3f - MathF.Sqrt( 5f ));

	// ----- validation (loud, ArgumentException, codebase style) -----

	/// <summary>
	/// Rejects non-positive counts, negative or non-finite sizes, and
	/// jitter outside [0, 1]. Called by every generator below, and
	/// available to authoring/validation code that checks a
	/// distribution before generating it.
	/// </summary>
	public static void Validate( int count, Vector3 size, float jitter )
	{
		ValidateCount( count );
		ValidateSize( size );
		if ( !float.IsFinite( jitter ) || jitter < 0 || jitter > 1 )
			throw new ArgumentException( $"distribution: jitter must be in [0, 1], got {jitter}." );
	}

	/// <summary>Rejects non-positive counts and negative or non-finite radii.</summary>
	public static void ValidateSphere( int count, float radius )
	{
		ValidateCount( count );
		if ( !float.IsFinite( radius ) || radius < 0 )
			throw new ArgumentException( $"distribution: sphere radius must be finite and >= 0, got {radius}." );
	}

	/// <summary>Rejects non-positive strata counts and negative or non-finite sizes.</summary>
	public static void ValidateStrata( int strataX, int strataY, int strataZ, Vector3 size )
	{
		if ( strataX < 1 || strataY < 1 || strataZ < 1 )
			throw new ArgumentException( $"distribution: strata counts must each be >= 1, got ({strataX}, {strataY}, {strataZ})." );
		ValidateSize( size );
	}

	static void ValidateCount( int count )
	{
		if ( count < 1 )
			throw new ArgumentException( $"distribution: count must be >= 1, got {count}." );
	}

	static void ValidateSize( Vector3 size )
	{
		if ( !ParticleMath.Finite( size ) || size.X < 0 || size.Y < 0 || size.Z < 0 )
			throw new ArgumentException( $"distribution: size components must be finite and >= 0, got {size}." );
	}

	// ----- axis-count selection (shared remainder behaviour) -----

	/// <summary>
	/// Picks balanced per-axis cell counts for a count-based box
	/// generator. Counts start proportional to the box side lengths
	/// (a zero side gets count 1, not 0) and the axis with the coarsest
	/// spacing is refined until the product is &gt;= <paramref name="count"/>.
	/// The product may exceed <paramref name="count"/>; callers truncate
	/// the tail (highest-z first), so the remainder behaviour is the
	/// same for <see cref="JitteredBox"/>, <see cref="GridBox(int, Vector3, Vector3)"/>
	/// and the count-based <see cref="StratifiedBox(ref ParticleRng, int, Vector3, Vector3)"/>.
	/// </summary>
	public static (int X, int Y, int Z) GridAxisCounts( int count, Vector3 size )
	{
		ValidateCount( count );
		ValidateSize( size );
		float sx = size.X > 0 ? size.X : 1f, sy = size.Y > 0 ? size.Y : 1f, sz = size.Z > 0 ? size.Z : 1f;
		double k = Math.Cbrt( count / (double)(sx * sy * sz) );
		int nx = Math.Max( 1, (int)Math.Round( k * sx ) );
		int ny = Math.Max( 1, (int)Math.Round( k * sy ) );
		int nz = Math.Max( 1, (int)Math.Round( k * sz ) );
		while ( (long)nx * ny * nz < count )
		{
			// Refine the axis with the largest current cell spacing.
			float spx = sx / nx, spy = sy / ny, spz = sz / nz;
			if ( spx >= spy && spx >= spz ) nx++;
			else if ( spy >= spz ) ny++;
			else nz++;
		}
		return (nx, ny, nz);
	}

	// ----- 1. jittered box -----

	/// <summary>
	/// Uniform random points in a box, one per grid cell.
	/// The box is divided into the <see cref="GridAxisCounts"/> cells;
	/// each point starts at its cell centre and is offset by a uniform
	/// random amount up to <paramref name="jitter"/> × half the cell
	/// size per axis. jitter = 0 is the exact grid (identical to
	/// <see cref="GridBoxExact"/> with the same axis counts);
	/// jitter = 1 is fully random within each cell (offsets span the
	/// whole cell, so neighbouring cells' ranges just touch, never
	/// overlap). Intermediate jitter keeps the grid's even coverage
	/// while breaking its regularity. If the cell product exceeds
	/// <paramref name="count"/>, the tail cells are dropped, so the
	/// last z layer may be partial — see <see cref="GridAxisCounts"/>.
	/// </summary>
	public static Vector3[] JitteredBox( ref ParticleRng rng, int count, Vector3 center, Vector3 size, float jitter )
	{
		Validate( count, size, jitter );
		var (nx, ny, nz) = GridAxisCounts( count, size );
		var cell = new Vector3( size.X / nx, size.Y / ny, size.Z / nz );
		var half = size * 0.5f;
		var points = new Vector3[count];
		int at = 0;
		for ( int z = 0; z < nz && at < count; z++ )
		for ( int y = 0; y < ny && at < count; y++ )
		for ( int x = 0; x < nx && at < count; x++ )
		{
			var p = center - half + new Vector3( (x + 0.5f) * cell.X, (y + 0.5f) * cell.Y, (z + 0.5f) * cell.Z );
			if ( jitter > 0 )
				p += new Vector3(
					(rng.NextFloat() * 2 - 1) * jitter * cell.X * 0.5f,
					(rng.NextFloat() * 2 - 1) * jitter * cell.Y * 0.5f,
					(rng.NextFloat() * 2 - 1) * jitter * cell.Z * 0.5f );
			points[at++] = p;
		}
		return points;
	}

	// ----- 2. grid box -----

	/// <summary>
	/// Regular lattice of cell-centre points in a box, exactly
	/// <c>nx × ny × nz</c> points. Cell centres, not corners: axis i
	/// sits at <c>center − size/2 + (i + 0.5) · size/n</c>, so a 2-count
	/// axis in a size-4 box lands at ±1, and a 1-count axis lands
	/// exactly on the centre. No RNG is consumed.
	/// </summary>
	public static Vector3[] GridBoxExact( Vector3 center, Vector3 size, int nx, int ny, int nz )
	{
		ValidateStrata( nx, ny, nz, size );
		var cell = new Vector3( size.X / nx, size.Y / ny, size.Z / nz );
		var half = size * 0.5f;
		var points = new Vector3[nx * ny * nz];
		int at = 0;
		for ( int z = 0; z < nz; z++ )
		for ( int y = 0; y < ny; y++ )
		for ( int x = 0; x < nx; x++ )
			points[at++] = center - half + new Vector3( (x + 0.5f) * cell.X, (y + 0.5f) * cell.Y, (z + 0.5f) * cell.Z );
		return points;
	}

	/// <summary>
	/// Regular lattice in a box for an arbitrary count N. Axis counts
	/// come from <see cref="GridAxisCounts"/>; when their product
	/// exceeds N, the tail cells are dropped (highest-z layer first),
	/// so the returned array has exactly N points but the final layer
	/// may be partial. For an exact, complete lattice use
	/// <see cref="GridBoxExact"/> with counts whose product is N.
	/// </summary>
	public static Vector3[] GridBox( int count, Vector3 center, Vector3 size )
	{
		ValidateCount( count );
		ValidateSize( size );
		var (nx, ny, nz) = GridAxisCounts( count, size );
		var all = GridBoxExact( center, size, nx, ny, nz );
		if ( all.Length == count ) return all;
		var points = new Vector3[count];
		Array.Copy( all, points, count );
		return points;
	}

	// ----- 3. Fibonacci sphere -----

	/// <summary>
	/// Near-evenly distributed points on a sphere surface: the standard
	/// golden-angle spiral. Point i (0-based) has polar coordinate
	/// <c>y = 1 − 2i/(N−1)</c> along +Z and azimuth
	/// <c>GoldenAngle · i</c>, so the points run pole to pole with
	/// successive points stepped by the golden angle — no two points
	/// share a latitude band and no RNG is needed. N = 1 returns the
	/// north pole (+Z); N = 2 returns both poles. Fully deterministic.
	/// </summary>
	public static Vector3[] FibonacciSphere( int count, Vector3 center, float radius )
	{
		ValidateSphere( count, radius );
		var points = new Vector3[count];
		if ( count == 1 ) { points[0] = center + new Vector3( 0, 0, radius ); return points; }
		for ( int i = 0; i < count; i++ )
		{
			float y = 1f - 2f * i / (count - 1);
			float r = MathF.Sqrt( MathF.Max( 0, 1 - y * y ) );
			float theta = GoldenAngle * i;
			points[i] = center + radius * new Vector3( r * MathF.Cos( theta ), r * MathF.Sin( theta ), y );
		}
		return points;
	}

	/// <summary>
	/// Seeded variant of <see cref="FibonacciSphere(int, Vector3, float)"/>:
	/// the same spiral, rotated about the Z (polar) axis by one uniform
	/// random phase drawn from <paramref name="rng"/>. Rotation about
	/// the polar axis preserves every nearest-neighbour distance, so
	/// evenness is identical to the deterministic version; only the
	/// absolute orientation changes, and it is reproducible for a
	/// fixed seed.
	/// </summary>
	public static Vector3[] FibonacciSphere( ref ParticleRng rng, int count, Vector3 center, float radius )
	{
		float phase = rng.NextFloat() * MathF.PI * 2;
		var points = FibonacciSphere( count, center, radius );
		float c = MathF.Cos( phase ), s = MathF.Sin( phase );
		for ( int i = 0; i < points.Length; i++ )
		{
			var rel = points[i] - center;
			points[i] = center + new Vector3( rel.X * c - rel.Y * s, rel.X * s + rel.Y * c, rel.Z );
		}
		return points;
	}

	// ----- 4. stratified box -----

	/// <summary>
	/// Stratified sampling of a box: the volume is divided into
	/// <c>strataX × strataY × strataZ</c> strata along the box axes
	/// (X, Y, Z — the stratification axes), and exactly one uniform
	/// random point is drawn inside each stratum. Unlike pure random
	/// sampling, no stratum is ever empty or double-filled, so clumps
	/// and voids larger than one stratum cannot occur; within a
	/// stratum the sample is fully uniform. Returns exactly
	/// strataX × strataY × strataZ points.
	/// </summary>
	public static Vector3[] StratifiedBox( ref ParticleRng rng, Vector3 center, Vector3 size, int strataX, int strataY, int strataZ )
	{
		ValidateStrata( strataX, strataY, strataZ, size );
		var cell = new Vector3( size.X / strataX, size.Y / strataY, size.Z / strataZ );
		var lo = center - size * 0.5f;
		var points = new Vector3[strataX * strataY * strataZ];
		int at = 0;
		for ( int z = 0; z < strataZ; z++ )
		for ( int y = 0; y < strataY; y++ )
		for ( int x = 0; x < strataX; x++ )
			points[at++] = lo + new Vector3( (x + rng.NextFloat()) * cell.X, (y + rng.NextFloat()) * cell.Y, (z + rng.NextFloat()) * cell.Z );
		return points;
	}

	/// <summary>
	/// Stratified sampling for an arbitrary count N: strata counts
	/// come from <see cref="GridAxisCounts"/> and one sample is drawn
	/// per stratum; when the strata product exceeds N, the tail
	/// strata are dropped (highest-z first), so the returned array
	/// has exactly N points but the dropped strata are unsampled.
	/// For guaranteed one-sample-per-stratum coverage use the
	/// strata-count overload.
	/// </summary>
	public static Vector3[] StratifiedBox( ref ParticleRng rng, int count, Vector3 center, Vector3 size )
	{
		ValidateCount( count );
		ValidateSize( size );
		var (nx, ny, nz) = GridAxisCounts( count, size );
		var all = StratifiedBox( ref rng, center, size, nx, ny, nz );
		if ( all.Length == count ) return all;
		var points = new Vector3[count];
		Array.Copy( all, points, count );
		return points;
	}
}
