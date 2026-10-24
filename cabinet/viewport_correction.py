"""Viewport-local review holds. Never route feedback through the global chat."""
import os
import json
import uuid

from PySide6.QtCore import QObject, Property, Signal, Slot

import live_runs


class ViewportCorrections(QObject):
    changed = Signal()

    def __init__(self, host):
        super().__init__(host)
        self.host = host
        self._sessions = {}
        host.runsChanged.connect(self.refresh)

    sessions = Property("QVariantMap", lambda self: self._sessions, notify=changed)

    def target(self, pane):
        if pane == 1:
            return self.host.watching
        if 2 <= pane <= 4:
            return self.host.panes[pane - 2].run
        return ""

    def publish(self, pane, **fields):
        key = str(pane)
        self._sessions[key] = dict(self._sessions.get(key, {}), **fields)
        self.changed.emit()

    def viewer(self, pane):
        return self.host if pane == 1 else self.host.panes[pane - 2]

    def valid(self, pane):
        session = self._sessions.get(str(pane), {})
        if not session.get("dir") or self.target(pane) != session["dir"]:
            raise ValueError("This viewport changed runs. Feedback was not sent.")
        state = live_runs.read_state(session["dir"])
        if state.get("runId") != session["runId"] or state.get("state") in live_runs.TERMINAL:
            raise ValueError("This run has ended. Its model and feedback are kept.")
        return session, state

    @Slot(int, result=bool)
    def stop(self, pane):
        root = self.target(pane)
        if not root:
            return False
        try:
            state = live_runs.read_state(root)
            if not self.host.run_live(root):
                raise ValueError("No active build in this viewport.")
            old = self._sessions.get(str(pane), {})
            if old.get("dir") == root and old.get("holding") and old.get("runId") == state.get("runId"):
                return True             # opening the chat again must never toggle pause
            command = live_runs.send_command(root, "review")
            same = old.get("dir") == root and old.get("runId") == state["runId"]
            self.publish(pane, dir=root, runId=state["runId"], command=command,
                         holding=True, ready=False, sent=False, error="",
                         messages=old.get("messages", []) if same else [],
                         writtenBatches=old.get("writtenBatches", []) if same else [],
                         queued=[], queueRevision=None, applying=[],
                         revision=0, pinned=False, legacy=False, correctable=True, pendingDirection="", steps=[],
                         status="Stopping after the current operation…")
            return True
        except (OSError, ValueError) as error:
            self.publish(pane, dir=root, holding=False, ready=False, error=str(error), status=str(error))
            return False

    @Slot()
    def refresh(self):
        for key, session in list(self._sessions.items()):
            if not session.get("holding"):
                continue
            try:
                _, state = self.valid(int(key))
                control = live_runs.control_state(state)
                if (not session.get("legacy") and control.get("commandId") == session.get("command")
                        and control.get("outcome") == "unknown-command"):
                    # Already-running processes have the old code loaded. Hold them
                    # using their existing protocol, but never promise fix-first ordering.
                    live_runs.send_command(session["dir"], "pause")
                    self.publish(int(key), legacy=True,
                                 status="This run uses an older runner. Requesting a safe hold…")
                    continue
                if session.get("pendingDirection"):
                    outcome = self.direction_outcome(session, state)
                    if outcome == "accepted":
                        live_runs.send_command(session["dir"], "resume")
                        self.viewer(int(key)).cmd("seek 0")
                        ids = {m["id"] for m in session.get("applying", [])}
                        messages = [dict(m, status="sent") if m.get("id") in ids else m for m in session["messages"]]
                        self.publish(int(key), pendingDirection="", holding=False, ready=False, sent=True,
                                     applying=[], messages=messages,
                                     status="Correction accepted. Continuing with your feedback.")
                    elif outcome:
                        batch = session.get("applying", [])
                        ids = {m["id"] for m in batch}
                        messages = [dict(m, status="queued") if m.get("id") in ids else m for m in session["messages"]]
                        self.publish(int(key), pendingDirection="", ready=True, error=outcome,
                                     queued=batch, applying=[], messages=messages,
                                     status="The builder rejected this correction: " + outcome + ". The build stays held.")
                    continue
                if control.get("paused") and not session.get("ready"):
                    steps = live_runs.steps(session["dir"])
                    revision = (state.get("snapshot") or {}).get("revision", 0)
                    viewer = self.viewer(int(key))
                    if not session.get("pinned"):
                        viewer.cmd("seek %d" % revision)
                        self.publish(int(key), pinned=True, revision=revision, steps=steps,
                                     status="Showing the held step…")
                    if viewer.displayed_revision == revision:
                        correctable = not (live_runs.run_meta(session["dir"]).get("script")
                                           and control.get("scriptReview") is False)
                        self.publish(int(key), ready=True, correctable=correctable, error="", status=(
                            "Held safely. This older run cannot apply corrections before its queued steps. Correction support is available for new runs."
                            if session.get("legacy") else
                            "Held safely. This script was not opened for corrections. You can continue without changes."
                            if not correctable else "Build held. What should change?"))
            except (OSError, ValueError) as error:
                if session.get("error") != str(error):
                    self.publish(int(key), ready=False, error=str(error), status=str(error))

    def direction_outcome(self, session, state):
        """Do not release a native builder until it accepts this specific direction."""
        ident = session["pendingDirection"]
        control = live_runs.control_state(state)
        if control.get("commandId") == ident:
            return control.get("outcome", "")
        # Another control may already have published. Recover our durable ack from
        # a bounded journal tail instead of treating an unrelated command as ours.
        try:
            with open(os.path.join(session["dir"], ".live", "events.jsonl"), "rb") as stream:
                stream.seek(max(0, os.fstat(stream.fileno()).st_size - 65536))
                lines = stream.read().splitlines()
        except FileNotFoundError:
            return ""
        for line in reversed(lines):
            try:
                event = json.loads(line)
                if event.get("runId") == session["runId"]:
                    control = live_runs.control_state(event)
                    if control.get("commandId") == ident:
                        return control.get("outcome", "")
            except (ValueError, AttributeError):
                pass
        return ""

    @Slot(int, int, result=bool)
    def preview(self, pane, revision):
        try:
            session, _ = self.valid(pane)
            if not session.get("ready") or not any(s["rev"] == revision for s in session["steps"]):
                raise ValueError("Wait for a saved step before previewing it.")
            if session.get("queued") and revision != session.get("queueRevision"):
                raise ValueError("Apply or remove queued messages before changing their saved step.")
            self.viewer(pane).cmd("seek %d" % revision)
            return True
        except (OSError, ValueError) as error:
            self.publish(pane, error=str(error), status=str(error))
            return False

    @Slot(int, int, str, result=bool)
    def send(self, pane, revision, text):
        """Protocol-level single approved correction; UI uses queue/apply instead."""
        return self._send(pane, revision, text)

    def _send(self, pane, revision, text, messages=None, batch_id=None):
        try:
            session, state = self.valid(pane)
            if session.get("legacy"):
                raise ValueError("This run uses an older runner and cannot guarantee correction-first ordering.")
            if session.get("correctable") is False:
                raise ValueError("This script was not opened for corrections.")
            if session.get("queued") and messages is None:
                raise ValueError("Apply the complete queued stack instead of sending a separate correction.")
            if not session.get("ready") or not live_runs.control_state(state).get("paused"):
                raise ValueError("Wait for the current operation to finish before sending.")
            text = text.strip()
            if not text or len(text) > 1600:
                raise ValueError("Use 1–1600 characters for the correction.")
            steps = live_runs.steps(session["dir"])
            step = next((s for s in steps if s["rev"] == revision), None)
            if revision and not step:
                raise ValueError("That saved step is no longer available.")
            root = session["dir"]
            script = bool(live_runs.run_meta(root).get("script"))
            if revision != session["revision"] and not script:
                raise ValueError("This builder can correct the current model, but cannot rewind saved steps.")
            instruction = ("CORRECTION from the viewport: " + text +
                           "\nApply every request in this approved batch to the existing model in this same run before continuing."
                           " Do not rebuild from scratch. Follow the Geppetto live-corrections workflow.")
            history = messages if messages is not None else session["messages"] + [{"role": "you", "text": text, "status": "sent"}]
            written = session.get("writtenBatches", [])
            if batch_id is None or batch_id not in written:
                if revision != session["revision"]:
                    live_runs.rewind_open(root, revision, step.get("op", ""))
                live_runs.add_note(root, revision, step.get("op", "") if step else "", instruction)
                if batch_id is not None:
                    self.publish(pane, writtenBatches=written + [batch_id])
            # Script modellers receive the durable note through their existing hook /
            # geppetto-notes monitor and send `more`. Native AI gets a direction too.
            if not script:
                ident = live_runs.send_command(root, "instruct", text=instruction)
                self.publish(pane, pendingDirection=ident, ready=False, error="",
                             messages=history,
                             status="Waiting for this builder to accept your correction…")
                return True
            live_runs.send_command(root, "resume")
            self.viewer(pane).cmd("seek 0")
            self.publish(pane, holding=False, sent=True, error="",
                          messages=history,
                         status="Sent to this run’s modeller. Waiting for its fix." if script else "Correction sent. Continuing with your feedback.")
            return True
        except (OSError, ValueError) as error:
            self.publish(pane, error=str(error), status=str(error))
            return False

    @staticmethod
    def batch_text(messages):
        return "\n\n".join("%d. %s" % (i + 1, m["text"]) for i, m in enumerate(messages))

    @Slot(int, int, str, result=bool)
    def queue(self, pane, revision, text):
        """Stack explicitly sent messages locally; never touch the modeller's inbox."""
        try:
            session, state = self.valid(pane)
            if not session.get("holding") or not session.get("ready") or session.get("pendingDirection"):
                raise ValueError("Wait for the held step before adding a message.")
            if session.get("legacy") or session.get("correctable") is False:
                raise ValueError("This runner does not support correction-first editing.")
            if not live_runs.control_state(state).get("paused"):
                raise ValueError("The build is no longer held. Stop it before adding a correction.")
            text = text.strip()
            if not text:
                raise ValueError("Write a message before sending.")
            queued = session.get("queued", [])
            if queued and revision != session.get("queueRevision"):
                raise ValueError("Queued messages belong to one saved step. Apply or remove them before changing steps.")
            if revision and not any(s["rev"] == revision for s in session.get("steps", [])):
                raise ValueError("That saved step is not available.")
            message = {"id": uuid.uuid4().hex, "role": "you", "text": text, "status": "queued", "revision": revision}
            batch = queued + [message]
            if len(batch) > 16 or len(self.batch_text(batch)) > 1600:
                raise ValueError("This correction batch is full (16 messages / 1600 characters). Apply it or remove a queued message first.")
            self.publish(pane, queued=batch, queueRevision=revision,
                         messages=session["messages"] + [message], error="",
                         status="%d message%s queued here. Nothing is sent to the modeller until Apply & continue." % (len(batch), "" if len(batch) == 1 else "s"))
            return True
        except (OSError, ValueError) as error:
            self.publish(pane, error=str(error), status=str(error))
            return False

    @Slot(int, str, result=bool)
    def removeQueued(self, pane, ident):
        try:
            session, _ = self.valid(pane)
            queued = session.get("queued", [])
            if session.get("pendingDirection") or not any(m["id"] == ident for m in queued):
                raise ValueError("Only unsent queued messages can be removed.")
            queued = [m for m in queued if m["id"] != ident]
            self.publish(pane, queued=queued, queueRevision=session.get("queueRevision") if queued else None,
                         messages=[m for m in session["messages"] if m.get("id") != ident], error="",
                         status="%d message(s) queued. The build stays held." % len(queued))
            return True
        except (OSError, ValueError) as error:
            self.publish(pane, error=str(error), status=str(error))
            return False

    @Slot(int, result=bool)
    def apply(self, pane):
        try:
            session, _ = self.valid(pane)
            batch = session.get("queued", [])
            if not batch or session.get("pendingDirection"):
                raise ValueError("Queue a message before applying the correction.")
            ids = {m["id"] for m in batch}
            script = bool(live_runs.run_meta(session["dir"]).get("script"))
            messages = [dict(m, status="sent" if script else "pending") if m.get("id") in ids else m for m in session["messages"]]
            if not self._send(pane, session["queueRevision"], self.batch_text(batch), messages=messages,
                              batch_id=":".join(m["id"] for m in batch)):
                return False
            self.publish(pane, queued=[], applying=[] if script else batch)
            return True
        except (OSError, ValueError) as error:
            self.publish(pane, error=str(error), status=str(error))
            return False

    @Slot(int, result=bool)
    def resume(self, pane):
        """Explicit continue without a correction; closing the panel never calls this."""
        try:
            session, state = self.valid(pane)
            if session.get("queued") or session.get("pendingDirection"):
                raise ValueError("Apply or remove the queued messages before continuing.")
            if not session.get("ready"):
                raise ValueError("Still waiting for the build to stop.")
            root = session["dir"]
            if live_runs.run_meta(root).get("script") and session.get("correctable", True) and not session.get("legacy"):
                # Wake the open script inbox. This no-op runs before its preserved tail.
                directory = os.path.join(root, ".live", "inbox")
                live_runs.makedirs_private(directory)
                import time
                import uuid
                name = "%019d%s.txt" % (time.time_ns() // 100 + 621355968000000000, uuid.uuid4().hex[:6])
                path = os.path.join(directory, name)
                with open(path + ".tmp", "x") as stream:
                    stream.write("# continue after viewport review\n")
                os.replace(path + ".tmp", path)
            live_runs.send_command(root, "resume")
            self.viewer(pane).cmd("seek 0")
            self.publish(pane, holding=False, ready=False, error="", status="Build continued.")
            return True
        except (OSError, ValueError) as error:
            self.publish(pane, error=str(error), status=str(error))
            return False
