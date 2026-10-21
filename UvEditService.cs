using System;
using Effigy;

namespace Marionette.Desktop;

public static class UvEditService
{
	public sealed class Options
	{
		/// <summary>Indices of vertices to pin (don't move).</summary>
		public int[] Pinned { get; set; } = Array.Empty<int>();
		public int Iterations { get; set; } = 5;
		public float Strength { get; set; } = 0.5f;
	}

	public static PolyMesh Relax( PolyMesh source, Options options = null )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		options ??= new Options();
		if ( options.Iterations < 0 ) throw new ArgumentException( "iterations must be >= 0" );

		var outMesh = new PolyMesh();
		outMesh.Positions.AddRange( source.Positions );
		outMesh.Faces.AddRange( source.Faces );
		outMesh.Skin = source.Skin;
		outMesh.VertexColors = source.VertexColors;
		// UVs are per-face corner; relax by averaging neighboring corner UVs per vertex.
		// Build per-vertex list of (face, corner) references.
		var outFaces = new Face[ source.Faces.Count ];
		for ( int fi = 0; fi < source.Faces.Count; fi++ )
			outFaces[ fi ] = new Face( (int[])source.Faces[ fi ].Indices.Clone(), (Vec2[])source.Faces[ fi ].UVs.Clone(), source.Faces[ fi ].Material );

		var pinned = new bool[ source.Positions.Count ];
		foreach ( var p in options.Pinned ) if ( p >= 0 && p < pinned.Length ) pinned[ p ] = true;

		for ( int it = 0; it < options.Iterations; it++ )
		{
			// accumulate neighbor UV sums per vertex
			var sumX = new float[ source.Positions.Count ];
			var sumY = new float[ source.Positions.Count ];
			var cnt = new int[ source.Positions.Count ];
			for ( int fi = 0; fi < outFaces.Length; fi++ )
			{
				var f = outFaces[ fi ];
				for ( int c = 0; c < f.Count; c++ )
				{
					int v = f.Indices[ c ];
					sumX[ v ] += f.UVs[ c ].x;
					sumY[ v ] += f.UVs[ c ].y;
					cnt[ v ]++;
					// neighbor prev/next corners
					int prev = ( c + f.Count - 1 ) % f.Count;
					int next = ( c + 1 ) % f.Count;
					int vp = f.Indices[ prev ];
					int vn = f.Indices[ next ];
					sumX[ v ] += f.UVs[ prev ].x + f.UVs[ next ].x;
					sumY[ v ] += f.UVs[ prev ].y + f.UVs[ next ].y;
					cnt[ v ] += 2;
					// (avoid unused)
					_ = vp; _ = vn;
				}
			}
			// apply
			for ( int fi = 0; fi < outFaces.Length; fi++ )
			{
				var f = outFaces[ fi ];
				for ( int c = 0; c < f.Count; c++ )
				{
					int v = f.Indices[ c ];
					if ( pinned[ v ] ) continue;
					if ( cnt[ v ] == 0 ) continue;
					float ax = sumX[ v ] / cnt[ v ];
					float ay = sumY[ v ] / cnt[ v ];
					f.UVs[ c ] = new Vec2(
						f.UVs[ c ].x + ( ax - f.UVs[ c ].x ) * options.Strength,
						f.UVs[ c ].y + ( ay - f.UVs[ c ].y ) * options.Strength );
				}
			}
		}
		outMesh.Faces.Clear();
		foreach ( var f in outFaces ) outMesh.Faces.Add( f );
		return outMesh;
	}
}
