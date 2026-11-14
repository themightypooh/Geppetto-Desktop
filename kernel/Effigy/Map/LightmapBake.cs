// Hammer Desktop P2: progressive native lightmap bake (low-res first, refine in background).
// Board task hammer-lightmap-progressive-bake.
//
// WHAT THIS IS. The real progressive bake, on the Effigy Baker pattern: a BakeJob-shaped
// orchestrator (progress + cancel, per-face fingerprints, change invalidation, never throws
// for a bake failure) over LiveFace + LiveLight inputs — the same input types the live
// preview (LiveLighting) evaluates, so the preview and the bake agree on the light model
// and the bake's per-face averages feed LiveLighting Full as its bounce source.
//
//   - Low-res first: BakeCoarse shades a coarse luxel grid (effective lightmap scale x
//     CoarseFactor, direct + ambient only) — the early preview Unity's Progressive
//     pattern shows while the bake works. Refine (or Run, which does both) then shades
//     the full-resolution grid and adds bounce. The refined result carries its coarse
//     result (LightmapBakeResult.Coarse) so a caller can show the preview, then swap.
//   - Atlas: each face gets a scale-aware rect in one atlas (shelf-pack with a 1-luxel
//     gutter). A face's grid is ceil(extent / lightmapScale) per axis, so per-face
//     lightmap scale is honoured: scale 4 vs 1 is 4x fewer luxels per axis on that face.
//     A face that does not fit the atlas is a named error, never a silent downscale.
//   - Direct + bounce, with real shadows: every luxel's direct light is shadow-tested
//     against every face (ray-triangle, nodraw faces still block — they are solid), and
//     bounce is a radiosity-style gather between face centroids (form factor x
//     visibility, BouncePasses iterations). HDR linear, no clamp: the values are light.
//   - Change invalidation: PlanRebake fingerprints every face (geometry, material,
//     lightmap scale, albedo) plus the lights and the options. BakeIncremental re-shades
//     only the dirty faces' direct luxels, reuses the clean faces' luxels, and recomputes
//     the (cheap) bounce gather globally. A light or option change dirties everything.
//   - GPU trust rule (the GpuBake 1K rule, applied here): the GPU path is only trusted
//     after its 1K test-bake (atlas 1024) matches the CPU reference per-face averages
//     within tolerance. Without a passing test-bake the GPU path is refused with a named
//     reason (TryGpuBake.RefusedReason) and the CPU bake is returned instead — the GPU
//     result is never silently shipped, and never silently replaced by CPU either: the
//     route says which path produced the result.
//
// LIGHT MODEL — deliberately the SAME as LiveLighting (the preview): point/spot falloff
// 1/(1+(d/64)^2), spot smooth cone falloff, sun NdotL with no falloff, environment as a
// uniform ambient, luxel = (direct + ambient + bounce) * albedo, luxel sample points at
// cell centres over FaceUv.ProjectedBounds. Direct() below duplicates LiveLighting's
// private Direct() line for line BECAUSE it is private and this file may not edit it
// (read-only); the cross-check test (preview Draft vs this bake on the analytic
// single-light case, tolerance 0.05, via LiveLighting.CompareToBake) enforces the match,
// and any drift fails loudly there instead of shipping two light models.
// Differences from the preview, BY DESIGN: this bake has shadows (the preview has
// none — a sealed room's floor loses its sun under the ceiling here, correctly) and a
// real inter-face bounce gather (the preview's Full bounce is 0.35 x the face's own
// baked average). So preview-vs-bake agrees at 0.05 ONLY on shadow-free setups; on a
// sealed room the bake is darker where shadows fall, and the tests assert that.
// Differences from CompileRunner's lightmap stage (worker 22, in progress): that stage
// is the compile's single-pass fixed-square-resolution direct bake (falloff 1/(1+(d/128)^2),
// RGB clamped to 1, no ambient, no bounce, no atlas, no sun/spot kinds). This bake is
// the progressive EDITOR bake; the integrator wires it into the compile's LightmapBake
// stage later — this file does NOT touch CompileRunner.cs.
//
// SEAMS (filed specs owned by parallel workers):
//   - Lighting entities (worker 54, hammer-lighting-environment-sun-fog, in progress):
//     light/light_spot/light_environment/env_sun entities convert to LiveLight via its
//     factories; this bake consumes LiveLight, so no change here when that lands. Fog,
//     shadow_control and env_tonemap_controller have no bake effect yet (fog is a
//     view effect, tonemap is encode-time); they are NOT read here.
//   - Compile pipeline (worker 22, hammer-compile-native-pipeline, in progress):
//     CompileInput/CompileBrush carry planes, not polygons, and its BuildBrushPolygons
//     is internal, so there is no FromCompileInput adapter yet — faces come from the
//     MapDocument adapter (one LiveFace per brush face) when it lands, exactly as the
//     preview's adapter does. CompileRunner.BakeLightmaps stays the compile's stage
//     until the integrator swaps this bake in.
//   - UVUnwrap (atlas): UVUnwrap packs charts at ONE uniform scale (uniform texel
//     density), which cannot express per-face lightmap scale. This bake therefore packs
//     its own scale-aware shelf atlas (same gutter principle); a future UVUnwrap-based
//     packer can replace PackAtlas without changing the result shape.
//   - bake-reference.json: worker 23's file (tests/fixtures/hammer/
//     hammer-preview-live-lighting/bake-reference.json) was recorded from the preview
//     engine itself, NOT an independent bake. The real reference from THIS bake lives
//     in tests/fixtures/hammer/hammer-lightmap-progressive-bake/bake-reference.json;
//     the integrator copies it over worker 23's file.
//
// UNITS. Kernel inches, Z-up. Colours are LINEAR RGB, HDR (may exceed 1). Deterministic:
// same faces + lights + options give the same luxels (no clock, no randomness, no
// hash-order dependence; faces shade in input order, fingerprints sort by id).
// NATIVE ONLY: no Hammer, no shelling out. No UI. No IO — the caller writes results.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace Effigy.Map;

/// <summary>Which pass of the progressive bake a result is from.</summary>
public enum LightmapBakePass
{
	/// <summary>Low-res first pass: coarse grid, direct + ambient only, no bounce.</summary>
	Coarse,
	/// <summary>Full-resolution pass: full grid, direct + ambient + bounce.</summary>
	Refined,
}

/// <summary>Options for a <see cref="LightmapBakeJob"/>. Validated by the job constructor.</summary>
public sealed class LightmapBakeOptions
{
	/// <summary>Atlas side in texels (square, power of two). Default 1024.</summary>
	public int AtlasSize { get; set; } = 1024;
	/// <summary>Coarse pass multiplies every face's lightmap scale by this (default 4): a 256-unit face at scale 16 bakes 4x4 coarse, 16x16 refined.</summary>
	public int CoarseFactor { get; set; } = 4;
	/// <summary>Bounce gather iterations in the refined pass (default 1). 0 = direct + ambient only, even when refined.</summary>
	public int BouncePasses { get; set; } = 1;
	/// <summary>Per-axis cap on a face's luxel grid (default 64). A face needing more is clamped, never an error.</summary>
	public int MaxGridPerAxis { get; set; } = 64;
	/// <summary>Form-factor scale for the bounce gather (default 0.35, the same factor LiveLighting Full applies to a baked average).</summary>
	public float BounceFactor { get; set; } = 0.35f;
	/// <summary>Faces further apart than this (centroid distance, units) exchange no bounce (default 1024). Bounds the O(faces^2) gather.</summary>
	public float BounceMaxDistance { get; set; } = 1024f;
	/// <summary>Ray origin offset along the face normal, units (default 0.05): keeps a luxel's own face from shadowing itself.</summary>
	public float ShadowEpsilon { get; set; } = 0.05f;
	/// <summary>GPU trust rule: the 1K test-bake size (atlas side, default 1024).</summary>
	public int GpuTestSize { get; set; } = 1024;
	/// <summary>GPU trust rule: mean absolute per-face-average deviation (HDR units) the test-bake may have (default 0.05).</summary>
	public double GpuMeanTolerance { get; set; } = 0.05;
	/// <summary>GPU trust rule: max absolute per-face-average deviation (HDR units) the test-bake may have (default 0.25).</summary>
	public double GpuMaxTolerance { get; set; } = 0.25;

