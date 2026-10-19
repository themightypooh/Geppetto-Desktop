#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Marionette.Desktop;

/// <summary>
/// Represents a recent project entry in user preferences.
/// </summary>
public sealed record RecentProjectEntry(
	string Path,
	DateTimeOffset LastOpenedUtc,
	string? Name = null
);

/// <summary>
/// Snap step defaults for viewport gizmos.
/// </summary>
public sealed class SnapPreferences
{
	public bool TranslationEnabled { get; set; } = false;
	public float TranslationStep { get; set; } = 1.0f;
	public bool RotationEnabled { get; set; } = false;
	public float RotationDegrees { get; set; } = 15.0f;
	public bool ScaleEnabled { get; set; } = false;
	public float ScaleStep { get; set; } = 0.1f;

	public void ValidateAndSanitize( Action<string>? logDiagnostic = null )
	{
		if ( !float.IsFinite( TranslationStep ) || TranslationStep <= 0f )
		{
			logDiagnostic?.Invoke( $"Invalid TranslationStep '{TranslationStep}'. Resetting to 1.0." );
			TranslationStep = 1.0f;
		}
		if ( !float.IsFinite( RotationDegrees ) || RotationDegrees <= 0f )
		{
			logDiagnostic?.Invoke( $"Invalid RotationDegrees '{RotationDegrees}'. Resetting to 15.0." );
			RotationDegrees = 15.0f;
		}
		if ( !float.IsFinite( ScaleStep ) || ScaleStep <= 0f )
		{
			logDiagnostic?.Invoke( $"Invalid ScaleStep '{ScaleStep}'. Resetting to 0.1." );
			ScaleStep = 0.1f;
		}
	}
}

/// <summary>
/// Playback defaults for animation playback.
/// </summary>
public sealed class PlaybackPreferences
{
	public float DefaultFps { get; set; } = 30.0f;
	public bool LoopPlayback { get; set; } = true;

	public void ValidateAndSanitize( Action<string>? logDiagnostic = null )
	{
		if ( !float.IsFinite( DefaultFps ) || DefaultFps <= 0f || DefaultFps > 1000f )
		{
			logDiagnostic?.Invoke( $"Invalid DefaultFps '{DefaultFps}'. Resetting to 30.0." );
			DefaultFps = 30.0f;
		}
	}
}

/// <summary>
/// Autosave interval and retention configuration.
/// </summary>
public sealed class AutosavePreferences
{
	public bool Enabled { get; set; } = true;
	public double IntervalSeconds { get; set; } = 60.0;
	public int MaxRecoverySnapshots { get; set; } = 20;
	public double RetentionDays { get; set; } = 7.0;

	public void ValidateAndSanitize( Action<string>? logDiagnostic = null )
	{
		if ( !double.IsFinite( IntervalSeconds ) || IntervalSeconds <= 0 )
		{
			logDiagnostic?.Invoke( $"Invalid AutosaveIntervalSeconds '{IntervalSeconds}'. Resetting to 60.0." );
			IntervalSeconds = 60.0;
		}
		if ( MaxRecoverySnapshots <= 0 )
		{
			logDiagnostic?.Invoke( $"Invalid MaxRecoverySnapshots '{MaxRecoverySnapshots}'. Resetting to 20." );
			MaxRecoverySnapshots = 20;
		}
		if ( !double.IsFinite( RetentionDays ) || RetentionDays <= 0 )
		{
			logDiagnostic?.Invoke( $"Invalid RetentionDays '{RetentionDays}'. Resetting to 7.0." );
			RetentionDays = 7.0;
		}
	}
}

/// <summary>
/// User preferences and backend configuration.
/// Kept strictly separate from authored project state. Never serializes GL handles or state.
/// </summary>
public sealed class DesktopPreferences
{
	public int SchemaVersion { get; set; } = 1;

	public float Exposure { get; set; } = 1.0f;

	public SnapPreferences Snap { get; set; } = new();

	public PlaybackPreferences Playback { get; set; } = new();

