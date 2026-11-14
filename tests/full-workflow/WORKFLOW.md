# Full asset workflow — step to bot-command inventory

Owner ticket: `prove-desktop-full-asset-workflow-through-bot-commands`
(claimed by muse-3-desktop-a; the live end-to-end run is the final
integrator's job. Nothing in this folder runs a build, a render, or the
workflow itself.)

The scripted workflow is `workflow.txt` (main BuildScript) +
`workflow-anim.txt` (anim script, separate runner — see W2) +
`run-workflow.sh` (bench driver + headless evidence capture).
`evidence-checklist.md` is the per-step expected evidence.

## Step -> command inventory

Legend: EXISTS = verified in source (file:line or verb usage string);
PARTIAL = the route exists but a named piece is missing/unverified;
MISSING = no documented Desktop route. "Bot tool" = `AiSuiteTools` tool name;
"script" = BuildScript verb; "bench" = `--bench` stdin command.

| # | Workflow step | Route | Status |
|---|---|---|---|
| 1 | Furnished room prop (table, chairs, shelf) | script `add Primitive shape=Box ... name=...`; bot `add_feature` | EXISTS |
| 2 | Viewmodel with moving parts | script `add Primitive ...` + `bone Lever parent=...` + `chain` | EXISTS (rig part); animation of the lever is W2 |
| 3 | Sculpt one organic fixture | script `add Sculpt body=...` + `sculpt Body dab at=... dir=... brush=draw radius=... strength=... [mirror=x] [topomirror=on]`; multires via `add Sculpt multireslevels=N` + `reproject Low High [levels=N]` | EXISTS |
| 4 | Retopo | script `retopo Body [targets=4000,1500,500]`; bot `remesh_model` (Quad/Decimate/Voxel) | EXISTS (built-in); OpenVDB backend in-progress (muse-3) — not required for the verb to run |
| 5 | Bake | script `bake Low High folder` / `bake * folder [size=1024]`; bot `bake_export` | EXISTS; MikkTSpace tangents landed (r-glb lane) but `MikkTangents.Install()` still unwired in `Program.Main` (integrator flag) |
| 6 | Paint maps | script `paintmaps Body out/tex [name=x]`; `genmaps out/tex [preset=skin] [size=1024]`; bot `bake_export` | EXISTS |
| 7 | Paint (texel strokes) | script `add Paint bodies=... resolution=...` + `texpaint Body path=... channel=color color=... radius=... strength=...` | EXISTS (color channel verified); other channels + ClipToBelow/adjustments are the paint-clipping ticket, in-progress by a sibling — workflow uses color only |
| 8 | UV edit | script `add UVProject bodies=... mode=Unwrap` + `uvcheck`; bot `unwrap_uvs`, `validate_uvs` | EXISTS (auto unwrap); PARTIAL: manual island edits exist as `UvEditService` but have NO script verb and NO bot tool — auto-unwrap only in this workflow |
| 9 | Rig | script `rig human height=...` / `rig citizen` / `rig biped` / `rig quadruped`; `bone`, `chain`, `bind Body method=smooth`, `skin`, `weight Body smooth`; bot `rig_command` | EXISTS |
| 10 | Animate (author keys) | anim script `clip name=... fps=30 frames=N`, `key bone=... frame=N pos=... rot=...`, `save_clip path=...` (`BuildScriptAnim.Run`) | MISSING as a bot route — see W2. The script text exists (`workflow-anim.txt`); no CLI/bot tool runs it |
| 11 | Playback check | script `play clip.riganim` / `play off`; bot `anim_command` (play/lint only) | EXISTS (validate + report, not authoring) |
| 12 | Save / reopen | script `save path.effigy [strict=1]`, `load path.effigy`; bench `--doc` | EXISTS; `save strict=1` refuses on hard validation fails |
| 13 | Undo / redo | bench stdin `undo` (emits `undone` event; journal keeps `# undo:` line) | EXISTS via bench only; there is NO BuildScript `undo` verb and NO AiSuiteTools undo tool |
| 14 | Geometry gate | script `validate [Body\|all] [target=game\|character\|print] [shells=N] [genus=N] [minwall=X]` (hard-fails the script); bot `validate_topology`, `validate_size`, `validate_symmetry`, `heal_mesh` | EXISTS |
| 15 | Material preservation | script `material SLOT [preset=...] [color=...] [rough=...] [metal=...] [emit=...] [normal=...] [ao=...] [emitmap=...]`; bot `material_preset`, `validate_materials` | EXISTS |
| 16 | Scale / axes | script `measure`, `measurebody`, `distance`, `symmetry`, `clearance`; bot `measure_model`, `validate_size` | EXISTS |
| 17 | Frame sampling | script `render ... clip=...` (render verb takes a clip); headless `--frames N --screenshot out.png`; bot `render` tool (windowless evidence set) | EXISTS |
| 18 | Elapsed time + peak memory | CLI `--stats` (one `frame_ms` line per frame); bot `get_performance_metrics`; `core/ResourceBudget.cs` | PARTIAL: frame time exists; a peak-memory number in CLI output was NOT confirmed — capture it from `get_performance_metrics` and record the tool output verbatim |
| 19 | Export + re-import | script `export folder [assets=root] [name=x] [skin=...] [fbx=0\|1] [lods=...]` (writes s&box `.vmdl`/`.obj`/`.dmx`); `exportvalidate folder [...]`; GLB via `ExportService` only | PARTIAL — see W1 |
| 20 | Headless render evidence | CLI `--headless --frames N --size WxH --model path.effigy --screenshot out.png [--stats]`; `tests/gpu-smoke.py` (required-EGL mode); bot `render` tool; all share `SceneRenderer` | EXISTS |

