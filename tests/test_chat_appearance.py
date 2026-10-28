"""Exercise the real chat QML offscreen; no chest, providers or conversations.

CHAT_APPEARANCE_SNAP_DIR saves the checked layouts as PNGs.
CHAT_APPEARANCE_QML optionally points at a staged candidate before installation.
"""
import os
import sys
import threading
import time
import unittest
from pathlib import Path
from types import SimpleNamespace

os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
os.environ.setdefault('QT_QUICK_BACKEND', 'software')
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'cabinet'))
from PySide6.QtCore import QObject, Property, Signal, Slot, QUrl, QMetaObject, QPoint, Qt, qInstallMessageHandler
from PySide6.QtGui import QGuiApplication, QColor
from PySide6.QtQml import QQmlEngine, QQmlComponent
from PySide6.QtQuick import QQuickWindow, QQuickItem
from PySide6.QtTest import QTest
from chat_log_model import ChatLogModel

APP = QGuiApplication.instance() or QGuiApplication([])


def settle(ms=160):
    until = time.monotonic() + ms / 1000
    while time.monotonic() < until:
        APP.processEvents()
        time.sleep(.002)


class AppearanceHost(QObject):
    changed = Signal()
    museTrackerChanged = Signal()

    def __init__(self):
        super().__init__()
        self.link = SimpleNamespace(messages=[], lock=threading.RLock())
        self.log = ChatLogModel(self.link)
        self.mode, self.state, self.prompt, self.ask = 'build', 'idle', False, ''
        self.sent, self.answers = [], []
        self.selected = 'muse'
        self.catalogue = [
            dict(id='muse', label='Muse · Muse (this chat) · file bridge', family='muse', kind='roundtable-cli', group='Muse'),
            dict(id='claude', label='Claude · Opus 4.6 · personal account', family='claude', kind='claude-cli', group='Claude'),
            dict(id='long', label='A very long provider name · an unusually long model and workspace identifier · free tier (quota applies)', family='grok', kind='grok-cli', group='Other'),
        ]

    claudeMessages = Property(QObject, lambda self: self.log, constant=True)
    claudeStatus = Property(str, lambda self: self.state, notify=changed)
    claudeMode = Property(str, lambda self: self.mode, notify=changed)
    claudeAsk = Property(str, lambda self: self.ask, notify=changed)
    claudeModel = Property(str, lambda self: self.selected, notify=changed)
    chatModels = Property('QVariantList', lambda self: self.catalogue, constant=True)
    claudeBuildSupported = Property(bool, lambda self: True, constant=True)
    claudeModeSupported = Property(bool, lambda self: True, constant=True)
    claudePictures = Property('QVariantList', lambda self: [], constant=True)
    favoriteModels = Property('QVariantList', lambda self: ['muse'], constant=True)
    catalogueBusy = Property(bool, lambda self: False, constant=True)
    providerAuthBusy = Property(bool, lambda self: False, constant=True)
    providerAuthMessage = Property(str, lambda self: '', constant=True)
    museTracker = Property('QVariantMap', lambda self: {}, constant=True)
    claudeResearchPrompt = Property(bool, lambda self: self.prompt, notify=changed)
    claudeResearch = Property('QVariantMap', lambda self: dict(papers=False, photos=False, models=False), constant=True)

    @Slot(str)
    def setClaudeMode(self, mode):
        self.mode = mode
        self.changed.emit()

    @Slot(str)
    def setClaudeModel(self, model):
        self.selected = model
        self.changed.emit()

    @Slot()
    def requestClaudeResearch(self):
        self.prompt = True
        self.changed.emit()

    @Slot(bool, bool, bool)
    def setClaudeResearch(self, *choices):
        self.prompt = False
        self.changed.emit()

    @Slot(str, result=bool)
    def claudeSend(self, text):
        self.sent.append(text)
        return True

    @Slot(bool)
    def claudeAnswer(self, answer):
        self.answers.append(answer)
        self.ask = ''
        self.changed.emit()

    @Slot()
    def claudeNew(self):
        self.link.messages = []
        self.log.sync()

    @Slot()
    def refreshChat(self):
        pass

    @Slot(str, result='QVariantMap')
    def providerAccountInfo(self, model_id):
        import provider_auth
        return provider_auth.account_info(next((m for m in self.catalogue if m['id'] == model_id), None))

    @Slot(str)
    def signInProvider(self, model_id):
        self.account_requested = model_id

    @Slot()
    def refreshMuseTracker(self):
        pass

    @Slot(str)
    def toggleFavorite(self, key):
        pass


