#!/usr/bin/env python3
"""Lane E fixture tests: Muse SSH bridge <-> Geppetto accept gate.

FIXTURE-ONLY. Never touches the real cabinet/muse-seat/requests,
responses, or confirm directories, never starts the visible chest,
never runs a paid provider, never uses the local `muse` CLI.

What is faked, and why it is still a meaningful test:
  * A fake gate.py (same CLI surface the bridge actually uses:
    preflight / file, plus an answer subcommand standing in for the
    host's Yes/No card) and a fake geppetto-live (same argv surface:
    script doc out --gate-token ID, --once rejection, token check,
    run registration, receipt write). The bridge under test is the
    REAL cabinet/muse-seat/gate_bridge.sh; only the gate and the
    launcher are stand-ins, because the real ones require the chest.
  * The REAL cabinet/muse-seat/in_progress.sh is used for claim /
    heartbeat / marker checks, with MUSE_SEAT_BASE pointed at a temp
    seat directory, so marker-loss and cancel behaviour is exercised
    against production code, not a second fake.
  * One test copies the REAL cabinet/muse_seat.py into a temp
    cabinet/ tree and runs it there, so its prompt cancel-stop is
    exercised without ever touching the real seat directories.

Temp directories live under ~/.cache/muse-gate-tests (or
$MUSE_GATE_TEST_ROOT), NOT /tmp: the PC's /tmp is a full tmpfs and
must not be used for test work.

Cases: Yes -> attach -> receipt -> response; No; yes-wait timeout;
cancel during wait (prompt stop); chest closed (immediate refusal);
edited script after filing; --once rejection; duplicate invocation
returns the existing run (no second live call); marker lost; an old
queued response is not mistaken for the new turn's work; Pip-like
explicit --doc/--out; attach timeout is bounded; seat stops on a
cancel record instead of polling 19 minutes.
"""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

HOME = Path.home()
TEST_ROOT = Path(os.environ.get("MUSE_GATE_TEST_ROOT", HOME / ".cache" / "muse-gate-tests"))
BRIDGE = Path(os.environ.get(
    "MUSE_GATE_BRIDGE",
    HOME / "Desktop" / "geppetto-desktop" / "cabinet" / "muse-seat" / "gate_bridge.sh"))
REAL_SEAT_PY = Path(os.environ.get(
    "MUSE_SEAT_PY",
    HOME / "Desktop" / "geppetto-desktop" / "cabinet" / "muse_seat.py"))
REAL_IN_PROGRESS = Path(os.environ.get(
    "MUSE_IN_PROGRESS_SH",
    HOME / "Desktop" / "geppetto-desktop" / "cabinet" / "muse-seat" / "in_progress.sh"))

SCRIPT_TEXT = """material 0 color=#c98a6b
add Primitive name=Shaft shape=Cylinder radius=1 height=5 position=0,0,2.5 materialslot=0
save test.effigy
"""

