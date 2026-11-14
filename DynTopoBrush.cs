using System;
using System.Collections.Generic;
using System.Diagnostics;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// DynTopo integration into the brush system (godlike-dyntopo-brush-integration, Wave 3 P2).
///
/// This is the BRUSH-SYSTEM slice, not DynTopo core: the core already exists as
/// <see cref="DyntopoService"/> (split long edges / collapse short edges / brush
/// displacement, every method returning a new mesh, never mutating its input).
/// This file wires that core into the brush path:
///
///   (1) per-brush detail size + DynTopo toggle, read off <see cref="BrushPreset"/>
///       (DetailSize / DynTopoEnabled — the only two fields this ticket adds to
///       the preset; both default to off/auto so existing strokes are unchanged);
///   (2) stroke-engine integration: <see cref="StrokeEngineApi.BuildSamples"/>
///       dabs drive tessellation, so spacing / dash / jitter under live
///       tessellation are the stroke engine's own behaviour, not a second copy;
///   (3) decimation on Smooth: the Smooth dab collapses short edges in the
///       brushed region (the DynTopo collapse, not a whole-mesh pass);
///   (4) multires level switching with separate sculpt/view levels, and the
///       one-key voxel-remesh path;
///   (5) hard cap + BVH rebuild signalling.
///
/// Shape: everything here is pure functions over PolyMesh. The session
/// (SculptSession) owns the mesh, the BVH and the undo stack; it calls
/// <see cref="DynTopoBrushStroke.Apply"/> per dab batch and rebuilds its BVH
/// from the result's <see cref="DynTopoBrushResult.TopologyChanged"/> flag
/// (see <see cref="DynTopoBvh.Rebuild"/>). Nothing here edits SculptSession.cs,
/// StrokeEngine.cs or BrushLibrary.cs — one writer per shared file — and the
/// DynTopo core task (godlike-dyntopo-dynamic-topology, a sibling) owns the
/// core; this file only CALLS it.
///
/// What is deliberately NOT here: the voxel engine itself. The remesh key
/// routes to <see cref="RemeshFeature.VoxelBackend"/> (installed by
/// RetopoService when a voxel engine is available — the OpenVDB task's half)
/// and refuses honestly while no backend is installed, instead of silently
/// doing nothing or pretending the built-in SurfaceNets ran.
/// </summary>
public sealed class DynTopoBrushSettings
{
	/// <summary>Target edge length in scene units. 0 = auto (brush radius x 0.1).</summary>
	public float DetailSize { get; set; }

	/// <summary>DynTopo on for this brush. Off by default: opt-in per brush.</summary>
	public bool DynTopoEnabled { get; set; }

	/// <summary>Smooth also decimates (collapses short edges in the brushed region). Default true.</summary>
	public bool SmoothDecimates { get; set; } = true;

	/// <summary>Hard face cap. 0 = no cap. Subdivision stops at the cap; an input
	/// already over it gets one proportional QEM pass toward it (triangles out — see
	/// <see cref="Decimate"/>).</summary>
	public int HardCapFaces { get; set; }

	/// <summary>Voxel size for the remesh key, in scene units. 0 = auto (detail x 2).</summary>
	public float VoxelSize { get; set; }

	/// <summary>Taubin smoothing passes over the voxel skin (RemeshFeature's Smooth).</summary>
	public int VoxelSmoothPasses { get; set; } = 2;

	/// <summary>DynTopo passes per dab (DyntopoService.Options.MaxIterations). 2 is the
	/// brush default: one dab should not re-tessellate the whole region four times.</summary>
	public int MaxDetailPasses { get; set; } = 2;

	public DynTopoBrushSettings Clone() => (DynTopoBrushSettings)MemberwiseClone();

	/// <summary>Detail size for a brush of this radius: the explicit value, else
	/// radius x 0.1 (floored at 0.01 so a hairline brush still tessellates).</summary>
	public float ResolveDetail( float brushRadius )
	{
		if ( DetailSize > 0f ) return DetailSize;
		return Math.Max( 0.01f, brushRadius * 0.1f );
	}

