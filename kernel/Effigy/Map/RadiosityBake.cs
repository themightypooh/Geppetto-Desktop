// Hammer Desktop P3: native radiosity-quality bake (direct + bounced, AO, HDR,
// static-prop lighting). Board task hammer-radiosity-quality-bake.
//
// WHAT THIS IS. A full-quality lightmap bake that runs entirely in the kernel — no
// Hammer/Hammer++/VBSP/VVIS/VRAD, no external compiler, no GPU required. It takes the
// same inputs as the live preview (LiveFace/LiveLight) and produces per-face HDR luxel
// grids: direct light (shadow-tested), multi-bounce radiosity, ambient occlusion, HDR
// values (no 0..1 clamp), static-prop vertex lighting, and an atlas packer with
// gutters. CPU first; the GPU path keeps GpuBake's 1K test-bake trust rule.
//
// LIGHT TRANSPORT MODEL (documented, so a test can check it analytically):
//   direct   = LiveLighting's light model EXACTLY (point/spot falloff 1/(1+(d/64)^2),
//              spot smooth cone, sun NdotL, environment ambient) PLUS a shadow test:
//              a patch behind a wall gets no direct light from that light. The
//              cross-check test proves the unoccluded direct pass agrees with
//              LiveLighting.Evaluate(Draft) within 0.05 relative.
//   bounce   = progressive radiosity (Jacobi gather) over luxel patches. Patch i
//              leaves B_i = albedo_i * (direct_i + ambient + indirect_i), where
//              indirect_i = sum_j F_ij * B_j and F_ij is the differential form factor
//              cos(theta_i)*cos(theta_j)*A_j / (pi*r^2), visibility-tested, with
//              adaptive subdivision for near pairs and row normalisation (a row that
//              sums above 1 is scaled back to 1: energy cannot be created).
//   AO       = deterministic cosine-weighted Hammersley hemisphere sampling: the
//              fraction of rays that hit geometry within AoDistance darkens the
//              INDIRECT + ambient term only (direct sun/point light is shadow-tested,
//              not AO-darkened). An open field has AO factor exactly 1.
//   luxel    = albedo * direct + AO * albedo * (ambient + indirect), HDR linear.
//   HDR      = floats, never clamped to 1; RadiosityResult.MaxLuxel reports the peak
//              and ToneMap() is the Reinhard helper an atlas exporter uses for LDR.
//   props    = per-vertex direct (shadow-tested) + ambient + indirect sampled from
//              nearby patches (inverse-distance weighted) + AO at the vertex.
//   atlas    = deterministic shelf packing with a padding gutter between charts.
//
// DEPENDENCY STATUS (2026-11-13, reconciled): hammer-lightmap-progressive-bake
// HAS now landed — kernel/Effigy/Map/LightmapBake.cs exists (P2 done, worker 57)
// — so the adapter points filed below are live, in
// kernel/Effigy/Map/LightmapBakeQuality.cs (P3, worker daxter-hammer-radiosity):
// LightmapBakeQuality.BakeRadiosity runs this engine from a LightmapBakeJob,
// PackAtlas packs its face grids with the multi-page padded packer,
// LightPropVertices / VerifyGpuTrust / BakeGpuRadiosity route the prop
// lighting and the GPU trust rule through the job, and CompareToProgressive
// compares this bake's per-face averages with the progressive refined bake
// (same metric as LightmapBakeJob.CompareAverages). The direct pass was NOT
// replaced: this engine's direct evaluation and LightmapBake's duplicate the
// same LiveLighting model by design (both private to their files), and the
// direct-only cross-check test enforces that they agree within 0.05.
// Do NOT invent a second radiosity evaluator against LightmapBake types;
// this file is the radiosity engine; LightmapBake stays the progressive job.
//
// GROUND TRUTH WARNING. worker 23's LiveLighting bake-reference.json is a snapshot
// recorded from LiveLighting's OWN Full evaluation, not an independent bake. Nothing
// in this file or its tests treats it as ground truth. The tests use analytic cases
// with known answers: a point-light luxel computed by hand, sun HDR = intensity,
// a shadow wall, the exact disk form factor R^2/(R^2+S^2), the enclosure invariant
// (form factors from one patch sum to 1), and a two-room fixture where the unlit
// room's light can ONLY come from bounce.
//
// UNITS. Kernel inches, Z-up. Colours LINEAR RGB, HDR (may exceed 1). Patch areas
// are true polygon area / cell count (not projected area). Deterministic: same
// faces + lights + settings give the same luxels bit for bit (no clock, no Random;
// Hammersley AO and FNV-1a keys are fixed sequences).
//
// PERFORMANCE. Form factors are O(P^2 * T) to precompute (P patches, T triangles),
// then O(bounces * sparse pairs) to gather; the sparse matrix only stores pairs
// with both cosines positive and a clear line of sight. Test scenes are small by
// design; RadiositySettings.MaxTotalPatches (default 8192) refuses absurd scenes
// with a named error instead of allocating a dense matrix. Perf measurement is for
// the final integration gate, not claimed here.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace Effigy.Map;

/// <summary>Quality presets: bounce count, AO ray count and luxel grid cap.</summary>
public enum RadiosityQuality
{
	/// <summary>1 bounce, no AO, coarse grid (max 8 per axis). Fast; direct + one bounce.</summary>
	Draft,
	/// <summary>3 bounces, 16 AO rays, grid cap 16. The default.</summary>
	Standard,
	/// <summary>5 bounces, 64 AO rays, grid cap 32. The quality bake.</summary>
	Quality,
}

/// <summary>
/// Settings for a radiosity bake. All fields are validated by
/// <see cref="RadiosityBake.Bake"/> with a named <see cref="ArgumentException"/>.
/// A <see cref="RadiositySettings"/> is a plain mutable bag: clone it before changing
/// a preset (<see cref="Clone"/>).
/// </summary>
public sealed class RadiositySettings
{
	/// <summary>The preset these settings came from (informational; the fields rule).</summary>
	public RadiosityQuality Quality { get; set; } = RadiosityQuality.Standard;
	/// <summary>Max Jacobi gather iterations (bounce levels). 0 = direct only. 0..8.</summary>
	public int Bounces { get; set; } = 3;
	/// <summary>AO hemisphere rays per patch. 0 = AO off (factor 1 everywhere). 0..256.</summary>
	public int AoRays { get; set; } = 16;
	/// <summary>AO ray length (world units). Rays that hit nothing within this are open sky. &gt; 0.</summary>
	public float AoDistance { get; set; } = 128f;
	/// <summary>How much occlusion darkens (0 = AO ignored, 1 = fully occluded is black). 0..1.</summary>
	public float AoStrength { get; set; } = 0.8f;
	/// <summary>Max luxels per face axis (grid cap). 1..64.</summary>
	public int MaxGridPerAxis { get; set; } = 16;
	/// <summary>Multiplier on the face's FaceUv lightmap scale (smaller = finer grid). &gt; 0.</summary>
	public float LuxelScaleMultiplier { get; set; } = 1f;
	/// <summary>Stop iterating when the largest unshot radiosity component drops below this. &gt; 0.</summary>
	public float ConvergenceEpsilon { get; set; } = 0.001f;
	/// <summary>Refuse the bake (named error, not a throw) above this many patches. &gt;= 1.</summary>
	public int MaxTotalPatches { get; set; } = 8192;
	/// <summary>Prop vertex lighting: patches within this distance contribute indirect. &gt; 0.</summary>
	public float PropSampleRadius { get; set; } = 64f;
	/// <summary>The GPU trust rule's test-bake budget: the 1K test-bake must fit in this many luxels. &gt;= 1.</summary>
	public int TestMaxLuxels { get; set; } = 1024;

	/// <summary>The preset's settings (a fresh instance each call).</summary>
	public static RadiositySettings Preset( RadiosityQuality quality )
	{
		switch ( quality )
		{
			case RadiosityQuality.Draft:
				return new RadiositySettings { Quality = quality, Bounces = 1, AoRays = 0, AoDistance = 128f, AoStrength = 0.8f, MaxGridPerAxis = 8, ConvergenceEpsilon = 0.01f };
			case RadiosityQuality.Quality:
				return new RadiositySettings { Quality = quality, Bounces = 5, AoRays = 64, AoDistance = 128f, AoStrength = 0.8f, MaxGridPerAxis = 32, ConvergenceEpsilon = 0.0005f };
			default:
				return new RadiositySettings { Quality = RadiosityQuality.Standard, Bounces = 3, AoRays = 16, AoDistance = 128f, AoStrength = 0.8f, MaxGridPerAxis = 16, ConvergenceEpsilon = 0.001f };
		}
	}

	public RadiositySettings Clone() => new()
	{
		Quality = Quality,
		Bounces = Bounces,
		AoRays = AoRays,
		AoDistance = AoDistance,
		AoStrength = AoStrength,
		MaxGridPerAxis = MaxGridPerAxis,
		LuxelScaleMultiplier = LuxelScaleMultiplier,
		ConvergenceEpsilon = ConvergenceEpsilon,
		MaxTotalPatches = MaxTotalPatches,
		PropSampleRadius = PropSampleRadius,
		TestMaxLuxels = TestMaxLuxels,
	};

