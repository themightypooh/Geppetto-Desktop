// The sketch carousel: the starting tools (the wheel's FORM group) as a cover-flow along the bottom of
// the viewport. The front tool stands square and lit; the others turn away on either side, shrink and
// fade into the dark. The pencil in the tools drawer brings it up (it slides in from below); roll the
// wheel, drag, or press the arrows to turn it; click the front tile to use that tool (its card opens),
// click another to bring it to the front; click off it, or press Escape, to put it away.
import QtQuick

Item {
    id: car
    property bool open: false
    signal picked(string key, string label)

    // [key, label, what it is for]: keys are Effigy feature types, as the wheel and BuildScript read them
    readonly property var tools: [
        ["sketch", "SKETCH", "draw a flat outline to build from"],
        ["profile", "PROFILE", "an outline that gets spun or pulled into a shape"],
        ["spline", "SPLINE", "a curve: a tube, a horn, a tail"],
        ["primitive", "PRIMITIVE", "a box, ball, tube or cone to start from"],
        ["blob", "BLOB", "soft shapes melted together"],
        ["plane", "PLANE", "a flat sheet"],
        ["part", "PART", "a ready-made part from the library"],
        ["import", "IMPORT", "load a model from a file"],
        ["bodyfrombones", "BODY FROM BONES", "a body grown round a skeleton"]]
    readonly property int count: tools.length
    property real at: Math.floor(tools.length / 2)   // which tile is at the front, fractional while it turns; starts centred
    readonly property int front: Math.max(0, Math.min(count - 1, Math.round(at)))
    property int hot: -1                              // the tile under the mouse

    readonly property real tile: 112
    readonly property real step: 70                   // spacing along the row: side tiles overlap, turned away
    readonly property real reach: 3.6                 // how many tiles out before one has faded to nothing
    readonly property color lit: "#f2a91c"
    readonly property string mono: "DejaVu Sans Mono"
    readonly property string art: Qt.resolvedUrl("art/tools/")

    // 0 = away, 1 = up: the whole row slides in from below and fades, the tiles a touch after the glow
    property real reveal: open ? 1 : 0
    Behavior on reveal { NumberAnimation { id: lift; duration: 420; easing.type: Easing.OutQuint } }
    visible: reveal > 0
    Behavior on at { NumberAnimation { duration: 380; easing.type: Easing.OutQuint } }
    onOpenChanged: if (open) { hot = -1; keys.forceActiveFocus() }

    function turn(n) { at = Math.max(0, Math.min(count - 1, Math.round(at) + n)) }
    function use(i) {
        if (i !== front) { at = i; return }
        var t = tools[i]
        open = false
        picked(t[0], t[1])
    }

    // click off the tiles: away
    MouseArea {
        anchors.fill: parent
        enabled: car.open
        onClicked: car.open = false
        onWheel: function (w) { car.turn(w.angleDelta.y < 0 ? 1 : -1); w.accepted = true }
    }
    Item {
        id: keys
        focus: car.open
        Keys.onEscapePressed: car.open = false
        Keys.onLeftPressed: car.turn(-1)
        Keys.onRightPressed: car.turn(1)
        Keys.onReturnPressed: car.use(car.front)
    }

    // a thin floor line the row stands on, bright under the front tile, gone at the ends
    Rectangle {
        x: car.width * 0.15; width: car.width * 0.7; height: 1
        y: row.y + car.tile * 0.5 + 10
        opacity: car.reveal * 0.5
        gradient: Gradient {
            orientation: Gradient.Horizontal
            GradientStop { position: 0.0; color: "#00ffffff" }
            GradientStop { position: 0.5; color: "#80ffffff" }
            GradientStop { position: 1.0; color: "#00ffffff" }
        }
    }

    Item {
        id: row
        x: car.width / 2
        y: car.height - car.tile * 0.5 - 78 + (1 - car.reveal) * 70   // slides up into place
        opacity: car.reveal
        Repeater {
            model: car.tools
            Item {
                id: slot
                readonly property real d: index - car.at                     // how far from the front, in tiles
                readonly property real a: Math.abs(d)
                readonly property bool isFront: index === car.front
                readonly property bool hovered: car.hot === index
                readonly property real fade: Math.max(0, 1 - a / car.reach)
                // the front tile stands square; the rest turn away, the first one most, then flatten out
                readonly property real turnAway: d === 0 ? 0 : (d < 0 ? 1 : -1) * Math.min(1, a) * 58
                // the front tile comes forward: a third bigger than its neighbours, which step down from there
                readonly property real s: (1 - Math.min(0.4, a * 0.12)) * (1 + 0.32 * Math.max(0, 1 - a))
                x: d * car.step + (d < 0 ? -1 : 1) * Math.min(a, 1) * car.tile * 0.35 - car.tile / 2
                // each tile slides in a beat after the one in front of it
                y: -car.tile / 2 - Math.max(0, 1 - a) * 8 + (1 - car.reveal) * a * 14
                z: 100 - Math.round(a * 10)
                width: car.tile; height: car.tile
                opacity: Math.pow(fade, 2.2)
                scale: s * (hovered && !isFront ? 1.06 : 1)
                Behavior on scale { NumberAnimation { duration: 200; easing.type: Easing.OutCubic } }
                transform: Rotation { origin.x: car.tile / 2; origin.y: car.tile / 2; axis.x: 0; axis.y: 1; axis.z: 0; angle: slot.turnAway }

                // the tile: dark glass, a hairline of light along the top, the front one warmed by the lamp
                Rectangle {
                    id: glass
                    anchors.fill: parent
                    radius: 16
                    color: slot.isFront ? "#2a2c32" : "#1a1c21"
                    border.color: slot.isFront ? "#66f2a91c" : "#2affffff"; border.width: 1
                    Rectangle {    // the sheen: light falling down the top half
                        x: 1; y: 1; width: parent.width - 2; height: parent.height * 0.5; radius: 15
                        gradient: Gradient {
                            GradientStop { position: 0.0; color: slot.isFront ? "#38ffffff" : "#1effffff" }
                            GradientStop { position: 1.0; color: "#00ffffff" }
                        }
                    }
                    Image {
                        source: car.art + modelData[0] + ".png"
                        width: 72; height: 72; smooth: true; mipmap: true
                        anchors.centerIn: parent
                    }
                    MouseArea {
                        anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor
                        onContainsMouseChanged: car.hot = containsMouse ? index : (car.hot === index ? -1 : car.hot)
                        onClicked: car.use(index)
                        onWheel: function (w) { car.turn(w.angleDelta.y < 0 ? 1 : -1); w.accepted = true }
                    }
                }
                // its reflection in the floor, fading out fast
                Image {
                    source: car.art + modelData[0] + ".png"
                    width: 72; height: 72; smooth: true; mipmap: true
                    x: (car.tile - 72) / 2; y: car.tile + 8
                    opacity: slot.isFront ? 0.22 : 0.1
                    transform: Scale { origin.y: 36; yScale: -1 }
                    Rectangle {
                        anchors.fill: parent
                        gradient: Gradient {
                            GradientStop { position: 0.0; color: "#ff000000" }
                            GradientStop { position: 0.45; color: "#c0000000" }
                            GradientStop { position: 1.0; color: "#00000000" }
                        }
                    }
                }
            }
        }
        // the front tool's name, and what it is for, under the row
        Column {
            x: -width / 2; y: car.tile / 2 + 18
            spacing: 3
            Text {
                anchors.horizontalCenter: parent.horizontalCenter
                text: car.tools[car.hot >= 0 ? car.hot : car.front][1]
                color: "#f4f7ee"; font.family: car.mono; font.pixelSize: 15; font.bold: true; font.letterSpacing: 2
            }
            Text {
                anchors.horizontalCenter: parent.horizontalCenter
                text: car.tools[car.hot >= 0 ? car.hot : car.front][2]
                color: "#9a9ea6"; font.family: car.mono; font.pixelSize: 11
            }
        }
        // drag anywhere along the row to turn it
        MouseArea {
            x: -car.width / 2; y: -car.tile; width: car.width; height: car.tile * 2
            z: -1
            property real downX: 0; property real downAt: 0
            onPressed: function (m) { downX = m.x; downAt = car.at }
            onPositionChanged: function (m) { if (pressed) car.at = Math.max(0, Math.min(car.count - 1, downAt - (m.x - downX) / car.step)) }
            onReleased: car.at = Math.round(car.at)
            onClicked: car.open = false
        }
    }
}
