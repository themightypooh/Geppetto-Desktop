# C1 — Stop and cabinet-style correction pop-outs

## Owner-authorized load

User explicitly approved loading/restarting the cabinet after the chat audit.
The first launch lacked desktop display variables and aborted before opening;
the retry used the actual KDE environment from `systemctl --user show-environment`.
Updated cabinet is now running (PID 1547954 at verification). Its own socket
answered `where`, and `snap` saved `/tmp/opencode/c1-loaded-cabinet.png`; the app
image was inspected. No forced quit or existing runner termination was needed.
Startup log contained no QML errors.

Knight run was read-only verified before/after: same run ID, sequence 227,
**paused at revision 111**. No resume, review or model mutation was sent to it.
There is no live build in the main viewport at this check, so the contextual
Stop control is not visible there. This proves updated-cabinet loading, not a
live correction interaction with that old knight runner. New review-barrier
semantics apply to new runner processes; the compatibility protection remains.

## Chat audit follow-up — stacked messages (2026-10-24)

This supersedes the original window's one-shot Send behavior. Source fixes:

- Send message / Ctrl+Enter stacks locally, without sending to the modeller or
  resuming. Enter is a newline. Explicit Apply & continue submits all numbered
  messages in order as one correction. Drafts/queued messages cannot be silently
  bypassed by Continue. Close/reopen/layout changes do not apply the queue.
- History remains across repeated stops of the same run. Queued/pending/sent
  labels distinguish actual state; × removes only unsent messages. Retries after
  a transport error do not duplicate the approved note within the controller
  session. Native rejection restores the stack without resuming.
- Composer is pinned and independently scrollable, separate from scrollable
  history. Batch limits (16 messages / 1600 characters) are visible, not silent.
- A queued batch is locked to its selected saved step; apply/remove it before
  changing that context. No global/provider-specific chat is involved.
- Shared skill: `.agents/skills/geppetto-live-corrections/SKILL.md`, auto-discovered
  in this session. Shared guide: `docs/LIVE-CORRECTIONS.md`. The canonical personal
  Geppetto skill now links its `references/live-corrections.md` and requires this
  provider-neutral workflow. Tool access is still required; a skill alone does
  not make a chat-only model capable of editing.

Follow-up acceptance:

- `test_viewport_correction.py`: **15/15**, at default, 0.75 and 1.5 text/DPI
  scales. Actual UI tests stack three messages, press Enter/Ctrl+Enter, close
  and reopen without transmitting, then explicitly Apply. Unit regressions cover
  removal, retained history, queue budget, native rejection/retry, and a failed
  resume write followed by retry without a duplicate note.
- Full cabinet QML load: **1/1**, no binding/type errors.
- Real installed-runner + real notes-follower stacked round-trip: **exit 0**:

  ```sh
  python3 tests/c1_stacked_notes_roundtrip.py --binary /home/poo/Desktop/geppetto-desktop/bin/Release/net8.0/marionette-desktop.dll --out /tmp/opencode/c1-stacked-notes-evidence
  ```

  Evidence: `/tmp/opencode/c1-stacked-notes-evidence/roundtrip-avvns16n/evidence.json`.
  Two locally queued messages generated no note before Apply; the approved batch
  reached the actual follower exactly once, with both messages in order and
  selected step 2. An unrelated script's note stayed unread; restarting the
  follower did not replay the batch. The corrective inbox edit retained Base and
  changed its width to 7, abandoning Later. Only viewer seek acknowledgement was
  stubbed. This did **not** connect to or restart the real cabinet.
- Reviewed populated-chat screenshots at small/default/large sizes and all
  three text/DPI scales: `/tmp/opencode/c1-previews/stack-*.png`.
- `git diff --check`: exit 0. No C# changes were needed for this follow-up.

The already-running cabinet is still unchanged. Aggregate-suite/deployment
limits recorded below still apply. `make rag-update S=effigy` completed with exit
0; the shared skill/reference knowledge pool was refreshed. This indexing was
not user-visible model-build activity.

2026-10-24. Implementation is in source and the normal Release runner. **Not
claimed deployed into the already-running cabinet**; the shared chest was not
restarted. Existing runner processes keep their old code loaded.

## Delivered scope

- One local Stop control per viewport. Persistent four-component ownership
  keeps a hidden pane's independent window/draft alive across 1/2/4/solo layouts.
