using System;
using System.Collections.Generic;
using System.Globalization;

namespace Effigy;

/// <summary>
/// Bone-chain muscle strands: twisting, clumping fibre bundles that run along a
/// skeletal chain and fuse into its joint insertion points.
///
/// DELIBERATELY DISTINCT from <see cref="Tendons"/> (same tree, untouched): that
/// feature spans a gap between two BODIES with 2-3 sagging, open tubes. This one
/// runs along a BONE CHAIN (a list of joint positions), builds one closed quad
/// tube per strand, twists the whole bundle, clumps strands into groups that
/// share a lateral offset, and jitters each strand's radius with Poisson-placed
/// bumps. The script verb only resolves bone NAMES to joints at wiring time —
/// that lookup is owed, not here — so this file takes joint positions directly
/// and stays a pure model: no skeleton, no script engine, no body lookup.
/// </summary>
public sealed class OrganicTendonOptions
{
	/// <summary>From-bone name, as written in the verb (<c>bone=</c>). Kept verbatim;
	/// resolving it to joints is the wiring step, not this model.</summary>
	public string Bone = "";

	/// <summary>To-bone name, as written in the verb (<c>to=</c>).</summary>
	public string To = "";

	/// <summary>Strand count. 1 is a lone central strand; 8 is the verb example.</summary>
	public int Strands = 8;

	/// <summary>Full turns the bundle twists along the chain. 1.2 is the verb example;
	/// negative twists the other way.</summary>
	public float Twist = 1.2f;

	/// <summary>Base strand radius before taper and jitter.</summary>
	public float Radius = 0.12f;

	/// <summary>Sides per tube ring. 4 is the minimum for a quad tube.</summary>
	public int Sides = 8;

	/// <summary>Rings along the chain minus one; 16 gives 17 rings.</summary>
	public int Steps = 16;

	/// <summary>Seed for every jittered choice, so a build replays bit-identically.</summary>
	public int Seed = 42;

	/// <summary>Clump strength 0..1: how far each clump's shared centre is pushed off
	/// the chain axis, as a fraction of the bundle orbit radius. 0 spreads strands
	/// evenly round the axis; 1 pushes clump centres a full orbit radius out.</summary>
	public float Clump = 0.35f;

	/// <summary>Radius jitter strength 0..1: the amplitude of the Poisson bumps,
	/// as a fraction of <see cref="Radius"/>.</summary>
	public float Jitter = 0.25f;

	public void Validate()
	{
		if ( string.IsNullOrWhiteSpace( Bone ) )
			throw new ArgumentException( "Organic tendons need a bone= name.", nameof( Bone ) );
		if ( string.IsNullOrWhiteSpace( To ) )
			throw new ArgumentException( "Organic tendons need a to= name.", nameof( To ) );
		if ( Strands < 1 || Strands > 64 )
			throw new ArgumentOutOfRangeException( nameof( Strands ), "Strands must be 1..64." );
		if ( !float.IsFinite( Twist ) || Twist < -10f || Twist > 10f )
			throw new ArgumentOutOfRangeException( nameof( Twist ), "Twist must be -10..10 turns." );
		if ( !float.IsFinite( Radius ) || Radius <= 0f || Radius > 10f )
			throw new ArgumentOutOfRangeException( nameof( Radius ), "Radius must be positive and at most 10." );
		if ( Sides < 4 || Sides > 16 )
			throw new ArgumentOutOfRangeException( nameof( Sides ), "Sides must be 4..16 for a quad tube." );
		if ( Steps < 4 || Steps > 64 )
			throw new ArgumentOutOfRangeException( nameof( Steps ), "Steps must be 4..64." );
		if ( Seed < 0 )
			throw new ArgumentOutOfRangeException( nameof( Seed ), "Seed must be 0 or more." );
		if ( !float.IsFinite( Clump ) || Clump < 0f || Clump > 1f )
			throw new ArgumentOutOfRangeException( nameof( Clump ), "Clump must be 0..1." );
		if ( !float.IsFinite( Jitter ) || Jitter < 0f || Jitter > 1f )
			throw new ArgumentOutOfRangeException( nameof( Jitter ), "Jitter must be 0..1." );
	}
}

