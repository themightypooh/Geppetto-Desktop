"""The cabinet's side of the drawing tool: it must reach the drawing app over its socket.

The socket key is overridden per test, so nothing here can raise the user's drawing
app or write to its drawings.
"""
import os
import sys
import tempfile
import time
import unittest
from pathlib import Path

CABINET = Path(__file__).resolve().parents[1] / "cabinet"
sys.path.insert(0, str(CABINET))
os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QCoreApplication  # noqa: E402
from PySide6.QtNetwork import QLocalServer, QLocalSocket  # noqa: E402

import host as chest  # noqa: E402

KEY = "geppetto-test-drawing-tool"


class DrawingHostTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QCoreApplication.instance() or QCoreApplication([])

    def setUp(self):
        self.host = chest.Host()
        self.host.drawing_key = KEY
        self.started = []

    def watch_starts(self):
        """watch instead of run: the tests must not start the user's drawing app"""
        self.host.drawing_start = lambda path="": self.started.append(path) or True

    def tearDown(self):
        QLocalServer.removeServer(KEY)

    def pump(self, seconds=0.4):
        end = time.time() + seconds
        while time.time() < end:
            self.app.processEvents()
            time.sleep(0.01)

    def serve(self):
        """a stand-in for the drawing app: it records what it is handed"""
        server = QLocalServer()
        QLocalServer.removeServer(KEY)
        server.listen(KEY)
        server.newConnection.connect(lambda: self.read(server))
        return server

    def read(self, server):
        connection = server.nextPendingConnection()
        self.received.append(bytes(connection.readAll()).decode())
        connection.readyRead.connect(
            lambda: self.received.append(bytes(connection.readAll()).decode()))

    def test_the_drawing_tool_is_reached_over_its_socket(self):
        self.received = []
        self.serve()
        self.assertTrue(self.host.drawing_running())
        self.assertTrue(self.host.drawing_send("/tmp/a.linework"))
        self.pump()
        self.assertTrue(any("/tmp/a.linework" in r for r in self.received),
                        "the drawing tool was never handed the drawing: %r" % self.received)

    def test_the_pencil_raises_the_drawing_tool_that_is_already_up(self):
        self.received = []
        self.serve()
        self.watch_starts()
        self.host.openDrawingTool()
        self.pump()
        self.assertEqual(self.started, [], "a second copy of the drawing tool was started")
        self.assertIn("drawing is up", self.host.status)
        self.assertTrue(self.host.drawingOpen)

    def test_the_pencil_starts_the_drawing_tool_when_it_is_down(self):
        self.watch_starts()
        self.host.openDrawingTool()
        self.assertEqual(self.started, [""], "the drawing tool was not started")
        self.assertTrue(self.host.drawingOpen)

    def test_a_drawing_is_handed_over_in_its_own_right(self):
        self.received = []
        self.serve()
        self.watch_starts()
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "one.linework")
            open(path, "w").close()
            self.host.openDrawingFile(path)
            self.pump()
            self.assertTrue(any(path in r for r in self.received))
            self.assertEqual(self.started, [])
            self.assertIn("one.linework", self.host.status)

    def test_a_missing_drawing_is_refused_rather_than_started(self):
        self.watch_starts()
        self.host.openDrawingFile("/tmp/definitely-not-here.linework")
        self.assertEqual(self.started, [])
        self.assertIn("no such drawing", self.host.status)

    def test_the_tool_drawer_lists_the_gallery(self):
        self.host.refreshTools()
        self.assertIsInstance(self.host.drawings, list)
        for drawing in self.host.drawings:
            self.assertTrue(os.path.isfile(drawing["path"]))
            self.assertTrue(drawing["path"].endswith(".linework"))

    def test_starting_the_drawing_tool_really_spawns_it(self):
        """the start path runs a program (a harmless stand-in here) with the drawing"""
        with tempfile.TemporaryDirectory() as directory:
            marker = os.path.join(directory, "started")
            script = os.path.join(directory, "fake-drawing-tool")
            with open(script, "w") as f:
                f.write("#!/bin/sh\necho \"$1\" > %s\n" % marker)
            os.chmod(script, 0o755)
            self.host.drawing_program = lambda: script
            self.assertTrue(self.host.drawing_start("/tmp/a.linework"))
            for _ in range(50):
                if os.path.isfile(marker):
                    break
                time.sleep(0.05)
            self.assertTrue(os.path.isfile(marker), "the drawing tool was not started")
            with open(marker) as f:
                self.assertEqual(f.read().strip(), "/tmp/a.linework")

    def test_a_drawing_tool_that_is_not_there_is_reported_not_started(self):
        self.host.drawing_program = lambda: "/tmp/not-a-drawing-tool-here"
        self.assertFalse(self.host.drawing_start())
        self.assertIn("no drawing tool", self.host.status)


if __name__ == "__main__":
    unittest.main()
