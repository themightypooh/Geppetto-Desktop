using System;
using Effigy;
using Marionette.Desktop;

namespace Marionette.Desktop;

public static class ShrinkwrapService
{
	public enum Mode { NearestSurface, NormalProjection }
	public sealed class Options
	{
		public Mode Mode { get; set; } = Mode.NearestSurface;
		public float MaxDistance { get; set; } = float.MaxValue;
		public float Offset { get; set; } = 0f;
	}
	public sealed class Result { public PolyMesh Mesh { get; } public int Moved { get; } public Result( PolyMesh m, int moved ){ Mesh=m; Moved=moved; } }

	public static Result WrapToSurface( PolyMesh source, PolyMesh target, Options options=null )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		if ( target is null ) throw new ArgumentNullException( nameof( target ) );
		options ??= new Options();
		// Per-vertex normals for source (area-weighted) used by normal projection.
		var srcNormals = new Vec3[ source.Positions.Count ];
		for ( int fi = 0; fi < source.Faces.Count; fi++ )
		{
			var f = source.Faces[ fi ];
			if ( f.Count < 3 ) continue;
			for ( int ti = 1; ti + 1 < f.Count; ti++ )
			{
				var a = source.Positions[ f.Indices[ 0 ] ];
				var b = source.Positions[ f.Indices[ ti ] ];
				var c = source.Positions[ f.Indices[ ti + 1 ] ];
				var nrm = Vec3.Cross( b - a, c - a );
				for ( int k = 0; k < 3; k++ )
				{
					int vi = f.Indices[ k == 0 ? 0 : ( k == 1 ? ti : ti + 1 ) ];
					if ( vi < srcNormals.Length ) srcNormals[ vi ] = srcNormals[ vi ] + nrm;
				}
			}
		}
		for ( int i = 0; i < srcNormals.Length; i++ )
		{
			float l = srcNormals[ i ].Length;
			if ( l > 1e-12f ) srcNormals[ i ] = srcNormals[ i ] / l;
			else srcNormals[ i ] = new Vec3( 0, 0, 1 );
		}

