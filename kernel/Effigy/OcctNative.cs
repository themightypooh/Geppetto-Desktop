using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Effigy;

// OCCT native loader (GODLIKE WAVE 1 P0, godlike-occt-brep-core-step-export).
//
// Desktop needs its own OCCT native loader + shipped Linux .so — the same pattern as
// ManifoldBoolean's libgmmanifold.so bundling (see kernel/Effigy/ManifoldBoolean.cs and
// packaging/MANIFOLD.md). Mobile binds OCCT through Godot paths; Desktop is plain .NET.
//
// WHAT THIS LOADER DOES: it finds and loads the system OCCT kernel library (libTKernel)
// and reports the version it actually loaded, plus whether the STEP module (libTKSTEP)
// is present. It does NOT route any OCCT operation yet: OCCT is C++, not a C ABI, so
// booleans / fillets / STEP-through-OCCT need a small C++ shim exposing a C ABI (the
// follow-on in packaging/OCCT.md), exactly like Mobile's gm_manifold.cpp shim.
//
// UNTIL THE SHIM LANDS, the working B-rep routes are the managed ones: the analytic core
// (BrepSolid) and the STEP text exchange (StepExchange). Nothing here claims an OCCT
// operation works when it does not: when the library is missing, Available() is false
// and LastRoute names the missing library, and BrepSolid.Boolean / Fillet return their
// named errors. The faceted-shell label and the Mobile truth gate stay in place.
//
// HOST WIRING (final integrator, shared entry point — not this file): nothing to wire yet.
// When the shim exists, its Install() will sit next to ManifoldBoolean.Install() in
// Program.Main; until then this loader is only a probe (and the tests assert it reports
// honestly either way).
public sealed unsafe class OcctNative
{
	/// <summary>Which OCCT was found and why, e.g. "occt 7.9.3 (/usr/lib/libTKernel.so)" or "unavailable (libTKernel not found)".</summary>
	public static string LastRoute { get; private set; } = "";

	/// <summary>Version string of the loaded libTKernel (e.g. "7.9.3"), or "" when unavailable.</summary>
	public static string Version { get; private set; } = "";

	/// <summary>Where loader notices go. Null (the default) means silent; the host installs a sink
	/// that writes to its log. Never throws into the caller.</summary>
	public static Action<string> Log { get; set; }

	/// <summary>Explicit library path, for hosts and tests. When set, it is the only candidate tried;
	/// setting it resets the loader, so a test can force the missing-library path with a nonexistent
	/// path. Null means search the standard locations. The GEPPETTO_OCCT_LIB environment variable is
	/// the same override without code.</summary>
	public static string LibraryPathOverride
	{
		get => _libraryPathOverride;
		set
		{
			if ( _libraryPathOverride == value ) return;
			_libraryPathOverride = value;
			ResetLoader();
		}
	}

	static string _libraryPathOverride;
	static bool _tried;
	static IntPtr _lib;
	static IntPtr _stepLib;
	static readonly object _loadLock = new();
	static delegate* unmanaged[Cdecl]<IntPtr> _versionString;

	static string LibraryFileName() => OperatingSystem.IsWindows() ? "TKernel.dll"
		: OperatingSystem.IsMacOS() ? "libTKernel.dylib"
		: "libTKernel.so";

	static string StepLibraryFileName() => OperatingSystem.IsWindows() ? "TKSTEP.dll"
		: OperatingSystem.IsMacOS() ? "libTKSTEP.dylib"
		: "libTKSTEP.so";

