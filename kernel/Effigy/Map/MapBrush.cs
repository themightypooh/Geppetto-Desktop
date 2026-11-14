using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

// MapBrush — plane-based convex brush core (Hammer Desktop P1).
// Board: hammer-brush-core-plane-based-brushes (Geppetto Desktop).
//
// A Source brush is a convex solid: the intersection of half-spaces. Each face is one
// plane (unit outward normal + distance, so the solid is Normal.p <= Distance) plus a
// FaceMaterial (material name + FaceUv: offset/scale/rotation + lightmap scale +
// smoothing group). This is the kernel type the Map layer's FaceMaterial.cs was written
// for: it says "when MapBrush lands, it stores one FaceMaterial per face".
//
// This is NOT kernel/Effigy/Brush.cs. That file is a sculpt brush (BrushKind /
// BrushStroke / BrushUndo), not a brush solid, and this type deliberately does not reuse
// the name Brush.
//
// INVALID IS A NAMED REASON, NEVER A SILENT FIX. Constructors store what they are given
// (normalising only the representation of a plane: a non-unit normal is divided out and
// the distance scaled by the same factor, which is the same geometric plane). Call
// IsValid / Validate() to find out what is wrong: Validate() returns the defects, each
// with a machine-readable Code, and Execute() on MapBrushFeature refuses an invalid
// brush with those codes in the failure instead of building something else.
//
// PLANE CONVENTION. Normal is unit length and points OUTWARD; Distance is the plane's
// offset so Normal.p = Distance on the plane and Normal.p <= Distance inside the solid.
// Vertices are SOLVED from the planes (triple intersections kept only when they satisfy
// every half-space), never stored, so a brush cannot drift out of agreement with its
// planes. Volume and centroid come from the solved polygons by the divergence theorem.
//
// UNITS. Kernel inches, Z-up, like the rest of Effigy.

/// <summary>One face of a <see cref="MapBrush"/>: a plane plus its surface data.</summary>
public readonly struct MapBrushFace : IEquatable<MapBrushFace>
{
	/// <summary>Unit outward normal. The solid is the half-space Normal.p &lt;= Distance.</summary>
	public readonly Vec3 Normal;

	/// <summary>Plane offset: Normal.p = Distance on the plane.</summary>
	public readonly float Distance;

	/// <summary>Material name + UV transform. Never null: an omitted material is nodraw.</summary>
	public readonly FaceMaterial Material;

	public MapBrushFace( Vec3 normal, float distance, FaceMaterial material = null )
	{
		Normal = normal;
		Distance = distance;
		Material = material ?? FaceMaterial.Default;
	}

	/// <summary>Plane + material name, default UV. An omitted UV is the default UV, not a
	/// zero scale: a zero scale would collapse the texture and Validate() would reject it.</summary>
	public MapBrushFace( Vec3 normal, float distance, string materialName, FaceUv uv = default )
		: this( normal, distance,
			new FaceMaterial( string.IsNullOrWhiteSpace( materialName ) ? ToolTexture.NoDrawName : materialName,
				uv.Equals( default( FaceUv ) ) ? FaceUv.Default : uv ) )
	{
	}

	/// <summary>Material name: "brick/brickwall001", "tools/toolsnodraw", ...</summary>
	public string MaterialId => Material.MaterialName;

	/// <summary>UV offset/scale/rotation + lightmap scale + smoothing group.</summary>
	public FaceUv Uv => Material.Uv;

	/// <summary>Signed distance from the plane: 0 on it, negative inside, positive outside.</summary>
	public float SignedDistance( Vec3 p ) => Vec3.Dot( Normal, p ) - Distance;

	public bool Equals( MapBrushFace o ) =>
		Normal.Equals( o.Normal ) && Distance == o.Distance && Material.Equals( o.Material );

	public override bool Equals( object obj ) => obj is MapBrushFace other && Equals( other );
	public override int GetHashCode() => HashCode.Combine( Normal, Distance, Material );
	public override string ToString() => $"n={Normal} d={Distance:0.####} {Material}";
}

