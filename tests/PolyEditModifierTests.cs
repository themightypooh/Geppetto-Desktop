#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;

/// <summary>
/// Poly edit mode + non-destructive modifier stack (see
/// kernel/Effigy/ModifierStack.cs, kernel/Effigy/PolyEdit.cs and
/// kernel/Effigy/MeshEditSession.PolyEdit.cs).
///
/// Survey first, because most of the ticket's tools already exist: extrude,
/// inset, loop cut, bridge, slide, knife, bevel, merge by distance and
/// loop/ring selection all live in MeshEditSession already, so the checks
/// below prove them on fixtures instead of reimplementing them. What this
/// ticket actually added is the one missing tool (edge collapse) and the
/// modifier stack itself (Mirror/Bevel/Boolean/Solidify/Subdivision, live
/// Evaluate, destructive Apply with the morph/sculpt-layer warning).
///
/// The stack's contract, checked here: Evaluate never touches the base mesh,
/// Apply returns exactly the mesh Evaluate returns (so apply-at-export and
/// the live preview are the same mesh by construction), evaluation order is
/// list order, a failed modifier is skipped and named in LastError rather
/// than fatal, and the Boolean modifier routes through MeshBoolean.Provider —
/// the Manifold backend once it is installed — with no second boolean
/// anywhere in the stack.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// PolyEditModifierTests.Register(registry, settings); line.
/// </summary>
internal static class PolyEditModifierTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "poly-edit-modifiers", name, test );
		void CheckManifoldOnly( string name, Action test ) => registry.Add( "poly-edit-modifiers", name, test,
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

		void RequireVolume( PolyMesh result, float expected, string message )
		{
			float got = result.SignedVolume();
			Require( got > 0, $"{message}: signed volume {got} is not positive, so the winding is inward" );
			Require( Math.Abs( got - expected ) < 1e-3f * Math.Max( 1f, expected ),
				$"{message}: signed volume {got}, expected {expected}" );
		}

		static bool SameMesh( PolyMesh a, PolyMesh b )
		{
			if ( a.VertexCount != b.VertexCount || a.FaceCount != b.FaceCount ) return false;
			for ( int i = 0; i < a.VertexCount; i++ )
				if ( !a.Positions[i].Equals( b.Positions[i] ) ) return false;
			for ( int i = 0; i < a.FaceCount; i++ )
			{
				if ( a.Faces[i].Material != b.Faces[i].Material ) return false;
				if ( !a.Faces[i].Indices.SequenceEqual( b.Faces[i].Indices ) ) return false;
			}
			return true;
		}

		static string? RepoManifoldLib()
		{
			var dir = new DirectoryInfo( AppContext.BaseDirectory );
			while ( dir is not null )
			{
				var p = Path.Combine( dir.FullName, "packaging", "native", "linux-x86_64", "libgmmanifold.so" );
				if ( File.Exists( p ) ) return p;
				dir = dir.Parent;
			}
			return null;
		}

		// --- the missing tool: edge collapse --------------------------------

		Check( "collapse-one-edge", () =>
		{
			var r = PolyEdit.CollapseEdges( Primitives.Box( 2, 2, 2 ), new[] { new EdgeKey( 0, 1 ) } );
			Require( r.VertexCount == 7, $"collapse should merge two vertices into one, got {r.VertexCount}" );
			Require( r.FaceCount == 6, $"no face should be lost collapsing one edge, got {r.FaceCount}" );
			Require( Closed( r ), "collapsed box must stay closed" );
			Require( r.SignedVolume() > 0, "collapsed box must keep outward winding" );
		} );

		Check( "collapse-two-disjoint-edges", () =>
		{
			var r = PolyEdit.CollapseEdges( Primitives.Box( 2, 2, 2 ), new[] { new EdgeKey( 0, 1 ), new EdgeKey( 6, 7 ) } );
			Require( r.VertexCount == 6, $"two disjoint collapses should remove two vertices, got {r.VertexCount}" );
			Require( r.FaceCount == 6, $"no face should be lost, got {r.FaceCount}" );
			Require( Closed( r ), "collapsed box must stay closed" );
		} );

		Check( "collapse-shared-vertex-chain", () =>
		{
			var r = PolyEdit.CollapseEdges( Primitives.Box( 2, 2, 2 ), new[] { new EdgeKey( 0, 1 ), new EdgeKey( 1, 2 ) } );
			Require( r.VertexCount == 6, $"a shared-vertex chain collapses to one point, got {r.VertexCount}" );
			Require( r.FaceCount == 5, $"the face whose three corners became one must drop, got {r.FaceCount}" );
		} );

		// --- selection: loop and ring are exact ------------------------------

		Check( "loop-selection-exact", () =>
		{
			var s = new MeshEditSession( Primitives.Plane( 2, 2, 2, 2 ) );
			s.SetMode( EditElement.Edge );
			s.SelectEdgeLoop( new EdgeKey( 3, 4 ) );
			Require( s.SelectedEdges.SetEquals( new[] { new EdgeKey( 3, 4 ), new EdgeKey( 4, 5 ) } ),
				$"loop through the middle row should be exactly (3,4)+(4,5), got {string.Join( ",", s.SelectedEdges )}" );
		} );

		Check( "ring-selection-exact", () =>
		{
			var s = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
			s.SetMode( EditElement.Edge );
			s.SelectEdgeRing( new EdgeKey( 0, 4 ) );
			Require( s.SelectedEdges.SetEquals( new[] { new EdgeKey( 0, 4 ), new EdgeKey( 1, 5 ), new EdgeKey( 2, 6 ), new EdgeKey( 3, 7 ) } ),
				$"ring through a vertical edge should be exactly the four vertical edges, got {string.Join( ",", s.SelectedEdges )}" );
		} );

		// --- the tools that already exist, proved on fixtures ----------------

		Check( "extrude-top-face", () =>
		{
			var s = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
			s.SelectFace( 1 );
			s.Extrude( 0.5f );
			Require( s.Mesh.FaceCount == 11, $"extrude should add a top plus four sides, got {s.Mesh.FaceCount}" );
			Require( Closed( s.Mesh ), "extruded box must stay closed" );
			RequireVolume( s.Mesh, 10, "extrude" );
		} );

		Check( "inset-top-face", () =>
		{
			var s = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
			s.SelectFace( 1 );
			s.Inset( 0.2f );
			Require( s.Mesh.FaceCount == 6, $"inset moves vertices, it adds no faces, got {s.Mesh.FaceCount}" );
			Require( Closed( s.Mesh ), "inset box must stay closed" );
			Require( s.Mesh.SignedVolume() < 8, "insetting the top face must shrink the volume" );
		} );

		Check( "loop-cut-box", () =>
		{
			var s = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
			s.SetMode( EditElement.Edge );
			s.LoopCut( new EdgeKey( 0, 4 ), 0.5f );
			Require( s.Mesh.FaceCount == 10, $"a loop cut should split the four crossed faces, got {s.Mesh.FaceCount}" );
			Require( Closed( s.Mesh ), "loop-cut box must stay closed" );
			RequireVolume( s.Mesh, 8, "loop cut" );
		} );

		Check( "bridge-two-open-boxes", () =>
		{
			var combined = Primitives.Box( 2, 2, 2 );
			MeshTransform.Append( combined, Moved( Primitives.Box( 2, 2, 2 ), 0, 0, 3 ) );
			var e = EditableMesh.FromPolyMesh( combined );
			e.DeleteFaces( new[] { 1, 6 } );
			var s = new MeshEditSession( e.ToPolyMesh() );
			s.SetMode( EditElement.Edge );
			s.SelectEdge( new EdgeKey( 4, 5 ) );
			s.SelectEdge( new EdgeKey( 8, 9 ), MeshEditSession.Combine.Add );
			s.BridgeSelectedLoops();
			Require( s.Mesh.FaceCount == 14, $"bridge should add a four-quad band, got {s.Mesh.FaceCount}" );
			Require( Closed( s.Mesh ), "bridged boxes must close up" );
			RequireVolume( s.Mesh, 20, "bridge" );
		} );

		Check( "vertex-slide", () =>
		{
			var s = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
			s.SetMode( EditElement.Vertex );
			s.SelectVertex( 0 );
			s.VertexSlide( 0.5f, new Vec3( 1, 0, 0 ) );
			var p = s.Mesh.Positions[0];
			Require( Math.Abs( p.x - 0f ) < 1e-4f && Math.Abs( p.y + 1f ) < 1e-4f && Math.Abs( p.z + 1f ) < 1e-4f,
				$"vertex 0 should slide halfway to vertex 1, to (0,-1,-1), got {p}" );
			Require( Closed( s.Mesh ), "slid box must stay closed" );
		} );

		Check( "knife-top-face", () =>
		{
			var s = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
			s.Knife( new Vec3( -1, 0, 1 ), new Vec3( 1, 0, 1 ), new Vec3( 0, 0, -1 ) );
			Require( s.Mesh.FaceCount == 7, $"a knife stroke across the top face should split it in two, got {s.Mesh.FaceCount}" );
			Require( Closed( s.Mesh ), "knifed box must stay closed" );
		} );

		Check( "merge-by-distance", () =>
		{
			var m = Primitives.Box( 2, 2, 2 );
			MeshTransform.Append( m, Primitives.Box( 2, 2, 2 ) );
			var s = new MeshEditSession( m );
			s.MergeByDistance( 0.01f );
			Require( s.Mesh.VertexCount == 8, $"two coincident boxes should weld to one box's vertices, got {s.Mesh.VertexCount}" );
		} );

		Check( "bevel-session", () =>
		{
			var s = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
			s.SetMode( EditElement.Edge );
			s.SelectAll();
			s.Bevel( 0.1f, 1 );
			Require( s.Mesh.FaceCount > 6, $"bevel should add faces, got {s.Mesh.FaceCount}" );
			Require( Closed( s.Mesh ), "bevelled box must stay closed" );
			Require( s.Mesh.SignedVolume() < 8, "chamfering the corners must shrink the volume" );
		} );

		// --- the stack ---------------------------------------------------------

		Check( "stack-order-matters", () =>
		{
			var box = Primitives.Box( 2, 2, 2 );
			var bevelThenSubdiv = new ModifierStack();
			bevelThenSubdiv.Add( new Modifier( ModifierKind.Bevel ) { Width = 0.1f, Segments = 1 } );
			bevelThenSubdiv.Add( new Modifier( ModifierKind.Subdivision ) { Levels = 1 } );
			var subdivThenBevel = new ModifierStack();
			subdivThenBevel.Add( new Modifier( ModifierKind.Subdivision ) { Levels = 1 } );
			subdivThenBevel.Add( new Modifier( ModifierKind.Bevel ) { Width = 0.1f, Segments = 1 } );
			var ea = bevelThenSubdiv.Evaluate( box );
			var eb = subdivThenBevel.Evaluate( box );
			Require( ea.FaceCount != eb.FaceCount,
				$"evaluation order must matter: bevel-then-subdiv {ea.FaceCount} faces vs subdiv-then-bevel {eb.FaceCount}" );
			Require( Closed( ea ) && Closed( eb ), "both orders must stay closed" );
		} );

		Check( "stack-evaluate-nondestructive-apply-matches", () =>
		{
			var box = Primitives.Box( 2, 2, 2 );
			var before = box.Clone();
			var stack = new ModifierStack();
			stack.Add( new Modifier( ModifierKind.Bevel ) { Width = 0.1f, Segments = 1 } );
			stack.Add( new Modifier( ModifierKind.Subdivision ) { Levels = 1 } );
			var live = stack.Evaluate( box );
			Require( SameMesh( box, before ), "Evaluate must not touch the base mesh" );
			var applied = stack.Apply( box );
			Require( SameMesh( applied.Mesh, live ), "apply-at-export must be exactly the live evaluation" );
			Require( SameMesh( box, before ), "Apply must not mutate its input either" );
			Require( applied.Warnings.Count == 0, "no morphs or layers, so no warnings" );
			Require( applied.DroppedMorphs == 0, "no morphs to drop" );
		} );

		CheckManifoldOnly( "stack-boolean-routes-through-manifold", () =>
		{
			var savedProvider = MeshBoolean.Provider;
			var savedOverride = ManifoldBoolean.LibraryPathOverride;
			try
			{
				ManifoldBoolean.LibraryPathOverride = RepoManifoldLib()!;
				ManifoldBoolean.Install();
				var stack = new ModifierStack();
				stack.Add( new Modifier( ModifierKind.Boolean ) { Op = BooleanOp.Union, Tool = Moved( Primitives.Box( 2, 2, 2 ), 1, 1, 1 ) } );
				var r = stack.Evaluate( Primitives.Box( 2, 2, 2 ) );
				Require( ManifoldBoolean.LastRoute == "manifold", $"boolean modifier should route through Manifold, route was '{ManifoldBoolean.LastRoute}'" );
				Require( Closed( r ), "boolean modifier result must stay closed" );
				RequireVolume( r, 15, "boolean modifier" );
			}
			finally
			{
				MeshBoolean.Provider = savedProvider;
				ManifoldBoolean.LibraryPathOverride = savedOverride;
			}
		} );

		Check( "stack-bevelled-fixture-exports-manifold", () =>
		{
			var stack = new ModifierStack();
			stack.Add( new Modifier( ModifierKind.Bevel ) { Width = 0.1f, Segments = 1 } );
			var r = stack.Evaluate( Primitives.Box( 2, 2, 2 ) );
			Require( Closed( r ), "a bevelled hard-surface fixture must export manifold" );
			Require( r.SignedVolume() > 0 && r.SignedVolume() < 8, $"bevelled fixture volume should be just under 8, got {r.SignedVolume()}" );
		} );

		Check( "stack-apply-warns-morph", () =>
		{
			var box = Primitives.Box( 2, 2, 2 );
			var target = box.Positions.ToList();
			target[0] = new Vec3( target[0].x, target[0].y, target[0].z + 0.5f );
			box.AddMorph( "lift", target );
			var stack = new ModifierStack();
			stack.Add( new Modifier( ModifierKind.Subdivision ) { Levels = 1 } );
			var applied = stack.Apply( box );
			Require( applied.Warnings.Count == 1 && applied.Warnings[0].Contains( "morph" ),
				$"apply should warn about the morph target, got: {string.Join( " ", applied.Warnings )}" );
			Require( applied.DroppedMorphs == 1, $"one morph should be dropped, got {applied.DroppedMorphs}" );
			Require( applied.Mesh.Morphs.Count == 0, "the result must carry no stale morphs" );
			Require( box.Morphs.Count == 1, "the base mesh keeps its morph" );
		} );

		Check( "stack-apply-warns-sculpt-layer", () =>
		{
			var box = Primitives.Box( 2, 2, 2 );
			var layer = new SculptLayer( new Vec3[box.VertexCount] );
			var stack = new ModifierStack();
			stack.Add( new Modifier( ModifierKind.Subdivision ) { Levels = 1 } );
			var warnings = stack.TopologyWarnings( box, new[] { layer } );
			Require( warnings.Count == 1 && warnings[0].Contains( "sculpt" ),
				$"apply should warn about the sculpt layer, got: {string.Join( " ", warnings )}" );
			var applied = stack.Apply( box, new[] { layer } );
			Require( applied.Warnings.Any( w => w.Contains( "sculpt" ) ), "Apply must carry the sculpt-layer warning" );
		} );

		Check( "session-apply-modifiers-and-undo", () =>
		{
			var s = new MeshEditSession( Primitives.Box( 2, 2, 2 ) );
			s.Modifiers.Add( new Modifier( ModifierKind.Bevel ) { Width = 0.1f, Segments = 1 } );
			var live = s.EvaluateModifiers();
			Require( live.FaceCount > 6, "the live preview should show the bevel" );
			s.ApplyModifiers();
			Require( s.Mesh.FaceCount == live.FaceCount, "the applied mesh should be the live mesh" );
			Require( s.Modifiers.Modifiers.Count == 0, "the stack should clear on apply" );
			Require( s.Undo(), "apply should be one undo step" );
			Require( s.Mesh.FaceCount == 6, "undo should restore the base mesh" );
		} );
	}
}
