#nullable enable
using System;
using System.Collections.Generic;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Procedural physics-driven character animation (Euphoria-style): balance, reach, stumble/recover, muscle stiffness, blend with authored clips.
/// Pure, deterministic, single-threaded.
/// </summary>
public static class EuphoriaService
{
	public sealed class BalanceState
	{
		public Vec3 CenterOfMass { get; set; } = Vec3.Zero;
		public List<Vec3> SupportFeet { get; set; } = new();
		public float Stiffness { get; set; } = 1f;
	}

	/// <summary>Center of mass of a set of weighted points (positions, masses).</summary>
	public static Vec3 CenterOfMass( IReadOnlyList<(Vec3 Pos, float Mass)> points )
	{
		Vec3 sum = Vec3.Zero; float msum = 0f;
		foreach ( var (p, m) in points ) { sum += p * m; msum += m; }
		if ( msum < 1e-9f ) return points.Count > 0 ? points[0].Pos : Vec3.Zero;
		return sum / msum;
	}

	/// <summary>Support polygon centroid (average of foot positions).</summary>
	public static Vec3 SupportCentroid( IReadOnlyList<Vec3> feet )
	{
		if ( feet == null || feet.Count == 0 ) return Vec3.Zero;
		Vec3 s = Vec3.Zero; foreach ( var f in feet ) s += f; return s / feet.Count;
	}

	/// <summary>Whether COM projects inside support polygon (2D XZ). Uses winding / point-in-convex-hull via centroid distance heuristic.</summary>
	public static bool IsBalanced( Vec3 com, IReadOnlyList<Vec3> feet, float margin = 0f )
	{
		if ( feet == null || feet.Count < 2 ) return true;
		// Project to XZ
		var c = new Vec2( com.x, com.z );
		// Build polygon and test point in polygon (ray casting)
		bool inside = false;
		int n = feet.Count;
		for ( int i = 0, j = n - 1; i < n; j = i++ )
		{
			var pi = new Vec2( feet[i].x, feet[i].z );
			var pj = new Vec2( feet[j].x, feet[j].z );
			bool cond = ((pi.y > c.y) != (pj.y > c.y)) &&
				(c.x < (pj.x - pi.x) * (c.y - pi.y) / (pj.y - pi.y) + pi.x);
			if ( cond ) inside = !inside;
		}
		if ( !inside ) return false;
		// margin: distance to centroid
		var cent = new Vec2( SupportCentroid( feet ).x, SupportCentroid( feet ).z );
		float dist = (c - cent).Length;
		float maxR = 0f; foreach ( var f in feet ) { float r = (new Vec2( f.x, f.z ) - cent).Length; if ( r > maxR ) maxR = r; }
		return dist <= maxR + margin;
	}

	/// <summary>Foot placement target to keep COM over support: shift feet toward COM projection.</summary>
	public static Vec3 FootPlacement( Vec3 com, Vec3 currentFoot, float strength )
	{
		Vec3 delta = new Vec3( com.x - currentFoot.x, 0, com.z - currentFoot.z ) * strength;
		return currentFoot + delta;
	}

	// --- Reach ----------------------------------------------------------------------

	/// <summary>Reach target with arm IK: returns end-effector position after clamping reach (body lean included as offset).</summary>
	public static Vec3 Reach( Vec3 shoulder, Vec3 target, float armLen, out Vec3 lean )
	{
		Vec3 toTarget = target - shoulder;
		float dist = toTarget.Length;
		lean = Vec3.Zero;
		if ( dist <= armLen )
		{
			return target;
		}
		// body lean toward target
		lean = toTarget.Normal * MathF.Min( 0.2f, (dist - armLen) * 0.1f );
		Vec3 dir = toTarget.Normal;
		return shoulder + dir * armLen;
	}

	// --- Stumble / recover ----------------------------------------------------------

	public sealed class StumbleState
	{
		public float Time { get; set; }
		public Vec3 Velocity { get; set; } = Vec3.Zero;
		public bool Recovering { get; set; }
	}

	/// <summary>Applies an impulse; returns recovery force (opposite) scaled by stiffness.</summary>
	public static Vec3 StumbleImpulse( Vec3 push, float stiffness )
	{
		return push * (1f - Math.Clamp( stiffness, 0f, 1f ));
	}

	/// <summary>Recovery step: move COM back toward support centroid.</summary>
	public static Vec3 RecoverStep( Vec3 com, Vec3 supportCentroid, float stiffness, float dt )
	{
		Vec3 delta = supportCentroid - com;
		return com + delta * (stiffness * dt);
	}

	// --- Muscle stiffness -----------------------------------------------------------

	public static float ApplyStiffness( float value, float target, float stiffness, float dt )
	{
		return value + (target - value) * Math.Clamp( stiffness, 0f, 1f ) * dt;
	}

	// --- Blend with authored animation ---------------------------------------------

	/// <summary>Blends procedural pose with authored pose by weight (0=authored, 1=procedural).</summary>
	public static Vec3 BlendPose( Vec3 authored, Vec3 procedural, float weight )
	{
		weight = Math.Clamp( weight, 0f, 1f );
		return authored * (1f - weight) + procedural * weight;
	}

	// --- Determinism helper ---------------------------------------------------------

	public static int DeterministicHash( int seed, int step )
	{
		unchecked
		{
			int h = seed;
			h = h * 31 + step;
			h ^= h << 13;
			h ^= h >> 17;
			h ^= h << 5;
			return h;
		}
	}
}
