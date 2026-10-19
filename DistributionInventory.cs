using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Effigy;

namespace Marionette.Desktop;

public enum LicenseCategory
{
	Permissive,
	ProjectOwned,
	Copyleft,
	Proprietary
}

public sealed record PackageDependency(
	string Name,
	string Version,
	string License,
	LicenseCategory Category,
	bool IsRuntimeRequired,
	bool IsManaged,
	string Description,
	string SpdxId
);

public sealed record NativeLibraryDependency(
	string Name,
	string Soname,
	string Purpose,
	bool IsRequired,
	bool IsBundled,
	string HostPackageHint
);

public sealed record LicenseAuditEntry(
	string Component,
	string License,
	string SpdxId,
	LicenseCategory Category,
	bool IsApprovedForDistribution,
	string Notes
);

public sealed record SystemCompatibilityReport(
	bool IsCompatible,
	string OsDescription,
	string Architecture,
	bool IsLinuxX64,
	bool HasRequiredEgl,
	bool HasRequiredGl,
	bool HasBundledSdl,
	IReadOnlyList<string> MissingLibraries,
	IReadOnlyList<string> Diagnostics
);

public sealed record PathAuditResult(
	string Path,
	bool IsMachineSpecific,
	string MachineSpecificPrefix,
	string Diagnostic
);

public sealed record AssetDependencyAuditResult(
	string AssetPath,
	bool IsApproved,
	string ViolationReason
);

/// <summary>
/// Programmatic query and audit service for distribution dependencies,
/// license compliance, host requirements, and path portability.
/// Strictly CPU-only: no UI, no window, no GL calls.
/// </summary>
public static class DistributionInventory
{
	private static readonly string[] DeveloperPathPrefixes = new[]
	{
		"/home/",
		"/Users/",
		"C:\\Users\\",
		"C:/Users/",
		"/root/",
		"/tmp/dev/"
	};

	private static readonly string[] ProprietaryKeywords = new[]
	{
		"facepunch",
		"sbox",
		"deep_sentance",
		"citizen",
		".vmdl"
	};

	/// <summary>
	/// Returns the inventory of required and optional native host libraries.
	/// </summary>
	public static IReadOnlyList<NativeLibraryDependency> GetNativeDependencies()
	{
		return new[]
		{
			new NativeLibraryDependency(
				Name: "EGL",
				Soname: "libEGL.so.1",
				Purpose: "Hardware EGL device enumeration and pbuffer surface context creation for headless GPU rendering.",
				IsRequired: true,
				IsBundled: false,
				HostPackageHint: "libegl1 (Debian/Ubuntu), mesa-libEGL (Fedora/RHEL), or NVIDIA proprietary driver package."
			),
			new NativeLibraryDependency(
				Name: "OpenGL",
				Soname: "libGL.so.1",
				Purpose: "OpenGL 4.6 core profile function dispatch and hardware GPU rendering pipeline.",
				IsRequired: true,
				IsBundled: false,
				HostPackageHint: "libgl1 (Debian/Ubuntu), mesa-libGL (Fedora/RHEL), or vendor GPU driver package."
			),
			new NativeLibraryDependency(
				Name: "SDL2",
				Soname: "libSDL2-2.0.so",
				Purpose: "Desktop windowing, display connection, and keyboard/mouse input handling.",
				IsRequired: true,
				IsBundled: true,
				HostPackageHint: "Bundled via Ultz.Native.SDL in runtimes/linux-x64/native; fallback host package: libsdl2-2.0-0."
			),
			new NativeLibraryDependency(
				Name: "GLFW",
				Soname: "libglfw.so.3",
				Purpose: "Alternative windowing and input backend for desktop platforms.",
				IsRequired: false,
				IsBundled: true,
				HostPackageHint: "Bundled via Ultz.Native.GLFW in runtimes/linux-x64/native; fallback host package: libglfw3."
			)
		};
	}