	/// <summary>Named defects, empty when usable. The job constructor throws with these listed.</summary>
	public List<string> Validate()
	{
		var defects = new List<string>();
		if ( AtlasSize < 64 || AtlasSize > 4096 || ( AtlasSize & ( AtlasSize - 1 ) ) != 0 )
			defects.Add( $"AtlasSize {AtlasSize} is not a power of two between 64 and 4096." );
		if ( CoarseFactor < 1 )
			defects.Add( $"CoarseFactor {CoarseFactor} must be at least 1." );
		if ( BouncePasses < 0 || BouncePasses > 4 )
			defects.Add( $"BouncePasses {BouncePasses} must be 0..4." );
		if ( MaxGridPerAxis < 1 || MaxGridPerAxis > 256 )
			defects.Add( $"MaxGridPerAxis {MaxGridPerAxis} must be 1..256." );
		if ( BounceFactor < 0f )
			defects.Add( $"BounceFactor {BounceFactor} must not be negative." );
		if ( BounceMaxDistance <= 0f )
			defects.Add( $"BounceMaxDistance {BounceMaxDistance} must be positive." );
		if ( ShadowEpsilon < 0f )
			defects.Add( $"ShadowEpsilon {ShadowEpsilon} must not be negative." );
		if ( GpuTestSize < 64 || GpuTestSize > 4096 || ( GpuTestSize & ( GpuTestSize - 1 ) ) != 0 )
			defects.Add( $"GpuTestSize {GpuTestSize} is not a power of two between 64 and 4096." );
		if ( GpuMeanTolerance < 0.0 || GpuMaxTolerance < 0.0 )
			defects.Add( $"GPU tolerances mean {GpuMeanTolerance} / max {GpuMaxTolerance} must not be negative." );
		return defects;
	}
}

/// <summary>One face's baked lightmap: its luxel grid plus where it sits in the atlas.</summary>
public sealed class LightmapFaceBake
{
	public int FaceId { get; }
	/// <summary>Luxel rows (along the face V axis). 0 for a skipped (nodraw) face.</summary>
	public int Rows { get; }
	/// <summary>Luxel columns (along the face U axis). 0 for a skipped face.</summary>
	public int Cols { get; }
	/// <summary>Nominal world units per luxel (the face's lightmap scale, x CoarseFactor on a coarse pass).</summary>
	public float LuxelSize { get; }
	/// <summary>Rows*Cols HDR linear luxels, row-major (row 0 is the smallest V). Empty for a skipped face.</summary>
	public IReadOnlyList<Vec3> Luxels { get; }
	/// <summary>Mean luxel colour over inside-polygon luxels; Zero for a skipped face.</summary>
	public Vec3 Average { get; }
	/// <summary>Component-wise maximum luxel colour; Zero for a skipped face.</summary>
	public Vec3 Max { get; }
	/// <summary>Mean direct + ambient irradiance over inside-polygon luxels (before albedo, before bounce): the bounce gather's outgoing source.</summary>
	public Vec3 DirectAverage { get; }
	/// <summary>Bounce irradiance applied to this face (uniform over the face); Zero on a coarse pass.</summary>
	public Vec3 BounceAverage { get; }
	/// <summary>Atlas rect in 0..1 UV (u0, v0, u1, v1); (0,0,0,0) for a skipped face.</summary>
	public (float U0, float V0, float U1, float V1) AtlasRect { get; }
	public bool SkippedNoDraw { get; }
	/// <summary>True when this face was baked direct-only (coarse pass).</summary>
	public bool DirectOnly { get; }
	/// <summary>Per-luxel inside-polygon mask, row-major (same length as Luxels): outside cells are stored Zero. Empty for a skipped face.</summary>
	public IReadOnlyList<bool> InsideMask { get; }
	/// <summary>Number of inside-polygon luxels (the count Average is taken over).</summary>
	public int InsideCount { get; }

	internal LightmapFaceBake( int faceId, int rows, int cols, float luxelSize, IReadOnlyList<Vec3> luxels,
		Vec3 directAverage, Vec3 bounceAverage, (float, float, float, float) atlasRect, bool skippedNoDraw, bool directOnly,
		int insideLuxels, bool[] insideMask )
	{
		FaceId = faceId;
		Rows = rows;
		Cols = cols;
		LuxelSize = luxelSize;
		Luxels = luxels;
		DirectAverage = directAverage;
		BounceAverage = bounceAverage;
		AtlasRect = atlasRect;
		SkippedNoDraw = skippedNoDraw;
		DirectOnly = directOnly;
		InsideMask = insideMask ?? Array.Empty<bool>();
		InsideCount = insideLuxels;
		var sum = Vec3.Zero;
		var max = Vec3.Zero;
		foreach ( var l in luxels )
		{
			sum += l;
			max = new Vec3( MathF.Max( max.x, l.x ), MathF.Max( max.y, l.y ), MathF.Max( max.z, l.z ) );
		}
		// Luxels outside the polygon are stored as Zero and must not dilute the average:
		// Average is over inside-polygon luxels only (insideLuxels), which the baker counts.
		Average = insideLuxels <= 0 ? Vec3.Zero : sum / insideLuxels;
		Max = max;
	}

	/// <summary>Luxel (row, col), row-major. Throws for a skipped face or out-of-range indices.</summary>
	public Vec3 LuxelAt( int row, int col )
	{
		if ( row < 0 || row >= Rows || col < 0 || col >= Cols )
			throw new ArgumentOutOfRangeException( $"Face {FaceId}: no luxel at row {row}, col {col} (grid is {Rows}x{Cols})." );
		return Luxels[row * Cols + col];
	}
}

/// <summary>
/// What a <see cref="LightmapBakeJob"/> run produced. A failed or cancelled run has
/// <see cref="Faces"/> null and a readable <see cref="Error"/> (or <see cref="Cancelled"/>);
/// it never throws, and it never writes files.
/// </summary>
public sealed class LightmapBakeResult
{
	public IReadOnlyList<LightmapFaceBake> Faces { get; }
	public LightmapBakePass Pass { get; }
	public int AtlasWidth { get; }
	public int AtlasHeight { get; }
	public int LuxelCount { get; }
	/// <summary>Component-wise maximum luxel over all faces.</summary>
	public Vec3 MaxLuxel { get; }
	public TimeSpan Duration { get; }
	public bool Cancelled { get; }
	public string Error { get; }
	public bool Succeeded => Error is null && !Cancelled && Faces is not null;
	/// <summary>True when an incremental bake reused the previous result untouched.</summary>
	public bool ReusedPrevious { get; }
	/// <summary>Face ids whose luxels were reused from the previous result (incremental bake).</summary>
	public IReadOnlyList<int> ReusedFaceIds { get; }
	/// <summary>Which path produced this result: "cpu", "cpu (coarse preview)", "gpu", or a "cpu (...)" fallback with its reason. Set internally when the GPU path accepts a backend's result.</summary>
	public string Route { get; internal set; }
	/// <summary>Why the GPU path was refused, or null when it was not attempted / was accepted.</summary>
	public string GpuRefusedReason { get; }
	public IReadOnlyList<string> Warnings { get; }
	/// <summary>Whole-job fingerprint (options + faces + lights).</summary>
	public string Fingerprint { get; }
	/// <summary>Per-face fingerprint (geometry + material + lightmap scale + albedo), by face id.</summary>
	public IReadOnlyDictionary<int, string> FaceFingerprints { get; }
	/// <summary>Lights fingerprint this result was baked under.</summary>
	public string LightsFingerprint { get; }
	/// <summary>Options fingerprint this result was baked under.</summary>
	public string OptionsFingerprint { get; }
	/// <summary>On a <see cref="LightmapBakeJob.Run"/> refined result: the coarse preview baked en route. Null otherwise.</summary>
	public LightmapBakeResult Coarse { get; }

	/// <summary>Per-face average luxels (HDR linear), the LiveLighting Full bounce source: face id -> average.</summary>
	public Dictionary<int, Vec3> BakedAveragePerFace { get; }

	/// <summary>Bake-log lines for the mirrored props corrected in this bake (one per
	/// corrected prop, naming the prop id and its scale). Empty when the job had no
	/// mirrored props. Zero-scale props are not corrections — they are reported in
	/// <see cref="Warnings"/> and by <see cref="LightmapBakeJob.EvaluatePropLighting"/>.</summary>
	public IReadOnlyList<string> PropCorrections { get; }

	internal LightmapBakeResult( IReadOnlyList<LightmapFaceBake> faces, LightmapBakePass pass, int atlasSize,
		TimeSpan duration, bool cancelled, string error, bool reusedPrevious, IReadOnlyList<int> reusedFaceIds,
		string route, string gpuRefusedReason, IReadOnlyList<string> warnings, string fingerprint,
		IReadOnlyDictionary<int, string> faceFingerprints, string lightsFingerprint, string optionsFingerprint,
		LightmapBakeResult coarse, IReadOnlyList<string> propCorrections = null )
	{
		Faces = faces;
		Pass = pass;
		AtlasWidth = atlasSize;
		AtlasHeight = atlasSize;
		Duration = duration;
		Cancelled = cancelled;
		Error = error;
		ReusedPrevious = reusedPrevious;
		ReusedFaceIds = reusedFaceIds ?? Array.Empty<int>();
		Route = route ?? "";
		GpuRefusedReason = gpuRefusedReason;
		Warnings = warnings ?? Array.Empty<string>();
		Fingerprint = fingerprint ?? "";
		FaceFingerprints = faceFingerprints ?? new Dictionary<int, string>();
		LightsFingerprint = lightsFingerprint ?? "";
		OptionsFingerprint = optionsFingerprint ?? "";
		Coarse = coarse;
		PropCorrections = propCorrections ?? Array.Empty<string>();
		var count = 0;
		var max = Vec3.Zero;
		BakedAveragePerFace = new Dictionary<int, Vec3>();
		if ( faces is not null )
		{
			foreach ( var f in faces )
			{
				count += f.Luxels.Count;
				max = new Vec3( MathF.Max( max.x, f.Max.x ), MathF.Max( max.y, f.Max.y ), MathF.Max( max.z, f.Max.z ) );
				if ( !f.SkippedNoDraw )
					BakedAveragePerFace[f.FaceId] = f.Average;
			}
		}
		LuxelCount = count;
		MaxLuxel = max;
	}

