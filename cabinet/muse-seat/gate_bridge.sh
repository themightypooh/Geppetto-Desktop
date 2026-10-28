#!/usr/bin/env bash
# gate_bridge.sh — Lane E: Muse SSH bridge <-> Geppetto accept gate.
#
# What this is: the remote Muse worker (over Tailscale SSH, the existing
# file bridge) does NOT run geppetto-live directly any more. It calls
# this helper, which does the gate dance in the exact order the gate
# requires, and returns only when the cabinet-launched run is attached
# and the worker's own Build receipt exists. It never starts a second
# run, never writes a receipt by hand, and never writes a response
# file — the caller writes responses/<turn>.json after this exits 0
# (or a no-receipt explanation after a non-zero exit) and releases
# the in_progress marker. That separation is deliberate: this helper
# owns the gate, the caller owns the turn.
#
# Flow:
#   1. Validate script / subject / turn; reject --once outright (the
#      new attach flow does not support it; lane B owns that decision).
#   2. If a mapping for this turn already says "attached" and its
#      receipt still exists, return it immediately — a duplicate
#      invocation never consumes another lane's run or receipt.
#   3. Preflight the exact script with gate.py; file the intent with
#      EXPLICIT --doc and --out (see --out note below). The intent id
#      and assigned pane are recorded in the turn mapping.
#   4. Poll (bounded by MUSE_GATE_POLL_TIMEOUT, default 600 s = the
#      gate's 10-minute approval expiry) for the user Yes. Every
#      iteration checks, in order: a cancel/<turn>.json record, the
#      in_progress marker (when --owner was given), the chest host
#      process, and the intent status. Any of cancel / no-marker /
#      chest-closed / intent-cancelled stops promptly — the worker
#      does NOT keep polling a dead turn for the seat's 19 minutes.
#      Heartbeats the marker every ~5 s while waiting so a live
#      Confirming turn is not reaped as stale-inactive mid-Yes-window.
#   5. On Yes, re-check the script hash against the filed intent
#      (edited script = refuse, no run), then attach with the
#      EXISTING launcher path:
#        geppetto-live <script> <doc> <out> --gate-token <id> --gate-worker <worker> --gate-session <session>
#      with GEPPETTO_BUILD_RECEIPTS / GEPPETTO_BUILD_TOKEN inherited
#      from the request environment. The cabinet's Yes-side launcher
#      owns the run; this call only attaches to it (launcher claim
#      ATTACH/WAIT in geppetto-live). Attach itself is bounded by
#      MUSE_GATE_ATTACH_TIMEOUT (default 600 s).
#   6. Verify a receipt file in the receipts directory whose token
#      matches this turn and whose revision is >= 2 and whose run
#      matches the run the launcher reported. No matching receipt
#      after a live exit 0 = failure (exit 5), never a hand-written
#      receipt.
#
# --out destination (for lane B alignment):
#   The launcher defaults out to the script directory when --out is
#   omitted (gate.py file_intent: out = abspath(out or dirname(script));
#   launch_intent: out = intent out or dirname(script)). Muse's
#   previous destination was that same script directory in practice:
#   the peen pipeline scripts lived in the same directory as their
#   `save` target, and panel scripts (muse-build-<turn>.txt) lived in
#   the request cwd, which is also where their save target lived.
#   This helper does NOT rely on that default: it always passes an
#   explicit --out. Default is the caller's working directory (the
#   request cwd for a panel turn; the Pip live dir when --doc is the
#   Pip doc and the caller passes that dir as --out). If lane B
#   changes the launcher default, Muse's destination is unaffected,
#   because it is always explicit here. Conversely, lane B must not
#   ignore an explicit --out and substitute the script directory.
#
# Build-prep gap (Lane D note, 2026-10-26) — reported, not papered over:
#   muse_seat.py does not import chat_phase; the Muse bridge gets
#   build preparation only because the host builds the request prompt
#   through chat.py. This helper enforces the mechanical preconditions
#   (complete script, gate preflight pass, explicit doc/out, hash
#   stability, one attach, receipt verification), but it cannot certify
#   that a subject-specific tool/material plan was made before filing:
#   a preflight-passing script can still be flat primitives, and the
#   gate's own docstring says preflight cannot judge artistic quality.
#   A worker that constructs prompts bypassing the host — or that
#   calls gate.py directly, bypassing this helper — therefore bypasses
#   prep, and no check in this helper can detect that from the script
#   alone (a --prep-note flag would be pure theatre: any worker could
#   pass any string). Closing that gap needs enforcement at the gate
#   (lane A) or the host prompt path (lane D), both outside lane E's
#   lease, so it is reported to the coordinator as a remaining
#   blocker instead of being "fixed" here. API seats (Anthropic /
#   Google / Ollama / OpenAI) intentionally do not receive build prep
#   in chat.py _system; that does not affect this bridge flow, which
#   never touches those seats, and lane E does not patch it.
#
# Cancellation limits, stated honestly:
#   This helper can stop its own polling and its own attach wait
#   promptly, and it never starts a run after a cancel is seen. It
#   cannot kill a cabinet-owned run: the Yes-side run is a separate
#   setsid process owned by the cabinet launcher. Terminating that
#   run on close/cancel is the cabinet's (host/gate) job, not this
#   helper's, and killing it from here would risk killing another
#   lane's run. A remote worker that is mid-attach when the chest
#   closes will therefore exit promptly itself, but the run's fate
#   depends on the cabinet's shutdown cancellation, which lane A/C
#   own. Likewise, a worker that is unreachable over SSH (network
#   down, VM gone) cannot be stopped by a cancel record at all —
#   cooperative cancellation only works while the worker is actually
#   polling, which is why this helper polls the cancel record, the
#   marker, and the host process on every iteration instead of
#   sleeping through the gate's 10-minute window.
#
# Environment overrides (production defaults first; tests set these):
#   MUSE_SEAT_BASE         seat dir; default: this script's directory
#                          (cabinet/muse-seat in production).
#   MUSE_GATE_PY           gate.py; default: <seat-dir>/../gate.py.
#                          When MUSE_SEAT_BASE is overridden for a
#                          test, set this explicitly too.
#   MUSE_GATE_LIVE         geppetto-live; default ~/.local/bin/geppetto-live.
#   MUSE_GATE_HOST_CMD     host-alive check command; default
#                          pgrep -f 'cabinet/host[.]py'. Tests set this
#                          to a stub that exits 0 (open) or 1 (closed).
#   MUSE_GATE_IN_PROGRESS  in_progress.sh; default <seat-dir>/in_progress.sh.
#   MUSE_GATE_POLL_SECS    poll interval; default 0.5.
#   MUSE_GATE_POLL_TIMEOUT yes-wait bound in seconds; default 600.
#   MUSE_GATE_ATTACH_TIMEOUT attach bound in seconds; default 600.
#   MUSE_GATE_PRE_ATTACH_DELAY seconds slept after Yes and before the
#   attach-time hash re-check; default 0. Test-only hook so the
#   edited-after-Yes case is deterministically exercisable.
#   GEPPETTO_BUILD_RECEIPTS / GEPPETTO_BUILD_TOKEN: inherited from
#   the request environment (exactly as geppetto-live expects them).
#
# Exit codes: 0 attached + receipt verified; 2 usage / preflight /
#   filing refusal / edited script / --once / missing receipts env;
#   3 yes-wait timeout or intent expired; 4 cancelled / chest closed /
#   marker lost during wait; 5 attach failed, attach timed out, or
#   no matching receipt after attach.
#
# Usage:
#   gate_bridge.sh --turn <turn_id> --script <abs-or-rel path>
#     --subject "<exact subject>" [--worker <id>] [--session <session>] [--pane N]
#     [--doc <doc.effigy|->] [--out <dir>] [--owner <claim owner>]
#     [--once]   # --once is rejected, see above
set -u

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
BASE="${MUSE_SEAT_BASE:-$SCRIPT_DIR}"
GATE_PY="${MUSE_GATE_PY:-$(dirname "$BASE")/gate.py}"
LIVE="${MUSE_GATE_LIVE:-$HOME/.local/bin/geppetto-live}"
IN_PROGRESS="${MUSE_GATE_IN_PROGRESS:-$BASE/in_progress.sh}"
POLL_SECS="${MUSE_GATE_POLL_SECS:-0.5}"
POLL_TIMEOUT="${MUSE_GATE_POLL_TIMEOUT:-600}"
ATTACH_TIMEOUT="${MUSE_GATE_ATTACH_TIMEOUT:-600}"
CONFIRM_DIR="$(dirname "$GATE_PY")/confirm"