	public AutosavePreferences Autosave { get; set; } = new();

	public List<RecentProjectEntry> RecentProjects { get; set; } = new();

	[JsonExtensionData]
	public Dictionary<string, JsonElement>? AdditionalData { get; set; }

	public void AddRecentProject( string path, string? name = null, int maxEntries = 10 )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return;
		string normalized = Path.GetFullPath( path );
		RecentProjects.RemoveAll( r => string.Equals( Path.GetFullPath( r.Path ), normalized,
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal ) );
		RecentProjects.Insert( 0, new RecentProjectEntry( path, DateTimeOffset.UtcNow, name ?? Path.GetFileName( path ) ) );
		if ( RecentProjects.Count > maxEntries )
		{
			RecentProjects.RemoveRange( maxEntries, RecentProjects.Count - maxEntries );
		}
	}

	public void ValidateAndSanitize( Action<string>? logDiagnostic = null )
	{
		if ( !float.IsFinite( Exposure ) || Exposure <= 0f || Exposure > 16f )
		{
			logDiagnostic?.Invoke( $"Invalid Exposure '{Exposure}'. Resetting to 1.0." );
			Exposure = 1.0f;
		}

		Snap.ValidateAndSanitize( logDiagnostic );
		Playback.ValidateAndSanitize( logDiagnostic );
		Autosave.ValidateAndSanitize( logDiagnostic );

		RecentProjects.RemoveAll( r => string.IsNullOrWhiteSpace( r.Path ) );
	}

	public static DesktopPreferences CreateDefault() => new();
}

/// <summary>
/// Resolves XDG paths for configuration, cache, and application state.
/// Respects XDG environment overrides and platform fallbacks.
/// Never uses cwd, source asset folder, or a hardcoded home directory.
/// </summary>
public sealed class DesktopPathEnvironment
{
	readonly Func<string, string?> _getEnv;
	readonly Func<string> _getHome;

	public DesktopPathEnvironment( Func<string, string?>? getEnv = null, Func<string>? getHome = null )
	{
		_getEnv = getEnv ?? Environment.GetEnvironmentVariable;
		_getHome = getHome ?? (() =>
		{
			string? home = _getEnv( "HOME" );
			if ( !string.IsNullOrWhiteSpace( home ) ) return home;
			return Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
		});
	}

	public string GetConfigDirectory()
	{
		string? xdgConfig = _getEnv( "XDG_CONFIG_HOME" );
		if ( !string.IsNullOrWhiteSpace( xdgConfig ) )
		{
			return Path.Combine( xdgConfig, "geppetto" );
		}

		if ( OperatingSystem.IsWindows() )
		{
			string appData = Environment.GetFolderPath( Environment.SpecialFolder.ApplicationData );
			return Path.Combine( appData, "geppetto" );
		}

		return Path.Combine( _getHome(), ".config", "geppetto" );
	}

	public string GetCacheDirectory()
	{
		string? xdgCache = _getEnv( "XDG_CACHE_HOME" );
		if ( !string.IsNullOrWhiteSpace( xdgCache ) )
		{
			return Path.Combine( xdgCache, "geppetto" );
		}

		if ( OperatingSystem.IsWindows() )
		{
			string localAppData = Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData );
			return Path.Combine( localAppData, "geppetto", "cache" );
		}

		return Path.Combine( _getHome(), ".cache", "geppetto" );
	}

	public string GetStateDirectory()
	{
		string? xdgState = _getEnv( "XDG_STATE_HOME" );
		if ( !string.IsNullOrWhiteSpace( xdgState ) )
		{
			return Path.Combine( xdgState, "geppetto" );
		}

		if ( OperatingSystem.IsWindows() )
		{
			string localAppData = Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData );
			return Path.Combine( localAppData, "geppetto", "state" );
		}

		return Path.Combine( _getHome(), ".local", "state", "geppetto" );
	}

	public string GetRecoveriesDirectory()
	{
		return Path.Combine( GetStateDirectory(), "recoveries" );
	}

	public string GetPreferencesFilePath()
	{
		return Path.Combine( GetConfigDirectory(), "preferences.json" );
	}
}

