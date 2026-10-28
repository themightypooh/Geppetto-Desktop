using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Rasterizes Linework drawings into heightfield <see cref="BrushAlpha"/> stamps, and resolves
/// the alpha specs a sculpt stroke accepts (<c>proc:</c> procedurals, <c>linework:</c> drawings).
///
/// HEIGHTFIELD, NOT VDM. A <see cref="BrushAlpha"/> is a greyscale image: the brush kernel
/// multiplies its strength by the sampled value and pushes along the surface normal, so
/// everything it makes is a bump. It can never make an overhang — that is what the vector
/// stamps in <c>VdmStamp.cs</c> are for, and this file deliberately never builds a vector
/// field. If a stamp needs sideways motion it belongs over there, not here.
///
/// WHAT RENDERS. Closed profiles (from <see cref="LineworkReader.Profiles"/>) fill solid at
/// value 1, minus their own hole rings. Open strokes draw as lines: width from the stroke's
/// <c>Size</c> in drawing units (0 or missing falls back to 2% of the drawing span), value
/// from <c>Opacity</c> times the per-point pressure (missing pressures read as 1), with a
/// one-texel edge ramp. A line crossing a fill never erases it — values only ever take the
/// max. Everything is normalized to the bounds of what actually renders, with a 2% margin,
/// so the stamp's edges are empty rather than clipped. Mirrored profiles from a symmetric
/// document come along for free, because <see cref="LineworkReader.Profiles"/> emits them.
///
/// DETERMINISM. No Random anywhere: the same drawing rasterizes to the same stamp on
/// another machine. Row 0 is the drawing's top, matching <see cref="BrushAlpha.Sample"/>'s
/// "down from its top" convention, so a stamp sampled at (u, v) reads the drawing point
/// it looks like it should.
/// </summary>
public static class LineworkAlpha
{
	/// <summary>True when the spec names an alpha this resolver understands: a procedural
	/// stamp (<c>proc:name</c> or a bare procedural name) or a <c>linework:</c> drawing.</summary>
	public static bool IsAlphaSpec( string spec )
	{
		var s = ( spec ?? "" ).Trim();
		if ( s.Length == 0 ) return false;
		if ( s.StartsWith( "linework:", StringComparison.OrdinalIgnoreCase ) ) return true;
		return BrushAlpha.IsProcedural( s );
	}

	/// <summary>Resolves an alpha spec to a stamp. False + error when the spec names a
	/// procedural or drawing that does not resolve; false + "not an alpha spec" for anything
	/// else (the caller then falls back to the PNG path). Never throws on bad input.</summary>
	public static bool TryResolveSpec( string spec, out BrushAlpha alpha, out string error, int size = 256 )
	{
		alpha = null;
		error = null;
		var s = ( spec ?? "" ).Trim();
		if ( s.StartsWith( "linework:", StringComparison.OrdinalIgnoreCase ) )
		{
			var rest = s["linework:".Length..].Trim();
			string layer = null;
			var hash = rest.IndexOf( '#' );
			if ( hash >= 0 )
			{
				layer = rest[( hash + 1 )..].Trim();
				rest = rest[..hash].Trim();
				if ( layer.Length == 0 ) layer = null;
			}
			if ( rest.Length == 0 )
			{
				error = "linework: is empty: give a .linework path or drawing id after 'linework:'.";
				return false;
			}
			return TryLoad( rest, out alpha, out error, size, layer );
		}
		if ( BrushAlpha.IsProcedural( s ) )
		{
			alpha = BrushAlpha.FromProcedural( s );
			if ( alpha is null )
			{
				error = $"procedural alpha '{s}' is not a known stamp.";
				return false;
			}
			return true;
		}
		error = $"not an alpha spec: '{spec}'. Give 'proc:<name>' or 'linework:<path-or-id>'.";
		return false;
	}

