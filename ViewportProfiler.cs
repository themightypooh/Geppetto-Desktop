// Viewport profiler: names the slow object and the slow shader (godlike Platform P1).
//
// Cinema 4D's Object Profiling Manager names the slow object/shader; Geppetto
// had only per-pass timings (RenderStats: CPU ms per pass last frame, GPU ms
// via timestamp queries read 4 frames later so nothing stalls). This file is
// the profiler on top of that public data — it reads NO private renderer
// state and edits NO renderer file:
//
//   - per-pass timings come from SceneRenderer.Stats (public);
//   - per-object costs come from the mesh itself (batch triangles, texture
//     maps, skinned or not) through a cost MODEL, labelled as such — the
//     renderer draws all batches in one DrawBatched call, so there is no
//     per-batch GPU timer to read. The model ranks objects; the measured
//     per-pass numbers rank passes/shaders. The report says which is which.
//
// The profiler is a pure function over (pass timings, object costs), so it is
// testable on the CPU with no GL: Profile() takes the data, ProfileRenderer()
// is the convenience that reads it from a live renderer + mesh.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Marionette.Desktop;

/// <summary>Measured timings for one render pass: CPU ms (last frame) and GPU
/// ms (from timestamp queries, 0 until the first frames have aged through
/// RenderStats' 4-frame latency).</summary>
public sealed record RenderPassTimings( string Pass, double CpuMs, double GpuMs, long AllocatedBytes );

/// <summary>One object's modelled draw cost. Triangles dominate; a texture
/// map costs <see cref="ViewportProfiler.TextureCostTriangles"/> triangle-
/// equivalents and skinning costs 1.5x — the model, not a measurement (see
/// the file header). Share is the fraction of the scene's total cost.</summary>
public sealed record ObjectCost( string Name, int Triangles, int Textures, bool Skinned, double CostUnits, double Share );

/// <summary>The profiler's report: the slowest object and shader, the full
/// per-pass table, the per-object ranking, and what to do about it.</summary>
public sealed class ViewportProfileReport
{
	/// <summary>Name of the slowest object (highest modelled cost), or null when the scene is empty.</summary>
	public string? SlowestObject;
	/// <summary>Its modelled cost units.</summary>
	public double SlowestObjectCost;
	/// <summary>Its share of the scene's total modelled cost, 0..1.</summary>
	public double SlowestObjectShare;
	/// <summary>Slowest pass by GPU ms (CPU ms when no GPU timing is available yet).</summary>
	public string? SlowestPass;
	/// <summary>The program that pass runs, by name — this is the "slow shader".</summary>
	public string? SlowestShader;
	/// <summary>Measured GPU ms of the slowest pass (0 when GPU timing is not yet available).</summary>
	public double SlowestPassGpuMs;
	/// <summary>True when the slowest-pass ranking used GPU timings; false when it fell back to CPU ms.</summary>
	public bool GpuTimingAvailable;
	/// <summary>Per-pass measured timings, slowest first.</summary>
	public IReadOnlyList<RenderPassTimings> Passes = Array.Empty<RenderPassTimings>();
	/// <summary>Per-object modelled costs, slowest first.</summary>
	public IReadOnlyList<ObjectCost> Objects = Array.Empty<ObjectCost>();
	/// <summary>What to do, in priority order. Plain words, no jargon.</summary>
	public IReadOnlyList<string> Recommendations = Array.Empty<string>();
	/// <summary>One-line verdict.</summary>
	public string Summary = "";
}

/// <summary>Viewport profiler: rank passes by measured time, rank objects by
/// modelled cost, name the slowest of each, and recommend the fix.</summary>
public static class ViewportProfiler
{
	/// <summary>Modelled cost of one texture map, in triangle-equivalents. A
	/// texture bind + sample costs real GPU time but far less than its texel
	/// count suggests; 4096 is the model's constant, not a measurement.</summary>
	public const double TextureCostTriangles = 4096;
	/// <summary>Modelled cost multiplier for a skinned batch (bone palette +
	/// vertex shader work).</summary>
	public const double SkinnedCostMultiplier = 1.5;

	/// <summary>Program name for a pass — the shader the pass runs.</summary>
	public static string ShaderForPass( string pass ) => pass switch
	{
		"Shadow" => "shadow-depth (KeyShadowMap)",
		"DepthPrePass" => "lit depth pre-pass (RendererShaders)",
		"Lit" => "lit (RendererShaders)",
		"PostChain" => "post (TAA/bloom/motion/volume)",
		_ => pass,
	};