/// <summary>One named thing wrong with a <see cref="MapBrush"/>. <see cref="Code"/> is the
/// machine-readable reason; <see cref="Message"/> says it in words; <see cref="FaceIndex"/>
/// is the face it is about, or -1 when it is about the whole brush.</summary>
public readonly struct MapBrushDefect
{
	/// <summary>Fewer than 4 faces: no convex solid exists.</summary>
	public const string TooFewFaces = "TooFewFaces";

	/// <summary>A face normal is zero-length, non-finite, or its distance is non-finite.</summary>
	public const string DegeneratePlane = "DegeneratePlane";

	/// <summary>The planes do not enclose a volume (fewer than 4 solved vertices).</summary>
	public const string Unbounded = "Unbounded";

	/// <summary>A face has fewer than 3 solved vertices, or a defining loop has fewer than 3 points.</summary>
	public const string DegenerateFace = "DegenerateFace";

	/// <summary>The solved solid has (near-)zero volume.</summary>
	public const string ZeroVolume = "ZeroVolume";

	/// <summary>A defining face loop is not planar: its points leave the face plane.</summary>
	public const string NonPlanarFace = "NonPlanarFace";

	/// <summary>The defining loops describe something the plane set cannot represent: a
	/// concave face polygon, or a loop vertex outside some face's half-space.</summary>
	public const string ConcaveInput = "ConcaveInput";

	/// <summary>Two faces share a plane AND the same solved vertices: one face twice.</summary>
	public const string DuplicateFace = "DuplicateFace";

	/// <summary>Two faces share a plane but cover different vertices: one face split in two.</summary>
	public const string CoplanarSplitFace = "CoplanarSplitFace";

	/// <summary>A face UV scale component is zero (FaceUv: the texture collapses to a line).</summary>
	public const string DegenerateUvScale = "DegenerateUvScale";

	/// <summary>A face lightmap scale is not positive.</summary>
	public const string DegenerateLightmapScale = "DegenerateLightmapScale";

	/// <summary>A face smoothing group is negative.</summary>
	public const string InvalidSmoothingGroup = "InvalidSmoothingGroup";

	/// <summary>A FaceUv defect MapBrush does not have a code for: kept visible, never dropped.</summary>
	public const string InvalidFaceUv = "InvalidFaceUv";

	public readonly string Code;
	public readonly string Message;
	public readonly int FaceIndex;

	public MapBrushDefect( string code, string message, int faceIndex = -1 )
	{
		Code = code;
		Message = message;
		FaceIndex = faceIndex;
	}

	public override string ToString() => FaceIndex >= 0
		? $"{Code} (face {FaceIndex}): {Message}"
		: $"{Code}: {Message}";
}

/// <summary>Plane-based convex brush solid: faces as planes + per-face material and UV.
///
/// Build it with <see cref="FromPlanes"/> (faces as planes, the Hammer form),
/// <see cref="FromVertices(IReadOnlyList{Vec3})"/> (a point cloud, via the convex hull)
/// or <see cref="FromFaceVertices"/> (per-face vertex loops, the vertex-tool form, which
/// is where twisted-vertex and concave input can be detected). <see cref="Box"/> builds
/// an axis-aligned box. Vertices, volume and centroid are solved from the planes on
/// demand and cached; the brush itself is immutable after construction.
/// </summary>
public sealed class MapBrush
{
	/// <summary>Half-space / planarity tolerance, kernel inches.</summary>
	public const float PlaneEps = 1e-3f;

	/// <summary>Vertex dedupe tolerance, kernel inches.</summary>
	public const float VertexEps = 1e-4f;

	/// <summary>Volume tolerance, cubic inches: at or below this the solid is zero-volume.</summary>
	public const float VolumeEps = 1e-6f;

	public IReadOnlyList<MapBrushFace> Faces => _faces;
	public int FaceCount => _faces.Count;

	readonly List<MapBrushFace> _faces;

	/// <summary>Defining vertex loops, one per face, when built by FromFaceVertices; null
	/// otherwise. Kept so Validate() can check planarity and concavity of the loops the
	/// brush was defined from — the planes alone cannot show a twisted vertex.</summary>
	readonly List<IReadOnlyList<Vec3>> _loops;

	bool _solved;
	List<Vec3> _vertices = new();
	List<List<int>> _polygons = new();
	float _volume;
	Vec3 _centroid;

	MapBrush( List<MapBrushFace> faces, List<IReadOnlyList<Vec3>> loops )
	{
		_faces = faces;
		_loops = loops;
	}

	/// <summary>Faces as planes, the Hammer form. A non-unit normal is divided out and the
	/// distance scaled by the same factor — the same geometric plane, not a fix: a
	/// zero-length normal is kept and Validate() reports DegeneratePlane for it.</summary>
	public static MapBrush FromPlanes( IEnumerable<MapBrushFace> faces )
	{
		if ( faces is null ) throw new ArgumentNullException( nameof( faces ) );
		var list = faces.ToList();
		for ( var i = 0; i < list.Count; i++ )
		{
			var f = list[i];
			var len = f.Normal.Length;
			if ( len > 1e-12f && MathF.Abs( len - 1f ) > 1e-6f )
				list[i] = new MapBrushFace( f.Normal / len, f.Distance / len, f.Material );
		}
		return new MapBrush( list, null );
	}

