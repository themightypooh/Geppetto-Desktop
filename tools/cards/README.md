# SillyTavern character cards

Editable source, build script, and the install target for character cards used
by the local SillyTavern build in `SillyTavern/`.

## The cards

| Card | Slug | What it is |
|---|---|---|
| **Sable Harrow** | `sable-harrow/` | Adult roleplay card. Night manager of a bathhouse on the far bank of Bellwether Reach. Follows the user's lead on explicitness. |
| **Vesper Quill** | `vesper-quill/` | Non-explicit noir card. Lamplighter and record-keeper of the same city. |
| **Andrea** | `andrea/` | Modern-day card. Your cousin, twenty-one, on your landing at four in the morning to buy drugs she is short of paying for, and the user is not going to let her leave. |

All three are installed in `SillyTavern/data/default-user/characters/` and all
three are listed by the running instance.

## Layout

| Path | Role |
|---|---|
| `<slug>/card.json` | **The card text.** This is the source of truth; edit it. |
| `<slug>/portrait.png` | The card image. Replace it with your own picture any time. |
| `<slug>/portrait.svg` | Vector source of the generated placeholder image. |
| `<slug>/make_portrait.py` | Regenerates `portrait.svg` / `portrait.png` (optional). |
| `build-card.mjs` | Embeds `card.json` into a PNG and installs it into SillyTavern. |

`sable-harrow/portrait.png` is a designed title card, not artwork - drop your
own image over it and rebuild. `vesper-quill/portrait.png` is a generated
lamp-and-rain silhouette.

## Edit and rebuild

1. Edit `<slug>/card.json` in any editor.
2. Run the build:

   ```sh
   node tools/cards/build-card.mjs                       # vesper-quill
   node tools/cards/build-card.mjs --card sable-harrow   # sable-harrow
   node tools/cards/build-card.mjs --card andrea         # andrea
   ```

3. The card PNG is written to
   `SillyTavern/data/default-user/characters/<Name>.png` and appears in
   SillyTavern's character list on the next page load (or restart).

To use a different picture, put it next to the card and rebuild:

```sh
node tools/cards/build-card.mjs --card sable-harrow --image my-portrait.png
```

To also put a card into the native LocalChat client:

```sh
node tools/cards/install-into-localchat.mjs --card sable-harrow
```

That flattens the card into the native app's character store at
`~/.local/share/local-chat/characters.json`, expanding the `{{user}}` and
`{{char}}` placeholders the native app has no macro engine for.

The runtime side of the setup - KoboldCpp flags, the sampler preset and the
SillyTavern connection - is documented in [`../chat/README.md`](../chat/README.md).

## What the build guarantees

- The card is embedded with SillyTavern's own writer
  (`SillyTavern/src/character-card-parser.js`), so the PNG carries a `chara`
  chunk (spec V2) and a `ccv3` chunk (spec V3).
- The written file is read back with SillyTavern's own reader and checked
  against the source JSON field by field, then validated against
  `TavernCardValidator`. The build fails loudly instead of writing a card that
  SillyTavern would reject.
- Required V2 fields are checked before writing: `name`, `description`,
  `personality`, `scenario`, `first_mes`, `mes_example`, `creator_notes`,
  `system_prompt`, `post_history_instructions`, `alternate_greetings`, `tags`,
  `creator`, `character_version`, `extensions`.

## Card layout inside `card.json`

- `data.description` — who she is: appearance, role, the lamp's tell, voice.
- `data.personality` — the short trait summary.
- `data.scenario` — the situation the chat starts in.
- `data.first_mes` — the opening message.
- `data.alternate_greetings` — alternative openings, pickable in SillyTavern.
- `data.mes_example` — example dialogue that teaches the model's voice.
- `data.post_history_instructions` — style rules applied after the chat history.
- `data.character_book` — the lorebook. Entries are keyword-triggered so they
  only cost context when the scene touches them; entry `0` is constant and
  always present.

## Local model notes

The local backend is KoboldCpp running `MN-12B-Mag-Mell-R1-Uncensored` at an
8k context, so keep `description` and `first_mes` tight and let the lorebook do
the rest. Prefer `alternate_greetings` over lengthening the first message.

Sable Harrow's card is written to follow the user's lead: `post_history_instructions`
tells the model to match the register the user sets and not to fade to black,
break character or add a content warning, and `mes_example` includes one
explicit exchange so the model knows the register is allowed. Both behaviours
were checked against the running model - see the verification note in
`../chat/README.md`.

Every character in the card is explicitly an adult, and the card carries a
standing instruction that minors and underage-coded characters are never
generated.

`system_prompt` is intentionally empty in all three cards, so the sampler preset and
the story template decide the format instead of a hardcoded system prompt
fighting them.

