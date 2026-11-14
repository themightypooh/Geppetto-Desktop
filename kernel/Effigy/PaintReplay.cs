using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// The texel bounds a dab touched, so the live session can recompose and re-upload only what a dab
/// changed rather than the whole canvas. A value type with no allocation, because a stroke fires
/// thousands of dabs and a class here would be thousands of objects a second.
/// </summary>
public readonly struct TexelBounds
{
	public readonly bool Any;
	public readonly int MinX, MinY, MaxX, MaxY;

	public TexelBounds( int minX, int minY, int maxX, int maxY )
	{
		Any = true;
		MinX = minX;
		MinY = minY;
		MaxX = maxX;
		MaxY = maxY;
	}

	public TexelBounds With( int x, int y ) => Any
		? new TexelBounds( Math.Min( MinX, x ), Math.Min( MinY, y ), Math.Max( MaxX, x ), Math.Max( MaxY, y ) )
		: new TexelBounds( x, y, x, y );
}

/// <summary>
/// Replays paint strokes onto a canvas — the derived-artifact half of the storage decision in
/// docs/dev/PAINTING.md §4.
///
/// Paint is stored as strokes in object space, not as texels, so a rebuilt or re-unwrapped mesh gets
/// its paint re-applied from scratch rather than pasted through an atlas that moved under it. This is
/// the re-applier: for each stroke it drops the brush sphere onto the surface and rasterises whatever
/// faces the sphere touches, weighting every texel by its 3D distance from the dab centre. Neither
/// the brush nor the rasteriser ever learns what a seam is — a stroke crossing a chart boundary
/// paints both charts because both have faces inside the sphere.
///
/// A STROKE IS COMPOSITED ONCE, NOT PER DAB. The vertex-colour path blended every dab straight into
/// the result, so a held brush kept compositing the same coverage over itself and the colour "ticked
/// up" like a sculpt brush instead of laying down like a paint brush. Here each stroke's dabs are
/// first stamped into a per-stroke COVERAGE buffer (the maximum falloff any dab reached at each
/// texel, never the sum), and then that one buffer is composited into the canvas. Holding still
/// re-stamps the same coverage and changes nothing.
///
/// THE DAB IS SHARED, NOT DUPLICATED. <see cref="PaintSession"/> composites a live dab through
/// <see cref="StampDab"/>; a rebuild replays the whole list through <see cref="Replay"/>. Both paths
/// end at the same rasterise-and-stamp, so a stroke painted live and the same stroke replayed later
/// produce identical texels.
///
/// AN ERASE IS A STROKE LIKE ANY OTHER, and takes the identical route: same dabs, same coverage
/// buffer, same once-per-stroke application. Only the last step differs — <see cref="Apply"/> sends a
/// painting stroke to <see cref="Composite"/> and an erasing one to <see cref="Erase"/>. That is why
/// erases and paint interleave correctly without any ordering machinery: they are the same log.
/// </summary>
public static class PaintReplay
{
	/// <summary>How many texels a replayed canvas bleeds outward at its islands' edges, so a shader
	/// filtering across a seam finds colour there instead of the gutter. Same figure the bake uses.</summary>
	const int DilatePasses = 4;

	/// <summary>
	/// Replay every stroke, in order, onto a fresh canvas. The order is the whole point — colour
	/// blending does not commute — and this preserves it exactly, compositing each stroke once from
	/// its own coverage buffer so dabs within a stroke never accumulate.
	/// </summary>
	public static PaintCanvas Replay( PolyMesh mesh, IReadOnlyList<PaintStroke> strokes, int resolution ) =>
		Replay( mesh, strokes, resolution, null );

	/// <summary>The same replay over a tree the caller already built — a layer replays one canvas per
	/// channel over one mesh, and building the tree once per channel is waste.</summary>
	internal static PaintCanvas Replay( PolyMesh mesh, IReadOnlyList<PaintStroke> strokes, int resolution, MeshBVH tree ) =>
		ReplayCore( mesh, strokes, resolution, tree, null );

	/// <summary>
	/// Replay every stroke with a tri-planar pattern modulating the coverage: each texel's weight
	/// is its falloff weight times the pattern sampled in world space at that texel's surface
	/// point (see <see cref="TriPlanar"/>). A pattern of None behaves exactly like
	/// <see cref="Replay"/> — same dabs, same coverage, same dilate.
	/// </summary>
	public static PaintCanvas ReplayTriPlanar( PolyMesh mesh, IReadOnlyList<PaintStroke> strokes, int resolution,
		TriPlanarPattern pattern, float scale, float sharpness ) =>
		ReplayTriPlanar( mesh, strokes, resolution, pattern, scale, sharpness, null );

