using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Render;

/// <summary>
/// Picture-to-world math for reference images, mirrored from the desktop
/// viewer's ReferenceImagePass.Axes()/Corners() into the kernel so headless
/// scripts can use it. The desktop pass is the authority: side = Right X,
/// Up Z, Facing -Y; front = Right -Y, Up Z, Facing X; top = Right -Y,
/// Up X, Facing Z. A picture's centre in its plane is facing * -Depth +
/// right * Offset.x + up * Offset.y; its width is Height * aspect.
/// Pixel (0,0) is the picture's top-left; pixel centres are used throughout.
/// </summary>
public static class RefImageMath
{
	public static (Vec3 Right, Vec3 Up, Vec3 Facing) Axes( string view ) => view switch
	{
		"side" => (new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ), new Vec3( 0, -1, 0 )),
		"top" => (new Vec3( 0, -1, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 )),
		_ => (new Vec3( 0, -1, 0 ), new Vec3( 0, 0, 1 ), new Vec3( 1, 0, 0 )),
	};

	public static string NormaliseView( string view ) =>
		view?.Trim().ToLowerInvariant() is "right" or "side" ? "side"
		: view?.Trim().ToLowerInvariant() is "top" ? "top" : "front";

	/// <summary>World units per picture pixel, from the picture's height.</summary>
	public static float WorldPerPixel( ReferenceImage image, int imgHeight ) =>
		imgHeight > 0 ? MathF.Max( image.Height, 0.01f ) / imgHeight : 0f;

	/// <summary>Picture pixel (fractional allowed) to (right, up) plane coordinates.</summary>
	public static (float u, float v) PixelToPlane( ReferenceImage image, int imgW, int imgH, float px, float py )
	{
		var w = image.Height * (imgW / (float)Math.Max( 1, imgH ));
		var h = image.Height;
		var u = (px + 0.5f) / Math.Max( 1, imgW ) * w - w / 2f + image.Offset.x;
		var v = h / 2f - (py + 0.5f) / Math.Max( 1, imgH ) * h + image.Offset.y;
		return (u, v);
	}

	/// <summary>Picture-corner coordinate (cx, cy) in corner units to plane coordinates.</summary>
	public static (float u, float v) CornerToPlane( ReferenceImage image, int imgW, int imgH, float cx, float cy )
	{
		var w = image.Height * (imgW / (float)Math.Max( 1, imgH ));
		var h = image.Height;
		return (cx / Math.Max( 1, imgW ) * w - w / 2f + image.Offset.x,
			h / 2f - cy / Math.Max( 1, imgH ) * h + image.Offset.y);
	}

	public static Vec3 PlaneToWorld( ReferenceImage image, float u, float v )
	{
		var (right, up, facing) = Axes( image.View );
		return facing * -image.Depth + right * u + up * v;
	}

	public static Vec3 PixelToWorld( ReferenceImage image, int imgW, int imgH, float px, float py )
	{
		var (u, v) = PixelToPlane( image, imgW, imgH, px, py );
		return PlaneToWorld( image, u, v );
	}

	/// <summary>World point to (right, up) plane coordinates for this picture's view.</summary>
	public static (float u, float v) WorldToPlane( ReferenceImage image, Vec3 world )
	{
		var (right, up, _) = Axes( image.View );
		return (Vec3.Dot( world, right ), Vec3.Dot( world, up ));
	}

	public static (float px, float py) WorldToPixel( ReferenceImage image, int imgW, int imgH, Vec3 world )
	{
		var (u, v) = WorldToPlane( image, world );
		var w = image.Height * (imgW / (float)Math.Max( 1, imgH ));
		var h = image.Height;
		var px = (u - image.Offset.x + w / 2f) / w * imgW - 0.5f;
		var py = (image.Offset.y + h / 2f - v) / h * imgH - 0.5f;
		return (px, py);
	}

	public static Vec3[] Corners( ReferenceImage image, int imgW, int imgH )
	{
		var (right, up, facing) = Axes( image.View );
		var w = image.Height * (imgW / (float)Math.Max( 1, imgH ));
		var h = image.Height;
		var c = facing * -image.Depth + right * image.Offset.x + up * image.Offset.y;
		return new[]
		{
			c - right * w / 2f - up * h / 2f, c + right * w / 2f - up * h / 2f,
			c + right * w / 2f + up * h / 2f, c - right * w / 2f + up * h / 2f,
		};
	}

	/// <summary>Sketch plane and sketch-plane point for a (u, v) picture point.</summary>
	public static (string Plane, Vec2 Point, float Offset) SketchForPlanePoint( string view, float u, float v, float depth )
	{
		// Sketch planes: Top (XY) origin (0,0,offset) normal +Z; Front (XZ)
		// origin (0,-offset,0) normal -Y; Right (YZ) origin (offset,0,0)
		// normal +X. Offset = -Depth puts the sketch on the picture plane
		// in all three views; the point mapping below is the inverse of each
		// plane's ToWorld.
		return NormaliseView( view ) switch
		{
			"side" => ("Front", new Vec2( u, v ), -depth),
			"top" => ("Top", new Vec2( v, -u ), -depth),
			_ => ("Right", new Vec2( -u, v ), -depth),
		};
	}
}

