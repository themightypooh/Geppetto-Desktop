#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Effigy;

namespace Marionette.Desktop;

/// <summary>Knobs for <see cref="AutoWeightService"/>. Defaults are an engine-ready bind.</summary>
public sealed class AutoWeightSettings
{
	/// <summary>Influences kept per vertex after refinement (renormalised). Four is what s&amp;box/GPU skinning index.</summary>
	public int MaxInfluences = 4;

	/// <summary>Influences below this (after normalising) are dropped, except a vertex's strongest one.</summary>
	public float MinWeight = 0.01f;

	/// <summary>Neighbour-averaging passes over the surface after the heat solve. 0 disables.</summary>
	public int SmoothPasses = 1;

	/// <summary>How far each smoothing pass moves toward the neighbour average (0..1).</summary>
	public float SmoothStrength = 0.5f;

	/// <summary>Also weight twist/helper/IK/face bones (kernel <see cref="Skeleton.IsAuxiliaryBone"/>).</summary>
	public bool IncludeHelperBones;

	/// <summary>Resolution of the kernel's geodesic-voxel fallback.</summary>
	public int VoxelResolution = 64;

	internal void Validate()
	{
		if ( MaxInfluences < 1 ) throw new ArgumentOutOfRangeException( nameof( MaxInfluences ), MaxInfluences, "MaxInfluences must be at least 1." );
		if ( !float.IsFinite( MinWeight ) || MinWeight < 0f || MinWeight >= 1f ) throw new ArgumentOutOfRangeException( nameof( MinWeight ), MinWeight, "MinWeight must be in [0, 1)." );
		if ( SmoothPasses < 0 ) throw new ArgumentOutOfRangeException( nameof( SmoothPasses ), SmoothPasses, "SmoothPasses cannot be negative." );
		if ( !float.IsFinite( SmoothStrength ) || SmoothStrength < 0f || SmoothStrength > 1f ) throw new ArgumentOutOfRangeException( nameof( SmoothStrength ), SmoothStrength, "SmoothStrength must be in [0, 1]." );
		if ( VoxelResolution < 8 || VoxelResolution > 256 ) throw new ArgumentOutOfRangeException( nameof( VoxelResolution ), VoxelResolution, "VoxelResolution must be in [8, 256]." );
	}
}

/// <summary>What an automatic bind produced and how. Every fallback is named.</summary>
public sealed class AutoWeightResult
{
	public required SkinWeights Weights { get; init; }

	/// <summary>The kernel's own report (requested/used method, per-method vertex counts, fallback reason).</summary>
	public required BindReport KernelReport { get; init; }

	/// <summary>Vertices weighted by the bone-heat solve.</summary>
	public int HeatVertices => KernelReport.HeatVertices;

	/// <summary>Vertices weighted by the kernel's geodesic-voxel / nearest-bone fallback instead of heat.</summary>
	public int FallbackVertices => KernelReport.VoxelVertices + KernelReport.OtherVertices;

	/// <summary>True when the whole mesh was weighted by heat.</summary>
	public bool FullyHeat => KernelReport.Used == BindMethod.Heat && FallbackVertices == 0;

	/// <summary>Largest influence count of any vertex in <see cref="Weights"/> (≤ MaxInfluences).</summary>
	public int MaxInfluencesUsed { get; init; }

	/// <summary>Validation of the final weights against mesh/skeleton; empty means valid.</summary>
	public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();

	public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

	/// <summary>Wall time of the solve + refinement on the calling thread (not GPU time).</summary>
	public double Milliseconds { get; init; }

	public override string ToString() => $"{KernelReport}; max {MaxInfluencesUsed} influences; {Milliseconds:0.#} ms";
}