	/// <summary>The tri-planar replay over a tree the caller already built.</summary>
	internal static PaintCanvas ReplayTriPlanar( PolyMesh mesh, IReadOnlyList<PaintStroke> strokes, int resolution,
		TriPlanarPattern pattern, float scale, float sharpness, MeshBVH tree )
	{
		Func<Vec3, Vec3, float> modulator = pattern == TriPlanarPattern.None
			? null
			: ( p, n ) => TriPlanar.Sample( pattern, p, n, scale, sharpness );

		return ReplayCore( mesh, strokes, resolution, tree, modulator );
	}

	/// <summary>
	/// The one replay body. The plain path passes no modulator and behaves byte-for-bit as before;
	/// the tri-planar path passes a world-space pattern sampler. Everything else — one composite
	/// per stroke, max-not-sum coverage, erase tracking, the dilate — is shared, so the two paths
	/// cannot drift apart about anything but the pattern.
	/// </summary>
	static PaintCanvas ReplayCore( PolyMesh mesh, IReadOnlyList<PaintStroke> strokes, int resolution, MeshBVH tree,
		Func<Vec3, Vec3, float> pattern )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( resolution < 1 )
			throw new ArgumentOutOfRangeException( nameof( resolution ) );

		var canvas = new PaintCanvas( resolution, resolution );

		if ( strokes is { Count: > 0 } )
		{
			var bvh = tree ?? MeshBVH.Build( mesh );
			var faces = new List<int>();
			var coverage = new float[resolution * resolution];

			// Which texels the log last spoke about with an ERASE, so the dilate below leaves them
			// alone. Without this an erased hole inside an island is indistinguishable from the
			// gutter outside one, and the dilate bleeds the paint straight back into it — see
			// PaintCanvas.Dilate. Allocated only when something actually erases, because the
			// overwhelmingly common document has no erases in it at all.
			bool[] erased = null;

			foreach ( var stroke in strokes )
			{
				Array.Clear( coverage, 0, coverage.Length );
				ReplayOne( canvas, stroke, mesh, bvh, coverage, resolution, faces, pattern );

				if ( stroke.Erase )
					erased ??= new bool[resolution * resolution];

				if ( erased is not null )
					MarkErased( erased, coverage, stroke.Erase );
			}

			canvas.Dilate( DilatePasses, erased );
		}

