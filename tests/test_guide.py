"""The guide (cabinet/guide.py): the pitch, pipeline and first-run tutorial as data + logic.

No Qt, no display: the pipeline stages, the tutorial pages and the persisted seen/done state
are plain Python, so they are checked here directly. The QML side is covered in
test_cabinet_qml.py.
"""
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

CABINET = Path(__file__).resolve().parents[1] / "cabinet"
sys.path.insert(0, str(CABINET))

import guide  # noqa: E402


class GuideDataTests(unittest.TestCase):
    def test_the_pipeline_is_the_pitch_in_five_stages(self):
        self.assertEqual([s["id"] for s in guide.PIPELINE],
                         ["describe", "plan", "build", "stepin", "keep"])
        for s in guide.PIPELINE:                     # every stage says what happens and where it shows
            self.assertTrue(s["name"] and s["short"] and s["what"] and s["where"], s)

    def test_the_tutorial_is_four_short_pages(self):
        self.assertEqual([p["id"] for p in guide.PAGES], ["welcome", "pipeline", "try", "controls"])
        for p in guide.PAGES:
            self.assertTrue(p["title"] and p["body"], p)
        welcome = guide.PAGES[0]
        self.assertIn("every step", welcome["body"])     # the pitch itself, on page one

    def test_the_pipeline_page_names_every_stage(self):
        lines = guide.PAGES[1]["lines"]
        self.assertEqual(len(lines), len(guide.PIPELINE))
        for stage, line in zip(guide.PIPELINE, lines):
            self.assertIn(stage["name"].title(), line)

    def test_examples_are_first_builds_ready_to_send(self):
        self.assertGreaterEqual(len(guide.EXAMPLES), 3)
        for e in guide.EXAMPLES:
            self.assertTrue(e["title"] and e["prompt"], e)
            self.assertIn(e["title"], e["prompt"].lower())


class GuideStageTests(unittest.TestCase):
    def test_a_fresh_chest_lights_describe(self):
        states, active = guide.stage_for()
        self.assertEqual(active, 0)
        self.assertEqual(states[0], "active")
        self.assertTrue(all(s == "todo" for s in states[1:]))

    def test_a_live_build_lights_build_with_step_in_ready(self):
        states, active = guide.stage_for(live=True)
        self.assertEqual(states[:2], ["done", "done"])
        self.assertEqual(states[2], "active")
        self.assertEqual(states[3], "ready")         # stepping in is possible *now*: say so
        self.assertEqual(active, 2)

    def test_a_paused_build_lights_step_in(self):
        states, active = guide.stage_for(live=True, paused=True)
        self.assertEqual(states[2], "done")
        self.assertEqual(states[3], "active")
        self.assertEqual(active, 3)

    def test_a_replay_lights_build(self):
        states, active = guide.stage_for(replaying=True, has_runs=True)
        self.assertEqual(states[2], "active")
        self.assertEqual(active, 2)

    def test_kept_models_light_keep(self):
        states, active = guide.stage_for(has_runs=True, has_kept=True)
        self.assertEqual(states[4], "active")
        self.assertEqual(active, 4)

    def test_runs_without_a_build_light_plan(self):
        states, active = guide.stage_for(has_runs=True)
        self.assertEqual(states[1], "active")
        self.assertEqual(active, 1)


class GuideStateTests(unittest.TestCase):
    def test_state_round_trips(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "guide.json")
            state = {"version": guide.VERSION, "seen": True, "done": True, "page": 2}
            self.assertTrue(guide.save_state(path, state))
            self.assertEqual(json.load(open(path))["page"], 2)
            back = guide.load_state(path)
            self.assertTrue(back["seen"] and back["done"])
            self.assertEqual(back["page"], 2)

    def test_a_missing_or_broken_file_means_never_seen(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "guide.json")
            self.assertFalse(guide.load_state(path)["seen"])
            with open(path, "w") as f:
                f.write("{not json")
            state = guide.load_state(path)
            self.assertFalse(state["seen"])
            self.assertEqual(state["page"], 0)

    def test_a_page_past_the_end_is_clamped(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "guide.json")
            guide.save_state(path, {"seen": True, "done": False, "page": 99})
            self.assertEqual(guide.load_state(path)["page"], len(guide.PAGES) - 1)

    def test_the_guide_map_carries_everything_qml_needs(self):
        m = guide.guide_map({"show": True, "seen": False, "done": False, "page": 1}, live=True)
        self.assertTrue(m["show"])
        self.assertEqual(m["page"], 1)
        self.assertEqual(m["pageCount"], len(guide.PAGES))
        self.assertEqual(len(m["pipeline"]), len(guide.PIPELINE))
        self.assertEqual(m["pipeline"][2]["state"], "active")
        self.assertEqual(m["stageName"], "BUILD")
        self.assertEqual(m["examples"], guide.EXAMPLES)


if __name__ == "__main__":
    unittest.main()
