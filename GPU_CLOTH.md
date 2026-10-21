# GPU Cloth Simulation

## Overview

`ClothSimGpuService` implements the cloth solver on the GPU using OpenGL 4.3+
compute shaders. It mirrors `ClothSimService` (Verlet/position-based) and operates
on the same `ClothSimService.State`, so results are comparable between CPU and GPU.

## API

- `Create(mesh, options, pinned)` — delegates to `ClothSimService.Create`.
- `Step(state, dt, options)` — CPU fallback (no GL context).
- `Step(state, dt, options, gl)` — runs the full solve on the GPU.
- `Upload(state, gl)` / `Download(state, gl)` — transfer positions/prev/pinned/edges
  between `State` and GPU SSBOs.
- `ToMesh(state, source)` — delegates to `ClothSimService.ToMesh`.

## Shaders

Four compute shaders (in `ClothSimGpuService.cs`):

1. **Integrate** — per-vertex velocity integration, gravity/wind, damping, max speed,
   sleep threshold.
2. **Tear** — marks edges whose stretch exceeds `TearStretch` (rest = -1).
3. **Constraint** — per-edge projection, repeated `ConstraintIterations` times.
4. **Collision** — sphere obstacle + floor collision.

## Buffers

SSBOs bound to bindings 0–3: positions (`vec4`), previous positions (`vec4`),
pinned flags (`int`), edges (`{int a, b; float rest;}`).

## Fallbacks

- No GL context: `Step` runs the CPU solver.
- Multiple obstacles: primary obstacle + floor on GPU; additional obstacles applied
  as a CPU post-pass after download.
- Tearing updates edge list on download.

## Tests

`tests/GpuClothTests.cs` (group `gpu-cloth`): GPU-vs-CPU parity and
upload/download round-trip. Gated on EGL availability.
