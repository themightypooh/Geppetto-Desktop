# Cloth Simulation: This Project vs Blender

## Overview

| Aspect | This Project (Marionette Desktop) | Blender |
|---|---|---|
| Solver type | Verlet / position-based (PBD) | Implicit (linear) + PBD options |
| Execution | CPU + GPU compute (OpenGL 4.3+) | CPU (OpenMP), optional GPU (experimental) |
| Integration with app | Mesh deformation, skin weights preserved | Full modifier stack, shape keys, armatures |
| Target | Real-time viewport / game asset prep | Offline + interactive animation |

## Feature Comparison

### Forces & Fields
| Feature | This Project | Blender |
|---|---|---|
| Gravity | Yes (Vec3) | Yes |
| Wind | Yes (uniform Vec3) | Yes (force fields, turbulence) |
| Pressure/inflation | Yes (PressurePass) | Yes (pressure) |
| Obstacles (spheres) | Yes (multiple) | Yes (collision objects) |
| Obstacles (triangles/mesh) | Yes (TriangleObstaclePass) | Yes (full mesh collision) |
| Self-collision | Yes (spatial hash) | Yes (spatial hash, BVH) |
| Pinning | Yes (per-vertex bool) | Yes (vertex groups) |

### Constraints
| Feature | This Project | Blender |
|---|---|---|
| Edge stretch | Yes | Yes (structural) |
| Bending springs | Yes (opposite-vertex bending edges) | Yes (bending) |
| Shear | Partial (diagonal edges) | Yes |
| Tearing | Yes (TearStretch) | No (not built-in) |
| Stiffness | Yes (scalar) | Yes (per-group) |
| Damping | Yes | Yes |
| Max speed / sleep | Yes | No (implicit stability) |
| Floor collision | Yes (bounce) | Yes (ground plane) |

### Quality & Robustness
| Feature | This Project | Blender |
|---|---|---|
| Determinism | Yes (CPU + GPU parity tested) | Mostly deterministic |
| GPU acceleration | Yes (compute shaders) | Experimental/limited |
| Self-collision spatial hash | Yes | Yes (BVH) |
| Multi-resolution | No | Yes (multires cloth) |
| Sewing/springs | No | Yes |
| Air pressure / internal | Partial (uniform pressure) | Yes |

### Integration
| Feature | This Project | Blender |
|---|---|---|
| Skinning preservation | Yes (SkinWeights copied) | Armature binding |
| UV preservation | Yes (ToMesh clones UVs) | Yes |
| Export | PolyMesh → mesh | Full scene export |
| Undo/edit | Partial (EditHistory) | Full undo stack |

## Limitations vs Blender

- No implicit (unconditionally stable) solver; large dt can explode.
- No force fields (vortex, turbulence, magnetic).
- No sewing, shrink-wrap garment panels, or pattern-based draping.
- No multi-layer cloth (stacked garments).
- No adaptive subdivision / multires.
- Obstacle collision is simple (closest-point push), no friction model.
- GPU path delegates most passes to CPU (only integrate/constraints on GPU).

## Strengths vs Blender

- Deterministic CPU/GPU parity with automated tests.
- Lightweight, no engine dependency; runs headless (EGL).
- Tearing support out of the box.
- Tight integration with game-asset pipeline (FBX/effigy, skin weights).

## Summary

This project provides a real-time, testable PBD cloth foundation suitable for
game asset preparation. Blender offers a far more complete, production-grade
cloth system (implicit solvers, force fields, sewing, multires). For parity
with Blender, the next steps would be: implicit solver, force fields, sewing
springs, and friction-aware collision.
