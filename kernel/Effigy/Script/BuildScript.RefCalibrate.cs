using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy.Render;

namespace Effigy;

/// <summary>
/// refimage calibrate / fit / frame. Calibration solves Height + Offset
/// from two picture points and their world points; it never sets Depth
/// (the picture's stand-off from the model is the modeller's choice, not
/// something two surface points determine).
/// </summary>
public static partial class BuildScript
{
	static bool CalibrateVerb( PartStudio studio, List<string> args, string baseDir, Result r )
	{
		if ( args.Count == 0 )
			return Fail( r, "refimage calibrate needs a view: refimage calibrate VIEW px,py=wx,wy,wz px,py=wx,wy,wz | refimage calibrate VIEW scalebar px,py px,py = length" );
		var view = RefImageMath.NormaliseView( args[0] );
		var rest = args.Skip( 1 ).ToList();
		var pairs = Pairs( rest.Where( t => t.IndexOf( '=' ) > 0 && !t.Contains( ',' ) ) );
		var tag = pairs.GetValueOrDefault( "tag", "" );
		var image = FindImage( studio, view, tag, r, out var why );
		if ( image is null ) return Fail( r, why );
		if ( !LoadPixels( image.Path, r, out var w, out var h, out _, out _ ) ) return false;

		if ( rest.Any( t => t.ToLowerInvariant() == "scalebar" || t.ToLowerInvariant().StartsWith( "scalebar=" ) ) )
		{
			var pxTokens = rest.Where( t => t.Contains( ',' ) && !t.Contains( '=' ) ).ToList();
			string lenText = null;
			foreach ( var t in rest )
			{
				if ( t.ToLowerInvariant().StartsWith( "scalebar=" ) ) lenText = t[(t.IndexOf( '=' ) + 1)..];
				else if ( t == "=" ) continue;
			}
			if ( lenText is null && rest.Count > 0 && !rest[^1].Contains( ',' ) && !rest[^1].Contains( '=' ) )
				lenText = rest[^1];
			if ( pxTokens.Count != 2 || lenText is null )
				return Fail( r, "refimage calibrate scalebar needs two pixels and a length: refimage calibrate side scalebar 100,200 300,200 = 10" );
			if ( !ParsePx( pxTokens[0], r, out var px1, out var py1 ) ) return false;
			if ( !ParsePx( pxTokens[1], r, out var px2, out var py2 ) ) return false;
			var length = Number( lenText );
			var pixDist = MathF.Sqrt( (px2 - px1) * (px2 - px1) + (py2 - py1) * (py2 - py1) );
			if ( pixDist < 10f )
				return Fail( r, $"scalebar points are only {pixDist:0.#} px apart — too close to calibrate reliably. Use a longer bar." );
			if ( !(length > 0) ) return Fail( r, "scalebar length must be above 0." );
			image.Height = length / pixDist * h;
			image.Calibrated = true;
			r.Log.Add( $"refimage calibrate {view}: scale {length / pixDist:0.####} u/px, height={image.Height:0.###} (offset unchanged)." );
			return true;
		}

		var corr = rest.Where( t => t.Contains( '=' ) && t.Contains( ',' ) ).ToList();
		if ( corr.Count != 2 )
			return Fail( r, "refimage calibrate needs exactly two correspondences: refimage calibrate VIEW px,py=wx,wy,wz px,py=wx,wy,wz" );
		var points = new List<(float px, float py, Vec3 world)>();
		foreach ( var token in corr )
		{
			var eq = token.IndexOf( '=' );
			if ( !ParsePx( token[..eq], r, out var px, out var py ) ) return false;
			if ( !ParseWorld( token[(eq + 1)..], r, out var world ) ) return false;
			points.Add( (px, py, world) );
		}
		var (right, up, _) = RefImageMath.Axes( view );
		var u1 = Vec3.Dot( points[0].world, right ); var v1 = Vec3.Dot( points[0].world, up );
		var u2 = Vec3.Dot( points[1].world, right ); var v2 = Vec3.Dot( points[1].world, up );
		var dpx = points[1].px - points[0].px;
		var dpyImg = points[1].py - points[0].py;
		// Picture up is -py, so the pixel delta in (right, up) is (dpx, -dpy).
		var pixDist2 = MathF.Sqrt( dpx * dpx + dpyImg * dpyImg );
		var worldDist = MathF.Sqrt( (u2 - u1) * (u2 - u1) + (v2 - v1) * (v2 - v1) );
		if ( pixDist2 < 10f )
			return Fail( r, $"calibration points are only {pixDist2:0.#} px apart — too close to calibrate reliably. Pick points far apart in the picture." );
		if ( worldDist <= 0f )
			return Fail( r, "calibration world points coincide in this view's plane — pick points that differ along the picture." );
		var pixUnitX = dpx / pixDist2; var pixUnitY = -dpyImg / pixDist2;
		var worldUnitX = (u2 - u1) / worldDist; var worldUnitY = (v2 - v1) / worldDist;
		var cos = pixUnitX * worldUnitX + pixUnitY * worldUnitY;
		if ( cos < 0 )
			return Fail( r, "refimage calibrate: the picture is MIRRORED against the world points (pixel direction runs opposite to world direction). Flip the picture or swap the points — a mirrored calibration is refused, not silently accepted." );
		var angleDeg = MathF.Acos( Math.Clamp( cos, -1f, 1f ) ) * 180f / MathF.PI;
		if ( angleDeg > 3f )
			return Fail( r, $"refimage calibrate: picture and world directions differ by {angleDeg:0.#} degrees. This calibration only solves scale + translation for an axis-aligned picture, not rotation — rotate the picture square to the view first." );
		var scale = worldDist / pixDist2;
		var newHeight = scale * h;
		var pmx = (points[0].px + points[1].px) / 2f;
		var pmy = (points[0].py + points[1].py) / 2f;
		var um = (u1 + u2) / 2f; var vm = (v1 + v2) / 2f;
		var picW = newHeight * (w / (float)h);
		var ox = um - ((pmx + 0.5f) * scale - picW / 2f);
		var oy = vm - (newHeight / 2f - (pmy + 0.5f) * scale);
		var predU = dpx * scale; var predV = -dpyImg * scale;
		var residual = MathF.Sqrt( (predU - (u2 - u1)) * (predU - (u2 - u1)) + (predV - (v2 - v1)) * (predV - (v2 - v1)) );
		image.Height = newHeight;
		image.Offset = new Vec2( ox, oy );
		image.Calibrated = true;
		r.Log.Add( $"refimage calibrate {view}: scale {scale:0.####} u/px, height={newHeight:0.###}, offset={ox:0.###},{oy:0.###}, residual {residual:0.###} u (angle {angleDeg:0.##} deg). Depth unchanged ({image.Depth:0.###})." );
		if ( image.Kind == "photo" )
			r.Log.Add( "refimage calibrate: the source is a photo — this calibration is approximate at the edges; centre measurements are the most reliable." );
		return true;
	}

