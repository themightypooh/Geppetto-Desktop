"""Headless checks for the cabinet's AI-run protocol; no Qt application or window."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("live_runs", Path(__file__).resolve().parents[1] / "cabinet/live_runs.py")
live = importlib.util.module_from_spec(spec)
spec.loader.exec_module(live)


class LiveRunsTests(unittest.TestCase):
    def test_recovery_paths_and_cancel_identity(self):
        with tempfile.TemporaryDirectory(prefix="geppetto-live-host-") as directory:
            root = Path(directory)
            revisions = root / ".live/revisions"
            revisions.mkdir(parents=True)
            model = revisions / "1.effigy"
            model.write_text('{}')
            state = {"schemaVersion": 1, "ev": "ai-live", "runId": "owned", "sequence": 1,
                     "state": "building", "operation": "add_feature",
                     "snapshot": {"revision": 1, "path": ".live/revisions/1.effigy"}}
            (root / ".live/events.jsonl").write_text(json.dumps(state) + '\n{torn')
            self.assertEqual(live.read_state(root), state)
            self.assertEqual(live.preview_path(root, state), str(model))
            self.assertEqual(live.activity(state), "add feature")
            live.request_cancel(root)
            control = json.loads((root / ".live/control.json").read_text())
            self.assertEqual(control["runId"], "owned")
            self.assertEqual(control["command"], "cancel")
            model.unlink()
            model.symlink_to('/etc/passwd')
            self.assertEqual(live.preview_path(root, state), "")
            state["snapshot"]["path"] = ".live/revisions/../../../etc/passwd.effigy"
            self.assertEqual(live.preview_path(root, state), "")

    def test_steering_commands_are_durable_validated_and_ordered(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / ".live").mkdir()
            state = {"schemaVersion": 1, "ev": "ai-live", "runId": "owned", "sequence": 2, "state": "waiting",
                     "control": {"pauseRequested": True, "paused": False, "paceSeconds": 3}}
            (root / ".live/latest.json").write_text(json.dumps(state))
            self.assertEqual(live.activity(state), "pausing after current operation")
            first = live.send_command(root, "pause")
            second = live.send_command(root, "instruct", text="  taller  ")
            third = live.send_command(root, "pace", seconds=5)
            self.assertLess(first, second)
            self.assertLess(second, third)
            files = sorted((root / ".live/commands").iterdir())
            self.assertEqual([f.stem for f in files], [first, second, third])
            commands = [json.loads(f.read_text()) for f in files]
            self.assertEqual(commands[1]["text"], "taller")
            self.assertEqual(commands[2]["seconds"], 5.0)
            self.assertTrue(all(c["runId"] == "owned" and c["schemaVersion"] == 1 for c in commands))
            for bad in ({"seconds": 31}, {"seconds": float("nan")}, {"seconds": True}, {"seconds": "3"}):
                self.assertRaises(ValueError, live.send_command, root, "pace", **bad)
            self.assertRaises(ValueError, live.send_command, root, "instruct", text=" ")
            self.assertRaises(ValueError, live.send_command, root, "instruct", text="x" * 2001)
            self.assertEqual([live.next_pace(p) for p in (0, 1, 3, 5, 10, 2)], [1, 3, 5, 10, 0, 3])
            state["state"] = "completed"
            (root / ".live/latest.json").write_text(json.dumps(state))
            self.assertRaises(ValueError, live.send_command, root, "resume")
            self.assertEqual(live.activity(state), "completed")

    def test_bad_state_is_not_a_run(self):
        with tempfile.TemporaryDirectory() as root:
            self.assertEqual(live.read_state(root), {})
            (Path(root) / ".live").mkdir()
            (Path(root) / ".live/latest.json").write_text('[1, 2]')
            self.assertEqual(live.read_state(root), {})
            (Path(root) / ".live/latest.json").write_text(json.dumps({"schemaVersion": 1, "ev": "ai-live", "snapshot": []}))
            self.assertEqual(live.read_state(root), {})
            self.assertRaises(ValueError, live.request_cancel, root)


class HostBindingTests(unittest.TestCase):
    def test_watch_detach_and_stop_use_existing_process(self):
        # QCoreApplication exercises QObject bindings and timers without a GUI/window.
        import sys
        from unittest.mock import patch
        sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "cabinet"))
        import host
        from PySide6.QtCore import QCoreApplication, QProcess
        app = QCoreApplication.instance() or QCoreApplication([])

        class FakeProcess:
            def __init__(self): self.commands = []
            def state(self): return QProcess.Running
            def write(self, data): self.commands.append(data.decode().strip())

        with tempfile.TemporaryDirectory() as root:
            run = Path(root) / "one"
            (run / ".live/revisions").mkdir(parents=True)
            state = {"schemaVersion": 1, "ev": "ai-live", "runId": "owned", "sequence": 1,
                     "state": "building", "operation": "add_feature", "snapshot": {"revision": 1, "path": ".live/revisions/one.effigy"}}
            (run / ".live/latest.json").write_text(json.dumps(state))
            (run / ".live/revisions/one.effigy").write_text('{}')
            with patch.object(host, "RUNS", root):
                obj = host.Host()
                obj.runTimer.stop()
                obj.proc = FakeProcess()
                obj.jobs[str(run)] = obj.proc
                obj.watchRun(str(run))
                self.assertEqual(obj.proc.commands[-1], "follow " + str(run))
                self.assertEqual(obj.watching, str(run))
                self.assertEqual(obj._runs[0]["latestRevision"], 1)
                obj.stopWatching()
                self.assertEqual(obj.proc.commands[-1], "follow")
                self.assertIn(str(run), obj.jobs)  # detach does not terminate the AI
                obj.togglePause(str(run))
                obj.cyclePace(str(run))
                obj.sendDirection(str(run), "narrower shoulders")
                sent = [json.loads(f.read_text()) for f in sorted((run / ".live/commands").iterdir())]
                self.assertEqual([c["command"] for c in sent], ["pause", "pace", "instruct"])
                self.assertEqual(sent[1]["seconds"], 1.0)
                obj.stopResearch(str(run))
                self.assertTrue((run / ".live/control.json").is_file())
                self.assertEqual(obj.watching, "")


if __name__ == '__main__':
    unittest.main()
