using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Effigy;

namespace Effigy.Map;

// ============================================================================
// Hammer Desktop P2: release packing (asset bundle) + cubemap build stage.
// Board task hammer-compile-packing-cubemap-build.
//
// The release step of the native compile: collect every asset the map
// actually uses (models, materials/textures, sounds, particles — from the
// registries), bundle them into a single map package alongside the compiled
// map, and run the cubemap build stage (6 faces at each probe, stored into
// the package). Missing assets are named issues and the pack fails loudly —
// nothing is silently dropped and no partial package is written.
//
// DEPENDENCY STATUS (2026-11-13, verified against the live tree):
//   * CompileRunner (hammer-compile-native-pipeline) HAS landed:
//     CompileInput / CompileBrush / CompileFace / CompileLight /
//     CompileIssue / CompileResult. The pack + cubemap stages live in
//     CompileRunner.Run; this file is the packing engine it calls.
//   * CubemapBake (hammer-cubemap-probes-bake) HAS landed: CubemapProbe /
//     CubemapProbeRegistry / CubemapBakeScene / CubemapBake.Bake /
//     CubemapBake.WriteText. BuildCubemaps below is a thin wrapper that
//     builds the registry from CompileInput.Probes and calls Bake with the
//     shipped CPU capture; a GPU capture plugs in later as a second
//     ICubemapCapture without changing this file's API.
//   * AssetRegistries (hammer-model-particle-sound-registries) HAS landed:
//     ModelAssetRegistry / ParticleAssetRegistry / SoundAssetRegistry +
//     the AssetRegistries facade (ProjectRoot + TryGet + ValidateFiles).
//     This file only READS the registries. It never registers, removes or
//     mutates an entry, and it does NOT bump UsedCount / NoteUsed — the
//     used-count ticket (hammer-browsers-used-count-replace-texture) owns
//     that seam and can call GatherUsedAssets and NoteUsed the result.
//   * MapDocument (hammer-map-document-model) HAS landed: the document
//     overloads below go through CompileInput.FromDocument, the adapter
//     the native-pipeline ticket deferred and this ticket adds.
//
// GATHERING RULES (used-only, deterministic — first-seen order, deduped):
//   * Materials: distinct face-material names across the compile input's
//     brushes. Blank names, the "default" CompileFace placeholder and tool
//     textures (nodraw, clip, skip, hint, ...) are NOT assets and are
//     skipped. There is NO material registry in the tree, so a material is
//     gathered by name only: a missing material FILE cannot be detected
//     here. That check belongs to a future material-registry ticket; do
//     not invent a VMT lookup in this file.
//   * Models / sounds: the input's referenced keys, looked up in the
//     registries by name-or-path. Not in the registry -> UnknownModel /
//     UnknownSound; in the registry but missing on disk under the
//     project root -> MISSING_MODEL / MISSING_SOUND (the same codes
//     AssetHotload emits, so the auto-fix ticket switches on one set).
//   * Particles: the input's referenced system names. Not in the
//     registry -> UnknownParticle; a definition file that is missing on
//     disk -> MISSING_PARTICLE. A built-in system (empty DefinitionPath)
//     needs no file and is never flagged missing.
//   * Unreferenced registry entries are never gathered and never appear
//     in the package — the counts prove used-only.
//   * A gather with any error issue fails (Succeeded false, Error names
//     the first missing asset); the found assets are still listed so the
//     caller can report exactly what is present and what is missing.
//
// PACKAGE FORMAT (GEPPETTO-MAPPACK 1, plain text, deterministic,
// invariant culture): header, map name, used-only counts, one line per
// asset sorted by kind then key, then (when a cubemap set is supplied)
// the cubemap inventory — one line per probe (resolution, 6 faces,
// envmap reference) and one line per face (mean). The full texel grids
// stay in <name>.cubemap written alongside the package; the package
// records the inventory + envmap refs so a loader can resolve
// reflections without re-reading the cubemap file.
//
// Backend / data-model / API only — no UI. Units are the kernel's inches,
// Z-up. Same document + registries -> same asset list and manifest bytes.
// ============================================================================