	/// <summary>The baked face with this id, or null when the run had no such face.</summary>
	public LightmapFaceBake Face( int id )
	{
		if ( Faces is null ) return null;
		foreach ( var f in Faces )
			if ( f.FaceId == id ) return f;
		return null;
	}

	/// <summary>
	/// Reference JSON in worker 23's bake-reference.json shape: {"averages": {"0": [x, y, z], ...}}.
	/// The integrator records this file from the real bake; it replaces the preview-recorded snapshot.
	/// </summary>
	public string ToReferenceJson()
	{
		var averages = new JsonObject();
		if ( Faces is not null )
		{
			foreach ( var f in Faces.Where( f => !f.SkippedNoDraw ).OrderBy( f => f.FaceId ) )
				averages[f.FaceId.ToString( CultureInfo.InvariantCulture )] = new JsonArray( (double)f.Average.x, (double)f.Average.y, (double)f.Average.z );
		}
		var doc = new JsonObject
		{
			["comment"] = "hammer-lightmap-progressive-bake reference: per-face average luxels (HDR linear) from the real LightmapBake refined pass. Recorded from the bake itself, not from the preview engine.",
			["pass"] = Pass.ToString(),
			["atlasSize"] = AtlasWidth,
			["averages"] = averages,
		};
		return doc.ToJsonString( new System.Text.Json.JsonSerializerOptions { WriteIndented = true } );
	}
}

/// <summary>One prop sample for the lighting evaluation: the prop's id, the world-space
/// sample point, and the normal as imported (from the prop's mirrored winding when the
/// prop is negatively scaled — that normal points inward and is what must be corrected,
/// not trusted). Scale is the prop's per-axis scale (MapEntity.Scale in MapEntities);
/// it is a plain Vec3 here so this file stays independent of the entity layer.</summary>
public sealed class PropLightSample
{
	public int PropId { get; }
	public Vec3 Position { get; }
	public Vec3 Normal { get; }
	public Vec3 Scale { get; }

	public PropLightSample( int propId, Vec3 position, Vec3 normal, Vec3 scale )
	{
		PropId = propId;
		Position = position;
		Normal = normal;
		Scale = scale;
	}
}

/// <summary>The lighting of one prop sample: the corrected normal actually lit with,
/// the irradiance (direct + ambient, HDR linear, before albedo/bounce), whether the
/// prop was mirrored, and a named <see cref="Error"/> when the prop cannot be lit
/// (zero scale) — a broken prop is reported, never silently lit black.</summary>
public sealed class PropLightingResult
{
	public int PropId { get; }
	public Vec3 Position { get; }
	public Vec3 CorrectedNormal { get; }
	public Vec3 Radiance { get; }
	public bool WasMirrored { get; }
	public string Error { get; }
	public bool Succeeded => Error is null;

	internal PropLightingResult( int propId, Vec3 position, Vec3 correctedNormal, Vec3 radiance, bool wasMirrored, string error )
	{
		PropId = propId;
		Position = position;
		CorrectedNormal = correctedNormal;
		Radiance = radiance;
		WasMirrored = wasMirrored;
		Error = error;
	}
}

/// <summary>
/// Negative-scale (mirrored) prop lighting correction (hammer-prop-lighting-negative-scale).
///
/// A negatively-scaled prop import flips the winding of every polygon, so the normal
/// Newell's method computes from the mirrored winding points INWARD — the prop lights
/// as if its faces faced the wrong way (CS2-era Hammer pain, r/hammer 197j4cx). The
/// correction is exact and rotation-independent: the mirrored winding's normal is the
/// exact negation of the true outward normal, so negating it restores the outward
/// normal, and reversing the vertex order restores the outward winding. No
/// component-wise normal rescaling is done: that is only correct for axis-aligned
/// transforms and would light a rotated mirrored prop wrong.
///
/// Zero scale is not corrected: a prop with a zero component has no defined
/// orientation on that axis, so <see cref="CorrectNormal"/> /
/// <see cref="CorrectWinding"/> throw naming the scale, and
/// <see cref="LightmapBakeJob.EvaluatePropLighting"/> reports the prop as a named
/// error instead. The scale predicates here are one-liners, duplicated in
/// MapEntities (which must compile without this file); keep the two copies agreeing.
/// </summary>
public static class PropLighting
{
	/// <summary>Determinant of the scale part: sx * sy * sz (a rotation has determinant +1, so it never changes the sign).</summary>
	public static float Determinant( Vec3 scale ) => scale.x * scale.y * scale.z;

	/// <summary>True when the scale mirrors the prop (negative determinant: an odd number of negative components).</summary>
	public static bool IsMirrored( Vec3 scale ) => Determinant( scale ) < 0f;

	/// <summary>True when any scale component is exactly zero: the prop cannot be lit.</summary>
	public static bool HasZeroScale( Vec3 scale ) => scale.x == 0f || scale.y == 0f || scale.z == 0f;

	/// <summary>Names ("x", "y", "z") of the zero scale components, in axis order.</summary>
	public static IReadOnlyList<string> ZeroScaleAxes( Vec3 scale )
	{
		var axes = new List<string>();
		if ( scale.x == 0f ) axes.Add( "x" );
		if ( scale.y == 0f ) axes.Add( "y" );
		if ( scale.z == 0f ) axes.Add( "z" );
		return axes;
	}

	/// <summary>The outward normal for lighting: the imported normal unchanged for a
	/// normal scale, negated for a mirrored scale (the mirrored winding's normal
	/// points inward). Throws naming the scale for a zero scale.</summary>
	public static Vec3 CorrectNormal( Vec3 normal, Vec3 scale )
	{
		if ( HasZeroScale( scale ) )
			throw new ArgumentException( $"Cannot correct a normal for zero scale ({scale.x}, {scale.y}, {scale.z}): the prop has no defined orientation on that axis." );
		return IsMirrored( scale ) ? -normal : normal;
	}

	/// <summary>The outward winding for a polygon: the vertices unchanged for a normal
	/// scale, reversed for a mirrored scale. Returns the input list unchanged when
	/// no correction is needed. Throws naming the scale for a zero scale.</summary>
	public static IReadOnlyList<Vec3> CorrectWinding( IReadOnlyList<Vec3> vertices, Vec3 scale )
	{
		if ( vertices is null ) throw new ArgumentNullException( nameof( vertices ) );
		if ( vertices.Count < 3 ) throw new ArgumentException( $"A polygon needs at least 3 vertices, has {vertices.Count}." );
		if ( HasZeroScale( scale ) )
			throw new ArgumentException( $"Cannot correct winding for zero scale ({scale.x}, {scale.y}, {scale.z}): the prop has no defined orientation on that axis." );
		if ( !IsMirrored( scale ) ) return vertices;
		var reversed = vertices.ToArray();
		Array.Reverse( reversed );
		return reversed;
	}
}

/// <summary>
/// The progressive lightmap bake job: low-res first, refine in background, invalidate only
/// changed faces, CPU first with the GpuBake trust rule (1K test-bake must match CPU before
/// the GPU path is trusted). Construct with the faces + lights, then BakeCoarse / Refine /
/// Run / BakeIncremental. Run methods never throw for a bake failure or a cancel: those
/// come back as <see cref="LightmapBakeResult.Error"/> / <see cref="LightmapBakeResult.Cancelled"/>.
/// The constructor throws for invalid inputs, naming the problem.
/// </summary>
public sealed class LightmapBakeJob
{
	public IReadOnlyList<LiveFace> Faces { get; }
	public IReadOnlyList<LiveLight> Lights { get; }
	public LightmapBakeOptions Options { get; }
	/// <summary>Prop samples this job lights via <see cref="EvaluatePropLighting"/> (empty when none were given).</summary>
	public IReadOnlyList<PropLightSample> Props { get; }

	/// <summary>A GPU bake backend. Returns false with a reason when it declines a bake — a decline is an ordinary outcome, not an error. A backend bakes every face it is given and returns a full refined result; a partial result is a decline.</summary>
	public interface IGpuLightmapBackend
	{
		bool TryBake( IReadOnlyList<LiveFace> faces, IReadOnlyList<LiveLight> lights, LightmapBakeOptions options, out LightmapBakeResult result, out string why );
	}

