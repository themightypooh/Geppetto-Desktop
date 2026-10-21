# Feature comparison: geppetto-desktop vs Blender

Reconciled 2026-10-20. Statuses describe what the **viewer** can do, not what a test proves.
A backend service with no viewport surface is "service only".

## Mesh editing
- Extrude, inset, bevel, loop cut: **missing**. Knife is a foundation (plane/polyline
  classification) with no face splitting.
- Subdivision surface: Catmull-Clark exists in the kernel. Interactive toggle missing.
- Decimate: exists (kernel). Quad remesher: **service only** (`RetopoService`,
  `CrossFieldService`) — not in the viewport.
- Sculpt: dyntopo subdivision + inflate/flatten/smooth foundation. Missing: full brush set,
  masking, multires.
- Non-destructive modifier stack: **missing** (`ModifierStack.cs` never landed).
- Geometry nodes: `GeometryNodes.Core.cs` exists as a **service with no test and no caller**.
- Particles: `ParticleSystem.cs` is a **service with no test and no caller**; only the GPU
  half (`GpuParticleSystem`) is tested.

## UV
- Unwrap, seams, pin, relax: relax + seam marking exist as **services** (`UvEditService`,
  `UvSeamService`); LSCM unwrap, packing and any UV UI are missing.

## Materials / Textures
- PBR material descriptors and real texture channels: **done** (base colour, normal,
  roughness, metallic, emission, occlusion; sRGB for colour, linear for data).
- Texture painting: CPU buffer foundation only (`TexturePaintService`). Missing: GPU viewport
  painting, layers, brush UI.
- Material breadth vs EEVEE: **missing** transmission, subsurface, sheen, clearcoat,
  anisotropy. See the R6 ticket.

## Rigging / Animation
- Skinning, IK, morphs, blend, clips: **done** (A-series).
- Weight painting and auto-weighting: **service only** (`WeightPaintService`,
  `AutoWeightService`).
- Advanced IK / euphoria / hair: **service only**.
- Missing vs Blender: a rig editor UI, NLA, retiming, IK constraints editor, armature
  presets, shape-key UI.

## Simulation
- Cloth: Verlet foundation (gravity, wind, collision, self-collision, tearing) — **service
  only**. Missing: full garment suite, self-collision perf, cloth in the viewport.
- Shrinkwrap: vertex + surface projection — **service only**. Missing: live modifier.

## Renderer (the big one)

The viewport renderer is an **LDR forward pipeline**. Versus Blender 4.x EEVEE Next:

| Feature | Status |
|---|---|
| AgX/Filmic view transform, single sRGB encode | missing (ACES on a 0.85 pre-scale, then a contrast/saturation polish) |
| HDR scene target | missing — the lit pass tone-maps into an `Rgba8` target |
| Bloom / Glare | effectively off — the bright-pass threshold is 1.0 on an already-clamped image |
| HDRI world, irradiance, prefiltered specular, BRDF LUT | missing — a single lookup of a 32×32 procedural sky |
| Soft shadows, shadow radius, cascades, temporal filtering | partial — one 2048² map, PCSS, no radius control |
| TAA / render samples / jitter | missing |
| SSAO / SSGI | broken — raw NDC depth compared with 8 fixed taps |
| Screen-space reflections | broken — samples the blurred bright-pass, not the scene |
| Transmission / refraction | missing |
| Subsurface, sheen, clearcoat, anisotropy | missing |
| Depth of field | an LDR 5×5 box blur on a depth difference, not a physical bokeh |
| Motion blur | missing |
| Volumetrics | missing |
| Transparent sorting / depth-write policy | missing — blended batches are unsorted and write depth |
| Two-sided shading | wrong — the normal is flipped toward the viewer |
| Live modifiers, GN instancing, particles, hair in the viewport | missing |

The plan and the cost: [30-renderer-quality.md](30-renderer-quality.md). The state:
BOARD.md section 3.

## Missing entirely vs Blender
- Modifiers stack (non-destructive).
- Geometry nodes node graph (a service core exists; the graph does not).
- Particle systems in the viewport.
- Compositor.
- Video sequencer.
- Grease pencil.
- Cycles-class path tracing (`PathTracerService` is a stub with no caller and no test; the
  renderer wave deliberately stops one step below it).

## Recommended priority

Two tracks, run in parallel:

**Renderer track** (biggest visual return):
1. R11 wire-or-delete → R0 architecture split → **R1 HDR pipeline** → R4 two-sided normals.
2. R2 IBL and R3 AA + shadows.
3. R6 material breadth and R5 screen-space effects.
4. R7 depth of field / motion blur and R8 volumetrics.

**Modelling track**:
1. Wire the landed services into the viewport (R9) and give them UI.
2. Real quad remesher (field-aligned, target count, 300k+ tris).
3. UV unwrap + packing.
4. Weight painting UI and bone heat.
5. Freehand/polyline knife with face splitting.
