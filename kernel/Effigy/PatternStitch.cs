using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Marvelous-style pattern stitching: flat 2D pattern pieces sewn into a 3D garment.
///
/// A piece is a simple polygon in pattern space (u,v). It is placed in 3D by an axis
/// mapping (xz = a torso panel, yz = a side panel, xy = a flat panel) plus an offset
/// and an in-plane rotation, triangulated by ear clipping, and oriented so its faces
/// point AWAY from the garment centre — a front panel at +y faces +y, a back panel at
/// -y faces -y, without the user having to wind points in opposite directions per piece.
///
/// Sewing WELDS seam vertices: the two vertices become one. That is the honest model
/// of a stitched seam in a built garment mesh, and it has one consequence users must
/// understand: two flat sheets sewn edge-to-edge become ONE flat double layer, not a
/// tube. A wearable tube needs side panels — front, back, left side, right side, sewn
/// into a rectangular (or shaped) cross-section — exactly like a real pattern, where
/// a shirt is not two rectangles but front + back + sides. The demo in the tests
/// builds exactly that four-panel tube.
///
/// After welding, the garment relaxes (edge-length constraints, so panels keep their
/// cut size) and, when a collider body is given, is pushed out to a clearance off it —
/// the garment sits ON the wearer instead of through them. This is a pattern-to-garment
/// builder, not a drape simulator: GarmentHang.Settle is the drape pass, and it can run
/// on this output afterwards.
///
/// Deterministic: same pieces, same seams, same garment. No randomness.
/// </summary>
public static class PatternStitch
{
	public sealed class Piece
	{
		public string Name = "";
		public List<Vec2> Points = new();

		/// <summary>Which plane the pattern lies in: "xz" (torso front/back), "yz" (side), "xy" (flat).</summary>
		public string Axis = "xz";

		/// <summary>Full 3D offset applied after the axis mapping.</summary>
		public Vec3 Offset;

		/// <summary>In-plane rotation in degrees, about the piece centre, before mapping.</summary>
		public float RotationDeg;
	}

	public sealed class SeamPair
	{
		public string A = "";
		public int Ai;
		public string B = "";
		public int Bi;
	}

	public sealed class Options
	{
		public float Clearance = 0.05f;
		public int RelaxSteps = 10;
		public float Stiffness = 0.8f;

		/// <summary>Optional body the garment must sit outside of. Null = free-standing garment.</summary>
		public PolyMesh Collider;
	}

	public sealed class Result
	{
		public bool Ok;
		public string Reason = "";
		public PolyMesh Mesh;
		public int WeldedPairs;
		public int BoundaryEdges;
	}

