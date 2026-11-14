using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Effigy.Map;

namespace Effigy;

// ============================================================================
// Hammer Desktop P1: NATIVE Geppetto compile pipeline (BSP build + visibility
// + lightmap bake). Board task hammer-compile-native-pipeline.
//
// A staged, cancellable compile that runs entirely inside Geppetto: no Hammer
// app, no VBSP / VVIS / VRAD, no Hammer++ compiler, no external process of
// any kind. Presets (Quick Play = BSP-only; Preview = BSP + fast visibility
// + low-res lightmap + cubemaps when probes exist; Final = full visibility
// + full lightmap bake + cubemaps + release pack), per-stage progress +
// elapsed time + log capture, stage failure mapped to named issues.
// Progress/cancel/result shape reuses the Baker pattern (BakeJob/BakePhase /
// IProgress<BakeProgress> + CancellationToken, a result that never throws for
// a run failure or a cancel). Backend / data-model / API only — no UI.
//
// DEPENDENCY STATUS (2026-11-13, verified against the live tree): only
// LeakFinder has landed (kernel/Effigy/Map/LeakFinder.cs, with the
// ILeakPortalGraph surface). BspBuilder / PortalGraph
// (hammer-bsp-core-tree-portals-leaves), MapDocument / MapBrush
// (hammer-map-document-model, hammer-brush-core-plane-based-brushes), VmfIo
// (hammer-vmf-map-import-export) and IssueEngine
// (hammer-validation-issue-engine) have NOT landed — their descs name no
// concrete member signatures, so this file cannot call them. What it does
// instead, per the task ("implement against the spec exactly as filed"):
//   * BSP build: a real native plane-split polygon BSP is implemented here
//     (BuildBsp), with leaves, portals and PVS — the shape the BSP ticket
//     specifies (build tree from brush planes, portals between leaves, PVS).
//     When BspBuilder lands, the integration is to swap BuildBsp's internals
//     for BspBuilder + a PortalGraph->ILeakPortalGraph adapter; this file's
//     public API does not change. Do not invent a second compile runner
//     against BspBuilder types; adapt this one.
//   * Leak check: reuses the LANDED LeakFinder API exactly — the BSP leaves
//     and portals are adapted to LeakFinder.LeakGraph and
//     LeakFinder.FindLeak does the flood-fill. The leak point file is written
//     with LeakFinder.WritePointFile (Valve .lin/.pts).
//   * Input: CompileInput / CompileBrush / CompileFace are the minimal
//     plane-based brush model the filed specs describe (faces as planes:
//     outward normal + distance, per-face material; box from 6 planes;
//     plane-intersection vertex solve; convex, non-zero volume). When
//     MapDocument / MapBrush land, the integration is one adapter,
//     CompileInput.FromDocument(MapDocument), added here — the runner and
//     its tests do not change shape.
//   * Issues: CompileIssue carries the fields the IssueEngine ticket
//     specifies (code, severity, location, named fix hint) and its codes are
//     the IssueEngine rule names (leak, invalid-brush, missing-player-start,
//     limit-exceeded, empty-map). When IssueEngine lands, CompileIssue maps
//     1:1 onto its Issue type.
//   * Lightmap bake: native per-face direct-light bake (Lambert + distance
//     falloff + a real shadow ray against the brush solids). It does not use
//     Baker/NormalBake: those bake texture maps from meshes, while a compile
//     bakes light into the map's own faces.
//
// OUTPUT FILES (all plain text, deterministic, invariant culture; written to
// CompileOptions.OutputFolder on a successful run only — a failed or
// cancelled run claims no partial product, Baker's rule; the leak point file
// and the log are the diagnostics of a failed run, not products):
//   <name>.bsp       GEPPETTO-BSP 1 — leaves (id, solid/empty/void, bounds)
//                    and portals (id, leaf A, leaf B, centre).
//   <name>.pvs       GEPPETTO-PVS 1 — one visible-leaf set per leaf.
//   <name>.lightmap  GEPPETTO-LIGHTMAP 1 — per-face texel grids + means.
//   <name>.entities  GEPPETTO-ENTITIES 1 — the entity lump (written by a full
//                    run when the input has entities; an Only-Entities run
//                    writes only this file and reuses the others in place).
//   <name>.cubemap   GEPPETTO-CUBEMAP 1 — 6 faces per env_cubemap probe.
//   <name>.mappack   GEPPETTO-MAPPACK 1 — used-only asset bundle + cubemap inventory.
//   <name>.lin       Valve point file, only when the map leaks.
//   <name>.compile.log  timestamped stage log (also CompileResult.Log).
//
// CORDON SEAM (hammer-compile-cordon-region, sequenced AFTER this ticket):
// that worker implements Cordon + ApplyCordon IN this file. The seam is
// PrepareInput: it currently returns the input unchanged, and ApplyCordon
// filters the input there before validation. This ticket invents no Cordon
// type and adds no cordon behaviour.
//
// Geometry is the kernel's inches and Z-up convention. Deterministic: same
// input + preset -> same leaves, portals, PVS and lightmap values (no time,
// randomness or hash-order dependence in any output file).
//
// UPDATE 2026-11-13 (hammer-compile-packing-cubemap-build): MapDocument HAS
// landed since the DEPENDENCY STATUS above was written, so the deferred
// CompileInput.FromDocument(MapDocument) adapter is added by this ticket
// (brushes + player start + lights + probes + model/sound/particle refs
// from compile-visible brushes/entities). Stages() below is still the
// preset's BASE list — CubemapBuild and Pack are input/options-gated and
// appear in CompileResult.CompletedStages, not in Stages(): CubemapBuild
// runs on Preview/Final when input.Probes is non-empty (QuickPlay skips
// it, per the cubemap seam); Pack runs on Final only, and only when
// CompileOptions.AssetRegistries is supplied (no registries = no pack
// stage, so existing preset tests keep passing unchanged).
//
// UPDATE 2026-11-13 (hammer-compile-presets-only-entities, P2, rebased onto
// the packing + parallel versions of this file — this ticket did NOT write
// the cubemap / pack / parallel code above): this file also carries the
// fast-iteration extensions — Only-Entities (rebuild the entity lump only,
// reuse BSP + PVS + lightmaps from the last full build), the FastVis
// built-in preset + CompileOptions.FastVis flag (relaxed 1-hop visibility
// for testing; a downgrade only — it never adds visibility to QuickPlay),
// named presets (CompileNamedPreset + CompilePresetStore, JSON sidecar
// file), and structured go-to log entries (CompileLogEntry +
// CompileSourceRef; every issue also carries its brush / entity /
// coordinate reference on CompileIssue.SourceRef). Only-Entities output is
// <name>.entities (GEPPETTO-ENTITIES 1); the prior .bsp / .pvs / .lightmap
// files are reused in place, never rewritten. MapDocument HAS landed (see
// the packing update above), so CompileInput.FromDocument also fills
// CompileInput.Entities — the entity lump of a document round-trips through
// Only-Entities. No external process is spawned anywhere in this file (no
// spawned processes, no shell); the runner is native-only, same as pass 1.
//
// UPDATE 2026-11-13 (hammer-compile-safety-net-gate): IssueEngine HAS
// landed since the DEPENDENCY STATUS above was written, and the gate in
// Run() runs it (via PreCompileGate / IssueEngineIssueSource) as the full
// Check-for-Problems pass before any build stage, in every preset.
// Errors block the compile; warnings are logged and it proceeds; a
// caller override (CompileOptions.SkipPreCompileGate) skips the gate and
// is logged. The gate is a pre-stage in the Preparing phase, not a build
// stage: Stages() / CompletedStages still list only the build stages, and
// a gate block reports FailedStage BspBuild (no build stage ran). The leak
// verdict stays with the BSP stage (the gate has no BSP portal graph yet,
// so its leak rules are skipped and LeakFinder + the .lin point file still
// own leaks); see PreCompileGate.cs. The gate does NOT run in the separate
// RunOnlyEntities static path (that path rebuilds the entity lump only and
// has its own entity validation; brush errors are irrelevant to it).
// ============================================================================

/// <summary>Which compile preset to run. See <see cref="CompilePresetDef"/>.</summary>
public enum CompilePreset
{
	/// <summary>BSP build only. No visibility, no lightmaps.</summary>
	QuickPlay,
	/// <summary>BSP + fast (1-hop) visibility + low-res lightmap.</summary>
	Preview,
	/// <summary>BSP + full visibility (transitive PVS) + full-res lightmap.</summary>
	Final,
	/// <summary>BSP + fast (1-hop) visibility, no lightmap — the standard test mode (Fast-VIS).</summary>
	FastVis,
}

/// <summary>Visibility quality a preset asks for.</summary>
public enum VisibilityQuality
{
	None,
	/// <summary>Direct portal neighbours only (1 hop).</summary>
	Fast,
	/// <summary>Full flood-fill closure through open portals.</summary>
	Full,
}

/// <summary>One stage of a compile, in run order.</summary>
public enum CompileStage
{
	BspBuild,
	Visibility,
	LightmapBake,
	/// <summary>Entity-lump rebuild only (Only-Entities run). No BSP, visibility or lightmap stage runs.</summary>
	EntityLump,
	CubemapBuild,
	Pack,
}

/// <summary>
/// Which phase a compile is in, for progress reporting. Mirrors the
/// <see cref="BakePhase"/> shape (Preparing / working / Writing / Done /
/// Cancelled / Failed) with one working phase per compile stage.
/// </summary>
public enum CompilePhase
{
	Preparing,
	BspBuild,
	Visibility,
	LightmapBake,
	EntityLump,
	CubemapBuild,
	Pack,
	Writing,
	Done,
	Cancelled,
	Failed,
}

/// <summary>Issue severity. Mirrors the IssueEngine ticket's severity field (Warning / Error); Info is log-only, never an issue severity.</summary>
public enum CompileSeverity
{
	Info,
	Warning,
	Error,
}

/// <summary>
/// The definition of one preset: which stages run, visibility quality and
/// lightmap resolution. Quick Play = BSP-only; Preview = BSP + fast
/// visibility + low-res lightmap; Final = full visibility + full lightmap;
/// FastVis = BSP + fast visibility, no lightmap (the standard test mode).
/// </summary>
public sealed class CompilePresetDef
{
	public CompilePreset Preset { get; }
	public string Name { get; }
	public string Description { get; }
	public bool RunVisibility => Visibility != VisibilityQuality.None;
	public VisibilityQuality Visibility { get; }
	/// <summary>Texels per face axis for the lightmap bake; 0 = no bake.</summary>
	public int LightmapResolution { get; }
	/// <summary>Run the cubemap build stage when the input has probes (Preview/Final; QuickPlay skips it).</summary>
	public bool RunCubemaps { get; }
	/// <summary>Run the release packing stage (Final only, and only when CompileOptions.AssetRegistries is supplied).</summary>
	public bool RunPacking { get; }

	CompilePresetDef( CompilePreset preset, string name, string description, VisibilityQuality visibility, int lightmapResolution, bool runCubemaps, bool runPacking )
	{
		Preset = preset;
		Name = name;
		Description = description;
		Visibility = visibility;
		LightmapResolution = lightmapResolution;
		RunCubemaps = runCubemaps;
		RunPacking = runPacking;
	}

	public static readonly CompilePresetDef QuickPlay = new(
		CompilePreset.QuickPlay, "Quick Play",
		"BSP build only — fastest iteration, no visibility, no lightmaps.",
		VisibilityQuality.None, 0, false, false );

	public static readonly CompilePresetDef Preview = new(
		CompilePreset.Preview, "Preview",
		"BSP + fast (1-hop) visibility + low-res lightmap + cubemaps when probes exist.",
		VisibilityQuality.Fast, 4, true, false );

	public static readonly CompilePresetDef Final = new(
		CompilePreset.Final, "Final",
		"BSP + full visibility + full-res lightmap + cubemaps + release pack.",
		VisibilityQuality.Full, 16, true, true );

	public static readonly CompilePresetDef FastVis = new(
		CompilePreset.FastVis, "Fast-VIS",
		"BSP + fast (1-hop) visibility, no lightmap — the standard test mode.",
		VisibilityQuality.Fast, 0, false, false );

	/// <summary>Definition for a preset. Throws for an undefined preset value.</summary>
	public static CompilePresetDef For( CompilePreset preset ) => preset switch
	{
		CompilePreset.QuickPlay => QuickPlay,
		CompilePreset.Preview => Preview,
		CompilePreset.Final => Final,
		CompilePreset.FastVis => FastVis,
		_ => throw new ArgumentException( $"Unknown compile preset {preset}.", nameof( preset ) ),
	};

	/// <summary>Base stages this preset runs, in order. CubemapBuild and Pack are input/options-gated and are appended by the runner (see CompileRunner.Run), not listed here.</summary>
	public IReadOnlyList<CompileStage> Stages()
	{
		var stages = new List<CompileStage> { CompileStage.BspBuild };
		if ( RunVisibility ) stages.Add( CompileStage.Visibility );
		if ( LightmapResolution > 0 ) stages.Add( CompileStage.LightmapBake );
		return stages;
	}
}

/// <summary>
/// A saved named preset: a base built-in preset plus overrides, the
/// CompilePal-style saved preset. Applying it runs the base preset with the
/// overrides merged in (see <see cref="CompileRunner(CompileInput, CompileNamedPreset, CompileOptions)"/>).
/// Serialized as one JSON object in a <see cref="CompilePresetStore"/> sidecar
/// file; the map document holds no preset state (MapCompileSettings carries
/// no preset slot), so the store is a sidecar by design, not a document field.
/// </summary>
public sealed class CompileNamedPreset
{
	public string Name { get; }
	public string Description { get; set; } = "";
	public CompilePreset BasePreset { get; }
	/// <summary>Visibility override, or null to use the base preset's quality.</summary>
	public VisibilityQuality? VisibilityOverride { get; set; }
	/// <summary>Lightmap resolution override, 0 = use the base preset's.</summary>
	public int LightmapResolutionOverride { get; set; }
	/// <summary>Fast-VIS: force relaxed (1-hop) visibility. A downgrade only (see <see cref="CompileOptions.FastVis"/>).</summary>
	public bool FastVis { get; set; }

	public CompileNamedPreset( string name, CompilePreset basePreset )
	{
		Name = ValidateName( name );
		if ( !Enum.IsDefined( basePreset ) ) throw new ArgumentException( $"Unknown compile preset {basePreset}.", nameof( basePreset ) );
		BasePreset = basePreset;
	}

	/// <summary>Preset names are file-safe identifiers: non-empty, no path separators, no control characters, max 64 chars.</summary>
	public static string ValidateName( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) throw new ArgumentException( "Preset name must not be empty.", nameof( name ) );
		var trimmed = name.Trim();
		if ( trimmed.Length > 64 ) throw new ArgumentException( $"Preset name '{trimmed}' is over 64 characters.", nameof( name ) );
		foreach ( var c in trimmed )
			if ( char.IsControl( c ) || c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|' )
				throw new ArgumentException( $"Preset name '{trimmed}' contains an illegal character '{c}'.", nameof( name ) );
		return trimmed;
	}

	public void Validate()
	{
		ValidateName( Name );
		if ( !Enum.IsDefined( BasePreset ) ) throw new ArgumentException( $"Unknown compile preset {BasePreset}.", nameof( BasePreset ) );
		if ( LightmapResolutionOverride < 0 || LightmapResolutionOverride > 64 )
			throw new ArgumentException( $"LightmapResolutionOverride {LightmapResolutionOverride} must be 0 (base preset) or 1..64.", nameof( LightmapResolutionOverride ) );
	}

	/// <summary>Base preset + this preset's overrides, as the options a run merges with any explicit per-run options.</summary>
	public (CompilePreset Base, CompileOptions Overrides) Resolve()
	{
		Validate();
		return (BasePreset, new CompileOptions
		{
			VisibilityOverride = VisibilityOverride,
			LightmapResolutionOverride = LightmapResolutionOverride,
			FastVis = FastVis,
		});
	}

	internal JsonObject ToJson()
	{
		var obj = new JsonObject
		{
			["name"] = Name,
			["description"] = Description,
			["base"] = BasePreset.ToString(),
			["lightmapResolution"] = LightmapResolutionOverride,
			["fastVis"] = FastVis,
		};
		if ( VisibilityOverride is not null ) obj["visibility"] = VisibilityOverride.Value.ToString();
		return obj;
	}

	internal static CompileNamedPreset FromJson( JsonElement element )
	{
		if ( element.ValueKind != JsonValueKind.Object ) throw new JsonException( "Named preset must be a JSON object." );
		var name = element.GetProperty( "name" ).GetString() ?? "";
		var baseText = element.GetProperty( "base" ).GetString() ?? "";
		if ( !Enum.TryParse<CompilePreset>( baseText, out var basePreset ) || !Enum.IsDefined( basePreset ) )
			throw new JsonException( $"Named preset '{name}' has an unknown base preset '{baseText}'." );
		var preset = new CompileNamedPreset( name, basePreset );
		if ( element.TryGetProperty( "description", out var description ) && description.ValueKind == JsonValueKind.String )
			preset.Description = description.GetString() ?? "";
		if ( element.TryGetProperty( "visibility", out var visibility ) && visibility.ValueKind == JsonValueKind.String
			&& Enum.TryParse<VisibilityQuality>( visibility.GetString(), out var quality ) )
			preset.VisibilityOverride = quality;
		if ( element.TryGetProperty( "lightmapResolution", out var lightmap ) && lightmap.ValueKind == JsonValueKind.Number )
			preset.LightmapResolutionOverride = lightmap.GetInt32();
		if ( element.TryGetProperty( "fastVis", out var fastVis ) && ( fastVis.ValueKind == JsonValueKind.True || fastVis.ValueKind == JsonValueKind.False ) )
			preset.FastVis = fastVis.GetBoolean();
		preset.Validate();
		return preset;
	}
}

/// <summary>
/// The named-preset store: create / list / apply by name, persisted as one
/// JSON sidecar file (<c>compile-presets.json</c> by convention, next to the
/// map or in the output folder). The store is in-memory first: <see cref="Save"/>
/// upserts and persists immediately when <see cref="FilePath"/> is set, and
/// <see cref="LoadFromFile"/> reads a sidecar back. A store with no file path
/// is memory-only (tests, or a caller that persists via <see cref="ToJson"/>).
/// Names are case-insensitive for lookup; the saved casing is preserved.
/// </summary>
public sealed class CompilePresetStore
{
	readonly Dictionary<string, CompileNamedPreset> _presets = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Sidecar file this store persists to on <see cref="Save"/> / <see cref="Remove"/>, or null for a memory-only store.</summary>
	public string FilePath { get; set; }
	public int Count => _presets.Count;

