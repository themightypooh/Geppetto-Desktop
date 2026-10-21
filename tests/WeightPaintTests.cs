#nullable enable
using System.Numerics;
using Effigy;
using Marionette.Desktop;

internal static class WeightPaintTests
{
	const int Root = 0, ArmL = 1, ArmR = 2;

	/// <summary>11×11 grid in the XY plane, x and y from -5 to 5 step 1, z = 0; vertex (i, j) = j*11 + i at x = i-5, y = j-5.
	/// Skin: everything on root.</summary>
	static (PolyMesh Mesh, Skeleton Rig) Plane()
	{
		var mesh = new PolyMesh();
		for ( int j = 0; j <= 10; j++ )
			for ( int i = 0; i <= 10; i++ )
				mesh.AddVertex( new Vec3( i - 5, j - 5, 0 ) );
		for ( int j = 0; j < 10; j++ )
			for ( int i = 0; i < 10; i++ )
				mesh.AddFace( new[] { j * 11 + i, j * 11 + i + 1, (j + 1) * 11 + i + 1, (j + 1) * 11 + i } );
		var rig = new Skeleton();
		rig.AddBoneFromPoints( "root", -1, new Vec3( 0, 0, -1 ), new Vec3( 0, 0, 0 ) );
		rig.AddBoneFromPoints( "arm_L", 0, new Vec3( 1, 0, 0 ), new Vec3( 5, 0, 0 ) );
		rig.AddBoneFromPoints( "arm_R", 0, new Vec3( -1, 0, 0 ), new Vec3( -5, 0, 0 ) );
		mesh.Skin = SkinWeights.AllTo( mesh.VertexCount, Root );
		for ( int v = 0; v < mesh.VertexCount; v++ ) mesh.Skin[v] = new[] { new BoneWeight( Root, 1f ) };
		return (mesh, rig);
	}