	/// <summary>
	/// Returns the managed packages and assembly dependencies for the application runtime.
	/// </summary>
	public static IReadOnlyList<PackageDependency> GetManagedDependencies()
	{
		return new[]
		{
			new PackageDependency( "Silk.NET.OpenGL", "2.23.0", "MIT", LicenseCategory.Permissive, true, true, "OpenGL 4.6 core API bindings", "MIT" ),
			new PackageDependency( "Silk.NET.Windowing", "2.23.0", "MIT", LicenseCategory.Permissive, true, true, "Desktop windowing abstraction", "MIT" ),
			new PackageDependency( "Silk.NET.Windowing.Sdl", "2.23.0", "MIT", LicenseCategory.Permissive, true, true, "SDL2 windowing implementation", "MIT" ),
			new PackageDependency( "Silk.NET.Input", "2.23.0", "MIT", LicenseCategory.Permissive, true, true, "Desktop input system abstraction", "MIT" ),
			new PackageDependency( "Silk.NET.Input.Sdl", "2.23.0", "MIT", LicenseCategory.Permissive, true, true, "SDL2 input implementation", "MIT" ),
			new PackageDependency( "Silk.NET.Core", "2.23.0", "MIT", LicenseCategory.Permissive, true, true, "Silk.NET native interop and runtime loader", "MIT" ),
			new PackageDependency( "Silk.NET.Maths", "2.23.0", "MIT", LicenseCategory.Permissive, true, true, "Silk vector and matrix primitives", "MIT" ),
			new PackageDependency( "Silk.NET.SDL", "2.23.0", "MIT", LicenseCategory.Permissive, true, true, "Silk raw SDL2 P/Invoke bindings", "MIT" ),
			new PackageDependency( "System.Text.Json", "8.0.x", "MIT", LicenseCategory.Permissive, true, true, "BCL JSON serialization and parsing", "MIT" ),
			new PackageDependency( "System.IO.Pipelines", "8.0.x", "MIT", LicenseCategory.Permissive, true, true, "High-performance buffered IO pipelines", "MIT" ),
			new PackageDependency( "Effigy.Kernel", "1.0.0", "Project-Owned", LicenseCategory.ProjectOwned, true, true, "Pure CIL engine-free 3D rigging and geometry kernel", "Owner-Proprietary" )
		};
	}

	/// <summary>
	/// Returns the intellectual property and redistribution license audit table.
	/// </summary>
	public static IReadOnlyList<LicenseAuditEntry> GetLicenseAudit()
	{
		return new[]
		{
			new LicenseAuditEntry( "Silk.NET Suite", "MIT License", "MIT", LicenseCategory.Permissive, true, "Permissive open-source; Dylan Perks and Silk.NET contributors" ),
			new LicenseAuditEntry( "Ultz.Native.SDL (SDL2)", "zlib License", "Zlib", LicenseCategory.Permissive, true, "Permissive open-source; Sam Lantinga and SDL contributors; binary redistribution allowed" ),
			new LicenseAuditEntry( "Ultz.Native.GLFW (GLFW)", "zlib/libpng License", "Zlib", LicenseCategory.Permissive, true, "Permissive open-source; Marcus Geelnard and Camilla Löwy; binary redistribution allowed" ),
			new LicenseAuditEntry( ".NET Runtime / BCL", "MIT License", "MIT", LicenseCategory.Permissive, true, "Microsoft Corporation; standard .NET 8.0 permissive license" ),
			new LicenseAuditEntry( "Effigy.Kernel", "Project-Owned", "Owner-Proprietary", LicenseCategory.ProjectOwned, true, "Geppetto Desktop project internal kernel; fully owned" ),
			new LicenseAuditEntry( "Marionette.Desktop", "Project-Owned", "Owner-Proprietary", LicenseCategory.ProjectOwned, true, "Geppetto Desktop desktop application; fully owned" )
		};
	}

	/// <summary>
	/// Verifies that all registered licenses are approved for distribution (Permissive or ProjectOwned).
	/// Throws or returns false if any unapproved or copyleft license is present.
	/// </summary>
	public static bool ValidateAllLicensesApproved( out IReadOnlyList<string> violations )
	{
		var list = new List<string>();
		foreach ( var entry in GetLicenseAudit() )
		{
			if ( !entry.IsApprovedForDistribution )
			{
				list.Add( $"Component '{entry.Component}' is marked as unapproved for distribution." );
			}
			if ( entry.Category is LicenseCategory.Copyleft or LicenseCategory.Proprietary )
			{
				list.Add( $"Component '{entry.Component}' has restrictive license category '{entry.Category}'." );
			}
		}
		violations = list;
		return list.Count == 0;
	}

