#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Godlike Wave 3 P2 — Plugin SDK v1 + store (collaboration half lives in
/// <see cref="TextSceneDocument"/>, <see cref="AssetLock"/> and
/// <see cref="TurntableViewer"/>).
///
/// What this is: the versioned, signed, capability-gated way for a third party
/// to ship a brush/material/io plug-in WITHOUT touching the kernel. It is the
/// follow-on to board tasks sync-script-and-plugin-parity (kernel PluginManager
/// parity only) and expose-geonodes-via-bot-commands (bot exposure only).
///
/// The kernel's <see cref="PluginManager"/> loads any DLL with an
/// <see cref="IPlugin"/> and calls parameterless OnLoad() — a loaded assembly
/// can do anything the process can. This SDK does NOT change the kernel. It
/// adds a host-side gate in front of it:
///
///   1. An add-on is a directory: addon.json (manifest) + its files.
///   2. The manifest is signed (HMAC-SHA256 over the canonical manifest
///      without the signature, keyed by the publisher's key). Unsigned
///      add-ons are rejected; tampered files are rejected (per-file SHA-256).
///      V1 signing is tamper-evidence against a trusted-publisher key list
///      held by the host/store — it is NOT a PKI code-signing chain, and
///      must never be described as one. PKI is the v2 follow-on.
///   3. The manifest declares the API version it needs and the capabilities
///      it wants. The host accepts only the same API major with a host minor
///      &gt;= the required minor, and only capabilities on the grantable list.
///      "kernel", "network", "shell" and anything unlisted are never
///      grantable in v1 and the install is refused.
///   4. A loaded plug-in receives ONLY an <see cref="IPluginApi"/>: logging,
///      project-root-scoped asset text read/write, and brush/material/io
///      registration into host-owned lists. No kernel types cross the API.
///      V1 sandboxing is install-time capability gating + API-surface
///      limiting. It is NOT process/AppDomain isolation: a malicious DLL in
///      the same process could still reflect. The boundary is that only
///      signed, capability-checked add-ons are loaded through this host.
///   5. One-click install = <see cref="AddonInstaller.Install"/>: validate,
///      then copy into addons/&lt;id&gt;-&lt;version&gt;/ and record it in
///      installed.json. No server in v1; the store is a local JSON catalog
///      (<see cref="AddonStoreCatalog"/>) with text search.
///
/// Out of scope here (do not widen): the USD layers per artist/department
/// (godlike-usd-stage-alembic-materialx owns USD; <see cref="TextSceneDocument"/>
/// layers map 1:1 onto USD layers when it lands), the in-app Geometry Nodes
/// graph editor (scoped separately in
/// handoffs/plugin-sdk-geonodes-editor-scope.md), and any store server.
/// </summary>
public static class PluginSdk
{
	/// <summary>The API this host implements. Bump Major only on a breaking
	/// change; Minor grows with additive API.</summary>
	public static readonly PluginApiVersion CurrentApi = new( 1, 0 );

	/// <summary>Capabilities the host can ever grant in v1. Anything else in
	/// a manifest — "kernel", "network", "shell", "process", ... — is refused.</summary>
	public static readonly IReadOnlyList<string> GrantableCapabilities = new[]
	{
		"assets.read", "assets.write",
		"brush.register", "material.register", "io.register",
		"log",
	};
}

/// <summary>An API version: major must match exactly, host minor must be
/// &gt;= the required minor.</summary>
public sealed record PluginApiVersion( int Major, int Minor )
{
	public bool SatisfiedBy( PluginApiVersion host ) =>
		host.Major == Major && host.Minor >= Minor;

	public override string ToString() => $"{Major}.{Minor}";
}

/// <summary>What an add-on is: brush, material, io, or a general tool.</summary>
public static class AddonKind
{
	public const string Brush = "brush";
	public const string Material = "material";
	public const string Io = "io";
	public const string Tool = "tool";
}

/// <summary>One file inside an add-on package, with its SHA-256 so a tampered
/// file fails install.</summary>
public sealed class AddonFile
{
	public string Path { get; set; } = "";
	public string Sha256 { get; set; } = "";
}

