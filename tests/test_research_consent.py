"""Per-chat explicit opt-in and provider-wire policy tests. No real model calls."""
import itertools
import json
import os
import threading
import tempfile
from pathlib import Path
import unittest
import urllib.request
from unittest.mock import patch

from tests import test_claude_chest as fixture
import chat
import research_consent


class PolicyTests(unittest.TestCase):
    def test_all_eight_combinations_and_unknown_licence_are_explicit(self):
        for papers, photos, models in itertools.product((False, True), repeat=3):
            choices = dict(papers=papers, photos=photos, models=models)
            policy = research_consent.policy(choices)
            if any(choices.values()):
                allowed = policy.split('Allowed categories: ', 1)[1].split('.', 1)[0]
                for key, label in [('papers', 'research papers'), ('photos', 'reference photos'), ('models', 'reference models')]:
                    self.assertEqual(label in allowed, choices[key])
            else:
                self.assertIn('No asset research is allowed', policy)
            self.assertIn('Unknown or restricted licence means do not download or reuse', policy)
            self.assertIn('separate explicit approval', policy)
        self.assertIn('has not been granted', research_consent.policy(None))

    def test_policy_reaches_each_http_provider_system_field(self):
        policy = research_consent.policy(dict(papers=True, photos=False, models=False))
        for kind in ('openai', 'anthropic', 'google', 'ollama'):
            model = dict(kind=kind, model='own-model', research_policy=policy)
            body = chat._body(model, [dict(role='you', text='design an asset')])
            self.assertIn(policy, json.dumps(body))
            self.assertEqual(body['model'] if kind != 'google' else model['model'], 'own-model')
        self.assertIn(policy, chat._chat_system(model))

    def test_provider_options_cannot_override_selection_or_research_policy(self):
        policy = research_consent.policy(dict(papers=False, photos=False, models=False))
        model = dict(kind='openai', model='selected-model', research_policy=policy,
                     extra_body=dict(model='wrong-model', messages=[], temperature=.2))
        body = chat._body(model, [dict(role='you', text='hi')])
        self.assertEqual(body['model'], 'selected-model')
        self.assertIn(policy, body['messages'][0]['content'])
        self.assertEqual(body['temperature'], .2)

    def test_selected_http_provider_alone_receives_its_key_and_choices(self):
        policy = research_consent.policy(dict(papers=True, photos=False, models=False))
        for kind in ('openai', 'anthropic', 'google', 'ollama'):
            model = dict(kind=kind, id='selected', label='Selected', model='chosen-model',
                         endpoint='http://127.0.0.1:9/' if kind == 'ollama' else 'https://api.example.com/v1/',
                         research_policy=policy, key_env='SELECTED_TEST_KEY' if kind != 'ollama' else '')
            with patch.dict(os.environ, {'SELECTED_TEST_KEY': 'selected-fake-secret', 'OTHER_PROVIDER_KEY': 'must-not-leak'}):
                req = chat._request(model, [dict(role='you', text='hello')], False)
            self.assertNotIn('must-not-leak', str(req.header_items()))
            body = json.loads(req.data)
            system = (body['system'] if kind == 'anthropic' else
                      body['systemInstruction']['parts'][0]['text'] if kind == 'google' else
                      body['messages'][0]['content'])
            self.assertIn(policy, system)
            if kind == 'google':
                self.assertIn('/models/chosen-model:generateContent', req.full_url)
            else:
                self.assertEqual(json.loads(req.data)['model'], 'chosen-model')

    def test_codex_and_opencode_shims_receive_choices_via_private_stdin(self):
        import roundtable_catalog
        with tempfile.TemporaryDirectory(dir='/tmp/opencode') as tmp:
            shim = Path(tmp) / 'shim.py'
            shim.write_text('''import sys, json
text = sys.stdin.read()
assert 'Allowed categories: research papers, reference models.' in text
assert '--model' in sys.argv and sys.argv[sys.argv.index('--model')+1] == 'chosen-model'
assert 'ASSET RESEARCH CONSENT' not in ' '.join(sys.argv)
print(json.dumps({'type':'result', 'result':'policy received'}))
''')
            policy = research_consent.policy(dict(papers=True, photos=False, models=True))
            for family in ('codex', 'opencode'):
                model = dict(kind='roundtable-cli', family=family, model='chosen-model',
                             label=family, research_policy=policy)
                with patch.object(roundtable_catalog, 'NATIVE_SHIM', shim), patch.object(chat, 'CLAUDE_HOME', tmp):
                    self.assertEqual(''.join(chat.stream(model, [dict(role='you', text='hello')])), 'policy received')

    def test_provider_redirect_cannot_leak_key_to_another_origin(self):
        handler = chat.ProviderRedirectHandler()
        req = urllib.request.Request('https://api.example.com/v1/messages',
                                     headers={'X-api-key': 'fake-secret'})
        for target in ('https://other.example.com/x', 'http://api.example.com/x',
                       'https://api.example.com:444/x'):
            with self.assertRaisesRegex(RuntimeError, 'different origin'):
                handler.redirect_request(req, None, 302, '', {}, target)
        safe = handler.redirect_request(req, None, 302, '', {}, 'https://api.example.com/next')
        self.assertEqual(safe.full_url, 'https://api.example.com/next')

    def test_release_workspace_uses_current_users_data_not_installation(self):
        import chat_workspace
        with patch.object(chat_workspace, '__file__', '/opt/geppetto/cabinet/chat_workspace.py'), \
             patch.dict(os.environ, {'HOME': '/home/new-user', 'XDG_DATA_HOME': '/home/new-user/.local/share'}, clear=True):
            self.assertEqual(chat_workspace.workspace(), '/home/new-user/.local/share/geppetto/workspace')

    def test_native_claude_policy_is_in_actual_cli_system_prompt(self):
        import tempfile
        from pathlib import Path
        import claude_link
        with tempfile.TemporaryDirectory(dir='/tmp/opencode') as tmp:
            link = claude_link.ClaudeLink(spawn=lambda args, **kw: fixture.FakeProc(args, **kw),
                                          state_path=str(Path(tmp) / 'state.json'))
            policy = research_consent.policy(dict(papers=False, photos=True, models=True))
            link.set_research_policy(policy)
            link.send('make an asset')
            args = link.proc.args
            self.assertIn(policy, args[args.index('--append-system-prompt') + 1])
            link.close()


class ConsentCardTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        fixture.CardTests.setUpClass()
        cls.ui = fixture.CardTests()

    @classmethod
    def tearDownClass(cls):
        fixture.CardTests.tearDownClass()

    def test_first_send_stays_local_choices_follow_selection_and_reset(self):
        import host
        h = self.ui.host
        ui = self.ui
        self.assertTrue(h.claude.lockdown, 'production card must isolate unrelated provider environment keys')
        h.claude._set('idle')
        h.claudeNew()
        h._catalogue_busy = True  # this fixture owns its catalogue; no real discovery race
        ui.root.setProperty('claudeOpen', True)
        ui.find('claudeInput').setProperty('text', 'make an asset')
        self.assertFalse(h.claudeSend('make an asset'))
        ui.settle(.15)
        self.assertTrue(ui.find('claudeResearchConsent').property('visible'))
        self.assertEqual(ui.find('claudeInput').property('text'), 'make an asset')
        for name in ('researchPapers', 'researchPhotos', 'researchModels'):
            self.assertFalse(ui.find(name).property('checked'))
        snap = os.environ.get('GEPPETTO_RESEARCH_SNAP')
        if snap:
            ui.settle(1)  # software rendering at high DPI may need more than one animation frame
            ui.find('claudeWin').grabWindow().save(snap)
        ui.find('researchPapers').setProperty('checked', True)
        ui.find('researchModels').setProperty('checked', True)
        models = [dict(id='test/' + family, label=family, kind=kind, model='own-model',
                       family=family, available=True, endpoint='cli')
                  for family, kind in [('opencode', 'roundtable-cli'), ('codex', 'roundtable-cli'),
                                       ('grok', 'grok-cli'), ('local', 'ollama'), ('api', 'openai')]]
        h._chat_models = models
        h.setClaudeModel(models[0]['id'])
        requests = []
        called = threading.Event()
        def stream(model, *args, **kwargs):
            requests.append(model)
            called.set()
            yield 'reference policy received'
        with patch.object(host.chatlib, 'stream', side_effect=stream), \
             patch.object(host.chatlib, 'effigy_reference', return_value=''):
            ui.click('researchApply')
            self.assertTrue(called.wait(3))
            ui.settle(.2)
            self.assertEqual(h.claudeResearch, dict(papers=True, photos=False, models=True))
            self.assertFalse(h.claudeResearchPrompt)
            self.assertEqual(ui.find('claudeInput').property('text'), '')
            for record in models[1:]:
                called.clear()
                h.setClaudeModel(record['id'])
                self.assertTrue(h.claudeSend('continue'))
                self.assertTrue(called.wait(3))
                ui.settle(.15)
            self.assertEqual([r['id'] for r in requests], [r['id'] for r in models])
            for request in requests:
                self.assertIn('Allowed categories: research papers, reference models.', request['research_policy'])
            self.assertTrue(all('research_policy' not in r for r in models))
        h.claudeNew()
        self.assertEqual(h.claudeResearch, {})
        self.assertFalse(h.claudeSend('new task'))
        ui.settle(.1)
        self.assertTrue(h.claudeResearchPrompt)
        h.setClaudeResearch(False, False, False)
        self.assertIn('No asset research', h.claude.research_policy)


if __name__ == '__main__':
    unittest.main()
