# Live corrections — shared workflow for every modeller

This protocol is provider-neutral. Claude, Codex, Gemini, Grok, local models
and other tool-using agents use the **same run, notes and inbox**. A skill teaches
the workflow; it does not grant tools to a model that cannot run them.

## User controls

1. **Stop** in one viewport requests a safe operation-boundary hold. The model,
   saved revisions and unexecuted tail remain in that same run.
2. A separate correction window opens. **Send message** stacks a message locally,
   labelled **queued — not sent**. It does not notify the modeller or resume.
3. Multiple messages share one selected saved step. Remove unsent messages with
   their ×. Apply/remove the queue before selecting another step. Limits are
   shown: 16 messages / 1600 characters per approved batch.
4. **Apply & continue** sends the complete, ordered batch as one correction.
   Draft text must be stacked or cleared first. Closing, reopening, switching
   layouts and ordinary typing do not send anything. Enter inserts a newline;
   Ctrl+Enter only stacks the current message.
5. History stays with this run across repeated stops. **Sent to modeller** means
   dispatched, not a claim that an external model has read or finished it.

## External tool-using modeller

- Start/edit through `geppetto-live`; keep the run **open**. Record the exact RUN
  path printed by the tool. Never use `last` with several sessions/runs.
- Keep `geppetto-notes --follow build.txt` armed, or call
  `geppetto-live wait RUN` between edits. Re-arm a monitor after it delivers;
  follow is not an eternal subscription. No Claude-only hook is required.
- A Stop is a review hold, not permission to finish/close the run. Do not send
  speculative edits while the user composes. Do not resume another run or a
  global agent session to work around it.
- An approved `CORRECTION from the viewport` note contains the selected saved
  revision, operation/source line and **all numbered messages**. Read the whole
  batch, in order. Later messages may refine an earlier request. Resolve genuine
  contradictions with the user rather than silently dropping a message.
- Inspect the existing model. Use the feature's existing ID and `set`/`remove`
  where possible. Write a small `fix.txt`, with a reason in each step's `echo`,
  then run **`geppetto-live more RUN fix.txt`**. Do not restart the build, create
  a replacement model, switch panes or discard saved history.
- If the user selected an earlier step, the UI already queued the rewind. Do
  not rewind a second time. The old future tail is deliberately abandoned.
  Otherwise the corrective `more` runs before the preserved pending tail.
- The cabinet handles the review's release. Do not send a separate resume or
  no-op batch before the fix: the fix must be the next real inbox edit.
- Show the correction live at the normal watchable pace. Report briefly what
  changed; do not claim a fix from a note receipt or compilation alone. Close
  the run only when the owner accepts it.

## Built-in native AI modeller

The same approved batch becomes an `instruct` command. The controller waits for
that exact command's acceptance before resuming. Rejection keeps the build held
and restores the local message stack for retry/removal. Locked judge rubrics are
not overridden. No separate provider-specific chat turn is fabricated.

## Boundaries and safety

- Target the run identity **and** saved revision, never the currently active
  global chat. A changed/ended run is not silently retargeted.
- Old runner processes cannot acquire the new review barrier by rebuilding the
  executable. The UI can hold them safely but must not promise fix-first ordering.
  Raw scripts not opened for corrections cannot accept this workflow either.
- A plain chat-only model with no Geppetto tools/monitor cannot edit a model just
  because it read this guide. Show a missing-tool limitation, not fake success.
- Never restart the shared cabinet or resume a real paused run for testing.

Implementation: `cabinet/viewport_correction.py`, `cabinet/ViewportCorrection.qml`,
`AiLiveControl.cs`, `ScriptLive.cs`. Acceptance: `handoffs/C1-OWNER-ACCEPTANCE.md`.