	/// <summary>A point cloud, via the convex hull: hull triangles on one plane merge into one
	/// brush face. Faces are nodraw — a cloud carries no textures. A cloud that encloses no
	/// volume (fewer than 4 points, flat, collinear) yields an empty brush whose Validate()
	/// says TooFewFaces / Unbounded, rather than an exception: the caller decides.</summary>
	public static MapBrush FromVertices( IReadOnlyList<Vec3> points )
	{
		if ( points is null ) throw new ArgumentNullException( nameof( points ) );
		var hull = ConvexHull.Build( points );
		if ( hull is null ) return new MapBrush( new List<MapBrushFace>(), null );
		var (hullPoints, hullFaces) = hull.Value;

		var scale = 1f;
		foreach ( var p in hullPoints )
			scale = MathF.Max( scale, MathF.Max( MathF.Abs( p.x ), MathF.Max( MathF.Abs( p.y ), MathF.Abs( p.z ) ) ) );

		// Hull triangles, grouped by plane. Hull faces are wound outward, so the triangle
		// normal already points out of the solid.
		var planes = new List<(Vec3 Normal, float Distance)>();
		foreach ( var tri in hullFaces )
		{
			var a = hullPoints[tri.A];
			var b = hullPoints[tri.B];
			var c = hullPoints[tri.C];
			var n = Vec3.Cross( b - a, c - a );
			if ( n.LengthSquared < 1e-12f ) continue;
			n = n.Normal;
			var d = Vec3.Dot( n, a );
			var merged = false;
			for ( var g = 0; g < planes.Count; g++ )
			{
				if ( Vec3.Dot( n, planes[g].Normal ) > 1f - 1e-6f
					&& MathF.Abs( d - planes[g].Distance ) <= PlaneEps * scale )
				{
					merged = true;
					break;
				}
			}
			if ( !merged ) planes.Add( (n, d) );
		}
		return FromPlanes( planes.Select( p => new MapBrushFace( p.Normal, p.Distance ) ) );
	}

	/// <summary>Per-face vertex loops, the vertex-tool form. The plane of each loop comes from
	/// its first three non-collinear points, oriented outward away from the centroid of all
	/// loop vertices; the loops are kept so Validate() can report a twisted (non-planar)
	/// loop, a concave loop, or a loop the planes cannot represent. A loop with fewer than
	/// 3 points, or with no non-collinear triple, becomes a degenerate face that Validate()
	/// reports — it is not silently dropped.</summary>
	public static MapBrush FromFaceVertices( IReadOnlyList<IReadOnlyList<Vec3>> faceLoops )
	{
		if ( faceLoops is null ) throw new ArgumentNullException( nameof( faceLoops ) );
		var centroid = Vec3.Zero;
		var count = 0;
		foreach ( var loop in faceLoops )
		{
			if ( loop is null ) throw new ArgumentException( "A face loop is null.", nameof( faceLoops ) );
			foreach ( var p in loop ) { centroid += p; count++; }
		}
		if ( count > 0 ) centroid /= count;

		var faces = new List<MapBrushFace>();
		var loops = new List<IReadOnlyList<Vec3>>();
		foreach ( var loop in faceLoops )
		{
			loops.Add( loop );
			if ( loop.Count < 3 )
			{
				faces.Add( new MapBrushFace( Vec3.Zero, 0f ) );
				continue;
			}
			var a = loop[0];
			var n = Vec3.Zero;
			var found = false;
			for ( var i = 1; i + 1 < loop.Count && !found; i++ )
			{
				for ( var j = i + 1; j < loop.Count; j++ )
				{
					var cross = Vec3.Cross( loop[i] - a, loop[j] - a );
					if ( cross.LengthSquared > 1e-12f ) { n = cross.Normal; found = true; break; }
				}
			}
			if ( !found )
			{
				faces.Add( new MapBrushFace( Vec3.Zero, 0f ) );
				continue;
			}
			if ( Vec3.Dot( n, centroid - a ) > 0f ) n = -n;
			faces.Add( new MapBrushFace( n, Vec3.Dot( n, a ) ) );
		}
		return new MapBrush( faces, loops );
	}