	public static Result Build( IReadOnlyList<Piece> pieces, IReadOnlyList<SeamPair> seams, Options o )
	{
		o ??= new Options();

		if ( pieces is null || pieces.Count == 0 )
			return new Result { Ok = false, Reason = "No pattern pieces given." };

		var byName = new Dictionary<string, Piece>( StringComparer.OrdinalIgnoreCase );

		foreach ( var piece in pieces )
		{
			if ( string.IsNullOrWhiteSpace( piece.Name ) )
				return new Result { Ok = false, Reason = "A pattern piece has no name." };

			if ( byName.ContainsKey( piece.Name ) )
				return new Result { Ok = false, Reason = $"Two pattern pieces are called '{piece.Name}'." };

			if ( piece.Points is null || piece.Points.Count < 3 )
				return new Result { Ok = false, Reason = $"Piece '{piece.Name}' needs at least 3 points, got {piece.Points?.Count ?? 0}." };

			if ( MathF.Abs( SignedArea( piece.Points ) ) <= 1e-9f )
				return new Result { Ok = false, Reason = $"Piece '{piece.Name}' has zero area (its points are collinear or degenerate)." };

			byName[piece.Name] = piece;
		}

		// --- place + triangulate each piece -------------------------------------
		var mesh = new PolyMesh();
		var pieceVerts = new Dictionary<string, List<int>>( StringComparer.OrdinalIgnoreCase );
		var pieceFaces = new List<(string Piece, int[] Tri)>();

		foreach ( var piece in pieces )
		{
			var centre2 = Vec2.Zero;

			foreach ( var p in piece.Points )
				centre2 += p;

			centre2 /= piece.Points.Count;

			var placed = new List<Vec3>();
			var rot = o is null ? 0f : piece.RotationDeg * MathF.PI / 180f;
			var cos = MathF.Cos( rot );
			var sin = MathF.Sin( rot );

			foreach ( var p in piece.Points )
			{
				var lx = p.x - centre2.x;
				var ly = p.y - centre2.y;
				var rx = lx * cos - ly * sin;
				var ry = lx * sin + ly * cos;
				placed.Add( Map( piece.Axis, rx, ry ) + piece.Offset );
			}

			var verts = new List<int>();

			foreach ( var v in placed )
				verts.Add( mesh.AddVertex( v ) );

			pieceVerts[piece.Name] = verts;

			if ( !Triangulate( piece.Points, out var tris, out var triError ) )
				return new Result { Ok = false, Reason = $"Piece '{piece.Name}' could not be triangulated: {triError}" };

			foreach ( var tri in tris )
				pieceFaces.Add( (piece.Name, tri) );
		}

		// Orient each piece away from the garment centre, so a front and a back panel
		// do not need opposite windings from the user.
		var garmentCentre = Vec3.Zero;

		foreach ( var v in mesh.Positions )
			garmentCentre += v;

		garmentCentre /= Math.Max( 1, mesh.VertexCount );

		var faces = new List<(int[] Idx, Vec2[] Uv, string Piece)>();

		foreach ( var (pieceName, tri) in pieceFaces )
		{
			var piece = byName[pieceName];
			var verts = pieceVerts[pieceName];
			var idx = new[] { verts[tri[0]], verts[tri[1]], verts[tri[2]] };
			var uv = new[] { piece.Points[tri[0]], piece.Points[tri[1]], piece.Points[tri[2]] };

			var sheetCentre = Vec3.Zero;

			foreach ( var vi in verts )
				sheetCentre += mesh.Positions[vi];

			sheetCentre /= verts.Count;

			var outward = Outward( piece.Axis, sheetCentre - garmentCentre );
			var normal = Vec3.Cross(
				mesh.Positions[idx[1]] - mesh.Positions[idx[0]],
				mesh.Positions[idx[2]] - mesh.Positions[idx[0]] );

			if ( Vec3.Dot( normal, outward ) < 0f )
				(idx[1], idx[2]) = (idx[2], idx[1]);

			faces.Add( (idx, uv, pieceName) );
		}

		foreach ( var (idx, uv, _) in faces )
			mesh.AddFace( idx, uv );

		// --- weld seams ----------------------------------------------------------
		var parent = new int[mesh.VertexCount];
		var rootPos = new List<Vec3>( mesh.Positions );

		for ( var i = 0; i < parent.Length; i++ )
			parent[i] = i;

		int Find( int x )
		{
			while ( parent[x] != x )
			{
				parent[x] = parent[parent[x]];
				x = parent[x];
			}

			return x;
		}

		var welded = 0;

		foreach ( var seam in seams ?? Array.Empty<SeamPair>() )
		{
			if ( !byName.TryGetValue( seam.A, out var pa ) )
				return new Result { Ok = false, Reason = $"Seam names piece '{seam.A}', which does not exist." };

			if ( !byName.TryGetValue( seam.B, out var pb ) )
				return new Result { Ok = false, Reason = $"Seam names piece '{seam.B}', which does not exist." };

			if ( seam.Ai < 0 || seam.Ai >= pa.Points.Count || seam.Bi < 0 || seam.Bi >= pb.Points.Count )
				return new Result { Ok = false, Reason = $"Seam {seam.A}:{seam.Ai}-{seam.B}:{seam.Bi} points outside its piece (piece '{seam.A}' has {pa.Points.Count} points, '{seam.B}' has {pb.Points.Count})." };

			var ga = pieceVerts[seam.A][seam.Ai];
			var gb = pieceVerts[seam.B][seam.Bi];
			var ra = Find( ga );
			var rb = Find( gb );

			if ( ra == rb )
				continue;

			// The seam vertex lives at the average of the two pattern edges, so neither
			// panel drags the seam onto itself.
			rootPos[ra] = (rootPos[ra] + rootPos[rb]) / 2f;
			parent[rb] = ra;
			welded++;
		}

		var remap = new Dictionary<int, int>();
		var finalMesh = new PolyMesh();

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			var r = Find( i );

			if ( !remap.TryGetValue( r, out var ni ) )
			{
				ni = finalMesh.AddVertex( rootPos[r] );
				remap[r] = ni;
			}
		}

