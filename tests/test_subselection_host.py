"""Sub-element selection (faces/edges/vertices) in the cabinet's host.

No viewer is started: the host's command line to the viewer and its bench path are
captured, and @selection events are fed in as the viewer would print them.
"""
import json
import os
import sys
import time
import unittest
from pathlib import Path

CABINET = Path(__file__).resolve().parents[1] / "cabinet"
sys.path.insert(0, str(CABINET))
os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QCoreApplication  # noqa: E402

import host as chest  # noqa: E402

FACE = {"kind": "face", "body": "Torso", "bodyId": "b1", "index": 4}
EDGE = {"kind": "edge", "body": "Torso", "bodyId": "b1", "index": 2}
VERT = {"kind": "vertex", "body": "Arm", "bodyId": "b2", "index": 7}


class FakeProc:
    """stands in for the viewer process: collects the lines written to it"""
    def __init__(self, host):
        self.host = host

    def state(self):
        return chest.QProcess.Running

    def readAllStandardOutput(self):
        out, self.pending = getattr(self, "pending", b""), b""
        return out


def event(**fields):
    ev = {"ev": "selection", "mode": "face", "kind": "", "count": 0, "faces": "", "edges": "",
          "bodyNames": "", "items": [], "bodies": {}, "lost": 0}
    ev.update(fields)
    return ev


class SubSelectionHostTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QCoreApplication.instance() or QCoreApplication([])

    def setUp(self):
        self.host = chest.Host()
        self.host.runTimer.stop()
        self.sent = []
        self.host.cmd = self.sent.append
        self.bench = []
        self.host.benchApply = self.bench.append
        self.status = []
        self.host.set_status = self.status.append

    def feed(self, ev):
        """the viewer printing @{json}: parsed by the same onOutput as the real one"""
        p = FakeProc(self.host)
        p.pending = b"@" + json.dumps(ev).encode() + b"\n"
        self.host.proc = p
        self.host.onOutput(p)

    # ---- parsing + prompt ----------------------------------------------------------------
    def test_one_face_parsed_with_prompt(self):
        seen = []
        self.host.subSelectionChanged.connect(lambda: seen.append(1))
        self.feed(event(kind="faces", count=1, faces="Torso:0,1,0:0,0,1", bodyNames="Torso", items=[FACE],
                        bodies={"Torso": {"select": "faces near 0,1,0", "element": "faces", "count": 1}}))
        self.assertTrue(seen)
        sel = self.host.subSelection
        self.assertEqual(sel["faces"], "Torso:0,1,0:0,0,1")
        self.assertEqual(sel["bodies"]["Torso"]["count"], 1)
        self.assertEqual(self.host.subMode, "face")
        self.assertEqual(self.host.subPrompt, "Face of Torso selected — tools that take faces will use it.")

    def test_prompts(self):
        p = chest.Host.sub_prompt
        self.assertEqual(p(event(items=[FACE, dict(FACE, index=5), dict(FACE, index=6)])), "3 faces selected")
        self.assertEqual(p(event(mode="edge", items=[EDGE, dict(EDGE, index=3)])), "2 edges selected")
        self.assertEqual(p(event(mode="vertex", items=[VERT] * 4)), "4 vertices selected")
        # edge wins over face, face over vertex
        self.assertEqual(p(event(items=[FACE, EDGE])), "1 edge selected")
        self.assertEqual(p(event(items=[FACE, dict(FACE, index=9), VERT])), "2 faces selected")
        self.assertEqual(p(event()), "")
        self.assertEqual(p(event(items=[FACE, FACE], notice="opposite face not found")),
                         "2 faces selected — opposite face not found")
        self.assertEqual(p(event(items=[FACE, FACE], lost=2)),
                         "2 faces selected 2 picked faces no longer exist… Re-pick.")
        self.assertEqual(p(event(lost=1)), "1 picked face no longer exists… Re-pick.")
        self.assertEqual(p(event(mode="edge", lost=3)), "3 picked edges no longer exist… Re-pick.")

    def test_selection_survives_other_events_and_resets_on_new_model(self):
        self.feed(event(count=1, faces="A:0,0,0:0,1,0", items=[dict(FACE, body="A")]))
        self.feed({"ev": "selected", "bone": -1, "body": 2})
        self.assertEqual(self.host.subSelection["faces"], "A:0,0,0:0,1,0")
        self.host._sub_reset()
        self.assertEqual(self.host.subMode, "body")
        self.assertEqual(dict(self.host.subSelection), {})
        self.assertEqual(self.host.subPrompt, "")

    # ---- commands to the viewer ----------------------------------------------------------
    def test_mode_switch(self):
        self.host.setSelMode("Face")
        self.host.setSelMode("bogus")
        self.assertEqual(self.sent, ["selmode face"])
        self.assertEqual(self.host.subMode, "face")

    def test_pick_modifiers(self):
        self.host.subPick(10.4, 20.6, False, False, False)
        self.host.subPick(10, 20, True, False, False)
        self.host.subPick(10, 20, False, True, False)
        self.host.subPick(10, 20, False, False, True)
        self.host.subPick(10, 20, True, False, True)
        self.assertEqual(self.sent, ["pick 10 21", "pick 10 20 add", "pick 10 20 remove",
                                     "pick 10 20 pair", "pick 10 20 add pair"])

    def test_hover_throttled_and_only_on_move(self):
        self.host.subHover(5, 5)                      # body mode: nothing
        self.assertEqual(self.sent, [])
        self.host._sub_mode = "edge"
        self.host.subHover(5, 5)
        self.host.subHover(5, 5)                      # same spot
        self.host.subHover(6, 5)                      # inside the 16 ms window: held back
        self.host.subHover(7, 5)
        self.assertEqual(self.sent, ["hover 5 5"])
        end = time.time() + 0.2
        while time.time() < end:
            self.app.processEvents()
            time.sleep(0.005)
        self.assertEqual(self.sent, ["hover 5 5", "hover 7 5"])   # the last one still arrives

    def test_clear(self):
        self.host.clearSubSelection()
        self.assertEqual(self.sent, ["selclear"])

    # ---- faces onto a material slot ------------------------------------------------------
    def test_assign_faces_to_slot(self):
        self.feed(event(count=2, faces="Torso:0,1,0:0,0,1;Torso:0,-1,0:0,0,-1", items=[FACE, FACE]))
        self.host.assignFacesToSlot(3)
        self.host.assignFacesToSlot(0)
        self.assertEqual(self.bench, ["facematerial slot=3 faces=Torso:0,1,0:0,0,1;Torso:0,-1,0:0,0,-1",
                                      "facematerial slot=0 faces=Torso:0,1,0:0,0,1;Torso:0,-1,0:0,0,-1"])

    def test_assign_without_faces_is_a_noop(self):
        self.feed(event(mode="edge", kind="edges", count=1, edges="Torso:0,0,0:1,0,0", items=[EDGE]))
        self.host.assignFacesToSlot(2)
        self.assertEqual(self.bench, [])
        self.assertTrue(self.status and "no faces" in self.status[-1])


if __name__ == "__main__":
    unittest.main()