	/// <summary>Axis-aligned box from its corners. Every face gets <paramref name="materialName"/>
	/// (nodraw when blank), default UV.</summary>
	public static MapBrush Box( Vec3 min, Vec3 max, string materialName = null )
	{
		var mat = string.IsNullOrWhiteSpace( materialName ) ? FaceMaterial.Default : new FaceMaterial( materialName );
		return FromPlanes( new[]
		{
			new MapBrushFace( new Vec3( -1, 0, 0 ), -min.x, mat ),
			new MapBrushFace( new Vec3( 1, 0, 0 ), max.x, mat ),
			new MapBrushFace( new Vec3( 0, -1, 0 ), -min.y, mat ),
			new MapBrushFace( new Vec3( 0, 1, 0 ), max.y, mat ),
			new MapBrushFace( new Vec3( 0, 0, -1 ), -min.z, mat ),
			new MapBrushFace( new Vec3( 0, 0, 1 ), max.z, mat ),
		} );
	}

	/// <summary>Solved vertices, deduplicated. Empty when the planes enclose nothing.</summary>
	public IReadOnlyList<Vec3> Vertices { get { Solve(); return _vertices; } }

	/// <summary>Enclosed volume, cubic inches. 0 when the brush is degenerate; sign is
	/// positive for outward-wound faces.</summary>
	public float Volume { get { Solve(); return _volume; } }

	/// <summary>Volume-weighted centroid. Zero when the volume is (near-)zero.</summary>
	public Vec3 Centroid { get { Solve(); return _centroid; } }

	/// <summary>True when Validate() finds nothing wrong.</summary>
	public bool IsValid => Validate().Count == 0;

	/// <summary>Ordered polygon of one face: solved vertices, counter-clockwise seen from
	/// outside (the face normal side), so the Newell normal points outward. Empty when the
	/// face has no solved polygon.</summary>
	public IReadOnlyList<Vec3> FaceVertices( int faceIndex )
	{
		Solve();
		if ( faceIndex < 0 || faceIndex >= _faces.Count ) throw new ArgumentOutOfRangeException( nameof( faceIndex ) );
		return _polygons[faceIndex].Select( i => _vertices[i] ).ToList();
	}

	/// <summary>One UV per corner of one face, by the face's FaceUv: projected onto the face
	/// plane (FaceUv.PlaneAxes) relative to the polygon's first vertex, then transformed.
	/// Empty when the face has no solved polygon.</summary>
	public Vec2[] FaceUvs( int faceIndex )
	{
		if ( faceIndex < 0 || faceIndex >= _faces.Count ) throw new ArgumentOutOfRangeException( nameof( faceIndex ) );
		var polygon = FaceVertices( faceIndex );
		if ( polygon.Count < 3 ) return Array.Empty<Vec2>();
		return _faces[faceIndex].Material.Uv.ComputeUvs( polygon, _faces[faceIndex].Normal );
	}

