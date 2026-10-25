"""Provider-neutral Build completion gate. Prose is never an execution receipt.

The live-tool wrapper writes an opt-in receipt only after its real runner answers.
Build replies stay private until a matching, changed saved revision is verified.
No chat text, fenced script or shell command is executed by this module.
"""
from contextlib import contextmanager
from contextvars import ContextVar
import hashlib
import json
import os
from pathlib import Path
import tempfile
import time
import uuid

import live_runs

ACTIVE = ContextVar("geppetto_build_turn", default=None)
RECEIPTS = "GEPPETTO_BUILD_RECEIPTS"
TOKEN = "GEPPETTO_BUILD_TOKEN"


def capabilities(model):
    model = model or {}
    available = bool(model and model.get("available", True))
    tools = model.get("kind") in ("claude-cli", "grok-cli", "roundtable-cli")
    if model.get("family") == "zoobie":
        tools = False                 # manual text bridge, not an execution adapter
    return {"plan": available, "build": available and tools}


def child_environment(environment):
    result = dict(environment)
    turn = ACTIVE.get()
    if turn is not None:
        result.update(turn.environment())
    return result


@contextmanager
def activate(turn):
    token = ACTIVE.set(turn)
    try:
        yield
    finally:
        ACTIVE.reset(token)


class ExecutionMissing(RuntimeError):
    pass


class BuildTurn:
    def __init__(self, runs_root=None, storage=None):
        self.runs_root = Path(runs_root or Path.home() / ".local/share/geppetto/research").resolve()
        base = Path(storage or Path.home() / ".cache/geppetto/build-verification")
        base.mkdir(parents=True, exist_ok=True, mode=0o700)
        self.directory = Path(tempfile.mkdtemp(prefix="turn-", dir=base))
        self.token = uuid.uuid4().hex
        self.started_ns = time.time_ns()
        manifest = self.directory / "request.json"
        manifest.write_text(json.dumps({"token": self.token, "runsRoot": str(self.runs_root), "startedNs": self.started_ns}))
        manifest.chmod(0o600)

    def environment(self):
        return {RECEIPTS: str(self.directory), TOKEN: self.token}

    def receipt(self):
        for path in sorted(self.directory.glob("*.receipt.json"), reverse=True):
            try:
                if path.stat().st_size > 65536:
                    continue
                item = json.loads(path.read_text())
                if item.get("token") != self.token or item.get("finishedNs", 0) < self.started_ns:
                    continue
                root = Path(item["run"]).resolve()
                if root.parent != self.runs_root or item.get("operation") not in ("build", "more", "rewind"):
                    continue
                state = live_runs.read_state(str(root))
                if state.get("runId") != item.get("runId"):
                    continue
                revision = item.get("revision", 0)
                if revision <= item.get("beforeRevision", 0) or revision < 2:
                    continue
                snapshot = {"snapshot": {"path": item.get("snapshot", "")}}
                saved = live_runs.preview_path(str(root), snapshot)
                if not saved or hashlib.sha256(Path(saved).read_bytes()).hexdigest() != item.get("sha256"):
                    continue
                if (state.get("snapshot") or {}).get("revision", 0) < revision:
                    continue
                return item
            except (OSError, ValueError, TypeError, KeyError, AttributeError):
                continue
        return None

    def retry_prompt(self):
        return ("BUILD EXECUTION CHECK: Your preceding answer did not produce a verified live-tool execution. "
                "Do not give the user a script to run or ask them to tell you to execute. Use your tools now: "
                "inspect the existing run if one was started, fix held/errors in that SAME run, or write the "
                "requested Effigy script and execute geppetto-live. Do not repeat completed edits, rebuild an "
                "existing model, bypass permissions, change research consent, restart the chest, or touch "
                "another pane. Respect the original request and current approvals. Finish only after the "
                "tool confirms a saved revision; if tools genuinely fail, report the concrete failure. "
                "A printed command, script file alone, invented run path or statement of success is not execution.")

    def failure(self):
        return ExecutionMissing("Build did not produce a verified saved revision after automatic execution retry. "
                                "No text-only build was accepted. Check the tool error; the user should not have to run a pasted script.")


def checked_stream(model, history, invoke, cancel=None, turn_factory=BuildTurn):
    if not capabilities(model)["build"]:
        raise ExecutionMissing("This connection has no execution tools. Plan is available; Build needs a tool-enabled adapter. No build was claimed or sent for manual execution.")
    turn = turn_factory()
    messages = [dict(row) for row in history]
    with activate(turn):
        for attempt in range(2):
            if cancel is not None and cancel.is_set():
                return
            chunks = []
            for chunk in invoke(messages):
                if cancel is not None and cancel.is_set():
                    return
                chunks.append(chunk)
            reply = "".join(chunks)
            if cancel is not None and cancel.is_set():
                return
            receipt = turn.receipt()
            if receipt:
                yield reply or ("Executed the live build; saved revision %d." % receipt["revision"])
                return
            if attempt == 0:
                # Keep invalid claims out of the visible conversation. This is an
                # internal corrective turn, not a new user request or approval.
                messages += [{"role": "model", "text": reply}, {"role": "you", "text": turn.retry_prompt()}]
        raise turn.failure()


def record(root, answer, operation, before_revision):
    """Called by geppetto-live after a successful real inbox answer, never by chat."""
    directory, token = os.environ.get(RECEIPTS), os.environ.get(TOKEN)
    if not directory or not token:
        return                 # all existing tools behave exactly as before
    folder = Path(directory).resolve()
    request = json.loads((folder / "request.json").read_text())
    root = Path(root).resolve()
    if token != request.get("token") or root.parent != Path(request["runsRoot"]).resolve():
        raise ExecutionMissing("Build receipt identity/root mismatch")
    if operation not in ("build", "more", "rewind"):
        return
    answer_file = root / ".live/answers" / (Path(answer).name + ".txt")
    if answer_file.stat().st_mtime_ns < request["startedNs"]:
        raise ExecutionMissing("Build answer predates this request")
    state = live_runs.read_state(str(root))
    snapshot = state.get("snapshot") or {}
    saved = live_runs.preview_path(str(root), state)
    revision = snapshot.get("revision", 0)
    if not saved or revision <= int(before_revision) or revision < 2:
        raise ExecutionMissing("Tool answered without a changed saved revision")
    item = {"token": token, "finishedNs": time.time_ns(), "run": str(root), "runId": state["runId"],
            "operation": operation, "beforeRevision": int(before_revision), "revision": revision,
            "snapshot": snapshot["path"], "sha256": hashlib.sha256(Path(saved).read_bytes()).hexdigest()}
    path = folder / ("%020d-%s.receipt.json" % (time.time_ns(), uuid.uuid4().hex))
    temp = path.with_suffix(".tmp")
    fd = os.open(temp, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w") as stream:
        json.dump(item, stream); stream.flush(); os.fsync(stream.fileno())
    os.replace(temp, path)


if __name__ == "__main__":
    import sys
    try:
        record(*sys.argv[1:])
    except (OSError, ValueError, TypeError, KeyError, ExecutionMissing) as error:
        print("Build execution receipt: " + str(error), file=sys.stderr)
        raise SystemExit(1)
