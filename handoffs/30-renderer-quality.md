# 30. Renderer quality wave — R0 to R12

**Status: scoping only.** No R ticket is implemented, assigned or certified. This file is the
plan; `handoffs/BOARD.md` owns the state of each ticket.

## Goal — one step below Blender, in the viewport

Target tier: a **modern EEVEE-class realtime renderer** (the Blender 4.x "EEVEE Next"
feature level) that renders this project's models the way Blender's viewport does:
HDR colour pipeline, image-based lighting, soft temporal shadows, TAA, screen-space
ray tracing, volumetrics, motion blur, real depth of field, and a complete PBR material
set with SSS/transmission.

**One deliberate step below Blender.** We are *not* building a Cycles-class path tracer:
no unbiased path tracing, no light tree, no caustics, no OpenImageDenoise-grade denoiser.
`PathTracerService.cs` and `GpuRayTracingService.cs` exist as stubs with no callers and no
tests; they stay stubs until a path-tracing ticket exists. If the goal later changes to
Cycles parity, that is a separate architecture, not an extension of this wave.

Also explicitly out of scope, because they are Blender products rather than renderer
features: the compositor node tree, the video sequencer, grease-pencil 2D painting, and
the full geometry-nodes node graph (we keep the existing `GeometryNodes.Core.cs` service).

Non-goals that apply to every ticket: no UI panels, no visible window, no kernel
(`../geppetto`) edits, no claims of parity with the *game's* `ps1_lit` shader, and no
"renderer parity" claims based on a cropped RMSE against a different camera.

## Current pipeline, as it actually runs

Order of operations in `SceneRenderer.Draw` (`SceneRenderer.cs:131`):

1. `mesh.ApplyMorphWeights` — morph deltas re-uploaded to the VBO.
2. `KeyShadowMap.Draw` — 2048² depth-only pass, ortho frustum fitted to the whole mesh
   (`KeyShadowMap.cs:10`, `KeyShadowMap.cs:43`).
3. SSAO depth pre-pass — depth-only into `_depthFbo`/`_depthTex`, `ColorMask(false…)`.
4. Main lit pass — into `BloomPass.ColorFbo`, i.e. an **`Rgba8`** target
   (`BloomPass.cs:131`). The fragment shader does lighting, tone-maps and encodes to sRGB
   *before writing* (`Program.cs:355`).
5. `BloomPass.Process(1.0f)` — bright-pass + separable blur on the already-encoded LDR
   image (`BloomPass.cs:191`).
6. `BloomPass.Composite` — adds bloom, applies depth-of-field blur, writes to the target
   (`BloomPass.cs:231`). The DoF blur also runs in sRGB space.
7. Depth blit back to the caller's target (`SceneRenderer.cs:256`).

What already works and must not be rebuilt: real material channels with sRGB internal
format, mipmaps and 8× anisotropy (`ViewportMesh.cs:540`), content-hash texture caching
(`ViewportMesh.cs:530`), per-corner UVs, tangents with handedness, a 20-float vertex
stride, the 220-bone GPU palette, energy-conserving GGX, PCSS-style soft shadows, the
shared renderer used by both the interactive app and windowless EGL captures, and the
per-process texture/byte/reload budget (`ResourceBudget.cs`).

## Scoping findings — defects that exist today

Each row is a defect visible in the source, not a wish. Line references are current.

