using System;
using Effigy;
using Marionette.Desktop;

internal static class KnifeTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "knife", name, test );
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

		Check( "null input throws", () => Throws<ArgumentNullException>( () => KnifeService.CutByPlane( null, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ) ) ) );
		Check( "degenerate normal throws", () =>
		{
			var m = new PolyMesh();
			Throws<ArgumentException>( () => KnifeService.CutByPlane( m, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 0 ) ) );
		} );
		Check( "non-crossing face kept", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 0, 0 ) );
			m.Positions.Add( new Vec3( 0, 1, 0 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			var r = KnifeService.CutByPlane( m, new Vec3( 0, 0, 5 ), new Vec3( 0, 0, 1 ) );
			Require( r.SplitFaces == 0 );
			Require( r.Mesh.Faces.Count == 1 );
		} );
		Check( "crossing face counted", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, -1 ) );
			m.Positions.Add( new Vec3( 1, 0, -1 ) );
			m.Positions.Add( new Vec3( 0, 0, 1 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			var r = KnifeService.CutByPlane( m, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ) );
			Require( r.SplitFaces == 1 );
		} );
	}
}