	internal void Validate()
	{
		if ( Bounces < 0 || Bounces > 8 )
			throw new ArgumentException( $"RadiositySettings: Bounces {Bounces} must be 0..8." );
		if ( AoRays < 0 || AoRays > 256 )
			throw new ArgumentException( $"RadiositySettings: AoRays {AoRays} must be 0..256." );
		if ( AoDistance <= 0f || !float.IsFinite( AoDistance ) )
			throw new ArgumentException( $"RadiositySettings: AoDistance {AoDistance} must be positive and finite." );
		if ( AoStrength < 0f || AoStrength > 1f )
			throw new ArgumentException( $"RadiositySettings: AoStrength {AoStrength} must be 0..1." );
		if ( MaxGridPerAxis < 1 || MaxGridPerAxis > 64 )
			throw new ArgumentException( $"RadiositySettings: MaxGridPerAxis {MaxGridPerAxis} must be 1..64." );
		if ( LuxelScaleMultiplier <= 0f || !float.IsFinite( LuxelScaleMultiplier ) )
			throw new ArgumentException( $"RadiositySettings: LuxelScaleMultiplier {LuxelScaleMultiplier} must be positive and finite." );
		if ( ConvergenceEpsilon <= 0f || !float.IsFinite( ConvergenceEpsilon ) )
			throw new ArgumentException( $"RadiositySettings: ConvergenceEpsilon {ConvergenceEpsilon} must be positive and finite." );
		if ( MaxTotalPatches < 1 )
			throw new ArgumentException( $"RadiositySettings: MaxTotalPatches {MaxTotalPatches} must be at least 1." );
		if ( PropSampleRadius <= 0f || !float.IsFinite( PropSampleRadius ) )
			throw new ArgumentException( $"RadiositySettings: PropSampleRadius {PropSampleRadius} must be positive and finite." );
		if ( TestMaxLuxels < 1 )
			throw new ArgumentException( $"RadiositySettings: TestMaxLuxels {TestMaxLuxels} must be at least 1." );
	}
}

/// <summary>
/// One baked face: HDR luxel grids plus the split a test (or a debugger) needs —
/// direct vs indirect and the AO factor per luxel. Row-major, row 0 is the smallest V.
/// A nodraw face is skipped: empty grids, zero colours, <see cref="SkippedNoDraw"/> true.
/// </summary>
public sealed class RadiosityFaceResult
{
	public int FaceId { get; }
	public int Rows { get; }
	public int Cols { get; }
	/// <summary>Nominal world units per luxel (face lightmap scale * settings multiplier).</summary>
	public float LuxelSize { get; }
	/// <summary>Final HDR linear luxels: albedo*direct + AO*albedo*(ambient+indirect).</summary>
	public IReadOnlyList<Vec3> Luxels { get; }
	/// <summary>Reflected direct light only (albedo * shadow-tested direct irradiance).</summary>
	public IReadOnlyList<Vec3> DirectLuxels { get; }
	/// <summary>Reflected indirect + ambient only (AO * albedo * (ambient + indirect)).</summary>
	public IReadOnlyList<Vec3> IndirectLuxels { get; }
	/// <summary>AO factor per luxel, 0..1 (1 = fully open). All 1 when AO is off.</summary>
	public IReadOnlyList<float> AoFactors { get; }
	public bool SkippedNoDraw { get; }
	public Vec3 Average { get; }
	public Vec3 Max { get; }
	public Vec3 DirectAverage { get; }
	public Vec3 IndirectAverage { get; }
	/// <summary>Mean AO factor over the face's luxels; 1 when AO is off or the face is skipped.</summary>
	public float MeanAoFactor { get; }

	public RadiosityFaceResult( int faceId, int rows, int cols, float luxelSize,
		IReadOnlyList<Vec3> luxels, IReadOnlyList<Vec3> directLuxels, IReadOnlyList<Vec3> indirectLuxels,
		IReadOnlyList<float> aoFactors, bool skippedNoDraw )
	{
		FaceId = faceId;
		Rows = rows;
		Cols = cols;
		LuxelSize = luxelSize;
		Luxels = luxels;
		DirectLuxels = directLuxels;
		IndirectLuxels = indirectLuxels;
		AoFactors = aoFactors;
		SkippedNoDraw = skippedNoDraw;
		var sum = Vec3.Zero;
		var dSum = Vec3.Zero;
		var iSum = Vec3.Zero;
		var max = Vec3.Zero;
		var aoSum = 0f;
		for ( var i = 0; i < luxels.Count; i++ )
		{
			sum += luxels[i];
			dSum += directLuxels[i];
			iSum += indirectLuxels[i];
			max = new Vec3( MathF.Max( max.x, luxels[i].x ), MathF.Max( max.y, luxels[i].y ), MathF.Max( max.z, luxels[i].z ) );
			aoSum += aoFactors[i];
		}
		Average = luxels.Count == 0 ? Vec3.Zero : sum / luxels.Count;
		DirectAverage = directLuxels.Count == 0 ? Vec3.Zero : dSum / directLuxels.Count;
		IndirectAverage = indirectLuxels.Count == 0 ? Vec3.Zero : iSum / indirectLuxels.Count;
		Max = max;
		MeanAoFactor = aoFactors.Count == 0 ? 1f : aoSum / aoFactors.Count;
	}

	/// <summary>Luxel (row, col), row-major. Throws for a skipped face or out-of-range indices.</summary>
	public Vec3 LuxelAt( int row, int col )
	{
		if ( row < 0 || row >= Rows || col < 0 || col >= Cols )
			throw new ArgumentOutOfRangeException( $"Face {FaceId}: no luxel at row {row}, col {col} (grid is {Rows}x{Cols})." );
		return Luxels[row * Cols + col];
	}

	/// <summary>Direct luxel (row, col): the shadow-tested direct part only.</summary>
	public Vec3 DirectAt( int row, int col )
	{
		if ( row < 0 || row >= Rows || col < 0 || col >= Cols )
			throw new ArgumentOutOfRangeException( $"Face {FaceId}: no direct luxel at row {row}, col {col} (grid is {Rows}x{Cols})." );
		return DirectLuxels[row * Cols + col];
	}

	/// <summary>Indirect luxel (row, col): the bounced + ambient part only.</summary>
	public Vec3 IndirectAt( int row, int col )
	{
		if ( row < 0 || row >= Rows || col < 0 || col >= Cols )
			throw new ArgumentOutOfRangeException( $"Face {FaceId}: no indirect luxel at row {row}, col {col} (grid is {Rows}x{Cols})." );
		return IndirectLuxels[row * Cols + col];
	}
}

/// <summary>
/// One patch as the prop-lighting sampler sees it: the outgoing radiosity that a
/// static prop vertex near it receives. Public so a caller can sample indirect light
/// without re-running the bake.
/// </summary>
public sealed class RadiosityPatchSample
{
	public int FaceId { get; }
	public Vec3 Centre { get; }
	public Vec3 Normal { get; }
	public float Area { get; }
	/// <summary>Total outgoing radiosity B = albedo * (direct + ambient + indirect), HDR linear.</summary>
	public Vec3 Outgoing { get; }

	public RadiosityPatchSample( int faceId, Vec3 centre, Vec3 normal, float area, Vec3 outgoing )
	{
		FaceId = faceId;
		Centre = centre;
		Normal = normal;
		Area = area;
		Outgoing = outgoing;
	}
}

/// <summary>
/// One radiosity bake run: per-face HDR luxels plus the run's own accounting. A
/// failed or cancelled run has <see cref="Faces"/> empty and a readable
/// <see cref="Error"/> (or <see cref="Cancelled"/>); it never throws, and it never
/// writes files. <see cref="Succeeded"/> is false for those runs.
/// </summary>
public sealed class RadiosityResult
{
	public IReadOnlyList<RadiosityFaceResult> Faces { get; }
	/// <summary>Patch samples for prop vertex lighting (empty for a failed run).</summary>
	public IReadOnlyList<RadiosityPatchSample> PatchSamples { get; }
	public RadiosityQuality Quality { get; }
	/// <summary>Gather iterations actually run (0 for a direct-only bake).</summary>
	public int BouncesRun { get; }
	/// <summary>True when the largest unshot radiosity fell below ConvergenceEpsilon.</summary>
	public bool Converged { get; }
	/// <summary>Largest unshot radiosity component left when the bake stopped.</summary>
	public float ResidualEnergy { get; }
	public int LuxelCount { get; }
	/// <summary>Component-wise maximum luxel over all faces (HDR: may exceed 1).</summary>
	public Vec3 MaxLuxel { get; }
	public string InvalidationKey { get; }
	public IReadOnlyList<string> Warnings { get; }
	/// <summary>Which path produced this result: "cpu", "gpu", or "cpu (gpu ...)" for a fallback.</summary>
	public string Route { get; }
	public bool Cancelled { get; }
	public string Error { get; }
	public bool Succeeded => Error is null && !Cancelled && Faces is not null;

