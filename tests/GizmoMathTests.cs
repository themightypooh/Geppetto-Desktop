using System;
using System.Numerics;
using Marionette.Desktop;

internal static class GizmoMathTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "gizmo-math", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"Expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"Expected {typeof(T).Name}, got silent success" );
		}

		// 1. Every axis hit and miss
		Check( "every axis hit and miss", () =>
		{
			var frame = GizmoFrame.World;
			float handleScale = 10f;
			var config = new GizmoConfig();

			// Ray aiming directly at X axis midpoint (5, 0, 0) from (5, 10, 0) directed down -Y
			var rayX = new Ray( new Vector3( 5f, 10f, 0f ), new Vector3( 0f, -1f, 0f ) );
			Require( GizmoMath.TryHitAxis( rayX, frame, GizmoAxis.X, handleScale, config.AxisLength, config.AxisHitRadius, out float dist, out var pt ), "X axis hit failed" );
			Require( MathF.Abs( pt.X - 5f ) < 0.01f && MathF.Abs( pt.Y ) < 0.01f && MathF.Abs( pt.Z ) < 0.01f, "X hit point wrong" );

			// Ray aiming directly at Y axis midpoint (0, 5, 0) from (10, 5, 0) directed down -X
			var rayY = new Ray( new Vector3( 10f, 5f, 0f ), new Vector3( -1f, 0f, 0f ) );
			Require( GizmoMath.TryHitAxis( rayY, frame, GizmoAxis.Y, handleScale, config.AxisLength, config.AxisHitRadius, out dist, out pt ), "Y axis hit failed" );
			Require( MathF.Abs( pt.Y - 5f ) < 0.01f, "Y hit point wrong" );

			// Ray aiming directly at Z axis midpoint (0, 0, 5) from (0, 10, 5) directed down -Y
			var rayZ = new Ray( new Vector3( 0f, 10f, 5f ), new Vector3( 0f, -1f, 0f ) );
			Require( GizmoMath.TryHitAxis( rayZ, frame, GizmoAxis.Z, handleScale, config.AxisLength, config.AxisHitRadius, out dist, out pt ), "Z axis hit failed" );
			Require( MathF.Abs( pt.Z - 5f ) < 0.01f, "Z hit point wrong" );

			// Ray far off to the side (misses all axes)
			var rayMiss = new Ray( new Vector3( 5f, 10f, 5f ), new Vector3( 0f, -1f, 0f ) );
			Require( !GizmoMath.TryHitAxis( rayMiss, frame, GizmoAxis.X, handleScale, config.AxisLength, config.AxisHitRadius, out _, out _ ), "X axis false positive" );
			Require( !GizmoMath.TryHitAxis( rayMiss, frame, GizmoAxis.Y, handleScale, config.AxisLength, config.AxisHitRadius, out _, out _ ), "Y axis false positive" );
			Require( !GizmoMath.TryHitAxis( rayMiss, frame, GizmoAxis.Z, handleScale, config.AxisLength, config.AxisHitRadius, out _, out _ ), "Z axis false positive" );
		} );

		// 2. Ray behind camera / pointing away
		Check( "ray behind camera pointing away", () =>
		{
			var frame = GizmoFrame.World;
			float handleScale = 10f;
			var config = new GizmoConfig();

			// Ray starts at (5, -10, 0) and points in -Y (away from the gizmo at origin)
			var rayPointingAway = new Ray( new Vector3( 5f, -10f, 0f ), new Vector3( 0f, -1f, 0f ) );
			Require( !GizmoMath.TryHitAxis( rayPointingAway, frame, GizmoAxis.X, handleScale, config.AxisLength, config.AxisHitRadius, out _, out _ ), "Pointing away hit axis" );
			Require( !GizmoMath.TryIntersectPlane( rayPointingAway, frame.Origin, Vector3.UnitZ, out _, out _ ), "Pointing away hit plane" );
			Require( !GizmoMath.TryHitRing( rayPointingAway, frame, GizmoAxis.Z, handleScale, config.RingRadius, config.RingHitThickness, out _, out _ ), "Pointing away hit ring" );

			// Ray starts at (0, 0, -10) pointing in -Z (gizmo origin is at (0, 0, 0) behind ray)
			var rayBehind = new Ray( new Vector3( 0f, 0f, -10f ), new Vector3( 0f, 0f, -1f ) );
			var hit = GizmoMath.HitTest( rayBehind, frame, handleScale );
			Require( !hit.IsHit, "Ray behind camera should not hit any handle" );
		} );

		// 3. Orthographic and perspective reference projections
		Check( "orthographic and perspective reference projections", () =>
		{
			var origin = new Vector3( 0f, 0f, 0f );

			// Perspective projection
			float fov = 45f * MathF.PI / 180f;
			float viewportHeight = 1080f;
			float targetPixelSize = 100f;

			float scaleDist100 = GizmoMath.CalculatePerspectiveHandleScale( 100f, fov, viewportHeight, targetPixelSize );
			float scaleDist200 = GizmoMath.CalculatePerspectiveHandleScale( 200f, fov, viewportHeight, targetPixelSize );
			Require( scaleDist100 > 0f && scaleDist200 > 0f, "Scales must be positive" );
			// At double distance, world size should be double to keep constant pixel size on screen
			Require( MathF.Abs( (scaleDist200 / scaleDist100) - 2.0f ) < 1e-3f, "Perspective scale must be linear with distance" );

			// Orthographic projection
			float orthoHeight = 500f;
			float scaleOrtho1 = GizmoMath.CalculateOrthographicHandleScale( orthoHeight, viewportHeight, targetPixelSize );
			float scaleOrtho2 = GizmoMath.CalculateOrthographicHandleScale( orthoHeight * 2f, viewportHeight, targetPixelSize );
			Require( MathF.Abs( (scaleOrtho2 / scaleOrtho1) - 2.0f ) < 1e-3f, "Ortho scale must scale with ortho viewport size" );

			// Matrix4x4 based calculations
			var view = Matrix4x4.CreateLookAt( new Vector3( 0, -100, 0 ), origin, Vector3.UnitZ );
			var projPersp = Matrix4x4.CreatePerspectiveFieldOfView( fov, 16f / 9f, 0.1f, 1000f );
			var projOrtho = Matrix4x4.CreateOrthographic( 800f, 600f, 0.1f, 1000f );

			float mPerspScale = GizmoMath.CalculateHandleScale( origin, view, projPersp );
			float mOrthoScale = GizmoMath.CalculateHandleScale( origin, view, projOrtho );
			Require( mPerspScale > 0f && float.IsFinite( mPerspScale ), "Matrix perspective scale must be positive and finite" );
			Require( mOrthoScale > 0f && float.IsFinite( mOrthoScale ), "Matrix ortho scale must be positive and finite" );

			// In orthographic projection, moving camera further away must not change handle size
			var viewFar = Matrix4x4.CreateLookAt( new Vector3( 0, -500, 0 ), origin, Vector3.UnitZ );
			float mOrthoFarScale = GizmoMath.CalculateHandleScale( origin, viewFar, projOrtho );
			Require( MathF.Abs( mOrthoFarScale - mOrthoScale ) < 1e-4f, "Ortho handle scale must not change with camera distance" );
		} );

		// 4. Parallel ray (no division by zero, returns false / null)
		Check( "parallel ray returns false", () =>
		{
			var frame = GizmoFrame.World;

			// Ray parallel to XY plane (pointing in +X direction, offset along Z)
			var rayParallelPlane = new Ray( new Vector3( 0f, 0f, 5f ), new Vector3( 1f, 0f, 0f ) );
			bool hitPlane = GizmoMath.TryIntersectPlane( rayParallelPlane, frame.Origin, Vector3.UnitZ, out _, out _ );
			Require( !hitPlane, "Parallel ray should not intersect plane" );

			// Ray parallel to X axis (offset along Y by 2 units)
			var rayParallelAxis = new Ray( new Vector3( 0f, 2f, 0f ), new Vector3( 1f, 0f, 0f ) );
			bool hitAxis = GizmoMath.TryHitAxis( rayParallelAxis, frame, GizmoAxis.X, 10f, 1f, 0.12f, out _, out _ );
			Require( !hitAxis, "Parallel ray should not hit axis" );

			// Ray nearly parallel within threshold
			var rayNearParallel = new Ray( new Vector3( 0f, 0f, 5f ), Vector3.Normalize( new Vector3( 1f, 0f, 0.0001f ) ) );
			bool hitNearPlane = GizmoMath.TryIntersectPlane( rayNearParallel, frame.Origin, Vector3.UnitZ, out _, out _, parallelThreshold: 1e-3f );
			Require( !hitNearPlane, "Near-parallel ray should be rejected safely" );
		} );

		// 5. Camera looking straight down an axis
		Check( "camera looking straight down an axis", () =>
		{
			var frame = GizmoFrame.World;

			// Looking straight down +X axis from (-10, 0, 0)
			var rayDownX = new Ray( new Vector3( -10f, 0f, 0f ), new Vector3( 1f, 0f, 0f ) );
			bool hitX = GizmoMath.TryHitAxis( rayDownX, frame, GizmoAxis.X, 10f, 1f, 0.12f, out _, out _ );
			Require( !hitX, "Looking straight down X axis should return false without NaN" );

			// Looking straight down +Z axis from (0, 0, 20)
			var rayDownZ = new Ray( new Vector3( 0f, 0f, 20f ), new Vector3( 0f, 0f, -1f ) );
			bool hitZ = GizmoMath.TryHitAxis( rayDownZ, frame, GizmoAxis.Z, 10f, 1f, 0.12f, out _, out _ );
			Require( !hitZ, "Looking straight down Z axis should return false without NaN" );
		} );

		// 6. Near and far zoom handle sizing
		Check( "near and far zoom handle sizing", () =>
		{
			float fov = 45f * MathF.PI / 180f;
			float viewportH = 1080f;

			float nearDist = 5f;
			float farDist = 2000f;

			float nearSize = GizmoMath.CalculatePerspectiveHandleScale( nearDist, fov, viewportH, 80f );
			float farSize = GizmoMath.CalculatePerspectiveHandleScale( farDist, fov, viewportH, 80f );

			Require( nearSize > 0f && farSize > 0f, "Handle sizes must be positive" );
			Require( farSize > nearSize * 100f, "Far handle world size must be significantly larger than near size" );
			Require( MathF.Abs( (farSize / nearSize) - (farDist / nearDist) ) < 0.1f, "Handle size ratio should match distance ratio" );
		} );

		// 7. +/- PI crossing / signed angle unwrapping
		Check( "pi crossing and signed angle unwrapping", () =>
		{
			// Test ComputeSignedAngleOnPlane
			var normal = Vector3.UnitZ;
			var v1 = new Vector3( 1f, 0f, 0f );
			var v2 = new Vector3( 0f, 1f, 0f );
			float angle90 = GizmoMath.ComputeSignedAngleOnPlane( v1, v2, normal );
			Require( MathF.Abs( angle90 - (MathF.PI * 0.5f) ) < 1e-4f, "Signed angle for 90 deg counter-clockwise failed" );

			var v3 = new Vector3( 0f, -1f, 0f );
			float angleNeg90 = GizmoMath.ComputeSignedAngleOnPlane( v1, v3, normal );
			Require( MathF.Abs( angleNeg90 - (-MathF.PI * 0.5f) ) < 1e-4f, "Signed angle for -90 deg clockwise failed" );

			// Test continuous unwrapping across +PI / -PI boundary
			float refAngle = 3.1f; // Just below +PI (3.14159)
			float jumpAngle = -3.1f; // Just above -PI (-3.14159), crossed +PI boundary
			float unwrapped = GizmoMath.UnwrapAngle( jumpAngle, refAngle );
			Require( unwrapped > MathF.PI, "Unwrapped angle should smoothly continue past +PI" );
			Require( MathF.Abs( unwrapped - (refAngle + (MathF.PI - 3.1f) + (MathF.PI - 3.1f)) ) < 0.05f, "Unwrapped value is smooth" );

			// Test RotationDragTracker
			var tracker = new GizmoMath.RotationDragTracker();
			tracker.Reset( 3.0f );
			tracker.Update( 3.1f );
			float t1 = tracker.Update( -3.1f ); // Crossing +PI boundary
			Require( t1 > MathF.PI, "Tracker should smoothly cross +PI without negative jump" );

			float t2 = tracker.Update( -3.0f );
			Require( t2 > t1, "Tracker continues increasing monotonically" );
		} );

		// 8. Positive and negative translation snapping
		Check( "positive and negative translation snapping", () =>
		{
			var start = new Vector3( 10f, 20f, -30f );
			float step = 5f;

			// Positive step
			var curPositive = new Vector3( 14.8f, 22.1f, -28.2f ); // deltas: +4.8 -> +5, +2.1 -> 0, +1.8 -> 0
			var snappedPos = GizmoMath.SnapTranslation( curPositive, start, step );
			Require( snappedPos == new Vector3( 15f, 20f, -30f ), $"Snapped positive mismatch: {snappedPos}" );

			// Negative step
			var curNegative = new Vector3( 7.1f, 17.6f, -33.4f ); // deltas: -2.9 -> -5, -2.4 -> 0, -3.4 -> -5
			var snappedNeg = GizmoMath.SnapTranslation( curNegative, start, step );
			Require( snappedNeg == new Vector3( 5f, 20f, -35f ), $"Snapped negative mismatch: {snappedNeg}" );

			// Disabled / step <= 0 leaves continuous motion
			var cont1 = GizmoMath.SnapTranslation( curPositive, start, 0f );
			Require( cont1 == curPositive, "Step 0 must leave translation continuous" );
			var cont2 = GizmoMath.SnapTranslation( curPositive, start, 5f, enabled: false );
			Require( cont2 == curPositive, "Enabled=false must leave translation continuous" );

			// Custom reference frame snapping
			var frame = new GizmoFrame( start, Vector3.UnitY, -Vector3.UnitX, Vector3.UnitZ );
			var snappedFrame = GizmoMath.SnapTranslation( curPositive, start, step, frame );
			Require( float.IsFinite( snappedFrame.X ) && float.IsFinite( snappedFrame.Y ) && float.IsFinite( snappedFrame.Z ) );
		} );

		// 9. Invalid / nonfinite snap rejection
		Check( "invalid and nonfinite snap rejection", () =>
		{
			var start = Vector3.Zero;
			var current = new Vector3( 1f, 2f, 3f );

			// Negative step rejection when snapping enabled
			Throws<ArgumentOutOfRangeException>( () => GizmoMath.SnapTranslation( current, start, -1f, enabled: true ) );
			Throws<ArgumentOutOfRangeException>( () => GizmoMath.SnapTranslation( current, start, 0f, enabled: true ) );
			Throws<ArgumentException>( () => GizmoMath.SnapTranslation( current, start, float.NaN ) );
			Throws<ArgumentException>( () => GizmoMath.SnapTranslation( current, start, float.PositiveInfinity ) );

			// Rotation snap rejection
			Throws<ArgumentOutOfRangeException>( () => GizmoMath.SnapRotation( 1.5f, -15f, enabled: true ) );
			Throws<ArgumentOutOfRangeException>( () => GizmoMath.SnapRotation( 1.5f, 0f, enabled: true ) );
			Throws<ArgumentException>( () => GizmoMath.SnapRotation( 1.5f, float.NaN ) );
			Throws<ArgumentException>( () => GizmoMath.SnapRotation( 1.5f, float.PositiveInfinity ) );

			// Rotation snapping valid cases
			float snapDeg = 15f;
			float snappedRot = GizmoMath.SnapRotation( 16f * MathF.PI / 180f, snapDeg );
			Require( MathF.Abs( snappedRot - (15f * MathF.PI / 180f) ) < 1e-4f, "Rotation snap to 15 deg failed" );

			float snappedRotNeg = GizmoMath.SnapRotation( -29f * MathF.PI / 180f, snapDeg );
			Require( MathF.Abs( snappedRotNeg - (-30f * MathF.PI / 180f) ) < 1e-4f, "Rotation snap to -30 deg failed" );
		} );

		// 10. Finite output for all degenerate / boundary fixtures
		Check( "finite output for degenerate boundary fixtures", () =>
		{
			var frame = GizmoFrame.World;

			// Zero ray direction
			var zeroRay = new Ray( Vector3.Zero, Vector3.Zero, normalize: false );
			var hitZero = GizmoMath.HitTest( zeroRay, frame, 10f );
			Require( !hitZero.IsHit, "Zero ray direction must not hit" );

			// Zero handle scale
			var normalRay = new Ray( new Vector3( 0, -10, 0 ), new Vector3( 0, 1, 0 ) );
			var hitZeroScale = GizmoMath.HitTest( normalRay, frame, 0f );
			Require( !hitZeroScale.IsHit, "Zero handle scale must not hit" );

			// Ray with NaN or Infinity coordinates
			var nanRay = new Ray( new Vector3( float.NaN, 0, 0 ), Vector3.UnitX );
			Require( !GizmoMath.TryIntersectPlane( nanRay, frame.Origin, Vector3.UnitZ, out _, out _ ), "NaN ray must safely fail" );
			Require( !GizmoMath.TryHitAxis( nanRay, frame, GizmoAxis.X, 10f, 1f, 0.12f, out _, out _ ), "NaN ray must safely fail on axis" );

			// CalculateHandleScale with zero or degenerate inputs
			float safeScale = GizmoMath.CalculatePerspectiveHandleScale( 0f, 0f, 0f, 0f );
			Require( safeScale > 0f && float.IsFinite( safeScale ), "CalculatePerspectiveHandleScale must return finite positive value" );

			float safeScaleOrtho = GizmoMath.CalculateOrthographicHandleScale( 0f, 0f, 0f );
			Require( safeScaleOrtho > 0f && float.IsFinite( safeScaleOrtho ), "CalculateOrthographicHandleScale must return finite positive value" );
		} );

		// 11. Scale clamping (no accidental negative scale)
		Check( "scale clamping no accidental negative scale", () =>
		{
			// Dragging through origin from positive 10 to negative -5
			float scaleFactor = GizmoMath.ComputeScaleFactor( 10f, -5f, minScale: 0.01f );
			Require( scaleFactor >= 0.01f, "Scale factor must not be negative when dragging through origin" );

			float scaleZero = GizmoMath.ComputeScaleFactor( 10f, 0f, minScale: 0.05f );
			Require( scaleZero >= 0.05f, "Scale factor must be clamped to positive minScale" );

			var vScale = new Vector3( -2f, 0f, 1.5f );
			var clampedV = GizmoMath.ClampScale( vScale, minScale: 0.001f );
			Require( clampedV.X >= 0.001f && clampedV.Y >= 0.001f && clampedV.Z >= 1.5f, "Vector scale clamping failed" );
		} );

		// 12. Translation plane handles hit and miss
		Check( "plane handles hit and miss", () =>
		{
			var frame = GizmoFrame.World;
			float handleScale = 10f;
			var config = new GizmoConfig(); // OffsetMin = 0.2, OffsetMax = 0.6 -> range [2.0, 6.0]

			// Hit XY plane in quadrant (3, 3, 0)
			var rayHitXY = new Ray( new Vector3( 3f, 3f, 10f ), new Vector3( 0f, 0f, -1f ) );
			Require( GizmoMath.TryHitPlane( rayHitXY, frame, GizmoPlane.XY, handleScale, config.PlaneOffsetMin, config.PlaneOffsetMax, out float d, out var pt ), "XY plane hit failed" );
			Require( MathF.Abs( pt.X - 3f ) < 0.01f && MathF.Abs( pt.Y - 3f ) < 0.01f, "XY hit point mismatch" );

			// Miss XY plane outside quad range (e.g. at 1, 1 or 8, 8 or negative quadrant)
			var rayMissXY1 = new Ray( new Vector3( 1f, 1f, 10f ), new Vector3( 0f, 0f, -1f ) );
			Require( !GizmoMath.TryHitPlane( rayMissXY1, frame, GizmoPlane.XY, handleScale, config.PlaneOffsetMin, config.PlaneOffsetMax, out _, out _ ), "XY inner miss failed" );

			var rayMissXYNeg = new Ray( new Vector3( -3f, 3f, 10f ), new Vector3( 0f, 0f, -1f ) );
			Require( !GizmoMath.TryHitPlane( rayMissXYNeg, frame, GizmoPlane.XY, handleScale, config.PlaneOffsetMin, config.PlaneOffsetMax, out _, out _ ), "XY negative quadrant miss failed" );

			// Hit YZ plane (Normal X) at (0, 3, 3)
			var rayHitYZ = new Ray( new Vector3( 10f, 3f, 3f ), new Vector3( -1f, 0f, 0f ) );
			Require( GizmoMath.TryHitPlane( rayHitYZ, frame, GizmoPlane.YZ, handleScale, config.PlaneOffsetMin, config.PlaneOffsetMax, out _, out _ ), "YZ plane hit failed" );

			// Hit XZ plane (Normal Y) at (3, 0, 3)
			var rayHitXZ = new Ray( new Vector3( 3f, 10f, 3f ), new Vector3( 0f, -1f, 0f ) );
			Require( GizmoMath.TryHitPlane( rayHitXZ, frame, GizmoPlane.XZ, handleScale, config.PlaneOffsetMin, config.PlaneOffsetMax, out _, out _ ), "XZ plane hit failed" );
		} );

		// 13. Rotation ring handles hit and miss
		Check( "rotation ring handles hit and miss", () =>
		{
			var frame = GizmoFrame.World;
			float handleScale = 10f;
			var config = new GizmoConfig(); // Radius = 1.0 (10 units), Thickness = 0.12 (1.2 units)

			// Ring Z lies in XY plane. Aim at (10, 0, 0) from (10, 0, 10) down -Z
			var rayHitZRing = new Ray( new Vector3( 10f, 0f, 10f ), new Vector3( 0f, 0f, -1f ) );
			Require( GizmoMath.TryHitRing( rayHitZRing, frame, GizmoAxis.Z, handleScale, config.RingRadius, config.RingHitThickness, out float d, out var pt ), "Z rotation ring hit failed" );
			Require( MathF.Abs( (pt - frame.Origin).Length() - 10f ) < 0.05f, "Z ring hit point distance mismatch" );

			// Miss inside the hole of the ring (radius 5)
			var rayHole = new Ray( new Vector3( 5f, 0f, 10f ), new Vector3( 0f, 0f, -1f ) );
			Require( !GizmoMath.TryHitRing( rayHole, frame, GizmoAxis.Z, handleScale, config.RingRadius, config.RingHitThickness, out _, out _ ), "Z ring hole miss failed" );

			// Miss outside the ring (radius 15)
			var rayOutside = new Ray( new Vector3( 15f, 0f, 10f ), new Vector3( 0f, 0f, -1f ) );
			Require( !GizmoMath.TryHitRing( rayOutside, frame, GizmoAxis.Z, handleScale, config.RingRadius, config.RingHitThickness, out _, out _ ), "Z ring outside miss failed" );
		} );

		// 14. Composite hit test and typed handle IDs
		Check( "composite hit test and typed handle ids", () =>
		{
			var frame = GizmoFrame.World;
			float handleScale = 10f;

			// Ray targeting translation X axis
			var rayX = new Ray( new Vector3( 8f, 10f, 0f ), new Vector3( 0f, -1f, 0f ) );
			var hitX = GizmoMath.HitTest( rayX, frame, handleScale, GizmoHandleFilter.TranslateAxes );
			Require( hitX.IsHit, "Composite hit X failed" );
			Require( hitX.HandleId == GizmoHandleId.TranslateX, $"Expected TranslateX, got {hitX.HandleId}" );
			Require( hitX.HandleId.Kind == GizmoHandleKind.TranslateAxis && hitX.HandleId.Axis == GizmoAxis.X );

			// Uniform scale center handle
			var rayCenter = new Ray( new Vector3( 0f, 10f, 0f ), new Vector3( 0f, -1f, 0f ) );
			var hitCenter = GizmoMath.HitTest( rayCenter, frame, handleScale, GizmoHandleFilter.ScaleUniform );
			Require( hitCenter.IsHit, "Composite hit center uniform scale failed" );
			Require( hitCenter.HandleId == GizmoHandleId.ScaleUniform, $"Expected ScaleUniform, got {hitCenter.HandleId}" );

			// Filter exclusion: Rotate only filter must ignore translation axes
			var hitRotateOnly = GizmoMath.HitTest( rayX, frame, handleScale, GizmoHandleFilter.Rotate );
			Require( !hitRotateOnly.IsHit, "Rotate filter must not hit translation axis" );
		} );

		// 15. Local coordinate frame transformations
		Check( "reference frame transformations", () =>
		{
			var origin = new Vector3( 100f, 50f, 25f );
			// 90 deg rotation around Z: X becomes Y, Y becomes -X
			var q = Quaternion.CreateFromAxisAngle( Vector3.UnitZ, MathF.PI * 0.5f );
			var frame = new GizmoFrame( origin, q );

			Require( MathF.Abs( frame.AxisX.Y - 1f ) < 1e-4f, "Frame X axis should point along world Y" );
			Require( MathF.Abs( frame.AxisY.X - (-1f) ) < 1e-4f, "Frame Y axis should point along world -X" );

			var worldPoint = origin + frame.AxisX * 10f;
			var localPoint = frame.ToLocal( worldPoint );
			Require( MathF.Abs( localPoint.X - 10f ) < 1e-4f && MathF.Abs( localPoint.Y ) < 1e-4f, "ToLocal failed" );

			var backToWorld = frame.ToWorld( localPoint );
			Require( (backToWorld - worldPoint).Length() < 1e-4f, "ToWorld round-trip failed" );
		} );
	}
}
