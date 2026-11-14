// Hammer brush transform + grid snap + texture lock API (no UI).
// Board task: hammer-brush-transform-grid-snap-api (worker muse-3-desktop-hammer-04).
//
// UPDATE 2026-11-13 (muse-3-desktop-hammer-46, hammer-texture-scale-uv-locks):
// MapBrush HAS since landed (kernel/Effigy/Map/MapBrush.cs): the landed face
// type is MapBrushFace (Vec3 normal, float distance, FaceMaterial material)
// with MapBrush.FromPlanes(IEnumerable&lt;MapBrushFace&gt;), NOT the assumed
// MapFace (MapPlane + int material slot) this header's adapter sketch below
// was written against. The sketch is therefore superseded — the final
// integrator reconciles it to the landed API (a MapBrushFace already carries
// a FaceMaterial, so its FaceUv.Locks ride along; see TextureLock in
// FaceMaterial.cs). The lock additions in this file (ApplyScaleLock /
// ApplyUvLock / ApplyLocks / TransformFaces, same file below) are written
// against the landed FaceMaterial/LockFlags types and are unaffected.
//
// What this file is: the headless transform API a future headless/UI caller uses to
// Move/Rotate/Scale a MapBrush (or a selection of brushes) about an adjustable pivot,
// snap positions to a grid size and rotations to an angle snap, and recompute face UVs
// so the texture stays glued to the face (texture lock). No gizmo rendering, no panel,
// no Desktop UI. Pure math on System.Numerics types; the kernel stays engine-free.
//
// MapBrush status (2026-11-13): MapBrush (hammer-brush-core-plane-based-brushes,
// worker muse-3-desktop-hammer-01) has NOT landed on disk yet — there is no
// kernel/Effigy/Map/MapBrush.cs and the core desc does not name the face type or its
// member names. So this file does NOT reference MapBrush: inventing member names to
// call would be guessing at a sibling's API. Instead the brush-level operations here
// work on the face representation the core spec defines — one System.Numerics.Plane
// per face (Normal + D; the spec's face "distance" d is -D, i.e. points satisfy
// n.x = d) plus one MapUvMapping per face (the spec's UV offset/scale/rotation, on
// world-space texture axes, Valve .VMF style). The MapBrush adapter the final
// integrator adds once MapBrush lands is a thin loop, sketched at the bottom of this
// header: for each face, TransformPlane(face plane, matrix) + (with texture lock)
// ApplyTextureLock(face UV mapping, matrix), then rebuild with MapBrush.FromPlanes.
//
// GizmoMath reconciliation: the ticket says "on GizmoMath types", but GizmoMath lives
// in Marionette.Desktop (the app assembly) and the kernel cannot reference it (RULES
// #8: never introduce desktop types into the engine-free kernel). This file is built
// on System.Numerics — the exact types GizmoMath itself is built on (Vector3,
// Quaternion, Matrix4x4, Plane). Axis/plane-constrained helpers take a Vector3 axis
// or plane normal; a desktop caller passes GizmoFrame.GetAxis(GizmoAxis.X) or
// GizmoFrame.GetPlaneNormal(GizmoPlane.XY). GizmoMath.cs itself is untouched.
//
// Plane convention: System.Numerics.Plane, n.x + D = 0, inside is n.x + D <= 0.
// TransformPlane uses the inverse-transpose, so the half-space side is preserved for
// every invertible matrix, including negative-determinant (mirroring) ones. A singular
// matrix (e.g. a zero scale factor, which TransformFeature also rejects) throws.
//
// Grid: standard Hammer sizes 1024 down to 0.125 (microgrid 0.125/0.25/0.5).
// SnapToGrid/SnapAngle round to the NEAREST multiple, ties away from zero:
// SnapToGrid(10, 16) = 16, SnapAngle(17, 15) = 15. SnapToGrid accepts any positive
// finite size (IsValidGridSize reports membership in the standard set).
//
// Texture lock semantics (ticket, not Valve): "texture lock keeps a face UV constant
// after a translate" — the UV of a material point of the face is invariant across the
// transform, i.e. the texture travels glued to the face. ApplyTextureLock recomputes
// the stored offset/scale/axes so Sample(moved point) == Sample(original point).
// This is the OPPOSITE of Valve's "texture lock" toggle (Valve lock ON = texture
// anchored to the world, which here is simply "carry the mapping unchanged, do not
// call ApplyTextureLock"). The two-arg overload is the locked case; the three-arg
// overload with textureLocked: false returns the mapping unchanged.
//
// Pivot semantics: Translate has no pivot (a pure translation does not need one).
// Rotate/Scale happen about the pivot: Rotate leaves the pivot point fixed, Scale
// leaves it fixed. ComposeMatrix is scale, then rotate, then translate — the same
// order TransformFeature uses — with scale/rotation about the pivot and the
// translation in world space.
//
// Immutability: every method returns new values; inputs (plane arrays, mappings)
// are never mutated. A MapBrush stays an immutable-ish value type: callers rebuild
// it from the transformed faces via MapBrush.FromPlanes (see adapter sketch below).
//
// MapBrush adapter sketch (final integrator, once MapBrush lands). Field names
// below follow the ASSUMED API the Map-lane siblings already build against
// (MapBrushPrimitives.cs class doc, MapClip.cs): namespace Effigy.Map,
// MapBrush.FromPlanes(IReadOnlyList<MapFace>), MapBrush.Faces,
// MapFace(MapPlane plane, int material, Vec2 uvOffset, Vec2 uvScale,
// float uvRotationDegrees, float lightmapScale, int smoothingGroup) exposing
// Plane/Material/UvOffset/UvScale/UvRotationDegrees/LightmapScale/SmoothingGroup,
// MapPlane(Vec3 normal, float distance) with n.x <= distance. If the landed API
// differs, the mismatch is confined to this sketch. A face carries no texture
// axes, so the adapter derives them from the face normal (DefaultForNormal):
//   static MapBrush TranslateBrush(MapBrush brush, Vector3 delta, bool textureLocked)
//   {
//       var m = MapTransform.TranslationMatrix(delta);
//       var faces = brush.Faces.Select(f =>
//       {
//           var plane = new MapPlane(
//               VecFrom(MapTransform.TransformPlane(PlaneFrom(f.Plane), m).Normal),
//               -MapTransform.TransformPlane(PlaneFrom(f.Plane), m).D);
//           // PlaneFrom: MapPlane -> System.Numerics.Plane (Normal, D = -Distance).
//           if (!textureLocked)
//               return new MapFace(plane, f.Material, f.UvOffset, f.UvScale,
//                   f.UvRotationDegrees, f.LightmapScale, f.SmoothingGroup);
//           var uv = new MapUvMapping(
//               DefaultAxes(f.Plane.Normal).U, DefaultAxes(f.Plane.Normal).V,
//               Vec2From(f.UvOffset), Vec2From(f.UvScale), f.UvRotationDegrees);
//           var locked = MapTransform.ApplyTextureLock(uv, m);
//           return new MapFace(plane, f.Material,
//               new Vec2(locked.Offset.X, locked.Offset.Y),
//               new Vec2(locked.Scale.X, locked.Scale.Y),
//               locked.RotationDegrees, f.LightmapScale, f.SmoothingGroup);
//       }).ToArray();
//       return MapBrush.FromPlanes(faces);
//   }
// Rotate/Scale are the same loop with RotationMatrix/ScaleMatrix. A selection is
// the same loop per brush (see TransformSelection for the plane-list form).
// NOTE: the sibling assumption also says MapFace.Material is an int SLOT (0..63),
// not a material name; the box-64.json fixture records material names only as a
// human-readable label, not as the code-facing id.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Effigy;