		var outMesh = new PolyMesh();
		outMesh.Positions.AddRange( source.Positions );
		if ( source.Skin is not null )
		{
			var w = new SkinWeights( source.Skin.Count );
			for ( int i = 0; i < source.Skin.Count; i++ ) w[ i ] = source.Skin[ i ];
			outMesh.Skin = w;
		}
		if ( source.VertexColors is not null ) outMesh.VertexColors = (Vec4[])source.VertexColors.Clone();
		outMesh.Faces.AddRange( source.Faces );
		int moved = 0;
		for ( int i = 0; i < source.Positions.Count; i++ )
		{
			var p = source.Positions[ i ];
			Vec3 bestPt = p;
			float bestDist = float.MaxValue;
			bool hit = false;
			if ( options.Mode == Mode.NormalProjection )
			{
				var dir = srcNormals[ i ];
				// ray vs target triangles
				float tHit = float.MaxValue;
				Vec3 hitPt = p;
				foreach ( var f in target.Faces )
				{
					if ( f.Count < 3 ) continue;
					for ( int ti = 1; ti + 1 < f.Count; ti++ )
					{
						var a = target.Positions[ f.Indices[ 0 ] ];
						var b = target.Positions[ f.Indices[ ti ] ];
						var c = target.Positions[ f.Indices[ ti + 1 ] ];
						if ( RayTriangle( p, dir, a, b, c, out float t, out Vec3 pt ) )
						{
							if ( t >= 0 && t < tHit ) { tHit = t; hitPt = pt; }
						}
					}
				}
				if ( tHit < float.MaxValue )
				{
					bestPt = hitPt + dir * options.Offset;
					bestDist = tHit;
					hit = true;
				}
			}
			else
			{
				float best = float.MaxValue; Vec3 bp = p;
				foreach ( var f in target.Faces )
				{
					if ( f.Count < 3 ) continue;
					for ( int ti = 1; ti + 1 < f.Count; ti++ )
					{
						var a = target.Positions[ f.Indices[ 0 ] ];
						var b = target.Positions[ f.Indices[ ti ] ];
						var c = target.Positions[ f.Indices[ ti + 1 ] ];
						var cp = ClosestPointOnTriangle( p, a, b, c );
						float d = ( cp - p ).LengthSquared;
						if ( d < best ) { best = d; bp = cp; }
					}
				}
				if ( MathF.Sqrt( best ) <= options.MaxDistance )
				{
					bestPt = bp;
					bestDist = ( float ) Math.Sqrt( best );
					hit = true;
				}
				if ( hit && options.Offset != 0f )
				{
					var dvec = p - bestPt;
					float dl = dvec.Length;
					if ( dl > 1e-12f ) bestPt = bestPt + ( dvec / dl ) * options.Offset;
				}
			}
			if ( hit && bestDist <= options.MaxDistance + 1e-6f )
			{
				outMesh.Positions[ i ] = bestPt;
				moved++;
			}
			else
			{
			}
		}
		return new Result( outMesh, moved );
	}

	static bool RayTriangle( Vec3 o, Vec3 d, Vec3 a, Vec3 b, Vec3 c, out float t, out Vec3 point )
	{
		t = 0; point = o;
		var e1 = b - a; var e2 = c - a;
		var h = Vec3.Cross( d, e2 );
		float det = Vec3.Dot( e1, h );
		if ( MathF.Abs( det ) < 1e-12f ) return false;
		float inv = 1f / det;
		var s = o - a;
		float u = inv * Vec3.Dot( s, h );
		if ( u < 0f || u > 1f ) return false;
		var q = Vec3.Cross( s, e1 );
		float v = inv * Vec3.Dot( d, q );
		if ( v < 0f || u + v > 1f ) return false;
		t = inv * Vec3.Dot( e2, q );
		if ( t < 0 ) return false;
		point = o + d * t;
		return true;
	}

	static Vec3 ClosestPointOnTriangle( Vec3 p, Vec3 a, Vec3 b, Vec3 c )
	{
		var ab = b - a; var ac = c - a; var ap = p - a;
		float d1 = Vec3.Dot( ab, ap );
		float d2 = Vec3.Dot( ac, ap );
		if ( d1 <= 0 && d2 <= 0 ) return a;
		var bp = p - b;
		float d3 = Vec3.Dot( ab, bp );
		float d4 = Vec3.Dot( ac, bp );
		if ( d3 >= 0 && d4 <= d3 ) return b;
		float vc = d1 * d4 - d3 * d2;
		if ( vc <= 0 && d1 >= 0 && d3 <= 0 )
		{
			float v = d1 / ( d1 - d3 );
			return a + ab * v;
		}
		var cp = p - c;
		float d5 = Vec3.Dot( ab, cp );
		float d6 = Vec3.Dot( ac, cp );
		if ( d6 >= 0 && d5 <= d6 ) return c;
		float vb = d5 * d2 - d1 * d6;
		if ( vb <= 0 && d2 >= 0 && d6 <= 0 )
		{
			float w = d2 / ( d2 - d6 );
			return a + ac * w;
		}
		float va = d3 * d6 - d5 * d4;
		if ( va <= 0 && ( d4 - d3 ) >= 0 && ( d5 - d6 ) >= 0 )
		{
			float w = ( d4 - d3 ) / ( ( d4 - d3 ) + ( d5 - d6 ) );
			return b + ( c - b ) * w;
		}
		float denom = 1f / ( va + vb + vc );
		float v2 = vb * denom;
		float w2 = vc * denom;
		return a + ab * v2 + ac * w2;
	}

	public static Result Wrap( PolyMesh source, PolyMesh target, Options options=null )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		if ( target is null ) throw new ArgumentNullException( nameof( target ) );
		options ??= new Options();
		var outMesh = new PolyMesh();
		outMesh.Positions.AddRange( source.Positions );
		if ( source.Skin is not null )
		{
			var w = new SkinWeights( source.Skin.Count );
			for ( int i = 0; i < source.Skin.Count; i++ ) w[ i ] = source.Skin[ i ];
			outMesh.Skin = w;
		}
		outMesh.Faces.AddRange( source.Faces );
		if ( source.VertexColors is not null ) outMesh.VertexColors = (Vec4[])source.VertexColors.Clone();
		int moved = 0;
		for ( int i = 0; i < source.Positions.Count; i++ )
		{
			var p = source.Positions[ i ];
			float best = float.MaxValue; Vec3 bestPt = p;
			// nearest vertex on target as proxy
			foreach ( var tp in target.Positions )
			{
				float d = ( tp - p ).LengthSquared;
				if ( d < best ) { best = d; bestPt = tp; }
			}
			if ( MathF.Sqrt( best ) <= options.MaxDistance )
			{
				outMesh.Positions[ i ] = bestPt;
				moved++;
			}
		}
		return new Result( outMesh, moved );
	}
}
