using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// ZBrush ShadowBox / Snapshot3D 3-view orthogonal silhouette CSG (roadmap Lane O2,
/// §4 Talon Plasma Pistol: the receiver frame comes from front + side silhouettes).
///
/// A point is inside the solid exactly when it is inside EVERY view's silhouette:
/// front (x,z) extruded along Y, side (y,z) extruded along X, and top (x,y) when
/// given, extruded along Z. Each view is one outer ring plus any number of hole
/// rings, written inline — rings separated by '|', points by ';', a point as "u,v":
///
///   add ShadowBox name=Receiver
///     front="-5,0;5,0;5,8;-5,8|-1,3;1,3;1,5;-1,5"
///     side="-1,0;1,0;1,8;-1,8" top="..."
///
/// The front hole above is a trigger window: the outer intersection gives the
/// frame, then every hole ring is extruded across its view's full extent and
/// subtracted, so a hole in one view cuts all the way through the solid.
///
/// The views are extruded across each other's ranges by default (the front slab
/// is as thick as the side silhouette is wide), and DepthX/DepthY/DepthZ override
/// one extent with a thickness centred on the auto range's midpoint. A view given
/// alone, with no other view to size it, becomes a slab as thick as its own
/// horizontal range, centred at 0.
///
/// Two forms feed the SAME prism + intersect + subtract path below. The inline-ring
/// form writes rings directly (front=/side=/top=). The `linework=` form (wave 5)
/// reads them from a `.linework` drawing through LineworkReader: one drawing, one
/// layer per view — `linework=<path or drawing id>` plus `frontlayer=`, `sidelayer=`
/// and optionally `toplayer=` naming the layers. A view layer must hold exactly one
/// closed profile (its stroke-carried holes become the view's hole rings); anything
/// else fails loudly naming the layers and strokes found, rather than guessing.
/// When `linework=` is given it wins outright and the inline rings are ignored
/// (the inline params carry defaults, so "both given" cannot be told apart).
/// A layer left unnamed falls back to a layer literally called front/side/top;
/// a missing top layer simply means no third view, exactly like an empty top=.
/// Symmetry-on drawings yield a mirrored profile per stroke, so a view layer in
/// such a drawing resolves to two profiles and fails — flatten sym first.
///
/// The solid is built from prisms intersected with the kernel's exact boolean
/// (MeshBoolean.Apply, default ExactMeshBoolean), not a voxel skin, so the
/// receiver keeps sharp edges. Concave silhouettes (an L-shaped frame) work:
/// caps are ear-clipped, not fanned from a centroid.
/// </summary>
public sealed class ShadowBoxFeature : Feature
{
	public override string TypeName => "ShadowBox";