	public RadiosityResult( IReadOnlyList<RadiosityFaceResult> faces, IReadOnlyList<RadiosityPatchSample> patchSamples,
		RadiosityQuality quality, int bouncesRun, bool converged, float residualEnergy,
		string invalidationKey, IReadOnlyList<string> warnings, string route, bool cancelled, string error )
	{
		Faces = faces;
		PatchSamples = patchSamples;
		Quality = quality;
		BouncesRun = bouncesRun;
		Converged = converged;
		ResidualEnergy = residualEnergy;
		InvalidationKey = invalidationKey;
		Warnings = warnings;
		Route = route;
		Cancelled = cancelled;
		Error = error;
		var count = 0;
		var max = Vec3.Zero;
		foreach ( var f in faces )
		{
			count += f.Luxels.Count;
			max = new Vec3( MathF.Max( max.x, f.Max.x ), MathF.Max( max.y, f.Max.y ), MathF.Max( max.z, f.Max.z ) );
		}
		LuxelCount = count;
		MaxLuxel = max;
	}

	/// <summary>The baked face with this id, or null when the run had no such face.</summary>
	public RadiosityFaceResult Face( int id )
	{
		foreach ( var f in Faces )
			if ( f.FaceId == id ) return f;
		return null;
	}
}

/// <summary>
/// The 1K test-bake verdict for the GPU path: the token <see cref="RadiosityBake.BakeGpu"/>
/// demands before it trusts a GPU backend on a scene. <see cref="Key"/> binds the
/// verdict to the scene (faces + lights); changing the scene voids it.
/// </summary>
public sealed class GpuTrustResult
{
	public bool Passed { get; }
	/// <summary>Worst per-face relative deviation between GPU and CPU test-bakes (0 when no comparison ran).</summary>
	public float WorstDeviation { get; }
	public int WorstFaceId { get; }
	/// <summary>Named reason: "GpuBackendMissing", "GpuDeclined", "GpuDiverged", "CpuTestBakeFailed", "TestBakeTooLarge", or "" when passed.</summary>
	public string Reason { get; }
	/// <summary>Scene key this verdict was verified under (see RadiosityBake.ComputeTrustKey).</summary>
	public string Key { get; }

	public GpuTrustResult( bool passed, float worstDeviation, int worstFaceId, string reason, string key )
	{
		Passed = passed;
		WorstDeviation = worstDeviation;
		WorstFaceId = worstFaceId;
		Reason = reason;
		Key = key;
	}
}

/// <summary>
/// A GPU radiosity backend, mirroring <see cref="GpuBake.IGpuBakeBackend"/>: the kernel
/// stays engine-free and a host installs the real GL implementation. Returns false
/// with a reason when it declines a job — a decline is an ordinary outcome, not an
/// error. A backend that returns a result must bake the same faces/lights/settings
/// it was given (the 1K test-bake compares its per-face averages against the CPU).
/// </summary>
public interface IRadiosityGpuBackend
{
	bool TryBake( IReadOnlyList<LiveFace> faces, IReadOnlyList<LiveLight> lights,
		RadiositySettings settings, out RadiosityResult result, out string why );
}

/// <summary>
/// One static-prop vertex to light: a position + normal + albedo. The prop itself is
/// not baked — props have no lightmap — so they sample the baked radiosity instead.
/// </summary>
public sealed class RadiosityPropVertex
{
	public Vec3 Position { get; }
	public Vec3 Normal { get; }
	/// <summary>Linear RGB albedo the vertex reflects; white by default.</summary>
	public Vec3 Albedo { get; }

	public RadiosityPropVertex( Vec3 position, Vec3 normal, Vec3 albedo )
	{
		if ( normal.LengthSquared <= 1e-12f )
			throw new ArgumentException( $"Prop vertex at {position}: normal is zero-length." );
		if ( albedo.x < 0f || albedo.y < 0f || albedo.z < 0f )
			throw new ArgumentException( $"Prop vertex at {position}: albedo {albedo} has a negative component." );
		Position = position;
		Normal = normal.Normal;
		Albedo = albedo;
	}

	public RadiosityPropVertex( Vec3 position, Vec3 normal ) : this( position, normal, Vec3.One ) { }
}

/// <summary>
/// The native radiosity-quality bake. Pure function over faces + lights (+ settings):
/// no document mutation, no IO, no GL, no UI. Deterministic — same inputs give the
/// same luxels bit for bit. See the file header for the light transport model.
/// </summary>
public static class RadiosityBake
{
	/// <summary>Falloff denominator: point/spot intensity halves at this distance (units). Same as LiveLighting — the direct pass must agree with the preview.</summary>
	public const float FalloffHalfDistance = 64f;
	/// <summary>Form-factor subdivision: subdivide a receiver patch while it is closer than this multiple of its own size (near-field accuracy). See FormFactorToPatch.</summary>
	public const float NearFieldSubdivisionFactor = 2f;
	/// <summary>Max subdivision depth for one receiver patch (4^3 = 64 sub-patches worst case).</summary>
	public const int MaxSubdivisionDepth = 3;
	/// <summary>Shadow/AO ray origin offset along the normal (world units): keeps a ray from hitting the surface it starts on.</summary>
	public const float RayOriginEpsilon = 0.01f;

	/// <summary>The installed GPU radiosity backend, or null when none is installed. A host that never installs one gets the CPU bake.</summary>
	public static IRadiosityGpuBackend GpuBackend { get; set; }

	public static bool GpuAvailable => GpuBackend is not null;

	static Vec3 Mul( Vec3 a, Vec3 b ) => new( a.x * b.x, a.y * b.y, a.z * b.z );

	static float Smoothstep( float t )
	{
		if ( t <= 0f ) return 0f;
		if ( t >= 1f ) return 1f;
		return t * t * (3f - 2f * t);
	}

	// One luxel patch: a cell of a face's luxel grid. Corners are kept so a near
	// receiver patch can be subdivided for an accurate form factor.
	sealed class Patch
	{
		public int FaceIndex;
		public int FaceId;
		public int Row;
		public int Col;
		public Vec3 Centre;
		public Vec3 Normal;
		public Vec3 Albedo;
		public float Area;
		public Vec3[] Corners = Array.Empty<Vec3>();
		public Vec3 Direct;      // direct irradiance (pre-albedo, shadow-tested)
		public float AoFactor = 1f;
		public Vec3 Indirect;    // accumulated arriving irradiance (all bounces)
		public Vec3 Unshot;      // outgoing radiosity not yet shot
	}

