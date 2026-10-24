#!/usr/bin/env python3
"""Reuse the independent real-monitor fixture, exercising UI queue/apply instead
of a single protocol send. Only disposable runs/HOME; never connects to cabinet.
"""
import sys

from PySide6.QtCore import QCoreApplication
import c1_notes_follow_roundtrip as baseline


class StackedCorrections(baseline.ViewportCorrections):
    def send(self, pane, revision, text):
        baseline.require(self.queue(pane, revision, text), "first message did not queue")
        baseline.require(self.queue(pane, revision, "Keep the existing Base feature's name."), "second message did not queue")
        baseline.require(len(self.sessions[str(pane)]["queued"]) == 2, "messages did not stack")
        root = self.sessions[str(pane)]["dir"]
        baseline.require(baseline.live_runs.notes(root) == [], "queue leaked a note before Apply")
        baseline.require(baseline.live_runs.control_state(baseline.live_runs.read_state(root)).get("paused"), "queue resumed the real runner")
        result = self.apply(pane)
        baseline.require(len(self.sessions[str(pane)]["messages"]) == 2, "Apply duplicated/lost stacked bubbles")
        notes = baseline.live_runs.notes(root)
        correction = next(n for n in notes if "CORRECTION from the viewport" in n["text"])
        baseline.require("1. " + text in correction["text"], "first message lost its batch order")
        baseline.require("2. Keep the existing Base feature's name." in correction["text"], "second message missing from approved note")
        return result


if __name__ == "__main__":
    app = QCoreApplication(sys.argv)
    baseline.ViewportCorrections = StackedCorrections
    baseline.main()