/// <summary>The add-on manifest (addon.json). Serialized camelCase; the
/// on-disk bytes may be indented — what gets signed is the canonical form
/// (<see cref="AddonSignature.CanonicalJson"/>: sorted keys, compact JSON),
/// so re-serializing never breaks a signature.</summary>
public sealed class AddonManifest
{
	public string Id { get; set; } = "";
	public string Name { get; set; } = "";
	public string Version { get; set; } = "";
	public string Author { get; set; } = "";
	public string Summary { get; set; } = "";
	public string Kind { get; set; } = AddonKind.Tool;
	public List<string> Tags { get; set; } = new();
	public string PublisherId { get; set; } = "";
	public int RequiredApiMajor { get; set; } = 1;
	public int RequiredApiMinor { get; set; } = 0;
	/// <summary>Assembly-qualified type name of the <see cref="ISdkPlugin"/>
	/// entry point inside the add-on's DLL.</summary>
	public string EntryPoint { get; set; } = "";
	public List<string> Capabilities { get; set; } = new();
	public List<AddonFile> Files { get; set; } = new();
	/// <summary>HMAC-SHA256 hex over the canonical manifest without this
	/// field. Null/empty means unsigned — always rejected.</summary>
	[JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
	public string? Signature { get; set; }

	public AddonManifest WithoutSignature() => new()
	{
		Id = Id, Name = Name, Version = Version, Author = Author, Summary = Summary,
		Kind = Kind, Tags = new List<string>( Tags ), PublisherId = PublisherId,
		RequiredApiMajor = RequiredApiMajor, RequiredApiMinor = RequiredApiMinor,
		EntryPoint = EntryPoint, Capabilities = new List<string>( Capabilities ),
		Files = Files.Select( f => new AddonFile { Path = f.Path, Sha256 = f.Sha256 } ).ToList(),
		Signature = null,
	};
}

/// <summary>Signing: HMAC-SHA256 over the canonical manifest (no signature
/// field) with the publisher's key bytes. The host holds the trusted
/// publisher keys; a manifest whose PublisherId is unknown, or whose
/// signature does not recompute, is rejected.</summary>
public static class AddonSignature
{
	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	/// <summary>Canonical form: sorted keys, compact JSON — the same form
	/// the Python API (tools/plugin-sdk/geppetto_plugin.py) uses, so a
	/// manifest signed on one side verifies on the other: sorted keys,
	/// compact JSON, raw UTF-8 text (no HTML escaping, no \uXXXX escapes).
	/// The C# side uses UnsafeRelaxedJsonEscaping and the Python side
	/// ensure_ascii=False for exactly this reason.</summary>
	public static string CanonicalJson( AddonManifest manifest )
	{
		var node = JsonSerializer.SerializeToNode( manifest.WithoutSignature(), JsonOptions );
		// SerializeToNode never returns null for a non-null manifest.
		return SortKeys( node )!.ToJsonString( new JsonSerializerOptions
		{
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		} );
	}

	static JsonNode? SortKeys( JsonNode? node )
	{
		switch ( node )
		{
			case JsonObject obj:
			{
				var sorted = new JsonObject();
				foreach ( var kv in obj.OrderBy( kv => kv.Key, StringComparer.Ordinal ) )
					sorted[kv.Key] = SortKeys( kv.Value );
				return sorted;
			}
			case JsonArray arr:
			{
				var sorted = new JsonArray();
				foreach ( var item in arr ) sorted.Add( SortKeys( item ) );
				return sorted;
			}
			default:
				return node?.DeepClone();
		}
	}

	public static string Sign( AddonManifest manifest, byte[] publisherKey )
	{
		ArgumentNullException.ThrowIfNull( publisherKey );
		using var hmac = new HMACSHA256( publisherKey );
		var bytes = hmac.ComputeHash( Encoding.UTF8.GetBytes( CanonicalJson( manifest ) ) );
		return Convert.ToHexString( bytes ).ToLowerInvariant();
	}

	public static bool Verify( AddonManifest manifest, byte[] publisherKey )
	{
		if ( string.IsNullOrWhiteSpace( manifest.Signature ) ) return false;
		var expected = Sign( manifest, publisherKey );
		return CryptographicOperations.FixedTimeEquals(
			Encoding.UTF8.GetBytes( expected ),
			Encoding.UTF8.GetBytes( manifest.Signature ) );
	}

	public static string Sha256Hex( byte[] bytes )
	{
		using var sha = SHA256.Create();
		return Convert.ToHexString( sha.ComputeHash( bytes ) ).ToLowerInvariant();
	}

