#nullable enable
using System;
using System.Linq;
using Effigy;
using Marionette.Desktop;

internal static class SkinBinderOptionsTests
{
	const int Bone0 = 0, Bone1 = 1;

	/// <summary>Two bones end to end along X: bone0 (0,0,0)-(10,0,0), bone1 (10,0,0)-(20,0,0).</summary>
	static Skeleton TwoBones()
	{
		var rig = new Skeleton();
		rig.AddBoneFromPoints( "bone0", -1, new Vec3( 0, 0, 0 ), new Vec3( 10, 0, 0 ) );
		rig.AddBoneFromPoints( "bone1", 0, new Vec3( 10, 0, 0 ), new Vec3( 20, 0, 0 ) );
		return rig;
	}

	static PolyMesh Points( params Vec3[] points )
	{
		var mesh = new PolyMesh();
		foreach ( var p in points ) mesh.AddVertex( p );
		return mesh;
	}

	/// <summary>10x10 quad in the XY plane, corners weighted bone0/bone1/bone1/bone0.</summary>
	static PolyMesh SourceQuad()
	{
		var mesh = Points( new Vec3( 0, 0, 0 ), new Vec3( 10, 0, 0 ), new Vec3( 10, 10, 0 ), new Vec3( 0, 10, 0 ) );
		mesh.AddFace( new[] { 0, 1, 2 } );
		mesh.AddFace( new[] { 0, 2, 3 } );
		mesh.Skin = new SkinWeights();
		mesh.Skin.Vertices.Add( new[] { new BoneWeight( Bone0, 1f ) } );
		mesh.Skin.Vertices.Add( new[] { new BoneWeight( Bone1, 1f ) } );
		mesh.Skin.Vertices.Add( new[] { new BoneWeight( Bone1, 1f ) } );
		mesh.Skin.Vertices.Add( new[] { new BoneWeight( Bone0, 1f ) } );
		return mesh;
	}

	static float W( BoneWeight[] w, int bone ) => w.Where( x => x.Bone == bone ).Sum( x => x.Weight );
	static float Sum( BoneWeight[] w ) => w.Sum( x => x.Weight );