/// <summary>
/// World-space UV mapping for one brush face (Valve .VMF style): the texture axes
/// live in world space, so the stored offset/scale/rotation describe where the
/// texture sits in the world, not on the face. Sample(p) = Rot(rotation) applied to
/// (dot(p, UAxis) * Scale.X + Offset.X, dot(p, VAxis) * Scale.Y + Offset.Y).
/// Axes are unit length; ApplyTextureLock keeps them unit by folding any axis
/// length change into Scale. Constructed values are validated: axes must be
/// non-zero and finite, scale components finite and non-zero.
/// </summary>
public readonly struct MapUvMapping
{
	public Vector3 UAxis { get; }
	public Vector3 VAxis { get; }
	public Vector2 Offset { get; }
	public Vector2 Scale { get; }
	public float RotationDegrees { get; }

	public MapUvMapping(Vector3 uAxis, Vector3 vAxis, Vector2 offset, Vector2 scale, float rotationDegrees)
	{
		RequireAxis(uAxis, nameof(uAxis));
		RequireAxis(vAxis, nameof(vAxis));
		if (!IsFinite(offset)) throw new ArgumentException("UV offset must be finite.", nameof(offset));
		if (!IsFinite(scale) || scale.X == 0f || scale.Y == 0f)
			throw new ArgumentException("UV scale must be finite and non-zero on both axes.", nameof(scale));
		if (!float.IsFinite(rotationDegrees))
			throw new ArgumentException("UV rotation must be finite.", nameof(rotationDegrees));
		UAxis = uAxis;
		VAxis = vAxis;
		Offset = offset;
		Scale = scale;
		RotationDegrees = rotationDegrees;
	}

	static void RequireAxis(Vector3 axis, string name)
	{
		if (!IsFinite(axis) || axis.LengthSquared() < 1e-12f)
			throw new ArgumentException("Texture axis must be finite and non-zero.", name);
	}

	static bool IsFinite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);
	static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

	/// <summary>
	/// Default mapping for a face with the given outward normal: U/V are an
	/// orthonormal in-plane basis (for +Z: U=+X, V=+Y; for +X: U=-Z, V=+Y),
	/// offset zero, scale one, no rotation.
	/// </summary>
	public static MapUvMapping DefaultForNormal(Vector3 normal)
	{
		if (!IsFinite(normal) || normal.LengthSquared() < 1e-12f)
			throw new ArgumentException("Face normal must be finite and non-zero.", nameof(normal));
		var n = Vector3.Normalize(normal);
		var reference = MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
		var u = Vector3.Normalize(Vector3.Cross(reference, n));
		var v = Vector3.Cross(n, u);
		return new MapUvMapping(u, v, Vector2.Zero, Vector2.One, 0f);
	}

	/// <summary>UV of a world point under this mapping.</summary>
	public Vector2 Sample(Vector3 worldPoint)
	{
		float rawU = Vector3.Dot(worldPoint, UAxis) * Scale.X + Offset.X;
		float rawV = Vector3.Dot(worldPoint, VAxis) * Scale.Y + Offset.Y;
		float radians = RotationDegrees * MathF.PI / 180f;
		float cos = MathF.Cos(radians), sin = MathF.Sin(radians);
		return new Vector2(cos * rawU - sin * rawV, sin * rawU + cos * rawV);
	}
}

