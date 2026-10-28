using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy.Render;

namespace Effigy;

/// <summary>
/// Reference-driven verbs: stored picture landmarks, proportion reports,
/// multi-view consistency, true-scale overlay, picture-to-sketch tracing,
/// and section-vs-reference checks. Everything here derives new curves or
/// measurements from a picture the modeller is looking at. Nothing here
/// imports a mesh, a scan, or geometry from another artist's model.
/// </summary>
public static partial class BuildScript
{
	public const string RefToolsUsage =
		"refmark VIEW NAME px,py | refmark list | refmark clear [VIEW]; " +
		"proportion [A B] [model=X,Y] [template=path] [tolerance=0.1]; " +
		"refcheck [tolerance=0.5] [scaletol=0.05]; " +
		"refoverlay VIEW [out.png] [ref=path] [tag=] [size=1024]; " +
		"trace VIEW region=x1,y1,x2,y2 [out=Name] [minarea=100] [simplify=2] [kind=poly|splineclosed] [threshold=] [tag=]; " +
		"refsection VIEW station= [body=Name] [tag=]; " +
		"refstep NAME state=map|built|checked | refstep list | refstep clear";

	static ReferenceImage FindImage( PartStudio studio, string view, string tag, Result r, out string why )
	{
		why = null;
		view = RefImageMath.NormaliseView( view );
		tag ??= "";
		var image = studio.ReferenceImages.FirstOrDefault( i => i.View == view && (i.Tag ?? "") == tag );
		if ( image is null )
		{
			why = tag.Length > 0
				? $"refimage {view}: no picture with tag '{tag}' is up. Put one up first: refimage {view} path.png tag={tag}."
				: $"refimage {view}: no picture is up. Put one up first: refimage {view} path.png.";
			return null;
		}
		return image;
	}

	static bool LoadPixels( string path, Result r, out int w, out int h, out int[] rgb, out byte[] alpha )
	{
		w = h = 0; rgb = null; alpha = null;
		if ( !File.Exists( path ) )
			return Fail( r, $"no picture at {path}." );
		if ( !path.EndsWith( ".png", StringComparison.OrdinalIgnoreCase ) )
			return Fail( r, $"headless reference work reads PNG only; {Path.GetFileName( path )} needs the desktop viewer, which converts other formats. Give a PNG." );
		try
		{
			(w, h, rgb, alpha) = Raster.LoadPng( path );
			return true;
		}
		catch ( Exception e )
		{
			return Fail( r, $"could not read {path}: {e.Message}" );
		}
	}

	static bool ParsePx( string text, Result r, out float px, out float py )
	{
		px = py = 0;
		var bits = text.Split( ',' );
		if ( bits.Length != 2 )
			return Fail( r, $"'{text}' is not a pixel px,py." );
		try { px = Number( bits[0] ); py = Number( bits[1] ); return true; }
		catch { return Fail( r, $"'{text}' is not a pixel px,py." ); }
	}

	static bool ParseWorld( string text, Result r, out Vec3 world )
	{
		world = Vec3.Zero;
		var bits = text.Split( ',' );
		if ( bits.Length != 3 )
			return Fail( r, $"'{text}' is not a world point wx,wy,wz." );
		try { world = new Vec3( Number( bits[0] ), Number( bits[1] ), Number( bits[2] ) ); return true; }
		catch { return Fail( r, $"'{text}' is not a world point wx,wy,wz." ); }
	}

	// Marks store pixels plus the picture's pixel size, and the world point
	// is recomputed from the CURRENT picture placement every time it is
	// read — so calibrating after marking moves the marks with the
	// picture instead of leaving stale world points behind.
	static Vec3 MarkWorld( PartStudio studio, RefMark mark )
	{
		var image = studio.ReferenceImages.FirstOrDefault( i => i.View == mark.View && (i.Tag ?? "") == (mark.Tag ?? "") )
			?? studio.ReferenceImages.FirstOrDefault( i => i.View == mark.View );
		if ( image is null ) return Vec3.Zero;
		return RefImageMath.PixelToWorld( image, Math.Max( 1, mark.ImgW ), Math.Max( 1, mark.ImgH ), mark.Px, mark.Py );
	}

	// --- refmark -----------------------------------------------------------------

