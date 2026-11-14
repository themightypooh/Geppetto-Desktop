using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Effigy.Map;

// ============================================================================
// Hammer Desktop P2: env_cubemap probes + native 6-face capture
// (buildcubemaps equivalent). Board task hammer-cubemap-probes-bake.
//
// A probe entity (position, resolution) + a native capture: 6 faces rendered
// at each probe from the baked scene and stored as reflection data for
// props / water / shiny surfaces. Validation: a probe inside a wall is an
// error, a probe too close to a wall warns (Hammer++ warns on bad setups),
// a probe outside the map is an error.
//
// CAPTURE. The capture runs through ICubemapCapture. The implementation that
// ships here is CpuRayCapture: a CPU ray-cast capture against the compiled
// scene (CompileInput brushes + lights), using the same slab ray test and
// the same direct-light model (Lambert + 1/(1+(d/128)^2) falloff + shadow
// ray) as CompileRunner's lightmap bake. It renders no GL and needs no
// renderer: it is the reference capture, and it is what the tests exercise.
// The ticket's CHECK says capture runs on the existing windowless EGL
// headless path with no new renderer — that is the GPU seam, not this file:
// a GPU capture (6 faces rendered through the shared SceneRenderer /
// HeadlessRender path) plugs in later as a second ICubemapCapture without
// changing CubemapBake's API. The CPU capture stays as the fallback.
//
// SHADING HONESTY. The CPU capture shades a hit with the direct light at
// the hit point times one neutral albedo (CubemapBakeOptions.Albedo, 0.8).
// There is no per-material albedo source to read yet: FaceMaterial carries
// UVs, not albedo, and the material bridge (VMT $envmap) does not expose a
// CPU albedo either. When a CPU albedo source lands, only DirectLight's
// albedo factor changes; the capture shape, the file format and the tests
// do not.
//
// DEPENDENCY STATUS (2026-11-13, verified against the live tree):
//   * CompileRunner (hammer-compile-native-pipeline) HAS landed, namespace
//     Effigy: CompileInput / CompileBrush / CompileFace / CompileLight /
//     CompileIssue / CompileBspResult. This file consumes those types
//     directly; CubemapBakeScene.FromCompileInput is the adapter, and
//     ValidateProbes takes an optional CompileBspResult for the
//     authoritative inside-map check (LeafContaining).
//   * EntityRegistry (hammer-entity-registry-fgd-json) is DONE but its
//     registry JSON (kernel/Effigy/Map/registry/core-source1.json, 14
//     classes) has NO env_cubemap class, so MapEntities cannot place one
//     today. CubemapProbe.FromMapEntity is the adapter for when the
//     registry owner adds it (keyvalues: cubemapsize, int, default 32);
//     until then probes are built directly or loaded from JSON fixtures.
//     The registry JSON is NOT this ticket's file; do not add the class
//     here.
//   * CompileRunner.cs is frozen to this ticket (worker 22 still in
//     progress). The cubemap stage is NOT wired into it here; the
//     sequenced task hammer-compile-packing-cubemap-build adds it. The
//     exact seam is CUBEMAPBUILD SEAM below.
//
// CUBEMAPBUILD SEAM (for hammer-compile-packing-cubemap-build — that worker
// owns CompileRunner.cs and makes these edits; this file changes nothing):
//   1. CompileStage gains CubemapBuild (after LightmapBake) and
//      CompilePhase gains CubemapBuild (after LightmapBake).
//   2. CompileInput gains `List<CubemapProbe> Probes { get; }` (empty =
//      no cubemap stage, same "stage skipped when empty" rule as the
//      lightmap resolution gate).
//   3. CompileResult gains `CubemapBakeSet Cubemaps { get; set; }` (this
//      file's type; CompileRunner.cs will need `using Effigy.Map;`).
//   4. CompileRunner.Run, after the lightmap stage and before Writing:
//      build a CubemapProbeRegistry from input.Probes, call
//      CubemapBake.Bake(registry, CubemapBakeScene.FromCompileInput(input),
//      progress, cancel); on !Succeeded fail the run with
//      Fail(CompileStage.CubemapBuild, result.Error); on success set
//      result.Cubemaps and log "<n> probes x 6 faces".
//      Presets: QuickPlay skips it (no probes stage, like the BSP-only
//      preset skips lightmaps); Preview and Final run it.
//   5. WriteOutputs writes `<name>.cubemap` via CubemapBake.WriteText
//      when result.Cubemaps is not null (product file, success only,
//      same rule as .bsp / .pvs / .lightmap).
//
// MATERIAL SEAM. A shiny prop material references a bake through the
// string CubemapBakeSet.ResolveEnvmap(position) returns,
// "env_cubemap:<nearest-probe-name>". MaterialBridge already preserves
// `$envmap env_cubemap` through VMT (see MaterialBridgeTests); the
// integrator wires ResolveEnvmap into prop placement so a prop's $envmap
// names the probe the bake actually wrote. This file invents no material
// type and edits no material file.
//
// Units are the kernel's inches, Z-up (CONTRACTS.md). Deterministic:
// same probes + scene -> same faces, means and file bytes (no time,
// randomness or hash-order dependence in any output).
// ============================================================================

/// <summary>One of the six faces of a cubemap, in Source capture order.</summary>
public enum CubemapFace
{
	PositiveX,
	NegativeX,
	PositiveY,
	NegativeY,
	PositiveZ,
	NegativeZ,
}

/// <summary>Which phase a cubemap bake is in, for progress reporting. Mirrors the BakeJob / CompileRunner phase shape.</summary>
public enum CubemapBakePhase
{
	Preparing,
	Capturing,
	Done,
	Cancelled,
	Failed,
}

