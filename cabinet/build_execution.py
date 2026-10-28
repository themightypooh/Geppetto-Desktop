"""Provider-neutral Build completion gate. Prose is never an execution receipt.

The live-tool wrapper writes an opt-in receipt only after its real runner answers.
Build replies stay private until a matching, changed saved revision is verified.
No chat text, fenced script or shell command is executed by this module.
"""
from contextlib import contextmanager
from contextvars import ContextVar
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import tempfile
import time
import uuid

import live_runs

ACTIVE = ContextVar("geppetto_build_turn", default=None)
RECEIPTS = "GEPPETTO_BUILD_RECEIPTS"
TOKEN = "GEPPETTO_BUILD_TOKEN"
# Pip verification (sub-task 2, patch only). Opt-in: only when a Pip model
# path is explicitly supplied to BuildTurn (argument or GEPPETTO_PIP_MODEL
# env). Non-Pip builds are untouched: no pip path means no pip checks.
PIP_MODEL_ENV = "GEPPETTO_PIP_MODEL"
PIP_BACKUP_ENV = "GEPPETTO_PIP_BACKUP"
PIP_PRE_HASH_ENV = "GEPPETTO_PIP_PRE_HASH"
# Invariants of the Pip skin fix. These are content checks, not a hash
# pin: the current pants.effigy hash moves with every legitimate edit,
# so the pre-edit hash is captured live at turn creation (see BuildTurn).
_PIP_MATERIALLOOK = re.compile(r"^materiallook 2 color=#654b40(?:\s|$)", re.M)
_PIP_REMOVED = re.compile(r"^\s*ints\s+RemovedPieces\s+.*\b15\b", re.M)


