using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Effigy;

// Mesh booleans (union / difference / intersect) through Manifold (Apache 2.0)
// via libgmmanifold.so, built by the Mobile tree's tools/build-manifold.sh
// (Manifold v3.5.4 + tools/manifold/gm_manifold.cpp; the Desktop copy of the
// Linux x86_64 binary lives in packaging/native/linux-x86_64/, see
// packaging/MANIFOLD.md). Ported from Mobile app/ManifoldBoolean.cs with the
// Godot parts removed: no OS.GetName / ProjectSettings / GD.Print anywhere in
// here — the kernel stays engine-free, and the host is plain .NET.
//
// Installed as the kernel's MeshBoolean.Provider, so every boolean (CAD
// Boolean, Model body booleans, cut features) goes through it. It fails
// soft: when the library is missing, or Manifold refuses an input as not
// manifold, the kernel's own ExactMeshBoolean runs instead and LastRoute
// says why. Install() only replaces the default exact provider; a host
// that explicitly cleared the provider, or installed its own, is left
// alone.
//
// WHAT THE MANIFOLD ROUTE KEEPS AND LOSES. The native ABI carries
// positions, triangles and one face id per triangle, so per-face Material
// survives (rebuilt from the face id). Per-corner UVs do NOT cross the
// ABI — a Manifold-routed result has zero UVs, exactly as in Mobile. The
// built-in fallback keeps UVs where a face passes through unsplit, so a
// caller that needs UVs and can live with the exact boolean should use
// the built-in provider directly.
//
// HOST WIRING (final integrator, shared entry point — not this file):
// call ManifoldBoolean.Install() once at Desktop startup, before the
// first rebuild, and optionally install a Log sink so fallback notices
// reach the Desktop log instead of being silent. Until then the default
// ExactMeshBoolean provider runs and nothing changes.
public sealed unsafe class ManifoldBoolean : IMeshBoolean
{
	public static readonly ManifoldBoolean Instance = new();

	/// <summary>Which engine ran the last boolean and why, e.g. "manifold" or "built-in (tool: not manifold)".</summary>
	public static string LastRoute { get; private set; } = "";

	/// <summary>Where fallback notices go. Null (the default) means silent; the host
	/// installs a sink that writes to its log. Never throws into the caller.</summary>
	public static Action<string> Log { get; set; }

	/// <summary>Explicit library path, for hosts and tests. When set, it is the only
	/// candidate tried; setting it resets the loader, so a test can force the
	/// missing-library path with a nonexistent path. Null means search the
	/// standard locations. The GEPPETTO_MANIFOLD_LIB environment variable is
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

	/// <summary>Makes Manifold the boolean provider (the built-in boolean stays as its fallback).</summary>
	public static void Install()
	{
		if ( MeshBoolean.Provider is ExactMeshBoolean ) MeshBoolean.Provider = Instance;
	}

	static string _libraryPathOverride;
	static bool _tried;
	static IntPtr _lib;
	static readonly object _loadLock = new();
	static delegate* unmanaged[Cdecl]<int, float*, uint, uint*, uint, uint*, float*, uint, uint*, uint, uint*, IntPtr> _boolean;
	static delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, double*, void> _counts;
	static delegate* unmanaged[Cdecl]<IntPtr, float*, uint*, uint*, void> _copy;
	static delegate* unmanaged[Cdecl]<IntPtr, void> _free;
	static delegate* unmanaged[Cdecl]<IntPtr> _error;

	static string LibraryFileName() => OperatingSystem.IsWindows() ? "gmmanifold.dll"
		: OperatingSystem.IsMacOS() ? "libgmmanifold.dylib"
		: "libgmmanifold.so";

