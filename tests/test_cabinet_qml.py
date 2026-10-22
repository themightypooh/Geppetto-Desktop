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

from PySide6.QtCore import QObject, QPoint, QPointF, Qt, QUrl, qInstallMessageHandler  # noqa: E402
from PySide6.QtGui import QGuiApplication, QWheelEvent  # noqa: E402
from PySide6.QtQuick import QQuickItem  # noqa: E402

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

    def test_a_live_run_shows_its_bar_with_hold_and_stop(self):
        """a research run in pane 1 says what it is doing and how long it has been quiet, with HOLD and STOP"""
        import time
        bar = self.root.findChild(QObject, "p1RunBar")
        self.assertIsNotNone(bar)
        saved = (self.host._runs, self.host.watching)
        self.host.runTimer.stop()                    # the run scan would put the real runs back
        try:
            self.host._runs = [{"dir": "/tmp/fake-run", "name": "fake-run", "prompt": "a pencil", "status": "running",
                                "activity": "web search", "paused": False, "bench": False,
                                "started": time.time() - 125, "lastEvent": time.time() - 90, "latestRevision": 0}]
            self.host.watching = "/tmp/fake-run"
            self.host.runsChanged.emit()
            self.settle(1.2)
            self.assertIsNotNone(bar.property("run"))          # pane 1 only shows when the chest has a view
            texts = [t.property("text") for t in bar.findChildren(QQuickItem) if t.property("text")]
            self.assertTrue(any("WEB SEARCH  2:0" in t and "quiet 1:3" in t for t in texts), texts)
            self.assertGreater(bar.property("quiet"), 60)
            for name in ("runBarHold", "runBarStop"):
                self.assertIsNotNone(self.root.findChild(QObject, name), name)
        finally:
            self.host._runs, self.host.watching = saved
            self.host.runTimer.start()
            self.host.runsChanged.emit()
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
        for flag, box, name in (("chatOut", "chatWell", "chatWindowBox"),
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

    def test_chat_note_opens_over_the_viewport_and_crumples_back(self):
        """the chat is a paper ball in the research drawer: open, it's a note on the viewport you can move; closed, it's back"""
        scroll = self.root.findChild(QObject, "paperBall")
        sheet = self.root.findChild(QObject, "openNote")
        self.root.findChild(QObject, "research").setProperty("open", 1.0)
        self.settle()
        self.assertTrue(scroll.property("visible"))
        self.assertFalse(sheet.property("visible"))
        self.root.setProperty("chatUnrolled", True)
        self.settle(1.0)
        self.assertFalse(scroll.property("visible"))
        self.assertTrue(sheet.property("visible"))
        self.assertGreater(sheet.property("width"), 400)
        self.assertLess(sheet.property("width"), self.root.property("g0").toVariant()["view"]["w"] / 2)
        self.assertEqual(sheet.property("scale"), 1)
        x0 = sheet.property("x")
        sheet.setProperty("homeX", sheet.property("homeX") + 120)    # where a drag leaves it
        self.settle()
        self.assertEqual(sheet.property("x"), x0 + 120)
        self.assertTrue(self.root.findChild(QObject, "chatNoteBox").property("enabled"))
        close = self.root.findChild(QObject, "chatNoteClose")
        self.assertTrue(close.property("visible"))
        close.clicked.emit()                                          # its close key crumples it
        self.settle(1.0)
        self.assertFalse(self.root.property("chatUnrolled"))
        self.assertFalse(sheet.property("visible"))
        self.assertTrue(scroll.property("visible"))
        self.assertEqual(self.qml_errors(), [])

    def test_long_chat_stays_fixed_inside_the_note(self):
        self.root.setProperty("chatUnrolled", True)
        self.settle(1.0)
        sheet = self.root.findChild(QObject, "openNote")
        box = self.root.findChild(QObject, "chatNoteBox")
        log = self.root.findChild(QObject, "chatNoteLog")
        original = box.property("height")
        self.host._chat = [{"role": "model", "text": "A long answer with all its lines. " * 200}]
        self.host.chatChanged.emit()
        self.settle()
        self.assertEqual(box.property("height"), original)
        from PySide6.QtCore import QPointF
        box_bottom = box.mapToScene(QPointF(0, box.property("height"))).y()
        sheet_bottom = sheet.mapToScene(QPointF(0, sheet.property("height"))).y()
        self.assertLessEqual(box_bottom, sheet_bottom)
        self.assertTrue(log.property("clip"))
        self.assertGreater(log.property("contentHeight"), log.property("height"))
        self.root.setProperty("chatUnrolled", False)
        self.settle(1.0)
        self.assertEqual(self.qml_errors(), [])

    def test_both_dropdowns_select_independently_and_stay_inside_drawer(self):
        from PySide6.QtCore import QMetaObject, Qt
        models = [{"id": "one", "label": "A very long local model name", "kind": "openai",
                   "endpoint": "http://localhost:9/", "model": "one", "available": True, "research": True}]
        models += [dict(models[0], id="other" + str(i), label="Other model " + str(i)) for i in range(25)]
        self.root.findChild(QObject, "research").setProperty("open", 1.0)
        self.root.setProperty("chatUnrolled", True)
        self.settle(1.0)
        self.host._models_done(models)
        self.settle()
        for name, room in (("chatNoteSelector", 576), ("researchSelector", 414)):
            selector = self.root.findChild(QObject, name)
            self.assertEqual(selector.property("count"), 26)
            popup = self.root.findChild(QObject, name + "Popup")
            self.assertIsNotNone(popup)
            QMetaObject.invokeMethod(popup, "open", Qt.DirectConnection)
            self.settle(.1)
            self.assertTrue(popup.property("visible"))
            self.assertGreaterEqual(popup.property("y"), 0)
            self.assertLessEqual(popup.property("y") + popup.property("height"), room)
            self.assertLessEqual(popup.property("width"), 400)
            QMetaObject.invokeMethod(popup, "close", Qt.DirectConnection)
        self.root.setProperty("chatUnrolled", False)
        self.settle(1.0)
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

    def test_the_tool_card_builds_one_script_line_from_what_you_changed(self):
        card = self.root.findChild(QObject, "toolCard")
        self.assertIsNotNone(card)
        size = {"key": "size", "label": "Size", "kind": "point", "value": "1,1,1"}
        name = {"key": "name", "label": "Name", "kind": "text", "value": ""}
        card.setProperty("bench", {"ready": True, "bodies": ["Hull"],
                                   "form": {"kind": "add", "type": "Primitive", "key": "primitive", "fields": [size, name]}})
        self.assertEqual(card.line(), "add Primitive", "untouched fields stay out of the line")
        card.setValue(size, "2,1,0.5")
        card.setValue(name, "My Box")
        self.assertEqual(card.line(), 'add Primitive size=2,1,0.5 name="My Box"')
        body = {"key": "body", "label": "Body", "kind": "bodies", "value": ""}
        dist = {"key": "distance", "label": "Distance", "kind": "number", "value": 0.1}
        card.setProperty("bench", {"ready": True, "bodies": ["Hull", "Lid"],
                                   "form": {"kind": "edit", "verb": "edit", "op": "bevel", "key": "edit:bevel", "fields": [body, dist]}})
        card.setValue(dist, "0.25")
        self.assertEqual(card.line(), "edit Lid bevel distance=0.25", "an edit goes to the last body made")
        card.setProperty("bench", {"ready": True, "bodies": [],
                                   "form": {"kind": "edit", "verb": "edit", "op": "bevel", "key": "edit:bevel", "fields": [body, dist]}})
        self.assertEqual(card.line(), "")
        self.assertIn("add something first", card.property("why"))
        card.setProperty("open", True)
        self.settle(0.05)
        callout = self.root.findChild(QObject, "benchError")
        self.assertTrue(callout.property("visible"), "the reason is called out over the viewport")
        self.assertIn("add something first", callout.property("shown"))
        card.setProperty("open", False)
        self.assertEqual(self.qml_errors(), [])

    def test_the_wheel_key_works_while_a_run_is_drawing_on_the_wheel(self):
        wheel = self.root.findChild(QObject, "toolWheel")
        key = self.root.findChild(QObject, "wheelKey")
        wheel.setProperty("manual", False)
        wheel.setProperty("op", "add Primitive")
        key.clicked.emit()
        self.assertTrue(wheel.property("manual"), "WHEEL opens the wheel by hand mid-run")
        wheel.setProperty("op", "edit Hull bevel")
        self.settle(0.1)
        self.assertTrue(wheel.property("manual"), "a run op does not take the wheel back")
        key.clicked.emit()
        self.assertFalse(wheel.property("manual"))
        wheel.setProperty("op", "")
        self.assertEqual(self.qml_errors(), [])

    def test_the_wheel_moves_with_the_model_and_only_with_it(self):
        from PySide6.QtCore import QMetaObject, Q_ARG, Q_RETURN_ARG
        wheel = self.root.findChild(QObject, "toolWheel")
        wheel.setProperty("manual", False)

        def line():
            v = wheel.property("info")
            return (v.toVariant() if hasattr(v, "toVariant") else v)["line"]

        def parse(op):
            v = QMetaObject.invokeMethod(wheel, "parse", Q_RETURN_ARG("QVariant"), Q_ARG("QVariant", op))
            return v.toVariant() if hasattr(v, "toVariant") else v
        # every kind of step a run, a replay or the bench writes lands on its tool
        for op, key, target in [("line 3: add Primitive name=Box shape=box", "primitive", "Box"),
                                ("add Blob Grip", "blob", "Grip"),
                                ("add BlobFeature", "blob", ""),
                                ("line 5: edit Box bevel width=0.1", "edit:bevel", "Box"),
                                ("line 6: edit Box extrude individual", "edit:extrude", "Box"),
                                ("line 7: edit Box Limited dissolve", "edit:dissolve", "Box"),
                                ("line 4: undo add Primitive name=Lid", "primitive", "Lid"),
                                ("line 8: material Box 0 color=#aa0000", "material", "Box"),
                                ("line 9: measure Box.top", "measure", "Box.top"),
                                ("line 2: set Box size=1,1,1", "set", "Box"),
                                ("start: steeltray.effigy", "import", "steeltray.effigy")]:
            p = parse(op)
            self.assertEqual((p["key"], p["target"]), (key, target), op)
        self.assertEqual(parse("line 3: add Primitive")["line"], 3)
        self.assertIsNone(parse("initial"), "the empty document changes nothing on screen")
        self.assertEqual(parse("echo hi")["key"], "echo", "a verb with no tile still names itself in the core")

        # a new revision on screen moves the wheel at once: no reach played out after the model changed
        wheel.setProperty("op", "line 3: add Primitive name=Box")
        self.assertEqual(wheel.property("tool"), "primitive")
        self.assertTrue(wheel.property("shown"))
        wheel.setProperty("op", "line 5: edit Box bevel")
        self.assertEqual(wheel.property("tool"), "edit:bevel", "lands on the step's tool the moment it is shown")
        self.assertEqual(wheel.property("openGroup"), 4)
        # the same tool again: the core presses in and the line moves on
        wheel.setProperty("op", "line 6: edit Box bevel width=0.2")
        self.assertEqual(line(), 6)
        core = wheel.findChild(QObject, "wheelCore")
        squash = 0
        for _ in range(15):
            self.settle(0.01)
            squash = max(squash, core.property("squash"))
        self.assertGreater(squash, 0.3, "the same tool again still visibly presses")
        # nothing on screen changed: the wheel holds still
        before = (wheel.property("tool"), wheel.property("openGroup"), line())
        wheel.setProperty("working", "line 7: edit Box inset")   # being built, not yet shown
        self.settle(0.3)
        self.assertEqual((wheel.property("tool"), wheel.property("openGroup"), line()), before,
                         "the step being built does not move the wheel before the model shows it")
        wheel.setProperty("working", "")
        wheel.setProperty("op", "")
        self.assertEqual(self.qml_errors(), [])

    def _modeling_sandbox(self):
        """RUNS and MODELING pointed at a temp dir for one test: a finished run with a sticker, and a bench"""
        import json
        import tempfile
        import host as chest
        tmp = tempfile.mkdtemp(prefix="geppetto-folder-")
        saved = chest.RUNS, chest.MODELING
        chest.RUNS, chest.MODELING = os.path.join(tmp, "research"), os.path.join(tmp, "modeling")
        def restore():
            chest.RUNS, chest.MODELING = saved
            self.host._bench, self.host._filed = {}, chest.filed_stickers()
            self.host.benchChanged.emit()
            self.host.filedChanged.emit()
        self.addCleanup(restore)
        run = os.path.join(chest.RUNS, "script-build-20261022-044521")
        os.makedirs(run)
        open(os.path.join(run, "sticker.png"), "wb").write(b"png")
        json.dump({"prompt": "script: build.txt", "script": "/x/folders/steeltray/build.txt"}, open(run + ".json", "w"))
        bench = os.path.join(chest.RUNS, "bench-steeltray-20261022-050553")
        os.makedirs(os.path.join(bench, ".live", "revisions"))
        open(os.path.join(bench, ".live", "revisions", "00000002.effigy"), "w").write("edited")
        return chest, run, bench

    def test_keep_files_your_edit_in_the_modeling_folder_and_saves_it_back_in_place(self):
        chest, run, bench = self._modeling_sandbox()
        self.host._bench = {"dir": bench, "revision": 2, "from": "script-build rev 11", "back": {"watching": run}}
        kept = self.host.benchKeep("")
        self.assertEqual(kept, os.path.join(chest.MODELING, "steeltray", "steeltray.effigy"),
                         "named after what the run built, in a folder of its own")
        self.assertEqual(open(kept).read(), "edited")
        open(os.path.join(bench, ".live", "revisions", "00000002.effigy"), "w").write("edited again")
        self.host._bench = {"dir": bench, "revision": 2, "from": "steeltray.effigy", "back": {"model": kept}}
        self.assertEqual(self.host.benchKeep(""), kept, "a model opened from the folder is saved over in place")
        self.assertEqual(open(kept).read(), "edited again")
        self.assertEqual(os.listdir(chest.MODELING), ["steeltray"])
        self.host._bench = {"dir": bench, "from": "empty"}
        self.assertEqual(self.host.benchKeep(""), "")
        self.assertIn("make an edit first", self.host._bench["error"])

    def test_the_modeling_folder_opens_and_lists_what_is_filed(self):
        chest, run, bench = self._modeling_sandbox()
        os.makedirs(os.path.join(chest.MODELING, "knob"))
        open(os.path.join(chest.MODELING, "knob", "sticker.png"), "wb").write(b"png")
        open(os.path.join(chest.MODELING, "knob", "knob.effigy"), "w").write("doc")
        self.host._filed = chest.filed_stickers()
        self.host.filedChanged.emit()
        folder = self.root.findChild(QObject, "folderView")
        self.assertIsNotNone(self.root.findChild(QObject, "modelingOpen"), "the folder itself is clickable")
        folder.setProperty("open", True)
        self.settle(0.1)
        names = sorted((e["name"], e["kind"]) for e in folder.property("items"))
        self.assertEqual(names, [("knob", "model"), ("steeltray", "run")], "bench copies aren't filed; runs are named for what they built")
        self.assertEqual(self.root.findChild(QObject, "folderSlips").property("count"), 2, "a slip for each")
        opened = []
        self.host.openModel = lambda p, clip="": opened.append(("model", p))
        self.host.watchRun = lambda d: opened.append(("run", d))
        try:
            for i, e in enumerate(folder.property("items")):
                self.host.openFiled(i)
        finally:
            del self.host.openModel, self.host.watchRun
        self.assertEqual(sorted(opened), [("model", os.path.join(chest.MODELING, "knob", "knob.effigy")), ("run", run)])
        # more files than fit: the scroll wheel runs through them
        self.host._filed = self.host._filed * 6
        self.host.filedChanged.emit()
        self.settle(0.6)
        files = self.root.findChild(QQuickItem, "folderFiles")
        self.assertTrue(files.property("interactive"))
        self.assertGreater(files.property("contentHeight"), files.height())
        at = files.mapToScene(QPointF(files.width() / 2, files.height() / 2))
        QGuiApplication.sendEvent(self.root, QWheelEvent(at, self.root.mapToGlobal(at.toPoint()), QPoint(), QPoint(0, -120),
                                                         Qt.NoButton, Qt.NoModifier, Qt.NoScrollPhase, False))
        self.settle(0.6)
        self.assertGreater(files.property("contentY"), 0, "wheeled down through the files")
        folder.setProperty("open", False)
        self.assertEqual(self.qml_errors(), [])

if __name__ == "__main__":
    unittest.main()