	static string RuntimeId() => (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")
		+ "-" + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");

	static IEnumerable<string> Candidates( string file )
	{
		string explicitPath = _libraryPathOverride;
		if ( string.IsNullOrEmpty( explicitPath ) )
		{
			try { explicitPath = Environment.GetEnvironmentVariable( "GEPPETTO_OCCT_LIB" ); } catch { }
		}
		if ( !string.IsNullOrEmpty( explicitPath ) ) { yield return explicitPath; yield break; }

		string appDir = null;
		try { appDir = AppContext.BaseDirectory; } catch { }

		if ( appDir != null )
		{
			// Bundled native layout, the same one DistributionInventory probes:
			// runtimes/<rid>/native/ next to the published binary.
			yield return Path.Combine( appDir, "runtimes", RuntimeId(), "native", file );
			// Next to the binary, the Mobile Linux export layout.
			yield return Path.Combine( appDir, file );
		}

		// System search path (a host-installed OCCT, e.g. FreeCAD's 7.9 on this PC).
		yield return file;
		// Versioned soname, for systems that only carry it (libTKernel.so.7.9).
		if ( file.EndsWith( ".so", StringComparison.Ordinal ) ) yield return file + ".7.9";
	}

	/// <summary>True when libTKernel loaded and OCCT_Version_String resolved. Never throws: a missing,
	/// corrupt or wrong-build library just means the native route is unavailable.</summary>
	public static bool Available()
	{
		lock ( _loadLock )
		{
			if ( _tried ) return _lib != IntPtr.Zero;
			_tried = true;
			foreach ( var c in Candidates( LibraryFileName() ) )
			{
				if ( string.IsNullOrEmpty( c ) ) continue;
				if ( (c.Contains( '/' ) || c.Contains( '\\' )) && !File.Exists( c ) ) continue;
				IntPtr h;
				try
				{
					if ( !NativeLibrary.TryLoad( c, out h ) ) continue;
				}
				catch ( BadImageFormatException ) { continue; }
				catch ( DllNotFoundException ) { continue; }
				try
				{
					_versionString = (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport( h, "OCCT_Version_String" );
					_lib = h;
					try { Version = Marshal.PtrToStringAnsi( _versionString() ) ?? ""; }
					catch { Version = ""; }
					LastRoute = $"occt {Version} ({c})";
					try { Log?.Invoke( $"OCCT native library loaded: {LastRoute}" ); } catch { }
					return true;
				}
				catch ( EntryPointNotFoundException ) { NativeLibrary.Free( h ); }
			}
			LastRoute = "unavailable (libTKernel not found in runtimes/<rid>/native, the app directory, or the system path; set GEPPETTO_OCCT_LIB to point at it)";
			try { Log?.Invoke( "OCCT native library unavailable: " + LastRoute ); } catch { }
			return false;
		}
	}

	/// <summary>True when the STEP module (libTKSTEP) loads alongside the kernel. False when the kernel
	/// itself is unavailable; never throws. STEP-through-OCCT still needs the C++ shim — this only
	/// reports the module's presence.</summary>
	public static bool StepAvailable()
	{
		if ( !Available() ) return false;
		lock ( _loadLock )
		{
			if ( _stepLib != IntPtr.Zero ) return true;
			foreach ( var c in Candidates( StepLibraryFileName() ) )
			{
				if ( string.IsNullOrEmpty( c ) ) continue;
				if ( (c.Contains( '/' ) || c.Contains( '\\' )) && !File.Exists( c ) ) continue;
				try
				{
					if ( NativeLibrary.TryLoad( c, out var h ) ) { _stepLib = h; return true; }
				}
				catch ( BadImageFormatException ) { continue; }
				catch ( DllNotFoundException ) { continue; }
			}
			return false;
		}
	}

	/// <summary>Forget the cached load attempt (and unload the libraries, if loaded) so the next
	/// Available() tries again from scratch. Host and test hook; never throws.</summary>
	public static void ResetLoader()
	{
		lock ( _loadLock )
		{
			if ( _lib != IntPtr.Zero )
			{
				try { NativeLibrary.Free( _lib ); } catch { }
			}
			if ( _stepLib != IntPtr.Zero )
			{
				try { NativeLibrary.Free( _stepLib ); } catch { }
			}
			_lib = IntPtr.Zero;
			_stepLib = IntPtr.Zero;
			_versionString = null;
			Version = "";
			LastRoute = "";
			_tried = false;
		}
	}
}