		return canvas;
	}

	/// <summary>
	/// Apply one stroke's finished coverage to a canvas: paint composites its colour in, an erase
	/// takes coverage back out of the alpha.
	///
	/// THE ONE PLACE THAT BRANCH IS MADE. The replay, the live session's commit and its reload all
	/// end here, so there is no route by which a stroke painted by hand and the same stroke replayed
	/// later could disagree about which of the two it was.
	/// </summary>
	internal static void Apply( PaintCanvas canvas, PaintStroke stroke, float[] coverage ) =>
		Apply( canvas, stroke, coverage, null );

	/// <summary>The same branch with a clone's resolved source. A clone stroke with no source (the
	/// caller had no mesh to sample) applies nothing — painting it in its own unused colour would be
	/// a mark the user never asked for.</summary>
	internal static void Apply( PaintCanvas canvas, PaintStroke stroke, float[] coverage, CloneSampler clone )
	{
		if ( stroke.Erase )
			Erase( canvas, coverage );
		else if ( stroke.Clone )
		{
			if ( clone is not null )
				CompositeClone( canvas, coverage, clone );
		}
		else
		{
			var (r, g, b) = StrokeBytes( stroke );
			Composite( canvas, coverage, r, g, b );
		}
	}

	/// <summary>The bytes a stroke lays down. A scalar channel (roughness, metalness, height, mask)
	/// reads its value from R and writes it as a grey, whatever G and B hold — so a hand-edited or
	/// scripted stroke cannot put colour into a map that has none.</summary>
	internal static (byte R, byte G, byte B) StrokeBytes( PaintStroke stroke )
	{
		if ( stroke.Channel == PaintChannel.Color )
			return (ToByte( stroke.R ), ToByte( stroke.G ), ToByte( stroke.B ));

		var v = ToByte( stroke.R );
		return (v, v, v);
	}

	/// <summary>
	/// Stamp and apply one stroke from a clean coverage buffer — the one route a replayed stroke and
	/// a session's reload both take, so a clone resolves its source the same way in each. A clone
	/// samples <paramref name="canvas"/> as it stands BEFORE this stroke, which is exactly the
	/// canvas passed in, because nothing has been applied to it yet.
	/// </summary>
	internal static void ReplayOne( PaintCanvas canvas, PaintStroke stroke, PolyMesh mesh, MeshBVH bvh,
		float[] coverage, int resolution, List<int> faces, Func<Vec3, Vec3, float> pattern )
	{
		if ( stroke is { Smudge: true, Erase: false } )
		{
			SmudgeStroke( canvas, stroke, mesh, bvh, coverage, resolution, faces, pattern );
			return;
		}

		if ( stroke is { Blur: true, Erase: false } )
		{
			BlurStroke( canvas, stroke, mesh, bvh, coverage, resolution, faces, pattern );
			return;
		}

		var clone = stroke is { Clone: true, Erase: false }
			? new CloneSampler( mesh, bvh, canvas, stroke.CloneOffset, stroke.Radius )
			: null;

		StampStroke( stroke, mesh, bvh, coverage, resolution, faces, pattern, clone );
		Apply( canvas, stroke, coverage, clone );
	}

	/// <summary>Composite a clone stroke's coverage: each covered texel takes its resolved source
	/// colour, at the coverage times the source's own alpha, so cloning half-painted paint copies it
	/// half-painted and cloning bare surface copies nothing.</summary>
	internal static void CompositeClone( PaintCanvas canvas, float[] coverage, CloneSampler clone )
	{
		for ( var y = 0; y < canvas.Height; y++ )
		{
			for ( var x = 0; x < canvas.Width; x++ )
			{
				var t = y * canvas.Width + x;
				var weight = coverage[t];

				if ( weight <= 0f || !clone.TryGet( t, out var r, out var g, out var b, out var a ) )
					continue;

				canvas.Blend( x, y, r, g, b, weight * a );
			}
		}
	}

	/// <summary>Note which texels this stroke touched, and whether it was an erase that touched them.
	/// LAST WRITER WINS, because the strokes are a log: painting over an erased texel makes it
	/// painted again, and erasing a painted one makes it deliberately empty.</summary>
	static void MarkErased( bool[] erased, float[] coverage, bool erasing )
	{
		for ( var i = 0; i < erased.Length; i++ )
		{
			if ( coverage[i] > 0f )
				erased[i] = erasing;
		}
	}

	/// <summary>Stamp one stroke's dabs into a coverage buffer, taking the maximum. The live session
	/// uses this to seed its coverage from the stroke in flight, so it must not composite anything —
	/// the composite is the caller's step, once, at the end.</summary>
	internal static void StampStroke( PaintStroke stroke, PolyMesh mesh, MeshBVH bvh,
		float[] coverage, int resolution, List<int> faces ) =>
		StampStroke( stroke, mesh, bvh, coverage, resolution, faces, null );

	/// <summary>
	/// Stamp one stroke's dabs with an optional world-space pattern modulator: when
	/// <paramref name="pattern"/> is set, each texel's weight is its falloff weight times the
	/// pattern sampled at that texel's surface point and face normal (see
	/// <see cref="TriPlanar"/>). A null modulator is the plain path, unchanged. The live session
	/// calls the plain overload, so a pattern shows on rebuild replay — a live dab previews
	/// unpatterned coverage, which is a stated limit, not a second code path.
	/// </summary>
	internal static void StampStroke( PaintStroke stroke, PolyMesh mesh, MeshBVH bvh,
		float[] coverage, int resolution, List<int> faces, Func<Vec3, Vec3, float> pattern ) =>
		StampStroke( stroke, mesh, bvh, coverage, resolution, faces, pattern, null );

	/// <summary>How much of what a smudge dab lands on joins the paint it carries: 0 drags the first
	/// colour forever, 1 carries nothing. A quarter lets a drag pull a colour a few radii before it
	/// is blended away, which is about where Procreate's default smudge sits.</summary>
	public const float SmudgePickup = 0.25f;

	/// <summary>
	/// Walk a smudge stroke's dabs in order over <paramref name="canvas"/>, writing as it goes (see
	/// <see cref="PaintStroke.Smudge"/>). <paramref name="coverage"/> ends as the stroke's max weight
	/// per texel, the same thing every other stroke leaves there, so the replay's erase tracking sees a
	/// smudge as paint. The replay, the session's commit and the live preview all come through here.
	/// </summary>
	internal static void SmudgeStroke( PaintCanvas canvas, PaintStroke stroke, PolyMesh mesh, MeshBVH bvh,
		float[] coverage, int resolution, List<int> faces, Func<Vec3, Vec3, float> pattern )
	{
		if ( stroke is null || stroke.Path.Count == 0 )
			return;

		var smudge = new SmudgeState( new float[resolution * resolution] );

		foreach ( var point in stroke.Path )
			SmudgeDab( canvas, stroke, mesh, bvh, coverage, resolution, faces, pattern, smudge, point.Position, point.Normal );
	}

	/// <summary>The carried paint between a smudge's dabs, and the dab's own scratch weights.</summary>
	internal sealed class SmudgeState
	{
		public readonly float[] Weights;
		public bool Loaded;
		public float R, G, B, A;   // premultiplied, 0..1

		/// <param name="weights">A zeroed scratch buffer, one float per texel; left zeroed after every dab.</param>
		public SmudgeState( float[] weights ) => Weights = weights;
	}

	/// <summary>One smudge dab: read the paint under the brush (weighted by the falloff), lay the
	/// carried paint over it, then carry a little of what was there. The first dab only picks up.
	/// Returns the texels it wrote.</summary>
	internal static TexelBounds SmudgeDab( PaintCanvas canvas, PaintStroke stroke, PolyMesh mesh, MeshBVH bvh,
		float[] coverage, int resolution, List<int> faces, Func<Vec3, Vec3, float> pattern, SmudgeState state,
		Vec3 point, Vec3 normal )
	{
		var w = state.Weights;
		var bounds = StampDab( mesh, bvh, w, resolution, point, normal, stroke.Radius, 1f, stroke.Falloff, faces, pattern, null );

		if ( !bounds.Any )
			return bounds;

		var rgba = canvas.Rgba;
		float sum = 0f, sr = 0f, sg = 0f, sb = 0f, sa = 0f;

		for ( var y = bounds.MinY; y <= bounds.MaxY; y++ )
		{
			for ( var x = bounds.MinX; x <= bounds.MaxX; x++ )
			{
				var t = y * resolution + x;

				if ( w[t] <= 0f )
					continue;

				var i = t * 4;
				var a = rgba[i + 3] / 255f;

				sum += w[t];
				sr += w[t] * a * rgba[i] / 255f;
				sg += w[t] * a * rgba[i + 1] / 255f;
				sb += w[t] * a * rgba[i + 2] / 255f;
				sa += w[t] * a;
			}
		}

		if ( sum > 0f )
		{
			sr /= sum;
			sg /= sum;
			sb /= sum;
			sa /= sum;
		}

		var strength = Math.Clamp( stroke.Strength * stroke.A, 0f, 1f );

		if ( state.Loaded && strength > 0f )
		{
			for ( var y = bounds.MinY; y <= bounds.MaxY; y++ )
			{
				for ( var x = bounds.MinX; x <= bounds.MaxX; x++ )
				{
					var t = y * resolution + x;
					var k = w[t] * strength;

					if ( k <= 0f )
						continue;

					var i = t * 4;
					var da = rgba[i + 3] / 255f;

					// Premultiplied lerp toward the carried paint, divided back out to straight alpha.
					var oa = da + (state.A - da) * k;
					var or = rgba[i] / 255f * da + (state.R - rgba[i] / 255f * da) * k;
					var og = rgba[i + 1] / 255f * da + (state.G - rgba[i + 1] / 255f * da) * k;
					var ob = rgba[i + 2] / 255f * da + (state.B - rgba[i + 2] / 255f * da) * k;

					if ( oa > 1e-6f )
						canvas.Write( x, y, ToByte( or / oa ), ToByte( og / oa ), ToByte( ob / oa ), ToByte( oa ) );
					else
						canvas.Write( x, y, rgba[i], rgba[i + 1], rgba[i + 2], 0 );

					if ( k > coverage[t] )
						coverage[t] = k;
				}
			}
		}

		if ( !state.Loaded )
		{
			(state.R, state.G, state.B, state.A) = (sr, sg, sb, sa);
			state.Loaded = true;
		}
		else
		{
			state.R += (sr - state.R) * SmudgePickup;
			state.G += (sg - state.G) * SmudgePickup;
			state.B += (sb - state.B) * SmudgePickup;
			state.A += (sa - state.A) * SmudgePickup;
		}

		for ( var y = bounds.MinY; y <= bounds.MaxY; y++ )
			Array.Clear( w, y * resolution + bounds.MinX, bounds.MaxX - bounds.MinX + 1 );

		return bounds;
	}

	/// <summary>Texel radius of the blur kernel at <paramref name="resolution"/>: 1 up to 256, then a
	/// texel more per 256, so the softening reads about the same on the body at every resolution.</summary>
	public static int BlurKernelRadius( int resolution ) => Math.Clamp( resolution / 256, 1, 8 );

	/// <summary>Walk a blur stroke's dabs in order over <paramref name="canvas"/> (see
	/// <see cref="PaintStroke.Blur"/>). Leaves <paramref name="coverage"/> as the stroke's max weight,
	/// like every other stroke, so the replay's erase tracking sees a blur as paint.</summary>
	internal static void BlurStroke( PaintCanvas canvas, PaintStroke stroke, PolyMesh mesh, MeshBVH bvh,
		float[] coverage, int resolution, List<int> faces, Func<Vec3, Vec3, float> pattern )
	{
		if ( stroke is null || stroke.Path.Count == 0 )
			return;

		var state = new BlurState( new float[resolution * resolution] );

		foreach ( var point in stroke.Path )
			BlurDab( canvas, stroke, mesh, bvh, coverage, resolution, faces, pattern, state, point.Position, point.Normal );
	}

	/// <summary>A blur dab's scratch: the falloff weights (left zeroed after every dab) and a
	/// premultiplied copy of the canvas under the dab, grown on demand and kept between dabs.</summary>
	internal sealed class BlurState
	{
		public readonly float[] Weights;
		public float[] Snapshot = Array.Empty<float>();
		public float[] Kernel;
		public int KernelRadius = -1;

		public BlurState( float[] weights ) => Weights = weights;
	}

	/// <summary>One blur dab: snapshot the paint under the brush, then pull each texel the dab
	/// reaches toward the gaussian average of its in-footprint neighbours, by weight times strength.
	/// Premultiplied, so bare texels thin the paint out rather than darkening it. Returns the texels
	/// it wrote.</summary>
	internal static TexelBounds BlurDab( PaintCanvas canvas, PaintStroke stroke, PolyMesh mesh, MeshBVH bvh,
		float[] coverage, int resolution, List<int> faces, Func<Vec3, Vec3, float> pattern, BlurState state,
		Vec3 point, Vec3 normal )
	{
		var w = state.Weights;
		var bounds = StampDab( mesh, bvh, w, resolution, point, normal, stroke.Radius, 1f, stroke.Falloff, faces, pattern, null );

		if ( !bounds.Any )
			return bounds;

		var strength = Math.Clamp( stroke.Strength * stroke.A, 0f, 1f );

		if ( strength > 0f )
		{
			var kr = BlurKernelRadius( resolution );

			if ( state.KernelRadius != kr )
			{
				// Gaussian, sigma half the radius plus a little so radius 1 is still a real blur.
				var sigma = kr * 0.5f + 0.35f;
				var size = kr * 2 + 1;
				state.Kernel = new float[size * size];

				for ( var dy = -kr; dy <= kr; dy++ )
					for ( var dx = -kr; dx <= kr; dx++ )
						state.Kernel[(dy + kr) * size + dx + kr] = MathF.Exp( -(dx * dx + dy * dy) / (2f * sigma * sigma) );

				state.KernelRadius = kr;
			}

			var bw = bounds.MaxX - bounds.MinX + 1;
			var bh = bounds.MaxY - bounds.MinY + 1;

			if ( state.Snapshot.Length < bw * bh * 4 )
				state.Snapshot = new float[bw * bh * 4];

			var snap = state.Snapshot;
			var rgba = canvas.Rgba;

			for ( var y = bounds.MinY; y <= bounds.MaxY; y++ )
			{
				for ( var x = bounds.MinX; x <= bounds.MaxX; x++ )
				{
					var i = (y * resolution + x) * 4;
					var j = ((y - bounds.MinY) * bw + x - bounds.MinX) * 4;
					var a = rgba[i + 3] / 255f;
					snap[j] = rgba[i] / 255f * a;
					snap[j + 1] = rgba[i + 1] / 255f * a;
					snap[j + 2] = rgba[i + 2] / 255f * a;
					snap[j + 3] = a;
				}
			}

			var kernel = state.Kernel;
			var ks = kr * 2 + 1;

			for ( var y = bounds.MinY; y <= bounds.MaxY; y++ )
			{
				for ( var x = bounds.MinX; x <= bounds.MaxX; x++ )
				{
					var t = y * resolution + x;
					var k = w[t] * strength;

					if ( k <= 0f )
						continue;

					float sum = 0f, ar = 0f, ag = 0f, ab = 0f, aa = 0f;
					var y0 = Math.Max( bounds.MinY, y - kr );
					var y1 = Math.Min( bounds.MaxY, y + kr );
					var x0 = Math.Max( bounds.MinX, x - kr );
					var x1 = Math.Min( bounds.MaxX, x + kr );

					for ( var ny = y0; ny <= y1; ny++ )
					{
						for ( var nx = x0; nx <= x1; nx++ )
						{
							if ( w[ny * resolution + nx] <= 0f )
								continue;

							var kw = kernel[(ny - y + kr) * ks + nx - x + kr];
							var j = ((ny - bounds.MinY) * bw + nx - bounds.MinX) * 4;
							sum += kw;
							ar += kw * snap[j];
							ag += kw * snap[j + 1];
							ab += kw * snap[j + 2];
							aa += kw * snap[j + 3];
						}
					}

					if ( sum <= 0f )
						continue;

					var c = ((y - bounds.MinY) * bw + x - bounds.MinX) * 4;
					var oa = snap[c + 3] + (aa / sum - snap[c + 3]) * k;
					var or = snap[c] + (ar / sum - snap[c]) * k;
					var og = snap[c + 1] + (ag / sum - snap[c + 1]) * k;
					var ob = snap[c + 2] + (ab / sum - snap[c + 2]) * k;
					var i = t * 4;

					if ( oa > 1e-6f )
						canvas.Write( x, y, ToByte( or / oa ), ToByte( og / oa ), ToByte( ob / oa ), ToByte( oa ) );
					else if ( rgba[i + 3] != 0 )
						canvas.Write( x, y, rgba[i], rgba[i + 1], rgba[i + 2], 0 );

					if ( k > coverage[t] )
						coverage[t] = k;
				}
			}
		}

		for ( var y = bounds.MinY; y <= bounds.MaxY; y++ )
			Array.Clear( w, y * resolution + bounds.MinX, bounds.MaxX - bounds.MinX + 1 );

		return bounds;
	}

	/// <summary>Stamp one stroke, resolving each newly covered texel's clone source as it goes when
	/// <paramref name="clone"/> is set.</summary>
	internal static void StampStroke( PaintStroke stroke, PolyMesh mesh, MeshBVH bvh,
		float[] coverage, int resolution, List<int> faces, Func<Vec3, Vec3, float> pattern, CloneSampler clone )
	{
		if ( stroke is null || stroke.Path.Count == 0 )
			return;

		// Coverage folds the stroke's own opacity into its strength, so the alpha the composite sees
		// is "how much paint arrives here" and the colour has no alpha of its own to double-count.
		var coverageScale = stroke.Strength * stroke.A;

		foreach ( var point in stroke.Path )
			StampDab( mesh, bvh, coverage, resolution, point.Position, point.Normal,
				stroke.Radius, coverageScale, stroke.Falloff, faces, pattern, clone );
	}

	/// <summary>
	/// One dab into a coverage buffer: the brush sphere at <paramref name="point"/>, rasterised into
	/// the faces it reaches, each texel's coverage raised to the dab's weight where that is higher.
	/// Returns the texel bounds it actually touched, so the live session can recompose and re-upload
	/// only that region.
	///
	/// THE 3D FOOTPRINT IS THE WHOLE DESIGN. A 2D disc in UV space cannot know a seam exists; a sphere
	/// in object space touches faces on both sides of one and paints them both for free. The normal
	/// gate is the other half of the same idea — a dab on one side of a thin wall must not bleed
	/// through to the far face, which is rejected by comparing its normal to the recorded surface
	/// normal.
	///
	/// MAX, NOT SUM, is what makes a stroke lay down like paint. Two dabs of one stroke overlapping
	/// must not darken each other — the coverage is the strongest the brush pressed anywhere, and the
	/// stroke is composited from that once.
	/// </summary>
	internal static TexelBounds StampDab( PolyMesh mesh, MeshBVH bvh, float[] coverage, int resolution,
		Vec3 point, Vec3 normal, float radius, float strength, BrushFalloff falloff, List<int> faces ) =>
		StampDab( mesh, bvh, coverage, resolution, point, normal, radius, strength, falloff, faces, null );

	/// <summary>The patterned dab: identical to <see cref="StampDab(PolyMesh, MeshBVH, float[], int, Vec3, Vec3, float, float, BrushFalloff, List{int})"/>
	/// except each texel's weight is multiplied by <paramref name="pattern"/> at that texel's
	/// rebuilt surface point and face normal, when a pattern is given.</summary>
	internal static TexelBounds StampDab( PolyMesh mesh, MeshBVH bvh, float[] coverage, int resolution,
		Vec3 point, Vec3 normal, float radius, float strength, BrushFalloff falloff, List<int> faces,
		Func<Vec3, Vec3, float> pattern ) =>
		StampDab( mesh, bvh, coverage, resolution, point, normal, radius, strength, falloff, faces, pattern, null );

	/// <summary>The dab with a clone hook: every texel it gives weight to has its clone source
	/// resolved from the texel's own rebuilt surface point, once — the first dab to reach a texel
	/// decides it, and the live session and the replay walk the dabs in the same order.</summary>
	internal static TexelBounds StampDab( PolyMesh mesh, MeshBVH bvh, float[] coverage, int resolution,
		Vec3 point, Vec3 normal, float radius, float strength, BrushFalloff falloff, List<int> faces,
		Func<Vec3, Vec3, float> pattern, CloneSampler clone )
	{
		if ( radius <= 0f || strength <= 0f )
			return default;

		bvh.FacesInRadius( mesh, point, radius, faces );

		var n = normal.LengthSquared >= 0.5f ? normal.Normal : new Vec3( 0, 0, 1 );
		var bounds = default( TexelBounds );

		foreach ( var fi in faces )
		{
			var face = mesh.Faces[fi];

			if ( face.Count < 3 || face.UVs is null || face.UVs.Length != face.Count )
				continue;

			var faceNormal = mesh.FaceNormal( face );

			// The far side of a thin wall points away from the brush; a face exactly perpendicular to
			// it belongs to the neighbouring face's own dab, not this one.
			if ( Vec3.Dot( faceNormal, n ) <= 0f )
				continue;

			var corners = new List<Vec3>( face.Count );

			for ( var c = 0; c < face.Count; c++ )
				corners.Add( mesh.Positions[face.Indices[c]] );

			foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
			{
				var p0 = corners[ia];
				var p1 = corners[ib];
				var p2 = corners[ic];

				var u0 = face.UVs[ia];
				var u1 = face.UVs[ib];
				var u2 = face.UVs[ic];

				NormalBake.Rasterise( u0, u1, u2, resolution, resolution, ( x, y, wa, wb, wc ) =>
				{
					// The barycentrics Rasterise hands back let a texel's 3D position be rebuilt from
					// its three corners, so the falloff can be measured in object space rather than in
					// UV space — the same distance that made the brush one consistent size everywhere.
					// The pattern is measured at that same rebuilt point, which is what keeps it
					// continuous across UV seams: the point, not the texel's UV, decides the value.
					var p = p0 * wa + p1 * wb + p2 * wc;
					var t = (p - point).Length / radius;

					// The rasteriser walks every texel of each face the BVH found, not just the
					// ones inside the brush; Falloff clamps t, so without this a Constant dab
					// filled whole faces edge to edge.
					if ( t > 1f )
						return;

					var weight = Brush.Falloff( t, falloff ) * strength;

					if ( pattern is not null )
						weight *= pattern( p, faceNormal );

					if ( weight <= 0f )
						return;

					var index = y * resolution + x;

					clone?.Resolve( index, p );

					if ( weight > coverage[index] )
					{
						coverage[index] = weight;
						bounds = bounds.With( x, y );
					}
				} );
			}
		}

		return bounds;
	}

	/// <summary>Composite a coverage buffer into a canvas once, at the stroke's colour. The coverage
	/// is the stroke's per-texel alpha, already capped at the maximum any of its dabs reached.</summary>
	internal static void Composite( PaintCanvas canvas, float[] coverage, byte r, byte g, byte b )
	{
		for ( var y = 0; y < canvas.Height; y++ )
		{
			for ( var x = 0; x < canvas.Width; x++ )
			{
				var weight = coverage[y * canvas.Width + x];

				if ( weight > 0f )
					canvas.Blend( x, y, r, g, b, weight );
			}
		}
	}

	/// <summary>Take a coverage buffer back out of a canvas's alpha, once — the mirror of
	/// <see cref="Composite"/>. Colour is left alone; see PaintCanvas.Erase for why straight alpha
	/// means an erase is entirely a statement about how much paint is there.</summary>
	internal static void Erase( PaintCanvas canvas, float[] coverage )
	{
		for ( var y = 0; y < canvas.Height; y++ )
		{
			for ( var x = 0; x < canvas.Width; x++ )
			{
				var weight = coverage[y * canvas.Width + x];

				if ( weight > 0f )
					canvas.Erase( x, y, weight );
			}
		}
	}

	/// <summary>A float colour channel to a byte, the same rounding the canvas blend uses.</summary>
	internal static byte ToByte( float v ) => (byte)Math.Clamp( MathF.Round( v * 255f ), 0f, 255f );
}

