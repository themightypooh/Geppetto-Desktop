#nullable enable
using System;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>
/// Identifies coordinate axes for gizmo manipulation.
/// </summary>
public enum GizmoAxis
{
	None = 0,
	X,
	Y,
	Z,
	Uniform
}

/// <summary>
/// Identifies planar quadrants for 2D gizmo translation.
/// </summary>
public enum GizmoPlane
{
	None = 0,
	XY, // Normal: Z
	YZ, // Normal: X
	XZ  // Normal: Y
}

/// <summary>
/// Distinct kinds of transform handles to avoid confusing translation, rotation, and scaling.
/// </summary>
public enum GizmoHandleKind
{
	None = 0,
	TranslateAxis,
	TranslatePlane,
	RotateRing,
	ScaleAxis,
	ScaleUniform
}

/// <summary>
/// Bitmask filter for selective handle testing and drawing.
/// </summary>
[Flags]
public enum GizmoHandleFilter
{
	None = 0,
	TranslateAxes = 1 << 0,
	TranslatePlanes = 1 << 1,
	RotateRings = 1 << 2,
	ScaleAxes = 1 << 3,
	ScaleUniform = 1 << 4,
	Translate = TranslateAxes | TranslatePlanes,
	Rotate = RotateRings,
	Scale = ScaleAxes | ScaleUniform,
	All = Translate | Rotate | Scale
}

/// <summary>
/// Strongly typed handle identifier guaranteeing that an X axis translation handle
/// cannot be confused with an X rotation ring or an XY plane handle.
/// </summary>
public readonly record struct GizmoHandleId(GizmoHandleKind Kind, GizmoAxis Axis, GizmoPlane Plane = GizmoPlane.None)
{
	public static readonly GizmoHandleId None = new(GizmoHandleKind.None, GizmoAxis.None, GizmoPlane.None);

	public static readonly GizmoHandleId TranslateX = new(GizmoHandleKind.TranslateAxis, GizmoAxis.X);
	public static readonly GizmoHandleId TranslateY = new(GizmoHandleKind.TranslateAxis, GizmoAxis.Y);
	public static readonly GizmoHandleId TranslateZ = new(GizmoHandleKind.TranslateAxis, GizmoAxis.Z);

	public static readonly GizmoHandleId TranslateXY = new(GizmoHandleKind.TranslatePlane, GizmoAxis.None, GizmoPlane.XY);
	public static readonly GizmoHandleId TranslateYZ = new(GizmoHandleKind.TranslatePlane, GizmoAxis.None, GizmoPlane.YZ);
	public static readonly GizmoHandleId TranslateXZ = new(GizmoHandleKind.TranslatePlane, GizmoAxis.None, GizmoPlane.XZ);

	public static readonly GizmoHandleId RotateX = new(GizmoHandleKind.RotateRing, GizmoAxis.X);
	public static readonly GizmoHandleId RotateY = new(GizmoHandleKind.RotateRing, GizmoAxis.Y);
	public static readonly GizmoHandleId RotateZ = new(GizmoHandleKind.RotateRing, GizmoAxis.Z);

	public static readonly GizmoHandleId ScaleX = new(GizmoHandleKind.ScaleAxis, GizmoAxis.X);
	public static readonly GizmoHandleId ScaleY = new(GizmoHandleKind.ScaleAxis, GizmoAxis.Y);
	public static readonly GizmoHandleId ScaleZ = new(GizmoHandleKind.ScaleAxis, GizmoAxis.Z);
	public static readonly GizmoHandleId ScaleUniform = new(GizmoHandleKind.ScaleUniform, GizmoAxis.Uniform);

	public bool IsValid => Kind != GizmoHandleKind.None;
}