	/// <summary>The installed GPU backend, or null when none is installed. A host that never installs one gets the CPU bake.</summary>
	public static IGpuLightmapBackend Provider { get; set; }

	public LightmapBakeJob( IReadOnlyList<LiveFace> faces, IReadOnlyList<LiveLight> lights, LightmapBakeOptions options = null, IReadOnlyList<PropLightSample> props = null )
	{
		if ( faces is null ) throw new ArgumentNullException( nameof( faces ) );
		if ( lights is null ) throw new ArgumentNullException( nameof( lights ) );
		if ( props is not null )
		{
			var seenProps = new HashSet<int>();
			foreach ( var prop in props )
			{
				if ( prop is null ) throw new ArgumentException( "LightmapBake: the prop list contains a null sample." );
				if ( !seenProps.Add( prop.PropId ) )
					throw new ArgumentException( $"LightmapBake: duplicate prop id {prop.PropId}." );
			}
		}
		if ( faces.Count == 0 ) throw new ArgumentException( "A lightmap bake needs at least one face.", nameof( faces ) );
		var seen = new HashSet<int>();
		foreach ( var face in faces )
		{
			if ( face is null ) throw new ArgumentException( "LightmapBake: the face list contains a null face." );
			if ( !seen.Add( face.Id ) )
				throw new ArgumentException( $"LightmapBake: duplicate face id {face.Id}." );
			if ( face.Normal.LengthSquared <= 1e-12f )
				throw new ArgumentException( $"LightmapBake: face {face.Id} is zero-area (its normal is zero-length)." );
			if ( face.LightmapScale <= 0f )
				throw new ArgumentException( $"LightmapBake: face {face.Id} has lightmap scale {face.LightmapScale}: it must be positive." );
		}
		foreach ( var light in lights )
			if ( light is null ) throw new ArgumentException( "LightmapBake: the light list contains a null light." );
		Options = options ?? new LightmapBakeOptions();
		var defects = Options.Validate();
		if ( defects.Count > 0 )
			throw new ArgumentException( "LightmapBake options are invalid: " + string.Join( "; ", defects ) );
		Faces = faces.ToArray();
		Lights = lights.ToArray();
		Props = props is null ? Array.Empty<PropLightSample>() : props.ToArray();
	}

	/// <summary>
	/// Light the job's props: each sample is evaluated with its normal corrected for
	/// mirroring (see <see cref="PropLighting"/>), using the same direct-light model
	/// as the face bake (sun/point/spot + shadows) plus the ambient term, so a
	/// mirrored prop lights identically to its unmirrored twin. A zero-scale prop
	/// is not lit: its result carries a named Error. Props do not affect the face
	/// bake or its fingerprints (they are not occluders here).
	/// </summary>
	public IReadOnlyList<PropLightingResult> EvaluatePropLighting()
	{
		var results = new List<PropLightingResult>();
		foreach ( var prop in Props )
		{
			if ( PropLighting.HasZeroScale( prop.Scale ) )
			{
				results.Add( new PropLightingResult( prop.PropId, prop.Position, Vec3.Zero, Vec3.Zero, false,
					$"prop {prop.PropId} has zero scale on axis {string.Join( ", ", PropLighting.ZeroScaleAxes( prop.Scale ) )}: it cannot be lit — set a non-zero scale." ) );
				continue;
			}
			var corrected = PropLighting.CorrectNormal( prop.Normal, prop.Scale );
			var radiance = Direct( prop.Position, corrected, -1 ) + Ambient();
			results.Add( new PropLightingResult( prop.PropId, prop.Position, corrected, radiance, PropLighting.IsMirrored( prop.Scale ), null ) );
		}
		return results;
	}

	/// <summary>Whole-job fingerprint: options + faces + lights.</summary>
	public string Fingerprint() => HashString( Mix( Mix( OptionsHash(), FacesHash() ), LightsHash() ) );

	/// <summary>One fingerprint per face id: geometry + material + lightmap scale + albedo.</summary>
	public Dictionary<int, string> FaceFingerprints()
	{
		var result = new Dictionary<int, string>();
		foreach ( var face in Faces )
			result[face.Id] = HashString( FaceHash( face ) );
		return result;
	}

	/// <summary>Stable fingerprint of the lights alone (for PlanRebake: a light change dirties every face).</summary>
	public string LightsFingerprint() => HashString( LightsHash() );

	/// <summary>Stable fingerprint of the options alone (for PlanRebake: an option change dirties every face).</summary>
	public string OptionsFingerprint() => HashString( OptionsHash() );

	/// <summary>
	/// Which faces need baking given a previous result: every face whose fingerprint changed,
	/// or that the previous result is missing. All faces when the previous result is null,
	/// failed, or was baked under different lights/options. An empty list means nothing
	/// changed and the previous result can be reused as-is.
	/// </summary>
	public IReadOnlyList<int> PlanRebake( LightmapBakeResult previous )
	{
		var all = Faces.Where( f => !f.IsNoDraw ).Select( f => f.Id ).ToList();
		if ( previous is null || !previous.Succeeded )
			return all;
		if ( previous.LightsFingerprint != LightsFingerprint() || previous.OptionsFingerprint != OptionsFingerprint() )
			return all;
		var current = FaceFingerprints();
		var dirty = new List<int>();
		foreach ( var face in Faces )
		{
			if ( face.IsNoDraw ) continue;
			if ( !previous.FaceFingerprints.TryGetValue( face.Id, out var old ) || old != current[face.Id] )
				dirty.Add( face.Id );
		}
		return dirty;
	}

	/// <summary>
	/// The low-res first pass: coarse grid (lightmap scale x CoarseFactor), direct + ambient
	/// only, no bounce. The early preview while the refine runs.
	/// </summary>
	public LightmapBakeResult BakeCoarse( IProgress<BakeProgress> progress = null, CancellationToken cancel = default ) =>
		Bake( LightmapBakePass.Coarse, null, progress, cancel );

	/// <summary>The refinement pass: full-resolution grid, direct + ambient + bounce.</summary>
	public LightmapBakeResult Refine( IProgress<BakeProgress> progress = null, CancellationToken cancel = default ) =>
		Bake( LightmapBakePass.Refined, null, progress, cancel );

	/// <summary>
	/// Progressive run: coarse preview first, then refinement. Returns the refined result
	/// with the coarse result attached (<see cref="LightmapBakeResult.Coarse"/>). A failed or
	/// cancelled coarse pass still returns a result (Error/Cancelled set) — the refine is
	/// not attempted after a cancelled coarse pass.
	/// </summary>
	public LightmapBakeResult Run( IProgress<BakeProgress> progress = null, CancellationToken cancel = default )
	{
		var coarse = BakeCoarse( progress, cancel );
		if ( !coarse.Succeeded )
			return coarse;
		return Bake( LightmapBakePass.Refined, coarse, progress, cancel );
	}

