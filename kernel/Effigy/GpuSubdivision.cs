// GPU subdivision: Catmull-Clark positions evaluated on the GPU (godlike Platform P1).
//
// The topology half of subdivision is expensive and stays on the CPU:
// CatmullClark.SubdivideOnce builds the edge/face tables and the new faces.
// The POSITION half is a pure stencil — every output vertex is a weighted
// average of source vertices — and that is what runs on the GPU.
//
// This file is the kernel side:
//   - GpuSubdivisionTable: the flattened stencil (one weighted sum per output
//     vertex, source vertices only), built by BuildTable from the mesh. The
//     weights reproduce CatmullClark.SubdivideOnce EXACTLY — same edge order
//     (sorted by (A, B)), same boundary/crease rules, same face/edge/vertex
//     formulas — so a CPU evaluation of the table equals Subdivide's positions
//     to float precision (the test asserts exactly that).
//   - EvaluateTable: the CPU evaluation, used by the cross-check and by hosts
//     without a GPU backend.
//   - GpuSubdivision.Subdivide: the entry point. One level per provider call;
//     when no backend is installed, or the backend declines (above its
//     ceilings, no GL context, any GL failure), the CPU reference runs
//     instead. A declined GPU path costs a CPU subdivision, never a crash or
//     a wrong mesh.
//
// HOST WIRING (final integrator, shared entry point — not this file): call
// GpuSubdivisionComputeBackend.Install() once at Desktop startup, next to
// ManifoldBoolean.Install(). Until then the CPU subdivision runs and nothing
// changes. The backend caches the table per topology (TopologyKey), so a
// sculpted cage re-evaluates positions on the GPU without rebuilding the
// topology — that is where the win comes from.
#nullable enable
using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>One Catmull-Clark level as a flat stencil: output vertex i is
/// sum(Weights[k] * Positions[Indices[k]]) for k in
/// [Offsets[i], Offsets[i+1]). Every weight set sums to 1 (each rule is an
/// affine combination), so the table also works for skin weights and colours
/// with no renormalisation — same property SubdivideOnce relies on.</summary>
public sealed class GpuSubdivisionTable
{
	/// <summary>Source (cage) vertex count.</summary>
	public int SourceVertexCount;
	/// <summary>Output vertex count: V + E + F of the cage.</summary>
	public int OutputVertexCount;
	/// <summary>Cage edge count (output vertices [V, V+E) are the edge points).</summary>
	public int EdgeCount;
	/// <summary>Cage face count (output vertices [V+E, V+E+F) are the face points).</summary>
	public int FaceCount;
	/// <summary>Per-output-vertex start into <see cref="Indices"/> /
	/// <see cref="Weights"/>; length OutputVertexCount + 1.</summary>
	public int[] Offsets = Array.Empty<int>();
	/// <summary>Flattened source-vertex indices.</summary>
	public int[] Indices = Array.Empty<int>();
	/// <summary>Flattened weights, same length as <see cref="Indices"/>.</summary>
	public float[] Weights = Array.Empty<float>();
	/// <summary>FNV-1a hash of the topology (faces, edges, creases) the table
	/// was built from. The host backend caches tables by this key.</summary>
	public long TopologyKey;
}

/// <summary>GPU subdivision backend contract: one Catmull-Clark level. A
/// backend declines (returns false with a reason) above its ceilings, without
/// a GL context, or on any GL failure — the caller then runs the CPU
/// reference. It never throws for a declined job.</summary>
public interface IGpuSubdivisionBackend
{
	bool TrySubdivide( PolyMesh mesh, out PolyMesh? subdivided, out string? why );
}

/// <summary>GPU subdivision entry point + table builder. Kernel-side, no GL.</summary>
public static class GpuSubdivision
{
	/// <summary>Installed GPU backend, or null for CPU-only. Set by the host
	/// backend's Install() (final integrator wires it at startup).</summary>
	public static IGpuSubdivisionBackend? Provider;