/// <summary>
/// Where a clone stroke's texels read their paint from: for each texel, the canvas colour at the
/// surface point nearest <c>texel point + offset</c>, as the canvas stood before the stroke.
///
/// RESOLVED ONCE PER TEXEL, LAZILY. A stroke touches a few thousand texels and each source is a
/// nearest-surface query, so they are found as the dabs first reach them and kept — the live
/// session can then recompose a texel any number of times for the price of one query.
///
/// THE SOURCE CANVAS MUST NOT CHANGE UNDER IT. The replay hands it the canvas before the stroke is
/// applied and the session hands it the committed canvas, which a stroke in flight never writes,
/// so a clone never reads its own paint back.
/// </summary>
public sealed class CloneSampler
{
	readonly PolyMesh _mesh;
	readonly MeshBVH _bvh;
	readonly PaintCanvas _source;
	readonly Vec3 _offset;
	readonly float _reach;

	// Per texel: 0 = not asked yet, 1 = resolved to a colour, 2 = no source (miss or bare surface).
	readonly byte[] _state;
	readonly byte[] _rgba;

	public CloneSampler( PolyMesh mesh, MeshBVH bvh, PaintCanvas source, Vec3 offset, float radius )
	{
		_mesh = mesh ?? throw new ArgumentNullException( nameof( mesh ) );
		_bvh = bvh ?? throw new ArgumentNullException( nameof( bvh ) );
		_source = source ?? throw new ArgumentNullException( nameof( source ) );
		_offset = offset;

		// How far off the surface the offset point may land and still count. A flat offset lands on
		// the surface; a curved one drifts off it by roughly the curvature times the offset squared,
		// so the reach grows with both the brush and the jump.
		_reach = MathF.Max( MathF.Max( radius, 1e-4f ) * 2f, offset.Length * 0.5f );

		var n = source.Width * source.Height;
		_state = new byte[n];
		_rgba = new byte[n * 4];
	}