	/// <summary>
	/// Bake faces + lights into per-face HDR luxel grids (CPU path). Never throws for
	/// a bake failure or a cancel: those come back as <see cref="RadiosityResult.Error"/>
	/// / <see cref="RadiosityResult.Cancelled"/> with empty faces. Invalid arguments
	/// (null inputs, duplicate face ids, zero-area faces, bad settings) DO throw, with
	/// the face or setting named — same contract as LiveLighting.Evaluate.
	/// </summary>
	public static RadiosityResult Bake( IReadOnlyList<LiveFace> faces, IReadOnlyList<LiveLight> lights,
		RadiositySettings settings = null, IProgress<BakeProgress> progress = null, CancellationToken cancel = default )
	{
		if ( faces is null ) throw new ArgumentNullException( nameof( faces ) );
		if ( lights is null ) throw new ArgumentNullException( nameof( lights ) );
		settings ??= new RadiositySettings();
		settings.Validate();
		ValidateFaces( faces );

		var key = ComputeInvalidationKey( faces, lights, settings );
		var clock = System.Diagnostics.Stopwatch.StartNew();

		void Report( BakePhase phase, float percent, string message ) =>
			progress?.Report( new BakeProgress { Phase = phase, Percent = percent, Message = message } );

		RadiosityResult Fail( string error, BakePhase phase )
		{
			Report( phase, 100f, error );
			return new RadiosityResult( Array.Empty<RadiosityFaceResult>(), Array.Empty<RadiosityPatchSample>(),
				settings.Quality, 0, false, 0f, key, Array.Empty<string>(), "cpu", false, error );
		}

		try
		{
			Report( BakePhase.Preparing, 0f, "Preparing radiosity patches." );
			cancel.ThrowIfCancellationRequested();

			var warnings = new List<string>();
			if ( lights.Count == 0 )
				warnings.Add( "No lights: every face bakes to black." );
			var hasSun = lights.Any( l => l.Kind == LiveLightKind.Sun );
			var hasEnvironment = lights.Any( l => l.Kind == LiveLightKind.Environment );
			if ( hasSun && !hasEnvironment )
				warnings.Add( "Sun light present with no light_environment: the sun has no ambient term (light_environment + env_sun pair is incomplete)." );

			var ambient = Vec3.Zero;
			foreach ( var light in lights )
				if ( light.Kind == LiveLightKind.Environment )
					ambient += light.Color * light.Intensity;

			var patches = BuildPatches( faces, settings, warnings );
			if ( patches.Count > settings.MaxTotalPatches )
				return Fail( $"PatchBudgetExceeded: the bake needs {patches.Count} patches, over the MaxTotalPatches budget of {settings.MaxTotalPatches} (raise MaxTotalPatches or coarsen the lightmap scales).", BakePhase.Failed );

			Report( BakePhase.Baking, 10f, $"Direct light for {patches.Count} patches." );
			foreach ( var patch in patches )
			{
				patch.Direct = DirectIrradiance( patch.Centre, patch.Normal, lights, faces, patch.FaceId );
				patch.Unshot = Mul( patch.Albedo, patch.Direct + ambient );
			}
			cancel.ThrowIfCancellationRequested();

			Report( BakePhase.Baking, 30f, $"Ambient occlusion for {patches.Count} patches." );
			if ( settings.AoRays > 0 )
				foreach ( var patch in patches )
					patch.AoFactor = AmbientOcclusion( patch.Centre, patch.Normal, faces, patch.FaceId, settings );
			cancel.ThrowIfCancellationRequested();

			Report( BakePhase.Baking, 50f, "Form factors (visibility + near-field subdivision)." );
			var rows = BuildFormFactorRows( patches, faces );
			cancel.ThrowIfCancellationRequested();

			Report( BakePhase.Baking, 70f, $"Gathering {settings.Bounces} bounce(s)." );
			var bouncesRun = 0;
			var residual = MaxUnshot( patches );
			for ( var bounce = 0; bounce < settings.Bounces; bounce++ )
			{
				cancel.ThrowIfCancellationRequested();
				var delta = new Vec3[patches.Count];
				for ( var s = 0; s < patches.Count; s++ )
				{
					var unshot = patches[s].Unshot;
					if ( unshot.x <= 0f && unshot.y <= 0f && unshot.z <= 0f ) continue;
					foreach ( var (receiver, f) in rows[s] )
						delta[receiver] += unshot * f;
				}
				for ( var i = 0; i < patches.Count; i++ )
				{
					patches[i].Indirect += delta[i];
					patches[i].Unshot = Mul( patches[i].Albedo, delta[i] );
				}
				bouncesRun++;
				residual = MaxUnshot( patches );
				Report( BakePhase.Baking, 70f + 25f * bouncesRun / Math.Max( 1, settings.Bounces ), $"Bounce {bouncesRun}/{settings.Bounces} (residual {residual:0.000})." );
				if ( residual < settings.ConvergenceEpsilon ) break;
			}

			var converged = residual < settings.ConvergenceEpsilon;
			if ( settings.Bounces > 0 && !converged )
				warnings.Add( $"Stopped after {bouncesRun} bounce(s) with residual {residual:0.000}: the bake did not converge below ConvergenceEpsilon {settings.ConvergenceEpsilon} (raise Bounces or loosen ConvergenceEpsilon)." );

			Report( BakePhase.Writing, 98f, "Assembling luxel grids." );
			var (faceResults, samples) = AssembleResults( faces, patches, ambient );
			Report( BakePhase.Done, 100f, $"Radiosity bake done: {faceResults.Count} faces, {samples.Count} patches, {bouncesRun} bounce(s) in {clock.Elapsed.TotalMilliseconds:0} ms." );
			return new RadiosityResult( faceResults, samples, settings.Quality, bouncesRun, converged, residual,
				key, warnings, "cpu", false, null );
		}
		catch ( OperationCanceledException )
		{
			return new RadiosityResult( Array.Empty<RadiosityFaceResult>(), Array.Empty<RadiosityPatchSample>(),
				settings.Quality, 0, false, 0f, key, Array.Empty<string>(), "cpu", true, null );
		}
		catch ( Exception ex )
		{
			return Fail( $"Radiosity bake failed ({ex.GetType().Name}): {ex.Message}", BakePhase.Failed );
		}
	}

	/// <summary>
	/// The GPU path. Demands a passed 1K test-bake verification for THIS scene
	/// (<paramref name="trust"/> from <see cref="VerifyGpuTrust"/>): without one the
	/// bake is REFUSED with a named reason (no silent CPU fallback — the caller asked
	/// for the GPU and has not proved it trustworthy on this scene). With a passed
	/// verification, a backend that declines the final bake (or fails it) falls back
	/// to the CPU bake and says so in <see cref="RadiosityResult.Route"/>, mirroring
	/// GpuBake's final-bake decline path.
	/// </summary>
	public static RadiosityResult BakeGpu( IReadOnlyList<LiveFace> faces, IReadOnlyList<LiveLight> lights,
		RadiositySettings settings, GpuTrustResult trust,
		IProgress<BakeProgress> progress = null, CancellationToken cancel = default )
	{
		if ( faces is null ) throw new ArgumentNullException( nameof( faces ) );
		if ( lights is null ) throw new ArgumentNullException( nameof( lights ) );
		settings ??= new RadiositySettings();
		settings.Validate();
		var key = ComputeInvalidationKey( faces, lights, settings );

		RadiosityResult Refuse( string error ) => new( Array.Empty<RadiosityFaceResult>(), Array.Empty<RadiosityPatchSample>(),
			settings.Quality, 0, false, 0f, key, Array.Empty<string>(), "cpu", false, error );

		if ( trust is null )
			return Refuse( "GpuTrustNotVerified: no GPU trust verification was supplied — run VerifyGpuTrust (the 1K test-bake) for this scene first." );
		if ( !trust.Passed )
			return Refuse( $"GpuTrustNotVerified: the 1K test-bake did not pass ({trust.Reason})." );
		if ( trust.Key != ComputeTrustKey( faces, lights ) )
			return Refuse( "GpuTrustNotVerified: the trust verification is for a different scene (faces or lights changed since the 1K test-bake)." );
		if ( GpuBackend is null )
			return Refuse( "GpuBackendMissing: no radiosity GPU backend is installed." );

		if ( !GpuBackend.TryBake( faces, lights, settings, out var gpu, out var why ) || gpu is null || !gpu.Succeeded )
		{
			var cpu = Bake( faces, lights, settings, progress, cancel );
			if ( !cpu.Succeeded ) return cpu;
			return new RadiosityResult( cpu.Faces, cpu.PatchSamples, cpu.Quality, cpu.BouncesRun, cpu.Converged,
				cpu.ResidualEnergy, cpu.InvalidationKey, cpu.Warnings,
				$"cpu (gpu declined the final bake: {why ?? gpu?.Error ?? "no reason given"})", false, null );
		}
		return new RadiosityResult( gpu.Faces, gpu.PatchSamples, gpu.Quality, gpu.BouncesRun, gpu.Converged,
			gpu.ResidualEnergy, gpu.InvalidationKey, gpu.Warnings, "gpu", false, null );
	}

	/// <summary>
	/// The 1K test-bake trust rule, same as GpuBake: the GPU path is only trusted for
	/// a scene after its test-bake matches the CPU reference within
	/// <paramref name="tolerance"/> (relative per-face average luxel difference).
	/// The test-bake runs at Bounces=1, AO off, grid cap 8 per axis, and is refused
	/// when it needs more than <see cref="RadiositySettings.TestMaxLuxels"/> luxels
	/// (default 1024) — the test-bake is 1K by budget, not by wish. The returned
	/// verdict is the token <see cref="BakeGpu"/> demands; it never throws for a
	/// failed comparison (invalid arguments still throw).
	/// </summary>
	public static GpuTrustResult VerifyGpuTrust( IRadiosityGpuBackend backend,
		IReadOnlyList<LiveFace> faces, IReadOnlyList<LiveLight> lights,
		RadiositySettings settings = null, float tolerance = 0.05f )
	{
		if ( faces is null ) throw new ArgumentNullException( nameof( faces ) );
		if ( lights is null ) throw new ArgumentNullException( nameof( lights ) );
		settings ??= new RadiositySettings();
		settings.Validate();
		if ( tolerance < 0f )
			throw new ArgumentException( $"VerifyGpuTrust: tolerance {tolerance} is negative." );
		var key = ComputeTrustKey( faces, lights );

		if ( backend is null )
			return new GpuTrustResult( false, 0f, -1, "GpuBackendMissing: no radiosity GPU backend is installed.", key );

		var testSettings = settings.Clone();
		testSettings.Bounces = 1;
		testSettings.AoRays = 0;
		testSettings.MaxGridPerAxis = 8;
		var cpu = Bake( faces, lights, testSettings );
		if ( !cpu.Succeeded )
			return new GpuTrustResult( false, 0f, -1, $"CpuTestBakeFailed: {cpu.Error ?? "no result"}", key );
		if ( cpu.LuxelCount > testSettings.TestMaxLuxels )
			return new GpuTrustResult( false, 0f, -1, $"TestBakeTooLarge: the 1K test-bake needs {cpu.LuxelCount} luxels, over the TestMaxLuxels budget of {testSettings.TestMaxLuxels} (coarsen the scene's lightmap scales).", key );

		if ( !backend.TryBake( faces, lights, testSettings, out var gpu, out var why ) || gpu is null )
			return new GpuTrustResult( false, 0f, -1, $"GpuDeclined: {why ?? "no reason given"}", key );
		if ( !gpu.Succeeded )
			return new GpuTrustResult( false, 0f, -1, $"GpuBakeFailed: {gpu.Error ?? "no result"}", key );

		var worst = 0f;
		var worstFace = -1;
		var compared = 0;
		foreach ( var face in cpu.Faces )
		{
			if ( face.SkippedNoDraw ) continue;
			var gpuFace = gpu.Face( face.FaceId );
			if ( gpuFace is null ) continue;
			compared++;
			var preview = new[] { face.Average.x, face.Average.y, face.Average.z };
			var baked = new[] { gpuFace.Average.x, gpuFace.Average.y, gpuFace.Average.z };
			for ( var i = 0; i < 3; i++ )
			{
				var rel = MathF.Abs( preview[i] - baked[i] ) / MathF.Max( MathF.Abs( baked[i] ), 0.01f );
				if ( rel > worst )
				{
					worst = rel;
					worstFace = face.FaceId;
				}
			}
		}
		if ( compared == 0 )
			return new GpuTrustResult( false, 0f, -1, "GpuDeclined: the GPU test-bake returned no faces to compare.", key );
		if ( worst > tolerance )
			return new GpuTrustResult( false, worst, worstFace, $"GpuDiverged: worst relative deviation {worst:0.###} on face {worstFace} exceeds tolerance {tolerance}.", key );
		return new GpuTrustResult( true, worst, worstFace, "", key );
	}

