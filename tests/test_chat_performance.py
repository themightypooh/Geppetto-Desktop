"""Windowless chat regressions; no providers, network or running-chest commands."""
import os
import sys
import threading
import time
import unittest
from pathlib import Path
from types import SimpleNamespace

os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'cabinet'))
from PySide6.QtCore import QObject, Property, QUrl, QMetaObject
from PySide6.QtGui import QGuiApplication
from PySide6.QtQml import QQmlComponent, QQmlEngine, QQmlExpression
from PySide6.QtQuick import QQuickWindow
from chat_log_model import ChatLogModel
from chat_phase import reported_phase

APP = QGuiApplication.instance() or QGuiApplication([])


def settle(ms=100):
    end = time.monotonic() + ms / 1000
    while time.monotonic() < end:
        APP.processEvents()
        time.sleep(.001)


class FakeHost(QObject):
    def __init__(self, model):
        super().__init__()
        self.model = model
        self.catalogue = [
            dict(id='zoopa/opus', label='Zoopa · Claude Opus · Max plan', kind='claude-cli', group='Claude'),
            dict(id='grok/4.7', label='Grok 4.7', kind='grok-cli', group='Grok'),
            dict(id='seat/zoopa', label='Zoopa · OpenCode · opencode/beam-free · free tier (quota applies)',
                 kind='roundtable-cli', group='Roundtable seats'),
        ]

    claudeMessages = Property(QObject, lambda self: self.model, constant=True)
    claudeStatus = Property(str, lambda self: 'working', constant=True)
    claudeAsk = Property(str, lambda self: '', constant=True)
    chatModels = Property('QVariantList', lambda self: self.catalogue, constant=True)
    claudeModel = Property(str, lambda self: 'zoopa/opus', constant=True)
    claudePictures = Property('QVariantList', lambda self: [], constant=True)
    catalogueBusy = Property(bool, lambda self: False, constant=True)