## Gaps (precise; these are the workflow's real blockers)

- **W1 — no bot route writes a .glb.** Same as export-verify G1: `validate_export`
  checks only, `export_preset` is plan-only, BuildScript `export` writes
  vmdl/obj/dmx. The workflow's "export and independently re-import" step can
  run today for OBJ (export verb) and for GLB only via `ExportService` driven
  from a test harness or the Poohdot plugin. Re-import: `GlbImport` is static
  only (refuses skins), FBX import exists, OBJ/STL/PLY have NO importer.
  Until a `glb` writer lands on a bot route, the GLB leg is integrator-driven.
- **W2 — no bot route authors animation.** Same as export-verify G2:
  `BuildScriptAnim.Run` (clip/key/save_clip) has no production caller; only
  tests call it. `anim_command` runs `play`/`lint` only; `timeline_control`
  records notes only. The workflow's animate step is scripted
  (`workflow-anim.txt`) but needs the missing driver (bot tool or
  `--anim-script` CLI path; files `AiSuiteTools.cs` / `Program.cs` /
  `ScriptLive.cs` — coordinator-owned).
- **W3 — manual UV island edits have no command route.** `UvEditService`
  exists (`core/UvEditService.cs`) and the manual-UV ticket landed the kernel
  side, but no BuildScript verb and no bot tool call it. The workflow uses
  auto-unwrap (`add UVProject mode=Unwrap`) and records this as a known gap.
- **W4 — dependencies still in flight at writing time (2026-11-13 ~10:40).**
  Paint clipping (sibling, this batch) gates step 7's non-color channels;
  animation reconcile (sibling, this batch) gates step 10's clip handling;
  rigs/morphs GLB export (muse-3-desktop-glb, DONE, not build-verified) gates
  step 19's GLB leg; OpenVDB remesh (muse-3, in-progress) gates step 4's
  OpenVDB path only, not the built-in retopo; MikkTSpace (same owner, DONE,
  not build-verified) gates step 5's tangent agreement. None of these block
  the scripted workflow from being *written*; they block its *run* from
  passing until the final build verifies them.

## What is deliberately NOT in this workflow

- Mechanical assemblies (Assemblies 01–15 own that gate; do not duplicate).
- UI polish, Ruinfolk gameplay, s&box/VMDL-as-a-requirement (the workflow is
  engine-independent; the s&box `export` verb is used only as one export leg).
- Any hidden test-only mutation or source-file hand edit to manufacture a
  result: every step goes through a documented route above, or is marked
  MISSING/PARTIAL here.