TURN=""; SCRIPT=""; SUBJECT=""; WORKER="muse"; SESSION=""; PANE=""; DOC="-"; OUT=""; OWNER=""; ONCE=0
INTENT=""; ASSIGNED_PANE=""; SCRIPT_HASH=""; MAP=""; MAPDIR=""

fail() { # fail <code> <status> <message>
    local code="$1" status="$2" msg="$3"
    echo "gate_bridge: $msg" >&2
    write_map "$status" "" "" "$msg" || true
    exit "$code"
}

write_map() { # write_map <status> <run> <receipt> <error>
    local status="$1" run="${2:-}" receipt="${3:-}" error="${4:-}"
    [ -n "${MAP:-}" ] || return 0
    mkdir -p "$MAPDIR" 2>/dev/null || return 0
    python3 - "$MAP" "$TURN" "$INTENT" "$status" "$run" "$receipt" "$error" \
        "$ASSIGNED_PANE" "$SCRIPT" "$SCRIPT_HASH" "$DOC" "$OUT" <<'PYEOF' 2>/dev/null || return 0
import json, os, sys, time
path, turn, intent, status, run, receipt, error, pane, script, shash, doc, out = sys.argv[1:13]
try:
    d = json.load(open(path))
except Exception:
    d = {}
d.update({"turn_id": turn, "script": script, "script_hash": shash or d.get("script_hash", ""),
          "doc": doc, "out": out, "status": status, "updated_at": time.time()})
if intent:
    d["intent_id"] = intent
if pane:
    d["pane"] = pane
if run:
    d["run"] = run
if receipt:
    d["receipt"] = receipt
if error:
    d["error"] = error
if status == "filed" and "filed_at" not in d:
    d["filed_at"] = time.time()
if status == "yes" and "yes_at" not in d:
    d["yes_at"] = time.time()
tmp = path + ".tmp"
with open(tmp, "w", encoding="utf-8") as f:
    json.dump(d, f, sort_keys=True)
os.replace(tmp, path)
PYEOF
}

