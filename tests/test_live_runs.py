"""Headless checks for the cabinet's AI-run protocol; no Qt application or window."""
import importlib.util
import json
import os
import time
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

    def test_close_open_puts_a_close_in_the_inbox_in_ticks_order(self):
        with tempfile.TemporaryDirectory() as root:
            live.close_open(root)
            [name] = os.listdir(Path(root) / ".live/inbox")
            self.assertTrue(name.endswith(".close") and name[:19].isdigit())
            # the same clock as ScriptLive.Inbox.Put (DateTime ticks), so it sorts after lines sent before it
            self.assertAlmostEqual(int(name[:19]) / 1e7, time.time() + 62135596800, delta=5)
            self.assertEqual(live.activity({"state": "held"}), "held at a bad line")

    def test_notes_reach_the_modeller_once_with_the_step_line(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = os.path.join(tmp, "script-x")
            os.makedirs(os.path.join(root, ".live"))
            Path(root + ".json").write_text(json.dumps({"script": "/x/build.txt", "pid": 1}))
            Path(root, "script.txt").write_text("refimage top a.png\nadd Blob name=Eraser voxel=0.01\n")
            (Path(root) / ".live/latest.json").write_text(json.dumps({"schemaVersion": 1, "ev": "ai-live", "state": "open",
                                                                       "runId": "r", "sequence": 1}))
            live.add_note(root, 2, "line 2: add Blob name=Eraser", "the ends are too steep")
            text = live.wait_notes(root, sleep=lambda s: self.fail("a note was waiting"))
            self.assertIn("NOTE from po on step 2 (line 2): the ends are too steep", text)
            self.assertIn("that step: add Blob name=Eraser voxel=0.01", text)
            self.assertIn("geppetto-live more %s fix.txt" % root, text)
            self.assertEqual(live.take_notes(root), [])        # handed over once
            # nothing comes and the run closes: wait ends with None
            ticks = []
            def tick(_):
                ticks.append(1)
                (Path(root) / ".live/latest.json").write_text(json.dumps({"schemaVersion": 1, "ev": "ai-live", "state": "completed",
                                                                           "runId": "r", "sequence": 1}))
            self.assertIsNone(live.wait_notes(root, sleep=tick))
            self.assertEqual(len(ticks), 1)
            # a dead process ends the wait too
            (Path(root) / ".live/latest.json").write_text(json.dumps({"schemaVersion": 1, "ev": "ai-live", "state": "open",
                                                                       "runId": "r", "sequence": 1}))
            self.assertIsNone(live.wait_notes(root, sleep=lambda s: None, alive=lambda: False))


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


class WatchTests(unittest.TestCase):
    def test_the_part_a_line_works_on(self):
        for line, part in (("add Primitive shape=Box name=B position=3,0,0", "B"), ("add Blob 0,0,0 0.2", "Blob"),
                           ("set Eraser blend=0.02", "Eraser"), ("remove Strap", "Strap"), ("material 0 color=#fff", "material 0"),
                           ("echo hi", ""), ("# note", ""), ("refimage top 1 0", "refimage")):
            self.assertEqual(live.line_part(line), part, line)

    def test_watch_names_the_line_of_how_many_its_part_and_a_held_error(self):
        with tempfile.TemporaryDirectory() as root:
            os.makedirs(os.path.join(root, ".live", "revisions"))
            with open(os.path.join(root, "script.txt"), "w") as f:
                f.write("add Primitive shape=Box name=A\nset A size=2,1,1\nset Nope x=1\n")
            events = []
            for rev, op in ((1, "initial"), (2, "line 1: add Primitive shape=Box name=A"), (3, "line 2: set A size=2,1,1")):
                open(os.path.join(root, ".live", "revisions", "%08d-x.effigy" % rev), "w").close()
                events.append({"state": "revision", "operation": op, "snapshot": {"revision": rev, "path": ".live/revisions/%08d-x.effigy" % rev}})
            with open(os.path.join(root, ".live", "events.jsonl"), "w") as f:
                f.write("".join(json.dumps(e) + "\n" for e in events))
            w = live.watch(root, {"state": "held", "sequence": 9, "outcome": "line 3: no feature 'Nope'", "operation": None})
            self.assertEqual((w["line"], w["total"], w["part"]), (3, 3, "Nope"))    # held: the bad line, not the last good one
            self.assertEqual(w["code"], "set Nope x=1")
            self.assertIn("no feature 'Nope'", w["error"])
            w = live.watch(root, {"state": "open", "sequence": 10, "operation": None})
            self.assertEqual((w["line"], w["code"], w["part"]), (2, "set A size=2,1,1", "A"))
            self.assertEqual([s["part"] for s in w["steps"]], ["", "A", "A"])


if __name__ == '__main__':
    unittest.main()
