using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// S&amp;box Lane 5b — curvature wear + crevice AO baker (data only, no engine dependency).
///
/// What it computes, per vertex and per face, from a triangle / n-gon mesh given as plain
/// arrays (positions + index lists):
///   - convex sharp edges -&gt; edge wear: paint chipping / roughness wear on exposed edges.
///   - concave sharp edges -&gt; crevice AO: grime / dirt accumulation in crevices.
///
/// Method: for every undirected edge shared by two or more faces, take the dihedral angle
/// between the face normals (Newell normals, fan-triangulated for n-gons) and classify it:
///   - sharpness = angle / pi, so a 90-degree cube edge is 0.5 and a flat join is 0.
///   - sign: let n1 be face 1's outward normal and c2 face 2's centroid. If face 2's centroid
///     sits BELOW face 1's outward plane (dot(c2 - v0_1, n1) &lt; 0) the edge is CONVEX
///     (the solid turns away there, like a cube edge); if it sits ABOVE it (&gt; 0) the edge
///     is CONCAVE (the surface folds inward there, like a valley). This assumes faces are
///     OUTWARD-wound. On an inward-wound mesh wear and AO swap — that is a winding error in
///     the input, not something this baker silently repairs.
///   - a vertex's wear is the MAX convex sharpness over its incident edges (chipping happens
///     at the sharpest edge, it does not add up); a vertex's crevice AO COMBINES its concave
///     edges as 1 - prod(1 - sharpness), so a vertex sitting in two crevices is darker than
///     one sitting in one, but never exceeds 1.
///   - boundary edges (one face only) contribute nothing; non-manifold edges (&gt; 2 faces)
///     contribute their sharpest pair and are counted, not hidden.
///   - degenerate faces (repeated indices, zero area) are skipped and counted, never NaN.
///
/// WHAT THIS IS NOT: this is cavity-from-dihedral, not ray-traced ambient occlusion.
/// Occlusion from NEARBY but non-adjacent geometry (an armpit shadowing a torso, a finger
/// shadowing a palm) is NOT computed — only the fold of the surface itself. A consumer that
/// needs true occlusion must add a ray / sampling pass on top; it must not read these
/// numbers as that.
///
/// CONSUMER CONTRACT — for SmartPbrCompiler.cs (which this file deliberately does NOT
/// touch, create, or depend on). SmartPbrCompiler reads <see cref="PbrWearMap"/> as:
///   - VertexWear       -&gt; edge-chipping mask / roughness wear (convex edges only).
///   - VertexCreviceAo  -&gt; ambient-occlusion darkening (concave crevices only).
///   - VertexPaintChip  -&gt; paint-layer chip mask, derived from wear above ChipThreshold.
///   - VertexGrime      -&gt; dirt / grime accumulation mask, derived from crevice AO.
///   - FaceWear / FaceCreviceAo -&gt; per-face fallback for consumers without vertex attributes
///     (each is the plain average of its face's vertices).
/// All arrays are in [0, 1] and parallel to the input (vertex arrays parallel to positions,
/// face arrays parallel to faces). No texture is written here and no .vmat is emitted here —
/// SmartPbrCompiler owns rasterising these values into TextureRoughness /
/// TextureAmbientOcclusion (or vertex colours) and emitting the .vmat.
///
/// PolyMesh adapter (for the caller, not a dependency — this file takes arrays on purpose
/// so it compiles without the rest of the kernel):
///   positions = mesh.Positions.ToArray();
///   faces     = mesh.Faces.Select( f =&gt; f.Indices ).ToArray();
///   map       = PbrWearBaker.Bake( positions, faces );
/// </summary>
public static class PbrWearBaker
{
	/// <summary>Bake with default options.</summary>
	public static PbrWearMap Bake( Vec3[] positions, int[][] faces ) =>
		Bake( positions, faces, null );

