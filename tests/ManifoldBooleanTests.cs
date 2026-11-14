#nullable enable
using System;
using System.IO;
using System.Linq;
using Effigy;

/// <summary>
/// Manifold boolean backend (see kernel/Effigy/ManifoldBoolean.cs and
/// packaging/MANIFOLD.md). The adapter routes through the native Manifold
/// library when it is installed and falls back to the kernel's own
/// ExactMeshBoolean otherwise, with ManifoldBoolean.LastRoute saying which
/// ran and why.
///
/// Every check below is route-aware: volumes, closedness and winding are
/// asserted on whichever route runs, and the route itself is asserted to
/// be explicit ("manifold" or "built-in (...)"). Checks that can only run
/// through the native library skip honestly (Missing) when the packaged
/// libgmmanifold.so is not under packaging/native/ — they never pretend
/// the manifold route ran. The missing-library path is forced
/// deterministically with LibraryPathOverride, so the fallback is covered
/// on every machine.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// ManifoldBooleanTests.Register(registry, settings); line.
/// </summary>
internal static class ManifoldBooleanTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "manifold-boolean", name, test );
		void CheckManifoldOnly( string name, Action test ) => registry.Add( "manifold-boolean", name, test,
			missing: () => RepoManifoldLib() is null ? "packaged libgmmanifold.so not found under packaging/native/; manifold-only check skipped" : null );

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

		static void RequireRouteExplicit( string route, string message )
		{
			if ( route != "manifold" && !route.StartsWith( "built-in (", StringComparison.Ordinal ) )
				throw new Exception( $"{message}: route was '{route}', expected 'manifold' or 'built-in (...)'" );
		}

		void RequireVolume( PolyMesh result, float expected, string message )
		{
			float got = result.SignedVolume();
			Require( got > 0, $"{message}: signed volume {got} is not positive, so the winding is inward" );
			Require( Math.Abs( got - expected ) < 1e-3f * Math.Max( 1f, expected ),
				$"{message}: volume {got:0.0000} vs expected {expected:0.0000}" );
		}

		// The packaged library, found by walking up from the test binary
		// towards the repo root. Null on a machine without the Desktop tree.
		static string? RepoManifoldLib()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( Path.GetFullPath( start ) );
				for ( int depth = 0; depth < 10 && dir != null; depth++, dir = dir.Parent )
				{
					string candidate = Path.Combine( dir.FullName, "packaging", "native", "linux-x86_64", "libgmmanifold.so" );
					if ( File.Exists( candidate ) ) return candidate;
				}
			}
			return null;
		}

		// Point the loader at one library (or at a nonexistent path to force
		// the missing-library route), run the body, restore the override.
		static void WithLibrary( string? path, Action body )
		{
			string? saved = ManifoldBoolean.LibraryPathOverride;
			ManifoldBoolean.LibraryPathOverride = path;
			try { body(); }
			finally { ManifoldBoolean.LibraryPathOverride = saved; }
		}

		Check( "null and empty inputs refuse with a reason", () =>
		{
			bool ok = ManifoldBoolean.Instance.TryApply( BooleanOp.Union, null!, Primitives.Box( 2, 2, 2 ), out var r, out var e );
			Require( !ok && r is null && !string.IsNullOrEmpty( e ), "null target must refuse with a reason" );
			var empty = new PolyMesh();
			ok = ManifoldBoolean.Instance.TryApply( BooleanOp.Union, Primitives.Box( 2, 2, 2 ), empty, out r, out e );
			Require( !ok && r is null && !string.IsNullOrEmpty( e ), "empty tool must refuse with a reason" );
		} );

		Check( "overlapping boxes route explicitly and volumes match inclusion-exclusion", () =>
		{
			var a = Primitives.Box( 2, 2, 2 );
			var b = Moved( Primitives.Box( 2, 2, 2 ), 1, 1, 1 );
			var cases = new[] { (BooleanOp.Union, 15f), (BooleanOp.Subtract, 7f), (BooleanOp.Intersect, 1f) };
			foreach ( var (op, expected) in cases )
			{
				bool ok = ManifoldBoolean.Instance.TryApply( op, a, b, out var r, out var e );
				Require( ok, $"overlapping {op} refused: {e}" );
				Require( Closed( r ), $"overlapping {op} result is not closed" );
				RequireVolume( r!, expected, $"overlapping {op}" );
				RequireRouteExplicit( ManifoldBoolean.LastRoute, $"overlapping {op}" );
			}

			// Nested: the small box is entirely inside the big one.
			var c = Primitives.Box( 1, 1, 1 );
			foreach ( var (op, expected) in new[] { (BooleanOp.Union, 8f), (BooleanOp.Subtract, 7f), (BooleanOp.Intersect, 1f) } )
			{
				bool ok = ManifoldBoolean.Instance.TryApply( op, a, c, out var r, out var e );
				Require( ok, $"nested {op} refused: {e}" );
				Require( Closed( r ), $"nested {op} result is not closed" );
				RequireVolume( r!, expected, $"nested {op}" );
			}
		} );

		Check( "touching boxes union and empty intersect have explicit outcomes", () =>
		{
			var a = Primitives.Box( 2, 2, 2 );
			var d = Moved( Primitives.Box( 2, 2, 2 ), 2, 0, 0 );
			bool ok = ManifoldBoolean.Instance.TryApply( BooleanOp.Union, a, d, out var r, out var e );
			Require( ok, $"touching union refused: {e}" );
			RequireVolume( r!, 16f, "touching union" );
			RequireRouteExplicit( ManifoldBoolean.LastRoute, "touching union" );

			ok = ManifoldBoolean.Instance.TryApply( BooleanOp.Intersect, a, d, out r, out e );
			Require( !ok && !string.IsNullOrEmpty( e ), "touching intersect must refuse: the solids share no volume" );
		} );

		Check( "coplanar cases have explicit routes and closed outcomes", () =>
		{
			var a = Primitives.Box( 2, 2, 2 );

			// Shared face: overlap 1x2x2 = 4, union 8 + 8 - 4 = 12.
			var shared = Moved( Primitives.Box( 2, 2, 2 ), 1, 0, 0 );
			bool ok = ManifoldBoolean.Instance.TryApply( BooleanOp.Union, a, shared, out var r, out var e );
			Require( ok, $"coplanar shared-face union refused: {e}" );
			Require( Closed( r ), "coplanar shared-face union is not closed" );
			RequireVolume( r!, 12f, "coplanar shared-face union" );
			RequireRouteExplicit( ManifoldBoolean.LastRoute, "coplanar shared-face union" );

			// Flush cut: tool face x=1 is coplanar with the target +X face;
			// overlap 1x1x2 = 2, so 8 - 2 = 6 remains.
			var flush = Moved( Primitives.Box( 1, 1, 3 ), 0.5f, 0.5f, 0 );
			ok = ManifoldBoolean.Instance.TryApply( BooleanOp.Subtract, a, flush, out r, out e );
			Require( ok, $"flush cut refused: {e}" );
			Require( Closed( r ), "flush cut is not closed" );
			RequireVolume( r!, 6f, "flush cut" );

			// Edge-touching union: the two boxes meet along one edge only.
			// Volume is still 16, but closedness is route-dependent (the
			// shared edge is non-manifold when the boxes are just appended),
			// so only the route and the volume are asserted here.
			var edge = Moved( Primitives.Box( 2, 2, 2 ), 2, 2, 0 );
			ok = ManifoldBoolean.Instance.TryApply( BooleanOp.Union, a, edge, out r, out e );
			Require( ok, $"edge-touching union refused: {e}" );
			RequireVolume( r!, 16f, "edge-touching union" );
			RequireRouteExplicit( ManifoldBoolean.LastRoute, "edge-touching union" );

			// Cylinder through the box: faceted 48-gon cross-section
			// (area 1.12775) over the 2 units inside the box removes 2.2555.
			var cyl = Primitives.Cylinder( 0.6f, 3f, 48 );
			ok = ManifoldBoolean.Instance.TryApply( BooleanOp.Subtract, a, cyl, out r, out e );
			Require( ok, $"cylinder cut refused: {e}" );
			Require( Closed( r ), "cylinder cut is not closed" );
			float got = r!.SignedVolume();
			Require( got > 0 && Math.Abs( got - 5.7445f ) < 0.06f, $"cylinder cut volume {got:0.0000} vs expected 5.7445" );
		} );

		Check( "open input falls back with an explicit route", () =>
		{
			var open = Primitives.Box( 2, 2, 2 );
			open.Faces.RemoveAt( 0 );
			var check = MeshValidator.Validate( open );
			Require( !check.IsClosed && check.BoundaryEdges == 4, "fixture: open box must have 4 boundary edges" );

			var b = Moved( Primitives.Box( 2, 2, 2 ), 1, 1, 1 );
			bool ok = ManifoldBoolean.Instance.TryApply( BooleanOp.Union, open, b, out var r, out var e );
			Require( !ok && r is null, "open input must refuse (the built-in boolean refuses open inputs too)" );
			Require( !string.IsNullOrEmpty( e ) && e.Contains( "closed" ), $"open input error must name the open mesh: '{e}'" );
			Require( ManifoldBoolean.LastRoute.StartsWith( "built-in (", StringComparison.Ordinal ),
				$"open input route must be an explicit built-in fallback, was '{ManifoldBoolean.LastRoute}'" );
		} );

		Check( "nonmanifold input has an explicit route and an honest outcome", () =>
		{
			var dup = Primitives.Box( 2, 2, 2 );
			var first = dup.Faces[0];
			dup.Faces.Add( new Face( (int[])first.Indices.Clone(), (Vec2[])first.UVs.Clone(), first.Material ) );
			var check = MeshValidator.Validate( dup );
			Require( !check.IsClosed && check.NonManifoldEdges == 4 && check.BoundaryEdges == 0,
				$"fixture: duplicated face must give 4 non-manifold edges, got {check.NonManifoldEdges} non-manifold / {check.BoundaryEdges} boundary" );

			var b = Moved( Primitives.Box( 2, 2, 2 ), 1, 1, 1 );
			bool ok;
			PolyMesh? r;
			string? e;
			try
			{
				ok = ManifoldBoolean.Instance.TryApply( BooleanOp.Union, dup, b, out r, out e );
			}
			catch ( Exception ex )
			{
				throw new Exception( "a boolean provider must return false with a reason, never throw, on a non-manifold input", ex );
			}
			Require( ManifoldBoolean.LastRoute.StartsWith( "built-in (", StringComparison.Ordinal ),
				$"non-manifold input route must be an explicit built-in fallback, was '{ManifoldBoolean.LastRoute}'" );
			// Either honest outcome: the built-in boolean kept a closed mesh,
			// or it refused with a reason. A half answer is not allowed.
			Require( (ok && Closed( r )) || (!ok && r is null && !string.IsNullOrEmpty( e )),
				$"non-manifold outcome must be a closed mesh or a refusal with a reason, got ok={ok}, error='{e}'" );
		} );

		Check( "material survives the boolean on both routes", () =>
		{
			var a = Primitives.Box( 2, 2, 2, material: 3 );
			var b = Moved( Primitives.Box( 2, 2, 2, material: 5 ), 1, 1, 1 );
			bool ok = ManifoldBoolean.Instance.TryApply( BooleanOp.Union, a, b, out var r, out var e );
			Require( ok, $"material union refused: {e}" );
			var materials = r!.Faces.Select( f => f.Material ).Distinct().OrderBy( m => m ).ToArray();
			Require( materials.All( m => m == 3 || m == 5 ), $"union faces carry unexpected materials: {string.Join( ",", materials )}" );
			Require( materials.Contains( 3 ) && materials.Contains( 5 ), "union must keep faces from both inputs (materials 3 and 5)" );
		} );

		CheckManifoldOnly( "manifold route reports its own volume", () =>
		{
			string lib = RepoManifoldLib()!;
			WithLibrary( lib, () =>
			{
				var a = Primitives.Box( 2, 2, 2 );
				var b = Moved( Primitives.Box( 2, 2, 2 ), 1, 1, 1 );
				bool ok = ManifoldBoolean.Instance.TryApply( BooleanOp.Union, a, b, out var r, out var e );
				Require( ok, $"manifold union refused: {e}" );
				Require( ManifoldBoolean.LastRoute == "manifold", $"expected the manifold route, was '{ManifoldBoolean.LastRoute}'" );
				Require( Closed( r ), "manifold union is not closed" );
				RequireVolume( r!, 15f, "manifold union" );

				var direct = ManifoldBoolean.RunManifold( BooleanOp.Union, a, b, out var why, out double vol );
				Require( direct != null, $"RunManifold refused: {why}" );
				Require( Math.Abs( vol - direct!.SignedVolume() ) < 1e-3 * Math.Max( 1.0, vol ),
					$"native volume {vol:0.0000} disagrees with the returned mesh volume {direct.SignedVolume():0.0000}" );
			} );
		} );

		CheckManifoldOnly( "manifold refuses an open input directly", () =>
		{
			string lib = RepoManifoldLib()!;
			WithLibrary( lib, () =>
			{
				var open = Primitives.Box( 2, 2, 2 );
				open.Faces.RemoveAt( 0 );
				var b = Moved( Primitives.Box( 2, 2, 2 ), 1, 1, 1 );
				var r = ManifoldBoolean.RunManifold( BooleanOp.Union, open, b, out var why, out _ );
				Require( r is null, "Manifold must refuse an open input" );
				Require( !string.IsNullOrEmpty( why ) && why.Contains( "not manifold" ),
					$"Manifold refusal must name the cause, was '{why}'" );
			} );
		} );

		Check( "Install replaces only the default provider", () =>
		{
			var saved = MeshBoolean.Provider;
			try
			{
				MeshBoolean.Provider = ExactMeshBoolean.Instance;
				ManifoldBoolean.Install();
				Require( MeshBoolean.Provider is ManifoldBoolean, "Install must install Manifold over the default exact provider" );

				// An explicitly cleared provider means "deliberately no
				// boolean" — Install must not override that.
				MeshBoolean.Provider = null;
				ManifoldBoolean.Install();
				Require( MeshBoolean.Provider is null, "Install must not override an explicitly cleared provider" );
			}
			finally { MeshBoolean.Provider = saved; }
		} );

		Check( "missing library falls back with an explicit route", () =>
		{
			WithLibrary( "/nonexistent/libgmmanifold.so", () =>
			{
				Require( !ManifoldBoolean.Available(), "a nonexistent library path must report unavailable" );
				var a = Primitives.Box( 2, 2, 2 );
				var b = Moved( Primitives.Box( 2, 2, 2 ), 1, 1, 1 );
				bool ok = ManifoldBoolean.Instance.TryApply( BooleanOp.Union, a, b, out var r, out var e );
				Require( ok, $"missing-library union refused: {e}" );
				Require( ManifoldBoolean.LastRoute == "built-in (library not installed)",
					$"missing-library route must say so, was '{ManifoldBoolean.LastRoute}'" );
				Require( Closed( r ), "missing-library union is not closed" );
				RequireVolume( r!, 15f, "missing-library union" );
			} );
		} );

		Check( "kernel stays engine-free", () =>
		{
			var refs = typeof( ManifoldBoolean ).Assembly.GetReferencedAssemblies();
			foreach ( var r in refs )
			{
				string name = r.Name ?? "";
				Require( !name.StartsWith( "Godot", StringComparison.OrdinalIgnoreCase ), $"kernel must not reference Godot: found {name}" );
				Require( !name.StartsWith( "Sandbox", StringComparison.OrdinalIgnoreCase ), $"kernel must not reference Sandbox: found {name}" );
				Require( !name.StartsWith( "UnityEngine", StringComparison.OrdinalIgnoreCase ), $"kernel must not reference Unity: found {name}" );
				Require( !name.StartsWith( "Silk.NET", StringComparison.OrdinalIgnoreCase ), $"kernel must not reference Silk.NET: found {name}" );
				Require( !name.StartsWith( "Marionette.Desktop", StringComparison.OrdinalIgnoreCase ), $"kernel must not reference Marionette.Desktop: found {name}" );
			}
		} );
	}
}
