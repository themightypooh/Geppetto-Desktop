"""Split viewport: the extra panes of the chest's viewport (panes 2-4; pane 1 is the host's own viewer).

Each pane runs its own headless viewer (`marionette-desktop --serve --watch-run`), so it has its own
camera, its own run, its own replay strip and its own notes target. The host decides which run goes in
which pane (Host.autoFollow / Host.showRunIn): a pane showing a live run is never taken over.
"""
import json
import mmap
import os
import re

from PySide6.QtCore import QObject, QProcess, QTimer, Property, Signal, Slot
from PySide6.QtGui import QImage

import live_runs
import pane_owner
import part_focus
from live_runs import TERMINAL, read_state, activity

# who is modelling a script run: the tool that started geppetto-live, found up its process tree
MODELLERS = (("claude", "Claude"), ("muse", "Muse"), ("codex", "Codex"), ("mimo", "MiMo"), ("gemini", "Gemini"),
             ("opencode", "opencode"), ("cursor", "Cursor"), ("aider", "aider"), ("qwen", "Qwen"),
             ("ollama", "Ollama"))
_modeller_cache = {}


def _proc(pid, what):
    try:
        with open("/proc/%d/%s" % (pid, what), "rb") as f:
            return f.read()
    except OSError:
        return b""


def _stem_who(d, meta):
    """last-resort who for a run: its script's name, else its folder's name, cleaned
    of the run-date suffix; a muse-build script is Muse, whatever started it"""
    script = str(meta.get("script") or "")
    if script:
        stem = os.path.splitext(os.path.basename(script))[0]
        if stem.startswith("muse-build-") or stem.startswith("muse_") or stem == "muse":
            return "Muse"
        stem = re.sub(r"-?\d{8}-\d{6}(-\d+)?$", "", stem)
        if stem.startswith("script-"):
            stem = stem[len("script-"):]
        if stem:
            return stem[:24]
    base = os.path.basename(d.rstrip("/"))
    base = re.sub(r"-?\d{8}-\d{6}(-\d+)?$", "", base)
    if base.startswith("script-"):
        base = base[len("script-"):]
    return base[:24] or "run"


def modeller(d):
    """Only recorded identity or a live process is evidence; filenames are not people."""
    if not d:
        return ""
    meta = live_runs.run_meta(d)
    who = str(meta.get("modeller") or meta.get("model") or "")
    # Older hosts persisted the script stem as a guessed identity. Ignore those guesses.
    if who and (meta.get("identitySource") or who != _stem_who(d, meta)):
        return who
    pid = live_runs.run_pid(d) or 0
    if pid > 0:
        env = dict(kv.split(b"=", 1) for kv in _proc(pid, "environ").split(b"\0") if b"=" in kv)
        if env.get(b"GEPPETTO_MODELLER"):
            return env[b"GEPPETTO_MODELLER"].decode(errors="replace")
        # T3 can inherit Claude's session variables even when the current agent is Codex.
        if env.get(b"CODEX_THREAD_ID") or env.get(b"CODEX_SESSION_ID"):
            return "Codex"
        for _ in range(12):
            if pid <= 1:
                break
            args = _proc(pid, "cmdline").split(b"\0")
            names = [os.path.basename(arg.decode(errors="replace")).lower() for arg in args[:2]]
            for key, label in MODELLERS:
                if any(name == key or name.startswith(key + ".") for name in names):
                    return label
            status = _proc(pid, "status").decode(errors="replace")
            pid = next((int(x.split()[1]) for x in status.splitlines() if x.startswith("PPid:")), 0)
    return ""

def script_label(d):
    """the script a run builds, short: its folder and file ("dagger/build.txt"), or the research prompt"""
    try:
        meta = json.load(open(d.rstrip("/") + ".json"))
    except (OSError, ValueError):
        meta = {}
    if not isinstance(meta, dict):
        meta = {}
    if meta.get("subject"):
        return str(meta["subject"])
    script = meta.get("script") or ""
    if script:
        return os.path.join(os.path.basename(os.path.dirname(script)), os.path.basename(script))
    return meta.get("subject") or meta.get("prompt") or os.path.basename(d)