- Separate non-modal, resizable correction windows: enamel/steel frame, amber
  label, chrome pull and paper writing surface. No chat overlays on the model.
- Safe operation-boundary hold, not destructive cancellation. Drafting works
  while the current atomic operation finishes; sending waits for acknowledgement
  and the actual displayed saved revision. Closing never resumes.
- Preview/select a saved step; correction notes retain run/revision/operation.
  Open scripts yield to the inbox, put the correction before the preserved tail,
  and never repeat completed operations. Earlier-step rewind retains historical
  snapshots and deliberately abandons the old future tail.
- Native directions must be accepted by the builder before automatic resume.
  Rejected/locked directions keep the run held. No global agent gate is changed.
- Old runner processes fall back to a safe pause with a visible compatibility
  explanation, not a false promise of correction-first ordering. Non-open scripts
  publish that they cannot yield for corrections; Continue remains available.

## Focused evidence

1. `python3 -m unittest discover -s tests -p test_viewport_correction.py`
   **exit 0, 10/10**. Repeat with both `QT_SCALE_FACTOR` and `C1_TEXT_ZOOM` set
   to **0.75** and **1.5**: **exit 0, 10/10 each**. Includes actual button input,
   native window close/reopen, local send routing, draft retention when view 4
   is hidden, resize independence, native direction acceptance/rejection and
   older/closed runner protection. No real runs are discovered by these tests.
2. `env PYTHONPATH=tests PYTHONWARNINGS=ignore::ResourceWarning python3 -m unittest
   test_cabinet_qml.CabinetQmlTests.test_the_cabinet_loads`: **exit 0, 1/1**.
3. Registered production tests: `ViewportCorrectionTests.Register` adds six
   `viewport-correction` fixtures to `tests/TestRegistry.cs`.
   Aggregate compilation is blocked below, so the **same test sources** were
   invoked by the isolated runner:

   ```sh
   dotnet run --project /tmp/opencode/c1-native-runner/Correction.csproj -c Release --artifacts-path /home/poo/.cache/geppetto/c1-validation-artifacts/focused-final
   ```

   **Exit 0, 6/6**: initial batch, more batch, idle/open, checkpoint, earlier-step,
   and native AI control holds/delivers directions exactly once. Native provider
   network/whole-agent cancellation is not claimed by the control fixture.
4. Independent script and **real notes follower** acceptance:
   `handoffs/C1-SCRIPT-ORDERING-EVIDENCE.md`. Greeting verified actual ScriptLive,
   actual controller send, actual `geppetto-notes --follow`, selected revision,
   decoy-script isolation, exactly-once delivery and actual corrected geometry.
5. Screenshot matrix: `/tmp/opencode/c1-previews/`, including
   `popout-default-text1-scale1.png`, `popout-small-text1.5-scale1.5.png`, and
   `layout4-small-scale1.png`. Reviewed small/default/large pop-outs and 1/2/4
   viewport sizes at min/default/max tested text/DPI scales.
6. `git diff --check`: exit 0. Normal Release build was successful; no viewer was
   launched. The final build result is recorded on the board after this report.

The temp filesystem filled during multi-project validation. Only this session's
scratch `c1-build`, `c1-acceptance` and `c1-focused-build` output trees were moved
intact to `~/.cache/geppetto/c1-validation-artifacts/`; no unknown data was deleted.

## Open gates / limits

- Installed **running-chest** acceptance needs explicit owner coordination for
  a future launch. Do not restart it or touch the paused knight run to deploy.
  Already-running old script processes cannot acquire the new review barrier
  merely by rebuilding their executable.
- Full `Desktop.Tests.csproj` compilation currently fails in the unmodified
  `tests/RenderArchitectureTests.cs:91`: ambiguous `GeometrySet` between desktop
  and kernel namespaces. C1 does not change that unrelated test or the kernel.
- An earlier full cabinet Python run had 4 failures and 1 error among 35 tests
  (drawer/pop-out/old input expectations); only the focused QML load is certified.
  Existing `test_live_runs.py` with `PYTHONPATH=cabinet` has 7 pass / 1 fail:
  watch expects source line 2 but sees line 3. `live_runs.py` is unchanged by C1.
- This is a model-build correction surface, not animation-playback transport or
  a global agent chat. External modeller replies depend on the existing notes
  hook/monitor; no synthetic AI reply is displayed. Native judged/locked builders
  may reject directions and remain safely held.