/// <summary>One connected true region of a mask, with its bounds.</summary>
public sealed class MaskBlob
{
	public List<int> Pixels = new();
	public int MinX, MaxX, MinY, MaxY;
	public int Area => Pixels.Count;
}

public static class RefImageTools
{
	/// <summary>4-connected blobs of a mask, largest first.</summary>
	public static List<MaskBlob> LabelBlobs( bool[] mask, int w, int h )
	{
		var label = new int[w * h];
		var blobs = new List<MaskBlob>();
		var stack = new Stack<int>();
		for ( var start = 0; start < mask.Length; start++ )
		{
			if ( !mask[start] || label[start] != 0 ) continue;
			var blob = new MaskBlob { MinX = w, MaxX = -1, MinY = h, MaxY = -1 };
			var id = blobs.Count + 1;
			label[start] = id;
			stack.Push( start );
			while ( stack.Count > 0 )
			{
				var i = stack.Pop();
				blob.Pixels.Add( i );
				var x = i % w; var y = i / w;
				if ( x < blob.MinX ) blob.MinX = x;
				if ( x > blob.MaxX ) blob.MaxX = x;
				if ( y < blob.MinY ) blob.MinY = y;
				if ( y > blob.MaxY ) blob.MaxY = y;
				void Try( int j ) { if ( mask[j] && label[j] == 0 ) { label[j] = id; stack.Push( j ); } }
				if ( x > 0 ) Try( i - 1 );
				if ( x + 1 < w ) Try( i + 1 );
				if ( y > 0 ) Try( i - w );
				if ( y + 1 < h ) Try( i + w );
			}
			blobs.Add( blob );
		}
		blobs.Sort( (a, b) => b.Area.CompareTo( a.Area ) );
		return blobs;
	}

	/// <summary>Background components fully enclosed by subject (holes), counted by
	/// flood-filling the outside from the border of the whole mask.</summary>
	public static int CountHoles( bool[] mask, int w, int h )
	{
		var outside = new bool[w * h];
		var stack = new Stack<int>();
		void Push( int x, int y )
		{
			var i = y * w + x;
			if ( !mask[i] && !outside[i] ) { outside[i] = true; stack.Push( i ); }
		}
		for ( var x = 0; x < w; x++ ) { Push( x, 0 ); Push( x, h - 1 ); }
		for ( var y = 0; y < h; y++ ) { Push( 0, y ); Push( w - 1, y ); }
		while ( stack.Count > 0 )
		{
			var i = stack.Pop();
			var x = i % w; var y = i / w;
			if ( x > 0 ) Push( x - 1, y );
			if ( x + 1 < w ) Push( x + 1, y );
			if ( y > 0 ) Push( x, y - 1 );
			if ( y + 1 < h ) Push( x, y + 1 );
		}
		var seen = new bool[w * h];
		var holes = 0;
		for ( var s = 0; s < mask.Length; s++ )
		{
			if ( mask[s] || outside[s] || seen[s] ) continue;
			holes++;
			seen[s] = true;
			stack.Push( s );
			while ( stack.Count > 0 )
			{
				var i = stack.Pop();
				var x = i % w; var y = i / w;
				void Try( int j ) { if ( !mask[j] && !outside[j] && !seen[j] ) { seen[j] = true; stack.Push( j ); } }
				if ( x > 0 ) Try( i - 1 );
				if ( x + 1 < w ) Try( i + 1 );
				if ( y > 0 ) Try( i - w );
				if ( y + 1 < h ) Try( i + w );
			}
		}
		return holes;
	}

