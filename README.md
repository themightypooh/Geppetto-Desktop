# Geppetto desktop viewer

## Implementation handoffs

Start at [handoffs/README.md](handoffs/README.md) for the eight non-UI work areas, split into
35 bounded tickets with dependencies, exact source/API pointers, safety rules and acceptance
tests. [WORKER-PROMPT.md](handoffs/WORKER-PROMPT.md) is the copy-paste assignment template.

**All 35 non-UI first-wave tickets are implemented and verified.** The areas are E (retained sessions,
history, clip and save edits), A (parametric, morph, part-track, constraint and live-IK
support), G (gizmo math, pose and IK targeting, viewport handles), I (export, FBX/DMX,
animation feasibility, clip conversion), M (materials, UVs, tangents, Survey mapping and the
resource budget), P (asset resolution, projects, dependency watching, preferences/autosave),
D (inventory, packaging, installed-artifact smoke) and V (test harness and invariants,
deformation, headless image checks, lifecycle). Per-ticket evidence is in
[BOARD.md](handoffs/BOARD.md).

**Two things that are true now and were not when that sentence was written:**

- A **second wave of modelling services** (remesher, UV edit, cloth, shrinkwrap, weight paint,
  auto-weight, morph editing, data transfer, particles, geometry nodes, advanced IK, euphoria,
  hair) has landed as tested backend code, but **none of it is reachable from the viewer yet**.
  "Tested" is not "shipped"; see BOARD.md section 2.
- The **renderer is still an LDR forward pipeline**. No anti-aliasing, no real image-based
  lighting, broken screen-space effects, no temporal accumulation. A renderer wave is scoped
  in [handoffs/30-renderer-quality.md](handoffs/30-renderer-quality.md).

Presentation work (timeline, material slot editor, undo/redo buttons, export and
preferences dialogs) is still outstanding, and is what makes the landed services reachable.

The cabinet's right drawer is the **tools** drawer now: a pencil rolls about on a steel
tray, and clicking it brings the 2D drawing app (Linework) up, or opens one of its
drawings from the gallery underneath. The outliner cards that used to live in that drawer
are gone; `host.py draw [PATH]` does the same from the command line.

Under the pencil sits the **MODELING** folder, where finished models are filed
(`~/.local/share/geppetto/modeling/<name>/<name>.effigy` with `sticker.png` and `kept.json`;
finished AI runs file their sticker in `research/` as before). Hover it and the cover tips
open; click it and the cover drops while paper files with each name rise out of it (orange
edge = a model you kept, RUN = an AI run; the one on screen is outlined). The scroll wheel
runs through them; click one to open it. The tool card's **KEEP** files your edit there:
a model opened from the folder is saved over in place, anything else gets a new folder named
for what it is. Bench edit copies (`research/bench-*`) no longer show in the runs list.
Socket: `keep [NAME]`, `folder`, `openfiled N`.

The **tool wheel** over the viewport (`cabinet/ToolWheel.qml`) shows which tool made the
change on screen. It moves in lockstep with the model: on every revision the viewport
shows, during a live run, a replay/scrub or a bench edit, it lights that step's category
and tool at once and names the script line. Between revisions it holds still, apart from
the countdown ring, and it goes when the model leaves. WHEEL in the tools drawer opens it
by hand. See [handoffs/33-tool-wheel.md](handoffs/33-tool-wheel.md).

## New here? Start with the guide

Geppetto is an AI model maker you can **see every step of the way**: describe what you
want, watch it being built step by step in plain words, step in at any step (pause,
single-step, redirect), and keep a model that stays yours and editable. The five stages
— DESCRIBE → PLAN → BUILD → STEP IN → KEEP — are written down once in
`cabinet/guide.py`, and the cabinet shows them three ways:

- **First run:** the cabinet opens on a four-page guide card (pitch, pipeline, a guided
  tour that replays a finished build for free, and the few controls that matter). It
  never auto-opens again; `python3 cabinet/host.py guide` or the GUIDE key reopens it.
  Seen/finished state lives in `~/.config/geppetto/guide.json`.
- **Always:** a pipeline tape along the viewport's bottom edge lights the stage the
  work is on and says the current step in plain words.
- **In the package:** `packaging/FIRST-RUN.md` ships in the bundle next to a
  `./geppetto` launcher — since the cabinet was bundled, the packaged product is the
  model maker itself, not just the standalone viewer.

## Running the current prototype

Run `dotnet run -c Release` to view Survey's player (suit plus its separate authored head/neck). Pass `--model path/to/file.effigy` or `--model path/to/file.fbx` for other sources. The source assets are never edited. Materials resolve the **declared standard texture channels** of the source — base colour, normal, roughness, metallic, emission, occlusion — with sRGB upload for colour and linear upload for data. They are **not** the game's `ps1_lit` shader; see the unsupported-features list below.