	/// <summary>
	/// Re-bake only the faces <see cref="PlanRebake"/> flags as dirty: dirty faces get fresh
	/// full-resolution direct luxels, clean faces keep their luxels, and the (cheap) bounce
	/// gather is recomputed globally from the new averages. Returns the previous result
	/// untouched (ReusedPrevious) when nothing is dirty. A null or failed previous bakes
	/// everything.
	/// </summary>
	public LightmapBakeResult BakeIncremental( LightmapBakeResult previous, IProgress<BakeProgress> progress = null, CancellationToken cancel = default )
	{
		var clock = Stopwatch.StartNew();
		if ( previous is null || !previous.Succeeded )
			return Refine( progress, cancel );

		var dirty = PlanRebake( previous );
		if ( dirty.Count == 0 )
		{
			Report( progress, BakePhase.Done, 100f, null, "Nothing changed since the last bake; reusing it." );
			return CloneResult( previous, clock.Elapsed, reusedPrevious: true, reusedFaceIds: Faces.Where( f => !f.IsNoDraw ).Select( f => f.Id ).ToList() );
		}

		try
		{
			Report( progress, BakePhase.Preparing, 0f, null, "Planning the incremental bake." );
			cancel.ThrowIfCancellationRequested();

			var dirtySet = new HashSet<int>( dirty );
			var grids = PlanGrids( LightmapBakePass.Refined );
			var rects = PackAtlas( grids );
			var ambient = Ambient();
			var directByFace = new Dictionary<int, (Vec3[] Direct, int Inside, bool[] InsideMask)>();
			var reused = new List<int>();

			for ( var i = 0; i < Faces.Count; i++ )
			{
				cancel.ThrowIfCancellationRequested();
				var face = Faces[i];
				if ( face.IsNoDraw ) continue;
				if ( !dirtySet.Contains( face.Id ) )
				{
					reused.Add( face.Id );
					continue;
				}
				Report( progress, BakePhase.Baking, 5f + 85f * i / Math.Max( 1, Faces.Count ), $"face {face.Id}", $"Re-baking face {face.Id} ({i + 1}/{Faces.Count})." );
				directByFace[face.Id] = ShadeDirect( face, grids[face.Id].Rows, grids[face.Id].Cols, grids[face.Id].LuxelSize );
			}

			// Bounce is recomputed globally: dirty faces contribute fresh direct averages,
			// clean faces contribute their previous direct averages.
			var directAvg = new Dictionary<int, Vec3>();
			foreach ( var face in Faces )
			{
				if ( face.IsNoDraw ) continue;
				if ( directByFace.TryGetValue( face.Id, out var fresh ) )
					directAvg[face.Id] = fresh.Direct.Length == 0 ? ambient : AverageOf( fresh.Direct, fresh.Inside ) + ambient;
				else
					directAvg[face.Id] = previous.Face( face.Id )?.DirectAverage ?? ambient;
			}
			var bounce = ComputeBounce( directAvg );

			var baked = new List<LightmapFaceBake>();
			foreach ( var face in Faces )
			{
				if ( face.IsNoDraw )
				{
					baked.Add( SkippedFace( face ) );
					continue;
				}
				if ( !dirtySet.Contains( face.Id ) )
				{
					var old = previous.Face( face.Id );
					if ( old is not null && !old.SkippedNoDraw )
					{
						// Clean face: keep its direct luxels, apply the fresh bounce.
						baked.Add( ApplyBounce( old, bounce.TryGetValue( face.Id, out var b ) ? b : Vec3.Zero, face.Albedo ) );
						continue;
					}
					// No reusable bake for this face (it was skipped before, or is missing):
					// fall through and bake it fresh below.
					reused.Remove( face.Id );
					directByFace[face.Id] = ShadeDirect( face, grids[face.Id].Rows, grids[face.Id].Cols, grids[face.Id].LuxelSize );
				}
				var (direct, inside, mask) = directByFace[face.Id];
				var grid = grids[face.Id];
				baked.Add( FinishFace( face, grid.Rows, grid.Cols, grid.LuxelSize, direct, inside, mask, ambient,
					bounce.TryGetValue( face.Id, out var bb ) ? bb : Vec3.Zero, rects[face.Id], directOnly: false ) );
			}

			Report( progress, BakePhase.Done, 100f, null, $"Incremental bake done: {dirty.Count} face(s) re-baked, {reused.Count} reused." );
			return MakeResult( baked, LightmapBakePass.Refined, clock.Elapsed, route: "cpu (incremental)",
				reusedFaceIds: reused, warnings: Warnings(), coarse: null );
		}
		catch ( OperationCanceledException )
		{
			return MakeResult( null, LightmapBakePass.Refined, clock.Elapsed, cancelled: true, route: "cpu (incremental)", warnings: Warnings(), coarse: null );
		}
		catch ( Exception ex )
		{
			return MakeResult( null, LightmapBakePass.Refined, clock.Elapsed,
				error: $"Incremental bake failed ({ex.GetType().Name}): {ex.Message}", route: "cpu (incremental)", warnings: Warnings(), coarse: null );
		}
	}

	/// <summary>
	/// The GPU path under the 1K trust rule. Runs the 1K test-bake (CPU reference vs GPU at
	/// <see cref="LightmapBakeOptions.GpuTestSize"/>) and compares per-face average luxels;
	/// the GPU path is accepted only when every face is inside tolerance, and only then is
	/// the final bake run on the GPU. When the GPU path is refused, the returned attempt has
	/// Accepted false, a named <see cref="GpuBakeAttempt.RefusedReason"/>, and Result set to
	/// the CPU bake — refused, not silently shipped, and the route says so.
	/// </summary>
	public GpuBakeAttempt TryGpuBake( IProgress<BakeProgress> progress = null, CancellationToken cancel = default )
	{
		var clock = Stopwatch.StartNew();
		if ( Provider is null )
		{
			var cpu = Refine( progress, cancel );
			return new GpuBakeAttempt( false, cpu, "no gpu backend installed", "cpu (no gpu backend installed)",
				new Dictionary<string, (double, double)>(), clock.Elapsed );
		}

		var testOptions = CloneOptions( Options );
		testOptions.AtlasSize = Options.GpuTestSize;
		var testJob = new LightmapBakeJob( Faces, Lights, testOptions );
		var cpuTest = testJob.Refine( progress, cancel );
		if ( !cpuTest.Succeeded )
			return new GpuBakeAttempt( false, cpuTest, $"cpu test-bake failed: {cpuTest.Error ?? "no result"}",
				"cpu (cpu test-bake failed)", new Dictionary<string, (double, double)>(), clock.Elapsed );

		if ( !Provider.TryBake( Faces, Lights, testOptions, out var gpuTest, out var why ) || gpuTest is null || !gpuTest.Succeeded )
		{
			var cpu = Refine( progress, cancel );
			return new GpuBakeAttempt( false, cpu, $"gpu declined the test-bake: {why ?? "no reason given"}",
				$"cpu (gpu declined the test-bake: {why ?? "no reason given"})",
				new Dictionary<string, (double, double)>(), clock.Elapsed );
		}

		var deviations = CompareAverages( gpuTest, cpuTest );
		var worst = deviations.Count == 0
			? (Face: -1, Mean: double.MaxValue, Max: double.MaxValue)
			: deviations.Select( d => (Face: int.Parse( d.Key, CultureInfo.InvariantCulture ), d.Value.Mean, d.Value.Max ) )
				.OrderByDescending( d => d.Max ).First();
		if ( deviations.Count == 0 || worst.Mean > Options.GpuMeanTolerance || worst.Max > Options.GpuMaxTolerance )
		{
			var cpu = Refine( progress, cancel );
			var reason = deviations.Count == 0
				? "gpu test-bake returned no comparable faces"
				: $"gpu test-bake diverged on face {worst.Face}: mean {worst.Mean:F3} max {worst.Max:F3} (tolerance mean {Options.GpuMeanTolerance} max {Options.GpuMaxTolerance})";
			return new GpuBakeAttempt( false, cpu, reason, $"cpu ({reason})", deviations, clock.Elapsed );
		}

		if ( !Provider.TryBake( Faces, Lights, Options, out var gpu, out why ) || gpu is null || !gpu.Succeeded )
		{
			var cpu = Refine( progress, cancel );
			return new GpuBakeAttempt( false, cpu, $"gpu declined the final bake: {why ?? "no reason given"}",
				$"cpu (gpu declined the final bake: {why ?? "no reason given"})", deviations, clock.Elapsed );
		}
		var missing = Faces.Where( f => !f.IsNoDraw ).Select( f => f.Id ).Except( gpu.Faces.Select( f => f.FaceId ) ).ToList();
		if ( missing.Count > 0 )
		{
			var cpu = Refine( progress, cancel );
			return new GpuBakeAttempt( false, cpu, $"gpu bake missing faces: {string.Join( ", ", missing )}",
				$"cpu (gpu bake missing faces: {string.Join( ", ", missing )})", deviations, clock.Elapsed );
		}
		gpu.Route = "gpu";
		return new GpuBakeAttempt( true, gpu, null, "gpu", deviations, clock.Elapsed );
	}

	/// <summary>What a <see cref="TryGpuBake"/> attempt produced.</summary>
	public sealed class GpuBakeAttempt
	{
		/// <summary>True only when the final result actually came from the GPU.</summary>
		public bool Accepted { get; }
		/// <summary>The bake result (GPU when accepted, CPU otherwise).</summary>
		public LightmapBakeResult Result { get; }
		/// <summary>Why the GPU path was refused; null when accepted.</summary>
		public string RefusedReason { get; }
		public string Route { get; }
		/// <summary>Per-face mean/max absolute average deviation from the 1K test-bake, by face id.</summary>
		public Dictionary<string, (double Mean, double Max)> Deviations { get; }
		public TimeSpan Duration { get; }

		public GpuBakeAttempt( bool accepted, LightmapBakeResult result, string refusedReason, string route,
			Dictionary<string, (double, double)> deviations, TimeSpan duration )
		{
			Accepted = accepted;
			Result = result;
			RefusedReason = refusedReason;
			Route = route;
			Deviations = deviations ?? new Dictionary<string, (double, double)>();
			Duration = duration;
		}
	}

	// -- Core bake ---------------------------------------------------------------

