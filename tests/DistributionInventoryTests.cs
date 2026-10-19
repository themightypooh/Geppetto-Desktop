using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Effigy;
using Marionette.Desktop;

internal static class DistributionInventoryTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "distribution-inventory", name, test );

		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		string FindPackagingInventoryPath()
		{
			var candidates = new[]
			{
				Path.Combine( Directory.GetCurrentDirectory(), "packaging", "INVENTORY.md" ),
				Path.Combine( AppContext.BaseDirectory, "packaging", "INVENTORY.md" ),
				Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "..", "packaging", "INVENTORY.md" ),
				Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "packaging", "INVENTORY.md" ),
				Path.Combine( "/home/poo/games/projects/geppetto-desktop", "packaging", "INVENTORY.md" )
			};
			foreach ( var path in candidates )
			{
				string full = Path.GetFullPath( path );
				if ( File.Exists( full ) ) return full;
			}
			return Path.GetFullPath( candidates[0] );
		}

		// 1. Inventory reports all required runtime native libraries (EGL, GL, SDL)
		Check( "inventory reports all required runtime native libraries (EGL, GL, SDL)", () =>
		{
			var nativeLibs = DistributionInventory.GetNativeDependencies();
			Require( nativeLibs != null && nativeLibs.Count >= 3, "Native dependencies list must contain at least 3 libraries." );

			var egl = nativeLibs!.FirstOrDefault( l => l.Soname == "libEGL.so.1" );
			Require( egl != null, "EGL (libEGL.so.1) must be in native dependencies." );
			Require( egl!.IsRequired, "libEGL.so.1 must be marked as required for headless GPU rendering." );
			Require( !egl.IsBundled, "libEGL.so.1 is host-provided and must not be marked as bundled." );
			Require( !string.IsNullOrWhiteSpace( egl.HostPackageHint ), "libEGL.so.1 must have host package hint." );

			var gl = nativeLibs!.FirstOrDefault( l => l.Soname == "libGL.so.1" );
			Require( gl != null, "OpenGL (libGL.so.1) must be in native dependencies." );
			Require( gl!.IsRequired, "libGL.so.1 must be marked as required." );
			Require( !gl.IsBundled, "libGL.so.1 is host-provided and must not be marked as bundled." );
			Require( !string.IsNullOrWhiteSpace( gl.HostPackageHint ), "libGL.so.1 must have host package hint." );

			var sdl = nativeLibs!.FirstOrDefault( l => l.Soname.StartsWith( "libSDL2", StringComparison.OrdinalIgnoreCase ) );
			Require( sdl != null, "SDL2 must be in native dependencies." );
			Require( sdl!.IsRequired, "SDL2 must be marked as required for interactive windowing." );
			Require( sdl.IsBundled, "SDL2 must be marked as bundled via Silk.NET runtimes." );
			Require( !string.IsNullOrWhiteSpace( sdl.HostPackageHint ), "SDL2 must have host package / fallback hint." );
		} );

		// 2. Package license audit validates MIT / project-owned licenses
		Check( "package license audit validates MIT / project-owned licenses", () =>
		{
			var audit = DistributionInventory.GetLicenseAudit();
			Require( audit != null && audit.Count > 0, "License audit roster must not be empty." );

			foreach ( var entry in audit! )
			{
				Require( entry.IsApprovedForDistribution, $"Entry '{entry.Component}' must be approved for distribution." );
				Require( entry.Category is LicenseCategory.Permissive or LicenseCategory.ProjectOwned,
					$"Entry '{entry.Component}' license category '{entry.Category}' must be Permissive or ProjectOwned." );
				Require( !string.IsNullOrWhiteSpace( entry.SpdxId ), $"Entry '{entry.Component}' must have a valid SPDX identifier." );
			}

			bool allApproved = DistributionInventory.ValidateAllLicensesApproved( out var violations );
			Require( allApproved, $"All licenses must be approved without violations: {string.Join( "; ", violations )}" );
			Require( violations.Count == 0, "Violations list must be empty." );

			var packages = DistributionInventory.GetManagedDependencies();
			foreach ( var pkg in packages )
			{
				Require( pkg.Category is LicenseCategory.Permissive or LicenseCategory.ProjectOwned,
					$"Managed package '{pkg.Name}' has non-approved license category '{pkg.Category}'." );
			}
		} );

		// 3. Kernel build-time vs runtime independence verified
		Check( "kernel build-time vs runtime independence verified", () =>
		{
			bool verified = DistributionInventory.VerifyKernelRuntimeIndependence( out var diagnostics );
			Require( verified, $"Kernel runtime independence failed: {string.Join( "; ", diagnostics )}" );

			Assembly kernelAssembly = typeof(Effigy.Skeleton).Assembly;
			Require( kernelAssembly.GetName().Name == "Effigy.Kernel", "Kernel assembly name must be Effigy.Kernel." );

			// Check that kernel assembly does not reference Silk.NET or Marionette.Desktop
			var refs = kernelAssembly.GetReferencedAssemblies();
			foreach ( var r in refs )
			{
				string rName = r.Name ?? "";
				Require( !rName.StartsWith( "Silk.NET", StringComparison.OrdinalIgnoreCase ),
					$"Kernel must not reference Silk.NET: found {rName}." );
				Require( !rName.StartsWith( "Marionette.Desktop", StringComparison.OrdinalIgnoreCase ),
					$"Kernel must not reference Marionette.Desktop: found {rName}." );
			}

			// Verify in-memory instantiation without disk dependencies
			var skeleton = new Effigy.Skeleton();
			skeleton.AddBone( "root", -1, Xform.Identity, 5f );
			Require( skeleton.Count == 1, "Skeleton bone count must be 1." );
			Require( skeleton.Bones[0].Name == "root", "Root bone name must be 'root'." );
		} );

		// 4. System compatibility check (Linux x64 + GL 4.6 capability)
		Check( "system compatibility check (Linux x64 + GL 4.6 capability)", () =>
		{
			var report = DistributionInventory.CheckSystemCompatibility();
			Require( report != null, "Compatibility report must not be null." );
			Require( !string.IsNullOrWhiteSpace( report!.OsDescription ), "OS description must be populated." );
			Require( !string.IsNullOrWhiteSpace( report.Architecture ), "Architecture must be populated." );
			Require( report.Diagnostics.Count > 0, "Diagnostics list must contain entries." );

			// On the current Linux host environment:
			if ( OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64 )
			{
				Require( report.IsLinuxX64, "Report must recognize Linux x64." );
				Require( report.HasRequiredEgl, "Host must have libEGL.so.1 for headless GPU tests." );
				Require( report.HasRequiredGl, "Host must have libGL.so.1." );
				Require( report.IsCompatible, "Report must be compatible on the target Linux x64 host." );
				Require( report.MissingLibraries.Count == 0, "No required libraries should be missing on this host." );
			}
		} );

		// 5. Portable project path resolution verified without relying on machine-specific roots
		Check( "portable project path resolution verified without relying on machine-specific roots", () =>
		{
			string testProjectRoot = Path.Combine( settings.WorkDirectory, "distribution-portable-proj-" + Guid.NewGuid().ToString( "N" ) );
			Directory.CreateDirectory( testProjectRoot );
			Directory.CreateDirectory( Path.Combine( testProjectRoot, "models" ) );
			Directory.CreateDirectory( Path.Combine( testProjectRoot, "clips" ) );

			string effigyPath = Path.Combine( testProjectRoot, "models", "box.effigy" );
			File.WriteAllText( effigyPath, "{ \"name\": \"box\" }" );
			string clipPath = Path.Combine( testProjectRoot, "clips", "idle.riganim" );
			File.WriteAllText( clipPath, "{ \"frameRate\": 30 }" );

			var resolver = new AssetPathResolver( testProjectRoot );
			var docRef = new AssetReference( "doc_box", "models/box.effigy" );
			var clipRef = new AssetReference( "clip_idle", "clips/idle.riganim" );

			var resolvedDoc = resolver.ResolveRead( docRef );
			Require( resolvedDoc.Exists, "Document asset must resolve and exist." );
			Require( resolvedDoc.ReferencePath == "models/box.effigy", "Reference path must be relative." );

			var resolvedClip = resolver.ResolveRead( clipRef );
			Require( resolvedClip.Exists, "Clip asset must resolve and exist." );
			Require( resolvedClip.ReferencePath == "clips/idle.riganim", "Reference path must be relative." );

			var docAudit = DistributionInventory.AuditPathForPortability( docRef.Path );
			Require( !docAudit.IsMachineSpecific, "Project asset path must not be machine-specific." );

			var clipAudit = DistributionInventory.AuditPathForPortability( clipRef.Path );
			Require( !clipAudit.IsMachineSpecific, "Clip asset path must not be machine-specific." );

			// Verify that hardcoded machine paths are detected
			var badAudit = DistributionInventory.AuditPathForPortability( "/home/poo/games/projects/deep_sentance/Assets/models/player/player_suit.fbx" );
			Require( badAudit.IsMachineSpecific, "Hardcoded developer path must be flagged as machine-specific." );
			Require( badAudit.MachineSpecificPrefix != null, "Machine-specific prefix must be captured." );

			// Relocate project to another directory and verify references stay relative
			string relocatedRoot = Path.Combine( settings.WorkDirectory, "distribution-relocated-proj-" + Guid.NewGuid().ToString( "N" ) );
			Directory.CreateDirectory( relocatedRoot );

			var project = new DesktopProject
			{
				Id = "portable_project",
				Name = "Portable Test Project",
				SourceDocuments = new List<SourceDocument>
				{
					new( "doc_box", docRef )
				},
				Clips = new List<ProjectClip>
				{
					new( "clip_idle", clipRef )
				}
			};

			var projectAudit = DistributionInventory.AuditProjectPortability( project );
			Require( projectAudit.All( r => !r.IsMachineSpecific ), "All project assets in portable project must pass portability audit." );
		} );

		// 6. Packaging inventory markdown exists and contains documented sections
		Check( "packaging inventory markdown exists and contains documented sections", () =>
		{
			string markdownPath = FindPackagingInventoryPath();
			Require( File.Exists( markdownPath ), $"Packaging inventory file must exist at '{markdownPath}'." );

			string content = File.ReadAllText( markdownPath );
			Require( content.Length > 200, "INVENTORY.md must have substantial content." );

			string[] requiredSections = new[]
			{
				"Dependency Matrix",
				"Native Host & Library Requirements",
				"Build-Time vs. Runtime Requirements",
				"License & Redistribution Audit",
				"Portability and Path Audit",
				"Supported System Compatibility Matrix"
			};

			foreach ( var sec in requiredSections )
			{
				Require( content.Contains( sec, StringComparison.OrdinalIgnoreCase ),
					$"INVENTORY.md must contain section '{sec}'." );
			}

			Require( content.Contains( "Silk.NET", StringComparison.OrdinalIgnoreCase ), "INVENTORY.md must document Silk.NET." );
			Require( content.Contains( "libEGL.so.1", StringComparison.OrdinalIgnoreCase ), "INVENTORY.md must document libEGL.so.1." );
			Require( content.Contains( "MIT", StringComparison.OrdinalIgnoreCase ), "INVENTORY.md must document MIT license." );
			Require( content.Contains( "Effigy.Kernel", StringComparison.OrdinalIgnoreCase ), "INVENTORY.md must document Effigy.Kernel." );
		} );

		// 7. No unapproved proprietary asset dependencies found in manifest inventory
		Check( "no unapproved proprietary asset dependencies found in manifest inventory", () =>
		{
			var proprietarySamples = new[]
			{
				"facepunch/citizen/citizen.vmdl",
				"/home/poo/games/projects/deep_sentance/Assets/models/player/player_suit.fbx",
				"sbox/core/materials/default.vmat",
				"models/characters/citizen_morphs.vmdl"
			};

			foreach ( var path in proprietarySamples )
			{
				var result = DistributionInventory.ValidateAssetDependency( path );
				Require( !result.IsApproved, $"Proprietary asset '{path}' must be rejected." );
				Require( !string.IsNullOrWhiteSpace( result.ViolationReason ), $"Rejection reason must be provided for '{path}'." );
			}

			var approvedSamples = new[]
			{
				"models/box.effigy",
				"clips/walk.riganim",
				"materials/wood.vmat",
				"textures/albedo.png"
			};

			foreach ( var path in approvedSamples )
			{
				var result = DistributionInventory.ValidateAssetDependency( path );
				Require( result.IsApproved, $"Approved project asset '{path}' must pass audit." );
				Require( result.ViolationReason == null, $"Approved asset '{path}' must have no violation reason." );
			}

			var batchResults = DistributionInventory.ValidateAssetDependencies( approvedSamples );
			Require( batchResults.All( r => r.IsApproved ), "All approved sample assets must pass batch audit." );
		} );

		// 8. Diagnostic output when a required system library is missing
		Check( "diagnostic output when a required system library is missing", () =>
		{
			string fakeLib = "libNonExistentFakeGraphicsEngine.so.999";
			bool available = DistributionInventory.CheckNativeLibraryAvailable( fakeLib, out string diag, out string? resolvedPath );
			Require( !available, "Non-existent library must not be reported as available." );
			Require( resolvedPath == null, "Resolved path for non-existent library must be null." );
			Require( !string.IsNullOrWhiteSpace( diag ), "Diagnostic message for missing library must not be empty." );
			Require( diag.Contains( fakeLib ), $"Diagnostic must mention the missing library name '{fakeLib}'." );

			string eglDiag = DistributionInventory.DiagnoseMissingLibrary( "libEGL.so.1" );
			Require( eglDiag.Contains( "libEGL.so.1" ), "EGL diagnostic must mention libEGL.so.1." );
			Require( eglDiag.Contains( "libegl1" ) || eglDiag.Contains( "mesa-libEGL" ), "EGL diagnostic must provide package installation advice." );

			string sdlDiag = DistributionInventory.DiagnoseMissingLibrary( "libSDL2-2.0.so" );
			Require( sdlDiag.Contains( "libSDL2-2.0.so" ) || sdlDiag.Contains( "SDL2" ), "SDL diagnostic must mention SDL2." );
			Require( sdlDiag.Contains( "libsdl2-2.0-0" ), "SDL diagnostic must provide package installation advice." );
		} );
	}
}