/// <summary>
/// Parses the script verb shape, with NO engine or script-system dependency so
/// the wiring step can call it from anywhere:
/// <c>tendons bone=arm_upper_L to=arm_lower_L strands=8 twist=1.2</c>
/// Optional keys: <c>radius=</c>, <c>sides=</c>, <c>steps=</c>, <c>seed=</c>,
/// <c>clump=</c>, <c>jitter=</c>. The leading <c>tendons</c> word is optional.
/// Unknown keys, duplicate keys, missing bone/to, and unparseable numbers all
/// throw <see cref="ArgumentException"/> instead of silently building the
/// wrong bundle — a typo in a verb must fail loudly at parse time.
/// </summary>
public static class OrganicTendonParser
{
	public static OrganicTendonOptions Parse( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) )
			throw new ArgumentException( "Empty tendons verb.", nameof( text ) );

		var options = new OrganicTendonOptions();
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var tokens = text.Split( new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries );
		var start = 0;

		if ( tokens[0].Equals( "tendons", StringComparison.OrdinalIgnoreCase ) )
			start = 1;

		for ( var i = start; i < tokens.Length; i++ )
		{
			var eq = tokens[i].IndexOf( '=' );
			if ( eq <= 0 )
				throw new ArgumentException( $"Tendons verb token '{tokens[i]}' is not key=value.", nameof( text ) );

			var key = tokens[i][..eq];
			var value = tokens[i][(eq + 1)..];
			if ( !seen.Add( key ) )
				throw new ArgumentException( $"Tendons verb repeats key '{key}'.", nameof( text ) );

			switch ( key.ToLowerInvariant() )
			{
				case "bone": options.Bone = value; break;
				case "to": options.To = value; break;
				case "strands": options.Strands = Int( key, value ); break;
				case "twist": options.Twist = Float( key, value ); break;
				case "radius": options.Radius = Float( key, value ); break;
				case "sides": options.Sides = Int( key, value ); break;
				case "steps": options.Steps = Int( key, value ); break;
				case "seed": options.Seed = Int( key, value ); break;
				case "clump": options.Clump = Float( key, value ); break;
				case "jitter": options.Jitter = Float( key, value ); break;
				default: throw new ArgumentException( $"Unknown tendons verb key '{key}'.", nameof( text ) );
			}
		}

		options.Validate();
		return options;
	}

	static int Int( string key, string value )
	{
		if ( !int.TryParse( value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result ) )
			throw new ArgumentException( $"Tendons verb key '{key}' needs an integer, got '{value}'." );
		return result;
	}

	static float Float( string key, string value )
	{
		if ( !float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result ) )
			throw new ArgumentException( $"Tendons verb key '{key}' needs a number, got '{value}'." );
		return result;
	}
}

/// <summary>
/// Builds the bundle. One <see cref="PolyMesh"/> holds every strand as its own
/// vertex range (no welding between strands — strands fuse visually at the
/// insertions by converging there, they do not share vertices).
///
/// Geometry, per strand, per ring:
/// <list type="bullet">
/// <item>Centreline follows the joint chain by arc length, so a bent chain bends
/// the bundle with it instead of cutting straight across the joint.</item>
/// <item>Frames are parallel-transported along the chain, so the twist angle is
/// measured against a frame that never flips on a bend.</item>
/// <item>Offset = orbit + clump, both rotated by the twist and scaled by
/// sin(pi*t): exactly zero at both insertions (fusion) and full mid-span.
/// Clumps are groups of 3 strands sharing one lateral offset direction; the
/// offset rotates with the twist, so a clump twists as a whole bundle.</item>
/// <item>Radius = base * (0.55 + 0.45*sin(pi*t)) + Poisson bumps: a
/// Poisson-distributed count of Gaussian bumps along the strand, each up to
/// Jitter*Radius tall. Ends narrow to 55% as they fuse; the belly stays full.
/// Jitter 0 is exactly the smooth taper, no bumps at all.</item>
/// <item>A separation pass per ring pushes strand centres apart until every
/// pair is at least 1.15x the sum of their radii apart — weighted by
/// min(1, envelope/0.5), so the fusion zone near the insertions (where
/// convergence is the point) is not fought, while the mid-span
/// (envelope &gt;= 0.5, i.e. roughly t in [1/6, 5/6]) is fully separated.
/// That pass is what makes the mid-span no-overlap claim constructive
/// rather than hoped-for.</item>
/// <item>Each tube is closed: side quads plus one ngon cap at each end, so
/// every edge of a strand's submesh is shared by exactly two faces.</item>
/// </list>
/// </summary>
public static class OrganicTendonBuilder
{
	const int ClumpSize = 3;

