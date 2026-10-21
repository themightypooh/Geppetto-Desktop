# Live AI modelling — first release, 2026-10-20

User approved the scope in `31-live-ai-modelling.md` with “go”. LM1–LM5 were implemented
sequentially by one agent; no delegated workers, kernel edits, new dependencies or visible
viewer launches. The scoped Watch/activity bindings use the existing cabinet run cards.

## Delivered

- Immutable native `.effigy` revision after each accepted AI mutation, initial empty
  document and checkpoint restore. Run/document/operation identity, affected feature IDs,
  monotonic revision/event sequence and timestamps are recorded. Failed/cancelled operations
  retain the previous revision. Restoring an earlier checkpoint creates a newer publication.
- Atomically published files and latest-state index, flushed append-only journal, and
  incremental bounded journal reading that can complete a torn tail later. The journal
  is authoritative if the convenience index cannot be written. Index recovery is also
  implemented in the Python host.
- Reuse of native worker geometry through existing kernel-validated caches, including
  checkpoint restore. Cache absence falls back to background rebuilding. The interrupted
  pipeline's parent only holds feature JSON/counts; it must not cache that unbuilt studio
  as empty geometry. A rendered final-restore equality regression covers this case.
- Persistent `--serve --watch-run DIRECTORY` and `follow DIRECTORY` / `follow` commands.
  One background loader and one replaceable pending CPU snapshot; no unbounded queue.
  Old geometry remains visible until upload succeeds. Empty documents clear the preview.
  GL upload/swap/release stays on the renderer owner thread; temporal history resets.
- Camera/settings survive updates, first nonempty model frames once, stale selections
  clear, and old run results cannot displace a newly followed run. Real previews continue
  to use `SceneRenderer`, not a separate shader or image generator.
- WATCH/UNWATCH on existing run cards, operation activity and displayed/latest revision
  counts. Unwatch retains the visible model and leaves AI work running. The host can
  attach a fresh blank server or reuse the existing viewport process. Opening a source
  document normally leaves watch mode; source files are never overwritten.
- Graceful STOP through a run-ID-scoped atomic cancellation mailbox. Acknowledgement
  precedes provider/native cancellation; terminal cancellation preserves the last commit.
  Late commits are rejected and actual in-flight native child processes are reaped.

## Files

New production files: `AiLiveRun.cs`, `AiLivePreview.cs`, `AiLiveControl.cs`,
`cabinet/live_runs.py`. Integration: `AiCli.cs`, `AiSuiteTools.cs`, `AiToolWorker.cs`,
`DesktopOptions.cs`, `Program.cs`, `ServeMode.cs`, `cabinet/host.py`,
`cabinet/cabinet.qml`. Tests: `tests/AiLiveTests.cs`, additive registration in
`tests/TestRegistry.cs`, `tests/test_live_runs.py`. Documentation: AI.md, tests/README.md,
the scope, this report and BOARD.md.

## Acceptance evidence

- Isolated final focused build/test: `/tmp/opencode/live-ai-final-build.log` and
  `/tmp/opencode/live-ai-final.json`; 9/9 live fixtures pass. The fixture drives a mocked
  provider through actual native child processes and a separate production EGL viewport.
- Full gate: `env -u DISPLAY -u WAYLAND_DISPLAY AI_REQUIRE_EGL=1 python3 tests/gpu-smoke.py`
  exit 0. `/tmp/opencode/geppetto-verify-0baur57d/verification.json` records **566 passed,
  0 failed, 0 skipped**, Python harness self-tests, isolated build identities, and three
  120-frame NVIDIA hardware EGL captures. Repeated 1024x1024 bind images match exactly;
  animated capture differs. This is not a monitor-FPS measurement.
- The live suite covers five distinct evolving images, final-restore image equality,
  rapid revisions, deliberately corrupt owned publication, empty removal, run switching,
  reconnect, camera retention and stable GL resource counts. It also covers foreign
  cancellation identities, acknowledgement latency, provider wait cancellation, native
  Subdivide cancellation with no orphan process or new revision, and torn/malformed logs.