	static string RuntimeId() => (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")
		+ "-" + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");

	static IEnumerable<string> Candidates()
	{
		string file = LibraryFileName();

		// Explicit overrides win outright: one candidate, no searching.
		string explicitPath = _libraryPathOverride;
		if ( string.IsNullOrEmpty( explicitPath ) )
		{
			try { explicitPath = Environment.GetEnvironmentVariable( "GEPPETTO_MANIFOLD_LIB" ); } catch { }
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

		// System search path (a host-installed Manifold shim).
		yield return file;
	}

	/// <summary>True when libgmmanifold loaded. Never throws: a missing, corrupt
	/// or wrong-build library just means the built-in fallback runs.</summary>
	public static bool Available()
	{
		lock ( _loadLock )
		{
			if ( _tried ) return _lib != IntPtr.Zero;
			_tried = true;
			foreach ( var c in Candidates() )
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
					_boolean = (delegate* unmanaged[Cdecl]<int, float*, uint, uint*, uint, uint*, float*, uint, uint*, uint, uint*, IntPtr>)NativeLibrary.GetExport( h, "gm_manifold_boolean" );
					_counts = (delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, double*, void>)NativeLibrary.GetExport( h, "gm_manifold_counts" );
					_copy = (delegate* unmanaged[Cdecl]<IntPtr, float*, uint*, uint*, void>)NativeLibrary.GetExport( h, "gm_manifold_copy" );
					_free = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport( h, "gm_manifold_free" );
					_error = (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport( h, "gm_manifold_error" );
					_lib = h;
					return true;
				}
				catch ( EntryPointNotFoundException ) { NativeLibrary.Free( h ); }
			}
			return false;
		}
	}

	/// <summary>Forget the cached load attempt (and unload the library, if one
	/// was loaded) so the next Available() tries again from scratch. Host and
	/// test hook; never throws.</summary>
	public static void ResetLoader()
	{
		lock ( _loadLock )
		{
			if ( _lib != IntPtr.Zero )
			{
				try { NativeLibrary.Free( _lib ); } catch { }
			}
			_lib = IntPtr.Zero;
			_boolean = null;
			_counts = null;
			_copy = null;
			_free = null;
			_error = null;
			_tried = false;
		}
	}

	const uint ToolBit = 0x80000000u;

	/// <summary>Welded, triangulated arrays for Manifold: positions (xyz), triangles, and the source face per triangle.</summary>
	static void Flatten( PolyMesh m, uint tag, out float[] pos, out uint[] tri, out uint[] face )
	{
		var weld = new Dictionary<Vec3, int>();
		var remap = new int[m.Positions.Count];
		var p = new List<float>( m.Positions.Count * 3 );
		for ( int i = 0; i < m.Positions.Count; i++ )
		{
			var v = m.Positions[i];
			if ( !weld.TryGetValue( v, out var j ) ) { j = weld.Count; weld[v] = j; p.Add( v.x ); p.Add( v.y ); p.Add( v.z ); }
			remap[i] = j;
		}
		var t = new List<uint>( m.Faces.Count * 6 );
		var f = new List<uint>( m.Faces.Count * 2 );
		var local = new List<Vec3>();
		for ( int fi = 0; fi < m.Faces.Count; fi++ )
		{
			var idx = m.Faces[fi].Indices;
			if ( idx.Length < 3 ) continue;
			void Add( int a, int b, int c )
			{
				int ra = remap[idx[a]], rb = remap[idx[b]], rc = remap[idx[c]];
				if ( ra == rb || rb == rc || ra == rc ) return;
				t.Add( (uint)ra ); t.Add( (uint)rb ); t.Add( (uint)rc ); f.Add( tag | (uint)fi );
			}
			if ( idx.Length == 3 ) { Add( 0, 1, 2 ); continue; }
			local.Clear();
			foreach ( var k in idx ) local.Add( m.Positions[k] );
			var tris = Triangulate.Face( local );
			if ( tris == null || tris.Count == 0 ) for ( int k = 1; k + 1 < idx.Length; k++ ) Add( 0, k, k + 1 );
			else foreach ( var (a, b, c) in tris ) Add( a, b, c );
		}
		pos = p.ToArray(); tri = t.ToArray(); face = f.ToArray();
	}

	/// <summary>The first reason this mesh cannot go through Manifold, or null
	/// when it is closed and valid. Manifold only accepts closed 2-manifold
	/// inputs, and the built-in boolean refuses open inputs too, so routing
	/// these to the fallback never skips a native success — it just names the
	/// reason up front instead of letting the native call refuse.</summary>
	static string ClosedProblem( string which, PolyMesh m )
	{
		var check = MeshValidator.Validate( m );
		if ( check.BoundaryEdges > 0 && check.NonManifoldEdges > 0 )
			return $"{which} is open ({check.BoundaryEdges} boundary edge(s)) and not manifold ({check.NonManifoldEdges} non-manifold edge(s))";
		if ( check.BoundaryEdges > 0 ) return $"{which} is open ({check.BoundaryEdges} boundary edge(s))";
		if ( check.NonManifoldEdges > 0 ) return $"{which} is not manifold ({check.NonManifoldEdges} non-manifold edge(s))";
		if ( check.Errors.Count > 0 ) return $"{which} is invalid ({check.Errors[0]})";
		return null;
	}

	public bool TryApply( BooleanOp op, PolyMesh target, PolyMesh tool, out PolyMesh result, out string error )
	{
		result = null; error = null;
		if ( target is null || tool is null || target.FaceCount == 0 || tool.FaceCount == 0 )
		{ error = "A boolean needs two solids with faces."; return false; }
		if ( !Available() ) return Fallback( "library not installed", op, target, tool, out result, out error );
		var problem = ClosedProblem( "target", target ) ?? ClosedProblem( "tool", tool );
		if ( problem != null ) return Fallback( problem, op, target, tool, out result, out error );
		var r = RunManifold( op, target, tool, out var why, out _ );
		if ( r == null ) return Fallback( why, op, target, tool, out result, out error );
		if ( r.FaceCount == 0 && op == BooleanOp.Intersect )
		{ LastRoute = "manifold"; error = "The solids do not overlap, so there is no shared volume to keep."; return false; }
		LastRoute = "manifold";
		result = r;
		return true;
	}

	static bool Fallback( string why, BooleanOp op, PolyMesh target, PolyMesh tool, out PolyMesh result, out string error )
	{
		LastRoute = "built-in (" + why + ")";
		try { Log?.Invoke( $"[manifold] fell back to the built-in boolean: {why}" ); } catch { }
		return ExactMeshBoolean.Instance.TryApply( op, target, tool, out result, out error );
	}

	/// <summary>The Manifold boolean alone (no fallback). Null with <paramref name="why"/> when Manifold refused.</summary>
	public static PolyMesh RunManifold( BooleanOp op, PolyMesh target, PolyMesh tool, out string why, out double volume )
	{
		why = null; volume = 0;
		if ( !Available() ) { why = "library not installed"; return null; }
		Flatten( target, 0, out var ap, out var at, out var af );
		Flatten( tool, ToolBit, out var bp, out var bt, out var bf );
		int o = op == BooleanOp.Union ? 0 : op == BooleanOp.Subtract ? 1 : 2;
		IntPtr h;
		fixed ( float* pa = ap ) fixed ( uint* ta = at ) fixed ( uint* fa = af )
		fixed ( float* pb = bp ) fixed ( uint* tb = bt ) fixed ( uint* fb = bf )
			h = _boolean( o, pa, (uint)(ap.Length / 3), ta, (uint)(at.Length / 3), fa, pb, (uint)(bp.Length / 3), tb, (uint)(bt.Length / 3), fb );
		if ( h == IntPtr.Zero ) { why = Marshal.PtrToStringUTF8( _error() ) ?? "refused"; return null; }
		try
		{
			uint nv, nt; double vol;
			_counts( h, &nv, &nt, &vol );
			volume = vol;
			var pos = new float[nv * 3]; var tri = new uint[nt * 3]; var face = new uint[nt];
			fixed ( float* pp = pos ) fixed ( uint* tp = tri ) fixed ( uint* fp = face ) _copy( h, pp, tp, fp );
			var mesh = new PolyMesh();
			for ( int i = 0; i < nv; i++ ) mesh.Positions.Add( new Vec3( pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2] ) );
			for ( int i = 0; i < nt; i++ )
			{
				uint id = face[i];
				int material = 0;
				if ( id != 0xFFFFFFFFu )
				{
					var src = (id & ToolBit) != 0 ? tool : target;
					int fi = (int)(id & ~ToolBit);
					if ( fi < src.Faces.Count ) material = src.Faces[fi].Material;
				}
				mesh.Faces.Add( new Face( new[] { (int)tri[i * 3], (int)tri[i * 3 + 1], (int)tri[i * 3 + 2] }, null, material ) );
			}
			// Same face cleanup the built-in boolean ends with: coplanar triangles back into n-gons.
			// Kept only when the merged mesh is still closed.
			var merged = mesh.Clone();
			CoplanarMerge.Merge( merged );
			return MeshValidator.Validate( merged ).IsClosed ? merged : mesh;
		}
		finally { _free( h ); }
	}
}
