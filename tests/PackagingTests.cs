#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Effigy;
using Marionette.Desktop;

internal static class PackagingTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "packaging", name, test );

		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		string FindScript()
		{
			var candidates = new[]
			{
				Path.Combine( Directory.GetCurrentDirectory(), "packaging", "package-linux.sh" ),
				Path.Combine( AppContext.BaseDirectory, "packaging", "package-linux.sh" ),
				Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "..", "packaging", "package-linux.sh" ),
				Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "packaging", "package-linux.sh" ),
				Path.Combine( "/home/poo/games/projects/geppetto-desktop", "packaging", "package-linux.sh" )
			};
			foreach ( var path in candidates )
			{
				string full = Path.GetFullPath( path );
				if ( File.Exists( full ) ) return full;
			}
			throw new FileNotFoundException( "Could not locate packaging/package-linux.sh in candidate paths." );
		}

		string? stagedPath = null;
		object stageLock = new object();

		string EnsureStaged()
		{
			lock ( stageLock )
			{
				if ( stagedPath != null && Directory.Exists( stagedPath ) )
					return stagedPath;

				string scriptPath = FindScript();
				string targetDir = Path.Combine( settings.WorkDirectory, "staged dist with spaces" );
				Directory.CreateDirectory( targetDir );

				var psi = new ProcessStartInfo
				{
					FileName = "/bin/bash",
					ArgumentList = { scriptPath, targetDir },
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false
				};

				using var proc = Process.Start( psi ) ?? throw new InvalidOperationException( "Failed to start packaging script process." );
				string stdout = proc.StandardOutput.ReadToEnd();
				string stderr = proc.StandardError.ReadToEnd();
				proc.WaitForExit();

				if ( proc.ExitCode != 0 )
				{
					throw new Exception( $"Packaging script failed with exit code {proc.ExitCode}:\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}" );
				}

				stagedPath = targetDir;
				return stagedPath;
			}
		}

		// 1. Packaging script exists and is executable
		Check( "packaging script exists and is executable", () =>
		{
			string scriptPath = FindScript();
			Require( File.Exists( scriptPath ), $"Packaging script not found at: {scriptPath}" );

			// Check Unix executable mode
			if ( OperatingSystem.IsLinux() )
			{
				var mode = File.GetUnixFileMode( scriptPath );
				Require( (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0,
					$"Packaging script '{scriptPath}' must have execute permissions." );
			}

			string firstLine = File.ReadLines( scriptPath ).FirstOrDefault() ?? "";
			Require( firstLine.StartsWith( "#!", StringComparison.Ordinal ),
				"Packaging script must start with a shebang line." );
		} );

		// 2. Packaging script successfully stages files to a custom path (with spaces supported)
		Check( "packaging script successfully stages files to a custom path (with spaces supported)", () =>
		{
			string staged = EnsureStaged();
			Require( Directory.Exists( staged ), "Staged output directory must exist." );
			Require( staged.Contains( ' ' ), "Staged output path must test support for paths containing spaces." );

			var files = Directory.GetFileSystemEntries( staged );
			Require( files.Length > 0, "Staged directory must contain published files." );
		} );

		// 3. Staged directory contains main executable, runtime DLLs, and launcher script
		Check( "staged directory contains main executable, runtime DLLs, and launcher script", () =>
		{
			string staged = EnsureStaged();

			// Main executable
			string mainExe = Path.Combine( staged, "marionette-desktop" );
			Require( File.Exists( mainExe ), "Main executable 'marionette-desktop' must exist in staged directory." );
			if ( OperatingSystem.IsLinux() )
			{
				var exeMode = File.GetUnixFileMode( mainExe );
				Require( (exeMode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute)) != 0,
					"Main executable 'marionette-desktop' must have execute permissions." );
			}

			// Launcher script
			string launcher = Path.Combine( staged, "run.sh" );
			Require( File.Exists( launcher ), "Launcher script 'run.sh' must exist in staged directory." );
			if ( OperatingSystem.IsLinux() )
			{
				var launcherMode = File.GetUnixFileMode( launcher );
				Require( (launcherMode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute)) != 0,
					"Launcher script 'run.sh' must have execute permissions." );
			}

			// Kernel and application assemblies
			Require( File.Exists( Path.Combine( staged, "Effigy.Kernel.dll" ) ), "Effigy.Kernel.dll must be present." );
			Require( File.Exists( Path.Combine( staged, "marionette-desktop.dll" ) ), "marionette-desktop.dll must be present." );

			// Silk.NET graphics and windowing dependencies
			Require( File.Exists( Path.Combine( staged, "Silk.NET.OpenGL.dll" ) ), "Silk.NET.OpenGL.dll must be present." );
			Require( File.Exists( Path.Combine( staged, "Silk.NET.Windowing.Sdl.dll" ) ), "Silk.NET.Windowing.Sdl.dll must be present." );

			// Self-contained coreclr and native runtime
			Require( File.Exists( Path.Combine( staged, "System.Private.CoreLib.dll" ) ), "System.Private.CoreLib.dll must be present in self-contained bundle." );
			Require( File.Exists( Path.Combine( staged, "libcoreclr.so" ) ), "libcoreclr.so must be present." );
			Require( File.Exists( Path.Combine( staged, "libhostfxr.so" ) ), "libhostfxr.so must be present." );

			// Bundled SDL native library
			bool sdlInRoot = File.Exists( Path.Combine( staged, "libSDL2-2.0.so" ) );
			bool sdlInRuntimes = File.Exists( Path.Combine( staged, "runtimes", "linux-x64", "native", "libSDL2-2.0.so" ) );
			Require( sdlInRoot || sdlInRuntimes, "libSDL2-2.0.so must be present in root or runtimes/linux-x64/native." );
		} );

		// 4. build-info.json manifest contains version, timestamp, and target architecture
		Check( "build-info.json manifest contains version, timestamp, and target architecture", () =>
		{
			string staged = EnsureStaged();
			string manifestPath = Path.Combine( staged, "build-info.json" );
			Require( File.Exists( manifestPath ), "build-info.json manifest must exist in staged directory." );

			string json = File.ReadAllText( manifestPath );
			using var doc = JsonDocument.Parse( json );
			var root = doc.RootElement;

			Require( root.TryGetProperty( "version", out var versionProp ) && !string.IsNullOrWhiteSpace( versionProp.GetString() ),
				"build-info.json must contain a valid non-empty 'version' field." );

			Require( root.TryGetProperty( "timestamp", out var tsProp ) && !string.IsNullOrWhiteSpace( tsProp.GetString() ),
				"build-info.json must contain a valid non-empty 'timestamp' field." );

			Require( DateTimeOffset.TryParse( tsProp.GetString(), out _ ),
				$"build-info.json timestamp '{tsProp.GetString()}' must be a valid ISO 8601 timestamp." );

			bool hasArch = false;
			if ( root.TryGetProperty( "targetArchitecture", out var targetArchProp ) )
			{
				string? val = targetArchProp.GetString();
				hasArch = val != null && (val.Contains( "x64", StringComparison.OrdinalIgnoreCase ) || val.Contains( "linux", StringComparison.OrdinalIgnoreCase ));
			}
			if ( !hasArch && root.TryGetProperty( "architecture", out var archProp ) )
			{
				string? val = archProp.GetString();
				hasArch = val != null && val.Contains( "x64", StringComparison.OrdinalIgnoreCase );
			}
			Require( hasArch, "build-info.json must specify target architecture (linux-x64 or x64)." );

			Require( root.TryGetProperty( "runtime", out var runtimeProp ) && !string.IsNullOrWhiteSpace( runtimeProp.GetString() ),
				"build-info.json must record runtime identifier." );

			Require( root.TryGetProperty( "sdkVersion", out var sdkProp ) && !string.IsNullOrWhiteSpace( sdkProp.GetString() ),
				"build-info.json must record SDK version used to build package." );
		} );

		// 5. Archive creation produces valid tar.gz and matching SHA256 checksum
		Check( "archive creation produces valid tar.gz and matching SHA256 checksum", () =>
		{
			string staged = EnsureStaged();
			string archivePath = Path.Combine( staged, "geppetto-desktop-linux-x64.tar.gz" );
			string checksumPath = Path.Combine( staged, "geppetto-desktop-linux-x64.tar.gz.sha256" );

			Require( File.Exists( archivePath ), "geppetto-desktop-linux-x64.tar.gz archive must exist." );
			var fileInfo = new FileInfo( archivePath );
			Require( fileInfo.Length > 1_000_000, $"Archive size must be substantial (>1MB), got {fileInfo.Length} bytes." );

			Require( File.Exists( checksumPath ), "geppetto-desktop-linux-x64.tar.gz.sha256 checksum file must exist." );

			// Verify SHA256 matches
			byte[] hashBytes = SHA256.HashData( File.ReadAllBytes( archivePath ) );
			string computedHex = Convert.ToHexString( hashBytes ).ToLowerInvariant();

			string checksumContent = File.ReadAllText( checksumPath ).Trim();
			Require( checksumContent.ToLowerInvariant().Contains( computedHex ),
				$"Checksum in {checksumPath} ({checksumContent}) did not contain computed SHA256 ({computedHex})." );

			// Verify archive decompression and tar stream validity
			using var fs = File.OpenRead( archivePath );
			using var gz = new GZipStream( fs, CompressionMode.Decompress );
			using var tar = new TarReader( gz );

			var entryNames = new HashSet<string>( StringComparer.Ordinal );
			TarEntry? entry;
			while ( (entry = tar.GetNextEntry()) != null )
			{
				entryNames.Add( entry.Name.TrimStart( '.', '/' ) );
			}

			Require( entryNames.Any( n => n.EndsWith( "marionette-desktop", StringComparison.Ordinal ) ),
				"Archive entries must contain marionette-desktop executable." );
			Require( entryNames.Any( n => n.EndsWith( "run.sh", StringComparison.Ordinal ) ),
				"Archive entries must contain run.sh launcher." );
			Require( entryNames.Any( n => n.EndsWith( "Effigy.Kernel.dll", StringComparison.Ordinal ) ),
				"Archive entries must contain Effigy.Kernel.dll." );
			Require( entryNames.Any( n => n.EndsWith( "build-info.json", StringComparison.Ordinal ) ),
				"Archive entries must contain build-info.json manifest." );
		} );

		// 6. Bundled sample project is present and valid
		Check( "bundled sample project is present and valid", () =>
		{
			string staged = EnsureStaged();
			string sampleDir = Path.Combine( staged, "sample" );
			Require( Directory.Exists( sampleDir ), "Sample directory 'sample/' must exist in staged package." );

			string projectPath = Path.Combine( sampleDir, "sample.geppetto-project.json" );
			Require( File.Exists( projectPath ), "sample.geppetto-project.json must exist in sample directory." );

			string effigyPath = Path.Combine( sampleDir, "sample.effigy" );
			Require( File.Exists( effigyPath ), "sample.effigy model must exist in sample directory." );

			string clipPath = Path.Combine( sampleDir, "sample.riganim" );
			Require( File.Exists( clipPath ), "sample.riganim clip must exist in sample directory." );
			string rigPath = Path.Combine( sampleDir, "sample-rig.fbx" );
			Require( File.Exists( rigPath ) && FbxImport.IsFbx( File.ReadAllBytes( rigPath ) ),
				"Bundled rigged sample must be a valid FBX file." );

			// Validate project manifest loading
			var project = DesktopProject.Load( projectPath );
			Require( !string.IsNullOrWhiteSpace( project.Id ), "Sample project must have an Id." );
			Require( !string.IsNullOrWhiteSpace( project.Name ), "Sample project must have a Name." );
			Require( project.SourceDocuments.Count > 0, "Sample project must declare at least one source document." );
			Require( project.SourceDocuments.Any( doc => doc.Asset.Path == "sample-rig.fbx" && doc.Format == DocumentSourceKind.Fbx ),
				"Sample project must identify the owned rigged FBX as an FBX source." );
			Require( project.Clips.Any( clip => clip.TargetRigId == "doc_sample_rig" ),
				"Sample clip must target its rigged source, not the unrigged cube." );

			// Validate .effigy format and rebuild in kernel
			var studio = StudioDocument.ReadFile( effigyPath );
			studio.Rebuild();
			Require( studio.Features.Count > 0, "Sample .effigy studio must contain valid features." );

			// Validate .riganim JSON content
			string clipJson = File.ReadAllText( clipPath );
			using var clipDoc = JsonDocument.Parse( clipJson );
			Require( clipDoc.RootElement.TryGetProperty( "BoneTracks", out var boneTracks ) && boneTracks.GetArrayLength() > 0,
				"Sample .riganim must define at least one bone track." );

			// Verify portability of sample asset references (no developer machine paths)
			var docAudit = DistributionInventory.AuditPathForPortability( project.SourceDocuments[0].Asset.Path );
			Require( !docAudit.IsMachineSpecific, $"Sample document path must be portable: {docAudit.Diagnostic}" );
			if ( project.Clips.Count > 0 )
			{
				var clipAudit = DistributionInventory.AuditPathForPortability( project.Clips[0].Asset.Path );
				Require( !clipAudit.IsMachineSpecific, $"Sample clip path must be portable: {clipAudit.Diagnostic}" );
			}
		} );

		// 7. Launcher script does not require dotnet SDK on path
		Check( "launcher script does not require dotnet SDK on path", () =>
		{
			string staged = EnsureStaged();
			string launcher = Path.Combine( staged, "run.sh" );
			Require( File.Exists( launcher ), "Launcher run.sh must exist." );

			string launcherSource = File.ReadAllText( launcher );
			Require( !launcherSource.Contains( "dotnet run", StringComparison.OrdinalIgnoreCase ),
				"Launcher must not invoke 'dotnet run'." );
			Require( !launcherSource.Contains( "dotnet exec", StringComparison.OrdinalIgnoreCase ),
				"Launcher must not invoke 'dotnet exec'." );

			// Test executing run.sh --help in an isolated environment without dotnet on PATH
			var psi = new ProcessStartInfo
			{
				FileName = "/bin/bash",
				ArgumentList = { launcher, "--help" },
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false
			};

			psi.EnvironmentVariables["PATH"] = "/usr/bin:/bin";
			psi.EnvironmentVariables.Remove( "DOTNET_ROOT" );
			psi.EnvironmentVariables.Remove( "DOTNET_MULTILEVEL_LOOKUP" );

			using var proc = Process.Start( psi ) ?? throw new InvalidOperationException( "Failed to execute launcher script." );
			string stdout = proc.StandardOutput.ReadToEnd();
			string stderr = proc.StandardError.ReadToEnd();
			proc.WaitForExit();

			Require( proc.ExitCode == 0,
				$"Launcher script execution without dotnet SDK on PATH failed (exit code {proc.ExitCode}):\nSTDOUT: {stdout}\nSTDERR: {stderr}" );
			Require( stdout.Contains( "Geppetto:", StringComparison.OrdinalIgnoreCase ),
				$"Expected usage string from launcher execution, got: {stdout}" );
		} );

		// 8. Sensitive files, caches, and developer paths excluded from package
		Check( "sensitive files, caches, and developer paths excluded from package", () =>
		{
			string staged = EnsureStaged();

			var allFiles = Directory.GetFiles( staged, "*", SearchOption.AllDirectories );
			var allDirs = Directory.GetDirectories( staged, "*", SearchOption.AllDirectories );

			// Check directories for forbidden patterns
			foreach ( var dir in allDirs )
			{
				string name = Path.GetFileName( dir );
				Require( name != ".git", "Package must not contain .git directory." );
				Require( name != "obj", "Package must not contain build cache 'obj' directory." );
				Require( name != "bin", "Package must not contain nested build output 'bin' directory." );
			}

			// Check files for source files, project files, and backup detritus
			foreach ( var file in allFiles )
			{
				string ext = Path.GetExtension( file ).ToLowerInvariant();
				Require( ext != ".cs", $"Source code file '{file}' must not be present in package." );
				Require( ext != ".csproj", $"Project file '{file}' must not be present in package." );
				Require( ext != ".sln", $"Solution file '{file}' must not be present in package." );
				Require( !file.EndsWith( "~", StringComparison.Ordinal ), $"Backup file '{file}' must not be present in package." );

				// Validate asset files against proprietary keywords
				string relativePath = Path.GetRelativePath( staged, file );
				var assetAudit = DistributionInventory.ValidateAssetDependency( relativePath );
				Require( assetAudit.IsApproved, $"File '{relativePath}' violates asset distribution policy: {assetAudit.ViolationReason}" );
			}

			// Audit build-info.json for lack of developer-specific private directories
			string manifestPath = Path.Combine( staged, "build-info.json" );
			string manifestText = File.ReadAllText( manifestPath );
			Require( !manifestText.Contains( "/home/poo", StringComparison.OrdinalIgnoreCase ),
				"build-info.json must not leak developer home directory paths." );
			Require( !manifestText.Contains( "deep_sentance", StringComparison.OrdinalIgnoreCase ),
				"build-info.json must not reference private game projects." );
		} );

		// 9. Documentation and capability list present in distribution package
		Check( "documentation and capability list present in distribution package", () =>
		{
			string staged = EnsureStaged();
			Require( File.Exists( Path.Combine( staged, "README.md" ) ), "Distribution README.md must be present." );
			Require( File.Exists( Path.Combine( staged, "NOTICES.md" ) ), "Dependency and license NOTICES.md must be present." );
			Require( File.Exists( Path.Combine( staged, "CAPABILITIES.md" ) ), "CAPABILITIES.md must be present." );

			string notices = File.ReadAllText( Path.Combine( staged, "NOTICES.md" ) );
			Require( notices.Contains( "Silk.NET", StringComparison.OrdinalIgnoreCase ), "NOTICES.md must mention Silk.NET." );
			Require( notices.Contains( "SDL", StringComparison.OrdinalIgnoreCase ), "NOTICES.md must mention SDL." );
			Require( notices.Contains( "MIT", StringComparison.OrdinalIgnoreCase ), "NOTICES.md must mention MIT license." );
		} );
	}
}