	public static string Sha256HexFile( string path )
	{
		using var stream = File.OpenRead( path );
		using var sha = SHA256.Create();
		return Convert.ToHexString( sha.ComputeHash( stream ) ).ToLowerInvariant();
	}
}

/// <summary>Why an add-on package failed validation.</summary>
public sealed record AddonValidation( bool Ok, IReadOnlyList<string> Errors )
{
	public static AddonValidation Pass() => new( true, Array.Empty<string>() );
	public static AddonValidation Fail( params string[] errors ) => new( false, errors );
}

/// <summary>An add-on package on disk: a directory holding addon.json plus
/// the files the manifest lists. Validates signature, API version,
/// capabilities, entry point and per-file hashes — nothing is copied or
/// loaded before this passes.</summary>
public static class AddonPackage
{
	public const string ManifestFileName = "addon.json";

	public static AddonManifest? ReadManifest( string packageDir, out string? error )
	{
		error = null;
		var path = Path.Combine( packageDir, ManifestFileName );
		if ( !File.Exists( path ) ) { error = $"Add-on package has no {ManifestFileName}."; return null; }
		try
		{
			var manifest = JsonSerializer.Deserialize<AddonManifest>( File.ReadAllText( path ), new JsonSerializerOptions
			{
				PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
				PropertyNameCaseInsensitive = true,
			} );
			if ( manifest is null ) { error = "addon.json is empty."; return null; }
			return manifest;
		}
		catch ( Exception e ) { error = $"addon.json is malformed: {e.Message}"; return null; }
	}

	/// <summary>Full gate: manifest present, signature valid for a trusted
	/// publisher, API compatible with this host, only grantable capabilities,
	/// entry point named, every listed file present with a matching hash.</summary>
	public static AddonValidation Validate(
		string packageDir,
		IReadOnlyDictionary<string, byte[]> trustedPublishers,
		PluginApiVersion hostApi )
	{
		var errors = new List<string>();
		var manifest = ReadManifest( packageDir, out var readError );
		if ( manifest is null ) return AddonValidation.Fail( readError ?? "addon.json could not be read." );

		if ( string.IsNullOrWhiteSpace( manifest.Id ) ) errors.Add( "Manifest id is required." );
		if ( string.IsNullOrWhiteSpace( manifest.Name ) ) errors.Add( "Manifest name is required." );
		if ( string.IsNullOrWhiteSpace( manifest.Version ) ) errors.Add( "Manifest version is required." );
		if ( string.IsNullOrWhiteSpace( manifest.EntryPoint ) ) errors.Add( "Manifest entry point is required." );
		if ( string.IsNullOrWhiteSpace( manifest.PublisherId ) ) errors.Add( "Manifest publisher id is required." );

		if ( string.IsNullOrWhiteSpace( manifest.Signature ) )
		{
			errors.Add( "Unsigned add-on is rejected: manifest has no signature." );
		}
		else if ( !trustedPublishers.TryGetValue( manifest.PublisherId, out var key ) )
		{
			errors.Add( $"Unknown publisher '{manifest.PublisherId}': not in the trusted publisher list." );
		}
		else if ( !AddonSignature.Verify( manifest, key ) )
		{
			errors.Add( "Signature does not match the manifest: the add-on was tampered with or signed with another key." );
		}

		var required = new PluginApiVersion( manifest.RequiredApiMajor, manifest.RequiredApiMinor );
		if ( !required.SatisfiedBy( hostApi ) )
			errors.Add( $"Add-on needs API {required}, this host implements {hostApi}." );

		foreach ( var capability in manifest.Capabilities )
			if ( !PluginSdk.GrantableCapabilities.Contains( capability, StringComparer.Ordinal ) )
				errors.Add( $"Capability '{capability}' is never granted in v1." );

		foreach ( var file in manifest.Files )
		{
			var full = SafeJoin( packageDir, file.Path );
			if ( full is null ) { errors.Add( $"File '{file.Path}' escapes the package directory." ); continue; }
			if ( !File.Exists( full ) ) { errors.Add( $"Listed file '{file.Path}' is missing from the package." ); continue; }
			var actual = AddonSignature.Sha256HexFile( full );
			if ( !string.Equals( actual, file.Sha256, StringComparison.OrdinalIgnoreCase ) )
				errors.Add( $"File '{file.Path}' hash mismatch: package was tampered with." );
		}

		return errors.Count == 0 ? AddonValidation.Pass() : AddonValidation.Fail( errors.ToArray() );
	}

