using System;
using System.Collections.Generic;
using Effigy;

namespace Marionette.Desktop;

public static class KnifeService
{
	public sealed class Options { public float Epsilon { get; set; } = 1e-5f; }

	public sealed class Result
	{
		public PolyMesh Mesh { get; }
		public int SplitFaces { get; }
		public int NewVertices { get; }
		public Result( PolyMesh m, int s, int n ) { Mesh = m; SplitFaces = s; NewVertices = n; }
	}

	public static Result CutByPolyline( PolyMesh source, Vec3[] points, Vec3 planeNormal, Options options = null )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		if ( points is null || points.Length < 2 ) throw new ArgumentException( "polyline needs at least 2 points" );
		var cur = source;
		int totalSplit = 0, totalNew = 0;
		for ( int i = 0; i + 1 < points.Length; i++ )
		{
			var mid = ( points[ i ] + points[ i + 1 ] ) * 0.5f;
			var dir = ( points[ i + 1 ] - points[ i ] );
			// plane through the segment, normal = cross(dir, planeNormal)
			var pn = Vec3.Cross( dir, planeNormal );
			if ( pn.Length < 1e-9f ) pn = planeNormal;
			pn = pn / pn.Length;
			var r = CutByPlane( cur, mid, pn, options );
			cur = r.Mesh;
			totalSplit += r.SplitFaces;
			totalNew += r.NewVertices;
		}
		return new Result( cur, totalSplit, totalNew );
	}

	public static Result CutByPlane( PolyMesh source, Vec3 planePoint, Vec3 planeNormal, Options options = null )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		options ??= new Options();
		float nl = planeNormal.Length; if ( nl < 1e-12f ) throw new ArgumentException( "degenerate plane normal" );
		var n = planeNormal / nl;
		float Signed( Vec3 p ) => Vec3.Dot( p - planePoint, n );

		var outMesh = new PolyMesh();
		outMesh.Positions.AddRange( source.Positions );
		if ( source.Skin is not null )
		{
			var w = new SkinWeights( source.Skin.Count );
			for ( int i = 0; i < source.Skin.Count; i++ ) w[ i ] = source.Skin[ i ];
			outMesh.Skin = w;
		}
		if ( source.VertexColors is not null ) outMesh.VertexColors = (Vec4[])source.VertexColors.Clone();

		int split = 0, newVerts = 0;
		foreach ( var f in source.Faces )
		{
			// Fan-triangulate n-gons to triangles for cutting.
			for ( int ti = 1; ti + 1 < f.Count; ti++ )
			{
				int[] tri = new[] { f.Indices[ 0 ], f.Indices[ ti ], f.Indices[ ti + 1 ] };
				Vec2[] triUV = new[] { f.UVs[ 0 ], f.UVs[ ti ], f.UVs[ ti + 1 ] };
				float[] sgn = new float[ 3 ];
				for ( int c = 0; c < 3; c++ ) sgn[ c ] = Signed( source.Positions[ tri[ c ] ] );

				bool hasPos = false, hasNeg = false;
				foreach ( var s in sgn ) { if ( s > options.Epsilon ) hasPos = true; if ( s < -options.Epsilon ) hasNeg = true; }
				if ( !( hasPos && hasNeg ) )
				{
					outMesh.Faces.Add( new Face( tri, (Vec2[])triUV.Clone(), f.Material ) );
					continue;
				}
				// Find the two edges that cross (sign change).
				var crossEdges = new List<(int a, int b, int ci)>();
				for ( int e = 0; e < 3; e++ )
				{
					int a = e, b = ( e + 1 ) % 3;
					if ( ( sgn[ a ] > options.Epsilon ) != ( sgn[ b ] > options.Epsilon ) &&
					     ( sgn[ a ] < -options.Epsilon ) != ( sgn[ b ] < -options.Epsilon ) )
						crossEdges.Add( ( a, b, e ) );
				}
				if ( crossEdges.Count != 2 )
				{
					// degenerate; keep original
					outMesh.Faces.Add( new Face( tri, (Vec2[])triUV.Clone(), f.Material ) );
					continue;
				}
				// Compute intersection points on the two crossing edges.
				var (e1a, e1b, _) = crossEdges[ 0 ];
				var (e2a, e2b, _) = crossEdges[ 1 ];
				Vec3 Intersect( int ia, int ib )
				{
					var pa = source.Positions[ tri[ ia ] ]; var pb = source.Positions[ tri[ ib ] ];
					float sa = sgn[ ia ], sb = sgn[ ib ];
					float t = sa / ( sa - sb );
					return pa + ( pb - pa ) * t;
				}
				Vec2 IntersectUV( int ia, int ib )
				{
					var ua = triUV[ ia ]; var ub = triUV[ ib ];
					float sa = sgn[ ia ], sb = sgn[ ib ];
					float t = sa / ( sa - sb );
					return new Vec2( ua.x + ( ub.x - ua.x ) * t, ua.y + ( ub.y - ua.y ) * t );
				}
				var p1 = Intersect( e1a, e1b );
				var p2 = Intersect( e2a, e2b );
				var uv1 = IntersectUV( e1a, e1b );
				var uv2 = IntersectUV( e2a, e2b );

				int i1 = outMesh.Positions.Count; outMesh.Positions.Add( p1 ); newVerts++;
				int i2 = outMesh.Positions.Count; outMesh.Positions.Add( p2 ); newVerts++;
				float t1 = sgn[ e1a ] / ( sgn[ e1a ] - sgn[ e1b ] );
				float t2 = sgn[ e2a ] / ( sgn[ e2a ] - sgn[ e2b ] );
				if ( outMesh.Skin is not null && source.Skin is not null )
				{
					var w = outMesh.Skin;
					while ( w.Count < outMesh.Positions.Count ) w.Vertices.Add( Array.Empty<BoneWeight>() );
					var wa1 = tri[ e1a ] < source.Skin.Count ? source.Skin[ tri[ e1a ] ] : Array.Empty<BoneWeight>();
					var wb1 = tri[ e1b ] < source.Skin.Count ? source.Skin[ tri[ e1b ] ] : Array.Empty<BoneWeight>();
					w[ i1 ] = SkinWeights.Blend( new[] { ( wa1, 1f - t1 ), ( wb1, t1 ) } );
					var wa2 = tri[ e2a ] < source.Skin.Count ? source.Skin[ tri[ e2a ] ] : Array.Empty<BoneWeight>();
					var wb2 = tri[ e2b ] < source.Skin.Count ? source.Skin[ tri[ e2b ] ] : Array.Empty<BoneWeight>();
					w[ i2 ] = SkinWeights.Blend( new[] { ( wa2, 1f - t2 ), ( wb2, t2 ) } );
				}
				else if ( outMesh.Skin is not null )
				{
					var w = outMesh.Skin;
					while ( w.Count < outMesh.Positions.Count ) w.Vertices.Add( Array.Empty<BoneWeight>() );
				}
				if ( outMesh.VertexColors is not null )
				{
					var vc = outMesh.VertexColors;
					Array.Resize( ref outMesh.VertexColors, outMesh.Positions.Count );
					Vec4 c1 = new Vec4( 1, 1, 1, 1 ), c2 = new Vec4( 1, 1, 1, 1 );
					if ( source.VertexColors is not null )
					{
						var ca1 = tri[ e1a ] < source.VertexColors.Length ? source.VertexColors[ tri[ e1a ] ] : c1;
						var cb1 = tri[ e1b ] < source.VertexColors.Length ? source.VertexColors[ tri[ e1b ] ] : c1;
						c1 = ca1 * ( 1 - t1 ) + cb1 * t1;
						var ca2 = tri[ e2a ] < source.VertexColors.Length ? source.VertexColors[ tri[ e2a ] ] : c2;
						var cb2 = tri[ e2b ] < source.VertexColors.Length ? source.VertexColors[ tri[ e2b ] ] : c2;
						c2 = ca2 * ( 1 - t2 ) + cb2 * t2;
					}
					outMesh.VertexColors[ i1 ] = c1;
					outMesh.VertexColors[ i2 ] = c2;
				}

				// Identify the lone vertex on one side (the one whose sign differs).
				// Build two triangles: (lone, i1, i2) and (other two + i1/i2).
				// Determine lone index: the vertex not on cross edges'... simpler: lone = the vertex whose sign has unique polarity.
				int lone = -1;
				for ( int c = 0; c < 3; c++ )
				{
					bool pos = sgn[ c ] > options.Epsilon;
					int others = 0;
					for ( int c2 = 0; c2 < 3; c2++ ) if ( c2 != c && ( sgn[ c2 ] > options.Epsilon ) == pos ) others++;
					if ( others == 0 ) { lone = c; break; }
				}
				if ( lone < 0 ) lone = 0;
				int o1 = ( lone + 1 ) % 3, o2 = ( lone + 2 ) % 3;

				// Triangle 1: lone, intersection on edge (lone-o1), intersection on edge (lone-o2)
				// find which cross edge connects lone
				int ia = crossEdges[ 0 ].a == lone ? crossEdges[ 0 ].b : ( crossEdges[ 0 ].b == lone ? crossEdges[ 0 ].a : -1 );
				// intersections: one on edge lone-o1 or lone-o2
				// We have i1 (edge e1a-e1b), i2 (edge e2a-e2b). Map: intersection near lone side.
				// Just emit: (lone, i1, i2) and (o1, o2, i1/i2) with i2/i1.
				outMesh.Faces.Add( new Face( new[] { tri[ lone ], i1, i2 }, new[] { triUV[ lone ], uv1, uv2 }, f.Material ) );
				outMesh.Faces.Add( new Face( new[] { tri[ o1 ], tri[ o2 ], i2 }, new[] { triUV[ o1 ], triUV[ o2 ], uv2 }, f.Material ) );
				outMesh.Faces.Add( new Face( new[] { tri[ o1 ], i2, i1 }, new[] { triUV[ o1 ], uv2, uv1 }, f.Material ) );
				split++;
			}
		}
		return new Result( outMesh, split, newVerts );
	}
}