	/// <summary>Find and keep texel <paramref name="index"/>'s source, if it has none yet.</summary>
	public void Resolve( int index, Vec3 point )
	{
		if ( _state[index] != 0 )
			return;

		_state[index] = 2;

		var hit = _bvh.NearestSurface( _mesh, point + _offset, _reach );

		if ( hit is not { } h || !TexelAt( h, out var tx, out var ty ) )
			return;

		var s = (ty * _source.Width + tx) * 4;

		if ( _source.Rgba[s + 3] == 0 )
			return;

		var d = index * 4;
		_rgba[d] = _source.Rgba[s];
		_rgba[d + 1] = _source.Rgba[s + 1];
		_rgba[d + 2] = _source.Rgba[s + 2];
		_rgba[d + 3] = _source.Rgba[s + 3];
		_state[index] = 1;
	}

	/// <summary>The resolved source for a texel: straight colour and its alpha in 0..1. False when the
	/// texel was never reached or found nothing to copy.</summary>
	public bool TryGet( int index, out byte r, out byte g, out byte b, out float a )
	{
		if ( _state[index] != 1 )
		{
			r = g = b = 0;
			a = 0f;
			return false;
		}

		var d = index * 4;
		r = _rgba[d];
		g = _rgba[d + 1];
		b = _rgba[d + 2];
		a = _rgba[d + 3] / 255f;
		return true;
	}

