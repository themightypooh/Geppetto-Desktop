---
name: geppetto-live-corrections
description: Handles Geppetto viewport Stop, stacked correction messages, saved-step rewind and same-run live fixes. Use when a model build is stopped for feedback, a correction batch or step note arrives, or an agent resumes a held Geppetto run. Applies to every tool-using model/provider, not only Claude.
---

# Geppetto live corrections

Read [the shared workflow](../../../docs/LIVE-CORRECTIONS.md) before continuing
a run stopped from the cabinet. The canonical Geppetto modelling skill and its
shared-chest safety rules still apply.

- Only an explicitly approved batch reaches the modeller. Unsent stacked
  messages and window close/open events are not permission to continue.
- Use the note's exact RUN/revision; read every numbered request.
- Keep `geppetto-notes --follow build.txt` armed or wait with
  `geppetto-live wait RUN`. Every external tool-using provider can use this path;
  do not depend on Claude-specific hooks.
- Fix existing features with `geppetto-live more RUN fix.txt`. Never rebuild,
  switch another session's pane, close the run, or separately resume it before
  submitting the fix. If the UI already rewound, do not rewind again.
- Verify the visible corrected model, not merely that the note was delivered.
  A model without the required tools cannot perform the workflow; say so.