/// <summary>What kind of asset a packed entry is.</summary>
public enum MapPackAssetKind
{
	Model,
	Material,
	Sound,
	Particle,
}

/// <summary>
/// One asset the map actually uses. <see cref="Key"/> is the registry
/// identity (model / sound path, particle system name, material name);
/// <see cref="Path"/> is the project-relative file ("-" in the manifest
/// when the asset needs no file, e.g. a built-in particle system).
/// <see cref="ReferencedBy"/> names the first referrer; <see cref="ReferenceCount"/>
/// counts every reference to the same key.
/// </summary>
public sealed class MapPackAsset
{
	public MapPackAssetKind Kind { get; }
	public string Key { get; }
	public string Path { get; }
	public string ReferencedBy { get; }
	public int ReferenceCount { get; internal set; }

	public MapPackAsset( MapPackAssetKind kind, string key, string path, string referencedBy )
	{
		Kind = kind;
		Key = key ?? throw new ArgumentNullException( nameof( key ) );
		Path = path ?? "";
		ReferencedBy = referencedBy ?? "";
		ReferenceCount = 1;
	}

	public override string ToString() => $"{Kind} '{Key}'";
}

/// <summary>
/// What a gather / pack produced. A failed result (any error issue) has
/// <see cref="Succeeded"/> false, <see cref="Error"/> naming the first
/// missing asset, and no package file written. <see cref="Assets"/> still
/// lists the assets that WERE found, so the caller can report exactly
/// what is present and what is missing.
/// </summary>
public sealed class MapPackResult
{
	public bool Succeeded => Error is null && !Issues.Any( i => i.Severity == CompileSeverity.Error );
	public string Error { get; set; }
	public string MapName { get; set; } = "";
	public List<MapPackAsset> Assets { get; } = new();
	public List<CompileIssue> Issues { get; } = new();
	/// <summary>Cubemap set embedded in the package (inventory only), or null when the pack has no cubemaps.</summary>
	public CubemapBakeSet Cubemaps { get; set; }
	/// <summary>Manifest text (<see cref="MapPacker.WriteText"/>) when one was built; null when the pack failed before writing.</summary>
	public string Manifest { get; set; }
	/// <summary>Full path of the package file written, or null when nothing was written.</summary>
	public string PackagePath { get; set; }

	public int ModelCount => Assets.Count( a => a.Kind == MapPackAssetKind.Model );
	public int MaterialCount => Assets.Count( a => a.Kind == MapPackAssetKind.Material );
	public int SoundCount => Assets.Count( a => a.Kind == MapPackAssetKind.Sound );
	public int ParticleCount => Assets.Count( a => a.Kind == MapPackAssetKind.Particle );
	public int TotalCount => Assets.Count;

	public string Summary => $"{TotalCount} assets ({ModelCount} models, {MaterialCount} materials, {SoundCount} sounds, {ParticleCount} particles)";
}

/// <summary>Release packing + cubemap build for the native compile runner. No UI.</summary>
public static class MapPacker
{
	public const string FormatHeader = "GEPPETTO-MAPPACK 1";
	/// <summary>The CompileFace placeholder material: names no file, never packed.</summary>
	public const string DefaultMaterialName = "default";

	static readonly string[] PropClasses =
	{
		"prop_static", "prop_dynamic", "prop_physics", "prop_physics_multiplayer",
	};

	static string F( float v ) => v.ToString( "0.###", CultureInfo.InvariantCulture );
	static string KindName( MapPackAssetKind kind ) => kind.ToString().ToLowerInvariant();
	static string DisplayPath( string path ) => string.IsNullOrEmpty( path ) ? "-" : path;

	// -- Gathering ------------------------------------------------------------