	/// <summary>Join a manifest-listed relative path under a root, refusing
	/// absolute paths and ".." escapes. Null when unsafe.</summary>
	public static string? SafeJoin( string root, string relativePath )
	{
		if ( string.IsNullOrWhiteSpace( relativePath ) ) return null;
		if ( Path.IsPathRooted( relativePath ) ) return null;
		var rootFull = Path.GetFullPath( root );
		var full = Path.GetFullPath( Path.Combine( rootFull, relativePath ) );
		return full.StartsWith( rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal )
			|| full.Equals( rootFull, StringComparison.Ordinal ) ? full : null;
	}
}

/// <summary>One installed add-on, as recorded in addons/installed.json.</summary>
public sealed class InstalledAddon
{
	public string Id { get; set; } = "";
	public string Version { get; set; } = "";
	public string Name { get; set; } = "";
	public string Path { get; set; } = "";
	public string InstalledUtc { get; set; } = "";
}

/// <summary>One-click install: validate the package, then copy it into
/// addons/&lt;id&gt;-&lt;version&gt;/ and record it. Installing the same
/// id+version twice is refused — uninstall first. Nothing is loaded here;
/// loading is <see cref="SdkPluginHost"/>.</summary>
public static class AddonInstaller
{
	public const string RegistryFileName = "installed.json";

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
	};

	public static IReadOnlyList<InstalledAddon> Installed( string addonsRoot )
	{
		var path = Path.Combine( addonsRoot, RegistryFileName );
		if ( !File.Exists( path ) ) return Array.Empty<InstalledAddon>();
		try
		{
			return JsonSerializer.Deserialize<List<InstalledAddon>>( File.ReadAllText( path ), JsonOptions )
				?? new List<InstalledAddon>();
		}
		catch { return Array.Empty<InstalledAddon>(); }
	}

	public static (bool Ok, string? Error, InstalledAddon? Installed) Install(
		string packageDir,
		string addonsRoot,
		IReadOnlyDictionary<string, byte[]> trustedPublishers,
		PluginApiVersion? hostApi = null )
	{
		var validation = AddonPackage.Validate( packageDir, trustedPublishers, hostApi ?? PluginSdk.CurrentApi );
		if ( !validation.Ok ) return ( false, string.Join( " ", validation.Errors ), null );
		var manifest = AddonPackage.ReadManifest( packageDir, out _ )!;
		var destination = Path.Combine( addonsRoot, $"{manifest.Id}-{manifest.Version}" );
		if ( Directory.Exists( destination ) )
			return ( false, $"Add-on {manifest.Id} {manifest.Version} is already installed: uninstall it first.", null );

		Directory.CreateDirectory( addonsRoot );
		Directory.CreateDirectory( destination );
		try
		{
			foreach ( var file in Directory.GetFiles( packageDir, "*", SearchOption.AllDirectories ) )
			{
				var relative = Path.GetRelativePath( packageDir, file );
				var target = Path.Combine( destination, relative );
				Directory.CreateDirectory( Path.GetDirectoryName( target )! );
				File.Copy( file, target );
			}
		}
		catch ( Exception e )
		{
			try { Directory.Delete( destination, recursive: true ); } catch { }
			return ( false, $"Install failed while copying files: {e.Message}", null );
		}

		var installed = new InstalledAddon
		{
			Id = manifest.Id, Version = manifest.Version, Name = manifest.Name,
			Path = destination, InstalledUtc = DateTimeOffset.UtcNow.ToString( "o" ),
		};
		var all = Installed( addonsRoot ).ToList();
		all.Add( installed );
		File.WriteAllText( Path.Combine( addonsRoot, RegistryFileName ),
			JsonSerializer.Serialize( all, JsonOptions ) );
		return ( true, null, installed );
	}

	public static bool Uninstall( string addonsRoot, string id, string version )
	{
		var all = Installed( addonsRoot ).ToList();
		var match = all.FirstOrDefault( a => a.Id == id && a.Version == version );
		if ( match is null ) return false;
		try { if ( Directory.Exists( match.Path ) ) Directory.Delete( match.Path, recursive: true ); } catch { return false; }
		all.Remove( match );
		File.WriteAllText( Path.Combine( addonsRoot, RegistryFileName ),
			JsonSerializer.Serialize( all, JsonOptions ) );
		return true;
	}
}

