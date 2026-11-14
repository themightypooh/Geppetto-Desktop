# Evidence checklist — full asset workflow run

Run by the final integrator with `run-workflow.sh` (or its steps by hand).
Every item names the file/line that proves it; "screenshot alone" never
passes a step. Times and memory are recorded from the run, never estimated.

## Per-step evidence

| Step | Pass evidence (all required) |
|---|---|
| 1 Room prop | `out/workflow.effigy` contains bodies TableTop, TableLeg*, ChairSeat, ChairBack, ChairLeg*, Shelf (check via `describe` / bench `state`); headless screenshot shows the table + chair + shelf in one frame |
| 2 Viewmodel | Bodies ToolGrip, ToolHead, ToolLever present; bones ToolRoot, ToolLeverBone present (`rigcheck` clean); lever visibly moves between the walk clip's frames 0/30/60 (sampled poses) |
| 3 Sculpt | `sculpt Hero topomap mirror=x` reports paired count/coverage > 0; Hero vertex positions differ from the pre-sculpt save (compare `measure Hero` before/after); no vertex moved across the mirror plane asymmetrically beyond the dab radius |
| 4 Retopo | `retopo` log reports target counts; Hero face count after <= 4000 (first LOD target); `validate` still passes (closed, 1 shell) |
| 5 Bake | `out/baked/` contains normal + AO maps (non-empty PNGs, 1024x1024); bake log names the low/high pair it used — if it reports "no pair", file the retopo-then-bake gap (see workflow.txt step 5) instead of forcing it |
| 6 Paint maps | `out/tex/` contains the paintmaps set + `hero_*` generated maps; files are non-empty PNGs |
| 7 Paint | Hero has a Paint layer (`describe` shows it); texpaint stroke is in the layer log with revision > 0; re-opened document still shows the stroke (save/reopen check in step 12) |
| 8 UV | `uvcheck` reports island count + coverage; Hero UVs cover (0,0)-(1,1) with no island outside; `add UVProject` log carries the unwrap report |
| 9 Rig/bind/weights | `rigcheck` clean; `bind` + `weight` log lines present; every Hero vertex has >= 1 bone weight and weights sum to 1 (spot-check via `measure`/`describe` or the weight-paint tests) |
| 10 Animate | `out/walk.riganim` + `out/idle.riganim` exist, parse (`lint` clean), walk = 60 frames @30fps, idle = 30 frames @30fps; sampled poses at frames 0/30/60 differ exactly as scripted (pelvis z +2 at frame 30, thigh swing +/-20 deg). BLOCKED until W2's driver lands — record as BLOCKED, not passed, if the driver is still missing |
| 11 Playback | `play out/walk.riganim` validates (no missing-bone errors); headless `--frames` run with the clip samples poses without GL errors |
| 12 Save/reopen | `out/workflow.effigy` re-opens (`load` in a fresh bench) with identical body count, rig, paint layer and clip references; `save strict=1` refused nothing (or the refusal is filed, not ignored) |
| 13 Undo/redo | Bench log shows an `undone` event for a deliberately undone line AND the re-applied line afterwards; the journal contains the `# undo:` line; the final document equals the no-undo run's document (same revision hash/counts) |
| 14 Geometry gate | `validate all target=game shells=1` PASS on every body; no open/degenerate geometry where solids are expected (TableTop, chairs, ToolHead: closed, genus 0); any WARN is listed in the run log with its disposition |
| 15 Materials | Slot 0 look = wood brown rough 0.8 metal 0; slot 1 = dark metal rough 0.5 metal 0.8; exported OBJ MTL (or GLB material, when W1 lands) carries the same factors; no slot silently falls back to default |
| 16 Scale/axes | `measure Hero` height = 72 in; table top at z = 28..30 in; `validate_size` (if run) matches expected width/depth/height within tolerance; axes: +X marker (export-verify fixture) is at +X after export |
| 17 Frame sampling | Headless render with the walk clip samples frames 0, 15, 30, 45, 60; screenshots at 0/30/60 show distinct poses; no dropped/NaN bone transforms in the sampled output |
| 18 Time + memory | `frame_ms` lines from `--stats` for the whole run (cold rebuild time stated separately from frame time); peak memory from `get_performance_metrics` output, pasted verbatim into the run log. No estimated numbers |
| 19 Export/re-import | s&box leg: `out/export/` contains `.vmdl` + `.obj` (+ `.dmx` for the skinned hero) and `exportvalidate` passes. GLB leg (when W1 lands): `verify-glb.py` PASS against a manifest + `import-check.gd` PASS in Godot AND Poohdot, reported separately. Re-import: GLB static re-import via `GlbImport` (names/bounds/materials only — skins refused by design) or FBX re-import with hierarchy check. Record which legs ran; an unrun leg is "not run", never "passed" |
| 20 Render evidence | `--headless --frames 1 --size 512x512 --screenshot` PNGs per fixture/view (front, side, three-quarter), saved under `out/shots/`; GPU smoke (`tests/gpu-smoke.py`) run for the same build; every PNG is non-blank (mean pixel != background) and shows the model |

## Run log contents (the integrator pastes all of this into the board note)

- Commands run (exact) + exit codes
- Bench `@...` JSON events (ready/applied/undone/state) for the undo step
- `validate`, `uvcheck`, `rigcheck`, `exportvalidate` outputs verbatim
- `verify-glb.py` output + `import-check.gd` outputs (Godot + Poohdot separately)
- `--stats` frame_ms lines + `get_performance_metrics` output verbatim
- Screenshots saved under `out/shots/`
- Gaps hit (W1/W2/W3/W4) with their disposition: fixed / filed / deferred
