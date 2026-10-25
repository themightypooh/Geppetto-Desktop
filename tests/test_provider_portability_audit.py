"""Release routing regressions: fake CLIs only, no providers, Qt or real assets."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'cabinet'))
import provider_cli
import roundtable_catalog as catalog


class ProviderPortabilityTests(unittest.TestCase):
    def test_missing_optional_projects_does_not_hide_native_clis(self):
        with patch.object(catalog, 'ROOT', Path('/nonexistent/audit-roundtable')), \
                patch.object(catalog, 'HUB', Path('/nonexistent/audit-hub')), \
                patch.object(catalog.shutil, 'which', return_value='/fake/cli'):
            choices = catalog.choices(False)
        self.assertEqual({m['id'] for m in choices},
                         {'roundtable/codex/default', 'roundtable/opencode/default'})
        self.assertTrue(all(m['available'] for m in choices))
        self.assertTrue(all(m['bridge'] == str(catalog.NATIVE_SHIM) for m in choices))

    def test_missing_native_clis_are_unavailable(self):
        with patch.object(catalog, 'ROOT', Path('/nonexistent/audit-roundtable')), \
                patch.object(catalog.shutil, 'which', return_value=None):
            self.assertTrue(all(not m['available'] for m in catalog.choices(False)))

    def test_v2_enabled_models_and_exact_variants(self):
        response = {'data': [
            {'providerID': 'openai', 'id': 'gpt-test',
             'variants': [{'id': 'fast'}, {'id': 'high'}]},
            {'providerID': 'xai', 'id': 'grok-test'},
            {'providerID': 'ollama', 'id': 'local:latest'},
            {'providerID': 'disabled', 'id': 'hidden', 'enabled': False},
        ]}
        with patch.object(catalog.shutil, 'which', return_value='/fake/opencode'), \
                patch.object(catalog.subprocess, 'run', return_value=
                             subprocess.CompletedProcess([], 0, json.dumps(response), '')) as run:
            models = catalog.opencode_models()
        self.assertEqual(models, ['openai/gpt-test', 'openai/gpt-test#fast',
                                  'openai/gpt-test#high', 'xai/grok-test', 'ollama/local:latest'])
        self.assertEqual(run.call_count, 1)

    def test_plain_discovery_fallback_includes_non_beam_models(self):
        responses = [subprocess.CompletedProcess([], 1, '', 'unsupported'),
                     subprocess.CompletedProcess([], 0,
                         'Available models\nopenai/gpt-test\nxai/grok-test\n'
                         'ollama/local:latest\nopencode/promo-free\nopenai/gpt-test\n', '')]
        with patch.object(catalog.shutil, 'which', return_value='/fake/opencode'), \
                patch.object(catalog.subprocess, 'run', side_effect=responses):
            self.assertEqual(catalog.opencode_models(), ['openai/gpt-test', 'xai/grok-test',
                                                        'ollama/local:latest', 'opencode/promo-free'])

    def test_discovery_failure_is_not_model_success(self):
        with patch.object(catalog.shutil, 'which', return_value='/fake/opencode'), \
                patch.object(catalog.subprocess, 'run', side_effect=subprocess.TimeoutExpired('fake', 8)):
            self.assertEqual(catalog.opencode_models(), [])

    def test_seat_ids_personas_and_other_bridges_preserved(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            personas = root / 'roundtable-data/personas'
            personas.mkdir(parents=True)
            (root / 'app.py').write_text("CODEX_MODELS = ['', 'chosen-codex']\n"
                                        "HUB_MODELS = {'cursor': ['auto']}\n")
            (root / 'hub_seat.py').touch()
            (personas / 'Knight.md').write_text('optional persona')
            (root / 'roundtable-data/seats.json').write_text(json.dumps({
                'Knight': {'cli': 'opencode', 'model': 'openai/exact#high'}}))
            with patch.object(catalog, 'ROOT', root), patch.object(catalog, 'HUB', root), \
                    patch.object(catalog.shutil, 'which', return_value='/fake/cli'):
                choices = catalog.choices(False)
            seat = next(m for m in choices if m['id'] == 'roundtable/seat/Knight')
            cursor = next(m for m in choices if m['family'] == 'cursor')
            self.assertEqual(seat['system_file'], str(personas / 'Knight.md'))
            command = catalog.command(seat, act=False)
            self.assertEqual(command[1], str(catalog.NATIVE_SHIM))
            self.assertEqual(command[-2:], ['--model', 'openai/exact#high'])
            self.assertEqual(catalog.command(cursor)[1], str(root / 'hub_seat.py'))

    def test_stale_external_bridge_cannot_redirect_native_provider(self):
        for family in ('codex', 'opencode'):
            model = dict(family=family, model='exact-selected-model', bridge='/unowned/bridge.py')
            command = catalog.command(model, act=True)
            self.assertEqual(command[1], str(catalog.NATIVE_SHIM))
            self.assertEqual(command[-3:], ['--model', 'exact-selected-model', '--act'])

    def _fake_cli(self, root, provider, failure=False, empty=False):
        exe = root / provider
        exe.write_text('#!' + sys.executable + '\n' + f'''
import json, os, pathlib, sys
args = sys.argv[1:]
if {provider!r} == 'codex':
    prompt = sys.stdin.read()
    pathlib.Path(args[args.index('-o') + 1]).write_text({'' if empty else 'fake native reply'!r})
else:
    request = pathlib.Path(args[args.index('--file') + 1])
    prompt = request.read_text()
    assert request.stat().st_mode & 0o077 == 0
pathlib.Path({str(root / 'capture.json')!r}).write_text(json.dumps(dict(
    args=args, prompt=prompt, cwd=os.getcwd(), home=os.environ.get('HOME'))))
if {failure!r}:
    print('fake authentication or quota failure', file=sys.stderr)
    sys.exit(7)
if {provider!r} == 'opencode':
    print({'' if empty else 'fake native reply'!r})
''')
        exe.chmod(0o700)

    def test_owned_shim_fake_clis_exact_models_modes_workspace_and_private_prompt(self):
        for provider in ('codex', 'opencode'):
            for act in (False, True):
                with self.subTest(provider=provider, act=act), tempfile.TemporaryDirectory() as tmp:
                    root = Path(tmp)
                    self._fake_cli(root, provider)
                    home, workspace = root / 'fresh-home', root / 'workspace with spaces'
                    home.mkdir()
                    workspace.mkdir()
                    env = dict(os.environ, HOME=str(home), PATH=str(root),
                               PYTHONDONTWRITEBYTECODE='1', TMPDIR=str(root))
                    cmd = [sys.executable, str(catalog.NATIVE_SHIM), '--provider', provider,
                           '--model', 'provider/exact-model#high'] + (['--act'] if act else [])
                    result = subprocess.run(cmd, input='private audit request', capture_output=True,
                                            text=True, cwd=workspace, env=env, timeout=10)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertEqual(json.loads(result.stdout),
                                     {'type': 'result', 'result': 'fake native reply'})
                    capture = json.loads((root / 'capture.json').read_text())
                    self.assertEqual(capture['prompt'], 'private audit request')
                    self.assertEqual(capture['cwd'], str(workspace))
                    self.assertEqual(capture['home'], str(home))
                    self.assertNotIn('private audit request', ' '.join(capture['args']))
                    flag = '-m' if provider == 'codex' else '--model'
                    self.assertEqual(capture['args'][capture['args'].index(flag) + 1],
                                     'provider/exact-model#high')
                    flag = '--sandbox' if provider == 'codex' else '--agent'
                    expected = ('workspace-write' if act else 'read-only') if provider == 'codex' else (
                        'build' if act else 'plan')
                    self.assertEqual(capture['args'][capture['args'].index(flag) + 1], expected)
                    self.assertFalse(list(root.glob('geppetto-provider-*')), 'private files must be removed')

    def test_empty_or_failed_cli_returns_error_not_success(self):
        for provider in ('codex', 'opencode'):
            for failure in (False, True):
                with self.subTest(provider=provider, failure=failure), tempfile.TemporaryDirectory() as tmp:
                    root = Path(tmp)
                    self._fake_cli(root, provider, failure=failure, empty=not failure)
                    with patch.dict(os.environ, {'PATH': str(root), 'TMPDIR': str(root)}):
                        with self.assertRaisesRegex(RuntimeError, 'failure' if failure else 'no text'):
                            provider_cli.run(provider, '', 'audit')

    def test_missing_cli_reports_setup_error(self):
        with patch.object(provider_cli.shutil, 'which', return_value=None):
            with self.assertRaisesRegex(RuntimeError, 'not installed'):
                provider_cli.run('codex', '', 'audit')

    def test_default_selection_does_not_override_users_cli_model(self):
        for provider in ('codex', 'opencode'):
            def fake_run(args, **kwargs):
                if provider == 'codex':
                    Path(args[args.index('-o') + 1]).write_text('reply')
                return subprocess.CompletedProcess(args, 0, 'reply', '')
            with self.subTest(provider=provider), \
                    patch.object(provider_cli.shutil, 'which', return_value='/fake/' + provider), \
                    patch.object(provider_cli.subprocess, 'run', side_effect=fake_run) as run:
                self.assertEqual(provider_cli.run(provider, '', 'audit'), 'reply')
                self.assertNotIn('--model', run.call_args.args[0])
                self.assertNotIn('-m', run.call_args.args[0])

    def test_codex_missing_answer_does_not_treat_cli_logs_as_reply(self):
        with patch.object(provider_cli.shutil, 'which', return_value='/fake/codex'), \
                patch.object(provider_cli.subprocess, 'run', return_value=
                             subprocess.CompletedProcess([], 0, 'diagnostic log, not an answer', '')):
            with self.assertRaisesRegex(RuntimeError, 'no text'):
                provider_cli.run('codex', '', 'audit')

    def test_release_declares_numpy_and_copies_owned_shim(self):
        source = (Path(__file__).resolve().parents[1] / 'packaging/package-linux.sh').read_text()
        requirements = source.split('> "${STAGE_DIR}/cabinet-requirements.txt"', 1)[1].split('EOF', 1)[0]
        self.assertIn('numpy>=1.24', requirements)
        self.assertTrue(catalog.NATIVE_SHIM.is_file())
        self.assertIn('"${PROJECT_DIR}/cabinet"', source)


if __name__ == '__main__':
    unittest.main()
