# C1 acceptance feedback from the knight-building session

2026-10-24. Owner remains the existing C1 session; this session is not editing
its implementation or shared files.

Owner feedback: "I need to see every step to be able to stop it at any step
and make comments." The user confirmed another session should implement this
and asked this session to ensure it lands and covers all paths.

## Required coverage before calling it shipped

- Initial live script builds AND `geppetto-live more` edits, including batches
  that change/export several knight variants in the same run.
- Native AI runs, held/open/waiting script runs, revision replay/rewind, and
  animation/clip previews: explicitly identify anything not supported.
- Same local Stop/comment interaction in one, two, four panes and solo view;
  operation description and current revision remain readable.
- Stop acknowledgement must correspond to the actually displayed saved step.
  No pending mutation may slip through while the comment UI opens or closes.
- Comment from a prior revision targets that revision and the SAME run. Never
  send it to the cabinet global chat or another modeller/pane.
- For external script modellers, confirm the note reaches the real monitor and
  the corrective `more` is applied before preserved script lines resume.
  `send()` currently sends `resume`: verify the ScriptLive review barrier makes
  this safe rather than assuming the modeller has already consumed the note.
- Show the corrected result before resuming ordinary work; closing the panel
  does not resume. Explicit Continue without correction remains available.
- Test the installed/running integration, not only new source or a fresh test
  window. Never restart the shared cabinet to deploy this without authorization.
- Small/default/large sizes and min/default/max text zoom; no clipped controls.

## Repro available for acceptance (do not resume without the owner)

Knight run:
`/home/poo/.local/share/geppetto/research/script-knight-helms-20261024-153652-753354`

Run ID: `a41327493d16475da3cf5eb6137634af`.
This session requested pause and confirmed `state=paused`, `control.paused=true`,
revision 111. A refinement batch is pending. Preserve it and the saved history.
The build uses a four-second pace and an active `geppetto-notes --follow` monitor.
The user saw "open: waiting for more lines" at the end of an earlier batch,
while offscreen table checks continued, and understandably thought work froze.
Make build progress versus validation/waiting explicit; offscreen diagnostics
must not be passed off as the user-visible modeling process.

Status here: C1 is assigned on BOARD.md; no C1 automated tests found yet by
filename discovery. This is NOT a verification or shipment claim.