	static bool RefMarkVerb( PartStudio studio, List<string> args, string baseDir, Result r )
	{
		if ( args.Count == 0 )
			return Fail( r, "refmark needs: refmark VIEW NAME px,py | refmark list | refmark clear [VIEW]" );
		if ( args[0].ToLowerInvariant() == "list" )
		{
			if ( studio.RefMarks.Count == 0 ) { r.Log.Add( "refmark: no picture landmarks stored." ); return true; }
			foreach ( var m in studio.RefMarks )
			{
				var wp = MarkWorld( studio, m );
				r.Log.Add( $"refmark {m.View} {m.Name} px={m.Px:0.#},{m.Py:0.#} world=({wp.x:0.###}, {wp.y:0.###}, {wp.z:0.###}){(string.IsNullOrEmpty( m.Tag ) ? "" : $" tag={m.Tag}")}" );
			}
			return true;
		}
		if ( args[0].ToLowerInvariant() == "clear" )
		{
			var only = args.Count > 1 ? RefImageMath.NormaliseView( args[1] ) : null;
			var removed = studio.RefMarks.RemoveAll( m => only is null || m.View == only );
			r.Log.Add( removed == 0 ? "refmark clear: no landmarks stored." : $"refmark clear: removed {removed} landmark(s)." );
			return true;
		}
		if ( args.Count < 3 )
			return Fail( r, "refmark needs: refmark VIEW NAME px,py" );
		var view = RefImageMath.NormaliseView( args[0] );
		var name = args[1];
		if ( name.Contains( '=' ) )
			return Fail( r, $"refmark name '{name}' must not contain '='." );
		if ( !ParsePx( args[2], r, out var px, out var py ) ) return false;
		var pairs = Pairs( args.Skip( 3 ) );
		var tag = pairs.GetValueOrDefault( "tag", "" );
		var image = FindImage( studio, view, tag, r, out var why );
		if ( image is null ) return Fail( r, why );
		if ( !LoadPixels( image.Path, r, out var w, out var h, out _, out _ ) ) return false;
		if ( px < 0 || px >= w || py < 0 || py >= h )
			return Fail( r, $"refmark {name}: pixel {px:0.#},{py:0.#} is outside the {w}x{h} picture." );
		var world = RefImageMath.PixelToWorld( image, w, h, px, py );
		studio.RefMarks.RemoveAll( m => m.View == view && m.Name == name && (m.Tag ?? "") == tag );
		studio.RefMarks.Add( new RefMark { View = view, Name = name, Px = px, Py = py, Tag = tag, ImgW = w, ImgH = h } );
		r.Log.Add( $"refmark {view} {name} px={px:0.#},{py:0.#} world=({world.x:0.###}, {world.y:0.###}, {world.z:0.###})" );
		if ( image.Kind == "photo" )
			r.Log.Add( "refmark: the source is a photo — edge measurements are approximate; centre measurements are the most reliable." );
		return true;
	}

	// --- proportion ---------------------------------------------------------------

	static bool ProportionVerb( PartStudio studio, List<string> args, string baseDir, Result r )
	{
		var pairs = Pairs( args.Where( a => a.Contains( '=' ) ) );
		var names = args.Where( a => !a.Contains( '=' ) ).ToList();
		var tol = pairs.TryGetValue( "tolerance", out var tolText ) ? Number( tolText ) : 0.1f;
		if ( pairs.TryGetValue( "template", out var templatePath ) )
			return ProportionTemplate( studio, Resolve( baseDir, templatePath ), tol, r );
		if ( names.Count == 2 )
		{
			var a = studio.RefMarks.FirstOrDefault( m => m.Name == names[0] );
			var b = studio.RefMarks.FirstOrDefault( m => m.Name == names[1] );
			if ( a is null ) return Fail( r, $"proportion: no landmark '{names[0]}'. Store it first: refmark VIEW {names[0]} px,py." );
			if ( b is null ) return Fail( r, $"proportion: no landmark '{names[1]}'. Store it first: refmark VIEW {names[1]} px,py." );
			var wa = MarkWorld( studio, a ); var wb = MarkWorld( studio, b );
			var dist = (wb - wa).Length;
			r.Log.Add( $"proportion {a.Name}–{b.Name}: picture distance {dist:0.###} world units." );
			if ( pairs.TryGetValue( "model", out var modelPair ) )
			{
				var bits = modelPair.Split( ',' );
				if ( bits.Length != 2 ) return Fail( r, "proportion model= needs two model landmarks: model=Disc.top,Disc.bottom." );
				if ( !ModelMeasure.TryLandmark( studio, bits[0].Trim(), out var ma, out var whyA ) )
					return Fail( r, $"proportion: model landmark '{bits[0]}' did not resolve: {whyA}" );
				if ( !ModelMeasure.TryLandmark( studio, bits[1].Trim(), out var mb, out var whyB ) )
					return Fail( r, $"proportion: model landmark '{bits[1]}' did not resolve: {whyB}" );
				var mdist = (mb - ma).Length;
				var diff = mdist - dist;
				var pct = dist > 0 ? diff / dist * 100f : 0f;
				r.Log.Add( $"proportion {a.Name}–{b.Name}: model distance {mdist:0.###}, difference {diff:+0.###;-0.###} ({pct:+0.#;-0.#}%){(MathF.Abs( pct ) > tol * 100f ? " — OFF, beyond tolerance." : " — within tolerance.")}" );
			}
			return true;
		}
		if ( names.Count != 0 )
			return Fail( r, "proportion needs two landmark names, or no names for the full table: proportion [A B] [model=X,Y] [template=path]" );
		if ( studio.RefMarks.Count < 2 ) { r.Log.Add( "proportion: fewer than two landmarks stored, nothing to compare." ); return true; }
		r.Log.Add( "proportion: pairwise picture distances (world units):" );
		for ( var i = 0; i < studio.RefMarks.Count; i++ )
			for ( var j = i + 1; j < studio.RefMarks.Count; j++ )
			{
				var a = studio.RefMarks[i]; var b = studio.RefMarks[j];
				var d = (MarkWorld( studio, b ) - MarkWorld( studio, a )).Length;
				r.Log.Add( $"  {a.Name}–{b.Name}: {d:0.###}" );
			}
		return true;
	}

