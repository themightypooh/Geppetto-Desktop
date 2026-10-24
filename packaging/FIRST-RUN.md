# Geppetto — first run

**An AI model maker you can see every step of the way.**

Say what you want. Geppetto's modeller plans it in the open, then builds it one real,
editable step at a time — each step on screen, in plain words, as it happens. Pause it,
step through it, tell it to change direction mid-build. When it's done, the model is
yours: kept, reopenable, replayable step by step, exportable. Nothing is a black box,
and the idea stays yours.

## The pipeline

Every build travels the same five stages. A tape at the bottom of the viewport lights
the stage your build is on, the whole time:

1. **DESCRIBE** — tell the modeller what to make, in your own words (the CLAUDE card, Ctrl+L).
2. **PLAN** — it researches and writes a plan and a model sheet before building anything (research drawer).
3. **BUILD** — the model grows step by step; the tool wheel shows which tool made each change, the build note says it in plain words.
4. **STEP IN** — pause, single-step, change the pace, or redirect it mid-build (LIVE BUILD keys, tools drawer).
5. **KEEP** — file the finished model in the MODELING folder; reopen, replay or export it whenever you like.

## Install & start

```sh
tar xzf geppetto-desktop-linux-x64.tar.gz
cd geppetto-dist        # (the folder the archive made)
pip install -r cabinet-requirements.txt   # once: Python 3 + PySide6 for the cabinet UI
./geppetto                                # the full experience
./run.sh --model sample/sample.effigy     # or: the standalone viewer on the bundled sample
```

`./geppetto` opens the cabinet — the AI model maker — driving the bundled viewer.
If PySide6 is missing it says so and opens the viewer alone.

## Your first five minutes

1. **The guide opens by itself.** Four short pages: the idea, the pipeline, a tour,
   and the few controls that matter. It never opens uninvited again —
   the **GUIDE** key on the pipeline tape (bottom of the viewport) brings it back.
2. **Take the guided tour.** On the guide's third page, *WATCH A BUILD* replays a
   finished build step by step from the revisions it kept. No model is called and
   nothing is spent. Scrub it, stop on a step, read what that step did.
3. **Make your first thing.** The same page offers starter ideas ("a red-bellied
   piranha", "a wooden stool", "a chess pawn"). One click puts the words in the
   CLAUDE card, ready — nothing is sent until you press **SEND**.
4. **Step in.** While it builds, open the tools drawer: **LIVE BUILD** holds pause,
   single-step, pace and stop, and **NOTE** lists every step so far. Click a step
   in the note to replay from exactly there.
5. **Keep it.** When you like where an edit landed, **KEEP** on the tool card files
   the model in the MODELING folder (tools drawer), where it stays yours.

## What you need a modeller for

Watching, replaying, editing and keeping models all work offline with what's in the
bundle. *New* AI builds are made by a modeller the cabinet talks to — by default a
local Claude Code CLI session (the CLAUDE card), with other local/hosted models
selectable in the card's model picker. If no modeller is configured yet, the guide
and the tour still work, and the CLAUDE card will tell you what's missing.

## If something's off

- **Black viewport, no guide:** the viewer needs OpenGL 4.6 / EGL 1.5+; try
  `./run.sh --headless --frames 1 --model sample/sample.effigy --screenshot out.png`
  to check the renderer on its own.
- **Cabinet won't start:** check `python3 -c "import PySide6"` — that's the one
  install the bundle can't do for you.
- **The guide again, on purpose:** `./geppetto` is already running? Run
  `python3 cabinet/host.py guide`, or press **GUIDE** on the tape.
