"""The BYO-AI notices (cabinet/ai_notice.py): cost pre-flight + first-send disclosure.

No Qt, no display, no network: block selection, consent keys and the persisted
ai-consent state are plain Python, checked here directly (mirrors test_guide.py).
Spec: geppetto-github/docs/gap-fixes-spec.md §1-§3, ticket T1.
"""
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

CABINET = Path(__file__).resolve().parents[1] / "cabinet"
sys.path.insert(0, str(CABINET))

import ai_notice as n  # noqa: E402

ANTHROPIC = dict(id="anthropic/sonnet", label="Claude Sonnet", kind="anthropic",
                 endpoint="https://api.anthropic.com/v1/", model="claude-sonnet",
                 key_env="ANTHROPIC_API_KEY")
CUSTOM = dict(id="mine/model", label="My model", kind="openai",
              endpoint="https://provider.example/v1/", model="model-id",
              key_env="MY_PROVIDER_API_KEY")
OLLAMA = dict(id="ollama/llama", label="Llama (local)", kind="ollama",
              endpoint="http://127.0.0.1:11434/v1/", model="llama", key_env="")
KOBOLD = dict(id="kobold/local", label="Kobold (local)", kind="openai",
              endpoint="http://127.0.0.1:5001/v1/", model="kobold", key_env="")
CLAUDE_CLI = dict(id="claude/opus", label="Claude Opus · Max plan", kind="claude-cli",
                  endpoint="cli", model="opus", key_env="")
GOOGLE_JUDGE = dict(id="google/gem", label="Gemini", kind="google",
                    endpoint="https://generativelanguage.googleapis.com/v1beta/",
                    model="gemini", key_env="GEMINI_API_KEY")


def cfg(model=ANTHROPIC, **kw):
    return n.config_from_model(model, **kw)


class RecordKeyTests(unittest.TestCase):
    def test_key_is_kind_host_and_key_env_name_never_a_value(self):
        self.assertEqual(n.record_key(ANTHROPIC),
                         "anthropic|api.anthropic.com|ANTHROPIC_API_KEY")

    def test_local_key_carries_its_port_and_no_key_env(self):
        self.assertEqual(n.record_key(OLLAMA), "ollama|127.0.0.1:11434|")

    def test_cli_key_is_kind_cli(self):
        self.assertEqual(n.record_key(CLAUDE_CLI), "claude-cli|cli|")

    def test_changing_host_or_key_env_is_a_new_provider(self):
        other_host = dict(ANTHROPIC, endpoint="https://elsewhere.example/v1/")
        other_env = dict(ANTHROPIC, key_env="OTHER_KEY")
        self.assertNotEqual(n.record_key(other_host), n.record_key(ANTHROPIC))
        self.assertNotEqual(n.record_key(other_env), n.record_key(ANTHROPIC))


class FirstSendTests(unittest.TestCase):
    def test_first_send_to_a_hosted_provider_shows_cost_and_disclosure(self):
        need = n.needs_notice(n.default_state(), cfg())
        self.assertTrue(need["show"])
        self.assertIn("cost", need["missing"])
        self.assertIn("disclosure", need["missing"])
        titles = [b["title"] for b in need["blocks"]]
        self.assertEqual(titles, ["FIRST, ABOUT THE MONEY", "WHAT LEAVES THIS MACHINE"])

    def test_acknowledging_means_it_does_not_show_again(self):
        state, c = n.default_state(), cfg()
        need = n.needs_notice(state, c)
        n.ack_notice(state, c, need["missing"])
        self.assertFalse(n.needs_notice(state, c)["show"])

    def test_local_models_never_gate(self):
        for model in (OLLAMA, KOBOLD):
            need = n.needs_notice(n.default_state(), cfg(model))
            self.assertFalse(need["show"], model)
            self.assertEqual(need["blocks"], [])

    def test_cli_plan_shows_its_own_words_not_api_caps_copy(self):
        need = n.needs_notice(n.default_state(), cfg(CLAUDE_CLI))
        self.assertTrue(need["show"])
        self.assertEqual(need["blocks"][0]["title"], "YOUR PLAN, NOT A KEY")
        self.assertIn("no API key", " ".join(need["blocks"][0]["lines"]))

    def test_custom_endpoint_copy_names_no_specific_model(self):
        need = n.needs_notice(n.default_state(), cfg(CUSTOM))
        text = "\n".join(line for b in need["blocks"] for line in b["lines"])
        self.assertIn("you're signed into yourself", text)
        self.assertIn("your provider", text)
        self.assertNotIn("provider.example", text)   # the notice never names a host
        self.assertNotIn("MY_PROVIDER_API_KEY", text)   # nor a key name
        self.assertNotIn("OPENAI_API_KEY", text)   # never another provider's key


