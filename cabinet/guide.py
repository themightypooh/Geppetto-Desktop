"""The guide: Geppetto's pitch, pipeline and first-run tutorial, as data plus small pure logic.

Geppetto's whole idea is an AI model maker you can *see* — describe something, watch it being
built step by step in plain words, step in at any step, and keep a model that stays yours and
stays editable. That idea used to live only in the maker's head: a new user opened the chest
and met drawers, keys and an empty pane, with nothing saying what the thing is or where a
build goes. This module is the one place that story is written down, so the first-run card,
the pipeline tape over the viewport and the packaging docs cannot drift apart:

  PIPELINE   the five stages every build travels, in plain words, with where each one shows
  PAGES      the first-run tutorial: four short pages a first-timer reads in under a minute
  EXAMPLES   first builds worth offering a newcomer (prefilled, never auto-sent)

`stage_for` maps what the host already knows (a live run? paused? a replay? kept models?)
onto a state per stage — done / active / ready / todo — so the tape can light the stage a
build is actually on. State (seen / finished / page) persists as JSON in
~/.config/geppetto/guide.json; every function here is pure or file-tolerant so the unit
tests need no Qt and no display.
"""

import json
import os

VERSION = 1

# The pitch in one line, used on the welcome page and in the packaged README.
PITCH = ("An AI model maker you can see every step of the way: say what you want, watch it "
         "being built step by step, step in whenever you like — the model stays yours.")

PIPELINE = [
    {"id": "describe", "name": "DESCRIBE", "short": "Say what you want",
     "what": "Tell the modeller what to make, in your own words. No tools, no jargon.",
     "where": "the CLAUDE card (Ctrl+L) or the research drawer"},
    {"id": "plan", "name": "PLAN", "short": "See the plan first",
     "what": "It looks things up and writes a plan — a model sheet and a list of steps — before anything is built.",
     "where": "the research drawer's run books"},
    {"id": "build", "name": "BUILD", "short": "Watch every step",
     "what": "The model grows one real, editable step at a time, each one said in plain words as it lands.",
     "where": "the viewport, the tool wheel and the build note"},
    {"id": "stepin", "name": "STEP IN", "short": "Step in any time",
     "what": "Pause it, step one change at a time, slow it down, or tell it to change direction mid-build.",
     "where": "the LIVE BUILD keys in the tools drawer"},
    {"id": "keep", "name": "KEEP", "short": "Keep it — it's yours",
     "what": "Keep the finished model, reopen it any time, replay any step, or export it. Nothing is a black box.",
     "where": "KEEP on the tool card, then the MODELING folder"},
]

EXAMPLES = [
    {"title": "a red-bellied piranha",
     "prompt": "Make me a red-bellied piranha, built step by step so I can watch."},
    {"title": "a wooden stool",
     "prompt": "Make me a wooden stool, built step by step so I can watch."},
    {"title": "a chess pawn",
     "prompt": "Make me a chess pawn, built step by step so I can watch."},
]

PAGES = [
    {"id": "welcome", "title": "MAKE THINGS BY DESCRIBING THEM",
     "body": PITCH,
     "lines": [
         "You don't need to learn 3D software. You bring the idea; Geppetto does the technical work in the open.",
         "Nothing happens behind a curtain: every step of a build shows on screen, in plain words, as it happens.",
     ]},
    {"id": "pipeline", "title": "EVERY BUILD TRAVELS THE SAME ROAD",
     "body": "Five stages, always in this order. The tape at the bottom of the viewport lights the stage your build is on.",
     "lines": ["%s — %s" % (s["name"].title(), s["short"].lower()) for s in PIPELINE]},
    {"id": "try", "title": "SEE ONE BEFORE YOU MAKE ONE",
     "body": ("The fastest way to get it: watch a finished build play back, step by step — "
              "that costs nothing and uses a real build this machine already made. "
              "Or start your own: pick an idea and it lands in the CLAUDE card, ready to send."),
     "lines": [
         "During a replay, drag the scrubber to any step and read what that step did.",
         "Every step in a replay is a real model you can stop on, note, or rewind to.",
     ]},
    {"id": "controls", "title": "THE FEW CONTROLS THAT MATTER",
     "body": "Everything else can wait. These five carry a whole build:",
     "lines": [
         "Drag the model to turn it; scroll to zoom; double-click to frame it.",
         "Ctrl+L opens the CLAUDE card: that's where you describe what to make.",
         "While a build runs, the tools drawer holds LIVE BUILD: pause, single-step, pace, stop.",
         "The build note (NOTE) lists every step so far — click a step to replay from it.",
         "KEEP files the model in the MODELING folder. The GUIDE key on the tape reopens this card.",
     ]},
]


def stage_for(live=False, paused=False, replaying=False, has_runs=False,
              has_model=False, has_kept=False):
    """Per-stage states for the pipeline tape, plus the index of the stage to light.

    Returns (states, active): states is one of done/active/ready/todo per PIPELINE entry.
    `ready` means "you can do this right now" (STEP IN while a build runs); `active` is
    where the work currently sits. Pure: the host feeds it what it already knows.
    """
    states = ["todo"] * len(PIPELINE)
    if replaying:
        states = ["done", "done", "active", "todo", "done" if has_kept else "todo"]
        return states, 2
    if live:
        states = ["done", "done", "active" if not paused else "done",
                  "active" if paused else "ready", "todo"]
        return states, 3 if paused else 2
    if has_kept:
        states = ["done", "done", "done", "todo", "active"]
        return states, 4
    if has_runs:
        states = ["done", "active", "todo", "todo", "todo"]
        return states, 1
    # nothing yet (with or without a model open): the next thing is always describing one
    return ["active", "todo", "todo", "todo", "todo"], 0


def pipeline_map(**kwargs):
    """PIPELINE with a `state` per stage, ready to hand to QML."""
    states, _active = stage_for(**kwargs)
    return [dict(stage, state=state) for stage, state in zip(PIPELINE, states)]


def default_state():
    return {"version": VERSION, "seen": False, "done": False, "page": 0}


def load_state(path):
    """The saved guide state; defaults (never seen) on any problem with the file."""
    try:
        with open(path) as f:
            state = json.load(f)
        if not isinstance(state, dict):
            return default_state()
    except (OSError, ValueError):
        return default_state()
    out = default_state()
    out["seen"] = bool(state.get("seen", False))
    out["done"] = bool(state.get("done", False))
    try:
        out["page"] = min(max(int(state.get("page", 0)), 0), len(PAGES) - 1)
    except (TypeError, ValueError):
        out["page"] = 0
    return out


def save_state(path, state):
    """Persist the guide state; a failed save must never break the app, so it returns bool."""
    try:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w") as f:
            json.dump(state, f)
        return True
    except OSError:
        return False


def guide_map(state, **stage_kwargs):
    """Everything QML needs in one map: pages, examples, the pipeline with live states."""
    states, active = stage_for(**stage_kwargs)
    page = min(max(int(state.get("page", 0)), 0), len(PAGES) - 1)
    return {
        "show": bool(state.get("show", False)),
        "seen": bool(state.get("seen", False)),
        "done": bool(state.get("done", False)),
        "page": page,
        "pages": PAGES,
        "pageCount": len(PAGES),
        "examples": EXAMPLES,
        "pitch": PITCH,
        "pipeline": [dict(s, state=st) for s, st in zip(PIPELINE, states)],
        "stage": active,
        "stageName": PIPELINE[active]["name"],
    }
