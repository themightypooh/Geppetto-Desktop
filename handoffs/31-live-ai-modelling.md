# Live AI modelling — proposed scope

Status: first release implemented after the user approved this scope with “go”, 2026-10-20.
Implementation/evidence: [LIVE-AI-RESULTS.md](LIVE-AI-RESULTS.md). The original scope below
records the intended boundaries; follow-up releases remain unimplemented.
User goal: watch the editable model take shape in Geppetto while the AI creates it.

## Recommended first release

An opt-in Watch action attaches the existing viewport to one AI run. Each successful
native modelling operation publishes an editable revision. The viewport adopts those
revisions while the user orbits, pans and zooms. A concise activity line shows real
operation state: researching, waiting for model response, building, preview updating,
judging, completed, cancelled or failed.

This means live updates after completed operations. A Boolean/remesh can still take
seconds; provider requests can take longer. Continuous tool strokes, token streaming,
and an always-changing model at 30/60 Hz are separate capabilities. The viewport can
stay responsive while geometry is unchanged. Do not invent progress percentages.

First release covers one followed run and the current native feature catalog. It does
not expand material authoring, animation, rigging, import or other AI tool capabilities.
An empty starting document and a successful operation that removes all geometry must
both be valid preview states.

## Source findings

- `AiCli.cs`: `AiSession.Agent` executes tool calls sequentially, logs tool names, and
  retains provider tool-call history. `RunTask` saves round checkpoints, judges and
  restores the best checkpoint. There is no committed-revision event channel.
- `AiSuiteTools.cs`: add/edit/remove clone and rebuild a candidate, reject errors and
  preserve prior state. In isolated mode `AcceptWorker` accepts document JSON plus
  geometry counts. `SaveCheckpoint` rejects empty geometry, so it cannot directly be
  reused for every live revision. `Restore` also needs publication, including when
  returning to an earlier judged best.
- `AiToolWorker.cs`: each native operation runs in a windowless child process. Geometry
  is rebuilt there but only document JSON/counts return; the viewport would otherwise
  rebuild the document again. The cancellation path kills that child's process tree.
- `DocumentSession.cs`: `RenderSnapshot` carries document ID/revision; session APIs
  exist for snapshot construction/promotion. These do not yet provide cross-process
  AI-run publication or continuous viewport replacement.
- `DocumentLoader.cs`: background load exists, but `Start` ignores requests while
  Loading. A stream of revisions needs an explicit bounded scheduler; calling Start
  repeatedly is insufficient.
- `ServeMode.cs`: one persistent EGL renderer streams frames to the cabinet through
  acknowledged shared memory. It supports camera/clip commands, but no model-revision
  replacement or blank AI-run startup.
- `cabinet/host.py`: starts AI jobs and polls run artifacts/logs. `openModel` stops and
  restarts the viewport; using it for each AI edit would reset camera/process state.
  Current Stop terminates the AI process rather than acknowledging graceful cancellation.
- `kernel/Effigy/Features/StudioCache.cs`: existing `TrySave`, `TryRestore`, `Savable`
  can support reuse of worker-built geometry. Cache eligibility/latency must be measured;
  no new kernel API or edits are assumed.

## Publication and ownership contract

The AI run remains the sole document writer. The watched viewport is a read-only
preview; camera interaction is independent. Do not mutate a studio while another thread
extracts its mesh. No GL work runs in the provider/rebuild thread.

After accepting a successful operation, publish an immutable `.effigy` revision to the
run-owned directory. Stage/write/close/atomic rename before advertising it. Reuse the
worker's built geometry through the existing cache where eligible; cache failure is a
reported slower rebuild, not a corrupted document or lost edit. Start with this durable
file bridge; reserve custom mesh IPC/persistent worker changes for measured bottlenecks.

Publish structured, versioned events with run ID, document ID, monotonic sequence,
operation ID/type, affected feature IDs, revision/path, timestamp and outcome. Structured
stdout events provide low latency; an append-only run journal supports reconnect/recovery.
Malformed or incomplete records cannot advance the visible revision. Events contain
observable actions/results, not private model reasoning, prompts or credentials.

Every state replacement has a new publication revision. Restoring checkpoint 3 after
revision 12 publishes revision 13 referencing checkpoint 3; it does not rewind the
sequence. Distinguish last valid, currently displayed and judged best revisions.

A single background loader builds snapshots; one latest pending revision replaces older
pending work. Keep the old mesh visible during load. Completed snapshots may advance
monotonically within the current run, even if a newer revision is waiting, to avoid
starvation during rapid updates. Discard older/equal revisions and results from prior
run/document generations. No unbounded work queue. Preserve all operation events even
when preview revisions are coalesced.

At a frame boundary the GL owner uploads the replacement, then swaps it in, resets
temporal history and releases the old resources. A failed load/upload preserves the
last valid preview. Keep camera/settings across updates; frame the first nonempty model
once, then only reframe on explicit request. Clear invalid selections by stable identity.
Follow affects only the explicitly selected run and never opens/raises a window.