	/// <summary>
	/// Performs a programmatic host system compatibility audit without opening a window or making GL calls.
	/// Checks Linux x64 platform, presence of libEGL.so.1, libGL.so.1, and SDL2.
	/// </summary>
	public static SystemCompatibilityReport CheckSystemCompatibility()
	{
		var diagnostics = new List<string>();
		var missing = new List<string>();

		bool isLinux = OperatingSystem.IsLinux();
		bool isX64 = RuntimeInformation.ProcessArchitecture == Architecture.X64;
		bool isLinuxX64 = isLinux && isX64;

		diagnostics.Add( $"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})" );
		diagnostics.Add( $"Process Architecture: {RuntimeInformation.ProcessArchitecture}" );
		diagnostics.Add( $".NET Runtime: {RuntimeInformation.FrameworkDescription}" );

		if ( !isLinux )
		{
			diagnostics.Add( "FAIL: Non-Linux host detected. Geppetto Desktop standalone distribution target is Linux x64." );
		}
		if ( !isX64 )
		{
			diagnostics.Add( $"FAIL: Non-x64 architecture ({RuntimeInformation.ProcessArchitecture}). Distribution target is x64." );
		}

		bool hasEgl = CheckNativeLibraryAvailable( "libEGL.so.1", out string eglDiag, out _ );
		diagnostics.Add( $"libEGL.so.1 probe: {(hasEgl ? "PASS" : "FAIL")} — {eglDiag}" );
		if ( !hasEgl ) missing.Add( "libEGL.so.1" );

		bool hasGl = CheckNativeLibraryAvailable( "libGL.so.1", out string glDiag, out _ );
		diagnostics.Add( $"libGL.so.1 probe: {(hasGl ? "PASS" : "FAIL")} — {glDiag}" );
		if ( !hasGl ) missing.Add( "libGL.so.1" );

		// Probe bundled or host SDL2
		bool hasSdl = CheckNativeLibraryAvailable( "libSDL2-2.0.so", out string sdlDiag, out _ )
			|| CheckNativeLibraryAvailable( "libSDL2-2.0.so.0", out sdlDiag, out _ )
			|| CheckNativeLibraryAvailable( "libSDL2.so", out sdlDiag, out _ );
		diagnostics.Add( $"SDL2 probe: {(hasSdl ? "PASS" : "FAIL")} — {sdlDiag}" );
		if ( !hasSdl ) missing.Add( "libSDL2-2.0.so" );

		bool isCompatible = isLinuxX64 && hasEgl && hasGl;

		return new SystemCompatibilityReport(
			IsCompatible: isCompatible,
			OsDescription: RuntimeInformation.OSDescription,
			Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
			IsLinuxX64: isLinuxX64,
			HasRequiredEgl: hasEgl,
			HasRequiredGl: hasGl,
			HasBundledSdl: hasSdl,
			MissingLibraries: missing,
			Diagnostics: diagnostics
		);
	}

	/// <summary>
	/// Tests whether a native shared library can be loaded by the OS dynamic linker.
	/// Strictly non-throwing; unloads immediately if successful.
	/// </summary>
	public static bool CheckNativeLibraryAvailable( string soname, out string diagnostic, out string resolvedPath )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( soname );

		if ( NativeLibrary.TryLoad( soname, typeof(DistributionInventory).Assembly, null, out IntPtr handle ) )
		{
			NativeLibrary.Free( handle );
			diagnostic = $"Library '{soname}' is available on the host system dynamic link path.";
			resolvedPath = soname;
			return true;
		}

		// Also check runtimes/linux-x64/native relative to application directory
		string appDir = AppContext.BaseDirectory;
		string bundledPath = Path.Combine( appDir, "runtimes", "linux-x64", "native", soname );
		if ( File.Exists( bundledPath ) && NativeLibrary.TryLoad( bundledPath, out IntPtr bundledHandle ) )
		{
			NativeLibrary.Free( bundledHandle );
			diagnostic = $"Library '{soname}' is bundled at: {bundledPath}";
			resolvedPath = bundledPath;
			return true;
		}