/// <summary>
/// Named error from the cubemap bake layer. <see cref="Code"/> is the stable
/// machine-readable name; validation findings instead surface as
/// <see cref="CompileIssue"/>s (codes below) so the compile stage can carry
/// them in <see cref="CompileResult.Issues"/> unchanged.
/// </summary>
public sealed class CubemapBakeException : Exception
{
	public const string CodeNotEnvCubemap = "not-env-cubemap";
	public const string CodeDuplicateProbe = "duplicate-probe";
	public const string CodeInvalidProbe = "invalid-probe";
	public const string CodeInvalidRegistry = "invalid-probe-registry";

	public string Code { get; }

	public CubemapBakeException( string code, string message )
		: base( message ) { Code = code; }
}

/// <summary>
/// One env_cubemap probe: a capture position + the resolution of each of its
/// 6 faces. Resolution is a power of two, 16..256 (Source cubemap sizes);
/// MinWallDistance is the Hammer++-style placement guide — a probe closer
/// than this to any brush surface warns (default 64 units: the report's
/// "typically 64 units from walls, above the floor").
/// </summary>
public sealed class CubemapProbe
{
	public string Name { get; set; } = "env_cubemap";
	public Vec3 Position { get; set; }
	public int Resolution { get; set; } = CubemapBake.DefaultResolution;
	public float MinWallDistance { get; set; } = CubemapBake.DefaultMinWallDistance;

	public CubemapProbe() { }

	public CubemapProbe( string name, Vec3 position, int resolution = CubemapBake.DefaultResolution, float minWallDistance = CubemapBake.DefaultMinWallDistance )
	{
		Name = name ?? "env_cubemap";
		Position = position;
		Resolution = resolution;
		MinWallDistance = minWallDistance;
	}

	/// <summary>Named validity defects, empty when valid: empty-name, non-finite-position, invalid-resolution.</summary>
	public List<string> Validate()
	{
		var defects = new List<string>();
		if ( string.IsNullOrWhiteSpace( Name ) ) defects.Add( "empty-name" );
		if ( !float.IsFinite( Position.x ) || !float.IsFinite( Position.y ) || !float.IsFinite( Position.z ) ) defects.Add( "non-finite-position" );
		if ( Resolution < CubemapBake.MinResolution || Resolution > CubemapBake.MaxResolution || ( Resolution & ( Resolution - 1 ) ) != 0 ) defects.Add( "invalid-resolution" );
		if ( !( MinWallDistance >= 0f ) || !float.IsFinite( MinWallDistance ) ) defects.Add( "invalid-min-wall-distance" );
		return defects;
	}

	/// <summary>
	/// Adapter from a placed entity. The entity must be an env_cubemap:
	/// name comes from targetname (or "env_cubemap" when unnamed),
	/// resolution from the "cubemapsize" keyvalue (default 32). The
	/// registry JSON has no env_cubemap class yet (see the file header),
	/// so this is the seam for when it lands — it is not reachable through
	/// MapEntities placement today. Wrong class / bad cubemapsize raise
	/// <see cref="CubemapBakeException"/>; null raises ArgumentNullException.
	/// </summary>
	public static CubemapProbe FromMapEntity( MapEntity entity )
	{
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		if ( !string.Equals( entity.ClassName, "env_cubemap", StringComparison.OrdinalIgnoreCase ) )
			throw new CubemapBakeException( CubemapBakeException.CodeNotEnvCubemap,
				$"Entity #{entity.Id} ('{entity.ClassName}') is not an env_cubemap." );
		var name = string.IsNullOrWhiteSpace( entity.TargetName ) ? "env_cubemap" : entity.TargetName;
		var resolution = CubemapBake.DefaultResolution;
		if ( entity.Keyvalues.TryGetValue( "cubemapsize", out var sizeText ) && !string.IsNullOrWhiteSpace( sizeText ) )
		{
			if ( !int.TryParse( sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out resolution ) )
				throw new CubemapBakeException( CubemapBakeException.CodeInvalidProbe,
					$"env_cubemap '{name}' has a non-integer cubemapsize '{sizeText}'." );
		}
		return new CubemapProbe( name, entity.Origin, resolution );
	}
}

/// <summary>
/// The probe registry for one map: the placed env_cubemap probes, by name.
/// Duplicate names are rejected (two probes with one name would make the
/// envmap reference ambiguous). LoadJson reads
/// <c>{ "probes": [ { "name", "position": [x, y, z], "resolution",
/// "minWallDistance" } ] }</c>.
/// </summary>
public sealed class CubemapProbeRegistry
{
	readonly List<CubemapProbe> _probes = new();

	public IReadOnlyList<CubemapProbe> Probes => _probes;
	public int Count => _probes.Count;

	public void Add( CubemapProbe probe )
	{
		if ( probe is null ) throw new ArgumentNullException( nameof( probe ) );
		if ( _probes.Any( p => string.Equals( p.Name, probe.Name, StringComparison.OrdinalIgnoreCase ) ) )
			throw new CubemapBakeException( CubemapBakeException.CodeDuplicateProbe,
				$"Duplicate cubemap probe name '{probe.Name}'." );
		_probes.Add( probe );
	}

	public bool Remove( string name )
	{
		var probe = Find( name );
		if ( probe is null ) return false;
		return _probes.Remove( probe );
	}

	public CubemapProbe Find( string name ) =>
		string.IsNullOrWhiteSpace( name ) ? null :
		_probes.FirstOrDefault( p => string.Equals( p.Name, name, StringComparison.OrdinalIgnoreCase ) );

