# C1 script Stop/correction ordering — independent acceptance

## Final follow-up: real `geppetto-notes --follow` round-trip — PASS

Added/called `tests/c1_notes_follow_roundtrip.py`. This launches a real ScriptLive
CLI throwaway run and the **actual** `/home/poo/.local/bin/geppetto-notes --follow`
executable. A disposable HOME restricts the monitor to throwaway metadata; the
wrapper locates the real cabinet Python source through a code-only symlink.
No real research runs, cabinet sockets, native viewer or knight run are accessed.

The test calls production `ViewportCorrections.stop/refresh/send`; its only UI
stub acknowledges viewer `seek` commands (the owner tests actual pop-out input).
Stop captures current revision 4, then Send selects **saved revision 2** with a
unique correction token. The real rewind/note/control/inbox implementation is
used, with no delivery or notes mocks.

- Correction token appears **exactly once** across more than three monitor polls.
- Printed header identifies the right script, **run folder, step 2, line 1**, and
  the exact saved operation, not the later held revision.
- Note is durably marked read. A restarted real follower prints **nothing**.
- An unrelated script's decoy note stays unread and is never printed.
- Rewind generates a separate informational note, correctly also scoped to step
  2. There are **two expected notes total, only one actual correction**.
- After observing the correction, the test supplies the real corrective inbox
  batch. Saved `monitor-corrected.effigy` has Base SizeX **7** and no abandoned
  Later feature. The throwaway run closes with exit **0**.
- Each monitor/run process is cleaned up individually; no cabinet restart or
  knight-run controls. Both complete invocations passed.

Latest complete evidence:
`/tmp/opencode/c1-notes-follow-evidence/roundtrip-0l8z6s3i/evidence.json`.
That directory also retains stdout/stderr, all journals, notes and saved models.
Evidence includes the tested binary SHA256 and Python source hashes.

```sh
python3 tests/c1_notes_follow_roundtrip.py --binary /tmp/opencode/c1-script-ordering-artifacts/bin/Marionette.Desktop/release/marionette-desktop.dll --out /tmp/opencode/c1-notes-follow-evidence
```

Exit **0**. This closes the actual script-monitor delivery check, but is not
proof of real viewer input, native-AI correction or installed-cabinet deployment.

2026-10-24, Greeting session (`ses_e909d958effd51ORJYP58fdCya`).
Assignment from the C1 owner: isolated initial-batch and `more`-batch ordering
tests only. Implementation files, shared registration and the knight run were
not changed or controlled during this assignment.

## Result: PASS, 5/5 (including the follow-up assignment)

Called real `ScriptLive.Execute`, `AiLiveControl` and `Inbox` from a fresh source
build. CPU-only: no viewer, cabinet connection, provider/model request or GL.
Each fixture owns its own GUID directory, cancellation source and command inbox.

| Case | Observed committed operation order | Result |
|---|---|---|
| Stop mid-initial batch | Base, **fix Base width=7**, CorrectionMarker, TailA, TailB | PASS |
| Stop mid-`more` batch | Base, CompletedMore, **fix Base width=7**, CorrectionMarker, TailA, TailB | PASS |
| Stop while idle/open | Base, **fix Base width=7**, CorrectionMarker; same run remains available | PASS |
| Stop at a checkpoint | Base, checkpoint, **fix Base width=7**, CorrectionMarker, TailA, TailB; checkpoint commits once | PASS |
| Correction from earlier saved step | Base, LaterGone, CompletedMore, **rewind to step 2**, **fix Base width=7**, CorrectionMarker; abandoned tail never commits | PASS |

Both tests:

1. Wait for the first operation of the target batch to commit.
2. Send the viewport's real `review` control command, wait for the acknowledged
   safe paused boundary, and confirm neither pending tail operation committed.
3. Write an isolated correction note and send `resume`, matching the external
   script branch of `ViewportCorrections.send()`.
4. Delay the modeller's corrective `more` by **1.1 seconds**, longer than the
   fixture's **0.8-second mutation pace**. The held revision must not advance
   during that delivery gap; `resume` alone must not release the preserved tail.
5. Submit the corrective `more` through the real Inbox protocol. Assert exact
   committed-operation order and exactly one commit per expected operation.
6. Close the fixture and verify retained feature order and actual Base mesh
   width **7**, not just a log message. No previously completed step repeats.
7. Cancel only that fixture on failure, so tests do not leak open runners.

## Files and reproducible command

- Test helper: `tests/C1ScriptOrderingEvidence.cs`.
- Independent runner: `/tmp/opencode/c1-script-ordering-runner/Ordering.csproj`
  and `Program.cs`. It references the desktop project and calls both tests;
  it does not modify the main tests project or its registration.
- Full results: `/tmp/opencode/c1-script-ordering-evidence/results.json`.
- Per-case journals, revision snapshots and `acceptance.json` are in the GUID
  subdirectories recorded in that JSON.
- Source hashes: `/tmp/opencode/c1-script-ordering-evidence/source-hashes.json`.

Follow-up results (all five cases rerun):
`/tmp/opencode/c1-script-ordering-evidence-followup/results.json`.
Use the same command below with that follow-up output directory to reproduce.
Exit **0**, no build warnings on this follow-up invocation.

The earlier-step case stops mid-`more`, selects revision 2, submits the same
`.rewind` inbox protocol as `live_runs.rewind_open`, and simulates the correction
delivery gap before submitting the fix. The retained document ends with only
Base and CorrectionMarker; LaterGone/CompletedMore remain in the saved journal
but not the restored model. TailA/TailB, which belonged to the abandoned future,
never execute. Every pre-Stop snapshot file remains byte-for-byte unchanged.
The correction note retains the selected revision number, not the later held
revision. No previously completed command re-executes during rewind.

The checkpoint case waits for the actual `checkpoint` state before Stop. Review
yields to the correction inbox; after the delayed fix, its preserved tail runs
once without re-committing or re-entering the old checkpoint. The idle/open case
acknowledges Stop without an in-flight mutation and accepts a correction in the
same run. All three also check the delivery gap and final mesh width.

```sh
dotnet run --project /tmp/opencode/c1-script-ordering-runner/Ordering.csproj -c Release --artifacts-path /tmp/opencode/c1-script-ordering-artifacts -- /tmp/opencode/c1-script-ordering-evidence
```

Exit **0**. One pre-existing kernel CA2014 warning in `ShadeEnvironment.cs`;
no compilation errors. Separate artifact root avoids the owner's bin/obj output.

## Boundaries of this evidence

- The original five tests prove the script review/inbox ordering seam, not QML
  button input/layout, installed app binary or the entire C1 ticket. Actual
  script-monitor delivery is now covered by the final round-trip test above.
- The external modeller is simulated by writing its actual corrective inbox
  batch after the delivery delay; no fake AI response is shown to the user.
- Native AI correction and animation previews are not covered by these five
  cases. The C1 owner retains those integration gates. Earlier saved-step script
  correction is now covered at the protocol/document level, not QML input.
- The standalone helper is NOT registered in `tests/TestRegistry.cs`; that file
  remains the owner's. To integrate, add registry callbacks that call
  `C1ScriptOrderingEvidence.Run(settings.WorkDirectory, false/true)` and fail
  when the returned `Passed` is false, preserving its `Error` and `Root`.
  Follow-up callbacks call `RunBoundaryCase(settings.WorkDirectory, scenario)`
  for `idle-open`, `checkpoint`, and `earlier-step` with the same pass/error check.
