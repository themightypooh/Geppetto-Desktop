#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;

/// <summary>
/// hammer-edit-3d-first-direct: 3D-first direct editing (DirectEdit3D) —
/// ray pick (face/edge/vertex), face extrude, 3D face/edge/vertex drags,
/// shear/scale about the gizmo pivot, ray-driven drag deltas.
///
/// DEPENDENCY NOTES (same as kernel/Effigy/Map/DirectEdit3D.cs): MapBrush
/// (core) has NOT landed — tests exercise DirectEdit3D against the core
/// spec exactly as filed via MapVertexBrush, the vertex-form working copy
/// (see MapVertexEdit.cs). MapTransform (grid snap) IS on disk and its
/// SnapToGrid is exercised through the grid-snap drag check. GizmoModel
/// has NOT landed — the pivot is passed as an explicit Vec3 (the gizmo
/// pivot once that type exists); no test asserts a GizmoModel API.
/// </summary>
internal static class DirectEdit3DTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "directedit3d", name, test );
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
		void Almost( double actual, double expected, double eps, string what )
		{
			if ( Math.Abs( actual - expected ) > eps )
				throw new Exception( $"{what}: expected {expected}, got {actual}" );
		}
		void VecAlmost( Vec3 actual, Vec3 expected, float eps, string what )
		{
			if ( !actual.AlmostEquals( expected, eps ) )
				throw new Exception( $"{what}: expected {expected}, got {actual}" );
		}

		MapVertexBrush LoadFixture( string name )
		{
			var path = FindFixture( name );
			if ( path is null ) throw new Exception( $"fixture {name}.json not found under tests/fixtures/hammer/hammer-edit-3d-first-direct/" );
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
			var rel = Path.Combine( "tests", "fixtures", "hammer", "hammer-edit-3d-first-direct", name + ".json" );
			var starts = new[]
			{
				Directory.GetCurrentDirectory(),
				AppContext.BaseDirectory,
				Path.GetDirectoryName( typeof( DirectEdit3DTests ).Assembly.Location ) ?? "",
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

		( DirectEditRay Ray, JsonElement RayJson ) LoadRay( string name )
		{
			var path = FindFixture( "pick-rays" );
			if ( path is null ) throw new Exception( "fixture pick-rays.json not found" );
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			foreach ( var ray in doc.RootElement.GetProperty( "rays" ).EnumerateArray() )
			{
				if ( ray.GetProperty( "name" ).GetString() != name ) continue;
				var o = ray.GetProperty( "origin" );
				var d = ray.GetProperty( "direction" );
				var r = new DirectEditRay(
					new Vec3( o[0].GetSingle(), o[1].GetSingle(), o[2].GetSingle() ),
					new Vec3( d[0].GetSingle(), d[1].GetSingle(), d[2].GetSingle() ) );
				return ( r, ray.Clone() );
			}
			throw new Exception( $"ray {name} not found in pick-rays.json" );
		}

		DirectEditOptions RayOptions( JsonElement rayJson )
		{
			var opt = new DirectEditOptions();
			if ( rayJson.TryGetProperty( "vertex_tolerance", out var vt ) ) opt.PickVertexTolerance = vt.GetDouble();
			if ( rayJson.TryGetProperty( "edge_tolerance", out var et ) ) opt.PickEdgeTolerance = et.GetDouble();
			return opt;
		}

		// Face order of MapVertexBrush.Box / the box fixtures: bottom 0,
		// top 1, front(-Y) 2, back(+Y) 3, left(-X) 4, right(+X) 5.
		// Top face loop is [4, 5, 6, 7]; vertex 6 is (1, 1, 1) on the
		// side-2 box, (32, 32, 32) on the side-64 box.

		Check( "box fixture loads valid with volume 8", () =>
		{
			var box = LoadFixture( "box" );
			var validity = MapVertexEdit.Validate( box );
			Require( validity.IsValid, string.Join( "; ", validity.Defects ) );
			Almost( validity.Volume, 8, 1e-6, "box volume" );
			Require( box.FaceCount == 6, $"faces {box.FaceCount}" );
		} );

		Check( "extruding a box-64 top face by 32 grows the volume by face area x 32", () =>
		{
			var box = LoadFixture( "box-64" );
			Almost( box.Volume(), 262144, 1e-6, "box-64 volume" );
			var area = box.FaceArea( 1 );
			Almost( area, 4096, 1e-6, "top face area" );
			var result = DirectEdit3D.ExtrudeFace( box, 1, 32f );
			Almost( result.VolumeAfter, 393216, 1e-6, "extruded volume" );
			Almost( result.VolumeDelta, area * 32, 1e-6, "volume delta" );
			Require( result.IsValid, string.Join( "; ", result.Validity.Defects ) );
			Require( result.Brush.FaceCount == 10, $"faces {result.Brush.FaceCount} (6 + 4 side quads)" );
			VecAlmost( result.Brush.Vertices[result.Brush.Faces[1][0]], new Vec3( -32, -32, 64 ), 1e-4f, "cap vertex" );
			Require( result.Validity.Lint != null && result.Validity.Lint.BoundaryEdges == 0, "extruded brush must stay watertight" );
			Require( result.AffectedFaces.Contains( 1 ), "cap face must be in the affected set" );
		} );

		Check( "extruding by 0 is a no-op", () =>
		{
			var box = LoadFixture( "box" );
			var result = DirectEdit3D.ExtrudeFace( box, 1, 0f );
			Almost( result.VolumeAfter, 8, 1e-6, "volume" );
			Require( result.Brush.FaceCount == 6, $"faces {result.Brush.FaceCount}" );
			Require( result.IsValid, "zero extrude must stay valid" );
		} );

		Check( "extruding inward by 16 shortens the box and stays valid", () =>
		{
			var box = LoadFixture( "box-64" );
			var result = DirectEdit3D.ExtrudeFace( box, 1, -16f );
			Almost( result.VolumeAfter, 196608, 1e-6, "shortened volume" );
			Almost( result.VolumeDelta, -65536, 1e-6, "volume delta" );
			Require( result.IsValid, string.Join( "; ", result.Validity.Defects ) );
			Require( result.Brush.FaceCount == 6, $"faces {result.Brush.FaceCount} (inward extrude moves in place — no skirt, see ExtrudeFace)" );
			VecAlmost( result.Brush.Vertices[result.Brush.Faces[1][0]], new Vec3( -32, -32, 16 ), 1e-4f, "cap vertex" );
			Require( result.Validity.Lint != null && result.Validity.Lint.BoundaryEdges == 0, "inward extrude must stay watertight" );
		} );

		Check( "dragging a face along its normal grows the volume with no new faces", () =>
		{
			var box = LoadFixture( "box-64" );
			var result = DirectEdit3D.DragFaceAlongNormal( box, 1, 16f );
			Almost( result.VolumeAfter, 327680, 1e-6, "dragged volume" );
			Almost( result.VolumeDelta, 4096 * 16, 1e-6, "volume delta" );
			Require( result.Brush.FaceCount == 6, $"faces {result.Brush.FaceCount} (drag moves shared vertices, adds none)" );
			Require( result.IsValid, string.Join( "; ", result.Validity.Defects ) );
		} );

		Check( "dragging a face in its own plane preserves the volume and stays valid", () =>
		{
			var box = LoadFixture( "box" );
			var result = DirectEdit3D.DragFace( box, 1, new Vec3( 16, 0, 0 ) );
			Almost( result.VolumeAfter, 8, 1e-6, "sheared volume" );
			Require( result.IsValid, string.Join( "; ", result.Validity.Defects ) );
			VecAlmost( result.Brush.Vertices[4], new Vec3( 15, -1, 1 ), 1e-4f, "moved top vertex" );
		} );

		Check( "dragging a face with grid 16 snaps a delta of 10 to 16", () =>
		{
			var box = LoadFixture( "box" );
			var opt = new DirectEditOptions { GridSize = 16 };
			var result = DirectEdit3D.DragFace( box, 1, new Vec3( 10, 0, 0 ), opt );
			VecAlmost( result.Brush.Vertices[4], new Vec3( 15, -1, 1 ), 1e-4f, "snapped top vertex (MapTransform.SnapToGrid(10, 16) = 16)" );
			Require( result.IsValid, string.Join( "; ", result.Validity.Defects ) );
		} );

		Check( "a 3D vertex drag matches the 2D vertex-edit result exactly", () =>
		{
			var box = LoadFixture( "box" );
			var target = new Vec3( 1, 1, 2 );
			var direct = DirectEdit3D.DragVertex( box, 6, target );
			var vertexEdit = MapVertexEdit.MoveVertex( box, 6, target );
			Require( direct.Brush.Vertices.SequenceEqual( vertexEdit.Brush.Vertices ), "vertex arrays differ" );
			Require( direct.Validity.Defects.SequenceEqual( vertexEdit.Validity.Defects ), "validity defects differ" );
			Require( direct.AffectedFaces.SequenceEqual( vertexEdit.AffectedFaces ), "affected faces differ" );
		} );

		Check( "a 3D edge drag matches the 2D vertex-edit result exactly", () =>
		{
			var box = LoadFixture( "box" );
			var delta = new Vec3( 0, 0, 1 );
			var direct = DirectEdit3D.DragEdge( box, 5, 6, delta );
			var vertexEdit = MapVertexEdit.MoveEdge( box, 5, 6, delta );
			Require( direct.Brush.Vertices.SequenceEqual( vertexEdit.Brush.Vertices ), "vertex arrays differ" );
			Require( direct.Validity.Defects.SequenceEqual( vertexEdit.Validity.Defects ), "validity defects differ" );
		} );

		Check( "a ray pick hits the nearest face", () =>
		{
			var box = LoadFixture( "box" );
			var (ray, _) = LoadRay( "top-down" );
			var hit = DirectEdit3D.Pick( box, ray );
			Require( hit.Hit, "ray must hit" );
			Require( hit.Kind == DirectEditHitKind.Face, $"kind {hit.Kind}" );
			Require( hit.FaceIndex == 1, $"face {hit.FaceIndex}" );
			Almost( hit.Distance, 99, 1e-6, "hit distance" );
			VecAlmost( hit.Point, new Vec3( 0, 0, 1 ), 1e-4f, "hit point" );
			VecAlmost( hit.Normal, new Vec3( 0, 0, 1 ), 1e-4f, "hit normal" );
		} );

		Check( "a ray pick hits the nearer of two brushes", () =>
		{
			var near = LoadFixture( "box" );
			var farVerts = near.Vertices.Select( v => v + new Vec3( 0, 0, 10 ) ).ToArray();
			var far = new MapVertexBrush( farVerts, near.Faces, near.Materials );
			var ray = new DirectEditRay( new Vec3( 0, 0, 100 ), new Vec3( 0, 0, -1 ) );
			var hit = DirectEdit3D.Pick( new[] { far, near }, ray );
			Require( hit.Hit && hit.BrushIndex == 0, $"brush {hit.BrushIndex} (far box listed first, z=11 top is nearer)" );
			Almost( hit.Distance, 89, 1e-6, "hit distance" );
			var hit2 = DirectEdit3D.Pick( new[] { near, far }, ray );
			Require( hit2.Hit && hit2.BrushIndex == 1, $"brush {hit2.BrushIndex}" );
		} );

		Check( "a ray that misses returns a named miss", () =>
		{
			var box = LoadFixture( "box" );
			var (ray, _) = LoadRay( "miss" );
			var hit = DirectEdit3D.Pick( box, ray );
			Require( !hit.Hit, "ray must miss" );
			Require( hit.Kind == DirectEditHitKind.None, $"kind {hit.Kind}" );
			Require( hit.FaceIndex == -1, $"face {hit.FaceIndex}" );
		} );

		Check( "a ray aimed at a corner picks the vertex", () =>
		{
			var box = LoadFixture( "box" );
			var (ray, _) = LoadRay( "vertex-6" );
			var hit = DirectEdit3D.Pick( box, ray );
			Require( hit.Hit && hit.Kind == DirectEditHitKind.Vertex, $"kind {hit.Kind}" );
			Require( hit.VertexIndex == 6, $"vertex {hit.VertexIndex}" );
			Require( hit.FaceIndex == 1, $"face {hit.FaceIndex}" );
		} );

		Check( "a ray aimed at an edge midpoint picks the edge, not the vertex", () =>
		{
			var box = LoadFixture( "box" );
			var (ray, rayJson) = LoadRay( "edge-top" );
			var hit = DirectEdit3D.Pick( box, ray, RayOptions( rayJson ) );
			Require( hit.Hit && hit.Kind == DirectEditHitKind.Edge, $"kind {hit.Kind}" );
			Require( ( hit.EdgeA == 6 && hit.EdgeB == 7 ) || ( hit.EdgeA == 7 && hit.EdgeB == 6 ), $"edge ({hit.EdgeA}, {hit.EdgeB})" );
		} );

		Check( "a ray from the side picks the right (+X) face", () =>
		{
			var box = LoadFixture( "box" );
			var (ray, _) = LoadRay( "side" );
			var hit = DirectEdit3D.Pick( box, ray );
			Require( hit.Hit && hit.Kind == DirectEditHitKind.Face, $"kind {hit.Kind}" );
			Require( hit.FaceIndex == 5, $"face {hit.FaceIndex}" );
			VecAlmost( hit.Point, new Vec3( 1, 0, 0 ), 1e-4f, "hit point" );
		} );

		Check( "shearing a box about a pivot preserves the volume and fixes the pivot", () =>
		{
			var box = LoadFixture( "box" );
			var pivot = new Vec3( 0, 0, 0 );
			var result = DirectEdit3D.Shear3D( box, pivot, new Vec3( 0, 0, 1 ), new Vec3( 1, 0, 0 ), 0.5f );
			Almost( result.VolumeAfter, 8, 1e-6, "sheared volume" );
			Require( result.IsValid, string.Join( "; ", result.Validity.Defects ) );
			VecAlmost( result.Brush.Vertices[6], new Vec3( 1.5f, 1, 1 ), 1e-4f, "sheared vertex 6 (x += 0.5 * z)" );
			VecAlmost( result.Brush.Vertices[0], new Vec3( -1.5f, -1, -1 ), 1e-4f, "sheared vertex 0 (x += 0.5 * -1)" );
			var movedPivot = DirectEdit3D.Shear3D( box, new Vec3( 1, 1, 1 ), new Vec3( 0, 0, 1 ), new Vec3( 1, 0, 0 ), 0.5f );
			VecAlmost( movedPivot.Brush.Vertices[6], new Vec3( 1, 1, 1 ), 1e-4f, "pivot vertex never moves" );
			Throws( () => DirectEdit3D.Shear3D( box, pivot, new Vec3( 0, 0, 1 ), new Vec3( 0, 0, 1 ), 0.5f ) );
		} );

		Check( "scaling about a pivot fixes the pivot and multiplies the volume", () =>
		{
			var box = LoadFixture( "box" );
			var aboutOrigin = DirectEdit3D.Scale3D( box, Vec3.Zero, 2f );
			Almost( aboutOrigin.VolumeAfter, 64, 1e-6, "doubled volume" );
			Require( aboutOrigin.IsValid, string.Join( "; ", aboutOrigin.Validity.Defects ) );
			var aboutCorner = DirectEdit3D.Scale3D( box, new Vec3( -1, -1, -1 ), 2f );
			VecAlmost( aboutCorner.Brush.Vertices[0], new Vec3( -1, -1, -1 ), 1e-4f, "pivot vertex never moves" );
			VecAlmost( aboutCorner.Brush.Vertices[6], new Vec3( 3, 3, 3 ), 1e-4f, "opposite vertex doubles away from the pivot" );
			var nonUniform = DirectEdit3D.Scale3D( box, Vec3.Zero, new Vec3( 2, 1, 1 ) );
			Almost( nonUniform.VolumeAfter, 16, 1e-6, "non-uniform volume" );
			Throws( () => DirectEdit3D.Scale3D( box, Vec3.Zero, 0f ) );
			Throws( () => DirectEdit3D.Scale3D( box, Vec3.Zero, new Vec3( 1, 0, 1 ) ) );
		} );

		Check( "a negative scale mirrors and flags the flipped faces inverted, never re-winds", () =>
		{
			var box = LoadFixture( "box" );
			var result = DirectEdit3D.Scale3D( box, Vec3.Zero, new Vec3( -1, 1, 1 ) );
			Almost( result.VolumeAfter, 8, 1e-6, "mirrored volume" );
			Require( !result.IsValid, "mirrored brush must be flagged, not silently fixed" );
			Require( result.InvalidFaceIndices.Length > 0, "no invalid faces named" );
			Require( result.Validity.Defects.Any( d => d.Contains( "inverted" ) ), "inverted faces must be named: " + string.Join( "; ", result.Validity.Defects ) );
		} );

		Check( "a ray-driven drag along an axis and in a plane give the pointer delta", () =>
		{
			var box = LoadFixture( "box" );
			var grab = DirectEdit3D.Pick( box, new DirectEditRay( new Vec3( 0, 0, 100 ), new Vec3( 0, 0, -1 ) ) );
			Require( grab.Hit, "grab must hit" );
			var moved = new DirectEditRay( new Vec3( 5, 0, 100 ), new Vec3( 0, 0, -1 ) );
			Require( DirectEdit3D.TryDragAlongAxis( grab, moved, new Vec3( 1, 0, 0 ), out var axisDelta ), "axis drag must resolve" );
			VecAlmost( axisDelta, new Vec3( 5, 0, 0 ), 1e-4f, "axis drag delta" );
			var moved2 = new DirectEditRay( new Vec3( 5, 5, 100 ), new Vec3( 0, 0, -1 ) );
			var plane = DirectEdit3D.DragPlaneForFace( box, 1, grab.Point );
			Require( DirectEdit3D.TryDragInPlane( grab, moved2, plane, out var planeDelta ), "plane drag must resolve" );
			VecAlmost( planeDelta, new Vec3( 5, 5, 0 ), 1e-4f, "plane drag delta" );
			var parallel = new DirectEditRay( new Vec3( 5, 5, 100 ), new Vec3( 1, 0, 0 ) );
			Require( !DirectEdit3D.TryDragInPlane( grab, parallel, plane, out _ ), "a ray parallel to the drag plane must stall, not guess" );
			var parallelAxis = new DirectEditRay( new Vec3( 5, 0, 100 ), new Vec3( 1, 0, 0 ) );
			Require( !DirectEdit3D.TryDragAlongAxis( grab, parallelAxis, new Vec3( 1, 0, 0 ), out _ ), "a ray parallel to the drag axis must stall, not guess" );
		} );

		Check( "twisting one vertex out of plane is flagged, never repaired", () =>
		{
			var box = LoadFixture( "box" );
			var before = (Vec3[]) box.Vertices.Clone();
			var result = DirectEdit3D.DragVertex( box, 6, new Vec3( 1, 1, 2 ) );
			Require( !result.IsValid, "twisted brush must be flagged" );
			Require( result.InvalidFaceIndices.Contains( 1 ), "top face 1 must be flagged" );
			Require( result.Validity.Faces[1].Defects.HasFlag( MapFaceDefect.NonPlanar ), "top face must be NonPlanar" );
			VecAlmost( result.Brush.Vertices[6], new Vec3( 1, 1, 2 ), 1e-4f, "twisted vertex stays where the drag put it" );
			Require( box.Vertices.SequenceEqual( before ), "input brush must never be mutated" );
		} );

		Check( "scaling a selection scales every brush about the one pivot, in order", () =>
		{
			var a = LoadFixture( "box" );
			var bVerts = a.Vertices.Select( v => v + new Vec3( 10, 0, 0 ) ).ToArray();
			var b = new MapVertexBrush( bVerts, a.Faces, a.Materials );
			var results = DirectEdit3D.ScaleSelection( new[] { a, b }, Vec3.Zero, new Vec3( 2, 2, 2 ) );
			Require( results.Count == 2, $"results {results.Count}" );
			Almost( results[0].VolumeAfter, 64, 1e-6, "first brush volume" );
			Almost( results[1].VolumeAfter, 64, 1e-6, "second brush volume" );
			VecAlmost( results[1].Brush.Vertices[6], new Vec3( 22, 2, 2 ), 1e-4f, "second brush vertex scales about the shared origin pivot" );
			Require( results.All( r => r.IsValid ), "selection scale must stay valid" );
		} );

		Check( "a zero-direction ray throws", () =>
		{
			Throws( () => new DirectEditRay( Vec3.Zero, Vec3.Zero ) );
			Throws( () => DirectEdit3D.Pick( LoadFixture( "box" ), new DirectEditRay( Vec3.Zero, new Vec3( 0, 0, 0 ) ) ) );
		} );
	}
}
