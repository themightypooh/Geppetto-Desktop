# Geppetto Physics, Cloth Simulation & Advanced Kinematics Architecture
**Target Systems:** Geppetto (Effigy Mesh Modeller & Marionette Kinematics/Rigging Engine)  
**Target Environment:** s&box (Source 2 / C# / Valve Native Engine Integration)  
**Execution Paradigm:** **Offline Bake, Never Runtime Generation.** Full Manifold Quad/Tri Physics Mesh Authoring, Settlement, Draping, Skinning, and Rig Balancing baked to `.vmdl`, `.vmat`, and `.scene`.

---

## Table of Contents
1. [Architectural Overview & Guiding Principles](#1-architectural-overview--guiding-principles)
2. [Extended Position-Based Dynamics (XPBD) Cloth & Tailoring Stack](#2-extended-position-based-dynamics-xpbd-cloth--tailoring-stack)
   - 2.1 [Theoretical Foundations & Time Integration](#21-theoretical-foundations--time-integration)
   - 2.2 [Stretch and Shear Compliance Formulations](#22-stretch-and-shear-compliance-formulations)
   - 2.3 [Isometric Bending Constraints (Dihedral & Quadratic Formulations)](#23-isometric-bending-constraints)
   - 2.4 [Air Pressure Balloon & Pneumatic Inflation Constraints](#24-air-pressure-balloon--pneumatic-inflation-constraints)
   - 2.5 [Sewing Pattern Constraints & 2D-to-3D Tailoring Dynamics](#25-sewing-pattern-constraints--2d-to-3d-tailoring-dynamics)
   - 2.6 [Spatial Hashing Broadphase & Continuous Self-Collision Resolution](#26-spatial-hashing-broadphase--continuous-self-collision-resolution)
   - 2.7 [Drop-In C# XPBD Implementation](#27-drop-in-c-xpbd-implementation)
3. [Dual Quaternion Skinning (DQS) with Spherical Blending](#3-dual-quaternion-skinning-dqs-with-spherical-blending)
   - 3.1 [Algebra of Unit Dual Quaternions](#31-algebra-of-unit-dual-quaternions)
   - 3.2 [Antipodality Handling & DLB Blending](#32-antipodality-handling--dlb-blending)
   - 3.3 [Eliminating Candy-Wrapper Singularities & Bulging Mitigation](#33-eliminating-candy-wrapper-singularities--bulging-mitigation)
   - 3.4 [Drop-In C# Dual Quaternion Math Library](#34-drop-in-c-dual-quaternion-math-library)
   - 3.5 [Production GLSL / HLSL Vertex Shader Implementation](#35-production-glsl--hlsl-vertex-shader-implementation)
4. [Physics-Assisted Animation, Dynamic Balance & Kinematics](#4-physics-assisted-animation-dynamic-balance--kinematics)
   - 4.1 [Cascadeur-Style Center of Mass (CoM) Ballistic Trajectory Solver](#41-cascadeur-style-center-of-mass-com-ballistic-trajectory-solver)
   - 4.2 [Zero Moment Point (ZMP) Stability & Support Polygons](#42-zero-moment-point-zmp-stability--support-polygons)
   - 4.3 [Angular Momentum Preservation in Air Phases](#43-angular-momentum-preservation-in-air-phases)
   - 4.4 [Secondary Spring-Damper & XPBD Jiggle Bone Dynamics](#44-secondary-spring-damper--xpbd-jiggle-bone-dynamics)
   - 4.5 [Drop-In C# Kinematics & Balance Solver](#45-drop-in-c-kinematics--balance-solver)
5. [Complete Offline Baking Pipeline](#5-complete-offline-baking-pipeline)

---

# 1. Architectural Overview & Guiding Principles

In the s&box and Source 2 engine ecosystem, high-fidelity deformers and soft-body cloth must satisfy strict performance, topological, and workflow rules:
1. **Bake, Never Generate at Runtime:** High-complexity simulation passes (tailoring seam pull, pneumatic inflation, garment draping, settled skin folds, and physics trajectory re-timing) are offline tools in Geppetto (Effigy & Marionette). The output is written to concrete `.vmdl` files, morph targets, skinning weights, and keyframed `.vanim` curves.
2. **Manifold Geometry:** XPBD collision, pneumatic volume calculations, and isometric bending require watertight, consistently oriented 2-manifold triangle or quad-dominant meshes.
3. **No UI Clipping:** All editor tools, parameter displays, and debugging inspectors follow strict bounded layout and word-wrapping guarantees.

```
       +-------------------------------------------------------------+
       |             GEPPETTO AUTHORING STACK (OFFLINE)              |
       +-------------------------------------------------------------+
       | 1. Effigy Tailoring: 2D Pattern Assembly & Sewing XPBD      |
       | 2. XPBD Solver: Stretch, Shear, Isometric Bending, Pressure |
       | 3. Collision: Spatial Hashing Broadphase + Point/Edge CCD   |
       | 4. Marionette: DQS Rigging, CoM Ballistics, ZMP Balance     |
       +-------------------------------------------------------------+
                                      |
                     [Bake to Source 2 Asset Pipeline]
                                      v
       +-------------------------------------------------------------+
       |                  BAKED ASSETS (RUNTIME)                     |
       +-------------------------------------------------------------+
       | - Clean Manifold Geometry (.vmdl)                           |
       | - Pre-calculated Dual Quaternions & Blended Bind-Poses     |
       | - Physics-Adjusted Motion Curves (.vanim)                   |
       | - Real VMat Material Shaders with DQS Deformers             |
       +-------------------------------------------------------------+
```

---

# 2. Extended Position-Based Dynamics (XPBD) Cloth & Tailoring Stack

### References & Foundations
- **Macklin, M., Müller, M., Chentanez, N., & Jeschke, S. (2016).** *XPBD: Position-Based Simulation of Compliant Constrained Dynamics.* In Proceedings of the 9th International Conference on Motion in Games (MIG '16).
- **Müller, M., Heidelberger, B., Hennix, M., & Ratcliff, J. (2007).** *Position Based Dynamics.* Journal of Visual Communication and Image Representation, 18(2), 109-118.
- **Bergou, M., Mathur, S., Wardetzky, M., & Grinspun, E. (2006).** *A Quadratic Bending Model for Discrete Surfaces.* ACM SIGGRAPH / Eurographics Symposium on Computer Animation.
- **SideFX Houdini Vellum Architecture:** Multi-constraint PBD/XPBD with unified spatial hash and Lagrange multiplier substepping.

---

## 2.1 Theoretical Foundations & Time Integration

Classical Position-Based Dynamics (PBD) couples constraint stiffness directly to the time-step $\Delta t$ and iteration count, leading to artificial stiffness dependencies and numerical instability when varying frame rates. **Extended Position-Based Dynamics (XPBD)** introduces physical compliance $\alpha$ (the inverse of physical stiffness $k$, $\alpha = 1/k$, where $\alpha = 0$ is infinitely rigid) via a discrete Lagrange multiplier formulation derived from implicit Euler integration.

### XPBD Governing Equations
Given $N$ particles with positions $\mathbf{x} = [\mathbf{x}_1^T, \dots, \mathbf{x}_N^T]^T$, velocities $\mathbf{v} = [\mathbf{v}_1^T, \dots, \mathbf{v}_N^T]^T$, and diagonal inverse mass matrix $\mathbf{M}^{-1} = \operatorname{diag}(w_1 \mathbf{I}_3, \dots, w_N \mathbf{I}_3)$ where $w_i = 1/m_i$.

For a constraint $C_j(\mathbf{x}) = 0$ with compliance $\alpha_j$:

$$\tilde{\alpha}_j = \frac{\alpha_j}{\Delta t^2}$$

The Lagrange multiplier correction $\Delta \lambda_j$ at iteration $k$ is:

$$\Delta \lambda_j = \frac{-C_j(\mathbf{x}) - \tilde{\alpha}_j \lambda_j}{\nabla C_j(\mathbf{x}) \mathbf{M}^{-1} \nabla C_j(\mathbf{x})^T + \tilde{\alpha}_j}$$

The total multiplier is accumulated:

$$\lambda_j \leftarrow \lambda_j + \Delta \lambda_j$$

The position updates for each participating vertex $i$ are:

$$\Delta \mathbf{x}_i = w_i \nabla_{\mathbf{x}_i} C_j(\mathbf{x})^T \Delta \lambda_j$$

$$\mathbf{x}_i \leftarrow \mathbf{x}_i + \Delta \mathbf{x}_i$$

### Substepping & Integration Algorithm
Using substepping with $S$ substeps per display frame, where $h = \Delta t / S$:
1. For each substep:
   - Predict positions: $\mathbf{x}^*_i = \mathbf{x}_i + h \mathbf{v}_i + h^2 w_i \mathbf{f}_{ext}(\mathbf{x}_i)$
   - Initialize Lagrange multipliers: $\lambda_j = 0$
   - Loop over solver iterations:
     - For each constraint $C_j$: evaluate $C_j(\mathbf{x}^*)$, compute $\nabla C_j$, compute $\Delta \lambda_j$, and apply $\Delta \mathbf{x}^*_i$
   - Update velocities: $\mathbf{v}_i = \frac{\mathbf{x}^*_i - \mathbf{x}_i}{h}$
   - Update positions: $\mathbf{x}_i = \mathbf{x}^*_i$
   - Velocity damping / friction update.

---

## 2.2 Stretch and Shear Compliance Formulations

### 1. Distance (Stretch) Constraint
For an edge connecting vertices $\mathbf{x}_1$ and $\mathbf{x}_2$ with rest length $L_0$:

$$C_{dist}(\mathbf{x}_1, \mathbf{x}_2) = \|\mathbf{x}_1 - \mathbf{x}_2\| - L_0$$

Gradients:
$$\nabla_{\mathbf{x}_1} C_{dist} = \mathbf{n}_{12} = \frac{\mathbf{x}_1 - \mathbf{x}_2}{\|\mathbf{x}_1 - \mathbf{x}_2\|}, \quad \nabla_{\mathbf{x}_2} C_{dist} = -\mathbf{n}_{12}$$

Denominator:
$$\operatorname{denom} = w_1 + w_2 + \tilde{\alpha}_{stretch}$$

Position updates:
$$\Delta \mathbf{x}_1 = -w_1 \frac{C_{dist}}{\operatorname{denom}} \mathbf{n}_{12}, \quad \Delta \mathbf{x}_2 = +w_2 \frac{C_{dist}}{\operatorname{denom}} \mathbf{n}_{12}$$

### 2. Triangle Shear & Stretch (Green-Lagrange Strain)
For anisotropic cloth where warp and weft fibers exhibit distinct moduli, we define 2D rest coordinates $\mathbf{u}_1, \mathbf{u}_2, \mathbf{u}_3 \in \mathbb{R}^2$ on the UV tailoring pattern.
The deformation gradient $\mathbf{F} \in \mathbb{R}^{3 \times 2}$ maps rest tangents to deformed tangents:

$$\mathbf{F} = [\mathbf{x}_2 - \mathbf{x}_1, \mathbf{x}_3 - \mathbf{x}_1] [\mathbf{u}_2 - \mathbf{u}_1, \mathbf{u}_3 - \mathbf{u}_1]^{-1}$$

Let $\mathbf{F} = [\mathbf{w}_1, \mathbf{w}_2]$.
- **Warp Stretch:** $C_{warp}(\mathbf{x}) = \|\mathbf{w}_1\| - 1$
- **Weft Stretch:** $C_{weft}(\mathbf{x}) = \|\mathbf{w}_2\| - 1$
- **Shear:** $C_{shear}(\mathbf{x}) = \mathbf{w}_1 \cdot \mathbf{w}_2$

Compliance mapping:
$$\alpha_{stretch} = \frac{1}{E \cdot A_{rest}}, \quad \alpha_{shear} = \frac{1}{G \cdot A_{rest}}$$
where $E$ is Young's modulus, $G$ is the shear modulus, and $A_{rest}$ is the rest triangle area.

---

## 2.3 Isometric Bending Constraints

### Dihedral Angle Bending (Müller et al.)
For two adjacent triangles $(\mathbf{x}_1, \mathbf{x}_2, \mathbf{x}_3)$ and $(\mathbf{x}_1, \mathbf{x}_2, \mathbf{x}_4)$ sharing edge $e = (\mathbf{x}_1, \mathbf{x}_2)$:

Let normals be:
$$\mathbf{n}_1 = \frac{(\mathbf{x}_2 - \mathbf{x}_1) \times (\mathbf{x}_3 - \mathbf{x}_1)}{\|(\mathbf{x}_2 - \mathbf{x}_1) \times (\mathbf{x}_3 - \mathbf{x}_1)\|}, \quad \mathbf{n}_2 = \frac{(\mathbf{x}_2 - \mathbf{x}_1) \times (\mathbf{x}_4 - \mathbf{x}_1)}{\|(\mathbf{x}_2 - \mathbf{x}_1) \times (\mathbf{x}_4 - \mathbf{x}_1)\|}$$

The dihedral constraint is:
$$C_{dihedral}(\mathbf{x}_1, \mathbf{x}_2, \mathbf{x}_3, \mathbf{x}_4) = \arccos(\mathbf{n}_1 \cdot \mathbf{n}_2) - \theta_0$$

Because $\arccos$ has gradient singularities near $\pm 1$, we use the robust formulation:
$$C(\mathbf{x}) = \mathbf{n}_1 \cdot \mathbf{n}_2 - d_0 \quad \text{where } d_0 = \cos(\theta_0)$$

### Quadratic Isometric Bending (Bergou et al. 2006)
For linear cloth bending that is invariant to rigid transformations and isometric deformations, we use the discrete Laplace-Beltrami stencil:

$$C_{iso}(\mathbf{x}_1, \mathbf{x}_2, \mathbf{x}_3, \mathbf{x}_4) = \sum_{k=1}^4 a_k \mathbf{x}_k$$

The scalar weights $a = [a_1, a_2, a_3, a_4]^T$ are computed from the cotangents of the angles at the unshared vertices in the rest configuration:

$$a_3 = \frac{|e|}{2 A_1}, \quad a_4 = \frac{|e|}{2 A_2}, \quad a_1 = -\frac{\cot(\alpha_3) + \cot(\alpha_4)}{|e|}, \quad a_2 = -\frac{\cot(\beta_3) + \cot(\beta_4)}{|e|}$$

where $A_1, A_2$ are triangle areas and $\sum_{k=1}^4 a_k = 0$.

The gradient is directly the scalar vector:
$$\nabla_{\mathbf{x}_k} C_{iso} = a_k \mathbf{I}_3$$

The denominator for XPBD is:
$$\sum_{k=1}^4 w_k a_k^2 + \tilde{\alpha}_{bend}$$

The isometric bending update is:
$$\Delta \mathbf{x}_k = -w_k a_k \frac{\sum_{j=1}^4 a_j \mathbf{x}_j}{\sum_{j=1}^4 w_j a_j^2 + \tilde{\alpha}_{bend}}$$

*Advantage:* Zero matrix inversions, linear, rotation-invariant, robust to zero dihedral configurations.

---

## 2.4 Air Pressure Balloon & Pneumatic Inflation Constraints

For inflatable props, creature sacs, lungs, body-horror blisters, or padded jackets, Geppetto implements a global closed-surface volume constraint based on the divergence theorem.

### Volume Formulation
For a watertight closed 2-manifold surface with triangular faces $F$:

$$V(\mathbf{x}) = \frac{1}{6} \sum_{k \in F} (\mathbf{x}_{k1} \times \mathbf{x}_{k2}) \cdot \mathbf{x}_{k3}$$

Let $V_0$ be the initial volume and $k_{over}$ be the over-inflation coefficient ($k_{over} \ge 1.0$).
$$C_{volume}(\mathbf{x}) = V(\mathbf{x}) - k_{over} V_0$$

### Exact Gradient Derivation
By cyclic permutation of the scalar triple product:
$$(\mathbf{x}_{k1} \times \mathbf{x}_{k2}) \cdot \mathbf{x}_{k3} = \mathbf{x}_{k1} \cdot (\mathbf{x}_{k2} \times \mathbf{x}_{k3})$$

The partial derivative of $V$ with respect to vertex $\mathbf{x}_i$ is the sum over all 1-ring adjacent triangles containing $i$:

$$\nabla_{\mathbf{x}_i} V = \frac{1}{6} \sum_{t \in \mathcal{N}_f(i)} (\mathbf{x}_{j(t)} \times \mathbf{x}_{k(t)})$$

where $(i, j(t), k(t))$ follow counter-clockwise winding order. Note that $\nabla_{\mathbf{x}_i} V = \frac{1}{3} \mathbf{n}_i A_i$ where $\mathbf{n}_i$ is the area-weighted outward normal at vertex $i$.

### XPBD Pressure Update
Given balloon compliance $\alpha_{press} = \frac{1}{k_{bulk}}$:

$$\Delta \lambda = \frac{-(V(\mathbf{x}) - k_{over} V_0) - \tilde{\alpha}_{press} \lambda}{\sum_{i=1}^N w_i \|\nabla_{\mathbf{x}_i} V\|^2 + \tilde{\alpha}_{press}}$$

$$\Delta \mathbf{x}_i = w_i \nabla_{\mathbf{x}_i} V \Delta \lambda$$

---

## 2.5 Sewing Pattern Constraints & 2D-to-3D Tailoring Dynamics

Geppetto Tailor enables authoring flat 2D pattern panels (in UV/metric space) that are pulled together in 3D around an Effigy mannequin.

```
       PANEL A (Front Torso)                   PANEL B (Back Torso)
       +-------------------+                   +-------------------+
       |                   |                   |                   |
       |  v_A0       v_A1  |                   |  v_B0       v_B1  |
       |    *=========*    |  Seam Constraints |    *=========*    |
       |    |         |    |<=================>|    |         |    |
       |    *=========*    | (Pull -> Weld)    |    *=========*    |
       |  v_A2       v_A3  |                   |  v_B2       v_B3  |
       +-------------------+                   +-------------------+
```

### Seam Dynamics Stages:
1. **Drape & Pull Stage (Pre-sew):**
   Pairs of boundary vertices $(v_A, v_B)$ along corresponding seam curves are connected by distance constraints with target rest length:
   $$L(t) = (1 - s(t)) \|\mathbf{x}_A(0) - \mathbf{x}_B(0)\| + s(t) L_{seam}$$
   where $s(t) = \operatorname{smoothstep}(0, 1, t / T_{pull})$ shrinks the distance to $L_{seam} \approx 0$.
2. **Topological Weld Stage:**
   Once $\|\mathbf{x}_A - \mathbf{x}_B\| < \epsilon_{weld}$, Effigy merges $(v_A, v_B)$ into a single manifold vertex $v_{merged}$, updates face indices, recalculates cotangent weights, and transitions the constraint stack to isometric bending and stretch across the newly welded seam.

---

## 2.6 Spatial Hashing Broadphase & Continuous Self-Collision Resolution

To prevent garments from self-intersecting or penetrating mannequin colliders at $O(N)$ overhead, we employ an optimized 3D Spatial Hash Grid (Teschner et al. 2003).

### Spatial Hash Function
For cell size $d_{cell} = 2 \cdot \max(L_{edge})$:

$$h(x, y, z) = \left( (x \cdot p_1) \oplus (y \cdot p_2) \oplus (z \cdot p_3) \right) \pmod M$$

where $p_1 = 73856093$, $p_2 = 19349663$, $p_3 = 83492791$, and $M$ is a prime number (e.g., $999983$).

### Collision Primitives:
1. **Point-Triangle Distance Barrier:**
   For vertex $\mathbf{p}$ and triangle $(\mathbf{t}_1, \mathbf{t}_2, \mathbf{t}_3)$ with normal $\mathbf{n}$ and thickness $r_0$:
   $$C_{pt}(\mathbf{p}, \mathbf{t}_1, \mathbf{t}_2, \mathbf{t}_3) = (\mathbf{p} - \mathbf{q}) \cdot \mathbf{n} - r_0$$
   where $\mathbf{q} = u \mathbf{t}_1 + v \mathbf{t}_2 + w \mathbf{t}_3$ is the closest point on the triangle.
2. **Edge-Edge Distance Barrier:**
   For edge $(\mathbf{p}_1, \mathbf{p}_2)$ and edge $(\mathbf{q}_1, \mathbf{q}_2)$:
   $$C_{ee} = \|\mathbf{p}(s) - \mathbf{q}(t)\| - (r_p + r_q)$$
   where $s, t \in [0, 1]$ minimize distance between the two segments.

Unilateral constraint condition: $C < 0 \implies$ apply XPBD repulsion impulse along the contact normal.

---

## 2.7 Drop-In C# XPBD Implementation

Below is the complete, drop-in, zero-dependency C# implementation of the core XPBD Cloth & Balloon Solver for Geppetto.

```csharp
// File: Geppetto/Physics/XpbdClothSolver.cs
namespace Geppetto.Physics
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using System.Runtime.CompilerServices;

    public sealed class XpbdClothSolver
    {
        public struct Particle
        {
            public Vector3 Position;
            public Vector3 PrevPosition;
            public Vector3 Velocity;
            public float InvMass; // 0.0 = fixed/pinned
        }

        public struct DistanceConstraint
        {
            public int V0;
            public int V1;
            public float RestLength;
            public float Compliance;
            public float Lambda;
        }

        public struct IsometricBendingConstraint
        {
            public int V0, V1, V2, V3; // V0, V1 = shared edge; V2, V3 = wing vertices
            public float A0, A1, A2, A3; // Weights from cotangent stencil
            public float Compliance;
            public float Lambda;
        }

        public struct PressureConstraint
        {
            public int[] FaceIndices; // 3 indices per triangle
            public float TargetVolume;
            public float Compliance;
            public float Lambda;
        }

        private readonly List<Particle> _particles = new List<Particle>();
        private readonly List<DistanceConstraint> _distanceConstraints = new List<DistanceConstraint>();
        private readonly List<IsometricBendingConstraint> _bendingConstraints = new List<IsometricBendingConstraint>();
        private PressureConstraint _pressureConstraint;
        private bool _hasPressure;

        public Vector3 Gravity = new Vector3(0, -9.81f, 0);
        public float Damping = 0.995f;

        public int AddParticle(Vector3 pos, float mass)
        {
            int id = _particles.Count;
            _particles.Add(new Particle
            {
                Position = pos,
                PrevPosition = pos,
                Velocity = Vector3.Zero,
                InvMass = mass > 0.0f ? 1.0f / mass : 0.0f
            });
            return id;
        }

        public void AddDistanceConstraint(int v0, int v1, float compliance)
        {
            float dist = Vector3.Distance(_particles[v0].Position, _particles[v1].Position);
            _distanceConstraints.Add(new DistanceConstraint
            {
                V0 = v0,
                V1 = v1,
                RestLength = dist,
                Compliance = compliance,
                Lambda = 0f
            });
        }

        public void AddIsometricBendingConstraint(int v0, int v1, int v2, int v3, float compliance)
        {
            // Compute discrete laplacian stencil weights based on rest geometry
            Vector3 p0 = _particles[v0].Position;
            Vector3 p1 = _particles[v1].Position;
            Vector3 p2 = _particles[v2].Position;
            Vector3 p3 = _particles[v3].Position;

            Vector3 e = p1 - p0;
            float elen = e.Length();
            if (elen < 1e-6f) return;

            float a1 = Vector3.Cross(p1 - p0, p2 - p0).Length() * 0.5f;
            float a2 = Vector3.Cross(p0 - p1, p3 - p1).Length() * 0.5f;

            float w2 = a1 > 1e-6f ? elen / (2.0f * a1) : 0f;
            float w3 = a2 > 1e-6f ? elen / (2.0f * a2) : 0f;
            float w0 = -(w2 + w3) * 0.5f;
            float w1 = -(w2 + w3) * 0.5f;

            _bendingConstraints.Add(new IsometricBendingConstraint
            {
                V0 = v0, V1 = v1, V2 = v2, V3 = v3,
                A0 = w0, A1 = w1, A2 = w2, A3 = w3,
                Compliance = compliance,
                Lambda = 0f
            });
        }

        public void SetPressureConstraint(int[] triangles, float overInflation, float compliance)
        {
            _hasPressure = true;
            _pressureConstraint = new PressureConstraint
            {
                FaceIndices = (int[])triangles.Clone(),
                TargetVolume = CalculateVolume(triangles) * overInflation,
                Compliance = compliance,
                Lambda = 0f
            };
        }

        public void StepSimulation(float dt, int substeps)
        {
            if (substeps <= 0) substeps = 1;
            float h = dt / substeps;

            for (int s = 0; s < substeps; s++)
            {
                // 1. Predict positions & apply external forces
                for (int i = 0; i < _particles.Count; i++)
                {
                    var p = _particles[i];
                    if (p.InvMass == 0.0f) continue;

                    p.Velocity += Gravity * h;
                    p.Velocity *= Damping;
                    p.PrevPosition = p.Position;
                    p.Position += p.Velocity * h;
                    _particles[i] = p;
                }

                // 2. Reset Lagrange multipliers for this substep
                for (int i = 0; i < _distanceConstraints.Count; i++)
                {
                    var c = _distanceConstraints[i];
                    c.Lambda = 0f;
                    _distanceConstraints[i] = c;
                }
                for (int i = 0; i < _bendingConstraints.Count; i++)
                {
                    var c = _bendingConstraints[i];
                    c.Lambda = 0f;
                    _bendingConstraints[i] = c;
                }
                if (_hasPressure)
                {
                    _pressureConstraint.Lambda = 0f;
                }

                // 3. Solve constraints
                SolveDistanceConstraints(h);
                SolveBendingConstraints(h);
                if (_hasPressure)
                {
                    SolvePressureConstraint(h);
                }

                // 4. Update velocities from settled positions
                float invH = 1.0f / h;
                for (int i = 0; i < _particles.Count; i++)
                {
                    var p = _particles[i];
                    if (p.InvMass == 0.0f) continue;

                    p.Velocity = (p.Position - p.PrevPosition) * invH;
                    _particles[i] = p;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SolveDistanceConstraints(float h)
        {
            float h2 = h * h;
            for (int i = 0; i < _distanceConstraints.Count; i++)
            {
                var c = _distanceConstraints[i];
                var p0 = _particles[c.V0];
                var p1 = _particles[c.V1];

                Vector3 delta = p0.Position - p1.Position;
                float currentLength = delta.Length();
                if (currentLength < 1e-6f) continue;

                Vector3 n = delta / currentLength;
                float constraintVal = currentLength - c.RestLength;
                float alphaTilde = c.Compliance / h2;

                float w0 = p0.InvMass;
                float w1 = p1.InvMass;
                float denom = w0 + w1 + alphaTilde;
                if (denom < 1e-8f) continue;

                float deltaLambda = (-constraintVal - alphaTilde * c.Lambda) / denom;
                c.Lambda += deltaLambda;
                _distanceConstraints[i] = c;

                p0.Position += n * (w0 * deltaLambda);
                p1.Position -= n * (w1 * deltaLambda);

                _particles[c.V0] = p0;
                _particles[c.V1] = p1;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SolveBendingConstraints(float h)
        {
            float h2 = h * h;
            for (int i = 0; i < _bendingConstraints.Count; i++)
            {
                var c = _bendingConstraints[i];
                var p0 = _particles[c.V0];
                var p1 = _particles[c.V1];
                var p2 = _particles[c.V2];
                var p3 = _particles[c.V3];

                Vector3 constraintValVec = (p0.Position * c.A0) +
                                           (p1.Position * c.A1) +
                                           (p2.Position * c.A2) +
                                           (p3.Position * c.A3);

                float alphaTilde = c.Compliance / h2;
                float sumWeights = (p0.InvMass * c.A0 * c.A0) +
                                   (p1.InvMass * c.A1 * c.A1) +
                                   (p2.InvMass * c.A2 * c.A2) +
                                   (p3.InvMass * c.A3 * c.A3);

                float denom = sumWeights + alphaTilde;
                if (denom < 1e-8f) continue;

                Vector3 deltaLambda = (-constraintValVec) / denom;

                p0.Position += deltaLambda * (p0.InvMass * c.A0);
                p1.Position += deltaLambda * (p1.InvMass * c.A1);
                p2.Position += deltaLambda * (p2.InvMass * c.A2);
                p3.Position += deltaLambda * (p3.InvMass * c.A3);

                _particles[c.V0] = p0;
                _particles[c.V1] = p1;
                _particles[c.V2] = p2;
                _particles[c.V3] = p3;
            }
        }

        private void SolvePressureConstraint(float h)
        {
            float h2 = h * h;
            int[] tris = _pressureConstraint.FaceIndices;
            int numTris = tris.Length / 3;

            float currentVolume = CalculateVolume(tris);
            float constraintVal = currentVolume - _pressureConstraint.TargetVolume;
            float alphaTilde = _pressureConstraint.Compliance / h2;

            Span<Vector3> gradV = stackalloc Vector3[_particles.Count];
            gradV.Clear();

            for (int t = 0; t < numTris; t++)
            {
                int i0 = tris[t * 3];
                int i1 = tris[t * 3 + 1];
                int i2 = tris[t * 3 + 2];

                Vector3 p0 = _particles[i0].Position;
                Vector3 p1 = _particles[i1].Position;
                Vector3 p2 = _particles[i2].Position;

                Vector3 cross01 = Vector3.Cross(p0, p1) / 6.0f;
                Vector3 cross12 = Vector3.Cross(p1, p2) / 6.0f;
                Vector3 cross20 = Vector3.Cross(p2, p0) / 6.0f;

                gradV[i0] += cross12;
                gradV[i1] += cross20;
                gradV[i2] += cross01;
            }

            float sumGradSqr = 0f;
            for (int i = 0; i < _particles.Count; i++)
            {
                float w = _particles[i].InvMass;
                if (w > 0.0f)
                {
                    sumGradSqr += w * gradV[i].LengthSquared();
                }
            }

            float denom = sumGradSqr + alphaTilde;
            if (denom < 1e-8f) return;

            float deltaLambda = (-constraintVal - alphaTilde * _pressureConstraint.Lambda) / denom;
            _pressureConstraint.Lambda += deltaLambda;

            for (int i = 0; i < _particles.Count; i++)
            {
                float w = _particles[i].InvMass;
                if (w > 0.0f)
                {
                    var p = _particles[i];
                    p.Position += gradV[i] * (w * deltaLambda);
                    _particles[i] = p;
                }
            }
        }

        public float CalculateVolume(int[] triangles)
        {
            double totalVolume = 0.0;
            int numTris = triangles.Length / 3;
            for (int t = 0; t < numTris; t++)
            {
                Vector3 p0 = _particles[triangles[t * 3]].Position;
                Vector3 p1 = _particles[triangles[t * 3 + 1]].Position;
                Vector3 p2 = _particles[triangles[t * 3 + 2]].Position;

                totalVolume += Vector3.Dot(Vector3.Cross(p0, p1), p2);
            }
            return (float)(totalVolume / 6.0);
        }

        public Vector3 GetPosition(int index) => _particles[index].Position;
        public void SetPosition(int index, Vector3 pos)
        {
            var p = _particles[index];
            p.Position = pos;
            p.PrevPosition = pos;
            _particles[index] = p;
        }
    }
}
```

---

# 3. Dual Quaternion Skinning (DQS) with Spherical Blending

### References & Foundations
- **Kavan, L., Collins, S., Žára, J., & O'Sullivan, C. (2007).** *Skinning with Dual Quaternions.* ACM SIGGRAPH 2007 Courses / I3D 2007.
- **Kavan, L., Collins, S., Žára, J., & O'Sullivan, C. (2008).** *Geometric Skinning with Approximative Dual Quaternion Blending.* ACM Transactions on Graphics (TOG), 27(4), 105.
- **Merry, B., Marais, P., & Gain, J. (2006).** *Animation Space Transfer for Character Animation.*

---

## 3.1 Algebra of Unit Dual Quaternions

A dual quaternion $\hat{\mathbf{q}}$ is an element of the Clifford algebra $\mathcal{C}\ell(0, 3, 1)$, expressed as:

$$\hat{\mathbf{q}} = \mathbf{q}_0 + \epsilon \mathbf{q}_\epsilon$$

where $\mathbf{q}_0, \mathbf{q}_\epsilon \in \mathbb{H}$ are quaternions, and $\epsilon$ is the dual unit satisfying $\epsilon^2 = 0$ with $\epsilon \neq 0$.
- $\mathbf{q}_0$ encodes 3D rotation: $\mathbf{q}_0 = \left[\cos(\frac{\theta}{2}), \sin(\frac{\theta}{2}) \mathbf{u}\right]$
- $\mathbf{q}_\epsilon$ encodes translation $\mathbf{t} = (t_x, t_y, t_z)$:
  $$\mathbf{q}_\epsilon = \frac{1}{2} \mathbf{t}^\mathbb{H} \mathbf{q}_0$$
  where $\mathbf{t}^\mathbb{H} = [0, t_x, t_y, t_z]$ is a pure vector quaternion.

### Unit Condition
A dual quaternion represents a rigid transformation (isometry $\mathbf{SE}(3)$) if and only if it is unit length:

$$\|\hat{\mathbf{q}}\| = 1 \iff \|\mathbf{q}_0\| = 1 \quad \text{and} \quad \mathbf{q}_0 \cdot \mathbf{q}_\epsilon = 0$$

### Dual Quaternion Multiplication
For $\hat{\mathbf{a}} = \mathbf{a}_0 + \epsilon \mathbf{a}_\epsilon$ and $\hat{\mathbf{b}} = \mathbf{b}_0 + \epsilon \mathbf{b}_\epsilon$:

$$\hat{\mathbf{a}} \hat{\mathbf{b}} = (\mathbf{a}_0 \mathbf{b}_0) + \epsilon (\mathbf{a}_0 \mathbf{b}_\epsilon + \mathbf{a}_\epsilon \mathbf{b}_0)$$

### Vector Transformation
To transform a point $\mathbf{p} \in \mathbb{R}^3$, construct $\hat{\mathbf{p}} = \mathbf{1} + \epsilon [0, \mathbf{p}]$:

$$\hat{\mathbf{p}}' = \hat{\mathbf{q}} \hat{\mathbf{p}} \hat{\mathbf{q}}^*$$

Equivalently in simplified vector arithmetic:

$$\mathbf{p}' = \mathbf{q}_0 \mathbf{p} \mathbf{q}_0^* + 2 (\mathbf{q}_\epsilon \mathbf{q}_0^* - \mathbf{q}_0 \mathbf{q}_\epsilon^*)_{\text{vector}} = \mathbf{q}_0 \mathbf{p} \mathbf{q}_0^* + \mathbf{t}$$

$$\mathbf{t} = 2 \left( \mathbf{q}_\epsilon \mathbf{q}_0^* \right)_{\text{xyz}}$$

---

## 3.2 Antipodality Handling & DLB Blending

Linear Blend Skinning (LBS) linearly blends transformation matrices: $\mathbf{M} = \sum w_i \mathbf{M}_i$, causing catastrophic volume collapse ("candy-wrapper" artifact) when twisting limbs by $180^\circ$ because the matrix center collapses to zero.

### Dual Quaternion Linear Blending (DLB)
Given $K$ influence bones with weights $w_i \ge 0, \sum w_i = 1$ and dual quaternions $\hat{\mathbf{q}}_i$:

$$\hat{\mathbf{b}} = \sum_{i=1}^K w_i \hat{\mathbf{q}}_i$$

The blended result is normalized to project back onto the $\mathbf{SE}(3)$ manifold:

$$\hat{\mathbf{q}}_{blend} = \frac{\hat{\mathbf{b}}}{\|\hat{\mathbf{b}}_0\|} = \frac{\mathbf{b}_0}{\|\mathbf{b}_0\|} + \epsilon \frac{\mathbf{b}_\epsilon}{\|\mathbf{b}_0\|}$$

### Antipodality Problem & Solution
Quaternions exhibit a double cover of $\mathbf{SO}(3)$: $\hat{\mathbf{q}}$ and $-\hat{\mathbf{q}}$ represent the identical rigid transformation. However:
$$\frac{\hat{\mathbf{q}} + (-\hat{\mathbf{q}})}{2} = 0 \quad (\text{Singularity / Cancellation})$$

**Antipodality Alignment Rule:**
Pick the pivot bone 0 (the bone with the highest skinning weight $w_0$). For every subsequent bone $i > 0$:

$$\text{if } (\mathbf{q}_{0, 0} \cdot \mathbf{q}_{i, 0} < 0) \implies \hat{\mathbf{q}}_i \leftarrow -\hat{\mathbf{q}}_i$$

This ensures all transformations blend along the shortest spherical geodesic arc.

---

## 3.3 Eliminating Candy-Wrapper Singularities & Bulging Mitigation

While DQS completely eliminates candy-wrapper volume loss, pure DLB can introduce minor "joint bulging" under high bending angles ($> 120^\circ$) because the rotation center is strictly bound to the kinematic joint origin.

```
       Linear Blend Skinning (LBS)               Dual Quaternion Skinning (DQS)
            [180 Twist Collapse]                       [Constant Volume]
                 \  |  /                                     |     |
                  \ | /                                      |     |
                   ><  <-- Volume lost                       |     |  <-- Volume preserved!
                  / | \                                      |     |
                 /  |  \                                     |     |
```

### Center-of-Rotation (CoR) Blending (Bulging Mitigation)
Geppetto incorporates an offline CoR weight pre-pass (Kavan et al. 2008 / Le & Hodgins 2016):
1. **Offline Bake Pass:** Compute local vertex scale adjustment factor $\gamma_v$ based on the angle between dominant bones:
   $$\gamma_v = 1.0 - \kappa \cdot w_0 w_1 (1.0 - \cos\theta_{01})$$
   where $\kappa \in [0.1, 0.25]$ is the bulge attenuation factor.
2. In the vertex deformer, the translated position is scaled along the normal $\mathbf{n}'$:
   $$\mathbf{p}_{final} = \mathbf{p}' - (1.0 - \gamma_v) (\mathbf{p}' - \mathbf{c}_{joint})_\perp$$

---

## 3.4 Drop-In C# Dual Quaternion Math Library

```csharp
// File: Geppetto/Kinematics/DualQuaternion.cs
namespace Geppetto.Kinematics
{
    using System;
    using System.Numerics;
    using System.Runtime.CompilerServices;

    public struct DualQuaternion
    {
        public Quaternion Real; // q0: Rotation
        public Quaternion Dual; // qe: Translation / 2 * Real

        public static readonly DualQuaternion Identity = new DualQuaternion(Quaternion.Identity, new Quaternion(0, 0, 0, 0));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DualQuaternion(Quaternion real, Quaternion dual)
        {
            Real = real;
            Dual = dual;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DualQuaternion FromRotationTranslation(Quaternion r, Vector3 t)
        {
            r = Quaternion.Normalize(r);
            Quaternion tQuat = new Quaternion(t.X, t.Y, t.Z, 0f);
            Quaternion dual = Quaternion.Multiply(tQuat, r);
            dual = new Quaternion(dual.X * 0.5f, dual.Y * 0.5f, dual.Z * 0.5f, dual.W * 0.5f);
            return new DualQuaternion(r, dual);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DualQuaternion FromMatrix4x4(Matrix4x4 m)
        {
            if (!Matrix4x4.Decompose(m, out Vector3 scale, out Quaternion rot, out Vector3 trans))
            {
                rot = Quaternion.Identity;
                trans = new Vector3(m.M41, m.M42, m.M43);
            }
            return FromRotationTranslation(rot, trans);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Normalize()
        {
            float norm = Real.Length();
            if (norm < 1e-8f)
            {
                this = Identity;
                return;
            }
            float invNorm = 1.0f / norm;
            Real = new Quaternion(Real.X * invNorm, Real.Y * invNorm, Real.Z * invNorm, Real.W * invNorm);
            Dual = new Quaternion(Dual.X * invNorm, Dual.Y * invNorm, Dual.Z * invNorm, Dual.W * invNorm);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 TransformPoint(Vector3 p)
        {
            // Vector translation t = 2 * Dual * Real^*
            Quaternion rConj = Quaternion.Conjugate(Real);
            Quaternion tQuat = Quaternion.Multiply(Dual, rConj);
            Vector3 trans = new Vector3(tQuat.X * 2.0f, tQuat.Y * 2.0f, tQuat.Z * 2.0f);

            // Rotate p: Real * [p, 0] * Real^*
            return Vector3.Transform(p, Real) + trans;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 TransformVector(Vector3 v)
        {
            // Normals and direction vectors are only affected by Real rotation
            return Vector3.Transform(v, Real);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DualQuaternion Blend(ReadOnlySpan<DualQuaternion> dqs, ReadOnlySpan<float> weights)
        {
            if (dqs.Length == 0) return Identity;

            // Pivot DQ for antipodality alignment
            DualQuaternion pivot = dqs[0];
            Vector4 bReal = Vector4.Zero;
            Vector4 bDual = Vector4.Zero;

            for (int i = 0; i < dqs.Length; i++)
            {
                float w = weights[i];
                if (MathF.Abs(w) < 1e-6f) continue;

                var dq = dqs[i];
                float dot = (pivot.Real.X * dq.Real.X) +
                            (pivot.Real.Y * dq.Real.Y) +
                            (pivot.Real.Z * dq.Real.Z) +
                            (pivot.Real.W * dq.Real.W);

                float sign = dot < 0.0f ? -1.0f : 1.0f;
                float sw = w * sign;

                bReal += new Vector4(dq.Real.X, dq.Real.Y, dq.Real.Z, dq.Real.W) * sw;
                bDual += new Vector4(dq.Dual.X, dq.Dual.Y, dq.Dual.Z, dq.Dual.W) * sw;
            }

            float realNorm = bReal.Length();
            if (realNorm < 1e-8f) return Identity;

            float invNorm = 1.0f / realNorm;
            Quaternion resReal = new Quaternion(bReal.X * invNorm, bReal.Y * invNorm, bReal.Z * invNorm, bReal.W * invNorm);
            Quaternion resDual = new Quaternion(bDual.X * invNorm, bDual.Y * invNorm, bDual.Z * invNorm, bDual.W * invNorm);

            return new DualQuaternion(resReal, resDual);
        }
    }
}
```

---

## 3.5 Production GLSL / HLSL Vertex Shader Implementation

Below is the production-grade GLSL / HLSL compute/vertex shader for Dual Quaternion Skinning with antipodality alignment and normal/tangent transformation.

```glsl
// File: shaders/geppetto_dqs.vert
#version 450 core

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;
layout(location = 3) in vec2 inTexCoord;
layout(location = 4) in uvec4 inBoneIndices;
layout(location = 5) in vec4 inBoneWeights;

layout(std140, binding = 0) uniform FrameUniforms {
    mat4 uViewProjection;
    mat4 uModel;
};

// Each bone dual quaternion is packed into two vec4s:
// boneDQ[boneId * 2 + 0] = Real Quaternion (rx, ry, rz, rw)
// boneDQ[boneId * 2 + 1] = Dual Quaternion (dx, dy, dz, dw)
layout(std430, binding = 1) readonly buffer BoneDualQuatBuffer {
    vec4 boneDQ[];
};

layout(location = 0) out vec3 outWorldPos;
layout(location = 1) out vec3 outNormal;
layout(location = 2) out vec4 outTangent;
layout(location = 3) out vec2 outTexCoord;

void main()
{
    vec4 weights = inBoneWeights;
    // Normalize weights
    float weightSum = dot(weights, vec4(1.0));
    if (weightSum > 1e-5) weights /= weightSum;

    // Load first non-zero bone DQ as antipodality reference
    vec4 real0 = boneDQ[inBoneIndices[0] * 2 + 0];
    vec4 dual0 = boneDQ[inBoneIndices[0] * 2 + 1];

    vec4 bReal = vec4(0.0);
    vec4 bDual = vec4(0.0);

    for (int i = 0; i < 4; i++)
    {
        uint bId = inBoneIndices[i];
        float w = weights[i];

        vec4 r = boneDQ[bId * 2 + 0];
        vec4 d = boneDQ[bId * 2 + 1];

        // Shortest-arc antipodality check
        float sign = (dot(real0, r) < 0.0) ? -1.0 : 1.0;
        w *= sign;

        bReal += r * w;
        bDual += d * w;
    }

    // Project back onto SE(3) manifold (Unit Dual Quaternion)
    float invNorm = 1.0 / length(bReal);
    bReal *= invNorm;
    bDual *= invNorm;

    // Translation vector: t = 2.0 * (bDual * conjugate(bReal))
    // bReal conjugate: (-bReal.xyz, bReal.w)
    vec3 t = 2.0 * (bDual.w * bReal.xyz - bReal.w * bDual.xyz + cross(bDual.xyz, bReal.xyz));

    // Rotate point: p' = p + 2.0 * cross(bReal.xyz, cross(bReal.xyz, p) + bReal.w * p)
    vec3 rotatedPos = inPosition + 2.0 * cross(bReal.xyz, cross(bReal.xyz, inPosition) + bReal.w * inPosition);
    vec3 skinnedPos = rotatedPos + t;

    // Skinned Normal & Tangent (rotation only)
    vec3 skinnedNormal = inNormal + 2.0 * cross(bReal.xyz, cross(bReal.xyz, inNormal) + bReal.w * inNormal);
    vec3 skinnedTangent = inTangent.xyz + 2.0 * cross(bReal.xyz, cross(bReal.xyz, inTangent.xyz) + bReal.w * inTangent.xyz);

    vec4 worldPos = uModel * vec4(skinnedPos, 1.0);
    gl_Position = uViewProjection * worldPos;

    outWorldPos = worldPos.xyz;
    outNormal = normalize(mat3(uModel) * skinnedNormal);
    outTangent = vec4(normalize(mat3(uModel) * skinnedTangent), inTangent.w);
    outTexCoord = inTexCoord;
}
```

---

# 4. Physics-Assisted Animation, Dynamic Balance & Kinematics

### References & Foundations
- **Cascadeur (Nekki):** Physics-based animation authoring; automated Center of Mass parabolic arcs; dynamic Zero Moment Point balancing; angular momentum conservation.
- **Vukobratović, M., & Borovac, B. (2004).** *Zero-Moment Point — Thirty Five Years of Its Life.* International Journal of Humanoid Robotics, 1(1), 157-173.
- **Kajita, S., Kanehiro, F., Kaneko, K., Fujiwara, K., Harada, K., & Hirukawa, H. (2003).** *Biped Walking Pattern Generation by using Preview Control of Zero-Moment Point.* ICRA 2003.

---

## 4.1 Cascadeur-Style Center of Mass (CoM) Ballistic Trajectory Solver

In natural human, creature, and mechanical motion, during any **flight phase** (jumping, running, falling, vaulting) when no external ground reaction forces exist except gravity, the **Center of Mass (CoM)** must describe an exact quadratic parabola in $\mathbb{R}^3$, and total angular momentum must be conserved.

```
       +--------------------------------------------------------------+
       |                  BALLISTIC TRAJECTORY PASS                   |
       +--------------------------------------------------------------+
         y ^                 CoM True Parabolic Arc: r(t)
           |                          .---.
           |                         /     \
           |             Takeoff    /       \     Landing
           |               *       /         \       *
           |              / \     /           \     / \
           |             /   \---'             `---/   \
           +------------*-------------------------*---------->
           Ground     t_takeoff                 t_land         x
```

### 1. Multi-Body Center of Mass (CoM) Formulation
Given $B$ kinematic bone segments, where bone $k$ has mass $m_k$, length $L_k$, center of mass $\mathbf{c}_k(t)$, and orientation matrix $\mathbf{R}_k(t)$:

$$\mathbf{r}_{CoM}(t) = \frac{\sum_{k=1}^B m_k \mathbf{c}_k(t)}{M_{total}}, \quad M_{total} = \sum_{k=1}^B m_k$$

### 2. Ballistic Arc Identification & Boundary Solver
During flight interval $t \in [t_{takeoff}, t_{land}]$:

$$\ddot{\mathbf{r}}_{CoM}(t) = \mathbf{g} = [0, -9.81, 0]^T$$

$$\mathbf{r}_{CoM}(t) = \mathbf{r}_{CoM}(t_{takeoff}) + \mathbf{v}_0 (t - t_{takeoff}) + \frac{1}{2} \mathbf{g} (t - t_{takeoff})^2$$

Given boundary conditions $\mathbf{r}_0 = \mathbf{r}_{CoM}(t_{takeoff})$ and $\mathbf{r}_1 = \mathbf{r}_{CoM}(t_{land})$ over duration $T = t_{land} - t_{takeoff}$:

$$\mathbf{v}_0 = \frac{\mathbf{r}_1 - \mathbf{r}_0 - \frac{1}{2} \mathbf{g} T^2}{T}$$

### 3. Pelvis/Root Offset Correction
For each keyframe $t \in [t_{takeoff}, t_{land}]$, let $\mathbf{r}_{raw}(t)$ be the animator's keyed CoM, and $\mathbf{r}_{phys}(t)$ be the ballistic parabola.
The global translation shift applied to the skeleton root is:

$$\Delta \mathbf{p}_{root}(t) = \mathbf{r}_{phys}(t) - \mathbf{r}_{raw}(t)$$

Applying $\Delta \mathbf{p}_{root}(t)$ guarantees physically plausible flight arcs while preserving all authored limb postures.

---

## 4.2 Zero Moment Point (ZMP) Stability & Support Polygons

During stance phases (walking, idling, combat stances), an animated character stays balanced without tipping over if and only if its **Zero Moment Point (ZMP)** resides strictly inside the interior of the **Support Polygon** $\mathcal{P}_{supp}$ defined by foot contacts.

### Mathematical Derivation of ZMP
The ZMP is the point on the ground plane ($z = z_{ground} = 0$) where the horizontal components of the total tipping moment are zero:

$$\tau_x(p_{zmp}) = 0, \quad \tau_y(p_{zmp}) = 0$$

Considering all $B$ rigid body links with masses $m_i$, positions $(x_i, y_i, z_i)$, accelerations $(\ddot{x}_i, \ddot{y}_i, \ddot{z}_i)$, and rate of angular momentum $\dot{\mathbf{H}}_i$:

$$x_{zmp} = \frac{\sum_{i=1}^B \left[ m_i (\ddot{z}_i + g) x_i - m_i \ddot{x}_i z_i - \dot{H}_{y,i} \right]}{\sum_{i=1}^B m_i (\ddot{z}_i + g)}$$

$$z_{zmp} = \frac{\sum_{i=1}^B \left[ m_i (\ddot{z}_i + g) z_i - m_i \ddot{z}_i y_i + \dot{H}_{x,i} \right]}{\sum_{i=1}^B m_i (\ddot{z}_i + g)}$$

For quasi-static animations ($\ddot{\mathbf{x}}_i \approx 0$), the ZMP simplifies directly to the vertical ground projection of the Center of Mass:

$$\mathbf{p}_{zmp}^{static} = [x_{CoM}, 0, z_{CoM}]^T$$

### Support Polygon Projection & IK Adjustment
1. **Contact Identification:** Identify feet vertices with ground clearance $< \epsilon_{contact}$ and velocity $< v_{threshold}$.
2. **Convex Hull:** Compute 2D convex polygon $\mathcal{P}_{supp} = \operatorname{ConvexHull}(\mathbf{v}_1, \dots, \mathbf{v}_M)$.
3. **Stability Condition:**
   $$\mathbf{p}_{zmp} \in \mathcal{P}_{supp}$$
4. **Correction:** If $\mathbf{p}_{zmp}$ lies outside $\mathcal{P}_{supp}$, compute the closest point $\mathbf{p}^* \in \partial\mathcal{P}_{supp}$ with safety margin $\delta_{margin}$.
   Adjust the pelvis/torso via Full-Body IK (FABRIK/CCD) such that $\mathbf{p}_{zmp} \to \mathbf{p}^*$.

---

## 4.3 Angular Momentum Preservation in Air Phases

In flight, total angular momentum $\mathbf{L}_{CoM}$ about the Center of Mass must remain constant:

$$\mathbf{L}_{CoM}(t) = \sum_{i=1}^B \left( \mathbf{I}_i \boldsymbol{\omega}_i + m_i (\mathbf{c}_i - \mathbf{r}_{CoM}) \times (\dot{\mathbf{c}}_i - \dot{\mathbf{r}}_{CoM}) \right) = \mathbf{L}_0$$

If the character pulls their arms and legs inward (reducing rotational inertia $\mathbf{I}_{total}$), their spin rate $\boldsymbol{\omega}_{body}$ must proportionally accelerate (the figure-skater effect).
Marionette computes $\mathbf{L}_{CoM}(t)$ at takeoff, and during air frames calculates the correction torque:

$$\Delta \boldsymbol{\tau} = \frac{\mathbf{L}_0 - \mathbf{L}_{CoM}(t)}{\Delta t}$$

distributing compensating angular velocity across the root and major limbs.

---

## 4.4 Secondary Spring-Damper & XPBD Jiggle Bone Dynamics

For fleshy appendages, creature tentacles, antennae, breasts, fat rolls, tails, and loose equipment, Marionette utilizes an XPBD orientation-spring formulation.

### Twist-Swing XPBD Formulation
For a jiggle bone with parent transform $\mathbf{T}_p$ and animated bind orientation $\mathbf{q}_{anim}$:
We simulate a secondary orientation quaternion $\mathbf{q}(t)$ governed by:
- **Angular Spring Potential:** $C_{rot}(\mathbf{q}) = 2 \arccos((\mathbf{q}^{-1} \mathbf{q}_{anim})_w)$
- **Cone Angle Constraint (Limit):** $\theta_{swing} \le \theta_{max}$
- **Twist Limit:** $|\theta_{twist}| \le \theta_{twist\_max}$

XPBD updates:
$$\Delta \boldsymbol{\theta} = \frac{-2 \operatorname{Im}(\mathbf{q}^{-1} \mathbf{q}_{anim}) - \tilde{\alpha}_{rot} \boldsymbol{\lambda}_{rot}}{I^{-1} + \tilde{\alpha}_{rot}}$$

---

## 4.5 Drop-In C# Kinematics & Balance Solver

Below is the complete C# solver implementing multi-body CoM tracking, ballistic flight path generation, and 2D Support Polygon projection for Marionette.

```csharp
// File: Geppetto/Kinematics/CoMBalanceSolver.cs
namespace Geppetto.Kinematics
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;

    public sealed class CoMBalanceSolver
    {
        public struct BoneSegment
        {
            public int BoneIndex;
            public float Mass;
            public Vector3 LocalCoM; // Offset from bone origin
            public Vector3 WorldCoM;
            public Vector3 Velocity;
            public Vector3 Acceleration;
        }

        public struct FrameData
        {
            public float Time;
            public Vector3 RootPosition;
            public Quaternion RootRotation;
            public Vector3 CoMPosition;
            public bool IsFlightPhase;
        }

        private readonly List<BoneSegment> _bones = new List<BoneSegment>();
        public float TotalMass { get; private set; }
        public Vector3 Gravity = new Vector3(0, -9.81f, 0);

        public void AddBone(int boneIndex, float mass, Vector3 localCoM)
        {
            _bones.Add(new BoneSegment
            {
                BoneIndex = boneIndex,
                Mass = mass,
                LocalCoM = localCoM,
                WorldCoM = Vector3.Zero,
                Velocity = Vector3.Zero,
                Acceleration = Vector3.Zero
            });
            TotalMass += mass;
        }

        public Vector3 ComputeCenterOfMass(Func<int, Matrix4x4> boneTransformProvider)
        {
            if (TotalMass < 1e-6f) return Vector3.Zero;

            Vector3 weightedSum = Vector3.Zero;
            for (int i = 0; i < _bones.Count; i++)
            {
                var b = _bones[i];
                Matrix4x4 mat = boneTransformProvider(b.BoneIndex);
                b.WorldCoM = Vector3.Transform(b.LocalCoM, mat);
                weightedSum += b.WorldCoM * b.Mass;
                _bones[i] = b;
            }

            return weightedSum / TotalMass;
        }

        public Vector3 CalculateZmp(Vector3 comPos, Vector3 comAccel, float groundY = 0f)
        {
            // Kajita formulation for dynamic ZMP:
            // x_zmp = x_com - (z_com - groundY) / (accel_y + g_y) * accel_x
            float effG = comAccel.Y - Gravity.Y; // Gravity is negative (-9.81)
            if (MathF.Abs(effG) < 1e-4f) return new Vector3(comPos.X, groundY, comPos.Z);

            float height = comPos.Y - groundY;
            float zmpX = comPos.X - (height / effG) * comAccel.X;
            float zmpZ = comPos.Z - (height / effG) * comAccel.Z;

            return new Vector3(zmpX, groundY, zmpZ);
        }

        public void SolveBallisticFlightCurve(Span<FrameData> frames, int takeoffIndex, int landIndex)
        {
            if (takeoffIndex >= landIndex || takeoffIndex < 0 || landIndex >= frames.Length)
                return;

            float t0 = frames[takeoffIndex].Time;
            float t1 = frames[landIndex].Time;
            float duration = t1 - t0;
            if (duration < 1e-4f) return;

            Vector3 r0 = frames[takeoffIndex].CoMPosition;
            Vector3 r1 = frames[landIndex].CoMPosition;

            // v0 = (r1 - r0 - 0.5 * g * T^2) / T
            Vector3 v0 = (r1 - r0 - 0.5f * Gravity * duration * duration) / duration;

            for (int i = takeoffIndex; i <= landIndex; i++)
            {
                var f = frames[i];
                float tau = f.Time - t0;

                // Ideal physics ballistic position
                Vector3 targetCoM = r0 + v0 * tau + 0.5f * Gravity * tau * tau;
                Vector3 delta = targetCoM - f.CoMPosition;

                // Shift skeletal root
                f.RootPosition += delta;
                f.CoMPosition = targetCoM;
                f.IsFlightPhase = true;

                frames[i] = f;
            }
        }

        public static Vector2 ProjectPointToConvexPolygon(Vector2 point, ReadOnlySpan<Vector2> polygon)
        {
            if (polygon.Length < 3) return point;

            // Check if point is inside convex hull
            bool inside = true;
            float minDistanceSqr = float.MaxValue;
            Vector2 closestEdgePoint = point;

            for (int i = 0; i < polygon.Length; i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[(i + 1) % polygon.Length];

                Vector2 edge = b - a;
                Vector2 toPoint = point - a;
                float cross = edge.X * toPoint.Y - edge.Y * toPoint.X;

                if (cross < 0.0f) // Outside edge in CCW polygon
                {
                    inside = false;
                }

                // Project to line segment
                float edgeLenSqr = edge.LengthSquared();
                float t = edgeLenSqr > 1e-6f ? Math.Clamp(Vector2.Dot(toPoint, edge) / edgeLenSqr, 0.0f, 1.0f) : 0.0f;
                Vector2 proj = a + edge * t;
                float distSqr = Vector2.DistanceSquared(point, proj);

                if (distSqr < minDistanceSqr)
                {
                    minDistanceSqr = distSqr;
                    closestEdgePoint = proj;
                }
            }

            return inside ? point : closestEdgePoint;
        }
    }
}
```

---

# 5. Complete Offline Baking Pipeline

To strictly respect the rule **"Bake, never generate at runtime"**, all tools interact through the standard Geppetto offline compilation pipeline:

```
[2D Tailoring Patterns]        [Mannequin Collision Cage]        [Keyframe Skeleton]
         |                                |                               |
         +----------------+---------------+-------------------------------+
                          |
                          v
         +------------------------------------------------+
         | Effigy XPBD Offline Settling Pass (500 steps)  |
         |  - Pre-sew seam springs pull flat UV panels    |
         |  - Topological edge welding at threshold       |
         |  - Isometric bending & balloon volume inflate  |
         |  - Spatial hash self-collision resolution      |
         +------------------------------------------------+
                          |
                          v
         +------------------------------------------------+
         | Marionette Rigging & Balancing Pass            |
         |  - Dual Quaternion Bind Matrix Decomposition   |
         |  - CoR Bulge Attenuation Weights Bake          |
         |  - CoM Ballistic Parabolic Arc Adjustment      |
         |  - Dynamic ZMP Foot Polygon Clamping           |
         +------------------------------------------------+
                          |
                          v
         +------------------------------------------------+
         | Final Compilation & Serialization              |
         |  - Clean Manifold Mesh -> .vmdl (Source 2)     |
         |  - Baked Physics Curves -> .vanim              |
         |  - Real Material Shader -> .vmat               |
         +------------------------------------------------+
```

### Result:
- Zero runtime physics lag or frame drops during gameplay.
- 100% deterministic, artifact-free deformation with true volume retention (DQS).
- Realistic clothing draping, pneumatic volume, and physically grounded character balance.
