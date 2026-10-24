"""Static surface checks for the BYO-AI notice gate (no Qt in this suite — the
behavioural logic is covered by test_ai_notice.py; this pins the wiring so the
gate can't be quietly unwired from the send paths or the card from the QML)."""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
HOST = (ROOT / "cabinet" / "host.py").read_text()
QML = (ROOT / "cabinet" / "cabinet.qml").read_text()


class HostGateTests(unittest.TestCase):
    def test_research_is_gated_before_any_process_starts(self):
        begin = HOST.index("def beginResearch")
        gate = HOST.index("_notice_gate(model, \"research\"", begin)
        popen = HOST.index("subprocess.Popen", begin)
        makedirs = HOST.index('makedirs_private(RUNS', begin)
        self.assertLess(gate, popen)
        self.assertLess(gate, makedirs)      # no run folder is made while the card is up

    def test_chat_is_gated_before_the_message_is_appended(self):
        send = HOST.index("def sendChat")
        gate = HOST.index('_notice_gate(model, "chat")', send)
        append = HOST.index("self._chat.append(message)", send)
        self.assertLess(gate, append)

    def test_acknowledging_sends_nothing_by_itself(self):
        start = HOST.index("def noticeStart")
        body = HOST[start:HOST.index("def noticeCancel", start)]
        self.assertNotIn("Popen", body)
        self.assertIn("ack_notice", body)
        self.assertIn("beginResearch", body)   # it resumes the waiting send, that's all
        self.assertIn("sendChat", body)

    def test_consent_lives_in_ai_consent_json(self):
        self.assertIn("ai-consent.json", HOST)
        self.assertIn("import ai_notice as noticelib", HOST)


class QmlCardTests(unittest.TestCase):
    def test_the_notice_card_is_a_root_level_component_with_an_instance(self):
        self.assertIn("component NoticeCard", QML)
        self.assertIn("NoticeCard { z: 31 }", QML)
        self.assertIn('objectName: "noticeCard"', QML)

    def test_the_card_buttons_call_the_host_slots(self):
        for needle in ("host.noticeStart(ncard.everyTime, ncard.neverShow)", "host.noticeCancel()",
                       'objectName: "noticeStart"', 'objectName: "noticeCancel"',
                       'objectName: "noticeEveryTime"', 'objectName: "noticeNeverShow"'):
            self.assertIn(needle, QML)


if __name__ == "__main__":
    unittest.main()
