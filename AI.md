# Geppetto AI CLI

The desktop executable now has a terminal AI mode. It uses official HTTP API formats,
not consumer-chat subscriptions, scraped logins, or a claim to reproduce provider apps.
No extra NuGet packages or engine dependencies. The cabinet also provides drawer chat
and model selectors, separate from the terminal mode described below.

## Connect a model

Use the installed `marionette-desktop`, or in this directory:

```sh
dotnet run -c Release -- --ai --help
```

Supply credentials through your shell/secret manager, never a project file. Available
protocols and default environment variables:

| `--provider` | Native endpoint | Key variable |
|---|---|---|
| `openai` | OpenAI Chat Completions | `OPENAI_API_KEY` |
| `anthropic` | Anthropic Messages | `ANTHROPIC_API_KEY` |
| `google` | Google generateContent | `GEMINI_API_KEY` |

Choose the exact model ID available in your account. Other companies with compatible
APIs use `--provider openai --endpoint https://their-host/v1/ --key-env THEIR_KEY`.
Custom hosted endpoints require an explicit key variable; company keys are not
implicitly forwarded to another company's hostname. Remote endpoints require HTTPS.
Local loopback HTTP endpoints use no key unless explicitly configured.

```sh
# Local Ollama: substitute an installed model that supports tool calls.
dotnet run -c Release -- --ai chat \
  --provider openai --endpoint http://localhost:11434/v1/ \
  --model YOUR_LOCAL_MODEL --out /tmp/opencode/geppetto-local-run

# A hosted model: substitute a real model ID. Mark vision only if supported.
dotnet run -c Release -- --ai create \
  --provider anthropic --model YOUR_MODEL_ID --vision \
  --prompt "Make one original 1940s British semi-detached house exterior" \
  --out /tmp/opencode/geppetto-house-run
```

`--out` must be **new**. Runs never overwrite an existing project or input model.
In terminal mode, model selection happens at process start; start another session to change it.
The terminal mode has no OAuth login or token streaming.

### Cabinet chat and research selectors

The left research drawer contains a fixed-size, scrollable chat transcript with its
model dropdown underneath, plus a separate dropdown for new research/build runs.
Selections are independent and saved for the next launch. Restart the cabinet to
load these changes; use **↻ Models** to refresh the catalogue.

Installed Ollama models and a running local KoboldCpp endpoint are discovered in the
background. Beam, DeepSeek and MiMo presets appear when their existing credentials
are available. Additional providers can be listed in
`~/.config/geppetto/chat-models.json`, for example:

```json
[
  {"id":"my-provider/model", "label":"My model", "kind":"openai",
   "endpoint":"https://provider.example/v1/", "model":"model-id",
   "key_env":"MY_PROVIDER_API_KEY", "research":true}
]
```

Supported kinds are `openai`, `ollama`, `anthropic` and `google`. Mark models without
tool calling as `"research":false`; KoboldCpp discovery is chat-only. Missing-key
entries remain visible but cannot be selected. Credentials are passed to research
children through the environment, not command-line arguments.

Drawer chat streams Ollama/OpenAI-compatible replies; Anthropic/Google replies are
buffered. **STOP** immediately ignores further output; an already-blocked network
read may continue until the request timeout. Chat has no scene/file editing tools.
**START** launches the existing research/build CLI with the research selector's
provider, endpoint, model and supported request options. Provider quota/billing
errors still require attention in that provider's account.

## Research → notes → build

`--research` allows public reference queries and HTTPS text-page fetches. With
`BRAVE_SEARCH_API_KEY`, search covers the general web. Without it, search covers only
Crossref scholarly metadata; the console states this limitation explicitly. The model
can fetch other known public page URLs. API/provider/search costs remain the player's.

The reference researcher has no modelling tools. Its transient conversation contains
untrusted pages/snippets; the builder receives only the compact design brief, player
request and citations. Source URLs are saved in `task-NN-sources.json`. Raw downloaded
page bodies are not persisted. No player files are deleted and no reference geometry
is imported. If no references were retrieved, the brief is explicitly marked unverified.