	/// <summary>All named defects, or empty when the brush is valid. Never throws for a
	/// geometrically bad brush: the defects ARE the answer.</summary>
	public IReadOnlyList<MapBrushDefect> Validate()
	{
		Solve();
		var defects = new List<MapBrushDefect>();

		if ( _faces.Count < 4 )
			defects.Add( new MapBrushDefect( MapBrushDefect.TooFewFaces,
				$"A convex solid needs at least 4 planes, got {_faces.Count}." ) );

		for ( var i = 0; i < _faces.Count; i++ )
		{
			var f = _faces[i];
			if ( !IsFinite( f.Normal ) || f.Normal.LengthSquared < 1e-12f || !float.IsFinite( f.Distance ) )
				defects.Add( new MapBrushDefect( MapBrushDefect.DegeneratePlane,
					$"Face {i} has a zero-length or non-finite normal, or a non-finite distance.", i ) );

			foreach ( var uvDefect in f.Uv.Validate() )
				defects.Add( new MapBrushDefect( ClassifyUvDefect( uvDefect ), uvDefect, i ) );
		}

		if ( _loops is not null )
		{
			for ( var i = 0; i < _loops.Count; i++ )
			{
				var loop = _loops[i];
				if ( loop.Count < 3 )
				{
					defects.Add( new MapBrushDefect( MapBrushDefect.DegenerateFace,
						$"Face {i} loop has {loop.Count} points; a face needs at least 3.", i ) );
					continue;
				}
				var f = _faces[i];
				if ( f.Normal.LengthSquared < 1e-12f ) continue;

				var maxDev = 0f;
				foreach ( var p in loop )
					maxDev = MathF.Max( maxDev, MathF.Abs( f.SignedDistance( p ) ) );
				if ( maxDev > PlaneEps )
					defects.Add( new MapBrushDefect( MapBrushDefect.NonPlanarFace,
						$"Face {i} loop leaves its plane by up to {maxDev:0.####}; a brush face must be planar.", i ) );

				var outside = -1;
				for ( var v = 0; v < loop.Count && outside < 0; v++ )
					for ( var k = 0; k < _faces.Count; k++ )
						if ( _faces[k].SignedDistance( loop[v] ) > PlaneEps ) { outside = k; break; }
				if ( outside >= 0 )
					defects.Add( new MapBrushDefect( MapBrushDefect.ConcaveInput,
						$"Face {i} loop has a vertex outside face {outside}'s half-space: the loops describe a solid the planes cannot represent.", i ) );
				else if ( IsConcaveLoop( f, loop ) )
					defects.Add( new MapBrushDefect( MapBrushDefect.ConcaveInput,
						$"Face {i} loop is a concave polygon; a convex brush face cannot be concave.", i ) );
			}
		}

		for ( var i = 0; i < _faces.Count; i++ )
		{
			for ( var j = i + 1; j < _faces.Count; j++ )
			{
				if ( Vec3.Dot( _faces[i].Normal, _faces[j].Normal ) <= 1f - 1e-6f ) continue;
				if ( MathF.Abs( _faces[i].Distance - _faces[j].Distance ) > PlaneEps ) continue;
				// Same plane, different face. Whether it is a duplicate or a split is judged
				// on the defining loops when there are any (two loops on one plane cover
				// different ground even though the solved polygon is the same for both);
				// otherwise on the solved polygons.
				bool sameSet;
				if ( _loops is not null )
				{
					if ( _loops[i].Count < 3 || _loops[j].Count < 3 ) continue;
					sameSet = SameLoopSet( _loops[i], _loops[j] );
				}
				else
				{
					if ( _polygons[i].Count < 3 || _polygons[j].Count < 3 ) continue;
					sameSet = SameVertexSet( _polygons[i], _polygons[j] );
				}
				defects.Add( new MapBrushDefect(
					sameSet ? MapBrushDefect.DuplicateFace : MapBrushDefect.CoplanarSplitFace,
					sameSet
						? $"Faces {i} and {j} share a plane and the same vertices: one face twice."
						: $"Faces {i} and {j} share a plane but cover different vertices: one face split in two.",
					j ) );
			}
		}

		if ( _vertices.Count < 4 )
			defects.Add( new MapBrushDefect( MapBrushDefect.Unbounded,
				$"The planes do not enclose a volume: only {_vertices.Count} intersection point(s) satisfy every half-space." ) );

		for ( var i = 0; i < _faces.Count; i++ )
			if ( _polygons[i].Count < 3 )
				defects.Add( new MapBrushDefect( MapBrushDefect.DegenerateFace,
					$"Face {i} has only {_polygons[i].Count} solved vertices; a face needs at least 3.", i ) );

		if ( MathF.Abs( _volume ) <= VolumeEps )
			defects.Add( new MapBrushDefect( MapBrushDefect.ZeroVolume,
				$"The solved solid has volume {_volume:0.######}: a brush must enclose space." ) );

		return defects;
	}

	/// <summary>Bake to a mesh: one n-gon per brush face, per-corner UVs from the face's
	/// FaceUv, material slot from <paramref name="materialSlot"/> (slot 0 when null, by
	/// material name in first-appearance order when the caller passes
	/// MapBrushFeature.MaterialSlot). Nodraw faces are kept in the brush but dropped here:
	/// FaceMaterial says they contribute nothing to the baked mesh. Faces with no solved
	/// polygon are dropped too — bake an invalid brush and you get a partial mesh, which is
	/// why MapBrushFeature refuses invalid brushes instead of baking them.</summary>
	public PolyMesh ToMesh( Func<string, int> materialSlot = null )
	{
		Solve();
		var mesh = new PolyMesh();
		mesh.Positions.AddRange( _vertices );
		for ( var f = 0; f < _faces.Count; f++ )
		{
			var poly = _polygons[f];
			if ( poly.Count < 3 ) continue;
			if ( !_faces[f].Material.ContributesToBakedMesh ) continue;
			var uvs = FaceUvs( f );
			var slot = materialSlot is null ? 0 : materialSlot( _faces[f].MaterialId );
			mesh.AddFace( poly.ToArray(), uvs, slot );
		}
		return mesh;
	}

