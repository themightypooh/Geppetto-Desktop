using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Effigy.Render;

/// <summary>
/// How well the model's silhouette from a view matches a reference drawing of the same view.
///
/// BOTH SILHOUETTES ARE FITTED TO THEIR OWN BOUNDS FIRST. A reference is drawn at whatever size
/// the paper was; the model is whatever size it is. What can be compared is SHAPE — the outline
/// scaled to fill the same box — and that is what the overlap measures. Height is compared
/// separately as the box's own aspect, since a fitted comparison would hide "too short".
///
/// The report says where it differs, in words an agent can act on: which bands of height the
/// model is wider or narrower than the drawing, and by how much of the width. That is the thing
/// a picture of two overlaid outlines shows a person, written down.
/// </summary>
public static class ReferenceMatch
{
	public sealed class Result
	{
		/// <summary>Intersection over union of the two fitted silhouettes. 1 is a perfect match; 0.8 is
		/// recognisably the same thing; 0.5 is a different shape.</summary>
		public float Overlap;

		/// <summary>The reference's height ÷ width, and the model's. Equal when the proportions agree.</summary>
		public float ReferenceAspect, ModelAspect;

		/// <summary>Where the model sticks out past the drawing, and where it falls short, as fractions
		/// of the frame.</summary>
		public float Excess, Missing;

		public string Report;
		public Raster Diff;
	}

