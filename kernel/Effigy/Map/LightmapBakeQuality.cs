// Hammer Desktop P3: native radiosity-quality bake (direct + bounced, AO, HDR,
// static-prop lighting). Board task hammer-radiosity-quality-bake.
//
// WHAT THIS IS. The quality entry point on the progressive bake: a
// <see cref="LightmapBakeJob"/> runs its low-res-first progressive bake
// (LightmapBake.cs, P2), and THIS file is how the same job runs the
// radiosity-quality bake — <see cref="RadiosityBake"/> (the luxel-level
// multi-bounce radiosity engine: shadow-tested direct, Jacobi gather over
// luxel patches with visibility-tested, energy-conserving form factors,
// Hammersley ambient occlusion, HDR linear luxels, static-prop vertex
// lighting, multi-page atlas packing with a padding gutter, and the 1K
// GPU test-bake trust rule). No second radiosity evaluator lives here: every
// method below is a thin, documented route into RadiosityBake, plus the two
// seams that only the progressive bake can provide — the job's faces/lights
// and the cross-check against its refined result.
//
// WHY A SEPARATE FILE. kernel/Effigy/Map/LightmapBake.cs is leased to the
// prop-lighting worker (muse-3-desktop-hammer-84, hub lock since 16:33
// 2026-11-13) — one writer per shared file, so the P3 quality options and
// methods do NOT go in that file in this ticket. The final integrator folds
// the settings mapping (<see cref="ToRadiositySettings"/>) into
// LightmapBakeOptions when the lease frees; until then this file is the
// quality API and it must not be bypassed by calling RadiosityBake directly
// from new callers. No UI, no IO, no GL: pure functions over faces + lights,
// same contract as the engine (never throws for a bake failure or a cancel).
//
// SETTINGS MAPPING. The progressive job's options do not carry AO or quality
// fields, so <see cref="BakeRadiosity"/> takes an explicit
// <see cref="RadiositySettings"/>: pass
// RadiositySettings.Preset(Quality) for the full-quality bake. When settings
// is null, <see cref="ToRadiositySettings"/> maps the job's options (grid cap
// and bounce count only; AO off) so the parameterless call reproduces the
// progressive job's resolution/bounce choices at radiosity quality.
//
// UNITS. Kernel inches, Z-up. Colours LINEAR RGB, HDR (may exceed 1).
// Deterministic: RadiosityBake is deterministic, and the atlas pack and the
// cross-check sort by face id.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Effigy.Map;

/// <summary>
/// The radiosity-quality bake, reached from a progressive
/// <see cref="LightmapBakeJob"/>: multi-bounce radiosity, AO, HDR,
/// static-prop lighting, multi-page atlas packing, and the GPU 1K trust rule.
/// Thin routes into <see cref="RadiosityBake"/> — this file holds no light
/// transport of its own. See the file header for why it is separate from
/// LightmapBake.cs.
/// </summary>
public static class LightmapBakeQuality
{
	/// <summary>
	/// Map a progressive job's options to radiosity settings: the job's
	/// per-axis grid cap (clamped to the radiosity engine's 1..64 range) and
	/// its bounce count; AO off (the quality preset is what turns AO on —
	/// pass explicit settings for that). The returned settings are a fresh
	/// instance; changing them never mutates the job's options.
	/// </summary>
	public static RadiositySettings ToRadiositySettings( LightmapBakeOptions options )
	{
		if ( options is null ) throw new ArgumentNullException( nameof( options ) );
		return new RadiositySettings
		{
			Bounces = options.BouncePasses,
			AoRays = 0,
			MaxGridPerAxis = Math.Clamp( options.MaxGridPerAxis, 1, 64 ),
		};
	}

	/// <summary>
	/// The radiosity-quality bake for this job's faces + lights, via
	/// <see cref="RadiosityBake.Bake"/>: per-face HDR luxel grids with
	/// shadow-tested direct light, multi-bounce radiosity and (when the
	/// settings ask for it) ambient occlusion. Never throws for a bake
	/// failure or a cancel — those come back as
	/// <see cref="RadiosityResult.Error"/> /
	/// <see cref="RadiosityResult.Cancelled"/> with empty faces. Invalid
	/// arguments (a null job, bad settings) DO throw, naming the problem.
	/// Pass null <paramref name="settings"/> to bake at the job's own grid
	/// cap and bounce count with AO off (see
	/// <see cref="ToRadiositySettings"/>); pass
	/// <c>RadiositySettings.Preset(RadiosityQuality.Quality)</c> for the
	/// full-quality bake (5 bounces, 64 AO rays, grid cap 32).
	/// </summary>
	public static RadiosityResult BakeRadiosity( LightmapBakeJob job, RadiositySettings settings = null,
		IProgress<BakeProgress> progress = null, CancellationToken cancel = default )
	{
		if ( job is null ) throw new ArgumentNullException( nameof( job ) );
		settings ??= ToRadiositySettings( job.Options );
		return RadiosityBake.Bake( job.Faces, job.Lights, settings, progress, cancel );
	}

	/// <summary>
	/// Pack a quality bake's face grids onto atlas pages, via
	/// <see cref="RadiosityAtlasPacker"/>: deterministic shelf packing with
	/// a <paramref name="paddingTexels"/> gutter between charts AND at the
	/// page edges, so a chart's luxels never bleed into its neighbour's
	/// under bilinear filtering — the atlas improvement over the progressive
	/// bake's single-page 1-luxel-gutter pack (which throws when the shelves
	/// run out of room instead of spilling to a new page). Skipped (nodraw)
	/// faces get no chart. Throws for a null result or a bad atlas size;
	/// a failed bake packs to zero pages (its face list is empty).
	/// </summary>
	public static AtlasPackResult PackAtlas( RadiosityResult baked, int atlasSize, int paddingTexels = 2 )
	{
		if ( baked is null ) throw new ArgumentNullException( nameof( baked ) );
		var charts = new List<AtlasChart>();
		if ( baked.Faces is not null )
			foreach ( var face in baked.Faces )
				if ( !face.SkippedNoDraw )
					charts.Add( new AtlasChart( face.FaceId, face.Cols, face.Rows ) );
		return RadiosityAtlasPacker.Pack( charts, atlasSize, paddingTexels );
	}

