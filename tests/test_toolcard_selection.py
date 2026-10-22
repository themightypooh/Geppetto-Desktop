"""USE SELECTION on the tool card: the viewport pick fills the tool's fields like s&box's
Feature.ApplyGeometrySelection. Loads ToolCard.qml offscreen against a fake host (no window)."""
import os
import sys
import unittest
from pathlib import Path

os.environ["QT_QPA_PLATFORM"] = "offscreen"
CABINET = Path(__file__).resolve().parents[1] / "cabinet"

from PySide6.QtCore import QObject, Property, Signal, QUrl, qInstallMessageHandler  # noqa: E402

MESSAGES = []
qInstallMessageHandler(lambda mode, ctx, msg: MESSAGES.append(msg))

FACES = "Box:0,0,10:0,0,1;Box:10,0,5:1,0,0"
EDGES = "Box:5,5,10:1,0,0"


class FakeHost(QObject):
    changed = Signal()

    def __init__(self):
        super().__init__()
        self._sel = {}

    def get(self):
        return self._sel

    def put(self, v):
        self._sel = v
        self.changed.emit()

    subSelection = Property("QVariantMap", get, put, notify=changed)


def sel(kind="faces", bodies=("Box",)):
    return {"ev": "selection", "mode": kind[:-1] if kind else "body", "kind": kind, "count": 2 if kind else 0,
            "faces": FACES if kind == "faces" else "", "edges": EDGES if kind == "edges" else "",
            "bodyNames": ",".join(bodies), "items": [], "lost": 0,
            "bodies": {b: {"select": "near:0,0,10", "element": kind or "auto", "count": 1} for b in bodies}}


EDIT = {"key": "edit:bevel", "kind": "edit", "verb": "edit", "op": "bevel", "fields": [
    {"key": "body", "label": "Body", "kind": "bodies", "value": ""},
    {"key": "select", "label": "Select", "kind": "text", "value": ""},
    {"key": "element", "label": "Element", "kind": "choice", "value": "Auto",
     "options": ["Auto", "Faces", "Edges", "Vertices"]},
    {"key": "distance", "label": "Distance", "kind": "number", "value": 1}]}


def add(type_, extra=()):
    return {"key": type_.lower(), "kind": "add", "verb": "add", "type": type_, "fields": [
        {"key": "bodies", "label": "Bodies", "kind": "bodies", "value": ""},
        {"key": "radius", "label": "Radius", "kind": "number", "value": 1}, *extra,
        {"key": "name", "label": "Name", "kind": "text", "value": ""}]}


class ToolCardSelection(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        from PySide6.QtGui import QGuiApplication
        from PySide6.QtQml import QQmlComponent, QQmlEngine
        cls.app = QGuiApplication.instance() or QGuiApplication([])
        cls.host = FakeHost()
        cls.engine = QQmlEngine()
        cls.engine.rootContext().setContextProperty("host", cls.host)
        cls.comp = comp = QQmlComponent(cls.engine, QUrl.fromLocalFile(str(CABINET / "ToolCard.qml")))
        cls.card = comp.create()
        assert cls.card is not None, comp.errorString()
        cls.card.setProperty("open", True)

    def load(self, form, selection):
        self.card.setProperty("bench", {"form": form, "bodies": ["Box", "Lid"], "ready": True})
        self.host.subSelection = selection
        self.card.setProperty("values", {})
        self.card.setProperty("extra", {})
        self.card.setProperty("why", "")

    def call(self, name, *a):
        from PySide6.QtCore import QMetaObject, Q_RETURN_ARG, Q_ARG
        return QMetaObject.invokeMethod(self.card, name, Q_RETURN_ARG("QVariant"), *[Q_ARG("QVariant", x) for x in a])

    def line(self):
        return self.call("line")

    def test_edit_takes_rule_element_body(self):
        self.load(EDIT, sel("edges"))
        self.assertEqual(self.card.property("selBlock"), "")
        self.call("useSelection")
        self.assertEqual(self.line(), "edit Box bevel select=near:0,0,10 element=Edges")
        self.assertEqual(self.card.property("problem"), "")

    def test_edit_two_bodies_uses_first_and_says_so(self):
        self.load(EDIT, sel("faces", ("Box", "Lid")))
        self.call("useSelection")
        self.assertIn("only Box", self.card.property("problem"))
        self.assertEqual(self.line(), "edit Box bevel select=near:0,0,10 element=Faces")

    def test_fillet_takes_edges_and_bodies(self):
        self.load(add("Fillet"), sel("edges"))
        self.call("useSelection")
        self.assertEqual(self.line(), "add Fillet bodies=Box edges=" + EDGES)

    def test_fillet_with_faces_takes_their_rims(self):
        # faces on Fillet/Chamfer ride as faces=; the build script turns each into its boundary edges
        self.load(add("Fillet"), sel("faces"))
        self.call("useSelection")
        self.assertEqual(self.card.property("problem"), "")
        self.assertEqual(self.line(), "add Fillet bodies=Box faces=" + FACES)

    def test_draft_takes_faces(self):
        self.load(add("Draft"), sel("faces"))
        self.call("useSelection")
        self.assertEqual(self.line(), "add Draft bodies=Box faces=" + FACES)

    def test_listed_faces_field_is_set_directly(self):
        self.load(add("Thing", [{"key": "faces", "label": "Faces", "kind": "faces", "value": ""}]), sel("faces"))
        self.call("useSelection")
        self.assertEqual(self.line(), "add Thing bodies=Box faces=" + FACES)

    def test_greyed_without_selection_or_compatible_field(self):
        self.load(add("Fillet"), {})
        self.assertIn("nothing selected", self.card.property("selBlock"))
        verb = {"key": "set", "kind": "verb", "verb": "set",
                "fields": [{"key": "", "label": "Arguments", "kind": "text", "value": ""}]}
        self.load(verb, sel("faces"))
        self.assertIn("takes no", self.card.property("selBlock"))
        key = self.card.findChild(QObject, "useSelection")
        self.assertFalse(key.property("enabled"))

    def test_no_qml_errors(self):
        bad = ("TypeError", "ReferenceError", "is not a function", "Cannot read property", "Binding loop")
        self.assertEqual([m for m in MESSAGES if any(b in m for b in bad)], [])


if __name__ == "__main__":
    unittest.main()