	/// <summary>
	/// The reference's silhouette, fitted to its own bounds into a square of <paramref name="size"/>.
	///
	/// THREE KINDS OF PICTURE, ONE ANSWER. A cut-out on transparency: the alpha is the answer. A
	/// photo or a render on a plain ground: the ground's colour is read off the border, and the
	/// subject is whatever is far from it in colour — which also reads a pencil drawing on paper,
	/// since paper is the border colour and graphite is far from it. A picture with no plain
	/// border — a drawing cropped tight — falls back to dark-versus-light. In every case only the
	/// largest connected blob is kept, so labels, a caption, a watermark or a second object do
	/// not stretch the frame, and an outline drawing is filled in.
	/// </summary>
	/// <summary>Raw subject classification of a picture, before any
	/// cleaning, largest-blob selection, filling, or fitting. Trace and
	/// overlay use this so multi-part subjects keep every blob and
	/// holes are not filled away. Alpha cut and colour cut default to
	/// the values ReferenceSilhouette always used (128 and 0.12).</summary>
	public static bool[] ClassifyMask( int width, int height, int[] rgb, byte[] alpha, float? alphaCutOpt = null, float? colourCutOpt = null )
	{
		var alphaCut = (byte)Math.Clamp( alphaCutOpt ?? 128f, 0f, 255f );
		var colourCut = colourCutOpt ?? 0.12f;
		var mask = new bool[width * height];
		var transparent = alpha.Any( a => a < 250 );

		if ( transparent )
		{
			for ( var i = 0; i < mask.Length; i++ )
				mask[i] = alpha[i] >= alphaCut;
		}
		else
		{
			// The ground: the commonest colour in the WHOLE image (quantised, so a gradient still has a
			// mode) — a backdrop is the biggest thing in a reference, and reading it off the border
			// alone fails the moment a crop touches the subject or a caption bar. The border ring then
			// says how plain the ground is: how much of the edge is far from that colour.
			var ring = Math.Max( 1, Math.Min( width, height ) / 40 );
			var counts = new Dictionary<int, int>();
			var borderPixels = new List<int>();

			for ( var y = 0; y < height; y++ )
				for ( var x = 0; x < width; x++ )
				{
					if ( x >= ring && x < width - ring && y >= ring && y < height - ring )
						continue;
					borderPixels.Add( rgb[y * width + x] );
				}

			foreach ( var p in rgb )
			{
				var q = ((p >> 19) << 10) | (((p >> 11) & 0x1F) << 5) | ((p >> 3) & 0x1F);
				counts[q] = counts.GetValueOrDefault( q ) + 1;
			}

			var mode = counts.OrderByDescending( kv => kv.Value ).First().Key;
			float r = ((mode >> 10) & 0x1F) * 255f / 31f, g = ((mode >> 5) & 0x1F) * 255f / 31f, b = (mode & 0x1F) * 255f / 31f;
			var spread = borderPixels.Count( p => Distance( p, r, g, b ) > 0.15f ) / (float)borderPixels.Count;

			var distance = new float[width * height];

			if ( spread < 0.35f )
			{
				// A plain ground: the subject is what is far from it in colour — or in brightness, which
				// is weighted up so a black visor on a slate backdrop still counts as subject.
				// Per row, the ground is what the row's own left and right edges show, when they agree —
				// a backdrop is rarely flat, and a top-to-bottom gradient crossing one fixed cut reads as
				// a band of subject across the whole picture. Rows whose edges disagree (the subject
				// touches an edge) fall back to the overall ground.
				for ( var y = 0; y < height; y++ )
				{
					float lr = 0, lg = 0, lb = 0, rr = 0, rg = 0, rb = 0;

					for ( var x = 0; x < ring; x++ )
					{
						var l = rgb[y * width + x]; var rt = rgb[y * width + width - 1 - x];
						lr += (l >> 16) & 0xFF; lg += (l >> 8) & 0xFF; lb += l & 0xFF;
						rr += (rt >> 16) & 0xFF; rg += (rt >> 8) & 0xFF; rb += rt & 0xFF;
					}

					lr /= ring; lg /= ring; lb /= ring; rr /= ring; rg /= ring; rb /= ring;

					var edgesAgree = MathF.Sqrt( (lr - rr) * (lr - rr) + (lg - rg) * (lg - rg) + (lb - rb) * (lb - rb) ) / 255f / 1.732f < 0.08f;
					var leftNearGround = Distance( Raster.Rgb( (int)lr, (int)lg, (int)lb ), r, g, b ) < 0.25f;
					var gr = edgesAgree && leftNearGround ? (lr + rr) * 0.5f : r;
					var gg = edgesAgree && leftNearGround ? (lg + rg) * 0.5f : g;
					var gb = edgesAgree && leftNearGround ? (lb + rb) * 0.5f : b;
					var groundLuminance = (gr * 0.299f + gg * 0.587f + gb * 0.114f) / 255f;

					for ( var x = 0; x < width; x++ )
					{
						var i = y * width + x;
						distance[i] = MathF.Max( Distance( rgb[i], gr, gg, gb ), MathF.Abs( Luminance( rgb[i] ) - groundLuminance ) * 1.6f );
					}
				}
			}
			else
			{
				// No plain ground to read: dark versus light, the darker side taken as ink, then
				// flipped if the ink runs to the border more than the paper does.
				for ( var i = 0; i < rgb.Length; i++ )
					distance[i] = 1f - Luminance( rgb[i] );
			}

			// With a plain ground the question is only "is this the ground?", so the cut is a fixed
			// small distance — Otsu would split a white head from a black visor and keep one of them.
			// Without a plain ground there are two populations, ink and paper, and Otsu finds them.
			var cut = spread < 0.35f ? colourCut : Otsu( distance );

			for ( var i = 0; i < mask.Length; i++ )
				mask[i] = distance[i] > cut;

			LastRead = $"ground rgb({r:0},{g:0},{b:0}), {spread * 100f:0}% of the border far from it, cut {cut:0.00}, {mask.Count( m => m )} of {mask.Length} px subject before cleaning";

			if ( spread >= 0.35f )
			{
				var borderInk = 0; var border = 0;
				for ( var x = 0; x < width; x++ ) { border += 2; if ( mask[x] ) borderInk++; if ( mask[(height - 1) * width + x] ) borderInk++; }
				for ( var y = 0; y < height; y++ ) { border += 2; if ( mask[y * width] ) borderInk++; if ( mask[y * width + width - 1] ) borderInk++; }

				if ( borderInk * 2 > border )
					for ( var i = 0; i < mask.Length; i++ )
						mask[i] = !mask[i];
			}
		}

		return mask;
	}