	/// <summary>Profile from data: measured per-pass timings + per-object
	/// (name, triangles, texture maps, skinned) costs. Pure function, no GL.</summary>
	public static ViewportProfileReport Profile( IReadOnlyList<RenderPassTimings> passes,
		IReadOnlyList<(string Name, int Triangles, int Textures, bool Skinned)> objects )
	{
		ArgumentNullException.ThrowIfNull( passes );
		ArgumentNullException.ThrowIfNull( objects );

		var report = new ViewportProfileReport();

		// Passes: GPU ms when available (any pass with a GPU time), else CPU ms.
		var gpuAvailable = passes.Any( p => p.GpuMs > 0 );
		var rankedPasses = passes
			.OrderByDescending( p => gpuAvailable ? p.GpuMs : p.CpuMs )
			.ToList();
		report.Passes = rankedPasses;
		report.GpuTimingAvailable = gpuAvailable;
		if ( rankedPasses.Count > 0 )
		{
			var slowest = rankedPasses[0];
			report.SlowestPass = slowest.Pass;
			report.SlowestShader = ShaderForPass( slowest.Pass );
			report.SlowestPassGpuMs = slowest.GpuMs;
		}

		// Objects: modelled cost, slowest first.
		var costs = new List<ObjectCost>();
		var total = 0.0;
		foreach ( var (name, triangles, textures, skinned) in objects )
		{
			if ( triangles < 0 || textures < 0 ) throw new ArgumentOutOfRangeException( nameof( objects ), "Triangle and texture counts cannot be negative." );
			var units = triangles * (skinned ? SkinnedCostMultiplier : 1.0) + textures * TextureCostTriangles;
			costs.Add( new ObjectCost( string.IsNullOrWhiteSpace( name ) ? "(unnamed)" : name, triangles, textures, skinned, units, 0 ) );
			total += units;
		}
		costs.Sort( ( a, b ) => b.CostUnits.CompareTo( a.CostUnits ) );
		var ranked = costs.Select( c => c with { Share = total > 0 ? c.CostUnits / total : 0 } ).ToList();
		report.Objects = ranked;
		if ( ranked.Count > 0 )
		{
			report.SlowestObject = ranked[0].Name;
			report.SlowestObjectCost = ranked[0].CostUnits;
			report.SlowestObjectShare = ranked[0].Share;
		}

		var recommendations = new List<string>();
		if ( ranked.Count > 0 && ranked[0].Share > 0.5 )
			recommendations.Add( $"Drop '{ranked[0].Name}' to a coarser proxy level: it is {ranked[0].Share:P0} of the scene's modelled draw cost ({ranked[0].Triangles:N0} triangles{(ranked[0].Skinned ? ", skinned" : "")})." );
		if ( rankedPasses.Count > 0 && rankedPasses[0].Pass == "Lit" )
			recommendations.Add( "The lit pass dominates the frame; the biggest textured/skinned batches are the first place to look." );
		if ( ranked.Count > 0 && ranked.Any( c => c.Skinned && c.Share > 0.25 ) )
			recommendations.Add( "A skinned object is a large share of the cost; pose on the proxy and transfer to full-res (the Rig P0 pattern) instead of posing full-res." );
		var totalTriangles = ranked.Sum( c => (long)c.Triangles );
		if ( totalTriangles > 5_000_000 )
			recommendations.Add( $"The scene is {totalTriangles:N0} triangles, over the 5M interactive target — proxies should be stepping down (see ViewportProxyLod)." );
		if ( recommendations.Count == 0 )
			recommendations.Add( "No single object or pass dominates; the frame is balanced." );
		report.Recommendations = recommendations;

		report.Summary = ranked.Count == 0
			? "Empty scene: nothing to profile."
			: $"Slowest object: '{report.SlowestObject}' ({report.SlowestObjectShare:P0} of modelled cost). " +
				$"Slowest pass: {report.SlowestPass ?? "(none)"} ({(gpuAvailable ? $"{report.SlowestPassGpuMs:F2} ms GPU" : "GPU timing not yet available; ranked by CPU ms")}).";
		return report;
	}

	/// <summary>Profile a live renderer + mesh: pass timings from
	/// <see cref="SceneRenderer.Stats"/> (public), object costs from the
	/// mesh's batches (one object per batch: material name, triangles,
	/// texture maps present, skinned). Call after at least one Draw — GPU
	/// timings need 4 frames to age through RenderStats.</summary>
	public static ViewportProfileReport ProfileRenderer( SceneRenderer renderer, ViewportMesh mesh )
	{
		ArgumentNullException.ThrowIfNull( renderer );
		ArgumentNullException.ThrowIfNull( mesh );
		var stats = renderer.Stats;
		var passes = new List<RenderPassTimings>();
		foreach ( var kind in Enum.GetValues<RenderPassKind>() )
		{
			var k = (int)kind;
			passes.Add( new RenderPassTimings( kind.ToString(), stats.CpuMs[k], stats.GpuMs[k], stats.AllocatedBytes[k] ) );
		}
		var objects = new List<(string, int, int, bool)>();
		var batches = mesh.Batches;
		if ( batches is not null )
		{
			for ( var i = 0; i < batches.Length; i++ )
			{
				var b = batches[i];
				var textures = 0;
				if ( b.AlbedoImage is not null ) textures++;
				if ( b.NormalImage is not null ) textures++;
				if ( b.RoughnessImage is not null ) textures++;
				if ( b.MetallicImage is not null ) textures++;
				if ( b.EmissionImage is not null ) textures++;
				if ( b.OcclusionImage is not null ) textures++;
				objects.Add( (string.IsNullOrWhiteSpace( b.MaterialName ) ? $"batch {i}" : b.MaterialName, Math.Max( 0, b.IndexCount / 3 ), textures, mesh.HasSkin) );
			}
		}
		return Profile( passes, objects );
	}
}