FAKE_GATE = '''#!/usr/bin/env python3
"""Fake gate.py: preflight / file / answer, same surface gate_bridge.sh uses.

Confirm dir is <this script's dir>/confirm. Slots (occupied panes) are
read from <dir>/slots.json (a JSON list, default []). Filing records
its arguments in <dir>/last_file.json so tests can assert the exact
--doc/--out the bridge passed.
"""
import hashlib, json, os, sys, time
from pathlib import Path

HERE = Path(__file__).resolve().parent
CONFIRM = HERE / "confirm"
CONFIRM.mkdir(parents=True, exist_ok=True)

def sha(text):
    return hashlib.sha256(text.encode()).hexdigest()

def main(argv):
    cmd = argv[1]
    args = {}
    i = 2
    while i < len(argv):
        if argv[i].startswith("--"):
            args[argv[i][2:]] = argv[i + 1] if i + 1 < len(argv) else ""
            i += 2
        else:
            i += 1
    if cmd == "preflight":
        text = Path(args["script"]).read_text()
        if "PREFLIGHT_FAIL" in text:
            print(json.dumps({"ok": False, "errors": ["fake preflight fail"]}))
        else:
            print(json.dumps({"ok": True, "errors": [], "lines": len(text.splitlines())}))
        return 0
    if cmd == "file":
        text = Path(args["script"]).read_text()
        (HERE / "last_file.json").write_text(json.dumps(args, sort_keys=True))
        try:
            occupied = json.loads((HERE / "slots.json").read_text())
        except Exception:
            occupied = []
        requested = int(args.get("pane") or 1)
        assigned = None
        for k in range(4):
            cand = ((requested - 1 + k) % 4) + 1
            if cand not in occupied:
                assigned = cand
                break
        if assigned is None:
            print("REFUSED: all four viewports are occupied", file=sys.stderr)
            return 2
        iid = "gate-%d-%s" % (int(time.time()), sha(args["subject"] + sha(text) + str(time.time_ns()))[:8])
        out = args.get("out") or str(Path(args["script"]).parent)
        intent = {"id": iid, "status": "pending", "subject": args["subject"],
                  "script": os.path.abspath(args["script"]),
                  "doc": args.get("doc") or "-", "out": os.path.abspath(out),
                  "script_hash": sha(text), "worker": args.get("worker", ""),
                  "pane": str(assigned), "created_at": time.time()}
        (CONFIRM / (iid + ".json")).write_text(json.dumps(intent, indent=2))
        print(json.dumps(intent, indent=2))
        return 0
    if cmd == "answer":
        iid, ans = args["id"], args["answer"]
        ipath = CONFIRM / (iid + ".json")
        intent = json.loads(ipath.read_text())
        if ans == "yes":
            text = Path(intent["script"]).read_text()
            if sha(text) != intent["script_hash"]:
                print("script changed before Yes", file=sys.stderr)
                return 2
            tok = {"id": iid, "script": intent["script"], "script_hash": intent["script_hash"],
                   "subject": intent["subject"], "yes_at": time.time(), "used_at": None}
            (CONFIRM / (iid + ".token.json")).write_text(json.dumps(tok, indent=2))
            intent["status"] = "yes"
            ipath.write_text(json.dumps(intent, indent=2))
            print(json.dumps(tok, indent=2))
        else:
            intent["status"] = "cancelled"
            ipath.write_text(json.dumps(intent, indent=2))
            print("cancelled: not modeled", file=sys.stderr)
        return 0
    print("unknown fake-gate command", file=sys.stderr)
    return 2

if __name__ == "__main__":
    sys.exit(main(sys.argv))
'''

FAKE_LIVE = '''#!/usr/bin/env python3
"""Fake geppetto-live: script doc out --gate-token ID [--once].

Token file is <this script's dir>/confirm/<id>.token.json. On a first
call it creates a run dir, a start answer, and a receipt in
GEPPETTO_BUILD_RECEIPTS (revision 2, this turn's token). On a repeat
call for an already-registered token it prints the existing run and
creates nothing new. --once exits 2. FAKE_LIVE_SLEEP delays the run
so the attach-timeout case can be exercised.
"""
import hashlib, json, os, sys, time, uuid
from pathlib import Path

HERE = Path(__file__).resolve().parent
CONFIRM = HERE / "confirm"

def main(argv):
    args, token, once = [], None, False
    i = 1
    while i < len(argv):
        if argv[i] == "--gate-token":
            token = argv[i + 1]; i += 2
        elif argv[i] == "--once":
            once = True; i += 1
        else:
            args.append(argv[i]); i += 1
    with open(HERE / "live_invocations.log", "a") as f:
        f.write("call token=%s once=%s\\n" % (token, once))
    if once:
        print("fake live: --once not supported", file=sys.stderr)
        return 2
    script, doc, out = (args + ["-", "-"])[:3]
    if not token:
        print("fake live: no gate token", file=sys.stderr)
        return 2
    tpath = CONFIRM / (token + ".token.json")
    if not tpath.exists():
        print("fake live: no yes token", file=sys.stderr)
        return 2
    tok = json.loads(tpath.read_text())
    text = Path(script).read_text()
    if hashlib.sha256(text.encode()).hexdigest() != tok["script_hash"]:
        print("fake live: script hash mismatch", file=sys.stderr)
        return 2
    if tok.get("run"):
        print("run: %s" % tok["run"])
        return 0
    time.sleep(float(os.environ.get("FAKE_LIVE_SLEEP", "0")))
    run = HERE / "runs" / ("script-%s-%d-%d" % (Path(script).stem, int(time.time()), os.getpid()))
    (run / ".live" / "answers").mkdir(parents=True)
    (run / ".live" / "answers" / "start.txt").write_text("run is open\\n")
    tok["used_at"] = time.time()
    tok["run"] = str(run)
    tok["registered_at"] = time.time()
    tmp = str(tpath) + ".tmp"
    Path(tmp).write_text(json.dumps(tok, indent=2))
    os.replace(tmp, tpath)
    receipts = os.environ.get("GEPPETTO_BUILD_RECEIPTS")
    rtoken = os.environ.get("GEPPETTO_BUILD_TOKEN")
    if receipts and rtoken:
        Path(receipts).mkdir(parents=True, exist_ok=True)
        item = {"token": rtoken, "run": str(run), "runId": run.name,
                "revision": 2, "operation": "build",
                "scriptHash": tok["script_hash"], "output": "test.effigy"}
        name = "%020d-%s.receipt.json" % (time.time_ns(), uuid.uuid4().hex)
        Path(receipts, name).write_text(json.dumps(item))
    print("run: %s" % run)
    return 0

if __name__ == "__main__":
    sys.exit(main(sys.argv))
'''