	public static bool[] ReferenceSilhouette( int width, int height, int[] rgb, byte[] alpha, int size, out float aspect )
	{
		var mask = ClassifyMask( width, height, rgb, alpha );

		// Thin lines — a grid, hatching, a caption's strokes — are not the subject, however far they
		// are from the ground. Eroding a pixel removes anything one pixel wide; the largest blob is
		// then a solid shape; dilating puts its edge back.
		// EFFIGY_DEBUG_MASK=path writes the raw classification, for a match that read wrong.
		if ( Environment.GetEnvironmentVariable( "EFFIGY_DEBUG_MASK" ) is { Length: > 0 } dump )
		{
			var dbg = new Raster( width, height );
			for ( var i = 0; i < mask.Length; i++ ) if ( mask[i] ) dbg.Pixels[i] = 0xffffff;
			dbg.SavePng( dump );
		}

		// First a closing, so hairlines drawn OVER the subject — a wireframe, pencil construction
		// lines — do not cut it into confetti before the blob is picked.
		Dilate( mask, width, height );
		Erode( mask, width, height );

		Erode( mask, width, height );
		KeepLargestBlob( mask, width, height );
		Dilate( mask, width, height );
		FillInside( mask, width, height );
		LastRead += $", {mask.Count( m => m )} after cleaning";

		return Fit( mask, width, height, size, out aspect );
	}

	/// <summary>How the last reference was read — the ground colour, how plain the border was, the
	/// cut — for a match that came out wrong.</summary>
	public static string LastRead = "";

	static float Luminance( int p ) =>
		(((p >> 16) & 0xFF) * 0.299f + ((p >> 8) & 0xFF) * 0.587f + (p & 0xFF) * 0.114f) / 255f;

	/// <summary>Colour distance from a mean, 0..1, weighted so a hue change counts as much as a
	/// brightness change — a red toy on a grey table is far even when they are equally bright.</summary>
	static float Distance( int p, float r, float g, float b )
	{
		var dr = (((p >> 16) & 0xFF) - r) / 255f;
		var dg = (((p >> 8) & 0xFF) - g) / 255f;
		var db = ((p & 0xFF) - b) / 255f;
		return MathF.Sqrt( dr * dr + dg * dg + db * db ) / 1.732f;
	}

	static void Erode( bool[] mask, int width, int height )
	{
		var src = (bool[])mask.Clone();
		for ( var y = 0; y < height; y++ )
			for ( var x = 0; x < width; x++ )
			{
				var i = y * width + x;
				mask[i] = src[i] && x > 0 && src[i - 1] && x + 1 < width && src[i + 1] && y > 0 && src[i - width] && y + 1 < height && src[i + width];
			}
	}

	static void Dilate( bool[] mask, int width, int height )
	{
		var src = (bool[])mask.Clone();
		for ( var y = 0; y < height; y++ )
			for ( var x = 0; x < width; x++ )
			{
				var i = y * width + x;
				mask[i] = src[i] || (x > 0 && src[i - 1]) || (x + 1 < width && src[i + 1]) || (y > 0 && src[i - width]) || (y + 1 < height && src[i + width]);
			}
	}

	/// <summary>Keep only the largest 4-connected true region.</summary>
	static void KeepLargestBlob( bool[] mask, int width, int height )
	{
		var label = new int[width * height];
		var sizes = new List<int> { 0 };
		var stack = new Stack<int>();

		for ( var start = 0; start < mask.Length; start++ )
		{
			if ( !mask[start] || label[start] != 0 )
				continue;

			var id = sizes.Count;
			sizes.Add( 0 );
			label[start] = id;
			stack.Push( start );

			while ( stack.Count > 0 )
			{
				var i = stack.Pop();
				sizes[id]++;
				var x = i % width; var y = i / width;

				void Try( int j ) { if ( mask[j] && label[j] == 0 ) { label[j] = id; stack.Push( j ); } }
				if ( x > 0 ) Try( i - 1 );
				if ( x + 1 < width ) Try( i + 1 );
				if ( y > 0 ) Try( i - width );
				if ( y + 1 < height ) Try( i + width );
			}
		}

		if ( sizes.Count <= 2 )
			return;

		var best = 1;
		for ( var id = 2; id < sizes.Count; id++ )
			if ( sizes[id] > sizes[best] ) best = id;

		for ( var i = 0; i < mask.Length; i++ )
			mask[i] = label[i] == best;
	}

