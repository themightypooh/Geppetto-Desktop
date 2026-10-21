"""The drawing tool's helpers: the gallery it keeps, and the socket it talks on.

Headless: no Qt, no window, no drawing app started.
"""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("drawing_tools", Path(__file__).resolve().parents[1] / "cabinet/drawing_tools.py")
dt = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dt)


class DrawingToolsTests(unittest.TestCase):
    def gallery(self, root, entries, files=()):
        """a drawings folder with an index and some drawing files"""
        directory = Path(root)
        directory.mkdir(parents=True, exist_ok=True)
        (directory / "index.json").write_text(json.dumps(entries))
        for name in files:
            (directory / (name + ".linework")).write_text("{}")
        return str(directory)

    def test_the_gallery_is_listed_newest_first(self):
        with tempfile.TemporaryDirectory() as root:
            directory = self.gallery(root, [
                {"id": "old", "name": "Old", "updated": 100},
                {"id": "new", "name": "New", "updated": 300},
                {"id": "mid", "name": "Mid", "updated": 200},
            ], ["old", "new", "mid"])
            found = dt.drawings(directory)
            self.assertEqual([d["id"] for d in found], ["new", "mid", "old"])
            self.assertEqual(found[0]["name"], "New")
            self.assertEqual(found[0]["path"], os.path.join(directory, "new.linework"))
            self.assertEqual(found[0]["thumb"], os.path.join(directory, "new.jpg"))

    def test_a_missing_or_hostile_index_is_empty_not_a_crash(self):
        with tempfile.TemporaryDirectory() as root:
            directory = os.path.join(root, "drawings")
            os.makedirs(directory)
            self.assertEqual(dt.drawings(directory), [])             # no index at all
            with open(os.path.join(directory, "index.json"), "w") as f:
                f.write("{not json")
            self.assertEqual(dt.drawings(directory), [])             # a torn index
            with open(os.path.join(directory, "index.json"), "w") as f:
                f.write('{"id": "x"}')
            self.assertEqual(dt.drawings(directory), [])             # not a list
            with open(os.path.join(directory, "index.json"), "w") as f:
                f.write('["nope", 3, null]')
            self.assertEqual(dt.drawings(directory), [])             # entries must be objects

    def test_an_index_cannot_point_out_of_the_drawings_folder(self):
        with tempfile.TemporaryDirectory() as root:
            directory = self.gallery(root, [
                {"id": "../../etc/passwd", "name": "escape"},
                {"id": "/etc/passwd", "name": "absolute"},
                {"id": "..", "name": "parent"},
                {"id": ".hidden", "name": "hidden"},
                {"id": "ok", "name": "Fine", "updated": 1},
            ], ["ok"])
            found = dt.drawings(directory)
            self.assertEqual([d["id"] for d in found], ["ok"])
            self.assertEqual(dt.safe_id("../../etc/passwd"), "")
            self.assertEqual(dt.safe_id("ok"), "ok")
            self.assertIsNone(dt.resolve(directory, "../../etc/passwd"))
            self.assertEqual(dt.resolve(directory, "ok"), os.path.join(directory, "ok.linework"))
            self.assertIsNone(dt.resolve(directory, "not-there"))

    def test_the_index_may_outlive_a_deleted_drawing(self):
        with tempfile.TemporaryDirectory() as root:
            directory = self.gallery(root, [{"id": "gone", "name": "Gone"}, {"id": "here", "name": "Here"}], ["here"])
            self.assertEqual([d["id"] for d in dt.drawings(directory)], ["here"])

    def test_a_bad_timestamp_does_not_hide_the_drawing(self):
        with tempfile.TemporaryDirectory() as root:
            directory = self.gallery(root, [{"id": "a", "name": "A", "updated": "soon"}], ["a"])
            found = dt.drawings(directory)
            self.assertEqual(len(found), 1)
            self.assertEqual(found[0]["updated"], 0)

    def test_the_socket_matches_the_drawing_app(self):
        # the app's main.cpp listens on "linework-" + USER
        self.assertEqual(dt.socket_key("poo"), "linework-poo")
        self.assertEqual(dt.socket_key(), "linework-" + os.environ.get("USER", ""))


if __name__ == "__main__":
    unittest.main()
