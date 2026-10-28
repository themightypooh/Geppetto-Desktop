# Muse — cabinet run-control placement (design handoff, not an implementation ticket)

Detailed step-by-step Muse plan: `handoffs/38-muse-time-drawer-controls.md`.

The user rejected large viewport overlays and a generic layout chooser and selected the existing **TIMELINE drawer**, specifically **reworking its TIME page** rather than adding a RUN page. **Do not implement production UI until this layout is further critiqued and approved.** The interactive mockup is `artifacts/viewport-control-chooser.html` (a portable, self-contained copy is `artifacts/geppetto-cabinet-controls-standalone.html`). Its Current TIME and KEYS imagery is captured from the actual cabinet QML with an offscreen test host; Reworked TIME is a proposed interactive layout using the existing baked drawer and key artwork. Mock controls only simulate behaviour. The real chest must stay closed for this design pass.

## What exists in the current checkout

- The cabinet has a 1024×576 viewport inside a 1104×656 frame; four-pane mode is roughly 511×287 per pane, with a one-pixel seam. Research opens left, Tools/Outliner opens right, and Timeline opens down. The label plates toggle these drawers. See `cabinet/art/geom.js` and the `Drawer` instances in `cabinet/cabinet.qml`.
- `AiLiveControl.Session` and `cabinet/live_runs.py` already carry per-run pause, resume, one-change step, exact 0–30 s pace, and cancel commands. `Host.stepRun`, `Host.setPace`, `Host.stopResearch` route these by run directory. Pane replay methods already exist in `cabinet/panes.py`.
- In the **current source**, `spKeys` and `ReplayStrip` in side panes are `visible: false`; `RunPaceSlider.qml` exists but is not instantiated by `cabinet/cabinet.qml`. The four-real-viewport test in `tests/test_run_pace_slider.py` fails because all four pace controls are `MISSING`. Do not report these as visible working cabinet controls.
- `ClaudePanel.qml` has a global STOP ALL. That is not a substitute for independently stopping a selected pane's run. The viewport correction UI is separate from these transport controls.

## Design choices to review with the user

1. Run station in **Research drawer**: clear viewport, left-side pull-out.
2. Run station in **Tools/Outliner drawer**: near editing tools, shares limited drawer space.
3. **Chosen direction:** use the existing Timeline drawer; rework TIME for pane-targeted run controls, preserve KEYS, and compare against the captured current TIME page.
4. **Dedicated control panel**, opened by a proposed lid button: outside the model area, mutually exclusive with the right drawer in the prototype to avoid overlap.

The HTML includes openable drawers, 1/2/4 pane modes, native-pixel and fit views, selectable pane, and simulated Pause/Resume, Step, Stop, pace, Replay/Live and revision scrub. This is for layout feedback, **not evidence of production wiring**. Keep control labels legible, never spill or clip text, and check min/default/max scale and the small quad panes before any production UI change.

Current Reworked TIME mockup splits STEP into **STEP −** and **STEP +**. Minus simulates rewinding a held run by one saved revision; plus advances one change and holds again. This is a visual/interaction proposal, **not a final destructive rewind policy**. The TIME/KEYS switch and key/lamp faces use baked/current-QML artwork rather than generic HTML buttons. The user is still critiquing the visuals.

## When a placement is approved

Scope the production work separately, preserve each pane's run identity while interacting, use existing validated commands, test the real QML route in all four panes, and keep correction notes and replay independent. A held run must not resume because another pane, window, or chat changes. Do not start the shared chest or call paid models merely to verify this design.