	/// <summary>Fill the interior of an outline: anything not reachable from the border is inside.</summary>
	static void FillInside( bool[] mask, int width, int height )
	{
		var outside = new bool[width * height];
		var stack = new Stack<int>();

		void Push( int y, int x )
		{
			var i = y * width + x;
			if ( !mask[i] && !outside[i] ) { outside[i] = true; stack.Push( i ); }
		}

		for ( var x = 0; x < width; x++ ) { Push( 0, x ); Push( height - 1, x ); }
		for ( var y = 0; y < height; y++ ) { Push( y, 0 ); Push( y, width - 1 ); }

		while ( stack.Count > 0 )
		{
			var i = stack.Pop();
			var x = i % width; var y = i / width;
			if ( x > 0 ) Push( y, x - 1 );
			if ( x + 1 < width ) Push( y, x + 1 );
			if ( y > 0 ) Push( y - 1, x );
			if ( y + 1 < height ) Push( y + 1, x );
		}

		for ( var i = 0; i < mask.Length; i++ )
			mask[i] = !outside[i];
	}

	/// <summary>Otsu's threshold: the cut that best separates the two brightness populations, so a
	/// grey model on a dark ground splits from the ground and not from a few white labels.</summary>
	static float Otsu( float[] values )
	{
		const int bins = 64;
		var histogram = new int[bins];

		foreach ( var v in values )
			histogram[Math.Clamp( (int)(v * (bins - 1)), 0, bins - 1 )]++;

		var total = values.Length;
		var sum = 0f;
		for ( var i = 0; i < bins; i++ ) sum += i * histogram[i];

		var sumB = 0f; var wB = 0; var best = 0f; var threshold = bins / 2;

		for ( var i = 0; i < bins; i++ )
		{
			wB += histogram[i];
			if ( wB == 0 ) continue;
			var wF = total - wB;
			if ( wF == 0 ) break;
			sumB += i * histogram[i];
			var mB = sumB / wB;
			var mF = (sum - sumB) / wF;
			var between = wB * (float)wF * (mB - mF) * (mB - mF);
			if ( between > best ) { best = between; threshold = i; }
		}

		return (threshold + 0.5f) / (bins - 1);
	}

	/// <summary>Crop a mask to its bounds and scale it into a square, keeping its aspect (centred).</summary>
	public static bool[] Fit( bool[] mask, int width, int height, int size, out float aspect )
	{
		int minX = width, minY = height, maxX = -1, maxY = -1;

		for ( var y = 0; y < height; y++ )
			for ( var x = 0; x < width; x++ )
				if ( mask[y * width + x] )
				{
					minX = Math.Min( minX, x ); maxX = Math.Max( maxX, x );
					minY = Math.Min( minY, y ); maxY = Math.Max( maxY, y );
				}

		var result = new bool[size * size];

		if ( maxX < 0 )
		{
			aspect = 1f;
			return result;
		}

		var w = maxX - minX + 1;
		var h = maxY - minY + 1;
		aspect = h / (float)w;

		var scale = MathF.Max( w, h ) / (float)size;
		var ox = (size - w / scale) * 0.5f;
		var oy = (size - h / scale) * 0.5f;

		for ( var y = 0; y < size; y++ )
			for ( var x = 0; x < size; x++ )
			{
				var sx = (int)((x - ox) * scale) + minX;
				var sy = (int)((y - oy) * scale) + minY;

				if ( sx < minX || sy < minY || sx > maxX || sy > maxY )
					continue;

				result[y * size + x] = mask[sy * width + sx];
			}

		return result;
	}