| # | Defect | Evidence | Visual cost | Ticket |
|---|---|---|---|---|
| D1 | The scene is rendered and tone-mapped into an **LDR** `Rgba8` target, so bloom, DoF and the post chain all operate on sRGB-encoded values. | `BloomPass.cs:131`, `Program.cs:355` | Highlight rolloff is crushed before post; DoF blurs in gamma space; no HDR headroom for real bloom | R1 |
| D2 | Bloom's bright-pass threshold is 1.0 on an LDR image that is already clamped to [0,1], so it contributes ~nothing. The visible glow comes from an in-shader approximation `hdr += max(hdr-1,0)*0.25`. | `BloomPass.cs:191`, `Program.cs:354` | No real bloom; glow is a crude boost with no radius control and no energy conservation | R1, R9 |
| D3 | **SSR samples the wrong texture.** The "screen colour" sampler is bound to `BloomPass.ScreenColorTex`, which is `_blurTexV` — the vertically blurred *bright-pass* image, not the scene. | `SceneRenderer.cs:212`, `BloomPass.cs:116`, `Program.cs:341` | Screen-space reflections are effectively dead or smeared; they also depend on the previous frame | R5 |
| D4 | **SSAO compares raw NDC depth** (`sd < centerDepth - 0.001`) with an 8-tap fixed ring, no reconstruction, no bilateral blur. | `Program.cs:257`–`Program.cs:269` | Perspective-distorted, banding-prone AO that darkens the wrong places | R5 |
| D5 | **The shading normal is flipped toward the viewer** (`if (dot(n,v) < 0.0) n = -n;`), so backfaces and interiors are lit as if they were front-facing, and the normal map is applied in a flipped tangent frame. | `Program.cs:237` | Wrong shading on closed meshes viewed from inside; broken normal maps on backfaces | R4 |
| D6 | **IBL is a single lookup of a 32×32 procedural sky** for both diffuse irradiance and specular reflection. No mip chain, no BRDF LUT. | `Program.cs:307`, `Program.cs:318`, `SceneRenderer.cs:77` | Rough metals mirror the sky; ambient is flat and directionally crude; no HDRI world | R2 |
| D7 | **No anti-aliasing in the scene pass.** The scene FBO is single-sampled; the window's MSAA is unused because the image is rendered offscreen. | `SceneRenderer.cs:225`, `CaptureTarget.cs:22` | Hard edges on every silhouette | R3 |
| D8 | **One 2048² shadow map for the whole model**, fitted to the mesh bounds, with a depth-only bias and no normal offset. | `KeyShadowMap.cs:10`, `KeyShadowMap.cs:43`, `Program.cs:172` | Blobby or acne-prone shadows; no soft-shadow radius control; large scenes lose texel density | R3 |
| D9 | **Blended batches are neither sorted nor depth-write-suppressed**; blending is enabled per batch with depth writes left on. | `ViewportMesh.cs:667`–`ViewportMesh.cs:671` | Transparent surfaces occlude each other in submission order | R6 |
| D10 | **No temporal accumulation, no jitter, no history buffer.** | absent from `SceneRenderer.cs` | No TAA, no soft shadow filtering over time, no motion blur | R3, R7 |
| D11 | **No transmission/refraction, no subsurface, no sheen/clearcoat/anisotropy.** | absent from `Program.cs:81`–`Program.cs:364` | Glass, skin and fabric look wrong next to Blender's output | R6 |
| D12 | **No volumetrics.** | absent | No fog volumes, no god rays | R8 |
| D13 | **Shaders live inside `Program.cs`** as two giant `const string`s, and `SceneRenderer` mixes shadow, pre-pass, main and post in one method. | `Program.cs:29`, `Program.cs:81`, `SceneRenderer.cs:131` | Nobody can own a renderer change without owning `Program.cs`; parallel work collides | R0 |
| D14 | **3 766 lines across 19 files of renderer-adjacent code have no caller and no test**: `ParticleSystem.cs`, `GeometryNodes.Core.cs`, `BaseMotionLibrary.cs`, `TexturePaintLayers.cs`, `SkinningLodService.cs`, 13 `Gpu*Service.cs` files, `PathTracerService.cs`. | BOARD.md section 2a | Dead code inflates the surface and can be mistaken for features | R11 |
| D15 | **Verified backend services are not wired into the app**: `ExportService`, `DependencyWatcher`, `AutosaveScheduler`, `PartTrackEvaluator`, `ClipBlendService`, `ClipCapabilityValidator`, `TimelineEventDispatcher`, `ConstraintParityInventory`, `AdvancedIkService`, `MorphEditService`, `WeightPaintService`, `RetopoService`, `TexturePaintService`, `UvEditService`, `ShrinkwrapService`, `DataTransferService` all have tests but no production caller. | grep over non-test sources | The viewer does far less than the test suite proves; "verified" ≠ "shipped" | R11 |

## Parity matrix — Blender 4.x viewport, feature by feature

"Current" is what today's code does. "Proof" is the artefact that closes the row.

