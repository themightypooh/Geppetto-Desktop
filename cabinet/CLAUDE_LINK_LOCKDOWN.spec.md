# ClaudeLink locked-down spawn — spec (audit C2)

Status: **env-scrubbing implemented in staging, OFF by default. Nothing else in this spec
is shipped.** Joshua must approve before any of it becomes the live behaviour — the working
Zoopa card session runs on this exact path and depends on today's spawn.

## What is implemented (staging `claude_link.py`)

- `locked_down_env(source)` — builds the child environment from `LOCKDOWN_ENV_ALLOW` only.
- `ClaudeLink(..., lockdown=None)` — `None` follows the env flag
  `GEPPETTO_CLAUDE_LINK_LOCKDOWN=1`; `True`/`False` force it per link.
- With lockdown on, `_start()` passes `locked_down_env()` instead of
  `dict(os.environ)` minus `CLAUDE_CODE_*`. With it off, today's behaviour is byte-for-byte
  the old behaviour.
- Unit tests: `tests/test_claude_link_lockdown.py` (allowlist contents; flag default-off;
  spawn receives the scrubbed env only when enabled).

## The locked-down spawn, as specified

**Environment (allowlist, exhaustive):**
`PATH, HOME, USER, LOGNAME, SHELL, TERM, LANG, LC_ALL, LC_CTYPE, TZ, TMPDIR, DISPLAY,
WAYLAND_DISPLAY, XDG_RUNTIME_DIR, XDG_CONFIG_HOME, XDG_DATA_HOME, XDG_CACHE_HOME,
DBUS_SESSION_BUS_ADDRESS, CLAUDE_CONFIG_DIR, SSH_AUTH_SOCK`

Rationale per group: process basics (PATH/HOME/USER/SHELL/TERM), locale/time
(LANG/LC_*/TZ), temp + desktop session (TMPDIR/DISPLAY/WAYLAND_DISPLAY/XDG_*/
DBUS_SESSION_BUS_ADDRESS), the CLI's own config location (CLAUDE_CONFIG_DIR),
git-over-ssh for the tools the session legitimately runs (SSH_AUTH_SOCK).
**Explicitly absent:** every `*_API_KEY` / `*_AUTH_TOKEN` provider credential, and the
inherited `CLAUDE_CODE_*` session-impersonation names (already stripped today).
The Claude CLI needs no API key in env: it authenticates from its own credential file
under `$HOME/.claude` (0600, the CLI's own store — audit OK-3/G4).

**cwd:** unchanged — the repo root (`PROJECT`). The session's work (geppetto-live runs,
build scripts) happens there by design; moving cwd is a product change, not a security fix.

**Permission mode:** unchanged — `--permission-mode auto` with
`--permission-prompt-tool stdio`, i.e. tool use is still gated by the in-card prompt
(the `ask` flow) plus the STOP/PAUSE gate hooks (`claude_gate.py`).

**Not in this implementation (needs its own approval + tests before shipping):**
- An `--allowedTools` list for ClaudeLink comparable to `chat.py`'s `CLAUDE_TOOLS`.
  Today the card session intentionally has the CLI's full tool surface, fenced by the
  permission prompt and the gate; restricting it changes what Zoopa-in-the-chest can do
  mid-build, so it must be decided against real sessions, not in the abstract.
- Any change to `--append-system-prompt` travelling in argv (audit G1 remainder): Claude
  Code also accepts `--append-system-prompt-file` in current versions, but switching the
  live card to it is a behaviour change on this same approval path.

## Rollout, when approved

1. Run one real card session with `GEPPETTO_CLAUDE_LINK_LOCKDOWN=1`: verify login,
   geppetto-live build, a git operation, and STOP/PAUSE all behave as today.
2. If clean, flip the default in `ClaudeLink.__init__` (flag becomes the opt-out).
3. Re-run the cabinet test suite and note the flip in BOARD.md.
