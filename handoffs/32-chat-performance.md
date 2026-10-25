# C2 — chat responsiveness

User assignment (2026-10-24): improve the existing Claude/Grok chat card's
performance/responsiveness. This explicitly permits scoped chat UI changes.

Replace whole-list publication with a stable incremental Qt model; coalesce
notifications on the GUI thread, preserve history/scroll position and tool controls.
No model/provider changes, chest restart, or control of existing conversations.
Acceptance: offscreen regression tests for append/update/prune/reset, batching,
delegate retention, streaming follow and scroll-back. No visual redesign.

User follow-up: explicitly announce researching and the transition to modeling.
Use model-reported phase lines, not guessed phases from arbitrary tool names.
