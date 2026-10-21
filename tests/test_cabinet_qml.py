"""The cabinet's QML has to load and wire up without a window.

Loads cabinet.qml on the offscreen platform (no display, no focus taken) and checks
that it comes up clean: the tools drawer is there, the drawing tool is wired to the
host, and nothing in the QML raised a binding or type error.
"""
import os
import sys
import unittest
from pathlib import Path

CABINET = Path(__file__).resolve().parents[1] / "cabinet"
sys.path.insert(0, str(CABINET))
os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QObject, QUrl, qInstallMessageHandler  # noqa: E402

MESSAGES = []


def _collect(mode, context, message):
    MESSAGES.append(message)


class CabinetQmlTests(unittest.TestCase):
    engine = None

    @classmethod
    def setUpClass(cls):
        from PySide6.QtGui import QGuiApplication
        from PySide6.QtQml import QQmlApplicationEngine, qmlRegisterType
        import host as chest

        qInstallMessageHandler(_collect)
        cls.app = QGuiApplication.instance() or QGuiApplication([])
        if not hasattr(chest, "_viewport_type_registered"):
            qmlRegisterType(chest.Viewport, "Geppetto", 1, 0, "Viewport")
            chest._viewport_type_registered = True
        cls.host = chest.Host()
        cls.host._preferences_path = "/nonexistent/geppetto-test/preferences.json"
        chest.HOST = cls.host
        cls.engine = QQmlApplicationEngine()
        cls.engine.rootContext().setContextProperty("host", cls.host)
        cls.engine.load(QUrl.fromLocalFile(str(CABINET / "cabinet.qml")))
        cls.root = cls.engine.rootObjects()[0] if cls.engine.rootObjects() else None

    def qml_errors(self):
        bad = ("TypeError", "ReferenceError", "Unable to assign", "is not a function",
               "Cannot read property", "Binding loop", "Cannot create", "is not a type",
               "was not initialized")
        return [m for m in MESSAGES if any(b in m for b in bad)]

    def settle(self, seconds=0.4):
        """let the scene graph catch up: delegates are built on the next tick"""
        import time
        end = time.time() + seconds
        while time.time() < end:
            self.app.processEvents()
            time.sleep(0.01)

    def test_the_cabinet_loads(self):
        self.assertIsNotNone(self.root, "cabinet.qml did not load: " + "; ".join(MESSAGES[-8:]))
        self.assertEqual(self.qml_errors(), [])

    def test_reading_panels_have_solid_high_contrast_backgrounds(self):
        source = (CABINET / "cabinet.qml").read_text()
        self.assertNotIn('"sheet.png"', source)
        background = self.root.property("panelFill")

        def luminance(color):
            channels = [color.redF(), color.greenF(), color.blueF()]
            linear = [c / 12.92 if c <= .04045 else ((c + .055) / 1.055) ** 2.4 for c in channels]
            return sum(c * weight for c, weight in zip(linear, (.2126, .7152, .0722)))

        self.assertEqual(background.alphaF(), 1)
        for name in ("ink", "inkFaint", "inkRed"):
            contrast = (luminance(background) + .05) / (luminance(self.root.property(name)) + .05)
            self.assertGreaterEqual(contrast, 4.5, name)

    def test_labels_are_not_rasterized_at_half_size(self):
        source = (CABINET / "cabinet.qml").read_text()
        labels = source.split("component Px: Item {", 1)[1].split("component ModelSelect:", 1)[0]
        self.assertNotIn("scale: 2", labels)
        self.assertNotIn("layer.enabled: true", labels)
        self.assertIn("antialiasing: true", labels)
        self.assertIn("Math.max(14, px.size * 2)", labels)

    def test_the_right_drawer_is_the_tools_drawer(self):
        self.assertIsNotNone(self.root.findChild(QObject, "tools"))
        self.assertIsNone(self.root.findChild(QObject, "outliner"))

    def test_the_drawing_tool_is_wired_to_the_host(self):
        for name in ("openDrawingTool", "openDrawingFile", "refreshTools"):
            self.assertTrue(callable(getattr(self.host, name, None)), name + " is missing")
        self.assertIsInstance(self.host.drawings, list)
        self.assertIsInstance(self.host.drawingOpen, bool)

    def test_the_tools_drawer_lists_the_drawings(self):
        self.host.refreshTools()
        for drawing in self.host.drawings:
            self.assertIn("id", drawing)
            self.assertIn("name", drawing)
            self.assertTrue(drawing["path"].endswith(".linework"))
            self.assertTrue(drawing["thumb"].endswith(".jpg"))

    def test_the_drawing_kit_is_loose_in_the_tools_drawer(self):
        """the pencil and the drawings folder lie loose in the drawer, and the folder opens the drawings"""
        self.assertTrue(callable(getattr(self.host, "openDrawingsFolder", None)))
        kit = self.root.findChild(QObject, "drawingKit")
        self.assertIsNotNone(kit)
        for name in ("pencil3d", "drawingsFolder"):
            body = self.root.findChild(QObject, name)
            self.assertIsNotNone(body, name)
            self.assertEqual(body.property("world"), kit, name + " is not in the drawer's world")
        self.assertIsNotNone(self.root.findChild(QObject, "modelingBin"))
        self.assertEqual(self.qml_errors(), [])

    def test_a_sideways_yank_rolls_the_pencil(self):
        """opening the tools drawer leaves the pencil behind, so it rolls on its facets"""
        tools = self.root.findChild(QObject, "tools")
        pencil = self.root.findChild(QObject, "pencil3d")
        tools.setProperty("open", 0.0); self.settle(1.0)
        roll = pencil.property("roll")
        tools.setProperty("open", 1.0); self.settle(0.3)
        self.assertNotEqual(pencil.property("roll"), roll)
        self.assertEqual(self.qml_errors(), [])

    def test_the_timeline_has_transport_rule_and_view_keys(self):
        for name in ("transport", "rule", "viewKeys"):
            self.assertIsNotNone(self.root.findChild(QObject, name), name)
        self.assertEqual(self.qml_errors(), [])

    def _timeline_with_clip(self, frames, rate=30):
        """a clip as the viewer reports it, and a fake viewer process that records commands"""
        from PySide6.QtCore import QProcess

        class Viewer:
            def __init__(self): self.sent = []
            def state(self): return QProcess.Running
            def write(self, data): self.sent += data.decode().splitlines()

        viewer = Viewer()
        self.host.proc = viewer
        self.host._clip = {"path": "/x/walk.riganim", "name": "walk", "frames": frames, "rate": rate}
        self.host.infoChanged.emit()
        self.host._playhead = {"time": 0, "frame": 0, "playing": True, "loop": True}
        self.host.playheadChanged.emit()
        self.settle(0.2)
        return viewer, self.root.findChild(QObject, "timelineFloor")

    def tearDown(self):
        self.host.proc = None

    def test_the_rule_ticks_run_the_whole_length_of_a_long_clip(self):
        _, floor = self._timeline_with_clip(600)
        rule = self.root.findChild(QObject, "rule")
        every = rule.property("every")
        ticks = self.root.findChild(QObject, "ticks")
        self.assertLessEqual(ticks.property("count"), 121)
        self.assertEqual((ticks.property("count") - 1) * every, 600)
        # frame <-> x is one mapping both ways
        for f in (0, 1, 299, 600):
            self.assertEqual(floor.frameAt(floor.xOf(f) + 2), f)
        self.assertEqual(self.qml_errors(), [])

    def test_stepping_builds_on_what_was_asked_not_a_stale_reply(self):
        viewer, floor = self._timeline_with_clip(100)
        for _ in range(3):
            floor.step(1)                       # no reply from the viewer between clicks
        self.assertEqual(floor.property("shown"), 3)
        self.assertEqual(viewer.sent, ["time 0.03333", "time 0.06667", "time 0.10000"])
        self.assertFalse(floor.property("playing"))
        floor.seek(3)                           # the same frame again isn't sent again
        self.assertEqual(len(viewer.sent), 3)
        floor.seek(1000)                        # past the end clamps to the last frame
        self.assertEqual(floor.property("shown"), 100)
        # the viewer catches up: it is the truth again
        self.host._playhead = {"time": 100 / 30, "frame": 100, "playing": False, "loop": True}
        self.host.playheadChanged.emit()
        self.assertFalse(floor.property("waiting"))
        self.assertEqual(self.qml_errors(), [])

    def test_play_and_loop_keys_drive_the_viewer(self):
        viewer, floor = self._timeline_with_clip(100)
        floor.step(1)
        floor.setPlaying(True)
        self.assertFalse(floor.property("waiting"))
        self.assertEqual(viewer.sent[-1], "play 1")
        self.assertTrue(floor.property("looping"))
        self.assertIsNotNone(self.root.findChild(QObject, "loopKey"))
        self.assertEqual(self.qml_errors(), [])

    def test_chat_and_research_pop_out_into_their_own_windows(self):
        """POP OUT moves a panel into a resizable window; closing it docks it back in the drawer"""
        for flag, box, name in (("chatOut", "chatBox", "chatWindowBox"),
                                ("researchOut", "newRun", "researchWindowNewRun")):
            self.root.setProperty(flag, True)
            self.settle()
            popped = self.root.findChild(QObject, name)
            self.assertIsNotNone(popped, name)
            self.assertTrue(popped.window().isVisible())
            self.assertGreater(popped.window().width(), 254)
            self.assertFalse(self.root.findChild(QObject, box).property("visible"))
            popped.window().close()
            self.settle()
            self.assertFalse(self.root.property(flag))
            self.assertTrue(self.root.findChild(QObject, box).property("visible"))
            self.assertIsNone(self.root.findChild(QObject, name))
        self.assertEqual(self.qml_errors(), [])

    def test_long_chat_stays_fixed_and_does_not_overlap_research(self):
        box = self.root.findChild(QObject, "chatBox")
        run = self.root.findChild(QObject, "newRun")
        log = self.root.findChild(QObject, "chatLog")
        original = box.property("height")
        self.host._chat = [{"role": "model", "text": "A long answer with all its lines. " * 200}]
        self.host.chatChanged.emit()
        self.settle()
        self.assertEqual(box.property("height"), original)
        from PySide6.QtCore import QPointF
        box_bottom = box.mapToScene(QPointF(0, box.property("height"))).y()
        self.assertLessEqual(box_bottom, run.mapToScene(QPointF(0, 0)).y())
        self.assertTrue(log.property("clip"))
        self.assertGreater(log.property("contentHeight"), log.property("height"))
        self.assertLess(box.property("height"), 394)
        self.assertEqual(self.qml_errors(), [])

    def test_both_dropdowns_select_independently_and_stay_inside_drawer(self):
        from PySide6.QtCore import QMetaObject, Qt
        models = [{"id": "one", "label": "A very long local model name", "kind": "openai",
                   "endpoint": "http://localhost:9/", "model": "one", "available": True, "research": True}]
        models += [dict(models[0], id="other" + str(i), label="Other model " + str(i)) for i in range(25)]
        self.host._models_done(models)
        self.root.findChild(QObject, "research").setProperty("open", 1.0)
        self.settle()
        for name in ("chatSelector", "researchSelector"):
            selector = self.root.findChild(QObject, name)
            self.assertEqual(selector.property("count"), 26)
            popup = self.root.findChild(QObject, name + "Popup")
            self.assertIsNotNone(popup)
            QMetaObject.invokeMethod(popup, "open", Qt.DirectConnection)
            self.settle(.1)
            self.assertTrue(popup.property("visible"))
            self.assertGreaterEqual(popup.property("y"), 0)
            self.assertLessEqual(popup.property("y") + popup.property("height"), 414)
            self.assertLessEqual(popup.property("width"), 254)
            QMetaObject.invokeMethod(popup, "close", Qt.DirectConnection)
        self.host.setChatModel("other1")
        self.assertEqual(self.host.chatModel, "other1")
        self.assertEqual(self.host.aiModel, "one")
        self.assertEqual(self.qml_errors(), [])


    def _key_page(self):
        """the timeline drawer open on the KEYS page, with a two track clip"""
        viewer, floor = self._timeline_with_clip(30)
        self.host._keys = {"tracks": [
            {"name": "root", "kind": "Bone", "bone": 0, "keys": [0, 0, 10, 0, 30, 1]},
            {"name": "arm_L", "kind": "Bone", "bone": 1, "keys": [0, 0, 20, 5]}],
            "undo": 0, "redo": 0, "dirty": False, "saveable": True, "frames": 30}
        self.host.keysChanged.emit()
        self.root.findChild(QObject, "timeline").setProperty("open", 1.0)
        floor.setProperty("page", 1)
        self.settle(0.6)
        page = self.root.findChild(QObject, "keyPage")
        page.setSel({}, "")
        viewer.sent.clear()
        return viewer, floor, page, self.root.findChild(QObject, "keyLanes")

    def _at(self, item, x, y):
        from PySide6.QtCore import QPointF
        p = item.mapToScene(QPointF(x, y))
        return p.toPoint()

    def _key_point(self, page, lanes, track, frame):
        return self._at(lanes, page.xOf(frame), track * page.property("rowH") + page.property("rowH") / 2 - page.property("scrollY"))

    def test_the_wall_switch_flips_the_drawer_to_the_key_sheet(self):
        from PySide6.QtTest import QTest
        from PySide6.QtCore import Qt
        _, floor = self._timeline_with_clip(30)
        self.root.findChild(QObject, "timeline").setProperty("open", 1.0)
        floor.setProperty("page", 0)
        self.settle(0.6)
        switch = self.root.findChild(QObject, "pageSwitch")
        centre = self._at(switch, switch.width() / 2, switch.height() / 2)
        QTest.mouseClick(self.root, Qt.LeftButton, Qt.NoModifier, centre)
        self.settle(0.6)
        self.assertEqual(floor.property("page"), 1)
        self.assertFalse(self.root.findChild(QObject, "timePage").property("visible"), "the time page slid out of sight")
        self.assertTrue(self.root.findChild(QObject, "keyPage").property("visible"))
        QTest.mouseClick(self.root, Qt.LeftButton, Qt.NoModifier, centre)
        self.settle(0.6)
        self.assertEqual(floor.property("page"), 0)
        self.assertEqual(self.qml_errors(), [])

    def test_clicking_a_key_selects_it_and_seeks_there(self):
        from PySide6.QtTest import QTest
        from PySide6.QtCore import Qt
        viewer, _, page, lanes = self._key_page()
        QTest.mouseClick(self.root, Qt.LeftButton, Qt.NoModifier, self._key_point(page, lanes, 1, 20))
        self.settle(0.1)
        self.assertEqual(page.refs().split(), ["1:20"])
        self.assertEqual(page.property("activeTrack"), 1)
        self.assertIn("time 0.66667", viewer.sent)
        # shift adds to the selection, delete sends all of it in one command
        QTest.mouseClick(self.root, Qt.LeftButton, Qt.ShiftModifier, self._key_point(page, lanes, 0, 10))
        self.settle(0.1)
        self.assertEqual(page.property("selCount"), 2)
        page.deleteSel()
        self.assertEqual(sorted(viewer.sent[-1].split()[1:]), ["0:10", "1:20"])
        self.assertTrue(viewer.sent[-1].startswith("kdel "))
        self.assertEqual(page.property("selCount"), 0)
        self.assertEqual(self.qml_errors(), [])

    def test_dragging_keys_sends_one_move(self):
        from PySide6.QtTest import QTest
        from PySide6.QtCore import Qt, QPoint
        viewer, _, page, lanes = self._key_page()
        start = self._key_point(page, lanes, 0, 10)
        end = self._key_point(page, lanes, 0, 15)
        QTest.mousePress(self.root, Qt.LeftButton, Qt.NoModifier, start)
        for i in range(1, 6):
            QTest.mouseMove(self.root, QPoint(start.x() + (end.x() - start.x()) * i // 5, start.y()))
            self.settle(0.02)
        QTest.mouseRelease(self.root, Qt.LeftButton, Qt.NoModifier, end)
        self.settle(0.1)
        moves = [s for s in viewer.sent if s.startswith("kmove")]
        self.assertEqual(moves, ["kmove 5 0:10"])
        self.assertEqual(page.refs().split(), ["0:15"], "the selection follows the keys")
        self.assertEqual(self.qml_errors(), [])

    def test_key_row_buttons_send_insert_ease_undo_and_save(self):
        viewer, floor, page, lanes = self._key_page()
        page.setProperty("activeTrack", 1)
        floor.seek(7)
        page.insertKey()
        self.assertEqual(viewer.sent[-1], "kins 7 1")
        page.setSel({"0:10": True}, "0:10")
        page.cycleEase()
        self.assertTrue(viewer.sent[-1].startswith("kease ") and viewer.sent[-1].endswith(" 0:10"), viewer.sent[-1])
        page.edit("undo")
        self.assertEqual(viewer.sent[-1], "undo")
        for name in ("keyInsert", "keyDelete", "keyEase", "keyUndo", "keyRedo", "keySave", "keyReadout"):
            self.assertIsNotNone(self.root.findChild(QObject, name), name)
        self.assertEqual(self.qml_errors(), [])

    def test_a_dirty_clip_is_not_dropped_by_one_click(self):
        viewer, _, _, _ = self._key_page()
        self.host._keys = dict(self.host._keys, dirty=True)
        self.host.playClip("/x/run.riganim")
        self.assertFalse(any(s.startswith("clip ") for s in viewer.sent), "the first click only warns")
        self.host.playClip("/x/run.riganim")
        self.assertTrue(any(s.startswith("clip ") for s in viewer.sent), "the second click goes ahead")

if __name__ == "__main__":
    unittest.main()
