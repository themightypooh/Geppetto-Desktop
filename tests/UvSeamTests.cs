using System;
using Effigy;
using Marionette.Desktop;

internal static class UvSeamTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "uv-seam", name, test );
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

		Check( "null source throws", () => Throws<ArgumentNullException>( () => UvSeamService.MarkSeams( null, Array.Empty<(int,int)>() ) ) );
		Check( "null edges throws", () => Throws<ArgumentNullException>( () => UvSeamService.MarkSeams( new PolyMesh(), null ) ) );

		Check( "seam duplicates vertex", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 1, 0 ) );
			m.Positions.Add( new Vec3( 0, 1, 0 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			m.Faces.Add( new Face( new[] { 0, 2, 3 } ) );
			var r = UvSeamService.MarkSeams( m, new[] { ( 0, 2 ) } );
			Require( r.Positions.Count > m.Positions.Count );
		} );

		Check( "no seam no change", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 0, 0 ) );
			m.Positions.Add( new Vec3( 0, 1, 0 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			var r = UvSeamService.MarkSeams( m, Array.Empty<(int,int)>() );
			Require( r.Positions.Count == m.Positions.Count );
		} );

		Check( "input unchanged", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 1, 0 ) );
			m.Positions.Add( new Vec3( 0, 1, 0 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			m.Faces.Add( new Face( new[] { 0, 2, 3 } ) );
			int before = m.Positions.Count;
			UvSeamService.MarkSeams( m, new[] { ( 0, 2 ) } );
			Require( m.Positions.Count == before );
		} );
	}
}
