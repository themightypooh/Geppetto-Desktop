using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// A boolean the kernel can do on its own, with sharp edges intact.
///
/// WHY BSP, NOT VOXELS AND NOT RAY VOTES. The kernel already has a voxel boolean, and it is
/// honest about what it is: a fresh skin whose edges come back rounded by about a voxel. That
/// is the wrong answer for a bolt through a panel or a window cut in a hull, where the cut edge
/// IS the part. The roadmap asks for BVH triangle intersection plus winding-number votes; this
/// implementation takes the other exact route that is actually shippable in one lane — a BSP
/// tree in the classic csg.js formulation, where every fragment is classified by the planes of
/// the other solid, not by a ray that can be flipped by a single degenerate hit. No ray vote
/// means no single-ray row-flip bug to work around: a fragment is inside or outside because
/// every plane that bounds it says so, and a fragment that straddles a plane is split on that
/// plane until it no longer straddles anything. Coplanar faces are handled by the BSP's
/// coplanar rule (same-direction coplanar fragments survive a front clip, opposite-direction
/// ones do not), which is what keeps two flush faces from doubling up or leaving a sliver.
///
/// THE SEAM PROBLEM, AND THE STITCH. BSP splitting alone does not give a watertight mesh: a
/// vertex created where mesh A's edge crosses mesh B's plane generally does not coincide with
/// the vertex where B's edge crosses A's plane, even though both sit on the same intersection
/// line — so one side's seam edge passes through the other side's seam vertex (a T-junction),
/// and edge-count validation calls that an open mesh. The fix here is a post-pass that inserts
/// every such vertex into every edge it lies on, then welds, drops duplicates and degenerates,
/// and finally runs <see cref="CoplanarMerge"/> to weld the split fragments of one original
/// face back into clean n-gons/quads. A result that still has boundary edges after that pass
/// is a bug in this file, not a property of the inputs, and the tests assert closedness on
/// every boolean they run.
///
/// WHAT IT DOES NOT DO. UVs survive only where a face passes through unsplit (split faces
/// interpolate the UVs they had, and faces that had none stay without); vertex colours, skin
/// weights and morphs do not come across — the result is a fresh mesh, like the voxel one's,
/// but with the original faces' geometry. Inputs must be closed, and the total input triangle
/// count is capped (see <see cref="MaxTriangles"/>): beyond that, use Method=Voxel, which is
/// built for huge organic meshes. This provider is for hard-surface and kitbash scale work,
/// where exactness matters more than triangle count.
/// </summary>
public sealed class ExactMeshBoolean : IMeshBoolean
{
	public static readonly ExactMeshBoolean Instance = new();

	/// <summary>Above this many input triangles (both solids, triangulated) the exact boolean
	/// refuses rather than hanging the rebuild: a 175k-triangle sculpt is voxel territory.</summary>
	public const int MaxTriangles = 100_000;

	/// <summary>Plane-classification epsilon, in model units (csg.js's 1e-5).</summary>
	const float Eps = 1e-5f;

	/// <summary>Vertex-weld and T-junction tolerance, in model units.</summary>
	const float WeldEps = 1e-4f;