	public static Result Compare( IReadOnlyList<Body> bodies, RenderView view, bool[] reference, float referenceAspect, int size )
	{
		var raw = ModelRender.Silhouette( bodies, view, size );
		var model = Fit( raw, size, size, size, out var modelAspect );

		var both = 0; var either = 0; var excess = 0; var missing = 0;

		for ( var i = 0; i < model.Length; i++ )
		{
			if ( model[i] && reference[i] ) both++;
			if ( model[i] || reference[i] ) either++;
			if ( model[i] && !reference[i] ) excess++;
			if ( !model[i] && reference[i] ) missing++;
		}

		var result = new Result
		{
			Overlap = either == 0 ? 1f : both / (float)either,
			ReferenceAspect = referenceAspect,
			ModelAspect = modelAspect,
			Excess = excess / (float)(size * size),
			Missing = missing / (float)(size * size),
		};

		// The diff picture: reference in blue, model in orange, the overlap where both agree.
		var diff = new Raster( size, size, 0x121418 );

		for ( var i = 0; i < model.Length; i++ )
		{
			if ( model[i] && reference[i] ) diff.Pixels[i] = 0x6f7d8c;
			else if ( model[i] ) diff.Pixels[i] = 0xf0a13a;
			else if ( reference[i] ) diff.Pixels[i] = 0x5b93ff;
		}

		diff.Text( 6, 6, $"overlap {result.Overlap * 100f:0}%", 0xe7e9ee, 2 );
		diff.Text( 6, 20, "orange: model only  blue: drawing only", 0x9aa2ae, 1 );
		result.Diff = diff;

		result.Report = Words( model, reference, size, result, view );
		return result;
	}

	/// <summary>The differences in bands of height, in words.</summary>
	static string Words( bool[] model, bool[] reference, int size, Result r, RenderView view )
	{
		var sb = new StringBuilder();
		sb.AppendLine( $"{view.Name}: silhouette overlap {r.Overlap * 100f:0}%. Drawing is {r.ReferenceAspect:0.00} tall per unit wide, model is {r.ModelAspect:0.00}"
			+ (MathF.Abs( r.ReferenceAspect - r.ModelAspect ) / MathF.Max( r.ReferenceAspect, 1e-3f ) > 0.08f
				? (r.ModelAspect < r.ReferenceAspect ? " — the model is too wide for its height (or too short)." : " — the model is too tall for its width (or too narrow).")
				: " — proportions agree.") );

		const int bands = 8;
		var names = new[] { "top", "upper", "upper-middle", "middle", "middle", "lower-middle", "lower", "bottom" };

		for ( var b = 0; b < bands; b++ )
		{
			var y0 = b * size / bands;
			var y1 = (b + 1) * size / bands;

			float modelWidth = 0, refWidth = 0, modelLeft = 0, refLeft = 0, modelRight = 0, refRight = 0;
			var rows = 0;

			for ( var y = y0; y < y1; y++ )
			{
				int mMin = size, mMax = -1, rMin = size, rMax = -1;

				for ( var x = 0; x < size; x++ )
				{
					if ( model[y * size + x] ) { mMin = Math.Min( mMin, x ); mMax = Math.Max( mMax, x ); }
					if ( reference[y * size + x] ) { rMin = Math.Min( rMin, x ); rMax = Math.Max( rMax, x ); }
				}

				if ( mMax < 0 && rMax < 0 )
					continue;

				rows++;
				if ( mMax >= 0 ) { modelWidth += mMax - mMin + 1; modelLeft += mMin; modelRight += mMax; }
				if ( rMax >= 0 ) { refWidth += rMax - rMin + 1; refLeft += rMin; refRight += rMax; }
			}

			if ( rows == 0 )
				continue;

			modelWidth /= rows; refWidth /= rows;
			var delta = (modelWidth - refWidth) / size;

			if ( MathF.Abs( delta ) < 0.04f )
				continue;

			var band = names[b];
			var pct = MathF.Abs( delta ) * 100f;
			sb.AppendLine( delta > 0
				? $"- {band}: model is wider than the drawing by {pct:0}% of the frame."
				: $"- {band}: model is narrower than the drawing by {pct:0}% of the frame." );
		}

		if ( r.Excess > 0.02f && r.Missing < 0.01f )
			sb.AppendLine( "Mostly the model sticks out past the drawing: trim rather than add." );
		else if ( r.Missing > 0.02f && r.Excess < 0.01f )
			sb.AppendLine( "Mostly the drawing reaches where the model does not: add rather than trim." );

		return sb.ToString().TrimEnd();
	}

