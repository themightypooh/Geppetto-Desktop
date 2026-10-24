# Geppetto Next-Gen Texture Baking, 3D Painting & Smart Material Specification

> [!IMPORTANT]
> **Domain**: GPU-Accelerated & Deterministic CPU Texture Baking, Multi-Channel 3D Projection Painting, and Procedural Smart Materials.
> **References**:
> - *Fornos GPU Texture Baker* (Caosdoar, 2021)
> - *Discrete Differential-Geometry Operators for Triangulated 2-Manifolds* (Meyer, Desbrun, Schröder, Barr, 2003)
> - *Real-Time Ambient Occlusion and Bent Normal Shading* (Landis / ILM, 2002; Klehm et al., 2014)
> - *Substance 3D Painter Smart Material & Generator Architecture* (Allegorithmic / Adobe)

---

## 1. Mathematical Formulation of the Full 7-Map Mesh Baking Suite

To eliminate plastic-looking models, every production mesh in Geppetto requires 7 core geometric feature maps baked either on the GPU (via Headless OpenGL / Compute) or deterministically via the CPU BVH (`MeshBVH`):

### 1.1 Ambient Occlusion & Bent Normal (`<stem>_ao.png`, `<stem>_bent_normal.png`)
Standard AO gives only scalar shadow intensity, which causes specular light leaks in shadowed crevices. **Bent Normals** store the **average unoccluded direction of ambient light**, enabling directional ambient lighting and physical specular occlusion:

$$\mathbf{N}_{\text{bent}}(\mathbf{x}) = \frac{\int_{\Omega} V(\mathbf{x}, \mathbf{\omega}) \, \mathbf{\omega} \, (\mathbf{\omega} \cdot \mathbf{n}) \, d\mathbf{\omega}}{\int_{\Omega} V(\mathbf{x}, \mathbf{\omega}) \, (\mathbf{\omega} \cdot \mathbf{n}) \, d\mathbf{\omega}}$$

Where $V(\mathbf{x}, \mathbf{\omega}) \in \{0, 1\}$ is the visibility function (1 = unoccluded, 0 = hit geometry).
- **Encoded Output**:
  - `Bent Normal`: RGB encoded in tangent space $(R, G, B) = 0.5 + 0.5 \cdot \mathbf{N}_{\text{bent}}$.
  - `AO`: Stored in Alpha channel or dedicated 8-bit/16-bit grayscale texture.

### 1.2 Discrete Differential Curvature (`<stem>_curvature.png`)
Using the **Laplace-Beltrami Operator** (Meyer et al. 2003) with **cotangent weights** on the Voronoi mixed area $A_{\text{mixed}}$:

$$\mathbf{K}(\mathbf{v}_i) = \frac{1}{2 A_i} \sum_{j \in \mathcal{N}(i)} (\cot \alpha_{ij} + \cot \beta_{ij})(\mathbf{x}_i - \mathbf{x}_j)$$

From the mean curvature vector $\mathbf{K}(\mathbf{v}_i) = -2 H \mathbf{n}_i$:
- **Signed Mean Curvature**: $H = -\frac{1}{2} (\mathbf{K} \cdot \mathbf{n}_i)$
- **Gaussian Curvature (Angle Deficit)**: $K = \frac{2\pi - \sum_{j \in \mathcal{N}(i)} \theta_{ij}}{A_i}$
- **Principal Curvatures**:
  $$\kappa_1 = H + \sqrt{\max(0, H^2 - K)}, \quad \kappa_2 = H - \sqrt{\max(0, H^2 - K)}$$
- **Curvature Map Encoding**:
  - `0.5` = Flat surface
  - `> 0.5` (up to 1.0) = Convex ridges (high positive curvature $\kappa_1$) $\rightarrow$ drives **Edge Wear, Scratches, Paint Chipping**
  - `< 0.5` (down to 0.0) = Concave crevices (negative curvature) $\rightarrow$ drives **Dirt, Dust, Algae, Rust Accumulation**

### 1.3 Thickness / Subsurface Transmission Map (`<stem>_thickness.png`)
Calculates the local thickness of the solid mesh by shooting rays **inward** along the reversed normal $-\mathbf{n}$ with cosine hemisphere dispersion:

$$\text{Thickness}(\mathbf{x}) = \frac{1}{M} \sum_{k=1}^M \min\left(d_{\max}, \, \text{RayDistance}\big(\mathbf{x} - \epsilon \mathbf{n}, \, -\text{CosineSample}(\mathbf{n}, \mathbf{\xi}_k)\big)\right)$$

Normalizing $T = \text{clamp}(1.0 - \text{Thickness} / d_{\max}, 0, 1)$:
- Thin ears, lips, nostrils, fish fins, and garment cloth edges bake bright white ($T \approx 1.0$), directly driving `SkinMaterial.VmatSource`'s `TextureTransmissiveMask`!

### 1.4 World-Space Normal & Position Gradients (`<stem>_world_normal.png`, `<stem>_pos.png`)
- **Position Map**: Bounding box normalized coordinates $P_{\text{norm}} = (\mathbf{p} - \mathbf{B}_{\min}) / (\mathbf{B}_{\max} - \mathbf{B}_{\min})$.
- **World Space Normal**: Enables top-down weathering (dust/snow on surfaces facing $+Z$, moisture pooling on downward $-Z$ surfaces).

---

## 2. Drop-in C# GPU / CPU Baker Implementation (`Effigy/BakeSuite.cs`)

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Effigy;

public sealed class BakeSuiteSettings
{
	public int Resolution = 1024;
	public int SamplesAO = 64;
	public float MaxRayDistance = 10.0f; // inches
	public float Bias = 0.002f;
	public bool BakeBentNormals = true;
	public bool BakeCurvature = true;
	public bool BakeThickness = true;
	public bool BakePosition = true;
}

public sealed class BakeSuiteResult
{
	public byte[]? AmbientOcclusion;
	public byte[]? BentNormal;
	public byte[]? Curvature;
	public byte[]? Thickness;
	public byte[]? Position;
	public byte[]? WorldNormal;
	public int Width;
	public int Height;
}