/// <summary>
/// Headless brush transform + grid snap + texture lock API (no UI).
/// Brush-level operations work on face-plane lists (see the file header for why
/// MapBrush itself is not referenced yet); point/plane/matrix helpers work on
/// single values. All methods are pure: inputs are never mutated.
/// </summary>
public static class MapTransform
{
	/// <summary>Standard Hammer grid sizes, largest first: 1024 down to 0.125.</summary>
	public static readonly IReadOnlyList<double> GridSizes = new double[]
	{
		1024, 512, 256, 128, 64, 32, 16, 8, 4, 2, 1, 0.5, 0.25, 0.125
	};

	public const double MaxGridSize = 1024;
	public const double MinGridSize = 0.125;

	/// <summary>True when size is one of the standard grid sizes (within 1e-9).</summary>
	public static bool IsValidGridSize(double size)
	{
		if (!double.IsFinite(size)) return false;
		foreach (var g in GridSizes)
			if (Math.Abs(size - g) < 1e-9) return true;
		return false;
	}

	/// <summary>
	/// Snap a value to the nearest multiple of size (ties away from zero).
	/// Size must be positive and finite; any positive size snaps, not just the
	/// standard grid sizes. SnapToGrid(10, 16) = 16.
	/// </summary>
	public static double SnapToGrid(double value, double size)
	{
		if (!double.IsFinite(value))
			throw new ArgumentOutOfRangeException(nameof(value), "Value must be finite.");
		if (!double.IsFinite(size) || size <= 0.0)
			throw new ArgumentOutOfRangeException(nameof(size), "Grid size must be a positive finite number.");
		return Math.Round(value / size, MidpointRounding.AwayFromZero) * size;
	}

	/// <summary>Snap a value to the nearest multiple of size (ties away from zero).</summary>
	public static float SnapToGrid(float value, double size) => (float)SnapToGrid((double)value, size);