	/// <summary>Marching-squares loops of one blob's mask, as corner-coordinate
	/// polylines. The largest loop is the outer contour; the rest are holes.</summary>
	public static List<List<(float x, float y)>> ContourLoops( bool[] mask, int w, int h, MaskBlob blob )
	{
		// Edge segments between grid corners, keyed by their two endpoints.
		var segs = new List<((float x, float y) a, (float x, float y) b)>();
		// Boundary cells sit one OUTSIDE the blob's own pixel box on the
		// top/left and at MaxX/MaxY on the right/bottom: a cell fully inside
		// the blob has no edge. At() already returns false out of bounds,
		// so the outer ring is safe even for a blob touching the picture edge.
		for ( var y = blob.MinY - 1; y <= blob.MaxY; y++ )
			for ( var x = blob.MinX - 1; x <= blob.MaxX; x++ )
			{
				bool At( int px, int py ) => px >= 0 && px < w && py >= 0 && py < h && mask[py * w + px];
				var tl = At( x, y ); var tr = At( x + 1, y ); var br = At( x + 1, y + 1 ); var bl = At( x, y + 1 );
				var top = ((float)x + 0.5f, (float)y); var right = ((float)x + 1, (float)y + 0.5f);
				var bottom = ((float)x + 0.5f, (float)y + 1); var left = ((float)x, (float)y + 0.5f);
				var code = (tl ? 1 : 0) | (tr ? 2 : 0) | (br ? 4 : 0) | (bl ? 8 : 0);
				switch ( code )
				{
					case 1: case 14: segs.Add( (left, top) ); break;
					case 2: case 13: segs.Add( (top, right) ); break;
					case 3: case 12: segs.Add( (left, right) ); break;
					case 4: case 11: segs.Add( (right, bottom) ); break;
					case 5: segs.Add( (left, top) ); segs.Add( (right, bottom) ); break;
					case 6: case 9: segs.Add( (top, bottom) ); break;
					case 7: case 8: segs.Add( (bottom, left) ); break;
					case 10: segs.Add( (top, right) ); segs.Add( (bottom, left) ); break;
				}
			}
		var byPoint = new Dictionary<(float, float), List<int>>();
		for ( var i = 0; i < segs.Count; i++ )
		{
			if ( !byPoint.TryGetValue( segs[i].a, out var la ) ) byPoint[segs[i].a] = la = new List<int>();
			la.Add( i );
			if ( !byPoint.TryGetValue( segs[i].b, out var lb ) ) byPoint[segs[i].b] = lb = new List<int>();
			lb.Add( i );
		}
		var used = new bool[segs.Count];
		var loops = new List<List<(float x, float y)>>();
		for ( var s = 0; s < segs.Count; s++ )
		{
			if ( used[s] ) continue;
			var loop = new List<(float x, float y)>();
			var cur = s;
			var at = segs[s].a;
			loop.Add( at );
			while ( true )
			{
				used[cur] = true;
				var next = segs[cur].a.Equals( at ) ? segs[cur].b : segs[cur].a;
				if ( next.Equals( loop[0] ) ) break;
				loop.Add( next );
				at = next;
				var candidates = byPoint[at].Where( j => !used[j] ).ToList();
				if ( candidates.Count == 0 ) break;
				cur = candidates[0];
			}
			if ( loop.Count >= 3 ) loops.Add( loop );
		}
		loops.Sort( (a, b) => MathF.Abs( LoopArea( b ) ).CompareTo( MathF.Abs( LoopArea( a ) ) ) );
		return loops;
	}

