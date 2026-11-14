# Export verification harness — Godot + Poohdot, no s&box

Owner ticket: `verify-asset-export-into-godot-and-poohdot-without-sbox`
(claimed by muse-3-desktop-a; live verification is the final integrator's job —
nothing in this folder runs Godot, Poohdot, a build, or a render by itself).

## What this folder is

The audit (below) + the fixtures + the checks the final integrator runs, in order:

1. `fixtures/build-fixtures.txt` — BuildScript that builds the three fixtures
   (prop, painted material fixture, rigged two-clip character with morph).
   Run it through `--script` / `--bench` (see `../full-workflow/run-workflow.sh`
   for the driver shape). It writes `fixtures/*.effigy` + the clip files.
2. Export each fixture to GLB via `ExportService` (see "Gap G1" — there is
   currently **no bot-command/CLI route that writes a .glb**; the integrator
   drives it from a test harness or the Poohdot plugin, it is not faked here).
3. `verify-glb.py <file.glb> <manifest.json>` — stdlib-only Python; checks the
   exported GLB against the manifest (units, axes, names, skins, animations,
   morphs, material textures). Exit 0 = all checks pass.
   Manifest: copy `expected-manifest.example.json`, fill in the values the
   fixture build actually produced (bounds, node names, image sizes).
4. `import-check.gd` — copy into a FRESH Godot project (and, separately, a
   fresh Poohdot project) next to the exported `.glb` files, then run headless:
   `godot --headless --path <fresh-proj> --import` (once, so the .glb is
   imported), then `godot --headless --path <fresh-proj> -s import-check.gd --
   --glb=res://imports/hero.glb --label=godot`. Same command under Poohdot
   (`/home/poo/.local/bin/poohdot`) with `--label=poohdot`. Report Godot and
   Poohdot **separately**; do not merge the logs.
5. Re-import check (Desktop side): `GlbImport.ReadFile` on the exported GLB
   (static only — see below). No script for this yet; the integrator adds one
   C# check or runs the existing `glb` test group and records names/bounds/
   materials surviving the round trip.

Do NOT run any of this in the real game projects. Fresh projects only.
Tamagatchi (`~/games/godot/tamagatchi/godot`) is never a test target.

## Audit — what is real (read in source, 2026-11-13)

Real export path (use this): `core/ExportService.cs` + `core/GlbExport.cs`,
capability-checked (`EvaluateCapabilities`), atomic staging, path sanitization,
reads retained CPU source only. Formats: OBJ, GLB, Effigy, RigAnim.

- GLB: glTF 2.0, **metres, Y-up** (kernel inches x 0.0254, Z-up -> Y-up, converted
  back on import). Skin: one shared skin, top-4 weights renormalised,
  unweighted vertex -> joint 0 weight 1, out-of-range bone refused.
  Morphs: POSITION + NORMAL deltas, weights 0 at rest, `extras.targetNames`.
  Animation: **one glTF animation per clip** (`GlbExport.WriteAnimated`,
  LINEAR T/R/S per bone at clip fps; Part/Camera/IK-goal tracks are folded into
  sampled bone transforms, not written). `ExportService` passes only the
  **session clip** (one clip) — multi-clip GLB is the export-rigs lane's work
  (muse-3-desktop-glb, DONE 2026-11-13, not build-verified).
- Materials on GLB (current, after the rigs lane): baseColor + metallic-roughness
  (R=255 occlusion-free, G=roughness, B=metal), normal texture (DirectX green
  flipped to OpenGL green), occlusion texture, emissive factor + emissive
  texture, opacity stamped into base-colour alpha with `alphaMode: "BLEND"`.
  (An older note claiming `BuildGlbMaterial` drops emit/opacity/AO is stale —
  `core/ExportService.cs` ~996-1104 wires all of them now.)
- OBJ: inches, Z-up, UV V-flipped; no rig/skin/morph/animation by format.
- FBX: static rigged binary FBX only (`core/FbxExportAdapter.cs`); no animation
  in FBX — animated interchange is GLB or native `.riganim`. DMX model/animation
  writes are optional (`FbxDmxExportTests`).
- Re-import: `core/GlbImport.cs` loads a `.glb` end to end (`DocumentLoader`
  too) but is **STATIC ONLY — a file carrying a skin is refused**
  (`GlbImport.cs:26,194-195`). It also refuses Draco, bad magic, truncated
  data, version 1, non-triangle mode, out-of-range indices, sparse accessors.
  So the round trip verifies static geometry + names + bounds + materials +
  textures, NEVER skin or animation on re-import.
- MikkTSpace tangents: `core/MikkTangents.cs` + `kernel/Effigy/NormalBake.cs`
  (byte-identical to Mobile) landed with the rigs lane; `MikkTangents.Install()`
  still needs wiring in `Program.Main` (integrator flag) and GLB TANGENT vs
  bake agreement is covered by `MikkTangentTests` (not yet registered).
- Kernel `kernel/Effigy/Export/` (`GlTfExporter`, `FbxExporter`, `Exporter`)
  are **stubs**: they write a header/comment only, no geometry. Do not use
  them; the real path is `core/ExportService` + `core/GlbExport`.

