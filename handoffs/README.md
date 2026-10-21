# Desktop backend handoff queue

These are **implementation instructions, not completed features**. They supersede the old
lane prompts/checklists in `HANDOFF.md`, `TASKS.md`, and `prompt-*.txt` for new work.
Scope: desktop backend, viewport rendering/manipulation and automated verification. **No UI panels.**

## Assign one small ticket, not a whole category

Give a worker the prompt in [WORKER-PROMPT.md](WORKER-PROMPT.md), one ticket ID from the
files below, and its allowed files. A category such as “animation completeness” is not a
single small-model assignment. Do not tell a worker to implement all eight categories.

| Category | Ticket IDs | Read the implementation brief |
|---|---|---|
| 1. Real materials | M1–M4 | [01-materials.md](01-materials.md) |
| 2. Editable documents, undo and save | E1–E5 | [02-editing-and-save.md](02-editing-and-save.md) |
| 3. Animation completeness | A1–A7 | [03-animation.md](03-animation.md) |
| 4. Transforms and IK manipulation | G1–G4 | [04-manipulation.md](04-manipulation.md) |
| 5. Import/export | I1–I4 | [05-import-export.md](05-import-export.md) |
| 6. Projects, paths, reload and settings | P1–P4 | [06-project-infrastructure.md](06-project-infrastructure.md) |
| 7. Standalone distribution | D1–D3 | [07-distribution.md](07-distribution.md) |
| 8. Broader verification | V1–V4 | [08-verification.md](08-verification.md) |
| 9. Renderer quality (wave 3) | R0–R12 | [30-renderer-quality.md](30-renderer-quality.md) |

Every worker must first read [RULES.md](RULES.md) and [CONTRACTS.md](CONTRACTS.md).
The contracts distinguish existing APIs from **new types to implement**; never assume a
proposed type already exists.

## Current truth, checked against the source

Reconciled 2026-10-20. Items marked **[now false]** were true when this file was written and
have been corrected elsewhere — do not act on them.

- C# / .NET 8; Linux; Silk.NET OpenGL 4.6. No game engine is required.
- `SceneRenderer` is shared by the interactive app and `HeadlessRender`.
- `HeadlessGlContext` uses hardware EGL device/pbuffer, without SDL/X11/Wayland.
- World-fixed key/fill/rim lighting, a GPU key-light self-shadow pass, exposure, and
  exact-size offscreen captures already exist. Do not rebuild these from scratch.
- `.effigy` geometry cache, FBX mesh/rig import, bone-key playback, limits, live IK via
  the kernel, animated triangle picking and Survey's assembled player already exist.
- The CPU suite registers **503 fixtures**; the last observed run on this machine was
  **496 passed, 0 failed, 7 skipped**. Rerun; do not quote this as your own test result.
- **Materials are no longer flat values.** `MaterialDescriptor`/`TextureBinding` resolve
  base colour, normal, roughness, metallic, emission and occlusion from the source, with
  sRGB upload for colour and linear upload for data. **[now false]** "Materials are still
  flat values."
- **[now false]** "`ViewportMesh.FloatsPerVertex` is 14 and has no UV or tangent attributes."
  The stride is **20**: position 3, normal 3, bone indices 4, bone weights 4, UV 2, tangent 4.
  `Face.UVs` are carried per corner and uploaded; tangents carry handedness.
- `DocumentLoader` still returns a mesh/player, not an editable retained document, but a
  `DocumentSession` (E1) now retains the source document, the original clip JSON, the
  editable state and a revision, with `EditHistory` (E2), `ClipEditService` (E3) and
  `DocumentSaveService` (E4) on top of it.
- Bone-key playback is allocation-tested and live IK runs through `LiveIkSolverBuffers` (A4);
  the kernel's allocating `RigAnimFile.SampleLocal` remains the fallback for spline /
  object-space curves, limits and root motion. Morph (A2), part/camera tracks (A6), clip
  blending (A3), timeline events (A5) and constraint parity (A7) are implemented and tested.
- **[now false]** "Coloured bone axes are not draggable gizmos." Typed transform handles are
  drawn and hit-tested from one source (G4), with an auto-key toggle that writes FK and live-IK
  keys as a single undoable entry.
- There is a full save/undo authoring backend (E1–E5) and export (I1–I4), but **no UI
  surfaces** reach them, so they are not yet reachable from the viewer.