	/// <summary>
	/// Light static-prop vertices from a finished bake, with the scene faces for the
	/// direct shadow test and the vertex AO rays: direct (shadow-tested) + ambient +
	/// indirect sampled from nearby patches + AO at the vertex, HDR linear. The faces
	/// are required because the bake result carries patch samples but not the occluder
	/// faces — without them the shadow test and the AO rays would have nothing to hit
	/// and every prop would be wrongly fully lit. Throws when
	/// <paramref name="baked"/> is null or a failed run, or a vertex is null or
	/// degenerate. <paramref name="lights"/> must be the same lights the bake ran
	/// with (the direct term is re-evaluated; the indirect term comes from the bake).
	/// </summary>
	public static IReadOnlyList<Vec3> LightPropVertices( RadiosityResult baked,
		IReadOnlyList<RadiosityPropVertex> vertices, IReadOnlyList<LiveLight> lights,
		IReadOnlyList<LiveFace> faces, RadiositySettings settings = null )
	{
		if ( baked is null ) throw new ArgumentNullException( nameof( baked ) );
		if ( !baked.Succeeded )
			throw new ArgumentException( $"Cannot light props from a failed bake: {baked.Error ?? "cancelled"}." );
		if ( vertices is null ) throw new ArgumentNullException( nameof( vertices ) );
		if ( lights is null ) throw new ArgumentNullException( nameof( lights ) );
		if ( faces is null ) throw new ArgumentNullException( nameof( faces ) );
		settings ??= new RadiositySettings();
		settings.Validate();

		var ambient = Vec3.Zero;
		foreach ( var light in lights )
			if ( light.Kind == LiveLightKind.Environment )
				ambient += light.Color * light.Intensity;

		var lit = new Vec3[vertices.Count];
		for ( var v = 0; v < vertices.Count; v++ )
		{
			var vertex = vertices[v];
			if ( vertex is null ) throw new ArgumentException( "LightPropVertices: the vertex list contains a null vertex." );
			var direct = DirectIrradiance( vertex.Position, vertex.Normal, lights, faces, ownFaceId: -1 );
			var indirect = SampleIndirect( vertex.Position, vertex.Normal, baked.PatchSamples, settings.PropSampleRadius );
			var ao = settings.AoRays > 0
				? AmbientOcclusion( vertex.Position, vertex.Normal, faces, ownFaceId: -1, settings )
				: 1f;
			lit[v] = Mul( vertex.Albedo, direct ) + Mul( vertex.Albedo, (ambient + indirect) * ao );
		}
		return lit;
	}

	/// <summary>
	/// Which faces need re-baking when <paramref name="changedFaceIds"/> changed (or
	/// the lights changed). Radiosity couples faces through bounce, so the rule is
	/// conservative but real: a light change re-bakes everything; a face change
	/// re-bakes the face itself plus every face whose centre has a clear line of
	/// sight to a changed face's centre (those are the faces its bounce reaches).
	/// Faces sealed away from every changed face (no line of sight) are NOT flagged.
	/// Returns face ids in input order; empty when nothing changed.
	/// </summary>
	public static IReadOnlyList<int> PlanRebake( IReadOnlyList<LiveFace> faces,
		IReadOnlyList<int> changedFaceIds, bool lightsChanged )
	{
		if ( faces is null ) throw new ArgumentNullException( nameof( faces ) );
		if ( lightsChanged )
			return faces.Select( f => f.Id ).ToList();
		if ( changedFaceIds is null || changedFaceIds.Count == 0 )
			return Array.Empty<int>();
		var changed = new HashSet<int>( changedFaceIds );
		var byId = new Dictionary<int, LiveFace>();
		foreach ( var f in faces )
			byId[f.Id] = f;
		var result = new List<int>();
		foreach ( var face in faces )
		{
			if ( changed.Contains( face.Id ) )
			{
				result.Add( face.Id );
				continue;
			}
			foreach ( var changedId in changed )
			{
				if ( !byId.TryGetValue( changedId, out var other ) ) continue;
				if ( !SegmentBlocked( face.Center, other.Center, faces, face.Id, other.Id ) )
				{
					result.Add( face.Id );
					break;
				}
			}
		}
		return result;
	}

	/// <summary>
	/// The invalidation key: a stable FNV-1a hash (hex) over the settings, every
	/// face's id / vertices / material name / lightmap scale / albedo, and every
	/// light's full parameters, all quantized to 0.001 and in id order — same scheme
	/// as LiveLighting.ComputeInvalidationKey, with a "radiosity/v1" prefix. A cached
	/// result is valid while its key equals the key of the current inputs.
	/// </summary>
	public static string ComputeInvalidationKey( IReadOnlyList<LiveFace> faces, IReadOnlyList<LiveLight> lights,
		RadiositySettings settings = null )
	{
		if ( faces is null ) throw new ArgumentNullException( nameof( faces ) );
		if ( lights is null ) throw new ArgumentNullException( nameof( lights ) );
		settings ??= new RadiositySettings();
		var sb = new StringBuilder();
		sb.Append( "radiosity/v1 quality=" ).Append( (int)settings.Quality )
			.Append( " bounces=" ).Append( settings.Bounces )
			.Append( " ao=" ).Append( settings.AoRays ).Append( '/' ).Append( Q( settings.AoDistance ) ).Append( '/' ).Append( Q( settings.AoStrength ) )
			.Append( " grid=" ).Append( settings.MaxGridPerAxis ).Append( " luxelScale=" ).Append( Q( settings.LuxelScaleMultiplier ) )
			.Append( " eps=" ).Append( Q( settings.ConvergenceEpsilon ) )
			.Append( " maxPatches=" ).Append( settings.MaxTotalPatches )
			.Append( " propR=" ).Append( Q( settings.PropSampleRadius ) )
			.Append( ';' );
		AppendFacesAndLights( sb, faces, lights );
		return HashString( sb.ToString() );
	}

	/// <summary>
	/// The GPU trust key: the scene (faces + lights) a <see cref="GpuTrustResult"/>
	/// was verified under. Settings are deliberately NOT part of it — trust is about
	/// the backend matching the CPU light transport on this scene at test resolution,
	/// not about the quality settings of a later final bake.
	/// </summary>
	public static string ComputeTrustKey( IReadOnlyList<LiveFace> faces, IReadOnlyList<LiveLight> lights )
	{
		if ( faces is null ) throw new ArgumentNullException( nameof( faces ) );
		if ( lights is null ) throw new ArgumentNullException( nameof( lights ) );
		var sb = new StringBuilder();
		sb.Append( "radiosity-gputrust/v1;" );
		AppendFacesAndLights( sb, faces, lights );
		return HashString( sb.ToString() );
	}

	static void AppendFacesAndLights( StringBuilder sb, IReadOnlyList<LiveFace> faces, IReadOnlyList<LiveLight> lights )
	{
		foreach ( var face in faces.OrderBy( f => f.Id ) )
		{
			sb.Append( "face " ).Append( face.Id ).Append( ' ' ).Append( face.Material.MaterialName )
				.Append( " lm=" ).Append( Q( face.LightmapScale ) )
				.Append( " albedo=" ).Append( Q( face.Albedo ) );
			foreach ( var v in face.Vertices )
				sb.Append( ' ' ).Append( Q( v ) );
			sb.Append( ';' );
		}
		foreach ( var light in lights.OrderBy( l => l.Id ) )
		{
			sb.Append( "light " ).Append( light.Id ).Append( ' ' ).Append( light.Kind )
				.Append( " pos=" ).Append( Q( light.Position ) )
				.Append( " dir=" ).Append( Q( light.Direction ) )
				.Append( " col=" ).Append( Q( light.Color ) )
				.Append( " i=" ).Append( Q( light.Intensity ) )
				.Append( " r=" ).Append( Q( light.Range ) )
				.Append( " cone=" ).Append( Q( light.InnerConeDegrees ) ).Append( '/' ).Append( Q( light.OuterConeDegrees ) )
				.Append( ';' );
		}
	}

	static string HashString( string text )
	{
		ulong hash = 14695981039346656037UL;
		foreach ( var b in Encoding.UTF8.GetBytes( text ) )
		{
			hash ^= b;
			hash *= 1099511628211UL;
		}
		return hash.ToString( "x16", CultureInfo.InvariantCulture );
	}

	static string Q( float v ) => MathF.Round( v, 3 ).ToString( "R", CultureInfo.InvariantCulture );
	static string Q( Vec3 v ) => Q( v.x ) + "," + Q( v.y ) + "," + Q( v.z );

