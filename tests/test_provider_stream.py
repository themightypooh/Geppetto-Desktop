"""Exercise the embedded providers' real subprocess transport without API usage."""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location('provider_stream', Path(__file__).parents[1] / 'cabinet/provider_stream.py')
stream = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(stream)


class ProviderStreamTests(unittest.TestCase):
    def fake(self, source):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        exe = Path(directory.name) / 'fake-provider'
        exe.write_text('#!/usr/bin/env python3\n' + source)
        exe.chmod(0o700)
        return str(exe)

    def codex_fake(self, events):
        return self.fake('''import sys,json
for raw in sys.stdin:
 e=json.loads(raw)
 if e.get('id')==0: print(json.dumps({'id':0,'result':{}}),flush=True)
 elif e.get('id')==1:
  assert e['params']['ephemeral'] is True
  assert e['params']['approvalPolicy']=='never'
  assert e['params']['sandbox']=='read-only'
  print(json.dumps({'id':1,'result':{'thread':{'id':'test'}}}),flush=True)
 elif e.get('id')==2:
  for value in EVENTS: print(json.dumps(value),flush=True)
'''.replace('EVENTS', repr(events)))

    def test_live_deltas_no_duplicate_final_and_no_reasoning_payload(self):
        exe = self.codex_fake([
            {'method':'item/reasoning/summaryTextDelta','params':{'delta':'private reasoning'}},
            {'method':'item/agentMessage/delta','params':{'itemId':'a','delta':'```effigy\n'}},
            {'method':'item/agentMessage/delta','params':{'itemId':'a','delta':'echo test\n```'}},
            {'method':'item/completed','params':{'item':{'id':'a','type':'agentMessage','text':'```effigy\necho test\n```'}}},
            {'method':'turn/completed','params':{'turn':{'status':'completed'}}}])
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            reply = stream.codex(exe, 'test', 'make model')
        self.assertEqual(reply, '```effigy\necho test\n```')
        self.assertNotIn('private reasoning', out.getvalue())
        events = [json.loads(x) for x in out.getvalue().splitlines()]
        self.assertEqual(sum(bool(x['event']['delta'].get('text')) for x in events), 2)

    def test_final_only_reply(self):
        exe = self.codex_fake([
            {'method':'item/completed','params':{'item':{'id':'a','type':'agentMessage','text':'answer'}}},
            {'method':'turn/completed','params':{'turn':{'status':'completed'}}}])
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(stream.codex(exe, '', 'request'), 'answer')

    def test_failed_turn_is_not_success(self):
        exe = self.codex_fake([{'method':'turn/completed','params':{'turn':{'status':'failed','error':{'message':'quota exhausted'}}}}])
        with self.assertRaisesRegex(RuntimeError, 'quota exhausted'):
            stream.codex(exe, '', 'request')

    def test_tool_attempt_stops_turn(self):
        exe = self.codex_fake([{'method':'item/started','params':{'item':{'type':'commandExecution'}}}])
        with self.assertRaisesRegex(RuntimeError, 'attempted commandExecution'):
            stream.codex(exe, '', 'request')

    def test_pending_approval_fails_instead_of_hanging(self):
        exe = self.codex_fake([{'id':52,'method':'item/commandExecution/requestApproval','params':{}}])
        with self.assertRaisesRegex(RuntimeError, 'unsupported interaction'):
            stream.codex(exe, '', 'request')

    def test_broken_stream_is_not_success(self):
        exe = self.fake('import sys\nsys.stderr.write("authentication failed")\n')
        with self.assertRaisesRegex(RuntimeError, 'authentication failed'):
            stream.codex(exe, '', 'request')

    def test_opencode_acp_live_chunks_and_private_profile(self):
        exe = self.fake('''import sys,json,pathlib
assert sys.argv[1:] == ['acp']
c=json.loads(pathlib.Path('opencode.json').read_text())
assert c['agents']['geppetto-native']['permissions'][-1]['effect']=='deny'
assert c['warming'] is False
for raw in sys.stdin:
 e=json.loads(raw);i=e['id']
 if i==0:r={'protocolVersion':1}
 elif i==1:r={'sessionId':'test'}
 elif i==2:r={}
 elif i==4:
  print(json.dumps({'method':'session/update','params':{'update':{'sessionUpdate':'agent_message_chunk','content':{'type':'text','text':'reply'}}}}),flush=True)
  r={'stopReason':'end_turn'}
 else:r={}
 print(json.dumps({'id':i,'result':r}),flush=True)
''')
        out=io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(stream.opencode(exe, 'test/model', 'request'), 'reply')
        self.assertIn('text_delta', out.getvalue())


if __name__ == '__main__':
    unittest.main()
