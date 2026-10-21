#nullable enable
using System.Diagnostics;
using Effigy;
using Marionette.Desktop;

internal static class AutoWeightTests
{
	/// <summary>Closed, consistently wound capped cylinder along +Z from 0 to <paramref name="height"/>,
	/// one vertex ring per unit of height, centre vertex on each cap.</summary>
	internal static PolyMesh Cylinder( float height = 20f, float radius = 2f, int segments = 16, float xOffset = 0f )
	{
		var mesh = new PolyMesh();
		AppendCylinder( mesh, height, radius, segments, xOffset );
		return mesh;
	}

	internal static void AppendCylinder( PolyMesh mesh, float height, float radius, int segments, float xOffset )
	{
		int rings = (int)MathF.Round( height ) + 1;
		int start = mesh.VertexCount;
		for ( int i = 0; i < rings; i++ )
			for ( int j = 0; j < segments; j++ )
			{
				float a = MathF.Tau * j / segments;
				mesh.AddVertex( new Vec3( xOffset + radius * MathF.Cos( a ), radius * MathF.Sin( a ), height * i / (rings - 1) ) );
			}
		int bottom = mesh.AddVertex( new Vec3( xOffset, 0, 0 ) );
		int top = mesh.AddVertex( new Vec3( xOffset, 0, height ) );
		int V( int i, int j ) => start + i * segments + ((j % segments) + segments) % segments;
		for ( int i = 0; i < rings - 1; i++ )
			for ( int j = 0; j < segments; j++ )
				mesh.AddFace( new[] { V( i, j ), V( i, j + 1 ), V( i + 1, j + 1 ), V( i + 1, j ) } );
		for ( int j = 0; j < segments; j++ )
		{
			mesh.AddFace( new[] { bottom, V( 0, j + 1 ), V( 0, j ) } );
			mesh.AddFace( new[] { top, V( rings - 1, j ), V( rings - 1, j + 1 ) } );
		}
	}