	/// <summary>
	/// Reinhard tone mapping for an HDR luxel: exposure, then c/(1+c) per component.
	/// The result is linear 0..1 (an atlas exporter still applies the sRGB encode).
	/// ToneMap(0) is 0; ToneMap is monotonic; nothing above 1 ever comes out.
	/// </summary>
	public static Vec3 ToneMap( Vec3 hdr, float exposure = 1f )
	{
		if ( exposure <= 0f || !float.IsFinite( exposure ) )
			throw new ArgumentException( $"ToneMap: exposure {exposure} must be positive and finite." );
		var c = hdr * exposure;
		return new Vec3(
			MathF.Max( 0f, c.x ) / (1f + MathF.Max( 0f, c.x )),
			MathF.Max( 0f, c.y ) / (1f + MathF.Max( 0f, c.y )),
			MathF.Max( 0f, c.z ) / (1f + MathF.Max( 0f, c.z )) );
	}

	/// <summary>
	/// The differential form factor from a differential patch at
	/// <paramref name="fromPoint"/> with <paramref name="fromNormal"/> to a finite
	/// patch centred at <paramref name="toCentre"/> with <paramref name="toNormal"/>
	/// and area <paramref name="toArea"/>: cos(theta_from)*cos(theta_to)*A_to/(pi*r^2).
	/// No visibility test, no clamp, no subdivision — the raw formula. Zero when
	/// either patch faces away, when the patches coincide, or when the area is not
	/// positive. The near-field limit is real: this approximation is only accurate
	/// when the patches are far apart relative to their size; the bake's row builder
	/// subdivides near receivers instead (see BuildFormFactorRows). Analytic anchor:
	/// summing this over small patches of a parallel disk of radius R at distance S
	/// converges to R^2/(R^2+S^2); summing it over a closed box from one patch
	/// converges to 1 (energy conservation).
	/// </summary>
	public static float FormFactor( Vec3 fromPoint, Vec3 fromNormal, Vec3 toCentre, Vec3 toNormal, float toArea )
	{
		if ( toArea <= 0f ) return 0f;
		var delta = toCentre - fromPoint;
		var r2 = delta.LengthSquared;
		if ( r2 <= 1e-12f ) return 0f;
		var r = MathF.Sqrt( r2 );
		var dir = delta / r;
		var cosFrom = Vec3.Dot( fromNormal, dir );
		if ( cosFrom <= 0f ) return 0f;
		var cosTo = Vec3.Dot( toNormal, -dir );
		if ( cosTo <= 0f ) return 0f;
		return cosFrom * cosTo * toArea / (MathF.PI * r2);
	}

	static void ValidateFaces( IReadOnlyList<LiveFace> faces )
	{
		var seen = new HashSet<int>();
		foreach ( var face in faces )
		{
			if ( face is null ) throw new ArgumentException( "Bake: the face list contains a null face." );
			if ( !seen.Add( face.Id ) )
				throw new ArgumentException( $"Bake: duplicate face id {face.Id}." );
			if ( face.Normal.LengthSquared <= 1e-12f )
				throw new ArgumentException( $"Bake: face {face.Id} is zero-area (its normal is zero-length)." );
			if ( face.LightmapScale <= 0f )
				throw new ArgumentException( $"Bake: face {face.Id} has lightmap scale {face.LightmapScale}: it must be positive." );
		}
	}

	static List<Patch> BuildPatches( IReadOnlyList<LiveFace> faces, RadiositySettings settings, List<string> warnings )
	{
		var patches = new List<Patch>();
		for ( var faceIndex = 0; faceIndex < faces.Count; faceIndex++ )
		{
			var face = faces[faceIndex];
			if ( face.IsNoDraw ) continue;
			var luxelSize = face.LightmapScale * settings.LuxelScaleMultiplier;
			var (uAxis, vAxis) = FaceUv.PlaneAxes( face.Normal );
			var (uMin, uMax, vMin, vMax) = FaceUv.ProjectedBounds( face.Vertices, face.Normal );
			var uExtent = uMax - uMin;
			var vExtent = vMax - vMin;
			var cols = Math.Clamp( (int)MathF.Ceiling( uExtent / luxelSize ), 1, settings.MaxGridPerAxis );
			var rows = Math.Clamp( (int)MathF.Ceiling( vExtent / luxelSize ), 1, settings.MaxGridPerAxis );
			if ( (int)MathF.Ceiling( uExtent / luxelSize ) > settings.MaxGridPerAxis ||
				(int)MathF.Ceiling( vExtent / luxelSize ) > settings.MaxGridPerAxis )
				warnings.Add( $"Face {face.Id}: luxel grid capped at {rows}x{cols} by MaxGridPerAxis {settings.MaxGridPerAxis} (lightmap scale {face.LightmapScale} wants a finer grid)." );
			var origin = face.Vertices[0];
			var area = PolygonArea( face.Vertices, face.Normal ) / (rows * cols);
			for ( var row = 0; row < rows; row++ )
			{
				for ( var col = 0; col < cols; col++ )
				{
					var u0 = uMin + col * uExtent / cols;
					var u1 = uMin + (col + 1) * uExtent / cols;
					var v0 = vMin + row * vExtent / rows;
					var v1 = vMin + (row + 1) * vExtent / rows;
					Vec3 Point( float u, float v ) => origin + uAxis * u + vAxis * v;
					patches.Add( new Patch
					{
						FaceIndex = faceIndex,
						FaceId = face.Id,
						Row = row,
						Col = col,
						Centre = Point( (u0 + u1) * 0.5f, (v0 + v1) * 0.5f ),
						Normal = face.Normal,
						Albedo = face.Albedo,
						Area = area,
						Corners = new[] { Point( u0, v0 ), Point( u1, v0 ), Point( u1, v1 ), Point( u0, v1 ) },
					} );
				}
			}
		}
		return patches;
	}

	static float PolygonArea( IReadOnlyList<Vec3> vertices, Vec3 normal )
	{
		var n = Vec3.Zero;
		for ( var i = 0; i < vertices.Count; i++ )
		{
			var a = vertices[i];
			var b = vertices[(i + 1) % vertices.Count];
			n += new Vec3(
				(a.y - b.y) * (a.z + b.z),
				(a.z - b.z) * (a.x + b.x),
				(a.x - b.x) * (a.y + b.y) );
		}
		return n.Length * 0.5f;
	}

	// Direct irradiance at a point from ALL lights: LiveLighting's light model
	// (same falloff, same spot cone, same sun, same ambient handling) PLUS a shadow
	// test per light. Environment lights contribute 0 here — the caller adds the
	// ambient term separately, so the bounce source stays exact.
	static Vec3 DirectIrradiance( Vec3 point, Vec3 normal, IReadOnlyList<LiveLight> lights,
		IReadOnlyList<LiveFace> faces, int ownFaceId )
	{
		var total = Vec3.Zero;
		foreach ( var light in lights )
			total += DirectFromLight( point, normal, light, faces, ownFaceId );
		return total;
	}

	static Vec3 DirectFromLight( Vec3 point, Vec3 normal, LiveLight light,
		IReadOnlyList<LiveFace> faces, int ownFaceId )
	{
		switch ( light.Kind )
		{
			case LiveLightKind.Environment:
				return Vec3.Zero;
			case LiveLightKind.Sun:
			{
				var nDotL = MathF.Max( 0f, Vec3.Dot( normal, -light.Direction ) );
				if ( nDotL <= 0f ) return Vec3.Zero;
				// Ray toward the sun: blocked by anything except the patch's own face.
				if ( RayBlocked( point + normal * RayOriginEpsilon, -light.Direction, 1e6f, faces, ownFaceId ) )
					return Vec3.Zero;
				return light.Color * (light.Intensity * nDotL);
			}
			case LiveLightKind.Point:
			case LiveLightKind.Spot:
			{
				var toLight = light.Position - point;
				var d = toLight.Length;
				if ( d <= 1e-6f ) return light.Color * light.Intensity;
				if ( light.Range > 0f && d > light.Range ) return Vec3.Zero;
				var dir = toLight / d;
				var nDotL = MathF.Max( 0f, Vec3.Dot( normal, dir ) );
				if ( nDotL <= 0f ) return Vec3.Zero;
				var falloff = 1f / (1f + (d * d) / (FalloffHalfDistance * FalloffHalfDistance));
				var amount = light.Intensity * nDotL * falloff;
				if ( light.Kind == LiveLightKind.Spot )
				{
					// dir points from the sample TOWARD the light; the cone opens along
					// Direction (light -> scene), so the cone angle uses -dir.
					var cosAngle = Vec3.Dot( -dir, light.Direction );
					var cosInner = MathF.Cos( light.InnerConeDegrees * MathF.PI / 180f );
					var cosOuter = MathF.Cos( light.OuterConeDegrees * MathF.PI / 180f );
					if ( cosAngle <= cosOuter ) return Vec3.Zero;
					if ( cosAngle < cosInner )
						amount *= Smoothstep( (cosAngle - cosOuter) / (cosInner - cosOuter) );
				}
				if ( amount <= 0f ) return Vec3.Zero;
				if ( SegmentBlocked( point + normal * RayOriginEpsilon, light.Position, faces, ownFaceId, -1 ) )
					return Vec3.Zero;
				return light.Color * amount;
			}
			default:
				throw new ArgumentException( $"Unknown light kind: {light.Kind}." );
		}
	}