	public static CubemapProbeRegistry LoadJson( string json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		var registry = new CubemapProbeRegistry();
		JsonDocument doc;
		try { doc = JsonDocument.Parse( json ); }
		catch ( JsonException ex ) { throw new CubemapBakeException( CubemapBakeException.CodeInvalidRegistry, $"Probe registry is not valid JSON: {ex.Message}" ); }
		using ( doc )
		{
			var root = doc.RootElement;
			if ( !root.TryGetProperty( "probes", out var probes ) || probes.ValueKind != JsonValueKind.Array )
				throw new CubemapBakeException( CubemapBakeException.CodeInvalidRegistry, "Probe registry JSON needs a \"probes\" array." );
			foreach ( var element in probes.EnumerateArray() )
			{
				var name = element.TryGetProperty( "name", out var n ) ? n.GetString() ?? "env_cubemap" : "env_cubemap";
				if ( !element.TryGetProperty( "position", out var p ) || p.ValueKind != JsonValueKind.Array || p.GetArrayLength() != 3 )
					throw new CubemapBakeException( CubemapBakeException.CodeInvalidRegistry, $"Probe '{name}' needs a 3-number \"position\"." );
				var position = new Vec3( p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle() );
				var resolution = element.TryGetProperty( "resolution", out var r ) ? r.GetInt32() : CubemapBake.DefaultResolution;
				var minWall = element.TryGetProperty( "minWallDistance", out var m ) ? m.GetSingle() : CubemapBake.DefaultMinWallDistance;
				registry.Add( new CubemapProbe( name, position, resolution, minWall ) );
			}
		}
		return registry;
	}

	public static CubemapProbeRegistry LoadFile( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) throw new ArgumentException( "Probe registry path is empty.", nameof( path ) );
		return LoadJson( File.ReadAllText( path ) );
	}
}

/// <summary>
/// What a cubemap bake captures from: the compiled scene's brushes
/// (occluders + the surfaces the reflections show) and lights. Built from
/// a <see cref="CompileInput"/> by the compile stage (see CUBEMAPBUILD
/// SEAM); detail brushes are included — they are visible geometry, same as
/// in the lightmap bake.
/// </summary>
public sealed class CubemapBakeScene
{
	public List<CompileBrush> Brushes { get; } = new();
	public List<CompileLight> Lights { get; } = new();

	public CubemapBakeScene() { }

	public static CubemapBakeScene FromCompileInput( CompileInput input )
	{
		if ( input is null ) throw new ArgumentNullException( nameof( input ) );
		var scene = new CubemapBakeScene();
		if ( input.Brushes is not null ) scene.Brushes.AddRange( input.Brushes );
		if ( input.Lights is not null ) scene.Lights.AddRange( input.Lights );
		return scene;
	}
}

/// <summary>Run options for a cubemap bake.</summary>
public sealed class CubemapBakeOptions
{
	/// <summary>Colour of texels whose ray hits no brush (the sky). Linear RGB 0..1.</summary>
	public Vec3 SkyColor { get; set; } = new( 0.08f, 0.09f, 0.12f );
	/// <summary>Neutral albedo the CPU capture multiplies direct light by (no per-material CPU albedo source exists yet — see the file header).</summary>
	public float Albedo { get; set; } = 0.8f;
	/// <summary>Maximum probes in one bake. Over it the bake fails loudly.</summary>
	public int MaxProbes { get; set; } = 256;
	/// <summary>When false, the too-close-to-wall warning is not raised (placement checks still run).</summary>
	public bool ValidateWallDistance { get; set; } = true;

	public void Validate()
	{
		if ( Albedo < 0f || Albedo > 1f || !float.IsFinite( Albedo ) ) throw new ArgumentException( $"Albedo {Albedo} must be 0..1.", nameof( Albedo ) );
		if ( MaxProbes < 1 ) throw new ArgumentException( $"MaxProbes {MaxProbes} must be at least 1.", nameof( MaxProbes ) );
		if ( !float.IsFinite( SkyColor.x ) || !float.IsFinite( SkyColor.y ) || !float.IsFinite( SkyColor.z )
			|| SkyColor.x < 0f || SkyColor.y < 0f || SkyColor.z < 0f || SkyColor.x > 1f || SkyColor.y > 1f || SkyColor.z > 1f )
			throw new ArgumentException( "SkyColor must be finite RGB 0..1.", nameof( SkyColor ) );
	}
}

/// <summary>One progress report from a running cubemap bake. Mirrors <see cref="CompileProgress"/>.</summary>
public sealed class CubemapBakeProgress
{
	public CubemapBakePhase Phase;
	public string ProbeName;
	public CubemapFace? Face;
	public string Message;
	public float Percent;
}

/// <summary>One captured cubemap face: a texel grid of linear RGB, like <see cref="CompileLightmapFace"/>.</summary>
public sealed class CubemapFaceCapture
{
	public string ProbeName { get; }
	public CubemapFace Face { get; }
	public int Resolution { get; }
	/// <summary>RGB floats, row-major, Resolution * Resolution * 3. Row 0 is the +up edge of the face.</summary>
	public float[] Rgb { get; }
	/// <summary>Mean of (r+g+b)/3 over all texels (sky texels included).</summary>
	public float Mean { get; }
	/// <summary>Texels whose ray hit a brush (the rest are sky).</summary>
	public int HitTexels { get; }
	public int TotalTexels => Resolution * Resolution;
	public int SkyTexels => TotalTexels - HitTexels;

	public CubemapFaceCapture( string probeName, CubemapFace face, int resolution, float[] rgb, float mean, int hitTexels )
	{
		ProbeName = probeName ?? "env_cubemap";
		Face = face;
		Resolution = resolution;
		Rgb = rgb ?? Array.Empty<float>();
		Mean = mean;
		HitTexels = hitTexels;
	}
}

/// <summary>
/// What a cubemap bake produced: 6 faces per probe, plus the probe
/// positions the envmap reference resolves against. A shiny prop material
/// references a bake through <see cref="ResolveEnvmap"/> —
/// "env_cubemap:&lt;nearest-probe-name&gt;" — the string the material
/// bridge stores as $envmap.
/// </summary>
public sealed class CubemapBakeSet
{
	public IReadOnlyList<CubemapFaceCapture> Faces { get; }
	public IReadOnlyList<string> ProbeNames { get; }
	public IReadOnlyDictionary<string, Vec3> ProbePositions { get; }
	public IReadOnlyDictionary<string, int> ProbeResolutions { get; }
	public int ProbeCount => ProbeNames.Count;
	/// <summary>Faces captured: ProbeCount * 6.</summary>
	public int FaceCount => Faces.Count;
	public float Mean => Faces.Count == 0 ? 0f : Faces.Average( f => f.Mean );

