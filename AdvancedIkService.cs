#nullable enable
using System;
using System.Collections.Generic;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Advanced IK helpers: foot planting, look-at, angle constraints, FABRIK chains, multi-target blend.
/// Pure math, single-threaded; no viewport or document coupling.
/// </summary>
public static class AdvancedIkService
{
	// --- Foot planting ---------------------------------------------------------------

	/// <summary>Projects a foot position onto a ground plane (normal + offset) and returns the planted point.</summary>
	public static Vec3 PlantFoot( Vec3 foot, Vec3 groundNormal, float groundOffset )
	{
		if ( groundNormal.LengthSquared < 1e-20f ) groundNormal = new Vec3( 0, 1, 0 );
		groundNormal = groundNormal.Normal;
		float d = Vec3.Dot( foot, groundNormal ) - groundOffset;
		return foot - groundNormal * d;
	}

	/// <summary>Adjusts a two-joint leg chain (hip->knee->ankle) so the ankle lands on target, clamping reach.</summary>
	public static (Vec3 knee, Vec3 ankle) SolveLeg( Vec3 hip, Vec3 ankleTarget, float upperLen, float lowerLen, Vec3 poleHint )
	{
		float total = upperLen + lowerLen;
		Vec3 toTarget = ankleTarget - hip;
		float dist = toTarget.Length;
		if ( dist < 1e-6f ) dist = 1e-6f;
		if ( dist > total ) toTarget = toTarget / dist * total;
		dist = MathF.Min( dist, total );
		// law of cosines for knee angle
		float cosKnee = (upperLen * upperLen + dist * dist - lowerLen * lowerLen) / (2f * upperLen * dist);
		cosKnee = Math.Clamp( cosKnee, -1f, 1f );
		float kneeAngle = MathF.Acos( cosKnee );
		Vec3 dir = toTarget.Normal;
		// pole vector perpendicular component
		Vec3 pole = poleHint - hip;
		Vec3 perp = pole - dir * Vec3.Dot( pole, dir );
		if ( perp.LengthSquared < 1e-20f ) perp = new Vec3( 0, 0, 1 );
		perp = perp.Normal;
		Vec3 knee = hip + dir * (upperLen * MathF.Cos( kneeAngle )) + perp * (upperLen * MathF.Sin( kneeAngle ));
		Vec3 ankle = ankleTarget;
		return (knee, ankle);
	}

	// --- Look-at --------------------------------------------------------------------

	/// <summary>Returns a unit vector from eye toward target (the look direction).</summary>
	public static Vec3 LookDirection( Vec3 eye, Vec3 target )
	{
		Vec3 d = target - eye;
		if ( d.LengthSquared < 1e-20f ) return new Vec3( 0, 0, 1 );
		return d.Normal;
	}

	/// <summary>Yaw (radians) around Y to face target, 0 = +Z forward.</summary>
	public static float LookYaw( Vec3 eye, Vec3 target )
	{
		Vec3 d = target - eye;
		return MathF.Atan2( d.x, d.z );
	}

	/// <summary>Pitch (radians) to face target.</summary>
	public static float LookPitch( Vec3 eye, Vec3 target )
	{
		Vec3 d = target - eye;
		float horiz = MathF.Sqrt( d.x * d.x + d.z * d.z );
		return MathF.Atan2( d.y, horiz );
	}

	// --- Constraints ----------------------------------------------------------------

	public static float ClampAngle( float angle, float min, float max ) => Math.Clamp( angle, min, max );

	public static Vec3 ClampToCone( Vec3 dir, Vec3 axis, float halfAngle )
	{
		if ( dir.LengthSquared < 1e-20f ) return axis.Normal;
		dir = dir.Normal;
		axis = axis.Normal;
		float cos = Vec3.Dot( dir, axis );
		float limit = MathF.Cos( halfAngle );
		if ( cos >= limit ) return dir;
		// rotate dir toward axis
		Vec3 cross = Vec3.Cross( dir, axis );
		// reconstruct
		float sin = MathF.Sqrt( MathF.Max( 0f, 1f - limit * limit ) );
		// simple: lerp direction toward axis
		return (axis * limit + (dir - axis * cos).Normal * sin).Normal;
	}

	// --- FABRIK chain ---------------------------------------------------------------

	/// <summary>Solves a FABRIK chain (positions) to reach target. Returns new positions.</summary>
	public static Vec3[] Fabrik( Vec3[] chain, Vec3 target, int iterations = 10, float tolerance = 1e-4f )
	{
		if ( chain == null || chain.Length < 2 ) throw new ArgumentException( "Chain must have at least 2 joints." );
		int n = chain.Length;
		var p = (Vec3[])chain.Clone();
		// segment lengths
		var len = new float[n - 1];
		for ( int i = 0; i < n - 1; i++ ) len[i] = (p[i + 1] - p[i]).Length;
		float total = 0f; foreach ( var l in len ) total += l;
		Vec3 root = p[0];

		for ( int it = 0; it < iterations; it++ )
		{
			// forward reaching
			p[n - 1] = target;
			for ( int i = n - 2; i >= 0; i-- )
			{
				Vec3 dir = (p[i] - p[i + 1]);
				float d = dir.Length;
				if ( d < 1e-9f ) d = 1e-9f;
				p[i] = p[i + 1] + dir / d * len[i];
			}
			// backward reaching
			p[0] = root;
			for ( int i = 0; i < n - 1; i++ )
			{
				Vec3 dir = (p[i + 1] - p[i]);
				float d = dir.Length;
				if ( d < 1e-9f ) d = 1e-9f;
				p[i + 1] = p[i] + dir / d * len[i];
			}
			if ( (p[n - 1] - target).Length < tolerance ) break;
		}
		// if target unreachable, stretch
		if ( (target - root).Length > total )
		{
			Vec3 dir = (target - root).Normal;
			p[0] = root;
			for ( int i = 0; i < n - 1; i++ ) p[i + 1] = p[i] + dir * len[i];
		}
		return p;
	}

	// --- Multi-target blend ---------------------------------------------------------

	/// <summary>Blends multiple IK goal positions by weights into a single target.</summary>
	public static Vec3 BlendTargets( IReadOnlyList<(Vec3 Target, float Weight)> targets )
	{
		if ( targets == null || targets.Count == 0 ) throw new ArgumentException( "No targets." );
		Vec3 sum = Vec3.Zero;
		float wsum = 0f;
		foreach ( var (t, w) in targets )
		{
			if ( !float.IsFinite( w ) ) continue;
			sum += t * w;
			wsum += w;
		}
		if ( MathF.Abs( wsum ) < 1e-9f ) return targets[0].Target;
		return sum / wsum;
	}

	// --- Twist ----------------------------------------------------------------------

	/// <summary>Twists a vector around an axis by angle (radians).</summary>
	public static Vec3 Twist( Vec3 v, Vec3 axis, float angle )
	{
		axis = axis.Normal;
		float cos = MathF.Cos( angle ), sin = MathF.Sin( angle );
		return v * cos + Vec3.Cross( axis, v ) * sin + axis * Vec3.Dot( axis, v ) * (1f - cos);
	}
}