	static bool FitVerb( PartStudio studio, List<string> args, string baseDir, Result r )
	{
		if ( args.Count < 2 )
			return Fail( r, "refimage fit needs a view and a body: refimage fit VIEW body=Name [tag=]" );
		var view = RefImageMath.NormaliseView( args[0] );
		var pairs = Pairs( args.Skip( 1 ) );
		if ( !pairs.TryGetValue( "body", out var bodyName ) )
			return Fail( r, "refimage fit needs body=Name." );
		var tag = pairs.GetValueOrDefault( "tag", "" );
		var image = FindImage( studio, view, tag, r, out var why );
		if ( image is null ) return Fail( r, why );
		var body = studio.Bodies.FirstOrDefault( b => string.Equals( b.Name, bodyName, StringComparison.OrdinalIgnoreCase ) );
		if ( body is null ) return Fail( r, $"refimage fit: no body '{bodyName}'." );
		if ( body.Mesh is null || body.Mesh.Positions.Count == 0 )
			return Fail( r, $"refimage fit: body '{bodyName}' has no mesh." );
		if ( !LoadPixels( image.Path, r, out var w, out var h, out _, out _ ) ) return false;
		var (right, up, _) = RefImageMath.Axes( view );
		var minU = float.MaxValue; var maxU = float.MinValue; var minV = float.MaxValue; var maxV = float.MinValue;
		foreach ( var p in body.Mesh.Positions )
		{
			var u = Vec3.Dot( p, right ); var v = Vec3.Dot( p, up );
			if ( u < minU ) minU = u;
			if ( u > maxU ) maxU = u;
			if ( v < minV ) minV = v;
			if ( v > maxV ) maxV = v;
		}
		var upExtent = maxV - minV;
		if ( upExtent <= 0 ) return Fail( r, $"refimage fit: body '{bodyName}' is flat along this view's up axis." );
		image.Height = upExtent;
		image.Offset = new Vec2( (minU + maxU) / 2f, (minV + maxV) / 2f );
		image.Calibrated = false;
		var picW = upExtent * (w / (float)h);
		var rightExtent = maxU - minU;
		r.Log.Add( $"refimage fit {view} to {bodyName}: height={upExtent:0.###}, offset={image.Offset.x:0.###},{image.Offset.y:0.###}. FIRST PASS ONLY — the picture width ({picW:0.###}) vs body width ({rightExtent:0.###}){(MathF.Abs( picW - rightExtent ) / Math.Max( rightExtent, 1e-6f ) > 0.1f ? " differ by over 10%: fit by height alone does not prove scale." : " agree within 10%.")} Calibrate from two landmarks for a real scale." );
		return true;
	}

	static bool FrameVerb( PartStudio studio, List<string> args, string baseDir, Result r )
	{
		if ( args.Count == 0 )
			return Fail( r, "refimage frame needs a view: refimage frame VIEW [tag=]" );
		var view = RefImageMath.NormaliseView( args[0] );
		var pairs = Pairs( args.Skip( 1 ) );
		var tag = pairs.GetValueOrDefault( "tag", "" );
		var image = FindImage( studio, view, tag, r, out var why );
		if ( image is null ) return Fail( r, why );
		if ( !LoadPixels( image.Path, r, out var w, out var h, out _, out _ ) ) return false;
		var corners = RefImageMath.Corners( image, w, h );
		r.Log.Add( $"refimage frame {view} {Path.GetFileName( image.Path )}: height={image.Height:0.###}, offset={image.Offset.x:0.###},{image.Offset.y:0.###}, depth={image.Depth:0.###}, corners BL({corners[0].x:0.###},{corners[0].y:0.###},{corners[0].z:0.###}) BR({corners[1].x:0.###},{corners[1].y:0.###},{corners[1].z:0.###}) TR({corners[2].x:0.###},{corners[2].y:0.###},{corners[2].z:0.###}) TL({corners[3].x:0.###},{corners[3].y:0.###},{corners[3].z:0.###})." );
		return true;
	}
}
