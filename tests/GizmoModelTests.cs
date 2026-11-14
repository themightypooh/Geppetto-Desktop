using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Effigy;

// Hammer gizmo + pivot tool model (state + math only, no rendering, no UI).
// Board task: hammer-gizmo-pivot-tool-model (worker muse-3-desktop-hammer-32).
// Group: gizmomodel. Register line (coordinator-owned TestRegistry.cs):
//     GizmoModelTests.Register( registry, settings );
// Fixture: tests/fixtures/hammer/hammer-gizmo-pivot-tool-model/drag-cases.json.
// Drag convention: a GizmoDrag ViewDelta is in view space (x = view Right,
// y = view Up, z = view Forward); GizmoView.Front has Right=+X, Up=+Y,
// Forward=-Z. Rotation angles are degrees, measured in the rotation plane
// from the plane U axis toward V (right-hand rule about the rotation axis).
// All expected values below were hand-computed from that convention, not
// copied from the implementation.

internal static class GizmoModelTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "gizmomodel", name, test );
		void CheckMissing( string name, Action test, Func<string?> missing ) => registry.Add( "gizmomodel", name, test, missing );
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
		void NearMat( Matrix4x4 actual, Matrix4x4 expected, float tolerance, string message )
		{
			var a = new[] { actual.M11, actual.M12, actual.M13, actual.M14, actual.M21, actual.M22, actual.M23, actual.M24, actual.M31, actual.M32, actual.M33, actual.M34, actual.M41, actual.M42, actual.M43, actual.M44 };
			var e = new[] { expected.M11, expected.M12, expected.M13, expected.M14, expected.M21, expected.M22, expected.M23, expected.M24, expected.M31, expected.M32, expected.M33, expected.M34, expected.M41, expected.M42, expected.M43, expected.M44 };
			for ( int i = 0; i < 16; i++ )
				if ( MathF.Abs( a[i] - e[i] ) > tolerance )
					throw new Exception( $"{message}: element {i} expected {e[i]}, got {a[i]}" );
		}
		Vector3 Apply( Matrix4x4 m, Vector3 p ) => Vector3.Transform( p, m );

		// 1. Defaults: translate, global, no lock, pivot at origin, snap off.
		Check( "defaults are translate global no-lock origin snap-off", () =>
		{
			var model = new GizmoModel();
			Require( model.Mode == GizmoMode.Translate, "default mode is translate" );
			Require( model.Space == GizmoSpace.Global, "default space is global" );
			Require( model.LockedAxis == GizmoAxisLock.None, "default lock is none" );
			NearVec( model.Pivot, Vector3.Zero, 1e-6f, "default pivot" );
			Require( model.SelectionOrientation == Quaternion.Identity, "default orientation is identity" );
			Require( !model.SnapEnabled, "snap is off by default" );
			Near( model.GridSize, GizmoModel.DefaultGridSize, 1e-9, "default grid size" );
			Near( model.AngleSnapDegrees, GizmoModel.DefaultAngleSnapDegrees, 1e-9, "default angle snap" );
			var state = model.State;
			Require( state.Mode == GizmoMode.Translate && state.Space == GizmoSpace.Global && state.LockedAxis == GizmoAxisLock.None, "state snapshot matches" );
		} );

		// 2. SetMode/SetSpace/LockAxis/UnlockAxis mutate state; snapshot reflects them.
		Check( "setters mutate state and snapshot reflects them", () =>
		{
			var model = new GizmoModel();
			model.SetMode( GizmoMode.Rotate );
			model.SetSpace( GizmoSpace.Local );
			model.LockAxis( GizmoAxisLock.Z );
			model.SetPivot( new Vector3( 1f, 2f, 3f ) );
			model.SetSnap( enabled: true, gridSize: 8.0, angleSnapDegrees: 30.0 );
			Require( model.Mode == GizmoMode.Rotate, "mode set" );
			Require( model.Space == GizmoSpace.Local, "space set" );
			Require( model.LockedAxis == GizmoAxisLock.Z, "lock set" );
			NearVec( model.Pivot, new Vector3( 1f, 2f, 3f ), 1e-6f, "pivot set" );
			Require( model.SnapEnabled && model.GridSize == 8.0 && model.AngleSnapDegrees == 30.0, "snap set" );
			model.UnlockAxis();
			Require( model.LockedAxis == GizmoAxisLock.None, "unlock clears the lock" );
			var state = model.State;
			Require( state.Mode == GizmoMode.Rotate && state.LockedAxis == GizmoAxisLock.None && state.SnapEnabled, "snapshot after unlock" );
			model.Reset();
			Require( model.Mode == GizmoMode.Translate && model.Space == GizmoSpace.Global && !model.SnapEnabled, "reset restores defaults" );
			NearVec( model.Pivot, Vector3.Zero, 1e-6f, "reset restores pivot" );
		} );

		// 3. CycleMode (Ctrl+J) and ToggleSpace cycle through the options.
		Check( "cycle mode and toggle space", () =>
		{
			var model = new GizmoModel();
			model.CycleMode();
			Require( model.Mode == GizmoMode.Rotate, "cycle 1 is rotate" );
			model.CycleMode();
			Require( model.Mode == GizmoMode.Scale, "cycle 2 is scale" );
			model.CycleMode();
			Require( model.Mode == GizmoMode.Translate, "cycle 3 wraps to translate" );
			model.ToggleSpace();
			Require( model.Space == GizmoSpace.Local, "toggle to local" );
			model.ToggleSpace();
			Require( model.Space == GizmoSpace.Global, "toggle back to global" );
		} );

		// 4. Spec case: a drag in translate mode moves along the locked axis only.
		Check( "translate locked axis moves along that axis only", () =>
		{
			var model = new GizmoModel();
			model.LockAxis( GizmoAxisLock.X );
			var m = model.DragToTransform( new GizmoDrag( new Vector3( 10f, 5f, 0f ) ), GizmoView.Front );
			NearVec( m.Translation, new Vector3( 10f, 0f, 0f ), 1e-5f, "locked X keeps only the X component" );
			model.LockAxis( GizmoAxisLock.Y );
			m = model.DragToTransform( new GizmoDrag( new Vector3( 10f, 5f, 0f ) ), GizmoView.Front );
			NearVec( m.Translation, new Vector3( 0f, 5f, 0f ), 1e-5f, "locked Y keeps only the Y component" );
			model.LockAxis( GizmoAxisLock.Z );
			m = model.DragToTransform( new GizmoDrag( new Vector3( 10f, 5f, 3f ) ), GizmoView.Front );
			// ViewDelta z=3 is along view Forward (-Z world), so the world drag is (10, 5, -3); locking Z keeps -3.
			NearVec( m.Translation, new Vector3( 0f, 0f, -3f ), 1e-5f, "locked Z keeps only the view-forward component" );
		} );

		// 5. Plane lock keeps the drag inside the plane.
		Check( "translate plane lock drops the out-of-plane component", () =>
		{
			var model = new GizmoModel();
			model.LockAxis( GizmoAxisLock.XY );
			var m = model.DragToTransform( new GizmoDrag( new Vector3( 10f, 5f, 3f ) ), GizmoView.Front );
			NearVec( m.Translation, new Vector3( 10f, 5f, 0f ), 1e-5f, "XY plane drops Z" );
			model.LockAxis( GizmoAxisLock.YZ );
			m = model.DragToTransform( new GizmoDrag( new Vector3( 10f, 5f, 3f ) ), GizmoView.Front );
			NearVec( m.Translation, new Vector3( 0f, 5f, -3f ), 1e-5f, "YZ plane drops X" );
		} );

		// 6. Grid snap on translate: 10 on grid 16 snaps to 16 (spec number).
		Check( "translate snap to grid 16", () =>
		{
			var model = new GizmoModel();
			model.SetSnap( enabled: true, gridSize: 16.0, angleSnapDegrees: 15.0 );
			model.LockAxis( GizmoAxisLock.X );
			var m = model.DragToTransform( new GizmoDrag( new Vector3( 10f, 0f, 0f ) ), GizmoView.Front );
			NearVec( m.Translation, new Vector3( 16f, 0f, 0f ), 1e-5f, "10 on grid 16 snaps to 16" );
			model.UnlockAxis();
			m = model.DragToTransform( new GizmoDrag( new Vector3( 10f, 26f, 0f ) ), GizmoView.Front );
			NearVec( m.Translation, new Vector3( 16f, 32f, 0f ), 1e-5f, "free drag snaps each component" );
			model.SetSnap( enabled: false, gridSize: 16.0, angleSnapDegrees: 15.0 );
			m = model.DragToTransform( new GizmoDrag( new Vector3( 10f, 0f, 0f ) ), GizmoView.Front );
			NearVec( m.Translation, new Vector3( 10f, 0f, 0f ), 1e-5f, "snap off leaves the drag exact" );
		} );

		// 7. Local-space translate differs from global on a rotated selection.
		Check( "translate local differs from global on rotated selection", () =>
		{
			var orientation = Quaternion.CreateFromAxisAngle( Vector3.UnitZ, MathF.PI / 2f ); // local X = world Y
			var model = new GizmoModel();
			model.SetSelectionOrientation( orientation );
			model.LockAxis( GizmoAxisLock.X );
			var drag = new GizmoDrag( new Vector3( 10f, 10f, 0f ) );
			var global = model.DragToTransform( drag, GizmoView.Front );
			NearVec( global.Translation, new Vector3( 10f, 0f, 0f ), 1e-5f, "global lock X moves along world X" );
			model.SetSpace( GizmoSpace.Local );
			var local = model.DragToTransform( drag, GizmoView.Front );
			NearVec( local.Translation, new Vector3( 0f, 10f, 0f ), 1e-5f, "local lock X moves along world Y (the selection's X)" );
			Require( Vector3.Distance( global.Translation, local.Translation ) > 1f, "local and global translations differ" );
			NearVec( model.LockedAxisWorld(), new Vector3( 0f, 1f, 0f ), 1e-5f, "LockedAxisWorld reports the rotated axis" );
		} );

		// 8. Rotate about world Z by the drag direction angle; pivot stays fixed.
		Check( "rotate global Z by drag direction angle", () =>
		{
			var model = new GizmoModel();
			model.SetMode( GizmoMode.Rotate );
			model.LockAxis( GizmoAxisLock.Z );
			var m = model.DragToTransform( new GizmoDrag( new Vector3( 1f, 1f, 0f ) ), GizmoView.Front );
			NearMat( m, MapTransform.RotationMatrix( Vector3.UnitZ, 45f, Vector3.Zero ), 1e-5f, "drag (1,1,0) is 45 deg about Z" );
			NearVec( Apply( m, new Vector3( 1f, 0f, 0f ) ), new Vector3( 0.70710678f, 0.70710678f, 0f ), 1e-5f, "point (1,0,0) swings to 45 deg" );
			NearVec( Apply( m, Vector3.Zero ), Vector3.Zero, 1e-6f, "pivot at origin stays fixed" );
		} );

		// 9. Spec case: local-space rotation differs from global on a rotated selection.
		Check( "rotate local differs from global on rotated selection", () =>
		{
			var orientation = Quaternion.CreateFromAxisAngle( Vector3.UnitY, MathF.PI / 2f ); // local Z = world X
			var model = new GizmoModel();
			model.SetMode( GizmoMode.Rotate );
			model.SetSelectionOrientation( orientation );
			model.LockAxis( GizmoAxisLock.Z );
			var drag = new GizmoDrag( new Vector3( 1f, 1f, 1f ) ); // world drag (1, 1, -1)
			var global = model.DragToTransform( drag, GizmoView.Front );
			NearMat( global, MapTransform.RotationMatrix( Vector3.UnitZ, 45f, Vector3.Zero ), 1e-5f, "global lock Z is 45 deg about world Z" );
			model.SetSpace( GizmoSpace.Local );
			var local = model.DragToTransform( drag, GizmoView.Front );
			NearMat( local, MapTransform.RotationMatrix( Vector3.UnitX, -45f, Vector3.Zero ), 1e-5f, "local lock Z is -45 deg about world X" );
			NearVec( Apply( global, new Vector3( 0f, 1f, 0f ) ), new Vector3( -0.70710678f, 0.70710678f, 0f ), 1e-5f, "global swings (0,1,0) in the XY plane" );
			NearVec( Apply( local, new Vector3( 0f, 1f, 0f ) ), new Vector3( 0f, 0.70710678f, -0.70710678f ), 1e-5f, "local swings (0,1,0) in the YZ plane" );
		} );

		// 10. Snap-on-rotate: 17 degrees with a 15-degree snap gives 15.
		Check( "rotate snap 17 degrees to 15", () =>
		{
			var model = new GizmoModel();
			model.SetMode( GizmoMode.Rotate );
			model.SetSnap( enabled: true, gridSize: 16.0, angleSnapDegrees: 15.0 );
			model.LockAxis( GizmoAxisLock.Z );
			float radians = 17f * MathF.PI / 180f;
			var drag = new GizmoDrag( new Vector3( MathF.Cos( radians ), MathF.Sin( radians ), 0f ) );
			var m = model.DragToTransform( drag, GizmoView.Front );
			NearMat( m, MapTransform.RotationMatrix( Vector3.UnitZ, 15f, Vector3.Zero ), 1e-5f, "17 deg snaps to 15 deg" );
			NearVec( Apply( m, new Vector3( 1f, 0f, 0f ) ), new Vector3( 0.96592583f, 0.25881905f, 0f ), 1e-5f, "point (1,0,0) swings to 15 deg" );
		} );

		// 11. Spec case: pivot change alters the rotation centre.
		Check( "pivot change alters the rotation centre", () =>
		{
			var model = new GizmoModel();
			model.SetMode( GizmoMode.Rotate );
			model.LockAxis( GizmoAxisLock.Z );
			var drag = new GizmoDrag( new Vector3( 1f, 1f, 0f ) );
			var atOrigin = model.DragToTransform( drag, GizmoView.Front );
			model.SetPivot( new Vector3( 10f, 0f, 0f ) );
			var atPivot = model.DragToTransform( drag, GizmoView.Front );
			NearVec( Apply( atPivot, new Vector3( 10f, 0f, 0f ) ), new Vector3( 10f, 0f, 0f ), 1e-6f, "pivot point itself does not move" );
			NearVec( Apply( atPivot, new Vector3( 11f, 0f, 0f ) ), new Vector3( 10.70710678f, 0.70710678f, 0f ), 1e-5f, "point next to the pivot swings around it" );
			Require( Vector3.Distance( Apply( atOrigin, new Vector3( 11f, 0f, 0f ) ), Apply( atPivot, new Vector3( 11f, 0f, 0f ) ) ) > 1f, "the two pivots move the point differently" );
		} );

		// 12. No lock in rotate mode rotates about the view Forward axis (screen-plane rotation).
		Check( "rotate with no lock rotates about view forward", () =>
		{
			var model = new GizmoModel();
			model.SetMode( GizmoMode.Rotate );
			var m = model.DragToTransform( new GizmoDrag( new Vector3( 1f, 1f, 0f ) ), GizmoView.Front );
			NearMat( m, MapTransform.RotationMatrix( -Vector3.UnitZ, -45f, Vector3.Zero ), 1e-5f, "up-right drag is -45 deg about -Z" );
			NearVec( Apply( m, new Vector3( 1f, 0f, 0f ) ), new Vector3( 0.70710678f, 0.70710678f, 0f ), 1e-5f, "screen-counterclockwise turn" );
			model.LockAxis( GizmoAxisLock.XY );
			var plane = model.DragToTransform( new GizmoDrag( new Vector3( 1f, 1f, 0f ) ), GizmoView.Front );
			NearMat( plane, MapTransform.RotationMatrix( Vector3.UnitZ, 45f, Vector3.Zero ), 1e-5f, "XY plane lock rotates about the plane normal Z" );
		} );

		// 13. Scale: axis lock scales one axis, plane lock scales two, pivot is the centre.
		Check( "scale axis plane and pivot", () =>
		{
			var model = new GizmoModel();
			model.SetMode( GizmoMode.Scale );
			model.LockAxis( GizmoAxisLock.X );
			var m = model.DragToTransform( new GizmoDrag( new Vector3( 0.5f, 2f, 0f ) ), GizmoView.Front );
			NearVec( Apply( m, new Vector3( 2f, 3f, 4f ) ), new Vector3( 3f, 3f, 4f ), 1e-5f, "lock X scales X by 1.5 only" );
			model.LockAxis( GizmoAxisLock.XY );
			m = model.DragToTransform( new GizmoDrag( new Vector3( 0.5f, 0.25f, 3f ) ), GizmoView.Front );
			NearVec( Apply( m, new Vector3( 2f, 4f, 6f ) ), new Vector3( 3f, 5f, 6f ), 1e-5f, "XY plane scales X by 1.5 and Y by 1.25" );
			model.UnlockAxis();
			m = model.DragToTransform( new GizmoDrag( new Vector3( 0.3f, 0.4f, 0f ) ), GizmoView.Front );
			NearVec( Apply( m, new Vector3( 2f, 0f, 0f ) ), new Vector3( 3f, 0f, 0f ), 1e-5f, "no lock scales uniformly by 1 + drag length (1.5)" );
			model.LockAxis( GizmoAxisLock.X );
			model.SetPivot( new Vector3( 1f, 0f, 0f ) );
			m = model.DragToTransform( new GizmoDrag( new Vector3( 1f, 0f, 0f ) ), GizmoView.Front );
			NearVec( Apply( m, new Vector3( 3f, 0f, 0f ) ), new Vector3( 5f, 0f, 0f ), 1e-5f, "scale about the pivot doubles the distance from it" );
			NearVec( Apply( m, new Vector3( 1f, 2f, 0f ) ), new Vector3( 1f, 2f, 0f ), 1e-5f, "pivot point itself does not move" );
		} );

		// 14. Local scale scales along the selection's rotated axes, and a drag through the pivot clamps instead of mirroring.
		Check( "scale local axes and clamp through pivot", () =>
		{
			var orientation = Quaternion.CreateFromAxisAngle( Vector3.UnitZ, MathF.PI / 2f ); // local X = world Y
			var model = new GizmoModel();
			model.SetMode( GizmoMode.Scale );
			model.SetSpace( GizmoSpace.Local );
			model.SetSelectionOrientation( orientation );
			model.LockAxis( GizmoAxisLock.X );
			var m = model.DragToTransform( new GizmoDrag( new Vector3( 0f, 0.5f, 0f ) ), GizmoView.Front );
			NearVec( Apply( m, new Vector3( 0f, 2f, 0f ) ), new Vector3( 0f, 3f, 0f ), 1e-5f, "local X scale stretches world Y" );
			NearVec( Apply( m, new Vector3( 2f, 0f, 0f ) ), new Vector3( 2f, 0f, 0f ), 1e-5f, "world X (local -Y) is untouched" );
			NearVec( Apply( m, new Vector3( 0f, 0f, 5f ) ), new Vector3( 0f, 0f, 5f ), 1e-5f, "world Z is untouched" );
			var global = new GizmoModel();
			global.SetMode( GizmoMode.Scale );
			global.LockAxis( GizmoAxisLock.X );
			var clamped = global.DragToTransform( new GizmoDrag( new Vector3( -5f, 0f, 0f ) ), GizmoView.Front );
			NearVec( Apply( clamped, new Vector3( 2f, 0f, 0f ) ), new Vector3( 0.0002f, 0f, 0f ), 1e-6f, "drag through the pivot clamps to the minimum scale, never negative" );
		} );

		// 15. A zero drag is the identity in every mode.
		Check( "zero drag is identity in all modes", () =>
		{
			var model = new GizmoModel();
			foreach ( var mode in new[] { GizmoMode.Translate, GizmoMode.Rotate, GizmoMode.Scale } )
			{
				model.SetMode( mode );
				model.LockAxis( GizmoAxisLock.Z );
				var m = model.DragToTransform( new GizmoDrag( Vector3.Zero ), GizmoView.Front );
				NearMat( m, Matrix4x4.Identity, 1e-6f, $"zero drag in {mode} is identity" );
			}
		} );

		// 16. Invalid inputs throw instead of producing a silent wrong transform.
		Check( "invalid inputs throw", () =>
		{
			var model = new GizmoModel();
			Throws<ArgumentException>( () => model.SetPivot( new Vector3( float.NaN, 0f, 0f ) ) );
			Throws<ArgumentOutOfRangeException>( () => model.SetSnap( enabled: true, gridSize: 0.0, angleSnapDegrees: 15.0 ) );
			Throws<ArgumentOutOfRangeException>( () => model.SetSnap( enabled: true, gridSize: 16.0, angleSnapDegrees: 0.0 ) );
			Throws<ArgumentOutOfRangeException>( () => model.SetMode( (GizmoMode)99 ) );
			Throws<ArgumentOutOfRangeException>( () => model.SetSpace( (GizmoSpace)99 ) );
			Throws<ArgumentOutOfRangeException>( () => model.LockAxis( (GizmoAxisLock)99 ) );
			Throws<ArgumentException>( () => model.SetPivotFromSelection( Array.Empty<Vector3>() ) );
			Throws<ArgumentException>( () => model.SetSelectionOrientation( new Quaternion( 0f, 0f, 0f, 0f ) ) );
			Throws<ArgumentException>( () => _ = new GizmoDrag( new Vector3( float.NaN, 0f, 0f ) ) );
			Throws<ArgumentException>( () => _ = GizmoDrag.FromScreen( new Vector2( 1f, 1f ), 0f ) );
			Throws<ArgumentException>( () => _ = model.DragToTransform( new GizmoDrag( Vector3.One ), default ) );
			Throws<ArgumentException>( () => _ = GizmoView.FromCamera( new Vector3( 1f, 2f, 3f ), new Vector3( 1f, 2f, 3f ), Vector3.UnitY ) );
			Throws<ArgumentException>( () => _ = new GizmoView( Vector3.UnitX, Vector3.UnitY, Vector3.Zero ) );
		} );

		// 17. Pivot from a selection is the centroid; view/drag factories build the documented bases.
		Check( "pivot from selection centroid and view drag factories", () =>
		{
			var model = new GizmoModel();
			model.SetPivotFromSelection( new Vector3[] { new( 0f, 0f, 0f ), new( 3f, 0f, 0f ), new( 0f, 3f, 0f ) } );
			NearVec( model.Pivot, new Vector3( 1f, 1f, 0f ), 1e-6f, "centroid of the three points" );
			var view = GizmoView.FromCamera( new Vector3( 0f, 0f, 100f ), Vector3.Zero, Vector3.UnitY );
			NearVec( view.Right, Vector3.UnitX, 1e-6f, "camera right" );
			NearVec( view.Up, Vector3.UnitY, 1e-6f, "camera up" );
			NearVec( view.Forward, -Vector3.UnitZ, 1e-6f, "camera forward" );
			NearVec( GizmoView.Top.Up, -Vector3.UnitZ, 1e-6f, "top view screen-up is world -Z" );
			NearVec( GizmoView.Side.Forward, -Vector3.UnitX, 1e-6f, "side view looks down -X" );
			var drag = GizmoDrag.FromScreen( new Vector2( 100f, -50f ), 0.1f );
			NearVec( drag.ViewDelta, new Vector3( 10f, 5f, 0f ), 1e-6f, "screen drag maps to view space (Y down flips)" );
			var moved = new GizmoModel().DragToTransform( drag, GizmoView.Front );
			NearVec( moved.Translation, new Vector3( 10f, 5f, 0f ), 1e-5f, "screen drag translates in front view" );
		} );

		// 18. LockedAxisWorld reports the active-frame axis/normal; plane locks report their normal.
		Check( "locked axis world reports active frame axis and plane normal", () =>
		{
			var model = new GizmoModel();
			Require( model.LockedAxisWorld() == Vector3.Zero, "no lock reports zero" );
			model.LockAxis( GizmoAxisLock.X );
			NearVec( model.LockedAxisWorld(), Vector3.UnitX, 1e-6f, "global X" );
			model.LockAxis( GizmoAxisLock.XY );
			NearVec( model.LockedAxisWorld(), Vector3.UnitZ, 1e-6f, "XY plane normal is Z" );
			Require( model.IsPlaneLock, "XY is a plane lock" );
			model.LockAxis( GizmoAxisLock.YZ );
			NearVec( model.LockedAxisWorld(), Vector3.UnitX, 1e-6f, "YZ plane normal is X" );
			model.SetSpace( GizmoSpace.Local );
			model.SetSelectionOrientation( Quaternion.CreateFromAxisAngle( Vector3.UnitZ, MathF.PI / 2f ) );
			model.LockAxis( GizmoAxisLock.X );
			NearVec( model.LockedAxisWorld(), new Vector3( 0f, 1f, 0f ), 1e-6f, "local X is world Y after a 90-degree Z rotation" );
		} );

		// 19. DragToTransform never mutates state.
		Check( "drag to transform does not mutate state", () =>
		{
			var model = new GizmoModel();
			model.SetMode( GizmoMode.Rotate );
			model.SetSpace( GizmoSpace.Local );
			model.LockAxis( GizmoAxisLock.Z );
			model.SetPivot( new Vector3( 4f, 5f, 6f ) );
			model.SetSelectionOrientation( Quaternion.CreateFromAxisAngle( Vector3.UnitY, 0.5f ) );
			var before = model.State;
			_ = model.DragToTransform( new GizmoDrag( new Vector3( 1f, 2f, 3f ) ), GizmoView.Front );
			var after = model.State;
			Require( before.Mode == after.Mode && before.Space == after.Space && before.LockedAxis == after.LockedAxis, "mode/space/lock unchanged" );
			NearVec( after.Pivot, before.Pivot, 1e-6f, "pivot unchanged" );
			Require( before.SelectionOrientation == after.SelectionOrientation, "orientation unchanged" );
		} );

		// 20. Fixture: every drag case in drag-cases.json reproduces its expected result.
		CheckMissing( "fixture drag-cases.json", () =>
		{
			var path = FindFixture()!;
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var cases = doc.RootElement.GetProperty( "cases" );
			Require( cases.GetArrayLength() == 8, "fixture has 8 drag cases" );
			foreach ( var c in cases.EnumerateArray() )
			{
				var name = c.GetProperty( "name" ).GetString()!;
				var model = new GizmoModel();
				model.SetMode( ParseMode( c.GetProperty( "mode" ).GetString()! ) );
				model.SetSpace( ParseSpace( c.GetProperty( "space" ).GetString()! ) );
				model.LockAxis( ParseLock( c.GetProperty( "lock" ).GetString()! ) );
				model.SetPivot( ReadVec3( c.GetProperty( "pivot" ) ) );
				if ( c.GetProperty( "orientation" ).ValueKind != JsonValueKind.Null )
				{
					var o = c.GetProperty( "orientation" );
					model.SetSelectionOrientation( Quaternion.CreateFromAxisAngle(
						ReadVec3( o.GetProperty( "axis" ) ),
						o.GetProperty( "degrees" ).GetSingle() * MathF.PI / 180f ) );
				}
				model.SetSnap(
					c.GetProperty( "snap" ).GetBoolean(),
					c.GetProperty( "grid" ).GetDouble(),
					c.GetProperty( "angleSnap" ).GetDouble() );
				var view = c.GetProperty( "view" ).GetString()! switch
				{
					"front" => GizmoView.Front,
					"top" => GizmoView.Top,
					"side" => GizmoView.Side,
					_ => throw new Exception( $"case {name}: unknown view" ),
				};
				var m = model.DragToTransform( new GizmoDrag( ReadVec3( c.GetProperty( "drag" ) ) ), view );
				var expect = c.GetProperty( "expect" );
				var kind = expect.GetProperty( "kind" ).GetString()!;
				switch ( kind )
				{
					case "translation":
						NearVec( m.Translation, ReadVec3( expect.GetProperty( "value" ) ), 1e-5f, $"case {name}: translation" );
						break;
					case "rotation":
						NearMat( m, MapTransform.RotationMatrix( ReadVec3( expect.GetProperty( "axis" ) ), expect.GetProperty( "angleDegrees" ).GetSingle(), model.Pivot ), 1e-4f, $"case {name}: rotation" );
						break;
					case "scale":
						NearMat( m, MapTransform.ScaleMatrix( ReadVec3( expect.GetProperty( "factors" ) ), model.Pivot ), 1e-5f, $"case {name}: scale" );
						break;
					case "point":
						NearVec( Apply( m, ReadVec3( expect.GetProperty( "in" ) ) ), ReadVec3( expect.GetProperty( "out" ) ), 1e-5f, $"case {name}: point" );
						break;
					default:
						throw new Exception( $"case {name}: unknown expect kind {kind}" );
				}
			}
		}, () => FindFixture() is null ? "fixture drag-cases.json not found under the repo root" : null );

		static string? FindFixture()
		{
			var dir = new DirectoryInfo( AppContext.BaseDirectory );
			for ( int i = 0; i < 8 && dir != null; i++ )
			{
				var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-gizmo-pivot-tool-model", "drag-cases.json" );
				if ( File.Exists( candidate ) ) return candidate;
				dir = dir.Parent;
			}
			return null;
		}

		static GizmoMode ParseMode( string value ) => value switch
		{
			"translate" => GizmoMode.Translate,
			"rotate" => GizmoMode.Rotate,
			"scale" => GizmoMode.Scale,
			_ => throw new Exception( $"unknown mode {value}" ),
		};

		static GizmoSpace ParseSpace( string value ) => value switch
		{
			"global" => GizmoSpace.Global,
			"local" => GizmoSpace.Local,
			_ => throw new Exception( $"unknown space {value}" ),
		};

		static GizmoAxisLock ParseLock( string value ) => value switch
		{
			"None" => GizmoAxisLock.None,
			"X" => GizmoAxisLock.X,
			"Y" => GizmoAxisLock.Y,
			"Z" => GizmoAxisLock.Z,
			"XY" => GizmoAxisLock.XY,
			"YZ" => GizmoAxisLock.YZ,
			"XZ" => GizmoAxisLock.XZ,
			_ => throw new Exception( $"unknown lock {value}" ),
		};

		Vector3 ReadVec3( JsonElement element )
		{
			var values = new List<float>();
			foreach ( var item in element.EnumerateArray() ) values.Add( item.GetSingle() );
			Require( values.Count == 3, "expected a 3-component vector in the fixture" );
			return new Vector3( values[0], values[1], values[2] );
		}
	}
}
