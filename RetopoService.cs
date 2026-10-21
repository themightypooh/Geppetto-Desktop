using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// CPU-only quad retopology foundation (R1). Converts a triangle mesh into a quad-dominant mesh
/// by pairing adjacent triangles that share an edge. Unpaired triangles remain triangles.
/// Does not mutate the input. See handoffs/10-retopology.md.
/// </summary>
public static class RetopoService
{
	public sealed class Options
	{
		/// <summary>Only pair triangles whose shared edge has equal UVs on both sides (seam-safe).</summary>
		public bool RespectUVSeams { get; set; } = true;

		/// <summary>Only pair triangles sharing the same material slot.</summary>
		public bool RespectMaterials { get; set; } = true;

		/// <summary>Skip pairing across sharp edges (dihedral angle above threshold).</summary>
		public bool RespectHardEdges { get; set; } = true;
		public float HardEdgeAngleDegrees { get; set; } = 30f;

		/// <summary>Drop faces that would make an edge non-manifold (>2 incident).</summary>
		public bool RepairManifold { get; set; } = true;

		/// <summary>Target triangle count for pre-decimation. 0 = no decimation.</summary>
		public int TargetTriangles { get; set; } = 0;

		/// <summary>Cost threshold for iterative optimization; collapse edges with cost below this. 0 disables.</summary>
		public float OptimizationThreshold { get; set; } = 0f;
	}

	public sealed class Result
	{
		public PolyMesh Mesh { get; }
		public int InputTriangles { get; }
		public int OutputQuads { get; }
		public int OutputTriangles { get; }
		public int PairedEdges { get; }
		public bool ReachedAllPaired { get; }
		/// <summary>Number of boundary (single-incident) edges in the output mesh.</summary>
		public int BoundaryEdges { get; }
		/// <summary>True if every edge has at most two incident faces (manifold).</summary>
		public bool IsManifold { get; }
		/// <summary>Ratio of output quads to total output faces.</summary>
		public double QuadRatio { get; }
		public Result( PolyMesh mesh, int inputTriangles, int outputQuads, int outputTriangles, int pairedEdges, int boundaryEdges, bool isManifold )
		{
			Mesh = mesh;
			InputTriangles = inputTriangles;
			OutputQuads = outputQuads;
			OutputTriangles = outputTriangles;
			PairedEdges = pairedEdges;
			ReachedAllPaired = pairedEdges * 2 + outputTriangles == inputTriangles;
			BoundaryEdges = boundaryEdges;
			IsManifold = isManifold;
			int total = outputQuads + outputTriangles;
			QuadRatio = total == 0 ? 0 : (double)outputQuads / total;
		}
	}

	private static Vec3 FaceNormal( System.Collections.Generic.List<Vec3> positions, Face f )
	{
		var a = positions[ f.Indices[ 0 ] ];
		var b = positions[ f.Indices[ 1 ] ];
		var c = positions[ f.Indices[ 2 ] ];
		return Vec3.Cross( b - a, c - a );
	}

	private static float Dot( Vec3 a, Vec3 b ) => Vec3.Dot( a, b );

	private static float EdgeCollapseCost( PolyMesh working, EdgeKey edge, List<int> faces )
	{
		var mid = ( working.Positions[ edge.A ] + working.Positions[ edge.B ] ) * 0.5f;
		float cost = 0;
		var seen = new HashSet<int>();
		foreach ( int fi in faces )
		{
			if ( !seen.Add( fi ) ) continue;
			var f = working.Faces[ fi ];
			var a = working.Positions[ f.Indices[ 0 ] ];
			var b = working.Positions[ f.Indices[ 1 ] ];
			var c = working.Positions[ f.Indices[ 2 ] ];
			var n = Vec3.Cross( b - a, c - a );
			float nl = n.Length;
			if ( nl < 1e-9f ) continue;
			n = n * ( 1f / nl );
			float d = Vec3.Dot( n, a );
			float dist = Vec3.Dot( n, mid ) - d;
			cost += dist * dist;
		}
		return cost;
	}

	private static float Area( Vec3 a, Vec3 b, Vec3 c )
	{
		var cross = Vec3.Cross( b - a, c - a );
		return 0.5f * cross.Length;
	}

