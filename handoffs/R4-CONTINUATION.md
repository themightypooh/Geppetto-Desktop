# R4 continuation — shading and object transforms

2026-10-20. R4's implementation was already present, but its board entry still cited
an old scratch build. Current focused acceptance runs directly in this checkout:

```sh
env -u DISPLAY -u WAYLAND_DISPLAY dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter two-sided/ --report /tmp/opencode/r4-focused.json
```

Exit 0, seven registered fixtures pass on NVIDIA hardware EGL. Existing probes cover
single-sided interiors, double-sided backs, mirrored UV normal maps on both sides,
nonuniform bone normals against independently baked geometry, material parsing and
source winding. Current measurements: interior luma 80.2 versus inward-facing room
206.1; double-sided room difference 0.76; mirrored-map twin difference 0.00 with
normal-map effects 3.76/6.53 at front/back; nonuniform bone versus baked geometry
difference 0.00, deliberately skewed normal control 1.25. Survey winding agrees on
140984/141098 triangles (99.92%).

The continuation also reproduced and fixed two production transform defects:

- `RendererShaders.VertexSrc` computed world lighting positions/normals using
  `uModel`, but projected the untransformed position. Raster position now uses the
  same world position. The shadow pass explicitly initializes its model matrix to
  identity, as it uses the shared vertex shader.
- `SceneRenderer.DrawProps` inherited character skinning. Independent reference
  props now draw with skinning disabled, and the character state is restored before
  later draws. The regression checks a translated/scaled emissive prop at its
  projected world position with both static and translated skinned characters.

Before the fixes, the prop-placement probe read only background radiance (0.13989)
at the expected position. Fixing only raster projection passed the static case but
still failed the moving-character case. Both now pass without changing thresholds.

Changed source files: `RendererShaders.cs`, `KeyShadowMap.cs`, `SceneRenderer.cs`,
`tests/TwoSidedShadingTests.cs`. No UI changes, source-asset edits or visible viewer.
Normal Release build exits 0 with zero warnings/errors. Actual clip capture:
`/tmp/opencode/r4-prop-transform-final.png`; its source/props are loaded read-only.
The capture preserves authored prop positions; it is not an animation-retargeting
acceptance claim. Independent prop shadow casting and temporal prop-motion coverage
remain separate integration work; R4's claims are limited to shading and raster
transform agreement. Full suite evidence is recorded in BOARD.md after completion.

Final aggregate acceptance: `python3 tests/gpu-smoke.py`, exit 0;
`/tmp/opencode/geppetto-verify-5z63hgz8/verification.json` records required hardware
EGL PASS and **556 passed, 0 failed, 3 skipped; 559 registered**. R4 is verified.
