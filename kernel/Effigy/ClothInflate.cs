using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Vellum-style pressure inflation for a closed cloth volume: a diver suit, an air bladder,
/// a helmet bubble. This is NOT GarmentFeature's Puff (a normal offset baked into a fitted
/// garment) and NOT Drape (gravity settle onto colliders). PressureBalloon takes a sealed
/// mesh and grows its volume toward a pressure target, held back by edge-length constraints
/// so the cloth stretches instead of exploding — the same tension that keeps a real
/// pressurised suit from blowing out.
///
/// STABILITY, DELIBERATELY. Each iteration displaces along vertex normals by at most a
/// fraction of the mesh's characteristic length, then runs edge constraints, then checks
/// every position is finite. If a step ever diverges the solver restores the last good
/// positions and reports failure instead of handing the tree a NaN mesh. A fixed
/// topology can only stretch so far: edge constraints mean the final volume usually
/// lands BELOW the target, and that is reported honestly (Converged = false) rather than
/// faked by scaling the mesh to the target after the fact.
///
/// Deterministic: same mesh, same options, same positions out. No randomness, no
/// wall-clock, no GPU.
/// </summary>
public static class PressureBalloon
{
	public sealed class Options
	{
		/// <summary>Fraction of rest volume to add: 0.5 = 50% bigger. -0.9..2, clamped.</summary>
		public float Pressure = 0.5f;

		/// <summary>Pressure/constraint iterations. 40 is enough for a sphere; more helps a big mesh converge, not explode.</summary>
		public int Steps = 40;

		/// <summary>0 = free membrane (no edge resistance, grows to target and past it), 1 = inextensible cloth.</summary>
		public float Stiffness = 0.8f;

		/// <summary>Vertex indices that never move: a neck ring, a waist seam. Null = none.</summary>
		public IReadOnlyList<int> Pins;
	}

	public sealed class Result
	{
		public bool Ok;
		public string Reason = "";
		public PolyMesh Mesh;
		public float RestVolume;
		public float TargetVolume;
		public float FinalVolume;
		public int StepsTaken;
		public bool Converged;
	}

	public static Result Inflate( PolyMesh mesh, Options o )
	{
		o ??= new Options();

		if ( mesh is null )
			return new Result { Ok = false, Reason = "No mesh to inflate." };

		if ( mesh.VertexCount == 0 || mesh.FaceCount == 0 )
			return new Result { Ok = false, Reason = "The mesh has no vertices or faces to inflate." };

		// A pressure volume must be sealed. An open sheet has no inside, so "volume" is
		// meaningless and the normals have nothing to push against — fail, don't guess.
		var boundary = BoundaryEdgeCount( mesh );

		if ( boundary > 0 )
			return new Result { Ok = false, Reason = $"The mesh is open ({boundary} boundary edges). PressureBalloon needs a closed volume — a sealed sphere, a closed suit, a helmet bubble — not a sheet." };

		var work = mesh.Clone();
		var restVolume = MathF.Abs( Volume( work ) );

		if ( restVolume <= 1e-9f || !float.IsFinite( restVolume ) )
			return new Result { Ok = false, Reason = "The mesh encloses no volume (flat or degenerate). Inflate a closed solid, not a flat shape." };

		var pressure = Math.Clamp( o.Pressure, -0.9f, 2f );
		var stiffness = Math.Clamp( o.Stiffness, 0f, 1f );
		var steps = Math.Clamp( o.Steps, 1, 500 );
		var target = restVolume * ( 1f + pressure );
		var charLen = MathF.Pow( restVolume, 1f / 3f );
		var maxStep = 0.1f * charLen;

		var pinned = new HashSet<int>( o.Pins ?? Array.Empty<int>() );
		var edges = UniqueEdges( work );
		var restLengths = edges.Select( e => (work.Positions[e.A] - work.Positions[e.B]).Length ).ToArray();

		var centroid = Centroid( work );
		var lastGood = new List<Vec3>( work.Positions );
		var converged = false;
		var taken = 0;

		for ( var step = 0; step < steps; step++ )
		{
			taken = step + 1;
			var volume = MathF.Abs( Volume( work ) );

			if ( MathF.Abs( volume - target ) <= 0.01f * target )
			{
				converged = true;
				break;
			}

			var area = SurfaceArea( work );

			if ( area <= 1e-9f || !float.IsFinite( area ) )
				break;

			// Uniform pressure displacement: the volume error spread over the surface.
			var push = Math.Clamp( (target - volume) / area, -maxStep, maxStep );
			var normals = VertexNormals( work, centroid );

			for ( var i = 0; i < work.VertexCount; i++ )
			{
				if ( pinned.Contains( i ) )
					continue;

				work.Positions[i] += normals[i] * push;
			}

			// Edge constraints: the cloth's tension. Without these the membrane grows
			// without bound; with them it stretches toward the target and stops.
			ConstrainEdges( work, edges, restLengths, stiffness, pinned );

			if ( !work.Positions.All( p => float.IsFinite( p.x ) && float.IsFinite( p.y ) && float.IsFinite( p.z ) ) )
			{
				work.Positions = new List<Vec3>( lastGood );
				return new Result
				{
					Ok = false, Reason = "Inflation diverged (non-finite positions) — restored the last good shape. Lower the pressure or raise the stiffness.",
					Mesh = work, RestVolume = restVolume, TargetVolume = target,
					FinalVolume = MathF.Abs( Volume( work ) ), StepsTaken = taken, Converged = false,
				};
			}

			lastGood = new List<Vec3>( work.Positions );
		}

		var finalVolume = MathF.Abs( Volume( work ) );

		if ( MathF.Abs( finalVolume - target ) <= 0.02f * target )
			converged = true;

		return new Result
		{
			Ok = true, Mesh = work, RestVolume = restVolume, TargetVolume = target,
			FinalVolume = finalVolume, StepsTaken = taken, Converged = converged,
		};
	}

