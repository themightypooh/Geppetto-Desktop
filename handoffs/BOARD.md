# Assignment and evidence board

## PA1 — chat provider sign-in again (2026-10-26)

Direct user assignment for Geppetto Desktop chat, explicitly scoped UI permission.
Solo ownership: new `cabinet/provider_auth.py`, new `tests/test_provider_auth.py`,
provider account footer only in `cabinet/ClaudePanel.qml`, auth initialization,
properties/slots and send guards only in `cabinet/host.py`, matching appearance
fixture stubs if needed. Preserve other chat/gate work. Native CLI auth only;
no credential-file edits, automatic account changes, paid requests or chest restart.
Verify using mocked CLI processes and offscreen Qt at three scales.

Implemented (2026-10-27); running-chest deployment/live OAuth not exercised.
Provider picker has Sign in again for Claude (including personas), Codex, Grok,
Cursor and OpenCode. Fixed native CLI argv runs in a waiting terminal; user must
confirm there before logout/login. OpenCode adds an account for the selected
integration without deleting saved accounts. Other providers show local/key/app
guidance, not a fake login. No credentials read/captured, conversation reset or
background sign-out. Concurrent chat sends are blocked until the terminal closes;
completion/error refreshes discovery and unlocks. No visible launch/restart.

Evidence: `env -u DISPLAY -u WAYLAND_DISPLAY -u LD_LIBRARY_PATH -u LD_PRELOAD
QT_QPA_PLATFORM=offscreen QT_QUICK_BACKEND=software
CHAT_APPEARANCE_SNAP_DIR=/tmp/opencode/provider-auth-screens python3 -m unittest
tests.test_provider_auth tests.test_provider_stream -v` exits 0, **19/19**.
Includes fake native auth commands, cancellation, failure, real QProcess lifecycle
with harmless Python children, double-click/send guards, exact OpenCode integration,
actual QML click routing and 75/100/150% screenshots plus enlarged fallback fonts.
Screenshots inspected in `/tmp/opencode/provider-auth-screens/`; scoped diff check
exits 0. Broader chat snapshot (49 checks) has two untouched failures: Grok fixture
expects old permission mode `auto`; old appearance assertion expects removed
`claudeBuildMode`. Neither was weakened or claimed green. Real provider sign-in
and terminal/browser account selection remain user-driven, not an automated test.
Scoped production leases released.

## SH3 — Tamagotchi normals / generator winding (2026-10-26)

User explicitly requested investigating and fixing the normals. Solo scope:
canonical `~/games/projects/geppetto/Effigy/Primitives.cs`, new canonical
`Effigy.Tests/NormalOrientationTests.cs`, additive test registration in
`Effigy.Tests/Program.cs`, and scoped propagation into editor/Desktop mirrors.
Probe reproduced 96 same-direction shared edges per 24-segment cylinder: central
cap grid has correct winding, surrounding patches are reversed. Other primitive
families tested separately; boolean output also under investigation. Preserve
existing edits, no model-file edits, no live window takeover/restart.
Extended scoped ownership: `Effigy/CoplanarMerge.cs` planarity check,
`Effigy/ExactMeshBoolean.cs` input triangulation, `Editor/EffigyEditor/EffigyPreview.cs`
shared output triangulation and explicit Sandbox.Mesh alias. The Tamagotchi Boolean
produces merged nonplanar polygons with backward triangles; investigate with the
exact Grok fixture, not by flipping faces after the fact.
Also owns `Effigy/NormalBake.cs` geometry-first triangulation fallback and focused
regressions: a UV fold must not force backward geometric triangles.

Verified: canonical and Desktop kernel normal suites 33/33; hard-surface boolean
suite 49/49; isolated editor build succeeded with zero errors. The remaining
screen-pocket exact-boolean depth failure is unresolved and was not hidden by
dropping fragments. No live editor restart or model files changed.

## SH2 — stop losing live preview steps (2026-10-26)

User follow-up “no timeouts”: fix the preview-order failure, not its deadline.
Solo scope: `AiLivePreview.cs`, additive tests in `tests/AiLiveTests.cs`, and the
obsolete backlog claim in `docs/viewport-live-performance.md`. Root cause:
`MaxBacklog = 3` discards queued revisions that the every-step test awaits.
Preserve dwell, pending-mesh backpressure, replay and explicit join-to-current.
No shared chest restart or open-model changes.

Removed `MaxBacklog` and its queue truncation, keeping one pending loaded mesh and
lightweight ordered paths. Added CPU slow-consumer regression with a 12-revision
burst. Original hardware-EGL every-revision test kept unchanged, including its
60-second deadline. Full `ai-live/` now passes 14/14, 0 skipped, including the
previously timing-out test and the 49k-triangle latency test:
`dotnet run --project tests/Desktop.Tests.csproj -c Release --artifacts-path
/tmp/opencode/geppetto-preview-order -- --filter ai-live/ --report
/tmp/opencode/geppetto-preview-order-after.json` (exit 0).
Log `/tmp/opencode/geppetto-preview-order-test.log`. Normal Release build also
passed (exit 0), log `/tmp/opencode/geppetto-preview-order-build.log`.
Next-launch binaries updated; existing viewer processes retain old loaded code.

## BF1 — remove automatic judging from builds (2026-10-26)

User superseded the quality roadmap: remove extra judge/quality machinery and let
builders work. Solo scope: `AiCli.cs`, `tests/AiTests.cs`, workflow text only in
`cabinet/chat_phase.py` and `cabinet/research_consent.py`, with corresponding
`tests/test_gate_prompt.py`, `tests/test_join_gate_poll.py` and
`tests/test_research_consent.py` assertions; update `AI.md` to match.
Preserve execution protocol from the concurrent gate work. Remove automatic rubric,
vision judging, score vetoes, corrective loops and score-selected rollback from
the session path; shorten the builder prompt. Keep legacy CLI flags harmless for
existing launchers; keep manual inspection tools and standalone judge independent.
Do not touch SH1's healer removal or other sessions' gate/UI work. Preserve file
sandbox, budgets, cancellation, pane controls and saved output. CPU regression
verification: **25/25** `ai/` fixtures pass via
`dotnet run --project tests/Desktop.Tests.csproj -c Release --artifacts-path /tmp/opencode/geppetto-builder-free-build -- --filter ai/ --report /tmp/opencode/geppetto-builder-free-focused.json`
(exit 0); **19/19** Python prompt/policy/seat-propagation tests pass with
`env -u DISPLAY -u WAYLAND_DISPLAY -u LD_LIBRARY_PATH -u LD_PRELOAD QT_QPA_PLATFORM=offscreen python3 -m unittest tests.test_gate_prompt tests.test_research_consent.PolicyTests tests.test_join_gate_poll.JoinGatePollTests.test_every_tool_capable_cabinet_seat_gets_the_same_prep -q`
(exit 0). Also removed the hidden 24-round builder cap; explicit call/time budgets
still apply. Broader `--filter ai` run: 146 pass, 2 live-preview failures (ordered-frame
timeout and 502.9 ms vs 500 ms latency); not fixed in this scope. Gate discovery
after clearing Steam library variables ran 109 checks with 2 failures: one stale
creative-prompt assertion now updated/passing, and the untouched launcher TERM
cancellation fixture returns 0 instead of 130 (also fails alone). Logs:
`/tmp/opencode/geppetto-builder-free-{focused,gates-clean}.log`.
Scoped diff whitespace checks pass. No shared chest restart, existing chat reset,
normal-bin deployment or paid requests; build verification uses isolated artifacts.

## SH1 — remove builder healer; repair STEP mutation gates (2026-10-26)

Direct user assignment: “axe healer fix bug”. Solo current session owns
`AiBuilderHealer.cs`, `tests/AiBuilderHealerTests.cs`, the healer registration
line in `tests/TestRegistry.cs`, new STEP safety tests, additive fixtures in
`tests/OpenScriptStepTests.cs`, and only the mutation classifier in both
`kernel/Effigy/Script/BuildScript.cs` and canonical
`~/games/projects/geppetto/Effigy/Script/BuildScript.cs`. Regenerate only that
editor mirror file, preserving all unrelated drift. No chest restart or model edits.
Also owns additive canonical `Effigy.Tests/StepPlayerTests.cs` regressions.

Implemented: deleted builder healer and its strategy tests/registration; replaced
with an assembly-absence regression. Both kernel classifiers gate new/unknown verbs
by default, exempting only named captions/reports/output commands. Regenerated only
canonical `BuildScript.cs` into `Editor/Effigy` after confirming the classifier was
its only difference. Actual open-run var/rebuild STEP fixture and production-delegate
s&box player regressions added. No models or live processes touched/restarted.

Verification: isolated `dotnet run --project tests/Desktop.Tests.csproj -c Release
--artifacts-path /tmp/opencode/geppetto-step-safety -- --filter step-mutation-safety/`
first reproduced `bone bypasses STEP` (2 passed/1 failed), then 3/3 passed after fix;
`--filter open-script-step/` passed 4/4. Reports:
`/tmp/opencode/geppetto-{step-before,mutation-after,step-after}.json`.
Canonical `dotnet run --project Effigy.Tests/Effigy.Tests.csproj -c Release
--artifacts-path /tmp/opencode/geppetto-sbox-step-safety -- --test StepPlayerTests`
passed 95 checks. Normal desktop `dotnet build Marionette.Desktop.csproj -c Release
--no-restore -v quiet` succeeded (22 warnings/0 errors), updating next-launch binaries.
Existing workers retain loaded old code. Broader `ai-live/` verification was NOT clean:
12 passed/1 failed; hardware-EGL “viewer shows every revision in order” timed out in
Viewer.Wait (OperationCanceledException). Report `/tmp/opencode/geppetto-ai-live-after.json`.
No unrelated preview fixes attempted. Engine sbox-cli unavailable at installed path;
s&box verification is engine-free CPU player testing, not an editor launch.

## CP2 — workshop radio chat redesign (2026-10-25)

Current user assignment: make the chat window more distinctive; explicitly permits
this scoped UI redesign. Solo ownership: `cabinet/ClaudePanel.qml`, new offscreen
chat appearance fixture/checks, and chat placement bounds only if needed in
`cabinet/cabinet.qml`. Preserve all existing model/mode/research/approval/streaming
wiring and other sessions' edits. Verify offscreen, with screenshots at minimum,
default and maximum scales and narrow/expanded sizes. No chest restart or real
conversation/provider calls. In progress.

## CP1 — walkie chat visual pass (2026-10-25)

User explicitly requested a new chat-panel look, superseding the handoff queue's
no-UI scope for this pass. Solo scoped edits: `cabinet/ClaudePanel.qml`, the
`placeChatByWalkie` function in `cabinet/cabinet.qml`, and provider selection
persistence in `cabinet/host.py`. The panel has a bottom grille tail and full
pixel-assembly opening, with no scan sweep; header, provider choice, mode and
composer were spaced for compact/default/expanded heights. Pause/Stop were
removed from this panel. Chat now opens without restoring a provider; favorites
still persist. Offscreen QML load, chat mode tests, provider-start probe and
screenshots at three sizes passed. The full cabinet suite has unrelated active
work failures and is not acceptance evidence. No visible chest restart.

## UIH1 — hanging file menu (2026-10-25)

User-assigned current session. Scoped ownership: the GEPPETTO nameplate menu block in
`cabinet/cabinet.qml`, new hanging-menu physics/assets/tests under `cabinet/` and `tests/`,
and a reusable project skill. Preserve all other ongoing UI edits. Verify the moving
SAVE / SAVE AS hit targets and hover state offscreen at small/default/large scales.
No chest restart or visible test viewer.

## VS1 — viewport step speed (2026-10-25)

Current angler session owns `cabinet/RunPaceSlider.qml`,
`tests/test_run_pace_slider.py`, and `handoffs/35-viewport-step-speed.md`.
Scoped integrations: RunBar only in `cabinet/cabinet.qml`; new `setPace` slot
beside cyclePace only in `cabinet/host.py`. Preserve all other sessions' edits.
No restart or real-run control changes for verification. Status: verified in
offscreen Qt; 7 control/integration checks + 1 protocol check passed.
Screenshots: `artifacts/viewport-step-speed/`. Live cabinet activation pending
its next launch; current session left open.

## RC1 — agent reference-image controls (2026-10-25)

User-assigned, current Codex session, in progress. Scoped ownership: new
`ReferenceImageControl.cs`, `cabinet/reference_control.py`,
`tests/ReferenceImageControlTests.cs`, `tests/test_reference_control.py`,
`handoffs/34-reference-control.md`; integration only in `ServeMode.cs`,
`cabinet/host.py`, `cabinet/panes.py`, `tests/TestRegistry.cs`, and the
reference subcommand of `~/.local/bin/geppetto-live`. No UI or kernel edits,
no shared chest restart and no real-run changes.

## C5 — Build execution proof and usable modes (user escalation)

Current C1 session owns new `cabinet/build_execution.py` and its new tests;
scoped integration leases transferred from C3: mode-capability predicates and
ClaudeLink construction in `cabinet/host.py`, mode-key enablement/tooltips in
`cabinet/ClaudePanel.qml`, child environment / stream wrapper / mode arguments
in `cabinet/chat.py`, and result verification / mode permission in
`cabinet/claude_link.py`. Preserve all unrelated C3/policy/performance edits.
Also owns an opt-in receipt hook only in `~/.local/bin/geppetto-live`.
No provider-specific exemptions, no paid/live model tests, no real run changes,
no chest restart. Plan stays read-only; Build text is not execution evidence.

## C3 — visible chat Build/Plan repair (2026-10-24)

Assigned to current repair session by user's screenshot/help request. Owns scoped
additions to `cabinet/ClaudePanel.qml`, `cabinet/host.py`,
`cabinet/chat.py`, `tests/test_chat_modes.py`, and Model Hub's
`providers.py` Codex/OpenCode launch functions only. Preserve existing edits.
Verify actual visible card routing and workspace permissions; no chest launch.

## C4 — asset workflow audit and centered viewport pause (2026-10-24)

User placement correction supersedes center placement: **lower center of each
viewport**. C1 placement follow-up owns only the pause-button anchor lines in
`cabinet/ViewportCorrection.qml` and matching location assertions/screenshot
names in `tests/test_viewport_correction.py`; C4 retains all other audit work.
No viewer launch, restart or real-run control for this placement-only change.

Current repair session, user-authorized follow-up. Owns
`cabinet/ViewportCorrection.qml`, `tests/test_viewport_correction.py` center/scale
assertions, and audit evidence in this board. Audit the live asset pipeline;
do not touch real runs or open the closed chest.
Also repair the earlier session's unsafe startup wait in
`tests/ControllerFixTests.cs` (test-only directory race).

C3 user follow-up: inline per-chat research opt-in (papers/photos/licensed
reference models). Additional owned files: new `cabinet/research_consent.py`,
new `tests/test_research_consent.py`, and scoped `cabinet/claude_link.py` policy
propagation. No global consent, provider credentials, or automatic asset imports.

User-authorized parallel audit leases: provider-portability agent is read-only
except optional new `tests/test_provider_portability_audit.py`; control-audit
agent is read-only except optional new `tests/test_asset_control_audit.py` and
isolated `/tmp/opencode` harnesses. Neither owns shared production files.

Provider-portability follow-up lease explicitly transferred: that agent alone
now owns `cabinet/roundtable_catalog.py`, new `cabinet/provider_cli.py`,
`tests/test_provider_portability_audit.py`, and NumPy declaration/import-check
lines in `packaging/package-linux.sh`. Main retains chat/host/policy files.

Control-audit follow-up lease: control agent owns `AiLiveControl.cs`,
`ScriptLive.cs`, new `tests/OpenScriptStepTests.cs`, and only its registration in
`tests/TestRegistry.cs`, for STEP after script pause. Main owns note-file race
repair in `cabinet/live_runs.py` and new `tests/test_note_atomicity.py`.

### C3/C4 integration evidence — 2026-10-25

