// New A "Drafting table". No chest. The bench is a pale birch board; the viewport is a sheet of
// grey drafting paper taped down at the corners with masking tape. The status is written on a strip
// of tape under the sheet. Research and chat are index cards lying on the board, the run pile is a
// card box with tabbed cards standing in it, the tool wheel is a brass protractor dial.
import QtQuick
Item {
    id: root
    readonly property string sh: "../_shared/"
    readonly property string mono: "DejaVu Sans Mono"
    readonly property color ink: "#2b2a26"
    readonly property color pencil: "#5a5650"
    readonly property color red: "#b8321e"
    readonly property color card: "#fbf8ef"
    readonly property color rule: "#c7d3e0"
    width: 1700; height: 1010
    // the board: birch ply, faint grain
    Rectangle { anchors.fill: parent; gradient: Gradient { orientation: Gradient.Horizontal; GradientStop { position: 0; color: "#d9c39a" } GradientStop { position: 0.5; color: "#e3cea7" } GradientStop { position: 1; color: "#d4bc92" } } }
    Repeater { model: 60; Rectangle { x: 0; y: index * 15.3 + (index % 3) * 2; width: root.width; height: 1; color: "#b8a078"; opacity: 0.18 + (index % 4) * 0.06 } }
    // the sheet: drafting paper, a shadow, grey field, 1024x576 of picture inside a narrow white border
    Item { id: sheet; x: 300; y: 70; width: 1024 + 28; height: 576 + 28
        Rectangle { x: 6; y: 8; width: parent.width; height: parent.height; color: "#000"; opacity: 0.18 }
        Rectangle { anchors.fill: parent; color: "#f3f1ea" }
        Item { id: view; x: 14; y: 14; width: 1024; height: 576; clip: true
            Rectangle { anchors.fill: parent; color: "#dcdcd6" }
            Image { x: 0; y: 0; width: 511; height: 287; source: root.sh + "view_b.png"; fillMode: Image.PreserveAspectCrop; smooth: false }
            Repeater { model: 3; Rectangle { x: index === 0 ? 0 : 512; y: index === 0 ? 288 : index === 1 ? 0 : 288; width: 512; height: 288; color: "#e6e6e0" } }
            // the hairline seams drawn as pencil lines
            Rectangle { x: 511; width: 2; height: parent.height; color: root.pencil; opacity: 0.6 }
            Rectangle { y: 287; height: 2; width: parent.width; color: root.pencil; opacity: 0.6 }
            Rectangle { x: 0; y: 0; width: 511; height: 287; color: "transparent"; border.color: root.red; border.width: 2 }
            Rectangle { x: 4; y: 4; width: p1t.implicitWidth + 10; height: 16; color: "#f3f1ea"; Text { id: p1t; x: 5; anchors.verticalCenter: parent.verticalCenter; text: "1  piranha/head.txt"; color: root.red; font.family: root.mono; font.bold: true; font.pixelSize: 10 } }
            Repeater { model: ["2", "3", "4"]; Text { x: index === 1 ? 6 : 518; y: index === 0 ? 4 : 292; text: modelData; color: root.pencil; font.family: root.mono; font.pixelSize: 10 } }
        }
        // masking tape at the corners
        Repeater { model: 4; Rectangle { width: 70; height: 22; color: "#efe3b6"; opacity: 0.92; rotation: index % 2 ? 42 : -42
            x: index % 2 ? sheet.width - 50 : -20; y: index < 2 ? -4 : sheet.height - 18 } }
    }
    // the status: a tape strip under the sheet, pencil handwriting
    Rectangle { x: sheet.x + 10; y: sheet.y + sheet.height + 14; width: 560; height: 26; color: "#efe3b6"; opacity: 0.95; rotation: -0.4
        Rectangle { x: 10; y: 8; width: 10; height: 10; radius: 5; color: root.red }
        Text { x: 28; anchors.verticalCenter: parent.verticalCenter; text: "open · waiting for more lines      head.txt  rev 15  pane 1"; color: root.ink; font.family: root.mono; font.pixelSize: 12 } }
    // the brass dial: the tool wheel as a half-round protractor lying on the board, right of the chat card
    Item { id: dial; x: 1010; y: 770; width: 220; height: 130
        Item { x: 0; y: 0; width: 220; height: 112; clip: true
            Rectangle { x: 8; y: 10; width: 210; height: 210; radius: 105; color: "#000"; opacity: 0.15 }
            Rectangle { x: 5; y: 5; width: 210; height: 210; radius: 105; gradient: Gradient { GradientStop { position: 0; color: "#e8c677" } GradientStop { position: 0.5; color: "#b88a36" } } border.color: "#6e4e14"; border.width: 2 }
            Rectangle { x: 70; y: 70; width: 80; height: 80; radius: 40; color: "#7a5418"; opacity: 0.35 }
        }
        Rectangle { x: 5; y: 108; width: 210; height: 6; color: "#6e4e14" }
        Canvas { anchors.fill: parent; onPaint: { const c = getContext("2d"); const cx = 110, cy = 110;
            c.strokeStyle = "#4a3410"; c.lineWidth = 1.5
            for (let i = 0; i <= 20; i++) { const a = Math.PI + i * Math.PI / 20; c.beginPath(); c.moveTo(cx + Math.cos(a) * 100, cy + Math.sin(a) * 100); c.lineTo(cx + Math.cos(a) * (i % 2 ? 92 : 84), cy + Math.sin(a) * (i % 2 ? 92 : 84)); c.stroke() }
            c.strokeStyle = "#b8321e"; c.lineWidth = 3; c.beginPath(); c.moveTo(cx, cy); c.lineTo(cx + Math.cos(Math.PI * 1.15) * 88, cy + Math.sin(Math.PI * 1.15) * 88); c.stroke()
            c.fillStyle = "#b8321e"; c.beginPath(); c.arc(cx, cy, 6, 0, Math.PI * 2); c.fill() } }
        Repeater { model: ["FORM", "BUILD", "REFINE", "DEFORM", "CLOTH", "RIG"]
            Text { readonly property real a: Math.PI + (index + 0.5) * Math.PI / 6
                   x: 110 + Math.cos(a) * 66 - width / 2; y: 110 + Math.sin(a) * 66 - 6; text: modelData; color: index === 0 ? "#fff6dc" : "#3a2808"; font.family: root.mono; font.bold: true; font.pixelSize: 9 } }
        Text { x: 0; y: 118; width: 220; horizontalAlignment: Text.AlignHCenter; text: "TOOLS · form ▸ blob"; color: root.ink; font.family: root.mono; font.bold: true; font.pixelSize: 10 }
    }
    // index card: RESEARCH, left of the sheet
    Item { x: 40; y: 90; width: 236; height: 400; rotation: -1.5
        Rectangle { x: 4; y: 5; width: parent.width; height: parent.height; color: "#000"; opacity: 0.15 }
        Rectangle { anchors.fill: parent; color: root.card }
        Rectangle { x: 0; y: 34; width: parent.width; height: 2; color: root.red }
        Repeater { model: 14; Rectangle { x: 0; y: 60 + index * 24; width: parent.width; height: 1; color: root.rule } }
        Text { x: 12; y: 10; text: "RESEARCH · red-bellied piranha"; color: root.red; font.family: root.mono; font.bold: true; font.pixelSize: 11 }
        Rectangle { x: 12; y: 44; width: 212; height: 120; color: "#ccc"; Image { anchors.fill: parent; source: root.sh + "view_a.png"; fillMode: Image.PreserveAspectCrop; smooth: false } Text { x: 4; y: parent.height - 16; text: "side, 280 mm"; color: "#fff"; font.family: root.mono; font.pixelSize: 9; style: Text.Outline; styleColor: "#000" } }
        Column { x: 12; y: 180; spacing: 10
            Repeater { model: ["length 280, depth 120", "grey #6d7a86 / belly #c8503c", "build along X, refimage side", "body → belly → jaw → fins"]
                Text { width: 212; text: modelData; color: root.ink; font.family: root.mono; font.pixelSize: 10; elide: Text.ElideRight } } }
        Rectangle { x: 12; y: 310; width: 212; height: 24; color: "transparent"; border.color: root.pencil; border.width: 1
            Text { x: 6; anchors.verticalCenter: parent.verticalCenter; text: "describe an asset…"; color: root.pencil; font.family: root.mono; font.pixelSize: 10 } }
        Row { x: 12; y: 346; spacing: 8
            Repeater { model: ["research", "build"]; Rectangle { width: 70; height: 24; radius: 12; color: index ? root.red : "transparent"; border.color: root.red; border.width: 1
                Text { anchors.centerIn: parent; text: modelData; color: index ? "#fff" : root.red; font.family: root.mono; font.pixelSize: 10 } } } }
    }
    // index card: CLAUDE, under the sheet on the left, landscape
    Item { x: 330; y: 740; width: 640; height: 230; rotation: 0.6
        Rectangle { x: 4; y: 5; width: parent.width; height: parent.height; color: "#000"; opacity: 0.15 }
        Rectangle { anchors.fill: parent; color: root.card }
        Rectangle { x: 0; y: 34; width: parent.width; height: 2; color: root.red }
        Repeater { model: 7; Rectangle { x: 0; y: 60 + index * 24; width: parent.width; height: 1; color: root.rule } }
        Text { x: 12; y: 10; text: "CLAUDE"; color: root.red; font.family: root.mono; font.bold: true; font.pixelSize: 11 }
        Text { x: parent.width - 100; y: 10; text: "● working"; color: root.pencil; font.family: root.mono; font.pixelSize: 10 }
        Column { x: 12; y: 44; width: 616; spacing: 7
            Text { width: parent.width; text: "you      make the head blunter, keep the jaw"; color: root.pencil; font.family: root.mono; font.pixelSize: 11 }
            Text { width: parent.width; wrapMode: Text.Wrap; text: "claude   Head: pulling the nose profile back 20% so the skull reads as a slab, not a cone. Sending 3 lines."; color: root.ink; font.family: root.mono; font.pixelSize: 11 }
            Text { width: parent.width; text: "         ▸ geppetto-live more last head.txt"; color: root.red; font.family: root.mono; font.pixelSize: 10 }
            Text { width: parent.width; text: "claude   Done, rev 15 in pane 1. Jaw untouched."; color: root.ink; font.family: root.mono; font.pixelSize: 11 } }
        Rectangle { x: 12; y: 192; width: 616; height: 26; color: "transparent"; border.color: root.pencil; border.width: 1
            Text { x: 6; anchors.verticalCenter: parent.verticalCenter; text: "say what to change…"; color: root.pencil; font.family: root.mono; font.pixelSize: 11 } }
    }
    // the card box: the run pile as tabbed cards standing in a wooden box, right of the sheet
    Item { x: 1380; y: 120; width: 280; height: 360
        Rectangle { anchors.fill: parent; color: "#8a6a3c"; border.color: "#5a4020"; border.width: 3 }
        Rectangle { x: 10; y: 10; width: parent.width - 20; height: parent.height - 20; color: "#4a3418" }
        Column { x: 18; y: 18; spacing: 2
            Repeater { model: [["PIRANHA", "head.txt · rev 15 · live", true], ["ROLODEX", "spin.riganim · done", false], ["CARD DRAWER", "build.txt · done", false], ["BOOTS", "build.txt · 3 steps held", false], ["SIR GAWAIN", "roundtable sheet", false]]
                Item { width: 244; height: 58
                    Rectangle { x: index * 36; y: 0; width: 80; height: 16; color: modelData[2] ? root.red : "#efe3b6"; border.color: "#b8a078" ; border.width: 1
                        Text { anchors.centerIn: parent; text: modelData[0]; color: modelData[2] ? "#fff" : root.ink; font.family: root.mono; font.bold: true; font.pixelSize: 8 } }
                    Rectangle { x: 0; y: 15; width: 244; height: 44; color: root.card; border.color: "#d9c39a"; border.width: 1
                        Text { x: 8; y: 6; text: modelData[0]; color: modelData[2] ? root.red : root.ink; font.family: root.mono; font.bold: true; font.pixelSize: 11 }
                        Text { x: 8; y: 24; text: modelData[1]; color: root.pencil; font.family: root.mono; font.pixelSize: 9 } } } } }
        Text { x: 0; y: parent.height + 8; text: "RUNS"; color: root.pencil; font.family: root.mono; font.bold: true; font.pixelSize: 10 }
    }
    Text { x: 1010; y: root.height - 26; text: "A · drafting table: paper sheet taped to a birch board, index cards, tape status, card-box runs, brass dial"; color: root.pencil; font.family: root.mono; font.pixelSize: 9 }
}
