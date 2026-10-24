// SPRITE PLAYER (2D animation suite, lane 2) — player UI ONLY, standalone.
// It is not wired into cabinet.qml yet (deferred wiring, see the note at
// the bottom). A future host patch sets sheetSource + frames + tags from
// a SpriteAnim sheet; until then it runs on whatever frames it is given,
// and shows an empty state when it has none.
// Frames are plain JS objects shaped like SpriteAnim's SpriteFrame:
//   { name, x, y, w, h, durationMs, sourceW, sourceH, offsetX, offsetY }
// durationMs <= 0 means 1000/fps. The sequence math mirrors
// SpriteAnimation in C#: ping-pong never doubles its endpoints
// (0,1,2,1,0...), reverse plays n-1..0, once stops on the last frame.
import QtQuick
import QtQuick.Controls as C

Item {
    id: player
    property string sheetSource: ""
    property var frames: []
    property var tags: []          // { name, from, to, direction, repeat }
    property int fps: 12
    property string loopMode: "loop"   // loop | pingpong | reverse | once
    property string background: "checker" // checker | black | white
    property real zoom: 2
    property bool playing: false
    property int currentIndex: frames.length > 0 ? sequence[seqPos] : -1
    property var currentFrame: currentIndex >= 0 ? frames[currentIndex] : null
    property string status: frames.length === 0 ? "no sheet loaded"
        : (currentFrame ? currentFrame.name + "  " + (currentIndex + 1) + "/" + frames.length : "")

    property int seqPos: 0
    property var sequence: buildSequence()
    property int tagFrom: -1
    property int tagTo: -1
    property int tagRepeat: 0
    property int cyclesDone: 0

    function frameMs(f) { return (f && f.durationMs > 0) ? f.durationMs : (fps > 0 ? 1000 / fps : 100) }
    function buildSequence() {
        var list = [];
        var from = tagFrom >= 0 ? tagFrom : 0;
        var to = tagTo >= 0 ? tagTo : frames.length - 1;
        var mode = loopMode;
        if (tagFrom >= 0) {
            var t = tags.find(function (tg) { return tg.from === tagFrom && tg.to === tagTo });
            if (t) mode = t.direction === "reverse" ? "reverse" : (t.direction === "pingpong" ? "pingpong" : "loop");
        }
        if (to < from || frames.length === 0) return list;
        if (mode === "reverse") { for (var i = to; i >= from; i--) list.push(i); return list; }
        for (var j = from; j <= to; j++) list.push(j);
        if (mode === "pingpong" && to - from >= 1) { for (var k = to - 1; k > from; k--) list.push(k); }
        return list;
    }
    function rebuild() { sequence = buildSequence(); seqPos = 0; cyclesDone = 0; }
    onFramesChanged: rebuild()
    onLoopModeChanged: rebuild()
    onTagsChanged: rebuild()

    function applyTag(name) {
        var t = tags.find(function (tg) { return tg.name === name });
        if (!t) { tagFrom = -1; tagTo = -1; tagRepeat = 0; rebuild(); return; }
        tagFrom = t.from; tagTo = t.to; tagRepeat = t.repeat || 0; rebuild();
    }
    function step(dir) {
        if (sequence.length === 0) return;
        var next = seqPos + dir;
        if (next < 0) next = loopMode === "once" && tagFrom < 0 ? 0 : sequence.length - 1;
        if (next >= sequence.length) {
            cyclesDone++;
            if (loopMode === "once" || (tagRepeat > 0 && cyclesDone >= tagRepeat)) { playing = false; seqPos = sequence.length - 1; return; }
            next = 0;
        }
        seqPos = next;
    }

    Timer {
        id: clock
        running: player.playing && player.sequence.length > 0
        interval: player.currentFrame ? player.frameMs(player.currentFrame) : 100
        repeat: true
        onTriggered: player.step(1)
    }

    readonly property color steel: "#2b2f36"
    readonly property color steelHi: "#3d424b"
    readonly property color steelLo: "#1f2228"
    readonly property color edge: "#0b0c0e"
    readonly property color lit: "#f2a91c"
    readonly property color text: "#c9ccd0"
    readonly property color dim: "#7a7f88"
    readonly property string mono: "DejaVu Sans Mono"

    width: 360
    height: stage.height + controls.height + strip.height + 12
    visible: true

    Rectangle { anchors.fill: parent; color: steelLo; border.color: edge; border.width: 1; radius: 4 }

    Column {
        id: body
        width: parent.width - 16; x: 8; y: 8; spacing: 8

        Text { text: "SPRITE PLAYER"; color: lit; font.family: mono; font.pixelSize: 14; font.bold: true }

        // ---- stage: current frame on a switchable background ----
        Item {
            id: stage
            width: parent.width
            height: Math.max(120, (player.currentFrame ? player.currentFrame.sourceH : 64) * player.zoom + 16)
            Rectangle { anchors.fill: parent; color: player.background === "black" ? "#000000" : (player.background === "white" ? "#ffffff" : "#20242a"); border.color: edge }
            Canvas {
                id: checker
                anchors.fill: parent; visible: player.background === "checker"
                onPaint: {
                    var ctx = getContext("2d"); ctx.clearRect(0, 0, width, height);
                    var s = 16;
                    for (var y = 0; y < height / s; y++) for (var x = 0; x < width / s; x++) {
                        ctx.fillStyle = (x + y) % 2 === 0 ? "#3a3f47" : "#24282e";
                        ctx.fillRect(x * s, y * s, s, s);
                    }
                }
                onVisibleChanged: if (visible) requestPaint()
            }
            // The frame sits at its trim offset inside its untrimmed box,
            // so trimmed frames do not jitter between frames.
            Item {
                anchors.centerIn: parent
                width: (player.currentFrame ? player.currentFrame.sourceW : 0) * player.zoom
                height: (player.currentFrame ? player.currentFrame.sourceH : 0) * player.zoom
                visible: player.currentFrame !== null && player.sheetSource !== ""
                Image {
                    x: (player.currentFrame ? player.currentFrame.offsetX : 0) * player.zoom
                    y: (player.currentFrame ? player.currentFrame.offsetY : 0) * player.zoom
                    width: (player.currentFrame ? player.currentFrame.w : 0) * player.zoom
                    height: (player.currentFrame ? player.currentFrame.h : 0) * player.zoom
                    source: player.sheetSource
                    sourceClipRect: player.currentFrame ? Qt.rect(player.currentFrame.x, player.currentFrame.y, player.currentFrame.w, player.currentFrame.h) : Qt.rect(0, 0, 0, 0)
                    fillMode: Image.Stretch; smooth: false; asynchronous: true; cache: false
                }
            }
            Text {
                anchors.centerIn: parent; visible: player.frames.length === 0
                text: "no sheet loaded"; color: dim; font.family: mono; font.pixelSize: 13
            }
        }

        // ---- transport ----
        Row {
            id: controls
            width: parent.width; spacing: 6
            C.Button { text: "⏮"; onClicked: { player.seqPos = 0; player.cyclesDone = 0; } }
            C.Button { text: "◀"; onClicked: { player.playing = false; player.step(-1); } }
            C.Button { text: player.playing ? "⏸" : "▶"; highlighted: player.playing; onClicked: player.playing = !player.playing && player.sequence.length > 0 }
            C.Button { text: "▶"; onClicked: { player.playing = false; player.step(1); } }
            C.Button { text: "⏭"; onClicked: { player.seqPos = player.sequence.length - 1; } }
            Text { text: player.status; color: text; font.family: mono; font.pixelSize: 12; anchors.verticalCenter: parent.verticalCenter; elide: Text.ElideRight; width: 120 }
        }

        // ---- fps + mode + background + tag ----
        Row {
            width: parent.width; spacing: 6
            Text { text: "fps"; color: dim; font.family: mono; font.pixelSize: 12; anchors.verticalCenter: parent.verticalCenter }
            C.SpinBox { from: 1; to: 60; value: player.fps; width: 90; onValueChanged: player.fps = value }
            C.ComboBox {
                width: 110; model: ["loop", "pingpong", "reverse", "once"]
                currentIndex: ["loop", "pingpong", "reverse", "once"].indexOf(player.loopMode)
                onActivated: i => { player.tagFrom = -1; player.tagTo = -1; player.loopMode = ["loop", "pingpong", "reverse", "once"][i]; player.rebuild(); }
            }
            C.ComboBox {
                width: 110; model: ["checker", "black", "white"]
                currentIndex: ["checker", "black", "white"].indexOf(player.background)
                onActivated: i => { player.background = ["checker", "black", "white"][i]; }
            }
        }
        Row {
            width: parent.width; spacing: 6; visible: player.tags.length > 0
            Text { text: "tag"; color: dim; font.family: mono; font.pixelSize: 12; anchors.verticalCenter: parent.verticalCenter }
            C.ComboBox {
                width: 200; model: ["(all frames)"].concat(player.tags.map(function (t) { return t.name; }))
                onActivated: i => { if (i === 0) { player.tagFrom = -1; player.tagTo = -1; player.rebuild(); } else player.applyTag(player.tags[i - 1].name); }
            }
        }

        // ---- frame strip: one cropped thumbnail per frame, current outlined ----
        Row {
            id: strip
            width: parent.width; spacing: 4
            Repeater {
                model: player.frames
                Rectangle {
                    required property var modelData
                    required property int index
                    width: 40; height: 40; color: steel; border.width: player.currentIndex === index ? 3 : 1; border.color: player.currentIndex === index ? lit : edge
                    Image {
                        anchors.centerIn: parent; width: Math.min(36, modelData.w); height: Math.min(36, modelData.h)
                        source: player.sheetSource; sourceClipRect: Qt.rect(modelData.x, modelData.y, modelData.w, modelData.h)
                        fillMode: Image.PreserveAspectFit; smooth: false; asynchronous: true; cache: false
                    }
                    MouseArea { anchors.fill: parent; onClicked: { player.playing = false; var p = player.sequence.indexOf(index); player.seqPos = p >= 0 ? p : 0; if (p < 0) player.seqPos = 0; } }
                }
            }
        }
    }
}

// DEFERRED WIRING (do not hand-edit cabinet.qml mid-race; report this patch):
//   In cabinet.qml, instantiate SpritePlayer where a 2D pane belongs and
//   feed it from a SpriteAnim sheet (host supplies sheetSource = the PNG
//   path, frames = sheet frames as {name,x,y,w,h,durationMs,sourceW,
//   sourceH,offsetX,offsetY}, tags = sheet tags). No host.py run-control
//   changes, no notice-region changes. Until that patch lands this file
//   is a standalone player component, not a live cabinet pane.