	// AO factor at a point: 1 - AoStrength * (blocked rays / AoRays), from a
	// deterministic cosine-weighted Hammersley hemisphere. No occluders in range
	// means exactly 1; a fully enclosed point tends to 1 - AoStrength.
	static float AmbientOcclusion( Vec3 point, Vec3 normal, IReadOnlyList<LiveFace> faces, int ownFaceId, RadiositySettings settings )
	{
		if ( settings.AoRays <= 0 ) return 1f;
		var (tangent, bitangent) = TangentBasis( normal );
		var origin = point + normal * RayOriginEpsilon;
		var blocked = 0;
		for ( var i = 0; i < settings.AoRays; i++ )
		{
			var u1 = (i + 0.5f) / settings.AoRays;
			var u2 = RadicalInverseBase2( i );
			var r = MathF.Sqrt( u2 );
			var phi = 2f * MathF.PI * u1;
			var dir = tangent * (r * MathF.Cos( phi )) + bitangent * (r * MathF.Sin( phi )) + normal * MathF.Sqrt( 1f - u2 );
			if ( RayBlocked( origin, dir.Normal, settings.AoDistance, faces, ownFaceId ) )
				blocked++;
		}
		return 1f - settings.AoStrength * (float)blocked / settings.AoRays;
	}

	static (Vec3 tangent, Vec3 bitangent) TangentBasis( Vec3 normal )
	{
		var n = normal.Normal;
		var helper = MathF.Abs( n.y ) < 0.9f ? new Vec3( 0f, 1f, 0f ) : new Vec3( 1f, 0f, 0f );
		var tangent = Vec3.Cross( helper, n ).Normal;
		var bitangent = Vec3.Cross( n, tangent ).Normal;
		return (tangent, bitangent);
	}

	// Radical inverse, base 2 (van der Corput): the Hammersley sequence's second
	// dimension. Fixed bit reversal over 32 bits — deterministic, no Random.
	static float RadicalInverseBase2( int i )
	{
		uint bits = (uint)i;
		bits = (bits << 16) | (bits >> 16);
		bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
		bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
		bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
		bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
		return bits / 4294967296f;
	}

	// Sparse form-factor rows: for each shooter, the (receiver, F) pairs with both
	// cosines positive and a clear line of sight. A row that sums above 1 is scaled
	// back to 1 (the differential approximation overshoots for near pairs; energy
	// cannot be created). Near receivers are subdivided for accuracy.
	static List<(int receiver, float f)>[] BuildFormFactorRows( List<Patch> patches, IReadOnlyList<LiveFace> faces )
	{
		var rows = new List<(int receiver, float f)>[patches.Count];
		for ( var s = 0; s < patches.Count; s++ )
		{
			var shooter = patches[s];
			var row = new List<(int receiver, float f)>();
			var sum = 0f;
			for ( var i = 0; i < patches.Count; i++ )
			{
				if ( i == s ) continue;
				var receiver = patches[i];
				if ( receiver.FaceId == shooter.FaceId ) continue;
				var delta = receiver.Centre - shooter.Centre;
				var r2 = delta.LengthSquared;
				if ( r2 <= 1e-12f ) continue;
				var r = MathF.Sqrt( r2 );
				var dir = delta / r;
				if ( Vec3.Dot( shooter.Normal, dir ) <= 0f ) continue;
				if ( Vec3.Dot( receiver.Normal, -dir ) <= 0f ) continue;
				if ( SegmentBlocked( shooter.Centre, receiver.Centre, faces, shooter.FaceId, receiver.FaceId ) ) continue;
				var f = FormFactorToPatch( shooter, receiver.Corners, receiver.Centre, receiver.Normal, receiver.Area, 0 );
				if ( f <= 0f ) continue;
				row.Add( (i, f) );
				sum += f;
			}
			if ( sum > 1f )
			{
				var scale = 1f / sum;
				for ( var k = 0; k < row.Count; k++ )
					row[k] = (row[k].receiver, row[k].f * scale);
			}
			rows[s] = row;
		}
		return rows;
	}

	// Form factor from a shooter patch to a receiver patch, subdividing the receiver
	// while it is close relative to its own size (r <= 2*sqrt(area)) and depth
	// remains. Subdivision is by bilinear split of the receiver quad; visibility was
	// already tested once at the patch level by the caller.
	static float FormFactorToPatch( Patch shooter, Vec3[] corners, Vec3 centre, Vec3 normal, float area, int depth )
	{
		var delta = centre - shooter.Centre;
		var r = delta.Length;
		if ( r <= 1e-6f ) return 0f;
		if ( depth >= MaxSubdivisionDepth || r > NearFieldSubdivisionFactor * MathF.Sqrt( area ) )
			return FormFactor( shooter.Centre, shooter.Normal, centre, normal, area );
		// Split the quad into 4 sub-quads (bilinear midpoints) and sum. Each sub-quad
		// carries its own corners so a deeper split divides the right quad.
		var c = corners;
		var m01 = (c[0] + c[1]) * 0.5f;
		var m12 = (c[1] + c[2]) * 0.5f;
		var m23 = (c[2] + c[3]) * 0.5f;
		var m30 = (c[3] + c[0]) * 0.5f;
		var mid = (c[0] + c[1] + c[2] + c[3]) * 0.25f;
		var subArea = area * 0.25f;
		var subQuads = new[]
		{
			(new[] { c[0], m01, mid, m30 }, (c[0] + m01 + mid + m30) * 0.25f),
			(new[] { m01, c[1], m12, mid }, (m01 + c[1] + m12 + mid) * 0.25f),
			(new[] { mid, m12, c[2], m23 }, (mid + m12 + c[2] + m23) * 0.25f),
			(new[] { m30, mid, m23, c[3] }, (m30 + mid + m23 + c[3]) * 0.25f),
		};
		var total = 0f;
		foreach ( var (subCorners, subCentre) in subQuads )
			total += FormFactorToPatch( shooter, subCorners, subCentre, normal, subArea, depth + 1 );
		return total;
	}

	static float MaxUnshot( List<Patch> patches )
	{
		var max = 0f;
		foreach ( var patch in patches )
		{
			max = MathF.Max( max, patch.Unshot.x );
			max = MathF.Max( max, patch.Unshot.y );
			max = MathF.Max( max, patch.Unshot.z );
		}
		return max;
	}

	static (List<RadiosityFaceResult> faces, List<RadiosityPatchSample> samples) AssembleResults(
		IReadOnlyList<LiveFace> faces, List<Patch> patches, Vec3 ambient )
	{
		var byFace = new Dictionary<int, List<Patch>>();
		foreach ( var patch in patches )
		{
			if ( !byFace.TryGetValue( patch.FaceIndex, out var list ) )
			{
				list = new List<Patch>();
				byFace[patch.FaceIndex] = list;
			}
			list.Add( patch );
		}
		var results = new List<RadiosityFaceResult>( faces.Count );
		var samples = new List<RadiosityPatchSample>( patches.Count );
		for ( var faceIndex = 0; faceIndex < faces.Count; faceIndex++ )
		{
			var face = faces[faceIndex];
			if ( face.IsNoDraw )
			{
				results.Add( new RadiosityFaceResult( face.Id, 0, 0, face.LightmapScale,
					Array.Empty<Vec3>(), Array.Empty<Vec3>(), Array.Empty<Vec3>(), Array.Empty<float>(), skippedNoDraw: true ) );
				continue;
			}
			var facePatches = byFace[faceIndex];
			var rows = facePatches.Max( p => p.Row ) + 1;
			var cols = facePatches.Max( p => p.Col ) + 1;
			var luxels = new Vec3[rows * cols];
			var directLuxels = new Vec3[rows * cols];
			var indirectLuxels = new Vec3[rows * cols];
			var aoFactors = new float[rows * cols];
			foreach ( var patch in facePatches )
			{
				var at = patch.Row * cols + patch.Col;
				directLuxels[at] = Mul( patch.Albedo, patch.Direct );
				indirectLuxels[at] = Mul( patch.Albedo, (ambient + patch.Indirect) * patch.AoFactor );
				luxels[at] = directLuxels[at] + indirectLuxels[at];
				aoFactors[at] = patch.AoFactor;
				samples.Add( new RadiosityPatchSample( patch.FaceId, patch.Centre, patch.Normal, patch.Area,
					Mul( patch.Albedo, patch.Direct + ambient + patch.Indirect ) ) );
			}
			results.Add( new RadiosityFaceResult( face.Id, rows, cols, face.LightmapScale,
				luxels, directLuxels, indirectLuxels, aoFactors, skippedNoDraw: false ) );
		}
		return (results, samples);
	}

