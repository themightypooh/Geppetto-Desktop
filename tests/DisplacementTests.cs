using MapBrushFace = Effigy.Map.MapBrushFace;
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Map;

// Displacement data model: grid bound to a quad face, power 2/3/4, sew,
// Subdivide via CatmullClark, ToMesh, Validate. Named group "displacement".
//
// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned
// and the final integrator adds the one
// DisplacementTests.Register(registry, settings); line.
internal static class DisplacementTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "displacement", "power 3 grid has 81 vertices", () =>
		{
			var d = Displacement.Create( UnitSquare(), 3 );
			Assert( d.GridSize == 9, $"power 3 grid must be 9 wide, got {d.GridSize}" );
			Assert( d.VertexCount == 81, $"power 3 grid must have 81 vertices, got {d.VertexCount}" );
			Assert( d.Power == 3, $"power must be 3, got {d.Power}" );
			Assert( d.Offsets.Length == 81 && d.Alphas.Length == 81, "offsets/alphas arrays must match the grid" );
		} );

		registry.Add( "displacement", "power 2 and 4 grid sizes", () =>
		{
			Assert( Displacement.GridSizeForPower( 2 ) == 5, "power 2 grid must be 5 wide" );
			Assert( Displacement.GridSizeForPower( 4 ) == 17, "power 4 grid must be 17 wide" );
			Assert( Displacement.Create( UnitSquare(), 2 ).VertexCount == 25, "power 2 grid must have 25 vertices" );
			Assert( Displacement.Create( UnitSquare(), 4 ).VertexCount == 289, "power 4 grid must have 289 vertices" );
		} );

		registry.Add( "displacement", "power outside 2..4 is rejected", () =>
		{
			AssertThrows<ArgumentOutOfRangeException>( () => Displacement.Create( UnitSquare(), 1 ), "power 1 must be rejected" );
			AssertThrows<ArgumentOutOfRangeException>( () => Displacement.Create( UnitSquare(), 5 ), "power 5 must be rejected" );
			AssertThrows<ArgumentOutOfRangeException>( () => Displacement.GridSizeForPower( 0 ), "GridSizeForPower(0) must throw" );
		} );

		registry.Add( "displacement", "degenerate face is rejected", () =>
		{
			var bad = MapBrushFace.FromCorners( new[]
			{
				new Vec3( 0f, 0f, 0f ), new Vec3( 0f, 0f, 0f ),
				new Vec3( 1f, 1f, 0f ), new Vec3( 0f, 1f, 0f ),
			} );
			Assert( bad.Validate().Contains( "face-degenerate" ), $"duplicate corners must report face-degenerate, got {string.Join( ",", bad.Validate() )}" );
			AssertThrows<ArgumentException>( () => Displacement.Create( bad, 2 ), "Create on a degenerate face must throw" );
		} );

		registry.Add( "displacement", "non-planar face is rejected", () =>
		{
			var tilted = MapBrushFace.FromCorners( new[]
			{
				new Vec3( 0f, 0f, 0f ), new Vec3( 1f, 0f, 0f ),
				new Vec3( 1f, 1f, 0.5f ), new Vec3( 0f, 1f, 0f ),
			} );
			Assert( tilted.Validate().Contains( "face-non-planar" ), $"lifted corner must report face-non-planar, got {string.Join( ",", tilted.Validate() )}" );
			AssertThrows<ArgumentException>( () => Displacement.Create( tilted, 2 ), "Create on a non-planar face must throw" );
		} );

		registry.Add( "displacement", "flat grid lies on the face", () =>
		{
			var d = Displacement.Create( UnitSquare(), 2 );
			for ( var r = 0; r < 5; r++ )
				for ( var c = 0; c < 5; c++ )
				{
					var p = d.VertexPosition( r, c );
					var b = d.BasePosition( r, c );
					AssertNear( p.x, b.x, 1e-6f, $"flat vertex ({r},{c}).x" );
					AssertNear( p.y, b.y, 1e-6f, $"flat vertex ({r},{c}).y" );
					AssertNear( p.z, b.z, 1e-6f, $"flat vertex ({r},{c}).z" );
				}
			AssertNear( d.BasePosition( 0, 0 ).x, 0f, 1e-6f, "corner 0 x" );
			AssertNear( d.BasePosition( 4, 4 ).x, 1f, 1e-6f, "corner 2 x" );
			AssertNear( d.BasePosition( 4, 4 ).y, 1f, 1e-6f, "corner 2 y" );
		} );

		registry.Add( "displacement", "offset moves a vertex along the normal", () =>
		{
			var d = Displacement.Create( UnitSquare(), 2 );
			d.SetOffset( 2, 2, 2f );
			var p = d.VertexPosition( 2, 2 );
			AssertNear( p.x, 0.5f, 1e-6f, "offset must not move x" );
			AssertNear( p.y, 0.5f, 1e-6f, "offset must not move y" );
			AssertNear( p.z, 2f, 1e-6f, "offset 2 along +Z must put the centre at z=2" );
			AssertNear( d.GetOffset( 2, 2 ), 2f, 1e-6f, "GetOffset must read back the offset" );
		} );

		registry.Add( "displacement", "validate is clean for a fresh displacement", () =>
		{
			var d = Displacement.Create( UnitSquare(), 3 );
			Assert( d.Validate().Count == 0, $"fresh displacement must validate clean, got {string.Join( ",", d.Validate() )}" );
			Assert( d.IsValid, "fresh displacement must be valid" );
		} );

		registry.Add( "displacement", "alpha out of range fails validation", () =>
		{
			var d = Displacement.Create( UnitSquare(), 2 );
			d.Alphas[d.Index( 1, 1 )] = 2f;
			Assert( d.Validate().Contains( "alpha-out-of-range" ), $"alpha 2 must report alpha-out-of-range, got {string.Join( ",", d.Validate() )}" );
			AssertThrows<ArgumentOutOfRangeException>( () => d.SetAlpha( 1, 1, -0.5f ), "SetAlpha must reject -0.5" );
			d.Alphas[d.Index( 1, 1 )] = 0.5f;
			Assert( d.Validate().Count == 0, "alpha back in range must validate clean" );
		} );

		registry.Add( "displacement", "sew joins two adjacent grids with no gap", () =>
		{
			var (a, b) = SewPairFromFixture();
			Assert( a.Sew( b ), "Sew must find the shared edge x=1" );
			Assert( a.IsSewnTo( b ) && b.IsSewnTo( a ), "both sides must record the sew" );
			Assert( a.SewnEdges.Contains( 1 ), "face A must report its edge 1 as sewn" );
			Assert( b.SewnEdges.Contains( 3 ), "face B must report its edge 3 as sewn" );
			// Shared edge, read from y=0 to y=1 on both sides: offsets and alphas
			// must agree at every vertex, and the displaced positions must coincide.
			for ( var i = 0; i < 5; i++ )
			{
				var (ar, ac) = a.EdgeVertex( 1, i );
				var (br, bc) = b.EdgeVertex( 3, 4 - i );
				AssertNear( a.GetOffset( ar, ac ), b.GetOffset( br, bc ), 1e-6f, $"shared-edge offset {i} must agree" );
				AssertNear( a.GetAlpha( ar, ac ), b.GetAlpha( br, bc ), 1e-6f, $"shared-edge alpha {i} must agree" );
				var pa = a.VertexPosition( ar, ac );
				var pb = b.VertexPosition( br, bc );
				AssertNear( pa.x, pb.x, 1e-6f, $"shared-edge position {i}.x must coincide (no gap)" );
				AssertNear( pa.y, pb.y, 1e-6f, $"shared-edge position {i}.y must coincide (no gap)" );
				AssertNear( pa.z, pb.z, 1e-6f, $"shared-edge position {i}.z must coincide (no gap)" );
			}
			AssertNear( a.GetOffset( a.EdgeVertex( 1, 0 ).Row, a.EdgeVertex( 1, 0 ).Col ), 0f, 1e-6f, "shared edge at y=0 must keep offset 0" );
			AssertNear( a.GetOffset( a.EdgeVertex( 1, 4 ).Row, a.EdgeVertex( 1, 4 ).Col ), 2f, 1e-6f, "shared edge at y=1 must keep offset 2" );
			Assert( a.Validate().Count == 0, $"sewn A must validate clean, got {string.Join( ",", a.Validate() )}" );
			Assert( b.Validate().Count == 0, $"sewn B must validate clean, got {string.Join( ",", b.Validate() )}" );
		}, missing: () => FixturePath( "sew-pair.json" ) is null ? "sew-pair.json fixture not found" : null );

		registry.Add( "displacement", "sew returns false with no shared edge", () =>
		{
			var a = Displacement.Create( UnitSquare(), 2 );
			var far = Displacement.Create( MapBrushFace.FromCorners( new[]
			{
				new Vec3( 10f, 0f, 0f ), new Vec3( 11f, 0f, 0f ),
				new Vec3( 11f, 1f, 0f ), new Vec3( 10f, 1f, 0f ),
			} ), 2 );
			a.SetOffset( 2, 2, 3f );
			Assert( !a.Sew( far ), "Sew must return false for disjoint faces" );
			Assert( !a.IsSewnTo( far ), "disjoint faces must not record a sew" );
			AssertNear( a.GetOffset( 2, 2 ), 3f, 1e-6f, "failed sew must leave the grid untouched" );
		} );

		registry.Add( "displacement", "sew returns false for non-coplanar faces", () =>
		{
			// Face B stands up from the shared edge (plane x=1): scalar offsets
			// along different normals cannot make edge positions coincide.
			var a = Displacement.Create( UnitSquare(), 2 );
			var up = Displacement.Create( MapBrushFace.FromCorners( new[]
			{
				new Vec3( 1f, 0f, 0f ), new Vec3( 1f, 1f, 0f ),
				new Vec3( 1f, 1f, 1f ), new Vec3( 1f, 0f, 1f ),
			} ), 2 );
			Assert( !a.Sew( up ), "Sew must refuse a perpendicular face (scalar model cannot sew it)" );
			Assert( a.Validate().Count == 0, "refused sew must leave A valid" );
		} );

		registry.Add( "displacement", "sew is idempotent", () =>
		{
			var (a, b) = SewPairFromFixture();
			Assert( a.Sew( b ), "first sew must succeed" );
			var before = a.Positions();
			Assert( a.Sew( b ), "second sew must still report the shared edge" );
			var after = a.Positions();
			for ( var i = 0; i < before.Length; i++ )
			{
				// Offsets run along +Z, so z is the component a re-sew could move.
				AssertNear( after[i].x, before[i].x, 1e-6f, $"second sew must not move vertex {i}.x" );
				AssertNear( after[i].y, before[i].y, 1e-6f, $"second sew must not move vertex {i}.y" );
				AssertNear( after[i].z, before[i].z, 1e-6f, $"second sew must not move vertex {i}.z" );
			}
			Assert( b.Sew( a ), "sewing from the other side must also succeed" );
		}, missing: () => FixturePath( "sew-pair.json" ) is null ? "sew-pair.json fixture not found" : null );

		registry.Add( "displacement", "sew across different powers agrees at shared parameters", () =>
		{
			var a = Displacement.Create( UnitSquare(), 2 );
			var b = Displacement.Create( MapBrushFace.FromCorners( new[]
			{
				new Vec3( 1f, 0f, 0f ), new Vec3( 2f, 0f, 0f ),
				new Vec3( 2f, 1f, 0f ), new Vec3( 1f, 1f, 0f ),
			} ), 3 );
			for ( var i = 0; i < 5; i++ )
			{
				var (r, c) = a.EdgeVertex( 1, i );
				a.SetOffset( r, c, i * 0.5f );
			}
			for ( var j = 0; j < 9; j++ )
			{
				var (r, c) = b.EdgeVertex( 3, j );
				b.SetOffset( r, c, ( 8 - j ) * 0.25f );
			}
			Assert( a.Sew( b ), "power-2 to power-3 sew must succeed" );
			for ( var i = 0; i < 5; i++ )
			{
				var (ar, ac) = a.EdgeVertex( 1, i );
				var (br, bc) = b.EdgeVertex( 3, 8 - 2 * i );
				AssertNear( a.GetOffset( ar, ac ), b.GetOffset( br, bc ), 1e-6f,
					$"power-2 vertex {i} must agree with the matching power-3 vertex" );
			}
			Assert( a.Validate().Count == 0, $"mixed-power sewn A must validate clean, got {string.Join( ",", a.Validate() )}" );
			Assert( b.Validate().Count == 0, $"mixed-power sewn B must validate clean, got {string.Join( ",", b.Validate() )}" );
		} );

		registry.Add( "displacement", "subdivide keeps outer vertices", () =>
		{
			var d = Displacement.Create( UnitSquare(), 2 );
			d.SetOffset( 2, 2, 2f );
			for ( var i = 0; i < 5; i++ )
			{
				var (r, c) = d.EdgeVertex( 0, i );
				d.SetOffset( r, c, 0.25f * i );
			}
			var next = d.Subdivide();
			Assert( next.Power == 3, $"subdivide must raise power 2 to 3, got {next.Power}" );
			Assert( next.GridSize == 9 && next.VertexCount == 81, "subdivided grid must be 9x9" );
			// Outer ring: every original outer vertex must still be exactly there.
			for ( var i = 0; i < 5; i++ )
			{
				var (r, c) = d.EdgeVertex( 0, i );
				AssertNear( next.VertexPosition( 0, 2 * i ).x, d.VertexPosition( r, c ).x, 1e-4f, $"outer vertex (0,{2 * i}).x" );
				AssertNear( next.VertexPosition( 0, 2 * i ).z, d.VertexPosition( r, c ).z, 1e-4f, $"outer vertex (0,{2 * i}).z" );
				var (r2, c2) = d.EdgeVertex( 2, i );
				AssertNear( next.VertexPosition( 8, 8 - 2 * i ).z, d.VertexPosition( r2, c2 ).z, 1e-4f, $"outer vertex (8,{8 - 2 * i}).z" );
			}
			// Interior vertices go through real Catmull-Clark smoothing, so the
			// centre spike is reduced, not preserved: with F = 0.5 (face-point
			// average), R = 1 (edge-midpoint average), V = 2, n = 4, the CC vertex
			// rule gives (F + 2R + (n-3)V) / n = (0.5 + 2 + 2) / 4 = 1.125. The
			// edge point between the centre and its right neighbour is
			// (2 + 0 + 2*0.5) / 4 = 0.75, and the face point of the quad at the
			// centre's lower right is the centroid (2 + 0 + 0 + 0) / 4 = 0.5.
			// These exact values prove the subdivide ran Catmull-Clark rather
			// than a naive midpoint split.
			AssertNear( next.GetOffset( 4, 4 ), 1.125f, 1e-3f, "subdivide must smooth the centre offset to 1.125 (Catmull-Clark vertex rule)" );
			AssertNear( next.GetOffset( 4, 5 ), 0.75f, 1e-3f, "subdivide edge point must be 0.75 (Catmull-Clark edge rule)" );
			AssertNear( next.GetOffset( 5, 5 ), 0.5f, 1e-3f, "subdivide face point must be 0.5 (quad centroid)" );
			Assert( next.Validate().Count == 0, $"subdivided grid must validate clean, got {string.Join( ",", next.Validate() )}" );
		} );

		registry.Add( "displacement", "subdivide at power 4 throws", () =>
		{
			var d = Displacement.Create( UnitSquare(), 4 );
			AssertThrows<InvalidOperationException>( () => d.Subdivide(), "subdividing power 4 must throw (Source maximum)" );
		} );

		registry.Add( "displacement", "tomesh has the grid quads", () =>
		{
			var doc = LoadFixture( "power3-bump.json" );
			var root = doc.RootElement;
			var face = MapBrushFace.FromCorners( ReadCorners( root.GetProperty( "corners" ) ),
				root.GetProperty( "materialId" ).GetInt32() );
			var d = Displacement.Create( face, root.GetProperty( "power" ).GetInt32() );
			var offsets = root.GetProperty( "offsets" );
			Assert( offsets.GetArrayLength() == 81, $"fixture must carry 81 offsets, got {offsets.GetArrayLength()}" );
			for ( var i = 0; i < 81; i++ )
				d.Offsets[i] = (float)offsets[i].GetDouble();
			d.SetAlpha( 4, 4, 0.5f );
			var mesh = d.ToMesh();
			Assert( mesh.VertexCount == 81, $"ToMesh must have 81 vertices, got {mesh.VertexCount}" );
			Assert( mesh.FaceCount == 64, $"ToMesh must have 64 quads, got {mesh.FaceCount}" );
			Assert( mesh.Faces[0].Material == 7, $"ToMesh faces must carry material 7, got {mesh.Faces[0].Material}" );
			AssertNear( mesh.Positions[d.Index( 4, 4 )].z, 2f, 1e-6f, "ToMesh centre vertex must sit at z=2" );
			AssertNear( mesh.Positions[d.Index( 0, 0 )].z, 0f, 1e-6f, "ToMesh corner must sit at z=0" );
			Assert( mesh.HasVertexColors, "ToMesh must carry vertex colours (grey = blend alpha)" );
			var centre = mesh.VertexColors[d.Index( 4, 4 )];
			AssertNear( centre.x, 0.5f, 1e-6f, "centre vertex colour must encode alpha 0.5" );
			AssertNear( centre.w, 1f, 1e-6f, "vertex colour alpha channel must be 1" );
			var corner = mesh.VertexColors[d.Index( 0, 0 )];
			AssertNear( corner.x, 0f, 1e-6f, "unpainted vertex colour must encode alpha 0" );
		}, missing: () => FixturePath( "power3-bump.json" ) is null ? "power3-bump.json fixture not found" : null );

		registry.Add( "displacement", "tomesh interpolates corner uvs", () =>
		{
			var face = new MapBrushFace(
				new[] { new Vec3( 0f, 0f, 0f ), new Vec3( 1f, 0f, 0f ), new Vec3( 1f, 1f, 0f ), new Vec3( 0f, 1f, 0f ) },
				new Vec3( 0f, 0f, 1f ), 0f, 5, new Vec2( 0f, 0f ), new Vec2( 1f, 1f ), 0f, 16f, 0,
				new[] { new Vec2( 0f, 0f ), new Vec2( 1f, 0f ), new Vec2( 1f, 1f ), new Vec2( 0f, 1f ) } );
			var d = Displacement.Create( face, 2 );
			var mesh = d.ToMesh();
			var uvs = mesh.Faces[0].UVs;
			AssertNear( uvs[0].x, 0f, 1e-6f, "first quad UV 0.x" );
			AssertNear( uvs[0].y, 0f, 1e-6f, "first quad UV 0.y" );
			AssertNear( uvs[1].x, 0.25f, 1e-6f, "first quad UV 1.x must be the edge midpoint" );
			AssertNear( uvs[1].y, 0f, 1e-6f, "first quad UV 1.y" );
			AssertNear( uvs[2].x, 0.25f, 1e-6f, "first quad UV 2.x must be the quad centre" );
			AssertNear( uvs[2].y, 0.25f, 1e-6f, "first quad UV 2.y must be the quad centre" );
			AssertNear( uvs[3].x, 0f, 1e-6f, "first quad UV 3.x" );
			AssertNear( uvs[3].y, 0.25f, 1e-6f, "first quad UV 3.y must be the edge midpoint" );
		} );

		registry.Add( "displacement", "snapshot round-trips", () =>
		{
			var d = Displacement.Create( UnitSquare(), 3 );
			d.SetOffset( 4, 4, 2f );
			d.SetAlpha( 4, 4, 0.75f );
			d.SetOffset( 0, 8, 1f );
			var copy = Displacement.FromSnapshot( d.Snapshot() );
			Assert( copy.Power == 3 && copy.GridSize == 9, "snapshot must restore power/grid" );
			AssertNear( copy.GetOffset( 4, 4 ), 2f, 1e-6f, "snapshot must restore the centre offset" );
			AssertNear( copy.GetAlpha( 4, 4 ), 0.75f, 1e-6f, "snapshot must restore the centre alpha" );
			AssertNear( copy.GetOffset( 0, 8 ), 1f, 1e-6f, "snapshot must restore the corner offset" );
			copy.SetOffset( 4, 4, 9f );
			AssertNear( d.GetOffset( 4, 4 ), 2f, 1e-6f, "mutating the restored copy must not touch the original" );
			var broken = d.Snapshot();
			broken.Offsets = new float[3];
			AssertThrows<ArgumentException>( () => Displacement.FromSnapshot( broken ),
				"a snapshot with a short offsets array must throw naming grid-size-mismatch" );
			try
			{
				Displacement.FromSnapshot( broken );
				throw new Exception( "FromSnapshot did not throw for a short offsets array" );
			}
			catch ( ArgumentException ex )
			{
				Assert( ex.Message.Contains( "grid-size-mismatch" ), $"throw must name grid-size-mismatch, got: {ex.Message}" );
			}
		} );

		registry.Add( "displacement", "sewn edge disagreement fails validation", () =>
		{
			var (a, b) = SewPairFromFixture();
			Assert( a.Sew( b ), "sew must succeed first" );
			var (r, c) = a.EdgeVertex( 1, 2 );
			a.SetOffset( r, c, 99f );
			Assert( a.Validate().Contains( "sewn-edge-mismatch" ), $"moved sewn edge must report sewn-edge-mismatch on A, got {string.Join( ",", a.Validate() )}" );
			Assert( b.Validate().Contains( "sewn-edge-mismatch" ), $"moved sewn edge must report sewn-edge-mismatch on B, got {string.Join( ",", b.Validate() )}" );
			a.SetOffset( r, c, 1f );
			Assert( a.Validate().Count == 0, "restored sewn edge must validate clean" );
		}, missing: () => FixturePath( "sew-pair.json" ) is null ? "sew-pair.json fixture not found" : null );

		registry.Add( "displacement", "does not seal the map", () =>
		{
			var d = Displacement.Create( UnitSquare(), 2 );
			Assert( !d.SealsMap, "a displacement must never report that it seals the map" );
		} );

		registry.Add( "displacement", "fromcorners derives plane and uvs", () =>
		{
			var face = MapBrushFace.FromCorners( new[]
			{
				new Vec3( 0f, 0f, 0f ), new Vec3( 1f, 0f, 0f ),
				new Vec3( 1f, 1f, 0f ), new Vec3( 0f, 1f, 0f ),
			} );
			AssertNear( face.Normal.x, 0f, 1e-6f, "unit square normal x" );
			AssertNear( face.Normal.y, 0f, 1e-6f, "unit square normal y" );
			AssertNear( face.Normal.z, 1f, 1e-6f, "unit square normal must be +Z" );
			AssertNear( face.PlaneDistance, 0f, 1e-6f, "unit square plane distance must be 0" );
			AssertNear( face.Area, 1f, 1e-6f, "unit square area must be 1" );
			// Documented tangent frame: U = normal x least-aligned axis, V = normal x U.
			AssertNear( face.CornerUVs[0].x, 0f, 1e-6f, "corner 0 uv.x" );
			AssertNear( face.CornerUVs[0].y, 0f, 1e-6f, "corner 0 uv.y" );
			AssertNear( face.CornerUVs[1].x, 0f, 1e-6f, "corner 1 uv.x" );
			AssertNear( face.CornerUVs[1].y, -1f, 1e-6f, "corner 1 uv.y" );
			AssertNear( face.CornerUVs[2].x, 1f, 1e-6f, "corner 2 uv.x" );
			AssertNear( face.CornerUVs[2].y, -1f, 1e-6f, "corner 2 uv.y" );
			AssertNear( face.CornerUVs[3].x, 1f, 1e-6f, "corner 3 uv.x" );
			AssertNear( face.CornerUVs[3].y, 0f, 1e-6f, "corner 3 uv.y" );
		} );
	}

	static MapBrushFace UnitSquare() => MapBrushFace.FromCorners( new[]
	{
		new Vec3( 0f, 0f, 0f ), new Vec3( 1f, 0f, 0f ),
		new Vec3( 1f, 1f, 0f ), new Vec3( 0f, 1f, 0f ),
	} );

	/// <summary>Two adjacent power-2 grids from sew-pair.json, with the fixture's edge profiles applied (not yet sewn).</summary>
	static (Displacement A, Displacement B) SewPairFromFixture()
	{
		var doc = LoadFixture( "sew-pair.json" );
		var root = doc.RootElement;
		var a = Displacement.Create( ReadFace( root.GetProperty( "faceA" ) ), root.GetProperty( "power" ).GetInt32() );
		var b = Displacement.Create( ReadFace( root.GetProperty( "faceB" ) ), root.GetProperty( "power" ).GetInt32() );
		var offA = root.GetProperty( "edgeOffsetsA" );
		var offB = root.GetProperty( "edgeOffsetsB" );
		var alA = root.GetProperty( "edgeAlphasA" );
		var alB = root.GetProperty( "edgeAlphasB" );
		for ( var i = 0; i < 5; i++ )
		{
			var (ar, ac) = a.EdgeVertex( 1, i );
			a.SetOffset( ar, ac, (float)offA[i].GetDouble() );
			a.SetAlpha( ar, ac, (float)alA[i].GetDouble() );
			var (br, bc) = b.EdgeVertex( 3, i );
			b.SetOffset( br, bc, (float)offB[i].GetDouble() );
			b.SetAlpha( br, bc, (float)alB[i].GetDouble() );
		}
		return (a, b);
	}

	static MapBrushFace ReadFace( JsonElement el ) =>
		MapBrushFace.FromCorners( ReadCorners( el.GetProperty( "corners" ) ), el.GetProperty( "materialId" ).GetInt32() );

	static Vec3[] ReadCorners( JsonElement el )
	{
		var corners = new Vec3[el.GetArrayLength()];
		for ( var i = 0; i < corners.Length; i++ )
			corners[i] = new Vec3( (float)el[i][0].GetDouble(), (float)el[i][1].GetDouble(), (float)el[i][2].GetDouble() );
		return corners;
	}

	static string? FixturePath( string name )
	{
		var dir = Directory.GetCurrentDirectory();
		for ( var i = 0; i < 8 && dir is not null; i++ )
		{
			var candidate = Path.Combine( dir, "tests", "fixtures", "hammer", "hammer-displacement-data-model", name );
			if ( File.Exists( candidate ) )
				return candidate;
			dir = Directory.GetParent( dir )?.FullName;
		}
		return null;
	}

	static JsonDocument LoadFixture( string name )
	{
		var path = FixturePath( name ) ?? throw new Exception( $"Fixture {name} not found" );
		return JsonDocument.Parse( File.ReadAllText( path ) );
	}

	static void Assert( bool condition, string message )
	{
		if ( !condition ) throw new Exception( message );
	}

	static void AssertNear( float actual, float expected, float tolerance, string what )
	{
		if ( MathF.Abs( actual - expected ) > tolerance )
			throw new Exception( $"{what}: expected {expected}, got {actual}" );
	}

	static void AssertThrows<T>( Action action, string what ) where T : Exception
	{
		try
		{
			action();
		}
		catch ( T )
		{
			return;
		}
		throw new Exception( $"{what}: expected {typeof( T ).Name}, no exception was thrown" );
	}
}
