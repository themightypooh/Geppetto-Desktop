# Geppetto gate — frozen intent registry / approval protocol (Lane A, contract v1)

Date: 2026-10-26. Owner: Lane A (`cabinet/gate.py`, `tests/test_gate_registry.py`,
this file). Coordinator freezes this contract; lanes B/C/E implement against it.
Status: **fixture-only, NOT live-verified.** The real chest was closed throughout
Lane A work and was never started for it. No visible pane run, no paid provider
turn, and no under-five-seconds claim is made from this document. The 5-second
figure below is a fixture acceptance target, measured by the integration
fixture (fake chest + fake runner), not by this registry.

## 1. What the registry is, and is not

- The registry is the durable, on-disk source of truth for gate intents. It is
  NOT the host's in-memory `_gate_slots`. Host memory dies on restart; intent
  files in `cabinet/confirm/` survive, and that survival is the whole point of
  this lane.
- A "yes" is an approval for **one exact script** (absolute path + SHA-256 hash),
  **one worker**, **one session binding** (see §5), **one assigned pane**, and
  **one run**. A new subject, a new script, or a new run needs a new intent
  and a new yes. Steering an already-placed run (`more`/`skip`/`rewind`/`close`)
  is not a fresh intent and needs no new token.
- The registry does not judge artistic quality, does not observe the viewport,
  and does not kill processes. It records timestamps and bindings; the launcher
  (Lane B) and host (Lane C) are responsible for killing only the run they own
  when a deadline is missed. A first-frame timestamp is **host-reported**
  (Lane C calls `record_first_frame`), not independently observed by the registry.

## 2. On-disk layout

Registry directory: `cabinet/confirm/` (the directory containing `gate.py`).
Tests MUST point `gate.CONFIRM_DIR` / `gate.AUDIT` at a temp directory; they
must never touch the real `cabinet/confirm/`.

Per intent `<id>.json` (intent file):

| Field | Type | Meaning |
|---|---|---|
| `id` | string | `gate-<unix seconds>-<8 hex>` (§4). The only accepted ID shape. |
| `status` | string | State machine (§3). |
| `subject` | string | Exact user subject, stripped, 1–120 chars, no control chars. |
| `script` | string | Absolute path of the approved script file. |
| `script_hash` | string | SHA-256 hex of the script text at filing time. |
| `doc` | string | `"-"` or absolute path of the document the run starts from. |
| `out` | string | Absolute output directory. If `--out` is omitted at filing, this is the script's directory (preserved Muse behaviour; see §8). |
| `worker` | string, required | Worker identity, `^[A-Za-z0-9][A-Za-z0-9_.\-]{0,119}$`. Empty worker is REFUSED. |
| `session` | string | Session binding filed with the intent, or `""`. `^[A-Za-z0-9][A-Za-z0-9_.\-:]{0,199}$` when non-empty. For Muse, Lane E files the bridge `session_id` here when it has one; see §5. |
| `pane` | string | Assigned pane `"1"`–`"4"`, assigned at filing, never reassigned. |
| `preflight` | object | Result of `preflight()` at filing time. |
| `created_at` | float | Epoch seconds at filing. Pending expiry is `created_at + 600`. |
| `placed_at` | float, optional | Set by `record_placement`. |
| `first_frame_at` | float, optional | Set by `record_first_frame` (host-reported). |
| `cancel_reason` | string, optional | Set on cancel/fail/close. |

Per intent `<id>.token.json` (token file, written only on yes):

| Field | Type | Meaning |
|---|---|---|
| `id` | string | Same as intent id. |
| `script` | string | Absolute script path, copied from the intent. |
| `script_hash` | string | Must match the script at claim time, byte-for-byte in hash. |
| `subject` | string | Copied from the intent. |
| `worker` | string | Copied from the intent. |
| `session` | string | Copied from the intent (`""` if unbound at filing). |
| `bound_session` | string, optional | First non-empty session seen at claim time when the intent filed `session == ""` (§5). |
| `yes_at` | float | Epoch seconds of the yes. Claim/start deadline is `yes_at + 600`. |
| `used_at` | float or null | Set by the single successful `claim` (START). Null until then. |
| `run` | string, optional | Absolute run directory, set once by `register`. Immutable after set. |
| `pid` | int, optional | Launcher child PID, set once by `register`. Immutable after set. |
| `registered_at` | float, optional | Set by `register`. |

