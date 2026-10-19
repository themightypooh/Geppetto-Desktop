# 8. Verification that catches fake completion — V1 to V4

No visible apps. CPU tests and **hardware EGL** are available. Never “temporarily” use SDL
windows, the user's browser or desktop screenshots because a test setup is inconvenient.

## Existing tools and their actual meaning

- `tests/Desktop.Tests.csproj`: executable CPU regression suite; last observed baseline 26
  passes on this machine, including conditional Survey asset checks. Some assets are not portable.
- `tests/gpu-smoke.py`: builds once and invokes `--headless`, with DISPLAY/WAYLAND_DISPLAY
  removed; exact 1024×1024 output, byte-identical repeated bind frames, changed demo pose.
  These checks prove repeatability/some deformation, **not correctness of every rendered feature**.
- `../geppetto-verify/run.sh`: full kernel + cold document timings + desktop tests + GPU checks.
- `../geppetto-verify/app.sh`: explicit pod cache test, now windowless EGL. `app_headless`
  history is separate from older visible-window/present timings.
- `known-failures.txt` contains existing kernel failures. Do not widen it for your desktop ticket.

## V1 — Explicit baseline/fixture coverage and test registration

**Prerequisites:** none. **Own:** test harness/fixture helpers and docs, shared runner under lease.

1. Enumerate baseline tests, CPU-vs-EGL requirements and fixture dependencies. Separate
   synthetic portable fixtures from read-only local Survey/pod/Camhead integration cases.
2. Make missing optional assets an explicit `SKIP` with reason and distinct count. In a job
   configured to require those assets, missing input is failure. A skipped section is not a pass.
3. Refactor test registration enough to add separate named test files without duplicate Main
   methods or silently uncalled methods. Keep all existing assertions running.
4. Record exact executable/build identity and test inventory in result artifacts. Refuse stale
   binaries after a failed build. Isolated MSBuild outputs must be per project, not one shared
   `BaseIntermediateOutputPath` that lets kernel restore overwrite app package assets.
5. Support explicit CPU-only and required-EGL modes. EGL unavailable in required mode fails;
   no window/software fallback disguised as a hardware performance pass.

**Tests:** deliberately missing fixture; deliberately failed build with old DLL present;
registered assertion deliberately fails; wrong output root; required GPU unavailable;
skip counts visible. Restore test fixtures after the negative harness self-tests.

## V2 — Numeric deformation/animation and input invariants

**Prerequisites:** V1; feature-specific assertions land with their owning tickets.

1. Expand synthetic rigs: rotated/nonuniformly scaled parents, multiple roots, unkeyed bones,
   zero/one-frame clips, all ease modes, limits, live IK spaces/weights, loop/end-frame seeking.
2. Compare every local/world transform to kernel reference at integer AND fractional frames.
   Position/basis/quaternion comparisons need documented tolerances and sign-equivalence for
   quaternions. Don't substitute one end-effector distance for all pose correctness.
3. Check bind identity, CPU/GPU-palette agreement, top-four normalization, invalid weights,
   over-capacity rigs and singular transforms. Do not hardcode Survey counts as general limits.
4. Add animated triangle picking, UV-corner/morph remapping and command/drag transaction
   sequences as those features arrive. Test no mutation of original bind/source data.
5. For regression capture, seed randomness and emit the failing seed/input, expected and actual
   values. Start with explicit per-test tolerances (existing simple fixtures use about 1e-4
   kernel units/basis error); justify different solver tolerances rather than loosening globally.

**Done:** reproducible numerical cases plus semantic assertions, not only “the screenshot changed.”

## V3 — EGL lighting/material/shadow image checks

**Prerequisites:** V1; M2/M3 when testing texture channels. Existing lighting/shadow tests may
start now. Renderer and fixtures must use the shared `SceneRenderer` and actual shaders.

1. Build owned synthetic asymmetric fixtures: color chart, normal directions, roughness/
   metallic patches, UV seam/mirror, skinned limb and an occluder/receiver shadow pair.
   Generate/save fixture assets offline, not scene content in startup hooks.
2. Lock camera, projection, bounds/framing, light directions/intensities, exposure, resolution,
   pose time and color conversion. Headless output is independent of window size.
3. Use region/pixel probes for meaning: a green emitter is green; UV corners land correctly;
   shadowed receiver is darker than lit receiver without acne bands; flat normal is neutral;
   a rough highlight is broader, not just “different.” Compare against independently computed
   reference values or reviewed fixture baselines, not output generated as its own oracle.
4. Check finite pixels, clipping distribution and GL errors. Test extreme allowed exposure,
   moving skinned casters, shadow-frustum edges, steep slopes and nonuniform scale.
5. Keep separate baselines/tolerances by driver when needed. Use exact hashes only for
   same-environment deterministic repeats; cross-driver rasterization need not be byte-identical.
6. A CPU-renderer comparison requires identical model/material definitions, projection,
   camera, lighting, exposure, tone map, background and masks. Do not crop a perspective shot
   to an orthographic reference and treat its RMSE as parity. If CPU lacks a feature, record it.

**Done:** assertable expected effects + retained fixture/camera metadata and failing artifacts.
Do not redefine current PBR as the game's custom shader or promise pixel parity without this work.

## V4 — Performance, lifecycle and hardware matrix

**Prerequisites:** V1; portable package tests later consume D2/D3.

1. Record load stages separately: source parse, cache restore/rebuild, texture decode, extraction,
   animation preparation, CPU upload submission and GPU completion. Use fixed real/synthetic
   workloads with model, vertex/triangle, batch, bone and texture counts in every report.
2. Warm managed code before allocation measurement. Use `GC.GetAllocatedBytesForCurrentThread`
   around synchronous evaluation on the same thread. Do not include logging/test harness work
   in the measured loop or claim a Task's work was measured by another thread's counter.
3. Headless tests may use `gl.Finish` at measurement boundaries for completed GPU work. They
   do not measure monitor FPS, input latency or compositor/vsync behaviour. Those need a
   separately authorized interactive test, not a focus-stealing automatic fallback.
4. Stress repeated project load/reload/swap/dispose, cancellation and failed material/clip jobs.
   Record retained CPU/GPU resources; check released resources aren't referenced by snapshots.
5. Maintain a matrix: Linux/driver/GPU/GL/EGL, supported asset types/features, explicit failed
   and untested cases. Do not infer cross-platform compatibility from one NVIDIA machine.
6. Performance guardrails must compare like-for-like workloads with stated jitter allowance.
   Cold-build speedups require cache-disabled measurements; cache restore is a different metric.

**Done:** repeatable commands, raw logs, stage definitions and resource counts. No unmeasured
“butter smooth” claim and no baseline updates merely to hide a new slowdown.
