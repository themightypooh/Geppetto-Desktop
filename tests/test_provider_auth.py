"""Provider account controls: fake CLI IO/processes and offscreen Qt only."""
import io
import os
import sys
import time
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
os.environ.setdefault('QT_QUICK_BACKEND', 'software')
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'cabinet'))
import provider_auth as auth


class AccountTests(unittest.TestCase):
    def test_native_families_and_personas_route_to_same_account(self):
        for model, family in [({'kind': 'claude-cli', 'id': 'zoopa/opus'}, 'claude'),
                              ({'kind': 'grok-cli'}, 'grok'),
                              *[({'kind': 'roundtable-cli', 'family': f}, f)
                                for f in ('codex', 'opencode', 'cursor')]]:
            info = auth.account_info(model)
            self.assertTrue(info['supported'])
            self.assertEqual(info['family'], family)
            self.assertIn('shared CLI', info['message'])

    def test_unsupported_records_do_not_offer_a_fake_login(self):
        for model in [None, {'kind': 'ollama'}, {'kind': 'openai', 'key_env': 'MY_KEY'},
                      {'kind': 'roundtable-cli', 'family': 'muse'},
                      {'kind': 'roundtable-cli', 'family': 'kimi'},
                      {'kind': 'openai', 'family': 'claude'}]:
            self.assertFalse(auth.account_info(model)['supported'])

    def test_confirmation_cancel_never_runs_a_cli(self):
        run = Mock()
        with patch.object(auth.shutil, 'which', return_value='/fake/cli'), redirect_stdout(io.StringIO()):
            for answer in ('', 'no'):
                self.assertEqual(auth.authenticate('claude', read=lambda _: answer, run=run), 2)
        run.assert_not_called()

    def test_logout_then_login_fixed_argv_and_no_output_capture(self):
        with patch.object(auth.shutil, 'which', return_value='/fake/cli'), redirect_stdout(io.StringIO()):
            for family, steps in auth.COMMANDS.items():
                run = Mock(return_value=SimpleNamespace(returncode=0))
                self.assertEqual(auth.authenticate(family, read=lambda _: 'yes', run=run), 0)
                self.assertEqual([c.args[0] for c in run.call_args_list],
                                 [['/fake/cli', *step] for step in steps])
                self.assertTrue(all(not c.kwargs for c in run.call_args_list))
        self.assertEqual(auth.COMMANDS['opencode'], (('auth', 'login'),))

    def test_failure_stops_sequence_and_is_not_success(self):
        with patch.object(auth.shutil, 'which', return_value='/fake/cli'), redirect_stdout(io.StringIO()):
            for code in (1, 130):
                run = Mock(return_value=SimpleNamespace(returncode=code))
                self.assertEqual(auth.authenticate('codex', read=lambda _: 'yes', run=run), 1)
                self.assertEqual(run.call_count, 1)

    def test_opencode_routes_exact_selected_integration_not_model_or_variant(self):
        info = auth.account_info(dict(kind='roundtable-cli', family='opencode', model='openai/gpt-model#high'))
        self.assertEqual(info['target'], 'openai')
        run = Mock(return_value=SimpleNamespace(returncode=0))
        with patch.object(auth.shutil, 'which', return_value='/fake/opencode'), redirect_stdout(io.StringIO()):
            self.assertEqual(auth.authenticate('opencode', read=lambda _: 'yes', run=run, target=info['target']), 0)
        run.assert_called_once_with(['/fake/opencode', 'auth', 'login', 'openai'])
        self.assertEqual(auth.authenticate('opencode', run=run, target='--bad'), 1)

    def test_terminal_launch_is_fixed_argv_waits_and_rejects_unknown_families(self):
        for terminal, flags in [('konsole', ['--separate', '--nofork', '-e']),
                                ('gnome-terminal', ['--wait', '--']), ('xterm', ['-e'])]:
            def which(name):
                return '/bin/' + name if name in ('codex', terminal) else None
            with patch.object(auth.shutil, 'which', side_effect=which):
                command = auth.terminal_command('codex')
                self.assertEqual(command[:1 + len(flags)], ['/bin/' + terminal, *flags])
                self.assertEqual(command[-2:], ['--family', 'codex'])
                self.assertNotIn('sh', command)
        with self.assertRaises(RuntimeError):
            auth.terminal_command('codex; rm something')
        with patch.object(auth.shutil, 'which', return_value=None), self.assertRaises(RuntimeError):
            auth.terminal_command('claude')
        with patch.object(auth.shutil, 'which', side_effect=lambda name: '/bin/codex' if name == 'codex' else None), self.assertRaises(RuntimeError):
            auth.terminal_command('codex')


class HostAccountTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        from PySide6.QtGui import QGuiApplication
        cls.app = QGuiApplication.instance() or QGuiApplication([])

    def harness(self):
        from PySide6.QtCore import QObject, Signal
        import host
        class Harness(QObject):
            chatChanged = Signal()
            providerAccountInfo = host.Host.providerAccountInfo
            signInProvider = host.Host.signInProvider
            _provider_auth_finished = host.Host._provider_auth_finished
            _provider_auth_error = host.Host._provider_auth_error
            claudeSend = host.Host.claudeSend
            sendChat = host.Host.sendChat
        h = Harness()
        h._provider_auth = None
        h._provider_auth_message = ''
        h._chat_busy = False
        h.claude = SimpleNamespace(status='idle', messages=['keep me'], session='keep-session')
        h._chat_models = [dict(id='codex', kind='roundtable-cli', family='codex')]
        h.refreshChat = Mock()
        h.set_status = Mock()
        return h

    def wait(self, h):
        until = time.monotonic() + 4
        while h._provider_auth is not None and time.monotonic() < until:
            self.app.processEvents()
            time.sleep(.005)
        self.assertIsNone(h._provider_auth)

    def test_busy_chat_and_unknown_provider_cannot_launch(self):
        h = self.harness()
        with patch.object(auth, 'terminal_command') as launch:
            for state in ('working', 'starting', 'paused'):
                h.claude.status = state
                h.signInProvider('codex')
            launch.assert_not_called()
        h.claude.status = 'idle'
        h.signInProvider('not-a-model')
        self.assertIsNone(h._provider_auth)
        self.assertIn('no supported', h._provider_auth_message)

    def test_real_qprocess_completion_refresh_guards_and_preserved_conversation(self):
        import host
        h = self.harness()
        with patch.object(auth, 'terminal_command', return_value=[sys.executable, '-c', 'import time; time.sleep(.1)']) as command, \
             patch.object(host.chatlib, 'child_env', return_value={'PATH': '/usr/bin', 'HOME': '/test'}):
            h.signInProvider('codex')
            process = h._provider_auth
            self.assertIsNotNone(process)
            self.assertNotIn('ANTHROPIC_API_KEY', process.processEnvironment().keys())
            h.signInProvider('codex')
            command.assert_called_once_with('codex', '')
            self.assertFalse(h.claudeSend('do not send during login'))
            h.sendChat('do not send during login')
            self.wait(h)
        h.refreshChat.assert_called_once()
        self.assertIn('terminal closed', h._provider_auth_message)
        self.assertEqual(h.claude.messages, ['keep me'])
        self.assertEqual(h.claude.session, 'keep-session')

    def test_terminal_failure_unlocks_and_never_claims_signed_in(self):
        for command in [[sys.executable, '-c', 'raise SystemExit(2)'], ['/nonexistent/geppetto-auth-terminal']]:
            h = self.harness()
            with patch.object(auth, 'terminal_command', return_value=command):
                h.signInProvider('codex')
                self.wait(h)
            self.assertIn('cancelled or failed', h._provider_auth_message)
            h.refreshChat.assert_called_once()


