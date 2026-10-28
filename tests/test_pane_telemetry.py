"""Truthful identities and responsive live pane telemetry; no shared chest or viewer."""
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
CABINET = Path(__file__).resolve().parents[1] / 'cabinet'
sys.path.insert(0, str(CABINET))
import panes

class Identity(unittest.TestCase):
    def test_filename_is_never_an_identity_and_metadata_refreshes(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = str(Path(tmp) / 'script-angler')
            path = Path(root + '.json')
            for meta, expected in [
                ({'script': '/models/angler-lure-new.txt'}, ''),
                ({'script': '/models/angler-lure-new.txt', 'modeller': 'angler-lure-new'}, ''),
                ({'modeller': 'Codex', 'identitySource': 'launcher'}, 'Codex'),
                ({'modeller': 'Claude', 'identitySource': 'launcher'}, 'Claude'),
                ({}, '')]:
                path.write_text(json.dumps(meta))
                self.assertEqual(panes.modeller(root), expected)

    def test_codex_beats_inherited_claude_session_and_script_arguments(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = str(Path(tmp) / 'run')
            Path(root + '.json').write_text(json.dumps({'pid': 123}))
            def proc(pid, what):
                return {'environ': b'CODEX_THREAD_ID=abc\0CLAUDE_CODE_SESSION_ID=def\0',
                        'cmdline': b'dotnet\0claude-fish.txt\0', 'status': b'PPid:\t1\n'}.get(what, b'')
            with patch.object(panes, '_proc', proc):
                self.assertEqual(panes.modeller(root), 'Codex')
            with patch.object(panes, '_proc', lambda pid, what: b'dotnet\0/x/claude-fish.txt\0' if what == 'cmdline' else b''):
                self.assertEqual(panes.modeller(root), '')

class Header(unittest.TestCase):
    def test_live_updates_and_zoom_layout(self):
        from PySide6.QtQuick import QQuickWindow
        from PySide6.QtCore import QUrl, QObject
        from PySide6.QtGui import QGuiApplication
        from PySide6.QtQml import QQmlComponent, QQmlEngine
        self.app = QGuiApplication.instance() or QGuiApplication([])
        src = (CABINET / 'cabinet.qml').read_text()
        start = src.index('    component PaneHeader: Rectangle {')
        end = src.index('\n    }\n', start) + 6
        parts = []
        for name in ('Chip', 'RunBar', 'CheckpointBar'):
            at = src.index('    component ' + name + ':')
            parts.append(src[at:src.index('\n    }\n', at) + 6])
        engine = QQmlEngine()
        component = QQmlComponent(engine)
        qml = '''import QtQuick
import QtQuick.Window
import QtQuick.Controls as C
Window {
 id: root; width: 900; height: 350; visible: true; color: "#101214"
 property color amber: "#f2a91c"; property color keyText: "#c9ccd0"
 property string mono: "DejaVu Sans Mono"; property int activePane: 1
 property bool blinkOn: false; property bool chatOpen: false
 property string buildNoteDir: ""; property string keyTip: ""
 QtObject { id: host; function setPace(dir, pace) {} }
 QtObject { id: claudePanel; property string who: "Codex" }
 component Tape: Text {}
 QtObject { id: paneMenu; property bool open: false; property int n: 1; function openFor(n, h) {} }
 %s
 Item { id: pane; objectName: "testPane"; width: root.width; height: root.height
 PaneHeader { objectName: "header"; n: 1; who: "Codex · gpt-6.1-sol";
 what: "Angler lure — an extremely long model name to check wrapping and elision at every zoom";
 run: ({dir: "/owned/pane-1", pace: 4, status: "running", openState: "open", latestRevision: 41, lastEvent: Date.now()/1000-12,
 telemetry: {label: "Thinking", build: "Waiting for steps", buildSeen: Date.now()/1000-12,
 agent: {seen: Date.now()/1000, known: true}, detail: "Agent: Thinking   ·   Build: Waiting for steps"},
 watch: {count: 18, line: 18, total: 92, part: "Dorsal fin", say: "Refine the silhouette"}}) }
 }
}''' % (src[start:end] + '\n' + '\n'.join(parts))
        component.setData(qml.encode(), QUrl.fromLocalFile(str(CABINET / 'telemetry-test.qml')))
        win = component.create()
        self.assertIsNotNone(win, component.errorString())
        header = win.findChild(QObject, 'header')
        pane = win.findChild(QObject, 'testPane')
        texts = [win.findChild(QObject, n) for n in ('paneIdentity', 'paneSubject', 'paneStatus', 'paneProgress', 'paneTelemetry')]
        fonts = [(item, item.property('font').pixelSize()) for item in header.findChildren(QObject) if item.property('font') is not None]
        for width in (300, 900):
            win.setProperty('width', width)
            for zoom in (0.75, 1.0, 2.0):
                for item, size in fonts:
                    font = item.property('font'); font.setPixelSize(max(1, round(size * zoom))); item.setProperty('font', font)
                from PySide6.QtTest import QTest
                QTest.qWait(40)
                for _ in range(15): self.app.processEvents()
                self.assertIn('WAITING FOR STEPS', header.property('detailText'))
                self.assertEqual(header.property('statusLabel'), 'Thinking')
                self.assertIn('Agent: Thinking', header.property('progressText'))
                self.assertIn('line 18/92', header.property('detailText'))
                self.assertLessEqual(header.property('height'), pane.property('height'))
                for item in texts:
                    self.assertLessEqual(item.property('x') + item.property('width'), pane.property('width'))
                    self.assertLessEqual(item.property('y') + item.property('height'), header.property('height'))
                controls = win.findChild(QObject, 'p1RunBar')
                self.assertIsNotNone(controls)
                self.assertIs(controls.parent().parent(), header)
                self.assertLessEqual(controls.property('width'), header.property('width'))
                for child in controls.findChildren(QObject):
                    if child.objectName().startswith('runBar'):
                        self.assertLessEqual(child.property('x') + child.property('width'), controls.property('width'))
                self.assertNotIn('component ModellingNow:', src)
                self.assertNotIn('id: spRunBar', src)
                artifact = Path('/tmp/geppetto-telemetry')
                artifact.mkdir(exist_ok=True)
                self.assertTrue(win.grabWindow().save(str(artifact / f'header-{width}-{zoom}.png')))
        # Replace the data just as a new host scan does; no viewer frame is needed.
        header.setProperty('run', {'status': 'running', 'openState': 'held', 'latestRevision': 42,
                                  'watch': {'line': 19, 'total': 92, 'error': 'Invalid parameter'}})
        self.app.processEvents()
        self.assertIn('ERROR', header.property('detailText'))
        self.assertIn('rev 42', header.property('detailText'))
        header.setProperty('run', {'status': 'ended', 'openState': 'open'})
        self.app.processEvents()
        self.assertTrue(header.property('detailText').startswith('ENDED'))
        header.setProperty('run', {'status': 'running', 'phase': 'pace-changed', 'telemetry': {
            'label': 'Rendering', 'build': 'Rendering', 'detail': 'Agent: Running a command   ·   Build: Rendering'}})
        self.app.processEvents()
        self.assertEqual(header.property('statusLabel'), 'Rendering')
        self.assertIn('Running a command', header.property('progressText'))
        self.assertNotIn('PACE CHANGED', header.property('fullText'))
        header.setProperty('run', None)
        header.setProperty('what', '/home/poo/games/projects/deep_sentance/tools/out/suit/live/pants.effigy')
        self.app.processEvents()
        self.assertEqual(header.property('statusLabel'), 'Saved model')
        self.assertIn('VIEWER', header.property('identityText'))
        self.assertNotIn('modeller', header.property('fullText').lower())
        self.assertIn('pants.effigy', header.property('fullText'))
        for width in (300, 900):
            win.setProperty('width', width)
            for zoom in (0.75, 1.0, 2.0):
                for item, size in fonts:
                    font = item.property('font'); font.setPixelSize(max(1, round(size * zoom))); item.setProperty('font', font)
                QTest.qWait(40)
                self.assertLessEqual(header.property('height'), pane.property('height'))
                self.assertTrue(win.grabWindow().save(str(artifact / f'saved-{width}-{zoom}.png')))
        win.close()

if __name__ == '__main__': unittest.main()