/// <summary>
/// Automatic skin weights (bone heat) for the desktop: a wrapper over the kernel's real solver plus
/// the refinement the kernel leaves to its callers.
///
/// THE SOLVE IS THE KERNEL'S. <see cref="AutoWeights.Heat"/> already implements Baran &amp; Popović
/// bone heat — cotangent Laplacian with vertex areas (<see cref="MeshLaplacian.Cotangent"/>),
/// H = 1/d² to the nearest bone segment each vertex can SEE (BVH ray to the bone), one sparse
/// LDLᵀ factorisation (<see cref="DirichletSolver"/>) reused for every bone — and falls back to
/// geodesic-voxel binding for non-manifold meshes and for pieces with no visible bone (closed-in or
/// floating islands), reporting which. A second CG solver here would be a weaker duplicate of that,
/// so this service does not write one.
///
/// WHAT IS ADDED HERE: input validation, a surface smoothing pass, max-influence pruning with
/// renormalisation done ONCE at the end (the kernel prunes before smoothing, which smoothing then
/// undoes), a final validation, and an undoable apply through <see cref="EditHistory"/>.
/// </summary>
public static class AutoWeightService
{
	/// <summary>Compute heat weights for <paramref name="mesh"/>. Does not modify the mesh.
	/// Pure CPU; safe on a worker thread as long as nobody mutates the inputs meanwhile.</summary>
	public static AutoWeightResult Compute( PolyMesh mesh, Skeleton skeleton, AutoWeightSettings? settings = null )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		ArgumentNullException.ThrowIfNull( skeleton );
		settings ??= new AutoWeightSettings();
		settings.Validate();

		if ( skeleton.Count == 0 )
			throw new InvalidOperationException( "Cannot auto-weight to a skeleton with no bones." );
		if ( mesh.VertexCount == 0 )
			throw new ArgumentException( "Cannot auto-weight a mesh with no vertices.", nameof( mesh ) );
		if ( mesh.Faces.Count == 0 )
			throw new ArgumentException( "Cannot auto-weight a mesh with no faces: heat diffuses over the surface.", nameof( mesh ) );
		for ( int v = 0; v < mesh.VertexCount; v++ )
		{
			var p = mesh.Positions[v];
			if ( !float.IsFinite( p.x ) || !float.IsFinite( p.y ) || !float.IsFinite( p.z ) )
				throw new ArgumentException( $"Vertex {v} has a non-finite position.", nameof( mesh ) );
		}
		for ( int b = 0; b < skeleton.Count; b++ )
		{
			Vec3 h = skeleton.HeadWorld( b ), t = skeleton.TailWorld( b );
			if ( !float.IsFinite( h.x + h.y + h.z + t.x + t.y + t.z ) )
				throw new ArgumentException( $"Bone {b} ('{skeleton.Bones[b].Name}') has a non-finite bind transform.", nameof( skeleton ) );
		}

		var clock = Stopwatch.StartNew();
		// Keep every influence through the solve; prune once, after smoothing.
		var options = new AutoWeightOptions
		{
			MaxInfluences = int.MaxValue,
			MinWeight = 0f,
			VoxelResolution = settings.VoxelResolution,
			IncludeHelperBones = settings.IncludeHelperBones,
		};
		var raw = AutoWeights.Heat( mesh, skeleton, out var report, options );
		var refined = Refine( mesh, raw, settings.SmoothPasses, settings.SmoothStrength, settings.MaxInfluences, settings.MinWeight );
		clock.Stop();

		var notes = new List<string>();
		if ( report.Fallback is not null ) notes.Add( "fallback: " + report.Fallback );
		notes.AddRange( report.Notes );
		if ( settings.SmoothPasses > 0 ) notes.Add( $"smoothed {settings.SmoothPasses} pass(es) at {settings.SmoothStrength:0.##}" );
		notes.Add( $"pruned to {settings.MaxInfluences} influences, min weight {settings.MinWeight:0.###}, renormalised" );

