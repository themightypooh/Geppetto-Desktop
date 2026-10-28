#nullable enable
using System;
using System.Linq;
using Effigy;

internal static class SkinBinderAutoBiharmonicTests
{
	static float WeightOf( BoneWeight[] w, int bone ) => w.Where( x => x.Bone == bone ).Sum( x => x.Weight );

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "biharmonic", name, test );
		void Require( bool condition, string message ) { if ( !condition ) throw new Exception( message ); }
		void RequireValid( SkinWeights w, PolyMesh mesh, Skeleton rig )
		{
			var problems = w.Validate( mesh.VertexCount, rig.Count );
			Require( problems.Count == 0, "invalid weights: " + string.Join( "; ", problems.Take( 5 ) ) );
			for ( int v = 0; v < w.Count; v++ )
				Require( MathF.Abs( w[v].Sum( x => x.Weight ) - 1f ) < 1e-4f, $"vertex {v} sums to {w[v].Sum( x => x.Weight )}" );
		}

		// bone_a: z 1..10, bone_b: z 10..19 inside a radius-2 cylinder from z 0..20.
		Check( "two-bone cylinder: sums to 1, validates, dominant near each bone", () =>
		{
			var mesh = AutoWeightTests.Cylinder();
			var rig = AutoWeightTests.Chain( 2, 9f );
			var weights = SkinBinder.BindBiharmonic( mesh, rig, out var report );
			Require( report.Fallback is null, "biharmonic fell back: " + report.Fallback );
			Require( report.Notes.Any( n => n.Contains( "biharmonic", StringComparison.OrdinalIgnoreCase ) ),
				"report does not say the weights are biharmonic: " + report );
			RequireValid( weights, mesh, rig );
			for ( int v = 0; v < mesh.VertexCount; v++ )
			{
				float z = mesh.Positions[v].z;
				float a = WeightOf( weights[v], 0 ), b = WeightOf( weights[v], 1 );
				if ( z <= 5f ) Require( a > 0.9f, $"vertex {v} at z={z} has bone_a {a}" );
				if ( z >= 15f ) Require( b > 0.9f, $"vertex {v} at z={z} has bone_b {b}" );
			}
			Console.WriteLine( "  " + report );
		} );

		Check( "two-bone cylinder: smooth monotone transition at joint", () =>
		{
			var mesh = AutoWeightTests.Cylinder();
			var rig = AutoWeightTests.Chain( 2, 9f );
			var weights = SkinBinder.BindBiharmonic( mesh, rig, out _ );
			// One column of side vertices (segment 0), bottom to top.
			var column = Enumerable.Range( 0, 21 ).Select( i => i * 16 ).ToArray();
			float previous = -1f, maxJump = 0f, maxSecond = 0f, prevJump = 0f;
			bool first = true;
			foreach ( int v in column )
			{
				float b = WeightOf( weights[v], 1 );
				Require( b >= previous - 0.02f, $"bone_b weight not monotone at z={mesh.Positions[v].z}: {b} after {previous}" );
				if ( previous >= 0f )
				{
					float jump = b - previous;
					maxJump = MathF.Max( maxJump, jump );
					if ( !first ) maxSecond = MathF.Max( maxSecond, MathF.Abs( jump - prevJump ) );
					prevJump = jump;
					first = false;
				}
				previous = b;
			}
			float mid = WeightOf( weights[10 * 16], 1 );
			Require( mid > 0.2f && mid < 0.8f, $"joint vertex (z=10) bone_b weight {mid} is not a blend" );
			Require( maxJump < 0.5f, $"transition jumps {maxJump} between adjacent rings; not smooth" );
			// Biharmonic, not just smooth: curvature (second difference) stays within the
			// slope scale — the field does not zigzag between rings.
			Require( maxSecond <= maxJump, $"second difference {maxSecond} exceeds max step {maxJump}; field zigzags" );
			Console.WriteLine( $"  joint weight {mid:0.###}, max ring-to-ring step {maxJump:0.###}, max second difference {maxSecond:0.###}" );
		} );

		Check( "single bone: all weight on that bone", () =>
		{
			var mesh = AutoWeightTests.Cylinder();
			var rig = AutoWeightTests.Chain( 1, 9f );
			var weights = SkinBinder.BindBiharmonic( mesh, rig, out var report );
			Require( report.Fallback is null, "biharmonic fell back: " + report.Fallback );
			for ( int v = 0; v < mesh.VertexCount; v++ )
				Require( MathF.Abs( WeightOf( weights[v], 0 ) - 1f ) < 1e-4f, $"vertex {v} bone weight {WeightOf( weights[v], 0 )}" );
		} );

		Check( "deterministic: two runs identical", () =>
		{
			var mesh = AutoWeightTests.Cylinder();
			var rig = AutoWeightTests.Chain( 2, 9f );
			var first = SkinBinder.BindBiharmonic( mesh, rig, out _ );
			var second = SkinBinder.BindBiharmonic( mesh, rig, out _ );
			for ( int v = 0; v < mesh.VertexCount; v++ )
			{
				Require( first[v].Length == second[v].Length, $"vertex {v} influence count changed between runs" );
				for ( int i = 0; i < first[v].Length; i++ )
				{
					Require( first[v][i].Bone == second[v][i].Bone, $"vertex {v} bone changed between runs" );
					Require( first[v][i].Weight == second[v][i].Weight, $"vertex {v} weight changed between runs" );
				}
			}
		} );

		Check( "max-influence cap respected and renormalised", () =>
		{
			var mesh = AutoWeightTests.Cylinder( 40f );
			var rig = AutoWeightTests.Chain( 5, 7.6f );
			var weights = SkinBinder.BindBiharmonic( mesh, rig, out _, new AutoWeightOptions { MaxInfluences = 2, MinWeight = 0f } );
			Require( weights.Vertices.All( w => w.Length <= 2 ), "cap 2 violated" );
			RequireValid( weights, mesh, rig );
		} );

		Check( "own-weights range keeps its weights", () =>
		{
			var mesh = AutoWeightTests.Cylinder();
			var rig = AutoWeightTests.Chain( 2, 9f );
			var own = new SkinWeights( mesh.VertexCount );
			for ( int v = 0; v < mesh.VertexCount; v++ )
				own[v] = new[] { new BoneWeight( 1, 1f ) };
			mesh.Skin = own;
			Require( mesh.IsRigged, "mesh with Skin set should report IsRigged" );
			var ranges = new[] { new BodyRange( "b0", "body0", 0, 50, hasOwnWeights: true ) };
			var weights = SkinBinder.BindBiharmonic( mesh, rig, out _, null, ranges );
			for ( int v = 0; v < 50; v++ )
				Require( weights[v].Length == 1 && weights[v][0].Bone == 1 && weights[v][0].Weight == 1f,
					$"vertex {v} did not keep its own weights: {string.Join( ",", weights[v].Select( x => x.ToString() ) )}" );
		} );

		Check( "disconnected island takes nearest bone rigidly and is reported", () =>
		{
			var mesh = AutoWeightTests.Cylinder();
			int islandStart = mesh.VertexCount;
			AutoWeightTests.AppendBox( mesh, new Vec3( 50, 50, 10 ), 1f );
			var rig = AutoWeightTests.Chain( 2, 9f );
			var weights = SkinBinder.BindBiharmonic( mesh, rig, out var report );
			Require( report.Fallback is null, "biharmonic fell back: " + report.Fallback );
			Require( report.OtherVertices > 0, $"island not counted in OtherVertices: {report.OtherVertices}" );
			for ( int v = islandStart; v < mesh.VertexCount; v++ )
			{
				Require( weights[v].Length == 1, $"island vertex {v} is not rigid: {weights[v].Length} influences" );
				Require( MathF.Abs( weights[v][0].Weight - 1f ) < 1e-4f, $"island vertex {v} weight {weights[v][0].Weight}" );
			}
			RequireValid( weights, mesh, rig );
			Console.WriteLine( "  " + report );
		} );

		Check( "heat and voxel paths still run and validate", () =>
		{
			var mesh = AutoWeightTests.Cylinder();
			var rig = AutoWeightTests.Chain( 2, 9f );
			var heat = SkinBinder.BindHeat( mesh, rig, out var heatReport );
			RequireValid( heat, mesh, rig );
			for ( int v = 0; v < mesh.VertexCount; v++ )
			{
				float z = mesh.Positions[v].z;
				if ( z <= 5f ) Require( WeightOf( heat[v], 0 ) > 0.9f, $"heat vertex {v} at z={z} has bone_a {WeightOf( heat[v], 0 )}" );
				if ( z >= 15f ) Require( WeightOf( heat[v], 1 ) > 0.9f, $"heat vertex {v} at z={z} has bone_b {WeightOf( heat[v], 1 )}" );
			}
			Console.WriteLine( "  heat: " + heatReport );
			var voxel = SkinBinder.BindVoxel( mesh, rig, out var voxelReport );
			RequireValid( voxel, mesh, rig );
			Console.WriteLine( "  voxel: " + voxelReport );
		} );
	}
}
