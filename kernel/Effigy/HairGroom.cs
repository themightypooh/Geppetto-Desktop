using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// Parametric hair groom: guides + children, scriptable operators, renderable output.
///
/// Built ON <see cref="StrandSolver"/>, not a second solver. Guides are settled by
/// StrandSolver.Settle (straight strands, gravity + wind, rest-length projection, sphere
/// colliders for non-target bodies, exact MeshBVH collision against the target bodies via
/// <see cref="StrandSolver.BvhSurfaceCollider"/>); this class adds the groom layer on top:
/// parting, crown whorl, curl/wave/kink, braid, gather, frizz/flyaways, and root-preserving
/// clumped children. Styles come from <see cref="HairStylePreset"/> and the Andre Walker
/// 1A-4C <see cref="HairCurlType"/> taxonomy (see <see cref="ApplyPreset"/>).
///
/// Determinism: same roots, same options, same strands, every rebuild. All randomness is
/// a seeded System.Random consumed in a fixed order.
/// </summary>
/// <summary>Andre Walker 1A-4C curl taxonomy (spec 2.1 A). The type is the authority for
/// curl shape: <see cref="HairGroom.ApplyCurlType"/> maps it onto CurlRadius / CurlFreq /
/// WaveAmp / WaveFreq / KinkAmp / Frizz.</summary>
public enum HairCurlType
{
	Straight_1A, Wavy_2A, Wavy_2B, Wavy_2C,
	Curly_3A, Curly_3B, Coily_4A, Coily_4B, Coily_4C
}

/// <summary>Named groom styles (spec 2.1 A). <see cref="HairGroom.ApplyPreset"/> maps each
/// one onto guide count, clump, curl and gather; a caller-supplied CurlType or GatherPoint
/// wins over the preset's values for that field.</summary>
public enum HairStylePreset
{
	Custom, ShortCrop, BuzzCut, SlickedBack, SidePartPompadour, BobCut, LongLayered,
	Ponytail, HighBun, TwinTails, FrenchBraid, Dreadlocks, Cornrows, Afro, Mohawk,
	WolfMane, LionMane, HorseTail, FacialStubble, FullBeard, Mustache, Eyebrows, Eyelashes
}

public static class HairGroom
{
	public sealed class Options
	{
		public int GuideCount = 40;
		public int ChildrenPerGuide = 4;
		public float Length = 3f;
		public int Segments = 8;
		public int Steps = 30;
		public float Gravity = -9.8f;
		public Vec3 Wind = Vec3.Zero;
		public Vec3 Comb = Vec3.Zero;
		public string Part = "Center";
		public string ClumpCurve = "0:0,0.4:0.55,1:0.92";
		public float Spread = 0.25f;
		public float CurlRadius = 0f;
		public float CurlFreq = 1f;
		public float WaveAmp = 0f;
		public float WaveFreq = 1f;
		public float KinkAmp = 0f;
		public float Frizz = 0f;
		public float FlyawayRatio = 0f;
		public bool Braid = false;
		public int BraidLobes = 12;
		public float BraidWidth = 0.6f;
		public float BraidTaper = 0.5f;
		public Vec3? GatherPoint = null;
		public int Seed = 42;
		public bool Interpolate = false;
		public float LengthVariation = 0f;
		public float WidthVariation = 0f;
		public HairStylePreset Preset = HairStylePreset.Custom;
		public HairCurlType CurlType = HairCurlType.Straight_1A;
		// Crown whorl (spec 2.1 A): spiral centre and twist in radians (+CW, -CCW).
		// 0 = whorl off, which keeps every existing groom byte-identical; presets that
		// want a visible vertex spiral set 0.35, the spec's value.
		public Vec3 CrownCenter = new( 0f, -1.8f, 4.2f );
		public float CrownSwirl = 0f;
	}

	/// <summary>One groomed strand: points from root (index 0) to tip.</summary>
	public sealed class Strand
	{
		public List<Vec3> Points = new();
		public int GuideIndex;
		public bool IsGuide;
		public int VariantBand;
		public float WidthScale = 1f;
		public float LengthScale = 1f;
	}

