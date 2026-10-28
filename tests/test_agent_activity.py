"""Observed activity must follow events, never a filename, silence or pace notification."""
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'cabinet'))
import agent_activity as activity

STAMP = '2026-10-25T05:00:00Z'
NOW = activity.timestamp(STAMP)
SESSION = 'abc-123'

class Activity(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name) / 'fish'
        (self.root / '.live').mkdir(parents=True)
        self.log = Path(self.tmp.name) / 'session.jsonl'
        self.log.write_text('')
        Path(str(self.root) + '.json').write_text(json.dumps({'session': SESSION, 'modeller': 'Codex'}))
        activity._files.clear(); activity._sessions.clear()
        self.path_patch = patch.object(activity, 'session_path', return_value=str(self.log))
        self.path_patch.start()

    def tearDown(self):
        self.path_patch.stop(); self.tmp.cleanup()

    def append(self, kind, payload, path=None):
        with open(path or self.log, 'a') as stream:
            stream.write(json.dumps({'timestamp': STAMP, 'type': kind, 'payload': payload}) + '\n')

    def observe(self, now=NOW):
        return activity.agent(str(self.root), now)

    def test_thinking_tools_and_finished_turn(self):
        self.append('event_msg', {'type': 'task_started'})
        self.assertEqual(self.observe()['label'], 'Processing')
        self.append('response_item', {'type': 'reasoning', 'summary': ['PRIVATE text never displayed']})
        self.assertEqual(self.observe()['label'], 'Thinking')
        self.append('response_item', {'type': 'custom_tool_call', 'name': 'exec', 'call_id': 'one',
                                     'input': 'await tools.exec_command({cmd:"private command"})'})
        result = self.observe()
        self.assertEqual(result['label'], 'Running a command')
        self.assertNotIn('private', json.dumps(result))
        self.append('response_item', {'type': 'custom_tool_call_output', 'call_id': 'one'})
        self.assertEqual(self.observe()['label'], 'Processing')
        self.append('event_msg', {'type': 'task_complete'})
        self.assertEqual(self.observe()['label'], 'Waiting for input')

    def test_parallel_tools_wait_for_all_outputs(self):
        for call in ('one', 'two'):
            self.append('response_item', {'type': 'function_call', 'name': 'apply_patch', 'call_id': call})
        self.observe()
        self.append('response_item', {'type': 'function_call_output', 'call_id': 'one'})
        self.assertEqual(self.observe()['label'], 'Editing files')
        self.append('response_item', {'type': 'function_call_output', 'call_id': 'two'})
        self.assertEqual(self.observe()['label'], 'Processing')

    def test_silence_is_not_thinking_and_no_session_is_unknown(self):
        self.assertFalse(self.observe()['known'])
        self.append('response_item', {'type': 'reasoning'})
        self.assertEqual(self.observe()['label'], 'Thinking')
        self.assertEqual(self.observe(NOW + 121)['label'], 'No recent agent signal')
        with patch.object(activity, 'session_path', return_value=''):
            self.assertEqual(self.observe()['label'], 'No agent signal')

    def test_partial_lines_and_truncation(self):
        self.log.write_text('{"type":"response_item","timestamp":"' + STAMP + '","payload":')
        self.assertFalse(self.observe()['known'])
        with self.log.open('a') as stream: stream.write('{"type":"reasoning"}}\n')
        self.assertEqual(self.observe()['label'], 'Thinking')
        self.log.write_text('{}\n')
        self.assertFalse(self.observe()['known'])

    def test_control_events_preserve_render_and_agent_state(self):
        self.append('response_item', {'type': 'reasoning'})
        journal = self.root / '.live/events.jsonl'
        build = {'state': 'building', 'operation': 'line 187: render fish.png views=4', 'timestamp': STAMP}
        control = {'state': 'pace-changed', 'operation': None, 'timestamp': STAMP}
        journal.write_text(json.dumps(build) + '\n' + json.dumps(control) + '\n')
        result = activity.telemetry(str(self.root), control, {'status': 'running'}, NOW)
        self.assertEqual(result['label'], 'Rendering')
        self.assertEqual(result['agent']['label'], 'Thinking')
        self.assertIn('Agent: Thinking', result['detail'])
        self.assertIn('Build: Rendering', result['detail'])
        with journal.open('a') as stream:
            stream.write(json.dumps({'state': 'open', 'timestamp': STAMP}) + '\n')
        self.assertEqual(activity.telemetry(str(self.root), control, {'status': 'running'}, NOW)['label'], 'Thinking')
        self.assertEqual(activity.telemetry(str(self.root), control, {'status': 'running', 'paused': True}, NOW)['label'], 'Paused')

    def test_claude_tool_blocks_and_results(self):
        self.append('assistant', {})  # malformed/empty records are harmless
        with self.log.open('a') as stream:
            stream.write(json.dumps({'type': 'assistant', 'timestamp': STAMP, 'message': {'content': [
                {'type': 'thinking', 'thinking': 'private'}, {'type': 'tool_use', 'name': 'Read', 'id': 't1'}]}}) + '\n')
        self.assertEqual(self.observe()['label'], 'Reading files')
        with self.log.open('a') as stream:
            stream.write(json.dumps({'type': 'user', 'timestamp': STAMP, 'message': {'content': [
                {'type': 'tool_result', 'tool_use_id': 't1'}]}}) + '\n')
        self.assertEqual(self.observe()['label'], 'Processing')

    def test_session_resolution_only_matches_its_id(self):
        self.path_patch.stop()
        session_dir = Path(self.tmp.name) / 'sessions/2026/10/25'
        session_dir.mkdir(parents=True)
        own = session_dir / ('rollout-date-' + SESSION + '.jsonl')
        own.write_text('')
        with patch.object(activity, 'CODEX', str(session_dir.parents[2])):
            self.assertEqual(activity.session_path(SESSION, NOW), str(own))
            self.assertEqual(activity.session_path('../abc', NOW), '')
        self.path_patch.start()

if __name__ == '__main__': unittest.main()