/// <summary>
/// Explicit local or world 3D coordinate frame for gizmo handles.
/// </summary>
public readonly struct GizmoFrame
{
	public Vector3 Origin { get; }
	public Vector3 AxisX { get; }
	public Vector3 AxisY { get; }
	public Vector3 AxisZ { get; }

	public static readonly GizmoFrame World = new(
		Vector3.Zero,
		Vector3.UnitX,
		Vector3.UnitY,
		Vector3.UnitZ
	);

	public GizmoFrame(Vector3 origin, Vector3 axisX, Vector3 axisY, Vector3 axisZ)
	{
		Origin = origin;
		AxisX = axisX.LengthSquared() > 0 ? Vector3.Normalize(axisX) : Vector3.UnitX;
		AxisY = axisY.LengthSquared() > 0 ? Vector3.Normalize(axisY) : Vector3.UnitY;
		AxisZ = axisZ.LengthSquared() > 0 ? Vector3.Normalize(axisZ) : Vector3.UnitZ;
	}

	public GizmoFrame(Vector3 origin, Quaternion orientation)
	{
		Origin = origin;
		var rot = Matrix4x4.CreateFromQuaternion(orientation);
		var x = new Vector3(rot.M11, rot.M12, rot.M13);
		var y = new Vector3(rot.M21, rot.M22, rot.M23);
		var z = new Vector3(rot.M31, rot.M32, rot.M33);
		AxisX = x.LengthSquared() > 0 ? Vector3.Normalize(x) : Vector3.UnitX;
		AxisY = y.LengthSquared() > 0 ? Vector3.Normalize(y) : Vector3.UnitY;
		AxisZ = z.LengthSquared() > 0 ? Vector3.Normalize(z) : Vector3.UnitZ;
	}

	public Vector3 GetAxis(GizmoAxis axis) => axis switch
	{
		GizmoAxis.X => AxisX,
		GizmoAxis.Y => AxisY,
		GizmoAxis.Z => AxisZ,
		_ => Vector3.Zero
	};

	public Vector3 GetPlaneNormal(GizmoPlane plane) => plane switch
	{
		GizmoPlane.XY => AxisZ,
		GizmoPlane.YZ => AxisX,
		GizmoPlane.XZ => AxisY,
		_ => Vector3.Zero
	};

	public (Vector3 U, Vector3 V) GetPlaneAxes(GizmoPlane plane) => plane switch
	{
		GizmoPlane.XY => (AxisX, AxisY),
		GizmoPlane.YZ => (AxisY, AxisZ),
		GizmoPlane.XZ => (AxisX, AxisZ),
		_ => (Vector3.Zero, Vector3.Zero)
	};

	public Vector3 ToLocal(Vector3 worldPoint)
	{
		var delta = worldPoint - Origin;
		return new Vector3(Vector3.Dot(delta, AxisX), Vector3.Dot(delta, AxisY), Vector3.Dot(delta, AxisZ));
	}

	public Vector3 ToWorld(Vector3 localPoint)
	{
		return Origin + AxisX * localPoint.X + AxisY * localPoint.Y + AxisZ * localPoint.Z;
	}
}

/// <summary>
/// 3D ray with origin and normalized direction.
/// </summary>
public readonly record struct Ray(Vector3 Origin, Vector3 Direction)
{
	public Ray(Vector3 origin, Vector3 direction, bool normalize = true)
		: this(origin, normalize && direction.LengthSquared() > 0 ? Vector3.Normalize(direction) : direction)
	{
	}

	public Vector3 GetPoint(float distance) => Origin + Direction * distance;

	public bool IsFinite =>
		float.IsFinite(Origin.X) && float.IsFinite(Origin.Y) && float.IsFinite(Origin.Z) &&
		float.IsFinite(Direction.X) && float.IsFinite(Direction.Y) && float.IsFinite(Direction.Z);
}

/// <summary>
/// Result of a gizmo ray-cast hit test.
/// </summary>
public readonly record struct GizmoHitResult(
	bool IsHit,
	GizmoHandleId HandleId,
	float RayDistance,
	Vector3 HitPoint
)
{
	public static readonly GizmoHitResult Miss = new(false, GizmoHandleId.None, float.MaxValue, Vector3.Zero);
}

/// <summary>
/// Geometric proportions and thresholds for gizmo hit testing.
/// </summary>
public sealed class GizmoConfig
{
	public float AxisLength { get; set; } = 1.0f;
	public float AxisHitRadius { get; set; } = 0.12f;
	public float PlaneOffsetMin { get; set; } = 0.2f;
	public float PlaneOffsetMax { get; set; } = 0.6f;
	public float RingRadius { get; set; } = 1.0f;
	public float RingHitThickness { get; set; } = 0.12f;
	public float UniformScaleHitRadius { get; set; } = 0.18f;
	public float MinScale { get; set; } = 1e-4f;
	public float ParallelDotThreshold { get; set; } = 1e-3f;
}

/// <summary>
/// Pure CPU ray/handle intersection, sizing, and snapping math for 3D viewport gizmos.
/// </summary>
public static class GizmoMath
{
	public const float DefaultParallelThreshold = 1e-3f;

	#region Ray and Plane Intersection

