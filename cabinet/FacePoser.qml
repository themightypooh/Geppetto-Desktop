// The FACE POSER card: Garry's Mod's Face Poser panel for the model in one pane. Presets and quick
// faces on top, then flex scale, RANDOMIZE and ZERO, a filter, and one fold-out form per group (eyes,
// brows, ...) with a slider per flex controller. A stereo controller (a key split into _L/_R) gets a
// balance slider under it, the way Valve's Faceposer pairs left and right. TALK holds the lip-sync
// visemes. Everything goes to the pane's viewer as `face ...` (FacePoserService), which bends the
// model and answers with the new face.
import QtQuick
import QtQuick.Controls as C

Item {
    id: card
    property bool open: host.facePane > 0
    property real maxHeight: 520
    readonly property var face: host.face
    readonly property var ctls: face.controllers || []
    readonly property var groups: (face.groups || [])
    readonly property var presets: (face.presets || []).filter(p => p.indexOf("viseme_") !== 0)
    readonly property var visemes: (face.presets || []).filter(p => p.indexOf("viseme_") === 0)
    property var folded: ({})
    property string filter: ""

    // the delegates are keyed on the controller names, so a new face event moves the sliders
    // instead of rebuilding them under the mouse
    property var names: []
    onCtlsChanged: {
        const n = ctls.map(c => c.name);
        if (n.join(",") !== names.join(",")) names = n;
    }
    function ctl(name) { return ctls.find(c => c.name === name) || { value: 0, balance: 0.5, min: 0, max: 1, stereo: false, group: "", tip: "" } }
    function nice(s) { return s.replace(/_/g, " ").replace(/\b\w/g, m => m.toUpperCase()) }
    function shown(name) {
        const c = ctl(name);
        if (folded[c.group]) return false;
        return filter === "" || name.indexOf(filter.toLowerCase()) >= 0 || nice(name).toLowerCase().indexOf(filter.toLowerCase()) >= 0;
    }
    function fold(g) { const f = Object.assign({}, folded); f[g] = !f[g]; folded = f }
    function num(v) { return Number(v).toFixed(2) }

    readonly property color steel: "#2b2f36"
    readonly property color steelHi: "#3d424b"
    readonly property color steelLo: "#1f2228"
    readonly property color edge: "#0b0c0e"
    readonly property color lit: "#f2a91c"
    readonly property color text: "#c9ccd0"
    readonly property color dim: "#7a7f88"
    readonly property string mono: "DejaVu Sans Mono"

    width: 330
    height: Math.min(maxHeight, body.implicitHeight + head.height + 16)
    visible: open
    opacity: open ? 1 : 0
    Behavior on opacity { NumberAnimation { duration: 160 } }

    Rectangle { anchors.fill: parent; color: card.steelLo; opacity: 0.93; border.color: card.edge; border.width: 1; radius: 4 }
    MouseArea { anchors.fill: parent; onWheel: w => w.accepted = false }   // clicks stop here, not on the model

    component Key: Rectangle {
        id: k
        property string label: ""
        property string tip: ""
        property bool on: false
        signal hit()
        width: Math.max(26, kt.implicitWidth + 12); height: 20; radius: 3
        color: on ? card.lit : km.containsMouse ? card.steelHi : card.steel; border.color: card.edge
        Text { id: kt; anchors.centerIn: parent; text: k.label; color: k.on ? card.steelLo : card.text; font.family: card.mono; font.bold: true; font.pixelSize: 10 }
        MouseArea { id: km; anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor; onClicked: k.hit() }
        C.ToolTip.visible: km.containsMouse && tip !== ""
        C.ToolTip.text: tip
        C.ToolTip.delay: 450
    }

    component Bar: C.Slider {
        id: s
        property color fill: card.lit
        height: 14
        background: Rectangle {
            x: s.leftPadding; y: s.topPadding + s.availableHeight / 2 - 2
            width: s.availableWidth; height: 4; radius: 2; color: card.edge
            Rectangle {
                // filled from 0 (the middle of a bipolar slider), so a face reads at a glance
                readonly property real z0: (Math.max(s.from, Math.min(s.to, 0)) - s.from) / (s.to - s.from)
                x: Math.min(z0, s.visualPosition) * parent.width
                width: Math.abs(s.visualPosition - z0) * parent.width
                height: parent.height; radius: 2; color: s.fill
            }
        }
        handle: Rectangle {
            x: s.leftPadding + s.visualPosition * (s.availableWidth - width); y: s.topPadding + s.availableHeight / 2 - height / 2
            width: 8; height: 12; radius: 2; color: s.pressed ? card.lit : card.text; border.color: card.edge
        }
    }

    // ---- title ------------------------------------------------------------------------------
    Item {
        id: head
        x: 8; y: 6; width: parent.width - 16; height: 22
        Text {
            anchors.verticalCenter: parent.verticalCenter
            text: "FACE POSER  ·  PANE " + host.facePane
            color: card.lit; font.family: card.mono; font.bold: true; font.pixelSize: 12
        }
        Key { anchors.right: parent.right; anchors.verticalCenter: parent.verticalCenter; label: "X"; tip: "shut the Face Poser (the face stays on the model)"; onHit: host.faceOpen(0) }
    }

    C.ScrollView {
        id: scroll
        x: 8; y: head.y + head.height + 4
        width: parent.width - 16; height: card.height - y - 8
        clip: true
        contentWidth: availableWidth

        Column {
            id: body
            width: scroll.availableWidth
            spacing: 6

            // no keys yet: say what the sliders need
            Text {
                visible: card.ctls.length === 0
                width: parent.width; wrapMode: Text.WordWrap
                color: card.dim; font.family: card.mono; font.pixelSize: 10
                text: card.face.ev === undefined ? "asking the viewer…"
                    : "This model has no face keys yet. Sculpt shape keys named after the controllers (jaw_drop, corner_puller, …), split the two-sided ones with Split Shape Key, and their sliders turn up here."
            }

            // ---- presets: the dropdown, SAVE under a name, and the quick faces ----------------
            Row {
                spacing: 4
                visible: card.ctls.length > 0
                C.ComboBox {
                    id: presetBox
                    width: 120; height: 22; topPadding: 0; bottomPadding: 0
                    model: card.presets
                    currentIndex: card.presets.indexOf(card.face.preset || "")
                    font.family: card.mono; font.pixelSize: 10
                    onActivated: i => host.faceCmd("preset " + card.presets[i])
                    C.ToolTip.visible: hovered; C.ToolTip.delay: 450
                    C.ToolTip.text: "pick a saved expression; the sliders jump to it"
                }
                C.TextField {
                    id: saveName
                    width: 90; height: 22
                    placeholderText: "name"
                    font.family: card.mono; font.pixelSize: 10
                    color: card.text
                    background: Rectangle { color: card.edge; radius: 3 }
                    onAccepted: saveKey.hit()
                }
                Key {
                    id: saveKey
                    label: "SAVE"; tip: card.face.saveable ? "keep these sliders as a preset, in MODEL.facerig.json next to the model" : "this pane shows no saved model, so there is nowhere to keep presets"
                    onHit: if (saveName.text.trim() !== "") { host.faceCmd("save " + saveName.text.trim().replace(/\s+/g, "_")); saveName.text = "" }
                }
            }
            Flow {
                width: parent.width; spacing: 4
                visible: card.ctls.length > 0
                Repeater {
                    model: card.presets
                    Key { required property string modelData; label: modelData.toUpperCase(); on: card.face.preset === modelData; tip: "quick face: " + modelData; onHit: host.faceCmd("preset " + modelData) }
                }
            }

            // ---- flex scale, randomize, zero, export ------------------------------------------
            Row {
                spacing: 6
                visible: card.ctls.length > 0
                Text { anchors.verticalCenter: parent.verticalCenter; width: 70; text: "FLEX SCALE"; color: card.text; font.family: card.mono; font.pixelSize: 10 }
                Bar {
                    id: scaleBar
                    width: 150; from: -5; to: 5; stepSize: 0.05
                    value: card.face.scale === undefined ? 1 : card.face.scale
                    onMoved: host.faceCmd("scale " + value.toFixed(2))
                    C.ToolTip.visible: hovered; C.ToolTip.delay: 450
                    C.ToolTip.text: "turns every flex up or down at once (negative turns the shapes inside out); 1 = as set"
                }
                Text { anchors.verticalCenter: parent.verticalCenter; text: card.num(scaleBar.value); color: card.dim; font.family: card.mono; font.pixelSize: 10 }
            }
            Row {
                spacing: 4
                visible: card.ctls.length > 0
                Key { label: "RANDOMIZE"; tip: "a random face, kept mild"; onHit: host.faceCmd("random") }
                Key { label: "ZERO"; tip: "every slider back to 0: the neutral face"; onHit: host.faceCmd("zero") }
                Key { label: "EXPORT"; tip: "write the presets as an s&box .vmdl_prefab of MorphFrames next to the model (viseme_* ones drive lip-sync)"; onHit: host.faceCmd("export") }
            }
            C.TextField {
                width: parent.width; height: 22
                visible: card.ctls.length > 0
                placeholderText: "filter sliders…"
                font.family: card.mono; font.pixelSize: 10
                color: card.text
                background: Rectangle { color: card.edge; radius: 3 }
                onTextChanged: card.filter = text.trim()
            }

            // ---- one form per group, a slider per controller ----------------------------------
            Repeater {
                model: card.groups
                Column {
                    id: grp
                    required property string modelData
                    width: body.width
                    spacing: 3
                    Rectangle {
                        width: parent.width; height: 20; radius: 3; color: card.steel; border.color: card.edge
                        Text {
                            x: 6; anchors.verticalCenter: parent.verticalCenter
                            text: (card.folded[grp.modelData] ? "▸ " : "▾ ") + grp.modelData.toUpperCase()
                            color: card.text; font.family: card.mono; font.bold: true; font.pixelSize: 10
                        }
                        MouseArea { anchors.fill: parent; cursorShape: Qt.PointingHandCursor; onClicked: card.fold(grp.modelData) }
                        Row {
                            anchors.right: parent.right; anchors.rightMargin: 3; anchors.verticalCenter: parent.verticalCenter
                            spacing: 3
                            Key {
                                height: 16; label: "RND"; tip: "randomize just " + grp.modelData
                                onHit: card.ctls.filter(c => c.group === grp.modelData).forEach(c =>
                                    host.faceCmd("set " + c.name + " " + (c.min + Math.random() * (c.max - c.min) * 0.7).toFixed(2)))
                            }
                            Key {
                                height: 16; label: "CLR"; tip: "zero just " + grp.modelData
                                onHit: card.ctls.filter(c => c.group === grp.modelData).forEach(c => host.faceCmd("set " + c.name + " 0 0.5"))
                            }
                        }
                    }
                    Repeater {
                        model: card.names.filter(n => card.ctl(n).group === grp.modelData)
                        Column {
                            id: row
                            required property string modelData
                            readonly property var c: card.ctl(modelData)
                            visible: card.shown(modelData)
                            width: grp.width
                            Row {
                                spacing: 4
                                Text {
                                    anchors.verticalCenter: parent.verticalCenter; width: 104; elide: Text.ElideRight
                                    text: card.nice(row.modelData); color: card.text; font.family: card.mono; font.pixelSize: 10
                                    MouseArea { id: tipM; anchors.fill: parent; hoverEnabled: true }
                                    C.ToolTip.visible: tipM.containsMouse; C.ToolTip.delay: 450
                                    C.ToolTip.text: row.c.tip + "\nkey" + (row.c.stereo ? "s " + row.modelData + "_L / _R" : ": " + row.modelData)
                                }
                                Bar {
                                    id: v
                                    width: grp.width - 104 - 40 - 8
                                    from: row.c.min; to: row.c.max; stepSize: 0.01
                                    onMoved: host.faceCmd("set " + row.modelData + " " + value.toFixed(2))
                                    Binding on value { value: row.c.value; when: !v.pressed }
                                }
                                Text { anchors.verticalCenter: parent.verticalCenter; width: 40; text: card.num(v.value); color: card.dim; font.family: card.mono; font.pixelSize: 10 }
                            }
                            // Faceposer's left/right: the right end is the character's right
                            Row {
                                visible: row.c.stereo
                                spacing: 4
                                Text { anchors.verticalCenter: parent.verticalCenter; width: 104; horizontalAlignment: Text.AlignRight; text: "L ◂ ▸ R"; color: card.dim; font.family: card.mono; font.pixelSize: 9 }
                                Bar {
                                    id: b
                                    width: grp.width - 104 - 40 - 8
                                    fill: card.dim
                                    // shown left-to-right as L..R; Valve's balance is 1 = left, 0 = right
                                    from: 1; to: 0; stepSize: 0.01
                                    onMoved: host.faceCmd("set " + row.modelData + " " + v.value.toFixed(2) + " " + value.toFixed(2))
                                    Binding on value { value: row.c.balance; when: !b.pressed }
                                    C.ToolTip.visible: hovered; C.ToolTip.delay: 450
                                    C.ToolTip.text: "balance: middle = both sides, drag towards L or R to fade the other side out"
                                }
                                Text { anchors.verticalCenter: parent.verticalCenter; width: 40; text: Math.abs(b.value - 0.5) < 0.01 ? "both" : b.value > 0.5 ? "L" : "R"; color: card.dim; font.family: card.mono; font.pixelSize: 9 }
                            }
                        }
                    }
                }
            }

            // ---- TALK: the lip-sync visemes, as s&box names them ------------------------------
            Rectangle {
                visible: card.ctls.length > 0 && card.visemes.length > 0
                width: parent.width; height: 20; radius: 3; color: card.steel; border.color: card.edge
                Text { x: 6; anchors.verticalCenter: parent.verticalCenter; text: "TALK (VISEMES)"; color: card.text; font.family: card.mono; font.bold: true; font.pixelSize: 10 }
            }
            Flow {
                width: parent.width; spacing: 4
                visible: card.ctls.length > 0
                Repeater {
                    model: card.visemes
                    Key { required property string modelData; label: modelData.slice(7); on: card.face.preset === modelData; tip: modelData + ": the mouth shape s&box lip-sync uses for it"; onHit: host.faceCmd("preset " + modelData) }
                }
            }
            Text {
                visible: (card.face.missing || 0) > 0 && card.ctls.length > 0
                width: parent.width; wrapMode: Text.WordWrap
                color: card.dim; font.family: card.mono; font.pixelSize: 9
                text: card.face.missing + " more keys would add sliders (see face-rigging.md for the list)"
            }
        }
    }
}