	LightmapBakeResult Bake( LightmapBakePass pass, LightmapBakeResult coarse, IProgress<BakeProgress> progress, CancellationToken cancel )
	{
		var clock = Stopwatch.StartNew();
		try
		{
			Report( progress, BakePhase.Preparing, 0f, null, pass == LightmapBakePass.Coarse ? "Preparing the coarse bake." : "Preparing the refined bake." );
			cancel.ThrowIfCancellationRequested();

			var grids = PlanGrids( pass );
			var rects = PackAtlas( grids );
			var ambient = Ambient();
			var directByFace = new Dictionary<int, (Vec3[] Direct, int Inside, bool[] InsideMask)>();
			var baked = new List<LightmapFaceBake>();

			for ( var i = 0; i < Faces.Count; i++ )
			{
				cancel.ThrowIfCancellationRequested();
				var face = Faces[i];
				Report( progress, BakePhase.Baking, 5f + 85f * i / Math.Max( 1, Faces.Count ), $"face {face.Id}",
					$"Baking face {face.Id} ({i + 1}/{Faces.Count})." );
				if ( face.IsNoDraw )
				{
					baked.Add( SkippedFace( face ) );
					continue;
				}
				var grid = grids[face.Id];
				var shaded = ShadeDirect( face, grid.Rows, grid.Cols, grid.LuxelSize );
				directByFace[face.Id] = shaded;
				// Coarse faces finish here (direct + ambient, no bounce); refined faces get
				// their bounce below, once every face's direct average is known.
				baked.Add( FinishFace( face, grid.Rows, grid.Cols, grid.LuxelSize, shaded.Direct, shaded.Inside, shaded.InsideMask, ambient,
					Vec3.Zero, rects[face.Id], directOnly: pass == LightmapBakePass.Coarse ) );
			}

			if ( pass == LightmapBakePass.Refined && Options.BouncePasses > 0 )
			{
				Report( progress, BakePhase.Baking, 92f, null, "Gathering bounce light." );
				cancel.ThrowIfCancellationRequested();
				var directAvg = new Dictionary<int, Vec3>();
				foreach ( var face in Faces )
				{
					if ( face.IsNoDraw ) continue;
					var shaded = directByFace[face.Id];
					directAvg[face.Id] = shaded.Direct.Length == 0 ? ambient : AverageOf( shaded.Direct, shaded.Inside ) + ambient;
				}
				var bounce = ComputeBounce( directAvg );
				for ( var i = 0; i < baked.Count; i++ )
				{
					var face = Faces[i];
					if ( face.IsNoDraw ) continue;
					var b = bounce.TryGetValue( face.Id, out var bb ) ? bb : Vec3.Zero;
					if ( b.LengthSquared > 0f )
						baked[i] = ApplyBounce( baked[i], b, face.Albedo );
				}
			}

			Report( progress, BakePhase.Done, 100f, null, pass == LightmapBakePass.Coarse ? "Coarse bake done." : "Refined bake done." );
			return MakeResult( baked, pass, clock.Elapsed,
				route: pass == LightmapBakePass.Coarse ? "cpu (coarse preview)" : "cpu",
				warnings: Warnings(), coarse: coarse );
		}
		catch ( OperationCanceledException )
		{
			return MakeResult( null, pass, clock.Elapsed, cancelled: true,
				route: pass == LightmapBakePass.Coarse ? "cpu (coarse preview)" : "cpu", warnings: Warnings(), coarse: coarse );
		}
		catch ( Exception ex )
		{
			return MakeResult( null, pass, clock.Elapsed,
				error: $"Lightmap bake failed ({ex.GetType().Name}): {ex.Message}",
				route: pass == LightmapBakePass.Coarse ? "cpu (coarse preview)" : "cpu", warnings: Warnings(), coarse: coarse );
		}
	}

	/// <summary>Per-face grid for a pass: cols/rows from the face extents over the effective lightmap scale.</summary>
	Dictionary<int, (int Rows, int Cols, float LuxelSize)> PlanGrids( LightmapBakePass pass )
	{
		var grids = new Dictionary<int, (int, int, float)>();
		foreach ( var face in Faces )
		{
			if ( face.IsNoDraw ) continue;
			var scale = face.LightmapScale * ( pass == LightmapBakePass.Coarse ? Options.CoarseFactor : 1 );
			var (uMin, uMax, vMin, vMax) = FaceUv.ProjectedBounds( face.Vertices, face.Normal );
			var cols = Math.Clamp( (int)MathF.Ceiling( ( uMax - uMin ) / scale ), 1, Options.MaxGridPerAxis );
			var rows = Math.Clamp( (int)MathF.Ceiling( ( vMax - vMin ) / scale ), 1, Options.MaxGridPerAxis );
			grids[face.Id] = (rows, cols, scale);
		}
		return grids;
	}

	/// <summary>
	/// Shelf-pack the face grids into the atlas with a 1-luxel gutter between rects and
	/// shelves. Faces pack largest-first (rows, then cols, then id — deterministic). Throws
	/// naming the face when a grid is wider than the atlas, or naming the atlas when the
	/// shelves run out of room: a bake that does not fit is an error, never a silent
	/// downscale. (UVUnwrap packs at one uniform scale and cannot express per-face
	/// lightmap scale, so this pack is scale-aware by construction; same gutter principle.)
	/// </summary>
	Dictionary<int, (float U0, float V0, float U1, float V1)> PackAtlas( Dictionary<int, (int Rows, int Cols, float LuxelSize)> grids )
	{
		const int gutter = 1;
		var rects = new Dictionary<int, (float, float, float, float)>();
		var ordered = Faces.Where( f => !f.IsNoDraw )
			.Select( f => (Face: f, Grid: grids[f.Id]) )
			.OrderByDescending( g => g.Grid.Rows ).ThenByDescending( g => g.Grid.Cols ).ThenBy( g => g.Face.Id )
			.ToList();
		foreach ( var (face, grid) in ordered )
		{
			if ( grid.Cols > Options.AtlasSize || grid.Rows > Options.AtlasSize )
				throw new ArgumentException( $"Atlas {Options.AtlasSize}x{Options.AtlasSize} does not fit face {face.Id}: its grid is {grid.Cols}x{grid.Rows} luxels." );
		}
		var shelfY = 0;
		var shelfH = 0;
		var x = 0;
		foreach ( var (face, grid) in ordered )
		{
			var w = grid.Cols + gutter;
			var h = grid.Rows + gutter;
			if ( x > 0 && x + w > Options.AtlasSize )
			{
				shelfY += shelfH;
				x = 0;
				shelfH = 0;
			}
			if ( shelfY + h > Options.AtlasSize )
				throw new ArgumentException( $"Atlas {Options.AtlasSize}x{Options.AtlasSize} does not fit {ordered.Count} faces: ran out of room packing face {face.Id} ({grid.Cols}x{grid.Rows} luxels)." );
			rects[face.Id] = (x / (float)Options.AtlasSize, shelfY / (float)Options.AtlasSize,
				(x + grid.Cols) / (float)Options.AtlasSize, (shelfY + grid.Rows) / (float)Options.AtlasSize);
			x += w;
			shelfH = Math.Max( shelfH, h );
		}
		return rects;
	}

	/// <summary>Direct-only luxels for one face (irradiance, no ambient, no albedo, no bounce). Returns the luxels, the count of inside-polygon cells, and the inside mask (outside cells stay Zero in Direct).</summary>
	(Vec3[] Direct, int Inside, bool[] InsideMask) ShadeDirect( LiveFace face, int rows, int cols, float luxelSize )
	{
		var (uAxis, vAxis) = FaceUv.PlaneAxes( face.Normal );
		var (uMin, uMax, vMin, vMax) = FaceUv.ProjectedBounds( face.Vertices, face.Normal );
		var uExtent = uMax - uMin;
		var vExtent = vMax - vMin;
		var origin = face.Vertices[0];
		var uv = face.Vertices.Select( p => (U: Vec3.Dot( p - origin, uAxis ), V: Vec3.Dot( p - origin, vAxis )) ).ToList();
		var direct = new Vec3[rows * cols];
		var mask = new bool[rows * cols];
		var inside = 0;
		for ( var row = 0; row < rows; row++ )
		{
			for ( var col = 0; col < cols; col++ )
			{
				var u = uMin + ( col + 0.5f ) * uExtent / cols;
				var v = vMin + ( row + 0.5f ) * vExtent / rows;
				if ( !PointInPolygon2D( u, v, uv ) ) continue;
				mask[row * cols + col] = true;
				inside++;
				var p = origin + uAxis * u + vAxis * v;
				direct[row * cols + col] = Direct( p, face.Normal, face.Id );
			}
		}
		return (direct, inside, mask);
	}

	/// <summary>Uniform ambient irradiance: the sum of every light_environment colour * intensity.</summary>
	Vec3 Ambient()
	{
		var ambient = Vec3.Zero;
		foreach ( var light in Lights )
			if ( light.Kind == LiveLightKind.Environment )
				ambient += light.Color * light.Intensity;
		return ambient;
	}