	/// <summary>Bake curvature wear + crevice AO for one mesh.</summary>
	public static PbrWearMap Bake( Vec3[] positions, int[][] faces, PbrWearOptions options )
	{
		if ( positions is null )
			throw new ArgumentNullException( nameof( positions ) );
		if ( faces is null )
			throw new ArgumentNullException( nameof( faces ) );
		if ( positions.Length == 0 )
			throw new ArgumentException( "Mesh has no vertices.", nameof( positions ) );

		options ??= new PbrWearOptions();
		var edgeStrength = Math.Max( 0f, options.EdgeWearStrength );
		var creviceStrength = Math.Max( 0f, options.CreviceStrength );
		var grimeStrength = Math.Max( 0f, options.GrimeStrength );
		var chipThreshold = Math.Clamp( options.ChipThreshold, 0f, 1f );
		var minSharpRadians = options.MinSharpAngleDegrees <= 0f
			? 0f
			: options.MinSharpAngleDegrees * MathF.PI / 180f;

		var vertexCount = positions.Length;
		var faceCount = faces.Length;
		var faceNormals = new Vec3[faceCount];
		var faceCentroids = new Vec3[faceCount];
		var faceValid = new bool[faceCount];
		var degenerate = 0;

		for ( var fi = 0; fi < faceCount; fi++ )
		{
			var face = faces[fi];
			if ( face is null )
				throw new ArgumentException( $"Face {fi} is null.", nameof( faces ) );
			if ( face.Length < 3 )
				throw new ArgumentException( $"Face {fi} has {face.Length} indices, need at least 3.", nameof( faces ) );
			for ( var ci = 0; ci < face.Length; ci++ )
				if ( face[ci] < 0 || face[ci] >= vertexCount )
					throw new ArgumentOutOfRangeException( nameof( faces ),
						$"Face {fi} corner {ci} references vertex {face[ci]} outside 0..{vertexCount - 1}." );

			// Newell normal via fan from corner 0; zero vector means degenerate.
			var nx = 0f; var ny = 0f; var nz = 0f;
			var cx = 0f; var cy = 0f; var cz = 0f;
			for ( var ci = 0; ci < face.Length; ci++ )
			{
				var p = positions[face[ci]];
				cx += p.x; cy += p.y; cz += p.z;
			}
			var p0 = positions[face[0]];
			for ( var ci = 1; ci + 1 < face.Length; ci++ )
			{
				var a = positions[face[ci]] - p0;
				var b = positions[face[ci + 1]] - p0;
				var c = Vec3.Cross( a, b );
				nx += c.x; ny += c.y; nz += c.z;
			}
			var normal = new Vec3( nx, ny, nz );
			if ( normal.LengthSquared <= 1e-24f )
			{
				degenerate++;
				continue;
			}
			faceValid[fi] = true;
			faceNormals[fi] = normal.Normal;
			faceCentroids[fi] = new Vec3( cx / face.Length, cy / face.Length, cz / face.Length );
		}

		// Undirected edge -> faces touching it. Key packs (min, max) into one long.
		var edgeFaces = new Dictionary<long, List<int>>();
		for ( var fi = 0; fi < faceCount; fi++ )
		{
			if ( !faceValid[fi] )
				continue;
			var face = faces[fi];
			for ( var ci = 0; ci < face.Length; ci++ )
			{
				var a = face[ci];
				var b = face[(ci + 1) % face.Length];
				if ( a == b )
					continue;
				var key = a < b
					? ((long)(uint)a << 32) | (uint)b
					: ((long)(uint)b << 32) | (uint)a;
				if ( !edgeFaces.TryGetValue( key, out var list ) )
					edgeFaces[key] = list = new List<int>();
				if ( !list.Contains( fi ) )
					list.Add( fi );
			}
		}

		var wearRaw = new float[vertexCount];
		var aoRaw = new float[vertexCount];
		var sharpConvex = 0;
		var sharpConcave = 0;
		var nonManifold = 0;

		foreach ( var pair in edgeFaces )
		{
			var list = pair.Value;
			if ( list.Count < 2 )
				continue; // Boundary edge: open mesh, no fold to classify.
			if ( list.Count > 2 )
				nonManifold++;

			// Sharpest pair wins; sign comes from that pair.
			var bestAngle = -1f;
			var bestConvex = false;
			for ( var i = 0; i < list.Count; i++ )
			{
				for ( var j = i + 1; j < list.Count; j++ )
				{
					var f1 = list[i]; var f2 = list[j];
					var dot = Math.Clamp( Vec3.Dot( faceNormals[f1], faceNormals[f2] ), -1f, 1f );
					var angle = MathF.Acos( dot );
					if ( angle <= bestAngle )
						continue;
					bestAngle = angle;
					// Convex when face 2 falls below face 1's outward plane.
					var first = faces[f1][0];
					var side = Vec3.Dot( faceCentroids[f2] - positions[first], faceNormals[f1] );
					bestConvex = side < 0f;
				}
			}
			// A zero-angle (coplanar) join is not a fold at all: skipping it here keeps
			// flat diagonals off both sharp counts instead of falling to the concave side
			// on a zero dot product.
			if ( bestAngle <= 1e-6f || bestAngle < minSharpRadians )
				continue;
			var sharpness = bestAngle / MathF.PI;
			var a = (int)(pair.Key >> 32);
			var b = (int)(pair.Key & 0xffffffffL);
			if ( bestConvex )
			{
				sharpConvex++;
				if ( sharpness > wearRaw[a] ) wearRaw[a] = sharpness;
				if ( sharpness > wearRaw[b] ) wearRaw[b] = sharpness;
			}
			else
			{
				sharpConcave++;
				aoRaw[a] = 1f - (1f - aoRaw[a]) * (1f - sharpness);
				aoRaw[b] = 1f - (1f - aoRaw[b]) * (1f - sharpness);
			}
		}

		var map = new PbrWearMap
		{
			VertexWear = new float[vertexCount],
			VertexCreviceAo = new float[vertexCount],
			VertexPaintChip = new float[vertexCount],
			VertexGrime = new float[vertexCount],
			FaceWear = new float[faceCount],
			FaceCreviceAo = new float[faceCount],
			SharpConvexEdgeCount = sharpConvex,
			SharpConcaveEdgeCount = sharpConcave,
			DegenerateFaceCount = degenerate,
			NonManifoldEdgeCount = nonManifold,
		};
		for ( var v = 0; v < vertexCount; v++ )
		{
			var wear = Math.Clamp( wearRaw[v] * edgeStrength, 0f, 1f );
			var ao = Math.Clamp( aoRaw[v] * creviceStrength, 0f, 1f );
			map.VertexWear[v] = wear;
			map.VertexCreviceAo[v] = ao;
			map.VertexPaintChip[v] = chipThreshold >= 1f
				? (wear >= 1f ? 1f : 0f)
				: Math.Clamp( (wear - chipThreshold) / (1f - chipThreshold), 0f, 1f );
			map.VertexGrime[v] = Math.Clamp( ao * grimeStrength, 0f, 1f );
		}
		for ( var fi = 0; fi < faceCount; fi++ )
		{
			if ( !faceValid[fi] )
				continue;
			var face = faces[fi];
			var w = 0f; var a = 0f;
			for ( var ci = 0; ci < face.Length; ci++ )
			{
				w += map.VertexWear[face[ci]];
				a += map.VertexCreviceAo[face[ci]];
			}
			map.FaceWear[fi] = w / face.Length;
			map.FaceCreviceAo[fi] = a / face.Length;
		}
		return map;
	}
}