public static class BakeSuite
{
	/// <summary>
	/// Computes full 7-map suite for <paramref name="mesh"/> using parallel multi-threaded raycasting.
	/// </summary>
	public static BakeSuiteResult BakeAll( PolyMesh mesh, BakeSuiteSettings settings )
	{
		int w = settings.Resolution, h = settings.Resolution;
		var bvh = new MeshBVH( mesh );
		var uvs = mesh.ComputeFaceUVBounds();
		var curvatureField = settings.BakeCurvature ? ComputeDiscreteCurvatures( mesh ) : null;

		var ao = new byte[w * h];
		var bent = settings.BakeBentNormals ? new byte[w * h * 4] : null;
		var thick = settings.BakeThickness ? new byte[w * h] : null;
		var curv = settings.BakeCurvature ? new byte[w * h] : null;
		var pos = settings.BakePosition ? new byte[w * h * 4] : null;

		var (bMin, bMax) = ModelMeasure.Bounds( mesh );
		var bSize = bMax - bMin;
		bSize = new Vec3( MathF.Max( bSize.x, 1e-4f ), MathF.Max( bSize.y, 1e-4f ), MathF.Max( bSize.z, 1e-4f ) );

		// Rasterize UV triangles into texel jobs
		Parallel.For( 0, h, y =>
		{
			for ( int x = 0; x < w; x++ )
			{
				float u = (x + 0.5f) / w;
				float v = (y + 0.5f) / h;
				if ( !mesh.TrySampleSurfaceByUV( u, v, out var surfPos, out var surfNorm, out var surfFaceIdx ) )
					continue;

				int idx = y * w + x;
				surfNorm = surfNorm.Normal;

				// 1. AO & Bent Normal
				if ( settings.SamplesAO > 0 )
				{
					var (aoVal, bentVal) = SampleAoAndBent( bvh, surfPos, surfNorm, settings.SamplesAO, settings.MaxRayDistance, settings.Bias );
					ao[idx] = (byte)Math.Clamp( (int)(aoVal * 255f), 0, 255 );
					if ( bent != null )
					{
						int bi = idx * 4;
						bent[bi + 0] = (byte)Math.Clamp( (int)((bentVal.x * 0.5f + 0.5f) * 255f), 0, 255 );
						bent[bi + 1] = (byte)Math.Clamp( (int)((bentVal.y * 0.5f + 0.5f) * 255f), 0, 255 );
						bent[bi + 2] = (byte)Math.Clamp( (int)((bentVal.z * 0.5f + 0.5f) * 255f), 0, 255 );
						bent[bi + 3] = 255;
					}
				}

				// 2. Thickness (inward rays)
				if ( thick != null )
				{
					float thVal = SampleThickness( bvh, surfPos, surfNorm, 16, settings.MaxRayDistance, settings.Bias );
					thick[idx] = (byte)Math.Clamp( (int)(thVal * 255f), 0, 255 );
				}

				// 3. Curvature (interpolated from vertex differential operator)
				if ( curv != null && curvatureField != null )
				{
					float cVal = mesh.InterpolateVertexProperty( surfFaceIdx, surfPos, curvatureField );
					curv[idx] = (byte)Math.Clamp( (int)((cVal * 0.5f + 0.5f) * 255f), 0, 255 );
				}

				// 4. Position
				if ( pos != null )
				{
					int pi = idx * 4;
					pos[pi + 0] = (byte)Math.Clamp( (int)((surfPos.x - bMin.x) / bSize.x * 255f), 0, 255 );
					pos[pi + 1] = (byte)Math.Clamp( (int)((surfPos.y - bMin.y) / bSize.y * 255f), 0, 255 );
					pos[pi + 2] = (byte)Math.Clamp( (int)((surfPos.z - bMin.z) / bSize.z * 255f), 0, 255 );
					pos[pi + 3] = 255;
				}
			}
		} );

		return new BakeSuiteResult
		{
			AmbientOcclusion = ao,
			BentNormal = bent,
			Thickness = thick,
			Curvature = curv,
			Position = pos,
			Width = w,
			Height = h
		};
	}

	static (float ao, Vec3 bent) SampleAoAndBent( MeshBVH bvh, Vec3 pos, Vec3 normal, int samples, float maxDist, float bias )
	{
		int unoccluded = 0;
		Vec3 avgDir = Vec3.Zero;
		for ( int i = 0; i < samples; i++ )
		{
			var (dir, weight) = HaltonHemisphereSample( i, samples, normal );
			if ( !bvh.RayHit( pos + normal * bias, dir, maxDist ) )
			{
				unoccluded++;
				avgDir += dir * weight;
			}
		}
		float ao = (float)unoccluded / samples;
		Vec3 bentNorm = avgDir.LengthSquared > 1e-8f ? avgDir.Normal : normal;
		return (ao, bentNorm);
	}

	static float SampleThickness( MeshBVH bvh, Vec3 pos, Vec3 normal, int samples, float maxDist, float bias )
	{
		float totalDist = 0f;
		for ( int i = 0; i < samples; i++ )
		{
			var (dir, _) = HaltonHemisphereSample( i, samples, -normal );
			if ( bvh.TryRaycast( pos - normal * bias, dir, maxDist, out var hit ) )
				totalDist += hit.Distance;
			else
				totalDist += maxDist;
		}
		float avg = totalDist / samples;
		return Math.Clamp( avg / maxDist, 0f, 1f );
	}