	/// <summary>
	/// Intersect a ray with a plane defined by a point and unit normal.
	/// Returns false if parallel, near-parallel, or if the plane lies behind the ray origin.
	/// </summary>
	public static bool TryIntersectPlane(
		Ray ray,
		Vector3 planePoint,
		Vector3 planeNormal,
		out float distance,
		out Vector3 hitPoint,
		float parallelThreshold = DefaultParallelThreshold)
	{
		distance = 0f;
		hitPoint = Vector3.Zero;

		if (!ray.IsFinite || !float.IsFinite(planeNormal.X) || !float.IsFinite(planeNormal.Y) || !float.IsFinite(planeNormal.Z))
			return false;

		float normalLenSq = planeNormal.LengthSquared();
		if (normalLenSq < 1e-7f || ray.Direction.LengthSquared() < 1e-7f)
			return false;

		float denom = Vector3.Dot(ray.Direction, planeNormal);
		if (MathF.Abs(denom) < parallelThreshold)
			return false; // Parallel or near-parallel: no stable solution

		float t = Vector3.Dot(planePoint - ray.Origin, planeNormal) / denom;
		if (!float.IsFinite(t) || t < 0f)
			return false; // Behind ray origin

		distance = t;
		hitPoint = ray.GetPoint(t);
		return true;
	}

	#endregion

	#region Ray and Line / Segment Math

	/// <summary>
	/// Computes closest points between a semi-infinite ray and an infinite line.
	/// Returns false if ray and line are parallel, near-parallel, degenerate, or if ray is behind.
	/// </summary>
	public static bool TryClosestPointsRayLine(
		Ray ray,
		Vector3 linePoint,
		Vector3 lineDir,
		out float rayT,
		out float lineS,
		out Vector3 closestOnRay,
		out Vector3 closestOnLine,
		float parallelThreshold = DefaultParallelThreshold)
	{
		rayT = 0f;
		lineS = 0f;
		closestOnRay = Vector3.Zero;
		closestOnLine = Vector3.Zero;

		if (!ray.IsFinite || !float.IsFinite(lineDir.X) || !float.IsFinite(lineDir.Y) || !float.IsFinite(lineDir.Z))
			return false;

		float rayDirLenSq = ray.Direction.LengthSquared();
		float lineDirLenSq = lineDir.LengthSquared();
		if (rayDirLenSq < 1e-7f || lineDirLenSq < 1e-7f)
			return false;

		var d1 = ray.Direction;
		var d2 = Vector3.Normalize(lineDir);
		var w0 = ray.Origin - linePoint;

		float b = Vector3.Dot(d1, d2);
		float denom = 1f - b * b;

		// Ray is parallel or near-parallel to line (e.g. camera looking straight down an axis)
		if (denom < parallelThreshold)
			return false;

		float d = Vector3.Dot(d1, w0);
		float e = Vector3.Dot(d2, w0);

		float t = (b * e - d) / denom;
		float s = (e - b * d) / denom;

		if (!float.IsFinite(t) || !float.IsFinite(s))
			return false;

		rayT = t;
		lineS = s;
		closestOnRay = ray.GetPoint(t);
		closestOnLine = linePoint + d2 * s;
		return true;
	}

	/// <summary>
	/// Computes closest point from a ray to a finite line segment [segmentStart, segmentStart + segmentVector].
	/// Returns false if parallel/near-parallel or if the closest point on the ray lies behind the camera.
	/// </summary>
	public static bool TryClosestPointRaySegment(
		Ray ray,
		Vector3 segmentStart,
		Vector3 segmentVector,
		out float rayT,
		out float segmentT,
		out Vector3 closestOnRay,
		out Vector3 closestOnSegment,
		float parallelThreshold = DefaultParallelThreshold)
	{
		rayT = 0f;
		segmentT = 0f;
		closestOnRay = Vector3.Zero;
		closestOnSegment = Vector3.Zero;

		float segLen = segmentVector.Length();
		if (segLen < 1e-6f)
			return false;

		var segDir = segmentVector / segLen;
		if (!TryClosestPointsRayLine(ray, segmentStart, segDir, out float rT, out float lS, out var ptRay, out var ptLine, parallelThreshold))
			return false;

		if (rT < 0f)
			return false; // Entire interaction is behind the ray origin

		float clampedS = Math.Clamp(lS, 0f, segLen);
		var ptSeg = segmentStart + segDir * clampedS;

		// Recompute ray point if segment point was clamped to end cap
		float recomputedT = MathF.Max(0f, Vector3.Dot(ptSeg - ray.Origin, ray.Direction));

		rayT = recomputedT;
		segmentT = clampedS / segLen;
		closestOnRay = ray.GetPoint(recomputedT);
		closestOnSegment = ptSeg;
		return true;
	}