	public static PolyMesh Build( OrganicTendonOptions options, IReadOnlyList<Vec3> joints )
	{
		if ( options is null )
			throw new ArgumentNullException( nameof( options ) );
		options.Validate();

		if ( joints is null || joints.Count < 2 )
			throw new ArgumentException( "Organic tendons need a chain of at least 2 joints.", nameof( joints ) );

		for ( var i = 1; i < joints.Count; i++ )
			if ( (joints[i] - joints[i - 1]).LengthSquared < 1e-10f )
				throw new ArgumentException( $"Joint {i} duplicates joint {i - 1}; a chain needs distinct joints.", nameof( joints ) );

		var steps = options.Steps;
		var sides = options.Sides;
		var samples = Resample( joints, steps );
		var frames = TransportFrames( samples );
		var orbit = OrbitRadius( options );
		var clumpRadius = options.Clump * orbit;
		var clumpCount = Math.Max( 1, (options.Strands + ClumpSize - 1) / ClumpSize );

		// Centres and radii per strand per ring, before the tube is emitted.
		var centres = new Vec3[options.Strands][];
		var radii = new float[options.Strands][];

		for ( var s = 0; s < options.Strands; s++ )
		{
			centres[s] = new Vec3[steps + 1];
			radii[s] = new float[steps + 1];
			var rng = new Random( options.Seed + s * 7919 );
			var phase = options.Strands == 1 ? 0f : MathF.PI * 2f * s / options.Strands;
			var clumpId = s / ClumpSize;
			var clumpAngle = MathF.PI * 2f * clumpId / clumpCount;
			var bumps = PoissonBumps( rng, steps, options );

			for ( var i = 0; i <= steps; i++ )
			{
				var t = (float)i / steps;
				var envelope = MathF.Sin( MathF.PI * t );
				var twistAngle = options.Twist * MathF.PI * 2f * t;
				var (fx, fy) = frames[i];
				var strandAngle = phase + twistAngle;
				var offset = (fx * MathF.Cos( strandAngle ) + fy * MathF.Sin( strandAngle )) * orbit
					+ (fx * MathF.Cos( clumpAngle + twistAngle ) + fy * MathF.Sin( clumpAngle + twistAngle )) * clumpRadius;
				centres[s][i] = samples[i] + offset * envelope;
				// Clamped so stacked Poisson bumps can never blow a strand up:
				// radius always stays in [0.5, 2.0] x base, whatever the seed draws.
				radii[s][i] = Math.Clamp( options.Radius * (0.55f + 0.45f * envelope) + BumpAt( bumps, t ),
					0.5f * options.Radius, 2.0f * options.Radius );
			}
		}

		SeparateRings( centres, radii, steps );

		var mesh = new PolyMesh();
		for ( var s = 0; s < options.Strands; s++ )
		{
			var start = mesh.Positions.Count;
			for ( var i = 0; i <= steps; i++ )
			{
				var (fx, fy) = frames[i];
				for ( var k = 0; k < sides; k++ )
				{
					var a = MathF.PI * 2f * k / sides;
					mesh.Positions.Add( centres[s][i] + (fx * MathF.Cos( a ) + fy * MathF.Sin( a )) * radii[s][i] );
				}
			}

			for ( var i = 0; i < steps; i++ )
				for ( var k = 0; k < sides; k++ )
				{
					var k2 = (k + 1) % sides;
					mesh.Faces.Add( new Face( new[]
					{
						start + i * sides + k, start + i * sides + k2,
						start + (i + 1) * sides + k2, start + (i + 1) * sides + k
					} ) );
				}

			var capStart = new int[sides];
			var capEnd = new int[sides];
			for ( var k = 0; k < sides; k++ )
			{
				capStart[k] = start + (sides - 1 - k);
				capEnd[k] = start + steps * sides + k;
			}
			mesh.Faces.Add( new Face( capStart ) );
			mesh.Faces.Add( new Face( capEnd ) );
		}

		return mesh;
	}

	/// <summary>Orbit sized so adjacent strands clear each other before the
	/// separation pass: chord &gt; 2 radii, with margin, for the base radius.</summary>
	static float OrbitRadius( OrganicTendonOptions options )
	{
		if ( options.Strands <= 1 )
			return 0f;
		return options.Radius * 1.25f / MathF.Sin( MathF.PI / options.Strands );
	}

