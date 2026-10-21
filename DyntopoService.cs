using System;
using Effigy;

namespace Marionette.Desktop;

public static class DyntopoService
{
	public sealed class Options
	{
		public Vec3 Center { get; set; }
		public float Radius { get; set; } = 1f;
		public float MaxEdgeLength { get; set; } = 0.1f;
		public int MaxIterations { get; set; } = 4;
	}

	// Foundation: subdivide edges longer than threshold within brush radius.
	// Full dyntopo (collapse, sculpt strokes) is a follow-up.
	public enum BrushMode { Subdivide, Inflate, Flatten, Smooth }

	public sealed class SculptOptions
	{
		public Vec3 Center { get; set; }
		public float Radius { get; set; } = 1f;
		public BrushMode Mode { get; set; } = BrushMode.Inflate;
		public float Strength { get; set; } = 0.1f;
		public Vec3 Direction { get; set; } = new Vec3( 0, 0, 1 );
	}

	public static PolyMesh Sculpt( PolyMesh source, SculptOptions options )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		if ( options is null ) throw new ArgumentNullException( nameof( options ) );
		if ( options.Radius <= 0 ) throw new ArgumentException( "radius must be positive" );

		var outMesh = new PolyMesh();
		outMesh.Positions.AddRange( source.Positions );
		outMesh.Faces.AddRange( source.Faces );
		outMesh.Skin = source.Skin;
		outMesh.VertexColors = source.VertexColors;

		for ( int i = 0; i < source.Positions.Count; i++ )
		{
			float d = ( source.Positions[ i ] - options.Center ).Length;
			if ( d > options.Radius ) continue;
			float t = 1f - d / options.Radius;
			switch ( options.Mode )
			{
				case BrushMode.Inflate:
					outMesh.Positions[ i ] = source.Positions[ i ] + options.Direction * ( options.Strength * t );
					break;
				case BrushMode.Flatten:
					{
						float target = options.Center.y; // flatten to plane Y
						outMesh.Positions[ i ] = new Vec3( source.Positions[ i ].x, source.Positions[ i ].y + ( target - source.Positions[ i ].y ) * ( options.Strength * t ), source.Positions[ i ].z );
						break;
					}
				case BrushMode.Smooth:
					{
						// move toward average of neighbors
						Vec3 sum = new Vec3( 0, 0, 0 ); int c = 0;
						for ( int j = 0; j < source.Positions.Count; j++ )
						{
							if ( ( source.Positions[ j ] - source.Positions[ i ] ).Length < options.Radius * 0.5f && j != i )
							{
								sum = sum + source.Positions[ j ]; c++;
							}
						}
						if ( c > 0 )
						{
							var avg = sum / c;
							outMesh.Positions[ i ] = source.Positions[ i ] + ( avg - source.Positions[ i ] ) * ( options.Strength * t );
						}
						break;
					}
			}
		}
		return outMesh;
	}

	public static PolyMesh Subdivide( PolyMesh source, Options options )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		if ( options is null ) throw new ArgumentNullException( nameof( options ) );
		if ( options.Radius <= 0 ) throw new ArgumentException( "radius must be positive" );

		var m = new PolyMesh();
		m.Positions.AddRange( source.Positions );
		m.Faces.AddRange( source.Faces );
		m.Skin = source.Skin;
		m.VertexColors = source.VertexColors;

		for ( int iter = 0; iter < options.MaxIterations; iter++ )
		{
			bool changed = false;
			// find an edge to split
			int splitFace = -1, splitEdge = -1;
			for ( int fi = 0; fi < m.Faces.Count; fi++ )
			{
				var f = m.Faces[ fi ];
				for ( int e = 0; e < f.Count; e++ )
				{
					int a = f.Indices[ e ];
					int b = f.Indices[ ( e + 1 ) % f.Count ];
					float len = ( m.Positions[ b ] - m.Positions[ a ] ).Length;
					if ( len <= options.MaxEdgeLength ) continue;
					// midpoint within brush?
					var mid = ( m.Positions[ a ] + m.Positions[ b ] ) * 0.5f;
					if ( ( mid - options.Center ).Length > options.Radius ) continue;
					splitFace = fi; splitEdge = e; break;
				}
				if ( splitFace >= 0 ) break;
			}
			if ( splitFace < 0 ) break;
			// split edge in that face: insert midpoint vertex, split face into two
			var face = m.Faces[ splitFace ];
			int ea = face.Indices[ splitEdge ];
			int eb = face.Indices[ ( splitEdge + 1 ) % face.Count ];
			int newIdx = m.Positions.Count;
			m.Positions.Add( ( m.Positions[ ea ] + m.Positions[ eb ] ) * 0.5f );
			// rebuild face: replace edge ea-eb with ea-newIdx-eb
			var newIndices = new int[ face.Count + 1 ];
			var newUvs = new Vec2[ face.Count + 1 ];
			int k = 0;
			for ( int c = 0; c < face.Count; c++ )
			{
				newIndices[ k ] = face.Indices[ c ];
				newUvs[ k ] = face.UVs[ c ];
				k++;
				if ( c == splitEdge )
				{
					newIndices[ k ] = newIdx;
					newUvs[ k ] = ( face.UVs[ c ] + face.UVs[ ( c + 1 ) % face.Count ] ) * 0.5f;
					k++;
				}
			}
			m.Faces[ splitFace ] = new Face( newIndices, newUvs, face.Material );
			changed = true;
			if ( !changed ) break;
		}
		return m;
	}
}