	// --- Lane I-delta (roadmap Lane K2 in the 12-lane rewrite): auto-fit from a reference ---
	//
	// ExtractProfileCurves turns ONE reference picture into ready-to-paste ProfileFeature
	// curve strings. It reuses ReferenceSilhouette's own cleaning (largest blob, filled
	// outline), so a pencil sketch, a cut-out, or a photo on a plain ground all read the same.
	//
	// FORMAT, AND THE ONE DEVIATION FROM THE ROADMAP'S NOTATION. The roadmap writes curves
	// as "s:value" (for example top="0:0.08;0.08:0.62"). The kernel's actual grammar — see
	// ProfileFeature.Parse and every script in the tree — is "s,value" pairs separated by
	// ';' (for example top=0,5;10,3.2). These strings use the kernel grammar, so they parse
	// as-is in `add Profile ... top=...`; a colon form would be silently skipped by Parse.
	// NORMALISATION. s runs 0..1 along the silhouette's major axis, and every extent is
	// divided by that axis's length, so one scale factor converts the whole set to inches:
	// multiply every s AND every extent by the desired model length. Top is the extent on
	// the image-up side of the centreline, Bottom the image-down side (for a vertical
	// major axis: image-left / image-right instead). Spine is the centreline's offset from
	// the straight chord between its endpoints, in the same units, positive toward the
	// Bottom side. Front is empty from a single side view — a side picture cannot know the
	// width — and is filled only by ExtractProfileCurvesFromViews, from a top view, as the
	// half-span (half the width) at each station.
	public sealed class ProfileCurves
	{
		public string Top = "";
		public string Bottom = "";
		public string Front = "";
		public string Spine = "";
		public int Stations;
		public float Aspect;
		public float MajorLengthPx;
	}

	/// <summary>Profile curves from one reference picture (a side view, normally).</summary>
	public static ProfileCurves ExtractProfileCurves( int width, int height, int[] rgb, byte[] alpha, int stations = 16 )
	{
		var points = ExtractPoints( width, height, rgb, alpha, stations, out var aspect, out var majorLen );
		return new ProfileCurves
		{
			Top = CurveString( points, p => p.top ),
			Bottom = CurveString( points, p => p.bottom ),
			Front = "",
			Spine = CurveString( points, p => p.spine ),
			Stations = points.Count,
			Aspect = aspect,
			MajorLengthPx = majorLen,
		};
	}

	/// <summary>Profile curves from a side view plus a top view: Front comes from the top
	/// view's half-span at the side view's stations. Both views must show the same subject
	/// with its long axis horizontal (or both vertical); the stations are the side's.</summary>
	public static ProfileCurves ExtractProfileCurvesFromViews( int sideWidth, int sideHeight, int[] sideRgb, byte[] sideAlpha,
		int topWidth, int topHeight, int[] topRgb, byte[] topAlpha, int stations = 16 )
	{
		var side = ExtractProfileCurves( sideWidth, sideHeight, sideRgb, sideAlpha, stations );
		var sidePoints = ExtractPoints( sideWidth, sideHeight, sideRgb, sideAlpha, stations, out _, out _ );
		var topFitted = ReferenceSilhouette( topWidth, topHeight, topRgb, topAlpha, 256, out _ );
		var (tMinX, tMaxX, tMinY, tMaxY) = Bounds( topFitted, 256 );
		if ( tMaxX < 0 )
			return side;
		var topHorizontal = tMaxX - tMinX >= tMaxY - tMinY;
		DenseSeries( topFitted, 256, topHorizontal, out var tMinMajor, out var tMaxMajor, out var tTop, out var tBottom, out _ );
		var topMajorLen = Math.Max( 1, tMaxMajor - tMinMajor + 1 );
		var front = new List<(float s, float v)>();
		foreach ( var p in sidePoints )
		{
			var half = ( SampleDense( tTop, tMinMajor, tMaxMajor, p.s ) + SampleDense( tBottom, tMinMajor, tMaxMajor, p.s ) ) * 0.5f;
			front.Add( (p.s, half / topMajorLen) );
		}
		side.Front = string.Join( ";", front.Select( f => F( f.s ) + "," + F( f.v ) ) );
		return side;
	}