sha_of() {
    sha256sum "$1" 2>/dev/null | cut -d' ' -f1
}

host_alive() {
    if [ -n "${MUSE_GATE_HOST_CMD:-}" ]; then
        eval "$MUSE_GATE_HOST_CMD" >/dev/null 2>&1
    else
        pgrep -f 'cabinet/host[.]py' >/dev/null 2>&1
    fi
}

cancel_seen() { [ -f "$BASE/cancel/$TURN.json" ]; }

marker_ok() {
    [ -z "$OWNER" ] && return 0
    local m="$BASE/in-progress/$TURN.json"
    [ -f "$m" ] || return 1
    [ "$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("owner",""))' "$m" 2>/dev/null)" = "$OWNER" ]
}

heartbeat() { # heartbeat <stage> <live> <detail>; 1 if marker is gone/not ours
    [ -z "$OWNER" ] && return 0
    [ -x "$IN_PROGRESS" ] || return 0
    local out
    out="$(bash "$IN_PROGRESS" heartbeat "$TURN" "$OWNER" "$1" "$2" "$3" 2>/dev/null)" || return 0
    case "$out" in NO_MARKER|NOT_OWNER) return 1 ;; *) return 0 ;; esac
}

while [ $# -gt 0 ]; do
    case "$1" in
        --turn) TURN="$2"; shift 2 ;;
        --script) SCRIPT="$2"; shift 2 ;;
        --subject) SUBJECT="$2"; shift 2 ;;
        --worker) WORKER="$2"; shift 2 ;;
        --session) SESSION="$2"; shift 2 ;;
        --pane) PANE="$2"; shift 2 ;;
        --doc) DOC="$2"; shift 2 ;;
        --out) OUT="$2"; shift 2 ;;
        --owner) OWNER="$2"; shift 2 ;;
        --once) ONCE=1; shift ;;
        --help) sed -n '1,120p' "$0"; exit 0 ;;
        *) echo "gate_bridge: unknown argument: $1" >&2; exit 2 ;;
    esac