	/// <summary>
	/// Direct irradiance at a point from every light. This MUST match LiveLighting's
	/// private Direct(): same falloff 1/(1+(d/64)^2), same spot smoothstep, same sun
	/// NdotL, same d &lt;= 1e-6 and range behaviour — the preview-vs-bake cross-check
	/// test enforces it. The one addition is the shadow test: a ray from the sample
	/// point toward the light that hits any other face kills that light's contribution.
	/// </summary>
	Vec3 Direct( Vec3 point, Vec3 normal, int faceId )
	{
		var total = Vec3.Zero;
		foreach ( var light in Lights )
		{
			switch ( light.Kind )
			{
				case LiveLightKind.Environment:
					break;
				case LiveLightKind.Sun:
				{
					var nDotL = MathF.Max( 0f, Vec3.Dot( normal, -light.Direction ) );
					if ( nDotL <= 0f ) break;
					if ( Shadowed( point, normal, -light.Direction, float.MaxValue, faceId ) ) break;
					total += light.Color * ( light.Intensity * nDotL );
					break;
				}
				case LiveLightKind.Point:
				case LiveLightKind.Spot:
				{
					var toLight = light.Position - point;
					var d = toLight.Length;
					if ( d <= 1e-6f ) { total += light.Color * light.Intensity; break; }
					if ( light.Range > 0f && d > light.Range ) break;
					var dir = toLight / d;
					var nDotL = MathF.Max( 0f, Vec3.Dot( normal, dir ) );
					if ( nDotL <= 0f ) break;
					if ( Shadowed( point, normal, dir, d, faceId ) ) break;
					var falloff = 1f / ( 1f + ( d * d ) / ( LiveLighting.FalloffHalfDistance * LiveLighting.FalloffHalfDistance ) );
					var amount = light.Intensity * nDotL * falloff;
					if ( light.Kind == LiveLightKind.Spot )
					{
						var cosAngle = Vec3.Dot( -dir, light.Direction );
						var cosInner = MathF.Cos( light.InnerConeDegrees * MathF.PI / 180f );
						var cosOuter = MathF.Cos( light.OuterConeDegrees * MathF.PI / 180f );
						if ( cosAngle <= cosOuter ) break;
						if ( cosAngle < cosInner )
							amount *= Smoothstep( ( cosAngle - cosOuter ) / ( cosInner - cosOuter ) );
					}
					total += light.Color * amount;
					break;
				}
				default:
					throw new ArgumentException( $"Unknown light kind: {light.Kind}." );
			}
		}
		return total;
	}

	static float Smoothstep( float t )
	{
		if ( t <= 0f ) return 0f;
		if ( t >= 1f ) return 1f;
		return t * t * ( 3f - 2f * t );
	}

	/// <summary>True when the segment origin -&gt; origin + dir * maxDist enters any face other than <paramref name="faceId"/> (nodraw faces block: they are solid).</summary>
	bool Shadowed( Vec3 point, Vec3 normal, Vec3 dir, float maxDist, int faceId )
	{
		var origin = point + normal * Options.ShadowEpsilon;
		foreach ( var face in Faces )
		{
			if ( face.Id == faceId ) continue;
			for ( var i = 1; i + 1 < face.Vertices.Count; i++ )
			{
				if ( RayTriangle( origin, dir, face.Vertices[0], face.Vertices[i], face.Vertices[i + 1], maxDist ) )
					return true;
			}
		}
		return false;
	}

	/// <summary>Möller–Trumbore ray/triangle: true when the ray hits the triangle at 0 &lt; t &lt; maxDist.</summary>
	internal static bool RayTriangle( Vec3 origin, Vec3 dir, Vec3 a, Vec3 b, Vec3 c, float maxDist )
	{
		var e1 = b - a;
		var e2 = c - a;
		var p = Vec3.Cross( dir, e2 );
		var det = Vec3.Dot( e1, p );
		if ( MathF.Abs( det ) < 1e-9f ) return false;
		var invDet = 1f / det;
		var t = origin - a;
		var u = Vec3.Dot( t, p ) * invDet;
		if ( u < 0f || u > 1f ) return false;
		var q = Vec3.Cross( t, e1 );
		var v = Vec3.Dot( dir, q ) * invDet;
		if ( v < 0f || u + v > 1f ) return false;
		var hit = Vec3.Dot( e2, q ) * invDet;
		return hit > 1e-6f && hit < maxDist;
	}

	static bool PointInPolygon2D( float x, float y, List<(float U, float V)> polygon )
	{
		var inside = false;
		for ( int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++ )
		{
			var (xi, yi) = polygon[i];
			var (xj, yj) = polygon[j];
			if ( ( yi > y ) != ( yj > y ) && x < ( xj - xi ) * ( y - yi ) / ( yj - yi ) + xi )
				inside = !inside;
		}
		return inside;
	}

	/// <summary>
	/// Bounce gather: for each face, the irradiance bounced in from every other face's
	/// outgoing light (albedo x direct average), weighted by the form factor between the
	/// centroids and cut to zero when the centroid ray is blocked. One iteration per
	/// <see cref="LightmapBakeOptions.BouncePasses"/>; the outgoing of later passes
	/// includes the bounce gathered so far. Faces with no outgoing contribute nothing.
	/// </summary>
	Dictionary<int, Vec3> ComputeBounce( Dictionary<int, Vec3> directAverage )
	{
		var bounce = Faces.Where( f => !f.IsNoDraw ).ToDictionary( f => f.Id, _ => Vec3.Zero );
		var outgoing = new Dictionary<int, Vec3>();
		foreach ( var face in Faces )
		{
			if ( face.IsNoDraw ) continue;
			outgoing[face.Id] = Mul( face.Albedo, directAverage.TryGetValue( face.Id, out var d ) ? d : Vec3.Zero );
		}
		for ( var pass = 0; pass < Options.BouncePasses; pass++ )
		{
			var gathered = new Dictionary<int, Vec3>();
			foreach ( var face in Faces )
			{
				if ( face.IsNoDraw ) continue;
				var sum = Vec3.Zero;
				foreach ( var other in Faces )
				{
					if ( other.IsNoDraw || other.Id == face.Id ) continue;
					var toOther = other.Center - face.Center;
					var d = toOther.Length;
					if ( d <= 1e-6f || d > Options.BounceMaxDistance ) continue;
					var dir = toOther / d;
					var cosI = Vec3.Dot( face.Normal, dir );
					var cosJ = Vec3.Dot( other.Normal, -dir );
					if ( cosI <= 0f || cosJ <= 0f ) continue;
					if ( CentroidBlocked( face, other ) ) continue;
					var area = PolygonArea( other.Vertices );
					var form = area * cosI * cosJ / ( MathF.PI * d * d + area );
					sum += outgoing[other.Id] * ( Options.BounceFactor * form );
				}
				gathered[face.Id] = sum;
			}
			foreach ( var pair in gathered )
			{
				bounce[pair.Key] = pair.Value;
				outgoing[pair.Key] = Mul( Faces.First( f => f.Id == pair.Key ).Albedo,
					( directAverage.TryGetValue( pair.Key, out var d ) ? d : Vec3.Zero ) + pair.Value );
			}
		}
		return bounce;
	}

	/// <summary>True when the centroid-to-centroid segment enters any face other than the two endpoints.</summary>
	bool CentroidBlocked( LiveFace from, LiveFace to )
	{
		var origin = from.Center + from.Normal * Options.ShadowEpsilon;
		var toTarget = to.Center - origin;
		var dist = toTarget.Length;
		if ( dist <= 1e-6f ) return false;
		var dir = toTarget / dist;
		foreach ( var face in Faces )
		{
			if ( face.Id == from.Id || face.Id == to.Id ) continue;
			for ( var i = 1; i + 1 < face.Vertices.Count; i++ )
			{
				if ( RayTriangle( origin, dir, face.Vertices[0], face.Vertices[i], face.Vertices[i + 1], dist ) )
					return true;
			}
		}
		return false;
	}

	static float PolygonArea( IReadOnlyList<Vec3> vertices )
	{
		var area = 0f;
		for ( var i = 1; i + 1 < vertices.Count; i++ )
			area += 0.5f * Vec3.Cross( vertices[i] - vertices[0], vertices[i + 1] - vertices[0] ).Length;
		return area;
	}

	static Vec3 Mul( Vec3 a, Vec3 b ) => new( a.x * b.x, a.y * b.y, a.z * b.z );

	static Vec3 AverageOf( IReadOnlyList<Vec3> values, int inside )
	{
		if ( inside <= 0 ) return Vec3.Zero;
		var sum = Vec3.Zero;
		foreach ( var v in values ) sum += v;
		return sum / inside;
	}

	/// <summary>Finish a face: final luxels = (direct + ambient + bounce) * albedo, over inside-polygon cells only; outside cells stay Zero.</summary>
	LightmapFaceBake FinishFace( LiveFace face, int rows, int cols, float luxelSize, Vec3[] direct, int inside, bool[] insideMask,
		Vec3 ambient, Vec3 bounce, (float, float, float, float) atlasRect, bool directOnly )
	{
		var luxels = new Vec3[rows * cols];
		var directSum = Vec3.Zero;
		for ( var i = 0; i < direct.Length; i++ )
		{
			directSum += direct[i];
			if ( insideMask[i] )
				luxels[i] = Mul( direct[i] + ambient + bounce, face.Albedo );
		}
		return new LightmapFaceBake( face.Id, rows, cols, luxelSize, luxels,
			inside <= 0 ? Vec3.Zero : directSum / inside + ambient, bounce, atlasRect, skippedNoDraw: false, directOnly, inside, insideMask );
	}

