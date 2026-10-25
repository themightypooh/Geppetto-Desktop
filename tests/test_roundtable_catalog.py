"""Catalogue parity and provider routing without invoking real AI providers."""
import json
import os
import sys
import tempfile
import threading
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'cabinet'))
import roundtable_catalog as catalog
import chat
import claude_link


class CatalogTests(unittest.TestCase):
    def test_reads_choices_and_seats_without_executing_roundtable(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / 'roundtable-data/personas').mkdir(parents=True)
            (root / 'hub_seat.py').write_text('')
            (root / 'app.py').write_text('''
raise RuntimeError("must not import the GUI")
CLAUDE_MODELS = ['', 'fable', 'opus']
CODEX_MODELS = ['', 'model-c']
GROK_MODELS = ['', 'model-g']
HUB_MODELS = {'ollama': discovery(), 'cursor': ['auto', 'composer'], 'openai-compat': [''], 'demo': ['demo-1']}
AGY_MODELS_FALLBACK = ['', 'gemini-a']
''')
            (root / 'roundtable-data/seats.json').write_text(json.dumps({
                'Claude 2': {'cli': 'opencode', 'model': 'opencode/beam-free'},
                '_names': {'Claude 2': 'Zoopa'}}))
            (root / 'roundtable-data/personas/Claude 2.md').write_text('persona')
            result = type('Result', (), {'returncode': 0, 'stdout': 'opencode/beam-free\nreflection/beam-1.0-501b-a23b\npaid/model\n'})()
            agy_result = type('Result', (), {'returncode': 0, 'stdout': 'gemini-a\n'})()
            with patch.object(catalog, 'ROOT', root), patch.object(catalog, 'HUB', root), \
                 patch.object(catalog.shutil, 'which', return_value='/fake/cli'), \
                 patch.object(catalog.subprocess, 'run', side_effect=lambda args, **kw: agy_result if args[0] == 'agy' else result):
                models = catalog.choices()
            for model in ('fable', 'opus', 'model-c', 'model-g', 'auto', 'composer', 'demo-1'):
                self.assertIn(model, [m['model'] for m in models])
            zoopa = next(m for m in models if m['id'] == 'roundtable/seat/Claude 2')
            self.assertEqual(zoopa['family'], 'opencode')
            self.assertIn('quota applies', zoopa['label'])
            self.assertIn('Zoopa', zoopa['label'])
            self.assertEqual(zoopa['kind'], 'roundtable-cli')
            self.assertIn('paid/model', [m['model'] for m in models])
            paid = next(m for m in models if m['model'] == 'reflection/beam-1.0-501b-a23b')
            self.assertIn('paid', paid['label'])
            self.assertEqual(paid['kind'], 'roundtable-cli')
            self.assertEqual(catalog.command(paid, act=False)[-2:], ['--model', 'reflection/beam-1.0-501b-a23b'])
            self.assertEqual(catalog.command(zoopa, act=False)[-2:], ['--model', 'opencode/beam-free'])
            self.assertEqual(catalog.command(paid)[-1], '--act')

    def test_missing_roundtable_is_optional(self):
        with patch.object(catalog, 'ROOT', Path('/nonexistent/roundtable')):
            self.assertEqual({m['family'] for m in catalog.choices(False)}, {'codex', 'opencode'})

    def test_native_claude_quota_error_identifies_real_provider(self):
        with tempfile.TemporaryDirectory() as tmp:
            link = claude_link.ClaudeLink(state_path=str(Path(tmp) / 'state.json'))
            link.model, link.persona = 'opus', 'Zoopa persona'
            link.handle({'type': 'result', 'is_error': True, 'subtype': 'error', 'result': 'usage limit reached'})
            self.assertEqual(link.messages[-1]['text'], 'Zoopa · Claude CLI · opus: usage limit reached')

    def test_bridge_streams_and_final_result_is_not_duplicated(self):
        with tempfile.TemporaryDirectory() as tmp:
            script = Path(tmp) / 'bridge.py'
            script.write_text('''import json, sys
prompt = sys.stdin.read()
assert 'hello' in prompt
print(json.dumps({'type':'stream_event','event':{'delta':{'type':'text_delta','text':'hello'}}}), flush=True)
print(json.dumps({'type':'result','result':'hello'}), flush=True)
''')
            model = dict(kind='roundtable-cli', family='cursor', model='auto', bridge=str(script), label='Cursor')
            with patch.object(chat, 'CLAUDE_HOME', tmp):
                result = ''.join(chat.stream(model, [dict(role='you', text='hello')], threading.Event()))
            self.assertEqual(result, 'hello')

    def test_bridge_provider_failure_does_not_claim_success(self):
        with tempfile.TemporaryDirectory() as tmp:
            script = Path(tmp) / 'bridge.py'
            script.write_text("import sys\nsys.stdin.read()\nprint('Free usage limit reached', file=sys.stderr)\nsys.exit(1)\n")
            model = dict(kind='roundtable-cli', family='opencode', model='opencode/beam-free',
                         bridge=str(script), label='Zoopa · OpenCode · Beam Free')
            with patch.object(chat, 'CLAUDE_HOME', tmp), patch.object(catalog, 'NATIVE_SHIM', script):
                with self.assertRaisesRegex(RuntimeError, 'Zoopa.*OpenCode.*Free usage limit reached'):
                    list(chat.stream(model, [dict(role='you', text='hello')], threading.Event()))


if __name__ == '__main__':
    unittest.main()
