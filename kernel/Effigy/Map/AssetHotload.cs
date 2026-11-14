using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Effigy.Map;

/// <summary>
/// Hammer Desktop P2: model / material hotload (hammer-assets-hotload).
///
/// Polling-based hotload service (backend only, no UI, no Hammer/VBSP
/// shell-out, engine-free kernel code): watch registered asset files for
/// external change, reload the changed model / material / sound / particle
/// definition, re-validate every map reference to it (missing / broken =
/// a named issue), and report what changed.
///
/// API (exactly as the ticket specifies):
///   <see cref="AssetHotload.PollForChanges"/> — changed assets,
///   <see cref="AssetHotload.Reload(HotloadAsset)"/> — reload one asset,
///   <see cref="AssetHotload.RevalidateReferences"/> — named issues for
///   every map reference to one asset.
///
/// Dependency status at write time (2026-11-13, verified against the
/// tree): hammer-model-particle-sound-registries is still `todo` on the
/// board (no owner) and kernel/Effigy/Map/AssetRegistries.cs does NOT
/// exist, so there is no ModelRegistry / ParticleRegistry / SoundRegistry
/// type to build against. This file therefore builds against that
/// ticket's desc exactly as filed, using only its specified shapes:
///   - models: path, attachments, scale, prop type
///     (static / dynamic / physics);
///   - particles: system name, params;
///   - sounds: path, volume;
///   - lookup/search (substring + wildcard, Used / Favourites / Recent)
///     and placement via the entity API.
/// The seam is deliberately primitive-typed so the registries can wire
/// in with NO type changes here: a registry calls
/// <c>Watch(AssetKind.Model, model.Path)</c> per entry (and likewise for
/// Material / Sound / Particle), and feeds a <see cref="HotloadReloadResult"/>
/// back into its own registry reload. When AssetRegistries lands, the
/// integrator adds ONE thin adapter (registry entry -&gt; Watch call,
/// reload result -&gt; registry entry refresh); nothing in this file is
/// redefined by a sibling (a duplicate type would be a compile break —
/// reconcile, do not fork). The sibling's registry entry types keep
/// their own names; this file defines no ModelRegistryEntry /
/// ParticleRegistryEntry / SoundRegistryEntry.
/// <para/>
/// What "reload" proves, honestly: the built-in
/// <see cref="FileAssetLoader"/> re-reads the file, records a SHA-256
/// content hash + length + revision, and parses a definition where the
/// format is text and kernel-parseable — .vmt KeyValues pairs,
/// .json flat fields, .obj vertex / face counts, .txt key/value lines.
/// Binary formats (.mdl models, .wav / .mp3 / .ogg sounds, .pcf
/// particles) are hash + length only: the bytes changed, the definition
/// is the new bytes, and parsing them belongs to the registries / model
/// loader, not to hotload. A reload whose hash is identical to the
/// previous one (mtime-only touch) succeeds with
/// <see cref="HotloadReloadResult.DefinitionChanged"/> false and does NOT
/// bump <see cref="HotloadAsset.Revision"/>.
/// <para/>
/// Polling semantics: <see cref="PollForChanges"/> stats each watched
/// file (exists / LastWriteTimeUtc / Length) and reports each change
/// ONCE — Modified, Deleted, or Added (a missing file that reappears).
/// A second poll with no further edit reports nothing. Polling never
/// reads file contents, so it is cheap; the hash is taken at reload.
/// Polling is caller-driven and single-threaded by design (call it from
/// a background / idle tick, never from the render thread — see
/// handoffs/RULES.md). A platform file watcher may replace polling
/// later behind the same PollForChanges / Reload surface; callers must
/// not depend on poll timing.
/// <para/>
/// References: <see cref="HotloadDocument"/> is a minimal document
/// snapshot in the same spirit as IssueEngine's IssueDocument — the map
/// document model (hammer-map-document-model) has not landed, so there
/// is no MapDocument type to read references from yet. It carries one
/// <see cref="HotloadReference"/> per model / material / sound /
/// particle reference (entity / brush / face id + project-relative
/// path). When MapDocument lands, the integrator adds ONE thin adapter
/// projecting it onto HotloadDocument; the hotload logic does not
/// change. Revalidation checks existence on disk plus last-reload
/// health; it does NOT re-parse the map or the model.
/// <para/>
/// Named issues reuse <see cref="IssueSeverity"/> from IssueEngine and
/// stable machine codes for the auto-fix ticket to switch on:
/// MISSING_MODEL, MISSING_MATERIAL, MISSING_SOUND, MISSING_PARTICLE,
/// RELOAD_FAILED. Material references are tracked by file path
/// (e.g. "materials/metal.vmt"); the material-name -&gt; file mapping
/// belongs to MaterialBridge, not to hotload.
/// <para/>
/// Geometry / paths: asset paths are project-relative with forward
/// slashes, resolved under the hotload root directory. Absolute paths
/// and ".." segments escaping the root are rejected.
/// </summary>
public enum AssetKind
{
	Model,
	Material,
	Sound,
	Particle,
}

