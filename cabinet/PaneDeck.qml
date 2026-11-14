// PaneDeck.qml — LV1 (grok-bot, 2026-11-13): the pace deck that slides up out of a pane while
// a model builds in it. One per pane (pane 1 and the side panes 2-4), each driving only its own
// pane's run through the host's existing run protocol (pauseRun/resumeRun/stepRun/stopResearch/
// setPace -> .live/commands), so a change lands on the running build at its next step.
//
// It is a physical object, baked in bake_deck.py from the chest's own parts (brushed aluminium
// plate on screws, bakelite keys, jewel lamp, recessed fader with a ribbed cap); state is shown
// by light and motion, never by a flat panel:
//   lamp   steady amber = building, blinking = held (paused), dark = ended; it blooms when a
//          step lands
//   fader  slow on the left, fast on the right; the cap glides to the run's reported pace and
//          follows the hand 1:1 while dragged; wheel nudges it one detent
//   keys   II / > pause-resume, >| one step while held, a red-lit square to stop
// It slides up (OutBack, 300 ms) when the pane's run starts and back down (InCubic, 220 ms)
// a moment after it ends. Ctrl+wheel over it, or Ctrl+= / Ctrl+- / Ctrl+0 in the chest, zoom
// it; it never grows past its pane, so nothing clips at any zoom.
import QtQuick

