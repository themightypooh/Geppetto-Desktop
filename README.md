# Geppetto desktop viewer

## Implementation handoffs

Start at [handoffs/README.md](handoffs/README.md) for the eight non-UI work areas, split into
35 bounded tickets with dependencies, exact source/API pointers, safety rules and acceptance
tests. [WORKER-PROMPT.md](handoffs/WORKER-PROMPT.md) is the copy-paste assignment template.

**All 35 non-UI tickets are implemented and verified.** The areas are E (retained sessions,
history, clip and save edits), A (parametric, morph, part-track, constraint and live-IK
support), G (gizmo math, pose and IK targeting, viewport handles), I (export, FBX/DMX,
animation feasibility, clip conversion), M (materials, UVs, tangents, Survey mapping and the
resource budget), P (asset resolution, projects, dependency watching, preferences/autosave),
D (inventory, packaging, installed-artifact smoke) and V (test harness and invariants,
deformation, headless image checks, lifecycle). Per-ticket evidence is in
[BOARD.md](handoffs/BOARD.md); what is left is presentation work only — see
[RESTART.md](handoffs/RESTART.md) for the closing state and remaining UI scope.

## Running the current prototype

Run `dotnet run -c Release` to view Survey's player (suit plus its separate authored head/neck). Pass `--model path/to/file.effigy` or `--model path/to/file.fbx` for other sources. The source assets are never edited. Materials are **flat preview values, not the authored texture maps or the game's shader**.

Models open in bind pose. `--animate` opts into the procedural demo; `--clip path.riganim` plays a real clip. No motion library is generated during startup. Camhead's offline generator remains in `BaseMotionLibrary`, but its former filename-only clip cache was unsafe and is no longer loaded automatically.

Effigy documents use `StudioCache`: a cache miss rebuilds the feature tree, which can take many seconds. FBX imports bypass that cache. The kernel has a separate deterministic assembly so app-only changes don't invalidate Effigy geometry caches. No build artifacts need to be deleted between launches.

- **Orbit:** left drag to rotate, wheel to zoom.
- **Fly:** hold **right mouse** to look and fly (slower look, inverted horizontal axis); **W/A/S/D** to move, **Q/E** to descend/ascend, **Shift** to sprint. **Wheel** adjusts fly speed while right mouse is held. Releasing right mouse returns to orbit at the new view.
- `--frames N --size 1024x1024 --screenshot /tmp/opencode/player.png` captures at exactly the requested resolution using an offscreen framebuffer, independent of window-manager size limits, then exits after N model frames.
- `--time 0.5` samples a reproducible animation time for the first capture. `--list-bones` lists the skeleton without opening a window. `--help` lists options; unknown/invalid arguments fail rather than silently opening an endless viewer.
- `--stats` reports steady-state `frame_ms` separately from `startup_frame_ms` (load/upload/capture). `--no-vsync` is available for uncapped measurements.

## Verification and limits

Use `python3 tests/verify.py --mode cpu` for isolated CPU verification, or
`python3 tests/verify.py --mode required-egl --require-local-fixtures` to also require
windowless hardware-GPU checks. See [tests/README.md](tests/README.md) for fixture policies,
test selection, JSON evidence and how to register new tests. Missing optional assets are
reported as SKIP, not silently omitted; required missing fixtures fail.

Run `dotnet run --project tests/Desktop.Tests.csproj -c Release` for desktop regressions, or `../geppetto-verify/run.sh` for kernel tests plus cold builds and two GPU launches. The latter explicitly tests the pod document's geometry cache, not whichever model is the viewer default.

`python3 tests/gpu-smoke.py` runs three **windowless EGL GPU** checks of Survey's player: two bind-pose captures must be byte-identical and a fixed-time demo capture must differ. Each image must be exactly 1024×1024. Logs and screenshots are retained under `/tmp/opencode`. No SDL, X11 or Wayland connection is created; there is no visible-window fallback.

For a single no-focus render: `dotnet run -c Release -- --headless --frames 1 --size 1024x1024 --screenshot /tmp/opencode/player.png`. `--exposure 1.2` adjusts linear brightness. The viewport and headless renderer share the same world-fixed key/fill/rim lights and GPU self-shadow pass. Headless GPU timings are not monitor/present FPS.

Bone-key playback (including limits) is tested against the kernel and measured for zero per-step allocations. Live IK uses `RigAnimFile.SampleLocal` for correct fractional-frame evaluation; **that path still allocates**. Morph/part/camera tracks are not implemented and fail explicitly instead of being ignored. Skeletons exceeding the 220-bone GPU palette are rejected, not silently clamped.

Picking uses animated triangles, not bind-pose AABBs. The coloured bone axes are **not draggable transform gizmos**. This is still a viewer/playback prototype, not a complete desktop modeller/animator: authoring, save/undo, full material/texture support, packaging, and renderer-reference parity remain unfinished. A cropped image RMSE from a different camera/projection is not evidence of renderer parity.

### Unsupported Survey shader features

Survey's player materials are authored for the game's `ps1_lit` shader, not a standard PBR
model. The viewport resolves the declared **standard texture channels only** (base colour,
normal, roughness, metallic, emission, occlusion) as linear/sRGB data and multiplies each by
its factor exactly once. These authored features are **not** reproduced, and are reported as
`MaterialDiagnostic` warnings rather than silently dropped:

- **Palette / colour-grading ramps** (`g_vPs1Palette*`, `g_flPs1*` tint and banding controls)
  — no palette lookup, posterisation or banding is applied.
- **Wear, grime and boot effects** (`g_flPs1Wear`, `g_flPs1Grime`, `g_flPs1Boot*`) — no
  procedurally accumulated dirt, damage or edge wear.
- **Shader glow / emissive ramps** (`g_flPs1Glow*`) — emission is the authored emission map ×
  emission factor, not a screen-space or ramp glow, and never becomes a light source.
- **Engine texture filtering / signal controls** (`F_TEXTURE_FILTERING`, `g_flSignal*`) —
  only the descriptor's own min/mag filter and wrap modes are honoured.

None of these are "automatic" PBR features: a material that relies on them will preview with
the flat standard channels, and the warning names the exact `g_flPs1…` key and `.vmat` so the
gap is visible in logs. Because an RGBA `.png` alpha channel alone never implies transparency,
blending is a separate capability and is not inferred. Headless captures and pixel probes
(`egl-image`, `material-channel`) are retained as the evidence that the supported channels
stay correct; they do not claim parity with the game shader.
# AI terminal mode

Provider/local-model connections, native modelling tools, optional research-to-brief,
and independent Judge Mode are available through `--ai`. See [AI.md](AI.md) for setup,
examples, safety limits, test evidence, and remaining limitations. This is a CLI/backend
addition, not a new UI panel.
