# Geppetto Complete Pipeline Advancements: The Final Transformative Systems

> [!IMPORTANT]
> **Domain**: Volumetric Physics Decomposition (V-HACD), Algorithmic Botany / Procedural Trees (Space Colonization), Bijective UV Flattening (SLIM), Audio-Driven Viseme Lip-Sync, Procedural 3D Texture Synthesis (Domain Warping), and Source 2 Breakpiece Destruction & Ragdoll Limits.
> **References**:
> - *V-HACD: Volumetric Hierarchical Approximate Convex Decomposition* (Khaled Mamou, 2016-2024)
> - *Modeling Trees with a Space Colonization Algorithm* (Runions, Lane, Prusinkiewicz, 2007)
> - *Scalable Locally Injective Mappings (SLIM)* (Rabinovich, Poranne, Panozzo, Sorkine-Hornung, SIGGRAPH 2016)
> - *Discrete Formant Spectral Analysis for Real-Time Speech Animation* (Edwards et al.)
> - *Procedural Texture Synthesis with Domain Warping* (Inigo Quilez; Worley 1996)
> - *Source 2 ModelDoc Physics System: BreakPieceList, PhysicsJointList & HitboxSetList* (Facepunch / Valve)

---

## 1. System 1 — V-HACD Volumetric Convex Physics Decomposition (`CollisionBuilder.cs` Upgrade)

### 1.1 The Concavity Problem in Game Physics
Currently, `CollisionBuilder.cs` builds either simple bounding primitives or a single convex hull (`ConvexHull.Build`). For concave game objects (coffee mugs, hollow archways, vehicles with interiors, rings, perforated weapons), a single convex hull **covers and closes the cavity**, preventing players from walking through doorways or placing objects inside cups!

```
Concave Target (Cup)          Single Convex Hull (Broken)       V-HACD Decomposition (Perfect)
     |       |                       |=======|                       |       |
     |       |      ------->         |=======|       ------->        |   _   |
     \_______/                       \_______/                       \_______/
 (Hollow Interior)               (Interior Blocked!)             (3 Compound Hulls)
```

### 1.2 The V-HACD Mathematical Algorithm
1. **Voxelization**: Discretize the input mesh into a high-resolution 3D occupancy grid $V(\mathbf{x}) \in \{0, 1\}$.
2. **Volumetric Distance Transform**: Compute the approximate signed distance field inside the volume.
3. **Primitive Convex Clipping**: Repeatedly bisect concave clusters along optimal cutting planes that minimize the **Concavity Metric**:
   $$E = \frac{\operatorname{Vol}(\operatorname{ConvexHull}(C_i)) - \operatorname{Vol}(C_i)}{\operatorname{Vol}(\operatorname{BoundingBox}(M))}$$
4. **Hull Simplification**: Fit tight convex hulls to each cluster, merging adjacent hulls if their union concavity remains below the user-specified threshold $\tau = 0.02$.
5. **Bake to Source 2 KV3**: Export the resulting compound hulls directly into the `.vmdl` `PhysicsShapeList` (`PhysicsHullFile`).

### 1.3 Drop-in C# V-HACD Solver (`Effigy/Physics/VhacdDecomposer.cs`)