	/// <summary>Loads a drawing (file path first, then a drawing id under
	/// ~/.local/share/linework/drawings/, per <see cref="LineworkReader.TryLoad"/>) and
	/// rasterizes it. False when the drawing cannot be loaded, or when it renders nothing
	/// (no visible closed profiles or open strokes) — an all-zero stamp would sculpt
	/// nothing, so that fails loudly instead of silently doing nothing.</summary>
	public static bool TryLoad( string pathOrId, out BrushAlpha alpha, out string error, int size = 256, string layerFilter = null )
	{
		alpha = null;
		error = null;
		if ( !LineworkReader.TryLoad( pathOrId, out var doc, out _, out error ) ) return false;
		alpha = FromDocument( doc, size, layerFilter );
		var max = 0f;
		foreach ( var v in alpha.Values ) if ( v > max ) max = v;
		if ( max <= 0f )
		{
			error = $"drawing '{pathOrId}' renders nothing as an alpha: no visible closed profiles or open strokes"
				+ ( layerFilter is null ? "." : $" on layer '{layerFilter}'." );
			alpha = null;
			return false;
		}
		return true;
	}

	/// <summary>One closed profile as a stamp: 1 inside the outer ring, 0 inside its holes
	/// and outside it. A profile with fewer than 3 outer points encloses nothing and yields
	/// an all-zero stamp.</summary>
	public static BrushAlpha FromProfile( LineworkProfile profile, int size = 256 )
	{
		var profiles = profile is null ? new List<LineworkProfile>() : new List<LineworkProfile> { profile };
		return Rasterize( profiles, new List<LineworkStroke>(), size );
	}

	/// <summary>Every visible layer (or the one named layer, by name or id) of a document
	/// as one stamp: closed profiles filled, open strokes drawn over them.</summary>
	public static BrushAlpha FromDocument( LineworkDocument doc, int size = 256, string layerFilter = null )
	{
		var profiles = doc is null ? new List<LineworkProfile>() : LineworkReader.Profiles( doc, layerFilter );
		var opens = new List<LineworkStroke>();
		if ( doc is not null )
		{
			foreach ( var layer in doc.Layers )
			{
				if ( layerFilter is not null )
				{
					if ( !layer.Name.Equals( layerFilter, StringComparison.OrdinalIgnoreCase )
						&& !layer.Id.Equals( layerFilter, StringComparison.OrdinalIgnoreCase ) ) continue;
				}
				else if ( !layer.Visible ) continue;
				foreach ( var st in layer.Strokes )
					if ( !st.Closed && st.Points.Count >= 2 ) opens.Add( st );
			}
		}
		return Rasterize( profiles, opens, size );
	}