	public CompilePresetStore( string filePath = null ) { FilePath = filePath; }

	/// <summary>Save (create or overwrite) a named preset, persisting the sidecar when a file path is set.</summary>
	public void Save( CompileNamedPreset preset )
	{
		if ( preset is null ) throw new ArgumentNullException( nameof( preset ) );
		preset.Validate();
		_presets[preset.Name] = preset;
		Persist();
	}

	/// <summary>Named preset by name. Throws <see cref="KeyNotFoundException"/> when unknown.</summary>
	public CompileNamedPreset Get( string name )
	{
		if ( !_presets.TryGetValue( CompileNamedPreset.ValidateName( name ), out var preset ) )
			throw new KeyNotFoundException( $"No compile preset named '{name}'." );
		return preset;
	}

	public bool TryGet( string name, out CompileNamedPreset preset )
	{
		preset = null;
		if ( string.IsNullOrWhiteSpace( name ) ) return false;
		return _presets.TryGetValue( name.Trim(), out preset );
	}

	/// <summary>Saved preset names, sorted (ordinal, case-insensitive).</summary>
	public IReadOnlyList<string> List() =>
		_presets.Keys.OrderBy( n => n, StringComparer.OrdinalIgnoreCase ).ToList();

	/// <summary>Remove a named preset. False when it was not saved. Persists the sidecar when a file path is set.</summary>
	public bool Remove( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return false;
		var removed = _presets.Remove( name.Trim() );
		if ( removed ) Persist();
		return removed;
	}

	/// <summary>Whole store as JSON: {"version":1,"presets":[...]}. Deterministic (presets in name order).</summary>
	public string ToJson()
	{
		var root = new JsonObject { ["version"] = 1 };
		var array = new JsonArray();
		foreach ( var preset in _presets.Values.OrderBy( p => p.Name, StringComparer.OrdinalIgnoreCase ) )
			array.Add( preset.ToJson() );
		root["presets"] = array;
		return root.ToJsonString( new JsonSerializerOptions { WriteIndented = true } );
	}

	/// <summary>Store from the JSON <see cref="ToJson"/> writes. Unknown fields are ignored; a bad preset entry fails the load.</summary>
	public static CompilePresetStore FromJson( string json, string filePath = null )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) throw new JsonException( "Preset sidecar JSON is empty." );
		using var doc = JsonDocument.Parse( json );
		var root = doc.RootElement;
		if ( root.ValueKind != JsonValueKind.Object ) throw new JsonException( "Preset sidecar must be a JSON object." );
		if ( root.TryGetProperty( "version", out var version ) && version.ValueKind == JsonValueKind.Number && version.GetInt32() != 1 )
			throw new JsonException( $"Unsupported preset sidecar version {version.GetInt32()} (expected 1)." );
		var store = new CompilePresetStore( filePath );
		if ( !root.TryGetProperty( "presets", out var presets ) || presets.ValueKind != JsonValueKind.Array ) return store;
		foreach ( var element in presets.EnumerateArray() )
		{
			var preset = CompileNamedPreset.FromJson( element );
			store._presets[preset.Name] = preset;
		}
		return store;
	}

	/// <summary>Write the sidecar file (creates the folder). No-op when <see cref="FilePath"/> is unset.</summary>
	public void SaveToFile( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) throw new ArgumentException( "Preset sidecar path must not be empty.", nameof( path ) );
		FilePath = path;
		Persist();
	}

	/// <summary>Load a sidecar file. A missing file loads as an empty store (no presets yet is not an error).</summary>
	public static CompilePresetStore LoadFromFile( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) throw new ArgumentException( "Preset sidecar path must not be empty.", nameof( path ) );
		if ( !File.Exists( path ) ) return new CompilePresetStore( path );
		return FromJson( File.ReadAllText( path ), path );
	}

	void Persist()
	{
		if ( string.IsNullOrWhiteSpace( FilePath ) ) return;
		Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( FilePath ) ) ?? "." );
		File.WriteAllText( FilePath, ToJson() + "\n" );
	}
}

/// <summary>Run options. Limits are the compiler limits the IssueEngine ticket names.</summary>
public sealed class CompileOptions
{
	/// <summary>Folder for <c>.bsp</c> / <c>.pvs</c> / <c>.lightmap</c> / <c>.cubemap</c> / <c>.mappack</c> / <c>.lin</c> outputs (and the log when LogPath is unset). Null = no output files.</summary>
	public string OutputFolder { get; set; }
	/// <summary>Explicit log file path. When null and OutputFolder is set, the log goes to <c>&lt;name&gt;.compile.log</c> in the output folder. Null + no output folder = log kept in memory only.</summary>
	public string LogPath { get; set; }
	/// <summary>Maximum brushes in one compile. Over it fails the BSP stage with a <c>limit-exceeded</c> issue.</summary>
	public int MaxBrushes { get; set; } = 16384;
	/// <summary>Maximum planes per brush. Over it fails the BSP stage with an <c>invalid-brush</c> issue.</summary>
	public int MaxPlanesPerBrush { get; set; } = 128;
	/// <summary>Override the preset's lightmap resolution (0 = use the preset). Must be 1..64.</summary>
	public int LightmapResolutionOverride { get; set; }
	/// <summary>Override the preset's visibility quality (null = use the preset).</summary>
	public VisibilityQuality? VisibilityOverride { get; set; }
	/// <summary>
	/// Fast-VIS: force relaxed (1-hop) visibility for this run, whatever the
	/// preset asks for. A downgrade only: it never adds a visibility stage to
	/// QuickPlay (which runs no visibility), and it never upgrades Fast to
	/// Full. Equivalent to VisibilityOverride = Fast, kept as its own flag so
	/// a caller (or a named preset) can ask for "the standard test mode"
	/// without naming a quality.
	/// </summary>
	public bool FastVis { get; set; }
	/// <summary>Registries the release packing stage resolves used assets against. Null = the Pack stage is skipped (existing runs without registries keep passing).</summary>
	public AssetRegistries AssetRegistries { get; set; }
	/// <summary>Worker pool size for parallel stage work (hammer-compile-parallel-stages). -1 = one worker per processor (the machine default, mirroring BspBuildOptions.MaxDegreeOfParallelism); 1 = single-threaded. Clamped to the processor count.</summary>
	public int MaxDegreeOfParallelism { get; set; } = -1;
	/// <summary>Debug fallback: run every stage single-threaded, exactly as before parallelisation. Overrides MaxDegreeOfParallelism.</summary>
	public bool SingleThreaded { get; set; }
	/// <summary>
	/// Skip the pre-compile safety-net gate (hammer-compile-safety-net-gate).
	/// The override is caller-supplied and is recorded in the compile log;
	/// it skips ONLY the gate — the BSP stage still validates brushes,
	/// limits and leaks. Default false: the gate runs before every preset.
	/// </summary>
	public bool SkipPreCompileGate { get; set; }


	public void Validate()
	{
		if ( MaxBrushes < 1 ) throw new ArgumentException( $"MaxBrushes {MaxBrushes} must be at least 1.", nameof( MaxBrushes ) );
		if ( MaxPlanesPerBrush < 4 ) throw new ArgumentException( $"MaxPlanesPerBrush {MaxPlanesPerBrush} must be at least 4.", nameof( MaxPlanesPerBrush ) );
		if ( LightmapResolutionOverride < 0 || LightmapResolutionOverride > 64 ) throw new ArgumentException( $"LightmapResolutionOverride {LightmapResolutionOverride} must be 0 (preset) or 1..64.", nameof( LightmapResolutionOverride ) );
		if ( MaxDegreeOfParallelism < -1 || MaxDegreeOfParallelism == 0 ) throw new ArgumentException( $"MaxDegreeOfParallelism {MaxDegreeOfParallelism} must be -1 (machine default) or at least 1.", nameof( MaxDegreeOfParallelism ) );

	}
}

/// <summary>
/// One face of a compile brush: a plane (outward unit normal + distance,
/// plane equation <c>Normal . p = Distance</c>) + per-face material + lightmap
/// scale. This is the plane-based face the brush-core ticket specifies; when
/// MapBrush lands, its faces adapt to this shape.
/// </summary>
public sealed class CompileFace
{
	public Vec3 Normal { get; }
	public float Distance { get; }
	public string Material { get; set; } = "default";
	public float LightmapScale { get; set; } = 1f;

	public CompileFace( Vec3 normal, float distance, string material = "default" )
	{
		Normal = normal;
		Distance = distance;
		Material = material ?? "default";
	}
}

/// <summary>
/// One convex brush for the compile: an intersection of half-spaces, exactly
/// the brush-core ticket's model (faces as planes + per-face material, validity
/// = convex, planar faces, non-zero volume, no duplicate faces; invalid = a
/// named defect, never a silent fix). <see cref="IsDetail"/> marks func_detail
/// brushes: they are real visible geometry (they get lightmaps and block
/// light) but are excluded from the BSP solid, per the BSP ticket.
/// </summary>
public sealed class CompileBrush
{
	public string Name { get; set; } = "brush";
	public int Id { get; set; }
	public bool IsDetail { get; set; }
	public List<CompileFace> Faces { get; } = new();

	public CompileBrush() { }

	public CompileBrush( string name, int id, IEnumerable<CompileFace> faces, bool isDetail = false )
	{
		Name = name ?? "brush";
		Id = id;
		IsDetail = isDetail;
		if ( faces is not null ) Faces.AddRange( faces );
	}

	/// <summary>Axis-aligned box brush from 6 planes. A zero-size box is invalid (zero volume) — it is built, not silently fixed, so validation can name it.</summary>
	public static CompileBrush Box( string name, int id, Vec3 min, Vec3 max, string material = "default", bool isDetail = false )
	{
		return new CompileBrush( name, id, new[]
		{
			new CompileFace( new Vec3( 1, 0, 0 ), max.x, material ),
			new CompileFace( new Vec3( -1, 0, 0 ), -min.x, material ),
			new CompileFace( new Vec3( 0, 1, 0 ), max.y, material ),
			new CompileFace( new Vec3( 0, -1, 0 ), -min.y, material ),
			new CompileFace( new Vec3( 0, 0, 1 ), max.z, material ),
			new CompileFace( new Vec3( 0, 0, -1 ), -min.z, material ),
		}, isDetail );
	}

	/// <summary>Named validity defects, empty when valid. Checks: at least 4 planes, no duplicate planes, every face builds a polygon with area, non-zero volume.</summary>
	public List<string> Validate()
	{
		var defects = new List<string>();
		if ( Faces.Count < 4 ) { defects.Add( "too-few-planes" ); return defects; }
		for ( var i = 0; i < Faces.Count; i++ )
		{
			if ( Math.Abs( Faces[i].Normal.Length - 1f ) > 1e-3f ) defects.Add( $"non-unit-normal:face-{i}" );
			for ( var j = i + 1; j < Faces.Count; j++ )
				if ( Faces[i].Normal.AlmostEquals( Faces[j].Normal, 1e-4f ) && Math.Abs( Faces[i].Distance - Faces[j].Distance ) <= 1e-2f )
					defects.Add( $"duplicate-plane:face-{i}-face-{j}" );
		}
		var polygons = CompileRunner.BuildBrushPolygons( this, out var polyDefects );
		defects.AddRange( polyDefects );
		if ( polygons.Count != Faces.Count ) defects.Add( "degenerate-face" );
		var volume = CompileRunner.BrushVolume( polygons );
		if ( !( volume > 1e-6f ) ) defects.Add( "zero-volume" );
		return defects;
	}

	/// <summary>Closed-polyhedron volume from the face polygons (divergence theorem). Zero or negative for a degenerate brush.</summary>
	public float Volume() => CompileRunner.BrushVolume( CompileRunner.BuildBrushPolygons( this, out _ ) );
}

/// <summary>One point light for the lightmap bake.</summary>
public sealed class CompileLight
{
	public Vec3 Position { get; set; }
	/// <summary>Linear RGB 0..1.</summary>
	public Vec3 Color { get; set; } = new( 1, 1, 1 );
	public float Intensity { get; set; } = 1f;
	/// <summary>Cutoff distance; 0 = no cutoff.</summary>
	public float Range { get; set; }

	public CompileLight( Vec3 position, float intensity = 1f, Vec3? color = null, float range = 0f )
	{
		Position = position;
		Intensity = intensity;
		Color = color ?? new Vec3( 1, 1, 1 );
		Range = range;
	}
}

/// <summary>
/// One map entity for the compile: the entity-lump entry (classname +
/// origin + targetname + keyvalues). This is the compile's own minimal
/// entity model, like CompileBrush / CompileLight: the placed-entity API
/// (MapEntities / MapEntity) needs an EntityRegistry and is the editor's
/// model, not the compile's input. <see cref="CompileInput.FromDocument"/>
/// fills CompileInput.Entities from a document's entities (id sequential in
/// document order); the runner and the lump format do not change shape.
/// Keyvalues are registry-style strings (angles, _light, model, ...);
/// "origin" is not a keyvalue here either — it lives on <see cref="Origin"/>.
/// </summary>
public sealed class CompileEntity
{
	/// <summary>Stable entity id, unique within the map (1-based, like MapEntity.Id). 0 = unset.</summary>
	public int Id { get; set; }
	/// <summary>Entity class, e.g. "light_spot", "prop_static", "logic_relay". Empty = invalid.</summary>
	public string ClassName { get; set; } = "";
	/// <summary>Placement origin (inches, Z-up).</summary>
	public Vec3 Origin { get; set; }
	/// <summary>Targetname (keyvalue "targetname"), "" when unnamed.</summary>
	public string TargetName { get; set; } = "";
	/// <summary>Keyvalues other than classname / targetname / origin (case-insensitive keys). Never contains "origin".</summary>
	public Dictionary<string, string> Keyvalues { get; } = new( StringComparer.OrdinalIgnoreCase );

	public CompileEntity() { }

	public CompileEntity( int id, string className, Vec3 origin, string targetName = "" )
	{
		Id = id;
		ClassName = className ?? "";
		Origin = origin;
		TargetName = targetName ?? "";
	}

	/// <summary>Named validity defects, empty when valid: positive unique id is checked by the caller (it sees all entities), this checks the rest.</summary>
	public List<string> Validate()
	{
		var defects = new List<string>();
		if ( Id <= 0 ) defects.Add( "bad-id" );
		if ( string.IsNullOrWhiteSpace( ClassName ) ) defects.Add( "missing-classname" );
		if ( !float.IsFinite( Origin.x ) || !float.IsFinite( Origin.y ) || !float.IsFinite( Origin.z ) ) defects.Add( "non-finite-origin" );
		foreach ( var pair in Keyvalues )
		{
			if ( string.IsNullOrWhiteSpace( pair.Key ) ) defects.Add( "empty-key" );
			if ( pair.Value is null ) defects.Add( $"null-value:{pair.Key}" );
			else if ( string.Equals( pair.Key, "origin", StringComparison.OrdinalIgnoreCase ) ) defects.Add( "origin-is-not-a-keyvalue" );
		}
		return defects;
	}

	public override string ToString() => $"#{Id} {ClassName} @ {Origin}";
}

/// <summary>
/// What a compile runs on: brushes + lights + the player start the leak
/// check flood-fills from. Built directly, or from a map document via
/// <see cref="CompileInput.FromDocument"/> — the runner does not change shape.
/// </summary>
public sealed class CompileInput
{
	public string Name { get; set; } = "map";
	public List<CompileBrush> Brushes { get; } = new();
	public List<CompileLight> Lights { get; } = new();
	/// <summary>Map entities (the entity lump). Full runs validate them; Only-Entities rebuilds the lump from exactly this list.</summary>
	public List<CompileEntity> Entities { get; } = new();
	/// <summary>Player start (info_player_start). Null = no leak check origin: the BSP stage warns with a <c>missing-player-start</c> issue and skips the leak check.</summary>
	public Vec3? PlayerStart { get; set; }

	public CompileInput( string name = "map" ) { Name = string.IsNullOrWhiteSpace( name ) ? "map" : name; }

	/// <summary>env_cubemap probes to bake (empty = no cubemap stage). Built by <see cref="FromDocument"/> from env_cubemap entities, or added directly.</summary>
	public List<CubemapProbe> Probes { get; } = new();
	/// <summary>Model paths the map references (prop model keyvalues). Resolved against the model registry by the packing stage; deduped by <see cref="FromDocument"/>.</summary>
	public List<string> Models { get; } = new();
	/// <summary>Sound paths the map references (ambient_generic message keyvalues). Resolved against the sound registry by the packing stage.</summary>
	public List<string> Sounds { get; } = new();
	/// <summary>Particle system names the map references (info_particle_system effect_name keyvalues). Resolved against the particle registry by the packing stage.</summary>
	public List<string> Particles { get; } = new();

