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
OPEN = ("open", "held")     # a script run that finished its lines (or held at a bad one) and takes more (geppetto-live more)


def close_open(root):
    """Ask an open script run to close: an inbox entry it takes between lines (ScriptLive.Inbox)."""
    directory = os.path.join(root, ".live", "inbox")
    os.makedirs(directory, exist_ok=True)
    name = "%019d%s" % (time.time_ns() // 100 + 621355968000000000, uuid.uuid4().hex[:6])
    tmp = os.path.join(directory, name + ".close.tmp")
    open(tmp, "w").close()
    os.replace(tmp, tmp[:-4])


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
        "open": "open: waiting for more lines", "held": "held at a bad line",
        "building": operation.replace("_", " ") or "building", "revision": "model updated",
        "judging": "judging", "completed": "completed", "below-target": "stopped below target",
        "cancelled": "cancelled", "failed": "failed", "operation-failed": "operation rejected",
        "cancelling": "stopping", "pausing": "pausing after current operation", "paused": "paused",
        "resumed": "resumed", "stepping": "stepping one change", "pace-changed": "pace changed", "direction-queued": "direction queued",
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


# ---- replay and notes ---------------------------------------------------------------------
# A run already keeps every committed revision under .live/revisions, so a replay just shows them
# in order: nothing is recorded or saved for it. Notes are the one thing it writes.

_rev_ops = {}


def revision_op(root, rev):
    """The op that made revision `rev` of a run ("line 14: add Blob ..."), from its journal; read again only when it grows."""
    path = os.path.join(root, ".live", "events.jsonl")
    try:
        size = os.path.getsize(path)
    except OSError:
        return ""
    seen = _rev_ops.get(root)
    if not seen or seen[0] > size:
        seen = _rev_ops[root] = [0, {}]
    if seen[0] < size:
        with open(path, "rb") as stream:
            stream.seek(seen[0])
            chunk = stream.read(size - seen[0])
        end = chunk.rfind(b"\n") + 1           # a half-written last line waits for the next read
        seen[0] += end
        for line in chunk[:end].splitlines():
            try:
                ev = json.loads(line)
            except ValueError:
                continue
            r = (ev.get("snapshot") or {}).get("revision") if isinstance(ev, dict) else None
            if isinstance(r, int) and ev.get("state") == "revision" and ev.get("operation"):
                seen[1][r] = ev["operation"]
    return seen[1].get(rev, "")


def steps(root):
    """Every kept revision of a run, in order: [{"rev": N, "op": "line 14: add Blob ..."}]."""
    out = {}
    try:
        with open(os.path.join(root, ".live", "events.jsonl")) as stream:
            for line in stream:
                try:
                    ev = json.loads(line)
                except ValueError:
                    continue
                rev = (ev.get("snapshot") or {}).get("revision") if isinstance(ev, dict) else None
                if isinstance(rev, int) and rev > 0 and (ev.get("state") == "revision" or rev not in out):
                    out[rev] = ev.get("operation") or out.get(rev, "")
    except OSError:
        pass
    kept = set()
    try:
        kept = {int(f[:8]) for f in os.listdir(os.path.join(root, ".live", "revisions"))
                if f.endswith(".effigy") and f[:8].isdigit()}
    except OSError:
        pass
    return [{"rev": r, "op": out[r] or ("start" if r == 1 else "")} for r in sorted(out) if r in kept]


def revision_path(root, rev):
    """the kept .effigy of revision `rev` of a run, or """""
    folder = os.path.join(root, ".live", "revisions")
    try:
        for f in os.listdir(folder):
            if f.endswith(".effigy") and f[:8].isdigit() and int(f[:8]) == rev:
                return os.path.join(folder, f)
    except (OSError, ValueError):
        pass
    return ""


def run_meta(root):
    try:
        with open(root.rstrip(os.sep) + ".json") as stream:
            meta = json.load(stream)
        return meta if isinstance(meta, dict) else {}
    except (OSError, ValueError):
        return {}


def script_line(root, op):
    """The full source line a script run's step ran ('line 14: add Blob ...' names it), or ''. Older runs
    kept no copy and the script may have been edited since, so the line must still start with what the run recorded; if it moved,
    the first line that does is used, and if none does there is nothing to show."""
    kept = os.path.join(root, "script.txt")             # the copy taken when the run started
    script = kept if os.path.isfile(kept) else run_meta(root).get("script", "")
    if not script or not op.startswith("line "):
        return ""
    try:
        head, recorded = op[5:].split(": ", 1)
        number = int(head)
        with open(script) as stream:
            lines = [x.rstrip("\n") for x in stream]
    except (OSError, ValueError):
        return ""
    recorded = recorded[:-3] if recorded.endswith("...") else recorded
    if 0 < number <= len(lines) and lines[number - 1].strip().startswith(recorded):
        return lines[number - 1]
    return next((x for x in lines if x.strip().startswith(recorded)), "")


def add_note(root, rev, op, text):
    text = (text or "").strip()
    if not text or len(text) > 2000:
        raise ValueError("a note must be 1 to 2000 characters")
    meta = run_meta(root)
    note = {"time": time.strftime("%Y-%m-%dT%H:%M:%S"), "run": os.path.basename(root.rstrip(os.sep)),
            "rev": int(rev), "op": op or "", "text": text, "script": meta.get("script", ""),
            "prompt": meta.get("prompt", ""), "read": False}
    if op.startswith("line "):
        try:
            note["line"] = int(op[5:].split(":", 1)[0])
        except ValueError:
            pass
    with open(os.path.join(root, "notes.jsonl"), "a") as stream:
        stream.write(json.dumps(note) + "\n")
    return note


def notes(root):
    out = []
    try:
        with open(os.path.join(root, "notes.jsonl")) as stream:
            for line in stream:
                try:
                    out.append(json.loads(line))
                except ValueError:
                    pass
    except OSError:
        pass
    return out


def unread_notes(runs_root, script=None):
    """Notes nobody has picked up yet, oldest first, optionally only for one build script."""
    found = []
    if not os.path.isdir(runs_root):
        return found
    want = os.path.realpath(script) if script else None
    for name in sorted(os.listdir(runs_root)):
        d = os.path.join(runs_root, name)
        if name.startswith(".") or not os.path.isdir(d):
            continue
        for n in notes(d):
            if not n.get("read") and (want is None or n.get("script") and os.path.realpath(n["script"]) == want):
                found.append(dict(n, dir=d))
    return found


def mark_read(found):
    by_dir = {}
    for n in found:
        by_dir.setdefault(n["dir"], set()).add((n.get("time"), n.get("rev"), n.get("text")))
    for d, keys in by_dir.items():
        rows = notes(d)
        for n in rows:
            if (n.get("time"), n.get("rev"), n.get("text")) in keys:
                n["read"] = True
        temp = os.path.join(d, "notes.jsonl.tmp")
        with open(temp, "w") as stream:
            stream.writelines(json.dumps(n) + "\n" for n in rows)
        os.replace(temp, os.path.join(d, "notes.jsonl"))


def rewind_open(root, rev, op=""):
    """REWIND HERE in a replay: an open script run puts the model back to revision `rev` (ScriptLive's
    .rewind inbox entry), and the Claude session behind it hears of it as a note, so it builds on that."""
    rev = int(rev)
    if rev < 1 or not revision_path(root, rev):
        raise ValueError("no step %d kept in this run" % rev)
    directory = os.path.join(root, ".live", "inbox")
    os.makedirs(directory, exist_ok=True)
    name = "%019d%s" % (time.time_ns() // 100 + 621355968000000000, uuid.uuid4().hex[:6])
    tmp = os.path.join(directory, name + ".rewind.tmp")
    with open(tmp, "w") as stream:
        stream.write(str(rev))
    os.replace(tmp, tmp[:-4])
    return add_note(root, rev, op, "REWOUND to this step: the model is back to how it was here; build on it from now on")


def take_notes(root):
    """The unread notes on one run, oldest first, marked read: each is handed over exactly once."""
    found = [dict(n, dir=root) for n in notes(root) if not n.get("read")]
    if found:
        mark_read(found)
    return found


def notes_text(found, root=None):
    """Notes as the modeller reads them, with the script line each step came from and how to act on it."""
    if not found:
        return ""
    lines = []
    for n in found:
        code = script_line(n["dir"], n.get("op", "")) if n.get("op") else ""
        lines.append("NOTE from po on step %d%s: %s" % (n.get("rev", 0), " (line %d)" % n["line"] if n.get("line") else "", n["text"]))
        if code:
            lines.append("  that step: %s" % code.strip()[:300])
    if root:
        lines.append("Act on it in the same run: `geppetto-live more %s fix.txt`. A `set` on an early feature rebuilds"
                     " everything after it, so step 1 can be fixed without starting over." % root)
    return "\n".join(lines) + "\n"


def wait_notes(root, poll=0.5, sleep=time.sleep, alive=None):
    """geppetto-live wait: block until po leaves a note on the run (returns its text) or the run ends (None)."""
    while True:
        found = take_notes(root)
        if found:
            return notes_text(found, root)
        state = read_state(root).get("state")
        if state in TERMINAL or (alive is not None and not alive()):
            found = take_notes(root)            # a note left just before CLOSE still counts
            return notes_text(found, root) if found else None
        sleep(poll)


def print_notes(found, out):
    for n in found:
        where = n.get("script") or n.get("prompt") or n["run"]
        line = (":%d" % n["line"]) if n.get("line") else ""
        out.write("NOTE  %s%s  (run %s, step %d)\n" % (where, line, n["run"], n.get("rev", 0)))
        if n.get("op"):
            out.write("  step: %s\n" % n["op"])
        out.write("  po:   %s\n" % n["text"])


if __name__ == "__main__":
    # geppetto-notes [build.txt] [--all] [--keep] [--follow]: print the notes left on runs in the chest, then mark them read
    # --follow keeps watching and prints each new note the moment it is left (a modelling session's Monitor runs this)
    import sys, time
    args = sys.argv[1:]
    runs_root = os.path.expanduser("~/.local/share/geppetto/research")
    script = next((a for a in args if not a.startswith("--")), None)
    if "--wait" in args:                     # geppetto-live wait RUN [PID]
        root = os.path.abspath(script)
        pid = int(args[args.index("--wait") + 2]) if len(args) > args.index("--wait") + 2 else 0
        def alive():
            try:
                os.kill(pid, 0)
                return True
            except OSError:
                return False
        text = wait_notes(root, alive=alive if pid else None)
        if text is None:
            print("run %s closed (%s): no notes came; it takes nothing more" % (root, read_state(root).get("state", "ended")))
            sys.exit(1)
        sys.stdout.write(text)
        sys.exit(0)
    if "--take" in args:                     # geppetto-live more: the run's unread notes go with its answer
        sys.stdout.write(notes_text(take_notes(os.path.abspath(script)), os.path.abspath(script)))
        sys.exit(0)
    if "--follow" in args:
        while True:
            found = unread_notes(runs_root, script)
            if found:
                print_notes(found, sys.stdout)
                sys.stdout.flush()
                mark_read(found)
            time.sleep(1)
    found = unread_notes(runs_root, script)
    if "--all" in args:
        found = [dict(n, dir=os.path.join(runs_root, n["run"])) for name in sorted(os.listdir(runs_root))
                 if not name.startswith(".") and os.path.isdir(os.path.join(runs_root, name))
                 for n in notes(os.path.join(runs_root, name))
                 if script is None or n.get("script") and os.path.realpath(n["script"]) == os.path.realpath(script)]
    print_notes(found, sys.stdout)
    if found and "--keep" not in args and "--all" not in args:
        mark_read(found)
