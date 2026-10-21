using System;
using Effigy;
using Marionette.Desktop;

internal static class EuphoriaTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "euphoria", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		Check( "com of weighted points", () =>
		{
			var pts = new[] { (new Vec3( 0, 0, 0 ), 1f), (new Vec3( 10, 0, 0 ), 1f) };
			var c = EuphoriaService.CenterOfMass( pts );
			Require( MathF.Abs( c.x - 5f ) < 1e-5f, "com should be midpoint" );
		} );

		Check( "balanced com inside support", () =>
		{
			var feet = new[] { new Vec3( -1, 0, -1 ), new Vec3( 1, 0, -1 ), new Vec3( 1, 0, 1 ), new Vec3( -1, 0, 1 ) };
			var com = new Vec3( 0, 1, 0 );
			Require( EuphoriaService.IsBalanced( com, feet ), "centered com should be balanced" );
		} );

		Check( "unbalanced com outside support", () =>
		{
			var feet = new[] { new Vec3( -1, 0, -1 ), new Vec3( 1, 0, -1 ), new Vec3( 1, 0, 1 ), new Vec3( -1, 0, 1 ) };
			var com = new Vec3( 5, 1, 0 );
			Require( !EuphoriaService.IsBalanced( com, feet ), "far com should be unbalanced" );
		} );

		Check( "foot placement shifts toward com", () =>
		{
			var com = new Vec3( 2, 0, 0 );
			var foot = new Vec3( 0, 0, 0 );
			var p = EuphoriaService.FootPlacement( com, foot, 0.5f );
			Require( p.x > 0f, "foot should shift toward com" );
		} );

		Check( "reach within arm length returns target", () =>
		{
			var shoulder = new Vec3( 0, 1, 0 );
			var target = new Vec3( 0.3f, 1, 0.2f );
			var r = EuphoriaService.Reach( shoulder, target, 1f, out var lean );
			Require( (r - target).Length < 1e-5f, "should reach target" );
		} );

		Check( "reach beyond arm length clamps and leans", () =>
		{
			var shoulder = new Vec3( 0, 1, 0 );
			var target = new Vec3( 5, 1, 0 );
			var r = EuphoriaService.Reach( shoulder, target, 1f, out var lean );
			Require( (r - shoulder).Length <= 1.01f, "clamped to arm length" );
		} );

		Check( "stumble impulse scales with stiffness", () =>
		{
			var push = new Vec3( 5, 0, 0 );
			var low = EuphoriaService.StumbleImpulse( push, 0f );
			var high = EuphoriaService.StumbleImpulse( push, 1f );
			Require( low.Length > high.Length, "lower stiffness => larger impulse" );
		} );

		Check( "recover step moves com toward support", () =>
		{
			var com = new Vec3( 2, 0, 0 );
			var support = new Vec3( 0, 0, 0 );
			var r = EuphoriaService.RecoverStep( com, support, 1f, 0.1f );
			Require( r.x < com.x, "com should move toward support" );
		} );

		Check( "stiffness interpolation", () =>
		{
			float v = EuphoriaService.ApplyStiffness( 0f, 10f, 1f, 0.5f );
			Require( MathF.Abs( v - 5f ) < 1e-5f, "stiffness 1, dt 0.5 => half" );
		} );

		Check( "pose blend endpoints", () =>
		{
			var a = new Vec3( 0, 0, 0 );
			var b = new Vec3( 10, 0, 0 );
			Require( (EuphoriaService.BlendPose( a, b, 0f ) - a).Length < 1e-5f, "weight 0 => authored" );
			Require( (EuphoriaService.BlendPose( a, b, 1f ) - b).Length < 1e-5f, "weight 1 => procedural" );
		} );

		Check( "determinism", () =>
		{
			Require( EuphoriaService.DeterministicHash( 42, 7 ) == EuphoriaService.DeterministicHash( 42, 7 ), "deterministic" );
		} );
	}
}