Item {
    id: deck
    // ---- inputs
    property int n: 1
    property var run: null                    // host.runs entry for this pane's run, or null
    property int revision: 0                  // the pane's shown revision: a change blooms the lamp
    property bool replaying: false
    property var controller: null             // the host (pauseRun/resumeRun/stepRun/stopResearch/setPace)
    property url art: Qt.resolvedUrl("art/")
    property real zoom: 1.0                   // asked-for zoom; the fit below caps it to the pane
    property real paneWidth: 1024
    property real paneHeight: 576
    property string mono: "DejaVu Sans Mono"
    property color amber: "#f2a91c"
    property bool blinkOn: true
    signal zoomStep(int dir)                  // Ctrl+wheel over the deck: -1 out, +1 in
    signal tip(string text)                   // what the key under the mouse does, for the chest's hint line

    // ---- state
    readonly property bool live: !!run && run.status === "running"
    readonly property bool paused: live && !!run.paused
    readonly property string dir: run && run.dir ? run.dir : ""
    readonly property bool openRun: live && !!run.openState
    // a run that just ended keeps its deck up a moment (DONE, lamp dark) before it slides away
    property bool lingering: false
    onLiveChanged: { if (!live && dir !== "") { lingering = true; linger.restart() } else if (live) { lingering = false; linger.stop() } }
    Timer { id: linger; interval: 2600; onTriggered: deck.lingering = false }
    readonly property bool shown: !replaying && dir !== "" && (live || lingering)

    // ---- geometry: baked at 1x = 364 x 76 (plate + shadow); scaled as a whole
    readonly property real baseW: 364
    readonly property real baseH: 76
    property real fit: Math.max(0.5, Math.min(zoom, (paneWidth - 16) / baseW, (paneHeight * 0.45) / baseH))
    width: baseW * fit
    height: baseH * fit
    // slide: 0 = tucked under the pane's bottom edge, 1 = up in place
    property real out: shown ? 1 : 0
    Behavior on out { NumberAnimation { duration: deck.shown ? 300 : 220; easing.type: deck.shown ? Easing.OutBack : Easing.InCubic; easing.overshoot: 1.2 } }
    visible: out > 0.001
    opacity: Math.min(1, out * 1.6)
    Behavior on fit { NumberAnimation { duration: 180; easing.type: Easing.OutCubic } }

    // ---- pace: a perceptual fader. p in [0,1], left slow (30 s) .. right fast (0 s).
    // seconds = 30 * (1-p)^2.6: 3 s sits at ~0.59, 1 s at ~0.73, so the useful 0.5-6 s range
    // gets most of the slot instead of the first sixth of a linear 0-30 s bar.
    readonly property real k: 2.6
    function paceAt(p) { var s = 30 * Math.pow(Math.max(0, Math.min(1, 1 - p)), k); return s < 0.05 ? 0 : (s < 10 ? Math.round(s * 10) / 10 : Math.round(s)) }
    function posOf(s) { return 1 - Math.pow(Math.max(0, Math.min(30, s)) / 30, 1 / k) }
    readonly property real reportedPace: live && Number.isFinite(run.pace) ? Math.max(0, Math.min(30, run.pace)) : 0
    property real shownPace: reportedPace
    property string pendingDir: ""
    onReportedPaceChanged: if (!capMouse.pressed && !pendingDir) shownPace = reportedPace
    onDirChanged: { flush.stop(); pendingDir = ""; shownPace = reportedPace }
    readonly property string paceText: shownPace < 0.05 ? "FULL" : shownPace < 10 ? shownPace.toFixed(1) + "S" : Math.round(shownPace) + "S"
    function submit() { flush.stop(); var d = pendingDir; pendingDir = ""; if (d && d === dir && controller) controller.setPace(d, shownPace) }
    function setFromPos(p) { shownPace = paceAt(p); pendingDir = dir; flush.restart() }
    Timer { id: flush; interval: 120; onTriggered: deck.submit() }
    // detents for the wheel: the paces people actually pick
    readonly property var detents: [0, 0.25, 0.5, 0.75, 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10, 15, 20, 30]
    function nudge(dirSign) {      // +1 faster, -1 slower
        var i = 0, best = 1e9;
        for (var j = 0; j < detents.length; j++) { var dd = Math.abs(detents[j] - shownPace); if (dd < best) { best = dd; i = j } }
        i = Math.max(0, Math.min(detents.length - 1, i - dirSign));
        shownPace = detents[i]; pendingDir = dir; flush.restart();
    }

    // ---- the lamp bloom when a step lands
    onRevisionChanged: if (live && !paused) bloom.restart()

    // a bakelite key off the Timeline drawer, small size: pressed it sinks 2 px
    component DeckKey: Item {
        id: dk
        property string label: ""
        property string tipText: ""
        property bool danger: false
        property bool lit: false
        signal clicked()
        width: 44; height: 36
        opacity: enabled ? 1 : 0.45
        Behavior on opacity { NumberAnimation { duration: 180 } }
        readonly property bool down: dkm.pressed && dkm.containsMouse
        Image {
            x: dk.down ? 2 : 0; y: dk.down ? 2 : 0
            Behavior on x { NumberAnimation { duration: 50 } }
            Behavior on y { NumberAnimation { duration: 50 } }
            source: deck.art + (dk.down ? "key_s_down.png" : "key_s.png"); smooth: false
            Text {
                x: 0; y: 0; width: 42; height: 34
                horizontalAlignment: Text.AlignHCenter; verticalAlignment: Text.AlignVCenter
                text: dk.label; font.family: deck.mono; font.pixelSize: 15; font.bold: true
                color: dk.danger ? (dk.enabled ? "#ff8a76" : "#a2a7ac") : dk.lit ? deck.amber : "#d3dae2"
                Behavior on color { ColorAnimation { duration: 160 } }
            }
        }
        MouseArea {
            id: dkm; anchors.fill: parent; hoverEnabled: true
            cursorShape: dk.enabled ? Qt.PointingHandCursor : Qt.ArrowCursor
            onClicked: dk.clicked()
            onContainsMouseChanged: deck.tip(containsMouse ? dk.tipText : "")
        }
    }

    Item {
        id: body
        width: deck.baseW; height: deck.baseH
        // the whole object rises out of the pane's bottom edge
        transform: [
            Translate { y: (1 - deck.out) * (deck.baseH + 12) },
            Scale { origin.x: 0; origin.y: 0; xScale: deck.fit; yScale: deck.fit }
        ]
        Image { source: deck.art + "deck_plate.png"; smooth: false }

        // -- top row: lamp + state, SLOW .. FAST over the fader
        Image {
            id: glow
            x: 13 - 12; y: 9 - 12
            source: deck.art + "deck_glow.png"; smooth: false
            opacity: 0
            SequentialAnimation {
                id: bloom
                NumberAnimation { target: glow; property: "opacity"; from: 0; to: 0.95; duration: 90; easing.type: Easing.OutQuad }
                NumberAnimation { target: glow; property: "opacity"; to: 0; duration: 260; easing.type: Easing.InQuad }
            }
        }
        Image {
            id: lamp
            objectName: "deckLamp"
            x: 13; y: 9
            source: deck.art + ((deck.live && (!deck.paused || deck.blinkOn)) ? "lamp_on.png" : "lamp_off.png"); smooth: false
        }
        // the state on a strip of black Dymo tape, like every label in the chest
        Image { x: 30; y: 4; width: 118; height: 22; source: deck.art + "dymo.png"; fillMode: Image.Tile; smooth: false }
        Text {
            objectName: "deckState"
            x: 36; y: 5; width: 108; height: 20
            text: (deck.n > 0 ? "P" + deck.n + " " : "") + (!deck.live ? "DONE" : deck.paused ? "HELD" : deck.openRun ? "OPEN" : "LIVE") + (deck.run && deck.run.latestRevision ? " " + deck.run.latestRevision : "")
            color: deck.live ? deck.amber : "#8f99a6"
            Behavior on color { ColorAnimation { duration: 200 } }
            font.family: deck.mono; font.pixelSize: 14; font.bold: true
            elide: Text.ElideRight; verticalAlignment: Text.AlignVCenter
        }
        Text { x: slot.x + 4; y: 4; height: 20; text: "SLOW"; color: "#2c2f34"; font.family: deck.mono; font.pixelSize: 14; font.bold: true; verticalAlignment: Text.AlignVCenter; style: Text.Raised; styleColor: "#f0f2f4" }
        Text { x: slot.x + slot.width - width - 4; y: 4; height: 20; text: "FAST"; color: "#2c2f34"; font.family: deck.mono; font.pixelSize: 14; font.bold: true; verticalAlignment: Text.AlignVCenter; style: Text.Raised; styleColor: "#f0f2f4" }

        // -- bottom row: three bakelite keys (DeckKey, below)
        DeckKey {
            objectName: "deckPause"
            x: 10; y: 31
            enabled: deck.live
            lit: deck.paused
            label: deck.paused ? "\u25B6" : "II"
            tipText: deck.paused ? "RESUME: let the build go on at its pace" : "PAUSE: hold the build after the step it is on"
            onClicked: if (deck.controller && deck.dir) { if (deck.paused) deck.controller.resumeRun(deck.dir); else deck.controller.pauseRun(deck.dir) }
        }
        DeckKey {
            objectName: "deckStep"
            x: 58; y: 31
            enabled: deck.paused
            label: "\u25B6|"
            tipText: deck.paused ? "STEP: make one more change, then hold again" : "STEP works while the build is held: pause it first"
            onClicked: if (deck.controller && deck.dir) deck.controller.stepRun(deck.dir)
        }
        DeckKey {
            objectName: "deckStop"
            x: 106; y: 31
            enabled: deck.live
            danger: true
            label: "\u25A0"
            tipText: deck.openRun ? "CLOSE: the model is done; end the run and keep it" : "STOP: end this pane's build and keep the model (stops the agent too)"
            onClicked: if (deck.controller && deck.dir) deck.controller.stopResearch(deck.dir)
        }

        // -- the fader
        Image {
            id: slot
            objectName: "deckSlot"
            x: 160; y: 41
            source: deck.art + "deck_slot.png"; smooth: false
            opacity: deck.live ? 1 : 0.55
            Behavior on opacity { NumberAnimation { duration: 200 } }
        }
        Item {
            id: cap
            objectName: "deckCap"
            readonly property real travel: slot.width - 16
            property real pos: deck.posOf(deck.shownPace)
            // the cap glides when the pace changes from elsewhere; under the hand it is 1:1
            property real shownPos: pos
            Behavior on shownPos { enabled: !capMouse.pressed && deck.out >= 0.999; SpringAnimation { spring: 6; damping: 0.38; epsilon: 0.002 } }
            x: slot.x + Math.round(shownPos * travel / 2) * 2
            y: slot.y - 6
            width: 16; height: 28
            Image {
                y: capMouse.pressed ? 1 : 0
                source: deck.art + (capMouse.pressed ? "deck_cap_down.png" : "deck_cap.png"); smooth: false
            }
        }
        MouseArea {
            id: capMouse
            objectName: "deckFader"
            x: slot.x - 6; y: slot.y - 10; width: slot.width + 12; height: 36
            enabled: deck.live
            hoverEnabled: true
            preventStealing: true
            cursorShape: enabled ? Qt.SizeHorCursor : Qt.ArrowCursor
            function at(mx) { return Math.max(0, Math.min(1, (mx - 6 - 8) / cap.travel)) }
            onPressed: function (m) { deck.setFromPos(at(m.x)) }
            onPositionChanged: function (m) { if (pressed) deck.setFromPos(at(m.x)) }
            onReleased: if (deck.pendingDir) deck.submit()
            onContainsMouseChanged: deck.tip(containsMouse ? "PACE: seconds between model changes for this pane's build, now " + deck.paceText.toLowerCase() + ". Left slow, right fast; it is also your pace for new builds" : "")
            onWheel: function (w) {
                if (w.modifiers & Qt.ControlModifier) { deck.zoomStep(w.angleDelta.y > 0 ? 1 : -1); return }
                deck.nudge(w.angleDelta.y > 0 ? 1 : -1)
            }
        }
        // -- the readout window
        Image {
            id: lcd
            x: 292; y: 37
            source: deck.art + "deck_lcd.png"; smooth: false
            Text {
                objectName: "deckPace"
                x: 4; y: 4; width: 52; height: 16
                horizontalAlignment: Text.AlignHCenter; verticalAlignment: Text.AlignVCenter
                text: deck.paceText
                font.family: deck.mono; font.pixelSize: 14; font.bold: true
                fontSizeMode: Text.HorizontalFit; minimumPixelSize: 12
                color: deck.live ? deck.amber : "#7a5a1c"
                Behavior on color { ColorAnimation { duration: 200 } }
            }
        }
        // Ctrl+wheel anywhere on the plate zooms it; a plain wheel is left to the pane (dolly)
        WheelHandler {
            acceptedModifiers: Qt.ControlModifier
            onWheel: function (e) { deck.zoomStep(e.angleDelta.y > 0 ? 1 : -1) }
        }
    }
}