	/// <summary>Settings for a brush preset: its DynTopo fields, everything else default.</summary>
	public static DynTopoBrushSettings ForPreset( BrushPreset preset )
	{
		if ( preset is null ) throw new ArgumentNullException( nameof( preset ) );
		return new DynTopoBrushSettings { DetailSize = preset.DetailSize, DynTopoEnabled = preset.DynTopoEnabled };
	}

	internal void Validate()
	{
		if ( DetailSize < 0f || !float.IsFinite( DetailSize ) )
			throw new ArgumentException( "DetailSize must be >= 0 (0 = auto)" );
		if ( HardCapFaces < 0 )
			throw new ArgumentException( "HardCapFaces must be >= 0 (0 = no cap)" );
		if ( MaxDetailPasses < 1 )
			throw new ArgumentException( "MaxDetailPasses must be at least 1" );
		if ( VoxelSize < 0f || !float.IsFinite( VoxelSize ) )
			throw new ArgumentException( "VoxelSize must be >= 0 (0 = auto)" );
		if ( VoxelSmoothPasses < 0 )
			throw new ArgumentException( "VoxelSmoothPasses must be >= 0" );
	}
}

public enum DynTopoBrushMode { Draw, Smooth, Flatten, Subdivide }

public sealed class DynTopoBrushResult
{
	public PolyMesh Mesh { get; set; }
	public int FacesBefore { get; set; }
	public int FacesAfter { get; set; }
	public int DabsApplied { get; set; }
	public int TessellatedDabs { get; set; }
	public int SkippedAtCap { get; set; }
	public bool Capped { get; set; }
	public bool DecimatedToCap { get; set; }
	public bool TopologyChanged { get; set; }
	public TimeSpan Elapsed { get; set; }
}

public static class DynTopoBrushStroke
{
	static DyntopoService.BrushMode MapMode( DynTopoBrushMode mode ) => mode switch
	{
		DynTopoBrushMode.Smooth => DyntopoService.BrushMode.Smooth,
		DynTopoBrushMode.Flatten => DyntopoService.BrushMode.Flatten,
		DynTopoBrushMode.Subdivide => DyntopoService.BrushMode.Subdivide,
		_ => DyntopoService.BrushMode.Inflate,
	};

	static Vec3 BrushDirection( BrushSample s )
	{
		if ( s.Direction.Length > 0f ) return s.Direction;
		if ( s.Normal.Length > 0f ) return s.Normal;
		return new Vec3( 0, 0, 1 );
	}

	static PolyMesh Tessellate( PolyMesh current, BrushSample s, float detail, DynTopoBrushSettings settings, ref int tessellated, ref int skippedAtCap, ref bool capped )
	{
		if ( settings.HardCapFaces > 0 && current.FaceCount >= settings.HardCapFaces )
		{
			skippedAtCap++;
			capped = true;
			return current;
		}
		var next = DyntopoService.Subdivide( current, new DyntopoService.Options
		{
			Center = s.Position,
			Radius = s.Radius,
			MaxEdgeLength = detail,
			MaxIterations = settings.MaxDetailPasses,
		} );
		tessellated++;
		return next;
	}

	static PolyMesh DecimateToCap( PolyMesh mesh, int capFaces )
	{
		int tris = Decimate.TriangleCount( mesh );
		int targetTris = Math.Max( 4, (int)Math.Round( (double)tris * capFaces / Math.Max( 1, mesh.FaceCount ) ) );
		if ( targetTris >= tris ) return mesh;
		return Decimate.ToTriangles( mesh, targetTris );
	}