	/// <summary>
	/// Collect every asset a compile input actually uses, resolved against
	/// the registries. Used-only: unreferenced registry entries never
	/// appear. Missing assets are named error issues and the result fails.
	/// </summary>
	public static MapPackResult GatherUsedAssets( CompileInput input, AssetRegistries registries )
	{
		if ( input is null ) throw new ArgumentNullException( nameof( input ) );
		if ( registries is null ) throw new ArgumentNullException( nameof( registries ) );
		var result = new MapPackResult { MapName = input.Name };
		var byKey = new Dictionary<string, MapPackAsset>( StringComparer.OrdinalIgnoreCase );

		void Add( MapPackAssetKind kind, string key, string path, string referencedBy )
		{
			var dictKey = $"{kind}|{key}";
			if ( byKey.TryGetValue( dictKey, out var existing ) ) { existing.ReferenceCount++; return; }
			var asset = new MapPackAsset( kind, key, path, referencedBy );
			byKey.Add( dictKey, asset );
			result.Assets.Add( asset );
		}

		// Materials: distinct face-material names. Blank, the "default"
		// placeholder and tool textures (nodraw et al.) name no file.
		foreach ( var brush in input.Brushes )
		{
			if ( brush is null ) continue;
			for ( var i = 0; i < brush.Faces.Count; i++ )
			{
				var face = brush.Faces[i];
				if ( face is null ) continue;
				var name = face.Material;
				if ( string.IsNullOrWhiteSpace( name ) ) continue;
				if ( string.Equals( name, DefaultMaterialName, StringComparison.OrdinalIgnoreCase ) ) continue;
				var material = new FaceMaterial( name );
				if ( material.Tool is not null ) continue;
				Add( MapPackAssetKind.Material, material.MaterialName, material.MaterialName,
					$"brush '{brush.Name}' ({brush.Id}) face {i}" );
			}
		}

		// Models: referenced keys, resolved by name-or-path.
		foreach ( var key in input.Models )
		{
			if ( string.IsNullOrWhiteSpace( key ) ) continue;
			if ( !registries.Models.TryGet( key, out var entry ) )
			{
				result.Issues.Add( new CompileIssue( AssetRegistryIssue.CodeUnknownModel, CompileSeverity.Error,
					$"Map references model '{key}' which is not in the model registry.", key, Vec3.Zero,
					"Register the model in the model registry, or fix the prop's model keyvalue." ) );
				continue;
			}
			if ( !File.Exists( AssetRegistries.ResolvePath( registries.ProjectRoot, entry.Path ) ) )
			{
				result.Issues.Add( new CompileIssue( AssetRegistryIssue.CodeMissingModel, CompileSeverity.Error,
					$"Model '{entry.Name}' is missing on disk: '{entry.Path}'.", entry.Path, Vec3.Zero,
					"Restore the model file under the project root, or re-point the registry entry." ) );
				continue;
			}
			Add( MapPackAssetKind.Model, entry.Path, entry.Path, $"map reference '{key}'" );
		}

		// Sounds: referenced keys, resolved by name-or-path.
		foreach ( var key in input.Sounds )
		{
			if ( string.IsNullOrWhiteSpace( key ) ) continue;
			if ( !registries.Sounds.TryGet( key, out var entry ) )
			{
				result.Issues.Add( new CompileIssue( AssetRegistryIssue.CodeUnknownSound, CompileSeverity.Error,
					$"Map references sound '{key}' which is not in the sound registry.", key, Vec3.Zero,
					"Register the sound in the sound registry, or fix the ambient_generic message keyvalue." ) );
				continue;
			}
			if ( !File.Exists( AssetRegistries.ResolvePath( registries.ProjectRoot, entry.Path ) ) )
			{
				result.Issues.Add( new CompileIssue( AssetRegistryIssue.CodeMissingSound, CompileSeverity.Error,
					$"Sound '{entry.Name}' is missing on disk: '{entry.Path}'.", entry.Path, Vec3.Zero,
					"Restore the sound file under the project root, or re-point the registry entry." ) );
				continue;
			}
			Add( MapPackAssetKind.Sound, entry.Path, entry.Path, $"map reference '{key}'" );
		}

		// Particles: referenced system names. A built-in system (no
		// definition file) needs no file and is never flagged missing.
		foreach ( var key in input.Particles )
		{
			if ( string.IsNullOrWhiteSpace( key ) ) continue;
			if ( !registries.Particles.TryGet( key, out var entry ) )
			{
				result.Issues.Add( new CompileIssue( AssetRegistryIssue.CodeUnknownParticle, CompileSeverity.Error,
					$"Map references particle system '{key}' which is not in the particle registry.", key, Vec3.Zero,
					"Register the particle system in the particle registry, or fix the info_particle_system effect_name keyvalue." ) );
				continue;
			}
			if ( !string.IsNullOrEmpty( entry.DefinitionPath )
				&& !File.Exists( AssetRegistries.ResolvePath( registries.ProjectRoot, entry.DefinitionPath ) ) )
			{
				result.Issues.Add( new CompileIssue( AssetRegistryIssue.CodeMissingParticle, CompileSeverity.Error,
					$"Particle system '{entry.SystemName}' is missing on disk: '{entry.DefinitionPath}'.", entry.DefinitionPath, Vec3.Zero,
					"Restore the particle definition file under the project root, or re-point the registry entry." ) );
				continue;
			}
			Add( MapPackAssetKind.Particle, entry.SystemName, entry.DefinitionPath, $"map reference '{key}'" );
		}

		var errors = result.Issues.Where( i => i.Severity == CompileSeverity.Error ).ToList();
		if ( errors.Count > 0 )
			result.Error = errors.Count == 1
				? errors[0].Message
				: $"Missing {errors.Count} asset(s); first: {errors[0].Message}";
		return result;
	}

