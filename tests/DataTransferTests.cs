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

		Check( "transfers skin weights by barycentric", () =>
		{
			// Barycentric transfer needs a source face to interpolate over (v4F rewrite); a bare
			// vertex has no triangle, so the old nearest-vertex fixture can never transfer.
			var src = new PolyMesh();
			src.Positions.Add( new Vec3( 0, 0, 0 ) );
			src.Positions.Add( new Vec3( 1, 0, 0 ) );
			src.Positions.Add( new Vec3( 0, 1, 0 ) );
			src.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			var sw = new SkinWeights( 3 );
			for ( int i = 0; i < 3; i++ ) sw[ i ] = new[] { new BoneWeight( 7, 1f ) };
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

		Check( "transfers UVs by barycentric", () =>
		{
			// Corner target gets its corner UV exactly; edge-midpoint target gets the blend of
			// its two corners. The old +0.1-offset fixture sat on edges, so barycentric (correctly)
			// returned blends, not corner values.
			var src = new PolyMesh();
			src.Positions.Add( new Vec3( 0, 0, 0 ) );
			src.Positions.Add( new Vec3( 1, 0, 0 ) );
			src.Positions.Add( new Vec3( 0, 1, 0 ) );
			var sf = new Face( new[] { 0, 1, 2 } );
			sf.UVs = new[] { new Vec2( 0.1f, 0.2f ), new Vec2( 0.3f, 0.4f ), new Vec2( 0.5f, 0.6f ) };
			src.Faces.Add( sf );

			var tgt = new PolyMesh();
			tgt.Positions.Add( new Vec3( 0, 0, 0 ) );
			tgt.Positions.Add( new Vec3( 0.5f, 0, 0 ) );
			tgt.Positions.Add( new Vec3( 0, 1, 0 ) );
			var tf = new Face( new[] { 0, 1, 2 } );
			tf.UVs = new[] { new Vec2( 0, 0 ), new Vec2( 0, 0 ), new Vec2( 0, 0 ) };
			tgt.Faces.Add( tf );

			var r = DataTransferService.Transfer( src, tgt, new DataTransferService.Options { Channels = new[] { DataTransferService.Channel.UVs } } );
			Require( MathF.Abs( r.Mesh.Faces[0].UVs[0].x - 0.1f ) < 1e-5f );
			Require( MathF.Abs( r.Mesh.Faces[0].UVs[1].x - 0.2f ) < 1e-5f, $"edge midpoint UV should blend corners (0.1+0.3)/2=0.2, got {r.Mesh.Faces[0].UVs[1].x}" );
		} );
		Check( "barycentric midpoint colour is the average not one endpoint", () =>
		{
			var src = new PolyMesh();
			src.Positions.Add( new Vec3( 0, 0, 0 ) );
			src.Positions.Add( new Vec3( 2, 0, 0 ) );
			src.Positions.Add( new Vec3( 0, 2, 0 ) );
			src.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			src.VertexColors = new[] { new Vec4( 1, 0, 0, 1 ), new Vec4( 0, 1, 0, 1 ), new Vec4( 0, 0, 1, 1 ) };
			var tgt = new PolyMesh();
			tgt.Positions.Add( new Vec3( 2f / 3f, 2f / 3f, 0 ) );
			var r = DataTransferService.Transfer( src, tgt, new DataTransferService.Options { Channels = new[] { DataTransferService.Channel.VertexColors } } );
			Require( r.TransferredVerts == 1 );
			var c = r.Mesh.VertexColors[ 0 ];
			Require( MathF.Abs( c.x - 1f / 3f ) < 0.05f && MathF.Abs( c.y - 1f / 3f ) < 0.05f && MathF.Abs( c.z - 1f / 3f ) < 0.05f, $"centroid colour should be ~(1/3,1/3,1/3), got ({c.x},{c.y},{c.z})" );
		} );

		Check( "transfers UV2 by barycentric interpolation", () =>
		{
			var src = new PolyMesh();
			src.Positions.Add( new Vec3( 0, 0, 0 ) );
			src.Positions.Add( new Vec3( 2, 0, 0 ) );
			src.Positions.Add( new Vec3( 0, 2, 0 ) );
			src.Faces.Add( new Face( new[] { 0, 1, 2 } ) );
			src.SetUV2Vertex( 0, new Vec2( 0, 0 ) );
			src.SetUV2Vertex( 1, new Vec2( 2, 0 ) );
			src.SetUV2Vertex( 2, new Vec2( 0, 2 ) );
			var tgt = new PolyMesh();
			tgt.Positions.Add( new Vec3( 2f / 3f, 2f / 3f, 0 ) );
			var r = DataTransferService.Transfer( src, tgt, new DataTransferService.Options { Channels = new[] { DataTransferService.Channel.UV2 } } );
			Require( r.TransferredVerts == 1 );
			var uv = r.Mesh.UV2[ 0 ];
			Require( MathF.Abs( uv.x - 2f / 3f ) < 0.05f && MathF.Abs( uv.y - 2f / 3f ) < 0.05f, $"centroid UV2 should be ~(2/3,2/3), got ({uv.x},{uv.y})" );
		} );


	}
}
