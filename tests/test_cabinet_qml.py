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

from PySide6.QtCore import QMetaObject, QObject, QPoint, QPointF, Q_ARG, Qt, QUrl, qInstallMessageHandler  # noqa: E402
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

    def test_live_now_lists_every_running_run_with_who_and_the_step(self):
        """LIVE NOW in the research drawer: a row per running run, who is on it and the step it is on, nothing for ended runs"""
        import time
        saved = (self.host._runs, self.host.watching)
        self.host.runTimer.stop()                    # the run scan would put the real runs back
        try:
            base = {"status": "running", "paused": False, "bench": False, "latestRevision": 3, "started": time.time() - 30,
                    "lastEvent": time.time(), "openState": ""}
            self.host._runs = [dict(base, dir="/tmp/r1", name="script-rolodex", prompt="script: rolodex", title="rolodex", who="Right drawer rolodex",
                                    pane=2, activity="building", watch={"line": 14, "total": 31, "say": "Card 12: a flat slab, so the wheel fills in"}),
                               dict(base, dir="/tmp/r2", name="piranha2", prompt="a piranha", title="piranha2", who="", pane=0, activity="researching", watch={}),
                               dict(base, dir="/tmp/r3", name="old", prompt="an old run", title="old", status="ended", who="", pane=0, watch={})]
            self.host.runsChanged.emit()
            self.settle(0.8)
            card = self.root.findChild(QObject, "liveNow")
            self.assertTrue(card.property("visible"))
            self.assertEqual(len(card.property("live").toVariant()), 2, "the two running runs, not the ended one")
            QMetaObject.invokeMethod(card, "probeRows")
            rows = card.property("rowProbe").toVariant()
            self.assertEqual(len(rows), 2)
            texts = [r["who"] + " | " + r["doing"] + r["where"] for r in rows]
            self.assertIn("Right drawer rolodex | Card 12: a flat slab, so the wheel fills in (14/31)", texts)
            self.assertIn("someone | researching", texts)
            for r in rows:                       # every line one elided line: the box is a fixed height, nothing spills
                self.assertEqual(r["height"], card.property("rowH"))
                self.assertTrue(r["clip"])
            self.assertEqual(self.qml_errors(), [])
        finally:
            self.host._runs, self.host.watching = saved
            self.host.runsChanged.emit()
            self.host.runTimer.start()

    def test_a_run_landing_in_another_pane_asks_before_moving_anyone(self):
        """the chest puts a live run in a pane you are not in: a YES/NO sheet asks; NO leaves it, YES acts; your own pane asks nothing"""
        import time
        saved = (self.host._runs, self.host.watching)
        self.host.runTimer.stop()                    # the run scan would put the real runs back, and land them again
        try:
            self.host._runs = [{"dir": "/tmp/r1", "name": "script-rolodex", "prompt": "script", "title": "rolodex", "who": "Grok", "status": "running",
                                "paused": False, "bench": False, "latestRevision": 3, "started": time.time(), "lastEvent": time.time(), "pane": 2, "watch": {}}]
            self.host.runsChanged.emit()
            ask = self.root.findChild(QObject, "joinAsk")
            self.host.waiting[:] = []                    # the chest keeps trying to land real runs: not in this test
            self.host.auto_pending.clear()
            self.settle(0.3)                             # a landing already on its way arrives now, not mid-test
            QMetaObject.invokeMethod(ask, "no")          # it may have landed one at start
            self.root.setProperty("activePane", 1)
            self.host.runLanded.emit("/tmp/r1", 1)
            self.settle(0.3)
            self.assertFalse(ask.property("visible"), "landed in the pane you are in: nothing to ask")
            self.host.runLanded.emit("/tmp/r1", 2)
            self.settle(0.3)
            self.assertTrue(ask.property("visible"))
            self.assertEqual(self.root.findChild(QObject, "joinAskWho").property("text"), "GROK started: rolodex")
            self.assertIn("pane 2", ask.property("question"))
            if ask.property("mineBusy"):                 # the real chest decides whether pane 1 is free
                self.assertIn("Go there?", ask.property("question"), "pane 1 is busy: offer to go to pane 2")
            else:
                self.assertIn("your pane 1", ask.property("question"), "pane 1 is free: offer to show it there")
            QMetaObject.invokeMethod(ask, "no")
            self.assertFalse(ask.property("visible"))
            self.assertEqual(self.root.property("activePane"), 1, "NO changes nothing")
            self.assertEqual(self.qml_errors(), [])
        finally:
            QMetaObject.invokeMethod(ask, "no")
            self.host._runs, self.host.watching = saved
            self.host.runsChanged.emit()
            self.host.runTimer.start()

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
            panel = self.root.findChild(QObject, "paneHeader1")
            self.assertIs(bar.parent().parent(), panel)
            telemetry = panel.property("detailText")
            self.assertIn("WEB SEARCH", telemetry)
            self.assertIn("updated 9", telemetry)
            self.assertIsNone(self.root.findChild(QObject, "modellingNow"))
            for name in ("runBarHold", "runBarStop"):
                self.assertIsNotNone(self.root.findChild(QObject, name), name)
        finally:
            self.host._runs, self.host.watching = saved
            self.host.runTimer.start()
            self.host.runsChanged.emit()
        self.assertEqual(self.qml_errors(), [])

    def test_a_pane_header_drops_a_menu_to_make_put_and_split(self):
        """click PANE n: TOOLS, DRAW, PUT A MODEL HERE >, PANES >; TOOLS opens the wheel"""
        menu = self.root.findChild(QObject, "paneMenu")
        wheel = self.root.findChild(QObject, "toolWheel")
        self.assertIsNotNone(menu)
        self.assertIsNotNone(self.root.findChild(QObject, "paneMenuKey1"))
        self.assertIsNone(self.root.findChild(QObject, "viewportsMenu"))     # the lid's dropdown is gone
        menu.setProperty("n", 1)
        menu.metaObject().invokeMethod(menu, "build")
        rows = menu.property("rows")
        rows = rows.toVariant() if hasattr(rows, "toVariant") else rows
        self.assertEqual([r.get("k") for r in rows], ["tools", "draw", "page:put", "page:panes"])
        tools = next(r for r in rows if r.get("k") == "tools")
        wheel.setProperty("manual", False)
        try:
            from PySide6.QtCore import QMetaObject, Q_ARG
            QMetaObject.invokeMethod(menu, "act", Q_ARG("QVariant", tools))
            self.settle(0.2)
            self.assertTrue(wheel.property("manual"))
            self.assertFalse(menu.property("open"))
        finally:
            wheel.setProperty("manual", False)
        self.assertEqual(self.qml_errors(), [])

    def test_a_held_script_run_shows_line_part_error_and_its_steps(self):
        """LIVE BUILD's NOTE key tosses out the build note: LINE x of n, the part, the line, a held run's
        error, every step newest first; the pane itself stays clear"""
        import time
        from PySide6.QtCore import QMetaObject, Q_ARG
        self.assertIsNone(self.root.findChild(QObject, "watchPanel1"))       # nothing squeezed into the pane
        note = self.root.findChild(QObject, "buildNote")
        self.assertIsNotNone(note)
        saved = (self.host._runs, self.host.watching)
        self.host.runTimer.stop()
        try:
            steps = [{"rev": i, "op": "line %d: x" % i, "code": "set Eraser blend=0.0%d" % i, "part": "Eraser"} for i in range(1, 9)]
            self.host._runs = [{"dir": "/tmp/fake-run", "name": "fake-run", "prompt": "script", "status": "running",
                                "activity": "held at a bad line", "paused": False, "bench": False, "openState": "held",
                                "outcome": "line 9: no feature 'Eraserr'", "started": time.time() - 5, "lastEvent": time.time(),
                                "latestRevision": 8, "watch": {"line": 9, "total": 12, "code": "set Eraserr voxel=0.012",
                                "part": "Eraserr", "building": False, "error": "line 9: no feature 'Eraserr'",
                                "steps": steps, "count": 8}}]
            self.host.watching = "/tmp/fake-run"
            self.host.runsChanged.emit()
            self.settle(1.2)
            key = self.root.findChild(QObject, "liveNote")
            if key is None:
                row = self.root.findChild(QObject, "liveBuild")
                names = [c.objectName() for c in self.root.findChildren(QObject) if c.objectName()]
                self.fail("no liveNote; open=%s count=%s probe=%s named=%s" % (
                    row.property("open") if row else None,
                    row.property("keyCount") if row else None,
                    row.property("noteProbe") if row else None,
                    [n for n in names if "live" in n.lower() or "Note" in n or "note" in n]))
            self.assertTrue(key.property("visible"))
            self.assertEqual(key.property("label"), "NOTE")
            self.assertIn("9/12", key.property("tip"))
            QMetaObject.invokeMethod(self.root, "toggleBuildNote", Q_ARG("QVariant", "/tmp/fake-run"), Q_ARG("QVariant", 1), Q_ARG("QVariant", key))
            self.settle(1.2)
            self.assertTrue(note.property("visible"))
            self.assertEqual(note.property("t"), 1)
            def walk(item):                              # list rows sit in the visual tree, not the object tree
                for c in item.childItems():
                    yield c
                    yield from walk(c)
            texts = [t.property("text") for t in walk(note) if isinstance(t.property("text"), str)]
            self.assertIn("LINE 9 of 12", texts)
            self.assertIn("Eraserr: set Eraserr voxel=0.012", texts)
            self.assertTrue(any(t.startswith("HELD: line 9: no feature") for t in texts), texts)
            rows = [t for t in texts if t.startswith("R") and t[1:2].isdigit()]
            self.assertTrue(rows and rows[0].startswith("R8"), rows)          # newest first
            QMetaObject.invokeMethod(self.root, "toggleBuildNote", Q_ARG("QVariant", "/tmp/fake-run"), Q_ARG("QVariant", 1), Q_ARG("QVariant", key))
            self.settle(1.0)
            self.assertFalse(note.property("visible"))                         # crumpled back
        finally:
            self.root.setProperty("buildNoteDir", "")
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
        """the pencil lies loose in the drawer"""
        self.assertTrue(callable(getattr(self.host, "openDrawingsFolder", None)))
        kit = self.root.findChild(QObject, "drawingKit")
        self.assertIsNotNone(kit)
        body = self.root.findChild(QObject, "pencil3d")
        self.assertIsNotNone(body)
        self.assertEqual(body.property("world"), kit, "pencil3d is not in the drawer's world")
        self.assertIsNotNone(self.root.findChild(QObject, "modelingBin"))
        self.assertEqual(self.qml_errors(), [])

    def test_the_pencil_brings_up_the_sketch_carousel(self):
        """a click on the pencil fans the starting tools along the bottom of the viewport;
        clicking the front card opens that tool's card, like picking it off the wheel"""
        car = self.root.findChild(QObject, "sketchCarousel")
        self.assertIsNotNone(car)
        tools = car.property("tools").toVariant()
        keys = [t[0] for t in tools]
        self.assertEqual(sorted(keys), sorted(["primitive", "blob", "part", "sketch", "profile", "spline", "plane", "import", "bodyfrombones"]))
        self.assertFalse(car.property("open"))
        pencil = self.root.findChild(QObject, "pencil3d")
        pencil.tapped.emit(); self.settle(0.3)
        self.assertTrue(car.property("open"))
        self.assertEqual(car.property("front"), 4)                 # the fan starts centred
        QMetaObject.invokeMethod(car, "turn", Q_ARG("QVariant", -2)); self.settle(0.3)
        self.assertEqual(car.property("front"), 2)
        card = self.root.findChild(QObject, "toolCard")
        QMetaObject.invokeMethod(car, "use", Q_ARG("QVariant", 2)); self.settle(0.3)
        self.assertFalse(car.property("open"))
        self.assertTrue(card.property("open"))
        self.assertEqual(card.property("label"), tools[2][1])
        pencil.tapped.emit(); self.settle(0.3)
        self.assertTrue(car.property("open"))
        pencil.tapped.emit(); self.settle(0.3)             # the pencil puts it away too
        self.assertFalse(car.property("open"))
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
        """POP OUT moves a panel into a resizable window; closing it docks it back in the drawer.
        The paper ball is the research, so the chat window does not take it"""
        self.root.setProperty("chatOut", True)
        self.settle()
        popped = self.root.findChild(QObject, "chatWindowBox")
        self.assertIsNotNone(popped)
        self.assertTrue(popped.window().isVisible())
        self.assertGreater(popped.window().width(), 254)
        self.assertTrue(self.root.findChild(QObject, "chatWell").property("visible"))
        popped.window().close()
        self.settle()
        self.assertFalse(self.root.property("chatOut"))
        self.assertIsNone(self.root.findChild(QObject, "chatWindowBox"))

        self.root.setProperty("researchOut", True)
        self.settle()
        popped = self.root.findChild(QObject, "researchWindowNewRun")
        self.assertIsNotNone(popped)
        self.assertTrue(popped.window().isVisible())
        self.assertGreater(popped.window().width(), 254)
        self.assertFalse(self.root.findChild(QObject, "newRun").property("visible"))
        popped.window().close()
        self.settle()
        self.assertFalse(self.root.property("researchOut"))
        self.assertTrue(self.root.findChild(QObject, "newRun").property("visible"))
        self.assertIsNone(self.root.findChild(QObject, "researchWindowNewRun"))
        self.assertEqual(self.qml_errors(), [])

    def test_the_paper_opens_the_research_and_crumples_back(self):
        """the paper ball in the research drawer opens the research column; closed, the ball is back and the column is in the drawer"""
        scroll = self.root.findChild(QObject, "paperBall")
        sheet = self.root.findChild(QObject, "openNote")
        panel = self.root.findChild(QObject, "researchPanel")
        self.root.findChild(QObject, "research").setProperty("open", 1.0)
        self.settle()
        self.assertTrue(scroll.property("visible"))
        self.assertFalse(sheet.property("visible"))
        self.assertEqual(panel.property("parent").objectName(), "researchDock")
        self.root.setProperty("paperOpen", True)
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
        self.assertEqual(panel.property("parent").objectName(), "paperResearchScroll")
        self.assertTrue(panel.property("enabled"))
        self.assertFalse(self.root.findChild(QObject, "researchDock").property("height") > 1)
        close = self.root.findChild(QObject, "paperClose")
        self.assertTrue(close.property("visible"))
        close.clicked.emit()                                          # its close key crumples it
        self.settle(1.0)
        self.assertFalse(self.root.property("paperOpen"))
        self.assertFalse(sheet.property("visible"))
        self.assertTrue(scroll.property("visible"))
        self.assertEqual(panel.property("parent").objectName(), "researchDock")
        self.assertEqual(self.qml_errors(), [])

    def test_research_stays_inside_the_paper(self):
        self.root.setProperty("paperOpen", True)
        self.settle(1.0)
        sheet = self.root.findChild(QObject, "openNote")
        box = self.root.findChild(QObject, "paperResearchScroll")
        original = box.property("height")
        self.assertGreater(original, 100)
        self.host._chat = [{"role": "model", "text": "A long answer with all its lines. " * 200}]
        self.host.chatChanged.emit()
        self.settle()
        self.assertEqual(box.property("height"), original)            # the chat growing does not stretch the paper
        from PySide6.QtCore import QPointF
        box_bottom = box.mapToScene(QPointF(0, box.property("height"))).y()
        sheet_bottom = sheet.mapToScene(QPointF(0, sheet.property("height"))).y()
        self.assertLessEqual(box_bottom, sheet_bottom + 1)
        self.assertTrue(box.property("clip"))
        self.root.setProperty("paperOpen", False)
        self.settle(1.0)
        self.assertEqual(self.qml_errors(), [])

    def test_both_dropdowns_select_independently_and_stay_inside_drawer(self):
        from PySide6.QtCore import QMetaObject, Qt
        models = [{"id": "one", "label": "A very long local model name", "kind": "openai",
                   "endpoint": "http://localhost:9/", "model": "one", "available": True, "research": True}]
        models += [dict(models[0], id="other" + str(i), label="Other model " + str(i)) for i in range(25)]
        self.root.findChild(QObject, "research").setProperty("open", 1.0)
        self.root.setProperty("paperOpen", True)
        self.root.setProperty("chatOut", True)
        self.settle(1.0)
        self.host._models_done(models)
        self.settle()
        # research dropdown lives on the paper; the chat dropdown lives in the chat window
        for name in ("chatWindowSelector", "researchSelector"):
            selector = self.root.findChild(QObject, name)
            self.assertIsNotNone(selector, name)
            self.assertEqual(selector.property("count"), 26, name)
            popup = self.root.findChild(QObject, name + "Popup")
            self.assertIsNotNone(popup, name)
            visual = popup.property("parent")
            self.assertIsNotNone(visual, name)
            room = visual.property("height")
            QMetaObject.invokeMethod(popup, "open", Qt.DirectConnection)
            self.settle(.1)
            self.assertTrue(popup.property("visible"), name)
            self.assertGreaterEqual(popup.property("y"), 0, name)
            self.assertLessEqual(popup.property("y") + popup.property("height"), room + 1, name)
            self.assertLessEqual(popup.property("width"), visual.property("width"), name)
            QMetaObject.invokeMethod(popup, "close", Qt.DirectConnection)
        self.root.setProperty("paperOpen", False)
        self.root.setProperty("chatOut", False)
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
        floor.setProperty("page", 2)  # KEYS moved to page 2: 0 BUILD, 1 CLIP, 2 KEYS
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

    def test_the_wall_switch_flips_the_drawer_to_each_sheet(self):
        # Three-position switch: top third -> BUILD (page 0), middle third ->
        # CLIP (page 1), bottom third -> KEYS (page 2). A centre click now
        # means CLIP, not KEYS, and clicks jump to a detent, not toggle.
        from PySide6.QtTest import QTest
        from PySide6.QtCore import Qt
        _, floor = self._timeline_with_clip(30)
        self.root.findChild(QObject, "timeline").setProperty("open", 1.0)
        floor.setProperty("page", 0)
        self.settle(0.6)
        switch = self.root.findChild(QObject, "pageSwitch")
        build_page = self.root.findChild(QObject, "buildPage")
        time_page = self.root.findChild(QObject, "timePage")
        key_page = self.root.findChild(QObject, "keyPage")

        def click_at(yfrac):
            QTest.mouseClick(self.root, Qt.LeftButton, Qt.NoModifier,
                             self._at(switch, switch.width() / 2, switch.height() * yfrac))
            self.settle(0.6)
        click_at(0.5)
        self.assertEqual(floor.property("page"), 1)
        self.assertTrue(time_page.property("visible"))
        self.assertFalse(build_page.property("visible"))
        self.assertFalse(key_page.property("visible"))
        click_at(0.85)
        self.assertEqual(floor.property("page"), 2)
        self.assertTrue(key_page.property("visible"), "the key sheet slid into sight")
        self.assertFalse(time_page.property("visible"), "the time page slid out of sight")
        click_at(0.15)
        self.assertEqual(floor.property("page"), 0)
        self.assertTrue(build_page.property("visible"))
        self.assertFalse(time_page.property("visible"))
        self.assertFalse(key_page.property("visible"))
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

        # a run's step does not put the wheel on the model. it comes up only when opened by hand
        wheel.setProperty("op", "line 3: add Primitive name=Box")
        self.assertFalse(wheel.property("shown"))
        wheel.setProperty("manual", True)
        self.assertTrue(wheel.property("shown"))
        wheel.setProperty("op", "line 5: edit Box bevel")
        self.assertTrue(wheel.property("manual"))
        self.assertTrue(wheel.property("shown"))
        wheel.setProperty("manual", False)
        self.assertFalse(wheel.property("shown"))
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

    def test_the_models_mini_drawer_pulls_out_with_the_folder_riding_in_it(self):
        mini = self.root.findChild(QObject, "miniModels")
        self.assertIsNotNone(mini, "the MODELS mini drawer is in the tools drawer")
        self.assertIsNotNone(self.root.findChild(QObject, "miniGrab"), "its front is a handle you can grab")
        folder = self.root.findChild(QObject, "folderView")
        bin_ = self.root.findChild(QQuickItem, "modelingBin")
        QMetaObject.invokeMethod(mini, "setOpen", Q_ARG("QVariant", False))   # an earlier test may have left it out
        self.settle(0.9)
        self.assertEqual(mini.property("slide"), 0, "it starts shut")
        shut_y = bin_.property("y")
        QMetaObject.invokeMethod(mini, "setOpen", Q_ARG("QVariant", True))
        self.settle(0.9)
        self.assertEqual(mini.property("slide"), mini.property("travel"), "pulled all the way out, stepped like the big drawers")
        self.assertTrue(folder.property("open"), "the folder opens as its drawer comes out")
        # floor coords, not scene: the floor itself scrolls to follow the front, which is the point
        self.assertAlmostEqual(bin_.property("y") - shut_y, mini.property("travel"), delta=3,
                               msg="the folder — and everything in it — rode out with the drawer")
        QMetaObject.invokeMethod(mini, "setOpen", Q_ARG("QVariant", False))
        self.settle(0.9)
        self.assertEqual(mini.property("slide"), 0, "and it shoves home again")
        self.assertFalse(folder.property("open"))
        self.assertEqual(self.qml_errors(), [])

    # ---- the tutorial card and pipeline tape were removed (2026-10-24, at po's word) -----
    def test_there_is_no_guide_card_pipeline_tape_or_guide_key(self):
        """po did not approve the tutorial UI and ordered it out: no first-run card,
        no pipeline tape along the frame's bottom edge, no GUIDE key anywhere."""
        self.assertIsNone(self.root.findChild(QObject, "guideCard"),
                          "the tutorial card is gone")
        self.assertIsNone(self.root.findChild(QObject, "pipelineTape"),
                          "the pipeline tape is gone")
        self.assertIsNone(self.root.findChild(QObject, "pipelineGuide"),
                          "and no GUIDE key left behind")
        self.assertFalse(hasattr(self.host, "guide"),
                         "the host no longer carries a guide property")
        self.assertEqual(self.qml_errors(), [])

    def test_the_mouse_and_wheel_only_move_the_pane_they_are_in(self):
        """quad view, cameras LINKED: drag-orbit and wheel-zoom in one pane must reach that
        pane's viewer only — the mouse is in one pane, so it moves one pane, link or no link"""
        from PySide6.QtCore import QProcess, Q_RETURN_ARG
        from PySide6.QtGui import QMouseEvent

        class FakeProc:
            def __init__(self):
                self.lines = []

            def state(self):
                return QProcess.Running

            def write(self, data):
                self.lines.append(bytes(data).decode())
                return len(data)

        saved = (self.host._layout, self.host._link, self.host.proc,
                 [p.proc for p in self.host.panes])
        self.host.runTimer.stop()                # the run scan would talk to the viewers itself
        fakes = {1: FakeProc()}
        try:
            self.host._layout, self.host._link = 4, True
            self.host.proc = fakes[1]
            for p in self.host.panes:
                fakes[p.number] = FakeProc()
                p.proc = fakes[p.number]
            self.host.panesChanged.emit()
            self.settle(0.8)                     # panes 2-4 build; their size lines land here

            def lines():
                return {n: list(f.lines) for n, f in fakes.items()}

            def clear():
                for f in fakes.values():
                    f.lines.clear()

            def wheel_on(at):
                QGuiApplication.sendEvent(self.root, QWheelEvent(
                    at, self.root.mapToGlobal(at.toPoint()), QPoint(), QPoint(0, 120),
                    Qt.NoButton, Qt.NoModifier, Qt.NoScrollPhase, False))
                self.settle(0.3)

            def drag_on(at):
                gp = self.root.mapToGlobal(at.toPoint())
                QGuiApplication.sendEvent(self.root, QMouseEvent(
                    QMouseEvent.Type.MouseButtonPress, at, gp, Qt.LeftButton, Qt.LeftButton, Qt.NoModifier))
                for step in (1, 2, 3):
                    to = at + QPointF(10 * step, 4 * step)
                    QGuiApplication.sendEvent(self.root, QMouseEvent(
                        QMouseEvent.Type.MouseMove, to, self.root.mapToGlobal(to.toPoint()),
                        Qt.NoButton, Qt.LeftButton, Qt.NoModifier))
                QGuiApplication.sendEvent(self.root, QMouseEvent(
                    QMouseEvent.Type.MouseButtonRelease, at + QPointF(30, 12),
                    self.root.mapToGlobal((at + QPointF(30, 12)).toPoint()),
                    Qt.LeftButton, Qt.NoButton, Qt.NoModifier))
                self.settle(0.3)

            view_mouse = self.root.findChild(QObject, "viewMouse")
            self.assertIsNotNone(view_mouse)

            def pane_center(n):
                # the SidePane items are QML-side only (findChild cannot see them), so
                # aim by geometry: pane 1's mouse area sits at its cell's origin
                r = QMetaObject.invokeMethod(self.root, "paneRect", Q_RETURN_ARG("QVariant"),
                                             Q_ARG("QVariant", n))
                origin = view_mouse.mapToScene(QPointF(0, 0))
                return origin + QPointF(r.property("x").toNumber() + r.property("w").toNumber() / 2,
                                        r.property("y").toNumber() + r.property("h").toNumber() / 2)

            pane1_at = view_mouse.mapToScene(QPointF(view_mouse.width() / 2, view_mouse.height() / 2))
            pane2_at = pane_center(2)
            self.assertGreater(pane2_at.x(), pane1_at.x(), "pane 2 is right of pane 1 in the quad view")

            clear()
            wheel_on(pane2_at)
            got = lines()
            self.assertEqual(got[2], ["zoom 0.9\n"], "the wheel zooms the pane it is over")
            for n in (1, 3, 4):
                self.assertEqual(got[n], [], "pane %d must not zoom: the wheel was in pane 2" % n)

            clear()
            drag_on(pane2_at)
            got = lines()
            self.assertTrue(any(l.startswith("orbit ") for l in got[2]), got)
            for n in (1, 3, 4):
                self.assertEqual(got[n], [], "pane %d must not orbit: the drag was in pane 2" % n)

            clear()
            wheel_on(pane1_at)
            got = lines()
            self.assertEqual(got[1], ["zoom 0.9\n"], "the wheel zooms pane 1 from pane 1")
            for n in (2, 3, 4):
                self.assertEqual(got[n], [], "pane %d must not zoom: the wheel was in pane 1" % n)

            clear()
            drag_on(pane1_at)
            got = lines()
            self.assertTrue(any(l.startswith("orbit ") for l in got[1]), got)
            for n in (2, 3, 4):
                self.assertEqual(got[n], [], "pane %d must not orbit: the drag was in pane 1" % n)
        finally:
            self.host._layout, self.host._link, self.host.proc = saved[0], saved[1], saved[2]
            for p, proc in zip(self.host.panes, saved[3]):
                p.proc = proc
            self.host.panesChanged.emit()
            self.host.runTimer.start()
        self.assertEqual(self.qml_errors(), [])


if __name__ == "__main__":
    unittest.main()
