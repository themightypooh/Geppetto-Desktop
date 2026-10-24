"""Isolated CPU/offscreen tests: never discover or control the user's real runs."""
import json
import os
from pathlib import Path
import sys
import tempfile
import time
import unittest
from types import SimpleNamespace
from unittest.mock import patch

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
os.environ.setdefault("QT_QUICK_BACKEND", "software")
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "cabinet"))
from PySide6.QtCore import QObject, Property, Signal, QUrl, QMetaObject, QPointF, Qt
from PySide6.QtGui import QGuiApplication
from PySide6.QtQml import QQmlApplicationEngine
from PySide6.QtQuick import QQuickWindow, QQuickItem
from PySide6.QtTest import QTest
import live_runs
from viewport_correction import ViewportCorrections


class FakeHost(QObject):
    runsChanged = Signal()

    def __init__(self, root):
        super().__init__()
        self.watching = str(root / "run1")
        self.commands = []
        self.displayed_revision = 0
        self.panes = [SimpleNamespace(run=str(root / ("run%d" % n)), displayed_revision=0, cmd=lambda line: None) for n in (2, 3, 4)]
        self._corrections = ViewportCorrections(self)

    corrections = Property(QObject, lambda self: self._corrections, constant=True)

    def cmd(self, line):
        self.commands.append(line)

    def run_live(self, root):
        return live_runs.read_state(root).get("state") not in live_runs.TERMINAL


class CorrectionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QGuiApplication.instance() or QGuiApplication([])

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(dir="/tmp/opencode", prefix="c1-")
        self.root = Path(self.tmp.name)
        for n in range(1, 5):
            run = self.root / ("run%d" % n)
            (run / ".live").mkdir(parents=True)
            (Path(str(run) + ".json")).write_text(json.dumps({"script": "/fixture/build.txt"}))
            self.state(run)
        self.host = FakeHost(self.root)
        self.review = self.host._corrections

    def tearDown(self):
        self.tmp.cleanup()

    def state(self, root, paused=False, **extra):
        state = dict(schemaVersion=1, ev="ai-live", runId=root.name,
                     state="paused" if paused else "building", sequence=1,
                     control={"paused": paused, "pauseRequested": paused},
                     snapshot=None, **extra)
        (root / ".live/latest.json").write_text(json.dumps(state))

    def commands(self, n):
        return [json.loads(p.read_text())["command"] for p in sorted((self.root / ("run%d" % n) / ".live/commands").glob("*.json"))]

    def held(self, n):
        self.assertTrue(self.review.stop(n))
        self.state(self.root / ("run%d" % n), paused=True)
        self.review.refresh()
        self.assertTrue(self.review._sessions[str(n)]["ready"])

    def test_each_stop_is_local_idempotent_and_never_cancels(self):
        for n in range(1, 5):
            self.held(n)
            self.assertTrue(self.review.stop(n))
            self.assertEqual(self.commands(n), ["review"])
        self.assertFalse(self.review.stop(5))

    def test_feedback_requires_ack_and_cannot_follow_a_replaced_run(self):
        self.review.stop(2)
        self.assertFalse(self.review.send(2, 0, "shorter"))
        self.assertEqual(self.commands(2), ["review"])
        self.host.panes[0].run = str(self.root / "run3")
        self.assertFalse(self.review.send(2, 0, "shorter"))
        self.assertEqual(self.commands(3), [])

    def test_feedback_is_durable_before_resume_and_only_reaches_its_modeller(self):
        self.held(3)
        self.assertTrue(self.review.send(3, 0, "Make the handle shorter."))
        self.assertEqual(self.commands(3), ["review", "resume"])
        notes = live_runs.notes(str(self.root / "run3"))
        self.assertEqual(len(notes), 1)
        self.assertIn("Make the handle shorter.", notes[0]["text"])
        self.assertIn("same run", notes[0]["text"])
        self.assertEqual(self.commands(1), [])

    def test_explicit_continue_wakes_the_script_inbox(self):
        self.held(4)
        self.assertTrue(self.review.resume(4))
        self.assertEqual(self.commands(4), ["review", "resume"])
        self.assertEqual(len(list((self.root / "run4/.live/inbox").glob("*.txt"))), 1)

    def test_bad_input_keeps_the_run_held(self):
        self.held(1)
        for text in ("", " ", "x" * 1601):
            self.assertFalse(self.review.send(1, 0, text))
        self.assertEqual(self.commands(1), ["review"])
        self.assertTrue(self.review._sessions["1"]["holding"])

    def test_native_builder_must_accept_before_resume(self):
        Path(str(self.root / "run1") + ".json").write_text("{}")
        self.held(1)
        self.assertTrue(self.review.send(1, 0, "Shorter handle."))
        self.assertEqual(self.commands(1), ["review", "instruct"])
        session = self.review._sessions["1"]
        path = self.root / "run1/.live/latest.json"
        state = json.loads(path.read_text())
        state["control"].update(commandId=session["pendingDirection"], outcome="accepted")
        path.write_text(json.dumps(state))
        self.review.refresh()
        self.assertEqual(self.commands(1), ["review", "instruct", "resume"])
        self.assertFalse(self.review._sessions["1"]["holding"])

    def test_rejected_native_direction_does_not_resume(self):
        Path(str(self.root / "run1") + ".json").write_text("{}")
        self.held(1)
        self.review.send(1, 0, "Shorter handle.")
        session = self.review._sessions["1"]
        path = self.root / "run1/.live/latest.json"
        state = json.loads(path.read_text())
        state["control"].update(commandId=session["pendingDirection"], outcome="judged-rubric-is-locked")
        path.write_text(json.dumps(state))
        self.review.refresh()
        self.assertEqual(self.commands(1), ["review", "instruct"])
        self.assertTrue(self.review._sessions["1"]["holding"])

    def test_old_runner_is_held_without_claiming_correction_support(self):
        self.review.stop(1)
        path = self.root / "run1/.live/latest.json"
        state = json.loads(path.read_text())
        state["control"].update(commandId=self.review._sessions["1"]["command"], outcome="unknown-command")
        path.write_text(json.dumps(state))
        self.review.refresh()
        self.assertEqual(self.commands(1), ["review", "pause"])
        self.state(self.root / "run1", paused=True)
        self.review.refresh()
        self.assertTrue(self.review._sessions["1"]["legacy"])
        self.assertFalse(self.review.send(1, 0, "This cannot safely run ahead of old queued lines."))
        self.assertNotIn("resume", self.commands(1))
        self.assertTrue(self.review.resume(1))

    def test_closed_script_does_not_promise_fix_first_ordering(self):
        self.review.stop(1)
        self.state(self.root / "run1", paused=True)
        path = self.root / "run1/.live/latest.json"
        state = json.loads(path.read_text())
        state["control"]["scriptReview"] = False
        path.write_text(json.dumps(state))
        self.review.refresh()
        self.assertFalse(self.review._sessions["1"]["correctable"])
        self.assertFalse(self.review.send(1, 0, "Don't run the old tail before this fix."))
        self.assertTrue(self.review.resume(1))
        self.assertFalse((self.root / "run1/.live/inbox").exists())

    def test_multiple_messages_stack_locally_then_apply_once_in_order(self):
        self.held(1)
        for text in ("Make the handle shorter.", "Keep the grip material.", "Leave the blade alone."):
            self.assertTrue(self.review.queue(1, 0, text))
            self.review.refresh()
        session = self.review._sessions["1"]
        self.assertEqual(len(session["messages"]), 3)
        self.assertEqual([m["status"] for m in session["messages"]], ["queued"] * 3)
        self.assertEqual(self.commands(1), ["review"])
        self.assertEqual(live_runs.notes(str(self.root / "run1")), [])
        self.assertFalse(self.review.resume(1), "Continue must not silently discard a queue")
        self.assertTrue(self.review.apply(1))
        self.assertFalse(self.review.apply(1), "double Apply must not send twice")
        self.assertEqual(self.commands(1), ["review", "resume"])
        notes = live_runs.notes(str(self.root / "run1"))
        self.assertEqual(len(notes), 1)
        self.assertLess(notes[0]["text"].index("handle shorter"), notes[0]["text"].index("grip material"))
        self.assertLess(notes[0]["text"].index("grip material"), notes[0]["text"].index("blade alone"))
        self.assertEqual(len(self.review._sessions["1"]["messages"]), 3, "do not add a duplicate combined bubble")
        self.assertEqual(self.commands(2), [])

    def test_history_survives_a_second_stop_and_pending_messages_are_removable(self):
        self.held(1)
        self.review.queue(1, 0, "First correction.")
        self.review.apply(1)
        self.held(1)
        self.assertEqual(self.review._sessions["1"]["messages"][0]["text"], "First correction.")
        self.review.queue(1, 0, "Second correction.")
        self.review.queue(1, 0, "Actually, do not change this.")
        session = self.review._sessions["1"]
        removed = session["queued"][-1]["id"]
        self.assertTrue(self.review.removeQueued(1, removed))
        self.assertEqual([m["text"] for m in self.review._sessions["1"]["messages"]], ["First correction.", "Second correction."])
        self.assertFalse(self.review.removeQueued(1, "not-a-message"))
        self.assertEqual(len(live_runs.notes(str(self.root / "run1"))), 1)

    def test_queue_limits_reject_without_losing_the_stack(self):
        self.held(1)
        self.assertTrue(self.review.queue(1, 0, "x" * 1500))
        self.assertFalse(self.review.queue(1, 0, "y" * 100))
        self.assertFalse(self.review.queue(1, 0, " "))
        self.assertEqual(len(self.review._sessions["1"]["queued"]), 1)
        self.assertEqual(self.commands(1), ["review"])

    def test_native_batch_rejection_restores_stack_and_acceptance_marks_each_sent(self):
        Path(str(self.root / "run1") + ".json").write_text("{}")
        self.held(1)
        self.review.queue(1, 0, "Shorter handle.")
        self.review.queue(1, 0, "Keep its colour.")
        self.assertTrue(self.review.apply(1))
        path = self.root / "run1/.live/latest.json"
        state = json.loads(path.read_text())
        state["control"].update(commandId=self.review._sessions["1"]["pendingDirection"], outcome="invalid-or-full-direction-queue")
        path.write_text(json.dumps(state))
        self.review.refresh()
        self.assertEqual(len(self.review._sessions["1"]["queued"]), 2)
        self.assertNotIn("resume", self.commands(1))
        self.assertTrue(self.review.apply(1))
        state["control"].update(commandId=self.review._sessions["1"]["pendingDirection"], outcome="accepted")
        path.write_text(json.dumps(state))
        self.review.refresh()
        self.assertEqual([m["status"] for m in self.review._sessions["1"]["messages"]], ["sent", "sent"])
        self.assertEqual(self.commands(1).count("resume"), 1)
        self.assertEqual(len(live_runs.notes(str(self.root / "run1"))), 1, "a retry must not duplicate the approved note")

    def test_resume_io_failure_preserves_stack_and_retry_does_not_duplicate_note(self):
        self.held(1)
        self.review.queue(1, 0, "One correction.")
        original = live_runs.send_command
        def failing(root, command, **fields):
            if command == "resume":
                raise OSError("fixture disk error")
            return original(root, command, **fields)
        with patch.object(live_runs, "send_command", failing):
            self.assertFalse(self.review.apply(1))
        self.assertEqual(len(self.review._sessions["1"]["queued"]), 1)
        self.assertTrue(self.review.apply(1))
        self.assertEqual(len(live_runs.notes(str(self.root / "run1"))), 1)

    def settle(self):
        for _ in range(20):
            self.app.processEvents()
            time.sleep(.01)

    def find(self, window, name):
        pending = [window.contentItem()]
        while pending:
            item = pending.pop()
            if item.objectName() == name:
                return item
            pending.extend(item.childItems())
        return None

    def test_same_control_in_all_layouts_clicks_locally_and_fits(self):
        engine = QQmlApplicationEngine()
        engine.rootContext().setContextProperty("host", self.host)
        source = '''import QtQuick
import QtQuick.Window
import "%s"
Window {
    id: harness
    width: 1200; height: 800; visible: true; color: "#15191f"
    property int panes: 4
    Repeater {
        model: 4
        Rectangle {
            required property int index
            width: harness.panes === 1 ? harness.width : (harness.width - 1) / 2
            height: harness.panes < 4 ? harness.height : (harness.height - 1) / 2
            x: harness.panes === 1 ? 0 : (index %% 2) * (width + 1)
            y: harness.panes === 4 ? Math.floor(index / 2) * (height + 1) : 0
            color: index %% 2 ? "#242a32" : "#20262e"
            visible: index < harness.panes
        }
    }
    Repeater {
        model: 4
        ViewportCorrection {
            required property int index
            pane: index + 1; active: true; runDir: "%s/run" + (index + 1)
            width: harness.panes === 1 ? harness.width : (harness.width - 1) / 2
            height: harness.panes < 4 ? harness.height : (harness.height - 1) / 2
            x: harness.panes === 1 ? 0 : (index %% 2) * (width + 1)
            y: harness.panes === 4 ? Math.floor(index / 2) * (height + 1) : 0
            paneVisible: index < harness.panes
            textZoom: %s
        }
    }
}''' % ((Path(__file__).resolve().parents[1] / "cabinet").as_uri(), self.root, os.environ.get("C1_TEXT_ZOOM", "1"))
        warnings = []
        engine.warnings.connect(lambda errors: warnings.extend(e.toString() for e in errors))
        engine.loadData(source.encode(), QUrl("file:///tmp/opencode/c1-harness.qml"))
        self.assertTrue(engine.rootObjects(), warnings)
        window = engine.rootObjects()[0]
        try:
            self.settle()
            for layout in (1, 2, 4):
                window.setProperty("panes", layout)
                self.settle()
                for n in range(1, layout + 1):
                    key = self.find(window, "viewportStop%d" % n)
                    self.assertIsNotNone(key, warnings)
                    pos = key.mapToScene(QPointF(key.width()/2, key.height()/2))
                    QTest.mouseClick(window, Qt.LeftButton, Qt.NoModifier, pos.toPoint())
                    self.settle()
                    chat = next(w for w in self.app.allWindows() if w.objectName() == "viewportCorrectionWindow%d" % n)
                    field = self.find(chat, "viewportCorrectionInput%d" % n)
                    self.assertTrue(field.property("enabled"), "typing must work while the safe stop is pending")
                    send = self.find(chat, "viewportCorrectionSend%d" % n)
                    self.assertFalse(send.property("enabled"))
                    self.state(self.root / ("run%d" % n), paused=True)
                    self.review.refresh()
                    self.settle()
                    chat = next(w for w in self.app.allWindows() if w.objectName() == "viewportCorrectionWindow%d" % n)
                    self.assertTrue(chat.isVisible())
                    self.assertIsNot(chat, window)
                    self.assertIn("View %d" % n, chat.title())
                    panel = self.find(chat, "viewportCorrectionPanel%d" % n)
                    self.assertTrue(panel.isVisible())
                    self.assertGreaterEqual(panel.x(), 0)
                    self.assertLessEqual(panel.x() + panel.width(), panel.parentItem().width())
                    self.assertLessEqual(panel.y() + panel.height(), panel.parentItem().height())
                    close = self.find(chat, "viewportCorrectionClose%d" % n)
                    QMetaObject.invokeMethod(close, "clicked")
                    self.assertFalse(chat.isVisible())
                    self.assertEqual(self.commands(n), ["review"])
                    QMetaObject.invokeMethod(key, "clicked")
                for width, height, tag in ((640, 480, "small"), (1200, 800, "default"), (1920, 1080, "large")):
                    chat_size = (chat.width(), chat.height())
                    window.resize(width, height)
                    self.settle()
                    self.assertEqual((chat.width(), chat.height()), chat_size, "viewport resizing must not squash the chat")
                    image = window.grabWindow()
                    self.assertFalse(image.isNull())
                    destination = Path("/tmp/opencode/c1-previews")
                    destination.mkdir(exist_ok=True)
                    image.save(str(destination / ("layout%d-%s-scale%s.png" % (layout, tag, os.environ.get("QT_SCALE_FACTOR", "1")))))
            for width, height, tag in ((380, 420, "small"), (500, 580, "default"), (900, 1000, "large")):
                chat.resize(max(width, chat.minimumWidth()), max(height, chat.minimumHeight()))
                self.settle()
                image = chat.grabWindow()
                self.assertFalse(image.isNull())
                image.save(str(destination / ("popout-%s-text%s-scale%s.png" % (tag, os.environ.get("C1_TEXT_ZOOM", "1"), os.environ.get("QT_SCALE_FACTOR", "1")))))
                send = self.find(chat, "viewportCorrectionSend4")
                pos = send.mapToScene(QPointF(0, 0))
                self.assertGreaterEqual(pos.y(), 0)
                self.assertLessEqual(pos.y() + send.height(), chat.height())
                self.assertLessEqual(pos.x() + send.width(), chat.width())
            # Layout changes do not destroy a hidden viewport's chat or its draft.
            field = self.find(chat, "viewportCorrectionInput4")
            field.setProperty("text", "Keep this correction draft for view four.")
            window.setProperty("panes", 1)
            self.settle()
            self.assertTrue(chat.isVisible())
            self.assertEqual(field.property("text"), "Keep this correction draft for view four.")
            chat.close()
            self.settle()
            self.assertEqual(self.commands(4), ["review"], "native close must never resume a build")
            window.setProperty("panes", 4)
            self.settle()
            key = self.find(window, "viewportStop4")
            QMetaObject.invokeMethod(key, "clicked")
            self.settle()
            send = self.find(chat, "viewportCorrectionSend4")
            pos = send.mapToScene(QPointF(send.width()/2, send.height()/2))
            QTest.mouseClick(chat, Qt.LeftButton, Qt.NoModifier, pos.toPoint())
            self.settle()
            self.assertEqual(self.commands(4), ["review"], "Send message must stack locally, not resume")
            self.assertEqual(live_runs.notes(str(self.root / "run4")), [])
            field.setProperty("text", "Also keep the original material.")
            field.forceActiveFocus()
            QTest.keyClick(chat, Qt.Key_Return)
            self.settle()
            self.assertIn("\n", field.property("text"), "ordinary Enter must be a newline, not a send")
            self.assertEqual(len(self.review._sessions["4"]["queued"]), 1)
            QTest.keyClick(chat, Qt.Key_Return, Qt.ControlModifier)
            self.settle()
            self.assertEqual(len(self.review._sessions["4"]["queued"]), 2, "Ctrl+Enter only stacks the message")
            field.setProperty("text", "And do not change the silhouette.")
            QTest.mouseClick(chat, Qt.LeftButton, Qt.NoModifier, pos.toPoint())
            self.settle()
            self.assertEqual(len(self.review._sessions["4"]["queued"]), 3)
            self.assertTrue(field.property("visible"), "the first Send must not hide the composer")
            chat.close()
            self.settle()
            self.assertEqual(self.commands(4), ["review"], "closing a stacked chat must not apply it")
            QMetaObject.invokeMethod(key, "clicked")
            self.settle()
            for width, height, tag in ((380, 520, "small"), (500, 580, "default"), (900, 1000, "large")):
                chat.resize(max(width, chat.minimumWidth()), max(height, chat.minimumHeight()))
                self.settle()
                apply = self.find(chat, "viewportCorrectionApply4")
                for control in (field, send, apply):
                    p = control.mapToScene(QPointF(0, 0))
                    self.assertGreaterEqual(p.y(), 0)
                    self.assertLessEqual(p.y() + control.height(), chat.height())
                    self.assertLessEqual(p.x() + control.width(), chat.width())
                image = chat.grabWindow()
                image.save(str(destination / ("stack-%s-text%s-scale%s.png" % (tag, os.environ.get("C1_TEXT_ZOOM", "1"), os.environ.get("QT_SCALE_FACTOR", "1")))))
            pos = apply.mapToScene(QPointF(apply.width()/2, apply.height()/2))
            QTest.mouseClick(chat, Qt.LeftButton, Qt.NoModifier, pos.toPoint())
            self.settle()
            self.assertEqual(self.commands(4), ["review", "resume"])
            notes = live_runs.notes(str(self.root / "run4"))
            self.assertEqual(len(notes), 1)
            self.assertIn("Keep this correction draft", notes[0]["text"])
            self.assertIn("original material", notes[0]["text"])
            self.assertIn("silhouette", notes[0]["text"])
            self.assertEqual(self.commands(3), ["review"], "view four's send must not affect view three")
            self.assertEqual(warnings, [])
        finally:
            for item in self.app.allWindows():
                if item is not window:
                    item.close()
            window.close()
            engine.deleteLater()
            self.settle()


if __name__ == "__main__":
    unittest.main()