done

[ -n "$TURN" ] || { echo "gate_bridge: --turn is required" >&2; exit 2; }
[ -n "$SCRIPT" ] || { echo "gate_bridge: --script is required" >&2; exit 2; }
[ -n "$SUBJECT" ] || { echo "gate_bridge: --subject is required" >&2; exit 2; }
MAPDIR="$BASE/gate"
MAP="$MAPDIR/$TURN.json"
mkdir -p "$MAPDIR" 2>/dev/null || true

if [ "$ONCE" -eq 1 ]; then
    fail 2 rejected "--once is not supported by the gate attach flow; use an open run, not a one-shot run"
fi

case "$SCRIPT" in /*) ;; *) SCRIPT="$PWD/$SCRIPT" ;; esac
[ -f "$SCRIPT" ] || fail 2 rejected "script not found: $SCRIPT"
if [ -z "$OUT" ]; then OUT="$PWD"; fi
case "$OUT" in /*) ;; *) OUT="$PWD/$OUT" ;; esac
if [ "$DOC" != "-" ]; then
    case "$DOC" in /*) ;; *) DOC="$PWD/$DOC" ;; esac
fi
[ -d "$OUT" ] || fail 2 rejected "out directory does not exist: $OUT"
SCRIPT_HASH="$(sha_of "$SCRIPT")"
[ -n "$SCRIPT_HASH" ] || fail 2 rejected "cannot hash script: $SCRIPT"

# Duplicate-invocation guard: an already-attached mapping with its
# receipt still present is returned, never re-run.
if [ -f "$MAP" ]; then
    _prev="$(python3 -c 'import json,sys,os; d=json.load(open(sys.argv[1])); print(d.get("status","")+"|"+d.get("run","")+"|"+d.get("receipt","")+"|"+d.get("intent_id",""))' "$MAP" 2>/dev/null || echo "")"
    _pstatus="${_prev%%|*}"; _rest="${_prev#*|}"; _prun="${_rest%%|*}"; _rest2="${_rest#*|}"; _preceipt="${_rest2%%|*}"; _pintent="${_rest2#*|}"
    if [ "$_pstatus" = "attached" ] && [ -n "$_prun" ] && [ -n "$_preceipt" ] && [ -f "$_preceipt" ]; then
        printf '{"turn_id": "%s", "intent_id": "%s", "run": "%s", "receipt": "%s"}\n' "$TURN" "$_pintent" "$_prun" "$_preceipt"
        exit 0
    fi
fi

# Chest closed at filing time = immediate refusal, no waiting.
if ! host_alive; then
    fail 2 rejected "Geppetto is closed (no cabinet/host.py process) — intent filing refused, no run started"
fi
if cancel_seen; then
    fail 4 cancelled "turn was cancelled before filing (cancel/$TURN.json exists)"
fi
if ! marker_ok; then
    fail 4 cancelled "in_progress marker for this turn is missing or owned by another worker — stopping, not filing"
fi

# Preflight the exact script before any intent exists.
PRE_OUT="$MAPDIR/.$TURN.preflight.json"
if ! python3 "$GATE_PY" preflight --script "$SCRIPT" > "$PRE_OUT" 2>"$MAPDIR/.$TURN.preflight.err"; then
    cat "$MAPDIR/.$TURN.preflight.err" >&2 2>/dev/null || true
    fail 2 rejected "gate preflight could not run for $SCRIPT"
fi
if ! python3 -c 'import json,sys; sys.exit(0 if json.load(open(sys.argv[1])).get("ok") else 1)' "$PRE_OUT" 2>/dev/null; then
    cat "$PRE_OUT" >&2
    fail 2 rejected "gate preflight failed for $SCRIPT — fix the script, do not file"
fi

# File the intent with explicit --doc and --out. Never a second
# pending-intent lock is assumed here: gate.py (lane A) owns pane
# reservation and refusal when all four panes are occupied.
INTENT_OUT="$MAPDIR/.$TURN.intent.json"
if ! python3 "$GATE_PY" file --script "$SCRIPT" --subject "$SUBJECT" --worker "$WORKER" \
        ${PANE:+--pane "$PANE"} ${SESSION:+--session "$SESSION"} --doc "$DOC" --out "$OUT" \
        > "$INTENT_OUT" 2>"$MAPDIR/.$TURN.intent.err"; then
    cat "$MAPDIR/.$TURN.intent.err" >&2 2>/dev/null || true
    fail 2 rejected "gate intent filing refused (see error above) — no run started"
fi
INTENT="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("id",""))' "$INTENT_OUT" 2>/dev/null)"
ASSIGNED_PANE="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("pane",""))' "$INTENT_OUT" 2>/dev/null)"
[ -n "$INTENT" ] || fail 2 rejected "gate filed an intent but returned no id — refusing to wait on an unknown intent"
write_map "filed" "" "" ""
echo "gate_bridge: intent $INTENT filed for turn $TURN, pane $ASSIGNED_PANE, awaiting user yes" >&2
heartbeat "Confirming" "" "gate $INTENT pane $ASSIGNED_PANE awaiting yes" || fail 4 cancelled "in_progress marker lost while filing"

# Poll for Yes, bounded. Order per iteration: cancel, marker, host,
# intent status. Never sleep through a cancellation.
START_TS="$(date +%s)"
LAST_HB="$START_TS"
YES=0
while :; do
    NOW_TS="$(date +%s)"
    if [ $((NOW_TS - START_TS)) -ge "$POLL_TIMEOUT" ]; then
        fail 3 timeout "no user yes within ${POLL_TIMEOUT}s for intent $INTENT — intent left to gate expiry, no run started"
    fi
    if cancel_seen; then
        fail 4 cancelled "turn cancelled while awaiting yes (cancel/$TURN.json appeared)"
    fi
    if ! marker_ok; then
        fail 4 cancelled "in_progress marker lost while awaiting yes — stopping, not attaching"
    fi
    if ! host_alive; then
        fail 4 cancelled "Geppetto chest closed while awaiting yes — stopping, no run started"
    fi
    INTENT_FILE="$CONFIRM_DIR/$INTENT.json"
    if [ ! -f "$INTENT_FILE" ]; then
        fail 4 cancelled "intent file $INTENT_FILE disappeared while awaiting yes"
    fi
    ISTATUS="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("status",""))' "$INTENT_FILE" 2>/dev/null)"
    case "$ISTATUS" in
        yes)
            [ -f "$CONFIRM_DIR/$INTENT.token.json" ] && YES=1 && break
            ;;
        cancelled) fail 4 cancelled "user said no / intent $INTENT cancelled — no runner, reservation released by the gate" ;;
        expired) fail 3 timeout "intent $INTENT expired (gate 10-minute window) — no run started" ;;
        pending) ;;
        *) fail 4 cancelled "intent $INTENT has unexpected status '$ISTATUS' — stopping" ;;
    esac
    if [ $((NOW_TS - LAST_HB)) -ge 5 ]; then
        heartbeat "Confirming" "" "gate $INTENT pane $ASSIGNED_PANE awaiting yes" || fail 4 cancelled "in_progress marker lost while awaiting yes"
        LAST_HB="$NOW_TS"
    fi
    sleep "$POLL_SECS"
done
write_map "yes" "" "" ""
echo "gate_bridge: user yes for intent $INTENT, pane $ASSIGNED_PANE — attaching, not starting a second run" >&2
if [ -n "${MUSE_GATE_PRE_ATTACH_DELAY:-}" ] && [ "$MUSE_GATE_PRE_ATTACH_DELAY" != "0" ]; then
    sleep "$MUSE_GATE_PRE_ATTACH_DELAY"
fi

# The script must be byte-identical to the filed one at attach time.
if [ "$(sha_of "$SCRIPT")" != "$SCRIPT_HASH" ]; then
    fail 2 rejected "script changed after filing (hash mismatch for intent $INTENT) — file a fresh intent, do not attach"
fi
if [ -z "${GEPPETTO_BUILD_RECEIPTS:-}" ] || [ -z "${GEPPETTO_BUILD_TOKEN:-}" ]; then
    fail 2 rejected "GEPPETTO_BUILD_RECEIPTS/TOKEN are not in the environment — cannot produce this turn's Build receipt"
fi
if cancel_seen; then
    fail 4 cancelled "turn cancelled between yes and attach — not attaching"
fi
if ! marker_ok; then
    fail 4 cancelled "in_progress marker lost between yes and attach — not attaching"
fi

# Attach to the cabinet-launched run. This is the existing launcher
# path, not a new run: geppetto-live claims the token and, for the
# cabinet winner, attaches to its registered run.
LIVE_OUT="$MAPDIR/.$TURN.live.out"
LIVE_ERR="$MAPDIR/.$TURN.live.err"
if command -v timeout >/dev/null 2>&1; then
    timeout "$ATTACH_TIMEOUT" "$LIVE" "$SCRIPT" "$DOC" "$OUT" --gate-token "$INTENT" --gate-worker "$WORKER" --gate-session "$SESSION" > "$LIVE_OUT" 2>"$LIVE_ERR"
    LIVE_RC=$?
else
    "$LIVE" "$SCRIPT" "$DOC" "$OUT" --gate-token "$INTENT" --gate-worker "$WORKER" --gate-session "$SESSION" > "$LIVE_OUT" 2>"$LIVE_ERR"
    LIVE_RC=$?
fi
if [ "$LIVE_RC" -eq 124 ]; then
    cat "$LIVE_ERR" >&2 2>/dev/null || true
    fail 5 attach_failed "attach to intent $INTENT timed out after ${ATTACH_TIMEOUT}s — no duplicate started, cabinet owns the run's fate"
fi
if [ "$LIVE_RC" -ne 0 ]; then
    cat "$LIVE_ERR" >&2 2>/dev/null || true
    cat "$LIVE_OUT" >&2 2>/dev/null || true
    fail 5 attach_failed "geppetto-live attach for intent $INTENT failed (exit $LIVE_RC) — no duplicate started"
fi
RUN="$(grep -m1 '^run: ' "$LIVE_OUT" 2>/dev/null | sed 's/^run: //')"
[ -n "$RUN" ] || fail 5 attach_failed "geppetto-live exited 0 for intent $INTENT but reported no run path"

# Verify the worker's own receipt: token match, revision >= 2, run match.
RECEIPT="$(python3 - "$GEPPETTO_BUILD_RECEIPTS" "$GEPPETTO_BUILD_TOKEN" "$RUN" <<'PYEOF' 2>/dev/null
import glob, json, os, sys
receipts, token, run = sys.argv[1], sys.argv[2], sys.argv[3]
for path in sorted(glob.glob(os.path.join(receipts, "*.receipt.json"))):
    try:
        d = json.load(open(path))
    except Exception:
        continue
    if d.get("token") != token:
        continue
    try:
        rev = int(d.get("revision", 0))
    except Exception:
        continue
    if rev < 2:
        continue
    if run and d.get("run") and os.path.abspath(str(d["run"])) != os.path.abspath(run):
        continue
    print(path)
    sys.exit(0)
sys.exit(1)
PYEOF
)"
[ -n "$RECEIPT" ] || fail 5 attach_failed "geppetto-live attached to $RUN but no matching Build receipt (token/revision>=2/run) appeared in $GEPPETTO_BUILD_RECEIPTS — receipt was not written by hand"

heartbeat "Building" "$RUN" "gate $INTENT attached run $RUN" || true
write_map "attached" "$RUN" "$RECEIPT" ""
printf '{"turn_id": "%s", "intent_id": "%s", "pane": "%s", "run": "%s", "receipt": "%s"}\n' "$TURN" "$INTENT" "$ASSIGNED_PANE" "$RUN" "$RECEIPT"
exit 0