class ReAskTests(unittest.TestCase):
    def acked(self, **kw):
        state, c = n.default_state(), cfg(**kw)
        n.ack_notice(state, c, n.needs_notice(state, c)["missing"])
        return state

    def test_higher_caps_than_acked_re_show_the_cost_block(self):
        state = self.acked()
        need = n.needs_notice(state, cfg(calls=61))
        self.assertTrue(need["show"])
        self.assertIn("cost", need["missing"])
        ok = n.needs_notice(state, cfg(calls=30))       # lower caps: no re-ask
        self.assertFalse(ok["show"])

    def test_caps_rendered_are_the_options_passed_in_not_hard_coded(self):
        need = n.needs_notice(n.default_state(), cfg(calls=7, minutes=3))
        text = "\n".join(line for b in need["blocks"] for line in b["lines"])
        self.assertIn("7 calls", text)
        self.assertIn("3 minutes", text)
        self.assertNotIn("60 calls to", text)

    def test_turning_research_on_re_shows_only_the_research_block(self):
        state = self.acked()
        need = n.needs_notice(state, cfg(research=True))
        self.assertEqual(need["missing"], ["research"])
        self.assertEqual([b["id"] for b in need["blocks"]], ["research"])

    def test_brave_clause_only_when_a_brave_key_is_present(self):
        plain = n.research_block(cfg(research=True, brave=False))["lines"][0]
        brave = n.research_block(cfg(research=True, brave=True))["lines"][0]
        self.assertNotIn("Brave", plain)
        self.assertIn("through Brave", brave)

    def test_turning_a_vision_builder_on_re_shows_the_vision_block(self):
        state = self.acked()
        need = n.needs_notice(state, cfg(vision=True))
        self.assertEqual(need["missing"], ["vision"])

    def test_same_provider_judge_acks_under_the_builders_record(self):
        state = self.acked()
        judge = {"model": ANTHROPIC, "same": True}
        need = n.needs_notice(state, cfg(judge=judge))
        self.assertEqual(need["missing"], ["judge"])
        self.assertIn("nothing goes to a second company",
                      "\n".join(need["blocks"][0]["lines"]))

    def test_a_different_judge_gets_its_own_ack(self):
        state = self.acked()
        judge = {"model": GOOGLE_JUDGE, "same": False}
        c = cfg(judge=judge)
        need = n.needs_notice(state, c)
        self.assertEqual(need["missing"], ["judge"])
        text = "\n".join(need["blocks"][0]["lines"])
        self.assertIn("second company", text)
        self.assertNotIn("GEMINI_API_KEY", text)   # the notice never names the judge's key
        n.ack_notice(state, c, need["missing"])
        self.assertFalse(n.needs_notice(state, c)["show"])
        jrec = state["providers"][n.record_key(GOOGLE_JUDGE)]
        self.assertTrue(jrec["judge_ack"])

    def test_judge_block_states_the_five_pictures_plainly(self):
        judge = {"model": ANTHROPIC, "same": True}
        block = n.judge_block(cfg(judge=judge))
        text = "\n".join(block["lines"])
        self.assertIn("Five pictures", text)
        self.assertIn("768×768", text)
        for view in ("front", "quarter", "side", "back", "low"):
            self.assertIn(view, text)

    def test_show_cost_every_build_opt_in_re_shows(self):
        state = self.acked()
        state["show_cost_every_build"] = True
        self.assertTrue(n.needs_notice(state, cfg())["show"])


class CopyHonestyTests(unittest.TestCase):
    def test_no_dollar_figure_anywhere_in_the_copy(self):
        judge = {"model": GOOGLE_JUDGE, "same": False}
        c = cfg(research=True, brave=True, vision=True, judge=judge)
        need = n.needs_notice(n.default_state(), c)
        text = "\n".join(line for b in need["blocks"] for line in b["lines"])
        self.assertNotIn("$", text)
        self.assertIn("doesn't count dollars or tokens yet", text)

    def test_retention_line_ships_with_the_base_block(self):
        block = n.disclosure_block(cfg())
        self.assertIn("their policy", "\n".join(block["lines"]))

    def test_compact_line_exists_for_hosted_and_never_for_local(self):
        self.assertIn("up to 60 calls / 15 min", n.compact_line(cfg()))
        self.assertEqual(n.compact_line(cfg(OLLAMA)), "")

    def test_chat_compact_line_says_chat_has_no_session_cap(self):
        line = n.chat_compact_line(cfg())
        self.assertIn("Chat has no session cap", line)


