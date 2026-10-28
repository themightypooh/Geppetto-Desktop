"""Exercise real receipt recording/consumption across messages; no providers or GUI."""
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "cabinet"))
import build_execution as execution


class FollowupReceiptTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="geppetto-followup-")
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.runs = self.base / "runs"
        self.root = self.runs / "script-existing"
        (self.root / ".live/answers").mkdir(parents=True)
        (self.root / ".live/revisions").mkdir()
        (self.root / ".live/events.jsonl").write_text(
            json.dumps({"timestamp": "2020-01-01T00:00:00Z"}) + "\n")
        (self.root / "script.txt").write_text("add Primitive name=Base\n")

    def turn(self):
        return execution.BuildTurn(runs_root=self.runs, storage=self.base / "turns")

    def advance(self, revision, name="edit"):
        relative = ".live/revisions/%08d-model.effigy" % revision
        (self.root / relative).write_text("fixture revision %d" % revision)
        (self.root / ".live/latest.json").write_text(json.dumps({
            "schemaVersion": 1, "ev": "ai-live", "runId": "existing",
            "sequence": revision, "state": "open",
            "snapshot": {"revision": revision, "path": relative},
        }))
        answer = self.root / ".live/answers" / (name + ".txt")
        answer.write_text("run is open\n")
        return answer

    def record(self, turn, operation="more", before=2, revision=3):
        self.advance(revision)
        with patch.dict(os.environ, turn.environment()):
            execution.record(self.root, "edit", operation, before)

    def test_second_third_and_rewind_messages_continue_same_run(self):
        for before, operation in ((2, "more"), (3, "more"), (4, "rewind")):
            with self.subTest(operation=operation, revision=before + 1):
                turn = self.turn()
                self.record(turn, operation, before, before + 1)
                receipt = turn.receipt()
                self.assertIsNotNone(receipt)
                self.assertEqual(receipt["revision"], before + 1)
                self.assertEqual(receipt["runId"], "existing")
                self.assertIsNone(turn.receipt(), "receipt must not replay")
                turn.cleanup()

    def test_new_build_cannot_claim_old_run(self):
        with self.assertRaisesRegex(execution.ExecutionMissing, "opened before"):
            self.record(self.turn(), operation="build")

    def test_reader_accepts_unbound_followup_receipt(self):
        turn = self.turn()
        self.record(turn)
        (turn.directory / "binding.json").unlink()
        self.assertIsNotNone(turn.receipt())

    def test_reader_rejects_unbound_old_build_receipt(self):
        turn = self.turn()
        self.record(turn)
        (turn.directory / "binding.json").unlink()
        path = next(turn.directory.glob("*.receipt.json"))
        item = json.loads(path.read_text())
        item["operation"] = "build"
        path.write_text(json.dumps(item))
        self.assertIsNone(turn.receipt())

    def test_reader_still_enforces_bound_run(self):
        turn = self.turn()
        self.record(turn)
        (turn.directory / "binding.json").write_text(json.dumps({"runId": "other"}))
        self.assertIsNone(turn.receipt())

    def test_stale_answer_still_rejected(self):
        turn = self.turn()
        answer = self.advance(3)
        os.utime(answer, ns=(turn.started_ns - 1_000_000_000,) * 2)
        with patch.dict(os.environ, turn.environment()):
            with self.assertRaisesRegex(execution.ExecutionMissing, "predates"):
                execution.record(self.root, "edit", "more", 2)

    def test_unchanged_revision_still_rejected(self):
        with self.assertRaisesRegex(execution.ExecutionMissing, "without a changed"):
            self.record(self.turn(), before=3, revision=3)

    def test_explicit_wrong_run_still_rejected(self):
        turn = self.turn()
        turn.allow_run("another-run")
        with self.assertRaisesRegex(execution.ExecutionMissing, "explicitly allowed"):
            self.record(turn)

    def test_previous_message_receipt_cannot_be_reused(self):
        first = self.turn()
        self.record(first)
        second = self.turn()
        for path in first.directory.glob("*.receipt.json"):
            (second.directory / path.name).write_bytes(path.read_bytes())
        self.assertIsNone(second.receipt())

    def test_changed_snapshot_still_rejected(self):
        turn = self.turn()
        self.record(turn)
        (self.root / ".live/revisions/00000003-model.effigy").write_text("changed")
        self.assertIsNone(turn.receipt())

    def test_checked_stream_accepts_followup_without_retry(self):
        turn = self.turn()
        calls = []

        def invoke(history):
            calls.append(history)
            self.advance(3)
            # Providers receive the active turn through child_environment().
            with patch.dict(os.environ, execution.child_environment({})):
                execution.record(self.root, "edit", "more", 2)
            yield "Saved revision 3."

        output = list(execution.checked_stream(
            {"kind": "grok-cli", "capabilities": ["tools"]},
            [{"role": "you", "text": "change it"}], invoke,
            turn_factory=lambda: turn))
        self.assertEqual(output, ["Saved revision 3."])
        self.assertEqual(len(calls), 1)
        self.assertFalse(turn.directory.exists())


if __name__ == "__main__":
    unittest.main()