def run_cmd(cmd, env, timeout=60):
    return subprocess.run(cmd, env=env, capture_output=True, text=True, timeout=timeout)


class GateBridgeTest(unittest.TestCase):
    def setUp(self):
        TEST_ROOT.mkdir(parents=True, exist_ok=True)
        self.tmp = Path(tempfile.mkdtemp(prefix="laneE-", dir=str(TEST_ROOT)))
        self.fake_dir = self.tmp / "fake"
        self.fake_dir.mkdir()
        (self.fake_dir / "confirm").mkdir()
        (self.fake_dir / "runs").mkdir()
        self.fake_gate = self.fake_dir / "fake_gate.py"
        self.fake_gate.write_text(FAKE_GATE)
        self.fake_live = self.fake_dir / "fake_live.py"
        self.fake_live.write_text(FAKE_LIVE)
        # The bridge execs the launcher directly (as it execs the real,
        # executable geppetto-live), so the fakes must be executable too.
        os.chmod(self.fake_gate, 0o755)
        os.chmod(self.fake_live, 0o755)
        (self.fake_dir / "live_invocations.log").write_text("")
        self.seat = self.tmp / "seat"
        for sub in ("requests", "responses", "status", "cancel", "in-progress", "gate"):
            (self.seat / sub).mkdir(parents=True)
        self.receipts = self.tmp / "receipts"
        self.receipts.mkdir()
        self.script_dir = self.tmp / "scripts"
        self.script_dir.mkdir()
        self.out_dir = self.tmp / "out"  # request cwd: deliberately != script_dir
        self.out_dir.mkdir()
        self.script = self.script_dir / "build.txt"
        self.script.write_text(SCRIPT_TEXT)
        self.turn = "testturn01"
        (self.seat / "requests" / (self.turn + ".json")).write_text(json.dumps({
            "turn_id": self.turn, "session_id": "muse-" + self.turn,
            "mode": "build", "cwd": str(self.out_dir)}))
        self.env = dict(os.environ)
        self.env.update({
            "MUSE_SEAT_BASE": str(self.seat),
            "MUSE_GATE_PY": str(self.fake_gate),
            "MUSE_GATE_LIVE": str(self.fake_live),
            "MUSE_GATE_HOST_CMD": "true",
            "MUSE_GATE_IN_PROGRESS": str(REAL_IN_PROGRESS),
            "MUSE_GATE_POLL_SECS": "0.1",
            "MUSE_GATE_POLL_TIMEOUT": "600",
            "MUSE_GATE_ATTACH_TIMEOUT": "600",
            "GEPPETTO_BUILD_RECEIPTS": str(self.receipts),
            "GEPPETTO_BUILD_TOKEN": "test-token-123",
        })
        self._claim()

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    # -- helpers -----------------------------------------------------
    def _claim(self):
        r = run_cmd(["bash", str(REAL_IN_PROGRESS), "claim", self.turn, "cron"],
                    {**self.env, "MUSE_SEAT_BASE": str(self.seat)})
        self.assertTrue(r.stdout.strip().startswith("CLAIMED"), r.stdout + r.stderr)

    def _bridge_args(self, extra=None):
        args = ["bash", str(BRIDGE), "--turn", self.turn, "--script", str(self.script),
                "--subject", "test model", "--worker", "muse-test", "--pane", "1",
                "--doc", "-", "--out", str(self.out_dir), "--owner", "cron"]
        if extra:
            args += extra
        return args

    def _start_bridge(self, env_overrides=None, extra=None):
        env = dict(self.env)
        if env_overrides:
            env.update(env_overrides)
        return subprocess.Popen(self._bridge_args(extra), env=env,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)

    def _wait_filed(self, proc, timeout=15):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if proc.poll() is not None:
                raise AssertionError("bridge exited early: %s" % proc.communicate())
            m = self.seat / "gate" / (self.turn + ".json")
            if m.exists():
                d = json.loads(m.read_text())
                if d.get("status") == "filed" and d.get("intent_id"):
                    return d["intent_id"]
            time.sleep(0.05)
        raise AssertionError("bridge did not file an intent in %ss" % timeout)

    def _answer(self, intent_id, ans):
        r = run_cmd(["python3", str(self.fake_gate), "answer",
                     "--id", intent_id, "--answer", ans], self.env)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def _live_calls(self):
        return len([l for l in (self.fake_dir / "live_invocations.log").read_text().splitlines() if l.strip()])

    def _receipts(self):
        return sorted(self.receipts.glob("*.receipt.json"))

    def _map(self):
        p = self.seat / "gate" / (self.turn + ".json")
        return json.loads(p.read_text()) if p.exists() else {}

    def _yes_flow(self):
        """Drive a full Yes flow; returns (stdout-json dict, map dict)."""
        proc = self._start_bridge()
        intent_id = self._wait_filed(proc)
        self._answer(intent_id, "yes")
        out, err = proc.communicate(timeout=30)
        self.assertEqual(proc.returncode, 0, "bridge failed: " + err)
        result = json.loads(out.strip().splitlines()[-1])
        self.assertEqual(result["intent_id"], intent_id)
        return result, self._map()

    # -- cases -------------------------------------------------------
    def test_yes_attach_receipt_mapping_and_explicit_out(self):
        result, m = self._yes_flow()
        self.assertEqual(self._live_calls(), 1, "exactly one live call; no second run")
        self.assertEqual(m["status"], "attached")
        self.assertEqual(m["run"], result["run"])
        self.assertTrue(Path(result["receipt"]).exists())
        rec = json.loads(Path(result["receipt"]).read_text())
        self.assertEqual(rec["token"], "test-token-123")
        self.assertGreaterEqual(rec["revision"], 2)
        self.assertEqual(rec["run"], result["run"])
        # --out is the request cwd, explicitly, NOT the script directory.
        last = json.loads((self.fake_dir / "last_file.json").read_text())
        self.assertEqual(last["out"], str(self.out_dir))
        self.assertNotEqual(last["out"], str(self.script_dir))
        self.assertEqual(last["doc"], "-")
        # The caller (not the bridge) writes the response after attach.
        resp = self.seat / "responses" / (self.turn + ".json")
        resp.write_text(json.dumps({"result": "built in " + result["run"],
                                    "session_id": "muse-" + self.turn}))
        self.assertEqual(json.loads(resp.read_text())["session_id"], "muse-" + self.turn)

    def test_no_answer_no_run_no_receipt(self):
        proc = self._start_bridge()
        intent_id = self._wait_filed(proc)
        self._answer(intent_id, "no")
        out, err = proc.communicate(timeout=30)
        self.assertEqual(proc.returncode, 4, out + err)
        self.assertEqual(self._live_calls(), 0)
        self.assertEqual(self._receipts(), [])
        self.assertEqual(self._map()["status"], "cancelled")

    def test_yes_wait_timeout(self):
        proc = self._start_bridge(env_overrides={"MUSE_GATE_POLL_TIMEOUT": "2"})
        out, err = proc.communicate(timeout=30)
        self.assertEqual(proc.returncode, 3, out + err)
        self.assertEqual(self._live_calls(), 0)
        self.assertEqual(self._map()["status"], "timeout")

    def test_cancel_during_wait_stops_promptly(self):
        proc = self._start_bridge()
        self._wait_filed(proc)
        t0 = time.monotonic()
        (self.seat / "cancel" / (self.turn + ".json")).write_text(
            json.dumps({"turn_id": self.turn, "reason": "user-stopped"}))
        out, err = proc.communicate(timeout=30)
        self.assertLess(time.monotonic() - t0, 5.0, "cancel must stop the wait promptly, not in 19 min")
        self.assertEqual(proc.returncode, 4, out + err)
        self.assertEqual(self._live_calls(), 0)
        self.assertEqual(self._map()["status"], "cancelled")

    def test_chest_closed_refuses_immediately(self):
        t0 = time.monotonic()
        r = run_cmd(self._bridge_args(), {**self.env, "MUSE_GATE_HOST_CMD": "false"}, timeout=30)
        self.assertLess(time.monotonic() - t0, 5.0)
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertEqual(list((self.fake_dir / "confirm").glob("gate-*.json")), [])
        self.assertEqual(self._live_calls(), 0)

    def test_edited_script_after_yes_refused(self):
        # Deterministic via the bridge's test-only pre-attach delay:
        # Yes lands, the script is edited inside the delay window, and
        # the attach-time hash re-check must refuse before any live call.
        proc = self._start_bridge(env_overrides={"MUSE_GATE_PRE_ATTACH_DELAY": "2"})
        intent_id = self._wait_filed(proc)
        self._answer(intent_id, "yes")
        time.sleep(0.5)
        self.script.write_text(SCRIPT_TEXT + "# changed after yes\n")
        out, err = proc.communicate(timeout=30)
        self.assertEqual(proc.returncode, 2, out + err)
        self.assertEqual(self._live_calls(), 0)
        self.assertEqual(self._receipts(), [])

    def test_once_rejected(self):
        r = run_cmd(self._bridge_args(extra=["--once"]), self.env, timeout=30)
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertEqual(list((self.fake_dir / "confirm").glob("gate-*.json")), [])
        self.assertEqual(self._live_calls(), 0)

    def test_duplicate_invocation_returns_existing_run(self):
        result, _ = self._yes_flow()
        self.assertEqual(self._live_calls(), 1)
        r = run_cmd(self._bridge_args(), self.env, timeout=30)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        again = json.loads(r.stdout.strip().splitlines()[-1])
        self.assertEqual(again["run"], result["run"])
        self.assertEqual(self._live_calls(), 1, "duplicate invocation must not start a second run")

    def test_marker_lost_stops_wait(self):
        proc = self._start_bridge()
        self._wait_filed(proc)
        (self.seat / "in-progress" / (self.turn + ".json")).unlink()
        out, err = proc.communicate(timeout=30)
        self.assertEqual(proc.returncode, 4, out + err)
        self.assertEqual(self._live_calls(), 0)

    def test_old_response_not_mistaken_for_new_turn(self):
        old = self.seat / "responses" / "oldturn99.json"
        old.write_text(json.dumps({"result": "old answer", "session_id": "muse-oldturn99"}))
        before = old.read_text()
        result, m = self._yes_flow()
        self.assertEqual(old.read_text(), before, "old response must be untouched")
        self.assertNotIn("oldturn99", json.dumps(m))
        self.assertNotEqual(result["run"], "oldturn99")

    def test_pip_like_doc_and_out_explicit(self):
        doc_dir = self.tmp / "pip-live"
        doc_dir.mkdir()
        doc = doc_dir / "pants.effigy"
        doc.write_text("fake pip doc\n")
        args = ["bash", str(BRIDGE), "--turn", self.turn, "--script", str(self.script),
                "--subject", "pip edit", "--worker", "muse-test", "--pane", "1",
                "--doc", str(doc), "--out", str(doc_dir), "--owner", "cron"]
        proc = subprocess.Popen(args, env=self.env, stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE, text=True)
        intent_id = self._wait_filed(proc)
        self._answer(intent_id, "yes")
        out, err = proc.communicate(timeout=30)
        self.assertEqual(proc.returncode, 0, err)
        last = json.loads((self.fake_dir / "last_file.json").read_text())
        self.assertEqual(last["doc"], str(doc))
        self.assertEqual(last["out"], str(doc_dir))
        self.assertEqual(self._receipts() != [], True)

    def test_attach_timeout_bounded(self):
        proc = self._start_bridge(env_overrides={
            "MUSE_GATE_ATTACH_TIMEOUT": "1", "FAKE_LIVE_SLEEP": "5"})
        intent_id = self._wait_filed(proc)
        self._answer(intent_id, "yes")
        t0 = time.monotonic()
        out, err = proc.communicate(timeout=30)
        self.assertLess(time.monotonic() - t0, 10.0)
        self.assertEqual(proc.returncode, 5, out + err)
        self.assertEqual(self._receipts(), [], "a timed-out attach must not leave a receipt behind")

    def test_seat_stops_on_cancel_promptly(self):
        if not REAL_SEAT_PY.exists():
            self.skipTest("real muse_seat.py not present at %s" % REAL_SEAT_PY)
        cab = self.tmp / "cabinet"
        seat = cab / "muse-seat"
        for sub in ("requests", "responses", "status", "cancel"):
            (seat / sub).mkdir(parents=True)
        shutil.copyfile(REAL_SEAT_PY, cab / "muse_seat.py")
        proc = subprocess.Popen(["python3", str(cab / "muse_seat.py"),
                                 "--provider", "muse", "--model", "muse-bridge"],
                                stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE, text=True)
        proc.stdin.write("hello\n")
        proc.stdin.close()
        # First stdout line is the system event carrying the turn_id.
        line = proc.stdout.readline()
        turn = json.loads(line)["turn_id"]
        (seat / "cancel" / (turn + ".json")).write_text(
            json.dumps({"turn_id": turn, "reason": "user-stopped"}))
        rc = proc.wait(timeout=15)
        self.assertEqual(rc, 1, "seat must exit on cancel, not poll 19 minutes")
        status = json.loads((seat / "status" / (turn + ".json")).read_text())
        self.assertEqual(status["stage"], "Cancelled")


if __name__ == "__main__":
    unittest.main()