/// <summary>How a watched file changed since the last poll.</summary>
public enum HotloadChangeKind
{
	/// <summary>File exists and its mtime or length moved.</summary>
	Modified,
	/// <summary>File existed at the last poll and is gone now.</summary>
	Deleted,
	/// <summary>File was missing at the last poll and exists now.</summary>
	Added,
}

/// <summary>One watched asset: identity + last observed on-disk state +
/// last loaded definition. Created by
/// <see cref="AssetHotload.Watch"/>; never construct directly.</summary>
public sealed class HotloadAsset
{
	public AssetKind Kind;
	/// <summary>Project-relative path, forward slashes, as watched.</summary>
	public string Path = "";
	/// <summary>Absolute path under the hotload root.</summary>
	public string FullPath = "";
	public DateTime LastWriteTimeUtc;
	public long Length;
	public bool Exists;
	/// <summary>Bumps once per reload whose content hash changed.</summary>
	public int Revision;
	/// <summary>SHA-256 hex of the last loaded bytes, "" until loaded.</summary>
	public string ContentHash = "";
	/// <summary>Last successfully loaded definition, null until loaded.</summary>
	public HotloadDefinition? Definition;
	/// <summary>Last reload failure, "" when the last reload succeeded
	/// (or no reload has been attempted).</summary>
	public string LastReloadError = "";
	public DateTime LastReloadAtUtc;

	public override string ToString() =>
		$"{Kind} {Path} rev {Revision}" + ( Exists ? "" : " (missing)" );
}

/// <summary>One change reported by
/// <see cref="AssetHotload.PollForChanges"/>.</summary>
public sealed class HotloadChange
{
	public HotloadAsset Asset = null!;
	public HotloadChangeKind Kind;
	public DateTime DetectedAtUtc;

	public override string ToString() => $"{Kind} {Asset.Kind} {Asset.Path}";
}

/// <summary>The re-read definition of one asset: content hash, parsed
/// text fields where the format allows, and .obj geometry counts.
/// Binary formats carry hash + length only (see the file header).</summary>
public sealed class HotloadDefinition
{
	public AssetKind Kind;
	public string Path = "";
	public int Revision;
	public string ContentHash = "";
	public long Length;
	public DateTime LoadedAtUtc;
	/// <summary>Parsed text fields (case-insensitive keys): .vmt
	/// KeyValues pairs, .json top-level scalars, .txt key/value lines,
	/// .obj "vertices" / "faces" / "object". Empty for binary formats.</summary>
	public Dictionary<string, string> Fields = new( StringComparer.OrdinalIgnoreCase );
	/// <summary>.obj vertex count, -1 for other formats.</summary>
	public int VertexCount = -1;
	/// <summary>.obj face count, -1 for other formats.</summary>
	public int FaceCount = -1;
	/// <summary>Load failure, "" on success.</summary>
	public string Error = "";
}

/// <summary>Outcome of <see cref="AssetHotload.Reload"/>.</summary>
public sealed class HotloadReloadResult
{
	public HotloadAsset Asset = null!;
	public bool Success;
	/// <summary>New definition on success; the previous definition
	/// (stale) on failure — reload never clears a good definition.</summary>
	public HotloadDefinition? Definition;
	/// <summary>True when the reloaded bytes differ from the previous
	/// load. A mtime-only touch reloads successfully with false.</summary>
	public bool DefinitionChanged;
	public string Error = "";
	/// <summary>What the poll saw for this asset, when the reload was
	/// driven by a poll change.</summary>
	public HotloadChangeKind? ChangeKind;
}