	static BrushAlpha Rasterize( List<LineworkProfile> profiles, List<LineworkStroke> opens, int size )
	{
		size = Math.Max( 8, size );
		var alpha = new BrushAlpha { Values = new float[size * size], Width = size, Height = size };
		float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
		void Include( Vec2 p )
		{
			if ( p.x < minX ) minX = p.x;
			if ( p.x > maxX ) maxX = p.x;
			if ( p.y < minY ) minY = p.y;
			if ( p.y > maxY ) maxY = p.y;
		}
		foreach ( var pr in profiles )
		{
			foreach ( var p in pr.Outer ) Include( p );
			foreach ( var h in pr.Holes ) foreach ( var p in h ) Include( p );
		}
		foreach ( var st in opens ) foreach ( var p in st.Points ) Include( p );
		if ( minX == float.MaxValue ) return alpha;
		var span = Math.Max( maxX - minX, maxY - minY );
		if ( span <= 1e-9f ) return alpha;
		var margin = 0.02f * span;
		minX -= margin; maxX += margin; minY -= margin; maxY += margin;
		var spanX = maxX - minX;
		var spanY = maxY - minY;
		var texel = Math.Max( spanX, spanY ) / size;
		for ( var row = 0; row < size; row++ )
		for ( var col = 0; col < size; col++ )
		{
			var x = minX + ( col + 0.5f ) / size * spanX;
			var y = maxY - ( row + 0.5f ) / size * spanY;
			var v = 0f;
			foreach ( var pr in profiles )
			{
				if ( !Inside( pr.Outer, x, y ) ) continue;
				var inHole = false;
				foreach ( var h in pr.Holes ) if ( Inside( h, x, y ) ) { inHole = true; break; }
				if ( !inHole ) v = Math.Max( v, 1f );
			}
			alpha.Values[row * size + col] = v;
		}
		foreach ( var st in opens )
		{
			var halfW = st.Size > 0f ? st.Size / 2f : 0.02f * span;
			var op = st.Opacity;
			for ( var i = 0; i + 1 < st.Points.Count; i++ )
			{
				var a = st.Points[i];
				var b = st.Points[i + 1];
				var pa = PressureAt( st, i );
				var pb = PressureAt( st, i + 1 );
				var loX = Math.Min( a.x, b.x ) - halfW - texel;
				var hiX = Math.Max( a.x, b.x ) + halfW + texel;
				var loY = Math.Min( a.y, b.y ) - halfW - texel;
				var hiY = Math.Max( a.y, b.y ) + halfW + texel;
				var c0 = Math.Max( 0, (int)MathF.Floor( ( loX - minX ) / spanX * size ) );
				var c1 = Math.Min( size - 1, (int)MathF.Ceiling( ( hiX - minX ) / spanX * size ) );
				var r1 = Math.Max( 0, (int)MathF.Floor( ( maxY - hiY ) / spanY * size ) );
				var r0 = Math.Min( size - 1, (int)MathF.Ceiling( ( maxY - loY ) / spanY * size ) );
				for ( var row = r1; row <= r0; row++ )
				for ( var col = c0; col <= c1; col++ )
				{
					var x = minX + ( col + 0.5f ) / size * spanX;
					var y = maxY - ( row + 0.5f ) / size * spanY;
					var (d, t) = SegDist( a, b, x, y );
					if ( d > halfW + texel ) continue;
					var ramp = d <= halfW ? 1f : ( halfW + texel - d ) / texel;
					var val = op * ( pa + ( pb - pa ) * t ) * ramp;
					var idx = row * size + col;
					if ( val > alpha.Values[idx] ) alpha.Values[idx] = val;
				}
			}
		}
		return alpha;
	}

	static float PressureAt( LineworkStroke st, int i )
	{
		if ( st.Pressures is null || st.Pressures.Count <= i ) return 1f;
		return Math.Clamp( st.Pressures[i], 0f, 1f );
	}

	/// <summary>Even-odd point-in-polygon: a point exactly on an edge reads either way by
	/// rounding, so tests sample away from edges rather than on them.</summary>
	static bool Inside( List<Vec2> ring, float x, float y )
	{
		var inside = false;
		for ( int i = 0, j = ring.Count - 1; i < ring.Count; j = i++ )
		{
			var xi = ring[i].x; var yi = ring[i].y;
			var xj = ring[j].x; var yj = ring[j].y;
			if ( ( yi > y ) != ( yj > y ) )
			{
				var xint = xi + ( y - yi ) / ( yj - yi ) * ( xj - xi );
				if ( x < xint ) inside = !inside;
			}
		}
		return inside;
	}

	static (float Dist, float T) SegDist( Vec2 a, Vec2 b, float x, float y )
	{
		var dx = b.x - a.x;
		var dy = b.y - a.y;
		var len2 = dx * dx + dy * dy;
		if ( len2 <= 1e-12f )
		{
			var ex = x - a.x;
			var ey = y - a.y;
			return ( MathF.Sqrt( ex * ex + ey * ey ), 0f );
		}
		var t = ( ( x - a.x ) * dx + ( y - a.y ) * dy ) / len2;
		t = Math.Clamp( t, 0f, 1f );
		var px = a.x + t * dx;
		var py = a.y + t * dy;
		var ddx = x - px;
		var ddy = y - py;
		return ( MathF.Sqrt( ddx * ddx + ddy * ddy ), t );
	}
}