	static float LoopArea( List<(float x, float y)> loop )
	{
		var sum = 0f;
		for ( var i = 0; i < loop.Count; i++ )
		{
			var a = loop[i]; var b = loop[(i + 1) % loop.Count];
			sum += a.x * b.y - b.x * a.y;
		}
		return sum / 2f;
	}

	/// <summary>Douglas-Peucker simplification of an open polyline, in pixels.</summary>
	public static List<(float x, float y)> Simplify( List<(float x, float y)> pts, float tol )
	{
		if ( pts.Count <= 2 || tol <= 0 ) return new List<(float, float)>( pts );
		var keep = new bool[pts.Count];
		keep[0] = true; keep[pts.Count - 1] = true;
		var stack = new Stack<(int, int)>();
		stack.Push( (0, pts.Count - 1) );
		while ( stack.Count > 0 )
		{
			var (a, b) = stack.Pop();
			var (ax, ay) = pts[a]; var (bx, by) = pts[b];
			var dx = bx - ax; var dy = by - ay;
			var len2 = dx * dx + dy * dy;
			var worst = -1; var worstD = tol;
			for ( var i = a + 1; i < b; i++ )
			{
				var (px, py) = pts[i];
				var t = len2 > 0 ? Math.Clamp( ((px - ax) * dx + (py - ay) * dy) / len2, 0f, 1f ) : 0f;
				var ex = ax + t * dx; var ey = ay + t * dy;
				var d = MathF.Sqrt( (px - ex) * (px - ex) + (py - ey) * (py - ey) );
				if ( d > worstD ) { worstD = d; worst = i; }
			}
			if ( worst >= 0 ) { keep[worst] = true; stack.Push( (a, worst) ); stack.Push( (worst, b) ); }
		}
		var outp = new List<(float x, float y)>();
		for ( var i = 0; i < pts.Count; i++ ) if ( keep[i] ) outp.Add( pts[i] );
		return outp;
	}

	/// <summary>Downsample a full-resolution mask to outW x outH by block majority.</summary>
	public static bool[] Downsample( bool[] mask, int w, int h, int outW, int outH )
	{
		var outp = new bool[outW * outH];
		for ( var y = 0; y < outH; y++ )
			for ( var x = 0; x < outW; x++ )
			{
				var x0 = x * w / outW; var x1 = Math.Max( x0 + 1, (x + 1) * w / outW );
				var y0 = y * h / outH; var y1 = Math.Max( y0 + 1, (y + 1) * h / outH );
				var yes = 0; var total = 0;
				for ( var yy = y0; yy < y1; yy++ )
					for ( var xx = x0; xx < x1; xx++ ) { total++; if ( mask[yy * w + xx] ) yes++; }
				outp[y * outW + x] = total > 0 && yes * 2 >= total;
			}
		return outp;
	}