/// <summary>One named hotload problem for a map reference. Codes are
/// stable machine strings for the auto-fix ticket:
/// MISSING_MODEL, MISSING_MATERIAL, MISSING_SOUND, MISSING_PARTICLE,
/// RELOAD_FAILED.</summary>
public sealed class HotloadIssue
{
	public string Code = "";
	public IssueSeverity Severity = IssueSeverity.Error;
	public string Message = "";
	public string FixHint = "";
	public string AssetPath = "";
	public AssetKind Kind;
	/// <summary>Referencing entity, or -1.</summary>
	public int EntityId = -1;
	/// <summary>Referencing brush, or -1.</summary>
	public int BrushId = -1;
	/// <summary>Referencing face, or -1.</summary>
	public int FaceId = -1;

	public override string ToString() =>
		$"[{Severity} {Code}] {Message}" + ( string.IsNullOrEmpty( FixHint ) ? "" : $" (fix: {FixHint})" );
}

/// <summary>One map reference to one asset file (model on a prop
/// entity, material on a brush face, sound / particle on an entity).
/// Minimal snapshot — see the file header.</summary>
public sealed class HotloadReference
{
	public AssetKind Kind;
	/// <summary>Project-relative asset path, forward slashes.</summary>
	public string Path = "";
	public int EntityId = -1;
	public int BrushId = -1;
	public int FaceId = -1;
	public string Description = "";
}

/// <summary>Minimal document snapshot for
/// <see cref="AssetHotload.RevalidateReferences"/>: the list of asset
/// references in the open map. The map document model has not landed;
/// when it does, the integrator projects it onto this shape.</summary>
public sealed class HotloadDocument
{
	public List<HotloadReference> References = new();

	public void AddModelReference( string path, int entityId, string description = "" ) =>
		References.Add( new HotloadReference { Kind = AssetKind.Model, Path = path, EntityId = entityId, Description = description } );

	public void AddMaterialReference( string path, int brushId, int faceId, string description = "" ) =>
		References.Add( new HotloadReference { Kind = AssetKind.Material, Path = path, BrushId = brushId, FaceId = faceId, Description = description } );

	public void AddSoundReference( string path, int entityId, string description = "" ) =>
		References.Add( new HotloadReference { Kind = AssetKind.Sound, Path = path, EntityId = entityId, Description = description } );

	public void AddParticleReference( string path, int entityId, string description = "" ) =>
		References.Add( new HotloadReference { Kind = AssetKind.Particle, Path = path, EntityId = entityId, Description = description } );

	/// <summary>References to one asset, matched on kind + normalized
	/// project-relative path (case-insensitive).</summary>
	public IReadOnlyList<HotloadReference> ReferencesTo( AssetKind kind, string path )
	{
		var normalized = AssetHotload.NormalizePath( path );
		return References
			.Where( r => r.Kind == kind && string.Equals( AssetHotload.NormalizePath( r.Path ), normalized, StringComparison.OrdinalIgnoreCase ) )
			.ToList();
	}
}

/// <summary>Reloads one asset's definition from disk. The registries
/// supply their own loader when they land (model / particle parsing
/// lives there); <see cref="FileAssetLoader"/> is the built-in
/// file + hash + text-format loader.</summary>
public interface IAssetLoader
{
	/// <summary>Reads <paramref name="asset"/>.FullPath. Returns false
	/// (with <paramref name="error"/>) when the file cannot be read or
	/// parsed; <paramref name="definition"/> still carries what was
	/// learned (hash / error) on failure.</summary>
	bool TryLoad( HotloadAsset asset, out HotloadDefinition definition, out string error );
}

/// <summary>Built-in loader: SHA-256 + length for every file, plus
/// parsed text fields for .vmt (KeyValues pairs), .json (top-level
/// scalars), .obj (vertex / face counts + object name) and .txt
/// (key/value lines). Binary formats (.mdl, .wav, .mp3, .ogg, .pcf)
/// are hash + length only — see the file header.</summary>
public sealed class FileAssetLoader : IAssetLoader
{
	static readonly Regex VmtPair = new( "\"([^\"]+)\"\\s+\"([^\"]*)\"", RegexOptions.Compiled );

