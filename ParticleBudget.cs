#nullable enable
using System;
using System.Globalization;

namespace Marionette.Desktop;

// P61 — budget / overdraw estimator for the particle editor HUD (wave 6).
//
// WHAT THIS IS: a pure editor-HUD estimator — data + math, no rendering,
// no UI, no GPU access, no sim dependency. The HUD calls it per frame (or
// per settings change) with live counts from a ParticleSystem and the
// render type of the layer that draws it, and displays the numbers.
//
// HONESTY RULE (read before quoting any number from this file): this is an
// ESTIMATE, not a GPU measurement. It does not read back the GPU, does not
// know the real framebuffer, and must never be presented as measured
// performance. Its value is comparability (same inputs -> same number) and
// early warning (over budget before the user renders 4,000,000 particles).
//
// FORMULA (pinned by tests, roster case first):
//   EffectiveCount    = ParticleCount x LodCountMultiplier
//   EffectiveMeanSize = MeanSize x LodSizeMultiplier
//   EffectiveMaxSize  = MaxSize x LodSizeMultiplier
//   CoveredArea       = EffectiveCount x EffectiveMeanSize^2 x OverdrawFactor
//   WorstCoveredArea  = EffectiveCount x EffectiveMaxSize^2 x OverdrawFactor
//   OverdrawRatio     = CoveredArea / ScreenArea
//   WorstOverdrawRatio = WorstCoveredArea / ScreenArea
// Roster example: 100 particles of size 10 on a 1000x1000 screen
// (ScreenArea = 1,000,000) with overdraw factor 1:
//   100 x 10^2 x 1 / 1,000,000 = 10,000 / 1,000,000 = 0.01.
// CoveredArea uses the MEAN size, so it is the expected coverage, not a
// bound: the true sum over particles of size^2 needs mean(size^2), which a
// mean+max summary does not carry. WorstCoveredArea (max size) is the
// upper bound under the same summary, shown alongside, never hidden.
//
// UNITS: counts are unitless. Sizes are in the same units as the layer's
// rendered size, and ScreenArea MUST be in the square of those same units —
// the estimator never converts and never invents a screen size. Two modes:
//   - Pixels: sizes are projected screen pixels, ScreenArea is viewport
//     pixels (width x height). This is the comparable mode: feed it
//     projected sizes and every scene compares fairly.
//   - Inches: sizes are 3D kernel inches (ParticleSystem units) and
//     ScreenArea is a caller-supplied world-space area in in^2 (e.g. the
//     view-frustum cross-section at the particles' depth). The ratio stays
//     dimensionless, but the caller owns the area definition.
// The SizeUnits label travels with the result so a HUD never mixes modes
// silently. A 3D caller that wants pixel-comparable numbers projects
// inches -> pixels with its own camera first and uses Pixels mode; this
// file invents no camera, no FOV, no distance.
//
// DRAW COUNTS — what the LAYERS actually draw (ParticleRenderLayers.cs,
// plus ParticleLayer billboards), not an imagined renderer:
//   - Billboard: 1 quad per particle (2 triangles, 4 vertices).
//   - Strand: (StrandPoints - 1) ribbon quads per particle — an UPPER
//     BOUND, because ParticleStrandLayer skips zero-length segments
//     (a trail before birth repeats the spawn position), so a real draw
//     is <= this estimate. StrandPoints mirrors ParticleSystem's rule.
//   - MeshInstance: 0 quads. It counts INSTANCES, not billboards:
//     InstanceCount = particle count, Triangles = count x
//     TemplateTriangleCount, Vertices = Triangles x 3. Without a template
//     triangle count the geometry is unknowable, so MeshInstance with
//     TemplateTriangleCount < 1 fails loudly instead of guessing.
// Fade-out shrink (layers fade particles toward zero at end of life) is
// NOT modelled: the estimate is for live particles at authored size, and
// says so. A faded frame draws less, never more.
//
// LOD (P35, ParticleLod.cs, LANDED): by default both LOD multipliers are
// 1, so the estimate is the PRE-LOD count — raw, comparable, and never a
// baked-in LOD assumption. Pass P35's RateMultiplierFor(distance) and
// SizeMultiplierFor(distance) as LodCountMultiplier / LodSizeMultiplier
// for a LOD-scaled estimate. Ranges mirror P35: count in [0, 1] (LOD only
// ever reduces work), size in [0, 2] (a distant effect may be drawn
// slightly larger to stay legible).
//
// VERDICT: thresholds are INPUTS, never hardcoded magic numbers.
// Defaults exist (NearRatio = 1.0 = the screen covered once on average,
// OverRatio = 2.0 = covered twice) and are documented here, not hidden;
// the caller overrides them. Rule, pinned at the boundaries:
//   ratio < NearRatio -> Under; NearRatio <= ratio < OverRatio -> Near;
//   ratio >= OverRatio -> Over. So at-threshold is Near at the Near
//   threshold and Over at the Over threshold. The verdict is computed
//   from OverdrawRatio (mean size), not the worst case, so a few huge
//   particles alone do not flip it; WorstOverdrawRatio is shown beside it.
//
// LARGEST CONTRIBUTOR: which input drives the estimate most, as a HUD
// hint ("too many particles" vs "particles too big" vs "too much
// overlap"). Each factor is scored as a multiple of its smallest
// meaningful value — count as a multiple of 1 particle, size as
// EffectiveMeanSize^2 (a multiple of a 1-unit^2 footprint), overdraw as a
// multiple of 1 (no overlap adjustment) — and the largest score wins.
// Ties break Count, then Size, then Overdraw (documented, tested).
// None when nothing is covered (zero particles, or a zero ratio from a
// zero size / zero overdraw factor). NOTE: the size score is in input
// units, so contributor verdicts compare fairly only within one units
// mode; for cross-scene comparison use Pixels mode. This is a hint, not
// a physical fact, and the HUD should present it as one.
//
// LOUD FAILURES: negative counts/sizes/areas, NaN or infinite inputs,
// MaxSize < MeanSize, an unknown render-type value, Strand without
// StrandPoints >= 2, MeshInstance without a template triangle count,
// out-of-range LOD multipliers, and invalid thresholds all throw
// instead of silently producing a plausible-looking number. Zero
// particles is NOT a failure: it is zero cost, never NaN.
//
// OUT OF SCOPE: GPU profiling/measurement, LOD itself (P35), render-layer
// changes, HUD widgets, save/serialization. No System.Random anywhere;
// the estimator is deterministic: same inputs -> bit-identical outputs.