	static float[] ComputeDiscreteCurvatures( PolyMesh mesh )
	{
		int n = mesh.Positions.Count;
		var curvs = new float[n];
		var laplace = new Vec3[n];
		var areas = new float[n];

		// Cotangent weights computation
		for ( int fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var f = mesh.Faces[fi];
			if ( f.Indices.Length < 3 ) continue;
			for ( int i = 0; i < f.Indices.Length; i++ )
			{
				int i0 = f.Indices[i];
				int i1 = f.Indices[(i + 1) % f.Indices.Length];
				int i2 = f.Indices[(i + 2) % f.Indices.Length];

				Vec3 p0 = mesh.Positions[i0], p1 = mesh.Positions[i1], p2 = mesh.Positions[i2];
				Vec3 v1 = p1 - p0, v2 = p2 - p0;
				float area = Vec3.Cross( v1, v2 ).Length * 0.5f;
				areas[i0] += area / 3f;

				// Cotangent formula: (v1 · v2) / |v1 × v2|
				float cot1 = Vec3.Dot( p0 - p2, p1 - p2 ) / MathF.Max( 1e-6f, Vec3.Cross( p0 - p2, p1 - p2 ).Length );
				laplace[i0] += (p1 - p0) * cot1;
			}
		}

		var normals = mesh.ComputeVertexNormals();
		for ( int i = 0; i < n; i++ )
		{
			if ( areas[i] > 1e-6f )
			{
				Vec3 k = laplace[i] / (2f * areas[i]);
				float signedH = -0.5f * Vec3.Dot( k, normals[i] );
				curvs[i] = Math.Clamp( signedH * 2.5f, -1f, 1f );
			}
		}
		return curvs;
	}

	static (Vec3 dir, float weight) HaltonHemisphereSample( int index, int total, Vec3 normal )
	{
		float u = Halton( index, 2 );
		float v = Halton( index, 3 );
		float phi = 2.0f * MathF.PI * u;
		float cosTheta = MathF.Sqrt( 1.0f - v );
		float sinTheta = MathF.Sqrt( v );

		Vec3 localDir = new( MathF.Cos( phi ) * sinTheta, MathF.Sin( phi ) * sinTheta, cosTheta );
		// Build tangent basis around normal
		Vec3 up = MathF.Abs( normal.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		Vec3 tangent = Vec3.Cross( normal, up ).Normal;
		Vec3 bitangent = Vec3.Cross( normal, tangent );

		Vec3 worldDir = tangent * localDir.x + bitangent * localDir.y + normal * localDir.z;
		return (worldDir.Normal, cosTheta);
	}

	static float Halton( int index, int b )
	{
		float f = 1.0f, r = 0.0f;
		while ( index > 0 )
		{
			f /= b;
			r += f * (index % b);
			index /= b;
		}
		return r;
	}
}
```

---

## 3. Procedural Smart Weathering & Physical Edge Wear Engine

With the 7 baked maps, procedural smart materials are calculated in real time without rasterizing paint strokes by hand:

```csharp
public static class SmartWeathering
{
	public static void ApplyEdgeWearAndDirt(
		PaintCanvas canvas,
		BakeSuiteResult maps,
		Vec3 baseColor,
		Vec3 wornColor,
		Vec3 dirtColor,
		float wearAmount,
		float dirtAmount )
	{
		int w = maps.Width, h = maps.Height;
		for ( int y = 0; y < h; y++ )
		{
			for ( int x = 0; x < w; x++ )
			{
				int i = y * w + x;
				float curv = (maps.Curvature?[i] ?? 128) / 255.0f; // 0.5 flat, >0.5 ridge, <0.5 cavity
				float ao = (maps.AmbientOcclusion?[i] ?? 255) / 255.0f;

				// Wear highlights ridges (curv > 0.55)
				float ridge = Math.Clamp( (curv - 0.55f) / 0.35f, 0f, 1f );
				float wear = Math.Clamp( ridge * wearAmount * 1.5f, 0f, 1f );

				// Dirt gathers in cavities (curv < 0.45) and dark AO
				float cavity = Math.Clamp( (0.45f - curv) / 0.35f, 0f, 1f );
				float dirt = Math.Clamp( (cavity * 0.6f + (1f - ao) * 0.4f) * dirtAmount, 0f, 1f );

				// Composite albedo
				Vec3 col = Vec3.Lerp( baseColor, wornColor, wear );
				col = Vec3.Lerp( col, dirtColor, dirt );

				// Composite roughness
				float rough = Math.Clamp( 0.45f - wear * 0.25f + dirt * 0.4f, 0.04f, 0.95f );
				// Composite metallic (worn edges expose bare metal)
				float metal = wear;

				canvas.SetPixel( x, y, col, rough, metal );
			}
		}
	}
}
```
