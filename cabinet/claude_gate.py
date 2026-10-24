#!/usr/bin/env python3
"""STOP and PAUSE from the chest reach the Claude session behind a run.

geppetto-live records which Claude Code session started each run (claude-runs/<pid>.json, the pid
of the run process, which the run's own <root>.json also names). STOP or PAUSE on that run card
writes claude-gate/<session>.json, and this file, installed as a Claude Code hook, reads it:

  PreToolUse        stopped -> the tool call is refused and Claude halts; paused -> it waits here
                    until RESUME (or STOP)
  PostToolUse       stopped -> Claude halts as soon as the tool it was in (the build) returns
  UserPromptSubmit  a new message to that session lifts the stop

Notes po leaves on a step of a run this session started (the chest's REPLAY) reach it too: at its next tool
call (PostToolUse) or message (UserPromptSubmit), unless `geppetto-live wait` or `more` took them first.

Any error lets the tool call through: a broken gate must never wedge every Claude session.
"""
import json
import os
import sys
import time

BASE = os.path.expanduser("~/.cache/geppetto")
GATES = os.path.join(BASE, "claude-gate")
RUNS = os.path.join(BASE, "claude-runs")
WAIT_LIMIT = 55 * 60          # the hook's timeout in settings.json is an hour


def _write(path, data):
    # Private by default (audit G6): gate/run files name sessions and run pids, so the
    # folder is 0700 and the file lands 0600 (the temp is created 0600, os.replace keeps it).
    os.makedirs(os.path.dirname(path), exist_ok=True)
    try:
        os.chmod(os.path.dirname(path), 0o700)
    except OSError:
        pass
    temp = "%s.%d.tmp" % (path, os.getpid())
    fd = os.open(temp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as stream:
        json.dump(data, stream)
    os.replace(temp, path)


def _read(path):
    try:
        with open(path) as stream:
            data = json.load(stream)
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def _safe(session):
    return "".join(c for c in str(session) if c.isalnum() or c in "-_")


def gate(session):
    """{"state": "stopped"|"paused", "reason": ..., "at": ...} or {} when the session may run."""
    return _read(os.path.join(GATES, _safe(session) + ".json")) if _safe(session) else {}


def set_gate(session, state, reason=""):
    if _safe(session):
        _write(os.path.join(GATES, _safe(session) + ".json"), {"state": state, "reason": reason, "at": time.time()})


def clear_gate(session):
    try:
        os.unlink(os.path.join(GATES, _safe(session) + ".json"))
    except OSError:
        pass


def record_run(pid, session, parent=0, script=""):
    """geppetto-live: the run process `pid` was started by Claude session `session`."""
    _write(os.path.join(RUNS, "%d.json" % int(pid)), {"session": session, "parent": int(parent or 0), "script": script, "at": time.time()})


def session_for_run(root):
    """The Claude session that started the run in `root` ('' when a person or the AI builder did)."""
    pid = _read(root + ".json").get("pid")
    if not isinstance(pid, int):
        return ""
    return str(_read(os.path.join(RUNS, "%d.json" % pid)).get("session", ""))


def run_root(pid, runs=os.path.expanduser("~/.local/share/geppetto/research")):
    """The run folder whose process is `pid` ('' when none)."""
    import glob
    for meta in sorted(glob.glob(os.path.join(runs, "*.json")), key=os.path.getmtime, reverse=True)[:200]:
        if _read(meta).get("pid") == int(pid) and os.path.isdir(meta[:-5]):
            return meta[:-5]
    return ""


def cancel_pid(pid, runs=os.path.expanduser("~/.local/share/geppetto/research")):
    """Cancel the live run whose process is `pid` through its own control file. True when found."""
    from live_runs import request_cancel
    root = run_root(pid, runs)
    try:
        return bool(root) and request_cancel(root) is None
    except (OSError, ValueError):
        return False


def session_runs(session, research=None, days=2):
    """The run folders of live run processes this Claude session started (recent ones only)."""
    import glob
    out = []
    for meta in glob.glob(os.path.join(RUNS, "*.json")):
        try:
            if time.time() - os.path.getmtime(meta) > days * 86400:
                continue
        except OSError:
            continue
        if _read(meta).get("session") != session:
            continue
        pid = int(os.path.basename(meta)[:-5])
        try:
            os.kill(pid, 0)
        except (OSError, ValueError):
            continue
        root = run_root(pid, research) if research else run_root(pid)
        if root:
            out.append(root)
    return out


def session_notes(session, research=None):
    """Unread notes on this session's live runs, as text (taken: each note is handed over once)."""
    import live_runs
    return "".join(live_runs.notes_text(live_runs.take_notes(root), root) for root in session_runs(session, research))


def _halt(event, reason):
    out = {"continue": False, "stopReason": "Stopped from the Geppetto chest", "systemMessage": reason}
    if event == "PreToolUse":
        out["hookSpecificOutput"] = {"hookEventName": "PreToolUse", "permissionDecision": "deny",
                                     "permissionDecisionReason": reason}
    return out


def hook(payload, sleep=time.sleep, clock=time.monotonic):
    """One hook call; returns the JSON to print, or None to let it through untouched."""
    session = payload.get("session_id", "")
    event = payload.get("hook_event_name", "")
    if not session:
        return None
    if event == "UserPromptSubmit":
        clear_gate(session)
        return _notes(event, session)
    g = gate(session)
    stopped = "po pressed STOP in the Geppetto chest%s. Stop now: no more tool calls, no retries. Say in one line where you left things."
    if g.get("state") == "stopped":
        return _halt(event, stopped % (": " + g["reason"] if g.get("reason") else ""))
    if g.get("state") == "paused" and event == "PreToolUse":
        start = clock()
        while clock() - start < WAIT_LIMIT:
            sleep(0.5)
            g = gate(session)
            if g.get("state") == "stopped":
                return _halt(event, stopped % (": " + g["reason"] if g.get("reason") else ""))
            if g.get("state") != "paused":
                return None
    return _notes(event, session) if event == "PostToolUse" else None


def _notes(event, session):
    try:
        text = session_notes(session)
    except Exception:
        return None
    return {"hookSpecificOutput": {"hookEventName": event, "additionalContext": text}} if text else None


def main():
    try:
        payload = json.load(sys.stdin)
        try:
            import claude_feed
            claude_feed.note(payload)              # pictures for the chest to pop (claude_feed.py)
        except Exception:
            pass
        out = hook(payload)
        if out:
            print(json.dumps(out))
    except Exception:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