/// <summary>Units of the size inputs. A label only — the estimator never
/// converts between modes; see the file header for what ScreenArea must
/// be in each mode.</summary>
public enum ParticleBudgetUnits { Pixels, Inches }

/// <summary>Budget verdict against the caller-supplied thresholds.</summary>
public enum ParticleBudgetVerdict { Under, Near, Over }

/// <summary>Which input drives the estimate most. None when nothing is
/// covered (zero particles or a zero overdraw ratio).</summary>
public enum ParticleBudgetContributor { None, Count, Size, Overdraw }

/// <summary>Verdict thresholds. Defaults (documented, overridable):
/// NearRatio = 1.0 (screen covered once on average), OverRatio = 2.0
/// (covered twice). Must satisfy 0 &lt; NearRatio &lt; OverRatio.</summary>
public sealed class ParticleBudgetThresholds
{
	public double NearRatio = 1.0;
	public double OverRatio = 2.0;

	public void Validate()
	{
		if ( !double.IsFinite( NearRatio ) || !double.IsFinite( OverRatio ) )
			throw new ArgumentException( "Budget thresholds must be finite." );
		if ( NearRatio <= 0 )
			throw new ArgumentOutOfRangeException( nameof( NearRatio ), "Near threshold must be > 0." );
		if ( !( OverRatio > NearRatio ) )
			throw new ArgumentException( "Over threshold must be strictly greater than the Near threshold." );
	}
}

/// <summary>Inputs to the estimator. ScreenArea has NO default: it is 0
/// until the caller supplies it, and 0 fails validation loudly, so a
/// screen size can never be silently assumed.</summary>
public sealed class ParticleBudgetInput
{
	/// <summary>Live particle count. Must be &gt;= 0.</summary>
	public int ParticleCount;

	/// <summary>Mean particle size, in SizeUnits. Must be finite, &gt;= 0.</summary>
	public double MeanSize;

	/// <summary>Max particle size, in SizeUnits. Must be finite, &gt;= 0
	/// and &gt;= MeanSize (a distribution summary with max below mean is
	/// inconsistent and is rejected).</summary>
	public double MaxSize;

	/// <summary>Viewport/screen area in SizeUnits^2. EXPLICIT input:
	/// pixels (width x height) in Pixels mode, a caller-defined area in
	/// in^2 in Inches mode. Must be finite and &gt; 0.</summary>
	public double ScreenArea;

	/// <summary>Which layer draws these particles. Unknown enum values
	/// fail loudly.</summary>
	public ParticleRenderType RenderType = ParticleRenderType.Billboard;

	/// <summary>Trail points per particle, Strand only (mirrors
	/// ParticleSystem: 2..256). Ignored for other render types.</summary>
	public int StrandPoints;