Models open in bind pose. `--animate` opts into the procedural demo; `--clip path.riganim` plays a real clip. No motion library is generated during startup. Camhead's offline generator remains in `BaseMotionLibrary`, but its former filename-only clip cache was unsafe and is no longer loaded automatically.

Effigy documents use `StudioCache`: a cache miss rebuilds the feature tree, which can take many seconds. FBX imports bypass that cache. The kernel has a separate deterministic assembly so app-only changes don't invalidate Effigy geometry caches. No build artifacts need to be deleted between launches.

- **Orbit:** left drag to rotate, wheel to zoom.
- **Fly:** hold **right mouse** to look and fly (slower look, inverted horizontal axis); **W/A/S/D** to move, **Q/E** to descend/ascend, **Shift** to sprint. **Wheel** adjusts fly speed while right mouse is held. Releasing right mouse returns to orbit at the new view.
- `--frames N --size 1024x1024 --screenshot /tmp/opencode/player.png` captures at exactly the requested resolution using an offscreen framebuffer, independent of window-manager size limits, then exits after N model frames.
- `--time 0.5` samples a reproducible animation time for the first capture. `--list-bones` lists the skeleton without opening a window. `--help` lists options; unknown/invalid arguments fail rather than silently opening an endless viewer.
- `--stats` reports steady-state `frame_ms` separately from `startup_frame_ms` (load/upload/capture). `--no-vsync` is available for uncapped measurements.

## Renderer

One renderer serves both the interactive window and the windowless EGL captures:
`SceneRenderer.Draw` (`SceneRenderer.cs`). The pass order is declared once, in
`RenderPassOrder` (`RenderPass.cs`):

1. **Shadow** — a 2048² key-light depth pass, 2. **DepthPrePass** — scene depth plus
hemisphere SSAO, 3. **Lit** — the shading pass, 4. **PostChain** — resolve, bloom, depth
of field, view transform, and the single display encode, then a depth blit back to the
caller's target.

### Colour convention

- **Colour and emission images are sRGB inputs**; roughness, metallic, normal, occlusion and
  mask images are **linear data**. Sampling decodes accordingly.
- **All lighting, image-based lighting, screen-space effects, bloom and depth of field run in
  linear Rec.709.** The lit pass writes **linear HDR into an `Rgba16f` target** — it does not
  tone-map and does not encode.
