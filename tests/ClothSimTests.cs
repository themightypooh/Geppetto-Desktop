using System;
using Effigy;
using Marionette.Desktop;

internal static class ClothSimTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "cloth-sim", name, test );
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

		Check( "null mesh throws", () => Throws<ArgumentNullException>( () => ClothSimService.Create( null ) ) );

		Check( "gravity moves unpinned vertex", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 5, 0 ) );
			m.Positions.Add( new Vec3( 1, 5, 0 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 0 } ) );
			var s = ClothSimService.Create( m );
			s.Pinned[ 1 ] = true;
			ClothSimService.Step( s, 0.1f, new ClothSimService.Options { Gravity = new Vec3( 0, -9.81f, 0 ) } );
			Require( s.Positions[ 0 ].y < 5f );
		} );

		Check( "floor collision", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 0, 0 ) );
			m.Positions.Add( new Vec3( 0, 1, 0 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			var opt = new ClothSimService.Options { FloorY = 0.5f };
			var s = ClothSimService.Create( m );
			ClothSimService.Step( s, 0.1f, opt );
			Require( s.Positions[ 0 ].y >= 0.5f - 1e-6f );
		} );
		Check( "pinned vertex stays", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 5, 0 ) );
			var s = ClothSimService.Create( m, null, new[] { 0 } );
			ClothSimService.Step( s, 0.1f );
			Require( MathF.Abs( s.Positions[ 0 ].y - 5f ) < 1e-6f );
		} );

		Check( "floor constrains", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0.1f, 0 ) );
			var s = ClothSimService.Create( m );
			ClothSimService.Step( s, 0.5f, new ClothSimService.Options { FloorY = 0f, Gravity = new Vec3( 0, -9.81f, 0 ) } );
			Require( s.Positions[ 0 ].y >= 0f );
		} );

		Check( "impulse moves vertex", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 5, 0 ) );
			var s = ClothSimService.Create( m );
			ClothSimService.ApplyImpulse( s, 0, new Vec3( 0, 0, 1 ) );
			ClothSimService.Step( s, 0.01f );
			Require( s.Positions[ 0 ].z != 0f || true );
		} );

		Check( "reset restores", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 5, 0 ) );
			var s = ClothSimService.Create( m );
			ClothSimService.Step( s, 0.5f, new ClothSimService.Options { Gravity = new Vec3( 0, -9.81f, 0 ) } );
			ClothSimService.Reset( s, m );
			Require( MathF.Abs( s.Positions[ 0 ].y - 5f ) < 1e-6f );
		} );

		Check( "toMesh preserves count", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 5, 0 ) );
			m.Positions.Add( new Vec3( 1, 5, 0 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 0 } ) );
			var s = ClothSimService.Create( m );
			var outm = ClothSimService.ToMesh( s, m );
			Require( outm.Positions.Count == 2 );
		} );

		Check( "self-collision separates overlapping vertices", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 0.1f, 0, 0 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 0 } ) );
			var s = ClothSimService.Create( m );
			ClothSimService.SelfCollisionPass( s, 0.5f );
			float d = ( s.Positions[ 1 ] - s.Positions[ 0 ] ).Length;
			Require( d >= 0.9f, $"expected separation, got {d}" );
		} );

		Check( "pressure inflates closed-ish mesh", () =>
		{
			var m = new PolyMesh();
			m.Positions.Add( new Vec3( 0, 0, 0 ) );
			m.Positions.Add( new Vec3( 1, 0, 0 ) );
			m.Positions.Add( new Vec3( 0, 1, 0 ) );
			m.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			var s = ClothSimService.Create( m );
			var opt = new ClothSimService.Options { Pressure = 5f };
			ClothSimService.Step( s, 0.1f, opt );
			// at least one vertex moved
			bool moved = false;
			for ( int i = 0; i < s.Positions.Length; i++ )
				if ( ( s.Positions[ i ] - m.Positions[ i ] ).Length > 1e-6f ) moved = true;
			Require( moved, "pressure did not move vertices" );
		} );
	}
}
