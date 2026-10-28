"""Gate dispatch: pane-card (in-process) Yes must not socket-check itself.

Regression for 2026-10-26 05:03 / 05:10: a Yes answered on the in-pane
card A runs answer_intent(launch=True) inside the host UI thread, and the
old launch_intent then ran `host.py gate-check` as a subprocess whose
reply could only come from that same blocked UI thread -> 2 s timeout
-> "" -> "reserved viewport is gone", intent cancelled, no run.

These tests run against a temp registry only, never the real
cabinet/confirm dir, never the chest, never a paid provider.
The in-process module is loaded exactly the way host.py loads it
(importlib spec name "geppetto_gate"); the outside-process module is
the normal "gate" import.
"""
import importlib.util
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "cabinet"))

import gate  # noqa: E402

SCRIPT = "add Primitive name=Body shape=Box materialslot=0\nsave model.effigy\n"


def load_inprocess(cabinet_dir):
    spec = importlib.util.spec_from_file_location(
        "geppetto_gate", str(Path(cabinet_dir) / "gate.py"))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


class FakeHost:
    """Only gate-reserve; gate-check must never be reached in-process."""

    def __init__(self):
        self.slots = {}
        self.check_calls = []

    def __call__(self, *args):
        if args[0] == "gate-reserve":
            iid, preferred = args[1], int(args[2])
            if iid in self.slots:
                return str(self.slots[iid])
            for n in list(range(preferred, 5)) + list(range(1, preferred)):
                if n not in self.slots.values():
                    self.slots[iid] = n
                    return str(n)
            return "FULL"
        if args[0] == "gate-check":
            self.check_calls.append(args[1])
            raise AssertionError("in-process launch must not call gate-check")
        if args[0] == "gate-release":
            self.slots.pop(args[1], None)
            return "ok"
        return ""


def write_yes_intent(confirm, iid, pane, worker="worker-a", session="sess-1"):
    d = {"id": iid, "status": "yes", "subject": "manual", "script": "/tmp/build.txt",
         "doc": "-", "out": "/tmp", "script_hash": "x", "worker": worker,
         "session": session, "pane": str(pane), "created_at": 1000000000.0,
         "preflight": {"ok": True}}
    (confirm / (iid + ".json")).write_text(json.dumps(d))
    return d


class InProcessLaunchTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.confirm = self.root / "confirm"
        self.confirm.mkdir()
        self.cabinet = Path(__file__).resolve().parents[1] / "cabinet"
        self.mod = load_inprocess(self.cabinet)
        self.mod.CONFIRM_DIR = str(self.confirm)
        self.mod.AUDIT = str(self.confirm / "audit.jsonl")
        self.host = FakeHost()
        self.patch = patch.object(self.mod, "_host_command", self.host)
        self.patch.start()
        self.script = self.root / "build.txt"
        self.script.write_text(SCRIPT)

    def tearDown(self):
        self.patch.stop()
        self.tmp.cleanup()

    def test_inprocess_yes_dispatches_without_gate_check(self):
        intent, err = self.mod.file_intent(
            str(self.script), "test subject", "worker-a", "2", "-", "", "sess-1")
        self.assertIsNotNone(intent, err)
        self.assertEqual(intent["pane"], "2")
        with patch.object(self.mod.subprocess, "Popen",
                          return_value=MagicMock()) as popen:
            _tok, launch_err = self.mod.answer_intent(
                intent["id"], "yes", launch=True)
        self.assertEqual(launch_err, "")
        self.assertEqual(self.host.check_calls, [])
        popen.assert_called_once()
        argv = popen.call_args.args[0]
        self.assertIn("--gate-token", argv)
        self.assertEqual(argv[argv.index("--gate-worker") + 1], "worker-a")
        self.assertEqual(argv[argv.index("--gate-session") + 1], "sess-1")

    def test_inprocess_yes_refused_when_another_intent_holds_the_pane(self):
        # Two active intents on pane 2, crafted with deterministic ids:
        # _active_panes iterates sorted filenames, so the later id wins
        # the pane and the earlier intent must be refused.
        write_yes_intent(self.confirm, "gate-1000000001-aaaa0001", "2")
        write_yes_intent(self.confirm, "gate-1000000002-aaaa0002", "2",
                         worker="worker-b", session="sess-2")
        with patch.object(self.mod.subprocess, "Popen",
                          return_value=MagicMock()) as popen:
            err = self.mod.launch_intent("gate-1000000001-aaaa0001")
        self.assertIn("reserved viewport is gone", err)
        popen.assert_not_called()

    def test_outside_process_yes_still_uses_socket_check(self):
        # Normal "gate" import with a socket stub: check returning "0"
        # (slot gone) refuses; check returning the pane dispatches.
        old_dir, old_audit = gate.CONFIRM_DIR, gate.AUDIT
        gate.CONFIRM_DIR = str(self.confirm)
        gate.AUDIT = str(self.confirm / "audit.jsonl")
        try:
            slots = {}

            def stub(*args):
                if args[0] == "gate-reserve":
                    slots[args[1]] = int(args[2])
                    return str(args[2])
                if args[0] == "gate-check":
                    return str(slots.get(args[1], 0))
                if args[0] == "gate-release":
                    slots.pop(args[1], None)
                    return "ok"
                return ""

            with patch.object(gate, "_host_command", stub):
                intent, err = gate.file_intent(
                    str(self.script), "socket path", "worker-a",
                    "2", "-", "", "sess-1")
                self.assertIsNotNone(intent, err)
                tok, err = gate.answer_intent(intent["id"], "yes")
                self.assertIsNotNone(tok, err)
                slots.pop(intent["id"], None)  # simulate a lost slot
                with patch.object(gate.subprocess, "Popen",
                                  return_value=MagicMock()) as popen:
                    err = gate.launch_intent(intent["id"])
                self.assertIn("reserved viewport is gone", err)
                popen.assert_not_called()
        finally:
            gate.CONFIRM_DIR, gate.AUDIT = old_dir, old_audit


if __name__ == "__main__":
    unittest.main()
