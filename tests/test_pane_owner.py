"""Viewport panes know who is in them: the session's name, whether it is still there, and a pane whose
owner has gone (run over) frees itself so AUTO folds the layout back down.

Owner lookups run against temp claude-runs/ and transcript folders. The host part builds a real Host
offscreen with its runs folder and view prefs pointed at temp files (the chest's own are never
touched) and fake pane contents (no viewer processes). GEPPETTO_SNAP=path saves the split view's
headers as a picture.
"""
import json
import os
import sys
import tempfile
import time
import unittest
from pathlib import Path

CABINET = Path(__file__).resolve().parents[1] / "cabinet"
sys.path.insert(0, str(CABINET))
os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

import pane_owner  # noqa: E402

SESSION = "11111111-2222-3333-4444-555555555555"


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as stream:
        stream.write(data if isinstance(data, str) else json.dumps(data))


def make_run(root, name, pid, seen):
    d = os.path.join(root, "research", name)
    write(os.path.join(d, ".live", "events.jsonl"), '{"state": "completed"}\n')
    os.utime(os.path.join(d, ".live", "events.jsonl"), (seen, seen))
    write(d + ".json", {"script": "/x/mug/build.txt", "pid": pid})
    return d


class Owner(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = self.tmp.name
        self.runs = os.path.join(self.root, "claude-runs")
        self.projects = os.path.join(self.root, "projects")
        pane_owner._paths.clear()
        pane_owner._titles.clear()

    def tearDown(self):
        self.tmp.cleanup()

    def transcript(self, seen, title="Red mug, short handle"):
        path = os.path.join(self.projects, "-home-poo-x", SESSION + ".jsonl")
        lines = [{"type": "user", "message": "hi"}, {"type": "custom-title", "customTitle": "old name"},
                 {"type": "custom-title", "customTitle": title}, {"type": "assistant", "message": "ok"}]
        write(path, "\n".join(json.dumps(x) for x in lines) + "\n")
        os.utime(path, (seen, seen))

    def test_a_run_names_its_session_and_title(self):
        now = time.time()
        d = make_run(self.root, "mug", 4242, now - 1000)
        write(os.path.join(self.runs, "4242.json"), {"session": SESSION, "script": "build.txt"})
        self.transcript(now - 20)
        o = pane_owner.owner(run=d, now=now, projects=self.projects, runs=self.runs)
        self.assertEqual(o["session"], SESSION)
        self.assertEqual(o["title"], "Red mug, short handle")
        self.assertEqual(o["presence"], "here")

    def test_quiet_then_gone(self):
        now = time.time()
        d = make_run(self.root, "mug", 4242, now - 5000)
        write(os.path.join(self.runs, "4242.json"), {"session": SESSION})
        self.transcript(now - 200)
        o = pane_owner.owner(run=d, now=now, projects=self.projects, runs=self.runs)
        self.assertEqual((o["presence"], o["quiet"]), ("idle", "3m"))
        self.transcript(now - 900)
        o = pane_owner.owner(run=d, now=now, projects=self.projects, runs=self.runs)
        self.assertEqual((o["presence"], o["quiet"]), ("left", "15m"))
        o = pane_owner.owner(run=d, now=now, live=True, projects=self.projects, runs=self.runs)
        self.assertEqual(o["presence"], "here")        # a live build is somebody working, however quiet

    def test_no_session_uses_the_run_journal(self):
        now = time.time()
        d = make_run(self.root, "codex-run", 99, now - 30)
        o = pane_owner.owner(run=d, now=now, projects=self.projects, runs=self.runs)
        self.assertEqual((o["session"], o["title"], o["presence"]), ("", "", "here"))


class HostPanes(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        from PySide6.QtGui import QGuiApplication
        import host as chest
        cls.chest = chest
        cls.app = QGuiApplication.instance() or QGuiApplication([])
        cls.tmp = tempfile.TemporaryDirectory()
        cls.saved = chest.RUNS, chest.VIEW_PREFS, chest.PANES_SAVED
        chest.PANES_SAVED = os.path.join(cls.tmp.name, "chest-panes.json")
        chest.RUNS = os.path.join(cls.tmp.name, "research")
        chest.VIEW_PREFS = os.path.join(cls.tmp.name, "chest-view.json")
        os.makedirs(chest.RUNS)
        cls.host = chest.Host()
        cls.host._preferences_path = "/nonexistent/geppetto-test/preferences.json"
        cls.host.claude.close()
        cls.projects = os.path.join(cls.tmp.name, "projects")
        cls.runs = os.path.join(cls.tmp.name, "claude-runs")
        cls.owner_was = pane_owner.owner

        def owner(session="", run="", since=0.0, live=False, now=None, **_):
            return cls.owner_was(session, run, since, live, now, projects=cls.projects, runs=cls.runs)
        pane_owner.owner = owner

    @classmethod
    def tearDownClass(cls):
        pane_owner.owner = cls.owner_was
        cls.chest.RUNS, cls.chest.VIEW_PREFS, cls.chest.PANES_SAVED = cls.saved
        cls.host.claude.close()
        cls.tmp.cleanup()

    def setUp(self):
        h = self.host
        for p in h.panes:
            p.shut()
        h.watching, h.waiting, h.auto_pending, h._parked = "", [], set(), {}
        h._auto, h._layout, h._solo = True, 2, 0
        pane_owner._paths.clear()

    def transcript(self, session, seen, title):
        path = os.path.join(self.projects, "-home-poo-x", session + ".jsonl")
        write(path, json.dumps({"type": "custom-title", "customTitle": title}) + "\n")
        os.utime(path, (seen, seen))

    def test_a_finished_run_whose_session_left_frees_its_pane_and_auto_folds(self):
        h, now = self.host, time.time()
        d = make_run(self.tmp.name, "mug", 5150, now - 2000)
        write(os.path.join(self.runs, "5150.json"), {"session": SESSION})
        self.transcript(SESSION, now - 30, "Red mug")
        pane = h.panes[0]
        pane.run, pane.since = d, now - 2000
        h.tidy_panes(now)
        self.assertEqual(pane.run, d)                       # still here: keeps its pane
        self.assertEqual(pane.owner["title"], "Red mug")
        self.transcript(SESSION, now - 1000, "Red mug")
        h.tidy_panes(now)
        self.assertEqual(pane.run, d)                       # quiet 16 min: maybe thinking, keeps its pane
        self.assertEqual(pane.owner["presence"], "left")
        self.transcript(SESSION, now - h.FREE_AFTER - 10, "Red mug")
        h.tidy_panes(now)
        self.assertEqual(pane.run, "")                      # gone and the run is over: freed
        self.assertEqual(pane.owner, {})
        h.auto_shrink()
        h._shrink_at -= h.SHRINK_AFTER + 1
        h.auto_shrink()
        self.assertEqual(h._layout, 1)

    def test_a_live_run_keeps_its_pane_however_quiet(self):
        h, now = self.host, time.time()
        d = make_run(self.tmp.name, "live", 5151, now - 2000)
        pane = h.panes[0]
        pane.run = d
        h.run_live = lambda r: r == d
        try:
            h.tidy_panes(now)
            self.assertEqual(pane.run, d)
            self.assertEqual(pane.owner["presence"], "here")
        finally:
            del h.run_live

    def test_a_finished_run_alone_does_not_hold_the_split(self):
        h = self.host
        h.panes[0].run = make_run(self.tmp.name, "done", 5152, time.time() - 30)    # owner still about
        h.tidy_panes()
        h.auto_shrink()
        h._shrink_at -= h.SHRINK_AFTER + 1
        h.auto_shrink()
        self.assertEqual(h._layout, 2)                      # its maker is still here: it stays on screen
        h.panes[0].run = make_run(self.tmp.name, "old", 5153, time.time() - 1000)
        h.tidy_panes()
        h.auto_shrink()
        h._shrink_at -= h.SHRINK_AFTER + 1
        h.auto_shrink()
        self.assertEqual(h._layout, 1)                      # gone quiet: AUTO folds it away

    def test_a_model_pane_goes_when_its_session_leaves(self):
        h, now = self.host, time.time()
        other = "99999999-2222-3333-4444-555555555555"
        model = os.path.join(self.tmp.name, "mug.effigy")
        write(model, "{}")
        pane = h.panes[0]
        pane.model, pane.owner_session, pane.since = model, other, now - 2000
        self.transcript(other, now - 2000, "Pinoch boots")
        h.tidy_panes(now)
        self.assertEqual(pane.model, "")
        pane.model, pane.owner_session, pane.since = model, "", now - 400      # nobody named: waits longer
        h.tidy_panes(now)
        self.assertEqual(pane.model, model)
        h.tidy_panes(now + h.MODEL_LEAVE)
        self.assertEqual(pane.model, "")

    def test_a_restart_puts_the_panes_back(self):
        h, now = self.host, time.time()
        d = make_run(self.tmp.name, "kept", 5154, now - 60)
        write(os.path.join(d, ".live", "state.json"), {"runId": "r1", "state": "completed"})
        shown = []
        h.panes[1].show = lambda run: (shown.append(run), setattr(h.panes[1], "run", run)) and True
        try:
            h._layout = 4
            with open(self.chest.PANES_SAVED, "w") as f:
                json.dump([{"n": 3, "run": d, "session": SESSION, "since": now - 60},
                           {"n": 4, "run": "/gone/run"}], f)
            h._panes_restored = False
            h.save_panes()                                  # the startup scan must not wipe the file
            h.restore_panes()
            self.assertEqual(shown, [d])
            self.assertEqual(h.panes[1].owner_session, SESSION)
            h.save_panes()
            self.assertEqual(json.load(open(self.chest.PANES_SAVED))[0]["run"], d)
        finally:
            del h.panes[1].show

    def test_auto_keeps_a_pane_somebody_is_in_and_a_hidden_pane_comes_back(self):
        """po: 2 panes, mine top-left, somebody's top-right; AUTO folded it to 1, back to 2 and the right one was gone"""
        h, now = self.host, time.time()
        d = make_run(self.tmp.name, "theirs", 5155, now - 120)
        write(os.path.join(d, ".live", "state.json"), {"runId": "r2", "state": "completed"})
        write(os.path.join(self.runs, "5155.json"), {"session": SESSION})
        self.transcript(SESSION, now - 10, "Survey boots")
        pane = h.panes[0]
        pane.show = lambda run: setattr(pane, "run", run) or True
        try:
            pane.run, pane.since = d, now - 120
            h._auto, h._layout = False, 2
            h.setViewLayout(0)                              # AUTO on
            h._shrink_at -= h.SHRINK_AFTER + 1
            h.scanRuns()
            self.assertEqual(h._layout, 2)                  # they are still working: it stays
            self.assertEqual(pane.run, d)
            h.setViewLayout(1)                              # folded by hand anyway
            self.assertEqual(pane.run, "")
            h.setViewLayout(2)
            self.assertEqual(pane.run, d)                   # and back it comes
            self.assertEqual(pane.owner_session, "")        # run panes name their owner from the run itself
        finally:
            del pane.show

    def test_one_pane_is_the_one_po_picks_and_nothing_moves(self):
        """po: switched to 1 and the other session's tool wheel landed in my pane"""
        h = self.host
        d = make_run(self.tmp.name, "his", 5156, time.time() - 5)
        pane = h.panes[0]
        pane.run = d
        h._auto, h._layout = False, 2
        h.setSolo(2)
        self.assertEqual((h._layout, h._solo, pane.run), (2, 2, d))    # his pane, alone, still his
        self.assertEqual(h.watching, "")                               # pane 1 didn't get his run
        h.setSolo(1)
        self.assertEqual((h._layout, h._solo, pane.run), (2, 1, d))    # hidden, still going
        h.setViewLayout(2)
        self.assertEqual(h._solo, 0)
        h.setSolo(3)
        self.assertEqual((h._layout, h._solo), (4, 3))

    def test_pane_one_is_open_to_runs_unless_it_is_my_scene(self):
        h = self.host
        h.setViewLayout(2)
        h.model, h._mine = "/x/mine.effigy", False
        h.proc = type("P", (), {"state": lambda self: 2})()
        try:
            self.assertFalse(h.pane_busy(1))                # a model a session showed: runs may land
            h._mine = True
            self.assertTrue(h.pane_busy(1))                 # MY SCENE: runs stay out
        finally:
            h.proc, h.model, h._mine = None, "", False

    def test_the_client_names_its_session(self):
        self.assertEqual(self.chest.pane_args(["2", "model", "a.effigy"]), "2 model " + os.path.abspath("a.effigy"))
        self.assertEqual(self.chest.pane_args(["3", "cmd", "frame"]), "3 cmd frame")


class Snap(unittest.TestCase):
    """the split view's headers, offscreen: GEPPETTO_SNAP=path saves them"""

    def test_headers(self):
        from PySide6.QtCore import QUrl, QObject
        from PySide6.QtQml import QQmlComponent, QQmlEngine
        from PySide6.QtGui import QGuiApplication
        QGuiApplication.instance() or QGuiApplication([])
        qml = """
import QtQuick
import QtQuick.Window
import QtQuick.Controls as C
Window {
    width: 720; height: 380; visible: true; color: "#0e1012"
    property color amber: "#f2a91c"; property color keyText: "#c9ccd0"; property string mono: "DejaVu Sans Mono"
    property int activePane: 2
    property bool blinkOn: false; property bool chatOpen: false
    property string buildNoteDir: ""; property string keyTip: ""
    QtObject { id: host; function setPace(dir, pace) {} }
    QtObject { id: claudePanel; property string who: "Codex" }
    component Tape: Text {}
    id: root
    %s
    Column { y: 6; spacing: 6; width: parent.width
        Item { width: parent.width; height: 84; PaneHeader { n: 1; who: "Claude"; what: "suit/boots2.txt"; state: "LIVE  REV 41"; owner: ({presence: "here", title: "Fix Survey player pants + mouth IN EFFIGY", quiet: ""}) } }
        Item { width: parent.width; height: 84; PaneHeader { n: 2; who: "Claude"; what: "mug/build.txt"; state: "DONE"; owner: ({presence: "idle", title: "Red mug, short handle", quiet: "3m"}) } }
        Item { width: parent.width; height: 84; PaneHeader { n: 3; who: "Codex"; what: "dagger.effigy"; owner: ({presence: "left", title: "", quiet: "6m"}) } }
        Item { width: parent.width; height: 84; PaneHeader { n: 4 } }
    }
}
"""
        src = open(CABINET / "cabinet.qml").read()
        start = src.index("    component PaneHeader: Rectangle {")
        end = src.index("\n    }\n", start) + 6
        dependencies = []
        for name in ('Chip', 'RunBar', 'CheckpointBar'):
            at = src.index('    component ' + name + ':')
            dependencies.append(src[at:src.index('\n    }\n', at) + 6])
        engine = QQmlEngine()
        comp = QQmlComponent(engine)
        comp.setData((qml % (src[start:end] + '\n' + '\n'.join(dependencies))).encode(), QUrl.fromLocalFile(str(CABINET / "snap.qml")))
        win = comp.create()
        self.assertIsNotNone(win, comp.errorString())
        app = QGuiApplication.instance()
        for _ in range(20):
            app.processEvents()
        texts = [o.property("text") for o in win.findChildren(QObject) if o.metaObject().className().startswith("QQuickText")]
        self.assertIn("mug/build.txt", win.findChild(QObject, "paneHeader2").property("fullText"))
        self.assertIn("dagger.effigy", win.findChild(QObject, "paneHeader3").property("fullText"))
        if os.environ.get("GEPPETTO_SNAP"):
            win.grabWindow().save(os.environ["GEPPETTO_SNAP"])


if __name__ == "__main__":
    unittest.main()
