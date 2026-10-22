// The CLAUDE card: talk to Claude Code from the chest (host.py -> claude_link.py, a Claude Code
// session of its own working in this checkout). Its replies and every tool it runs scroll past
// here; a tool that needs your OK waits on ALLOW / DENY. PAUSE holds it at its next tool call and
// pauses the runs it started; STOP cuts the turn off, refuses its next tool and cancels its runs.
// Folded it is just the title, the keys and the line you type in, so the model stays in view.
import QtQuick
import QtQuick.Controls as C

Item {
    id: card
    property bool open: false
    property bool folded: false
    property real maxHeight: 420
    property real bigHeight: 700
    property bool big: false                // BIG: wider and taller, to read everything it does
    property var expanded: ({})             // log rows opened by a click (full output, all of a thought)
    property int spinStep: 0
    readonly property string spin: "◐◓◑◒"[spinStep % 4]
    Timer { interval: 150; repeat: true; running: card.visible && card.busy; onTriggered: card.spinStep++ }
    function toggle(i) { const e = Object.assign({}, expanded); e[i] = !e[i]; expanded = e }
    signal closed()

    readonly property var messages: host.claudeMessages
    readonly property string status: host.claudeStatus
    readonly property string ask: host.claudeAsk
    readonly property bool busy: status === "working" || status === "starting" || status === "paused"

    readonly property color steel: "#2b2f36"
    readonly property color steelHi: "#3d424b"
    readonly property color steelLo: "#1f2228"
    readonly property color edge: "#0b0c0e"
    readonly property color lit: "#f2a91c"
    readonly property color text: "#c9ccd0"
    readonly property color dim: "#7a7f88"
    readonly property color bad: "#e0604a"
    readonly property color you: "#9fc7e8"
    readonly property string mono: "DejaVu Sans Mono"

    width: big ? 560 : 340
    height: folded ? head.height + strip.space + inputRow.height + (ask ? askRow.height + 4 : 0) + 16 : big ? bigHeight : maxHeight
    Behavior on width { NumberAnimation { duration: 120 } }
    visible: open
    opacity: open ? 1 : 0
    Behavior on opacity { NumberAnimation { duration: 160 } }
    Behavior on height { NumberAnimation { duration: 120 } }

    function send() {
        const t = field.text.trim();
        if (t === "") return;
        host.claudeSend(t);
        field.text = "";
    }
    function focusInput() { field.forceActiveFocus() }
    signal pictureClicked(string path)

    Rectangle { anchors.fill: parent; color: card.steelLo; opacity: 0.9; border.color: card.edge; border.width: 1; radius: 4 }

    // ---- title, state lamp and the keys that matter most: always in reach --------------------
    Item {
        id: head
        x: 5; y: 5; width: parent.width - 10; height: 22
        Rectangle {
            id: lamp
            width: 9; height: 9; radius: 5; anchors.verticalCenter: parent.verticalCenter
            color: card.status === "working" || card.status === "starting" ? card.lit
                 : card.status === "paused" ? "#e8d36a" : card.status === "stopped" ? card.bad
                 : card.status === "offline" ? card.dim : "#7fc77a"
            SequentialAnimation on opacity {
                running: card.status === "working" || card.status === "starting"; loops: Animation.Infinite
                NumberAnimation { to: 0.3; duration: 500 } NumberAnimation { to: 1; duration: 500 }
                onRunningChanged: if (!running) lamp.opacity = 1
            }
        }
        Text {
            x: 14; anchors.verticalCenter: parent.verticalCenter
            width: parent.width - keys.width - 20; elide: Text.ElideRight
            text: (card.folded ? "▸ " : "▾ ") + "CLAUDE  " + (card.ask ? "needs OK" : card.status)
            color: card.lit; font.family: card.mono; font.bold: true; font.pixelSize: 11
            MouseArea { anchors.fill: parent; cursorShape: Qt.PointingHandCursor; onClicked: card.folded = !card.folded }
        }
        Row {
            id: keys
            anchors.right: parent.right; anchors.verticalCenter: parent.verticalCenter; spacing: 3
            CardKey {
                objectName: "claudePause"
                text: card.status === "paused" ? "RESUME" : "PAUSE"; hot: card.status === "paused"
                enabled: card.status === "working" || card.status === "paused"
                onClicked: host.claudePause()
                tip: "hold Claude at its next tool call and pause the runs it started"
            }
            CardKey {
                objectName: "claudeStop"
                text: "STOP"; danger: true
                enabled: card.busy || card.ask !== ""
                onClicked: host.claudeStop()
                tip: "stop Claude now and cancel the runs it started"
            }
            CardKey {
                text: "NEW"; enabled: !card.busy
                onClicked: { card.expanded = {}; host.claudeNew() }
                tip: "start a fresh conversation"
            }
            CardKey { objectName: "claudeBig"; text: card.big ? "SMALL" : "BIG"; hot: card.big; onClicked: card.big = !card.big; tip: "a bigger card to read everything it does" }
            CardKey { text: "X"; onClicked: card.closed() }
        }
    }

    // ---- pictures it looked at or showed you, newest right; a click pops one large again ----
    ListView {
        id: strip
        objectName: "claudeStrip"
        readonly property real space: count > 0 ? height + 4 : 0
        x: 5; y: head.y + head.height + 4
        width: parent.width - 10; height: count > 0 ? 40 : 0
        visible: count > 0
        orientation: ListView.Horizontal; spacing: 3; clip: true
        model: host.claudePictures
        onCountChanged: positionViewAtEnd()
        delegate: Rectangle {
            required property string modelData
            width: 52; height: 40; color: card.edge; border.color: card.steelHi; border.width: 1
            Image {
                anchors.fill: parent; anchors.margins: 1
                source: "file://" + parent.modelData; fillMode: Image.PreserveAspectCrop
                sourceSize.width: 104; asynchronous: true; cache: false
            }
            MouseArea { anchors.fill: parent; cursorShape: Qt.PointingHandCursor; onClicked: card.pictureClicked(parent.modelData) }
        }
    }

    // ---- the conversation ------------------------------------------------------------------
    ListView {
        id: log
        objectName: "claudeLog"
        visible: !card.folded
        x: 5; y: strip.y + strip.space
        width: parent.width - 10
        height: parent.height - y - inputRow.height - (card.ask ? askRow.height + 4 : 0) - 12
        clip: true; spacing: 4
        boundsBehavior: Flickable.StopAtBounds
        model: card.messages
        // follow the newest line while it streams in; scroll up to read back and it stops following
        property bool follow: true
        onMovementEnded: follow = atYEnd
        onModelChanged: if (follow) Qt.callLater(positionViewAtEnd)
        onHeightChanged: if (follow) positionViewAtEnd()
        C.ScrollBar.vertical: C.ScrollBar { policy: C.ScrollBar.AsNeeded }
        delegate: Item {
            id: row
            required property var modelData
            required property int index
            readonly property string role: modelData.role
            readonly property bool open: card.expanded[index] === true
            width: log.width - 8
            height: col.implicitHeight
            Rectangle {     // subagent work hangs off a rail
                visible: !!row.modelData.sub; x: 1; width: 2; height: parent.height; color: card.steelHi
            }
            Column {
                id: col
                x: row.modelData.sub ? 8 : 0; width: parent.width - x; spacing: 1
                Text {
                    width: parent.width
                    wrapMode: Text.Wrap
                    visible: row.role !== "out"
                    textFormat: row.role === "claude" && !row.modelData.live ? Text.MarkdownText : Text.PlainText
                    text: row.role === "you" ? "› " + row.modelData.text
                        : row.role === "tool" ? (row.modelData.done ? (row.modelData.error ? "✗ " : "✓ ") : card.spin + " ") + row.modelData.text
                        : row.role === "ask" ? "? " + row.modelData.text
                        : row.role === "think" ? row.modelData.text + (row.modelData.live ? " " + card.spin : "")
                        : row.modelData.text + (row.modelData.live ? "▍" : "")
                    color: row.role === "you" ? card.you : row.role === "claude" ? card.text
                         : row.role === "ask" ? card.lit : row.role === "tool" ? (row.modelData.error ? card.bad : row.modelData.done ? "#a9b4a0" : card.lit)
                         : row.role === "note" && /^(refused|denied|stopped|claude exited)/.test(row.modelData.text) ? card.bad : card.dim
                    font.family: card.mono; font.italic: row.role === "think"
                    font.bold: row.role === "tool"
                    font.pixelSize: row.role === "claude" || row.role === "you" ? 11 : row.role === "think" ? 9 : 10
                    maximumLineCount: row.role === "think" && !row.open && !row.modelData.live ? 3 : 100000
                    elide: Text.ElideRight
                    onLinkActivated: link => Qt.openUrlExternally(link)
                    MouseArea { anchors.fill: parent; enabled: row.role === "think"; onClicked: card.toggle(row.index) }
                }
                Text {      // the command, the edit, the file: what the tool was told to do
                    visible: row.role === "tool" && !!row.modelData.detail
                    x: 12; width: parent.width - 12
                    wrapMode: Text.WrapAnywhere
                    text: row.modelData.detail || ""
                    color: "#8fa3b8"; font.family: card.mono; font.pixelSize: 9
                }
                Rectangle {     // what came back: click to see all of it
                    visible: row.role === "out"
                    x: 12; width: parent.width - 12
                    height: outText.implicitHeight + 6
                    color: "#15171b"; radius: 2
                    border.color: row.modelData.error ? card.bad : card.edge
                    Text {
                        id: outText
                        x: 4; y: 3; width: parent.width - 8
                        wrapMode: Text.WrapAnywhere
                        text: row.open && row.modelData.full ? row.modelData.full : row.modelData.text
                        color: row.modelData.error ? card.bad : "#9aa0a8"
                        font.family: card.mono; font.pixelSize: 9
                    }
                    MouseArea { anchors.fill: parent; cursorShape: Qt.PointingHandCursor; onClicked: card.toggle(row.index) }
                }
            }
        }
        Text {
            anchors.centerIn: parent; width: parent.width - 20; visible: log.count === 0
            horizontalAlignment: Text.AlignHCenter; wrapMode: Text.Wrap
            text: "Talk to Claude here. It works in this checkout, builds with geppetto-live so you see it here, and STOP stops it."
            color: card.dim; font.family: card.mono; font.pixelSize: 10
        }
    }

    // ---- a tool waiting on your OK -----------------------------------------------------------
    Rectangle {
        id: askRow
        visible: card.ask !== ""
        x: 5; width: parent.width - 10; height: askText.implicitHeight + 32
        anchors.bottom: inputRow.top; anchors.bottomMargin: 4
        color: card.steel; border.color: card.lit; border.width: 1; radius: 3
        Text {
            id: askText
            x: 5; y: 4; width: parent.width - 10; wrapMode: Text.Wrap; maximumLineCount: 4; elide: Text.ElideRight
            text: "wants to run: " + card.ask
            color: card.lit; font.family: card.mono; font.pixelSize: 10
        }
        Row {
            anchors.right: parent.right; anchors.bottom: parent.bottom; anchors.margins: 4; spacing: 3
            CardKey { objectName: "claudeAllow"; text: "ALLOW"; hot: true; onClicked: host.claudeAnswer(true) }
            CardKey { objectName: "claudeDeny"; text: "DENY"; onClicked: host.claudeAnswer(false) }
        }
    }

    // ---- what you type ------------------------------------------------------------------------
    Rectangle {
        id: inputRow
        x: 5; width: parent.width - 10; height: Math.min(72, Math.max(24, field.contentHeight + 10))
        anchors.bottom: parent.bottom; anchors.bottomMargin: 5
        radius: 3
        color: field.activeFocus ? "#f4f7ee" : card.steel; border.color: field.activeFocus ? card.lit : card.edge
        Flickable {
            id: fieldFlick
            anchors.fill: parent; anchors.leftMargin: 5; anchors.rightMargin: sendKey.width + 8
            anchors.topMargin: 4; anchors.bottomMargin: 4
            contentHeight: field.contentHeight; clip: true
            boundsBehavior: Flickable.StopAtBounds
            TextEdit {
                id: field
                objectName: "claudeInput"
                width: fieldFlick.width
                wrapMode: TextEdit.Wrap
                color: activeFocus ? "#1d2318" : card.text
                selectionColor: card.lit
                font.family: card.mono; font.pixelSize: 11
                selectByMouse: true
                // Enter sends, Shift+Enter makes a new line; Escape gives the keys back to the viewport
                Keys.onReturnPressed: event => { if (event.modifiers & Qt.ShiftModifier) event.accepted = false; else card.send() }
                Keys.onEnterPressed: card.send()
                Keys.onEscapePressed: field.focus = false
                onCursorRectangleChanged: if (cursorRectangle.y + cursorRectangle.height > fieldFlick.contentY + fieldFlick.height)
                                              fieldFlick.contentY = cursorRectangle.y + cursorRectangle.height - fieldFlick.height
            }
            Text {
                visible: field.text === "" && !field.activeFocus
                text: card.busy ? "add something while it works…" : "tell Claude what to make…"
                color: card.dim; font.family: card.mono; font.pixelSize: 11
            }
        }
        CardKey {
            id: sendKey
            anchors.right: parent.right; anchors.rightMargin: 3; anchors.verticalCenter: parent.verticalCenter
            text: "SEND"; hot: field.text.trim() !== ""
            onClicked: card.send()
        }
    }

    component CardKey: Rectangle {
        id: ck
        property string text: ""
        property string tip: ""
        property bool hot: false
        property bool danger: false
        signal clicked()
        width: Math.max(30, t.implicitWidth + 10); height: 20; radius: 3
        opacity: enabled ? 1 : 0.4
        color: ckm.pressed ? card.steelLo : ckm.containsMouse ? card.steelHi : card.steel
        border.color: danger && enabled ? card.bad : hot && enabled ? card.lit : card.edge
        border.width: hot || danger ? 2 : 1
        Text { id: t; anchors.centerIn: parent; text: ck.text; color: ck.danger ? card.bad : ck.hot ? card.lit : card.text; font.family: card.mono; font.bold: true; font.pixelSize: 10 }
        MouseArea { id: ckm; anchors.fill: parent; hoverEnabled: true; enabled: ck.enabled; cursorShape: Qt.PointingHandCursor; onClicked: ck.clicked() }
        C.ToolTip.visible: ck.tip !== "" && ckm.containsMouse
        C.ToolTip.delay: 500
        C.ToolTip.text: ck.tip
    }
}