	/// <summary>
	/// Build a compile input from a map document — the adapter the
	/// native-pipeline ticket deferred until MapDocument landed (it has).
	/// Compile-visible brushes become plane brushes (face material name or
	/// "default"; lightmap scale from the face UV), the first
	/// info_player_start becomes the player start, "light" entities become
	/// lights ("light" keyvalue "r g b brightness", unparsable = skipped),
	/// env_cubemap entities become probes (targetname, cubemapsize default
	/// 32), and prop / ambient_generic / info_particle_system keyvalues
	/// become the referenced model / sound / particle lists (deduped).
	/// EVERY compile-visible entity is also recorded on
	/// <see cref="CompileInput.Entities"/> (P2: the entity lump of a document
	/// round-trips through Only-Entities) — id is sequential in document
	/// order, because MapEntityData.Id is a GUID string, not an int.
	/// Detail brushes are NOT carried: brush-entity linkage (func_detail)
	/// belongs to a later ticket, so every brush compiles as a solid here.
	/// Light_spot / light_environment are NOT carried either (owned by the
	/// lighting-environment ticket).
	/// </summary>
	public static CompileInput FromDocument( MapDocument document )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		var input = new CompileInput( document.Name );
		var brushes = document.CompileVisibleBrushes();
		for ( var i = 0; i < brushes.Count; i++ )
		{
			var brush = brushes[i];
			if ( brush is null ) continue;
			if ( !int.TryParse( brush.Id, out var id ) ) id = i;
			var faces = new List<CompileFace>();
			foreach ( var face in brush.Faces )
			{
				if ( face is null ) continue;
				var material = face.Material?.MaterialName;
				if ( string.IsNullOrWhiteSpace( material ) ) material = "default";
				faces.Add( new CompileFace( face.Normal, face.Distance, material )
				{
					LightmapScale = face.Material?.Uv.LightmapScale ?? 1f,
				} );
			}
			input.Brushes.Add( new CompileBrush( brush.Name ?? $"brush-{id}", id, faces ) );
		}
		var entities = document.CompileVisibleEntities();
		var seenModels = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var seenSounds = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var seenParticles = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var entity in entities )
		{
			if ( entity is null || string.IsNullOrWhiteSpace( entity.ClassName ) ) continue;
			var cls = entity.ClassName;
			// The entity lump carries every entity (P2): id is sequential in
			// document order (MapEntityData.Id is a GUID string, not an int);
			// targetname / classname live on the entity, not in keyvalues.
			var compileEntity = new CompileEntity( input.Entities.Count + 1, cls, entity.Origin, entity.TargetName );
			foreach ( var kv in entity.KeyValues )
			{
				if ( string.Equals( kv.Key, "targetname", StringComparison.OrdinalIgnoreCase )
					|| string.Equals( kv.Key, "classname", StringComparison.OrdinalIgnoreCase )
					|| string.Equals( kv.Key, "origin", StringComparison.OrdinalIgnoreCase ) ) continue;
				compileEntity.Keyvalues[kv.Key] = kv.Value ?? "";
			}
			input.Entities.Add( compileEntity );
			if ( string.Equals( cls, "info_player_start", StringComparison.OrdinalIgnoreCase ) )
			{
				input.PlayerStart ??= entity.Origin;
			}
			else if ( string.Equals( cls, "light", StringComparison.OrdinalIgnoreCase ) )
			{
				if ( entity.KeyValues.TryGetValue( "light", out var lightText )
					&& TryParseLight( lightText, out var color, out var intensity ) )
					input.Lights.Add( new CompileLight( entity.Origin, intensity, color ) );
			}
			else if ( string.Equals( cls, "env_cubemap", StringComparison.OrdinalIgnoreCase ) )
			{
				var name = string.IsNullOrWhiteSpace( entity.TargetName ) ? "env_cubemap" : entity.TargetName;
				var resolution = CubemapBake.DefaultResolution;
				if ( entity.KeyValues.TryGetValue( "cubemapsize", out var sizeText )
					&& int.TryParse( sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed ) )
					resolution = parsed;
				input.Probes.Add( new CubemapProbe( name, entity.Origin, resolution ) );
			}
			else if ( IsPropClass( cls )
				&& entity.KeyValues.TryGetValue( "model", out var model )
				&& !string.IsNullOrWhiteSpace( model ) && seenModels.Add( model ) )
			{
				input.Models.Add( model );
			}
			else if ( string.Equals( cls, "ambient_generic", StringComparison.OrdinalIgnoreCase )
				&& entity.KeyValues.TryGetValue( "message", out var sound )
				&& !string.IsNullOrWhiteSpace( sound ) && seenSounds.Add( sound ) )
			{
				input.Sounds.Add( sound );
			}
			else if ( string.Equals( cls, "info_particle_system", StringComparison.OrdinalIgnoreCase )
				&& entity.KeyValues.TryGetValue( "effect_name", out var particle )
				&& !string.IsNullOrWhiteSpace( particle ) && seenParticles.Add( particle ) )
			{
				input.Particles.Add( particle );
			}
		}
		return input;
	}

	static bool IsPropClass( string className ) =>
		string.Equals( className, "prop_static", StringComparison.OrdinalIgnoreCase )
		|| string.Equals( className, "prop_dynamic", StringComparison.OrdinalIgnoreCase )
		|| string.Equals( className, "prop_physics", StringComparison.OrdinalIgnoreCase )
		|| string.Equals( className, "prop_physics_multiplayer", StringComparison.OrdinalIgnoreCase );

	/// <summary>Parse a Source "light" keyvalue ("r g b brightness", 0..255 each) into a linear colour + intensity. False when the value is missing or malformed (the light is skipped, never guessed).</summary>
	static bool TryParseLight( string text, out Vec3 color, out float intensity )
	{
		color = new Vec3( 1, 1, 1 );
		intensity = 1f;
		if ( string.IsNullOrWhiteSpace( text ) ) return false;
		var parts = text.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != 4 ) return false;
		var values = new float[4];
		for ( var i = 0; i < 4; i++ )
			if ( !float.TryParse( parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i] ) ) return false;
		color = new Vec3( values[0] / 255f, values[1] / 255f, values[2] / 255f );
		intensity = values[3];
		return true;
	}
}

/// <summary>
/// A go-to reference: what a log entry / issue points at, so a caller can
/// jump to it (editor viewport, entity list, brush list). At least one of
/// the ids / the coordinates is set on a real reference; an entry with none
/// of them is a map-wide note, not a jump target.
/// </summary>
public sealed class CompileSourceRef
{
	/// <summary>Brush id the entry is about, or null when it is not about a brush.</summary>
	public int? BrushId { get; set; }
	/// <summary>Entity id the entry is about, or null when it is not about an entity.</summary>
	public int? EntityId { get; set; }
	/// <summary>BSP leaf id the entry is about (e.g. the leaf a leak gap opens into), or null.</summary>
	public int? LeafId { get; set; }
	/// <summary>Location in map space, or null when the entry has no location.</summary>
	public Vec3? Location { get; set; }
	/// <summary>Human-readable subject (brush name, entity classname, "map").</summary>
	public string Subject { get; set; } = "map";

	/// <summary>True when this reference names something a caller can jump to (a brush, an entity, a leaf, or a coordinate).</summary>
	public bool HasTarget => BrushId is not null || EntityId is not null || LeafId is not null || Location is not null;

	public override string ToString()
	{
		var parts = new List<string>();
		if ( BrushId is not null ) parts.Add( $"brush #{BrushId}" );
		if ( EntityId is not null ) parts.Add( $"entity #{EntityId}" );
		if ( LeafId is not null ) parts.Add( $"leaf #{LeafId}" );
		if ( Location is not null ) parts.Add( $"@ ({Location.Value.x:0.###} {Location.Value.y:0.###} {Location.Value.z:0.###})" );
		if ( parts.Count == 0 ) return Subject;
		return $"{Subject} ({string.Join( " ", parts )})";
	}
}

/// <summary>
/// One structured log entry: a timestamped line a caller can filter, sort
/// and jump from, unlike the plain-text <see cref="CompileResult.Log"/> lines.
/// Errors and warnings always carry the <see cref="SourceRef"/> of the issue
/// they report (brush / entity / coordinates); stage lines carry the stage
/// and no reference. <see cref="ToString"/> renders the go-to line a log
/// view shows, e.g. "[error] leak @ (136 0 64) — Map leaks ...".
/// </summary>
public sealed class CompileLogEntry
{
	public DateTime Timestamp { get; set; }
	/// <summary>Stage that wrote the entry ("compile" for run-level lines).</summary>
	public string Stage { get; set; } = "compile";
	public CompileSeverity Severity { get; set; }
	/// <summary>Machine-readable code ("leak", "invalid-brush", "stage", ...). Plain stage lines use "stage".</summary>
	public string Code { get; set; } = "stage";
	public string Message { get; set; } = "";
	/// <summary>Where to jump to. Never null; <see cref="CompileSourceRef.HasTarget"/> is false for run-level lines.</summary>
	public CompileSourceRef SourceRef { get; set; } = new();

	public override string ToString()
	{
		var where = SourceRef.HasTarget ? $" [{SourceRef}]" : "";
		return $"[{Timestamp:HH:mm:ss.fff}] [{Stage}] [{Severity.ToString().ToLowerInvariant()}] {Code}{where}: {Message}";
	}
}

/// <summary>
/// One compile issue: the fields the IssueEngine ticket specifies (code,
/// severity, location, named fix hint). Codes are the IssueEngine rule names
/// so this maps 1:1 onto its Issue type when it lands: <c>leak</c>,
/// <c>invalid-brush</c>, <c>empty-map</c>, <c>limit-exceeded</c>,
/// <c>missing-player-start</c>, <c>stage-failed</c> — plus the P2 codes
/// <c>invalid-entity</c> (Only-Entities / entity validation) and
/// <c>stale-build</c> (Only-Entities refused: the brushes, lights or player
/// start changed since the full build whose artefacts were offered).
/// </summary>
public sealed class CompileIssue
{
	public string Code { get; }
	public CompileSeverity Severity { get; }
	public string Message { get; }
	/// <summary>Brush / entity / map the issue is about, e.g. a brush name.</summary>
	public string Subject { get; }
	/// <summary>Location in map space; Zero when the issue has no location.</summary>
	public Vec3 Location { get; }
	public string FixHint { get; }
	/// <summary>Brush id the issue is about, or null. Set for brush issues (invalid-brush carries the brush id).</summary>
	public int? BrushId { get; }
	/// <summary>Entity id the issue is about, or null. Set for entity issues (invalid-entity carries the entity id).</summary>
	public int? EntityId { get; }
	/// <summary>BSP leaf id the issue is about, or null. Set for leaks (the leaf the gap opens into).</summary>
	public int? LeafId { get; }

	/// <summary>Go-to reference for this issue: brush / entity / leaf / coordinates, or a map-wide note when none apply.</summary>
	public CompileSourceRef SourceRef => new()
	{
		BrushId = BrushId,
		EntityId = EntityId,
		LeafId = LeafId,
		Location = Location.Equals( Vec3.Zero ) ? null : Location,
		Subject = Subject,
	};

	public CompileIssue( string code, CompileSeverity severity, string message, string subject, Vec3 location, string fixHint,
		int? brushId = null, int? entityId = null, int? leafId = null )
	{
		Code = code;
		Severity = severity;
		Message = message;
		Subject = subject ?? "map";
		Location = location;
		FixHint = fixHint ?? "";
		BrushId = brushId;
		EntityId = entityId;
		LeafId = leafId;
	}

	/// <summary>Structured log entry for this issue, for the stage that found it.</summary>
	public CompileLogEntry ToLogEntry( string stage ) => new()
	{
		Timestamp = DateTime.UtcNow,
		Stage = stage ?? "compile",
		Severity = Severity,
		Code = Code,
		Message = Message,
		SourceRef = SourceRef,
	};

	public override string ToString() => $"[{Severity}] {Code} ({Subject}): {Message}";
}

/// <summary>One progress report from a running <see cref="CompileRunner"/>. Mirrors <see cref="BakeProgress"/>.</summary>
public sealed class CompileProgress
{
	public CompilePhase Phase;
	public string Stage;
	public string Message;
	public float Percent;
}

/// <summary>One convex BSP leaf as the compile sees it.</summary>
public sealed class CompileLeaf
{
	public int Id { get; }
	/// <summary>Representative point strictly inside the leaf region.</summary>
	public Vec3 Center { get; }
	public Vec3 Min { get; }
	public Vec3 Max { get; }
	/// <summary>Inside a brush solid.</summary>
	public bool IsSolid { get; }
	/// <summary>Empty leaf touching the outer bound — outside the map. Reaching one from the player start means LEAKED.</summary>
	public bool IsVoid { get; }

	public CompileLeaf( int id, Vec3 center, Vec3 min, Vec3 max, bool isSolid, bool isVoid )
	{
		Id = id;
		Center = center;
		Min = min;
		Max = max;
		IsSolid = isSolid;
		IsVoid = isVoid;
	}

	public bool Contains( Vec3 p ) =>
		p.x >= Min.x && p.x <= Max.x &&
		p.y >= Min.y && p.y <= Max.y &&
		p.z >= Min.z && p.z <= Max.z;
}

/// <summary>One open portal: a fragment of a BSP splitting plane, not covered by any solid brush, joining two leaves.</summary>
public sealed class CompilePortal
{
	public int Id { get; }
	public int LeafA { get; }
	public int LeafB { get; }
	public Vec3 Center { get; }

	public CompilePortal( int id, int leafA, int leafB, Vec3 center )
	{
		Id = id;
		LeafA = leafA;
		LeafB = leafB;
		Center = center;
	}

	public int Other( int leafId ) => leafId == LeafA ? LeafB : leafId == LeafB ? LeafA : -1;
}

/// <summary>What the BSP stage built: leaves + portals + lookup.</summary>
public sealed class CompileBspResult
{
	public IReadOnlyList<CompileLeaf> Leaves { get; }
	public IReadOnlyList<CompilePortal> Portals { get; }
	public int LeafCount => Leaves.Count;
	public int PortalCount => Portals.Count;
	public int EmptyLeafCount => Leaves.Count( l => !l.IsSolid && !l.IsVoid );
	public int SolidLeafCount => Leaves.Count( l => l.IsSolid );
	public int VoidLeafCount => Leaves.Count( l => l.IsVoid );

	public CompileBspResult( IReadOnlyList<CompileLeaf> leaves, IReadOnlyList<CompilePortal> portals )
	{
		Leaves = leaves ?? Array.Empty<CompileLeaf>();
		Portals = portals ?? Array.Empty<CompilePortal>();
	}

	/// <summary>Leaf whose region contains the point (walk of the BSP tree is approximated by bounds: interior non-solid leaves win over void leaves, whose bounds are generous and overlap them). Null when the point is in no leaf.</summary>
	public CompileLeaf LeafContaining( Vec3 point )
	{
		foreach ( var leaf in Leaves )
			if ( !leaf.IsVoid && !leaf.IsSolid && leaf.Contains( point ) ) return leaf;
		foreach ( var leaf in Leaves )
			if ( leaf.IsVoid && leaf.Contains( point ) ) return leaf;
		return null;
	}
}

/// <summary>What the visibility stage computed: one visible-leaf set per interior leaf.</summary>
public sealed class CompilePvsResult
{
	/// <summary>Leaf id -> sorted visible interior leaf ids (void and solid leaves are never listed as visible).</summary>
	public IReadOnlyDictionary<int, List<int>> Sets { get; }
	public VisibilityQuality Quality { get; }

	public CompilePvsResult( Dictionary<int, List<int>> sets, VisibilityQuality quality )
	{
		Sets = sets ?? new Dictionary<int, List<int>>();
		Quality = quality;
	}

	public IReadOnlyList<int> VisibleFrom( int leafId ) =>
		Sets.TryGetValue( leafId, out var set ) ? set : Array.Empty<int>();
}

/// <summary>One face's baked lightmap: a texel grid over the face polygon.</summary>
public sealed class CompileLightmapFace
{
	public int BrushId { get; }
	public int FaceIndex { get; }
	public string Material { get; }
	public int Resolution { get; }
	/// <summary>RGB floats, row-major, Resolution * Resolution * 3. Texels outside the face polygon are 0.</summary>
	public float[] Rgb { get; }
	/// <summary>Mean of (r+g+b)/3 over sampled (inside-polygon) texels.</summary>
	public float Mean { get; }
	public int SampledTexels { get; }
	public int TotalTexels => Resolution * Resolution;

	public CompileLightmapFace( int brushId, int faceIndex, string material, int resolution, float[] rgb, float mean, int sampledTexels )
	{
		BrushId = brushId;
		FaceIndex = faceIndex;
		Material = material ?? "default";
		Resolution = resolution;
		Rgb = rgb ?? Array.Empty<float>();
		Mean = mean;
		SampledTexels = sampledTexels;
	}
}

/// <summary>What the lightmap stage baked.</summary>
public sealed class CompileLightmapSet
{
	public IReadOnlyList<CompileLightmapFace> Faces { get; }
	public int Resolution { get; }
	/// <summary>Mean over all faces' means.</summary>
	public float Mean => Faces.Count == 0 ? 0f : Faces.Average( f => f.Mean );

	public CompileLightmapSet( IReadOnlyList<CompileLightmapFace> faces, int resolution )
	{
		Faces = faces ?? Array.Empty<CompileLightmapFace>();
		Resolution = resolution;
	}
}

/// <summary>
/// The rebuilt entity lump from an Only-Entities run: the entities as
/// compiled, plus the serialized lump text (GEPPETTO-ENTITIES 1) written to
/// the <c>.entities</c> file. Deterministic: entities in id order, keyvalues
/// sorted by key.
/// </summary>
public sealed class CompileEntityLump
{
	public IReadOnlyList<CompileEntity> Entities { get; }
	/// <summary>Serialized lump text (GEPPETTO-ENTITIES 1). Written to <c>&lt;name&gt;.entities</c>.</summary>
	public string Text { get; }
	public int Count => Entities.Count;

	public CompileEntityLump( IReadOnlyList<CompileEntity> entities, string text )
	{
		Entities = entities ?? Array.Empty<CompileEntity>();
		Text = text ?? "";
	}
}

/// <summary>
/// The reusable artefacts of a successful full build: the BSP, PVS and
/// lightmaps an Only-Entities run reuses in place, plus the fingerprint of
/// the input they were built from and the product files they came from.
/// Obtained from a successful <see cref="CompileResult"/> via
/// <see cref="CompileResult.ToArtefacts"/>; never constructed from a failed
/// or cancelled run. The fingerprint covers the map name, brushes, lights and
/// player start — everything the BSP / PVS / lightmaps depend on — and
/// excludes the entities, which is exactly what Only-Entities is allowed to
/// change.
/// </summary>
public sealed class CompileBuildArtefacts
{
	public string Name { get; }
	public CompileBspResult Bsp { get; }
	public CompilePvsResult Pvs { get; }
	public CompileLightmapSet Lightmaps { get; }
	/// <summary>Preset the full build ran under (reported on the Only-Entities result).</summary>
	public CompilePreset BasePreset { get; }
	/// <summary>Fingerprint of the full-build input (brushes + lights + player start). Only-Entities refuses a fingerprint mismatch.</summary>
	public string InputFingerprint { get; }
	/// <summary>Product files of the full build (<c>.bsp</c> / <c>.pvs</c> / <c>.lightmap</c>), reused in place by Only-Entities.</summary>
	public IReadOnlyList<string> OutputFiles { get; }

	public CompileBuildArtefacts( string name, CompileBspResult bsp, CompilePvsResult pvs, CompileLightmapSet lightmaps,
		CompilePreset basePreset, string inputFingerprint, IReadOnlyList<string> outputFiles )
	{
		Name = string.IsNullOrWhiteSpace( name ) ? "map" : name;
		Bsp = bsp;
		Pvs = pvs;
		Lightmaps = lightmaps;
		BasePreset = basePreset;
		InputFingerprint = inputFingerprint ?? "";
		OutputFiles = outputFiles ?? Array.Empty<string>();
	}
}