	/// <summary>Snap every component of a point to the nearest multiple of size.</summary>
	public static Vector3 SnapToGrid(Vector3 point, double size) => new(
		SnapToGrid(point.X, size),
		SnapToGrid(point.Y, size),
		SnapToGrid(point.Z, size));

	/// <summary>
	/// Snap an angle in degrees to the nearest multiple of snapDegrees (ties away
	/// from zero). No wrapping is applied: callers normalize first if they want
	/// (-180, 180]. SnapAngle(17, 15) = 15. Snap must be positive and finite.
	/// </summary>
	public static double SnapAngle(double degrees, double snapDegrees)
	{
		if (!double.IsFinite(degrees))
			throw new ArgumentOutOfRangeException(nameof(degrees), "Angle must be finite.");
		if (!double.IsFinite(snapDegrees) || snapDegrees <= 0.0)
			throw new ArgumentOutOfRangeException(nameof(snapDegrees), "Angle snap must be a positive finite number.");
		return Math.Round(degrees / snapDegrees, MidpointRounding.AwayFromZero) * snapDegrees;
	}

	/// <summary>Snap an angle in degrees to the nearest multiple of snapDegrees.</summary>
	public static float SnapAngle(float degrees, float snapDegrees) => (float)SnapAngle((double)degrees, (double)snapDegrees);

	/// <summary>Translation matrix for a pure move. No pivot: translation does not need one.</summary>
	public static Matrix4x4 TranslationMatrix(Vector3 delta) => Matrix4x4.CreateTranslation(delta);

	/// <summary>Rotation matrix about a pivot: the pivot point itself does not move.</summary>
	public static Matrix4x4 RotationMatrix(Quaternion rotation, Vector3 pivot) =>
		Matrix4x4.CreateTranslation(-pivot)
		* Matrix4x4.CreateFromQuaternion(rotation)
		* Matrix4x4.CreateTranslation(pivot);