	public readonly StringParam Front = new( "Front", "-5,0;5,0;5,8;-5,8" );
	public readonly StringParam Side = new( "Side", "-1,0;1,0;1,8;-1,8" );
	public readonly StringParam Top = new( "Top", "" );
	public readonly StringParam Linework = new( "Linework", "" );
	public readonly StringParam FrontLayer = new( "Front layer", "" );
	public readonly StringParam SideLayer = new( "Side layer", "" );
	public readonly StringParam TopLayer = new( "Top layer", "" );
	public readonly FloatParam DepthX = new( "Depth X", 0f, 0f, 1000f, "u" );
	public readonly FloatParam DepthY = new( "Depth Y", 0f, 0f, 1000f, "u" );
	public readonly FloatParam DepthZ = new( "Depth Z", 0f, 0f, 1000f, "u" );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Front, Side, Top, DepthX, DepthY, DepthZ, Linework, FrontLayer, SideLayer, TopLayer };

	protected override void Execute( FeatureContext ctx )
	{
		View front, side, top;
		if ( !string.IsNullOrWhiteSpace( Linework.Value ) )
		{
			(front, side, top) = ResolveLineworkViews();
		}
		else
		{
			if ( string.IsNullOrWhiteSpace( Front.Value ) || string.IsNullOrWhiteSpace( Side.Value ) )
				Fail( "ShadowBox needs a front and a side silhouette.",
					"A single view is a slab, not a solid — the intersection of the views is what gives it depth.",
					"Give both front= and side= rings", "Add top= for a third view" );

			front = ParseView( Front.Value, "front" );
			side = ParseView( Side.Value, "side" );
			top = string.IsNullOrWhiteSpace( Top.Value ) ? null : ParseView( Top.Value, "top" );
		}

		// Each view's extrusion extent comes from the OTHER views' ranges: the
		// front slab is as thick as the side silhouette is wide, the side slab
		// as wide as the front silhouette, the top slab as tall as the front.
		var frontY = RangeOf( DepthY.Clamped, side?.Outer, r => Range( r, p => p.x ),
			top is null ? null : Range( top.Outer, p => p.y ),
			Range( front.Outer, p => p.x ) );
		var sideX = RangeOf( DepthX.Clamped, front.Outer, r => Range( r, p => p.x ),
			top is null ? null : Range( top.Outer, p => p.x ),
			Range( side.Outer, p => p.x ) );
		(float lo, float hi) topZ = top is null ? (0f, 0f) : RangeOf( DepthZ.Clamped, front.Outer, r => Range( r, p => p.y ),
			Range( side.Outer, p => p.y ),
			Range( top.Outer, p => p.x ) );

		var result = Prism( front.Outer, ( p, y ) => new Vec3( p.x, y, p.y ), frontY.lo, frontY.hi );
		result = Boolean( BooleanOp.Intersect, result,
			Prism( side.Outer, ( p, x ) => new Vec3( x, p.x, p.y ), sideX.lo, sideX.hi ), "side" );
		if ( top is not null )
			result = Boolean( BooleanOp.Intersect, result,
				Prism( top.Outer, ( p, z ) => new Vec3( p.x, p.y, z ), topZ.lo, topZ.hi ), "top" );

		// Holes cut all the way through: a hole prism runs past the solid on both
		// ends of its view's axis, so its caps never sit coplanar with the solid.
		foreach ( var hole in front.Holes )
			result = Boolean( BooleanOp.Subtract, result,
				Prism( hole, ( p, y ) => new Vec3( p.x, y, p.y ), frontY.lo - 0.1f, frontY.hi + 0.1f ), "front hole" );
		foreach ( var hole in side.Holes )
			result = Boolean( BooleanOp.Subtract, result,
				Prism( hole, ( p, x ) => new Vec3( x, p.x, p.y ), sideX.lo - 0.1f, sideX.hi + 0.1f ), "side hole" );
		if ( top is not null )
			foreach ( var hole in top.Holes )
				result = Boolean( BooleanOp.Subtract, result,
					Prism( hole, ( p, z ) => new Vec3( p.x, p.y, z ), topZ.lo - 0.1f, topZ.hi + 0.1f ), "top hole" );

		if ( result is null || result.VertexCount == 0 || result.Faces.Count == 0 )
			Fail( "ShadowBox's views do not overlap.",
				"The front, side and top silhouettes intersect to nothing — check their ranges share the same z heights.",
				"Line the silhouettes up on z", "Widen one view's range" );

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name ?? "ShadowBox", result ) );
	}

	PolyMesh Boolean( BooleanOp op, PolyMesh target, PolyMesh tool, string what )
	{
		try { return MeshBoolean.Apply( op, target, tool ); }
		catch ( Exception e )
		{
			Fail( $"ShadowBox's {what} boolean failed.",
				e.Message,
				"Check the silhouettes are simple rings with no self-crossing edges",
				"Simplify a concave corner" );
			return null;
		}
	}

	static (float lo, float hi) RangeOf( float depth, List<Vec2> primary, Func<List<Vec2>, (float lo, float hi)> pick,
		(float lo, float hi)? fallback, (float lo, float hi) own )
	{
		var range = primary is null ? fallback ?? own : pick( primary );
		if ( depth > 0f )
		{
			var mid = (range.lo + range.hi) * 0.5f;
			range = (mid - depth * 0.5f, mid + depth * 0.5f);
		}
		if ( range.hi - range.lo < 1e-4f )
			range = (range.lo - 0.5f, range.lo + 0.5f);
		return range;
	}

	static (float lo, float hi) Range( List<Vec2> ring, Func<Vec2, float> pick )
	{
		var lo = float.MaxValue; var hi = float.MinValue;
		foreach ( var p in ring ) { lo = Math.Min( lo, pick( p ) ); hi = Math.Max( hi, pick( p ) ); }
		return (lo, hi);
	}

	sealed class View
	{
		public List<Vec2> Outer;
		public List<List<Vec2>> Holes = new();
	}

	(View front, View side, View top) ResolveLineworkViews()
	{
		if ( !LineworkReader.TryLoad( Linework.Value, out var doc, out _, out var lerr ) )
			Fail( $"ShadowBox cannot load its drawing: {lerr}",
				"The linework= value did not resolve to a .linework file.",
				"Check the path, or copy the drawing into ~/.local/share/linework/drawings/" );
		var front = ViewFromLayer( doc, FrontLayer.Value, "front", required: true );
		var side = ViewFromLayer( doc, SideLayer.Value, "side", required: true );
		var top = ViewFromLayer( doc, TopLayer.Value, "top", required: false );
		return (front, side, top);
	}

	/// <summary>One view from one drawing layer: the layer named by <paramref name="layerParam"/>,
	/// or a layer literally called <paramref name="view"/> when the param is empty. A view is
	/// exactly one closed profile; zero or several profiles fail loudly instead of guessing
	/// which stroke is the silhouette. An unnamed, missing top layer is not an error — it
	/// just means no third view.</summary>
	static View ViewFromLayer( LineworkDocument doc, string layerParam, string view, bool required )
	{
		var wanted = string.IsNullOrWhiteSpace( layerParam ) ? view : layerParam.Trim();
		var layer = doc.Layers.FirstOrDefault( l =>
			l.Name.Equals( wanted, StringComparison.OrdinalIgnoreCase ) ||
			l.Id.Equals( wanted, StringComparison.OrdinalIgnoreCase ) );
		if ( layer is null )
		{
			if ( !required && string.IsNullOrWhiteSpace( layerParam ) )
				return null;
			Fail( $"ShadowBox found no '{wanted}' layer for its {view} view.",
				$"Layers in this drawing: {string.Join( ", ", doc.Layers.Select( l => l.Name ) )}.",
				$"Give {view}layer= with one of those names", "Add a layer with that name to the drawing" );
		}
		var profiles = LineworkReader.Profiles( doc, wanted );
		if ( profiles.Count == 0 )
			Fail( $"ShadowBox's {view} layer '{wanted}' holds no closed profile.",
				"Linework profiles are closed strokes; open strokes are drawing lines, not silhouettes.",
				"Close the shape in Linework (closed=true)" );
		if ( profiles.Count > 1 )
			Fail( $"ShadowBox's {view} layer '{wanted}' holds {profiles.Count} closed profiles, not one.",
				$"Strokes: {string.Join( ", ", profiles.Select( p => p.StrokeId ) )}. A view is a single silhouette.",
				"Split the layer so each view holds one closed stroke",
				doc.SymOn ? "This drawing has symmetry on, so every stroke also yields a mirrored profile" : "One silhouette per view" );
		return new View { Outer = profiles[0].Outer, Holes = profiles[0].Holes };
	}

	static View ParseView( string spec, string name )
	{
		var rings = spec.Split( '|' ).Select( s => s.Trim() ).Where( s => s.Length > 0 ).ToArray();
		if ( rings.Length == 0 )
			Fail( $"ShadowBox's {name} silhouette is empty.",
				$"The {name}= value has no rings in it.",
				$"Write {name}=\"x,z;x,z;...\" with at least 3 points" );
		var view = new View { Outer = ParseRing( rings[0], name ) };
		foreach ( var ring in rings.Skip( 1 ) )
			view.Holes.Add( ParseRing( ring, name ) );
		return view;
	}

	static List<Vec2> ParseRing( string spec, string name )
	{
		var points = new List<Vec2>();
		foreach ( var token in spec.Split( ';' ) )
		{
			var t = token.Trim();
			if ( t.Length == 0 ) continue;
			var parts = t.Split( ',' );
			float u = 0f, v = 0f;
			if ( parts.Length != 2
				|| !float.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out u )
				|| !float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out v )
				|| !float.IsFinite( u ) || !float.IsFinite( v ) )
				Fail( $"ShadowBox's {name} silhouette has a bad point: '{t}'.",
					"Points are written u,v — two numbers separated by a comma.",
					"Rewrite the point as two numbers, e.g. 5,8" );
			points.Add( new Vec2( u, v ) );
		}
		if ( points.Count < 3 )
			Fail( $"ShadowBox's {name} silhouette has only {points.Count} points.",
				"A ring needs at least 3 points to enclose an area.",
				"Add points until the ring closes" );
		return points;
	}

	/// <summary>Prism over a simple ring: ear-clipped caps, quad sides, winding
	/// normalised by signed volume so the boolean sees outward faces.</summary>
	static PolyMesh Prism( List<Vec2> ring, Func<Vec2, float, Vec3> point, float wLo, float wHi )
	{
		if ( wHi - wLo < 1e-4f )
			Fail( "ShadowBox's silhouette has no extrusion depth.",
				"The view's range along its extrusion axis is zero.",
				"Give the other views a range, or set a Depth value" );
		var tris = Triangulate( ring );
		var mesh = new PolyMesh();
		foreach ( var p in ring ) mesh.AddVertex( point( p, wLo ) );
		foreach ( var p in ring ) mesh.AddVertex( point( p, wHi ) );
		var n = ring.Count;
		for ( var i = 0; i < n; i++ )
		{
			var j = (i + 1) % n;
			mesh.AddFace( new[] { i, j, n + j, n + i } );
		}
		foreach ( var t in tris ) mesh.AddFace( new[] { n + t[0], n + t[1], n + t[2] } );
		foreach ( var t in tris ) mesh.AddFace( new[] { t[2], t[1], t[0] } );

		// Signed volume: negative means every face points inward — flip them all.
		var vol = 0.0;
		foreach ( var f in mesh.Faces )
		{
			if ( f.Indices.Length < 3 ) continue;
			var a = mesh.Positions[f.Indices[0]];
			for ( var k = 1; k + 1 < f.Indices.Length; k++ )
			{
				var b = mesh.Positions[f.Indices[k]];
				var c = mesh.Positions[f.Indices[k + 1]];
				vol += Vec3.Dot( a, Vec3.Cross( b - a, c - a ) ) / 6.0;
			}
		}
		if ( vol < 0 )
			foreach ( var f in mesh.Faces ) Array.Reverse( f.Indices );
		return mesh;
	}

	/// <summary>Ear clipping for a simple polygon. Returns triangles as index
	/// triples into the (possibly reversed) ring; the caller reorders winding.</summary>
	static List<int[]> Triangulate( List<Vec2> ring )
	{
		var pts = ring.ToList();
		var area = 0f;
		for ( var i = 0; i < pts.Count; i++ )
		{
			var a = pts[i]; var b = pts[(i + 1) % pts.Count];
			area += a.x * b.y - b.x * a.y;
		}
		if ( area < 0 ) pts.Reverse();
		var idx = Enumerable.Range( 0, pts.Count ).ToList();
		var tris = new List<int[]>();
		while ( idx.Count > 3 )
		{
			var clipped = false;
			for ( var i = 0; i < idx.Count; i++ )
			{
				var prev = pts[idx[(i + idx.Count - 1) % idx.Count]];
				var cur = pts[idx[i]];
				var next = pts[idx[(i + 1) % idx.Count]];
				var cross = (cur.x - prev.x) * (next.y - cur.y) - (cur.y - prev.y) * (next.x - cur.x);
				if ( cross <= 1e-6f ) continue;
				var ear = true;
				for ( var j = 0; j < idx.Count; j++ )
				{
					if ( j == (i + idx.Count - 1) % idx.Count || j == i || j == (i + 1) % idx.Count ) continue;
					if ( PointInTriangle( pts[idx[j]], prev, cur, next ) ) { ear = false; break; }
				}
				if ( !ear ) continue;
				tris.Add( new[] { idx[(i + idx.Count - 1) % idx.Count], idx[i], idx[(i + 1) % idx.Count] } );
				idx.RemoveAt( i );
				clipped = true;
				break;
			}
			if ( !clipped )
				Fail( "ShadowBox's silhouette could not be triangulated.",
					"The ring is probably self-crossing or has a repeated point.",
					"Draw a simple ring with no edge crossing another", "Remove duplicate points" );
		}
		tris.Add( new[] { idx[0], idx[1], idx[2] } );
		return tris;
	}

	static bool PointInTriangle( Vec2 p, Vec2 a, Vec2 b, Vec2 c )
	{
		float Sign( Vec2 p1, Vec2 p2, Vec2 p3 ) => (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
		var d1 = Sign( p, a, b ); var d2 = Sign( p, b, c ); var d3 = Sign( p, c, a );
		var hasNeg = d1 < -1e-6f || d2 < -1e-6f || d3 < -1e-6f;
		var hasPos = d1 > 1e-6f || d2 > 1e-6f || d3 > 1e-6f;
		return !(hasNeg && hasPos);
	}
}