	public CubemapBakeSet( IReadOnlyList<CubemapFaceCapture> faces, IReadOnlyList<string> probeNames,
		IReadOnlyDictionary<string, Vec3> probePositions, IReadOnlyDictionary<string, int> probeResolutions )
	{
		Faces = faces ?? Array.Empty<CubemapFaceCapture>();
		ProbeNames = probeNames ?? Array.Empty<string>();
		ProbePositions = probePositions ?? new Dictionary<string, Vec3>();
		ProbeResolutions = probeResolutions ?? new Dictionary<string, int>();
	}

	/// <summary>The 6 faces of one probe, in <see cref="CubemapFace"/> order. Empty when the probe is not in this bake.</summary>
	public IReadOnlyList<CubemapFaceCapture> FacesFor( string probeName ) =>
		string.IsNullOrWhiteSpace( probeName ) ? Array.Empty<CubemapFaceCapture>() :
		Faces.Where( f => string.Equals( f.ProbeName, probeName, StringComparison.OrdinalIgnoreCase ) )
			.OrderBy( f => f.Face ).ToList();

	/// <summary>Nearest captured probe to a position (ties: first in registry order). Null when the bake has no probes.</summary>
	public string NearestProbe( Vec3 position )
	{
		string best = null;
		var bestDist = float.MaxValue;
		foreach ( var name in ProbeNames )
		{
			if ( !ProbePositions.TryGetValue( name, out var probePos ) ) continue;
			var dist = ( probePos - position ).LengthSquared;
			if ( dist < bestDist ) { bestDist = dist; best = name; }
		}
		return best;
	}

	/// <summary>The envmap reference for a captured probe, "env_cubemap:&lt;name&gt;". Null when the probe is not in this bake.</summary>
	public string ReferenceForProbe( string probeName ) =>
		string.IsNullOrWhiteSpace( probeName ) || !ProbePositions.ContainsKey( probeName ) ? null :
		$"{CubemapBake.ReferencePrefix}:{probeName}";

	/// <summary>The envmap reference a prop material at a position uses: the nearest captured probe's reference. Null when the bake has no probes.</summary>
	public string ResolveEnvmap( Vec3 position )
	{
		var nearest = NearestProbe( position );
		return nearest is null ? null : ReferenceForProbe( nearest );
	}
}

/// <summary>
/// What a <see cref="CubemapBake"/> run produced. A failed or cancelled run
/// has a null <see cref="Set"/> and a readable <see cref="Error"/> (or
/// <see cref="Cancelled"/>); it never throws for a run failure or a cancel,
/// mirroring <see cref="CompileResult"/>.
/// </summary>
public sealed class CubemapBakeResult
{
	public bool Succeeded => Error is null && !Cancelled;
	public bool Cancelled { get; set; }
	public string Error { get; set; }
	public CubemapBakeSet Set { get; set; }
	/// <summary>Issues found, errors first. Empty on a clean run.</summary>
	public List<CompileIssue> Issues { get; } = new();
	public List<string> Log { get; } = new();
	public TimeSpan Duration { get; set; }
}

/// <summary>
/// The capture seam: one face rendered at one probe from the baked scene.
/// <see cref="CpuRayCapture"/> is the shipped implementation; a GPU capture
/// (6 faces through the shared SceneRenderer / HeadlessRender windowless EGL
/// path) implements this interface later without changing
/// <see cref="CubemapBake"/>.
/// </summary>
public interface ICubemapCapture
{
	CubemapFaceCapture CaptureFace( CubemapProbe probe, CubemapFace face, CubemapBakeScene scene, CubemapBakeOptions options, CancellationToken cancel );
}

/// <summary>
/// The native CPU reference capture: one 90-degree face rendered by
/// ray-casting the compiled scene from the probe position. Same slab ray
/// test and same direct-light model as CompileRunner's lightmap bake, so a
/// face's brightness agrees with what the lightmap bake says about the same
/// surfaces. Deterministic; no GL, no renderer, no process.
/// </summary>
public sealed class CpuRayCapture : ICubemapCapture
{
	public CubemapFaceCapture CaptureFace( CubemapProbe probe, CubemapFace face, CubemapBakeScene scene, CubemapBakeOptions options, CancellationToken cancel )
	{
		if ( probe is null ) throw new ArgumentNullException( nameof( probe ) );
		if ( scene is null ) throw new ArgumentNullException( nameof( scene ) );
		options ??= new CubemapBakeOptions();
		var resolution = probe.Resolution;
		var rgb = new float[resolution * resolution * 3];
		CubemapBake.FaceBasis( face, out var forward, out var right, out var up );
		var hitTexels = 0;
		var sum = 0f;

		for ( var iy = 0; iy < resolution; iy++ )
		{
			cancel.ThrowIfCancellationRequested();
			var v = 1f - ( iy + 0.5f ) * 2f / resolution;
			for ( var ix = 0; ix < resolution; ix++ )
			{
				var u = ( ix + 0.5f ) * 2f / resolution - 1f;
				var dir = ( forward + right * u + up * v ).Normal;
				var idx = ( iy * resolution + ix ) * 3;
				if ( CubemapBake.Raycast( probe.Position, dir, CubemapBake.MaxRayDistance, scene.Brushes, out var hit ) )
				{
					var point = probe.Position + dir * hit.Distance;
					var light = CubemapBake.DirectLight( point, hit.Normal, scene.Lights, scene.Brushes, options.Albedo );
					rgb[idx] = Math.Min( 1f, light.x );
					rgb[idx + 1] = Math.Min( 1f, light.y );
					rgb[idx + 2] = Math.Min( 1f, light.z );
					hitTexels++;
				}
				else
				{
					rgb[idx] = options.SkyColor.x;
					rgb[idx + 1] = options.SkyColor.y;
					rgb[idx + 2] = options.SkyColor.z;
				}
				sum += ( rgb[idx] + rgb[idx + 1] + rgb[idx + 2] ) / 3f;
			}
		}
		var total = resolution * resolution;
		return new CubemapFaceCapture( probe.Name, face, resolution, rgb, total == 0 ? 0f : sum / total, hitTexels );
	}
}

