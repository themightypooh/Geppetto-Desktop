"""Claude Code in the chest: the STOP/PAUSE gate hook, the stream-json link and the CLAUDE card.

The gate and the link run against temp folders and a fake `claude` process (no real Claude, no
network). The card loads cabinet.qml offscreen (no window, no focus taken), fills the link with a
conversation and a pending permission question, and checks the keys reach the link.
GEPPETTO_SNAP=path also saves a picture of the open card.
"""
import io
import json
import os
import sys
import tempfile
import threading
import unittest
from pathlib import Path

CABINET = Path(__file__).resolve().parents[1] / "cabinet"
sys.path.insert(0, str(CABINET))
os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

import claude_gate  # noqa: E402
import claude_link  # noqa: E402
import claude_feed  # noqa: E402


class TempDirs(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.saved = (claude_gate.GATES, claude_gate.RUNS)
        claude_gate.GATES = os.path.join(self.tmp.name, "gate")
        claude_gate.RUNS = os.path.join(self.tmp.name, "runs")

    def tearDown(self):
        claude_gate.GATES, claude_gate.RUNS = self.saved
        self.tmp.cleanup()


class GateTests(TempDirs):
    def call(self, event, session="s1", **kw):
        return claude_gate.hook({"session_id": session, "hook_event_name": event}, **kw)

    def test_open_gate_lets_everything_through(self):
        self.assertIsNone(self.call("PreToolUse"))
        self.assertIsNone(self.call("PostToolUse"))

    def test_stop_refuses_the_next_tool_and_halts(self):
        claude_gate.set_gate("s1", "stopped", "run tower")
        out = self.call("PreToolUse")
        self.assertFalse(out["continue"])
        self.assertEqual(out["hookSpecificOutput"]["permissionDecision"], "deny")
        self.assertIn("run tower", out["systemMessage"])
        self.assertFalse(self.call("PostToolUse")["continue"])
        self.assertNotIn("hookSpecificOutput", self.call("PostToolUse"))
        self.assertIsNone(self.call("PreToolUse", session="other"))     # only that session

    def test_new_prompt_lifts_the_stop(self):
        claude_gate.set_gate("s1", "stopped")
        self.assertIsNone(self.call("UserPromptSubmit"))
        self.assertIsNone(self.call("PreToolUse"))

    def test_pause_waits_until_resumed(self):
        claude_gate.set_gate("s1", "paused")
        ticks = []

        def sleep(_):
            ticks.append(1)
            if len(ticks) == 3:
                claude_gate.clear_gate("s1")
        self.assertIsNone(self.call("PreToolUse", sleep=sleep, clock=lambda: 0))
        self.assertEqual(len(ticks), 3)

    def test_stop_while_paused_halts(self):
        claude_gate.set_gate("s1", "paused")
        out = self.call("PreToolUse", sleep=lambda _: claude_gate.set_gate("s1", "stopped"), clock=lambda: 0)
        self.assertEqual(out["hookSpecificOutput"]["permissionDecision"], "deny")

    def test_pause_does_not_hold_post_tool(self):
        claude_gate.set_gate("s1", "paused")
        self.assertIsNone(self.call("PostToolUse"))

    def test_broken_input_fails_open(self):
        saved = sys.stdin, sys.stdout
        try:
            sys.stdin, sys.stdout = io.StringIO("not json"), io.StringIO()
            self.assertEqual(claude_gate.main(), 0)
            self.assertEqual(sys.stdout.getvalue(), "")
        finally:
            sys.stdin, sys.stdout = saved

    def test_run_maps_back_to_its_session(self):
        runs = os.path.join(self.tmp.name, "research")
        root = os.path.join(runs, "tower")
        os.makedirs(root)
        with open(root + ".json", "w") as f:
            json.dump({"pid": 4242, "script": "build.txt"}, f)
        claude_gate.record_run(4242, "s1", 100, "build.txt")
        self.assertEqual(claude_gate.session_for_run(root), "s1")
        self.assertEqual(claude_gate.run_root(4242, runs), root)
        self.assertEqual(claude_gate.run_root(9999, runs), "")


    def test_notes_on_this_sessions_run_ride_in_on_its_next_tool_call(self):
        import live_runs
        research = os.path.join(self.tmp.name, "research")
        root = os.path.join(research, "script-a")
        os.makedirs(os.path.join(root, ".live"))
        Path(root + ".json").write_text(json.dumps({"script": "a.txt", "pid": os.getpid()}))
        claude_gate.record_run(os.getpid(), "s1")
        live_runs.add_note(root, 1, "line 1: add Box", "too wide")
        self.assertEqual(claude_gate.session_runs("other", research), [])
        text = claude_gate.session_notes("s1", research)
        self.assertIn("too wide", text)
        self.assertEqual(claude_gate.session_notes("s1", research), "")     # once
        live_runs.add_note(root, 1, "line 1: add Box", "and taller")
        saved = claude_gate.session_notes
        claude_gate.session_notes = lambda session: saved(session, research)
        try:
            out = self.call("PostToolUse")
        finally:
            claude_gate.session_notes = saved
        self.assertEqual(out["hookSpecificOutput"]["hookEventName"], "PostToolUse")
        self.assertIn("and taller", out["hookSpecificOutput"]["additionalContext"])

class FakeProc:
    """Stands in for `claude -p`: lines written to stdin are kept, stdout is fed by the test."""
    def __init__(self, args, **kw):
        self.args, self.kw = args, kw
        self.sent = []
        self.out = Pipe()
        self.stdin = self
        self.stdout = self.out
        self.stderr = iter(())
        self.code = None

    def write(self, text):
        self.sent.append(json.loads(text))

    def flush(self):
        pass

    def close(self):
        pass

    def poll(self):
        return self.code

    def terminate(self):
        self.code = 0
        self.out.end()


class Pipe:
    def __init__(self):
        self.lines, self.cv, self.done = [], threading.Condition(), False

    def feed(self, obj):
        with self.cv:
            self.lines.append(json.dumps(obj) + "\n")
            self.cv.notify_all()

    def end(self):
        with self.cv:
            self.done = True
            self.cv.notify_all()

    def __iter__(self):
        while True:
            with self.cv:
                self.cv.wait_for(lambda: self.lines or self.done)
                if not self.lines:
                    return
                line = self.lines.pop(0)
            yield line


class LinkTests(TempDirs):
    def setUp(self):
        super().setUp()
        self.procs = []
        self.link = claude_link.ClaudeLink(spawn=self.spawn, state_path=os.path.join(self.tmp.name, "chat.json"))

    def tearDown(self):
        self.link.close()
        super().tearDown()

    def spawn(self, args, **kw):
        self.procs.append(FakeProc(args, **kw))
        return self.procs[-1]

    def test_missing_build_receipt_never_sends_a_second_turn(self):
        class NoReceipt:
            def receipt(self):
                return None

            def failure(self):
                return "no verified revision; send again only if you want to retry"

        self.link._execution = NoReceipt()
        sent = []
        self.link._send = lambda msg: sent.append(msg) or True
        self.link.handle({"type": "result", "result": "built it"})
        self.assertEqual(sent, [])
        self.assertIsNone(self.link._execution)
        self.assertTrue(any("no verified revision" in row["text"] for row in self.link.messages))

    def test_send_starts_claude_and_shows_the_reply(self):
        self.link.send("make a mug")
        proc = self.procs[0]
        self.assertIn("--permission-prompt-tool", proc.args)
        self.assertTrue(proc.kw["start_new_session"])
        self.assertNotIn("--resume", proc.args)
        self.assertFalse(any(k.startswith("CLAUDE_CODE_") for k in proc.kw["env"]))
        self.assertEqual(proc.sent[0]["request"]["subtype"], "initialize")
        self.assertEqual(proc.sent[1]["message"]["content"], "make a mug")
        self.link.handle({"type": "system", "subtype": "init", "session_id": "abc"})
        self.link.handle({"type": "assistant", "message": {"content": [
            {"type": "text", "text": "On it."},
            {"type": "tool_use", "id": "t1", "name": "Bash", "input": {"command": "geppetto-live mug.txt", "description": "Build the mug"}}]}})
        self.link.handle({"type": "result", "subtype": "success"})
        self.assertEqual([m["role"] for m in self.link.messages], ["you", "claude", "tool"])
        self.assertEqual(self.link.messages[2]["text"], "Bash  Build the mug")
        self.assertEqual(self.link.status, "idle")
        self.assertEqual(json.load(open(self.link.state_path))["session"], "abc")
        again = claude_link.ClaudeLink(spawn=self.spawn, state_path=self.link.state_path)
        again.send("bigger handle")
        self.assertEqual(self.procs[-1].args[self.procs[-1].args.index("--resume") + 1], "abc")
        again.close()

    def test_permission_question_allow_and_deny(self):
        self.link.send("x")
        proc = self.procs[0]
        self.link.handle({"type": "control_request", "request_id": "r1", "request": {
            "subtype": "can_use_tool", "tool_name": "Bash", "input": {"command": "rm -rf build"}}})
        self.assertEqual(self.link.ask["text"], "Bash  rm -rf build")
        self.link.answer(False)
        self.assertIsNone(self.link.ask)
        reply = proc.sent[-1]["response"]
        self.assertEqual((reply["request_id"], reply["response"]["behavior"]), ("r1", "deny"))
        self.link.handle({"type": "control_request", "request_id": "r2", "request": {
            "subtype": "can_use_tool", "tool_name": "Write", "input": {"file_path": "a.txt"}}})
        self.link.answer(True)
        reply = proc.sent[-1]["response"]
        self.assertEqual(reply["response"], {"behavior": "allow", "updatedInput": {"file_path": "a.txt"}})

    def test_stop_gates_interrupts_and_cancels_its_runs(self):
        self.link.send("x")
        self.link.handle({"type": "system", "subtype": "init", "session_id": "abc"})
        self.link.handle({"type": "control_request", "request_id": "r1", "request": {
            "subtype": "can_use_tool", "tool_name": "Bash", "input": {}}})
        claude_gate.record_run(os.getpid(), "abc")      # a live pid that "belongs" to this session
        cancelled = []
        saved = claude_gate.cancel_pid
        claude_gate.cancel_pid = lambda pid, *a: cancelled.append(pid)
        try:
            self.link.stop()
        finally:
            claude_gate.cancel_pid = saved
        self.assertEqual(claude_gate.gate("abc")["state"], "stopped")
        self.assertEqual(cancelled, [os.getpid()])
        sent = self.procs[0].sent
        self.assertEqual(sent[-2]["response"]["response"]["behavior"], "deny")
        self.assertEqual(sent[-1]["request"]["subtype"], "interrupt")
        self.assertEqual(self.link.status, "stopped")
        self.link.handle({"type": "result", "subtype": "error_during_execution"})
        self.assertEqual(self.link.status, "stopped")       # the interrupted result doesn't undo STOP
        self.link.send("carry on")
        self.assertEqual(claude_gate.gate("abc"), {})        # a new message lifts it

    def test_pause_toggles_the_gate(self):
        self.link.send("x")
        self.link.handle({"type": "system", "subtype": "init", "session_id": "abc"})
        self.link.pause()
        self.assertEqual((self.link.status, claude_gate.gate("abc")["state"]), ("paused", "paused"))
        self.link.pause()
        self.assertEqual((self.link.status, claude_gate.gate("abc")), ("working", {}))

    def test_new_forgets_the_conversation(self):
        self.link.send("x")
        self.link.handle({"type": "system", "subtype": "init", "session_id": "abc"})
        self.link.fresh()
        self.assertEqual((self.link.session, self.link.messages, self.link.status), ("", [], "idle"))


class FeedTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.feed, self.runs = os.path.join(self.tmp.name, "feed"), os.path.join(self.tmp.name, "runs")
        self.state = os.path.join(self.tmp.name, "claude-chat.json")
        # The card's session id lands in claude-chat.json at session init (claude_link._save),
        # before any of its tool calls reach the gate hook - register it the same way here.
        Path(self.state).write_text(json.dumps({"session": "card"}))
        self.png = os.path.join(self.tmp.name, "ref.png")
        Path(self.png).write_bytes(b"x")

    def tearDown(self):
        self.tmp.cleanup()

    def note(self, session, tool, data):
        claude_feed.note({"session_id": session, "hook_event_name": "PreToolUse", "tool_name": tool,
                          "tool_input": data, "cwd": self.tmp.name},
                         feed=self.feed, runs=self.runs, state=self.state)

    def test_only_images_from_sessions_that_matter_pop(self):
        feed = claude_feed.Feed(self.feed, self.runs, clock=lambda: 0)
        self.note("card", "Read", {"file_path": "ref.png"})                 # relative to the session's cwd
        self.note("card", "Read", {"file_path": os.path.join(self.tmp.name, "build.txt")})
        self.note("other", "Read", {"file_path": self.png})                 # a session with no runs: ignored
        saved, claude_gate.RUNS = claude_gate.RUNS, self.runs
        try:
            claude_gate.record_run(123, "modeller")                         # a run registers when it starts,
        finally:                                                            # before the session's pictures
            claude_gate.RUNS = saved
        self.note("modeller", "SendUserFile", {"files": [self.png, "missing.jpg"]})
        new = feed.poll("card")
        self.assertEqual([(p["session"], p["path"]) for p in new], [("card", self.png), ("modeller", self.png)])
        self.assertEqual(feed.poll("card"), [])                            # read once
        self.note("card", "Read", {"file_path": self.png})
        self.assertEqual(len(feed.poll("card")), 1)
        self.assertEqual(len(feed.pictures), 3)

    def test_old_pictures_and_post_tool_do_not_pop(self):
        self.note("card", "Read", {"file_path": self.png})
        claude_feed.note({"session_id": "card", "hook_event_name": "PostToolUse", "tool_name": "Read",
                          "tool_input": {"file_path": self.png}}, feed=self.feed)
        self.assertEqual(claude_feed.Feed(self.feed, self.runs).poll("card"), [])    # chest opened after it


class CardTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        from PySide6.QtCore import QUrl
        from PySide6.QtGui import QGuiApplication
        from PySide6.QtQml import QQmlApplicationEngine, qmlRegisterType
        import host as chest
        cls.app = QGuiApplication.instance() or QGuiApplication([])
        if not hasattr(chest, "_viewport_type_registered"):
            qmlRegisterType(chest.Viewport, "Geppetto", 1, 0, "Viewport")
            chest._viewport_type_registered = True
        cls.tmp = tempfile.TemporaryDirectory()
        cls.host = chest.Host()
        # This UI fixture must never discover/follow the user's actual live runs.
        cls.host.runTimer.stop()
        cls.host.pictureTimer.stop()
        cls.host._preferences_path = "/nonexistent/geppetto-test/preferences.json"
        cls.host.claude.close()
        cls.sent = []
        cls.host.claude = claude_link.ClaudeLink(on_change=cls.host.claudeChanged.emit,
                                                  spawn=lambda a, **k: FakeProc(a, **k),
                                                  state_path=os.path.join(cls.tmp.name, "chat.json"), lockdown=True)
        cls.host._claude_log.link = cls.host.claude
        chest.HOST = cls.host
        cls.engine = QQmlApplicationEngine()
        cls.engine.rootContext().setContextProperty("host", cls.host)
        cls.engine.load(QUrl.fromLocalFile(str(CABINET / "cabinet.qml")))
        cls.root = cls.engine.rootObjects()[0]

    @classmethod
    def tearDownClass(cls):
        cls.host.claude.close()
        cls.tmp.cleanup()

    def settle(self, seconds=0.4):
        import time
        end = time.time() + seconds
        while time.time() < end:
            self.app.processEvents()

    def find(self, name):
        from PySide6.QtCore import QObject
        return self.root.findChild(QObject, name)

    def click(self, name):
        from PySide6.QtCore import QMetaObject
        key = self.find(name)
        self.assertTrue(key.property("enabled"), name + " is disabled")
        QMetaObject.invokeMethod(key, "clicked")
        self.settle(0.1)

    def test_card_shows_the_conversation_and_answers(self):
        link = self.host.claude
        panel = self.find("claudePanel")
        self.assertFalse(panel.property("visible"))
        self.root.setProperty("chatOpen", True)
        link.send("make a red mug, short handle")
        link.handle({"type": "system", "subtype": "init", "session_id": "card"})
        link.handle({"type": "assistant", "message": {"content": [
            {"type": "text", "text": "Getting a reference photo first, then building it in pane 2."},
            {"type": "tool_use", "id": "t1", "name": "Bash", "input": {"description": "Build the mug in the chest"}}]}})
        link.handle({"type": "control_request", "request_id": "r1", "request": {
            "subtype": "can_use_tool", "tool_name": "Write", "input": {"file_path": "/home/poo/mug/build.txt"}}})
        self.settle()
        self.assertTrue(panel.property("visible"))
        self.assertEqual(self.find("claudeLog").property("count"), 4)
        self.assertTrue(self.find("claudeAllow").property("visible"))
        if os.environ.get("GEPPETTO_SNAP"):
            self.root.grabWindow().save(os.environ["GEPPETTO_SNAP"])
        proc = link.proc
        self.click("claudeAllow")
        self.assertEqual(proc.sent[-1]["response"]["response"]["behavior"], "allow")
        self.assertFalse(self.find("claudeAllow").property("visible"))
        self.click("claudePause")
        self.assertEqual(link.status, "paused")
        self.click("claudePause")
        self.click("claudeStop")
        self.assertEqual(link.status, "stopped")
        self.assertEqual(proc.sent[-1]["request"]["subtype"], "interrupt")
        claude_gate.clear_gate("card")

    def test_a_picture_pops_then_lives_in_the_strip(self):
        import shutil
        feed_dir = os.path.join(self.tmp.name, "feed")
        self.host.claudeFeed = claude_feed.Feed(feed_dir, os.path.join(self.tmp.name, "runs"), clock=lambda: 0)
        self.host.claude.session = "card"
        self.root.setProperty("chatOpen", True)
        png = os.path.join(self.tmp.name, "ref.png")
        shutil.copy(os.environ.get("GEPPETTO_PICTURE", str(CABINET / "model" / "appicon" / "ref_flip.png")), png)
        state = os.path.join(self.tmp.name, "claude-chat.json")             # the link saved its session
        Path(state).write_text(json.dumps({"session": "card"}))             # id there at init, as in real use
        claude_feed.note({"session_id": "card", "hook_event_name": "PreToolUse", "tool_name": "Read",
                          "tool_input": {"file_path": png}}, feed=feed_dir,
                         runs=os.path.join(self.tmp.name, "runs"), state=state)
        self.host._poll_pictures()
        self.settle(0.6)
        pop = self.find("claudePicture")
        self.assertTrue(pop.property("visible"))
        self.assertEqual(pop.property("path"), png)
        self.assertEqual(self.find("claudeStrip").property("count"), 1)
        if os.environ.get("GEPPETTO_PICSNAP"):
            self.root.grabWindow().save(os.environ["GEPPETTO_PICSNAP"])
        self.settle(4.9)                                                        # holds ~4 s, then shrinks away
        self.assertFalse(pop.property("visible"))
        self.assertEqual(self.find("claudeStrip").property("count"), 1)
        self.root.setProperty("chatOpen", False)
        self.settle(0.3)


if __name__ == "__main__":
    unittest.main()