/// <summary>
/// What a <see cref="CompileRunner"/> run produced. A failed or cancelled run
/// has null stage products and a readable <see cref="Error"/> (or
/// <see cref="Cancelled"/>); it never throws, and it never writes product
/// files — writing happens only on success, mirroring
/// <see cref="BakeJobResult"/>.
/// </summary>
public sealed class CompileResult
{
	public bool Succeeded => Error is null && !Cancelled && FailedStage is null;
	public bool Cancelled { get; set; }
	public string Error { get; set; }
	/// <summary>Stage that failed, or null when the run succeeded / was cancelled.</summary>
	public CompileStage? FailedStage { get; set; }
	/// <summary>Stage the cancel landed in, or null when not cancelled.</summary>
	public CompileStage? CancelledStage { get; set; }
	/// <summary>Stages that completed, in run order.</summary>
	public List<CompileStage> CompletedStages { get; } = new();
	public CompileBspResult Bsp { get; set; }
	public CompilePvsResult Pvs { get; set; }
	public CompileLightmapSet Lightmaps { get; set; }
	/// <summary>Entity lump built by this run (full runs write it when the input has entities; Only-Entities always builds it). Null when the input has no entities.</summary>
	public CompileEntityLump Entities { get; set; }
	/// <summary>True when this result came from an Only-Entities run (entity lump rebuilt, BSP + PVS + lightmaps reused).</summary>
	public bool OnlyEntities { get; set; }
	/// <summary>True when this run reused prior artefacts (Only-Entities). False on a full run.</summary>
	public bool ReusedPriorArtefacts { get; set; }
	/// <summary>Prior product files reused in place by this run (Only-Entities). Empty on a full run.</summary>
	public List<string> ReusedFiles { get; } = new();
	/// <summary>Fingerprint of the input this result was built from (map name + brushes + lights + player start). Used to validate a later Only-Entities run.</summary>
	public string InputFingerprint { get; set; } = "";
	/// <summary>Named preset this run applied, or null for a built-in preset run.</summary>
	public string NamedPreset { get; set; }
	/// <summary>Cubemap bake set when the CubemapBuild stage ran. Null when the stage was skipped (QuickPlay, or no probes).</summary>
	public CubemapBakeSet Cubemaps { get; set; }
	/// <summary>Release pack when the Pack stage ran. Null when the stage was skipped (not Final, or no registries supplied).</summary>
	public MapPackResult Pack { get; set; }
	/// <summary>Leak check result when one ran (player start present). Null when the leak check was skipped.</summary>
	public LeakFinder.LeakResult Leak { get; set; }
	/// <summary>Pre-compile safety-net gate result when the gate ran (or was skipped by override). Null when the run was cancelled before the gate.</summary>
	public PreCompileGateResult Gate { get; set; }
	/// <summary>Issues found, errors first. Empty on a clean run.</summary>
	public List<CompileIssue> Issues { get; } = new();
	/// <summary>Timestamped log lines (also written to <see cref="LogPath"/> when one is configured).</summary>
	public List<string> Log { get; } = new();
	/// <summary>Structured log entries (one per log line, plus one per issue): each error / warning carries the brush / entity / coordinate reference a caller can jump to. Same order as <see cref="Log"/> for stage lines; issue entries follow the run in the order the issues were found.</summary>
	public List<CompileLogEntry> LogEntries { get; } = new();
	public string LogPath { get; set; }
	/// <summary>Product files written on success (plus the .lin on a leak). Empty when nothing was written.</summary>
	public List<string> OutputFiles { get; } = new();
	public TimeSpan Duration { get; set; }
	public CompilePreset Preset { get; set; }

	/// <summary>
	/// Capture this result as reusable artefacts for a later Only-Entities
	/// run. Throws when the run did not succeed or produced no BSP (a failed
	/// or cancelled run has nothing to reuse). The fingerprint is taken from
	/// this result when the runner recorded it, else recomputed from
	/// <paramref name="input"/>; pass the same input the run used.
	/// </summary>
	public CompileBuildArtefacts ToArtefacts( CompileInput input )
	{
		if ( !Succeeded ) throw new ArgumentException( "Only a successful compile result can be reused as Only-Entities artefacts.", nameof( input ) );
		if ( Bsp is null ) throw new ArgumentException( "Compile result has no BSP to reuse.", nameof( input ) );
		var fingerprint = !string.IsNullOrEmpty( InputFingerprint ) ? InputFingerprint
			: input is null ? "" : CompileRunner.FingerprintInput( input );
		return new CompileBuildArtefacts(
			input?.Name ?? "map", Bsp, Pvs, Lightmaps, Preset, fingerprint,
			OutputFiles.ToList() );
	}
}

/// <summary>
/// The native Geppetto compile pipeline: preset definitions, stage
/// orchestration (BSP build -> visibility -> lightmap bake -> cubemap build -> release pack) reusing Baker's
/// progress/cancel pattern, log capture to a file, stage failure mapped to
/// <see cref="CompileIssue"/>s. Mirrors <see cref="BakeJob"/>: construct with
/// the input + preset, <see cref="Run"/> never throws for a run failure or a
/// cancel, progress is real (per stage, per face during the bake).
/// </summary>
public sealed class CompileRunner
{
	// Plane classification tolerance (units). Map coordinates run to ~16K, so
	// this must stay well above float noise at that scale (~0.002) and well
	// below the smallest real gap (the leak fixture's 1-unit gap).
	const float EpsPlane = 0.01f;
	const float EpsPoint = 0.02f;
	// Portal fragment -> leaf lookup offset (units). Well inside a 16-unit wall.
	const float PortalOffset = 0.25f;
	// World bound margin around the map (units) for the outer BSP box.
	const float OuterMargin = 1024f;
	const int MaxBspDepth = 48;

	readonly CompileInput _input;
	readonly CompilePresetDef _def;
	readonly CompileOptions _options;
	readonly string _namedPreset;
	readonly List<string> _log = new();
	readonly List<CompileLogEntry> _entries = new();
	readonly object _progressLock = new();

	/// <summary>Worker count for this run's parallel stage work: 1 when SingleThreaded, else the resolved MaxDegreeOfParallelism.</summary>
	int DegreeOfParallelism => _options.SingleThreaded ? 1 : ParallelStageRunner.ResolveDegreeOfParallelism( _options.MaxDegreeOfParallelism );


	public CompileInput Input => _input;
	public CompilePreset Preset => _def.Preset;
	public CompilePresetDef Definition => _def;
	public CompileOptions Options => _options;
	/// <summary>Named preset this runner applies, or null for a built-in preset run.</summary>
	public string NamedPreset => _namedPreset;

	public CompileRunner( CompileInput input, CompilePreset preset, CompileOptions options = null )
	{
		_input = input ?? throw new ArgumentNullException( nameof( input ) );
		_def = CompilePresetDef.For( preset );
		_options = options ?? new CompileOptions();
		_options.Validate();
	}

	/// <summary>
	/// Run with a saved named preset: the base preset plus the named preset's
	/// overrides, merged with any explicit per-run <paramref name="options"/>
	/// (explicit options win where they are set: a set VisibilityOverride or
	/// a LightmapResolutionOverride &gt; 0, or FastVis true; OutputFolder /
	/// LogPath / limits always come from the explicit options).
	/// </summary>
	public CompileRunner( CompileInput input, CompileNamedPreset named, CompileOptions options = null )
	{
		_input = input ?? throw new ArgumentNullException( nameof( input ) );
		if ( named is null ) throw new ArgumentNullException( nameof( named ) );
		named.Validate();
		_namedPreset = named.Name;
		_def = CompilePresetDef.For( named.BasePreset );
		var merged = new CompileOptions
		{
			VisibilityOverride = options?.VisibilityOverride ?? named.VisibilityOverride,
			LightmapResolutionOverride = options is not null && options.LightmapResolutionOverride > 0
				? options.LightmapResolutionOverride : named.LightmapResolutionOverride,
			FastVis = named.FastVis || ( options?.FastVis == true ),
			OutputFolder = options?.OutputFolder,
			LogPath = options?.LogPath,
			MaxBrushes = options?.MaxBrushes ?? 16384,
			MaxPlanesPerBrush = options?.MaxPlanesPerBrush ?? 128,
			AssetRegistries = options?.AssetRegistries,
			MaxDegreeOfParallelism = options?.MaxDegreeOfParallelism ?? -1,
			SingleThreaded = options?.SingleThreaded == true,
		};
		merged.Validate();
		_options = merged;
	}

	/// <summary>One-shot run without constructing a runner.</summary>
	public static CompileResult Run( CompileInput input, CompilePreset preset, CompileOptions options = null,
		IProgress<CompileProgress> progress = null, CancellationToken cancel = default ) =>
		new CompileRunner( input, preset, options ).Run( progress, cancel );

	/// <summary>One-shot run with a saved named preset, without constructing a runner.</summary>
	public static CompileResult Run( CompileInput input, CompileNamedPreset named, CompileOptions options = null,
		IProgress<CompileProgress> progress = null, CancellationToken cancel = default ) =>
		new CompileRunner( input, named, options ).Run( progress, cancel );

	/// <summary>One-shot named-preset run on a background thread (same result contract as <see cref="Run"/>).</summary>
	public static Task<CompileResult> RunAsync( CompileInput input, CompileNamedPreset named, CompileOptions options = null,
		IProgress<CompileProgress> progress = null, CancellationToken cancel = default ) =>
		Task.Run( () => Run( input, named, options, progress, cancel ) );

	/// <summary>One-shot run on a background thread (same result contract as <see cref="Run"/>).</summary>
	public static Task<CompileResult> RunAsync( CompileInput input, CompilePreset preset, CompileOptions options = null,
		IProgress<CompileProgress> progress = null, CancellationToken cancel = default ) =>
		Task.Run( () => Run( input, preset, options, progress, cancel ) );

	/// <summary>Run on a background thread (same result contract as <see cref="Run"/>).</summary>
	public Task<CompileResult> RunAsync( IProgress<CompileProgress> progress = null, CancellationToken cancel = default ) =>
		Task.Run( () => Run( progress, cancel ) );

	/// <summary>
	/// Run the compile. Never throws for a run failure or a cancel: those come
	/// back as <see cref="CompileResult.Error"/> / <see cref="CompileResult.Cancelled"/>.
	/// </summary>
	public CompileResult Run( IProgress<CompileProgress> progress = null, CancellationToken cancel = default )
	{
		var clock = Stopwatch.StartNew();
		var result = new CompileResult { Preset = _def.Preset, NamedPreset = _namedPreset };
		var visibility = _options.VisibilityOverride ?? _def.Visibility;
		// Fast-VIS is a downgrade only: relax an existing visibility stage to
		// 1-hop, never add visibility to QuickPlay, never upgrade to Full.
		if ( _options.FastVis && visibility != VisibilityQuality.None ) visibility = VisibilityQuality.Fast;
		var lightmapResolution = _options.LightmapResolutionOverride > 0 ? _options.LightmapResolutionOverride : _def.LightmapResolution;
		CompileStage? currentStage = null;

		void Report( CompilePhase phase, float percent, string stage = null, string message = null ) =>
			progress?.Report( new CompileProgress { Phase = phase, Percent = percent, Stage = stage, Message = message } );

		void Log( string stage, string message )
		{
			var now = DateTime.UtcNow;
			var line = string.Format( CultureInfo.InvariantCulture, "[{0:HH:mm:ss.fff}] [{1}] {2}",
				now, stage, message );
			_log.Add( line );
			_entries.Add( new CompileLogEntry
			{
				Timestamp = now,
				Stage = stage,
				Severity = CompileSeverity.Info,
				Code = "stage",
				Message = message,
			} );
		}

		CompileResult Fail( CompileStage stage, string error, List<CompileIssue> issues = null )
		{
			result.Error = error;
			result.FailedStage = stage;
			result.Duration = clock.Elapsed;
			if ( issues is not null ) result.Issues.AddRange( issues );
			Log( stage.ToString(), "FAILED: " + error );
			Report( CompilePhase.Failed, 100f, stage.ToString(), error );
			FinishLog( result, _log, _entries, _options, _input.Name );
			return result;
		}

		CompileResult Cancel( CompileStage? stage )
		{
			result.Cancelled = true;
			result.CancelledStage = stage;
			result.Duration = clock.Elapsed;
			Log( "compile", stage is null ? "Cancelled before any stage." : $"Cancelled during {stage}." );
			Report( CompilePhase.Cancelled, 100f, stage?.ToString(), "Compile cancelled." );
			FinishLog( result, _log, _entries, _options, _input.Name );
			return result;
		}

		try
		{
			var input = PrepareInput( _input );
			Log( "compile", $"Compile started: '{input.Name}' preset {_def.Name}{(_namedPreset is null ? "" : $" (named preset '{_namedPreset}')")} ({string.Join( " -> ", _def.Stages() )})." );
			Report( CompilePhase.Preparing, 0f, null, $"Compiling '{input.Name}' ({_def.Name})." );
			cancel.ThrowIfCancellationRequested();

			// -- Pre-compile safety-net gate: the full Check-for-Problems
			// pass, first in every preset (hammer-compile-safety-net-gate).
			// Errors block the compile before any build stage runs;
			// warnings are logged and the compile proceeds. A caller
			// override skips the gate and is logged. The gate is a
			// pre-stage in the Preparing phase, not a build stage: a gate
			// block reports FailedStage BspBuild (no build stage ran) so
			// the existing stage contract holds, and the gate result (with
			// the named issues) is on CompileResult.Gate. The leak verdict
			// stays with the BSP stage: the gate has no BSP portal graph
			// yet, so its leak rules are skipped and the BSP stage's
			// LeakFinder flood-fill + .lin point file still own leaks
			// (see PreCompileGate.cs).
			Report( CompilePhase.Preparing, 1f, null, "Running pre-compile gate." );
			if ( _options.SkipPreCompileGate )
			{
				result.Gate = PreCompileGateResult.Skipped();
				Log( "gate", "Pre-compile gate SKIPPED by caller override (CompileOptions.SkipPreCompileGate). The compile proceeds without the safety-net check." );
			}
			else
			{
				var gate = PreCompileGate.Check( input, _options );
				result.Gate = gate;
				if ( gate.SkippedRules.Count > 0 )
					Log( "gate", $"Rules skipped (no BSP portal graph yet — the BSP stage owns the leak verdict): {string.Join( ", ", gate.SkippedRules )}." );
				foreach ( var gateIssue in gate.Issues )
					Log( "gate", $"{gateIssue.Severity.ToString().ToLowerInvariant()} {PreCompileGate.IssueCode( gateIssue.Type )}: {gateIssue.Message} (fix: {gateIssue.FixHint})" );
				if ( gate.Blocked )
				{
					var first = gate.Issues.First( i => i.Severity == MapIssueSeverity.Error );
					return Fail( CompileStage.BspBuild,
						$"Pre-compile gate blocked the compile: {gate.ErrorCount} error(s), first: {first.Message}",
						gate.Issues.Select( i => PreCompileGate.ToCompileIssue( input, i ) ).ToList() );
				}
				result.Issues.AddRange( gate.Issues.Select( i => PreCompileGate.ToCompileIssue( input, i ) ) );
				Log( "gate", gate.Issues.Count == 0
					? "Pre-compile gate passed: no issues."
					: $"Pre-compile gate passed with {gate.WarningCount} warning(s); the compile proceeds." );
			}

			// -- Stage 1: BSP build (includes validation + leak check). ------
			currentStage = CompileStage.BspBuild;
			Report( CompilePhase.BspBuild, 2f, nameof( CompileStage.BspBuild ), "Building BSP." );
			var bsp = BuildBspStage( input, result, cancel, Log, out var bspError );
			if ( cancel.IsCancellationRequested ) return Cancel( CompileStage.BspBuild );
			if ( bsp is null ) return Fail( CompileStage.BspBuild, bspError );
			result.Bsp = bsp;
			result.CompletedStages.Add( CompileStage.BspBuild );
			Log( nameof( CompileStage.BspBuild ),
				$"BSP done: {bsp.LeafCount} leaves ({bsp.EmptyLeafCount} empty, {bsp.SolidLeafCount} solid, {bsp.VoidLeafCount} void), {bsp.PortalCount} portals." );
			Report( CompilePhase.BspBuild, 40f, nameof( CompileStage.BspBuild ), "BSP done." );

			// -- Stage 2: visibility. ----------------------------------------
			if ( visibility != VisibilityQuality.None )
			{
				currentStage = CompileStage.Visibility;
				Report( CompilePhase.Visibility, 42f, nameof( CompileStage.Visibility ), "Computing visibility." );
				cancel.ThrowIfCancellationRequested();
			result.Pvs = DegreeOfParallelism <= 1
				? ComputePvs( bsp, visibility )
				: ComputePvsParallel( bsp, visibility, DegreeOfParallelism, cancel );

				result.CompletedStages.Add( CompileStage.Visibility );
				Log( nameof( CompileStage.Visibility ),
					$"Visibility done ({visibility}): {result.Pvs.Sets.Count} leaf sets." );
				Report( CompilePhase.Visibility, 55f, nameof( CompileStage.Visibility ), "Visibility done." );
			}

			// -- Stage 3: lightmap bake. --------------------------------------
			if ( lightmapResolution > 0 )
			{
				currentStage = CompileStage.LightmapBake;
				Report( CompilePhase.LightmapBake, 57f, nameof( CompileStage.LightmapBake ), "Baking lightmaps." );
				result.Lightmaps = BakeLightmaps( input, lightmapResolution, progress, cancel, Log );
				if ( cancel.IsCancellationRequested ) return Cancel( CompileStage.LightmapBake );
				result.CompletedStages.Add( CompileStage.LightmapBake );
				Log( nameof( CompileStage.LightmapBake ),
					$"Lightmap bake done: {result.Lightmaps.Faces.Count} faces at {lightmapResolution}x{lightmapResolution}, mean {result.Lightmaps.Mean:0.###}." );
				Report( CompilePhase.LightmapBake, 95f, nameof( CompileStage.LightmapBake ), "Lightmap bake done." );
			}

			// -- Stage 4: cubemap build. --------------------------------------
			if ( _def.RunCubemaps && input.Probes.Count > 0 )
			{
				currentStage = CompileStage.CubemapBuild;
				Report( CompilePhase.CubemapBuild, 95.5f, nameof( CompileStage.CubemapBuild ), "Building cubemaps." );
				if ( cancel.IsCancellationRequested ) return Cancel( CompileStage.CubemapBuild );
				var bake = MapPacker.BuildCubemaps( input, cancel: cancel );
				if ( bake.Cancelled ) return Cancel( CompileStage.CubemapBuild );
				if ( !bake.Succeeded ) return Fail( CompileStage.CubemapBuild, bake.Error, bake.Issues );
				result.Cubemaps = bake.Set;
				result.Issues.AddRange( bake.Issues );
				result.CompletedStages.Add( CompileStage.CubemapBuild );
				Log( nameof( CompileStage.CubemapBuild ),
					$"Cubemap build done: {bake.Set.ProbeCount} probes x 6 faces." );
				Report( CompilePhase.CubemapBuild, 96.5f, nameof( CompileStage.CubemapBuild ), "Cubemap build done." );
			}

			// -- Stage 5: release packing. ------------------------------------
			if ( _def.RunPacking )
			{
				if ( _options.AssetRegistries is null )
				{
					Log( nameof( CompileStage.Pack ), "Packing skipped: no asset registries supplied (CompileOptions.AssetRegistries)." );
				}
				else
				{
					currentStage = CompileStage.Pack;
					Report( CompilePhase.Pack, 96.7f, nameof( CompileStage.Pack ), "Packing used assets." );
					if ( cancel.IsCancellationRequested ) return Cancel( CompileStage.Pack );
					var pack = MapPacker.Pack( input, _options.AssetRegistries, packagePath: null, cubemaps: result.Cubemaps );
					if ( !pack.Succeeded ) return Fail( CompileStage.Pack, pack.Error, pack.Issues );
					result.Pack = pack;
					result.CompletedStages.Add( CompileStage.Pack );
					Log( nameof( CompileStage.Pack ),
						$"Packed {pack.Summary}{( pack.Cubemaps is null ? "" : $" + {pack.Cubemaps.ProbeCount} probe(s) x 6 cubemap faces" )}." );
					Report( CompilePhase.Pack, 96.9f, nameof( CompileStage.Pack ), "Packing done." );
				}
			}

			// -- Entity lump + fingerprint (products only on success). --------
			// Entities were validated in the BSP stage, so by here they are
			// valid; the lump is a product of the full build too, not only of
			// Only-Entities. The fingerprint covers the map name + brushes +
			// lights + player start (everything the BSP / PVS / lightmaps
			// depend on).
			result.InputFingerprint = FingerprintInput( input );
			if ( input.Entities is not null && input.Entities.Count > 0 )
			{
				result.Entities = BuildEntityLump( input.Entities );
				Log( "compile", $"Entity lump built: {result.Entities.Count} entities." );
			}

			// -- Writing (products only on success). ---------------------------
			currentStage = null;
			Report( CompilePhase.Writing, 97f, null, "Writing outputs." );
			cancel.ThrowIfCancellationRequested();
			WriteOutputs( input, result );
			result.Duration = clock.Elapsed;
			Log( "compile", $"Compile finished in {result.Duration.TotalSeconds:0.###}s: stages {string.Join( ", ", result.CompletedStages )}." );
			Report( CompilePhase.Done, 100f, null, $"Compile done in {result.Duration.TotalSeconds:0.###}s." );
			FinishLog( result, _log, _entries, _options, _input.Name );
			return result;
		}
		catch ( OperationCanceledException )
		{
			return Cancel( currentStage );
		}
		catch ( Exception ex )
		{
			result.Error = $"Compile failed ({ex.GetType().Name}): {ex.Message}";
			result.FailedStage = currentStage ?? ( result.CompletedStages.Count == 0 ? CompileStage.BspBuild : result.CompletedStages[^1] );
			result.Issues.Add( new CompileIssue( "stage-failed", CompileSeverity.Error, result.Error, "map", Vec3.Zero,
				"Fix the underlying error and recompile." ) );
			result.Duration = clock.Elapsed;
			Log( "compile", "FAILED: " + result.Error );
			Report( CompilePhase.Failed, 100f, result.FailedStage.ToString(), result.Error );
			FinishLog( result, _log, _entries, _options, _input.Name );
			return result;
		}
	}

