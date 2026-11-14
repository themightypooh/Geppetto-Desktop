#!/usr/bin/env python3
"""Geppetto accept gate — shared intent registry + preflight + token.

Built 2026-10-26 (files only, Geppetto closed; loads on next Geppetto
restart, live pane verification still owed — do NOT claim the gate is
live until a pane test proves it).

The gate, in one sentence: NO live model runs in a pane without a
user yes tied to the exact script hash, and geppetto-live is the
choke point that enforces it.

Flow:
  1. Worker reads the relevant Effigy skill/API tools, chooses actual
     features and materials for the subject, then finishes the script.
     Nothing after yes but the run. Simple primitives are fine where
     appropriate, but this preflight alone cannot judge artistic quality.
  2. Worker files an intent:  gate.py file --script build.txt
     --subject "exact subject, user words" --worker <id> [--pane N]
     [--session <session>] [--doc <doc>] [--out <outdir>]
     Preflight must pass or no intent is filed at all.
     Multiple agents may hold independent pending intents in distinct
     panes; there is NO global one-pending lock. All four panes
     occupied -> immediate refusal, never a queue.
  3. Host (host.py, tracker refresh) polls cabinet/confirm/*.json,
     shows the accept card with the subject, and sends the fixed
     geppetto-notify toast naming the ASSIGNED pane. It holds the pane:
     no run is allocated until the user answers.
  4. User yes -> host writes cabinet/confirm/<id>.token.json.
     User no, or 10 minutes with no answer -> intent dies, nothing
     built, nothing half-built.
  5. Worker/launcher runs: geppetto-live build.txt ... --gate-token <id>
     geppetto-live claims the token (claim --worker/--session proves
     worker/session ownership, not just intent-id possession), exactly
     one caller gets START, the others WAIT then ATTACH to the same
     registered run. Missing / mismatched / expired / used token =
     refuse, exit 2, no run, no headless fallback, no auto-launch.
  6. Steering inside the same run (more / skip / rewind / close /
     wait) needs NO new token: the user is already watching that
     run. A new subject, a new run, or a resume from a stopped-step
     record after close = a NEW intent + a NEW yes.

  Frozen contract: cabinet/gate-protocol.md (Lane A, v1, 2026-10-26).
  States: pending -> yes -> launched -> placed (terminal for the gate);
  terminal: cancelled, expired, failed, closed. Active states
  (pending/yes/launched) durably reserve their pane in the intent file
  itself, so a chest restart cannot silently double-book a pane: see
  reserve_pane() and the restart-reconciliation rule in gate-protocol.md.
  Deadlines: pending 600s, yes->claim 600s, claim->register 10s,
  register->first-frame 30s (registry hard deadline; the 5s figure in
  the handoff is a fixture acceptance target, measured separately).

Headless effigy.sh script output is test-only and can never be
placed in a pane; pane placement happens only through a confirmed
live run. Usage waste is NOT a reason to skip any of this.
"""
import argparse, fcntl, hashlib, json, os, re, subprocess, sys, time
from contextlib import contextmanager

HERE = os.path.dirname(os.path.abspath(__file__))
CONFIRM_DIR = os.path.join(HERE, "confirm")
AUDIT = os.path.join(CONFIRM_DIR, "audit.log.jsonl")
TIMEOUT_S = 600  # 10 minutes, per proposal
START_TIMEOUT_S = 600  # an old yes must not launch a new run hours later
REGISTER_TIMEOUT_S = 10  # claim START -> register, else failed (not WAIT forever)
FIRST_FRAME_TIMEOUT_S = 120  # register -> first frame, else failed (see protocol §8).
# 2026-11-14: was 30. A painted doc (squirrel v7) takes ~45 s to rebuild before the run
# publishes anything, so every heavy-model run blew this and was failed while building fine.
TERMINAL_STATES = frozenset(("placed", "cancelled", "expired", "failed", "closed"))
ACTIVE_STATES = frozenset(("pending", "yes", "launched"))
_ID_RE = re.compile(r"^gate-[0-9]+-[0-9a-f]{8}$")
_WORKER_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,119}$")
_SESSION_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.\-:]{0,199}$")


def is_safe_id(iid):
    """True only for the minted intent-id shape; anything else is rejected
    before any file is opened, so an id can never escape the registry dir."""
    return isinstance(iid, str) and bool(_ID_RE.match(iid))


def _validate_worker(worker):
    if not worker or not _WORKER_RE.match(worker):
        return "REFUSED: --worker is required and must match [A-Za-z0-9][A-Za-z0-9_.-]{0,119}"
    return ""


def _validate_session(session):
    if session and not _SESSION_RE.match(session):
        return "REFUSED: --session must match [A-Za-z0-9][A-Za-z0-9_.:-]{0,199}"
    return ""


def _pid_alive(pid):
    try:
        pid = int(pid)
    except (TypeError, ValueError):
        return False
    if pid <= 0:
        return False
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except OSError:
        return False
    return True

@contextmanager
def _registry_lock():
    _ensure()
    with open(os.path.join(CONFIRM_DIR, ".lock"), "a+b") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(lock, fcntl.LOCK_UN)

def _ensure():
    os.makedirs(CONFIRM_DIR, exist_ok=True)
    os.makedirs(os.path.join(CONFIRM_DIR, "stopped"), exist_ok=True)

def _atomic(path, obj):
    # Per-process temp name (a crashed writer must not leave a shared
    # ".tmp" that a later writer would silently overwrite mid-write),
    # flush + fsync, then atomic replace. Callers hold _registry_lock
    # for read-then-write sequences; _atomic alone is still atomic.
    tmp = "%s.tmp.%d" % (path, os.getpid())
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(obj, f, indent=2, sort_keys=True)
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, path)

def _audit(event, data):
    try:
        _ensure()
        rec = {"t": time.time(), "event": event}
        rec.update(data)
        with open(AUDIT, "a", encoding="utf-8") as f:
            f.write(json.dumps(rec, sort_keys=True) + "\n")
    except OSError:
        pass