class StateTests(unittest.TestCase):
    def test_save_load_round_trip_and_dismiss_persists(self):
        state, c = n.default_state(), cfg()
        n.ack_notice(state, c, n.needs_notice(state, c)["missing"])
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "ai-consent.json")
            self.assertTrue(n.save_state(path, state))
            loaded = n.load_state(path)
            with open(path) as f:   # the file holds names, never a key value
                self.assertNotIn("sk-", f.read())
        self.assertFalse(n.needs_notice(loaded, c)["show"])

    def test_a_corrupt_consent_file_degrades_to_show_everything(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "ai-consent.json")
            with open(path, "w") as f:
                f.write("{not json")
            state = n.load_state(path)
        self.assertTrue(n.needs_notice(state, cfg())["show"])

    def test_a_missing_file_is_a_first_run(self):
        state = n.load_state("/nonexistent/geppetto/ai-consent.json")
        self.assertEqual(state, n.default_state())

    def test_do_not_show_again_suppresses_only_that_provider(self):
        state, c = n.default_state(), cfg()
        n.ack_notice(state, c, n.needs_notice(state, c)["missing"], never_show=True)
        self.assertFalse(n.needs_notice(state, c)["show"])
        self.assertTrue(n.needs_notice(state, cfg(CUSTOM))["show"])
        self.assertNotIn("never_show", state)

    def test_acknowledge_with_never_show_sets_the_per_provider_flag_and_persists(self):
        state, c = n.default_state(), cfg()
        n.ack_notice(state, c, n.needs_notice(state, c)["missing"], never_show=True)
        self.assertTrue(state["providers"][c["key"]]["never_show"])
        self.assertFalse(n.needs_notice(state, c)["show"])
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "ai-consent.json")
            self.assertTrue(n.save_state(path, state))
            loaded = n.load_state(path)
            self.assertTrue(loaded["providers"][c["key"]]["never_show"])
            self.assertTrue(n.needs_notice(loaded, cfg(CUSTOM))["show"])

    def test_dismissing_the_builder_does_not_dismiss_a_different_judge(self):
        state, c = n.default_state(), cfg()
        n.ack_notice(state, c, n.needs_notice(state, c)["missing"], never_show=True)
        judged = cfg(judge={"model": GOOGLE_JUDGE, "same": False})
        need = n.needs_notice(state, judged)
        self.assertTrue(need["show"])
        self.assertEqual(need["missing"], ["judge"])

    def test_legacy_global_never_show_migrates_only_to_the_single_acked_provider(self):
        anthropic_key = cfg()["key"]
        custom_key = cfg(CUSTOM)["key"]
        raw = {"version": 1, "show_cost_every_build": False, "never_show": True,
               "providers": {
                   anthropic_key: {"disclosure_seen": True, "cost_ack": True,
                                   "acked_calls": 60, "acked_minutes": 15},
                   custom_key: {}}}
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "ai-consent.json")
            with open(path, "w") as f:
                json.dump(raw, f)
            loaded = n.load_state(path)
        self.assertTrue(loaded["providers"][anthropic_key]["never_show"])
        self.assertNotIn("never_show", loaded["providers"][custom_key])
        self.assertNotIn("never_show", loaded)
        self.assertFalse(n.needs_notice(loaded, cfg())["show"])
        self.assertTrue(n.needs_notice(loaded, cfg(CUSTOM))["show"])

    def test_legacy_global_never_show_with_two_acked_providers_is_dropped(self):
        raw = {"version": 1, "show_cost_every_build": False, "never_show": True,
               "providers": {
                   cfg()["key"]: {"disclosure_seen": True, "cost_ack": True,
                                  "acked_calls": 60, "acked_minutes": 15},
                   cfg(CUSTOM)["key"]: {"disclosure_seen": True, "cost_ack": True,
                                        "acked_calls": 60, "acked_minutes": 15}}}
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "ai-consent.json")
            with open(path, "w") as f:
                json.dump(raw, f)
            loaded = n.load_state(path)
        self.assertNotIn("never_show", loaded)
        self.assertTrue(all("never_show" not in rec for rec in loaded["providers"].values()))


if __name__ == "__main__":
    unittest.main()
