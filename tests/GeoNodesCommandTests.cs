using System;
using System.Linq;
using Effigy;

internal static class GeoNodesCommandTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "geonodes", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, float tol, string message )
		{
			if ( MathF.Abs( actual - expected ) > tol )
				throw new Exception( $"{message}: expected {expected}, got {actual}" );
		}

		const string ScatterGraph = "{\"nodes\":[" +
			"{\"id\":\"ground\",\"type\":\"Grid\",\"size_x\":4,\"size_y\":4,\"vertices_x\":3,\"vertices_y\":3}," +
			"{\"id\":\"pts\",\"type\":\"DistributePointsOnFaces\",\"geometry\":\"ground\",\"count\":6,\"seed\":1}," +
			"{\"id\":\"proto\",\"type\":\"UVSphere\",\"radius\":0.1,\"segments\":6,\"rings\":3}," +
			"{\"id\":\"scat\",\"type\":\"InstanceOnPoints\",\"geometry\":\"pts\",\"inputs\":{\"prototype\":\"proto\"}}" +
			"]}";

		const string RealizedScatterGraph = "{\"nodes\":[" +
			"{\"id\":\"ground\",\"type\":\"Grid\",\"size_x\":4,\"size_y\":4,\"vertices_x\":3,\"vertices_y\":3}," +
			"{\"id\":\"pts\",\"type\":\"DistributePointsOnFaces\",\"geometry\":\"ground\",\"count\":6,\"seed\":1}," +
			"{\"id\":\"proto\",\"type\":\"UVSphere\",\"radius\":0.1,\"segments\":6,\"rings\":3}," +
			"{\"id\":\"scat\",\"type\":\"InstanceOnPoints\",\"geometry\":\"pts\",\"inputs\":{\"prototype\":\"proto\"}}," +
			"{\"id\":\"real\",\"type\":\"RealizeInstances\",\"geometry\":\"scat\"}" +
			"]}";

		Check( "cmd: grid graph evaluates to the expected mesh", () =>
		{
			var r = GeoNodesCommands.Evaluate( "Grid size_x=4 size_y=2 vertices_x=3 vertices_y=2" );
			Require( r.Ok, "grid graph refused: " + r.Error );
			Require( r.HasMesh, "grid graph produced no mesh" );
			Require( !r.HasInstances, "grid graph should have no instances" );
			Require( r.PointCount == 6, $"expected 6 points, got {r.PointCount}" );
			Require( r.FaceCount == 2, $"expected 2 faces, got {r.FaceCount}" );
			Require( r.HasBounds, "no bounds reported" );
			Near( r.MinX, -2f, 1e-5f, "min x" );
			Near( r.MaxX, 2f, 1e-5f, "max x" );
			Near( r.MinY, -1f, 1e-5f, "min y" );
			Near( r.MaxY, 1f, 1e-5f, "max y" );
			Near( r.MinZ, 0f, 1e-5f, "min z" );
			Near( r.MaxZ, 0f, 1e-5f, "max z" );
			Require( r.RealizedPointCount == -1, "realized counts should be -1 when not asked" );
		} );

		Check( "cmd: realizing a mesh-only graph returns the mesh counts", () =>
		{
			var r = GeoNodesCommands.Evaluate( "Grid size_x=4 size_y=2 vertices_x=3 vertices_y=2", true );
			Require( r.Ok, "grid graph refused: " + r.Error );
			Require( r.RealizedPointCount == 6, $"expected 6 realized points, got {r.RealizedPointCount}" );
			Require( r.RealizedFaceCount == 2, $"expected 2 realized faces, got {r.RealizedFaceCount}" );
		} );

		Check( "cmd: scatter graph instances the right count at the right points", () =>
		{
			var r = GeoNodesCommands.Evaluate( ScatterGraph );
			Require( r.Ok, "scatter graph refused: " + r.Error );
			Require( r.HasInstances, "scatter graph produced no instances" );
			Require( r.InstanceCount == 6, $"expected 6 instances, got {r.InstanceCount}" );
			Require( r.InstanceOrigins.Length == 6, "instance origins missing" );
			foreach ( var o in r.InstanceOrigins )
			{
				Near( o.z, 0f, 1e-4f, "instance not on the ground plane" );
				Require( MathF.Abs( o.x ) <= 2f && MathF.Abs( o.y ) <= 2f,
					$"instance outside the 4x4 ground: ({o.x}, {o.y}, {o.z})" );
			}
		} );

		Check( "cmd: realized scatter bakes one sphere per point", () =>
		{
			var r = GeoNodesCommands.Evaluate( RealizedScatterGraph, true );
			Require( r.Ok, "realized scatter refused: " + r.Error );
			Require( r.HasMesh, "realized scatter produced no mesh" );
			// 6 point-mesh vertices + 6 spheres x 20 vertices; 6 spheres x 24 faces.
			Require( r.PointCount == 126, $"expected 126 points, got {r.PointCount}" );
			Require( r.FaceCount == 144, $"expected 144 faces, got {r.FaceCount}" );
			Require( r.RealizedPointCount == 126, $"expected 126 realized points, got {r.RealizedPointCount}" );
			Require( r.RealizedFaceCount == 144, $"expected 144 realized faces, got {r.RealizedFaceCount}" );
		} );

		Check( "cmd: invalid graphs are refused honestly", () =>
		{
			var unknown = GeoNodesCommands.Evaluate( "NoSuchNode foo=1" );
			Require( !unknown.Ok, "unknown node type was not refused" );
			Require( unknown.Error.Contains( "NoSuchNode" ), "refusal does not name the bad node: " + unknown.Error );
			var empty = GeoNodesCommands.Evaluate( "" );
			Require( !empty.Ok && empty.Error.Contains( "required" ), "empty graph was not refused" );
			var badJson = GeoNodesCommands.Evaluate( "{bad json" );
			Require( !badJson.Ok, "malformed json was not refused" );
			var badRef = GeoNodesCommands.Evaluate( "{\"nodes\":[{\"id\":\"a\",\"type\":\"DistributePointsOnFaces\",\"geometry\":\"missing\"}]}" );
			Require( !badRef.Ok && badRef.Error.Contains( "missing" ), "dangling geometry ref was not refused: " + badRef.Error );
		} );

		Check( "cmd: node types list the scatter ops", () =>
		{
			var types = GeoNodesCommands.NodeTypes;
			foreach ( var t in new[] { "Grid", "UVSphere", "DistributePointsOnFaces", "InstanceOnPoints", "RealizeInstances" } )
				Require( types.Contains( t ), $"node types are missing {t}" );
		} );
	}
}