	static bool ProportionTemplate( PartStudio studio, string path, float tol, Result r )
	{
		if ( !File.Exists( path ) ) return Fail( r, $"proportion: no template at {path}." );
		string baseA = null, baseB = null;
		var rows = new List<(string A, string B, float Expected)>();
		foreach ( var raw in File.ReadAllLines( path ) )
		{
			var line = raw.Trim();
			if ( line.Length == 0 || line.StartsWith( '#' ) ) continue;
			var bits = line.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
			if ( bits.Length == 3 && bits[0] == "base" ) { baseA = bits[1]; baseB = bits[2]; continue; }
			if ( bits.Length != 3 ) return Fail( r, $"proportion template: line '{line}' is not 'base A B' or 'A B ratio'." );
			if ( !float.TryParse( bits[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var exp ) )
				return Fail( r, $"proportion template: '{bits[2]}' is not a ratio." );
			rows.Add( (bits[0], bits[1], exp) );
		}
		if ( baseA is null ) return Fail( r, "proportion template: the first line must be 'base A B'." );
		RefMark Find( string n ) => studio.RefMarks.FirstOrDefault( m => m.Name == n );
		if ( Find( baseA ) is null || Find( baseB ) is null )
			return Fail( r, $"proportion template: base landmarks '{baseA}'/'{baseB}' are not stored." );
		var baseDist = (MarkWorld( studio, Find( baseB ) ) - MarkWorld( studio, Find( baseA ) )).Length;
		if ( baseDist <= 0 ) return Fail( r, "proportion template: base landmarks coincide, ratios are undefined." );
		r.Log.Add( $"proportion template {Path.GetFileName( path )}: base {baseA}–{baseB} = {baseDist:0.###}." );
		var bad = 0;
		foreach ( var (a, b, expected) in rows )
		{
			if ( Find( a ) is null || Find( b ) is null ) { r.Log.Add( $"  {a}–{b}: landmark not stored, skipped." ); continue; }
			var measured = (MarkWorld( studio, Find( b ) ) - MarkWorld( studio, Find( a ) )).Length / baseDist;
			var diff = expected > 0 ? (measured - expected) / expected : 0f;
			var flag = MathF.Abs( diff ) > tol ? " — OFF" : "";
			if ( flag.Length > 0 ) bad++;
			r.Log.Add( $"  {a}–{b}: measured {measured:0.###} vs template {expected:0.###} ({diff * 100f:+0.#;-0.#}%){flag}." );
		}
		r.Log.Add( bad == 0 ? "proportion template: all ratios within tolerance." : $"proportion template: {bad} ratio(s) off, beyond tolerance {tol * 100f:0}%." );
		return true;
	}

	// --- refcheck -----------------------------------------------------------------

	static bool RefCheckVerb( PartStudio studio, List<string> args, Result r )
	{
		var pairs = Pairs( args );
		var tol = pairs.TryGetValue( "tolerance", out var t ) ? Number( t ) : 0.5f;
		var scaletol = pairs.TryGetValue( "scaletol", out var s ) ? Number( s ) : 0.05f;
		var bad = 0;
		// Scale agreement: world-per-pixel across views, using each picture's
		// own pixel height (re-read so a stale Height is not trusted blindly).
		var scales = new Dictionary<string, float>();
		foreach ( var image in studio.ReferenceImages )
		{
			if ( !File.Exists( image.Path ) || !image.Path.EndsWith( ".png", StringComparison.OrdinalIgnoreCase ) ) continue;
			try { var (_, h, _, _) = Raster.LoadPng( image.Path ); scales[image.View] = RefImageMath.WorldPerPixel( image, h ); }
			catch { /* a picture that will not load is reported by its own verbs */ }
		}
		if ( scales.Count >= 2 )
		{
			var min = scales.Values.Min(); var max = scales.Values.Max();
			var spread = min > 0 ? (max - min) / min : 0f;
			r.Log.Add( $"refcheck scale: world-per-pixel {string.Join( ", ", scales.Select( kv => $"{kv.Key}={kv.Value:0.####}" ) )} — spread {spread * 100f:0.#}%{(spread > scaletol ? " — OFF, pictures are not at one scale." : " — agree.")}" );
			if ( spread > scaletol ) bad++;
		}
		foreach ( var group in studio.RefMarks.GroupBy( m => m.Name ) )
		{
			if ( group.Count() < 2 ) continue;
			var pts = group.ToList();
			for ( var i = 0; i < pts.Count; i++ )
				for ( var j = i + 1; j < pts.Count; j++ )
				{
					var a = pts[i]; var b = pts[j];
					var wa = MarkWorld( studio, a ); var wb = MarkWorld( studio, b );
					// Shared axis: front/side share Z, side/top share X, front/top share Y.
					var (axis, da, db) = (a.View, b.View) switch
					{
						("front", "side") or ("side", "front") => ("Z", wa.z, wb.z),
						("side", "top") or ("top", "side") => ("X", wa.x, wb.x),
						("front", "top") or ("top", "front") => ("Y", wa.y, wb.y),
						_ => ("", 0f, 0f),
					};
					if ( axis.Length == 0 ) continue;
					var diff = MathF.Abs( da - db );
					var flag = diff > tol ? " — OFF" : "";
					if ( flag.Length > 0 ) bad++;
					r.Log.Add( $"refcheck {group.Key} {a.View}/{b.View} shared {axis}: {da:0.###} vs {db:0.###}, difference {diff:0.###}{(flag.Length > 0 ? $" — beyond tolerance {tol:0.###}." : ".")}" );
				}
		}
		r.Log.Add( bad == 0 ? "refcheck: PASS — views agree." : $"refcheck: FAIL — {bad} disagreement(s) above tolerance." );
		return true;
	}

	// --- overlay ------------------------------------------------------------------

	sealed class OverlayData
	{
		public bool[] RefMask, ModelMask;
		public int OutW, OutH;
		public float Overlap, Excess, Missing, ShiftU, ShiftV, RefArea, ModelArea;
		public string Report;
	}

	static bool BuildOverlay( PartStudio studio, ReferenceImage image, string refPath, int sizeCap, Result r, out OverlayData data, out string why )
	{
		data = null; why = null;
		if ( !LoadPixels( refPath, r, out var w, out var h, out var rgb, out var alpha ) ) { why = "picture could not be read."; return false; }
		var outH = Math.Min( h, Math.Max( 64, sizeCap ) );
		var outW = Math.Max( 1, (int)Math.Round( (double)w * outH / h ) );
		var raw = ReferenceMatch.ClassifyMask( w, h, rgb, alpha );
		var refMask = RefImageTools.Downsample( raw, w, h, outW, outH );
		var modelMask = RefImageTools.ModelMaskAtTrueScale( studio.Bodies, image, w, h, outW, outH );
		var both = 0; var either = 0; var excess = 0; var missing = 0;
		var refCx = 0.0; var refCy = 0.0; var modCx = 0.0; var modCy = 0.0; var refN = 0; var modN = 0;
		for ( var i = 0; i < refMask.Length; i++ )
		{
			if ( refMask[i] && modelMask[i] ) both++;
			if ( refMask[i] || modelMask[i] ) either++;
			if ( modelMask[i] && !refMask[i] ) excess++;
			if ( refMask[i] && !modelMask[i] ) missing++;
			if ( refMask[i] ) { refCx += i % outW; refCy += i / outW; refN++; }
			if ( modelMask[i] ) { modCx += i % outW; modCy += i / outW; modN++; }
		}
		var wpp = RefImageMath.WorldPerPixel( image, h ) * h / (float)outH;
		var shiftU = refN > 0 && modN > 0 ? (float)(modCx / modN - refCx / refN) * wpp : 0f;
		var shiftV = refN > 0 && modN > 0 ? (float)(refCy / refN - modCy / modN) * wpp : 0f;
		var sb = new System.Text.StringBuilder();
		sb.AppendLine( $"overlay {image.View}: true-scale overlap {(either == 0 ? 100f : both / (float)either * 100f):0}% (fitted match would hide size/shift errors; this one does not)." );
		sb.AppendLine( $"  reference area {refN * wpp * wpp:0.##} u^2, model area {modN * wpp * wpp:0.##} u^2, model-only {excess / (float)refMask.Length * 100f:0.#}%, drawing-only {missing / (float)refMask.Length * 100f:0.#}%." );
		sb.AppendLine( $"  centroid shift: right {shiftU:+0.###;-0.###} u, up {shiftV:+0.###;-0.###} u (0,0 is perfectly centred on the drawing)." );
		const int bands = 8;
		for ( var band = 0; band < bands; band++ )
		{
			var y0 = band * outH / bands; var y1 = (band + 1) * outH / bands;
			float RefWidth( bool[] m )
			{
				var mn = outW; var mx = -1;
				for ( var y = y0; y < y1; y++ )
					for ( var x = 0; x < outW; x++ )
						if ( m[y * outW + x] ) { if ( x < mn ) mn = x; if ( x > mx ) mx = x; }
				return mx < 0 ? 0f : (mx - mn + 1) * wpp;
			}
			var rw = RefWidth( refMask ); var mw = RefWidth( modelMask );
			sb.AppendLine( $"  band {band + 1}/{bands} (up): drawing width {rw:0.##} u, model width {mw:0.##} u, difference {mw - rw:+0.##;-0.##} u." );
		}
		data = new OverlayData
		{
			RefMask = refMask, ModelMask = modelMask, OutW = outW, OutH = outH,
			Overlap = either == 0 ? 1f : both / (float)either,
			Excess = excess / (float)refMask.Length, Missing = missing / (float)refMask.Length,
			ShiftU = shiftU, ShiftV = shiftV,
			RefArea = refN * wpp * wpp, ModelArea = modN * wpp * wpp, Report = sb.ToString().TrimEnd(),
		};
		return true;
	}

	static Raster OverlayRaster( string refPath, OverlayData data )
	{
		var (w, h, rgb, _) = Raster.LoadPng( refPath );
		var raster = new Raster( data.OutW, data.OutH, 0x121418 );
		for ( var y = 0; y < data.OutH; y++ )
			for ( var x = 0; x < data.OutW; x++ )
			{
				var sx = x * w / data.OutW; var sy = y * h / data.OutH;
				var baseCol = rgb[sy * w + sx];
				var i = y * data.OutW + x;
				if ( data.ModelMask[i] && !data.RefMask[i] )
					raster.Pixels[i] = Blend( baseCol, 0xf0a13a );
				else if ( data.ModelMask[i] )
					raster.Pixels[i] = Blend( baseCol, 0x6f7d8c );
				else if ( data.RefMask[i] )
					raster.Pixels[i] = Blend( baseCol, 0x5b93ff );
				else
					raster.Pixels[i] = baseCol;
			}
		raster.Text( 6, 6, $"true-scale overlap {data.Overlap * 100f:0}%  shift {data.ShiftU:+0.##;-0.##},{data.ShiftV:+0.##;-0.##} u", 0xe7e9ee, 2 );
		raster.Text( 6, 20, "orange: model only  blue: drawing only  grey: both", 0x9aa2ae, 1 );
		return raster;
	}

	static int Blend( int baseCol, int tint )
	{
		var r = (((baseCol >> 16) & 0xFF) + ((tint >> 16) & 0xFF)) / 2;
		var g = (((baseCol >> 8) & 0xFF) + ((tint >> 8) & 0xFF)) / 2;
		var b = ((baseCol & 0xFF) + (tint & 0xFF)) / 2;
		return (r << 16) | (g << 8) | b;
	}

	static bool RefOverlayVerb( PartStudio studio, List<string> args, string baseDir, Result r )
	{
		if ( args.Count == 0 )
			return Fail( r, "refoverlay needs a view: refoverlay VIEW [out.png] [ref=path] [tag=] [size=1024]" );
		var view = RefImageMath.NormaliseView( args[0] );
		var rest = args.Skip( 1 ).ToList();
		var outPath = rest.Count > 0 && !rest[0].Contains( '=' ) ? Resolve( baseDir, rest[0] ) : null;
		if ( outPath is not null ) rest.RemoveAt( 0 );
		var pairs = Pairs( rest );
		var tag = pairs.GetValueOrDefault( "tag", "" );
		var image = FindImage( studio, view, tag, r, out var why );
		if ( image is null ) return Fail( r, why );
		outPath ??= pairs.TryGetValue( "out", out var op ) ? Resolve( baseDir, op ) : null;
		var refPath = pairs.TryGetValue( "ref", out var rp ) ? Resolve( baseDir, rp ) : image.Path;
		var sizeCap = pairs.TryGetValue( "size", out var sz ) ? (int)Number( sz ) : 1024;
		if ( !BuildOverlay( studio, image, refPath, sizeCap, r, out var data, out var owhy ) )
			return Fail( r, owhy ?? "overlay failed." );
		r.Log.Add( data.Report );
		outPath ??= Path.Combine( baseDir, $"overlay-{view}.png" );
		Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( outPath ) ) ?? "." );
		OverlayRaster( refPath, data ).SavePng( outPath );
		var diffPath = outPath[..^Path.GetExtension( outPath ).Length] + "-diff" + Path.GetExtension( outPath );
		var diff = new Raster( data.OutW, data.OutH, 0x121418 );
		for ( var i = 0; i < data.RefMask.Length; i++ )
			diff.Pixels[i] = data.ModelMask[i] && data.RefMask[i] ? 0x6f7d8c : data.ModelMask[i] ? 0xf0a13a : data.RefMask[i] ? 0x5b93ff : 0x121418;
		diff.SavePng( diffPath );
		r.Written.Add( outPath ); r.Written.Add( diffPath );
		r.Log.Add( $"overlay: model on reference to {outPath}, mask diff to {diffPath}." );
		return true;
	}

	/// <summary>Shared by the render verb's refimage=1 branch.</summary>
	public static bool RenderOverlay( PartStudio studio, ReferenceImage image, string outPath, string baseDir, Result r )
	{
		if ( !BuildOverlay( studio, image, image.Path, 1024, r, out var data, out var why ) )
			return Fail( r, why ?? "overlay failed." );
		Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( outPath ) ) ?? "." );
		OverlayRaster( image.Path, data ).SavePng( outPath );
		r.Written.Add( outPath );
		r.Log.Add( data.Report );
		r.Log.Add( $"rendered {image.View} with reference to {outPath}" );
		return true;
	}

	// --- trace --------------------------------------------------------------------

	static bool TraceVerb( PartStudio studio, List<string> args, string baseDir, Result r )
	{
		if ( args.Count == 0 )
			return Fail( r, "trace needs a view: trace VIEW region=x1,y1,x2,y2 [out=Name] [minarea=100] [simplify=2] [kind=poly|splineclosed] [threshold=] [tag=]" );
		var view = RefImageMath.NormaliseView( args[0] );
		var pairs = Pairs( args.Skip( 1 ) );
		pairs.TryGetValue( "region", out var regionText );
		var tag = pairs.GetValueOrDefault( "tag", "" );
		var image = FindImage( studio, view, tag, r, out var why );
		if ( image is null ) return Fail( r, why );
		if ( !LoadPixels( image.Path, r, out var w, out var h, out var rgb, out var alpha ) ) return false;
		// No region means the whole picture.
		int rx0, ry0, rx1, ry1;
		if ( regionText is null ) { rx0 = 0; ry0 = 0; rx1 = w - 1; ry1 = h - 1; }
		else
		{
			var rb = regionText.Split( ',' );
			if ( rb.Length != 4 ) return Fail( r, "trace region is four pixel numbers: region=x1,y1,x2,y2." );
			try { rx0 = (int)Number( rb[0] ); ry0 = (int)Number( rb[1] ); rx1 = (int)Number( rb[2] ); ry1 = (int)Number( rb[3] ); }
			catch { return Fail( r, "trace region is four pixel numbers: region=x1,y1,x2,y2." ); }
		}
		rx0 = Math.Clamp( rx0, 0, w - 1 ); ry0 = Math.Clamp( ry0, 0, h - 1 );
		rx1 = Math.Clamp( rx1, 0, w - 1 ); ry1 = Math.Clamp( ry1, 0, h - 1 );
		if ( rx1 <= rx0 || ry1 <= ry0 ) return Fail( r, "trace region is empty after clamping to the picture." );
		var rw = rx1 - rx0 + 1; var rh = ry1 - ry0 + 1;
		float? alphaCut = null; float? colourCut = null;
		if ( pairs.TryGetValue( "threshold", out var th ) )
		{
			var t = Number( th );
			alphaCut = t; colourCut = t;
		}
		var full = ReferenceMatch.ClassifyMask( w, h, rgb, alpha, alphaCut, colourCut );
		var region = new bool[rw * rh];
		for ( var y = 0; y < rh; y++ )
			for ( var x = 0; x < rw; x++ )
				region[y * rw + x] = full[(ry0 + y) * w + (rx0 + x)];
		var minArea = pairs.TryGetValue( "minarea", out var ma ) ? (int)Number( ma ) : 100;
		var simplify = pairs.TryGetValue( "simplify", out var sim ) ? Number( sim ) : 2f;
		var kind = pairs.GetValueOrDefault( "kind", "poly").ToLowerInvariant();
		if ( kind is not ("poly" or "splineclosed") )
			return Fail( r, "trace kind is poly (closed polygon) or splineclosed (smoothed closed spline)." );
		var blobs = RefImageTools.LabelBlobs( region, rw, rh ).Where( b => b.Area >= minArea ).ToList();
		if ( blobs.Count == 0 )
			return Fail( r, $"trace: no blob of at least {minArea} px in the region. Lower minarea=, widen the region, or check the picture reads as subject-on-ground." );
		var holes = RefImageTools.CountHoles( region, rw, rh );
		var curves = new List<string>();
		var totalPts = 0;
		var anyLoop = false;
		foreach ( var blob in blobs )
		{
			var loops = RefImageTools.ContourLoops( region, rw, rh, blob );
			if ( loops.Count == 0 ) continue;
			anyLoop = true;
			var loop = loops[0];
			var tol = simplify;
			List<(float x, float y)> simplified;
			do
			{
				simplified = RefImageTools.Simplify( loop, tol );
				if ( simplified.Count <= 200 ) break;
				tol *= 2f;
			} while ( tol <= 64f );
			var pts = new List<string>();
			foreach ( var (cx, cy) in simplified )
			{
				var (u, v) = RefImageMath.CornerToPlane( image, w, h, rx0 + cx, ry0 + cy );
				var (_, sp, _) = RefImageMath.SketchForPlanePoint( view, u, v, image.Depth );
				pts.Add( $"{sp.x:0.###},{sp.y:0.###}" );
			}
			if ( pts.Count < 3 ) continue;
			curves.Add( $"{kind}:{string.Join( ",", pts )}" );
			totalPts += pts.Count;
			var area = blob.Area; var perim = loop.Count;
			var thin = perim > 0 && 4 * MathF.PI * area / (perim * perim) < 0.2f;
			if ( thin )
				r.Log.Add( $"trace: blob at {blob.MinX + rx0},{blob.MinY + ry0} is thin (area {area}, contour {perim}) — a centreline with half-width would read better than this filled outline; edit the curve before extruding." );
		}
		if ( curves.Count == 0 )
			return Fail( r, anyLoop
				? "trace: contours simplified away to fewer than 3 points. Lower simplify= and try again."
				: "trace: no contour found in the region. Check the picture reads as subject-on-ground, or lower minarea=." );
		if ( holes > 0 )
			r.Log.Add( $"trace: the region has {holes} enclosed hole(s) — holes are NOT emitted as sketch curves (a sketch poly has no holes); check them by hand." );
		if ( blobs.Count > 1 )
			r.Log.Add( $"trace: {blobs.Count} blobs became {curves.Count} curves in one sketch, one per blob — internal parts are kept separate, not filled into one outline." );
		var curvesText = string.Join( ";", curves );
		var (plane, _, offset) = RefImageMath.SketchForPlanePoint( view, 0, 0, image.Depth );
		var sketchName = pairs.GetValueOrDefault( "out", $"Trace_{view}" );
		var feature = Create( "Sketch" );
		if ( feature is null ) return Fail( r, "trace: no Sketch feature type." );
		var assign = new Dictionary<string, string>
		{
			["name"] = sketchName, ["plane"] = plane,
			["offset"] = offset.ToString( System.Globalization.CultureInfo.InvariantCulture ),
			["curves"] = curvesText,
		};
		if ( !Assign( studio, feature, assign, r ) ) return false;
		studio.Add( feature );
		if ( !RebuildAndCheck( studio, feature, r, $"added Sketch '{sketchName}' (trace candidate — look at it against the picture, edit, then extrude; it is not a body)" ) )
			return false;
		r.Log.Add( $"trace: {curves.Count} curve(s), {totalPts} points, simplify {simplify} px: curves=\"{curvesText}\"" );
		r.Log.Add( "trace: this is a candidate curve in the document, never a finished body and never imported geometry — edit it before extruding." );
		return true;
	}

	// --- refsection -----------------------------------------------------------------

	static bool RefSectionVerb( PartStudio studio, List<string> args, string baseDir, Result r )
	{
		if ( args.Count == 0 )
			return Fail( r, "refsection needs a view: refsection VIEW station= [body=Name] [tag=]" );
		var view = RefImageMath.NormaliseView( args[0] );
		var pairs = Pairs( args.Skip( 1 ) );
		if ( !pairs.TryGetValue( "station", out var stationText ) )
			return Fail( r, "refsection needs station=, the value along the view's up axis (side/front: Z, top: X)." );
		var station = Number( stationText );
		var tag = pairs.GetValueOrDefault( "tag", "" );
		var image = FindImage( studio, view, tag, r, out var why );
		if ( image is null ) return Fail( r, why );
		IReadOnlyList<Body> bodies = studio.Bodies;
		if ( pairs.TryGetValue( "body", out var bodyName ) )
		{
			var body = studio.Bodies.FirstOrDefault( b => string.Equals( b.Name, bodyName, StringComparison.OrdinalIgnoreCase ) );
			if ( body is null ) return Fail( r, $"refsection: no body '{bodyName}'." );
			bodies = new[] { body };
		}
		var slice = RefImageTools.SliceAt( bodies, image, station );
		if ( slice is null )
			return Fail( r, $"refsection: nothing crosses the station plane at {station:0.###}." );
		if ( !LoadPixels( image.Path, r, out var w, out var h, out var rgb, out var alpha ) ) return false;
		var mask = ReferenceMatch.ClassifyMask( w, h, rgb, alpha );
		// Station is an up-axis value; the picture row for it comes from the
		// up coordinate alone, whatever the right coordinate is.
		var py = (image.Offset.y + image.Height / 2f - station) / image.Height * h - 0.5f;
		var row = (int)Math.Round( py );
		var refWidth = 0f; var refCount = 0;
		if ( row >= 0 && row < h )
		{
			var mn = w; var mx = -1;
			for ( var x = 0; x < w; x++ )
				if ( mask[row * w + x] ) { if ( x < mn ) mn = x; if ( x > mx ) mx = x; refCount++; }
			if ( mx >= 0 ) refWidth = (mx - mn + 1) * RefImageMath.WorldPerPixel( image, h );
		}
		var (width, depth, segs) = slice.Value;
		r.Log.Add( $"refsection {view} station {station:0.###}: model width {width:0.###} u, depth {depth:0.###} u ({segs} slice segments); reference row {row} width {refWidth:0.###} u ({refCount} px)." );
		if ( refWidth > 0 )
		{
			var err = width - refWidth;
			r.Log.Add( $"refsection: width error {err:+0.###;-0.###} u ({err / refWidth * 100f:+0.#;-0.#}%). Positive means the model is wider than the drawing at this station." );
		}
		else
			r.Log.Add( "refsection: the drawing is empty at this station row, so no width error is reported — check the station is inside the drawing." );
		return true;
	}

	// --- refstep ----------------------------------------------------------------------

	static bool RefStepVerb( PartStudio studio, List<string> args, Result r )
	{
		if ( args.Count == 0 )
			return Fail( r, "refstep needs: refstep NAME state=map|built|checked | refstep list | refstep clear" );
		if ( args[0].ToLowerInvariant() == "list" )
		{
			if ( studio.RefSteps.Count == 0 ) { r.Log.Add( "refstep: no sections tracked." ); return true; }
			foreach ( var s in studio.RefSteps ) r.Log.Add( $"refstep {s.Name}: {s.State}" );
			return true;
		}
		if ( args[0].ToLowerInvariant() == "clear" )
		{
			var n = studio.RefSteps.Count;
			studio.RefSteps.Clear();
			r.Log.Add( n == 0 ? "refstep clear: nothing tracked." : $"refstep clear: removed {n} section(s)." );
			return true;
		}
		var pairs = Pairs( args.Skip( 1 ) );
		if ( !pairs.TryGetValue( "state", out var state ) )
			return Fail( r, "refstep needs state=map|built|checked." );
		state = state.ToLowerInvariant();
		if ( state is not ("map" or "built" or "checked" or "todo") )
			return Fail( r, "refstep state is map, built, checked, or todo." );
		studio.RefSteps.RemoveAll( s => s.Name == args[0] );
		studio.RefSteps.Add( new RefStep { Name = args[0], State = state } );
		r.Log.Add( $"refstep {args[0]}: {state}." );
		return true;
	}
}
