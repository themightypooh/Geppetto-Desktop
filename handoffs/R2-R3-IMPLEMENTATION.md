# R2/R3 implementation notes

Status: **R2 verified on hardware EGL** (2026-10-20). **R3 is also verified on hardware EGL**, with the final combined smoke run recorded below. `python3 tests/gpu-smoke.py`
exited 0; report `/tmp/opencode/geppetto-verify-6ow0uoa8/verification.json` contains
542 passed, 0 failed, 3 skipped (545 registered). Release build succeeded with
0 warnings/errors. R2's new shared-renderer tests cover gradient diffuse, rough-metal
reflection broadening, dielectric Fresnel, HDR file radiance parity, cancellation,
failed replacement, repeated environment reload and GL texture disposal.

Palette-only draw batches now default ambient occlusion to one; the previous zero
value suppressed all environment lighting. GPU measurements read scene-linear HDR
and subtract a black-environment draw to isolate IBL from the analytic studio lights.

## Clip authoring space (R2-era bug, fixed)

s&box authors `.riganim` bone translations in **centimetres**. The desktop rig is in
**inches**, because the FBX import scales the mesh and the rig together. A clip played
unchanged therefore puts every translation 2.54x too far out: the arms leave the body
and the skeleton fans apart.

`RigAnimSpace.Detect(rig, clip)` measures the unit difference from the rig itself:

- Bones whose clip translation length is constant across their track keep their bind
  length (a pose rotates a bone, it does not stretch it), so `clip length / bind length`
  reads the unit ratio directly. Bones with animated translations are skipped.
- The dominant cluster of constant-length non-root offsets decides: 1 means the clip and the rig agree, 2.54 means the clip is
  in centimetres and is scaled by 1/2.54. Anything else is left untouched and reported.
- Only keyed bones are scaled. Rotations and scales are unit free.
- The decision and the measured ratio are reported by the headless run and by the
  serve mode's `ready`/`clip` events (`clip_space`, `clip_space_scale`).

Reference props and object/part placements in a clip carry the same centimetre
translations; a future prop loader must apply the same factor.

## R2 interfaces

`SceneRenderer` uses a worker-prepared procedural Z-up environment by default.
Diffuse uses cosine-convolved SH9 divided by pi. Specular uses explicit GGX roughness
mips in RGB16F plus a Smith-GGX/Schlick split-sum RG16F LUT. Analytic studio lights
are unchanged. Environment textures are independent of viewport dimensions.

The CPU source pyramid integrates spherical bin overlap, including fractional bins
for odd image sizes, so polar rows are not over-weighted. Specular integration uses
the GGX sample PDF versus source texel solid angle to select source LOD; the zero-
roughness cube level samples the original image directly.

For an HDRI file, call `await renderer.LoadEnvironmentAsync(path, cancellationToken)`.
The task decodes and prepares CPU data only. The next `Draw` promotes the result on
the GL thread. Awaiting this method does not mean the GL upload already happened.
New requests invalidate older pending publications; cancellation is checked inside
the convolution/prefilter/LUT loops. Errors propagate to the caller and leave the
previous live environment in place.

Alternatively call `IblBaker.Prepare` on a worker and `SetEnvironment` on the GL
thread. Prepared arrays transfer ownership: do not mutate them after publication.
Payload dimensions, full mip chain, roughness levels, finite SH/LUT data and finite
RGB16F radiance range are validated before replacing live resources.

Supported source: Radiance RGBE, standard `-Y height +X width`, modern scanline RLE
or flat pixels. XYZE, other axis orientations, legacy repeat encoding and EXR are
explicitly unsupported. Colour space is linear scene radiance, not sRGB.

## R3 controls

- `renderer.AntiAliasing = AntiAliasingMode.None`: default, no jitter or history.
- `AntiAliasingMode.Temporal`: deterministic eight-sample Halton jitter, camera
  or deformation-motion reprojection, previous-depth disocclusion rejection and
  YCoCg variance clipping. R7's existing SSBO-backed current/previous bone pass
  provides the motion field; no second skinning implementation or doubled vertex
  uniform palette. The field is raster-aligned with TAA jitter and stores unjittered
  pixel displacement, expected previous depth and validity. History UVs compensate
  for the current/previous Halton offsets. Motion blur reuses the same velocity draw.
- `AntiAliasingMode.Msaa4`: explicit four-sample RGBA16F renderbuffer path, resolved
  at the requested dimensions before post-processing. Unsupported hosts throw.
- `ShadowRadius`: Poisson/PCSS filter radius in shadow texels; zero selects hard
  comparison. Static capture kernel rotation is fixed. Temporal mode rotates the
  kernel on static geometry and accumulates it through TAA.
- `ShadowNormalOffsetTexels`, `ShadowSlopeBias`, `ShadowConstantBias`: receiver
  normal-offset and caster raster bias controls.
