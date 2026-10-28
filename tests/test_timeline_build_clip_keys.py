"""TIMELINE drawer BUILD / CLIP / KEYS, exercised with Qt's offscreen platform only.

No live chest: the host is the test fixture's Host, runs are fake dirs on disk
with kept revisions, and routing is captured by stubbing live_command /
tryCancel (plain Python methods the QML slots call through).
"""
import os
os.environ['QT_QPA_PLATFORM'] = 'offscreen'
os.environ['QSG_RHI_BACKEND'] = 'software'
import sys, time, json, tempfile, unittest
from pathlib import Path
from PySide6.QtCore import QObject, QUrl, QMetaObject, Q_ARG, Qt
from PySide6.QtQml import QQmlComponent
from PySide6.QtTest import QTest

CABINET = Path(__file__).resolve().parents[1] / 'cabinet'
sys.path.insert(0, str(CABINET))

PROBE = b'''import QtQml
QtObject {
    property string result: ""
    property var searchRoot: null
    property string targetName: ""
    property string fnName: ""
    property var argValue: 0
    property bool hasArg: false
    function find(item, name) {
        if (!item) return null;
        if (item.objectName === name) return item;
        const children = item.children || [];
        for (let i = 0; i < children.length; i++) {
            const found = find(children[i], name);
            if (found) return found;
        }
        return null;
    }
    function act() {
        const item = find(searchRoot, targetName);
        if (!item) { result = "MISSING:" + targetName; return; }
        if (hasArg) item[fnName](argValue); else item[fnName]();
        result = "CALLED:" + fnName;
    }
    function actValue() {
        const item = find(searchRoot, targetName);
        if (!item) { result = "MISSING:" + targetName; return; }
        result = JSON.stringify(hasArg ? item[fnName](argValue) : item[fnName]());
    }
    function clickIt() {
        const item = find(searchRoot, targetName);
        if (!item) { result = "MISSING:" + targetName; return; }
        item.clicked();
        result = "CLICKED:" + targetName;
    }
}
'''


def make_run(root, revs):
    """A fake run dir whose kept revisions are exactly revs (sparse allowed)."""
    root = Path(root)
    (root / '.live' / 'revisions').mkdir(parents=True, exist_ok=True)
    lines = []
    for i, rev in enumerate(revs, 1):
        (root / '.live' / 'revisions' / ('%08d.effigy' % rev)).write_text('fake')
        lines.append(json.dumps({
            'schemaVersion': 1, 'ev': 'ai-live', 'runId': 'test-run',
            'sequence': i, 'state': 'revision',
            'snapshot': {'revision': rev, 'path': '.live/revisions/%08d.effigy' % rev},
            'operation': 'line %d: add Box' % rev}) + '\n')
    (root / '.live' / 'events.jsonl').write_text(''.join(lines))
    return str(root)


class TimelineBuildTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import test_cabinet_qml as fixture
        cls.fixture = fixture
        fixture.CabinetQmlTests.setUpClass()
        cls.f = fixture.CabinetQmlTests()
        cls.host = cls.f.host
        cls.root = cls.f.root
        cls.host.runTimer.stop()

    @classmethod
    def tearDownClass(cls):
        try:
            cls.host.runTimer.start()
        except Exception:
            pass

    def settle(self, seconds=.3):
        self.f.settle(seconds)

    def find(self, name):
        return self.root.findChild(QObject, name)

    def build(self):
        return self.find('buildPage')

    def floor(self):
        return self.find('timelineFloor')

    def probe(self):
        # The drawer lives under the window Overlay, not contentItem, so
        # search from buildPage itself (found via QObject, which works).
        comp = QQmlComponent(self.f.engine)
        comp.setData(PROBE, QUrl())
        self.assertFalse(comp.isError(), str(comp.errors()))
        p = comp.create()
        self.assertIsNotNone(p)
        p.setProperty('searchRoot', self.build())
        # Keep the component (and probe) alive: a probe created from a
        # short-lived component is deleted as soon as the helper returns.
        self._probe_comp = comp
        self._probe = p
        return p

    def probe_call(self, item, fn, arg=None):
        p = self.probe()
        p.setProperty('targetName', item)
        p.setProperty('fnName', fn)
        p.setProperty('hasArg', arg is not None)
        if arg is not None:
            p.setProperty('argValue', arg)
        ok = QMetaObject.invokeMethod(p, 'act')
        self.assertTrue(ok, 'probe act failed for %s.%s' % (item, fn))
        result = p.property('result')
        self.assertTrue(result.startswith('CALLED:'), 'probe %s.%s -> %s' % (item, fn, result))
        return result

    def probe_click(self, name):
        p = self.probe()
        p.setProperty('targetName', name)
        ok = QMetaObject.invokeMethod(p, 'clickIt')
        self.assertTrue(ok, 'probe clickIt failed for %s' % name)
        result = p.property('result')
        self.assertTrue(result.startswith('CLICKED:'), 'probe click %s -> %s' % (name, result))
        return result

    def probe_value(self, item, fn, arg):
        import json as _json
        p = self.probe()
        p.setProperty('targetName', item)
        p.setProperty('fnName', fn)
        p.setProperty('argValue', arg)
        p.setProperty('hasArg', True)
        ok = QMetaObject.invokeMethod(p, 'actValue')
        self.assertTrue(ok, 'probe actValue failed for %s.%s' % (item, fn))
        result = p.property('result')
        self.assertFalse(result.startswith('MISSING:'), 'probe %s.%s -> %s' % (item, fn, result))
        return _json.loads(result)

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir1 = make_run(Path(self.tmp.name) / 'run-one', [1, 3, 7])
        self.dir2 = make_run(Path(self.tmp.name) / 'run-two', [2, 5])
        self.dir_empty = make_run(Path(self.tmp.name) / 'run-empty', [])
        self.dir_one = make_run(Path(self.tmp.name) / 'run-one-step', [4])
        self.saved_runs = self.host._runs
        self.saved_watching = self.host.watching
        self.saved_layout = self.host._layout
        self.saved_replay = self.host._replay
        self.saved_live = self.host.live_command
        self.saved_try = self.host.tryCancel
        self.saved_scan = self.host.scanRuns
        self.saved_jobs = dict(self.host.jobs)
        self.host.scanRuns = lambda: None
        self.calls = []
        self.stop_calls = []
        self.host.live_command = lambda *a, **kw: self.calls.append((a, kw))
        self.host.tryCancel = lambda d: self.stop_calls.append(d)
        self.host._runs = [
            {'dir': self.dir1, 'status': 'running', 'paused': False, 'bench': False,
             'latestRevision': 7, 'openState': 'open', 'pace': 2.0, 'who': 'test', 'pane': 1, 'watch': {}},
            {'dir': self.dir2, 'status': 'running', 'paused': True, 'bench': False,
             'latestRevision': 5, 'openState': 'open', 'pace': 4.0, 'who': 'test', 'pane': 2, 'watch': {}},
            {'dir': self.dir_empty, 'status': 'ended', 'paused': False, 'bench': False,
             'latestRevision': 0, 'openState': '', 'pace': 0, 'who': 'test', 'pane': 3, 'watch': {}},
        ]
        self.host.watching = self.dir1
        for n, pane in enumerate(self.host.panes, 2):
            pane.run = self.dir2 if n == 2 else ''
            pane.changed.emit()
        self.host.panesChanged.emit()
        self.host.runsChanged.emit()
        self.settle(.5)
        self.floor().setProperty('page', 0)
        self.settle(.6)
        self.probe_call('buildPage', 'selectPane', 1)
        self.settle(.3)

    def tearDown(self):
        try:
            self.host._replay = {}
            self.host.replayChanged.emit()
        except Exception:
            pass
        for pane in self.host.panes:
            try:
                pane._replay = {}
            except Exception:
                pass
        self.host._runs = self.saved_runs
        self.host.watching = self.saved_watching
        self.host._layout = self.saved_layout
        self.host.live_command = self.saved_live
        self.host.tryCancel = self.saved_try
        self.host.scanRuns = self.saved_scan
        self.host.jobs = self.saved_jobs
        self.host.panesChanged.emit()
        self.host.runsChanged.emit()
        self.tmp.cleanup()
        self.settle(.1)

    def test_switch_has_three_positions_and_clip_label_is_not_under_the_knob(self):
        floor = self.floor()
        switch = self.find('pageSwitch')
        self.assertIsNotNone(switch)
        self.assertEqual(switch.property('width'), 60)
        self.assertEqual(switch.property('x'), 4)
        knob = self.find('pageKnob')
        self.assertIsNotNone(knob)
        self.assertEqual(knob.property('width'), 16)

        def click_at(yfrac):
            from PySide6.QtCore import QPointF
            pt = switch.mapToScene(QPointF(switch.property('width') / 2,
                                           switch.property('height') * yfrac)).toPoint()
            QTest.mouseClick(self.root, Qt.LeftButton, Qt.NoModifier, pt)
            self.settle(.6)

        click_at(0.15)
        self.assertEqual(floor.property('page'), 0)
        self.assertTrue(self.build().property('visible'))
        y0 = knob.property('y')
        click_at(0.5)
        self.assertEqual(floor.property('page'), 1)
        self.assertTrue(self.find('timePage').property('visible'))
        self.assertFalse(self.build().property('visible'))
        y1 = knob.property('y')
        self.assertGreater(y1, y0)
        click_at(0.85)
        self.assertEqual(floor.property('page'), 2)
        self.assertTrue(self.find('keyPage').property('visible'))
        self.assertFalse(self.find('timePage').property('visible'))
        y2 = knob.property('y')
        self.assertGreater(y2, y1)
        # Every switch label sits left of the knob track, none under the knob.
        labels = {}

        def walk(item):
            for ch in item.childItems():
                try:
                    t = ch.property('text')
                except Exception:
                    t = None
                if t in ('BUILD', 'CLIP', 'KEYS'):
                    labels[t] = (ch.mapToItem(switch, 0, 0).x(), ch.property('width'))
                walk(ch)
        walk(switch)
        self.assertEqual(set(labels), {'BUILD', 'CLIP', 'KEYS'})
        for name, (x, w) in labels.items():
            self.assertLessEqual(x + w, knob.property('x') + 1, name)
        self.assertEqual(self.f.qml_errors(), [])

    def test_build_page_fits_the_168px_floor(self):
        from PySide6.QtCore import QPointF
        b = self.build()
        floorW = self.floor().property('width')
        floorH = self.floor().property('height')
        self.assertEqual(b.property('width'), floorW)
        # The floor itself scales with the window (148 in the test window,
        # 168 at production size); the page must fill it, and its children
        # must fit the 168px design floor.
        self.assertEqual(b.property('height'), floorH)

        def walk(item, bad):
            for ch in item.childItems():
                if ch.property('visible') is False:
                    continue
                try:
                    pt = ch.mapToItem(b, QPointF(0, 0))
                    x, y, w, h = pt.x(), pt.y(), ch.property('width'), ch.property('height')
                except Exception:
                    walk(ch, bad)
                    continue
                if w is not None and x + w > floorW + 0.1:
                    bad.append((ch.objectName(), x, w))
                if h is not None and y + h > 168.1:
                    bad.append((ch.objectName(), y, h))
                walk(ch, bad)
        bad = []
        walk(b, bad)
        self.assertEqual(bad, [], 'children spilling out of the drawer floor')
        pace = self.find('buildPace')
        self.assertIsNotNone(pace)
        self.assertGreater(pace.property('width'), 100)
        self.assertEqual(self.f.qml_errors(), [])

    def test_pane_routing_pause_resume_step_stop_pace(self):
        b = self.build()
        self.assertEqual(b.property('buildDir'), self.dir1)
        # Pane 1 is running, not paused: PAUSE routes to pane 1 only.
        self.probe_call('buildPage', 'doPauseResume')
        self.assertEqual([(a[0], a[1]) for a, kw in self.calls], [(self.dir1, 'pause')])
        # Make pane 1 held, then RESUME.
        self.host._runs[0]['paused'] = True
        self.host.runsChanged.emit()
        self.settle(.3)
        self.calls.clear()
        self.probe_call('buildPage', 'doPauseResume')
        self.assertEqual([(a[0], a[1]) for a, kw in self.calls], [(self.dir1, 'resume')])
        # STEP+ only while held, exactly one step command.
        self.calls.clear()
        self.probe_call('buildPage', 'doStep')
        self.assertEqual([(a[0], a[1]) for a, kw in self.calls], [(self.dir1, 'step')])
        # STOP routes through stopResearch for this run only (captured tryCancel).
        self.host.jobs[self.dir1] = True
        self.calls.clear()
        self.stop_calls.clear()
        self.probe_call('buildPage', 'doStop')
        self.assertEqual(self.stop_calls, [self.dir1])
        self.assertEqual(self.calls, [])
        # Pane 2 is held: select it by clicking its key, then pace it.
        self.assertEqual(self.probe_click('buildPaneKey2'), 'CLICKED:buildPaneKey2')
        self.settle(.3)
        self.assertEqual(b.property('buildPane'), 2)
        self.assertEqual(b.property('buildDir'), self.dir2)
        pace = self.find('buildPace')
        pace.setProperty('shownPace', 3.5)
        pace.setProperty('pendingDir', self.dir2)
        self.probe_call('buildPace', 'submit')
        self.assertIn(((self.dir2, 'pace'), {'seconds': 3.5}), [(a[:2], kw) for a, kw in self.calls])
        self.assertEqual(self.f.qml_errors(), [])

    def test_pace_never_retargets_a_new_run(self):
        self.probe_call('buildPage', 'selectPane', 2)
        self.settle(.3)
        pace = self.find('buildPace')
        pace.setProperty('shownPace', 3.5)
        pace.setProperty('pendingDir', self.dir2)
        # Selecting pane 1 clears the pending edit for pane 2.
        self.probe_call('buildPage', 'selectPane', 1)
        self.settle(.3)
        self.assertEqual(pace.property('pendingDir'), '')
        self.calls.clear()
        self.probe_call('buildPace', 'submit')
        self.assertEqual([a for a, kw in self.calls if a[1] == 'pace'], [])
        self.assertEqual(self.f.qml_errors(), [])

    def test_changed_run_aborts_and_empty_pane_never_falls_back(self):
        b = self.build()
        # Empty pane 3: every action disabled, nothing sent anywhere.
        self.probe_call('buildPage', 'selectPane', 3)
        self.settle(.3)
        self.assertEqual(b.property('buildDir'), '')
        for name in ('buildPauseKey', 'buildStepKey', 'buildStopKey', 'buildReplayKey'):
            self.assertFalse(self.find(name).property('enabled'), name)
        self.calls.clear()
        self.stop_calls.clear()
        for fn in ('doPauseResume', 'doStep', 'doStop'):
            self.probe_call('buildPage', fn)
        self.assertEqual(self.calls, [])
        self.assertEqual(self.stop_calls, [])
        # Pane 2's run changes under a stale selection: the next command
        # aborts instead of retargeting to the new run.
        self.probe_call('buildPage', 'selectPane', 2)
        self.settle(.3)
        self.host.panes[0].run = self.dir_one
        self.host.panes[0].changed.emit()
        self.settle(.3)
        self.calls.clear()
        self.probe_call('buildPage', 'doPauseResume')
        self.assertEqual(self.calls, [])
        self.assertEqual(b.property('buildDir'), self.dir_one)
        self.assertEqual(self.f.qml_errors(), [])

    def test_step_back_is_disabled_and_non_mutating(self):
        key = self.find('buildStepBackKey')
        self.assertIsNotNone(key)
        self.assertFalse(key.property('enabled'))
        self.assertIn('does not rewind', key.property('tip'))
        self.assertEqual(key.property('opacity'), 0.45)
        rewind_calls = []
        saved_rewind = self.host.rewind_run
        self.host.rewind_run = lambda r: rewind_calls.append(r) or False
        try:
            from PySide6.QtCore import QPointF
            pt = key.mapToScene(QPointF(key.property('width') / 2, key.property('height') / 2)).toPoint()
            QTest.mouseClick(self.root, Qt.LeftButton, Qt.NoModifier, pt)
            self.settle(.3)
        finally:
            self.host.rewind_run = saved_rewind
        self.assertEqual(rewind_calls, [])
        self.assertEqual(self.calls, [])
        self.assertEqual(self.host._replay, {})
        self.assertEqual(self.f.qml_errors(), [])

    def test_ruler_endpoints_sparse_steps_and_seek_does_not_resume(self):
        b = self.build()
        steps = b.property('buildSteps')
        steps = steps.toVariant() if hasattr(steps, 'toVariant') else steps
        self.assertEqual([s['rev'] for s in steps], [1, 3, 7])
        self.assertEqual(b.property('buildAt'), 2)
        # Endpoints are exact, and the flag sits on the real revision label.
        self.assertEqual(self.probe_value('buildPage', 'stepX', 0), 0)
        floorW = self.floor().property('width')
        self.assertEqual(self.probe_value('buildPage', 'stepX', 2), floorW - 4)
        self.assertEqual(self.probe_value('buildPage', 'stepAt', 2), 0)
        self.assertEqual(self.probe_value('buildPage', 'stepAt', floorW - 2), 2)
        # Seek to the first saved step: replay opens paused, nothing resumes.
        self.calls.clear()
        self.probe_call('buildPage', 'seekIndex', 0)
        self.settle(.3)
        self.assertTrue(self.host._replay)
        self.assertEqual(self.host._replay.get('at'), 0)
        self.assertFalse(self.host._replay.get('playing'))
        self.assertEqual(self.calls, [])
        self.assertEqual(b.property('buildAt'), 0)
        # Seeking while replaying stays paused and follows the replay cursor.
        self.probe_call('buildPage', 'seekIndex', 2)
        self.settle(.3)
        self.assertEqual(self.host._replay.get('at'), 2)
        self.assertFalse(self.host._replay.get('playing'))
        self.assertEqual(self.calls, [])
        # Keyboard: Home, End, arrows, all without resuming.
        rule = self.find('buildRule')
        rule.forceActiveFocus()
        QTest.keyClick(self.root, Qt.Key_Home)
        self.settle(.2)
        self.assertEqual(self.host._replay.get('at'), 0)
        QTest.keyClick(self.root, Qt.Key_End)
        self.settle(.2)
        self.assertEqual(self.host._replay.get('at'), 2)
        QTest.keyClick(self.root, Qt.Key_Left)
        self.settle(.2)
        self.assertEqual(self.host._replay.get('at'), 1)
        QTest.keyClick(self.root, Qt.Key_Right)
        self.settle(.2)
        self.assertEqual(self.host._replay.get('at'), 2)
        self.assertFalse(self.host._replay.get('playing'))
        self.assertEqual(self.calls, [])
        self.assertEqual(self.f.qml_errors(), [])

    def test_no_steps_and_one_step(self):
        b = self.build()
        self.host.panes[1].run = self.dir_empty
        self.probe_call('buildPage', 'selectPane', 3)
        self.settle(.3)
        steps = b.property('buildSteps')
        steps = steps.toVariant() if hasattr(steps, 'toVariant') else steps
        self.assertEqual(list(steps), [])
        self.assertFalse(self.find('buildReplayKey').property('enabled'))
        self.assertFalse(self.find('buildScrub').property('enabled'))
        self.host.panes[0].run = self.dir_one
        self.probe_call('buildPage', 'selectPane', 2)
        self.settle(.3)
        steps = b.property('buildSteps')
        steps = steps.toVariant() if hasattr(steps, 'toVariant') else steps
        self.assertEqual([s['rev'] for s in steps], [4])
        self.assertEqual(b.property('buildAt'), 0)
        self.assertEqual(self.f.qml_errors(), [])

    def test_replay_live_returns_to_newest_and_new_step_repaints(self):
        b = self.build()
        self.probe_call('buildPage', 'seekIndex', 0)
        self.settle(.3)
        self.assertTrue(self.host._replay)
        self.probe_call('buildPage', 'doReplayLive')
        self.settle(.3)
        self.assertEqual(self.host._replay, {})
        self.assertFalse(b.property('replaying'))
        self.assertEqual(b.property('buildAt'), 2)
        # A new kept revision arrives: the ruler repaints to it.
        make_run(Path(self.tmp.name) / 'run-one', [1, 3, 7, 9])
        self.host.runsChanged.emit()
        self.settle(.3)
        steps = b.property('buildSteps')
        steps = steps.toVariant() if hasattr(steps, 'toVariant') else steps
        self.assertEqual([s['rev'] for s in steps], [1, 3, 7, 9])
        self.assertEqual(b.property('buildAt'), 3)
        self.assertEqual(self.f.qml_errors(), [])

    def test_clip_verbatim_and_keys_still_work(self):
        floor = self.floor()
        floor.setProperty('page', 1)
        self.settle(.6)
        self.assertTrue(self.find('timePage').property('visible'))
        for name in ('playKey', 'loopKey', 'viewKeys', 'rule', 'transport'):
            self.assertIsNotNone(self.find(name), name)
        self.assertEqual(self.find('viewKeys').parent().objectName(), 'timePage')
        floor.setProperty('page', 2)
        self.settle(.6)
        self.assertTrue(self.find('keyPage').property('visible'))
        self.assertFalse(self.find('timePage').property('visible'))
        self.assertFalse(self.build().property('visible'))
        for name in ('keyInsert', 'keyDelete', 'keyUndo', 'keyRedo'):
            self.assertIsNotNone(self.find(name), name)
        self.assertEqual(self.f.qml_errors(), [])

    def test_layouts_and_screenshots(self):
        out = Path(os.environ.get('TIMELINE_TEST_ARTIFACTS', tempfile.mkdtemp(prefix='timeline-qa-')))
        out.mkdir(parents=True, exist_ok=True)
        timeline = self.find('timeline')
        timeline.setProperty('open', 1.0)
        self.settle(.8)
        for layout in (1, 2, 4):
            self.host._layout = layout
            self.host.panesChanged.emit()
            self.settle(.5)
            self.assertEqual(self.build().property('width'), self.floor().property('width'))
            self.root.grabWindow().save(str(out / ('timeline-layout-%d.png' % layout)))
        print('timeline screenshots:', out)
        self.assertEqual(self.f.qml_errors(), [])


if __name__ == '__main__':
    unittest.main()
