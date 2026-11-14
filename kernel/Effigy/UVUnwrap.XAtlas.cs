using System;
using System.Collections.Generic;

namespace Effigy;

public static partial class UVUnwrap
{
	/// <summary>
	/// Optional native chart-and-pack unwrapper (xatlas, installed by the app). Takes positions
	/// (3 floats per vertex), polygon corner indices, corners per polygon and a gutter in texels;
	/// returns one 0..1 UV per corner index plus the chart count, or null on failure. Left null, every unwrap uses the kernel's
	/// own charting and flatten.
	/// </summary>
	public static Func<float[], uint[], byte[], int, (float[] Uv, int Charts)?> XAtlas;

	/// <summary>How many unwraps the native path wrote (checks read it).</summary>
	public static int XAtlasRuns;

	/// <summary>
	/// The automatic unwrap through <see cref="XAtlas"/>: used only when the caller asked for the
	/// default (no seam set passed, angle mode, conformal flatten) and the mesh carries no marked
	/// seams, so a user's own cuts always go through the kernel path that honours them. Returns
	/// null - and leaves the mesh untouched - whenever the native result can't be written as one UV
	/// per face corner, so the caller falls back to the old path.
	/// </summary>
	static UnwrapReport TryXAtlas( PolyMesh mesh, float margin )
	{
		var hook = XAtlas;
		if ( hook is null || mesh.Faces.Count == 0 || mesh.Positions.Count < 3 )
			return null;
		if ( mesh.LiveSeams().Count > 0 )
			return null;

		var pos = new float[mesh.Positions.Count * 3];
		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var p = mesh.Positions[i];
			pos[i * 3] = p.x; pos[i * 3 + 1] = p.y; pos[i * 3 + 2] = p.z;
		}
		var idx = new List<uint>();
		var sizes = new byte[mesh.Faces.Count];
		var owner = new List<(int Face, int Corner)>();
		for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			var f = mesh.Faces[fi];
			if ( f.Count < 3 || f.Count > 255 )
				return null;
			sizes[fi] = (byte)f.Count;
			for ( var c = 0; c < f.Count; c++ )
			{
				idx.Add( (uint)f.Indices[c] );
				owner.Add( (fi, c) );
			}
		}
		if ( idx.Count < 3 )
			return null;

		// The margin is a fraction of the square; xatlas wants texels on its ~1024 estimate.
		var padding = Math.Clamp( (int)MathF.Round( Math.Clamp( margin, 0f, 0.2f ) * 1024f ), 1, 32 );
		(float[] Uv, int Charts)? res;
		try { res = hook( pos, idx.ToArray(), sizes, padding ); }
		catch { return null; }
		if ( res is null || res.Value.Uv is null || res.Value.Uv.Length != idx.Count * 2 )
			return null;
		var uv = res.Value.Uv;

		// One UV per corner; a corner left out or outside the square means the native result can't
		// be stored, so the old path runs instead.
		var outUv = new Vec2[mesh.Faces.Count][];
		var seen = new bool[mesh.Faces.Count][];
		for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			outUv[fi] = new Vec2[mesh.Faces[fi].Count];
			seen[fi] = new bool[mesh.Faces[fi].Count];
		}
		for ( var k = 0; k < owner.Count; k++ )
		{
			float u = uv[k * 2], v = uv[k * 2 + 1];
			if ( !(u >= 0f && u <= 1f && v >= 0f && v <= 1f) )
				return null;
			var (fi, c) = owner[k];
			var w = new Vec2( u, v );
			if ( seen[fi][c] )
			{
				if ( (outUv[fi][c] - w).LengthSquared > 1e-10f )
					return null;
			}
			else { outUv[fi][c] = w; seen[fi][c] = true; }
		}
		double area3 = 0, areaUv = 0;
		for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			for ( var c = 0; c < seen[fi].Length; c++ )
				if ( !seen[fi][c] ) return null; // a corner no triangle used: degenerate face
			var f = mesh.Faces[fi];
			foreach ( var (a, b, c) in NormalBake.FaceTriangles( mesh, fi ) )
			{
				area3 += 0.5 * Vec3.Cross( mesh.Positions[f.Indices[b]] - mesh.Positions[f.Indices[a]],
					mesh.Positions[f.Indices[c]] - mesh.Positions[f.Indices[a]] ).Length;
				var e1 = outUv[fi][b] - outUv[fi][a];
				var e2 = outUv[fi][c] - outUv[fi][a];
				areaUv += 0.5 * Math.Abs( e1.x * e2.y - e1.y * e2.x );
			}
		}
		for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
			mesh.Faces[fi].UVs = outUv[fi];
		mesh.InvalidateVertexCache();
		XAtlasRuns++;

		var scale = areaUv > 1e-12 ? (float)Math.Sqrt( area3 / areaUv ) : 0f;
		var (mean, max) = UVIslands.Distortion( mesh );
		return new UnwrapReport( res.Value.Charts, mesh.Faces.Count, 0, scale, mean, max );
	}
}