## Proposed implementation tickets, one at a time

1. **LM1 — Durable committed-revision feed (1–2 days).** Own new event/publisher types,
   scoped hooks in AiSession/AiSuiteTools/AiToolWorker, and focused tests. Publish every
   successful mutation and restore, plus lifecycle/error events. Atomic artifacts,
   empty state, monotonic IDs, reconnect journal and cache eligibility/size measurements.
   Failed/cancelled operations never advertise a revision. Add a deterministic scripted
   provider fixture that produces at least five visibly distinct native revisions.

2. **LM2 — Continuous viewport adoption (2–3 days).** Own a revision-load scheduler and
   leased ServeMode/DocumentSession integration. Add opt-in follow/run attachment and
   blank startup; use the existing SceneRenderer/shared-memory frame path. Background
   loading, bounded pending state, GL-thread swap/release, camera retention, generation
   rejection and temporal reset. First demo ends here: the scripted AI sequence visibly
   changes the same running viewport without process restart.

3. **LM3 — Run attachment and activity in the existing host (1–2 days).** Future UI ticket,
   separately approved because current handoff rules exclude UI implementation. Own
   cabinet host integration and minimal existing-control bindings: Watch/Stop Watching,
   activity text and showing displayed/latest revision lag. Reuse the existing run list
   and viewport; no new panel or redesign. Detaching the preview does not cancel the AI.
   Starting/following a run must not overwrite the user's open document.

4. **LM4 — Reliable stopping and recovery (1–2 days).** Add a run control channel and
   cancellation acknowledgement; cancel provider requests/native child work, stop new
   operations, preserve published files, and reject late results. Reconnect to a running
   run or reopen a stopped run's last valid revision. Keep preview error separate from
   model-build failure. Handle journal truncation, missing cache and worker/host crashes.

5. **LM5 — End-to-end acceptance and measurements (1 day).** Scripted provider plus real
   windowless EGL, process-level follow/cancel/reconnect, rapid updates and lifecycle
   checks. Reuse desktop regression/hardware smoke gates. Document live-provider smoke
   separately; mocked fixtures do not establish artistic quality or provider latency.

Planning range: **6–10 engineer-days** for this first release, one experienced developer,
assuming the existing AI/renderer paths are stable and the small UI ticket is approved.
A backend scripted demonstration is approximately **3–5 days** (LM1 + LM2). These are
scope estimates, not measured throughput promises. LM1 measurements may expose a cache
or mesh-upload bottleneck that requires a separately scoped optimization.

Before implementation, lease shared files in BOARD.md. Do not mix these tickets with
renderer-wave fixes. No new dependencies, kernel edits or visible automated tests.

## Acceptance

- Start with an empty run; five committed operations appear in order in the same
  viewport process. Camera changes remain responsive during a deliberately slow rebuild.
- Failed operation and malformed preview publication retain the prior model. Removing
  all geometry shows a valid empty state. No partially written document is displayed.
- Rapid revisions keep the queue bounded and converge to the latest committed revision;
  stale results after switching runs never replace the active preview.
- Restoring a judged best is displayed as a new revision with its checkpoint identity.
- Camera, lighting and render settings survive updates. No automatic focus change or
  per-operation viewport restart. First geometry frames sensibly; later edits do not
  continually steal the camera.
- Cancel during provider wait and native rebuild yields a terminal acknowledgement,
  leaves no orphan native worker and preserves the last committed revision. Prototype
  target: local control acknowledgment within one second, measured separately from
  worker shutdown; any escalation timeout is explicit.
- Reconnect/crash recovery selects a complete committed document and resumes journal
  consumption without duplicate mutations. Closing preview alone leaves the run intact.
- Resize and repeated swaps leave GL object counts stable after cleanup. Report time
  from operation commit to first displayed frame, rebuild/extract/upload time, peak
  queue size and memory. Initial performance target: <=500 ms commit-to-display for
  cache-eligible <=100k-triangle fixtures at the existing viewport resolution, excluding
  provider/tool execution; measure p50/p95 and revise scope openly if not achieved.
- Run CPU regressions and windowless hardware acceptance. UI automation must not launch
  a visible viewer; an interactive demonstration requires an explicit user request.

## Follow-up releases

- Pause/resume at operation boundaries and player instructions such as narrower
  shoulders. These need a control mailbox, defined precedence for new directions,
  and a policy for any operation already executing; current CLI chat is between tasks.
- Editable collaboration and undo: define one authoritative DocumentSession, group AI
  operations into meaningful EditHistory transactions, branch after undo and reject
  in-flight results from abandoned branches. Saved checkpoints alone are not undo.
- Replay/scrubbing and return to live: use the revision journal with retention/storage
  budgets; retain more than the latest preview and avoid confusing historical with live.
- Continuous sculpt-stroke playback, change highlighting and persistent incremental
  workers. Each requires actual tool support and measured need; no invented animation
  should imply the AI performed work it did not execute.