class AccountAppearanceTests(unittest.TestCase):
    # Reuse the isolated real-QML harness, not its historical mode/research tests.
    from tests.test_chat_appearance import ChatAppearanceTests as _UI
    setUp = _UI.setUp
    tearDown = _UI.tearDown
    find = _UI.find
    click = _UI.click
    snap = _UI.snap

    def test_account_button_routing_disabled_bridges_and_bounded_three_zoom_layouts(self):
        from PySide6.QtCore import QMetaObject, QPointF
        from tests.test_chat_appearance import settle
        self.host.setClaudeModel('claude')
        for scale in (.75, 1, 1.5):
            self.window.resize(round(360 * scale), round(660 * scale))
            settle()  # let QQuickWindow publish its resize before fixing logical dimensions
            self.window.contentItem().setWidth(344)
            self.window.contentItem().setHeight(644)
            self.window.contentItem().setScale(scale)
            self.card.setProperty('popupParent', self.window.contentItem())
            QMetaObject.invokeMethod(self.card, 'showPopup')
            settle()
            button = self.find('providerSignInAgain')
            note = self.find('providerAccountNote')
            self.assertTrue(button.isEnabled())
            self.assertLessEqual(note.property('lineCount'), 2)
            for item in (button, note):
                top = item.mapToScene(QPointF(0, 0))
                bottom = item.mapToScene(QPointF(item.width(), item.height()))
                self.assertGreaterEqual(top.x(), 0)
                self.assertGreaterEqual(top.y(), 0)
                self.assertLessEqual(bottom.x(), self.window.width() + 1)
                self.assertLessEqual(bottom.y(), self.window.height() + 1)
            self.assertLessEqual(button.mapToScene(QPointF(0, button.height())).y(),
                                 note.mapToScene(QPointF(0, 0)).y())
            self.snap('account-picker-' + str(scale))
            self.click('providerSignInAgain')
            self.assertEqual(self.host.account_requested, 'claude')
            QMetaObject.invokeMethod(self.find('claudeModelPickPopup'), 'close')
        self.host.setClaudeModel('muse')
        QMetaObject.invokeMethod(self.card, 'showPopup')
        settle()
        self.assertFalse(self.find('providerSignInAgain').isEnabled())
        self.assertIn('own app or bridge', self.find('providerAccountNote').property('text'))

    def test_larger_fonts_wrap_actions_without_overlap(self):
        from PySide6.QtCore import QMetaObject, QPointF
        from PySide6.QtQuick import QQuickItem
        from tests.test_chat_appearance import settle
        self.window.resize(340, 660)
        settle()
        self.host.setClaudeModel('claude')
        QMetaObject.invokeMethod(self.card, 'showPopup')
        settle()
        button = self.find('providerSignInAgain')
        note = self.find('providerAccountNote')
        footer = button.parentItem().parentItem()
        for item in footer.findChildren(QQuickItem):
            if item.metaObject().indexOfProperty('font') >= 0:
                font = item.property('font')
                font.setFamily('DejaVu Serif')
                font.setPixelSize(round(font.pixelSize() * 1.5))
                item.setProperty('font', font)
        settle()
        for item in (button, note):
            bottom = item.mapToScene(QPointF(item.width(), item.height()))
            self.assertLessEqual(bottom.x(), self.window.width() + 1)
            self.assertLessEqual(bottom.y(), self.window.height() + 1)
        for text in button.findChildren(QQuickItem):
            if text.metaObject().indexOfProperty('contentWidth') >= 0:
                self.assertLessEqual(text.property('contentWidth'), button.width())
        self.assertLessEqual(button.mapToScene(QPointF(0, button.height())).y(),
                             note.mapToScene(QPointF(0, 0)).y())
        self.snap('account-picker-large-fonts')


if __name__ == '__main__':
    unittest.main()