	public bool TryLoad( HotloadAsset asset, out HotloadDefinition definition, out string error )
	{
		definition = new HotloadDefinition { Kind = asset.Kind, Path = asset.Path, Revision = asset.Revision + 1, LoadedAtUtc = DateTime.UtcNow };
		error = "";
		byte[] bytes;
		try { bytes = File.ReadAllBytes( asset.FullPath ); }
		catch ( Exception ex ) { error = ex.Message; definition.Error = error; return false; }

		definition.Length = bytes.Length;
		definition.ContentHash = Convert.ToHexString( SHA256.HashData( bytes ) ).ToLowerInvariant();
		var ext = Path.GetExtension( asset.FullPath ).ToLowerInvariant();
		try
		{
			switch ( ext )
			{
				case ".vmt": ParseVmt( Encoding.UTF8.GetString( bytes ), definition ); break;
				case ".json": ParseJson( Encoding.UTF8.GetString( bytes ), definition ); break;
				case ".obj": ParseObj( Encoding.UTF8.GetString( bytes ), definition ); break;
				case ".txt": ParseKeyValueLines( Encoding.UTF8.GetString( bytes ), definition ); break;
			}
		}
		catch ( Exception ex ) { error = ex.Message; definition.Error = error; return false; }
		return true;
	}

	static void ParseVmt( string text, HotloadDefinition definition )
	{
		foreach ( var rawLine in text.Split( '\n' ) )
		{
			var line = rawLine;
			var comment = line.IndexOf( "//", StringComparison.Ordinal );
			if ( comment >= 0 ) line = line.Substring( 0, comment );
			foreach ( Match m in VmtPair.Matches( line ) )
				definition.Fields[m.Groups[1].Value] = m.Groups[2].Value;
		}
	}

	static void ParseJson( string text, HotloadDefinition definition )
	{
		using var doc = JsonDocument.Parse( text );
		if ( doc.RootElement.ValueKind != JsonValueKind.Object )
			throw new JsonException( "Particle / sound definition must be a JSON object." );
		foreach ( var property in doc.RootElement.EnumerateObject() )
		{
			switch ( property.Value.ValueKind )
			{
				case JsonValueKind.String: definition.Fields[property.Name] = property.Value.GetString() ?? ""; break;
				case JsonValueKind.Number:
				case JsonValueKind.True:
				case JsonValueKind.False: definition.Fields[property.Name] = property.Value.GetRawText(); break;
			}
		}
	}

	static void ParseObj( string text, HotloadDefinition definition )
	{
		var vertices = 0;
		var faces = 0;
		foreach ( var rawLine in text.Split( '\n' ) )
		{
			var line = rawLine.Trim();
			if ( line.StartsWith( "v ", StringComparison.Ordinal ) ) vertices++;
			else if ( line.StartsWith( "f ", StringComparison.Ordinal ) ) faces++;
			else if ( line.StartsWith( "o ", StringComparison.Ordinal ) && !definition.Fields.ContainsKey( "object" ) )
				definition.Fields["object"] = line.Substring( 2 ).Trim();
		}
		definition.VertexCount = vertices;
		definition.FaceCount = faces;
		definition.Fields["vertices"] = vertices.ToString( System.Globalization.CultureInfo.InvariantCulture );
		definition.Fields["faces"] = faces.ToString( System.Globalization.CultureInfo.InvariantCulture );
	}

	static void ParseKeyValueLines( string text, HotloadDefinition definition )
	{
		foreach ( var rawLine in text.Split( '\n' ) )
		{
			var line = rawLine.Trim();
			if ( line.Length == 0 || line.StartsWith( "#", StringComparison.Ordinal ) || line.StartsWith( "//", StringComparison.Ordinal ) ) continue;
			var equals = line.IndexOf( '=' );
			if ( equals > 0 ) { definition.Fields[line.Substring( 0, equals ).Trim()] = line.Substring( equals + 1 ).Trim(); continue; }
			var space = line.IndexOfAny( new[] { ' ', '\t' } );
			if ( space > 0 ) definition.Fields[line.Substring( 0, space ).Trim()] = line.Substring( space + 1 ).Trim();
		}
	}
}

/// <summary>One poll + reload + revalidate pass, for callers that want
/// a single "what changed" report.</summary>
public sealed class HotloadReport
{
	public IReadOnlyList<HotloadChange> Changes = Array.Empty<HotloadChange>();
	public IReadOnlyList<HotloadReloadResult> Reloads = Array.Empty<HotloadReloadResult>();
	public IReadOnlyList<HotloadIssue> Issues = Array.Empty<HotloadIssue>();
	public bool HasIssues => Issues.Count > 0;

