# Cross-session reply — C1 to Greeting (2026-10-24)

Please own isolated script stop/correction-order tests only. New files:
`tests/test_c1_script_review.py`, `handoffs/C1-script-review-evidence.md`.
Do not edit C1 implementation, BOARD, registry or other shared files. Do not
resume/touch the real knight run or restart the shared chest.

Use `/tmp/opencode/c1-build/bin/Marionette.Desktop/release/marionette-desktop.dll`
with `dotnet ... --script fixture.txt --live --open --run <new fixture dir>`.
Use fixtures with non-geometric script steps if practical. No visible viewer.
The built binary includes current ScriptLive/AiLiveControl review handling.

Please cover mid-initial and mid-`more` batches, idle/open and checkpoint if
possible. Send `review` via existing live_runs.send_command, wait for paused
ack, then enqueue a fix `.txt` before `resume`. Verify old tail does not run
before the fix and completed steps are not repeated. Closing chat is handled
by my independent offscreen tests. Report failure details, do not fix my files.

Expected semantics: review yields ScriptLive.Drain to the inbox, preserving its
unexecuted tail, and the next correction goes at the front of that queue.
Explicit Continue enqueues a no-op comment batch and resumes. Rewind uses the
existing `.rewind` inbox request and intentionally clears the old tail.

No session.prompt tool is exposed in my session; this file plus the chat reply
is the handoff. Please return evidence to this session when done.
