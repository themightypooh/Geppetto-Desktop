"""Lane C (2026-10-26): four-pane gate reservation and placement.

Fixture-only: a real Host() offscreen (QGuiApplication, no window, no
chest socket, no viewer processes — Pane.show/openModel are stubbed at the
class level) plus the real gate.py registry pointed at a temp directory.
Never touches real cabinet/confirm, never starts the visible chest, never
runs a paid provider.

What is covered here:
- reserve 1 -> 2 -> 3 -> 4 -> 1 from the requested pane, fifth refusal
- occupancy: live run, finished run, static model, replay, bench, parked /
  hidden content, another reservation; an idle viewer alone is NOT occupied
- gate-place validates against the registered run (forged / unregistered
  refused, registered accepted) using the real gate registry in temp dir
- _gate_place_ready: waits for .live/latest.json, bounded ready timeout,
  slot released before ready, pane taken before placement (nothing evicted),
  showRunIn refusal (bench in pane 1) lands nothing elsewhere
- autoFollow never places a gate-pending or denied run
- put_back never fills a reserved pane and keeps its parked entry
- layout growth to include a reserved pane
- first frame recorded; first-frame timeout closes only that run (and does
  not close a completed run or a run the user moved)
- _gate_free_slot gives parked content back when the pane is empty

Deliberately NOT covered here (needs the live chest, not authorised):
the chest socket commands themselves (gate-reserve/check/release/place
over the unix socket) and any real pane snap. Those go through the same
Host methods tested here; the socket handler is a thin reply wrapper.
"""
import json
import os
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest.mock import patch

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

CABINET = Path(__file__).resolve().parents[1] / "cabinet"
sys.path.insert(0, str(CABINET))

from PySide6.QtCore import QObject  # noqa: E402
from PySide6.QtGui import QGuiApplication  # noqa: E402

import host as host_module  # noqa: E402
import panes as paneslib  # noqa: E402
import gate  # noqa: E402

SCRIPT = "add Primitive name=Body shape=Box materialslot=0\nsave model.effigy\n"


def live_file(run, state="open"):
    live = os.path.join(run, ".live")
    os.makedirs(live, exist_ok=True)
    with open(os.path.join(live, "latest.json"), "w") as f:
        json.dump({"schemaVersion": 1, "ev": "ai-live", "runId": os.path.basename(run),
                   "sequence": 1, "state": state}, f)


class FakeBenchProc:
    def state(self):
        return 1  # QProcess.Running (NotRunning is 0)


def stub_show(self, directory):
    self.run = os.path.abspath(directory)
    return True


def stub_open_model(self, path, clip="", session=""):
    self.model = path
    self.run = ""
    return None