	static LightmapFaceBake SkippedFace( LiveFace face ) =>
		new( face.Id, 0, 0, face.LightmapScale, Array.Empty<Vec3>(), Vec3.Zero, Vec3.Zero,
			(0f, 0f, 0f, 0f), skippedNoDraw: true, directOnly: false, insideLuxels: 0, insideMask: Array.Empty<bool>() );

	/// <summary>Re-apply a fresh bounce to a reused face: luxels = old luxels + (new bounce - old bounce) * albedo, inside-polygon cells only; outside cells stay Zero.</summary>
	static LightmapFaceBake ApplyBounce( LightmapFaceBake old, Vec3 bounce, Vec3 albedo )
	{
		var luxels = new Vec3[old.Luxels.Count];
		var delta = Mul( bounce - old.BounceAverage, albedo );
		var mask = new bool[old.Luxels.Count];
		for ( var i = 0; i < luxels.Length; i++ )
		{
			mask[i] = i < old.InsideMask.Count && old.InsideMask[i];
			luxels[i] = mask[i] ? old.Luxels[i] + delta : Vec3.Zero;
		}
		return new LightmapFaceBake( old.FaceId, old.Rows, old.Cols, old.LuxelSize, luxels,
			old.DirectAverage, bounce, old.AtlasRect, skippedNoDraw: false, directOnly: false, old.InsideCount, mask );
	}

	static List<string> WarningsStatic( IReadOnlyList<LiveLight> lights )
	{
		var warnings = new List<string>();
		if ( lights.Count == 0 )
			warnings.Add( "No lights: every face bakes black." );
		var hasSun = lights.Any( l => l.Kind == LiveLightKind.Sun );
		var hasEnvironment = lights.Any( l => l.Kind == LiveLightKind.Environment );
		if ( hasSun && !hasEnvironment )
			warnings.Add( "Sun light present with no light_environment: the sun has no ambient term (light_environment + env_sun pair is incomplete)." );
		return warnings;
	}

	List<string> Warnings()
	{
		var warnings = WarningsStatic( Lights );
		foreach ( var prop in Props )
			if ( PropLighting.HasZeroScale( prop.Scale ) )
				warnings.Add( $"prop {prop.PropId} has zero scale on axis {string.Join( ", ", PropLighting.ZeroScaleAxes( prop.Scale ) )}: it cannot be lit — set a non-zero scale." );
		return warnings;
	}

	/// <summary>One bake-log line per mirrored prop in this job: the correction is
	/// flagged in the log, never silent. Zero-scale props are not corrections —
	/// they are reported by <see cref="Warnings"/> and by
	/// <see cref="EvaluatePropLighting"/> instead.</summary>
	IReadOnlyList<string> PropCorrectionLog() =>
		Props.Where( p => PropLighting.IsMirrored( p.Scale ) )
			.Select( p => $"prop {p.PropId} mirrored (scale {Q( p.Scale )}): normal/winding corrected for lighting" )
			.ToArray();

	static void Report( IProgress<BakeProgress> progress, BakePhase phase, float percent, string group, string message ) =>
		progress?.Report( new BakeProgress { Phase = phase, Percent = percent, Group = group, Message = message } );

	LightmapBakeResult MakeResult( IReadOnlyList<LightmapFaceBake> faces, LightmapBakePass pass, TimeSpan duration,
		bool cancelled = false, string error = null, bool reusedPrevious = false, IReadOnlyList<int> reusedFaceIds = null,
		string route = "cpu", string gpuRefusedReason = null, IReadOnlyList<string> warnings = null, LightmapBakeResult coarse = null ) =>
		new( faces, pass, Options.AtlasSize, duration, cancelled, error, reusedPrevious, reusedFaceIds,
			route, gpuRefusedReason, warnings ?? Array.Empty<string>(), Fingerprint(), FaceFingerprints(),
			LightsFingerprint(), OptionsFingerprint(), coarse, PropCorrectionLog() );

	LightmapBakeResult CloneResult( LightmapBakeResult previous, TimeSpan duration, bool reusedPrevious, IReadOnlyList<int> reusedFaceIds ) =>
		new( previous.Faces, previous.Pass, previous.AtlasWidth, duration, false, null, reusedPrevious, reusedFaceIds,
			"cpu (reused previous)", null, previous.Warnings, previous.Fingerprint, previous.FaceFingerprints,
			previous.LightsFingerprint, previous.OptionsFingerprint, previous.Coarse, previous.PropCorrections );

	/// <summary>Mean / max absolute deviation of per-face average luxels, by face id, over faces present in both results.</summary>
	public static Dictionary<string, (double Mean, double Max)> CompareAverages( LightmapBakeResult a, LightmapBakeResult b )
	{
		var result = new Dictionary<string, (double Mean, double Max)>();
		if ( a?.Faces is null || b?.Faces is null ) return result;
		foreach ( var fa in a.Faces )
		{
			var fb = b.Face( fa.FaceId );
			if ( fb is null || fa.SkippedNoDraw || fb.SkippedNoDraw ) continue;
			var sum = Math.Abs( fa.Average.x - fb.Average.x ) + Math.Abs( fa.Average.y - fb.Average.y ) + Math.Abs( fa.Average.z - fb.Average.z );
			var max = Math.Max( Math.Abs( fa.Average.x - fb.Average.x ), Math.Max( Math.Abs( fa.Average.y - fb.Average.y ), Math.Abs( fa.Average.z - fb.Average.z ) ) );
			result[fa.FaceId.ToString( CultureInfo.InvariantCulture )] = (sum / 3.0, max);
		}
		return result;
	}

	static LightmapBakeOptions CloneOptions( LightmapBakeOptions options ) => new()
	{
		AtlasSize = options.AtlasSize,
		CoarseFactor = options.CoarseFactor,
		BouncePasses = options.BouncePasses,
		MaxGridPerAxis = options.MaxGridPerAxis,
		BounceFactor = options.BounceFactor,
		BounceMaxDistance = options.BounceMaxDistance,
		ShadowEpsilon = options.ShadowEpsilon,
		GpuTestSize = options.GpuTestSize,
		GpuMeanTolerance = options.GpuMeanTolerance,
		GpuMaxTolerance = options.GpuMaxTolerance,
	};

	// -- Fingerprints (FNV-1a hex, quantized to 0.001, id order — mirrors LiveLighting.ComputeInvalidationKey) --

	static ulong FaceHash( LiveFace face )
	{
		var sb = new StringBuilder();
		sb.Append( "face " ).Append( face.Id ).Append( ' ' ).Append( face.Material.MaterialName )
			.Append( " lm=" ).Append( Q( face.LightmapScale ) )
			.Append( " albedo=" ).Append( Q( face.Albedo ) );
		foreach ( var v in face.Vertices )
			sb.Append( ' ' ).Append( Q( v ) );
		return Fnv( sb.ToString() );
	}

	ulong FacesHash()
	{
		var h = 1469598103934665603ul;
		foreach ( var face in Faces.OrderBy( f => f.Id ) )
			h = Mix( h, FaceHash( face ) );
		return h;
	}

	ulong LightsHash()
	{
		var sb = new StringBuilder();
		foreach ( var light in Lights.OrderBy( l => l.Id ) )
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
		return Fnv( sb.ToString() );
	}

	ulong OptionsHash()
	{
		var h = 1469598103934665603ul;
		h = MixInt( h, Options.AtlasSize );
		h = MixInt( h, Options.CoarseFactor );
		h = MixInt( h, Options.BouncePasses );
		h = MixInt( h, Options.MaxGridPerAxis );
		h = MixFloat( h, Options.BounceFactor );
		h = MixFloat( h, Options.BounceMaxDistance );
		h = MixFloat( h, Options.ShadowEpsilon );
		return h;
	}

	static ulong Fnv( string text )
	{
		ulong hash = 14695981039346656037UL;
		foreach ( var b in Encoding.UTF8.GetBytes( text ) )
		{
			hash ^= b;
			hash *= 1099511628211UL;
		}
		return hash;
	}

	static string Q( float v ) => MathF.Round( v, 3 ).ToString( "R", CultureInfo.InvariantCulture );
	static string Q( Vec3 v ) => Q( v.x ) + "," + Q( v.y ) + "," + Q( v.z );

	static ulong Mix( ulong h, ulong v ) => ( h ^ v ) * 1099511628211ul;
	static ulong MixInt( ulong h, int v ) => Mix( h, (ulong)(uint)v );
	static ulong MixFloat( ulong h, float v ) => Mix( h, (ulong)(uint)BitConverter.SingleToInt32Bits( v ) );
	static string HashString( ulong hash ) => hash.ToString( "X16", CultureInfo.InvariantCulture );
}
