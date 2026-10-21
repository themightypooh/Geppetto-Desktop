"""Run protocol helpers, independent of Qt and any visible window."""
import json
import math
import os
import time
import uuid


def valid_state(state):
    if not isinstance(state, dict):
        return False
    snapshot = state.get("snapshot")
    return (state.get("schemaVersion") == 1 and state.get("ev") == "ai-live"
            and isinstance(state.get("runId"), str) and bool(state["runId"])
            and isinstance(state.get("sequence"), int) and state["sequence"] > 0
            and isinstance(state.get("state"), str)
            and (snapshot is None or isinstance(snapshot, dict)
                 and isinstance(snapshot.get("revision"), int) and snapshot["revision"] > 0
                 and isinstance(snapshot.get("path"), str)))


def read_state(root):
    """Read the atomic index; recover from complete journal records if it is missing/torn."""
    try:
        with open(os.path.join(root, ".live", "latest.json")) as stream:
            state = json.load(stream)
        journal = os.path.join(root, ".live", "events.jsonl")
        index = os.path.join(root, ".live", "latest.json")
        if (valid_state(state)
                and (not os.path.exists(journal) or os.stat(index).st_mtime_ns >= os.stat(journal).st_mtime_ns)):
            return state
    except (OSError, ValueError, AttributeError):
        pass
    last = {}
    try:
        with open(os.path.join(root, ".live", "events.jsonl")) as stream:
            for line in stream:
                if not line.endswith("\n"):
                    break
                try:
                    state = json.loads(line)
                    if (valid_state(state)
                            and state.get("sequence", 0) > last.get("sequence", 0)
                            and (not last or state.get("runId") == last.get("runId"))):
                        last = state
                except (ValueError, AttributeError, TypeError):
                    pass
    except OSError:
        pass
    return last


def preview_path(root, state):
    relative = (state.get("snapshot") or {}).get("path", "")
    if not isinstance(relative, str) or not relative.startswith(".live/revisions/"):
        return ""
    root = os.path.realpath(root)
    path = os.path.realpath(os.path.join(root, relative))
    if not path.startswith(root + os.sep) or not path.endswith(".effigy"):
        return ""
    return path if os.path.isfile(path) else ""


PACES = (0, 1, 3, 5, 10)
TERMINAL = ("completed", "below-target", "cancelled", "failed")


def control_state(state):
    control = state.get("control")
    return control if isinstance(control, dict) else {}


def activity(state):
    control = control_state(state)
    if state.get("state") not in TERMINAL and state.get("state") != "cancelling":
        if control.get("paused"):
            return "paused"
        if control.get("pauseRequested"):
            return "pausing after current operation"
    phase = state.get("state", "")
    operation = state.get("operation") or ""
    return {
        "starting": "starting", "researching": "researching", "waiting": "waiting for model",
        "building": operation.replace("_", " ") or "building", "revision": "model updated",
        "judging": "judging", "completed": "completed", "below-target": "stopped below target",
        "cancelled": "cancelled", "failed": "failed", "operation-failed": "operation rejected",
        "cancelling": "stopping", "pausing": "pausing after current operation", "paused": "paused",
        "resumed": "resumed", "pace-changed": "pace changed", "direction-queued": "direction queued",
        "direction-delivered": "direction delivered", "control-rejected": "control rejected",
    }.get(phase, phase)


def next_pace(seconds):
    """Run-card cycle: Normal -> 1 -> 3 -> 5 -> 10 -> Normal (finer values are protocol-only)."""
    larger = [p for p in PACES if p > (seconds or 0)]
    return larger[0] if larger else 0


def send_command(root, command, **fields):
    """Queue one durable, uniquely identified command for the run's control watcher."""
    state = read_state(root)
    if not state.get("runId"):
        raise ValueError("Run has not published its control identity yet")
    if state.get("state") in TERMINAL:
        raise ValueError("Run has finished; controls never restart it")
    if command == "pace":
        seconds = fields.get("seconds")
        if (not isinstance(seconds, (int, float)) or isinstance(seconds, bool)
                or not math.isfinite(seconds) or not 0 <= seconds <= 30):
            raise ValueError("pace must be 0 to 30 seconds")
        fields["seconds"] = float(seconds)
    if command == "instruct":
        text = fields.get("text")
        if not isinstance(text, str) or not text.strip() or len(text) > 2000:
            raise ValueError("direction must be 1 to 2000 characters")
        fields["text"] = text.strip()
    directory = os.path.join(root, ".live", "commands")
    os.makedirs(directory, exist_ok=True)
    ident = "%020d-%s" % (time.time_ns(), uuid.uuid4().hex)
    path = os.path.join(directory, ident + ".json")
    temp = path + ".tmp"
    with open(temp, "x") as stream:
        json.dump(dict(fields, schemaVersion=1, runId=state["runId"], id=ident, command=command), stream)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temp, path)
    return ident


def request_cancel(root):
    state = read_state(root)
    if not state.get("runId"):
        raise ValueError("Run has not published its control identity yet")
    path = os.path.join(root, ".live", "control.json")
    temp = path + "." + uuid.uuid4().hex + ".tmp"
    try:
        with open(temp, "x") as stream:
            json.dump({"schemaVersion": 1, "runId": state["runId"], "command": "cancel"}, stream)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temp, path)
    finally:
        if os.path.exists(temp):
            os.unlink(temp)