	/// <summary>Ceiling the host backend enforces on the cage: above this many
	/// cage vertices it declines and the CPU reference runs.</summary>
	public const int MaxCageVertices = 65536;
	/// <summary>Ceiling the host backend enforces on the subdivided mesh.</summary>
	public const int MaxOutputVertices = 1024 * 1024;
	/// <summary>Ceiling on one output vertex's stencil. An extraordinary vertex
	/// past this declines; the CPU reference has no such ceiling.</summary>
	public const int MaxStencilEntries = 64;

	/// <summary>Subdivide <paramref name="levels"/> times. Each level tries the
	/// installed GPU backend first and falls back to the CPU reference
	/// (CatmullClark.Subdivide) when there is no backend or it declines.
	/// Level 0 returns a clone, like the CPU reference.</summary>
	public static PolyMesh Subdivide( PolyMesh mesh, int levels = 1 )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		if ( levels < 0 ) throw new ArgumentOutOfRangeException( nameof( levels ), "Subdivision levels cannot be negative." );
		var current = mesh;
		for ( var i = 0; i < levels; i++ )
		{
			var provider = Provider;
			if ( provider is not null && provider.TrySubdivide( current, out var gpu, out _ ) && gpu is not null )
			{
				current = gpu;
				continue;
			}
			current = CatmullClark.Subdivide( current, 1 );
		}
		return current;
	}

	/// <summary>Cross-check: true when <paramref name="gpu"/> has the same
	/// topology as the CPU reference AND every position matches within
	/// <paramref name="tolerance"/>. The host backend runs this on its first
	/// use of a new table and declines (falls back to CPU) when it fails.</summary>
	public static bool MatchesCpu( PolyMesh mesh, PolyMesh gpu, float tolerance = 1e-4f )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		ArgumentNullException.ThrowIfNull( gpu );
		var cpu = CatmullClark.Subdivide( mesh, 1 );
		if ( cpu.VertexCount != gpu.VertexCount || cpu.FaceCount != gpu.FaceCount ) return false;
		for ( var i = 0; i < cpu.VertexCount; i++ )
			if ( (cpu.Positions[i] - gpu.Positions[i]).Length > tolerance ) return false;
		return true;
	}

	/// <summary>Build the flattened one-level stencil for <paramref name="mesh"/>,
	/// reproducing CatmullClark.SubdivideOnce's position rules exactly (same
	/// edge order, same boundary/crease handling). <paramref name="creases"/>
	/// maps an edge to its sharpness 0..1 (null = all smooth). The table is
	/// topology-only: it does not depend on the cage positions, so the host
	/// backend caches it per <see cref="GpuSubdivisionTable.TopologyKey"/> and
	/// re-evaluates it for every cage edit.</summary>
	public static GpuSubdivisionTable BuildTable( PolyMesh mesh, IReadOnlyDictionary<EdgeKey, float>? creases = null )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		var edgeFaces = mesh.BuildEdgeFaces();
		var vertexFaces = mesh.BuildVertexFaces();
		var vertexEdges = mesh.BuildVertexEdges();

		var edgeList = new List<EdgeKey>( edgeFaces.Keys );
		edgeList.Sort( ( a, b ) =>
		{
			var cmp = a.A.CompareTo( b.A );
			return cmp != 0 ? cmp : a.B.CompareTo( b.B );
		} );

		var vertCount = mesh.VertexCount;
		var edgeCount = edgeList.Count;
		var faceCount = mesh.FaceCount;
		var outputCount = vertCount + edgeCount + faceCount;

		float Sharp( EdgeKey key ) => creases is not null && creases.TryGetValue( key, out var s ) ? Math.Clamp( s, 0f, 1f ) : 0f;

		var offsets = new int[outputCount + 1];
		var indices = new List<int>();
		var weights = new List<float>();

		// One accumulator per output vertex: duplicate source indices merge by
		// summing weights (a face point's corners can repeat through several
		// adjacent faces; a vertex point's edge endpoints repeat).
		void Emit( Dictionary<int, float> acc )
		{
			var keys = new List<int>( acc.Keys );
			keys.Sort();
			foreach ( var k in keys )
			{
				if ( Math.Abs( acc[k] ) < 1e-9f ) continue;
				indices.Add( k );
				weights.Add( acc[k] );
			}
		}
		void Add( Dictionary<int, float> acc, int index, float weight )
		{
			acc.TryGetValue( index, out var old );
			acc[index] = old + weight;
		}
		// A face point's stencil, expanded to source vertices.
		void AddFacePoint( Dictionary<int, float> acc, int faceIndex, float weight )
		{
			var face = mesh.Faces[faceIndex];
			var share = weight / face.Count;
			foreach ( var ci in face.Indices ) Add( acc, ci, share );
		}

		for ( var vi = 0; vi < vertCount; vi++ )
		{
			var acc = new Dictionary<int, float>();
			var edges = vertexEdges[vi];

			var boundaryNeighbours = new List<int>( 2 );
			foreach ( var key in edges )
				if ( edgeFaces[key].Count == 1 )
					boundaryNeighbours.Add( key.A == vi ? key.B : key.A );

			if ( boundaryNeighbours.Count == 2 )
			{
				// Cubic B-spline: (p_prev + 6v + p_next) / 8.
				Add( acc, boundaryNeighbours[0], 0.125f );
				Add( acc, vi, 0.75f );
				Add( acc, boundaryNeighbours[1], 0.125f );
			}
			else if ( boundaryNeighbours.Count > 2 )
			{
				// Corner where several borders meet: pinned.
				Add( acc, vi, 1f );
			}
			else
			{
				var faces = vertexFaces[vi];
				var n = faces.Count;
				if ( n < 3 )
				{
					// Degenerate valence: pinned.
					Add( acc, vi, 1f );
				}
				else
				{
					// Creases meeting here.
					var creaseNeighbours = new List<int>( 2 );
					var creaseSharp = 0f;
					if ( creases is not null )
					{
						foreach ( var key in edges )
						{
							var sh = Sharp( key );
							if ( sh <= 0f ) continue;
							creaseNeighbours.Add( key.A == vi ? key.B : key.A );
							creaseSharp = creaseNeighbours.Count == 1 ? sh : MathF.Min( creaseSharp, sh );
						}
					}

					// Smooth vertex: (F + 2R + (n-3)P) / n, with F the average of
					// adjacent face points and R the average of edge midpoints.
					var smooth = new Dictionary<int, float>();
					foreach ( var fi in faces ) AddFacePoint( smooth, fi, 1f / (n * n) );
					var endpointShare = 1f / (n * edges.Count);
					foreach ( var key in edges )
					{
						Add( smooth, key.A, endpointShare );
						Add( smooth, key.B, endpointShare );
					}
					Add( smooth, vi, (n - 3f) / n );

					var creaseMix = creaseNeighbours.Count >= 2 ? creaseSharp : 0f;
					if ( creaseMix <= 0f )
					{
						foreach ( var kv in smooth ) Add( acc, kv.Key, kv.Value );
					}
					else
					{
						foreach ( var kv in smooth ) Add( acc, kv.Key, kv.Value * (1f - creaseMix) );
						if ( creaseNeighbours.Count == 2 )
						{
							Add( acc, creaseNeighbours[0], 0.125f * creaseMix );
							Add( acc, vi, 0.75f * creaseMix );
							Add( acc, creaseNeighbours[1], 0.125f * creaseMix );
						}
						else
						{
							Add( acc, vi, creaseMix );
						}
					}
				}
			}
			Emit( acc );
			offsets[vi + 1] = indices.Count;
		}

		for ( var ei = 0; ei < edgeCount; ei++ )
		{
			var key = edgeList[ei];
			var acc = new Dictionary<int, float>();
			var faces = edgeFaces[key];
			if ( faces.Count == 1 )
			{
				// Boundary edge: plain midpoint.
				Add( acc, key.A, 0.5f );
				Add( acc, key.B, 0.5f );
			}
			else
			{
				// Smooth: (a + b + avg(face points) * 2) * 0.25, lerped toward the
				// midpoint by the crease sharpness.
				var sharp = Sharp( key );
				Add( acc, key.A, 0.25f * (1f - sharp) + 0.5f * sharp );
				Add( acc, key.B, 0.25f * (1f - sharp) + 0.5f * sharp );
				var facePointShare = 0.5f / faces.Count * (1f - sharp);
				foreach ( var fi in faces ) AddFacePoint( acc, fi, facePointShare );
			}
			var oi = vertCount + ei;
			Emit( acc );
			offsets[oi + 1] = indices.Count;
		}

		for ( var fi = 0; fi < faceCount; fi++ )
		{
			var acc = new Dictionary<int, float>();
			AddFacePoint( acc, fi, 1f );
			var oi = vertCount + edgeCount + fi;
			Emit( acc );
			offsets[oi + 1] = indices.Count;
		}

		return new GpuSubdivisionTable
		{
			SourceVertexCount = vertCount,
			OutputVertexCount = outputCount,
			EdgeCount = edgeCount,
			FaceCount = faceCount,
			Offsets = offsets,
			Indices = indices.ToArray(),
			Weights = weights.ToArray(),
			TopologyKey = TopologyKey( mesh, creases ),
		};
	}

	/// <summary>Evaluate a table on the CPU: output[i] is the weighted sum of
	/// the source positions. Used by the cross-check (and by hosts without a
	/// GPU backend, where it is exactly as fast as the CPU reference — the
	/// table exists so the GPU can do this sum).</summary>
	public static Vec3[] EvaluateTable( GpuSubdivisionTable table, IReadOnlyList<Vec3> positions )
	{
		ArgumentNullException.ThrowIfNull( table );
		ArgumentNullException.ThrowIfNull( positions );
		if ( positions.Count < table.SourceVertexCount ) throw new ArgumentException( "Not enough source positions for this table.", nameof( positions ) );
		var output = new Vec3[table.OutputVertexCount];
		for ( var i = 0; i < table.OutputVertexCount; i++ )
		{
			var sum = Vec3.Zero;
			for ( var k = table.Offsets[i]; k < table.Offsets[i + 1]; k++ )
				sum += positions[table.Indices[k]] * table.Weights[k];
			output[i] = sum;
		}
		return output;
	}

	/// <summary>FNV-1a hash of the topology a table was built from: vertex and
	/// face counts, every face's indices, every edge (sorted), and the crease
	/// values. Two meshes with the same topology hash share a table.</summary>
	public static long TopologyKey( PolyMesh mesh, IReadOnlyDictionary<EdgeKey, float>? creases = null )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		unchecked
		{
			long hash = 1469598103934665603L;
			void Mix( long v ) { hash ^= v; hash *= 1099511628211L; }
			Mix( mesh.VertexCount );
			Mix( mesh.FaceCount );
			foreach ( var face in mesh.Faces )
				foreach ( var vi in face.Indices ) Mix( vi );
			var edges = new List<EdgeKey>( mesh.BuildEdgeFaces().Keys );
			edges.Sort( ( a, b ) => a.A != b.A ? a.A.CompareTo( b.A ) : a.B.CompareTo( b.B ) );
			foreach ( var e in edges ) { Mix( e.A ); Mix( e.B ); }
			if ( creases is not null )
				foreach ( var kv in creases ) { Mix( kv.Key.A ); Mix( kv.Key.B ); Mix( BitConverter.SingleToInt32Bits( kv.Value ) ); }
			return hash;
		}
	}
}
