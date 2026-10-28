# VS1 — per-viewport build speed slider

User request (2026-10-25): put a step-speed slider in the viewport(s).
Owner: current angler modelling session. One bounded ticket.
Scope: new cabinet/RunPaceSlider.qml; scoped RunBar integration in cabinet/cabinet.qml;
new Host.setPace slot in cabinet/host.py; isolated offscreen Qt tests.
Existing cyclePace and live_runs.send_command remain the protocol source.
No renderer/kernel edits, no chest restart, no live-run changes during tests.
Verify pane-specific command routing, 0–30 s bounds, keyboard/drag behavior,
and screenshots at minimum/default/maximum UI scales with long labels.

## Evidence

- Implemented RunPaceSlider in the shared RunBar, so panes 1–4 each expose the
  control. Exact per-run delay uses the existing validated pace protocol.
- Slider covers 0–30 seconds, supports mouse/keyboard, reflects external pace
  changes and does not retarget an edit when the viewport changes runs.
- `PACE_TEST_ARTIFACTS=artifacts/viewport-step-speed env -u DISPLAY -u WAYLAND_DISPLAY python3 tests/test_run_pace_slider.py`: 7 passed.
- `PYTHONPATH=cabinet python3 tests/test_live_runs.py LiveRunsTests.test_steering_commands_are_durable_validated_and_ordered`: 1 passed.
- Cabinet QML load check: 1 passed. Python host compilation passed.
- Actual four-pane offscreen QML integration exercised all four sliders and
  verified four distinct run targets. Screenshots at widths 220/300 and text
  scales .75/1/1.5 saved under artifacts/viewport-step-speed.
- Current running cabinet deliberately not restarted; the new QML/Python slot
  becomes available on its next launch. Lure/fin section was not started.
