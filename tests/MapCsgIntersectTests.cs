#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Effigy;
using Effigy.Map;

/// <summary>
/// CSG intersect for map brushes (see kernel/Effigy/Map/MapCsg.cs, board
/// hammer-csg-intersect). The fourth sane-CSG op: Intersect(a, b) is the
/// convex overlap, and two solids that share no volume give the named empty
/// result "csg-empty-result" — never a zero-volume brush.
///
/// The acceptance numbers live in the fixture
/// tests/fixtures/hammer/hammer-csg-intersect/cases.json and one check below
/// recomputes them from that file; the rest build the same cases
/// synthetically so a missing fixture can never hide a geometry failure.
/// Volumes are asserted on SignedVolume (positive = outward winding) and
/// every successful result must be a closed, convex solid.
///
/// MAPBRUSH NOTE. Same as the pass-1 group (tests/MapCsgTests.cs): the
/// MapBrush type has no stable API yet, so these checks run against
/// PolyMesh, the solid type IMeshBoolean consumes, plus the plane layer
/// (BrushPlanes / MeshFromPlanes). Nothing here assumes MapBrush signatures.
///
/// FAILURE SAFETY (board hammer-csg-failure-safety). The checks added for
/// that ticket prove the contract for intersect too: on ANY failure
/// (mutating/refusing/throwing provider, empty result, missing provider,
/// manifold-missing fallback failure) both source brushes are
/// byte-identical to snapshots taken before the call, and the failure is a
/// named reason with no mesh. The provider doubles below deliberately
/// destroy their inputs before refusing/throwing, so a MapCsg without the
/// clone-before-attempt guard would fail these checks by corrupting the
/// caller's brushes.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// MapCsgIntersectTests.Register(registry, settings); line.
/// </summary>
internal static class MapCsgIntersectTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "mapcsgintersect", name, test );

		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		static PolyMesh Moved( PolyMesh m, float x, float y, float z )
		{
			var c = m.Clone();
			for ( int i = 0; i < c.Positions.Count; i++ ) { var p = c.Positions[i]; c.Positions[i] = new Vec3( p.x + x, p.y + y, p.z + z ); }
			c.InvalidateVertexCache();
			return c;
		}

		static bool Closed( PolyMesh? m ) => m != null && m.FaceCount > 0 && MeshValidator.Validate( m ).IsClosed;

		void RequireVolume( PolyMesh result, float expected, string message )
		{
			float got = result.SignedVolume();
			Require( got > 0, $"{message}: signed volume {got} is not positive, so the winding is inward" );
			Require( Math.Abs( got - expected ) < 1e-3f * Math.Max( 1f, expected ),
				$"{message}: volume {got:0.0000} vs expected {expected:0.0000}" );
		}

		void RequireRouteExplicit( string route, string message )
		{
			if ( route != "manifold" && !route.StartsWith( "built-in (", StringComparison.Ordinal ) && !route.StartsWith( "custom (", StringComparison.Ordinal ) )
				throw new Exception( $"{message}: route was '{route}', expected 'manifold', 'built-in (...)' or 'custom (...)'" );
		}

		void RequireEmpty( CsgResult r, string message )
		{
			Require( !r.Ok && r.Reason == "csg-empty-result",
				$"{message} must refuse with csg-empty-result, got ok={r.Ok} reason={r.Reason}" );
			Require( r.Mesh is null, $"{message}: an empty intersection must not return a mesh (not even a zero-volume one)" );
		}

		/// <summary>True when two meshes carry identical geometry: same
		/// positions (exact), same faces in the same order (indices,
		/// material, UVs). Volume alone is not enough — a provider that
		/// moved one vertex and moved another back would keep the volume
		/// and still have destroyed the brush.</summary>
		static bool SameGeometry( PolyMesh a, PolyMesh b )
		{
			if ( a is null || b is null ) return a is null && b is null;
			if ( a.Positions.Count != b.Positions.Count || a.FaceCount != b.FaceCount ) return false;
			for ( int i = 0; i < a.Positions.Count; i++ )
				if ( !a.Positions[i].Equals( b.Positions[i] ) ) return false;
			for ( int f = 0; f < a.FaceCount; f++ )
			{
				var fa = a.Faces[f];
				var fb = b.Faces[f];
				if ( fa.Material != fb.Material ) return false;
				if ( fa.Indices is null || fb.Indices is null ) return fa.Indices is null && fb.Indices is null;
				if ( fa.Indices.Length != fb.Indices.Length ) return false;
				for ( int i = 0; i < fa.Indices.Length; i++ )
					if ( fa.Indices[i] != fb.Indices[i] ) return false;
				var ua = fa.UVs;
				var ub = fb.UVs;
				if ( ua is null || ub is null ) return ua is null && ub is null;
				if ( ua.Length != ub.Length ) return false;
				for ( int i = 0; i < ua.Length; i++ )
					if ( ua[i].x != ub[i].x || ua[i].y != ub[i].y ) return false;
			}
			return true;
		}

		void RequireUntouched( PolyMesh before, PolyMesh after, string message )
		{
			Require( SameGeometry( before, after ),
				$"{message}: the source brush changed during a failed operation — positions, faces, UVs or materials differ from the snapshot taken before the call" );
		}

		static string? FindFailureFixture( string name )
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( Path.GetFullPath( start ) );
				for ( int depth = 0; depth < 10 && dir != null; depth++, dir = dir.Parent )
				{
					string candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-csg-failure-safety", name );
					if ( File.Exists( candidate ) ) return candidate;
				}
			}
			return null;
		}

		static string? FindFixture( string name )
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( Path.GetFullPath( start ) );
				for ( int depth = 0; depth < 10 && dir != null; depth++, dir = dir.Parent )
				{
					string candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-csg-intersect", name );
					if ( File.Exists( candidate ) ) return candidate;
				}
			}
			return null;
		}

		Check( "two overlapping boxes intersect to the overlap volume", () =>
		{
			var r = MapCsg.TryIntersect( Primitives.Box( 4, 4, 4 ), Moved( Primitives.Box( 4, 4, 4 ), 2, 0, 0 ) );
			Require( r.Ok, $"partial intersect refused: {r.Reason} {r.Message}" );
			Require( Closed( r.Mesh ), "partial intersect result is not closed" );
			// Overlap is x in [0,2]: 2*4*4 = 32.
			RequireVolume( r.Mesh!, 32f, "partial intersect" );
			Require( r.Mesh!.FaceCount == 6, $"overlap should be one 2x4x4 box with 6 faces, has {r.Mesh.FaceCount}" );
			Require( MapCsg.IsConvex( r.Mesh ), "overlap of two convex boxes must be convex" );
			RequireRouteExplicit( r.Route, "partial intersect" );
		} );

		Check( "contained box intersect is the smaller box", () =>
		{
			var r = MapCsg.TryIntersect( Primitives.Box( 4, 4, 4 ), Primitives.Box( 2, 2, 2 ) );
			Require( r.Ok, $"contained intersect refused: {r.Reason} {r.Message}" );
			Require( Closed( r.Mesh ), "contained intersect result is not closed" );
			RequireVolume( r.Mesh!, 8f, "contained intersect" );
			Require( r.Mesh!.FaceCount == 6, $"contained intersect should be the 2-cube with 6 faces, has {r.Mesh.FaceCount}" );
			Require( MapCsg.IsConvex( r.Mesh ), "contained intersect is not convex" );
		} );

		Check( "identical boxes intersect to themselves", () =>
		{
			var r = MapCsg.TryIntersect( Primitives.Box( 4, 4, 4 ), Primitives.Box( 4, 4, 4 ) );
			Require( r.Ok, $"identical intersect refused: {r.Reason} {r.Message}" );
			Require( Closed( r.Mesh ), "identical intersect result is not closed" );
			RequireVolume( r.Mesh!, 64f, "identical intersect" );
			Require( r.Mesh!.FaceCount == 6, $"identical intersect should be the 4-cube with 6 faces, has {r.Mesh.FaceCount}" );
			Require( MapCsg.IsConvex( r.Mesh ), "identical intersect is not convex" );
		} );

		Check( "disjoint boxes give the named empty result", () =>
		{
			var r = MapCsg.TryIntersect( Primitives.Box( 4, 4, 4 ), Moved( Primitives.Box( 2, 2, 2 ), 10, 0, 0 ) );
			RequireEmpty( r, "disjoint intersect" );
		} );

		Check( "touching boxes give the named empty result, not a zero-volume brush", () =>
		{
			// Face-to-face touch: the 2-cubes share the x=1 plane, no volume.
			var faceTouch = MapCsg.TryIntersect( Primitives.Box( 2, 2, 2 ), Moved( Primitives.Box( 2, 2, 2 ), 2, 0, 0 ) );
			RequireEmpty( faceTouch, "face-touching intersect" );
			// Point touch: the 2-cubes meet at a single corner point only.
			var pointTouch = MapCsg.TryIntersect( Primitives.Box( 2, 2, 2 ), Moved( Primitives.Box( 2, 2, 2 ), 2, 2, 2 ) );
			RequireEmpty( pointTouch, "point-touching intersect" );
		} );

		Check( "intersect output is convex and valid with coplanar faces merged", () =>
		{
			var r = MapCsg.TryIntersect( Primitives.Box( 4, 4, 4 ), Moved( Primitives.Box( 2, 2, 2 ), 1, 0, 0 ) );
			Require( r.Ok, $"offset intersect refused: {r.Reason} {r.Message}" );
			Require( Closed( r.Mesh ), "offset intersect result is not closed" );
			// Overlap is x in [0,2], y/z in [-1,1]: 2*2*2 = 8.
			RequireVolume( r.Mesh!, 8f, "offset intersect" );
			Require( MapCsg.IsConvex( r.Mesh ), "offset intersect result is not convex" );
			// Coplanar merge on output: the overlap shares the big box's +X
			// face plane, so a split-fragment result would carry extra faces.
			// A clean 2-cube has 6 faces and 6 face planes.
			Require( r.Mesh!.FaceCount == 6, $"merged intersect should have 6 faces, has {r.Mesh.FaceCount}" );
			Require( MapCsg.BrushPlanes( r.Mesh ).Count == 6,
				$"merged intersect should have 6 face planes, has {MapCsg.BrushPlanes( r.Mesh ).Count}" );
			Require( r.FacesMerged >= 0, "FacesMerged must be a count, not a sentinel" );
			RequireRouteExplicit( r.Route, "offset intersect" );
		} );

		Check( "intersect is commutative", () =>
		{
			var a = Primitives.Box( 4, 4, 4 );
			var b = Moved( Primitives.Box( 2, 2, 2 ), 1, 0, 0 );
			var ab = MapCsg.TryIntersect( a, b );
			var ba = MapCsg.TryIntersect( b, a );
			Require( ab.Ok && ba.Ok, $"commutative intersect refused: {ab.Reason} / {ba.Reason}" );
			RequireVolume( ab.Mesh!, 8f, "intersect(a, b)" );
			RequireVolume( ba.Mesh!, 8f, "intersect(b, a)" );
			Require( Math.Abs( ab.Mesh!.SignedVolume() - ba.Mesh!.SignedVolume() ) < 1e-3f,
				"intersect(a, b) and intersect(b, a) must have the same volume" );
		} );

		Check( "intersect null empty and open inputs refuse with named reasons", () =>
		{
			var n = MapCsg.TryIntersect( null!, Primitives.Box( 2, 2, 2 ) );
			Require( !n.Ok && n.Reason == "csg-input-null", $"null first input must refuse with csg-input-null, got {n.Reason}" );
			var n2 = MapCsg.TryIntersect( Primitives.Box( 2, 2, 2 ), null! );
			Require( !n2.Ok && n2.Reason == "csg-input-null", $"null second input must refuse with csg-input-null, got {n2.Reason}" );
			var e = MapCsg.TryIntersect( new PolyMesh(), Primitives.Box( 2, 2, 2 ) );
			Require( !e.Ok && e.Reason == "csg-input-empty", $"empty first input must refuse with csg-input-empty, got {e.Reason}" );
			var open = Primitives.Box( 2, 2, 2 );
			open.Faces.RemoveAt( 0 );
			var o = MapCsg.TryIntersect( open, Primitives.Box( 2, 2, 2 ) );
			Require( !o.Ok && o.Reason == "csg-input-open", $"open first input must refuse with csg-input-open, got {o.Reason}" );
		} );

		Check( "intersect with no provider is a named capability error, not fake success", () =>
		{
			var saved = MeshBoolean.Provider;
			MeshBoolean.Provider = null;
			try
			{
				var r = MapCsg.TryIntersect( Primitives.Box( 4, 4, 4 ), Moved( Primitives.Box( 4, 4, 4 ), 2, 0, 0 ) );
				Require( !r.Ok && r.Reason == "csg-no-boolean-provider",
					$"missing provider must fail with csg-no-boolean-provider, got ok={r.Ok} reason={r.Reason}" );
				Require( r.Mesh is null, "a capability failure must not return a fake mesh" );
				var direct = MapCsg.TryIntersect( Primitives.Box( 4, 4, 4 ), Moved( Primitives.Box( 4, 4, 4 ), 2, 0, 0 ), ExactMeshBoolean.Instance );
				Require( direct.Ok, $"explicit provider override must still solve while the global provider is null: {direct.Reason}" );
				RequireVolume( direct.Mesh!, 32f, "explicit-provider intersect" );
			}
			finally { MeshBoolean.Provider = saved; }
		} );

		Check( "manifold-missing falls back to built-in and still solves", () =>
		{
			string? saved = ManifoldBoolean.LibraryPathOverride;
			ManifoldBoolean.LibraryPathOverride = "/nonexistent/geppetto-test/libgmmanifold.so";
			try
			{
				var r = MapCsg.TryIntersect( Primitives.Box( 4, 4, 4 ), Moved( Primitives.Box( 4, 4, 4 ), 2, 0, 0 ), ManifoldBoolean.Instance );
				Require( r.Ok, $"manifold-missing fallback refused: {r.Reason} {r.Message}" );
				Require( r.Route.StartsWith( "built-in (", StringComparison.Ordinal ),
					$"route should name the built-in fallback, was '{r.Route}'" );
				RequireVolume( r.Mesh!, 32f, "fallback intersect" );
			}
			finally { ManifoldBoolean.LibraryPathOverride = saved; }
		} );

		Check( "manifold-missing disjoint intersect is still the named empty result", () =>
		{
			string? saved = ManifoldBoolean.LibraryPathOverride;
			ManifoldBoolean.LibraryPathOverride = "/nonexistent/geppetto-test/libgmmanifold.so";
			try
			{
				var r = MapCsg.TryIntersect( Primitives.Box( 4, 4, 4 ), Moved( Primitives.Box( 2, 2, 2 ), 10, 0, 0 ), ManifoldBoolean.Instance );
				RequireEmpty( r, "manifold-missing disjoint intersect" );
			}
			finally { ManifoldBoolean.LibraryPathOverride = saved; }
		} );

		Check( "a mutating provider that refuses cannot destroy the intersect inputs", () =>
		{
			var first = Primitives.Box( 4, 4, 4 );
			var second = Moved( Primitives.Box( 4, 4, 4 ), 2, 0, 0 );
			var firstBefore = first.Clone();
			var secondBefore = second.Clone();
			var r = MapCsg.TryIntersect( first, second, new MutatingRefusingBoolean() );
			Require( !r.Ok && r.Reason == "csg-provider-refused",
				$"mutating refusal must fail with csg-provider-refused, got ok={r.Ok} reason={r.Reason}" );
			Require( r.Mesh is null, "a refused intersect must not return a mesh" );
			RequireUntouched( firstBefore, first, "intersect first input after a mutating refusal" );
			RequireUntouched( secondBefore, second, "intersect second input after a mutating refusal" );
		} );

		Check( "a mutating provider that throws cannot destroy the intersect inputs", () =>
		{
			var first = Primitives.Box( 4, 4, 4 );
			var second = Moved( Primitives.Box( 4, 4, 4 ), 2, 0, 0 );
			var firstBefore = first.Clone();
			var secondBefore = second.Clone();
			var r = MapCsg.TryIntersect( first, second, new MutatingThrowingBoolean() );
			Require( !r.Ok && r.Reason == "csg-provider-threw",
				$"mutating throw must fail with csg-provider-threw, got ok={r.Ok} reason={r.Reason}" );
			Require( r.Mesh is null, "an intersect that threw must not return a mesh" );
			RequireUntouched( firstBefore, first, "intersect first input after a mutating throw" );
			RequireUntouched( secondBefore, second, "intersect second input after a mutating throw" );
		} );

		Check( "disjoint intersect under the manifold-missing fallback leaves inputs untouched", () =>
		{
			string? saved = ManifoldBoolean.LibraryPathOverride;
			ManifoldBoolean.LibraryPathOverride = "/nonexistent/geppetto-test/libgmmanifold.so";
			try
			{
				var first = Primitives.Box( 4, 4, 4 );
				var second = Moved( Primitives.Box( 2, 2, 2 ), 10, 0, 0 );
				var firstBefore = first.Clone();
				var secondBefore = second.Clone();
				var r = MapCsg.TryIntersect( first, second, ManifoldBoolean.Instance );
				RequireEmpty( r, "manifold-missing disjoint intersect" );
				RequireUntouched( firstBefore, first, "intersect first input after a manifold-missing fallback failure" );
				RequireUntouched( secondBefore, second, "intersect second input after a manifold-missing fallback failure" );
			}
			finally { ManifoldBoolean.LibraryPathOverride = saved; }
		} );

		Check( "intersect with no provider leaves inputs unchanged", () =>
		{
			var first = Primitives.Box( 4, 4, 4 );
			var second = Moved( Primitives.Box( 4, 4, 4 ), 2, 0, 0 );
			var firstBefore = first.Clone();
			var secondBefore = second.Clone();
			var saved = MeshBoolean.Provider;
			MeshBoolean.Provider = null;
			try
			{
				var r = MapCsg.TryIntersect( first, second );
				Require( !r.Ok && r.Reason == "csg-no-boolean-provider",
					$"missing provider must fail with csg-no-boolean-provider, got ok={r.Ok} reason={r.Reason}" );
				Require( r.Mesh is null, "a capability failure must not return a fake mesh" );
				RequireUntouched( firstBefore, first, "intersect first input with no provider" );
				RequireUntouched( secondBefore, second, "intersect second input with no provider" );
			}
			finally { MeshBoolean.Provider = saved; }
		} );

		Check( "deferred plan stages intersect and solves only at bake", () =>
		{
			var plan = new MapCsgPlan();
			var target = Primitives.Box( 4, 4, 4 );
			plan.StageIntersect( target, Moved( Primitives.Box( 4, 4, 4 ), 2, 0, 0 ), "overlap" );
			plan.StageSubtract( Primitives.Box( 4, 4, 4 ), Primitives.Box( 2, 2, 2 ), "room-cut" );
			plan.StageIntersect( Primitives.Box( 4, 4, 4 ), Moved( Primitives.Box( 2, 2, 2 ), 10, 0, 0 ), "miss" );
			Require( plan.PendingCount == 3, $"plan should hold 3 staged ops, has {plan.PendingCount}" );
			// Staging snapshots: mutating the caller's brush afterwards must
			// not change what bake solves.
			target.Positions[0] = new Vec3( 1000, 1000, 1000 );
			target.InvalidateVertexCache();
			var saved = MeshBoolean.Provider;
			MeshBoolean.Provider = null;
			try
			{
				var failed = plan.Solve();
				Require( !failed.Ok && failed.Results.Count == 3, "solving with no provider must fail all 3 staged ops" );
				foreach ( var r in failed.Results )
					Require( !r.Ok && r.Reason == "csg-no-boolean-provider" && r.Mesh is null,
						$"staged op with no provider must fail with csg-no-boolean-provider, got {r.Reason}" );
			}
			finally { MeshBoolean.Provider = saved; }
			var baked = plan.Solve( ExactMeshBoolean.Instance );
			Require( !baked.Ok, "bake with a disjoint staged intersect must report not-ok overall" );
			Require( baked.Results.Count == 3, $"bake should return 3 results, has {baked.Results.Count}" );
			RequireVolume( baked.Results[0].Mesh!, 32f, "plan intersect" );
			RequireVolume( baked.Results[1].Mesh!, 56f, "plan subtract" );
			RequireEmpty( baked.Results[2], "plan disjoint intersect" );
			Require( plan.PendingCount == 3, "solve does not consume the plan; Clear() is explicit" );
			plan.Clear();
			Require( plan.PendingCount == 0, "Clear() must empty the plan" );
		} );

		Check( "no Carve operation exists, and Intersect is a staged op kind", () =>
		{
			foreach ( var type in new[] { typeof( MapCsg ), typeof( MapCsgPlan ), typeof( CsgResult ), typeof( CsgPendingOp ), typeof( CsgSolveResult ), typeof( CsgOpKind ), typeof( BrushPlane ) } )
				foreach ( var m in type.GetMembers( BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly ) )
					Require( !m.Name.Contains( "Carve", StringComparison.OrdinalIgnoreCase ),
						$"{type.Name} exposes a member named {m.Name}: Carve must not exist" );
			Require( Enum.IsDefined( typeof( CsgOpKind ), "Intersect" ), "CsgOpKind must define Intersect" );
			Require( typeof( MapCsgPlan ).GetMethod( "StageIntersect" ) != null, "MapCsgPlan must expose StageIntersect" );
			Require( typeof( MapCsg ).GetMethod( "TryIntersect" ) != null, "MapCsg must expose TryIntersect" );
		} );

		registry.Add( "mapcsgintersect", "fixture acceptance cases match computed volumes", () =>
		{
			string path = FindFixture( "cases.json" )!;
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var cases = doc.RootElement.GetProperty( "cases" );
			int ran = 0;
			foreach ( var c in cases.EnumerateArray() )
			{
				string name = c.GetProperty( "name" ).GetString()!;
				string op = c.GetProperty( "op" ).GetString()!;
				Require( op == "intersect", $"fixture case {name} has unknown op '{op}' for this group" );
				var t = c.GetProperty( "target" );
				var target = Primitives.Box( t[0].GetSingle(), t[1].GetSingle(), t[2].GetSingle() );
				var tool = Moved( BoxOf( c ), OffX( c ), OffY( c ), OffZ( c ) );
				var r = MapCsg.TryIntersect( target, tool );
				if ( c.TryGetProperty( "empty", out var empty ) && empty.GetBoolean() )
				{
					RequireEmpty( r, $"fixture case {name}" );
					ran++;
					continue;
				}
				float expected = c.GetProperty( "expectedVolume" ).GetSingle();
				Require( r.Ok, $"fixture case {name} refused: {r.Reason} {r.Message}" );
				RequireVolume( r.Mesh!, expected, $"fixture case {name}" );
				if ( c.TryGetProperty( "expectedFaces", out var faces ) )
					Require( r.Mesh!.FaceCount == faces.GetInt32(), $"fixture case {name} should have {faces.GetInt32()} faces, has {r.Mesh.FaceCount}" );
				if ( c.TryGetProperty( "convex", out var convex ) )
					Require( MapCsg.IsConvex( r.Mesh! ) == convex.GetBoolean(), $"fixture case {name} convexity mismatch" );
				ran++;
			}
			Require( ran == 6, $"expected 6 fixture cases, ran {ran}" );

			static PolyMesh BoxOf( JsonElement c )
			{
				var tool = c.GetProperty( "tool" );
				return Primitives.Box( tool[0].GetSingle(), tool[1].GetSingle(), tool[2].GetSingle() );
			}
			static float OffX( JsonElement c ) => c.GetProperty( "toolOffset" )[0].GetSingle();
			static float OffY( JsonElement c ) => c.GetProperty( "toolOffset" )[1].GetSingle();
			static float OffZ( JsonElement c ) => c.GetProperty( "toolOffset" )[2].GetSingle();
		}, missing: () => FindFixture( "cases.json" ) is null
			? "cases.json fixture not found under tests/fixtures/hammer/hammer-csg-intersect/"
			: null );

		registry.Add( "mapcsgintersect", "fixture failure cases leave inputs intact with named reasons", () =>
		{
			// Board hammer-csg-failure-safety. The intersect cases of the
			// shared failure fixture must fail with their expected named
			// reason, return no mesh, and leave both inputs byte-identical
			// to snapshots taken before the call. The subtract/merge/hollow
			// cases in the same file are owned by the mapcsg group.
			string path = FindFailureFixture( "cases.json" )!;
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var cases = doc.RootElement.GetProperty( "cases" );
			int ran = 0;
			int skippedOther = 0;
			foreach ( var c in cases.EnumerateArray() )
			{
				string name = c.GetProperty( "name" ).GetString()!;
				string op = c.GetProperty( "op" ).GetString()!;
				if ( op != "intersect" ) { skippedOther++; continue; }
				var t = c.GetProperty( "target" );
				var first = Primitives.Box( t[0].GetSingle(), t[1].GetSingle(), t[2].GetSingle() );
				var firstBefore = first.Clone();
				var tool = c.GetProperty( "tool" );
				var second = Moved( Primitives.Box( tool[0].GetSingle(), tool[1].GetSingle(), tool[2].GetSingle() ),
					c.GetProperty( "toolOffset" )[0].GetSingle(),
					c.GetProperty( "toolOffset" )[1].GetSingle(),
					c.GetProperty( "toolOffset" )[2].GetSingle() );
				var secondBefore = second.Clone();
				string expected = c.GetProperty( "expectedReason" ).GetString()!;
				bool noProvider = c.TryGetProperty( "provider", out var prov ) && prov.GetString() == "none";
				var saved = MeshBoolean.Provider;
				if ( noProvider ) MeshBoolean.Provider = null;
				CsgResult r;
				try { r = MapCsg.TryIntersect( first, second ); }
				finally { MeshBoolean.Provider = saved; }
				Require( !r.Ok && r.Reason == expected,
					$"fixture failure case {name} must fail with {expected}, got ok={r.Ok} reason={r.Reason}" );
				Require( r.Mesh is null, $"fixture failure case {name} must not return a mesh" );
				RequireUntouched( firstBefore, first, $"fixture failure case {name} first input" );
				RequireUntouched( secondBefore, second, $"fixture failure case {name} second input" );
				ran++;
			}
			Require( ran == 2, $"expected 2 intersect fixture failure cases, ran {ran}" );
			Require( skippedOther == 4, $"expected 4 non-intersect fixture failure cases for the other group, skipped {skippedOther}" );
		}, missing: () => FindFailureFixture( "cases.json" ) is null
			? "cases.json fixture not found under tests/fixtures/hammer/hammer-csg-failure-safety/"
			: null );
	}

	// -- Failure-safety provider doubles (board hammer-csg-failure-safety).
	// Same contract as in tests/MapCsgTests.cs: they destroy their inputs
	// before refusing/throwing, so MapCsg must never hand them the
	// caller's meshes (Apply() clones first).

	/// <summary>Worst-case provider: clears both inputs, then refuses.</summary>
	sealed class MutatingRefusingBoolean : IMeshBoolean
	{
		public bool TryApply( BooleanOp op, PolyMesh target, PolyMesh tool, out PolyMesh result, out string error )
		{
			target.Positions.Clear();
			target.Faces.Clear();
			tool.Positions.Clear();
			tool.Faces.Clear();
			result = null!;
			error = "refused after corrupting its inputs";
			return false;
		}
	}

	/// <summary>Worst-case provider: corrupts both inputs, then throws.</summary>
	sealed class MutatingThrowingBoolean : IMeshBoolean
	{
		public bool TryApply( BooleanOp op, PolyMesh target, PolyMesh tool, out PolyMesh result, out string error )
		{
			target.Positions[0] = new Vec3( 999, 999, 999 );
			target.InvalidateVertexCache();
			tool.Faces.RemoveAt( 0 );
			result = null!;
			error = null!;
			throw new InvalidOperationException( "provider blew up mid-operation" );
		}
	}
}