**Not implemented:** downloading/rendering study-only CC0 models, video/YouTube frame
extraction, contact-sheet generation, PDF paper extraction, or automatic license
verification. Discovering a URL is not evidence that its linked media was inspected.
Source licensing and original-design requirements still apply after taking notes.

## Native suite tools

The agent can discover parameter schemas/defaults/choices; add, edit and remove native
features; inspect its document; and render its work. It uses actual kernel features,
not generated substitute geometry or arbitrary shell commands. The initial safe catalog
includes Primitive, Profile, Spline, Blob, Stroke, Detail, Remesh, Subdivide, transforms,
patterns, deformers, UV projection and several solid operations. Unsupported operations
fail explicitly; the whole suite is not yet exposed (notably animation, rigging,
file-backed sculpt edits, texture baking and import/export workflows).

Native rebuild/render jobs run in a separate, windowless child process. Cancellation
or the session deadline kills an in-flight child and preserves the previous model.
Rendering uses the same `SceneRenderer` and EGL implementation as the desktop app;
there is no visible-window fallback. Geometry and parameter budgets are checked, but
this is not an OS-level memory sandbox. Run-owned worker snapshots live in `.ai-jobs/`.

Chat supports `/inspect`, `/freeze FEATURE_ID`, and `/quit`. Frozen feature parameters
cannot be edited or removed by the agent; this is not a guarantee that upstream edits
cannot change the final appearance. Recent chat context is bounded to four exchanges.

## Builder-led output

Builds follow the player's request without an automatic judge, rubric, score veto,
forced render, correction loop or score-selected rollback. The model chooses its
approach and when to finish within the explicit call/time budget; there is no hidden
24-tool-round cutoff. Inspection and rendering remain available as tools.

Legacy `--judge`, `--judge-*`, `--rubric`, `--target`, `--floor`, `--rounds`,
`--plateau` and `--delta` options are accepted for launcher compatibility but do not
activate judging or contact another provider. The standalone `--ai judge` command
remains a separate, manually invoked inspection tool, not part of a build.

Cabinet prompts no longer require phase announcements, artistic recipes or a
research stage. Research permission is optional scope, not a prerequisite to modeling.
Explicit terminal `--research` / `research` requests still run the research workflow.

Calls and elapsed time are bounded; there are no automatic paid retries. Dollar-spend
and token-cost accounting are not yet implemented. Ctrl-C cancels. The API request
timeout is three minutes, and the overall session deadline can stop it sooner.

## Outputs and honest stops

- Editable `round-NN.effigy` checkpoints; PNGs only when the model requests a render.
- `task-NN-best.effigy` (legacy name for the final builder result) and `task-NN-result.json`.
- Result status is `built-unjudged`; no quality target is claimed.
- `stopped.json` on cancellation/error/budget exhaustion, including the earlier best
  completed checkpoint if available. Last valid native state is checkpointed too.

Creation exit codes: `0` saved the builder's output; `1` error/cancellation/call/time
budget. Interactive chat reports each task's status and continues until quit or an
error. Existing checkpoints are never rewritten. Successful saving is not an
artistic quality verdict.

## Verification

```sh
env -u DISPLAY -u WAYLAND_DISPLAY \
  dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter ai/
```

Protocol fixtures cover all three native wire formats, tool-call history/signatures,
local-key handling, safe paths, reference address checks, rollback/frozen parameters,
and call limits. Mock provider fixtures verify that legacy judge options do not
trigger extra calls, renders or revisions, and that builders can exceed 24 tool
rounds within their explicit budget. These tests do not demonstrate artistic model
quality or authenticate against a live provider. Live-provider and local-model
acceptance still need actual credentials/running model servers.

## Watch the AI build

