# Desktop verification

- For any new implementation assignment, read `handoffs/README.md`, `handoffs/RULES.md`,
  `handoffs/CONTRACTS.md`, and the assigned ticket. These are the current handoff instructions;
  old `HANDOFF.md` / `TASKS.md` lane prompts are historical. Work one ticket at a time.
- Shared-file ownership must be assigned in `handoffs/BOARD.md` before parallel edits.
  Do not mark a ticket verified from compilation alone or from an uncalled helper class.

- Do not steal the user's focus. Automated renders, smoke tests and animation checks must use
  `--headless` (hardware EGL, no window/display connection), or the headless kernel tests.
  Only launch the interactive viewer when the user explicitly asks to see/use it.
- `dotnet run --project tests/Desktop.Tests.csproj -c Release` runs CPU regressions.
- `python3 tests/gpu-smoke.py` runs windowless GPU checks, with DISPLAY and WAYLAND_DISPLAY removed.
- The interactive app and headless rendering must share `SceneRenderer`; do not maintain a
  second shader/lighting implementation solely for passing tests.
- Do not delete obj/bin to hide build failures. Multi-project verification uses MSBuild's
  `--artifacts-path` to isolate each project's restore/build outputs.
- UI work is not part of the current task. Render/lighting and backend fixes are allowed.