	/// <summary>
	/// Collect every asset a map document actually uses: the document's
	/// compile-visible brushes + entities, via
	/// <see cref="CompileInput.FromDocument(MapDocument)"/>. Same
	/// used-only / fail-loudly contract as the compile-input overload.
	/// </summary>
	public static MapPackResult GatherUsedAssets( MapDocument document, AssetRegistries registries )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		return GatherUsedAssets( CompileInput.FromDocument( document ), registries );
	}

	// -- Packing --------------------------------------------------------------

	/// <summary>
	/// Gather the used assets and build the package manifest. When
	/// <paramref name="packagePath"/> is non-blank the manifest is written
	/// there; when blank only the manifest text is built (the compile
	/// runner writes it as &lt;name&gt;.mappack). On a failed gather nothing
	/// is written and the result carries the named missing-asset issues.
	/// </summary>
	public static MapPackResult Pack( CompileInput input, AssetRegistries registries, string packagePath, CubemapBakeSet cubemaps = null )
	{
		if ( input is null ) throw new ArgumentNullException( nameof( input ) );
		var result = GatherUsedAssets( input, registries );
		result.Cubemaps = cubemaps;
		result.PackagePath = string.IsNullOrWhiteSpace( packagePath ) ? null : Path.GetFullPath( packagePath );
		if ( !result.Succeeded ) return result;
		result.Manifest = WriteText( input.Name, result.Assets, cubemaps );
		if ( result.PackagePath is not null )
		{
			Directory.CreateDirectory( Path.GetDirectoryName( result.PackagePath ) ?? "." );
			File.WriteAllText( result.PackagePath, result.Manifest );
		}
		return result;
	}

	/// <summary>Pack a map document: gather via <see cref="CompileInput.FromDocument(MapDocument)"/> then pack. Same fail-loudly contract.</summary>
	public static MapPackResult Pack( MapDocument document, AssetRegistries registries, string packagePath, CubemapBakeSet cubemaps = null )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		return Pack( CompileInput.FromDocument( document ), registries, packagePath, cubemaps );
	}

	/// <summary>GEPPETTO-MAPPACK 1 manifest text: header, map name, used-only counts, one line per asset (kind, key, path, ref count, first referrer), then the cubemap inventory when a set is supplied.</summary>
	public static string WriteText( string mapName, IReadOnlyList<MapPackAsset> assets, CubemapBakeSet cubemaps = null )
	{
		var list = ( assets ?? Array.Empty<MapPackAsset>() ).Where( a => a is not null ).ToList();
		list.Sort( ( a, b ) => a.Kind != b.Kind
			? a.Kind.CompareTo( b.Kind )
			: string.CompareOrdinal( a.Key, b.Key ) );
		var lines = new List<string>
		{
			FormatHeader,
			$"map {mapName ?? "map"}",
			$"assets {list.Count}",
			$"models {list.Count( a => a.Kind == MapPackAssetKind.Model )}",
			$"materials {list.Count( a => a.Kind == MapPackAssetKind.Material )}",
			$"sounds {list.Count( a => a.Kind == MapPackAssetKind.Sound )}",
			$"particles {list.Count( a => a.Kind == MapPackAssetKind.Particle )}",
		};
		foreach ( var asset in list )
			lines.Add( $"{KindName( asset.Kind )} {asset.Key} {DisplayPath( asset.Path )} refs {asset.ReferenceCount} from {asset.ReferencedBy}" );
		if ( cubemaps is not null )
		{
			lines.Add( $"cubemaps {cubemaps.ProbeCount}" );
			foreach ( var name in cubemaps.ProbeNames )
			{
				cubemaps.ProbeResolutions.TryGetValue( name, out var res );
				lines.Add( $"cubemap {name} resolution {res} faces 6 envmap {CubemapBake.ReferencePrefix}:{name}" );
			}
			foreach ( var face in cubemaps.Faces )
				lines.Add( $"cubemap-face {face.ProbeName} {face.Face} mean {F( face.Mean )}" );
		}
		return string.Join( "\n", lines ) + "\n";
	}

	/// <summary>Write a <see cref="WriteText"/> manifest file. Returns the full path written. A failed write throws — the caller decides how to report it.</summary>
	public static string WriteFile( string packagePath, string mapName, IReadOnlyList<MapPackAsset> assets, CubemapBakeSet cubemaps = null )
	{
		if ( string.IsNullOrWhiteSpace( packagePath ) ) throw new ArgumentException( "Package path is empty.", nameof( packagePath ) );
		var text = WriteText( mapName, assets, cubemaps );
		var full = Path.GetFullPath( packagePath );
		Directory.CreateDirectory( Path.GetDirectoryName( full ) ?? "." );
		File.WriteAllText( full, text );
		return full;
	}

	// -- Cubemap build --------------------------------------------------------

	/// <summary>
	/// Build cubemaps for a compile input: a registry from
	/// <see cref="CompileInput.Probes"/>, then
	/// <see cref="CubemapBake.Bake(CubemapProbeRegistry, CubemapBakeScene, CubemapBakeOptions, ICubemapCapture, IProgress{CubemapBakeProgress}, CancellationToken)"/>
	/// with the shipped CPU capture (a GPU capture plugs in later as a
	/// second <see cref="ICubemapCapture"/>). Duplicate probe names fail
	/// loudly as a named <c>duplicate-probe</c> issue instead of throwing.
	/// An empty probe list bakes an empty set (same as CubemapBake.Bake).
	/// </summary>
	public static CubemapBakeResult BuildCubemaps( CompileInput input, CubemapBakeOptions options = null, ICubemapCapture capture = null,
		IProgress<CubemapBakeProgress> progress = null, CancellationToken cancel = default )
	{
		if ( input is null ) throw new ArgumentNullException( nameof( input ) );
		var registry = new CubemapProbeRegistry();
		try
		{
			foreach ( var probe in input.Probes )
			{
				if ( probe is null ) continue;
				registry.Add( probe );
			}
		}
		catch ( CubemapBakeException ex )
		{
			var failed = new CubemapBakeResult { Error = ex.Message };
			failed.Issues.Add( new CompileIssue(
				string.Equals( ex.Code, CubemapBakeException.CodeDuplicateProbe, StringComparison.Ordinal )
					? CubemapBake.IssueCodes.DuplicateProbe
					: CubemapBake.IssueCodes.InvalidProbe,
				CompileSeverity.Error, ex.Message, "map", Vec3.Zero,
				"Give every env_cubemap a unique targetname and a power-of-two cubemapsize 16..256." ) );
			return failed;
		}
		return CubemapBake.Bake( registry, CubemapBakeScene.FromCompileInput( input ), options, capture, progress, cancel );
	}

	/// <summary>Build cubemaps for a map document, via <see cref="CompileInput.FromDocument(MapDocument)"/>. Same contract as the compile-input overload.</summary>
	public static CubemapBakeResult BuildCubemaps( MapDocument document, CubemapBakeOptions options = null, ICubemapCapture capture = null,
		IProgress<CubemapBakeProgress> progress = null, CancellationToken cancel = default )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		return BuildCubemaps( CompileInput.FromDocument( document ), options, capture, progress, cancel );
	}
}
