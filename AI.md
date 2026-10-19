# Geppetto AI CLI

The desktop executable now has a terminal AI mode. It uses official HTTP API formats,
not consumer-chat subscriptions, scraped logins, or a claim to reproduce provider apps.
No extra NuGet packages or engine dependencies. No chat panel/checkbox UI has been added.

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
  --research --judge --out /tmp/opencode/geppetto-house-run
```

`--out` must be **new**. Runs never overwrite an existing project or input model.
Model selection currently happens at process start; start another session to change it.
There is no model-picker UI, OAuth login, automatic model listing, or streaming yet.

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

## Judge Mode

`--judge` enables an independent build → render → judge → fix loop. The AI selects and
locks a task-specific rubric, target, category floor, round count and plateau policy
before building. The player need not configure any of those.

Mark a capable model `--vision`, or choose a separate judge:

```sh
--judge --judge-provider google --judge-model YOUR_VISION_MODEL --judge-vision
```

Separate judges may also use `--judge-endpoint` and `--judge-key-env`. Each judgment
starts a fresh context with the brief, locked rubric, native model data, and five actual
rendered views. It gets no builder self-praise or previous scores. It cannot run tools.
The host validates every category, score and evidence field, computes the weighted
score itself, and checks the floor. This reduces score gaming; it does not make an AI's
subjective rating equivalent to a human quality guarantee.

Optional player overrides:

```sh
--target 9 --floor 7 --rounds 6 --plateau 3 --delta 0.2 --calls 60 --minutes 15
```

Plateau means no cumulative improvement of `delta` from the last significant best for
`plateau` consecutive judgments. Round count includes the initial judgment. Revision
starts from the best checkpoint, not a degraded latest version. Passing all category
floors takes precedence over a higher average that fails one.

Provide your own JSON with `--rubric file.json`:

```json
{
  "target": 9,
  "floor": 7,
  "maxRounds": 6,
  "plateauRounds": 3,
  "minImprovement": 0.2,
  "categories": [
    { "name": "Period fidelity", "weight": 2, "description": "Roof, openings and facade proportions follow the researched brief." },
    { "name": "Construction", "weight": 1, "description": "Walls, roof and openings meet plausibly in front, side and rear views." }
  ]
}
```

The rubric can use 1–12 unique categories with positive weights and checkable
descriptions. Command-line target/floor overrides win over the file. Custom rubrics
that omit loop policy use defaults of 6 rounds, 3-judgment patience and 0.2 improvement.
AI-generated policy has bounded ranges; player round overrides are capped at 12.

Calls and elapsed time are bounded; there are no automatic paid retries. Dollar-spend
and token-cost accounting are not yet implemented. Ctrl-C cancels. The API request
timeout is three minutes, and the overall session deadline can stop it sooner.

## Outputs and honest stops

- Editable `round-NN.effigy` checkpoints and multi-angle PNG evidence.
- Frozen rubric, selected limits, per-round judge evidence and selection records.
- `task-NN-best.effigy` and `task-NN-result.json` on a completed loop.
- `stopped.json` on cancellation/error/budget exhaustion, including the earlier best
  checkpoint if one had been judged. Last valid native state is checkpointed too.

Creation exit codes: `0` built without judging or met the target; `2` stopped below
target due to plateau/round limit; `1` error/cancellation/call/time budget. Interactive
chat reports each task's status and continues until quit or an error. A plateau is
never renamed a success. Existing checkpoints are never rewritten.

## Verification

```sh
env -u DISPLAY -u WAYLAND_DISPLAY AI_REQUIRE_EGL=1 \
  dotnet run --project tests/Desktop.Tests.csproj -c Release -- --filter ai/
```

Protocol fixtures cover all three native wire formats, tool-call history/signatures,
local-key handling, safe paths, reference address checks, rollback/frozen parameters,
and call limits. The GPU integration tests use real EGL renders and editable native
documents with **mock provider responses** to verify passing, plateau and failure
checkpoint behavior. They do not demonstrate artistic model quality or authenticate
against a live provider. Live-provider and local-model acceptance still need actual
credentials/running model servers.
