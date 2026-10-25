// One identical, viewport-local control in every layout. No global chat/session controls.
import QtQuick
import QtQuick.Controls as C
import QtQuick.Window

Item {
    id: review
    required property int pane
    property string runDir: ""
    property bool active: false
    property bool open: false
    property bool paneVisible: true
    property real textZoom: 1
    readonly property string runName: runDir ? runDir.split("/").pop() : ""
    readonly property var session: host.corrections.sessions[String(pane)] || ({})
    readonly property bool sameRun: !!runDir && session.dir === runDir
    readonly property bool holding: sameRun && !!session.holding
    readonly property bool ready: holding && !!session.ready
    readonly property bool canCorrect: ready && !session.legacy && session.correctable !== false
    readonly property var queued: session.queued || []
    readonly property int queuedChars: queued.map(function(m, i) { return (i + 1) + ". " + m.text }).join("\n\n").length
    readonly property int proposedChars: queuedChars + (queued.length ? 2 : 0) + String(queued.length + 1).length + 2 + field.text.trim().length
    readonly property color accent: "#e3ae60"
    readonly property color ink: "#e7e9ec"
    readonly property string mono: "DejaVu Sans Mono"
    objectName: "viewportCorrection" + pane
    z: 30
    onRunDirChanged: { open = false; field.text = "" }

    function stop() {
        const stopped = host.corrections.stop(pane);
        if (stopped || (session.dir === runDir && session.error)) {
            open = true;
            chatWindow.raise();
            chatWindow.requestActivate();
            field.forceActiveFocus();
        }
    }
    function send() {
        const rev = stepPick.currentIndex >= 0 && stepPick.currentIndex < stepPick.model.length
                  ? stepPick.model[stepPick.currentIndex].rev : session.revision || 0;
        if (host.corrections.queue(pane, rev, field.text)) {
            field.text = "";
            field.forceActiveFocus();
            Qt.callLater(function() { scroll.contentItem.contentY = Math.max(0, scroll.contentItem.contentHeight - scroll.contentItem.height) });
        } else scroll.contentItem.contentY = 0;
    }

    C.Button {
        id: stopKey
        objectName: "viewportStop" + review.pane
        anchors.horizontalCenter: parent.horizontalCenter
        anchors.bottom: parent.bottom
        anchors.bottomMargin: 12
        visible: review.paneVisible && (review.active || review.holding || (review.sameRun && review.session.sent))
        text: review.holding ? "CHAT" : "PAUSE"
        implicitWidth: stopLabel.implicitWidth + 42
        implicitHeight: Math.max(32, stopLabel.implicitHeight + 16)
        padding: 0
        onClicked: {
            if (review.holding) {
                review.open = true;
                chatWindow.raise(); chatWindow.requestActivate();
            }
            else review.stop();
        }
        contentItem: Row {
            spacing: 8
            leftPadding: 14
            Rectangle {
                anchors.verticalCenter: parent.verticalCenter
                width: 7; height: 7; radius: review.holding ? 4 : 1
                color: review.holding ? review.accent : "#eb8c79"
            }
            Text {
                id: stopLabel
                anchors.verticalCenter: parent.verticalCenter
                text: stopKey.text; color: review.ink
                font.family: review.mono; font.pixelSize: 11 * review.textZoom; font.bold: true
            }
        }
        background: Rectangle {
            radius: 7; color: stopKey.hovered ? "#34353a" : "#ef25282d"
            border.color: stopKey.activeFocus ? review.accent : "#55545a"
        }
        C.ToolTip.visible: hovered
        C.ToolTip.text: "Pause this viewport’s build at the next safe step and give a correction. The model is kept."
    }

    Window {
        id: chatWindow
        objectName: "viewportCorrectionWindow" + review.pane
        title: "Geppetto · Correction · View " + review.pane + " · " + review.runName
        transientParent: null
        flags: Qt.Window | Qt.FramelessWindowHint
        modality: Qt.NonModal
        width: 500; height: 580
        minimumWidth: Math.max(380, 300 * review.textZoom)
        minimumHeight: Math.max(520, 440 * review.textZoom)
        visible: review.open && review.sameRun
        color: "#181b20"
        onClosing: review.open = false
        Shortcut { sequences: [StandardKey.Close]; onActivated: review.open = false }
        Shortcut { sequence: "Escape"; onActivated: review.open = false }

        Rectangle { anchors.fill: parent; color: "#353a42"; border.color: "#090b0e"; border.width: 2; radius: 5 }
        Rectangle { anchors.fill: parent; anchors.margins: 3; color: "#24282f"; border.color: "#555b64"; radius: 3 }
        // The cabinet's enamel, inset plate and chrome pull, not a generic modal.
        Rectangle {
            id: lid
            x: 8; y: 8; width: parent.width - 16
            height: titleBlock.height + 38
            color: "#2b3038"; border.color: "#111419"; radius: 3
            Rectangle { x: 2; y: 2; width: parent.width - 4; height: 1; color: "#555c66" }
            MouseArea {
                anchors.fill: parent
                cursorShape: Qt.SizeAllCursor
                onPressed: chatWindow.startSystemMove()
            }
            Column {
                id: titleBlock
                x: 22; y: 16; width: parent.width - 76; spacing: 8
                Text {
                    width: parent.width
                    text: "CORRECTION  /  VIEW " + review.pane
                    color: review.accent; font.family: review.mono
                    font.pixelSize: 12 * review.textZoom; font.bold: true
                    wrapMode: Text.Wrap; maximumLineCount: 2; elide: Text.ElideRight
                }
                Text {
                    width: parent.width
                    text: review.runName; textFormat: Text.PlainText
                    color: "#bec4cb"; font.pixelSize: 11 * review.textZoom
                    wrapMode: Text.WrapAnywhere; maximumLineCount: 2; elide: Text.ElideMiddle
                    C.ToolTip.visible: runHover.hovered
                    C.ToolTip.text: review.runName
                    HoverHandler { id: runHover }
                }
            }
            C.ToolButton {
                id: closeKey
                objectName: "viewportCorrectionClose" + review.pane
                anchors.right: parent.right; anchors.top: parent.top; anchors.margins: 10
                width: 30; height: 30
                contentItem: Text {
                    text: "×"; color: "#c9ced4"; font.pixelSize: 20
                    horizontalAlignment: Text.AlignHCenter; verticalAlignment: Text.AlignVCenter
                }
                background: Rectangle { radius: 3; color: closeKey.hovered ? "#444b55" : "#20252c"; border.color: "#12161c" }
                onClicked: review.open = false
                C.ToolTip.visible: hovered
                C.ToolTip.text: "Close chat. The build stays held."
            }
        }
        Rectangle {
            id: pull
            anchors.top: lid.bottom; anchors.topMargin: 8
            anchors.horizontalCenter: parent.horizontalCenter
            width: 72; height: 6; radius: 3
            gradient: Gradient {
                GradientStop { position: 0; color: "#7b828a" }
                GradientStop { position: 0.35; color: "#d2d5d5" }
                GradientStop { position: 1; color: "#474d55" }
            }
        }
        Rectangle {
            id: panel
            objectName: "viewportCorrectionPanel" + review.pane
            x: 16; y: pull.y + pull.height + 12
            width: parent.width - 32
            height: Math.max(0, (footer.visible ? footer.y - 12 : parent.height - 20) - y)
            color: "#eef0e7"; border.color: "#11151b"; radius: 3
            clip: true
        C.ScrollView {
            id: scroll
            anchors.fill: parent; anchors.margins: 14
            anchors.bottomMargin: composer.visible ? composer.height + 26 : 14
            contentWidth: availableWidth
            clip: true
            C.ScrollBar.horizontal.policy: C.ScrollBar.AlwaysOff
            Column {
                id: body
                width: scroll.availableWidth
                spacing: 12
                Text {
                    width: parent.width
                    text: review.session.status || "Stopping…"
                    color: review.session.error ? "#913e32" : "#424c50"
                    font.pixelSize: 13 * review.textZoom; wrapMode: Text.Wrap
                    textFormat: Text.PlainText
                }
                Repeater {
                    model: review.session.messages || []
                    Rectangle {
                        required property var modelData
                        required property int index
                        objectName: "correctionMessage" + review.pane + "-" + index
                        width: body.width; height: message.y + message.implicitHeight + 10
                        radius: 3; color: "#dce1d5"
                        Text {
                            id: messageState
                            x: 10; y: 10; width: parent.width - 46
                            text: "YOU · " + (modelData.status === "queued" ? "QUEUED — NOT SENT" : modelData.status === "pending" ? "AWAITING ACCEPTANCE" : "SENT TO MODELLER")
                            color: "#576457"; font.family: review.mono; font.pixelSize: 9 * review.textZoom
                            wrapMode: Text.Wrap; maximumLineCount: 2; elide: Text.ElideRight
                        }
                        C.ToolButton {
                            objectName: "correctionRemove" + review.pane + "-" + index
                            anchors.right: parent.right; anchors.top: parent.top; anchors.margins: 4
                            width: 28; height: 28; visible: modelData.status === "queued"
                            onClicked: host.corrections.removeQueued(review.pane, modelData.id)
                            contentItem: Text { text: "×"; color: "#576457"; font.pixelSize: 17; horizontalAlignment: Text.AlignHCenter; verticalAlignment: Text.AlignVCenter }
                            C.ToolTip.visible: hovered
                            C.ToolTip.text: "Remove this unsent message."
                        }
                        Text {
                            id: message
                            x: 10; y: messageState.y + messageState.height + 6; width: parent.width - 20
                            text: modelData.text; textFormat: Text.PlainText
                            color: "#273337"; font.pixelSize: 13 * review.textZoom; wrapMode: Text.Wrap
                        }
                    }
                }
            }
        }
            // Keep the composer reachable while the independent message history scrolls.
            Column {
                id: composer
                x: 14; anchors.bottom: parent.bottom; anchors.bottomMargin: 14
                width: parent.width - 28; spacing: 8
                visible: review.holding
                C.ComboBox {
                    id: stepPick
                    objectName: "viewportCorrectionStep" + review.pane
                    width: parent.width
                    visible: review.holding && model.length > 0
                    enabled: review.canCorrect && review.queued.length === 0
                    model: review.session.steps || []
                    currentIndex: {
                        if (review.queued.length) {
                            for (let i = 0; i < model.length; i++) if (model[i].rev === review.session.queueRevision) return i;
                        }
                        return model.length - 1;
                    }
                    textRole: "rev"
                    displayText: "Step " + (currentValue || review.session.revision || 0)
                        + (currentIndex >= 0 && model[currentIndex] ? " · " + model[currentIndex].op : "")
                    valueRole: "rev"
                    onActivated: host.corrections.preview(review.pane, currentValue)
                    C.ToolTip.visible: hovered
                    C.ToolTip.text: displayText
                    contentItem: Text {
                        text: stepPick.displayText; color: "#303b3f"
                        verticalAlignment: Text.AlignVCenter; elide: Text.ElideRight
                        leftPadding: 10; rightPadding: 26; font.pixelSize: 12 * review.textZoom
                    }
                    background: Rectangle { color: "#e0e4d9"; radius: 3; border.color: "#a5ada3" }
                    delegate: C.ItemDelegate {
                        required property var modelData
                        width: stepPick.width
                        contentItem: Text {
                            text: "Step " + modelData.rev + " · " + (modelData.op || "")
                            color: review.ink; elide: Text.ElideRight; font.pixelSize: 12 * review.textZoom
                        }
                        background: Rectangle { color: parent.highlighted ? "#434951" : "#262b32" }
                    }
                    popup.width: width
                    popup.height: Math.min(220, chatWindow.height * 0.4)
                }
                C.ScrollView {
                    id: composerField
                    width: parent.width; height: 110 * review.textZoom
                    contentWidth: availableWidth
                    clip: true
                    C.ScrollBar.horizontal.policy: C.ScrollBar.AlwaysOff
                    C.TextArea {
                    id: field
                    objectName: "viewportCorrectionInput" + review.pane
                    width: composerField.availableWidth
                    visible: review.holding
                    // Let the user compose while the current atomic edit finishes;
                    // only submitting waits for the acknowledged, displayed step.
                    enabled: review.holding && !review.session.legacy && review.session.correctable !== false
                    placeholderText: "What should change?"
                    color: "#263235"; placeholderTextColor: "#737e7c"
                    font.pixelSize: 14 * review.textZoom; wrapMode: TextEdit.Wrap
                    selectByMouse: true
                    implicitHeight: Math.max(composerField.height, contentHeight + 24)
                    padding: 12
                    background: Rectangle {
                        color: "#f8f9f1"; radius: 3
                        border.color: field.activeFocus ? "#a27637" : "#adb6aa"
                    }
                    Keys.onPressed: function(event) {
                        if ((event.modifiers & Qt.ControlModifier) && (event.key === Qt.Key_Return || event.key === Qt.Key_Enter)) {
                            review.send(); event.accepted = true;
                        }
                    }
                }
                }
            }
        }
        Column {
            id: footer
            x: 18; anchors.bottom: parent.bottom; anchors.bottomMargin: 22
            width: parent.width - 36; spacing: 8
            visible: review.holding
            Text {
                width: parent.width
                text: review.queued.length + " queued · " + (field.text.trim().length ? review.proposedChars : review.queuedChars) + "/1600 characters\nCtrl+Enter stacks a message. Apply sends the stack."
                color: field.text.trim().length && review.proposedChars > 1600 ? "#eea18e" : "#aab2bc"
                font.pixelSize: 10 * review.textZoom; wrapMode: Text.Wrap
            }
            Row {
                width: parent.width; spacing: 10
            C.Button {
                    objectName: "viewportCorrectionSend" + review.pane
                    width: (parent.width - parent.spacing) / 2
                    enabled: review.canCorrect && field.text.trim().length > 0 && review.proposedChars <= 1600 && review.queued.length < 16
                    text: "Send message"
                    onClicked: review.send()
                    contentItem: Text {
                        text: parent.text; color: parent.enabled ? "#1c2026" : "#929aa6"
                        font.pixelSize: 12 * review.textZoom; font.bold: true
                        horizontalAlignment: Text.AlignHCenter; verticalAlignment: Text.AlignVCenter
                        wrapMode: Text.Wrap
                    }
                    background: Rectangle { radius: 6; color: parent.enabled ? "#c3cabd" : "#343941" }
                }
            C.Button {
                    objectName: "viewportCorrectionApply" + review.pane
                    width: (parent.width - parent.spacing) / 2
                    enabled: review.canCorrect && review.queued.length > 0 && field.text.trim().length === 0
                    text: "Apply & continue"
                    C.ToolTip.visible: hovered
                    C.ToolTip.text: "Send the entire queued stack to this modeller, fix the model, then continue. Send or clear any draft first."
                    onClicked: host.corrections.apply(review.pane)
                    contentItem: Text {
                        text: parent.text; color: parent.enabled ? "#1c2026" : "#929aa6"; font.pixelSize: 12 * review.textZoom
                        wrapMode: Text.Wrap; horizontalAlignment: Text.AlignHCenter
                        verticalAlignment: Text.AlignVCenter
                    }
                    background: Rectangle { radius: 6; color: parent.enabled ? review.accent : "#343941" }
                }
            }
            C.Button {
                objectName: "viewportCorrectionContinue" + review.pane
                width: parent.width
                enabled: review.ready && review.queued.length === 0 && !review.session.pendingDirection && field.text.trim().length === 0
                text: "Continue without changes"
                onClicked: if (host.corrections.resume(review.pane)) review.open = false
                contentItem: Text {
                    text: parent.text; color: parent.enabled ? "#bdc3cb" : "#727b86"; font.pixelSize: 11 * review.textZoom
                    wrapMode: Text.Wrap; horizontalAlignment: Text.AlignHCenter; verticalAlignment: Text.AlignVCenter
                }
                background: Rectangle { color: "transparent" }
            }
        }
        // Native Wayland resize/move gestures; this never touches the real cursor.
        MouseArea {
            anchors.right: parent.right; anchors.bottom: parent.bottom
            width: 18; height: 18; cursorShape: Qt.SizeFDiagCursor
            onPressed: chatWindow.startSystemResize(Qt.RightEdge | Qt.BottomEdge)
            Text { anchors.centerIn: parent; text: "◢"; color: "#77808b"; font.pixelSize: 13 }
        }
    }
}