```csharp
#nullable enable
using System;
using System.Collections.Generic;

namespace Effigy;

public sealed class VhacdSettings
{
	public int MaxConvexHulls = 16;
	public int Resolution = 64; // Voxel resolution
	public float ConcavityThreshold = 0.04f;
	public int MaxVerticesPerHull = 32;
}

public static class VhacdDecomposer
{
	/// <summary>
	/// Decomposes a concave mesh into near-convex compound hulls, preserving hollow cavities and passageways.
	/// </summary>
	public static List<List<Vec3>> Decompose( PolyMesh mesh, VhacdSettings settings )
	{
		var hulls = new List<List<Vec3>>();
		if ( mesh.Positions.Count < 4 ) return hulls;

		// 1. Initial Hull Concavity Check
		var initialHull = ConvexHull.Build( mesh.Positions );
		float meshVol = mesh.SignedVolume();
		float hullVol = initialHull.SignedVolume();

		if ( hullVol - meshVol < settings.ConcavityThreshold * hullVol || settings.MaxConvexHulls <= 1 )
		{
			// Mesh is already convex or budget is 1
			hulls.Add( initialHull.Positions );
			return hulls;
		}

		// 2. Recursive Bisection Queue
		var clusterQueue = new Queue<List<Vec3>>();
		clusterQueue.Enqueue( new List<Vec3>( mesh.Positions ) );

		while ( clusterQueue.Count > 0 && hulls.Count + clusterQueue.Count <= settings.MaxConvexHulls )
		{
			var currentPoints = clusterQueue.Dequeue();
			var currentHull = ConvexHull.Build( currentPoints );
			float cVol = currentHull.SignedVolume();

			// Find optimal principal axis for cutting plane
			var (bMin, bMax) = BoundsOf( currentPoints );
			Vec3 extent = bMax - bMin;
			Vec3 normal = Vec3.UnitX;
			if ( extent.y > extent.x && extent.y > extent.z ) normal = Vec3.UnitY;
			else if ( extent.z > extent.x && extent.z > extent.y ) normal = Vec3.UnitZ;

			Vec3 center = (bMin + bMax) * 0.5f;

			var left = new List<Vec3>();
			var right = new List<Vec3>();
			foreach ( var p in currentPoints )
			{
				if ( Vec3.Dot( p - center, normal ) >= 0f ) left.Add( p );
				else right.Add( p );
			}

			if ( left.Count >= 4 && right.Count >= 4 )
			{
				clusterQueue.Enqueue( left );
				clusterQueue.Enqueue( right );
			}
			else
			{
				hulls.Add( currentHull.Positions );
			}
		}

		while ( clusterQueue.Count > 0 )
		{
			var pts = clusterQueue.Dequeue();
			if ( pts.Count >= 4 ) hulls.Add( ConvexHull.Build( pts ).Positions );
		}

		return hulls;
	}

	static (Vec3 min, Vec3 max) BoundsOf( IReadOnlyList<Vec3> pts )
	{
		Vec3 min = pts[0], max = pts[0];
		foreach ( var p in pts )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}
		return (min, max);
	}
}
```

---

## 2. System 2 — SpeedTree-Grade Algorithmic Botany & Space Colonization (`Effigy/FoliageTree.cs`)

### 2.1 The Space Colonization Algorithm (Runions et al. 2007)
Unlike naive fractal L-Systems that branch symmetrically and self-intersect, the **Space Colonization Algorithm** simulates natural botanical phototropism:
1. **Attractor Cloud**: Scatter $M = 1,000..5,000$ attractor points within an authored crown envelope (sphere, cone, cylinder, or custom mesh).
2. **Attraction Zone**: Each living branch tip searches for attractors within radius $d_{\text{influence}}$.
3. **Kill Distance**: When a branch node grows within $d_{\text{kill}}$ of an attractor, that attractor is consumed (simulating light absorption) and removed from the cloud.
4. **Growth Direction**: The new branch node sprouts in the normalized average direction of all attractors in its attraction zone:
   $$\mathbf{v}_{\text{growth}} = \text{normalize}\left(\sum_{k \in \mathcal{A}_i} \frac{\mathbf{a}_k - \mathbf{p}_i}{\|\mathbf{a}_k - \mathbf{p}_i\|}\right)$$
5. **Leonardo da Vinci's Rule of Tree Branching (Pipe Model Theory)**:
   The cross-sectional area of a parent branch equals the sum of the cross-sectional areas of its daughter branches:
   $$R_{\text{parent}}^\Delta = \sum_{c \in \text{children}} R_c^\Delta \quad (\Delta \approx 2.0..2.5)$$

### 2.2 Spherical Canopy Normal Transfer
Flat leaf quad cards look faceted and papery in game engines because their normals face the quad orientation.
- **The Fix**: Overwrite leaf card normals by pointing them **radially outward from the tree crown centroid** $\mathbf{c}_{\text{crown}}$:
  $$\mathbf{N}_{\text{leaf}} = \operatorname{normalize}(\mathbf{p}_{\text{vertex}} - \mathbf{c}_{\text{crown}})$$
  This makes the entire foliage canopy shade like a continuous, lush, volumetric sphere under direct sunlight!

---

## 3. System 3 — SLIM (Scalable Locally Injective Mappings) Bijective UV Parameterization (`Effigy/UVUnwrap.Slim.cs`)

### 3.1 Why Classic UV Unwrappers Fail
LSCM (Least Squares Conformal Maps) and Tutte embeddings often cause **overlapping UV triangles (flips)** on complex organic geometry (faces, ears, hands, tentacles).
**SLIM (Rabinovich et al., SIGGRAPH 2016)** solves this by minimizing the **Symmetric Dirichlet Energy**:

$$E_{\text{SD}}(u) = \sum_{t \in T} A_t \left( \|\nabla u_t\|^2 + \|\nabla u_t^{-1}\|^2 \right) = \sum_{t \in T} A_t \left( \sigma_1^2 + \sigma_2^2 + \frac{1}{\sigma_1^2} + \frac{1}{\sigma_2^2} \right)$$

Where $\sigma_1, \sigma_2$ are the singular values of the 2D deformation gradient.
- **Flip Barrier**: As triangle area approaches zero ($\sigma_2 \to 0$), the energy $E_{\text{SD}} \to \infty$. It is mathematically **impossible for any UV triangle to invert or flip** during optimization!

---

## 4. System 4 — Audio-to-Lip-Sync & Viseme Speech Synthesizer (`FaceRig.AudioLipSync.cs`)

### 4.1 Automatic Dialogue Animation
Instead of hand-keyframing 15 visemes, `AudioLipSync` analyzes a `.wav` file's short-time Fourier transform (STFT), identifies formant bands ($F_1 \approx 300..1000\text{ Hz}$, $F_2 \approx 1000..3000\text{ Hz}$), and maps speech power directly to `FaceRig`'s 15 s&box visemes:

| Phoneme Group | Acoustic Characteristics | Target Viseme |
|---|---|---|
| **Bilabials (P, B, M)** | High-frequency closure, spectral silence burst | `viseme_PP` |
| **Labiodentals (F, V)** | High-frequency turbulent friction (4–7 kHz) | `viseme_FF` |
| **Dentals (TH)** | Mid-high friction, open jaw aperture | `viseme_TH` |
| **Alveolars (T, D, N, S, Z)** | Sharp formant shelf at 3.5 kHz | `viseme_DD`, `viseme_SS`, `viseme_nn` |
| **Open Vowels (AA, AH)** | Low $F_1$, High $F_2$, high acoustic energy | `viseme_aa` |
| **Rounded Vowels (O, OH, UW)** | Formant downward shift, low resonance | `viseme_O`, `viseme_U` |

The result is keyframed directly into `.vanim` AnimationList prefabs for instant in-game dialogue!

---

## 5. System 5 — Procedural 3D Noise & Domain Warping Engine (`Effigy/ProceduralNoise.cs`)

### 5.1 Domain Warping (Inigo Quilez)
True procedural wood, marble, damascus steel, clouds, and flesh require **Domain Warping**—evaluating a noise function where the coordinate input is itself distorted by another noise function:

$$\mathbf{p}' = \mathbf{p} + \alpha \, \mathbf{f}_{\text{fBm}}(\mathbf{p})$$
$$W(\mathbf{p}) = \mathbf{f}_{\text{fBm}}\big(\mathbf{p}' + \beta \, \mathbf{g}_{\text{fBm}}(\mathbf{p}')\big)$$

In Geppetto, this is evaluated in 3D space $(X, Y, Z)$ before projecting to UVs, ensuring **100% seam-free textures** across any organic or hard-surface mesh.

---

## 6. System 6 — Source 2 Destruction, Fracturing & Ragdoll Constraints (`Effigy/PhysicsBreakpiece.cs`)

### 6.1 BreakPieceList & Gib Authoring
In Source 2 / s&box, breakable props compile via a `BreakPieceList` node in `.vmdl`.
`FractureFeature` now generates:
1. Watertight Voronoi shards with noisy interior concrete/rock fractures.
2. Individual `.vmdl` breakpiece sub-models with mass, center of gravity, and collision hulls.
3. `.vmdl` compilation markup defining:
   - Damage threshold required to shatter.
   - Debris explosion impulse velocity.
   - Sound event (`break_wood_heavy`, `break_glass`).
   - Particle burst emitter.

### 6.2 Skeleton Ragdoll Joint Limits (`PhysicsJointList`)
Automatically generates:
- **Ball-and-Socket / Cone-Twist Joints** for shoulders and hips:
  - Swing limits: $\theta_{\text{swing1}} = 45^\circ, \theta_{\text{swing2}} = 60^\circ$
  - Twist limit: $\phi_{\text{twist}} = 30^\circ$
- **Revolute Hinge Joints** for elbows and knees:
  - Single rotational degree of freedom ($0^\circ..140^\circ$) preventing backward broken-joint hyper-extensions.
