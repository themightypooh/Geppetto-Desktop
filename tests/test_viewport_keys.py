"""The cabinet viewport's mouse and Escape handling, against s&box (EffigyViewport).

cabinet.qml is loaded offscreen; the viewer process is a fake that records the command
lines written to it. No viewer is started and no window is shown.
"""
import os
import sys
import time
import unittest
from pathlib import Path

CABINET = Path(__file__).resolve().parents[1] / "cabinet"
sys.path.insert(0, str(CABINET))
os.environ["QT_QPA_PLATFORM"] = "offscreen"

from PySide6.QtCore import QObject, QPointF, Qt, QUrl  # noqa: E402
from PySide6.QtGui import QGuiApplication  # noqa: E402
from PySide6.QtQml import QQmlApplicationEngine, qmlRegisterType  # noqa: E402
from PySide6.QtTest import QTest  # noqa: E402

import host as chest  # noqa: E402


class FakeProc:
    def __init__(self):
        self.lines = []

    def state(self):
        return chest.QProcess.Running

    def write(self, b):
        self.lines.extend(x for x in bytes(b).decode().splitlines() if x.strip())


class ViewportKeysTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QGuiApplication.instance() or QGuiApplication([])
        qmlRegisterType(chest.Viewport, "Geppetto", 1, 0, "Viewport")
        cls.host = chest.Host()
        cls.host.runTimer.stop()
        cls.host._preferences_path = "/nonexistent/prefs.json"
        chest.HOST = cls.host
        cls.proc = FakeProc()
        cls.host.proc = cls.proc
        cls.engine = QQmlApplicationEngine()
        cls.engine.rootContext().setContextProperty("host", cls.host)
        cls.engine.load(QUrl.fromLocalFile(str(CABINET / "cabinet.qml")))
        cls.root = cls.engine.rootObjects()[0]
        cls.settle(0.3)
        cls.vm = cls.root.findChild(QObject, "viewMouse")
        cls.centre = cls.vm.mapToScene(QPointF(cls.vm.width() / 2, cls.vm.height() / 2)).toPoint()

    @classmethod
    def settle(cls, seconds=0.05):
        end = time.time() + seconds
        while time.time() < end:
            cls.app.processEvents()
            time.sleep(0.01)

    def setUp(self):
        self.mode("face")
        self.host._sel = {"bone": -1, "body": -1}
        self.host._sub_sel = {}
        self.host.infoChanged.emit()
        self.host.subSelectionChanged.emit()
        for name in ("toolCard", "materialRing"):
            self.root.findChild(QObject, name).setProperty("open", False)
        self.root.findChild(QObject, "toolWheel").setProperty("manual", False)
        time.sleep(0.6)                      # no double-click carried over from the last test
        self.settle()
        QTest.mouseClick(self.root, Qt.LeftButton, Qt.NoModifier, self.centre)   # focus the view
        self.settle()
        time.sleep(0.6)
        self.proc.lines.clear()

    def mode(self, m):
        self.host._sub_mode = m
        self.host.subSelectionChanged.emit()
        self.settle()

    def escape(self):
        QTest.keyClick(self.root, Qt.Key_Escape)
        self.settle()

    def picks(self):
        return [x for x in self.proc.lines if x.startswith("pick ")]

    # ---- double-click -----------------------------------------------------------------
    def test_double_click_in_sub_mode_is_one_pick_and_no_frame(self):
        QTest.mouseDClick(self.root, Qt.LeftButton, Qt.NoModifier, self.centre)
        self.settle()
        self.assertEqual(len(self.picks()), 1, self.proc.lines)
        self.assertNotIn("frame", self.proc.lines)

    def test_double_click_in_body_mode_frames_with_one_pick(self):
        self.mode("body")
        self.proc.lines.clear()
        QTest.mouseDClick(self.root, Qt.LeftButton, Qt.NoModifier, self.centre)
        self.settle()
        self.assertEqual(len(self.picks()), 1, self.proc.lines)
        self.assertIn("frame", self.proc.lines)

    def test_two_slow_clicks_are_two_picks(self):
        QTest.mouseClick(self.root, Qt.LeftButton, Qt.NoModifier, self.centre)
        self.settle()
        time.sleep(0.6)
        QTest.mouseClick(self.root, Qt.LeftButton, Qt.NoModifier, self.centre)
        self.settle()
        self.assertEqual(len(self.picks()), 2, self.proc.lines)

    # ---- Escape, one layer per press ---------------------------------------------------
    def test_escape_while_held_cancels_the_press(self):
        QTest.mousePress(self.root, Qt.LeftButton, Qt.NoModifier, self.centre)
        self.settle()
        self.escape()
        QTest.mouseMove(self.root, self.centre + type(self.centre)(40, 20))
        self.settle()
        QTest.mouseRelease(self.root, Qt.LeftButton, Qt.NoModifier, self.centre + type(self.centre)(40, 20))
        self.settle()
        self.assertEqual(self.picks(), [], self.proc.lines)
        self.assertFalse([x for x in self.proc.lines if x.startswith("orbit")], self.proc.lines)

    def test_escape_order(self):
        card = self.root.findChild(QObject, "toolCard")
        ring = self.root.findChild(QObject, "materialRing")
        wheel = self.root.findChild(QObject, "toolWheel")
        wheel.setProperty("manual", True)
        card.setProperty("open", True)
        ring.setProperty("open", True)
        self.host._sel = {"bone": 3, "body": -1}
        self.host.infoChanged.emit()
        self.host._sub_sel = {"count": 2, "faces": "Torso:1,2"}
        self.host.subSelectionChanged.emit()
        self.settle()
        self.proc.lines.clear()

        self.escape()
        self.assertFalse(ring.property("open"))
        self.assertTrue(card.property("open"))
        self.escape()
        self.assertFalse(card.property("open"))
        self.assertTrue(wheel.property("manual"))
        self.escape()
        self.assertFalse(wheel.property("manual"))
        self.assertEqual(self.proc.lines, [])
        self.escape()
        self.assertEqual(self.proc.lines, ["select -1"])        # the bone goes before the faces
        self.host._sel = {"bone": -1, "body": -1}                # as the viewer's @selected reply would
        self.host.infoChanged.emit()
        self.settle()
        self.escape()
        self.assertEqual(self.proc.lines, ["select -1", "selclear"])

    def test_escape_with_nothing_to_drop_sends_nothing(self):
        self.escape()
        self.assertEqual(self.proc.lines, [])


if __name__ == "__main__":
    unittest.main()
