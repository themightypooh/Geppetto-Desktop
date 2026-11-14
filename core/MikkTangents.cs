#nullable enable
using System;
using System.Collections.Generic;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Pure-C# MikkTSpace tangent provider for <see cref="NormalBake.MikkTangents"/>.
/// Mobile binds Godot's SurfaceTool for this; Desktop has no engine, so this managed
/// version runs everywhere the kernel runs. It implements the published mikktspace
/// algorithm (Mortensen et al., "MikkTSpace", zlib-licensed C reference; Mobile ships
/// that source under <c>vendor/mikktspace-LICENSE</c>), written independently here —
/// no mikktspace.c and no native library exists on Desktop.
///
/// Per triangle: sdir/tdir from positions + UVs (skipped when |det| &lt; 1e-12, a
/// degenerate-UV triangle that contributes nothing). Corners weld by exact
/// (position, shading normal, UV) equality — the same weld MikkTSpace performs —
/// with -0 normalised to +0 so equal keys hash equal. Each weld group's tangents
/// accumulate over its triangles, then orthonormalise against the corner's normal
/// (Gram-Schmidt). The tangent is NOT negated for handedness: a mirrored UV layout
/// flips the bitangent instead, so w carries the mirror and T stays the surface's
/// dP/du. A group with no usable tangent (all its triangles degenerate) yields
/// (0,0,0,1); <see cref="NormalBake"/> treats a zero tangent as "no tangent" and
/// its downstream AnyPerpendicular fallback covers the texel.
///
/// Wire it with <see cref="Install"/>; the integrator adds the one-liner to
/// <c>Program.Main</c> next to <c>SoftFail.HookKernel()</c>. Returns null for
/// invalid input (empty, not a triangle count, or mismatched array lengths), which
/// the hook contract reads as "could not run" and falls back from.
/// </summary>
public static class MikkTangents
{
	/// <summary>Wire this provider into <see cref="NormalBake.MikkTangents"/> if nothing is wired yet.</summary>
	public static void Install() => NormalBake.MikkTangents ??= Generate;

	public static Vec4[]? Generate( Vec3[] positions, Vec3[] normals, Vec2[] uvs )
	{
		if ( positions is null || normals is null || uvs is null ) return null;
		if ( positions.Length == 0 || positions.Length % 3 != 0 ) return null;
		if ( normals.Length != positions.Length || uvs.Length != positions.Length ) return null;

		int n = positions.Length;
		var groups = new Dictionary<(int, int, int, int, int, int, int, int), int>();
		var groupTan1 = new List<Vec3>();
		var groupTan2 = new List<Vec3>();
		var cornerGroup = new int[n];

		for ( int i = 0; i < n; i++ )
		{
			var key = WeldKey( positions[i], normals[i], uvs[i] );
			if ( !groups.TryGetValue( key, out int g ) )
			{
				g = groupTan1.Count;
				groups[key] = g;
				groupTan1.Add( Vec3.Zero );
				groupTan2.Add( Vec3.Zero );
			}
			cornerGroup[i] = g;
		}

		for ( int i = 0; i < n; i += 3 )
		{
			var e1 = positions[i + 1] - positions[i];
			var e2 = positions[i + 2] - positions[i];
			float s1 = uvs[i + 1].x - uvs[i].x;
			float t1 = uvs[i + 1].y - uvs[i].y;
			float s2 = uvs[i + 2].x - uvs[i].x;
			float t2 = uvs[i + 2].y - uvs[i].y;
			float det = s1 * t2 - s2 * t1;
			if ( MathF.Abs( det ) < 1e-12f ) continue;
			float inv = 1f / det;
			var sdir = new Vec3(
				( t2 * e1.x - t1 * e2.x ) * inv,
				( t2 * e1.y - t1 * e2.y ) * inv,
				( t2 * e1.z - t1 * e2.z ) * inv );
			var tdir = new Vec3(
				( s1 * e2.x - s2 * e1.x ) * inv,
				( s1 * e2.y - s2 * e1.y ) * inv,
				( s1 * e2.z - s2 * e1.z ) * inv );
			groupTan1[cornerGroup[i]] += sdir;
			groupTan1[cornerGroup[i + 1]] += sdir;
			groupTan1[cornerGroup[i + 2]] += sdir;
			groupTan2[cornerGroup[i]] += tdir;
			groupTan2[cornerGroup[i + 1]] += tdir;
			groupTan2[cornerGroup[i + 2]] += tdir;
		}

		var result = new Vec4[n];
		for ( int i = 0; i < n; i++ )
		{
			var nrm = normals[i];
			var t = groupTan1[cornerGroup[i]] - nrm * Vec3.Dot( nrm, groupTan1[cornerGroup[i]] );
			if ( t.LengthSquared < 1e-12f )
			{
				result[i] = new Vec4( 0f, 0f, 0f, 1f );
				continue;
			}
			t = t.Normal;
			var b = groupTan2[cornerGroup[i]] - nrm * Vec3.Dot( nrm, groupTan2[cornerGroup[i]] );
			float w = Vec3.Dot( Vec3.Cross( nrm, t ), b ) < 0f ? -1f : 1f;
			result[i] = new Vec4( t.x, t.y, t.z, w );
		}
		return result;
	}

	// Exact (position, normal, uv) equality as the weld key, with -0 normalised to
	// +0: float.GetHashCode distinguishes -0 from +0, so a raw float key would split
	// one weld group into two whenever a computation produced -0.
	static (int, int, int, int, int, int, int, int) WeldKey( Vec3 p, Vec3 n, Vec2 u )
	{
		static int Bits( float f ) => f == 0f ? 0 : BitConverter.SingleToInt32Bits( f );
		return (Bits( p.x ), Bits( p.y ), Bits( p.z ), Bits( n.x ), Bits( n.y ), Bits( n.z ), Bits( u.x ), Bits( u.y ));
	}
}