	static int At( int x, int y ) => (y + 5) * 11 + (x + 5);
	static float W( PolyMesh m, int v, int bone ) => m.Skin[v].Where( w => w.Bone == bone ).Sum( w => w.Weight );
	static float Sum( BoneWeight[] w ) => w.Sum( x => x.Weight );
	static BoneWeight[][] Snapshot( PolyMesh m ) => m.Skin.Vertices.Select( w => (BoneWeight[])w.Clone() ).ToArray();
	static bool Exact( PolyMesh m, BoneWeight[][] snap ) =>
		Enumerable.Range( 0, m.VertexCount ).All( v => m.Skin[v].Length == snap[v].Length
			&& m.Skin[v].Zip( snap[v] ).All( p => p.First.Bone == p.Second.Bone && p.First.Weight.Equals( p.Second.Weight ) ) );

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "weight-paint", name, test );
		void Require( bool condition, string message ) { if ( !condition ) throw new Exception( message ); }
		void Near( float actual, float expected, string what, float tol = 1e-5f ) =>
			Require( MathF.Abs( actual - expected ) <= tol, $"{what}: expected {expected}, got {actual}" );
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, got silent success" );
		}
		WeightPaintService Service( PolyMesh mesh, Skeleton rig, EditHistory history ) => new( mesh, rig, history )
		{
			Bone = ArmL, Tool = WeightPaintTool.Replace, Falloff = BrushFalloff.Constant, Radius = 0.5f, Strength = 1f, Value = 0.7f,
		};

		Check( "paint sets weight on vertex", () =>
		{
			var (mesh, rig) = Plane();
			var history = new EditHistory();
			var paint = Service( mesh, rig, history );
			var hit = paint.Hit( new Vector3( 2, 0, 10 ), new Vector3( 0, 0, -1 ) );
			Require( hit is { } h && Vector3.Distance( h.Point, new Vector3( 2, 0, 0 ) ) < 1e-4f, $"ray should hit (2,0,0), got {hit}" );
			Require( paint.Stroke( new[] { hit!.Value.Point } ), "stroke must commit" );
			Near( W( mesh, At( 2, 0 ), ArmL ), 0.7f, "arm_L at painted vertex" );
			Near( W( mesh, At( 2, 0 ), Root ), 0.3f, "root takes the remainder" );
			Require( mesh.Skin[At( 3, 0 )].Length == 1 && W( mesh, At( 3, 0 ), Root ) == 1f, "vertex outside the radius changed" );
			Require( history.UndoCount == 1, "one command per stroke" );
		} );

		Check( "add/subtract with smooth falloff", () =>
		{
			var (mesh, rig) = Plane();
			var history = new EditHistory();
			var paint = Service( mesh, rig, history );
			paint.Tool = WeightPaintTool.Add; paint.Falloff = BrushFalloff.Smooth; paint.Radius = 2.5f; paint.Strength = 0.5f;
			paint.Stroke( new[] { new Vector3( 0, 0, 0 ) } );
			float c = W( mesh, At( 0, 0 ), ArmL ), e1 = W( mesh, At( 1, 0 ), ArmL ), e2 = W( mesh, At( 2, 0 ), ArmL );
			Near( c, 0.5f, "centre gets full strength" );
			Require( c > e1 && e1 > e2 && e2 > 0f, $"falloff not decreasing: {c} {e1} {e2}" );
			Require( W( mesh, At( 3, 0 ), ArmL ) == 0f, "outside radius painted" );
			for ( int v = 0; v < mesh.VertexCount; v++ ) Near( Sum( mesh.Skin[v] ), 1f, $"sum at {v}" );
			paint.Tool = WeightPaintTool.Subtract; paint.Strength = 1f;
			paint.Stroke( new[] { new Vector3( 0, 0, 0 ) } );
			Require( W( mesh, At( 0, 0 ), ArmL ) == 0f && W( mesh, At( 0, 0 ), Root ) == 1f, "subtract to zero must remove the bone and give root everything" );
			Require( history.UndoCount == 2, "two strokes, two commands" );
		} );

		Check( "subtract on single-influence vertex is reported, not a command", () =>
		{
			var (mesh, rig) = Plane();
			var history = new EditHistory();
			var paint = Service( mesh, rig, history );
			paint.Bone = Root; paint.Tool = WeightPaintTool.Subtract; paint.Strength = 0.5f;
			Require( !paint.Stroke( new[] { new Vector3( 0, 0, 0 ) } ), "nothing can change" );
			Require( paint.LastStuckVertices == 1, $"stuck count {paint.LastStuckVertices}" );
			Require( history.UndoCount == 0, "no-op stroke must not add history" );
		} );

		Check( "normalize sums to 1 with locked bone preserved", () =>
		{
			var (mesh, rig) = Plane();
			var history = new EditHistory();
			var paint = Service( mesh, rig, history );
			int v = At( 0, 0 );
			mesh.Skin[v] = new[] { new BoneWeight( Root, 0.5f ), new BoneWeight( ArmL, 0.3f ), new BoneWeight( ArmR, 0.2f ) };
			paint.LockedBones.Add( ArmR );
			paint.Value = 0.6f;
			paint.Stroke( new[] { new Vector3( 0, 0, 0 ) } );
			Require( W( mesh, v, ArmR ) == 0.2f, $"locked bone changed: {W( mesh, v, ArmR )}" );
			Near( W( mesh, v, ArmL ), 0.6f, "painted bone" );
			Near( W( mesh, v, Root ), 0.2f, "unlocked partner absorbs the change" );
			Near( Sum( mesh.Skin[v] ), 1f, "sum" );
			// Asking for more than 1 - locked clamps; root drops out entirely.
			paint.Value = 0.95f;
			paint.Stroke( new[] { new Vector3( 0, 0, 0 ) } );
			Near( W( mesh, v, ArmL ), 0.8f, "clamped to 1 - locked" );
			Require( W( mesh, v, Root ) == 0f && W( mesh, v, ArmR ) == 0.2f, "root must be 0 and lock kept" );
			Near( Sum( mesh.Skin[v] ), 1f, "sum after clamp" );
			// Explicit normalize of an unnormalised vertex keeps the lock.
			int u = At( 4, 4 );
			mesh.Skin[u] = new[] { new BoneWeight( Root, 0.2f ), new BoneWeight( ArmL, 0.2f ), new BoneWeight( ArmR, 0.2f ) };
			Require( paint.Normalize( new[] { u } ), "normalize must commit" );
			Require( W( mesh, u, ArmR ) == 0.2f, "normalize changed a locked bone" );
			Near( W( mesh, u, Root ), 0.4f, "root rescaled" ); Near( W( mesh, u, ArmL ), 0.4f, "arm_L rescaled" );
			// Painting the locked bone itself is refused.
			paint.Bone = ArmR;
			Throws<InvalidOperationException>( () => paint.BeginStroke(), "painting a locked bone" );
			Throws<InvalidOperationException>( () => paint.Fill( 1f ), "filling a locked bone" );
			// Pure helper agrees.
			var pure = WeightPaintService.SetWeight( new[] { new BoneWeight( 0, 0.5f ), new BoneWeight( 1, 0.5f ) }, 2, 0.4f, new HashSet<int> { 1 } );
			Require( pure is not null && pure.Single( w => w.Bone == 1 ).Weight == 0.5f && MathF.Abs( pure.Single( w => w.Bone == 0 ).Weight - 0.1f ) < 1e-6f, "SetWeight with lock" );
		} );

		Check( "blur smooths a hard edge and keeps sums", () =>
		{
			var (mesh, rig) = Plane();
			var history = new EditHistory();
			var paint = Service( mesh, rig, history );
			Require( paint.Fill( 1f, Enumerable.Range( 0, mesh.VertexCount ).Where( v => mesh.Positions[v].x > 0f ) ), "fill" );
			paint.Tool = WeightPaintTool.Blur; paint.Radius = 1.5f; paint.Strength = 1f;
			paint.Stroke( new[] { new Vector3( 0, 0, 0 ), new Vector3( 0, 1, 0 ) } );
			float left = W( mesh, At( 0, 0 ), ArmL ), right = W( mesh, At( 1, 0 ), ArmL );
			Require( left > 0.05f && left < 0.95f, $"x=0 should blend after blur, got {left}" );
			Require( right > 0.05f && right < 0.999f, $"x=1 should soften after blur, got {right}" );
			for ( int v = 0; v < mesh.VertexCount; v++ ) Near( Sum( mesh.Skin[v] ), 1f, $"sum at {v}" );
			Require( history.UndoCount == 2, "fill + blur stroke = 2 commands" );
		} );

		Check( "mirror X paints the L/R counterpart", () =>
		{
			Require( WeightPaintService.MirrorBoneName( "arm_L" ) == "arm_R", "_L" );
			Require( WeightPaintService.MirrorBoneName( "hand.r" ) == "hand.l", ".r" );
			Require( WeightPaintService.MirrorBoneName( "L_leg" ) == "R_leg", "L_ prefix" );
			Require( WeightPaintService.MirrorBoneName( "LeftHand" ) == "RightHand", "Left word" );
			Require( WeightPaintService.MirrorBoneName( "spine" ) is null, "centre bone" );
			Require( WeightPaintService.MirrorBoneName( "pelvis" ) is null, "no false match" );

			var (mesh, rig) = Plane();
			var history = new EditHistory();
			var paint = Service( mesh, rig, history );
			paint.MirrorX = true; paint.Value = 0.8f;
			Require( paint.MirrorVertices()[At( 3, 2 )] == At( -3, 2 ), "vertex mirror map" );
			paint.Stroke( new[] { new Vector3( 3, 2, 0 ) } );
			Near( W( mesh, At( 3, 2 ), ArmL ), 0.8f, "painted side" );
			Near( W( mesh, At( -3, 2 ), ArmR ), 0.8f, "mirrored side gets arm_R" );
			Require( W( mesh, At( -3, 2 ), ArmL ) == 0f, "mirrored side must not get arm_L" );
			Require( paint.LastPaintedBones.SequenceEqual( new[] { ArmL, ArmR } ), "painted bones" );
			Require( history.UndoCount == 1, "mirror stroke is one command" );
			history.Undo();
			Require( W( mesh, At( -3, 2 ), ArmR ) == 0f && W( mesh, At( 3, 2 ), ArmL ) == 0f, "undo reverts both sides" );
			// Mirrored gradient.
			paint.Gradient( new Vector3( 1, 0, 0 ), new Vector3( 5, 0, 0 ) );
			for ( int x = 1; x <= 5; x++ )
				Near( W( mesh, At( -x, 3 ), ArmR ), W( mesh, At( x, 3 ), ArmL ), $"gradient symmetry at x={x}" );
			// A locked counterpart is not painted.
			paint.LockedBones.Add( ArmR );
			Require( paint.MirrorBone( ArmL ) is null, "locked mirror bone must be skipped" );
		} );

		Check( "undo/redo restores exact weights; cancel leaves no entry", () =>
		{
			var (mesh, rig) = Plane();
			var history = new EditHistory();
			var paint = Service( mesh, rig, history );
			mesh.Skin[At( 1, 1 )] = new[] { new BoneWeight( Root, 0.6f ), new BoneWeight( ArmR, 0.4f ) };
			var before = Snapshot( mesh );
			paint.Tool = WeightPaintTool.Add; paint.Falloff = BrushFalloff.Smooth; paint.Radius = 2f; paint.Strength = 0.3f;
			paint.BeginStroke();
			for ( int i = 0; i < 6; i++ ) paint.Dab( new Vector3( i * 0.4f, 1, 0 ) ); // overlapping dabs
			Require( paint.EndStroke(), "stroke committed" );
			Require( history.UndoCount == 1, $"one stroke = one command, got {history.UndoCount}" );
			var after = Snapshot( mesh );
			Require( !Exact( mesh, before ), "stroke changed nothing" );
			history.Undo();
			Require( Exact( mesh, before ), "undo not bit-exact" );
			history.Redo();
			Require( Exact( mesh, after ), "redo not bit-exact" );

			paint.BeginStroke();
			paint.Dab( new Vector3( -3, -3, 0 ) );
			paint.CancelStroke();
			Require( Exact( mesh, after ) && history.UndoCount == 1, "cancel must restore and add no entry" );
		} );

		Check( "fill and linear gradient set values; colours follow the ramp", () =>
		{
			var (mesh, rig) = Plane();
			var history = new EditHistory();
			var paint = Service( mesh, rig, history );
			Require( paint.Gradient( new Vector3( -5, 0, 0 ), new Vector3( 5, 0, 0 ) ), "gradient commits" );
			Near( W( mesh, At( -5, 2 ), ArmL ), 0f, "start" );
			Near( W( mesh, At( 0, 2 ), ArmL ), 0.5f, "middle" );
			Near( W( mesh, At( 5, 2 ), ArmL ), 1f, "end" );
			Near( W( mesh, At( 3, -4 ), ArmL ), 0.8f, "x=3" );
			for ( int v = 0; v < mesh.VertexCount; v++ ) Near( Sum( mesh.Skin[v] ), 1f, $"sum at {v}" );

			var rgba = paint.Colors( ArmL );
			Require( rgba.Length == mesh.VertexCount * 4, "4 floats per vertex" );
			Vector3 C( int v ) => new( rgba[v * 4], rgba[v * 4 + 1], rgba[v * 4 + 2] );
			Require( C( At( -5, 0 ) ) == new Vector3( 0, 0, 1 ), $"0 must be blue, got {C( At( -5, 0 ) )}" );
			Require( C( At( 5, 0 ) ) == new Vector3( 1, 0, 0 ), $"1 must be red, got {C( At( 5, 0 ) )}" );
			Require( C( At( 0, 0 ) ) == new Vector3( 0, 1, 0 ), $"0.5 must be green, got {C( At( 0, 0 ) )}" );
			Require( Enumerable.Range( 0, mesh.VertexCount ).All( v => rgba[v * 4 + 3] == 1f ), "alpha 1" );
			Require( WeightPaintService.WeightColor( float.NaN ) == new Vector3( 0, 0, 1 ), "NaN shows as 0" );
			// Red rises and blue falls monotonically along the gradient.
			for ( int x = -4; x <= 5; x++ )
			{
				var a = C( At( x - 1, 0 ) ); var b = C( At( x, 0 ) );
				Require( b.X >= a.X && b.Z <= a.Z, $"ramp not monotone at x={x}" );
			}

			Require( paint.Fill( 0.25f, new[] { At( 0, 0 ) } ), "fill" );
			Near( W( mesh, At( 0, 0 ), ArmL ), 0.25f, "fill value" );
			Require( !paint.Fill( 0.25f, new[] { At( 0, 0 ) } ), "refill with the same value is a no-op" );
			Require( history.UndoCount == 2, "gradient + fill" );
		} );

		Check( "paint on auto-weighted cylinder; history interleaves with auto-weight", () =>
		{
			var mesh = AutoWeightTests.Cylinder();
			var rig = AutoWeightTests.Chain( 2, 9f );
			var history = new EditHistory();
			AutoWeightService.Apply( mesh, rig, history );
			var auto = Snapshot( mesh );
			var paint = new WeightPaintService( mesh, rig, history ) { Bone = 1, Tool = WeightPaintTool.Replace, Value = 1f, Strength = 1f, Falloff = BrushFalloff.Constant, Radius = 1.5f };
			var hit = paint.Hit( new Vector3( 10, 0, 5 ), new Vector3( -1, 0, 0 ) );
			Require( hit is { } h && MathF.Abs( h.Point.X - 2f ) < 0.05f, $"side hit expected near x=2, got {hit}" );
			paint.Stroke( new[] { hit!.Value.Point } );
			int painted = Enumerable.Range( 0, mesh.VertexCount ).Count( v => mesh.Skin[v].Length == 1 && mesh.Skin[v][0].Bone == 1 && MathF.Abs( mesh.Positions[v].z - 5 ) <= 1.5f && mesh.Positions[v].x > 1f );
			Require( painted > 0, "no vertex was painted to bone_b" );
			Require( mesh.Skin.Validate( mesh.VertexCount, rig.Count ).Count == 0, "weights invalid after paint" );
			history.Undo();
			Require( Exact( mesh, auto ), "undo paint → exact auto weights" );
			history.Undo();
			Require( mesh.Skin is null, "undo auto → unrigged" );
		} );

		Check( "invalid inputs are rejected", () =>
		{
			var (mesh, rig) = Plane();
			var history = new EditHistory();
			Throws<ArgumentNullException>( () => new WeightPaintService( null!, rig, history ), "null mesh" );
			Throws<ArgumentNullException>( () => new WeightPaintService( mesh, null!, history ), "null skeleton" );
			Throws<ArgumentNullException>( () => new WeightPaintService( mesh, rig, null! ), "null history" );
			Throws<ArgumentException>( () => new WeightPaintService( mesh, new Skeleton(), history ), "no bones" );
			var (unskinned, _) = Plane(); unskinned.Skin = null!;
			Throws<ArgumentException>( () => new WeightPaintService( unskinned, rig, history ), "no skin" );
			var (short_, _) = Plane(); short_.Skin = new SkinWeights( 3 );
			Throws<ArgumentException>( () => new WeightPaintService( short_, rig, history ), "skin count mismatch" );
			var (badBone, _) = Plane(); badBone.Skin[0] = new[] { new BoneWeight( 7, 1f ) };
			Throws<ArgumentException>( () => new WeightPaintService( badBone, rig, history ), "skin bone out of range" );

			var paint = Service( mesh, rig, history );
			Throws<ArgumentOutOfRangeException>( () => paint.Bone = 3, "bone out of range" );
			Throws<ArgumentOutOfRangeException>( () => paint.Bone = -1, "negative bone" );
			Throws<ArgumentOutOfRangeException>( () => paint.Radius = 0f, "zero radius" );
			Throws<ArgumentOutOfRangeException>( () => paint.Radius = float.NaN, "NaN radius" );
			Throws<ArgumentOutOfRangeException>( () => paint.Strength = 1.5f, "strength > 1" );
			Throws<ArgumentOutOfRangeException>( () => paint.Value = -0.1f, "value < 0" );
			Throws<InvalidOperationException>( () => paint.Dab( Vector3.Zero ), "dab without stroke" );
			Throws<InvalidOperationException>( () => paint.EndStroke(), "end without stroke" );
			paint.BeginStroke();
			Throws<InvalidOperationException>( () => paint.BeginStroke(), "nested stroke" );
			Throws<ArgumentException>( () => paint.Dab( new Vector3( float.NaN, 0, 0 ) ), "NaN dab" );
			Throws<InvalidOperationException>( () => paint.Fill( 1f ), "fill during stroke" );
			paint.CancelStroke();
			Throws<ArgumentOutOfRangeException>( () => paint.Fill( 1.5f ), "fill value > 1" );
			Throws<ArgumentOutOfRangeException>( () => paint.Fill( 1f, new[] { -1 } ), "vertex index < 0" );
			Throws<ArgumentOutOfRangeException>( () => paint.Fill( 1f, new[] { mesh.VertexCount } ), "vertex index too big" );
			Throws<ArgumentException>( () => paint.Gradient( Vector3.One, Vector3.One ), "degenerate gradient" );
			Throws<ArgumentException>( () => paint.Hit( Vector3.Zero, Vector3.Zero ), "zero ray" );
			Require( paint.Hit( new Vector3( 50, 50, 10 ), new Vector3( 0, 0, -1 ) ) is null, "miss returns null" );
			paint.Tool = (WeightPaintTool)99;
			Throws<InvalidOperationException>( () => paint.BeginStroke(), "unknown tool" );
			Throws<ArgumentOutOfRangeException>( () => WeightPaintService.SetWeight( null, -1, 0.5f ), "SetWeight negative bone" );
			Throws<InvalidOperationException>( () => WeightPaintService.SetWeight( null, 1, 0.5f, new HashSet<int> { 1 } ), "SetWeight locked bone" );
			using ( var tx = history.BeginTransaction( "other", () => { } ) )
			{
				paint.Tool = WeightPaintTool.Add;
				Throws<InvalidOperationException>( () => paint.BeginStroke(), "stroke during transaction" );
			}
			Require( history.UndoCount == 0, "nothing should have been committed" );
		} );
	}
}
