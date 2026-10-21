using System;
using Effigy;
using Marionette.Desktop;

internal static class AdvancedIkTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "advanced-ik", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		Check( "foot plants on ground plane", () =>
		{
			var foot = new Vec3( 1, 2, 3 );
			var planted = AdvancedIkService.PlantFoot( foot, new Vec3( 0, 1, 0 ), 0f );
			Require( MathF.Abs( planted.y ) < 1e-5f, "foot should be on y=0 plane" );
			Require( MathF.Abs( planted.x - 1f ) < 1e-5f && MathF.Abs( planted.z - 3f ) < 1e-5f, "x/z unchanged" );
		} );

		Check( "look-at direction and yaw/pitch", () =>
		{
			var eye = new Vec3( 0, 0, 0 );
			var target = new Vec3( 0, 0, 5 );
			var dir = AdvancedIkService.LookDirection( eye, target );
			Require( MathF.Abs( dir.z - 1f ) < 1e-5f, "direction should be +Z" );
			Require( MathF.Abs( AdvancedIkService.LookYaw( eye, target ) ) < 1e-5f, "yaw should be 0" );
			Require( MathF.Abs( AdvancedIkService.LookPitch( eye, target ) ) < 1e-5f, "pitch should be 0" );
		} );

		Check( "angle clamp", () =>
		{
			Require( MathF.Abs( AdvancedIkService.ClampAngle( 5f, -1f, 1f ) - 1f ) < 1e-6f, "clamp high" );
			Require( MathF.Abs( AdvancedIkService.ClampAngle( -5f, -1f, 1f ) + 1f ) < 1e-6f, "clamp low" );
			Require( MathF.Abs( AdvancedIkService.ClampAngle( 0.5f, -1f, 1f ) - 0.5f ) < 1e-6f, "clamp passthrough" );
		} );

		Check( "cone clamp keeps within cone", () =>
		{
			var axis = new Vec3( 0, 1, 0 );
			var dir = new Vec3( 1, 0, 0 ); // 90 deg off
			var c = AdvancedIkService.ClampToCone( dir, axis, MathF.PI / 4f );
			float cos = Vec3.Dot( c, axis.Normal );
			Require( cos >= MathF.Cos( MathF.PI / 4f ) - 1e-5f, "clamped direction must be within cone" );
		} );

		Check( "fabrik reaches target", () =>
		{
			var chain = new[] { new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 2, 0, 0 ) };
			var target = new Vec3( 1.5f, 0.5f, 0f );
			var res = AdvancedIkService.Fabrik( chain, target, 20 );
			Require( (res[res.Length - 1] - target).Length < 1e-3f, "end effector should reach target" );
		} );

		Check( "fabrik stretch when unreachable", () =>
		{
			var chain = new[] { new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 2, 0, 0 ) };
			var target = new Vec3( 10, 0, 0 );
			var res = AdvancedIkService.Fabrik( chain, target, 10 );
			Require( res[res.Length - 1].x > 1.9f, "chain should stretch toward target" );
		} );

		Check( "leg solve keeps ankle at target", () =>
		{
			var hip = new Vec3( 0, 1, 0 );
			var target = new Vec3( 0.3f, 0, 0.2f );
			var (knee, ankle) = AdvancedIkService.SolveLeg( hip, target, 0.5f, 0.5f, new Vec3( 0, 0, 1 ) );
			Require( (ankle - target).Length < 1e-5f, "ankle should land on target" );
		} );

		Check( "blend targets weighted", () =>
		{
			var t = new[] { (new Vec3( 0, 0, 0 ), 1f), (new Vec3( 10, 0, 0 ), 1f) };
			var b = AdvancedIkService.BlendTargets( t );
			Require( MathF.Abs( b.x - 5f ) < 1e-5f, "blend should average" );
		} );

		Check( "twist rotates vector", () =>
		{
			var v = new Vec3( 1, 0, 0 );
			var axis = new Vec3( 0, 1, 0 );
			var r = AdvancedIkService.Twist( v, axis, MathF.PI / 2f );
			Require( MathF.Abs( r.z + 1f ) < 1e-5f, "twist 90 should move to -Z" );
		} );

		Check( "invalid fabrik throws", () =>
		{
			try { AdvancedIkService.Fabrik( new[] { new Vec3( 0, 0, 0 ) }, new Vec3( 1, 0, 0 ) ); throw new Exception( "no throw" ); }
			catch ( ArgumentException ) { }
		} );
	}
}