| Blender (EEVEE Next) feature | Current | Ticket | Proof |
|---|---|---|---|
| AgX / Filmic view transform, exposure, white balance | ACES with a 0.85 pre-scale, then a contrast/saturation polish, one sRGB encode | R1 | EGL pixel probe: mid-grey ramp matches the reference curve; single encode verified |
| HDR scene target | No — LDR `Rgba8` | R1 | Emissive > 1.0 survives to the bloom pass |
| Bloom / Glare | LDR threshold, effectively off | R1, R9 | Halo radius measured around a bright emitter |
| HDRI world + ambient occlusion | 32×32 procedural sky, single lookup | R2 | HDRI fixture: diffuse irradiance and rough-metal reflection match a CPU reference |
| Soft shadows, shadow radius, jitter | Single 2048² map, PCSS, no radius control | R3 | Shadow edge softness widens with the radius parameter; acne-free on slopes |
| TAA / render samples | None | R3 | Static camera converges; edge pixels stop changing |
| SSAO / SSGI | Broken raw-depth AO | R5 | Corner darker than flat plane; flat plane unchanged |
| Screen-space reflections | Wrong source texture | R5 | Reflected object colour present in the expected region |
| Transmission / refraction | None | R6 | Refracted background offset correct behind a glass panel |
| Transparent sorting | Unsorted, depth writes on | R6 | Two overlapping alpha surfaces composite in the documented order |
| Subsurface scattering, sheen, clearcoat, anisotropy | None | R6 | SSS probe: thin limb glows red at grazing thickness |
| Depth of field (bokeh) | LDR 5×5 box blur on depth difference | R7 | Background blur increases with aperture; in-focus subject stays sharp |
| Motion blur (incl. deformation) | None | R7 | Moving object smears along its screen velocity |
| Volumetrics (world/object) | None | R8 | Fog volume scatters light; density falls off with distance |
| Real-time modifiers, GN instancing, particles, hair | Services exist, not rendered | R9 | Each visible in the viewport through the shared renderer |
| Curves / text / grease pencil | No | out of scope | — |
| Compositor / sequencer | No | out of scope | — |
| Cycles path tracing | Stub only | out of scope (later tier) | — |

## Tickets

Every ticket: headless verification only (`--headless`, windowless EGL), CPU tests where the
work is CPU-side, no visible window, no kernel edits, and no changes to `Program.cs`
without the coordinator's lease (see R0).

### R0 — Renderer architecture split (structural prerequisite)

**Prerequisite:** none. **Own:** new `RendererShaders.cs`, `RenderPass.cs`, `RenderTarget.cs`;
coordinator-owned edits to `Program.cs` and `SceneRenderer.cs`.

1. Move the two shader strings out of `Program.cs` into `RendererShaders.cs` as named
   constants (`Vertex`, `Fragment`). `Program.cs` keeps a thin alias so nothing else
   changes behaviour. **Every later pass gets its own shader source file** (`TaaShaders.cs`,
   `SsaoShaders.cs`, `IblShaders.cs`, …) rather than appending to one file — otherwise every
   shader ticket collides on the same file and the wave cannot be parallelised.
2. Split `SceneRenderer.Draw` into explicit passes with an explicit target description:
   `ShadowPass`, `DepthPrePass`, `LitPass`, `PostChain`. One place decides the pass order.
3. Introduce a `RenderTarget` helper that owns an FBO + colour/depth attachments, a
   requested internal format, and the resize/dispose lifecycle, so R1/R3/R7 can add
   targets without duplicating FBO boilerplate.
4. Keep `SceneRenderer` as the single shared entry point for the interactive app and
   `HeadlessRender`. No second lighting implementation.

**Required tests:** CPU: pass-order enumeration and target-lifecycle unit tests; EGL: the
existing bind/animated captures stay byte-identical to the current baseline (proves the
split changed no pixels).

**Done:** a shader change no longer requires touching `Program.cs`, and two workers can
own renderer files simultaneously.

### R1 — HDR pipeline and colour management

**Prerequisite:** R0. **Own:** `SceneRenderer.cs`, `BloomPass.cs`, `RendererShaders.cs`,
`Program.cs` (coordinator), `tests/HdrPipelineTests.cs`.

1. The lit pass writes **linear HDR** into an `Rgba16f` target. Tone-mapping and the sRGB
   encode happen exactly once, in the final composite.
2. Bloom's bright-pass runs on linear HDR with a threshold in linear units, then a
   physically-plausible blur (progressive downsample/upsample or a Karis-averaged
   Gaussian). Delete the in-shader `hdr += max(hdr-1,0)*0.25` approximation (D2).
3. DoF moves to the HDR chain and blurs in linear space with a real CoC.
4. Add an explicit view transform with a Blender-like look: an AgX-style or Filmic-style
   curve, exposure, gamma, and a "look" parameter. One documented default that matches
   Blender's default view transform as closely as the maths allows.