/// <summary>
/// Service managing reading, validating, and atomically persisting user preferences.
/// Corrupted preferences report a diagnostic and gracefully restore safe defaults without crashing.
/// </summary>
public sealed class PreferencesService
{
	readonly DesktopPathEnvironment _pathEnv;
	readonly IFileSystem _fs;
	readonly Action<string>? _logDiagnostic;

	public DesktopPathEnvironment PathEnvironment => _pathEnv;

	public PreferencesService(
		DesktopPathEnvironment? pathEnv = null,
		IFileSystem? fileSystem = null,
		Action<string>? logDiagnostic = null )
	{
		_pathEnv = pathEnv ?? new DesktopPathEnvironment();
		_fs = fileSystem ?? PhysicalFileSystem.Instance;
		_logDiagnostic = logDiagnostic;
	}

	public DesktopPreferences Load( string? customFilePath = null )
	{
		string filePath = customFilePath ?? _pathEnv.GetPreferencesFilePath();
		if ( !_fs.FileExists( filePath ) )
		{
			return DesktopPreferences.CreateDefault();
		}

		try
		{
			string json = _fs.ReadAllText( filePath );
			if ( string.IsNullOrWhiteSpace( json ) )
			{
				_logDiagnostic?.Invoke( $"Preferences file '{filePath}' was empty. Restoring safe defaults." );
				return DesktopPreferences.CreateDefault();
			}

			var options = new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true
			};
			var prefs = JsonSerializer.Deserialize<DesktopPreferences>( json, options );
			if ( prefs == null )
			{
				_logDiagnostic?.Invoke( $"Deserialized preferences from '{filePath}' was null. Restoring safe defaults." );
				return DesktopPreferences.CreateDefault();
			}

			prefs.ValidateAndSanitize( _logDiagnostic );
			return prefs;
		}
		catch ( Exception ex )
		{
			_logDiagnostic?.Invoke( $"Corrupted preferences file at '{filePath}': {ex.Message}. Restoring safe defaults." );
			return DesktopPreferences.CreateDefault();
		}
	}

	public bool Save( DesktopPreferences preferences, string? customFilePath = null )
	{
		ArgumentNullException.ThrowIfNull( preferences );
		string filePath = customFilePath ?? _pathEnv.GetPreferencesFilePath();
		string dir = Path.GetDirectoryName( Path.GetFullPath( filePath ) ) ?? "";
		if ( string.IsNullOrWhiteSpace( dir ) ) dir = ".";

		string? tempFile = null;
		try
		{
			if ( !_fs.DirectoryExists( dir ) )
			{
				_fs.CreateDirectory( dir );
			}

			preferences.ValidateAndSanitize( _logDiagnostic );

			string json = JsonSerializer.Serialize( preferences, new JsonSerializerOptions
			{
				WriteIndented = true
			} );

			tempFile = Path.Combine( dir, $".{Path.GetFileName( filePath )}.{Guid.NewGuid():N}.tmp" );

			using ( var stream = _fs.OpenWrite( tempFile ) )
			using ( var writer = new StreamWriter( stream, new UTF8Encoding( false ) ) )
			{
				writer.Write( json );
				writer.Flush();
				stream.Flush();
			}

			if ( _fs.FileExists( filePath ) )
			{
				_fs.ReplaceFile( tempFile, filePath );
			}
			else
			{
				_fs.MoveFile( tempFile, filePath, overwrite: false );
			}

			return true;
		}
		catch ( Exception ex )
		{
			if ( tempFile != null )
			{
				try
				{
					if ( _fs.FileExists( tempFile ) ) _fs.DeleteFile( tempFile );
				}
				catch { }
			}
			_logDiagnostic?.Invoke( $"Failed to save preferences to '{filePath}': {ex.Message}" );
			return false;
		}
	}
}
