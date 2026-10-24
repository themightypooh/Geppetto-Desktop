// The tool card: the tool you picked off the wheel by hand, ready to use on the model. Its fields are
// the tool's own parameters as the kernel describes them (host.benchDescribe -> bench.form), filled
// with their defaults; APPLY turns the ones you changed into one build-script line and the bench runs
// it on your copy of what's on screen. UNDO steps back one; KEEP saves it into the MODELING folder;
// WHEEL picks another tool.
// Nothing here edits a run: the bench works on a copy (see host.py, "the bench").
import QtQuick
import QtQuick.Controls as C

Item {
    id: card
    property bool open: false
    property string label: ""
    property var bench: ({})
    property real maxHeight: 600
    property bool folded: false            // just the title and the keys: the fields out of your view
    signal apply(string line)
    signal undo()
    signal keep()                          // save your edit as a model in the MODELING folder
    signal wheel()
    signal closed()
    signal drawOver()             // arm the pane-1 draw-over layer: drag a stroke over the model

    readonly property var form: bench.form || ({})
    readonly property var fields: form.fields || []
    readonly property var bodies: bench.bodies || []
    property var values: ({})              // key -> what you typed, only for the fields you touched
    // only a different tool clears what you typed: the bench refreshes `form` on every apply and failure
    readonly property string formKey: form.loading ? "" : String(form.key || "")
    onFormKeyChanged: if (formKey) { values = ({}); extra = ({}) }

    readonly property color steel: "#2b2f36"
    readonly property color steelHi: "#3d424b"
    readonly property color steelLo: "#1f2228"
    readonly property color edge: "#0b0c0e"
    readonly property color lit: "#f2a91c"
    readonly property color text: "#c9ccd0"
    readonly property color dim: "#7a7f88"
    readonly property color bad: "#e0604a"
    readonly property string mono: "DejaVu Sans Mono"

    width: 230
    height: Math.min(maxHeight, body.implicitHeight + keys.height + 18)
    visible: open
    opacity: open ? 1 : 0
    Behavior on opacity { NumberAnimation { duration: 160 } }

    function valueOf(f) { return values[f.key] !== undefined ? values[f.key] : f.value }
    function setValue(f, v) { const o = Object.assign({}, values); o[f.key] = v; values = o }
    function changed(f) { return values[f.key] !== undefined && String(values[f.key]) !== String(f.value) }
    function quote(v) { v = String(v); return /[\s"]/.test(v) ? '"' + v.replace(/"/g, "") + '"' : v }
    // the body an edit works on: the one you chose, else the last one made
    function bodyField() { return fields.find(f => f.kind === "bodies") }
    // the control socket's hands (host.py: card KEY=VALUE, apply): same as typing in a field and pressing APPLY
    function setKey(k, v) { const f = fields.find(x => x.key === k); if (f) setValue(f, v); return !!f }
    function applyNow() { applyKey.go() }

    // ---- USE SELECTION: the faces/edges/bodies you picked in the viewport fill the tool's fields, the way
    // s&box Geppetto's Feature.ApplyGeometrySelection seeds a tool from the current selection.
    // host.subSelection is the latest `selection` event (SubSelection.ToJson); guarded, the host may lack it.
    readonly property var sel: (typeof host !== "undefined" && host && host.subSelection) ? host.subSelection : ({})
    // the tools whose List<FaceRef> Faces / List<EdgeRef> Edges take the pick (Feature.ApplyGeometrySelection);
    // the bench's describe doesn't list those fields, so they ride along here and line() adds them
    readonly property var faceTools: ["FaceMaterial", "Draft", "Hole", "Subdivide", "MoveFace", "DeleteFace", "Extrude", "Fur"]
    readonly property var edgeTools: ["Fillet", "Chamfer"]
    property var extra: ({})               // faces=/edges= from the selection, for fields the form doesn't list
    function selBodies() {
        const b = sel.bodies ? Object.keys(sel.bodies) : [];
        return b.length ? b : String(sel.bodyNames || "").split(",").filter(x => x);
    }
    function hasSel() { return (sel.count | 0) > 0 || selBodies().length > 0 }
    function takesFaces() { return fields.some(f => f.kind === "faces" || f.key === "faces") || (form.kind === "add" && faceTools.indexOf(form.type) >= 0) }
    function takesEdges() { return fields.some(f => f.kind === "edges" || f.key === "edges") || (form.kind === "add" && edgeTools.indexOf(form.type) >= 0) }
    // "" when the button can be used, else why not (the tooltip)
    readonly property string selBlock: {
        if (form.loading || !form.kind) return "no tool on the card";
        if (!hasSel()) return "nothing selected: pick faces, edges or bodies in the viewport first";
        if (form.kind === "edit" || takesFaces() || takesEdges() || fields.some(f => f.kind === "bodies")) return "";
        return "this tool takes no faces, edges or bodies";
    }
    function setExtra(k, v) { if (setKey(k, v)) return; const o = Object.assign({}, extra); o[k] = v; extra = o }
    function useSelection() {
        if (selBlock) { why = selBlock; return false }
        why = "";
        const names = selBodies();
        if (form.kind === "edit") {
            const b = names[0], r = (sel.bodies || {})[b] || {};
            setKey("body", b);
            if (r.select !== undefined) setKey("select", r.select);
            if (r.element) { const e = String(r.element); setKey("element", e.charAt(0).toUpperCase() + e.slice(1)) }
            if (names.length > 1) why = "only " + b + " is used: an edit works on one body (picked " + names.join(", ") + ")";
            return true;
        }
        for (const f of fields) if (f.kind === "bodies") setValue(f, names.join(","));
        if (takesFaces() && sel.faces) setExtra("faces", sel.faces);
        if (takesEdges()) {
            if (sel.edges) setExtra("edges", sel.edges);
            // faces on an edge tool: their rims (BuildScript faces= → Feature.ApplyGeometrySelection → CaptureBoundary)
            else if (sel.faces) setExtra("faces", sel.faces);
        }
        return true;
    }

    // the build-script line for what's on the card, or "" with `why` set
    property string why: ""
    readonly property string problem: card.open ? (card.bench.error || card.why || "") : ""
    function line() {
        why = "";
        if (form.kind === "verb") {
            const a = String(valueOf(fields[0] || { key: "", value: "" })).trim();
            return (form.verb + " " + a).trim();
        }
        const parts = [];
        let target = "";
        for (const f of fields) {
            if (form.kind === "edit" && f.kind === "bodies") { target = String(valueOf(f) || bodies[bodies.length - 1] || ""); continue; }
            if (!changed(f)) continue;
            const v = valueOf(f);
            if (String(v).trim() === "" && f.kind !== "text") continue;
            parts.push(f.key + "=" + quote(f.kind === "bool" ? (v ? "on" : "off") : v));
        }
        if (form.kind === "edit") {
            if (!target) { why = "nothing to edit yet: add something first"; return ""; }
            return ["edit", quote(target), form.op].concat(parts).join(" ");
        }
        if (form.kind === "add") {
            for (const k in extra) if (extra[k]) parts.push(k + "=" + quote(extra[k]));
            return ["add", form.type].concat(parts).join(" ");
        }
        why = form.error || "";
        return "";
    }

    Rectangle { anchors.fill: parent; color: card.steelLo; opacity: 0.88; border.color: card.edge; border.width: 1; radius: 4 }

    Flickable {
        id: flick
        anchors.fill: parent; anchors.margins: 5; anchors.bottomMargin: keys.height + 8
        contentHeight: body.implicitHeight
        clip: true
        boundsBehavior: Flickable.StopAtBounds
        Column {
            id: body
            width: flick.width
            spacing: 3

            Row {
                spacing: 5
                Image { width: 20; height: 20; source: card.form.key ? Qt.resolvedUrl("art/tools/" + String(card.form.key).replace(":", "_") + ".png") : ""; smooth: true }
                Column {
                    anchors.verticalCenter: parent.verticalCenter
                    Text { text: (card.folded ? "▸ " : "▾ ") + card.label; color: card.lit; font.family: card.mono; font.bold: true; font.pixelSize: 12
                        MouseArea { anchors.fill: parent; cursorShape: Qt.PointingHandCursor; onClicked: card.folded = !card.folded } }
                    Text {
                        text: card.bench.ready === false ? "getting your copy ready…" : "on a copy of " + (card.bench.from || "nothing") + (card.bench.revision ? "  ·  rev " + card.bench.revision : "")
                        color: card.dim; font.family: card.mono; font.pixelSize: 9
                        width: card.width - 40; elide: Text.ElideRight
                    }
                }
            }

            Text {
                visible: !!card.form.loading
                text: "reading the tool…"; color: card.dim; font.family: card.mono; font.pixelSize: 10
            }

            Repeater {
                model: card.form.loading || card.folded ? [] : card.fields
                delegate: Item {
                    id: row
                    required property var modelData
                    readonly property var f: modelData
                    width: body.width; height: 19
                    Text {
                        width: 84; anchors.verticalCenter: parent.verticalCenter
                        text: row.f.label || "arguments"; elide: Text.ElideRight
                        color: card.changed(row.f) ? card.lit : card.text
                        font.family: card.mono; font.pixelSize: 10
                    }
                    Item {
                        x: 88; width: parent.width - 88; height: parent.height

                        // on/off, a choice, a body: click to step through, right-click to step back
                        Rectangle {
                            visible: ["bool", "choice", "bodies"].indexOf(row.f.kind) >= 0
                            anchors.fill: parent; radius: 3
                            color: cycler.containsMouse ? card.steelHi : card.steel; border.color: card.edge
                            readonly property var options: row.f.kind === "bool" ? [false, true]
                                : row.f.kind === "choice" ? (row.f.options || []) : [""].concat(card.bodies)
                            Text {
                                anchors.fill: parent; anchors.leftMargin: 4; verticalAlignment: Text.AlignVCenter
                                text: { const v = card.valueOf(row.f); return row.f.kind === "bool" ? (v ? "ON" : "OFF") : row.f.kind === "bodies" && !v ? (row.f.key === "body" ? (card.bodies[card.bodies.length - 1] || "— none yet —") : "all bodies") : String(v) }
                                color: card.changed(row.f) ? card.lit : card.text; font.family: card.mono; font.pixelSize: 10; elide: Text.ElideRight
                            }
                            MouseArea {
                                id: cycler
                                anchors.fill: parent; hoverEnabled: true; acceptedButtons: Qt.LeftButton | Qt.RightButton
                                cursorShape: Qt.PointingHandCursor
                                onClicked: mouse => {
                                    const o = parent.options; if (!o.length) return;
                                    let i = o.findIndex(x => String(x) === String(card.valueOf(row.f)));
                                    i = (i + (mouse.button === Qt.RightButton ? o.length - 1 : 1)) % o.length;
                                    card.setValue(row.f, o[i]);
                                }
                            }
                        }
                        // numbers, points, text
                        Rectangle {
                            visible: ["bool", "choice", "bodies"].indexOf(row.f.kind) < 0
                            anchors.fill: parent; radius: 3
                            color: input.activeFocus ? "#f4f7ee" : card.steel; border.color: input.activeFocus ? card.lit : card.edge
                            TextInput {
                                id: input
                                anchors.fill: parent; anchors.leftMargin: 4; anchors.rightMargin: 3
                                verticalAlignment: TextInput.AlignVCenter; clip: true
                                text: String(card.valueOf(row.f))
                                color: activeFocus ? "#1d2318" : card.changed(row.f) ? card.lit : card.text
                                selectionColor: card.lit
                                font.family: card.mono; font.pixelSize: 10
                                selectByMouse: true
                                onTextEdited: card.setValue(row.f, text)
                                onAccepted: applyKey.go()
                            }
                        }
                    }
                }
            }

            // the viewport pick into the fields (useSelection); greyed with the reason on hover when it can't
            Item {
                visible: !card.form.loading && !card.folded && !!card.form.kind
                width: useSelKey.width; height: useSelKey.height
                HoverHandler { id: useSelHover }          // outside the key, so a greyed key still says why
                C.ToolTip.visible: useSelHover.hovered
                C.ToolTip.delay: 400
                C.ToolTip.text: card.selBlock || "fill the fields from what you picked: " + (card.sel.kind || "bodies") + " on " + card.selBodies().join(", ")
                CardKey {
                    id: useSelKey
                    objectName: "useSelection"
                    text: "USE SELECTION" + ((card.sel.count | 0) > 0 ? " (" + card.sel.count + ")" : "")
                    enabled: card.selBlock === "" && !card.bench.busy
                    onClicked: card.useSelection()
                }
            }

            // why it didn't work is called out top-right over the viewport (cabinet.qml: benchError), not in here
            Text {
                width: body.width; wrapMode: Text.Wrap; maximumLineCount: 3; elide: Text.ElideRight
                visible: text !== "" && !card.bench.error
                text: card.bench.log || ""
                color: card.dim; font.family: card.mono; font.pixelSize: 9
            }
        }
    }

    Row {
        id: keys
        spacing: 3
        anchors.left: parent.left; anchors.bottom: parent.bottom; anchors.margins: 5
        CardKey {
            id: applyKey
            text: card.bench.busy ? "WORKING" : "APPLY"; hot: true
            enabled: !card.bench.busy && !card.form.loading && !!card.form.kind
            function go() { if (!enabled) return; const l = card.line(); if (l) card.apply(l) }
            onClicked: go()
        }
        CardKey { text: "UNDO"; enabled: !!card.bench.canUndo && !card.bench.busy; onClicked: card.undo() }
        CardKey { text: "KEEP"; enabled: !!card.bench.revision && !card.bench.busy; onClicked: card.keep() }
        CardKey { text: "WHEEL"; onClicked: card.wheel() }
        CardKey { text: "DRAW"; onClicked: card.drawOver() }
        CardKey { text: "X"; onClicked: card.closed() }
    }

    component CardKey: Rectangle {
        id: ck
        property string text: ""
        property bool hot: false
        signal clicked()
        width: Math.max(34, t.implicitWidth + 10); height: 20; radius: 3
        opacity: enabled ? 1 : 0.4
        color: ckm.pressed ? card.steelLo : ckm.containsMouse ? card.steelHi : card.steel
        border.color: hot && enabled ? card.lit : card.edge; border.width: hot ? 2 : 1
        Text { id: t; anchors.centerIn: parent; text: ck.text; color: ck.hot ? card.lit : card.text; font.family: card.mono; font.bold: true; font.pixelSize: 10 }
        MouseArea { id: ckm; anchors.fill: parent; hoverEnabled: true; enabled: ck.enabled; cursorShape: Qt.PointingHandCursor; onClicked: ck.clicked() }
    }
}