/// <summary>The versioned API a plug-in receives. This is the ONLY surface a
/// v1 plug-in is given: logging, project-root-scoped asset text, and
/// registration into host-owned lists. No kernel types cross it, and the host
/// refuses calls for capabilities the manifest did not grant.</summary>
public interface IPluginApi
{
	PluginApiVersion ApiVersion { get; }
	IReadOnlyList<string> Capabilities { get; }
	void Log( string message );
	string ReadAssetText( string relativePath );
	void WriteAssetText( string relativePath, string content );
	void RegisterBrush( string name, string description );
	void RegisterMaterial( string name, string description );
	void RegisterIo( string name, string description );
	IReadOnlyList<(string Kind, string Name, string Description)> Registrations { get; }
}

/// <summary>Host-side implementation of <see cref="IPluginApi"/>. Asset paths
/// are confined to the project root: absolute paths and ".." escapes are
/// refused, so a plug-in cannot read or write outside its project.</summary>
public sealed class PluginApi : IPluginApi
{
	readonly string _projectRoot;
	readonly List<(string Kind, string Name, string Description)> _registrations = new();
	readonly Action<string>? _log;

	public PluginApiVersion ApiVersion => PluginSdk.CurrentApi;
	public IReadOnlyList<string> Capabilities { get; }
	public IReadOnlyList<(string Kind, string Name, string Description)> Registrations => _registrations;

	public PluginApi( string projectRoot, IReadOnlyList<string> capabilities, Action<string>? log = null )
	{
		ArgumentNullException.ThrowIfNull( projectRoot );
		_projectRoot = Path.GetFullPath( projectRoot );
		Capabilities = capabilities.ToArray();
		_log = log;
	}

	public void Log( string message )
	{
		Require( "log" );
		_log?.Invoke( message );
	}

	void Require( string capability )
	{
		if ( !Capabilities.Contains( capability, StringComparer.Ordinal ) )
			throw new InvalidOperationException( $"Plug-in capability '{capability}' was not granted." );
	}

	string ResolveAsset( string relativePath )
	{
		var full = AddonPackage.SafeJoin( _projectRoot, relativePath )
			?? throw new InvalidOperationException( $"Asset path '{relativePath}' escapes the project root." );
		return full;
	}

	public string ReadAssetText( string relativePath )
	{
		Require( "assets.read" );
		var full = ResolveAsset( relativePath );
		if ( !File.Exists( full ) ) throw new FileNotFoundException( $"Asset '{relativePath}' does not exist.", full );
		return File.ReadAllText( full );
	}

	public void WriteAssetText( string relativePath, string content )
	{
		Require( "assets.write" );
		var full = ResolveAsset( relativePath );
		Directory.CreateDirectory( Path.GetDirectoryName( full )! );
		File.WriteAllText( full, content );
	}

	public void RegisterBrush( string name, string description )
	{
		Require( "brush.register" );
		_registrations.Add( ( "brush", name, description ) );
	}

	public void RegisterMaterial( string name, string description )
	{
		Require( "material.register" );
		_registrations.Add( ( "material", name, description ) );
	}

	public void RegisterIo( string name, string description )
	{
		Require( "io.register" );
		_registrations.Add( ( "io", name, description ) );
	}
}

/// <summary>A versioned plug-in: the contract a third-party add-on codes
/// against. Unlike the kernel's <see cref="IPlugin"/> (parameterless OnLoad),
/// this one receives the sandboxed <see cref="IPluginApi"/> — and nothing else.
/// The SDK host (<see cref="SdkPluginHost"/>) loads these; the kernel
/// <see cref="PluginManager"/> legacy path is untouched.</summary>
public interface ISdkPlugin
{
	string Name { get; }
	string Version { get; }
	void OnLoad( IPluginApi api );
	void OnUnload();
}

/// <summary>Loads versioned plug-ins and hands each one its sandboxed API.
/// The assembly path loads the type the manifest names as its entry point
/// (<see cref="AddonManifest.EntryPoint"/>), falling back to the first
/// non-abstract <see cref="ISdkPlugin"/> type when no entry point is given;
/// the instance path exists so tests and embedders can load a plug-in
/// without a compiled DLL on disk.</summary>
public sealed class SdkPluginHost
{
	readonly List<ISdkPlugin> _loaded = new();
	public IReadOnlyList<ISdkPlugin> Loaded => _loaded;

	public IPluginApi CreateApi( string projectRoot, IReadOnlyList<string> capabilities, Action<string>? log = null ) =>
		new PluginApi( projectRoot, capabilities, log );

