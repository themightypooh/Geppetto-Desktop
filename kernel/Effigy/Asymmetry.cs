using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Biological asymmetry, S&amp;box Lane 4a (spec docs/SBOX-NATIVE-AI-ASSET-MAKER-LANES.md,
/// Lane 4, asymmetry operator only — tendon bundles are a separate file, not this one).
///
/// Domain-warped 3D simplex noise, low frequency, gentle amplitude:
/// <c>p' = p + Noise(p * f) * A</c>, where the noise coordinate itself is first warped by a
/// second, lower-frequency noise field. The warp is what keeps the result organic instead of
/// looking like a regular ripple: straight ridges bend, so a spine, ribcage, horn, or facial
/// contour stops reading as mirrored or machine-made.
///
/// WATERTIGHTNESS, BY CONSTRUCTION: displacement is a pure function of position. Two faces
/// sharing a vertex share that vertex's index, so they move together — face indices, edge
/// counts, and boundary edges are never touched. This file deliberately does NOT depend on
/// <c>PolyMesh</c>: callers apply it to <c>mesh.Positions</c> (a <c>List&lt;Vec3&gt;</c>), which
/// keeps the kernel file dependency-free and keeps every other mesh channel (faces, UVs,
/// skin weights, morphs) exactly where it was. The one thing it cannot promise on its own
/// is that a displacement never folds a face on a very coarse mesh: keep Amplitude small
/// relative to the mesh's shortest edge, which the defaults do for body-scale models
/// (see <see cref="AsymmetryOptions"/>). The tests pin that on real test meshes.
///
/// DETERMINISM: the noise permutation is shuffled from the seed alone (SplitMix32, no
/// <c>System.Random</c>, no clock, no hash-code randomisation), so the same seed and the
/// same point give the same displacement on every run and every platform.
/// </summary>
public sealed class AsymmetryOptions
{
	/// <summary>Noise seed. Same seed + same point = same displacement, always.</summary>
	public int Seed { get; set; } = 1337;

	/// <summary>
	/// Base noise frequency, in 1/model-units. Default 0.08: on an inch-scale body model one
	/// noise wavelength is ~12 units, so a torso gets a few broad swells, not a buzz.
	/// Must be &gt; 0.
	/// </summary>
	public float Frequency { get; set; } = 0.08f;

	/// <summary>
	/// Frequency of the domain-warp field. Lower than <see cref="Frequency"/> on purpose:
	/// the warp bends the noise field slowly, it does not add detail of its own.
	/// Must be &gt; 0.
	/// </summary>
	public float WarpFrequency { get; set; } = 0.03f;

	/// <summary>
	/// How far the warp field shifts the noise coordinate, in noise-coordinate units
	/// (dimensionless, not model units). 0 = plain simplex, no warp. Default 0.5: enough
	/// to bend ridges visibly, small enough that the field stays low-frequency.
	/// Must be &gt;= 0.
	/// </summary>
	public float WarpStrength { get; set; } = 0.5f;

	/// <summary>
	/// Peak displacement per axis, in model units. Default 0.35: on a 69-unit human that is
	/// about half a percent of height — visible asymmetry, not a deformity. Must be &gt;= 0.
	/// 0 = identity (useful as a no-op check, and tested as one).
	/// </summary>
	public float Amplitude { get; set; } = 0.35f;

	/// <summary>A fresh options object carrying every default above.</summary>
	public static AsymmetryOptions Default => new();
}

/// <summary>
/// The Lane 4a asymmetry operator. All methods are static and side-effect-free except
/// <see cref="ApplyTo"/>, which rewrites the list it is given in place.
/// </summary>
public static class Asymmetry
{
	/// <summary>
	/// The displacement applied to <paramref name="p"/>: the domain-warped noise vector
	/// times <see cref="AsymmetryOptions.Amplitude"/>. Each component lies in
	/// [-Amplitude, Amplitude]; see <see cref="MaxDisplacement"/>.
	/// </summary>
	public static Vec3 Displacement( Vec3 p, AsymmetryOptions options = null )
	{
		var o = Effective( options );
		var noise = new AsymmetryNoise( o.Seed );
		return Displacement( p, o, noise );
	}

	/// <summary>The warped point itself: <c>p + Displacement(p)</c>.</summary>
	public static Vec3 WarpPoint( Vec3 p, AsymmetryOptions options = null ) =>
		p + Displacement( p, options );

	/// <summary>
	/// Every point warped, in order, into a fresh array. The input is never modified.
	/// </summary>
	public static Vec3[] WarpPoints( IReadOnlyList<Vec3> points, AsymmetryOptions options = null )
	{
		if ( points is null )
			throw new ArgumentNullException( nameof( points ) );
		var o = Effective( options );
		var noise = new AsymmetryNoise( o.Seed );
		var outPoints = new Vec3[points.Count];
		for ( var i = 0; i < points.Count; i++ )
			outPoints[i] = points[i] + Displacement( points[i], o, noise );
		return outPoints;
	}

	/// <summary>
	/// Rewrites <paramref name="positions"/> in place — pass <c>mesh.Positions</c> and the
	/// mesh's topology, UVs, skin weights, and morphs are untouched, because none of them
	/// are written here. <paramref name="weight"/>, when given, scales the displacement
	/// per point (clamped to [0, 1]): 0 leaves a point exactly where it was, so a caller
	/// can pin a seam, a socket, or a mirror plane while the rest of the body warps.
	/// </summary>
	public static void ApplyTo( IList<Vec3> positions, AsymmetryOptions options = null, Func<Vec3, float> weight = null )
	{
		if ( positions is null )
			throw new ArgumentNullException( nameof( positions ) );
		var o = Effective( options );
		var noise = new AsymmetryNoise( o.Seed );
		for ( var i = 0; i < positions.Count; i++ )
		{
			var p = positions[i];
			var w = 1f;
			if ( weight is not null )
			{
				w = weight( p );
				if ( float.IsNaN( w ) )
					w = 0f;
				w = MathF.Max( 0f, MathF.Min( 1f, w ) );
			}
			positions[i] = w == 0f ? p : p + Displacement( p, o, noise ) * w;
		}
	}

	/// <summary>
	/// Hard bound on any displacement vector's length: every noise component is clamped
	/// to [-1, 1], so the vector length can never exceed Amplitude * sqrt(3).
	/// </summary>
	public static float MaxDisplacement( AsymmetryOptions options = null ) =>
		Effective( options ).Amplitude * MathF.Sqrt( 3f );

	static AsymmetryOptions Effective( AsymmetryOptions options )
	{
		var o = options ?? AsymmetryOptions.Default;
		if ( !float.IsFinite( o.Frequency ) || o.Frequency <= 0f )
			throw new ArgumentException( $"Frequency must be > 0, got {o.Frequency}.", nameof( options ) );
		if ( !float.IsFinite( o.WarpFrequency ) || o.WarpFrequency <= 0f )
			throw new ArgumentException( $"WarpFrequency must be > 0, got {o.WarpFrequency}.", nameof( options ) );
		if ( !float.IsFinite( o.WarpStrength ) || o.WarpStrength < 0f )
			throw new ArgumentException( $"WarpStrength must be >= 0, got {o.WarpStrength}.", nameof( options ) );
		if ( !float.IsFinite( o.Amplitude ) || o.Amplitude < 0f )
			throw new ArgumentException( $"Amplitude must be >= 0, got {o.Amplitude}.", nameof( options ) );
		return o;
	}

	static Vec3 Displacement( Vec3 p, AsymmetryOptions o, AsymmetryNoise noise )
	{
		// Domain warp, in noise-coordinate units: sample the warp field at the low
		// warp frequency, then shift the main sample coordinate by it. WarpStrength 0
		// short-circuits to plain simplex, which the tests pin against a warped run.
		var coord = p * o.Frequency;
		if ( o.WarpStrength > 0f )
			coord += noise.Vector( p * o.WarpFrequency ) * o.WarpStrength;
		return noise.Vector( coord ) * o.Amplitude;
	}
}

/// <summary>
/// Seeded 3D simplex noise (Gustavson-style, reimplemented here, no engine types).
/// One instance per warp operation, never one per vertex: the permutation table is
/// built once from the seed, then sampling is pure arithmetic.
/// </summary>
internal sealed class AsymmetryNoise
{
	// Skew / unskew factors for 3D simplex.
	const float F3 = 1f / 3f;
	const float G3 = 1f / 6f;

	// The 12 edge directions of a cube: simplex gradients, each of length sqrt(2).
	static readonly Vec3[] Grad = new Vec3[]
	{
		new( 1, 1, 0 ), new( -1, 1, 0 ), new( 1, -1, 0 ), new( -1, -1, 0 ),
		new( 1, 0, 1 ), new( -1, 0, 1 ), new( 1, 0, -1 ), new( -1, 0, -1 ),
		new( 0, 1, 1 ), new( 0, -1, 1 ), new( 0, 1, -1 ), new( 0, -1, -1 ),
	};

	// Per-channel sample offsets: non-integer, mutually unrelated, so the three
	// components of a displacement vector are three different noise fields rather
	// than one field read three times. Without them a displacement would point
	// along a diagonal everywhere, which reads as a shear, not asymmetry.
	static readonly Vec3 OffsetY = new( 17.13f, 9.71f, 31.07f );
	static readonly Vec3 OffsetZ = new( 41.29f, 23.53f, 11.19f );

	readonly byte[] _perm = new byte[512];
	readonly byte[] _permMod12 = new byte[512];

	public AsymmetryNoise( int seed )
	{
		// Deterministic shuffle of 0..255 from the seed alone (SplitMix32). No
		// System.Random: its seeding is fine, but this keeps the whole chain
		// auditable in one place and identical across runtimes.
		var table = new byte[256];
		for ( var i = 0; i < 256; i++ )
			table[i] = (byte)i;
		var state = (uint)seed;
		for ( var i = 255; i > 0; i-- )
		{
			state += 0x9E3779B9u;
			var z = state;
			z = (z ^ (z >> 16)) * 0x85EBCA6Bu;
			z = (z ^ (z >> 13)) * 0xC2B2AE35u;
			z ^= z >> 16;
			var j = (int)(z % (uint)(i + 1));
			(table[i], table[j]) = (table[j], table[i]);
		}
		for ( var i = 0; i < 512; i++ )
		{
			_perm[i] = table[i & 255];
			_permMod12[i] = (byte)(_perm[i] % 12);
		}
	}

	/// <summary>One noise component in [-1, 1] (clamped; the raw sum stays inside it).</summary>
	public float Sample( Vec3 p ) => Sample( p.x, p.y, p.z );

	/// <summary>Three decorrelated components, each in [-1, 1].</summary>
	public Vec3 Vector( Vec3 p ) => new(
		Sample( p ),
		Sample( p + OffsetY ),
		Sample( p + OffsetZ ) );

	public float Sample( float x, float y, float z )
	{
		// Skew the input into simplex-grid space.
		var s = (x + y + z) * F3;
		var i = FastFloor( x + s );
		var j = FastFloor( y + s );
		var k = FastFloor( z + s );
		var t = (i + j + k) * G3;
		var x0 = x - (i - t);
		var y0 = y - (j - t);
		var z0 = z - (k - t);

		// Which simplex corner is second / third, by ordering of the offsets.
		int i1, j1, k1, i2, j2, k2;
		if ( x0 >= y0 )
		{
			if ( y0 >= z0 ) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
			else if ( x0 >= z0 ) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 0; k2 = 1; }
			else { i1 = 0; j1 = 0; k1 = 1; i2 = 1; j2 = 0; k2 = 1; }
		}
		else
		{
			if ( y0 < z0 ) { i1 = 0; j1 = 0; k1 = 1; i2 = 0; j2 = 1; k2 = 1; }
			else if ( x0 < z0 ) { i1 = 0; j1 = 1; k1 = 0; i2 = 0; j2 = 1; k2 = 1; }
			else { i1 = 0; j1 = 1; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
		}

		var x1 = x0 - i1 + G3;
		var y1 = y0 - j1 + G3;
		var z1 = z0 - k1 + G3;
		var x2 = x0 - i2 + 2f * G3;
		var y2 = y0 - j2 + 2f * G3;
		var z2 = z0 - k2 + 2f * G3;
		var x3 = x0 - 1f + 3f * G3;
		var y3 = y0 - 1f + 3f * G3;
		var z3 = z0 - 1f + 3f * G3;

		var ii = i & 255;
		var jj = j & 255;
		var kk = k & 255;

		var n = 0f;
		n += Corner( ii + _perm[jj + _perm[kk]], x0, y0, z0 );
		n += Corner( ii + i1 + _perm[jj + j1 + _perm[kk + k1]], x1, y1, z1 );
		n += Corner( ii + i2 + _perm[jj + j2 + _perm[kk + k2]], x2, y2, z2 );
		n += Corner( ii + 1 + _perm[jj + 1 + _perm[kk + 1]], x3, y3, z3 );

		// 32 scales the corner sum into roughly [-1, 1]; the clamp is the guarantee
		// MaxDisplacement relies on, not a rounding fix.
		return MathF.Max( -1f, MathF.Min( 1f, 32f * n ) );
	}

	float Corner( int gi, float x, float y, float z )
	{
		var t = 0.6f - x * x - y * y - z * z;
		if ( t < 0f )
			return 0f;
		var g = Grad[_permMod12[gi & 511]];
		return t * t * t * t * (g.x * x + g.y * y + g.z * z);
	}

	static int FastFloor( float v ) => (int)MathF.Floor( v );
}