	/// <summary>The texel under a surface point: the face's triangle nearest the point, its
	/// barycentrics, the interpolated UV, then the texel whose centre that UV falls in — the inverse
	/// of the rasteriser's <c>(x + 0.5) / width</c>.</summary>
	bool TexelAt( MeshHit hit, out int tx, out int ty )
	{
		tx = ty = 0;

		if ( hit.FaceIndex < 0 || hit.FaceIndex >= _mesh.Faces.Count )
			return false;

		var face = _mesh.Faces[hit.FaceIndex];

		if ( face.Count < 3 || face.UVs is null || face.UVs.Length != face.Count )
			return false;

		var corners = new List<Vec3>( face.Count );

		for ( var c = 0; c < face.Count; c++ )
			corners.Add( _mesh.Positions[face.Indices[c]] );

		var best = float.MaxValue;
		Vec2 uv = default;

		foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
		{
			var a = corners[ia];
			var b = corners[ib];
			var c = corners[ic];
			var q = MeshBVH.ClosestTriangle( hit.Point, a, b, c );
			var dist = (q - hit.Point).LengthSquared;

			if ( dist >= best )
				continue;

			best = dist;

			var (wa, wb, wc) = Barycentric( q, a, b, c );
			uv = face.UVs[ia] * wa + face.UVs[ib] * wb + face.UVs[ic] * wc;
		}

		if ( best == float.MaxValue )
			return false;

		tx = Math.Clamp( (int)MathF.Floor( uv.x * _source.Width ), 0, _source.Width - 1 );
		ty = Math.Clamp( (int)MathF.Floor( uv.y * _source.Height ), 0, _source.Height - 1 );
		return true;
	}

	static (float A, float B, float C) Barycentric( Vec3 p, Vec3 a, Vec3 b, Vec3 c )
	{
		var v0 = b - a;
		var v1 = c - a;
		var v2 = p - a;
		var d00 = Vec3.Dot( v0, v0 );
		var d01 = Vec3.Dot( v0, v1 );
		var d11 = Vec3.Dot( v1, v1 );
		var d20 = Vec3.Dot( v2, v0 );
		var d21 = Vec3.Dot( v2, v1 );
		var denom = d00 * d11 - d01 * d01;

		if ( MathF.Abs( denom ) < 1e-20f )
			return (1f, 0f, 0f);

		var v = (d11 * d20 - d01 * d21) / denom;
		var w = (d00 * d21 - d01 * d20) / denom;
		return (1f - v - w, v, w);
	}
}
