"""Approved corrections must not be erased by read receipts or draw-over annotations."""
import json
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from tests import test_live_runs  # establishes the cabinet import path
import live_runs


class NoteAtomicityTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(dir='/tmp/opencode')
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)

    def test_append_during_receipt_is_never_lost(self):
        first = live_runs.add_note(str(self.root), 1, '', 'first approved batch')
        append = live_runs._append_note_record
        def interleave(path, record):
            if path.endswith('notes-read.jsonl'):
                live_runs.add_note(str(self.root), 1, '', 'second approved batch')
            append(path, record)
        with patch.object(live_runs, '_append_note_record', side_effect=interleave):
            live_runs.mark_read([dict(first, dir=str(self.root))])
        rows = live_runs.notes(str(self.root))
        self.assertEqual([r['text'] for r in rows], ['first approved batch', 'second approved batch'])
        self.assertTrue(rows[0]['read'])
        self.assertFalse(rows[1]['read'])

    def test_same_text_and_timestamp_are_distinct_new_messages(self):
        a = live_runs.add_note(str(self.root), 1, '', 'same text')
        b = live_runs.add_note(str(self.root), 1, '', 'same text')
        self.assertNotEqual(a['id'], b['id'])
        live_runs.mark_read([dict(a, dir=str(self.root))])
        self.assertEqual([n['read'] for n in live_runs.notes(str(self.root))], [True, False])

    def test_legacy_checkpoint_note_receipts_do_not_rewrite_source(self):
        note = dict(time='fixture', rev=3, text='legacy checkpoint', read=False)
        path = self.root / 'notes.jsonl'
        path.write_text(json.dumps(note) + '\n')
        original = path.read_bytes()
        live_runs.mark_read([dict(note, dir=str(self.root))])
        self.assertTrue(live_runs.notes(str(self.root))[0]['read'])
        self.assertEqual(path.read_bytes(), original)

    def test_concurrent_append_and_consume_preserve_every_message(self):
        def write(i):
            note = live_runs.add_note(str(self.root), 1, '', str(i))
            if i % 2 == 0:
                live_runs.mark_read([dict(note, dir=str(self.root))])
        with ThreadPoolExecutor(max_workers=8) as pool:
            list(pool.map(write, range(80)))
        rows = live_runs.notes(str(self.root))
        self.assertEqual(len(rows), 80)
        self.assertEqual(len({r['id'] for r in rows}), 80)
        self.assertEqual(sum(bool(r['read']) for r in rows), 40)

    def test_draw_over_never_replaces_the_note_log(self):
        note = live_runs.add_note(str(self.root), 1, '', 'draw here')
        path = self.root / 'notes.jsonl'
        original = path.read_bytes()
        hit = dict(body='Body', station_s=.5, world_pos=[0, 0, 0], normal=[0, 0, 1])
        with patch.object(live_runs.os, 'replace', side_effect=AssertionError('must not rewrite notes')):
            live_runs.record_note_stroke(str(self.root), note, [hit])
        self.assertEqual(path.read_bytes(), original)
        self.assertEqual(live_runs.notes(str(self.root))[0]['stroke'], [hit])


if __name__ == '__main__':
    unittest.main()
