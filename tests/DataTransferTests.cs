using System;
using Effigy;
using Marionette.Desktop;

internal static class DataTransferTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "data-transfer", name, test );
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

		Check( "null source throws", () => Throws<ArgumentNullException>( () => DataTransferService.Transfer( null, new PolyMesh() ) ) );
		Check( "null target throws", () => Throws<ArgumentNullException>( () => DataTransferService.Transfer( new PolyMesh(), null ) ) );

		Check( "transfers skin weights by nearest vertex", () =>
		{
			var src = new PolyMesh();
			src.Positions.Add( new Vec3( 0, 0, 0 ) );
			var sw = new SkinWeights( 1 );
			sw[ 0 ] = new[] { new BoneWeight( 7, 1f ) };
			src.Skin = sw;

			var tgt = new PolyMesh();
			tgt.Positions.Add( new Vec3( 0.1f, 0, 0 ) );
			var r = DataTransferService.Transfer( src, tgt );
			Require( r.TransferredVerts == 1 );
			Require( r.Mesh.Skin[ 0 ][ 0 ].Bone == 7 );
		} );

		Check( "max distance skips", () =>
		{
			var src = new PolyMesh();
			src.Positions.Add( new Vec3( 0, 0, 0 ) );
			var sw = new SkinWeights( 1 );
			sw[ 0 ] = new[] { new BoneWeight( 7, 1f ) };
			src.Skin = sw;
			var tgt = new PolyMesh();
			tgt.Positions.Add( new Vec3( 100, 0, 0 ) );
			var r = DataTransferService.Transfer( src, tgt, new DataTransferService.Options { MaxDistance = 1f } );
			Require( r.SkippedVerts == 1 );
			Require( r.TransferredVerts == 0 );
		} );

		Check( "input target unchanged", () =>
		{
			var src = new PolyMesh();
			src.Positions.Add( new Vec3( 0, 0, 0 ) );
			var sw = new SkinWeights( 1 );
			sw[ 0 ] = new[] { new BoneWeight( 7, 1f ) };
			src.Skin = sw;
			var tgt = new PolyMesh();
			tgt.Positions.Add( new Vec3( 0.1f, 0, 0 ) );
			DataTransferService.Transfer( src, tgt );
			Require( tgt.Skin is null );
		} );

		Check( "transfers UVs by nearest vertex", () =>
		{
			var src = new PolyMesh();
			src.Positions.Add( new Vec3( 0, 0, 0 ) );
			src.Positions.Add( new Vec3( 1, 0, 0 ) );
			src.Positions.Add( new Vec3( 0, 1, 0 ) );
			var sf = new Face( new[] { 0, 1, 2 } );
			sf.UVs = new[] { new Vec2( 0.1f, 0.2f ), new Vec2( 0.3f, 0.4f ), new Vec2( 0.5f, 0.6f ) };
			src.Faces.Add( sf );

			var tgt = new PolyMesh();
			tgt.Positions.Add( new Vec3( 0.1f, 0, 0 ) );
			tgt.Positions.Add( new Vec3( 1.1f, 0, 0 ) );
			tgt.Positions.Add( new Vec3( 0.1f, 1.1f, 0 ) );
			var tf = new Face( new[] { 0, 1, 2 } );
			tf.UVs = new[] { new Vec2( 0, 0 ), new Vec2( 0, 0 ), new Vec2( 0, 0 ) };
			tgt.Faces.Add( tf );

			var r = DataTransferService.Transfer( src, tgt, new DataTransferService.Options { Channels = new[] { DataTransferService.Channel.UVs } } );
			Require( MathF.Abs( r.Mesh.Faces[0].UVs[0].x - 0.1f ) < 1e-5f );
		} );
	}
}
