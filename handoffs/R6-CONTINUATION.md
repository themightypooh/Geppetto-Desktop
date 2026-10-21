# R6 — material shading acceptance

2026-10-20, single-agent continuation. Existing feature implementations run through
the shared SceneRenderer and production descriptor/draw-batch/uniform path. Focused
hardware EGL acceptance:

```sh
env -u DISPLAY -u WAYLAND_DISPLAY dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter material-feature/ --report /tmp/opencode/r6-focused-final.json
```

Exit 0, four registered fixtures. The existing MaterialDescriptorTests.Register
entry calls MaterialFeatureTests.Register; the live-AI owner's shared test manifest
was not edited. Raw material metrics are saved under the test report's work directory.

## Fixes reproduced by the probes

1. Captured opaque HDR colour already includes scene exposure. Transmission added it
   to unexposed shading and exposed it again, producing roughly four times the signal
   at exposure 2. The refraction sample now returns to scene radiance before final
   exposure. Reference red transmission: 3.609375 at exposure 1, 7.21875 at exposure 2.
2. Clearcoat computed powers of `1-dot(v,h)` without bounding the cosine. Floating-point
   error made some cosines slightly larger than one, and GLSL pow of a negative base
   produced NaNs (771 NaN components in the initial card capture). Fresnel/view cosines
   are bounded to their physical interval; the same complete HDR capture is now finite.
3. MaterialLobes clamps attenuation colour to [0,1], preventing negative absorption or
   amplification. All lobe scalar/vector components reject nonfinite values before GL
   publication. Roughness floors, nonnegative thickness/radius/scale, weight bounds and
   signed anisotropy bounds are retained. Zero-weight defaults remain unchanged.

## What the probes establish

- Transmission samples an explicitly emissive opaque backdrop and attenuates red/green
  with thickness while preserving the white attenuation channel. Thickness 0.1 to 1.1:
  red 3.609375 to 1.8105469, green 3.7890625 to 3.0351562, blue unchanged at 3.890625.
  Exposure is applied once. MSAA4 and single-sample backdrop capture agree at the
  interior probe within 0.02 linear RGB distance.
- Wrapped subsurface scattering uses base colour as its scattering colour and per-channel
  radius/scale for bleed. A grazing fixture gains RGB (0.06396, 0.01392, 0.00415), showing
  the longer red mean free path. This is the ticket's permitted wrap approximation.
- Sheen tint produces a blue-dominant grazing lobe: RGB gain (0.02228, 0.05579, 0.33673).
- Clearcoat adds a narrow second highlight over a rough base; its peak gain exceeds 0.1
  linear radiance and four times the shoulder gain. The measured peak gain is about 36.9
  under the deliberately bright, aligned test light, within finite HDR range.
- Rotating anisotropy by pi/2 swaps highlight shoulders: opposite-axis differences are
  approximately +16.23 and -16.22. This measures spatial stretching, not a uniform tint.
- Hashed transparency produces 40–60% coverage for alpha 0.5 and byte-identical HDR
  results with reversed batch order. Opaque, mask and blend modes produce distinct results.

## Explicit material semantics and limits

`MaterialDescriptor` is CPU-only. R6 scalar/vector factors are retained per batch;
no new lobe texture channels are claimed. Anisotropy follows the mesh tangent, rotated
in radians by AnisotropyRotation. Clearcoat uses the unperturbed geometric normal.
Transmission uses screen-space opaque colour, refraction, roughness blur and Beer-Lambert
attenuation; it does not trace offscreen geometry or multiple transparent layers.

AlphaMode.Opaque ignores base alpha. Mask discards below AlphaCutoff and writes depth.
Blend composites source alpha after opaque geometry, tests depth and disables depth
writes; ordinary overlapping blend surfaces still require appropriate order. Hashed
uses a deterministic screen-space threshold, discards holes and writes depth for surviving
samples; depth testing makes the accepted surfaces independent of draw order. It is a
spatial approximation, with correlated screen-space noise across layers. Alpha-aware
shadow casting and full volumetric/multiple-scattering transport are not certified here.

Normal Release build exits 0, zero warnings/errors. No source assets, UI or interactive
viewer were touched. Final combined smoke-suite evidence is recorded in BOARD.md.

Final aggregate attempt: `python3 tests/gpu-smoke.py`, exit 1;
`/tmp/opencode/geppetto-verify-6nlgx8_h/verification.json`: **563 passed, 1 failed,
3 skipped, 567 registered**. The sole failure is the separate, actively owned R5 SSAO
corner acceptance probe (corner 2.4618 versus 2.5377). R6's four fixtures pass in the
aggregate as well as the focused run. The aggregate gate remains open; this report does
not certify R5 or claim a fully passing suite. No R5 source or assertion was changed.
Changed R6 files: `MaterialLobes.cs`, `MaterialFeatureShaders.cs`, the view-cosine clamp
in `RendererShaders.cs`, new `tests/MaterialFeatureTests.cs`, its registration in
`tests/MaterialDescriptorTests.cs`, and handoff documentation.