	public bool TryApply( BooleanOp op, PolyMesh target, PolyMesh tool, out PolyMesh result, out string error )
	{
		result = null;
		error = null;

		if ( target is null || tool is null || target.FaceCount == 0 || tool.FaceCount == 0 )
		{
			error = "A boolean needs two solids with faces.";
			return false;
		}

		var targetCheck = MeshValidator.Validate( target );
		var toolCheck = MeshValidator.Validate( tool );

		if ( !targetCheck.IsClosed )
		{
			error = $"The target is not closed ({targetCheck.BoundaryEdges} boundary edge(s)), so it has no inside to combine.";
			return false;
		}

		if ( !toolCheck.IsClosed )
		{
			error = $"The tool is not closed ({toolCheck.BoundaryEdges} boundary edge(s)), so it has no inside to combine.";
			return false;
		}

		var aPolys = MeshToPolygons( target );
		var bPolys = MeshToPolygons( tool );

		if ( aPolys.Count + bPolys.Count > MaxTriangles )
		{
			error = $"The exact boolean refuses meshes this large ({aPolys.Count + bPolys.Count} triangles; the cap is {MaxTriangles}). Use Method=Voxel for a mesh this size.";
			return false;
		}

		// Disjoint solids need no BSP at all, and the BSP would have nothing to split on.
		if ( !Overlaps( target, tool ) )
		{
			switch ( op )
			{
				case BooleanOp.Union:
					result = target.Clone();
					MeshTransform.Append( result, tool );
					return true;
				case BooleanOp.Subtract:
					result = target.Clone();
					return true;
				default:
					error = "The solids do not overlap, so there is no shared volume to keep.";
					return false;
			}
		}

		List<Polygon> combined;

		switch ( op )
		{
			case BooleanOp.Union:
			{
				var a = new Node( aPolys );
				var b = new Node( bPolys );
				a.ClipTo( b );
				b.ClipTo( a );
				b.Invert();
				b.ClipTo( a );
				b.Invert();
				a.Build( b.AllPolygons() );
				combined = a.AllPolygons();
				break;
			}
			case BooleanOp.Subtract:
			{
				var a = new Node( aPolys );
				var b = new Node( bPolys );
				a.Invert();
				a.ClipTo( b );
				b.ClipTo( a );
				b.Invert();
				b.ClipTo( a );
				b.Invert();
				a.Build( b.AllPolygons() );
				a.Invert();
				combined = a.AllPolygons();
				break;
			}
			default: // Intersect
			{
				var a = new Node( aPolys );
				var b = new Node( bPolys );
				a.Invert();
				b.ClipTo( a );
				b.Invert();
				a.ClipTo( b );
				b.ClipTo( a );
				a.Build( b.AllPolygons() );
				a.Invert();
				combined = a.AllPolygons();
				break;
			}
		}

		result = PolygonsToMesh( combined );

		if ( result.FaceCount == 0 )
		{
			error = "The boolean left nothing behind.";
			result = null;
			return false;
		}

		return true;
	}

	/// <summary>
	/// The intersection seam of a boolean result: result edges whose midpoint lies on BOTH input
	/// surfaces. BooleanFeature uses these, and only these, for the post-boolean bevel, so a
	/// bevel after a union fillets the weld line rather than every sharp edge of the part.
	/// An edge that merely touches one surface is an original edge, not a seam.
	/// </summary>
	public static List<EdgeKey> SeamEdges( PolyMesh result, PolyMesh target, PolyMesh tool, float tolerance = 1e-3f )
	{
		var seams = new List<EdgeKey>();

		if ( result is null || target is null || tool is null )
			return seams;

		var targetTree = MeshBVH.Build( target );
		var toolTree = MeshBVH.Build( tool );
		var edgeFaces = result.BuildEdgeFaces();

		foreach ( var pair in edgeFaces )
		{
			if ( pair.Value.Count != 2 )
				continue;

			var mid = (result.Positions[pair.Key.A] + result.Positions[pair.Key.B]) * 0.5f;

			if ( targetTree.NearestSurface( target, mid, tolerance ) is not null
				&& toolTree.NearestSurface( tool, mid, tolerance ) is not null )
				seams.Add( pair.Key );
		}

		return seams;
	}

	static bool Overlaps( PolyMesh a, PolyMesh b )
	{
		Extent( a, out var aMin, out var aMax );
		Extent( b, out var bMin, out var bMax );

		return aMin.x <= bMax.x && bMin.x <= aMax.x
			&& aMin.y <= bMax.y && bMin.y <= aMax.y
			&& aMin.z <= bMax.z && bMin.z <= aMax.z;
	}

