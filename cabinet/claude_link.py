#!/usr/bin/env python3
"""Claude Code in the chest: the CLAUDE card talks to a real Claude Code session.

One `claude -p` process per chest, driven over its stream-json protocol (the same one the Agent SDK
uses): your messages go in on stdin, its replies, tool calls and permission questions come out on
stdout. The conversation is kept (its session id in ~/.cache/geppetto/claude-chat.json), so closing
the chest and opening it again carries on where it was. NEW starts a fresh one.

STOP interrupts the turn, gates the session (claude_gate.py refuses its next tool call) and cancels
every live run it started. PAUSE gates it to wait at its next tool call and pauses those runs.
Nothing here knows Qt: host.py turns `on_change` into a signal.
"""
import json
import os
import chat_workspace
import subprocess
import threading
import time
import uuid

import claude_gate
from chat_phase import PHASE_PROMPT

STATE = os.path.expanduser("~/.cache/geppetto/claude-chat.json")
PROJECT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLAUDE = os.environ.get("GEPPETTO_CLAUDE", "claude")
PROMPT = ("You are talking to the user through the assistant card in the Geppetto chest, right over the viewport "
          "they are watching. Keep replies short: a few lines, no tables. Do the work yourself with "
          "geppetto-live so it shows in the chest. If a hook says the user pressed STOP, stop at once.") + PHASE_PROMPT
KEEP = 200

# --- Locked-down spawn environment (audit C2); enabled by the cabinet Host. ---
# Today _start() hands the child the whole process environment minus CLAUDE_CODE_* names —
# i.e. every provider key the cabinet process holds. The lockdown replaces that with the
# allowlist below (baseline only, no provider keys; the Claude CLI authenticates from its
# own files under $HOME / $CLAUDE_CONFIG_DIR). Host passes lockdown=True. The constructor's
# compatibility default remains opt-in for other callers; no claim of filesystem sandboxing.
# Full spec: CLAUDE_LINK_LOCKDOWN.spec.md in this folder.
# Enable per-process with GEPPETTO_CLAUDE_LINK_LOCKDOWN=1, or per-link with lockdown=True.
LOCKDOWN_FLAG = "GEPPETTO_CLAUDE_LINK_LOCKDOWN"
LOCKDOWN_ENV_ALLOW = ("PATH", "HOME", "USER", "LOGNAME", "SHELL", "TERM", "LANG", "LC_ALL",
                      "LC_CTYPE", "TZ", "TMPDIR", "DISPLAY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR",
                      "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME",
                      "DBUS_SESSION_BUS_ADDRESS", "CLAUDE_CONFIG_DIR", "SSH_AUTH_SOCK")


def locked_down_env(source=None):
    """The environment a locked-down ClaudeLink child gets: LOCKDOWN_ENV_ALLOW, nothing else."""
    source = os.environ if source is None else source
    return {k: source[k] for k in LOCKDOWN_ENV_ALLOW if k in source}


def tool_line(name, data):
    """One short line for a tool call: what it does, not its whole input."""
    data = data if isinstance(data, dict) else {}
    what = data.get("description") or data.get("command") or data.get("file_path") or data.get("pattern") \
        or data.get("url") or data.get("prompt") or ""
    what = " ".join(str(what).split())
    return (name + "  " + what)[:160]


def _clip(text, lines=14, width=200):
    rows = [r[:width] for r in str(text).splitlines()]
    return "\n".join(rows[:lines] + (["… %d more lines" % (len(rows) - lines)] if len(rows) > lines else []))