5. Document the convention: colour/emission images are sRGB inputs; roughness, metallic,
   normal and mask images are linear data; the shader works in linear; one encode at the
   end.

**Required tests:** EGL probes on a synthetic ramp: a linear ramp must map monotonically
through the curve; a mid-grey must land within a stated tolerance of the reference; an
emissive value above 1.0 must produce a bloom halo whose radius is measurable and whose
energy is finite; the previous LDR behaviour must not survive (a bright emitter must now
clip less).

**Done:** bloom is a real effect with a measurable halo, and one encode happens.

### R2 — Image-based lighting: world HDRI, irradiance, prefiltered specular

**Prerequisite:** R1. **Own:** `SceneRenderer.cs`, `IblBaker.cs` (new),
`tests/IblTests.cs`.

1. Support a **world HDRI** (equirect and/or cubemap) as the environment source, with a
   procedural fallback that replaces today's 32×32 cube.
2. Bake **cosine-convolved irradiance** (or SH-9) for diffuse, and a **roughness-mipped
   specular** cube for reflections. Rough metals must blur, not mirror.
3. Add a **split-sum BRDF LUT** so the specular env term is energy-correct.
4. Keep the world-fixed key/fill/rim lights: they remain analytic; the environment is
   ambient/reflection only.

**Required tests:** EGL: a grey diffuse card facing up is brighter than the same card
facing down under a gradient sky; a metal at roughness 0.05 shows a sharp reflection while
roughness 0.6 shows a broad one; a dielectric shows a Fresnel rim. CPU: the irradiance
convolution matches a Monte-Carlo reference within a stated tolerance.

**Done:** rough materials look rough, and the world can be an HDRI file.

### R3 — Anti-aliasing and shadow quality

**Prerequisite:** R1. **Own:** `SceneRenderer.cs`, `KeyShadowMap.cs`, `TaaPass.cs` (new),
`tests/AntiAliasingTests.cs`.

1. **TAA**: jitter the projection per frame, reproject with the previous view-projection
   and motion vectors, resolve with a variance-clamped neighbourhood filter. When motion
   vectors are unavailable, fall back to camera-only reprojection and disable the history
   on disocclusion.
2. **MSAA alternative**: a multisampled `Rgba16f` renderbuffer path for hosts/driver
   configurations where TAA is not wanted, selected explicitly. The offscreen capture must
   still be exactly the requested size.
3. **Shadows**: cascaded or tightly-fitted shadow maps with a shadow-radius control,
   normal-offset and slope-scaled bias, and temporal filtering across frames.
4. Shadow and TAA must not change the deterministic capture contract: bind-pose captures
   remain reproducible.

**Required tests:** EGL: a high-contrast diagonal edge fixture measures edge coverage
against analytic coverage; a static scene converges over frames (per-pixel delta drops
below a threshold); a shadow fixture on a sloped receiver shows no acne bands and the
shadow edge widens with the radius parameter; two bind-pose captures taken with the same
seed are byte-identical.

**Done:** silhouettes are smooth, and shadows are soft and stable.

### R4 — Shading correctness: two-sided normals and tangent frames

**Prerequisite:** R0. **Own:** `RendererShaders.cs`, `MaterialDescriptor.cs`,
`tests/TwoSidedShadingTests.cs`.

1. Remove the viewer-facing normal flip (D5). Use `gl_FrontFacing` for genuinely
   double-sided materials, selected by an explicit material property, and keep single-sided
   materials single-sided.
2. Apply the normal map in a consistent tangent frame for backfaces; re-orthogonalise the
   tangent after skinning; keep the bitangent sign.
3. Nonuniform scale must transform the normal by inverse-transpose and the tangent by the
   same deformation as the position, including under skinning.

**Required tests:** EGL: a closed mesh viewed from inside must not be lit as if seen from
outside (probe the interior); a mirrored-UV fixture must keep the same normal-map response
as its non-mirrored twin; a nonuniformly scaled bone must not skew the normal.

**Done:** backfaces and interiors shade correctly.

### R5 — Screen-space ray tracing: AO, reflections, refraction

**Prerequisite:** R1, R4. **Own:** `SsaoPass.cs`, `SsrPass.cs` (new),
`SceneRenderer.cs`, `tests/ScreenSpaceTests.cs`.

