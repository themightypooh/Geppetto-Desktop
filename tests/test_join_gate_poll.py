"""Shared join intents remain visible without an active Muse SSH turn."""
import os
import json
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from concurrent.futures import ThreadPoolExecutor
from unittest.mock import patch
from pathlib import Path

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "cabinet"))

from host import Host  # noqa: E402
import chat  # noqa: E402
import claude_link  # noqa: E402
import gate  # noqa: E402
from chat_phase import BUILD_PREP_PROMPT  # noqa: E402


class SignalStub:
    def emit(self):
        pass


class TrackerStub:
    _muse_turn_id = ""
    _muse_tracker = {}
    _confirm_model = ""
    _confirm_turn = ""
    museTrackerChanged = SignalStub()
    confirmModelChanged = SignalStub()

    def __init__(self):
        self.polls = 0

    def _gate_poll_shared_intents(self):
        self.polls += 1


class JoinGatePollTests(unittest.TestCase):
    def test_preflight_accepts_real_nonprimitive_geometry(self):
        for feature in ("Profile", "Spline", "Blob", "Loft", "Sweep", "Sketch"):
            result = gate.preflight(f"add {feature} name=Body\nsave body.effigy\n")
            self.assertTrue(result["ok"], (feature, result["errors"]))
            self.assertEqual(result["first_shape"], feature)

    def test_approval_is_single_use_and_expires(self):
        with tempfile.TemporaryDirectory() as root:
            old = gate.CONFIRM_DIR, gate.AUDIT
            gate.CONFIRM_DIR = root
            gate.AUDIT = os.path.join(root, "audit.jsonl")
            script = os.path.join(root, "build.txt")
            with open(script, "w") as f:
                f.write("add Primitive name=Body shape=Box materialslot=0\nsave model.effigy\n")
            try:
                slots = iter((1, 2, 3))
                with patch.object(gate, "reserve_pane", side_effect=lambda *_: (next(slots), "")), \
                     patch.object(gate, "release_pane"):
                    with ThreadPoolExecutor(max_workers=2) as pool:
                        filed = list(pool.map(lambda _: gate.file_intent(script, "test", "worker", "1"), range(2)))
                    self.assertEqual(sum(intent is not None for intent, _ in filed), 2)
                    self.assertEqual(len({intent["id"] for intent, _ in filed}), 2)
                    intent = filed[0][0]
                    gate.answer_intent(filed[1][0]["id"], "no")
                    approved_at = time.monotonic()
                    gate.answer_intent(intent["id"], "yes")
                    with ThreadPoolExecutor(max_workers=2) as pool:
                        results = list(pool.map(lambda _: gate.claim_token(intent["id"], script), range(2)))
                    self.assertLess(time.monotonic() - approved_at, 5,
                                    "approval must make the exact script claimable within 5 seconds")
                    self.assertEqual(sum(state == "START" for state, _ in results), 1)
                    self.assertEqual(sum(state == "WAIT" for state, _ in results), 1)
                    other, error = gate.file_intent(script, "test again", "worker", "1")
                    self.assertFalse(error)
                    gate.answer_intent(other["id"], "yes")
                token_path = os.path.join(root, other["id"] + ".token.json")
                with open(token_path) as f:
                    token = json.load(f)
                token["yes_at"] -= gate.START_TIMEOUT_S + 1
                with open(token_path, "w") as f:
                    json.dump(token, f)
                self.assertFalse(gate.verify_token(other["id"], script)[0])
            finally:
                gate.CONFIRM_DIR, gate.AUDIT = old

    def test_blocked_cli_turn_is_killed_on_cancel(self):
        proc = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"],
                                start_new_session=True)
        cancel = threading.Event()
        done = chat._turn_guard(proc, cancel, 3600)
        try:
            chat.cancel_owned_cli_turns()  # fast cabinet close before guard can wake
            proc.wait(timeout=3)
            self.assertNotEqual(proc.returncode, 0)
        finally:
            cancel.set()
            done.set()
            chat._kill_owned_group(proc)
            with chat._owned_cli_lock:
                chat._owned_cli_turns.discard(proc)
            if proc.poll() is None:
                proc.kill()
            proc.wait()

    def test_every_tool_capable_cabinet_seat_gets_the_same_prep(self):
        self.assertIn(BUILD_PREP_PROMPT, chat.CLAUDE_CHAT)
        self.assertIn(BUILD_PREP_PROMPT, claude_link.PROMPT)
        self.assertIn(BUILD_PREP_PROMPT, chat._chat_system({}, "build"))
        self.assertNotIn(BUILD_PREP_PROMPT, chat._chat_system({}, "plan"))
        for phrase in ("choose your own modeling approach", "tool documentation", "preflight", "After Yes"):
            self.assertIn(phrase, BUILD_PREP_PROMPT)

    def test_poll_without_muse_status(self):
        stub = TrackerStub()
        Host.refreshMuseTracker(stub)
        self.assertEqual(stub.polls, 1)

    def test_muse_tracker_does_not_erase_shared_intent(self):
        stub = TrackerStub()

        def pending_gate():
            stub.polls += 1
            stub._confirm_turn = "gate-test"
            stub._confirm_model = "test subject"

        stub._gate_poll_shared_intents = pending_gate
        Host.refreshMuseTracker(stub)
        self.assertEqual(stub._confirm_turn, "gate-test")
        self.assertEqual(stub._confirm_model, "test subject")

    def test_gate_answer_does_not_write_a_muse_answer(self):
        # A shared gate ID must take only the shared answer path, not the
        # legacy Muse confirm/<turn>.answer.json path underneath it.
        stub = type("AnswerStub", (), {})()
        stub._confirm_turn = "gate-test"
        stub._confirm_model = "test"
        stub._join_gate = {}
        stub.joinGateChanged = SignalStub()
        stub.confirmModelChanged = SignalStub()
        stub._muse_write_json_atomic = lambda *_: self.fail("wrote a Muse answer")
        Host._confirm_write_answer(stub, "no")
        self.assertEqual(stub._confirm_turn, "")
        self.assertEqual(stub._confirm_model, "")


if __name__ == "__main__":
    unittest.main()