		foreach ( var f in mesh.Faces )
		{
			var idx = f.Indices.Select( vi => remap[Find( vi )] ).ToArray();

			if ( idx.Distinct().Count() != idx.Length )
				continue; // welding collapsed this triangle — it was a seam sliver, not a panel.

			finalMesh.AddFace( idx, f.UVs );
		}

		if ( finalMesh.FaceCount == 0 )
			return new Result { Ok = false, Reason = "Welding the seams collapsed every face. Seams must join edges, not weld a whole piece onto itself." };

		// --- relax + collider push-out -------------------------------------------
		var relax = Math.Clamp( o.RelaxSteps, 0, 200 );
		var stiffness = Math.Clamp( o.Stiffness, 0f, 1f );

		if ( relax > 0 && stiffness > 0f )
			Relax( finalMesh, relax, stiffness );

		if ( o.Collider is not null && o.Collider.FaceCount > 0 )
		{
			var bvh = MeshBVH.Build( o.Collider );
			var clearance = Math.Max( 0f, o.Clearance );

			for ( var i = 0; i < finalMesh.VertexCount; i++ )
			{
				var hit = bvh.NearestSurface( o.Collider, finalMesh.Positions[i], clearance * 4f + 1f );

				if ( hit is not { } h || h.Distance >= clearance )
					continue;

				// Push AWAY FROM THE HIT POINT, not along the face normal. The BVH
				// reports the hit triangle's normal, and for a corner/edge hit that
				// normal points along the wrong axis entirely: a vertex hovering over
				// a box corner was pushed sideways (y 6 -> 5, z 12 -> 13.2) instead of
				// outward. The vertex-to-hit direction is outward by construction;
				// it equals the face normal for a clean face-centre hit, and is the
				// only correct direction for an edge/corner hit. Fall back to the
				// face normal only when the vertex sits exactly on the hit point.
				var away = finalMesh.Positions[i] - h.Point;
				var dir = away.Length > 1e-9f ? away.Normal : h.Normal;
				finalMesh.Positions[i] = h.Point + dir * clearance;
			}
		}

		if ( !finalMesh.Positions.All( p => float.IsFinite( p.x ) && float.IsFinite( p.y ) && float.IsFinite( p.z ) ) )
			return new Result { Ok = false, Reason = "The stitched garment has non-finite positions." };