	static string ClassifyUvDefect( string message )
	{
		if ( message.StartsWith( "UV scale", StringComparison.Ordinal ) ) return MapBrushDefect.DegenerateUvScale;
		if ( message.StartsWith( "Lightmap scale", StringComparison.Ordinal ) ) return MapBrushDefect.DegenerateLightmapScale;
		if ( message.StartsWith( "Smoothing group", StringComparison.Ordinal ) ) return MapBrushDefect.InvalidSmoothingGroup;
		return MapBrushDefect.InvalidFaceUv;
	}

	static bool IsFinite( Vec3 v ) => float.IsFinite( v.x ) && float.IsFinite( v.y ) && float.IsFinite( v.z );

	bool SameVertexSet( List<int> a, List<int> b )
	{
		if ( a.Count != b.Count ) return false;
		foreach ( var i in a )
		{
			var found = false;
			foreach ( var j in b )
				if ( _vertices[i].AlmostEquals( _vertices[j], VertexEps ) ) { found = true; break; }
			if ( !found ) return false;
		}
		return true;
	}

	bool SameLoopSet( IReadOnlyList<Vec3> a, IReadOnlyList<Vec3> b )
	{
		if ( a.Count != b.Count ) return false;
		foreach ( var p in a )
		{
			var found = false;
			foreach ( var q in b )
				if ( p.AlmostEquals( q, VertexEps ) ) { found = true; break; }
			if ( !found ) return false;
		}
		return true;
	}

	/// <summary>True when the loop, projected onto its face plane, turns both ways: a
	/// concave polygon. Collinear runs (cross ~ 0) are not turns either way.</summary>
	static bool IsConcaveLoop( MapBrushFace face, IReadOnlyList<Vec3> loop )
	{
		var (uAxis, vAxis) = FaceUv.PlaneAxes( face.Normal );
		var turnsPositive = false;
		var turnsNegative = false;
		for ( var i = 0; i < loop.Count; i++ )
		{
			var p0 = loop[i];
			var p1 = loop[(i + 1) % loop.Count];
			var p2 = loop[(i + 2) % loop.Count];
			var e1 = new Vec2( Vec3.Dot( p1 - p0, uAxis ), Vec3.Dot( p1 - p0, vAxis ) );
			var e2 = new Vec2( Vec3.Dot( p2 - p1, uAxis ), Vec3.Dot( p2 - p1, vAxis ) );
			var cross = Vec2.Cross( e1, e2 );
			if ( cross > 1e-6f ) turnsPositive = true;
			else if ( cross < -1e-6f ) turnsNegative = true;
			if ( turnsPositive && turnsNegative ) return true;
		}
		return false;
	}

	void Solve()
	{
		if ( _solved ) return;
		_solved = true;

		_vertices = new List<Vec3>();
		_polygons = new List<List<int>>();
		for ( var i = 0; i < _faces.Count; i++ ) _polygons.Add( new List<int>() );

		for ( var i = 0; i < _faces.Count; i++ )
			for ( var j = i + 1; j < _faces.Count; j++ )
				for ( var k = j + 1; k < _faces.Count; k++ )
				{
					if ( !TryIntersect( _faces[i], _faces[j], _faces[k], out var p ) ) continue;
					if ( !InsideAll( p ) ) continue;
					if ( IndexOfVertex( p ) >= 0 ) continue;
					_vertices.Add( p );
				}

		for ( var f = 0; f < _faces.Count; f++ )
		{
			if ( _faces[f].Normal.LengthSquared < 1e-12f ) continue;
			var onPlane = new List<int>();
			for ( var v = 0; v < _vertices.Count; v++ )
				if ( MathF.Abs( _faces[f].SignedDistance( _vertices[v] ) ) <= PlaneEps ) onPlane.Add( v );
			_polygons[f] = OrderAround( _faces[f], onPlane );
		}

		float volume = 0f;
		var moment = Vec3.Zero;
		var seenPlanes = new List<(Vec3 Normal, float Distance)>();
		for ( var f = 0; f < _faces.Count; f++ )
		{
			var poly = _polygons[f];
			if ( poly.Count < 3 ) continue;
			// A duplicated plane contributes once: summing it twice would double-count
			// that face's share of the volume (and Validate() already reports the duplicate).
			var duplicatePlane = false;
			foreach ( var seen in seenPlanes )
				if ( Vec3.Dot( _faces[f].Normal, seen.Normal ) > 1f - 1e-6f
					&& MathF.Abs( _faces[f].Distance - seen.Distance ) <= PlaneEps )
				{
					duplicatePlane = true;
					break;
				}
			if ( duplicatePlane ) continue;
			seenPlanes.Add( (_faces[f].Normal, _faces[f].Distance) );
			var p0 = _vertices[poly[0]];
			for ( var n = 1; n + 1 < poly.Count; n++ )
			{
				var a = _vertices[poly[n]];
				var b = _vertices[poly[n + 1]];
				var vt = Vec3.Dot( p0, Vec3.Cross( a, b ) ) / 6f;
				volume += vt;
				moment += (p0 + a + b) * (vt / 4f);
			}
		}
		_volume = volume;
		_centroid = MathF.Abs( volume ) > VolumeEps ? moment / volume : Vec3.Zero;
	}