def tool_detail(name, data):
    """What the tool is doing, in full enough to follow: the command, the edit, the file it writes."""
    data = data if isinstance(data, dict) else {}
    if "command" in data:
        return _clip("$ " + str(data["command"]), 20)
    if "old_string" in data or "new_string" in data:
        old = ["- " + r for r in str(data.get("old_string", "")).splitlines()]
        new = ["+ " + r for r in str(data.get("new_string", "")).splitlines()]
        return _clip("\n".join([str(data.get("file_path", ""))] + old[:12] + new[:12]), 26)
    if "content" in data and "file_path" in data:
        return _clip(str(data["file_path"]) + "\n" + str(data["content"]), 16)
    if "file_path" in data:
        return str(data["file_path"])
    if "pattern" in data:
        return " ".join(str(data[k]) for k in ("pattern", "path", "glob") if data.get(k))
    return _clip(json.dumps(data, ensure_ascii=False), 8) if data else ""


def _partial(raw):
    """A tool's input while it is still being written: as much of it as reads."""
    try:
        return json.loads(raw)
    except ValueError:
        pass
    for tail in ('"}', '}', '"]}', ''):
        try:
            return json.loads(raw + tail)
        except ValueError:
            continue
    return None


class ClaudeLink:
    def __init__(self, on_change=lambda: None, spawn=subprocess.Popen, state_path=STATE, cwd=None,
                 lockdown=None):
        self.on_change = on_change
        self.spawn = spawn
        self.state_path = state_path
        self.cwd = cwd or chat_workspace.workspace()
        # Audit C2 lockdown (see top of file): None = follow the GEPPETTO_CLAUDE_LINK_LOCKDOWN flag.
        self.lockdown = (os.environ.get(LOCKDOWN_FLAG) == "1") if lockdown is None else bool(lockdown)
        self.messages = []          # {"role": "you"|"claude"|"tool"|"note"|"ask", "text": ..., ...}
        self.model = ""             # the Claude model (opus, sonnet, haiku); "" = the CLI's default
        self.persona = ""           # extra system prompt for this card (e.g. Zoopa, from the picked model's persona file)
        self.research_policy = ""
        self.status = "idle"        # idle | starting | working | paused | stopped | offline
        self.ask = None             # the permission question waiting on you: {"id", "tool", "text"}
        self.session = self._load().get("session", "")
        self.proc = None
        self.lock = threading.RLock()
        self.live = {}              # (api message id, block index) -> the message being streamed into
        self.tools = {}             # tool_use id -> its "tool" message
        self._last = 0.0
        self._timer = None

    # ---- persistence ------------------------------------------------------------------------
    def _load(self):
        try:
            with open(self.state_path) as stream:
                data = json.load(stream)
            return data if isinstance(data, dict) else {}
        except (OSError, ValueError):
            return {}

    def _save(self):
        # Private (audit G6): the state file names the card's Claude session.
        os.makedirs(os.path.dirname(os.path.abspath(self.state_path)), mode=0o700, exist_ok=True)
        temp = self.state_path + ".tmp"
        fd = os.open(temp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w") as stream:
            json.dump({"session": self.session}, stream)
        os.replace(temp, self.state_path)

    # ---- what you see -------------------------------------------------------------------------
    def _add(self, role, text, **extra):
        with self.lock:
            msg = dict(extra, role=role, text=text)
            self.messages.append(msg)
            del self.messages[:-KEEP]
        self.on_change()
        return msg

    def _changed(self):
        """Streaming text changes many times a second: tell the card at most ~15 times a second."""
        now = time.monotonic()
        if now - self._last > 0.066:
            self._last = now
            self.on_change()
        elif self._timer is None:
            def flush():
                self._timer = None
                self._last = time.monotonic()
                self.on_change()
            self._timer = threading.Timer(0.07, flush)
            self._timer.daemon = True
            self._timer.start()

    def _set(self, status):
        if self.status != status:
            self.status = status
            self.on_change()

    # ---- the process ----------------------------------------------------------------------------
    def _start(self):
        prompt = PROMPT + ("\n\n" + self.persona if self.persona else "")
        if self.research_policy:
            prompt += "\n\n" + self.research_policy
        args = [CLAUDE, "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                "--include-partial-messages",
                "--permission-mode", "auto", "--permission-prompt-tool", "stdio",
                "--append-system-prompt", prompt, "-n", "Zoopa in the Geppetto chest" if self.persona else "Geppetto chest"]
        args += ["--resume", self.session] if self.session else []
        args += ["--model", self.model] if self.model else []
        if self.lockdown:
            env = locked_down_env()
        else:
            env = dict(os.environ)
            for name in [k for k in env if k.startswith(("CLAUDE_CODE_", "CLAUDECODE"))]:
                env.pop(name)       # a chest started from inside a Claude session must not look like its child
        self._set("starting")
        os.makedirs(self.cwd, mode=0o700, exist_ok=True)
        self.proc = self.spawn(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, bufsize=1, cwd=self.cwd, env=env)
        proc = self.proc
        threading.Thread(target=self._read, args=(proc,), daemon=True).start()
        threading.Thread(target=self._errors, args=(proc,), daemon=True).start()
        self._send({"type": "control_request", "request_id": "init-" + uuid.uuid4().hex,
                    "request": {"subtype": "initialize"}})

    def _send(self, obj):
        try:
            self.proc.stdin.write(json.dumps(obj) + "\n")
            self.proc.stdin.flush()
            return True
        except (OSError, ValueError, AttributeError):
            return False

    def _errors(self, proc):
        tail = []
        for line in proc.stderr:
            tail = (tail + [line.rstrip()])[-5:]
        if proc is self.proc and proc.poll() not in (None, 0) and tail:
            self._add("note", "claude exited: " + self._source_error(" / ".join(t for t in tail if t)[:300]))

    def _source_error(self, text):
        """A persona is not a provider or a separate usage allowance."""
        return ("Zoopa · " if self.persona else "") + "Claude CLI · " + (self.model or "default") + ": " + text

    def _read(self, proc):
        for line in proc.stdout:
            try:
                self.handle(json.loads(line))
            except ValueError:
                continue
        if proc is self.proc:
            self.proc = None
            self.ask = None
            self._set("offline" if self.status not in ("stopped",) else "stopped")

    # ---- what comes back --------------------------------------------------------------------
    def handle(self, msg):
        kind = msg.get("type")
        if msg.get("session_id") and msg["session_id"] != self.session:
            self.session = msg["session_id"]
            self._save()
        sub = bool(msg.get("parent_tool_use_id"))      # a subagent's work, shown indented
        if kind == "stream_event":
            self._stream(msg.get("event") or {}, msg.get("api_message_id", ""), sub)
        elif kind == "assistant":
            mid = (msg.get("message") or {}).get("id", "")
            for block in (msg.get("message") or {}).get("content") or []:
                self._final(block, mid, sub)
        elif kind == "user":
            content = (msg.get("message") or {}).get("content")
            extra = msg.get("tool_use_result") if isinstance(msg.get("tool_use_result"), dict) else {}
            for block in content if isinstance(content, list) else []:
                if block.get("type") == "tool_result":
                    self._result(block, extra, sub)
        elif kind == "control_request":
            req = msg.get("request") or {}
            if req.get("subtype") == "can_use_tool":
                self.ask = {"id": msg.get("request_id", ""), "tool": req.get("tool_name", ""),
                            "input": req.get("input") or {},
                            "text": tool_line(req.get("display_name") or req.get("tool_name", ""), req.get("input"))}
                self._add("ask", self.ask["text"])
            else:       # nothing else is answered by the chest: say no rather than leave it hanging
                self._send({"type": "control_response", "response": {
                    "subtype": "error", "request_id": msg.get("request_id", ""), "error": "not supported by the chest"}})
        elif kind == "result":
            if self.status not in ("stopped",):
                self._set("idle")
            if msg.get("is_error") and msg.get("subtype") not in ("success",):
                self._add("note", self._source_error(str(msg.get("result") or msg.get("subtype") or "error")[:300]))
        elif kind == "system" and msg.get("subtype") == "init" and self.status == "starting":
            self._set("working")

    def _stream(self, event, mid, sub):
        kind = event.get("type")
        key = (mid, event.get("index", 0))
        if kind == "content_block_start":
            block = event.get("content_block") or {}
            if block.get("type") == "text":
                self.live[key] = self._add("claude", "", sub=sub, live=True)
            elif block.get("type") == "thinking":
                self.live[key] = self._add("think", "", sub=sub, live=True)
            elif block.get("type") == "tool_use":
                name = block.get("name", "tool")
                self.live[key] = self.tools[block.get("id", "")] = self._add(
                    "tool", name, name=name, id=block.get("id", ""), detail="", raw="", sub=sub, live=True)
        elif kind == "content_block_delta" and key in self.live:
            delta, m = event.get("delta") or {}, self.live[key]
            if delta.get("type") == "text_delta":
                m["text"] += delta.get("text", "")
            elif delta.get("type") == "thinking_delta":
                m["text"] += delta.get("thinking", "")
            elif delta.get("type") == "input_json_delta":
                m["raw"] += delta.get("partial_json", "")
                data = _partial(m["raw"])
                m["detail"] = tool_detail(m["name"], data) if isinstance(data, dict) else _clip(m["raw"], 8)
                m["text"] = tool_line(m["name"], data)
            self._changed()
        elif kind == "content_block_stop" and key in self.live:
            self.live.pop(key)["live"] = False
            self._changed()

    def _final(self, block, mid, sub):
        """The whole block once it is written: fills in what streaming showed, or adds it."""
        kind = block.get("type")
        if kind == "tool_use":
            m = self.tools.get(block.get("id", ""))
            name = block.get("name", "tool")
            if m is None:
                m = self.tools[block.get("id", "")] = self._add("tool", "", name=name, id=block.get("id", ""), sub=sub)
            m.update(text=tool_line(name, block.get("input")), detail=tool_detail(name, block.get("input")), raw="")
            self.on_change()
            return
        role = {"text": "claude", "thinking": "think"}.get(kind)
        text = (block.get("text") if kind == "text" else block.get("thinking")) or ""
        if not role or not text.strip():
            return
        streamed = [m for (k, i), m in self.live.items() if k == mid and m["role"] == role]
        done = [m for m in self.messages[-12:] if m.get("role") == role and m.get("text") == text]
        if streamed:
            streamed[0]["text"] = text
        elif not (mid and done):
            self._add(role, text.strip(), sub=sub)
        self.on_change()

    def _result(self, block, extra, sub):
        """A tool's output under the tool: stdout, stderr, or why it was refused."""
        text = block.get("content")
        if isinstance(text, list):
            text = "\n".join(c.get("text", "") for c in text if isinstance(c, dict) and c.get("type") == "text")
        out = str(extra.get("stdout") or "") if extra.get("stdout") or extra.get("stderr") else str(text or "")
        if extra.get("stderr"):
            out = (out + "\n" if out else "") + str(extra["stderr"])
        error = bool(block.get("is_error"))
        if not out.strip():
            out = "(no output)" if not error else "refused"
        tool = self.tools.get(block.get("tool_use_id", ""))
        if tool is not None:
            tool["done"] = True
            tool["error"] = error
        self._add("note" if error else "out", ("refused: " if error else "") + _clip(out.strip(), 40, 240),
                  full=out[:20000], sub=sub, error=error)

    # ---- what you do --------------------------------------------------------------------------
    def send(self, text):
        text = (text or "").strip()[:8000]
        if not text:
            return False
        if self.session:
            claude_gate.clear_gate(self.session)      # a new message lifts STOP and PAUSE
        if self.proc is None or self.proc.poll() is not None:
            self._start()
        self._add("you", text)
        self._set("working")
        return self._send({"type": "user", "message": {"role": "user", "content": text},
                           "parent_tool_use_id": None, "session_id": self.session})

    def answer(self, allow):
        ask, self.ask = self.ask, None
        if not ask:
            return
        if allow:
            response = {"behavior": "allow", "updatedInput": ask["input"]}
        else:
            response = {"behavior": "deny", "message": "po said no in the Geppetto chest"}
        self._send({"type": "control_response", "response": {"subtype": "success", "request_id": ask["id"], "response": response}})
        self._add("note", ("allowed: " if allow else "denied: ") + ask["text"][:120])

    def runs(self):
        """Live run processes this session started (geppetto-live records them)."""
        found = []
        for name in os.listdir(claude_gate.RUNS) if os.path.isdir(claude_gate.RUNS) else []:
            if not name.endswith(".json"):
                continue
            data = claude_gate._read(os.path.join(claude_gate.RUNS, name))
            if self.session and data.get("session") == self.session:
                try:
                    pid = int(name[:-5])
                    os.kill(pid, 0)
                    found.append(pid)
                except (ValueError, OSError):
                    pass
        return found

    def stop(self):
        if self.session:
            claude_gate.set_gate(self.session, "stopped", "CLAUDE card")
        for pid in self.runs():
            claude_gate.cancel_pid(pid)
        if self.ask:
            self.answer(False)
        if self.proc is not None:
            self._send({"type": "control_request", "request_id": "stop-" + uuid.uuid4().hex,
                        "request": {"subtype": "interrupt"}})
        self._set("stopped")
        self._add("note", "stopped")

    def pause(self):
        """Toggle: hold Claude at its next tool call and pause its runs, or let both go again."""
        from live_runs import send_command
        paused = self.status == "paused"
        if self.session:
            if paused:
                claude_gate.clear_gate(self.session)
            else:
                claude_gate.set_gate(self.session, "paused", "CLAUDE card")
        for pid in self.runs():
            root = claude_gate.run_root(pid)
            if root:
                try:
                    send_command(root, "resume" if paused else "pause")
                except (OSError, ValueError):
                    pass
        self._set("working" if paused and self.proc is not None else ("idle" if paused else "paused"))

    def set_persona(self, persona):
        """Switch persona: like set_model, the running process goes and the next message restarts
        (resuming the same session) with the new persona in its system prompt."""
        persona = (persona or "").strip()
        if persona == self.persona:
            return
        self.persona = persona
        if self.proc is not None:
            self.close()
            self._set("idle")

    def set_research_policy(self, policy):
        if policy == self.research_policy:
            return
        self.research_policy = policy
        if self.proc is not None:
            self.close()
            self._set("idle")

    def set_model(self, model):
        """Switch Claude models: the running process goes, the conversation stays (the next message
        resumes the same session on the new model)."""
        model = model or ""
        if model == self.model:
            return
        self.model = model
        if self.proc is not None:
            self.close()
            self._set("idle")

    def fresh(self):
        """NEW: forget this conversation; the next message starts another session."""
        self.close()
        self.session = ""
        self._save()
        with self.lock:
            self.messages = []
            self.live, self.tools = {}, {}
        self._set("idle")

    def close(self):
        proc, self.proc = self.proc, None
        if proc is not None:
            try:
                proc.stdin.close()
                proc.terminate()
            except OSError:
                pass


if __name__ == "__main__":      # talk to it from a terminal: python3 claude_link.py "message"
    import sys
    link = ClaudeLink(on_change=lambda: None)
    shown = [0]

    def show():
        for m in link.messages[shown[0]:]:
            print("%-6s %s" % (m["role"], m["text"]))
        shown[0] = len(link.messages)
    link.on_change = show
    link.send(" ".join(sys.argv[1:]) or "say hi in five words")
    while link.status not in ("idle", "offline", "stopped") or link.proc is None and link.status == "starting":
        if link.ask:
            link.answer(input("allow %s? [y/N] " % link.ask["text"]).lower().startswith("y"))
        time.sleep(0.2)
    link.close()