		return new Result
		{
			Ok = true, Mesh = finalMesh, WeldedPairs = welded,
			BoundaryEdges = PressureBalloon.BoundaryEdgeCount( finalMesh ),
		};
	}

	static Vec3 Map( string axis, float u, float v ) => axis switch
	{
		"yz" => new Vec3( 0f, u, v ),
		"xy" => new Vec3( u, v, 0f ),
		_ => new Vec3( u, 0f, v ),
	};

	static Vec3 Outward( string axis, Vec3 fromCentre ) => axis switch
	{
		"yz" => new Vec3( MathF.Sign( fromCentre.x ) == 0 ? 1f : MathF.Sign( fromCentre.x ), 0f, 0f ),
		"xy" => new Vec3( 0f, 0f, MathF.Sign( fromCentre.z ) == 0 ? 1f : MathF.Sign( fromCentre.z ) ),
		_ => new Vec3( 0f, MathF.Sign( fromCentre.y ) == 0 ? 1f : MathF.Sign( fromCentre.y ), 0f ),
	};

	static float SignedArea( IReadOnlyList<Vec2> points )
	{
		var total = 0f;

		for ( var i = 0; i < points.Count; i++ )
		{
			var a = points[i];
			var b = points[(i + 1) % points.Count];
			total += a.x * b.y - b.x * a.y;
		}

		return total / 2f;
	}

	/// <summary>Ear clipping over the polygon's original indices (so seam indices stay meaningful).</summary>
	static bool Triangulate( IReadOnlyList<Vec2> points, out List<int[]> tris, out string error )
	{
		tris = new List<int[]>();
		error = "";

		var order = Enumerable.Range( 0, points.Count ).ToList();

		if ( SignedArea( points ) < 0f )
			order.Reverse();

		bool Convex( int prev, int cur, int next )
		{
			var a = points[prev];
			var b = points[cur];
			var c = points[next];
			return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) > 1e-9f;
		}

		bool InTriangle( Vec2 p, Vec2 a, Vec2 b, Vec2 c )
		{
			var d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
			var d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
			var d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
			var hasNeg = d1 < -1e-9f || d2 < -1e-9f || d3 < -1e-9f;
			var hasPos = d1 > 1e-9f || d2 > 1e-9f || d3 > 1e-9f;
			return !(hasNeg && hasPos);
		}

		var guard = 0;

		while ( order.Count > 3 )
		{
			if ( guard++ > points.Count * points.Count + 10 )
			{
				error = "no ear found — the polygon is self-intersecting or degenerate";
				return false;
			}

			var clipped = false;

			for ( var i = 0; i < order.Count; i++ )
			{
				var prev = order[(i - 1 + order.Count) % order.Count];
				var cur = order[i];
				var next = order[(i + 1) % order.Count];

				if ( !Convex( prev, cur, next ) )
					continue;

				var blocked = false;

				foreach ( var other in order )
				{
					if ( other == prev || other == cur || other == next )
						continue;

					if ( InTriangle( points[other], points[prev], points[cur], points[next] ) )
					{
						blocked = true;
						break;
					}
				}

				if ( blocked )
					continue;

				tris.Add( new[] { prev, cur, next } );
				order.RemoveAt( i );
				clipped = true;
				break;
			}

			if ( !clipped )
			{
				error = "no ear found — the polygon is self-intersecting or degenerate";
				return false;
			}
		}

		tris.Add( order.ToArray() );
		return true;
	}

	static void Relax( PolyMesh mesh, int steps, float stiffness )
	{
		var edges = new List<(int A, int B)>();
		var seen = new HashSet<EdgeKey>();

		foreach ( var f in mesh.Faces )
		{
			for ( var i = 0; i < f.Indices.Length; i++ )
			{
				var key = new EdgeKey( f.Indices[i], f.Indices[(i + 1) % f.Indices.Length] );

				if ( seen.Add( key ) )
					edges.Add( (f.Indices[i], f.Indices[(i + 1) % f.Indices.Length]) );
			}
		}

		var rest = edges.Select( e => (mesh.Positions[e.A] - mesh.Positions[e.B]).Length ).ToArray();

		for ( var step = 0; step < steps; step++ )
		{
			for ( var i = 0; i < edges.Count; i++ )
			{
				var (a, b) = edges[i];
				var delta = mesh.Positions[b] - mesh.Positions[a];
				var len = delta.Length;

				if ( len < 1e-9f )
					continue;

				var correction = (len - rest[i]) * stiffness / 2f;
				var dir = delta / len;
				mesh.Positions[a] += dir * correction;
				mesh.Positions[b] -= dir * correction;
			}
		}
	}
}