	static string F( float v ) => v.ToString( "0.000", CultureInfo.InvariantCulture );

	static string CurveString( List<(float s, float top, float bottom, float spine)> points, Func<(float s, float top, float bottom, float spine), float> pick )
		=> string.Join( ";", points.Select( p => F( p.s ) + "," + F( pick( p ) ) ) );

	/// <summary>Sampled (s, top, bottom, spine) points, s in 0..1, extents normalised by major length.</summary>
	static List<(float s, float top, float bottom, float spine)> ExtractPoints( int width, int height, int[] rgb, byte[] alpha, int stations, out float aspect, out float majorLen )
	{
		if ( width <= 0 || height <= 0 )
			throw new ArgumentException( "ExtractProfileCurves needs a picture with width and height above 0." );
		if ( rgb is null || alpha is null || rgb.Length != width * height || alpha.Length != width * height )
			throw new ArgumentException( "ExtractProfileCurves needs rgb and alpha arrays of exactly width*height." );
		if ( stations < 2 )
			throw new ArgumentException( "ExtractProfileCurves needs at least 2 stations." );
		stations = Math.Min( stations, 128 );

		var fitted = ReferenceSilhouette( width, height, rgb, alpha, 256, out aspect );
		var (minX, maxX, minY, maxY) = Bounds( fitted, 256 );
		var points = new List<(float s, float top, float bottom, float spine)>();
		majorLen = 0;
		if ( maxX < 0 )
			return points;

		var horizontal = maxX - minX >= maxY - minY;
		DenseSeries( fitted, 256, horizontal, out var minMajor, out var maxMajor, out var top, out var bottom, out var centre );
		majorLen = maxMajor - minMajor + 1;
		var centre0 = centre[0];
		var centre1 = centre[centre.Length - 1];

		// Even stations, plus curvature inflection points: places where the span or the
		// centreline bends sharply (a jaw step, a fin root, a waist). A second difference
		// above 2% of the major length, at a local maximum, earns an extra station.
		var sVals = new List<float>();
		for ( var i = 0; i < stations; i++ )
			sVals.Add( i / (float)(stations - 1) );
		var span = new float[top.Length];
		for ( var i = 0; i < span.Length; i++ )
			span[i] = top[i] + bottom[i];
		var thresh = 0.02f * majorLen;
		var candidates = new List<(float s, float score)>();
		for ( var i = 1; i < span.Length - 1; i++ )
		{
			var dSpan = MathF.Abs( span[i - 1] - 2 * span[i] + span[i + 1] );
			var dCentre = MathF.Abs( centre[i - 1] - 2 * centre[i] + centre[i + 1] );
			var score = Math.Max( dSpan, dCentre );
			if ( score <= thresh )
				continue;
			var prev = i >= 2 ? Math.Max( MathF.Abs( span[i - 2] - 2 * span[i - 1] + span[i] ), MathF.Abs( centre[i - 2] - 2 * centre[i - 1] + centre[i] ) ) : 0f;
			var next = i + 2 < span.Length ? Math.Max( MathF.Abs( span[i] - 2 * span[i + 1] + span[i + 2] ), MathF.Abs( centre[i] - 2 * centre[i + 1] + centre[i + 2] ) ) : 0f;
			if ( score >= prev && score >= next )
				candidates.Add( (i / (float)(span.Length - 1), score) );
		}
		candidates.Sort( (a, b) => b.score.CompareTo( a.score ) );
		foreach ( var c in candidates )
		{
			if ( sVals.Count >= stations * 2 )
				break;
			if ( sVals.Any( s => MathF.Abs( s - c.s ) <= 0.01f ) )
				continue;
			sVals.Add( c.s );
		}
		sVals.Sort();

		foreach ( var s in sVals )
		{
			var t = SampleDense( top, minMajor, maxMajor, s );
			var b = SampleDense( bottom, minMajor, maxMajor, s );
			var c = SampleDense( centre, minMajor, maxMajor, s );
			var chord = centre0 + s * (centre1 - centre0);
			points.Add( (s, t / majorLen, b / majorLen, (c - chord) / majorLen) );
		}
		return points;
	}

