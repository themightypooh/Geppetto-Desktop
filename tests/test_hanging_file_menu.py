"""Offscreen checks for the moving, expandable Geppetto file menu."""

import os
import time
import unittest
from pathlib import Path

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QObject, QPoint, QPointF, Qt, QUrl, Slot
from PySide6.QtGui import QGuiApplication
from PySide6.QtQml import QQmlApplicationEngine
from PySide6.QtQuick import QQuickItem
from PySide6.QtTest import QTest


FIXTURE = Path(__file__).with_name("hanging_menu_fixture.qml")


class SaveHost(QObject):
    def __init__(self):
        super().__init__()
        self.saved = []

    @Slot(str)
    def benchKeep(self, name):
        self.saved.append(name)


class HangingFileMenuTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QGuiApplication.instance() or QGuiApplication([])

    def setUp(self):
        self.host = SaveHost()
        self.engine = QQmlApplicationEngine()
        self.engine.rootContext().setContextProperty("host", self.host)
        self.engine.load(QUrl.fromLocalFile(str(FIXTURE)))
        self.window = self.engine.rootObjects()[0]
        self.menu = self.window.findChild(QQuickItem, "hangingFileMenu")
        self.panel = self.window.findChild(QQuickItem, "hangingFilePanel")
        self.assertIsNotNone(self.menu)
        self.assertIsNotNone(self.panel)
        self.menu.setProperty("open", True)
        self.settle(0.04)

    def tearDown(self):
        self.menu.setProperty("open", False)
        self.window.close()
        self.engine.deleteLater()

    def settle(self, seconds):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            self.app.processEvents()
            time.sleep(0.005)

    def point_on_panel(self, x, y):
        point = self.panel.mapToScene(QPointF(x, y))
        return QPoint(round(point.x()), round(point.y()))

    def click_panel(self, x, y):
        point = self.point_on_panel(x, y)
        QTest.mouseMove(self.window, point)
        self.app.processEvents()
        QTest.mouseClick(self.window, Qt.LeftButton, pos=point)
        self.app.processEvents()

    def test_both_actions_work_while_the_sign_is_angled(self):
        self.menu.setProperty("angle", 0.2)
        self.menu.setProperty("pitch", 0.3)
        self.click_panel(55, 51)
        self.assertTrue(self.menu.property("naming"))

        self.menu.setProperty("open", False)
        self.menu.setProperty("open", True)
        self.menu.setProperty("angle", -0.2)
        self.menu.setProperty("pitch", -0.3)
        self.click_panel(55, 25)
        self.assertEqual(self.host.saved, [""])
        self.assertFalse(self.menu.property("open"))

    def test_hover_and_growing_rows_stay_inside_the_window(self):
        self.menu.setProperty("angle", 0)
        self.menu.setProperty("pitch", 0)
        self.menu.setProperty("held", True)
        point = self.point_on_panel(20, 25)
        QTest.mouseMove(self.window, QPoint(450, 290))
        self.app.processEvents()
        before = self.window.grabWindow().pixelColor(point)
        QTest.mouseMove(self.window, point)
        self.app.processEvents()
        after = self.window.grabWindow().pixelColor(point)
        self.assertNotEqual(before, after)

        self.menu.setProperty("entries", [
            {"t": "SAVE", "a": "save"},
            {"t": "SAVE AS...", "a": "as"},
            {"t": "A MUCH LONGER FUTURE FILE MENU ACTION THAT MUST WRAP", "a": "future"},
            {"t": "ANOTHER ACTION", "a": "future"},
        ])
        self.settle(0.04)
        self.assertGreater(self.panel.property("height"), 100)
        self.assertGreaterEqual(self.panel.property("width"), 150)
        for angle in (-0.48, 0, 0.48):
            self.menu.setProperty("angle", angle)
            self.menu.setProperty("pitch", 0.7)
            self.app.processEvents()
            corners = [self.panel.mapToScene(QPointF(x, y))
                       for x in (0, self.panel.property("width"))
                       for y in (0, self.panel.property("height"))]
            self.assertTrue(all(0 <= p.x() < self.window.width() and
                                0 <= p.y() < self.window.height() for p in corners))


if __name__ == "__main__":
    unittest.main()