	static void Extent( PolyMesh mesh, out Vec3 min, out Vec3 max )
	{
		min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}
	}

	// --- polygons -------------------------------------------------------------------------

	sealed class Polygon
	{
		public List<Vec3> Verts;
		public List<Vec2> Uvs; // null when the source face carried no usable UVs
		public int Material;
		public Vec3 Normal;
		public float W;

		public Polygon( List<Vec3> verts, List<Vec2> uvs, int material )
		{
			Verts = verts;
			Uvs = uvs;
			Material = material;
			ComputePlane();
		}

		public void ComputePlane()
		{
			// First non-degenerate triple wins; a fully degenerate polygon has no plane and
			// is dropped by the caller, never classified.
			for ( var i = 2; i < Verts.Count; i++ )
			{
				var n = Vec3.Cross( Verts[1] - Verts[0], Verts[i] - Verts[0] );

				if ( n.LengthSquared > 1e-12f )
				{
					Normal = n.Normal;
					W = Vec3.Dot( Normal, Verts[0] );
					return;
				}
			}

			Normal = Vec3.Zero;
			W = 0f;
		}

		public void Flip()
		{
			Verts.Reverse();
			Uvs?.Reverse();
			Normal = -Normal;
			W = -W;
		}
	}

	sealed class Plane
	{
		public Vec3 N;
		public float W;

		public Plane( Vec3 n, float w ) { N = n; W = w; }

		public Plane Flip() => new( -N, -W );

		// Split one polygon by this plane, following csg.js: a polygon that straddles is cut
		// into a front piece and a back piece sharing the interpolated seam vertices; a
		// coplanar polygon goes to the front list when it faces the same way as the plane.
		public void SplitPolygon( Polygon polygon, List<Polygon> coplanarFront, List<Polygon> coplanarBack,
			List<Polygon> front, List<Polygon> back )
		{
			const int Coplanar = 0, Front = 1, Back = 2;
			var count = polygon.Verts.Count;
			var types = new int[count];
			var polyType = 0;

			for ( var i = 0; i < count; i++ )
			{
				var t = Vec3.Dot( N, polygon.Verts[i] ) - W;
				types[i] = t < -Eps ? Back : t > Eps ? Front : Coplanar;
				polyType |= types[i];
			}

			switch ( polyType )
			{
				case Coplanar:
					(Vec3.Dot( N, polygon.Normal ) > 0 ? coplanarFront : coplanarBack).Add( polygon );
					return;
				case Front:
					front.Add( polygon );
					return;
				case Back:
					back.Add( polygon );
					return;
			}

			// Spanning: walk the edges, interpolating each crossing once, into both pieces.
			var fVerts = new List<Vec3>();
			var fUvs = polygon.Uvs is null ? null : new List<Vec2>();
			var bVerts = new List<Vec3>();
			var bUvs = polygon.Uvs is null ? null : new List<Vec2>();

			for ( var i = 0; i < count; i++ )
			{
				var j = (i + 1) % count;
				var ti = types[i];
				var tj = types[j];
				var vi = polygon.Verts[i];
				var vj = polygon.Verts[j];

				// A vertex exactly ON the plane belongs to both pieces: it sits on the
				// seam, and dropping it (the naive csg.js behaviour) deletes the whole
				// triangle whenever a third vertex straddles the plane — common when a
				// cylinder vertex or a box corner lands exactly on a cutting plane.
				if ( ti == Coplanar )
				{
					fVerts.Add( vi );
					bVerts.Add( vi );

					if ( polygon.Uvs is not null )
					{
						fUvs.Add( polygon.Uvs[i] );
						bUvs.Add( polygon.Uvs[i] );
					}
				}
				else
				{
					(ti == Front ? fVerts : bVerts).Add( vi );

					if ( polygon.Uvs is not null )
						(ti == Front ? fUvs : bUvs).Add( polygon.Uvs[i] );
				}

				if ( (ti | tj) == (Front | Back) )
				{
					var denom = Vec3.Dot( N, vj - vi );
					var t = MathF.Abs( denom ) < 1e-12f ? 0f : (W - Vec3.Dot( N, vi )) / denom;
					var v = vi + (vj - vi) * t;
					fVerts.Add( v );
					bVerts.Add( v );

					if ( polygon.Uvs is not null )
					{
						var uv = polygon.Uvs[i] + (polygon.Uvs[j] - polygon.Uvs[i]) * t;
						fUvs.Add( uv );
						bUvs.Add( uv );
					}
				}
			}

			if ( fVerts.Count >= 3 )
				front.Add( new Polygon( fVerts, fUvs, polygon.Material ) );

			if ( bVerts.Count >= 3 )
				back.Add( new Polygon( bVerts, bUvs, polygon.Material ) );
		}
	}

	sealed class Node
	{
		public Plane Plane;
		public Node Front, Back;
		public List<Polygon> Polys = new();

		public Node() { }

		public Node( List<Polygon> polygons ) => Build( polygons );

		public void Build( List<Polygon> polygons )
		{
			if ( polygons.Count == 0 )
				return;

			if ( Plane is null )
			{
				var first = polygons[0];
				Plane = new Plane( first.Normal, first.W );
			}

			var front = new List<Polygon>();
			var back = new List<Polygon>();

			foreach ( var p in polygons )
				Plane.SplitPolygon( p, Polys, Polys, front, back );

			if ( front.Count > 0 )
			{
				Front ??= new Node();
				Front.Build( front );
			}

			if ( back.Count > 0 )
			{
				Back ??= new Node();
				Back.Build( back );
			}
		}

		public void Invert()
		{
			foreach ( var p in Polys )
				p.Flip();

			Plane = Plane?.Flip();
			(Front, Back) = (Back, Front);
			Front?.Invert();
			Back?.Invert();
		}

		public List<Polygon> ClipPolygons( List<Polygon> polygons )
		{
			if ( Plane is null )
				return new List<Polygon>( polygons );

			var front = new List<Polygon>();
			var back = new List<Polygon>();

			foreach ( var p in polygons )
				Plane.SplitPolygon( p, front, back, front, back );

			if ( Front is not null )
				front = Front.ClipPolygons( front );

			// No back child means that region is inside this solid: fragments that end up
			// there are dropped, which is exactly the "keep only the outside" clip.
			back = Back is not null ? Back.ClipPolygons( back ) : new List<Polygon>();

			front.AddRange( back );
			return front;
		}

		public void ClipTo( Node other )
		{
			Polys = other.ClipPolygons( Polys );
			Front?.ClipTo( other );
			Back?.ClipTo( other );
		}

		public List<Polygon> AllPolygons()
		{
			var all = new List<Polygon>( Polys );

			if ( Front is not null )
				all.AddRange( Front.AllPolygons() );

			if ( Back is not null )
				all.AddRange( Back.AllPolygons() );

			return all;
		}
	}

	// --- mesh conversion --------------------------------------------------------------------

	static List<Polygon> MeshToPolygons( PolyMesh mesh )
	{
		var polys = new List<Polygon>();

		foreach ( var face in mesh.Faces )
		{
			if ( face.Count < 3 )
				continue;

			var hasUvs = face.UVs is not null && face.UVs.Length == face.Count;

			// Fan-triangulate. Inputs here are kernel meshes (quad-dominant, convex faces);
			// a fan of a convex n-gon is exact, and a warped quad's diagonal is the same
			// choice every other triangulator in this tree makes.
			for ( var k = 1; k + 1 < face.Count; k++ )
			{
				var verts = new List<Vec3>
				{
					mesh.Positions[face.Indices[0]],
					mesh.Positions[face.Indices[k]],
					mesh.Positions[face.Indices[k + 1]],
				};

				if ( Vec3.Cross( verts[1] - verts[0], verts[2] - verts[0] ).LengthSquared < 1e-12f )
					continue;

				List<Vec2> uvs = null;

				if ( hasUvs )
					uvs = new List<Vec2> { face.UVs[0], face.UVs[k], face.UVs[k + 1] };

				polys.Add( new Polygon( verts, uvs, face.Material ) );
			}
		}

		return polys;
	}

	static PolyMesh PolygonsToMesh( List<Polygon> polygons )
	{
		var mesh = new PolyMesh();
		var weld = new Dictionary<(long, long, long), int>();

		int Weld( Vec3 p )
		{
			var key = ((long)MathF.Round( p.x / WeldEps ), (long)MathF.Round( p.y / WeldEps ), (long)MathF.Round( p.z / WeldEps ));

			if ( weld.TryGetValue( key, out var idx ) )
				return idx;

			idx = mesh.Positions.Count;
			mesh.Positions.Add( p );
			weld[key] = idx;
			return idx;
		}

		foreach ( var poly in polygons )
		{
			if ( poly.Normal.LengthSquared < 0.5f )
				continue;

			var indices = new List<int>();
			var uvs = poly.Uvs is null ? null : new List<Vec2>();

			for ( var i = 0; i < poly.Verts.Count; i++ )
			{
				var idx = Weld( poly.Verts[i] );

				if ( indices.Count > 0 && indices[^1] == idx )
					continue;

				indices.Add( idx );

				if ( uvs is not null )
					uvs.Add( poly.Uvs[i] );
			}

			if ( indices.Count >= 2 && indices[0] == indices[^1] )
			{
				indices.RemoveAt( indices.Count - 1 );
				uvs?.RemoveAt( uvs.Count - 1 );
			}

			if ( indices.Count < 3 )
				continue;

			mesh.Faces.Add( new Face( indices.ToArray(), uvs?.ToArray(), poly.Material ) );
		}

		DropDuplicatesAndDegenerates( mesh );
		StitchTJunctions( mesh );
		DropDuplicatesAndDegenerates( mesh );
		CoplanarMerge.Merge( mesh );
		DropDuplicatesAndDegenerates( mesh );

		return mesh;
	}

	// Two faces with the same vertex set are either the same surface twice (keep one) or the
	// two sides of a zero-thickness sliver / internal interface (drop both). Faces that lost
	// a corner to welding, or that are flat, are not faces at all.
	static void DropDuplicatesAndDegenerates( PolyMesh mesh )
	{
		var kept = new List<Face>();

		foreach ( var face in mesh.Faces )
		{
			var distinct = face.Indices.Distinct().ToArray();

			if ( distinct.Length < 3 )
				continue;

			// A merged face can START along a straight edge — CoplanarMerge leaves the
			// seam vertices in the loop, so the first three corners are often collinear
			// on a perfectly good hexagon. Only a face with no area at all is degenerate,
			// so test the whole loop (Newell area), not its first triple.
			var area = Vec3.Zero;

			for ( var k = 0; k < distinct.Length; k++ )
			{
				var pk = mesh.Positions[distinct[k]];
				var qk = mesh.Positions[distinct[(k + 1) % distinct.Length]];
				area += Vec3.Cross( pk, qk );
			}

			if ( area.LengthSquared < 1e-12f )
				continue;

			kept.Add( face );
		}

		mesh.Faces.Clear();

		var groups = new Dictionary<string, List<Face>>();

		foreach ( var face in kept )
		{
			var key = string.Join( ",", face.Indices.OrderBy( i => i ) );
			(groups.TryGetValue( key, out var list ) ? list : groups[key] = new List<Face>()).Add( face );
		}

		foreach ( var group in groups.Values )
		{
			// Within one vertex set, cancel opposite-winding pairs first, then keep one
			// copy of whatever survives.
			var survivors = new List<Face>( group );

			for ( var i = 0; i < survivors.Count; i++ )
			{
				var found = -1;

				for ( var j = i + 1; j < survivors.Count; j++ )
				{
					if ( IsOppositeCycle( survivors[i].Indices, survivors[j].Indices ) )
					{
						found = j;
						break;
					}
				}

				if ( found >= 0 )
				{
					survivors.RemoveAt( found );
					survivors.RemoveAt( i );
					i--;
				}
			}

			if ( survivors.Count > 0 )
				mesh.Faces.Add( survivors[0] );
		}
	}

	static bool IsOppositeCycle( int[] a, int[] b )
	{
		if ( a.Length != b.Length )
			return false;

		var reversed = a.Reverse().ToArray();
		return SameCycle( reversed, b );
	}

	static bool SameCycle( int[] a, int[] b )
	{
		if ( a.Length != b.Length )
			return false;

		var min = a.Min();
		var ai = Array.IndexOf( a, min );
		var bi = Array.IndexOf( b, min );

		for ( var k = 0; k < a.Length; k++ )
		{
			if ( a[(ai + k) % a.Length] != b[(bi + k) % b.Length] )
				return false;
		}

		return true;
	}

	// A vertex that lies in the interior of another face's edge splits that edge, or the two
	// sides of the seam disagree about where the seam runs and the mesh reads as open.
	// Restarted after every insertion, because inserting changes the edge list; a vertex is
	// never inserted at an endpoint, and insertion creates no new vertices, so this terminates.
	static void StitchTJunctions( PolyMesh mesh )
	{
		// The stitch is quadratic in the worst case; for the kitbash-scale meshes this
		// provider is for, that is seconds at most. Above this size the seam is left as the
		// BSP produced it rather than hanging the rebuild on a stitch pass.
		if ( (long)mesh.VertexCount * mesh.FaceCount > 20_000_000L )
			return;

		var changed = true;

		while ( changed )
		{
			changed = false;

			for ( var vi = 0; vi < mesh.VertexCount && !changed; vi++ )
			{
				var p = mesh.Positions[vi];

				for ( var fi = 0; fi < mesh.FaceCount && !changed; fi++ )
				{
					var face = mesh.Faces[fi];

					if ( face.Indices.Contains( vi ) )
						continue;

					for ( var e = 0; e < face.Count; e++ )
					{
						var a = face.Indices[e];
						var b = face.Indices[(e + 1) % face.Count];
						var pa = mesh.Positions[a];
						var pb = mesh.Positions[b];
						var ab = pb - pa;
						var lenSq = ab.LengthSquared;

						if ( lenSq < 1e-12f )
							continue;

						var t = Vec3.Dot( p - pa, ab ) / lenSq;

						if ( t <= 0f || t >= 1f )
							continue;

						var closest = pa + ab * t;

						if ( (p - closest).Length > WeldEps )
							continue;

						var indices = face.Indices.ToList();
						indices.Insert( e + 1, vi );

						var uvs = face.UVs is null ? null : face.UVs.ToList();
						uvs?.Insert( e + 1, face.UVs[e] + (face.UVs[(e + 1) % face.Count] - face.UVs[e]) * t );

						mesh.Faces[fi] = new Face( indices.ToArray(), uvs?.ToArray(), face.Material );
						changed = true;
						break;
					}
				}
			}
		}
	}
}
