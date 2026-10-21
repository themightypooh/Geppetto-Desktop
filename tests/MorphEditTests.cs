#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Effigy;
using Marionette.Desktop;

internal static class MorphEditTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "morph-edit", name, test );
		static void Require( bool condition, string message )
		{
			if ( !condition ) throw new Exception( message );
		}
		static bool Near( Vec3 a, Vec3 b, float eps = 1e-5f ) =>
			MathF.Abs( a.x - b.x ) <= eps && MathF.Abs( a.y - b.y ) <= eps && MathF.Abs( a.z - b.z ) <= eps;
		static Vec3 MirrorX( Vec3 v ) => new( -v.x, v.y, v.z );
		static void Throws<T>( string what, Action action ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, nothing was thrown" );
		}

		// Symmetric (2n+1)^2 grid on the XY plane, unit spacing, quads wound for a +Z normal.
		static PolyMesh Grid( int n = 3 )
		{
			var mesh = new PolyMesh();
			int side = 2 * n + 1;
			for ( int y = -n; y <= n; y++ )
				for ( int x = -n; x <= n; x++ )
					mesh.Positions.Add( new Vec3( x, y, 0 ) );
			int Id( int x, int y ) => (y + n) * side + (x + n);
			for ( int y = -n; y < n; y++ )
				for ( int x = -n; x < n; x++ )
					mesh.Faces.Add( new Face(
						new[] { Id( x, y ), Id( x + 1, y ), Id( x + 1, y + 1 ), Id( x, y + 1 ) },
						new[] { new Vec2( x, y ), new Vec2( x + 1, y ), new Vec2( x + 1, y + 1 ), new Vec2( x, y + 1 ) } ) );
			return mesh;
		}
		static int GridIndex( int x, int y, int n = 3 ) => (y + n) * (2 * n + 1) + (x + n);
		static int FindVertex( PolyMesh m, Vec3 p ) => m.Positions.FindIndex( q => q.Equals( p ) );

		// Grid split along x = 0: faces right of the line use duplicated vertices (an imported UV seam).
		static PolyMesh SplitGrid( out List<(int A, int B)> pairs )
		{
			var mesh = Grid( 3 );
			pairs = new();
			var dup = new Dictionary<int, int>();
			for ( int y = -3; y <= 3; y++ )
			{
				int a = GridIndex( 0, y );
				int b = mesh.Positions.Count;
				mesh.Positions.Add( mesh.Positions[a] );
				dup[a] = b;
				pairs.Add( (a, b) );
			}
			foreach ( var f in mesh.Faces )
			{
				bool right = f.Indices.All( i => mesh.Positions[i].x >= 0 );
				if ( !right ) continue;
				for ( int c = 0; c < f.Indices.Length; c++ )
					if ( dup.TryGetValue( f.Indices[c], out var b ) ) f.Indices[c] = b;
			}
			return mesh;
		}

		Check( "move stroke writes deltas and preview applies them at weight", () =>
		{
			var mesh = Grid();
			var svc = new MorphEditService( mesh, new EditHistory() );
			svc.CreateTarget( "bulge" );
			Require( svc.ActiveTarget == "bulge", "first target becomes active" );
			bool changed = svc.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Move, 1.5f, 0.5f, BrushFalloff.Constant ),
				new[] { (Vec3.Zero, new Vec3( 0, 0, 2 )) } );
			Require( changed, "stroke reports a change" );

			var d = svc.GetDeltas( "bulge" );
			for ( int i = 0; i < mesh.Positions.Count; i++ )
			{
				bool inside = mesh.Positions[i].Length < 1.5f;
				var expect = inside ? new Vec3( 0, 0, 1 ) : Vec3.Zero;
				Require( d[i].Equals( expect ), $"vertex {i} {mesh.Positions[i]}: delta {d[i]} expected {expect}" );
			}
			Require( mesh.Morphs.Single().Deltas.SequenceEqual( d ), "deltas live on PolyMesh.Morphs (A2 representation)" );

			var outPos = new Vec3[mesh.Positions.Count];
			svc.Preview( new Dictionary<string, float> { ["bulge"] = 0.5f }, outPos );
			for ( int i = 0; i < outPos.Length; i++ )
				Require( Near( outPos[i], mesh.Positions[i] + d[i] * 0.5f ), $"preview 0.5 at {i}" );
			svc.Preview( new Dictionary<string, float> { ["bulge"] = 0f }, outPos );
			for ( int i = 0; i < outPos.Length; i++ )
				Require( outPos[i].Equals( mesh.Positions[i] ), $"preview 0 restores base exactly at {i}" );

			// The A2 evaluator over the service's retained morphs agrees.
			var eval = svc.CreateEvaluator();
			var viaEval = new Vec3[outPos.Length];
			MorphEvaluator.DeformPositions( mesh.Positions.ToArray(), new Dictionary<string, float> { ["bulge"] = 1f }, eval.Morphs, viaEval );
			for ( int i = 0; i < outPos.Length; i++ )
				Require( viaEval[i].Equals( mesh.Positions[i] + d[i] ), $"MorphEvaluator sees sculpted delta at {i}" );
		} );

		Check( "falloff, strength and move-from-stroke-start", () =>
		{
			var mesh = Grid();
			var svc = new MorphEditService( mesh, new EditHistory() );
			svc.CreateTarget( "f" );
			svc.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Move, 2f, 1f, BrushFalloff.Linear ),
				new[] { (Vec3.Zero, new Vec3( 0, 0, 1 )) } );
			var d = svc.GetDeltas( "f" );
			Require( Near( d[GridIndex( 0, 0 )], new Vec3( 0, 0, 1 ) ), "centre full" );
			Require( Near( d[GridIndex( 1, 0 )], new Vec3( 0, 0, 0.5f ) ), "half radius linear = 0.5" );
			Require( d[GridIndex( 2, 0 )].Equals( Vec3.Zero ), "at radius = 0" );

			// Two dabs in one stroke: Move measures falloff on the stroke-start shape, so the lifted
			// centre (now at z=1) is still found by a dab at the original surface point.
			var svc2 = new MorphEditService( Grid(), new EditHistory() );
			svc2.CreateTarget( "g" );
			var stroke = svc2.BeginStroke( new MorphBrushSettings( MorphBrushOp.Move, 0.5f, 1f, BrushFalloff.Constant ) );
			stroke.Dab( Vec3.Zero, new Vec3( 0, 0, 1 ) );
			stroke.Dab( Vec3.Zero, new Vec3( 0, 0, 1 ) );
			Require( stroke.Commit(), "commit" );
			Require( svc2.GetDeltas( "g" )[GridIndex( 0, 0 )].Equals( new Vec3( 0, 0, 2 ) ), "grab accumulates" );
		} );

		Check( "inflate pushes along the vertex normal", () =>
		{
			var mesh = Grid();
			var svc = new MorphEditService( mesh, new EditHistory() );
			svc.CreateTarget( "puff" );
			svc.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Inflate, 1.5f, 0.5f, BrushFalloff.Constant, InflateDistance: 2f ),
				new[] { (Vec3.Zero, Vec3.Zero) } );
			var normals = SkinStrain.VertexNormals( mesh.Positions, mesh.Faces );
			var d = svc.GetDeltas( "puff" );
			int c = GridIndex( 0, 0 );
			Require( Near( d[c], normals[c] * 1f ), $"centre moved along normal by 1: {d[c]} vs {normals[c]}" );
			Require( Near( d[c], new Vec3( 0, 0, 1 ) ), "flat +Z grid inflates along +Z" );
			Require( d[GridIndex( 3, 3 )].Equals( Vec3.Zero ), "outside radius untouched" );

			// Negative distance deflates.
			svc.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Inflate, 1.5f, 0.5f, BrushFalloff.Constant, InflateDistance: -2f ),
				new[] { (Vec3.Zero, Vec3.Zero) } );
			Require( Near( svc.GetDeltas( "puff" )[c], Vec3.Zero ), "deflate returns centre" );
		} );

		Check( "smooth relaxes a spike toward neighbours", () =>
		{
			var mesh = Grid();
			int c = GridIndex( 0, 0 );
			var init = new Vec3[mesh.Positions.Count];
			init[c] = new Vec3( 0, 0, 4 );
			var svc = new MorphEditService( mesh, new EditHistory() );
			svc.CreateTarget( "spike", init );
			// Falloff is measured on the shaped surface (the spike tip sits at z=4), so the radius covers it.
			svc.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Smooth, 5f, 1f, BrushFalloff.Constant ), new[] { (Vec3.Zero, Vec3.Zero) } );
			var d = svc.GetDeltas( "spike" );
			Require( d[c].z == 0f, $"centre replaced by neighbour average (all zero): {d[c]}" );
			Require( Near( d[GridIndex( 1, 0 )], new Vec3( 0, 0, 1 ) ), $"edge neighbour gets 4/4 = 1 (Jacobi snapshot): {d[GridIndex( 1, 0 )]}" );
			Require( d[GridIndex( 3, 3 )].Equals( Vec3.Zero ), "no neighbour delta => stays zero" );
			Require( d[GridIndex( 2, 2 )].Equals( Vec3.Zero ), "not adjacent to the spike => stays zero" );
		} );

		Check( "combined influence sums linearly and bake matches the mix", () =>
		{
			var mesh = Grid();
			var svc = new MorphEditService( mesh, new EditHistory() );
			var rnd = new Random( 7 );
			Vec3[] R() => Enumerable.Range( 0, mesh.Positions.Count ).Select( _ => new Vec3( (float)rnd.NextDouble() - .5f, (float)rnd.NextDouble() - .5f, (float)rnd.NextDouble() - .5f ) ).ToArray();
			var a = R(); var b = R(); var c = R();
			svc.CreateTarget( "a", a ); svc.CreateTarget( "b", b ); svc.CreateTarget( "c", c );
			var w = new Dictionary<string, float> { ["a"] = 0.3f, ["b"] = 0.6f, ["c"] = 1f };
			var mix = new Vec3[a.Length];
			svc.Preview( w, mix );
			for ( int i = 0; i < a.Length; i++ )
				Require( Near( mix[i], mesh.Positions[i] + a[i] * 0.3f + b[i] * 0.6f + c[i] ), $"linear sum at {i}" );

			// Superposition: (a at .3) + (b at .6) - base == mix - base.
			var pa = new Vec3[a.Length]; var pb = new Vec3[a.Length];
			svc.Preview( new Dictionary<string, float> { ["a"] = 0.3f }, pa );
			svc.Preview( new Dictionary<string, float> { ["b"] = 0.6f, ["c"] = 1f }, pb );
			for ( int i = 0; i < a.Length; i++ )
				Require( Near( pa[i] + pb[i] - mesh.Positions[i], mix[i] ), $"superposition at {i}" );

			// Default policy clamps like A2: weight 2 behaves as 1; overdrive policy doubles.
			var clamped = new Vec3[a.Length]; var over = new Vec3[a.Length];
			svc.Preview( new Dictionary<string, float> { ["a"] = 2f }, clamped );
			svc.Preview( new Dictionary<string, float> { ["a"] = 2f }, over, MorphPolicy.Overdrive );
			for ( int i = 0; i < a.Length; i++ )
			{
				Require( Near( clamped[i], mesh.Positions[i] + a[i] ), $"clamp at {i}" );
				Require( Near( over[i], mesh.Positions[i] + a[i] * 2f ), $"overdrive at {i}" );
			}

			svc.BakeMix( "mix", w );
			var baked = new Vec3[a.Length];
			svc.Preview( new Dictionary<string, float> { ["mix"] = 1f }, baked );
			for ( int i = 0; i < a.Length; i++ )
				Require( Near( baked[i], mix[i] ), $"baked target reproduces mix at {i}" );
			Require( svc.TargetNames.SequenceEqual( new[] { "a", "b", "c", "mix" } ), "bake appends a new target" );
			Require( svc.GetDeltas( "a" ).SequenceEqual( a ), "bake leaves sources untouched" );
		} );

		Check( "x-mirror stroke is exactly symmetric", () =>
		{
			foreach ( var op in new[] { MorphBrushOp.Move, MorphBrushOp.Inflate, MorphBrushOp.Smooth, MorphBrushOp.Erase } )
			{
				var mesh = Grid();
				var svc = new MorphEditService( mesh, new EditHistory() );
				// Symmetric starting shape so smooth/erase have something to work on.
				var init = mesh.Positions.Select( p => new Vec3( p.x * 0.1f, 0.05f * p.y, 0.3f * MathF.Abs( p.x ) + 0.1f * p.y * p.y ) ).ToArray();
				svc.CreateTarget( "m", init );
				var settings = new MorphBrushSettings( op, 1.6f, 0.7f, BrushFalloff.Smooth, MirrorX: true, InflateDistance: 0.8f );
				svc.ApplyStroke( settings, new[] { (new Vec3( 2, 0.5f, 0 ), new Vec3( 1, 0.5f, 1 )), (new Vec3( 1.5f, -1, 0 ), new Vec3( 0.25f, 0, 0.5f )) } );
				var d = svc.GetDeltas( "m" );
				Require( !d.SequenceEqual( init ), $"{op}: stroke changed something" );
				for ( int i = 0; i < d.Length; i++ )
				{
					int j = FindVertex( mesh, MirrorX( mesh.Positions[i] ) );
					Require( j >= 0, "grid is symmetric" );
					Require( Near( d[j], MirrorX( d[i] ), 1e-6f ), $"{op}: vertex {mesh.Positions[i]} {d[i]} vs mirror {d[j]}" );
				}
				// Mirrored side really moved, not just untouched-and-symmetric.
				int r = GridIndex( 2, 0 ), l = GridIndex( -2, 0 );
				Require( !d[r].Equals( init[r] ) && !d[l].Equals( init[l] ), $"{op}: both sides edited" );
			}

			// Without mirror only one side is edited.
			var mesh2 = Grid();
			var svc2 = new MorphEditService( mesh2, new EditHistory() );
			svc2.CreateTarget( "one" );
			svc2.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Move, 1.1f, 1f, BrushFalloff.Constant ), new[] { (new Vec3( 2, 0, 0 ), new Vec3( 1, 0.5f, 1 )) } );
			var e = svc2.GetDeltas( "one" );
			Require( e[GridIndex( 2, 0 )].Equals( new Vec3( 1, 0.5f, 1 ) ), "primary side" );
			Require( e[GridIndex( -2, 0 )].Equals( Vec3.Zero ), "no mirror => other side untouched" );

			// Mirror explicit values: +x gets offset, -x gets offset with x negated.
			var mesh3 = Grid();
			var svc3 = new MorphEditService( mesh3, new EditHistory() );
			svc3.CreateTarget( "two" );
			svc3.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Move, 1.1f, 1f, BrushFalloff.Constant, MirrorX: true ), new[] { (new Vec3( 2, 0, 0 ), new Vec3( 1, 0.5f, 1 )) } );
			var f = svc3.GetDeltas( "two" );
			Require( f[GridIndex( 2, 0 )].Equals( new Vec3( 1, 0.5f, 1 ) ), "mirror primary" );
			Require( f[GridIndex( -2, 0 )].Equals( new Vec3( -1, 0.5f, 1 ) ), "mirror copy" );
			// A stroke on the plane keeps on-plane vertices on the plane (x components cancel).
			svc3.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Move, 0.5f, 1f, BrushFalloff.Constant, MirrorX: true ), new[] { (Vec3.Zero, new Vec3( 1, 0, 1 )) } );
			Require( svc3.GetDeltas( "two" )[GridIndex( 0, 0 )].Equals( new Vec3( 0, 0, 2 ) ), "on-plane x cancels, y/z sum (both copies hit)" );
		} );

		Check( "erase restores neutral exactly", () =>
		{
			var mesh = Grid();
			var svc = new MorphEditService( mesh, new EditHistory() );
			svc.CreateTarget( "e" );
			svc.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Inflate, 3f, 1f, InflateDistance: 1.3f ), new[] { (new Vec3( 0.3f, 0.1f, 0 ), Vec3.Zero) } );
			svc.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Move, 2f, 0.8f ), new[] { (new Vec3( -1, 1, 0 ), new Vec3( 0.2f, 0.7f, -0.4f )) } );
			Require( svc.GetDeltas( "e" ).Any( v => !v.Equals( Vec3.Zero ) ), "sculpted" );

			// Partial erase scales by (1 - strength*falloff).
			var before = svc.GetDeltas( "e" );
			svc.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Erase, 100f, 0.25f, BrushFalloff.Constant ), new[] { (Vec3.Zero, Vec3.Zero) } );
			var partial = svc.GetDeltas( "e" );
			for ( int i = 0; i < partial.Length; i++ )
				Require( Near( partial[i], before[i] * 0.75f, 1e-6f ), $"partial erase at {i}" );

			svc.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Erase, 100f, 1f, BrushFalloff.Constant ), new[] { (Vec3.Zero, Vec3.Zero) } );
			Require( svc.GetDeltas( "e" ).All( v => v.Equals( Vec3.Zero ) ), "full erase writes exact zeros" );
			var outPos = new Vec3[mesh.Positions.Count];
			svc.Preview( new Dictionary<string, float> { ["e"] = 1f }, outPos );
			for ( int i = 0; i < outPos.Length; i++ )
				Require( outPos[i].Equals( mesh.Positions[i] ), $"neutral at weight 1, vertex {i}" );
		} );

		Check( "each stroke and op is one exact undo/redo entry", () =>
		{
			var mesh = Grid();
			var history = new EditHistory();
			var svc = new MorphEditService( mesh, history );
			svc.CreateTarget( "u" );
			Require( history.UndoCount == 1, "create = 1 entry" );
			var s0 = svc.GetDeltas( "u" );

			var stroke = svc.BeginStroke( new MorphBrushSettings( MorphBrushOp.Inflate, 1.7f, 0.37f, InflateDistance: 0.913f ) );
			for ( int k = 0; k < 5; k++ ) stroke.Dab( new Vec3( 0.21f * k, -0.13f * k, 0 ) );
			Require( stroke.Commit(), "stroke committed" );
			Require( history.UndoCount == 2, "5 dabs = 1 entry" );
			var s1 = svc.GetDeltas( "u" );
			svc.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Smooth, 2f, 0.6f, MirrorX: true ), new[] { (new Vec3( 0.5f, 0, 0 ), Vec3.Zero) } );
			var s2 = svc.GetDeltas( "u" );
			Require( !s1.SequenceEqual( s2 ) && history.UndoCount == 3, "second stroke" );

			int rev = svc.Revision;
			history.Undo();
			Require( svc.GetDeltas( "u" ).SequenceEqual( s1 ), "undo stroke 2 exact" );
			Require( svc.Revision > rev, "undo bumps revision" );
			history.Undo();
			Require( svc.GetDeltas( "u" ).SequenceEqual( s0 ), "undo stroke 1 exact" );
			history.Redo();
			Require( svc.GetDeltas( "u" ).SequenceEqual( s1 ), "redo stroke 1 exact" );
			history.Redo();
			Require( svc.GetDeltas( "u" ).SequenceEqual( s2 ), "redo stroke 2 exact" );

			// Preview cache follows undo.
			var outPos = new Vec3[mesh.Positions.Count];
			history.Undo();
			svc.Preview( new Dictionary<string, float> { ["u"] = 1f }, outPos );
			for ( int i = 0; i < outPos.Length; i++ ) Require( outPos[i].Equals( mesh.Positions[i] + s1[i] ), $"preview after undo at {i}" );
			history.Redo();

			// Cancel restores and records nothing; a dab that misses records nothing.
			int count = history.UndoCount;
			var cancel = svc.BeginStroke( new MorphBrushSettings( MorphBrushOp.Move, 1f ) );
			cancel.Dab( Vec3.Zero, new Vec3( 3, 3, 3 ) );
			cancel.Cancel();
			Require( svc.GetDeltas( "u" ).SequenceEqual( s2 ) && history.UndoCount == count, "cancel exact, no entry" );
			Require( !svc.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Move, 0.5f ), new[] { (new Vec3( 50, 50, 0 ), new Vec3( 1, 0, 0 )) } ), "miss = no change" );
			Require( history.UndoCount == count, "miss = no entry" );

			// Structural ops.
			svc.DuplicateTarget( "u", "u2" );
			svc.RenameTarget( "u2", "copy" );
			svc.CreateTarget( "z" );
			svc.DeleteTarget( "u" );
			Require( svc.TargetNames.SequenceEqual( new[] { "copy", "z" } ), $"after ops: {string.Join( ",", svc.TargetNames )}" );
			Require( svc.GetDeltas( "copy" ).SequenceEqual( s2 ), "duplicate copies deltas" );
			history.Undo(); // delete
			Require( svc.TargetNames.SequenceEqual( new[] { "u", "copy", "z" } ), "undo delete reinserts at original index" );
			Require( svc.ActiveTarget == "u", "active restored with the same object" );
			history.Undo(); // create z
			history.Undo(); // rename
			Require( svc.TargetNames.SequenceEqual( new[] { "u", "u2" } ), "undo rename" );
			history.Undo(); // duplicate
			Require( svc.TargetNames.SequenceEqual( new[] { "u" } ), "undo duplicate" );
			history.Redo(); history.Redo(); history.Redo(); history.Redo();
			Require( svc.TargetNames.SequenceEqual( new[] { "copy", "z" } ), "redo all structural ops" );
			Require( svc.ActiveTarget == null, "active target deleted => none" );
			Throws<InvalidOperationException>( "stroke with no active", () => svc.BeginStroke( new MorphBrushSettings( MorphBrushOp.Move, 1f ) ) );

			// Mixed: undo of a duplicate's stroke does not affect its source.
			svc.SetActiveTarget( "copy" );
			var copyBefore = svc.GetDeltas( "copy" );
			svc.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Erase, 100f ), new[] { (Vec3.Zero, Vec3.Zero) } );
			history.Undo();
			Require( svc.GetDeltas( "copy" ).SequenceEqual( copyBefore ), "erase undo exact" );
		} );

		Check( "rename keeps the active target and stroke undo by object", () =>
		{
			var history = new EditHistory();
			var svc = new MorphEditService( Grid(), history );
			svc.CreateTarget( "a" );
			svc.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Move, 1f, 1f, BrushFalloff.Constant ), new[] { (Vec3.Zero, new Vec3( 0, 0, 1 )) } );
			svc.RenameTarget( "a", "b" );
			Require( svc.ActiveTarget == "b", "active follows rename" );
			history.Undo(); // rename
			history.Undo(); // stroke on the (now) 'a' object
			Require( svc.GetDeltas( "a" ).All( v => v.Equals( Vec3.Zero ) ), "stroke undo follows the morph object" );
		} );

		Check( "seam corners and split vertices stay consistent", () =>
		{
			// Corner seams: per-corner UV duplicates all receive the source vertex delta.
			var mesh = Grid();
			var svc = new MorphEditService( mesh, new EditHistory() );
			svc.CreateTarget( "s" );
			svc.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Inflate, 2.5f, 1f, InflateDistance: 1f ), new[] { (new Vec3( 0.4f, 0.2f, 0 ), Vec3.Zero) } );
			var map = CornerVertexMap.FromMesh( mesh );
			Require( map.CornerCount > mesh.Positions.Count, "fixture has duplicated corners" );
			var cp = new Vec3[map.CornerCount]; var cn = new Vec3[map.CornerCount];
			svc.PreviewCorners( map, new Dictionary<string, float> { ["s"] = 1f }, cp, cn );
			var d = svc.GetDeltas( "s" );
			for ( int c = 0; c < map.CornerCount; c++ )
				Require( cp[c].Equals( map.BasePositions[c] + d[map.CornerToSource[c]] ), $"corner {c} = base + source delta" );
			for ( int v = 0; v < map.SourceVertexCount; v++ )
				foreach ( var c in map.SourceToCorners[v] )
					Require( cp[c].Equals( cp[map.SourceToCorners[v][0]] ), $"duplicates of vertex {v} agree" );

			// Split-vertex seam (coincident separate indices): every op keeps the pair identical.
			var split = SplitGrid( out var pairs );
			var svc2 = new MorphEditService( split, new EditHistory() );
			Require( svc2.GroupCount == split.Positions.Count - pairs.Count, "coincident vertices grouped" );
			svc2.CreateTarget( "seam" );
			var dabs = new[] { (new Vec3( 0.3f, 0.2f, 0 ), new Vec3( 0.4f, 0.1f, 0.6f )), (new Vec3( -0.2f, -0.6f, 0 ), new Vec3( 0, 0.2f, 0.3f )) };
			svc2.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Move, 1.8f, 0.9f ), dabs );
			svc2.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Inflate, 1.8f, 0.9f, InflateDistance: 0.7f ), dabs );
			svc2.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Smooth, 1.8f, 0.9f, MirrorX: true ), dabs );
			svc2.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Erase, 1.2f, 0.4f ), dabs );
			var sd = svc2.GetDeltas( "seam" );
			Require( pairs.Any( p => !sd[p.A].Equals( Vec3.Zero ) ), "seam was sculpted" );
			foreach ( var (a, b) in pairs )
				Require( sd[a].Equals( sd[b] ), $"split pair {a}/{b}: {sd[a]} vs {sd[b]}" );

			// Inflate on a split seam uses the welded normal (not each half's partial normal).
			var svc3 = new MorphEditService( SplitGrid( out var pairs3 ), new EditHistory() );
			svc3.CreateTarget( "n" );
			svc3.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Inflate, 0.5f, 1f, BrushFalloff.Constant, InflateDistance: 1f ), new[] { (Vec3.Zero, Vec3.Zero) } );
			var nd = svc3.GetDeltas( "n" );
			var (pa, pb) = pairs3[3]; // (0,0)
			Require( Near( nd[pa], new Vec3( 0, 0, 1 ) ) && nd[pa].Equals( nd[pb] ), $"welded normal at seam: {nd[pa]} / {nd[pb]}" );

			var smap = CornerVertexMap.FromMesh( split );
			var sp = new Vec3[smap.CornerCount]; var sn = new Vec3[smap.CornerCount];
			svc2.PreviewCorners( smap, new Dictionary<string, float> { ["seam"] = 1f }, sp, sn );
			for ( int c = 0; c < smap.CornerCount; c++ )
				for ( int k = 0; k < smap.CornerCount; k++ )
					if ( smap.BasePositions[c].Equals( smap.BasePositions[k] ) )
						Require( sp[c].Equals( sp[k] ), $"coincident corners {c}/{k} stay closed" );
		} );

		Check( "export: sculpted morph reaches FBX and DMX through the adapters", () =>
		{
			var dir = Path.Combine( settings.WorkDirectory, "morph-edit-export-" + Guid.NewGuid().ToString( "N" ) );
			Directory.CreateDirectory( dir );
			var mesh = Grid( 2 );
			var svc = new MorphEditService( mesh, new EditHistory() );
			svc.CreateTarget( "smile" );
			svc.ApplyStroke( new MorphBrushSettings( MorphBrushOp.Move, 1.5f, 1f, BrushFalloff.Linear, MirrorX: true ), new[] { (new Vec3( 1, 0, 0 ), new Vec3( 0.5f, 0.25f, 1 )) } );
			svc.CreateTarget( "blank_kept_out" );
			svc.DeleteTarget( "blank_kept_out" );
			var deltas = svc.GetDeltas( "smile" );
			int moved = deltas.Count( v => v.Length > 1e-5f );
			Require( moved > 0, "fixture moved vertices" );

			// FBX: compare the shape against an export of the fully shaped mesh, so the test does not
			// assume the writer's unit/axis conversion; it only requires base + offset == shaped.
			var fbx = FbxExportAdapter.ExportBinary( mesh, Path.Combine( dir, "morph.fbx" ) );
			var shapedMesh = mesh.Clone();
			shapedMesh.Morphs.Clear();
			for ( int i = 0; i < shapedMesh.Positions.Count; i++ ) shapedMesh.Positions[i] += deltas[i];
			var fbxShaped = FbxExportAdapter.ExportBinary( shapedMesh, Path.Combine( dir, "shaped.fbx" ) );
			var scene = FbxScene.ReadFile( fbx );
			var sceneShaped = FbxScene.ReadFile( fbxShaped );
			var m = scene.Meshes.Single();
			Require( m.Shapes.Count == 1, $"FBX shape count {m.Shapes.Count}" );
			var shape = m.Shapes[0];
			Require( shape.Name.EndsWith( "smile", StringComparison.Ordinal ), $"FBX shape name '{shape.Name}'" );
			// The writer also lists points whose normal turned but which did not move (offset 0).
			Require( shape.Indexes.Length >= moved, $"FBX sparse indexes {shape.Indexes.Length} vs moved {moved}" );
			var dense = shape.Dense( m.Points.Length );
			var sp = sceneShaped.Meshes.Single().Points;
			Require( sp.Length == m.Points.Length && m.Points.Length == mesh.Positions.Count, "control point count preserved" );
			for ( int i = 0; i < m.Points.Length; i++ )
				Require( Near( m.Points[i] + dense[i], sp[i], 1e-4f ), $"FBX base+shape == shaped at {i}: {m.Points[i] + dense[i]} vs {sp[i]}" );
			Require( m.Shapes[0].DeformPercent == 0, "saved with key off" );

			// DMX (KeyValues2 text): base position$0 + delta state position$0 at its indices == shaped.
			var dmx = DmxExportAdapter.ExportMesh( mesh, Path.Combine( dir, "morph.dmx" ) );
			var text = File.ReadAllText( dmx );
			Require( text.Contains( "\"DmeVertexDeltaData\"" ) && text.Contains( "\"smile\"" ), "DMX has a delta state named smile" );
			Require( !text.Contains( "blank_kept_out" ), "deleted target not exported" );
			Require( text.Contains( "\"DmeCombinationOperator\"" ), "DMX combination operator present" );
			var basePos = ReadVec3Array( text, 0, "position$0", out _ );
			int deltaAt = text.IndexOf( "\"DmeVertexDeltaData\"", StringComparison.Ordinal );
			var dPos = ReadVec3Array( text, deltaAt, "position$0", out int after );
			var dIdx = ReadIntArray( text, after, "position$0Indices" );
			Require( basePos.Length == mesh.Positions.Count, $"DMX base positions {basePos.Length}" );
			Require( dIdx.Length == moved && dPos.Length == moved, $"DMX sparse delta count {dIdx.Length}/{dPos.Length} vs {moved}" );
			for ( int k = 0; k < dIdx.Length; k++ )
				Require( Near( basePos[dIdx[k]] + dPos[k], mesh.Positions[dIdx[k]] + deltas[dIdx[k]], 1e-4f ), $"DMX delta {k} (vertex {dIdx[k]})" );
		} );

		Check( "invalid inputs are rejected", () =>
		{
			Throws<ArgumentNullException>( "null mesh", () => new MorphEditService( null!, new EditHistory() ) );
			Throws<ArgumentNullException>( "null history", () => new MorphEditService( Grid(), null! ) );
			Throws<ArgumentException>( "empty mesh", () => new MorphEditService( new PolyMesh(), new EditHistory() ) );
			var bad = Grid(); bad.Faces.Add( new Face( new[] { 0, 1, 999 } ) );
			Throws<ArgumentException>( "face index out of range", () => new MorphEditService( bad, new EditHistory() ) );
			Throws<ArgumentOutOfRangeException>( "NaN weld", () => new MorphEditService( Grid(), new EditHistory(), float.NaN ) );

			var mesh = Grid();
			var history = new EditHistory();
			var svc = new MorphEditService( mesh, history );
			Throws<ArgumentException>( "null name", () => svc.CreateTarget( null! ) );
			Throws<ArgumentException>( "empty name", () => svc.CreateTarget( "" ) );
			Throws<ArgumentException>( "whitespace name", () => svc.CreateTarget( "   " ) );
			Throws<ArgumentException>( "padded name", () => svc.CreateTarget( " x" ) );
			Throws<ArgumentException>( "quote in name", () => svc.CreateTarget( "a\"b" ) );
			Throws<ArgumentException>( "control char", () => svc.CreateTarget( "a\nb" ) );
			Throws<ArgumentException>( "wrong delta length", () => svc.CreateTarget( "x", new Vec3[3] ) );
			var nan = new Vec3[mesh.Positions.Count]; nan[4] = new Vec3( float.NaN, 0, 0 );
			Throws<ArgumentException>( "NaN delta", () => svc.CreateTarget( "x", nan ) );
			Require( history.UndoCount == 0 && mesh.Morphs.Count == 0, "rejected creates leave no trace" );

			svc.CreateTarget( "a" );
			svc.CreateTarget( "b" );
			Throws<ArgumentException>( "duplicate name", () => svc.CreateTarget( "a" ) );
			Throws<ArgumentException>( "rename onto existing", () => svc.RenameTarget( "a", "b" ) );
			Throws<ArgumentException>( "rename unknown", () => svc.RenameTarget( "nope", "c" ) );
			Throws<ArgumentException>( "delete unknown", () => svc.DeleteTarget( "nope" ) );
			Throws<ArgumentException>( "duplicate unknown", () => svc.DuplicateTarget( "nope", "c" ) );
			Throws<ArgumentException>( "duplicate onto existing", () => svc.DuplicateTarget( "a", "b" ) );
			Throws<ArgumentException>( "select unknown", () => svc.SetActiveTarget( "nope" ) );
			Throws<ArgumentException>( "get unknown", () => svc.GetDeltas( "nope" ) );
			int entries = history.UndoCount;
			svc.RenameTarget( "a", "a" );
			Require( history.UndoCount == entries, "rename to same name is a no-op" );

			Throws<ArgumentOutOfRangeException>( "zero radius", () => svc.BeginStroke( new MorphBrushSettings( MorphBrushOp.Move, 0f ) ) );
			Throws<ArgumentOutOfRangeException>( "NaN radius", () => svc.BeginStroke( new MorphBrushSettings( MorphBrushOp.Move, float.NaN ) ) );
			Throws<ArgumentOutOfRangeException>( "strength > 1", () => svc.BeginStroke( new MorphBrushSettings( MorphBrushOp.Move, 1f, 1.5f ) ) );
			Throws<ArgumentOutOfRangeException>( "negative strength", () => svc.BeginStroke( new MorphBrushSettings( MorphBrushOp.Move, 1f, -0.1f ) ) );
			Throws<ArgumentOutOfRangeException>( "inf inflate", () => svc.BeginStroke( new MorphBrushSettings( MorphBrushOp.Inflate, 1f, InflateDistance: float.PositiveInfinity ) ) );
			Throws<ArgumentOutOfRangeException>( "bad op", () => svc.BeginStroke( new MorphBrushSettings( (MorphBrushOp)99, 1f ) ) );
			Throws<ArgumentException>( "stroke unknown target", () => svc.BeginStroke( new MorphBrushSettings( MorphBrushOp.Move, 1f ), "nope" ) );

			var stroke = svc.BeginStroke( new MorphBrushSettings( MorphBrushOp.Move, 1f ) );
			Throws<ArgumentException>( "NaN centre", () => stroke.Dab( new Vec3( float.NaN, 0, 0 ) ) );
			Throws<ArgumentException>( "inf offset", () => stroke.Dab( Vec3.Zero, new Vec3( 0, float.PositiveInfinity, 0 ) ) );
			Throws<InvalidOperationException>( "second open stroke", () => svc.BeginStroke( new MorphBrushSettings( MorphBrushOp.Move, 1f ) ) );
			Throws<InvalidOperationException>( "structural op during stroke", () => svc.CreateTarget( "c" ) );
			stroke.Cancel();
			Throws<InvalidOperationException>( "dab after finish", () => stroke.Dab( Vec3.Zero ) );
			Throws<InvalidOperationException>( "commit after cancel", () => stroke.Commit() );
			Require( svc.GetDeltas( "a" ).All( v => v.Equals( Vec3.Zero ) ), "failed dabs changed nothing" );

			var outPos = new Vec3[mesh.Positions.Count];
			Throws<ArgumentException>( "unknown weight name", () => svc.Preview( new Dictionary<string, float> { ["nope"] = 1f }, outPos ) );
			Throws<ArgumentException>( "NaN weight", () => svc.Preview( new Dictionary<string, float> { ["a"] = float.NaN }, outPos ) );
			Throws<ArgumentException>( "small output", () => svc.Preview( new Dictionary<string, float>(), new Vec3[2] ) );
			Throws<ArgumentNullException>( "null weights", () => svc.Preview( null!, outPos ) );
			Throws<InvalidOperationException>( "active overflow", () => svc.Preview( new Dictionary<string, float> { ["a"] = 1f, ["b"] = 1f }, outPos, MorphPolicy.CustomBounds( 0, 1, maxActiveTargets: 1 ) ) );
			Throws<ArgumentException>( "bake unknown", () => svc.BakeMix( "m", new Dictionary<string, float> { ["nope"] = 1f } ) );
			Throws<ArgumentException>( "bake onto existing", () => svc.BakeMix( "a", new Dictionary<string, float> { ["b"] = 1f } ) );
			var otherMap = CornerVertexMap.FromMesh( Grid( 1 ) );
			Throws<ArgumentException>( "corner map for another mesh", () => svc.PreviewCorners( otherMap, new Dictionary<string, float>(), new Vec3[otherMap.CornerCount], new Vec3[otherMap.CornerCount] ) );

			// A recording (open Shape Key feature) morph is not editable and is not exported.
			mesh.Morphs.Add( new MeshMorph { Name = "rec", Deltas = new Vec3[mesh.Positions.Count], Base = mesh.Positions.ToArray() } );
			Require( !svc.HasTarget( "rec" ), "recording morph hidden" );
			Throws<ArgumentException>( "stroke on recording morph", () => svc.BeginStroke( new MorphBrushSettings( MorphBrushOp.Move, 1f ), "rec" ) );
			Throws<ArgumentException>( "create clashes with recording morph name", () => svc.CreateTarget( "rec" ) );

			// Another tool's open transaction blocks morph edits instead of interleaving.
			using ( history.BeginTransaction( "other", () => { } ) )
				Throws<InvalidOperationException>( "foreign transaction", () => svc.CreateTarget( "c" ) );

			// Topology change after construction is refused, never silently mis-indexed.
			mesh.Positions.Add( new Vec3( 9, 9, 9 ) );
			Throws<InvalidOperationException>( "topology changed: stroke", () => svc.BeginStroke( new MorphBrushSettings( MorphBrushOp.Move, 1f ) ) );
			Throws<InvalidOperationException>( "topology changed: preview", () => svc.Preview( new Dictionary<string, float>(), new Vec3[100] ) );
		} );
	}

	static string ArrayBody( string text, int from, string name, out int end )
	{
		int at = text.IndexOf( $"\"{name}\"", from, StringComparison.Ordinal );
		if ( at < 0 ) throw new Exception( $"DMX array '{name}' not found after {from}" );
		int open = text.IndexOf( '[', at );
		int close = text.IndexOf( ']', open );
		end = close;
		return text.Substring( open + 1, close - open - 1 );
	}

	static Vec3[] ReadVec3Array( string text, int from, string name, out int end )
	{
		var body = ArrayBody( text, from, name, out end );
		return Regex.Matches( body, "\"([^\"]*)\"" ).Select( m =>
		{
			var p = m.Groups[1].Value.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).Select( s => float.Parse( s, CultureInfo.InvariantCulture ) ).ToArray();
			return new Vec3( p[0], p[1], p[2] );
		} ).ToArray();
	}

	static int[] ReadIntArray( string text, int from, string name )
	{
		var body = ArrayBody( text, from, name, out _ );
		return Regex.Matches( body, "-?\\d+" ).Select( m => int.Parse( m.Value, CultureInfo.InvariantCulture ) ).ToArray();
	}
}