/// <summary>
/// env_cubemap probes + native 6-face capture for the map bake output.
/// Probe registry in, 6 faces per probe out; validation is named issues,
/// never a silent fix — a probe inside a wall fails the bake loudly.
/// </summary>
public static class CubemapBake
{
	/// <summary>Default face resolution (Source's default cubemap size).</summary>
	public const int DefaultResolution = 32;
	public const int MinResolution = 16;
	public const int MaxResolution = 256;
	/// <summary>Default minimum distance from a probe to any brush surface (units). The report's "typically 64 units from walls, above the floor".</summary>
	public const float DefaultMinWallDistance = 64f;
	/// <summary>Envmap reference prefix: a bake is referenced as "env_cubemap:&lt;probe-name&gt;".</summary>
	public const string ReferencePrefix = "env_cubemap";
	/// <summary>Ray length for capture rays (map coordinates run to ~16K).</summary>
	public const float MaxRayDistance = 1000000f;

	/// <summary>All six faces, in capture order.</summary>
	public static readonly IReadOnlyList<CubemapFace> AllFaces = new[]
	{
		CubemapFace.PositiveX, CubemapFace.NegativeX,
		CubemapFace.PositiveY, CubemapFace.NegativeY,
		CubemapFace.PositiveZ, CubemapFace.NegativeZ,
	};

	/// <summary>Issue codes. These are the IssueEngine rule names the cubemap stage reports, mirroring CompileIssue's codes.</summary>
	public static class IssueCodes
	{
		public const string ProbeInsideSolid = "probe-inside-solid";
		public const string ProbeTooCloseToWall = "probe-too-close-to-wall";
		public const string ProbeOutsideMap = "probe-outside-map";
		public const string DuplicateProbe = "duplicate-probe";
		public const string InvalidProbe = "invalid-probe";
		public const string NoProbes = "no-probes";
		public const string LimitExceeded = "limit-exceeded";
	}

	// -- Validation -----------------------------------------------------------

	/// <summary>
	/// Validate every probe against the scene, without a BSP: inside-solid
	/// (error), too-close-to-wall (warning), outside the brush-union bounds
	/// (error), bad probe fields (error), duplicate names (error). No-BSP
	/// bounds check is the brush AABB: a probe in a corner gap between wall
	/// boxes passes it (documented limitation — the BSP overload is
	/// authoritative when a BSP exists).
	/// </summary>
	public static List<CompileIssue> ValidateProbes( CubemapProbeRegistry registry, CubemapBakeScene scene ) =>
		ValidateProbes( registry, scene, bsp: null );

	/// <summary>
	/// Validate every probe against the scene + the compiled BSP. The BSP is
	/// authoritative for inside-map: a probe must sit in a non-solid,
	/// non-void leaf (CompileBspResult.LeafContaining); the AABB check is
	/// skipped when a BSP is given. This is the overload the compile stage
	/// calls with its BSP result.
	/// </summary>
	public static List<CompileIssue> ValidateProbes( CubemapProbeRegistry registry, CubemapBakeScene scene, CompileBspResult bsp )
	{
		if ( registry is null ) throw new ArgumentNullException( nameof( registry ) );
		if ( scene is null ) throw new ArgumentNullException( nameof( scene ) );
		var issues = new List<CompileIssue>();
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		var hasBrushes = scene.Brushes is not null && scene.Brushes.Any( b => b is not null );
		Vec3 boundsMin = default, boundsMax = default;
		if ( hasBrushes ) ( boundsMin, boundsMax ) = BrushUnionBounds( scene.Brushes );

		foreach ( var probe in registry.Probes )
		{
			if ( probe is null )
			{
				issues.Add( new CompileIssue( IssueCodes.InvalidProbe, CompileSeverity.Error,
					"Null probe in the registry.", "map", Vec3.Zero, "Remove the null probe entry." ) );
				continue;
			}
			var subject = string.IsNullOrWhiteSpace( probe.Name ) ? "env_cubemap" : probe.Name;
			foreach ( var defect in probe.Validate() )
				issues.Add( new CompileIssue( IssueCodes.InvalidProbe, CompileSeverity.Error,
					$"Probe '{subject}' is invalid: {defect}.", subject, probe.Position,
					"Set a non-empty name, a finite position and a power-of-two resolution 16..256." ) );
			if ( !seen.Add( probe.Name ?? "" ) )
				issues.Add( new CompileIssue( IssueCodes.DuplicateProbe, CompileSeverity.Error,
					$"Duplicate cubemap probe name '{probe.Name}'.", subject, probe.Position,
					"Rename one of the probes so every env_cubemap has a unique targetname." ) );
			if ( probe.Validate().Count > 0 ) continue;

			if ( bsp is not null )
			{
				// Inside-solid is checked against the brushes first:
				// CompileBspResult.LeafContaining never returns a solid leaf
				// (it only returns empty leaves, then void leaves), so the
				// BSP alone cannot tell "inside a wall" from "outside the map".
				if ( InsideAnySolid( probe.Position, scene.Brushes ) )
				{
					issues.Add( new CompileIssue( IssueCodes.ProbeInsideSolid, CompileSeverity.Error,
						$"Probe '{subject}' at {probe.Position} is inside a solid brush.", subject, probe.Position,
						"Move the probe out of the brush, into open space." ) );
					continue;
				}
				var leaf = bsp.LeafContaining( probe.Position );
				if ( leaf is null || leaf.IsVoid )
				{
					issues.Add( new CompileIssue( IssueCodes.ProbeOutsideMap, CompileSeverity.Error,
						$"Probe '{subject}' at {probe.Position} is outside the map (no empty leaf contains it).", subject, probe.Position,
						"Move the probe inside the sealed map, clear of solid brushes." ) );
					continue;
				}
				if ( leaf.IsSolid )
				{
					// Defensive: LeafContaining never returns a solid leaf
					// today, but if that changes this stays the right code.
					issues.Add( new CompileIssue( IssueCodes.ProbeInsideSolid, CompileSeverity.Error,
						$"Probe '{subject}' at {probe.Position} is inside a solid brush (leaf {leaf.Id}).", subject, probe.Position,
						"Move the probe out of the brush, into open space." ) );
					continue;
				}
			}
			else
			{
				if ( !hasBrushes )
				{
					issues.Add( new CompileIssue( IssueCodes.ProbeOutsideMap, CompileSeverity.Error,
						$"Probe '{subject}' at {probe.Position} is outside the map: the map has no brushes.", subject, probe.Position,
						"Add brushes to the map and move the probe inside it." ) );
					continue;
				}
				if ( InsideAnySolid( probe.Position, scene.Brushes ) )
				{
					issues.Add( new CompileIssue( IssueCodes.ProbeInsideSolid, CompileSeverity.Error,
						$"Probe '{subject}' at {probe.Position} is inside a solid brush.", subject, probe.Position,
						"Move the probe out of the brush, into open space." ) );
					continue;
				}
				if ( probe.Position.x < boundsMin.x || probe.Position.x > boundsMax.x
					|| probe.Position.y < boundsMin.y || probe.Position.y > boundsMax.y
					|| probe.Position.z < boundsMin.z || probe.Position.z > boundsMax.z )
				{
					issues.Add( new CompileIssue( IssueCodes.ProbeOutsideMap, CompileSeverity.Error,
						$"Probe '{subject}' at {probe.Position} is outside the map bounds.", subject, probe.Position,
						"Move the probe inside the sealed map, clear of solid brushes." ) );
					continue;
				}
			}

			var distance = DistanceToNearestSurface( probe.Position, scene.Brushes );
			if ( distance < probe.MinWallDistance )
				issues.Add( new CompileIssue( IssueCodes.ProbeTooCloseToWall, CompileSeverity.Warning,
					$"Probe '{subject}' at {probe.Position} is {distance:0.#} units from the nearest wall, under the {probe.MinWallDistance:0.#}-unit guide.",
					subject, probe.Position,
					"Move the probe at least 64 units from walls and above the floor, as Hammer++ recommends." ) );
		}
		return issues;
	}

