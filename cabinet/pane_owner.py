"""Who is in a viewport pane, and are they still there.

A pane's owner is the session that put something in it: the Claude Code session that started its run
(geppetto-live records claude-runs/<pid>.json, the run's <root>.json names that pid) or the session
that sent `host.py pane N model ...` (host.py's client sends CLAUDE_CODE_SESSION_ID along). Its title
and when it last did anything come from its transcript (~/.claude/projects/*/<session>.jsonl): the
newest custom-title line, and the file's mtime. Without a session (Codex, a shell) the run's own
journal is the only sign of life.

presence(): "here" (did something in the last HERE_FOR s), "idle" (quiet), "left" (quiet LEAVE_AFTER s).
The host frees a pane whose owner has left and whose run is over, then AUTO shrinks the layout.
"""
import glob
import json
import os
import time

BASE = os.path.expanduser("~/.cache/geppetto")
RUNS = os.path.join(BASE, "claude-runs")
PROJECTS = os.path.expanduser("~/.claude/projects")
HERE_FOR = 90.0
LEAVE_AFTER = 300.0
TAIL = 262144                      # custom-title is written again and again: the tail always has one

_paths = {}                        # session -> transcript path ("" when none was found)
_titles = {}                       # path -> (mtime, title)


def _read_json(path):
    try:
        with open(path) as stream:
            data = json.load(stream)
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def run_session(d, runs=RUNS):
    """the Claude Code session that started run d, "" when unknown"""
    pid = str(_read_json(d.rstrip("/") + ".json").get("pid") or "")
    if not pid.isdigit():
        return ""
    return str(_read_json(os.path.join(runs, pid + ".json")).get("session") or "")


def transcript(session, projects=PROJECTS):
    safe = "".join(c for c in str(session) if c.isalnum() or c == "-")
    if not safe:
        return ""
    path = _paths.get(safe)
    if path is None or (path and not os.path.exists(path)):
        hits = glob.glob(os.path.join(projects, "*", safe + ".jsonl"))
        path = _paths[safe] = max(hits, key=os.path.getmtime) if hits else ""
    return path


def _title(path, mtime):
    cached = _titles.get(path)
    if cached and cached[0] == mtime:
        return cached[1]
    title = ""
    try:
        with open(path, "rb") as stream:
            stream.seek(max(0, os.path.getsize(path) - TAIL))
            tail = stream.read().decode(errors="replace")
        for line in reversed(tail.splitlines()):
            if '"custom-title"' in line or '"summary"' in line:
                try:
                    ev = json.loads(line)
                except ValueError:
                    continue
                title = ev.get("customTitle") or (ev.get("summary") if ev.get("type") == "summary" else "") or ""
                if title:
                    break
    except OSError:
        pass
    _titles[path] = (mtime, title)
    return title


def session_info(session, projects=PROJECTS):
    """{"title", "seen"} for a session: its name and when its transcript last changed (0: no transcript)"""
    path = transcript(session, projects) if session else ""
    if not path:
        return {"title": "", "seen": 0.0}
    try:
        mtime = os.path.getmtime(path)
    except OSError:
        return {"title": "", "seen": 0.0}
    return {"title": _title(path, mtime), "seen": mtime}


def run_seen(d):
    """when run d last did anything (its journal), 0 for never"""
    try:
        return os.path.getmtime(os.path.join(d, ".live", "events.jsonl"))
    except OSError:
        return 0.0


def presence(seen, now=None, live=False):
    now = time.time() if now is None else now
    if live:
        return "here"
    quiet = now - seen if seen else 1e9
    return "here" if quiet < HERE_FOR else "idle" if quiet < LEAVE_AFTER else "left"


def quiet_label(seen, now=None):
    now = time.time() if now is None else now
    if not seen:
        return ""
    q = max(0, int(now - seen))
    return "%ds" % q if q < 60 else "%dm" % (q // 60) if q < 3600 else "%dh" % (q // 3600)


def owner(session="", run="", since=0.0, live=False, now=None, projects=PROJECTS, runs=RUNS):
    """everything a pane header says about who is in it.
    session: who put it there (a model pane); run: the run it shows; since: when it was put there"""
    session = session or (run_session(run, runs) if run else "")
    info = session_info(session, projects)
    seen = max(info["seen"], run_seen(run) if run else 0.0, since or 0.0)
    state = presence(seen, now, live)
    return {"session": session, "title": info["title"], "seen": seen, "presence": state,
            "quiet": "" if state == "here" else quiet_label(seen, now)}