	private static double QuadQuality( System.Collections.Generic.List<Vec3> positions, int f0, int f1, System.Collections.Generic.List<Face> faces )
	{
		var a = faces[ f0 ]; var b = faces[ f1 ];
		float area0 = Area( positions[ a.Indices[ 0 ] ], positions[ a.Indices[ 1 ] ], positions[ a.Indices[ 2 ] ] );
		float area1 = Area( positions[ b.Indices[ 0 ] ], positions[ b.Indices[ 1 ] ], positions[ b.Indices[ 2 ] ] );
		// Prefer similar-area pairs (balanced quads).
		float diff = Math.Abs( area0 - area1 );
		return 1.0 / ( 1.0 + diff );
	}

	public static List<Result> RetopoLodChain( PolyMesh source, IEnumerable<int> targets, Options baseOptions = null )
	{
		var results = new List<Result>();
		foreach ( var t in targets )
		{
			var opts = new Options
			{
				RespectUVSeams = baseOptions?.RespectUVSeams ?? true,
				RespectMaterials = baseOptions?.RespectMaterials ?? true,
				RespectHardEdges = baseOptions?.RespectHardEdges ?? true,
				HardEdgeAngleDegrees = baseOptions?.HardEdgeAngleDegrees ?? 30.0f,
				TargetTriangles = t,
				RepairManifold = baseOptions?.RepairManifold ?? true,
			};
			results.Add( Retopo( source, opts ) );
		}
		return results;
	}

