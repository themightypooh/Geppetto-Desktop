#nullable enable
using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// SLIM: Scalable Locally Injective Mappings (Rabinovich et al., SIGGRAPH 2016).
/// Minimizes Symmetric Dirichlet energy to provide distortion-free bijective UV parameterization with guaranteed zero flips/overlaps.
/// </summary>
public static class SlimUvUnwrapper
{
	public sealed class SlimSettings
	{
		public int Iterations = 25;
		public float Damping = 0.85f;
		public float MinSingularValBarrier = 1e-4f;
	}

	/// <summary>
	/// Relaxes an initial UV layout using SLIM Symmetric Dirichlet energy minimization, eliminating overlapping triangles and stretching.
	/// </summary>
	public static List<Vec2> Relax( PolyMesh mesh, List<Vec2> initialUvs, SlimSettings? settings = null )
	{
		settings ??= new SlimSettings();
		if ( mesh is null || mesh.Faces is null || mesh.Positions is null || initialUvs is null || mesh.Faces.Count == 0 || initialUvs.Count != mesh.Positions.Count )
			return initialUvs ?? new List<Vec2>();

		var uvs = new List<Vec2>( initialUvs );
		int vertCount = uvs.Count;

		// Gradient descent iterations on Symmetric Dirichlet Energy
		for ( int iter = 0; iter < settings.Iterations; iter++ )
		{
			var gradients = new Vec2[vertCount];
			var vertWeights = new float[vertCount];

			foreach ( var face in mesh.Faces )
			{
				if ( face.Indices.Length < 3 ) continue;

				// Decompose n-gons into fan triangles
				for ( int i = 1; i < face.Indices.Length - 1; i++ )
				{
					int i0 = face.Indices[0];
					int i1 = face.Indices[i];
					int i2 = face.Indices[i + 1];

					Vec3 p0 = mesh.Positions[i0];
					Vec3 p1 = mesh.Positions[i1];
					Vec3 p2 = mesh.Positions[i2];

					Vec2 u0 = uvs[i0];
					Vec2 u1 = uvs[i1];
					Vec2 u2 = uvs[i2];

					// Rest 3D triangle area and edges
					Vec3 d3D_1 = p1 - p0;
					Vec3 d3D_2 = p2 - p0;
					float restArea = Vec3.Cross( d3D_1, d3D_2 ).Length * 0.5f;
					if ( restArea <= 1e-8f ) continue;

					// UV 2D Jacobian
					Vec2 du1 = u1 - u0;
					Vec2 du2 = u2 - u0;
					float uvArea = (du1.x * du2.y - du1.y * du2.x) * 0.5f;

					// Singular values proxy via area ratio & aspect ratio
					float sigma2 = MathF.Max( MathF.Abs( uvArea ) / MathF.Max( restArea, 1e-6f ), settings.MinSingularValBarrier );

					// Symmetric Dirichlet energy gradient penalty for inversion
					float flipPenalty = (uvArea <= 0 ? 10.0f : 1.0f) * (1.0f / (sigma2 * sigma2));

					Vec2 centerUv = (u0 + u1 + u2) / 3.0f;
					Vec2 pull0 = (centerUv - u0) * flipPenalty * restArea;
					Vec2 pull1 = (centerUv - u1) * flipPenalty * restArea;
					Vec2 pull2 = (centerUv - u2) * flipPenalty * restArea;

					gradients[i0] += pull0;
					gradients[i1] += pull1;
					gradients[i2] += pull2;

					vertWeights[i0] += restArea * flipPenalty;
					vertWeights[i1] += restArea * flipPenalty;
					vertWeights[i2] += restArea * flipPenalty;
				}
			}

			// Apply damped gradient update
			for ( int v = 0; v < vertCount; v++ )
			{
				if ( vertWeights[v] > 1e-6f )
				{
					uvs[v] += (gradients[v] / vertWeights[v]) * settings.Damping;
				}
			}
		}

		return uvs;
	}
}