	// -- Only-Entities fast path (P2) -----------------------------------------
	//
	// Rebuild the entity lump only, reusing the BSP + PVS + lightmaps of the
	// last full build. The fingerprint (map name + brushes + lights + player
	// start — everything the BSP / PVS / lightmaps depend on, entities
	// excluded) must match the prior build, or the run refuses with a
	// stale-build issue: a changed brush would make the reused BSP wrong, and
	// silently reusing it would ship a map whose collision does not match its
	// geometry. No BSP, visibility or lightmap stage runs; no prior product
	// file is rewritten — only <name>.entities is written, on success.
	// The leak check is skipped: the prior full build already proved the map
	// sealed, and entities cannot open a leak (brush entities are geometry —
	// changing one is a brush change, which the fingerprint catches).

	/// <summary>
	/// Only-Entities run: rebuild the entity lump from <paramref name="input"/>
	/// and reuse <paramref name="lastBuild"/>'s BSP, PVS and lightmaps.
	/// Never throws for a run failure or a cancel (those come back as
	/// <see cref="CompileResult.Error"/> / <see cref="CompileResult.Cancelled"/>);
	/// throws for API misuse (null input / artefacts, artefacts with no BSP).
	/// </summary>
	public static CompileResult RunOnlyEntities( CompileInput input, CompileBuildArtefacts lastBuild, CompileOptions options = null,
		IProgress<CompileProgress> progress = null, CancellationToken cancel = default )
	{
		if ( input is null ) throw new ArgumentNullException( nameof( input ) );
		if ( lastBuild is null ) throw new ArgumentNullException( nameof( lastBuild ) );
		if ( lastBuild.Bsp is null ) throw new ArgumentException( "Only-Entities needs a prior build with a BSP to reuse.", nameof( lastBuild ) );
		options ??= new CompileOptions();
		options.Validate();

		var clock = Stopwatch.StartNew();
		var result = new CompileResult { Preset = lastBuild.BasePreset, OnlyEntities = true };
		var log = new List<string>();
		var entries = new List<CompileLogEntry>();
		CompileStage? currentStage = null;

		void Report( CompilePhase phase, float percent, string stage = null, string message = null ) =>
			progress?.Report( new CompileProgress { Phase = phase, Percent = percent, Stage = stage, Message = message } );

		void Log( string stage, string message )
		{
			var now = DateTime.UtcNow;
			log.Add( string.Format( CultureInfo.InvariantCulture, "[{0:HH:mm:ss.fff}] [{1}] {2}", now, stage, message ) );
			entries.Add( new CompileLogEntry { Timestamp = now, Stage = stage, Severity = CompileSeverity.Info, Code = "stage", Message = message } );
		}

		CompileResult Fail( string error, List<CompileIssue> issues = null )
		{
			result.Error = error;
			result.FailedStage = CompileStage.EntityLump;
			result.Duration = clock.Elapsed;
			if ( issues is not null ) result.Issues.AddRange( issues );
			Log( nameof( CompileStage.EntityLump ), "FAILED: " + error );
			Report( CompilePhase.Failed, 100f, nameof( CompileStage.EntityLump ), error );
			FinishLog( result, log, entries, options, input.Name );
			return result;
		}

		CompileResult Cancel( CompileStage? stage )
		{
			result.Cancelled = true;
			result.CancelledStage = stage;
			result.Duration = clock.Elapsed;
			Log( "compile", stage is null ? "Cancelled before any stage." : $"Cancelled during {stage}." );
			Report( CompilePhase.Cancelled, 100f, stage?.ToString(), "Compile cancelled." );
			FinishLog( result, log, entries, options, input.Name );
			return result;
		}

		try
		{
			Log( "compile", $"Only-Entities started: '{input.Name}' reusing BSP/PVS/lightmaps from the '{lastBuild.Name}' build." );
			Report( CompilePhase.Preparing, 0f, null, $"Rebuilding entities for '{input.Name}'." );
			cancel.ThrowIfCancellationRequested();

			// The prior artefacts are only valid for the input they were built
			// from (name + brushes + lights + player start). Anything else
			// changed means a full build is required.
			var fingerprint = FingerprintInput( input );
			if ( !string.Equals( fingerprint, lastBuild.InputFingerprint, StringComparison.Ordinal ) )
			{
				var issues = new List<CompileIssue>
				{
					new CompileIssue( "stale-build", CompileSeverity.Error,
						$"Brushes, lights or player start changed since the '{lastBuild.Name}' build: Only-Entities reuses its BSP, PVS and lightmaps, so run a full build instead.",
						"map", Vec3.Zero,
						"Run a full build (Quick Play, Preview, Final or Fast-VIS) to rebuild the BSP, PVS and lightmaps for the changed geometry." ),
				};
				return Fail( "Prior build artefacts are stale for this input.", issues );
			}
			result.InputFingerprint = fingerprint;

			// Entities are the only thing allowed to change — validate them.
			var entityIssues = ValidateEntities( input.Entities );
			if ( entityIssues.Any( i => i.Severity == CompileSeverity.Error ) )
				return Fail( entityIssues.First( i => i.Severity == CompileSeverity.Error ).Message, entityIssues );
			result.Issues.AddRange( entityIssues );

			currentStage = CompileStage.EntityLump;
			Report( CompilePhase.EntityLump, 50f, nameof( CompileStage.EntityLump ), "Rebuilding entity lump." );
			cancel.ThrowIfCancellationRequested();
			result.Entities = BuildEntityLump( input.Entities );
			result.CompletedStages.Add( CompileStage.EntityLump );
			Log( nameof( CompileStage.EntityLump ), $"Entity lump built: {result.Entities.Count} entities." );
			Report( CompilePhase.EntityLump, 90f, nameof( CompileStage.EntityLump ), "Entity lump built." );

			// Reuse the prior products (same object references — the caller can
			// prove no rebuild happened) and write only the new lump.
			result.Bsp = lastBuild.Bsp;
			result.Pvs = lastBuild.Pvs;
			result.Lightmaps = lastBuild.Lightmaps;
			result.ReusedPriorArtefacts = true;
			result.ReusedFiles.AddRange( lastBuild.OutputFiles );
			Report( CompilePhase.Writing, 97f, null, "Writing entity lump." );
			cancel.ThrowIfCancellationRequested();
			if ( !string.IsNullOrWhiteSpace( options.OutputFolder ) )
			{
				Directory.CreateDirectory( options.OutputFolder );
				var path = Path.Combine( options.OutputFolder, input.Name + ".entities" );
				File.WriteAllText( path, result.Entities.Text );
				result.OutputFiles.Add( path );
			}
			result.Duration = clock.Elapsed;
			Log( "compile", $"Only-Entities finished in {result.Duration.TotalSeconds:0.###}s: {result.Entities.Count} entities, BSP ({result.Bsp.LeafCount} leaves), PVS and lightmaps reused." );
			Report( CompilePhase.Done, 100f, null, $"Only-Entities done in {result.Duration.TotalSeconds:0.###}s." );
			FinishLog( result, log, entries, options, input.Name );
			return result;
		}
		catch ( OperationCanceledException )
		{
			return Cancel( currentStage );
		}
		catch ( Exception ex )
		{
			result.Error = $"Only-Entities failed ({ex.GetType().Name}): {ex.Message}";
			result.FailedStage = CompileStage.EntityLump;
			result.Issues.Add( new CompileIssue( "stage-failed", CompileSeverity.Error, result.Error, "map", Vec3.Zero,
				"Fix the underlying error and retry Only-Entities." ) );
			result.Duration = clock.Elapsed;
			Log( "compile", "FAILED: " + result.Error );
			Report( CompilePhase.Failed, 100f, nameof( CompileStage.EntityLump ), result.Error );
			FinishLog( result, log, entries, options, input.Name );
			return result;
		}
	}

	/// <summary>Only-Entities run on a background thread (same result contract as <see cref="RunOnlyEntities"/>).</summary>
	public static Task<CompileResult> RunOnlyEntitiesAsync( CompileInput input, CompileBuildArtefacts lastBuild, CompileOptions options = null,
		IProgress<CompileProgress> progress = null, CancellationToken cancel = default ) =>
		Task.Run( () => RunOnlyEntities( input, lastBuild, options, progress, cancel ) );

	/// <summary>
	/// Fingerprint of everything the BSP / PVS / lightmaps depend on: the map
	/// name, every brush (id, name, detail flag, face planes + materials, in
	/// id order), every light (position, colour, intensity, range, in position
	/// order) and the player start. Entities are excluded by design — they are
	/// what Only-Entities is allowed to change. Deterministic (FNV-1a over a
	/// canonical invariant-culture string); same input -&gt; same fingerprint.
	/// </summary>
	internal static string FingerprintInput( CompileInput input )
	{
		var text = new System.Text.StringBuilder();
		text.Append( "name=" ).Append( input.Name ?? "" ).Append( '\n' );
		if ( input.PlayerStart is not null )
			text.Append( "playerStart=" ).Append( R( input.PlayerStart.Value.x ) ).Append( ' ' ).Append( R( input.PlayerStart.Value.y ) ).Append( ' ' ).Append( R( input.PlayerStart.Value.z ) ).Append( '\n' );
		foreach ( var brush in ( input.Brushes ?? new List<CompileBrush>() ).Where( b => b is not null ).OrderBy( b => b.Id ) )
		{
			text.Append( "brush " ).Append( brush.Id ).Append( ' ' ).Append( brush.Name ?? "" ).Append( " detail=" ).Append( brush.IsDetail ? "1" : "0" ).Append( '\n' );
			foreach ( var face in brush.Faces )
				text.Append( " face " ).Append( R( face.Normal.x ) ).Append( ' ' ).Append( R( face.Normal.y ) ).Append( ' ' ).Append( R( face.Normal.z ) ).Append( ' ' ).Append( R( face.Distance ) ).Append( ' ' ).Append( face.Material ?? "" ).Append( '\n' );
		}
		foreach ( var light in ( input.Lights ?? new List<CompileLight>() ).Where( l => l is not null )
			.OrderBy( l => l.Position.x ).ThenBy( l => l.Position.y ).ThenBy( l => l.Position.z ) )
		{
			text.Append( "light " ).Append( R( light.Position.x ) ).Append( ' ' ).Append( R( light.Position.y ) ).Append( ' ' ).Append( R( light.Position.z ) )
				.Append( ' ' ).Append( R( light.Color.x ) ).Append( ' ' ).Append( R( light.Color.y ) ).Append( ' ' ).Append( R( light.Color.z ) )
				.Append( ' ' ).Append( R( light.Intensity ) ).Append( ' ' ).Append( R( light.Range ) ).Append( '\n' );
		}
		ulong hash = 14695981039346656037ul;
		foreach ( var b in System.Text.Encoding.UTF8.GetBytes( text.ToString() ) )
		{
			hash ^= b;
			hash *= 1099511628211ul;
		}
		return hash.ToString( "x16", CultureInfo.InvariantCulture );
	}

	static string R( float v ) => v.ToString( "R", CultureInfo.InvariantCulture );

	/// <summary>
	/// Validate an entity list: per-entity defects plus duplicate ids.
	/// Returns error issues (empty when valid). The go-to reference on each
	/// issue carries the entity id and its origin.
	/// </summary>
	internal static List<CompileIssue> ValidateEntities( IReadOnlyList<CompileEntity> entities )
	{
		var issues = new List<CompileIssue>();
		if ( entities is null ) return issues;
		var seenIds = new HashSet<int>();
		foreach ( var entity in entities )
		{
			if ( entity is null )
			{
				issues.Add( new CompileIssue( "invalid-entity", CompileSeverity.Error,
					"Null entity in the map.", "map", Vec3.Zero, "Remove the null entity entry." ) );
				continue;
			}
			if ( entity.Id > 0 && !seenIds.Add( entity.Id ) )
				issues.Add( new CompileIssue( "invalid-entity", CompileSeverity.Error,
					$"Duplicate entity id {entity.Id} (entity '{entity.ClassName}').", entity.ClassName, entity.Origin,
					"Give each entity a unique id.", entityId: entity.Id ) );
			foreach ( var defect in entity.Validate() )
				issues.Add( new CompileIssue( "invalid-entity", CompileSeverity.Error,
					$"Entity #{entity.Id} ('{entity.ClassName}') is invalid: {defect}.", entity.ClassName, entity.Origin,
					"Fix the entity (positive unique id, non-empty classname, finite origin) or delete it.", entityId: entity.Id > 0 ? entity.Id : null ) );
		}
		return issues;
	}

	/// <summary>Build the entity lump for an entity list (id order, deterministic text).</summary>
	internal static CompileEntityLump BuildEntityLump( IReadOnlyList<CompileEntity> entities )
	{
		var sorted = ( entities ?? Array.Empty<CompileEntity>() ).Where( e => e is not null )
			.OrderBy( e => e.Id ).ThenBy( e => e.ClassName, StringComparer.Ordinal ).ToList();
		return new CompileEntityLump( sorted, EntityLumpText( sorted ) );
	}

	/// <summary>
	/// GEPPETTO-ENTITIES 1: header, count, one line per entity —
	/// <c>entity &lt;id&gt; &lt;classname&gt; origin &lt;x y z&gt; [targetname &lt;name&gt;] [&lt;key&gt;="&lt;value&gt;" ...]</c>
	/// with keyvalues sorted by key and values escaped. Deterministic.
	/// </summary>
	internal static string EntityLumpText( IReadOnlyList<CompileEntity> entities )
	{
		var lines = new List<string> { "GEPPETTO-ENTITIES 1", $"count {entities.Count}" };
		foreach ( var entity in entities )
		{
			var line = $"entity {entity.Id} {entity.ClassName} origin {F( entity.Origin.x )} {F( entity.Origin.y )} {F( entity.Origin.z )}";
			if ( !string.IsNullOrWhiteSpace( entity.TargetName ) ) line += $" targetname {entity.TargetName}";
			foreach ( var pair in entity.Keyvalues.OrderBy( p => p.Key, StringComparer.Ordinal ) )
				line += $" {pair.Key}=\"{EscapeLumpValue( pair.Value ?? "" )}\"";
			lines.Add( line );
		}
		return string.Join( "\n", lines ) + "\n";
	}

	static string EscapeLumpValue( string value ) => value.Replace( "\\", "\\\\", StringComparison.Ordinal ).Replace( "\"", "\\\"", StringComparison.Ordinal );

	/// <summary>
	/// CORDON SEAM (hammer-compile-cordon-region, sequenced after this ticket):
	/// that worker filters the input here — Cordon struct + ApplyCordon
	/// producing a sealed sub-document for the compile. This ticket invents no
	/// cordon type and changes nothing here; the runner validates and compiles
	/// whatever PrepareInput returns.
	/// </summary>
	CompileInput PrepareInput( CompileInput input ) => input;