## Andrea: the extension pipeline

Andrea's card is built to run against a set of community extensions rather
than relying on the 12B to follow instructions on its own. All of the
settings for these live in one place, written through SillyTavern's own
settings API so the running server stays authoritative:

```sh
ST_USE_API=1 node tools/chat/configure-andrea-extensions.mjs
```

| Extension | What it does for this card |
|---|---|
| **SillyTavern-State** | Runs a second pass after every turn and writes Andrea's position into the chat as one small rolling system message. This is the authoritative position tracker; the bracket line at the end of her reply is the cheap in-context echo of it. |
| **Recast** | A character-consistency pass over her reply. Only the Character Behaviour Validator is enabled, and it runs on the finished message rather than on streaming chunks. The Diff Viewer is left on, so a rewrite can be reviewed and rejected. |
| **SimTracker** | The HUD. The tracker data comes from a secondary LLM pointed at the same KoboldCpp, so Andrea's prose stays clean while the HUD still tracks affection, desire, trust, contempt, place and distance. |
| **MessageSummarize** | Auto-summarises on the current connection, so a 12k context survives a long night. |
| **SuperObjective** | Gives her a standing objective so she pursues something instead of only reacting. The prompt set is in the configure script; the task list itself is per-chat and is created in the UI. |
| **Starting Conversations** | The cold open. `data.extensions.starting_conversation` is an array of `{role, text}` turns that replace the first message, so the scene opens with her line, then yours, then hers again. It makes SillyTavern ignore alternate greetings, so Andrea's `alternate_greetings` is empty and the three older opening scenes are preserved in `creator_notes` instead. |
| **Regex** | One script trims everything after the tracker line, so the tracker is always the last thing in her reply. |

Andrea's `create_date` is pinned in `card.json`. SillyTavern otherwise takes
it from the PNG's file timestamp, which changes on every rebuild - and the
State extension keys its per-character config on `<name>-<create_date>`, so
an unpinned date would orphan that config on the next build.

The card's tracker format is `[ANDREA: <place> | <distance from the user> |
<what her hands are doing> | <which way she is facing>]`. The distance is the
character: she is on the landing before she is in the flat, in the kitchen
before she is beside you, across the room before she is touching.

### The tracker HUD

SimTracker draws the affection tracker. It needs three things that are easy
to get wrong, so they are all pinned down here:

| File | What it is |
|---|---|
| `andrea/tracker-prompt.json` | The single source of truth for the tracker's system prompt and its field list. |
| `build-andrea-tracker-template.mjs` | Builds `andrea-tracker.json` into the SimTracker extension, from that file. |
| `chat/configure-andrea-extensions.mjs` | Writes the same prompt and field list into SillyTavern's settings, from that file. |

```sh
node tools/cards/build-andrea-tracker-template.mjs
ST_USE_API=1 node tools/chat/configure-andrea-extensions.mjs
```

Three things the stock extension does not do, and why:

- **The prompt lives in the template, not the settings.** SimTracker prefers
  `templateData.sysPrompt` over the `datingSimPrompt` setting, so a prompt
  put in the settings is silently discarded.
- **The prompt has to say the block is the whole reply.** The stock dating-sim
  prompt asks for "Narrative -> Tracker -> codeblock", and as a secondary LLM
  that means it writes narrative into Andrea's message as well.
- **The model supplies numbers, the template supplies the words.** It was
  labelling `ap=40` as "Strangers" and `dp=60` as "Not feeling the heat", and
  the stock template never rendered those strings anyway. The band is now
  derived from the number in the template's html, so the model only has to
  produce four integers.

The four meters are named `affection`, `desire`, `trust` and `contempt`
because that is what SimTracker's change tracker watches. It computes deltas
for exactly `affection`, `desire`, `trust`, `contempt`, `affinity` and
`health` and for nothing else, so a field called `ap` would never have
produced a delta however it was rendered.

The card itself is written from scratch rather than patched out of the stock
bento template. The bento one is a dating-sim card: it renders an empty
dashed box where the pregnancy pill would be, it puts the change indicator on
top of the meter value, and it is four stacked rows of candy-coloured bars.
Andrea's is a 2x2 grid of four tiles on a dark card, with the band, the
position and her private thought all on it, and the band computed in the
template rather than asked of the model.

SimTracker's secondary LLM has to be set to the `custom` provider. The
`openai` provider ignores the configured endpoint, posts to
api.openai.com, and wants an OpenAI key out of SillyTavern's secrets. The
custom branch uses the endpoint verbatim, so it needs the full URL.

To see the card without starting a chat, render the template against sample
stats with the same Handlebars helpers the extension registers.