	public ISdkPlugin Load( ISdkPlugin plugin, IPluginApi api )
	{
		ArgumentNullException.ThrowIfNull( plugin );
		ArgumentNullException.ThrowIfNull( api );
		if ( _loaded.Any( p => p.Name == plugin.Name ) ) return _loaded.First( p => p.Name == plugin.Name );
		plugin.OnLoad( api );
		_loaded.Add( plugin );
		return plugin;
	}

	public ISdkPlugin LoadFromAssembly( string dllPath, IPluginApi api, string? entryPointTypeName = null )
	{
		ArgumentNullException.ThrowIfNull( api );
		if ( !File.Exists( dllPath ) ) throw new FileNotFoundException( $"Plug-in assembly '{dllPath}' does not exist.", dllPath );
		var assembly = System.Reflection.Assembly.LoadFrom( Path.GetFullPath( dllPath ) );
		Type? target = null;
		if ( !string.IsNullOrWhiteSpace( entryPointTypeName ) )
		{
			target = assembly.GetType( entryPointTypeName, throwOnError: false );
			if ( target is null )
				throw new InvalidOperationException( $"Assembly '{dllPath}' has no entry-point type '{entryPointTypeName}'." );
		}
		target ??= assembly.GetTypes().FirstOrDefault( t =>
			typeof( ISdkPlugin ).IsAssignableFrom( t ) && !t.IsAbstract );
		if ( target is null || !typeof( ISdkPlugin ).IsAssignableFrom( target ) || target.IsAbstract )
			throw new InvalidOperationException( $"Assembly '{dllPath}' contains no ISdkPlugin entry point." );
		if ( Activator.CreateInstance( target ) is ISdkPlugin plugin )
			return Load( plugin, api );
		throw new InvalidOperationException( $"Entry-point type '{target.FullName}' could not be created." );
	}

	public void Unload( string name )
	{
		var plugin = _loaded.FirstOrDefault( p => p.Name == name );
		if ( plugin is null ) return;
		plugin.OnUnload();
		_loaded.Remove( plugin );
	}

	public void UnloadAll()
	{
		foreach ( var plugin in _loaded.ToArray() ) plugin.OnUnload();
		_loaded.Clear();
	}
}

/// <summary>Minimal store listing, v1: a local JSON catalog, no server. An
/// entry is what a user browses and one-click installs; the download is a
/// package directory (or an archive the store unpacks — out of scope here).
/// Search is case-insensitive substring over name, then tags, then summary.
/// </summary>
public sealed class StoreEntry
{
	public string Id { get; set; } = "";
	public string Name { get; set; } = "";
	public string Version { get; set; } = "";
	public string Author { get; set; } = "";
	public string Summary { get; set; } = "";
	public string Kind { get; set; } = AddonKind.Tool;
	public List<string> Tags { get; set; } = new();
	public int RequiredApiMajor { get; set; } = 1;
	public int RequiredApiMinor { get; set; } = 0;
	public string DownloadUri { get; set; } = "";
	public string Sha256 { get; set; } = "";
}

public sealed class AddonStoreCatalog
{
	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		WriteIndented = true,
	};

	public List<StoreEntry> Entries { get; set; } = new();

	public static AddonStoreCatalog Load( string path )
	{
		if ( !File.Exists( path ) ) return new AddonStoreCatalog();
		return JsonSerializer.Deserialize<AddonStoreCatalog>( File.ReadAllText( path ), JsonOptions )
			?? new AddonStoreCatalog();
	}

	public void Save( string path ) =>
		File.WriteAllText( path, JsonSerializer.Serialize( this, JsonOptions ) );

	/// <summary>Blank query returns everything, name order. Otherwise name
	/// matches first, then tag matches, then summary matches.</summary>
	public IReadOnlyList<StoreEntry> Search( string? query )
	{
		if ( string.IsNullOrWhiteSpace( query ) )
			return Entries.OrderBy( e => e.Name, StringComparer.OrdinalIgnoreCase ).ToArray();
		var q = query.Trim();
		int Score( StoreEntry e )
		{
			if ( e.Name.Contains( q, StringComparison.OrdinalIgnoreCase ) ) return 0;
			if ( e.Tags.Any( t => t.Contains( q, StringComparison.OrdinalIgnoreCase ) ) ) return 1;
			if ( e.Summary.Contains( q, StringComparison.OrdinalIgnoreCase ) ) return 2;
			return 3;
		}
		return Entries.Where( e => Score( e ) < 3 )
			.OrderBy( Score )
			.ThenBy( e => e.Name, StringComparer.OrdinalIgnoreCase )
			.ToArray();
	}
}