1. Rewrite AO with **view-space depth reconstruction**, a hemisphere kernel, range checks,
   a depth-aware bilateral blur and a rotation noise texture (fixes D4).
2. Rewrite SSR to sample a **real history colour buffer** (the previous frame's HDR
   colour, not the blurred bright-pass) with a view-space march, binary refinement,
   roughness fade, screen-edge fade and an environment fallback on a miss (fixes D3).
3. Screen-space refraction for transmissive materials, sampling the same history buffer.
4. Expose each effect with a strength/quality setting; disabled effects must cost nothing
   measurable.

**Required tests:** EGL: a corner fixture is darker than a flat plane and a flat plane is
unchanged; a mirror-ish floor fixture shows the reflected colour in the expected region and
a rough material shows none; a glass panel fixture shows the expected background offset;
no NaNs and no GL errors in the probes.

**Done:** AO, SSR and refraction are individually switchable and measurable.

### R6 — Material completeness: transmission, SSS, sheen, clearcoat, anisotropy

**Prerequisite:** R1, R4. **Own:** `MaterialDescriptor.cs`, `RendererShaders.cs`,
`tests/MaterialFeatureTests.cs`.

1. Extend `MaterialDescriptor` with the new channels and factors (transmission, thickness,
   subsurface colour/radius/scale, sheen tint, clearcoat/roughness, anisotropy direction).
   Keep the existing "no GL handles in the descriptor" rule.
2. Transmission with refraction and thickness-based attenuation; thin-film-style
   transmission is optional.
3. Subsurface scattering as a wrap-diffuse/Burley approximation with a scale parameter;
   sheen as a retro-reflective lobe; clearcoat as a second specular lobe; anisotropy as a
   stretched highlight.
4. Alpha modes must be explicit and documented: opaque, masked (already present), blend,
   and dithered/hashed for order-independent transparency.

**Required tests:** EGL: a thin limb with SSS glows toward the red channel at grazing
thickness; a clearcoat card shows a distinct second highlight; a transmissive panel
attenuates with thickness; dithered transparency does not depend on draw order.

**Done:** each material feature has a probe that fails without it.

### R7 — Depth of field, motion blur and temporal stability

**Prerequisite:** R1, R3. **Own:** `PostChain` in `SceneRenderer.cs`, `MotionBlurPass.cs`
(new), `tests/PostEffectsTests.cs`.