	#endregion

	#region Individual Handle Hit Testing

	/// <summary>
	/// Hit test an axis handle (Translate or Scale axis).
	/// </summary>
	public static bool TryHitAxis(
		Ray ray,
		GizmoFrame frame,
		GizmoAxis axis,
		float handleScale,
		float lengthFactor,
		float hitRadiusFactor,
		out float rayDistance,
		out Vector3 hitPoint,
		float parallelThreshold = DefaultParallelThreshold)
	{
		rayDistance = 0f;
		hitPoint = Vector3.Zero;

		var axisDir = frame.GetAxis(axis);
		if (axisDir == Vector3.Zero || handleScale <= 1e-6f)
			return false;

		float length = handleScale * lengthFactor;
		float hitRadius = handleScale * hitRadiusFactor;

		if (!TryClosestPointRaySegment(ray, frame.Origin, axisDir * length, out float rT, out _, out var ptRay, out var ptSeg, parallelThreshold))
			return false;

		float distSq = (ptRay - ptSeg).LengthSquared();
		if (distSq <= hitRadius * hitRadius)
		{
			rayDistance = rT;
			hitPoint = ptSeg;
			return true;
		}

		return false;
	}

	/// <summary>
	/// Hit test a planar translation quadrant handle (XY, YZ, or XZ).
	/// </summary>
	public static bool TryHitPlane(
		Ray ray,
		GizmoFrame frame,
		GizmoPlane plane,
		float handleScale,
		float minOffsetFactor,
		float maxOffsetFactor,
		out float rayDistance,
		out Vector3 hitPoint,
		float parallelThreshold = DefaultParallelThreshold)
	{
		rayDistance = 0f;
		hitPoint = Vector3.Zero;

		if (handleScale <= 1e-6f)
			return false;

		var normal = frame.GetPlaneNormal(plane);
		var (uAxis, vAxis) = frame.GetPlaneAxes(plane);
		if (normal == Vector3.Zero || uAxis == Vector3.Zero || vAxis == Vector3.Zero)
			return false;

		if (!TryIntersectPlane(ray, frame.Origin, normal, out float dist, out var pt, parallelThreshold))
			return false;

		var delta = pt - frame.Origin;
		float u = Vector3.Dot(delta, uAxis);
		float v = Vector3.Dot(delta, vAxis);

		float minU = handleScale * minOffsetFactor;
		float maxU = handleScale * maxOffsetFactor;
		float minV = handleScale * minOffsetFactor;
		float maxV = handleScale * maxOffsetFactor;

		if (u >= minU && u <= maxU && v >= minV && v <= maxV)
		{
			rayDistance = dist;
			hitPoint = pt;
			return true;
		}

		return false;
	}

	/// <summary>
	/// Hit test a rotation ring handle about a primary axis.
	/// </summary>
	public static bool TryHitRing(
		Ray ray,
		GizmoFrame frame,
		GizmoAxis ringAxis,
		float handleScale,
		float radiusFactor,
		float thicknessFactor,
		out float rayDistance,
		out Vector3 hitPoint,
		float parallelThreshold = DefaultParallelThreshold)
	{
		rayDistance = 0f;
		hitPoint = Vector3.Zero;

		if (handleScale <= 1e-6f)
			return false;

		var normal = frame.GetAxis(ringAxis);
		if (normal == Vector3.Zero)
			return false;

		if (!TryIntersectPlane(ray, frame.Origin, normal, out float dist, out var pt, parallelThreshold))
			return false;

		float distFromOrigin = (pt - frame.Origin).Length();
		float targetRadius = handleScale * radiusFactor;
		float thickness = handleScale * thicknessFactor;

		if (MathF.Abs(distFromOrigin - targetRadius) <= thickness)
		{
			rayDistance = dist;
			hitPoint = pt;
			return true;
		}

		return false;
	}

	/// <summary>
	/// Hit test a center uniform scaling handle.
	/// </summary>
	public static bool TryHitScaleUniform(
		Ray ray,
		GizmoFrame frame,
		float handleScale,
		float hitRadiusFactor,
		out float rayDistance,
		out Vector3 hitPoint)
	{
		rayDistance = 0f;
		hitPoint = Vector3.Zero;

		if (!ray.IsFinite || ray.Direction.LengthSquared() < 1e-7f || handleScale <= 1e-6f)
			return false;

		float radius = handleScale * hitRadiusFactor;
		var m = ray.Origin - frame.Origin;
		float b = Vector3.Dot(m, ray.Direction);
		float c = Vector3.Dot(m, m) - radius * radius;

		if (c > 0f && b > 0f)
			return false; // Outside sphere and pointing away

		float discr = b * b - c;
		if (discr < 0f)
			return false;

		float sqrtDiscr = MathF.Sqrt(discr);
		float t = -b - sqrtDiscr;
		if (t < 0f)
			t = -b + sqrtDiscr;

		if (t < 0f)
			return false;

		rayDistance = t;
		hitPoint = ray.GetPoint(t);
		return true;
	}

