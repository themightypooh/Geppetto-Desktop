using System;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>Ray/box test moved out of SkeletonOverlay (GL) so MeshPicker (Core) can use it. SkeletonOverlay.RayHitsAabb becomes a one-line forward.</summary>
public static class RayMath
{
	public static bool RayHitsAabb( Vector3 ro, Vector3 rd, Vector3 min, Vector3 max, out float tEnter )
	{
		tEnter = 0f;
		float tMin = 0.01f;
		float tMax = float.MaxValue;

		for ( int axis = 0; axis < 3; axis++ )
		{
			float o = axis == 0 ? ro.X : axis == 1 ? ro.Y : ro.Z;
			float d = axis == 0 ? rd.X : axis == 1 ? rd.Y : rd.Z;
			float bMin = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
			float bMax = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;

			if ( MathF.Abs( d ) < 1e-7f )
			{
				if ( o < bMin || o > bMax ) return false;
			}
			else
			{
				float invD = 1f / d;
				float t1 = (bMin - o) * invD;
				float t2 = (bMax - o) * invD;
				if ( t1 > t2 ) (t1, t2) = (t2, t1);
				if ( t1 > tMin ) tMin = t1;
				if ( t2 < tMax ) tMax = t2;
				if ( tMin > tMax ) return false;
			}
		}

		tEnter = tMin;
		return true;
	}
}
