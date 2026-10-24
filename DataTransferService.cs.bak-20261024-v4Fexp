using System;
using System.Collections.Generic;
using Effigy;

namespace Marionette.Desktop;

public static class DataTransferService
{
	public enum Channel { SkinWeights, UVs }

	public sealed class Options
	{
		public Channel[] Channels { get; set; } = new[] { Channel.SkinWeights, Channel.UVs };
		public float MaxDistance { get; set; } = float.MaxValue;
	}

	public sealed class Result
	{
		public PolyMesh Mesh { get; }
		public int TransferredVerts { get; }
		public int SkippedVerts { get; }
		public Result( PolyMesh m, int t, int s ) { Mesh = m; TransferredVerts = t; SkippedVerts = s; }
	}

	public static Result Transfer( PolyMesh source, PolyMesh target, Options options = null )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		if ( target is null ) throw new ArgumentNullException( nameof( target ) );
		options ??= new Options();
		var wantSkin = Array.IndexOf( options.Channels, Channel.SkinWeights ) >= 0;
		var wantUV = Array.IndexOf( options.Channels, Channel.UVs ) >= 0;

		var outMesh = new PolyMesh();
		outMesh.Positions.AddRange( target.Positions );
		outMesh.Faces.AddRange( target.Faces );
		if ( target.Skin is not null )
		{
			var w = new SkinWeights( target.Skin.Count );
			for ( int i = 0; i < target.Skin.Count; i++ ) w[ i ] = target.Skin[ i ];
			outMesh.Skin = w;
		}
		else if ( source.Skin is not null )
		{
			outMesh.Skin = new SkinWeights( target.Positions.Count );
		}

		int transferred = 0, skipped = 0;
		var nearestSource = new int[ target.Positions.Count ];
		for ( int i = 0; i < nearestSource.Length; i++ ) nearestSource[ i ] = -1;
		for ( int ti = 0; ti < target.Positions.Count; ti++ )
		{
			// nearest source vertex
			float best = float.MaxValue; int bestSi = -1;
			for ( int si = 0; si < source.Positions.Count; si++ )
			{
				float d = ( source.Positions[ si ] - target.Positions[ ti ] ).LengthSquared;
				if ( d < best ) { best = d; bestSi = si; }
			}
			if ( bestSi < 0 || MathF.Sqrt( best ) > options.MaxDistance ) { skipped++; continue; }
			nearestSource[ ti ] = bestSi;

			if ( wantSkin && source.Skin is not null && bestSi < source.Skin.Count && outMesh.Skin is not null )
				outMesh.Skin[ ti ] = source.Skin[ bestSi ];

			if ( wantUV )
			{
				// UVs transferred in a post-pass using nearestSource mapping.
			}
			transferred++;
		}
		if ( wantUV && source.Faces.Count > 0 )
		{
			// Build source vertex -> (face, corner) first occurrence.
			var srcLookup = new Dictionary<int, (int face, int corner)>();
			for ( int fi = 0; fi < source.Faces.Count; fi++ )
			{
				var f = source.Faces[ fi ];
				for ( int c = 0; c < f.Count; c++ )
				{
					int v = f.Indices[ c ];
					if ( !srcLookup.ContainsKey( v ) ) srcLookup[ v ] = ( fi, c );
				}
			}
			for ( int fi = 0; fi < outMesh.Faces.Count; fi++ )
			{
				var f = outMesh.Faces[ fi ];
				for ( int c = 0; c < f.Count; c++ )
				{
					int v = f.Indices[ c ];
					int sv = v < nearestSource.Length ? nearestSource[ v ] : -1;
					if ( sv < 0 ) continue;
					if ( !srcLookup.TryGetValue( sv, out var loc ) ) continue;
					var srcFace = source.Faces[ loc.face ];
					if ( loc.corner < srcFace.UVs.Length )
						f.UVs[ c ] = srcFace.UVs[ loc.corner ];
				}
			}
		}
		return new Result( outMesh, transferred, skipped );
	}
}