	/// <summary>Signed volume by fan-triangulating every face. Sign depends on winding; callers take Abs.</summary>
	public static float Volume( PolyMesh mesh )
	{
		var total = 0f;

		foreach ( var f in mesh.Faces )
		{
			if ( f.Indices.Length < 3 )
				continue;

			var a = mesh.Positions[f.Indices[0]];

			for ( var i = 1; i + 1 < f.Indices.Length; i++ )
			{
				var b = mesh.Positions[f.Indices[i]];
				var c = mesh.Positions[f.Indices[i + 1]];
				total += Vec3.Dot( a, Vec3.Cross( b - a, c - a ) ) / 6f;
			}
		}

		return total;
	}

	public static float SurfaceArea( PolyMesh mesh )
	{
		var total = 0f;

		foreach ( var f in mesh.Faces )
		{
			if ( f.Indices.Length < 3 )
				continue;

			var a = mesh.Positions[f.Indices[0]];

			for ( var i = 1; i + 1 < f.Indices.Length; i++ )
			{
				var b = mesh.Positions[f.Indices[i]];
				var c = mesh.Positions[f.Indices[i + 1]];
				total += Vec3.Cross( b - a, c - a ).Length / 2f;
			}
		}

		return total;
	}

	/// <summary>Area-weighted vertex normals, flipped to point away from the centroid so inward-wound meshes still inflate outward.</summary>
	public static Vec3[] VertexNormals( PolyMesh mesh, Vec3 centroid )
	{
		var normals = new Vec3[mesh.VertexCount];

		foreach ( var f in mesh.Faces )
		{
			if ( f.Indices.Length < 3 )
				continue;

			var a = mesh.Positions[f.Indices[0]];

			for ( var i = 1; i + 1 < f.Indices.Length; i++ )
			{
				var b = mesh.Positions[f.Indices[i]];
				var c = mesh.Positions[f.Indices[i + 1]];
				var n = Vec3.Cross( b - a, c - a );
				normals[f.Indices[0]] += n;
				normals[f.Indices[i]] += n;
				normals[f.Indices[i + 1]] += n;
			}
		}

		for ( var i = 0; i < normals.Length; i++ )
		{
			var n = normals[i].Normal;

			if ( Vec3.Dot( n, mesh.Positions[i] - centroid ) < 0f )
				n = -n;

			normals[i] = n;
		}

		return normals;
	}

	public static int BoundaryEdgeCount( PolyMesh mesh )
	{
		var uses = new Dictionary<EdgeKey, int>();

		foreach ( var f in mesh.Faces )
		{
			for ( var i = 0; i < f.Indices.Length; i++ )
			{
				var key = new EdgeKey( f.Indices[i], f.Indices[(i + 1) % f.Indices.Length] );
				uses[key] = uses.TryGetValue( key, out var count ) ? count + 1 : 1;
			}
		}

		return uses.Values.Count( c => c == 1 );
	}

	static List<(int A, int B)> UniqueEdges( PolyMesh mesh )
	{
		var seen = new HashSet<EdgeKey>();
		var edges = new List<(int, int)>();

		foreach ( var f in mesh.Faces )
		{
			for ( var i = 0; i < f.Indices.Length; i++ )
			{
				var a = f.Indices[i];
				var b = f.Indices[(i + 1) % f.Indices.Length];
				var key = new EdgeKey( a, b );

				if ( seen.Add( key ) )
					edges.Add( (a, b) );
			}
		}

		return edges;
	}

	static void ConstrainEdges( PolyMesh mesh, List<(int A, int B)> edges, float[] restLengths, float stiffness, HashSet<int> pinned )
	{
		if ( stiffness <= 0f )
			return;

		for ( var i = 0; i < edges.Count; i++ )
		{
			var (a, b) = edges[i];
			var pa = mesh.Positions[a];
			var pb = mesh.Positions[b];
			var delta = pb - pa;
			var len = delta.Length;

			if ( len < 1e-9f )
				continue;

			var wa = pinned.Contains( a ) ? 0f : 1f;
			var wb = pinned.Contains( b ) ? 0f : 1f;
			var total = wa + wb;

			if ( total <= 0f )
				continue;

			var correction = (len - restLengths[i]) * stiffness / total;
			var dir = delta / len;
			mesh.Positions[a] += dir * correction * wa;
			mesh.Positions[b] -= dir * correction * wb;
		}
	}

	static Vec3 Centroid( PolyMesh mesh )
	{
		var sum = Vec3.Zero;

		foreach ( var p in mesh.Positions )
			sum += p;

		return mesh.VertexCount == 0 ? sum : sum / mesh.VertexCount;
	}
}