	#endregion

	#region Composite Hit Testing

	/// <summary>
	/// Comprehensive hit test against all active handles in the gizmo, returning the nearest hit.
	/// </summary>
	public static GizmoHitResult HitTest(
		Ray ray,
		GizmoFrame frame,
		float handleScale,
		GizmoHandleFilter filter = GizmoHandleFilter.All,
		GizmoConfig? config = null)
	{
		config ??= new GizmoConfig();

		if (handleScale <= 1e-6f || !ray.IsFinite || ray.Direction.LengthSquared() < 1e-7f)
			return GizmoHitResult.Miss;

		float bestDist = float.MaxValue;
		var bestId = GizmoHandleId.None;
		var bestPoint = Vector3.Zero;

		void TestHit(GizmoHandleId id, bool hit, float dist, Vector3 point)
		{
			if (hit && dist > 0f && dist < bestDist)
			{
				bestDist = dist;
				bestId = id;
				bestPoint = point;
			}
		}

		// 1. Center Uniform Scale (prioritized if ray hits center)
		if ((filter & GizmoHandleFilter.ScaleUniform) != 0)
		{
			bool hit = TryHitScaleUniform(ray, frame, handleScale, config.UniformScaleHitRadius, out float d, out var pt);
			TestHit(GizmoHandleId.ScaleUniform, hit, d, pt);
		}

		// 2. Translation Planes
		if ((filter & GizmoHandleFilter.TranslatePlanes) != 0)
		{
			var planes = new[]
			{
				(GizmoPlane.XY, GizmoHandleId.TranslateXY),
				(GizmoPlane.YZ, GizmoHandleId.TranslateYZ),
				(GizmoPlane.XZ, GizmoHandleId.TranslateXZ)
			};
			foreach (var (plane, id) in planes)
			{
				bool hit = TryHitPlane(ray, frame, plane, handleScale, config.PlaneOffsetMin, config.PlaneOffsetMax, out float d, out var pt, config.ParallelDotThreshold);
				TestHit(id, hit, d, pt);
			}
		}

		// 3. Translation Axes
		if ((filter & GizmoHandleFilter.TranslateAxes) != 0)
		{
			var axes = new[]
			{
				(GizmoAxis.X, GizmoHandleId.TranslateX),
				(GizmoAxis.Y, GizmoHandleId.TranslateY),
				(GizmoAxis.Z, GizmoHandleId.TranslateZ)
			};
			foreach (var (axis, id) in axes)
			{
				bool hit = TryHitAxis(ray, frame, axis, handleScale, config.AxisLength, config.AxisHitRadius, out float d, out var pt, config.ParallelDotThreshold);
				TestHit(id, hit, d, pt);
			}
		}

		// 4. Scale Axes
		if ((filter & GizmoHandleFilter.ScaleAxes) != 0)
		{
			var axes = new[]
			{
				(GizmoAxis.X, GizmoHandleId.ScaleX),
				(GizmoAxis.Y, GizmoHandleId.ScaleY),
				(GizmoAxis.Z, GizmoHandleId.ScaleZ)
			};
			foreach (var (axis, id) in axes)
			{
				bool hit = TryHitAxis(ray, frame, axis, handleScale, config.AxisLength, config.AxisHitRadius, out float d, out var pt, config.ParallelDotThreshold);
				TestHit(id, hit, d, pt);
			}
		}

		// 5. Rotation Rings
		if ((filter & GizmoHandleFilter.RotateRings) != 0)
		{
			var rings = new[]
			{
				(GizmoAxis.X, GizmoHandleId.RotateX),
				(GizmoAxis.Y, GizmoHandleId.RotateY),
				(GizmoAxis.Z, GizmoHandleId.RotateZ)
			};
			foreach (var (axis, id) in rings)
			{
				bool hit = TryHitRing(ray, frame, axis, handleScale, config.RingRadius, config.RingHitThickness, out float d, out var pt, config.ParallelDotThreshold);
				TestHit(id, hit, d, pt);
			}
		}

		if (bestId.IsValid)
			return new GizmoHitResult(true, bestId, bestDist, bestPoint);

		return GizmoHitResult.Miss;
	}