	/// <summary>
	/// Finish a run's log: copy the plain lines and structured entries onto
	/// the result (one structured entry per issue, carrying its go-to
	/// reference, appended after the stage lines in issue order), then write
	/// the log file when one is configured. Shared by full runs and
	/// Only-Entities runs, which keep their own line/entry lists.
	/// </summary>
	static void FinishLog( CompileResult result, List<string> log, List<CompileLogEntry> entries, CompileOptions options, string name )
	{
		result.Log.AddRange( log );
		result.LogEntries.AddRange( entries );
		foreach ( var issue in result.Issues )
		{
			// The stage an issue came from, by code: entity issues belong to
			// the EntityLump stage, pack-registry and cubemap-probe codes to
			// their stages, a stage failure to the stage that failed, and the
			// BSP-stage codes (leak / invalid-brush / empty-map /
			// limit-exceeded / missing-player-start) to BspBuild.
			var stage = issue.Code switch
			{
				"invalid-entity" or "stale-build" => nameof( CompileStage.EntityLump ),
				"UnknownModel" or "MISSING_MODEL" or "UnknownSound" or "MISSING_SOUND" or "UnknownParticle" or "MISSING_PARTICLE"
					=> nameof( CompileStage.Pack ),
				"probe-inside-solid" or "probe-too-close-to-wall" or "probe-outside-map" or "duplicate-probe" or "invalid-probe"
					=> nameof( CompileStage.CubemapBuild ),
				"stage-failed" when result.FailedStage is not null => result.FailedStage.Value.ToString(),
				_ => nameof( CompileStage.BspBuild ),
			};
			result.LogEntries.Add( issue.ToLogEntry( stage ) );
		}
		var logPath = options?.LogPath;
		if ( string.IsNullOrWhiteSpace( logPath ) && !string.IsNullOrWhiteSpace( options?.OutputFolder ) )
			logPath = Path.Combine( options.OutputFolder, name + ".compile.log" );
		if ( string.IsNullOrWhiteSpace( logPath ) ) return;
		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( logPath ) ) ?? "." );
			File.WriteAllText( logPath, string.Join( "\n", log ) + "\n" );
			result.LogPath = logPath;
		}
		catch
		{
			// The log is a diagnostic, never a reason to fail the compile.
		}
	}

	// -- Stage 1: BSP build + leak check -------------------------------------

	CompileBspResult BuildBspStage( CompileInput input, CompileResult result,
		CancellationToken cancel, Action<string, string> log, out string error )
	{
		error = null;
		var issues = new List<CompileIssue>();

		if ( input.Brushes is null || input.Brushes.Count == 0 )
		{
			issues.Add( new CompileIssue( "empty-map", CompileSeverity.Error,
				"Map has no brushes: there is nothing to build.", "map", Vec3.Zero,
				"Add at least one brush." ) );
			error = "Map has no brushes.";
			result.Issues.AddRange( issues );
			return null;
		}

		if ( input.Brushes.Count > _options.MaxBrushes )
		{
			issues.Add( new CompileIssue( "limit-exceeded", CompileSeverity.Error,
				$"Map has {input.Brushes.Count} brushes, over the limit of {_options.MaxBrushes}.", "map", Vec3.Zero,
				$"Reduce the brush count below {_options.MaxBrushes} or raise CompileOptions.MaxBrushes." ) );
			error = $"Brush count {input.Brushes.Count} exceeds the limit {_options.MaxBrushes}.";
			result.Issues.AddRange( issues );
			return null;
		}

		// Validate every brush (named defects, never a silent fix) and build
		// its face polygons. Detail brushes validate the same way. The
		// per-brush work (Validate + BuildBrushPolygons) is a pure function of
		// one brush, so the parallel path computes it on the worker pool and
		// merges in brush order below — same issues, same polygon order as the
		// sequential path.
		var polygons = new List<CompilePolygon>();
		if ( DegreeOfParallelism <= 1 )
		{
			foreach ( var brush in input.Brushes )
			{
				cancel.ThrowIfCancellationRequested();
				if ( brush is null )
				{
					issues.Add( new CompileIssue( "invalid-brush", CompileSeverity.Error,
						"Null brush in the map.", "map", Vec3.Zero, "Remove the null brush entry." ) );
					continue;
				}
				if ( brush.Faces.Count > _options.MaxPlanesPerBrush )
					issues.Add( new CompileIssue( "invalid-brush", CompileSeverity.Error,
						$"Brush '{brush.Name}' has {brush.Faces.Count} planes, over the limit of {_options.MaxPlanesPerBrush}.", brush.Name, Vec3.Zero,
						"Simplify the brush to fewer planes.", brushId: brush.Id ) );
				var defects = brush.Validate();
				if ( defects.Count > 0 )
					issues.Add( new CompileIssue( "invalid-brush", CompileSeverity.Error,
						$"Brush '{brush.Name}' is invalid: {string.Join( ", ", defects )}.", brush.Name, Vec3.Zero,
						"Rebuild the brush from valid planes (convex, non-zero volume) or delete it.", brushId: brush.Id ) );
				else
					polygons.AddRange( BuildBrushPolygons( brush, out _ ) );
			}
		}
		else
		{
			var perBrush = ParallelStageRunner.RunParallel(
				input.Brushes,
				( brush, token ) => brush is null
					? null
					: new PerBrushBuild( brush.Validate(), BuildBrushPolygons( brush, out _ ) ),
				DegreeOfParallelism, cancel );
			for ( var bi = 0; bi < input.Brushes.Count; bi++ )
			{
				var brush = input.Brushes[bi];
				if ( brush is null )
				{
					issues.Add( new CompileIssue( "invalid-brush", CompileSeverity.Error,
						"Null brush in the map.", "map", Vec3.Zero, "Remove the null brush entry." ) );
					continue;
				}
				if ( brush.Faces.Count > _options.MaxPlanesPerBrush )
					issues.Add( new CompileIssue( "invalid-brush", CompileSeverity.Error,
						$"Brush '{brush.Name}' has {brush.Faces.Count} planes, over the limit of {_options.MaxPlanesPerBrush}.", brush.Name, Vec3.Zero,
						"Simplify the brush to fewer planes.", brushId: brush.Id ) );
				var built = perBrush[bi];
				if ( built.Defects.Count > 0 )
					issues.Add( new CompileIssue( "invalid-brush", CompileSeverity.Error,
						$"Brush '{brush.Name}' is invalid: {string.Join( ", ", built.Defects )}.", brush.Name, Vec3.Zero,
						"Rebuild the brush from valid planes (convex, non-zero volume) or delete it.", brushId: brush.Id ) );
				else
					polygons.AddRange( built.Polygons );
			}
		}

		// Entities are validated on every full run too: an invalid entity
		// must fail here, not surface later as a lump that Only-Entities
		// then refuses to build.
		issues.AddRange( ValidateEntities( input.Entities ) );

		if ( issues.Any( i => i.Severity == CompileSeverity.Error ) )
		{
			error = issues.First( i => i.Severity == CompileSeverity.Error ).Message;
			result.Issues.AddRange( issues );
			return null;
		}

		// BSP solid = non-detail brushes only (func_detail excluded, BSP ticket).
		var solidPolygons = polygons.Where( p => !p.IsDetail ).ToList();
		var bsp = BuildBsp( solidPolygons, input.Brushes, cancel );
		if ( bsp is null )
		{
			error = "BSP build produced no leaves.";
			result.Issues.Add( new CompileIssue( "stage-failed", CompileSeverity.Error, error, "map", Vec3.Zero,
				"Check the map geometry and recompile." ) );
			return null;
		}
		log( nameof( CompileStage.BspBuild ), $"Tree built: {bsp.LeafCount} leaves, {bsp.PortalCount} portals." );

		// Leak check: flood-fill from the player start through the BSP leaves
		// via the LANDED LeakFinder API. A leak fails the BSP stage.
		if ( input.PlayerStart is null )
		{
			issues.Add( new CompileIssue( "missing-player-start", CompileSeverity.Warning,
				"Map has no player start, so the leak check was skipped.", "map", Vec3.Zero,
				"Place an info_player_start entity inside the map so the leak check has an origin." ) );
			log( nameof( CompileStage.BspBuild ), "No player start: leak check skipped (warning)." );
		}
		else
		{
			var graph = ToLeakGraph( bsp );
			LeakFinder.LeakResult leak;
			try { leak = LeakFinder.FindLeak( graph, input.PlayerStart.Value ); }
			catch ( ArgumentException ex )
			{
				issues.Add( new CompileIssue( "missing-player-start", CompileSeverity.Error,
					$"Player start {input.PlayerStart.Value} is not inside any empty leaf: {ex.Message}", "info_player_start", input.PlayerStart.Value,
					"Move the player start inside the map, clear of solid brushes." ) );
				error = "Player start is not inside any empty leaf.";
				result.Issues.AddRange( issues );
				return null;
			}
			result.Leak = leak;
			if ( leak.IsLeaked )
			{
				var gap = leak.GapLocation;
				issues.Add( new CompileIssue( "leak", CompileSeverity.Error,
					$"Map leaks: the void is reachable from the player start through a gap at {gap} (leaf {leak.GapLeafId}, portal {leak.GapPortalId}).",
					"map", gap,
					"Seal the gap at the reported location: add or extend a brush so no void is reachable from the player start.",
					leafId: leak.GapLeafId ) );
				error = $"Map leaks through a gap at {gap}.";
				result.Issues.AddRange( issues );
				WriteLeakPointFile( input, leak, result );
				return null;
			}
			log( nameof( CompileStage.BspBuild ), "Leak check passed: sealed." );
		}

		result.Issues.AddRange( issues );
		return bsp;
	}

	void WriteLeakPointFile( CompileInput input, LeakFinder.LeakResult leak, CompileResult result )
	{
		if ( string.IsNullOrWhiteSpace( _options.OutputFolder ) ) return;
		try
		{
			Directory.CreateDirectory( _options.OutputFolder );
			var path = Path.Combine( _options.OutputFolder, input.Name + ".lin" );
			LeakFinder.WritePointFile( path, leak );
			result.OutputFiles.Add( path );
			// The result object is filled in by the caller after this returns;
			// record the file through the log so it is never lost.
			var now = DateTime.UtcNow;
			_log.Add( string.Format( CultureInfo.InvariantCulture, "[{0:HH:mm:ss.fff}] [{1}] Leak point file written: {2}",
				now, nameof( CompileStage.BspBuild ), path ) );
			_entries.Add( new CompileLogEntry
			{
				Timestamp = now,
				Stage = nameof( CompileStage.BspBuild ),
				Severity = CompileSeverity.Info,
				Code = "stage",
				Message = $"Leak point file written: {path}",
			} );
		}
		catch
		{
			// A failed point-file write must not mask the leak itself.
		}
	}

	/// <summary>Adapt the BSP leaves/portals to the LANDED LeakFinder graph shape.</summary>
	static LeakFinder.LeakGraph ToLeakGraph( CompileBspResult bsp )
	{
		var graph = new LeakFinder.LeakGraph();
		foreach ( var leaf in bsp.Leaves )
			graph.AddLeaf( new LeakFinder.LeakLeaf( leaf.Id, leaf.Center, leaf.Min, leaf.Max, leaf.IsVoid, leaf.IsSolid ) );
		foreach ( var portal in bsp.Portals )
			graph.AddPortal( new LeakFinder.LeakPortal( portal.Id, portal.LeafA, portal.LeafB, portal.Center, isOpen: true ) );
		return graph;
	}

	// -- Stage 2: visibility (PVS) -------------------------------------------

	/// <summary>
	/// PVS over the BSP portals: two interior leaves see each other when an
	/// open portal chain joins them. Fast = direct portal neighbours only
	/// (1 hop); Full = transitive flood-fill closure. Void and solid leaves
	/// are never listed as visible; the flood itself may pass through the void
	/// only in a leaked map, which never reaches this stage.
	/// </summary>
	static CompilePvsResult ComputePvs( CompileBspResult bsp, VisibilityQuality quality )
	{
		var adjacency = BuildAdjacency( bsp );
		var byId = bsp.Leaves.ToDictionary( l => l.Id );
		var sets = new Dictionary<int, List<int>>();
		foreach ( var leaf in bsp.Leaves.OrderBy( l => l.Id ) )
		{
			if ( leaf.IsSolid || leaf.IsVoid ) continue;
			sets[leaf.Id] = VisibleSetForLeaf( leaf, adjacency, byId, quality );
		}
		return new CompilePvsResult( sets, quality );
	}

	/// <summary>
	/// Parallel PVS (hammer-compile-parallel-stages): the per-leaf visible
	/// set is a pure function of one leaf + the read-only adjacency, so the
	/// leaves run on the worker pool and merge in leaf-id order — same sets,
	/// same bytes as <see cref="ComputePvs"/>. The call site in Run keeps the
	/// sequential path for single-threaded runs.
	/// </summary>
	static CompilePvsResult ComputePvsParallel( CompileBspResult bsp, VisibilityQuality quality,
		int maxDegreeOfParallelism, CancellationToken cancel )
	{
		var adjacency = BuildAdjacency( bsp );
		var byId = bsp.Leaves.ToDictionary( l => l.Id );
		var interior = bsp.Leaves.OrderBy( l => l.Id ).Where( l => !l.IsSolid && !l.IsVoid ).ToList();
		var sets = ParallelStageRunner.RunParallel(
			interior,
			( leaf, token ) => VisibleSetForLeaf( leaf, adjacency, byId, quality ),
			maxDegreeOfParallelism, cancel );
		var dict = new Dictionary<int, List<int>>();
		for ( var i = 0; i < interior.Count; i++ )
			dict[interior[i].Id] = sets[i];
		return new CompilePvsResult( dict, quality );
	}

	/// <summary>Leaf adjacency through portals, both directions, one entry per portal.</summary>
	static Dictionary<int, List<int>> BuildAdjacency( CompileBspResult bsp )
	{
		var adjacency = new Dictionary<int, List<int>>();
		foreach ( var portal in bsp.Portals )
		{
			if ( !adjacency.TryGetValue( portal.LeafA, out var a ) ) adjacency[portal.LeafA] = a = new List<int>();
			if ( !adjacency.TryGetValue( portal.LeafB, out var b ) ) adjacency[portal.LeafB] = b = new List<int>();
			if ( !a.Contains( portal.LeafB ) ) a.Add( portal.LeafB );
			if ( !b.Contains( portal.LeafA ) ) b.Add( portal.LeafA );
		}
		return adjacency;
	}

	/// <summary>Visible interior leaf ids for one leaf (self + neighbours), sorted. Shared by the sequential and parallel PVS paths so they cannot drift apart.</summary>
	static List<int> VisibleSetForLeaf( CompileLeaf leaf, Dictionary<int, List<int>> adjacency,
		Dictionary<int, CompileLeaf> byId, VisibilityQuality quality )
	{
		var visible = new HashSet<int> { leaf.Id };
		if ( quality == VisibilityQuality.Fast )
		{
			if ( adjacency.TryGetValue( leaf.Id, out var neighbours ) )
				foreach ( var n in neighbours )
					if ( byId.TryGetValue( n, out var nl ) && !nl.IsSolid && !nl.IsVoid )
						visible.Add( n );
		}
		else
		{
			var queue = new Queue<int>();
			queue.Enqueue( leaf.Id );
			while ( queue.Count > 0 )
			{
				var current = queue.Dequeue();
				if ( !adjacency.TryGetValue( current, out var neighbours ) ) continue;
				foreach ( var n in neighbours.OrderBy( x => x ) )
				{
					if ( !byId.TryGetValue( n, out var nl ) || nl.IsSolid || nl.IsVoid ) continue;
					if ( visible.Add( n ) ) queue.Enqueue( n );
				}
			}
		}
		var list = visible.ToList();
		list.Sort();
		return list;
	}

	// -- Stage 3: lightmap bake ----------------------------------------------

	/// <summary>
	/// Native per-face direct-light bake. Each brush face (detail included:
	/// it is visible geometry) gets a texel grid; a texel's light is the sum
	/// over lights of Lambert * distance falloff, zero when a shadow ray from
	/// the texel to the light hits any brush solid. Deterministic.
	/// </summary>
	CompileLightmapSet BakeLightmaps( CompileInput input, int resolution,
		IProgress<CompileProgress> progress, CancellationToken cancel, Action<string, string> log )
	{
		var faces = new List<CompileLightmapFace>();
		var polygons = new List<CompilePolygon>();
		foreach ( var brush in input.Brushes )
		{
			if ( brush is null ) continue;
			polygons.AddRange( BuildBrushPolygons( brush, out _ ) );
		}
		// Shadow rays test every brush solid (detail included — it blocks light).
		var solids = input.Brushes.Where( b => b is not null ).ToList();
		var total = polygons.Count;
		if ( DegreeOfParallelism <= 1 )
		{
			for ( var i = 0; i < polygons.Count; i++ )
			{
				cancel.ThrowIfCancellationRequested();
				var polygon = polygons[i];
				faces.Add( BakeFace( polygon, resolution, input.Lights, solids ) );
				if ( ( i + 1 ) % 16 == 0 || i + 1 == total )
				{
					var percent = 57f + 38f * ( i + 1 ) / Math.Max( 1, total );
					progress?.Report( new CompileProgress
					{
						Phase = CompilePhase.LightmapBake, Percent = percent,
						Stage = nameof( CompileStage.LightmapBake ),
						Message = $"Baking lightmaps ({i + 1}/{total} faces).",
					} );
				}
			}
		}
		else
		{
			// Per-face bake is a pure function of one face polygon, so the
			// faces run on the worker pool and merge in polygon order — same
			// faces, same order, same bytes as the sequential path. Progress
			// is aggregated from the runner's monotonic completion counts
			// (throttled to every 16 faces, as above) and reported under
			// _progressLock, so concurrent workers never invoke the caller's
			// IProgress concurrently and percents never go backwards.
			IProgress<int> completed = new Progress<int>( count =>
			{
				if ( count % 16 != 0 && count != total ) return;
				var percent = 57f + 38f * count / Math.Max( 1, total );
				lock ( _progressLock )
					progress?.Report( new CompileProgress
					{
						Phase = CompilePhase.LightmapBake, Percent = percent,
						Stage = nameof( CompileStage.LightmapBake ),
						Message = $"Baking lightmaps ({count}/{total} faces).",
					} );
			} );
			var baked = ParallelStageRunner.RunParallel(
				polygons,
				( polygon, token ) => BakeFace( polygon, resolution, input.Lights, solids ),
				DegreeOfParallelism, cancel, completed );
			faces.AddRange( baked );
		}
		log( nameof( CompileStage.LightmapBake ), $"Baked {faces.Count} faces." );
		return new CompileLightmapSet( faces, resolution );
	}

	static CompileLightmapFace BakeFace( CompilePolygon polygon, int resolution,
		IReadOnlyList<CompileLight> lights, IReadOnlyList<CompileBrush> solids )
	{
		var rgb = new float[resolution * resolution * 3];
		var normal = polygon.Normal;
		var up = Math.Abs( normal.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 0, 1, 0 );
		var u = Vec3.Cross( normal, up ).Normal;
		var v = Vec3.Cross( normal, u );
		var origin = polygon.Vertices[0];
		var uv = polygon.Vertices.Select( p => (U: Vec3.Dot( p - origin, u ), V: Vec3.Dot( p - origin, v )) ).ToList();
		var minU = uv.Min( p => p.U );
		var maxU = uv.Max( p => p.U );
		var minV = uv.Min( p => p.V );
		var maxV = uv.Max( p => p.V );
		var sampled = 0;
		var sum = 0f;

		for ( var iy = 0; iy < resolution; iy++ )
		{
			for ( var ix = 0; ix < resolution; ix++ )
			{
				var su = minU + ( ix + 0.5f ) * ( maxU - minU ) / resolution;
				var sv = minV + ( iy + 0.5f ) * ( maxV - minV ) / resolution;
				var idx = ( iy * resolution + ix ) * 3;
				if ( !PointInPolygon2D( su, sv, uv ) ) continue;
				var point = origin + u * su + v * sv;
				var light = DirectLight( point, normal, lights, solids );
				rgb[idx] = Math.Min( 1f, light.x );
				rgb[idx + 1] = Math.Min( 1f, light.y );
				rgb[idx + 2] = Math.Min( 1f, light.z );
				sum += ( rgb[idx] + rgb[idx + 1] + rgb[idx + 2] ) / 3f;
				sampled++;
			}
		}
		return new CompileLightmapFace( polygon.BrushId, polygon.FaceIndex, polygon.Material, resolution, rgb,
			sampled == 0 ? 0f : sum / sampled, sampled );
	}

	static Vec3 DirectLight( Vec3 point, Vec3 normal, IReadOnlyList<CompileLight> lights, IReadOnlyList<CompileBrush> solids )
	{
		var total = Vec3.Zero;
		if ( lights is null ) return total;
		foreach ( var light in lights )
		{
			if ( light is null || light.Intensity <= 0f ) continue;
			var toLight = light.Position - point;
			var dist = toLight.Length;
			if ( dist < 1e-6f ) continue;
			if ( light.Range > 0f && dist > light.Range ) continue;
			var dir = toLight / dist;
			var facing = Vec3.Dot( normal, dir );
			if ( facing <= 0f ) continue;
			var origin = point + normal * PortalOffset;
			if ( Shadowed( origin, dir, dist, solids ) ) continue;
			var atten = light.Intensity / ( 1f + ( dist / 128f ) * ( dist / 128f ) );
			total += new Vec3( light.Color.x * atten * facing, light.Color.y * atten * facing, light.Color.z * atten * facing );
		}
		return total;
	}

	/// <summary>True when the segment origin -> origin + dir * maxDist enters any brush solid.</summary>
	static bool Shadowed( Vec3 origin, Vec3 dir, float maxDist, IReadOnlyList<CompileBrush> solids )
	{
		foreach ( var brush in solids )
		{
			var tMin = 0f;
			var tMax = maxDist;
			var hit = true;
			foreach ( var face in brush.Faces )
			{
				var dn = Vec3.Dot( face.Normal, dir );
				var dist = face.Distance - Vec3.Dot( face.Normal, origin );
				if ( Math.Abs( dn ) < 1e-9f )
				{
					// Ray parallel to the plane: outside the half-space = miss.
					if ( dist < 0f ) { hit = false; break; }
					continue;
				}
				var t = dist / dn;
				if ( dn < 0f )
				{
					if ( t > tMin ) tMin = t;
				}
				else
				{
					if ( t < tMax ) tMax = t;
				}
				if ( tMin > tMax ) { hit = false; break; }
			}
			if ( hit && tMax > 1e-3f && tMin < maxDist ) return true;
		}
		return false;
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

	// -- Outputs -----------------------------------------------------------------

	void WriteOutputs( CompileInput input, CompileResult result )
	{
		if ( string.IsNullOrWhiteSpace( _options.OutputFolder ) ) return;
		Directory.CreateDirectory( _options.OutputFolder );
		var written = new List<string>();

		if ( result.Bsp is not null )
		{
			var path = Path.Combine( _options.OutputFolder, input.Name + ".bsp" );
			File.WriteAllText( path, BspText( result.Bsp ) );
			written.Add( path );
		}
		if ( result.Pvs is not null )
		{
			var path = Path.Combine( _options.OutputFolder, input.Name + ".pvs" );
			File.WriteAllText( path, PvsText( result.Pvs ) );
			written.Add( path );
		}
		if ( result.Lightmaps is not null )
		{
			var path = Path.Combine( _options.OutputFolder, input.Name + ".lightmap" );
			File.WriteAllText( path, LightmapText( result.Lightmaps ) );
			written.Add( path );
		}
		if ( result.Cubemaps is not null )
		{
			var path = Path.Combine( _options.OutputFolder, input.Name + ".cubemap" );
			File.WriteAllText( path, CubemapBake.WriteText( result.Cubemaps ) );
			written.Add( path );
		}
		if ( result.Pack is not null && result.Pack.Manifest is not null )
		{
			var path = Path.Combine( _options.OutputFolder, input.Name + ".mappack" );
			File.WriteAllText( path, result.Pack.Manifest );
			result.Pack.PackagePath = path;
			written.Add( path );
		}
		if ( result.Entities is not null )
		{
			var path = Path.Combine( _options.OutputFolder, input.Name + ".entities" );
			File.WriteAllText( path, result.Entities.Text );
			written.Add( path );
		}
		result.OutputFiles.AddRange( written );
		_log.Add( string.Format( CultureInfo.InvariantCulture, "[{0:HH:mm:ss.fff}] [compile] Wrote {1} output file(s).",
			DateTime.UtcNow, written.Count ) );
	}

	static string F( float v ) => v.ToString( "0.####", CultureInfo.InvariantCulture );
	static string V( Vec3 v ) => $"{F( v.x )} {F( v.y )} {F( v.z )}";

	/// <summary>GEPPETTO-BSP 1: header, counts, one line per leaf and portal.</summary>
	static string BspText( CompileBspResult bsp )
	{
		var lines = new List<string>
		{
			"GEPPETTO-BSP 1",
			$"leaves {bsp.LeafCount}",
			$"portals {bsp.PortalCount}",
		};
		foreach ( var leaf in bsp.Leaves.OrderBy( l => l.Id ) )
			lines.Add( $"leaf {leaf.Id} {( leaf.IsSolid ? "solid" : leaf.IsVoid ? "void" : "empty" )} min {V( leaf.Min )} max {V( leaf.Max )} center {V( leaf.Center )}" );
		foreach ( var portal in bsp.Portals.OrderBy( p => p.Id ) )
			lines.Add( $"portal {portal.Id} {portal.LeafA} {portal.LeafB} center {V( portal.Center )}" );
		return string.Join( "\n", lines ) + "\n";
	}

	/// <summary>GEPPETTO-PVS 1: one line per leaf, visible leaf ids comma-separated.</summary>
	static string PvsText( CompilePvsResult pvs )
	{
		var lines = new List<string> { "GEPPETTO-PVS 1", $"quality {pvs.Quality}" };
		foreach ( var pair in pvs.Sets.OrderBy( p => p.Key ) )
			lines.Add( $"leaf {pair.Key}: {string.Join( ",", pair.Value )}" );
		return string.Join( "\n", lines ) + "\n";
	}

	/// <summary>GEPPETTO-LIGHTMAP 1: resolution, then per face a header line and Resolution rows of RGB triples.</summary>
	static string LightmapText( CompileLightmapSet set )
	{
		var lines = new List<string> { "GEPPETTO-LIGHTMAP 1", $"resolution {set.Resolution}" };
		foreach ( var face in set.Faces )
		{
			lines.Add( $"face {face.BrushId} {face.FaceIndex} {face.Material} mean {F( face.Mean )} sampled {face.SampledTexels}/{face.TotalTexels}" );
			for ( var y = 0; y < face.Resolution; y++ )
			{
				var row = new List<string>();
				for ( var x = 0; x < face.Resolution; x++ )
				{
					var idx = ( y * face.Resolution + x ) * 3;
					row.Add( $"{F( face.Rgb[idx] )} {F( face.Rgb[idx + 1] )} {F( face.Rgb[idx + 2] )}" );
				}
				lines.Add( string.Join( " ", row ) );
			}
		}
		return string.Join( "\n", lines ) + "\n";
	}

	// -- BSP build (native) ---------------------------------------------------
	// A plane-split polygon BSP, the shape the BSP ticket specifies: the tree
	// is built from brush-face planes, leaves are convex cells, portals are
	// the open fragments of splitting planes (splitting-plane polygon minus
	// every solid brush), PVS/leak run over leaf adjacency through those
	// portals. When BspBuilder lands, swap this section for BspBuilder +
	// PortalGraph via an adapter; the public API above does not change.

	/// <summary>One brush-face polygon: the face plane + its wound vertices + provenance.</summary>
	internal sealed class CompilePolygon
	{
		public Vec3 Normal;
		public float Distance;
		public string Material = "default";
		public List<Vec3> Vertices = new();
		public int BrushId;
		public int FaceIndex;
		/// <summary>True when the source brush is func_detail (excluded from the BSP solid).</summary>
		public bool IsDetail;
		public float Area;
		public Vec3 Centroid;
	}

	/// <summary>One brush's parallel build output: its validity defects + its face polygons. Null in the per-brush array when the brush itself is null.</summary>
	internal sealed class PerBrushBuild
	{
		public List<string> Defects;
		public List<CompilePolygon> Polygons;
		public PerBrushBuild( List<string> defects, List<CompilePolygon> polygons ) { Defects = defects; Polygons = polygons; }
	}

	/// <summary>A plane for clipping / splitting: Normal . p = Distance, unit normal.</summary>
	internal readonly struct ClipPlane
	{
		public readonly Vec3 Normal;
		public readonly float Distance;
		public ClipPlane( Vec3 normal, float distance ) { Normal = normal; Distance = distance; }
		public float SignedDistance( Vec3 p ) => Vec3.Dot( Normal, p ) - Distance;
	}

	internal sealed class BspNode
	{
		public bool IsLeaf;
		public ClipPlane Split;
		public BspNode Front, Back;
		public List<CompilePolygon> Coplanar = new();
		public Vec3 Min, Max;
		/// <summary>Half-spaces defining this node's region: (plane, true = front side n.p >= d).</summary>
		public List<(ClipPlane Plane, bool Front)> Constraints = new();
		public int LeafId = -1;
	}

	/// <summary>Build face polygons for a brush by plane-intersection vertex solve (the brush-core ticket's method).</summary>
	internal static List<CompilePolygon> BuildBrushPolygons( CompileBrush brush, out List<string> defects )
	{
		defects = new List<string>();
		var polygons = new List<CompilePolygon>();
		var faces = brush.Faces;
		var vertices = new List<Vec3>();

		for ( var i = 0; i < faces.Count; i++ )
			for ( var j = i + 1; j < faces.Count; j++ )
				for ( var k = j + 1; k < faces.Count; k++ )
				{
					if ( !SolvePlaneTriple(
						new ClipPlane( faces[i].Normal, faces[i].Distance ),
						new ClipPlane( faces[j].Normal, faces[j].Distance ),
						new ClipPlane( faces[k].Normal, faces[k].Distance ), out var p ) ) continue;
					var inside = true;
					foreach ( var f in faces )
						if ( Vec3.Dot( f.Normal, p ) > f.Distance + 1e-3f ) { inside = false; break; }
					if ( !inside ) continue;
					if ( vertices.Any( q => ( q - p ).LengthSquared <= 1e-6f ) ) continue;
					vertices.Add( p );
				}

		for ( var i = 0; i < faces.Count; i++ )
		{
			var face = faces[i];
			var onPlane = vertices.Where( pt => Math.Abs( Vec3.Dot( face.Normal, pt ) - face.Distance ) <= 1e-2f ).ToList();
			if ( onPlane.Count < 3 ) { defects.Add( $"degenerate-face:{i}" ); continue; }
			var centroid = Vec3.Zero;
			foreach ( var pt in onPlane ) centroid += pt;
			centroid /= onPlane.Count;
			var up = Math.Abs( face.Normal.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 0, 1, 0 );
			var u = Vec3.Cross( face.Normal, up ).Normal;
			var v = Vec3.Cross( face.Normal, u );
			onPlane.Sort( ( a, b ) =>
			{
				var aa = Math.Atan2( Vec3.Dot( a - centroid, v ), Vec3.Dot( a - centroid, u ) );
				var bb = Math.Atan2( Vec3.Dot( b - centroid, v ), Vec3.Dot( b - centroid, u ) );
				return aa.CompareTo( bb );
			} );
			// Newell normal: reverse the winding when it disagrees with the outward plane normal.
			var newell = Vec3.Zero;
			for ( var e = 0; e < onPlane.Count; e++ )
			{
				var a = onPlane[e];
				var b = onPlane[( e + 1 ) % onPlane.Count];
				newell += new Vec3(
					( a.y - b.y ) * ( a.z + b.z ),
					( a.z - b.z ) * ( a.x + b.x ),
					( a.x - b.x ) * ( a.y + b.y ) );
			}
			var area = newell.Length / 2f;
			if ( !( area > 1e-6f ) ) { defects.Add( $"degenerate-face:{i}" ); continue; }
			if ( Vec3.Dot( newell, face.Normal ) < 0f ) onPlane.Reverse();
			polygons.Add( new CompilePolygon
			{
				Normal = face.Normal,
				Distance = face.Distance,
				Material = face.Material,
				Vertices = onPlane,
				BrushId = brush.Id,
				FaceIndex = i,
				IsDetail = brush.IsDetail,
				Area = area,
				Centroid = centroid,
			} );
		}
		return polygons;
	}

	/// <summary>Closed-polyhedron volume from wound face polygons (divergence theorem). Zero or negative for a degenerate brush.</summary>
	internal static float BrushVolume( List<CompilePolygon> polygons )
	{
		double volume = 0;
		foreach ( var polygon in polygons )
		{
			var v0 = polygon.Vertices[0];
			for ( var i = 1; i + 1 < polygon.Vertices.Count; i++ )
			{
				var a = polygon.Vertices[i];
				var b = polygon.Vertices[i + 1];
				volume += Vec3.Dot( v0, Vec3.Cross( a, b ) ) / 6.0;
			}
		}
		return (float)volume;
	}

	static bool SolvePlaneTriple( ClipPlane a, ClipPlane b, ClipPlane c, out Vec3 point )
	{
		point = Vec3.Zero;
		// Cramer on rows (a.Normal | a.Distance) etc., in doubles for stability.
		var a1 = a.Normal.x; var b1 = a.Normal.y; var c1 = a.Normal.z; var d1 = a.Distance;
		var a2 = b.Normal.x; var b2 = b.Normal.y; var c2 = b.Normal.z; var d2 = b.Distance;
		var a3 = c.Normal.x; var b3 = c.Normal.y; var c3 = c.Normal.z; var d3 = c.Distance;
		var det = a1 * ( b2 * c3 - b3 * c2 ) - b1 * ( a2 * c3 - a3 * c2 ) + c1 * ( a2 * b3 - a3 * b2 );
		if ( Math.Abs( det ) < 1e-12 ) return false;
		var x = ( d1 * ( b2 * c3 - b3 * c2 ) - b1 * ( d2 * c3 - d3 * c2 ) + c1 * ( d2 * b3 - d3 * b2 ) ) / det;
		var y = ( a1 * ( d2 * c3 - d3 * c2 ) - d1 * ( a2 * c3 - a3 * c2 ) + c1 * ( a2 * d3 - a3 * d2 ) ) / det;
		var z = ( a1 * ( b2 * d3 - b3 * d2 ) - b1 * ( a2 * d3 - a3 * d2 ) + d1 * ( a2 * b3 - a3 * b2 ) ) / det;
		point = new Vec3( (float)x, (float)y, (float)z );
		return true;
	}

	CompileBspResult BuildBsp( List<CompilePolygon> solidPolygons, List<CompileBrush> brushes, CancellationToken cancel )
	{
		// World bounds from the solid geometry, expanded: the outer box makes
		// every leaf finite, and its faces are the void boundary.
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var polygon in solidPolygons )
			foreach ( var v in polygon.Vertices )
			{
				min = new Vec3( Math.Min( min.x, v.x ), Math.Min( min.y, v.y ), Math.Min( min.z, v.z ) );
				max = new Vec3( Math.Max( max.x, v.x ), Math.Max( max.y, v.y ), Math.Max( max.z, v.z ) );
			}
		if ( solidPolygons.Count == 0 )
		{
			min = new Vec3( -512, -512, -512 );
			max = new Vec3( 512, 512, 512 );
		}
		var outerMin = min - new Vec3( OuterMargin, OuterMargin, OuterMargin );
		var outerMax = max + new Vec3( OuterMargin, OuterMargin, OuterMargin );

		var outerBrush = CompileBrush.Box( "__outer__", -1, outerMin, outerMax );
		var outerPolygons = BuildBrushPolygons( outerBrush, out _ );
		var all = new List<CompilePolygon>( solidPolygons );
		all.AddRange( outerPolygons );

		var outerConstraints = new List<(ClipPlane, bool)>
		{
			( new ClipPlane( new Vec3( 1, 0, 0 ), outerMax.x ), false ),
			( new ClipPlane( new Vec3( -1, 0, 0 ), -outerMin.x ), false ),
			( new ClipPlane( new Vec3( 0, 1, 0 ), outerMax.y ), false ),
			( new ClipPlane( new Vec3( 0, -1, 0 ), -outerMin.y ), false ),
			( new ClipPlane( new Vec3( 0, 0, 1 ), outerMax.z ), false ),
			( new ClipPlane( new Vec3( 0, 0, -1 ), -outerMin.z ), false ),
		};

		var root = BuildNode( all, outerMin, outerMax, outerConstraints, 0, cancel );
		if ( root is null ) return null;

		// Leaves in deterministic (front-first) order.
		var leafNodes = new List<BspNode>();
		CollectLeaves( root, leafNodes );
		var solidBrushes = brushes.Where( b => b is not null && !b.IsDetail ).ToList();
		var leaves = new List<CompileLeaf>();
		for ( var i = 0; i < leafNodes.Count; i++ )
		{
			var node = leafNodes[i];
			node.LeafId = i;
			var region = RegionVertices( node.Constraints );
			Vec3 center, leafMin, leafMax;
			if ( region.Count > 0 )
			{
				center = Vec3.Zero;
				leafMin = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
				leafMax = new Vec3( float.MinValue, float.MinValue, float.MinValue );
				foreach ( var v in region )
				{
					center += v;
					leafMin = new Vec3( Math.Min( leafMin.x, v.x ), Math.Min( leafMin.y, v.y ), Math.Min( leafMin.z, v.z ) );
					leafMax = new Vec3( Math.Max( leafMax.x, v.x ), Math.Max( leafMax.y, v.y ), Math.Max( leafMax.z, v.z ) );
				}
				center /= region.Count;
			}
			else
			{
				center = ( node.Min + node.Max ) / 2f;
				leafMin = node.Min;
				leafMax = node.Max;
			}
			var isSolid = solidBrushes.Any( b => PointInBrush( center, b ) );
			var isVoid = !isSolid &&
				( leafMin.x <= outerMin.x + 0.05f || leafMin.y <= outerMin.y + 0.05f || leafMin.z <= outerMin.z + 0.05f ||
				  leafMax.x >= outerMax.x - 0.05f || leafMax.y >= outerMax.y - 0.05f || leafMax.z >= outerMax.z - 0.05f );
			leaves.Add( new CompileLeaf( i, center, leafMin, leafMax, isSolid, isVoid ) );
		}

		var portals = BuildPortals( root, leaves, solidBrushes, cancel );
		return new CompileBspResult( leaves, portals );
	}

	static bool PointInBrush( Vec3 p, CompileBrush brush )
	{
		foreach ( var face in brush.Faces )
			if ( Vec3.Dot( face.Normal, p ) > face.Distance + 1e-3f ) return false;
		return true;
	}

	static void CollectLeaves( BspNode node, List<BspNode> leaves )
	{
		if ( node.IsLeaf ) { leaves.Add( node ); return; }
		if ( node.Front is not null ) CollectLeaves( node.Front, leaves );
		if ( node.Back is not null ) CollectLeaves( node.Back, leaves );
	}

	/// <summary>Vertices of the convex region a leaf's half-space constraints define (all plane triples, kept when inside every constraint).</summary>
	static List<Vec3> RegionVertices( List<(ClipPlane Plane, bool Front)> constraints )
	{
		var points = new List<Vec3>();
		for ( var i = 0; i < constraints.Count; i++ )
			for ( var j = i + 1; j < constraints.Count; j++ )
				for ( var k = j + 1; k < constraints.Count; k++ )
				{
					if ( !SolvePlaneTriple( constraints[i].Plane, constraints[j].Plane, constraints[k].Plane, out var p ) ) continue;
					var inside = true;
					foreach ( var (plane, front) in constraints )
					{
						var d = plane.SignedDistance( p );
						if ( front ? d < -EpsPoint : d > EpsPoint ) { inside = false; break; }
					}
					if ( !inside ) continue;
					if ( points.Any( q => ( q - p ).LengthSquared <= 0.0025f ) ) continue;
					points.Add( p );
				}
		return points;
	}

	/// <summary>Best-balanced splitter plane for a node (see BuildNode). Classifies by min/max signed distance, no allocation.</summary>
	static ClipPlane ChooseSplitter( List<CompilePolygon> polygons )
	{
		var bestPlane = new ClipPlane( polygons[0].Normal, polygons[0].Distance );
		var bestHeavier = int.MaxValue;
		var bestSplits = int.MaxValue;
		var bestCoplanar = -1;
		for ( var i = 0; i < polygons.Count; i++ )
		{
			var plane = new ClipPlane( polygons[i].Normal, polygons[i].Distance );
			var front = 0;
			var back = 0;
			var splits = 0;
			var coplanar = 0;
			foreach ( var polygon in polygons )
			{
				var lo = float.MaxValue;
				var hi = float.MinValue;
				foreach ( var v in polygon.Vertices )
				{
					var d = plane.SignedDistance( v );
					if ( d < lo ) lo = d;
					if ( d > hi ) hi = d;
				}
				if ( lo >= -EpsPlane && hi <= EpsPlane ) coplanar++;
				else if ( lo >= -EpsPlane ) front++;
				else if ( hi <= EpsPlane ) back++;
				else splits++;
			}
			var heavier = Math.Max( front, back );
			if ( heavier < bestHeavier
				|| ( heavier == bestHeavier && splits < bestSplits )
				|| ( heavier == bestHeavier && splits == bestSplits && coplanar > bestCoplanar ) )
			{
				bestPlane = plane;
				bestHeavier = heavier;
				bestSplits = splits;
				bestCoplanar = coplanar;
			}
		}
		return bestPlane;
	}

	BspNode BuildNode( List<CompilePolygon> polygons, Vec3 min, Vec3 max,
		List<(ClipPlane Plane, bool Front)> constraints, int depth, CancellationToken cancel )
	{
		cancel.ThrowIfCancellationRequested();
		var node = new BspNode { Min = min, Max = max, Constraints = constraints };
		if ( polygons.Count == 0 || depth >= MaxBspDepth )
		{
			node.IsLeaf = true;
			return node;
		}

		// Choose the splitter: the plane that best balances the split (fewest
		// polygons on the heavier side, then fewest splits, then most
		// coplanar). polygons[0] as a splitter makes a degenerate comb — the
		// tree depth explodes past MaxBspDepth before the room's own faces
		// ever split it, so leaves swallow whole walls and the gap/doorway
		// portals never form. Deterministic: lowest polygon index wins ties.
		var splitter = ChooseSplitter( polygons );
		var front = new List<CompilePolygon>();
		var back = new List<CompilePolygon>();
		foreach ( var polygon in polygons )
		{
			var (f, b, coplanar) = SplitPolygon( polygon, splitter );
			if ( coplanar ) { node.Coplanar.Add( polygon ); continue; }
			if ( f is not null ) front.Add( f );
			if ( b is not null ) back.Add( b );
		}

		node.Split = splitter;
		var frontBounds = ClipAabbBounds( min, max, splitter, front: true );
		var backBounds = ClipAabbBounds( min, max, splitter, front: false );
		var frontConstraints = new List<(ClipPlane, bool)>( constraints ) { ( splitter, true ) };
		var backConstraints = new List<(ClipPlane, bool)>( constraints ) { ( splitter, false ) };
		if ( front.Count == 0 && back.Count == 0 )
		{
			// All polygons lie ON the splitter plane: the plane is still a
			// real solid/empty boundary through this region (e.g. a ceiling
			// face over the room). Splitting anyway separates the solid side
			// from the empty side; both children are leaves bounded by the
			// plane. Making this a leaf instead would swallow the solid into
			// the empty leaf (the room leaf's region would extend through
			// the ceiling, and no leak/PVS portal would ever form there).
			node.Front = new BspNode { IsLeaf = true, Min = frontBounds.Min, Max = frontBounds.Max, Constraints = frontConstraints };
			node.Back = new BspNode { IsLeaf = true, Min = backBounds.Min, Max = backBounds.Max, Constraints = backConstraints };
			return node;
		}

		node.Front = BuildNode( front, frontBounds.Min, frontBounds.Max, frontConstraints, depth + 1, cancel );
		node.Back = BuildNode( back, backBounds.Min, backBounds.Max, backConstraints, depth + 1, cancel );
		return node;
	}

	/// <summary>AABB of a box clipped to one side of a plane (corner + edge-intersection method; falls back to the input box when the clip is empty).</summary>
	static (Vec3 Min, Vec3 Max) ClipAabbBounds( Vec3 min, Vec3 max, ClipPlane plane, bool front )
	{
		var points = new List<Vec3>();
		var corners = new[]
		{
			new Vec3( min.x, min.y, min.z ), new Vec3( max.x, min.y, min.z ),
			new Vec3( min.x, max.y, min.z ), new Vec3( max.x, max.y, min.z ),
			new Vec3( min.x, min.y, max.z ), new Vec3( max.x, min.y, max.z ),
			new Vec3( min.x, max.y, max.z ), new Vec3( max.x, max.y, max.z ),
		};
		int[,] edges = { { 0, 1 }, { 0, 2 }, { 0, 4 }, { 1, 3 }, { 1, 5 }, { 2, 3 }, { 2, 6 }, { 3, 7 }, { 4, 5 }, { 4, 6 }, { 5, 7 }, { 6, 7 } };
		bool Keeps( Vec3 p ) => front ? plane.SignedDistance( p ) >= -EpsPlane : plane.SignedDistance( p ) <= EpsPlane;
		foreach ( var c in corners )
			if ( Keeps( c ) ) points.Add( c );
		for ( var e = 0; e < edges.GetLength( 0 ); e++ )
		{
			var a = corners[edges[e, 0]];
			var b = corners[edges[e, 1]];
			var da = plane.SignedDistance( a );
			var db = plane.SignedDistance( b );
			if ( ( da < 0 ) == ( db < 0 ) || Math.Abs( da - db ) < 1e-9f ) continue;
			var t = da / ( da - db );
			points.Add( a + ( b - a ) * t );
		}
		if ( points.Count == 0 ) return (min, max);
		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var p in points )
		{
			lo = new Vec3( Math.Min( lo.x, p.x ), Math.Min( lo.y, p.y ), Math.Min( lo.z, p.z ) );
			hi = new Vec3( Math.Max( hi.x, p.x ), Math.Max( hi.y, p.y ), Math.Max( hi.z, p.z ) );
		}
		return (lo, hi);
	}

	// -- Polygon splitting / clipping -----------------------------------------

	/// <summary>
	/// Split a polygon by a plane: (front, back, coplanar). Vertices on the
	/// plane go to both sides. Front = n.p &gt; d, back = n.p &lt;= d.
	/// </summary>
	static (CompilePolygon Front, CompilePolygon Back, bool Coplanar) SplitPolygon( CompilePolygon polygon, ClipPlane plane )
	{
		var frontVerts = new List<Vec3>();
		var backVerts = new List<Vec3>();
		var allOn = true;
		var anyFront = false;
		var anyBack = false;
		var verts = polygon.Vertices;
		for ( var i = 0; i < verts.Count; i++ )
		{
			var a = verts[i];
			var b = verts[( i + 1 ) % verts.Count];
			var da = plane.SignedDistance( a );
			var db = plane.SignedDistance( b );
			if ( Math.Abs( da ) > EpsPlane ) allOn = false;
			if ( da > EpsPlane ) anyFront = true;
			if ( da < -EpsPlane ) anyBack = true;

			if ( da >= -EpsPlane ) frontVerts.Add( a );
			if ( da <= EpsPlane ) backVerts.Add( a );
			if ( ( da > EpsPlane && db < -EpsPlane ) || ( da < -EpsPlane && db > EpsPlane ) )
			{
				var t = da / ( da - db );
				var hit = a + ( b - a ) * t;
				frontVerts.Add( hit );
				backVerts.Add( hit );
			}
		}
		if ( allOn || ( !anyFront && !anyBack ) ) return (null, null, true);
		CompilePolygon Make( List<Vec3> list )
		{
			if ( list.Count < 3 ) return null;
			// Drop consecutive duplicates the split can produce.
			var clean = new List<Vec3>();
			foreach ( var v in list )
				if ( clean.Count == 0 || ( clean[^1] - v ).LengthSquared > 1e-8f ) clean.Add( v );
			if ( clean.Count > 1 && ( clean[0] - clean[^1] ).LengthSquared <= 1e-8f ) clean.RemoveAt( clean.Count - 1 );
			if ( clean.Count < 3 ) return null;
			return new CompilePolygon
			{
				Normal = polygon.Normal,
				Distance = polygon.Distance,
				Material = polygon.Material,
				Vertices = clean,
				BrushId = polygon.BrushId,
				FaceIndex = polygon.FaceIndex,
				IsDetail = polygon.IsDetail,
				Area = PolygonArea( clean ),
				Centroid = PolygonCentroid( clean ),
			};
		}
		return (Make( frontVerts ), Make( backVerts ), false);
	}

	/// <summary>Clip a vertex list by a plane, keeping one side (front: n.p &gt;= d; back: n.p &lt;= d). Null when fewer than 3 vertices survive.</summary>
	static List<Vec3> ClipVertices( List<Vec3> verts, ClipPlane plane, bool keepFront )
	{
		var kept = new List<Vec3>();
		for ( var i = 0; i < verts.Count; i++ )
		{
			var a = verts[i];
			var b = verts[( i + 1 ) % verts.Count];
			var da = plane.SignedDistance( a );
			var db = plane.SignedDistance( b );
			var aIn = keepFront ? da >= -EpsPlane : da <= EpsPlane;
			var bIn = keepFront ? db >= -EpsPlane : db <= EpsPlane;
			if ( aIn ) kept.Add( a );
			if ( aIn != bIn )
			{
				var t = da / ( da - db );
				kept.Add( a + ( b - a ) * t );
			}
		}
		return kept.Count >= 3 ? kept : null;
	}

	static float PolygonArea( List<Vec3> verts )
	{
		var newell = Vec3.Zero;
		for ( var e = 0; e < verts.Count; e++ )
		{
			var a = verts[e];
			var b = verts[( e + 1 ) % verts.Count];
			newell += new Vec3(
				( a.y - b.y ) * ( a.z + b.z ),
				( a.z - b.z ) * ( a.x + b.x ),
				( a.x - b.x ) * ( a.y + b.y ) );
		}
		return newell.Length / 2f;
	}

	static Vec3 PolygonCentroid( List<Vec3> verts )
	{
		var c = Vec3.Zero;
		foreach ( var v in verts ) c += v;
		return verts.Count == 0 ? c : c / verts.Count;
	}

	// -- Portals ---------------------------------------------------------------

	/// <summary>
	/// Portals, one per open fragment of each internal node's splitting plane:
	/// the splitter polygon clipped to the node's bounds, minus every solid
	/// brush. A fragment joins the leaves on its two sides (found by point
	/// location offset either side of the plane); fragments against a solid
	/// leaf, outside the tree, or joining a leaf to itself are dropped.
	/// Deterministic: pre-order node traversal, fragments in clip order.
	/// </summary>
	List<CompilePortal> BuildPortals( BspNode root, List<CompileLeaf> leaves, List<CompileBrush> solidBrushes, CancellationToken cancel )
	{
		var portals = new List<CompilePortal>();
		var byId = leaves.ToDictionary( l => l.Id );
		var stack = new Stack<BspNode>();
		stack.Push( root );
		var order = new List<BspNode>();
		while ( stack.Count > 0 )
		{
			var node = stack.Pop();
			if ( node.IsLeaf ) continue;
			order.Add( node );
			// Pre-order: push back first so front is visited first.
			if ( node.Back is not null ) stack.Push( node.Back );
			if ( node.Front is not null ) stack.Push( node.Front );
		}

		foreach ( var node in order )
		{
			cancel.ThrowIfCancellationRequested();
			var plane = node.Split;
			var extent = Math.Max( Math.Max( node.Max.x - node.Min.x, node.Max.y - node.Min.y ), node.Max.z - node.Min.z );
			if ( !( extent > 0f ) ) continue;
			var up = Math.Abs( plane.Normal.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 0, 1, 0 );
			var u = Vec3.Cross( plane.Normal, up ).Normal;
			var v = Vec3.Cross( plane.Normal, u );
			var center = plane.Normal * plane.Distance;
			var half = extent;
			var quad = new List<Vec3>
			{
				center + u * half + v * half,
				center - u * half + v * half,
				center - u * half - v * half,
				center + u * half - v * half,
			};
			// Clip the quad to the node's AABB.
			var clipped = quad;
			foreach ( var (axis, bound, keepAbove) in new[]
			{
				(0, node.Min.x, true), (0, node.Max.x, false),
				(1, node.Min.y, true), (1, node.Max.y, false),
				(2, node.Min.z, true), (2, node.Max.z, false),
			} )
			{
				var n = axis == 0 ? new Vec3( 1, 0, 0 ) : axis == 1 ? new Vec3( 0, 1, 0 ) : new Vec3( 0, 0, 1 );
				if ( !keepAbove ) n = -n;
				var d = keepAbove ? bound : -bound;
				clipped = ClipVertices( clipped, new ClipPlane( n, d ), keepFront: true );
				if ( clipped is null ) break;
			}
			if ( clipped is null || PolygonArea( clipped ) <= 1e-4f ) continue;

			var fragments = new List<List<Vec3>> { clipped };
			foreach ( var brush in solidBrushes )
				fragments = SubtractBrushFragments( fragments, brush );
			foreach ( var fragment in fragments )
			{
				if ( PolygonArea( fragment ) <= 1e-4f ) continue;
				var centroid = PolygonCentroid( fragment );
				var leafA = LeafContainingPoint( root, centroid - plane.Normal * PortalOffset );
				var leafB = LeafContainingPoint( root, centroid + plane.Normal * PortalOffset );
				if ( leafA is null || leafB is null || leafA.LeafId == leafB.LeafId ) continue;
				var a = byId[leafA.LeafId];
				var b = byId[leafB.LeafId];
				if ( a.IsSolid || b.IsSolid ) continue;
				portals.Add( new CompilePortal( portals.Count, a.Id, b.Id, centroid ) );
			}
		}
		return portals;
	}

	/// <summary>Remove the parts of each fragment that lie inside the brush: a fragment survives when it is outside at least one brush plane.</summary>
	static List<List<Vec3>> SubtractBrushFragments( List<List<Vec3>> fragments, CompileBrush brush )
	{
		var result = new List<List<Vec3>>();
		foreach ( var fragment in fragments )
		{
			var working = new List<List<Vec3>> { fragment };
			foreach ( var face in brush.Faces )
			{
				var plane = new ClipPlane( face.Normal, face.Distance );
				var next = new List<List<Vec3>>();
				foreach ( var piece in working )
				{
					var (front, back, coplanar) = SplitPolygon(
						new CompilePolygon { Normal = plane.Normal, Distance = plane.Distance, Vertices = piece }, plane );
					if ( coplanar )
					{
						// Exactly on the brush face: not outside this brush.
						next.Add( piece );
						continue;
					}
					if ( front is not null && PolygonArea( front.Vertices ) > 1e-4f )
						result.Add( front.Vertices ); // outside this plane => outside the brush
					if ( back is not null && PolygonArea( back.Vertices ) > 1e-4f )
						next.Add( back.Vertices );
				}
				working = next;
				if ( working.Count == 0 ) break;
			}
			// Anything still working after every plane is inside the brush: dropped.
		}
		return result;
	}

	/// <summary>Leaf containing a point, by walking the BSP tree. Null when the walk leaves the tree.</summary>
	static BspNode LeafContainingPoint( BspNode root, Vec3 point )
	{
		var node = root;
		while ( node is not null && !node.IsLeaf )
		{
			var d = node.Split.SignedDistance( point );
			node = d >= -EpsPlane ? node.Front : node.Back;
		}
		return node;
	}
}