	static (int minX, int maxX, int minY, int maxY) Bounds( bool[] mask, int size )
	{
		int minX = size, minY = size, maxX = -1, maxY = -1;
		for ( var y = 0; y < size; y++ )
			for ( var x = 0; x < size; x++ )
				if ( mask[y * size + x] )
				{
					minX = Math.Min( minX, x ); maxX = Math.Max( maxX, x );
					minY = Math.Min( minY, y ); maxY = Math.Max( maxY, y );
				}
		return (minX, maxX, minY, maxY);
	}

	/// <summary>Per-major-coordinate top/bottom extents and centre, gaps interpolated, in fitted pixels.</summary>
	static void DenseSeries( bool[] fitted, int size, bool horizontal, out int minMajor, out int maxMajor, out float[] top, out float[] bottom, out float[] centre )
	{
		var mn = new float[size];
		var mx = new float[size];
		for ( var i = 0; i < size; i++ ) { mn[i] = float.MaxValue; mx[i] = float.MinValue; }
		for ( var y = 0; y < size; y++ )
			for ( var x = 0; x < size; x++ )
			{
				if ( !fitted[y * size + x] )
					continue;
				var major = horizontal ? x : y;
				var cross = horizontal ? y : x;
				mn[major] = Math.Min( mn[major], cross );
				mx[major] = Math.Max( mx[major], cross );
			}
		minMajor = -1; maxMajor = -1;
		for ( var i = 0; i < size; i++ )
			if ( mx[i] >= mn[i] ) { if ( minMajor < 0 ) minMajor = i; maxMajor = i; }
		var n = Math.Max( 1, maxMajor - minMajor + 1 );
		top = new float[n]; bottom = new float[n]; centre = new float[n];
		for ( var k = 0; k < n; k++ )
		{
			var m = minMajor + k;
			float lo, hi;
			if ( mx[m] >= mn[m] ) { lo = mn[m]; hi = mx[m]; }
			else
			{
				// A gap inside the span (a narrow neck the cleaning left open): bridge it
				// from the nearest filled neighbours, or the nearest end outside the span.
				var prev = m - 1;
				while ( prev >= minMajor && mx[prev] < mn[prev] ) prev--;
				var next = m + 1;
				while ( next <= maxMajor && mx[next] < mn[next] ) next++;
				if ( prev >= minMajor && next <= maxMajor )
				{
					var t = (m - prev) / (float)(next - prev);
					lo = mn[prev] + t * (mn[next] - mn[prev]);
					hi = mx[prev] + t * (mx[next] - mx[prev]);
				}
				else if ( prev >= minMajor ) { lo = mn[prev]; hi = mx[prev]; }
				else { lo = mn[next]; hi = mx[next]; }
			}
			var c = (lo + hi) * 0.5f;
			centre[k] = c; top[k] = c - lo; bottom[k] = hi - c;
		}
	}

	static float SampleDense( float[] arr, int minMajor, int maxMajor, float s )
	{
		if ( arr.Length == 1 )
			return arr[0];
		var pos = s * (arr.Length - 1);
		var i = Math.Clamp( (int)MathF.Floor( pos ), 0, arr.Length - 2 );
		var t = pos - i;
		return arr[i] + t * (arr[i + 1] - arr[i]);
	}

}