	#endregion

	#region Handle Sizing

	/// <summary>
	/// Derives world handle scale from camera view and projection matrices to maintain roughly constant screen size.
	/// Works seamlessly for both perspective and orthographic camera projections.
	/// </summary>
	public static float CalculateHandleScale(
		Vector3 gizmoOrigin,
		Matrix4x4 view,
		Matrix4x4 projection,
		float targetScreenFraction = 0.15f)
	{
		targetScreenFraction = Math.Clamp(targetScreenFraction, 0.01f, 1.0f);

		var viewPos = Vector3.Transform(gizmoOrigin, view);
		bool isOrtho = MathF.Abs(projection.M34) < 1e-6f && MathF.Abs(projection.M44 - 1f) < 1e-6f;

		float projFactor = MathF.Abs(projection.M22);
		if (projFactor < 1e-6f)
			projFactor = MathF.Abs(projection.M11);
		if (projFactor < 1e-6f)
			projFactor = 1f;

		float scale;
		if (isOrtho)
		{
			// Orthographic projection: projected size is independent of distance/depth
			scale = targetScreenFraction * 2.0f / projFactor;
		}
		else
		{
			// Perspective projection: scale increases proportionally with depth
			float depth = MathF.Abs(viewPos.Z);
			scale = targetScreenFraction * depth * 2.0f / projFactor;
		}

		return MathF.Max(1e-4f, scale);
	}

	/// <summary>
	/// Derives world handle scale for perspective camera given distance and field of view.
	/// </summary>
	public static float CalculatePerspectiveHandleScale(
		float distanceToCamera,
		float fovRadians,
		float viewportHeight,
		float targetPixelSize = 80f)
	{
		distanceToCamera = MathF.Max(0.01f, distanceToCamera);
		fovRadians = Math.Clamp(fovRadians, 0.01f, MathF.PI - 0.01f);
		viewportHeight = MathF.Max(1f, viewportHeight);
		targetPixelSize = MathF.Max(1f, targetPixelSize);

		float worldHeightAtDistance = 2f * distanceToCamera * MathF.Tan(fovRadians * 0.5f);
		float scale = (worldHeightAtDistance / viewportHeight) * targetPixelSize;
		return MathF.Max(1e-4f, scale);
	}

	/// <summary>
	/// Derives world handle scale for orthographic camera given orthographic view height.
	/// </summary>
	public static float CalculateOrthographicHandleScale(
		float orthoHeight,
		float viewportHeight,
		float targetPixelSize = 80f)
	{
		orthoHeight = MathF.Max(0.01f, orthoHeight);
		viewportHeight = MathF.Max(1f, viewportHeight);
		targetPixelSize = MathF.Max(1f, targetPixelSize);

		float scale = (orthoHeight / viewportHeight) * targetPixelSize;
		return MathF.Max(1e-4f, scale);
	}

	/// <summary>
	/// Derives world handle scale given explicit camera parameters.
	/// </summary>
	public static float CalculateHandleScale(
		Vector3 gizmoOrigin,
		Vector3 cameraPosition,
		bool isOrthographic,
		float orthoHeightOrFovRadians,
		float viewportHeight,
		float targetPixelSize = 80f)
	{
		if (isOrthographic)
			return CalculateOrthographicHandleScale(orthoHeightOrFovRadians, viewportHeight, targetPixelSize);

		float distance = (gizmoOrigin - cameraPosition).Length();
		return CalculatePerspectiveHandleScale(distance, orthoHeightOrFovRadians, viewportHeight, targetPixelSize);
	}

	#endregion

	#region Snapping

	/// <summary>
	/// Snaps a 3D translation vector in world space, relative to the drag's start position.
	/// Leaves motion continuous when stepSize &lt;= 0 or snapping is disabled.
	/// </summary>
	public static Vector3 SnapTranslation(Vector3 current, Vector3 start, float stepSize)
	{
		if (!float.IsFinite(stepSize))
			throw new ArgumentException("Step size must be finite.", nameof(stepSize));
		if (stepSize <= 0f)
			return current; // Continuous when stepSize <= 0

		var delta = current - start;
		var snappedDelta = new Vector3(
			MathF.Round(delta.X / stepSize) * stepSize,
			MathF.Round(delta.Y / stepSize) * stepSize,
			MathF.Round(delta.Z / stepSize) * stepSize
		);
		return start + snappedDelta;
	}