	/// <summary>
	/// Run one brush stroke (stroke-engine dabs) over a mesh. Per dab: tessellate
	/// the brushed region to the brush's detail size, then displace — except Smooth,
	/// which displaces first and then tessellates, so the edges smoothing shortened
	/// are the ones that collapse (that collapse IS the Smooth decimation).
	/// The input mesh is never mutated; every step returns a new mesh.
	/// </summary>
	public static DynTopoBrushResult Apply( PolyMesh mesh, IReadOnlyList<BrushSample> samples, DynTopoBrushSettings settings, DynTopoBrushMode mode, Action<PolyMesh> onTopologyChanged = null )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( samples is null ) throw new ArgumentNullException( nameof( samples ) );
		if ( settings is null ) throw new ArgumentNullException( nameof( settings ) );
		settings.Validate();

		var sw = Stopwatch.StartNew();
		var current = mesh;
		int facesBefore = mesh.FaceCount;
		int tessellated = 0, skippedAtCap = 0, dabs = 0;
		bool capped = false, decimated = false;

		// Over-cap input: one proportional QEM pass toward the cap before stroking.
		if ( settings.HardCapFaces > 0 && current.FaceCount > settings.HardCapFaces )
		{
			current = DecimateToCap( current, settings.HardCapFaces );
			decimated = true;
			capped = true;
		}

		// Auto detail derives from the widest dab, so tessellation is stable across
		// the stroke even as pressure varies the per-dab radius.
		float detail = 0f;
		if ( settings.DynTopoEnabled )
		{
			float maxRadius = 0f;
			foreach ( var s in samples ) maxRadius = Math.Max( maxRadius, s.Radius );
			detail = settings.ResolveDetail( maxRadius );
		}

		foreach ( var s in samples )
		{
			if ( s.Radius <= 0f || !float.IsFinite( s.Radius ) ) continue;
			dabs++;

			if ( mode == DynTopoBrushMode.Smooth )
			{
				current = DyntopoService.Sculpt( current, new DyntopoService.SculptOptions
				{
					Center = s.Position,
					Radius = s.Radius,
					Mode = DyntopoService.BrushMode.Smooth,
					Strength = s.Strength,
					Direction = BrushDirection( s ),
				} );
				if ( settings.DynTopoEnabled && settings.SmoothDecimates )
					current = Tessellate( current, s, detail, settings, ref tessellated, ref skippedAtCap, ref capped );
			}
			else
			{
				if ( settings.DynTopoEnabled )
					current = Tessellate( current, s, detail, settings, ref tessellated, ref skippedAtCap, ref capped );
				current = DyntopoService.Sculpt( current, new DyntopoService.SculptOptions
				{
					Center = s.Position,
					Radius = s.Radius,
					Mode = MapMode( mode ),
					Strength = s.Strength,
					Direction = BrushDirection( s ),
				} );
			}

			// A single Subdivide call can blow past the cap in its own iterations,
			// so enforce after every dab, not just before tessellation.
			if ( settings.HardCapFaces > 0 && current.FaceCount > settings.HardCapFaces )
			{
				current = DecimateToCap( current, settings.HardCapFaces );
				decimated = true;
				capped = true;
			}
		}

		bool topologyChanged = current.FaceCount != facesBefore || current.VertexCount != mesh.VertexCount;
		if ( topologyChanged ) onTopologyChanged?.Invoke( current );
		sw.Stop();
		return new DynTopoBrushResult
		{
			Mesh = current,
			FacesBefore = facesBefore,
			FacesAfter = current.FaceCount,
			DabsApplied = dabs,
			TessellatedDabs = tessellated,
			SkippedAtCap = skippedAtCap,
			Capped = capped,
			DecimatedToCap = decimated,
			TopologyChanged = topologyChanged,
			Elapsed = sw.Elapsed,
		};
	}
}