	/// <summary>
	/// Light static-prop vertices from a finished quality bake: per-vertex
	/// direct (shadow-tested) + ambient + indirect sampled from nearby
	/// patches + AO at the vertex, HDR linear. Props have no lightmap, so
	/// they sample the baked radiosity instead. The faces are required
	/// because the bake result carries patch samples but not the occluder
	/// faces. Throws when <paramref name="baked"/> is null or a failed
	/// run, or a vertex is null or degenerate. See
	/// <see cref="RadiosityBake.LightPropVertices"/>.
	/// </summary>
	public static IReadOnlyList<Vec3> LightPropVertices( RadiosityResult baked,
		IReadOnlyList<RadiosityPropVertex> vertices, IReadOnlyList<LiveLight> lights,
		IReadOnlyList<LiveFace> faces, RadiositySettings settings = null ) =>
		RadiosityBake.LightPropVertices( baked, vertices, lights, faces, settings );

	/// <summary>
	/// Reinhard tone mapping for an HDR luxel: exposure, then c/(1+c) per
	/// component. The result is linear 0..1 (an atlas exporter still
	/// applies the sRGB encode). See
	/// <see cref="RadiosityBake.ToneMap(Vec3, float)"/>.
	/// </summary>
	public static Vec3 ToneMap( Vec3 hdr, float exposure = 1f ) =>
		RadiosityBake.ToneMap( hdr, exposure );

	/// <summary>
	/// The 1K test-bake trust rule for the quality path, same as
	/// <see cref="RadiosityBake.VerifyGpuTrust"/>: the GPU path is only
	/// trusted for a scene after its test-bake (1 bounce, AO off, grid cap
	/// 8, at most <see cref="RadiositySettings.TestMaxLuxels"/> luxels)
	/// matches the CPU reference within <paramref name="tolerance"/>
	/// (relative per-face average luxel difference). The returned verdict
	/// is the token <see cref="BakeGpuRadiosity"/> demands.
	/// </summary>
	public static GpuTrustResult VerifyGpuTrust( IRadiosityGpuBackend backend, LightmapBakeJob job,
		RadiositySettings settings = null, float tolerance = 0.05f )
	{
		if ( job is null ) throw new ArgumentNullException( nameof( job ) );
		return RadiosityBake.VerifyGpuTrust( backend, job.Faces, job.Lights, settings, tolerance );
	}

	/// <summary>
	/// The GPU path for the quality bake. Demands a passed 1K test-bake
	/// verification for THIS scene (<paramref name="trust"/> from
	/// <see cref="VerifyGpuTrust"/>): without one the bake is REFUSED
	/// with a named reason (no silent CPU fallback — the caller asked for
	/// the GPU and has not proved it trustworthy on this scene). With a
	/// passed verification, a backend that declines the final bake (or
	/// fails it) falls back to the CPU bake and says so in
	/// <see cref="RadiosityResult.Route"/>. The backend is the one
	/// installed on <see cref="RadiosityBake.GpuBackend"/>.
	/// </summary>
	public static RadiosityResult BakeGpuRadiosity( LightmapBakeJob job, RadiositySettings settings,
		GpuTrustResult trust, IProgress<BakeProgress> progress = null, CancellationToken cancel = default )
	{
		if ( job is null ) throw new ArgumentNullException( nameof( job ) );
		settings ??= ToRadiositySettings( job.Options );
		return RadiosityBake.BakeGpu( job.Faces, job.Lights, settings, trust, progress, cancel );
	}

	/// <summary>
	/// Mean / max absolute deviation of per-face average luxels between a
	/// quality bake and the progressive bake's refined result, by face id
	/// (same metric as <see cref="LightmapBakeJob.CompareAverages"/>: mean
	/// of the per-channel absolute differences, and the largest single
	/// channel difference). Only faces present in both results and skipped
	/// in neither are compared. Direct-only bakes agree closely on
	/// unoccluded scenes (same light model, same grid convention); with
	/// bounce and AO on, the quality bake is the reference and the two are
	/// expected to differ — the quality bake has luxel-level bounce and AO
	/// where the progressive bake has a uniform per-face bounce factor.
	/// </summary>
	public static Dictionary<string, (double Mean, double Max)> CompareToProgressive(
		RadiosityResult quality, LightmapBakeResult progressive )
	{
		var result = new Dictionary<string, (double Mean, double Max)>();
		if ( quality?.Faces is null || progressive?.Faces is null ) return result;
		foreach ( var face in quality.Faces )
		{
			if ( face.SkippedNoDraw ) continue;
			var other = progressive.Face( face.FaceId );
			if ( other is null || other.SkippedNoDraw ) continue;
			var sum = Math.Abs( face.Average.x - other.Average.x ) +
				Math.Abs( face.Average.y - other.Average.y ) +
				Math.Abs( face.Average.z - other.Average.z );
			var max = Math.Max( Math.Abs( face.Average.x - other.Average.x ),
				Math.Max( Math.Abs( face.Average.y - other.Average.y ),
					Math.Abs( face.Average.z - other.Average.z ) ) );
			result[face.FaceId.ToString( CultureInfo.InvariantCulture )] = (sum / 3.0, max);
		}
		return result;
	}
}
