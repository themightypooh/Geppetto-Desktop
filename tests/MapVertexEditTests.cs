#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;

/// <summary>
/// hammer-brush-vertex-edit-invalid-face: vertex/edge manipulation with
/// invalid-face highlighting (MapVertexEdit) + per-face defect flags
/// extending the validity checker from hammer-brush-core-plane-based-brushes.
///
/// DEPENDENCY NOTE: MapBrush (core task, muse-3-desktop-hammer-01) had NOT
/// landed when these tests were written — no kernel/Effigy/Map/MapBrush.cs
/// exists on disk. These tests therefore exercise MapVertexEdit against the
/// core spec exactly as filed (plane-based brush; FromVertices; Validate()
/// returning named defects; invalid = named reason, never a silent fix),
/// via MapVertexBrush, the vertex-form working copy documented in
/// kernel/Effigy/Map/MapVertexEdit.cs. When MapBrush lands, the final
/// integrator wires MapBrush.FromVertices -&gt; MapVertexBrush -&gt;
/// MapVertexEdit -&gt; MapBrush.FromPlanes (ToPlanes) and re-runs this group;
/// no test here asserts an API the core spec does not name.
/// </summary>
internal static class MapVertexEditTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "mapvertexedit", name, test );
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
			if ( path is null ) throw new Exception( $"fixture {name}.json not found under tests/fixtures/hammer/hammer-brush-vertex-edit-invalid-face/" );
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
			var rel = Path.Combine( "tests", "fixtures", "hammer", "hammer-brush-vertex-edit-invalid-face", name + ".json" );
			var starts = new[]
			{
				Directory.GetCurrentDirectory(),
				AppContext.BaseDirectory,
				Path.GetDirectoryName( typeof( MapVertexEditTests ).Assembly.Location ) ?? "",
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

		MapVertexBrush LPrism()
		{
			// L-section prism, extruded z 0..1. Top/bottom faces are concave
			// (reflex vertex at (1,1)); the six side faces are rectangles.
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
			return new MapVertexBrush( v, f );
		}

		Check( "box fixture loads valid with volume 8 and no boundary edges", () =>
		{
			var box = LoadFixture( "box" );
			var validity = MapVertexEdit.Validate( box );
			Require( validity.IsValid, string.Join( "; ", validity.Defects ) );
			Require( Math.Abs( validity.Volume - 8 ) < 1e-6, $"volume {validity.Volume}" );
			Require( box.FaceCount == 6, $"faces {box.FaceCount}" );
			Require( validity.Lint != null && validity.Lint.BoundaryEdges == 0, "box must be watertight" );
			Require( validity.InvalidFaceIndices.Length == 0, "no face may be flagged" );
		} );

		Check( "box factory matches the box fixture", () =>
		{
			var made = MapVertexBrush.Box( 1f );
			var filed = LoadFixture( "box" );
			Require( Math.Abs( made.Volume() - filed.Volume() ) < 1e-6, "factory volume differs from fixture" );
			Require( made.Validate().IsValid && filed.Validate().IsValid, "factory and fixture must both be valid" );
			Require( made.FaceCount == filed.FaceCount, "face counts differ" );
		} );

		Check( "moving a pyramid apex up stays valid and grows the volume", () =>
		{
			var pyramid = LoadFixture( "pyramid" );
			Require( Math.Abs( pyramid.Volume() - 8.0 / 3.0 ) < 1e-4, $"pyramid volume {pyramid.Volume()}" );
			var moved = MapVertexEdit.MoveVertex( pyramid, 4, new Vec3( 0, 0, 3 ) );
			Require( moved.IsValid, string.Join( "; ", moved.Validity.Defects ) );
			Require( Math.Abs( moved.Brush.Volume() - 4 ) < 1e-6, $"moved volume {moved.Brush.Volume()}" );
			Require( moved.Brush.Vertices[4].Equals( new Vec3( 0, 0, 3 ) ), "apex did not land" );
			// The edit is pure: the source brush is untouched.
			Require( pyramid.Vertices[4].Equals( new Vec3( 0, 0, 2 ) ), "source brush was mutated" );
			Require( Math.Abs( pyramid.Volume() - 8.0 / 3.0 ) < 1e-4, "source volume moved" );
		} );

		Check( "twisting one top vertex out of plane flags only the top face, never repairs it", () =>
		{
			var box = MapVertexBrush.Box( 1f );
			var twisted = MapVertexEdit.MoveVertexBy( box, 6, new Vec3( 0, 0, 1 ) );
			Require( !twisted.IsValid, "twisted brush must be reported invalid" );
			Require( twisted.InvalidFaceIndices.SequenceEqual( new[] { 1 } ),
				$"invalid faces {string.Join( ",", twisted.InvalidFaceIndices )}, expected only face 1 (top)" );
			var top = twisted.Validity.Faces[1];
			Require( top.Defects.HasFlag( MapFaceDefect.NonPlanar ), $"top face defects {top.Defects}" );
			Require( top.MaxPlanarDeviation > 0.2, $"deviation {top.MaxPlanarDeviation}" );
			Require( twisted.Validity.Defects.Any( d => d.Contains( "face 1" ) && d.Contains( "non-planar" ) ),
				$"no named non-planar defect for face 1: {string.Join( "; ", twisted.Validity.Defects )}" );
			// Never silently repaired: the vertex is exactly where the drag put it.
			Require( twisted.Brush.Vertices[6].Equals( new Vec3( 1, 1, 2 ) ),
				$"twisted vertex moved to {twisted.Brush.Vertices[6]}" );
			// Adjacent side faces stay planar (they share the x=1 / y=1 planes).
			Require( twisted.Validity.Faces[4].IsValid && twisted.Validity.Faces[3].IsValid,
				"side faces sharing the twisted vertex must stay valid" );
			// The adjacent top plane was re-solved from the moved vertices.
			var (normal, _) = twisted.Brush.FacePlane( 1 );
			Require( normal.LengthSquared > 0.99f, "re-solved top plane has no normal" );
		} );

		Check( "moving a pyramid base edge outwards stays valid and re-solves its faces", () =>
		{
			var pyramid = MapVertexBrush.SquarePyramid( 1f, 2f );
			var moved = MapVertexEdit.MoveEdge( pyramid, 0, 1, new Vec3( 0, -1, 0 ) );
			Require( moved.IsValid, string.Join( "; ", moved.Validity.Defects ) );
			Require( Math.Abs( moved.Brush.Volume() - 4 ) < 1e-6, $"moved volume {moved.Brush.Volume()}" );
			Require( moved.AffectedFaces.SequenceEqual( new[] { 0, 1, 2, 4 } ),
				$"affected faces {string.Join( ",", moved.AffectedFaces )}, expected base + front + right + left (all faces incident to either endpoint)" );
			Require( moved.Brush.Vertices[0].Equals( new Vec3( -1, -2, 0 ) ), "edge start did not move" );
			Require( moved.Brush.Vertices[1].Equals( new Vec3( 1, -2, 0 ) ), "edge end did not move" );
		} );

		Check( "splitting the top face diagonally yields two planar faces, same volume, watertight", () =>
		{
			var box = MapVertexBrush.Box( 1f );
			var split = MapVertexEdit.SplitFace( box, 1, 4, 6 );
			Require( split.IsValid, string.Join( "; ", split.Validity.Defects ) );
			Require( split.Brush.FaceCount == 7, $"faces {split.Brush.FaceCount}" );
			Require( Math.Abs( split.Brush.Volume() - 8 ) < 1e-6, $"volume {split.Brush.Volume()}" );
			Require( split.Validity.Lint != null && split.Validity.Lint.BoundaryEdges == 0,
				"split brush must stay watertight" );
			Require( split.AffectedFaces.SequenceEqual( new[] { 1, 6 } ),
				$"affected faces {string.Join( ",", split.AffectedFaces )}" );
			var children = split.AffectedFaces.Select( i => split.Validity.Faces[i] ).ToArray();
			Require( children.All( f => f.IsValid ), "both split halves must be planar" );
			Require( children.All( f => f.Area > 1.9 && f.Area < 2.1 ), "split halves must each be half the top face" );
			Require( split.Brush.Materials[6] == split.Brush.Materials[1], "split half must keep the face material" );
		} );

		Check( "splitting the top face on two edge midpoints yields two planar faces and two new vertices", () =>
		{
			var box = MapVertexBrush.Box( 1f );
			var split = MapVertexEdit.SplitFaceOnEdges( box, 1, 0, 2 );
			Require( split.IsValid, string.Join( "; ", split.Validity.Defects ) );
			Require( split.Brush.FaceCount == 7, $"faces {split.Brush.FaceCount}" );
			Require( split.Brush.VertexCount == 10, $"vertices {split.Brush.VertexCount}" );
			Require( Math.Abs( split.Brush.Volume() - 8 ) < 1e-6, $"volume {split.Brush.Volume()}" );
			Require( split.Brush.Vertices[8].AlmostEquals( new Vec3( 0, -1, 1 ) ), $"midpoint A {split.Brush.Vertices[8]}" );
			Require( split.Brush.Vertices[9].AlmostEquals( new Vec3( 0, 1, 1 ) ), $"midpoint B {split.Brush.Vertices[9]}" );
			Require( split.Validity.Faces[1].IsValid && split.Validity.Faces[6].IsValid, "both halves must be planar" );
		} );

		Check( "concave L-prism faces are flagged non-convex and the side faces stay clean", () =>
		{
			var prism = LPrism();
			var validity = MapVertexEdit.Validate( prism );
			Require( !validity.IsValid, "L-prism must be reported invalid" );
			Require( validity.InvalidFaceIndices.SequenceEqual( new[] { 0, 1 } ),
				$"invalid faces {string.Join( ",", validity.InvalidFaceIndices )}, expected top/bottom only" );
			Require( validity.Faces[0].Defects.HasFlag( MapFaceDefect.NonConvex ), "bottom face must be non-convex" );
			Require( validity.Faces[1].Defects.HasFlag( MapFaceDefect.NonConvex ), "top face must be non-convex" );
			Require( validity.Faces.Skip( 2 ).All( f => f.IsValid ), "side faces must be clean" );
			Require( Math.Abs( validity.Volume - 3 ) < 1e-6, $"volume {validity.Volume}" );
			var filed = LoadFixture( "l-prism" );
			Require( filed.Validate().InvalidFaceIndices.SequenceEqual( new[] { 0, 1 } ), "fixture must agree with the coded prism" );
		} );

		Check( "collapsing a top face to a point flags it degenerate and zero-area, and is not repaired", () =>
		{
			var box = MapVertexBrush.Box( 1f );
			var apex = new Vec3( -1, -1, 1 );
			var collapsed = MapVertexEdit.MoveVertices( box, new Dictionary<int, Vec3>
			{
				[5] = apex, [6] = apex, [7] = apex,
			} );
			Require( !collapsed.IsValid, "collapsed brush must be reported invalid" );
			var top = collapsed.Validity.Faces[1];
			Require( top.Defects.HasFlag( MapFaceDefect.Degenerate ), $"top defects {top.Defects}" );
			Require( top.Defects.HasFlag( MapFaceDefect.ZeroArea ), $"top defects {top.Defects}" );
			Require( collapsed.Brush.Vertices[5].Equals( apex ) && collapsed.Brush.Vertices[6].Equals( apex ),
				"collapsed vertices were moved back — silent repair" );
		} );

		Check( "a face with flipped winding is flagged inverted", () =>
		{
			var box = MapVertexBrush.Box( 1f );
			var faces = box.Faces.Select( f => (int[]) f.Clone() ).ToArray();
			faces[1] = faces[1].Reverse().ToArray();
			var flipped = new MapVertexBrush( box.Vertices, faces, box.Materials );
			var validity = MapVertexEdit.Validate( flipped );
			Require( !validity.IsValid, "flipped brush must be reported invalid" );
			Require( validity.Faces[1].Defects.HasFlag( MapFaceDefect.Inverted ),
				$"top face defects {validity.Faces[1].Defects}" );
			Require( validity.InvalidFaceIndices.SequenceEqual( new[] { 1 } ),
				$"invalid faces {string.Join( ",", validity.InvalidFaceIndices )}" );
		} );

		Check( "invalid edits are refused, not silently accepted", () =>
		{
			var box = MapVertexBrush.Box( 1f );
			Throws( () => MapVertexEdit.MoveVertex( box, 99, Vec3.Zero ) );
			Throws( () => MapVertexEdit.MoveVertex( box, -1, Vec3.Zero ) );
			Throws( () => MapVertexEdit.MoveEdge( box, 0, 6, new Vec3( 0, 0, 1 ) ) ); // diagonal, not an edge
			Throws( () => MapVertexEdit.MoveEdge( box, 0, 0, new Vec3( 0, 0, 1 ) ) );
			Throws( () => MapVertexEdit.SplitFace( box, 1, 4, 5 ) ); // adjacent: an edge, not a split
			Throws( () => MapVertexEdit.SplitFace( box, 1, 4, 0 ) ); // vertex 0 is not on face 1
			Throws( () => MapVertexEdit.SplitFace( box, 99, 4, 6 ) );
			Throws( () => MapVertexEdit.SplitFaceOnEdges( box, 1, 0, 0 ) );
			Throws( () => MapVertexEdit.Validate( null! ) );
		} );

		Check( "twisted-box fixture flags exactly face 1 non-planar with a named defect", () =>
		{
			var twisted = LoadFixture( "twisted-box" );
			var validity = MapVertexEdit.Validate( twisted );
			Require( !validity.IsValid, "twisted fixture must be invalid" );
			Require( validity.InvalidFaceIndices.SequenceEqual( new[] { 1 } ),
				$"invalid faces {string.Join( ",", validity.InvalidFaceIndices )}" );
			Require( validity.Faces[1].Defects.HasFlag( MapFaceDefect.NonPlanar ), "face 1 must be non-planar" );
			Require( validity.Defects.Any( d => d.StartsWith( "face 1: non-planar", StringComparison.Ordinal ) ),
				$"defects: {string.Join( "; ", validity.Defects )}" );
			Require( Math.Abs( validity.Volume - 9.333333 ) < 1e-4, $"volume {validity.Volume}" );
		} );

		Check( "ValidateDefects and InvalidFaces expose the highlight set in MapBrush.Validate shape", () =>
		{
			var box = MapVertexBrush.Box( 1f );
			Require( MapVertexEdit.ValidateDefects( box ).Count == 0, "valid box must have no defects" );
			Require( MapVertexEdit.InvalidFaces( box ).Length == 0, "valid box must have no highlight faces" );
			var twisted = MapVertexEdit.MoveVertexBy( box, 6, new Vec3( 0, 0, 1 ) );
			var defects = MapVertexEdit.ValidateDefects( twisted.Brush );
			Require( defects.Count >= 1 && defects[0].StartsWith( "face 1:", StringComparison.Ordinal ),
				$"defects must name the face first: {string.Join( "; ", defects )}" );
			Require( MapVertexEdit.InvalidFaces( twisted.Brush ).SequenceEqual( new[] { 1 } ), "highlight set must be face 1" );
		} );
	}
}
