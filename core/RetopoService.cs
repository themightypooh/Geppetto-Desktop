using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
	/// <summary>Script-facing LOD targets: "4000,1500,500" - the targets= list of the
	/// roadmap's <c>retopo Body targets=4000,1500,500</c> operation. The verb binding itself
	/// lives in ServeMode/BuildScript (other lanes); this is the operation it calls, so the
	/// string never has to be parsed twice. Sorted descending, duplicates dropped.</summary>
	public static int[] ParseLodTargets( string targets )
	{
		if ( string.IsNullOrWhiteSpace( targets ) )
			throw new ArgumentException( "LOD targets must list at least one triangle count, e.g. 4000,1500,500.", nameof( targets ) );
		var parsed = new List<int>();
		foreach ( var piece in targets.Split( ',' ) )
		{
			if ( !int.TryParse( piece.Trim(), out int t ) || t <= 0 )
				throw new ArgumentException( $"LOD target '{piece.Trim()}' is not a positive triangle count.", nameof( targets ) );
			if ( !parsed.Contains( t ) ) parsed.Add( t );
		}
		parsed.Sort( ( a, b ) => b.CompareTo( a ) );
		return parsed.ToArray();
	}

	/// <summary>The <c>retopo Body targets=...</c> operation: one <see cref="RetopoLodChain"/>
	/// per target in the string, each with the seam/material/hard-edge-respecting defaults
	/// (or <paramref name="baseOptions"/> when given).</summary>
	public static List<Result> RetopoLodChain( PolyMesh source, string targets, Options baseOptions = null )
		=> RetopoLodChain( source, ParseLodTargets( targets ), baseOptions );

	/// <summary>What a LOD kept, structurally, from its source. These are counts, not a
	/// vertex-correspondence proof: decimation has no correspondence map, so "preserved"
	/// here means the LOD still carries the same material slots and no worse seam/hard-edge
	/// structure than the source, not that any one seam vertex survived.</summary>
	public sealed class LodPreservation
	{
		public int Target;
		public Result Lod;
		public bool MaterialsPreserved;
		public int SourceSeamEdges, LodSeamEdges;
		public int SourceHardEdges, LodHardEdges;
		public string Summary() => $"target {Target}: faces {Lod.OutputQuads}q/{Lod.OutputTriangles}t, manifold {Lod.IsManifold}, "
			+ $"materials preserved {MaterialsPreserved}, seam edges {SourceSeamEdges}->{LodSeamEdges}, hard edges {SourceHardEdges}->{LodHardEdges}";
	}

	public static LodPreservation CheckLodPreservation( PolyMesh source, int target, Result lod, Options options = null )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		if ( lod is null ) throw new ArgumentNullException( nameof( lod ) );
		float hardDeg = options?.HardEdgeAngleDegrees ?? 30f;
		var srcMats = new HashSet<int>();
		foreach ( var f in source.Faces ) srcMats.Add( f.Material );
		bool matsOk = true;
		foreach ( var f in lod.Mesh.Faces ) if ( !srcMats.Contains( f.Material ) ) matsOk = false;
		return new LodPreservation
		{
			Target = target,
			Lod = lod,
			MaterialsPreserved = matsOk,
			SourceSeamEdges = CountSeamEdges( source ),
			LodSeamEdges = CountSeamEdges( lod.Mesh ),
			SourceHardEdges = CountHardEdges( source, hardDeg ),
			LodHardEdges = CountHardEdges( lod.Mesh, hardDeg ),
		};
	}

	static int CountSeamEdges( PolyMesh mesh )
	{
		var inc = new Dictionary<EdgeKey, List<(Face face, int ca, int cb)>>();
		for ( int fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			var f = mesh.Faces[fi];
			for ( int e = 0; e < f.Count; e++ )
			{
				var key = new EdgeKey( f.Indices[e], f.Indices[( e + 1 ) % f.Count] );
				if ( !inc.TryGetValue( key, out var list ) ) { list = new List<(Face, int, int)>(); inc[key] = list; }
				list.Add( (f, e, ( e + 1 ) % f.Count) );
			}
		}
		int seams = 0;
		foreach ( var kv in inc )
		{
			if ( kv.Value.Count != 2 ) continue;
			var (f0, a0, b0) = kv.Value[0];
			var (f1, a1, b1) = kv.Value[1];
			// Same geometric edge, opposite corner order in the two faces: compare each
			// endpoint's UV across the faces (endpoint identity by vertex index).
			bool mismatch = false;
			foreach ( var (fa, ca, fb, cb) in new[] { (f0, a0, f1, a1), (f0, a0, f1, b1), (f0, b0, f1, a1), (f0, b0, f1, b1) } )
			{
				if ( fa.Indices[ca] != fb.Indices[cb] ) continue;
				if ( ca < fa.UVs.Length && cb < fb.UVs.Length
					&& ( fa.UVs[ca].x != fb.UVs[cb].x || fa.UVs[ca].y != fb.UVs[cb].y ) ) mismatch = true;
			}
			if ( mismatch ) seams++;
		}
		return seams;
	}

	static int CountHardEdges( PolyMesh mesh, float thresholdDegrees )
	{
		var inc = new Dictionary<EdgeKey, List<Face>>();
		for ( int fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			var f = mesh.Faces[fi];
			if ( f.Count < 3 ) continue;
			for ( int e = 0; e < f.Count; e++ )
			{
				var key = new EdgeKey( f.Indices[e], f.Indices[( e + 1 ) % f.Count] );
				if ( !inc.TryGetValue( key, out var list ) ) { list = new List<Face>(); inc[key] = list; }
				list.Add( f );
			}
		}
		int hard = 0;
		foreach ( var kv in inc )
		{
			if ( kv.Value.Count != 2 ) continue;
			var n0 = FaceNormal( mesh.Positions, kv.Value[0] );
			var n1 = FaceNormal( mesh.Positions, kv.Value[1] );
			double cos = Vec3.Dot( n0, n1 ) / Math.Max( 1e-12, n0.Length * n1.Length );
			cos = Math.Clamp( cos, -1.0, 1.0 );
			if ( Math.Acos( cos ) * 180.0 / Math.PI > thresholdDegrees ) hard++;
		}
		return hard;
	}


	// OpenVDB voxel remesh for sculpt (Mobile: app/SculptOpenVdb.cs).
	//
	// libgmvdb.so (Mobile tools/build-openvdb.sh, shim tools/openvdb/gm_vdb.cpp) turns the body into a
	// narrow-band level set (meshToLevelSet) and skins it back (volumeToMesh), the way Blender's voxel
	// remesher does. It plugs into the kernel's RemeshFeature voxel mode through RemeshFeature.VoxelBackend
	// (see Install), so the step, its undo, save/reopen and the voxel remesh control stay exactly as they
	// were. If the library is missing (or a call fails) the backend returns null and the kernel's built-in
	// SurfaceNets path runs instead: a soft fallback, never a crash.
	//
	// Off the main thread: Skin runs the native remesh (plus smoothing) on a worker before the step is
	// added; Stash keeps that finished skin for the rebuild that asks for the same input, so the rebuild
	// hands it over instead of computing it again.
	//
	// Packaging: the Linux x86_64 build lives in packaging/native/linux-x86_64/ (copied verbatim from
	// Mobile app/addons/openvdb/lib/linux/x86_64/, see packaging/OPENVDB.md); the published bundle
	// carries it in runtimes/<rid>/native/. Licences: OpenVDB + oneTBB, Apache 2.0 (see
	// DistributionInventory.GetLicenseAudit).
	//
	// HOST WIRING (final integrator, shared entry point — not this file): call OpenVdb.Install() once at
	// Desktop startup, before the first rebuild, and optionally install a Log sink so fallback notices
	// reach the Desktop log instead of being silent. Until then VoxelBackend stays null and the built-in
	// SurfaceNets path runs.
	public static unsafe class OpenVdb
	{
		/// <summary>Which engine the OpenVDB host used on its last call, e.g. "openvdb" or "built-in (library not installed)".
		/// A voxel run that never reaches the host (no backend installed) leaves this at the previous host call.</summary>
		public static string LastRoute { get; private set; } = "";

		/// <summary>Where fallback notices go. Null (the default) means silent; the host installs a sink
		/// that writes to its log. Never throws into the caller.</summary>
		public static Action<string> Log { get; set; }

		/// <summary>Why the library is not in use (null when it is).</summary>
		public static string Missing { get; private set; } = "not loaded yet";

		/// <summary>Tests can force the built-in path.</summary>
		public static bool Disabled;

		/// <summary>Explicit library path, for hosts and tests. When set, it is the only candidate tried;
		/// setting it resets the loader, so a test can force the missing-library path with a nonexistent
		/// path. Null means search the standard locations. The GEPPETTO_OPENVDB_LIB environment variable
		/// is the same override without code.</summary>
		public static string LibraryPathOverride
		{
			get => _libraryPathOverride;
			set
			{
				if ( _libraryPathOverride == value ) return;
				_libraryPathOverride = value;
				ResetLoader();
			}
		}

		/// <summary>Makes OpenVDB the kernel's voxel engine (the built-in SurfaceNets stays as its fallback).</summary>
		public static void Install()
		{
			if ( Available() ) RemeshFeature.VoxelBackend = Backend;
		}

		static string _libraryPathOverride;
		static bool _tried;
		static IntPtr _lib;
		static readonly object _loadLock = new();
		static delegate* unmanaged[Cdecl]<int> _version;
		static delegate* unmanaged[Cdecl]<IntPtr> _error;
		static delegate* unmanaged[Cdecl]<float*, int, int*, int, float, float, int*, int*, int*, IntPtr> _remesh;
		static delegate* unmanaged[Cdecl]<IntPtr, float*, int*, int*, void> _fetch;
		static delegate* unmanaged[Cdecl]<IntPtr, void> _free;

		static string LibraryFileName() => OperatingSystem.IsWindows() ? "gmvdb.dll"
			: OperatingSystem.IsMacOS() ? "libgmvdb.dylib"
			: "libgmvdb.so";

		static string RuntimeId() => ( OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux" )
			+ "-" + ( RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64" );

		static IEnumerable<string> Candidates()
		{
			string file = LibraryFileName();

			// Explicit overrides win outright: one candidate, no searching.
			string explicitPath = _libraryPathOverride;
			if ( string.IsNullOrEmpty( explicitPath ) )
			{
				try { explicitPath = Environment.GetEnvironmentVariable( "GEPPETTO_OPENVDB_LIB" ); } catch { }
			}
			if ( !string.IsNullOrEmpty( explicitPath ) ) { yield return explicitPath; yield break; }

			string appDir = null;
			try { appDir = AppContext.BaseDirectory; } catch { }

			if ( appDir != null )
			{
				// Bundled native layout, the same one DistributionInventory probes:
				// runtimes/<rid>/native/ next to the published binary.
				yield return Path.Combine( appDir, "runtimes", RuntimeId(), "native", file );
				// Next to the binary, the Mobile Linux export layout.
				yield return Path.Combine( appDir, file );
			}

			// System search path (a host-installed shim).
			yield return file;
		}

		/// <summary>True when libgmvdb loaded. Never throws: a missing, corrupt or wrong-build library
		/// just means the built-in SurfaceNets runs.</summary>
		public static bool Available()
		{
			lock ( _loadLock )
			{
				if ( Disabled ) return false;
				if ( _tried ) return _lib != IntPtr.Zero;
				_tried = true;
				foreach ( var c in Candidates() )
				{
					if ( string.IsNullOrEmpty( c ) ) continue;
					if ( ( c.Contains( '/' ) || c.Contains( '\\' ) ) && !File.Exists( c ) ) continue;
					IntPtr h;
					try
					{
						if ( !NativeLibrary.TryLoad( c, out h ) ) continue;
					}
					catch ( BadImageFormatException ) { continue; }
					catch ( DllNotFoundException ) { continue; }
					try
					{
						_version = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport( h, "gm_vdb_version" );
						_error = (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport( h, "gm_vdb_error" );
						_remesh = (delegate* unmanaged[Cdecl]<float*, int, int*, int, float, float, int*, int*, int*, IntPtr>)NativeLibrary.GetExport( h, "gm_vdb_remesh" );
						_fetch = (delegate* unmanaged[Cdecl]<IntPtr, float*, int*, int*, void>)NativeLibrary.GetExport( h, "gm_vdb_fetch" );
						_free = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport( h, "gm_vdb_free" );
						if ( _version() < 1 ) { NativeLibrary.Free( h ); continue; }
						_lib = h; Missing = null;
						return true;
					}
					catch ( EntryPointNotFoundException ) { NativeLibrary.Free( h ); }
				}
				Missing = "libgmvdb.so not found";
				return false;
			}
		}

		/// <summary>Forget the cached load attempt (and unload the library, if one was loaded) so the next
		/// Available() tries again from scratch. Host and test hook; never throws.</summary>
		public static void ResetLoader()
		{
			lock ( _loadLock )
			{
				if ( _lib != IntPtr.Zero )
				{
					try { NativeLibrary.Free( _lib ); } catch { }
				}
				_lib = IntPtr.Zero;
				_version = null;
				_error = null;
				_remesh = null;
				_fetch = null;
				_free = null;
				_tried = false;
				Missing = "not loaded yet";
			}
		}

		/// <summary>The native side's last refusal, or null after a good run.</summary>
		public static string LastError { get; private set; }

		/// <summary>Remesh the union of <paramref name="parts"/> at <paramref name="voxel"/> spacing. N-gons are fanned to
		/// triangles on the way in; the skin comes back as triangles and quads, with adaptivity 0 favoring quads. Null when
		/// the library is missing or fails.</summary>
		public static PolyMesh Remesh( IReadOnlyList<PolyMesh> parts, float voxel, float adaptivity = 0f )
		{
			if ( !Available() ) { LastRoute = "built-in (" + ( Disabled ? "disabled" : Missing ?? "not available" ) + ")"; return null; }
			if ( parts == null || parts.Count == 0 || !( voxel > 0f ) ) return null;
			int nv = 0, nt = 0;
			foreach ( var m in parts ) { nv += m.Positions.Count; foreach ( var f in m.Faces ) nt += Math.Max( 0, f.Count - 2 ); }
			var verts = new float[nv * 3];
			var tris = new int[nt * 3];
			int vi = 0, ti = 0, baseV = 0;
			foreach ( var m in parts )
			{
				foreach ( var p in m.Positions ) { verts[vi++] = p.x; verts[vi++] = p.y; verts[vi++] = p.z; }
				foreach ( var f in m.Faces )
					for ( int c = 1; c + 1 < f.Count; c++ )
					{
						tris[ti++] = baseV + f.Indices[0]; tris[ti++] = baseV + f.Indices[c]; tris[ti++] = baseV + f.Indices[c + 1];
					}
				baseV += m.Positions.Count;
			}
			int ov, ot, oq;
			IntPtr h;
			fixed ( float* pv = verts ) fixed ( int* pt = tris ) h = _remesh( pv, nv, pt, nt, voxel, adaptivity, &ov, &ot, &oq );
			if ( h == IntPtr.Zero )
			{
				LastError = Marshal.PtrToStringAnsi( _error() );
				LastRoute = "built-in (openvdb refused: " + ( LastError ?? "no reason" ) + ")";
				try { Log?.Invoke( "[openvdb] fell back to SurfaceNets: " + LastError ); } catch { }
				return null;
			}
			try
			{
				var rv = new float[ov * 3]; var rt = new int[ot * 3]; var rq = new int[oq * 4];
				fixed ( float* a = rv ) fixed ( int* b = rt ) fixed ( int* c = rq ) _fetch( h, a, b, c );
				var mesh = new PolyMesh();
				for ( int i = 0; i < ov; i++ ) mesh.AddVertex( new Vec3( rv[i * 3], rv[i * 3 + 1], rv[i * 3 + 2] ) );
				for ( int i = 0; i < oq; i++ ) mesh.AddFace( new[] { rq[i * 4], rq[i * 4 + 1], rq[i * 4 + 2], rq[i * 4 + 3] } );
				for ( int i = 0; i < ot; i++ ) mesh.AddFace( new[] { rt[i * 3], rt[i * 3 + 1], rt[i * 3 + 2] } );
				LastError = null;
				LastRoute = "openvdb";
				return mesh;
			}
			finally { _free( h ); }
		}

		// ---------------------------------------------------------------- the kernel hook

		/// <summary>A skin computed ahead of the rebuild (off the main thread), keyed by its input.</summary>
		static (long Key, PolyMesh Mesh) _ready;
		static readonly object _gate = new();

		public static long Key( IReadOnlyList<PolyMesh> parts, float voxel, int smooth )
		{
			var h = new HashCode();
			h.Add( voxel ); h.Add( smooth );
			foreach ( var m in parts )
			{
				h.Add( m.Positions.Count ); h.Add( m.FaceCount );
				double s = 0; foreach ( var p in m.Positions ) s += p.x * 0.37 + p.y * 0.61 + p.z * 0.29;
				h.Add( Math.Round( s, 3 ) );
			}
			return h.ToHashCode();
		}

		/// <summary>Remesh and smooth: the whole job, safe on a worker (touches only its inputs and a fresh mesh).</summary>
		public static PolyMesh Skin( IReadOnlyList<PolyMesh> parts, float voxel, int smooth )
		{
			var m = Remesh( parts, voxel );
			if ( m == null || m.FaceCount == 0 ) return null;
			if ( smooth > 0 ) MeshSmooth.Taubin( m, smooth );
			return m;
		}

		/// <summary>Keep a finished skin for the next rebuild that asks for this exact input.</summary>
		public static void Stash( long key, PolyMesh mesh ) { lock ( _gate ) _ready = ( key, mesh ); }

		static PolyMesh Backend( IReadOnlyList<PolyMesh> parts, float voxel, int smooth )
		{
			var key = Key( parts, voxel, smooth );
			lock ( _gate )
				if ( _ready.Mesh != null && _ready.Key == key ) { var m = _ready.Mesh; _ready = default; return m; }
			return Skin( parts, voxel, smooth );   // replays (undo, reopen) compute it here; null falls back to SurfaceNets
		}
	}


	// Auto retopo (Mobile: app/AutoRetopo.cs, InstantMeshes).
	//
	// Instant Meshes (BSD-3-Clause, see packaging/INSTANTMESHES.md) rebuilds a dense sculpt as a
	// field-aligned, quad-dominant (or all-triangle) mesh near a face target. The solver/extraction core
	// runs headless in libgminstantmeshes.so (Mobile tools/build-instantmeshes.sh, shim
	// tools/instantmeshes/gm_instantmeshes.cpp); the Desktop copy of the Linux x86_64 build lives in
	// packaging/native/linux-x86_64/, and the published bundle carries it in runtimes/<rid>/native/.
	//
	// The solve runs on a worker thread with a progress line and a working Cancel (see AutoRetopo);
	// the result lands as one Mesh edit step ("Auto retopo"), so one Undo takes it back. UVs and skin
	// weights are not carried over — the bake handoff (AutoRetopo.BakeOnto) re-derives them instead.
	// If the library is missing the call says so honestly and the voxel / quad remesh still work.
	public static unsafe class InstantMeshes
	{
		/// <summary>Why the library is not in use (null when it is).</summary>
		public static string LoadError => _loadError;

		/// <summary>Explicit library path, for hosts and tests. When set, it is the only candidate tried;
		/// setting it resets the loader, so a test can force the missing-library path with a nonexistent
		/// path. Null means search the standard locations. The GEPPETTO_INSTANTMESHES_LIB environment
		/// variable is the same override without code.</summary>
		public static string LibraryPathOverride
		{
			get => _libraryPathOverride;
			set
			{
				if ( _libraryPathOverride == value ) return;
				_libraryPathOverride = value;
				ResetLoader();
			}
		}

		static string _libraryPathOverride;
		static bool _tried;
		static IntPtr _lib;
		static string _loadError;
		static readonly object _loadLock = new();
		static delegate* unmanaged[Cdecl]<int> _version;
		static delegate* unmanaged[Cdecl]<float*, int, uint*, int, int, int, int, int, int*, int*, IntPtr> _remesh;
		static delegate* unmanaged[Cdecl]<IntPtr, int*, int*, int*, void> _counts;
		static delegate* unmanaged[Cdecl]<IntPtr, float*, int*, int> _copy;
		static delegate* unmanaged[Cdecl]<IntPtr, void> _free;
		static delegate* unmanaged[Cdecl]<IntPtr> _error;

		static string LibraryFileName() => OperatingSystem.IsWindows() ? "gminstantmeshes.dll"
			: OperatingSystem.IsMacOS() ? "libgminstantmeshes.dylib"
			: "libgminstantmeshes.so";

		static string RuntimeId() => ( OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux" )
			+ "-" + ( RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64" );

		static IEnumerable<string> Candidates()
		{
			string file = LibraryFileName();

			// Explicit overrides win outright: one candidate, no searching.
			string explicitPath = _libraryPathOverride;
			if ( string.IsNullOrEmpty( explicitPath ) )
			{
				try { explicitPath = Environment.GetEnvironmentVariable( "GEPPETTO_INSTANTMESHES_LIB" ); } catch { }
			}
			if ( !string.IsNullOrEmpty( explicitPath ) ) { yield return explicitPath; yield break; }

			string appDir = null;
			try { appDir = AppContext.BaseDirectory; } catch { }

			if ( appDir != null )
			{
				// Bundled native layout, the same one DistributionInventory probes:
				// runtimes/<rid>/native/ next to the published binary.
				yield return Path.Combine( appDir, "runtimes", RuntimeId(), "native", file );
				// Next to the binary, the Mobile Linux export layout.
				yield return Path.Combine( appDir, file );
			}

			// System search path (a host-installed shim).
			yield return file;
		}

		/// <summary>True when libgminstantmeshes loaded. Never throws: a missing, corrupt or wrong-build
		/// library just means auto retopo refuses honestly.</summary>
		public static bool Available()
		{
			lock ( _loadLock )
			{
				if ( _tried ) return _lib != IntPtr.Zero;
				_tried = true;
				foreach ( var c in Candidates() )
				{
					if ( string.IsNullOrEmpty( c ) ) continue;
					if ( ( c.Contains( '/' ) || c.Contains( '\\' ) ) && !File.Exists( c ) ) continue;
					IntPtr h;
					try
					{
						if ( !NativeLibrary.TryLoad( c, out h ) ) continue;
					}
					catch ( BadImageFormatException ) { continue; }
					catch ( DllNotFoundException ) { continue; }
					try
					{
						_version = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport( h, "gm_im_version" );
						_remesh = (delegate* unmanaged[Cdecl]<float*, int, uint*, int, int, int, int, int, int*, int*, IntPtr>)NativeLibrary.GetExport( h, "gm_im_remesh" );
						_counts = (delegate* unmanaged[Cdecl]<IntPtr, int*, int*, int*, void>)NativeLibrary.GetExport( h, "gm_im_counts" );
						_copy = (delegate* unmanaged[Cdecl]<IntPtr, float*, int*, int>)NativeLibrary.GetExport( h, "gm_im_copy" );
						_free = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport( h, "gm_im_free" );
						_error = (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport( h, "gm_im_error" );
						if ( _version() < 1 ) { NativeLibrary.Free( h ); continue; }
						_lib = h;
						return true;
					}
					catch ( EntryPointNotFoundException ) { NativeLibrary.Free( h ); }
				}
				_loadError = "auto retopo isn't installed in this build";
				return false;
			}
		}

		/// <summary>Forget the cached load attempt (and unload the library, if one was loaded) so the next
		/// Available() tries again from scratch. Host and test hook; never throws.</summary>
		public static void ResetLoader()
		{
			lock ( _loadLock )
			{
				if ( _lib != IntPtr.Zero )
				{
					try { NativeLibrary.Free( _lib ); } catch { }
				}
				_lib = IntPtr.Zero;
				_version = null;
				_remesh = null;
				_counts = null;
				_copy = null;
				_free = null;
				_error = null;
				_tried = false;
				_loadError = null;
			}
		}

		/// <summary>Flat input: 3 floats per vertex, 3 indices per triangle.</summary>
		public sealed class Input { public float[] Positions; public uint[] Triangles; }

		/// <summary>Fan-triangulates the mesh's faces (any n-gon) into flat arrays.</summary>
		public static Input From( PolyMesh m )
		{
			var p = new float[m.Positions.Count * 3];
			for ( int i = 0; i < m.Positions.Count; i++ ) { p[i * 3] = m.Positions[i].x; p[i * 3 + 1] = m.Positions[i].y; p[i * 3 + 2] = m.Positions[i].z; }
			var t = new List<uint>( m.FaceCount * 6 );
			foreach ( var f in m.Faces )
				for ( int c = 1; c + 1 < f.Count; c++ ) { t.Add( (uint)f.Indices[0] ); t.Add( (uint)f.Indices[c] ); t.Add( (uint)f.Indices[c + 1] ); }
			return new Input { Positions = p, Triangles = t.ToArray() };
		}

		/// <summary>Runs the solver (blocking; call from a worker thread). <paramref name="cancel"/> and
		/// <paramref name="progress"/> are shared ints the native side polls / writes (0..100).</summary>
		public static PolyMesh Remesh( Input input, int targetFaces, bool quads, int smooth, int threads, int[] cancel, int[] progress, out string error )
		{
			error = null;
			if ( !Available() ) { error = _loadError; return null; }
			IntPtr h;
			fixed ( float* p = input.Positions ) fixed ( uint* t = input.Triangles ) fixed ( int* c = cancel ) fixed ( int* pr = progress )
				h = _remesh( p, input.Positions.Length / 3, t, input.Triangles.Length / 3, targetFaces, quads ? 1 : 0, smooth, threads, c, pr );
			if ( h == IntPtr.Zero ) { error = Marshal.PtrToStringUTF8( _error() ) ?? "it couldn't be solved"; return null; }
			try
			{
				int nv, nf, nc;
				_counts( h, &nv, &nf, &nc );
				var pos = new float[nv * 3]; var faces = new int[nf * 4];
				fixed ( float* pp = pos ) fixed ( int* ff = faces ) if ( _copy( h, pp, ff ) == 0 ) { error = "no result"; return null; }
				var m = new PolyMesh();
				for ( int i = 0; i < nv; i++ ) m.AddVertex( new Vec3( pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2] ) );
				for ( int i = 0; i < nf; i++ )
				{
					int a = faces[i * 4], b = faces[i * 4 + 1], c2 = faces[i * 4 + 2], d = faces[i * 4 + 3];
					if ( a == b || b == c2 || a == c2 ) continue;
					m.AddFace( d >= 0 && d != a && d != b && d != c2 ? new[] { a, b, c2, d } : new[] { a, b, c2 } );
				}
				return m;
			}
			finally { _free( h ); }
		}
	}

	/// <summary>Normal + AO maps baked from the sculpt onto the retopo result's UVs, as paint canvases
	/// the <see cref="MeshEditFeature.SetBake"/> handoff takes. A null member means that map did not bake.</summary>
	public sealed class BakedMaps
	{
		public PaintCanvas Normal;
		public PaintCanvas Ao;
	}

	/// <summary>
	/// Auto retopo: rebuild the last visible body as clean, flowing faces near a face count, through
	/// Instant Meshes (see <see cref="InstantMeshes"/>). Ported from Mobile app/AutoRetopo.cs with the
	/// Godot parts removed: no panels, no busy card, no Main — the host drives Prepare on its own thread,
	/// Solve (+ BakeOnto) on a worker thread, and Apply back on its own thread, exactly as Mobile's
	/// busy card does around the same three calls.
	///
	/// WHAT IT KEEPS AND LOSES. The result is one Mesh edit step ("Auto retopo"), so one host Undo takes
	/// it back — Apply adds exactly one feature, and removing it + rebuilding restores the sculpt. UVs and
	/// skin weights are NOT carried over (the solver makes new vertices); the bake handoff re-derives the
	/// maps, and a host that needs weights can run <see cref="RetopoService.TransferSkinWeights"/> from the
	/// source onto the result before Apply. The face count is a target, not a promise: the solver lands
	/// near it (Mobile's checks allow 0.75x–1.3x), and Apply's message reports the count it actually got
	/// with the quad share counted from the faces, never a label the mesh does not earn.
	///
	/// HOST WIRING (final integrator, shared entry point — not this file): the bot-command / UI host calls
	/// Prepare, runs Solve + BakeOnto on a worker (Task.Run) with Cancel wired to its cancel control and
	/// Progress read for its progress line, then Apply on its own thread. Until the library is installed
	/// Prepare refuses with "Auto retopo isn't in this build." and the voxel / quad remesh still work.
	/// </summary>
	public sealed class AutoRetopo
	{
		public AutoRetopo( PartStudio studio )
		{
			Studio = studio ?? throw new ArgumentNullException( nameof( studio ) );
		}

		public PartStudio Studio { get; }

		public static readonly int[] FaceChoices = { 1000, 2000, 5000, 10000 };
		public int TargetFaces = 2000;
		public bool Quads = true;
		public int MaxFaces = 60000;

		public string Refusal { get; private set; }
		public string Message { get; private set; }
		public MeshEditFeature Last { get; private set; }
		public double SolveMs { get; private set; }

		/// <summary>Boundary (hole) and non-manifold edge counts of the last solved / applied mesh, from
		/// MeshValidator.Validate. Instant Meshes does not guarantee a watertight result (see
		/// packaging/INSTANTMESHES.md): a solved mesh with holes still applies, and Apply() says so in its
		/// message instead of labelling it clean. Zero before the first solve.</summary>
		public int LastBoundaryEdges { get; private set; }
		public int LastNonManifoldEdges { get; private set; }

		void MeasureHoles( PolyMesh m )
		{
			var v = MeshValidator.Validate( m );
			LastBoundaryEdges = v.BoundaryEdges;
			LastNonManifoldEdges = v.NonManifoldEdges;
		}

		/// <summary>Bake the sculpt onto the result as normal + AO maps (see BakeOnto) after the solve.
		/// Saved with the Auto retopo edit (MeshEditFeature.SetBake), so the maps survive rebuilds, undo and reload.</summary>
		public bool BakeAfter = true;
		public static readonly int[] BakeSizes = { 256, 512, 1024 };
		public int BakeSize = 512;
		public BakedMaps LastBake { get; private set; }
		public volatile bool Baking;

		public readonly int[] Cancel = new int[1];
		public readonly int[] Progress = new int[1];

		bool Ok( string m ) { Refusal = null; Message = m; return true; }
		bool Refuse( string why ) { Refusal = why; Message = null; return false; }


		public Body Target => Studio.Bodies.LastOrDefault( b => b.Visible && b.Mesh != null && b.Mesh.FaceCount > 0 );

		public string Preview()
		{
			var body = Target;
			if ( !InstantMeshes.Available() ) return "Auto retopo isn't in this build. Voxel remesh above still works.";
			if ( body == null ) return "No body yet. Make a body in CAD first.";
			return $"About {TargetFaces:N0} {( Quads ? "quads" : "triangles" )} (now {body.Mesh.FaceCount:N0} faces)" + ( BakeAfter ? $", then {BakeSize}px normal + AO maps" : "" );
		}

		/// <summary>Calling thread: checks and snapshots the input. Null result means refused (see Refusal).</summary>
		public (Body Body, PolyMesh Source, InstantMeshes.Input Input)? Prepare()
		{
			if ( !InstantMeshes.Available() ) { Refuse( "Auto retopo isn't in this build." ); return null; }
			var body = Target;
			if ( body == null ) { Refuse( "Nothing to retopo: make a body in CAD first." ); return null; }
			if ( TargetFaces > MaxFaces ) { Refuse( $"{TargetFaces:N0} faces is too heavy (limit {MaxFaces:N0})." ); return null; }
			var src = body.Mesh.Clone();
			Cancel[0] = 0; Progress[0] = 0;
			return ( body, src, InstantMeshes.From( src ) );
		}

		/// <summary>Worker thread: the solve. Null when it failed or was cancelled (see <paramref name="error"/>).</summary>
		public PolyMesh Solve( InstantMeshes.Input input, out string error )
		{
			LastBoundaryEdges = 0;
			LastNonManifoldEdges = 0;
			var sw = Stopwatch.StartNew();
			int threads = Math.Clamp( Environment.ProcessorCount - 1, 1, 4 );
			var m = InstantMeshes.Remesh( input, TargetFaces, Quads, 2, threads, Cancel, Progress, out error );
			SolveMs = sw.Elapsed.TotalMilliseconds;
			if ( m != null ) MeasureHoles( m );
			return m;
		}

		/// <summary>Worker thread: unwraps <paramref name="result"/> and bakes <paramref name="source"/> onto it.
		/// Never throws: a bake that fails leaves LastBake null and the retopo still applies without maps.</summary>
		public void BakeOnto( PolyMesh result, PolyMesh source )
		{
			LastBake = null;
			if ( !BakeAfter || result == null || source == null || Cancel[0] != 0 ) return;
			Baking = true;
			try
			{
				if ( !HasUVs( result ) )
				{
					var unwrap = UVUnwrap.Unwrap( result );
					if ( unwrap.Charts == 0 ) return;
				}
				var set = NormalBake.BakeAll( result, source, BakeSize, BakeSize,
					BakeMaps.Normal | BakeMaps.AmbientOcclusion,
					new BakeOptions { AoRays = 16 } );
				LastBake = new BakedMaps { Normal = ToCanvas( set.Normal ), Ao = ToCanvas( set.AmbientOcclusion ) };
			}
			catch ( Exception e ) { KernelLog.Soft( "AutoRetopo.BakeOnto", e ); LastBake = null; }
			finally { Baking = false; }
		}

		static bool HasUVs( PolyMesh m ) => m.Faces.Any( f => f.UVs.Any( uv => uv.x != 0f || uv.y != 0f ) );

		/// <summary>A baked map as a paint canvas: row 0 is v = 0 on both sides, so the rows copy
		/// straight — no flip (same as Mobile's SculptBake.Run and PaintBake.FromBaked). A flip here
		/// would put the maps upside-down against the UVs and light exactly as wrongly as an inverted
		/// green channel, invisible in a thumbnail. Unfilled texels go transparent. Null in, null out.</summary>
		static PaintCanvas ToCanvas( BakedMap map )
		{
			if ( map is null ) return null;
			var canvas = new PaintCanvas( map.Width, map.Height );
			for ( var i = 0; i < map.Width * map.Height; i++ )
			{
				var s = i * 3;
				var d = i * 4;
				canvas.Rgba[d] = map.Rgb[s];
				canvas.Rgba[d + 1] = map.Rgb[s + 1];
				canvas.Rgba[d + 2] = map.Rgb[s + 2];
				canvas.Rgba[d + 3] = map.Filled[i] ? (byte)255 : (byte)0;
			}
			return canvas;
		}

		/// <summary>Calling thread: the result as one Mesh edit step on the body. Adds exactly one feature
		/// ("Auto retopo"), so one host Undo — remove that feature and rebuild — restores the sculpt.</summary>
		public bool Apply( Body body, PolyMesh source, PolyMesh result )
		{
			if ( result == null || result.FaceCount == 0 ) return Refuse( "Auto retopo came out empty: pick more faces." );
			if ( Target?.Id != body.Id || Target.Mesh.FaceCount != source.FaceCount ) return Refuse( "The body changed while auto retopo ran. Nothing was applied." );
			var f = new MeshEditFeature { Name = "Auto retopo" };
			f.Bodies.BodyIds.Add( body.Id );
			f.Commit( result, body.Mesh );
			if ( LastBake != null ) f.SetBake( LastBake.Normal, LastBake.Ao );
			int at = Math.Min( Studio.RollbackIndex, Studio.Features.Count );
			if ( at < Studio.Features.Count ) Studio.Insert( at, f ); else Studio.Add( f );
			Studio.Rebuild();
			if ( f.Error != null )
			{
				var why = f.Error;
				Studio.Remove( f );
				Studio.Rebuild();
				return Refuse( why );
			}
			Last = f;
			MeasureHoles( result );
			string bakeNote = LastBake != null ? $" Normal and AO maps baked from the sculpt ({BakeSize}px)." : BakeAfter ? " The bake didn't come out, so there are no maps." : "";
			string holeNote = LastBoundaryEdges > 0 || LastNonManifoldEdges > 0
				? $" {LastBoundaryEdges} boundary edges, {LastNonManifoldEdges} non-manifold edges — not watertight."
				: "";
			int quads = result.Faces.Count( x => x.Count == 4 );
			return Ok( Quads
				? $"Auto retopo: {result.FaceCount:N0} faces ({quads * 100 / Math.Max( 1, result.FaceCount )}% quads).{holeNote}{bakeNote}"
				: $"Auto retopo: {result.FaceCount:N0} triangles.{holeNote}{bakeNote}" );
		}

		/// <summary>Whole run on the calling thread (checks only): Prepare, Solve, BakeOnto, Apply.
		/// A host that wants the solve off its thread calls those four itself, as Mobile's busy card does.</summary>
		public bool RunBlocking()
		{
			var prep = Prepare();
			if ( prep == null ) return false;
			var (body, src, input) = prep.Value;
			var m = Solve( input, out var err );
			BakeOnto( m, src );
			if ( m == null ) return Refuse( err == "cancelled" ? "Auto retopo cancelled. Nothing changed." : $"Auto retopo failed: {err}." );
			return Apply( body, src, m );
		}
	}

}