		resolvedPath = null;
		diagnostic = DiagnoseMissingLibrary( soname );
		return false;
	}

	/// <summary>
	/// Generates actionable troubleshooting and package remediation advice when a native library cannot be loaded.
	/// </summary>
	public static string DiagnoseMissingLibrary( string soname )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( soname );

		return soname switch
		{
			"libEGL.so.1" => "Missing host EGL 1.5+ runtime 'libEGL.so.1'. Headless GPU rendering requires hardware EGL. Please install 'libegl1' (Debian/Ubuntu), 'mesa-libEGL' (Fedora), or your GPU vendor driver.",
			"libGL.so.1" => "Missing host OpenGL driver 'libGL.so.1'. Hardware rendering requires OpenGL 4.6. Please install 'libgl1' (Debian/Ubuntu), 'mesa-libGL' (Fedora), or your GPU vendor driver.",
			"libSDL2-2.0.so" or "libSDL2.so" => "Missing SDL2 shared library 'libSDL2-2.0.so'. Interactive desktop mode requires SDL2. Verify bundled native assets or install 'libsdl2-2.0-0' (Debian/Ubuntu) / 'SDL2' (Fedora).",
			"libglfw.so.3" => "Missing GLFW shared library 'libglfw.so.3'. Fallback windowing requires GLFW. Install 'libglfw3' (Debian/Ubuntu) or 'glfw' (Fedora).",
			_ => $"Missing native library '{soname}'. Dynamic linker (dlopen) failed to find or load the specified shared object. Check system package repository or LD_LIBRARY_PATH."
		};
	}

	/// <summary>
	/// Audits an arbitrary path string for machine-specific developer prefixes or lack of portability.
	/// </summary>
	public static PathAuditResult AuditPathForPortability( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
		{
			return new PathAuditResult( path ?? "", false, null, "Path is empty." );
		}

		string normalized = path.Replace( '\\', '/' );

		foreach ( var prefix in DeveloperPathPrefixes )
		{
			if ( normalized.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) ||
			     normalized.Contains( prefix, StringComparison.OrdinalIgnoreCase ) )
			{
				return new PathAuditResult(
					Path: path,
					IsMachineSpecific: true,
					MachineSpecificPrefix: prefix,
					Diagnostic: $"Path '{path}' contains developer machine prefix '{prefix}' and cannot be distributed portably."
				);
			}
		}

		if ( Path.IsPathRooted( path ) )
		{
			return new PathAuditResult(
				Path: path,
				IsMachineSpecific: true,
				MachineSpecificPrefix: Path.GetPathRoot( path ),
				Diagnostic: $"Path '{path}' is an absolute rooted path. Portable projects must use relative asset references."
			);
		}

		return new PathAuditResult(
			Path: path,
			IsMachineSpecific: false,
			MachineSpecificPrefix: null,
			Diagnostic: "Path is portable and relative."
		);
	}

	/// <summary>
	/// Audits a DesktopOptions instance for machine-specific paths or configuration.
	/// </summary>
	public static IReadOnlyList<PathAuditResult> AuditOptionsPortability( DesktopOptions options )
	{
		ArgumentNullException.ThrowIfNull( options );
		var results = new List<PathAuditResult>();

		if ( !string.IsNullOrEmpty( options.Model ) )
		{
			results.Add( AuditPathForPortability( options.Model ) );
		}
		if ( !string.IsNullOrEmpty( options.Clip ) )
		{
			results.Add( AuditPathForPortability( options.Clip ) );
		}
		if ( !string.IsNullOrEmpty( options.Screenshot ) )
		{
			results.Add( AuditPathForPortability( options.Screenshot ) );
		}

		return results;
	}

	/// <summary>
	/// Audits all asset references within a DesktopProject to verify they are portable relative paths.
	/// </summary>
	public static IReadOnlyList<PathAuditResult> AuditProjectPortability( DesktopProject project )
	{
		ArgumentNullException.ThrowIfNull( project );
		var results = new List<PathAuditResult>();

		foreach ( var doc in project.SourceDocuments )
		{
			if ( doc.Asset != null )
			{
				results.Add( AuditPathForPortability( doc.Asset.Path ) );
			}
		}

		foreach ( var clip in project.Clips )
		{
			if ( clip.Asset != null )
			{
				results.Add( AuditPathForPortability( clip.Asset.Path ) );
			}
		}

		foreach ( var mo in project.MaterialOverrides )
		{
			if ( mo.MaterialAsset != null )
			{
				results.Add( AuditPathForPortability( mo.MaterialAsset.Path ) );
			}
		}

		return results;
	}

	/// <summary>
	/// Validates that an asset path does not reference proprietary Facepunch, s&box, or private game assets.
	/// </summary>
	public static AssetDependencyAuditResult ValidateAssetDependency( string assetPath )
	{
		if ( string.IsNullOrWhiteSpace( assetPath ) )
		{
			return new AssetDependencyAuditResult( assetPath ?? "", false, "Asset path is empty." );
		}

		string lower = assetPath.ToLowerInvariant();

		foreach ( var keyword in ProprietaryKeywords )
		{
			if ( lower.Contains( keyword ) )
			{
				return new AssetDependencyAuditResult(
					AssetPath: assetPath,
					IsApproved: false,
					ViolationReason: $"Asset path contains proprietary keyword '{keyword}'. Proprietary Facepunch/s&box/game assets cannot be distributed."
				);
			}
		}

		// Also check machine-specific developer prefixes
		var pathAudit = AuditPathForPortability( assetPath );
		if ( pathAudit.IsMachineSpecific )
		{
			return new AssetDependencyAuditResult(
				AssetPath: assetPath,
				IsApproved: false,
				ViolationReason: $"Asset path is rooted in a machine-specific path: {pathAudit.MachineSpecificPrefix}"
			);
		}

		return new AssetDependencyAuditResult(
			AssetPath: assetPath,
			IsApproved: true,
			ViolationReason: null
		);
	}

	/// <summary>
	/// Batch validates a collection of asset paths.
	/// </summary>
	public static IReadOnlyList<AssetDependencyAuditResult> ValidateAssetDependencies( IEnumerable<string> assetPaths )
	{
		ArgumentNullException.ThrowIfNull( assetPaths );
		return assetPaths.Select( ValidateAssetDependency ).ToList();
	}

	/// <summary>
	/// Verifies build-time vs runtime independence of the Effigy kernel:
	/// - Kernel is compiled into an independent standalone assembly (Effigy.Kernel.dll).
	/// - Assembly can execute in memory without reading source files from disk.
	/// - Assembly does not reference UI, Windowing, or Silk.NET libraries.
	/// </summary>
	public static bool VerifyKernelRuntimeIndependence( out IReadOnlyList<string> diagnostics )
	{
		var diag = new List<string>();
		bool success = true;

		Assembly kernelAssembly = typeof(Effigy.Skeleton).Assembly;
		string name = kernelAssembly.GetName().Name ?? "";
		diag.Add( $"Kernel assembly name: {name}" );

		if ( name != "Effigy.Kernel" )
		{
			diag.Add( $"FAIL: Expected kernel assembly name 'Effigy.Kernel', got '{name}'." );
			success = false;
		}

		// Verify zero UI/Silk.NET references in the kernel
		var referenced = kernelAssembly.GetReferencedAssemblies();
		foreach ( var r in referenced )
		{
			string rName = r.Name ?? "";
			if ( rName.StartsWith( "Silk.NET", StringComparison.OrdinalIgnoreCase ) ||
			     rName.StartsWith( "System.Windows", StringComparison.OrdinalIgnoreCase ) ||
			     rName.StartsWith( "Marionette.Desktop", StringComparison.OrdinalIgnoreCase ) )
			{
				diag.Add( $"FAIL: Kernel assembly illegally references engine/UI assembly: {rName}" );
				success = false;
			}
		}

		// Verify that kernel code executes in-memory without source tree dependency
		try
		{
			var skeleton = new Effigy.Skeleton();
			var studio = new Effigy.PartStudio();
			diag.Add( $"Kernel instantiation in memory succeeded: {skeleton.GetType().FullName}, {studio.GetType().FullName}." );
		}
		catch ( Exception ex )
		{
			diag.Add( $"FAIL: Instantiating kernel types in memory threw: {ex.Message}" );
			success = false;
		}

		diagnostics = diag;
		return success;
	}
}
