# Audit & Repair: viewport paint, fur, live steps — final verification (2026-11-13)

Task: `audit-and-repair-viewport-paint-fur-and-live-steps` (Geppetto Desktop board).
Initial audit & core repair batch: codex (`01a1d706-afc9-7cc2-a19a-a6b1b9170e9f`, hit ChatGPT usage limit at 18:46 EST).
Completion, UV-flip/backface repair & full GPU verification: antigravity (~19:49 EST).

## Root causes identified & repaired

1. **Per-body `PaintCanvas` slot collisions (`core/CpuMesh.cs`, `core/DocumentSession.cs`) — FIXED & GPU-VERIFIED.**
   - Every body defaulted to `MaterialSlot = 0`. `ApplyPaintTextures` previously keyed paint by saved material slot with first-body-wins, so multi-body painted models (like `squirrel.effigy`'s 28 painted bodies) either showed only the first body's paint or dropped paint when using incremental `FromStudioPieces`.
   - `CpuMesh.FromStudio` now assigns render-only material slots per painted body (`SourceMaterialSlots` + `PaintCanvases`) while keeping saved `face.Material` slots intact, and `FromStudioPieces` is bypassed when any visible body carries `Mesh.Paint`.
2. **`PaintCanvas` vertical UV-flip on GL upload (`core/DocumentSession.cs`) — FIXED & GPU-VERIFIED.**
   - `PaintCanvas` / `NormalBake.Rasterise` / `PaintFeature` rasterizes `UV.y = 0` at `row = 0` (`V` down), whereas `ViewportMesh.CreateTexture` flips every `DecodedImage` vertically (`bottomFirst`) assuming `PngReader`'s top-first (`row = 0` is `V = 1`) convention.
   - As a result, UV-unwrapped painted bodies (such as `squirrel.effigy`'s `Torso` back gradient) sampled `(u, 1 - v)` upside-down across the atlas.
   - `DocumentSession.ApplyPaintTextures` now converts `PaintCanvas` channel buffers via `PaintImage(width, height, rgba)` into `DecodedImage`'s top-first row order before GL upload, and marks unbound `.effigy` studio batches `DoubleSided = true` so parallel-plane lofts/open shells (such as `squirrel.effigy`'s `Tail`) do not shade dark on back-facing spans.
3. **Unpainted vertex-color alpha multiplying black (`RendererShaders.cs`) & vertex-paint cache hash (`core/CpuMesh.cs`) — FIXED & GPU-VERIFIED.**
   - `RendererShaders.cs` now treats `aColor.a <= 0.0` as unpainted white (`vec3(1.0)`), and `CpuMesh.GeometryHash` mixes in `VertexColors` so live piece caching invalidates on vertex-paint edits.
4. **Fur shell masking & geometry-cache persistence (`RendererShaders.cs`, `ViewportMesh.cs`, `KeyShadowMap.cs`, `kernel/Effigy/Features/ClothingFeatures.cs`, `kernel/Effigy/Features/StudioCache.cs`) — FIXED & GPU-VERIFIED.**
   - `FurShells` encoded shell height/coat mask in vertex color RGB, which previously multiplied directly into surface color without shell alpha masking. Added `FurMaskSrc` / `applyFurMask` and `uFur*` uniforms in main and shadow passes, and bumped `StudioCache.Version = 3` to persist/restore `FurFeature.ResolvedSlot`.
5. **AI render snapshot & isolated script-worker step publishing (`AiSuiteTools.cs`, `AiLivePreview.cs`) — FIXED & VERIFIED.**
   - `AiSuiteTools.Render` now builds a full `DocumentSession` snapshot (`BuildSnapshot().Mesh`) so headless/AI renders include paint, materials, and fur.
   - Isolated worker dispatch now handles script tools (`measure_model`, `rig_skeleton`, `edit_mesh`, `animate`, `bake_export`, `map_*`), publishes one live revision per command, and syncs built bodies back to the parent via `StudioCache.Load`.
   - `AiLivePreview` replays existing journal steps in order (`nextRevision = 0`) when attaching to a run.
6. **Directory symlink cycle in `.vmdl` material discovery (`kernel/Effigy/VmdlReader.cs`) — FIXED & VERIFIED.**
   - `EnumerateModelDocs` now skips `FileAttributes.ReparsePoint` directories.
7. **Launcher source-freshness rebuild (`tools/build-needed.py`, `~/.local/bin/geppetto`) — FIXED & VERIFIED.**
   - Added `tools/build-needed.py` and wired `~/.local/bin/geppetto` to rebuild when `.cs`/`.csproj` sources are newer than `.source-build.stamp`.

## Verification evidence

- **Full test suite (RTX 5060 Ti headless EGL):**
  - `1446 passed, 0 failed, 7 skipped; 1453/1453 selected/registered` (`/tmp/geppetto-finish-suite.json`).
  - `viewport-repair` (5/5 PASS, including `PaintCanvas` V-flip row check and hardware EGL fur shell masking), `two-sided` (7/7 PASS), `material-gpu` (3/3 PASS), `kernel-parity` (7/7 PASS).
- **Kernel parity inventory:**
  - `python3 tools/kernel-parity/inventory.py check --baseline handoffs/kernel-parity-inventory.json` → `OK: inventory matches baseline`.
- **Visual render verification (`squirrel.effigy`):**
  - Headless EGL screenshot rendered to `/tmp/squirrel-desktop-fixed.png` (`800x600`, 28 bodies, 45,420 tris, 28 material batches) and visually inspected: torso dorsal-to-ventral gradient, tail loft shading, and all 28 per-body paint atlases render cleanly and accurately.
