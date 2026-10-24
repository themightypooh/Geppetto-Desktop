"""Audit G5/G6/G7 fixes that live in the Qt-free cabinet modules (claude_feed,
claude_gate, live_runs) - the host.py halves (chat-image cleanup, open_private)
need PySide6 and are covered by HostTests in test_chat.py on a Qt box."""
import json
import os
import sys
import tempfile
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'cabinet'))
import claude_feed
import claude_gate
import live_runs


def payload(session, path='/tmp/pic.png'):
    return {'session_id': session, 'hook_event_name': 'PreToolUse',
            'tool_name': 'Read', 'tool_input': {'file_path': path}, 'cwd': ''}


class FeedScopeTests(unittest.TestCase):
    """G7: note() records Geppetto sessions only, at write time."""

    def setUp(self):
        d = tempfile.TemporaryDirectory()
        self.addCleanup(d.cleanup)
        self.feed = os.path.join(d.name, 'feed')
        self.runs = os.path.join(d.name, 'runs')
        self.state = os.path.join(d.name, 'claude-chat.json')
        os.makedirs(self.runs)
        Path(self.state).write_text(json.dumps({'session': 'card-sess'}))
        Path(self.runs, '123.json').write_text(json.dumps({'session': 'run-sess'}))

    def note(self, session):
        claude_feed.note(payload(session), feed=self.feed, runs=self.runs, state=self.state)

    def files(self):
        return sorted(os.listdir(self.feed)) if os.path.isdir(self.feed) else []

    def test_foreign_session_records_nothing(self):
        self.note('someone-elses-session')
        self.assertEqual(self.files(), [])

    def test_card_and_run_sessions_are_recorded(self):
        self.note('card-sess')
        self.note('run-sess')
        self.assertEqual(self.files(), ['card-sess.jsonl', 'run-sess.jsonl'])
        lines = Path(self.feed, 'card-sess.jsonl').read_text().splitlines()
        self.assertEqual(json.loads(lines[0])['path'], '/tmp/pic.png')

    def test_feed_files_and_folder_are_private(self):
        self.note('card-sess')
        self.assertEqual(os.stat(self.feed).st_mode & 0o777, 0o700)
        self.assertEqual(os.stat(os.path.join(self.feed, 'card-sess.jsonl')).st_mode & 0o777, 0o600)

    def test_prune_drops_old_feed_files_only(self):
        self.note('card-sess')
        old = os.path.join(self.feed, 'run-sess.jsonl')
        Path(old).write_text('{}\n')
        past = time.time() - 31 * 86400
        os.utime(old, (past, past))
        claude_feed.prune(self.feed)
        self.assertEqual(self.files(), ['card-sess.jsonl'])


class PrivateCreationTests(unittest.TestCase):
    """G6: files/dirs these modules create land 0600/0700, not umask defaults."""

    def test_gate_write_is_private_and_roundtrips(self):
        with tempfile.TemporaryDirectory() as d:
            gates = os.path.join(d, 'claude-gate')
            with _patched_gates(gates):
                claude_gate.set_gate('sess1', 'paused', 'test')
                path = os.path.join(gates, 'sess1.json')
                self.assertEqual(os.stat(path).st_mode & 0o777, 0o600)
                self.assertEqual(os.stat(gates).st_mode & 0o777, 0o700)
                self.assertEqual(claude_gate.gate('sess1')['state'], 'paused')

    def test_live_runs_private_makedirs(self):
        with tempfile.TemporaryDirectory() as d:
            target = os.path.join(d, 'run', '.live', 'inbox')
            live_runs.makedirs_private(target)
            self.assertEqual(os.stat(target).st_mode & 0o777, 0o700)


class _patched_gates:
    """claude_gate builds gate paths from its module-level GATES at call time."""

    def __init__(self, gates):
        self.gates = gates

    def __enter__(self):
        self.old = claude_gate.GATES
        claude_gate.GATES = self.gates

    def __exit__(self, *exc):
        claude_gate.GATES = self.old


if __name__ == '__main__':
    unittest.main()