	/// <summary>Model silhouette at the picture's own true scale and offset, as a
	/// mask over the picture frame (outW x outH). Software-rasterised so it
	/// uses the picture's axes, not the render view's (front differs by a
	/// mirror between the two). Depth is dot(p, Facing); larger wins.</summary>
	public static bool[] ModelMaskAtTrueScale( IReadOnlyList<Body> bodies, ReferenceImage image, int imgW, int imgH, int outW, int outH )
	{
		var mask = new bool[outW * outH];
		var depth = new float[outW * outH];
		Array.Fill( depth, float.MinValue );
		var (right, up, facing) = RefImageMath.Axes( image.View );
		var picW = image.Height * (imgW / (float)Math.Max( 1, imgH ));
		var picH = image.Height;
		(float px, float py) Project( Vec3 p )
		{
			var u = Vec3.Dot( p, right ); var v = Vec3.Dot( p, up );
			return ((u - image.Offset.x + picW / 2f) / picW * outW - 0.5f,
				(image.Offset.y + picH / 2f - v) / picH * outH - 0.5f);
		}
		foreach ( var body in bodies ?? Array.Empty<Body>() )
		{
			if ( body?.Mesh is null || !body.Visible || body.Mesh.Positions.Count == 0 ) continue;
			var mesh = body.Mesh;
			foreach ( var face in mesh.Faces )
			{
				if ( face.Indices.Length < 3 ) continue;
				for ( var t = 1; t + 1 < face.Indices.Length; t++ )
				{
					var p0 = mesh.Positions[face.Indices[0]];
					var p1 = mesh.Positions[face.Indices[t]];
					var p2 = mesh.Positions[face.Indices[t + 1]];
					var (x0, y0) = Project( p0 ); var (x1, y1) = Project( p1 ); var (x2, y2) = Project( p2 );
					var d0 = Vec3.Dot( p0, facing ); var d1 = Vec3.Dot( p1, facing ); var d2 = Vec3.Dot( p2, facing );
					var minX = Math.Max( 0, (int)MathF.Floor( MathF.Min( x0, MathF.Min( x1, x2 ) ) ) );
					var maxX = Math.Min( outW - 1, (int)MathF.Ceiling( MathF.Max( x0, MathF.Max( x1, x2 ) ) ) );
					var minY = Math.Max( 0, (int)MathF.Floor( MathF.Min( y0, MathF.Min( y1, y2 ) ) ) );
					var maxY = Math.Min( outH - 1, (int)MathF.Ceiling( MathF.Max( y0, MathF.Max( y1, y2 ) ) ) );
					var denom = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
					if ( MathF.Abs( denom ) < 1e-9f ) continue;
					for ( var y = minY; y <= maxY; y++ )
						for ( var x = minX; x <= maxX; x++ )
						{
							var a = ((y1 - y2) * (x - x2) + (x2 - x1) * (y - y2)) / denom;
							var b = ((y2 - y0) * (x - x2) + (x0 - x2) * (y - y2)) / denom;
							var c = 1f - a - b;
							if ( a < -0.001f || b < -0.001f || c < -0.001f ) continue;
							var d = a * d0 + b * d1 + c * d2;
							var i = y * outW + x;
							if ( d > depth[i] ) { depth[i] = d; mask[i] = true; }
						}
				}
			}
		}
		return mask;
	}

	/// <summary>Width and depth of a mesh slice at up-coordinate = station, in
	/// (right, facing) coordinates. Null when nothing crosses the plane.</summary>
	public static (float Width, float Depth, int Segments)? SliceAt( IReadOnlyList<Body> bodies, ReferenceImage image, float station )
	{
		var (right, up, facing) = RefImageMath.Axes( image.View );
		var minR = float.MaxValue; var maxR = float.MinValue;
		var minF = float.MaxValue; var maxF = float.MinValue;
		var segs = 0;
		foreach ( var body in bodies ?? Array.Empty<Body>() )
		{
			if ( body?.Mesh is null || !body.Visible ) continue;
			var mesh = body.Mesh;
			foreach ( var face in mesh.Faces )
			{
				if ( face.Indices.Length < 3 ) continue;
				for ( var t = 1; t + 1 < face.Indices.Length; t++ )
				{
					var pts = new[] { mesh.Positions[face.Indices[0]], mesh.Positions[face.Indices[t]], mesh.Positions[face.Indices[t + 1]] };
					var s = new[] { Vec3.Dot( pts[0], up ) - station, Vec3.Dot( pts[1], up ) - station, Vec3.Dot( pts[2], up ) - station };
					var crossings = new List<Vec3>();
					for ( var e = 0; e < 3; e++ )
					{
						var a = pts[e]; var b = pts[(e + 1) % 3];
						var sa = s[e]; var sb = s[(e + 1) % 3];
						if ( sa == 0f ) crossings.Add( a );
						else if ( (sa < 0f) != (sb < 0f) ) crossings.Add( a + (b - a) * (-sa / (sb - sa)) );
					}
					if ( crossings.Count >= 2 )
					{
						segs++;
						foreach ( var p in crossings )
						{
							var r = Vec3.Dot( p, right ); var f = Vec3.Dot( p, facing );
							if ( r < minR ) minR = r;
							if ( r > maxR ) maxR = r;
							if ( f < minF ) minF = f;
							if ( f > maxF ) maxF = f;
						}
					}
				}
			}
		}
		return segs == 0 ? null : (maxR - minR, maxF - minF, segs);
	}
}
