"""Audit C2 (staging): ClaudeLink's locked-down spawn env — implemented, OFF by default.

Nothing here touches the live card: the spawn is faked, and the default behaviour is
asserted unchanged (full env minus CLAUDE_CODE_* names).
"""
import os
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'cabinet'))
import claude_link


class FakeProc:
    stdin = None
    stdout = []
    stderr = []

    def poll(self):
        return None


def capturing_spawn(box):
    def spawn(args, **kw):
        box['args'] = args
        box['env'] = kw.get('env')
        return FakeProc()
    return spawn


FULL = {'PATH': '/bin', 'HOME': '/home/x', 'OPENAI_API_KEY': 'no', 'ANTHROPIC_API_KEY': 'no',
        'TEACHER_API_KEY': 'no', 'CLAUDE_CODE_SESSION_ID': 'parent', 'SSH_AUTH_SOCK': '/sock'}


class LockdownTests(unittest.TestCase):
    def test_locked_down_env_is_the_allowlist_only(self):
        env = claude_link.locked_down_env(FULL)
        self.assertEqual(env.get('PATH'), '/bin')
        self.assertEqual(env.get('SSH_AUTH_SOCK'), '/sock')
        for name in ('OPENAI_API_KEY', 'ANTHROPIC_API_KEY', 'TEACHER_API_KEY', 'CLAUDE_CODE_SESSION_ID'):
            self.assertNotIn(name, env)

    def test_lockdown_is_off_by_default_and_follows_the_flag(self):
        with patch.dict(os.environ, {}, clear=True):
            self.assertFalse(claude_link.ClaudeLink(state_path='/no/state').lockdown)
        with patch.dict(os.environ, {claude_link.LOCKDOWN_FLAG: '1'}, clear=True):
            self.assertTrue(claude_link.ClaudeLink(state_path='/no/state').lockdown)
        self.assertTrue(claude_link.ClaudeLink(state_path='/no/state', lockdown=True).lockdown)

    def test_spawn_env_only_scrubbed_when_lockdown_is_on(self):
        with patch.dict(os.environ, FULL, clear=True):
            box = {}
            link = claude_link.ClaudeLink(spawn=capturing_spawn(box), state_path='/no/state', lockdown=True)
            link._start()
            self.assertNotIn('OPENAI_API_KEY', box['env'])
            self.assertNotIn('CLAUDE_CODE_SESSION_ID', box['env'])
            box2 = {}
            link2 = claude_link.ClaudeLink(spawn=capturing_spawn(box2), state_path='/no/state', lockdown=False)
            link2._start()
            self.assertEqual(box2['env'].get('OPENAI_API_KEY'), 'no')   # today's behaviour, unchanged
            self.assertNotIn('CLAUDE_CODE_SESSION_ID', box2['env'])


if __name__ == '__main__':
    unittest.main()