/// <summary>
/// Multires level switching with separate sculpt and view levels (ticket (4)).
///
/// The view level is stored state on the sculpt (MultiresSculpt.ViewLevel, display
/// only by design — it never bumps Revision). The sculpt level is NOT stored: it is
/// the per-stroke choice passed to SculptTarget.ApplyStroke. This class keeps the
/// pair together so the brush UI cannot silently sculpt at the level it happens to
/// be displaying. SwitchView forces the evaluate so the reported time is the real
/// switch cost, not a lazy cache fill on the next frame.
/// </summary>
public sealed class DynTopoLevelSwitch
{
	public int Level { get; set; }
	public TimeSpan Elapsed { get; set; }
	public int Vertices { get; set; }
	public int Faces { get; set; }
}

public static class DynTopoLevels
{
	public static DynTopoLevelSwitch SwitchView( MultiresSculpt sculpt, int level )
	{
		if ( sculpt is null ) throw new ArgumentNullException( nameof( sculpt ) );
		int resolved = SculptTarget.ResolveLevel( sculpt, level );
		var sw = Stopwatch.StartNew();
		sculpt.ViewLevel = resolved;
		var mesh = sculpt.Evaluate( resolved );
		sw.Stop();
		return new DynTopoLevelSwitch { Level = resolved, Elapsed = sw.Elapsed, Vertices = mesh.VertexCount, Faces = mesh.FaceCount };
	}

	/// <summary>Sculpt level for the next stroke (-1 = top level), resolved the same way the script API does.</summary>
	public static int ResolveSculpt( MultiresSculpt sculpt, int level ) => SculptTarget.ResolveLevel( sculpt, level );
}

/// <summary>
/// The one-key voxel remesh from the brush UI (ticket (4)). Routes to
/// <see cref="RemeshFeature.VoxelBackend"/> — installed by RetopoService when a
/// voxel engine is available (the OpenVDB task's half, in-progress at the time of
/// writing). With no backend installed this refuses honestly instead of silently
/// doing nothing or pretending the built-in SurfaceNets ran.
/// </summary>
public sealed class DynTopoRemeshResult
{
	public bool Success { get; set; }
	public PolyMesh Mesh { get; set; }
	public string Reason { get; set; } = "";
	public float VoxelSize { get; set; }
	public TimeSpan Elapsed { get; set; }
}

public static class DynTopoRemeshKey
{
	public static DynTopoRemeshResult Invoke( IReadOnlyList<PolyMesh> parts, DynTopoBrushSettings settings )
	{
		if ( parts is null ) throw new ArgumentNullException( nameof( parts ) );
		if ( settings is null ) throw new ArgumentNullException( nameof( settings ) );
		settings.Validate();

		float voxel = settings.VoxelSize > 0f ? settings.VoxelSize : settings.ResolveDetail( 1f ) * 2f;
		var backend = RemeshFeature.VoxelBackend;
		if ( backend is null )
			return new DynTopoRemeshResult
			{
				Success = false,
				Reason = "No voxel backend installed — the OpenVDB voxel engine is not in this build yet.",
				VoxelSize = voxel,
			};

		var sw = Stopwatch.StartNew();
		var mesh = backend( parts, voxel, settings.VoxelSmoothPasses );
		sw.Stop();
		if ( mesh is null )
			return new DynTopoRemeshResult { Success = false, Reason = "Voxel backend returned no mesh.", VoxelSize = voxel, Elapsed = sw.Elapsed };
		return new DynTopoRemeshResult { Success = true, Mesh = mesh, VoxelSize = voxel, Elapsed = sw.Elapsed };
	}
}

/// <summary>
/// BVH rebuild after a stroke changed topology (ticket (5)). Same faces: a cheap
/// refit (Refits++). Changed faces: a fresh build. The host calls this from the
/// onTopologyChanged callback with the BVH it already holds; the returned tree is
/// the one to keep. Never returns null for a non-null mesh.
/// </summary>
public static class DynTopoBvh
{
	public static MeshBVH Rebuild( MeshBVH previous, PolyMesh mesh ) => MeshBVH.BuildOrRefit( previous, mesh );
}