	/// <summary>Rotation matrix about an axis through a pivot. Angle is in degrees.</summary>
	public static Matrix4x4 RotationMatrix(Vector3 axis, float angleDegrees, Vector3 pivot)
	{
		if (!float.IsFinite(axis.X) || !float.IsFinite(axis.Y) || !float.IsFinite(axis.Z) || axis.LengthSquared() < 1e-12f)
			throw new ArgumentException("Rotation axis must be finite and non-zero.", nameof(axis));
		if (!float.IsFinite(angleDegrees))
			throw new ArgumentException("Rotation angle must be finite.", nameof(angleDegrees));
		return RotationMatrix(
			Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angleDegrees * MathF.PI / 180f),
			pivot);
	}

	/// <summary>Scale matrix about a pivot: the pivot point itself does not move. Zero on any axis throws.</summary>
	public static Matrix4x4 ScaleMatrix(Vector3 factors, Vector3 pivot)
	{
		RequireNonZeroScale(factors);
		return Matrix4x4.CreateTranslation(-pivot)
			* Matrix4x4.CreateScale(factors)
			* Matrix4x4.CreateTranslation(pivot);
	}

	/// <summary>Uniform scale matrix about a pivot. A zero factor throws.</summary>
	public static Matrix4x4 ScaleMatrix(float uniformFactor, Vector3 pivot)
	{
		if (!float.IsFinite(uniformFactor) || uniformFactor == 0f)
			throw new ArgumentException("Scale factor must be finite and non-zero.", nameof(uniformFactor));
		return ScaleMatrix(new Vector3(uniformFactor), pivot);
	}

	static void RequireNonZeroScale(Vector3 factors)
	{
		if (!float.IsFinite(factors.X) || !float.IsFinite(factors.Y) || !float.IsFinite(factors.Z))
			throw new ArgumentException("Scale factors must be finite.", nameof(factors));
		if (factors.X == 0f || factors.Y == 0f || factors.Z == 0f)
			throw new ArgumentException("Scale cannot be zero on any axis: a zero axis flattens the solid to nothing.", nameof(factors));
	}

	/// <summary>
	/// Combined transform: scale, then rotate, then translate — the same order
	/// TransformFeature uses. Scale/rotation happen about the pivot; the
	/// translation is in world space.
	/// </summary>
	public static Matrix4x4 ComposeMatrix(Vector3 translate, Quaternion rotation, Vector3 scale, Vector3 pivot)
	{
		RequireNonZeroScale(scale);
		return Matrix4x4.CreateTranslation(-pivot)
			* Matrix4x4.CreateScale(scale)
			* Matrix4x4.CreateFromQuaternion(rotation)
			* Matrix4x4.CreateTranslation(pivot)
			* Matrix4x4.CreateTranslation(translate);
	}

	/// <summary>Transform a point (translation applies).</summary>
	public static Vector3 TransformPoint(Vector3 point, Matrix4x4 matrix) => Vector3.Transform(point, matrix);

	/// <summary>Transform a direction/normal (translation does not apply).</summary>
	public static Vector3 TransformDirection(Vector3 direction, Matrix4x4 matrix) => Vector3.TransformNormal(direction, matrix);

	/// <summary>
	/// Transform a plane (n.x + D = 0) by a matrix, via the inverse-transpose, so
	/// the half-space side is preserved for every invertible matrix, including
	/// mirroring ones. A singular matrix throws ArgumentException.
	/// </summary>
	public static Plane TransformPlane(Plane plane, Matrix4x4 matrix)
	{
		if (!Matrix4x4.Invert(matrix, out Matrix4x4 inverse))
			throw new ArgumentException("Transform matrix is singular and cannot be applied to a plane.", nameof(matrix));
		// m = A^-T n in column convention; in System.Numerics row-vector terms
		// that is TransformNormal(n, Transpose(inverse)).
		var normal = Vector3.TransformNormal(plane.Normal, Matrix4x4.Transpose(inverse));
		float lengthSquared = normal.LengthSquared();
		if (lengthSquared < 1e-12f)
			throw new ArgumentException("Transform collapses the plane normal to zero.", nameof(matrix));
		float length = MathF.Sqrt(lengthSquared);
		var newNormal = normal / length;
		float newD = (plane.D - Vector3.Dot(normal, matrix.Translation)) / length;
		return new Plane(newNormal, newD);
	}

	/// <summary>Transform every face plane of one brush. Returns a new array; the input is untouched.</summary>
	public static Plane[] Transform(IReadOnlyList<Plane> planes, Matrix4x4 matrix)
	{
		if (planes == null) throw new ArgumentNullException(nameof(planes));
		var result = new Plane[planes.Count];
		for (int i = 0; i < planes.Count; i++) result[i] = TransformPlane(planes[i], matrix);
		return result;
	}

	/// <summary>Move every face plane of one brush by delta.</summary>
	public static Plane[] Translate(IReadOnlyList<Plane> planes, Vector3 delta) =>
		Transform(planes, TranslationMatrix(delta));

	/// <summary>Rotate every face plane of one brush about a pivot.</summary>
	public static Plane[] Rotate(IReadOnlyList<Plane> planes, Quaternion rotation, Vector3 pivot) =>
		Transform(planes, RotationMatrix(rotation, pivot));

	/// <summary>Rotate every face plane of one brush about an axis through a pivot. Angle is in degrees.</summary>
	public static Plane[] Rotate(IReadOnlyList<Plane> planes, Vector3 axis, float angleDegrees, Vector3 pivot) =>
		Transform(planes, RotationMatrix(axis, angleDegrees, pivot));

	/// <summary>Scale every face plane of one brush about a pivot. Zero on any axis throws.</summary>
	public static Plane[] Scale(IReadOnlyList<Plane> planes, Vector3 factors, Vector3 pivot) =>
		Transform(planes, ScaleMatrix(factors, pivot));

	/// <summary>Uniformly scale every face plane of one brush about a pivot. A zero factor throws.</summary>
	public static Plane[] Scale(IReadOnlyList<Plane> planes, float uniformFactor, Vector3 pivot) =>
		Transform(planes, ScaleMatrix(uniformFactor, pivot));

	/// <summary>
	/// Transform a selection: one plane list per brush. Returns one new array per
	/// brush, in the same order; inputs are untouched.
	/// </summary>
	public static Plane[][] TransformSelection(IReadOnlyList<IReadOnlyList<Plane>> brushes, Matrix4x4 matrix)
	{
		if (brushes == null) throw new ArgumentNullException(nameof(brushes));
		var result = new Plane[brushes.Count][];
		for (int i = 0; i < brushes.Count; i++)
		{
			if (brushes[i] == null)
				throw new ArgumentNullException(nameof(brushes), $"Brush {i} in the selection is null.");
			result[i] = Transform(brushes[i], matrix);
		}
		return result;
	}

	/// <summary>
	/// Keep only the component of a move delta along an axis (unit or not; zero
	/// throws). A desktop caller passes GizmoFrame.GetAxis(GizmoAxis.X/Y/Z).
	/// </summary>
	public static Vector3 ConstrainToAxis(Vector3 delta, Vector3 axis)
	{
		if (!float.IsFinite(axis.X) || !float.IsFinite(axis.Y) || !float.IsFinite(axis.Z) || axis.LengthSquared() < 1e-12f)
			throw new ArgumentException("Constraint axis must be finite and non-zero.", nameof(axis));
		var unit = Vector3.Normalize(axis);
		return unit * Vector3.Dot(delta, unit);
	}

	/// <summary>
	/// Keep only the components of a move delta inside a plane (drop the part
	/// along the plane normal; zero normal throws). A desktop caller passes
	/// GizmoFrame.GetPlaneNormal(GizmoPlane.XY/YZ/XZ).
	/// </summary>
	public static Vector3 ConstrainToPlane(Vector3 delta, Vector3 planeNormal)
	{
		if (!float.IsFinite(planeNormal.X) || !float.IsFinite(planeNormal.Y) || !float.IsFinite(planeNormal.Z) || planeNormal.LengthSquared() < 1e-12f)
			throw new ArgumentException("Plane normal must be finite and non-zero.", nameof(planeNormal));
		var unit = Vector3.Normalize(planeNormal);
		return delta - unit * Vector3.Dot(delta, unit);
	}

	/// <summary>
	/// Texture lock (ticket semantics): recompute a face's UV mapping after a brush
	/// transform so the texture stays glued to the face — the UV of every material
	/// point is unchanged: ApplyTextureLock(m, T).Sample(T * p) == m.Sample(p).
	/// With textureLocked: false the mapping is returned unchanged (world-anchored
	/// texture: Valve's "texture lock ON" behaviour). A singular transform throws.
	/// </summary>
	public static MapUvMapping ApplyTextureLock(MapUvMapping mapping, Matrix4x4 transform) =>
		ApplyTextureLock(mapping, transform, textureLocked: true);

	/// <summary>
	/// Scale lock (ticket semantics — see <see cref="LockFlags"/>): the texture's
	/// world size survives a transform. In this world-space model the stored
	/// scale already IS world-relative, so a resize keeps the stored scale and
	/// rotates the texture axes with the face; the offset is solved so the UV
	/// at <paramref name="anchor"/> is preserved (pass the pivot the matrix
	/// was built about — e.g. the pivot of <see cref="ScaleMatrix"/> /
	/// <see cref="RotationMatrix"/>; <see cref="Vector3.Zero"/> for a
	/// transform about the origin). A pure translation is a no-op: a move
	/// changes no scale, so the mapping is returned unchanged. The UV gradient
	/// (UV per world inch) is unchanged by this operation — the texture keeps
	/// its size while the brush grows or shrinks over it — while the UV at a
	/// material point away from the anchor is NOT preserved; that stronger
	/// promise is <see cref="ApplyUvLock(MapUvMapping, Matrix4x4)"/>. A
	/// singular transform throws <see cref="ArgumentException"/>; a transform
	/// that collapses a texture axis throws
	/// <see cref="InvalidOperationException"/>.
	/// </summary>
	public static MapUvMapping ApplyScaleLock( MapUvMapping mapping, Matrix4x4 transform ) =>
		ApplyScaleLock( mapping, transform, Vector3.Zero );

	public static MapUvMapping ApplyScaleLock( MapUvMapping mapping, Matrix4x4 transform, Vector3 anchor )
	{
		if ( !Matrix4x4.Invert( transform, out _ ) )
			throw new ArgumentException( "Transform matrix is singular and cannot be applied to a UV mapping.", nameof( transform ) );

		// The linear part, read off the matrix: a pure translation (identity
		// linear part) changes no scale, so scale lock is a no-op for it.
		bool linearIsIdentity =
			MathF.Abs( transform.M11 - 1f ) < 1e-6f && MathF.Abs( transform.M12 ) < 1e-6f && MathF.Abs( transform.M13 ) < 1e-6f &&
			MathF.Abs( transform.M21 ) < 1e-6f && MathF.Abs( transform.M22 - 1f ) < 1e-6f && MathF.Abs( transform.M23 ) < 1e-6f &&
			MathF.Abs( transform.M31 ) < 1e-6f && MathF.Abs( transform.M32 ) < 1e-6f && MathF.Abs( transform.M33 - 1f ) < 1e-6f;
		if ( linearIsIdentity ) return mapping;

		var carriedU = Vector3.TransformNormal( mapping.UAxis, transform );
		var carriedV = Vector3.TransformNormal( mapping.VAxis, transform );
		if ( carriedU.Length() < 1e-6f || carriedV.Length() < 1e-6f )
			throw new InvalidOperationException( "Scale lock collapses a texture axis to zero." );
		var newU = Vector3.Normalize( carriedU );
		var newV = Vector3.Normalize( carriedV );

		// Offset: Sample'(T * anchor) == Sample(anchor). Un-rotate the anchor
		// UV, then subtract the new axes' contribution at the moved anchor.
		var anchorUv = mapping.Sample( anchor );
		float radians = mapping.RotationDegrees * MathF.PI / 180f;
		float cos = MathF.Cos( radians ), sin = MathF.Sin( radians );
		var unrotated = new Vector2(
			cos * anchorUv.X + sin * anchorUv.Y,
			-sin * anchorUv.X + cos * anchorUv.Y );
		var movedAnchor = Vector3.Transform( anchor, transform );
		var newOffset = new Vector2(
			unrotated.X - Vector3.Dot( movedAnchor, newU ) * mapping.Scale.X,
			unrotated.Y - Vector3.Dot( movedAnchor, newV ) * mapping.Scale.Y );
		return new MapUvMapping( newU, newV, newOffset, mapping.Scale, mapping.RotationDegrees );
	}

	/// <summary>
	/// UV lock (ticket semantics — see <see cref="LockFlags"/>): the UV of
	/// every material point is invariant across the transform, including
	/// shear/skew: <c>ApplyUvLock(m, T).Sample(T * p) == m.Sample(p)</c>. This
	/// is worker 04's texture lock under the flag's name — same math, same
	/// invariant (<see cref="ApplyTextureLock(MapUvMapping, Matrix4x4)"/>).
	/// A singular transform throws <see cref="ArgumentException"/>.
	/// </summary>
	public static MapUvMapping ApplyUvLock( MapUvMapping mapping, Matrix4x4 transform ) =>
		ApplyTextureLock( mapping, transform, textureLocked: true );

	/// <summary>
	/// The one-call dispatcher the transform path uses: read
	/// <paramref name="locks"/> and apply the promise. <see cref="LockFlags.UvLock"/>
	/// wins when both flags are set; <see cref="LockFlags.ScaleLock"/> alone
	/// keeps the texture's world size (anchored at <paramref name="anchor"/>);
	/// no flag returns the mapping unchanged (world-anchored).
	/// </summary>
	public static MapUvMapping ApplyLocks( MapUvMapping mapping, LockFlags locks, Matrix4x4 transform, Vector3 anchor )
	{
		if ( (locks & LockFlags.UvLock) != 0 ) return ApplyUvLock( mapping, transform );
		if ( (locks & LockFlags.ScaleLock) != 0 ) return ApplyScaleLock( mapping, transform, anchor );
		return mapping;
	}

	/// <summary>Same as <see cref="ApplyLocks(MapUvMapping, LockFlags, Matrix4x4, Vector3)"/> with the anchor at the origin.</summary>
	public static MapUvMapping ApplyLocks( MapUvMapping mapping, LockFlags locks, Matrix4x4 transform ) =>
		ApplyLocks( mapping, locks, transform, Vector3.Zero );

	/// <summary>
	/// The wired transform path for this ticket: transform every face plane of
	/// one brush AND its per-face UV mappings, applying each face's locks.
	/// <paramref name="planes"/>, <paramref name="mappings"/> and
	/// <paramref name="locks"/> are parallel lists (one entry per face, same
	/// order); <paramref name="anchor"/> is the pivot the matrix was built
	/// about (Vector3.Zero for a transform about the origin, or for a pure
	/// translation, where the anchor is unused). Returns the transformed
	/// planes and the lock-applied mappings, in face order; inputs are
	/// untouched. Null lists throw <see cref="ArgumentNullException"/>;
	/// mismatched counts throw <see cref="ArgumentException"/>. A singular
	/// matrix throws from <see cref="TransformPlane"/>.
	/// </summary>
	public static (Plane[] Planes, MapUvMapping[] Mappings) TransformFaces(
		IReadOnlyList<Plane> planes, IReadOnlyList<MapUvMapping> mappings,
		IReadOnlyList<LockFlags> locks, Matrix4x4 matrix, Vector3 anchor )
	{
		if ( planes is null ) throw new ArgumentNullException( nameof( planes ) );
		if ( mappings is null ) throw new ArgumentNullException( nameof( mappings ) );
		if ( locks is null ) throw new ArgumentNullException( nameof( locks ) );
		if ( planes.Count != mappings.Count || planes.Count != locks.Count )
			throw new ArgumentException( $"TransformFaces needs one plane, mapping and lock set per face; got {planes.Count} planes, {mappings.Count} mappings, {locks.Count} lock sets." );
		var newPlanes = Transform( planes, matrix );
		var newMappings = new MapUvMapping[mappings.Count];
		for ( int i = 0; i < mappings.Count; i++ )
			newMappings[i] = ApplyLocks( mappings[i], locks[i], matrix, anchor );
		return (newPlanes, newMappings);
	}

	/// <summary>Same as <see cref="TransformFaces(IReadOnlyList{Plane}, IReadOnlyList{MapUvMapping}, IReadOnlyList{LockFlags}, Matrix4x4, Vector3)"/> with the anchor at the origin.</summary>
	public static (Plane[] Planes, MapUvMapping[] Mappings) TransformFaces(
		IReadOnlyList<Plane> planes, IReadOnlyList<MapUvMapping> mappings,
		IReadOnlyList<LockFlags> locks, Matrix4x4 matrix ) =>
		TransformFaces( planes, mappings, locks, matrix, Vector3.Zero );

	public static MapUvMapping ApplyTextureLock(MapUvMapping mapping, Matrix4x4 transform, bool textureLocked)
	{
		if (!textureLocked) return mapping;
		if (!Matrix4x4.Invert(transform, out Matrix4x4 inverse))
			throw new ArgumentException("Transform matrix is singular and cannot be applied to a UV mapping.", nameof(transform));

		float radians = mapping.RotationDegrees * MathF.PI / 180f;
		float cos = MathF.Cos(radians), sin = MathF.Sin(radians);

		// Effective 2x3 map M (rows) and offset b with Sample(p) = M p + b.
		var rowU = mapping.UAxis * (cos * mapping.Scale.X) - mapping.VAxis * (sin * mapping.Scale.Y);
		var rowV = mapping.UAxis * (sin * mapping.Scale.X) + mapping.VAxis * (cos * mapping.Scale.Y);
		var b = new Vector2(
			cos * mapping.Offset.X - sin * mapping.Offset.Y,
			sin * mapping.Offset.X + cos * mapping.Offset.Y);

		// Locked: M' = M A^-1, b' = b - M' t. Rows map through
		// Transpose(inverse), the same inverse-transpose as TransformPlane.
		var inverseTranspose = Matrix4x4.Transpose(inverse);
		var newRowU = Vector3.TransformNormal(rowU, inverseTranspose);
		var newRowV = Vector3.TransformNormal(rowV, inverseTranspose);
		var translation = transform.Translation;
		var newB = new Vector2(
			b.X - Vector3.Dot(newRowU, translation),
			b.Y - Vector3.Dot(newRowV, translation));

		// Decompose M' back with the same rotation/scale split, then fold the
		// axis lengths into the scales so the axes stay unit length. Sample is
		// exact either way; unit axes match what a face record carries.
		var newU = (newRowU * cos + newRowV * sin) / mapping.Scale.X;
		var newV = (-newRowU * sin + newRowV * cos) / mapping.Scale.Y;
		float newULength = newU.Length();
		float newVLength = newV.Length();
		if (newULength < 1e-6f || newVLength < 1e-6f)
			throw new InvalidOperationException("Texture lock collapses a texture axis to zero.");
		var newOffset = new Vector2(
			cos * newB.X + sin * newB.Y,
			-sin * newB.X + cos * newB.Y);
		return new MapUvMapping(
			newU / newULength,
			newV / newVLength,
			newOffset,
			new Vector2(mapping.Scale.X * newULength, mapping.Scale.Y * newVLength),
			mapping.RotationDegrees);
	}
}