	bool InsideAll( Vec3 p )
	{
		foreach ( var f in _faces )
			if ( f.SignedDistance( p ) > PlaneEps ) return false;
		return true;
	}

	int IndexOfVertex( Vec3 p )
	{
		for ( var i = 0; i < _vertices.Count; i++ )
			if ( _vertices[i].AlmostEquals( p, VertexEps ) ) return i;
		return -1;
	}

	/// <summary>Counter-clockwise seen from outside (the face-normal side): ascending polar
	/// angle in the right-handed FaceUv.PlaneAxes basis, so the Newell normal points outward.</summary>
	List<int> OrderAround( MapBrushFace face, List<int> indices )
	{
		if ( indices.Count < 3 ) return indices;
		var (uAxis, vAxis) = FaceUv.PlaneAxes( face.Normal );
		var c = Vec3.Zero;
		foreach ( var i in indices ) c += _vertices[i];
		c /= indices.Count;
		indices.Sort( ( a, b ) =>
		{
			var pa = _vertices[a] - c;
			var pb = _vertices[b] - c;
			return MathF.Atan2( Vec3.Dot( pa, vAxis ), Vec3.Dot( pa, uAxis ) )
				.CompareTo( MathF.Atan2( Vec3.Dot( pb, vAxis ), Vec3.Dot( pb, uAxis ) ) );
		} );
		return indices;
	}

	/// <summary>Triple-plane intersection: p = (d1(n2xn3) + d2(n3xn1) + d3(n1xn2)) / n1.(n2xn3).
	/// False when the planes are parallel / coincident (determinant ~ 0) or non-finite.</summary>
	static bool TryIntersect( MapBrushFace a, MapBrushFace b, MapBrushFace c, out Vec3 p )
	{
		p = Vec3.Zero;
		var n1 = a.Normal;
		var n2 = b.Normal;
		var n3 = c.Normal;
		var det = Vec3.Dot( n1, Vec3.Cross( n2, n3 ) );
		if ( MathF.Abs( det ) < 1e-9f ) return false;
		p = (Vec3.Cross( n2, n3 ) * a.Distance + Vec3.Cross( n3, n1 ) * b.Distance
			+ Vec3.Cross( n1, n2 ) * c.Distance) / det;
		return float.IsFinite( p.x ) && float.IsFinite( p.y ) && float.IsFinite( p.z );
	}
}

/// <summary>A MapBrush as a document feature, so StudioDocument can persist it.
///
/// PERSISTENCE. StudioDocument's writer only handles a fixed set of field types (see
/// WriteField: params, List&lt;int&gt;/List&lt;string&gt;/List&lt;Vec2&gt;/List&lt;Vec3&gt;, ...),
/// so the brush rides as six parallel per-face lists, one entry per face in face order:
/// PlaneNormals (vec3s), FaceScalars (vec3s: x = plane distance, y = UV rotation degrees,
/// z = lightmap scale — List&lt;float&gt; has no save case, so the three scalars share a
/// Vec3), UvOffsets (vec2s), UvScales (vec2s), SmoothingGroups (ints), MaterialIds
/// (texts). <see cref="Brush"/> rebuilds the MapBrush from them; <see cref="SetBrush"/>
/// writes them. Material ids are never blank here (FaceMaterial forbids blank names), so
/// the texts line never loses an entry to the reader's empty-entry split.
/// </summary>
[Feature( "Map Brush", Category = "Map", Description = "Convex plane-based brush solid (Hammer-style): faces are planes with per-face material and UVs." )]
public sealed class MapBrushFeature : Feature
{
	public override string TypeName => "Map Brush";

