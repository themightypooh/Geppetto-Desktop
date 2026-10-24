using System;
using Effigy;
using Marionette.Desktop;

internal static class ShrinkwrapTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "shrinkwrap", name, test );
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

		Check( "null source throws", () => Throws<ArgumentNullException>( () => ShrinkwrapService.Wrap( null, new PolyMesh() ) ) );
		Check( "null target throws", () => Throws<ArgumentNullException>( () => ShrinkwrapService.Wrap( new PolyMesh(), null ) ) );

		Check( "moves vertices to target", () =>
		{
			var src = new PolyMesh();
			src.Positions.Add( new Vec3( 5, 5, 5 ) );
			var tgt = new PolyMesh();
			tgt.Positions.Add( new Vec3( 1, 1, 1 ) );
			var r = ShrinkwrapService.Wrap( src, tgt );
			Require( r.Moved == 1 );
			Require( r.Mesh.Positions[ 0 ].x == 1 );
		} );

		Check( "max distance respected", () =>
		{
			var src = new PolyMesh();
			src.Positions.Add( new Vec3( 5, 5, 5 ) );
			var tgt = new PolyMesh();
			tgt.Positions.Add( new Vec3( 1, 1, 1 ) );
			var r = ShrinkwrapService.Wrap( src, tgt, new ShrinkwrapService.Options { MaxDistance = 0.1f } );
			Require( r.Moved == 0 );
		} );

		Check( "input unchanged", () =>
		{
			var src = new PolyMesh();
			src.Positions.Add( new Vec3( 5, 5, 5 ) );
			var tgt = new PolyMesh();
			tgt.Positions.Add( new Vec3( 1, 1, 1 ) );
			ShrinkwrapService.Wrap( src, tgt );
			Require( src.Positions[ 0 ].x == 5 );
		} );

		Check( "wrap-to-surface moves to closest point", () =>
		{
			var src = new PolyMesh();
			src.Positions.Add( new Vec3( 0.5f, 0.5f, 1 ) );
			var tgt = new PolyMesh();
			tgt.Positions.Add( new Vec3( 0, 0, 0 ) );
			tgt.Positions.Add( new Vec3( 1, 0, 0 ) );
			tgt.Positions.Add( new Vec3( 1, 1, 0 ) );
			tgt.Positions.Add( new Vec3( 0, 1, 0 ) );
			tgt.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			tgt.Faces.Add( new Face( new[] { 0, 2, 3 } ) );
			var r = ShrinkwrapService.WrapToSurface( src, tgt );
			Require( r.Moved == 1 );
			Require( MathF.Abs( r.Mesh.Positions[ 0 ].z ) < 1e-3f );
		} );

		Check( "offset pushes along surface normal", () =>
		{
			var src = new PolyMesh();
			src.Positions.Add( new Vec3( 0.5f, 0.5f, 1 ) );
			var tgt = new PolyMesh();
			tgt.Positions.Add( new Vec3( 0, 0, 0 ) );
			tgt.Positions.Add( new Vec3( 1, 0, 0 ) );
			tgt.Positions.Add( new Vec3( 1, 1, 0 ) );
			tgt.Positions.Add( new Vec3( 0, 1, 0 ) );
			tgt.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			tgt.Faces.Add( new Face( new[] { 0, 2, 3 } ) );
			var r = ShrinkwrapService.WrapToSurface( src, tgt, new ShrinkwrapService.Options { Offset = 0.5f } );
			Require( r.Moved == 1 );
			Require( r.Mesh.Positions[ 0 ].z > 0.4f );
		} );

		Check( "normal projection hits target", () =>
		{
			var src = new PolyMesh();
			src.Positions.Add( new Vec3( 0.5f, 0.5f, 5 ) );
			src.Positions.Add( new Vec3( 0.5f, 0.5f, 5 ) ); // duplicate for normal
			src.Faces.Add( new Face( new[] { 0, 1, 0 } ) );
			// give src a face so normal exists; rebuild properly
			var src2 = new PolyMesh();
			src2.Positions.Add( new Vec3( 0.5f, 0.5f, 5 ) );
			// add a small triangle around to define normal pointing -z
			src2.Positions.Add( new Vec3( 0.5f, 0.6f, 5 ) );
			src2.Positions.Add( new Vec3( 0.6f, 0.5f, 5 ) );
			src2.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			var tgt = new PolyMesh();
			tgt.Positions.Add( new Vec3( 0, 0, 0 ) );
			tgt.Positions.Add( new Vec3( 2, 0, 0 ) );
			tgt.Positions.Add( new Vec3( 0, 2, 0 ) );
			tgt.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			var r = ShrinkwrapService.WrapToSurface( src2, tgt, new ShrinkwrapService.Options { Mode = ShrinkwrapService.Mode.NormalProjection } );
			Require( r.Moved >= 1 );
		} );

		Check( "null source throws wrap-to-surface", () => Throws<ArgumentNullException>( () => ShrinkwrapService.WrapToSurface( null, new PolyMesh() ) ) );
	}
}