		return new AutoWeightResult
		{
			Weights = refined,
			KernelReport = report,
			MaxInfluencesUsed = refined.Vertices.Count == 0 ? 0 : refined.Vertices.Max( w => w.Length ),
			Problems = refined.Validate( mesh.VertexCount, skeleton.Count ),
			Notes = notes,
			Milliseconds = clock.Elapsed.TotalMilliseconds,
		};
	}

	/// <summary>
	/// Smooth over the surface, then drop influences under <paramref name="minWeight"/> (never a
	/// vertex's strongest), cap at <paramref name="maxInfluences"/> and renormalise. Returns a new set.
	/// </summary>
	public static SkinWeights Refine( PolyMesh mesh, SkinWeights weights, int smoothPasses, float smoothStrength, int maxInfluences, float minWeight )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		ArgumentNullException.ThrowIfNull( weights );
		if ( weights.Count != mesh.VertexCount ) throw new ArgumentException( $"weights ({weights.Count}) and mesh ({mesh.VertexCount}) disagree" );
		if ( maxInfluences < 1 ) throw new ArgumentOutOfRangeException( nameof( maxInfluences ) );
		if ( smoothPasses < 0 ) throw new ArgumentOutOfRangeException( nameof( smoothPasses ) );
		if ( !float.IsFinite( minWeight ) || minWeight < 0f || minWeight >= 1f ) throw new ArgumentOutOfRangeException( nameof( minWeight ) );

		var smoothed = smoothPasses > 0 ? SkinBinder.SmoothWeights( mesh, weights, smoothPasses, smoothStrength ) : weights;
		var result = new SkinWeights( mesh.VertexCount );
		for ( int v = 0; v < mesh.VertexCount; v++ )
			result[v] = PruneOne( smoothed[v], maxInfluences, minWeight );
		return result;
	}

	/// <summary>Strongest-first, at most <paramref name="max"/>, each ≥ <paramref name="minWeight"/>
	/// (except the first), summing to 1. Empty in → empty out.</summary>
	public static BoneWeight[] PruneOne( BoneWeight[]? weights, int max, float minWeight )
	{
		if ( max < 1 ) throw new ArgumentOutOfRangeException( nameof( max ) );
		if ( weights is null || weights.Length == 0 ) return Array.Empty<BoneWeight>();

		var merged = new Dictionary<int, float>();
		foreach ( var w in weights )
			if ( w.Weight > 0f && float.IsFinite( w.Weight ) )
				merged[w.Bone] = merged.GetValueOrDefault( w.Bone ) + w.Weight;
		float total = merged.Values.Sum();
		if ( total <= 1e-12f ) return Array.Empty<BoneWeight>();

		var sorted = merged.Select( kv => new BoneWeight( kv.Key, kv.Value / total ) )
			.OrderByDescending( w => w.Weight ).ThenBy( w => w.Bone ).ToList();
		var kept = new List<BoneWeight>( Math.Min( max, sorted.Count ) );
		for ( int i = 0; i < sorted.Count && kept.Count < max; i++ )
		{
			if ( i > 0 && sorted[i].Weight < minWeight ) break;
			kept.Add( sorted[i] );
		}
		float keptTotal = kept.Sum( w => w.Weight );
		var result = new BoneWeight[kept.Count];
		for ( int i = 0; i < kept.Count; i++ ) result[i] = new BoneWeight( kept[i].Bone, kept[i].Weight / keptTotal );
		return result;
	}

	/// <summary>
	/// Compute and write the result into <c>mesh.Skin</c> as ONE undoable command on
	/// <paramref name="history"/> (or directly when history is null). Undo restores the exact prior
	/// skin, including "no skin". The existing <see cref="SkinWeights"/> object is updated in place
	/// when present so holders of the reference (a paint session) stay valid.
	/// </summary>
	public static AutoWeightResult Apply( PolyMesh mesh, Skeleton skeleton, EditHistory? history, AutoWeightSettings? settings = null )
	{
		var result = Compute( mesh, skeleton, settings );
		if ( result.Problems.Count > 0 )
			throw new InvalidOperationException( "Auto-weight produced invalid weights; not applied: " + string.Join( "; ", result.Problems.Take( 5 ) ) );

		var before = mesh.Skin?.Clone();
		var after = result.Weights.Clone();
		var command = new DelegateEditCommand( "Auto weights (bone heat)",
			() => WriteSkin( mesh, after ),
			() => WriteSkin( mesh, before ) );
		if ( history is null ) command.Execute();
		else history.Execute( command );
		return result;
	}

	static void WriteSkin( PolyMesh mesh, SkinWeights? source )
	{
		if ( source is null ) { mesh.Skin = null!; return; }
		mesh.Skin ??= new SkinWeights();
		var target = mesh.Skin;
		target.Vertices.Clear();
		foreach ( var w in source.Vertices ) target.Vertices.Add( (BoneWeight[])w.Clone() );
	}
}