	public override string ToString() =>
		$"{Changes.Count} changed, {Reloads.Count( r => r.Success )} reloaded, {Issues.Count} issue(s)";
}

/// <summary>Polling hotload service. One instance watches the asset
/// files of one project root; bind a <see cref="HotloadDocument"/> for
/// revalidation. Caller-driven, single-threaded: call
/// <see cref="PollForChanges"/> from a background / idle tick, never
/// from the render thread. Zero UI code.</summary>
public sealed class AssetHotload
{
	readonly string _root;
	readonly IAssetLoader _loader;
	readonly Dictionary<string, HotloadAsset> _watched = new( StringComparer.OrdinalIgnoreCase );

	public AssetHotload( string rootDirectory, HotloadDocument? document = null, IAssetLoader? loader = null )
	{
		if ( string.IsNullOrWhiteSpace( rootDirectory ) ) throw new ArgumentException( "A project root directory is required.", nameof( rootDirectory ) );
		_root = Path.GetFullPath( rootDirectory );
		if ( !Directory.Exists( _root ) ) throw new DirectoryNotFoundException( $"Hotload root does not exist: {_root}" );
		Document = document;
		_loader = loader ?? new FileAssetLoader();
	}

	public string RootDirectory => _root;

	/// <summary>Document whose references
	/// <see cref="RevalidateReferences"/> checks. Null until bound —
	/// revalidation throws until then.</summary>
	public HotloadDocument? Document { get; set; }

	public IReadOnlyList<HotloadAsset> WatchedAssets => _watched.Values.ToList();

	/// <summary>Stable machine code for a missing asset of this kind
	/// (for the auto-fix ticket to switch on).</summary>
	public static string MissingIssueCode( AssetKind kind ) => kind switch
	{
		AssetKind.Model => "MISSING_MODEL",
		AssetKind.Material => "MISSING_MATERIAL",
		AssetKind.Sound => "MISSING_SOUND",
		AssetKind.Particle => "MISSING_PARTICLE",
		_ => "MISSING_ASSET",
	};