	/// <summary>Closed axis-aligned box centred at <paramref name="c"/>, outward winding.</summary>
	internal static void AppendBox( PolyMesh mesh, Vec3 c, float h )
	{
		int s = mesh.VertexCount;
		for ( int i = 0; i < 8; i++ )
			mesh.AddVertex( new Vec3( c.x + ((i & 1) != 0 ? h : -h), c.y + ((i & 2) != 0 ? h : -h), c.z + ((i & 4) != 0 ? h : -h) ) );
		int[][] faces =
		{
			new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, // -z, +z
			new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, // -y, +y
			new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 }, // -x, +x
		};
		foreach ( var f in faces ) mesh.AddFace( f.Select( i => s + i ).ToArray() );
	}

	/// <summary>A chain of <paramref name="count"/> bones up the Z axis, each <paramref name="length"/> long, starting at z = start.</summary>
	internal static Skeleton Chain( int count, float length, float start = 1f )
	{
		var rig = new Skeleton();
		for ( int b = 0; b < count; b++ )
			rig.AddBoneFromPoints( $"bone_{(char)('a' + b)}", b - 1, new Vec3( 0, 0, start + b * length ), new Vec3( 0, 0, start + (b + 1) * length ) );
		return rig;
	}

	static float WeightOf( BoneWeight[] w, int bone ) => w.Where( x => x.Bone == bone ).Sum( x => x.Weight );

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "auto-weight", name, test );
		void Require( bool condition, string message ) { if ( !condition ) throw new Exception( message ); }
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, got silent success" );
		}
		void RequireValid( SkinWeights w, PolyMesh mesh, Skeleton rig )
		{
			var problems = w.Validate( mesh.VertexCount, rig.Count );
			Require( problems.Count == 0, "invalid weights: " + string.Join( "; ", problems.Take( 5 ) ) );
			for ( int v = 0; v < w.Count; v++ )
				Require( MathF.Abs( w[v].Sum( x => x.Weight ) - 1f ) < 1e-4f, $"vertex {v} sums to {w[v].Sum( x => x.Weight )}" );
		}

		// bone_a: z 1..10, bone_b: z 10..19 inside a radius-2 cylinder from z 0..20.
		Check( "two-bone cylinder: each bone dominant near itself", () =>
		{
			var mesh = Cylinder();
			var rig = Chain( 2, 9f );
			var result = AutoWeightService.Compute( mesh, rig );
			Require( result.FullyHeat, "expected heat on every vertex: " + result );
			Require( result.HeatVertices == mesh.VertexCount, $"heat vertices {result.HeatVertices} of {mesh.VertexCount}" );
			for ( int v = 0; v < mesh.VertexCount; v++ )
			{
				float z = mesh.Positions[v].z;
				float a = WeightOf( result.Weights[v], 0 ), b = WeightOf( result.Weights[v], 1 );
				if ( z <= 5f ) Require( a > 0.9f, $"vertex {v} at z={z} has bone_a {a}" );
				if ( z >= 15f ) Require( b > 0.9f, $"vertex {v} at z={z} has bone_b {b}" );
			}
			Console.WriteLine( "  " + result );
		} );

		Check( "two-bone cylinder: smooth monotone transition at joint", () =>
		{
			var mesh = Cylinder();
			var rig = Chain( 2, 9f );
			var result = AutoWeightService.Compute( mesh, rig );
			// One column of side vertices (segment 0), bottom to top.
			var column = Enumerable.Range( 0, 21 ).Select( i => i * 16 ).ToArray();
			float previous = -1f, maxJump = 0f;
			foreach ( int v in column )
			{
				float b = WeightOf( result.Weights[v], 1 );
				Require( b >= previous - 0.02f, $"bone_b weight not monotone at z={mesh.Positions[v].z}: {b} after {previous}" );
				if ( previous >= 0f ) maxJump = MathF.Max( maxJump, b - previous );
				previous = b;
			}
			float mid = WeightOf( result.Weights[10 * 16], 1 );
			Require( mid > 0.2f && mid < 0.8f, $"joint vertex (z=10) bone_b weight {mid} is not a blend" );
			Require( maxJump < 0.5f, $"transition jumps {maxJump} between adjacent rings; not smooth" );
			Console.WriteLine( $"  joint weight {mid:0.###}, max ring-to-ring step {maxJump:0.###}" );
		} );

		Check( "weights sum to 1 and validate", () =>
		{
			var mesh = Cylinder();
			var rig = Chain( 2, 9f );
			var result = AutoWeightService.Compute( mesh, rig );
			Require( result.Problems.Count == 0, "problems: " + string.Join( "; ", result.Problems ) );
			RequireValid( result.Weights, mesh, rig );
		} );

		Check( "max-influence cap respected and renormalised", () =>
		{
			var mesh = Cylinder( 40f );
			var rig = Chain( 5, 7.6f );
			var uncapped = AutoWeightService.Compute( mesh, rig, new AutoWeightSettings { MaxInfluences = 5, MinWeight = 0f, SmoothPasses = 3 } );
			Require( uncapped.MaxInfluencesUsed > 2, $"fixture should need more than 2 influences somewhere, got {uncapped.MaxInfluencesUsed}" );
			foreach ( int cap in new[] { 1, 2 } )
			{
				var result = AutoWeightService.Compute( mesh, rig, new AutoWeightSettings { MaxInfluences = cap, MinWeight = 0f, SmoothPasses = 3 } );
				Require( result.MaxInfluencesUsed <= cap, $"cap {cap}: found {result.MaxInfluencesUsed}" );
				Require( result.Weights.Vertices.All( w => w.Length <= cap ), $"cap {cap} violated" );
				RequireValid( result.Weights, mesh, rig );
			}
		} );

		Check( "disconnected island falls back and is reported", () =>
		{
			var mesh = Cylinder();
			int cylinderVerts = mesh.VertexCount;
			AppendBox( mesh, new Vec3( 10, 0, 15 ), 1f ); // floating outside the closed cylinder: sees no bone
			var rig = Chain( 2, 9f );
			var result = AutoWeightService.Compute( mesh, rig );
			Require( result.KernelReport.Fallback is not null, "fallback must be reported: " + result );
			Require( result.FallbackVertices == 8, $"expected the 8 island vertices to fall back, got {result.FallbackVertices}: {result}" );
			Require( result.HeatVertices == cylinderVerts, $"cylinder should still be heat-weighted: {result.HeatVertices} of {cylinderVerts}" );
			Require( !result.FullyHeat, "FullyHeat must be false with a fallback" );
			Require( result.Notes.Any( n => n.StartsWith( "fallback:" ) ), "notes must carry the fallback reason" );
			RequireValid( result.Weights, mesh, rig );
			// The island is nearer bone_b (z 15) than bone_a.
			for ( int v = cylinderVerts; v < mesh.VertexCount; v++ )
				Require( WeightOf( result.Weights[v], 1 ) > 0.5f, $"island vertex {v} not dominated by nearest bone" );
			// The cylinder's answer is unchanged by the island's presence.
			var alone = AutoWeightService.Compute( Cylinder(), rig );
			for ( int v = 0; v < cylinderVerts; v++ )
				Require( MathF.Abs( WeightOf( alone.Weights[v], 1 ) - WeightOf( result.Weights[v], 1 ) ) < 1e-3f, $"cylinder vertex {v} changed by the island" );
			Console.WriteLine( "  " + result );
		} );

		Check( "refine prunes min weight and renormalises", () =>
		{
			var pruned = AutoWeightService.PruneOne( new[] { new BoneWeight( 0, 0.5f ), new BoneWeight( 1, 0.3f ), new BoneWeight( 2, 0.15f ), new BoneWeight( 3, 0.05f ) }, 2, 0.01f );
			Require( pruned.Length == 2 && pruned[0].Bone == 0 && pruned[1].Bone == 1, "kept the wrong bones" );
			Require( MathF.Abs( pruned[0].Weight - 0.625f ) < 1e-5f && MathF.Abs( pruned[1].Weight - 0.375f ) < 1e-5f, "not renormalised" );
			var tiny = AutoWeightService.PruneOne( new[] { new BoneWeight( 4, 0.995f ), new BoneWeight( 5, 0.005f ) }, 4, 0.01f );
			Require( tiny.Length == 1 && tiny[0].Bone == 4 && tiny[0].Weight == 1f, "min weight not dropped" );
			Require( AutoWeightService.PruneOne( null, 4, 0f ).Length == 0, "null → empty" );
			var dup = AutoWeightService.PruneOne( new[] { new BoneWeight( 1, 0.25f ), new BoneWeight( 1, 0.25f ), new BoneWeight( 0, 0.5f ) }, 4, 0f );
			Require( dup.Length == 2 && dup.All( w => MathF.Abs( w.Weight - 0.5f ) < 1e-6f ), "duplicate bones not merged" );
		} );

		Check( "apply is one undoable command restoring exact prior skin", () =>
		{
			var mesh = Cylinder();
			var rig = Chain( 2, 9f );
			var history = new EditHistory();
			AutoWeightService.Apply( mesh, rig, history );
			Require( mesh.IsRigged && history.UndoCount == 1, "apply must rig the mesh in one command" );
			var applied = mesh.Skin.Clone();
			history.Undo();
			Require( mesh.Skin is null, "undo must restore 'no skin'" );
			history.Redo();
			for ( int v = 0; v < mesh.VertexCount; v++ )
				Require( mesh.Skin![v].SequenceEqual( applied[v] ), $"redo differs at vertex {v}" );

			// Over an existing skin, the same object is updated and undo restores exact values.
			var mesh2 = Cylinder();
			mesh2.Skin = SkinWeights.AllTo( mesh2.VertexCount, 1 );
			var reference = mesh2.Skin;
			var history2 = new EditHistory();
			AutoWeightService.Apply( mesh2, rig, history2 );
			Require( ReferenceEquals( reference, mesh2.Skin ), "skin object must be updated in place" );
			history2.Undo();
			Require( mesh2.Skin.Vertices.All( w => w.Length == 1 && w[0].Bone == 1 && w[0].Weight == 1f ), "undo must restore prior weights exactly" );
		} );

		Check( "export: FBX round trip and DMX carry the auto weights", () =>
		{
			var mesh = Cylinder();
			var rig = Chain( 2, 9f );
			var result = AutoWeightService.Apply( mesh, rig, null );
			string dir = Path.Combine( settings.WorkDirectory, "auto_weight_export" );
			Directory.CreateDirectory( dir );

			string fbx = FbxExportAdapter.ExportBinary( mesh, Path.Combine( dir, "cyl.fbx" ), rig );
			var import = FbxExportAdapter.ValidateFbxRoundTrip( fbx );
			Require( import.Skeleton is { Count: 2 }, "FBX skeleton missing" );
			Require( import.Weights.Count > 0 && import.Weights[0] is not null, "FBX carries no weights" );
			var skin = FbxImport.Resolve( import.Weights[0], import.Skeleton, null, out int unweighted );
			Require( unweighted == 0, $"{unweighted} unweighted vertices after FBX round trip" );
			Require( skin.Count == mesh.VertexCount, $"FBX vertex count {skin.Count} vs {mesh.VertexCount}" );
			int boneA = import.Skeleton.IndexOf( "bone_a" ), boneB = import.Skeleton.IndexOf( "bone_b" );
			float worst = 0f;
			for ( int v = 0; v < mesh.VertexCount; v++ )
			{
				worst = MathF.Max( worst, MathF.Abs( WeightOf( skin[v], boneA ) - WeightOf( mesh.Skin[v], 0 ) ) );
				worst = MathF.Max( worst, MathF.Abs( WeightOf( skin[v], boneB ) - WeightOf( mesh.Skin[v], 1 ) ) );
			}
			Require( worst < 1e-3f, $"FBX weights differ by up to {worst}" );

			string dmx = DmxExportAdapter.ExportMesh( mesh, Path.Combine( dir, "cyl.dmx" ), rig );
			string text = File.ReadAllText( dmx );
			Require( text.Contains( "blendweights$0" ) && text.Contains( "blendindices$0" ), "DMX lacks blend weights/indices" );
			Require( text.Contains( "bone_a" ) && text.Contains( "bone_b" ), "DMX lacks the bones" );
			Console.WriteLine( $"  FBX max weight error {worst:G3}; {result.Milliseconds:0.#} ms solve" );
		} );

		Check( "invalid inputs are rejected", () =>
		{
			var mesh = Cylinder();
			var rig = Chain( 2, 9f );
			Throws<ArgumentNullException>( () => AutoWeightService.Compute( null!, rig ), "null mesh" );
			Throws<ArgumentNullException>( () => AutoWeightService.Compute( mesh, null! ), "null skeleton" );
			Throws<InvalidOperationException>( () => AutoWeightService.Compute( mesh, new Skeleton() ), "empty skeleton" );
			Throws<ArgumentException>( () => AutoWeightService.Compute( new PolyMesh(), rig ), "empty mesh" );
			var loose = new PolyMesh(); loose.AddVertex( new Vec3( 0, 0, 0 ) );
			Throws<ArgumentException>( () => AutoWeightService.Compute( loose, rig ), "no faces" );
			var nan = Cylinder(); nan.Positions[3] = new Vec3( float.NaN, 0, 0 );
			Throws<ArgumentException>( () => AutoWeightService.Compute( nan, rig ), "NaN vertex" );
			Throws<ArgumentOutOfRangeException>( () => AutoWeightService.Compute( mesh, rig, new AutoWeightSettings { MaxInfluences = 0 } ), "zero influences" );
			Throws<ArgumentOutOfRangeException>( () => AutoWeightService.Compute( mesh, rig, new AutoWeightSettings { SmoothStrength = 2f } ), "strength > 1" );
			Throws<ArgumentOutOfRangeException>( () => AutoWeightService.Compute( mesh, rig, new AutoWeightSettings { MinWeight = 1f } ), "min weight 1" );
			Throws<ArgumentOutOfRangeException>( () => AutoWeightService.Compute( mesh, rig, new AutoWeightSettings { SmoothPasses = -1 } ), "negative passes" );
			Throws<ArgumentException>( () => AutoWeightService.Refine( mesh, new SkinWeights( 3 ), 1, 0.5f, 4, 0f ), "refine count mismatch" );
			Require( mesh.Skin is null, "Compute must not modify the mesh" );
		} );

		Check( "timing: 4-bone 32-segment cylinder solve", () =>
		{
			var mesh = Cylinder( 40f, 2f, 32 );
			var rig = Chain( 4, 9.5f );
			var clock = Stopwatch.StartNew();
			var result = AutoWeightService.Compute( mesh, rig );
			Console.WriteLine( $"  {mesh.VertexCount} verts, {rig.Count} bones: {clock.Elapsed.TotalMilliseconds:0.#} ms ({result})" );
			Require( result.FullyHeat, "expected full heat" );
		} );
	}
}