	/// <summary>Triangles in the instance template mesh, MeshInstance
	/// only. Must be &gt;= 0; &lt; 1 fails loudly for MeshInstance.
	/// Ignored for other render types.</summary>
	public int TemplateTriangleCount;

	/// <summary>Per-particle overlap factor. 1 = no overlap adjustment;
	/// &gt; 1 = overlapping layers; 0 = covers nothing. Must be finite
	/// and &gt;= 0.</summary>
	public double OverdrawFactor = 1.0;

	/// <summary>Optional LOD scaling (P35): rate multiplier, [0, 1].
	/// Default 1 = pre-LOD estimate.</summary>
	public double LodCountMultiplier = 1.0;

	/// <summary>Optional LOD scaling (P35): size multiplier, [0, 2].
	/// Default 1 = pre-LOD estimate.</summary>
	public double LodSizeMultiplier = 1.0;

	/// <summary>Units label for sizes (and therefore ScreenArea).
	/// Travels with the result; never converted.</summary>
	public ParticleBudgetUnits SizeUnits = ParticleBudgetUnits.Pixels;

	/// <summary>Verdict thresholds. Null fails loudly.</summary>
	public ParticleBudgetThresholds Thresholds = new();

	public void Validate()
	{
		if ( ParticleCount < 0 )
			throw new ArgumentOutOfRangeException( nameof( ParticleCount ), "Particle count must be >= 0." );
		if ( !double.IsFinite( MeanSize ) || MeanSize < 0 )
			throw new ArgumentOutOfRangeException( nameof( MeanSize ), "Mean size must be finite and >= 0." );
		if ( !double.IsFinite( MaxSize ) || MaxSize < 0 )
			throw new ArgumentOutOfRangeException( nameof( MaxSize ), "Max size must be finite and >= 0." );
		if ( MaxSize < MeanSize )
			throw new ArgumentException( "Max size must be >= mean size." );
		if ( !double.IsFinite( ScreenArea ) || ScreenArea <= 0 )
			throw new ArgumentOutOfRangeException( nameof( ScreenArea ), "Screen area must be finite and > 0; it is an explicit input, never assumed." );
		if ( !double.IsFinite( OverdrawFactor ) || OverdrawFactor < 0 )
			throw new ArgumentOutOfRangeException( nameof( OverdrawFactor ), "Overdraw factor must be finite and >= 0." );
		if ( !double.IsFinite( LodCountMultiplier ) || LodCountMultiplier < 0 || LodCountMultiplier > 1 )
			throw new ArgumentOutOfRangeException( nameof( LodCountMultiplier ), "LOD count multiplier must be in [0, 1] (P35 range)." );
		if ( !double.IsFinite( LodSizeMultiplier ) || LodSizeMultiplier < 0 || LodSizeMultiplier > 2 )
			throw new ArgumentOutOfRangeException( nameof( LodSizeMultiplier ), "LOD size multiplier must be in [0, 2] (P35 range)." );
		switch ( RenderType )
		{
			case ParticleRenderType.Billboard:
				break;
			case ParticleRenderType.Strand:
				if ( StrandPoints < 2 || StrandPoints > 256 )
					throw new ArgumentOutOfRangeException( nameof( StrandPoints ), "Strand render type needs StrandPoints in 2..256 (ParticleSystem rule)." );
				break;
			case ParticleRenderType.MeshInstance:
				if ( TemplateTriangleCount < 1 )
					throw new ArgumentOutOfRangeException( nameof( TemplateTriangleCount ), "MeshInstance render type needs a template triangle count >= 1; the geometry is otherwise unknowable." );
				break;
			default:
				throw new ArgumentOutOfRangeException( nameof( RenderType ), $"Unknown particle render type value {(int)RenderType}." );
		}
		if ( Thresholds is null )
			throw new ArgumentNullException( nameof( Thresholds ) );
		Thresholds.Validate();
	}
}

/// <summary>Estimator outputs: plain numbers a HUD can display, plus the
/// verdict and the largest contributor. Deterministic: same inputs give
/// bit-identical outputs.</summary>
public sealed class ParticleBudgetEstimate
{
	/// <summary>Honesty note for any surface that displays these numbers.</summary>
	public const string HonestyNote = "Estimate only — not a GPU measurement. Comparable across identical inputs; not measured performance.";

	public double EffectiveParticleCount;
	public double EffectiveMeanSize;
	public double EffectiveMaxSize;
	public double QuadsPerParticle;
	public double QuadCount;
	public double TriangleCount;
	public double VertexCount;

	/// <summary>MeshInstance only; 0 for billboards and strands, which
	/// count quads, not instances.</summary>
	public double InstanceCount;