	// -- Bake -----------------------------------------------------------------

	/// <summary>
	/// Bake every probe: validate, then capture 6 faces per probe through
	/// <paramref name="capture"/> (default <see cref="CpuRayCapture"/>).
	/// Never throws for a run failure or a cancel: those come back as
	/// <see cref="CubemapBakeResult.Error"/> / <see cref="CubemapBakeResult.Cancelled"/>.
	/// A validation error fails the bake loudly with no set; a warning does not.
	/// </summary>
	public static CubemapBakeResult Bake( CubemapProbeRegistry registry, CubemapBakeScene scene,
		CubemapBakeOptions options = null, ICubemapCapture capture = null,
		IProgress<CubemapBakeProgress> progress = null, CancellationToken cancel = default )
	{
		if ( registry is null ) throw new ArgumentNullException( nameof( registry ) );
		if ( scene is null ) throw new ArgumentNullException( nameof( scene ) );
		options ??= new CubemapBakeOptions();
		options.Validate();
		capture ??= new CpuRayCapture();
		var clock = System.Diagnostics.Stopwatch.StartNew();
		var result = new CubemapBakeResult();
		var log = new List<string>();

		void Log( string message ) => log.Add( string.Format( CultureInfo.InvariantCulture,
			"[{0:HH:mm:ss.fff}] [cubemap] {1}", DateTime.UtcNow, message ) );
		void Report( CubemapBakePhase phase, float percent, string probe = null, CubemapFace? face = null, string message = null ) =>
			progress?.Report( new CubemapBakeProgress { Phase = phase, Percent = percent, ProbeName = probe, Face = face, Message = message } );

		try
		{
			Report( CubemapBakePhase.Preparing, 0f, null, null, $"Baking cubemaps for {registry.Count} probe(s)." );
			cancel.ThrowIfCancellationRequested();

			if ( registry.Count == 0 )
			{
				result.Issues.Add( new CompileIssue( IssueCodes.NoProbes, CompileSeverity.Warning,
					"Map has no env_cubemap probes: nothing to bake.", "map", Vec3.Zero,
					"Place an env_cubemap probe to bake reflections, or skip the cubemap stage." ) );
				result.Set = new CubemapBakeSet( Array.Empty<CubemapFaceCapture>(), Array.Empty<string>(),
					new Dictionary<string, Vec3>(), new Dictionary<string, int>() );
				result.Duration = clock.Elapsed;
				Log( "No probes: empty cubemap set." );
				result.Log.AddRange( log );
				Report( CubemapBakePhase.Done, 100f, null, null, "No probes: empty cubemap set." );
				return result;
			}
			if ( registry.Count > options.MaxProbes )
			{
				result.Error = $"Probe count {registry.Count} exceeds the limit of {options.MaxProbes}.";
				result.Issues.Add( new CompileIssue( IssueCodes.LimitExceeded, CompileSeverity.Error, result.Error, "map", Vec3.Zero,
					$"Reduce the probe count below {options.MaxProbes} or raise CubemapBakeOptions.MaxProbes." ) );
				result.Duration = clock.Elapsed;
				Log( "FAILED: " + result.Error );
				Report( CubemapBakePhase.Failed, 100f, null, null, result.Error );
				result.Log.AddRange( log );
				return result;
			}

			var issues = ValidateProbes( registry, scene );
			result.Issues.AddRange( issues );
			if ( issues.Any( i => i.Severity == CompileSeverity.Error ) )
			{
				result.Error = issues.First( i => i.Severity == CompileSeverity.Error ).Message;
				result.Duration = clock.Elapsed;
				Log( "FAILED: " + result.Error );
				Report( CubemapBakePhase.Failed, 100f, null, null, result.Error );
				result.Log.AddRange( log );
				return result;
			}

			var faces = new List<CubemapFaceCapture>();
			var positions = new Dictionary<string, Vec3>();
			var resolutions = new Dictionary<string, int>();
			var total = registry.Count * AllFaces.Count;
			var done = 0;
			foreach ( var probe in registry.Probes )
			{
				cancel.ThrowIfCancellationRequested();
				positions[probe.Name] = probe.Position;
				resolutions[probe.Name] = probe.Resolution;
				foreach ( var face in AllFaces )
				{
					cancel.ThrowIfCancellationRequested();
					faces.Add( capture.CaptureFace( probe, face, scene, options, cancel ) );
					done++;
					Report( CubemapBakePhase.Capturing, 100f * done / total, probe.Name, face,
						$"Capturing {probe.Name} {face} ({done}/{total})." );
				}
				Log( $"Probe '{probe.Name}' done: 6 faces at {probe.Resolution}x{probe.Resolution}." );
			}
			result.Set = new CubemapBakeSet( faces, registry.Probes.Select( p => p.Name ).ToList(), positions, resolutions );
			result.Duration = clock.Elapsed;
			Log( $"Cubemap bake done: {result.Set.ProbeCount} probe(s), {result.Set.FaceCount} faces, mean {result.Set.Mean:0.###}." );
			Report( CubemapBakePhase.Done, 100f, null, null, "Cubemap bake done." );
			result.Log.AddRange( log );
			return result;
		}
		catch ( OperationCanceledException )
		{
			result.Cancelled = true;
			result.Duration = clock.Elapsed;
			Log( "Cancelled." );
			Report( CubemapBakePhase.Cancelled, 100f, null, null, "Cubemap bake cancelled." );
			result.Log.AddRange( log );
			return result;
		}
		catch ( Exception ex )
		{
			result.Error = $"Cubemap bake failed ({ex.GetType().Name}): {ex.Message}";
			result.Issues.Add( new CompileIssue( "stage-failed", CompileSeverity.Error, result.Error, "map", Vec3.Zero,
				"Fix the underlying error and re-bake." ) );
			result.Duration = clock.Elapsed;
			Log( "FAILED: " + result.Error );
			Report( CubemapBakePhase.Failed, 100f, null, null, result.Error );
			result.Log.AddRange( log );
			return result;
		}
	}