	/// <summary>
	/// Parse a "t:v,t:v" curve. Malformed entries are skipped; an empty result falls back
	/// to the roadmap clump profile (0 at root, tightening toward the tip).
	/// </summary>
	public static List<(float T, float V)> ParseCurve( string text )
	{
		var pts = new List<(float, float)>();

		if ( !string.IsNullOrWhiteSpace( text ) )
		{
			foreach ( var part in text.Split( ',' ) )
			{
				var kv = part.Split( ':' );

				if ( kv.Length == 2
					&& float.TryParse( kv[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t )
					&& float.TryParse( kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v ) )
					pts.Add( (Math.Clamp( t, 0f, 1f ), v) );
			}
		}

		if ( pts.Count == 0 )
			pts.Add( (0f, 0f) );

		pts.Sort( (a, b) => a.Item1.CompareTo( b.Item1 ) );
		return pts;
	}

	/// <summary>Sample a parsed curve with linear interpolation, clamped to its ends.</summary>
	public static float SampleCurve( List<(float T, float V)> curve, float t )
	{
		if ( curve.Count == 1 )
			return curve[0].V;

		if ( t <= curve[0].T )
			return curve[0].V;

		if ( t >= curve[^1].T )
			return curve[^1].V;

		for ( var i = 1; i < curve.Count; i++ )
		{
			if ( t <= curve[i].T )
			{
				var span = curve[i].T - curve[i - 1].T;
				var k = span < 1e-9f ? 0f : (t - curve[i - 1].T) / span;
				return curve[i - 1].V + (curve[i].V - curve[i - 1].V) * k;
			}
		}

		return curve[^1].V;
	}

	/// <summary>Map an Andre Walker curl type onto the curl/wave/kink shape fields, in place.
	/// The type is the authority for shape: a caller that wants hand-tuned radii should leave
	/// CurlType at Straight_1A and set CurlRadius / CurlFreq / WaveAmp / WaveFreq / KinkAmp /
	/// Frizz directly. Reset first, so switching types never leaves a previous type's residue.</summary>
	public static void ApplyCurlType( Options o, HairCurlType curl )
	{
		if ( o is null ) throw new ArgumentNullException( nameof( o ) );
		o.CurlRadius = 0f; o.CurlFreq = 1f; o.WaveAmp = 0f; o.WaveFreq = 1f; o.KinkAmp = 0f; o.Frizz = 0f;
		o.CurlType = curl;
		switch ( curl )
		{
			case HairCurlType.Straight_1A: break;
			case HairCurlType.Wavy_2A: o.WaveAmp = 0.15f; o.WaveFreq = 1f; break;
			case HairCurlType.Wavy_2B: o.WaveAmp = 0.3f; o.WaveFreq = 1.5f; break;
			case HairCurlType.Wavy_2C: o.WaveAmp = 0.45f; o.WaveFreq = 2f; o.CurlRadius = 0.05f; o.CurlFreq = 1f; break;
			case HairCurlType.Curly_3A: o.CurlRadius = 0.25f; o.CurlFreq = 2f; break;
			case HairCurlType.Curly_3B: o.CurlRadius = 0.35f; o.CurlFreq = 3f; break;
			case HairCurlType.Coily_4A: o.CurlRadius = 0.2f; o.CurlFreq = 5f; o.KinkAmp = 0.05f; break;
			case HairCurlType.Coily_4B: o.CurlRadius = 0.25f; o.CurlFreq = 4f; o.KinkAmp = 0.15f; o.Frizz = 0.1f; break;
			case HairCurlType.Coily_4C: o.CurlRadius = 0.12f; o.CurlFreq = 6f; o.KinkAmp = 0.2f; o.Frizz = 0.15f; break;
		}
	}

	/// <summary>Apply a named style preset to <paramref name="o"/>, in place (guide count,
	/// clump, curl and gather). A caller-supplied CurlType wins over the preset's curl, and a
	/// caller-supplied GatherPoint wins over the preset's conventional tie point, so a scripted
	/// <c>preset=FrenchBraid curl=Wavy_2B</c> or <c>preset=Ponytail gather=...</c> keeps the
	/// caller's values for those fields. A preset that needs a tie point (Ponytail, HighBun,
	/// TwinTails, HorseTail) falls back to a conventional back-of-head point when the caller
	/// gives none; the caller should pass the real tie point for a real head. TwinTails has a
	/// single tie point here — a second tie needs a second groom pass.</summary>
	public static void ApplyPreset( Options o )
	{
		if ( o is null ) throw new ArgumentNullException( nameof( o ) );
		if ( o.Preset == HairStylePreset.Custom ) return;
		var callerCurl = o.CurlType;
		var presetCurl = HairCurlType.Straight_1A;
		switch ( o.Preset )
		{
			case HairStylePreset.ShortCrop:
				o.GuideCount = 48; o.Length = 1.2f; presetCurl = HairCurlType.Straight_1A;
				o.ClumpCurve = "0:0,0.4:0.6,1:0.95"; o.Spread = 0.15f; o.Part = "Center"; o.CrownSwirl = 0.35f;
				break;
			case HairStylePreset.BuzzCut:
				o.GuideCount = 64; o.ChildrenPerGuide = 2; o.Length = 0.4f; presetCurl = HairCurlType.Straight_1A;
				o.Spread = 0.1f; o.Part = "Center"; o.CrownSwirl = 0.35f;
				break;
			case HairStylePreset.SlickedBack:
				o.GuideCount = 48; o.Length = 4f; presetCurl = HairCurlType.Wavy_2A;
				o.Part = "None"; o.CrownSwirl = 0.35f; o.Comb = new Vec3( 0, -1, 0 );
				break;
			case HairStylePreset.SidePartPompadour:
				o.GuideCount = 48; o.Length = 5f; presetCurl = HairCurlType.Wavy_2B;
				o.Part = "SideLeft"; o.CrownSwirl = 0.35f;
				break;
			case HairStylePreset.BobCut:
				o.GuideCount = 56; o.Length = 6f; presetCurl = HairCurlType.Wavy_2A;
				o.Part = "Center"; o.CrownSwirl = 0.35f;
				break;
			case HairStylePreset.LongLayered:
				o.GuideCount = 64; o.Length = 12f; presetCurl = HairCurlType.Wavy_2B;
				o.LengthVariation = 0.25f; o.Part = "Center"; o.CrownSwirl = 0.35f;
				break;
			case HairStylePreset.Ponytail:
				o.GuideCount = 56; o.Length = 10f; presetCurl = HairCurlType.Wavy_2A;
				o.Part = "Center"; o.CrownSwirl = 0.35f; o.GatherPoint ??= new Vec3( 0, -2.2f, 2.5f );
				break;
			case HairStylePreset.HighBun:
				o.GuideCount = 56; o.Length = 8f; presetCurl = HairCurlType.Wavy_2A;
				o.Part = "Center"; o.CrownSwirl = 0.35f; o.GatherPoint ??= new Vec3( 0, -2f, 4f );
				break;
			case HairStylePreset.TwinTails:
				o.GuideCount = 64; o.Length = 10f; presetCurl = HairCurlType.Wavy_2B;
				o.Part = "Center"; o.CrownSwirl = 0.35f; o.GatherPoint ??= new Vec3( 0, -2.2f, 2.5f );
				break;
			case HairStylePreset.FrenchBraid:
				o.GuideCount = 48; o.Length = 10f; presetCurl = HairCurlType.Wavy_2A;
				o.Part = "Center"; o.CrownSwirl = 0.35f;
				o.Braid = true; o.BraidLobes = 12; o.BraidWidth = 0.6f;
				break;
			case HairStylePreset.Dreadlocks:
				o.GuideCount = 40; o.Length = 9f; presetCurl = HairCurlType.Coily_4C;
				o.ClumpCurve = "0:0,0.4:0.7,1:0.98"; o.Spread = 0.12f; o.Part = "Center"; o.CrownSwirl = 0.35f;
				break;
			case HairStylePreset.Cornrows:
				o.GuideCount = 48; o.Length = 4f; presetCurl = HairCurlType.Straight_1A;
				o.Part = "Center"; o.CrownSwirl = 0f; o.Interpolate = true;
				o.Braid = true; o.BraidLobes = 8; o.BraidWidth = 0.3f;
				break;
			case HairStylePreset.Afro:
				o.GuideCount = 96; o.ChildrenPerGuide = 6; o.Length = 3f; presetCurl = HairCurlType.Coily_4B;
				o.ClumpCurve = "0:0,0.4:0.4,1:0.8"; o.Spread = 0.6f; o.Part = "Center"; o.CrownSwirl = 0.35f;
				break;
			case HairStylePreset.Mohawk:
				o.GuideCount = 32; o.Length = 5f; presetCurl = HairCurlType.Wavy_2C;
				o.Spread = 0.1f; o.Part = "Center"; o.CrownSwirl = 0f;
				break;
			case HairStylePreset.WolfMane:
				o.GuideCount = 80; o.Length = 12f; presetCurl = HairCurlType.Wavy_2C;
				o.LengthVariation = 0.2f; o.Part = "None"; o.CrownSwirl = 0.35f;
				break;
			case HairStylePreset.LionMane:
				o.GuideCount = 96; o.ChildrenPerGuide = 6; o.Length = 10f; presetCurl = HairCurlType.Curly_3A;
				o.Spread = 0.5f; o.Part = "None"; o.CrownSwirl = 0.35f;
				break;
			case HairStylePreset.HorseTail:
				o.GuideCount = 56; o.Length = 14f; presetCurl = HairCurlType.Wavy_2A;
				o.Part = "Center"; o.CrownSwirl = 0.35f; o.GatherPoint ??= new Vec3( 0, -2.2f, 2.5f );
				break;
			case HairStylePreset.FacialStubble:
				o.GuideCount = 96; o.Length = 0.3f; presetCurl = HairCurlType.Straight_1A;
				o.Spread = 0.1f; o.Part = "None"; o.CrownSwirl = 0f;
				break;
			case HairStylePreset.FullBeard:
				o.GuideCount = 64; o.Length = 3f; presetCurl = HairCurlType.Wavy_2A;
				o.Spread = 0.3f; o.Part = "None"; o.CrownSwirl = 0f;
				break;
			case HairStylePreset.Mustache:
				o.GuideCount = 32; o.Length = 1.2f; presetCurl = HairCurlType.Wavy_2A;
				o.Spread = 0.2f; o.Part = "None"; o.CrownSwirl = 0f;
				break;
			case HairStylePreset.Eyebrows:
				o.GuideCount = 32; o.Length = 0.8f; presetCurl = HairCurlType.Straight_1A;
				o.Spread = 0.1f; o.Part = "None"; o.CrownSwirl = 0f;
				break;
			case HairStylePreset.Eyelashes:
				o.GuideCount = 32; o.Length = 0.5f; presetCurl = HairCurlType.Straight_1A;
				o.Spread = 0.05f; o.Part = "None"; o.CrownSwirl = 0f;
				break;
			default: return;
		}
		ApplyCurlType( o, callerCurl != HairCurlType.Straight_1A ? callerCurl : presetCurl );
	}

	/// <summary>Crown whorl tangent initializer (spec 2.1 B.1): the radial direction from the
	/// crown centre, projected onto the tangent plane, rotated by the swirl angle around the
	/// surface normal: d_whorl = cos(swirl) * d_radial + sin(swirl) * (n x d_radial). A root
	/// sitting on the crown centre has no radial direction, so the surface normal is returned.</summary>
	public static Vec3 WhorlDirection( Vec3 root, Vec3 normal, Vec3 crownCenter, float swirl )
	{
		var n = normal.LengthSquared > 1e-12f ? normal.Normal : new Vec3( 0, 0, 1 );
		var toRoot = root - crownCenter;
		var radial = toRoot - n * Vec3.Dot( toRoot, n );
		if ( radial.LengthSquared < 1e-12f ) return n;
		radial = radial.Normal;
		return (radial * MathF.Cos( swirl ) + Vec3.Cross( n, radial ) * MathF.Sin( swirl )).Normal;
	}
	/// <summary>
	/// Build guides from roots + outward normals, then run the operators in roadmap order:
	/// preset/curl (see <see cref="ApplyPreset"/>), parting and crown whorl (into the initial
	/// direction), settle, curl/wave/kink, braid, gather, frizz/flyaways. Children are derived
	/// from the finished guides, so they inherit every operator for free.
	/// </summary>
	public static List<Strand> Build( IReadOnlyList<Vec3> roots, IReadOnlyList<Vec3> normals,
		IReadOnlyList<(Vec3 Centre, float Radius)> colliders, Options o )
		=> Build( roots, normals, colliders, o, null, null, null, 0f );

	public static List<Strand> Build( IReadOnlyList<Vec3> roots, IReadOnlyList<Vec3> normals,
		IReadOnlyList<(Vec3 Centre, float Radius)> colliders, Options o,
		IReadOnlyList<Vec3> flows, IReadOnlyList<float> lengthScales,
		IReadOnlyList<PolyMesh> surfaceMeshes, float surfaceRadius )
	{
		if ( roots is null || roots.Count == 0 )
			throw new ArgumentException( "A groom needs at least one root.", nameof( roots ) );

		// Preset first (it fills guide count, clump, curl and gather); a bare CurlType
		// without a preset still maps onto the curl shape fields. Neither consumes RNG,
		// so the seeded sequence below is unchanged for existing grooms.
		if ( o.Preset != HairStylePreset.Custom ) ApplyPreset( o );
		else if ( o.CurlType != HairCurlType.Straight_1A ) ApplyCurlType( o, o.CurlType );

		var rng = new Random( o.Seed );
		var centre = Vec3.Zero;

		foreach ( var r in roots )
			centre += r;

		centre /= roots.Count;

		var dirs = new List<Vec3>( roots.Count );

		for ( var i = 0; i < roots.Count; i++ )
		{
			var d = normals is not null && i < normals.Count && normals[i].LengthSquared > 1e-12f
				? normals[i]
				: new Vec3( 0, 0, 1 );

			if ( flows is not null && i < flows.Count && flows[i].LengthSquared > 1e-12f )
				d = (d + flows[i]).Normal;

			if ( o.CrownSwirl != 0f )
			{
				// Crown whorl tangent initializer (spec 2.1 B.1): tilt the initial direction
				// toward the spiral tangent around the crown centre; the tilt weight is |swirl|.
				// Skipped entirely at the 0 default, so existing grooms are unchanged.
				var nrm = normals is not null && i < normals.Count ? normals[i] : new Vec3( 0, 0, 1 );
				var whorl = WhorlDirection( roots[i], nrm, o.CrownCenter, o.CrownSwirl );
				var w = Math.Clamp( MathF.Abs( o.CrownSwirl ), 0f, 1f );
				d = (d * (1f - w) + whorl * w).Normal;
			}

			dirs.Add( ApplyParting( d, roots[i], centre, o.Part ) );
		}

		// Exact MeshBVH collision (spec 2.1 B.2) supersedes the uniform-grid SurfaceCollider
		// on this path; the sphere colliders still guard non-target bodies.
		var bvhSurface = surfaceMeshes is not null && surfaceRadius > 0f ? StrandSolver.BvhSurfaceCollider.Build( surfaceMeshes ) : null;
		var settled = StrandSolver.Settle( roots, dirs,
			new StrandSolver.Options
			{
				Length = o.Length, Segments = o.Segments, Steps = o.Steps,
				Gravity = o.Gravity, Wind = o.Wind, Comb = o.Comb,
			}, colliders, null, surfaceRadius, bvhSurface );

		var guides = new List<Strand>();

		for ( var i = 0; i < settled.Count; i++ )
		{
			var ls = lengthScales is not null && i < lengthScales.Count && lengthScales[i] > 0f ? lengthScales[i] : 1f;
			if ( o.LengthVariation > 0f ) ls *= 1f + ((float)rng.NextDouble() * 2f - 1f) * o.LengthVariation;
			var ws = o.WidthVariation > 0f ? 1f + ((float)rng.NextDouble() * 2f - 1f) * o.WidthVariation : 1f;
			guides.Add( new Strand { Points = settled[i], GuideIndex = i, IsGuide = true, LengthScale = ls, WidthScale = ws } );
		}

		ApplyCurlWaveKink( guides, o, rng );
		Reproject( guides, o );
		if ( o.Braid )
		{
			ApplyBraid( guides, o );
			Reproject( guides, o );
		}
		if ( o.GatherPoint.HasValue )
		{
			ApplyGather( guides, o.GatherPoint.Value );
			Reproject( guides, o );
		}
		ApplyFrizz( guides, o, rng );
		Reproject( guides, o );

		var all = new List<Strand>( guides );
		all.AddRange( o.Interpolate ? BuildInterpolatedChildren( guides, o, centre ) : BuildChildren( guides, o ) );
		for ( var i = 0; i < all.Count; i++ ) all[i].VariantBand = i % 5;
		return all;
	}

	static void Reproject( List<Strand> guides, Options o )
	{
		foreach ( var g in guides ) StrandSolver.ProjectToLength( g.Points, o.Length * g.LengthScale );
	}

	static Vec3 ApplyParting( Vec3 dir, Vec3 root, Vec3 centre, string part )
	{
		var tilt = part switch
		{
			"SideLeft" => new Vec3( 1, 0, 0 ),
			"SideRight" => new Vec3( -1, 0, 0 ),
			"Radial" => new Vec3( root.x - centre.x, root.y - centre.y, 0 ).Normal,
			"None" => Vec3.Zero,
			_ => new Vec3( root.x >= centre.x ? 1 : -1, 0, 0 ),
		};

		if ( tilt.LengthSquared < 1e-12f )
			return dir;

		return (dir + tilt * 0.6f).Normal;
	}

	/// <summary>Parallel-transport side/binormal frames along a strand (same idea as BuildCards).</summary>
	public static void Frames( List<Vec3> pts, out Vec3[] side, out Vec3[] binormal )
	{
		side = new Vec3[pts.Count];
		binormal = new Vec3[pts.Count];

		for ( var i = 0; i < pts.Count; i++ )
		{
			var tangent = (pts[Math.Min( i + 1, pts.Count - 1 )] - pts[Math.Max( i - 1, 0 )]).Normal;

			if ( tangent.LengthSquared < 1e-12f )
				tangent = new Vec3( 0, 0, -1 );

			if ( i == 0 )
			{
				var helper = MathF.Abs( tangent.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
				side[i] = Vec3.Cross( tangent, helper ).Normal;
			}
			else
			{
				var s = side[i - 1] - tangent * Vec3.Dot( side[i - 1], tangent );
				side[i] = s.LengthSquared > 1e-12f ? s.Normal : side[i - 1];
			}

			binormal[i] = Vec3.Cross( tangent, side[i] ).Normal;
		}
	}

	static void ApplyCurlWaveKink( List<Strand> guides, Options o, Random rng )
	{
		var hasCurl = o.CurlRadius > 0f && o.CurlFreq > 0f;
		var hasWave = o.WaveAmp > 0f && o.WaveFreq > 0f;
		var hasKink = o.KinkAmp > 0f;

		if ( !hasCurl && !hasWave && !hasKink )
			return;

		foreach ( var g in guides )
		{
			var pts = g.Points;
			Frames( pts, out var side, out var binormal );
			var phase = (float)rng.NextDouble() * MathF.PI * 2f;

			for ( var i = 1; i < pts.Count; i++ )
			{
				var t = (float)i / (pts.Count - 1);
				var off = Vec3.Zero;

				if ( hasCurl )
				{
					var a = 2f * MathF.PI * o.CurlFreq * t + phase;
					off += (side[i] * MathF.Cos( a ) + binormal[i] * MathF.Sin( a )) * o.CurlRadius * t;
				}

				if ( hasWave )
					off += side[i] * (o.WaveAmp * MathF.Sin( 2f * MathF.PI * o.WaveFreq * t + phase ) * t);

				if ( hasKink )
				{
					var tri = 1f - MathF.Abs( 1f - 2f * (t * o.WaveFreq - MathF.Floor( t * o.WaveFreq )));
					off += binormal[i] * (o.KinkAmp * (tri - 0.5f) * 2f * t);
				}

				pts[i] = pts[i] + off;
			}
		}
	}

	static void ApplyBraid( List<Strand> guides, Options o )
	{
		for ( var baseIdx = 0; baseIdx + 2 < guides.Count; baseIdx += 3 )
		{
			var trio = new[] { guides[baseIdx], guides[baseIdx + 1], guides[baseIdx + 2] };
			var n = trio[0].Points.Count;

			if ( trio.Any( s => s.Points.Count != n ) )
				continue;

			var centroid = new List<Vec3>( n );

			for ( var i = 0; i < n; i++ )
				centroid.Add( (trio[0].Points[i] + trio[1].Points[i] + trio[2].Points[i]) / 3f );

			Frames( centroid, out var side, out var binormal );

			for ( var k = 0; k < 3; k++ )
			{
				for ( var i = 0; i < n; i++ )
				{
					var t = n == 1 ? 0f : (float)i / (n - 1);
					var a = 2f * MathF.PI * o.BraidLobes * t + k * 2f * MathF.PI / 3f;
					var r = o.BraidWidth * (1f - o.BraidTaper * t);
					trio[k].Points[i] = centroid[i] + side[i] * (MathF.Cos( a ) * r) + binormal[i] * (MathF.Sin( a ) * r);
				}
			}
		}
	}

	static void ApplyGather( List<Strand> guides, Vec3 tie )
	{
		foreach ( var g in guides )
		{
			var pts = g.Points;
			var tieIdx = 0;
			var best = float.MaxValue;

			for ( var i = 0; i < pts.Count; i++ )
			{
				var d = (pts[i] - tie).LengthSquared;

				if ( d < best )
				{
					best = d;
					tieIdx = i;
				}
			}

			var atTie = pts[tieIdx];

			for ( var i = 1; i < pts.Count; i++ )
			{
				if ( i < tieIdx )
				{
					var w = tieIdx == 0 ? 0f : (float)i / tieIdx * 0.9f;
					pts[i] = Vec3.Lerp( pts[i], Vec3.Lerp( pts[0], tie, (float)i / Math.Max( 1, tieIdx )), w );
				}
				else if ( i > tieIdx )
				{
					pts[i] = tie + (pts[i] - atTie) * 1.3f;
				}
				else
				{
					pts[i] = tie;
				}
			}
		}
	}

	static void ApplyFrizz( List<Strand> guides, Options o, Random rng )
	{
		if ( o.Frizz <= 0f && o.FlyawayRatio <= 0f )
			return;

		for ( var s = 0; s < guides.Count; s++ )
		{
			var pts = guides[s].Points;
			var flyaway = o.FlyawayRatio > 0f && (s * 2654435761u % 100) < o.FlyawayRatio * 100f;
			// Coherent along the strand: one direction and one low-frequency wave per
			// strand, so frizz reads as a wave, not independent per-point zigzag jitter.
			var dir = new Vec3( (float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f );
			if ( dir.LengthSquared < 1e-12f ) dir = new Vec3( 1, 0, 0 );
			dir = dir.Normal;
			var phase = (float)rng.NextDouble() * MathF.PI * 2f;

			for ( var i = 1; i < pts.Count; i++ )
			{
				var tt = (float)i / (pts.Count - 1);
				var wave = 0.5f + 0.5f * MathF.Sin( 2f * MathF.PI * 2f * tt + phase );
				var jitter = o.Frizz * tt * wave;

				if ( flyaway )
					jitter += o.FlyawayRatio * o.Length * 0.2f * tt * tt;

				if ( jitter <= 0f )
					continue;

				pts[i] = pts[i] + dir * jitter;
			}
		}
	}

	/// <summary>
	/// Children of a guide. Root-preserving, per the roadmap clump profile: the lateral
	/// offset is exactly 0 at the root (sin(0) = 0), peaks mid-strand, and tightens toward
	/// the tip as the clump curve rises to ~0.92 — so child roots stay planted in the
	/// scalp and child tips converge on the guide. This is the fix for the old
	/// HairStrandService.Clump bug that teleported every root to strand zero's root.
	/// </summary>
	public static List<Strand> BuildChildren( List<Strand> guides, Options o )
	{
		var curve = ParseCurve( o.ClumpCurve );
		var rng = new Random( o.Seed + 7919 );
		var children = new List<Strand>();

		foreach ( var g in guides )
		{
			Frames( g.Points, out var side, out var binormal );
			var groupAngle = 0f;

			for ( var c = 0; c < o.ChildrenPerGuide; c++ )
			{
				if ( c % 4 == 0 ) groupAngle = (float)rng.NextDouble() * MathF.PI * 2f;
				var a = groupAngle + ((float)rng.NextDouble() - 0.5f) * 1.0f;
				var dir = side[0] * MathF.Cos( a ) + binormal[0] * MathF.Sin( a );
				var pts = new List<Vec3>( g.Points.Count );

				for ( var i = 0; i < g.Points.Count; i++ )
				{
					var t = g.Points.Count == 1 ? 0f : (float)i / (g.Points.Count - 1);
					var envelope = MathF.Sin( MathF.PI * Math.Min( t * 1.15f, 1f ) ) * (1f - SampleCurve( curve, t ));
					pts.Add( g.Points[i] + dir * (o.Spread * envelope) );
				}

				children.Add( new Strand { Points = pts, GuideIndex = g.GuideIndex, IsGuide = false } );
			}
		}

		return children;
	}

	/// <summary>One sampled root: point inside a face, its normal, its face UV,
	/// a length scale from the length map (1 when no map), and a flow direction from
	/// the flow map (zero when no map).</summary>
	public sealed class Sample
	{
		public Vec3 Point;
		public Vec3 Normal;
		public Vec2 Uv;
		public float LengthScale = 1f;
		public Vec3 Flow;
	}

	public static float SampleMap( (int W, int H, int[] Rgb) map, Vec2 uv, int channel )
	{
		if ( map.Rgb is null || map.W <= 0 || map.H <= 0 ) return 0.5f;
		var x = Math.Clamp( (int)(uv.x * map.W), 0, map.W - 1 );
		var y = Math.Clamp( (int)(uv.y * map.H), 0, map.H - 1 );
		var px = map.Rgb[y * map.W + x];
		return channel switch { 0 => ((px >> 16) & 255) / 255f, 1 => ((px >> 8) & 255) / 255f, _ => (px & 255) / 255f };
	}

	/// <summary>Interpolated dense strands (Rank 1): each child of a guide blends
	/// that guide toward its nearest same-side neighbour, with t spread evenly
	/// across the guide's children, so two guides leaning opposite ways produce
	/// a smooth fan instead of two clone blocks. Parting side (Center part) blocks
	/// interpolation across the centre line. Falls back to a clone when a guide
	/// has no same-side neighbour.</summary>
	public static List<Strand> BuildInterpolatedChildren( List<Strand> guides, Options o, Vec3 centre )
	{
		var children = new List<Strand>();
		if ( guides.Count == 0 || o.ChildrenPerGuide <= 0 ) return children;
		for ( var gi = 0; gi < guides.Count; gi++ )
		{
			var g = guides[gi];
			var partner = -1; var best = float.MaxValue;
			for ( var j = 0; j < guides.Count; j++ )
			{
				if ( j == gi ) continue;
				if ( o.Part == "Center" && Math.Sign( guides[j].Points[0].x - centre.x ) != Math.Sign( g.Points[0].x - centre.x ) ) continue;
				var d = (guides[j].Points[0] - g.Points[0]).LengthSquared;
				if ( d < best ) { best = d; partner = j; }
			}
			for ( var c = 0; c < o.ChildrenPerGuide; c++ )
			{
				var pts = new List<Vec3>( g.Points.Count );
				if ( partner < 0 )
				{
					pts.AddRange( g.Points );
				}
				else
				{
					var tt = (float)(c + 0.5) / o.ChildrenPerGuide;
					var p = guides[partner];
					for ( var i = 0; i < g.Points.Count && i < p.Points.Count; i++ )
						pts.Add( Vec3.Lerp( g.Points[i], p.Points[i], tt ) );
				}
				children.Add( new Strand { Points = pts, GuideIndex = g.GuideIndex, IsGuide = false, LengthScale = g.LengthScale, WidthScale = g.WidthScale } );
			}
		}
		return children;
	}

	/// <summary>
	/// Region filter over a body's faces, as a fraction of the body's bounding height
	/// (Z-up). Bands are approximations for a head in any facing — a scalp is the top
	/// cap, a beard the bottom, brows/eyelashes narrow bands — and Custom takes explicit
	/// zMin/zMax fractions. Area-weighted sampling, deterministic per seed, roots nudged
	/// out along the face normal like StrandSolver.Scatter.
	/// </summary>
	public static List<Sample> SampleRegion( IReadOnlyList<PolyMesh> meshes,
		string region, int count, int seed, float zMin = 0f, float zMax = 1f,
		(int W, int H, int[] Rgb)? densityMap = null, (int W, int H, int[] Rgb)? lengthMap = null, (int W, int H, int[] Rgb)? flowMap = null )
	{
		var (lo, hi, normalZ) = region switch
		{
			"Scalp" => (0.62f, 1f, 0f),
			"Beard" => (0f, 0.38f, 0.4f),
			"Moustache" => (0.30f, 0.48f, 1f),
			"Brows" => (0.58f, 0.72f, 1f),
			"Eyelashes" => (0.62f, 0.75f, 1f),
			"Mane" => (0.5f, 1f, 1f),
			"Tail" => (0f, 0.3f, 1f),
			"Custom" => (zMin, zMax, 1f),
			_ => (0f, 1f, 1f),
		};

		var minZ = float.MaxValue;
		var maxZ = float.MinValue;

		foreach ( var m in meshes )
		{
			if ( m is null )
				continue;

			foreach ( var p in m.Positions )
			{
				minZ = Math.Min( minZ, p.z );
				maxZ = Math.Max( maxZ, p.z );
			}
		}

		if ( maxZ - minZ < 1e-9f )
			return new List<Sample>();

		var rng = new Random( seed );
		var faces = new List<(PolyMesh Mesh, int Face, float Area)>();
		var total = 0f;

		foreach ( var mesh in meshes )
		{
			if ( mesh is null )
				continue;

			for ( var fi = 0; fi < mesh.FaceCount; fi++ )
			{
				var f = mesh.Faces[fi];
				var c = mesh.FaceCentroid( f );
				var nz = (c.z - minZ) / (maxZ - minZ);

				if ( nz < lo || nz > hi )
					continue;

				var n = mesh.FaceNormal( f );

				if ( region == "Scalp" && n.z <= 0f )
					continue;

				if ( region == "Beard" && n.z >= normalZ )
					continue;

				var area = mesh.FaceArea( f );

				if ( area <= 0f )
					continue;

				faces.Add( (mesh, fi, area) );
				total += area;
			}
		}

		var result = new List<Sample>();

		if ( faces.Count == 0 || total <= 0f || count <= 0 )
			return result;

		var attempts = 0;
		var maxAttempts = Math.Max( count * 100, 1000 );
		while ( result.Count < count && attempts < maxAttempts )
		{
			attempts++;
			var pick = (float)rng.NextDouble() * total;
			var chosen = faces[^1];

			foreach ( var candidate in faces )
			{
				pick -= candidate.Area;

				if ( pick <= 0f )
				{
					chosen = candidate;
					break;
				}
			}

			var face = chosen.Mesh.Faces[chosen.Face];
			var (sp, sn, suv) = StrandSolver.SamplePointInFace( chosen.Mesh, face, rng );
			var normal = sn.LengthSquared > 1e-12f ? sn.Normal : new Vec3( 0, 0, 1 );
			if ( densityMap.HasValue )
			{
				var dv = (SampleMap( densityMap.Value, suv, 0 ) + SampleMap( densityMap.Value, suv, 1 ) + SampleMap( densityMap.Value, suv, 2 )) / 3f;
				if ( (float)rng.NextDouble() >= dv ) continue;
			}
			var ls = 1f;
			if ( lengthMap.HasValue )
				ls = 0.5f + (SampleMap( lengthMap.Value, suv, 0 ) + SampleMap( lengthMap.Value, suv, 1 ) + SampleMap( lengthMap.Value, suv, 2 )) / 3f;
			var flow = Vec3.Zero;
			if ( flowMap.HasValue )
			{
				var dx = SampleMap( flowMap.Value, suv, 0 ) * 2f - 1f;
				var dy = SampleMap( flowMap.Value, suv, 1 ) * 2f - 1f;
				if ( dx * dx + dy * dy > 0.04f )
				{
					var helper = MathF.Abs( normal.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
					var side = Vec3.Cross( normal, helper ).Normal;
					var bin = Vec3.Cross( normal, side );
					flow = (side * dx + bin * dy).Normal;
				}
			}
			result.Add( new Sample { Point = sp + normal * 0.01f, Normal = normal, Uv = suv, LengthScale = ls, Flow = flow } );
		}

		return result;
	}

	/// <summary>
	/// Card mesh for groom strands, with vertex colours the roadmap's card spec asks for:
	/// R = root-to-tip (0 at root, 1 at tip), G = strand id hash, B = ambient occlusion
	/// (dark at root, bright at tip). BuildCards lays vertices out strand by strand,
	/// two per point (left, right), so colours can be assigned by index in the same order.
	/// </summary>
	public static PolyMesh BuildCardsMesh( IReadOnlyList<Strand> strands, float width, float tipRatio, int material )
		=> BuildCardsMesh( strands, width, tipRatio, material, 0 );

	public static PolyMesh BuildCardsMesh( IReadOnlyList<Strand> strands, float width, float tipRatio, int material, int bandCount )
	{
		var pointLists = strands.Select( s => s.Points ).ToList();
		var widths = strands.Select( s => width * s.WidthScale ).ToList();
		var mesh = StrandSolver.BuildCards( pointLists, width, tipRatio, material, widths );
		if ( bandCount > 0 )
		{
			var faceIdx = 0;
			for ( var s = 0; s < strands.Count; s++ )
			{
				var n = strands[s].Points.Count;
				if ( n < 2 ) continue;
				var bandV0 = (float)(strands[s].VariantBand % bandCount) / bandCount;
				var bandH = 1f / bandCount;
				for ( var k = 0; k < 2 * (n - 1) && faceIdx < mesh.FaceCount; k++, faceIdx++ )
				{
					var f = mesh.Faces[faceIdx];
					if ( f.UVs is null ) continue;
					for ( var ci = 0; ci < f.UVs.Length; ci++ )
						f.UVs[ci] = new Vec2( f.UVs[ci].y, bandV0 + f.UVs[ci].x * bandH );
				}
			}
		}
		var colors = new Vec4[mesh.VertexCount];
		var vi = 0;

		for ( var s = 0; s < strands.Count; s++ )
		{
			var n = strands[s].Points.Count;
			var id = ((s * 2654435761u) % 256) / 255f;

			for ( var i = 0; i < n; i++ )
			{
				var t = n == 1 ? 0f : (float)i / (n - 1);
				var c = new Vec4( t, id, 0.35f + 0.65f * t, 1f );
				colors[vi++] = c;
				colors[vi++] = c;
			}
		}

		if ( vi == mesh.VertexCount )
			mesh.VertexColors = colors;

		return mesh;
	}

	/// <summary>Tube mesh for groom strands (tapered, like StrandSolver.BuildTube).</summary>
	public static PolyMesh BuildTubesMesh( IReadOnlyList<Strand> strands, float radius, float tipRatio, int sides, int material )
		=> StrandSolver.BuildTube( strands.Select( s => s.Points ).ToList(), radius, tipRatio, sides, material );
}