1. Real DoF: physically-motivated circle of confusion from focus distance, focal length
   and f-stop (Blender's camera model), a bokeh shape parameter, and near/far handling.
2. Motion blur: per-object screen-space velocity, including deformation (skinned) motion,
   with a shutter parameter. Deformation motion needs the previous frame's skinned
   positions — pick the cheap honest version and document it.
3. Temporal stability: any history-based effect must reject stale history after a camera
   cut, resize or source reload.

**Required tests:** EGL: an in-focus subject stays sharp while the background blurs more
with a larger aperture; a moving object smears along its screen velocity; a camera cut
produces no ghosting.

**Done:** DoF and motion blur are physical, not box blurs.

### R8 — Volumetrics

**Prerequisite:** R1. **Own:** `VolumePass.cs` (new), `VolumeGrid.cs` (new),
`tests/VolumeTests.cs`.

1. A froxel grid (or a ray-marched equivalent) with world and object volumes, additive and
   multiplicative media, emission, and density textures.
2. Light shafts from the key light through the volume, with the shadow map used for
   volumetric occlusion.
3. A cheap fallback for hosts without the required texture formats.

**Required tests:** EGL: a fog volume between the camera and the model scatters the key
light; density falls off with distance from the volume centre; an empty volume costs no
measurable time and changes no pixels.

**Done:** volumetrics exist, are measurable, and can be switched off.

### R9 — Live scene features in the viewport

**Prerequisite:** R0, and the corresponding service ticket. **Own:** coordinator-only
(`Program.cs`, `SceneRenderer.cs`, `ViewportMesh.cs`) plus the service files.

1. Render the services that already exist but are not drawn: particles
   (`ParticleSystem.cs`), hair strands (`HairStrandService.cs`), geometry-nodes instances
   (`GeometryNodes.Core.cs`), cloth (`ClothSimService.cs`/`ClothSimGpuService.cs`).
2. A live modifier stack evaluated in the viewport before upload (`RetopoService`,
   `ShrinkwrapService`, `WeightPaintService`, `MorphEditService`).
3. Each feature must go through the shared `SceneRenderer`; no second material or lighting
   path.

**Required tests:** EGL: a fixture with a live modifier shows the modified geometry; a
particle fixture shows the expected particle count and depth order; the headless capture
differs from the unmodified capture in the expected region only.

**Done:** the features that exist as services are visible in the viewport.

### R10 — Renderer performance and resource lifecycle

**Prerequisite:** R1–R3. **Own:** `SceneRenderer.cs`, `ViewportMesh.cs`,
`tests/RendererPerformanceTests.cs`.

1. Measure and reduce per-frame CPU cost: per-batch uniform uploads and six texture binds
   per batch per frame are the current hot path. Consider texture arrays per channel or a
   uniform buffer per material.
2. Per-frame allocation must stay at zero for the render path (the bone-key path already
   holds this bar).
3. Render-target lifecycle: every new target from R1/R3/R7/R8 is resized with the
   viewport and released on dispose; repeated load/reload must not leak GL objects.
4. Extend the existing `MeshResourceBudget` counters to cover render targets, not just
   textures, and report the totals in `--stats`.

**Required tests:** CPU/EGL: allocation counter over N frames is zero; a resize/reload
loop shows a stable GL object count; timings are reported per stage with the workload
described (vertex/triangle/batch/texture counts).

**Done:** the renderer has a measured budget and does not leak.

### R11 — Decide the unwired surface (wire it or delete it)

**Prerequisite:** none, but do it early — it is cheap and it removes noise. **Own:**
coordinator plus the owner of each file.

1. For each of the 3 766 lines listed in D14 and each unwired service in D15, decide:
   wire it into the app, wire it into the renderer (R9), give it tests and a caller, or
   delete it. A file with no caller and no test is not a feature.
2. Do not delete a service that another ticket is about to use; record the decision in
   `handoffs/BOARD.md` with the reason.
3. `BaseMotionLibrary.cs` (824 lines) is explicitly not loaded at startup — decide whether
   it stays as an offline generator with a test, or goes.

**Required tests:** a CPU test that enumerates the renderer/service surface and fails if a
listed file has neither a caller nor a test — the check itself prevents the drift from
returning.

**Done:** every service file is either wired and tested, or gone.

### R12 — Renderer verification harness

**Prerequisite:** R0. **Own:** `tests/RendererImageTests.cs`, `HeadlessImageChecks.cs`
(extend), `tests/README.md`.

1. Extend the existing V3 headless image checks into a renderer test suite: AA edge
   coverage, bloom halo, AO corner, SSR reflection, transparent ordering, shadow softness,
   DoF, motion blur, volume scattering, SSS, transmission.
2. Every check is a region/pixel probe with an independently computed expectation — never
   "the image changed".
3. Keep a deterministic capture contract: identical inputs produce identical bytes, so
   TAA and temporal effects must be seeded or explicitly disabled for capture.

**Required tests:** the suite itself, with one deliberately failing probe as a harness
self-test, so a broken probe cannot silently pass.

**Done:** each renderer feature in this wave has a retained, assertable capture.

## Order and staffing

```
R0  ──► R1 ──► R2
 │        ├──► R3 ──► R7
 │        ├──► R4 ──► R5
 │        │      └──► R6
 │        └──► R8
 └──► R11 (parallel, cheap, do first)

R9 depends on R0 plus the service tickets. R10 depends on R1–R3.
R12 runs alongside and grows with each ticket.
```

Recommended first wave, highest visual return per unit of risk:

1. **R11** — one day, removes 3 766 lines of ambiguity and unblocks honest status claims.
2. **R0** — the split that lets the rest be worked on in parallel without `Program.cs`
   collisions.
3. **R1** — the HDR pipeline. This is the single biggest visual change: real bloom,
   correct highlight rolloff, correct DoF.
4. **R4** — cheap, and fixes outright wrong shading.

Then **R2/R3** (the look), then **R5/R6** (the material breadth), then **R7/R8** (the
post stack), then **R9/R10** (integration and cost).

**Risk to state plainly:** R1 changes every pixel the project has ever compared against a
baseline. The existing headless captures and the GPU smoke test's byte-identical
requirement must be re-baselined deliberately, with the old and new images kept side by
side. Do not "update the baseline" to hide a regression — record the change, why it is an
improvement, and the probe that proves it.

## Workload — where the work actually is

Rough order-of-magnitude engineering effort for **one experienced graphics engineer** who
already knows this codebase. These are planning numbers, not commitments: a worker who
has to learn the render pipeline first adds —% to their ticket.

| Rank | Ticket | Big work items | Days | Risk | Depends on |
|---|---|---|---|---|---|
| 1 | **R3** AA + shadows | TAA jitter + reprojection + velocity buffers + history management (≈3 d), variance-clamped resolve (≈1 d), MSAA `Rgba16f` path (≈1 d), cascaded/tight-fit shadow maps (≈2 d), normal-offset + slope-scaled bias (≈1 d), temporal shadow filtering (≈1 d), tests (≈2 d) | **—** | High — temporal effects are the easiest thing to get subtly wrong and the hardest to test | R1 |
| 2 | **R6** Material breadth | Descriptor extension (≈1.5 d), transmission + refraction (≈2 d), Burley SSS (≈1.5 d), sheen lobe (≈1 d), clearcoat lobe (≈1 d), anisotropy (≈1.5 d), dithered/hashed alpha (≈1 d), tests (≈2.5 d) | **—** | Medium — mostly shading maths, well understood | R1, R4 |
| 3 | **R5** Screen-space ray tracing | View-space reconstruction (≈1 d), SSAO kernel + bilateral blur + noise (≈2 d), SSR history buffer + march + binary refine + fades (≈3 d), refraction (≈1.5 d), tests (≈2 d) | **—** | High — SSR is history-dependent and visually noisy; verification is fiddly | R1, R4 |
| 4 | **R2** IBL | HDRI load + equirect→cube + procedural fallback (≈1.5 d), irradiance convolution or SH9 (≈2 d), roughness-mipped specular prefilter (≈2 d), BRDF LUT + integration (≈1.5 d), tests (≈1.5 d) | **—** | Medium — a lot of GL plumbing, but referenceable on the CPU | R1 |
| 5 | **R1** HDR pipeline | `Rgba16f` targets + linear pipeline + single encode (≈1 d), bloom rework (≈2 d), DoF into the HDR chain (≈1 d), view transform + exposure + look (≈2 d), probes (≈2 d) | **—** | Medium — **changes every baseline pixel in the project** | R0 |
| 6 | **R8** Volumetrics | Froxel grid + scatter integration (≈4 d), shadowed volumetrics (≈2 d), fallback path (≈1 d), tests (≈2 d) | **—** | High — worst ratio of visual value to engineering hours in the wave | R1 |
| 7 | **R7** DoF + motion blur | Physical CoC from focus/f-stop/focal length + bokeh shape (≈3 d), velocity buffers + deformation motion (≈3 d), history invalidation on cut/resize/reload (≈1 d), tests (≈2 d) | **—** | Medium-high — velocity buffers need a previous-frame position copy | R1, R3 |
| 8 | **R9** Live scene features | Wire particles, hair, GN instances, cloth (≈4 d), live modifier stack before upload (≈3 d), tests (≈2 d) | **—** | Medium-high — integration risk sits in `Program.cs`/`ViewportMesh.cs` | R0 + service tickets |
| 9 | **R10** Performance + lifecycle | Texture-array / UBO material batching (≈2 d), allocation counters (≈1 d), render-target lifecycle + budget counters (≈1.5 d), stage timings (≈1 d) | **—** | Low | R1–R3 |
| 10 | **R12** Verification harness | Extend `HeadlessImageChecks` into a renderer suite (≈3 d), per-feature probes as features land (≈1 d per ticket) | **— + ~1/ticket** | Low, but it never finishes on its own | R0 |
| 11 | **R0** Architecture split | Shader relocation (≈0.5 d), pass split (≈1 d), `RenderTarget` helper (≈1 d), tests (≈1 d) | **—** | Low — but everything is blocked on it | none |
| 12 | **R11** Wire or delete | Inventory + per-file decision + the anti-drift test (≈2 d) | **—** | Low | none |
| 13 | **R4** Two-sided normals | `gl_FrontFacing` policy (≈0.5 d), tangent-frame fix for backfaces (≈0.5 d), nonuniform-scale correctness (≈0.5 d), tests (≈1 d) | **—** | Low — small but it fixes outright wrong output | R0 |

**Total ≈ — engineer-days**, plus 15% coordination overhead → **~— days**
(one engineer, ≈— months; three engineers on parallel lanes, ≈2.— months).

### The big chunk, ranked

These seven tickets are **~70% of the wave's work** and they are also the seven that decide
whether the result reads as "Blender-class" or "nice but not Blender":

1. R3 — AA and shadows
2. R6 — material breadth
3. R5 — screen-space ray tracing
4. R2 — image-based lighting
5. R1 — HDR pipeline
6. R8 — volumetrics
7. R7 — depth of field and motion blur

R0, R4, R10, R11 and R12 are the cheap ones that make the other seven possible or
verifiable. **Do not start R3/R5/R7 before R0 and R1** — they all touch the same colour
pipeline, and doing them first means redoing them.

### Staffing lanes

The one-writer-per-file rule is the binding constraint, so lanes are drawn around file
ownership, not around features:

| Lane | Sequence | Files it owns | Blocked by |
|---|---|---|---|
| **A — coordinator** | R0 → R1 → R10 | `Program.cs`, `SceneRenderer.cs`, `RenderPass.cs`, `RenderTarget.cs`, `RendererShaders.cs` | — |
| **B — lighting** | R2 (after R1) | `IblBaker.cs`, `IblShaders.cs`, `WorldEnvironment.cs` | R1 |
| **C — shading** | R4 → R6 | `MaterialDescriptor.cs`, `MaterialFeatureShaders.cs` | R0, R1 for R6 |
| **D — screen space** | R5 → R7 | `SsaoPass.cs`, `SsrPass.cs`, `MotionBlurPass.cs`, `ScreenSpaceShaders.cs` | R1, R4 |
| **E — AA + shadows** | R3 → R8 | `TaaPass.cs`, `TaaShaders.cs`, `KeyShadowMap.cs`, `VolumePass.cs` | R1 |
| **F — integration** | R11 → R9 → R12 | service files, `tests/RendererImageTests.cs` | R0 |

Lanes C, D and E only diverge after R1 lands, and each owns its own shader file, so the
three can run simultaneously. Lane A must not be touched by B–E: B–E hand the coordinator a
new pass plus its target description, and the coordinator wires it into the pass order.

### Calendar shape

| Weeks | Milestone |
|---|---|
| — | R11 + R0 + R12 scaffold. Nothing renders differently yet. |
| — | **R1** (lane A) — the last point at which the baseline can be re-taken cheaply. R2 starts once R1's target format is fixed. |
| — | R3, R4→R6, R5, R7, R8 in parallel across lanes D, C, E. |
| — | R9 live features, R10 performance, R12 coverage, deliberate re-baseline. |
| 13+ | Parity report: the matrix above with a real capture behind every row. |

### Cut lines, if the budget shrinks

- **"Looks like a modern engine" (≈— days):** R0 + R1 + R2 + R3 + R4. This is the
  minimum that fixes the LDR pipeline, gives soft temporal shadows, real AA and correct
  IBL. Skips SSS/transmission, volumetrics, motion blur.
- **"Reads as Blender in a screenshot" (≈+30 days):** add R6 and R5. Skin, glass and
  reflections are what people actually compare against Blender.
- **"Feels like Blender in motion" (≈+25 days):** add R7 and R8.
- **Do not cut R11, R0 or R12.** They are cheap and they are what keeps the rest honest.

## Rules that apply to every R ticket

- Headless only. `--headless` with DISPLAY and WAYLAND_DISPLAY removed, or the CPU suite.
  Never a visible window.
- `SceneRenderer` stays the single shared renderer. No second lighting or skinning path
  for tests.
- GL resource creation and deletion stay on the GL thread; file IO and decode stay off it.
- Every new render target is disposed on resize and on dispose, and is counted by the
  resource budget.
- One writer per file. `Program.cs`, `SceneRenderer.cs`, `ViewportMesh.cs`,
  `MarionettePlayer.cs`, `DocumentLoader.cs`, `DocumentSession.cs`, project files and
  `tests/TestRegistry.cs` are coordinator-leased; record the lease in `handoffs/BOARD.md`
  before editing.
- New test files expose `Register(TestRegistry, TestSettings)` and are added to the
  manifest in `tests/TestRegistry.cs`. No second `Main`.
- Do not widen the assignment into `../geppetto`. Kernel changes need their own ticket,
  mirror sync and kernel tests.
- Evidence, not claims: exact command, exit code, retained artefact, and the remaining
  limitation. Never write "all done" for a partially supported feature.
