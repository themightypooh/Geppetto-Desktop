# C1 — viewport-local stop and correction

User-authorized UI exception (2026-10-24): only a Stop button and its correction
chat per viewport. Updated user direction: only Stop sits in the viewport;
correction chat uses a separate resizable, cabinet-styled window. Keep window
size independent of 1/2/4/solo layout, and keep its draft if the pane folds away.

Stop preserves the current model, stops at a safe operation boundary, and opens
feedback scoped to the captured run, pane and revision. Closing chat must not
resume. Feedback goes to that run's modeller, not the cabinet's global chat.
Never cancel a shared agent session or another pane's run. Pending script lines
must not race ahead of the correction. Existing revision history is retained.

Acceptance: headless backend regression; offscreen QML loading/input and bounds
checks with screenshots at small/default/large sizes and font scales. Never
restart or take over the running chest. External agent replies depend on its
existing notes hook/monitor; do not manufacture an AI response.

Chat audit follow-up: Send message stacks locally without resuming. Explicit
Apply & continue delivers the approved stack. Closing/opening never transmits.
Keep history across stops of the same run, show queued/delivered status, allow
removing unsent messages, and retain run/revision isolation. Publish the workflow
in the canonical provider-neutral Geppetto skill, not just a Claude-specific hook.
