#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Effigy;
using Effigy.Map;

/// <summary>
/// CSG subtract / merge / hollow for map brushes (see
/// kernel/Effigy/Map/MapCsg.cs, board hammer-brush-csg-subtract-merge-hollow).
///
/// The acceptance numbers live in the fixture
/// tests/fixtures/hammer/hammer-brush-csg-subtract-merge-hollow/cases.json
/// and one check below recomputes them from that file; the rest build the
/// same cases synthetically so a missing fixture can never hide a geometry
/// failure. Volumes are asserted on SignedVolume (positive = outward
/// winding) and every result must be a closed solid.
///
/// MAPBRUSH NOTE. The MapBrush type this ticket depends on has no stable API
/// yet — the real implementation landed 16:04 and was overwritten 16:06 by the
/// MapClip worker's "NEVER in the real repo" stub (see the mismatch note in
/// MapCsg.cs) — so these checks run against PolyMesh, the solid type
/// IMeshBoolean consumes, plus the plane layer (BrushPlanes /
/// MeshFromPlanes). When the real MapBrush is restored and frozen, the
/// MapBrush adapter checks belong with it; nothing here assumes its signatures.
///
/// FAILURE SAFETY (board hammer-csg-failure-safety). The checks added for
/// that ticket prove the contract, not just the reasons: on ANY failure
/// (mutating/refusing/throwing provider, degenerate boolean result,
/// non-convex merge, too-thick hollow, missing provider, manifold-missing
/// fallback failure) the source brushes are byte-identical to a snapshot
/// taken before the call, and the failure is a named reason with no mesh.
/// The provider doubles below are the proof: they deliberately destroy
/// their inputs before refusing/throwing, so a MapCsg without the
/// clone-before-attempt guard would fail these checks by corrupting the
/// caller's brushes.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// MapCsgTests.Register(registry, settings); line.
/// </summary>
internal static class MapCsgTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "mapcsg", name, test );

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
					string candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-brush-csg-subtract-merge-hollow", name );
					if ( File.Exists( candidate ) ) return candidate;
				}
			}
			return null;
		}

		Check( "subtract nested box leaves hollow shell with correct volume", () =>
		{
			var r = MapCsg.TrySubtract( Primitives.Box( 4, 4, 4 ), Primitives.Box( 2, 2, 2 ) );
			Require( r.Ok, $"nested subtract refused: {r.Reason} {r.Message}" );
			Require( Closed( r.Mesh ), "nested subtract result is not closed" );
			RequireVolume( r.Mesh!, 56f, "nested subtract" );
			RequireRouteExplicit( r.Route, "nested subtract" );
		} );

		Check( "subtract through-cut box has correct volume", () =>
		{
			var r = MapCsg.TrySubtract( Primitives.Box( 4, 4, 4 ), Moved( Primitives.Box( 2, 4, 2 ), 1, 0, 0 ) );
			Require( r.Ok, $"through-cut refused: {r.Reason} {r.Message}" );
			Require( Closed( r.Mesh ), "through-cut result is not closed" );
			RequireVolume( r.Mesh!, 48f, "through-cut" );
		} );

		Check( "subtract disjoint cutter is a no-op with unchanged volume", () =>
		{
			var r = MapCsg.TrySubtract( Primitives.Box( 4, 4, 4 ), Moved( Primitives.Box( 2, 2, 2 ), 10, 0, 0 ) );
			Require( r.Ok, $"disjoint subtract refused: {r.Reason} {r.Message}" );
			RequireVolume( r.Mesh!, 64f, "disjoint subtract" );
		} );

		Check( "merge two coplanar boxes is one convex brush", () =>
		{
			var a = Primitives.Box( 2, 2, 2 );
			var b = Moved( Primitives.Box( 2, 2, 2 ), 2, 0, 0 );
			var r = MapCsg.TryMerge( a, b );
			Require( r.Ok, $"coplanar merge refused: {r.Reason} {r.Message}" );
			Require( Closed( r.Mesh ), "merged brush is not closed" );
			RequireVolume( r.Mesh!, 16f, "coplanar merge" );
			Require( r.Mesh!.FaceCount == 6, $"merged brush should be one 4x2x2 box with 6 faces, has {r.Mesh.FaceCount}" );
			Require( MapCsg.IsConvex( r.Mesh ), "merged brush is not convex" );
			RequireRouteExplicit( r.Route, "coplanar merge" );
		} );

		Check( "merge L-shaped union is rejected with merge-not-convex", () =>
		{
			var a = Primitives.Box( 2, 2, 2 );
			var b = Moved( Primitives.Box( 2, 2, 2 ), 1, 1, 0 );
			bool okU = ExactMeshBoolean.Instance.TryApply( BooleanOp.Union, a, b, out var union, out _ );
			Require( okU && !MapCsg.IsConvex( union ), "premise: the L-shaped union really is non-convex" );
			var r = MapCsg.TryMerge( a, b );
			Require( !r.Ok && r.Reason == "merge-not-convex", $"L merge must refuse with merge-not-convex, got ok={r.Ok} reason={r.Reason}" );
			Require( r.Mesh is null, "a refused merge must not return a mesh" );
			Require( Math.Abs( a.SignedVolume() - 8f ) < 1e-3f && Math.Abs( b.SignedVolume() - 8f ) < 1e-3f,
				"a refused merge must leave its inputs untouched" );
		} );

		Check( "hollow 128-cube with 8 walls leaves the right inner volume", () =>
		{
			var r = MapCsg.TryHollow( Primitives.Box( 128, 128, 128 ), 8f );
			Require( r.Ok, $"hollow refused: {r.Reason} {r.Message}" );
			Require( Closed( r.Mesh ), "hollow shell is not closed" );
			// Outer 128^3 = 2097152, inner void 112^3 = 1404928, walls = 692224.
			RequireVolume( r.Mesh!, 692224f, "hollow 128-cube t=8" );
			Require( r.Mesh!.FaceCount == 12, $"hollow shell should have 6 outer + 6 inner faces, has {r.Mesh.FaceCount}" );
			Require( !MapCsg.IsConvex( r.Mesh ), "a hollow shell is not a convex solid" );
		} );

		Check( "hollow refusals carry named reasons", () =>
		{
			var zero = MapCsg.TryHollow( Primitives.Box( 4, 4, 4 ), 0f );
			Require( !zero.Ok && zero.Reason == "hollow-thickness-not-positive",
				$"zero thickness must refuse with hollow-thickness-not-positive, got {zero.Reason}" );
			var thick = MapCsg.TryHollow( Primitives.Box( 128, 128, 128 ), 100f );
			Require( !thick.Ok && thick.Reason == "hollow-too-thick",
				$"thickness 100 on a 128-cube must refuse with hollow-too-thick, got {thick.Reason}" );
			bool okU = ExactMeshBoolean.Instance.TryApply( BooleanOp.Union,
				Primitives.Box( 2, 2, 2 ), Moved( Primitives.Box( 2, 2, 2 ), 1, 1, 0 ), out var l, out _ );
			Require( okU, "premise: L-shaped union must build" );
			var nonConvex = MapCsg.TryHollow( l!, 0.25f );
			Require( !nonConvex.Ok && nonConvex.Reason == "hollow-needs-convex",
				$"non-convex hollow must refuse with hollow-needs-convex, got {nonConvex.Reason}" );
		} );

		Check( "plane round-trip rebuilds the box", () =>
		{
			var box = Primitives.Box( 2, 3, 4 );
			var planes = MapCsg.BrushPlanes( box );
			Require( planes.Count == 6, $"a box has 6 face planes, got {planes.Count}" );
			var rebuilt = MapCsg.MeshFromPlanes( planes );
			Require( rebuilt != null, "plane-intersection rebuild returned no solid" );
			Require( rebuilt!.FaceCount == 6, $"rebuilt box should have 6 faces, has {rebuilt.FaceCount}" );
			Require( Closed( rebuilt ), "rebuilt box is not closed" );
			RequireVolume( rebuilt, 24f, "plane round-trip" );
		} );

		Check( "manifold-missing falls back to built-in and still solves", () =>
		{
			string? saved = ManifoldBoolean.LibraryPathOverride;
			ManifoldBoolean.LibraryPathOverride = "/nonexistent/geppetto-test/libgmmanifold.so";
			try
			{
				var r = MapCsg.TrySubtract( Primitives.Box( 4, 4, 4 ), Primitives.Box( 2, 2, 2 ), ManifoldBoolean.Instance );
				Require( r.Ok, $"manifold-missing fallback refused: {r.Reason} {r.Message}" );
				Require( r.Route.StartsWith( "built-in (", StringComparison.Ordinal ),
					$"route should name the built-in fallback, was '{r.Route}'" );
				RequireVolume( r.Mesh!, 56f, "fallback subtract" );
			}
			finally { ManifoldBoolean.LibraryPathOverride = saved; }
		} );

		Check( "no boolean provider is a named capability error, not fake success", () =>
		{
			var saved = MeshBoolean.Provider;
			MeshBoolean.Provider = null;
			try
			{
				var r = MapCsg.TrySubtract( Primitives.Box( 4, 4, 4 ), Primitives.Box( 2, 2, 2 ) );
				Require( !r.Ok && r.Reason == "csg-no-boolean-provider",
					$"missing provider must fail with csg-no-boolean-provider, got ok={r.Ok} reason={r.Reason}" );
				Require( r.Mesh is null, "a capability failure must not return a fake (uncut) mesh" );
				var direct = MapCsg.TrySubtract( Primitives.Box( 4, 4, 4 ), Primitives.Box( 2, 2, 2 ), ExactMeshBoolean.Instance );
				Require( direct.Ok, $"explicit provider override must still solve while the global provider is null: {direct.Reason}" );
				RequireVolume( direct.Mesh!, 56f, "explicit-provider subtract" );
			}
			finally { MeshBoolean.Provider = saved; }
		} );

		Check( "a mutating provider that refuses cannot destroy the subtract inputs", () =>
		{
			var target = Primitives.Box( 4, 4, 4 );
			var tool = Primitives.Box( 2, 2, 2 );
			var targetBefore = target.Clone();
			var toolBefore = tool.Clone();
			var r = MapCsg.TrySubtract( target, tool, new MutatingRefusingBoolean() );
			Require( !r.Ok && r.Reason == "csg-provider-refused",
				$"mutating refusal must fail with csg-provider-refused, got ok={r.Ok} reason={r.Reason}" );
			Require( r.Mesh is null, "a refused subtract must not return a mesh" );
			RequireUntouched( targetBefore, target, "subtract target after a mutating refusal" );
			RequireUntouched( toolBefore, tool, "subtract tool after a mutating refusal" );
		} );

		Check( "a mutating provider that throws cannot destroy the merge inputs", () =>
		{
			var first = Primitives.Box( 2, 2, 2 );
			var second = Moved( Primitives.Box( 2, 2, 2 ), 2, 0, 0 );
			var firstBefore = first.Clone();
			var secondBefore = second.Clone();
			var r = MapCsg.TryMerge( first, second, new MutatingThrowingBoolean() );
			Require( !r.Ok && r.Reason == "csg-provider-threw",
				$"mutating throw must fail with csg-provider-threw, got ok={r.Ok} reason={r.Reason}" );
			Require( r.Mesh is null, "a merge that threw must not return a mesh" );
			RequireUntouched( firstBefore, first, "merge first input after a mutating throw" );
			RequireUntouched( secondBefore, second, "merge second input after a mutating throw" );
		} );

		Check( "hollow too thick leaves the source brush intact with hollow-too-thick", () =>
		{
			// The Reddit case: "make hollow deletes the brush itself". A
			// thickness that leaves no inner solid must refuse and leave
			// the brush exactly as it was — full volume and all.
			var solid = Primitives.Box( 128, 128, 128 );
			var before = solid.Clone();
			var r = MapCsg.TryHollow( solid, 100f );
			Require( !r.Ok && r.Reason == "hollow-too-thick",
				$"thickness 100 on a 128-cube must refuse with hollow-too-thick, got ok={r.Ok} reason={r.Reason}" );
			Require( r.Mesh is null, "a hollow that leaves no inner solid must not return a mesh — and must not delete the brush" );
			RequireUntouched( before, solid, "hollow source after hollow-too-thick" );
			Require( Math.Abs( solid.SignedVolume() - 2097152f ) < 1e-3f * 2097152f,
				$"the source brush must keep its full 128^3 volume, has {solid.SignedVolume():0.###}" );
		} );

		Check( "hollow whose boolean returns a degenerate result leaves the source intact", () =>
		{
			var solid = Primitives.Box( 4, 4, 4 );
			var before = solid.Clone();
			var r = MapCsg.TryHollow( solid, 0.5f, new DegenerateResultBoolean() );
			Require( !r.Ok && r.Reason == "csg-empty-result",
				$"degenerate hollow result must fail with csg-empty-result, got ok={r.Ok} reason={r.Reason}" );
			Require( r.Mesh is null, "a degenerate hollow must not return a mesh" );
			RequireUntouched( before, solid, "hollow source after a degenerate boolean result" );
		} );

		Check( "a provider that returns an open mesh leaves subtract inputs untouched", () =>
		{
			var target = Primitives.Box( 4, 4, 4 );
			var tool = Primitives.Box( 2, 2, 2 );
			var targetBefore = target.Clone();
			var toolBefore = tool.Clone();
			var r = MapCsg.TrySubtract( target, tool, new OpenResultBoolean() );
			Require( !r.Ok && r.Reason == "csg-result-not-closed",
				$"open boolean result must fail with csg-result-not-closed, got ok={r.Ok} reason={r.Reason}" );
			Require( r.Mesh is null, "an open boolean result must not be returned as a mesh" );
			RequireUntouched( targetBefore, target, "subtract target after an open boolean result" );
			RequireUntouched( toolBefore, tool, "subtract tool after an open boolean result" );
		} );

		Check( "subtract with no provider leaves inputs unchanged with csg-no-boolean-provider", () =>
		{
			var target = Primitives.Box( 4, 4, 4 );
			var tool = Primitives.Box( 2, 2, 2 );
			var targetBefore = target.Clone();
			var toolBefore = tool.Clone();
			var saved = MeshBoolean.Provider;
			MeshBoolean.Provider = null;
			try
			{
				var r = MapCsg.TrySubtract( target, tool );
				Require( !r.Ok && r.Reason == "csg-no-boolean-provider",
					$"missing provider must fail with csg-no-boolean-provider, got ok={r.Ok} reason={r.Reason}" );
				Require( r.Mesh is null, "a capability failure must not return a fake (uncut) mesh" );
				RequireUntouched( targetBefore, target, "subtract target with no provider" );
				RequireUntouched( toolBefore, tool, "subtract tool with no provider" );
			}
			finally { MeshBoolean.Provider = saved; }
		} );

		Check( "merge refusal for a non-convex union leaves both inputs untouched", () =>
		{
			var a = Primitives.Box( 2, 2, 2 );
			var b = Moved( Primitives.Box( 2, 2, 2 ), 1, 1, 0 );
			var aBefore = a.Clone();
			var bBefore = b.Clone();
			var r = MapCsg.TryMerge( a, b );
			Require( !r.Ok && r.Reason == "merge-not-convex",
				$"L merge must refuse with merge-not-convex, got ok={r.Ok} reason={r.Reason}" );
			Require( r.Mesh is null, "a refused merge must not return a mesh" );
			RequireUntouched( aBefore, a, "merge first input after merge-not-convex" );
			RequireUntouched( bBefore, b, "merge second input after merge-not-convex" );
		} );

		Check( "manifold-missing fallback failure leaves inputs untouched", () =>
		{
			// Manifold is missing, so the exact boolean runs the fallback
			// — and the L-shaped union it builds is refused as non-convex.
			// The failure must carry the named reason and both brushes
			// must survive it byte-for-byte.
			string? saved = ManifoldBoolean.LibraryPathOverride;
			ManifoldBoolean.LibraryPathOverride = "/nonexistent/geppetto-test/libgmmanifold.so";
			try
			{
				var a = Primitives.Box( 2, 2, 2 );
				var b = Moved( Primitives.Box( 2, 2, 2 ), 1, 1, 0 );
				var aBefore = a.Clone();
				var bBefore = b.Clone();
				var r = MapCsg.TryMerge( a, b, ManifoldBoolean.Instance );
				Require( !r.Ok && r.Reason == "merge-not-convex",
					$"manifold-missing merge failure must be merge-not-convex, got ok={r.Ok} reason={r.Reason}" );
				Require( r.Mesh is null, "a refused fallback merge must not return a mesh" );
				RequireUntouched( aBefore, a, "merge first input after a manifold-missing fallback failure" );
				RequireUntouched( bBefore, b, "merge second input after a manifold-missing fallback failure" );
			}
			finally { ManifoldBoolean.LibraryPathOverride = saved; }
		} );

		Check( "a failed staged op leaves the plan snapshots intact for re-bake", () =>
		{
			var plan = new MapCsgPlan();
			plan.StageHollow( Primitives.Box( 4, 4, 4 ), 100f, "too-thick" );
			plan.StageSubtract( Primitives.Box( 4, 4, 4 ), Primitives.Box( 2, 2, 2 ), "room-cut" );
			var failed = plan.Solve( new MutatingRefusingBoolean() );
			Require( !failed.Ok && failed.Results.Count == 2, "a plan with a failing provider must report not-ok with 2 results" );
			Require( !failed.Results[0].Ok && failed.Results[0].Reason == "hollow-too-thick",
				$"staged too-thick hollow must fail with hollow-too-thick, got {failed.Results[0].Reason}" );
			Require( !failed.Results[1].Ok && failed.Results[1].Reason == "csg-provider-refused",
				$"staged subtract with a refusing provider must fail with csg-provider-refused, got {failed.Results[1].Reason}" );
			// The staged snapshots survived the failed bake: re-baking with
			// a real provider solves the subtract and still refuses the
			// hollow the same way — the failure did not eat the plan.
			var baked = plan.Solve( ExactMeshBoolean.Instance );
			Require( !baked.Ok, "re-bake must still report the too-thick hollow as failed" );
			Require( !baked.Results[0].Ok && baked.Results[0].Reason == "hollow-too-thick",
				$"re-baked hollow must still fail with hollow-too-thick, got {baked.Results[0].Reason}" );
			Require( baked.Results[1].Ok, $"re-baked subtract refused: {baked.Results[1].Reason} {baked.Results[1].Message}" );
			RequireVolume( baked.Results[1].Mesh!, 56f, "re-baked plan subtract" );
		} );

		Check( "deferred plan stages cuts and solves only at bake", () =>
		{
			var plan = new MapCsgPlan();
			var target = Primitives.Box( 4, 4, 4 );
			plan.StageSubtract( target, Primitives.Box( 2, 2, 2 ), "room-cut" );
			plan.StageMerge( Primitives.Box( 2, 2, 2 ), Moved( Primitives.Box( 2, 2, 2 ), 2, 0, 0 ), "join" );
			plan.StageHollow( Primitives.Box( 16, 16, 16 ), 2f, "room" );
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
			Require( baked.Ok, $"bake refused: {baked.Results[0].Reason} {baked.Results[0].Message}" );
			RequireVolume( baked.Results[0].Mesh!, 56f, "plan subtract" );
			RequireVolume( baked.Results[1].Mesh!, 16f, "plan merge" );
			RequireVolume( baked.Results[2].Mesh!, 2368f, "plan hollow" );
			Require( plan.PendingCount == 3, "solve does not consume the plan; Clear() is explicit" );
			plan.Clear();
			Require( plan.PendingCount == 0, "Clear() must empty the plan" );
		} );

		Check( "no Carve operation exists", () =>
		{
			foreach ( var type in new[] { typeof( MapCsg ), typeof( MapCsgPlan ), typeof( CsgResult ), typeof( CsgPendingOp ), typeof( CsgSolveResult ), typeof( CsgOpKind ), typeof( BrushPlane ) } )
				foreach ( var m in type.GetMembers( BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly ) )
					Require( !m.Name.Contains( "Carve", StringComparison.OrdinalIgnoreCase ),
						$"{type.Name} exposes a member named {m.Name}: Carve must not exist" );
		} );

		Check( "open null and empty inputs refuse with named reasons", () =>
		{
			var n = MapCsg.TrySubtract( null!, Primitives.Box( 2, 2, 2 ) );
			Require( !n.Ok && n.Reason == "csg-input-null", $"null target must refuse with csg-input-null, got {n.Reason}" );
			var e = MapCsg.TrySubtract( new PolyMesh(), Primitives.Box( 2, 2, 2 ) );
			Require( !e.Ok && e.Reason == "csg-input-empty", $"empty target must refuse with csg-input-empty, got {e.Reason}" );
			var open = Primitives.Box( 2, 2, 2 );
			open.Faces.RemoveAt( 0 );
			var o = MapCsg.TrySubtract( open, Primitives.Box( 2, 2, 2 ) );
			Require( !o.Ok && o.Reason == "csg-input-open", $"open target must refuse with csg-input-open, got {o.Reason}" );
			var h = MapCsg.TryHollow( null!, 1f );
			Require( !h.Ok && h.Reason == "csg-input-null", $"null hollow input must refuse with csg-input-null, got {h.Reason}" );
		} );

		registry.Add( "mapcsg", "fixture acceptance cases match computed volumes", () =>
		{
			string path = FindFixture( "cases.json" )!;
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var cases = doc.RootElement.GetProperty( "cases" );
			int ran = 0;
			foreach ( var c in cases.EnumerateArray() )
			{
				string name = c.GetProperty( "name" ).GetString()!;
				string op = c.GetProperty( "op" ).GetString()!;
				var t = c.GetProperty( "target" );
				var target = Primitives.Box( t[0].GetSingle(), t[1].GetSingle(), t[2].GetSingle() );
				float expected = c.GetProperty( "expectedVolume" ).GetSingle();
				CsgResult r = op switch
				{
					"subtract" => MapCsg.TrySubtract( target, Moved( BoxOf( c ), OffX( c ), OffY( c ), OffZ( c ) ) ),
					"merge" => MapCsg.TryMerge( target, Moved( BoxOf( c ), OffX( c ), OffY( c ), OffZ( c ) ) ),
					"hollow" => MapCsg.TryHollow( target, c.GetProperty( "thickness" ).GetSingle() ),
					_ => throw new Exception( $"fixture case {name} has unknown op '{op}'" ),
				};
				Require( r.Ok, $"fixture case {name} refused: {r.Reason} {r.Message}" );
				RequireVolume( r.Mesh!, expected, $"fixture case {name}" );
				if ( c.TryGetProperty( "expectedFaces", out var faces ) )
					Require( r.Mesh!.FaceCount == faces.GetInt32(), $"fixture case {name} should have {faces.GetInt32()} faces, has {r.Mesh.FaceCount}" );
				if ( c.TryGetProperty( "convex", out var convex ) )
					Require( MapCsg.IsConvex( r.Mesh! ) == convex.GetBoolean(), $"fixture case {name} convexity mismatch" );
				ran++;
			}
			Require( ran == 4, $"expected 4 fixture cases, ran {ran}" );

			static PolyMesh BoxOf( JsonElement c )
			{
				var tool = c.GetProperty( "tool" );
				return Primitives.Box( tool[0].GetSingle(), tool[1].GetSingle(), tool[2].GetSingle() );
			}
			static float OffX( JsonElement c ) => c.GetProperty( "toolOffset" )[0].GetSingle();
			static float OffY( JsonElement c ) => c.GetProperty( "toolOffset" )[1].GetSingle();
			static float OffZ( JsonElement c ) => c.GetProperty( "toolOffset" )[2].GetSingle();
		}, missing: () => FindFixture( "cases.json" ) is null
			? "cases.json fixture not found under tests/fixtures/hammer/hammer-brush-csg-subtract-merge-hollow/"
			: null );

		registry.Add( "mapcsg", "fixture failure cases leave inputs intact with named reasons", () =>
		{
			// Board hammer-csg-failure-safety. Every case in the shared
			// failure fixture must fail with its expected named reason,
			// return no mesh, and leave its inputs byte-identical to
			// snapshots taken before the call. Intersect cases live in the
			// same file but are owned by the mapcsgintersect group, so
			// this check skips them (and asserts the skip count).
			string path = FindFailureFixture( "cases.json" )!;
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var cases = doc.RootElement.GetProperty( "cases" );
			int ran = 0;
			int skippedIntersect = 0;
			foreach ( var c in cases.EnumerateArray() )
			{
				string name = c.GetProperty( "name" ).GetString()!;
				string op = c.GetProperty( "op" ).GetString()!;
				if ( op == "intersect" ) { skippedIntersect++; continue; }
				var t = c.GetProperty( "target" );
				var target = Primitives.Box( t[0].GetSingle(), t[1].GetSingle(), t[2].GetSingle() );
				var targetBefore = target.Clone();
				PolyMesh? tool = null;
				PolyMesh? toolBefore = null;
				if ( c.TryGetProperty( "tool", out _ ) )
				{
					tool = Moved( BoxOf( c ), OffX( c ), OffY( c ), OffZ( c ) );
					toolBefore = tool.Clone();
				}
				string expected = c.GetProperty( "expectedReason" ).GetString()!;
				bool noProvider = c.TryGetProperty( "provider", out var prov ) && prov.GetString() == "none";
				var saved = MeshBoolean.Provider;
				if ( noProvider ) MeshBoolean.Provider = null;
				CsgResult r;
				try
				{
					r = op switch
					{
						"subtract" => MapCsg.TrySubtract( target, tool! ),
						"merge" => MapCsg.TryMerge( target, tool! ),
						"hollow" => MapCsg.TryHollow( target, c.GetProperty( "thickness" ).GetSingle() ),
						_ => throw new Exception( $"fixture failure case {name} has unknown op '{op}'" ),
					};
				}
				finally { MeshBoolean.Provider = saved; }
				Require( !r.Ok && r.Reason == expected,
					$"fixture failure case {name} must fail with {expected}, got ok={r.Ok} reason={r.Reason}" );
				Require( r.Mesh is null, $"fixture failure case {name} must not return a mesh" );
				RequireUntouched( targetBefore, target, $"fixture failure case {name} target" );
				if ( toolBefore != null ) RequireUntouched( toolBefore, tool!, $"fixture failure case {name} tool" );
				ran++;
			}
			Require( ran == 4, $"expected 4 non-intersect fixture failure cases, ran {ran}" );
			Require( skippedIntersect == 2, $"expected 2 intersect fixture failure cases for the other group, skipped {skippedIntersect}" );

			static PolyMesh BoxOf( JsonElement c )
			{
				var tool = c.GetProperty( "tool" );
				return Primitives.Box( tool[0].GetSingle(), tool[1].GetSingle(), tool[2].GetSingle() );
			}
			static float OffX( JsonElement c ) => c.GetProperty( "toolOffset" )[0].GetSingle();
			static float OffY( JsonElement c ) => c.GetProperty( "toolOffset" )[1].GetSingle();
			static float OffZ( JsonElement c ) => c.GetProperty( "toolOffset" )[2].GetSingle();
		}, missing: () => FindFailureFixture( "cases.json" ) is null
			? "cases.json fixture not found under tests/fixtures/hammer/hammer-csg-failure-safety/"
			: null );
	}

	// -- Failure-safety provider doubles (board hammer-csg-failure-safety).
	// Each one misbehaves the way a real host/engine provider could: they
	// destroy their inputs before refusing/throwing, or answer success
	// with a degenerate result. MapCsg must never hand them the caller's
	// meshes (Apply() clones first), so these doubles can only ever damage
	// the work copies.

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

	/// <summary>Answers success with an empty mesh: a degenerate result.</summary>
	sealed class DegenerateResultBoolean : IMeshBoolean
	{
		public bool TryApply( BooleanOp op, PolyMesh target, PolyMesh tool, out PolyMesh result, out string error )
		{
			result = new PolyMesh();
			error = null!;
			return true;
		}
	}

	/// <summary>Answers success with an open mesh (one face removed).</summary>
	sealed class OpenResultBoolean : IMeshBoolean
	{
		public bool TryApply( BooleanOp op, PolyMesh target, PolyMesh tool, out PolyMesh result, out string error )
		{
			result = Primitives.Box( 2, 2, 2 );
			result.Faces.RemoveAt( 0 );
			error = null!;
			return true;
		}
	}
}