	// Indirect light at a prop vertex: inverse-distance weighted average of nearby
	// patch outgoing radiosity. A vertex far from every patch gets no indirect.
	static Vec3 SampleIndirect( Vec3 position, Vec3 normal, IReadOnlyList<RadiosityPatchSample> samples, float radius )
	{
		var sum = Vec3.Zero;
		var weightSum = 0f;
		foreach ( var sample in samples )
		{
			var delta = sample.Centre - position;
			var d = delta.Length;
			if ( d > radius ) continue;
			// Only patches facing the vertex contribute (a patch's back side is dark).
			if ( Vec3.Dot( sample.Normal, -delta / MathF.Max( d, 1e-6f ) ) <= 0f && d > 1e-6f ) continue;
			var weight = 1f / (1f + (d / radius) * (d / radius));
			sum += sample.Outgoing * weight;
			weightSum += weight;
		}
		return weightSum <= 0f ? Vec3.Zero : sum / weightSum;
	}

	// --- Ray casting (double-sided; occluders are every face except the ignored ones) ---

	/// <summary>True when any face (except <paramref name="ignoreFaceId"/>) blocks the segment a→b, strictly between the endpoints.</summary>
	public static bool SegmentBlocked( Vec3 a, Vec3 b, IReadOnlyList<LiveFace> faces, int ignoreFaceId, int ignoreFaceId2 = -1 )
	{
		var delta = b - a;
		var len = delta.Length;
		if ( len <= 1e-9f ) return false;
		return RayBlocked( a, delta / len, len, faces, ignoreFaceId, ignoreFaceId2 );
	}

	/// <summary>True when any face (except the ignored ones) intersects the ray origin + dir*t for t in (RayOriginEpsilon, <paramref name="maxDistance"/> - RayOriginEpsilon].</summary>
	public static bool RayBlocked( Vec3 origin, Vec3 direction, float maxDistance, IReadOnlyList<LiveFace> faces, int ignoreFaceId, int ignoreFaceId2 = -1 )
	{
		if ( faces is null ) return false;
		foreach ( var face in faces )
		{
			if ( face is null || face.Id == ignoreFaceId || face.Id == ignoreFaceId2 ) continue;
			var verts = face.Vertices;
			for ( var i = 1; i + 1 < verts.Count; i++ )
			{
				if ( RayHitsTriangle( origin, direction, verts[0], verts[i], verts[i + 1], out var t ) &&
					t > RayOriginEpsilon && t <= maxDistance - RayOriginEpsilon )
					return true;
			}
		}
		return false;
	}

	// Möller–Trumbore, double-sided (no backface culling: a wall blocks from either side).
	static bool RayHitsTriangle( Vec3 origin, Vec3 dir, Vec3 v0, Vec3 v1, Vec3 v2, out float t )
	{
		t = 0f;
		var edge1 = v1 - v0;
		var edge2 = v2 - v0;
		var p = Vec3.Cross( dir, edge2 );
		var det = Vec3.Dot( edge1, p );
		if ( MathF.Abs( det ) < 1e-12f ) return false;
		var invDet = 1f / det;
		var tvec = origin - v0;
		var u = Vec3.Dot( tvec, p ) * invDet;
		if ( u < -1e-6f || u > 1f + 1e-6f ) return false;
		var q = Vec3.Cross( tvec, edge1 );
		var v = Vec3.Dot( dir, q ) * invDet;
		if ( v < -1e-6f || u + v > 1f + 1e-6f ) return false;
		t = Vec3.Dot( edge2, q ) * invDet;
		return t > 0f;
	}
}

/// <summary>
/// One lightmap atlas chart: a face's luxel grid, to place on an atlas page.
/// </summary>
public sealed class AtlasChart
{
	public int FaceId { get; }
	public int Cols { get; }
	public int Rows { get; }

	public AtlasChart( int faceId, int cols, int rows )
	{
		if ( cols < 1 ) throw new ArgumentException( $"Atlas chart for face {faceId}: cols {cols} must be at least 1." );
		if ( rows < 1 ) throw new ArgumentException( $"Atlas chart for face {faceId}: rows {rows} must be at least 1." );
		FaceId = faceId;
		Cols = cols;
		Rows = rows;
	}
}

/// <summary>
/// One placed chart on an atlas page: integer texel rect plus its 0..1 UV rect.
/// The rect is the chart alone; the <see cref="RadiosityAtlasPacker"/> padding
/// gutter sits OUTSIDE it, so neighbouring charts never bleed into each other's
/// luxels (the gutter is what the "atlas improvements" in the ticket buy).
/// </summary>
public sealed class AtlasPlacement
{
	public int FaceId { get; }
	public int Page { get; }
	public int X { get; }
	public int Y { get; }
	public int Cols { get; }
	public int Rows { get; }
	public float U0 { get; }
	public float V0 { get; }
	public float U1 { get; }
	public float V1 { get; }

	public AtlasPlacement( int faceId, int page, int x, int y, int cols, int rows, int atlasSize )
	{
		FaceId = faceId;
		Page = page;
		X = x;
		Y = y;
		Cols = cols;
		Rows = rows;
		U0 = (float)x / atlasSize;
		V0 = (float)y / atlasSize;
		U1 = (float)(x + cols) / atlasSize;
		V1 = (float)(y + rows) / atlasSize;
	}
}

/// <summary>The result of packing charts onto atlas pages.</summary>
public sealed class AtlasPackResult
{
	public IReadOnlyList<AtlasPlacement> Placements { get; }
	public int PageCount { get; }
	public int AtlasSize { get; }
	public int PaddingTexels { get; }
	/// <summary>Chart texels / (pages * atlasSize^2): 0..1, higher is tighter.</summary>
	public float Utilization { get; }

	public AtlasPackResult( IReadOnlyList<AtlasPlacement> placements, int pageCount, int atlasSize, int paddingTexels, float utilization )
	{
		Placements = placements;
		PageCount = pageCount;
		AtlasSize = atlasSize;
		PaddingTexels = paddingTexels;
		Utilization = utilization;
	}

	/// <summary>The placement for a face id, or null when the pack had no such chart.</summary>
	public AtlasPlacement PlacementFor( int faceId )
	{
		foreach ( var p in Placements )
			if ( p.FaceId == faceId ) return p;
		return null;
	}
}

/// <summary>
/// Lightmap atlas packing with a padding gutter. Deterministic shelf packing:
/// charts sorted by height (then width, then face id), placed left-to-right on a
/// shelf, a new shelf when the row is full, a new page when the page is full.
/// Padding is a gutter between charts AND at the page edges, so a chart's luxels
/// never bleed into its neighbour's when the atlas is sampled with bilinear
/// filtering. A LightmapBake UVUnwrap chart feeds this directly as
/// (faceId, cols, rows). Throws for null/duplicate charts, a chart larger than the
/// atlas, or a non-power-of-two atlas size.
/// </summary>
public static class RadiosityAtlasPacker
{
	/// <summary>Pack charts onto square atlas pages of <paramref name="atlasSize"/> texels.</summary>
	public static AtlasPackResult Pack( IReadOnlyList<AtlasChart> charts, int atlasSize, int paddingTexels = 2 )
	{
		if ( charts is null ) throw new ArgumentNullException( nameof( charts ) );
		if ( atlasSize < 16 || atlasSize > 8192 || (atlasSize & (atlasSize - 1)) != 0 )
			throw new ArgumentException( $"Atlas size {atlasSize} is not a power of two between 16 and 8192." );
		if ( paddingTexels < 0 )
			throw new ArgumentException( $"Atlas padding {paddingTexels} must be >= 0." );
		var seen = new HashSet<int>();
		foreach ( var chart in charts )
		{
			if ( chart is null ) throw new ArgumentException( "Pack: the chart list contains a null chart." );
			if ( !seen.Add( chart.FaceId ) )
				throw new ArgumentException( $"Pack: duplicate chart for face {chart.FaceId}." );
			if ( chart.Cols > atlasSize || chart.Rows > atlasSize )
				throw new ArgumentException( $"Pack: chart for face {chart.FaceId} is {chart.Cols}x{chart.Rows}, larger than the {atlasSize} atlas." );
		}

		var sorted = charts.OrderByDescending( c => c.Rows ).ThenByDescending( c => c.Cols ).ThenBy( c => c.FaceId ).ToList();
		var placements = new List<AtlasPlacement>();
		var page = 0;
		var x = paddingTexels;
		var y = paddingTexels;
		var shelfHeight = 0;
		var chartTexels = 0;
		foreach ( var chart in sorted )
		{
			chartTexels += chart.Cols * chart.Rows;
			if ( x + chart.Cols > atlasSize - paddingTexels )
			{
				// New shelf.
				x = paddingTexels;
				y += shelfHeight + paddingTexels;
				shelfHeight = 0;
			}
			if ( y + chart.Rows > atlasSize - paddingTexels )
			{
				// New page.
				page++;
				x = paddingTexels;
				y = paddingTexels;
				shelfHeight = 0;
			}
			placements.Add( new AtlasPlacement( chart.FaceId, page, x, y, chart.Cols, chart.Rows, atlasSize ) );
			x += chart.Cols + paddingTexels;
			if ( chart.Rows > shelfHeight ) shelfHeight = chart.Rows;
		}
		var pageCount = charts.Count == 0 ? 0 : page + 1;
		var utilization = pageCount == 0 ? 0f : (float)chartTexels / (pageCount * atlasSize * atlasSize);
		return new AtlasPackResult( placements, pageCount, atlasSize, paddingTexels, utilization );
	}
}
