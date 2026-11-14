using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Effigy;

// Hammer brush transform + grid snap + texture lock API (no UI).
// Board task: hammer-brush-transform-grid-snap-api (worker muse-3-desktop-hammer-04).
// Group: maptransform. Register line (coordinator-owned TestRegistry.cs):
//     MapTransformTests.Register( registry, settings );
// The box fixture below is the -32..32 box recorded in
// tests/fixtures/hammer/hammer-brush-transform-grid-snap-api/box-64.json.
// Plane convention: System.Numerics.Plane, n.x + D = 0; a face's "distance" d
// (core spec) is -D. All six box faces have D = -32 (distance 32 from origin).

internal static class MapTransformTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "maptransform", name, test );
		void CheckMissing( string name, Action test, Func<string?> missing ) => registry.Add( "maptransform", name, test, missing );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"Expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"Expected {typeof( T ).Name}, got silent success" );
		}
		void Near( double actual, double expected, double tolerance, string message )
		{
			if ( Math.Abs( actual - expected ) > tolerance )
				throw new Exception( $"{message}: expected {expected}, got {actual}" );
		}
		void NearVec( Vector3 actual, Vector3 expected, float tolerance, string message )
		{
			if ( Vector3.Distance( actual, expected ) > tolerance )
				throw new Exception( $"{message}: expected {expected}, got {actual}" );
		}
		void NearVec2( Vector2 actual, Vector2 expected, float tolerance, string message )
		{
			if ( Vector2.Distance( actual, expected ) > tolerance )
				throw new Exception( $"{message}: expected {expected}, got {actual}" );
		}

		// The -32..32 box: +X, -X, +Y, -Y, +Z, -Z faces, all with D = -32.
		static Plane[] BoxPlanes() => new Plane[]
		{
			new( new Vector3( 1, 0, 0 ), -32f ),
			new( new Vector3( -1, 0, 0 ), -32f ),
			new( new Vector3( 0, 1, 0 ), -32f ),
			new( new Vector3( 0, -1, 0 ), -32f ),
			new( new Vector3( 0, 0, 1 ), -32f ),
			new( new Vector3( 0, 0, -1 ), -32f ),
		};

		// 1. Spec case: move by 10 on grid 16 snaps to 16.
		Check( "move by 10 on grid 16 snaps to 16", () =>
		{
			Near( MapTransform.SnapToGrid( 10.0, 16.0 ), 16.0, 1e-9, "SnapToGrid(10, 16)" );
			Near( MapTransform.SnapToGrid( 10f, 16.0 ), 16.0, 1e-6, "SnapToGrid(10f, 16)" );
			NearVec( MapTransform.SnapToGrid( new Vector3( 10f, 26f, -6f ), 16.0 ), new Vector3( 16f, 32f, 0f ), 1e-5f, "SnapToGrid vector" );
			Near( MapTransform.SnapToGrid( -10.0, 16.0 ), -16.0, 1e-9, "SnapToGrid(-10, 16) rounds away from zero" );
			Near( MapTransform.SnapToGrid( 8.0, 16.0 ), 16.0, 1e-9, "SnapToGrid(8, 16) tie goes away from zero" );
		} );

		// 2. Standard grid sizes, 1024 down to 0.125.
		Check( "grid sizes are 1024 down to 0.125", () =>
		{
			var expected = new double[] { 1024, 512, 256, 128, 64, 32, 16, 8, 4, 2, 1, 0.5, 0.25, 0.125 };
			Require( MapTransform.GridSizes.Count == expected.Length, $"GridSizes has {MapTransform.GridSizes.Count} entries, expected {expected.Length}" );
			for ( int i = 0; i < expected.Length; i++ )
				Near( MapTransform.GridSizes[i], expected[i], 1e-9, $"GridSizes[{i}]" );
			Near( MapTransform.MaxGridSize, 1024, 1e-9, "MaxGridSize" );
			Near( MapTransform.MinGridSize, 0.125, 1e-9, "MinGridSize" );
			Require( MapTransform.IsValidGridSize( 16 ), "16 is a standard grid size" );
			Require( MapTransform.IsValidGridSize( 0.125 ), "0.125 is a standard grid size" );
			Require( MapTransform.IsValidGridSize( 1024 ), "1024 is a standard grid size" );
			Require( !MapTransform.IsValidGridSize( 3 ), "3 is not a standard grid size" );
			Require( !MapTransform.IsValidGridSize( 0.1 ), "0.1 is not a standard grid size" );
			Require( !MapTransform.IsValidGridSize( 0 ), "0 is not a grid size" );
			Require( !MapTransform.IsValidGridSize( -16 ), "-16 is not a grid size" );
			Require( !MapTransform.IsValidGridSize( 2048 ), "2048 is not a grid size" );
			Require( !MapTransform.IsValidGridSize( double.NaN ), "NaN is not a grid size" );
		} );

		// 3. Microgrid 0.125 and the 1024 boundary.
		Check( "microgrid snap at 0.125 and 1024 boundary", () =>
		{
			Near( MapTransform.SnapToGrid( 0.2, 0.125 ), 0.25, 1e-9, "SnapToGrid(0.2, 0.125)" );
			Near( MapTransform.SnapToGrid( 0.06, 0.125 ), 0.0, 1e-9, "SnapToGrid(0.06, 0.125)" );
			Near( MapTransform.SnapToGrid( 1000.0, 1024.0 ), 1024.0, 1e-9, "SnapToGrid(1000, 1024)" );
			Near( MapTransform.SnapToGrid( 1536.0, 1024.0 ), 2048.0, 1e-9, "SnapToGrid(1536, 1024) tie goes away from zero" );
		} );

		// 4. Snap rejects bad sizes and non-finite values.
		Check( "snap rejects bad sizes and non-finite values", () =>
		{
			Throws<ArgumentOutOfRangeException>( () => MapTransform.SnapToGrid( 10.0, 0.0 ) );
			Throws<ArgumentOutOfRangeException>( () => MapTransform.SnapToGrid( 10.0, -16.0 ) );
			Throws<ArgumentOutOfRangeException>( () => MapTransform.SnapToGrid( 10.0, double.NaN ) );
			Throws<ArgumentOutOfRangeException>( () => MapTransform.SnapToGrid( double.NaN, 16.0 ) );
			Throws<ArgumentOutOfRangeException>( () => MapTransform.SnapAngle( 17.0, 0.0 ) );
			Throws<ArgumentOutOfRangeException>( () => MapTransform.SnapAngle( 17.0, -15.0 ) );
			Throws<ArgumentOutOfRangeException>( () => MapTransform.SnapAngle( double.NaN, 15.0 ) );
		} );

		// 5. Spec case: rotate 17deg with 15deg snap gives 15.
		Check( "rotate 17deg with 15deg snap gives 15", () =>
		{
			Near( MapTransform.SnapAngle( 17.0, 15.0 ), 15.0, 1e-9, "SnapAngle(17, 15)" );
			Near( MapTransform.SnapAngle( 22.0, 15.0 ), 15.0, 1e-9, "SnapAngle(22, 15)" );
			Near( MapTransform.SnapAngle( 23.0, 15.0 ), 30.0, 1e-9, "SnapAngle(23, 15)" );
			Near( MapTransform.SnapAngle( -17.0, 15.0 ), -15.0, 1e-9, "SnapAngle(-17, 15)" );
			Near( MapTransform.SnapAngle( 7.5, 15.0 ), 15.0, 1e-9, "SnapAngle(7.5, 15) tie goes away from zero" );
			Near( MapTransform.SnapAngle( 0.0, 15.0 ), 0.0, 1e-9, "SnapAngle(0, 15)" );
			Near( MapTransform.SnapAngle( 17f, 15f ), 15.0, 1e-6, "SnapAngle(17f, 15f)" );
		} );

		// 6. Translate moves every face plane; the input array is untouched.
		Check( "translate moves every face plane", () =>
		{
			var box = BoxPlanes();
			var moved = MapTransform.Translate( box, new Vector3( 10f, 0f, 0f ) );
			Require( moved.Length == 6, "translated box still has 6 faces" );
			Near( moved[0].D, -42f, 1e-5, "+X face D after +10 X (distance 42)" );
			Near( moved[1].D, -22f, 1e-5, "-X face D after +10 X (distance 22)" );
			for ( int i = 2; i < 6; i++ )
				Near( moved[i].D, -32f, 1e-5, $"face {i} D unchanged by an X move" );
			NearVec( moved[0].Normal, new Vector3( 1f, 0f, 0f ), 1e-6f, "+X normal unchanged by translation" );
			// Input untouched: MapTransform never mutates its inputs.
			Near( box[0].D, -32f, 1e-6, "input +X face D unchanged" );
			Near( box[1].D, -32f, 1e-6, "input -X face D unchanged" );
		} );

		// 7. Rotate 90deg about Z through the origin turns the +X face into +Y.
		Check( "rotate 90deg about Z through origin turns +X face into +Y", () =>
		{
			var box = BoxPlanes();
			var rotated = MapTransform.Rotate( box, Vector3.UnitZ, 90f, Vector3.Zero );
			NearVec( rotated[0].Normal, new Vector3( 0f, 1f, 0f ), 1e-5f, "+X normal becomes +Y" );
			Near( rotated[0].D, -32f, 1e-4, "+X face D stays -32 (distance 32)" );
			NearVec( rotated[1].Normal, new Vector3( 0f, -1f, 0f ), 1e-5f, "-X normal becomes -Y" );
			NearVec( rotated[2].Normal, new Vector3( -1f, 0f, 0f ), 1e-5f, "+Y normal becomes -X" );
			NearVec( rotated[4].Normal, new Vector3( 0f, 0f, 1f ), 1e-5f, "+Z normal unchanged by a Z rotation" );
			// Half-space side is preserved: (-32, 0, 0) was inside the +X half-space
			// (x <= 32); its image (0, -32, 0) must satisfy the rotated plane (y <= 32).
			var p = rotated[0];
			Require( Vector3.Dot( p.Normal, new Vector3( 0f, -32f, 0f ) ) + p.D <= 0f, "rotated image of an inside point is inside" );
			Require( Vector3.Dot( p.Normal, new Vector3( 0f, 40f, 0f ) ) + p.D > 0f, "a point past the rotated face is outside" );
			// Quaternion overload agrees with the axis overload.
			var byQuat = MapTransform.Rotate( box, Quaternion.CreateFromAxisAngle( Vector3.UnitZ, MathF.PI / 2f ), Vector3.Zero );
			NearVec( byQuat[0].Normal, rotated[0].Normal, 1e-5f, "quaternion rotation matches axis rotation" );
			Near( byQuat[0].D, rotated[0].D, 1e-4, "quaternion rotation D matches axis rotation" );
		} );

		// 8. Rotate about a pivot moves the plane: the pivot point stays on it.
		Check( "rotate about a pivot moves the plane", () =>
		{
			var face = new Plane[] { new( new Vector3( 1, 0, 0 ), -32f ) }; // plane x = 32
			var aboutPivot = MapTransform.Rotate( face, Vector3.UnitZ, 90f, new Vector3( 32f, 0f, 0f ) );
			NearVec( aboutPivot[0].Normal, new Vector3( 0f, 1f, 0f ), 1e-5f, "normal turns to +Y" );
			Near( aboutPivot[0].D, 0f, 1e-4, "plane through the pivot (32,0,0) has D = 0 after the turn" );
			var aboutOrigin = MapTransform.Rotate( face, Vector3.UnitZ, 90f, Vector3.Zero );
			Near( aboutOrigin[0].D, -32f, 1e-4, "same plane turned about the origin has D = -32" );
			// The pivot point itself does not move under its own rotation matrix.
			NearVec( MapTransform.TransformPoint( new Vector3( 32f, 0f, 0f ), MapTransform.RotationMatrix( Vector3.UnitZ, 90f, new Vector3( 32f, 0f, 0f ) ) ),
				new Vector3( 32f, 0f, 0f ), 1e-5f, "pivot point fixed by its rotation" );
		} );

		// 9. Scale doubles distances about the origin and holds the pivot.
		Check( "scale doubles distances about origin, holds the pivot", () =>
		{
			var box = BoxPlanes();
			var doubled = MapTransform.Scale( box, 2f, Vector3.Zero );
			for ( int i = 0; i < 6; i++ )
				Near( doubled[i].D, -64f, 1e-4, $"face {i} D doubles to -64" );
			var aboutPivot = MapTransform.Scale( box, new Vector3( 2f, 2f, 2f ), new Vector3( 32f, 0f, 0f ) );
			Near( aboutPivot[0].D, -32f, 1e-4, "+X face through the pivot stays at distance 32" );
			Near( aboutPivot[1].D, -96f, 1e-4, "-X face moves to x = -96 (D = -96)" );
			Near( aboutPivot[2].D, -64f, 1e-4, "+Y face moves to y = 64 (D = -64)" );
			Near( aboutPivot[4].D, -64f, 1e-4, "+Z face moves to z = 64 (D = -64)" );
			// Non-uniform scale: X faces double, Y faces stay.
			var stretched = MapTransform.Scale( box, new Vector3( 2f, 1f, 1f ), Vector3.Zero );
			Near( stretched[0].D, -64f, 1e-4, "+X face doubles under X-only scale" );
			Near( stretched[2].D, -32f, 1e-4, "+Y face unchanged under X-only scale" );
		} );

		// 10. Zero scale and singular matrices are rejected, never silently fixed.
		Check( "zero scale and singular matrices are rejected", () =>
		{
			var box = BoxPlanes();
			Throws<ArgumentException>( () => MapTransform.Scale( box, new Vector3( 0f, 1f, 1f ), Vector3.Zero ) );
			Throws<ArgumentException>( () => MapTransform.Scale( box, 0f, Vector3.Zero ) );
			Throws<ArgumentException>( () => MapTransform.ScaleMatrix( new Vector3( 1f, 0f, 1f ), Vector3.Zero ) );
			Throws<ArgumentException>( () => MapTransform.TransformPlane( box[0], Matrix4x4.CreateScale( 0f, 1f, 1f ) ) );
			Throws<ArgumentException>( () => MapTransform.Transform( box, new Matrix4x4() ) );
			Throws<ArgumentException>( () => MapTransform.RotationMatrix( Vector3.Zero, 90f, Vector3.Zero ) );
			Throws<ArgumentNullException>( () => MapTransform.Transform( null!, Matrix4x4.Identity ) );
		} );

		// 11. Axis/plane-constrained deltas (desktop callers pass
		// GizmoFrame.GetAxis(GizmoAxis.X) / GetPlaneNormal(GizmoPlane.XY)).
		Check( "axis and plane constrained deltas", () =>
		{
			NearVec( MapTransform.ConstrainToAxis( new Vector3( 3f, 4f, 0f ), new Vector3( 1f, 0f, 0f ) ), new Vector3( 3f, 0f, 0f ), 1e-6f, "X-axis constraint" );
			NearVec( MapTransform.ConstrainToAxis( new Vector3( 3f, 4f, 0f ), new Vector3( 0f, 2f, 0f ) ), new Vector3( 0f, 4f, 0f ), 1e-6f, "non-unit Y axis is normalized" );
			NearVec( MapTransform.ConstrainToPlane( new Vector3( 3f, 4f, 5f ), new Vector3( 0f, 0f, 1f ) ), new Vector3( 3f, 4f, 0f ), 1e-6f, "XY-plane constraint drops Z" );
			NearVec( MapTransform.ConstrainToPlane( new Vector3( 3f, 4f, 5f ), new Vector3( 0f, 3f, 0f ) ), new Vector3( 3f, 0f, 5f ), 1e-6f, "non-unit XZ-plane normal is normalized" );
			Throws<ArgumentException>( () => MapTransform.ConstrainToAxis( new Vector3( 1f, 0f, 0f ), Vector3.Zero ) );
			Throws<ArgumentException>( () => MapTransform.ConstrainToPlane( new Vector3( 1f, 0f, 0f ), Vector3.Zero ) );
		} );

		// 12. Default UV axes for the +Z and +X faces.
		Check( "default UV axes for +Z and +X faces", () =>
		{
			var top = MapUvMapping.DefaultForNormal( new Vector3( 0f, 0f, 1f ) );
			NearVec( top.UAxis, new Vector3( 1f, 0f, 0f ), 1e-6f, "+Z face U is +X" );
			NearVec( top.VAxis, new Vector3( 0f, 1f, 0f ), 1e-6f, "+Z face V is +Y" );
			var side = MapUvMapping.DefaultForNormal( new Vector3( 1f, 0f, 0f ) );
			NearVec( side.UAxis, new Vector3( 0f, 0f, -1f ), 1e-6f, "+X face U is -Z" );
			NearVec( side.VAxis, new Vector3( 0f, 1f, 0f ), 1e-6f, "+X face V is +Y" );
			Throws<ArgumentException>( () => MapUvMapping.DefaultForNormal( Vector3.Zero ) );
		} );

		// 13. Spec case: texture lock keeps a face UV constant after a translate.
		Check( "texture lock keeps a face UV constant after a translate", () =>
		{
			var mapping = MapUvMapping.DefaultForNormal( new Vector3( 0f, 0f, 1f ) );
			var point = new Vector3( 2f, 3f, 32f );
			NearVec2( mapping.Sample( point ), new Vector2( 2f, 3f ), 1e-6f, "UV before the move" );
			var locked = MapTransform.ApplyTextureLock( mapping, MapTransform.TranslationMatrix( new Vector3( 10f, 0f, 0f ) ) );
			NearVec2( locked.Sample( new Vector3( 12f, 3f, 32f ) ), new Vector2( 2f, 3f ), 1e-5f, "locked UV at the moved point" );
			NearVec2( locked.Offset, new Vector2( -10f, 0f ), 1e-5f, "locked offset compensates the move" );
			// Without the recompute the texture slides: the stored mapping at the
			// moved point reads (12, 3), not (2, 3).
			NearVec2( mapping.Sample( new Vector3( 12f, 3f, 32f ) ), new Vector2( 12f, 3f ), 1e-6f, "unlocked UV slides with the world" );
		} );

		// 14. Texture lock keeps a face UV constant after a rotation.
		Check( "texture lock keeps a face UV constant after a rotation", () =>
		{
			var mapping = MapUvMapping.DefaultForNormal( new Vector3( 0f, 0f, 1f ) );
			var point = new Vector3( 2f, 3f, 32f );
			var locked = MapTransform.ApplyTextureLock( mapping, MapTransform.RotationMatrix( Vector3.UnitZ, 90f, Vector3.Zero ) );
			NearVec2( locked.Sample( new Vector3( -3f, 2f, 32f ) ), new Vector2( 2f, 3f ), 1e-4f, "locked UV at the rotated point" );
			NearVec( locked.UAxis, new Vector3( 0f, 1f, 0f ), 1e-5f, "locked U axis turns with the face" );
			NearVec( locked.VAxis, new Vector3( -1f, 0f, 0f ), 1e-5f, "locked V axis turns with the face" );
			Near( locked.Scale.X, 1.0, 1e-5, "rotation does not change the U scale" );
		} );

		// 15. Texture lock with offset, scale and rotation, under translate/rotate/scale.
		Check( "texture lock with offset, scale and rotation", () =>
		{
			var mapping = new MapUvMapping(
				new Vector3( 1f, 0f, 0f ), new Vector3( 0f, 1f, 0f ),
				new Vector2( 5f, -2f ), new Vector2( 0.5f, 2f ), 30f );
			var point = new Vector3( 2f, 3f, 32f );
			var uvBefore = mapping.Sample( point );

			var moved = MapTransform.ApplyTextureLock( mapping, MapTransform.TranslationMatrix( new Vector3( 10f, -4f, 7f ) ) );
			NearVec2( moved.Sample( new Vector3( 12f, -1f, 39f ) ), uvBefore, 1e-4f, "locked UV after translate" );

			var turned = MapTransform.ApplyTextureLock( mapping, MapTransform.RotationMatrix( Vector3.UnitZ, 45f, new Vector3( 0f, 0f, 32f ) ) );
			var rotatedPoint = MapTransform.TransformPoint( point, MapTransform.RotationMatrix( Vector3.UnitZ, 45f, new Vector3( 0f, 0f, 32f ) ) );
			NearVec2( turned.Sample( rotatedPoint ), uvBefore, 1e-4f, "locked UV after rotate about a pivot" );

			var scaled = MapTransform.ApplyTextureLock( mapping, MapTransform.ScaleMatrix( 2f, Vector3.Zero ) );
			NearVec2( scaled.Sample( new Vector3( 4f, 6f, 64f ) ), uvBefore, 1e-4f, "locked UV after uniform scale" );
			Near( scaled.Scale.X, 0.25, 1e-5, "scale halves the effective U scale twice over (axis length folds in)" );
		} );

		// 16. Texture lock off leaves the mapping untouched.
		Check( "texture lock off leaves the mapping untouched", () =>
		{
			var mapping = new MapUvMapping(
				new Vector3( 1f, 0f, 0f ), new Vector3( 0f, 1f, 0f ),
				new Vector2( 5f, -2f ), new Vector2( 0.5f, 2f ), 30f );
			var matrix = MapTransform.TranslationMatrix( new Vector3( 10f, 0f, 0f ) );
			var unlocked = MapTransform.ApplyTextureLock( mapping, matrix, textureLocked: false );
			NearVec( unlocked.UAxis, mapping.UAxis, 1e-6f, "unlocked U axis unchanged" );
			NearVec( unlocked.VAxis, mapping.VAxis, 1e-6f, "unlocked V axis unchanged" );
			NearVec2( unlocked.Offset, mapping.Offset, 1e-6f, "unlocked offset unchanged" );
			NearVec2( unlocked.Scale, mapping.Scale, 1e-6f, "unlocked scale unchanged" );
			Near( unlocked.RotationDegrees, mapping.RotationDegrees, 1e-6, "unlocked rotation unchanged" );
		} );

		// 17. Texture lock rejects bad mappings and singular transforms.
		Check( "texture lock rejects bad mappings and singular transforms", () =>
		{
			Throws<ArgumentException>( () => new MapUvMapping( Vector3.Zero, Vector3.UnitY, Vector2.Zero, Vector2.One, 0f ) );
			Throws<ArgumentException>( () => new MapUvMapping( Vector3.UnitX, Vector3.UnitY, Vector2.Zero, new Vector2( 0f, 1f ), 0f ) );
			Throws<ArgumentException>( () => new MapUvMapping( Vector3.UnitX, Vector3.UnitY, Vector2.Zero, Vector2.One, float.NaN ) );
			var mapping = MapUvMapping.DefaultForNormal( new Vector3( 0f, 0f, 1f ) );
			Throws<ArgumentException>( () => MapTransform.ApplyTextureLock( mapping, Matrix4x4.CreateScale( 0f, 1f, 1f ) ) );
		} );

		// 18. Compose is scale, then rotate, then translate about the pivot.
		Check( "compose is scale, then rotate, then translate about the pivot", () =>
		{
			var rotation = Quaternion.CreateFromAxisAngle( Vector3.UnitZ, MathF.PI / 2f );
			var matrix = MapTransform.ComposeMatrix( new Vector3( 10f, 0f, 0f ), rotation, new Vector3( 2f, 1f, 1f ), new Vector3( 1f, 0f, 0f ) );
			NearVec( MapTransform.TransformPoint( new Vector3( 2f, 0f, 0f ), matrix ), new Vector3( 11f, 2f, 0f ), 1e-5f, "point through scale->rotate->translate" );
			NearVec( MapTransform.TransformPoint( new Vector3( 1f, 0f, 0f ), matrix ), new Vector3( 11f, 0f, 0f ), 1e-5f, "pivot point only translates" );
			// Matches the manual chain: T(-pivot) * S * R * T(pivot) * T(translate).
			var manual = Matrix4x4.CreateTranslation( new Vector3( -1f, 0f, 0f ) )
				* Matrix4x4.CreateScale( new Vector3( 2f, 1f, 1f ) )
				* Matrix4x4.CreateFromQuaternion( rotation )
				* Matrix4x4.CreateTranslation( new Vector3( 1f, 0f, 0f ) )
				* Matrix4x4.CreateTranslation( new Vector3( 10f, 0f, 0f ) );
			NearVec( MapTransform.TransformPoint( new Vector3( 5f, 3f, 0f ), matrix ),
				MapTransform.TransformPoint( new Vector3( 5f, 3f, 0f ), manual ), 1e-5f, "compose matches the manual chain" );
		} );

		// 19. Selection transform keeps brush counts and leaves inputs untouched.
		Check( "selection transform keeps brush counts", () =>
		{
			var first = BoxPlanes();
			var second = BoxPlanes();
			var selection = new IReadOnlyList<Plane>[] { first, second };
			var moved = MapTransform.TransformSelection( selection, MapTransform.TranslationMatrix( new Vector3( 10f, 0f, 0f ) ) );
			Require( moved.Length == 2, "selection still has 2 brushes" );
			Require( moved[0].Length == 6 && moved[1].Length == 6, "each brush still has 6 faces" );
			Near( moved[0][0].D, -42f, 1e-5, "first brush +X face moved" );
			Near( moved[1][0].D, -42f, 1e-5, "second brush +X face moved" );
			Near( first[0].D, -32f, 1e-6, "input selection untouched" );
			Throws<ArgumentNullException>( () => MapTransform.TransformSelection( null!, Matrix4x4.Identity ) );
			Throws<ArgumentNullException>( () => MapTransform.TransformSelection( new IReadOnlyList<Plane>[] { first, null! }, Matrix4x4.Identity ) );
			var empty = MapTransform.TransformSelection( Array.Empty<IReadOnlyList<Plane>>(), Matrix4x4.Identity );
			Require( empty.Length == 0, "empty selection stays empty" );
		} );

		// 20. Fixture record: box-64.json matches the in-code box and the spec numbers.
		CheckMissing( "fixture box-64.json matches the in-code box", () =>
		{
			var path = FindFixture()!;
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var root = doc.RootElement;
			var faces = root.GetProperty( "box" ).GetProperty( "faces" );
			Require( faces.GetArrayLength() == 6, "fixture box has 6 faces" );
			var box = BoxPlanes();
			for ( int i = 0; i < 6; i++ )
			{
				var face = faces[i];
				var normal = ReadVec3( face.GetProperty( "normal" ) );
				NearVec( normal, box[i].Normal, 1e-6f, $"fixture face {i} normal" );
				Near( face.GetProperty( "distance" ).GetDouble(), -box[i].D, 1e-9, $"fixture face {i} distance" );
				var uv = face.GetProperty( "uv" );
				var mapping = new MapUvMapping(
					ReadVec3( uv.GetProperty( "uAxis" ) ),
					ReadVec3( uv.GetProperty( "vAxis" ) ),
					ReadVec2( uv.GetProperty( "offset" ) ),
					ReadVec2( uv.GetProperty( "scale" ) ),
					uv.GetProperty( "rotationDegrees" ).GetSingle() );
				var expected = MapUvMapping.DefaultForNormal( box[i].Normal );
				NearVec( mapping.UAxis, expected.UAxis, 1e-6f, $"fixture face {i} U axis" );
				NearVec( mapping.VAxis, expected.VAxis, 1e-6f, $"fixture face {i} V axis" );
			}
			var expectedJson = root.GetProperty( "expected" );
			var snapMove = expectedJson.GetProperty( "snapMove" );
			Near( MapTransform.SnapToGrid( snapMove.GetProperty( "delta" ).GetDouble(), snapMove.GetProperty( "grid" ).GetDouble() ),
				snapMove.GetProperty( "snapped" ).GetDouble(), 1e-9, "fixture snapMove" );
			var snapAngle = expectedJson.GetProperty( "snapAngle" );
			Near( MapTransform.SnapAngle( snapAngle.GetProperty( "degrees" ).GetDouble(), snapAngle.GetProperty( "snap" ).GetDouble() ),
				snapAngle.GetProperty( "snapped" ).GetDouble(), 1e-9, "fixture snapAngle" );
			var textureLock = expectedJson.GetProperty( "textureLock" );
			var mappingTop = MapUvMapping.DefaultForNormal( new Vector3( 0f, 0f, 1f ) );
			var point = ReadVec3( textureLock.GetProperty( "point" ) );
			var uvAtPoint = ReadVec2( textureLock.GetProperty( "uv" ) );
			NearVec2( mappingTop.Sample( point ), uvAtPoint, 1e-6f, "fixture texture-lock UV before the move" );
			var delta = ReadVec3( textureLock.GetProperty( "delta" ) );
			var locked = MapTransform.ApplyTextureLock( mappingTop, MapTransform.TranslationMatrix( delta ) );
			NearVec2( locked.Sample( point + delta ), ReadVec2( textureLock.GetProperty( "lockedUvAtMovedPoint" ) ), 1e-5f, "fixture locked UV at the moved point" );
			NearVec2( mappingTop.Sample( point + delta ), ReadVec2( textureLock.GetProperty( "unlockedUvAtMovedPoint" ) ), 1e-6f, "fixture unlocked UV at the moved point" );
		}, () => FindFixture() is null ? "fixture box-64.json not found under the repo root" : null );

		static string? FindFixture()
		{
			var dir = new DirectoryInfo( AppContext.BaseDirectory );
			for ( int i = 0; i < 8 && dir != null; i++ )
			{
				var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-brush-transform-grid-snap-api", "box-64.json" );
				if ( File.Exists( candidate ) ) return candidate;
				dir = dir.Parent;
			}
			return null;
		}

		Vector3 ReadVec3( JsonElement element )
		{
			var values = new List<float>();
			foreach ( var item in element.EnumerateArray() ) values.Add( item.GetSingle() );
			Require( values.Count == 3, "expected a 3-component vector in the fixture" );
			return new Vector3( values[0], values[1], values[2] );
		}

		Vector2 ReadVec2( JsonElement element )
		{
			var values = new List<float>();
			foreach ( var item in element.EnumerateArray() ) values.Add( item.GetSingle() );
			Require( values.Count == 2, "expected a 2-component vector in the fixture" );
			return new Vector2( values[0], values[1] );
		}
	}
}
