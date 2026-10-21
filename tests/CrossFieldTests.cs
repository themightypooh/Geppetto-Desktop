using System;
using Effigy;
using Marionette.Desktop;

internal static class CrossFieldTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "cross-field", name, test );
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

		Check( "null mesh throws", () => Throws<ArgumentNullException>( () => CrossFieldService.Compute( null ) ) );
		Check( "compute produces frames", () =>
		{
			var m = SdfShapes.Sphere( 1f, 8 );
			var f = CrossFieldService.Compute( m );
			Require( f.Normal.Length == m.Positions.Count );
			Require( f.Tangent.Length == m.Positions.Count );
			for ( int i = 0; i < f.Normal.Length; i++ )
			{
				Require( MathF.Abs( f.Normal[i].Length - 1f ) < 1e-5f, "normal not unit" );
				Require( MathF.Abs( Vec3.Dot( f.Normal[i], f.Tangent[i] ) ) < 1e-5f, "tangent not perpendicular" );
			}
		} );
		Check( "smooth keeps unit normals", () =>
		{
			var m = SdfShapes.Sphere( 1f, 6 );
			var f = CrossFieldService.Compute( m );
			CrossFieldService.Smooth( m, f, 2 );
			for ( int i = 0; i < f.Normal.Length; i++ )
				Require( MathF.Abs( f.Normal[i].Length - 1f ) < 1e-5f );
		} );
		Check( "vertex curvature positive on sphere", () =>
		{
			var m = SdfShapes.Sphere( 1f, 8 );
			var k = CrossFieldService.VertexCurvature( m );
			Require( k.Length == m.Positions.Count );
			// average curvature should be positive for a sphere
			float sum = 0; foreach ( var v in k ) sum += v;
			Require( sum > 0, "sphere curvature not positive" );
		} );
		Check( "optimize MIQ sets rotations", () =>
		{
			var m = SdfShapes.Sphere( 1f, 6 );
			var f = CrossFieldService.Compute( m );
			CrossFieldService.OptimizeMIQ( m, f, 2 );
			for ( int i = 0; i < f.Rotation.Length; i++ )
				Require( f.Rotation[i] >= 0 && f.Rotation[i] < 4 );
		} );
		Check( "trace streamlines returns paths", () =>
		{
			var m = SdfShapes.Sphere( 1f, 6 );
			var f = CrossFieldService.Compute( m );
			var paths = CrossFieldService.TraceStreamlines( m, f, 20, 0.05f );
			Require( paths.Count > 0 );
		} );
	}
}