class GatePanesTests(unittest.TestCase):
    def setUp(self):
        self.app = QGuiApplication.instance() or QGuiApplication([])
        self.host = host_module.Host()
        self.host._preferences_path = "/nonexistent/geppetto-test/preferences.json"
        self.host.runTimer.stop()
        self.host.pictureTimer.stop()
        self.host._fresh_start = False
        self.host._layout = 4
        self.statuses = []
        self.host.set_status = lambda s: self.statuses.append(s)
        self.host.scanRuns = lambda: None
        self.host.watchRun = lambda d: setattr(self.host, "watching", os.path.abspath(d))
        self._show_patch = patch.object(paneslib.Pane, "show", stub_show)
        self._model_patch = patch.object(paneslib.Pane, "openModel", stub_open_model)
        self._show_patch.start()
        self._model_patch.start()
        self._tmp = tempfile.TemporaryDirectory()
        self.root = self._tmp.name

    def tearDown(self):
        self._show_patch.stop()
        self._model_patch.stop()
        for p in reversed(getattr(self, "_registry_patches", [])):
            p.stop()
        try:
            self.host.runTimer.stop()
            self.host.pictureTimer.stop()
        except Exception:
            pass
        self._tmp.cleanup()

    # ---- helpers ------------------------------------------------------
    def reset_gate(self):
        self.host._gate_slots = {}
        self.host._gate_placements = {}
        self.host._gate_shown = {}
        self.host._gate_frames = {}
        self.host._gate_failures = {}
        self.host._gate_denied = set()
        self.host._gate_dispatched = {}
        self.host._parked = {}
        self.host.watching = ""
        self.host.model = ""
        self.host._replay = {}
        self.host.bench_proc = None
        for p in self.host.panes:
            p.run = ""
            p.model = ""
            p._replay = {}
        self.host.waiting = []
        self.host.auto_pending = set()
        self.host._solo = 0
        self.statuses = []

    def run_dir(self, name):
        d = os.path.join(self.root, name)
        os.makedirs(d, exist_ok=True)
        return d

    def reserve_for(self, iid, pane):
        """File a real gate intent in a temp registry, reserving via this host."""
        script = os.path.join(self.root, "build-%s.txt" % iid)
        with open(script, "w") as f:
            f.write(SCRIPT)
        return script

    def _dispatch(self, iid, pane):
        """Stand in for the yes-site record: slot held, launch dispatched."""
        self.host._gate_slots[iid] = pane
        self.host._gate_dispatched[iid] = {"pane": pane, "dispatched_at": time.time()}

    # ---- reservation --------------------------------------------------
    def test_reserve_wraps_and_fifth_refused(self):
        self.reset_gate()
        self.assertEqual(self.host.gate_reserve("a", "3"), 3)
        self.assertEqual(self.host.gate_reserve("b", "3"), 4)
        self.assertEqual(self.host.gate_reserve("c", "3"), 1)
        self.assertEqual(self.host.gate_reserve("d", "3"), 2)
        self.assertEqual(self.host.gate_reserve("e", "3"), 0)
        # Idempotent: re-reserving the same intent returns its pane.
        self.assertEqual(self.host.gate_reserve("a", "1"), 3)
        self.assertEqual(self.host.gate_reserve("bad", "9"), 0)
        self.assertEqual(self.host.gate_reserve("bad2", "x"), 0)

    def test_occupancy_combinations(self):
        self.reset_gate()
        # Everything empty: requested pane wins.
        self.assertEqual(self.host.gate_reserve("x", "1"), 1)
        self.reset_gate()
        # Pane 1 showing a live run (watching) is occupied.
        self.host.watching = self.run_dir("live1")
        self.assertEqual(self.host.gate_reserve("x", "1"), 2)
        self.reset_gate()
        # Pane 1 static model counts even without watching (old code only
        # counted it when _mine was set; the gate must never evict it).
        self.host.model = "/models/some.effigy"
        self.assertEqual(self.host.gate_reserve("x", "1"), 2)
        self.reset_gate()
        # Pane 1 replay counts.
        self.host._replay = {"dir": self.run_dir("rep")}
        self.assertEqual(self.host.gate_reserve("x", "1"), 2)
        self.reset_gate()
        # Pane 1 bench counts.
        self.host.bench_proc = FakeBenchProc()
        self.assertEqual(self.host.gate_reserve("x", "1"), 2)
        self.reset_gate()
        # Pane 2 finished run still on screen counts (gate_occupied, not
        # busy(): a finished run is not live but must not be evicted).
        self.host.panes[0].run = self.run_dir("finished")
        self.assertEqual(self.host.gate_reserve("x", "2"), 3)
        self.reset_gate()
        # Pane 2 replay counts.
        self.host.panes[0]._replay = {"dir": self.run_dir("rep2")}
        self.assertEqual(self.host.gate_reserve("x", "2"), 3)
        self.reset_gate()
        # Pane 3 parked content counts even though the pane object is empty.
        self.host._parked[3] = {"n": 3, "run": self.run_dir("parked"),
                                "model": "", "session": "", "since": time.time()}
        self.assertEqual(self.host.gate_reserve("x", "3"), 4)
        self.reset_gate()
        # Another intent's reservation counts.
        self.host._gate_slots["other"] = 4
        self.assertEqual(self.host.gate_reserve("x", "4"), 1)
        self.reset_gate()
        # An idle viewer alone does NOT occupy: pane object with a live
        # proc but no run, no model, no replay is free.
        self.host.panes[0].proc = FakeBenchProc()
        self.assertEqual(self.host.gate_reserve("x", "2"), 2)

    def test_pane_gate_occupied_combos(self):
        pane = paneslib.Pane(QObject(), 2, "", {})
        self.assertFalse(pane.gate_occupied())
        pane.run = "/runs/finished"
        self.assertTrue(pane.gate_occupied())
        pane.run = ""
        pane.model = "/models/static.effigy"
        self.assertTrue(pane.gate_occupied())
        pane.model = ""
        pane._replay = {"dir": "/runs/rep"}
        self.assertTrue(pane.gate_occupied())

    # ---- gate-place validation against the real registry ---------------
    def test_gate_place_registered_only(self):
        self.reset_gate()
        with tempfile.TemporaryDirectory() as conf:
            old_conf, old_audit = gate.CONFIRM_DIR, gate.AUDIT
            gate.CONFIRM_DIR = conf
            gate.AUDIT = os.path.join(conf, "audit.jsonl")
            try:
                def fake_reserve(iid, preferred):
                    n = self.host.gate_reserve(iid, preferred)
                    return (n, "" if n else "REFUSED: all four viewports are occupied")

                with patch.object(gate, "reserve_pane", side_effect=fake_reserve):
                    script = self.reserve_for("i1", "2")
                    intent, err = gate.file_intent(script, "test model", "worker", "2")
                    self.assertTrue(intent, err)
                    self.assertEqual(intent["pane"], "2")
                    iid = intent["id"]
                    run = self.run_dir("gate-run")
                    other = self.run_dir("other-run")
                    # Forged run before registration: refused, no placement.
                    self.assertFalse(self.host.gate_place(iid, other))
                    self.assertEqual(self.host._gate_placements, {})
                    self.assertIn(iid, self.host._gate_failures)
                    # Yes but not yet registered: refused.
                    gate.answer_intent(iid, "yes")
                    self.assertFalse(self.host.gate_place(iid, run))
                    self.assertEqual(self.host._gate_placements, {})
                    # Claim + register (live pid: register refuses a dead
                    # pid), then the exact run is accepted.
                    state, _ = gate.claim_token(iid, script, "worker")
                    self.assertEqual(state, "START")
                    self.assertTrue(gate.register_run(iid, run, os.getpid()))
                    self.assertTrue(self.host.gate_place(iid, run))
                    self.assertIn(run, self.host._gate_placements)
                    # Still waiting for .live/latest.json: not shown yet.
                    self.assertNotIn(run, self.host._gate_shown)
                    live_file(run, "open")
                    self.host._gate_place_ready()
                    self.assertIn(run, self.host._gate_shown)
                    self.assertEqual(self.host._gate_shown[run]["pane"], 2)
                    self.assertEqual(self.host.panes[0].run, run)
                    self.assertNotIn(iid, self.host._gate_slots)  # run owns pane now
                    self.assertEqual(gate.load_intent(iid)["status"], "placed")
                    # First frame closes the wait with a recorded timestamp.
                    shown_at = self.host._gate_shown[run]["shown_at"]
                    self.host._gate_frame(2)
                    self.assertNotIn(run, self.host._gate_shown)
                    rec = self.host._gate_frames[run]
                    self.assertGreaterEqual(rec["first_frame_at"], shown_at)
                    self.assertTrue(rec["registry_recorded"])
                    self.assertEqual(gate.load_intent(iid)["status"], "placed")
            finally:
                gate.CONFIRM_DIR, gate.AUDIT = old_conf, old_audit

    # ---- _gate_place_ready mechanics (injected reservation) ------------
    def _inject_pending(self, iid, pane, run):
        # Stand in for the registry: validate ok, live pid, assigned pane,
        # placement recording ok. (The real-registry path is covered by
        # test_gate_place_registered_only.)
        self.host._gate_slots[iid] = pane
        patches = [
            patch.object(gate, "validate_placement", return_value=(True, "")),
            patch.object(gate, "_load_token",
                         return_value={"pid": os.getpid()}),
            patch.object(gate, "load_intent",
                         return_value={"pane": str(pane)}),
            patch.object(gate, "record_placement", return_value=True),
        ]
        for p in patches:
            p.start()
        self._registry_patches = getattr(self, "_registry_patches", []) + patches
        self.assertTrue(self.host.gate_place(iid, run))
        return run

    def test_place_ready_timeout_and_slot_release(self):
        self.reset_gate()
        run = self.run_dir("slow-run")
        self._inject_pending("g1", 2, run)
        self.assertIn(run, self.host._gate_placements)
        # No live file yet: stays pending, slot held.
        self.host._gate_place_ready()
        self.assertIn(run, self.host._gate_placements)
        self.assertEqual(self.host._gate_slots.get("g1"), 2)
        # Past the ready deadline with still no live file: failure, slot freed.
        self.host._gate_placements[run]["placed_at"] = time.monotonic() - 31
        self.host._gate_place_ready()
        self.assertNotIn(run, self.host._gate_placements)
        self.assertNotIn("g1", self.host._gate_slots)
        self.assertIn("g1", self.host._gate_failures)

    def test_place_ready_slot_released_before_ready(self):
        self.reset_gate()
        run = self.run_dir("rel-run")
        self._inject_pending("g1", 2, run)
        self.host._gate_slots.pop("g1", None)  # e.g. socket gate-release
        self.host._gate_place_ready()
        self.assertNotIn(run, self.host._gate_placements)
        self.assertIn("g1", self.host._gate_failures)

    def test_place_ready_pane_taken_evicts_nothing(self):
        self.reset_gate()
        run = self.run_dir("late-run")
        other = self.run_dir("arrived-first")
        self._inject_pending("g1", 2, run)
        # Another owner arrives in pane 2 before the gate run is ready.
        self.host.panes[0].run = other
        live_file(run, "open")
        self.host._gate_place_ready()
        self.assertNotIn(run, self.host._gate_placements)
        self.assertNotIn(run, self.host._gate_shown)
        self.assertEqual(self.host.panes[0].run, other)  # untouched
        self.assertIn(run, self.host._gate_denied)
        self.assertIn("g1", self.host._gate_failures)
        self.assertNotIn("g1", self.host._gate_slots)

    def test_showrunin_refusal_bench_lands_nowhere(self):
        self.reset_gate()
        run = self.run_dir("bench-run")
        self._inject_pending("g1", 1, run)
        # Bench starts in pane 1 after reservation, before placement.
        self.host.bench_proc = FakeBenchProc()
        live_file(run, "open")
        self.host._gate_place_ready()
        self.assertNotIn(run, self.host._gate_placements)
        self.assertNotIn(run, self.host._gate_shown)
        self.assertEqual(self.host.watching, "")
        self.assertNotIn(run, [p.run for p in self.host.panes])
        self.assertIn(run, self.host._gate_denied)
        self.assertIn("g1", self.host._gate_failures)

    def test_layout_expands_for_reserved_pane(self):
        self.reset_gate()
        self.host._layout = 1
        calls = []
        self.host._apply_layout = lambda n: (calls.append(n), setattr(self.host, "_layout", n))
        run = self.run_dir("grow-run")
        self._inject_pending("g1", 3, run)
        live_file(run, "open")
        self.host._gate_place_ready()
        self.assertEqual(calls, [4])
        self.assertEqual(self.host.panes[1].run, run)
        self.assertEqual(self.host._gate_shown[run]["pane"], 3)

    # ---- autoFollow ----------------------------------------------------
    def test_autofollow_skips_gate_and_denied_runs(self):
        self.reset_gate()
        run = self.run_dir("auto-run")
        with patch.object(host_module.live_runs, "close_open") as close_mock:
            self.host._gate_placements[run] = {"iid": "g1", "pane": 2,
                                               "placed_at": time.monotonic()}
            self.host.waiting = [run]
            self.host.autoFollow()
            self.assertEqual(self.host.waiting, [])
            self.assertNotIn(run, [p.run for p in self.host.panes])
            self.host._gate_placements.pop(run, None)
            self.host._gate_denied.add(run)
            self.host.waiting = [run]
            self.host.autoFollow()
            self.assertEqual(self.host.waiting, [])
            self.assertNotIn(run, [p.run for p in self.host.panes])
            close_mock.assert_not_called()

    # ---- put_back / free-slot ------------------------------------------
    def test_putback_skips_reserved_pane_keeps_parked(self):
        self.reset_gate()
        run2 = self.run_dir("back2")
        run3 = self.run_dir("back3")
        now = time.time()
        self.host._gate_slots["gr"] = 3
        self.host.put_back([{"n": 2, "run": run2, "model": "", "session": "", "since": now},
                            {"n": 3, "run": run3, "model": "", "session": "", "since": now}])
        self.assertEqual(self.host.panes[0].run, run2)
        self.assertEqual(self.host.panes[1].run, "")
        self.assertEqual(self.host._parked[3]["run"], run3)

    def test_free_slot_restores_parked_when_pane_empty(self):
        self.reset_gate()
        run2 = self.run_dir("parked2")
        self.host._parked[2] = {"n": 2, "run": run2, "model": "",
                                "session": "", "since": time.time()}
        self.host._gate_slots["gf"] = 2
        self.assertEqual(self.host._gate_free_slot("gf"), 2)
        self.assertNotIn("gf", self.host._gate_slots)
        self.assertNotIn(2, self.host._parked)
        self.assertEqual(self.host.panes[0].run, run2)

    # ---- first frame ----------------------------------------------------
    def test_first_frame_timeout_closes_only_that_run(self):
        self.reset_gate()
        run = self.run_dir("stuck-run")
        other = self.run_dir("other-pane-run")
        live_file(run, "open")
        live_file(other, "open")
        self.host.panes[0].run = run
        self.host.panes[1].run = other
        self.host._gate_shown[run] = {"iid": "g1", "pane": 2,
                                      "shown_at": time.monotonic() - 10,
                                      "first_frame_deadline_at": time.time() - 1}
        with patch.object(host_module.live_runs, "close_open") as close_mock:
            self.host._gate_place_ready()
            close_mock.assert_called_once_with(run)
        self.assertNotIn(run, self.host._gate_shown)
        self.assertIn("g1", self.host._gate_failures)
        self.assertEqual(self.host.panes[1].run, other)

    def test_first_frame_slow_state_is_not_a_failure(self):
        self.reset_gate()
        run = self.run_dir("slow-run")
        live_file(run, "open")
        self.host.panes[0].run = run
        self.host._gate_shown[run] = {"iid": "g1", "pane": 2,
                                      "shown_at": time.monotonic() - 10,
                                      "first_frame_deadline_at": time.time() + 30,
                                      "slow_noted": False}
        with patch.object(host_module.live_runs, "close_open") as close_mock:
            self.host._gate_place_ready()
            close_mock.assert_not_called()
        self.assertIn(run, self.host._gate_shown)
        self.assertNotIn("g1", self.host._gate_failures)
        self.assertTrue(any("still starting" in s for s in self.statuses))

    def test_gate_place_dead_pid_refused(self):
        self.reset_gate()
        with tempfile.TemporaryDirectory() as conf:
            old_conf, old_audit = gate.CONFIRM_DIR, gate.AUDIT
            gate.CONFIRM_DIR = conf
            gate.AUDIT = os.path.join(conf, "audit.jsonl")
            try:
                def fake_reserve(iid, preferred):
                    n = self.host.gate_reserve(iid, preferred)
                    return (n, "" if n else "REFUSED")

                with patch.object(gate, "reserve_pane", side_effect=fake_reserve):
                    script = self.reserve_for("dp", "2")
                    intent, err = gate.file_intent(script, "dead pid", "worker", "2")
                    self.assertTrue(intent, err)
                    iid = intent["id"]
                    run = self.run_dir("dp-run")
                    gate.answer_intent(iid, "yes")
                    state, _ = gate.claim_token(iid, script, "worker")
                    self.assertEqual(state, "START")
                    self.assertTrue(gate.register_run(iid, run, os.getpid()))
                    # Launcher pid dies after register: placement refused.
                    with patch.object(gate, "_pid_alive", return_value=False):
                        self.assertFalse(self.host.gate_place(iid, run))
                    self.assertNotIn(run, self.host._gate_placements)
                    self.assertIn(iid, self.host._gate_failures)
            finally:
                gate.CONFIRM_DIR, gate.AUDIT = old_conf, old_audit

    def test_showrunin_refusals_are_loud(self):
        self.reset_gate()
        run = self.run_dir("ref-run")
        self.assertFalse(self.host.showRunIn(run, 9))
        self.assertTrue(any("does not exist" in s for s in self.statuses))
        self.host._layout = 1
        self.assertFalse(self.host.showRunIn(run, 3))
        self.assertTrue(any("not shown" in s for s in self.statuses))
        self.assertNotIn(run, [p.run for p in self.host.panes])
        self.assertEqual(self.host.watching, "")
        self.host._layout = 4
        self.host.bench_proc = FakeBenchProc()
        self.assertFalse(self.host.showRunIn(run, 1))
        self.assertEqual(self.host.watching, "")

    def test_free_pane_skips_gate_runs(self):
        self.reset_gate()
        run = self.run_dir("fp-run")
        self.host._gate_placements[run] = {"iid": "g1", "pane": 2,
                                           "placed_at": time.monotonic()}
        self.assertEqual(self.host.free_pane(run), 0)
        self.assertEqual(self.host.free_pane(""), 1)

    def test_solo_cleared_for_gate_placement_only(self):
        self.reset_gate()
        run = self.run_dir("solo-run")
        self.host._solo = 3
        self._inject_pending("g1", 2, run)
        live_file(run, "open")
        self.host._gate_place_ready()
        self.assertEqual(self.host._solo, 0)
        self.assertEqual(self.host.panes[0].run, run)
        self.reset_gate()
        run2 = self.run_dir("solo-run2")
        self.host._solo = 2
        self._inject_pending("g2", 2, run2)
        live_file(run2, "open")
        self.host._gate_place_ready()
        self.assertEqual(self.host._solo, 2)
        self.assertEqual(self.host.panes[0].run, run2)

    def test_rehydrate_slots_from_registry(self):
        self.reset_gate()
        entries = [
            {"id": "gate-1000000000-abcdef01", "pane": "2", "reserving": True},
            {"id": "gate-1000000001-abcdef02", "pane": "2", "reserving": True},
            {"id": "gate-1000000002-abcdef03", "pane": "3", "reserving": False},
            {"id": "gate-1000000003-abcdef04", "pane": "9", "reserving": True},
            {"id": "../evil", "pane": "4", "reserving": True},
        ]
        with patch.object(gate, "reservations_list", return_value=entries):
            self.assertEqual(self.host.rehydrate_gate_slots(), 1)
        self.assertEqual(self.host._gate_slots,
                         {"gate-1000000000-abcdef01": 2})

    def test_first_frame_timeout_spares_completed_and_moved_runs(self):
        self.reset_gate()
        done = self.run_dir("done-run")
        moved = self.run_dir("moved-run")
        live_file(done, "completed")
        live_file(moved, "open")
        self.host.panes[0].run = done
        # moved run is no longer in its gate pane (user moved it to pane 3).
        self.host.panes[1].run = moved
        self.host._gate_shown[done] = {"iid": "gd", "pane": 2,
                                       "shown_at": time.monotonic() - 10}
        self.host._gate_shown[moved] = {"iid": "gm", "pane": 2,
                                        "shown_at": time.monotonic() - 10}
        with patch.object(host_module.live_runs, "close_open") as close_mock:
            self.host._gate_place_ready()
            close_mock.assert_not_called()
        self.assertNotIn(done, self.host._gate_shown)
        self.assertNotIn(moved, self.host._gate_shown)
        self.assertNotIn("gd", self.host._gate_failures)
        self.assertNotIn("gm", self.host._gate_failures)

    # ---- dispatched-launch watch (Lane B request) ----------------------
    def test_launch_watch_claim_timeout_fails_and_frees_slot(self):
        self.reset_gate()
        self._dispatch("lw1", 2)
        st = {"status": "yes", "first_frame_at": None,
              "token": {"yes_at": time.time() - 700, "used_at": time.time() - 650,
                        "run": None, "pid": None}}
        with patch.object(gate, "intent_status", return_value=st):
            self.host._gate_launch_watch()
        self.assertNotIn("lw1", self.host._gate_dispatched)
        self.assertNotIn("lw1", self.host._gate_slots)
        self.assertIn("lw1", self.host._gate_failures)
        self.assertTrue(any("never registered" in s or "never claimed" in s
                            or "claimed but never registered" in s for s in self.statuses))

    def test_launch_watch_failed_intent_surfaces_and_denies_run(self):
        self.reset_gate()
        run = self.run_dir("lw-failed-run")
        self._dispatch("lw2", 2)
        st = {"status": "failed", "cancel_reason": "register refused: launcher pid already dead",
              "first_frame_at": None,
              "token": {"run": run, "pid": 1234}}
        with patch.object(gate, "intent_status", return_value=st):
            self.host._gate_launch_watch()
        self.assertNotIn("lw2", self.host._gate_dispatched)
        self.assertNotIn("lw2", self.host._gate_slots)
        self.assertIn(run, self.host._gate_denied)
        self.assertIn("register refused", self.host._gate_failures["lw2"]["reason"])

    def test_launch_watch_placed_clears_silently(self):
        self.reset_gate()
        self._dispatch("lw3", 2)
        st = {"status": "placed", "first_frame_at": time.time(),
              "token": {"run": self.run_dir("lw-placed-run"), "pid": os.getpid()}}
        with patch.object(gate, "intent_status", return_value=st):
            self.host._gate_launch_watch()
        self.assertNotIn("lw3", self.host._gate_dispatched)
        self.assertNotIn("lw3", self.host._gate_failures)

    def test_launch_watch_live_launch_kept_waiting(self):
        self.reset_gate()
        run = self.run_dir("lw-live-run")
        self._dispatch("lw4", 2)
        st = {"status": "launched", "first_frame_at": None,
              "token": {"run": run, "pid": os.getpid(), "registered_at": time.time()}}
        with patch.object(gate, "intent_status", return_value=st), \
                patch.object(gate, "_pid_alive", return_value=True):
            self.host._gate_launch_watch()
        self.assertIn("lw4", self.host._gate_dispatched)
        self.assertEqual(self.host._gate_slots.get("lw4"), 2)
        self.assertNotIn("lw4", self.host._gate_failures)

    def test_launch_watch_dead_pid_fails_and_denies_run(self):
        self.reset_gate()
        run = self.run_dir("lw-dead-run")
        self._dispatch("lw5", 2)
        st = {"status": "launched", "first_frame_at": None,
              "token": {"run": run, "pid": 1234, "registered_at": time.time()}}
        with patch.object(gate, "intent_status", return_value=st), \
                patch.object(gate, "_pid_alive", return_value=False):
            self.host._gate_launch_watch()
        self.assertNotIn("lw5", self.host._gate_dispatched)
        self.assertNotIn("lw5", self.host._gate_slots)
        self.assertIn(run, self.host._gate_denied)
        self.assertIn("died before any first frame", self.host._gate_failures["lw5"]["reason"])

    def test_launch_watch_registered_never_placed_deadline(self):
        self.reset_gate()
        run = self.run_dir("lw-stuck-run")
        self._dispatch("lw6", 2)
        st = {"status": "launched", "first_frame_at": None,
              "token": {"run": run, "pid": os.getpid(), "registered_at": time.time() - 40}}
        with patch.object(gate, "intent_status", return_value=st), \
                patch.object(gate, "_pid_alive", return_value=True):
            self.host._gate_launch_watch()
        self.assertNotIn("lw6", self.host._gate_dispatched)
        self.assertNotIn("lw6", self.host._gate_slots)
        self.assertIn(run, self.host._gate_denied)
        self.assertIn("never placed", self.host._gate_failures["lw6"]["reason"])

    def test_launch_watch_shown_run_not_double_failed(self):
        self.reset_gate()
        run = self.run_dir("lw-shown-run")
        self._dispatch("lw7", 2)
        self.host._gate_shown[run] = {"iid": "lw7", "pane": 2,
                                      "shown_at": time.monotonic(),
                                      "first_frame_deadline_at": time.time() + 30,
                                      "slow_noted": False}
        st = {"status": "launched", "first_frame_at": None,
              "token": {"run": run, "pid": os.getpid(), "registered_at": time.time()}}
        with patch.object(gate, "intent_status", return_value=st), \
                patch.object(gate, "_pid_alive", return_value=True):
            self.host._gate_launch_watch()
        self.assertNotIn("lw7", self.host._gate_dispatched)
        self.assertNotIn("lw7", self.host._gate_failures)
        self.assertIn(run, self.host._gate_shown)  # place-ready still owns it

    def test_launch_note_dispatched_uses_slot(self):
        self.reset_gate()
        self.host._gate_slots["ln1"] = 3
        self.host._gate_note_dispatched("ln1")
        self.assertEqual(self.host._gate_dispatched["ln1"]["pane"], 3)
        self.host._gate_note_dispatched("no-slot")
        self.assertNotIn("no-slot", self.host._gate_dispatched)


if __name__ == "__main__":
    unittest.main()
