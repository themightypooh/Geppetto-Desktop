"""Drawer model discovery, actual HTTP protocols, cancellation and research routing."""
import base64
import json
import os
import sys
import tempfile
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from threading import Event, Thread
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'cabinet'))
os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
import chat

REAL_CLAUDE = chat.CLAUDE
chat.CLAUDE = '/no/claude'   # the catalogue tests see only their own fake models
chat.GROK = '/no/grok'


def fixture(**kw):
    m = dict(id='local', label='Local test', kind='openai', endpoint='http://127.0.0.1:9/v1/',
             model='test-model', available=True, research=True)
    m.update(kw)
    return m


class CatalogueTests(unittest.TestCase):
    def test_discovers_installed_models_and_chat_only_rp(self):
        def get(url):
            if url.endswith('api/tags'):
                return {'models': [dict(name='prompt-pocket:latest', details={'family': 'qwen35', 'parameter_size': '4.5B'}, capabilities=['tools'])]}
            return {'data': [{'id': 'koboldcpp/rp'}]}
        with patch.object(chat, '_get', side_effect=get), patch.object(chat, 'CONFIG', '/no/file'), patch.object(chat, 'DEFAULT_MODELS', []):
            models = chat.models()
        self.assertEqual(models[0]['id'], 'ollama/prompt-pocket:latest')
        self.assertTrue(models[0]['research'])
        self.assertFalse(models[1]['research'])
        self.assertNotIn('llama3.1:latest', [m['model'] for m in models])

    def test_custom_model_overrides_and_missing_key_is_visible(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / 'models.json'
            p.write_text(json.dumps([fixture(key_env='GEPPETTO_TEST_MISSING_KEY')]))
            with patch.object(chat, 'CONFIG', str(p)), patch.object(chat, 'DEFAULT_MODELS', []), patch.dict(os.environ, {}, clear=True):
                models = chat.models(discover=False)
        self.assertFalse(models[0]['available'])
        self.assertIn('key missing', models[0]['label'])

    def test_existing_env_file_is_read_without_overriding_environment(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / 'key.env'; p.write_text('TEST_KEY="file-secret"\n')
            m = fixture(key_env='TEST_KEY', env_file=str(p))
            with patch.dict(os.environ, {}, clear=True):
                self.assertEqual(chat.key(m), 'file-secret')
            with patch.dict(os.environ, {'TEST_KEY': 'environment-secret'}):
                self.assertEqual(chat.key(m), 'environment-secret')

    def test_research_uses_selected_provider_and_never_puts_key_in_args(self):
        with patch.dict(os.environ, {'TEST_KEY': 'secret'}):
            args = chat.research_arguments(fixture(kind='anthropic', key_env='TEST_KEY'))
        self.assertEqual(args[:2], ['--provider', 'anthropic'])
        self.assertIn('--key-env', args)
        self.assertNotIn('secret', args)
        with self.assertRaises(RuntimeError):
            chat.research_arguments(fixture(research=False))
        args = chat.research_arguments(fixture(kind='ollama', endpoint='http://localhost:11434/', extra_body={'reasoning_effort': 'none'}))
        self.assertIn('http://localhost:11434/v1/', args)
        self.assertIn('--request-options', args)
        self.assertNotIn('--vision', args)
        self.assertIn('--vision', chat.research_arguments(fixture(vision=True)))

    def test_ollama_vision_capability_is_discovered(self):
        def get(url):
            if url.endswith('api/tags'):
                return {'models': [dict(name='seer:latest', details={}, capabilities=['tools', 'vision']),
                                   dict(name='blind:latest', details={}, capabilities=['tools'])]}
            return {'data': []}
        with patch.object(chat, '_get', side_effect=get), patch.object(chat, 'CONFIG', '/no/file'), patch.object(chat, 'DEFAULT_MODELS', []):
            vision = {m['model']: m['vision'] for m in chat.models()}
        self.assertEqual(vision, {'seer:latest': True, 'blind:latest': False})

    def test_research_notes_use_newest_brief_and_are_bounded(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertEqual(chat.research_notes(d), '')
            self.assertEqual(chat.research_notes(os.path.join(d, 'missing')), '')
            Path(d, 'task-01-brief.txt').write_text('old brief')
            Path(d, 'task-02-brief.txt').write_text('new brief\n' + 'x' * 80 + '\n' + 'y' * chat.NOTES_LIMIT)
            notes = chat.research_notes(d)
        self.assertTrue(notes.startswith('new brief'))
        self.assertTrue(notes.endswith('[notes truncated]'))
        self.assertLessEqual(len(notes), chat.NOTES_LIMIT + 20)
        body = chat._body(fixture(), [dict(role='you', text='hi')], notes='SPRUNG HINGE')
        self.assertIn('<research_notes>\nSPRUNG HINGE', body['messages'][0]['content'])
        self.assertNotIn('research_notes', chat._body(fixture(), [dict(role='you', text='hi')])['messages'][0]['content'])

    def test_effigy_reference_picks_matching_sections_within_limit(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / 'api.md'
            p.write_text('# Effigy\nintro\n\n## Saving and exporting\nUse studio.Save to write files.\n\n'
                         '## Lit renders\nShadedRender draws the model.\n\n## Traps\n' + 'z ' * 3000)
            ref = chat.effigy_reference('how do I save and export?', path=p, limit=500)
            self.assertIn('## Saving and exporting', ref)
            self.assertNotIn('ShadedRender', ref)
            self.assertNotIn('## Traps', chat.effigy_reference('traps', path=p, limit=500))
            self.assertEqual(chat.effigy_reference('the and', path=p), '')
            self.assertEqual(chat.effigy_reference('save', path=Path(d) / 'missing.md'), '')
        body = chat._body(fixture(), [dict(role='you', text='hi')], api='PartStudio')
        self.assertIn('<effigy_api>\nPartStudio', body['messages'][0]['content'])

    def test_claude_cli_streams_on_its_own_login(self):
        with tempfile.TemporaryDirectory() as d:
            fake = Path(d) / 'claude'
            fake.write_text('#!/usr/bin/env python3\nimport json, os, sys\n'
                            'said = sys.stdin.read()\n'
                            'assert \'ANTHROPIC_API_KEY\' not in os.environ\n'
                            'assert \'Bash(geppetto-live *)\' in sys.argv and \'--dangerously-skip-permissions\' not in sys.argv\n'
                            'assert sys.argv[sys.argv.index(\'--session-id\') + 1] == \'s1\'\n'
                            'print(json.dumps(dict(type=\'system\')))\n'
                            'for t in (\'saw \', str(\'teal\' in said)):\n'
                            '    print(json.dumps(dict(type=\'stream_event\', event=dict(delta=dict(type=\'text_delta\', text=t)))))\n'
                            'print(json.dumps(dict(type=\'result\', is_error=False)))\n')
            fake.chmod(0o755)
            with patch.object(chat, 'CLAUDE', str(fake)), patch.object(chat, '_get', side_effect=OSError), \
                 patch.object(chat, 'CONFIG', '/no/file'), patch.object(chat, 'DEFAULT_MODELS', []), \
                 patch.dict(os.environ, {'ANTHROPIC_API_KEY': 'must-not-be-used'}):
                models = chat.models()
                self.assertEqual([m['id'] for m in models], ['claude/haiku', 'claude/opus', 'claude/sonnet'])
                self.assertFalse(any(m['research'] for m in models))
                history = [dict(role='you', text='I like teal'), dict(role='model', text='ok'), dict(role='you', text='colour?')]
                self.assertEqual(''.join(chat.stream(chat.find('claude/sonnet', models), history, session='s1')), 'saw True')
                self.assertIn('s1', chat._claude_sessions)   # the next turn resumes it

    def test_grok_cli_streams_on_its_own_login(self):
        with tempfile.TemporaryDirectory() as d:
            fake = Path(d) / 'grok'
            fake.write_text('#!/usr/bin/env python3\nimport json, os, sys\n'
                            'assert \'GROK_SESSION_ID\' not in os.environ\n'
                            'assert \'XAI_API_KEY\' not in os.environ\n'
                            'assert sys.argv[sys.argv.index(\'--session-id\') + 1] == \'g1\'\n'
                            'assert sys.argv[sys.argv.index(\'--permission-mode\') + 1] == \'auto\'\n'
                            'assert \'--single\' not in sys.argv and \'teal\' not in str(sys.argv)\n'
                            'said = open(sys.argv[sys.argv.index(\'--prompt-file\') + 1]).read()\n'
                            'print(json.dumps(dict(type=\'system\', session_id=\'g1\')))\n'
                            'for t in (\'saw \', str(\'teal\' in said)):\n'
                            '    print(json.dumps(dict(type=\'stream_event\', event=dict(delta=dict(type=\'text_delta\', text=t)))))\n'
                            'print(json.dumps(dict(type=\'result\', is_error=False, result=\'saw True\')))\n')
            fake.chmod(0o755)
            with patch.object(chat, 'GROK', str(fake)), patch.object(chat, '_get', side_effect=OSError), \
                 patch.object(chat, 'CONFIG', '/no/file'), patch.object(chat, 'DEFAULT_MODELS', []), \
                 patch.object(chat, 'CLAUDE', '/no/claude'), \
                 patch.dict(os.environ, {'GROK_SESSION_ID': 'must-not-be-used', 'XAI_API_KEY': 'must-not-be-used'}):
                models = chat.models()
                self.assertEqual([m['id'] for m in models if m['id'].startswith('grok/')],
                                 ['grok/grok-4.5', 'grok/grok-4.6', 'grok/grok-4.7', 'grok/grok-4.7-build-fast'])
                history = [dict(role='you', text='I like teal'), dict(role='model', text='ok'), dict(role='you', text='colour?')]
                self.assertEqual(''.join(chat.stream(chat.find('grok/grok-4.7', models), history, session='g1')), 'saw True')
                self.assertIn('g1', chat._grok_sessions)


class EndpointSafetyTests(unittest.TestCase):
    """Audit C1: a record's key may only travel to the host that key belongs to."""

    def test_official_host_is_accepted(self):
        chat.validate_endpoint(fixture(kind='anthropic', endpoint='https://api.anthropic.com/v1/',
                                       key_env='ANTHROPIC_API_KEY'))
        chat.validate_endpoint(fixture(kind='google', endpoint='https://generativelanguage.googleapis.com/v1beta/',
                                       key_env='GEMINI_API_KEY'))
        chat.validate_endpoint(fixture(kind='openai', endpoint='https://api.openai.com/v1/',
                                       key_env='OPENAI_API_KEY'))

    def test_explicit_key_custom_host_is_accepted(self):
        # A new service, its own id, naming its own key variable: the explicit-key custom case.
        chat.validate_endpoint(fixture(id='myservice/model', kind='openai',
                                       endpoint='https://llm.example.com/v1/', key_env='MYSERVICE_API_KEY'))
        chat.validate_endpoint(fixture(id='proxy/claude', kind='anthropic',
                                       endpoint='https://proxy.example.com/v1/', key_env='MY_PROXY_KEY'))

    def test_redirected_known_provider_is_rejected(self):
        preset = next(m for m in chat.DEFAULT_MODELS if m['id'] == 'deepseek/deepseek-v4-pro')
        with self.assertRaisesRegex(RuntimeError, 'known provider'):
            chat.validate_endpoint(dict(preset, endpoint='https://evil.example/v1/'))
        # the default provider key must not go to a non-official host either
        with self.assertRaisesRegex(RuntimeError, 'only sent to'):
            chat.validate_endpoint(fixture(kind='anthropic', endpoint='https://evil.example/v1/',
                                           key_env='ANTHROPIC_API_KEY'))
        with self.assertRaisesRegex(RuntimeError, 'only sent to'):
            chat.validate_endpoint(fixture(kind='google', endpoint='https://evil.example/v1beta/',
                                           key_env='GEMINI_API_KEY'))

    def test_http_nonloopback_is_rejected(self):
        with self.assertRaisesRegex(RuntimeError, 'plain http'):
            chat.validate_endpoint(fixture(endpoint='http://example.com/v1/'))
        with self.assertRaisesRegex(RuntimeError, 'plain http'):
            chat.validate_endpoint(fixture(kind='anthropic', endpoint='http://api.anthropic.com/v1/',
                                           key_env='ANTHROPIC_API_KEY'))
        chat.validate_endpoint(fixture(endpoint='http://127.0.0.1:11434/'))   # local stays allowed
        chat.validate_endpoint(fixture(endpoint='http://localhost:5001/v1/'))

    def test_request_refuses_a_redirected_provider_before_any_send(self):
        preset = next(m for m in chat.DEFAULT_MODELS if m['id'] == 'deepseek/deepseek-v4-pro')
        with patch.dict(os.environ, {'TEACHER_API_KEY': 'secret'}):
            with self.assertRaisesRegex(RuntimeError, 'known provider'):
                chat.complete(dict(preset, endpoint='https://evil.example/v1/'),
                              [dict(role='you', text='hi')])

    def test_cli_records_and_keyless_records_are_not_blocked(self):
        chat.validate_endpoint(dict(id='claude/opus', kind='claude-cli', endpoint='cli', model='opus'))
        chat.validate_endpoint(fixture(endpoint='https://public.example.com/v1/'))   # no key to leak


class ChildEnvTests(unittest.TestCase):
    """Audit C2/G2: children get a baseline plus only what they themselves need."""

    def test_child_env_is_an_allowlist(self):
        with patch.dict(os.environ, {'PATH': '/bin', 'HOME': '/home/x', 'OPENAI_API_KEY': 'no',
                                     'ANTHROPIC_API_KEY': 'no', 'TEACHER_API_KEY': 'no'}):
            env = chat.child_env()
        self.assertEqual(env.get('PATH'), '/bin')
        self.assertNotIn('OPENAI_API_KEY', env)
        self.assertNotIn('ANTHROPIC_API_KEY', env)
        self.assertNotIn('TEACHER_API_KEY', env)

    def test_research_env_carries_only_the_selected_key(self):
        base = {'PATH': '/bin', 'HOME': '/home/x', 'DOTNET_ROOT': '/dotnet', 'OPENAI_API_KEY': 'stray'}
        with patch.dict(os.environ, {'TEST_KEY': 'picked', 'OTHER_KEY': 'no',
                                     'BRAVE_SEARCH_API_KEY': 'search'}):
            env = chat.research_env(base, fixture(key_env='TEST_KEY'))
        self.assertEqual(env['TEST_KEY'], 'picked')
        self.assertEqual(env['BRAVE_SEARCH_API_KEY'], 'search')   # research's own search key
        self.assertNotIn('OTHER_KEY', env)
        self.assertNotIn('OPENAI_API_KEY', env)                   # a stray in the base is dropped
        self.assertEqual(env['DOTNET_ROOT'], '/dotnet')


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        self.server.body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        self.server.headers = dict(self.headers)
        self.server.path = self.path
        self.send_response(self.server.code)
        self.send_header('Content-Type', self.server.content_type)
        self.end_headers()
        self.wfile.write(self.server.payload)

    def log_message(self, *_):
        pass


class WireTests(unittest.TestCase):
    def setUp(self):
        self.server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        self.server.payload = b'{"choices":[{"message":{"content":"hello"}}]}'
        self.server.content_type = 'application/json'
        self.server.code = 200
        self.server.body = None
        Thread(target=self.server.serve_forever, daemon=True).start()
        self.model = fixture(endpoint='http://127.0.0.1:%s/' % self.server.server_port)

    def tearDown(self):
        self.server.shutdown(); self.server.server_close()

    def test_attached_pictures_go_out_on_the_wire(self):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / 'dot.png'
            path.write_bytes(base64.b64decode(
                'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=='))
            turn = [dict(role='you', text='what is this', images=[str(path)])]
            chat.complete(self.model, turn)
            block = self.server.body['messages'][-1]['content']
            self.assertEqual(block[0]['type'], 'text')
            self.assertTrue(block[1]['image_url']['url'].startswith('data:image/png;base64,'))
            ollama = chat._body(dict(self.model, kind='ollama'), turn)
            self.assertEqual(len(ollama['messages'][-1]['images']), 1)
            anthropic = chat._body(dict(self.model, kind='anthropic'), turn)
            self.assertEqual(anthropic['messages'][-1]['content'][0]['type'], 'image')
            google = chat._body(dict(self.model, kind='google', model='g'), turn)
            self.assertIn('inline_data', google['contents'][-1]['parts'][1])
            self.assertEqual(chat._said(dict(role='you', text='look', images=[str(path)])).count(str(path)), 1)
            self.assertEqual(chat._image_payloads([str(Path(d) / 'note.txt')]), [])
            (Path(d) / 'note.txt').write_text('no')
            self.assertEqual(chat._image_payloads([str(Path(d) / 'note.txt')]), [])

    def test_openai_system_history_and_errors_filtered(self):
        answer = chat.complete(self.model, [dict(role='you', text='hi'), dict(role='error', text='bad'), dict(role='model', text='hello'), dict(role='you', text='again')])
        self.assertEqual(answer, 'hello')
        self.assertEqual([m['role'] for m in self.server.body['messages']], ['system', 'user', 'assistant', 'user'])

    def test_openai_stream_and_custom_header(self):
        self.server.content_type = 'text/event-stream'
        self.server.payload = b'data: {"choices":[{"delta":{"content":"hel"}}]}\n\ndata: {"choices":[{"delta":{"content":"lo"}}]}\n\ndata: [DONE]\n\n'
        with patch.dict(os.environ, {'TEST_KEY': 'private'}):
            answer = chat.complete(dict(self.model, key_env='TEST_KEY', auth_header='api-key'), [dict(role='you', text='hi')])
        self.assertEqual(answer, 'hello')
        self.assertEqual(self.server.headers['Api-Key'], 'private')
        self.assertNotIn('Authorization', self.server.headers)

    def test_ollama_stream_disables_thinking_and_limits_context(self):
        self.server.content_type = 'application/x-ndjson'
        self.server.payload = b'{"message":{"content":"hi"},"done":false}\n{"message":{"content":"!"},"done":true}\n'
        answer = chat.complete(dict(self.model, kind='ollama'), [dict(role='you', text='hi')])
        self.assertEqual(answer, 'hi!')
        self.assertEqual(self.server.path, '/api/chat')
        self.assertFalse(self.server.body['think'])
        self.assertEqual(self.server.body['options']['num_ctx'], 8192)

    def test_anthropic_and_google(self):
        self.server.payload = b'{"content":[{"type":"text","text":"claude"}]}'
        self.assertEqual(chat.complete(dict(self.model, kind='anthropic'), [dict(role='you', text='hi')]), 'claude')
        self.assertIn('system', self.server.body)
        self.server.payload = b'{"candidates":[{"content":{"parts":[{"text":"hidden","thought":true},{"text":"gemini"}]}}]}'
        self.assertEqual(chat.complete(dict(self.model, kind='google', model='with/slash'), [dict(role='you', text='hi')]), 'gemini')
        self.assertIn('with%2Fslash:generateContent', self.server.path)

    def test_null_content_and_http_failures_are_readable(self):
        self.server.payload = b'{"choices":[{"message":{"content":null}}]}'
        with self.assertRaisesRegex(RuntimeError, 'no text'):
            chat.complete(self.model, [dict(role='you', text='hi')])
        self.server.code = 401; self.server.payload = b'sensitive-body'
        with self.assertRaisesRegex(RuntimeError, 'HTTP 401') as caught:
            chat.complete(self.model, [dict(role='you', text='hi')])
        self.assertNotIn('sensitive-body', str(caught.exception))

    def test_missing_key_does_not_make_a_request(self):
        with self.assertRaisesRegex(RuntimeError, 'Missing GEPPETTO_TEST_MISSING_KEY'):
            chat.complete(dict(self.model, key_env='GEPPETTO_TEST_MISSING_KEY'), [dict(role='you', text='hi')])
        self.assertIsNone(self.server.body)

    def test_cancelled_request_never_contacts_provider(self):
        cancel = Event()
        cancel.set()
        self.assertEqual(list(chat.stream(self.model, [dict(role='you', text='hi')], cancel)), [])
        self.assertIsNone(self.server.body)

    def test_cancel_during_buffered_reply_suppresses_answer(self):
        cancel = Event()
        original = chat.json.load

        def load(response):
            result = original(response)
            cancel.set()
            return result

        with patch.object(chat.json, 'load', side_effect=load):
            self.assertEqual(list(chat.stream(self.model, [dict(role='you', text='hi')], cancel)), [])


class HostTests(unittest.TestCase):
    def setUp(self):
        from PySide6.QtGui import QGuiApplication
        import host
        self.module = host
        self.app = QGuiApplication.instance() or QGuiApplication([])
        self.host = host.Host()
        self.host._preferences_path = '/nonexistent/geppetto-test/preferences.json'
        self.host._models_done([fixture(), fixture(id='cloud', kind='anthropic', model='cloud')])
        self.host._chat_reply = lambda *args: None

    def tearDown(self):
        self.host.stopChat(); self.host.runTimer.stop()
        self.host.deleteLater()

    def test_reply_stream_error_and_stale_completion(self):
        self.host.sendChat('hello')
        request = self.host._chat_request
        self.host._chat_chunk(request, 'hello back')
        self.assertEqual(self.host.chatMessages[-1]['text'], 'hello back')
        self.host._chat_done(request, '', 'failed')
        self.assertFalse(self.host.chatBusy)
        self.assertEqual(self.host.chatMessages[-1], dict(role='error', text='failed'))
        self.host.sendChat('again')
        self.host.clearChat()
        self.host._chat_done(self.host._chat_request - 1, 'stale', '')
        self.assertEqual(self.host.chatMessages, [])

    def test_pictures_attach_and_send_with_the_message(self):
        from PySide6.QtGui import QImage
        with tempfile.TemporaryDirectory() as d:
            src = Path(d) / 'shot.png'
            self.assertTrue(QImage(4, 4, QImage.Format_RGB32).save(str(src)))
            self.assertFalse(self.host.attachChatImage(str(Path(d) / 'missing.png')))
            self.assertTrue(self.host.attachChatImage(src.as_uri()))
            self.assertEqual(len(self.host.chatImages), 1)
            self.host.sendChat('')
            sent = self.host.chatMessages[0]
            self.assertEqual(sent['text'], 'What is in this picture?')
            self.assertTrue(sent['images'][0].endswith('.jpg'))
            self.assertEqual(self.host.chatImages, [])
            self.host._chat_busy = False
            note = Path(d) / 'note.txt'
            note.write_text('no')
            self.assertFalse(self.host.attachChatImage(str(note)))

    def test_independent_selectors_and_invalid_selections(self):
        self.host.setChatModel('cloud')
        self.assertEqual(self.host.chatModel, 'cloud')
        self.assertEqual(self.host.aiModel, 'local')
        self.host.setAiModel('cloud')
        self.assertEqual(self.host.aiModel, 'cloud')
        self.host.setAiModel('missing')
        self.assertEqual(self.host.aiModel, 'cloud')

    def test_real_worker_delivers_on_ui_thread(self):
        self.host._chat_reply = self.module.Host._chat_reply.__get__(self.host)
        with patch.object(chat, 'stream', return_value=iter(['first ', 'second'])):
            self.host.sendChat('hello')
            until = time.monotonic() + 2
            while self.host.chatBusy and time.monotonic() < until:
                self.app.processEvents(); time.sleep(.01)
        self.assertFalse(self.host.chatBusy)
        self.assertEqual(self.host.chatMessages[-1]['text'], 'first second')

    def test_research_process_gets_selected_connection_and_private_environment(self):
        # Rewritten 2026-10-23 (audit G2 coverage): the previous version of this test
        # asserted the OLD QProcess spawn (setArguments / setProcessEnvironment /
        # p.start) and failed identically on the unpatched tree - beginResearch no
        # longer uses QProcess at all. It spawns the research child with
        # subprocess.Popen in its own session (so restarting the chest does not kill
        # a run), passing the connection as an argv list and the environment as a
        # plain dict built by chatlib.research_env (round-1 G2 fix). The assertions
        # below pin THAT contract: selected provider/model in argv, the key's VALUE
        # only in the env dict (never in argv), no other provider's key in the env,
        # and the run's .json/.log files created private (audit G6).
        from unittest.mock import MagicMock
        self.host._chat_models = [fixture(id='cloud', kind='anthropic', key_env='TEST_KEY')]
        self.host.ai_model = 'cloud'
        with tempfile.TemporaryDirectory() as d, patch.object(self.module, 'RUNS', d), \
                patch('subprocess.Popen') as popen, \
                patch.dict(os.environ, {'TEST_KEY': 'private', 'OTHER_API_KEY': 'other-secret'}):
            popen.return_value.pid = 4242
            self.assertTrue(self.host.startResearch('make a block'))
            popen.assert_called_once()
            args, kwargs = popen.call_args
            argv = args[0]
            self.assertEqual(argv[1:3], ['--ai', 'research'])
            self.assertEqual(argv[argv.index('--provider') + 1], 'anthropic')
            self.assertEqual(argv[argv.index('--model') + 1], 'test-model')
            self.assertIn('--key-env', argv)
            self.assertNotIn('private', argv)
            env = kwargs['env']
            self.assertEqual(env['TEST_KEY'], 'private')
            self.assertNotIn('OTHER_API_KEY', env)
            self.assertNotIn('other-secret', env.values())
            self.assertTrue(kwargs['start_new_session'])
            metas = [n for n in os.listdir(d) if n.endswith('.json')]
            self.assertEqual(len(metas), 1)
            meta = json.load(open(os.path.join(d, metas[0])))
            self.assertEqual(meta['pid'], 4242)
            self.assertEqual(meta['prompt'], 'make a block')
            for name in metas + [n for n in os.listdir(d) if n.endswith('.log')]:
                self.assertEqual(os.stat(os.path.join(d, name)).st_mode & 0o777, 0o600, name)
        self.host.jobs.clear()


if __name__ == '__main__':
    unittest.main()
