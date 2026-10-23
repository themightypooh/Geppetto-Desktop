// New B "Instrument": a dark oscilloscope/bench-instrument face. The viewport is the CRT screen,
// recessed in a black bezel with a fine phosphor-green graticule only in its corners. A single
// strip of segment-display readouts along the top is the status. Left and right are thin control
// columns: knob-and-label rows for research, a printer-tape roll for chat on the right, run pile as
// a rack of cartridge slots, tool wheel as a big detented rotary knob. Graphite, warm white, one
// signal orange accent. No red enamel, no cream paper.
import QtQuick
Item {
    id: root
    readonly property string sh: "../_shared/"
    readonly property string mono: "DejaVu Sans Mono"
    readonly property color body: "#23262a"
    readonly property color bodyHi: "#30343a"
    readonly property color face: "#1a1c1f"
    readonly property color label: "#d9d6cc"
    readonly property color dim: "#7d8086"
    readonly property color sig: "#ff7a1a"
    readonly property color green: "#58e08a"
    readonly property int vx: 290
    readonly property int vy: 96
    width: 290 + 1024 + 330; height: 96 + 576 + 120
    Rectangle { anchors.fill: parent; color: "#111214" }
    // the case: graphite, a bevel and four feet
    Rectangle { x: 10; y: 10; width: root.width - 20; height: root.height - 20; radius: 14; color: root.body; border.color: "#0a0b0c"; border.width: 2 }
    Rectangle { x: 14; y: 14; width: root.width - 28; height: 2; radius: 1; color: "#4a4f56" }
    // top strip: maker name, the segment readouts, power lamp
    Text { x: 34; y: 30; text: "GEPPETTO"; color: root.label; font.family: root.mono; font.bold: true; font.pixelSize: 16; font.letterSpacing: 4 }
    Text { x: 34; y: 52; text: "MODEL BENCH  MK II"; color: root.dim; font.family: root.mono; font.pixelSize: 9; font.letterSpacing: 2 }
    Row { x: root.vx; y: 28; spacing: 10
        Repeater { model: [["STATE", "OPEN · WAIT", 160], ["RUN", "HEAD.TXT", 130], ["REV", "15", 54], ["PANE", "1/2", 64], ["STEP", "NOSE PULLED BACK, SKULL READS AS A SLAB", 520]]
            Item { width: modelData[2]; height: 50
                Text { text: modelData[0]; color: root.dim; font.family: root.mono; font.pixelSize: 8; font.letterSpacing: 1 }
                Rectangle { y: 12; width: parent.width; height: 30; radius: 3; color: "#0c0d0e"; border.color: "#000"; border.width: 1
                    Text { x: 8; anchors.verticalCenter: parent.verticalCenter; width: parent.width - 16; elide: Text.ElideRight; text: modelData[1]; color: index === 0 ? root.sig : root.green; font.family: root.mono; font.bold: true; font.pixelSize: 13 } } } } }
    Rectangle { x: root.width - 52; y: 34; width: 14; height: 14; radius: 7; color: root.sig; border.color: "#5a2a08"; border.width: 2 }
    Text { x: root.width - 64; y: 54; text: "LIVE"; color: root.dim; font.family: root.mono; font.pixelSize: 8 }
    // the screen: deep bezel, soft rounded corners, 1024x576 inside
    Rectangle { x: root.vx - 16; y: root.vy - 16; width: 1024 + 32; height: 576 + 32; radius: 18; color: "#0a0b0c"
        border.color: "#3a3e44"; border.width: 1 }
    Item { x: root.vx; y: root.vy; width: 1024; height: 576; clip: true
        Rectangle { anchors.fill: parent; color: "#14171a" }
        Image { x: 0; y: 0; width: 1024; height: 576; source: root.sh + "view_a.png"; fillMode: Image.PreserveAspectCrop; smooth: false; opacity: 0.0 }
        Image { x: 0; y: 0; width: 511; height: 576; source: root.sh + "view_b.png"; fillMode: Image.PreserveAspectCrop; smooth: false }
        Rectangle { x: 511; width: 2; height: parent.height; color: "#0a0b0c" }
        // corner graticule ticks only, so the picture stays clear
        Repeater { model: 4; Item { x: index % 2 ? 1024 - 22 : 6; y: index < 2 ? 6 : 576 - 22; width: 16; height: 16
            Rectangle { x: index % 2 ? 15 : 0; width: 1; height: 16; color: root.green; opacity: 0.5 }
            Rectangle { y: index < 2 ? 0 : 15; width: 16; height: 1; color: root.green; opacity: 0.5 } } }
        Text { x: 14; y: 10; text: "CH1  piranha"; color: root.green; font.family: root.mono; font.bold: true; font.pixelSize: 10 }
        Text { x: 526; y: 10; text: "CH2  empty"; color: root.dim; font.family: root.mono; font.pixelSize: 10 }
        Rectangle { x: 0; y: 0; width: 511; height: 3; color: root.sig }
    }
    // the 1/2/4 pane switch under the screen: a three-position slider
    Row { x: root.vx; y: root.vy + 576 + 30; spacing: 14
        Text { text: "SPLIT"; color: root.dim; font.family: root.mono; font.pixelSize: 9; anchors.verticalCenter: parent.verticalCenter }
        Rectangle { width: 120; height: 22; radius: 11; color: "#0c0d0e"
            Repeater { model: ["1", "2", "4"]; Text { x: 14 + index * 40; anchors.verticalCenter: parent.verticalCenter; text: modelData; color: index === 1 ? root.face : root.dim; font.family: root.mono; font.bold: true; font.pixelSize: 11; z: 2 } }
            Rectangle { x: 42; y: 2; width: 36; height: 18; radius: 9; color: root.label } }
        Item { width: 30; height: 1 }
        Repeater { model: ["HOLD", "STOP", "REPLAY", "LINK CAMS"]
            Rectangle { width: 86; height: 26; radius: 4; color: index === 1 ? "#3a1c10" : root.bodyHi; border.color: "#0a0b0c"
                Text { anchors.centerIn: parent; text: modelData; color: index === 1 ? root.sig : root.label; font.family: root.mono; font.bold: true; font.pixelSize: 10 } } }
    }
    // left column: RESEARCH as a labelled control strip
    Column { x: 34; y: root.vy; width: 220; spacing: 12
        Text { text: "RESEARCH"; color: root.label; font.family: root.mono; font.bold: true; font.pixelSize: 11; font.letterSpacing: 2 }
        Rectangle { width: 220; height: 130; radius: 8; color: "#0a0b0c"
            Image { anchors.fill: parent; anchors.margins: 6; source: root.sh + "view_a.png"; fillMode: Image.PreserveAspectCrop; smooth: false }
            Text { x: 12; y: parent.height - 20; text: "REF · SIDE"; color: root.green; font.family: root.mono; font.bold: true; font.pixelSize: 9 } }
        Repeater { model: [["LENGTH", "280 mm"], ["DEPTH", "120 mm"], ["BODY", "#6d7a86"], ["BELLY", "#c8503c"], ["AXIS", "X · side"]]
            Item { width: 220; height: 18
                Text { text: modelData[0]; color: root.dim; font.family: root.mono; font.pixelSize: 9 }
                Rectangle { x: 70; y: 8; width: 60; height: 1; color: "#3a3e44" }
                Text { anchors.right: parent.right; text: modelData[1]; color: root.label; font.family: root.mono; font.pixelSize: 10 } } }
        Rectangle { width: 220; height: 28; radius: 4; color: "#0c0d0e"; border.color: "#3a3e44"
            Text { x: 8; anchors.verticalCenter: parent.verticalCenter; text: "describe an asset_"; color: root.dim; font.family: root.mono; font.pixelSize: 10 } }
        Row { spacing: 8
            Repeater { model: ["SEARCH", "BUILD"]; Rectangle { width: 106; height: 26; radius: 4; color: index ? root.sig : root.bodyHi
                Text { anchors.centerIn: parent; text: modelData; color: index ? "#1a0a00" : root.label; font.family: root.mono; font.bold: true; font.pixelSize: 10 } } } }
    }
    // right column: CLAUDE on a printer-tape roll, the run rack, the knob
    Item { x: root.vx + 1024 + 40; y: root.vy; width: 250; height: 576
        Text { text: "CLAUDE"; color: root.label; font.family: root.mono; font.bold: true; font.pixelSize: 11; font.letterSpacing: 2 }
        Rectangle { x: 200; y: 2; width: 8; height: 8; radius: 4; color: root.green }
        Rectangle { y: 22; width: 250; height: 250; radius: 6; color: "#0a0b0c"
            Rectangle { x: 6; y: 0; width: 238; height: 244; color: "#ecebe4"
                Column { x: 8; y: 8; width: 222; spacing: 6
                    Repeater { model: [["> ", "make the head blunter, keep the jaw", "#6a6a64"], ["", "Head: pulling the nose profile back 20% so the skull reads as a slab, not a cone.", "#1c1c1a"], ["$ ", "geppetto-live more last head.txt", "#b2470f"], ["", "Done, rev 15 in pane 1. Jaw untouched.", "#1c1c1a"]]
                        Text { width: 222; wrapMode: Text.Wrap; text: modelData[0] + modelData[1]; color: modelData[2]; font.family: root.mono; font.pixelSize: 10 } } }
                Repeater { model: 12; Rectangle { x: index * 20; y: 236; width: 10; height: 8; color: "#0a0b0c"; rotation: 45 } }   // torn tape edge
            } }
        Rectangle { y: 280; width: 250; height: 26; radius: 4; color: "#0c0d0e"; border.color: "#3a3e44"
            Text { x: 8; anchors.verticalCenter: parent.verticalCenter; text: "say what to change_"; color: root.dim; font.family: root.mono; font.pixelSize: 10 } }
        // the run rack: cartridges standing in slots
        Text { y: 322; text: "RUNS"; color: root.label; font.family: root.mono; font.bold: true; font.pixelSize: 11; font.letterSpacing: 2 }
        Row { y: 342; spacing: 6
            Repeater { model: [["PIRANHA", true], ["ROLODEX", false], ["DRAWER", false], ["BOOTS", false], ["GAWAIN", false]]
                Rectangle { width: 44; height: 110; radius: 3; color: modelData[1] ? "#3a2a20" : root.bodyHi; border.color: "#0a0b0c"; border.width: 2
                    Rectangle { x: 6; y: 6; width: 32; height: 6; radius: 3; color: modelData[1] ? root.sig : "#4a4f56" }
                    Text { anchors.centerIn: parent; rotation: -90; text: modelData[0]; color: modelData[1] ? root.sig : root.label; font.family: root.mono; font.bold: true; font.pixelSize: 10 } } } }
        // the tool knob
        Item { x: 70; y: 466; width: 110; height: 110
            Canvas { anchors.fill: parent; onPaint: { const c = getContext("2d"); c.strokeStyle = "#7d8086"; c.lineWidth = 1.5
                for (let i = 0; i < 11; i++) { const a = -Math.PI * 1.25 + i * Math.PI * 1.5 / 10; c.beginPath(); c.moveTo(55 + Math.cos(a) * 54, 55 + Math.sin(a) * 54); c.lineTo(55 + Math.cos(a) * 46, 55 + Math.sin(a) * 46); c.stroke() } } }
            Rectangle { x: 13; y: 13; width: 84; height: 84; radius: 42; gradient: Gradient { GradientStop { position: 0; color: "#4a4f56" } GradientStop { position: 1; color: "#1c1e21" } } border.color: "#0a0b0c"; border.width: 2
                Rectangle { x: 39; y: 6; width: 6; height: 26; radius: 3; color: root.sig; rotation: -40; transformOrigin: Item.Bottom } }
            Text { x: -60; y: 44; text: "TOOL"; color: root.dim; font.family: root.mono; font.pixelSize: 9 }
            Text { x: 112; y: 38; text: "FORM\nblob"; color: root.label; font.family: root.mono; font.bold: true; font.pixelSize: 10 } }
    }
    Text { x: 34; y: root.height - 36; text: "B · instrument: graphite case, CRT screen, segment readouts as status, tape-roll chat, cartridge runs, rotary tool knob"; color: root.dim; font.family: root.mono; font.pixelSize: 9 }
}
