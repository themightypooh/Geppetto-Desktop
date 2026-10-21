# LM6 — Pause/resume and player directions during a live build

Assignment: user requested “more” after LM1–LM5. Implement the next bounded live-build
slice: pause/resume and new directions, through the existing run cards/control protocol.
No new panels, renderer/kernel changes, replay or collaborative undo.

- Pause is acknowledged as requested immediately, then paused at a safe boundary.
  A running provider request/native operation finishes; no new operation starts while
  paused. Resume continues the same document/run. Stop remains immediate and works paused.
- Durable run-ID-scoped command files carry unique IDs. Validate size/type/text, consume
  each command once, acknowledge accepted/rejected/delivered outcomes, and preserve FIFO
  direction order. Cancellation has precedence. Do not overwrite an unprocessed direction.
- Directions received during a provider response supersede its unstarted modelling calls.
  Supply explicit not-executed tool results to retain every provider's native history.
  Deliver queued directions to the builder with current document state; newer directions
  override conflicting older ones. A current operation may commit before the boundary.
- Directions queued while paused remain queued until resume. Directions racing a final
  answer must be handled before declaring completion. Completed/stopped runs reject new
  controls at the host; commands never restart a finished process.
- First steering slice targets unjudged creation/chat runs. Judged runs can pause/resume
  and cancel, but reject direction commands explicitly because their rubric/target was
  locked before creation. Replanning a rubric and resetting judged-best selection belongs
  to a later ticket; never silently judge updated requirements against an old rubric.
- Existing run cards gain Pause/Resume and a short direction input/send action. Show
  pending versus paused state and direction delivery; no focus stealing or auto-open.
- Tests: provider wait + multi-call response supersession + resulting native dimensions;
  pause/resume blocks tools at a boundary; directions persist while paused; cancel paused;
  duplicate/foreign/invalid/oversize commands rejected; judged steering rejected; no raw
  private model reasoning in events. Headless regressions, existing live fixtures, Python
  host controls and QML compilation; normal Release build. Mock providers do not prove
  artistic obedience by live models.

User steering: explicitly requests adjustable slower building. Include a live `pace`
command, finite minimum gap in seconds [0,30] between modelling operations, measured
from completion of the previous mutation. Zero is natural speed; the first mutation
starts without an artificial delay. Recompute waiting time when changed. Pause/Stop
remain responsive during a pace wait. Existing run cards cycle Normal/1/3/5/10 seconds;
protocol supports finer values for tests/hosts. This changes scheduling, not provider
compute speed or viewport FPS. Wall-clock budgets still apply.

## Status 2026-10-20 (Claude, continuing interrupted Codex run)

Implemented: `AiLiveControl.Session` (durable FIFO commands, pause/resume/pace/instruct/
cancel, close-time rejection), control events in `AiLiveRun` that don't disturb operation
identity, boundary/supersession/final-race handling in `AiCli.cs`, `send_command` /
`next_pace` in `cabinet/live_runs.py`, host slots `togglePause`/`cyclePace`/`sendDirection`,
run-card PAUSE/RESUME, PACE and direction input in `cabinet.qml`. Verification: see
`handoffs/LIVE-AI-RESULTS.md` (LM6 section).
