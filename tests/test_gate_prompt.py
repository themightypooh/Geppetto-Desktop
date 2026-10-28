"""Lane D: provider-neutral modeling preparation reaches every tool-enabled seat,
and ordinary/Plan turns never acquire it. Fixture-only: no provider, no chest,
no display, no real cabinet/confirm or muse-seat files.

Seats covered by injection point (not by a live provider call):
- Claude card: claude_link.PROMPT / ClaudeLink._start(mode='build')
- Claude CLI and Grok CLI: chat._chat_system(mode='build') via CLAUDE_CHAT
  (GROK_CHAT is the same object)
- Codex/OpenCode and Muse bridge (roundtable-cli): _roundtable_cli build
  base_sys starts with CLAUDE_CHAT, so the same BUILD_PREP_PROMPT rides along.
  The Popen path itself is not executed here; see test_chat_modes.py for
  roundtable mode/workspace routing.
API (anthropic/google/ollama/openai) seats carry no execution tools in chat.py
and are gate-refused by build_execution.checked_stream when they lack a
receipt, so build preparation is intentionally not injected there: a prompt
alone must never be mistaken for build permission.
"""
import os
import sys
import tempfile
import unittest
from pathlib import Path

CABINET = Path(__file__).resolve().parents[1] / "cabinet"
sys.path.insert(0, str(CABINET))

import chat  # noqa: E402
import chat_phase  # noqa: E402
import claude_link  # noqa: E402


class BuildPrepContentTests(unittest.TestCase):
    def test_prep_keeps_execution_protocol_without_prescribing_artistic_workflow(self):
        prep = chat_phase.BUILD_PREP_PROMPT
        for needle in (
            "preflight it BEFORE the toast/card is shown",
            "file ONE intent for the exact script/subject/pane",
            "execute that exact approved script promptly",
            "do not research, rewrite, or substitute it",
            "No means no build",
        ):
            self.assertIn(needle, prep, needle)
        self.assertIn('choose your own modeling approach', prep)
        for removed in ('short internal tool plan', 'silhouette and proportions',
                        'Profile/Spline/Blob', 'flat-coloured Primitive', 'blobs'):
            self.assertNotIn(removed, prep)

    def test_phase_announcements_are_not_injected_but_optional_reports_still_parse(self):
        self.assertEqual(chat_phase.PHASE_PROMPT, '')
        self.assertEqual(chat_phase.reported_phase([
            {'role': 'claude', 'text': 'Phase: Modeling'}]), 'modeling')

    def test_prep_forbids_silent_retry_and_second_turn_after_stop_or_close(self):
        prep = chat_phase.BUILD_PREP_PROMPT
        self.assertIn("stop at once", prep)
        self.assertIn("do not silently retry, restart, or start a second or nested", prep)
        self.assertIn("the user chooses any second attempt", prep)

    def test_prep_is_provider_neutral(self):
        prep = chat_phase.BUILD_PREP_PROMPT
        for name in ("claude", "codex", "grok", "opencode", "openai", "anthropic"):
            self.assertNotIn(name, prep.lower(), name)
        for setting in ("temperature", "effort", "max_tokens", "xhigh"):
            self.assertNotIn(setting, prep.lower(), setting)

    def test_plan_boundary_carries_no_build_prep(self):
        boundary = chat_phase.PLAN_BOUNDARY_PROMPT
        self.assertIn("Do not file a Geppetto model intent", boundary)
        self.assertIn("do not claim build permission", boundary)
        self.assertNotIn("BUILD_PREP", boundary)
        self.assertNotIn(chat_phase.BUILD_PREP_PROMPT, boundary)


class SeatPropagationTests(unittest.TestCase):
    def test_claude_and_grok_cli_share_build_prep_in_build_mode_only(self):
        self.assertIs(chat.GROK_CHAT, chat.CLAUDE_CHAT)
        self.assertIn(chat_phase.BUILD_PREP_PROMPT, chat.CLAUDE_CHAT)
        build = chat._chat_system({}, mode="build")
        self.assertIn(chat_phase.BUILD_PREP_PROMPT, build)
        plan = chat._chat_system({}, mode="plan")
        self.assertNotIn(chat_phase.BUILD_PREP_PROMPT, plan)

    def test_ordinary_chat_default_is_plan_and_has_no_build_prep(self):
        # Fail closed: a caller that forgets mode must not get build preparation.
        default = chat._chat_system({})
        self.assertNotIn(chat_phase.BUILD_PREP_PROMPT, default)

    def test_plan_system_states_no_intent_and_no_build_permission(self):
        self.assertIn("Do not file a Geppetto model intent", chat.SYSTEM_PLAN)
        self.assertIn("Do not file a Geppetto model intent or claim build permission", chat.SYSTEM_PLAN)
        self.assertNotIn(chat_phase.BUILD_PREP_PROMPT, chat.SYSTEM_PLAN)

    def test_roundtable_build_base_is_the_same_claude_chat(self):
        # _roundtable_cli build base_sys = CLAUDE_CHAT + SYSTEM_BUILD, so the
        # Codex/OpenCode/Muse-bridge seats inherit this exact preparation.
        self.assertIn(chat_phase.BUILD_PREP_PROMPT, chat.CLAUDE_CHAT)

    def test_card_build_gets_prep_and_card_plan_gets_boundary_not_prep(self):
        with tempfile.TemporaryDirectory() as tmp:
            prompts = {}

            class FakeProc:
                def __init__(self):
                    import io
                    self.stderr = io.StringIO("")
                    self.stdout = io.StringIO("")
                    self.stdin = io.StringIO("")

            def fake_spawn(args, **kwargs):
                idx = args.index("--append-system-prompt")
                prompts["prompt"] = args[idx + 1]
                return FakeProc()

            for mode, want_prep, want_boundary in (
                ("build", True, False),
                ("plan", False, True),
            ):
                link = claude_link.ClaudeLink(
                    spawn=fake_spawn,
                    state_path=os.path.join(tmp, "state-%s.json" % mode),
                    cwd=os.path.join(tmp, "cwd-" + mode),
                )
                link.mode = mode
                link._start()
                prompt = prompts["prompt"]
                self.assertEqual(chat_phase.BUILD_PREP_PROMPT in prompt, want_prep, mode)
                self.assertEqual(chat_phase.PLAN_BOUNDARY_PROMPT in prompt, want_boundary, mode)
                if mode == "plan":
                    self.assertIn("read-only Plan mode", prompt)


if __name__ == "__main__":
    unittest.main()
