"""The live part glow and camera: each step's part glows and the camera comes in on it (rate-limited)."""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "cabinet"))
from part_focus import PartFocus, part_of, MOVE_EVERY  # noqa: E402


class Clock:
    def __init__(self):
        self.t = 0.0

    def __call__(self):
        return self.t


class PartOf(unittest.TestCase):
    def test_ops(self):
        self.assertEqual(part_of("line 6: add Blob name=Sole shapes=\"box 0,0,0 1,1,1\""), ("add", "Sole"))
        self.assertEqual(part_of('line 3: set Head shapes="ellipsoid 0,0,0 1,1,1"'), ("set", "Head"))
        self.assertEqual(part_of('line 2: add Import name="Boots" source=/x.fbx'), ("add", "Boots"))
        self.assertEqual(part_of('line 9: add Stitching name=Welt "bodies=Suit / sole_L,Suit / sole_R" gap=1'),
                         ("add", "Suit / sole_L,Suit / sole_R"))
        for op in ("line 2: add Import source=/x.fbx", "line 1: material suit /a.vmat", "refimage front a.png",
                   "initial", "start: x.fbx", "", "line 4: remove Head"):
            self.assertIsNone(part_of(op), op)


class Focus(unittest.TestCase):
    def setUp(self):
        self.sent, self.clock = [], Clock()
        self.f = PartFocus(self.sent.append, self.clock)

    def test_a_hundred_quick_adds_move_the_camera_at_most_every_few_seconds(self):
        for i in range(100):
            self.f.step("line %d: add Blob name=Ball%d shapes=x" % (i, i))
            self.clock.t += .1
        moves = [c for c in self.sent if c.startswith("framebody")]
        self.assertIn(len(moves), (int(10 / MOVE_EVERY), int(10 / MOVE_EVERY) + 1))
        self.assertEqual(moves[0], "framebody Ball0")

    def test_edit_glides_in_and_a_material_keeps_the_view(self):
        self.f.step("line 1: set Head shapes=x")
        self.assertEqual(self.sent, ["focus Head", "framebody Head", "holdframe 1"])
        self.sent.clear()
        self.f.step("line 2: material skin a.vmat")
        self.assertEqual(self.sent, [])

    def test_a_detail_frames_the_bodies_it_goes_on(self):
        self.f.step('line 4: add Stitching name=Welt "bodies=Suit / boot_L,Suit / boot_R"')
        self.assertEqual(self.sent, ["focus Suit / boot_L,Suit / boot_R", "framebody Suit / boot_L,Suit / boot_R",
                                     "holdframe 1"])

    def test_edits_close_together_move_once(self):
        self.f.step("line 1: set Head shapes=x")
        self.clock.t += 1
        self.sent.clear()
        self.f.step("line 2: set Neck shapes=x")
        self.assertEqual(self.sent, ["focus Neck", "holdframe 1"])        # glows, camera stays on Head
        self.clock.t += MOVE_EVERY
        self.sent.clear()
        self.f.step("line 3: set Neck shapes=y")
        self.assertEqual(self.sent, ["framebody Neck", "holdframe 1"])

    def test_material_keeps_glow_unnamed_add_clears_it(self):
        self.f.step("line 1: add Blob name=Sole shapes=x")
        self.f.step("line 2: material leather a.vmat")
        glows = lambda: [c for c in self.sent if c.startswith("focus")]
        self.assertEqual(glows(), ["focus Sole"])
        self.f.step("line 3: add Import source=/x.fbx")
        self.assertEqual(glows(), ["focus Sole", "focus"])

    def test_camera_off_still_glows(self):
        f = PartFocus(self.sent.append, self.clock, enabled=False)
        f.step("line 1: set Head shapes=x")
        self.assertEqual(self.sent, ["focus Head"])


class Follow(unittest.TestCase):
    def test_reads_the_run_journal_and_skips_replay(self):
        import json, os, tempfile
        import part_focus
        root = tempfile.mkdtemp()
        os.makedirs(os.path.join(root, ".live"))
        with open(os.path.join(root, ".live", "events.jsonl"), "w") as stream:
            for rev, op in ((1, "initial"), (2, "line 1: set Head shapes=x")):
                stream.write(json.dumps({"state": "revision", "operation": op, "snapshot": {"revision": rev}}) + "\n")

        class Pane:
            def __init__(self):
                self.sent = []
                self.cmd = self.sent.append
        pane = Pane()
        part_focus.follow(pane, root, 2, {"shown": 2})
        self.assertEqual(pane.sent, [])
        part_focus.follow(pane, root, 2, {})
        self.assertEqual(pane.sent, ["focus Head", "framebody Head", "holdframe 1"])


if __name__ == "__main__":
    unittest.main()
