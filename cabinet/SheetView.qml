// A research run's model sheet on its card: the sizes table (M measured, P read off a photo,
// E estimated), a colour swatch per part, the reference photos with their view and licence, and the
// source links. While the researcher is still working it shows the photos saved so far.
// Data comes from host.py scanRuns -> model_sheet.card(): run.sheet, run.refs, run.links, run.sheetMd.
import QtQuick

Column {
    id: sv
    property var run: ({})
    readonly property var sheet: run.sheet || null
    readonly property var refs: run.refs || []
    readonly property var links: run.links || []
    readonly property color ink: "#1d2318"
    readonly property color inkFaint: "#424a3c"
    readonly property color inkRed: "#b3261c"
    readonly property color edge: "#5d6655"
    readonly property color linkInk: "#1f4e8c"
    readonly property string mono: "DejaVu Sans Mono"
    spacing: 4
    visible: sheet !== null || refs.length > 0

    component Cell: Text {
        color: sv.ink; font.family: sv.mono; font.pixelSize: 13; font.bold: true
        elide: Text.ElideRight; renderType: Text.QtRendering
    }

    Item { width: 1; height: 4 }
    Cell { text: sv.sheet ? "MODEL SHEET  " + sv.sheet.object : "PHOTOS SO FAR"; width: sv.width; color: sv.sheet ? sv.ink : sv.inkRed; font.pixelSize: 14 }

    // ---- sizes ----
    Column {
        visible: sv.sheet !== null
        width: sv.width
        Repeater {
            model: sv.sheet ? sv.sheet.dims : []
            Row {
                required property var modelData
                width: sv.width
                Cell { width: sv.width * 0.34; text: modelData.part; color: modelData.part === "overall" ? sv.inkRed : sv.ink }
                Cell { width: sv.width * 0.22; text: modelData.dim; color: sv.inkFaint }
                Cell { width: sv.width * 0.36; text: modelData.size }
                Cell { width: sv.width * 0.08; text: modelData.basis; horizontalAlignment: Text.AlignRight
                       color: modelData.basis === "M" ? "#2c6a2a" : modelData.basis === "E" ? sv.inkRed : sv.inkFaint }
            }
        }
        Cell { text: "M measured   P from photo   E estimated"; width: sv.width; color: sv.inkFaint; font.pixelSize: 11 }
    }

    // ---- materials: a swatch per part, the colour sampled off the photo ----
    Column {
        visible: sv.sheet !== null
        width: sv.width; spacing: 3
        Item { width: 1; height: 2 }
        Repeater {
            model: sv.sheet ? sv.sheet.parts : []
            Row {
                required property var modelData
                spacing: 6
                Rectangle { width: 26; height: 26; color: modelData.hex; border.width: 2; border.color: sv.edge; radius: modelData.metal ? 13 : 2 }
                Column {
                    Cell { width: sv.width - 32; text: modelData.name + "  " + modelData.hex + (modelData.sampled ? "" : " (not sampled)") }
                    Cell { width: sv.width - 32; color: sv.inkFaint; font.pixelSize: 11
                           text: modelData.material + (modelData.metal ? ", metal" : "") + (modelData.roughness !== null && modelData.roughness !== undefined ? ", rough " + modelData.roughness : "")
                                 + (modelData.section ? ", " + modelData.section : "") }
                }
            }
        }
        Text { width: sv.width; wrapMode: Text.Wrap; color: sv.ink; font.family: sv.mono; font.pixelSize: 12; font.bold: true
               text: sv.sheet ? "build: " + sv.sheet.order : "" }
        Text { width: sv.width; wrapMode: Text.Wrap; color: sv.inkFaint; font.family: sv.mono; font.pixelSize: 12; font.bold: true
               text: sv.sheet ? "scale: " + sv.sheet.scale + (sv.sheet.gaps > 0 ? "   (" + sv.sheet.gaps + " gaps)" : "") : "" }
    }

    // ---- photos ----
    Flow {
        width: sv.width; spacing: 6
        Repeater {
            model: sv.refs
            Column {
                required property var modelData
                width: Math.max(80, thumb.paintedWidth + 4)
                Rectangle {
                    width: parent.width; height: 104; color: "#f4f7ee"; border.width: modelData.main ? 3 : 1; border.color: modelData.main ? sv.inkRed : sv.edge
                    Image {
                        id: thumb
                        anchors.centerIn: parent
                        height: 100; width: Math.min(sv.width - 8, 260)
                        source: "file://" + modelData.png
                        sourceSize.height: 200
                        fillMode: Image.PreserveAspectFit; asynchronous: true; cache: false
                    }
                    MouseArea { anchors.fill: parent; cursorShape: Qt.PointingHandCursor; onClicked: Qt.openUrlExternally("file://" + modelData.png) }
                }
                Cell { width: parent.width; text: (modelData.view || "photo") + (modelData.main ? " *" : ""); font.pixelSize: 11 }
                Cell { width: parent.width; text: modelData.licence; color: sv.inkFaint; font.pixelSize: 10; visible: text !== "" }
            }
        }
    }

    // ---- sources ----
    Column {
        visible: sv.links.length > 0
        width: sv.width
        Cell { text: "SOURCES " + sv.links.length; color: sv.inkFaint; font.pixelSize: 12 }
        Repeater {
            model: sv.links.slice(0, 12)
            Text {
                required property var modelData
                width: sv.width; text: modelData.replace("https://", ""); elide: Text.ElideMiddle
                color: sv.linkInk; font.family: sv.mono; font.pixelSize: 11; font.underline: true
                MouseArea { anchors.fill: parent; cursorShape: Qt.PointingHandCursor; onClicked: Qt.openUrlExternally(modelData) }
            }
        }
    }
    Text {
        visible: (sv.run.sheetMd || "") !== ""
        text: "open model-sheet.md"; color: sv.linkInk; font.family: sv.mono; font.pixelSize: 12; font.bold: true; font.underline: true
        MouseArea { anchors.fill: parent; cursorShape: Qt.PointingHandCursor; onClicked: Qt.openUrlExternally("file://" + sv.run.sheetMd) }
    }
}
