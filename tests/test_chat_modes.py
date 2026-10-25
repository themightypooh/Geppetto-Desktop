"""Visible card -> request mode -> actual CLI sandbox/cwd; no paid model calls."""
import importlib.util
import os
import threading
import unittest
from pathlib import Path
from unittest.mock import patch

from tests import test_claude_chest as fixture


class VisibleModeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        fixture.CardTests.setUpClass()
        cls.ui = fixture.CardTests()

    @classmethod
    def tearDownClass(cls):
        fixture.CardTests.tearDownClass()

    def test_visible_controls_route_both_modes_and_lock_during_work(self):
        import host
        ui = self.ui
        h = ui.host
        h.claude._set('idle')
        h.setClaudeResearch(False, False, False)
        h._catalogue_busy = True  # isolate this card's test-owned catalogue
        model = dict(id='test/codex', kind='roundtable-cli', family='codex',
                     model='test', label='Codex test', available=True)
        h._chat_models = [model]
        h.setClaudeModel(model['id'])
        ui.root.setProperty('claudeOpen', True)
        for mode, name in [('plan', 'claudePlanMode'), ('build', 'claudeBuildMode')]:
            ui.settle(.1)
            ui.click(name)
            self.assertEqual(h.claudeMode, mode)
            seen = []
            called = threading.Event()
            def stream(*args, **kwargs):
                seen.append(kwargs['mode'])
                called.set()
                yield 'mode verified'
            with patch.object(host.chatlib, 'stream', side_effect=stream), \
                 patch.object(host.chatlib, 'effigy_reference', return_value=''):
                h.claudeSend('check mode')
                self.assertTrue(called.wait(3))
                self.assertEqual(seen, [mode])
                h.setClaudeMode('build' if mode == 'plan' else 'plan')
                self.assertEqual(h.claudeMode, mode)
                ui.settle(.2)
            self.assertEqual(h.claude.status, 'idle')
        snap = os.environ.get('GEPPETTO_MODE_SNAP')
        if snap:
            ui.find('claudeWin').grabWindow().save(snap)


class ProviderModeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = Path.home() / 'Projects/model-hub-native/providers.py'
        if not path.exists():
            raise unittest.SkipTest('Optional local Model Hub is not installed')
        spec = importlib.util.spec_from_file_location('chat_mode_providers', path)
        cls.providers = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.providers)

    def test_codex_and_opencode_use_real_modes_and_workspace(self):
        p = self.providers
        for acting in (False, True):
            cfg = dict(act=acting, cwd='/tmp/opencode/mode-workspace', model='test')
            with patch.object(p.shutil, 'which', return_value='/fake/cli'), \
                 patch.object(p, '_cli') as cli:
                cli.return_value.stdout = 'OK'
                p.call_codex(cfg, [])
                args = cli.call_args.args[0]
                self.assertEqual(args[args.index('--sandbox') + 1],
                                 'workspace-write' if acting else 'read-only')
                self.assertEqual(cli.call_args.kwargs['cwd'], cfg['cwd'] if acting else None)
                p.call_opencode(cfg, [])
                args = cli.call_args.args[0]
                self.assertEqual(args[args.index('--agent') + 1], 'build' if acting else 'plan')
                self.assertEqual(cli.call_args.kwargs['cwd'], cfg['cwd'])


if __name__ == '__main__':
    unittest.main()
