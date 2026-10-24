#!/usr/bin/env python3
"""Real notes-monitor acceptance, isolated HOME/run; never connects to cabinet.

Calls the production ViewportCorrections backend with a tiny viewer-ack stub,
a real ScriptLive subprocess, and the real geppetto-notes --follow executable.
The stub supplies only viewer seek acknowledgement; no notes/control mocks.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import uuid

from PySide6.QtCore import QCoreApplication, QObject, Signal

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / "cabinet"))
import live_runs
from viewport_correction import ViewportCorrections


def require(value, message):
    if not value:
        raise AssertionError(message)


def wait(predicate, message, seconds=8):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(0.02)
    raise AssertionError(message)


class FixtureHost(QObject):
    runsChanged = Signal()

    def __init__(self, root, process):
        super().__init__()
        self.watching = str(root)
        self.process = process
        self.displayed_revision = 0
        self.viewer_commands = []

    def run_live(self, root):
        return root == self.watching and self.process.poll() is None

    def cmd(self, text):
        self.viewer_commands.append(text)
        if text.startswith("seek "):
            self.displayed_revision = int(text.split()[1])


def put(root, kind, text=""):
    directory = root / ".live/inbox"
    directory.mkdir(parents=True, exist_ok=True)
    name = f"{time.time_ns() // 100 + 621355968000000000:019d}{uuid.uuid4().hex[:6]}"
    temp = directory / f"{name}.{kind}.tmp"
    temp.write_text(text)
    temp.rename(directory / f"{name}.{kind}")
    return name


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--binary", type=Path, required=True)
    ap.add_argument("--out", type=Path, default=Path("/tmp/opencode/c1-notes-follow-evidence"))
    args = ap.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    fixture = Path(tempfile.mkdtemp(prefix="roundtrip-", dir=args.out))
    home = fixture / "home"
    (home / "Desktop").mkdir(parents=True)
    # The real wrapper locates live_runs.py through HOME. Only this code-location
    # link is shared; its notes discovery root is entirely this disposable HOME.
    (home / "Desktop/geppetto-desktop").symlink_to(PROJECT, target_is_directory=True)
    runs = home / ".local/share/geppetto/research"
    runs.mkdir(parents=True)
    script = fixture / "monitor-fixture.txt"
    script.write_text("add Primitive shape=Box name=Base width=2 depth=2 height=2\n"
                      "set Base width=3\n"
                      "add Primitive shape=Box name=Later position=3,0,0\n")
    root = runs / ("script-monitor-" + uuid.uuid4().hex)
    env = dict(os.environ, HOME=str(home), PYTHONUNBUFFERED="1")
    for key in ("DISPLAY", "WAYLAND_DISPLAY"):
        env.pop(key, None)
    wrapper = Path("/home/poo/.local/bin/geppetto-notes")
    outputs = {name: fixture / name for name in ("run.stdout", "run.stderr", "monitor.stdout", "monitor.stderr", "restart.stdout", "restart.stderr")}
    streams = {name: path.open("w") for name, path in outputs.items()}
    process = monitor = restarted = None
    report = {"passed": False, "fixture": str(fixture), "run": str(root), "selected_revision": 2}
    try:
        process = subprocess.Popen(["dotnet", str(args.binary), "--script", str(script),
            "--live", "--open", "--idle", "1", "--pace", "0.2", "--run", str(root),
            "--out", str(fixture)], env=env, stdout=streams["run.stdout"], stderr=streams["run.stderr"])
        wait(lambda: (root / ".live/answers/start.txt").exists(), "real ScriptLive run did not open")
        require(process.poll() is None, "real fixture exited early")
        require(live_runs.read_state(str(root))["snapshot"]["revision"] == 4, "unexpected initial saved steps")

        # An unread note for a different script must not be consumed by the
        # filtered follower. This is another isolated metadata directory only.
        decoy = runs / "script-unrelated"
        decoy.mkdir()
        other_script = fixture / "unrelated.txt"
        other_script.write_text("# unrelated fixture\n")
        Path(str(decoy) + ".json").write_text(json.dumps({"script": str(other_script)}))
        live_runs.add_note(str(decoy), 99, "line 1: unrelated", "UNRELATED_MUST_NOT_BE_CONSUMED")

        monitor = subprocess.Popen([str(wrapper), "--follow", str(script)], env=env,
            stdout=streams["monitor.stdout"], stderr=streams["monitor.stderr"])
        time.sleep(0.2)
        require(monitor.poll() is None, "real geppetto-notes --follow did not stay alive")
        host = FixtureHost(root, process)
        correction = ViewportCorrections(host)
        require(correction.stop(1), "production Stop rejected the fixture")

        def ready():
            correction.refresh()
            return correction.sessions.get("1", {}).get("ready", False)

        wait(ready, "production correction controller did not acknowledge held revision")
        require(correction.sessions["1"]["revision"] == 4, "Stop captured the wrong current revision")
        token = "C1_REAL_FOLLOW_" + uuid.uuid4().hex
        report["correction_token"] = token
        selected = next(s for s in live_runs.steps(str(root)) if s["rev"] == 2)
        require(correction.send(1, 2, token + " Set Base width to 7 on this saved step."),
                "production earlier-step correction send failed")

        wait(lambda: token in outputs["monitor.stdout"].read_text(), "correction did not reach real --follow monitor")
        wait(lambda: all(n.get("read") for n in live_runs.notes(str(root))), "monitor did not mark delivered notes read")
        # More than three real one-second discovery cycles: no repeated delivery.
        time.sleep(3.2)
        text = outputs["monitor.stdout"].read_text()
        require(text.count(token) == 1, "correction delivered more than once")
        notes = live_runs.notes(str(root))
        matches = [n for n in notes if token in n.get("text", "")]
        require(len(matches) == 1 and matches[0]["read"], "correction not durably consumed exactly once")
        require(matches[0]["rev"] == 2 and matches[0]["run"] == root.name, "note targets wrong saved step/run")
        require(matches[0]["op"] == selected["op"], "note operation differs from selected saved step")
        header = f"NOTE  {script}:1  (run {root.name}, step 2)"
        require(header in text and "  step: " + selected["op"] in text, "printed monitor header/operation is incorrectly scoped")
        require("UNRELATED_MUST_NOT_BE_CONSUMED" not in text, "monitor leaked another script's note")
        require(live_runs.notes(str(decoy))[0]["read"] is False, "monitor consumed another script's note")

        # Act on the observed correction using the real script inbox, not a
        # fabricated assistant reply. The earlier revision must be retained.
        fix = put(root, "txt", "set Base width=7\nsave monitor-corrected.effigy\n")
        wait(lambda: (root / ".live/answers" / f"{fix}.txt").exists(), "modeller fix did not finish")
        require("ERROR" not in (root / ".live/answers" / f"{fix}.txt").read_text(), "real corrective batch failed")
        require((fixture / "monitor-corrected.effigy").exists(), "corrected saved model missing")
        saved = (fixture / "monitor-corrected.effigy").read_text()
        require("\tparam SizeX 7\n" in saved and "\tname Base\n" in saved,
                "observed correction was not saved into the retained Base feature")
        require("\tname Later\n" not in saved, "earlier-step correction retained the abandoned future feature")

        monitor.terminate(); monitor.wait(timeout=4)
        restarted = subprocess.Popen([str(wrapper), "--follow", str(script)], env=env,
            stdout=streams["restart.stdout"], stderr=streams["restart.stderr"])
        time.sleep(2.2)
        require(restarted.poll() is None, "restarted follower exited")
        require(outputs["restart.stdout"].read_text() == "", "follower replayed consumed notes after restart")
        restarted.terminate(); restarted.wait(timeout=4)
        put(root, "close")
        require(process.wait(timeout=5) == 0, "throwaway run did not finish cleanly")
        report.update(passed=True, correction_delivery_count=text.count(token),
                      consumed_note_count=len(notes), monitor_stdout=text,
                      decoy_note_unread=True, restart_output_empty=True,
                      selected_operation=selected["op"], run_exit_code=process.returncode,
                      corrected_feature_width=7, abandoned_feature_absent=True,
                      tested_binary=str(args.binary), binary_sha256=hashlib.sha256(args.binary.read_bytes()).hexdigest(),
                      viewer_commands=host.viewer_commands,
                      sources={p: hashlib.sha256((PROJECT / p).read_bytes()).hexdigest() for p in
                               ("cabinet/live_runs.py", "cabinet/viewport_correction.py", "tests/c1_notes_follow_roundtrip.py")})
    except Exception as exc:
        report["error"] = repr(exc)
        raise
    finally:
        for child in (restarted, monitor, process):
            if child is not None and child.poll() is None:
                child.terminate()
                try:
                    child.wait(timeout=4)
                except subprocess.TimeoutExpired:
                    child.kill(); child.wait(timeout=4)
        for stream in streams.values():
            stream.close()
        (fixture / "evidence.json").write_text(json.dumps(report, indent=2))
        print(json.dumps(report, indent=2))


if __name__ == "__main__":
    app = QCoreApplication(sys.argv)
    main()