	/// <summary>
	/// Snaps a 3D translation vector with an explicit enabled flag.
	/// When snapping is enabled, rejects nonpositive or nonfinite step sizes.
	/// </summary>
	public static Vector3 SnapTranslation(Vector3 current, Vector3 start, float stepSize, bool enabled)
	{
		if (!enabled)
			return current;

		if (stepSize <= 0f || !float.IsFinite(stepSize))
			throw new ArgumentOutOfRangeException(nameof(stepSize), "Step size must be positive and finite when snapping is enabled.");

		return SnapTranslation(current, start, stepSize);
	}

	/// <summary>
	/// Snaps a 3D translation vector along the axes of a specified reference frame, relative to start.
	/// Leaves motion continuous when stepSize &lt;= 0.
	/// </summary>
	public static Vector3 SnapTranslation(Vector3 current, Vector3 start, float stepSize, GizmoFrame frame)
	{
		if (!float.IsFinite(stepSize))
			throw new ArgumentException("Step size must be finite.", nameof(stepSize));
		if (stepSize <= 0f)
			return current;

		var delta = current - start;
		float dx = Vector3.Dot(delta, frame.AxisX);
		float dy = Vector3.Dot(delta, frame.AxisY);
		float dz = Vector3.Dot(delta, frame.AxisZ);

		float snappedDx = MathF.Round(dx / stepSize) * stepSize;
		float snappedDy = MathF.Round(dy / stepSize) * stepSize;
		float snappedDz = MathF.Round(dz / stepSize) * stepSize;

		return start + frame.AxisX * snappedDx + frame.AxisY * snappedDy + frame.AxisZ * snappedDz;
	}

	/// <summary>
	/// Snaps a 3D translation vector along a reference frame with an explicit enabled flag.
	/// Rejects nonpositive or nonfinite step sizes when snapping is enabled.
	/// </summary>
	public static Vector3 SnapTranslation(Vector3 current, Vector3 start, float stepSize, GizmoFrame frame, bool enabled)
	{
		if (!enabled)
			return current;

		if (stepSize <= 0f || !float.IsFinite(stepSize))
			throw new ArgumentOutOfRangeException(nameof(stepSize), "Step size must be positive and finite when snapping is enabled.");

		return SnapTranslation(current, start, stepSize, frame);
	}

	/// <summary>
	/// Snaps a rotation angle given in radians to steps specified in degrees at the API boundary.
	/// Leaves motion continuous when stepDegrees &lt;= 0.
	/// </summary>
	public static float SnapRotation(float angleRadians, float stepDegrees)
	{
		if (!float.IsFinite(stepDegrees))
			throw new ArgumentException("Step degrees must be finite.", nameof(stepDegrees));
		if (stepDegrees <= 0f)
			return angleRadians; // Continuous when step <= 0

		float stepRadians = stepDegrees * (MathF.PI / 180f);
		return MathF.Round(angleRadians / stepRadians) * stepRadians;
	}

	/// <summary>
	/// Snaps a rotation angle with an explicit enabled flag.
	/// Rejects nonpositive or nonfinite step sizes when snapping is enabled.
	/// </summary>
	public static float SnapRotation(float angleRadians, float stepDegrees, bool enabled)
	{
		if (!enabled)
			return angleRadians;

		if (stepDegrees <= 0f || !float.IsFinite(stepDegrees))
			throw new ArgumentOutOfRangeException(nameof(stepDegrees), "Step degrees must be positive and finite when snapping is enabled.");

		return SnapRotation(angleRadians, stepDegrees);
	}

	#endregion

	#region Rotation Angle and Unwrapping

	/// <summary>
	/// Computes a stable signed angle in radians between two vectors projected onto a plane with given normal.
	/// The returned angle is strictly in the range (-PI, PI].
	/// </summary>
	public static float ComputeSignedAngleOnPlane(Vector3 v1, Vector3 v2, Vector3 planeNormal)
	{
		if (!float.IsFinite(v1.X) || !float.IsFinite(v2.X) || !float.IsFinite(planeNormal.X))
			return 0f;

		// Project onto plane
		var p1 = v1 - planeNormal * Vector3.Dot(v1, planeNormal);
		var p2 = v2 - planeNormal * Vector3.Dot(v2, planeNormal);

		float l1 = p1.Length();
		float l2 = p2.Length();
		if (l1 < 1e-6f || l2 < 1e-6f)
			return 0f;

		p1 /= l1;
		p2 /= l2;

		float dot = Math.Clamp(Vector3.Dot(p1, p2), -1f, 1f);
		var cross = Vector3.Cross(p1, p2);
		float sin = Vector3.Dot(cross, planeNormal);

		return MathF.Atan2(sin, dot);
	}