	static List<(float Centre, float Amp)> PoissonBumps( Random rng, int steps, OrganicTendonOptions options )
	{
		var bumps = new List<(float, float)>();
		// Count is Poisson(steps/6) by Knuth's algorithm; centres uniform in t.
		var lambda = Math.Max( 1f, steps / 6f );
		var limit = MathF.Exp( -lambda );
		var p = 1f;
		var count = 0;
		do
		{
			p *= (float)rng.NextDouble();
			count++;
		} while ( p > limit );
		count--;

		for ( var b = 0; b < count; b++ )
			bumps.Add( ((float)rng.NextDouble(), options.Jitter * options.Radius * (0.5f + 0.5f * (float)rng.NextDouble())) );

		return bumps;
	}

	static float BumpAt( List<(float Centre, float Amp)> bumps, float t )
	{
		var sum = 0f;
		foreach ( var (centre, amp) in bumps )
		{
			var d = (t - centre) / 0.10f;
			sum += amp * MathF.Exp( -d * d );
		}
		return sum;
	}

	static void SeparateRings( Vec3[][] centres, float[][] radii, int steps )
	{
		var strands = centres.Length;
		if ( strands <= 1 )
			return;

		for ( var i = 0; i <= steps; i++ )
		{
			var t = (float)i / steps;
			var weight = Math.Clamp( MathF.Sin( MathF.PI * t ) / 0.5f, 0f, 1f );
			if ( weight <= 0f )
				continue;

			for ( var iter = 0; iter < 24; iter++ )
			{
				var moved = false;
				for ( var a = 0; a < strands; a++ )
					for ( var b = a + 1; b < strands; b++ )
					{
						var req = (radii[a][i] + radii[b][i]) * 1.15f * weight;
						var diff = centres[b][i] - centres[a][i];
						var dist = diff.Length;
						if ( dist >= req || dist < 1e-6f )
							continue;
						var push = (req - dist) * 0.5f;
						var dir = diff / dist;
						centres[a][i] = centres[a][i] - dir * push;
						centres[b][i] = centres[b][i] + dir * push;
						moved = true;
					}
				if ( !moved )
					break;
			}
		}
	}

	static Vec3[] Resample( IReadOnlyList<Vec3> joints, int steps )
	{
		var total = 0f;
		var segLen = new float[joints.Count - 1];
		for ( var i = 0; i < joints.Count - 1; i++ )
		{
			segLen[i] = (joints[i + 1] - joints[i]).Length;
			total += segLen[i];
		}

		var samples = new Vec3[steps + 1];
		var seg = 0;
		var segStart = 0f;
		for ( var i = 0; i <= steps; i++ )
		{
			var target = total * i / steps;
			while ( seg < joints.Count - 2 && target > segStart + segLen[seg] )
			{
				segStart += segLen[seg];
				seg++;
			}
			var local = segLen[seg] > 1e-9f ? (target - segStart) / segLen[seg] : 0f;
			local = Math.Clamp( local, 0f, 1f );
			samples[i] = joints[seg] + (joints[seg + 1] - joints[seg]) * local;
		}
		samples[steps] = joints[^1];
		return samples;
	}

	static (Vec3, Vec3)[] TransportFrames( Vec3[] samples )
	{
		var n = samples.Length;
		var tangents = new Vec3[n];
		for ( var i = 0; i < n; i++ )
		{
			var a = samples[Math.Max( 0, i - 1 )];
			var b = samples[Math.Min( n - 1, i + 1 )];
			var d = b - a;
			tangents[i] = d.LengthSquared > 1e-12f ? d.Normal : new Vec3( 1, 0, 0 );
		}

		var frames = new (Vec3, Vec3)[n];
		Vec3 prevX = Vec3.Zero;
		for ( var i = 0; i < n; i++ )
		{
			var t = tangents[i];
			Vec3 x;
			if ( i == 0 )
			{
				var helper = MathF.Abs( t.z ) < 0.92f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
				x = Vec3.Cross( t, helper ).Normal;
			}
			else
			{
				x = prevX - t * Vec3.Dot( prevX, t );
				if ( x.LengthSquared < 1e-6f )
				{
					var helper = MathF.Abs( t.z ) < 0.92f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
					x = Vec3.Cross( t, helper ).Normal;
				}
				else
				{
					x = x.Normal;
				}
			}
			frames[i] = (x, Vec3.Cross( t, x ));
			prevX = x;
		}
		return frames;
	}
}
