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
		Check( "target-normal-project offsets along target normal", () =>
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
			var r = ShrinkwrapService.WrapToSurface( src, tgt, new ShrinkwrapService.Options { Mode = ShrinkwrapService.Mode.TargetNormalProject, Offset = 0.5f } );
			Require( r.Moved == 1 );
			Require( MathF.Abs( r.Mesh.Positions[ 0 ].z - 0.5f ) < 1e-3f, $"expected z 0.5, got {r.Mesh.Positions[ 0 ].z}" );
		} );

		Check( "smoothing pulls wrapped vertex toward neighbours", () =>
		{
			// Two separated planes with a gap (left z=0 over x 0..1, right z=2 over x 2..3) and a
			// source grid at z=1.5: left-block vertices wrap to z=0, right-block to z=2, so the
			// boundary vertex (index 1) has neighbours on both levels and smoothing must lift it.
			var src = new PolyMesh();
			foreach ( var p in new[] { new Vec3( 0, 0, 1.5f ), new Vec3( 0.5f, 0, 1.5f ), new Vec3( 2.5f, 0, 1.5f ), new Vec3( 0, 1, 1.5f ), new Vec3( 0.5f, 1, 1.5f ), new Vec3( 2.5f, 1, 1.5f ) } ) src.Positions.Add( p );
			src.Faces.Add( new Face( new[] { 0, 1, 4 } ) );
			src.Faces.Add( new Face( new[] { 0, 4, 3 } ) );
			src.Faces.Add( new Face( new[] { 1, 2, 5 } ) );
			src.Faces.Add( new Face( new[] { 1, 5, 4 } ) );
			var tgt = new PolyMesh();
			foreach ( var p in new[] { new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 1, 1, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 2, 0, 2 ), new Vec3( 3, 0, 2 ), new Vec3( 3, 1, 2 ), new Vec3( 2, 1, 2 ) } ) tgt.Positions.Add( p );
			tgt.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			tgt.Faces.Add( new Face( new[] { 0, 2, 3 } ) );
			tgt.Faces.Add( new Face( new[] { 4, 5, 6 } ) );
			tgt.Faces.Add( new Face( new[] { 4, 6, 7 } ) );
			var plain = ShrinkwrapService.WrapToSurface( src, tgt );
			Require( plain.Moved == 6 );
			Require( MathF.Abs( plain.Mesh.Positions[ 1 ].z ) < 1e-3f, $"unsmoothed boundary vertex should sit at z=0, got {plain.Mesh.Positions[ 1 ].z}" );
			var s1 = ShrinkwrapService.WrapToSurface( src, tgt, new ShrinkwrapService.Options { SmoothIterations = 1 } );
			Require( s1.Mesh.Positions[ 1 ].z > 0.4f && s1.Mesh.Positions[ 1 ].z < 1.1f, $"1 smoothing pass should pull boundary vertex toward neighbours, got {s1.Mesh.Positions[ 1 ].z}" );
			var s2 = ShrinkwrapService.WrapToSurface( src, tgt, new ShrinkwrapService.Options { SmoothIterations = 2 } );
			Require( s2.Mesh.Positions[ 1 ].z > s1.Mesh.Positions[ 1 ].z, "2nd smoothing pass should pull further than 1st" );
		} );

	}
}