	public static PolyMesh GenerateCollisionProxy( PolyMesh source )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		// Simple AABB proxy.
		if ( source.Positions.Count == 0 ) return new PolyMesh();
		float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
		float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
		foreach ( var p in source.Positions )
		{
			if ( p.x < minX ) minX = p.x; if ( p.y < minY ) minY = p.y; if ( p.z < minZ ) minZ = p.z;
			if ( p.x > maxX ) maxX = p.x; if ( p.y > maxY ) maxY = p.y; if ( p.z > maxZ ) maxZ = p.z;
		}
		var mesh = new PolyMesh();
		Vec3[] corners = new[]
		{
			new Vec3(minX,minY,minZ), new Vec3(maxX,minY,minZ), new Vec3(maxX,maxY,minZ), new Vec3(minX,maxY,minZ),
			new Vec3(minX,minY,maxZ), new Vec3(maxX,minY,maxZ), new Vec3(maxX,maxY,maxZ), new Vec3(minX,maxY,maxZ),
		};
		foreach ( var c in corners ) mesh.Positions.Add( c );
		int[] tris = new[]
		{
			0,1,2, 0,2,3,
			4,6,5, 4,7,6,
			0,4,5, 0,5,1,
			2,6,7, 2,7,3,
			0,3,7, 0,7,4,
			1,5,6, 1,6,2,
		};
		for ( int i = 0; i < tris.Length; i += 3 )
		{
			mesh.Faces.Add( new Face( new[] { tris[ i ], tris[ i + 1 ], tris[ i + 2 ] }, new[] { new Vec2(0,0), new Vec2(0,0), new Vec2(0,0) }, 0 ) );
		}
		return mesh;
	}

	public static void RepackUvs( PolyMesh mesh )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		// Simple normalization: fit all UVs into 0..1 based on current bounds.
		float minU = float.MaxValue, minV = float.MaxValue;
		float maxU = float.MinValue, maxV = float.MinValue;
		foreach ( var f in mesh.Faces )
			foreach ( var uv in f.UVs )
			{
				if ( uv.x < minU ) minU = uv.x; if ( uv.y < minV ) minV = uv.y;
				if ( uv.x > maxU ) maxU = uv.x; if ( uv.y > maxV ) maxV = uv.y;
			}
		float rangeU = Math.Max( 1e-6f, maxU - minU );
		float rangeV = Math.Max( 1e-6f, maxV - minV );
		foreach ( var f in mesh.Faces )
			for ( int i = 0; i < f.UVs.Length; i++ )
				f.UVs[ i ] = new Vec2( ( f.UVs[ i ].x - minU ) / rangeU, ( f.UVs[ i ].y - minV ) / rangeV );
	}

	public static List<string> Validate( PolyMesh mesh )
	{
		var issues = new List<string>();
		if ( mesh is null ) { issues.Add( "null mesh" ); return issues; }
		// Manifold check
		var counts = new Dictionary<EdgeKey, int>();
		foreach ( var f in mesh.Faces )
			for ( int e = 0; e < f.Count; e++ )
			{
				var key = new EdgeKey( f.Indices[ e ], f.Indices[ ( e + 1 ) % f.Count ] );
				counts[ key ] = counts.TryGetValue( key, out int c ) ? c + 1 : 1;
			}
		foreach ( var kv in counts )
			if ( kv.Value > 2 ) issues.Add( $"non-manifold edge {kv.Key}" );
		// Degenerate triangles
		foreach ( var f in mesh.Faces )
		{
			if ( f.Count == 3 )
			{
				var a = mesh.Positions[ f.Indices[ 0 ] ];
				var b = mesh.Positions[ f.Indices[ 1 ] ];
				var c = mesh.Positions[ f.Indices[ 2 ] ];
				if ( Area( a, b, c ) < 1e-9f ) issues.Add( "degenerate triangle" );
			}
		}
		if ( mesh.Positions.Count == 0 ) issues.Add( "empty mesh" );
		return issues;
	}

	public static (Result Result, PolyMesh CollisionProxy, List<string> Issues) RetopoPipeline( PolyMesh source, Options options = null, IEnumerable<int> lodTargets = null )
	{
		var result = Retopo( source, options );
		RepackUvs( result.Mesh );
		var proxy = GenerateCollisionProxy( result.Mesh );
		var issues = Validate( result.Mesh );
		if ( lodTargets != null )
		{
			// generate LODs (ignored return for now)
			var lods = RetopoLodChain( source, lodTargets, options );
		}
		return ( result, proxy, issues );
	}

	public static int DetectSymmetryAxis( PolyMesh source, float tolerance = 1e-3f )
	{
		if ( source is null || source.Positions.Count == 0 ) return -1;
		for ( int axis = 0; axis < 3; axis++ )
		{
			// compute centroid
			float c = 0;
			foreach ( var p in source.Positions )
				c += axis == 0 ? p.x : axis == 1 ? p.y : p.z;
			c /= source.Positions.Count;
			bool ok = true;
			foreach ( var p in source.Positions )
			{
				float v = axis == 0 ? p.x : axis == 1 ? p.y : p.z;
				float mirrored = 2 * c - v;
				// find a vertex with mirrored coordinate on this axis and same others
				bool found = false;
				foreach ( var q in source.Positions )
				{
					float qv = axis == 0 ? q.x : axis == 1 ? q.y : q.z;
					float qx = axis == 0 ? q.y : q.x;
					float qy = axis == 2 ? q.y : ( axis == 0 ? q.z : q.z );
					float px = axis == 0 ? p.y : p.x;
					float py = axis == 2 ? p.y : ( axis == 0 ? p.z : p.z );
					if ( Math.Abs( qv - mirrored ) < tolerance && Math.Abs( qx - px ) < tolerance && Math.Abs( qy - py ) < tolerance ) { found = true; break; }
				}
				if ( !found ) { ok = false; break; }
			}
			if ( ok ) return axis;
		}
		return -1;
	}

	public static void TransferSkinWeights( PolyMesh source, PolyMesh target )
	{
		if ( source is null || target is null ) throw new ArgumentNullException();
		if ( source.Skin is null ) return;
		if ( target.Skin is null ) target.Skin = new SkinWeights( target.Positions.Count );
		for ( int ti = 0; ti < target.Positions.Count; ti++ )
		{
			// IDW blend of 4 nearest source vertices' skin weights.
			var nearest = new List<(int idx, float dist)>();
			for ( int si = 0; si < source.Positions.Count; si++ )
			{
				float d = ( source.Positions[ si ] - target.Positions[ ti ] ).LengthSquared;
				nearest.Add( ( si, d ) );
			}
			nearest.Sort( ( a, b ) => a.dist.CompareTo( b.dist ) );
			var acc = new Dictionary<int, float>();
			float totalW = 0;
			foreach ( var (si, d) in nearest.Take( 4 ) )
			{
				float w = 1f / Math.Max( 1e-6f, d );
				totalW += w;
				foreach ( var bw in source.Skin[ si ] )
					acc[ bw.Bone ] = acc.TryGetValue( bw.Bone, out float v ) ? v + bw.Weight * w : bw.Weight * w;
			}
			var list = new List<BoneWeight>();
			foreach ( var kv in acc )
				list.Add( new BoneWeight( kv.Key, kv.Value / totalW ) );
			target.Skin[ ti ] = list.ToArray();
		}
	}

	public static void SplitLongEdges( PolyMesh mesh, float maxLength )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		bool changed = true;
		while ( changed )
		{
			changed = false;
			var newFaces = new List<Face>();
			foreach ( var f in mesh.Faces )
			{
				if ( f.Count != 3 ) { newFaces.Add( f ); continue; }
				int splitEdge = -1;
				for ( int e = 0; e < 3; e++ )
				{
					var a = mesh.Positions[ f.Indices[ e ] ];
					var b = mesh.Positions[ f.Indices[ ( e + 1 ) % 3 ] ];
					if ( ( b - a ).Length > maxLength ) { splitEdge = e; break; }
				}
				if ( splitEdge < 0 ) { newFaces.Add( f ); continue; }
				// split triangle by midpoint of long edge (add new vertex)
				int i0 = f.Indices[ splitEdge ];
				int i1 = f.Indices[ ( splitEdge + 1 ) % 3 ];
				int i2 = f.Indices[ ( splitEdge + 2 ) % 3 ];
				var mid = ( mesh.Positions[ i0 ] + mesh.Positions[ i1 ] ) * 0.5f;
				int newIdx = mesh.Positions.Count;
				mesh.Positions.Add( mid );
				if ( mesh.Skin is not null )
				{
					// copy averaged skin
					mesh.Skin.Vertices.Add( mesh.Skin[ i0 ] );
				}
				var uv0 = f.UVs[ splitEdge ];
				var uv1 = f.UVs[ ( splitEdge + 1 ) % 3 ];
				var midUv = new Vec2( ( uv0.x + uv1.x ) * 0.5f, ( uv0.y + uv1.y ) * 0.5f );
				newFaces.Add( new Face( new[] { i0, newIdx, i2 }, new[] { f.UVs[ splitEdge ], midUv, f.UVs[ ( splitEdge + 2 ) % 3 ] }, f.Material ) );
				newFaces.Add( new Face( new[] { newIdx, i1, i2 }, new[] { midUv, f.UVs[ ( splitEdge + 1 ) % 3 ], f.UVs[ ( splitEdge + 2 ) % 3 ] }, f.Material ) );
				changed = true;
			}
			mesh.Faces.Clear();
			foreach ( var f in newFaces ) mesh.Faces.Add( f );
		}
	}

	public static Result Retopo( PolyMesh source, Options options = null )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		options ??= new Options();

		// Decimation-to-target: if requested, drop lowest-area triangles first until target.
		PolyMesh decSource = source;
		if ( options.TargetTriangles > 0 && options.TargetTriangles < CountTris( source ) )
		{
			decSource = DropLowestAreaTris( source, options.TargetTriangles );
		}
		var working = new PolyMesh();
		working.Positions.AddRange( decSource.Positions );
		if ( source.Skin is not null )
		{
			var w = new SkinWeights( source.Skin.Count );
			for ( int i = 0; i < source.Skin.Count; i++ ) w[ i ] = source.Skin[ i ];
			working.Skin = w;
		}
		int inputTris = 0;
		foreach ( var f in decSource.Faces )
		{
			if ( f.Count == 3 )
			{
				working.Faces.Add( new Face( (int[])f.Indices.Clone(), (Vec2[])f.UVs.Clone(), f.Material ) );
				inputTris++;
			}
			else if ( f.Count == 4 )
			{
				// split quad into two tris for pairing input
				working.Faces.Add( new Face( new[] { f.Indices[ 0 ], f.Indices[ 1 ], f.Indices[ 2 ] }, new[] { f.UVs[ 0 ], f.UVs[ 1 ], f.UVs[ 2 ] }, f.Material ) );
				working.Faces.Add( new Face( new[] { f.Indices[ 0 ], f.Indices[ 2 ], f.Indices[ 3 ] }, new[] { f.UVs[ 0 ], f.UVs[ 2 ], f.UVs[ 3 ] }, f.Material ) );
				inputTris += 2;
			}
			else
			{
				for ( int i = 1; i < f.Count - 1; i++ )
				{
					working.Faces.Add( new Face( new[] { f.Indices[ 0 ], f.Indices[ i ], f.Indices[ i + 1 ] }, new[] { f.UVs[ 0 ], f.UVs[ i ], f.UVs[ i + 1 ] }, f.Material ) );
					inputTris++;
				}
			}
		}

		// Decimation-to-target: constraint-aware edge collapse. Collapse shortest eligible
		// interior edges (not hard/UV/material protected, not boundary) until target reached.
		if ( options.TargetTriangles > 0 )
		{
			while ( working.Faces.Count > options.TargetTriangles )
			{
				// Build edge incidence.
				var edgeIncidence = new Dictionary<EdgeKey, List<int>>();
				for ( int fi = 0; fi < working.Faces.Count; fi++ )
				{
					var f = working.Faces[ fi ];
					for ( int e = 0; e < 3; e++ )
					{
						var key = new EdgeKey( f.Indices[ e ], f.Indices[ ( e + 1 ) % 3 ] );
						if ( !edgeIncidence.TryGetValue( key, out var list ) ) { list = new List<int>(); edgeIncidence[ key ] = list; }
						list.Add( fi );
					}
				}
				// Candidate edges: exactly two incident, not protected.
				var cand = new List<(EdgeKey key, float len)>();
				foreach ( var kv in edgeIncidence )
				{
					if ( kv.Value.Count != 2 ) continue;
					int a = kv.Key.A, b = kv.Key.B;
					// protected: UV seam / material
					var f0 = working.Faces[ kv.Value[ 0 ] ];
					var f1 = working.Faces[ kv.Value[ 1 ] ];
					if ( options.RespectMaterials && f0.Material != f1.Material ) continue;
					if ( options.RespectUVSeams )
					{
						// find UVs
						Vec2 uva = default, uvb = default, uva2 = default, uvb2 = default;
						for ( int c = 0; c < 3; c++ ) { if ( f0.Indices[ c ] == a ) uva = f0.UVs[ c ]; if ( f0.Indices[ c ] == b ) uvb = f0.UVs[ c ]; }
						for ( int c = 0; c < 3; c++ ) { if ( f1.Indices[ c ] == a ) uva2 = f1.UVs[ c ]; if ( f1.Indices[ c ] == b ) uvb2 = f1.UVs[ c ]; }
						if ( uva.x != uva2.x || uva.y != uva2.y || uvb.x != uvb2.x || uvb.y != uvb2.y ) continue;
					}
					if ( options.RespectHardEdges )
					{
						var n0 = FaceNormal( working.Positions, f0 );
						var n1 = FaceNormal( working.Positions, f1 );
						double cos = Vec3.Dot( n0, n1 ) / Math.Max( 1e-12, n0.Length * n1.Length );
						cos = Math.Clamp( cos, -1.0, 1.0 );
						double ang = Math.Acos( cos ) * 180.0 / Math.PI;
						if ( ang > options.HardEdgeAngleDegrees ) continue;
					}
					// QEM-ish cost: sum of squared distances of endpoints to incident face planes.
					float cost = EdgeCollapseCost( working, kv.Key, edgeIncidence[ kv.Key ] );
					cand.Add( ( kv.Key, cost ) );
				}
				if ( cand.Count == 0 ) break;
				cand.Sort( ( x, y ) => x.len.CompareTo( y.len ) );
				var edge = cand[ 0 ].key;
				int va = edge.A, vb = edge.B;
				// Collapse vb -> va: midpoint position, average UV/skin.
				working.Positions[ va ] = ( working.Positions[ va ] + working.Positions[ vb ] ) * 0.5f;
				// keep va's skin weights (simple)
				// Replace vb with va in all faces; remove degenerate.
				var newFaces = new List<Face>();
				foreach ( var f in working.Faces )
				{
					var idx = new int[ f.Count ];
					var uv = new Vec2[ f.Count ];
					for ( int c = 0; c < f.Count; c++ )
					{
						if ( f.Indices[ c ] == vb ) { idx[ c ] = va; uv[ c ] = f.UVs[ c ]; }
						else { idx[ c ] = f.Indices[ c ]; uv[ c ] = f.UVs[ c ]; }
					}
					// degenerate check
					bool deg = false;
					for ( int i = 0; i < idx.Length; i++ )
						for ( int j = i + 1; j < idx.Length; j++ )
							if ( idx[ i ] == idx[ j ] ) { deg = true; break; }
					if ( deg ) continue;
					newFaces.Add( new Face( idx, uv, f.Material ) );
				}
				working.Faces.Clear();
				foreach ( var f in newFaces ) working.Faces.Add( f );
			}
		}

		// Build edge -> list of (faceIndex, cornerIndex of edge start).
		var edgeMap = new Dictionary<EdgeKey, List<(int face, int edge)>> ( );
		for ( int fi = 0; fi < working.Faces.Count; fi++ )
		{
			var f = working.Faces[ fi ];
			for ( int e = 0; e < f.Count; e++ )
			{
				int a = f.Indices[ e ];
				int b = f.Indices[ ( e + 1 ) % f.Count ];
				var key = new EdgeKey( a, b );
				if ( !edgeMap.TryGetValue( key, out var list ) ) { list = new List<(int,int)> ( ); edgeMap[ key ] = list; }
				list.Add( ( fi, e ) );
			}
		}

		// Candidate pairs: edges with exactly two incident triangles.
		var candidates = new List<(EdgeKey key, int f0, int e0, int f1, int e1, float len)>();
		foreach ( var kv in edgeMap )
		{
			if ( kv.Value.Count != 2 ) continue;
			var (f0, e0) = kv.Value[ 0 ];
			var (f1, e1) = kv.Value[ 1 ];
			if ( options.RespectMaterials && working.Faces[ f0 ].Material != working.Faces[ f1 ].Material ) continue;
			if ( options.RespectHardEdges )
			{
				var n0 = FaceNormal( working, working.Faces[ f0 ] );
				var n1 = FaceNormal( working, working.Faces[ f1 ] );
				float cos = Vec3.Dot( n0, n1 );
				float angle = (float)( Math.Acos( Math.Clamp( (double)cos, -1.0, 1.0 ) ) * 180.0 / Math.PI );
				if ( angle > options.HardEdgeAngleDegrees ) continue;
			}
			if ( options.RespectUVSeams )
			{
				var fa = working.Faces[ f0 ]; var fb = working.Faces[ f1 ];
				// UVs along the shared edge must match (seam-safe).
				int a0 = fa.Indices[ e0 ], b0 = fa.Indices[ ( e0 + 1 ) % fa.Count ];
				int a1 = fb.Indices[ e1 ], b1 = fb.Indices[ ( e1 + 1 ) % fb.Count ];
				// find matching corner UVs
				var uvA0 = fa.UVs[ e0 ]; var uvB0 = fa.UVs[ ( e0 + 1 ) % fa.Count ];
				// other face traverses opposite; match endpoints
				Vec2 uvA1 = default, uvB1 = default;
				for ( int c = 0; c < fb.Count; c++ )
				{
					if ( fb.Indices[ c ] == a0 ) uvA1 = fb.UVs[ c ];
					if ( fb.Indices[ c ] == b0 ) uvB1 = fb.UVs[ c ];
				}
				if ( uvA0.x != uvA1.x || uvA0.y != uvA1.y || uvB0.x != uvB1.x || uvB0.y != uvB1.y ) continue;
			}
			if ( options.RespectHardEdges )
			{
				// Dihedral angle between the two faces sharing this edge: sharp edges are non-pairable.
				var n0 = FaceNormal( working.Positions, working.Faces[ f0 ] );
				var n1 = FaceNormal( working.Positions, working.Faces[ f1 ] );
				double cosAngle = Dot( n0, n1 ) / ( Math.Max( 1e-12, n0.Length * n1.Length ) );
				cosAngle = Math.Clamp( cosAngle, -1.0, 1.0 );
				double angleDeg = Math.Acos( cosAngle ) * 180.0 / Math.PI;
				if ( angleDeg > options.HardEdgeAngleDegrees ) continue;
			}
			var p0 = working.Positions[ kv.Key.A ];
			var p1 = working.Positions[ kv.Key.B ];
			float len = ( p1 - p0 ).Length;
			candidates.Add( ( kv.Key, f0, e0, f1, e1, len ) );
		}
		// Sort candidates by quad quality (prefer compact quads) then edge length.
		candidates.Sort( ( x, y ) =>
		{
			double qx = QuadQuality( working.Positions, x.f0, x.f1, working.Faces );
			double qy = QuadQuality( working.Positions, y.f0, y.f1, working.Faces );
			int cmp = qy.CompareTo( qx );
			if ( cmp != 0 ) return cmp;
			return x.len.CompareTo( y.len );
		} );

		// Greedy pairing, honoring TargetTriangles as a decimation-to-target budget.
		var paired = new HashSet<int>(); // face indices paired
		var pairOf = new Dictionary<int, int>(); // face -> paired face
		int maxPairs = int.MaxValue;
		if ( options.TargetTriangles > 0 && options.TargetTriangles < inputTris )
		{
			// Each pair collapses two triangles into one quad, reducing the
			// triangle-equivalent count by two.
			maxPairs = Math.Max( 0, ( inputTris - options.TargetTriangles ) / 2 );
		}
		int pairsMade = 0;
		foreach ( var c in candidates )
		{
			if ( pairsMade >= maxPairs ) break;
			if ( paired.Contains( c.f0 ) || paired.Contains( c.f1 ) ) continue;
			paired.Add( c.f0 ); paired.Add( c.f1 );
			pairOf[ c.f0 ] = c.f1;
			pairOf[ c.f1 ] = c.f0;
			pairsMade++;
		}

		// Build output mesh.
		var outMesh = new PolyMesh();
		outMesh.Positions.AddRange( working.Positions );
		if ( working.Skin is not null )
		{
			var w = new SkinWeights( working.Skin.Count );
			for ( int i = 0; i < working.Skin.Count; i++ ) w[ i ] = working.Skin[ i ];
			outMesh.Skin = w;
		}

		int quads = 0, tris = 0, pairedEdges = paired.Count / 2;
		for ( int fi = 0; fi < working.Faces.Count; fi++ )
		{
			var f = working.Faces[ fi ];
			if ( f.Count != 3 ) { outMesh.Faces.Add( new Face( (int[])f.Indices.Clone(), (Vec2[])f.UVs.Clone(), f.Material ) ); if ( f.Count == 4 ) quads++; else tris++; continue; }
			if ( !pairOf.TryGetValue( fi, out int other ) )
			{
				outMesh.Faces.Add( new Face( (int[])f.Indices.Clone(), (Vec2[])f.UVs.Clone(), f.Material ) );
				tris++;
				continue;
			}
			if ( other < fi ) continue; // emit once per pair
			// Build quad: f indices + other's third vertex.
			var fa = working.Faces[ fi ];
			var fb = working.Faces[ other ];
			// find the vertex of fb not on shared edge
			var shared = new HashSet<int> { fa.Indices[ 0 ], fa.Indices[ 1 ], fa.Indices[ 2 ] };
			// shared edge endpoints are the two common vertices
			int extra = -1;
			foreach ( var v in fb.Indices ) if ( !shared.Contains( v ) || ( Array.IndexOf( fa.Indices, v ) < 0 ) ) { /* heuristic below */ }
			// simpler: extra = the fb vertex not in fa
			foreach ( var v in fb.Indices )
			{
				if ( Array.IndexOf( fa.Indices, v ) < 0 ) { extra = v; break; }
			}
			if ( extra < 0 ) { // fallback keep both tris
				outMesh.Faces.Add( new Face( (int[])fa.Indices.Clone(), (Vec2[])fa.UVs.Clone(), fa.Material ) );
				outMesh.Faces.Add( new Face( (int[])fb.Indices.Clone(), (Vec2[])fb.UVs.Clone(), fb.Material ) );
				tris += 2; continue;
			}
			// quad order: a, b (shared edge), extra, c
			// find shared edge endpoints
			int sa = -1, sb = -1;
			for ( int i = 0; i < 3; i++ )
			{
				if ( Array.IndexOf( fb.Indices, fa.Indices[ i ] ) >= 0 )
				{
					if ( sa < 0 ) sa = fa.Indices[ i ]; else sb = fa.Indices[ i ];
				}
			}
			// third vertex of fa
			int faThird = -1;
			foreach ( var v in fa.Indices ) if ( v != sa && v != sb ) { faThird = v; break; }

			// UV lookup helper
			Vec2 UvOf( Face face, int vertex )
			{
				for ( int c = 0; c < face.Count; c++ ) if ( face.Indices[ c ] == vertex ) return face.UVs[ c ];
				return default;
			}

			int[] qIdx = new[] { sa, sb, extra, faThird };
			var qUv = new Vec2[ 4 ];
			// for shared-edge endpoints use fa's UV; for extra use fb's; for faThird use fa's
			qUv[ 0 ] = UvOf( fa, sa );
			qUv[ 1 ] = UvOf( fa, sb );
			qUv[ 2 ] = UvOf( fb, extra );
			qUv[ 3 ] = UvOf( fa, faThird );

			outMesh.Faces.Add( new Face( qIdx, qUv, fa.Material ) );
			quads++;
		}

		// Manifold repair: ensure no edge has more than two incident faces.
		if ( options.RepairManifold )
		{
			var kept = new List<Face>();
			var counts = new Dictionary<EdgeKey, int>();
			foreach ( var f in outMesh.Faces )
			{
				// Check if adding this face would exceed 2 on any edge.
				var edges = new List<EdgeKey>();
				bool ok = true;
				for ( int e = 0; e < f.Count; e++ )
				{
					var key = new EdgeKey( f.Indices[ e ], f.Indices[ ( e + 1 ) % f.Count ] );
					edges.Add( key );
					if ( counts.TryGetValue( key, out int c ) && c + 1 > 2 ) { ok = false; break; }
				}
				if ( !ok ) continue;
				kept.Add( f );
				foreach ( var key in edges ) counts[ key ] = counts.TryGetValue( key, out int c ) ? c + 1 : 1;
			}
			outMesh.Faces.Clear();
			foreach ( var f in kept ) outMesh.Faces.Add( f );
		}

		if ( options.RepairManifold )
		{
			var kept = new List<Face>();
			var counts = new Dictionary<EdgeKey, int>();
			foreach ( var f in outMesh.Faces )
			{
				bool ok = true;
				for ( int e = 0; e < f.Count; e++ )
				{
					var key = new EdgeKey( f.Indices[ e ], f.Indices[ ( e + 1 ) % f.Count ] );
					if ( counts.TryGetValue( key, out int c ) && c >= 2 ) { ok = false; break; }
				}
				if ( !ok ) continue;
				kept.Add( f );
				for ( int e = 0; e < f.Count; e++ )
				{
					var key = new EdgeKey( f.Indices[ e ], f.Indices[ ( e + 1 ) % f.Count ] );
					counts[ key ] = counts.TryGetValue( key, out int c ) ? c + 1 : 1;
				}
			}
			outMesh.Faces.Clear();
			outMesh.Faces.AddRange( kept );
		}

		// Manifold / boundary metrics on output mesh.
		var outEdgeMap = new Dictionary<EdgeKey, int>();
		foreach ( var f in outMesh.Faces )
		{
			for ( int e = 0; e < f.Count; e++ )
			{
				var key = new EdgeKey( f.Indices[ e ], f.Indices[ ( e + 1 ) % f.Count ] );
				outEdgeMap[ key ] = outEdgeMap.TryGetValue( key, out int c ) ? c + 1 : 1;
			}
		}
		int boundary = 0; bool manifold = true;
		foreach ( var kv in outEdgeMap )
		{
			if ( kv.Value == 1 ) boundary++;
			else if ( kv.Value > 2 ) manifold = false;
		}
		return new Result( outMesh, inputTris, quads, tris, pairedEdges, boundary, manifold );
	}

	private static Vec3 FaceNormal( PolyMesh m, Face f )
	{
		var a = m.Positions[ f.Indices[ 0 ] ];
		var b = m.Positions[ f.Indices[ 1 ] ];
		var c = m.Positions[ f.Indices[ 2 ] ];
		var n = Vec3.Cross( b - a, c - a );
		float l = n.Length;
		return l > 1e-9f ? n / l : new Vec3( 0, 0, 1 );
	}

	private static int CountTris( PolyMesh m )
	{
		int n = 0;
		foreach ( var f in m.Faces )
		{
			if ( f.Count == 3 ) n++;
			else if ( f.Count == 4 ) n += 2;
			else n += f.Count - 2;
		}
		return n;
	}

	private static PolyMesh DropLowestAreaTris( PolyMesh source, int target )
	{
		// Score each face by area; keep the largest `target` triangles.
		var scored = new List<(Face face, float area)>();
		foreach ( var f in source.Faces )
		{
			if ( f.Count < 3 ) continue;
			// triangulate face for area scoring
			float area = 0;
			for ( int i = 1; i < f.Count - 1; i++ )
			{
				var a = source.Positions[ f.Indices[ 0 ] ];
				var b = source.Positions[ f.Indices[ i ] ];
				var c = source.Positions[ f.Indices[ i + 1 ] ];
				area += Vec3.Cross( b - a, c - a ).Length * 0.5f;
			}
			scored.Add( ( f, area ) );
		}
		scored.Sort( ( x, y ) => y.area.CompareTo( x.area ) );
		int keep = Math.Min( target, scored.Count );
		var outMesh = new PolyMesh();
		outMesh.Positions.AddRange( source.Positions );
		if ( source.Skin is not null )
		{
			var w = new SkinWeights( source.Skin.Count );
			for ( int i = 0; i < source.Skin.Count; i++ ) w[ i ] = source.Skin[ i ];
			outMesh.Skin = w;
		}
		for ( int i = 0; i < keep; i++ ) outMesh.Faces.Add( new Face( (int[])scored[ i ].face.Indices.Clone(), (Vec2[])scored[ i ].face.UVs.Clone(), scored[ i ].face.Material ) );
		return outMesh;
	}
}