def _sha256_file(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def verify_pip_final(pants_path, snapshot_sha256, backup_path=None, pre_hash=None):
    """Verify a Pip doc after a live run. Returns (ok, reason, post_hash).

    Checks, in order: pants.effigy exists; its hash equals the verified
    snapshot hash; the skin-fix lines are still present (materiallook 2
    colour #654b40, and a Suit `ints RemovedPieces 15` line -- the Face
    feature's empty RemovedPieces line must not satisfy this); and, when
    a backup is supplied, it is a different file from pants.effigy whose
    hash equals the pre-edit hash captured before the run. Backup
    selection is by hash, never by filename: a stale pre-skin-fix backup
    (e.g. hash 388faf2c...) will not match a skin-fixed pre-edit hash
    and therefore fails, instead of silently re-greying the skin.
    A missing backup is a failure for a Pip turn, not a warning.
    """
    pants = Path(pants_path).resolve()
    if not pants.is_file():
        return False, "pip final model missing: %s" % pants, None
    try:
        raw = pants.read_bytes()
    except OSError as error:
        return False, "pip final model unreadable: %s" % error, None
    post_hash = hashlib.sha256(raw).hexdigest()
    if snapshot_sha256 is not None and post_hash != snapshot_sha256:
        return False, "pip final hash %s != snapshot hash %s" % (post_hash, snapshot_sha256), post_hash
    text = raw.decode("utf-8", errors="replace")
    if not _PIP_MATERIALLOOK.search(text):
        return False, "pip skin fix lost: no 'materiallook 2 color=#654b40' line", post_hash
    if not _PIP_REMOVED.search(text):
        return False, "pip skin fix lost: no 'ints RemovedPieces 15' line", post_hash
    if backup_path is None or pre_hash is None:
        return False, "pip backup missing: no backup path / pre-edit hash for this turn", post_hash
    backup = Path(backup_path).resolve()
    if backup == pants:
        return False, "pip backup must not be pants.effigy itself", post_hash
    if not backup.is_file():
        return False, "pip backup file missing: %s" % backup, post_hash
    try:
        backup_hash = _sha256_file(backup)
    except OSError as error:
        return False, "pip backup unreadable: %s" % error, post_hash
    if backup_hash != pre_hash:
        return False, "pip backup hash %s != pre-edit hash %s" % (backup_hash, pre_hash), post_hash
    return True, "", post_hash




def _capability_tokens(model):
    tokens = set()
    for key in ("capabilities", "modes", "features"):
        value = model.get(key)
        if isinstance(value, str):
            value = [value]
        if isinstance(value, (list, tuple, set, frozenset)):
            tokens.update(str(item).strip().lower() for item in value)
    return tokens


def capabilities(model):
    model = model or {}
    available = bool(model and model.get("available", True))
    tokens = _capability_tokens(model)

    # Explicit capability-based mode availability. A model may declare what it
    # can do (tools/build/execution/agent for Build, plan for Plan) instead of
    # relying on an app-side family/kind allowlist.
    build = None
    plan = True
    if tokens:
        if tokens & {"tools", "tool", "build", "execution", "execute", "agent", "live", "act"}:
            build = True
        elif tokens <= {"chat", "plan", "research", "vision", "none"}:
            build = False
    if "plan" in tokens:
        plan = True

    # Explicit boolean capability fields override token inference.
    for key in ("build", "tools", "execution", "execute", "agent", "can_build",
                "can_execute", "supports_build", "supports_tools", "build_supported",
                "tools_supported", "live"):
        if key in model:
            build = bool(model.get(key))
            break
    for key in ("plan", "supports_plan", "plan_supported"):
        if key in model:
            plan = bool(model.get(key))
            break

    # Backward-compatible default: the CLI families that can actually run tools.
    if build is None:
        build = model.get("kind") in ("claude-cli", "grok-cli", "roundtable-cli")
        if model.get("family") == "zoobie":
            build = False                 # manual text bridge, not an execution adapter

    # Zoobie is never an execution adapter, even if a capability was claimed.
    if model.get("family") == "zoobie":
        build = False

    if not available:
        build = False
        plan = False
    return {"plan": bool(available and plan), "build": bool(available and build)}


def mode_supported(model, mode):
    mode = (mode or "").strip().lower()
    caps = capabilities(model)
    if mode in caps:
        return bool(caps[mode])
    return False


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


def _write_json_atomic(path, data):
    """Write a small turn-dir JSON file atomically, 0600, like a receipt."""
    path = Path(path)
    temp = path.parent / (path.name + "." + uuid.uuid4().hex + ".tmp")
    fd = os.open(temp, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        with os.fdopen(fd, "w") as stream:
            json.dump(data, stream); stream.flush(); os.fsync(stream.fileno())
        os.replace(temp, path)
    except BaseException:
        try:
            temp.unlink()
        except OSError:
            pass
        raise


def _read_json(path):
    """A turn-dir JSON dict, or None if absent. Corrupt JSON propagates so
    callers cannot silently treat an unreadable binding as 'no binding'."""
    try:
        data = json.loads(Path(path).read_text())
    except FileNotFoundError:
        return None
    return data if isinstance(data, dict) else None


def _script_path(root):
    kept = Path(root) / "script.txt"
    if kept.is_file():
        return str(kept)
    try:
        script = live_runs.run_meta(str(root)).get("script", "")
    except (OSError, ValueError, AttributeError):
        script = ""
    return script if isinstance(script, str) else ""


def _script_hash(root):
    path = _script_path(root)
    if path and Path(path).is_file():
        try:
            return hashlib.sha256(Path(path).read_bytes()).hexdigest()
        except OSError:
            return ""
    return ""


def _output_name(root):
    """Basename of the last `save <name>` line in the run script, else ''.
    This is the target effigy name as the script itself names it; the gate
    still verifies the snapshot file hash, it does not trust this name."""
    path = _script_path(root)
    if not path or not Path(path).is_file():
        return ""
    try:
        lines = Path(path).read_text().splitlines()
    except OSError:
        return ""
    output = ""
    for line in lines:
        parts = line.strip().split()
        if len(parts) >= 2 and parts[0] == "save":
            output = Path(parts[1].strip("\"'")).name
    return output


def _run_opened_ns(root):
    """When this run opened, in epoch ns, or None if it cannot be told.

    Preferred source is the first journal event's timestamp (stable after
    start); fallback is the run directory's mtime, which in practice is its
    creation time because later files land under .live/. Unknown means
    fail-open here -- the existing token/revision/hash checks and, once a
    binding exists, the runId check still apply.
    """
    try:
        with open(Path(root) / ".live" / "events.jsonl") as stream:
            first = json.loads(stream.readline())
        stamp = first.get("timestamp", "")
        if isinstance(stamp, str) and stamp:
            stamp = stamp[:-1] + "+00:00" if stamp.endswith("Z") else stamp
            stamp = re.sub(r"\.(\d+)(?=[+-]\d{2}:|\Z)",
                           lambda m: "." + m.group(1)[:6].ljust(6, "0"), stamp)
            parsed = datetime.fromisoformat(stamp)
            if parsed.tzinfo is None:
                parsed = parsed.replace(tzinfo=timezone.utc)
            return int(parsed.timestamp() * 1_000_000_000)
    except (OSError, ValueError, TypeError, AttributeError):
        pass
    try:
        return Path(root).stat().st_mtime_ns
    except OSError:
        return None


class BuildTurn:
    # Turn dirs older than this are stale (host restart / crash / abandoned
    # generator) and are swept on the next turn; see _sweep_stale().
    STALE_AFTER_SECS = 24 * 3600

    def __init__(self, runs_root=None, storage=None, pip_model=None):
        self.runs_root = Path(runs_root or Path.home() / ".local/share/geppetto/research").resolve()
        base = Path(storage or Path.home() / ".cache/geppetto/build-verification")
        base.mkdir(parents=True, exist_ok=True, mode=0o700)
        self._base = base
        self.directory = Path(tempfile.mkdtemp(prefix="turn-", dir=base))
        self.token = uuid.uuid4().hex
        self.started_ns = time.time_ns()
        self._consumed = set()
        # Pip opt-in (sub-task 2). Explicit argument wins; env is a fallback.
        # Supply pip_model ONLY when the Build target is the Pip doc
        # (host DEFAULT_MODEL, pants.effigy); never for a new-model build.
        pip_model = pip_model or os.environ.get(PIP_MODEL_ENV)
        self.pip_path = Path(pip_model).resolve() if pip_model else None
        self.pip_backup_path = os.environ.get(PIP_BACKUP_ENV) or None
        self.pip_pre_hash = None
        if self.pip_path is not None:
            try:
                self.pip_pre_hash = _sha256_file(self.pip_path)
            except OSError:
                self.pip_pre_hash = None
        manifest = self.directory / "request.json"
        manifest.write_text(json.dumps({"token": self.token, "runsRoot": str(self.runs_root), "startedNs": self.started_ns,
                                        "pipModel": str(self.pip_path) if self.pip_path else None,
                                        "pipPreHash": self.pip_pre_hash}))
        manifest.chmod(0o600)
        self._sweep_stale()

    def environment(self):
        result = {RECEIPTS: str(self.directory), TOKEN: self.token}
        if self.pip_path is not None:
            result[PIP_MODEL_ENV] = str(self.pip_path)
            if self.pip_pre_hash:
                result[PIP_PRE_HASH_ENV] = self.pip_pre_hash
            if self.pip_backup_path:
                result[PIP_BACKUP_ENV] = self.pip_backup_path
        return result

    def _consumed_path(self, receipt_path):
        return receipt_path.with_name(receipt_path.name + ".consumed")

    def _is_consumed(self, receipt_path):
        return receipt_path.name in self._consumed or self._consumed_path(receipt_path).exists()

    def _mark_consumed(self, receipt_path, item):
        self._consumed.add(receipt_path.name)
        try:
            marker = self._consumed_path(receipt_path)
            marker.write_text(json.dumps({"token": item.get("token"), "runId": item.get("runId"),
                                          "revision": item.get("revision"),
                                          "consumedNs": time.time_ns()}))
            marker.chmod(0o600)
        except OSError:
            pass  # in-memory set still blocks replay within this turn object

    def cleanup(self):
        # Best-effort only, and only for a directory this turn actually owns:
        # a turn-* dir under the verification base. Never touches anything else.
        try:
            if self.directory.name.startswith("turn-") and self.directory.parent == self._base:
                shutil.rmtree(self.directory, ignore_errors=True)
        except OSError:
            pass

    def _sweep_stale(self):
        # Covers turns orphaned by a host restart/crash, where checked_stream's
        # finally never ran. Only dirs older than STALE_AFTER_SECS, never the
        # current turn and never a turn younger than a day (an active turn,
        # including a long Build, is well under 24h old).
        try:
            now = time.time()
            for entry in self._base.glob("turn-*"):
                try:
                    if entry == self.directory or not entry.is_dir():
                        continue
                    if now - entry.stat().st_mtime > self.STALE_AFTER_SECS:
                        shutil.rmtree(entry, ignore_errors=True)
                except OSError:
                    continue
        except OSError:
            pass

    def pip_backup(self):
        """Backup path for this Pip turn: explicit value, else a sidecar
        the responder/host writes to <turn>/pip.json as {"backup": path}
        after backing up pants.effigy and before editing it. The sidecar
        only names the backup; the hash check against pip_pre_hash is
        what makes it trustworthy, so a post-edit backup fails."""
        if self.pip_backup_path:
            return self.pip_backup_path
        try:
            sidecar = json.loads((self.directory / "pip.json").read_text())
            if isinstance(sidecar, dict) and sidecar.get("backup"):
                return sidecar["backup"]
        except (OSError, ValueError):
            pass
        return None

    def allow_run(self, run_id):
        """Explicitly name the one pre-existing run this turn may continue.

        Fresh more/rewind edits can continue an older run without this override.
        A build receipt for an older run still requires this explicit allowance.
        Written to the
        turn dir (0600, atomic) so record(), which runs in a separate
        process, sees the same allowance as receipt().
        """
        _write_json_atomic(self.directory / "allowed.json", {"runId": str(run_id)})
        return True

    def receipt(self):
        for path in sorted(self.directory.glob("*.receipt.json"), reverse=True):
            try:
                if path.stat().st_size > 65536:
                    continue
                if self._is_consumed(path):
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
                # Receipt binding (all checks above unchanged and still required).
                # A bound turn accepts only its own run, script and output;
                # an unbound build accepts only a run opened at/after turn start,
                # unless allow_run() explicitly named it. Fresh more/rewind edits
                # continue existing runs. Empty scriptHash /
                # output fields are treated as unknown, not as a mismatch, so
                # receipts written before this patch (or for runs with no
                # script file) are not rejected on a field they never had.
                allowed = _read_json(self.directory / "allowed.json")
                if allowed is not None and allowed.get("runId") != item.get("runId"):
                    continue
                binding = _read_json(self.directory / "binding.json")
                if binding is not None:
                    if binding.get("runId") != item.get("runId"):
                        continue
                    bound_hash = binding.get("scriptHash") or ""
                    if bound_hash and item.get("scriptHash") and item["scriptHash"] != bound_hash:
                        continue
                    bound_output = binding.get("output") or ""
                    if bound_output and item.get("output") and item["output"] != bound_output:
                        continue
                elif allowed is None and item.get("operation") not in ("more", "rewind"):
                    # A brand-new build must have opened during this turn, so an
                    # old run's leftover receipt cannot be claimed. more/rewind
                    # are the second message: they continue the run the first
                    # message already opened. The answer-time and revision checks
                    # above already prove this turn changed it.
                    opened = _run_opened_ns(root)
                    if opened is not None and opened < self.started_ns:
                        continue
                # Pip gate, receipt-time only (sub-task 2, opt-in): only
                # when pip_path is set. Failure skips this receipt, so
                # checked_stream retries, then fails the turn.
                if self.pip_path is not None:
                    ok, reason, post_hash = verify_pip_final(
                        self.pip_path, item.get("sha256"),
                        backup_path=self.pip_backup(), pre_hash=self.pip_pre_hash)
                    if not ok:
                        continue
                    item = dict(item)
                    item["pip"] = {"model": str(self.pip_path), "preHash": self.pip_pre_hash,
                                   "backup": self.pip_backup(), "postHash": post_hash}
                    try:
                        manifest_path = self.directory / "pip-manifest.json"
                        manifest_path.write_text(json.dumps(dict(item["pip"], turn=self.directory.name)) + "\n")
                        manifest_path.chmod(0o600)
                    except OSError:
                        pass
                self._mark_consumed(path, item)
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


def _verified_reply(reply, receipt):
    """Compare run/revision claims in text against the verified receipt.

    The receipt itself is already fully verified (token, run parent, revision,
    snapshot hash) before this runs, so a text mismatch is not a reason to
    reject a real build -- it is a reason not to let the text's claims pass
    silently. On a match (or no claim at all) the reply is returned unchanged.
    On a mismatch, claim lines are stripped and the gate supplies the verified
    run and revision, so the user sees gate truth, not responder claims.
    """
    revision = receipt.get("revision", 0)
    gate_line = "Verified build: %s, saved revision %s." % (Path(str(receipt.get("run", ""))).name, revision)
    if not reply:
        return gate_line
    rev_claims = [int(n) for n in re.findall(r"(?i)\b(?:revision|rev\.?)\s*[:#=]?\s*(\d+)", reply)]
    run_claims = re.findall(r"script-[A-Za-z0-9_.\-]+", reply)
    receipt_run = str(receipt.get("run", ""))
    run_mismatch = any(token not in receipt_run for token in run_claims)
    if (not rev_claims or all(n == revision for n in rev_claims)) and not run_mismatch:
        return reply
    kept = []
    for line in reply.splitlines():
        if re.search(r"(?i)\b(?:revision|rev\.?)\s*[:#=]?\s*\d+", line):
            continue
        if re.search(r"script-[A-Za-z0-9_.\-]+", line):
            continue
        kept.append(line)
    stripped = "\n".join(kept).strip()
    return (stripped + "\n\n" + gate_line) if stripped else gate_line


def _has_build_claim(reply):
    """True only if the reply claims a build was made. A greeting, question,
    or chat reply with no run/revision/effigy/built claim is not a build,
    so it must not fail the build gate — that is what blocked chat in
    Build mode (now the only mode, since the Plan toggle was removed).
    A reply that DOES claim a build still needs a verified receipt."""
    if not reply:
        return False
    return bool(re.search(r"(?i)\b(?:revision|rev\.?)\s*[:#=]?\s*\d+|script-[A-Za-z0-9_.\-]+|\.effigy\b|built\s+(?:your|a|the)\b|saved\s+(?:as|revision)\b", reply))

def checked_stream(model, history, invoke, cancel=None, turn_factory=BuildTurn, mode="build"):
    if mode != "build":
        yield from invoke(history)
        return
    if not capabilities(model)["build"]:
        raise ExecutionMissing("This connection has no execution tools. Plan is available; Build needs a tool-enabled adapter. No build was claimed or sent for manual execution.")
    turn = turn_factory()
    try:
        messages = [dict(row) for row in history]
        with activate(turn):
            # ONE ATTEMPT 2026-10-25 at user order: no retry, no second
            # model call for a failed build claim. If it cannot get a
            # verified receipt, it is out — the user re-sends if wanted.
            for attempt in range(1):
                if cancel is not None and cancel.is_set():
                    yield "Build cancelled."
                    return
                chunks = []
                for chunk in invoke(messages):
                    if cancel is not None and cancel.is_set():
                        yield "Build cancelled."
                        return
                    chunks.append(chunk)
                reply = "".join(chunks)
                if cancel is not None and cancel.is_set():
                    yield "Build cancelled."
                    return
                receipt = turn.receipt()
                if receipt:
                    yield _verified_reply(reply, receipt)
                    return
                if attempt == 0 and not _has_build_claim(reply):
                    # Chat, not a claimed build: deliver it. Do not retry,
                    # do not fail the turn for not producing a revision.
                    yield reply
                    return
                # No retry: a build claim with no receipt fails here, it is
                # not sent again as a corrective turn.
            raise turn.failure()
    finally:
        # Terminal outcome only (success, failure, or cancel): remove this
        # turn's verification dir, including any consumed receipt markers.
        # A generator abandoned mid-stream is not cleaned here; the 24h sweep
        # in BuildTurn.__init__ covers that case on the next turn.
        cleanup = getattr(turn, "cleanup", None)
        if callable(cleanup):
            cleanup()


def record(root, answer, operation, before_revision, supersede=False):
    """Called by geppetto-live after a successful real inbox answer, never by chat.

    supersede=True is the explicit escape hatch for abandoning the run this
    turn was first bound to and starting a different one: it replaces the
    binding (keeping the old binding under 'previous') instead of silently
    letting a second run's receipt pass alongside the first. It must be
    passed deliberately by the record caller; the default is to refuse a
    different runId for a bound turn.
    """
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
    # Bind this turn to its first run, script and output. All checks above
    # run first and are unchanged; a binding failure raises here and no
    # receipt is written, so a second run cannot silently add a receipt
    # that receipt() would later pick as first-passing.
    script_hash = _script_hash(root)
    output = _output_name(root)
    try:
        binding = _read_json(folder / "binding.json")
        allowed = _read_json(folder / "allowed.json")
    except ValueError:
        raise ExecutionMissing("Build binding file is corrupt; refusing receipt")
    if allowed is not None and allowed.get("runId") != state["runId"]:
        raise ExecutionMissing("Build run is not the explicitly allowed run for this turn")
    if binding is not None:
        if binding.get("runId") != state["runId"]:
            if not supersede:
                raise ExecutionMissing("Build turn already bound to another run; "
                                       "continue that run or supersede explicitly")
            old = {key: value for key, value in binding.items() if key != "previous"}
            previous = binding.get("previous") if isinstance(binding.get("previous"), list) else []
            binding = {"runId": state["runId"], "scriptHash": script_hash, "output": output,
                       "openedNs": _run_opened_ns(root), "createdNs": time.time_ns(),
                       "previous": previous + [old]}
            _write_json_atomic(folder / "binding.json", binding)
        elif binding.get("scriptHash") != script_hash or binding.get("output") != output:
            # Same run, script fixed via more/rewind: track the latest script
            # and output so a stale receipt for the old script cannot pass
            # once a new one exists. No receipt is valid yet at this point.
            binding = dict(binding)
            binding["scriptHash"] = script_hash
            binding["output"] = output
            _write_json_atomic(folder / "binding.json", binding)
    else:
        opened = _run_opened_ns(root)
        # more/rewind continue the run the previous message opened. Rejecting
        # that made the second message's tool fail after the model had already
        # changed, so the pane hung or the chat stopped. A new build still
        # cannot claim a run it did not open.
        if (allowed is None and operation not in ("more", "rewind")
                and opened is not None and opened < int(request.get("startedNs", 0))):
            raise ExecutionMissing("Build run opened before this turn; "
                                   "explicitly allow it or start a new run")
        _write_json_atomic(folder / "binding.json",
                           {"runId": state["runId"], "scriptHash": script_hash, "output": output,
                            "openedNs": opened, "createdNs": time.time_ns()})
    item = {"token": token, "finishedNs": time.time_ns(), "run": str(root), "runId": state["runId"],
            "operation": operation, "beforeRevision": int(before_revision), "revision": revision,
            "snapshot": snapshot["path"], "sha256": hashlib.sha256(Path(saved).read_bytes()).hexdigest(),
            "scriptHash": script_hash, "output": output}
    path = folder / ("%020d-%s.receipt.json" % (time.time_ns(), uuid.uuid4().hex))
    temp = path.with_suffix(".tmp")
    fd = os.open(temp, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w") as stream:
        json.dump(item, stream); stream.flush(); os.fsync(stream.fileno())
    os.replace(temp, path)


if __name__ == "__main__":
    import sys
    try:
        args = sys.argv[1:]
        supersede = False
        if args and args[-1] == "supersede":
            supersede = True
            args = args[:-1]
        record(*args, supersede=supersede)
    except (OSError, ValueError, TypeError, KeyError, ExecutionMissing) as error:
        print("Build execution receipt: " + str(error), file=sys.stderr)
        raise SystemExit(1)
