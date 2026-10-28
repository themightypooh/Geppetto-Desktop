# Whole modelling pipeline — release gates for Muse

**Status: not certified.** This is a separate acceptance document, not permission to start the closed shared chest, run a paid model, delete work, or declare the product ready. The TIMELINE drawer design handoff is `handoffs/38-muse-time-drawer-controls.md`.

## Evidence as of 2026-10-26

- `env -u DISPLAY -u WAYLAND_DISPLAY QT_QPA_PLATFORM=offscreen python3 -m unittest discover -s tests -p 'test_*gate*.py' -q` → **105 tests passed**, exit 0. These are isolated fixtures; they do **not** certify a real click-to-first-frame or a visible chest run.
- `env -u DISPLAY -u WAYLAND_DISPLAY QT_QPA_PLATFORM=offscreen python3 -m unittest tests.test_cabinet_qml.CabinetQmlTests.test_the_cabinet_loads tests.test_cabinet_qml.CabinetQmlTests.test_the_timeline_has_transport_rule_and_view_keys -v` → **2 passed**, exit 0. They test the *old* TIME/KEYS drawer, not BUILD/CLIP/KEYS or per-pane run control routing.
- `cabinet/gate.py` currently contains registration and first-frame deadlines; the older list of missing gate features in `handoffs/36-muse-gate-parallel.md` is an implementation brief, **not a current verified-failure list**. Re-test every claim against current source before changing it.
- No real-chest acceptance run or paid-provider turn was conducted for this report. No s&box compile/load of a newly created end-to-end asset was certified here.

## Release gates, in priority order

### 1. Approval to first visible geometry, exactly once

Use an isolated fake chest/runner first. Measure **filed → toast observed → Yes → launcher spawned → run registered → first frame received → worker receipt** as separate timestamps. Assert no runner before Yes, exact reserved pane, no duplicate model when worker attaches after cabinet launch, four independent occupied lanes and immediate fifth refusal, no reassignment on slow or failed startup. Under the supported fixture, Yes→first frame must meet the five-second target; a timeout must report failure and clean up only that intent. After this is green, ask po for explicit authorization before any real chest verification. A token write or `Popen` timestamp is not a frame.

### 2. Stop and close without orphaned work or token spend

Test Stop during an operation, while held, during approval, and while provider stdout is silent. Closing the chest must cancel pending approvals, stop only its owned runner/provider turn, wake local waiters and deliver a cancellation record to the external Muse worker. It must **not** kill unrelated SSH shells, other applications, or another pane's work. Assert no later revision after acknowledged Stop and no automatic resume from correction, replay, chat, or window events. State the limit of cooperative remote cancellation honestly; no invisible paid retry.

### 3. Saved-model integrity on the real path

Exercise the actual `geppetto-live` / live-script KEEP and save path, not just `DocumentSaveService` unit tests. Use disposable fixture destinations; leave existing user assets untouched. Verify the last good `.effigy` survives interrupted write, full disk/error, duplicate save, and rewind-then-save; the new file opens and has the intended revision. No generator should be required at scene runtime to reconstruct an allegedly saved result.

### 4. One complete usable asset, not just a prompt or receipt

With permission for an acceptance run, prove: subject/tool choice and script preflight → approval → visible build revisions in its assigned pane → Pause/STEP+/correction/replay → saved model/material/texture outputs → reopen in Geppetto → bake/export → s&box compile/load of the actual asset. Record paths, logs, screenshots and failure conditions. A Build receipt proves that a step ran, **not** that materials, topology, export and in-engine use are good. Visual quality and reference/licence checks need explicit review; do not label them enforced by prompts alone.

### 5. Drawer controls and per-pane isolation

The accepted design direction is the TIMELINE drawer with a **three-position BUILD / CLIP / KEYS switch**. BUILD controls exact selected run identity; CLIP preserves existing PLAY/LOOP/frame/clip selection; KEYS preserves dope-sheet editing. Pause/Resume needs a real QML-callable Host route; STEP+ advances one change and holds; pace edits cannot retarget during a drag; Stop affects only the selected run. STEP− policy is **still open**: non-mutating previous saved step versus explicit rewind of the live run. Do not guess or silently make it destructive. See `handoffs/38-muse-time-drawer-controls.md` for visual details and offscreen tests.

## Reporting rule

For each gate, report exact command, exit code, fixture versus real-chest context, measured timestamps where applicable, output paths, and any remaining blocker. Passing isolated tests is useful but does not turn this entire checklist green. Do not start or restart the visible shared chest without po explicitly asking for it.