Implemented, release certification **blocked**. Visible card now has working
Codex/OpenCode Build/Plan and per-conversation research choices (papers, licensed
photos, CC0/public-domain models). First send remains local until Apply/Skip;
all choices default off, model switches keep them, New chat resets them. Policy
is carried in actual provider prompts/bodies. It is **not** a research-category
network sandbox or an automatic licence verifier. Ordinary HTTP/Ollama chat has
no new tools simply because a category is selected.

Bundled Codex/OpenCode routing no longer requires the developer's Roundtable or
Model Hub checkouts; exact model/variant selection and each user's own CLI auth
are retained. HTTP tuning cannot override selected model/history/policy.
Cross-origin provider redirects are refused. Production Claude card uses the
environment allowlist. Release workspace is user-owned data, not installation;
development checkout remains editable. NumPy is declared and launcher-checked.

Approved notes and draw-over annotations now append receipts/metadata instead
of replacing notes.jsonl (including the host's second draw-over writer), so
concurrent approvals are preserved. New notes have unique IDs. STEP after a
script review pause now wakes the inbox; repeated STEP and correction-first
ordering have executable regressions. Watch labels retain the actual operation
source line rather than falsely pointing at an unexecuted script tail.

Evidence (all commands exited 0 unless explicitly stated):
- Focused Python chat/provider/consent/notes suites: **94/94**,
  `/tmp/opencode/asset-audit-focused-final.log`.
- Offscreen consent/mode/correction tests: **24/24** at 50%, 75%, 100%, 150%,
  200% DPI/text test combinations. Screenshots under `/tmp/opencode/c1-previews/`
  and `/tmp/opencode/research-consent-*.png`; high-DPI consent capture needs
  rendering settled, final 200% proof is `research-consent-2-final.png`.
  C1's subsequent lower-center placement is a separately owned follow-up.
- Actual normal-build CPU fixtures: STEP **3/3**, viewport corrections **6/6**,
  controller-fixes **12/12** (`asset-audit-final-*.log`). Save filter **33/33**,
  export filter **17/17**, FBX/DMX **9/9**; groups overlap, do not sum as a full run.
- Full native suite after integration: **652 pass / 29 fail / 8 skip**,
  **689/689 selected**, exit 1; `/tmp/opencode/asset-audit-cpu-final.log` and
  `/tmp/opencode/geppetto-desktop-tests-5c95bb183f7d4c7cb21ff90c6b1465f7/results.json`.
- Full Python discovery snapshot: **272 tests / 6 failures**, exit 1,
  `/tmp/opencode/asset-audit-python-final.log`; two additional wire tests were
  subsequently added and pass in the focused 94-test run.
- Normal Release/tests build succeeds (warnings remain); Linux package staged
  at `/tmp/opencode/geppetto-audit-release-final`, exit 0. This is a candidate,
  not certification that the entire asset-making workflow is shippable.
- No interactive chest launch/restart or real-run manipulation by these audits.

Remaining release blockers / limitations, not hidden by the focused passes:
1. Explicit foreign-session pane commands are not ownership-authorized; owner
   tracking is primarily Claude-specific. Exact-run monitors are safer than
   script-name monitors shared by several runs.
2. A close request behind an edit consumed while paused can remain blocked.
   Checkpoint rewind alone does not release the checkpoint gate; checkpoint
   `more` without review runs the old tail first. Checkpoint notes omit script
   identity and can be missed by script-filtered followers.
3. Package still omits portable live/research/notes helper entrypoints and the
   versioned modelling skill/API reference that prompts require. Optional
   non-native Model Hub providers retain external dependencies; Cursor is
   ask-only and Model Hub OpenAI-compatible routing/config needs separate work.
4. Live-script save and bench KEEP bypass atomic DocumentSaveService;
   passing service tests do not certify their overwrite/crash behaviour.
5. Many GPU tests still construct 20-float fixtures while production uses 24;
   renderer failures cannot be honestly classified until fixtures are repaired.
   Reload/DocumentLoader failures, live revision delivery/latency, selected
   face/edge token rejection and six legacy QML/part-focus failures remain.
6. Research approval, licence provenance, reference-model non-reuse and 9+ judging
   are not uniformly hard gates across all providers. Script standalone judge
   explicitly leaves vision/reference gates unscored; native judging may fall
   back to vision-only when measurements fail. No in-engine s&box compile/load
   or newly authored full asset was certified in this audit.

Independent proof of the pre-fix control/note/pane findings is under
`/tmp/opencode/asset-control-audit/`. Some broad-suite failures are stale
expectations, others genuine integration gaps; do not call the product green.

## C1 — viewport-local stop and correction (2026-10-24)

`implemented-unverified` (running-chest deployment gate open), current agent. User explicitly authorizes only Stop and its
viewport-local correction chat, superseding the old no-UI scope for this ticket.
Brief: `handoffs/31-viewport-correction.md`. Owned: `cabinet/ViewportCorrection.qml`
(new), additive `cabinet/cabinet.qml`, `cabinet/host.py`, `cabinet/viewport_correction.py`
(new), `AiLiveControl.cs`, `ScriptLive.cs`, new correction tests, and this board.
No kernel edits; preserve existing user changes in StudioDocument/BuildScript.
Greeting session (user-authorized independent testing) owns only new
`tests/test_c1_script_review.py` and `handoffs/C1-script-review-evidence.md`;
assignment: `handoffs/C1-testing-reply.md`. No shared implementation edits.
C1 integration additionally leases `tests/TestRegistry.cs` (registration only)
and new `tests/ViewportCorrectionTests.cs`, plus additive `AiLiveRun.cs` control
capability publication. Pop-out design replaces the initial
in-viewport chat per the user's follow-up; only Stop remains over the model.
Evidence: `handoffs/C1-OWNER-ACCEPTANCE.md`. Focused script/native control checks
6/6, Python/controller/pop-out checks 10/10 at each of three text/DPI scales,
full cabinet QML load 1/1. Greeting's real notes follower round-trip passed.
Aggregate suite is not certified (unrelated existing compile/test failures).
Shared chest and knight run were not restarted, resumed or controlled.
Final `dotnet build Marionette.Desktop.csproj -c Release`: exit 0, 0 warnings,
0 errors; normal `bin/Release/net8.0` output updated. `git diff --check`: exit 0.
C1 implementation/shared-registration leases released; deployment remains open.

C1 chat-audit follow-up (user requested): current agent re-leases
`cabinet/viewport_correction.py`, `cabinet/ViewportCorrection.qml`,
`tests/test_viewport_correction.py`, C1 docs, and the canonical Geppetto skill's
correction-workflow reference/link only. Scope: explicit multi-message queue,
retained history, no auto-send on window events, model-neutral instructions.
No C# changes, other pane/run control or cabinet restart.
Follow-up implemented: explicit queue/apply, per-message states/removal, retained
same-run history, fixed pinned composer, shared auto-discovered skill. Evidence:
`handoffs/C1-OWNER-ACCEPTANCE.md` follow-up. Python **15/15 at all three scales**,
cabinet QML load **1/1**, real stacked notes/fix round-trip exit 0. No auto-send
on Enter/close/reopen. No running-cabinet deployment claim; leases released.
Owner subsequently authorized loading the cabinet. Updated GUI started and its
socket/screenshot were checked; `handoffs/C1-OWNER-ACCEPTANCE.md` has evidence.
Knight runner remains unchanged, paused at revision 111 / sequence 227. No live
correction test was performed against that old runner or another session's pane.

Last reconciled against the source on **2026-10-20** by the coordinator.

Allowed states: `unassigned`, `assigned`, `blocked`, `implemented-unverified`, `verified`.
One coordinator updates ownership. Workers report evidence; they do not certify themselves.

**Current baseline on this machine (2026-10-20):**
`dotnet run --project tests/Desktop.Tests.csproj -c Release` → **496 passed, 0 failed, 7 skipped;
503/503 selected/registered** (the 7 skips are EGL-gated probes that need `V3_REQUIRE_EGL=1`).
Rerun before quoting a number; do not reuse this line as your own result.

**What is not true any more.** The 35 first-wave tickets are all `verified`, but the project is
**not** "finished except for UI": wave-1 backend services have landed without viewport
integration, 3 766 lines of service code have no caller and no test, and the renderer itself is
still an LDR forward pipeline without AA, real IBL, correct screen-space effects or temporal
accumulation. The renderer wave below is the plan that closes that gap.

## 1. First wave — backend tickets (35/35 verified)

| Ticket | State | Owner | Allowed files | Verified prerequisites | Test/evidence paths / blocker |
|---|---|---|---|---|---|
| V1 | verified | current agent | tests/*, README.md, handoffs/BOARD.md, handoffs/RESTART.md | baseline retained | 30 CPU + 8 harness tests, required hardware EGL passed; tests/README.md links exact reports and negative CLI cases |
| P1 | verified | current agent | AssetPathResolver.cs, tests/AssetPathTests.cs, tests/TestRegistry.cs, documentation | V1 verified | 15 resolver fixtures; 45 CPU + 8 harness + real EGL pass; tests/README.md has API/evidence; loader integration is E1/P2 |
| E1 | verified | current agent | DocumentSession.cs, DocumentLoader.cs, tests/DocumentSessionTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | P1 verified | 10 session fixtures; 55 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-d65kom3b/verification.json; next is E2 or M1 |
| M1 | verified | subagent-m1 (certified by coordinator) | MaterialDescriptor.cs, tests/MaterialDescriptorTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | P1 verified | 13 material fixtures; 113 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-f8gagctb/verification.json; next is M2 |
| M2 | verified | current agent | ViewportMesh.cs, SceneRenderer.cs, Program.cs, DocumentSession.cs, tests/MaterialGpuTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | M1 verified | 3 material fixtures; 300/300 CPU and required EGL PASS (/tmp/opencode/geppetto-verify-fuoe05l7/verification.json). Per-corner UV, sRGB albedo, GL upload/release, seam and asymmetric pixel probes; explicit project overrides still require a caller. Shared-file lease released for M3. |
| M3 | verified | subagent-m3 (certified by coordinator) | ViewportMesh.cs, SceneRenderer.cs, Program.cs, tests/MaterialChannelGpuTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | M2 verified | 20-float vertex stride (`FloatsPerVertex=20`), `layout(location=5)` tangent, sampler units 0-6 bound in the SceneRenderer ctor, per-batch channel binds, fragment shader samples all 6 maps; 4 hardware EGL fixtures including nonuniform bone-palette tangent frame; 343/343 CPU and required EGL PASS (/tmp/opencode/geppetto-final3/geppetto-verify-x3tc_7zg/verification.json); shared-file lease released |
| M4 | verified | subagent-m4 (certified by coordinator) | SurveyMaterialMap.cs, ResourceBudget.cs, DocumentLoader.cs, DocumentSession.cs, ViewportMesh.cs, README.md, tests/SurveyMaterialMapTests.cs, tests/ResourceBudgetTests.cs, tests/PoseAgreementTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | M1, M2, M3 verified | (1) read-only Survey mapping inventory + non-Survey fallback via `SurveyMaterialMap.ForSourcePath`/`Resolver` and `DocumentSession.SurveyModelReference`/`ResolveSurveyReference`; (2) pose agreement — one 20-float skinned 2-bone mesh asserted across skinning, picking and the shadow pass with cross-over probes under both palettes; (3) `MeshResourceBudget` with process-wide live texture/byte/reload counters wired into `ApplyMaterials`/`Upload`/`ReleaseGpu`/`Texture`/`Extract`; (4) unsupported `ps1_lit` palette/wear/glow/filtering effects documented in README and named by runtime diagnostics; 343/343 CPU and required EGL PASS (/tmp/opencode/geppetto-final3/geppetto-verify-x3tc_7zg/verification.json); lease released |
| E2 | verified | current agent | EditHistory.cs, DocumentSession.cs, tests/EditHistoryTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | E1 verified | 10 history fixtures; 65 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-2kux_i63/verification.json; next is E3, E4, or M1 |
| E3 | verified | current agent | ClipEditService.cs, MarionettePlayer.cs, DocumentSession.cs, tests/ClipEditTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | E1, E2 verified | 10 clip fixtures; 75 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-gtcj89ru/verification.json; next is E4, M1, or G1 |
| E4 | verified | coordinator | DocumentSaveService.cs, DocumentSession.cs, EditHistory.cs, tests/DocumentSaveTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | E1, E2, P1 verified | 10 save fixtures; 85 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-l5_3mn3j/verification.json; next is E5, M1, or G1 |
| E5 | verified | subagent-e5 (certified by coordinator) | ParametricEditService.cs, tests/ParametricEditTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | E1, E2, E4 verified | 8 parametric fixtures; 160 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-ow253_2u/verification.json |
| A1 | verified | subagent-a1 (certified by coordinator) | ClipCapabilityValidator.cs, tests/ClipCapabilityTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | baseline verified | 10 clip audit fixtures; 160 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-ow253_2u/verification.json; next is A2 |
| A2 | verified | subagent-a2 (certified by coordinator) | MorphEvaluator.cs, tests/MorphEvaluationTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | A1, E1 verified | 11 morph fixtures; 294 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-j63uc_l2/verification.json |
| A3 | verified | subagent-a3 (certified by coordinator) | ClipBlendService.cs, tests/ClipBlendTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | A1, E1 verified | 12 blend fixtures; 236 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-2jtholaj/verification.json |
| A4 | verified | subagent-a4 (certified by coordinator) | LiveIkSolverBuffers.cs, MarionettePlayer.cs, tests/LiveIkBufferTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | A1 verified | 5 live IK buffer fixtures including a real warmed player `Step` at 0 B over 2000 calls; recertified against the M3 20-float vertex layout; 343/343 CPU and required EGL PASS (/tmp/opencode/geppetto-final3/geppetto-verify-x3tc_7zg/verification.json); unsupported spline/object-space/limits/root-motion still use the kernel fallback; lease released |
| A5 | verified | subagent-a5 (certified by coordinator) | TimelineEventDispatcher.cs, tests/TimelineEventTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | A1, E1 verified | 12 event fixtures; 236 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-2jtholaj/verification.json |
| A6 | verified | subagent-a6 (certified by coordinator) | PartTrackEvaluator.cs, tests/PartTrackTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | E1, A1 verified | 10 part/camera track fixtures; 294 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-j63uc_l2/verification.json |
| A7 | verified | subagent-a7 (certified by coordinator) | ConstraintParityInventory.cs, packaging/CONSTRAINTS.md, tests/ConstraintParityTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | A1 verified | 8 constraint fixtures; 294 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-j63uc_l2/verification.json |
| G1 | verified | subagent-g1 (certified by coordinator) | GizmoMath.cs, tests/GizmoMathTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | none | 15 gizmo math fixtures; 113 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-f8gagctb/verification.json; next is G2 |
| G2 | verified | subagent-g2 (certified by coordinator) | PoseEditService.cs, DragSession.cs, tests/PoseEditTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | G1, E1, E2 verified | 10 pose fixtures; 160 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-ow253_2u/verification.json; next is G3 |
| G3 | verified | subagent-g3 (certified by coordinator) | IkTargetService.cs, tests/IkTargetTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | G2, A1 verified | 12 IK target fixtures; 236 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-2jtholaj/verification.json; next is G4 |
| G4 | verified | subagent-g4 (certified by coordinator) | ViewportHandleService.cs, SkeletonOverlay.cs, DragSession.cs, IkTargetService.cs, ClipEditService.cs, EditHistory.cs, Program.cs, tests/ViewportHandleTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | G1, G2, G3, E3 verified | Typed handles drawn through `SkeletonOverlay.EmitHandleLines` with no synthesized arrowheads or constant-size axes, so drawn geometry and hit geometry share one source; Program.cs routes hover/press/move honoring `WantsPointerCapture`, Escape and focus-loss and window-close cancel, source-reload cancel+dispose, rebuilds the gizmo at camera-relative scale, and exposes a deliberate `K` auto-key toggle that seeds the key frame from the playhead; item 4 keying policy is now implemented, not stubbed — auto-key off keeps the unkeyed pose override, auto-key on writes FK local bone keys and live IK goal/weight keys through E3 joined into the drag's own transaction so the pose and the key undo/redo as one entry; 10 fixtures; 343/343 CPU and required EGL PASS (/tmp/opencode/geppetto-final3/geppetto-verify-x3tc_7zg/verification.json); lease released |
| I4 | verified | subagent-i4 (certified by coordinator) | FbxClipConverter.cs, DocumentLoader.cs, tests/FbxClipConversionTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | I3, I1 verified | Native clip conversion from the approved FBX subset; `DocumentLoader.ConvertFbxClip` reads clip bytes up front and defers conversion, writing `.clip.json` beside the source, and throws `NotSupportedException` for a non-FBX document carrying an FBX clip; 343/343 CPU and required EGL PASS (/tmp/opencode/geppetto-final3/geppetto-verify-x3tc_7zg/verification.json); lease released |
| I1 | verified | subagent-i1 (certified by coordinator) | ExportService.cs, tests/ExportServiceTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | E1, P1, E4 verified | 9 export fixtures; 160 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-ow253_2u/verification.json; next is I2 |
| I2 | verified | subagent-i2 (certified by coordinator) | FbxExportAdapter.cs, DmxExportAdapter.cs, tests/FbxDmxExportTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | I1 verified | 9 FBX/DMX export fixtures; 236 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-2jtholaj/verification.json; next is I3 |
| I3 | verified | subagent-i3 (certified by coordinator) | FbxAnimationFeasibility.cs, tests/FbxAnimationFeasibilityTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | baseline verified | 9 FBX animation feasibility fixtures; 294 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-j63uc_l2/verification.json; next is I4 |
| P2 | verified | subagent-p2 (certified by coordinator) | DesktopProject.cs, tests/DesktopProjectTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | P1, E1, E4 verified | 10 project manifest fixtures; 160 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-ow253_2u/verification.json; next is P3 |
| P3 | verified | subagent-p3 (certified by coordinator) | DependencyWatcher.cs, tests/DependencyWatcherTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | P1, P2, E1, E4 verified | 10 watcher fixtures; 236 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-2jtholaj/verification.json |
| P4 | verified | subagent-p4 (certified by coordinator) | DesktopPreferences.cs, AutosaveScheduler.cs, tests/PreferencesAutosaveTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | P1, P2, E4 verified | 13 preferences fixtures; 236 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-2jtholaj/verification.json |
| D1 | verified | subagent-d1 (certified by coordinator) | packaging/INVENTORY.md, DistributionInventory.cs, tests/DistributionInventoryTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | P1, P2 verified | 8 distribution fixtures; 236 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-2jtholaj/verification.json; next is D2 |
| D2 | verified | subagent-d2 (certified by coordinator) | packaging/package-linux.sh, tests/PackagingTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | D1, P1, P2 verified | 9 packaging fixtures; 294 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-j63uc_l2/verification.json; next is D3 |
| D3 | verified | current agent | packaging/smoke-test.sh, packaging/sample-rig.fbx, packaging/package-linux.sh, tests/InstalledArtifactSmokeTests.cs, tests/PackagingTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | D2 verified | 3 installed-artifact fixtures; 297/297 CPU and required EGL PASS (/tmp/opencode/geppetto-verify-u36bvyxq/verification.json). Extracted archive from unrelated cwd; read-only install, sanitized env with no SDK executable on PATH, owned rigged FBX/clip, deterministic decoded 128x96 pixels, bad-input diagnostic; host and ldd logs /tmp/opencode/geppetto-d3-no-sdk-path/. Second GPU/clean physical host remains untested. |
| V2 | verified | subagent-v2 (certified by coordinator) | tests/DeformationInvariantTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | V1, baseline verified | 11 deformation invariant fixtures; 294 CPU + 8 harness + real EGL pass; report /tmp/opencode/geppetto-verify-j63uc_l2/verification.json |
| V3 | verified | subagent-v3 (certified by coordinator) | HeadlessImageChecks.cs, tests/HeadlessImageTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | V1, baseline verified | Headless EGL lighting/material/shadow image checks, gated behind `V3_REQUIRE_EGL` (tests/verify.py sets it in required-egl mode); camera/project/mean-luma helpers are reused by M4's pose-agreement probes; 343/343 CPU and required EGL PASS (/tmp/opencode/geppetto-final3/geppetto-verify-x3tc_7zg/verification.json); lease released |
| V4 | verified | subagent-v4 (certified by coordinator) | PerformanceLifecycleMatrix.cs, tests/PerformanceLifecycleTests.cs, tests/TestRegistry.cs, handoffs/BOARD.md | V1, D2 verified | 5 lifecycle fixtures including hardware upload/completion and CPU timers, gated behind `V4_REQUIRE_EGL`; recertified against the M3/M4 renderer; 343/343 CPU and required EGL PASS (/tmp/opencode/geppetto-final3/geppetto-verify-x3tc_7zg/verification.json); other GPU/OS and present/vsync remain untested |

## 2. Second wave — modelling services (backend merged, not integrated)

Owners were agents working in their own copies; the coordinator merged the owned files into
this folder. Every row below is backend-only: **nothing here is reachable from the viewer
yet**, and no row may be described as a shipped feature until R9 wires it.

| Ticket | State | Owner | Owned files | Note |
|---|---|---|---|---|
| 12 remesher | implemented-unverified | agent t12 | RetopoService.cs, CrossFieldService.cs, tests/RetopoTests.cs, tests/CrossFieldTests.cs, tests/RemeshTests.cs | `RemeshQuality.cs` never landed |
| 13 texture paint | implemented-unverified | agent t13 | TexturePaintService.cs, tests/TexturePaintTests.cs | `TexturePaintLayers.cs` landed with no test and no caller; `GpuTexturePaintService.cs` never landed |
| 14 UV | implemented-unverified | agent t14 | UvEditService.cs, UvSeamService.cs, tests/UvEditTests.cs, tests/UvSeamTests.cs | `UvUnwrapService.cs`, `UvPackService.cs` never landed |
| 15 cloth | implemented-unverified | agent t15 | ClothSimService.cs, ClothSimGpuService.cs, tests/ClothSimTests.cs, tests/GpuClothTests.cs | `GarmentDrapeService.cs` never landed |
| 17 particles | implemented-unverified | agent t17 | GpuParticleSystem.cs, tests/GpuParticleSystemTests.cs | `ParticleSystem.cs` landed with no test and no caller; `ParticleBake.cs` never landed |
| 18 geometry nodes | implemented-unverified | agent t18 | GeometryNodes.Core.cs | No test, no caller, no `tests/GeometryNodeTests.cs` |
| 19+20 modifiers/shrinkwrap | implemented-unverified | agent t19 | ShrinkwrapService.cs, tests/ShrinkwrapTests.cs | `ModifierStack.cs`, `Modifiers*.cs` never landed |
| 21 data transfer | implemented-unverified | agent t21 | DataTransferService.cs, tests/DataTransferTests.cs | — |
| 22+25 weights | implemented-unverified | agent t22 | WeightPaintService.cs, AutoWeightService.cs, tests/WeightPaintTests.cs, tests/AutoWeightTests.cs | Viewport overlay pending |
| 23 advanced IK | implemented-unverified | agent t26 | AdvancedIkService.cs, tests/AdvancedIkTests.cs | Landed early; was deferred to wave 2 |
| 24 morph editing | verified | agent t24 | MorphEditService.cs, tests/MorphEditTests.cs | — |
| 26 IK variants | not started | agent t26 | — | `IkSolvers.cs`, `tests/IkSolverTests.cs` never landed |
| 27 curved IK | not started | — | — | — |
| 28 euphoria | implemented-unverified | agent t26 | EuphoriaService.cs, tests/EuphoriaTests.cs | Landed early; was deferred to wave 2 |
| 29 hair | implemented-unverified | agent t26 | HairStrandService.cs, tests/HairStrandTests.cs | Landed early; was deferred to wave 2 |
| foundations 11 | implemented-unverified | agent t11 | RetopoService.cs, KnifeService.cs, ShrinkwrapService.cs, SdfShapes.cs, DataTransferService.cs, PolypaintService.cs, UvEditService.cs, DyntopoService.cs, ClothSimService.cs | See `handoffs/11-foundations.md` |
| AI1 live acceptance | assigned | agent ai1 | AiCli.cs, AiProvider.cs, AiJudge.cs, AiResearch.cs, AiSuiteTools.cs, AiToolWorker.cs, AI.md, tests/AiTests.cs | Live-provider acceptance pending |
| L1 local chat client | implemented-unverified | current agent | tools/LocalChat/* (all new) | Standalone; does not touch the viewer |

**Still deferred:** all viewport / `SceneRenderer` / `Program.cs` integration of the wave-1
services (coordinator, serial), and tickets 26/27.

### 2a. No caller and no test — not counted as features (3 766 lines)

Verified by grep over non-test sources on 2026-10-20. Left untouched pending the R11
wire-or-delete decision:

`ParticleSystem.cs`, `GeometryNodes.Core.cs`, `BaseMotionLibrary.cs`, `TexturePaintLayers.cs`,
`SkinningLodService.cs`, `GpuAnimationBlendService.cs`, `GpuConstraintSolverService.cs`,
`GpuExportBakeService.cs`, `GpuFluidSimService.cs`, `GpuIkSolverService.cs`,
`GpuLodGeneratorService.cs`, `GpuMorphService.cs`, `GpuPhysicsService.cs`,
`GpuRayTracingService.cs`, `GpuSkinningService.cs`, `GpuTerrainService.cs`,
`GpuTextureCompressionService.cs`, `GpuVoxelizerService.cs`, `PathTracerService.cs`.

### 2b. Tested but not wired into the app (16 services)

`ExportService`, `DependencyWatcher`, `AutosaveScheduler`, `PartTrackEvaluator`,
`ClipBlendService`, `ClipCapabilityValidator`, `TimelineEventDispatcher`,
`ConstraintParityInventory`, `AdvancedIkService`, `MorphEditService`, `WeightPaintService`,
`RetopoService`, `TexturePaintService`, `UvEditService`, `ShrinkwrapService`,
`DataTransferService`.

"Verified" means tested and self-consistent, **not** reachable from the viewer. R11 decides
wire-or-delete for both lists; R9 does the viewport integration for whatever survives.

## 3. Third wave — renderer quality (R0–R12, scoped, unassigned)

Full brief: [30-renderer-quality.md](30-renderer-quality.md). Target tier: a modern
EEVEE-class realtime renderer (Blender 4.x viewport look), explicitly **one step below
Blender** — no Cycles-class path tracer, no compositor, no sequencer, no grease pencil.

Every R ticket: headless verification only, `SceneRenderer` stays the single shared
renderer, one writer per file, coordinator leases for `Program.cs`, `SceneRenderer.cs`,
`ViewportMesh.cs`, `MarionettePlayer.cs`, `DocumentLoader.cs`, `DocumentSession.cs`,
`tests/TestRegistry.cs` and project files.

| Ticket | State | Owner | Allowed files | Prerequisite | Est. days | Blocker / evidence to close |
|---|---|---|---|---|---|---|
| R11 wire-or-delete | implemented-unverified | claude subagent (2026-10-20) | `tests/ServiceSurfaceTests.cs`, `handoffs/R11-DECISIONS.md` (no service file edited or deleted) | none | — | Decisions: `handoffs/R11-DECISIONS.md`. 2a: `ParticleSystem.cs`/`GeometryNodes.Core.cs` now wired by R9 + EGL-tested; 15 DELETE candidates (13 `Gpu*Service.cs`, `SkinningLodService.cs`, `PathTracerService.cs`, 1 109 lines) plus `SsrPass.cs` (orphaned after R5 test rewrite); `BaseMotionLibrary.cs` keep-as-offline-tool-or-delete, `TexturePaintLayers.cs` ticket 13 or delete; all 18 await the user's call and are the only allowlist entries. 2b: all 16 tested, none reachable; Retopo/Shrinkwrap/WeightPaint/MorphEdit already called by unwired `LiveModifiers.cs` (R9 item 2). Test group `service-surface` (4 fixtures incl. synthetic-orphan self-test and stale-allowlist check) is **NOT registered**: `tests/TestRegistry.cs` was held by another lease; coordinator must add `ServiceSurfaceTests.Register( registry, settings );`. Evidence: scratch harness = real tests + that one line, `--filter service-surface` exit 0, 4/4 pass; mutation (one allowlist entry removed) fails as expected. Real `dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter service-surface` exit 1 (0 selected, unregistered); real full suite exit 1: 557 pass / 5 fail / 7 skip, 569 registered — the 5 failures are `packaging/*` from `/tmp` full (tmpfs 98%, `/tmp/opencode` 14 GB), not R11 |
| R0 architecture split | implemented-unverified (historical pixel baseline unavailable) | Codex continuation, 2026-10-20 | `RendererShaders.cs`, `RenderPass.cs`, `RenderTarget.cs`, `SceneRenderer.cs`, host integration | none | 2/2 architecture fixtures pass; production Draw order, resize/dispose/allocation failure exercised. Current bind captures byte-identical; pre-split comparison cannot be certified. See R0-R1-CONTINUATION.md | Historical pre-split bind/animated pixel comparison remains open |
| R1 HDR + colour management | implemented-unverified (focused acceptance passed; aggregate gate now green) | Codex continuation, 2026-10-20 | `SceneRenderer.cs`, `BloomPass.cs`, `RendererShaders.cs`, `WhiteBalance.cs`, host colour flags, `tests/HdrPipelineTests.cs` | R0 | 11/11 HDR fixtures pass: AgX references, linear HDR bloom/halo, single sRGB encode, white balance, shared renderer and CLI validation. Actual CLI captures differ. Full snapshot: **575 pass / 0 fail / 7 skips, 582 registered**; `python3 tests/gpu-smoke.py` exit 0, report `/tmp/opencode/geppetto-verify-aqryfrtg/verification.json` status PASS with byte-identical bind captures. See R0-R1-CONTINUATION.md | R0 historical pixel baseline remains open; certification is the coordinator's call |
| R2 IBL (HDRI, irradiance, prefiltered spec) | verified | current agent | `IblBaker.cs`, `WorldEnvironment.cs`, `SceneRenderer.cs`, `RendererShaders.cs`, `ViewportMesh.cs`, `tests/IblTests.cs`, `tests/IblGpuTests.cs` | R1 | `/tmp/opencode/geppetto-verify-6ow0uoa8/verification.json` | Required hardware EGL gradient diffuse, sharp/broad metal reflection and dielectric Fresnel pass through shared SceneRenderer; CPU convolution matches seeded Monte Carlo within 1%; HDR file publication/radiance, cancellation, replacement and texture lifecycle pass. Fixed palette-batch neutral AO default. Radiance RGBE supported; EXR/XYZE/nonstandard orientation unsupported. |
| R3 AA + shadow quality | verified | current agent (single-agent continuation) | `TaaPass.cs`, `TaaShaders.cs`, `MsaaPass.cs`, `KeyShadowMap.cs`, `MotionBlurPass.cs`, `SceneRenderer.cs`, `RendererShaders.cs`, `tests/AntiAliasingTests.cs`, `tests/ShadowQualityTests.cs`, host CLI hooks | R1 | `/tmp/opencode/geppetto-verify-0xgkq_ji/verification.json` | Required hardware EGL acceptance passes: analytic diagonal coverage (none 0.20231, MSAA4 0.07052, TAA 0.13944 MAE), static/temporal shadow convergence, sloped receiver acne and radius broadening, exact 128x96 capture, byte-identical repeat sequences. Fixed jittered TAA output and per-tap receiver-plane shadow depth; skin disocclusion, camera cuts/reset and resize/dispose covered. Morph/dynamic-live history remains deliberately rejected. |
| R4 two-sided normals + tangent frames | verified | Codex continuation (2026-10-20) | `RendererShaders.cs`, `MaterialDescriptor.cs`, `ViewportMesh.cs`, `SceneRenderer.cs`, `KeyShadowMap.cs`, `tests/TwoSidedShadingTests.cs` | R0 | `/tmp/opencode/geppetto-verify-5z63hgz8/verification.json`: required NVIDIA EGL PASS, 556 passed / 0 failed / 3 skipped, 559 registered | Seven focused fixtures pass in this checkout: interior/backface policy, front/back mirrored normal maps, inverse-transpose bone normals versus baked geometry, winding/material bindings and prop model/raster agreement. Fixed model transform omitted from gl_Position and independent props inheriting character skinning. Normal Release build clean. See R4-CONTINUATION.md. |
| R5 screen-space AO/SSR/refraction | implemented-unverified (AO+SSR acceptance passed; refraction probe open) | claude (2026-10-20) | `SsaoPass.cs`, `SsrPass.cs`, `ScreenSpaceShaders.cs`, `tests/ScreenSpaceTests.cs` | R1, R4 | 3 EGL probes in `tests/ScreenSpaceTests.cs` (`--filter ss/`, exit 0, 8/8 with baseline): flat floor AO term 1.000 and lit HDR unchanged; corner strip AO 0.824 vs Monte-Carlo expectation ~0.81, lit corner 2.449 vs 2.533; smooth metal floor red excess 0.761 (SSR on) vs -0.032 (off) at the projected mirror image, rough floor and off-image control unchanged; all HDR texels finite, no GL errors; AO/SSR off ⇒ frame independent of history. **Fixed** SSAO flat-ground self-occlusion (was 0.96 near / 0.91 grazing on a wall-less floor): occlusion now tests the reconstructed surface point at the sampled texel (`dot(v,N)`), not depth-vs-depth. Full CPU suite 560/0/7 (567 registered); V3/V4-gated egl-image, gpu-cloth, performance-lifecycle 8/8 — scratch copy excluding R11's in-progress `tests/ServiceSurfaceTests.cs` | Open: glass-panel refraction offset probe (R6 transmission, R6 agent active). First frame after a history reset reflects black (SSR replaces env with empty history) rather than falling back to env. `SsrPass.cs` is a 96-byte dead stub (SSR lives in the lit shader) — R11 delete candidate |
| R6 material breadth | implemented; focused acceptance verified, aggregate R5 gate open | Codex continuation (2026-10-20) | `MaterialDescriptor.cs`, `MaterialLobes.cs`, `MaterialFeatureShaders.cs`, `SurveyMaterialMap.cs`, R6 hooks in renderer, `tests/MaterialFeatureTests.cs` | R1, R4 | `/tmp/opencode/r6-focused-final.json`: 4/4 fixtures pass on hardware EGL | Each lobe has a zero-weight/parameter contrast probe; thickness attenuation, exposure, MSAA backdrop capture, finite HDR, hashed order independence and explicit alpha modes pass. Fixed transmission double exposure, clearcoat Fresnel NaNs and invalid/amplifying lobe inputs. Normal Release build clean. Aggregate `/tmp/opencode/geppetto-verify-6nlgx8_h/verification.json`: 563 pass / 1 R5 SSAO failure / 3 skip; R5 files actively leased to its owner. See R6-CONTINUATION.md. |
| R7 DoF + motion blur | verified | parallel session R7 (2026-10-20) | `MotionBlurPass.cs`, `BloomPass.cs`, `PostChainShaders.cs`, `PostChain` hooks in `SceneRenderer.cs` | R1, R3 | — | Verified 2026-10-20: `post-effects/` hardware EGL tests (tests/PostEffectsTests.cs) pass — physical CoC f8=2.350/f2=9.398 vs thin-lens reference, signed near-field CoC + spread, bokeh blades/rotation, focus sharp; motion blur horizontal width 32 vs sharp 5, stale history rejected on cut/resize/source reload. Fixed reversed softDepth order in MotionBlurPass. Full suite 562 pass / 0 fail / 7 skip (installed-artifact failures were /tmp full, cleared). |
| R8 volumetrics | verified (2026-10-20) | parallel session R8 (2026-10-20) | `VolumePass.cs`, `VolumeGrid.cs`, `tests/VolumeTests.cs` | R1 | — | Fog scatters the key light, density falls off, empty volume costs nothing Verified 2026-10-20: 3 `volume/` tests pass; added EGL test through `SceneRenderer` (not just `VolumePass`): empty grid pixel-identical to none and no dispatch, sphere fog +20.7 mean G, green emission +118 G, density texture changes the image, forced fallback height fog changes pixels with `UsingFallback`. Shadowed centre 0.023 vs lit 0.611. |
| R9 live scene features | partial (render half implemented-unverified; live modifier stack not started) | claude (2026-10-20) | `LiveGeometry.cs`, `LiveFixtures.cs` (new), hooks in `SceneRenderer.cs` (pre-pass/lit/TAA/dispose), `Program.cs`, `HeadlessRender.cs`, `DesktopOptions.cs` (`--live`), `HairStrandService.cs` (opt-in `PreserveLength`) | R0 + service tickets | +0 (render-architecture EGL test extended) | `SceneRenderer.Live`: particles (sorted soft sprites), hair/curves (camera ribbons), cloth (smooth normals), geometry-nodes sets (nested instances, curves) in one dynamic VBO through the same lit shader/material uniforms; opaque layers write pre-pass depth (SSAO/SSR see them); TAA treats dynamic layers as moving. `--live particles,hair,cloth,instances` demo fixtures; 0 B/frame. EGL test: live cube covers centre pixel and disappears when hidden, 50/50 particles drawn, 0 B steady frame. Missing: shadows from live geometry (`KeyShadowMap.cs` is R3's), R7 velocity for live layers, item 2 live modifier stack. Capture: `handoffs/r9-live.png`. |
| R10 performance + lifecycle | implemented-unverified | claude (2026-10-20) | `SceneRenderer.cs`, `ViewportMesh.cs`, `RenderStats.cs`, `UniformCache.cs`, `HeadlessRender.cs`, perf hooks in `BloomPass.cs`/`SsaoPass.cs`/`KeyShadowMap.cs` (one-condition), `tests/RenderArchitectureTests.cs` | R1–R3 | +0 (extended render-architecture EGL test) | `RenderStats`: per-pass CPU/GPU(timestamp ring)/alloc, `--stats` prints passes, workload, live GL objects. 0 B/frame steady state (cached batch UVs, uniform locations, deduped binds); morph re-deform/upload + shadow bounds rescan skipped when weights unchanged (suit 200 frames 1.35s→0.40s, image hashes identical). EGL test asserts 0 B steady frame and stable RenderTarget/mesh-texture counts over resize + 3 reload cycles. Note: `ViewportMesh.Upload` drops CPU arrays, so meshes are upload-once (reload = new mesh). |
| R12 renderer verification harness | implemented-unverified | Codex continuation, 2026-10-20 | `tests/RendererImageTests.cs` (new), `tests/TestRegistry.cs` (one registration line), `tests/README.md` | R0 | New `renderer-image` group renders one synthetic scene per renderer feature through the shared `SceneRenderer` and retains the image: antialiasing, bloom, screen-space (AO corner + SSR mirror reflection with a rough-floor and off-image control), shadow, depth-of-field, motion-blur, volumetrics, material-lobes (SSS + Beer-Lambert transmission), transparency (hashed order independence + blend order). 9 feature JSON records with per-probe measured/expected/tolerance plus SHA256 per capture; 20 PPMs in `/tmp/opencode/renderer-images` (outside the run work dir, which a passing run deletes). Every probe compares against an independently computed expectation (exact polygon/pixel coverage, thin-lens CoC, Beer-Lambert attenuation, a reference render with the feature off, or a one-sided physical bound) — never "the image changed". Harness self-test in the `harness` group exercises every comparison mode and the capture writer; mutation (probe machinery never fails) turns it red. Full CPU suite 577 pass / 0 fail / 7 skip (584 registered); `python3 tests/gpu-smoke.py` exit 0, report `/tmp/opencode/geppetto-verify-gmi9yxda/verification.json` status PASS, 581 pass / 0 fail / 3 skip, byte-identical bind captures. One earlier run failed only on `ai-live/hardware EGL cache-eligible ... p95` (a 500 ms wall-clock target) under the concurrent session's load; it passes 3/3 in isolation at p95 316-364 ms and is not a renderer regression | Certification is the coordinator's call; the captures are EGL-only and are not yet diffed against a reference image |

**Total ≈ — engineer-days** (+15% coordination) → ~— days for one engineer, or
~2.— months with three parallel lanes. The seven heavy tickets (R3, R6, R5, R2, R1, R8,
R7) are ~70% of the work.

**Recommended first wave:** R11 → R0 → R1, then R4. R1 is the last point at which the pixel
baseline can be re-taken cheaply, and it is the biggest single visual change.

## 4. Shared-file leases

**Outer kernel: the knee-pad failure is fixed, the cloth one is diagnosed.** The outer
project's suite was 7186 passed / 3 failing sections; it is now **7189 passed / 2**.
`Effigy.Tests/GarmentLayeringTests.cs` asked for the side named `"Left"`, but the recipe's
side names are `"Both"`, `"Left only"`, `"Right only"` - so the lookup returned -1, which
reads as "Both", the pad went on both legs, and "the knee pad is on the left leg only"
could never pass. The test now asks for `"Left only"`.

**What the two remaining failures are, measured.** (All numbers from the outer suite's own
notes; the solver and its test mirror are unchanged.)

1. *Cloth stretch* - "99% of edges within 1.5x their length" measures 1.71x-2.01x on the
   arms-out and arms-up poses. What was ruled out: the rest lengths are exact (the initial
   edges measure 1.0000 against them, so it is not a stale fit or a unit error); more
   solver iterations do not help (6 -> 20 moved it from 1.89x to 1.97x, so it is not
   under-convergence); the layer pass is not the cause (disabling it changes nothing).
   What drives it: the capsule collision. With the collision off the same pose measures
   1.44x, and the stretch is already present immediately after the link solve, so the
   collision's push against the pinned collar is what the fabric cannot absorb. A hard
   max-stretch clamp fixes the number (1.45x) but drives the cloth 2.15 in inside the
   capsule, past that check's 1 in limit - the stretch and the intersection trade against
   each other, and the current solver has no way to satisfy both.
   Making the friction relative to the capsule's own motion (so a moving arm slides under
   the cloth instead of dragging it) was tried and made it *worse* (2.05x-2.73x); it is
   reverted. A real fix needs the links and the contacts projected together, or a sliding
   contact model - a change to `Code/Cloth/GarmentCloth.cs` and its mirror in
   `Effigy.Tests/ClothingSim.cs`.
2. *Layers* - "no layer through the one under it" (0.39 in) and "at bind, under 0.5% of the
   jacket is more than 0.1 in under the shirt" (18 of 1750, deepest 0.48 in) are the same
   thing: an outer garment ends up under the one it goes over. The layer pass pushes the
   outer particle along the inner layer's triangle normal, so a garment whose inner layer
   is wound the other way is pushed inward. The fit, not the solver, decides the static
   case; the sim decides the other.

**Kernel mirror synced (the outstanding item).** `tools/sync-kernel.sh` in the outer project
mirrors the canonical kernel into the editor assembly, and `KernelSyncTests` failed because
`Effigy/VmdlReader.cs` had no mirror copy. The script was run: "synced 242 kernel files into
Editor/Effigy, synced 4 runtime kernel files into Code/Effigy". The outer kernel suite now
reports **7188 passed, 3 failed**; the three failures are the pre-existing cloth/character
ones ("Arms out/up: 99% of edges within 1.5x their length", "comes back to where it settled
after the poses", "the knee pad is on the left leg only") and are unrelated to the ModelDoc
reader, which is a pure text reader nothing in the cloth path calls.

**Desktop gate, after the pose fix.** `python3 tests/gpu-smoke.py` exit 0:
`/tmp/opencode/geppetto-verify-egz1lvv_/verification.json`, required NVIDIA hardware EGL
PASS, **579 passed / 0 failed / 3 skipped, 582 registered**.

**The pose fix in its final form.** The earlier "a foreign bone keeps its bind translation"
guard was the wrong answer, and it broke thirteen kernel-parity fixtures when it was applied
to clips already in the rig's unit. The real cause is s&box's *weight-culled* skeleton: the
model compiler drops bones that carry no skin weight (the root, the clavicles, the
unweighted arm_lower, the twist controllers), and a .riganim stores each kept bone relative
to its nearest kept ancestor, or to model space when none is left - so the elbow bend of a
culled arm_lower is baked into the hand's translation. Played against the mesh file's own
parents those locals crumple the arm. `RigAnimSpace.Detect` now recognises this from the
bind pose (keyed bones whose clip offset matches their bind offset from the nearest keyed
ancestor, under each of the four quarter-turn yaws the compiler's axis convention can
produce) and returns the clip-side parent of each bone plus the yaw between s&box model
space and this rig's. `MarionettePlayer` places such a bone from its clip parent and
restates the local against the rig parent, so the arm keeps its length and bends correctly.
A clip already in the rig's unit is untouched: the kernel's semantics stand, and the
parity fixtures pass unchanged.

**ModelDoc reading moved into the kernel** (user: the application must not know a game
format). New `kernel/Effigy/VmdlReader.cs` (engine-free, text only) reads the two nodes a
viewer needs from a ModelDoc: the render mesh it draws and the material group that binds
each mesh material to a `.vmat`. The kernel already read s&box's `.riganim` and wrote
ModelDocs, so the format knowledge belongs there; the application asks the kernel. The
desktop keeps `VmdlMaterialMap.cs`, which turns those bindings into per-slot `.vmat`
overrides for the existing `MaterialResolver` - the desktop side of that is ordinary
material work, not format knowledge.

**Historical pending mirror-sync note (resolved; checked 2026-10-20).** `kernel/Effigy/VmdlReader.cs` is new in
the desktop's vendored copy of the kernel and is NOT yet in `../geppetto/Effigy/`. The
desktop's copy already diverges from the outer project in `FbxImport.cs` (the UV
convention), so the vendored copy is where desktop work lands; the outer kernel needs the
same file under a kernel ticket with a kernel test, and the desktop's `tests/VmdlReaderTests.cs`
covers the reader here in the meantime.

**The name-spelling bug that made models grey.** A ModelDoc names a material
`hab_brass.vmat`; the mesh file carries `hab_brass`. The reader registers every spelling a
mesh might use (the name, the bare stem, the last path segment, each with and without
`.vmat`) - and the bare stem was only produced for names containing a slash, so a plain
`hab_brass.vmat` never matched `hab_brass` and the model fell back to a flat palette
colour. Fixed, with a test.

**Reference props.** A clip's `ReferenceProps` are now built and drawn: each prop's own
ModelDoc gives its mesh and its materials, the clip places it, and the placement goes
through the same measured clip-space factor the bones do. The prop's size is the ratio
between its ModelDoc import scale and the character's, because s&box imports each ModelDoc
with its own scale. Props are drawn through the same lit shader, the same material path and
the same uniforms as the character - no second lighting path.

**Grey models now say why.** The headless run prints the material diagnostics: a `.vmat` the
project does not have, an unsupported effect, a texture that would not decode. For the
deep_sentance project the real unsupported parts are the ps1_lit shader and its palette,
wear, grime and filtering effects.

Evidence: `dotnet run --project tests/Desktop.Tests.csproj -c Release -- --report
/tmp/opencode/vmdl-final-tests.json` exit 0, 530 passed, 0 failed, 7 skipped, 537
registered (4 new ModelDoc reader fixtures). `dotnet build Marionette.Desktop.csproj -c
Release` exit 0, 0 warnings, 0 errors. Headless captures: the wall hoist now shows its
brushed metal instead of flat grey (/tmp/opencode/fixed-wall_hoist.png), marker_arms shows
its materials (/tmp/opencode/mat-marker_arms.png), the Survey player still renders
(/tmp/opencode/player-suit-materials.png), and the clip's props appear
(/tmp/opencode/pull-arms-props2.png). No interactive viewer launched.

User-reported "no colour or materials at all" (current agent): new `VmdlModel.cs` and
`VmdlMaterialMap.cs`, plus `DocumentSession.cs` (material resolution). Released again after
the change.

**Why the model was grey.** A model's colours live in its `.vmdl`, not in its mesh file: the
mesh file carries material *names*, and the ModelDoc's MaterialGroupList says which `.vmat`
each name means. The viewer loaded the `.fbx` directly, so every slot fell back to a neutral
grey - no albedo, no normal, no roughness, for any model that was not one of Survey's two
hardcoded ones. `VmdlModel` is a targeted, read-only reader for the two nodes the viewer
needs (the render mesh and the material remaps); `VmdlMaterialMap` hands those bindings to
the ordinary `MaterialResolver`, which already parses `.vmat` files and decodes their maps.
The ModelDoc is found through the clip's own `SourceModel` first, and otherwise by looking
for the `.vmdl` that draws the same mesh file (bounded, read-only, load worker only).

Evidence: headless capture of `pull_arms.fbx` + `hoist_press.riganim` now shows the model's
textures and colours (/tmp/opencode/pull-arms-materials.png) where it was flat grey before
(/tmp/opencode/pull-arms-final.png). The Survey player still resolves through its own map
and renders with its materials (/tmp/opencode/player-suit-materials.png). Full CPU suite:
`dotnet run --project tests/Desktop.Tests.csproj -c Release -- --report
/tmp/opencode/vmdl-materials-tests.json` exit 0, 526 passed, 0 failed, 7 skipped, 533
registered. Release build clean.

Not done: the .vmdl's `import_scale`/`import_translation`/`import_rotation` are read but not
yet applied to the mesh, and the ModelDoc's animation list is not read. Reference props
(clip `ReferenceProps`) are still not rendered.

User-reported cabinet bug fixed (current agent): new `RigAnimSpace.cs`, `MarionettePlayer.cs`
(clip-space adapter), `MeshPicker.cs` (evaluated bounds), `ServeMode.cs` (pose framing, clip
diagnostics), `HeadlessRender.cs` (pose framing, clip note). `tests/TwoSidedShadingTests.cs`
(measurement exposure only) and `tests/MaterialChannelGpuTests.cs` (reverted, no change) were
touched and are released again.

**Root cause of the broken cabinet scene.** s&box authors .riganim bone translations in
centimetres; the desktop rig is in inches (the FBX import scales mesh and rig together).
Playing such a clip unchanged put every translation 2.54x too far out, so the arms left the
body and the skeleton fanned apart. `RigAnimSpace.Detect` measures the ratio between the
clip's bone-translation lengths and the rig's bind lengths (bones whose translation length is
constant across a track keep their bind length, so the ratio reads the unit difference
directly) and returns 1/2.54 for a centimetre clip, 1 for a clip already in the rig's unit.
Only keyed bones are scaled; rotations and scales are unit free. Unresolved ratios are left
untouched and reported. Evidence: `pull_arms.fbx` + `hoist_press.riganim` reports
"clip translations are centimetres and the rig is inches; scaled by 1/2.54"; headless capture
before the fix showed the arms scattered, after it the pose is coherent
(/tmp/opencode/pull-arms-fixed2.png, /tmp/opencode/pull-arms-final.png). The measured ratio
is 2.5400 for both project clips.

**Shadow bias regression (mine, fixed).** The tighter animated shadow fit shrank the
normalised depth bias, so receivers self-shadowed and the key light was lost. The receiver
bias is now world-space: `texel * (constant + slope * tan)` converted with the light's depth
range, so a tight fit keeps the same bias.

**Probe measurement change (documented).** `two-sided/hardware EGL: nonuniformly scaled bone
does not skew the normal` now draws at exposure 2 instead of 0.35. The view transform's toe
is flat: at 0.35 the whole lit plane spans ten pixel levels, so the skewed-normal control
measured 0.80 against the probe's floor of 1 and the probe could not see the response it
exists to check. At exposure 2 the plane reads 197/255 (far from clipping) and the same
assertion measures 1.25. Assertion thresholds unchanged; both numbers recorded here.
`material-channel/hardware EGL probes all maps...` needed no change: it had been failing
because of the shadow-bias regression above and passes again at its original exposure 1.

**Full CPU suite after the fixes:** `dotnet run --project tests/Desktop.Tests.csproj -c
Release -- --report /tmp/opencode/clip-space-cpu-tests2.json` exit 0, 526 passed, 0 failed,
7 skipped, 533 registered. `dotnet build Marionette.Desktop.csproj -c Release` exit 0, 0
warnings, 0 errors. No interactive viewer launched.

**Launcher.** `/home/poo/.local/bin/geppetto` now starts the drawer UI
(`cabinet/host.py`) for a normal launch and only falls back to the native binary for
explicit CLI flags; it still builds the Release output first when the binary is missing.

User-reported cabinet animation framing fix: current agent leases `ServeMode.cs` and `MeshPicker.cs`. Reframe must use evaluated pose bounds, not bind-pose centre/radius; loading a clip must refresh pose/history/framing. No source assets modified. User-authorized build retained; no test suite or visible viewer launch.

Latest authorized build: `dotnet build Marionette.Desktop.csproj -c Release` exit 0, 0 warnings / 0 errors, after the R2 spherical source-mip/GGX PDF filtering changes and R3/R7 velocity integration. Output: `/home/poo/Desktop/geppetto-desktop/bin/Release/net8.0/marionette-desktop.dll` (apphost alongside it); this is the exact directory used by `/home/poo/.local/bin/geppetto` and both desktop entries. No test suite or interactive viewer launched. R2/R3 remain implemented-unverified pending image/lifecycle acceptance, not blocked on compilation.

User now explicitly authorizes a build after finishing the current R2 source-filtering slice. Current agent remains solo; tests still not requested. Latest R2 source mips integrate spherical bin overlap (including odd dimensions); GGX prefilter chooses source LOD from sample PDF versus equirectangular texel solid angle. Build must target this checkout's normal Release output so the desktop icon launches it, not an isolated scratch artifact.

Single-agent R3 completion checkpoint (implementation, not verification): shared R7 velocity raster now RGBA16F (xy=current-minus-previous pixels, z=expected previous depth, w=valid). TAA uses it for skin deformation, aligns current/previous Halton offsets, checks velocity/scene-depth agreement and rejects stale/disoccluded history. One velocity draw/palette upload reused by TAA and motion blur. Morph/dynamic-live history still explicitly rejected. Fixed known Silk.NET GL_MAX_SAMPLES enum gap with named numeric constant comment; shadow bounds tolerate released upload arrays via retained bind data/conservative fallback; existing R9 live casters preserved. `--aa none|taa|msaa4`, `--hdri`, `--shadow-radius` are reachable in interactive/headless hosts. MSAA live object count included in headless stats. No build/test/visible viewer launched. All post-integration CPU/GPU acceptance remains pending.

R2/R3 host reachability lease: current agent owns additive `--aa`, `--hdri`, `--shadow-radius` plumbing in `DesktopOptions.cs`, `Program.cs`, `HeadlessRender.cs`. No UI changes; preserve other host features. Single agent, no tests/builds.

Single-agent remaining-work assignment (user): current agent continues R3 and acquires `MotionBlurPass.cs` for sharing R7's existing SSBO-backed deformation velocities with TAA. `SceneRenderer.cs`, `TaaPass.cs`, `TaaShaders.cs`, `MsaaPass.cs`, `KeyShadowMap.cs` remain current-agent owned. No new agents; no tests/builds. Existing live shadow integration is present already and will be preserved.

R2/R3 latest implementation follow-up: shadow radius, receiver normal-offset, slope/constant caster bias and TAA-rotated static shadow kernel now wired into shared renderer/shader, with out-of-map taps treated as lit. Fixed R6+MSAA interoperability: opaque-colour capture reads/restores the actual active lit framebuffer and caches its callback. AA changes reset temporal histories; leaving MSAA releases its targets. Player-driven morphs reject camera-only history too. IBL prepared payloads now validate before GL upload, stage replacement textures, release staged resources on exceptions, restore the active texture unit and reset temporal histories after publication. Cancellation reaches CPU bake loops; cubemap sampling reuses its delegate. Current interfaces/limitations recorded in `handoffs/R2-R3-IMPLEMENTATION.md`. No tests/builds per user. Camera-only fallback remains deliberate; no object/deformation motion buffer claimed. R2/R3 acceptance remains unverified.

R3 shared-file work resumed: latest active other-worker lease no longer lists SceneRenderer/RendererShaders. Current agent retains those two files for R3 integration; existing R6 opaque-copy/R8 volume changes preserved. No tests/builds per user.

R3 independent follow-up: `KeyShadowMap` now caches per-bone influenced AABBs and unions transformed corners (conservative for convex four-weight skinning); morph snapshots refresh bounds, nonuniform bone scale preserved. Fits depth and XY extent, quantises extent/snaps world centre to shadow texels, handles a vertical light without singular look-at, exposes caster slope/constant bias. `TaaPass` now resets jitter on resize/toggle, guards GL ownership, uses scale-independent frustum reprojection cut detection, finite history checks, YCoCg variance clipping, configurable history weight and gamma; no per-frame heap texture array. Removed unused, empty `IblShaders.cs` scaffold: actual IBL baking is CPU-side and shading lives in shared RendererShaders. No tests or builds executed for these changes per user instruction. Renderer shared-file collision remains unresolved; independent work does not certify R2/R3 complete.

R3 implementation checkpoint (no tests/builds per user): camera-only TAA with deterministic Halton jitter, reprojection, variance neighbourhood clipping and depth rejection; skinned/morph history rejected; explicit HDR MSAA4 path; default AA None; GPU targets disposed. Shadow enhancement patch NOT applied: SceneRenderer changed concurrently (`_opaqueCopy` added by another worker) despite R2/R3 lease. Other worker changes preserved. Shared-renderer edits paused pending coordination; R2/R3 not verified and R3 shadows/motion-vector coverage incomplete.

R2 renderer integration now implemented-unverified (user requested no further tests): worker-prepared SH9, RGB16F roughness cubemap, RG16F BRDF LUT bound in shared SceneRenderer; analytic lights retained; async HDR load with stale-request rejection and Draw-thread promotion; GL textures disposed. No post-integration compilation/GPU result claimed. R3 now leased to current agent: `TaaPass.cs`, `TaaShaders.cs`, `MsaaPass.cs`, `KeyShadowMap.cs`, plus continuing `SceneRenderer.cs`/`RendererShaders.cs` lease. Other leases unchanged.

User-authorized handover to R2 (current agent): `SceneRenderer.cs`, `RendererShaders.cs` now leased for IBL integration, superseding the R6 lease on these two files only. R2 also owns `IblBaker.cs`, `IblShaders.cs`, `WorldEnvironment.cs`, `tests/IblTests.cs`. R3 follows R2 per user request. Preserve R6 changes and do not edit its other files.

R2 preparation extended: current agent owns `WorldEnvironment.cs` alongside `IblBaker.cs` and `tests/IblTests.cs` for CPU HDRI decode/sampling and baking. Renderer/shared registry leases unchanged.

R2 CPU bake follow-up: Radiance RGBE flat/modern-RLE decode with bounded input validation, immutable linear equirect sampling (Z-up; wrapped longitude/clamped latitude), explicit GGX roughness cubemap mip data (+X,-X,+Y,-Y,+Z,-Z), and Smith-GGX/Schlick split-sum BRDF LUT implemented. `dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter ibl/ --report /tmp/opencode/r2-cpu-tests.json` exit 0: 12/12 pass. Full `dotnet run --project tests/Desktop.Tests.csproj -c Release -- --report /tmp/opencode/r2-full-cpu-tests.json` exit 0: 524 passed, 0 failed, 7 skipped, 531 registered (includes existing headless EGL fixtures). R2 itself still has no GPU integration/probes. HDR support deliberately rejects XYZE, nonstandard axis orientation, legacy repeat encoding; no EXR decoder. Mip/LUT arrays are CPU upload payloads, not yet bound by SceneRenderer. R0/R1 shared files untouched. Remaining: coordinator renderer/host integration after lease release, GPU lifecycle and the three R2 EGL acceptance fixtures.

R2 independent preparation (current agent): owns `IblBaker.cs`, `tests/IblTests.cs` only for CPU SH-9 irradiance baking and reference tests. No edits to the active R0/R1 lease or renderer integration; R2 remains in progress.

R2 CPU preparation evidence: `dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter ibl/ --report /tmp/opencode/r2-irradiance-tests.json` exited 0, 6 passed / 0 failed. SH9 cosine convolution, linear HDR constant-sky pi normalization, Z-up gradient versus independent seeded Monte Carlo (1% tolerance), procedural fallback, invalid-input atomicity. Initial constant-sky regression failed with float accumulation; double accumulation fixed it without loosening tolerance. Remaining: HDRI decoding, specular prefilter, split-sum LUT, renderer wiring and EGL probes. No GPU acceptance claimed; shared registry unchanged (IBL registration already existed).

Active: **R0+R1+R5+R6 (claude, 2026-10-20)** holds `SsaoPass.cs`, `SsrPass.cs`, `ScreenSpaceShaders.cs`, `MaterialDescriptor.cs`, `MaterialLobes.cs`, `MaterialFeatureShaders.cs`, `SurveyMaterialMap.cs`, `ViewportMesh.cs`, `RenderPass.cs`, `RenderTarget.cs`, `tests/HdrPipelineTests.cs`, `tests/RenderArchitectureTests.cs`, `tests/TestRegistry.cs`. Other agents: do not edit these until released.
R4 leases released (claude, 2026-10-20). Note: `tests/TestRegistry.cs` was also edited by the R3
agent during R4's lease (`AntiAliasingTests.Register`). No active implementation leases. Record the owner and ticket here before
editing any of: `Program.cs`, `DesktopOptions.cs`, `DocumentLoader.cs`, `ViewportMesh.cs`,
`SceneRenderer.cs`, `MarionettePlayer.cs`, `BloomPass.cs`, `KeyShadowMap.cs`,
`tests/Program.cs`, `tests/TestRegistry.cs`, project files, shared serializers/contracts, or
verification scripts.

Released: V1, P1, E1–E5, M1–M4, G1–G4, I1–I4, P2–P4, D1–D3, V2–V4, A1–A7, AI1 (backend),
L1.

## 5. Wave 2026-10-20 (coordinator: Claude session; each worker in its own copy under ~/Desktop/geppetto-agents/<id>)

Workers edit ONLY their owned files in their own copy. The coordinator merges owned files
back and alone edits `tests/TestRegistry.cs`, `Program.cs`, `SceneRenderer.cs`,
`ViewportMesh.cs`, `MarionettePlayer.cs`, `DocumentSession.cs`, `DocumentLoader.cs`,
exporters and csproj files in this folder. That rule still holds for the R wave.

## 6. L1 — local chat client (2026-10-20, current agent)

A native chat window for a local KoboldCpp server, requested by the player as a replacement for
the KoboldCpp web portal. It is a standalone project and does not touch the viewer.

Owned files, all new: `tools/LocalChat/LocalChat.csproj`, `Program.cs`, `ChatOptions.cs`,
`ChatController.cs`, `ChatUi.cs`, `Character.cs`, `KoboldClient.cs`, `LocalHttp.cs`,
`SelfTest.cs`, `README.md`.

**Shared-file edit, coordinator review required:** `Marionette.Desktop.csproj` now excludes `tools/**` from the
root project glob (`<Compile Remove>` plus the matching `None`/`EmbeddedResource` removes). The root project
compiles `**/*.cs`, so without this the new tool's sources and its generated `obj/` AssemblyInfo were compiled
into `marionette-desktop` and the desktop build failed. The change is additive and only narrows the glob;
`dotnet build Marionette.Desktop.csproj -c Release` and the 496-fixture CPU suite pass with it in place.

Evidence: `dotnet run --project tools/LocalChat -c Release -- --selftest` exits 0 with 15/15 checks (SSE parsing,
request body shape, character store round trip, character selection semantics, and live probes of
`/api/v1/model`, `/v1/models`, non-streaming and streaming completions against a running KoboldCpp).

Not part of L1: the desktop viewer, the AI CLI, and the kernel are unchanged. The model files live in
`~/koboldcpp/` and are not repository assets.

## 7. AI1 — provider-flexible AI CLI (backend implemented-unverified)

Added `AiCli.cs`, `AiProvider.cs`, `AiJudge.cs`, `AiResearch.cs`, `AiSuiteTools.cs`,
`AiToolWorker.cs`, `tests/AiTests.cs`, `AI.md`, `handoffs/09-ai.md`; integration in
`Program.cs` and `tests/TestRegistry.cs`, README.md link. No UI or kernel edits.

Evidence: `env AI_REQUIRE_EGL=1 python3 tests/gpu-smoke.py` exit 0;
`/tmp/opencode/geppetto-verify-_ftgpom0/verification.json`: required hardware EGL PASS,
359 passed / 0 failed / 0 skipped, plus Python harness selftests. Sixteen AI fixtures
include native editable feature execution, real five-view renders, independent mocked
judge responses, passing/plateau/budget stops, best checkpoint preservation, all three
provider protocols and safe path/credential/reference handling. The first isolated build
exposed per-user .NET apphost discovery; worker launch now uses the current runtime's
host for framework-dependent builds and the apphost for self-contained distributions.

Remaining: live company/local model and external research-service acceptance, media/model
study + video/contact sheets/PDF extraction, complete suite tool exposure, and UI panel/
checkbox. Exact supported/unsupported cases and reproducible commands are in AI.md.
R6 note (claude, 2026-10-20): `SceneRenderer.cs`/`RendererShaders.cs` belong to R2 per the handover; R6 added only small hooks there (opaque copy on unit 11, `MaterialFeatureShaders.Lobes` splice, `r6*` calls). R2: `InternalFormat.Rg16f` at SceneRenderer.cs:151 does not compile — Silk's name is `RG16f`.

Parallel sessions (2026-10-20): `SceneRenderer.cs`/`RendererShaders.cs` are shared by R2 + R5 (claude) + R7 + R8 sessions as **hook-only** edits: re-read immediately before each Edit, never rewrite the file wholesale, never revert others' lines. Texture units: 12 = R5 AO map, 13 = R8 volume, 14 = R7 velocity.

Continuation (Codex, 2026-10-20, solo): resumes this thread's R0/R1 work. Owns additive colour-management CLI plumbing in `DesktopOptions.cs`, `Program.cs`, `HeadlessRender.cs`, `ServeMode.cs`, documentation, and R0/R1 fixes/tests in `RenderPass.cs`, `RenderTarget.cs`, `tests/RenderArchitectureTests.cs`, `tests/HdrPipelineTests.cs`. Preserves other renderer-ticket implementations. Headless verification authorized by the resumed assignment.

R2 single-agent completion resumed (2026-10-20): current agent owns `tests/IblTests.cs`, new `tests/IblGpuTests.cs`, `SceneRenderer.cs`, `IblBaker.cs`, `WorldEnvironment.cs` and this board for required shared-renderer EGL acceptance and environment lifecycle fixes. No parallel edits. Interrupted playback follow-up rerun: `/tmp/opencode/r2-resume-cpu.json`, exit 0, 535 passed, 0 failed, 7 skipped (542 registered).

R2 acceptance found `ViewportMesh.DrawBatch` palette constructor leaves `Ps1Occlusion` at zero, suppressing all IBL. Current agent additionally owns the one-line neutral-default correction in `ViewportMesh.cs`; GPU card fixture directly covers this production constructor.

R0/R1 continuation handed back: implementation and focused checks complete; this continuation's file leases released. Evidence and explicit historical-baseline/aggregate-gate limits: `handoffs/R0-R1-CONTINUATION.md`. Preserve other active ticket leases.

R2 verification complete: `python3 tests/gpu-smoke.py` exit 0, `/tmp/opencode/geppetto-verify-6ow0uoa8/verification.json`, 542 passed / 0 failed / 3 skipped (545 registered), hardware NVIDIA EGL. New shared-renderer probes cover gradient diffuse, rough-metal peak/shoulder, dielectric Fresnel, file radiance parity, cancellation, failed replacement and repeated GL texture release. Neutral AO constructor fix included. Normal Release build exit 0, 0 warnings/errors. R2 lease released.

Latest user-reported pose/placement follow-up resumed solo: current agent owns `RigAnimSpace.cs`, `MarionettePlayer.cs`, `PropScene.cs`, new `tests/ClipSpaceTests.cs`, additive registration in `tests/TestRegistry.cs`, and this board. Audit found prop mesh scale also scales placement translation (wrong row-vector composition), and foreign-clip mismatch handling differs between FK and fallback sampling. Root and deliberately animated translations must remain valid. Outer `/home/poo/games/projects/geppetto/Effigy/VmdlReader.cs` already matches the desktop reader byte-for-byte and has registered outer tests; no second sync edit needed.

Mirror-sync audit: `/home/poo/games/projects/geppetto/Effigy/VmdlReader.cs` exists and `cmp` with `kernel/Effigy/VmdlReader.cs` exits 0. Outer `Effigy.Tests/VmdlReaderTests.cs` exists and `Program.cs` registers it. The earlier relative `../geppetto` path is stale from the old workspace layout. No outer kernel changes made in this continuation.

Final continuation evidence (2026-10-20): `dotnet build Marionette.Desktop.csproj -c Release` exit 0, 0 warnings/errors; the normal Release binary used by `/home/poo/.local/bin/geppetto` is updated. `python3 tests/gpu-smoke.py` exit 0: `/tmp/opencode/geppetto-verify-z42b2l9o/verification.json`, **547 passed / 0 failed / 3 skipped, 550 registered**, required NVIDIA hardware EGL. Five new clip-space regressions first reproduced the fallback/root/unknown-unit/prop-composition failures, then passed (`/tmp/opencode/clip-space-final.json`). Actual `pull_arms.fbx` + `hoist_press.riganim` headless capture at 0.6 seconds: `/tmp/opencode/arms-placement-final.png`; both props loaded, materials resolved, ten incompatible translated bones retain bind offsets, no source assets changed. Conservative mismatch protection is not full skeleton retargeting; arbitrary foreign IK goals are not certified by these fixtures. R2 verified; R3 full acceptance remains pending. All current continuation file leases released. No interactive viewer launched.

## Live AI modelling (user approved scope, 2026-10-20)

LM1 in progress, solo Codex: owns new `AiLiveRun.cs`, `tests/AiLiveTests.cs`, additive `tests/TestRegistry.cs` registration, and hooks in `AiCli.cs`, `AiSuiteTools.cs`, `AiToolWorker.cs`. LM2–LM5 follow sequentially; no parallel agents. User “go” authorizes the scoped minimal Watch/activity integration in the existing cabinet controls; no new panel/redesign.

R3 continuation (2026-10-20), single-agent ownership before edits: current agent owns `tests/AntiAliasingTests.cs`, new R3 fixture files if needed, `TaaPass.cs`, `TaaShaders.cs`, `MsaaPass.cs`, `KeyShadowMap.cs`, and additive fixes in `SceneRenderer.cs`/`MotionBlurPass.cs`. No parallel edits. Required acceptance: analytic diagonal coverage, temporal convergence, sloped-receiver acne/penumbra, deterministic repeat captures; use shared SceneRenderer with hardware EGL and no display connection.
R3 additionally leases `RendererShaders.cs` for receiver-plane depth correction in the existing shadow filter; hardware regression shows radius 32 creates 0.3408 linear self-shadow error on an unoccluded sloped receiver. Other live-AI ownership (including TestRegistry) remains untouched.

LM1 focused acceptance: isolated build and `--filter ai-live/` exit 0, 2/2 fixtures pass (`/tmp/opencode/live-ai-lm1.json`): five native child-worker mutations through a mocked provider, reusable geometry caches, initial/removed empty state, rollback, immutable snapshots, monotonic restore and torn journal. LM2 now active; owns new `AiLivePreview.cs`, additive `DesktopOptions.cs`/`Program.cs` watch flag plumbing, `ServeMode.cs`, and tests in the existing LM test file. Other leases unchanged.

LM2 implemented and process seam exercised: `/tmp/opencode/live-ai-lm2.json`, 3/3 live fixtures pass; real persistent NVIDIA EGL viewport followed five mocked-provider/native-worker edits, retained an orbited camera, reconnected without restart, and restored the final checkpoint without changing its rendered geometry. Observed maximum commit-to-frame 180.1 ms in the small fixture, not a general benchmark. LM3 now active: leases `cabinet/host.py`, `cabinet/cabinet.qml`, new `cabinet/live_runs.py`, and Python tests. User-approved existing-control bindings only.

R3 completion evidence (2026-10-20): `python3 tests/gpu-smoke.py` exit 0, required NVIDIA EGL, `/tmp/opencode/geppetto-verify-0xgkq_ji/verification.json`: **552 passed / 0 failed / 3 skipped, 555 registered**. Focused acceptance `/tmp/opencode/r3-acceptance-final.json`: 3/3 pass; raw coverage/convergence/shadow metrics are saved in its recorded work directory. Normal `dotnet build Marionette.Desktop.csproj -c Release` exit 0, 0 warnings/errors. Source changes this continuation: `TaaPass.cs`, `TaaShaders.cs`, receiver-plane correction in `RendererShaders.cs`, corrected XML summary in `SceneRenderer.cs`, expanded `tests/AntiAliasingTests.cs`, new `tests/ShadowQualityTests.cs`, board/implementation notes. Existing test manifest reaches all new fixtures through AntiAliasingTests.Register; no edits to the live-AI owner's TestRegistry. AA remains explicit (`--aa none|taa|msaa4`), default None; no UI work or interactive viewer launch. R3 file leases released; other-session leases remain unchanged.

LM3 Watch/Unwatch and activity/revision lag bindings implemented in existing run cards. `python3 -m unittest discover -s tests -p test_live_runs.py -v` exit 0 (2 protocol fixtures); Python syntax passes. Visual app not launched. LM4 now active, owns new `AiLiveControl.cs`, lifecycle hooks in `AiCli.cs`/`AiLiveRun.cs`, the existing host Stop method and focused tests. No source asset changes or new UI surfaces.

R4 continuation (2026-10-20), single agent: current agent owns `tests/TwoSidedShadingTests.cs`, `RendererShaders.cs`, additive model-uniform initialization in `KeyShadowMap.cs`, and related `SceneRenderer.cs` fixes if required. First audit found lit vertex `vWorld` uses uModel but gl_Position omits uModel, so object lighting and raster geometry disagree. Preserve other-session ownership; no TestRegistry/host/UI edits.

LM4 focused acceptance: `/tmp/opencode/live-ai-lm4.json`, 6/6 live fixtures pass. Run-identity cancellation acknowledgement is emitted before cancellation, foreign identities ignored, late commits rejected; provider wait cancels and preserves revision 1; an actual native Subdivide worker is cancelled in flight, exits, and publishes no revision. LM5 active: owns live tests and event/preview performance fixes needed for acceptance; headless regression/GPU gates and final normal Release build authorized by the implementation assignment.

R4 continuation completed (2026-10-20): focused `--filter two-sided/` exits 0, 7/7 pass (`/tmp/opencode/r4-focused.json`). `python3 tests/gpu-smoke.py` exits 0, required hardware EGL, `/tmp/opencode/geppetto-verify-5z63hgz8/verification.json`: **556 passed / 0 failed / 3 skipped; 559 registered**. `dotnet build Marionette.Desktop.csproj -c Release` exits 0, zero warnings/errors, normal desktop-launcher output updated. New regression reproduced both static prop placement and inherited character-skin failures before the respective fixes. Detailed supported cases/limitations: `handoffs/R4-CONTINUATION.md`. This continuation's R4 file leases are released; preserve other sessions' leases. No interactive viewer or UI work.

R11 lease (claude subagent, 2026-10-20): owns new `tests/ServiceSurfaceTests.cs` and planned decision file `handoffs/R11-DECISIONS.md`. Additive registration in `tests/TestRegistry.cs` NOT taken: the board still shows it held (LM live-AI owner, LM5 active; line "Active: R0+R1+R5+R6 ... tests/TestRegistry.cs" not released) and the file changed at 11:49 today. Registration left for the coordinator/holder. No service files edited or deleted.
R11 lease released (claude subagent, 2026-10-20): `tests/ServiceSurfaceTests.cs` and `handoffs/R11-DECISIONS.md` written; TestRegistry registration still pending for the coordinator.

R6 continuation (2026-10-20), single agent: current agent owns `MaterialLobes.cs`, `MaterialFeatureShaders.cs`, R6-only changes in `MaterialDescriptor.cs`/`RendererShaders.cs`/`ViewportMesh.cs`/`SceneRenderer.cs`, new `tests/MaterialFeatureTests.cs`, and its registration through existing `tests/MaterialDescriptorTests.cs` (preserving the other session's TestRegistry lease). Verify each lobe, Beer-Lambert thickness/exposure, explicit alpha/order independence, valid/boundary/invalid inputs. No UI or other host edits.

R5 continuation (claude, 2026-10-20 12:05): owns `tests/ScreenSpaceTests.cs` (already registered; no TestRegistry edit), `SsaoPass.cs`, `SsrPass.cs`, `ScreenSpaceShaders.cs`, and hook-only R5 fixes in `SceneRenderer.cs`/`RendererShaders.cs` if acceptance needs them (re-read before every edit). Goal: R5 EGL acceptance probes (AO corner vs flat plane, SSR mirror vs rough, no NaNs/GL errors).

LM1–LM5 first-release completion (2026-10-20): implementation and required headless acceptance verified; live file leases released. Report: `handoffs/LIVE-AI-RESULTS.md`. Full gate `env -u DISPLAY -u WAYLAND_DISPLAY AI_REQUIRE_EGL=1 python3 tests/gpu-smoke.py` exit 0, `/tmp/opencode/geppetto-verify-0baur57d/verification.json`: 566 pass / 0 fail / 0 skip, hardware NVIDIA EGL determinism checks pass. Nine live fixtures include real native worker cancellation, real persistent viewer updates, final restore image equality, corrupt/empty/rapid revisions, run switching/reconnect, camera retention and stable GL counts. 49,152 triangles at 1024x576: steady commit-to-frame p50 238.8 ms / p95 433.5 ms (five samples; excludes provider/tool time). Python protocol/actual QObject control tests 3/3 pass; QML compiles without a window. Normal Release build exit 0 and actual Host-to-Release-server QCore smoke passes; no visible viewer launched or user app restarted. New runs expose WATCH/UNWATCH/activity and graceful STOP. Read-only preview; steering, collaborative undo, replay, retention and live-provider artistic acceptance remain follow-ups.

R6 focused completion (2026-10-20): `env -u DISPLAY -u WAYLAND_DISPLAY dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter material-feature/ --report /tmp/opencode/r6-focused-final.json` exit 0, 4/4 CPU/real NVIDIA EGL fixtures pass. `dotnet build Marionette.Desktop.csproj -c Release` exit 0, zero warnings/errors, normal desktop-launcher output updated. Full `python3 tests/gpu-smoke.py` exit 1: `/tmp/opencode/geppetto-verify-6nlgx8_h/verification.json`, 563 passed / 1 failed / 3 skipped, 567 registered. Sole failure is the independently added/owned R5 `ss/hardware EGL: SSAO darkens the floor/wall corner and leaves an open flat floor unchanged` (corner 2.4618 vs 2.5377); no R6 fixture fails. Do not treat this as a passed aggregate gate. R6 fixes/tests and explicit alpha/support limits documented in `handoffs/R6-CONTINUATION.md`. This continuation's R6 file leases released; R5 ownership remains active.

R5 lease released (claude, 2026-10-20). Changed: `ScreenSpaceShaders.cs` (SSAO occlusion test), `tests/ScreenSpaceTests.cs` (stub replaced by 3 EGL probes). No TestRegistry/SceneRenderer/RendererShaders edits.

LM6 live steering (2026-10-20), solo Codex: user requested “more” after the first live release. Bounded assignment `handoffs/32-live-ai-steering.md`; owns `AiLiveControl.cs`, additive control metadata in `AiLiveRun.cs`, boundary/direction hooks in `AiCli.cs`, `cabinet/live_runs.py`, `cabinet/host.py`, existing run-card bindings in `cabinet/cabinet.qml`, `tests/AiLiveTests.cs`, `tests/test_live_runs.py` and LM6 docs. No test-registry/renderer/kernel ownership taken. First steering slice is unjudged runs; judged steering explicitly rejected, pause/resume/cancel allowed. Other renderer-ticket leases preserved.

R7 continuation (2026-10-20), single agent: current agent owns `MotionBlurPass.cs`, `BloomPass.cs`, `PostChainShaders.cs`, R7-only hook fixes in `SceneRenderer.cs`, new `tests/PostEffectsTests.cs`, and registration through owned/released `tests/AntiAliasingTests.cs`. Preserve active R5 shader/renderer hooks; no R5 fixes per user's redirection. Acceptance: physical CoC/focus/aperture, near/far handling, directional skinned blur, camera-cut/resize/reload history rejection and lifecycle, hardware EGL only.

Temp-leak fix lease (claude, 2026-10-20): `tests/TestRegistry.cs` (TestSettings/Run tail only, no registration edits) and `tests/verify.py`. Every test run left a ~270 MB `/tmp/opencode/geppetto-desktop-tests-*` dir and every verify a ~92 MB build copy; /tmp (16 GB tmpfs) hit 99%.
Temp-leak fix released (claude, 2026-10-20): passing test runs now delete their work dir (report defaults to `/tmp/opencode/geppetto-desktop-tests-last.json`; `--keep-work` or any failure keeps it); passing `verify.py` runs delete their `build/` copy, keeping verification.json and captures. `--filter baseline/` 24/24 and dir removed; `test_verification.py` 8/8. Old leaked dirs left for the user to remove.
R11 test registered (claude, 2026-10-20, user-approved): `ServiceSurfaceTests.Register` added to `tests/TestRegistry.cs`; `--filter service-surface` exit 0, 4/4. Deletion of the 16 candidates in `handoffs/R11-DECISIONS.md` awaits the user.

LM6 live steering (2026-10-20): implemented and verified headlessly; leases released. Report in `handoffs/LIVE-AI-RESULTS.md` (LM6 section). Full gate 575/1/0 — the single failure is the service-surface allowlist going stale after a concurrent deletion of 16 dead service files, not LM6.
R11 deletions (claude, 2026-10-20, user-approved "DELETE"): 16 DELETE-candidate files from `handoffs/R11-DECISIONS.md` moved to the KDE trash (recoverable), allowlist entries removed. Full CPU suite exit 0: 570 passed / 0 failed / 7 skipped, 577 registered; app Release build succeeds. `BaseMotionLibrary.cs` and `TexturePaintLayers.cs` remain on the allowlist awaiting the user's call.

Ground shadow catcher lease (claude, 2026-10-20, user: "do whatever you think is best to look better"): new `GroundShadowPass.cs`, opt-in `GroundShadow` hook lines in `SceneRenderer.cs` (pre-pass + lit pass + dispose only; R7 hooks untouched), `--no-ground-shadow` in `DesktopOptions.cs`, enable in `HeadlessRender.cs`/`Program.cs`, new `tests/GroundShadowTests.cs`. Off by default in SceneRenderer so existing fixtures are unaffected.

## The 2D drawing tool: the tools drawer (user requested, 2026-10-20)

Tools drawer (user request: "turn the right drawer into tools, have a pencil that rolls about and when clicked it pulls drawing up"). Solo, no parallel edits. Files: `cabinet/cabinet.qml`, `cabinet/host.py`, `cabinet/bake.py`, new `cabinet/drawing_tools.py`, new `tests/test_drawing_tools.py`, new `tests/test_drawing_host.py`, new `tests/test_cabinet_qml.py`, `cabinet/art/` (new `pencil.png`, `pencil_shadow.png`, `tool_tray.png`; `drawer_outliner.png` -> `drawer_tools.png`, byte-identical; regenerated `geom.js` with the `tools` key), `README.md`. **No C# touched.**

What it does. The right drawer is the **tools** drawer now: a steel tray with a pencil tumbling end over end along it, and a gallery of the drawings the 2D tool has made. Clicking the pencil raises the drawing app (Linework) — through its own single-instance socket, so a second click never starts a second copy — and clicking a thumbnail opens that drawing in it. The outliner cards that used to live in that drawer were dropped at the user's choice; `host.py open outliner` still works as an alias for `open tools`. `host.py draw [PATH]` does the same from the command line.

Art: `bake.py` grew `bake_pencil`, `bake_pencil_shadow` and `bake_tool_tray` (school pencil: red rubber, chrome ferrule, yellow hex barrel, sharpened cedar and graphite point, on a square canvas so it spins in place; dithered shadow; pressed-steel tray with a ribbed rubber mat). `python3 bake.py` is byte-deterministic: every other art file re-baked identically, and the renamed drawer is byte-identical to the old `drawer_outliner.png`.

Verification (no visible viewer launched; everything offscreen):
- `python3 -m unittest discover -s tests -p "test_*.py"` exit 0, **31 tests** (19 new across three files). New: gallery ordering/missing-file/hostile-index/ path-traversal cases, the socket handshake against a stand-in server, the raise-vs-start decision, a real spawn through `QProcess.startDetached`, and a headless QML load that asserts the tools drawer exists and one tile per drawing.
- The real app was reached over its own socket: Linework run with `QT_QPA_PLATFORM=offscreen`, the cabinet connected, raised it and listed the 7 drawings (`drawing tool reachable: True`, exit 0). The offscreen copy was terminated; no drawing file was written.
- The whole chest was rendered offscreen with the real viewer (`host.py` under `QT_QPA_PLATFORM=offscreen`, `open tools`, `snap`): `/tmp/opencode/chest.png` — TOOLS plate, pencil on its tray, gallery thumbnails, Survey player in the viewport.

Two defects found and fixed while doing it: `QProcess.startDetached` returns `(ok, pid)` in PySide6 (the first attempt unpacked it the other way round), and Qt 6.12 has no `GridView.columns`, so the gallery is laid out by a `Repeater` with computed positions instead. The gallery originally built no tiles at all — the QML test now counts them, so that cannot regress unnoticed.

Limits: "pulls drawing up" means the Linework **window** comes up (its canvas is C++ and cannot be hosted inside the PySide6 chest without rebuilding Linework as a loadable QML plugin). Drawing thumbnails come from Linework's own `index.json`; a drawing made while the drawer is open appears after the drawer is opened again. The pencil rolls only while the drawer is open.

### The drawing app side of it (Linework, 2026-10-20)

The drawer sends a drawing's **path** to the drawing app over its single-instance socket, and the app used to *import* any file it was handed — so opening a gallery drawing made a copy of it. Two changes in `/home/poo/games/projects/linework` (the user's app, built and installed with `make` + the same atomic `install -m755` its `install.sh` uses; the previous binary is kept at `/tmp/opencode/linework-bin-backup-2026-10-20`):

- `src/store.h`: `Store::docPath(id)`.
- `src/canvas.h` / `src/canvas.cpp`: `Canvas::galleryIdForPath(path)` — the gallery id of a drawing file, or "" if that file is not one of ours.
- `src/main.cpp`: the socket handler opens one of our own drawings with `openDrawing(id)` (where it is) and imports anything else, as before.
- `src/canvas.cpp` `Canvas::boot`: a drawing the app remembers in its settings but no longer has is now forgotten before anything is opened. Without this, opening the next drawing saved a **blank** document over the forgotten id and brought it back as a new gallery entry.

Verified offscreen against the real data folder: the installed app, handed `kw0d7scs.linework`, left the gallery at 7 entries and created no file (exit 0); the previous binary made a copy (`nmdyzle0`) — the same script run against the backup exits 1. The stale-id fix is verified in a throwaway data folder: with `"current": "ghostid"` in the settings, the app writes no file for it (exit 0).

One stray copy created by an earlier accidental launch of the app (id `9k3phvdq`, a copy of `patjm52t`) was removed from the gallery and its files moved aside to `/tmp/opencode/linework-stray/`; the settings' `current` was pointed back at `patjm52t`. The user's seven drawings are intact.


## Left drawer chat and model selectors (2026-10-20, solo Codex)

Continuation completed (solo, backend/docs only): retained the existing fixed chat area
and independent dropdowns without UI edits. Fixed cancellation before HTTP submission
and cancellation during buffered responses in `cabinet/chat.py`; both regressions failed
before the fix and pass afterwards. Updated `AI.md` with catalogue configuration,
research routing, restart instructions and cancellation limitations.

Verification (all exit 0, no visible viewer):
- `env -u DISPLAY -u WAYLAND_DISPLAY QT_QPA_PLATFORM=offscreen python3 -m unittest discover -s tests -p 'test_*.py'`: **49 passed**, including offscreen QML containment/dropdown checks, provider wire fixtures and host worker/research routing. Harness self-tests intentionally print simulated FAIL diagnostics; unittest result is OK.
- `dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter ai/ --report /tmp/opencode/chat-ai-tests.json`: **14 passed, 0 failed, 3 skipped** (opt-in EGL AI checks).
- `env -u DISPLAY -u WAYLAND_DISPLAY dotnet run --project tests/Desktop.Tests.csproj -c Release -- --report /tmp/opencode/chat-completion-desktop-tests.json`: **578 passed, 0 failed, 7 skipped; 585 registered**. Log: `/tmp/opencode/chat-completion-desktop-tests.log`.
- `python3 cabinet/chat.py`: discovered installed local models and configured Beam/DeepSeek/MiMo presets; no secrets printed. No new paid-provider requests or live research/build runs made during this continuation. Earlier provider results in the interrupted session are not newly certified here; account billing/quota remains external.

No renderer/kernel changes, new UI work, visible app launch or delegation. Existing
blocked network reads can linger until timeout after STOP, but cancelled replies are
ignored; this is not transport-level cancellation.

User explicitly supersedes old no-UI scope: take over stopped chat work, contain chat inside left research drawer, dropdown model selectors for chat and research, real local and external model connections. Owns cabinet/chat.py, cabinet/host.py, cabinet/cabinet.qml, tests/test_chat.py, tests/test_cabinet_qml.py, AiProvider.cs, AiCli.cs, tests/AiTests.cs, and documentation for this slice. No renderer/kernel edits or delegation. Previous chat files backed up in ~/local-llm/backups/geppetto-chat-20261020.

Chat notes + research vision (2026-10-20, current agent, user-requested): owns `cabinet/chat.py`,
`cabinet/host.py` (`_chat_notes`/`_chat_reply` only), `tests/test_chat.py`. Ollama discovery records the
`vision` capability and `research_arguments` passes `--vision` for it, so START builds see their renders.
Drawer chat now adds the newest `task-NN-brief.txt` (watched run first, else newest run; bounded to 6000
chars) to the system prompt as `<research_notes>`. Python suite offscreen: 59 passed. Not verified live:
koboldcpp held the GPU, so Ollama vision/chat requests failed with CUDA OOM during this session.
Follow-up: drawer chat also adds `EFFIGY-API.md` sections (default `~/games/projects/geppetto/EFFIGY-API.md`,
override `GEPPETTO_EFFIGY_API`) whose words match the latest question, bounded to 4000 chars, as `<effigy_api>`.
Live after koboldcpp was closed: Ollama `mag-mell` answered from injected notes and API excerpts; `geppetto-chat`
described a PNG through `/v1/chat/completions` at 100% GPU. Models swap; only one is loaded at a time.

MODELING folder + KEEP (2026-10-22, Claude, user-requested UI): `cabinet/host.py` (MODELING dir,
`filed_stickers`/`folder_entry`/`run_name`/`kept_model`, `benchKeep`, `openFiled`, `folder` property,
bench runs hidden in `scanRuns`, socket `keep`/`folder`/`openfiled`), `cabinet/ToolCard.qml` (KEEP key),
`cabinet/cabinet.qml` (folder hover/open animation, paper files, wheel scroll; the earlier viewport
sticker grid was removed at the user's request), `tests/test_cabinet_qml.py` (keep + folder + wheel tests).
Verified live in the cabinet (open, edit, KEEP, reopen, save in place) and offscreen: 25/26, the failure is
the pre-existing `test_the_drawing_kit_is_loose_in_the_tools_drawer`. Not committed.
Open: hover animation not seen live by the agent (no hover injection); models opened from `~/games/projects`
are kept as a copy, not saved in place; an overwritten kept model's sticker may be stale until restart;
KEEP is enabled with no edit (revision 1); leftover `research/bench-*` dirs are hidden but still on disk
(proposed: move to `research/.cleared`, awaiting user).


Sub-element selection (2026-10-22, session local_8502a83f, started from "Tool wheel viewport functionality"). s&box-faithful face/edge/vertex picking. Files: new `SubSelection.cs` (refs: FaceRef/EdgeRef via FacePlane.Capture, FaceSurface picks, 10px rim→edge in Face mode, shift toggles / ctrl removes / alt opposite face, kinds don't mix (notice), refs pruned by body id on rebuild, lost count, MeshSelector rules + faces=/edges= tokens, amber/blue highlight layers in SceneRenderer.Live); `ServeMode.cs` (`selmode`, `selclear`, `hover X Y`, `pick X Y [add|remove|pair]`, `@{"ev":"selection",...}`; follow keeps refs that carry over); `kernel/Effigy/Script/BuildScript.cs` (additive: `edges=` parser for List<EdgeRef>); `Bench.cs` (`facematerial slot=N faces=…` via FaceMaterialEdit.Assign/Detach, also reachable as `apply facematerial …`; Describe emits kind faces/edges); `cabinet/host.py` (subSelection/subMode/subPrompt, setSelMode/subPick/subHover/clearSubSelection/assignFacesToSlot); `cabinet/cabinet.qml` (viewport routing, keys 1-4, Esc, B/F/E/V chips + prompt); `cabinet/ToolCard.qml` (USE SELECTION); `cabinet/MaterialRing.qml` (right-click chip / "→ SEL" = faces → slot). Tests: `tests/SubSelectionTests.cs`, `tests/FaceMaterialAssignTests.cs`, `tests/test_subselection_host.py`, `tests/test_toolcard_selection.py`. Verified headless: CPU suite 602/0 (before /tmp filled), sub/bench/facematerial/ai-live groups green after the last edits, gpu-smoke PASS, real --bench + --serve e2e (pick face → pick edge → bevel → selection survives, 0 lost → face to slot 2). Not committed.
- 2026-10-22 (side agent): faces → rim edges + replayable face slots. `kernel/Effigy/Script/BuildScript.cs` (additive): `add Fillet|Chamfer faces=Body:x,y,z:nx,ny,nz;…` runs Feature.ApplyGeometrySelection (s&box ApplyBlendEdges → FacePlane.CaptureBoundary, also sets Bodies; edges= wins when both given); new verb `facematerial slot=N faces=…` (FaceMaterialEdit.Assign/Detach on the history, slot 0 detaches, all faces resolve first; in `Mutates`); faces= parsing factored into public `ParseFaces`, `AssignFaceSlots`. `Bench.cs` FaceMaterial now calls BuildScript.AssignFaceSlots (same code as replay); Describe("facematerial") still the feature. `cabinet/ToolCard.qml`: faces on Fillet/Chamfer send `faces=` instead of "Pick edges"; `tests/test_toolcard_selection.py` updated. New `tests/FaceBoundaryEdgeTests.cs` (group face-boundary, 4 tests). `dotnet run --project tests/Desktop.Tests.csproj -c Release --artifacts-path <scratch>/art -- --filter face-boundary/` 4/0; bench/ 3/0, facematerial/ 1/0, ai-live/ 12/0, controller-fixes/ 11/0; subselect/ 5/3 (loop/ring, box/lasso, grow/shrink: in-progress SubSelection.cs work, not touched); python3 tests/test_toolcard_selection.py 8 OK, test_subselection_host.py 9 OK. Open: `# undo:` lines in bench script.txt still not replayable (undone line stays in the script).
- 2026-10-22 (session c3969abe): s&box mesh-edit selection extras. `SubSelection.cs`: alt on an edge = loop (MeshEditSession.EdgeLoop), ctrl+alt (host sends remove+pair) = ring (SelectEdgeRing), shift adds; `SelectRegion` box/lasso with s&box occlusion (ray from eye, eps = diag·1e-3; faces at centroid+n·eps, edges need both ends in + midpoint seen, vertices by position; `xray` skips it); `Apply(Topology.Grow|Shrink|Linked)` via MeshEditSession, back to one ref per surface; `SetTool(paint|sculpt|meshedit|materialbrush|weight|none)` hides layers, keeps picks (`hidden` in the event); `SetRig` → picks/box take whole bodies (kind `bodies`, shift toggles); `PickDrop`/`FaceTokenOf`; hover no longer re-resolves the selection; edge refs resolve through cached per-mesh edge tables. `ServeMode.cs` (additive): `pick X Y [loop|ring]`, `selbox X0 Y0 X1 Y1 [add|remove] [xray]`, `sellasso [flags] X Y …`, `selgrow`/`selshrink`/`sellinked`, `seltool T`, `selrig 0|1` (bone first when bones shown), `drop X Y [material]` → `@{"ev":"drop","ok",face,faces}` (selection unchanged). host.py/cabinet.qml not touched (no rig/sculpt/drop UI in the desktop yet). Tests: 6 new in `tests/SubSelectionTests.cs`, subselect/ 8/0; real --bench + --serve e2e of every command green (frames: loop 16, ring 16, box 16 vs xray 31). Idle wire (user request): in Edge/Vertex mode every pickable edge (rim edges, surface seams skipped, cached per mesh, none past 30k edges) draws light grey (`SubSelection.WireTint`) under the picks; hidden by seltool and in rig mode. subselect/ 9/0. Not committed.
- 2026-10-22 (side agent of c3969abe): viewport gaps. (1) Double-click: s&box selects on each press and has no viewport double-click framing, so in face/edge/vertex mode a double-click is now ONE pick and no frame (was `frame`+2 picks); body mode keeps frame but also sends one pick (`cabinet/cabinet.qml` viewMouse `dbl`). (2) Escape backs out one layer per press, s&box EffigyViewport.OnKeyPress order: press/drag in progress (cancelled: no pick, no orbit) → MaterialRing → ToolCard → hand-opened wheel → selected bone/body (`select -1`) → sub-selection (`selclear`); unaccepted when nothing is left. (3) Floor grid over geometry: serve/headless was already depth-correct (the e2e lines on the box are the floor in front of its below-z=0 half); the bug was multisampled targets (the interactive window), where PostChain skips the depth blit and the composite left depth 0.5, hiding the grid completely. Fix: the Composite shader writes the scene depth (`uWriteDepth`, `gl_FragDepth`), `BloomPass.Composite` enables it with DepthFunc Always; same SceneRenderer path for window and headless. New `tests/OverlayDepthTests.cs` (registered in TestRegistry), new `tests/test_viewport_keys.py`. `dotnet build tests/Desktop.Tests.csproj -c Release --artifacts-path <scratch>/art` 0 errors; `env -u DISPLAY -u WAYLAND_DISPLAY dotnet <scratch>/art/bin/Desktop.Tests/release/Desktop.Tests.dll` 614/0/8 skipped (622) before the last test edit, `--filter overlay/` 1/0 (fails 4x 'grid not drawn at all, diff 0' without the fix); `python3 tests/gpu-smoke.py --output-root <scratch>/verify` PASS; `python3 tests/test_viewport_keys.py` 6 OK, test_subselection_host 9 OK, test_toolcard_selection 8 OK, test_cabinet_qml 25/26 (drawingsFolder missing: another session's uncommitted drawer edit, not this work). SubSelection.cs/ServeMode.cs untouched, no ServeMode patch needed. Not committed.
- 2026-10-22 (session 3824e846, user request): Claude Code in the chest. STOP/PAUSE reach Claude: new `cabinet/claude_gate.py` (a Claude Code PreToolUse/PostToolUse/UserPromptSubmit hook in `~/.claude/settings.json`, backup `settings.json.bak-before-geppetto-gate`; gate file per session in `~/.cache/geppetto/claude-gate`, stopped → refuse + halt, paused → wait at the next tool call, a new prompt lifts it, any error fails open); `~/.local/bin/geppetto-live` (backup `.bak-before-claude-gate`) runs the build as a child, records `claude-runs/<pid>.json` with `CLAUDE_CODE_SESSION_ID`, cancels the run cleanly on INT/TERM/HUP or when the calling shell dies; `cabinet/host.py` (additive: run-bar STOP/HOLD also gate the session that started the run; `claude*` properties/slots; socket `open|shut claude`). CLAUDE card: new `cabinet/claude_link.py` (one `claude -p` stream-json session, resumed across restarts, tool approve/deny over `--permission-prompt-tool stdio`, STOP = gate + interrupt + cancel its runs, PAUSE = gate + pause its runs), new `cabinet/ClaudePanel.qml`, `cabinet/cabinet.qml` (additive: `claudeOpen`, Ctrl+L, CLAUDE key on the run bar, card bottom-left of the viewport). The research CHAT panel is untouched. Tests: new `tests/test_claude_chest.py` 14/0 offscreen (gate, link with a fake claude, card keys); test_cabinet_qml 26/27 (drawingsFolder: another session's edit). Live: a real Claude session halted at its next tool after STOP; the link answered a real message. Not committed.
- 2026-10-22 (session 3824e846, user request "pictures pop up + part it's on"): DONE, not committed. Part focus: `ServeMode.cs` (additive `focus NAME` = teal see-through glow on the named bodies via new `FocusLayer.cs` live layer, follows each new revision; `framebody [NAME] [SECS]` = smooth camera glide to a part / the whole model), `MeshPicker.cs` (`Count`, `Triangles` accessor); new `cabinet/part_focus.py` (adds only move the glow; `set X` glides the camera in, max once per 2.5 s, `holdframe 1`, glides out on the next non-edit; replays skipped), hooked by one line in `host.py` (pane 1) and `panes.py` (panes 2-4) ai-preview. Pictures: new `cabinet/claude_feed.py`; `claude_gate.py` main() notes PreToolUse Read-of-image / SendUserFile images to `~/.cache/geppetto/claude-feed/<session>.jsonl` (wrapped, fails open); `host.py` polls it (card session + sessions in claude-runs only) → `claudePictures`/`claudePicture`; `cabinet.qml` pop overlay (right side, below the red error, 4 s then shrinks away); `ClaudePanel.qml` thumb strip (click = pop again). Tests: new `tests/test_part_focus.py` 7/0; `tests/test_claude_chest.py` 17/0 offscreen; test_cabinet_qml 26/27 (same drawingsFolder failure as before, not mine); headless `--serve` snaps of glow + glide checked. The live chest still runs the old binary/host until po restarts it.
- 2026-10-22 (session 3824e846, user request "panes empty / AUTO lost a pane / pick the one pane / camera too far"): not committed. `cabinet/host.py` (pane freeing only after the owner is gone 30 min (`FREE_AFTER`) with no live run; panes saved to `~/.cache/geppetto/chest-panes.json` and put back on restart; hidden panes parked and restored when the layout grows; AUTO holds any pane with an owner here/idle; `soloPane`/`setSolo` = show one chosen pane; `myScene`/`setMyScene` = pane 1 is an open pane runs land in unless MY SCENE is on), `cabinet/cabinet.qml` (VIEWPORTS rows "1: P1..P4", MY SCENE toggle, solo layout), `cabinet/pane_owner.py`, `cabinet/panes.py`, `cabinet/part_focus.py` (every add/set frames its part: `bodies=` list, else `name=`; max once per 2.5 s; materials keep the view), `FocusLayer.cs` (comma lists), `ServeMode.cs` (`framebody` multi-word names; Glide comes in close so the part fills a small pane and swings round to a part on the far side). Tests: `tests/test_pane_owner.py` 13 OK, `tests/test_part_focus.py` 8 OK, test_cabinet_qml 26/27 (pre-existing drawing-kit failure). Built with `--artifacts-path` only; the chest runs the old code until po restarts it.
- 2026-10-23 (Zoopa, user request "additions for the pitch/pipeline: UI/UX, onboarding, tutorial; get packaged soon"): the guide + pipeline tape + real packaging. New `cabinet/guide.py`: the pitch and the 5-stage pipeline (DESCRIBE → PLAN → BUILD → STEP IN → KEEP) written once as data, plus tutorial pages, starter examples, pure `stage_for()` state mapping and guide.json persistence (~/.config/geppetto/guide.json). `cabinet/host.py` (additive: `guide` property, guideShow/Next/Back/Done/Tour slots, `guideFirstRun()` called only from main() so tests never pop it, socket verb `guide`, and a bundle-layout fallback so a packaged cabinet drives the viewer beside it instead of bin/Release). `cabinet/cabinet.qml` (additive: `PipelineTape` along the viewport bottom — lights the live stage, says the current step in plain words, GUIDE key; `GuideCard` — 4-page first-run tutorial over the viewport, page 3 = guided tour replaying a finished build's kept steps for free + example chips). `cabinet/ClaudePanel.qml` (`prefill()`: examples land ready to send, never auto-sent). `packaging/package-linux.sh` now bundles the cabinet (runtime only: cabinet/model/ is 8.8 GB of art sources, excluded), a `./geppetto` launcher, `cabinet-requirements.txt` and `packaging/FIRST-RUN.md`; staged README points at FIRST-RUN. Tests: new `tests/test_guide.py` 14 OK; `tests/test_cabinet_qml.py` +4 guide/tape/tour tests, 37 run with only the 3 pre-existing failures (held-run liveNote, modeling-folder wheel, chat pop-out error — all reproduced identically on unpatched code). Package built and smoke-tested: `packaging/smoke-test.sh` PASS on the new archive (38 MB, cabinet 6.9 MB inside), bundle host resolves EXE to the bundled viewer. Backups `*.bak-before-guide`. Not committed. The live chest runs old code until po restarts it; delete ~/.config/geppetto/guide.json to see the first-run card again.
- 2026-10-23 Zoopa: rolodex rebuilt to po's spec -- wheel alone, no cradle/spindle/backdrop panel, no ROLODEX tape header, no per-card picking (drag/wheel spin it, click flips one card; settle 180+100ms per card, cap 540). bake_rolodex is spin-only now: frames baked fully opaque over a shaded drum fill (union silhouette, column-filled) so the wheel can fill its stretch of the drawer edge to edge with no wood in any gap; topspin ID frames regenerated with render_frames.py (they were missing -- only .objs remained). QML item is floor-width x 274 with PreserveAspectCrop. Old rolodex_base.png deleted. Backups *.bak-before-rolodex. Tests: 38 run, only the 3 pre-existing failures.
- 2026-10-23 Zoopa: rolodex WHEEL version rejected by po (drum fill read as a background, slats never read as files). Rebuilt as an ACCORDION FAN FILE per po's reference (expanding file that fans out with dividers): bake_rolodex now draws 19 cream dividers with blue tabs procedurally (fan hinged on a visible spine rivet, tips bleeding off the top, covers in the bottom corners, fully opaque, no wood), 36 frames / 12 flips, front divider leafs to the back per flip. Same QML as the wheel build. Tests: 38 run, only the 3 pre-existing failures.
- 2026-10-23 Zoopa: rolodex REMOVED from the tools drawer entirely, at po's word (fan scrapped too). QML block, wheel-scroll guard, liveRow anchor and floorExtra 48 -> 8 all reverted/removed; its QML test deleted. bake_rolodex + art + geom entry left in place for the drawer rework to dispose of. Backups *.bak-before-rolodex-removal. Tests: 37 run, only the 3 pre-existing failures. Verified in a drawer shot: clean wood under the drawing kit.

## 2026-10-26 — Geppetto gate and modeling pipeline, Muse implementation handoff (handoffs/36-muse-gate-parallel.md) — COORDINATOR LEASES

Coordinator: Muse (main chat). Chest status at assignment: CLOSED, verified 2026-10-26 ~03:27 EDT
(pgrep '[c]abinet/host.py', '[g]eppetto-live', '[m]arionette-desktop', '[m]use_seat.py' all
empty; `python3 cabinet/gate.py state` -> {"pending": []}). Do NOT start/restart the visible
chest for this assignment; do NOT run a paid provider as a test; no visible viewer in tests.
Tree was already dirty at assignment (many other workers' files); NO reset/stash/broad cleanup.
Integration order: coordinator freezes lane A's on-disk/CLI + host-socket contract first;
lanes B/C/E implement against that contract; D is independent; F audits read-only.
Merge order: A, then C, then B, then E, then D, resolved serially under the assigned owner.
Coordinator alone owns shared socket contracts and handoffs/BOARD.md lease entries.

Lane leases (one writer per file, including tests and ~/.local/bin scripts):
- Lane A — intent registry and approval protocol — agent 65538a53-18c3-4e3e-a79e-fe5f63b62ba7
  Owns: cabinet/gate.py, tests/test_gate_registry.py (new), cabinet/gate-protocol.md (new, frozen contract doc).
  Does NOT own: cabinet/host.py, ~/.local/bin/geppetto-live, cabinet/muse_seat.py, cabinet/muse-seat/*.sh.
- Lane B — live launcher and attach/receipt path — agent 204b429e-96fd-4d52-9a31-eb4a368964ae
  Owns: ~/.local/bin/geppetto-live (back up first as *.bak-gate-laneB-20261026; preserve
  more|skip|rewind|close|wait semantics), new isolated harness file(s) under tests/ (new files only).
  Does NOT own: cabinet/gate.py, cabinet/host.py, cabinet/muse_seat.py, existing test files.
- Lane C — four-pane reservation and placement — agent 3fed4065-0cc8-4fd6-9d8b-0b2b0e3028ac
  Owns: cabinet/host.py (gate-reservation/placement sections only), tests/test_gate_panes.py (new).
  Lease transferred by coordinator for this assignment: cabinet/panes.py (reservation-counting
  parts only; all other lanes read-only for panes.py).
  Does NOT own: cabinet/gate.py, ~/.local/bin/geppetto-live, cabinet/muse_seat.py.
- Lane D — provider-neutral modeling preparation — agent 3e14f43b-cf45-4ec9-b332-f32407053e3d
  Owns: cabinet/chat_phase.py, prompt injection in cabinet/chat.py and cabinet/claude_link.py
  (prompt text/injection points only), new prompt test file(s) under tests/.
  Does NOT own: Muse transport (cabinet/muse_seat.py, cabinet/muse-seat/*.sh), cabinet/gate.py,
  cabinet/host.py, ~/.local/bin/geppetto-live.
- Lane E — Muse SSH bridge compatibility — agent b0d55e8a-3ef4-4681-a4fc-f461459634ea
  Owns: cabinet/muse_seat.py, cabinet/muse-seat/*.sh bridge helpers, tests/test_muse_gate_bridge.py (new).
  Does NOT own: cabinet/gate.py, cabinet/host.py, ~/.local/bin/geppetto-live.
- Lane F — independent cancellation and failure audit — agent 2888530c-2b52-45dd-93a1-34dcf351fc0e
  READ-ONLY. No file lease. May request a lease from the coordinator before any fix; do not patch first.
  Inspects (read-only): cabinet/build_execution.py, cabinet/claude_gate.py, cabinet/claude_link.py,
  cabinet/chat.py, cabinet/host.py, cabinet/live_runs.py, wrapper and shutdown path.

Rules for all lanes: tests use temp directories/fixtures only — never real cabinet/confirm,
never real cabinet/muse-seat/requests|responses, never the user's display. A lane that needs
a file outside its lease must request it from the coordinator, never patch it independently.
Real-pane verification is NOT authorised until po explicitly authorises opening the chest;
fixture results must be labelled fixture-only, and click-to-first-frame must not be claimed
from token-write or Popen timing.
Status at lease time: lanes spawned, reading first; editing authorised only on coordinator
go-ahead sent after this entry. Lane F audit may proceed read-only immediately.