def script_hash(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()

# ---------------- preflight ----------------
_KNOWN_VERBS = ("add ", "set ", "remove ", "save ", "echo ", "material ",
                "render ", "load ", "import ", "feature ", "slot ", "group ")

def preflight(text):
    """Return dict(ok, errors, first_geom_line, lines, ...)."""
    lines = [l.strip() for l in text.splitlines()]
    kept = [(i + 1, l) for i, l in enumerate(lines)
            if l and not l.startswith("#")]
    errs = []
    first_geom = None
    mat_before = 0
    box_rot = 0
    first_shape = ""
    has_save = False
    for lineno, l in kept:
        if l.startswith("save "):
            has_save = True
        if l.startswith("material "):
            if first_geom is None:
                mat_before += 1
        # The first visible shape need not be a Primitive: organic/curve
        # workflows often start with Profile, Spline or Blob. Do not make
        # the approval gate push every agent toward box/cylinder blockouts.
        feature = l.split(None, 2)[1] if l.startswith("add ") and len(l.split(None, 2)) > 1 else ""
        if feature in ("Primitive", "Profile", "Spline", "Blob", "Loft", "Sweep", "Sketch", "Import", "Garment"):
            if first_geom is None:
                first_geom = lineno
                m = re.search(r"shape=(\S+)", l)
                first_shape = m.group(1) if m else feature
        if feature == "Primitive":
            # Cylinder radius rule: width=/depth= on a Cylinder, or no radius=, is a FAIL
            if "shape=Cylinder" in l and ("width=" in l or "depth=" in l or "radius=" not in l):
                errs.append(f"line {lineno}: Cylinder with width=/depth= or missing radius=")
            if "shape=Box" in l and "rotation=" in l:
                box_rot += 1
            if "materialslot=" not in l:
                errs.append(f"line {lineno}: Primitive missing materialslot=")
    if first_geom is None:
        errs.append("no geometry (no supported geometry-producing add line)")
    if mat_before > 2:
        errs.append(f"{mat_before} material lines before first geometry (max 2)")
    if box_rot >= 16:
        errs.append(f"box-circle risk: {box_rot} rotated Box primitives")
    if not has_save:
        errs.append("no save line")
    return {"ok": not errs, "errors": errs, "first_geom_line": first_geom,
            "first_shape": first_shape, "lines": len(kept),
            "materials_before_first": mat_before, "rotated_boxes": box_rot}

# ---------------- intents ----------------
def _intent_path(iid):
    if not is_safe_id(iid):
        return None
    return os.path.join(CONFIRM_DIR, iid + ".json")

def _token_path(iid):
    if not is_safe_id(iid):
        return None
    return os.path.join(CONFIRM_DIR, iid + ".token.json")

def list_pending():
    _ensure()
    out = []
    for name in sorted(os.listdir(CONFIRM_DIR)):
        if not name.endswith(".json") or name.endswith(".token.json") or name.endswith(".answer.json"):
            continue
        try:
            with open(os.path.join(CONFIRM_DIR, name), encoding="utf-8") as f:
                d = json.load(f)
        except (OSError, ValueError):
            continue
        # Corrupt/mismatched files are never treated as intents.
        if isinstance(d, dict) and d.get("status") == "pending" \
                and is_safe_id(d.get("id")) and d.get("id") == name[:-len(".json")]:
            out.append(d)
    return out


def list_active():
    """All intents in an active (pane-reserving) state, plus placed ones
    for diagnosis. Malformed files are skipped, never trusted."""
    _ensure()
    out = []
    for name in sorted(os.listdir(CONFIRM_DIR)):
        if not name.endswith(".json") or name.endswith(".token.json") or name.endswith(".answer.json"):
            continue
        try:
            with open(os.path.join(CONFIRM_DIR, name), encoding="utf-8") as f:
                d = json.load(f)
        except (OSError, ValueError):
            continue
        if isinstance(d, dict) and d.get("status") in ACTIVE_STATES | {"placed"} \
                and is_safe_id(d.get("id")) and d.get("id") == name[:-len(".json")]:
            out.append(d)
    return out


def _active_panes():
    """pane -> intent id for active intents. Caller holds _registry_lock."""
    panes = {}
    for d in list_active():
        if d.get("status") in ACTIVE_STATES and str(d.get("pane", "")) in ("1", "2", "3", "4"):
            panes[str(d["pane"])] = d["id"]
    return panes


def active_panes():
    with _registry_lock():
        return _active_panes()


def _float_field(d, key):
    try:
        return float(d.get(key, 0))
    except (TypeError, ValueError):
        return 0.0


def _expire_pending_locked(now):
    """Mark stale pending intents expired. Caller holds the lock; returns
    the expired intents (host releases happen AFTER the lock, by caller)."""
    expired = []
    for d in list_pending():
        if now - _float_field(d, "created_at") > TIMEOUT_S:
            d["status"] = "expired"
            d["cancel_reason"] = "pending timeout (600s, no answer)"
            _atomic(_intent_path(d["id"]), d)
            _audit("intent_expired", {"id": d["id"], "subject": d.get("subject", "")})
            expired.append(d)
    return expired


def _expire_yes_locked(now):
    """Mark yes intents whose claim window lapsed as expired. A yes that
    was already claimed (token used_at set) is not expired here; its
    register/first-frame deadlines are handled by _deadlines_locked."""
    expired = []
    for d in list_active():
        if d.get("status") != "yes":
            continue
        tok = _load_token(d["id"])
        if tok and tok.get("used_at"):
            continue
        if tok and now - _float_field(tok, "yes_at") > START_TIMEOUT_S:
            d["status"] = "expired"
            d["cancel_reason"] = "yes timeout (600s, no claim)"
            _atomic(_intent_path(d["id"]), d)
            _audit("intent_expired", {"id": d["id"], "subject": d.get("subject", "")})
            expired.append(d)
    return expired


def _deadlines_locked(now):
    """Mark deadline-missed intents failed. Caller holds the lock; returns
    the failed intents. This marks and releases only — it does NOT kill
    any process (the launcher/host must terminate the registered pid
    when they observe the failure; see gate-protocol.md §8).
    Covered here, in addition to the time deadlines: a claim START that
    never registered (status yes OR launched, token used, no run), and a
    launched run whose registered pid is dead before any first frame.
    A dead pid is definitive (a reused pid would read alive); a live
    pid is only advisory, never proof the original run still lives."""
    failed = []
    for d in list_active():
        if d.get("status") not in ("yes", "launched"):
            continue
        tok = _load_token(d["id"])
        if not tok:
            continue
        reason = ""
        if tok.get("used_at") and not tok.get("run"):
            if now - _float_field(tok, "used_at") > REGISTER_TIMEOUT_S:
                reason = "register timeout (10s, no run registered)"
        elif tok.get("run") and not d.get("first_frame_at"):
            if tok.get("pid") and not _pid_alive(tok["pid"]):
                reason = "registered pid is dead before any first frame"
            elif tok.get("registered_at") and now - _float_field(tok, "registered_at") > FIRST_FRAME_TIMEOUT_S:
                reason = "first-frame timeout (30s, no first frame recorded)"
        if reason:
            d["status"] = "failed"
            d["cancel_reason"] = reason
            _atomic(_intent_path(d["id"]), d)
            _audit("intent_failed", {"id": d["id"], "reason": reason})
            failed.append(d)
    return failed


def _release_all(intents):
    """Host pane releases, always OUTSIDE the registry lock."""
    for d in intents:
        if d.get("id"):
            release_pane(d["id"])


def expire_pending():
    """Mark intents older than TIMEOUT_S as expired. Returns list expired.
    Also expires stale yes approvals (kept under the old name for host
    callers; see _expire_yes_locked)."""
    with _registry_lock():
        expired = _expire_pending_locked(time.time())
        expired += _expire_yes_locked(time.time())
    _release_all(expired)
    return expired


def check_deadlines():
    """Run deadline checks now; returns intents newly marked failed."""
    with _registry_lock():
        failed = _deadlines_locked(time.time())
    _release_all(failed)
    return failed


def _expire_pending():
    # Back-compat helper for any in-process caller that held the lock:
    # it assumes the caller already holds _registry_lock.
    return _expire_pending_locked(time.time())


def file_intent(script_path, subject, worker, pane="", doc="-", out="", session=""):
    return _file_intent(script_path, subject, worker, pane, doc, out, session)


def _file_intent(script_path, subject, worker, pane="", doc="-", out="", session=""):
    # Phase 1, under lock: expire stale intents, validate, preflight.
    # No host call happens under the lock (see gate-protocol.md §6).
    expired = []
    with _registry_lock():
        _ensure()
        expired = _expire_pending_locked(time.time())
        expired += _expire_yes_locked(time.time())
        err = _validate_worker(worker)
        if not err:
            err = _validate_session(session)
        if not err and (not subject or not subject.strip() or any(ord(c) < 32 for c in subject)):
            err = "REFUSED: subject is empty or contains control characters — file the user's exact subject"
        if not err:
            try:
                requested = int(pane) if str(pane) not in ("", None) else 1
            except (TypeError, ValueError):
                requested = 0
            if requested not in (1, 2, 3, 4):
                err = "REFUSED: --pane must be 1-4 (or omitted for 1)"
        # Early failures are recorded, not returned, so the expired
        # releases below still happen. (Returning inside the with block
        # would skip them and leave dead host slots occupied.)
        if not err:
            subject = subject.strip()[:120]
            try:
                with open(script_path, encoding="utf-8") as f:
                    text = f.read()
            except OSError as e:
                err = f"REFUSED: cannot read script: {e}"
                text = ""
            except ValueError as e:
                err = f"REFUSED: script is not valid UTF-8 text: {e}"
                text = ""
            if not err and len(text) > 1_000_000:
                err = "REFUSED: script is larger than 1,000,000 characters"
            # Narrow guard: only refuse a script that IS a registry file
            # (.json inside the registry dir). A .txt script that happens
            # to sit in the same directory as a test registry (as in
            # test_join_gate_poll.py, which points CONFIRM_DIR at its
            # temp root) is legitimate and must not be refused.
            if not err and os.path.abspath(script_path).startswith(
                    os.path.abspath(CONFIRM_DIR) + os.sep) \
                    and os.path.basename(script_path).endswith(".json"):
                err = "REFUSED: script must not be a registry (.json) file"
            if not err:
                pf = preflight(text)
                if not pf["ok"]:
                    err = "PREFLIGHT FAIL: " + "; ".join(pf["errors"])
            if not err:
                iid = "gate-%d-%s" % (int(time.time()), hashlib.sha256(
                    (subject + script_hash(text) + str(time.time_ns())).encode()).hexdigest()[:8])
        else:
            text, pf, iid = "", None, ""
    # Expired intents' host slots are released here, outside the lock and
    # before the new reservation attempt, so their panes are actually free.
    _release_all(expired)
    if err:
        return None, err
    # Phase 2, OUTSIDE the lock: host reservation (a slow socket/subprocess call).
    assigned, reason = reserve_pane(iid, requested)
    if not assigned:
        return None, reason
    # Phase 3, under lock again: re-check durable occupancy before writing.
    # If the host restarted between phase 2 and now, another filer may
    # already own this pane durably; refusing here is the fail-closed
    # restart case, not a retry loop (protocol §6).
    with _registry_lock():
        active = _active_panes()
        if str(assigned) in active and active[str(assigned)] != iid:
            conflict = active[str(assigned)]
            # No intent file is written at all: this filing never existed.
            # The host slot is released after the lock, below.
            release_needed = True
        else:
            intent = {"id": iid, "status": "pending", "subject": subject,
                      "script": os.path.abspath(script_path),
                      "doc": "-" if doc in ("", "-") else os.path.abspath(doc),
                      "out": os.path.abspath(out or os.path.dirname(os.path.abspath(script_path))),
                      "script_hash": script_hash(text),
                      "preflight": pf, "worker": worker, "session": session or "",
                      "pane": str(assigned), "created_at": time.time()}
            _atomic(_intent_path(iid), intent)
            _audit("intent_filed", {"id": iid, "subject": subject, "worker": worker,
                                    "pane": str(assigned),
                                    "first_geom_line": pf["first_geom_line"],
                                    "lines": pf["lines"]})
            return intent, ""
    if release_needed:
        release_pane(iid)
    return None, ("REFUSED: viewport %s is already reserved by intent %s in the "
                  "durable registry (chest restart has not rehydrated "
                  "reservations) — do not delete that intent; rehydrate the "
                  "host from gate.py reservations first" % (assigned, conflict))

def _host_command(*args):
    try:
        result = subprocess.run([sys.executable, os.path.join(HERE, "host.py"), *args],
                                capture_output=True, text=True, timeout=2)
    except (OSError, subprocess.TimeoutExpired):
        return ""
    return result.stdout.strip() if result.returncode == 0 else ""

def reserve_pane(iid, requested=""):
    """Ask the host for a pane, then check the DURABLE registry (outside
    any host call, and re-checked under the lock by the caller before
    writing). If the host assigned a pane that a surviving active intent
    already owns durably — the chest-restart case — release the host slot
    and refuse explicitly instead of double-booking. Callers must NOT
    hold _registry_lock across this function (it makes a host call)."""
    try:
        requested_n = int(requested) if str(requested) not in ("", None) else 1
    except (TypeError, ValueError):
        requested_n = 0
    if requested_n not in (1, 2, 3, 4):
        return 0, "REFUSED: --pane must be 1-4 (or omitted for 1)"
    reply = _host_command("gate-reserve", iid, str(requested_n))
    if reply in ("1", "2", "3", "4"):
        pane = int(reply)
        with _registry_lock():
            active = _active_panes()
        if str(pane) in active and active[str(pane)] != iid:
            release_pane(iid)
            return 0, ("REFUSED: viewport %d is already reserved by intent %s in the "
                       "durable registry (chest restart has not rehydrated "
                       "reservations) — do not delete that intent; rehydrate "
                       "the host from gate.py reservations first"
                       % (pane, active[str(pane)]))
        return pane, ""
    return 0, ("REFUSED: all four viewports are occupied" if reply == "FULL" else
               "REFUSED: Geppetto is closed or cannot reserve a viewport — no worker should wait")

def release_pane(iid):
    if not is_safe_id(iid):
        return
    _host_command("gate-release", iid)

def cancel_waiters():
    """Chest shutdown: no unanswered or unused approval survives a closed app.
    - pending -> cancelled
    - yes, never claimed, or claimed but never registered -> cancelled
      (a claim START with no run is not a run; there is nothing to stop)
    - yes with a registered run, or launched -> closed (their runs are
      stopped separately by the host/launcher)
    The record itself is what remote workers poll for. Intent files are
    never deleted, so the cancellation is observable. Host pane slots for
    the cancelled/closed intents are released AFTER the lock (best-effort:
    if the host is already gone, release_pane fails silently and the
    slots die with it anyway). Returns the cancelled/closed intents.
    This closes Lane F cases A/B/C/E against the old code: used tokens
    no longer survive, a claimed-but-unregistered token no longer
    returns WAIT forever, and cancelled intents no longer leak a host
    slot when the host is still answering."""
    cancelled = []
    with _registry_lock():
        for d in list_active():
            status = d.get("status")
            if status == "pending":
                d["status"] = "cancelled"
                d["cancel_reason"] = "chest closed"
            elif status == "launched":
                d["status"] = "closed"
                d["cancel_reason"] = "chest closed"
            elif status == "yes":
                tok = _load_token(d["id"])
                if tok and tok.get("run"):
                    d["status"] = "closed"
                    d["cancel_reason"] = "chest closed"
                else:
                    d["status"] = "cancelled"
                    d["cancel_reason"] = "chest closed"
            else:
                continue
            _atomic(_intent_path(d["id"]), d)
            _audit("chest_closed", {"id": d["id"], "was": status})
            cancelled.append(d)
    _release_all(cancelled)
    return cancelled

def load_intent(iid):
    path = _intent_path(iid)
    if path is None:
        return None
    try:
        with open(path, encoding="utf-8") as f:
            d = json.load(f)
    except (OSError, ValueError):
        return None
    if not isinstance(d, dict) or d.get("id") != iid:
        return None
    return d

def answer_intent(iid, answer, launch=False):
    """Host calls this on yes/no. Yes writes the token; no kills the intent."""
    if not is_safe_id(iid):
        return None, "invalid intent id"
    with _registry_lock():
        result, error = _answer_intent(iid, answer)
    if result and launch:
        error = launch_intent(iid)
        if error:
            with _registry_lock():
                d = load_intent(iid)
                if d and d.get("status") == "yes" and not (_load_token(iid) or {}).get("used_at"):
                    d["status"] = "cancelled"
                    d["cancel_reason"] = "launch dispatch failed: " + error
                    _atomic(_intent_path(iid), d)
    if answer != "yes" or error:
        release_pane(iid)
    return result, error

def _answer_intent(iid, answer):
    _ensure()
    d = load_intent(iid)
    if not d or d.get("status") != "pending":
        return None, "no pending intent " + iid
    try:
        created = float(d.get("created_at", 0))
    except (TypeError, ValueError):
        created = 0
    if time.time() - created > TIMEOUT_S:
        d["status"] = "expired"
        d["cancel_reason"] = "pending timeout (600s, no answer)"
        _atomic(_intent_path(iid), d)
        _audit("intent_expired", {"id": iid})
        return None, "intent expired (10 min, no answer)"
    if answer == "yes":
        try:
            with open(d["script"], encoding="utf-8") as script:
                current_hash = script_hash(script.read())
        except OSError:
            current_hash = ""
        if current_hash != d["script_hash"]:
            return None, "script changed before Yes — file a fresh intent"
        token = {"id": iid, "script": d["script"], "script_hash": d["script_hash"],
                 "subject": d["subject"], "worker": d.get("worker", ""),
                 "session": d.get("session", ""),
                 "yes_at": time.time(), "used_at": None}
        _atomic(_token_path(iid), token)
        d["status"] = "yes"
        _atomic(_intent_path(iid), d)
        _audit("intent_yes", {"id": iid, "subject": d["subject"]})
        return token, ""
    d["status"] = "cancelled"
    d["cancel_reason"] = "user answered no"
    _atomic(_intent_path(iid), d)
    _audit("intent_no", {"id": iid, "subject": d["subject"]})
    return None, "cancelled: not modeled"

def launch_intent(iid):
    """Approval owns the launch. The worker's later call attaches to this run.

    Worker/session for the launcher's claim (gate-protocol.md §5.6, frozen):
    launch_intent reads worker/session from the INTENT FILE (the only
    authoritative source for the host launcher) and passes them to the
    launcher as explicit flags --gate-worker/--gate-session. The launcher
    (Lane B) must forward those flags verbatim to
    `gate.py claim --worker/--session`; it must NOT derive claim
    identity from GEPPETTO_MODELLER (a display label, also set from
    Codex/Claude session env on other paths), from its own process env,
    or by parsing the intent file itself in shell. If the intent has no
    valid worker (a legacy pre-v1 intent), dispatch is refused here and
    answer_intent cancels the intent — fail closed, do not launch with
    an invented worker."""
    d = load_intent(iid)
    if not d or d.get("status") != "yes":
        return "no approved intent to launch"
    err = _validate_worker(d.get("worker", ""))
    if not err:
        err = _validate_session(d.get("session", ""))
    if err:
        return "launch refused: intent has no valid worker/session for claim: " + err
    pane = str(d.get("pane"))
    if __name__ == "geppetto_gate":
        # In-process Yes (pane card A): host.py _confirm_write_answer
        # loads this file as "geppetto_gate" and calls answer_intent in
        # the host UI thread. That thread is blocked here, so it cannot
        # answer its own gate-check socket request: the subprocess client
        # times out at 2 s, returns "", and every pane-card Yes was
        # cancelled as "reserved viewport is gone" (2026-10-26 05:03 and
        # 05:10). Do NOT make a socket round-trip on this path. Verify
        # the reservation from the durable registry instead -- the same
        # source host.rehydrate_gate_slots() trusts at startup -- and
        # require that no OTHER active intent holds this pane. The
        # launcher's gate-place step still enforces the live host slot
        # before any run is shown, so this does not weaken placement.
        with _registry_lock():
            holder = _active_panes().get(pane)
        if holder != iid:
            return "reserved viewport is gone or Geppetto closed — no run started"
    elif _host_command("gate-check", iid) != pane:
        # Outside-process Yes (gate.py CLI, desktop card B): the host
        # event loop is free, so the socket check works and must stay:
        # it is what refuses a Yes answered while Geppetto is closed.
        return "reserved viewport is gone or Geppetto closed — no run started"
    script = d["script"]
    out = d.get("out") or os.path.dirname(script)
    wrapper = os.path.expanduser("~/.local/bin/geppetto-live")
    try:
        # Never pass the host's provider credentials into a modelling script.
        env = {k: v for k, v in os.environ.items()
               if not (k.endswith("_API_KEY") or k in ("GEPPETTO_BUILD_TOKEN", "GEPPETTO_BUILD_RECEIPTS"))}
        env["GEPPETTO_MODELLER"] = d.get("worker") or "agent"
        log = open(os.path.join(CONFIRM_DIR, iid + ".launch.log"), "ab")
        try:
            subprocess.Popen([wrapper, script, d.get("doc") or "-", out,
                              "--gate-token", iid, "--gate-launch",
                              "--gate-worker", d["worker"],
                              "--gate-session", d.get("session", "")],
                             cwd=os.path.dirname(script), env=env,
                             stdin=subprocess.DEVNULL, stdout=log, stderr=log,
                             start_new_session=True)
        finally:
            log.close()
    except OSError as exc:
        _audit("launch_failed", {"id": iid, "error": str(exc)})
        return "approved but launch failed: " + str(exc)
    _audit("launch_dispatched", {"id": iid})
    return ""

def claim_token(iid, script_path, worker="", session=""):
    """Exactly one caller starts; all others attach to its recorded run.
    Ownership (protocol §5): the claim worker must equal the intent
    worker, and the claim session must match the filed intent session
    exactly (an empty claim session never matches a filed one). When
    the intent filed no session, the first non-empty claim binds its
    session, after which a claim must carry the filed (empty) session
    or the bound one. A foreign worker/session
    gets ERROR and learns no run directory. If a claim START was taken
    but no run was registered within REGISTER_TIMEOUT_S, the intent is
    marked failed (pane released after the lock) and later claims get
    ERROR instead of waiting forever."""
    if not is_safe_id(iid):
        return "ERROR", "invalid intent id"
    release = False
    result = ("ERROR", "internal error")
    with _registry_lock():
        tok = _load_token(iid)
        error = _token_error(tok, iid, script_path, worker, session)
        if error:
            # Stale yes / stale registration become terminal here so a
            # later caller does not keep retrying a dead approval.
            d = load_intent(iid)
            if d and d.get("status") in ("yes", "launched") and (
                    "expired" in error or "not registered" in error):
                d["status"] = "expired" if "approval expired" in error else "failed"
                d["cancel_reason"] = error
                _atomic(_intent_path(iid), d)
                release = True
            result = ("ERROR", error)
        elif tok.get("used_at"):
            if tok.get("run"):
                # Never ATTACH to a run whose process is already dead and
                # which never showed a first frame: that run cannot be
                # placed, and attaching would let a worker mint a receipt
                # for a dead run (Lane F receipt audit).
                d = load_intent(iid)
                if tok.get("pid") and not _pid_alive(tok["pid"]) and d and not d.get("first_frame_at"):
                    d["status"] = "failed"
                    d["cancel_reason"] = "registered pid is dead before any first frame"
                    _atomic(_intent_path(iid), d)
                    release = True
                    result = ("ERROR", "registered run's pid is dead; approval void, file a fresh intent")
                else:
                    result = ("ATTACH", tok["run"])
            elif time.time() - _float_field(tok, "used_at") > REGISTER_TIMEOUT_S:
                d = load_intent(iid)
                if d and d.get("status") in ("yes", "launched"):
                    d["status"] = "failed"
                    d["cancel_reason"] = "register timeout (10s, no run registered)"
                    _atomic(_intent_path(iid), d)
                    release = True
                result = ("ERROR", "approved run was not registered within 10 seconds; approval void, file a fresh intent")
            else:
                result = ("WAIT", "")
        else:
            # Bind a session on first non-empty claim when the intent filed none.
            if session and not tok.get("bound_session") and not tok.get("session"):
                tok["bound_session"] = session
            tok["used_at"] = time.time()
            _atomic(_token_path(iid), tok)
            _audit("run_start", {"id": iid, "subject": tok.get("subject", ""), "script_hash": tok["script_hash"],
                                 "worker": worker, "session": session})
            result = ("START", "")
    if release:
        # Host release happens OUTSIDE the registry lock, always.
        release_pane(iid)
    return result


def register_run(iid, run, pid):
    """Bind a claimed token to exactly one run/pid, immutably. The intent
    moves yes -> launched. A dead pid is refused and fails the intent
    (PID death before register is a failure, not a retryable state)."""
    if not is_safe_id(iid):
        return False
    failed = False
    with _registry_lock():
        tok = _load_token(iid)
        d = load_intent(iid)
        if not isinstance(tok, dict) or not tok.get("used_at") or tok.get("run"):
            return False
        if not d or d.get("status") not in ("yes", "launched"):
            return False
        try:
            pid_n = int(pid)
        except (TypeError, ValueError):
            return False
        if not _pid_alive(pid_n):
            d["status"] = "failed"
            d["cancel_reason"] = "register refused: launcher pid already dead"
            _atomic(_intent_path(iid), d)
            _audit("register_refused_dead_pid", {"id": iid, "pid": pid_n})
            failed = True
        else:
            tok.update(run=os.path.abspath(run), pid=pid_n, registered_at=time.time())
            _atomic(_token_path(iid), tok)
            d["status"] = "launched"
            _atomic(_intent_path(iid), d)
            _audit("run_registered", {"id": iid, "run": tok["run"], "pid": pid_n})
            return True
    if failed:
        release_pane(iid)
    return False


def _load_token(iid):
    path = _token_path(iid)
    if path is None:
        return None
    try:
        with open(path, encoding="utf-8") as f:
            d = json.load(f)
    except (OSError, ValueError):
        return None
    if not isinstance(d, dict) or d.get("id") != iid:
        return None
    return d


def _token_error(tok, iid, script_path, worker="", session=""):
    d = load_intent(iid)
    if not isinstance(tok, dict) or tok.get("id") != iid or not d \
            or d.get("status") not in ("yes", "launched"):
        return "NOT CONFIRMED: no valid yes for this intent"
    if tok.get("used_at") is None and d.get("status") == "launched":
        return "NOT CONFIRMED: inconsistent intent (launched without a claim)"
    # Worker/session ownership (protocol §5). Empty worker from a
    # legacy in-process caller is not a match failure here; the CLI
    # requires --worker, and lanes B/E must pass both. A supplied worker
    # that disagrees is a hard failure. Session is stricter: a filed
    # session must match exactly — an empty claim session never matches
    # a filed one. When the intent filed no session, the first
    # non-empty claim binds its session (see claim_token); after
    # binding, a claim must carry the filed (empty) session or the
    # bound one, so the empty-session launcher can still attach.
    if worker and tok.get("worker") and worker != tok["worker"]:
        return "NOT CONFIRMED: intent belongs to another worker"
    if worker and d.get("worker") and worker != d["worker"]:
        return "NOT CONFIRMED: intent belongs to another worker"
    filed = d.get("session") or tok.get("session") or ""
    bound = tok.get("bound_session") or ""
    if filed:
        if session != filed:
            return "NOT CONFIRMED: intent belongs to another session"
    elif bound:
        if session and session != bound:
            return "NOT CONFIRMED: intent belongs to another session"
    try:
        with open(script_path, encoding="utf-8") as f:
            text = f.read()
    except OSError:
        return "NOT CONFIRMED: script cannot be read"
    if tok.get("script_hash") != script_hash(text):
        return "NOT CONFIRMED: script changed after Yes"
    try:
        approved = float(tok.get("yes_at") or 0)
    except (TypeError, ValueError):
        approved = 0
    if (not tok.get("used_at") and time.time() - approved > START_TIMEOUT_S) or approved > time.time() + 5:
        return "NOT CONFIRMED: approval expired"
    if os.path.abspath(script_path) != tok.get("script"):
        return "NOT CONFIRMED: this approval belongs to a different script path"
    return ""


def _validate_placement_locked(iid, run):
    """Validate_placement core. Caller holds _registry_lock (this split
    exists because record_placement already holds the lock, and flock is
    NOT re-entrant: calling the locking wrapper from inside a held lock
    deadlocks the process — that was the 2026-10-26 test hang)."""
    d = load_intent(iid)
    tok = _load_token(iid)
    if not d or not tok:
        return False, "no such intent or token"
    if d.get("status") != "launched":
        return False, "intent is not launched (status %s)" % d.get("status")
    if not tok.get("run") or os.path.abspath(run) != tok["run"]:
        return False, "run does not match the registered run for this intent"
    if tok.get("pid") and not _pid_alive(tok["pid"]) and not d.get("first_frame_at"):
        return False, "registered pid is dead before any first frame"
    if _float_field(tok, "registered_at") and \
            time.time() - _float_field(tok, "registered_at") > FIRST_FRAME_TIMEOUT_S \
            and not d.get("first_frame_at"):
        return False, "first-frame deadline already missed for this intent"
    return True, ""


def validate_placement(iid, run):
    """Read-only check Lane C's gate-place must run BEFORE showRunIn:
    the run must equal the token's registered run, the intent must be
    launched, and the pane must still be the intent's assigned pane.
    Returns (True, '') or (False, reason). No state is changed."""
    if not is_safe_id(iid):
        return False, "invalid intent id"
    with _registry_lock():
        return _validate_placement_locked(iid, run)


def record_placement(iid, run):
    """Commit a validated placement: intent -> placed, reservation freed.
    Lane C calls this only AFTER a successful showRunIn. Returns True
    on success; False leaves the intent unchanged (caller must free the
    reservation instead of claiming placement)."""
    if not is_safe_id(iid):
        return False
    with _registry_lock():
        ok, _reason = _validate_placement_locked(iid, run)
        if not ok:
            return False
        d = load_intent(iid)
        d["status"] = "placed"
        d["placed_at"] = time.time()
        _atomic(_intent_path(iid), d)
        _audit("intent_placed", {"id": iid, "run": os.path.abspath(run), "pane": d.get("pane")})
    release_pane(iid)
    return True


def record_first_frame(iid, run):
    """Host-reported first frame (Lane C). Sets first_frame_at and moves
    the intent to placed if it was launched. Returns True on success."""
    if not is_safe_id(iid):
        return False
    with _registry_lock():
        d = load_intent(iid)
        tok = _load_token(iid)
        if not d or not tok or tok.get("run") != os.path.abspath(run):
            return False
        if d.get("status") not in ("launched", "placed"):
            return False
        if not d.get("first_frame_at"):
            d["first_frame_at"] = time.time()
        d["status"] = "placed"
        if not d.get("placed_at"):
            d["placed_at"] = d["first_frame_at"]
        _atomic(_intent_path(iid), d)
        _audit("first_frame", {"id": iid, "run": tok["run"], "pane": d.get("pane")})
    release_pane(iid)
    return True


def cancel_intent(iid, reason="cancelled by worker"):
    """Explicit cancellation for any active intent. Terminal intents are
    reported (not resurrected) and still return their record. Returns
    (intent_or_None, error_string); error is non-empty only when the id
    is invalid or no such intent exists."""
    if not is_safe_id(iid):
        return None, "invalid intent id"
    with _registry_lock():
        d = load_intent(iid)
        if not d:
            return None, "no such intent " + iid
        if d.get("status") in TERMINAL_STATES:
            return d, ""
        d["status"] = "cancelled"
        d["cancel_reason"] = reason
        _atomic(_intent_path(iid), d)
        _audit("intent_cancelled", {"id": iid, "reason": reason})
    release_pane(iid)
    return d, ""


def intent_status(iid):
    """Merged intent + token + deadlines for workers polling this intent.
    Returns None for an invalid id, a missing intent, or a corrupt file."""
    if not is_safe_id(iid):
        return None
    d = load_intent(iid)
    if not d:
        return None
    tok = _load_token(iid)
    now = time.time()
    deadlines = {
        "pending_expires_at": (_float_field(d, "created_at") + TIMEOUT_S) if d.get("status") == "pending" else None,
        "start_expires_at": (_float_field(tok, "yes_at") + START_TIMEOUT_S) if tok and d.get("status") == "yes" else None,
        "register_deadline_at": (_float_field(tok, "used_at") + REGISTER_TIMEOUT_S) if tok and tok.get("used_at") and not tok.get("run") else None,
        "first_frame_deadline_at": (_float_field(tok, "registered_at") + FIRST_FRAME_TIMEOUT_S) if tok and tok.get("registered_at") and not d.get("first_frame_at") else None,
    }
    out = dict(d)
    out["token"] = tok
    out["deadlines"] = deadlines
    out["pid_alive"] = _pid_alive(tok["pid"]) if tok and tok.get("pid") else None
    return out


def reservations_list():
    """Active reservations for host startup rehydration (Lane C), plus
    placed intents flagged reserving=false for diagnosis."""
    out = []
    for d in list_active():
        tok = _load_token(d["id"])
        out.append({
            "id": d["id"], "pane": d.get("pane"), "status": d.get("status"),
            "subject": d.get("subject", ""), "worker": d.get("worker", ""),
            "session": d.get("session", ""),
            "run": tok.get("run") if tok else None,
            "pid": tok.get("pid") if tok else None,
            "registered_at": tok.get("registered_at") if tok else None,
            "first_frame_at": d.get("first_frame_at"),
            "pid_alive": _pid_alive(tok["pid"]) if tok and tok.get("pid") else None,
            "reserving": d.get("status") in ACTIVE_STATES,
        })
    return out

def verify_token(iid, script_path, worker="", session=""):
    """Check + consume a yes token for an initial geppetto-live run.
    LEGACY path, deprecated for new launches (protocol §7a): it consumes
    a token without the claim/attach protocol or a registered-run binding.
    Lanes B/E must use claim + register instead. If worker/session are
    supplied here, they are enforced the same way as in claim."""
    with _registry_lock():
        return _verify_token(iid, script_path, worker, session)

def _verify_token(iid, script_path, worker="", session=""):
    _ensure()
    if not is_safe_id(iid):
        return False, "NOT CONFIRMED: invalid intent id"
    try:
        with open(script_path, encoding="utf-8") as f:
            text = f.read()
    except OSError as e:
        return False, f"NOT CONFIRMED: cannot read script: {e}"
    tok = _load_token(iid)
    if not tok:
        return False, ("NOT CONFIRMED: no yes token for " + iid +
                       " — file an intent, wait for the user yes, then retry")
    if not isinstance(tok, dict) or tok.get("id") != iid or (load_intent(iid) or {}).get("status") != "yes":
        return False, "NOT CONFIRMED: approval is not valid for this intent"
    if worker and tok.get("worker") and worker != tok["worker"]:
        return False, "NOT CONFIRMED: intent belongs to another worker"
    filed = (load_intent(iid) or {}).get("session") or tok.get("session") or ""
    bound = tok.get("bound_session") or ""
    if filed:
        if session != filed:
            return False, "NOT CONFIRMED: intent belongs to another session"
    elif bound:
        if session and session != bound:
            return False, "NOT CONFIRMED: intent belongs to another session"
    try:
        approved = float(tok.get("yes_at") or 0)
    except (TypeError, ValueError):
        approved = 0
    if time.time() - approved > START_TIMEOUT_S or approved > time.time() + 5:
        return False, "NOT CONFIRMED: approval is too old — file a new intent and ask again"
    if tok.get("used_at"):
        return False, "NOT CONFIRMED: token already used — a new run needs a new intent + new yes"
    if tok.get("script_hash") != script_hash(text):
        return False, ("NOT CONFIRMED: script changed after yes (hash mismatch) — "
                       "never run a changed script silently; file a new intent + get a new yes")
    if tok.get("script") and os.path.abspath(script_path) != tok["script"]:
        # Same hash, different path: allowed only if hash matches — hash is the binding.
        pass
    tok["used_at"] = time.time()
    _atomic(_token_path(iid), tok)
    _audit("run_start", {"id": iid, "subject": tok.get("subject", ""),
                         "script_hash": tok["script_hash"]})
    return True, ""

def write_stopped_step(run_dir, revision, script_path, notes="", snapshot=""):
    """Stopped-step record: the resume point after stop / hold / close.
    `snapshot` may name the kept .effigy of the stopped revision directly;
    when empty, the newest .effigy under the run dir is used (best-effort)."""
    _ensure()
    snap = snapshot or ""
    if not snap:
        try:
            # Best-effort: newest .effigy under the run dir is the saved snapshot.
            cands = []
            for root, _dirs, files in os.walk(run_dir):
                for fn in files:
                    if fn.endswith(".effigy"):
                        p = os.path.join(root, fn)
                        cands.append((os.path.getmtime(p), p))
            if cands:
                snap = sorted(cands)[-1][1]
        except OSError:
            pass
    rec = {"run_dir": run_dir, "revision": revision, "script": script_path,
           "snapshot": snap, "notes": notes, "at": time.time()}
    name = re.sub(r"[^A-Za-z0-9_.-]+", "_", os.path.basename(run_dir.rstrip("/"))) or "run"
    path = os.path.join(CONFIRM_DIR, "stopped", name + ".json")
    _atomic(path, rec)
    _audit("stopped_step", {"run_dir": run_dir, "revision": revision})
    return rec

def main(argv=None):
    ap = argparse.ArgumentParser(description="Geppetto accept gate")
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("preflight"); p.add_argument("--script", required=True)
    f = sub.add_parser("file"); f.add_argument("--script", required=True)
    f.add_argument("--subject", required=True); f.add_argument("--worker", required=True)
    f.add_argument("--pane", default="")
    f.add_argument("--session", default="")
    f.add_argument("--doc", default="-"); f.add_argument("--out", default="")
    a = sub.add_parser("answer"); a.add_argument("--id", required=True)
    a.add_argument("--answer", required=True, choices=["yes", "no"])
    a.add_argument("--launch", action="store_true")
    v = sub.add_parser("verify"); v.add_argument("--id", required=True)
    v.add_argument("--script", required=True)
    v.add_argument("--worker", required=True); v.add_argument("--session", default="")
    c = sub.add_parser("claim"); c.add_argument("--id", required=True); c.add_argument("--script", required=True)
    c.add_argument("--worker", required=True); c.add_argument("--session", default="")
    r = sub.add_parser("register"); r.add_argument("--id", required=True)
    r.add_argument("--run", required=True); r.add_argument("--pid", required=True, type=int)
    pl = sub.add_parser("place"); pl.add_argument("--id", required=True); pl.add_argument("--run", required=True)
    vp = sub.add_parser("validate-place"); vp.add_argument("--id", required=True); vp.add_argument("--run", required=True)
    ff = sub.add_parser("first-frame"); ff.add_argument("--id", required=True); ff.add_argument("--run", required=True)
    ca = sub.add_parser("cancel"); ca.add_argument("--id", required=True); ca.add_argument("--reason", default="cancelled by worker")
    stt = sub.add_parser("status"); stt.add_argument("--id", required=True)
    res = sub.add_parser("reservations")
    dl = sub.add_parser("deadlines")
    s = sub.add_parser("state")
    e = sub.add_parser("expire")
    st = sub.add_parser("stopped"); st.add_argument("--run", required=True)
    st.add_argument("--revision", default=""); st.add_argument("--script", default="")
    st.add_argument("--notes", default="")
    args = ap.parse_args(argv)
    if args.cmd == "preflight":
        with open(args.script, encoding="utf-8") as fh:
            print(json.dumps(preflight(fh.read()), indent=2))
        return 0
    if args.cmd == "file":
        intent, err = file_intent(args.script, args.subject, args.worker, args.pane, args.doc, args.out, args.session)
        if not intent:
            print(err, file=sys.stderr); return 2
        print(json.dumps(intent, indent=2)); return 0
    if args.cmd == "answer":
        tok, err = answer_intent(args.id, args.answer, args.launch)
        if tok:
            if err:
                print(err, file=sys.stderr); return 2
            print(json.dumps(tok, indent=2)); return 0
        print(err, file=sys.stderr); return 2
    if args.cmd == "verify":
        ok, msg = verify_token(args.id, args.script, args.worker, args.session)
        print("OK" if ok else msg)
        return 0 if ok else 2
    if args.cmd == "claim":
        state, value = claim_token(args.id, args.script, args.worker, args.session)
        print(state + (" " + value if value else ""))
        return 2 if state == "ERROR" else 0
    if args.cmd == "register":
        return 0 if register_run(args.id, args.run, args.pid) else 2
    if args.cmd == "place":
        if record_placement(args.id, args.run):
            print(json.dumps(load_intent(args.id), indent=2)); return 0
        ok, reason = validate_placement(args.id, args.run)
        print(reason or "placement refused", file=sys.stderr); return 2
    if args.cmd == "validate-place":
        ok, reason = validate_placement(args.id, args.run)
        print("OK" if ok else reason)
        return 0 if ok else 2
    if args.cmd == "first-frame":
        return 0 if record_first_frame(args.id, args.run) else 2
    if args.cmd == "cancel":
        d, err = cancel_intent(args.id, args.reason)
        if not d:
            print(err, file=sys.stderr); return 2
        print(json.dumps(d, indent=2)); return 0
    if args.cmd == "status":
        d = intent_status(args.id)
        if not d:
            print("no such intent " + args.id, file=sys.stderr); return 2
        print(json.dumps(d, indent=2)); return 0
    if args.cmd == "reservations":
        print(json.dumps({"reservations": reservations_list()}, indent=2)); return 0
    if args.cmd == "deadlines":
        print(json.dumps({"failed": check_deadlines()}, indent=2)); return 0
    if args.cmd == "state":
        expired = expire_pending()
        failed = check_deadlines()
        print(json.dumps({"pending": list_pending(), "active": list_active(),
                          "failed_deadlines": failed}, indent=2)); return 0
    if args.cmd == "expire":
        expired = expire_pending()
        print(json.dumps({"expired": expired, "failed": check_deadlines()}, indent=2)); return 0
    if args.cmd == "stopped":
        print(json.dumps(write_stopped_step(args.run, args.revision, args.script, args.notes), indent=2))
        return 0
    return 2

if __name__ == "__main__":
    sys.exit(main())