- A second wave of modelling services has landed as tested backend code and is **not wired
  into the viewport**; another 3 766 lines have no caller and no test. See BOARD.md
  sections 2, 2a and 2b.
- **The renderer is an LDR forward pipeline.** No anti-aliasing, no real IBL, broken
  screen-space AO/SSR, a viewer-facing normal flip, no temporal accumulation, no volumetrics.
  The upgrade plan is [30-renderer-quality.md](30-renderer-quality.md); the state is tracked
  in BOARD.md section 3.
- This is a viewer/playback prototype with a modelling backend, not an already-finished
  desktop modeller.

## Recommended order and dependency gates

1. **V1**: make baseline/fixture coverage explicit. Keep verification working throughout.
2. **P1**, then **E1**: portable asset resolution and retained document/session ownership.
3. **M1 → M2 → M3 → M4** for real textures; **E2 → E3 → E4 → E5** for editing/save.
4. **G1 → G2 → G3 → G4**, after E1/E2; G4 also needs the clip-key commands in E3.
5. **A1**, then independently design **A2**, **A3**, **A4**; **A5/A6** need stable session/
   transport contracts. **A7** is an inventory and can start early, not a blanket parity task.
6. **I1 → I2**, then optional **I3 → I4**. I1 is possible before advanced animation.
7. **P2 → P3 → P4**, after retained documents and safe saves exist.
8. **V2–V4** grow with features. **D1 → D2 → D3** packages the supported feature set.

Do not block every ticket on everything above. Each brief names its actual prerequisites.
Examples: M1 can be built from fixtures without renderer edits; G1 is pure math; D1 can
inventory dependencies early, but cannot claim a portable release before P1 is integrated.

## The renderer wave runs as its own track

The renderer quality wave (R0–R12) is **not** part of the sequence above and does not wait for
it. It is a parallel track with its own order, its own lanes and its own budget:

```
R11 → R0 → R1 → { R2 , R3+R7 , R4→R6→R5 } , R8 , R9 , R10 , R12
```

R0 (the shader/pass split out of `Program.cs`) is the prerequisite for parallel renderer work,
and R1 (the HDR pipeline) is the prerequisite for every visual ticket. Do not start R3, R5 or
R7 before R1: they all change the colour pipeline, so doing them first means redoing them.
Costed effort, staffing lanes and cut lines are in
[30-renderer-quality.md](30-renderer-quality.md); the state is in BOARD.md section 3.

## Match assignment size to worker strength

- Good bounded starting points: **V1, P1, M1, G1, D1**, with a reviewer checking their tests.
- Moderate integration work: **E1–E4, I1/I2, P2/P4**. Give one ticket and explicit shared-file leases.
- Experienced graphics/animation/concurrency workers: **M2–M4, A2–A4, G2–G4, P3, I3/I4**.
  A weaker worker may prepare isolated fixtures or inventory, not independently certify these.
- Foundation tickets still need executable tests; expert tickets still need small acceptance slices.
- If a ticket is too large for one worker, the coordinator splits it further and freezes the
  smaller contract. Do not solve size by deleting edge cases or marking a stub complete.

## Parallel work without collisions

- A coordinator assigns ownership in [BOARD.md](BOARD.md) **before** edits.
- One writer per file, including tests and project files. “Separate ticket” does not
  make simultaneous edits to `Program.cs` or `tests/Program.cs` safe.
- Safe parallel examples: M1's new material descriptor files; G1's new math files;
  D1's dependency inventory. Give each worker separate test files too.
- The coordinator alone integrates shared entry points, serializers, vertex layouts,
  `SceneRenderer`, `MarionettePlayer`, `DocumentLoader`, `DesktopOptions` and `.csproj`
  changes unless a worker explicitly owns those files during its assignment.
- Kernel changes need a separately approved kernel ticket, mirror sync, and kernel tests.
  A desktop worker does not quietly widen its assignment into `../geppetto`.

## Completion is evidence, not a checkbox

A ticket is `verified` only after its named tests, failure cases and integration seam work.
An unused helper class, a compiling stub, or “image changed” alone is not completion.
Record exact commands, exit codes, artifacts and remaining limitations in BOARD.md.

This handoff-writing pass changes instructions only. It does not mark any new ticket implemented.