Engines available on this PC (existence only, not tested here):
- Godot: `/usr/bin/godot`.
- Poohdot: `/home/poo/.local/bin/poohdot`, engine at
  `/home/poo/src/poohdot/godot/bin/godot.linuxbsd.editor.x86_64.mono`.
  Geppetto plugin lives in `godot-plugin/addons/geppetto/` (incl.
  `EffigyGlbExporter.cs`, `EffigySceneImporter.cs`, headless `tools/*_test.gd`
  suites). Test project `~/games/godot/geppetto-test` exists; run windowed
  checks on throwaway copies only. `handoffs/GEPPETTO-POOHDOT-STATUS.md`
  (2026-10-29) and `handoffs/POOHDOT-PARITY-AUDIT.md` (2026-10-28) are the
  last full write-ups; the parity audit is partly stale (it predates the
  GLB rigs/morphs work and the paint-channels work).

## Pass criteria (per fixture, per engine)

- Units: exported bounds in metres equal source bounds in inches x 0.0254
  (tolerance 1mm). A 72-inch character is 1.8288 m tall in Godot.
- Axes: Y-up; a named asymmetric marker (the fixture script puts a small
  `Marker` box at +X on the prop) is at +X in the imported scene, not mirrored.
- Names: every expected node name present, no duplicates, no `Node`/`Mesh`
  fallbacks. Object names survive export AND import.
- Textures: baseColor + normal + emissive + occlusion images present with the
  manifest's pixel sizes; alpha image present on the opacity fixture with
  `alphaMode: "BLEND"`.
- Animation (export side only): both named clips present, input time range
  0..duration at the clip fps, bone channels present. (Re-import: NOT checked —
  GlbImport refuses skins, so animation cannot round-trip through Desktop.)
- Morphs (export side only): target count + `targetNames` match the manifest;
  weights start at 0.
- Screenshots: one windowless render per fixture per engine, saved next to the
  logs. A screenshot is evidence, not a pass by itself — it must accompany the
  `verify-glb.py` PASS and the `import-check.gd` PASS.

## Gaps (precise; do not paper over these)

- **G1 — no bot route writes a .glb.** `AiSuiteTools.validate_export` only
  runs `ExportService.EvaluateCapabilities` (check, no file). `export_preset`
  is plan-only. The `bake_export` tool runs BuildScript `export`, which writes
  s&box `.vmdl`/`.obj`/`.dmx`, not GLB. `ExportService.Export` has no
  production caller outside the Poohdot plugin (`godot-plugin/addons/geppetto/
  Tools/AnimateExport.cs`). Fix (another worker, coordinator-owned files):
  add a `glb` writer to the `export` verb (`kernel/Effigy/Script/BuildScript.cs`)
  or an `export_glb` bot tool (`AiSuiteTools.cs`), or drive it from the test
  harness in the final build. Until then the GLB step is integrator-driven.
- **G2 — no bot route authors animation.** `clip`/`key`/`save_clip` live in
  `BuildScriptAnim.Run` (anim-script text over a `Skeleton`); it has NO
  production caller — only tests call it. `anim_command` only runs `play` /
  `lint` (validate + report). `timeline_control` explicitly records notes
  only ("No animation clip is created and nothing plays"). Fix: wire the anim
  script runner to a bot tool or a `--anim-script` CLI path (files:
  `AiSuiteTools.cs` / `Program.cs` / `ScriptLive.cs` — coordinator-owned).
- **G3 — stale claims in `AiSuiteTools.cs`.** The comments at :506, :1082,
  :1090, :1160 say "ExportService supports only obj, effigy and riganim".
  Wrong since GLB landed: `ExportFormat` (`core/ExportService.cs:15-21`)
  includes `Glb` and `MapExportFormat` maps `"glb" => ExportFormat.Glb`.
  Behaviour is correct; the comments (and the `validate_export` docstring's
  parenthetical) need a one-line doc fix by that file's owner. Same staleness
  in `validate_import`: it reports "only .effigy documents and FBX via
  FbxImport are importable" although `GlbImport` exists — the tool just
  doesn't offer GLB. And it correctly reports OBJ/STL/PLY have no importer.
- **G4 — morph authoring route is thin.** Shape keys exist
  (`add ShapeKey keyname=... bodies=...`, features go in with `inkey=`), but
  the fixture's smile morph is written from the feature docs, not from a
  verified end-to-end run. Integrator: run `describe ShapeKey` first; the
  exported GLB must carry a morph target with non-zero deltas or the morph
  step is not done.
- **G5 — `docs/EFFIGY-API.md` is stale.** It has no `Character` section
  (the DNA lane landed after the last regeneration) and no `MikkTangents`
  note. Regenerate with `effigy.sh api --write` (ApiCatalog reads the kernel
  by reflection; do not hand-edit the generated file).
- **G6 — assembly/solver + `RigAnimPlayer.cs`.** Not this ticket's files, but
  the final build will hit them: `AssemblyDocument.cs:951` had an unterminated
  raw string (`or """`) — already repaired in-tree by re-check time
  (2026-11-13, now `or ""`); `RigAnimPlayer.cs` shows a single
  `class RigAnimPlayer` declaration at :393 on re-check (a duplicate class
  reported earlier is no longer visible). Both need the build to confirm.
