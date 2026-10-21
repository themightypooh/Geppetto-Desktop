# local-chat

A native chat window for a local [KoboldCpp](https://koboldcpp.net) server. It replaces the
KoboldCpp web portal with a real desktop window: a character picker, a streaming transcript and a
composer, drawn with ImGui on an OpenGL surface.

It is a standalone project. It does not reference `Marionette.Desktop`, and the desktop viewer does
not reference it.

## Run

```sh
# the app
dotnet run --project tools/LocalChat -c Release

# a server to talk to
~/koboldcpp/koboldcpp-linux-x64 --model <file>.gguf --usecuda --gpulayers 33 \
    --contextsize 4096 --quantkv q8_0 --host 127.0.0.1 --port 5001
```

Options:

| Option | Meaning |
|---|---|
| `-e, --endpoint <url>` | Server base URL (default `http://127.0.0.1:5001/`) |
| `-m, --model <id>` | Model id to use (default: whatever the server has loaded) |
| `-s, --system <text>` | System prompt; stored as a character called `Custom` |
| `--session <path>` | Session file (default `~/.local/share/local-chat/session.json`) |
| `--list-models` | Print the models the server reports and exit |
| `--selftest` | Run the headless protocol checks and exit |
| `--width`, `--height` | Window size |

Keys: **Enter** sends, **Shift+Enter** inserts a newline, **Ctrl+L** clears the transcript,
**Ctrl+K** moves to the next character.

The composer empties itself when a message is sent, and the caret stays in the box so you can
keep typing. ImGui holds the text in a buffer of its own and writes that buffer back over the
caller's variable every frame, so clearing the variable does nothing while the box is focused;
the widget is given a new id instead, which makes ImGui read the box afresh from the empty
string.

## Using it

* **Character** (top bar) picks a persona. Every character keeps its own conversation: switching
  away files the transcript under the character it belongs to, and switching back brings it back
  word for word. A character you have not talked to yet starts with its greeting as the opening
  line. **Next >** steps to the next character in the cast, and **Ctrl+K** does the same from the
  keyboard. Characters are edited in the **Settings** panel (name, tagline, system prompt,
  greeting) and are stored in `~/.local/share/local-chat/characters.json`. The app ships with a
  small cast; edit or add to it.
* **Model** (top bar) lists the models the server reports. The server decides what is actually loaded.
* **Settings** holds the character editor, the transcript tools and the sampling parameters
  (temperature, top P, min P, repeat penalty, top K, max tokens).
* The transcript and the session are saved to `~/.local/share/local-chat/session.json`. The
  per-character conversations live in the `threads` map in that same file, keyed by character name.
  A session file written before per-character transcripts had a single transcript; it is moved
  under the character that was selected the first time the new build opens it.

## The tracker

A character card can ask the model to end every reply with a line saying where the character is:

```
[ANDREA: landing | 4m | hand on coat | facing away]
```

The app reads that line out of the reply and puts the position on screen immediately, because it is
already in the text and costs nothing. It appears in a panel down the right-hand side of the window.

The four meters under it — affection, desire, trust and contempt — need more than a line, so the app
runs a **second, cheap generation** after each reply: a tracker pass that reads the conversation and
reports the character's state as JSON. The pass runs against the same server and the same model, and
takes about fifteen seconds on a 12B.

Two things make that pass work with a small model:

* The prompt says the JSON is the *entire* reply, and that the values belong **inside** the
  `characters` array. A model asked for "narrative then tracker" writes narrative, and a model that
  flattens the JSON produces something the app cannot read. The reader accepts a flattened reply
  anyway, because small models flatten it often.
* Every key gets exactly one value. A model that writes two values for one key produces invalid
  JSON, and the whole pass is lost.

The tracker is told about the character through a **tracker context**: a shorter version of the
system prompt, holding the description and the always-on world entries but not the example
messages. The examples are what the model needs when it is *writing*; the tracker is only *reading*
a scene, and sending all of it costs seconds of prefill for nothing. `tools/cards/install-into-localchat.mjs`
writes that field alongside the system prompt, and it is editable in the settings panel.

The bands under the numbers — *Strangers*, *Wary*, *Holds you in contempt* — are worked out from the
number, not asked of the model. It was labelling `affection=40` as "Strangers" and `desire=60` as
"Not feeling the heat".

Turn it off with **Track character state** in the settings panel.

## Why it does not use HttpClient

KoboldCpp answers in HTTP/1.0 and sends `connection: keep-alive`, but its streamed body carries no
`Content-Length`. .NET's `HttpClient` trusts the keep-alive header, treats the streamed body as empty
and then hands the next request a socket the server has already closed, which surfaces as
*"the response ended prematurely"*. `LocalHttp` talks to the socket directly, asks for
`Connection: close` and reads the body until the server hangs up, so the framing stays under our
control. `curl` and browsers tolerate the same server; .NET does not.

## Verification

```sh
dotnet run --project tools/LocalChat -c Release -- --selftest
```

28 checks: server-sent-event parsing (including malformed and null-content events), request body
shape, the character store round trip, character-selection semantics, per-character transcript
switching and clearing, migration of an old single-transcript session file, and live probes of
`/api/v1/model`, `/v1/models`, a non-streaming completion and a streaming completion against a
running server. The tracker checks cover reading the inline position line out of a reply, reading
the tracker JSON in both the shape the model is asked for and the flattened one it often returns,
ignoring a reply that has no tracker in it, and the bands following the number. Exits 0 when all
pass.

The window itself is not exercised by the checks; it needs a display.