	public double CoveredArea;
	public double WorstCoveredArea;
	public double OverdrawRatio;
	public double WorstOverdrawRatio;
	public ParticleBudgetVerdict Verdict;
	public ParticleBudgetContributor LargestContributor;
	public ParticleBudgetUnits SizeUnits;

	/// <summary>One-line plain-English summary naming the draw unit
	/// (quads / ribbon quads / instances), the overdraw ratio, and the
	/// verdict. Deterministic formatting (invariant culture).</summary>
	public string Summary = "";
}

/// <summary>Pure editor-HUD estimator. See the file header for the
/// formula, units, verdict rule, contributor rule, and honesty rule.</summary>
public static class ParticleBudget
{
	public static ParticleBudgetEstimate Estimate( ParticleBudgetInput input )
	{
		if ( input is null ) throw new ArgumentNullException( nameof( input ) );
		input.Validate();

		double count = input.ParticleCount * input.LodCountMultiplier;
		double mean = input.MeanSize * input.LodSizeMultiplier;
		double max = input.MaxSize * input.LodSizeMultiplier;

		double quadsPer, trisPer, vertsPer, instances;
		switch ( input.RenderType )
		{
			case ParticleRenderType.Billboard:
				quadsPer = 1; trisPer = 2; vertsPer = 4; instances = 0;
				break;
			case ParticleRenderType.Strand:
				// Upper bound: the strand layer skips zero-length
				// segments, so a real draw is <= this.
				quadsPer = input.StrandPoints - 1; trisPer = 2 * quadsPer; vertsPer = 4 * quadsPer; instances = 0;
				break;
			case ParticleRenderType.MeshInstance:
				quadsPer = 0; trisPer = input.TemplateTriangleCount; vertsPer = 3 * trisPer; instances = count;
				break;
			default:
				throw new ArgumentOutOfRangeException( nameof( input.RenderType ), $"Unknown particle render type value {(int)input.RenderType}." );
		}

		double covered = count * mean * mean * input.OverdrawFactor;
		double worst = count * max * max * input.OverdrawFactor;
		double ratio = covered / input.ScreenArea;
		double worstRatio = worst / input.ScreenArea;

		ParticleBudgetVerdict verdict;
		if ( ratio < input.Thresholds.NearRatio ) verdict = ParticleBudgetVerdict.Under;
		else if ( ratio < input.Thresholds.OverRatio ) verdict = ParticleBudgetVerdict.Near;
		else verdict = ParticleBudgetVerdict.Over;

		// Largest contributor: each factor as a multiple of its smallest
		// meaningful value (1 particle, a 1-unit^2 footprint, no-overlap
		// factor 1); largest wins; ties Count, then Size, then Overdraw.
		// None when nothing is covered.
		ParticleBudgetContributor contributor;
		if ( count <= 0 || ratio <= 0 )
			contributor = ParticleBudgetContributor.None;
		else
		{
			double countScore = count;
			double sizeScore = mean * mean;
			double overdrawScore = input.OverdrawFactor;
			if ( countScore >= sizeScore && countScore >= overdrawScore ) contributor = ParticleBudgetContributor.Count;
			else if ( sizeScore >= overdrawScore ) contributor = ParticleBudgetContributor.Size;
			else contributor = ParticleBudgetContributor.Overdraw;
		}

		string drawUnit = input.RenderType switch
		{
			ParticleRenderType.Billboard => $"{count.ToString( "G", CultureInfo.InvariantCulture )} billboard quads",
			ParticleRenderType.Strand => $"{( count * quadsPer ).ToString( "G", CultureInfo.InvariantCulture )} ribbon quads",
			ParticleRenderType.MeshInstance => $"{instances.ToString( "G", CultureInfo.InvariantCulture )} instances x {input.TemplateTriangleCount} triangles",
			_ => "unknown draw",
		};

		return new ParticleBudgetEstimate
		{
			EffectiveParticleCount = count,
			EffectiveMeanSize = mean,
			EffectiveMaxSize = max,
			QuadsPerParticle = quadsPer,
			QuadCount = count * quadsPer,
			TriangleCount = count * trisPer,
			VertexCount = count * vertsPer,
			InstanceCount = instances,
			CoveredArea = covered,
			WorstCoveredArea = worst,
			OverdrawRatio = ratio,
			WorstOverdrawRatio = worstRatio,
			Verdict = verdict,
			LargestContributor = contributor,
			SizeUnits = input.SizeUnits,
			Summary = $"{drawUnit} · overdraw {ratio.ToString( "G", CultureInfo.InvariantCulture )} · {verdict}",
		};
	}
}
