#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;

/// <summary>
/// hammer-vertex-multi-edit-autotriangulate: multi-vertex / multi-edge /
/// multi-face moves (one operation, one result) + explicit JACK-style
/// auto-triangulation of invalid brushes (MapVertexEdit.TriangulateInvalid).
///
/// Builds on hammer-brush-vertex-edit-invalid-face (worker 07): MoveVertices
/// with ABSOLUTE targets already exists there and is NOT changed here — this
/// ticket adds the SET+DELTA overload MoveVertices(set, delta) plus
/// MoveEdges / MoveFaces, and TriangulateInvalid. The one rule the tests
/// hammer hardest: a plain vertex move NEVER triangulates — it returns the
/// twisted brush with its faces flagged, and only an explicit
/// TriangulateInvalid call repairs it.
/// </summary>
internal static class VertexMultiEditTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "vertexmultiedit", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws( Action action )
		{
			try { action(); }
			catch ( Exception ) { return; }
			throw new Exception( "expected an error, got silent success" );
		}

		MapVertexBrush LoadFixture( string name )
		{
			var path = FindFixture( name );
			if ( path is null ) throw new Exception( $"fixture {name}.json not found under tests/fixtures/hammer/hammer-vertex-multi-edit-autotriangulate/" );
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var root = doc.RootElement;
			var verts = root.GetProperty( "vertices" ).EnumerateArray()
				.Select( v => new Vec3( v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle() ) ).ToArray();
			var faces = root.GetProperty( "faces" ).EnumerateArray()
				.Select( f => f.EnumerateArray().Select( i => i.GetInt32() ).ToArray() ).ToArray();
			var mats = root.TryGetProperty( "materials", out var m )
				? m.EnumerateArray().Select( s => s.GetString() ?? "default" ).ToArray()
				: null;
			return new MapVertexBrush( verts, faces, mats );
		}

		string? FindFixture( string name )
		{
			var rel = Path.Combine( "tests", "fixtures", "hammer", "hammer-vertex-multi-edit-autotriangulate", name + ".json" );
			var starts = new[]
			{
				Directory.GetCurrentDirectory(),
				AppContext.BaseDirectory,
				Path.GetDirectoryName( typeof( VertexMultiEditTests ).Assembly.Location ) ?? "",
			};
			foreach ( var start in starts )
			{
				var dir = new DirectoryInfo( start );
				while ( dir != null )
				{
					var candidate = Path.Combine( dir.FullName, rel );
					if ( File.Exists( candidate ) ) return candidate;
					dir = dir.Parent;
				}
			}
			return null;
		}

		// Box(1) with one distinct material per face, so triangulation tests can
		// prove each child triangle kept its parent face's material. Face order
		// matches MapVertexBrush.Box: bottom, top, front(-Y), back(+Y), left(-X), right(+X).
		MapVertexBrush MatBox()
		{
			var box = MapVertexBrush.Box( 1f );
			return new MapVertexBrush( box.Vertices, box.Faces,
				new[] { "bottom", "top", "front", "back", "left", "right" } );
		}

		MapVertexBrush LPrism()
		{
			// L-section prism, extruded z 0..1 (same layout as worker 07's
			// MapVertexEditTests helper). Top/bottom faces are concave (reflex
			// vertex at (1,1)); the six side faces are rectangles. Volume 3.
			var v = new[]
			{
				new Vec3( 0, 0, 0 ), new Vec3( 2, 0, 0 ), new Vec3( 2, 1, 0 ),
				new Vec3( 1, 1, 0 ), new Vec3( 1, 2, 0 ), new Vec3( 0, 2, 0 ),
				new Vec3( 0, 0, 1 ), new Vec3( 2, 0, 1 ), new Vec3( 2, 1, 1 ),
				new Vec3( 1, 1, 1 ), new Vec3( 1, 2, 1 ), new Vec3( 0, 2, 1 ),
			};
			var f = new[]
			{
				new[] { 0, 5, 4, 3, 2, 1 }, new[] { 6, 7, 8, 9, 10, 11 },
				new[] { 0, 1, 7, 6 }, new[] { 1, 2, 8, 7 }, new[] { 2, 3, 9, 8 },
				new[] { 3, 4, 10, 9 }, new[] { 4, 5, 11, 10 }, new[] { 5, 0, 6, 11 },
			};
			return new MapVertexBrush( v, f,
				new[] { "bottom", "top", "front", "right-a", "notch-x", "notch-y", "back-b", "left" } );
		}

		Check( "moving two top vertices together keeps the brush valid and grows the volume to 10", () =>
		{
			var box = MapVertexBrush.Box( 1f );
			// Top-front edge (verts 4,5 at y=-1) up by 1: the top face becomes a
			// slanted plane, every face stays planar.
			var moved = MapVertexEdit.MoveVertices( box, new[] { 4, 5 }, new Vec3( 0, 0, 1 ) );
			Require( moved.IsValid, string.Join( "; ", moved.Validity.Defects ) );
			Require( Math.Abs( moved.Brush.Volume() - 10 ) < 1e-6, $"volume {moved.Brush.Volume()}, expected 10" );
			Require( moved.Brush.Vertices[4].Equals( new Vec3( -1, -1, 2 ) ), $"v4 {moved.Brush.Vertices[4]}" );
			Require( moved.Brush.Vertices[5].Equals( new Vec3( 1, -1, 2 ) ), $"v5 {moved.Brush.Vertices[5]}" );
			Require( moved.Brush.Vertices[6].Equals( new Vec3( 1, 1, 1 ) ), "v6 must not move" );
			Require( moved.Brush.Vertices[7].Equals( new Vec3( -1, 1, 1 ) ), "v7 must not move" );
			Require( moved.AffectedFaces.SequenceEqual( new[] { 1, 2, 4, 5 } ),
				$"affected faces {string.Join( ",", moved.AffectedFaces )}, expected 1,2,4,5" );
			Require( moved.Brush.FaceCount == 6, $"faces {moved.Brush.FaceCount}" );
		} );

		Check( "moving a set of edges together lifts the whole top face", () =>
		{
			var box = MapVertexBrush.Box( 1f );
			var moved = MapVertexEdit.MoveEdges( box, new[] { ( 4, 5 ), ( 6, 7 ) }, new Vec3( 0, 0, 1 ) );
			Require( moved.IsValid, string.Join( "; ", moved.Validity.Defects ) );
			Require( Math.Abs( moved.Brush.Volume() - 12 ) < 1e-6, $"volume {moved.Brush.Volume()}, expected 12" );
			foreach ( var v in new[] { 4, 5, 6, 7 } )
				Require( moved.Brush.Vertices[v].z == 2f, $"top vertex {v} z={moved.Brush.Vertices[v].z}" );
			foreach ( var v in new[] { 0, 1, 2, 3 } )
				Require( moved.Brush.Vertices[v].z == -1f, $"bottom vertex {v} moved" );
			Require( moved.AffectedFaces.SequenceEqual( new[] { 1, 2, 3, 4, 5 } ),
				$"affected faces {string.Join( ",", moved.AffectedFaces )}, expected 1,2,3,4,5" );
		} );

		Check( "moving the top face together lifts the box", () =>
		{
			var box = MapVertexBrush.Box( 1f );
			var moved = MapVertexEdit.MoveFaces( box, new[] { 1 }, new Vec3( 0, 0, 1 ) );
			Require( moved.IsValid, string.Join( "; ", moved.Validity.Defects ) );
			Require( Math.Abs( moved.Brush.Volume() - 12 ) < 1e-6, $"volume {moved.Brush.Volume()}, expected 12" );
			Require( moved.Brush.FaceCount == 6, $"faces {moved.Brush.FaceCount}" );
			Require( moved.AffectedFaces.SequenceEqual( new[] { 1, 2, 3, 4, 5 } ),
				$"affected faces {string.Join( ",", moved.AffectedFaces )}, expected 1,2,3,4,5" );
			foreach ( var v in new[] { 0, 1, 2, 3 } )
				Require( moved.Brush.Vertices[v].z == -1f, $"bottom vertex {v} moved" );
		} );

		Check( "a plain multi-vertex move never auto-triangulates: a twist stays twisted", () =>
		{
			var box = MapVertexBrush.Box( 1f );
			var twisted = MapVertexEdit.MoveVertices( box, new[] { 6 }, new Vec3( 0, 0, 1 ) );
			Require( !twisted.IsValid, "twisted brush must be reported invalid" );
			Require( twisted.InvalidFaceIndices.SequenceEqual( new[] { 1 } ),
				$"invalid faces {string.Join( ",", twisted.InvalidFaceIndices )}, expected only face 1 (top)" );
			// Same topology — no triangulation happened: the top face is still a quad.
			Require( twisted.Brush.FaceCount == 6, $"faces {twisted.Brush.FaceCount}, a move must not add faces" );
			Require( twisted.Brush.Faces[1].Length == 4, "top face must still be a quad" );
			Require( twisted.Brush.Vertices[6].Equals( new Vec3( 1, 1, 2 ) ),
				$"twisted vertex moved to {twisted.Brush.Vertices[6]}" );
			Require( twisted.Validity.Faces[1].Defects.HasFlag( MapFaceDefect.NonPlanar ), "top face must be flagged non-planar" );
		} );

		Check( "twisting then explicitly triangulating yields one valid piece with the same volume", () =>
		{
			var box = MatBox();
			var twisted = MapVertexEdit.MoveVertices( box, new[] { 6 }, new Vec3( 0, 0, 1 ) );
			Require( !twisted.IsValid, "precondition: twisted brush must be invalid" );
			var t = MapVertexEdit.TriangulateInvalid( twisted.Brush );
			Require( t.WasTriangulated, "triangulation must report it ran" );
			Require( t.Mode == MapTriangulationMode.FanTriangulation, $"mode {t.Mode}, expected FanTriangulation" );
			Require( t.Pieces.Count == 1, $"pieces {t.Pieces.Count}, expected 1" );
			Require( t.AllValid, string.Join( "; ", t.PieceValidity.SelectMany( v => v.Defects ) ) );
			Require( Math.Abs( t.OriginalVolume - twisted.Brush.Volume() ) < 1e-9, "original volume must be the twisted brush volume" );
			Require( Math.Abs( t.TotalVolume - 9.333333 ) < 1e-4, $"total volume {t.TotalVolume}, expected 9.333333" );
			Require( Math.Abs( t.TotalVolume - twisted.Brush.Volume() ) < 1e-6, "fan triangulation must preserve the volume exactly" );
			Require( t.VolumePreserved, "volume must be reported preserved" );
			var piece = t.Pieces[0];
			Require( piece.FaceCount == 7, $"faces {piece.FaceCount}, expected 6 - 1 quad + 2 triangles" );
			// Only the invalid top face is fanned, so its two children land at
			// piece indices 1 and 2; faces 2..5 shift to 3..6.
			Require( piece.Materials[1] == "top" && piece.Materials[2] == "top",
				$"both top children must keep material 'top', got '{piece.Materials[1]}' / '{piece.Materials[2]}'" );
			Require( piece.Materials[0] == "bottom", "untouched faces must keep their material" );
			Require( t.PieceValidity[0].Lint != null && t.PieceValidity[0].Lint.BoundaryEdges == 0,
				"triangulated brush must stay watertight" );
			Require( t.Reason.Length == 0, $"reason must be empty on success, got '{t.Reason}'" );
		} );

		Check( "triangulating an already-valid box returns it unchanged", () =>
		{
			var box = MapVertexBrush.Box( 1f );
			var t = MapVertexEdit.TriangulateInvalid( box );
			Require( !t.WasTriangulated, "a valid brush must not be triangulated" );
			Require( t.Mode == MapTriangulationMode.AlreadyValid, $"mode {t.Mode}, expected AlreadyValid" );
			Require( t.Pieces.Count == 1, $"pieces {t.Pieces.Count}" );
			Require( ReferenceEquals( t.Pieces[0], box ), "the valid brush must come back as itself, untouched" );
			Require( t.AllValid, "the valid brush must report all valid" );
			Require( Math.Abs( t.TotalVolume - 8 ) < 1e-6, $"volume {t.TotalVolume}" );
			Require( t.VolumePreserved, "volume must be reported preserved" );
		} );

		Check( "an L-prism fan-triangulates into one valid piece with the same volume and kept materials", () =>
		{
			var prism = LPrism();
			Require( !MapVertexEdit.Validate( prism ).IsValid, "precondition: L-prism faces must be flagged non-convex" );
			Require( Math.Abs( prism.Volume() - 3 ) < 1e-6, $"prism volume {prism.Volume()}, expected 3" );
			var t = MapVertexEdit.TriangulateInvalid( prism );
			Require( t.Mode == MapTriangulationMode.FanTriangulation, $"mode {t.Mode}, expected FanTriangulation" );
			Require( t.Pieces.Count == 1, $"pieces {t.Pieces.Count}" );
			Require( t.AllValid, string.Join( "; ", t.PieceValidity.SelectMany( v => v.Defects ) ) );
			Require( Math.Abs( t.TotalVolume - 3 ) < 1e-6, $"total volume {t.TotalVolume}, expected 3" );
			Require( t.VolumePreserved, "volume must be reported preserved" );
			var piece = t.Pieces[0];
			Require( piece.FaceCount == 14, $"faces {piece.FaceCount}, expected 8 - 2 hexagons + 8 triangles" );
			Require( piece.Materials.Take( 4 ).All( m => m == "bottom" ), "bottom hexagon children must keep 'bottom'" );
			Require( piece.Materials.Skip( 4 ).Take( 4 ).All( m => m == "top" ), "top hexagon children must keep 'top'" );
			Require( piece.Materials.Skip( 8 ).SequenceEqual( new[] { "front", "right-a", "notch-x", "notch-y", "back-b", "left" } ),
				"untouched side faces must keep their materials in order" );
			Require( t.PieceValidity[0].Lint != null && t.PieceValidity[0].Lint.BoundaryEdges == 0,
				"triangulated prism must stay watertight" );
		} );

		Check( "a collapsed-top brush falls back to convex decomposition with valid pieces and kept volume", () =>
		{
			var box = MatBox();
			// Collapse the whole top face to one point via worker 07's absolute
			// MoveVertices overload: the top face is degenerate, and no fan of
			// it can fix that — stage 2 (ConvexDecomp) must take over.
			var collapsed = MapVertexEdit.MoveVertices( box, new Dictionary<int, Vec3>
			{
				[4] = new Vec3( 0, 0, 1 ), [5] = new Vec3( 0, 0, 1 ),
				[6] = new Vec3( 0, 0, 1 ), [7] = new Vec3( 0, 0, 1 ),
			} );
			Require( !collapsed.IsValid, "precondition: collapsed brush must be invalid" );
			Require( collapsed.Validity.Faces[1].Defects.HasFlag( MapFaceDefect.Degenerate ), "top face must be flagged degenerate" );
			var t = MapVertexEdit.TriangulateInvalid( collapsed.Brush );
			Require( t.Mode == MapTriangulationMode.ConvexDecomposition, $"mode {t.Mode}, expected ConvexDecomposition" );
			Require( t.WasTriangulated, "triangulation must report it ran" );
			Require( t.Pieces.Count >= 1, "at least one hull piece is required" );
			Require( t.AllValid, string.Join( "; ", t.PieceValidity.SelectMany( v => v.Defects ) ) );
			Require( Math.Abs( t.TotalVolume - collapsed.Brush.Volume() ) < 1e-4,
				$"total volume {t.TotalVolume} vs collapsed volume {collapsed.Brush.Volume()}" );
			Require( t.VolumePreserved, "collapsed-top hull volume must match the pyramid volume" );
			var originalMats = new HashSet<string>( box.Materials );
			foreach ( var piece in t.Pieces )
			{
				Require( piece.Faces.All( f => f.Length == 3 ), "hull pieces must be all triangles" );
				Require( piece.Materials.All( m => originalMats.Contains( m ) ),
					$"hull face material must come from the original brush, got {string.Join( ",", piece.Materials )}" );
			}
		} );

		Check( "invalid multi-edit arguments are refused and a repeated vertex moves once", () =>
		{
			var box = MapVertexBrush.Box( 1f );
			Throws( () => MapVertexEdit.MoveVertices( box, (IEnumerable<int>) null!, new Vec3( 0, 0, 1 ) ) );
			Throws( () => MapVertexEdit.MoveVertices( (MapVertexBrush) null!, new[] { 4 }, new Vec3( 0, 0, 1 ) ) );
			Throws( () => MapVertexEdit.MoveVertices( box, new[] { 99 }, new Vec3( 0, 0, 1 ) ) );
			Throws( () => MapVertexEdit.MoveVertices( box, new[] { -1 }, new Vec3( 0, 0, 1 ) ) );
			Throws( () => MapVertexEdit.MoveEdges( box, new[] { ( 0, 2 ) }, new Vec3( 0, 0, 1 ) ) );
			Throws( () => MapVertexEdit.MoveEdges( box, new[] { ( 4, 4 ) }, new Vec3( 0, 0, 1 ) ) );
			Throws( () => MapVertexEdit.MoveFaces( box, new[] { 99 }, new Vec3( 0, 0, 1 ) ) );
			Throws( () => MapVertexEdit.MoveFaces( (MapVertexBrush) null!, new[] { 1 }, new Vec3( 0, 0, 1 ) ) );
			Throws( () => MapVertexEdit.TriangulateInvalid( (MapVertexBrush) null! ) );
			// Empty sets are no-ops, not errors.
			var noop = MapVertexEdit.MoveVertices( box, Array.Empty<int>(), new Vec3( 0, 0, 1 ) );
			Require( noop.IsValid, "empty move must stay valid" );
			Require( Math.Abs( noop.Brush.Volume() - 8 ) < 1e-6, "empty move must not change the volume" );
			Require( noop.AffectedFaces.Length == 0, "empty move must affect no faces" );
			// A repeated index is deduplicated: the vertex moves by delta once, not twice.
			var dup = MapVertexEdit.MoveVertices( box, new[] { 4, 4, 5, 5 }, new Vec3( 0, 0, 1 ) );
			Require( dup.Brush.Vertices[4].Equals( new Vec3( -1, -1, 2 ) ), $"v4 {dup.Brush.Vertices[4]}, expected one delta" );
			Require( dup.Brush.Vertices[5].Equals( new Vec3( 1, -1, 2 ) ), $"v5 {dup.Brush.Vertices[5]}, expected one delta" );
		} );

		Check( "twisted-box fixture triangulates to one valid piece with the same volume", () =>
		{
			var twisted = LoadFixture( "twisted-box" );
			var validity = MapVertexEdit.Validate( twisted );
			Require( !validity.IsValid, "fixture must load invalid" );
			Require( validity.InvalidFaceIndices.SequenceEqual( new[] { 1 } ), "fixture must flag only face 1" );
			Require( Math.Abs( validity.Volume - 9.333333 ) < 1e-4, $"fixture volume {validity.Volume}" );
			var t = MapVertexEdit.TriangulateInvalid( twisted );
			Require( t.Mode == MapTriangulationMode.FanTriangulation, $"mode {t.Mode}" );
			Require( t.Pieces.Count == 1 && t.AllValid, "fixture must triangulate to one valid piece" );
			Require( Math.Abs( t.TotalVolume - 9.333333 ) < 1e-4, $"total volume {t.TotalVolume}" );
			Require( t.VolumePreserved, "volume must be reported preserved" );
			Require( t.Pieces[0].Materials[1] == "top" && t.Pieces[0].Materials[2] == "top",
				"both top children must keep material 'top'" );
		} );

		Check( "collapsed-top fixture falls back to convex decomposition", () =>
		{
			var collapsed = LoadFixture( "collapsed-top" );
			Require( !MapVertexEdit.Validate( collapsed ).IsValid, "fixture must load invalid" );
			Require( Math.Abs( collapsed.Volume() - 8.0 / 3.0 ) < 1e-4, $"fixture volume {collapsed.Volume()}, expected 8/3" );
			var t = MapVertexEdit.TriangulateInvalid( collapsed );
			Require( t.Mode == MapTriangulationMode.ConvexDecomposition, $"mode {t.Mode}, expected ConvexDecomposition" );
			Require( t.AllValid, string.Join( "; ", t.PieceValidity.SelectMany( v => v.Defects ) ) );
			Require( Math.Abs( t.TotalVolume - 8.0 / 3.0 ) < 1e-4, $"total volume {t.TotalVolume}, expected 8/3" );
			Require( t.VolumePreserved, "volume must be reported preserved" );
		} );

		Check( "a multi-vertex move never mutates its input", () =>
		{
			var box = MapVertexBrush.Box( 1f );
			var before = box.Vertices.Select( v => v ).ToArray();
			var moved = MapVertexEdit.MoveVertices( box, new[] { 4, 5, 6, 7 }, new Vec3( 0, 0, 2 ) );
			Require( moved.IsValid, string.Join( "; ", moved.Validity.Defects ) );
			Require( Math.Abs( moved.Brush.Volume() - 16 ) < 1e-6, $"moved volume {moved.Brush.Volume()}, expected 16" );
			for ( var i = 0; i < before.Length; i++ )
				Require( box.Vertices[i].Equals( before[i] ), $"source vertex {i} was mutated" );
			Require( Math.Abs( box.Volume() - 8 ) < 1e-6, "source volume moved" );
		} );

		Check( "triangulating an empty brush fails with a named reason, never throws", () =>
		{
			var empty = new MapVertexBrush( Array.Empty<Vec3>(), Array.Empty<int[]>() );
			var t = MapVertexEdit.TriangulateInvalid( empty );
			Require( t.Mode == MapTriangulationMode.Failed, $"mode {t.Mode}, expected Failed" );
			Require( !t.AllValid, "empty brush triangulation must not report all valid" );
			Require( !string.IsNullOrWhiteSpace( t.Reason ), "failure must carry a named reason" );
			Require( t.Reason.Contains( "convex decomposition" ), $"reason must name the failed stage: '{t.Reason}'" );
			Require( t.Pieces.Count == 1, "failed result still returns the best-effort piece for inspection" );
		} );
	}
}