	/// <summary>Normalizes a project-relative path to forward slashes
	/// with "." segments collapsed. Rejects absolute paths and ".."
	/// segments that would escape the project root.</summary>
	public static string NormalizePath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) throw new ArgumentException( "An asset path is required.", nameof( path ) );
		if ( path.IndexOf( '\0' ) >= 0 ) throw new ArgumentException( "Asset path contains a NUL character.", nameof( path ) );
		var flat = path.Replace( '\\', '/' ).Trim();
		if ( flat.StartsWith( "/", StringComparison.Ordinal ) || flat.Length >= 2 && flat[1] == ':' )
			throw new ArgumentException( $"Asset path must be project-relative, not absolute: '{path}'", nameof( path ) );
		var kept = new List<string>();
		foreach ( var segment in flat.Split( '/' ) )
		{
			if ( segment.Length == 0 || segment == "." ) continue;
			if ( segment == ".." )
			{
				if ( kept.Count == 0 ) throw new ArgumentException( $"Asset path escapes the project root: '{path}'", nameof( path ) );
				kept.RemoveAt( kept.Count - 1 );
				continue;
			}
			kept.Add( segment );
		}
		if ( kept.Count == 0 ) throw new ArgumentException( $"Asset path names no file: '{path}'", nameof( path ) );
		return string.Join( "/", kept );
	}

	static string Key( AssetKind kind, string normalizedPath ) => $"{kind}|{normalizedPath}";

	/// <summary>Starts watching one asset file. Watching the same
	/// kind + path twice returns the existing entry (no duplicate).
	/// The initial snapshot is taken now, so a file that already
	/// exists is NOT reported as changed by the first poll.</summary>
	public HotloadAsset Watch( AssetKind kind, string projectRelativePath )
	{
		var normalized = NormalizePath( projectRelativePath );
		var key = Key( kind, normalized );
		if ( _watched.TryGetValue( key, out var existing ) ) return existing;
		var full = Path.Combine( _root, normalized.Replace( '/', Path.DirectorySeparatorChar ) );
		var asset = new HotloadAsset { Kind = kind, Path = normalized, FullPath = full };
		RefreshSnapshot( asset );
		_watched[key] = asset;
		return asset;
	}

	/// <summary>Stops watching one asset. Returns false when it was
	/// not watched.</summary>
	public bool Unwatch( AssetKind kind, string projectRelativePath ) =>
		_watched.Remove( Key( kind, NormalizePath( projectRelativePath ) ) );

	public HotloadAsset? TryGetAsset( AssetKind kind, string projectRelativePath ) =>
		_watched.TryGetValue( Key( kind, NormalizePath( projectRelativePath ) ), out var asset ) ? asset : null;

	static void RefreshSnapshot( HotloadAsset asset )
	{
		if ( !File.Exists( asset.FullPath ) ) { asset.Exists = false; return; }
		var info = new FileInfo( asset.FullPath );
		asset.Exists = true;
		asset.LastWriteTimeUtc = info.LastWriteTimeUtc;
		asset.Length = info.Length;
	}

	/// <summary>Reports every watched file whose existence, mtime or
	/// length moved since the last poll (or since
	/// <see cref="Watch"/>). Each change is reported ONCE; a second
	/// poll with no further edit reports nothing. Never reads file
	/// contents. A file that cannot be statted is skipped, not fatal.</summary>
	public IReadOnlyList<HotloadChange> PollForChanges()
	{
		var changes = new List<HotloadChange>();
		foreach ( var asset in _watched.Values )
		{
			bool exists;
			DateTime mtime;
			long length;
			try
			{
				exists = File.Exists( asset.FullPath );
				if ( !exists ) { mtime = default; length = 0; }
				else
				{
					var info = new FileInfo( asset.FullPath );
					mtime = info.LastWriteTimeUtc;
					length = info.Length;
				}
			}
			catch ( IOException ) { continue; }
			catch ( UnauthorizedAccessException ) { continue; }

			HotloadChangeKind? kind = null;
			if ( asset.Exists && !exists ) kind = HotloadChangeKind.Deleted;
			else if ( !asset.Exists && exists ) kind = HotloadChangeKind.Added;
			else if ( exists && ( mtime != asset.LastWriteTimeUtc || length != asset.Length ) ) kind = HotloadChangeKind.Modified;
			if ( kind is null ) continue;

			asset.Exists = exists;
			asset.LastWriteTimeUtc = mtime;
			asset.Length = length;
			changes.Add( new HotloadChange { Asset = asset, Kind = kind.Value, DetectedAtUtc = DateTime.UtcNow } );
		}
		return changes;
	}

	/// <summary>Reloads one watched asset from disk. Throws
	/// <see cref="ArgumentException"/> for an asset this service does
	/// not watch. A deleted file fails (error set, stale definition
	/// kept); a mtime-only touch succeeds with
	/// <see cref="HotloadReloadResult.DefinitionChanged"/> false and no
	/// revision bump.</summary>
	public HotloadReloadResult Reload( HotloadAsset asset )
	{
		if ( asset is null ) throw new ArgumentNullException( nameof( asset ) );
		if ( !_watched.TryGetValue( Key( asset.Kind, asset.Path ), out var watched ) || !ReferenceEquals( watched, asset ) )
			throw new ArgumentException( $"Asset is not watched by this service: {asset.Kind} {asset.Path}", nameof( asset ) );
		return ReloadCore( asset, null );
	}

	/// <summary>Reloads a watched asset by kind + path.</summary>
	public HotloadReloadResult Reload( AssetKind kind, string projectRelativePath )
	{
		var asset = TryGetAsset( kind, projectRelativePath )
			?? throw new ArgumentException( $"Asset is not watched by this service: {kind} {NormalizePath( projectRelativePath )}", nameof( projectRelativePath ) );
		return ReloadCore( asset, null );
	}

	HotloadReloadResult ReloadCore( HotloadAsset asset, HotloadChangeKind? changeKind )
	{
		var result = new HotloadReloadResult { Asset = asset, ChangeKind = changeKind };
		if ( !File.Exists( asset.FullPath ) )
		{
			asset.Exists = false;
			asset.LastReloadError = $"File not found: {asset.Path}";
			result.Definition = asset.Definition;
			result.Error = asset.LastReloadError;
			return result;
		}
		if ( !_loader.TryLoad( asset, out var definition, out var error ) )
		{
			asset.LastReloadError = error;
			asset.LastReloadAtUtc = DateTime.UtcNow;
			result.Definition = asset.Definition;
			result.Error = error;
			return result;
		}

		result.DefinitionChanged = asset.Definition is null || !string.Equals( definition.ContentHash, asset.ContentHash, StringComparison.Ordinal );
		definition.Revision = result.DefinitionChanged ? asset.Revision + 1 : asset.Revision;
		if ( result.DefinitionChanged ) asset.Revision = definition.Revision;
		asset.Definition = definition;
		asset.ContentHash = definition.ContentHash;
		asset.LastReloadError = "";
		asset.LastReloadAtUtc = DateTime.UtcNow;
		RefreshSnapshot( asset );
		result.Success = true;
		result.Definition = definition;
		return result;
	}

	/// <summary>Polls, reloads every reported change (a Deleted asset
	/// reloads as a failed result with its file-not-found error), then
	/// re-validates every changed asset against the bound document
	/// (skipped when no document is bound).</summary>
	public HotloadReport PollAndReload()
	{
		var changes = PollForChanges();
		var reloads = new List<HotloadReloadResult>();
		var issues = new List<HotloadIssue>();
		foreach ( var change in changes )
		{
			reloads.Add( ReloadCore( change.Asset, change.Kind ) );
			if ( Document is not null ) issues.AddRange( RevalidateReferences( change.Asset ) );
		}
		return new HotloadReport { Changes = changes, Reloads = reloads, Issues = issues };
	}

	/// <summary>Re-validates every map reference to one asset and
	/// returns one named issue per broken reference (missing file, or
	/// last reload failed). Clean references return no issues. Throws
	/// <see cref="InvalidOperationException"/> when no document is
	/// bound.</summary>
	public IReadOnlyList<HotloadIssue> RevalidateReferences( HotloadAsset asset )
	{
		if ( asset is null ) throw new ArgumentNullException( nameof( asset ) );
		if ( Document is null ) throw new InvalidOperationException( "No document is bound: bind a HotloadDocument before revalidating references." );
		var references = Document.ReferencesTo( asset.Kind, asset.Path );
		var issues = new List<HotloadIssue>();
		if ( references.Count == 0 ) return issues;
		if ( !File.Exists( asset.FullPath ) )
		{
			foreach ( var reference in references )
				issues.Add( MissingIssue( asset, reference, $"Referenced {asset.Kind.ToString().ToLowerInvariant()} file is missing on disk." ) );
			return issues;
		}
		if ( asset.LastReloadError.Length > 0 )
		{
			foreach ( var reference in references )
				issues.Add( new HotloadIssue
				{
					Code = "RELOAD_FAILED",
					Severity = IssueSeverity.Error,
					Message = $"Referenced {asset.Kind.ToString().ToLowerInvariant()} '{asset.Path}' failed to reload: {asset.LastReloadError}",
					FixHint = "Fix the file on disk (see the reload error) or re-point the reference to a valid asset.",
					AssetPath = asset.Path,
					Kind = asset.Kind,
					EntityId = reference.EntityId,
					BrushId = reference.BrushId,
					FaceId = reference.FaceId,
				} );
		}
		return issues;
	}

	/// <summary>Re-validates every watched asset; the union of
	/// <see cref="RevalidateReferences"/> over all of them.</summary>
	public IReadOnlyList<HotloadIssue> RevalidateAll()
	{
		if ( Document is null ) throw new InvalidOperationException( "No document is bound: bind a HotloadDocument before revalidating references." );
		var issues = new List<HotloadIssue>();
		foreach ( var asset in _watched.Values ) issues.AddRange( RevalidateReferences( asset ) );
		return issues;
	}

	static HotloadIssue MissingIssue( HotloadAsset asset, HotloadReference reference, string what )
	{
		var kindName = asset.Kind.ToString().ToLowerInvariant();
		return new HotloadIssue
		{
			Code = MissingIssueCode( asset.Kind ),
			Severity = IssueSeverity.Error,
			Message = $"Referenced {kindName} '{asset.Path}' is missing. {what}",
			FixHint = $"Restore the file at '{asset.Path}' or re-point the reference to an existing asset.",
			AssetPath = asset.Path,
			Kind = asset.Kind,
			EntityId = reference.EntityId,
			BrushId = reference.BrushId,
			FaceId = reference.FaceId,
		};
	}
}