Audit: `cabinet/confirm/audit.log.jsonl`, one JSON object per line
(`t`, `event`, `id`, …). Audit writes are best-effort (an audit failure never
changes a gate decision). Stopped-step records live under
`cabinet/confirm/stopped/` and are unchanged by this lane.

All intent/token writes are atomic: write a per-process temp file in the same
directory, flush + fsync, then `os.replace` over the target. All registry
mutations that read-then-write run under one `flock` on
`cabinet/confirm/.lock`. **The lock is never held across a host subprocess call
or any provider call**: filing reserves with the host outside the lock, then
re-checks durable occupancy under the lock before writing the intent (§6).
Readers that do not mutate (e.g. `list_pending`) may read without the lock;
`os.replace` guarantees they see a complete old or new file, never a torn one.

## 3. State machine

```
pending ──yes──► yes ──claim START──► launched ──place──► placed (terminal for the gate)
   │            │  ▲                    │  │
   │            │  │                    │  └── first-frame recorded (stays placed)
   no/expire    │  │                    │
   ▼            │  │                    └── deadline missed / dead pid ──► failed (terminal)
cancelled      │  │                    │
(expired)      │  └─ WAIT/ATTACH callers do not change state
               │
               └── chest closed (unused approval) ──► cancelled (terminal)
               └── claim START taken but no register in time ──► failed (terminal)
               └── yes not claimed in time ──► expired (terminal)
pending ──chest closed──► cancelled (terminal)
launched ──chest closed──► closed (terminal; host stops the run separately)
```

Terminal states: `placed`, `cancelled`, `expired`, `failed`, `closed`.
Active states (they reserve a pane in the durable registry): `pending`, `yes`,
`launched`. `placed` does NOT reserve a pane in the registry — the placed run
itself owns the pane, and the host's occupancy check (Lane C) must refuse that
pane to new reservations. A terminal intent never becomes active again; a
duplicate yes, a second claim START, or a re-register is an error, not a
resurrection.

## 4. Identity and validation (fail closed)

- Intent IDs must match `^gate-[0-9]+-[0-9a-f]{8}$`. Any other id — including
  anything containing `/`, `..`, or whitespace — is rejected **before any file
  is opened**, so an id can never escape the registry directory. This applies
  to every CLI subcommand and every public function that takes an id.
- Script: must exist, be a regular file, be valid UTF-8 text, and pass
  `preflight()` at filing. Preflight is unchanged by this lane (geometry line,
  Cylinder `radius=` rule, `materialslot=` rule, at most 2 material lines
  before first geometry, fewer than 16 rotated Boxes, a `save` line).
- At yes time the script is re-hashed; a changed script refuses the yes
  ("file a fresh intent"). At claim time the script path **and** hash are
  re-checked; a changed script, a different path with a different hash, or a
  missing script is `ERROR`, never a silent run.
- Requested pane must be `""` (default 1) or `"1"`–`"4"`. Anything else is
  REFUSED, not clamped.
- Malformed or corrupt intent/token JSON is never trusted: readers skip it
  (`list_pending`) or return an explicit error (single-intent operations),
  never crash and never treat it as approved. A corrupt file does not block
  unrelated intents.
- If the chest is closed (host socket/command answers nothing, or answers
  anything other than a pane number / `FULL`), filing fails immediately with
  exit 2. There is no queue, no wait, and no headless fallback.

## 5. Worker / session ownership of a claim (the §"claim proves identity" fix)

Old behaviour proved only possession of an intent id plus a matching script.
New behaviour (this contract):

1. Filing requires `--worker`. The worker string is copied into the intent and
   the token.
2. `claim` (CLI) requires `--worker` and accepts optional `--session`.
   - If the claim worker differs from the intent worker → `ERROR`
     ("intent belongs to another worker"). No START, no ATTACH, no WAIT.
   - If the intent filed a non-empty `session`, the claim session must equal
     it exactly, or → `ERROR`.
   - If the intent filed `session == ""`, the first non-empty claim session
     is recorded as `bound_session`; later claims with a different non-empty
     session → `ERROR`. Claims with an empty session (the cabinet launcher
     acting on behalf of the intent, which reads worker/session from the
     intent itself) are always allowed on the session check, so launcher-first
     and worker-first ordering both work.
3. `ATTACH` (a run is already registered) is only returned to a caller that
   passes the ownership check above. A foreign worker never learns another
   lane's run directory from the registry.
