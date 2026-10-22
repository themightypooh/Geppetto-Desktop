# 33 · Tool wheel follows the model

Status 2026-10-22 (Claude): implemented and checked live. Not committed.

## What it does

`cabinet/ToolWheel.qml` (the `toolWheel` instance in `cabinet/cabinet.qml`) shows the tool behind
the change the viewport is showing. Rule: **the model doesn't change unless the wheel changes,
and the wheel doesn't change unless the model changes.**

- Each displayed revision: the wheel lights the step's category, fans it out, lights the tool
  and puts the tool, `LINE n` and `on <target>` in the core. All of it happens at once, with no
  timed reach. The same tool again presses the core in, and the line number moves on.
- Between revisions it holds still. The only motion is the countdown ring around the core: it
  fills over the run's pace, then creeps while the next step is built (`working`).
- It goes when the model leaves the screen (empty `op`), or `hold` ms (4.2 s) after the run
  stops.
- WHEEL in the tools drawer opens it by hand (`manual`). Run ops are then ignored until you put
  it away.

## How the op gets there

- The viewer emits `@{"ev":"ai-preview","revision":N}` when it shows a revision.
  `host.py` stores `displayed_revision` and emits `shownOpChanged`.
- `host.shownOp` = `live_runs.revision_op(watching, displayed_revision)`. That function reads
  `<run>/.live/events.jsonl` incrementally and maps revision → `operation`.
- `cabinet.qml` wiring:
  - `op`: `host.shownOp` while a run or replay is watched, else the run card's `wheelOp`
    (the op of its latest revision).
  - `working`: the op being built, which only drives the ring.
  - `revision`, `pace` and `stepping` come from the replay or the live run.
- Bench runs are filtered out of `host.runs`, but they are watched, so they still reach the
  wheel through `shownOp`.

## Op → tool (`parse()`)

- Strips `line N:` and a leading `undo `.
- `add X …` → feature key; a trailing `Feature` is dropped. The target is `name=`, or the bare
  third word (AI runs: `add Blob Grip`).
- `edit Body op` → `edit:op`. A two-word op (`Loop cut`, `Limited dissolve`) is recognised
  when it maps to a tile.
- Any other verb (set, material, measure …) → its own key. A verb with no tile still shows its
  name in the core.
- `start: file.effigy` (bench) → IMPORT. `initial` → nothing.
- Aliases: extrude variants → EXTRUDE FACES, limited dissolve → DISSOLVE, `at` → MEASURE,
  `corrective` → CORRECTIVE.

## Verification

- `python3 tests/test_cabinet_qml.py`: `test_the_wheel_moves_with_the_model_and_only_with_it`
  covers parsing, the immediate landing, the press on a repeated tool, and `working` not moving
  the wheel. The only failure is the pre-existing
  `test_the_drawing_kit_is_loose_in_the_tools_drawer`.
- Live: an 11-step `geppetto-live … --pace 2` build, snapped every 0.4 s. On every step the
  model and the wheel changed in the same frame, with no wheel change in between. The wheel
  left in the same frame as the model at run end. A replay (`host.py replay DIR`) gave the
  same result.

## Still open

- Bench hand edits are covered by the parse test only; not yet watched live with snaps.
- Motion that isn't a model change, if strict lockstep should cover these too:
  - the countdown ring;
  - the ~0.4 s settle of the core press;
  - the 4.2 s fade after a run stops.
- Steps that change data but not the picture (e.g. `material 1` on an unused slot) still move
  the wheel, because they are revisions.
- Script verbs that make no revision (`measure`, `echo`, `describe`) never show on the wheel,
  by design.