- Five steady cache-eligible 49,152-triangle updates at 1024x576 in the full gate:
  commit-to-frame **p50 238.8 ms / p95 433.5 ms**, meeting the scoped 500 ms target.
  The earlier isolated focused run measured p50 211.8 / p95 304.0 ms. These are small
  hardware-specific samples excluding provider/tool execution and first context startup.
  Exact sample JSON is in the full test run's fixture directory (`live-latency.json`),
  whose location is recorded in `cpu-results.json` under `artifacts`.
- `python3 -m unittest discover -s tests -p test_live_runs.py -v`: exit 0, 3/3.
  Includes actual cabinet QObject bindings under QCoreApplication; no GUI window.
  Python syntax checks passed. QQmlComponent compiled cabinet.qml under an offscreen
  QGuiApplication without instantiating a window.
- `dotnet build Marionette.Desktop.csproj -c Release`: exit 0; normal launcher binary
  updated. Log `/tmp/opencode/live-ai-release-build.log` includes one transient existing
  output-file lock/copy retry warning, no errors. No obj/bin deletion.
- An additional no-window QCoreApplication smoke used the actual cabinet Host and normal
  Release server: received revision 7 and its shared-memory image, orbited, and detached
  without changing the viewport PID. The ordinary app was not launched/restarted.

An earlier full-gate attempt was interrupted before producing its report; it is not
counted as a pass. The completed report above is the acceptance result.

## Usage and remaining boundaries

Restart the cabinet to load its updated host/QML, start a new AI run, then press WATCH
on that run card. Existing pre-feature runs without a journal still use OPEN on their
completed checkpoint. Headless protocol details and controls are documented in AI.md.

Live means after completed operations, not streaming private reasoning/tokens or
continuous sculpt strokes. Preview is read-only. Mid-build instructions, collaborative
undo/branching, replay scrubbing, automatic artifact retention and incremental persistent
native workers remain separate follow-ups. Revision/job files are retained for recovery.
Live-provider artistic quality, live-provider latency and a user-visible/manual UI review
are not established by the mocked-provider/headless acceptance fixtures.

# LM6 — pause/resume, build pace and directions, 2026-10-20

Scope `handoffs/32-live-ai-steering.md`. Run cards gain PAUSE/RESUME, PACE (Normal/1/3/5/10 s)
and a direction box. Protocol and limits are documented in AI.md.

Evidence:
- `--filter ai-live`: 12/12 locally; in the full gate all 13 ai-live fixtures pass, including
  three new ones: pause/pace/validation at boundaries (pace gap honoured, lowered pace
  releases a wait, stop while paused, foreign/unknown/oversize/judged commands rejected),
  direction during provider wait (3 unstarted calls get `not_executed`, builder receives
  the direction, final model has 1 feature not 3, direction text absent from the journal),
  and a direction racing the final answer is built before `completed`; a command
  sent after completion is not processed.
- Full gate `/tmp/opencode/geppetto-verify-wi0fxljq/`: 575 pass / **1 fail** / 0 skip.
  The failure is `service-surface/awaiting-decision allowlist`: a concurrent cleanup
  deleted 16 dead service files (SsrPass.cs, Gpu*Service.cs, PathTracerService.cs…)
  without updating that allowlist. Not LM6; left for its owner. 49k-tri latency p50
  231.8 / p95 282.9 ms.
- Python `test_live_runs.py` 4/4 (command ordering/validation, host slots under
  QCoreApplication). cabinet.qml compiles via QQmlComponent offscreen, no window.
- Normal `dotnet build -c Release` exit 0. No visible viewer launched.

Limits: mock providers do not prove a live model obeys directions; pace is scheduling,
not compute speed; judged runs reject directions.