class ChatPerformanceTests(unittest.TestCase):
    def setUp(self):
        self.link = SimpleNamespace(messages=[], lock=threading.RLock())
        self.model = ChatLogModel(self.link)
        self.resets, self.changes = [], []
        self.model.modelReset.connect(lambda: self.resets.append(True))
        self.model.dataChanged.connect(lambda a, b, roles: self.changes.append((a.row(), b.row())))

    def tearDown(self):
        self.model._timer.stop()

    def test_stream_only_changes_live_row_not_history(self):
        self.link.messages = [dict(role='claude', text=str(i)) for i in range(199)]
        self.link.messages.append(dict(role='claude', text='', live=True))
        self.model.sync()
        for i in range(100):
            self.link.messages[-1]['text'] += 'token '
            self.model.sync()
        self.assertEqual(self.resets, [])
        self.assertEqual(self.changes, [(199, 199)] * 100)
        self.assertEqual(self.model.rowCount(), 200)

    def test_batch_has_fixed_deadline_and_preserves_final_text(self):
        self.link.messages = [dict(role='claude', text='')]
        updates = []
        self.model.updated.connect(lambda: updates.append(True))
        for i in range(1000):
            self.link.messages[0]['text'] = str(i)
            self.model.schedule()
        self.assertEqual(updates, [])
        settle()
        self.assertEqual(len(updates), 1)
        self.assertEqual(self.model.data(self.model.index(0), self.model.ROW)['text'], '999')

    def test_prune_duplicates_clear_and_replace(self):
        a, b, c = [dict(role='you', text='same') for _ in range(3)]
        self.link.messages = [a, b]
        self.model.sync()
        removed = []
        self.model.rowsRemoved.connect(lambda p, first, last: removed.append((first, last)))
        self.link.messages = [b, c]
        self.model.sync()
        self.assertEqual(removed, [(0, 0)])
        self.assertIs(self.model._sources[0], b)
        self.link.messages = []
        self.model.sync()
        self.assertEqual(self.model.rowCount(), 0)
        self.link.messages = [a]
        self.model.sync()
        self.link.messages = [c]
        self.model.sync()
        self.assertIs(self.model._sources[0], c)
        self.assertEqual(self.resets, [])

    def test_internal_tool_json_not_published_and_snapshot_is_detached(self):
        row = dict(role='tool', text='Read', raw='secret internal JSON')
        self.link.messages = [row]
        self.model.sync()
        data = self.model.data(self.model.index(0), self.model.ROW)
        row['text'] = 'Write'
        self.assertEqual(data['text'], 'Read')
        self.assertNotIn('raw', data)

    def test_explicit_phases_only_and_new_turn_clears(self):
        self.assertEqual(reported_phase([dict(role='you', text='Phase: Modeling')]), '')
        self.assertEqual(reported_phase([dict(role='claude', text='I might start modeling later.')]), '')
        self.link.messages = [dict(role='claude', text='Phase: Researching\nFinding references.')]
        self.model.sync()
        self.assertEqual(self.model.phase, 'researching')
        self.link.messages.append(dict(role='claude', text='Phase: Modeling\nResearch complete.'))
        self.model.sync()
        self.assertEqual(self.model.phase, 'modeling')
        self.link.messages.append(dict(role='you', text='hello'))
        self.model.sync()
        self.assertEqual(self.model.phase, '')

    def test_real_qml_retains_delegates_and_scroll_back(self):
        engine = QQmlEngine()
        host = FakeHost(self.model)
        engine.rootContext().setContextProperty('host', host)
        component = QQmlComponent(engine, QUrl.fromLocalFile(str(
            Path(__file__).resolve().parents[1] / 'cabinet' / 'ClaudePanel.qml')))
        card = component.create()
        self.assertIsNotNone(card, str(component.errors()))
        window = QQuickWindow()
        window.resize(560, 700)
        card.setParentItem(window.contentItem())
        window.show()  # QT_QPA_PLATFORM=offscreen: no desktop connection or focus.
        try:
            card.setProperty('open', True)
            self.link.messages = [dict(role='you', text='history ' + str(i)) for i in range(100)]
            self.link.messages.append(dict(role='claude', text='Phase: Researching\n', live=True))
            self.model.sync()
            settle()
            log = card.findChild(QObject, 'claudeLog')
            self.assertEqual(log.property('count'), 101)
            log.setProperty('follow', False)
            QQmlExpression(engine.rootContext(), log, 'positionViewAtBeginning()').evaluate()
            settle()
            scroll_y = log.property('contentY')
            expr = QQmlExpression(engine.rootContext(), log, 'itemAtIndex(0)')
            first, error = expr.evaluate()
            self.assertFalse(error)
            self.assertIsNotNone(first)
            for i in range(20):
                self.link.messages[-1]['text'] += 'streaming text\n'
                self.model.sync()
            settle()
            after, error = expr.evaluate()
            self.assertIs(first, after)
            self.assertAlmostEqual(log.property('contentY'), scroll_y, delta=1)
            log.setProperty('follow', True)
            self.link.messages[-1]['text'] += 'Phase: Modeling\nResearch complete.'
            self.model.sync()
            settle()
            self.assertTrue(log.property('atYEnd'))
            self.assertEqual(card.property('phase'), 'modeling')
            self.assertEqual(self.resets, [])
            if os.environ.get('CHAT_SNAP'):
                self.assertTrue(window.grabWindow().save(os.environ['CHAT_SNAP']))
            QMetaObject.invokeMethod(card, 'showPopup')
            settle()
            self.assertTrue(card.findChild(QObject, 'claudeModelPickPopup').property('visible'))
            if os.environ.get('CHAT_SNAP'):
                self.assertTrue(window.grabWindow().save(os.environ['CHAT_SNAP'].replace('.png', '-picker.png')))
        finally:
            card.setProperty('open', False)
            card.deleteLater()
            window.close()
            settle(10)


if __name__ == '__main__':
    unittest.main()
