// In-cabinet chat: workshop radio shell and paper dispatch transcript.
// Keep the incremental host model, provider catalogue, modes, research choices,
// tool approvals and keyboard composer shared with the existing chat backend.
import QtQuick
import QtQuick.Controls as C

Item {
    id: card
    property bool open: false
    property bool folded: false
    property real maxHeight: 620
    property real bigHeight: 700
    property bool big: false                // BIG: wider and taller, to read everything it does
    property var expanded: ({})             // log rows opened by a click (full output, all of a thought)
    property int spinStep: 0
    readonly property string spin: "◐◓◑◒"[spinStep % 4]
    Timer { interval: 150; repeat: true; running: card.visible && card.busy; onTriggered: card.spinStep++ }
    // Pixel display opens from the walkie grille.
    transformOrigin: Item.BottomLeft
    property real reveal: open ? 1 : 0
    onOpenChanged: if (open) { reveal = 0; revealAnim.restart() }
    NumberAnimation { id: revealAnim; target: card; property: "reveal"; from: 0; to: 1; duration: 650; easing.type: Easing.OutCubic }
    readonly property real contentReveal: Math.max(0, Math.min(1, (reveal - 0.84) / 0.16))
    function toggle(i) { const e = Object.assign({}, expanded); e[i] = !e[i]; expanded = e }
    signal closed()

    readonly property var messages: host.claudeMessages
    readonly property string status: host.claudeStatus
    readonly property string ask: host.claudeAsk
    readonly property bool busy: status === "working" || status === "starting" || status === "paused"
    readonly property string phase: messages ? messages.phase : ""
    readonly property var museTracker: host.museTracker
    Timer { interval: 1000; repeat: true; running: card.open && card.busy && card.isMuseBridge; onTriggered: host.refreshMuseTracker() }
    // Muse reply notification (port 2026-10-25, standalone v2): when the
    // tracker flips to "Reply written" for a turn not yet notified, and
    // the app is not the active application, ask the host for one desktop
    // notification. Never while focused; once per turn only.
    property string notifiedMuseTurn: ""
    Connections {
        target: host
        function onMuseTrackerChanged() {
            const t = card.museTracker;
            if (!t || t.stage !== "Reply written" || !t.turn_id) return;
            if (t.turn_id === card.notifiedMuseTurn) return;
            card.notifiedMuseTurn = t.turn_id;
            if (Qt.application.state !== Qt.ApplicationActive) host.notifyMuseReply();
        }
    }

    readonly property color steel: "#283431"
    readonly property color steelHi: "#b9d9b1"
    readonly property color steelLo: "#182420"
    readonly property color edge: "#506057"
    readonly property color lit: "#f5a16c"
    readonly property color text: "#f4eedf"
    readonly property color dim: "#aab5a5"
    readonly property color bad: "#ec866e"
    readonly property color paper: "#efe9d9"
    readonly property color ink: "#293b32"
    readonly property color paperDim: "#657064"
    readonly property string labelFont: "DejaVu Sans Mono"
    readonly property string uiFont: "DejaVu Sans"
    property string modelSearch: ""
    property var collapsedGroups: ({})
    function toggleGroup(group) { if (group === "__pinned") return; const c = Object.assign({}, collapsedGroups); c[group] = !c[group]; collapsedGroups = c }
    // Muse bridge + current model are pinned first, so they are never buried in a
    // collapsed provider group. Everything else keeps its original catalogue order
    // and group, so no provider, model or seat is removed or reordered away.
    readonly property bool isMuseBridge: modelRecord && modelRecord.family === "muse"
    function switchToMuse() {
        for (const m of (host.chatModels || [])) {
            if (m.id === "roundtable/muse/muse-bridge") { host.setClaudeModel(m.id); return true }
        }
        return false
    }
    readonly property var pickerRows: {
        const query = modelSearch.trim().toLowerCase();
        const searching = query.length > 0;
        const all = (host.chatModels || []).filter(m => {
            if (!searching) return true;
            const hay = (m.label + " " + m.id + " " + (m.group || "")).toLowerCase();
            return hay.indexOf(query) >= 0;
        });
        // Favorites first (2026-10-25): starred models, in starred order,
        // so your usual picks need no scrolling through 252 models.
        // Favorites never duplicate below: pinned and rest skip them.
        const favIds = host.favoriteModels || [];
        const favs = [];
        for (const fid of favIds) {
            for (const m of all) if (m.id === fid) { favs.push(m); break }
        }
        const seen = {};
        for (const m of favs) seen[m.id] = true;
        const pinned = [];
        for (const m of all) {
            if (m.family === "muse" || m.id === host.claudeModel) { if (!seen[m.id]) { pinned.push(m); seen[m.id] = true } }
        }
        const rest = all.filter(m => !seen[m.id]);
        const rows = [];
        if (favs.length > 0) {
            rows.push({ kind: "group", group: "__favorites", collapsed: false, pinnedLabel: "★ Favorites" });
            for (const m of favs) rows.push({ kind: "model", model: m });
        }
        if (pinned.length > 0) {
            rows.push({ kind: "group", group: "__pinned", collapsed: false, pinnedLabel: host.claudeModel ? "★ Pinned · Muse + current" : "★ Pinned · Muse" });
            for (const m of pinned) rows.push({ kind: "model", model: m });
        }
        let last = "";
        for (const m of rest) {
            const group = m.group || "Models";
            if (group !== last) {
                rows.push({ kind: "group", group: group, collapsed: !!collapsedGroups[group] && !searching });
                last = group;
            }
            if (searching || !collapsedGroups[group])
                rows.push({ kind: "model", model: m });
        }
        return rows;
    }

    width: Math.min(big ? 600 : 420, parent ? Math.max(280, parent.width - 16) : 420)
    readonly property real foldedHeight: modeArea.y + modeArea.height + phaseLine.height + strip.space + (ask ? askRow.height + 8 : 0) + inputRow.height + actionsRow.height + 80
    height: Math.min(folded ? foldedHeight : Math.max(foldedHeight + 44, big ? bigHeight : maxHeight), parent ? parent.height - 16 : 900)
    Behavior on width { NumberAnimation { duration: 120 } }
    Behavior on scale { NumberAnimation { duration: 120 } }
    visible: open
    opacity: open ? 1 : 0
    Behavior on height { NumberAnimation { duration: 120 } }

    property string sendError: ""
    property string stopNote: ""
    function send() {
        const t = field.text.trim();
        if (t === "") return;
        if (typeof host.claudeSend !== "function") {
            // @Slot regression: claudeSend is not exposed to QML, so calling it
            // would silently do nothing. Keep the text, unfold, show why.
            card.sendError = "Send broken: claudeSend not exposed (missing @Slot) - text kept, nothing sent";
            card.folded = false;
            return;
        }
        card.sendError = "";
        card.stopNote = "";
        if (host.claudeSend(t)) field.text = "";
        else card.folded = false;   // failure is now visible: status line says why, log/research overlay is shown
    }
    function focusInput() { field.forceActiveFocus() }
    function prefill(t) { field.text = t; focusInput() }   // the guide's example ideas land here, ready to send — never auto-sent
    signal pictureClicked(string path)

    // A radio transmission resolves into a little screen. Pixel fragments travel
    // from the speaker first; the surface assembles before controls appear.
    Canvas {
        id: pixelFrame
        anchors.fill: parent
        renderTarget: Canvas.Image
        onWidthChanged: requestPaint()
        onHeightChanged: requestPaint()
        onPaint: {
            const c = getContext("2d");
            c.clearRect(0, 0, width, height);
            const p = Math.max(0, Math.min(1, card.reveal));
            const bx = 22, top = 8, fullW = width - 34, fullH = height - 50;
            const bw = fullW, bh = fullH, by = top;
            const tipX = card.tailRight ? width - 8 : 8;
            const tipY = height - 4;
            function block(x, y, w, h, color) { c.fillStyle = color; c.fillRect(x, y, w, h) }
            // Boot pixels leave the exact speaker point, then hop into the screen.
            if (p < 0.90) {
                for (let i = 0; i < 12; i++) {
                    const t = (p * 1.8 - i * 0.055);
                    if (t < 0 || t > 1) continue;
                    const px = tipX + (card.tailRight ? -1 : 1) * (12 + i * 6) * t;
                    const py = tipY - (20 + i * 11) * t;
                    block(Math.round(px / 4) * 4, Math.round(py / 4) * 4,
                          i % 3 === 0 ? 8 : 4, i % 3 === 0 ? 8 : 4,
                          i % 3 === 0 ? "#f5a16c" : "#b9d9b1");
                }
            }
            // The connector grows down from the panel's bottom edge.
            if (p > 0.07) {
                const leftBase = card.tailRight ? bx + fullW - 72 : bx + 28;
                const rightBase = leftBase + 42;
                const baseY = top + fullH - 2;
                c.beginPath(); c.moveTo(leftBase, baseY);
                c.lineTo(rightBase, baseY); c.lineTo(tipX, tipY);
                c.closePath(); c.fillStyle = "#182420"; c.fill();
            }
            // The whole display is transmitted as 12px cells. A fixed per-cell
            // offset breaks the wave up without random flicker between frames.
            if (p < .84) {
                const boot = Math.max(0, (p - .12) / .72);
                const cols = Math.ceil(bw / 12), rows = Math.ceil(bh / 12);
                for (let iy = 0; iy < rows; iy++) for (let ix = 0; ix < cols; ix++) {
                    const nx = ix / Math.max(1, cols - 1);
                    const ny = iy / Math.max(1, rows - 1);
                    const jitter = ((ix * 17 + iy * 29) % 11) / 90;
                    const distance = (card.tailRight ? 1 - nx : nx) * .26 + (1 - ny) * .62;
                    if (boot < distance + jitter) continue;
                    const x = bx + ix * 12, y = by + iy * 12;
                    const justArrived = boot < distance + jitter + .13;
                    block(x, y, Math.min(12, bx + bw - x), Math.min(12, by + bh - y),
                          justArrived ? ((ix + iy) % 3 ? "#b9d9b1" : "#f5a16c") : "#202d28");
                }
                return;
            }
            // Offset shadow and stepped corners give a soft, game-like silhouette.
            function shape(x, y, w, h, fill) {
                c.beginPath(); c.moveTo(x + 12, y); c.lineTo(x + w - 16, y);
                c.lineTo(x + w - 16, y + 4); c.lineTo(x + w - 4, y + 4);
                c.lineTo(x + w - 4, y + 16); c.lineTo(x + w, y + 16);
                c.lineTo(x + w, y + h - 16); c.lineTo(x + w - 4, y + h - 16);
                c.lineTo(x + w - 4, y + h - 4); c.lineTo(x + w - 16, y + h - 4);
                c.lineTo(x + w - 16, y + h); c.lineTo(x + 12, y + h);
                c.lineTo(x + 12, y + h - 4); c.lineTo(x + 4, y + h - 4);
                c.lineTo(x + 4, y + h - 12); c.lineTo(x, y + h - 12);
                c.lineTo(x, y + 12); c.lineTo(x + 4, y + 12);
                c.lineTo(x + 4, y + 4); c.lineTo(x + 12, y + 4);
                c.closePath(); c.fillStyle = fill; c.fill();
            }
            shape(bx + 4, by + 5, bw, bh, "#111c18");
            shape(bx, by, bw, bh, "#202d28");
        }
    }
    property int dynPhase: 0
    onDynPhaseChanged: pixelFrame.requestPaint()
    onRevealChanged: pixelFrame.requestPaint()
    onGrillOffsetChanged: pixelFrame.requestPaint()
    Timer { interval: card.busy ? 170 : 900; repeat: true; running: card.open; onTriggered: card.dynPhase++ }
    property real grillOffset: height - 4
    property bool tailRight: false
    // Workshop radio: one bold call sign, with connection state kept separate.
    Item {
        id: head
        opacity: card.contentReveal
        x: 40; y: 26; width: parent.width - 80; height: Math.max(36, title.height)
        Text {
            id: title
            objectName: "claudeTitle"
            x: 0; y: 0; width: parent.width - closeKey.width - stopKey.width - 24
            text: card.modelRecord ? card.modelRecord.label : "Choose your provider"
            color: card.text; font.family: card.uiFont; font.weight: Font.Black
            font.pixelSize: 22; font.letterSpacing: -1
            elide: Text.ElideRight; wrapMode: Text.WordWrap; maximumLineCount: 2
            MouseArea {
                id: titleMouse
                anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor
                onClicked: { if (host.claudeResearchPrompt) card.folded = false; else card.folded = !card.folded }
            }
            C.ToolTip.visible: titleMouse.containsMouse
            C.ToolTip.text: card.folded ? "Show conversation" : "Fold conversation"
        }
        CardKey { id: stopKey; objectName: "claudeStopAll"; anchors.right: closeKey.left; anchors.rightMargin: 6; y: 3; text: "STOP"; tip: "Stop all modeling and chat work \u2014 runs halt at the current step; reply RESUME to continue"; danger: true; onClicked: { if (typeof host.claudeStopAll !== "function") { card.stopNote = "STOP needs a host restart to take effect"; } else if (host.claudeStopAll()) { card.stopNote = "STOPPED"; stopNoteTimer.restart(); } else { card.stopNote = ""; } } }
        Timer { id: stopNoteTimer; interval: 3000; repeat: false; onTriggered: card.stopNote = "" }
        CardKey { id: closeKey; anchors.right: parent.right; y: 3; text: "×"; tip: "Close chat"; onClicked: card.closed() }
    }

    Item {
        id: modeArea
        opacity: card.contentReveal
        x: 40; y: pick.y + pick.height + 10; width: parent.width - 80
        height: card.sendError !== "" ? sendErrorLabel.implicitHeight + 6 : 0  // BUILD/PLAN toggle + Research button removed 2026-10-25: no mode choice, no research choice
        Text {
            id: sendErrorLabel
            y: 0; width: parent.width
            visible: card.sendError !== ""; text: card.sendError
            color: card.bad; font.family: card.uiFont; font.pixelSize: 10
            wrapMode: Text.WrapAnywhere
        }
    }

    // ---- which model you are talking to: every model the chest knows (the chat's catalogue) --
    // A Claude model is the Claude Code session (tools, ALLOW/DENY, PAUSE); Grok, a local model or
    // an API model answers here too, through the chat's wiring.
    readonly property var modelRecord: {
        var ms = host.chatModels || [];
        for (var i = 0; i < ms.length; ++i) if (ms[i].id === host.claudeModel) return ms[i];
        // Fallback only: if the seat is the Muse bridge but its catalogue record
        // is temporarily missing, use the last known bridge record instead of
        // null (which would silently switch the UI to Claude: who="Claude",
        // isClaude=true, isMuseBridge=false, losing tracker/placeholder).
        if (host.claudeModel === "roundtable/muse/muse-bridge") return { id: "roundtable/muse/muse-bridge", label: "Muse · Muse (this chat) · file bridge", kind: "roundtable-cli", model: "muse-bridge", family: "muse", group: "Muse" };
        return null;
    }
    readonly property string who: modelRecord ? modelRecord.label.split(" · ")[0] : ""
    readonly property bool isClaude: !modelRecord || modelRecord.kind === "claude-cli"
    readonly property bool chatOnly: modelRecord && modelRecord.kind !== "claude-cli" && modelRecord.kind !== "grok-cli"
        && !(modelRecord.kind === "roundtable-cli" && ["codex", "opencode", "antigravity"].indexOf(modelRecord.family) >= 0)
    property Item popupParent: card
    function showPopup() { modelPopup.open() }
    // ---- model selector, directly below the head (no separate search bar above it) ----
    Item {
        id: pick
        opacity: card.contentReveal
        objectName: "claudeModelPick"
        x: 40; y: head.y + head.height + 10; width: parent.width - 80
        height: providerLabel.y + providerLabel.height + 10
        readonly property string currentText: card.modelRecord ? card.modelRecord.label : ""
        Rectangle {
            anchors.fill: parent; radius: 3
            color: pickMouse.containsMouse && !card.busy ? "#34473b" : card.steel
            border.color: pickMouse.containsMouse && !card.busy ? card.steelHi : card.edge
        }
        Row {
            x: 12; y: 9; spacing: 3; height: 10
            Repeater {
                model: 4
                Rectangle {
                    required property int index
                    width: 3; height: 4 + index * 2; anchors.bottom: parent.bottom
                    color: !card.modelRecord ? card.edge : card.busy && index > card.dynPhase % 4 ? card.edge : card.steelHi
                }
            }
        }
        Text {
            objectName: "claudeConnectionState"
            x: 38; y: 8; width: parent.width - 54
            text: card.stopNote !== "" ? card.stopNote : card.ask ? "AWAITING APPROVAL" : !card.modelRecord ? "TUNE IN / CHOOSE A PROVIDER" : card.busy ? card.status.toUpperCase() : "CONNECTED / READY"
            color: card.ask || card.busy ? card.lit : card.dim
            font.family: card.labelFont; font.pixelSize: 8; font.letterSpacing: .5
            elide: Text.ElideRight
        }
        Text {
            id: providerLabel
            objectName: "claudeProviderLabel"
            x: 12; y: 24; width: parent.width - 42
            height: implicitHeight
            text: card.modelRecord ? card.modelRecord.label : (host.catalogueBusy ? "Finding your models…" : "Who are we talking to?")
            color: card.text; font.family: card.uiFont; font.pixelSize: 12; font.weight: Font.DemiBold
            wrapMode: Text.Wrap; maximumLineCount: 2; elide: Text.ElideRight
        }
        Text { x: parent.width - 25; anchors.verticalCenter: parent.verticalCenter; text: modelPopup.visible ? "−" : "+"; color: card.steelHi; font.pixelSize: 19 }
        MouseArea {
            id: pickMouse
            anchors.fill: parent; hoverEnabled: true
            cursorShape: !card.busy && (host.chatModels || []).length > 0 ? Qt.PointingHandCursor : Qt.ArrowCursor
            onClicked: { if (card.busy || (host.chatModels || []).length === 0) return; if (modelPopup.visible) modelPopup.close(); else { card.modelSearch = ""; modelPopup.open() } }
        }
        C.ToolTip.visible: pickMouse.containsMouse
        C.ToolTip.text: (card.modelRecord ? card.modelRecord.label : "Choose a provider") + (card.busy ? "\nModel selection unlocks when this turn finishes." : "\nChoose a model or Roundtable seat")
        C.Popup {
            id: modelPopup
            objectName: "claudeModelPickPopup"
            parent: card.popupParent
            x: Math.max(0, Math.min(parent.width - width, pick.mapToItem(parent, 0, 0).x))
            y: Math.max(0, Math.min(parent.height - height, pick.mapToItem(parent, 0, pick.height).y))
            width: pick.width
            height: Math.max(80, Math.min(parent.height - 16, 380))
            padding: 10
            onOpened: { searchField.forceActiveFocus() }
            contentItem: Item {
                C.TextField {
                    id: searchField
                    objectName: "claudeModelSearch"
                    width: parent.width; height: 34
                    placeholderText: "Search models, providers or seats…"
                    color: card.text; placeholderTextColor: card.dim
                    font.family: card.uiFont; font.pixelSize: 11
                    text: card.modelSearch
                    onTextEdited: card.modelSearch = text
                    background: Rectangle { color: card.steelLo; border.color: searchField.activeFocus ? card.lit : "#b9d9b1"; border.width: 2; radius: 0 }
                }
                ListView {
                    id: modelList
                    objectName: "claudeModelResults"
                    anchors.top: searchField.bottom; anchors.topMargin: 8
                    anchors.bottom: providerFooter.top; anchors.bottomMargin: 8; width: parent.width
                    clip: true; boundsBehavior: Flickable.StopAtBounds
                    model: modelPopup.visible ? card.pickerRows : []
                    delegate: rowDelegate
                    C.ScrollBar.vertical: C.ScrollBar {}
                    Text {
                        visible: modelList.count === 0; width: parent.width
                        text: card.modelSearch.trim() !== "" ? "No matching models"
                             : host.catalogueBusy ? "Finding models…"
                             : "No providers found on this machine.\nGeppetto uses YOUR providers — if you are already signed in to one, it appears here automatically.\nOtherwise sign in, then Refresh:\nClaude — run `claude` in a terminal and log in\nGrok — run `grok` and log in\nOllama — start Ollama\nOr add your own in ~/.config/geppetto/chat-models.json"
                        color: card.dim
                        font.family: card.uiFont; font.pixelSize: 11
                        wrapMode: Text.Wrap; topPadding: 20
                    }
                }
                Item {
                    id: providerFooter
                    readonly property var account: typeof host.providerAccountInfo === "function" ? host.providerAccountInfo(host.claudeModel) : ({supported: false, message: "Your logins, on this machine — nothing shared"})
                    readonly property bool signingIn: host.providerAuthBusy === true
                    anchors.bottom: parent.bottom; width: parent.width
                    height: accountActions.height + accountNote.height + 8
                    Flow {
                        id: accountActions
                        width: parent.width; spacing: 6
                        CardKey {
                            id: providerRefresh
                            text: "Refresh providers"; onClicked: host.refreshChat()
                            enabled: !providerFooter.signingIn && !host.catalogueBusy
                            tip: "Re-discover providers on this machine after you sign in"
                        }
                        CardKey {
                            objectName: "providerSignInAgain"
                            text: providerFooter.signingIn ? "Signing in…" : "Sign in again"
                            enabled: providerFooter.account.supported && !providerFooter.signingIn && !card.busy
                            onClicked: host.signInProvider(host.claudeModel)
                            tip: "Switch the selected provider's shared CLI account. Opens a terminal and asks before signing out."
                        }
                    }
                    Text {
                        id: accountNote
                        objectName: "providerAccountNote"
                        y: accountActions.height + 8; width: parent.width
                        text: (host.providerAuthMessage || "") !== "" ? host.providerAuthMessage : providerFooter.account.message
                        color: card.dim; font.family: card.uiFont; font.pixelSize: 10
                        wrapMode: Text.Wrap; maximumLineCount: 2; elide: Text.ElideRight
                        C.ToolTip.visible: accountHover.containsMouse
                        C.ToolTip.text: text
                        MouseArea { id: accountHover; anchors.fill: parent; hoverEnabled: true; acceptedButtons: Qt.NoButton }
                    }
                }
            }
            background: Rectangle { color: card.steel; border.color: "#b9d9b1"; border.width: 2; radius: 0 }
        }
        Component {
            id: rowDelegate
            C.ItemDelegate {
            required property var modelData
            required property int index
            readonly property bool header: modelData.kind === "group"
            readonly property var model: header ? null : modelData.model
            width: modelList.width
            height: header ? 28 : Math.max(38, modelRowText.implicitHeight + 12)
            enabled: header || model.available !== false
            highlighted: !header && (hovered || model.id === host.claudeModel)
            onClicked: {
                if (header) { card.toggleGroup(modelData.group); return }
                host.setClaudeModel(model.id); modelPopup.close()
            }
            contentItem: Text {
                id: modelRowText
                width: parent.width
                leftPadding: header ? 2 : 14
                rightPadding: header ? 2 : 34
                text: header
                    ? (modelData.pinnedLabel ? modelData.pinnedLabel
                       : ((modelData.collapsed ? "▸  " : "▾  ") + modelData.group))
                    : model.label
                color: header ? card.lit : (model.available === false ? card.dim : parent.highlighted ? card.steelLo : card.text)
                font.family: card.uiFont
                font.pixelSize: header ? 10 : 12
                font.bold: header || model.id === host.claudeModel
                wrapMode: Text.Wrap; maximumLineCount: 2
                font.capitalization: header ? Font.AllUppercase : Font.MixedCase
                verticalAlignment: Text.AlignVCenter
                elide: Text.ElideRight
            }
            // Star, right of every model row: tap it to favorite / unfavorite.
            // It does not select the model — the row itself still does that.
            Item {
                visible: !header
                anchors.right: parent.right; anchors.rightMargin: 4; anchors.verticalCenter: parent.verticalCenter
                width: 28; height: 28; z: 2
                Text {
                    anchors.centerIn: parent
                    text: !header && (host.favoriteModels || []).indexOf(model.id) >= 0 ? "★" : "☆"
                    color: !header && (host.favoriteModels || []).indexOf(model.id) >= 0 ? card.lit : card.dim
                    font.pixelSize: 16
                }
                MouseArea {
                    anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor
                    onClicked: host.toggleFavorite(model.id)
                }
            }
            C.ToolTip.visible: hovered && !header
            C.ToolTip.text: header ? "" : model.label + "\n" + model.id
            background: Rectangle {
                radius: 0
                color: header ? "transparent" : (parent.highlighted ? card.steelHi : "transparent")
            }
            }
        }
    }

    Text {
        id: phaseLine
        opacity: card.contentReveal
        x: 40; y: modeArea.y + modeArea.height + 8; width: parent.width - 80
        height: visible ? implicitHeight + 16 : 0
        visible: card.busy && (card.phase !== "" || card.isMuseBridge)
        text: card.isMuseBridge ? ("Muse · " + (card.museTracker && card.museTracker.stage ? card.museTracker.stage : "Thinking") + (card.museTracker && card.museTracker.elapsed_text ? " · " + card.museTracker.elapsed_text : "") + (card.museTracker && card.museTracker.detail ? "\n" + card.museTracker.detail : "") + (card.museTracker && card.museTracker.live_op && card.museTracker.live_op !== card.museTracker.detail ? "\n" + card.museTracker.live_op : "")) : (card.phase === "researching" ? "Researching · gathering references" : "Modeling · research complete")
        color: card.lit; font.family: card.uiFont; font.pixelSize: 11
        leftPadding: 10; rightPadding: 10; topPadding: 8
        wrapMode: Text.Wrap
        Rectangle { anchors.fill: parent; z: -1; color: card.steel; radius: 0; border.color: "#b9d9b1"; border.width: 2 }
    }

    // ---- pictures it looked at or showed you, newest right; a click pops one large again ----
    ListView {
        id: strip
        opacity: card.contentReveal
        objectName: "claudeStrip"
        readonly property real space: count > 0 ? height + 4 : 0
        x: 40; y: phaseLine.y + phaseLine.height + 4
        width: parent.width - 80; height: count > 0 ? 40 : 0
        visible: count > 0
        orientation: ListView.Horizontal; spacing: 3; clip: true
        model: host.claudePictures
        onCountChanged: positionViewAtEnd()
        delegate: Rectangle {
            required property string modelData
            width: 52; height: 40; color: card.steel; border.color: card.steelHi; border.width: 2
            Image {
                anchors.fill: parent; anchors.margins: 1
                source: "file://" + parent.modelData; fillMode: Image.PreserveAspectCrop
                sourceSize.width: 104; asynchronous: true; cache: false
            }
            MouseArea { anchors.fill: parent; cursorShape: Qt.PointingHandCursor; onClicked: card.pictureClicked(parent.modelData) }
        }
    }

    // The transcript is a dispatch roll: quiet paper, tiny role stamps, torn edge.
    Rectangle {
        id: transcript
        objectName: "claudeTranscript"
        opacity: card.contentReveal
        visible: !card.folded
        x: 36; y: strip.y + strip.space + 4; width: parent.width - 72
        height: Math.max(0, (card.ask ? askRow.y : inputRow.y) - y - 12)
        color: card.paper
        Rectangle { x: 0; y: 0; width: 3; height: parent.height; color: "#d3d8bf" }
        Item {
            id: transcriptHead
            x: 14; y: 11; width: parent.width - 28; height: 18
            Text {
                width: parent.width - dispatchNumber.width - 8
                text: "CONVERSATION"; font.family: card.labelFont; font.pixelSize: 8
                font.letterSpacing: 1.1; color: card.paperDim; elide: Text.ElideRight
            }
            Text {
                id: dispatchNumber; anchors.right: parent.right
                text: ("00" + log.count).slice(-3)
                color: card.paperDim; font.family: card.labelFont; font.pixelSize: 8
            }
            Rectangle { anchors.bottom: parent.bottom; width: parent.width; height: 1; color: "#d2d4c0" }
        }
        Canvas {
            anchors.top: parent.bottom; width: parent.width; height: 5
            onWidthChanged: requestPaint()
            onPaint: {
                const c = getContext("2d"); c.clearRect(0, 0, width, height);
                c.beginPath(); c.moveTo(0, 0);
                for (let x = 0; x < width; x += 10) { c.lineTo(x + 5, 4); c.lineTo(x + 10, 0) }
                c.lineTo(width, 0); c.closePath(); c.fillStyle = card.paper; c.fill();
            }
        }
        Flickable {
            visible: log.count === 0 && !host.claudeResearchPrompt
            x: 20; y: 42; width: parent.width - 40; height: Math.max(0, parent.height - 54)
            contentHeight: emptyColumn.height; clip: true; boundsBehavior: Flickable.StopAtBounds
            Column {
                id: emptyColumn
                width: parent.width; spacing: 10
                Text { text: "✳"; color: "#b75b38"; font.pixelSize: 34 }
                Text {
                    width: parent.width; text: "Welcome to Geppetto."
                    font.family: card.uiFont; font.weight: Font.Black; font.pixelSize: 24
                    font.letterSpacing: -.8; color: card.ink; wrapMode: Text.Wrap
                }
                Text {
                    width: parent.width; text: "Talk here, and build with your own models —\nwhatever comes to mind."
                    color: card.paperDim; font.family: card.uiFont; font.pixelSize: 11; wrapMode: Text.Wrap
                }
            }
        }
    }

    // ---- the conversation ------------------------------------------------------------------
    // Research consent overlay removed 2026-10-25 at user order: no research as a selectable option.
    ListView {
        id: log
        opacity: card.contentReveal
        objectName: "claudeLog"
        visible: !card.folded && !host.claudeResearchPrompt
        x: transcript.x + 14; y: transcript.y + 40
        width: transcript.width - 28
        height: Math.max(0, transcript.height - 54)
        clip: true; spacing: 16
        boundsBehavior: Flickable.StopAtBounds
        model: card.messages
        // follow the newest line while it streams in; scroll up to read back and it stops following
        property bool follow: true
        onMovementEnded: follow = atYEnd
        function followEnd() { if (follow && !moving) positionViewAtEnd() }
        onCountChanged: Qt.callLater(followEnd)
        onContentHeightChanged: Qt.callLater(followEnd)
        Connections {
            target: card.messages
            function onUpdated() { Qt.callLater(log.followEnd) }
        }
        onHeightChanged: if (follow) positionViewAtEnd()
        C.ScrollBar.vertical: C.ScrollBar {
            policy: C.ScrollBar.AsNeeded; width: 4
            contentItem: Rectangle { implicitWidth: 4; radius: 2; color: "#7d9180"; opacity: parent.active ? 1 : .45 }
        }
        delegate: Item {
            id: row
            required property var modelData
            required property int index
            readonly property string role: modelData.role
            readonly property bool open: card.expanded[index] === true
            width: log.width - 8
            height: col.implicitHeight
            readonly property bool mainMessage: role === "you" || role === "claude"
            Rectangle {     // subagent work hangs off a rail
                visible: !!row.modelData.sub; x: 1; width: 2; height: parent.height; color: card.steelHi
            }
            Column {
                id: col
                x: row.modelData.sub ? 8 : 0; width: parent.width - x; spacing: row.mainMessage ? 6 : 3
                Item {
                    visible: row.mainMessage; width: parent.width; height: visible ? 16 : 0
                    Rectangle {
                        width: Math.min(roleLabel.implicitWidth + 10, col.width - 24); height: 16
                        color: row.role === "you" ? "#d8dfc9" : "#dfc7ad"
                        Text {
                            id: roleLabel; x: 5; anchors.verticalCenter: parent.verticalCenter
                            text: row.role === "you" ? "YOU" : (card.who || "ASSISTANT").toUpperCase()
                            width: Math.min(implicitWidth, col.width - 34); elide: Text.ElideRight
                            color: card.ink; font.family: card.labelFont; font.pixelSize: 8; font.bold: true
                            MouseArea { id: speakerMouse; anchors.fill: parent; hoverEnabled: true; acceptedButtons: Qt.NoButton }
                            C.ToolTip.visible: speakerMouse.containsMouse && truncated
                            C.ToolTip.text: row.role === "you" ? "You" : (card.modelRecord ? card.modelRecord.label : "Assistant")
                        }
                    }
                    Text {
                        anchors.right: parent.right; anchors.verticalCenter: parent.verticalCenter
                        text: row.modelData.live ? "LIVE" : ""; color: "#a25132"
                        font.family: card.labelFont; font.pixelSize: 8
                    }
                }
                Text {
                    width: parent.width
                    objectName: "claudeMessageText"
                    wrapMode: Text.Wrap
                    visible: row.role !== "out"
                    textFormat: row.role === "claude" && !row.modelData.live ? Text.MarkdownText : Text.PlainText
                    text: row.role === "you" ? row.modelData.text
                        : row.role === "tool" ? (row.modelData.done ? (row.modelData.error ? "✗ " : "✓ ") : card.spin + " ") + row.modelData.text
                        : row.role === "ask" ? "? " + row.modelData.text
                        : row.role === "think" ? row.modelData.text + (row.modelData.live ? " " + card.spin : "")
                        : row.modelData.text + (row.modelData.live ? "▍" : "")
                    color: row.mainMessage ? card.ink
                         : row.role === "ask" ? "#a25132" : row.role === "tool" ? (row.modelData.error ? "#ad392c" : "#41624c")
                         : row.role === "note" && /^(refused|denied|stopped|claude exited)/.test(row.modelData.text) ? "#ad392c" : card.paperDim
                    font.family: card.uiFont; font.italic: row.role === "think"
                    font.bold: row.role === "tool"
                    lineHeight: 1.2
                    font.pixelSize: row.mainMessage ? 13 : 11
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
                    color: card.paperDim; font.family: card.labelFont; font.pixelSize: 10
                }
                Rectangle {     // what came back: click to see all of it
                    visible: row.role === "out"
                    x: 12; width: parent.width - 12
                    height: outText.implicitHeight + 6
                    color: "#e3e3d1"; radius: 2
                    border.color: row.modelData.error ? "#ad392c" : "#ccd0ba"; border.width: 1
                    Text {
                        id: outText
                        x: 4; y: 3; width: parent.width - 8
                        wrapMode: Text.WrapAnywhere
                        text: row.open && row.modelData.full ? row.modelData.full : row.modelData.text
                        color: row.modelData.error ? "#ad392c" : card.ink
                        font.family: card.labelFont; font.pixelSize: 10
                    }
                    MouseArea { anchors.fill: parent; cursorShape: Qt.PointingHandCursor; onClicked: card.toggle(row.index) }
                }
            }
        }
    }

    // ---- a tool waiting on your OK -----------------------------------------------------------
    Rectangle {
        id: askRow
        opacity: card.contentReveal
        visible: card.ask !== ""
        x: 36; width: parent.width - 72; height: askText.implicitHeight + 42
        anchors.bottom: inputRow.top; anchors.bottomMargin: 4
        color: card.steel; border.color: card.lit; border.width: 2; radius: 0
        Text {
            id: askText
            x: 5; y: 4; width: parent.width - 10; wrapMode: Text.Wrap; maximumLineCount: 4; elide: Text.ElideRight
            text: "wants to run: " + card.ask
            color: card.lit; font.family: card.uiFont; font.pixelSize: 10
        }
        Row {
            anchors.right: parent.right; anchors.bottom: parent.bottom; anchors.margins: 4; spacing: 3
            CardKey { objectName: "claudeAllow"; text: "ALLOW"; hot: true; onClicked: host.claudeAnswer(true) }
            CardKey { objectName: "claudeDeny"; text: "DENY"; onClicked: host.claudeAnswer(false) }
        }
    }

    // Composer: an orange transmit key, with a quiet keyboard hint below.
    Rectangle {
        id: inputRow
        objectName: "claudeComposer"
        opacity: card.contentReveal
        x: 36; width: parent.width - 72
        height: Math.min(112, Math.max(62, field.contentHeight + 26))
        anchors.bottom: actionsRow.top; anchors.bottomMargin: 6
        radius: 3; color: card.steelLo
        border.color: field.activeFocus ? card.lit : card.edge; border.width: 1
        Rectangle { x: 0; y: 9; width: 3; height: parent.height - 18; color: field.activeFocus ? card.lit : card.edge }
        Flickable {
            id: fieldFlick
            anchors.fill: parent; anchors.leftMargin: 14; anchors.rightMargin: sendKey.width + 22
            anchors.topMargin: 13; anchors.bottomMargin: 12
            contentHeight: field.contentHeight; clip: true
            boundsBehavior: Flickable.StopAtBounds
            TextEdit {
                id: field
                objectName: "claudeInput"
                width: fieldFlick.width
                wrapMode: TextEdit.Wrap
                color: card.text; selectionColor: card.lit; selectedTextColor: card.steelLo
                font.family: card.uiFont; font.pixelSize: 13
                selectByMouse: true
                Keys.onReturnPressed: event => { if (event.modifiers & Qt.ShiftModifier) event.accepted = false; else card.send() }
                Keys.onEnterPressed: event => { if (event.modifiers & Qt.ShiftModifier) event.accepted = false; else card.send() }
                Keys.onEscapePressed: field.focus = false
                onCursorRectangleChanged: {
                    if (cursorRectangle.y < fieldFlick.contentY) fieldFlick.contentY = cursorRectangle.y;
                    else if (cursorRectangle.y + cursorRectangle.height > fieldFlick.contentY + fieldFlick.height)
                        fieldFlick.contentY = cursorRectangle.y + cursorRectangle.height - fieldFlick.height;
                }
            }
            Text {
                visible: field.text === ""
                text: !card.modelRecord ? "Tune in above, then say hello…"
                     : card.busy ? "Your next thought…" : "What are we making?"
                width: parent.width; wrapMode: Text.Wrap; maximumLineCount: 2; elide: Text.ElideRight
                color: card.dim; font.family: card.uiFont; font.pixelSize: 12
            }
        }
        Item {
            id: sendKey
            objectName: "claudeSendKey"
            width: 42; height: 40
            anchors.right: parent.right; anchors.rightMargin: 10; anchors.verticalCenter: parent.verticalCenter
            enabled: field.text.trim() !== "" && !card.busy && card.modelRecord !== null
            Rectangle { anchors.fill: parent; radius: 2; color: sendMouse.pressed ? card.paper : sendMouse.containsMouse && sendKey.enabled ? "#ffb983" : card.lit; opacity: sendKey.enabled ? 1 : .45 }
            Text { anchors.centerIn: parent; text: "↗"; color: card.steelLo; font.pixelSize: 26 }
            MouseArea { id: sendMouse; anchors.fill: parent; hoverEnabled: true; enabled: sendKey.enabled; cursorShape: Qt.PointingHandCursor; onClicked: card.send() }
            C.ToolTip.visible: sendMouse.containsMouse
            C.ToolTip.text: "Send message · Enter"
        }
    }

    Item {
        id: actionsRow
        objectName: "claudeActions"
        opacity: card.contentReveal
        x: 40; width: parent.width - 80; height: 26
        anchors.bottom: parent.bottom; anchors.bottomMargin: 50
        Text {
            id: newChatLabel
            text: "+ NEW"; color: newChatMouse.containsMouse ? card.lit : card.dim
            font.family: card.labelFont; font.pixelSize: 9; height: parent.height
            verticalAlignment: Text.AlignVCenter; opacity: card.busy ? .4 : 1
            MouseArea {
                id: newChatMouse; anchors.fill: parent; enabled: !card.busy
                hoverEnabled: true; cursorShape: Qt.PointingHandCursor
                onClicked: { card.expanded = {}; host.claudeNew() }
            }
            C.ToolTip.visible: newChatMouse.containsMouse
            C.ToolTip.text: "Start a new conversation"
        }
        Text {
            anchors.left: newChatLabel.right; anchors.leftMargin: 14
            anchors.right: expandLabel.left; anchors.rightMargin: 12
            height: parent.height; verticalAlignment: Text.AlignVCenter
            text: "ENTER TO SEND"; visible: width > implicitWidth
            color: "#809383"; font.family: card.labelFont; font.pixelSize: 8
        }
        Text {
            id: expandLabel
            objectName: "claudeBig"; anchors.right: parent.right
            text: card.big ? "↙ COMPACT" : "↗ EXPAND"
            color: expandMouse.containsMouse ? card.lit : card.dim
            font.family: card.labelFont; font.pixelSize: 9
            height: parent.height; verticalAlignment: Text.AlignVCenter
            MouseArea { id: expandMouse; anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor; onClicked: card.big = !card.big }
        }
    }

    component CardKey: Rectangle {
        id: ck
        property string text: ""
        property string tip: ""
        property bool hot: false
        property bool danger: false
        signal clicked()
        width: Math.max(32, t.implicitWidth + 20); height: Math.max(32, t.implicitHeight + 14); radius: 3
        opacity: enabled ? 1 : 0.4
        color: ckm.pressed ? card.lit : ckm.containsMouse ? "#415847" : (danger && enabled ? card.bad : hot && enabled ? card.steelHi : card.steel)
        border.color: ckm.containsMouse ? card.steelHi : card.edge
        border.width: 1
        Text {
            id: t; anchors.centerIn: parent; text: ck.text
            color: ck.hot || ckm.pressed ? card.steelLo : card.text
            font.family: card.labelFont; font.pixelSize: 10; font.weight: Font.DemiBold
            font.capitalization: Font.AllUppercase
        }
        MouseArea { id: ckm; anchors.fill: parent; hoverEnabled: true; enabled: ck.enabled; cursorShape: Qt.PointingHandCursor; onClicked: ck.clicked() }
        C.ToolTip.visible: ck.tip !== "" && ckm.containsMouse
        C.ToolTip.delay: 500
        C.ToolTip.text: ck.tip
    }
    component ResearchChoice: C.CheckBox {
        id: choice
        width: consentBody.width
        contentItem: Text {
            text: choice.text
            leftPadding: (choice.indicator ? choice.indicator.width : 24) + choice.spacing + 4
            color: card.text; font.family: card.uiFont; font.pixelSize: 12
            verticalAlignment: Text.AlignVCenter; wrapMode: Text.Wrap
        }
    }
}