New runs publish a live, editable model after every successful native operation. In the
cabinet's existing run card, press **WATCH**. Orbit/pan/zoom continues to work while
new revisions load. The activity line shows the current operation, and **view N / latest M**
shows preview lag. **UNWATCH** leaves the current model visible and lets the AI continue.
**STOP** requests graceful cancellation of the provider request/native worker; the last
committed model remains available. Open an existing model normally to leave the preview.
These controls are available after restarting the cabinet on the updated app build.

This is live geometry publication after completed operations. It does not stream model
reasoning/tokens or animate unfinished sculpt strokes. Expensive operations keep the
previous geometry visible until the new document is ready. Only one run is followed at
a time. Camera/settings persist across updates; the first nonempty model frames once.
Preview geometry is read-only; collaborative editing, mid-build instructions, undo
branching and replay scrubbing remain follow-up work.

The existing headless server can also attach directly, including before a run exists:

```sh
dotnet bin/Release/net8.0/marionette-desktop.dll --serve --watch-run /absolute/run/directory --size 1024x576
```

It uses the normal shared-memory/ack protocol, not a second renderer. `follow PATH`
switches runs; `follow` detaches. This command is intended for a host that consumes and
acknowledges frames, rather than a terminal image viewer.

Run-owned `.live/revisions/*.effigy` files are immutable editable publications;
`.live/events.jsonl` is the authoritative versioned event journal and `.live/latest.json`
is an atomic convenience index. Empty models are valid publications. Successful edits
and checkpoint restores advance revision numbers; failed/cancelled operations do not.
On reconnect the preview adopts the latest complete committed revision, ignoring torn or
malformed journal records. Geometry caches are optional and validated by the kernel;
cache misses rebuild in the background. Cache payloads use the existing per-user cache.
Revision files and job artifacts are retained for recovery; automatic retention/cleanup
is not implemented.

A local host requests cancellation by atomically writing `.live/control.json`:
`{"schemaVersion":1,"runId":"ID_FROM_LIVE_STATE","command":"cancel"}`.
The run acknowledges with `state: cancelling` / `outcome: accepted`, then emits a
terminal cancelled event once in-flight work has stopped. A different run ID is ignored.
### Pause, pace and directions (LM6)

Run cards also show **PAUSE/RESUME**, **PACE** (cycles Normal → 1 → 3 → 5 → 10 s) and a
direction box (Enter sends). Hosts queue commands as atomic files in `.live/commands/`,
one per command, named so lexical order is FIFO:
`{"schemaVersion":1,"runId":"ID","id":"UNIQUE","command":"pause|resume|pace|instruct|cancel", "seconds":0..30, "text":"..."}`.
Each ID is processed once; every command is acknowledged in the journal (`control.commandId`,
`outcome` = `accepted` or a rejection reason). Cancel takes precedence over other commands
from the same poll. Foreign run IDs are ignored; finished runs reject commands.

- **Pause** takes effect at the next safe boundary: an in-flight provider request or native
  operation finishes, then no new one starts until resume. Stop works while paused.
- **Pace** is a minimum gap between modelling mutations, measured from the previous one
  finishing. It slows scheduling only; it cannot make the provider or rebuilds faster.
  Changing it mid-wait applies immediately.
- **Directions** queue (max 16, 2000 chars each). Unstarted tool calls from a response that
  arrived after a direction get explicit `not_executed` results, then the builder receives
  the directions with the current model. A direction that races the final answer is
  applied before the run completes. Direction text is not written to the
  public event journal.

If the process has crashed there can be no live acknowledgement; published documents
remain readable. Watching an older pre-feature run is unavailable unless it has a live
journal; use its existing OPEN action for completed checkpoints.

Headless verification:

```sh
env -u DISPLAY -u WAYLAND_DISPLAY dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter ai-live/
python3 -m unittest discover -s tests -p test_live_runs.py -v
```

These fixtures use a mocked provider, real native modelling child processes, and the
production EGL viewport. They verify the integration, not a live model's artistic quality.