	static bool Same( BoneWeight[] a, BoneWeight[] b ) =>
		a.Length == b.Length && a.Zip( b ).All( p => p.First.Bone == p.Second.Bone && p.First.Weight.Equals( p.Second.Weight ) );

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "skinbinder-options", name, test );
		void Require( bool condition, string message ) { if ( !condition ) throw new Exception( message ); }
		void Near( float actual, float expected, string what, float tol = 1e-3f ) =>
			Require( MathF.Abs( actual - expected ) <= tol, $"{what}: expected {expected}, got {actual}" );
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, got silent success" );
		}

		Check( "sum mode is the old call bit for bit", () =>
		{
			var rig = TwoBones();
			var mesh = Points( new Vec3( 10, 3, 0 ), new Vec3( 5, 0, 0 ), new Vec3( 3, 4, 0 ), new Vec3( 15, 2, 0 ) );
			var oldWeights = SkinBinder.BindSmooth( mesh, rig );
			var newWeights = SkinBinder.BindSmooth( mesh, rig, SkinNormalizeMode.Sum );
			for ( var v = 0; v < mesh.VertexCount; v++ )
				Require( Same( oldWeights[v], newWeights[v] ), $"vertex {v} differs between old call and Sum mode" );
		} );

		Check( "strongest mode rescales so the strongest bone is 1", () =>
		{
			var rig = TwoBones();
			// (10,3,0) is equidistant (3) from both segments: raw influences equal.
			var mesh = Points( new Vec3( 10, 3, 0 ) );
			var sum = SkinBinder.BindSmooth( mesh, rig, SkinNormalizeMode.Sum );
			var strong = SkinBinder.BindSmooth( mesh, rig, SkinNormalizeMode.Strongest );
			var none = SkinBinder.BindSmooth( mesh, rig, SkinNormalizeMode.None );
			Near( W( sum[0], Bone0 ), 0.5f, "sum bone0" );
			Near( W( strong[0], Bone0 ), 1f, "strongest bone0" );
			Near( W( strong[0], Bone1 ), 1f, "strongest bone1" );
			Near( Sum( strong[0] ), 2f, "strongest sum is 2, not a partition of unity" );
			Near( W( none[0], Bone0 ), 1f / 9f, "raw bone0 is 1/d^2" );
			Near( Sum( none[0] ), 2f / 9f, "raw sum is not 1" );
			// Ratios preserved across modes.
			Near( W( strong[0], Bone0 ) / W( strong[0], Bone1 ), W( sum[0], Bone0 ) / W( sum[0], Bone1 ), "ratio preserved" );
		} );

		Check( "on-bone snap is identical in every mode", () =>
		{
			var rig = TwoBones();
			var mesh = Points( new Vec3( 5, 0, 0 ) );
			foreach ( var mode in new[] { SkinNormalizeMode.Sum, SkinNormalizeMode.Strongest, SkinNormalizeMode.None } )
			{
				var w = SkinBinder.BindSmooth( mesh, rig, mode );
				Require( w[0].Length == 1 && w[0][0].Bone == Bone0 && w[0][0].Weight == 1f, $"mode {mode} did not snap to bone0" );
			}
		} );

		Check( "normalize merges duplicates, drops negatives, orders strongest first", () =>
		{
			var weights = new SkinWeights();
			weights.Vertices.Add( new[] { new BoneWeight( Bone1, 1f ), new BoneWeight( Bone0, 2f ), new BoneWeight( Bone0, 1f ), new BoneWeight( Bone1, -5f ) } );
			weights.Vertices.Add( Array.Empty<BoneWeight>() );
			var snap = weights.Clone();
			var sum = SkinBinder.Normalize( weights, SkinNormalizeMode.Sum );
			Require( sum[0].Length == 2 && sum[0][0].Bone == Bone0, "strongest first after merge" );
			Near( W( sum[0], Bone0 ), 0.75f, "merged bone0 share" );
			Near( W( sum[0], Bone1 ), 0.25f, "bone1 share" );
			Require( sum[1].Length == 0, "empty stays empty" );
			Require( Same( weights[0], snap[0] ), "input mutated by Normalize" );
			var strong = SkinBinder.Normalize( weights, SkinNormalizeMode.Strongest );
			Near( W( strong[0], Bone0 ), 1f, "strongest bone0 is 1" );
			Near( W( strong[0], Bone1 ), 1f / 3f, "strongest bone1 keeps ratio" );
			var none = SkinBinder.Normalize( weights, SkinNormalizeMode.None );
			Near( W( none[0], Bone0 ), 3f, "none keeps raw merged value" );
			Near( W( none[0], Bone1 ), 1f, "none keeps raw bone1" );
		} );

		Check( "normalize rejects null", () =>
		{
			Throws<ArgumentNullException>( () => SkinBinder.Normalize( null! ), "null weights" );
		} );

		Check( "fill keep clones and fills nothing", () =>
		{
			var rig = TwoBones();
			var mesh = Points( new Vec3( 0, 0, 0 ), new Vec3( 20, 0, 0 ) );
			var weights = new SkinWeights();
			weights.Vertices.Add( new[] { new BoneWeight( Bone0, 1f ) } );
			weights.Vertices.Add( Array.Empty<BoneWeight>() );
			var (filled, count) = SkinBinder.FillUnweighted( mesh, weights, rig, UnweightedMode.Keep );
			Require( count == 0, $"keep filled {count}" );
			Require( filled[1].Length == 0, "keep filled an empty vertex" );
			Require( !ReferenceEquals( filled, weights ), "keep returned the input object, not a copy" );
		} );

		Check( "fill nearest bone pins each hole to its segment", () =>
		{
			var rig = TwoBones();
			var mesh = Points( new Vec3( -5, 0, 0 ), new Vec3( 15, 0, 0 ), new Vec3( 5, 0, 0 ) );
			var weights = new SkinWeights();
			weights.Vertices.Add( Array.Empty<BoneWeight>() );
			weights.Vertices.Add( Array.Empty<BoneWeight>() );
			weights.Vertices.Add( new[] { new BoneWeight( Bone0, 1f ) } );
			var (filled, count) = SkinBinder.FillUnweighted( mesh, weights, rig, UnweightedMode.NearestBone );
			Require( count == 2, $"expected 2 filled, got {count}" );
			Require( filled[0].Length == 1 && filled[0][0].Bone == Bone0, "far-left vertex is not bone0" );
			Require( filled[1].Length == 1 && filled[1][0].Bone == Bone1, "mid-right vertex is not bone1" );
			Require( filled[2].Length == 1 && filled[2][0].Bone == Bone0, "weighted vertex changed" );
		} );

		Check( "fill neighbour average flows along the surface, island stays empty", () =>
		{
			var mesh = Points( new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 2, 0, 0 ), new Vec3( 3, 0, 0 ) );
			mesh.AddFace( new[] { 0, 1, 2 } );
			mesh.AddFace( new[] { 1, 2, 3 } );
			var weights = new SkinWeights();
			weights.Vertices.Add( new[] { new BoneWeight( Bone0, 1f ) } );
			weights.Vertices.Add( Array.Empty<BoneWeight>() );
			weights.Vertices.Add( Array.Empty<BoneWeight>() );
			weights.Vertices.Add( Array.Empty<BoneWeight>() );
			var (filled, count) = SkinBinder.FillUnweighted( mesh, weights, null!, UnweightedMode.NeighbourAverage );
			Require( count == 3, $"expected 3 filled, got {count}" );
			for ( var v = 1; v <= 3; v++ )
				Require( filled[v].Length == 1 && filled[v][0].Bone == Bone0, $"vertex {v} did not fill from its neighbours" );

			var island = Points( new Vec3( 0, 0, 0 ), new Vec3( 9, 9, 9 ) );
			var islandWeights = new SkinWeights();
			islandWeights.Vertices.Add( new[] { new BoneWeight( Bone0, 1f ) } );
			islandWeights.Vertices.Add( Array.Empty<BoneWeight>() );
			var (islandFilled, islandCount) = SkinBinder.FillUnweighted( island, islandWeights, null!, UnweightedMode.NeighbourAverage );
			Require( islandCount == 0, $"island filled {islandCount}" );
			Require( islandFilled[1].Length == 0, "island vertex gained weights with no neighbours" );
		} );

		Check( "fill rejects bad input", () =>
		{
			var rig = TwoBones();
			var mesh = Points( new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ) );
			var shortWeights = new SkinWeights();
			shortWeights.Vertices.Add( new[] { new BoneWeight( Bone0, 1f ) } );
			Throws<ArgumentException>( () => SkinBinder.FillUnweighted( mesh, shortWeights, rig, UnweightedMode.NearestBone ), "count mismatch" );
			Throws<ArgumentNullException>( () => SkinBinder.FillUnweighted( mesh, new SkinWeights( 2 ), null!, UnweightedMode.NearestBone ), "nearest without skeleton" );
			Throws<ArgumentOutOfRangeException>( () => SkinBinder.FillUnweighted( mesh, new SkinWeights( 2 ), rig, (UnweightedMode)99 ), "unknown mode" );
		} );

		Check( "transfer corner blend is exact on a corner, blended inside the face", () =>
		{
			var source = SourceQuad();
			var target = Points( new Vec3( 0, 0, 0 ), new Vec3( 8, 2, 0 ) );
			var reached = SkinBinder.TransferFrom( target, source );
			Require( reached == 2, $"expected 2 reached, got {reached}" );
			Require( target.Skin[0].Length == 1 && target.Skin[0][0].Bone == Bone0, "corner vertex is not exactly bone0" );
			// (8,2,0) sits strictly inside triangle (0,1,2): distances sqrt68, sqrt8, sqrt68.
			Near( W( target.Skin[1], Bone0 ), 0.2034f, "inside bone0 share" );
			Near( W( target.Skin[1], Bone1 ), 0.7966f, "inside bone1 share" );
		} );

		Check( "transfer options default is the old call bit for bit", () =>
		{
			var source = SourceQuad();
			var a = Points( new Vec3( 8, 2, 0 ), new Vec3( 3, 7, 0 ), new Vec3( 0, 0, 0 ) );
			var b = Points( new Vec3( 8, 2, 0 ), new Vec3( 3, 7, 0 ), new Vec3( 0, 0, 0 ) );
			SkinBinder.TransferFrom( a, source );
			SkinBinder.TransferFrom( b, source, new TransferOptions() );
			for ( var v = 0; v < 3; v++ )
				Require( Same( a.Skin[v], b.Skin[v] ), $"vertex {v} differs between old call and default options" );
		} );

		Check( "transfer falloff sharpens toward the nearest corner", () =>
		{
			var source = SourceQuad();
			var soft = Points( new Vec3( 8, 2, 0 ) );
			var sharp = Points( new Vec3( 8, 2, 0 ) );
			SkinBinder.TransferFrom( soft, source, new TransferOptions { Falloff = 1f } );
			SkinBinder.TransferFrom( sharp, source, new TransferOptions { Falloff = 4f } );
			Require( W( sharp.Skin[0], Bone1 ) > W( soft.Skin[0], Bone1 ), "higher falloff did not sharpen" );
			Near( W( sharp.Skin[0], Bone1 ), 0.9865f, "falloff 4 bone1 share" );
		} );

		Check( "transfer mix blends into existing weights, mix 0 keeps them verbatim", () =>
		{
			var source = SourceQuad();
			var kept = Points( new Vec3( 8, 2, 0 ) );
			kept.Skin = SkinWeights.AllTo( 1, Bone0 );
			var reachedKept = SkinBinder.TransferFrom( kept, source, new TransferOptions { Mix = 0f } );
			Require( reachedKept == 1, "mix 0 still counts the surface reach" );
			Require( kept.Skin[0].Length == 1 && kept.Skin[0][0].Bone == Bone0 && kept.Skin[0][0].Weight == 1f, "mix 0 changed existing weights" );

			var half = Points( new Vec3( 8, 2, 0 ) );
			half.Skin = SkinWeights.AllTo( 1, Bone0 );
			SkinBinder.TransferFrom( half, source, new TransferOptions { Mix = 0.5f } );
			Near( W( half.Skin[0], Bone0 ), 0.6017f, "mix 0.5 bone0" );
			Near( W( half.Skin[0], Bone1 ), 0.3983f, "mix 0.5 bone1" );
		} );

		Check( "transfer unweighted neighbour average fills unreached vertices from reached ones", () =>
		{
			var source = SourceQuad();
			// t0/t2 reached inside the quad, t1 beyond maxDistance but sharing a face with them.
			var keep = Points( new Vec3( 8, 2, 0 ), new Vec3( 20, 2, 0 ), new Vec3( 8, 3, 0 ) );
			keep.AddFace( new[] { 0, 1, 2 } );
			var reachedKeep = SkinBinder.TransferFrom( keep, source, new TransferOptions { MaxDistance = 5f } );
			Require( reachedKeep == 2, $"expected 2 reached, got {reachedKeep}" );
			Require( keep.Skin[1].Length == 0, "keep mode filled an unreached vertex" );

			var fill = Points( new Vec3( 8, 2, 0 ), new Vec3( 20, 2, 0 ), new Vec3( 8, 3, 0 ) );
			fill.AddFace( new[] { 0, 1, 2 } );
			var reachedFill = SkinBinder.TransferFrom( fill, source, new TransferOptions { MaxDistance = 5f, Unweighted = UnweightedMode.NeighbourAverage } );
			Require( reachedFill == 2, "fill mode changed the reached count" );
			Require( fill.Skin[1].Length > 0, "neighbour fill left the unreached vertex empty" );
			Near( Sum( fill.Skin[1] ), 1f, "filled vertex is a partition of unity" );
		} );

		Check( "transfer unweighted nearest bone needs a skeleton, then pins the hole", () =>
		{
			var source = SourceQuad();
			var rig = TwoBones();
			var noRig = Points( new Vec3( 20, 2, 0 ) );
			Throws<InvalidOperationException>(
				() => SkinBinder.TransferFrom( noRig, source, new TransferOptions { MaxDistance = 5f, Unweighted = UnweightedMode.NearestBone } ),
				"nearest without skeleton" );

			var withRig = Points( new Vec3( 20, 2, 0 ) );
			var reached = SkinBinder.TransferFrom( withRig, source, new TransferOptions { MaxDistance = 5f, Unweighted = UnweightedMode.NearestBone }, rig );
			Require( reached == 0, $"expected 0 reached, got {reached}" );
			Require( withRig.Skin[0].Length == 1 && withRig.Skin[0][0].Bone == Bone1, "unreached vertex is not bone1" );
		} );

		Check( "transfer rejects bad options and an unrigged source", () =>
		{
			var source = SourceQuad();
			var bare = Points( new Vec3( 0, 0, 0 ) );
			Throws<InvalidOperationException>( () => SkinBinder.TransferFrom( bare, Points( new Vec3( 0, 0, 0 ) ) ), "unrigged source" );
			var target = Points( new Vec3( 8, 2, 0 ) );
			Throws<ArgumentNullException>( () => SkinBinder.TransferFrom( target, source, null! ), "null options" );
			Throws<ArgumentOutOfRangeException>( () => SkinBinder.TransferFrom( target, source, new TransferOptions { Falloff = 0f } ), "falloff 0" );
			Throws<ArgumentOutOfRangeException>( () => SkinBinder.TransferFrom( target, source, new TransferOptions { Mix = float.NaN } ), "mix NaN" );
		} );
	}
}