- `ResetTemporalHistory()`: call after in-place source edits or explicit camera cuts.

The shadow projection fits conservative animated bounds derived from per-bone
influenced AABBs, handles nonuniform scale, snaps its centre to world shadow texels
and fits the depth range. Morph snapshots refresh bounds from their current vertices.
MSAA opaque-colour capture resolves from the active multisample framebuffer so R6
transmission does not sample stale single-sample colour.

### Deliberate limitations

Skin deformation and camera motion share R7's velocity field. Morph-target motion
and dynamic live-layer topology are not reliable yet, so these still reject TAA
history. Masked/translucent geometry can differ between the lit and velocity passes;
a velocity-depth agreement check rejects mismatches instead of accumulating them.
The camera-only fallback remains available when no field is supplied. No UI panel
was added.

Both interactive and headless hosts accept `--aa none|taa|msaa4`, `--hdri world.hdr`,
and `--shadow-radius texels` (0..64). Defaults remain no temporal history and the
procedural world. For temporal captures choose a fixed frame count and `--shot-frame`
explicitly; a first-frame screenshot is not a converged temporal result. Launch
paths wait for CPU HDR preparation, and the first Draw performs GL publication.

R2 acceptance and environment-resource lifecycle checks pass as recorded above.
R3 focused acceptance covers analytic diagonal coverage, static/temporal-shadow
convergence, skinned disocclusion, camera cuts, explicit reset, resize/dispose and
deterministic repeat captures. Wider renderer performance work remains under R10.


## Clip/prop placement follow-up

Unknown unit ratios are left unchanged; they are no longer guessed as centimetres.
Unit measurement checks every authored key, so an out-and-back translation does not
vote as a constant bone length. Same-unit clips retain kernel parity. For converted
clips, incompatible first-key offsets retain bind translation in both FK and fallback
sampling, including zero-length twist bones. Root motion and initially compatible
animated translations remain authored and receive only the measured unit conversion.
This is a conservative compatibility fallback, not general skeleton retargeting.

Prop matrices apply mesh-local import scaling before clip placement under
System.Numerics row-vector composition. Mesh size no longer scales world translation.
Five regressions pass in `/tmp/opencode/clip-space-final.json`; owned scene capture:
`/tmp/opencode/arms-placement-final.png`. Source models/clips were read without changes.

Final combined check after placement fixes: `python3 tests/gpu-smoke.py` exit 0,
`/tmp/opencode/geppetto-verify-z42b2l9o/verification.json`: 547 passed, 0 failed,
3 skipped, 550 registered. Normal Release build: 0 warnings/errors.


## R3 acceptance follow-up (2026-10-20)

Two regressions were reproduced before fixing the production shader path:

- TAA stored jittered output, so a static diagonal edge oscillated and measured worse
  than no AA (coverage MAE 0.26818 versus 0.20231). Reconstructing the current frame
  onto an unjittered output/history grid reduces TAA MAE to 0.13944. MSAA4 measures
  0.07052. The reference uses exact triangle/pixel polygon intersection, independent
  of GL's sample positions. Capture extent is a deliberately non-square 128x96.
- Wide shadow filters compared every tap to the centre receiver depth. A radius-32
  kernel created 0.34082 linear self-shadow error on an unoccluded sloped receiver.
  Receiver-plane depth gradients now correct each blocker-search/PCF tap. The same
  unoccluded receiver matches the fully lit oracle within 0.015 linear radiance at
  radius 0 and 32; observed maximum error is zero on this GPU. At radius 12 the edge
  transition spans 59 samples at 0.01 world-unit intervals, versus zero at radius 0,
  while preserving the shadow's dark centre (0.7715 versus 0.7769 contrast).

`env -u DISPLAY -u WAYLAND_DISPLAY dotnet run --project tests/Desktop.Tests.csproj
-c Release -- --filter aa/ --report /tmp/opencode/r3-acceptance-final.json` exits 0:
three registered fixtures exercise the shared SceneRenderer on NVIDIA hardware EGL.
Static normalized per-pixel delta falls from 0.001006 to 0.000631 by frame 64;
temporal-shadow delta falls from 0.001523 to 0.001201. Repeating the same 64-frame
sequence after reset yields identical pixels. Moving skin leaves no old silhouette,
camera cuts/reset match the current-pose reference, and resizing/disposal leak no
tracked render targets. No visible viewer was launched.

Final combined R3 check: `python3 tests/gpu-smoke.py` exit 0,
`/tmp/opencode/geppetto-verify-0xgkq_ji/verification.json`: **552 passed, 0 failed,
3 skipped, 555 registered**. Normal Release build: 0 warnings/errors. R2 and R3
are verified for their named acceptance fixtures; additional renderer work remains
under the other tickets.
