"""Pictures Claude looks at or shows, for the chest to pop over the viewport.

The gate hook (claude_gate.py) calls note() on every PreToolUse: a Read of an image file, or a
SendUserFile with images, appends {"at", "path", "tool"} to claude-feed/<session>.jsonl. The chest
polls with Feed.poll() and only keeps sessions that matter there: the CLAUDE card's own session and
sessions that started a geppetto-live run (claude-runs/).

Audit G7: the hook is installed for *every* Claude session on the machine, so note() applies that
same filter at WRITE time - a session that is neither the card session (its id is in
claude-chat.json, written by claude_link.py) nor a claude-runs session records nothing at all.
Feed files are pruned after 30 days and created private (audit G6): the folder is 0700, files 0600.
"""
import glob
import json
import os
import time

BASE = os.path.expanduser("~/.cache/geppetto")
FEED = os.path.join(BASE, "claude-feed")
RUNS = os.path.join(BASE, "claude-runs")
CARD_STATE = os.path.join(BASE, "claude-chat.json")   # claude_link.py's session id file
PRUNE_DAYS = 30
IMAGES = (".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp")


def _safe(session):
    return "".join(c for c in str(session) if c.isalnum() or c in "-_")


def images_in(payload):
    """The image paths a PreToolUse payload looks at or shows ([] for anything else)."""
    tool, data = payload.get("tool_name", ""), payload.get("tool_input") or {}
    if not isinstance(data, dict):
        return []
    paths = [data.get("file_path")] if tool == "Read" else data.get("files") if tool == "SendUserFile" else []
    out = []
    for p in paths if isinstance(paths, list) else []:
        if isinstance(p, str) and p.lower().endswith(IMAGES):
            p = os.path.abspath(os.path.expanduser(p if os.path.isabs(p) else os.path.join(payload.get("cwd") or "", p)))
            out.append(p)
    return out


def card_session(state=CARD_STATE):
    """The CLAUDE card's current session id ('' when there is none)."""
    try:
        with open(state) as stream:
            s = json.load(stream).get("session")
    except (OSError, ValueError, AttributeError):
        return ""
    return _safe(s) if s else ""


def prune(feed=FEED, clock=time.time, days=PRUNE_DAYS):
    """Drop feed files untouched for `days` days; the feed is a pop-up cache, not an archive."""
    try:
        names = os.listdir(feed)
    except OSError:
        return
    for name in names:
        if name.endswith(".jsonl"):
            try:
                path = os.path.join(feed, name)
                if clock() - os.path.getmtime(path) > days * 86400:
                    os.unlink(path)
            except OSError:
                pass


def note(payload, feed=FEED, runs=RUNS, state=CARD_STATE, clock=time.time):
    session = _safe(payload.get("session_id", ""))
    paths = images_in(payload) if session and payload.get("hook_event_name") == "PreToolUse" else []
    if not paths:
        return
    if session != card_session(state) and session not in run_sessions(runs):
        return            # G7: not a Geppetto session - record nothing
    os.makedirs(feed, exist_ok=True)
    try:
        os.chmod(feed, 0o700)
    except OSError:
        pass
    fd = os.open(os.path.join(feed, session + ".jsonl"), os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)
    with os.fdopen(fd, "a") as stream:
        for p in paths:
            stream.write(json.dumps({"at": clock(), "path": p, "tool": payload.get("tool_name", "")}) + "\n")
    prune(feed, clock)


def run_sessions(runs=RUNS):
    out = set()
    for path in glob.glob(os.path.join(runs, "*.json")):
        try:
            with open(path) as stream:
                s = json.load(stream).get("session")
        except (OSError, ValueError, AttributeError):
            continue
        if s:
            out.add(_safe(s))
    return out


class Feed:
    """Reads new feed lines since the last poll; poll() returns the new pictures, oldest first."""

    def __init__(self, feed=FEED, runs=RUNS, clock=time.time, keep=12):
        prune(feed, clock)
        self.feed, self.runs, self.keep = feed, runs, keep
        self.started = clock()             # pictures from before the chest opened don't pop
        self.offsets = {}
        self.pictures = []                 # [{"path", "at", "session", "tool"}], newest last

    def poll(self, card_session=""):
        new = []
        wanted = None
        for path in glob.glob(os.path.join(self.feed, "*.jsonl")):
            session = os.path.basename(path)[:-6]
            try:
                size = os.path.getsize(path)
            except OSError:
                continue
            seen = self.offsets.get(path, 0)
            if size <= seen:
                self.offsets[path] = min(seen, size)
                continue
            if wanted is None:
                wanted = self.run_sessions_and(card_session)
            with open(path, "rb") as stream:
                stream.seek(seen)
                chunk = stream.read(size - seen)
            end = chunk.rfind(b"\n") + 1
            self.offsets[path] = seen + end
            if session not in wanted:
                continue
            for line in chunk[:end].splitlines():
                try:
                    ev = json.loads(line)
                except ValueError:
                    continue
                if ev.get("at", 0) >= self.started and os.path.isfile(ev.get("path", "")):
                    new.append(dict(ev, session=session))
        new.sort(key=lambda e: e["at"])
        self.pictures = (self.pictures + new)[-self.keep:]
        return new

    def run_sessions_and(self, card_session):
        return run_sessions(self.runs) | ({_safe(card_session)} if card_session else set())