4. Honest limit: this is **binding, not authentication**. The trust boundary
   is the local filesystem plus Tailscale SSH authentication of the remote
   worker. A local process that can already read `cabinet/confirm/` and knows
   a worker string is not stopped by this check alone, and this document does
   not claim otherwise. Lane E must still file with the real bridge session
   id so that a mismatched session is at least detectable in the audit log.
5. Enforcement point is the **CLI** (`claim --worker/--session`,
   `verify --worker`): a remote worker or launcher that goes through the
   CLI cannot ATTACH with token possession + script path alone. The
   Python functions (`claim_token`, `verify_token`) still allow an empty
   worker/session from a legacy in-process caller — that path exists only
   for existing in-process tests (e.g. `test_join_gate_poll.py`, which
   calls `claim_token`/`verify_token` directly without a worker) and for
   local trusted code that already has filesystem access. Lanes B/E must
   never call the functions directly to bypass the CLI check; the
   integration suite must test the CLI, not just the functions, for
   ownership. Gate-token possession + script path alone is **not**
   sufficient to ATTACH or to mint a receipt through any supported path:
   the launcher mints a receipt only after a successful owned claim, and
   a foreign worker/session gets `ERROR` and learns no run directory.

### 5.6 How the launcher obtains worker/session for claim — FROZEN, no guessing (2026-10-26 coordinator urgent)

This is the question Lane B hit in production: `gate.py claim --worker`
is REQUIRED, but the production launcher's claim call passed only
`--id/--script`, so every gate launch failed at claim with an argparse
error, exit 2, no run, token not consumed. That fail-closed behaviour
is intentional and is **not** a gate bug — but the contract below is the
only authorised way to fix it. Lane B must not guess a different one.

There are exactly two claim callers, and each has exactly one source:

| Caller | Source of `--worker` / `--session` for `claim` | Exact claim call |
|---|---|---|
| **Worker attach** (`geppetto-live` WITHOUT `--gate-launch`, i.e. the worker's own invocation after yes) | The worker's **own filing context**: the same worker string and session it filed the intent with. For Muse (Lane E), that is the bridge worker id and the bridge `session_id` from the turn that filed. | `gate.py claim --id <id> --script <script> --worker <filing worker> --session <filing session>` (session may be `""` only if the intent filed `""`) |
| **Host launcher** (`geppetto-live --gate-launch`, spawned ONLY by `gate.py launch_intent` after yes) | **Intent-file fields, transported as launcher flags.** `launch_intent` reads `worker`/`session` from the intent file (the only authoritative source for the host launcher) and spawns: `geppetto-live <script> <doc> <out> --gate-token <id> --gate-launch --gate-worker <intent worker> --gate-session <intent session>` (session arg may be empty, preserved as a separate argv entry by `Popen`). The launcher (Lane B) must parse `--gate-worker`/`--gate-session` in its argument loop and forward them **verbatim** to its `gate.py claim` call. | `gate.py claim --id <id> --script <script> --worker <flag value> --session <flag value>` |

**One flag surface for both paths (Lane B's required change).**
`geppetto-live` must accept `--gate-worker`/`--gate-session` in all gate
modes and forward them to `gate.py claim`. The host launcher receives
them from `launch_intent`; the worker attach receives them from the
worker that invokes `geppetto-live` (Lane E must pass the filing
worker/session through to its `geppetto-live` invocation — it already
knows both, it filed the intent with them). Until Lane B implements
that forwarding, production launches **will continue to fail closed**
at claim (argparse error, exit 2, no run, token NOT consumed) — and
that is the correct state to stay in until the fix lands, because the
alternative (claim without identity) would silently re-open the
ownership hole Lane F flagged. No approval is lost by the current
failures: the token is not consumed by a failed argparse call, the
intent stays `yes`, and the same intent can be launched once B's fix
lands, provided it is within the 600-second yes→claim window.

**Forbidden, explicitly (do not implement any of these as a fallback):**
- Deriving claim identity from `GEPPETTO_MODELLER`. That variable is a
  display label: `launch_intent` sets it from the intent worker, but
  other `geppetto-live` paths set it from `CODEX_THREAD_ID` /
  `CODEX_SESSION_ID` / `CLAUDE_CODE_SESSION_ID` env, so it is
  ambiguous and is not claim identity.
- Deriving claim identity from any other process environment variable.
  `launch_intent` deliberately does **not** pass worker/session via
  environment (no `GEPPETTO_GATE_WORKER`/`GEPPETTO_GATE_SESSION`):
  flags on the spawn argv are auditable in the launch log and in the
  process list, and cannot be confused with provider credentials that
  `launch_intent` strips from the environment.
- Parsing the intent file in the launcher shell as a fallback when the
  flags are absent. If `--gate-launch` is invoked without
  `--gate-worker`, the launcher must exit 2 with an explicit error
  ("gate-launch requires --gate-worker from host launch_intent"),
  not invent a worker, not read the intent file, and not start a run.
- A `--gate-launch` bypass in `claim` itself (i.e. "launcher mode
  need not prove worker/session"). No such mode exists in this
  contract, and Lane B must not invent one: a worker that can invoke
  the launcher manually could otherwise use it to start another
  lane's intent, which is exactly the cross-lane start this protocol
  exists to make an error. (Reminder from §5 point 4: this binding is
  not authentication against a determined local attacker — it is what
  makes accidental cross-lane starts and receipt minting fail loudly
  and auditably, instead of silently succeeding.)
- Falling back to the old behaviour of claiming with only
  `--id/--script`. That behaviour is removed by this contract; the
  CLI `--worker` requirement is intentional.

**Legacy intents:** an intent filed before this contract with no valid
worker cannot be launched by `launch_intent` under v1: `launch_intent`
returns an error, `answer_intent` cancels that intent and releases its
pane, and the worker must file a fresh intent. This is fail-closed,
not a silent launch with an invented worker.

## 6. Pane reservation (durable + host, no double-booking)

Filing order, exactly:

1. Under the lock: expire stale pending/yes intents (releases collected, sent
   to the host **after** the lock is released), validate subject/worker/
   session/pane/script, run preflight, mint the id.
2. Outside the lock: ask the host for the requested pane
   (`gate-reserve <id> <requested>`, §7). The host tries requested, then
   following numbers, wrapping 4→1, and never evicts anything.
3. Under the lock: re-read durable active panes. If the host-assigned pane is
   already reserved by a *different* active intent in the durable registry,
   the filing is REFUSED (see restart case below) and the host slot is
   released after the lock. If it is free, the intent is written and the
   pane is durably reserved from that moment.
4. If the host answers `FULL`, or nothing (chest closed), filing is REFUSED
   immediately. All four panes occupied — counting host occupancy **and**
   durable gate reservations — means refusal, never a queue.

**Restart reconciliation (the gap this lane closes, and its honest limit).**
The registry alone cannot resurrect host memory. After a chest restart:

- Filing a new intent will NOT double-book a pane held by a surviving active
  intent: step 3 catches the host-assigned pane colliding with the durable
  registry and refuses the filing explicitly, with the colliding intent id in
  the message. It does not silently pick another pane in that case, because
  the host's view of occupancy is known-stale at that moment.
- The correct recovery is Lane C's: on host startup, the host MUST repopulate
  its reservation slots from `gate.py reservations` (active intents, §7)
  **before** accepting new reservations, and must treat those panes as
  occupied. Once it has done so, step 3 no longer collides and normal
  requested→wrap assignment resumes. Until Lane C implements that, the
  refusal in step 3 is the fail-closed behaviour, and it must not be
  "fixed" by deleting the surviving intent file.
- A `placed` intent is not in the durable reservation set (the run owns the
  pane); after restart, host occupancy (live run / static model / replay)
  is what protects that pane, and Lane C's occupancy counting must include
  it. The registry's `reservations` output still lists placed intents'
  runs for diagnosis, flagged as non-reserving.

## 7. Commands and host-socket contract

### 7a. `gate.py` CLI (all commands run from the project root or anywhere;
`gate.py` locates `cabinet/confirm/` relative to itself)

| Command | Success | Failure |
|---|---|---|
| `gate.py preflight --script PATH` | exit 0, preflight JSON | exit 2 on unreadable script (exception, not a gate refusal — preflight itself is read-only) |
| `gate.py file --script PATH --subject S --worker W [--pane N] [--session SESS] [--doc D] [--out O]` | exit 0, intent JSON (includes assigned `pane`) | exit 2, REFUSED/PREFLIGHT FAIL on stderr, no intent file, no host slot left held |
| `gate.py answer --id ID --answer yes\|no [--launch]` | yes: exit 0, token JSON. no: exit 2 with `cancelled: not modeled` on stderr (the no is recorded; the exit code signals "nothing to run") | exit 2, reason on stderr; duplicate yes/no, expired, or changed script |
| `gate.py claim --id ID --script PATH --worker W [--session SESS]` | exit 0, `START`, `WAIT`, or `ATTACH <run>` on stdout | exit 2, `ERROR <reason>` on stdout; missing `--worker` is an argparse error, exit 2, token NOT consumed — see §5.6 for exactly how each caller obtains W/SESS, and what Lane B must change in `geppetto-live` to forward them |
| `gate.py register --id ID --run RUN --pid PID` | exit 0 | exit 2 (no claim START taken, run already registered, pid dead, or intent terminal) |
| `gate.py verify --id ID --script PATH [--worker W] [--session SESS]` | exit 0, `OK` | exit 2, reason. **Legacy single-use path, deprecated for new launches**: it consumes a token without the claim/attach protocol and without a registered-run binding. Lanes B/E must use `claim` + `register`, not `verify`, for gate launches. |
| `gate.py place --id ID --run RUN` | exit 0 (intent → `placed`, reservation released) | exit 2 (run does not match the registered run, intent not launched, or terminal) |
| `gate.py validate-place --id ID --run RUN` | exit 0 | exit 2. Read-only: this is what Lane C's `gate-place` must check **before** `showRunIn`. |
| `gate.py first-frame --id ID --run RUN` | exit 0 (sets `first_frame_at`, intent → `placed` if not already) | exit 2 (run mismatch or terminal) |
| `gate.py cancel --id ID [--reason R]` | exit 0, intent JSON (already-terminal intents report their terminal status and still exit 0; the reservation is released if it was still held) | exit 2 only if the id is invalid or no such intent exists |
| `gate.py status --id ID` | exit 0, merged intent + token + deadline JSON (§7c) | exit 2 (invalid id / not found / corrupt) |
| `gate.py reservations` | exit 0, `{"reservations": [...]}` (§7c) | exit 0 with an empty list when there is nothing active (never an error) |
| `gate.py state` | exit 0, `{"pending": [...], "active": [...], "failed_deadlines": [...]}` (state also expires stale pending/yes and runs deadline checks) | — |
| `gate.py expire` | exit 0, `{"expired": [...], "failed": [...]}` | — |
| `gate.py deadlines` | exit 0, `{"failed": [...]}` (marks deadline-missed intents failed and releases their panes; it does NOT kill any process — see §8) | — |
| `gate.py stopped --run DIR --revision R [--script S] [--notes N]` | exit 0, stopped-step JSON (unchanged) | — |

`answer --launch` (host-side only): after a yes, the host spawns the cabinet
launcher (`geppetto-live … --gate-token ID --gate-launch`, Lane B). If that
spawn itself fails (not the later run — the spawn), the intent is cancelled
and the pane released, so a failed dispatch never leaves a live-looking yes.

### 7b. Host socket messages (unchanged wire format, tightened meaning)

Existing messages, sent by `gate.py` as `python3 cabinet/host.py <msg>` (the
host's own CLI/socket path — Lane A does not change that transport):

- `gate-reserve <id> <preferred>` → `"1"`–`"4"`, or `"FULL"`, or no reply
  (chest closed). Lane C must additionally exclude panes reserved by durable
  active intents (from `reservations`) and must count static models, live
  runs, replay, bench, parked/hidden panes, and pending reservations as
  occupied, while never counting a visually empty pane as occupied merely
  because its idle viewer exists.
- `gate-check <id>` → assigned pane number, or `0` (no reservation).
- `gate-release <id>` → `"ok"` (idempotent; also drops any pending placement
  for that id).
- `gate-place <id> <run>` → `"ok"` or `"no reservation"`. Lane C must, before
  `showRunIn`, call the registry's `validate_placement(id, run)` (§7a
  `validate-place` semantics): the run must equal the token's registered run,
  the intent must be launched (not pending, not terminal, not merely "yes"
  with an unregistered claim), and the pane must still be the intent's
  assigned pane. After a successful `showRunIn`, Lane C must call
  `record_placement(id, run)` so the registry releases the reservation and
  marks the intent placed. If `showRunIn` fails, it must NOT record placement
  and must free the reservation instead.

### 7c. `status` / `reservations` JSON shape

`status --id` prints one object: the intent fields, plus
`"token": {…}` (the token file, or null), plus
`"deadlines": {"pending_expires_at": …, "start_expires_at": …,
"register_deadline_at": …, "first_frame_deadline_at": …}` (nulls where a
deadline does not apply), plus `"pid_alive": true|false|null` (null when no
pid is registered; best-effort `os.kill(pid, 0)` — pid reuse means this is
advisory, never proof the original process lives).

`reservations` prints `{"reservations": [ {id, pane, status, subject, worker,
session, run, pid, registered_at, first_frame_at, pid_alive, reserving:
true|false}, … ]}` for active intents, plus placed intents with
`reserving: false` for diagnosis. Lane C's startup rehydration consumes
exactly this output.

## 8. Deadlines, and what happens when one is missed

| Deadline | From | Length | Missed → |
|---|---|---|---|
| Pending answer | `created_at` | 600 s | intent → `expired`, pane released, no run. |
| Yes → claim START | `yes_at` | 600 s | intent → `expired`, pane released. An old yes must not launch a new run hours later. |
| Claim START → register | token `used_at` | 10 s | intent → `failed`, pane released. Later `claim` calls return `ERROR` (registration expired), never `WAIT` forever. Lane B's own attach wait is 5 s (its contract), so it errors first; the registry's 10 s is the backstop that stops a stale `WAIT` from ever becoming a start. |
| Register → first frame | `registered_at` | 30 s | intent → `failed`, pane released (by `deadlines`/`state`/`expire` checks). **The registry marks and releases; it does not kill the run.** Lane B (launcher) or Lane C (host) must terminate only that intent's registered pid/run when they observe the failure, and must surface an explicit failure state in the pane. No silent queue, no move to another pane. |

Fixture acceptance target (separate from the hard deadlines above): under the
integration fixture (fake chest + fake runner), click → first synthetic frame
in the assigned pane in under 5 seconds. The registry's 30-second first-frame
deadline is deliberately looser than the fixture target: a real model load can
legitimately exceed 5 seconds, and killing a legitimate real build at 5
seconds would be a worse failure than reporting a slow fixture. The fixture
must therefore measure and report click → launch → register → first-frame
timestamps separately, and a fixture miss of 5 seconds is a reported failure
of the fixture target, not an automatic registry kill. No latency claim in
any Lane A report may be derived from token-write or `Popen` timing alone.

Other deadline-adjacent rules:

- `register` with a dead pid is refused and marks the intent `failed`
  (PID death before register is a failure, not a retryable state).
- A launched run whose registered pid dies before any first frame is
  marked `failed` by deadline checks (`state`/`expire`/`deadlines`, and
  on the next `claim` for that intent), and a later `claim` for it
  returns `ERROR`, never `ATTACH` to the dead run. A dead pid is
  definitive (a reused pid would read alive); a live pid is only
  advisory. `validate_placement` likewise refuses a dead-pid run.
- `cancel_waiters` (chest close) now cancels pending, yes-unclaimed,
  and yes-claimed-but-unregistered intents, closes yes-with-run and
  launched intents, and releases their host pane slots after the lock
  (best-effort; if the host is already gone the release fails silently
  and the slots die with it). Previously (old 512-line code) it left
  used tokens untouched and released nothing — Lane F cases A/B/C/E.
  See §11 for the full re-verification.
- A duplicate yes, a yes after expiry, or a yes after a no is an error and
  leaves the existing terminal state untouched.
- `cancel` (explicit, or chest-close via `cancel_waiters`) is terminal and
  releases the pane. Chest close cancels pending and unused yes approvals,
  and marks launched intents `closed`. It never deletes intent files: the
  cancellation record is what a remote worker (Lane E) polls for, promptly,
  via `status`. Cooperative remote cancellation has a hard limit that must
  be stated honestly: a remote worker that has already started a run, or
  that never polls again, cannot be stopped by a registry record alone —
  only the host/launcher killing that run stops it, and a worker mid-`claim`
  that never re-checks status will not see the cancellation until its next
  registry call. Lane E must therefore check `status` before attaching and
  after any bounded wait, not only at filing time.
- `--once` is NOT supported by the claim/attach flow: a launcher that
  receives `--once` together with a gate token must reject it explicitly
  (Lane B's contract) rather than running once outside the registered run.
  The registry takes no position on `--once` for non-gate runs.
- The launcher's `out` default (script directory when `--out` is omitted)
  is preserved by this contract. Lane E must file with an explicit `--out`
  (and `--doc`) so Muse runs land in the bridge's expected destination
  rather than wherever the script happens to sit; if Lane E's previous
  destination differed from the script directory, that difference must be
  resolved by Lane E's explicit `--out`, not by changing this default for
  every other worker.

## 9. Concurrency

- Four simultaneous filings into four distinct panes succeed; a fifth filing
  is refused immediately (host `FULL` or durable conflict), never queued.
- Two simultaneous `claim` calls: exactly one `START`, the other `WAIT`
  (until register) then `ATTACH <same run>`. No second run, no second
  receipt for the same intent.
- Two simultaneous filings with the same script/subject are independent
  intents with distinct ids and distinct panes (or a refusal if no pane is
  free). Idempotency is by intent id, never by subject/script.
- The lock is per registry directory (one `flock` file). It does not
  serialise host or provider work, by design (§2).

## 10. What Lane A did NOT change, and what it asks of other lanes

- `host.py`: untouched by Lane A. Lane C owns: durable-reservation-aware
  `gate-reserve`, `validate_placement` before `showRunIn`,
  `record_placement` after success, bounded registration/first-frame timers
  with a visible failure state, startup rehydration from `reservations`,
  and never auto-following a reserved run into another pane.
- `geppetto-live`: untouched by Lane A. Lane B owns: `--gate-launch`
  starting once in the approved doc/out, `claim --worker/--session` +
  `register`, both ordering races, a bounded attach wait, explicit
  `--once` rejection with a gate token, receipt separation from the cabinet
  launcher, and killing only its own run on cancellation/failure.
- `muse_seat.py` / bridge helpers: untouched by Lane A. Lane E owns:
  filing with explicit `--doc`/`--out`/worker/session, turn↔intent/run
  mapping, attach-after-yes, and stopping on cancel/closed/no-marker —
  including not polling a dead turn for 19 minutes after a clear close.
- `chat.py` / `chat_phase.py` / `claude_link.py`: untouched. Lane D owns
  prompt propagation; Lane F audits cancellation/failure read-only.
- Open questions for the coordinator (not silently decided by Lane A):
  1. Should the 30-second registry first-frame deadline also become the
     host's visible-failure threshold, or should the host show a "slow
     start" state at 5 seconds and only fail at 30? Lane A recommends:
     visible "starting…" at once, explicit failure at the registry
     deadline, with the fixture 5-second target reported separately.
  2. Should `verify` (legacy single-use path) be removed once Lanes B/E
     have migrated, or kept for non-gate tooling? Lane A recommends
     removal in a follow-up, not in this lane, to avoid breaking unknown
     callers mid-integration.
  3. The host's `_gate_place_ready` timer and first-frame observation
     (Lane C) are the only true first-frame witnesses; until they exist,
     `first_frame_at` in the registry is host-reported and must be
     labelled as such in every report.

## 11. Lane F re-verification (2026-10-26, against current code — NOT F's old 512-line copy)

Lane F read `gate.py` at 512 lines; this lane has since grown it past
1,000 lines. Every F item below was re-checked against the current code
and, where it was still open, fixed in this lane (tests in
`tests/test_gate_registry.py` cover the fixed cases):

1. **Shutdown cancellation (F cases A/B/C/E) — CLOSED, with one fix.**
   Old code: `cancel_waiters` cancelled only pending and yes-unused
   intents, released no host slots, and a claimed-but-unregistered
   token returned `WAIT` forever. Current code before the F forward:
   launched → `closed` (case A closed), but yes-claimed-unregistered
   still survived (case B still open) and no releases were sent
   (cases C/E still open). Fix in this lane: `cancel_waiters` now
   cancels yes-claimed-but-unregistered, closes yes-with-run, and
   releases all cancelled/closed panes after the lock. A used token
   can no longer survive a chest close and re-ATTACH, and a
   claimed-but-unregistered token can no longer return `WAIT` forever
   (it is cancelled by close, or failed by the 10-second register
   deadline if close never happens).
2. **Persist reservations / reconcile dead-pid tokens — CLOSED for the
   registry side.** Reservations are durable in intent files, not host
   memory; `reservations` output is the host-startup rehydration
   source; a filing that collides with a durable reservation is
   refused explicitly (§6). Dead-pid tokens are now failed by deadline
   checks and refused by `validate_placement`/`claim` (see §8). The
   remaining half is Lane C's: the host must actually rehydrate from
   `reservations` on startup and must run the bounded timers.
3. **Docstring "only ONE pending" vs multi-pending code — CLOSED in
   this lane.** The old docstring was wrong; the code already allowed
   multiple pending intents (see `test_join_gate_poll.py`, which files
   two). The current docstring says multiple independent pending
   intents in distinct panes, no global lock. **Host poller second
   half — NOT Lane A's file, referred to Lane C:** `host.py`
   `_gate_poll_shared_intents` (lines ~3643–3710) still displays only
   `_pending[0]` — it toasts and launches the desktop card only for
   the first pending intent, so a second intent can sit unseen until
   `expire_pending()` (called each poll) expires it at 600 seconds.
   Gate-side expiry is correct; the user-visible gap is Lane C's to
   fix (iterate all pending intents, or queue cards per intent).
   Lane A must not patch `host.py` to fix it.
4. **Claim identity — CLOSED via CLI enforcement, documented honestly
   (§5 point 5).** Old code checked intent id + script path/hash only.
   Current code: `claim` CLI requires `--worker`, session binding is
   enforced, foreign workers/sessions get `ERROR` and no run
   directory, and `verify` CLI now also requires `--worker`. The
   residual hole is only the in-process function path (empty worker
   allowed for legacy tests/local trusted code), which is documented,
   not hidden, and which Lanes B/E must not use to bypass the CLI.
   Receipt minting itself is Lane B's: a receipt is minted only after
   an owned claim, so a foreign ATTACH cannot mint another lane's
   receipt through any supported path. (Lane F's receipt audit script
   is about `build_execution.record`, not the gate registry; its
   duplicate-receipt finding is referred to the coordinator/Lane B,
   not fixed in this lane — the gate side of it is the ATTACH
   ownership check above.)
5. **Test-hang found while re-verifying — FIXED, root cause recorded.**
   The first `test_gate_registry.py` run hung (timeout, no output past
   `test_validate_place_and_first_frame`): `record_placement` held the
   registry `flock` and then called `validate_placement`, which
   re-acquired the same `flock` — and `flock` is not re-entrant, even
   in the same process, so the process deadlocked holding the lock.
   Fix: `_validate_placement_locked` (assumes the lock is held) used by
   `record_placement`; the locking `validate_placement` wrapper is for
   external callers only. Any future registry function that calls
   another registry function must use a `_locked` variant, never
   re-acquire the lock. A second real failure found in the same run:
   the pid-death-before-register test failed because the register
   timeout branch only failed intents in status `launched`, while a
   claim START leaves the intent in status `yes` until register —
   fixed to fail `yes` or `launched`.

## 12. Coordinator urgent, 2026-10-26 — claim `--worker` and `test_join_gate_poll.py` 7/7

**Production launches failing closed at claim: expected, and the fix is
§5.6, not a gate revert.** Lane B's production launcher passes only
`--id/--script` to `claim`, so argparse fails (exit 2, no run, token
not consumed). That is the v1 contract working as frozen: claim without
identity must not start a run. The fix is Lane B's, in one place:
`geppetto-live` must accept `--gate-worker`/`--gate-session` and
forward them to `gate.py claim`; `launch_intent` (this lane, updated
with §5.6) already spawns the host launcher with those flags, and
Lane E must pass the filing worker/session through to worker-attach
invocations. No approval is lost by the failures so far, provided the
fix lands within the 600-second yes→claim window for each intent.

**`test_join_gate_poll.py` 7/7 — confirmed, B's 6/7 does not reproduce
and must not be carried as a stale failure.** Timeline, all on the PC:
first Lane A push → join-poll run 6/7, sole failure
`test_approval_is_single_use_and_expires` filing 0 intents (my
"script must not live in `cabinet/confirm`" guard false-positived
because that test points `CONFIRM_DIR` at its temp root, where its
script also lives). Fix in the same lane: guard narrowed to actual
registry files (`.json` inside the registry dir) only. Re-run after
the fix → **7/7, exit 0**, and registry suite still 28/28. The
coordinator's independent rerun just now → **7/7 OK**. Conclusion:
B's reported 6/7 predated the fix (B ran against pre-fix `gate.py`);
it is not a current failure, and the merge report should record 7/7.

## 13. Version

Contract v1, 2026-10-26, Lane A. Changes require the coordinator and the
Lane A owner; B/C/E must not invent a second intent/token schema. If this
file and `gate.py` disagree, `gate.py`'s tested behaviour wins and this
file must be corrected in the same change — a silent drift between doc and
code is a contract break, not a documentation nit.