- The **view transform** (AgX by default, matching Blender's default view with look "None"),
  the white balance, and the **single sRGB display encode** happen exactly once, in the
  composite (`BloomPass.Composite`). There is no second encode anywhere in the renderer.

`ViewSettings` (`BloomPass.cs`) is the display-side configuration, mirroring Blender's Color
Management panel: view transform, look, exposure in stops, gamma, white balance temperature
in kelvin (6500 K / D65 is neutral) and tint. The CLI exposes these as `--view-transform`,
`--look`, `--view-exposure`, `--gamma`, `--white-balance` and `--tint`; `--exposure` remains
the separate *scene* exposure multiplier applied in the lit pass.

What is already right: real material channels with sRGB upload, mipmaps and 8× anisotropy;
a content-hash texture cache; per-corner UVs and tangents with handedness; a 20-float vertex
stride; the 220-bone GPU palette; energy-conserving GGX; soft PCSS-style shadows; a
per-process texture/byte/reload budget; an HDR linear pipeline with a single display encode;
AgX with a measured match to Blender's grey ramp.

What is **not** right yet, stated plainly:

- Screen-space reflections, ambient occlusion, temporal anti-aliasing, transmission,
  subsurface, volumetrics and the reference-prop path are **implemented but not yet
  independently certified** against a reference image; see [BOARD.md](handoffs/BOARD.md)
  section 3 for their per-ticket state.
- Blended batches are neither sorted nor depth-write suppressed.
- A cropped image RMSE from a different camera/projection is not evidence of renderer
  parity.

The upgrade plan — a modern EEVEE-class renderer, one step below Blender — is scoped
ticket-by-ticket with effort estimates and staffing lanes in
[handoffs/30-renderer-quality.md](handoffs/30-renderer-quality.md), and tracked on
[BOARD.md](handoffs/BOARD.md) section 3.

## Verification and limits

Use `python3 tests/verify.py --mode cpu` for isolated CPU verification, or
`python3 tests/verify.py --mode required-egl --require-local-fixtures` to also require
windowless hardware-GPU checks. See [tests/README.md](tests/README.md) for fixture policies,
test selection, JSON evidence and how to register new tests. Missing optional assets are
reported as SKIP, not silently omitted; required missing fixtures fail.

The CPU suite currently registers **503 fixtures** (496 passing, 7 EGL-gated skips on a
machine without the EGL gate enabled). Rerun it rather than quoting that number.

Run `dotnet run --project tests/Desktop.Tests.csproj -c Release` for desktop regressions, or `../geppetto-verify/run.sh` for kernel tests plus cold builds and two GPU launches. The latter explicitly tests the pod document's geometry cache, not whichever model is the viewer default.

`python3 tests/gpu-smoke.py` runs three **windowless EGL GPU** checks of Survey's player: two bind-pose captures must be byte-identical and a fixed-time demo capture must differ. Each image must be exactly 1024×1024. Logs and screenshots are retained under `/tmp/opencode`. No SDL, X11 or Wayland connection is created; there is no visible-window fallback.

For a single no-focus render: `dotnet run -c Release -- --headless --frames 1 --size 1024x1024 --screenshot /tmp/opencode/player.png`. `--exposure 1.2` adjusts linear brightness. The viewport and headless renderer share the same world-fixed key/fill/rim lights and GPU self-shadow pass. Headless GPU timings are not monitor/present FPS.

Bone-key playback (including limits) is tested against the kernel and measured for zero per-step allocations. Live IK runs through `LiveIkSolverBuffers` (A4) so the hot path does not allocate; the kernel's `RigAnimFile.SampleLocal` remains the fallback for the parts A4 does not cover (spline/object-space curves, limits, root motion). Morph tracks (A2), part and camera tracks (A6), clip blending (A3), timeline events (A5) and constraint parity (A7) are implemented and tested. Skeletons exceeding the 220-bone GPU palette are rejected, not silently clamped.

Picking uses animated triangles, not bind-pose AABBs. **Dragging handles** are implemented (G4): typed transform handles are drawn and hit-tested from one source, and auto-key (`K`) writes FK and live-IK keys through the clip editor as a single undoable entry. Editing, save/undo (E1–E5), export (I1–I4), project/preferences infrastructure (P1–P4) and packaging (D1–D3) are implemented and tested as backend services; the viewer exposes the pose/handle path, while the export, watcher and autosave surfaces still need UI to reach them.

This is still a viewer/playback prototype with a modelling backend, not a finished desktop
modeller/animator: the second-wave modelling services are not yet in the viewport, and the
renderer's screen-space, temporal and material features are not yet independently
certified. A cropped image RMSE from a different camera/projection is not evidence of
renderer parity.

### Unsupported Survey shader features

Survey's player materials are authored for the game's `ps1_lit` shader, not a standard PBR
model. The viewport resolves the declared **standard texture channels only** (base colour,
normal, roughness, metallic, emission, occlusion) as linear/sRGB data and multiplies each by
its factor exactly once. These authored features are **not** reproduced, and are reported as
`MaterialDiagnostic` warnings rather than silently dropped:

- **Palette / colour-grading ramps** (`g_vPs1Palette*`, `g_flPs1*` tint and banding controls)
  — no palette lookup, posterisation or banding is applied.
- **Wear, grime and boot effects** (`g_flPs1Wear`, `g_flPs1Grime`, `g_flPs1Boot*`) — basic
  wear-colour edge blend and grime overlay are now applied; boot wear specifics are not.
- **Shader glow / emissive ramps** (`g_flPs1Glow*`) — glow multiplier now modulates emission;
  screen-space/ramp glow is still not applied.
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

### Colour management

All lighting, bloom and depth of field run in linear Rec.709 HDR. Colour/emission
textures decode from sRGB; normal/roughness/metallic/occlusion maps remain linear.
The final composite applies the view transform and encodes to sRGB once.

The default view is AgX, look None, display exposure 0 stops, gamma 1, white balance
6500 K and tint 0. These settings are shared by the native viewer, `--serve` and
`--headless` captures. For example:

```sh
dotnet bin/Release/net8.0/marionette-desktop.dll --headless --frames 1 --view-transform agx --look punchy --view-exposure 1 --gamma 1 --white-balance 6500 --tint 0 --screenshot /tmp/geppetto-agx.png
```

`--view-transform agx|standard` selects the display transform; `--look none|punchy`
sets the AgX look. `--view-exposure` is in stops [-16,16] and applies at the display
stage; the existing `--exposure` remains a scene-light multiplier. `--gamma` accepts
[0.1,5]. `--white-balance` accepts [1667,25000] kelvin: lower values correct a warm
illuminant by cooling the image; higher values warm it. `--tint` ranges from -1
(green) to 1 (magenta). White-balance gains preserve neutral-grey linear luminance;
coloured surfaces can change luminance. The Rec.709 gain approximation is not an
exact Blender chromatic-adaptation match at extreme temperatures.