	/// <summary>No dialog parameters: a brush IS its faces. The faces are the persisted
	/// fields below, edited by the map tools, not by a parameter row.</summary>
	public override IReadOnlyList<IParam> Parameters => Array.Empty<IParam>();

	/// <summary>Per-face plane normals, one per face, in face order.</summary>
	public List<Vec3> PlaneNormals = new();

	/// <summary>Per-face scalars, one Vec3 per face: x = plane distance, y = UV rotation in
	/// degrees, z = lightmap scale.</summary>
	public List<Vec3> FaceScalars = new();

	/// <summary>Per-face UV offsets, one per face, in face order.</summary>
	public List<Vec2> UvOffsets = new();

	/// <summary>Per-face UV scales, one per face, in face order.</summary>
	public List<Vec2> UvScales = new();

	/// <summary>Per-face smoothing groups, one per face, in face order.</summary>
	public List<int> SmoothingGroups = new();

	/// <summary>Per-face material names, one per face, in face order. Never blank: FaceMaterial
	/// forbids blank names, and SetBrush stores tools/toolsnodraw for an unset face.</summary>
	public List<string> MaterialIds = new();

	/// <summary>The brush these fields describe. Built from the shortest list when the lists
	/// disagree in length (a damaged load still yields a brush; the missing faces are gone,
	/// not invented). Setting it rewrites all six lists.</summary>
	public MapBrush Brush
	{
		get
		{
			var count = Math.Min( Math.Min( Math.Min( PlaneNormals.Count, FaceScalars.Count ),
					Math.Min( UvOffsets.Count, UvScales.Count ) ),
				Math.Min( SmoothingGroups.Count, MaterialIds.Count ) );
			var faces = new List<MapBrushFace>( count );
			for ( var i = 0; i < count; i++ )
			{
				var material = string.IsNullOrWhiteSpace( MaterialIds[i] )
					? FaceMaterial.Default
					: new FaceMaterial( MaterialIds[i], new FaceUv( UvOffsets[i], UvScales[i],
						FaceScalars[i].y, FaceScalars[i].z, SmoothingGroups[i] ) );
				faces.Add( new MapBrushFace( PlaneNormals[i], FaceScalars[i].x, material ) );
			}
			return MapBrush.FromPlanes( faces );
		}
		set => SetBrush( value );
	}

	/// <summary>Store a brush in the six persisted lists, replacing whatever was there.</summary>
	public void SetBrush( MapBrush brush )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		PlaneNormals.Clear();
		FaceScalars.Clear();
		UvOffsets.Clear();
		UvScales.Clear();
		SmoothingGroups.Clear();
		MaterialIds.Clear();
		foreach ( var face in brush.Faces )
		{
			PlaneNormals.Add( face.Normal );
			FaceScalars.Add( new Vec3( face.Distance, face.Uv.RotationDegrees, face.Uv.LightmapScale ) );
			UvOffsets.Add( face.Uv.Offset );
			UvScales.Add( face.Uv.Scale );
			SmoothingGroups.Add( face.Uv.SmoothingGroup );
			MaterialIds.Add( face.MaterialId );
		}
	}

	/// <summary>Material name to PolyMesh material slot, in first-appearance order over the
	/// persisted MaterialIds. Deterministic across save/load, so a reloaded brush bakes the
	/// same slots. A name that never appears gets the next slot after the listed ones.</summary>
	public int MaterialSlot( string materialId )
	{
		var id = string.IsNullOrWhiteSpace( materialId ) ? ToolTexture.NoDrawName : materialId;
		var seen = new List<string>();
		foreach ( var m in MaterialIds )
		{
			var name = string.IsNullOrWhiteSpace( m ) ? ToolTexture.NoDrawName : m;
			if ( !seen.Contains( name ) ) seen.Add( name );
		}
		var slot = seen.IndexOf( id );
		return slot < 0 ? seen.Count : slot;
	}

	protected override void Execute( FeatureContext ctx )
	{
		var brush = Brush;
		var defects = brush.Validate();
		if ( defects.Count > 0 )
			FailOn( "Brush",
				"Map brush is invalid",
				string.Join( "; ", defects.Select( d => d.ToString() ) ),
				"Fix the brush planes so they enclose a convex volume",
				"Delete the brush and rebuild it from planes" );

		var mesh = brush.ToMesh( MaterialSlot );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );
		Info( $"Map brush: {brush.FaceCount} faces, volume {brush.Volume:0.###}, {mesh.FaceCount} mesh faces" );
	}
}
