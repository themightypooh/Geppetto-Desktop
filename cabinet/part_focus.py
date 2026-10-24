"""The part a live step works on glows in the pane, and the camera comes in close on it.

`add X name=N bodies=A,B` works on A and B (a garment detail on the boots), so those are what glows and
gets framed; a plain `add X name=N` makes N; `set N ...` edits N. The camera moves at most once every
MOVE_EVERY seconds and holds there for the next revision. Steps without a part (materials, refimages)
leave the camera where it is.
"""
import json
import os
import re
import shlex
import time

MOVE_EVERY = 2.5
VIEW_PREFS = os.path.expanduser("~/.config/geppetto/chest-view.json")


def zoom_to_part_enabled():
    """Auto-zoom onto the edited part is OFF by default (po, 2026-10-23: "it needs rethinking").
    Turn it back on with "zoom_to_part": true in ~/.config/geppetto/chest-view.json (read at launch)."""
    try:
        return bool(json.load(open(VIEW_PREFS)).get("zoom_to_part", False))
    except Exception:
        return False
_LINE = re.compile(r"^line \d+:\s*")


def part_of(op):
    """("add", target) / ("set", target) for a live op, None for anything else (materials, refimages, ...).
    target is a body name or a comma list of them."""
    op = _LINE.sub("", (op or "").strip())
    try:
        words = shlex.split(op)
    except ValueError:
        words = op.split()
    if len(words) >= 2 and words[0] == "set":
        return ("set", words[1])
    if len(words) >= 2 and words[0] == "add":
        args = dict(w.split("=", 1) for w in words[2:] if "=" in w)
        target = args.get("bodies") or args.get("body") or args.get("name") or ""
        return ("add", target.strip()) if target.strip() else None
    return None


class PartFocus:
    """Turns each shown revision's op into viewer commands for one pane (`send` takes one command line)."""

    def __init__(self, send, clock=time.monotonic, enabled=True):
        self.send, self.clock, self.enabled = send, clock, enabled
        self.on_part = False
        self.moved_at = None
        self.glowing = ""

    def step(self, op):
        part = part_of(op)
        if part is None:
            if _LINE.sub("", (op or "").strip()).startswith("add ") and self.glowing:
                self._glow("")                      # an unnamed add: the old glow would point at the wrong part
            return
        _kind, name = part
        self._glow(name)
        if not self.enabled:
            return
        now = self.clock()
        if self.moved_at is None or now - self.moved_at >= MOVE_EVERY:
            self.send("framebody %s" % name)
            self.moved_at, self.on_part = now, True
        if self.on_part:
            self.send("holdframe 1")                # the next revision keeps this view instead of snapping out

    def clear(self):
        """The pane moved off the run: no glow left behind."""
        self._glow("")
        self.on_part = False

    def _glow(self, name):
        if name != self.glowing:
            self.send("focus %s" % name if name else "focus")
            self.glowing = name

    def _back(self):
        if self.on_part:
            self.send("framebody")
            self.on_part = False


def follow(owner, run, revision, replaying):
    """One pane's hook on each shown revision: the PartFocus kept on `owner` (a pane or the host) gets its op."""
    focus = getattr(owner, "_part_focus", None)
    if focus is None:
        focus = owner._part_focus = PartFocus(owner.cmd, enabled=zoom_to_part_enabled())   # glow always; camera only if on
    if run and not replaying:
        from live_runs import revision_op
        focus.step(revision_op(run, revision))
