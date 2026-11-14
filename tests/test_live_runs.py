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


class StopHoldTests(unittest.TestCase):
    """STOP button path: halt at the step boundary, stopped-step record, resume."""

    def _make_run(self, root, state_name="building", rev=3):
        run = Path(root) / "run-stop"
        live_dir = run / ".live"
        (live_dir / "revisions").mkdir(parents=True)
        (live_dir / "revisions" / ("%08d-x.effigy" % rev)).write_text("{}")
        script = str(run / "build.txt")
        Path(script).write_text("add Primitive shape=Box name=A\n")
        (run.with_suffix(".json")).write_text(json.dumps({"script": script, "pid": 0}))
        state = {"schemaVersion": 1, "ev": "ai-live", "runId": "owned", "sequence": 1,
                 "state": state_name, "operation": "add_feature",
                 "snapshot": {"revision": rev, "path": ".live/revisions/%08d-x.effigy" % rev},
                 "control": {}}
        (live_dir / "latest.json").write_text(json.dumps(state))
        events = []
        for r, op in ((1, "initial"),
                      (2, "line 1: add Primitive shape=Box name=A"),
                      (3, "line 2: set A size=2,1,1")):
            if r <= rev:
                events.append({"schemaVersion": 1, "ev": "ai-live", "runId": "owned", "sequence": r,
                               "state": "revision", "operation": op,
                               "snapshot": {"revision": r, "path": ".live/revisions/%08d-x.effigy" % r}})
        # the journal always ends with the run's current state, like a real run
        events.append({"schemaVersion": 1, "ev": "ai-live", "runId": "owned", "sequence": rev + 1,
                       "state": state_name, "operation": "add_feature",
                       "snapshot": {"revision": rev, "path": ".live/revisions/%08d-x.effigy" % rev},
                       "control": {}})
        (live_dir / "events.jsonl").write_text("".join(json.dumps(e) + "\n" for e in events))
        (run / "notes.jsonl").write_text(json.dumps(
            {"key": "n1", "time": "t", "run": "run-stop", "rev": 2, "op": "line 1: add Primitive shape=Box name=A",
             "text": "po: the box looks small", "read": False}) + "\n")
        return run

    def _watcher(self, run, stop_event):
        """Fake AiLiveControl: drains .live/commands like the real 50 ms poll loop."""
        import threading
        def go():
            cmds = run / ".live" / "commands"
            seen = set()
            while not stop_event.is_set():
                try:
                    files = sorted(cmds.glob("*.json")) if cmds.is_dir() else []
                except OSError:
                    files = []
                for f in files:
                    if f.name in seen:
                        continue
                    try:
                        cmd = json.loads(f.read_text())
                    except (OSError, ValueError):
                        continue
                    seen.add(f.name)
                    name = cmd.get("command")
                    if name in ("pause", "resume"):
                        try:
                            st = json.loads((run / ".live" / "latest.json").read_text())
                        except (OSError, ValueError):
                            continue
                        ctrl = st.get("control") or {}
                        ctrl["paused"] = (name == "pause")
                        st["control"] = ctrl
                        st["sequence"] = st.get("sequence", 0) + 1
                        (run / ".live" / "latest.json").write_text(json.dumps(st))
                stop_event.wait(0.05)
        t = threading.Thread(target=go, daemon=True)
        t.start()
        return t

    def test_stop_hold_halts_at_step_boundary_and_writes_record(self):
        import threading
        with tempfile.TemporaryDirectory() as directory:
            run = self._make_run(directory)
            confirm = Path(directory) / "confirm"
            real_confirm = live.gate.CONFIRM_DIR
            live.gate.CONFIRM_DIR = str(confirm)
            stop_event = threading.Event()
            try:
                self._watcher(run, stop_event)
                done = live.stop_hold(str(run), notes="po: stop here", timeout_s=10)
                self.assertTrue(done["halted"])
                self.assertEqual(done["revision"], 3)
                self.assertEqual(done["op"], "line 2: set A size=2,1,1")
                # a pause command was queued (never a mid-step kill)
                queued = [json.loads(f.read_text())["command"] for f in (run / ".live" / "commands").glob("*.json")]
                self.assertIn("pause", queued)
                # the stopped-step record is the resume point
                recs = list((confirm / "stopped").glob("*.json"))
                self.assertEqual(len(recs), 1)
                rec = json.loads(recs[0].read_text())
                self.assertEqual(rec["run_dir"], str(run))
                self.assertEqual(rec["revision"], 3)
                self.assertTrue(rec["script"].endswith("build.txt"))
                self.assertTrue(rec["snapshot"].endswith("%08d-x.effigy" % 3))
                self.assertIn("po: stop here", rec["notes"])
                self.assertIn("the box looks small", rec["notes"])  # unread user note carried along
                # resume clears the hold: the same run continues
                live.send_command(str(run), "resume")
                deadline = time.time() + 10
                while time.time() < deadline:
                    if not live.control_state(live.read_state(str(run))).get("paused"):
                        break
                    time.sleep(0.05)
                self.assertFalse(live.control_state(live.read_state(str(run))).get("paused"))
            finally:
                stop_event.set()
                live.gate.CONFIRM_DIR = real_confirm

    def test_stop_hold_open_run_writes_record_immediately(self):
        with tempfile.TemporaryDirectory() as directory:
            run = self._make_run(directory, state_name="open", rev=2)
            confirm = Path(directory) / "confirm"
            real_confirm = live.gate.CONFIRM_DIR
            live.gate.CONFIRM_DIR = str(confirm)
            try:
                done = live.stop_hold(str(run), timeout_s=5)
                self.assertTrue(done["halted"])
                self.assertEqual(done["revision"], 2)
                self.assertFalse((run / ".live" / "commands").exists())  # no pause needed between steps
                rec = json.loads(next((confirm / "stopped").glob("*.json")).read_text())
                self.assertEqual(rec["revision"], 2)
            finally:
                live.gate.CONFIRM_DIR = real_confirm

    def test_stop_hold_refuses_finished_or_unknown_run(self):
        with tempfile.TemporaryDirectory() as directory:
            run = self._make_run(directory, state_name="completed", rev=3)
            self.assertRaises(ValueError, live.stop_hold, str(run))
            self.assertRaises(ValueError, live.stop_hold, str(Path(directory) / "nope"))

    def test_host_stop_button_records_open_run_and_chat_offers_resume_steer(self):
        # the chat-panel STOP wiring headless: QCoreApplication, no window, no live Geppetto
        import sys
        from unittest.mock import patch
        sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "cabinet"))
        import host
        from PySide6.QtCore import QCoreApplication
        app = QCoreApplication.instance() or QCoreApplication([])
        with tempfile.TemporaryDirectory() as directory:
            run = self._make_run(directory, state_name="open", rev=2)
            confirm = Path(directory) / "confirm"
            real_confirm = live.gate.CONFIRM_DIR
            live.gate.CONFIRM_DIR = str(confirm)
            try:
                obj = host.Host()
                obj.runTimer.stop()
                obj.jobs[str(run)] = object()  # a live run, as in a running chest
                obj.stopRunAtStep(str(run))
                self.assertIn(str(run), obj._stopped_runs)
                rec = obj._stopped_runs[str(run)]
                self.assertEqual(rec["revision"], 2)
                self.assertTrue(rec["snapshot"].endswith("%08d-x.effigy" % 2))
                note = obj.claude.messages[-1]["text"]
                self.assertIn("STOPPED", note)
                self.assertIn("step 2", note)
                self.assertIn("RESUME", note)
                self.assertIn("STEER", note)
                # RESUME: the same run gets a resume command, the offer clears, nothing sent to a provider
                self.assertTrue(obj._stopped_chat_command("resume"))
                self.assertEqual(obj._stopped_runs, {})
                queued = [json.loads(f.read_text())["command"] for f in (run / ".live" / "commands").glob("*.json")]
                self.assertIn("resume", queued)
                self.assertIn("RESUMED", obj.claude.messages[-1]["text"])
                # STEER: steering notes queue as a direction on the held run
                obj.stopRunAtStep(str(run))
                self.assertTrue(obj._stopped_chat_command("steer: thicker horns"))
                instructs = [json.loads(f.read_text()) for f in (run / ".live" / "commands").glob("*.json")
                             if json.loads(f.read_text())["command"] == "instruct"]
                self.assertTrue(any("thicker horns" in c["text"] for c in instructs))
                self.assertIn("Steering queued", obj.claude.messages[-1]["text"])
                # ordinary chat is untouched by the intercept
                self.assertFalse(obj._stopped_chat_command("resume the game"))
            finally:
                live.gate.CONFIRM_DIR = real_confirm

    def test_parse_stopped_command(self):
        self.assertEqual(live.parse_stopped_command("resume"), ("resume", ""))
        self.assertEqual(live.parse_stopped_command("  RESUME  "), ("resume", ""))
        self.assertEqual(live.parse_stopped_command("STEER make it bigger"), ("steer", "make it bigger"))
        self.assertEqual(live.parse_stopped_command("steer: thicker horns"), ("steer", "thicker horns"))
        self.assertEqual(live.parse_stopped_command("resume the game"), (None, None))
        self.assertEqual(live.parse_stopped_command("steer"), (None, None))
        self.assertEqual(live.parse_stopped_command("hello"), (None, None))


if __name__ == '__main__':
    unittest.main()