	/// <summary>
	/// Smoothly unwraps an angle across the +/- PI discontinuity relative to a reference angle,
	/// preventing sudden +/- 2*PI jumps during continuous rotational dragging.
	/// </summary>
	public static float UnwrapAngle(float currentAngle, float referenceAngle)
	{
		float diff = currentAngle - referenceAngle;
		diff = (diff + MathF.PI) % (2f * MathF.PI);
		if (diff < 0f)
			diff += 2f * MathF.PI;
		return referenceAngle + (diff - MathF.PI);
	}

	/// <summary>
	/// Stateful tracker for rotational drag sessions that accumulates continuous angle deltas across +/- PI.
	/// </summary>
	public struct RotationDragTracker
	{
		float _accumulatedAngle;
		float _lastRawAngle;
		bool _initialized;

		public float TotalAngle => _accumulatedAngle;

		public void Reset(float initialAngle = 0f)
		{
			_accumulatedAngle = initialAngle;
			_lastRawAngle = initialAngle;
			_initialized = true;
		}

		public float Update(float currentRawAngle)
		{
			if (!_initialized)
			{
				_lastRawAngle = currentRawAngle;
				_accumulatedAngle = currentRawAngle;
				_initialized = true;
				return _accumulatedAngle;
			}

			float delta = currentRawAngle - _lastRawAngle;
			while (delta > MathF.PI) delta -= 2f * MathF.PI;
			while (delta < -MathF.PI) delta += 2f * MathF.PI;

			_accumulatedAngle += delta;
			_lastRawAngle = currentRawAngle;
			return _accumulatedAngle;
		}
	}

	#endregion

	#region Scale Clamping

	/// <summary>
	/// Clamps a scalar scale factor to positive values, preventing accidental negative scale when dragging through the origin.
	/// </summary>
	public static float ClampScale(float scale, float minScale = 1e-4f)
	{
		minScale = MathF.Max(1e-6f, minScale);
		if (!float.IsFinite(scale))
			return minScale;
		return MathF.Max(minScale, scale);
	}

	/// <summary>
	/// Clamps a 3D scale vector to strictly positive values on all axes.
	/// </summary>
	public static Vector3 ClampScale(Vector3 scale, float minScale = 1e-4f)
	{
		return new Vector3(
			ClampScale(scale.X, minScale),
			ClampScale(scale.Y, minScale),
			ClampScale(scale.Z, minScale)
		);
	}

	/// <summary>
	/// Computes a positive scale factor from drag distance along an axis.
	/// Dragging through the origin produces clamped positive scale rather than negative scale.
	/// </summary>
	public static float ComputeScaleFactor(float startDistance, float currentDistance, float minScale = 1e-4f)
	{
		minScale = MathF.Max(1e-6f, minScale);
		if (startDistance <= 1e-6f || !float.IsFinite(startDistance))
			return 1.0f;
		if (!float.IsFinite(currentDistance))
			return minScale;

		float scale = currentDistance / startDistance;
		return ClampScale(scale, minScale);
	}

	#endregion

	#region Interaction Projection Utilities

	/// <summary>
	/// Projects a ray onto an axis, returning world position along the axis.
	/// </summary>
	public static bool TryProjectRayOnAxis(
		Ray ray,
		Vector3 axisOrigin,
		Vector3 axisDir,
		out Vector3 worldPoint,
		out float distanceAlongAxis,
		float parallelThreshold = DefaultParallelThreshold)
	{
		worldPoint = Vector3.Zero;
		distanceAlongAxis = 0f;

		if (!TryClosestPointsRayLine(ray, axisOrigin, axisDir, out _, out float lineS, out _, out var ptLine, parallelThreshold))
			return false;

		distanceAlongAxis = lineS;
		worldPoint = ptLine;
		return true;
	}

	/// <summary>
	/// Projects a ray onto a plane, returning intersection point on the plane.
	/// </summary>
	public static bool TryProjectRayOnPlane(
		Ray ray,
		Vector3 planeOrigin,
		Vector3 planeNormal,
		out Vector3 worldPoint,
		float parallelThreshold = DefaultParallelThreshold)
	{
		worldPoint = Vector3.Zero;
		if (!TryIntersectPlane(ray, planeOrigin, planeNormal, out _, out var hitPoint, parallelThreshold))
			return false;

		worldPoint = hitPoint;
		return true;
	}

	#endregion
}