class ChatAppearanceTests(unittest.TestCase):
    def setUp(self):
        self.warnings = []
        self.previous_handler = qInstallMessageHandler(lambda kind, ctx, msg: self.warnings.append(msg))
        self.host = AppearanceHost()
        self.engine = QQmlEngine()
        self.engine.rootContext().setContextProperty('host', self.host)
        source = os.environ.get('CHAT_APPEARANCE_QML', str(ROOT / 'cabinet/ClaudePanel.qml'))
        self.component = QQmlComponent(self.engine, QUrl.fromLocalFile(source))
        self.card = self.component.create()
        self.assertIsNotNone(self.card, str(self.component.errors()))
        self.window = QQuickWindow()
        self.window.contentItem().setTransformOrigin(QQuickItem.TopLeft)
        self.window.setColor(QColor('#bcb9ac'))
        self.window.resize(440, 660)
        self.card.setParentItem(self.window.contentItem())
        self.card.setX(8)
        self.card.setY(8)
        self.window.show()  # offscreen: no display connection or real focus
        self.card.setProperty('open', True)
        settle(1250)

    def tearDown(self):
        self.host.log._timer.stop()
        self.card.setProperty('open', False)
        self.card.setParentItem(None)
        self.card.deleteLater()
        self.window.close()
        settle(20)
        qInstallMessageHandler(self.previous_handler)
        self.assertEqual(self.warnings, [], '\n'.join(self.warnings))

    def find(self, name):
        item = self.card.findChild(QObject, name)
        self.assertIsNotNone(item, name)
        return item

    def click(self, name):
        item = self.find(name)
        point = item.mapToScene(item.boundingRect().center()).toPoint()
        QTest.mouseClick(self.window, Qt.LeftButton, Qt.NoModifier, point)
        settle()

    def snap(self, name):
        dest = os.environ.get('CHAT_APPEARANCE_SNAP_DIR')
        if dest:
            folder = Path(dest)
            folder.mkdir(parents=True, exist_ok=True)
            self.assertTrue(self.window.grabWindow().save(str(folder / (name + '.png'))))

    def assert_layout(self):
        provider = self.find('claudeProviderLabel')
        build = self.find('claudeBuildMode')
        research = self.find('claudeResearchOptions')
        composer = self.find('claudeComposer')
        paper = self.find('claudeTranscript')
        actions = self.find('claudeActions')
        # Control regions remain disjoint after names wrap and the composer grows.
        self.assertLessEqual(provider.mapToScene(QPoint(0, int(provider.height()))).y(), build.mapToScene(QPoint()).y())
        self.assertLessEqual(build.mapToScene(QPoint(int(build.width() * 2), 0)).x(), research.mapToScene(QPoint()).x())
        self.assertLessEqual(research.mapToScene(QPoint(0, int(research.height()))).y(), paper.mapToScene(QPoint()).y())
        self.assertLessEqual(paper.y() + paper.height(), composer.y())
        self.assertLessEqual(composer.y() + composer.height(), actions.y())
        for item in (provider, build, research, composer, paper, actions):
            self.assertGreaterEqual(item.width(), 0)
            self.assertGreaterEqual(item.height(), 0)
            lo = item.mapToScene(QPoint())
            hi = item.mapToScene(QPoint(int(item.width()), int(item.height())))
            self.assertGreaterEqual(lo.x(), 0)
            self.assertGreaterEqual(lo.y(), 0)
            self.assertLessEqual(hi.x(), self.window.width() + 1)
            self.assertLessEqual(hi.y(), self.window.height() + 1)

    def test_layouts_at_minimum_default_maximum_zoom_and_long_names(self):
        self.host.link.messages = [
            dict(role='you', text='Make me a weevil with a royal blue shell and a ridiculously long snout.'),
            dict(role='claude', text='A tiny gentleman with a very big nose.\n\nI’ll give him a deep blue shell, a warm copper snout, and six little feet.'),
            dict(role='tool', text='Shaping the shell', done=True),
            dict(role='claude', text='Your weevil is ready. The elytra have a clean centre seam, with small antennae tucked beside his snout.'),
        ]
        self.host.log.sync()
        for name, width, height, big, folded, zoom in [
            ('default', 440, 660, False, False, 1),
            ('narrow', 340, 560, False, False, 1),
            ('expanded', 640, 760, True, False, 1),
            ('folded', 440, 540, False, True, 1),
            ('zoom-min', 440, 660, False, False, .75),
            ('zoom-max', 640, 960, False, False, 1.5),
        ]:
            with self.subTest(name=name):
                self.card.setProperty('big', big)
                self.card.setProperty('folded', folded)
                self.window.resize(width, height)
                settle()
                # Parent dimensions stay in logical coordinates, as with Qt DPI scaling.
                self.window.contentItem().setWidth((width - 16) / zoom)
                self.window.contentItem().setHeight((height - 16) / zoom)
                self.window.contentItem().setScale(zoom)
                self.card.setProperty('maxHeight', min(620, (height - 32) / zoom))
                settle(220)
                self.assert_layout()
                self.snap(name)
        self.window.contentItem().setScale(1)
        self.window.resize(340, 560)
        self.window.contentItem().setWidth(340)
        self.window.contentItem().setHeight(560)
        self.host.setClaudeModel('long')
        settle()
        self.assert_layout()
        self.assertLessEqual(self.find('claudeProviderLabel').property('lineCount'), 2)
        self.snap('long-provider')
        self.find('claudeInput').setProperty('text', ('AnUnbrokenAssetName' * 30 + '\n') * 12)
        settle()
        self.assert_layout()
        self.assertGreater(self.find('claudeInput').property('contentHeight'), self.find('claudeComposer').height())
        self.snap('long-input')

    def test_larger_fallback_fonts_and_unbroken_paths_fit(self):
        self.window.resize(340, 580)
        self.host.setClaudeModel('long')
        self.host.link.messages = [dict(role='claude', text='Saved at /home/artist/models/' + 'a_long_asset_name_' * 18)]
        self.host.log.sync()
        settle()
        # Change actual Qt font metrics, in addition to the zoom captures above.
        for item in self.card.findChildren(QQuickItem):
            if item.metaObject().indexOfProperty('font') >= 0:
                font = item.property('font')
                font.setFamily('DejaVu Serif')
                if font.pixelSize() > 0:
                    font.setPixelSize(round(font.pixelSize() * 1.25))
                else:
                    font.setPointSizeF(font.pointSizeF() * 1.25)
                item.setProperty('font', font)
        settle()
        self.assert_layout()
        for message in self.card.findChildren(QQuickItem, 'claudeMessageText'):
            self.assertLessEqual(message.property('contentWidth'), message.width() + 1)
        self.snap('larger-fonts')

    def test_keyboard_mouse_modes_research_and_send_remain_reachable(self):
        self.click('claudePlanMode')
        self.assertEqual(self.host.mode, 'plan')
        self.click('claudeBuildMode')
        self.assertEqual(self.host.mode, 'build')
        field = self.find('claudeInput')
        field.setProperty('text', 'make a weevil')
        field.forceActiveFocus()
        QTest.keyClick(self.window, Qt.Key_Return, Qt.ShiftModifier)
        self.assertEqual(self.host.sent, [])
        self.assertIn('\n', field.property('text'))
        QTest.keyClick(self.window, Qt.Key_Return)
        self.assertEqual(self.host.sent, ['make a weevil'])
        self.assertEqual(field.property('text'), '')
        self.click('claudeResearchOptions')
        self.assertTrue(self.find('claudeResearchConsent').isVisible())
        self.snap('research')
        self.click('researchSkip')
        self.assertFalse(self.host.prompt)
        field.setProperty('text', 'a second thought')
        self.click('claudeSendKey')
        self.assertEqual(self.host.sent[-1], 'a second thought')

    def test_picker_and_approval_are_visible_and_bounded(self):
        QMetaObject.invokeMethod(self.card, 'showPopup')
        settle()
        popup = self.find('claudeModelPickPopup')
        self.assertTrue(popup.property('visible'))
        self.snap('picker')
        QMetaObject.invokeMethod(popup, 'close')
        self.host.ask = 'Read a reference file from the workspace'
        self.host.changed.emit()
        settle()
        self.assert_layout()
        self.snap('approval')
        self.click('claudeAllow')
        self.assertEqual(self.host.answers, [True])


if __name__ == '__main__':
    unittest.main()