	// -- Output ---------------------------------------------------------------

	/// <summary>GEPPETTO-CUBEMAP 1: header, one line per probe, then per face a header line and Resolution rows of RGB triples. Mirrors CompileRunner's .lightmap text.</summary>
	public static string WriteText( CubemapBakeSet set )
	{
		if ( set is null ) throw new ArgumentNullException( nameof( set ) );
		var lines = new List<string> { "GEPPETTO-CUBEMAP 1", $"probes {set.ProbeCount}", $"faces {set.FaceCount}" };
		foreach ( var name in set.ProbeNames )
		{
			set.ProbePositions.TryGetValue( name, out var pos );
			set.ProbeResolutions.TryGetValue( name, out var res );
			lines.Add( $"probe {name} resolution {res} position {F( pos.x )} {F( pos.y )} {F( pos.z )}" );
		}
		foreach ( var face in set.Faces )
		{
			lines.Add( $"face {face.ProbeName} {face.Face} mean {F( face.Mean )} hit {face.HitTexels}/{face.TotalTexels}" );
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

	/// <summary>Write a <see cref="WriteText"/> product file. Returns the path written. A failed write throws — the caller (compile stage) decides how to report it.</summary>
	public static string WriteFile( string path, CubemapBakeSet set )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) throw new ArgumentException( "Cubemap output path is empty.", nameof( path ) );
		var text = WriteText( set );
		Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( path ) ) ?? "." );
		File.WriteAllText( path, text );
		return path;
	}

	static string F( float v ) => v.ToString( "0.####", CultureInfo.InvariantCulture );

	// -- Geometry (ray test mirrors CompileRunner.Shadowed) -------------------

	/// <summary>One ray hit: distance along the ray + the entry face's outward normal and material.</summary>
	internal readonly struct RayHit
	{
		public readonly float Distance;
		public readonly Vec3 Normal;
		public readonly string Material;
		public RayHit( float distance, Vec3 normal, string material ) { Distance = distance; Normal = normal; Material = material; }
	}

	/// <summary>
	/// View basis for one cube face: forward + right + up, orthonormal, Z-up
	/// world. The face is a 90-degree view; a texel at (u, v) in [-1, 1] looks
	/// along (forward + right*u + up*v).
	/// </summary>
	internal static void FaceBasis( CubemapFace face, out Vec3 forward, out Vec3 right, out Vec3 up )
	{
		switch ( face )
		{
			case CubemapFace.PositiveX: forward = new Vec3( 1, 0, 0 ); up = new Vec3( 0, 0, 1 ); break;
			case CubemapFace.NegativeX: forward = new Vec3( -1, 0, 0 ); up = new Vec3( 0, 0, 1 ); break;
			case CubemapFace.PositiveY: forward = new Vec3( 0, 1, 0 ); up = new Vec3( 0, 0, 1 ); break;
			case CubemapFace.NegativeY: forward = new Vec3( 0, -1, 0 ); up = new Vec3( 0, 0, 1 ); break;
			case CubemapFace.PositiveZ: forward = new Vec3( 0, 0, 1 ); up = new Vec3( 0, 1, 0 ); break;
			default: forward = new Vec3( 0, 0, -1 ); up = new Vec3( 0, 1, 0 ); break;
		}
		right = Vec3.Cross( forward, up ).Normal;
		up = Vec3.Cross( right, forward );
	}

	/// <summary>
	/// Nearest brush hit along origin + dir * [0, maxDist], slab method over
	/// each convex brush's half-spaces — the same test CompileRunner.Shadowed
	/// uses, extended to return the entry face. False when nothing is hit.
	/// </summary>
	internal static bool Raycast( Vec3 origin, Vec3 dir, float maxDist, IReadOnlyList<CompileBrush> solids, out RayHit hit )
	{
		hit = default;
		if ( solids is null ) return false;
		var found = false;
		var best = float.MaxValue;
		Vec3 bestNormal = default;
		var bestMaterial = "default";
		foreach ( var brush in solids )
		{
			if ( brush is null || brush.Faces is null ) continue;
			var tMin = 0f;
			var tMax = maxDist;
			var ok = true;
			Vec3 entryNormal = default;
			var entryMaterial = "default";
			foreach ( var face in brush.Faces )
			{
				if ( face is null ) continue;
				var dn = Vec3.Dot( face.Normal, dir );
				var dist = face.Distance - Vec3.Dot( face.Normal, origin );
				if ( Math.Abs( dn ) < 1e-9f )
				{
					if ( dist < 0f ) { ok = false; break; }
					continue;
				}
				var t = dist / dn;
				if ( dn < 0f )
				{
					if ( t > tMin ) { tMin = t; entryNormal = face.Normal; entryMaterial = face.Material; }
				}
				else
				{
					if ( t < tMax ) tMax = t;
				}
				if ( tMin > tMax ) { ok = false; break; }
			}
			if ( ok && tMax > 1e-3f && tMin > 1e-3f && tMin < maxDist && tMin < best )
			{
				best = tMin;
				bestNormal = entryNormal;
				bestMaterial = entryMaterial;
				found = true;
			}
		}
		if ( found ) hit = new RayHit( best, bestNormal, bestMaterial );
		return found;
	}

	/// <summary>
	/// Direct light at a surface point, mirroring CompileRunner's lightmap
	/// model exactly (Lambert + 1/(1+(d/128)^2) falloff + shadow ray): a
	/// cubemap face's brightness agrees with what the lightmap bake says
	/// about the same surfaces. Times the neutral albedo.
	/// </summary>
	internal static Vec3 DirectLight( Vec3 point, Vec3 normal, IReadOnlyList<CompileLight> lights, IReadOnlyList<CompileBrush> solids, float albedo )
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
			var origin = point + normal * 0.25f;
			if ( Raycast( origin, dir, dist, solids, out _ ) ) continue;
			var atten = light.Intensity / ( 1f + ( dist / 128f ) * ( dist / 128f ) );
			total += new Vec3( light.Color.x * atten * facing, light.Color.y * atten * facing, light.Color.z * atten * facing );
		}
		return total * albedo;
	}

	/// <summary>True when the point is inside any brush solid (all half-spaces, 1e-3 tolerance).</summary>
	internal static bool InsideAnySolid( Vec3 point, IReadOnlyList<CompileBrush> solids )
	{
		if ( solids is null ) return false;
		foreach ( var brush in solids )
		{
			if ( brush is null || brush.Faces is null ) continue;
			var inside = true;
			foreach ( var face in brush.Faces )
			{
				if ( face is null ) continue;
				if ( Vec3.Dot( face.Normal, point ) > face.Distance + 1e-3f ) { inside = false; break; }
			}
			if ( inside ) return true;
		}
		return false;
	}

	/// <summary>
	/// Distance from a point to the nearest brush surface, as the minimum
	/// distance to any face plane. A lower bound on the true distance (the
	/// perpendicular foot can miss the face polygon near edges/corners), so
	/// the too-close warning is conservative there — it warns, never misses.
	/// </summary>
	internal static float DistanceToNearestSurface( Vec3 point, IReadOnlyList<CompileBrush> solids )
	{
		var best = float.MaxValue;
		if ( solids is null ) return best;
		foreach ( var brush in solids )
		{
			if ( brush is null || brush.Faces is null ) continue;
			foreach ( var face in brush.Faces )
			{
				if ( face is null ) continue;
				var d = Math.Abs( Vec3.Dot( face.Normal, point ) - face.Distance );
				if ( d < best ) best = d;
			}
		}
		return best;
	}

	/// <summary>Union AABB of all brush solids, from plane-triple vertices (same solve as CompileRunner.BuildBrushPolygons).</summary>
	internal static (Vec3 Min, Vec3 Max) BrushUnionBounds( IReadOnlyList<CompileBrush> solids )
	{
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var brush in solids )
		{
			if ( brush is null ) continue;
			foreach ( var vertex in BrushVertices( brush ) )
			{
				min = new Vec3( Math.Min( min.x, vertex.x ), Math.Min( min.y, vertex.y ), Math.Min( min.z, vertex.z ) );
				max = new Vec3( Math.Max( max.x, vertex.x ), Math.Max( max.y, vertex.y ), Math.Max( max.z, vertex.z ) );
			}
		}
		return ( min, max );
	}

	/// <summary>Brush vertices by plane-intersection solve (Cramer, doubles for stability), deduplicated. Same method as CompileRunner.BuildBrushPolygons.</summary>
	internal static List<Vec3> BrushVertices( CompileBrush brush )
	{
		var vertices = new List<Vec3>();
		var faces = brush.Faces;
		for ( var i = 0; i < faces.Count; i++ )
			for ( var j = i + 1; j < faces.Count; j++ )
				for ( var k = j + 1; k < faces.Count; k++ )
				{
					if ( !SolvePlaneTriple( faces[i], faces[j], faces[k], out var p ) ) continue;
					var inside = true;
					foreach ( var f in faces )
						if ( Vec3.Dot( f.Normal, p ) > f.Distance + 1e-3f ) { inside = false; break; }
					if ( !inside ) continue;
					if ( vertices.Any( q => ( q - p ).LengthSquared <= 1e-6f ) ) continue;
					vertices.Add( p );
				}
		return vertices;
	}

	static bool SolvePlaneTriple( CompileFace a, CompileFace b, CompileFace c, out Vec3 point )
	{
		point = Vec3.Zero;
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
}
