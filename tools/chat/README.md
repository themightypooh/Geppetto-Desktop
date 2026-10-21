# Local chat runtime

KoboldCpp backend + SillyTavern frontend for the local adult roleplay setup.

## What is running where

| Piece | Where | Notes |
|---|---|---|
| KoboldCpp | `http://127.0.0.1:5001` | Serves the **KoboldAI native API** under `/api/v1/*` and the **OpenAI-compatible API** under `/v1/*` (POST only). SillyTavern uses the first, the native LocalChat client uses the second. |
| SillyTavern | `http://127.0.0.1:8000` | Local clone in `SillyTavern/`. User data lives in `SillyTavern/data/default-user/`. |
| LocalChat | native window | `tools/LocalChat`, an ImGui client for the same KoboldCpp server. |

## Desktop icons

| Icon | Opens | Script |
|---|---|---|
| `~/Desktop/SillyTavern.desktop` | The SillyTavern web UI in a browser tab | `tools/chat/launch-sillytavern.sh` |
| `~/Desktop/Local Chat.desktop` | The native LocalChat window | `tools/chat/launch-localchat.sh` |

Both launchers start only what is not already running: KoboldCpp first, then
the app. Nothing is ever killed or restarted. Both are also installed into
`~/.local/share/applications/` so they appear in the KDE application launcher.

```sh
tools/chat/launch-sillytavern.sh           # open SillyTavern in the browser
tools/chat/launch-localchat.sh             # open the native window
tools/chat/launch-localchat.sh --check     # verify the setup, open nothing
```

The native client is launched through the `dotnet` muxer rather than its
apphost: the runtime lives in `~/.dotnet`, and the apphost looks in
`/usr/share/dotnet`, so running `bin/Release/net8.0/local-chat` directly fails
with "You must install .NET to run this application". The launcher sets
`DOTNET_ROOT` and runs `local-chat.dll` through the muxer.

## Characters in the native client

The native app keeps its cast in `~/.local/share/local-chat/characters.json`
(`name`, `tagline`, `system`, `greeting`). To put a SillyTavern card in it:

```sh
node tools/cards/install-into-localchat.mjs --card sable-harrow
```

The card is flattened into one system prompt (description + personality +
post-history instructions) plus the first message as the greeting, and the
`{{user}}` / `{{char}}` placeholders are expanded, because the native app has no
macro engine. The store is merged, not replaced - the app's built-in cast
stays.

Each character in the native app keeps its own conversation. Switching
characters in the top bar (or with **Next >**, or **Ctrl+K**) files the
transcript you are leaving under that character and brings the other one back
word for word. The conversations live in the `threads` map in
`~/.local/share/local-chat/session.json`.

## The one setting that matters

SillyTavern's KoboldAI backend builds URLs as `${api_server}/v1/generate` and
`${api_server}/v1/model`. Against a stock KoboldCpp those live under `/api`, so
`api_server` has to carry the `/api` suffix:

```
http://127.0.0.1:5001/api
```

With the suffix, SillyTavern reaches:

- `POST http://127.0.0.1:5001/api/v1/generate` - generation
- `POST http://127.0.0.1:5001/api/extra/generate/stream` - streaming
- `GET  http://127.0.0.1:5001/api/v1/model` - model name / online status
- `GET  http://127.0.0.1:5001/api/v1/info/version` - Kobold United 1.2.5
- `GET  http://127.0.0.1:5001/api/extra/version` - KoboldCpp 1.123.1

Verified against the running server: `POST /api/backends/kobold/status` through
SillyTavern returns the model name, and both card generations work.

## Launching KoboldCpp

```sh
tools/chat/koboldcpp-roleplay.sh
```

Stop the old instance first (`pkill -f koboldcpp-linux-x64`). The script fails
fast if port 5001 is already taken.

Flags and why:

| Flag | Reason |
|---|---|
| `--contextsize 12288` | Headroom for a long card + lorebook + chat history. 8 GB VRAM is tight for a 12B IQ4_XS, so drop to `CONTEXT=8192` if it OOMs. |
| `--quantkv q8_0` | Quantises the KV cache, which is what actually lets a 12B fit in 8 GB at a larger context. Flash Attention is on by default in 1.123.x. |
| `--blasbatchsize 512` | Prompt processing is the bottleneck with a big character card; this cuts the wait before the first token. |
| `--gpulayers -1` | Autofit: put as much of the model on the GPU as fits. |
| `--usecuda` | NVIDIA backend. |
| `--host 127.0.0.1 --port 5001` | Loopback only. |

Overrides: `MODEL=... CONTEXT=... PORT=... tools/chat/koboldcpp-roleplay.sh`

## SillyTavern configuration (already written)

`SillyTavern/data/default-user/settings.json`:

| Key | Value |
|---|---|
| `main_api` | `kobold` (KoboldAI Classic - **not** `koboldai`) |
| `api_server` | `http://127.0.0.1:5001/api` |
| `preset_settings` | `Roleplay-12B` |
| `max_context` | `8192` |
| `amount_gen` | `320` |
| `power_user.custom_stopping_strings` | `["<START>", "<|", "Example dialogue:", "### Instruction:"]` |
| `power_user.trim_sentences` | `true` |

The custom stop strings matter: without them the model occasionally trails off
into template junk (`**<|** en:Name`) at the end of a reply. `trim_sentences`
cleans up any remaining ragged edge.

Sampler preset: `SillyTavern/data/default-user/KoboldAI Settings/Roleplay-12B.json`

| Setting | Value | Note |
|---|---|---|
| temperature | 0.85 | |
| top_p / min_p | 0.92 / 0.05 | min_p needs KoboldCpp >= 1.84; this build has it |
| top_k | 40 | |
| rep_pen / range / slope | 1.08 / 128 / 0.2 | keeps a 12B from looping the same phrase |
| sampler_order | `[6,5,2,0,4,1,3]` | ST's KoboldCpp order ids: 6 rep-pen, 5 temp, 2 top_p+min_p, 0 top_k, 4 typical, 1 top_a, 3 tfs |

## Starting SillyTavern

```sh
cd SillyTavern
node server.js
```

Then open <http://127.0.0.1:8000/>.

To start it without it opening a browser tab:

```sh
node server.js --browserLaunchEnabled false
```

## Checks

```sh
# backend reachable and model detected
curl -s http://127.0.0.1:5001/api/v1/model
curl -s http://127.0.0.1:5001/api/extra/version

# characters SillyTavern can see
curl -s -X POST http://127.0.0.1:8000/api/characters/all   # needs the CSRF token
```