/// <summary>Tuning for <see cref="PbrWearBaker.Bake"/>; all strengths are multipliers, all outputs stay in [0, 1].</summary>
public sealed class PbrWearOptions
{
	/// <summary>Multiplier on convex-edge wear (1 = raw dihedral sharpness).</summary>
	public float EdgeWearStrength = 1f;
	/// <summary>Multiplier on concave crevice AO (1 = raw combined sharpness).</summary>
	public float CreviceStrength = 1f;
	/// <summary>Multiplier turning crevice AO into the grime mask (1 = grime equals AO).</summary>
	public float GrimeStrength = 1f;
	/// <summary>Wear below this does not chip paint at all; wear at 1 always chips fully. Default 0.25.</summary>
	public float ChipThreshold = 0.25f;
	/// <summary>Dihedral angles below this (degrees) are treated as flat — tessellation noise guard. Default 0 (exact).</summary>
	public float MinSharpAngleDegrees = 0f;
}

/// <summary>
/// Plain-data result of <see cref="PbrWearBaker.Bake"/>, shaped for SmartPbrCompiler to read.
/// No engine types, no textures, no file output — just arrays a compiler can rasterise.
/// </summary>
public sealed class PbrWearMap
{
	/// <summary>Per vertex, parallel to positions: convex-edge wear in [0, 1].</summary>
	public float[] VertexWear;
	/// <summary>Per vertex, parallel to positions: concave crevice AO in [0, 1].</summary>
	public float[] VertexCreviceAo;
	/// <summary>Per vertex: paint-chip mask derived from <see cref="VertexWear"/> above ChipThreshold.</summary>
	public float[] VertexPaintChip;
	/// <summary>Per vertex: dirt / grime mask derived from <see cref="VertexCreviceAo"/>.</summary>
	public float[] VertexGrime;
	/// <summary>Per face: average of its vertices' <see cref="VertexWear"/> (0 for degenerate faces).</summary>
	public float[] FaceWear;
	/// <summary>Per face: average of its vertices' <see cref="VertexCreviceAo"/> (0 for degenerate faces).</summary>
	public float[] FaceCreviceAo;
	/// <summary>Edges classified convex and sharper than MinSharpAngleDegrees.</summary>
	public int SharpConvexEdgeCount;
	/// <summary>Edges classified concave and sharper than MinSharpAngleDegrees.</summary>
	public int SharpConcaveEdgeCount;
	/// <summary>Faces skipped as degenerate (repeated indices / zero area).</summary>
	public int DegenerateFaceCount;
	/// <summary>Edges touched by more than 2 faces (sharpest pair used).</summary>
	public int NonManifoldEdgeCount;
}