class Pane(QObject):
    """one extra viewport pane: its own viewer process, run, camera and replay"""
    frameReady = Signal()
    changed = Signal()
    replayChanged = Signal()
    ownerChanged = Signal()

    SEND_OFF_DELAY_MS = 1800

    def __init__(self, host, number, exe, env):
        super().__init__(host)
        self.host = host
        self.number = number            # 2..4, as po sees it
        self.exe, self.env = exe, env
        self.proc = None
        self.buf = b""
        self.shm = None
        self.shm_path = ""
        self.image = None
        self.info = {}
        self.run = ""
        self.model = ""                 # a model (and clip) another session opened here with `host.py pane N model`
        self.owner_session = ""         # the session that opened that model ("" for a run: the run names it)
        self.since = 0.0                # when the run or model was put here
        self._owner = {}                # pane_owner.owner(): title, presence, how long quiet
        self.size = (512, 288)
        self.displayed_revision = 0
        self.saw_building = False
        self.cam = None                 # (yaw, pitch) from the last frame
        self._replay = {}
        self.replayTimer = QTimer(self)
        self.replayTimer.setInterval(700)
        self.replayTimer.timeout.connect(self._replay_tick)

    num = Property(int, lambda self: self.number, constant=True)
    runDir = Property(str, lambda self: self.run, notify=changed)
    modelPath = Property(str, lambda self: self.model, notify=changed)
    modeller = Property(str, lambda self: modeller(self.run) if self.run else "", notify=changed)
    script = Property(str, lambda self: script_label(self.run) if self.run else "", notify=changed)
    shownOp = Property(str, lambda self: live_runs.revision_op(self.run, self.displayed_revision)
                       if self.run and self.displayed_revision else "", notify=changed)
    shownRevision = Property(int, lambda self: self.displayed_revision, notify=changed)
    stepChangeText = Property(str, lambda self: getattr(self, "stepChange", ""), notify=changed)   # "NEW tail · CHANGED head"
    replay = Property("QVariantMap", lambda self: self._replay, notify=replayChanged)
    owner = Property("QVariantMap", lambda self: self._owner, notify=ownerChanged)

    def refresh_owner(self, now=None):
        """who is in this pane and whether they are still there; the host calls it every scan"""
        if not self.run and not self.model:
            info = {}
        else:
            info = pane_owner.owner(self.owner_session, self.run, self.since,
                                    live=bool(self._replay) or (bool(self.run) and self.host.run_live(self.run)), now=now)
        if info != self._owner:
            self._owner = info
            self.ownerChanged.emit()
        return info

    def touch(self, session=""):
        """a `host.py pane N ...` verb: its sender is still here"""
        import time
        self.since = time.time()
        if session:
            self.owner_session = session

    def busy(self):
        """showing something po would lose: a live run, or a replay"""
        if self._replay or self.model:
            return True
        return bool(self.run) and self.host.run_live(self.run)

    def gate_occupied(self):
        """Reservation counting (lane C, 2026-10-26): ANY shown content
        occupies the pane for gate purposes -- a live run, a finished run
        still on screen, a static model, or a replay. busy() is narrower
        (live runs only), so a finished run left on screen would look free
        to auto-follow; the gate must never evict it. An idle viewer (no
        run, no model, no replay) does NOT occupy the pane."""
        return bool(self._replay or self.model or self.run)

    # ---- the viewer process ------------------------------------------------------------------
    def alive(self):
        return self.proc is not None and self.proc.state() != QProcess.NotRunning

    @Slot(str)
    def cmd(self, line):
        if self.alive():
            self.proc.write((line + "\n").encode())

    @Slot(int, int)
    def setSize(self, w, h):
        w, h = max(16, int(w)), max(16, int(h))
        if (w, h) != self.size:
            self.size = (w, h)
            self.cmd("size %d %d" % (w, h))

    def show(self, directory):
        """put a run in this pane (live, its newest model)"""
        directory = os.path.abspath(directory)
        if not read_state(directory).get("runId"):
            self.host.set_status("pane %d: run is starting, try again shortly" % self.number)
            return False
        self._end_replay()
        if directory != self.run:
            import time
            self.since, self.owner_session = time.time(), ""
        self.run = directory
        self.displayed_revision = 0
        self.saw_building = False
        if not self.alive():
            p = QProcess(self)
            p.setProcessEnvironment(self.env)
            p.setProgram(self.exe)
            p.setArguments(["--serve", "--watch-run", directory, "--size", "%dx%d" % self.size])
            p.setProcessChannelMode(QProcess.SeparateChannels)
            p.readyReadStandardOutput.connect(lambda p=p: self._output(p))
            p.readyReadStandardError.connect(lambda p=p: p.readAllStandardError())
            p.finished.connect(lambda code, _st, p=p: self._finished(p, code))
            self.buf = b""
            self.proc = p
            p.start()
        else:
            self.cmd("follow " + directory)
        self.changed.emit()
        return True

    def openModel(self, path, clip="", session=""):
        """open a model (playing clip) in this pane, so a session can work here without taking pane 1"""
        self._end_replay()
        self.touch(session)
        self.owner_session = session
        self.run = ""
        self.displayed_revision = 0
        p, self.proc = self.proc, None
        if p is not None and p.state() != QProcess.NotRunning:
            p.write(b"quit\n")
            if not p.waitForFinished(1500):
                p.kill()
        self.model = path
        p = QProcess(self)
        p.setProcessEnvironment(self.env)
        p.setProgram(self.exe)
        p.setArguments(["--serve", "--size", "%dx%d" % self.size, "--model", path] + (["--clip", clip] if clip else []))
        p.setProcessChannelMode(QProcess.SeparateChannels)
        p.readyReadStandardOutput.connect(lambda p=p: self._output(p))
        p.readyReadStandardError.connect(lambda p=p: p.readAllStandardError())
        p.finished.connect(lambda code, _st, p=p: self._finished(p, code))
        self.buf = b""
        self.proc = p
        p.start()
        self.changed.emit()

    @Slot()
    def clear(self):
        """empty the pane (its viewer stays up, showing nothing)"""
        # stop the viewer outright: a --model viewer can't be told to forget its model, and an older
        # --watch-run viewer kept the last revision on screen under the "pane is empty" sign.
        # show()/openModel() start a fresh one when something is put here again.
        self.shut()

    def shut(self):
        """the layout hid this pane: stop its viewer, free the GPU"""
        self._end_replay()
        self.run = ""
        self.model = ""
        self.owner_session, self.since = "", 0.0
        self.displayed_revision = 0
        p, self.proc = self.proc, None
        if p is not None and p.state() != QProcess.NotRunning:
            p.write(b"quit\n")
            if not p.waitForFinished(1500):
                p.kill()
        if self.shm is not None:
            self.shm.close()
        self.shm, self.shm_path, self.image = None, "", None
        self.changed.emit()
        self.frameReady.emit()

    def _finished(self, p, code):
        if p is self.proc:
            self.proc = None
            if self.run or self.model:
                self.host.set_status("pane %d: viewer exited (%d)" % (self.number, code))

    def _output(self, p):
        if p is not self.proc:
            p.readAllStandardOutput()
            return
        self.buf += bytes(p.readAllStandardOutput())
        *lines, self.buf = self.buf.split(b"\n")
        frame = None
        for raw in lines:
            if not raw.startswith(b"@"):
                continue
            try:
                ev = json.loads(raw[1:])
            except ValueError:
                continue
            kind = ev.get("ev")
            if kind == "frame":
                frame = ev
            elif kind == "ready":
                self.info = ev
                self.cmd("size %d %d" % self.size)
                for line in self.host.pane_setup():
                    self.cmd(line)
            elif kind == "ai-activity" and self.run:
                self.host.live_states[self.run] = ev
                self._maybe_send_off(ev)
            elif kind == "ai-preview":
                self.stepChange = step_change_words(ev)
                self.displayed_revision = ev.get("revision", 0)
                part_focus.follow(self, self.run, self.displayed_revision, self._replay)   # the part this step touched glows
                if self._replay:
                    self._replay = dict(self._replay, shown=self.displayed_revision)
                    self.replayChanged.emit()
                self.changed.emit()
            elif kind == "sent-off":
                if self.run and not self._replay:
                    # the sticker has gone to the folder: the finished model comes back and stays in the pane
                    self.host.set_status("pane %d: finished %s" % (self.number, os.path.basename(self.run)))
                    self.cmd("follow " + self.run)
                    self.changed.emit()
                    self.host.scanRuns()
            elif kind == "face":
                self.host.on_face(self.number, ev)
            elif kind == "ai-preview-error":
                self.host.set_status("pane %d preview: %s" % (self.number, ev.get("msg", "?")))
        if frame is not None:
            self._frame(frame)

    def _frame(self, ev):
        w, h = ev["w"], ev["h"]
        path = self.info.get("shm", "")
        try:
            if self.shm is None or self.shm_path != path or len(self.shm) != w * h * 4:
                if self.shm is not None:
                    self.shm.close()
                with open(path, "rb") as f:
                    self.shm = mmap.mmap(f.fileno(), w * h * 4, access=mmap.ACCESS_READ)
                self.shm_path = path
            data = self.shm[:w * h * 4]
            self.image = QImage(data, w, h, w * 4, QImage.Format_RGBX8888).copy()
            if ev.get("card") and self.run:
                self.host.paneSticker(self, data, w, h)
        except (OSError, ValueError) as e:
            self.host.set_status("pane %d frame: %s" % (self.number, e))
        self.cmd("ack")
        self.cam = (ev.get("yaw", 0), ev.get("pitch", 0))
        self.frameReady.emit()

    def _maybe_send_off(self, ev):
        """the run in this pane completed while it was watched: let it sit a moment, send it off, empty the pane"""
        state = ev.get("state")
        if state not in TERMINAL:
            self.saw_building = True
        elif state == "completed" and self.saw_building:
            self.saw_building = False
            d = self.run
            QTimer.singleShot(self.SEND_OFF_DELAY_MS, lambda: self.cmd("sendoff") if self.run == d and not self._replay else None)

    # ---- replay and notes, this pane's own -----------------------------------------------------
    def _replay_step(self, at):
        steps = self._replay.get("steps", [])
        if not steps:
            return {}
        at = max(0, min(len(steps) - 1, at))
        st = steps[at]
        return {"at": at, "rev": st["rev"], "op": st["op"], "code": live_runs.script_line(self._replay["dir"], st["op"]),
                "say": live_runs.plain_words.say(live_runs.script_line(self._replay["dir"], st["op"]) or st["op"].split(": ", 1)[-1])}

    def _replay_set(self, at, **extra):
        step = self._replay_step(at)
        if step:
            self._replay = dict(self._replay, **step, **extra)
            self.cmd("seek %d" % step["rev"])
            self.replayChanged.emit()

    @Slot(str)
    def replayRun(self, directory):
        directory = os.path.abspath(directory)
        steps = live_runs.steps(directory)
        if not steps:
            self.host.set_status("nothing kept to replay for " + os.path.basename(directory))
            return
        if self.run != directory and not self.show(directory):
            return
        self._replay = {"dir": directory, "steps": steps, "count": len(steps), "playing": True, "shown": 0,
                        "notes": live_runs.notes(directory), "live": self.host.run_live(directory)}
        self._replay_set(0)
        self.replayTimer.start()
        self.host.set_status("pane %d: replaying %s, %d steps" % (self.number, os.path.basename(directory), len(steps)))

    @Slot(str, int)
    def replayAt(self, directory, rev):
        """a step clicked in the watch panel: the replay opens paused on that step (a note or REWIND HERE goes from there)"""
        self.replayRun(directory)
        r = self._replay
        if not r or r.get("dir") != os.path.abspath(directory):
            return
        self.replayTimer.stop()
        at = next((i for i, st in enumerate(r["steps"]) if st["rev"] == rev), r["count"] - 1)
        self._replay_set(at, playing=False)

    def _replay_tick(self):
        r = self._replay
        if not r or not r.get("playing"):
            self.replayTimer.stop()
            return
        if r.get("shown") != r.get("rev"):
            return
        if r["at"] >= r["count"] - 1:
            fresh = live_runs.steps(r["dir"])
            if len(fresh) > r["count"]:
                self._replay = dict(r, steps=fresh, count=len(fresh))
            else:
                self._replay = dict(r, playing=False)
                self.replayTimer.stop()
                self.replayChanged.emit()
                return
        self._replay_set(self._replay["at"] + 1)

    @Slot(int)
    def replaySeek(self, at):
        if self._replay:
            self.replayTimer.stop()
            self._replay_set(at, playing=False)

    @Slot()
    def replayToggle(self):
        r = self._replay
        if not r:
            return
        if r.get("playing"):
            self.replayTimer.stop()
            self._replay = dict(r, playing=False)
            self.replayChanged.emit()
        else:
            self._replay_set(0 if r["at"] >= r["count"] - 1 else r["at"], playing=True)
            self.replayTimer.start()

    @Slot()
    def replayRewind(self):
        if self._replay and self.host.rewind_run(self._replay):
            self.replayLive()

    @Slot()
    def replayLive(self):
        self._end_replay()
        self.cmd("seek 0")

    def _end_replay(self):
        if self._replay:
            self.replayTimer.stop()
            self._replay = {}
            self.replayChanged.emit()

    @Slot(str)
    def addNote(self, text):
        """a note on the step on screen in this pane, kept with this pane's run (geppetto-notes reads it)"""
        r = self._replay
        if not r or not text.strip():
            return
        try:
            live_runs.add_note(r["dir"], r["rev"], r["op"], text)
        except (OSError, ValueError) as e:
            self.host.set_status("note: " + str(e))
            return
        self.host.note_sent(r, text)
        self._replay = dict(r, notes=live_runs.notes(r["dir"]))
        self.replayChanged.emit()


def step_change_words(ev):
    """What a shown step did, in plain words: "added: tail fur · edited: head, body" (the pane flashes the same parts)."""
    said = []
    for key, word in (("added", "added:"), ("changed", "edited:")):
        names = ev.get(key) or []
        if names:
            said.append(word + " " + ", ".join(names[:4]) + (" +%d" % (len(names) - 4) if len(names) > 4 else ""))
    if ev.get("removed"):
        said.append("removed: %d part(s)" % ev["removed"])
    return " · ".join(said)
